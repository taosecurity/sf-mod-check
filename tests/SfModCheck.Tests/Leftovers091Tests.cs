// 0.10.0 item 6b: the 0.9.1 last-review items (NOTES-triage.md, "0.9.1 last review (2026-11-20): left for the next
// release"): a symlinked plugin named on the command line is a link (LinkWhy) and is counted, two links whose names
// differ only in case keep their own spellings, and the triage header says "1 file". Bug fixes inside the settled
// skipped-link design, not a new design.
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class Leftovers091Tests
{
    [Fact]
    public void The_triage_header_says_1_file_for_one_file()
    {
        Assert.Equal("sf-mod-check 0.10.0 triage: Foo.esm (1 file; triage, not a verdict)", TriageCommand.Header("0.10.0", "Foo.esm", 1));
        Assert.Equal("sf-mod-check 0.10.0 triage: Mod (0 files; triage, not a verdict)", TriageCommand.Header("0.10.0", "Mod", 0));
        Assert.Equal("sf-mod-check 0.10.0 triage: Mod (2 files; triage, not a verdict)", TriageCommand.Header("0.10.0", "Mod", 2));
    }

    [Fact]
    public void Links_are_named_lower_case_unless_two_differ_only_in_case()
    {
        // one link: its lower-case name, as before; two spellings of one name: each its own; the same spelling twice: once
        Assert.Equal(["textures\\foo.dds"], TriageCommand.LinkNames(["Textures\\Foo.dds"]));
        Assert.Equal(["Tex", "tex"], TriageCommand.LinkNames(["tex", "Tex"]));
        Assert.Equal(["foo - main.ba2"], TriageCommand.LinkNames(["Foo - Main.ba2", "Foo - Main.ba2"]));
        Assert.Equal(["a", "b\\c"], TriageCommand.LinkNames(["B/C", "A"]));
    }

    static TriageContext Run(string root, TriageInputKind kind, string input, List<(string File, string Why)> skipped, List<string> files) => new()
    {
        Options = new TriageOptions(), Kind = kind, InputPath = input, Root = root, Files = files,
        DataPrefix = "", DataDir = Path.Combine(root, "no-game"), Work = root, Report = new TriageReport { InputName = "Mod" },
        Skipped = skipped,
    };

    [UnixFact]
    public void A_symlinked_plugin_named_on_the_command_line_is_a_counted_link()
    {
        // 0.9.1 labeled it "reached only through a link", so the skipped-link count was 0
        using var s = new Sandbox();
        var outside = s.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "Real.esm"), "x");
        var mod = s.Sub("mod");
        var plugin = Path.Combine(mod, "Foo.esm");   // upper case on purpose: the folder is matched ignoring case
        File.CreateSymbolicLink(plugin, Path.Combine(outside, "Real.esm"));
        var skipped = new List<(string File, string Why)>();
        var (root, files) = TriageCommand.Gather(TriageInputKind.Plugin, plugin, "unused", skipped);
        var c = Run(root, TriageInputKind.Plugin, plugin, skipped, files);
        Assert.Equal(1, c.SetAsideLinked());
        Assert.Empty(c.Files);
        Assert.Equal(("foo.esm", TriageCommand.LinkWhy), Assert.Single(c.Skipped));
        Assert.Equal(1, c.SkippedLinks);
        Assert.Contains(TriageFiles.Run(c), x => x.Level == TriageLevel.Note && x.Text.StartsWith("1 linked item was skipped", StringComparison.Ordinal));
    }

    [UnixFact]
    public void A_file_inside_a_linked_folder_is_still_only_reached_through_it()
    {
        // the other side of the fix: the file isn't a link, its folder is (a context made without the folder scan)
        using var s = new Sandbox();
        var outside = s.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "a.pex"), "x");
        var mod = s.Sub("mod");
        Directory.CreateSymbolicLink(Path.Combine(mod, "Scripts"), outside);
        var c = Run(mod, TriageInputKind.Folder, mod, [], ["scripts\\a.pex"]);
        Assert.Equal(1, c.SetAsideLinked());
        Assert.Equal(("scripts\\a.pex", TriageCommand.LinkReachedWhy), Assert.Single(c.Skipped));
        Assert.Equal(0, c.SkippedLinks);
    }

    [UnixFact]
    public void Two_links_that_differ_only_in_case_get_one_LOOK_each()
    {
        // 0.9.1 named both "tex", so the report had two identical LOOKs and the note counted one
        using var s = new Sandbox();
        var outside = s.Sub("outside");
        var mod = s.Sub("mod");
        File.WriteAllText(Path.Combine(mod, "foo.esm"), "x");
        Directory.CreateSymbolicLink(Path.Combine(mod, "Tex"), outside);
        Directory.CreateSymbolicLink(Path.Combine(mod, "tex"), outside);
        var skipped = new List<(string File, string Why)>();
        var (root, files) = TriageCommand.Gather(TriageInputKind.Folder, mod, "unused", skipped);
        Assert.Equal([("Tex", TriageCommand.LinkWhy), ("tex", TriageCommand.LinkWhy)], skipped);
        var c = Run(root, TriageInputKind.Folder, mod, skipped, files);
        Assert.Equal(2, c.SkippedLinks);
        var looks = TriageFiles.Run(c).Where(x => x.Level == TriageLevel.Look).Select(x => x.File).ToList();
        Assert.Equal(["Tex", "tex"], looks.Order(StringComparer.Ordinal));
    }
}
