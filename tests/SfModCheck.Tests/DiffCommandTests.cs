// The diff command (0.11.0 relay): step 1's frame (arguments, input checks, --out guards, plugin pairing, the exe's
// exit codes) and step 2's header compare and record index, on small plugins built by the tests.
using System.IO.Compression;
using System.Text.Json.Nodes;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class DiffCommandTests
{
    // ---- arguments ----

    [Fact]
    public void Parse_takes_two_inputs_and_every_option()
    {
        var p = DiffCommand.Parse(["old.esm", "new.esm", "--data", @"D:\Data", "--json", "--out", "r.json", "--verbose", "--log", "l.txt", "--quiet", "--all"]);
        Assert.Null(p.Error);
        var o = p.Options!;
        Assert.Equal("old.esm", o.Old);
        Assert.Equal("new.esm", o.New);
        Assert.Equal(@"D:\Data", o.DataDir);
        Assert.True(o.DataGiven);
        Assert.True(o.Json);
        Assert.Equal("r.json", o.Out);
        Assert.True(o.Verbose);
        Assert.Equal("l.txt", o.Log);
        Assert.True(o.Quiet);
        Assert.True(o.All);

        // options may come first, and without --data the default is Steam's usual folder
        var q = DiffCommand.Parse(["--json", "a.zip", "b.zip"]).Options!;
        Assert.Equal(("a.zip", "b.zip"), (q.Old, q.New));
        Assert.Equal(Cli.SteamData, q.DataDir);
        Assert.False(q.DataGiven);
    }

    [Theory]
    [InlineData(new string[0], "give the old and the new version")]
    [InlineData(new[] { "only.esm" }, "only one input (only.esm)")]
    [InlineData(new[] { "a.esm", "b.esm", "c.esm" }, "not 3")]
    [InlineData(new[] { "a.esm", "b.esm", "--frobnicate" }, "unknown option --frobnicate")]
    [InlineData(new[] { "a.esm", "b.esm", "--out" }, "--out needs a file name for the report after it")]
    [InlineData(new[] { "a.esm", "b.esm", "--data", "" }, "(it was given an empty value)")]
    [InlineData(new[] { "a.esm", "--log", "--json", "b.esm" }, "--log needs a file name")]
    [InlineData(new[] { "a.esm", " " }, "an input is empty")]
    public void Parse_errors_name_the_problem(string[] args, string fragment)
    {
        var p = DiffCommand.Parse(args);
        Assert.Null(p.Options);
        Assert.Contains(fragment, p.Error);
    }

    [Fact]
    public void Help_is_its_own_answer()
    {
        Assert.True(DiffCommand.Parse(["--help"]).Help);
        Assert.True(DiffCommand.Parse(["a.esm", "-h"]).Help);
    }

    // ---- inputs and --out ----

    [Fact]
    public void Inputs_must_exist_and_be_the_same_kind()
    {
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "A.esm"); File.WriteAllText(a, "x");
        var folder = s.Sub("mod");
        var txt = Path.Combine(s.Dir, "notes.txt"); File.WriteAllText(txt, "x");

        string? Check(string o, string n, string? outFile = null) =>
            DiffCommand.CheckInputs(new DiffOptions { Old = o, New = n, Out = outFile }, out _, out _);

        Assert.Null(Check(a, a));
        Assert.Null(Check(folder, folder));
        Assert.Contains("doesn't exist", Check(a, Path.Combine(s.Dir, "NoSuch.esm")));
        Assert.Contains("the new version", Check(a, Path.Combine(s.Dir, "NoSuch.esm")));
        Assert.Contains("give a plugin (.esm .esp .esl), a .zip", Check(txt, a));
        Assert.Contains("can't compare a plugin with a whole mod", Check(a, folder));
        Assert.Contains("--data", DiffCommand.CheckInputs(new DiffOptions { Old = a, New = a, DataDir = Path.Combine(s.Dir, "nodata"), DataGiven = true }, out _, out _));

        DiffCommand.CheckInputs(new DiffOptions { Old = folder, New = folder }, out var ok, out var nk);
        Assert.Equal((TriageInputKind.Folder, TriageInputKind.Folder), (ok, nk));
    }

    [Fact]
    public void Out_never_lands_on_an_input_or_a_mod_file()
    {
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "A.esm"); File.WriteAllText(a, "x");
        var folder = s.Sub("mod");
        Assert.Null(DiffCommand.OutProblem(Path.Combine(s.Dir, "report.txt"), a, a));
        Assert.Contains("plugin, archive or script", DiffCommand.OutProblem(Path.Combine(s.Dir, "A.ESM"), a, a));   // the input itself
        Assert.Contains("plugin, archive or script", DiffCommand.OutProblem(Path.Combine(s.Dir, "B.esm"), a, a));
        Assert.Contains("plugin, archive or script", DiffCommand.OutProblem(Path.Combine(s.Dir, "x.zip"), a, a));
        Assert.Contains("inside the input folder", DiffCommand.OutProblem(Path.Combine(folder, "report.txt"), folder, a));
        Assert.Contains("that is a folder", DiffCommand.OutProblem(folder, a, a));
        Assert.Contains("doesn't exist", DiffCommand.OutProblem(Path.Combine(s.Dir, "nofolder", "r.txt"), a, a));
    }

    [LinksFact]
    public void Out_and_log_through_a_link_into_an_input_folder_are_refused()
    {
        // code-review medium rounds 2 and 3 (0.11.0): a junction or link to the input folder got past the guard
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "A.esm"); File.WriteAllText(a, "x");
        var folder = s.Sub("mod");
        var link = Path.Combine(s.Dir, "link");
        Assert.True(FollowUps091Tests.TryLink(link, folder, folder: true));
        Assert.Equal(DiffCommand.RealPath(folder), DiffCommand.RealPath(link));
        Assert.Contains("inside the input folder", DiffCommand.OutProblem(Path.Combine(link, "readme.txt"), folder, a));
        Assert.Contains("inside the input folder", DiffCommand.OutProblem(Path.Combine(link, "readme.txt"), folder, a, "--log"));
        Assert.Null(DiffCommand.OutProblem(Path.Combine(s.Dir, "report.txt"), folder, a));

        // the same file reached once directly and once through a link (outside the inputs)
        var other = s.Sub("other");
        var otherLink = Path.Combine(s.Dir, "otherlink");
        Assert.True(FollowUps091Tests.TryLink(otherLink, other, folder: true));
        Assert.Contains("both --out and --log", DiffCommand.LogProblem(new DiffOptions
            { Old = a, New = a, Out = Path.Combine(other, "r.txt"), Log = Path.Combine(otherLink, "R.txt") }));

        // a link whose target holds another link: both are followed
        var hop = s.Sub("hop");
        Assert.True(FollowUps091Tests.TryLink(Path.Combine(hop, "inner"), s.Dir, folder: true));
        var outer = Path.Combine(s.Dir, "outer");
        Assert.True(FollowUps091Tests.TryLink(outer, Path.Combine(hop, "inner", "mod"), folder: true));
        Assert.Contains("inside the input folder", DiffCommand.OutProblem(Path.Combine(outer, "r.txt"), folder, a));
    }

    [UnixFact]
    public void On_linux_dot_dot_after_a_link_goes_up_from_the_real_folder()
    {
        // code-review medium round 3 (0.11.0): /tmp/l -> <input>/sub; /tmp/l/../r.txt is <input>/r.txt to the kernel
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "A.esm"); File.WriteAllText(a, "x");
        var folder = s.Sub("mod");
        var sub = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
        var link = Path.Combine(s.Dir, "l");
        Directory.CreateSymbolicLink(link, sub);
        Assert.Contains("inside the input folder", DiffCommand.OutProblem(link + "/../r.txt", folder, a));
    }

    [Fact]
    public void The_input_checks_ignore_case_on_every_system()
    {
        // code-review medium round 3 (0.11.0): case-blind folders (WSL /mnt/c, NTFS, exFAT) exist on Linux too
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "A.esm"); File.WriteAllText(a, "x");
        var folder = s.Sub("ModA");
        Directory.CreateDirectory(Path.Combine(s.Dir, "moda"));   // on Linux a second folder; on Windows the same one
        Assert.Contains("inside the input folder", DiffCommand.OutProblem(Path.Combine(s.Dir, "moda", "r.txt"), folder, a));
    }

    // ---- pairing ----

    [Fact]
    public void Plugins_pair_by_file_name_and_the_rest_are_added_or_removed()
    {
        var p = DiffCommand.PairPlugins(
            ["data\\mod.esm", "data\\mod - main.ba2", "old.esp", "readme.txt"],
            ["mod.esm", "extra.esm", "scripts\\a.pex"]);
        Assert.Null(p.Error);
        var pair = Assert.Single(p.Paired);
        Assert.Equal(("mod.esm", "data\\mod.esm", "mod.esm"), (pair.Name, pair.OldRel, pair.NewRel));
        Assert.Equal("old.esp", Assert.Single(p.Removed).Name);
        Assert.Equal("extra.esm", Assert.Single(p.Added).Name);
    }

    [Fact]
    public void Two_plugins_with_one_name_in_one_input_stop_the_pairing()
    {
        var p = DiffCommand.PairPlugins(["a\\mod.esm", "b\\mod.esm"], ["mod.esm"]);
        Assert.Contains("the old version holds two plugins named mod.esm", p.Error);
        Assert.Contains("sf-mod-check diff <old plugin> <new plugin>", p.Error);
    }

    [Fact]
    public void Two_plugins_given_directly_pair_whatever_their_names()
    {
        Assert.Equal("mymod.esm", Assert.Single(DiffCommand.PairTwoPlugins("MyMod.esm", "mymod.esm").Paired).Name);
        Assert.Equal("MyMod-1.0.esm -> MyMod.esm", Assert.Single(DiffCommand.PairTwoPlugins("MyMod-1.0.esm", "MyMod.esm").Paired).Name);
    }

    // ---- docs ----

    [Fact]
    public void Every_diff_option_is_in_the_usage_readme_agents_and_changelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "DiffCommand.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\DiffCommand.cs not found above " + AppContext.BaseDirectory);
        var options = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(dir!.FullName, "src", "DiffCommand.cs")), @"case ""(--[a-z-]+)"":")
            .Select(m => m.Groups[1].Value).Where(o => o != "--help").ToHashSet();
        Assert.Equal(new[] { "--all", "--data", "--json", "--log", "--out", "--quiet", "--verbose" }, options.Order());
        var readme = File.ReadAllText(Path.Combine(dir.FullName, "README.md"));
        var agents = File.ReadAllText(Path.Combine(dir.FullName, "AGENTS.md"));
        var changelog = File.ReadAllText(Path.Combine(dir.FullName, "CHANGELOG.md"));
        Assert.Contains("## Compare two versions of a mod: `sf-mod-check diff`", readme);
        Assert.Contains("sf-mod-check diff ", agents);
        foreach (var o in options)
        {
            Assert.Contains(o, DiffCommand.Usage);
            Assert.True(readme.Contains("`" + o), $"README.md doesn't mention diff's {o}");
            Assert.True(agents.Contains("`" + o) || agents.Contains(" " + o + " "), $"AGENTS.md doesn't mention diff's {o}");
            Assert.True(changelog.Contains("`" + o + "`"), $"CHANGELOG.md doesn't mention diff's {o}");
        }
        // step 3: noise is explained everywhere a user or a model reads the result
        foreach (var (name, text) in new[] { ("usage", DiffCommand.Usage), ("README.md", readme), ("AGENTS.md", agents), ("CHANGELOG.md", changelog) })
            Assert.True(text.Contains("noise"), $"{name} doesn't explain diff's noise");
        Assert.Contains("`noise`", agents);   // the JSON count
        // step 4: the detail views and their JSON names
        foreach (var (name, text) in new[] { ("usage", DiffCommand.Usage), ("README.md", readme), ("AGENTS.md", agents), ("CHANGELOG.md", changelog) })
            Assert.True(text.Contains("cell") && text.Contains("leveled") && text.Contains("base-object swap"), $"{name} doesn't describe diff's detail views");
        foreach (var key in new[] { "`cells`", "`leveled_lists`", "`base_swaps`", "`moved_out`", "`editor_ids_missing`", "`editor_ids_damaged`" }) Assert.Contains(key, agents);
        // step 5: files, archives and scripts
        foreach (var (name, text) in new[] { ("usage", DiffCommand.Usage), ("README.md", readme), ("AGENTS.md", agents), ("CHANGELOG.md", changelog) })
            Assert.True(text.Contains("loose files") && text.Contains("archive") && text.Contains("script"), $"{name} doesn't describe diff's file compare");
        foreach (var key in new[] { "`files`", "`archives`", "`scripts`", "`functions_changed`", "`code_same`" }) Assert.Contains(key, agents);
    }

    // ---- step 2: header compare and record index, on plugins built here ----

    /// <summary>Writes a plugin built by fill into folder sub (masters looked up in mastersDir) and returns its path.</summary>
    internal static string Build(Sandbox s, string sub, string name, PluginStyle style, Action<StarfieldMod, GameData> fill, string? mastersDir = null,
        IEnumerable<ModKey>? masterOrder = null)
    {
        var mod = Plugin.New(name, style, "test", "diff test");
        var dir = s.Sub(sub);
        using var g = new GameData(s.Sub("data"), mastersDir == null ? [] : [mastersDir]);
        fill(mod, g);
        PluginWriter.Write(mod, dir, g, new WriteOptions { GapCheck = false, MasterOrder = masterOrder });
        return Path.Combine(dir, name);
    }

    static MiscItem Misc(StarfieldMod mod, uint id, string editorId)
    {
        var m = mod.MiscItems.AddNew(new FormKey(mod.ModKey, id));
        m.EditorID = editorId;
        return m;
    }

    static PluginDiff Compare(string a, string b, string? mastersDir = null)
    {
        Func<string, RawHeader?> look = n => mastersDir != null && File.Exists(Path.Combine(mastersDir, n)) ? RawHeader.Read(Path.Combine(mastersDir, n)) : null;
        var oi = RecordIndex.Build(a, look, out var e1); Assert.Null(e1);
        var ni = RecordIndex.Build(b, look, out var e2); Assert.Null(e2);
        return PluginDiff.Compare(oi!, ni!);
    }

    [Fact]
    public void Records_added_removed_changed_and_renumbered_are_counted()
    {
        using var s = new Sandbox();
        var a = Build(s, "v1", "Mod.esm", PluginStyle.Full, (mod, _) =>
        {
            var kw = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x800)); kw.EditorID = "MOD_Keyword";
            Misc(mod, 0x801, "MOD_Changes");
            Misc(mod, 0x802, "MOD_Gone");
            Misc(mod, 0x810, "MOD_Moves");
        });
        var b = Build(s, "v2", "Mod.esm", PluginStyle.Full, (mod, _) =>
        {
            var kw = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x800)); kw.EditorID = "MOD_Keyword";
            Misc(mod, 0x801, "MOD_Changes").Keywords = [kw.ToLink<IKeywordGetter>()];
            Misc(mod, 0x803, "MOD_New");
            Misc(mod, 0x820, "MOD_Moves");
        });

        var d = Compare(a, b);
        Assert.True(d.Differences);
        var misc = d.Types["MISC"].Own;
        Assert.Equal((1, 1, 1, 1, 0), (misc.Added, misc.Removed, misc.Changed, misc.Renumbered, misc.Same));
        Assert.Equal(1, d.Types["KYWD"].Own.Same);
        Assert.False(d.Types["KYWD"].Own.Any);
        Assert.Equal("MOD_New", Assert.Single(d.Added).Rec.EditorId);
        Assert.Equal("MOD_Gone", Assert.Single(d.Removed).Rec.EditorId);
        Assert.Equal(0x801u, Assert.Single(d.Changed).Key.Local);
        var r = Assert.Single(d.Renumbered);
        Assert.Equal((0x810u, 0x820u, "MOD_Moves"), (r.OldKey.Local, r.NewKey.Local, r.New.EditorId));
        Assert.Equal(0, d.Total(false).Same + d.Total(false).Changed);   // no masters, so no overrides

        // the same plugin against itself: nothing differs
        var same = Compare(a, a);
        Assert.False(same.Differences);
        Assert.Equal(4, same.Total(true).Same);
    }

    /// <summary>Light.esm (light) and Big.esm (full) masters, each with one keyword, in folder "masters".</summary>
    static string Masters(Sandbox s)
    {
        Build(s, "masters", "Light.esm", PluginStyle.Light, (mod, _) => { var k = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x800)); k.EditorID = "LIGHT_Keyword"; });
        Build(s, "masters", "Big.esm", PluginStyle.Full, (mod, _) => { var k = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x900)); k.EditorID = "BIG_Keyword"; });
        return Path.Combine(s.Dir, "masters");
    }

    static string WithOverrides(Sandbox s, string sub, string mastersDir, string lightEditorId, IEnumerable<ModKey>? order = null) =>
        Build(s, sub, "Mod.esm", PluginStyle.Full, (mod, g) =>
        {
            mod.Keywords.GetOrAddAsOverride(g.Open("Light.esm").Keywords.First()).EditorID = lightEditorId;
            mod.Keywords.GetOrAddAsOverride(g.Open("Big.esm").Keywords.First());
            Misc(mod, 0x800, "MOD_Own");
        }, mastersDir, order);

    [Fact]
    public void Own_records_and_overrides_follow_the_master_list_of_their_style()
    {
        using var s = new Sandbox();
        var masters = Masters(s);
        var a = WithOverrides(s, "v1", masters, "LIGHT_Keyword");
        var b = WithOverrides(s, "v2", masters, "LIGHT_Keyword_Renamed");

        // Mod.esm has one full master (Big.esm), so its own records carry master index 1, which a plain reading of the
        // two-master list would take for the second master (the Community Spaceship Expansion case)
        Assert.Equal(2, RawHeader.Read(a)!.Masters.Count);
        var d = Compare(a, b, masters);
        Assert.Equal(1, d.Old.Records.Count(r => r.Key.Own));
        Assert.Equal(2, d.Old.Records.Count(r => !r.Key.Own));
        Assert.Contains(new RecKey("light.esm", 0x800), d.Old.Records.Keys);
        Assert.Contains(new RecKey("big.esm", 0x900), d.Old.Records.Keys);
        Assert.Empty(d.Old.UnknownMasters);
        var kw = d.Types["KYWD"].Overrides;
        Assert.Equal((1, 1), (kw.Changed, kw.Same));
        Assert.Equal(1, d.Types["MISC"].Own.Same);
        Assert.Equal(new RecKey("light.esm", 0x800), Assert.Single(d.Changed).Key);

        // without the masters' headers they are taken as full, and the report can say why own records look wrong
        var blind = Compare(a, b);
        Assert.Equal(2, blind.Old.UnknownMasters.Count);
        Assert.DoesNotContain(new RecKey("light.esm", 0x800), blind.Old.Records.Keys);   // its FE-style ID reads as the plugin's own
        Assert.DoesNotContain(new RecKey("big.esm", 0x800), d.Old.Records.Keys);
        Assert.Contains(blind.Old.Records.Keys, k => k.File is "light.esm" or "big.esm" && k.Local == 0x800);   // the own misc, read as a master's
    }

    [Fact]
    public void Reordered_masters_change_the_header_but_no_record()
    {
        using var s = new Sandbox();
        var masters = Masters(s);
        var lk = ModKey.FromFileName("Light.esm"); var bk = ModKey.FromFileName("Big.esm");
        var a = WithOverrides(s, "v1", masters, "LIGHT_Keyword", [lk, bk]);
        var b = WithOverrides(s, "v2", masters, "LIGHT_Keyword", [bk, lk]);
        Assert.NotEqual(RawHeader.Read(a)!.Masters, RawHeader.Read(b)!.Masters);
        var d = Compare(a, b, masters);
        Assert.Equal(0, d.Total(true).Changed + d.Total(false).Changed + d.Total(true).Added + d.Total(true).Removed);
        Assert.Contains(d.Header, h => h.Real && h.Text == "masters reordered: Light.esm, Big.esm -> Big.esm, Light.esm");
        Assert.True(d.Differences);
    }

    [Fact]
    public void Header_changes_say_which_are_real()
    {
        var a = new RawHeader { Flags = Flags.Master, FormVersion = 555, NumRecords = 10, Masters = ["Starfield.esm", "A.esm", "B.esm"] };
        var b = new RawHeader { Flags = Flags.Master | Flags.Light, FormVersion = 556, NumRecords = 11, Masters = ["starfield.esm", "B.esm", "C.esm"] };
        var c = PluginDiff.HeaderChanges(a, b);
        Assert.Equal(
            [("type: full -> light", true), ("master removed: A.esm", true), ("master added: C.esm", true),
             ("form version: 555 -> 556", false), ("HEDR record count: 10 -> 11", false)],
            c.Select(h => (h.Text, h.Real)));
        Assert.Empty(PluginDiff.HeaderChanges(a, a));
        var e = new RawHeader { Flags = Flags.Master | Flags.Localized, FormVersion = 555, NumRecords = 10, Masters = a.Masters };
        Assert.Equal("flags: 00000001 -> 00000081", Assert.Single(PluginDiff.HeaderChanges(a, e)).Text);
    }

    [Fact]
    public void EditorId_is_read_from_the_body_even_after_a_big_field()
    {
        static byte[] Field(string type, byte[] data) => [.. System.Text.Encoding.Latin1.GetBytes(type), .. BitConverter.GetBytes((ushort)data.Length), .. data];
        var plain = Field("EDID", [.. "Abc"u8, 0]);
        Assert.Equal("Abc", RecordIndex.EditorId(plain));
        // XXXX gives the next field a 32-bit size
        var big = new byte[70000];
        byte[] xxxx = [.. "XXXX"u8, 4, 0, .. BitConverter.GetBytes(big.Length), .. "DATA"u8, 0, 0, .. big];
        Assert.Equal("Abc", RecordIndex.EditorId([.. xxxx, .. plain]));
        Assert.Null(RecordIndex.EditorId(Field("DATA", [1, 2])));
        Assert.Null(RecordIndex.EditorId([.. "EDID"u8, 50, 0, 1]));   // runs past the end
    }

    // ---- step 3: the field compare and noise ----

    /// <summary>Mod.esm with one misc item and a leveled list holding it once; padding goes in the entry's padding
    /// members (LVLO bytes 2-3 and 11), count is the entry's count, keyword gives the misc item a real change.</summary>
    static string Lists(Sandbox s, string sub, short padding = 0, sbyte padding2 = 0, short count = 1, bool keyword = false) =>
        Build(s, sub, "Mod.esm", PluginStyle.Full, (mod, _) =>
        {
            var kw = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x800)); kw.EditorID = "MOD_Keyword";
            var misc = Misc(mod, 0x801, "MOD_Misc");
            if (keyword) misc.Keywords = [kw.ToLink<IKeywordGetter>()];
            var list = mod.LeveledItems.AddNew(new FormKey(mod.ModKey, 0x802));
            list.EditorID = "MOD_List";
            list.Entries = [new LeveledItemEntry { Level = 1, Unused = padding, Unused2 = padding2, Reference = misc.ToLink<IItemGetter>(), Count = count }];
        });

    static PluginDiff CompareFields(string a, string b)
    {
        var d = Compare(a, b);
        DiffFields.Apply(d);
        return d;
    }

    [Fact]
    public void Padding_only_differences_are_noise_and_field_changes_name_the_field()
    {
        using var s = new Sandbox();
        var plain = Lists(s, "plain");
        var padded = Lists(s, "padded", padding: 0x1234, padding2: 7);
        var counted = Lists(s, "counted", count: 3, keyword: true);
        Assert.False(File.ReadAllBytes(plain).AsSpan().SequenceEqual(File.ReadAllBytes(padded)));   // the bytes do differ

        var noise = CompareFields(plain, padded);
        Assert.False(noise.Differences);
        var lvli = noise.Types["LVLI"].Own;
        Assert.Equal((0, 1), (lvli.Changed, lvli.Noise));
        Assert.Empty(noise.Changed);
        Assert.Equal(0x802u, Assert.Single(noise.Noise).Key.Local);
        Assert.Null(noise.FieldsNote);

        var real = CompareFields(plain, counted);
        Assert.True(real.Differences);
        Assert.Equal(2, real.Changed.Count);
        Assert.Equal(["Entries"], real.Changed.Single(c => c.Old.Sig == "LVLI").Fields);
        Assert.Equal(["Keywords"], real.Changed.Single(c => c.Old.Sig == "MISC").Fields);
        Assert.All(real.Changed, c => Assert.Null(c.NotCompared));
        Assert.Equal(0, real.Total(true).Noise);
    }

    [Fact]
    public void Overrides_are_compared_by_fields_with_the_masters_styles()
    {
        using var s = new Sandbox();
        var masters = Masters(s);
        var a = WithOverrides(s, "v1", masters, "LIGHT_Keyword");
        var b = WithOverrides(s, "v2", masters, "LIGHT_Keyword_Renamed");
        Func<string, RawHeader?> look = n => File.Exists(DiffCommand.InFolder(masters, n)) ? RawHeader.Read(DiffCommand.InFolder(masters, n)) : null;
        var d = PluginDiff.Compare(RecordIndex.Build(a, look, out _)!, RecordIndex.Build(b, look, out _)!);
        DiffFields.Apply(d);
        var c = Assert.Single(d.Changed);
        Assert.Equal(new RecKey("light.esm", 0x800), c.Key);   // a light master's record, found by Mutagen under the same key
        Assert.Equal(["EditorID"], c.Fields);
    }

    [Fact]
    public void Padding_names_are_only_unused_and_a_number()
    {
        foreach (var n in new[] { "Unused", "Unused1", "Unused2", "Unused12" }) Assert.True(DeepCompare.IsPadding(n), n);
        foreach (var n in new[] { "UnusedConditions", "UnusedWorldspaceParent", "UnusedISET2", "Unknown", "Used", "unused" }) Assert.False(DeepCompare.IsPadding(n), n);
        // conflict's own rule sets still see padding
        Assert.False(DeepCompare.Conflict.IgnorePadding);
        Assert.Contains(DeepCompare.Conflict.Properties(typeof(LeveledItemEntry)), p => p.Name == "Unused");
        Assert.DoesNotContain(DeepCompare.DiffIgnoringPadding.Properties(typeof(LeveledItemEntry)), p => p.Name is "Unused" or "Unused2");
    }

    // ---- the exe ----

    static (int Code, string Stdout, string Stderr) Run(params string[] args) => ExeTests.RunSplit(args);

    static string Simple(Sandbox s, string sub, string editorId) =>
        Build(s, sub, "Mod.esm", PluginStyle.Light, (mod, _) => Misc(mod, 0x800, editorId));

    [Fact]
    public void Exe_same_plugin_exits_0_and_changed_exits_1()
    {
        using var s = new Sandbox();
        var a = Simple(s, "a", "MOD_Misc");
        var b = Simple(s, "b", "MOD_Misc");
        var c = Simple(s, "c", "MOD_Misc_Changed");

        var same = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(0, same.Code);
        Assert.Equal("RESULT: NO DIFFERENCES (1 plugin compared, 0 changed, 0 added, 0 removed; records: 0 added, 0 removed, 0 changed, 0 renumbered, 0 noise)",
            ExeTests.ResultLine(same.Stdout));
        Assert.Contains("own records: -; 1 the same", same.Stdout);

        var changed = Run("diff", a, c, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, changed.Code);
        Assert.Equal("RESULT: DIFFERENCES (1 plugin compared, 1 changed, 0 added, 0 removed; records: 0 added, 0 removed, 1 changed, 0 renumbered, 0 noise)",
            ExeTests.ResultLine(changed.Stdout));
        Assert.Contains("MISC  own: 1 changed  |  overrides: -", changed.Stdout);
    }

    [Fact]
    public void Exe_names_renumbered_records_in_text_and_json()
    {
        using var s = new Sandbox();
        var a = Build(s, "a", "Mod.esm", PluginStyle.Light, (mod, _) => Misc(mod, 0x800, "MOD_Handler"));
        var b = Build(s, "b", "Mod.esm", PluginStyle.Light, (mod, _) => Misc(mod, 0x801, "MOD_Handler"));

        var text = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, text.Code);
        Assert.Contains("renumbered records (1; same type and EditorID, new FormID):", text.Stdout);
        Assert.Contains("MISC MOD_Handler: 000800:Mod.esm -> 000801:Mod.esm", text.Stdout);

        var json = Run("diff", a, b, "--quiet", "--json", "--data", s.Sub("nodata"));
        var r = Assert.Single(JsonNode.Parse(json.Stdout[..json.Stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)])!["plugins"]![0]!["renumbered"]!.AsArray())!;
        Assert.Equal(("MISC", "MOD_Handler", "000800:Mod.esm", "000801:Mod.esm"),
            ((string)r["type"]!, (string)r["editor_id"]!, (string)r["old_key"]!, (string)r["new_key"]!));
    }

    [Fact]
    public void Exe_noise_only_exits_0_and_json_lists_the_changed_fields()
    {
        using var s = new Sandbox();
        var plain = Lists(s, "plain");
        var padded = Lists(s, "padded", padding: 0x1234);
        var counted = Lists(s, "counted", count: 3);

        var noise = Run("diff", plain, padded, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(0, noise.Code);
        Assert.Equal("RESULT: NO DIFFERENCES (1 plugin compared, 0 changed, 0 added, 0 removed; records: 0 added, 0 removed, 0 changed, 0 renumbered, 1 noise)",
            ExeTests.ResultLine(noise.Stdout));
        Assert.Contains("LVLI  own: 1 noise  |  overrides: -", noise.Stdout);
        Assert.DoesNotContain("changed records", noise.Stdout);

        var real = Run("diff", plain, counted, "--quiet", "--json", "--data", s.Sub("nodata"));
        Assert.Equal(1, real.Code);
        using var doc = System.Text.Json.JsonDocument.Parse(real.Stdout[..real.Stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)]);
        var p = doc.RootElement.GetProperty("plugins")[0];
        var c = Assert.Single(p.GetProperty("changed").EnumerateArray());
        Assert.Equal(("LVLI", "MOD_List", false), (c.GetProperty("type").GetString(), c.GetProperty("editor_id").GetString(), c.GetProperty("override").GetBoolean()));
        Assert.Equal(["Entries"], c.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, p.GetProperty("records").GetProperty("own").GetProperty("noise").GetInt32());
    }

    [Fact]
    public void Exe_a_paired_file_that_is_no_plugin_exits_2()
    {
        using var s = new Sandbox();
        var a = Simple(s, "a", "MOD_Misc");
        var bad = Path.Combine(s.Sub("b"), "Mod.esm"); File.WriteAllText(bad, "not a plugin");
        var r = Run("diff", a, bad, "--quiet");
        Assert.Equal(2, r.Code);
        Assert.Equal("RESULT: CANNOT DIFF (Mod.esm can't be read)", ExeTests.ResultLine(r.Stdout));
        Assert.Contains("is not a plugin: it doesn't start with a TES4 header", r.Stderr);
    }

    [Fact]
    public void Exe_pairs_a_zip_with_a_folder_and_writes_json_to_out()
    {
        using var s = new Sandbox();
        var plugin = File.ReadAllBytes(Simple(s, "built", "MOD_Misc"));
        var zip = Path.Combine(s.Dir, "Mod-1.0.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in new[] { ("Data/Mod.esm", plugin), ("Data/Gone.esp", "x"u8.ToArray()) })
                using (var w = z.CreateEntry(name).Open()) w.Write(data);
        }
        var folder = s.Sub("Mod-1.1");
        File.WriteAllBytes(Path.Combine(folder, "Mod.esm"), plugin);
        File.WriteAllText(Path.Combine(folder, "New.esm"), "y");   // only in one version: not read
        var outFile = Path.Combine(s.Dir, "diff.json");
        var log = Path.Combine(s.Dir, "diff-log.txt");

        var r = Run("diff", zip, folder, "--json", "--out", outFile, "--log", log, "--quiet");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("RESULT: DIFFERENCES (1 plugin compared, 0 changed, 1 added, 1 removed; records: 0 added, 0 removed, 0 changed, 0 renumbered, 0 noise; files: 0 added, 0 removed, 0 changed)",
            ExeTests.ResultLine(r.Stdout));
        Assert.DoesNotContain("\"schema\"", r.Stdout);   // the report went to --out
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(outFile));
        Assert.Equal(DiffCommand.JsonSchema, doc.RootElement.GetProperty("schema").GetInt32());
        var statuses = doc.RootElement.GetProperty("plugins").EnumerateArray()
            .Select(p => (p.GetProperty("name").GetString(), p.GetProperty("status").GetString())).ToList();
        Assert.Equal([("Mod.esm", "same"), ("Gone.esp", "removed"), ("New.esm", "added")], statuses);   // own spellings
        var mod = doc.RootElement.GetProperty("plugins")[0];
        Assert.Equal("light", mod.GetProperty("header").GetProperty("old").GetProperty("type").GetString());
        Assert.Equal(0, mod.GetProperty("header").GetProperty("changes").GetArrayLength());
        Assert.Equal(1, mod.GetProperty("records").GetProperty("own").GetProperty("same").GetInt32());
        var misc = mod.GetProperty("types")[0];
        Assert.Equal(("MISC", 1, 0), (misc.GetProperty("type").GetString(), misc.GetProperty("own").GetProperty("same").GetInt32(), misc.GetProperty("own").GetProperty("changed").GetInt32()));
        var logText = File.ReadAllText(log);
        Assert.Contains("paired: data\\mod.esm with mod.esm", logText);
        Assert.Contains("only in the old version: data\\gone.esp", logText);
        // read-only: nothing new beside the inputs, and the temp folder is gone
        Assert.Equal(["mod.esm", "new.esm"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f).ToLowerInvariant()).Order());
    }

    [Fact]
    public void Exe_bad_input_exits_2_with_the_fix()
    {
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "A.esm"); File.WriteAllText(a, "x");
        var r = Run("diff", a, Path.Combine(s.Dir, "NoSuch.esm"), "--quiet");
        Assert.Equal(2, r.Code);
        Assert.Equal("RESULT: CANNOT DIFF (bad input; see the message above)", ExeTests.ResultLine(r.Stdout));
        Assert.Contains("the new version", r.Stderr);

        var args = Run("diff", a);
        Assert.Equal(2, args.Code);
        Assert.Contains("only one input", args.Stderr);
        Assert.Contains("usage: sf-mod-check diff <old> <new>", args.Stderr);

        var help = Run("diff", "--help");
        Assert.Equal(2, help.Code);
        foreach (var o in new[] { "--data", "--json", "--out", "--quiet", "--verbose", "--log", "--all" }) Assert.Contains(o, help.Stderr);
    }

    // ---- step 4: the detail views ----

    static PlacedObject Ref(StarfieldMod mod, uint id, IFormLinkGetter<IPlaceableObjectGetter> baseObj) =>
        new(new FormKey(mod.ModKey, id), StarfieldRelease.Starfield) { Base = baseObj.FormKey.ToLink<IPlaceableObjectGetter>() };

    /// <summary>Mod.esm with misc items MOD_MiscA (801) and MOD_MiscB (802) and the cells MOD_CellA (810) and MOD_CellB
    /// (811); place puts the placed objects in them (g opens the masters in mastersDir).</summary>
    static string Placed(Sandbox s, string sub, Action<StarfieldMod, GameData, MiscItem, MiscItem, Cell, Cell> place, string? mastersDir = null) =>
        Build(s, sub, "Mod.esm", PluginStyle.Full, (mod, g) =>
        {
            var a = Misc(mod, 0x801, "MOD_MiscA"); var b = Misc(mod, 0x802, "MOD_MiscB");
            var ca = new Cell(new FormKey(mod.ModKey, 0x810), StarfieldRelease.Starfield) { EditorID = "MOD_CellA" };
            var cb = new Cell(new FormKey(mod.ModKey, 0x811), StarfieldRelease.Starfield) { EditorID = "MOD_CellB" };
            place(mod, g, a, b, ca, cb);
            mod.Cells.Add(new CellBlock { SubBlocks = new Noggog.ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new Noggog.ExtendedList<Cell> { ca, cb } } } });
        }, mastersDir);

    static string MovedAndSwapped(Sandbox s, string sub, bool v2) =>
        Placed(s, sub, (mod, _, a, b, ca, cb) =>
        {
            var la = a.ToLink<IPlaceableObjectGetter>(); var lb = b.ToLink<IPlaceableObjectGetter>();
            if (!v2)
            {
                ca.Temporary.Add(Ref(mod, 0x820, la));   // moves to CellB in v2
                ca.Temporary.Add(Ref(mod, 0x821, la));   // its base becomes MOD_MiscB in v2
                cb.Temporary.Add(Ref(mod, 0x822, la));   // gone in v2
            }
            else
            {
                ca.Temporary.Add(Ref(mod, 0x821, lb));
                ca.Temporary.Add(Ref(mod, 0x823, la));   // new in v2
                cb.Temporary.Add(Ref(mod, 0x820, la));
            }
        });

    [Fact]
    public void Placed_objects_are_grouped_by_cell_with_moves_and_base_swaps()
    {
        using var s = new Sandbox();
        var a = MovedAndSwapped(s, "v1", false);
        var b = MovedAndSwapped(s, "v2", true);
        var d = CompareFields(a, b);
        Assert.Equal(new RecKey("", 0x810), d.Old.Records[new RecKey("", 0x820)].Cell);
        Assert.Equal(new RecKey("", 0x811), d.New.Records[new RecKey("", 0x820)].Cell);
        Assert.Null(d.New.Records[new RecKey("", 0x801)].Cell);   // a misc item isn't in a cell

        var moved = d.Changed.Single(c => c.Key.Local == 0x820);
        Assert.True(moved.Moved);
        Assert.Contains(DiffFields.ParentCell, moved.Fields);
        var swapped = d.Changed.Single(c => c.Key.Local == 0x821);
        Assert.Equal(["Base"], swapped.Fields);
        Assert.Equal((new RecKey("", 0x801), new RecKey("", 0x802)), swapped.BaseSwap!.Value);

        var x = DiffDetails.Build(d);
        x.Names = DiffNames.Resolve(d, x.Keys(), _ => null, _ => null);
        Assert.Equal(2, x.Cells.Count);
        var cellA = x.Cells.Single(c => c.Cell.Local == 0x810);
        var cellB = x.Cells.Single(c => c.Cell.Local == 0x811);
        Assert.Equal("+REFR 1, ~REFR 1, >REFR 1", cellA.Summary());   // 823 added, 821 changed, 820 moved out
        Assert.Equal("-REFR 1, ~REFR 1", cellB.Summary());             // 822 removed, 820 moved in
        Assert.Equal(new RecKey("", 0x810), cellB.Records.Single(r => r.Change == "changed").From);
        Assert.Equal("MOD_CellA", x.Names.Of(cellA.Cell));
        Assert.Equal("MOD_MiscB", x.Names.Of(new RecKey("", 0x802)));
        Assert.Empty(x.Names.MissingMasters);
        Assert.Equal(0x821u, Assert.Single(x.BaseSwaps).Key.Local);
        Assert.Empty(x.LeveledLists);

        var sb = new System.Text.StringBuilder();
        x.Text(sb, "Mod.esm", all: true);
        var text = sb.ToString();
        Assert.Contains("    000810:Mod.esm MOD_CellA: +REFR 1, ~REFR 1, >REFR 1", text);
        Assert.Contains("      moved out REFR 000820:Mod.esm (to 000811:Mod.esm MOD_CellB)", text);
        Assert.Contains("      changed REFR 000820:Mod.esm (moved from 000810:Mod.esm MOD_CellA)", text);
        Assert.Contains("REFR 000821:Mod.esm in 000810:Mod.esm MOD_CellA: 000801:Mod.esm MOD_MiscA -> 000802:Mod.esm MOD_MiscB", text);
    }

    /// <summary>Mod.esm with MOD_MiscA (801), MOD_MiscB (802) and the leveled list MOD_List (803) holding the entries
    /// (level, 'A' or 'B', count).</summary>
    static string ListOf(Sandbox s, string sub, params (short Level, char Item, short Count)[] entries) =>
        Build(s, sub, "Mod.esm", PluginStyle.Full, (mod, _) =>
        {
            var a = Misc(mod, 0x801, "MOD_MiscA"); var b = Misc(mod, 0x802, "MOD_MiscB");
            var list = mod.LeveledItems.AddNew(new FormKey(mod.ModKey, 0x803));
            list.EditorID = "MOD_List";
            list.Entries = [.. entries.Select(e => new LeveledItemEntry
                { Level = e.Level, Reference = (e.Item == 'A' ? a : b).ToLink<IItemGetter>(), Count = e.Count })];
        });

    [Fact]
    public void Leveled_list_entries_added_and_removed_are_listed()
    {
        using var s = new Sandbox();
        var a = ListOf(s, "v1", (1, 'A', 1), (1, 'A', 1), (5, 'B', 2));
        var b = ListOf(s, "v2", (1, 'A', 1), (5, 'B', 3), (2, 'B', 1));
        var d = CompareFields(a, b);
        var l = Assert.Single(d.Changed);
        Assert.Equal(["Entries"], l.Fields);
        LeveledEntry E(int level, uint item, int count) => new(level, new RecKey("", item), count);
        Assert.Equal([E(5, 0x802, 3), E(2, 0x802, 1)], l.EntriesAdded);
        Assert.Equal([E(1, 0x801, 1), E(5, 0x802, 2)], l.EntriesRemoved);   // one of the two A entries went
        Assert.False(l.OtherEntryChanges);

        var x = DiffDetails.Build(d);
        x.Names = DiffNames.Resolve(d, x.Keys(), _ => null, _ => null);
        Assert.Same(l, Assert.Single(x.LeveledLists));
        Assert.Empty(x.Cells);
        var sb = new System.Text.StringBuilder();
        x.Text(sb, "Mod.esm", all: false);
        var text = sb.ToString();
        Assert.Contains("    LVLI 000803:Mod.esm MOD_List: 2 added, 2 removed", text);
        Assert.Contains("      + level 5, 000802:Mod.esm MOD_MiscB, count 3", text);
        Assert.Contains("      - level 1, 000801:Mod.esm MOD_MiscA, count 1", text);

        // the same entries by level, reference and count, in another order: still a change, said plainly
        var c = ListOf(s, "v3", (5, 'B', 2), (1, 'A', 1), (1, 'A', 1));
        var r = CompareFields(a, c);
        var o = Assert.Single(r.Changed);
        Assert.True(o.OtherEntryChanges);
        Assert.Empty(o.EntriesAdded!);
        Assert.Empty(o.EntriesRemoved!);
    }

    [Fact]
    public void Master_editor_ids_come_from_the_master_or_the_report_says_it_is_missing()
    {
        using var s = new Sandbox();
        Build(s, "masters", "Base.esm", PluginStyle.Full, (mod, _) => Misc(mod, 0x900, "BASE_Misc"));
        var masters = Path.Combine(s.Dir, "masters");
        string Version(string sub, bool master) => Placed(s, sub, (mod, g, a, _, ca, _) =>
            ca.Temporary.Add(Ref(mod, 0x820, master ? g.Open("Base.esm").MiscItems.First().ToLink<IPlaceableObjectGetter>() : a.ToLink<IPlaceableObjectGetter>())), masters);
        var v1 = Version("v1", false);
        var v2 = Version("v2", true);
        Func<string, RawHeader?> look = n => File.Exists(DiffCommand.InFolder(masters, n)) ? RawHeader.Read(DiffCommand.InFolder(masters, n)) : null;
        var d = PluginDiff.Compare(RecordIndex.Build(v1, look, out _)!, RecordIndex.Build(v2, look, out _)!);
        DiffFields.Apply(d);
        var x = DiffDetails.Build(d);
        var swap = Assert.Single(x.BaseSwaps).BaseSwap!.Value;
        Assert.Equal(new RecKey("base.esm", 0x900), swap.New);

        x.Names = DiffNames.Resolve(d, x.Keys(), n => File.Exists(DiffCommand.InFolder(masters, n)) ? DiffCommand.InFolder(masters, n) : null, look);
        Assert.Equal("BASE_Misc", x.Names.Of(swap.New));
        Assert.Equal("base.esm", Assert.Single(x.Names.Read).Master);

        x.Names = DiffNames.Resolve(d, x.Keys(), _ => null, _ => null);
        Assert.Null(x.Names.Of(swap.New));
        Assert.Equal(["base.esm"], x.Names.MissingMasters);
        var sb = new System.Text.StringBuilder();
        x.Text(sb, "Mod.esm", all: false);
        Assert.Contains("note: EditorIDs of records in base.esm aren't shown", sb.ToString());
        Assert.Contains("-> 000900:base.esm", sb.ToString());

        // code-review medium round 2 (0.11.0): a master that stops early. Cut inside its first group: the wanted record
        // is never reached, so the master is damaged (its own note), not missing.
        var good = File.ReadAllBytes(DiffCommand.InFolder(masters, "Base.esm"));
        var headerEnd = 24 + BitConverter.ToInt32(good, 4);
        var cutDir = s.Sub("cut");
        var cut = Path.Combine(cutDir, "Base.esm");
        File.WriteAllBytes(cut, good[..(headerEnd + 30)]);
        x.Names = DiffNames.Resolve(d, x.Keys(), _ => cut, look);
        Assert.Null(x.Names.Of(swap.New));
        Assert.Empty(x.Names.MissingMasters);
        Assert.Equal(["base.esm"], x.Names.DamagedMasters);
        sb.Clear();
        x.Text(sb, "Mod.esm", all: false);
        Assert.Contains("note: base.esm is damaged (the file stops early)", sb.ToString());
        Assert.DoesNotContain("Give --data", sb.ToString());
        var json = new System.Text.Json.Nodes.JsonObject();
        x.Json(json, "Mod.esm");
        Assert.Equal("base.esm", json["editor_ids_damaged"]![0]!.GetValue<string>());

        // damaged only after the wanted record: every name is found, so no warning
        var tail = Path.Combine(cutDir, "Tail.esm");
        File.WriteAllBytes(tail, [.. good, 0x47, 0x52, 0x55, 0x50, 0xff, 0xff, 0xff, 0x7f]);
        x.Names = DiffNames.Resolve(d, x.Keys(), _ => tail, look);
        Assert.Equal("BASE_Misc", x.Names.Of(swap.New));
        Assert.Empty(x.Names.DamagedMasters);
        Assert.Empty(x.Names.MissingMasters);
    }

    [Fact]
    public void A_compressed_record_that_wont_unpack_has_no_editor_id()
    {
        // code-review medium (0.11.0): the EditorID was read from the packed bytes
        using var s = new Sandbox();
        var path = Build(s, "p", "Mod.esm", PluginStyle.Full, (mod, _) => Misc(mod, 0x800, "MOD_Misc"));
        var ix = RecordIndex.Build(path, _ => null, out _)!;
        var rec = ix.Records.Values.Single(r => r.Sig == "MISC");
        Assert.Equal("MOD_Misc", rec.EditorId);
        // set the compressed flag on a record that isn't compressed: its body no longer unpacks
        var bytes = File.ReadAllBytes(path);
        var flags = BitConverter.ToUInt32(bytes, (int)rec.Offset + 8) | Flags.Compressed;
        BitConverter.GetBytes(flags).CopyTo(bytes, (int)rec.Offset + 8);
        var bad = Path.Combine(s.Sub("bad"), "Mod.esm");
        File.WriteAllBytes(bad, bytes);
        var ix2 = RecordIndex.Build(bad, _ => null, out _)!;
        Assert.Equal(1, ix2.Unpackable);
        Assert.Null(ix2.Records.Values.Single(r => r.Sig == "MISC").EditorId);
    }

    [Fact]
    public void Masters_are_found_by_their_lower_case_key_name_in_the_plugin_folder_and_the_data_folder()
    {
        // record keys hold lower-case master names; Linux and macOS folders are case-sensitive (Linux suite, step 8)
        using var s = new Sandbox();
        Build(s, "plugin", "Base.esm", PluginStyle.Full, (mod, _) => Misc(mod, 0x900, "BASE_Misc"));
        Build(s, "gamedata", "Other.esm", PluginStyle.Medium, (mod, _) => Misc(mod, 0x900, "OTHER_Misc"));
        var pluginDir = Path.Combine(s.Dir, "plugin"); var dataDir = Path.Combine(s.Dir, "gamedata");
        var input = new DiffInput { Path = Path.Combine(pluginDir, "Mod.esm"), Kind = TriageInputKind.Plugin, Root = pluginDir };

        var paths = DiffCommand.MasterPaths(input, dataDir);
        // Windows opens any spelling, so the plain path may come back there; on Linux only the real spelling exists
        Assert.True(File.Exists(paths("base.esm")));
        Assert.Equal("Base.esm", Path.GetFileName(paths("base.esm")), ignoreCase: true);
        Assert.Equal(pluginDir, Path.GetDirectoryName(paths("base.esm")));
        Assert.True(File.Exists(paths("other.esm")));
        Assert.Equal(dataDir, Path.GetDirectoryName(paths("other.esm")));
        Assert.Null(paths("missing.esm"));
        var headers = DiffCommand.MasterLookup(input, dataDir, "old");
        Assert.Equal(SfModCheck.Style.Medium, headers("other.esm")!.Style);
        Assert.Null(headers("missing.esm"));

        Assert.Equal(Path.Combine(dataDir, "missing.esm"), DiffCommand.InFolder(dataDir, "missing.esm"));
        Assert.Equal(Path.Combine(s.Dir, "nofolder", "base.esm"), DiffCommand.InFolder(Path.Combine(s.Dir, "nofolder"), "base.esm"));
    }

    [Fact]
    public void Exe_shows_the_detail_views_in_text_and_json()
    {
        using var s = new Sandbox();
        var a = MovedAndSwapped(s, "v1", false);
        var b = MovedAndSwapped(s, "v2", true);

        var text = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, text.Code);
        Assert.Contains("by cell (2 cells, 5 records; + added, - removed, ~ changed, > moved out):", text.Stdout);
        Assert.Contains("(--all lists each cell's records)", text.Stdout);
        Assert.DoesNotContain("moved out REFR", text.Stdout);
        Assert.Contains("base-object swaps (1; placed objects whose base object changed):", text.Stdout);

        var all = Run("diff", a, b, "--quiet", "--all", "--data", s.Sub("nodata"));
        Assert.Contains("      moved out REFR 000820:Mod.esm (to 000811:Mod.esm MOD_CellB)", all.Stdout);

        var json = Run("diff", a, b, "--quiet", "--json", "--data", s.Sub("nodata"));
        using var doc = System.Text.Json.JsonDocument.Parse(json.Stdout[..json.Stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)]);
        var p = doc.RootElement.GetProperty("plugins")[0];
        Assert.Equal(2, p.GetProperty("cells").GetArrayLength());
        var cellA = p.GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("editor_id").GetString() == "MOD_CellA");
        Assert.Equal(1, cellA.GetProperty("moved_out").GetProperty("REFR").GetInt32());
        Assert.Equal(3, cellA.GetProperty("records").GetArrayLength());
        var swap = Assert.Single(p.GetProperty("base_swaps").EnumerateArray());
        Assert.Equal(("MOD_MiscA", "MOD_MiscB"), (swap.GetProperty("old").GetProperty("editor_id").GetString(), swap.GetProperty("new").GetProperty("editor_id").GetString()));
        Assert.Equal(0, p.GetProperty("leveled_lists").GetArrayLength());
        Assert.Equal(0, p.GetProperty("editor_ids_missing").GetArrayLength());
    }
}
