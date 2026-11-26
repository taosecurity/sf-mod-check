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
        // the release's asset list is read before the .nupkg is added to the upload
        Assert.Contains("--json assets", text);
        int check = text.IndexOf("--json assets", StringComparison.Ordinal);
        int add = text.IndexOf("files+=(out/*.nupkg", StringComparison.Ordinal);
        Assert.True(add > check, "the .nupkg is added to the upload only after the asset check");
        // the one --clobber upload takes the list, never a literal .nupkg
        foreach (var l in lines.Where(l => l.Contains("--clobber")))
            Assert.DoesNotContain(".nupkg", l);
        foreach (var l in lines.Where(l => l.Contains("gh release upload")))
            Assert.DoesNotContain(".nupkg", l);
    }
}
