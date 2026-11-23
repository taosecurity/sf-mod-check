// The Mutagen gap check (0.7.0, chunk C3: GapCheck.cs, a port of tools\mutagen-gap-check.py). The masters and
// overrides are written byte by byte here, so each test controls exactly which fields and flags a record has.
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class GapCheckTests
{
    // ---- a tiny raw plugin writer ----

    static byte[] Field(string type, byte[] data) =>
        [.. Encoding.ASCII.GetBytes(type), .. BitConverter.GetBytes((ushort)data.Length), .. data];

    static byte[] Z(string s) => [.. Encoding.ASCII.GetBytes(s), 0];

    static byte[] Record(string sig, uint fid, uint flags, params byte[][] fields)
    {
        var body = fields.SelectMany(f => f).ToArray();
        var h = new byte[24];
        Encoding.ASCII.GetBytes(sig).CopyTo(h, 0);
        BitConverter.GetBytes((uint)body.Length).CopyTo(h, 4);
        BitConverter.GetBytes(flags).CopyTo(h, 8);
        BitConverter.GetBytes(fid).CopyTo(h, 12);
        BitConverter.GetBytes((ushort)557).CopyTo(h, 20);
        return [.. h, .. body];
    }

    static byte[] Group(uint label, int type, params byte[][] items)
    {
        var body = items.SelectMany(i => i).ToArray();
        var h = new byte[24];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(h, 0);
        BitConverter.GetBytes((uint)(24 + body.Length)).CopyTo(h, 4);
        BitConverter.GetBytes(label).CopyTo(h, 8);
        BitConverter.GetBytes(type).CopyTo(h, 12);
        return [.. h, .. body];
    }

    static byte[] Top(string sig, params byte[][] records) => Group(BitConverter.ToUInt32(Encoding.ASCII.GetBytes(sig)), 0, records);

    /// <summary>A cell (in block 0, sub-block 0) with its temporary children.</summary>
    static byte[] CellWithTemp(byte[] cell, uint cellId, params byte[][] refs) =>
        Top("CELL", Group(0, 2, Group(0, 3, cell, Group(cellId, 6, Group(cellId, 9, refs)))));

    static string Write(string dir, string name, uint flags, string[] masters, uint[]? onam, params byte[][] groups)
    {
        var hf = new List<byte[]> { Field("HEDR", [.. BitConverter.GetBytes(0.96f), .. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(0x800u)]) };
        foreach (var m in masters) { hf.Add(Field("MAST", Z(m))); hf.Add(Field("DATA", new byte[8])); }
        if (onam != null) hf.Add(Field("ONAM", onam.SelectMany(BitConverter.GetBytes).ToArray()));
        var file = Record("TES4", 0, flags, [.. hf]).Concat(groups.SelectMany(g => g)).ToArray();
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    static readonly byte[] Data8 = new byte[8];

    static byte[] Misc(uint fid, uint flags, bool withFull = true) =>
        withFull ? Record("MISC", fid, flags, Field("EDID", Z($"GAP_Misc{fid:X}")), Field("FULL", Z("Thing")), Field("DATA", Data8))
                 : Record("MISC", fid, flags, Field("EDID", Z($"GAP_Misc{fid:X}")), Field("DATA", Data8));

    static readonly byte[] CellRec = Record("CELL", 0x803, 0, Field("EDID", Z("GAP_Cell")), Field("DATA", new byte[2]));
    static readonly byte[] RefRec = Record("REFR", 0x804, 0, Field("NAME", BitConverter.GetBytes(0x800u)), Field("DATA", new byte[24]));

    /// <summary>GapM.esm: a full master with misc items 800-802 and cell 803 holding temporary reference 804.</summary>
    static string Master(string dir) =>
        Write(dir, "GapM.esm", Flags.Master, [], null,
            Top("MISC", Misc(0x800, 0), Misc(0x801, 0), Misc(0x802, 0)),
            CellWithTemp(CellRec, 0x803, RefRec));

    // Records the plugin overrides use the master's index (00) in the top byte.
    const uint M800 = 0x00000800, M801 = 0x00000801, M802 = 0x00000802;

    static (Sandbox S, string Dir) Setup()
    {
        var s = new Sandbox();
        var dir = s.Sub("data");
        Master(dir);
        return (s, dir);
    }

    [Fact]
    public void Clean_override_reports_nothing()
    {
        var (s, dir) = Setup();
        using var _ = s;
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null, Top("MISC", Misc(M802, 0)));
        var r = GapCheck.Check(p, [dir]);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
        Assert.Equal(["GapP.esm: 0 own records, 1 overrides, 1 compared with their master's copy", "  0 error(s), 0 warning(s)"], r.Lines());
    }

    [Fact]
    public void Dropped_field_is_a_warning()
    {
        var (s, dir) = Setup();
        using var _ = s;
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null, Top("MISC", Misc(M800, 0, withFull: false), Misc(0x01000900, 0)));
        var r = GapCheck.Check(p, [dir]);
        Assert.Empty(r.Errors);
        Assert.Equal(["MISC 00000800 (GapM.esm GAP_Misc800): fields the master has but this plugin lacks: FULL"], r.Warnings);
        Assert.Equal(1, r.Own);
    }

    [Fact]
    public void Partial_form_missing_a_field_is_not_reported()
    {
        var (s, dir) = Setup();
        using var _ = s;
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null, Top("MISC", Misc(M801, 0x4000, withFull: false)));
        var r = GapCheck.Check(p, [dir]);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
        Assert.Equal(1, r.Compared);
    }

    [Fact]
    public void Overridden_temporary_reference_without_ONAM_is_an_error()
    {
        var (s, dir) = Setup();
        using var _ = s;
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null, CellWithTemp(CellRec, 0x803, RefRec));
        var r = GapCheck.Check(p, [dir]);
        Assert.Equal(["ONAM: 1 overridden temporary reference(s) of full masters not listed in ONAM (e.g. 00000804); the game ignores those edits"], r.Errors);
        Assert.Empty(r.Warnings);

        // listed in ONAM: fine
        var ok = Write(s.Sub("ok"), "GapP.esm", Flags.Master, ["GapM.esm"], [0x804], CellWithTemp(CellRec, 0x803, RefRec));
        Assert.Empty(GapCheck.Check(ok, [s.Sub("ok"), dir]).Errors);
    }

    [Fact]
    public void Temporary_reference_of_a_light_master_needs_no_ONAM()
    {
        using var s = new Sandbox();
        var dir = s.Sub("data");
        Write(dir, "GapL.esm", Flags.Master | Flags.Light, [], null, CellWithTemp(CellRec, 0x803, RefRec));
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapL.esm"], null, CellWithTemp(CellRec, 0x803, RefRec));
        Assert.Empty(GapCheck.Check(p, [dir]).Errors);
    }

    [Fact]
    public void Known_Mutagen_losses_are_errors()
    {
        using var s = new Sandbox();
        var dir = s.Sub("data");
        byte[] Molm(params uint[] ids) => [.. BitConverter.GetBytes((ushort)ids.Length), .. ids.SelectMany(BitConverter.GetBytes)];
        Write(dir, "GapM.esm", Flags.Master, [], null,
            Top("HAZD", Record("HAZD", 0x810, 0, Field("EDID", Z("GAP_Hazard")), Field("OPDS", new byte[4]), Field("DNAM", new byte[4]))),
            Top("STAT", Record("STAT", 0x811, 0, Field("EDID", Z("GAP_Static")), Field("MOLM", Molm(1)), Field("MOLM", Molm(2)))));
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null,
            Top("HAZD", Record("HAZD", 0x810, 0, Field("EDID", Z("GAP_Hazard")), Field("DNAM", new byte[4]))),
            Top("STAT", Record("STAT", 0x811, 0, Field("EDID", Z("GAP_Static")), Field("MOLM", Molm(1)))));
        var r = GapCheck.Check(p, [dir]);
        Assert.Equal([
            "HAZD 00000810 (GapM.esm GAP_Hazard): OPDS dropped (Mutagen's hazard has no OPDS field)",
            "STAT 00000811 (GapM.esm GAP_Static): 1 MOLM material swap(s) dropped (master 2, plugin 1)",
        ], r.Errors);
        Assert.Empty(r.Warnings);   // OPDS and MOLM aren't reported twice
    }

    [Fact]
    public void Missing_master_throws_and_the_release_step_warns()
    {
        using var s = new Sandbox();
        var dir = s.Sub("zip");
        var p = Write(dir, "GapP.esm", Flags.Master, ["NoSuch.esm"], null, Top("MISC", Misc(M800, 0)));
        Assert.Throws<GapCheck.MasterNotFoundException>(() => GapCheck.Check(p, [dir]));

        var data = s.Sub("gamedata");
        var ctx = new ReleaseContext
        {
            Options = new ReleaseOptions { DataDir = data },
            Packages = [new ReleasePackage { ZipPath = Path.Combine(s.Dir, "GapP-0.1.0.zip"), Root = dir, Files = ["gapp.esm"], Plugins = [p], Archives = [] }],
            Work = s.Sub("work"),
        };
        var f = Assert.Single(GapCheck.Run(ctx));
        Assert.Equal(Level.Warning, f.Level);
        Assert.Equal("mutagen-gap", f.Rule);
        Assert.StartsWith("gap check could not run on GapP.esm: master NoSuch.esm not found", f.Text);
    }

    [Fact]
    public void Release_step_finds_masters_in_data_and_reports_levels()
    {
        using var s = new Sandbox();
        var data = s.Sub("gamedata");
        Master(data);
        var dir = s.Sub("zip");
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null,
            Top("MISC", Misc(M800, 0, withFull: false)), CellWithTemp(CellRec, 0x803, RefRec));
        var ctx = new ReleaseContext
        {
            Options = new ReleaseOptions { DataDir = data },
            Packages = [new ReleasePackage { ZipPath = Path.Combine(s.Dir, "GapP-0.1.0.zip"), Root = dir, Files = ["gapp.esm"], Plugins = [p], Archives = [] }],
            Work = s.Sub("work"),
        };
        var f = GapCheck.Run(ctx).ToList();
        Assert.Equal([Level.Problem, Level.Warning, Level.Note], f.Select(x => x.Level));
        Assert.All(f, x => Assert.Equal(RuleKind.Engine, x.Kind));
        Assert.StartsWith("GapP.esm: ONAM: 1 overridden", f[0].Text);
        Assert.Equal("GapP.esm: MISC 00000800 (GapM.esm GAP_Misc800): fields the master has but this plugin lacks: FULL", f[1].Text);
        Assert.Equal("GapP.esm: 0 own records, 3 overrides, 3 compared with their master's copy", f[2].Text);
    }

    // ---- the documented command, "sf-mod-check gap" (0.15.0) ----

    static List<string> Out(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l != "").ToList();

    [Fact]
    public void Gap_command_clean_plugin_is_ok_with_exit_0()
    {
        var (s, dir) = Setup();
        using var _ = s;
        var plugDir = s.Sub("build");
        var p = Write(plugDir, "GapP.esm", Flags.Master, ["GapM.esm"], null, Top("MISC", Misc(M800, 0, withFull: false)));
        var (code, stdout, stderr) = ExeTests.RunSplit("gap", p, "--data", s.Sub("nodata"), "--masters-dir", dir);
        Assert.Equal(0, code);
        Assert.Equal([
            "GapP.esm: 0 own records, 1 overrides, 1 compared with their master's copy",
            "  WARNING MISC 00000800 (GapM.esm GAP_Misc800): fields the master has but this plugin lacks: FULL",
            "  0 error(s), 1 warning(s)",
            "RESULT: OK (0 errors, 1 warning)",
        ], Out(stdout));
        Assert.Contains("sf-mod-check ", stderr);   // the byline
    }

    [Fact]
    public void Gap_command_error_is_fail_with_exit_1_and_quiet_drops_warnings()
    {
        var (s, dir) = Setup();
        using var _ = s;
        var p = Write(dir, "GapP.esm", Flags.Master, ["GapM.esm"], null,
            Top("MISC", Misc(M800, 0, withFull: false)), CellWithTemp(CellRec, 0x803, RefRec));
        var (code, stdout, _) = ExeTests.RunSplit("gap", p, "--data", s.Sub("nodata"));
        Assert.Equal(1, code);
        var lines = Out(stdout);
        Assert.Contains(lines, l => l.StartsWith("  ERROR   ONAM: 1 overridden temporary reference(s)"));
        Assert.Contains(lines, l => l.StartsWith("  WARNING MISC 00000800"));
        Assert.Equal("RESULT: FAIL (1 error, 1 warning)", lines[^1]);

        var (qcode, qout, qerr) = ExeTests.RunSplit("gap", p, "--data", s.Sub("nodata"), "--quiet");
        Assert.Equal(1, qcode);
        Assert.DoesNotContain(Out(qout), l => l.StartsWith("  WARNING "));
        Assert.Equal("RESULT: FAIL (1 error, 1 warning)", Out(qout)[^1]);
        Assert.DoesNotContain("sf-mod-check ", qerr);
    }

    [Fact]
    public void Gap_command_missing_master_cannot_check_with_exit_2()
    {
        using var s = new Sandbox();
        var p = Write(s.Sub("zip"), "GapP.esm", Flags.Master, ["NoSuch.esm"], null, Top("MISC", Misc(M800, 0)));
        var (code, stdout, stderr) = ExeTests.RunSplit("gap", p, "--data", s.Sub("nodata"));
        Assert.Equal(2, code);
        var lines = Out(stdout);
        Assert.StartsWith("master NoSuch.esm not found in ", lines[0]);   // the line tools\gap_parity.py looks for
        Assert.Contains(" or ", lines[0]);   // the folders read apart even when they hold spaces (bug hunt, 2026-11-23)
        Assert.Equal("RESULT: CANNOT CHECK (master NoSuch.esm not found)", lines[^1]);
        Assert.Contains("--masters-dir", stderr);
    }

    [Theory]
    [InlineData("RESULT: CANNOT CHECK (the plugin doesn't exist)", "doesn't exist", "gap", "no-such-plugin.esm")]
    [InlineData("RESULT: CANNOT CHECK (bad arguments; see the message above)", "give the plugin to check", "gap")]
    [InlineData("RESULT: CANNOT CHECK (bad arguments; see the message above)", "unknown option --nope", "gap", "a.esm", "--nope")]
    [InlineData("RESULT: CANNOT CHECK (bad arguments; see the message above)", "one plugin per run", "gap", "a.esm", "b.esm")]
    [InlineData("RESULT: CANNOT CHECK (bad arguments; see the message above)", "--masters-dir needs a folder", "gap", "a.esm", "--masters-dir")]
    public void Gap_command_bad_input_exits_2(string result, string message, params string[] args)
    {
        var (code, stdout, stderr) = ExeTests.RunSplit(args);
        Assert.Equal(2, code);
        Assert.Equal(result, ExeTests.ResultLine(stdout));
        Assert.Contains(message, stderr);
    }

    [Fact]
    public void Gap_command_help_exits_0_with_no_result_line()
    {
        foreach (var h in new[] { "--help", "-h" })
        {
            var (code, stdout, _) = ExeTests.RunSplit("gap", h);
            Assert.Equal(0, code);
            Assert.StartsWith("usage: sf-mod-check gap <plugin>", stdout);
            Assert.Empty(stdout.Split('\n').Where(l => l.StartsWith("RESULT: ")));
        }
        Assert.Contains("sf-mod-check gap <plugin>", ExeTests.RunSplit("--help").Stdout);   // listed in the top-level usage
    }

    [Fact]
    public void Every_gap_option_is_in_the_usage_readme_agents_and_changelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "GapCheck.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\GapCheck.cs not found above " + AppContext.BaseDirectory);
        var options = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(dir!.FullName, "src", "GapCheck.cs")), @"case ""(--[a-z-]+)"":")
            .Select(m => m.Groups[1].Value).Where(o => o != "--help").ToHashSet();
        Assert.Equal(new[] { "--data", "--log", "--masters-dir", "--quiet", "--verbose" }, options.Order());
        var readme = File.ReadAllText(Path.Combine(dir.FullName, "README.md"));
        var agents = File.ReadAllText(Path.Combine(dir.FullName, "AGENTS.md"));
        var changelog = File.ReadAllText(Path.Combine(dir.FullName, "CHANGELOG.md"));
        Assert.Contains("## Check a plugin for Mutagen losses: `sf-mod-check gap`", readme);
        Assert.Contains("sf-mod-check gap ", agents);
        Assert.Contains("`sf-mod-check gap <plugin>`", changelog);
        foreach (var text in new[] { readme, agents })
            Assert.Contains("`RESULT: OK (0 errors, n warnings)`", text.ReplaceLineEndings(" "));
        foreach (var o in options)
        {
            Assert.Contains(o, GapCheck.Usage);
            Assert.True(readme.Contains("`" + o), $"README.md doesn't mention gap's {o}");
            Assert.True(agents.Contains("`" + o) || agents.Contains(" " + o + " "), $"AGENTS.md doesn't mention gap's {o}");
        }
    }
}
