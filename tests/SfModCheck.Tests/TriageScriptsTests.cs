// Triage section "Scripts" (0.8.0, chunk C3): made-up scripts (PexBytes in PexTests.cs), loose and inside a made-up
// BA2, with a made-up game folder whose only archive lists a few script names.
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class TriageScriptsTests
{
    /// <summary>A general (GNRL, version 1) BA2 with these files; compressed ones are stored with zlib.</summary>
    public static byte[] Ba2(params (string Name, byte[] Data, bool Zip)[] files)
    {
        var stored = files.Select(f =>
        {
            if (!f.Zip) return f.Data;
            var m = new MemoryStream();
            using (var z = new ZLibStream(m, CompressionLevel.Optimal, leaveOpen: true)) z.Write(f.Data);
            return m.ToArray();
        }).ToList();
        long dataAt = 24 + 36L * files.Length;
        var names = new MemoryStream();
        foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Name); names.Write(BitConverter.GetBytes((ushort)b.Length)); names.Write(b); }
        long nameAt = dataAt + stored.Sum(s => (long)s.Length);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BTDX")); w.Write(1u); w.Write(Encoding.ASCII.GetBytes("GNRL")); w.Write((uint)files.Length); w.Write((ulong)nameAt);
        long at = dataAt;
        for (int i = 0; i < files.Length; i++)
        {
            w.Write(0u); w.Write(Encoding.ASCII.GetBytes("pex\0")); w.Write(0u); w.Write(0x100100u);
            w.Write((ulong)at); w.Write(files[i].Zip ? (uint)stored[i].Length : 0u); w.Write((uint)files[i].Data.Length); w.Write(0xBAADF00Du);
            at += stored[i].Length;
        }
        foreach (var s in stored) w.Write(s);
        w.Write(names.ToArray());
        w.Flush();
        return o.ToArray();
    }

    sealed class Mod : Sandbox
    {
        public string Root => Sub("mod");
        public string Game => Sub(Path.Combine("game", "Data"));   // a Data folder inside a game folder, as a real install
        public void Put(string rel, byte[] data)
        {
            var p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        public TriageContext Context(bool game = true)
        {
            if (game)
            {
                File.WriteAllText(Path.Combine(Game, "Starfield.esm"), "");
                File.WriteAllBytes(Path.Combine(Game, "Starfield - Misc.ba2"), Ba2(
                    ("scripts/quest.pex", [1], false), ("scripts/game.pex", [1], false), ("scripts/debug.pex", [1], false),
                    ("scripts/objectreference.pex", [1], false), ("scripts/actor.pex", [1], false), ("scripts/utility.pex", [1], false),
                    ("scripts/scriptobject.pex", [1], false)));
            }
            var files = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Select(f => Ba2Names.Normalize(Path.GetRelativePath(Root, f))).OrderBy(f => f, StringComparer.Ordinal).ToList();
            return new TriageContext
            {
                Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = Root, Root = Root, Files = files,
                DataPrefix = "", DataDir = Game, Work = Sub("work"), Report = new TriageReport(),
            };
        }
    }

    static byte[] Busy() => PexBytes.Build(
    [
        new PObj("MyMod:Busy", "Quest", Vars: [("Path", "String", PV.Str("C:\\Tools\\run.exe"))], States:
        [
            ("", [
                new PFn("OnInit", Locals: [("::temp0", "Form"), ("::temp1", "Bool"), ("::NoneVar", "None")], Code:
                [
                    PIns.CallStatic("Game", "GetFormFromFile", "::temp0", PV.Int(0x800), PV.Str("OtherMod.esm")),
                    PIns.CallStatic("Game", "GetFormFromFile", "::temp0", PV.Int(0x801), PV.Str("MyMod.esm")),
                    PIns.CallStatic("Game", "GetFormFromFile", "::temp0", PV.Int(0x802), PV.Str("Starfield.esm")),
                    PIns.CallStatic("Game", "IsPluginInstalled", "::temp1", PV.Str("Helper.esp")),
                    PIns.CallStatic("Debug", "ExecuteConsole", "::NoneVar", PV.Str("tgm")),
                    PIns.CallStatic("Utility", "SetINIFloat", "::NoneVar", PV.Str("fJumpHeight:Movement"), PV.Flt(5f)),
                    PIns.CallStatic("ConsoleUtil", "ExecuteCommand", "::NoneVar", PV.Str("tcl")),
                    PIns.CallStatic("Debug", "OpenUserLog", "::temp1", PV.Str("MyModLog")),
                    PIns.CallStatic("Debug", "QuitGame", "::NoneVar"),
                    PIns.Assign("::temp1", PV.Str("Patch.esp")),
                    PIns.Assign("::temp1", PV.Str("Meshes\\Thing.nif")),
                    PIns.CallMethod("StartTimer", "self", "::NoneVar", PV.Flt(1f), PV.Int(1)),
                ]),
                new PFn("OnTimer", Params: [("aiTimerID", "Int")], Locals: [("::NoneVar", "None")], Code:
                [
                    PIns.CallMethod("StartTimer", "self", "::NoneVar", PV.Flt(0.1f), PV.Int(1)),
                ]),
                new PFn("OnPlayerLoadGame", Locals: [("::NoneVar", "None")], Code:
                [
                    PIns.CallMethod("RegisterForMenuOpenCloseEvent", "self", "::NoneVar", PV.Str("PauseMenu")),
                ]),
            ]),
        ]),
    ]);

    static byte[] Quiet() => PexBytes.Build(
    [
        new PObj("MyMod:Quiet", "ObjectReference", States:
            [("", [new PFn("OnLoad", Locals: [("::NoneVar", "None")], Code: [PIns.CallMethod("Disable", "self", "::NoneVar", PV.Bool(false))])])]),
    ]);

    static byte[] Logs() => PexBytes.Build(
    [
        new PObj("MyMod:Logs", "Quest", States:
            [("", [new PFn("OnInit", Locals: [("::NoneVar", "None")], Code:
                [PIns.CallStatic("Debug", "Trace", "::NoneVar", PV.Str("MyMod: hello"), PV.Int(0)),
                 PIns.CallStatic("MyMod:Quiet", "Helper", "::NoneVar")])])]),
    ]);

    static string Dump(IEnumerable<TriageFinding> f) => string.Join("\n", f.Select(x => $"{x.Level} {x.File}: {x.Text}"));

    [Fact]
    public void Loose_and_archived_scripts_are_read_and_their_calls_listed()
    {
        using var m = new Mod();
        m.Put("MyMod.esm", [0]);
        m.Put("Scripts/MyMod/Busy.pex", Busy());
        m.Put("MyMod - Main.ba2", Ba2(("scripts/mymod/quiet.pex", Quiet(), true), ("scripts/mymod/logs.pex", Logs(), false), ("meshes/a.nif", [1, 2], false)));
        var c = m.Context();
        var findings = TriageScripts.Run(c).ToList();
        var all = Dump(findings);

        var table = c.Report.Tables.Single(t => t.Title == "Scripts");
        Assert.Equal(3, table.Rows.Count);
        var busy = table.Rows.Single(r => r[0] == "scripts\\mymod\\busy.pex");
        Assert.Equal("MyMod:Busy", busy[1]);
        Assert.Equal("Quest", busy[2]);
        Assert.Equal("ConsoleUtil: ExecuteCommand; Debug: ExecuteConsole, OpenUserLog, QuitGame; Game: GetFormFromFile (3), IsPluginInstalled; " +
                     "MyMod:Busy: RegisterForMenuOpenCloseEvent, StartTimer (2); Utility: SetINIFloat", busy[3]);
        Assert.Equal("12", busy[4]);
        Assert.Equal("no", busy[5]);
        var quiet = table.Rows.Single(r => r[0] == "mymod - main.ba2:scripts\\mymod\\quiet.pex");
        Assert.Equal("MyMod:Quiet: Disable", quiet[3]);
        Assert.Equal("yes", table.Rows.Single(r => r[0] == "mymod - main.ba2:scripts\\mymod\\logs.pex")[5]);

        bool Has(TriageLevel l, string file, string text) => findings.Any(f => f.Level == l && f.File.EndsWith(file) && f.Text.Contains(text));
        Assert.True(Has(TriageLevel.Look, "busy.pex", "reads records from OtherMod.esm with Game.GetFormFromFile"), all);
        Assert.True(Has(TriageLevel.Note, "busy.pex", "reads records from its own plugin MyMod.esm"), all);
        Assert.True(Has(TriageLevel.Note, "busy.pex", "reads records from the official plugin Starfield.esm"), all);
        Assert.True(Has(TriageLevel.Note, "busy.pex", "checks whether Helper.esp is installed"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "runs console commands with Debug.ExecuteConsole"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "runs console commands with ConsoleUtil.ExecuteCommand"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "changes INI settings with Utility.SetINIFloat"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "calls Debug.QuitGame"), all);
        Assert.True(Has(TriageLevel.Note, "busy.pex", "writes its own Papyrus log file"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "OnTimer calls StartTimer with 0.1 seconds: the event starts itself again"), all);
        Assert.False(findings.Any(f => f.Text.StartsWith("OnInit calls StartTimer")), all);   // OnInit runs once
        Assert.True(Has(TriageLevel.Note, "busy.pex", "OnPlayerLoadGame calls RegisterForMenuOpenCloseEvent"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "calls ConsoleUtil, a script that is neither in the mod nor in the game"), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "names Patch.esp in its text"), all);
        Assert.True(Has(TriageLevel.Note, "busy.pex", "names the file \"Meshes\\Thing.nif\""), all);
        Assert.True(Has(TriageLevel.Look, "busy.pex", "names the file \"C:\\Tools\\run.exe\""), all);
        Assert.False(findings.Any(f => f.Text.Contains("names OtherMod.esm")), all);   // already named by GetFormFromFile
        Assert.True(Has(TriageLevel.Note, "", "1 of 3 scripts with code log with Debug.Trace"), all);
        // the mod's own script and the game's scripts aren't "outside" calls
        Assert.False(findings.Any(f => f.Text.Contains("calls MyMod:Quiet, a script")), all);
        Assert.False(findings.Any(f => f.Text.Contains("calls Game, a script")), all);

        var calls = c.Report.Tables.Single(t => t.Title == "Calls worth a look");
        Assert.Contains(calls.Rows, r => r[0] == "LOOK" && r[2] == "OnInit" && r[3] == "Game.GetFormFromFile" && r[4] == "OtherMod.esm, form 0x000800");
        Assert.Contains(calls.Rows, r => r[3] == "Debug.ExecuteConsole" && r[4] == "command \"tgm\"");

        // plain style: the text says what was found, never a judgement
        foreach (var f in findings) Assert.DoesNotMatch(new Regex(@"\b(malicious|bad|unsafe|dangerous)\b", RegexOptions.IgnoreCase), f.Text);
    }

    [Fact]
    public void Without_the_game_outside_calls_are_not_guessed()
    {
        using var m = new Mod();
        m.Put("Scripts/Busy.pex", Busy());
        var findings = TriageScripts.Run(m.Context(game: false)).ToList();
        Assert.Contains(findings, f => f.Level == TriageLevel.Note && f.Text.Contains("no game was found"));
        Assert.DoesNotContain(findings, f => f.Text.Contains("neither in the mod nor in the game"));
        // without the mod's plugin, MyMod.esm counts as another plugin
        Assert.Contains(findings, f => f.Level == TriageLevel.Look && f.Text.Contains("reads records from MyMod.esm"));
    }

    [Fact]
    public void Damaged_scripts_and_archives_are_listed_and_the_rest_still_read()
    {
        using var m = new Mod();
        var cut = Logs()[..40];
        m.Put("Scripts/Cut.pex", cut);
        m.Put("Scripts/Logs.pex", Logs());
        var arch = Ba2(("scripts/a.pex", Quiet(), true), ("scripts/b.pex", Quiet(), true));
        // damage the second entry's zlib data
        var badZip = (byte[])arch.Clone();
        long secondAt = BitConverter.ToInt64(badZip, 24 + 36 + 16);
        for (int i = 2; i < 8; i++) badZip[secondAt + i] ^= 0x5A;
        m.Put("Bad - Main.ba2", badZip);
        m.Put("Junk - Main.ba2", Encoding.ASCII.GetBytes("not an archive at all"));
        var c = m.Context();
        var findings = TriageScripts.Run(c).ToList();
        var all = Dump(findings);
        Assert.Contains(findings, f => f.Level == TriageLevel.Look && f.File == "scripts\\cut.pex" && f.Text.Contains("can't be read as a compiled script") && f.Text.Contains("at byte"));
        Assert.Contains(findings, f => f.Level == TriageLevel.Look && f.File == "bad - main.ba2:scripts\\b.pex" && f.Text.Contains("wasn't read"));
        Assert.Contains(findings, f => f.Level == TriageLevel.Look && f.File == "junk - main.ba2" && f.Text.Contains("couldn't be listed"));
        var rows = c.Report.Tables.Single(t => t.Title == "Scripts").Rows;
        Assert.Equal(4, rows.Count);
        Assert.Equal("yes", rows.Single(r => r[0] == "scripts\\logs.pex")[5]);
        Assert.Equal("MyMod:Quiet", rows.Single(r => r[0] == "bad - main.ba2:scripts\\a.pex")[1]);
        Assert.NotEmpty(all);
    }

    [Fact]
    public void An_archive_entry_that_points_past_the_end_is_refused_by_name()
    {
        using var m = new Mod();
        var arch = Ba2(("scripts/a.pex", Quiet(), false));
        BitConverter.TryWriteBytes(arch.AsSpan(24 + 28), 0x7FFF0000u);   // size: huge
        m.Put("A - Main.ba2", arch);
        var findings = TriageScripts.Run(m.Context()).ToList();
        Assert.Contains(findings, f => f.Level == TriageLevel.Look && f.File == "a - main.ba2:scripts\\a.pex" && f.Text.Contains("larger than any compiled script"));
    }

    [Fact]
    public void A_mod_without_scripts_says_so()
    {
        using var m = new Mod();
        m.Put("MyMod.esm", [0]);
        m.Put("Textures - Main.ba2", Ba2(("textures/a.dds", [1], false)));
        var findings = TriageScripts.Run(m.Context()).ToList();
        var f = Assert.Single(findings);
        Assert.Contains("no compiled scripts", f.Text);
    }

    [Fact]
    public void A_Fallout_4_script_is_marked()
    {
        using var m = new Mod();
        m.Put("Scripts/Old.pex", PexBytes.Sample(minor: 9));
        var findings = TriageScripts.Run(m.Context()).ToList();
        Assert.Contains(findings, f => f.Level == TriageLevel.Look && f.Text.Contains("PEX 3.9") && f.Text.Contains("Fallout 4"));
    }

    [Theory]
    [InlineData("scripts\\foo\\bar.pex", "foo:bar")]
    [InlineData("data\\scripts\\bar.pex", "bar")]
    [InlineData("x - main.ba2:scripts\\a\\b.pex", "a:b")]
    [InlineData("loose.pex", "loose")]
    public void Script_types_from_paths(string path, string type) => Assert.Equal(type, TriageScripts.TypeOfPath(path));

    [Fact]
    public void The_section_runs_inside_the_triage_command()
    {
        using var m = new Mod();
        m.Put("Scripts/Logs.pex", Logs());
        m.Context();   // makes the made-up game folder
        var outDir = m.Sub("out");
        var oldOut = Console.Out;
        Console.SetOut(new StringWriter());
        int code;
        try { code = TriageCommand.Run([m.Root, "--out", outDir, "--quiet", "--game", m.Game], "test", "byline"); }
        finally { Console.SetOut(oldOut); }
        Assert.Equal(0, code);
        var json = File.ReadAllText(Path.Combine(outDir, "mod-triage.json"));
        Assert.Contains("\"Functions called\"", json);
        Assert.Contains("Debug: Trace", json);
    }
}
