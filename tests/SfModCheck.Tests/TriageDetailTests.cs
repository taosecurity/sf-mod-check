// triage 0.17.0: the full difference of each changed field ("Changes to official records, field by field"), on made-up
// games built here with BuildKit. Each test fails on 0.16.x: that version had no field-by-field table, cut Before and
// After at 160 characters, and didn't list a text whose only difference is dropped translations at all.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class TriageDetailTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>A made-up Starfield.esm (localized or not) and a mod folder; build adds the base game's records.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, ModDir;
        public Game(Action<StarfieldMod> build, bool localized = false)
        {
            Data = Path.Combine(Sub("Game"), "Data");
            Directory.CreateDirectory(Data);
            ModDir = Sub("Mod");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game", localized);
            build(s);
            using var g = new GameData(Data);
            PluginWriter.Write(s, Data, g, NoGap);
        }

        public void Mod(Action<StarfieldMod, IStarfieldModGetter> build)
        {
            var mod = Plugin.New("TdMod.esm", PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data, [ModDir]);
            build(mod, g.Open("Starfield.esm"));
            PluginWriter.Write(mod, ModDir, g, NoGap);
        }

        public TriageContext Run()
        {
            var (root, files) = TriageCommand.Gather(TriageInputKind.Folder, ModDir, "unused");
            var c = new TriageContext
            {
                Options = new TriageOptions { DataDir = Data }, Kind = TriageInputKind.Folder, InputPath = ModDir, Root = root, Files = files,
                DataPrefix = "", DataDir = Data, Work = Sub("work"),
                Report = new TriageReport { InputName = "Mod", HasGame = true, DataDir = Data },
            };
            VanillaIndex.ForgetLoaded();
            c.Report.Findings.AddRange(TriagePlugins.Run(c));
            return c;
        }
    }

    static TriageTable Table(TriageContext c, string title) => c.Report.Tables.Single(t => t.Title == title);
    static List<List<string>> Detail(TriageContext c) => Table(c, "Changes to official records, field by field").Rows;
    static string Notes(TriageContext c) => string.Join("\n", c.Report.Findings.Select(f => $"{f.Level} {f.File}: {f.Text}"));

    [Fact]
    public void A_reordered_alias_faction_list_is_one_line_named_same_entries_other_order()
    {
        using var game = new Game(s =>
        {
            var f1 = s.Factions.AddNew("TD_F1"); var f2 = s.Factions.AddNew("TD_F2"); var f3 = s.Factions.AddNew("TD_F3");
            var alias = new QuestReferenceAlias
            {
                ID = 7, Name = "TdAlias",
                Factions = new ExtendedList<IFormLinkGetter<IFactionGetter>> { f1.ToLink<IFactionGetter>(), f2.ToLink<IFactionGetter>(), f3.ToLink<IFactionGetter>() },
            };
            s.Quests.Add(new Quest(s) { EditorID = "TD_Quest", Aliases = new ExtendedList<AQuestAlias> { alias } });
        });
        game.Mod((m, s) =>
        {
            var q = m.Quests.GetOrAddAsOverride(s.Quests.First());
            var a = (QuestReferenceAlias)q.Aliases[0];
            var moved = a.Factions![2];
            a.Factions.RemoveAt(2);
            a.Factions.Insert(0, moved);
        });
        var c = game.Run();

        var rows = Detail(c);
        var row = Assert.Single(rows);
        Assert.Equal("Aliases", row[2]);
        Assert.Contains(".Factions", row[3]);
        Assert.Equal(TriageDetail.Reordered, row[4]);
        Assert.Contains("first different at entry 1 of 3", row[6]);
        Assert.Contains("1 is a list with the same entries in another order", Notes(c));
    }

    [Fact]
    public void A_non_localized_override_of_a_localized_master_names_the_dropped_translations()
    {
        using var game = new Game(s =>
        {
            var misc = s.MiscItems.AddNew("TD_Misc");
            misc.Name = Localized.Text("Lucky Rock");
            misc.Weight = 1;
        }, localized: true);
        game.Mod((m, s) => m.MiscItems.GetOrAddAsOverride(s.MiscItems.First()).Weight = 2);
        var c = game.Run();

        // the weight is a real change; the name's English text is the same, but the mod stores it as one string
        var rows = Detail(c);
        Assert.Contains(rows, r => r[2] == "Weight" && r[4] == TriageDetail.Changed && r[5] == "1" && r[6] == "2");
        var name = Assert.Single(rows, r => r[2] == "Name");
        Assert.Equal(TriageDetail.TranslationsDropped, name[4]);
        Assert.Equal("\"Lucky Rock\"", name[5]);
        Assert.Equal("\"Lucky Rock\"", name[6]);
        // the summary table lists the name too (0.16.x didn't: its comparison reads only the English text)
        Assert.Contains(Table(c, "Changes to official records").Rows, r => r[3] == "Name");
        Assert.Contains("1 is a text with the same English text but without the translations", Notes(c));
    }

    [Fact]
    public void A_long_value_is_shown_in_full_in_the_field_by_field_table()
    {
        var longName = string.Concat(Enumerable.Range(0, 30).Select(i => $"word{i:D2}abc "));   // 300 characters
        Assert.Equal(300, longName.Length);
        using var game = new Game(s => s.MiscItems.AddNew("TD_Misc").Name = "Short name");
        game.Mod((m, s) => m.MiscItems.GetOrAddAsOverride(s.MiscItems.First()).Name = longName);
        var c = game.Run();

        // the summary table still cuts at 160 characters; the detail table holds the whole value
        Assert.Contains(Table(c, "Changes to official records").Rows, r => r[3] == "Name" && r[5].Length < 200);
        var row = Assert.Single(Detail(c), r => r[2] == "Name");
        Assert.Equal(TriageDetail.Changed, row[4]);
        Assert.Equal($"\"{longName}\"", row[6]);
    }

    [Fact]
    public void Padding_in_leveled_list_entries_gives_no_rows_and_padding_alone_one_row_that_says_so()
    {
        // bug hunt 0.17.0: a real leveled-list mod gave an "Entries[...].Unused" row for every entry it kept
        using var game = new Game(s =>
        {
            var m1 = s.MiscItems.AddNew("TD_M1"); var m2 = s.MiscItems.AddNew("TD_M2"); var m3 = s.MiscItems.AddNew("TD_M3");
            foreach (var id in new[] { "TD_LL_Add", "TD_LL_Pad" })
            {
                var ll = s.LeveledBaseForms.AddNew(id);
                ll.Entries = new ExtendedList<LeveledBaseFormEntry>
                {
                    new() { Level = 1, Count = 1, Reference = m1.ToLink<ILeveledBaseFormTargetGetter>() },
                    new() { Level = 1, Count = 1, Reference = m2.ToLink<ILeveledBaseFormTargetGetter>() },
                };
            }
        });
        game.Mod((m, s) =>
        {
            foreach (var base_ in s.LeveledBaseForms)
            {
                var ll = m.LeveledBaseForms.GetOrAddAsOverride(base_);
                foreach (var e in ll.Entries!) e.Unused = 7;
                if (ll.EditorID == "TD_LL_Add")
                    ll.Entries.Add(new() { Level = 1, Count = 1, Reference = s.MiscItems.Single(x => x.EditorID == "TD_M3").ToLink<ILeveledBaseFormTargetGetter>() });
            }
        });
        var c = game.Run();

        var rows = Detail(c);
        Assert.DoesNotContain(rows, r => r[3].Contains("Unused"));
        var added = Assert.Single(rows, r => r[1].Contains("TD_LL_Add"));
        Assert.Equal(TriageDetail.Added, added[4]);
        var pad = Assert.Single(rows, r => r[1].Contains("TD_LL_Pad"));
        Assert.Equal("Entries", pad[3]);
        Assert.EndsWith(TriageDetail.PaddingOnly, pad[6]);
    }

    [Fact]
    public void Translation_rows_never_crowd_out_a_real_change_and_the_note_counts_them_apart()
    {
        // code review 0.17.0: a quest's 250 stage texts (dropped translations) filled the record's 200 rows, so the real
        // change to an alias that follows them had no row, and the NOTE counted the texts as real changes
        using var game = new Game(s =>
        {
            var f1 = s.Factions.AddNew("TD_F1"); var f2 = s.Factions.AddNew("TD_F2");
            var alias = new QuestReferenceAlias
            {
                ID = 7, Name = "TdAlias",
                Factions = new ExtendedList<IFormLinkGetter<IFactionGetter>> { f1.ToLink<IFactionGetter>(), f2.ToLink<IFactionGetter>() },
            };
            var stages = new ExtendedList<QuestStage>();
            for (int i = 0; i < 250; i++)
                stages.Add(new QuestStage { Index = (ushort)(i * 10), LogEntries = new ExtendedList<QuestLogEntry> { new() { Entry = Localized.Text($"Log {i}") } } });
            s.Quests.Add(new Quest(s) { EditorID = "TD_Quest", Name = Localized.Text("Td Quest"), Stages = stages, Aliases = new ExtendedList<AQuestAlias> { alias } });
        }, localized: true);
        game.Mod((m, s) =>
        {
            var a = (QuestReferenceAlias)m.Quests.GetOrAddAsOverride(s.Quests.First()).Aliases[0];
            var moved = a.Factions![1];
            a.Factions.RemoveAt(1);
            a.Factions.Insert(0, moved);
        });
        var c = game.Run();

        var rows = Detail(c);
        Assert.Contains(rows, r => r[2] == "Aliases" && r[4] == TriageDetail.Reordered);
        Assert.Equal(TriageDetail.MaxPerRecord, rows.Count(r => r[4] == TriageDetail.TranslationsDropped));
        Assert.Contains(rows, r => r[4] == "not shown" && r[5].StartsWith("51 more differences"));   // 250 log texts + the name
        // the stage rows are labeled by each stage's unique Index
        Assert.Contains(rows, r => r[3] == "Stages[Index 0].LogEntries[0].Entry");
        // the summary table lists the real change first
        Assert.Equal("Aliases", Table(c, "Changes to official records").Rows[0][3]);
        Assert.Contains("changes 3 fields (2 of them only in their translations, not in English) in 1 official record", Notes(c));
    }

    [Fact]
    public void Translation_rows_inside_a_changed_field_never_crowd_out_its_real_change()
    {
        // code review 0.17.0 round 3: a real change to the last of 250 stages came after the 249 other stages' texts
        // (dropped translations) in the same field, so it fell past the record's 200 rows
        using var game = new Game(s =>
        {
            var stages = new ExtendedList<QuestStage>();
            for (int i = 0; i < 250; i++)
                stages.Add(new QuestStage { Index = (ushort)(i * 10), LogEntries = new ExtendedList<QuestLogEntry> { new() { Entry = Localized.Text($"Log {i}") } } });
            s.Quests.Add(new Quest(s) { EditorID = "TD_QuestS", Stages = stages });
        }, localized: true);
        game.Mod((m, s) =>
        {
            var q = m.Quests.GetOrAddAsOverride(s.Quests.First());
            q.Stages[249].LogEntries[0].Entry = Localized.Text("Log changed");
        });
        var c = game.Run();

        var rows = Detail(c);
        var real = Assert.Single(rows, r => r[4] == TriageDetail.Changed);
        Assert.Equal("Stages[Index 2490].LogEntries[0].Entry", real[3]);
        Assert.Equal(real, rows[0]);   // the field's real change comes before its translation rows
        Assert.Equal(TriageDetail.MaxPerRecord, rows.Count(r => r[4] == TriageDetail.TranslationsDropped));
        Assert.Contains(rows, r => r[4] == "not shown" && r[5].StartsWith("49 more differences"));   // 249 texts, 200 shown
    }

    [Fact]
    public void Detail_lines_stop_at_the_record_limit_and_values_at_the_character_limit()
    {
        var d = new TriageDetail();
        var (lines, hidden) = d.Field("List", Enumerable.Range(0, 300).ToList(), Enumerable.Range(1000, 300).ToList(), null);
        Assert.Equal(TriageDetail.MaxPerRecord, lines.Count);
        Assert.Equal(100, hidden);
        Assert.Equal("List[0]", lines[0].Path);
        var cut = TriageDetail.Cap(new string('x', TriageDetail.MaxValue + 1234));
        Assert.EndsWith("… (1,234 more characters)", cut);
        Assert.Equal(TriageDetail.MaxValue + "… (1,234 more characters)".Length, cut.Length);
    }

    [Fact]
    public void Field_by_field_table_lists_real_changes_before_translation_only_records()
    {
        // code review 0.17.0 round 2: rows went in record order, so translation-only records early in the plugin could
        // fill the report's 10,000 rows before a later record's real change
        using var game = new Game(s =>
        {
            s.Quests.Add(new Quest(s) { EditorID = "TD_QuestA", Name = Localized.Text("Td Quest A") });
            var f1 = s.Factions.AddNew("TD_F1"); var f2 = s.Factions.AddNew("TD_F2");
            var alias = new QuestReferenceAlias
            {
                ID = 7, Name = "TdAlias",
                Factions = new ExtendedList<IFormLinkGetter<IFactionGetter>> { f1.ToLink<IFactionGetter>(), f2.ToLink<IFactionGetter>() },
            };
            s.Quests.Add(new Quest(s) { EditorID = "TD_QuestB", Aliases = new ExtendedList<AQuestAlias> { alias } });
        }, localized: true);
        game.Mod((m, s) =>
        {
            m.Quests.GetOrAddAsOverride(s.Quests.Single(q => q.EditorID == "TD_QuestA"));
            var a = (QuestReferenceAlias)m.Quests.GetOrAddAsOverride(s.Quests.Single(q => q.EditorID == "TD_QuestB")).Aliases[0];
            var moved = a.Factions![1];
            a.Factions.RemoveAt(1);
            a.Factions.Insert(0, moved);
        });
        var c = game.Run();

        var rows = Detail(c);
        Assert.Contains(rows, r => r[1].Contains("TD_QuestA") && r[4] == TriageDetail.TranslationsDropped);
        Assert.Contains("TD_QuestB", rows[0][1]);
        Assert.Equal(TriageDetail.Reordered, rows[0][4]);
    }

    [Fact]
    public void A_list_too_long_to_match_gives_one_line_not_every_entry_removed_and_added()
    {
        // code review 0.17.0 round 2: 2,500 entries with one appended gave 5,001 removed and added lines
        var d = new TriageDetail();
        var before = Enumerable.Range(0, 2500).ToList();
        var (lines, hidden) = d.Field("List", before, before.Append(9999).ToList(), null);
        var line = Assert.Single(lines);
        Assert.Equal(0, hidden);
        Assert.Equal(TriageDetail.Changed, line.Kind);
        Assert.EndsWith("(2,500 entries before, 2,501 after; too long to match entry by entry)", line.After);
    }
}
