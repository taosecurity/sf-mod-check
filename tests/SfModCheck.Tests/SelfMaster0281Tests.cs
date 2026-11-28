// sf-mod-check 0.28.1 (Round 3, B1): a plugin that masters itself, or two plugins that master each other, crashed
// `check` and `release` with a stack overflow (Checker.Need walked the masters with no visited set). These tests run the
// built program as a child process, so a crash can't take down the test host; each fails on 0.28.0 (no RESULT line,
// a crash exit code). All plugins are made up.
using System.IO.Compression;
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Xunit;

namespace SfModCheck.Tests;

public class SelfMaster0281Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>A plugin whose only master is <paramref name="masterName"/> (patched in over Base.esm, same length).</summary>
    static string WithMaster(string dir, string name, string masterName)
    {
        using (var g = new GameData(dir))
        {
            if (!File.Exists(Path.Combine(dir, "Base.esm")))
            {
                var bs = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base");
                bs.Keywords.AddNew("B_Kw");
                PluginWriter.Write(bs, dir, g, NoGap);
            }
        }
        using (var g = new GameData(dir))
        {
            var baseMod = g.Open("Base.esm");
            var m = Plugin.New(name, PluginStyle.Full, "test", "made-up");
            m.Keywords.GetOrAddAsOverride(baseMod.Keywords.First());
            m.Keywords.AddNew("T_" + name[..4]);
            PluginWriter.Write(m, dir, g, NoGap);
        }
        var path = Path.Combine(dir, name);
        var bytes = File.ReadAllBytes(path);
        var from = Encoding.ASCII.GetBytes("Base.esm");
        var to = Encoding.UTF8.GetBytes(masterName);
        Assert.Equal(from.Length, to.Length);
        int hits = 0;
        for (int i = 0; i + from.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, from.Length).SequenceEqual(from)) { to.CopyTo(bytes, i); hits++; i += from.Length - 1; }
        Assert.True(hits >= 1, "the master name was not in the file");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static string Result(string output) => ExeTests.ResultLine(output);

    [Fact]
    public void Check_on_a_self_mastering_plugin_ends_with_a_RESULT_and_the_self_master_error()
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        var p = WithMaster(data, "Self.esm", "Self.esm");
        var (code, stdout, stderr) = ExeTests.RunSplit("check", p, "--data", data);
        var all = stdout + stderr;
        Assert.True(code is 1, $"exit {code}\n{all}");
        Assert.Contains("the plugin lists itself as a master", all);
        Assert.StartsWith("RESULT: FAILED", Result(all));
    }

    [Fact]
    public void Check_on_two_plugins_that_master_each_other_ends_with_a_RESULT_not_a_crash()
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        var a = WithMaster(data, "AAAA.esm", "BBBB.esm");
        WithMaster(data, "BBBB.esm", "AAAA.esm");
        var (code, stdout, stderr) = ExeTests.RunSplit("check", a, "--data", data);
        var all = stdout + stderr;
        Assert.True(code is 0 or 1, $"exit {code}\n{all}");
        Assert.StartsWith("RESULT: ", Result(all));
        Assert.DoesNotContain("Stack overflow", all);
    }

    [Fact]
    public void Release_on_a_zip_holding_a_self_mastering_plugin_ends_with_a_RESULT()
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        var work = s.Sub("work");
        var p = WithMaster(work, "Self.esm", "Self.esm");
        var zip = Path.Combine(s.Dir, "Self-1.0.0.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = z.CreateEntry("Self.esm").Open()) w.Write(File.ReadAllBytes(p));
        File.Copy(Path.Combine(work, "Base.esm"), Path.Combine(data, "Base.esm"));
        var (code, stdout, stderr) = ExeTests.RunSplit("release", zip, "--data", data);
        var all = stdout + stderr;
        Assert.True(code is 0 or 1, $"exit {code}\n{all}");
        Assert.StartsWith("RESULT: ", Result(all));
        Assert.DoesNotContain("Stack overflow", all);
    }

    [Fact]
    public void Check_prints_a_bidi_character_in_a_master_name_escaped()
    {
        // 0.28.1 (B2): finding text goes through SafeText.OneLine, so U+202E can't reorder the line on screen
        using var s = new Sandbox();
        var data = s.Sub("data");
        var p = WithMaster(data, "Odd.esm", "\u202Eb.esm");
        var (code, stdout, stderr) = ExeTests.RunSplit("check", p, "--data", data);
        var all = stdout + stderr;
        Assert.True(code is 0 or 1, $"exit {code} {all}");
        var raw = all.Split('\n').Where(l => l.Contains('\u202E')).Select(l => l.Replace('\u202E', '#')).ToList();
        Assert.True(raw.Count == 0, "raw bidi on: " + string.Join(" | ", raw));
        Assert.Contains("\\u202eb.esm", all, StringComparison.Ordinal);   // the escaped name (a raw U+202E would match any text under a culture compare)
    }
}
