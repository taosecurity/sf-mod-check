// The file-to-file asset links (0.14.0, design D2b and D3b): a .nif names its
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
    public static List<string> NifMaterials(byte[] d) => NifLinks(d).Materials;

    /// <summary>What a .nif names (0.15.0): its materials (as NifMaterials) and the geometry files of its BSGeometry
    /// blocks ("geometries\a\b.mesh", data-relative). MeshProblem is set (and Meshes may be partial) when a
    /// BSGeometry block doesn't have the layout below; the materials are still returned. The layout (nifly and
    /// nifxml, Bethesda stream 172 and up; proven on 2026-11-23 against 400 of the game's .nif files and 13 mods'
    /// archives, every block read to its exact end): NiObjectNET (name index, extra data count and refs,
    /// controller), NiAVObject (flags, translation, rotation, scale, collision), the bounding sphere, bound min and
    /// max, skin, shader and alpha refs, then four slots of a has-mesh byte and, when set, the triangle size, vertex
    /// count, flags and a length-prefixed name. Flag 0x200 on the object means the geometry is inside the .nif, so
    /// that block names no file. The game's files give the name as "&lt;folder&gt;\&lt;file&gt;" with no folder
    /// or extension; some mods give "geometries\...\x.mesh"; both mean the same file.</summary>
    public static (List<string> Materials, List<string> Meshes, string? MeshProblem) NifLinks(byte[] d)
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
        var typeNames = new List<string>(types);
        for (int i = 0; i < types; i++) typeNames.Add(System.Text.Encoding.ASCII.GetString(r.Take(r.U32())));
        r.Need(6L * blocks);
        var typeOf = new ushort[blocks];
        for (uint i = 0; i < blocks; i++) typeOf[i] = r.U16();
        var sizes = new uint[blocks];
        for (uint i = 0; i < blocks; i++) sizes[i] = r.U32();
        var strings = r.U32();
        r.U32();                                       // longest string
        var list = new List<string>();
        for (uint i = 0; i < strings; i++)
        {
            var n = r.U32();
            var s = System.Text.Encoding.UTF8.GetString(r.Take(n));
            if (s.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) && s.Trim().Length > 4) list.Add(DataRel(s, "materials"));
        }
        var materials = list.Distinct().ToList();
        var meshes = new List<string>();
        if (bs < 172) return (materials, meshes, null);   // before Starfield's stream there is no BSGeometry
        try
        {
            r.Skip(4L * r.U32());                      // groups
            for (uint i = 0; i < blocks; i++)
            {
                var start = r.Pos;
                r.Skip(sizes[i]);
                if (typeOf[i] >= typeNames.Count) throw new InvalidDataException($"block {i} has type number {typeOf[i]}, but the file lists only {typeNames.Count} types");
                if (typeNames[typeOf[i]] == "BSGeometry") GeometryNames(d, start, sizes[i], i, meshes);
            }
        }
        catch (InvalidDataException e) { return (materials, meshes.Distinct().ToList(), e.Message); }
        return (materials, meshes.Distinct().ToList(), null);
    }

    /// <summary>The geometry file names of one BSGeometry block (layout: NifLinks). Throws InvalidDataException when
    /// the block doesn't read to its exact end.</summary>
    static void GeometryNames(byte[] d, long start, uint size, uint index, List<string> meshes)
    {
        var block = new byte[size];
        Array.Copy(d, start, block, 0, size);
        var r = new Reader(block);
        var bad = $"its BSGeometry block {index} doesn't have the layout sf-mod-check knows";
        var found = new List<string>();   // kept only when the block reads to its exact end
        try
        {
            r.U32();                                   // name
            r.Skip(4L * r.U32());                      // extra data refs
            r.U32();                                   // controller
            var flags = r.U32();
            r.Skip(12 + 36 + 4 + 4);                   // translation, rotation, scale, collision
            r.Skip(16 + 24 + 12);                      // bounding sphere, bound min and max, skin, shader, alpha
            for (int slot = 0; slot < 4; slot++)
            {
                var has = r.Take(1)[0];
                if (has == 0) continue;
                if (has != 1) throw new InvalidDataException(bad);
                r.Skip(12);                            // triangle size, vertex count, flags
                var name = System.Text.Encoding.UTF8.GetString(r.Take(r.U32())).Trim();
                if ((flags & 0x200) != 0) return;      // the geometry is inside the .nif
                if (name.Length > 0) found.Add(MeshRel(name));
            }
        }
        catch (InvalidDataException) { throw new InvalidDataException(bad); }
        if (r.Pos != size) throw new InvalidDataException(bad);
        meshes.AddRange(found);
    }

    /// <summary>A BSGeometry name as a data-relative geometry file: "ab12\cd34" and "Data\Geometries\ab12\cd34.mesh"
    /// both become "geometries\ab12\cd34.mesh".</summary>
    public static string MeshRel(string name)
    {
        var p = DataRel(name, "geometries");
        return p.EndsWith(".mesh", StringComparison.Ordinal) ? p : p + ".mesh";
    }

    sealed class Reader(byte[] d)
    {
        public long Pos;
        public void Need(long n) { if (n < 0 || Pos + n > d.Length) throw new InvalidDataException($"its header ends early (at byte {Pos} of {d.Length})"); }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(d, (int)Pos); Pos += 4; return v; }
        public byte U8() { Need(1); return d[Pos++]; }
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
        var ids = new List<uint>();
        var seen = new HashSet<uint>();
        var version = WalkBank(d, (type, r) =>
        {
            if (type != 2) return;
            r.U32();                           // object ID
            var plugin = r.U32();
            var stream = r.U8();
            var source = r.U32();
            if ((plugin & 0xF) == 1 && stream != 0 && seen.Add(source)) ids.Add(source);
        });
        return (version, ids);
    }

    /// <summary>The event IDs a Wwise sound bank holds (0.23.0): the IDs of its HIRC Event objects (type 4), each of
    /// which starts with its 32-bit ID. For a bank of a version not in ProvenBankVersions the set is empty and only the
    /// version is read. Throws InvalidDataException as BankSources does.</summary>
    public static (uint Version, HashSet<uint> Events) BankEvents(byte[] d)
    {
        var events = new HashSet<uint>();
        var version = WalkBank(d, (type, r) => { if (type == 4) events.Add(r.U32()); });
        return (version, events);
    }

    /// <summary>Walks a bank's chunks: the BKHD version, then for a proven version each HIRC object, given to onObject
    /// with the reader at the object's body (after its type byte and length). The reader is put back at the next object
    /// afterwards. Returns the version (0 when the bank has no BKHD chunk after the first).</summary>
    static uint WalkBank(byte[] d, Action<byte, Reader> onObject)
    {
        var r = new Reader(d);
        if (d.Length < 12 || d[0] != 'B' || d[1] != 'K' || d[2] != 'H' || d[3] != 'D') throw new InvalidDataException("it doesn't start with a BKHD chunk (not a Wwise sound bank)");
        uint version = 0;
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
                if (!ProvenBankVersions.Contains(version)) return version;
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
                    if (next > start + size) throw new InvalidDataException($"its HIRC chunk ends early (object {i + 1} of {objects})");
                    onObject(type, r);
                    r.Pos = next;
                }
            }
            r.Pos = start;
            r.Skip(size);
        }
        return version;
    }

    /// <summary>A record's Wwise event GUID (a SoundReference's Start or Stop, a WWED's Start or End) as the bank
    /// manifest writes it (0.23.0; SmcLookup\wwise-research NOTES, question 1). The record stores the GUID as two
    /// little-endian 64-bit numbers: the GUID's first 16 hex digits, then its last 16. So the 128-bit value is the first
    /// eight stored bytes read little-endian (high half), then the last eight (low half).</summary>
    public static UInt128 SoundGuid(ReadOnlySpan<byte> stored)
    {
        if (stored.Length != 16) throw new ArgumentException($"a sound GUID is 16 bytes, not {stored.Length}", nameof(stored));
        return new UInt128(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(stored),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(stored[8..]));
    }

    /// <summary>SoundGuid of a GUID as Mutagen reads it (a .NET Guid made from the 16 stored bytes; ToByteArray gives
    /// them back unchanged). Guid.ToString() prints those bytes in another order and never matches a manifest.</summary>
    public static UInt128 SoundGuid(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        return SoundGuid(b);
    }

    /// <summary>A sound GUID as the manifest writes it: "{024EA771-3422-448B-BEAB-BAA79FD8C998}".</summary>
    public static string SoundGuidText(UInt128 g)
    {
        var h = $"{(ulong)(g >> 64):X16}{(ulong)g:X16}";
        return $"{{{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}}}";
    }

    /// <summary>A manifest's "GUID" string ("{024EA771-3422-448B-BEAB-BAA79FD8C998}", braces and dashes optional, any
    /// case) as a sound GUID, or null when it isn't 32 hex digits.</summary>
    public static UInt128? ParseSoundGuid(string? s)
    {
        if (s == null) return null;
        var h = s.Trim().TrimStart('{').TrimEnd('}').Replace("-", "");
        if (h.Length != 32 || !h.All(Uri.IsHexDigit)) return null;
        return new UInt128(Convert.ToUInt64(h[..16], 16), Convert.ToUInt64(h[16..], 16));
    }

    /// <summary>The Wwise short ID of a name: FNV-1 32-bit (offset basis 0x811C9DC5, prime 0x01000193, multiply then
    /// XOR) of the lower-case name's bytes. An event's ID is this of its name, and a bank's file name is this of the
    /// bank's name (SmcLookup\wwise-research NOTES, question 2).</summary>
    public static uint Fnv1(string name)
    {
        uint h = 0x811C9DC5;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())) { h *= 0x01000193; h ^= b; }
        return h;
    }

    /// <summary>The events a per-bank manifest ("sound\soundbanks\&lt;bank id&gt;.json", Wwise's SoundBanksInfo) lists:
    /// for each bank in SoundBanksInfo.SoundBanks, each IncludedEvents entry's GUID, ID and name, with the bank's
    /// ShortName. IDs may be strings or numbers. An entry without a readable GUID or ID is counted in Skipped, not
    /// returned. Throws JsonException on damaged JSON and InvalidDataException when SoundBanksInfo.SoundBanks isn't
    /// there.</summary>
    public static (List<(UInt128 Guid, uint Id, string Name, string Bank)> Events, int Skipped) ManifestEvents(byte[] json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("SoundBanksInfo", out var info) ||
            info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("SoundBanks", out var banks) || banks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("it has no SoundBanksInfo.SoundBanks list (not a Wwise bank manifest)");
        var list = new List<(UInt128, uint, string, string)>();
        int skipped = 0;
        foreach (var bank in banks.EnumerateArray())
        {
            if (bank.ValueKind != JsonValueKind.Object) continue;
            var shortName = Text(bank, "ShortName") ?? "";
            if (!bank.TryGetProperty("IncludedEvents", out var evs) || evs.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in evs.EnumerateArray())
            {
                var guid = e.ValueKind == JsonValueKind.Object ? ParseSoundGuid(Text(e, "GUID")) : null;
                uint? id = null;
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Id", out var idv))
                {
                    if (idv.ValueKind == JsonValueKind.Number && idv.TryGetUInt32(out var n)) id = n;
                    else if (idv.ValueKind == JsonValueKind.String && uint.TryParse(idv.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s)) id = s;
                }
                if (guid is not { } g || id is not { } i) { skipped++; continue; }
                list.Add((g, i, Text(e, "Name") ?? "", shortName));
            }
        }
        return (list, skipped);

        static string? Text(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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
