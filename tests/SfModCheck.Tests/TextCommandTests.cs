// sf-mod-check 0.28.0: the text command (TextCommand.cs), tests 1 to 8 of NOTES.md "0.28.0 (Followups)", part (a).
// All plugins are made up: a made-up Starfield.esm stands in for the official file.
using System.IO.Compression;
using System.Text.Json.Nodes;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class TextCommandTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Data\Starfield.esm: ingestibles S_Drink (named "Water") and S_Food (named "Bread", weight 1).
    /// mods\Mod.esm: a new BOOK, MESG (text and two buttons), TMLM (body text and a menu item) and MISC; overrides
    /// S_Drink renamed "Clean Water" and S_Food with only its weight changed.</summary>
    sealed class Set : Sandbox
    {
        public readonly string Data, ModDir;
        public readonly FormKey Drink, Food;

        public Set(Action<StarfieldMod>? more = null, Action<StarfieldMod>? baseMore = null)
        {
            Data = Sub("Data");
            ModDir = Sub("mods");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            baseMore?.Invoke(s);
            var drink = s.Ingestibles.AddNew("S_Drink");
            drink.Name = "Water";
            Drink = drink.FormKey;
            var food = s.Ingestibles.AddNew("S_Food");
            food.Name = "Bread";
            food.Weight = 1;
            Food = food.FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(s, Data, g, NoGap);
            using (var g = new GameData(Data, [ModDir]))
            {
                var sf = g.Open("Starfield.esm");
                var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up mod");
                var book = m.Books.AddNew("M_Book");
                book.Name = "Field Guide";
                book.Text = "Page one.";
                var msg = m.Messages.AddNew("M_Msg");
                msg.Description = "Proceed?";
                msg.MenuButtons.Add(new MessageButton { Text = "Yes" });
                msg.MenuButtons.Add(new MessageButton { Text = "No" });
                var term = m.TerminalMenus.AddNew("M_Term");
                term.BodyTexts = new ExtendedList<TerminalMenuBodyText> { new() { Text = "Welcome aboard" } };
                term.MenuItems = new ExtendedList<TerminalMenuItem> { new() { Text = "Crew logs", Target = new TerminalMenuItemForceRedraw() } };
                m.MiscItems.AddNew("M_Rock").Name = "Lucky Rock";
                m.Ingestibles.GetOrAddAsOverride(sf.Ingestibles.First(k => k.EditorID == "S_Drink")).Name = "Clean Water";
                m.Ingestibles.GetOrAddAsOverride(sf.Ingestibles.First(k => k.EditorID == "S_Food")).Weight = 2;
                more?.Invoke(m);
                PluginWriter.Write(m, ModDir, g, NoGap);
            }
        }

        public string Mod => Path.Combine(ModDir, "Mod.esm");

        public (int Code, string Stdout, string Stderr) Text(string target, params string[] extra) =>
            ExeTests.RunSplit(new[] { "text", target, "--data", Data }.Concat(extra).ToArray());
    }

    static List<string> Lines(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>The lines from the plugin's heading to the RESULT line (the byline and the "text of" line left out).</summary>
    static List<string> Listing(string stdout) =>
        Lines(stdout).SkipWhile(l => !l.StartsWith("Mod.esm (", StringComparison.Ordinal)).TakeWhile(l => !l.StartsWith("RESULT:", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Test1_new_book_message_terminal_and_misc_strings_are_listed_with_their_paths_by_type()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Text(s.Mod);
        Assert.True(code == 0, stdout + stderr);
        var l = Lines(stdout);
        Assert.Contains("  BOOK (1 record, 2 strings)", l);
        Assert.Contains(l, x => x.StartsWith("    BOOK M_Book [", StringComparison.Ordinal) && x.EndsWith(":Mod.esm] new", StringComparison.Ordinal));
        Assert.Contains("      Name: Field Guide", l);
        Assert.Contains("      Text: Page one.", l);
        Assert.Contains("  MESG (1 record, 3 strings)", l);
        Assert.Contains("      Description: Proceed?", l);
        Assert.Contains("      MenuButtons[0].Text: Yes", l);
        Assert.Contains("      MenuButtons[1].Text: No", l);
        Assert.Contains("  TMLM (1 record, 2 strings)", l);
        Assert.Contains("      BodyTexts[0].Text: Welcome aboard", l);
        Assert.Contains("      MenuItems[0].Text: Crew logs", l);
        Assert.Contains("  MISC (1 record, 1 string)", l);
        Assert.Contains("      Name: Lucky Rock", l);
        // grouped by type in signature order
        var heads = l.Where(x => x.Length > 2 && x.StartsWith("  ", StringComparison.Ordinal) && x[2] != ' ').Select(x => x[2..6]).ToList();
        Assert.Equal(["ALCH", "BOOK", "MESG", "MISC", "TMLM"], heads);
        Assert.Contains("RESULT: text: 9 strings in 5 records (4 new, 1 changed) in Mod.esm", stdout);
    }

    [Fact]
    public void Test2_a_renamed_override_is_listed_with_was_and_a_weight_only_override_is_left_out()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Text(s.Mod);
        Assert.True(code == 0, stdout + stderr);
        var l = Listing(stdout);
        var at = l.FindIndex(x => x == $"    ALCH S_Drink [{s.Drink}] changed");
        Assert.True(at >= 0, stdout);
        Assert.Equal("      Name: Clean Water", l[at + 1]);
        Assert.Equal("        was: Water", l[at + 2]);
        Assert.Contains("  ALCH (1 record, 1 string)", l);
        Assert.DoesNotContain(l, x => x.Contains("S_Food") || x.Contains(s.Food.ToString()));
    }

    // 0.28.0 step 5 (bug hunt: a real override drops its master's quest name and objectives, and text listed nothing):
    // an override that clears a string its master has is listed, with "(no text)" and the master's text as "was"
    [Fact]
    public void Test9_an_override_that_clears_a_master_string_is_listed_as_no_text_with_was()
    {
        using var s = new Set(m => m.Ingestibles.First(x => x.EditorID == "S_Food").Name = null);
        var (code, stdout, stderr) = s.Text(s.Mod);
        Assert.True(code == 0, stdout + stderr);
        var l = Listing(stdout);
        var at = l.FindIndex(x => x == $"    ALCH S_Food [{s.Food}] changed");
        Assert.True(at >= 0, stdout);
        Assert.Equal("      Name: (no text)", l[at + 1]);
        Assert.Equal("        was: Bread", l[at + 2]);
        Assert.Contains("  ALCH (2 records, 2 strings)", l);
        Assert.Contains("RESULT: text: 10 strings in 6 records (4 new, 2 changed) in Mod.esm", stdout);

        var (jcode, json, jerr) = s.Text(s.Mod, "--json");
        Assert.True(jcode == 0, json + jerr);
        var food = System.Text.Json.Nodes.JsonNode.Parse(json)!["plugins"]![0]!["records"]!.AsArray()
            .Single(r => (string?)r!["editorId"] == "S_Food")!;
        Assert.Equal("changed", (string?)food["kind"]);
        var str = food["strings"]!.AsArray().Single()!;
        Assert.Equal("Name", (string?)str["field"]);
        Assert.Equal("", (string?)str["text"]);
        Assert.Equal("Bread", (string?)str["was"]);
    }

    // 0.28.0 step 5: a partial-form quest override (flag 0x4000) that leaves out the master's name keeps it in the
    // game, so it is not listed; the same override without the flag clears the name and is listed
    [Fact]
    public void Test10_a_partial_form_that_leaves_out_a_master_string_is_not_listed_as_cleared()
    {
        FormKey partial = default, full = default;
        using var s = new Set(m =>
        {
            m.Quests.Add(new Quest(partial, StarfieldRelease.Starfield) { EditorID = "S_QuestPartial", MajorRecordFlagsRaw = 0x4000 });
            m.Quests.Add(new Quest(full, StarfieldRelease.Starfield) { EditorID = "S_QuestFull" });
        }, b =>
        {
            var q1 = b.Quests.AddNew("S_QuestPartial");
            q1.Name = "Old Friends";
            partial = q1.FormKey;
            var q2 = b.Quests.AddNew("S_QuestFull");
            q2.Name = "New Friends";
            full = q2.FormKey;
        });
        var (code, stdout, stderr) = s.Text(s.Mod, "--type", "QUST");
        Assert.True(code == 0, stdout + stderr);
        var l = Listing(stdout);
        Assert.DoesNotContain(l, x => x.Contains("S_QuestPartial"));
        var at = l.FindIndex(x => x == $"    QUST S_QuestFull [{full}] changed");
        Assert.True(at >= 0, stdout);
        Assert.Equal("      Name: (no text)", l[at + 1]);
        Assert.Equal("        was: New Friends", l[at + 2]);
        Assert.Contains("RESULT: text: 1 string in 1 record (0 new, 1 changed) in Mod.esm", stdout);
    }

    // 0.28.0 code review round 1: list entries are matched by their text, not their index, so a button added in front
    // lists only that button, and a button left out lists only that one as (no text)
    [Fact]
    public void Test11_a_list_entry_added_or_left_out_lists_only_that_entry()
    {
        FormKey added = default, removed = default;
        using var s = new Set(m =>
        {
            var a = new Message(added, StarfieldRelease.Starfield) { EditorID = "S_MsgAdded", Description = "Proceed?" };
            a.MenuButtons.Add(new MessageButton { Text = "Maybe" });
            a.MenuButtons.Add(new MessageButton { Text = "Yes" });
            a.MenuButtons.Add(new MessageButton { Text = "No" });
            m.Messages.Add(a);
            var r = new Message(removed, StarfieldRelease.Starfield) { EditorID = "S_MsgRemoved", Description = "Proceed?" };
            r.MenuButtons.Add(new MessageButton { Text = "No" });
            m.Messages.Add(r);
        }, b =>
        {
            foreach (var id in new[] { "S_MsgAdded", "S_MsgRemoved" })
            {
                var msg = b.Messages.AddNew(id);
                msg.Description = "Proceed?";
                msg.MenuButtons.Add(new MessageButton { Text = "Yes" });
                msg.MenuButtons.Add(new MessageButton { Text = "No" });
                if (id == "S_MsgAdded") added = msg.FormKey; else removed = msg.FormKey;
            }
        });
        var (code, stdout, stderr) = s.Text(s.Mod, "--type", "MESG");
        Assert.True(code == 0, stdout + stderr);
        var l = Listing(stdout);
        var at = l.FindIndex(x => x == $"    MESG S_MsgAdded [{added}] changed");
        Assert.True(at >= 0, stdout);
        Assert.Equal("      MenuButtons[0].Text: Maybe", l[at + 1]);
        Assert.Equal("        was: (no text in the master)", l[at + 2]);
        Assert.True(at + 3 >= l.Count || !l[at + 3].StartsWith("      ", StringComparison.Ordinal), stdout);
        var rt = l.FindIndex(x => x == $"    MESG S_MsgRemoved [{removed}] changed");
        Assert.True(rt >= 0, stdout);
        Assert.Equal("      MenuButtons[0].Text: (no text)", l[rt + 1]);
        Assert.Equal("        was: Yes", l[rt + 2]);
        Assert.True(rt + 3 >= l.Count || !l[rt + 3].StartsWith("      ", StringComparison.Ordinal), stdout);
    }

    // 0.28.0 code review round 1: a localized override whose string files are missing reads every string as empty;
    // its master's strings are not listed as cleared (only the WARNING says the files are missing)
    [Fact]
    public void Test12_a_localized_override_without_its_string_files_lists_nothing_as_cleared()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var dir = s.Sub("loc");
        var b = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
        b.Ingestibles.AddNew("S_Drink").Name = "Water";
        using (var g = new GameData(data)) PluginWriter.Write(b, data, g, NoGap);
        using (var g = new GameData(data, [dir]))
        {
            var sf = g.Open("Starfield.esm");
            var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up localized mod", localized: true);
            m.Ingestibles.GetOrAddAsOverride(sf.Ingestibles.First(k => k.EditorID == "S_Drink")).Name = Localized.Text("Clean Water");
            PluginWriter.Write(m, dir, g, NoGap);
        }
        var (c1, o1, e1) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(c1 == 0, o1 + e1);
        Assert.Contains("      Name: Clean Water", Lines(o1));
        Assert.Contains("        was: Water", Lines(o1));

        Directory.Delete(Path.Combine(dir, "Strings"), true);
        var (c2, o2, e2) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(c2 == 0, o2 + e2);
        Assert.DoesNotContain(Lines(o2), x => x.Contains(TextCommand.NoText));
        Assert.Contains("RESULT: text: 0 strings in 0 records (0 new, 0 changed) in Mod.esm; WARNING: Mod.esm's string files weren't found", o2);
    }

    // 0.28.0 code review round 2: a localized master whose string files are missing reads every "was" as empty; a
    // WARNING says so (it said nothing, and every override read as changed against no text)
    [Fact]
    public void Test13_a_localized_master_without_its_string_files_gets_a_warning()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var dir = s.Sub("mods");
        var b = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up localized base game", localized: true);
        b.Ingestibles.AddNew("S_Drink").Name = Localized.Text("Water");
        using (var g = new GameData(data)) PluginWriter.Write(b, data, g, NoGap);
        using (var g = new GameData(data, [dir]))
        {
            var sf = g.Open("Starfield.esm");
            var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up mod");
            m.Ingestibles.GetOrAddAsOverride(sf.Ingestibles.First(k => k.EditorID == "S_Drink")).Name = "Clean Water";
            PluginWriter.Write(m, dir, g, NoGap);
        }
        var strings = Path.Combine(data, "Strings");
        Assert.True(Directory.Exists(strings), "the made-up master's string files weren't written next to it");
        var (c1, o1, e1) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(c1 == 0, o1 + e1);
        Assert.Contains("        was: Water", Lines(o1));
        Assert.DoesNotContain("WARNING", o1);

        Directory.Delete(strings, true);
        var (c2, o2, e2) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(c2 == 0, o2 + e2);
        Assert.Contains("        was: (no text in the master)", Lines(o2));
        Assert.Contains(Lines(o2), x => x.StartsWith("  WARNING: every one of the ", StringComparison.Ordinal) && x.Contains("of the master versions it overrides reads as empty")
                                        && x.Contains("Starfield.esm"));
        Assert.Contains("RESULT: text: 1 string in 1 record (0 new, 1 changed) in Mod.esm; WARNING: the string files of Mod.esm's masters weren't found", o2);
        var (c3, j3, e3) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data, "--json");
        Assert.True(c3 == 0, j3 + e3);
        Assert.NotNull(JsonNode.Parse(j3)!["plugins"]![0]!["masterWarning"]);
    }

    // 0.28.0 code review round 2: a string at its master entry's place keeps it, so a later exact match isn't taken by
    // an earlier text-only one (["Accept", "Decline"] to ["Decline", "Decline"] listed [1] as changed from Accept)
    [Fact]
    public void Test14_an_exact_match_is_paired_before_a_text_match()
    {
        var master = new List<TextCommand.Str> { new("MenuButtons[0].Text", "Accept", null), new("MenuButtons[1].Text", "Decline", null) };
        var ov = new List<TextCommand.Str> { new("MenuButtons[0].Text", "Decline", null), new("MenuButtons[1].Text", "Decline", null) };
        var c = Assert.Single(TextCommand.Changes(master, ov, cleared: true));
        Assert.Equal(new TextCommand.Str("MenuButtons[0].Text", "Decline", "Accept"), c);
    }

    [Fact]
    public void Test3_a_localized_plugin_lists_its_text_and_warns_without_its_string_files()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var dir = s.Sub("loc");
        using (var g = new GameData(data)) PluginWriter.Write(Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game"), data, g, NoGap);
        var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up localized mod", localized: true);
        m.MiscItems.AddNew("L_Rock").Name = Localized.Text("Lucky Rock");
        var book = m.Books.AddNew("L_Book");
        book.Name = Localized.Text("Field Guide");
        book.Text = Localized.Text("Page one.");
        using (var g = new GameData(data, [dir])) PluginWriter.Write(m, dir, g, NoGap);
        var strings = Path.Combine(dir, "Strings");
        Assert.True(Directory.Exists(strings), "the test plugin's string files weren't written next to it");

        var (code, stdout, stderr) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(code == 0, stdout + stderr);
        var l = Lines(stdout);
        Assert.Contains("Mod.esm (localized, 3 strings in 2 records)", l);
        Assert.Contains("      Name: Lucky Rock", l);
        Assert.Contains("      Text: Page one.", l);
        Assert.DoesNotContain(l, x => x.Contains("WARNING"));

        Directory.Delete(strings, true);
        var (c2, o2, e2) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(c2 == 0, o2 + e2);
        Assert.Contains(Lines(o2), x => x.StartsWith("  WARNING: Mod.esm is localized but every one of its 3 strings reads as empty", StringComparison.Ordinal)
                                        && x.Contains("Strings\\Mod_en.STRINGS"));
        Assert.Contains("RESULT: text: 0 strings in 0 records (0 new, 0 changed) in Mod.esm; WARNING: Mod.esm's string files weren't found", o2);
    }

    [Fact]
    public void Test4_type_keeps_only_that_type_and_an_unknown_type_is_refused()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Text(s.Mod, "--type", "MESG");
        Assert.True(code == 0, stdout + stderr);
        var l = Listing(stdout);
        Assert.Contains("  MESG (1 record, 3 strings)", l);
        Assert.DoesNotContain(l, x => x.StartsWith("  BOOK", StringComparison.Ordinal) || x.StartsWith("  MISC", StringComparison.Ordinal)
                                      || x.StartsWith("  TMLM", StringComparison.Ordinal) || x.StartsWith("  ALCH", StringComparison.Ordinal));
        Assert.Contains("RESULT: text: 3 strings in 1 record (1 new, 0 changed) in Mod.esm", stdout);

        var (c2, o2, e2) = s.Text(s.Mod, "--type", "XXXX");
        Assert.Equal(2, c2);
        Assert.Contains("--type: \"XXXX\" isn't a Starfield record type", e2);
        Assert.Contains("RESULT: CANNOT CHECK (unknown record type XXXX)", o2);
    }

    [Fact]
    public void Test5_json_has_the_designed_shape_out_writes_the_file_and_a_folder_out_is_refused()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Text(s.Mod, "--json");
        Assert.True(code == 0, stdout + stderr);
        var j = JsonNode.Parse(stdout)!;
        Assert.Equal("sf-mod-check text", (string?)j["tool"]);
        Assert.Equal("done", (string?)j["result"]);
        Assert.False(string.IsNullOrEmpty((string?)j["version"]));
        var p = Assert.Single(j["plugins"]!.AsArray())!;
        Assert.Equal("Mod.esm", (string?)p["name"]);
        Assert.False((bool)p["localized"]!);
        Assert.Null(p["warning"]);
        var recs = p["records"]!.AsArray();
        Assert.Equal(5, recs.Count);
        var drink = recs.Single(r => (string?)r!["editorId"] == "S_Drink")!;
        Assert.Equal("ALCH", (string?)drink["type"]);
        Assert.Equal(s.Drink.ToString(), (string?)drink["formKey"]);
        Assert.Equal("changed", (string?)drink["kind"]);
        var ds = Assert.Single(drink["strings"]!.AsArray())!;
        Assert.Equal("Name", (string?)ds["field"]);
        Assert.Equal("Clean Water", (string?)ds["text"]);
        Assert.Equal("Water", (string?)ds["was"]);
        var msg = recs.Single(r => (string?)r!["editorId"] == "M_Msg")!;
        Assert.Equal("new", (string?)msg["kind"]);
        Assert.Equal(["Description", "MenuButtons[0].Text", "MenuButtons[1].Text"], msg["strings"]!.AsArray().Select(x => (string)x!["field"]!).ToArray());
        Assert.Null(msg["strings"]![0]!["was"]);

        // --out: the text goes to the file, the console gets the Wrote line and the RESULT line; an existing file is replaced
        var outFile = Path.Combine(s.Sub("out"), "text.txt");
        File.WriteAllText(outFile, "old content");
        var (c2, o2, e2) = s.Text(s.Mod, "--out", outFile);
        Assert.True(c2 == 0, o2 + e2);
        var ol = Lines(o2).Where(x => x.Length > 0).ToList();
        Assert.Equal(2, ol.Count);
        Assert.StartsWith("Wrote ", ol[0]);
        Assert.EndsWith("text.txt: 9 strings in 5 records.", ol[0]);
        Assert.Equal("RESULT: text: 9 strings in 5 records (4 new, 1 changed) in Mod.esm", ol[1]);
        var file = File.ReadAllText(outFile);
        Assert.DoesNotContain("old content", file);
        Assert.Contains("      MenuButtons[1].Text: No", Lines(file));
        Assert.DoesNotContain("RESULT:", file);
        Assert.False(File.ReadAllBytes(outFile).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "--out wrote a BOM");

        // --out with --json writes the JSON
        var jsonFile = Path.Combine(s.Sub("out"), "text.json");
        var (c3, o3, e3) = s.Text(s.Mod, "--json", "--out", jsonFile);
        Assert.True(c3 == 0, o3 + e3);
        Assert.Equal("sf-mod-check text", (string?)JsonNode.Parse(File.ReadAllText(jsonFile))!["tool"]);

        // a folder as --out, and a plugin name as --out, are refused before anything is written
        var (c4, o4, e4) = s.Text(s.Mod, "--out", s.Sub("out"));
        Assert.Equal(2, c4);
        Assert.Contains("is a folder", e4);
        Assert.Contains("RESULT: CANNOT CHECK (--out is a folder)", o4);
        var (c5, _, e5) = s.Text(s.Mod, "--out", Path.Combine(s.Sub("out"), "Other.esm"));
        Assert.Equal(2, c5);
        Assert.Contains("not a .esm file", e5);
        Assert.False(File.Exists(Path.Combine(s.Sub("out"), "Other.esm")));
        // 0.28.0 code review round 1: a string file is a mod file too
        var table = Path.Combine(s.Sub("out"), "Mod_en.STRINGS");
        File.WriteAllText(table, "kept");
        var (c6, o6, e6) = s.Text(s.Mod, "--out", table);
        Assert.Equal(2, c6);
        Assert.Contains("not a .STRINGS file", e6);
        Assert.Contains("RESULT: CANNOT CHECK (--out names a mod file)", o6);
        Assert.Equal("kept", File.ReadAllText(table));
    }

    [Fact]
    public void Test6_a_zip_holding_the_plugin_gives_the_same_list_as_the_plugin()
    {
        using var s = new Set();
        var zip = Path.Combine(s.Sub("zips"), "Mod.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntryFromFile(s.Mod, "Data/Mod.esm");
        var (c1, o1, e1) = s.Text(s.Mod);
        var (c2, o2, e2) = s.Text(zip);
        Assert.True(c1 == 0, o1 + e1);
        Assert.True(c2 == 0, o2 + e2);
        var fromPlugin = Listing(o1);
        var fromZip = Listing(o2);
        Assert.Equal("Mod.esm (9 strings in 5 records)", fromPlugin[0]);
        Assert.Equal("Mod.esm (9 strings in 5 records, from Mod.zip)", fromZip[0]);
        Assert.Equal(fromPlugin.Skip(1), fromZip.Skip(1));
        Assert.Contains("RESULT: text: 9 strings in 5 records (4 new, 1 changed) in Mod.esm", o2);
    }

    [Fact]
    public void Test7_a_control_character_and_a_line_break_are_printed_safely()
    {
        using var s = new Set(m =>
        {
            var b = m.Books.AddNew("M_Note");
            b.Name = "Note";
            b.Text = "First line\nSecond\u001b[2Jline\r\nThird\u0007line";
        });
        var (code, stdout, stderr) = s.Text(s.Mod, "--type", "BOOK");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain('\u001b', stdout);
        Assert.DoesNotContain('\u0007', stdout);
        var l = Lines(stdout);
        var at = l.FindIndex(x => x == "      Text: First line");
        Assert.True(at >= 0, stdout);
        Assert.Equal("        Second\\u001b[2Jline", l[at + 1]);
        Assert.Equal("        Third\\u0007line", l[at + 2]);
    }

    [Fact]
    public void Test8_a_plugin_with_no_strings_prints_0_and_exits_0()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var dir = s.Sub("mods");
        using (var g = new GameData(data)) PluginWriter.Write(Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game"), data, g, NoGap);
        var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up mod with only keywords");
        m.Keywords.AddNew("K_One");
        m.Keywords.AddNew("K_Two");
        using (var g = new GameData(data, [dir])) PluginWriter.Write(m, dir, g, NoGap);
        var (code, stdout, stderr) = ExeTests.RunSplit("text", Path.Combine(dir, "Mod.esm"), "--data", data);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("Mod.esm (0 strings in 0 records)", Lines(stdout));
        Assert.Contains("RESULT: text: 0 strings in 0 records (0 new, 0 changed) in Mod.esm", stdout);
        Assert.DoesNotContain("WARNING", stdout);
    }
}
