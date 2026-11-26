// The 0.23.0 sound-bank index: the record-GUID conversion, the Wwise short ID, a bank's events, a bank manifest's
// events, and the index over made-up archives and loose files (load order, replacement, the four answers, damaged and
// unproven files). Every bank and manifest is built here; none comes from the game. Each fails on 0.22.0, which has
// none of these.
using System.Text;
using System.Text.Json;
using Xunit;

namespace SfModCheck.Tests;

public class SoundBanksTests
{
    /// <summary>A made-up Wwise sound bank: BKHD of this version, then HIRC with one action (type 3), one Sound
    /// (type 2) and one Event object (type 4) per ID.</summary>
    internal static byte[] Bnk(uint version, params uint[] eventIds)
    {
        var hirc = new MemoryStream();
        var h = new BinaryWriter(hirc);
        h.Write((uint)eventIds.Length + 2);
        h.Write((byte)3); h.Write(6u); h.Write(99u); h.Write((ushort)0);
        h.Write((byte)2); h.Write(20u); h.Write(500u); h.Write(0x00040001u); h.Write((byte)2); h.Write(77u); h.Write(1234u); h.Write(new byte[3]);
        foreach (var id in eventIds) { h.Write((byte)4); h.Write(9u); h.Write(id); h.Write((byte)1); h.Write(42u); }
        h.Flush();
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BKHD")); w.Write(8u); w.Write(version); w.Write(4242u);
        w.Write(Encoding.ASCII.GetBytes("HIRC")); w.Write((uint)hirc.Length); w.Write(hirc.ToArray());
        w.Flush();
        return o.ToArray();
    }

    /// <summary>A made-up per-bank manifest in Wwise's SoundBanksInfo shape, one bank with these events (ID as a
    /// string, as Wwise writes it).</summary>
    internal static byte[] Manifest(string bank, params (string Guid, uint Id, string Name)[] events)
    {
        var doc = new
        {
            SoundBanksInfo = new
            {
                Platform = "Windows",
                SoundBanks = new[]
                {
                    new
                    {
                        Id = AssetFiles.Fnv1(bank).ToString(), ShortName = bank,
                        IncludedEvents = events.Select(e => new { Id = e.Id.ToString(), Name = e.Name, GUID = e.Guid }).ToArray(),
                    },
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc);
    }

    // made-up GUIDs and events
    const string G1 = "{11111111-2222-3333-4444-555555555555}", G2 = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
                 G3 = "{01234567-89AB-CDEF-0123-456789ABCDEF}", G4 = "{FEDCBA98-7654-3210-FEDC-BA9876543210}",
                 G5 = "{0F0F0F0F-0F0F-0F0F-0F0F-0F0F0F0F0F0F}";

    static UInt128 G(string s) => AssetFiles.ParseSoundGuid(s)!.Value;

    [Fact]
    public void A_record_guid_reads_as_two_little_endian_halves()
    {
        // the research's worked example (SmcLookup\wwise-research NOTES, question 1): IDs only
        var stored = Convert.FromHexString("8b44223471a74e0298c9d89fa7baabbe");
        Assert.Equal("{024EA771-3422-448B-BEAB-BAA79FD8C998}", AssetFiles.SoundGuidText(AssetFiles.SoundGuid(stored)));
        // through a .NET Guid made from the stored bytes, as Mutagen reads it; its own ToString never matches
        var asMutagen = new Guid(stored);
        Assert.Equal(AssetFiles.SoundGuid(stored), AssetFiles.SoundGuid(asMutagen));
        Assert.NotEqual("024ea771-3422-448b-beab-baa79fd8c998", asMutagen.ToString());
        // the manifest's text, any case, with or without braces and dashes
        Assert.Equal(AssetFiles.SoundGuid(stored), AssetFiles.ParseSoundGuid("{024EA771-3422-448B-BEAB-BAA79FD8C998}"));
        Assert.Equal(AssetFiles.SoundGuid(stored), AssetFiles.ParseSoundGuid("024ea7713422448bbeabbaa79fd8c998"));
        Assert.Null(AssetFiles.ParseSoundGuid("{024EA771-3422-448B-BEAB-BAA79FD8C99}"));   // 31 digits
        Assert.Null(AssetFiles.ParseSoundGuid("{024EA771-3422-448B-BEAB-BAA79FD8C99Z}"));
        Assert.Null(AssetFiles.ParseSoundGuid(null));
        Assert.Throws<ArgumentException>(() => AssetFiles.SoundGuid(new byte[15]));
    }

    [Fact]
    public void A_wwise_short_id_is_fnv1_of_the_lower_case_name()
    {
        Assert.Equal(3371767461u, AssetFiles.Fnv1("MadeUpSounds"));
        Assert.Equal(3371767461u, AssetFiles.Fnv1("madeupsounds"));
        Assert.Equal(1802953484u, AssetFiles.Fnv1("Play_MadeUp_Door_Open"));
    }

    [Fact]
    public void A_bank_lists_its_event_ids_and_its_streamed_sources_still()
    {
        var bank = Bnk(140, 7001, 7002, 7001);
        var (v, ids) = AssetFiles.BankEvents(bank);
        Assert.Equal(140u, v);
        Assert.Equal(new HashSet<uint> { 7001, 7002 }, ids);
        Assert.Equal(new uint[] { 77 }, AssetFiles.BankSources(bank).Streamed);   // the shared chunk walk
        var (v2, ids2) = AssetFiles.BankEvents(Bnk(150, 7001));
        Assert.Equal(150u, v2);
        Assert.Empty(ids2);   // a version other than 140 is not read past its header
        Assert.Throws<InvalidDataException>(() => AssetFiles.BankEvents(bank[..40]));
        Assert.Throws<InvalidDataException>(() => AssetFiles.BankEvents(Encoding.ASCII.GetBytes("RIFF not a bank at all")));
        // an object whose length runs past its chunk
        var bad = (byte[])bank.Clone();
        const int at = 64;   // the first Event object: 24 (BKHD, HIRC header) + 4 (count) + 11 (action) + 25 (sound)
        BitConverter.GetBytes(100000u).CopyTo(bad, at + 1);
        Assert.Contains("ends early", Assert.Throws<InvalidDataException>(() => AssetFiles.BankEvents(bad)).Message);
    }

    [Fact]
    public void A_manifest_lists_each_events_guid_id_and_name()
    {
        var (list, skipped) = AssetFiles.ManifestEvents(Manifest("MadeUpSounds", (G1, 7001, "Play_MadeUp_A"), (G2.ToLowerInvariant(), 7002, "Play_MadeUp_B"), ("not a guid", 7003, "Bad")));
        Assert.Equal(1, skipped);
        Assert.Equal(new[] { (G(G1), 7001u, "Play_MadeUp_A", "MadeUpSounds"), (G(G2), 7002u, "Play_MadeUp_B", "MadeUpSounds") }, list);
        // a number ID is read too
        var num = Encoding.UTF8.GetBytes($$$"""{"SoundBanksInfo":{"SoundBanks":[{"ShortName":"X","IncludedEvents":[{"Id":7004,"Name":"N","GUID":"{{{G3}}}"}]}]}}""");
        Assert.Equal(7004u, AssetFiles.ManifestEvents(num).Events.Single().Id);
        // damaged: cut short, or another JSON file
        Assert.ThrowsAny<JsonException>(() => AssetFiles.ManifestEvents(Manifest("MadeUpSounds", (G1, 7001, "A"))[..30]));
        Assert.Throws<InvalidDataException>(() => AssetFiles.ManifestEvents("""{"PluginInfo":{"Plugins":[]}}"""u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => AssetFiles.ManifestEvents("[1,2,3]"u8.ToArray()));
    }

    [Fact]
    public void Only_a_banks_own_files_count()
    {
        Assert.True(SoundBankIndex.IsBankFile(@"sound\soundbanks\3371767461.bnk"));
        Assert.True(SoundBankIndex.IsBankFile(@"sound\soundbanks\3371767461.json"));
        Assert.False(SoundBankIndex.IsBankFile(@"sound\soundbanks\soundbanksinfo.json"));
        Assert.False(SoundBankIndex.IsBankFile(@"sound\soundbanks\plugininfo.json"));
        Assert.False(SoundBankIndex.IsBankFile(@"sound\soundbanks\77.wem"));
        Assert.False(SoundBankIndex.IsBankFile(@"sound\soundbanks\sub\1.bnk"));
        Assert.False(SoundBankIndex.IsBankFile(@"sound\voice\1.bnk"));
    }

    static void Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void The_index_follows_the_load_order_and_answers_each_case()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        const string bnk = @"sound\soundbanks\100.bnk", man = @"sound\soundbanks\100.json";
        // the base game: 01 has an old copy of bank 100, the patch replaces it; the project file is never read
        File.WriteAllBytes(Path.Combine(data, "Made - Sounds01.ba2"), AssetFilesTests.Gnrl(
            (bnk, Bnk(140, 1)), (man, Manifest("Base", (G1, 1, "Old")))));
        File.WriteAllBytes(Path.Combine(data, "Made - SoundsPatch.ba2"), AssetFilesTests.Gnrl(
            (bnk, Bnk(140, 7001, 7002)), (man, Manifest("Base", (G1, 7001, "Play_MadeUp_A"), (G2, 7002, "Play_MadeUp_B"), (G4, 7999, "Play_MadeUp_Cut"))),
            (@"sound\soundbanks\soundbanksinfo.json", Manifest("Project", (G5, 7001, "Play_MadeUp_A")))));
        // a mod's archive, packed with zlib: its bank is version 150 (not read), and a damaged manifest
        File.WriteAllBytes(Path.Combine(data, "MadeMod - Main.ba2"), Ba2CommandTests.Gnrl(
            (@"sound\soundbanks\200.bnk", Bnk(150, 8001), Ba2CommandTests.Pack.Zlib),
            (@"sound\soundbanks\200.json", Manifest("MadeMod", (G3, 8001, "Play_MadeMod_Door")), Ba2CommandTests.Pack.Zlib),
            (@"Sound\SoundBanks\300.JSON", "{ not json"u8.ToArray(), Ba2CommandTests.Pack.Stored)));
        // a loose manifest replaces the archives' copy of 100.json and adds one more event
        Put(Path.Combine(data, "Sound", "SoundBanks", "100.json"), Manifest("Base", (G1, 7001, "Play_MadeUp_A"), (G2, 7002, "Play_MadeUp_B"), (G5, 7002, "Play_MadeUp_Loose")));

        var index = SoundBankIndex.Build(new[] { "Made - Sounds01.ba2", "Made - SoundsPatch.ba2", "MadeMod - Main.ba2" }.Select(a => Path.Combine(data, a)), new[] { data });
        Assert.Equal(new[] { "Made - Sounds01.ba2", "Made - SoundsPatch.ba2", "MadeMod - Main.ba2" }, index.Archives);
        Assert.Equal("Made - SoundsPatch.ba2", index.Banks[bnk]);
        Assert.Equal(Path.Combine(data, "Sound", "SoundBanks", "100.json"), index.Manifests[man]);

        Assert.Equal(SoundEventState.Resolved, index.Resolve(G(G1), out var e1));
        Assert.Equal(("Play_MadeUp_A", 7001u, "Base"), (e1!.Name, e1.Id, e1.Bank));
        Assert.Equal(SoundEventState.Resolved, index.Resolve(G(G5), out var e5));         // the loose manifest, not soundbanksinfo.json
        Assert.Equal("Play_MadeUp_Loose", e5!.Name);
        Assert.Equal(SoundEventState.NotInManifest, index.Resolve(G(G4), out var e4));    // only the replaced patch manifest had it
        Assert.Null(e4);
        Assert.Equal(SoundEventState.Unchecked, index.Resolve(G(G3), out _));             // its bank's version isn't read
        Assert.Contains(index.Notes, n => n.Contains(@"sound\soundbanks\200.bnk (from MadeMod - Main.ba2) is a Wwise sound bank of version 150"));
        Assert.Contains(index.Notes, n => n.StartsWith(@"couldn't read the bank manifest sound\soundbanks\300.json (from MadeMod - Main.ba2)"));
        Assert.Equal(2, index.Notes.Count);
    }

    [Fact]
    public void A_manifest_event_that_no_loaded_bank_holds_is_miss_b()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        File.WriteAllBytes(Path.Combine(data, "MadeMod - Main.ba2"), AssetFilesTests.Gnrl(
            (@"sound\soundbanks\3371767461.bnk", Bnk(140, 7001)),
            (@"sound\soundbanks\3371767461.json", Manifest("MadeUpSounds", (G1, 7001, "Play_MadeUp_A"), (G2, 7002, "Play_MadeUp_Stale")))));
        // a manifest with no bank at all: its events are in no loaded bank either
        Put(Path.Combine(data, "sound", "soundbanks", "9.json"), Manifest("Lonely", (G3, 9001, "Play_MadeUp_Lonely")));
        var index = SoundBankIndex.Build([Path.Combine(data, "MadeMod - Main.ba2")], [data]);
        Assert.Equal(SoundEventState.Resolved, index.Resolve(G(G1), out _));
        Assert.Equal(SoundEventState.NotInBank, index.Resolve(G(G2), out var stale));
        Assert.Equal((7002u, "Play_MadeUp_Stale"), (stale!.Id, stale.Name));
        Assert.Equal(SoundEventState.NotInBank, index.Resolve(G(G3), out _));
        Assert.Empty(index.Notes);
    }

    [Fact]
    public void A_damaged_archive_or_bank_entry_is_a_note_not_a_crash()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        File.WriteAllText(Path.Combine(data, "Broken - Main.ba2"), "not an archive at all");
        // an entry whose record says it is larger than its data (it runs past the end of the archive)
        File.WriteAllBytes(Path.Combine(data, "Short - Main.ba2"), Ba2CommandTests.Gnrl((i, size) => size + 100000,
            (@"sound\soundbanks\5.bnk", Bnk(140, 1), Ba2CommandTests.Pack.Stored)));
        // a zlib entry that unpacks to less than its record says
        File.WriteAllBytes(Path.Combine(data, "Lies - Main.ba2"), Ba2CommandTests.Gnrl((i, size) => size + 10,
            (@"sound\soundbanks\6.bnk", Bnk(140, 1), Ba2CommandTests.Pack.Zlib)));
        var index = SoundBankIndex.Build(new[] { "Broken - Main.ba2", "Short - Main.ba2", "Lies - Main.ba2" }.Select(a => Path.Combine(data, a)), []);
        Assert.Contains(index.Notes, n => n.StartsWith("archive Broken - Main.ba2 couldn't be read for sound banks"));
        Assert.Contains(index.Notes, n => n.StartsWith(@"couldn't read sound\soundbanks\5.bnk (from Short - Main.ba2): its data at byte"));
        Assert.Contains(index.Notes, n => n.StartsWith(@"couldn't read sound\soundbanks\6.bnk (from Lies - Main.ba2): it unpacks to"));
        Assert.Equal(0, index.EventCount);
    }

    [Fact]
    public void The_asset_index_gives_the_ini_archives_first_then_the_plugins()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        foreach (var a in new[] { "Made - Sounds01.ba2", "Made - SoundsPatch.ba2", "MadeMod - Main.ba2", "MadeMod - Textures.ba2" })
            File.WriteAllBytes(Path.Combine(data, a), a.Contains("Textures") ? AssetsTests.Ba2("DX10", @"textures\a.dds") : AssetFilesTests.Gnrl((@"sound\soundbanks\1.json", Manifest("X"))));
        var ini = Path.Combine(s.Dir, "Made.ini");
        File.WriteAllText(ini, "[Archive]\nsResourceStartUpArchiveList=Made - Sounds01.ba2, Made - SoundsPatch.ba2\n");
        var assets = AssetIndex.Build(["MadeMod.esm"], [data], [ini], "en");
        Assert.Equal(new[] { "Made - Sounds01.ba2", "Made - SoundsPatch.ba2", "MadeMod - Main.ba2" },
            assets.GeneralArchivesInLoadOrder().Select(Path.GetFileName));
        Assert.Equal(new[] { Path.GetFullPath(data) }, assets.LooseDirs);
    }

    [Fact]
    public void An_ini_archive_named_after_a_plugin_keeps_its_ini_place()
    {
        // code review 0.23.0 round 2: "Starfield - *.ba2" is named after Starfield.esm, so the plugin loop took the game's
        // archives first; they lost their INI order, and an archive only an INI lists went ahead of them
        using var s = new Sandbox();
        var data = s.Sub("Data");
        foreach (var a in new[] { "Made - Sounds01.ba2", "Made - Sounds02.ba2", "Extra - Sounds.ba2", "MadeMod - Main.ba2" })
            File.WriteAllBytes(Path.Combine(data, a), AssetFilesTests.Gnrl((@"sound\soundbanks\1.json", Manifest("X"))));
        var ini = Path.Combine(s.Dir, "Made.ini");
        File.WriteAllText(ini, "[Archive]\nsResourceStartUpArchiveList=Made - Sounds02.ba2, Made - Sounds01.ba2\nsResourceArchiveList2=Extra - Sounds.ba2\n");
        var assets = AssetIndex.Build(["Made.esm", "MadeMod.esm"], [data], [ini], "en");
        Assert.Equal(new[] { "Made - Sounds02.ba2", "Made - Sounds01.ba2", "Extra - Sounds.ba2", "MadeMod - Main.ba2" },
            assets.GeneralArchivesInLoadOrder().Select(Path.GetFileName));
    }
}
