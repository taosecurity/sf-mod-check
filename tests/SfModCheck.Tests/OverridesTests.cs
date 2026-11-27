// sf-mod-check 0.26.0: the overrides command (OverridesCommand.cs) and more than one --plugin (LoadOrderInput.cs,
// A.8). All plugins are made up: a made-up Starfield.esm and SFBGS003.esm stand in for the official files.
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace SfModCheck.Tests;

public class OverridesTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Data\: Starfield.esm (keywords S_A, S_B, S_C; misc item S_Misc) and SFBGS003.esm (overrides S_C).
    /// mods\: A.esm (overrides S_A, S_B, S_Misc; adds A_New), B.esm (masters Starfield.esm and A.esm; overrides S_A,
    /// S_Misc and A_New), C.esm (overrides S_A) and D.esm (master Missing.esm, which exists nowhere).</summary>
    sealed class Set : Sandbox
    {
        public readonly string Data, ModDir, Tmp;
        public readonly FormKey SA, SB, SC, SMisc, ANew;

        public Set()
        {
            Data = Sub("Data");
            ModDir = Sub("mods");
            Tmp = Sub("tmp");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            SA = s.Keywords.AddNew("S_A").FormKey;
            SB = s.Keywords.AddNew("S_B").FormKey;
            SC = s.Keywords.AddNew("S_C").FormKey;
            SMisc = s.MiscItems.AddNew("S_Misc").FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(s, Data, g, NoGap);
            using (var g = new GameData(Data))
            {
                var sf = g.Open("Starfield.esm");
                var u = Plugin.New("SFBGS003.esm", PluginStyle.Full, "test", "made-up official update");
                u.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_C"));
                PluginWriter.Write(u, Data, g, NoGap);
            }
            using (var g = new GameData(Data, [ModDir]))
            {
                var sf = g.Open("Starfield.esm");
                var a = Plugin.New("A.esm", PluginStyle.Full, "test", "made-up mod A");
                foreach (var k in sf.Keywords.Where(k => k.EditorID is "S_A" or "S_B")) a.Keywords.GetOrAddAsOverride(k);
                a.MiscItems.GetOrAddAsOverride(sf.MiscItems.First());
                ANew = a.Keywords.AddNew("A_New").FormKey;
                PluginWriter.Write(a, ModDir, g, NoGap);
                var c = Plugin.New("C.esm", PluginStyle.Full, "test", "made-up mod C");
                c.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_A"));
                PluginWriter.Write(c, ModDir, g, NoGap);
            }
            using (var g = new GameData(Data, [ModDir]))
            {
                var sf = g.Open("Starfield.esm");
                var am = g.Open("A.esm");
                var b = Plugin.New("B.esm", PluginStyle.Full, "test", "made-up mod B");
                b.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_A"));
                b.MiscItems.GetOrAddAsOverride(sf.MiscItems.First());
                b.Keywords.GetOrAddAsOverride(am.Keywords.First(k => k.EditorID == "A_New"));
                PluginWriter.Write(b, ModDir, g, NoGap);
            }
            // D.esm's master Missing.esm is written into a folder of its own, which no run is given
            var hidden = Sub("hidden");
            var miss = Plugin.New("Missing.esm", PluginStyle.Full, "test", "made-up missing master");
            miss.Keywords.AddNew("M_Kw");
            using (var g = new GameData(hidden)) PluginWriter.Write(miss, hidden, g, NoGap);
            using (var g = new GameData(hidden))
            {
                var mm = g.Open("Missing.esm");
                var d = Plugin.New("D.esm", PluginStyle.Full, "test", "made-up mod D");
                d.Keywords.GetOrAddAsOverride(mm.Keywords.First());
                PluginWriter.Write(d, ModDir, g, NoGap);
            }
        }

        public string PluginsTxt(params string[] lines)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, lines);
            return txt;
        }

        public string File_(string name, params string[] lines)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllLines(path, lines);
            return path;
        }

        public string Zip(string name, params string[] plugins)
        {
            var zip = Path.Combine(Sub("zips"), name);
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var p in plugins) z.CreateEntryFromFile(Path.Combine(ModDir, p), p);
            return zip;
        }

        /// <summary>overrides over Data and A, B and C (Plugins.txt), mods\ as --extra-data.</summary>
        public (int Code, string Stdout, string Stderr) Overrides(params string[] extra) =>
            Run(new[] { "overrides", "--data", Data, "--plugins", PluginsTxt("*A.esm", "*B.esm", "*C.esm"), "--extra-data", ModDir }.Concat(extra).ToArray());

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
    }

    static List<string> Lines(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>The record line of key (it starts with the FormKey, two spaces in).</summary>
    static string? LineOf(string stdout, FormKey key) => Lines(stdout).FirstOrDefault(l => l.StartsWith($"  {key}"));

    [Fact]
    public void Records_two_mods_hold_are_listed_with_the_winner_and_one_mod_alone_or_officials_only_are_not()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Overrides();
        Assert.True(code == 0, stdout + stderr);
        // two or more mods overriding one vanilla record: the holders in load order, the last wins
        Assert.Equal($"  {s.SA} S_A: A.esm, B.esm, C.esm (wins)", LineOf(stdout, s.SA));
        Assert.Equal($"  {s.SMisc} S_Misc: A.esm, B.esm (wins)", LineOf(stdout, s.SMisc));
        // a mod's own new record that a second mod overrides counts both
        Assert.Equal($"  {s.ANew} A_New: A.esm (adds), B.esm (wins)", LineOf(stdout, s.ANew));
        // one mod alone (S_B: A only) and an official-only override (S_C: SFBGS003) are not listed
        Assert.Null(LineOf(stdout, s.SB));
        Assert.Null(LineOf(stdout, s.SC));
        Assert.Contains("KYWD (2 records)", stdout);
        Assert.Contains("MISC (1 record)", stdout);
        Assert.Contains("RESULT: 3 records held by 2 or more non-official plugins (KYWD 2, MISC 1)", stdout);
        // the types are sorted: KYWD before MISC
        Assert.True(stdout.IndexOf("KYWD (", StringComparison.Ordinal) < stdout.IndexOf("MISC (", StringComparison.Ordinal), stdout);
    }

    [Fact]
    public void Min_and_type_filter_and_an_unknown_type_is_refused()
    {
        using var s = new Set();
        var (c3, o3, e3) = s.Overrides("--min", "3");
        Assert.True(c3 == 0, o3 + e3);
        Assert.NotNull(LineOf(o3, s.SA));
        Assert.Null(LineOf(o3, s.ANew));
        Assert.Contains("RESULT: 1 record held by 3 or more non-official plugins (KYWD 1)", o3);

        var (cm, om, em) = s.Overrides("--type", "misc");
        Assert.True(cm == 0, om + em);
        Assert.NotNull(LineOf(om, s.SMisc));
        Assert.Null(LineOf(om, s.SA));
        Assert.Contains("RESULT: 1 record held by 2 or more non-official plugins (MISC 1)", om);

        var (cb, ob, eb) = s.Overrides("--type", "KYWD,MISC");
        Assert.True(cb == 0, ob + eb);
        Assert.Contains("RESULT: 3 records", ob);

        // none matched: exit 1
        var (cn, on, en) = s.Overrides("--type", "MISC", "--min", "3");
        Assert.True(cn == 1, on + en);
        Assert.Contains("RESULT: no record is held by 3 or more non-official plugins", on);

        // an unknown type: exit 2 with the known types
        var (cx, ox, ex) = s.Overrides("--type", "KYWD,ZZZZ");
        Assert.Equal(2, cx);
        Assert.Contains("\"ZZZZ\" isn't a Starfield record type. Known types:", ex);
        Assert.Contains("KYWD", ex);
        Assert.Contains("RESULT: CANNOT CHECK (unknown record type ZZZZ)", ox);

        // --min 0 is refused
        var (c0, o0, _) = s.Overrides("--min", "0");
        Assert.Equal(2, c0);
        Assert.Contains("RESULT: CANNOT CHECK", o0);
    }

    [Fact]
    public void Max_cuts_the_list_with_the_and_more_line()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Overrides("--max", "1");
        Assert.True(code == 0, stdout + stderr);
        Assert.Single(Lines(stdout), l => System.Text.RegularExpressions.Regex.IsMatch(l, "^  [0-9A-F]{6}:"));
        Assert.Contains("... and 2 more (raise --max to see them", stdout);
        Assert.Contains("RESULT: 3 records", stdout);
    }

    [Fact]
    public void Records_file_lists_the_whole_chain_with_officials_marked_and_reports_not_found_and_bad_lines()
    {
        using var s = new Set();
        var list = s.File_("who.txt", "# records to look up", "", s.SA.ToString(), s.SC.ToString(), "000999:Starfield.esm", s.SB.ToString());
        var (code, stdout, stderr) = s.Overrides("--records", list);
        Assert.True(code == 0, stdout + stderr);
        Assert.Equal($"  {s.SA} S_A: Starfield.esm (adds, official), A.esm, B.esm, C.esm (wins)", LineOf(stdout, s.SA));
        // --min defaults to 1 with --records: S_B (A only) is listed
        Assert.Equal($"  {s.SB} S_B: Starfield.esm (adds, official), A.esm (wins)", LineOf(stdout, s.SB));
        // only non-official holders count: S_C (official copies only) is not listed
        Assert.Null(LineOf(stdout, s.SC));
        Assert.Contains("not found: 000999:Starfield.esm (line 5): no plugin holds 000999:Starfield.esm", stdout);
        Assert.Contains("; 1 not found", stdout);

        // a line that is neither a FormKey nor a FormID: refused with its line number
        var bad = s.File_("bad.txt", s.SA.ToString(), "not a record");
        var (cb, ob, eb) = s.Overrides("--records", bad);
        Assert.Equal(2, cb);
        Assert.Contains("line 2: \"not a record\" is neither a FormKey", eb);
        Assert.Contains("RESULT: CANNOT CHECK (--records line 2 isn't a FormKey or FormID)", ob);
    }

    [Fact]
    public void Several_plugin_archives_a_later_one_wins_a_name_clash_and_one_with_a_missing_master_is_left_out()
    {
        using var s = new Set();
        var a1 = s.Zip("A-1.0.zip", "A.esm");
        var b = s.Zip("B-1.0.zip", "B.esm");
        var d = s.Zip("D-1.0.zip", "D.esm");
        var a2 = s.Zip("A-1.1.zip", "A.esm");
        var (code, stdout, stderr) = s.Run("overrides", "--data", s.Data, "--plugin", a1, "--plugin", b, "--plugin", d, "--plugin", a2);
        Assert.True(code == 0, stdout + stderr);
        // B's master A.esm comes from A-1.1.zip, given later: masters first, so A.esm loads before B.esm
        Assert.Equal($"  {s.SA} S_A: A.esm, B.esm (wins)", LineOf(stdout, s.SA));
        Assert.Equal($"  {s.ANew} A_New: A.esm (adds), B.esm (wins)", LineOf(stdout, s.ANew));
        Assert.Contains("note: --plugin A-1.0.zip left out: it holds A.esm, as A-1.1.zip (given later) does, so the later one is used", stderr);
        Assert.Contains("note: --plugin D-1.0.zip left out: D.esm's master Missing.esm wasn't found", stderr);
        Assert.Contains("left out: A-1.0.zip:", stdout);
        Assert.Contains("left out: D-1.0.zip:", stdout);
        Assert.Contains("RESULT: 3 records held by 2 or more non-official plugins", stdout);
        Assert.False(Directory.EnumerateFileSystemEntries(s.Tmp).Any(), "the temp folder was left behind");

        // a single --plugin with a missing master still stops the run
        var (c1, o1, e1) = s.Run("overrides", "--data", s.Data, "--plugin", Path.Combine(s.ModDir, "D.esm"));
        Assert.True(c1 == 2, o1 + e1);
        Assert.Contains("master Missing.esm not found", o1);

        // every input left out: exit 2
        var (c2, o2, e2) = s.Run("overrides", "--data", s.Data, "--plugin", d, "--plugin", Path.Combine(s.ModDir, "D.esm"));
        Assert.True(c2 == 2, o2 + e2);
        Assert.Contains("every --plugin input was left out", o2);
    }

    /// <summary>Review round 1: a kept input's master found in a left-out input's folder is still loaded from there.
    /// AD.zip holds A.esm and D.esm (D's master is missing, so AD.zip is left out); B.esm, given alone, needs A.esm.</summary>
    [Fact]
    public void A_master_from_a_left_out_input_is_still_found()
    {
        using var s = new Set();
        var ad = s.Zip("AD-1.0.zip", "A.esm", "D.esm");
        var alone = s.Sub("alone");
        File.Copy(Path.Combine(s.ModDir, "B.esm"), Path.Combine(alone, "B.esm"));
        File.Copy(Path.Combine(s.ModDir, "C.esm"), Path.Combine(alone, "C.esm"));
        var (code, stdout, stderr) = s.Run("overrides", "--data", s.Data, "--plugin", ad,
            "--plugin", Path.Combine(alone, "B.esm"), "--plugin", Path.Combine(alone, "C.esm"));
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("note: --plugin AD-1.0.zip left out: D.esm's master Missing.esm wasn't found", stderr);
        Assert.DoesNotContain("--plugin B.esm left out", stderr);
        Assert.Equal($"  {s.SA} S_A: A.esm, B.esm, C.esm (wins)", LineOf(stdout, s.SA));
        Assert.Equal($"  {s.ANew} A_New: A.esm (adds), B.esm (wins)", LineOf(stdout, s.ANew));
        Assert.False(Directory.EnumerateFileSystemEntries(s.Tmp).Any(), "the temp folder was left behind");
    }

    [Fact]
    public void Json_shapes_for_found_none_and_cannot_run()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Overrides("--json");
        Assert.True(code == 0, stdout + stderr);
        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("sf-mod-check overrides", root.GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("loadOrder").GetProperty("plugins").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("loadOrder").GetProperty("source").ValueKind);
        var filter = root.GetProperty("filter");
        Assert.Equal(JsonValueKind.Null, filter.GetProperty("types").ValueKind);
        Assert.Equal(2, filter.GetProperty("min").GetInt32());
        Assert.Equal(JsonValueKind.Null, filter.GetProperty("records").ValueKind);
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("shown").GetInt32());
        Assert.Equal(2, root.GetProperty("byType").GetProperty("KYWD").GetInt32());
        Assert.Equal(1, root.GetProperty("byType").GetProperty("MISC").GetInt32());
        var rec = root.GetProperty("records").EnumerateArray().Single(r => r.GetProperty("formKey").GetString() == s.ANew.ToString());
        Assert.Equal("A_New", rec.GetProperty("editorId").GetString());
        Assert.Equal("KYWD", rec.GetProperty("type").GetString());
        Assert.Equal("A.esm", rec.GetProperty("origin").GetString());
        Assert.Equal("B.esm", rec.GetProperty("winner").GetString());
        var holders = rec.GetProperty("holders").EnumerateArray().ToList();
        Assert.Equal(["A.esm", "B.esm"], holders.Select(h => h.GetProperty("plugin").GetString()));
        Assert.True(holders[0].GetProperty("adds").GetBoolean());
        Assert.False(holders[1].GetProperty("adds").GetBoolean());
        Assert.False(holders[0].GetProperty("official").GetBoolean());
        foreach (var k in new[] { "notFound", "leftOut", "unread" }) Assert.Equal(JsonValueKind.Array, root.GetProperty(k).ValueKind);
        Assert.Equal("found", root.GetProperty("result").GetString());

        var (cn, on, en) = s.Overrides("--json", "--type", "MISC", "--min", "3");
        Assert.True(cn == 1, on + en);
        var none = JsonDocument.Parse(on).RootElement;
        Assert.Equal("none", none.GetProperty("result").GetString());
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal(["MISC"], none.GetProperty("filter").GetProperty("types").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(3, none.GetProperty("filter").GetProperty("min").GetInt32());

        var (cx, ox, ex) = s.Overrides("--json", "--type", "ZZZZ");
        Assert.True(cx == 2, ox + ex);
        var cant = JsonDocument.Parse(ox).RootElement;
        Assert.Equal("cannot run", cant.GetProperty("result").GetString());
        Assert.Equal("sf-mod-check overrides", cant.GetProperty("tool").GetString());
        Assert.Contains("ZZZZ", cant.GetProperty("error").GetString());
    }

    /// <summary>Plan step 5: every key of B.7's JSON is there, with no others, each with the right kind; filled with
    /// --records (official holders, a FormKey not found) and several --plugin archives (left-out inputs).</summary>
    [Fact]
    public void Json_has_every_key_of_the_design_with_its_kind()
    {
        using var s = new Set();
        var list = s.File_("who.txt", s.SA.ToString(), "000999:Starfield.esm");
        var (code, stdout, stderr) = s.Run("overrides", "--data", s.Data, "--plugin", s.Zip("A-1.0.zip", "A.esm"), "--plugin", s.Zip("B-1.0.zip", "B.esm"),
                                           "--plugin", s.Zip("D-1.0.zip", "D.esm"), "--records", list, "--type", "KYWD", "--json");
        Assert.True(code == 0, stdout + stderr);
        var root = JsonShape.Exactly(JsonDocument.Parse(stdout).RootElement, "root",
            "tool:string", "version:string", "loadOrder:object", "filter:object", "total:number", "shown:number", "byType:object",
            "records:array", "notFound:array", "leftOut:array", "unread:array", "result:string");
        JsonShape.Exactly(root.GetProperty("loadOrder"), "loadOrder", "source:string", "plugins:number");
        var filter = JsonShape.Exactly(root.GetProperty("filter"), "filter", "types:array?", "min:number", "records:string?");
        Assert.Equal("who.txt", filter.GetProperty("records").GetString());
        JsonShape.AllOf(filter.GetProperty("types"), "string", "filter.types");
        foreach (var t in root.GetProperty("byType").EnumerateObject()) Assert.Equal(JsonValueKind.Number, t.Value.ValueKind);
        var records = root.GetProperty("records").EnumerateArray().ToList();
        Assert.NotEmpty(records);
        foreach (var r in records)
        {
            JsonShape.Exactly(r, "records[]", "formKey:string", "editorId:string?", "type:string", "origin:string", "holders:array", "winner:string");
            foreach (var h in r.GetProperty("holders").EnumerateArray()) JsonShape.Exactly(h, "holders[]", "plugin:string", "adds:bool", "official:bool");
        }
        // the official copy is in the chain, marked
        var sa = records.Single(r => r.GetProperty("formKey").GetString() == s.SA.ToString());
        Assert.Contains(sa.GetProperty("holders").EnumerateArray(), h => h.GetProperty("plugin").GetString() == "Starfield.esm" && h.GetProperty("official").GetBoolean());
        JsonShape.AllOf(root.GetProperty("notFound"), "string", "notFound");
        Assert.Contains(root.GetProperty("notFound").EnumerateArray(), n => n.GetString()!.Contains("000999:Starfield.esm"));
        var leftOut = root.GetProperty("leftOut").EnumerateArray().ToList();
        Assert.Contains(leftOut, l => l.GetProperty("input").GetString() == "D-1.0.zip");
        foreach (var l in leftOut) JsonShape.Exactly(l, "leftOut[]", "input:string", "why:string");
        JsonShape.AllOf(root.GetProperty("unread"), "string", "unread");

        // cannot run: exactly tool, version, result and error
        var (cx, ox, ex) = s.Run("overrides", "--data", s.Data, "--plugin", Path.Combine(s.ModDir, "D.esm"), "--json");
        Assert.True(cx == 2, ox + ex);
        JsonShape.Exactly(JsonDocument.Parse(ox).RootElement, "cannot run", "tool:string", "version:string", "result:string", "error:string");
        Assert.False(Directory.EnumerateFileSystemEntries(s.Tmp).Any(), "the temp folder was left behind");
    }

    [Fact]
    public void Help_exits_0_and_the_top_level_help_names_overrides()
    {
        using var s = new Set();
        var (code, stdout, _) = s.Run("overrides", "--help");
        Assert.Equal(0, code);
        Assert.Contains("usage: sf-mod-check overrides", stdout);
        var (_, top, topErr) = s.Run("--help");
        Assert.Contains("sf-mod-check overrides", top + topErr);
    }
}
