// Triage section "Plugins" (0.8.0, chunk C2) on made-up games and mods: a Starfield.esm and an SFBGS003.esm built here
// with BuildKit, a game archive made in the test, and mods that hold an ITM, a deleted official record, an undone official
// change, a missing script, temporary references left out of ONAM, a missing master and text that isn't UTF-8. No game
// files are read. Also ConflictAnalyzer's `only` set.
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class TriagePluginsTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    static readonly WriteOptions NoOnam = new() { GapCheck = false, FixEffects = false, OverriddenForms = Mutagen.Bethesda.Plugins.Binary.Parameters.OverriddenFormsOption.NoCheck };

    /// <summary>A made-up game: Starfield.esm with two keywords, a misc item (value 5, weight 1) and an interior cell
    /// holding one temporary placed object; SFBGS003.esm (an official update) raises the misc item's value to 10; the
    /// archive "Starfield - Misc.ba2" holds scripts\trtest\found.pex. Mods are written into ModDir.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, ModDir;
        public Game(bool dlc = true)
        {
            Data = Path.Combine(Sub("Game"), "Data");
            Directory.CreateDirectory(Data);
            ModDir = Sub("Mod");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            s.Keywords.AddNew("S_KeepKw");
            s.Keywords.AddNew("S_GoneKw");
            var misc = s.MiscItems.AddNew("S_Misc");
            misc.Value = 5;
            misc.Weight = 1;
            var cell = new Cell(s) { EditorID = "S_Cell" };
            cell.Temporary.Add(new PlacedObject(s) { Base = misc.ToLink<IPlaceableObjectGetter>() });
            s.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { cell } } } });
            using (var g = new GameData(Data)) PluginWriter.Write(s, Data, g, NoGap);
            if (dlc)
            {
                var d = Plugin.New("SFBGS003.esm", PluginStyle.Full, "test", "made-up official update");
                using var g = new GameData(Data);
                d.MiscItems.GetOrAddAsOverride(g.Open("Starfield.esm").MiscItems.First()).Value = 10;
                PluginWriter.Write(d, Data, g, NoGap);
            }
            File.WriteAllBytes(Path.Combine(Data, "Starfield - Misc.ba2"), TriageFilesTests.Gnrl(("scripts/TrTest/Found.pex", 10, false)));
        }

        /// <summary>Writes a light mod into ModDir (masters from Data and ModDir) and returns its path.</summary>
        public string Mod(string name, Action<StarfieldMod, Func<string, IStarfieldModGetter>> build, WriteOptions? o = null, string? dir = null, PluginStyle style = PluginStyle.Light)
        {
            var mod = Plugin.New(name, style, "test", "made-up mod");
            using var g = new GameData(Data, [ModDir]);
            build(mod, n => g.Open(n));
            return PluginWriter.Write(mod, dir ?? ModDir, g, o ?? NoGap);
        }

        public TriageContext Context(string? dataDir = null)
        {
            var data = dataDir ?? Data;
            var (root, files) = TriageCommand.Gather(TriageInputKind.Folder, ModDir, "unused");
            var report = new TriageReport { InputName = "Mod", HasGame = File.Exists(Path.Combine(data, "Starfield.esm")), DataDir = data };
            return new TriageContext
            {
                Options = new TriageOptions { DataDir = data }, Kind = TriageInputKind.Folder, InputPath = ModDir, Root = root, Files = files,
                DataPrefix = "", DataDir = data, Work = Sub("work"), Report = report,
            };
        }
    }

    static List<TriageFinding> Run(TriageContext c)
    {
        VanillaIndex.ForgetLoaded();
        var f = TriagePlugins.Run(c).ToList();
        c.Report.Findings.AddRange(f);
        return f;
    }

    static string List(IEnumerable<TriageFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} {x.File}: {x.Text}"));

    static TriageFinding One(List<TriageFinding> f, TriageLevel level, string file, string fragment)
    {
        var hits = f.Where(x => x.Level == level && x.File == file && x.Text.Contains(fragment)).ToList();
        Assert.True(hits.Count == 1, $"wanted one {level} {file} ~'{fragment}', got:\n{List(f)}");
        return hits[0];
    }

    static void None(List<TriageFinding> f, TriageLevel level, string fragment) =>
        Assert.True(!f.Any(x => x.Level == level && x.Text.Contains(fragment)), $"wanted no {level} ~'{fragment}', got:\n{List(f)}");

    static TriageTable Table(TriageContext c, string title) => c.Report.Tables.Single(t => t.Title == title);

    /// <summary>The temporary placed object of Starfield.esm's cell, overridden with a change.</summary>
    static void PlacedOverride(StarfieldMod m, IStarfieldModGetter b)
    {
        var baseCell = b.Cells.Records.First().SubBlocks.First().Cells.First();
        var cell = (Cell)baseCell.DeepCopy();
        cell.Temporary.Clear(); cell.Persistent.Clear(); cell.NavigationMeshes.Clear();
        var r = (PlacedObject)baseCell.Temporary.First().DeepCopy();
        r.Radius = 5;
        cell.Temporary.Add(r);
        m.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { cell } } } });
    }

    [Fact]
    public void A_missing_script_keeps_its_full_look_and_names_skipped_links()
    {
        // settled design of 0.9.1 (NOTES-triage.md): a skipped link never softens the missing-script LOOK (that could
        // hide a broken script); the LOOK keeps its wording and adds how many links were skipped
        using var game = new Game();
        game.Mod("TrLink.esm", (m, _) =>
            m.Activators.AddNew("TrL_Act").VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "TrTest:InLink" } } });
        var c = game.Context();
        c.Skipped.Add(("trlink - main.ba2", TriageCommand.LinkWhy));
        c.Skipped.Add(("elsewhere\\x.pex", TriageCommand.LinkReachedWhy));   // reached through a link: not a link itself
        var look = One(Run(c), TriageLevel.Look, "trlink.esm", "1 attached script is in neither the mod (loose or in its archives) nor the game's archives, so it doesn't run");
        Assert.Contains("TrTest:InLink", look.Text);
        Assert.EndsWith(" (triage skipped 1 linked item, which may hold it; see Files)", look.Text);
    }

    [Fact]
    public void A_mod_with_an_itm_a_deleted_record_an_undone_change_and_a_missing_script()
    {
        using var game = new Game();
        game.Mod("TrMod.esm", (m, open) =>
        {
            var s = open("Starfield.esm");
            m.MiscItems.GetOrAddAsOverride(s.MiscItems.First()).Weight = 2;          // built on Starfield.esm: SFBGS003's value 10 is undone
            m.Keywords.GetOrAddAsOverride(s.Keywords.First(k => k.EditorID == "S_KeepKw"));   // unchanged: an ITM
            m.Keywords.GetOrAddAsOverride(s.Keywords.First(k => k.EditorID == "S_GoneKw")).IsDeleted = true;
            m.Activators.AddNew("Tr_Act").VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts =
                {
                    new ScriptEntry { Name = "TrTest:Missing" }, new ScriptEntry { Name = "TrTest:Found" }, new ScriptEntry { Name = "TrTest:Loose" },
                },
            };
        });
        Directory.CreateDirectory(Path.Combine(game.ModDir, "Scripts", "TrTest"));
        File.WriteAllText(Path.Combine(game.ModDir, "Scripts", "TrTest", "Loose.pex"), "x");
        var c = game.Context();
        var f = Run(c);

        One(f, TriageLevel.Note, "trmod.esm", "light master");
        One(f, TriageLevel.Look, "trmod.esm", "identical to the master version (ITM)");
        Assert.Contains(Table(c, "Identical to master (ITM)").Rows, r => r[1].Contains("S_KeepKw"));
        var del = One(f, TriageLevel.Look, "trmod.esm", "deletes 1 record of Starfield.esm");
        Assert.Contains("KYWD", del.Text);
        var undone = One(f, TriageLevel.Look, "trmod.esm", "undoes SFBGS003.esm");
        Assert.Contains("Value", undone.Text);
        Assert.Contains(Table(c, "Official changes undone").Rows, r => r[2] == "SFBGS003.esm" && r[3] == "Value" && r[4] == "10" && r[5] == "5");
        var miss = One(f, TriageLevel.Look, "trmod.esm", "1 attached script is in neither the mod");
        Assert.Contains("TrTest:Missing", miss.Text);
        Assert.Equal("scripts\\trtest\\missing.pex", Assert.Single(Table(c, "Attached scripts not found").Rows)[2]);
        // the mod's field changes to official records: the weight (and not the unchanged keyword)
        Assert.Contains(Table(c, "Changes to official records").Rows, r => r[1].Contains("S_Misc") && r[3] == "Weight" && r[4] == "1" && r[5] == "2");
        One(f, TriageLevel.Note, "trmod.esm", "changes 1 field in 1 official record");
        // tables by type and origin
        Assert.Contains(Table(c, "Records by type").Rows, r => r[1] == "KYWD" && r[3] == "2" && r[4] == "1");
        Assert.Contains(Table(c, "Overrides by origin").Rows, r => r[1] == "Starfield.esm" && r[2] == "MISC" && r[3] == "1");
        Assert.Contains(Table(c, "Deleted records of other plugins").Rows, r => r[1] == "KYWD" && r[3] == "Starfield.esm");
        // nothing pretends to be a verdict
        Assert.All(f, x => Assert.DoesNotMatch("(?i)malicious|unsafe|\\bbad\\b", x.Text));
    }

    [Fact]
    public void Temporary_references_left_out_of_onam_are_a_look()
    {
        using var game = new Game(dlc: false);
        game.Mod("TrOnam.esm", (m, open) => PlacedOverride(m, open("Starfield.esm")), NoOnam);
        var c = game.Context();
        var f = Run(c);
        One(f, TriageLevel.Look, "tronam.esm", "temporary reference of full masters that its ONAM list doesn't name");
        var row = Assert.Single(Table(c, "Temporary references missing from ONAM").Rows);
        Assert.Equal(("REFR", "Starfield.esm"), (row[1], row[3]));
    }

    [Fact]
    public void Onam_written_like_the_creation_kit_is_only_a_note()
    {
        using var game = new Game(dlc: false);
        game.Mod("TrOnamOk.esm", (m, open) => PlacedOverride(m, open("Starfield.esm")));   // BuildKit's default: ONAM filled
        var f = Run(game.Context());
        One(f, TriageLevel.Note, "tronamok.esm", "all listed in its ONAM field");
        None(f, TriageLevel.Look, "ONAM");
    }

    [Fact]
    public void A_missing_master_is_a_look_and_the_rest_is_still_reported()
    {
        using var game = new Game(dlc: false);
        var other = game.Sub("Other");
        game.Mod("TrDep.esm", (m, open) => m.Keywords.AddNew("Dep_Kw"), dir: other, style: PluginStyle.Full);   // a missing master is taken as full
        File.Copy(Path.Combine(other, "TrDep.esm"), Path.Combine(game.ModDir, "TrDep.esm"));   // there while building only
        game.Mod("TrNeeds.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("TrDep.esm").Keywords.First()));
        File.Delete(Path.Combine(game.ModDir, "TrDep.esm"));
        var c = game.Context();
        var f = Run(c);
        One(f, TriageLevel.Look, "trneeds.esm", "needs the master TrDep.esm, which is neither in the mod nor in the game's Data folder");
        One(f, TriageLevel.Note, "trneeds.esm", "1 overrides of other plugins' records");
        Assert.Contains(Table(c, "Plugin headers").Rows, r => r[0] == "TrNeeds.esm" && r[2].Contains("TrDep.esm"));
    }

    [Fact]
    public void A_master_that_cant_be_read_is_named_not_blamed_on_the_mod()
    {
        // 0.8.0 code review: a master in Data that can't be read (a lost read grant) made the mod's own plugin look damaged
        using var game = new Game(dlc: false);
        game.Mod("TrDep.esm", (m, open) => m.Keywords.AddNew("Dep_Kw"), dir: game.Data, style: PluginStyle.Full);
        game.Mod("TrNeeds.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("TrDep.esm").Keywords.First()));
        var c = game.Context();
        List<TriageFinding> f;
        using (new FileStream(Path.Combine(game.Data, "TrDep.esm"), FileMode.Open, FileAccess.Read, FileShare.None))   // locked: can't be opened
            f = Run(c);
        Assert.Contains("check that file's permissions", One(f, TriageLevel.Look, "trneeds.esm", "its master TrDep.esm is there but can't be read").Text);
        None(f, TriageLevel.Look, "can't be read to the end");
        None(f, TriageLevel.Look, "isn't a plugin the game can read");

        // a damaged master gets the other fix (second code review)
        var dep = Path.Combine(game.Data, "TrDep.esm");
        var bytes = File.ReadAllBytes(dep);
        File.WriteAllBytes(dep, bytes[..30]);   // the TES4 header cut short
        var c2 = game.Context();
        var f2 = Run(c2);
        // 30 bytes: its header still reads, so the plugin check and the comparison name it as unloadable
        Assert.True(f2.Any(x => x.File == "trneeds.esm" && x.Text.Contains("TrDep.esm is unloadable")), List(f2));
        None(f2, TriageLevel.Look, "can't be read to the end");
        None(f2, TriageLevel.Look, "isn't a plugin the game can read");

        // shorter than a plugin header: damaged too, never "missing" (third code review)
        File.WriteAllBytes(dep, bytes[..10]);
        var f3 = Run(game.Context());
        var damaged = One(f3, TriageLevel.Look, "trneeds.esm", "its master TrDep.esm is there but can't be read").Text;
        Assert.Contains("looks damaged or cut short", damaged);
        Assert.Contains("install it again", damaged);
        Assert.DoesNotContain("permissions", damaged);
        None(f3, TriageLevel.Look, "neither in the mod nor");
    }

    [Fact]
    public void A_requirement_installed_in_data_is_named_and_compared()
    {
        using var game = new Game(dlc: false);
        game.Mod("TrReq.esm", (m, open) => m.Keywords.AddNew("Req_Kw"), dir: game.Data);   // another mod the player has
        game.Mod("TrUser.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("TrReq.esm").Keywords.First()));
        var f = Run(game.Context());
        One(f, TriageLevel.Note, "truser.esm", "needs TrReq.esm, another mod's plugin");
        One(f, TriageLevel.Look, "truser.esm", "identical to the master version (ITM)");   // compared with TrReq's copy
        None(f, TriageLevel.Look, "needs the master");
    }

    [Fact]
    public void Text_that_isnt_utf8_is_noted()
    {
        using var game = new Game(dlc: false);
        var path = game.Mod("TrText.esm", (m, open) => m.Keywords.AddNew("TrEncX_e"));
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf("TrEncX_e"u8);
        Assert.True(at > 0);
        bytes[at + 7] = 0xE9;   // a Windows-1252 e-acute: not valid UTF-8 on its own
        File.WriteAllBytes(path, bytes);
        var c = game.Context();
        var f = Run(c);
        One(f, TriageLevel.Note, "trtext.esm", "1 text field isn't valid UTF-8");
        Assert.Equal("EDID", Assert.Single(Table(c, "Text that isn't valid UTF-8").Rows)[2]);
    }

    [Fact]
    public void Without_the_game_the_plugin_alone_is_still_reported()
    {
        using var game = new Game(dlc: false);
        game.Mod("TrAlone.esm", (m, open) => { m.Keywords.AddNew("Alone_Kw"); m.Keywords.GetOrAddAsOverride(open("Starfield.esm").Keywords.First()); });
        var empty = game.Sub("NoGame");
        var c = game.Context(empty);
        var f = Run(c);
        Assert.Single(f, x => x.File == "" && x.Text.StartsWith("no game found"));
        One(f, TriageLevel.Note, "tralone.esm", "1 new, 1 overrides");
        None(f, TriageLevel.Look, "Starfield.esm");   // an official master isn't called missing without the game
        None(f, TriageLevel.Look, "ITM");
    }

    [Fact]
    public void A_file_that_isnt_a_plugin_is_a_look_not_a_stop()
    {
        using var game = new Game(dlc: false);
        File.WriteAllText(Path.Combine(game.ModDir, "Fake.esm"), "x");
        var f = Run(game.Context());
        One(f, TriageLevel.Look, "fake.esm", "isn't a plugin the game can read");
    }

    [Fact]
    public void A_mod_without_plugins_says_so()
    {
        using var game = new Game(dlc: false);
        File.WriteAllText(Path.Combine(game.ModDir, "readme.txt"), "x");
        var f = Run(game.Context());
        Assert.Equal("the mod has no plugins (.esm, .esp or .esl), so there are no records to check", Assert.Single(f).Text);
    }

    [Fact]
    public void Light_formids_past_the_range_are_a_look()
    {
        using var game = new Game(dlc: false);
        var path = game.Mod("TrLight.esm", (m, open) => m.Keywords.AddNew("L_Kw"));
        var bytes = File.ReadAllBytes(path);
        var rec = bytes.AsSpan().LastIndexOf("KYWD"u8);   // the record (the group's label comes before it)
        Assert.Equal(0xFE000800u, BitConverter.ToUInt32(bytes, rec + 12));   // light style: FE, light master index 0 = itself
        BitConverter.GetBytes(0x00001234u).CopyTo(bytes, rec + 12);         // full style (index 0 = itself: no masters), own ID 1234: past FFF
        File.WriteAllBytes(path, bytes);
        var f = Run(game.Context());
        One(f, TriageLevel.Look, "trlight.esm", "past the end of the light range");
    }

    // ---- ConflictAnalyzer's `only` set ----

    [Fact]
    public void Analyzer_only_limits_the_records_and_lists_their_changes()
    {
        using var game = new Game(dlc: false);
        game.Mod("TrA.esm", (m, open) => m.MiscItems.GetOrAddAsOverride(open("Starfield.esm").MiscItems.First()).Value = 7, dir: game.Data);
        game.Mod("TrB.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Starfield.esm").Keywords.First()).EditorID = "S_KeepKwRenamed", dir: game.Data);
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*TrA.esm", "*TrB.esm"]);
        var lo = LoadOrderSetup.Build(game.Data, txt);
        try
        {
            var all = Analyzer.Run(lo.Plugins);
            Assert.Equal(2, all.RecordsOverriddenByMods);
            Assert.Empty(all.Changes);   // without `only`: as before, no changes listed
            var a = Analyzer.Run(lo.Plugins, new HashSet<string> { "tra.esm" });
            Assert.Equal(1, a.RecordsOverriddenByMods);
            var ch = Assert.Single(a.Changes);
            Assert.Equal(("TrA.esm", "Value", "Starfield.esm", "5", "7"), (ch.Plugin, ch.Field, ch.Base, ch.BaseValue, ch.NewValue));
            // onlyRecords: of those plugins' overrides, only these records
            var none = Analyzer.Run(lo.Plugins, new HashSet<string> { "TrA.esm", "TrB.esm" }, new HashSet<FormKey> { ch.FormKey });
            Assert.Equal(1, none.RecordsOverriddenByMods);
            Assert.Equal("TrA.esm", Assert.Single(none.Changes).Plugin);
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    // ---- records in cells, compared raw ----

    [Fact]
    public void A_mod_that_changes_only_placed_objects_is_not_pointed_to_an_empty_field_by_field_table()
    {
        // 0.17.0 code review round 4: the NOTE said each difference was in the field-by-field table, which has no rows
        // for records compared raw
        using var game = new Game(dlc: false);
        game.Mod("TrRaw.esm", (m, open) => PlacedOverride(m, open("Starfield.esm")));
        var c = game.Context();
        var f = Run(c);
        var note = One(f, TriageLevel.Note, "trraw.esm", "changes 1 field in 1 official record");
        Assert.Contains("compared raw, so the field-by-field table has no rows for them", note.Text);
        Assert.DoesNotContain("each difference inside it", note.Text);
        Assert.Empty(Table(c, "Changes to official records, field by field").Rows);
    }

    [Fact]
    public void An_official_change_to_a_placed_reference_the_mod_undoes_is_found_raw()
    {
        using var game = new Game(dlc: false);
        // an official update scales the placed object; the mod, built on Starfield.esm only, moves its radius instead
        var d = Plugin.New("SFBGS003.esm", PluginStyle.Full, "test", "made-up official update");
        using (var g = new GameData(game.Data))
        {
            var s = g.Open("Starfield.esm");
            var baseCell = s.Cells.Records.First().SubBlocks.First().Cells.First();
            var cell = (Cell)baseCell.DeepCopy();
            cell.Temporary.Clear(); cell.Persistent.Clear(); cell.NavigationMeshes.Clear();
            var r = (PlacedObject)baseCell.Temporary.First().DeepCopy();
            r.Scale = 2;
            cell.Temporary.Add(r);
            d.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { cell } } } });
            PluginWriter.Write(d, game.Data, g, NoGap);
        }
        game.Mod("TrCell.esm", (m, open) => PlacedOverride(m, open("Starfield.esm")));
        var c = game.Context();
        var f = Run(c);
        var look = One(f, TriageLevel.Look, "trcell.esm", "undoes SFBGS003.esm");
        Assert.Contains("XSCL", look.Text);
        Assert.Contains(Table(c, "Official changes undone").Rows, r => r[2] == "SFBGS003.esm" && r[3] == "XSCL (scale)" && r[5] == "(none)");
        Assert.Contains(Table(c, "Changes to official records").Rows, r => r[0] == "TrCell.esm" && r[1].Contains("REFR") && r[3].StartsWith("XRDS"));
    }
}
