// The 0.23.0 sound-event check's record side: the walk over every SoundReference and WWED, the two misses, the
// override and master-GUID rules, the per-plugin cap, conflict's counts, and release's rules (sound-event-missing,
// sound-bank-manifest-missing, sound-bank-name, music-track-silent). Every bank, manifest and plugin is made up here.
// Each fails on 0.22.0, which has no sound-event check.
using System.Buffers.Binary;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;
using static SfModCheck.Tests.SoundBanksTests;

namespace SfModCheck.Tests;

public class SoundEventsTests
{
    // made-up GUIDs, as the manifests write them
    const string G1 = "{11111111-2222-3333-4444-555555555555}", G2 = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
                 G3 = "{01234567-89AB-CDEF-0123-456789ABCDEF}", G4 = "{FEDCBA98-7654-3210-FEDC-BA9876543210}",
                 G5 = "{0F0F0F0F-0F0F-0F0F-0F0F-0F0F0F0F0F0F}", G6 = "{12121212-3434-5656-7878-909090909090}";

    /// <summary>The GUID as a record stores it (Mutagen's Guid): the manifest's two halves, each little-endian.</summary>
    internal static Guid Rec(string manifestGuid)
    {
        var u = AssetFiles.ParseSoundGuid(manifestGuid)!.Value;
        var b = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)(u >> 64));
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), (ulong)u);
        return new Guid(b);
    }

    static SoundReference Snd(string? start, string? stop = null) =>
        new() { Start = start == null ? Guid.Empty : Rec(start), Stop = stop == null ? Guid.Empty : Rec(stop) };

    static StarfieldMod Mod(string name) => new(Mutagen.Bethesda.Plugins.ModKey.FromFileName(name), StarfieldRelease.Starfield);

    [Fact]
    public void The_walk_finds_every_sound_guid_with_its_field()
    {
        Assert.Equal(G3, AssetFiles.SoundGuidText(AssetFiles.SoundGuid(Rec(G3))));   // the helper matches the conversion
        var mod = Mod("SeWalk.esm");
        var misc = mod.MiscItems.AddNew("SE_Misc");
        misc.PickupSound = Snd(G1, G2);
        misc.DropdownSound = Snd(null);   // empty: nothing
        var mgef = mod.MagicEffects.AddNew("SE_Effect");
        mgef.Sounds.Add(new MagicEffectSound { Sound = Snd(G3) });
        var scene = new Scene(mod) { EditorID = "SE_Scene", Actions = new() };
        mod.Quests.AddNew("SE_Quest").Scenes.Add(scene);
        scene.Actions.Add(new DialogueSceneAction { WED0 = Snd(G4) });   // an action type behind the abstract ASceneAction
        var wwed = mod.WWiseEventDatas.AddNew("SE_Event");
        wwed.Start = Rec(G5); wwed.End = Rec(G6);
        var track = mod.MusicTracks.AddNew("SE_Track");
        track.Type = MusicTrack.TypeEnum.SingleTrack;
        track.MTSH = Snd(G1);

        string Fields(IMajorRecordGetter r) => string.Join(";", SoundRefs.Guids(r).Select(x => $"{x.Field}={AssetFiles.SoundGuidText(AssetFiles.SoundGuid(x.Guid))}"));
        Assert.Equal($"PickupSound.Start={G1};PickupSound.Stop={G2}", Fields(misc));
        Assert.Equal($"Sounds[0].Sound.Start={G3}", Fields(mgef));
        Assert.Equal($"Actions[0].WED0.Start={G4}", Fields(scene));
        Assert.Equal($"WWED.Start={G5};WWED.End={G6}", Fields(wwed));
        Assert.Equal($"MTSH.Start={G1}", Fields(track));
        Assert.False(SoundRefs.IsSilentTrack(track));
        var plain = mod.EquipTypes.AddNew("SE_Equip");   // records with components can (a vehicle component holds a sound)
        Assert.True(!SoundRefs.MayHaveSounds(plain), string.Join(",", SoundRefs.Plan(plain.GetType()).Select(p => p.Name + ":" + p.PropertyType)));

        // the typed walk of a whole plugin (the master-GUID rule) covers the record types the research found
        var types = SoundRefs.RecordTypes.Value.Select(t => t.Name).ToHashSet();
        foreach (var t in new[] { "IMiscItemGetter", "IWeaponGetter", "IActivatorGetter", "IMagicEffectGetter", "ISceneGetter", "IDialogResponsesGetter",
                                  "IMusicTrackGetter", "IWWiseEventDataGetter", "INpcGetter", "IRaceGetter", "IImpactGetter", "IExplosionGetter", "IProjectileGetter" })
            Assert.Contains(t, types);
        Assert.DoesNotContain("IEquipTypeGetter", types);
        Assert.Equal(new[] { G1, G2, G3, G4, G5, G6 }.Select(g => AssetFiles.ParseSoundGuid(g)!.Value).ToHashSet(), SoundRefs.AllGuids(mod));
    }

    [Fact]
    public void A_single_track_with_no_event_is_silent()
    {
        var mod = Mod("SeTrack.esm");
        var none = mod.MusicTracks.AddNew("SE_None");
        none.Type = MusicTrack.TypeEnum.SingleTrack;
        var empty = mod.MusicTracks.AddNew("SE_Empty");
        empty.Type = MusicTrack.TypeEnum.SingleTrack;
        empty.MTSH = Snd(null);
        var mapped = mod.MusicTracks.AddNew("SE_Mapped");
        mapped.Type = MusicTrack.TypeEnum.SingleTrack;
        mapped.MTSH = new SoundReference();
        mapped.MTSH.EventMapping.SetTo(mod.WWiseEventDatas.AddNew("SE_Map"));
        var palette = mod.MusicTracks.AddNew("SE_Palette");
        palette.Type = MusicTrack.TypeEnum.Palette;
        Assert.Equal(new[] { true, true, false, false }, new[] { none, empty, mapped, palette }.Select(t => SoundRefs.IsSilentTrack(t)));
    }

    /// <summary>Data with one bank archive the INI lists: bank 100 holds events 7001 and 7002; its manifest lists G1
    /// (7001), G2 (7002) and G3 (7003, which no bank holds: miss B).</summary>
    static AssetIndex Index(Sandbox s)
    {
        var data = s.Sub("Data");
        File.WriteAllBytes(Path.Combine(data, "Made - Sounds.ba2"), AssetFilesTests.Gnrl(
            (@"sound\soundbanks\100.bnk", Bnk(140, 7001, 7002)),
            (@"sound\soundbanks\100.json", Manifest("Base", (G1, 7001, "Play_MadeUp_A"), (G2, 7002, "Play_MadeUp_B"), (G3, 7003, "Play_MadeUp_Stale")))));
        var ini = Path.Combine(s.Dir, "Made.ini");
        File.WriteAllText(ini, "[Archive]\nsResourceArchiveList=Made - Sounds.ba2\n");
        return AssetIndex.Build([], [data], [ini], "en");
    }

    [Fact]
    public void A_resolved_guid_is_quiet_and_misses_a_and_b_are_warnings()
    {
        using var s = new Sandbox();
        var index = Index(s);
        var mod = Mod("SeMiss.esm");
        var misc = mod.MiscItems.AddNew("SE_Misc");
        misc.PickupSound = Snd(G1, G2);   // resolved
        misc.DropdownSound = Snd(G4);     // miss A
        mod.MiscItems.AddNew("SE_Misc2").PickupSound = Snd(G4, G3);   // miss A again (one finding), miss B
        var uses = new AssetUses();
        var missing = Assets.Find(mod, index, r => r.EditorID ?? "", uses: uses).Missing.Where(m => m.Kind == "sound-event").ToList();
        Assert.Equal(5, uses.SoundEvents);
        Assert.Equal(2, missing.Count);
        var a = Assert.Single(missing, m => m.Path == G4);
        Assert.Equal(("WARNING", SoundEventState.NotInManifest), (a.Severity, a.SoundState));
        Assert.Equal(new[] { "SE_Misc DropdownSound.Start", "SE_Misc2 PickupSound.Start" }, a.Users);
        Assert.StartsWith($"sound event {G4} is in no loaded sound bank's manifest, so the sound won't play", a.Text);
        Assert.Contains("1 archive and 1 loose folder were searched for sound banks, and banks were found in Made - Sounds.ba2", a.Text);
        var b = Assert.Single(missing, m => m.Path == G3);
        Assert.Equal(SoundEventState.NotInBank, b.SoundState);
        Assert.Contains($"sound event {G3} (Play_MadeUp_Stale, ID 7003) is listed in the bank manifest sound\\soundbanks\\100.json (from Made - Sounds.ba2), but no loaded sound bank holds that event", b.Text);
        Assert.Contains("used by SE_Misc2 PickupSound.Stop", b.Text);
    }

    [Fact]
    public void An_override_keeping_a_masters_dangling_guid_and_a_guid_a_master_names_are_not_reported()
    {
        using var s = new Sandbox();
        var index = Index(s);
        var master = Mod("SeMaster.esm");
        var orig = master.MiscItems.AddNew("SE_Vanilla");
        orig.PickupSound = Snd(G4);   // dangling in the master's own data
        master.Weapons.AddNew("SE_VanillaGun").Equip = Snd(G5);   // dangling too, in another record
        var mod = Mod("SeOver.esm");
        var ov = mod.MiscItems.GetOrAddAsOverride(orig);
        ov.DropdownSound = Snd(G6);   // the override's own new GUID
        mod.MiscItems.AddNew("SE_Copy").PickupSound = Snd(G5);   // a new record copying a master's GUID
        var cache = master.ToImmutableLinkCache();
        IMajorRecordGetter? Original(IMajorRecordGetter r) => cache.TryResolve(r.FormKey, r.Registration.GetterType, out var o) ? o : null;

        var both = Assets.Find(mod, index, r => r.EditorID ?? "", Original, masters: () => [master]).Missing.Where(m => m.Kind == "sound-event");
        Assert.Equal(G6, Assert.Single(both).Path);
        // without the master-GUID rule the copy is reported; without the overridden record the kept GUID is too
        Assert.Equal(new[] { G5, G6 }.Order(), Assets.Find(mod, index, r => r.EditorID ?? "", Original).Missing.Where(m => m.Kind == "sound-event").Select(m => m.Path).Order());
        Assert.Equal(3, Assets.Find(mod, index, r => r.EditorID ?? "").Missing.Count(m => m.Kind == "sound-event"));
    }

    [Fact]
    public void Check_shows_25_sound_event_lines_per_plugin_then_a_count()
    {
        using var s = new Sandbox();
        var index = Index(s);
        var mod = Mod("SeMany.esm");
        for (int i = 0; i < 27; i++) mod.MiscItems.AddNew($"SE_Misc{i}").PickupSound = Snd($"{{{i + 1:X8}-0000-0000-0000-000000000000}}");
        var lines = new List<string>();
        Assets.Check(mod, index, r => r.EditorID ?? "", (sev, check, text) => { if (check == "assets") lines.Add($"{sev} {text}"); });
        Assert.Equal(25, lines.Count(l => l.StartsWith("WARNING sound event {")));
        Assert.Contains("WARNING 2 more sound event(s) are in no loaded sound bank (--verbose lists them all)", lines);
    }

    [Fact]
    public void Check_counts_an_official_plugins_sound_events_without_findings()
    {
        using var s = new Sandbox();
        var index = Index(s);
        var mod = Mod("SeOfficial.esm");
        mod.MiscItems.AddNew("SE_Misc").PickupSound = Snd("{00000001-0000-0000-0000-000000000000}");
        var lines = new List<string>();
        Assets.Check(mod, index, r => r.EditorID ?? "", (sev, check, text) => { if (check == "assets") lines.Add($"{sev} {text}"); }, official: true);
        Assert.DoesNotContain(lines, l => l.Contains("sound event"));
        lines.Clear();
        Assets.Check(mod, index, r => r.EditorID ?? "", (sev, check, text) => { if (check == "assets") lines.Add($"{sev} {text}"); });
        Assert.Contains(lines, l => l.StartsWith("WARNING sound event {"));
    }

    [Fact]
    public void Conflict_counts_sound_events_as_their_own_kind()
    {
        var a = new Conflict.Analysis { AssetsChecked = true };
        a.MissingAssets.Add(new Conflict.AssetFinding("SeMod.esm", new MissingAsset("WARNING", "sound-event", G1, null, ["SE_Misc PickupSound.Start"])));
        a.MissingAssets.Add(new Conflict.AssetFinding("SeMod.esm", new MissingAsset("WARNING", "model", @"meshes\a.nif", null, ["SE_Misc"])));
        Assert.Equal("0 scripts, 1 model, 1 sound event", Conflict.Report.Kinds(a));
        Assert.Contains("1 missing model and 1 missing sound event", Assert.Single(Conflict.Report.AssetActions(a)).Text);
    }

    [Fact]
    public void Release_reports_missing_events_banks_without_manifests_misnamed_banks_and_silent_tracks()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        File.WriteAllBytes(Path.Combine(data, "Starfield - Sounds.ba2"), AssetFilesTests.Gnrl(
            (@"sound\soundbanks\100.bnk", Bnk(140, 7001)), (@"sound\soundbanks\100.json", Manifest("Base", (G1, 7001, "Play_MadeUp_A")))));
        var pkg = s.Sub("RnSound");
        ReleaseNeedsTests.Write(pkg, "RnSound.esm", (mod, misc) =>
        {
            misc.PickupSound = Snd(G1, G3);   // the game's bank, the mod's own bank
            misc.DropdownSound = Snd(G2);     // in no bank: miss A
            var track = mod.MusicTracks.AddNew("RN_Silent");
            track.Type = MusicTrack.TypeEnum.SingleTrack;
        });
        var own = AssetFiles.Fnv1("rnsound");
        File.WriteAllBytes(Path.Combine(pkg, "RnSound - Main.ba2"), AssetFilesTests.Gnrl(
            ($@"sound\soundbanks\{own}.bnk", Bnk(140, 8001)), ($@"sound\soundbanks\{own}.json", Manifest("RnSound", (G3, 8001, "Play_RnSound_A"))),
            (@"sound\soundbanks\12345.bnk", Bnk(140, 8002)),     // misnamed, and no manifest
            (@"sound\soundbanks\100.bnk", Bnk(140, 7001))));     // replaces the game's bank, whose manifest still loads: neither rule

        var f = ReleaseNeeds.Run(ReleaseNeedsTests.Context(s, pkg, data)).ToList();
        var ev = Assert.Single(f, x => x.Rule == "sound-event-missing");
        Assert.Equal((Level.Warning, "RnSound.esm", G2), (ev.Level, ev.Plugin, ev.Subject));
        Assert.StartsWith($"RnSound.esm: sound event {G2} is in no loaded sound bank's manifest", ev.Text);
        var man = Assert.Single(f, x => x.Rule == "sound-bank-manifest-missing");
        Assert.Equal(Level.Warning, man.Level);
        Assert.Contains(@"the sound bank sound\soundbanks\12345.bnk (in RnSound - Main.ba2) has no manifest sound\soundbanks\12345.json", man.Text);
        var name = Assert.Single(f, x => x.Rule == "sound-bank-name");
        Assert.Equal(Level.Note, name.Level);
        Assert.Contains($"which for RnSound.esm is {own}", name.Text);
        Assert.DoesNotContain(f, x => x.Rule.StartsWith("sound-bank") && x.Text.Contains(@"soundbanks\100.bnk"));
        Assert.DoesNotContain(f, x => x.Rule == "sound-event-missing" && x.Subject == G1);   // the replacement bank holds the event
        var silent = Assert.Single(f, x => x.Rule == "music-track-silent");
        Assert.Equal(Level.Note, silent.Level);
        Assert.Contains("RN_Silent", silent.Text);
    }

    [Fact]
    public void Find_full_shows_a_sound_guid_in_the_manifest_form_too()
    {
        // 0.23.0 step 3: .NET's Guid text never matches a bank manifest, so --full adds the manifest form.
        var misc = Mod("SeFind.esm").MiscItems.AddNew("SE_FindMisc");
        misc.PickupSound = Snd(G3);
        var (nodes, _) = FindTree.Build(misc, l => $"[{l.FormKey}]");
        var lines = FindTree.Lines(nodes, 0, "");
        var start = Assert.Single(lines, l => l.TrimStart().StartsWith("Start"));
        Assert.Contains($"{Rec(G3)} (manifest form {G3})", start);
        var json = FindTree.Json(nodes).ToJsonString();
        Assert.DoesNotContain("(manifest form", json);   // the JSON view keeps the plain GUID (code review round 2)
        Assert.Contains($"\"{Rec(G3)}\"", json);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("Stop") && l.Contains("manifest"));   // an empty GUID gets no form
    }
}
