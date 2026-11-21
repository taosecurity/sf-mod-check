// 0.10.0 item 5a (README "Known gaps" until 0.9.1): paths with a part ending in a dot or a space, and the learned
// list of fields every official record of a type has (the fields listing).
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Starfield.Assets;
using Xunit;

namespace SfModCheck.Tests;

public class KnownGapsTests
{
    [Theory]
    [InlineData("meshes\\armor.\\helm.nif", "meshes\\armor\\helm.nif")]      // a middle part loses one trailing dot
    [InlineData("meshes\\armor..\\helm.nif", "meshes\\armor..\\helm.nif")]   // but not when a dot comes before it
    [InlineData("meshes\\armor \\helm.nif", "meshes\\armor \\helm.nif")]     // and never a space
    [InlineData("meshes\\armor. .\\helm.nif", "meshes\\armor. \\helm.nif")]
    [InlineData("meshes\\helm.nif.", "meshes\\helm.nif")]                    // the last part loses all trailing dots and spaces
    [InlineData("meshes\\helm.nif. .", "meshes\\helm.nif")]
    [InlineData("meshes\\helm.nif", "meshes\\helm.nif")]
    public void Windows_trims_a_path_the_way_windows_does(string path, string trimmed) =>
        Assert.Equal(trimmed, AssetIndex.WindowsTrim(path));

    // "Test either way": on Windows, the rule must agree with what the file system really opens.
    [WindowsFact("compares the rule with Windows' own file system")]
    public void The_trim_rule_matches_the_real_file_system_on_windows()
    {
        using var s = new Sandbox();
        Directory.CreateDirectory(Path.Combine(s.Dir, "sub"));
        File.WriteAllBytes(Path.Combine(s.Dir, "sub", "x.txt"), new byte[1]);
        foreach (var p in new[] { "sub.\\x.txt", "sub..\\x.txt", "sub \\x.txt", "sub. .\\x.txt", "sub\\x.txt.", "sub\\x.txt ", "sub\\x.txt. ", "sub\\x.txt.." })
            Assert.True(File.Exists(Path.Combine(s.Dir, p)) == (AssetIndex.WindowsTrim(p) == "sub\\x.txt"), "the rule disagrees with Windows on " + p);
    }

    [Fact]
    public void Odd_part_finds_dot_and_space_endings_only()
    {
        Assert.True(AssetIndex.OddPart("meshes\\armor.\\helm.nif"));
        Assert.True(AssetIndex.OddPart("meshes\\armor \\helm.nif"));
        Assert.True(AssetIndex.OddPart("meshes\\helm.nif "));
        Assert.False(AssetIndex.OddPart("meshes\\.\\helm.nif"));
        Assert.False(AssetIndex.OddPart("meshes\\armor\\helm.nif"));
    }

    [Fact]
    public void A_model_path_windows_trims_to_a_loose_file_is_its_own_warning()
    {
        using var w = new World();
        var p = w.Build("oddmodel", m => m.MiscItems.First().Model = new Model { File = new AssetLink<StarfieldModelAssetType>("SfmTest.\\Thing.nif") });
        // nothing there either way: missing, with the trimmed name
        Expect.Finding(w.Check(p), "WARNING", "assets", "model meshes\\sfmtest.\\thing.nif is in no loose file or loaded archive, and it has a part that ends in a dot or a space (Windows would open meshes\\sfmtest\\thing.nif");
        // the trimmed file is loose: the game may load it on Windows, but not once packed
        Directory.CreateDirectory(Path.Combine(w.Dir, "meshes", "SfmTest"));
        File.WriteAllBytes(Path.Combine(w.Dir, "meshes", "SfmTest", "Thing.nif"), new byte[4]);
        Expect.Finding(w.Check(p), "WARNING", "assets", "Windows trims it and opens the loose file meshes\\sfmtest\\thing.nif");
    }

    [Fact]
    public void A_script_windows_trims_to_a_loose_file_is_a_warning_not_an_error()
    {
        using var w = new World();
        var p = w.Build("oddscript", m => m.MiscItems.First().VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "SfmTest.:Odd" } } });
        Expect.Finding(w.Check(p), "ERROR", "assets", "(Windows would open scripts\\sfmtest\\odd.pex");
        Directory.CreateDirectory(Path.Combine(w.Dir, "scripts", "SfmTest"));
        File.WriteAllBytes(Path.Combine(w.Dir, "scripts", "SfmTest", "Odd.pex"), new byte[4]);
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", "script SfmTest.:Odd (scripts\\sfmtest.\\odd.pex) has a part that ends in a dot or a space");
        Assert.DoesNotContain(r.Findings, f => f.Severity == "ERROR" && f.Check == "assets");
        // a normal missing script keeps its old text
        var q = w.Build("plainscript", m => m.MiscItems.First().VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "SfmTest:Gone" } } });
        Expect.Finding(w.Check(q), "ERROR", "assets", "script SfmTest:Gone (scripts\\sfmtest\\gone.pex) is in no loose file or loaded archive; used by");
    }

    static readonly Dictionary<string, Dictionary<string, long>> Table = RequiredFields.Parse(new StringReader(
        "# test\nKYWD CNAM 7041\nKYWD TNAM 7041\nSTAT OBND 100\n"));

    static List<(string, byte[])> Fields(params string[] types) => types.Select(t => (t, new byte[1])).ToList();

    [Fact]
    public void A_record_missing_an_always_present_field_is_listed_once_per_type_and_field()
    {
        var f = new RequiredFields.Findings();
        RequiredFields.Check(new RawRecord("KYWD", 0, 0x01000800, 0, 0, 0), Fields("EDID", "CNAM"), () => "A", f, Table);
        RequiredFields.Check(new RawRecord("KYWD", 0, 0x01000801, 0, 0, 0), Fields("EDID"), () => "B", f, Table);
        RequiredFields.Check(new RawRecord("KYWD", 0, 0x01000802, 0, 0, 0), Fields("CNAM", "TNAM"), () => "C", f, Table);
        RequiredFields.Check(new RawRecord("MISC", 0, 0x01000803, 0, 0, 0), Fields("EDID"), () => "D", f, Table);   // no list for MISC
        Assert.Equal(3, f.Checked);
        Assert.Equal(new[]
        {
            "1 KYWD record has no CNAM field, which all 7,041 KYWD records in the official masters have, e.g. B",
            "2 KYWD records have no TNAM field, which all 7,041 KYWD records in the official masters have, e.g. A, B",
        }, RequiredFields.Texts(f, Table).ToArray());
    }

    [Fact]
    public void Deleted_records_and_partial_forms_are_not_checked()
    {
        var f = new RequiredFields.Findings();
        RequiredFields.Check(new RawRecord("STAT", Flags.Deleted, 0x01000800, 0, 0, 0), Fields(), () => "A", f, Table);
        RequiredFields.Check(new RawRecord("STAT", 0x4000, 0x01000801, 0, 0, 0), Fields("EDID"), () => "B", f, Table);
        Assert.Equal(0, f.Checked);
        Assert.Empty(f.Missing);
    }

    [Fact]
    public void The_learned_list_is_built_in_and_the_listing_is_not_a_finding()
    {
        Assert.True(RequiredFields.TypeCount > 100, "data\\required-fields-seen.txt isn't built in");
        Assert.True(RequiredFields.FieldCount > RequiredFields.TypeCount);
        // a Mutagen keyword has no TNAM (official keywords all do): logged, never a finding
        using var w = new World();
        var p = w.Build("fields");
        Expect.No(w.Check(p), "fields");
    }
}
