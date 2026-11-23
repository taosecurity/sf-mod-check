// patch-sources.json (0.15.0): what one `patch` run was built from, written beside the patch so `patch check` can
// later tell whether the patch is out of date. Schema 1: the patch's own facts, the load order (file names only), each
// master's file facts, and every patched record with the plugins whose copies it drew on, each with a record hash.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class PatchSources
{
    public const int Schema = 1;
    public const string FileName = "patch-sources.json";

    public sealed record Editor(string Plugin, string? Hash);
    public sealed record Rec(string FormKey, string Type, string? EditorId, List<Editor> Editors);
    public sealed record MasterFacts(string Name, int Position, long Size, string Modified, string Sha256);
    public sealed record PatchFacts(string Name, long Size, string Sha256);
    public sealed record Manifest(int Schema, string Tool, string Made, PatchFacts Patch, LoadOrderFacts LoadOrder, List<MasterFacts> Masters, List<Rec> Records);
    public sealed record LoadOrderFacts(List<string> Plugins);

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // plain text in a local file, never put in a web page
    };

    /// <summary>The record hash: the first 16 hex digits of the SHA-256 of the record's flags (without the compressed
    /// flag) and its data after decompression. The record header's size, FormID, version-control, timestamp and form
    /// version fields are left out, so saving a plugin again without changes keeps the hash while any changed field
    /// changes it. A compressed record that won't decompress is hashed by its stored bytes.</summary>
    public static string RecordHash(RawFile f, RawRecord rec)
    {
        var stored = f.Span(rec.Offset + 24, (int)rec.Size);
        ReadOnlySpan<byte> body = stored;
        if ((rec.Flags & Flags.Compressed) != 0 && Subrecords.Decompress(stored, out _) is { } d) body = d;
        return Hash(rec.Flags, body);
    }

    public static string Hash(uint flags, ReadOnlySpan<byte> body)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> fl = stackalloc byte[4];
        BitConverter.TryWriteBytes(fl, flags & ~Flags.Compressed);
        sha.AppendData(fl);
        sha.AppendData(body);
        Span<byte> hash = stackalloc byte[32];
        sha.GetHashAndReset(hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    /// <summary>The hashes of the wanted records in one plugin, keyed by FormKey. A record the plugin doesn't hold is
    /// left out. Reads only record headers, plus the bodies of the wanted records.</summary>
    public static Dictionary<FormKey, string> Hashes(PluginInfo p, IReadOnlyDictionary<string, (ModKey Key, Style Style)> files,
        IReadOnlyCollection<string> masters, Style selfStyle, ISet<FormKey> want)
    {
        var found = new Dictionary<FormKey, string>();
        using var f = new RawFile(p.Path);
        var map = new IdMap(p.FileName, selfStyle, masters.Select(m => (m, files.TryGetValue(m, out var x) ? x.Style : Style.Full)));
        var w = new RawWalker(f, deep: false, (_, _, _) => { })
        {
            OnRecord = rec =>
            {
                var (file, local, _) = map.Decode(rec.FormId);
                if (file is null || !files.TryGetValue(file, out var mk)) return;
                var fk = new FormKey(mk.Key, IdMap.Mask(local, mk.Style));
                if (want.Contains(fk)) found.TryAdd(fk, RecordHash(f, rec));
            },
        };
        w.Walk();
        if (w.StoppedEarly is { } why) Log.Write($"patch-sources: {p.FileName} can't be read to the end ({SafeText.OneLine(why)}); records after that point get no hash");
        return found;
    }

    static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Builds the manifest for a written patch. records: (FormKey, type, EditorID, editors) of each patched
    /// record; masters: the patch's masters in load order.</summary>
    /// <summary>Each loaded plugin's ModKey and master style, and its master list, for <see cref="Hashes"/>. A plugin
    /// whose header can't be read is left out (logged).</summary>
    public static (Dictionary<string, (ModKey Key, Style Style)> Files, Dictionary<string, List<string>> Masters) Styles(LoadOrderSetup lo)
    {
        var files = new Dictionary<string, (ModKey Key, Style Style)>(StringComparer.OrdinalIgnoreCase);
        var masterLists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in lo.Plugins)
        {
            try
            {
                var style = ((Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter)p.Mod).MasterStyle switch
                {
                    MasterStyle.Small => Style.Light, MasterStyle.Medium => Style.Medium, _ => Style.Full,
                };
                files[p.FileName] = (p.Mod.ModKey, style);
                masterLists[p.FileName] = p.Mod.MasterReferences.Select(m => m.Master.FileName.String).ToList();
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"patch-sources: {p.FileName}'s header can't be read ({Impact.Summary(e)}); its records get no hash"); }
        }
        return (files, masterLists);
    }

    public static string Sha256Of(string path) => Sha256(path);

    public static Manifest Build(string version, string patchPath, LoadOrderSetup lo, IReadOnlyList<string> masters,
        IEnumerable<(FormKey FormKey, string Type, string? EditorId, IReadOnlyList<string> Editors)> records, DateTime now)
    {
        var recs = records.ToList();
        var (files, masterLists) = Styles(lo);
        // one walk per editor plugin, for just the records it edits
        var wantBy = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recs)
            foreach (var e in r.Editors)
            {
                if (!wantBy.TryGetValue(e, out var set)) wantBy[e] = set = new();
                set.Add(r.FormKey);
            }
        var byName = lo.Plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, Dictionary<FormKey, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, want) in wantBy)
        {
            if (!byName.TryGetValue(name, out var p) || !masterLists.TryGetValue(name, out var ms)) { hashes[name] = new(); continue; }
            try { hashes[name] = Hashes(p, files, ms, files[name].Style, want); }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                hashes[name] = new();
                Log.Write($"patch-sources: {name} can't be read for record hashes ({Impact.Summary(e)}); its records get no hash");
            }
            var missing = want.Count - hashes[name].Count;
            if (missing > 0) Log.Write($"patch-sources: {missing} of the {want.Count} records {name} edits weren't found in its file; they get no hash");
        }

        var order = lo.Plugins.Select(p => p.FileName).ToList();
        var position = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        var masterFacts = masters.Select(m =>
        {
            var path = byName[m].Path;
            var fi = new FileInfo(path);
            return new MasterFacts(m, position[m], fi.Length, Utc(fi.LastWriteTimeUtc), Sha256(path));
        }).ToList();
        var pfi = new FileInfo(patchPath);
        return new Manifest(Schema, $"sf-mod-check {version}", Utc(now),
            new PatchFacts(Path.GetFileName(patchPath), pfi.Length, Sha256(patchPath)),
            new LoadOrderFacts(order), masterFacts,
            recs.Select(r => new Rec(r.FormKey.ToString(), r.Type, r.EditorId,
                r.Editors.Select(e => new Editor(byName.TryGetValue(e, out var ep) ? ep.FileName : e,
                    hashes.TryGetValue(e, out var h) && h.TryGetValue(r.FormKey, out var x) ? x : null)).ToList())).ToList());
    }

    public static void Write(string path, Manifest m) => File.WriteAllText(path, JsonSerializer.Serialize(m, Json) + "\n");

    /// <summary>Reads a manifest; null and the reason when the file isn't one.</summary>
    public static Manifest? Read(string path, out string? why)
    {
        why = null;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
            if (m is null) { why = "the file is empty"; return null; }
            return m;
        }
        catch (JsonException e) { why = $"it isn't valid JSON ({SafeText.OneLine(e.Message)})"; return null; }
    }
}
