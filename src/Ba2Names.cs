// The file names inside a Starfield archive (.ba2), read from its name table only (0.6.0, for the assets check).
// Ported from StarWarsGenesis\swg_ba2conflicts.py ba2_names(), which read all 253 archives of a 157-plugin load order
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

    /// <summary>A data-relative path the way archives and the lookups compare it: lower case, backslashes, no leading
    /// separator.</summary>
    public static string Normalize(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
