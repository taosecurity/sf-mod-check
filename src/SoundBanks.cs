// The Wwise sound-event index (0.23.0; design: SmcLookup\wwise-research NOTES.md, "Proposed check design", and
// StarfieldPluginCheck NOTES.md, "0.23.0 (SmcLookup)"). Records name a Wwise event by its 128-bit authoring GUID. A
// bank's manifest, sound\soundbanks\<bank id>.json, maps each GUID to the event's 32-bit ID, and the bank,
// sound\soundbanks\<bank id>.bnk, holds the events themselves (HIRC type 4). The index reads every loaded bank and
// manifest, the later archive's copy of a name replacing the earlier one's and a loose file replacing both, and then
// answers, for a GUID: resolved, in no manifest (miss A) or in a manifest but in no loaded bank (miss B).
// soundbanksinfo.json and plugininfo.json are the Wwise project's own files and are not read. Banks and manifests are
// untrusted input: each file is size-capped, and a damaged one is a note, never a crash.
using System.Diagnostics;

namespace SfModCheck;

/// <summary>What the index says about one sound GUID.</summary>
public enum SoundEventState
{
    /// <summary>A loaded manifest lists it and a loaded bank holds its event.</summary>
    Resolved,
    /// <summary>Miss A: no loaded manifest lists the GUID.</summary>
    NotInManifest,
    /// <summary>Miss B: a manifest lists it, but no loaded bank holds the event ID.</summary>
    NotInBank,
    /// <summary>A manifest lists it, but the bank beside that manifest couldn't be read (damaged, or a version that
    /// isn't proven), so whether the event is there is unknown. The bank's note says so; no finding.</summary>
    Unchecked,
}

public sealed class SoundBankIndex
{
    /// <summary>One event a manifest lists: its 32-bit ID, its name, the bank's short name and the manifest it came
    /// from (data-relative name and where it was read).</summary>
    public sealed record Event(uint Id, string Name, string Bank, string Manifest, string Where);

    /// <summary>Largest bank or manifest read (the base game's biggest bank is about 30 MB). A larger one is a note.</summary>
    public const long MaxFileBytes = AssetFiles.MaxFileBytes;

    /// <summary>The Wwise project files that sit beside the banks and are not one bank's manifest.</summary>
    static readonly HashSet<string> ProjectFiles = new(StringComparer.Ordinal) { "soundbanksinfo.json", "plugininfo.json" };

    sealed record Source(string Where, long Size, Func<byte[]> Get, int Seq);

    readonly Dictionary<string, Source> files = new(StringComparer.Ordinal);
    readonly Dictionary<UInt128, Event> guids = new();
    readonly HashSet<uint> events = new();
    readonly HashSet<string> unreadBanks = new(StringComparer.Ordinal);   // bank stems ("1234") not read
    int seq;

    /// <summary>The archives searched for banks and manifests, by file name, in load order (for the finding's
    /// message), and the loose folders searched.</summary>
    public readonly List<string> Archives = new();
    public readonly List<string> LooseDirs = new();

    /// <summary>Damaged, too large, unreadable or unproven-version banks and manifests, one line each.</summary>
    public readonly List<string> Notes = new();

    /// <summary>The banks and manifests used (data-relative name to where it was read: an archive's file name or a
    /// loose path), after replacement.</summary>
    public readonly SortedDictionary<string, string> Banks = new(StringComparer.Ordinal), Manifests = new(StringComparer.Ordinal);

    public int GuidCount => guids.Count;
    public int EventCount => events.Count;

    /// <summary>Whether a data-relative name is a bank or one bank's manifest: "sound\soundbanks\&lt;name&gt;.bnk" or
    /// ".json" directly in that folder, not the Wwise project's soundbanksinfo.json or plugininfo.json.</summary>
    public static bool IsBankFile(string rel)
    {
        const string dir = @"sound\soundbanks\";
        if (!rel.StartsWith(dir, StringComparison.Ordinal)) return false;
        var name = rel[dir.Length..];
        if (name.Length == 0 || name.Contains('\\') || ProjectFiles.Contains(name)) return false;
        return name.EndsWith(".bnk", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal);
    }

    /// <summary>Offers one bank or manifest (rel already normalized). A later offer of the same name replaces an
    /// earlier one, as a later archive's or a loose file does in the game.</summary>
    public void Offer(string rel, string where, long size, Func<byte[]> get)
    {
        if (!IsBankFile(rel)) return;
        files[rel] = new Source(where, size, get, seq++);
    }

    /// <summary>Offers every bank and manifest in a general archive. An archive that can't be read is a note.</summary>
    public void AddArchive(string path)
    {
        var name = Path.GetFileName(path);
        Archives.Add(name);
        try
        {
            var index = Ba2Command.Read(path);
            if (index.Type != "GNRL") return;
            int n = 0;
            foreach (var it in index.Items)
            {
                if (!IsBankFile(it.Name)) continue;
                var item = it;
                Offer(it.Name, name, it.Size, () => { using var f = File.OpenRead(path); return Ba2Command.EntryBytes(f, index, item, MaxFileBytes); });
                n++;
            }
            if (n > 0) Log.Write($"sound banks: {name}: {n} bank(s) and manifest(s)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Notes.Add($"archive {name} couldn't be read for sound banks ({SafeText.OneLine(e.Message)})");
        }
    }

    /// <summary>Offers the loose banks and manifests under one folder's sound\soundbanks (any case).</summary>
    public void AddLoose(string dir)
    {
        LooseDirs.Add(dir);
        try
        {
            foreach (var (rel, file) in LooseFiles(dir))
                Offer(rel, file, new FileInfo(file).Length, () => File.ReadAllBytes(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notes.Add($"sound banks under {dir} couldn't be listed ({SafeText.OneLine(e.Message)})");
        }
    }

    /// <summary>The loose banks and manifests under one folder's sound\soundbanks (any case), as (data-relative name,
    /// full path). Throws IOException or UnauthorizedAccessException when a folder can't be listed.</summary>
    public static List<(string Rel, string Path)> LooseFiles(string dir)
    {
        var list = new List<(string, string)>();
        var ci = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        if (!Directory.Exists(dir)) return list;
        foreach (var sound in Directory.EnumerateDirectories(dir, "sound", ci))
            foreach (var banks in Directory.EnumerateDirectories(sound, "soundbanks", ci))
                foreach (var f in Directory.EnumerateFiles(banks, "*", ci).Order(StringComparer.Ordinal))
                {
                    var rel = Ba2Names.Normalize(@"sound\soundbanks\" + Path.GetFileName(f));
                    if (IsBankFile(rel)) list.Add((rel, f));
                }
        return list;
    }

    /// <summary>The index for a load order: the general archives in load order (AssetIndex.GeneralArchivesInLoadOrder),
    /// then the loose folders (the first folder wins, so they are offered last to first).</summary>
    public static SoundBankIndex Build(IEnumerable<string> archivesInLoadOrder, IEnumerable<string> looseDirsFirstWins)
    {
        var sw = Stopwatch.StartNew();
        var index = new SoundBankIndex();
        foreach (var a in archivesInLoadOrder) index.AddArchive(a);
        foreach (var d in looseDirsFirstWins.Reverse()) index.AddLoose(d);
        index.Finish();
        Log.Write($"sound banks: {index.Banks.Count} bank(s) and {index.Manifests.Count} manifest(s) read from {index.Archives.Count} archive(s) " +
                  $"and {index.LooseDirs.Count} loose folder(s): {index.EventCount:N0} events in banks, {index.GuidCount:N0} event GUIDs in manifests, " +
                  $"{index.Notes.Count} note(s); {sw.Elapsed.TotalSeconds:F2} s");
        return index;
    }

    /// <summary>Reads the offered files that won (in load order, so a later manifest's GUID replaces an earlier
    /// one's). Called once, after the last offer.</summary>
    public void Finish()
    {
        foreach (var (rel, src) in files.OrderBy(kv => kv.Value.Seq))
        {
            var isBank = rel.EndsWith(".bnk", StringComparison.Ordinal);
            var stem = Path.GetFileNameWithoutExtension(rel);
            var label = $"{rel} (from {src.Where})";
            byte[] bytes;
            try
            {
                if (src.Size > MaxFileBytes) throw new InvalidDataException($"it is larger than {MaxFileBytes:N0} bytes");
                bytes = src.Get();
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Notes.Add($"couldn't read {label}: {SafeText.OneLine(e.Message)}");
                if (isBank) unreadBanks.Add(stem);
                continue;
            }
            if (isBank)
            {
                Banks[rel] = src.Where;
                try
                {
                    var (version, ids) = AssetFiles.BankEvents(bytes);
                    if (!AssetFiles.ProvenBankVersions.Contains(version))
                    {
                        Notes.Add($"{label} is a Wwise sound bank of version {version}, and only version {string.Join(", ", AssetFiles.ProvenBankVersions)} is read, so its events weren't indexed");
                        unreadBanks.Add(stem);
                        continue;
                    }
                    events.UnionWith(ids);
                    Log.Write($"sound banks: {label}: version {version}, {ids.Count} event(s)");
                }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    Notes.Add($"couldn't read the sound bank {label}: {SafeText.OneLine(e.Message)}");
                    unreadBanks.Add(stem);
                }
                continue;
            }
            Manifests[rel] = src.Where;
            try
            {
                var (list, skipped) = AssetFiles.ManifestEvents(bytes);
                foreach (var (g, id, name, bank) in list) guids[g] = new Event(id, name, bank, rel, src.Where);
                if (skipped > 0) Notes.Add($"{label}: {skipped} event entr{(skipped == 1 ? "y has" : "ies have")} no readable GUID or ID and {(skipped == 1 ? "was" : "were")} skipped");
                Log.Write($"sound banks: {label}: manifest, {list.Count} event(s)");
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Notes.Add($"couldn't read the bank manifest {label}: {SafeText.OneLine(e.Message)}");
            }
        }
    }

    /// <summary>What the index says about a sound GUID (AssetFiles.SoundGuid of the record's bytes). ev is the
    /// manifest's event when one lists it.</summary>
    public SoundEventState Resolve(UInt128 guid, out Event? ev)
    {
        if (!guids.TryGetValue(guid, out ev)) return SoundEventState.NotInManifest;
        if (events.Contains(ev.Id)) return SoundEventState.Resolved;
        return unreadBanks.Contains(Path.GetFileNameWithoutExtension(ev.Manifest)) ? SoundEventState.Unchecked : SoundEventState.NotInBank;
    }
}
