// sf-mod-check find --layout (0.22.0, E): the winning copy's subrecords in file order, read from the plugin's raw bytes
// (no Mutagen): each one's signature, size, offset in the record and a short preview of its value. A survey of a
// record type's layout then needs no one-off raw reader. A truncated or malformed record lists the subrecords before
// the bad one and says what is wrong; a preview is never more than PreviewBytes bytes or PreviewChars characters.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using SfModCheck.Conflict;

namespace SfModCheck;

/// <summary>One subrecord: Offset counts from the start of the record's (decompressed) data; Big = its size came from
/// an XXXX field before it, which isn't listed on its own.</summary>
public sealed record LayoutField(string Sig, long Size, long Offset, bool Big, string Preview);

/// <summary>The raw layout of one copy of a record.</summary>
public sealed class RecordLayout
{
    public required string Plugin;
    /// <summary>Where the record header starts in the plugin file, or -1 when the record wasn't reached.</summary>
    public long FileOffset = -1;
    public string Sig = "";
    public uint Flags, StoredId, DataSize;
    public ushort FormVersion;
    public bool Compressed;
    /// <summary>The data the subrecords are read from: DataSize, or the decompressed size.</summary>
    public long BodySize;
    /// <summary>The first subrecords (at most the cap Read was given); Total counts every one read.</summary>
    public List<LayoutField> Fields = new();
    public int Total;
    /// <summary>Why the layout stops early or couldn't be read, or null.</summary>
    public string? Problem;
}

public static class RecordLayouts
{
    /// <summary>The subrecords the text output prints per record at most; JSON keeps up to MaxJson.</summary>
    public const int MaxText = 500, MaxJson = 5000;
    public const int PreviewBytes = 16, PreviewChars = 48;

    /// <summary>The layout of key's copy in plugin p. files: each loaded plugin's ModKey and master style; masters: p's
    /// master list (both from <see cref="PatchSources.Styles"/>). Reads every record header of the file, then only the
    /// wanted record's data. Never throws for a bad file: the problem is in the result.</summary>
    public static RecordLayout Read(PluginInfo p, FormKey key, IReadOnlyDictionary<string, (ModKey Key, Style Style)> files,
                                    IReadOnlyCollection<string> masters, int cap)
        => ReadMany(p, [key], files, masters, cap)[key];

    /// <summary>The layouts of several records' copies in plugin p from one pass over its record headers (a --plugin
    /// listing with --layout reads each file once, not once per record). Every key gets a result.</summary>
    public static Dictionary<FormKey, RecordLayout> ReadMany(PluginInfo p, IReadOnlyCollection<FormKey> keys,
                                    IReadOnlyDictionary<string, (ModKey Key, Style Style)> files,
                                    IReadOnlyCollection<string> masters, int cap)
    {
        var result = keys.Distinct().ToDictionary(k => k, _ => new RecordLayout { Plugin = p.FileName });
        if (!files.TryGetValue(p.FileName, out var self))
        {
            foreach (var lay in result.Values) lay.Problem = $"{SafeText.OneLine(p.FileName)}'s header can't be read";
            return result;
        }
        try
        {
            using var f = new RawFile(p.Path);
            var map = new IdMap(p.FileName, self.Style, masters.Select(m => (m, files.TryGetValue(m, out var x) ? x.Style : Style.Full)));
            var hits = new Dictionary<FormKey, RawRecord>();
            var w = new RawWalker(f, deep: false, (_, _, _) => { })
            {
                OnRecord = rec =>
                {
                    if (hits.Count == result.Count) return;
                    var (file, local, _) = map.Decode(rec.FormId);
                    if (file is null || !files.TryGetValue(file, out var mk)) return;
                    var k = new FormKey(mk.Key, IdMap.Mask(local, mk.Style));
                    if (result.ContainsKey(k)) hits.TryAdd(k, rec);
                },
            };
            w.Walk();
            foreach (var (k, lay) in result)
            {
                if (!hits.TryGetValue(k, out var r))
                {
                    lay.Problem = w.StoppedEarly is { } why
                        ? $"the record wasn't reached: the file can't be read past a bad spot ({SafeText.OneLine(why)})"
                        : "the record wasn't found in the file's raw layout";
                    continue;
                }
                // a read that fails names this record only; the others are still filled (code review 0.22.0 round 2)
                try { Fill(lay, f, r, cap); }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    lay.Problem ??= $"the record's data can't be read ({e.GetType().Name})";
                    Log.Write($"find --layout: {SafeText.OneLine(p.FileName)} {k}: {e.GetType().Name}: {SafeText.OneLine(e.Message)}");
                }
            }
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            foreach (var lay in result.Values)
                if (lay.FileOffset < 0 && lay.Problem == null) lay.Problem = $"{SafeText.OneLine(p.FileName)} can't be read ({e.GetType().Name})";
        }
        return result;
    }

    static void Fill(RecordLayout lay, RawFile f, RawRecord r, int cap)
    {
        lay.FileOffset = r.Offset;
        lay.Sig = r.Sig;
        lay.Flags = r.Flags;
        lay.StoredId = r.FormId;
        lay.DataSize = r.Size;
        lay.FormVersion = r.FormVersion;
        lay.Compressed = (r.Flags & SfModCheck.Flags.Compressed) != 0;
        if (r.Size > int.MaxValue) { lay.Problem = $"the record is too big to read ({r.Size} bytes)"; return; }
        var stored = f.Span(r.Offset + 24, (int)r.Size);   // the walker checked that it ends inside its group
        ReadOnlySpan<byte> body = stored;
        if (lay.Compressed)
        {
            var d = Subrecords.Decompress(stored, out var why);
            if (d == null) { lay.Problem = $"the record is compressed but {why}"; return; }
            body = d;
        }
        lay.BodySize = body.Length;
        (lay.Fields, lay.Total, lay.Problem) = Parse(body, cap);
    }

    /// <summary>A record's data split into subrecords in file order (XXXX gives the next one a 32-bit size). Keeps the
    /// first cap of them; problem = why the sizes don't add up to the data's length (the fields before it are kept).</summary>
    public static (List<LayoutField> Fields, int Total, string? Problem) Parse(ReadOnlySpan<byte> body, int cap)
    {
        var list = new List<LayoutField>();
        string? problem = null;
        int total = 0;
        long i = 0;
        uint? big = null;
        while (i < body.Length)
        {
            if (body.Length - i < 6) { problem = $"{body.Length - i} stray bytes at offset {i}, too few for a subrecord header"; break; }
            var at = (int)i;
            var sig = Printable(body.Slice(at, 4));
            long size = BitConverter.ToUInt16(body.Slice(at + 4, 2));
            if (sig == "XXXX")
            {
                if (size != 4) { problem = $"XXXX at offset {i} has size {size}, not 4"; break; }
                if (body.Length - i < 10) { problem = $"XXXX at offset {i} says 4 bytes, but only {body.Length - i - 6} are left in the record"; break; }
                big = BitConverter.ToUInt32(body.Slice(at + 6, 4));
                i += 10;
                continue;
            }
            bool isBig = big != null;
            if (big != null) { size = big.Value; big = null; }
            if (i + 6 + size > body.Length)
            {
                problem = $"{sig} at offset {i} says {size} bytes, but only {body.Length - i - 6} are left in the record";
                break;
            }
            total++;
            if (list.Count < cap) list.Add(new LayoutField(sig, size, i, isBig, Preview(body.Slice(at + 6, (int)size))));
            i += 6 + size;
        }
        if (problem == null && big != null) problem = "XXXX at the end of the record with no subrecord after it";
        return (list, total, problem);
    }

    /// <summary>A short, one-line view of a value: text (zero-ended, printable) in quotes, 1, 2 and 4 bytes as numbers
    /// (4 bytes in hex, since they are often a FormID), anything else as its first PreviewBytes bytes in hex.</summary>
    public static string Preview(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "(empty)";
        if (data.Length >= 2 && data[^1] == 0 && LooksLikeText(data[..^1]))
        {
            var s = Encoding.UTF8.GetString(data[..^1]);
            if (s.Length > PreviewChars) s = s[..(char.IsHighSurrogate(s[PreviewChars - 1]) ? PreviewChars - 1 : PreviewChars)] + "...";
            return "\"" + SafeText.OneLine(s) + "\"";
        }
        switch (data.Length)
        {
            case 1: return data[0].ToString(CultureInfo.InvariantCulture);
            case 2: return BitConverter.ToUInt16(data).ToString(CultureInfo.InvariantCulture);
            case 4: return "0x" + BitConverter.ToUInt32(data).ToString("X8", CultureInfo.InvariantCulture);
        }
        var n = Math.Min(data.Length, PreviewBytes);
        return Convert.ToHexString(data[..n]).ToLowerInvariant() + (data.Length > n ? "..." : "");
    }

    /// <summary>Valid UTF-8 text with no control characters but tab and line breaks (which the preview escapes).</summary>
    static bool LooksLikeText(ReadOnlySpan<byte> b)
    {
        foreach (var c in b) if ((c < 0x20 && c is not (0x09 or 0x0A or 0x0D)) || c == 0x7F) return false;
        try { _ = new UTF8Encoding(false, true).GetString(b); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>A signature as text, any byte outside letters, digits and '_' shown as '?'.</summary>
    static string Printable(ReadOnlySpan<byte> sig)
    {
        Span<char> c = stackalloc char[4];
        for (int k = 0; k < 4; k++) c[k] = sig[k] is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_' ? (char)sig[k] : '?';
        return new string(c);
    }

    /// <summary>The text block printed under a match.</summary>
    public static IEnumerable<string> Lines(RecordLayout l)
    {
        if (l.FileOffset < 0)
        {
            yield return $"  layout ({SafeText.OneLine(l.Plugin)}): can't be shown: {l.Problem}";
            yield break;
        }
        yield return $"  layout ({SafeText.OneLine(l.Plugin)}, record at 0x{l.FileOffset:X}): {l.Sig} {l.StoredId:X8}, form version {l.FormVersion}, " +
                     $"flags 0x{l.Flags:X8}, {l.DataSize:N0} bytes" + (l.Compressed ? $" compressed ({l.BodySize:N0} unpacked)" : "") +
                     $", {l.Total:N0} subrecord{(l.Total == 1 ? "" : "s")}";
        var shown = l.Fields.Take(MaxText).ToList();
        foreach (var f in shown)
            yield return $"    {f.Offset,6}  {f.Sig}  {f.Size,6}{(f.Big ? " (XXXX)" : "")}  {f.Preview}";
        if (l.Total > shown.Count) yield return $"    ... and {l.Total - shown.Count:N0} more subrecords (--json lists up to {MaxJson:N0})";
        if (l.Problem != null)
            yield return $"    problem: {l.Problem}; " + (l.Total == 0 ? "no subrecords were read" : "the subrecords after it can't be read");
    }

    public static JsonObject Json(RecordLayout l)
    {
        var o = new JsonObject { ["plugin"] = l.Plugin };
        if (l.FileOffset >= 0)
        {
            o["fileOffset"] = l.FileOffset;
            o["signature"] = l.Sig;
            o["storedFormId"] = l.StoredId.ToString("X8", CultureInfo.InvariantCulture);
            o["formVersion"] = l.FormVersion;
            o["flags"] = l.Flags.ToString("X8", CultureInfo.InvariantCulture);
            o["dataSize"] = l.DataSize;
            o["compressed"] = l.Compressed;
            o["bodySize"] = l.BodySize;
            o["subrecordCount"] = l.Total;
            o["subrecords"] = new JsonArray(l.Fields.Select(f =>
            {
                var x = new JsonObject { ["signature"] = f.Sig, ["size"] = f.Size, ["offset"] = f.Offset, ["preview"] = f.Preview };
                if (f.Big) x["sizeFromXXXX"] = true;
                return (JsonNode)x;
            }).ToArray());
            if (l.Total > l.Fields.Count) o["notShown"] = l.Total - l.Fields.Count;
        }
        if (l.Problem != null) o["problem"] = l.Problem;
        return o;
    }
}
