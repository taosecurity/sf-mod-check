// 0.22.0 item D: on a tag rerun the release workflow must not replace the .nupkg on the GitHub release (it could then differ
// from the copy on nuget.org). The test reads ci.yml: the .nupkg upload is behind a check of the release's assets, and no
// upload line that names a .nupkg carries --clobber.
using Xunit;

namespace SfModCheck.Tests;

public class CiWorkflowTests
{
    static string[] CiLines()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "ci.yml"))) dir = dir.Parent;
        if (dir == null) return [];   // a source copy without the workflow (the public-only file): nothing to check
        return File.ReadAllLines(Path.Combine(dir.FullName, ".github", "workflows", "ci.yml"));
    }

    [Fact]
    public void A_tag_rerun_does_not_clobber_the_nupkg()
    {
        var lines = CiLines();
        if (lines.Length == 0) return;
        var text = string.Join("\n", lines);
        // the release's asset list is read before any file is uploaded to an existing release
        Assert.Contains("--json assets", text);
        int check = text.IndexOf("--json assets", StringComparison.Ordinal);
        int upload = text.IndexOf("gh release upload", StringComparison.Ordinal);
        Assert.True(upload > check, "files are uploaded to an existing release only after the asset check");
        // a rerun never replaces a published file (0.24.0: the release job has no --clobber at all)
        Assert.DoesNotContain("--clobber", text.Replace("no --clobber", ""));
        // a release that has every file is not re-uploaded: its published package goes to nuget.org
        Assert.Contains("gh release download", text);
    }
}
