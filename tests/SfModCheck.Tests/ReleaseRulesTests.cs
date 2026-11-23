// The release command's zip rules (0.7.0, chunk C1): plugin names, sound files in archives, BlueprintShips masters,
// one plugin per zip, script sources, root .md names and private files. Zips, archives and plugin headers are written
// here byte by byte.
using System.IO.Compression;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseRulesTests
{
    // ---------------------------------------------------------------- builders

    static string MakeZip(Sandbox s, string name, params (string Path, byte[] Data)[] entries)
    {
        var zip = Path.Combine(s.Sub("zips"), name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, d) in entries)
        {
            using var w = z.CreateEntry(p).Open();
            w.Write(d);
        }
        return zip;
    }

    static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    static List<ReleaseFinding> Rules(Sandbox s, string zip)
    {
        var p = ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out", Path.GetFileNameWithoutExtension(zip)));
        var c = new ReleaseContext { Options = new ReleaseOptions(), Packages = [p], Work = s.Sub("work") };
        return ReleaseRules.Run(c).ToList();
    }

    static string List(IEnumerable<ReleaseFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Rule}] {x.Text}"));

    static ReleaseFinding One(List<ReleaseFinding> f, string rule)
    {
        var m = f.Where(x => x.Rule == rule).ToList();
        Assert.True(m.Count == 1, $"wanted one [{rule}], got:\n{List(f)}");
        return m[0];
    }

    static void None(List<ReleaseFinding> f, string rule) =>
        Assert.True(f.All(x => x.Rule != rule), $"wanted no [{rule}], got:\n{List(f)}");

    /// <summary>A BA2 with these entries. GNRL entries have one block, DX10 entries the given chunks; a compressed
    /// block has a packed size other than 0. The file data itself is filler (never read).</summary>
    static byte[] Ba2(string type, uint version, params (string Name, bool[] Chunks)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(T("BTDX")); w.Write(version); w.Write(T(type)); w.Write((uint)entries.Length);
        w.Write(0UL);                                    // name table offset, filled in below
        if (version is 2 or 3) w.Write(0UL);
        if (version == 3) w.Write(0u);
        foreach (var (_, chunks) in entries)
        {
            if (type == "GNRL")
            {
                w.Write(0u); w.Write(T("wem\0")); w.Write(0u); w.Write(0u);
                w.Write(0UL); w.Write(chunks[0] ? 10u : 0u); w.Write(20u); w.Write(0xBAADF00Du);
            }
            else
            {
                w.Write(0u); w.Write(T("dds\0")); w.Write(0u);
                w.Write((byte)0); w.Write((byte)chunks.Length); w.Write((ushort)24);
                w.Write((ushort)4); w.Write((ushort)4); w.Write((byte)1); w.Write((byte)71); w.Write((byte)0); w.Write((byte)0);
                foreach (var packed in chunks)
                {
                    w.Write(0UL); w.Write(packed ? 10u : 0u); w.Write(20u); w.Write((ushort)0); w.Write((ushort)0); w.Write(0xBAADF00Du);
                }
            }
        }
        w.Write(new byte[64]);                           // the file data
        var names = ms.Position;
        foreach (var (n, _) in entries) { var b = T(n); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ulong)names).CopyTo(bytes, 16);
        return bytes;
    }

    static (string, bool[]) G(string name, bool packed) => (name, [packed]);

    /// <summary>A plugin that is only a TES4 header: HEDR, then a MAST + DATA pair per master.</summary>
    internal static byte[] Plugin(params string[] masters)
    {
        using var body = new MemoryStream();
        using (var b = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
        {
            b.Write(T("HEDR")); b.Write((ushort)12); b.Write(0.96f); b.Write(0u); b.Write(0x800u);
            foreach (var m in masters)
            {
                var z = T(m + "\0");
                b.Write(T("MAST")); b.Write((ushort)z.Length); b.Write(z);
                b.Write(T("DATA")); b.Write((ushort)8); b.Write(0UL);
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(T("TES4")); w.Write((uint)body.Length); w.Write(1u); w.Write(0u); w.Write(0u); w.Write((ushort)578); w.Write((ushort)0);
        w.Write(body.ToArray());
        w.Flush();
        return ms.ToArray();
    }

    static readonly byte[] X = T("x");

    // ---------------------------------------------------------------- Ba2Names.ReadEntries

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ReadEntries_gnrl_gives_names_and_compression(uint version)
    {
        using var s = new Sandbox();
        var f = Path.Combine(s.Dir, "A.ba2");
        File.WriteAllBytes(f, Ba2("GNRL", version, G("Sound/Voice/A/x.wem", true), G("sound/voice/a/y.wem", false)));
        var e = Ba2Names.ReadEntries(f);
        Assert.Equal(new Ba2Names.Entry[] { new("sound\\voice\\a\\x.wem", true), new("sound\\voice\\a\\y.wem", false) }, e);
        Assert.Equal(e.Select(x => x.Name), Ba2Names.Read(f));
    }

    [Fact]
    public void ReadEntries_dx10_is_compressed_if_any_chunk_is()
    {
        using var s = new Sandbox();
        var f = Path.Combine(s.Dir, "T.ba2");
        File.WriteAllBytes(f, Ba2("DX10", 2, ("textures\\a.dds", [false, false]), ("textures\\b.dds", [false, true]), ("textures\\c.dds", [])));
        Assert.Equal(new[] { false, true, false }, Ba2Names.ReadEntries(f).Select(x => x.Compressed));
    }

    [Fact]
    public void ReadEntries_refuses_records_that_run_into_the_name_table()
    {
        using var s = new Sandbox();
        var f = Path.Combine(s.Dir, "Bad.ba2");
        var b = Ba2("GNRL", 1, G("a.wem", false));
        BitConverter.GetBytes(40UL).CopyTo(b, 16);       // name table inside the one 36-byte record
        // a valid name table at 40 (one empty name), so only the record check can fail
        b[40] = 0; b[41] = 0;
        File.WriteAllBytes(f, b);
        var ex = Assert.Throws<InvalidDataException>(() => Ba2Names.ReadEntries(f));
        Assert.Contains("Bad.ba2", ex.Message);
    }

    // ---------------------------------------------------------------- engine rules

    [Theory]
    [InlineData("MyMod-ShatteredSpace.esm", true)]
    [InlineData("MyMod-constellation.esp", true)]
    [InlineData("Fix-SFBGS003.esm", true)]
    [InlineData("Fix-SFTA01.esl", true)]
    [InlineData("Fix-Starfield.esm", true)]
    [InlineData("Fix-OldMars.esm", true)]
    [InlineData("BlueprintShips-Mine.esm", true)]
    [InlineData("MYMOD-CONSTELLATION.ESM", true)]
    [InlineData("BLUEPRINTSHIPS-MINE.ESM", true)]
    [InlineData("Data\\Sub/MyMod-ShatteredSpace.esm", true)]
    [InlineData("MyMod-ShatteredSpaceAddon.esm", false)]
    [InlineData("ShatteredSpace.esm", false)]
    [InlineData("MyMod.esm", false)]
    [InlineData("MyBlueprintShips-x.esm", false)]
    public void Dropped_plugin_names_match_reference_output(string name, bool dropped) =>
        Assert.Equal(dropped, ReleaseRules.DroppedName(name));

    [Fact]
    public void Plugin_name_rule_is_an_engine_problem()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("MyMod-ShatteredSpace.esm", Plugin())));
        var r = One(f, "plugin-name");
        Assert.Equal((Level.Problem, RuleKind.Engine), (r.Level, r.Kind));
        Assert.Contains("Mod-0.1.0.zip: plugin name MyMod-ShatteredSpace.esm", r.Text);
        Assert.Contains("rename it", r.Text);
        None(Rules(s, MakeZip(s, "Ok-0.1.0.zip", ("MyMod-ShatteredSpaceAddon.esm", Plugin()))), "plugin-name");
    }

    [Fact]
    public void Compressed_voice_is_an_engine_problem_one_line_per_archive()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Voice-0.1.0.zip",
            ("Voice - Voices_en.ba2", Ba2("GNRL", 2, G("sound\\voice\\voice.esm\\a\\1.wem", false), G("sound\\voice\\voice.esm\\a\\2.wem", true),
                                               G("sound\\voice\\voice.esm\\a\\2.ffxanim", true), G("scripts\\a.pex", true)))));
        var r = One(f, "voice-compressed");
        Assert.Equal((Level.Problem, RuleKind.Engine), (r.Level, r.Kind));
        Assert.Contains("Voice-0.1.0.zip:Voice - Voices_en.ba2: 2 of 3 sound files", r.Text);
        Assert.Contains("first: sound\\voice\\voice.esm\\a\\2.wem", r.Text);
        Assert.Contains("Repack with Archive2 -compression=None", r.Text);
    }

    [Fact]
    public void Uncompressed_voice_is_a_note_and_an_archive_without_sound_says_nothing()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Voice-0.1.0.zip",
            ("Voice - Voices_en.ba2", Ba2("GNRL", 2, G("sound\\voice\\a\\1.wem", false), G("sound\\voice\\a\\1.ffxanim", false))),
            ("Voice - Main.ba2", Ba2("GNRL", 2, G("scripts\\a.pex", true))),
            ("Voice - Textures.ba2", Ba2("DX10", 2, ("textures\\a.dds", [true])))));
        var r = One(f, "voice-compressed");
        Assert.Equal(Level.Note, r.Level);
        Assert.Contains("Voices_en.ba2: 2 sound files, all stored uncompressed", r.Text);
        None(f, "ba2-unreadable");
    }

    [Fact]
    public void Compressed_sound_in_a_dx10_archive_counts_too()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Odd-0.1.0.zip", ("Odd.ba2", Ba2("DX10", 1, ("sound\\x.wem", [false, true])))));
        Assert.Equal(Level.Problem, One(f, "voice-compressed").Level);
    }

    [Fact]
    public void Unreadable_archive_is_a_problem_naming_it()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Bad-0.1.0.zip", ("Bad - Main.ba2", T("not an archive at all, just text"))));
        var r = One(f, "ba2-unreadable");
        Assert.Equal(Level.Problem, r.Level);
        Assert.Contains("Bad-0.1.0.zip:Bad - Main.ba2: can't read the archive", r.Text);
    }

    // ---------------------------------------------------------------- house rules

    [Fact]
    public void Masters_are_read_from_the_header()
    {
        using var s = new Sandbox();
        var p = Path.Combine(s.Dir, "A.esm");
        File.WriteAllBytes(p, Plugin("Starfield.esm", "BlueprintShips-Starfield.esm"));
        Assert.Equal(["Starfield.esm", "BlueprintShips-Starfield.esm"], ReleaseRules.Masters(p));
        File.WriteAllBytes(p, T("junk"));
        Assert.Empty(ReleaseRules.Masters(p));
    }

    [Fact]
    public void BlueprintShips_master_is_a_house_problem()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", Plugin("Starfield.esm", "BlueprintShips-Starfield.esm"))));
        var r = One(f, "master");
        Assert.Equal((Level.Problem, RuleKind.House), (r.Level, r.Kind));
        Assert.Contains("MyMod.esm has the master BlueprintShips-Starfield.esm", r.Text);
        Assert.Contains("remove that master", r.Text);
        None(Rules(s, MakeZip(s, "Ok-0.1.0.zip", ("MyMod.esm", Plugin("Starfield.esm", "SFBGS003.esm")))), "master");
    }

    [Fact]
    public void Two_plugins_in_a_zip_is_a_house_problem()
    {
        using var s = new Sandbox();
        var r = One(Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod-Patch.esm", Plugin()))), "one-plugin");
        Assert.Equal((Level.Problem, RuleKind.House), (r.Level, r.Kind));
        Assert.Contains("2 plugins in one zip (MyMod-Patch.esm, MyMod.esm)", r.Text);
        None(Rules(s, MakeZip(s, "Ok-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Main.ba2", Ba2("GNRL", 2, G("scripts\\a.pex", false))))), "one-plugin");
    }

    [Fact]
    public void Script_sources_are_a_house_problem()
    {
        using var s = new Sandbox();
        var r = One(Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("Scripts/Source/MyMod/Foo.psc", X), ("Scripts/Source/MyMod/Bar.psc", X))), "script-source");
        Assert.Equal((Level.Problem, RuleKind.House), (r.Level, r.Kind));
        Assert.Contains("2 Papyrus script sources (.psc)", r.Text);
        Assert.Contains(Path.Combine("Scripts", "Source", "MyMod", "Bar.psc"), r.Text);   // the zip's own spelling
        None(Rules(s, MakeZip(s, "Ok-0.1.0.zip", ("Scripts/MyMod/Foo.pex", X))), "script-source");
    }

    [Fact]
    public void Root_md_without_a_mod_name_is_a_house_problem()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("README.md", X), ("LICENSE.md", X), ("README-MyMod.md", X), ("Docs/README.md", X)));
        var md = f.Where(x => x.Rule == "root-md").ToList();
        Assert.Equal(2, md.Count);
        Assert.All(md, x => Assert.Equal((Level.Problem, RuleKind.House), (x.Level, x.Kind)));
        Assert.Contains(md, x => x.Text.Contains("without the mod's name") && x.Text.Contains(": README.md;"));
        Assert.Contains(md, x => x.Text.Contains(": LICENSE.md;"));
        None(Rules(s, MakeZip(s, "Ok-0.1.0.zip", ("README-MyMod.md", X), ("Docs/README.md", X))), "root-md");
    }

    [Fact]
    public void Private_files_are_a_house_problem()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("PERMISSION.md", X), ("Notes/Author permission request.png", X),
            ("COMPARISON.md", X), ("comparison/a.txt", X), ("README-MyMod.md", X)));
        var p = f.Where(x => x.Rule == "private-file").ToList();
        Assert.Equal(4, p.Count);
        Assert.All(p, x => Assert.Equal((Level.Problem, RuleKind.House), (x.Level, x.Kind)));
        Assert.Contains(p, x => x.Text.Contains("permission record") && x.Text.Contains("PERMISSION.md"));
        Assert.Contains(p, x => x.Text.Contains("permission screenshot"));
        Assert.Contains(p, x => x.Text.Contains("private comparison folder"));
        None(Rules(s, MakeZip(s, "Ok-0.1.0.zip", ("README-MyMod.md", X), ("Textures/permissionless.dds", X))), "private-file");
    }

    [Fact]
    public void Clean_zip_gives_no_findings()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin("Starfield.esm")), ("README-MyMod.md", X),
            ("MyMod - Main.ba2", Ba2("GNRL", 2, G("scripts\\mymod\\a.pex", true)))));
        Assert.True(f.Count == 0, List(f));
    }

    // ---------------------------------------------------------------- profiles

    [Fact]
    public void Basic_profile_warns_on_house_rules_and_fails_on_engine_rules()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Mod-0.1.0.zip", ("MyMod-ShatteredSpace.esm", Plugin("BlueprintShips-Starfield.esm")),
            ("Extra.esm", Plugin()), ("README.md", X), ("Scripts/Source/a.psc", X), ("PERMISSION.md", X)));
        var basic = f.Select(x => ReleaseCommand.Apply(x, "basic")).ToList();
        var house = f.Select(x => ReleaseCommand.Apply(x, "house")).ToList();
        Assert.Equal(new[] { "plugin-name" }, basic.Where(x => x.Level == Level.Problem).Select(x => x.Rule));
        Assert.Equal(new[] { "master", "one-plugin", "private-file", "root-md", "script-source" },
            basic.Where(x => x.Level == Level.Warning).Select(x => x.Rule).Distinct().Order());   // PERMISSION.md is a root .md too
        Assert.All(basic.Where(x => x.Level == Level.Warning), x => Assert.EndsWith("(a failure with --profile house)", x.Text));
        Assert.Equal(7, house.Count(x => x.Level == Level.Problem));
    }

    // ---------------------------------------------------------------- 0.14.0: archive-name and file-name

    static List<ReleaseFinding> Rules(Sandbox s, ReleaseOptions o, params string[] zips)
    {
        var ps = zips.Select(z => ReleaseCommand.Unpack(z, Path.Combine(s.Dir, "out", Path.GetFileNameWithoutExtension(z)))).ToList();
        var c = new ReleaseContext { Options = o, Packages = ps, Work = s.Sub("work") };
        return ReleaseRules.Run(c).ToList();
    }

    static byte[] MainBa2() => Ba2("GNRL", 2, G("scripts\\mymod\\a.pex", false));

    [Fact]
    public void Archive_named_after_its_plugin_and_its_xbox_twin_are_quiet()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Main.ba2", MainBa2()), ("MyMod - Main_xbox.ba2", MainBa2()),
            ("Data/Other Mod.esm", Plugin()), ("Data/other mod - Textures.ba2", Ba2("DX10", 2, ("textures\\a.dds", [false])))));
        None(f, "archive-name");
    }

    [Fact]
    public void Misnamed_archive_is_a_problem_with_a_plugin_and_a_warning_without()
    {
        using var s = new Sandbox();
        var with = One(Rules(s, MakeZip(s, "A-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyModMain.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Problem, with.Level);
        Assert.Contains("MyModMain.ba2", with.Text);
        Assert.Contains("never loads", with.Text);
        Assert.Contains("MyMod - Main.ba2", with.Text);   // the fix
        var without = One(Rules(s, MakeZip(s, "B-0.1.0.zip", ("Loose - Main.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Warning, without.Level);
        Assert.Contains("holds no plugin", without.Text);
    }

    [Fact]
    public void Archive_without_spaces_around_the_dash_or_in_a_subfolder_is_a_problem()
    {
        using var s = new Sandbox();
        var dash = One(Rules(s, MakeZip(s, "A-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod-Main.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Problem, dash.Level);
        var sub = One(Rules(s, MakeZip(s, "B-0.1.0.zip", ("MyMod.esm", Plugin()), ("Extras/MyMod - Main.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Problem, sub.Level);
        Assert.Contains("top level", sub.Text);
    }

    [Fact]
    public void Archive_named_after_a_plugin_outside_the_zips_or_listed_by_an_ini()
    {
        using var s = new Sandbox();
        var masters = s.Sub("masters");
        File.WriteAllBytes(Path.Combine(masters, "BaseMod.esm"), Plugin());
        var o = new ReleaseOptions();
        o.MastersDirs.Add(masters);
        // a patch that ships an archive for the mod it patches: loads with BaseMod.esm
        None(Rules(s, o, MakeZip(s, "Patch-0.1.0.zip", ("MyPatch.esm", Plugin()), ("BaseMod - Textures.ba2", MainBa2()))), "archive-name");
        // listed by an INI in the zip: a WARNING
        var ini = One(Rules(s, MakeZip(s, "Ini-0.1.0.zip", ("MyMod.esm", Plugin()), ("Extra.ba2", MainBa2()),
            ("MyMod.ini", T("[Archive]\nsResourceIndexFileList=Extra.ba2\n")))), "archive-name");
        Assert.Equal(Level.Warning, ini.Level);
        Assert.Contains("MyMod.ini", ini.Text);
    }

    // ---------------------------------------------------------------- 0.15.1: parts the game loads by itself

    [Fact]
    public void Archive_part_other_than_main_or_textures_is_a_warning()
    {
        using var s = new Sandbox();
        var f = One(Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Main.ba2", MainBa2()), ("MyMod - Sounds.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Warning, f.Level);
        Assert.Contains("MyMod - Sounds.ba2", f.Text);
        Assert.Contains("Main or Textures", f.Text);
        Assert.Contains("sResourceIndexFileList", f.Text);
        Assert.Contains("rename it, for example to MyMod - Main.ba2", f.Text);   // the fix names the plugin
    }

    [Fact]
    public void Numbered_archive_after_a_gap_is_a_warning()
    {
        using var s = new Sandbox();
        var f = One(Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Main01.ba2", MainBa2()), ("MyMod - Main03.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Warning, f.Level);
        Assert.Contains("MyMod - Main03.ba2", f.Text);
        Assert.Contains("there is no MyMod - Main02.ba2", f.Text);
        // the numbered loop starts at 01 even when the unnumbered archive is there
        var alone = One(Rules(s, MakeZip(s, "B-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Textures.ba2", MainBa2()), ("MyMod - Textures02.ba2", MainBa2()))), "archive-name");
        Assert.Contains("MyMod - Textures02.ba2", alone.Text);
        Assert.Contains("there is no MyMod - Textures01.ba2", alone.Text);
    }

    [Fact]
    public void Main_and_textures_numbered_without_gaps_and_their_xbox_twins_are_quiet()
    {
        using var s = new Sandbox();
        None(Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Main.ba2", MainBa2()), ("MyMod - Main01.ba2", MainBa2()),
            ("MyMod - Main02.ba2", MainBa2()), ("MyMod - Textures.ba2", MainBa2()), ("MyMod - Textures01.ba2", MainBa2()),
            ("MyMod - Main_xbox.ba2", MainBa2()), ("MyMod - Textures_xbox.ba2", MainBa2()))), "archive-name");
        // "Foo - Patch - Main.ba2" belongs to "Foo - Patch.esm", not to Foo.esm with a part "Patch - Main"
        None(Rules(s, MakeZip(s, "Foo-0.1.0.zip", ("Foo.esm", Plugin()), ("Foo - Patch.esm", Plugin()), ("Foo - Patch - Main.ba2", MainBa2()))), "archive-name");
    }

    [Fact]
    public void All_99_numbered_archives_are_quiet_and_do_not_crash()
    {
        // code review round 1 (2026-11-23): with 01 to 99 all there, the gap search found no missing number and threw
        using var s = new Sandbox();
        var files = new List<(string, byte[])> { ("MyMod.esm", Plugin()) };
        for (int n = 1; n <= 99; n++) files.Add(($"MyMod - Main{n:00}.ba2", MainBa2()));
        None(Rules(s, MakeZip(s, "MyMod-0.1.0.zip", files.ToArray())), "archive-name");
    }

    [Fact]
    public void Voice_archives_of_a_language_are_quiet()
    {
        // the official add-ons ship "<plugin> - Voices_en.ba2" (and _de, _es, _fr, _ja), listed in no INI
        using var s = new Sandbox();
        None(Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin()), ("MyMod - Main.ba2", MainBa2()),
            ("MyMod - Voices_en.ba2", MainBa2()), ("MyMod - Voices_de.ba2", MainBa2()), ("MyMod - Voices_fr_ps.ba2", MainBa2()))), "archive-name");
    }

    [Fact]
    public void Patch_archive_for_a_plugin_outside_the_zips_gets_the_part_check_too()
    {
        using var s = new Sandbox();
        var masters = s.Sub("masters");
        File.WriteAllBytes(Path.Combine(masters, "BaseMod.esm"), Plugin());
        var o = new ReleaseOptions();
        o.MastersDirs.Add(masters);
        var f = One(Rules(s, o, MakeZip(s, "Patch-0.1.0.zip", ("MyPatch.esm", Plugin()), ("BaseMod - Voices.ba2", MainBa2()))), "archive-name");
        Assert.Equal(Level.Warning, f.Level);
        Assert.Contains("to BaseMod - Main.ba2", f.Text);
    }

    [Fact]
    public void Docs_name_the_0151_archive_part_warning()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ReleaseRules.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src/ReleaseRules.cs not found above " + AppContext.BaseDirectory);
        string Doc(string n) => File.ReadAllText(Path.Combine(dir!.FullName, n)).Replace("\r\n", "\n");
        var readme = Doc("README.md");
        Assert.Contains("Since 0.15.1, a WARNING also when the part after `<plugin> - ` is none of `Main`, `Textures` (each optionally numbered `01` to `99`) or a language's voice archive", readme);
        Assert.Contains("`MyMod - Main01.ba2` to `MyMod - Main99.ba2` without gaps", readme);
        Assert.Contains("0.15.1 also a WARNING for a plugin's archive whose part is none of `Main`, `Textures` (each optionally numbered\n   `01` to `99`) or a language's voice archive", Doc("AGENTS.md"));
        Assert.Contains("`MyMod - Voices_en.ba2`", readme);
        var changes = Doc("CHANGELOG.md").Split("\n## 0.15.0")[0];
        Assert.Contains("## 0.15.1", changes);
        Assert.Contains("WARNING  [archive-name] MyMod-0.1.0.zip: archive MyMod - Sounds.ba2", changes);
        Assert.Contains("there is no MyMod - Main02.ba2", changes);
    }

    [Fact]
    public void Non_ascii_names_loose_and_in_an_archive_are_warnings()
    {
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "MyMod-0.1.0.zip", ("MyMod.esm", Plugin()), ("Textures/MyMod/Café.dds", X),
            ("MyMod - Main.ba2", Ba2("GNRL", 2, G("scripts\\mymod\\ok.pex", false), G("scripts\\mymod\\naïve.pex", false)))))
            .Where(x => x.Rule == "file-name").ToList();
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.Equal(Level.Warning, x.Level));
        Assert.Contains(f, x => x.Text.Contains("café.dds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(f, x => x.Text.Contains("naïve.pex") && x.Text.Contains("MyMod - Main.ba2"));
    }

    [Fact]
    public void Files_the_game_never_loads_get_no_file_name_warning()
    {
        // code review 0.14.0: a README beside the plugin and the fomod folder aren't game files; a plugin with an
        // accent beside them still counts
        using var s = new Sandbox();
        var f = Rules(s, MakeZip(s, "Docs-0.1.0.zip", ("MyMod.esm", Plugin()), ("Lisez-moi é.txt", X), ("fomod/images/ü.png", X),
            ("Mödl.esp", Plugin()))).Where(x => x.Rule == "file-name").ToList();
        var one = Assert.Single(f);
        Assert.Contains("mödl.esp", one.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loose_path_over_200_characters_is_a_warning_and_200_is_quiet()
    {
        using var s = new Sandbox();
        string PathOf(int length) { var head = "Textures/MyMod/"; return head + new string('a', length - head.Length - 4) + ".dds"; }
        Assert.Equal(201, PathOf(201).Length);
        var f = One(Rules(s, MakeZip(s, "Long-0.1.0.zip", ("MyMod.esm", Plugin()), (PathOf(201), X))), "file-name");
        Assert.Equal(Level.Warning, f.Level);
        Assert.Contains("201 characters", f.Text);
        None(Rules(s, MakeZip(s, "Fine-0.1.0.zip", ("MyMod.esm", Plugin()), (PathOf(200), X))), "file-name");
        // a Data folder in the zip doesn't count toward the length
        None(Rules(s, MakeZip(s, "Data-0.1.0.zip", ("Data/MyMod.esm", Plugin()), ("Data/" + PathOf(200), X))), "file-name");
    }

    [Fact]
    public void Many_file_name_warnings_name_five_and_count_the_rest()
    {
        using var s = new Sandbox();
        var entries = new List<(string, byte[])> { ("MyMod.esm", Plugin()) };
        for (int i = 0; i < 8; i++) entries.Add(($"Textures/MyMod/é{i}.dds", X));
        var f = Rules(s, MakeZip(s, "Many-0.1.0.zip", entries.ToArray())).Where(x => x.Rule == "file-name").ToList();
        Assert.Equal(6, f.Count);
        Assert.Contains("and 3 more", f[^1].Text);
    }
}
