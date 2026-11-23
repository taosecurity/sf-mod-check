// The file names inside a Starfield archive (.ba2), read from its name table only (0.6.0, for the assets check).
// Ported from an older private script's ba2_names(), which read all 253 archives of a 157-plugin load order
// correctly. Every BA2 version keeps the same first 24 bytes: "BTDX", version, type ("GNRL" or "DX10"), file count at
// 12 and the name table's offset at 16; the name table is one uint16 length plus that many bytes per file. Nothing is
// decompressed, so a large texture archive costs no more than a small one.
namespace SfModCheck;

public static class Ba2Names
{
    /// <summary>The archive's type from its header: "GNRL" (general files: scripts, meshes, materials) or "DX10"
    /// (textures). Throws InvalidDataException when the file isn't a BA2.</summary>
    public static string Type(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[24];
        if (f.ReadAtLeast(head, 24, throwOnEndOfStream: false) < 24 || head[0] != 'B' || head[1] != 'T' || head[2] != 'D' || head[3] != 'X')
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a BA2 archive (no BTDX header)");
        return System.Text.Encoding.ASCII.GetString(head, 8, 4);
    }

    /// <summary>Every file name in the archive, lower case with backslashes ("scripts\foo\bar.pex"). Throws
    /// InvalidDataException, naming the file, when the header or name table doesn't add up.</summary>
    public static List<string> Read(string path)
    {
        using var f = File.OpenRead(path);
        var name = Path.GetFileName(path);
        var head = new byte[24];
        if (f.ReadAtLeast(head, 24, throwOnEndOfStream: false) < 24 || head[0] != 'B' || head[1] != 'T' || head[2] != 'D' || head[3] != 'X')
            throw new InvalidDataException($"{name} is not a BA2 archive (no BTDX header)");
        var count = BitConverter.ToUInt32(head, 12);
        var nameOfs = BitConverter.ToUInt64(head, 16);
        if (nameOfs == 0) throw new InvalidDataException($"{name} has no name table");
        if (nameOfs >= (ulong)f.Length) throw new InvalidDataException($"{name}: its name table offset {nameOfs} is past the end of the file ({f.Length} bytes)");
        var left = f.Length - (long)nameOfs;
        // each name takes at least its 2-byte length, so a count larger than that is a damaged header
        if (count > left / 2) throw new InvalidDataException($"{name}: its header says {count} files, more than its name table can hold");
        if (left > int.MaxValue) throw new InvalidDataException($"{name}: its name table is too large ({left} bytes)");
        f.Seek((long)nameOfs, SeekOrigin.Begin);
        var data = new byte[left];
        f.ReadExactly(data);
        var names = new List<string>((int)count);
        int pos = 0;
        for (uint i = 0; i < count; i++)
        {
            if (pos + 2 > data.Length) throw new InvalidDataException($"{name}: its name table ends after {i} of {count} names");
            int n = BitConverter.ToUInt16(data, pos);
            pos += 2;
            if (pos + n > data.Length) throw new InvalidDataException($"{name}: name {i + 1} of {count} runs past the end of the file");
            names.Add(Normalize(System.Text.Encoding.UTF8.GetString(data, pos, n)));
            pos += n;
        }
        return names;
    }

    /// <summary>One archive entry: its name (as Read gives it) and whether any of its stored blocks is compressed.</summary>
    public readonly record struct Entry(string Name, bool Compressed);

    /// <summary>Every entry with its compression (0.7.0, for the release command's voice check). Reads the file records
    /// after the header: GNRL records are 36 bytes with the packed size at 24; DX10 records are a 24-byte header (chunk
    /// count at 13, chunk header size at 14) plus one chunk header per chunk with its packed size at 8. A packed size of
    /// 0 means stored as is. Version 2 headers are 8 bytes longer and version 3 headers 12 (as tools\ba2-extract.py).
    /// Throws InvalidDataException, naming the file, when the header or records don't add up.</summary>
    public static List<Entry> ReadEntries(string path)
    {
        var names = Read(path);   // checks the header and the name table
        var name = Path.GetFileName(path);
        using var f = File.OpenRead(path);
        var head = new byte[24];
        f.ReadExactly(head);
        var version = BitConverter.ToUInt32(head, 4);
        var type = System.Text.Encoding.ASCII.GetString(head, 8, 4);
        var nameOfs = (long)BitConverter.ToUInt64(head, 16);
        long pos = 24 + version switch { 2 => 8, 3 => 12, _ => 0 };
        if (type != "GNRL" && type != "DX10") throw new InvalidDataException($"{name}: unsupported archive type \"{type}\" (expected GNRL or DX10)");
        if (pos > nameOfs) throw new InvalidDataException($"{name}: its name table starts inside the header");
        // The records follow the header; the file data (skipped) and then the name table come after them. Only the
        // records are read, never past the name table.
        f.Seek(pos, SeekOrigin.Begin);
        var buf = new byte[36];
        void Take(int n, string what)
        {
            if (f.Position + n > nameOfs) throw new InvalidDataException($"{name}: {what} run into the name table");
            f.ReadExactly(buf, 0, n);
        }
        var result = new List<Entry>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            bool packed = false;
            if (type == "GNRL")
            {
                Take(36, $"its file records (entry {i + 1} of {names.Count})");
                packed = BitConverter.ToUInt32(buf, 24) != 0;
            }
            else
            {
                Take(24, $"its texture records (entry {i + 1} of {names.Count})");
                int chunks = buf[13];
                int chunkHdr = BitConverter.ToUInt16(buf, 14);
                if (chunkHdr == 0) chunkHdr = 24;
                if (chunkHdr < 12) throw new InvalidDataException($"{name}: entry {i + 1} has a chunk header size of {chunkHdr} bytes, too small to hold a packed size");
                for (int k = 0; k < chunks; k++)
                {
                    Take(12, $"the chunks of entry {i + 1} of {names.Count}");
                    if (BitConverter.ToUInt32(buf, 8) != 0) packed = true;
                    if (f.Position - 12 + chunkHdr > nameOfs) throw new InvalidDataException($"{name}: the chunks of entry {i + 1} of {names.Count} run into the name table");
                    f.Seek(chunkHdr - 12, SeekOrigin.Current);
                }
            }
            result.Add(new Entry(names[i], packed));
        }
        return result;
    }

    /// <summary>A data-relative path the way archives and the lookups compare it: lower case, backslashes, no leading
    /// separator.</summary>
    public static string Normalize(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
