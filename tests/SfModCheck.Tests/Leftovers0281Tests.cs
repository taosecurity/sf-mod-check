// sf-mod-check 0.28.1 (Round 3) leftovers: one --out guard for scenes, voice-lines and patch check --json (C2, A4), check
// finding text through SafeText.OneLine (B2), a repeated --plugin is exit 2 (B4) and a console FormID with 0X (C5).
// Each test runs the built program as a child process. All plugins are made up.
using System.Text;
using System.Text.RegularExpressions;
using BuildKit;
using Mutagen.Bethesda;
using Xunit;

namespace SfModCheck.Tests;

public class Leftovers0281Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static string MakePlugin(string dir, string name, string kw)
    {
        using var g = new GameData(dir);
        var m = Plugin.New(name, PluginStyle.Full, "test", "made-up");
        m.Keywords.AddNew(kw);
        PluginWriter.Write(m, dir, g, NoGap);
        return Path.Combine(dir, name);
    }

    static string Victim(Sandbox s, string name)
    {
        var p = Path.Combine(s.Dir, name);
        File.WriteAllText(p, "precious");
        return p;
    }

    [Theory]
    [InlineData("scenes", "Whatever", "--out")]
    [InlineData("voice-lines", "@Mod.esm", "--out")]
    [InlineData("patch", "check", "--json")]
    public void An_output_name_that_is_a_mod_file_is_refused_and_nothing_is_written(string cmd, string arg, string opt)
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        MakePlugin(data, "Mod.esm", "M_Kw");
        var victim = Victim(s, "Other.esm");
        var args = cmd == "patch"
            ? new[] { "patch", "check", Path.Combine(data, "Mod.esm"), opt, victim, "--data", data }
            : new[] { cmd, arg.StartsWith("@") ? Path.Combine(data, arg[1..]) : arg, opt, victim, "--data", data };
        var (code, stdout, stderr) = ExeTests.RunSplit(args);
        Assert.True(code == 2, $"exit {code}\n{stdout}{stderr}");
        Assert.Equal("precious", File.ReadAllText(victim));
        Assert.Contains("not a .esm file", stderr);
    }

    [Fact]
    public void Scenes_out_that_is_a_folder_is_refused()
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        var (code, _, stderr) = ExeTests.RunSplit("scenes", "Whatever", "--out", s.Dir, "--data", data);
        Assert.Equal(2, code);
        Assert.Contains("is a folder", stderr);
    }

    [Fact]
    public void A_repeated_plugin_option_is_exit_2()
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        var a = MakePlugin(data, "AAAA.esm", "A_Kw");
        var b = MakePlugin(data, "BBBB.esm", "B_Kw");
        var (code, stdout, stderr) = ExeTests.RunSplit("find", "A_Kw", "--plugin", a, "--plugin", b, "--data", data);
        Assert.True(code == 2, $"exit {code}\n{stdout}{stderr}");
        Assert.Contains("--plugin was given twice", stderr);
    }

    [Fact]
    public void A_console_FormID_with_an_upper_case_0X_is_read_as_a_FormID()
    {
        using var s = new Sandbox();
        var data = s.Sub("data");
        var p = MakePlugin(data, "Solo.esm", "S_Kw");
        // --plugin can't take a console FormID, and says so: on 0.28.0 the 0X form was read as an EditorID and gave NOT FOUND
        var (code, stdout, stderr) = ExeTests.RunSplit("find", "0X00000800", "--plugin", p, "--data", data);
        Assert.True(code == 2, $"exit {code} {stdout}{stderr}");
        Assert.Contains("is a console FormID", stdout + stderr);
    }
}
