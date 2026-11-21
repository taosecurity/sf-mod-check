// The built program: exit codes 0/1/2, the RESULT line and the troubleshooting log. It runs as a separate process, so
// SFMODCHECK_CACHE_DIR points its caches into the test run's temp folder, never the user's real caches.
using System.Diagnostics;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class ExeTests
{
    static (int Code, string Out) Run(params string[] args)
    {
        var (code, stdout, stderr) = RunSplit(args);
        return (code, stdout + stderr);
    }

    internal static (int Code, string Stdout, string Stderr) RunSplit(params string[] args)
    {
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sf-mod-check.exe" : "sf-mod-check");
        var psi = File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(dir, "sf-mod-check.dll") } };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SFMODCHECK_CACHE_DIR"] = Path.Combine(TestSetup.RunDir, "exe-cache");   // never the user's real caches
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "sf-mod-check didn't finish in 2 minutes");
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Solo.esm with two misc items that each link a keyword the plugin doesn't have: two [links] ERRORs.</summary>
    static string TwoBadLinks(Sandbox s)
    {
        var mod = Plugin.New("Solo.esm", PluginStyle.Light, "test", "no masters");
        mod.MiscItems.AddNew("SOLO_MiscA").Keywords = [new FormKey(mod.ModKey, 0xFF0).ToLink<IKeywordGetter>()];
        mod.MiscItems.AddNew("SOLO_MiscB").Keywords = [new FormKey(mod.ModKey, 0xFF1).ToLink<IKeywordGetter>()];
        var dir = s.Sub("plugin");
        using (var g = new GameData(dir)) PluginWriter.Write(mod, dir, g, new WriteOptions { GapCheck = false });
        return Path.Combine(dir, "Solo.esm");
    }

    static List<string> Lines(string output) => output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l != "").ToList();

    /// <summary>Solo.esm: a light master with no masters, one keyword and a misc item linking it.</summary>
    static string Solo(Sandbox s)
    {
        var mod = Plugin.New("Solo.esm", PluginStyle.Light, "test", "no masters");
        var kw = mod.Keywords.AddNew("SOLO_Keyword");
        mod.MiscItems.AddNew("SOLO_Misc").Keywords = [kw.ToLink<IKeywordGetter>()];
        var dir = s.Sub("plugin");
        using (var g = new GameData(dir)) PluginWriter.Write(mod, dir, g, new WriteOptions { GapCheck = false });
        return Path.Combine(dir, "Solo.esm");
    }

    internal static string ResultLine(string output) => output.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("RESULT: ")) ?? "(no RESULT line)\n" + output;

    // 0.5.3: the release candidate's package said 0.5.3 but --version said 0.5.2 (Program.cs kept its own constant).
    // 0.5.4: Program.cs reads the version from the assembly; this still checks it against the csproj's <Version> text,
    // and that SourceLink's "+<commit>" is cut off.
    [Fact]
    public void Version_text_matches_the_package_version()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "sfmodcheck.csproj"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\sfmodcheck.csproj not found above " + AppContext.BaseDirectory);
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(dir!.FullName, "src", "sfmodcheck.csproj")), "<Version>([^<]+)</Version>");
        Assert.True(m.Success, "no <Version> in sfmodcheck.csproj");
        var (code, output) = Run("--version");
        Assert.Equal(0, code);
        Assert.StartsWith($"sf-mod-check {m.Groups[1].Value} by ", output);
        Assert.DoesNotContain("+", Lines(output)[0]);
    }

    [Fact]
    public void Clean_plugin_exits_0()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--data", s.Sub("data"));
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, 0 errors, 0 warnings (Solo.esm: ", ResultLine(output));
    }

    [Fact]
    public void Plugin_with_an_error_exits_1()
    {
        using var s = new Sandbox();
        var p = Solo(s);
        var b = File.ReadAllBytes(p);
        Bytes.SetU32(b, 8, Bytes.U32(b, 8) | Flags.Medium);   // Light + Medium
        File.WriteAllBytes(p, b);
        var (code, output) = Run(p, "--data", s.Sub("data"));   // no "check": older callers leave it out
        Assert.Equal(1, code);
        Assert.Contains("ERROR [header] both the Light and the Medium flag are set", output);
        // (Mutagen can't read a plugin with both flags either, so there's a [read] error too and the links aren't checked)
        Assert.StartsWith("RESULT: FAILED, 2 errors, 0 warnings; links not checked (Solo.esm: ", ResultLine(output));
    }

    [Fact]
    public void Missing_plugin_exits_2()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Path.Combine(s.Dir, "NoSuch.esm"));
        Assert.Equal(2, code);
        Assert.Contains("no such file", output);
    }

    [Fact]
    public void Unknown_argument_exits_2()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--bogus");
        Assert.Equal(2, code);
        Assert.Contains("unknown option --bogus", output);
        Assert.Contains("usage: sf-mod-check check <plugin>", output);
    }

    // ---- 0.4.0 step 6: the troubleshooting log (--verbose, --log) and errors that name the fix ----

    /// <summary>Addon.esm (light) needs Solo.esm, which sits in its own folder: a master found through --masters-dir.</summary>
    static (string Addon, string MastersDir) AddonWithMaster(Sandbox s)
    {
        var master = Solo(s);
        var mm = Plugin.New("Addon.esm", PluginStyle.Light, "test", "needs Solo.esm");
        var dir = s.Sub("addon");
        using (var g = new GameData(s.Sub("data"), [Path.GetDirectoryName(master)!]))
        {
            var solo = g.Open("Solo.esm");
            mm.MiscItems.AddNew("ADDON_Misc").Keywords = [solo.Keywords.First().ToLink<IKeywordGetter>()];
            PluginWriter.Write(mm, dir, g, new WriteOptions { GapCheck = false });
        }
        return (Path.Combine(dir, "Addon.esm"), Path.GetDirectoryName(master)!);
    }

    [Fact]
    public void Log_file_records_the_data_folder_masters_and_result()
    {
        using var s = new Sandbox();
        var (addon, mdir) = AddonWithMaster(s);
        var log = Path.Combine(s.Dir, "run.log");
        var (code, stdout, stderr) = RunSplit("check", addon, "--data", s.Sub("data"), "--masters-dir", mdir, "--log", log);
        Assert.Equal(0, code);
        Assert.DoesNotContain("log: ", stdout + stderr);   // --log alone writes only the file
        var text = File.ReadAllText(log);
        Assert.Contains($"plugin: {Path.GetFullPath(addon)} (", text);
        Assert.Contains($"Data folder: {Path.GetFullPath(s.Sub("data"))}: from --data; Starfield.esm NOT there", text);
        Assert.Contains($"masters are looked for in: {Path.GetFullPath(mdir)} (--masters-dir)", text);
        Assert.Contains($"master Solo.esm: {Path.Combine(Path.GetFullPath(mdir), "Solo.esm")} (", text);
        Assert.Contains($"cache folder: {Path.Combine(TestSetup.RunDir, "exe-cache")} (from SFMODCHECK_CACHE_DIR)", text);
        Assert.Contains("edid-cache Solo.esm: ", text);   // read or built, depending on the test order
        Assert.Contains("stage itm: ", text);
        Assert.Contains("result: OK, 0 errors, 0 warnings, exit code 0", text);
    }

    [Fact]
    public void Verbose_goes_to_stderr_and_leaves_stdout_alone()
    {
        using var s = new Sandbox();
        var p = TwoBadLinks(s);
        var (_, plain, plainErr) = RunSplit("check", p, "--data", s.Sub("data"));
        var (code, stdout, stderr) = RunSplit("check", p, "--data", s.Sub("data"), "--verbose");
        Assert.Equal(1, code);
        static List<string> NoTiming(string o) => Lines(o).Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"; [0-9.]+ s\)$", ")")).ToList();
        Assert.Equal(NoTiming(plain), NoTiming(stdout));
        Assert.DoesNotContain("log: ", plainErr);
        Assert.Contains("log: sf-mod-check ", stderr);
        Assert.Contains("log: findings: 2 ERROR [links]", stderr);
        Assert.Contains("log: result: FAILED, 2 errors, 0 warnings, exit code 1", stderr);
    }

    [Fact]
    public void Missing_master_is_logged_with_every_place_looked()
    {
        using var s = new Sandbox();
        var (addon, mdir) = AddonWithMaster(s);
        File.Delete(Path.Combine(mdir, "Solo.esm"));
        var (code, stdout, stderr) = RunSplit("check", addon, "--data", s.Sub("data"), "--masters-dir", mdir, "--verbose");
        Assert.Equal(1, code);
        Assert.Contains("ERROR [header] master Solo.esm is not installed", stdout);
        Assert.Contains($"log: master Solo.esm: NOT FOUND (looked in {mdir}, {s.Sub("data")})", stderr);
        Assert.Contains("log: links not checked", stderr);
    }

    [Theory]
    [InlineData("--max", "abc", "--max needs a whole number")]
    [InlineData("--max", null, "--max needs a number after it")]
    [InlineData("--data", null, "--data needs the game's Data folder after it")]
    [InlineData("--log", null, "--log needs a file name after it")]
    [InlineData("--data", "", "--data needs the game's Data folder after it (it was given an empty value)")]
    [InlineData("--masters-dir", " ", "--masters-dir needs a folder after it (it was given an empty value)")]
    public void Bad_option_value_exits_2_with_the_fix(string option, string? value, string expected)
    {
        using var s = new Sandbox();
        var args = new List<string> { "check", Solo(s), option };
        if (value != null) args.Add(value);
        var (code, output) = Run(args.ToArray());
        Assert.Equal(2, code);
        Assert.Contains(expected, output);
        Assert.Contains("for example: " + option, output);
        Assert.DoesNotContain("   at ", output);   // no stack trace
    }

    [Fact]
    public void Empty_plugin_name_exits_2_with_the_fix()
    {
        var (code, output) = Run("check", "");
        Assert.Equal(2, code);
        Assert.Contains("the plugin name is empty", output);
        Assert.DoesNotContain("   at ", output);
    }

    [Theory]
    [InlineData("--data")]
    [InlineData("--plugins")]
    [InlineData("--out")]
    public void Conflict_option_with_an_empty_value_exits_2_with_the_fix(string option)
    {
        var (code, output) = Run("conflict", option, "", "--quiet");
        Assert.Equal(2, code);
        Assert.Contains($"{option} needs", output);
        Assert.Contains("(it was given an empty value)", output);
        Assert.DoesNotContain("ArgumentException", output);
    }

    [Fact]
    public void Log_in_a_missing_folder_exits_2_naming_the_folder()
    {
        using var s = new Sandbox();
        var log = Path.Combine(s.Dir, "no-such-folder", "run.log");
        var (code, output) = Run("check", Solo(s), "--data", s.Sub("data"), "--log", log);
        Assert.Equal(2, code);
        Assert.Contains($"can't write the log file {log}: the folder {Path.GetDirectoryName(log)} doesn't exist", output);
    }

    [Fact]
    public void Missing_plugin_names_the_path_and_the_fix()
    {
        using var s = new Sandbox();
        var p = Path.Combine(s.Dir, "NoSuch.esm");
        var (code, output) = Run("check", p, "--log", Path.Combine(s.Dir, "run.log"));
        Assert.Equal(2, code);
        Assert.Contains($"can't check {p}: no such file. Check the path", output);
        Assert.Contains($"stopped: plugin {p}: no such file", File.ReadAllText(Path.Combine(s.Dir, "run.log")));
    }

    [Fact]
    public void Every_option_is_in_the_usage_readme_and_agents()
    {
        // Program.cs's option switch, README.md and AGENTS.md must name the same options (CLAUDE.md: AGENTS.md changes
        // in the same commit as any change to the options).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Program.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\Program.cs not found above " + AppContext.BaseDirectory);
        HashSet<string> Options(string file) =>
            System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(dir!.FullName, "src", file)), @"case ""(--[a-z-]+)"":")
                .Select(m => m.Groups[1].Value).Where(o => o != "--help").ToHashSet();
        var check = Options("Program.cs");
        Assert.Equal(new[] { "--data", "--ini", "--language", "--log", "--masters-dir", "--max", "--no-assets", "--no-hints", "--quiet", "--verbose" }, check.Order());
        var conflict = Options("ConflictCommand.cs");
        Assert.Equal(new[] { "--data", "--extra-data", "--ini", "--language", "--log", "--max-memory", "--no-assets", "--no-priorities", "--out", "--plugins", "--quiet", "--verbose" }, conflict.Order());
        var (_, usage) = Run("check");
        var (_, conflictUsage) = Run("conflict", "--help");
        foreach (var file in new[] { "README.md", "AGENTS.md" })
        {
            var text = File.ReadAllText(Path.Combine(dir!.FullName, file));
            Assert.All(check.Union(conflict), o => Assert.True(text.Contains("`" + o) || text.Contains(" " + o + " "), $"{file} doesn't mention {o}"));
        }
        Assert.All(check, o => Assert.Contains(o, usage));
        Assert.All(conflict, o => Assert.Contains(o, conflictUsage));
    }

    // ---- conflict: arguments and errors (part 2 of the move, 2026-11-16; synthetic conflict cases come later)

    static string PluginsTxt(Sandbox s, params string[] active)
    {
        var p = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllLines(p, new[] { "# test load order" }.Concat(active.Select(a => "*" + a)));
        return p;
    }

    [Fact]
    public void Conflict_missing_data_folder_exits_2_with_cannot_check()
    {
        using var s = new Sandbox();
        var (code, output) = Run("conflict", "--data", Path.Combine(s.Dir, "no-such-data"), "--plugins", PluginsTxt(s, "Solo.esm"));
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK (the Data folder ", ResultLine(output));
        Assert.Contains("--data \"<the game's Data folder>\"", output);
    }

    [Fact]
    public void Conflict_missing_plugins_txt_exits_2_and_names_the_file()
    {
        using var s = new Sandbox();
        var missing = Path.Combine(s.Dir, "NoSuch.txt");
        var (code, output) = Run("conflict", "--data", s.Sub("data"), "--plugins", missing);
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK (the Plugins.txt ", ResultLine(output));
        Assert.Contains(missing, output);
    }

    [Theory]
    [InlineData("--max-memory", "lots")]
    [InlineData("--max-memory", "0")]
    [InlineData("--frobnicate", null)]
    [InlineData("MyMod.esm", null)]
    public void Conflict_bad_arguments_exit_2_with_the_usage(string arg, string? value)
    {
        using var s = new Sandbox();
        var args = new List<string> { "conflict", "--data", s.Sub("data"), "--plugins", PluginsTxt(s, "Solo.esm"), arg };
        if (value != null) args.Add(value);
        var (code, output) = Run(args.ToArray());
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK (bad arguments", ResultLine(output));
    }

    [Fact]
    public void Conflict_on_a_one_plugin_load_order_writes_the_report_and_exits_0()
    {
        using var s = new Sandbox();
        var plugin = Solo(s);
        var outDir = Path.Combine(s.Dir, "report");
        var log = Path.Combine(s.Dir, "conflict.log");
        var (code, stdout, _) = RunSplit("conflict", "--data", Path.GetDirectoryName(plugin)!, "--plugins", PluginsTxt(s, "Solo.esm"),
            "--out", outDir, "--log", log);
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, 0 lost-change entries (0 official, 0 mod vs mod), 0 benign entries shown apart, 0 ITMs, 0 load-order problems (report: ", ResultLine(stdout));
        foreach (var f in new[] { "report.html", "report.md", "conflicts.json" }) Assert.True(File.Exists(Path.Combine(outDir, f)), f);
        Assert.DoesNotContain("googleapis", File.ReadAllText(Path.Combine(outDir, "report.html")));   // PRIVACY.md: no network
        var logText = File.ReadAllText(log);
        Assert.Contains("load order: ", logText);
        Assert.Contains("result: 0 lost-change entries", logText);
    }

    [Fact]
    public void Conflict_lists_a_missing_plugin_as_a_load_order_problem()
    {
        using var s = new Sandbox();
        var plugin = Solo(s);
        var (code, stdout, _) = RunSplit("conflict", "--data", Path.GetDirectoryName(plugin)!, "--plugins", PluginsTxt(s, "Solo.esm", "Gone.esm"),
            "--out", Path.Combine(s.Dir, "report"), "--quiet");
        Assert.Equal(0, code);
        Assert.Contains("1 load-order problems", ResultLine(stdout));
        Assert.Contains("Gone.esm: listed but not found in Data", File.ReadAllText(Path.Combine(s.Dir, "report", "report.md")));
    }

    [Fact]
    public void Missing_data_folder_exits_2_with_cannot_check()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--data", Path.Combine(s.Dir, "no-such-data"));
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK Solo.esm (", ResultLine(output));
    }

    [Fact]
    public void Hint_comes_once_after_the_checks_last_finding()
    {
        using var s = new Sandbox();
        var (code, stdout, _) = RunSplit("check", TwoBadLinks(s), "--data", s.Sub("data"));
        Assert.Equal(1, code);
        var lines = Lines(stdout);
        var links = lines.FindAll(l => l.StartsWith("ERROR [links] "));
        Assert.Equal(2, links.Count);
        var hints = lines.FindAll(l => l.StartsWith("HINT "));
        Assert.Equal(new[] { "HINT [links] " + Hints.ByCheck["links"] }, hints);
        Assert.Equal(lines.LastIndexOf(links[^1]) + 1, lines.IndexOf(hints[0]));   // right after the last [links] line
        Assert.StartsWith("RESULT: FAILED, 2 errors, 0 warnings (Solo.esm: ", lines[^1]);
    }

    [Fact]
    public void No_hints_leaves_only_the_hint_lines_out()
    {
        using var s = new Sandbox();
        var p = TwoBadLinks(s);
        var (_, with, _) = RunSplit("check", p, "--data", s.Sub("data"));
        var (code, without, _) = RunSplit("check", p, "--data", s.Sub("data"), "--no-hints");
        Assert.Equal(1, code);
        static List<string> Findings(string o) => Lines(o).Where(l => !l.StartsWith("RESULT: ")).ToList();   // RESULT has a timing
        Assert.DoesNotContain(Findings(without), l => l.StartsWith("HINT "));
        Assert.Equal(Findings(with).Where(l => !l.StartsWith("HINT ")), Findings(without));
    }

    [Fact]
    public void Clean_plugin_has_no_hints()
    {
        using var s = new Sandbox();
        var (_, stdout, _) = RunSplit("check", Solo(s), "--data", s.Sub("data"));
        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("HINT "));
    }

    [Fact]
    public void Very_large_plugin_gets_the_memory_note_on_stderr_only()
    {
        using var s = new Sandbox();
        var p = Solo(s);
        var (_, plainOut, plainErr) = RunSplit("check", p, "--data", s.Sub("data"));
        Assert.DoesNotContain("very large", plainErr);
        var b = File.ReadAllBytes(p);
        Assert.Equal("HEDR", System.Text.Encoding.ASCII.GetString(b, 24, 4));
        Bytes.SetU32(b, 34, 600_000);   // the header's record count
        File.WriteAllBytes(p, b);
        var (_, stdout, stderr) = RunSplit("check", p, "--data", s.Sub("data"));
        Assert.Contains("note: Solo.esm is very large", stderr);
        Assert.DoesNotContain("very large", stdout);
    }

    [Fact]
    public void Every_check_has_a_hint()
    {
        // The check names the checker can report, read from its source: Add("ERROR", "links", ...), add(sev, "scenes", ...).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Hints.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\Hints.cs not found above " + AppContext.BaseDirectory);
        var re = new System.Text.RegularExpressions.Regex(@"\b[Aa]dd\((?:""(?:ERROR|WARNING)""|sev), ""([a-z]+)""");
        var used = Directory.GetFiles(Path.Combine(dir!.FullName, "src"), "*.cs")
            .SelectMany(f => re.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.Equal(15, used.Count);   // structure, header, ids, records, read, links, navi, conditions, dialogue, scenes, editorids, itm, assets, trees, ships (0.10.0)
        Assert.Equal(used.OrderBy(x => x), Hints.ByCheck.Keys.OrderBy(x => x));
        Assert.All(Hints.ByCheck.Values, h => Assert.True(h.Length > 40 && !h.Contains('\n'), "hint too short or not one line: " + h));
        // README's "For Creation Kit and xEdit authors" section repeats each hint word for word.
        var readme = File.ReadAllText(Path.Combine(dir.FullName, "README.md"));
        Assert.All(Hints.ByCheck, kv => Assert.True(readme.Contains($"- **{kv.Key}**: {kv.Value}\n") || readme.Contains($"- **{kv.Key}**: {kv.Value}\r\n"),
            $"README.md doesn't have the hint for {kv.Key} word for word"));
    }

    // ---- 0.4.1 (review fixes, 2026-11-16) ----

    /// <summary>A plugin whose master list holds a network path: never looked up, reported as a header ERROR.</summary>
    [Fact]
    public void Network_path_master_is_refused()
    {
        using var s = new Sandbox();
        var p = Solo(s);
        var b = File.ReadAllBytes(p);
        // Rebuild the TES4 header with one extra MAST/DATA pair holding a UNC path (".invalid" never resolves).
        var hsize = BitConverter.ToInt32(b, 4);
        var mast = System.Text.Encoding.UTF8.GetBytes(@"\attacker.invalid\s\x.esm" + "\0");
        var extra = new List<byte>();
        extra.AddRange("MAST"u8.ToArray()); extra.AddRange(BitConverter.GetBytes((ushort)mast.Length)); extra.AddRange(mast);
        extra.AddRange("DATA"u8.ToArray()); extra.AddRange(BitConverter.GetBytes((ushort)8)); extra.AddRange(new byte[8]);
        var outb = new List<byte>(b[..24]);
        var hedrEnd = 24 + 6 + 12;   // HEDR comes first: 6-byte field header + 12 bytes
        outb.AddRange(b[24..hedrEnd]); outb.AddRange(extra); outb.AddRange(b[hedrEnd..]);
        var arr = outb.ToArray();
        BitConverter.GetBytes(hsize + extra.Count).CopyTo(arr, 4);
        File.WriteAllBytes(p, arr);
        var (code, output) = Run("check", p, "--data", s.Sub("data"));
        Assert.Equal(1, code);
        Assert.Contains("not a plain plugin file name", output);
        Assert.StartsWith("RESULT: FAILED", ResultLine(output));
    }

    [Fact]
    public void Missing_plugin_still_prints_a_RESULT_line()
    {
        var (code, output) = Run("check", "No-Such-Plugin-7f3a.esm");
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK", ResultLine(output));
    }

    [Fact]
    public void Unknown_option_still_prints_a_RESULT_line()
    {
        var (code, output) = Run("check", "x.esm", "--no-such-option");
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK", ResultLine(output));
    }

    [Fact]
    public void Missing_masters_dir_is_exit_2()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--data", s.Sub("data"), "--masters-dir", Path.Combine(s.Dir, "no-such-folder"));
        Assert.Equal(2, code);
        Assert.Contains("--masters-dir", output);
        Assert.StartsWith("RESULT: CANNOT CHECK", ResultLine(output));
    }

    [Fact]
    public void Log_never_overwrites_a_plugin()
    {
        using var s = new Sandbox();
        var p = Solo(s);
        var victim = Path.Combine(s.Sub("plugin"), "Victim.txt");
        File.Copy(p, victim);   // a plugin under another extension: refused by its TES4 start
        var before = File.ReadAllBytes(p);
        var (code, output) = Run("check", p, "--data", s.Sub("data"), "--log", p);
        Assert.Equal(2, code);
        Assert.Equal(before, File.ReadAllBytes(p));
        (code, _) = Run("check", p, "--data", s.Sub("data"), "--log", victim);
        Assert.Equal(2, code);
        Assert.Equal(before, File.ReadAllBytes(victim));
        Assert.StartsWith("RESULT: CANNOT CHECK", ResultLine(output));
    }
}
