// sf-mod-check 0.27.0: ba2 find (Ba2Find.cs; the 0.27.0 design notes, parts C and D). All plugins and archives are
// made up: two empty plugins (ModA.esm, ModB.esm, in that order) and small archives written here with the name-table
// writer the 0.6.0 tests use. No game files are needed.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

/// <summary>The two made-up plugins, built once; each test copies them into its own Data folder.</summary>
public sealed class Ba2FindPlugins : Sandbox
{
    public readonly string Folder;

    public Ba2FindPlugins()
    {
        Folder = Sub("plugins");
        var opts = new WriteOptions { GapCheck = false, FixEffects = false };
        foreach (var name in new[] { "ModA.esm", "ModB.esm" })
        {
            var m = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            m.MiscItems.AddNew(name[..4] + "_Misc");
            using var g = new GameData(Folder);
            PluginWriter.Write(m, Folder, g, opts);
        }
    }
}

public class Ba2FindTests(Ba2FindPlugins plugins) : IClassFixture<Ba2FindPlugins>
{
    /// <summary>One test's game: a Data folder with the two plugins and a Plugins.txt listing ModA then ModB.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Txt;

        public Game(Ba2FindPlugins p)
        {
            Data = Sub("Game\\Data");
            foreach (var f in Directory.GetFiles(p.Folder, "*.esm")) File.Copy(f, Path.Combine(Data, Path.GetFileName(f)));
            Txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(Txt, ["*ModA.esm", "*ModB.esm"]);
        }

        public void Archive(string name, string type, params string[] names) =>
            File.WriteAllBytes(Path.Combine(Data, name), AssetsTests.Ba2(type, names));

        public string Loose(string dir, string rel)
        {
            var p = Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, [1, 2, 3]);
            return p;
        }

        public (int Code, string Out, string Err) Run(params string[] args) =>
            ExeTests.RunSplit(["ba2", "find", .. args, "--data", Data, "--plugins", Txt, "--quiet"]);
    }

    /// <summary>The holder lines printed under one name (the numbered lines after it), without their numbers.</summary>
    static List<string> Holders(string stdout, string name)
    {
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var at = lines.IndexOf(name);
        Assert.True(at >= 0, $"{name} not listed:\n{stdout}");
        return lines.Skip(at + 1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal))
            .Select(l => l.Trim()[(l.Trim().IndexOf(". ", StringComparison.Ordinal) + 2)..]).ToList();
    }

    /// <summary>The holder lines, compared without regard to case: on Windows an exact name's loose path is printed in
    /// the pattern's case.</summary>
    static void Same(List<string> expected, List<string> actual) => Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_loose_file_beats_a_later_archive_which_beats_an_earlier_one()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"sound\voice\moda.esm\guard\0001_1.wem", @"meshes\shared.nif");
        g.Archive("ModB - Main.ba2", "GNRL", @"Sound/Voice/ModA.esm/Guard/0001_1.wem", @"meshes\shared.nif");
        var loose = g.Loose(g.Data, @"Sound\Voice\ModA.esm\Guard\0001_1.wem");
        var (code, o, e) = g.Run(@"sound\voice\moda.esm\guard\0001_1.wem");
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)", "ModB - Main.ba2 (named after ModB.esm)", $"loose: {loose}   <- wins"],
            Holders(o, @"sound\voice\moda.esm\guard\0001_1.wem"));
        Assert.Contains("RESULT: 1 file matched in 2 of 2 archives and 1 loose folder", o);
        (code, o, e) = g.Run("MESHES/SHARED.NIF");
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)", "ModB - Main.ba2 (named after ModB.esm)   <- wins"], Holders(o, @"meshes\shared.nif"));
    }

    [Fact]
    public void A_loose_file_over_an_archive_gets_a_note_unless_the_ini_lets_loose_files_replace_archived_ones()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\shared.nif");
        g.Loose(g.Data, @"meshes\shared.nif");
        g.Loose(g.Data, @"meshes\only.nif");
        // a later --ini replaces the keys of the account's own StarfieldCustom.ini, so the test doesn't depend on it
        var off = Path.Combine(g.Dir, "Off.ini");
        File.WriteAllText(off, "[Archive]\nbInvalidateOlderFiles=0\nsResourceDataDirsFinal=STRINGS\\\n");
        var on = Path.Combine(g.Dir, "On.ini");
        File.WriteAllText(on, "[Archive]\nbInvalidateOlderFiles=1\nsResourceDataDirsFinal=\n");
        var (code, o, e) = g.Run(@"meshes\*.nif", "--ini", off);
        Assert.True(code == 0, o + e);
        Assert.Contains("note: 1 name is both loose and in a loaded archive. A loose file replaces the archived copy only when an INI sets [Archive] bInvalidateOlderFiles=1 and sResourceDataDirsFinal=", o);
        (code, o, e) = g.Run(@"meshes\*.nif", "--ini", off, "--json");
        Assert.True(code == 0, o + e);
        Assert.False(JsonDocument.Parse(o).RootElement.GetProperty("looseFilesReplaceArchives").GetBoolean());
        (code, o, e) = g.Run(@"meshes\*.nif", "--ini", off, "--ini", on);
        Assert.True(code == 0, o + e);
        Assert.DoesNotContain("note:", o);
        (code, o, e) = g.Run(@"meshes\*.nif", "--ini", on, "--json");
        Assert.True(code == 0, o + e);
        Assert.True(JsonDocument.Parse(o).RootElement.GetProperty("looseFilesReplaceArchives").GetBoolean());
        // a name that is only loose needs no note
        (code, o, e) = g.Run(@"meshes\only.nif", "--ini", off);
        Assert.True(code == 0, o + e);
        Assert.DoesNotContain("note:", o);
    }

    [Fact]
    public void An_ini_archive_comes_before_the_plugins_archives()
    {
        using var g = new Game(plugins);
        g.Archive("ModB - Main.ba2", "GNRL", @"scripts\a.pex");
        g.Archive("Base - Misc.ba2", "GNRL", @"scripts\a.pex");
        var ini = Path.Combine(g.Dir, "Extra.ini");
        File.WriteAllText(ini, "[Archive]\nsResourceStartUpArchiveList=Base - Misc.ba2\n");
        var (code, o, e) = g.Run(@"scripts\a.pex", "--ini", ini);
        Assert.True(code == 0, o + e);
        Same(["Base - Misc.ba2 (listed in Extra.ini (sResourceStartUpArchiveList))", "ModB - Main.ba2 (named after ModB.esm)   <- wins"], Holders(o, @"scripts\a.pex"));
        // without the INI the archive isn't loaded
        (code, o, _) = g.Run(@"scripts\a.pex");
        Same(["ModB - Main.ba2 (named after ModB.esm)   <- wins"], Holders(o, @"scripts\a.pex"));
    }

    [Fact]
    public void A_texture_archive_serves_only_dds_files()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"textures\moda\readme.txt");
        g.Archive("ModB - Textures.ba2", "DX10", @"textures\moda\readme.txt", @"textures\moda\wall_color.dds");
        var (code, o, e) = g.Run(@"textures\moda\*");
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)   <- wins", "ModB - Textures.ba2 (named after ModB.esm, texture archive, not served from a texture archive)"],
            Holders(o, @"textures\moda\readme.txt"));
        Same(["ModB - Textures.ba2 (named after ModB.esm, texture archive)   <- wins"], Holders(o, @"textures\moda\wall_color.dds"));
    }

    [Fact]
    public void A_glob_walks_below_its_folder_and_one_starting_with_a_wildcard_walks_everything()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"sound\voice\moda.esm\guard\0001_1.wem", @"sound\fx\boom.wem");
        var deep = g.Loose(g.Data, @"Sound\Voice\ModA.esm\Guard\Sub\0002_1.wem");
        g.Loose(g.Data, @"Other\0003_1.wem");
        g.Loose(g.Data, @"Sound\Voice\ModA.esm\Guard\notes.txt");
        var (code, o, e) = g.Run(@"sound\voice\moda.esm\*.wem");
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)   <- wins"], Holders(o, @"sound\voice\moda.esm\guard\0001_1.wem"));
        Same([$"loose: {deep}   <- wins"], Holders(o, @"sound\voice\moda.esm\guard\sub\0002_1.wem"));
        Assert.DoesNotContain(@"other\0003_1.wem", o);
        Assert.DoesNotContain("notes.txt", o);
        Assert.DoesNotContain("boom.wem", o);
        Assert.Contains("RESULT: 2 files matched in 1 of 1 archive and 1 loose folder", o);
        (code, o, e) = g.Run("*.wem");
        Assert.True(code == 0, o + e);
        Assert.Contains(@"other\0003_1.wem", o);
        Assert.Contains(@"sound\fx\boom.wem", o);
        Assert.Contains("RESULT: 4 files matched", o);
    }

    [Fact]
    public void An_extra_data_loose_file_wins_over_the_same_file_in_data()
    {
        using var g = new Game(plugins);
        var extra = g.Sub("Extra");
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif");
        var inData = g.Loose(g.Data, @"meshes\a.nif");
        var inExtra = g.Loose(extra, @"Meshes\A.nif");
        var (code, o, e) = g.Run(@"meshes\a.nif", "--extra-data", extra);
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)", $"loose: {inData}", $"loose: {inExtra}   <- wins"], Holders(o, @"meshes\a.nif"));
        Assert.Contains("and 2 loose folders", o);
    }

    [Fact]
    public void Archives_from_the_archives_folder_are_not_loaded_and_never_win()
    {
        using var g = new Game(plugins);
        var incoming = g.Sub("Incoming");
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif");
        File.WriteAllBytes(Path.Combine(incoming, "NewMod - Main.ba2"), AssetsTests.Ba2("GNRL", @"meshes\a.nif", @"meshes\new.nif"));
        g.Archive("ModA - Voices_de.ba2", "GNRL", @"meshes\a.nif");   // skipped by the load order (language en)
        var (code, o, e) = g.Run(@"meshes\*", "--archives", incoming);
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)   <- wins", "NewMod - Main.ba2 (not loaded: in --archives Incoming)"], Holders(o, @"meshes\a.nif"));
        Same(["NewMod - Main.ba2 (not loaded: in --archives Incoming)"], Holders(o, @"meshes\new.nif"));
        // --archives on Data itself adds only what the load order didn't load (the other language's voices)
        (code, o, e) = g.Run(@"meshes\a.nif", "--archives", g.Data);
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)   <- wins", "ModA - Voices_de.ba2 (not loaded: in --archives Data)"], Holders(o, @"meshes\a.nif"));
        // a loose file is listed before the not-loaded archives and wins
        var loose = g.Loose(g.Data, @"meshes\new.nif");
        (code, o, _) = g.Run(@"meshes\new.nif", "--archives", incoming);
        Same([$"loose: {loose}   <- wins", "NewMod - Main.ba2 (not loaded: in --archives Incoming)"], Holders(o, @"meshes\new.nif"));
    }

    [Fact]
    public void An_unreadable_archive_is_listed_and_the_search_goes_on()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif");
        var broken = AssetsTests.Ba2("GNRL", @"meshes\a.nif");
        BitConverter.GetBytes(1_000_000u).CopyTo(broken, 12);   // more names than the table holds: the type reads, the names don't
        File.WriteAllBytes(Path.Combine(g.Data, "ModB - Main.ba2"), broken);
        File.WriteAllText(Path.Combine(g.Data, "ModB - Extra.ba2"), "not an archive");
        var (code, o, e) = g.Run(@"meshes\a.nif");
        Assert.True(code == 0, o + e);
        Same(["ModA - Main.ba2 (named after ModA.esm)   <- wins"], Holders(o, @"meshes\a.nif"));
        Assert.Contains("unreadable: ModB - Main.ba2: ModB - Main.ba2: its header says 1000000 files", o);
        Assert.Contains("unreadable: ModB - Extra.ba2: ModB - Extra.ba2 is not a BA2 archive", o);
        Assert.Contains("RESULT: 1 file matched in 1 of 1 archive and 0 loose folders", o);
    }

    [Fact]
    public void Other_language_voices_are_skipped_unless_asked_for()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Voices_en.ba2", "GNRL", @"sound\voice\moda.esm\guard\0001_1.wem");
        g.Archive("ModA - Voices_de.ba2", "GNRL", @"sound\voice\moda.esm\guard\0001_1.wem");
        var (code, o, e) = g.Run(@"sound\voice\*");
        Assert.True(code == 0, o + e);
        Same(["ModA - Voices_en.ba2 (named after ModA.esm)   <- wins"], Holders(o, @"sound\voice\moda.esm\guard\0001_1.wem"));
        (code, o, e) = g.Run(@"sound\voice\*", "--language", "de");
        Assert.True(code == 0, o + e);
        Same(["ModA - Voices_de.ba2 (named after ModA.esm)   <- wins"], Holders(o, @"sound\voice\moda.esm\guard\0001_1.wem"));
        (code, o, _) = g.Run(@"sound\voice\*", "--json");
        using var doc = JsonDocument.Parse(o);
        Assert.Contains(doc.RootElement.GetProperty("skipped").EnumerateArray(),
            s => s.GetProperty("archive").GetString() == "ModA - Voices_de.ba2" && s.GetProperty("why").GetString()!.Contains("another language"));
    }

    [Fact]
    public void Max_caps_the_names_listed_but_not_the_count()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif", @"meshes\b.nif", @"meshes\c.nif");
        var (code, o, e) = g.Run(@"meshes\*.nif", "--max", "2");
        Assert.True(code == 0, o + e);
        Assert.Contains(@"meshes\b.nif", o);
        Assert.DoesNotContain(@"meshes\c.nif", o);
        Assert.Contains("... and 1 more (raise --max to see them; --max 0 lists them all)", o);
        Assert.Contains("RESULT: 3 files matched in 1 of 1 archive", o);
        (_, o, _) = g.Run(@"meshes\*.nif", "--max", "0");
        Assert.Contains(@"meshes\c.nif", o);
    }

    [Fact]
    public void No_match_is_exit_1_and_bad_input_is_exit_2()
    {
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif");
        var (code, o, _) = g.Run(@"meshes\missing.nif");
        Assert.Equal(1, code);
        Assert.Contains(@"RESULT: NOT FOUND (no file matches meshes\missing.nif in 1 archive and 1 loose folder)", o);
        (code, o, var e) = g.Run(@"..\Starfield.ini");
        Assert.Equal(2, code);
        Assert.Contains("has a .. part, which leaves Data", e);
        Assert.Contains("RESULT: CANNOT CHECK (the pattern can't be used)", o);
        (code, _, e) = g.Run(@"meshes\a.nif", "--archives", Path.Combine(g.Dir, "nowhere"));
        Assert.Equal(2, code);
        Assert.Contains("there is no folder", e);
        (code, o, _) = ExeTests.RunSplit("ba2", "find", "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (no pattern given)", o);
        (code, o, _) = ExeTests.RunSplit("ba2", "find", "--help");
        Assert.Equal(0, code);
        Assert.Contains("usage: sf-mod-check ba2 find <pattern>", o);
    }

    [Fact]
    public void Json_has_each_name_with_its_holders()
    {
        using var g = new Game(plugins);
        var incoming = g.Sub("Incoming");
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif");
        g.Archive("ModB - Textures.ba2", "DX10", @"meshes\a.nif");
        File.WriteAllBytes(Path.Combine(incoming, "NewMod - Main.ba2"), AssetsTests.Ba2("GNRL", @"meshes\a.nif"));
        var loose = g.Loose(g.Data, @"meshes\a.nif");
        var (code, o, e) = g.Run(@"meshes\a.nif", "--archives", incoming, "--json");
        Assert.True(code == 0, o + e);
        using var doc = JsonDocument.Parse(o);
        var root = doc.RootElement;
        Assert.Equal("sf-mod-check ba2 find", root.GetProperty("tool").GetString());
        Assert.Equal(@"meshes\a.nif", root.GetProperty("pattern").GetString());
        Assert.Equal(1, root.GetProperty("listed").GetInt32());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("archivesRead").GetInt32());
        Assert.Equal(0, root.GetProperty("unreadable").GetArrayLength());
        var file = root.GetProperty("files")[0];
        Assert.Equal(@"meshes\a.nif", file.GetProperty("name").GetString());
        var h = file.GetProperty("holders").EnumerateArray().ToList();
        Assert.Equal(["archive", "archive", "loose", "archive"], h.Select(x => x.GetProperty("kind").GetString()));
        Assert.Equal("ModA - Main.ba2", h[0].GetProperty("archive").GetString());
        Assert.Equal("GNRL", h[0].GetProperty("type").GetString());
        Assert.Equal("named after ModA.esm", h[0].GetProperty("why").GetString());
        Assert.Equal("DX10", h[1].GetProperty("type").GetString());
        Assert.False(h[1].GetProperty("served").GetBoolean());
        Assert.Equal(loose, h[2].GetProperty("path").GetString(), ignoreCase: true);
        Assert.True(h[2].GetProperty("winner").GetBoolean());
        Assert.Equal(JsonValueKind.Null, h[2].GetProperty("type").ValueKind);
        Assert.False(h[3].GetProperty("loaded").GetBoolean());
        Assert.Equal(1, h.Count(x => x.GetProperty("winner").GetBoolean()));
        // no match: still one object, exit 1
        (code, o, _) = g.Run(@"meshes\none.nif", "--json");
        Assert.Equal(1, code);
        using var none = JsonDocument.Parse(o);
        Assert.Equal(0, none.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Json_keys_are_exactly_the_designs()
    {
        // part C's JSON, key for key (tool, version and loadOrder added as the other JSON commands have them)
        using var g = new Game(plugins);
        g.Archive("ModA - Main.ba2", "GNRL", @"meshes\a.nif");
        g.Archive("ModA - Voices_de.ba2", "GNRL", @"meshes\a.nif");
        File.WriteAllText(Path.Combine(g.Data, "ModB - Main.ba2"), "not an archive");
        g.Loose(g.Data, @"meshes\a.nif");
        var (code, o, e) = g.Run(@"meshes\a.nif", "--json");
        Assert.True(code == 0, o + e);
        using var doc = JsonDocument.Parse(o);
        var root = JsonShape.Exactly(doc.RootElement, "ba2 find", "tool:string", "version:string", "pattern:string", "loadOrder:string",
            "files:array", "listed:number", "total:number", "archivesRead:number", "looseFilesReplaceArchives:bool", "skipped:array", "unreadable:array");
        JsonShape.Exactly(root.GetProperty("skipped")[0], "skipped[0]", "archive:string", "why:string");
        JsonShape.Exactly(root.GetProperty("unreadable")[0], "unreadable[0]", "archive:string", "why:string");
        var file = JsonShape.Exactly(root.GetProperty("files")[0], "files[0]", "name:string", "holders:array");
        var h = file.GetProperty("holders");
        // an archive holder names the archive, a loose one its path; type is null for a loose file
        JsonShape.Exactly(h[0], "archive holder", "kind:string", "archive:string", "why:string", "type:string", "loaded:bool", "served:bool", "winner:bool");
        JsonShape.Exactly(h[1], "loose holder", "kind:string", "path:string", "why:string", "type:string?", "loaded:bool", "served:bool", "winner:bool");
        Assert.Equal("loose", h[1].GetProperty("kind").GetString());
    }

    [Fact]
    public void The_archive_order_holds_texture_archives_and_leaves_the_general_order_alone()
    {
        // 0.27.0 changed AssetIndex to keep one GNRL-plus-DX10 order; the sound-bank order (GNRL only) must not change
        using var s = new Sandbox();
        var data = s.Sub("Data");
        foreach (var a in new[] { "Made - Sounds01.ba2", "Made - Textures.ba2", "MadeMod - Main.ba2", "MadeMod - Textures.ba2" })
            File.WriteAllBytes(Path.Combine(data, a), AssetsTests.Ba2(a.Contains("Textures") ? "DX10" : "GNRL", @"x\a.dds"));
        var ini = Path.Combine(s.Dir, "Made.ini");
        File.WriteAllText(ini, "[Archive]\nsResourceStartUpArchiveList=Made - Textures.ba2, Made - Sounds01.ba2\n");
        foreach (var names in new[] { true, false })
        {
            var index = AssetIndex.Build(["MadeMod.esm"], [data], [ini], "en", names);
            Assert.Equal(["Made - Textures.ba2", "Made - Sounds01.ba2", "MadeMod - Main.ba2", "MadeMod - Textures.ba2"],
                index.ArchivesInLoadOrder().Select(x => Path.GetFileName(x.Path)));
            Assert.Equal(["DX10", "GNRL", "GNRL", "DX10"], index.ArchivesInLoadOrder().Select(x => x.Type));
            Assert.Equal(["Made - Sounds01.ba2", "MadeMod - Main.ba2"], index.GeneralArchivesInLoadOrder().Select(Path.GetFileName));
        }
    }
}
