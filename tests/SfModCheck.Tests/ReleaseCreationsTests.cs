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
        Assert.Contains("ConsoleAPI", ReleaseCreations.List.Frameworks);
        Assert.Equal("MyMod - Main_xbox.ba2", ReleaseCreations.XboxTwin("MyMod - Main.ba2"));
    }

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
        Assert.Contains(debug, x => x.Text.StartsWith("Extra - Main_xbox.ba2:") && x.Text.Contains("other.pex"));

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
        Assert.StartsWith("Snd-0.1.0.zip:Snd - Main.ba2 and Snd - Main_xbox.ba2 (1): 1 sound file.", audio.Text);
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
