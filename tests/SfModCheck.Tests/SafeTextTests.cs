// 0.13.0 plan step 6: one escaping rule (SafeText.OneLine) for the log writer, the release print sites and the
// conflict lines (0.10.0 code review round 10, findings 1, 2 and 6). Names are made up here; no game files.
using System.IO.Compression;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class SafeTextTests
{
    // Built with escapes, so the source holds no invisible characters.
    const string Rlo = "\u202E", Zwsp = "\u200B";

    static void AssertNoRawSpecials(string text)
    {
        // ordinal: a culture compare ignores format characters, so it would find them in any text
        Assert.DoesNotContain(Rlo, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Zwsp, text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2028', text);
    }

    // code review 0.13.0, round 7: every command's catch-all RESULT line escapes the error text, so a multi-line message
    // can't split the RESULT line (before, check, diff and triage printed e.Message as it was)
    [Fact]
    public void Every_catch_all_result_line_escapes_the_error_text()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "SafeText.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\SafeText.cs not found above " + AppContext.BaseDirectory);
        int sites = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "src"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            for (int at = text.IndexOf("Fatal.ResultLine(e", StringComparison.Ordinal); at >= 0; at = text.IndexOf("Fatal.ResultLine(e", at + 1, StringComparison.Ordinal))
            {
                var end = text.IndexOf("));", at, StringComparison.Ordinal);
                var call = text[at..(end < 0 ? text.Length : end)];
                sites++;
                Assert.False(call.Contains("{e.Message}", StringComparison.Ordinal), $"{Path.GetFileName(file)}: a RESULT line prints e.Message raw: {call}");
                Assert.True(call.Contains("SafeText.OneLine(", StringComparison.Ordinal), $"{Path.GetFileName(file)}: a RESULT line doesn't escape: {call}");
            }
        }
        Assert.True(sites >= 7, $"only {sites} RESULT-line sites found");
    }

    // code review 0.13.0, round 8: the other RESULT lines (check's no-Data-folder and summary lines, the STOPPED lines of
    // patch and conflict) escape the file name and the error text too, as the CHANGELOG says
    [Fact]
    public void No_result_line_prints_a_file_name_or_error_text_raw()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "SafeText.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\SafeText.cs not found above " + AppContext.BaseDirectory);
        int lines = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "src"), "*.cs"))
            foreach (var line in File.ReadAllLines(file).Where(l => l.Contains("\"RESULT: ", StringComparison.Ordinal) || l.Contains("$\"RESULT: ", StringComparison.Ordinal)))
            {
                lines++;
                Assert.False(line.Contains("{e.Message", StringComparison.Ordinal), $"{Path.GetFileName(file)}: a RESULT line prints e.Message raw: {line.Trim()}");
                Assert.False(line.Contains("{Path.GetFileName(", StringComparison.Ordinal), $"{Path.GetFileName(file)}: a RESULT line prints a file name raw: {line.Trim()}");
            }
        Assert.True(lines >= 20, $"only {lines} RESULT lines found");
    }

    [Fact]
    public void OneLine_is_null_safe_and_leaves_ordinary_names_alone()
    {
        Assert.Null(SafeText.OneLine(null));
        Assert.Equal("Ünïcødé Mod – Part 2.esm", SafeText.OneLine("Ünïcødé Mod – Part 2.esm"));
        Assert.Equal(@"Data\Scripts\My Mod.pex", SafeText.OneLine(@"Data\Scripts\My Mod.pex"));
        Assert.Equal("a\\nb\\u202ec\\u200bd\\te", SafeText.OneLine("a\nb" + Rlo + "c" + Zwsp + "d\te"));
        // escaping twice changes nothing: a finding already escaped by a step prints the same at the print site
        var once = SafeText.OneLine("x\n" + Rlo);
        Assert.Equal(once, SafeText.OneLine(once));
    }

    [Fact]
    public void A_conflict_line_escapes_control_and_format_characters_instead_of_spaces()
    {
        // Before 0.13.0 Conflict.Impact turned these into spaces; now it uses the shared rule.
        var line = Conflict.Impact.Summary(new InvalidDataException("record Bad" + Rlo + "Name" + Zwsp + "\tX can't be read"));
        Assert.Equal("InvalidDataException: record Bad\\u202eName\\u200b\\tX can't be read", line);
        AssertNoRawSpecials(line);
    }

    [Fact]
    public void A_creations_log_line_with_a_newline_and_format_characters_stays_one_log_line()
    {
        using var s = new Sandbox();
        var log = Path.Combine(s.Dir, "log.txt");
        Assert.Null(Log.ToFile(log));
        try
        {
            Log.Write("creations: MyMod" + Rlo + "Name" + Zwsp + ".esm: not read\nWARNING [x] fake line");
            Log.Write("creations: second line");
        }
        finally { Log.Close(); }
        var lines = File.ReadAllLines(log).Where(l => l.Contains("creations: MyMod") || l.Contains("fake line") || l.Contains("second line")).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Matches(@"^\s*\d+\.\d\ds  creations: MyMod\\u202eName\\u200b\.esm: not read$", lines[0]);
        // the line after a newline is indented, so it can't pass for a log line of its own
        Assert.Matches(@"^\s*\d+\.\d\ds    WARNING \[x\] fake line$", lines[1]);
        Assert.Matches(@"^\s*\d+\.\d\ds  creations: second line$", lines[2]);
        AssertNoRawSpecials(File.ReadAllText(log));
    }

    [Fact]
    public void Exe_release_escapes_names_in_the_header_the_findings_and_the_log()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var zip = Path.Combine(s.Sub("zips"), "Mod" + Rlo + Zwsp + "-0.1.0.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string path, byte[] bytes) { using var w = z.CreateEntry(path).Open(); w.Write(bytes); }
            Add("Bad" + Rlo + "Name" + Zwsp + ".esm", ReleaseRulesTests.Plugin("Starfield.esm", "Missing" + Rlo + ".esm"));
            Add("README-Mod.md", Encoding.UTF8.GetBytes("x"));
        }
        var log = Path.Combine(s.Dir, "release-log.txt");
        var (code, stdout, stderr) = ExeTests.RunSplit("release", zip, "--data", data, "--profile", "creations", "--log", log);
        Assert.True(code == 0 || code == 1, stdout + stderr);
        var header = stdout.Split('\n')[0];
        Assert.Contains("Mod\\u202e\\u200b-0.1.0", header);
        Assert.Contains("Bad\\u202eName\\u200b.esm", stdout);   // at least one step's finding names the plugin
        AssertNoRawSpecials(stdout);
        AssertNoRawSpecials(File.ReadAllText(log));
        Assert.Matches(@"^RESULT: (OK|FAIL) ", ExeTests.ResultLine(stdout));
    }
}
