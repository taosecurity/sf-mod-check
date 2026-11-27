// sf-mod-check 0.26.0: the summary command (SummaryCommand.cs), parts C.1 to C.4 (header and masters, records per type,
// ITMs, changed fields). All plugins are made up: a made-up Starfield.esm stands in for the official file.
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace SfModCheck.Tests;

public class SummaryTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Data\Starfield.esm: keywords S_A and S_B, misc items S_Misc (value 5) and S_Part (named, value 7).
    /// mods\Mod.esm: adds M_New; overrides S_A unchanged (an ITM), S_B deleted, S_Misc with value 50, and S_Part as a
    /// partial form with no name and value 70. mods\Mod2.esm (masters Starfield.esm and Mod.esm): adds M2_New and
    /// overrides M_New unchanged (an ITM).</summary>
    sealed class Set : Sandbox
    {
        public readonly string Data, ModDir, Tmp;
        public readonly FormKey SA, SB, SMisc, SPart, MNew;

        public Set()
        {
            Data = Sub("Data");
            ModDir = Sub("mods");
            Tmp = Sub("tmp");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            SA = s.Keywords.AddNew("S_A").FormKey;
            SB = s.Keywords.AddNew("S_B").FormKey;
            var misc = s.MiscItems.AddNew("S_Misc");
            misc.Value = 5;
            SMisc = misc.FormKey;
            var part = s.MiscItems.AddNew("S_Part");
            part.Name = "Part";
            part.Value = 7;
            SPart = part.FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(s, Data, g, NoGap);
            using (var g = new GameData(Data, [ModDir]))
            {
                var sf = g.Open("Starfield.esm");
                var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up mod");
                MNew = m.Keywords.AddNew("M_New").FormKey;
                m.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_A"));
                m.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_B")).IsDeleted = true;
                m.MiscItems.GetOrAddAsOverride(sf.MiscItems.First(k => k.EditorID == "S_Misc")).Value = 50;
                var p = m.MiscItems.GetOrAddAsOverride(sf.MiscItems.First(k => k.EditorID == "S_Part"));
                p.Name = null;
                p.Value = 70;
                p.MajorRecordFlagsRaw |= 0x4000;   // a partial form: the empty name keeps the earlier value
                PluginWriter.Write(m, ModDir, g, NoGap);
            }
            using (var g = new GameData(Data, [ModDir]))
            {
                var mod = g.Open("Mod.esm");
                var m2 = Plugin.New("Mod2.esm", PluginStyle.Full, "test", "made-up second mod");
                m2.Keywords.AddNew("M2_New");
                m2.Keywords.GetOrAddAsOverride(mod.Keywords.First(k => k.EditorID == "M_New"));
                PluginWriter.Write(m2, ModDir, g, NoGap);
            }
        }

        public string Zip(string name, params string[] plugins)
        {
            var zip = Path.Combine(Sub("zips"), name);
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var p in plugins) z.CreateEntryFromFile(Path.Combine(ModDir, p), "Data/" + p);
            return zip;
        }

        public (int Code, string Stdout, string Stderr) Run(params string[] args)
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sf-mod-check.exe" : "sf-mod-check");
            var psi = File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(dir, "sf-mod-check.dll") } };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["SFMODCHECK_CACHE_DIR"] = Path.Combine(TestSetup.RunDir, "exe-cache");
            psi.Environment["TMP"] = psi.Environment["TEMP"] = psi.Environment["TMPDIR"] = Tmp;
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(120_000), "sf-mod-check didn't finish in 2 minutes");
            return (p.ExitCode, stdout.Result, stderr.Result);
        }

        public (int Code, string Stdout, string Stderr) Summary(string plugin, params string[] extra) =>
            Run(new[] { "summary", plugin, "--data", Data }.Concat(extra).ToArray());
    }

    static List<string> Lines(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    [Fact]
    public void Counts_per_type_with_deleted_overrides_and_the_masters_match_the_made_up_plugin()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"));
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("Mod.esm (full plugin)", lines);
        Assert.Contains("  Masters (1):", lines);
        Assert.Contains(lines, l => l.StartsWith("    Starfield.esm (full), in ") && l.EndsWith("Data"));
        Assert.Contains("  Records: 5 records (1 new, 4 overrides, 1 of them deleted)", lines);
        Assert.Contains("    KYWD: 1 new, 2 overrides (1 deleted), 1 ITM", lines);
        Assert.Contains("    MISC: 0 new, 2 overrides", lines);
        Assert.Contains("RESULT: Mod.esm: 5 records (1 new, 4 overrides, 1 deleted), 1 ITM, 1 field changed in 2 overrides", stdout);
    }

    [Fact]
    public void An_itm_is_counted_and_named_and_check_finds_the_same_itm()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"));
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("  ITMs (overrides identical to their master version): 1", lines);
        Assert.Contains("    KYWD (1)", lines);
        Assert.Contains($"      {s.SA} S_A", lines);
        // the deleted override is not an ITM, and its empty fields are not changes
        Assert.DoesNotContain(lines, l => l.Contains(s.SB.ToString()));

        // check (FindItms with no callback) finds the same one ITM
        var (_, co, ce) = s.Run("check", Path.Combine(s.ModDir, "Mod.esm"), "--data", s.Data, "--no-assets");
        var itmLines = Lines(co + ce).Where(l => l.Contains("identical to the master version it overrides")).ToList();
        Assert.True(itmLines.Count == 1, co + ce);
        Assert.Contains("S_A", itmLines[0]);
    }

    [Fact]
    public void A_changed_field_is_counted_with_its_values_and_a_partial_forms_empty_field_is_not()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"));
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("  Changed fields: 1 field changed in 2 overrides", lines);
        Assert.Contains("    MISC Value: 2 overrides", lines);
        Assert.Contains($"      {s.SMisc} S_Misc: 5 -> 50", lines);
        Assert.Contains($"      {s.SPart} S_Part: 7 -> 70", lines);
        // the partial form's empty name keeps the master's: not a change; nor is an unchanged field
        Assert.DoesNotContain(lines, l => l.StartsWith("    MISC Name"));
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^    KYWD [A-Za-z]+: "));

        // --max 1 cuts the examples with the "and more" line
        var (c1, o1, e1) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"), "--max", "1");
        Assert.True(c1 == 0, o1 + e1);
        Assert.Contains("      ... and 1 more (raise --max to see them; --max 0 lists them all)", Lines(o1));
    }

    [Fact]
    public void An_archive_with_two_plugins_gives_two_summaries_in_load_order()
    {
        using var s = new Set();
        var zip = s.Zip("Pair-1.0.zip", "Mod2.esm", "Mod.esm");
        var (code, stdout, stderr) = s.Run("summary", zip, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        var first = lines.IndexOf("Mod.esm (full plugin, from Pair-1.0.zip)");
        var second = lines.IndexOf("Mod2.esm (full plugin, from Pair-1.0.zip)");
        Assert.True(first >= 0 && second > first, stdout);
        Assert.Contains("    Mod.esm (full), in Pair-1.0.zip", lines);
        Assert.Contains($"      {s.MNew} M_New", lines);
        Assert.Contains("RESULT: Mod.esm: 5 records (1 new, 4 overrides, 1 deleted), 1 ITM, 1 field changed in 2 overrides; " +
                        "Mod2.esm: 2 records (1 new, 1 override, 0 deleted), 1 ITM, 0 fields changed in 0 overrides", stdout);
        Assert.False(Directory.EnumerateFileSystemEntries(s.Tmp).Any(), "the temp folder was left behind");
    }

    [Fact]
    public void Json_shape_and_the_cannot_run_cases()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"), "--json");
        Assert.True(code == 0, stdout + stderr);
        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("sf-mod-check summary", root.GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("version").ValueKind);
        Assert.Equal("done", root.GetProperty("result").GetString());
        var p = root.GetProperty("plugins").EnumerateArray().Single();
        Assert.Equal("Mod.esm", p.GetProperty("name").GetString());
        Assert.Equal("full", p.GetProperty("style").GetString());
        var master = p.GetProperty("masters").EnumerateArray().Single();
        Assert.Equal("Starfield.esm", master.GetProperty("name").GetString());
        Assert.Equal("full", master.GetProperty("style").GetString());
        Assert.True(master.GetProperty("found").GetBoolean());
        Assert.Equal(5, p.GetProperty("records").GetInt32());
        var kywd = p.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "KYWD");
        Assert.Equal(1, kywd.GetProperty("new").GetInt32());
        Assert.Equal(2, kywd.GetProperty("override").GetInt32());
        Assert.Equal(1, kywd.GetProperty("deleted").GetInt32());
        Assert.Equal(1, kywd.GetProperty("itm").GetInt32());
        var itms = p.GetProperty("itms");
        Assert.Equal(1, itms.GetProperty("count").GetInt32());
        Assert.Equal(0, itms.GetProperty("notCompared").GetInt32());
        Assert.Equal(JsonValueKind.Number, itms.GetProperty("noMaster").ValueKind);
        var ex = itms.GetProperty("examples").EnumerateArray().Single();
        Assert.Equal(s.SA.ToString(), ex.GetProperty("formKey").GetString());
        Assert.Equal("S_A", ex.GetProperty("editorId").GetString());
        Assert.Equal("KYWD", ex.GetProperty("type").GetString());
        var field = p.GetProperty("fields").EnumerateArray().Single();
        Assert.Equal("MISC", field.GetProperty("type").GetString());
        Assert.Equal("Value", field.GetProperty("field").GetString());
        Assert.Equal(2, field.GetProperty("count").GetInt32());
        var fx = field.GetProperty("examples").EnumerateArray().Single(e => e.GetProperty("formKey").GetString() == s.SMisc.ToString());
        Assert.Equal("5", fx.GetProperty("from").GetString());
        Assert.Equal("50", fx.GetProperty("to").GetString());
        Assert.Equal(JsonValueKind.Array, p.GetProperty("unread").ValueKind);

        // can't run: a file that doesn't exist (JSON and text), no input, two inputs, a load-order option
        var (cx, ox, _) = s.Summary(Path.Combine(s.ModDir, "Nope.esm"), "--json");
        Assert.Equal(2, cx);
        var cant = JsonDocument.Parse(ox).RootElement;
        Assert.Equal("cannot run", cant.GetProperty("result").GetString());
        Assert.Equal("sf-mod-check summary", cant.GetProperty("tool").GetString());
        var (c0, o0, _) = s.Run("summary", "--data", s.Data);
        Assert.Equal(2, c0);
        Assert.Contains("RESULT: CANNOT CHECK (no plugin or archive given)", o0);
        var (c2, o2, _) = s.Run("summary", Path.Combine(s.ModDir, "Mod.esm"), Path.Combine(s.ModDir, "Mod2.esm"));
        Assert.Equal(2, c2);
        Assert.Contains("RESULT: CANNOT CHECK (more than one plugin or archive given)", o2);
        var (cp, op, ep) = s.Run("summary", Path.Combine(s.ModDir, "Mod.esm"), "--plugins", "x.txt");
        Assert.Equal(2, cp);
        Assert.Contains("summary doesn't take --plugins", ep);
        Assert.Contains("RESULT: CANNOT CHECK", op);
    }

    [Fact]
    public void Help_exits_0_and_the_top_level_help_names_summary()
    {
        using var s = new Set();
        var (code, stdout, _) = s.Run("summary", "--help");
        Assert.Equal(0, code);
        Assert.Contains("usage: sf-mod-check summary", stdout);
        var (_, top, topErr) = s.Run("--help");
        Assert.Contains("sf-mod-check summary", top + topErr);
    }
}
