// Triage sections "Files" and "Replaces vanilla" (0.8.0, chunk C1): hashes, archive listings with sizes, name checks,
// layout notes, the vanilla path index and its cache, and replaced game files. Every archive here is made up in the test.
using System.Security.Cryptography;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class TriageFilesTests
{
    static byte[] T(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>A GNRL BA2 (version 2) whose entries have these unpacked sizes; packed entries get a packed size.</summary>
    internal static byte[] Gnrl(params (string Name, uint Size, bool Packed)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(T("BTDX")); w.Write(2u); w.Write(T("GNRL")); w.Write((uint)entries.Length);
        w.Write(0UL); w.Write(0UL);
        foreach (var (_, size, packed) in entries)
        {
            w.Write(0u); w.Write(T("pex\0")); w.Write(0u); w.Write(0u);
            w.Write(0UL); w.Write(packed ? size / 2 + 1 : 0u); w.Write(size); w.Write(0xBAADF00Du);
        }
        w.Write(new byte[16]);
        var names = ms.Position;
        foreach (var (n, _, _) in entries) { var b = Encoding.UTF8.GetBytes(n); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ulong)names).CopyTo(bytes, 16);
        return bytes;
    }

    /// <summary>A DX10 BA2 (version 3) whose textures have these chunks (unpacked sizes; packed when the flag is set).</summary>
    internal static byte[] Dx10(params (string Name, (uint Size, bool Packed)[] Chunks)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(T("BTDX")); w.Write(3u); w.Write(T("DX10")); w.Write((uint)entries.Length);
        w.Write(0UL); w.Write(0UL); w.Write(0u);
        foreach (var (_, chunks) in entries)
        {
            w.Write(0u); w.Write(T("dds\0")); w.Write(0u);
            w.Write((byte)0); w.Write((byte)chunks.Length); w.Write((ushort)24);
            w.Write((ushort)4); w.Write((ushort)4); w.Write((byte)1); w.Write((byte)71); w.Write((byte)0); w.Write((byte)0);
            foreach (var (size, packed) in chunks)
            {
                w.Write(0UL); w.Write(packed ? 5u : 0u); w.Write(size); w.Write((ushort)0); w.Write((ushort)0); w.Write(0xBAADF00Du);
            }
        }
        w.Write(new byte[16]);
        var names = ms.Position;
        foreach (var (n, _) in entries) { var b = Encoding.UTF8.GetBytes(n); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ulong)names).CopyTo(bytes, 16);
        return bytes;
    }

    static byte[] Names(params string[] names) => Gnrl(names.Select(n => (n, 10u, false)).ToArray());

    static void Put(string root, string rel, byte[] bytes)
    {
        var p = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    static void Put(string root, string rel, string text) => Put(root, rel, Encoding.UTF8.GetBytes(text));

    /// <summary>A made-up game: Data\Starfield.esm (a stand-in) and these archives.</summary>
    static string Game(Sandbox s, params (string Name, byte[] Bytes)[] archives)
    {
        var data = Path.Combine(s.Sub("Game"), "Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "Starfield.esm"), "x");
        foreach (var (n, b) in archives) File.WriteAllBytes(Path.Combine(data, n), b);
        return data;
    }

    /// <summary>The triage context for a mod folder, as the command makes it.</summary>
    static TriageContext Context(Sandbox s, string modRoot, string dataDir)
    {
        var (root, files) = TriageCommand.Gather(TriageInputKind.Folder, modRoot, "unused");
        var report = new TriageReport { InputName = "Mod", HasGame = File.Exists(Path.Combine(dataDir, "Starfield.esm")), DataDir = dataDir };
        return new TriageContext
        {
            Options = new TriageOptions { DataDir = dataDir }, Kind = TriageInputKind.Folder, InputPath = modRoot, Root = root, Files = files,
            DataPrefix = files.Count > 0 && files.All(f => f.StartsWith("data\\", StringComparison.Ordinal)) ? "data\\" : "",
            DataDir = dataDir, Work = s.Sub("work"), Report = report,
        };
    }

    static List<TriageFinding> Run(TriageContext c)
    {
        VanillaIndex.ForgetLoaded();
        var f = TriageFiles.Run(c).ToList();
        c.Report.Findings.AddRange(f);
        return f;
    }

    static string List(IEnumerable<TriageFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Section}] {x.File}: {x.Text}"));

    static TriageFinding One(List<TriageFinding> f, TriageLevel level, string section, string file, string fragment)
    {
        var hits = f.Where(x => x.Level == level && x.Section == section && x.File == file && x.Text.Contains(fragment)).ToList();
        Assert.True(hits.Count == 1, $"wanted one {level} [{section}] {file} ~'{fragment}', got:\n{List(f)}");
        return hits[0];
    }

    static TriageTable Table(TriageContext c, string title) => c.Report.Tables.Single(t => t.Title == title);

    [Fact]
    public void Archive_entries_have_their_sizes_and_compression()
    {
        using var s = new Sandbox();
        var g = Path.Combine(s.Dir, "g.ba2");
        File.WriteAllBytes(g, Gnrl(("Scripts/A.pex", 100, false), ("meshes\\b.nif", 2000, true)));
        Assert.Equal([new TriageFiles.ArchiveEntry("scripts\\a.pex", 100, false), new TriageFiles.ArchiveEntry("meshes\\b.nif", 2000, true)],
            TriageFiles.ReadEntries(g));
        var d = Path.Combine(s.Dir, "d.ba2");
        File.WriteAllBytes(d, Dx10(("textures\\a.dds", [(100, false), (50, true)]), ("textures\\b.dds", [])));
        Assert.Equal([new TriageFiles.ArchiveEntry("textures\\a.dds", 150, true), new TriageFiles.ArchiveEntry("textures\\b.dds", 0, false)],
            TriageFiles.ReadEntries(d));
        // a damaged one names the file
        var bad = Gnrl(("a.pex", 1, false));
        Array.Resize(ref bad, 30);
        File.WriteAllBytes(g, bad);
        Assert.Contains("g.ba2", Assert.Throws<InvalidDataException>(() => TriageFiles.ReadEntries(g)).Message);
    }

    [Theory]
    [InlineData("Foo-1.2.esm", "1.2")]
    [InlineData("Foo_v2.esm", "v2")]
    [InlineData("Foo v1.0.3 - Main.ba2", "v1.0.3")]
    [InlineData("Foo_1_2.zip", "1_2")]
    [InlineData("RoachFollower1.2.esm", "1.2")]
    [InlineData("Foo - Textures01.ba2", null)]
    [InlineData("SFBGS003 - Main01.ba2", null)]
    [InlineData("GrafsHabs-Nova1x1.esm", null)]
    [InlineData("sfta01.esm", null)]
    public void Version_numbers_in_names(string name, string? version) => Assert.Equal(version, TriageFiles.VersionIn(name));

    [Fact]
    public void Files_lists_hashes_archives_names_and_layout()
    {
        using var s = new Sandbox();
        var data = Game(s);
        var mod = s.Sub("mod");
        Put(mod, "Foo-1.2.esm", "plugin");
        Put(mod, "Foo-1.2 - Main.ba2", Gnrl(("scripts\\foo.pex", 7, true)));
        Put(mod, "Bar - Main.ba2", Names("meshes\\bar.nif"));              // no Bar plugin
        Put(mod, "Loose.ba2", Names("meshes\\loose.nif"));                 // no " - " part
        Put(mod, "Broken - Main.ba2", "damaged, not BTDX at all......");    // unreadable
        Put(mod, "Extras.zip", "PK");
        Put(mod, "dxgi.dll", "MZ");
        Put(mod, "fomod\\ModuleConfig.xml", "<config/>");
        Put(mod, "Docs\\guide.txt", "read me");
        Put(mod, "Notes.ini", "[x]");
        Put(mod, "readme.txt", "hi");
        Put(mod, "SFSE\\Plugins\\Ok.dll", "MZ");
        var c = Context(s, mod, data);
        var f = Run(c);

        var every = Table(c, "Every file");
        Assert.Equal(c.Files.Count, every.Rows.Count);
        var row = every.Rows.Single(r => r[0] == "foo-1.2.esm");
        Assert.Equal("6", row[1]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("plugin"))).ToLowerInvariant(), row[2]);

        var contents = Table(c, "Archive contents");
        Assert.Contains(contents.Rows, r => r.SequenceEqual(["foo-1.2 - main.ba2", "scripts\\foo.pex", "7", "compressed"]));
        Assert.Contains(contents.Rows, r => r[0] == "bar - main.ba2" && r[1] == "meshes\\bar.nif");

        const string F = TriageSections.Files;
        One(f, TriageLevel.Note, F, "foo-1.2.esm", "version number (1.2)");
        One(f, TriageLevel.Note, F, "foo-1.2.esm", "comes with 1 archive the game loads with it: foo-1.2 - main.ba2");
        One(f, TriageLevel.Look, F, "bar - main.ba2", "no plugin named bar.esm");
        One(f, TriageLevel.Look, F, "loose.ba2", "has no \" - \" part");
        One(f, TriageLevel.Look, F, "broken - main.ba2", "couldn't be read as a game archive");
        One(f, TriageLevel.Note, F, "extras.zip", "triage didn't open it");
        One(f, TriageLevel.Note, F, "dxgi.dll", "a program file at the top of the mod");
        One(f, TriageLevel.Note, F, "fomod\\moduleconfig.xml", "FOMOD installer");
        One(f, TriageLevel.Note, F, "docs\\", "a folder the game doesn't read from Data (1 file)");
        One(f, TriageLevel.Note, F, "notes.ini", "doesn't read INI files from Data");
        Assert.DoesNotContain(f, x => x.File is "readme.txt" or "sfse\\plugins\\ok.dll" or "fomod\\");
        // the words the report never uses
        Assert.DoesNotContain(f, x => x.Text.Contains("malicious", StringComparison.OrdinalIgnoreCase) || x.Text.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_archive_listed_by_an_ini_or_meant_for_another_folder()
    {
        using var s = new Sandbox();
        var data = Game(s);
        var mod = s.Sub("mod");
        Put(mod, "Data\\Shared.ba2", Names("meshes\\a.nif"));
        Put(mod, "Data\\Mod.ini", "[Archive]\nsResourceArchiveList2=Shared.ba2\n");
        Put(mod, "Data\\Opt\\Mod.esm", "x");
        Put(mod, "Data\\Mod - Main.ba2", Names("meshes\\b.nif"));
        Put(mod, "Data\\SFSE\\Plugins\\Ok.dll", "MZ");
        Put(mod, "Data\\Scripts\\Odd.dll", "MZ");
        Put(mod, "Loader.exe", "MZ");
        Put(mod, "Data\\Mod - Patch.esm", "x");                              // a plugin whose name holds " - "
        Put(mod, "Data\\Mod - Patch - Main.ba2", Names("meshes\\c.nif"));
        var c = Context(s, mod, data);
        var f = Run(c);
        const string F = TriageSections.Files;
        One(f, TriageLevel.Note, F, "data\\mod - patch.esm", "comes with 1 archive the game loads with it: mod - patch - main.ba2");
        One(f, TriageLevel.Note, F, "data\\shared.ba2", "data\\mod.ini lists it");
        One(f, TriageLevel.Look, F, "data\\mod - main.ba2", "data\\opt\\mod.esm is in another folder");
        One(f, TriageLevel.Note, F, "data\\opt\\mod.esm", "sits in the folder data\\opt\\, not at the top of the mod");
        One(f, TriageLevel.Note, F, "loader.exe", "outside the mod's Data folder");
        One(f, TriageLevel.Note, F, "data\\scripts\\odd.dll", "inside Data, outside SFSE\\Plugins");
        Assert.DoesNotContain(f, x => x.File == "data\\sfse\\plugins\\ok.dll");
    }

    [Theory]
    [InlineData("data\\scripts\\a.pex", "scripts\\a.pex")]
    [InlineData("scripts\\a.pex", "scripts\\a.pex")]
    [InlineData("mymod\\data\\textures\\a.dds", "textures\\a.dds")]
    [InlineData("00 main\\textures\\a.dds", "textures\\a.dds")]
    [InlineData("00 main\\mod.esm", "mod.esm")]
    [InlineData("docs\\readme.txt", "docs\\readme.txt")]
    public void Game_paths_drop_data_and_one_wrapper_folder(string rel, string game)
    {
        using var s = new Sandbox();
        var c = Context(s, s.Sub("empty"), s.Sub("nogame"));
        Assert.Equal(game, TriageFiles.GamePath(c, rel));
    }

    [Fact]
    public void Replaced_vanilla_scripts_are_looks_and_other_files_are_counted()
    {
        using var s = new Sandbox();
        var data = Game(s,
            ("Starfield - Misc.ba2", Names("Scripts/QuestX.pex", "scripts\\fragments\\quests\\qf_y.pex", "meshes\\a.nif")),
            ("Starfield - Textures01.ba2", Names(Enumerable.Range(0, 40).Select(i => $"textures\\t{i}.dds").ToArray())),
            ("SFBGS003 - Main.ba2", Names("sound\\v.wem")),
            ("OtherMod - Main.ba2", Names("scripts\\othermod.pex")));   // not the game's own: not in the index
        var mod = s.Sub("mod");
        Put(mod, "Data\\Scripts\\QuestX.pex", "pex");
        Put(mod, "Data\\Scripts\\OtherMod.pex", "pex");
        Put(mod, "Data\\Scripts\\Mine.pex", "pex");
        Put(mod, "Data\\Meshes\\A.nif", "nif");
        Put(mod, "Data\\Mod.esm", "x");
        Put(mod, "Data\\Mod - Main.ba2", Names("scripts\\fragments\\quests\\qf_y.pex", "sound\\v.wem", "meshes\\a.nif", "meshes\\new.nif"));
        Put(mod, "Data\\Mod - Textures.ba2", Names(Enumerable.Range(0, 40).Select(i => $"textures\\t{i}.dds").ToArray()));
        var c = Context(s, mod, data);
        var f = Run(c);
        const string R = TriageSections.ReplacesVanilla;
        One(f, TriageLevel.Look, R, "data\\scripts\\questx.pex", "replaces the game's own script questx (in Starfield - Misc.ba2): every mod and quest that uses it gets this copy");
        One(f, TriageLevel.Look, R, "data\\mod - main.ba2", "holds scripts\\fragments\\quests\\qf_y.pex, which replaces the game's own script fragments:quests:qf_y");
        One(f, TriageLevel.Note, R, "data\\mod - textures.ba2", "holds 40 .dds files under textures\\ that replace the game's own");
        One(f, TriageLevel.Note, R, "data\\mod - main.ba2", "holds 1 .wem file under sound\\ that replaces the game's own (sound\\v.wem): the game uses this copy instead");
        One(f, TriageLevel.Note, R, "data\\mod - main.ba2", "1 .nif file under meshes\\");
        One(f, TriageLevel.Note, R, "data\\meshes\\a.nif", "replaces the game's own meshes\\a.nif (in Starfield - Misc.ba2): the game uses this copy instead");
        Assert.DoesNotContain(f, x => x.Text.Contains("othermod") || x.Text.Contains("mine.pex") || x.Text.Contains("new.nif"));
        // 40 textures make one NOTE, but every one is a row
        var table = Table(c, "Game files this mod replaces");
        Assert.Equal(1 + 1 + 1 + 1 + 1 + 40, table.Rows.Count);
        Assert.Contains(table.Rows, r => r.SequenceEqual(["data\\mod - main.ba2", "sound\\v.wem", "SFBGS003 - Main.ba2"]));
        Assert.Equal(2, f.Count(x => x.Level == TriageLevel.Look && x.Section == R));
    }

    [Fact]
    public void Many_replaced_scripts_stop_at_a_count()
    {
        using var s = new Sandbox();
        var names = Enumerable.Range(0, 30).Select(i => $"scripts\\s{i}.pex").ToArray();
        var data = Game(s, ("Starfield - Misc.ba2", Names(names)));
        var mod = s.Sub("mod");
        Put(mod, "Mod.esm", "x");
        Put(mod, "Mod - Main.ba2", Names(names));
        var c = Context(s, mod, data);
        var f = Run(c).Where(x => x.Section == TriageSections.ReplacesVanilla && x.Level == TriageLevel.Look).ToList();
        Assert.Equal(TriageFiles.MaxScriptLooks + 1, f.Count);
        One(f, TriageLevel.Look, TriageSections.ReplacesVanilla, "", "and 5 more of the game's own scripts are replaced");
    }

    [Fact]
    public void Game_names_on_the_mod_s_own_plugins_and_archives_are_looks()
    {
        using var s = new Sandbox();
        var data = Game(s, ("Starfield - Misc.ba2", Names("scripts\\a.pex")));
        var mod = s.Sub("mod");
        Put(mod, "Starfield - Misc.ba2", Names("scripts\\a.pex"));
        Put(mod, "SFBGS003 - Main.ba2", Names("meshes\\x.nif"));
        Put(mod, "ShatteredSpace.esm", "x");
        var c = Context(s, mod, data);
        var f = Run(c);
        const string R = TriageSections.ReplacesVanilla;
        One(f, TriageLevel.Look, R, "starfield - misc.ba2", "overwrites the game's file");
        One(f, TriageLevel.Look, R, "sfbgs003 - main.ba2", "named after the game's own plugin sfbgs003.esm");
        One(f, TriageLevel.Look, R, "shatteredspace.esm", "the game's own plugin");
        // not also reported as archives without a plugin
        Assert.DoesNotContain(f, x => x.Section == TriageSections.Files && x.Text.Contains("no plugin named"));
    }

    [Fact]
    public void Without_a_game_one_note_says_nothing_was_compared()
    {
        using var s = new Sandbox();
        var mod = s.Sub("mod");
        Put(mod, "scripts\\a.pex", "x");
        var c = Context(s, mod, s.Sub("nogame"));
        var f = Run(c).Where(x => x.Section == TriageSections.ReplacesVanilla).ToList();
        Assert.Single(f);
        Assert.Contains("no game found", f[0].Text);
        Assert.Equal(TriageLevel.Note, f[0].Level);
    }

    [Fact]
    public void A_game_without_official_archives_says_so()
    {
        using var s = new Sandbox();
        var data = Game(s, ("SomeMod - Main.ba2", Names("scripts\\a.pex")));
        var mod = s.Sub("mod");
        Put(mod, "scripts\\a.pex", "x");
        var f = Run(Context(s, mod, data)).Where(x => x.Section == TriageSections.ReplacesVanilla).ToList();
        Assert.Contains("no readable official archives", Assert.Single(f).Text);
    }

    [Fact]
    public void The_index_is_cached_and_rebuilt_when_an_archive_changes()
    {
        using var s = new Sandbox();
        var data = Game(s, ("Starfield - Misc.ba2", Names("scripts\\a.pex", "meshes\\b.nif")),
                           ("ShatteredSpace - Main01.ba2", Names("scripts\\c.pex")),
                           ("Starfield - Broken.ba2", T("damaged, not BTDX at all......")));
        VanillaIndex.ForgetLoaded();
        var first = VanillaIndex.Get(data);
        Assert.True(first.Built);
        Assert.Equal(3, first.Count);
        Assert.Equal("Starfield - Misc.ba2", first.ArchiveOf("Scripts/A.pex"));
        Assert.Equal("ShatteredSpace - Main01.ba2", first.ArchiveOf("scripts\\c.pex"));
        Assert.Null(first.ArchiveOf("scripts\\nothere.pex"));
        Assert.Equal("Starfield - Broken.ba2", Assert.Single(first.Unreadable).Archive);
        Assert.NotNull(first.CachePath);
        Assert.StartsWith(Path.Combine(s.Dir, "cache", "vanilla-index"), first.CachePath);

        VanillaIndex.ForgetLoaded();
        var second = VanillaIndex.Get(data);
        Assert.False(second.Built);
        Assert.Equal(first.CachePath, second.CachePath);
        Assert.Equal("ShatteredSpace - Main01.ba2", second.ArchiveOf("scripts\\c.pex"));
        Assert.Single(second.Unreadable);

        // a game update: an archive's size and time change, so the index is built again and the old cache removed
        File.WriteAllBytes(Path.Combine(data, "ShatteredSpace - Main01.ba2"), Names("scripts\\c.pex", "scripts\\d.pex"));
        File.SetLastWriteTimeUtc(Path.Combine(data, "ShatteredSpace - Main01.ba2"), DateTime.UtcNow.AddMinutes(5));
        VanillaIndex.ForgetLoaded();
        var third = VanillaIndex.Get(data);
        Assert.True(third.Built);
        Assert.Equal("ShatteredSpace - Main01.ba2", third.ArchiveOf("scripts\\d.pex"));
        Assert.Equal([Path.GetFileName(third.CachePath!)], Directory.GetFiles(VanillaIndex.CacheDir).Select(Path.GetFileName));

        // a damaged cache file is rebuilt, not trusted
        File.WriteAllBytes(third.CachePath!, T("SFMCVIDXjunk"));
        VanillaIndex.ForgetLoaded();
        var fourth = VanillaIndex.Get(data);
        Assert.True(fourth.Built);
        Assert.Equal(4, fourth.Count);
    }

    [Fact]
    public void Exe_report_has_both_sections()
    {
        using var s = new Sandbox();
        var data = Game(s, ("Starfield - Misc.ba2", Names("scripts\\questx.pex")));
        var mod = s.Sub("mod");
        Put(mod, "Scripts\\QuestX.pex", "pex");
        Put(mod, "Mod.esm", "x");
        var out_ = Path.Combine(s.Dir, "report");
        var (code, stdout, stderr) = ExeTests.RunSplit("triage", mod, "--game", Path.GetDirectoryName(data)!, "--out", out_);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("LOOK [Replaces vanilla] scripts\\questx.pex: replaces the game's own script questx", stdout);
        var text = File.ReadAllText(Path.Combine(out_, "mod-triage.txt"));
        Assert.Contains("-- Every file (2 rows) --", text);
        Assert.Contains("-- Game files this mod replaces (1 rows) --", text);
    }

    [Fact]
    public void File_names_with_non_ascii_or_long_paths_are_looks()
    {
        using var s = new Sandbox();
        var data = Game(s);
        var mod = s.Sub("mod");
        const string E = "\u00e9";   // \u00e9
        Put(mod, "Data/Mod.esm", "x");
        Put(mod, "Data/Mod - Main.ba2", Names("meshes/caf" + E + ".nif", "meshes/plain.nif"));
        Put(mod, "Data/Textures/na" + E + "ve.dds", "t");
        Put(mod, "Data/readm" + E + ".txt", "skipped: a document at the top");
        Put(mod, "fomod/" + E + ".png", "skipped: the installer");
        // inside Data: "meshes" + separator (7) + name; 200 characters is quiet, 201 is a LOOK; the Data prefix isn't counted
        Put(mod, "Data/meshes/" + new string('a', 189) + ".nif", "ok");
        Put(mod, "Data/meshes/" + new string('b', 190) + ".nif", "long");
        var c = Context(s, mod, data);
        var f = Run(c);
        const string F = TriageSections.Files;
        One(f, TriageLevel.Look, F, @"data\mod - main.ba2", @"holds meshes\caf" + E + ".nif, whose name has a character outside plain ASCII");
        One(f, TriageLevel.Look, F, @"data\textures\na" + E + "ve.dds", "character outside plain ASCII");
        One(f, TriageLevel.Look, F, @"data\meshes\" + new string('b', 190) + ".nif", "201 characters long, more than 200");
        Assert.DoesNotContain(f, x => x.Level == TriageLevel.Look && (x.File.Contains("readm") || x.File.StartsWith("fomod") || x.File.Contains(new string('a', 189))));
        Assert.DoesNotContain(f, x => x.Text.Contains("plain.nif"));

        // more than five: five named, then one LOOK counts the rest
        var many = s.Sub("many");
        for (int i = 0; i < 8; i++) Put(many, $"Textures/{E}{i}.dds", "t");
        var f2 = Run(Context(s, many, data));
        Assert.Equal(5, f2.Count(x => x.Level == TriageLevel.Look && x.Text.Contains("outside plain ASCII")));
        One(f2, TriageLevel.Look, F, "", "and 3 more file names like these");
    }

    [Fact]
    public void Docs_name_the_triage_file_names_and_the_stale_face_note()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "TriageFiles.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src/TriageFiles.cs not found above " + AppContext.BaseDirectory);
        string Doc(string n) => File.ReadAllText(Path.Combine(dir!.FullName, n));
        var readme = Doc("README.md");
        Assert.DoesNotContain("`archive-name` and `file-name` run only in `release`, not in `triage`", readme);
        Assert.Contains("(the `release` rule `file-name`, since 0.15.0;", readme);
        Assert.DoesNotContain("so a face left over from the master isn't noticed", readme);
        Assert.Contains("the baked face is stale", readme);
        var changes = Doc("CHANGELOG.md").Split("\n## 0.14.0")[0];
        Assert.Contains("`triage` reports the `release` rule `file-name` too", changes);
        Assert.Contains("the baked face is stale", changes);
        var agents = Doc("AGENTS.md");
        Assert.Contains("`triage` reports the same file names as LOOKs", agents);
        Assert.Contains("the face is stale", agents);
    }
}
