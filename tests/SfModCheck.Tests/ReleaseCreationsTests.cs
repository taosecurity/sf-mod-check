// release --profile creations (0.10.0): Bethesda Creations' rules for PC, Xbox and PlayStation. Zips, archives, plugin
// headers and scripts are made up here byte by byte: no game files.
using System.IO.Compression;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseCreationsTests
{
    static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    static string MakeZip(Sandbox s, string name, params (string Path, byte[] Data)[] entries)
    {
        var zip = Path.Combine(s.Sub("zips"), name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, d) in entries)
        {
            using var w = z.CreateEntry(p).Open();
            w.Write(d);
        }
        return zip;
    }

    /// <summary>A BA2 whose name table holds these names (the records and data are filler; only names are read).</summary>
    static byte[] Ba2(string type, params string[] names)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(T("BTDX")); w.Write(1u); w.Write(T(type)); w.Write((uint)names.Length); w.Write(0UL);
        w.Write(new byte[36 * names.Length + 16]);
        var at = ms.Position;
        foreach (var n in names) { var b = T(n); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ulong)at).CopyTo(bytes, 16);
        return bytes;
    }

    static List<ReleaseFinding> Run(Sandbox s, string zip, string profile = "creations", params string[] allow)
    {
        var p = ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out", Path.GetFileNameWithoutExtension(zip) + "-" + profile));
        var o = new ReleaseOptions { Profile = profile };
        o.CreationsMasters.AddRange(allow);
        return ReleaseCreations.Run(new ReleaseContext { Options = o, Packages = [p], Work = s.Sub("work") }).ToList();
    }

    /// <summary>The zip rules (ReleaseRules) on the creations profile, which report unreadable archives.</summary>
    static List<ReleaseFinding> ZipRules(Sandbox s, string zip)
    {
        var p = ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out", Path.GetFileNameWithoutExtension(zip) + "-rules"));
        return ReleaseRules.Run(new ReleaseContext { Options = new ReleaseOptions { Profile = "creations" }, Packages = [p], Work = s.Sub("work-rules") }).ToList();
    }

    static string List(IEnumerable<ReleaseFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Rule}] {x.Text}"));

    static List<ReleaseFinding> Of(List<ReleaseFinding> f, string rule, Level level) => f.Where(x => x.Rule == rule && x.Level == level).ToList();

    [Fact]
    public void The_built_in_list_allows_Bethesdas_plugins_and_names_the_SFSE_frameworks()
    {
        Assert.True(ReleaseCreations.Allowed("Starfield.esm", []));
        Assert.True(ReleaseCreations.Allowed("SFBGS050.esm", []));
        Assert.True(ReleaseCreations.Allowed("ShatteredSpace.esm", []));
        Assert.False(ReleaseCreations.Allowed("OtherMod.esm", []));
        Assert.True(ReleaseCreations.Allowed("othermod.esm", ["OtherMod.esm"]));
        // 0.10.1 code review: the patterns end with \z, so a trailing newline doesn't match.
        Assert.False(ReleaseCreations.IsOfficial("Starfield.esm\n"));
        Assert.False(ReleaseCreations.IsOfficial("SFBGS050.esm\n"));
        // Round 2: the ship blueprints master the docs name. (The CultureInvariant fix has no test here: the patterns
        // are built once per run, so a culture switch inside one test can't reach them reliably.)
        Assert.True(ReleaseCreations.IsOfficial("BlueprintShips-Starfield.esm"));
        Assert.True(ReleaseCreations.IsOfficial("blueprintships-starfield.ESM"));
        Assert.Contains("ConsoleAPI", ReleaseCreations.List.Frameworks);
        Assert.Equal("MyMod - Main_xbox.ba2", ReleaseCreations.XboxTwin("MyMod - Main.ba2"));
    }

    // 0.13.0: the [official] list uses Bethesda's exact name shapes, as check does, not SFBGS*/SFTA* prefixes.
    [Theory]
    [InlineData("SFBGS003.esm", true)]
    [InlineData("sfbgs00a.ESM", true)]
    [InlineData("SFBGS003Fixes.esm", false)]
    [InlineData("SFBGSExtras.esm", false)]
    [InlineData("sfta01.esm", true)]
    [InlineData("SFTA1.esm", false)]
    [InlineData("SFTA100.esm", false)]
    [InlineData("BlueprintShips-SFBGS050.esm", true)]
    [InlineData("BlueprintShips-sfta01.esm", true)]
    [InlineData("BlueprintShips-SFBGS050Fixes.esm", false)]
    [InlineData("SFBGS003.esm\n", false)]
    public void Official_names_follow_Bethesdas_exact_shapes(string name, bool official)
    {
        Assert.Equal(official, ReleaseCreations.IsOfficial(name));
        // the same answer check gives for the same name
        Assert.Equal(official, Checker.IsOfficial(name));
    }

    [Theory]
    [InlineData("A?C.esm", "ABC.esm", true)]
    [InlineData("A?C.esm", "AC.esm", false)]
    [InlineData("X[0-9].esm", "x7.esm", true)]
    [InlineData("X[0-9].esm", "XA.esm", false)]
    [InlineData("X[!0-9].esm", "XA.esm", true)]
    [InlineData("X[!0-9].esm", "X7.esm", false)]
    [InlineData("X[.esm", "X[.esm", true)]
    [InlineData("A.esm", "AXesm", false)]
    [InlineData("*.esm", "Any Name.esm", true)]
    [InlineData("X[z-a].esm", "Xb.esm", false)]       // a reversed range is empty, as in fnmatch (no exception)
    [InlineData("X[!z-a].esm", "Xb.esm", true)]
    [InlineData("X[z-a0-9].esm", "X5.esm", true)]
    [InlineData("X[z-a0-9].esm", "Xb.esm", false)]
    public void Wildcards_read_like_fnmatch(string pattern, string name, bool match) =>
        Assert.Equal(match, ReleaseCreations.Wildcard(pattern).IsMatch(name));

    [Fact]
    public void Other_profiles_run_none_of_these_rules()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.ini", T("x")), ("SFSE/Plugins/a.dll", T("x")));
        Assert.Empty(Run(s, zip, "basic"));
        Assert.Empty(Run(s, zip, "house"));
    }

    [Fact]
    public void Files_no_platform_takes_and_unknown_masters_fail()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod-0.1.0.zip",
            ("MyMod.esm", ReleaseRulesTests.Plugin("Starfield.esm", "SFBGS050.esm", "OtherMod.esm", "Friend.esm")),
            ("MyMod.ini", T("x")), ("SFSE/Plugins/a.dll", T("x")), ("SFSE/Plugins/a.toml", T("x")), ("Sound/a.wem", T("x")));
        var f = Run(s, zip, "creations", "Friend.esm");
        Assert.Single(Of(f, "creations-ini", Level.Problem));
        Assert.Contains("2 SFSE or program files", Assert.Single(Of(f, "creations-sfse", Level.Problem)).Text);
        Assert.Contains("OtherMod.esm", Assert.Single(Of(f, "creations-master", Level.Problem)).Text);
        Assert.Contains("Friend.esm", Assert.Single(Of(f, "creations-master", Level.Note)).Text);
        // code review round 6: the master findings name the zip, like the other creations findings
        Assert.All(Of(f, "creations-master", Level.Problem).Concat(Of(f, "creations-master", Level.Note)), x => Assert.StartsWith("Mod-0.1.0.zip:MyMod.esm: master ", x.Text));
        Assert.Single(Of(f, "creations-audio", Level.Warning));
        Assert.Single(Of(f, "creations-size", Level.Note));
    }

    [Fact]
    public void Archives_need_an_Xbox_twin_and_textures_without_one_fail()
    {
        using var s = new Sandbox();
        var bad = MakeZip(s, "Bad-0.1.0.zip", ("Bad.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Bad - Main.ba2", Ba2("GNRL", "meshes/a.nif", "sound/voice/a.wem")), ("Bad - Textures.ba2", Ba2("DX10", "textures/a.dds")));
        var f = Run(s, bad);
        Assert.True(Of(f, "creations-xbox", Level.Problem).Count == 1, List(f));
        Assert.Contains("Bad - Textures_xbox.ba2", Of(f, "creations-xbox", Level.Problem)[0].Text);
        Assert.Contains("Bad - Main_xbox.ba2", Assert.Single(Of(f, "creations-xbox", Level.Warning)).Text);
        Assert.Single(Of(f, "creations-audio", Level.Warning));

        var good = MakeZip(s, "Good-0.1.0.zip", ("Good.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Good - Main.ba2", Ba2("GNRL", "meshes/a.nif")), ("Good - Main_xbox.ba2", Ba2("GNRL", "meshes/a.nif")));
        var g = Run(s, good);
        Assert.True(g.All(x => x.Level == Level.Note), List(g));
        Assert.Contains(g, x => x.Rule == "creations-xbox" && x.Text.Contains("Good - Main_xbox.ba2"));
    }

    static PIns Notify(string text) => PIns.CallStatic("Debug", "Notification", "::NoneVar", PV.Str(text));
    static PIns Jmpf(string v, int ofs) => new(0x16, [PV.Id(v), PV.Int(ofs)]);
    static PIns Jmpt(string v, int ofs) => new(0x15, [PV.Id(v), PV.Int(ofs)]);
    static PIns Not(string d, string v) => new(0x0A, [PV.Id(d), PV.Id(v)]);
    static PIns CmpEq(string d, string a, PV b) => new(0x0F, [PV.Id(d), PV.Id(a), b]);

    static readonly (string, string)[] Locals = [("::NoneVar", "None"), ("::temp0", "Float"), ("::temp1", "Bool")];

    static PexFile Script(params PFn[] fns) =>
        Pex.Read(PexBytes.Build([new PObj("MyMod:Quest", "Quest", Vars: [("MyMod_Debug", "GlobalVariable", PV.None)], States: [("", fns)])]), "q.pex");

    [Fact]
    public void A_Debug_message_is_player_facing_unless_a_debug_switch_or_name_guards_it()
    {
        var pex = Script(
            // plain: the player would see it (on PC only)
            new PFn("OnInit", Locals: Locals, Code: [Notify("Hello")]),
            // a function named for debugging
            new PFn("DebugNote", Locals: Locals, Code: [Notify("dbg")]),
            // If MyMod_Debug.GetValue() != 0.0 : cmp_eq + not + jmpf over the call
            new PFn("Guarded", Locals: Locals, Code:
            [
                PIns.CallMethod("GetValue", "MyMod_Debug", "::temp0"), CmpEq("::temp1", "::temp0", PV.Flt(0f)), Not("::temp1", "::temp1"),
                Jmpf("::temp1", 2), Notify("on"), PIns.Return(PV.None),
            ]),
            // If MyMod_Debug.GetValue() == 0.0 : runs when the switch is off, so the player sees it
            new PFn("WhenOff", Locals: Locals, Code:
            [
                PIns.CallMethod("GetValue", "MyMod_Debug", "::temp0"), CmpEq("::temp1", "::temp0", PV.Flt(0f)),
                Jmpf("::temp1", 2), Notify("off"), PIns.Return(PV.None),
            ]),
            // jmpt on a turned-around switch: the block after it runs only when the switch is on
            new PFn("Inverted", Locals: Locals, Code:
            [
                PIns.CallMethod("GetValue", "MyMod_Debug", "::temp0"), CmpEq("::temp1", "::temp0", PV.Flt(0f)),
                Jmpt("::temp1", 2), Notify("on too"), PIns.Return(PV.None),
            ]),
            // after the guarded block ends, a call is player-facing again
            new PFn("After", Locals: Locals, Code:
            [
                PIns.CallMethod("GetValue", "MyMod_Debug", "::temp0"), Jmpf("::temp0", 2), Notify("on"), Notify("always"),
            ]));
        var calls = ReleaseCreations.PlayerDebugCalls(pex).Select(x => x.Function).ToList();
        Assert.Equal(new[] { "OnInit", "WhenOff", "After" }, calls);
    }

    [Fact]
    public void Scripts_that_name_an_SFSE_framework_fail_and_messages_warn()
    {
        using var s = new Sandbox();
        var pex = PexBytes.Build([new PObj("MyMod:Quest", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("ConsoleAPI", "Execute", "::NoneVar", PV.Str("x")), Notify("Hello"),
        ])])])]);
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin("Starfield.esm")), ("Scripts/MyMod/Quest.pex", pex));
        var f = Run(s, zip);
        Assert.Contains("ConsoleAPI", Assert.Single(Of(f, "creations-framework", Level.Problem)).Text);
        Assert.Contains("MyMod:Quest.OnInit", Assert.Single(Of(f, "creations-debug", Level.Warning)).Text);
    }

    [Fact]
    public void A_framework_script_that_calls_IsPluginInstalled_warns_instead()
    {
        // user, 2026-11-21 (option c): the guard makes it a WARNING; a script without it stays a PROBLEM
        using var s = new Sandbox();
        var guarded = PexBytes.Build([new PObj("MyMod:Guarded", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str("ConsoleAPI.esm")), Jmpf("::temp1", 2),
            PIns.CallStatic("ConsoleAPI", "Execute", "::NoneVar", PV.Str("x")), PIns.Return(PV.None),
        ])])])]);
        var bare = PexBytes.Build([new PObj("MyMod:Bare", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("ConsoleAPI", "Execute", "::NoneVar", PV.Str("x")),
        ])])])]);
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Scripts/MyMod/Guarded.pex", guarded), ("Scripts/MyMod/Bare.pex", bare));
        var f = Run(s, zip);
        var w = Assert.Single(Of(f, "creations-framework", Level.Warning));
        Assert.Contains("Guarded.pex", w.Text);
        Assert.Contains("Game.IsPluginInstalled (testing \"ConsoleAPI.esm\")", w.Text);   // names what it tests (round 7)
        var p = Assert.Single(Of(f, "creations-framework", Level.Problem));
        Assert.Contains("Bare.pex", p.Text);
        Assert.Contains("a guard in another script isn't seen", p.Text);
    }

    [Fact]
    public void A_guard_that_tests_only_official_plugins_stays_a_problem()
    {
        // user, 2026-11-21 (0.10.1, option b): only official names → PROBLEM; any other name or a variable → WARNING
        using var s = new Sandbox();
        byte[] Script(string name, params PV[] tested) => PexBytes.Build([new PObj("MyMod:" + name, "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
            tested.Select(t => PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", t))
                .Append(PIns.CallStatic("ConsoleAPI", "Execute", "::NoneVar", PV.Str("x"))).ToArray())])])]);
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Scripts/MyMod/Official.pex", Script("Official", PV.Str("ShatteredSpace.esm"), PV.Str("sfbgs003.esm"))),
            ("Scripts/MyMod/Mixed.pex", Script("Mixed", PV.Str("ShatteredSpace.esm"), PV.Str("ConsoleAPI.esm"))),
            ("Scripts/MyMod/Variable.pex", Script("Variable", PV.Str("Starfield.esm"), PV.Id("::temp1"))));
        var f = Run(s, zip);
        var p = Assert.Single(Of(f, "creations-framework", Level.Problem));
        Assert.Contains("Official.pex", p.Text);
        Assert.Contains("test only official plugins (\"ShatteredSpace.esm\", \"sfbgs003.esm\")", p.Text);
        var w = Of(f, "creations-framework", Level.Warning).Select(x => x.Text).ToList();
        Assert.Equal(2, w.Count);
        Assert.Contains(w, t => t.Contains("Mixed.pex") && t.Contains("\"ConsoleAPI.esm\""));
        Assert.Contains(w, t => t.Contains("Variable.pex") && t.Contains("a name held in a variable"));
    }

    [Fact]
    public void IsPluginInstalled_names_keep_the_raw_text_and_null_for_a_variable()
    {
        var pex = Pex.Read(PexBytes.Build([new PObj("MyMod:Q", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str("a\nb.esm")),
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Id("::temp1")),
        ])])])]), "Q.pex");
        Assert.Equal(["a\nb.esm", null], ReleaseCreations.IsPluginInstalledNames(pex));
    }

    [Fact]
    public void IsPluginInstalled_targets_name_each_string_once_and_a_variable_plainly()
    {
        var pex = Pex.Read(PexBytes.Build([new PObj("MyMod:Q", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str("ShatteredSpace.esm")),
            PIns.CallStatic("game", "isplugininstalled", "::temp1", PV.Str("shatteredspace.esm")),
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Id("::temp1")),
            PIns.CallStatic("Game", "GetFormFromFile", "::temp1", PV.Str("Other.esm")),
        ])])])]), "Q.pex");
        Assert.Equal(["\"ShatteredSpace.esm\"", "a name held in a variable"], ReleaseCreations.IsPluginInstalledTargets(pex));
        var none = Pex.Read(PexBytes.Build([new PObj("MyMod:N", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("Game", "GetFormFromFile", "::temp1", PV.Str("Other.esm")),
        ])])])]), "N.pex");
        Assert.Null(ReleaseCreations.IsPluginInstalledTargets(none));
    }

    [Fact]
    public void A_tested_plugin_name_stays_on_one_line_in_the_warning()
    {
        var pex = Pex.Read(PexBytes.Build([new PObj("MyMod:Q", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str("a\nb\"c\\d\u0001")),
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str(new string('x', 100))),
        ])])])]), "Q.pex");
        var t = ReleaseCreations.IsPluginInstalledTargets(pex)!;
        Assert.Equal("\"a\\nb\\\"c\\\\d\\u0001\"", t[0]);
        Assert.Equal("\"" + new string('x', 80) + "...\"", t[1]);
        Assert.DoesNotContain(t, s => s.Any(char.IsControl));
    }

    [Fact]
    public void Quoted_cuts_cleanly_and_escapes_every_line_break()
    {
        Assert.Equal("\"" + new string('x', 80) + "\"", ReleaseCreations.Quoted(new string('x', 80)));
        // an emoji across the cut: the pair is dropped whole, not split
        var q = ReleaseCreations.Quoted(new string('x', 79) + "\U0001F600" + "tail");
        Assert.Equal("\"" + new string('x', 79) + "...\"", q);
        Assert.DoesNotContain(q, char.IsSurrogate);
        Assert.Equal("\"a\\u2028b\\u2029c\\u202ed\"", ReleaseCreations.Quoted("a\u2028b\u2029c\u202Ed"));
        Assert.Equal("\"" + new string('y', 79) + "\\n...\"", ReleaseCreations.Quoted(new string('y', 79) + "\n" + new string('z', 5)));
    }

    [Fact]
    public void A_script_name_with_a_newline_stays_on_one_line_in_the_finding()
    {
        // code review round 9: object and function names went into creations-debug raw
        using var s = new Sandbox();
        var pex = PexBytes.Build([new PObj("MyMod:Q\nWARNING [x] fake", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code: [Notify("Hello")])])])]);
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin("Starfield.esm")), ("Scripts/MyMod/Quest.pex", pex));
        var text = Assert.Single(Of(Run(s, zip), "creations-debug", Level.Warning)).Text;
        Assert.Contains("MyMod:Q\\nWARNING [x] fake.OnInit", text);
        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public void OneLine_escapes_line_breaks_but_keeps_paths()
    {
        Assert.Equal(@"Scripts\My Mod.pex", SafeText.OneLine(@"Scripts\My Mod.pex"));
        var s = SafeText.OneLine("Obj\nWARNING [x] fake\u2028more\u202E\u0001");
        Assert.Equal("Obj\\nWARNING [x] fake\\u2028more\\u202e\\u0001", s);
        Assert.DoesNotContain(s, c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);
    }

    [Fact]
    public void OneLine_escapes_every_format_character()
    {
        // Code review round 10: U+061C (a bidi mark) and the zero-width characters weren't escaped.
        var s = SafeText.OneLine("A\u061CB\u200BC\u200DD\u2060E\uFEFFF\u200EG\u2066H");
        Assert.Equal("A\\u061cB\\u200bC\\u200dD\\u2060E\\ufeffF\\u200eG\\u2066H", s);
        Assert.Equal("\"x\\u061c\"", ReleaseCreations.Quoted("x\u061C"));
        Assert.Equal("Ünïcødé – ok", SafeText.OneLine("Ünïcødé – ok"));
    }

    [Fact]
    public void Two_long_tested_names_sharing_their_start_are_both_listed()
    {
        var a = new string('n', 85) + "A.esm";
        var b = new string('n', 85) + "B.esm";
        var pex = Pex.Read(PexBytes.Build([new PObj("MyMod:Q", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code:
        [
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str(a)),
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str(b)),
            PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str(a.ToUpperInvariant())),
        ])])])]), "Q.pex");
        Assert.Equal(2, ReleaseCreations.IsPluginInstalledTargets(pex)!.Count);
    }

    /// <summary>A version 1 general BA2 holding these files stored (not packed), so scripts can be read from it.</summary>
    static byte[] Ba2Stored(params (string Name, byte[] Data)[] files)
    {
        long dataAt = 24 + 36L * files.Length;
        long nameAt = dataAt + files.Sum(f => (long)f.Data.Length);
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write(T("BTDX")); w.Write(1u); w.Write(T("GNRL")); w.Write((uint)files.Length); w.Write((ulong)nameAt);
        long at = dataAt;
        foreach (var f in files)
        {
            w.Write(0u); w.Write(T("pex\0")); w.Write(0u); w.Write(0x100100u);
            w.Write((ulong)at); w.Write(0u); w.Write((uint)f.Data.Length); w.Write(0xBAADF00Du);
            at += f.Data.Length;
        }
        foreach (var f in files) w.Write(f.Data);
        foreach (var f in files) { var b = T(f.Name); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        return o.ToArray();
    }

    [Fact]
    public void Scripts_in_an_Xbox_twin_are_read_once_and_its_sound_is_checked()
    {
        // 0.10.0 code review: the twin's copy of each script doubled every finding, and its sound went unchecked
        using var s = new Sandbox();
        var pex = PexBytes.Build([new PObj("MyMod:Quest", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code: [Notify("Hello")])])])]);
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("MyMod - Main.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex))),
            ("MyMod - Main_xbox.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex), ("sound/voice/a.wem", T("x")))));
        var f = Run(s, zip);
        Assert.True(Of(f, "creations-debug", Level.Warning).Count == 1, List(f));
        Assert.Contains("MyMod - Main_xbox.ba2", Assert.Single(Of(f, "creations-audio", Level.Warning)).Text);
        Assert.Equal("MyMod - Main.ba2", ReleaseCreations.PcTwin("MyMod - Main_xbox.ba2"));

        // an Xbox archive alone still has its scripts read
        var only = MakeZip(s, "Only-0.1.0.zip", ("Only.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Only - Main_xbox.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex))));
        Assert.Single(Of(Run(s, only), "creations-debug", Level.Warning));
    }

    [Fact]
    public void An_Xbox_twins_own_scripts_are_read_when_the_PC_archive_lacks_them_or_is_unreadable()
    {
        // 0.10.0 code review round 2: the twin was skipped whenever a PC archive of the same name was in the zip
        using var s = new Sandbox();
        var pex = PexBytes.Build([new PObj("MyMod:Quest", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code: [Notify("Hello")])])])]);
        var other = PexBytes.Build([new PObj("MyMod:Other", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code: [Notify("Hi")])])])]);
        // the twin holds a script the PC archive lacks: both scripts are read, the shared one once
        var extra = MakeZip(s, "Extra-0.1.0.zip", ("Extra.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Extra - Main.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex))),
            ("Extra - Main_xbox.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex), ("scripts/mymod/other.pex", other))));
        var f = Run(s, extra);
        var debug = Of(f, "creations-debug", Level.Warning);
        Assert.True(debug.Count == 2, List(f));
        Assert.Contains(debug, x => x.Text.StartsWith("Extra-0.1.0.zip:Extra - Main_xbox.ba2:") && x.Text.Contains("other.pex"));

        // the PC archive can't be read: every script in the twin is read
        var broken = MakeZip(s, "Broken-0.1.0.zip", ("Broken.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Broken - Main.ba2", T("not an archive")),
            ("Broken - Main_xbox.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex))));
        Assert.Single(Of(Run(s, broken), "creations-debug", Level.Warning));
    }

    [Fact]
    public void Sound_in_a_PC_archive_and_its_Xbox_twin_gives_one_warning()
    {
        // 0.10.0 code review round 2: one WARNING per pair, naming both archives
        using var s = new Sandbox();
        var zip = MakeZip(s, "Snd-0.1.0.zip", ("Snd.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Snd - Main.ba2", Ba2("GNRL", "sound/voice/a.wem")), ("Snd - Main_xbox.ba2", Ba2("GNRL", "sound/voice/a.wem")));
        var f = Run(s, zip);
        var audio = Assert.Single(Of(f, "creations-audio", Level.Warning));
        Assert.StartsWith("Snd-0.1.0.zip:Snd - Main.ba2 (1 sound file) and Snd - Main_xbox.ba2 (1 sound file).", audio.Text);
    }

    [Fact]
    public void Archives_of_the_same_name_in_two_folders_are_checked_apart()
    {
        // 0.10.0 code review round 3: lists keyed by file name mixed two folders' archives, and paired twins across folders
        using var s = new Sandbox();
        var pex = PexBytes.Build([new PObj("MyMod:Quest", "Quest", States: [("", [new PFn("OnInit", Locals: Locals, Code: [Notify("Hello")])])])]);
        var zip = MakeZip(s, "Opt-0.1.0.zip", ("Opt.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("OptionA/Opt - Main.ba2", Ba2("DX10", "textures/a.dds")),
            ("OptionB/Opt - Main.ba2", Ba2("GNRL", "sound/voice/a.wem")),
            ("OptionB/Opt - Main_xbox.ba2", Ba2Stored(("scripts/mymod/quest.pex", pex))));
        var f = Run(s, zip);
        // OptionA's textures have no twin in OptionA, though OptionB has one; messages name the folder (round 4)
        var a = Path.Combine("OptionA", "Opt - Main.ba2");
        var b = Path.Combine("OptionB", "Opt - Main.ba2");
        var xbox = Assert.Single(Of(f, "creations-xbox", Level.Problem));
        Assert.True(xbox.Text.StartsWith($"Opt-0.1.0.zip:{a}: 1 texture and no Opt - Main_xbox.ba2."), List(f));
        // one sound WARNING, for OptionB's PC archive only (its twin holds no sound)
        Assert.Equal($"Opt-0.1.0.zip:{b}: 1 sound file.", Assert.Single(Of(f, "creations-audio", Level.Warning)).Text.Split(" PC-format")[0]);
        // OptionB's twin script is read: no PC archive in its folder holds it; its label names the folder too
        // ... and the zip's name, as loose scripts' labels have (round 5)
        Assert.StartsWith("Opt-0.1.0.zip:" + Path.Combine("OptionB", "Opt - Main_xbox.ba2") + ":", Assert.Single(Of(f, "creations-debug", Level.Warning)).Text);
    }

    [Fact]
    public void A_shared_sound_warning_names_both_archives_by_their_folder()
    {
        // 0.10.0 code review round 5: the twin was named by its bare file name
        using var s = new Sandbox();
        var zip = MakeZip(s, "Snd-0.1.0.zip", ("Snd.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("OptionA/Snd - Main.ba2", Ba2("GNRL", "sound/voice/a.wem")), ("OptionA/Snd - Main_xbox.ba2", Ba2("GNRL", "sound/voice/a.wem")));
        var audio = Assert.Single(Of(Run(s, zip), "creations-audio", Level.Warning));
        Assert.StartsWith($"Snd-0.1.0.zip:{Path.Combine("OptionA", "Snd - Main.ba2")} (1 sound file) and {Path.Combine("OptionA", "Snd - Main_xbox.ba2")} (1 sound file).", audio.Text);
    }

    [Fact]
    public void An_unreadable_PC_archive_still_needs_its_Xbox_twin()
    {
        // 0.10.0 code review round 4: an archive that can't be read got no creations-xbox finding at all
        using var s = new Sandbox();
        var zip = MakeZip(s, "Bad-0.1.0.zip", ("Bad.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Bad - Main.ba2", T("not an archive")));
        var f = Run(s, zip);
        var w = Assert.Single(Of(f, "creations-xbox", Level.Warning));
        Assert.True(w.Text.StartsWith("Bad-0.1.0.zip:Bad - Main.ba2: no Bad - Main_xbox.ba2, so Xbox gets no archive"), List(f));
        // round 5: the text says the archive couldn't be read, so its textures weren't checked
        Assert.Contains("this archive can't be read, so its textures weren't checked", w.Text);
        // an unreadable Xbox archive is a ba2-unreadable PROBLEM (ReleaseRules), and the creations rules add nothing
        var bx = MakeZip(s, "Bx-0.1.0.zip", ("Bx.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Bx - Main.ba2", Ba2("GNRL", "meshes/a.nif")), ("Bx - Main_xbox.ba2", T("not an archive")));
        var fx = Run(s, bx);
        Assert.Empty(Of(fx, "creations-xbox", Level.Warning));
        Assert.Empty(Of(fx, "creations-xbox", Level.Problem));
        // round 6: both broken archives really do get their ba2-unreadable PROBLEM from the zip rules, which run in every profile
        Assert.Contains("Bx - Main_xbox.ba2", Assert.Single(Of(ZipRules(s, bx), "ba2-unreadable", Level.Problem)).Text);
        Assert.Contains("Bad - Main.ba2", Assert.Single(Of(ZipRules(s, zip), "ba2-unreadable", Level.Problem)).Text);
        Assert.Contains(ReleaseCommand.Steps, step => step.Method.DeclaringType == typeof(ReleaseRules));
        // with its twin present there's nothing to say about the pair
        var twin = MakeZip(s, "Bad2-0.1.0.zip", ("Bad2.esm", ReleaseRulesTests.Plugin("Starfield.esm")),
            ("Bad2 - Main.ba2", T("not an archive")), ("Bad2 - Main_xbox.ba2", Ba2("GNRL", "meshes/a.nif")));
        Assert.Empty(Of(Run(s, twin), "creations-xbox", Level.Warning));
    }

    [Fact]
    public void The_size_limit_is_per_zip_and_asi_loaders_fail()
    {
        using var s = new Sandbox();
        var a = ReleaseCommand.Unpack(MakeZip(s, "A-0.1.0.zip", ("A.esm", ReleaseRulesTests.Plugin("Starfield.esm")), ("hook.asi", T("x"))), Path.Combine(s.Dir, "out", "a"));
        var b = ReleaseCommand.Unpack(MakeZip(s, "B-0.1.0.zip", ("B.esm", ReleaseRulesTests.Plugin("Starfield.esm"))), Path.Combine(s.Dir, "out", "b"));
        var f = ReleaseCreations.Run(new ReleaseContext { Options = new ReleaseOptions { Profile = "creations" }, Packages = [a, b], Work = s.Sub("work") }).ToList();
        var sizes = Of(f, "creations-size", Level.Note);
        Assert.Equal(2, sizes.Count);
        Assert.StartsWith(a.Name + ":", sizes[0].Text);
        Assert.Contains("hook.asi", Assert.Single(Of(f, "creations-sfse", Level.Problem)).Text);
    }

    [Fact]
    public void House_rules_stay_warnings_in_the_creations_profile()
    {
        var house = new ReleaseFinding(Level.Problem, "one-plugin", "two plugins", RuleKind.House);
        Assert.Equal(Level.Warning, ReleaseCommand.Apply(house, "creations").Level);
    }
}
