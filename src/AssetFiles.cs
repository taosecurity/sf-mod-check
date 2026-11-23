// The file-to-file asset links (0.14.0, design D2b and D3b in PMTUpkeep\smc-0.14.0\RELAY.md): a .nif names its
// materials in its header's string table, a .mat (JSON) names its textures and its parent materials, and the game's own
// materials live only in materials\materialsbeta.cdb, found there by a 12-byte resource ID. Only names are read: no
// geometry, no texture data, nothing kept from the game's files but the IDs.
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SfModCheck;

public static class AssetFiles
{
    /// <summary>Largest .nif or .mat read (a mod's own files only). A larger one gets the "couldn't read" note.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>The data-relative path of the game's material database.</summary>
    public const string CdbPath = @"materials\materialsbeta.cdb";

    /// <summary>A path as a .nif, .mat or record gives it, made data-relative under its base folder: "Data\Textures\a.dds"
    /// and "a.dds" both become "textures\a.dds" (base "textures"). Lower case, backslashes.</summary>
    public static string DataRel(string path, string baseDir)
    {
        var p = Ba2Names.Normalize(path.Trim());
        if (p.StartsWith(@"data\", StringComparison.Ordinal)) p = p[5..];
        return p.StartsWith(baseDir + "\\", StringComparison.Ordinal) ? p : baseDir + "\\" + p;
    }

    /// <summary>The material paths a .nif names (strings in its header's string table ending in ".mat"), data-relative.
    /// The header layout is the Gamebryo one nifxml describes: the version line, version, endian, user version, block
    /// count, the Bethesda stream header, the block types, the block indexes and sizes, then the strings. Throws
    /// InvalidDataException when the header doesn't add up.</summary>
    public static List<string> NifMaterials(byte[] d)
    {
        var r = new Reader(d);
        int nl = Array.IndexOf(d, (byte)'\n', 0, Math.Min(d.Length, 64));
        if (nl < 0 || !System.Text.Encoding.ASCII.GetString(d, 0, nl).StartsWith("Gamebryo File Format", StringComparison.Ordinal))
            throw new InvalidDataException("it doesn't start with a Gamebryo header");
        r.Pos = nl + 1;
        var version = r.U32();
        if (version < 0x14020005) throw new InvalidDataException($"its NIF version {version:X8} is older than Starfield's");
        r.Skip(1);                                     // endian
        r.U32();                                       // user version
        var blocks = r.U32();
        var bs = r.U32();
        r.Short();                                     // author
        if (bs > 130) r.Skip(4);
        if (bs < 131) r.Short();                       // process script
        r.Short();                                     // export script
        if (bs >= 103) r.Short();                      // max filepath
        int types = r.U16();
        for (int i = 0; i < types; i++) r.Skip(r.U32());
        r.Skip(6L * blocks);                           // a u16 type index and a u32 size per block
        var strings = r.U32();
        r.U32();                                       // longest string
        var list = new List<string>();
        for (uint i = 0; i < strings; i++)
        {
            var n = r.U32();
            var s = System.Text.Encoding.UTF8.GetString(r.Take(n));
            if (s.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) && s.Trim().Length > 4) list.Add(DataRel(s, "materials"));
        }
        return list.Distinct().ToList();
    }

    sealed class Reader(byte[] d)
    {
        public long Pos;
        void Need(long n) { if (n < 0 || Pos + n > d.Length) throw new InvalidDataException($"its header ends early (at byte {Pos} of {d.Length})"); }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(d, (int)Pos); Pos += 4; return v; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(d, (int)Pos); Pos += 2; return v; }
        public void Skip(long n) { Need(n); Pos += n; }
        public void Short() { Need(1); var n = d[Pos]; Pos++; Skip(n); }
        public ReadOnlySpan<byte> Take(uint n) { Need(n); var s = d.AsSpan((int)Pos, (int)n); Pos += n; return s; }
    }

    /// <summary>What a .mat (JSON) names: textures (string values of "File" and "FileName" keys ending in .dds or .png)
    /// and parent materials ("Import" strings and "Parent" strings ending in .mat; "res:" IDs are internal). The
    /// top-level "Filename" is the material's own name and isn't a link. Throws JsonException on damaged JSON.</summary>
    public static (List<string> Textures, List<string> Parents) MatLinks(byte[] json)
    {
        var textures = new List<string>();
        var parents = new List<string>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
        void Walk(JsonElement e, string? key)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var x in e.EnumerateArray()) Walk(x, key);
                    break;
                case JsonValueKind.String:
                    var s = e.GetString()!;
                    if (s.Trim().Length == 0 || s.StartsWith("res:", StringComparison.Ordinal)) break;
                    if (key is "File" or "FileName" && (s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                        textures.Add(DataRel(s, "textures"));
                    else if (key is "Import" || key is "Parent" && s.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                        parents.Add(DataRel(s, "materials"));
                    break;
            }
        }
        Walk(doc.RootElement, null);
        return (textures.Distinct().ToList(), parents.Distinct().ToList());
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        return c;
    }).ToArray();

    /// <summary>The CRC32 the game names resources by: polynomial 0xEDB88320, start 0, no final xor, over the ASCII
    /// bytes with A-Z lower-cased and "/" as "\" (the layout libfo76utils, MIT, describes for BSResourceID).</summary>
    public static uint Crc(string s)
    {
        uint h = 0;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
        {
            var c = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b == (byte)'/' ? (byte)'\\' : b;
            h = (h >> 8) ^ CrcTable[(h ^ c) & 0xFF];
        }
        return h;
    }

    /// <summary>A material path's resource ID as one number: the folder's CRC in the high half, the file stem's in the
    /// low half ("materials\a\b.mat" -> Crc("materials\a") and Crc("b")).</summary>
    public static ulong MaterialId(string path)
    {
        var p = Ba2Names.Normalize(path);
        var cut = p.LastIndexOf('\\');
        var dir = cut < 0 ? "" : p[..cut];
        var file = Path.GetFileNameWithoutExtension(cut < 0 ? p : p[(cut + 1)..]);
        return (ulong)Crc(dir) << 32 | Crc(file);
    }

    /// <summary>Every material resource ID in a material database: each "mat\0" with the 4 bytes before it (the file
    /// CRC) and after it (the folder CRC). Plain scanning, so no knowledge of the database's other structures is needed;
    /// on the game's file it matched 935 of 936 known paths and none of 3 made-up ones (2026-11-23).</summary>
    public static HashSet<ulong> CdbIds(ReadOnlySpan<byte> data)
    {
        var ids = new HashSet<ulong>();
        ReadOnlySpan<byte> tag = "mat\0"u8;
        int at = 0;
        while (true)
        {
            var i = data[at..].IndexOf(tag);
            if (i < 0) break;
            i += at;
            if (i >= 4 && i + 8 <= data.Length)
                ids.Add((ulong)BitConverter.ToUInt32(data[(i + 4)..]) << 32 | BitConverter.ToUInt32(data[(i - 4)..]));
            at = i + 1;
        }
        return ids;
    }

    /// <summary>The Wwise bank versions whose Sound object layout is proven (relay step 6, 2026-11-23: every vanilla bank
    /// in Starfield's WwiseSounds archives and three mod banks are version 140, and all 110,276 streamed sources in the
    /// vanilla banks have their .wem). A bank of another version is not read past its header.</summary>
    public static readonly uint[] ProvenBankVersions = { 140 };

    /// <summary>What a Wwise sound bank (.bnk) streams: its BKHD version and, for a proven version, the source IDs of
    /// the streamed media in its HIRC Sound objects (type 2), each once. A Sound object starts with its object ID, the
    /// source's plugin ID (low 4 bits 1 = a codec, that is a media file; 2 = a generated source, no file), the stream
    /// type (0 = in the bank, 1 = prefetched, 2 = streamed) and the source ID; a streamed codec source is
    /// sound\soundbanks\&lt;source id&gt;.wem. Sources in the bank itself need no file. Throws InvalidDataException when
    /// the bank doesn't start with a BKHD chunk or a chunk runs past the end.</summary>
    public static (uint Version, List<uint> Streamed) BankSources(byte[] d)
    {
        var r = new Reader(d);
        if (d.Length < 12 || d[0] != 'B' || d[1] != 'K' || d[2] != 'H' || d[3] != 'D') throw new InvalidDataException("it doesn't start with a BKHD chunk (not a Wwise sound bank)");
        uint version = 0;
        var ids = new List<uint>();
        var seen = new HashSet<uint>();
        while (r.Pos + 8 <= d.Length)
        {
            var tag = System.Text.Encoding.ASCII.GetString(d, (int)r.Pos, 4);
            r.Skip(4);
            var size = r.U32();
            var start = r.Pos;
            if (start + size > d.Length) throw new InvalidDataException($"its {tag} chunk runs past the end of the file");
            if (tag == "BKHD")
            {
                version = r.U32();
                if (!ProvenBankVersions.Contains(version)) return (version, ids);
            }
            else if (tag == "HIRC")
            {
                var objects = r.U32();
                for (uint i = 0; i < objects; i++)
                {
                    r.Skip(1);
                    var type = d[r.Pos - 1];
                    var length = r.U32();
                    var next = r.Pos + length;
                    if (type == 2)
                    {
                        r.U32();                           // object ID
                        var plugin = r.U32();
                        r.Skip(1);
                        var stream = d[r.Pos - 1];
                        var source = r.U32();
                        if ((plugin & 0xF) == 1 && stream != 0 && seen.Add(source)) ids.Add(source);
                    }
                    r.Pos = next;
                    if (r.Pos > start + size) throw new InvalidDataException($"its HIRC chunk ends early (object {i + 1} of {objects})");
                }
            }
            r.Pos = start;
            r.Skip(size);
        }
        return (version, ids);
    }

    /// <summary>Whether a data-relative name is a sound bank the game loads ("sound\soundbanks\x.bnk").</summary>
    public static bool IsBank(string rel) => rel.StartsWith(@"sound\soundbanks\", StringComparison.Ordinal) && rel.EndsWith(".bnk", StringComparison.Ordinal);

    /// <summary>The data-relative path of a bank's streamed source.</summary>
    public static string WemPath(uint source) => $@"sound\soundbanks\{source}.wem";

    /// <summary>The bytes of one file in a general (GNRL) archive, by its data-relative name, or null when the archive
    /// doesn't hold it. Read with Mutagen's archive reader (as release's DMMDeps staging). Throws on a damaged archive.</summary>
    public static byte[]? FromArchive(string ba2, string rel)
    {
        var reader = Archive.CreateReader(GameRelease.Starfield, ba2);
        foreach (var f in reader.Files)
            if (Ba2Names.Normalize(f.Path) == rel) return f.GetBytes();
        return null;
    }

    /// <summary>Every (name, bytes) of the .nif, .mat and .bnk files in a general archive, read lazily.</summary>
    public static IEnumerable<(string Name, Func<byte[]> Bytes)> ArchiveModelFiles(string ba2)
    {
        var reader = Archive.CreateReader(GameRelease.Starfield, ba2);
        foreach (var f in reader.Files)
        {
            var n = Ba2Names.Normalize(f.Path);
            if (n.EndsWith(".nif", StringComparison.Ordinal) || n.EndsWith(".mat", StringComparison.Ordinal) || IsBank(n)) yield return (n, f.GetBytes);
        }
    }
}
