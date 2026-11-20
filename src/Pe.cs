// A small, bounds-checked reader for Windows PE files (.dll .exe), PE32 and PE32+ (0.8.0, triage chunk C4).
// It only reads bytes: it never loads or runs the file (no LoadLibrary, no Assembly.Load).
// What it reads: the COFF and optional headers (machine, build time, subsystem, characteristics, DLL characteristics),
// the section table, the import table (by name and by ordinal), delay-load imports, the export table, the debug
// directory (CodeView PDB path, reproducible-build marker), TLS callbacks, the certificate table's place, whether it is
// a .NET assembly, and printable ASCII and UTF-16 strings.
// A damaged or crafted file gives an InvalidDataException naming the offset. The headers must be sound (else Read
// throws); a damaged directory (imports, exports, debug, TLS) is skipped and its error kept in Problems, so the rest of
// the file can still be described. Every count and length is capped, so no input can make it loop for long or
// allocate much more than the file's own size.
using System.Buffers.Binary;
using System.Text;

namespace SfModCheck;

public sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics);

/// <summary>One imported function: Name, or Ordinal when imported by number only.</summary>
public sealed record PeImport(string Dll, string? Name, ushort? Ordinal, bool Delayed)
{
    public string Display => Name ?? $"#{Ordinal}";
}

/// <summary>One exported function or data item. Forwarder is set when the export points into another DLL.</summary>
public sealed record PeExport(string? Name, uint Ordinal, uint Rva, string? Forwarder);

/// <summary>A printable run of text found in the file, with its file offset.</summary>
public sealed record PeString(long Offset, string Text, bool Wide);

public sealed class PeFile
{
    /// <summary>The largest file Read accepts (mod DLLs are a few MB).</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const int MaxSections = 96;
    public const int MaxImportDlls = 4096;
    public const int MaxImportsPerDll = 65536;
    public const int MaxExports = 65536;
    public const int MaxStrings = 100_000;
    public const int MaxStringChars = 512;
    public const int MinStringChars = 6;

    public const ushort MachineI386 = 0x14c, MachineAmd64 = 0x8664, MachineArm64 = 0xaa64, MachineArm = 0x1c4;

    public byte[] Data { get; }
    public string Path { get; }
    public ushort Machine { get; private set; }
    public uint TimeDateStamp { get; private set; }
    public ushort Characteristics { get; private set; }
    public bool Is64 { get; private set; }
    public ulong ImageBase { get; private set; }
    public uint EntryPointRva { get; private set; }
    public ushort Subsystem { get; private set; }
    public ushort DllCharacteristics { get; private set; }
    public uint SizeOfHeaders { get; private set; }
    public List<PeSection> Sections { get; } = new();
    public List<PeImport> Imports { get; } = new();
    public List<PeExport> Exports { get; } = new();
    public string? ExportDllName { get; private set; }
    public string? PdbPath { get; private set; }
    /// <summary>The debug directory has a REPRO entry: the "build time" is then a hash of the build, not a date.</summary>
    public bool Reproducible { get; private set; }
    public int TlsCallbacks { get; private set; }
    /// <summary>File offset and size of the certificate table (an Authenticode signature block), or null.</summary>
    public (uint Offset, uint Size)? Certificate { get; private set; }
    public bool IsDotNet { get; private set; }
    /// <summary>Directory faults that were skipped, each naming the offset.</summary>
    public List<string> Problems { get; } = new();

    readonly (uint Rva, uint Size)[] dirs = new (uint, uint)[16];

    PeFile(byte[] data, string path) { Data = data; Path = path; }

    public bool IsDll => (Characteristics & 0x2000) != 0;
    public bool IsExe => (Characteristics & 0x0002) != 0 && !IsDll;
    public bool Aslr => (DllCharacteristics & 0x0040) != 0;
    public bool HighEntropyAslr => (DllCharacteristics & 0x0020) != 0;
    public bool Dep => (DllCharacteristics & 0x0100) != 0;
    public bool Cfg => (DllCharacteristics & 0x4000) != 0;
    public DateTimeOffset BuildTime => DateTimeOffset.FromUnixTimeSeconds(TimeDateStamp);

    public string MachineName => Machine switch
    {
        MachineAmd64 => "x64",
        MachineI386 => "x86 (32-bit)",
        MachineArm64 => "ARM64",
        MachineArm => "ARM (32-bit)",
        _ => $"0x{Machine:X4}",
    };

    public string SubsystemName => Subsystem switch
    {
        1 => "native",
        2 => "Windows GUI",
        3 => "console",
        9 => "Windows CE",
        10 => "EFI application",
        _ => $"subsystem {Subsystem}",
    };

    public (uint Rva, uint Size) Directory(int i) => dirs[i];

    /// <summary>Reads a PE file from disk (refuses files over MaxFileBytes).</summary>
    public static PeFile Read(string path)
    {
        var len = new FileInfo(path).Length;
        if (len > MaxFileBytes)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is {len:N0} bytes, more than the {MaxFileBytes / (1024 * 1024)} MB this reader accepts");
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <summary>Reads a PE image from bytes; path is only used in messages.</summary>
    public static PeFile Parse(byte[] data, string path = "")
    {
        if (data.LongLength > MaxFileBytes)
            throw new InvalidDataException($"the file is {data.LongLength:N0} bytes, more than the {MaxFileBytes / (1024 * 1024)} MB this reader accepts");
        var pe = new PeFile(data, path);
        pe.ReadHeaders();
        pe.Try("import table", pe.ReadImports);
        pe.Try("delay-load import table", pe.ReadDelayImports);
        pe.Try("export table", pe.ReadExports);
        pe.Try("debug directory", pe.ReadDebug);
        pe.Try("TLS directory", pe.ReadTls);
        return pe;
    }

    void Try(string what, Action read)
    {
        try { read(); }
        catch (InvalidDataException e) { Problems.Add($"{what}: {e.Message}"); }
    }

    // ---- bounds-checked reads ----

    static InvalidDataException Bad(string what, long off) => new($"{what} at offset 0x{off:X}");

    void Need(long off, long len, string what)
    {
        if (off < 0 || len < 0 || off > Data.LongLength || len > Data.LongLength - off)
            throw Bad($"{what} runs past the end of the file ({Data.LongLength:N0} bytes)", off);
    }

    ushort U16(long off, string what) { Need(off, 2, what); return BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)off)); }
    uint U32(long off, string what) { Need(off, 4, what); return BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off)); }
    ulong U64(long off, string what) { Need(off, 8, what); return BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)off)); }

    /// <summary>The file offset of an RVA, or throws when the RVA is in no section (or past the section's raw bytes).</summary>
    public long Offset(uint rva, uint length, string what)
    {
        if (TryOffset(rva, length) is { } o) return o;
        throw new InvalidDataException($"{what} at RVA 0x{rva:X} isn't inside the file's sections (or runs past their bytes in the file)");
    }

    public long? TryOffset(uint rva, uint length)
    {
        if (rva < SizeOfHeaders)
            return (long)rva + length <= Math.Min(SizeOfHeaders, (uint)Math.Min(Data.LongLength, uint.MaxValue)) ? rva : null;
        foreach (var s in Sections)
        {
            long start = s.VirtualAddress, span = Math.Max(s.VirtualSize, s.RawSize);
            if (rva < start || rva - start >= span) continue;
            long delta = rva - start;
            if (delta + length > s.RawSize) return null;
            long off = s.RawOffset + delta;
            if (off + length > Data.LongLength) return null;
            return off;
        }
        return null;
    }

    /// <summary>Bytes at an RVA, or null when they aren't all in the file.</summary>
    public byte[]? BytesAt(uint rva, int length) =>
        length >= 0 && TryOffset(rva, (uint)length) is { } o ? Data.AsSpan((int)o, length).ToArray() : null;

    /// <summary>A zero-ended ASCII name at an RVA (max chars); throws when it runs out of the file or has no end.</summary>
    string CString(uint rva, string what, int max = 512)
    {
        var off = Offset(rva, 1, what);
        var b = new StringBuilder();
        for (long i = off; ; i++)
        {
            if (i >= Data.LongLength) throw Bad($"{what} has no end before the end of the file", off);
            var c = Data[i];
            if (c == 0) break;
            if (b.Length >= max) throw Bad($"{what} is longer than {max} characters", off);
            b.Append(c is >= 0x20 and < 0x7f ? (char)c : '?');
        }
        return b.ToString();
    }

    // ---- headers ----

    void ReadHeaders()
    {
        if (Data.Length < 64) throw Bad($"the file is too short to be a Windows program ({Data.Length} bytes)", 0);
        if (Data[0] != (byte)'M' || Data[1] != (byte)'Z') throw Bad("no MZ header (not a Windows .dll or .exe)", 0);
        var peOff = U32(0x3c, "the PE header pointer");
        if (peOff < 0x40 || peOff > Data.Length - 24) throw Bad($"the PE header pointer (0x{peOff:X}) is outside the file", 0x3c);
        if (U32(peOff, "the PE signature") != 0x00004550) throw Bad("no PE signature", peOff);
        long coff = peOff + 4;
        Machine = U16(coff, "the machine field");
        int nSections = U16(coff + 2, "the section count");
        TimeDateStamp = U32(coff + 4, "the build time");
        int optSize = U16(coff + 16, "the optional header size");
        Characteristics = U16(coff + 18, "the characteristics");
        if (nSections > MaxSections) throw Bad($"the file says it has {nSections} sections (more than {MaxSections})", coff + 2);
        long opt = coff + 20;
        if (optSize < 2) throw Bad("the optional header is missing", opt);
        Need(opt, optSize, "the optional header");
        var magic = U16(opt, "the optional header magic");
        int dirStart, minSize;
        switch (magic)
        {
            case 0x10b:
                Is64 = false; ImageBase = U32(opt + 28, "the image base"); dirStart = 96; break;
            case 0x20b:
                Is64 = true; ImageBase = U64(opt + 24, "the image base"); dirStart = 112; break;
            default: throw Bad($"unknown optional header magic 0x{magic:X}", opt);
        }
        minSize = dirStart;
        if (optSize < minSize) throw Bad($"the optional header is {optSize} bytes, too short for its kind", opt);
        EntryPointRva = U32(opt + 16, "the entry point");
        SizeOfHeaders = U32(opt + 60, "the header size");
        Subsystem = U16(opt + 68, "the subsystem");
        DllCharacteristics = U16(opt + 70, "the DLL characteristics");
        var nDirs = U32(opt + dirStart - 4, "the data directory count");
        int usable = (int)Math.Min(Math.Min(nDirs, 16u), (uint)((optSize - dirStart) / 8));
        for (int i = 0; i < usable; i++)
            dirs[i] = (U32(opt + dirStart + i * 8, "a data directory"), U32(opt + dirStart + i * 8 + 4, "a data directory"));

        long sec = opt + optSize;
        Need(sec, (long)nSections * 40, "the section table");
        for (int i = 0; i < nSections; i++)
        {
            long o = sec + i * 40;
            var raw = Data.AsSpan((int)o, 8);
            int n = raw.IndexOf((byte)0); if (n < 0) n = 8;
            var name = new string(raw[..n].ToArray().Select(c => c is >= 0x20 and < 0x7f ? (char)c : '?').ToArray());
            Sections.Add(new PeSection(name, U32(o + 12, "a section"), U32(o + 8, "a section"), U32(o + 20, "a section"), U32(o + 16, "a section"), U32(o + 36, "a section")));
        }

        var (certOff, certSize) = dirs[4];   // a file offset, not an RVA
        if (certOff != 0 && certSize != 0)
        {
            if ((long)certOff + certSize > Data.LongLength) Problems.Add($"certificate table: runs past the end of the file at offset 0x{certOff:X}");
            else Certificate = (certOff, certSize);
        }
        IsDotNet = dirs[14].Rva != 0 && dirs[14].Size != 0;
    }

    // ---- imports ----

    void ReadImports()
    {
        var (rva, size) = dirs[1];
        if (rva == 0 || size == 0) return;
        for (int i = 0; i < MaxImportDlls; i++)
        {
            uint d = rva + (uint)(i * 20);
            var o = Offset(d, 20, "an import descriptor");
            uint lookup = U32(o, "an import descriptor"), nameRva = U32(o + 12, "an import descriptor"), iat = U32(o + 16, "an import descriptor");
            if (lookup == 0 && nameRva == 0 && iat == 0) return;
            var dll = CString(nameRva, "an imported DLL name");
            ReadThunks(dll, lookup != 0 ? lookup : iat, false, vaBased: false);
        }
        throw Bad($"the import table has more than {MaxImportDlls} DLLs", rva);
    }

    void ReadDelayImports()
    {
        var (rva, size) = dirs[13];
        if (rva == 0 || size == 0) return;
        for (int i = 0; i < MaxImportDlls; i++)
        {
            uint d = rva + (uint)(i * 32);
            var o = Offset(d, 32, "a delay-load descriptor");
            uint attrs = U32(o, "a delay-load descriptor"), nameRef = U32(o + 4, "a delay-load descriptor"), names = U32(o + 16, "a delay-load descriptor");
            if (nameRef == 0 && names == 0) return;
            bool va = (attrs & 1) == 0;   // very old linkers stored addresses, not RVAs
            var dll = CString(Rva(nameRef, va), "a delay-loaded DLL name");
            ReadThunks(dll, Rva(names, va), true, va);
        }
        throw Bad($"the delay-load table has more than {MaxImportDlls} DLLs", rva);
    }

    uint Rva(ulong value, bool va)
    {
        if (!va)
        {
            if (value > uint.MaxValue) throw new InvalidDataException($"the RVA 0x{value:X} is too large for a PE image");
            return (uint)value;
        }
        if (value < ImageBase || value - ImageBase > uint.MaxValue) throw Bad($"the address 0x{value:X} isn't inside the image", (long)Math.Min(value, long.MaxValue));
        return (uint)(value - ImageBase);
    }

    void ReadThunks(string dll, uint table, bool delayed, bool vaBased)
    {
        int width = Is64 ? 8 : 4;
        for (int i = 0; i < MaxImportsPerDll; i++)
        {
            var o = Offset(table + (uint)(i * width), (uint)width, $"the import list of {dll}");
            ulong t = Is64 ? U64(o, "an import entry") : U32(o, "an import entry");
            if (t == 0) return;
            ulong ordinalFlag = Is64 ? 0x8000000000000000UL : 0x80000000UL;
            if ((t & ordinalFlag) != 0) { Imports.Add(new PeImport(dll, null, (ushort)(t & 0xffff), delayed)); continue; }
            var hint = Rva(Is64 ? t & 0x7fffffffffffffffUL : t, vaBased);
            var name = CString(hint + 2, $"an imported function name in {dll}");
            Imports.Add(new PeImport(dll, name, null, delayed));
        }
        throw Bad($"the import list of {dll} has more than {MaxImportsPerDll} entries", table);
    }

    // ---- exports ----

    void ReadExports()
    {
        var (rva, size) = dirs[0];
        if (rva == 0 || size == 0) return;
        var o = Offset(rva, 40, "the export directory");
        uint nameRva = U32(o + 12, "the export directory"), ordBase = U32(o + 16, "the export directory");
        uint nFuncs = U32(o + 20, "the export directory"), nNames = U32(o + 24, "the export directory");
        uint funcs = U32(o + 28, "the export directory"), names = U32(o + 32, "the export directory"), ords = U32(o + 36, "the export directory");
        if (nFuncs > MaxExports || nNames > MaxExports) throw Bad($"the export table says it has {Math.Max(nFuncs, nNames):N0} entries (more than {MaxExports})", o + 20);
        if (nameRva != 0) ExportDllName = CString(nameRva, "the export DLL name");
        var funcOff = nFuncs > 0 ? Offset(funcs, nFuncs * 4, "the export address list") : 0;
        var nameOff = nNames > 0 ? Offset(names, nNames * 4, "the export name list") : 0;
        var ordOff = nNames > 0 ? Offset(ords, nNames * 2, "the export ordinal list") : 0;
        var named = new Dictionary<uint, string>();
        for (uint i = 0; i < nNames; i++)
        {
            var idx = U16(ordOff + i * 2, "an export ordinal");
            if (idx >= nFuncs) throw Bad($"an export name points at entry {idx}, past the {nFuncs} entries", ordOff + i * 2);
            named.TryAdd(idx, CString(U32(nameOff + i * 4, "an export name"), "an exported name"));
        }
        for (uint i = 0; i < nFuncs; i++)
        {
            var f = U32(funcOff + i * 4, "an export address");
            if (f == 0) continue;
            string? fwd = f >= rva && f - rva < size ? CString(f, "an export forwarder") : null;
            Exports.Add(new PeExport(named.GetValueOrDefault(i), ordBase + i, f, fwd));
        }
    }

    public PeExport? Export(string name) => Exports.FirstOrDefault(e => e.Name == name);

    // ---- debug ----

    void ReadDebug()
    {
        var (rva, size) = dirs[6];
        if (rva == 0 || size == 0) return;
        int n = (int)Math.Min(size / 28, 64);
        var o = Offset(rva, (uint)(n * 28), "the debug directory");
        for (int i = 0; i < n; i++)
        {
            long e = o + i * 28;
            uint type = U32(e + 12, "a debug entry"), len = U32(e + 16, "a debug entry"), raw = U32(e + 24, "a debug entry");
            if (type == 16) Reproducible = true;
            if (type != 2 || PdbPath != null || len < 24 || len > 4096) continue;
            Need(raw, len, "the CodeView debug entry");
            if (U32(raw, "the CodeView debug entry") == 0x53445352)   // "RSDS"
            {
                var path = Data.AsSpan((int)raw + 24, (int)len - 24);
                int z = path.IndexOf((byte)0); if (z >= 0) path = path[..z];
                PdbPath = Encoding.UTF8.GetString(path);
            }
            else if (U32(raw, "the CodeView debug entry") == 0x3031424e && len > 16)  // "NB10" (old)
            {
                var path = Data.AsSpan((int)raw + 16, (int)len - 16);
                int z = path.IndexOf((byte)0); if (z >= 0) path = path[..z];
                PdbPath = Encoding.UTF8.GetString(path);
            }
        }
    }

    // ---- TLS ----

    void ReadTls()
    {
        var (rva, size) = dirs[9];
        if (rva == 0 || size == 0) return;
        var o = Offset(rva, Is64 ? 40u : 24u, "the TLS directory");
        ulong cb = Is64 ? U64(o + 24, "the TLS directory") : U32(o + 12, "the TLS directory");
        if (cb == 0) return;
        var list = Rva(cb, va: true);
        int width = Is64 ? 8 : 4, count = 0;
        for (; count < 64; count++)
        {
            var e = Offset(list + (uint)(count * width), (uint)width, "the TLS callback list");
            ulong v = Is64 ? U64(e, "a TLS callback") : U32(e, "a TLS callback");
            if (v == 0) break;
        }
        TlsCallbacks = count;
    }

    // ---- strings ----

    /// <summary>Printable ASCII runs and UTF-16LE runs of at least MinStringChars characters, each cut at MaxStringChars,
    /// at most MaxStrings in all, in file order (ASCII and wide mixed by offset).</summary>
    public List<PeString> Strings() => FindStrings(Data);

    public static List<PeString> FindStrings(byte[] data, int min = MinStringChars, int maxChars = MaxStringChars, int maxCount = MaxStrings)
    {
        static bool P(byte c) => c is >= 0x20 and < 0x7f or (byte)'\t';
        var list = new List<PeString>();
        var b = new StringBuilder();
        // ASCII
        long start = -1;
        for (long i = 0; i <= data.LongLength && list.Count < maxCount; i++)
        {
            if (i < data.LongLength && P(data[i]))
            {
                if (start < 0) { start = i; b.Clear(); }
                if (b.Length < maxChars) b.Append((char)data[i]);
                continue;
            }
            if (start >= 0 && i - start >= min) list.Add(new PeString(start, b.ToString(), false));
            start = -1;
        }
        // UTF-16LE (printable ASCII range in the low byte, zero high byte), at both alignments
        var wide = new List<PeString>();
        for (int align = 0; align < 2; align++)
        {
            start = -1;
            for (long i = align; i <= data.LongLength && wide.Count + list.Count < maxCount; i += 2)
            {
                bool ok = i + 1 < data.LongLength && P(data[i]) && data[i + 1] == 0;
                if (ok)
                {
                    if (start < 0) { start = i; b.Clear(); }
                    if (b.Length < maxChars) b.Append((char)data[i]);
                    continue;
                }
                if (start >= 0 && (i - start) / 2 >= min) wide.Add(new PeString(start, b.ToString(), true));
                start = -1;
            }
        }
        list.AddRange(wide);
        list.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        if (list.Count > maxCount) list.RemoveRange(maxCount, list.Count - maxCount);
        return list;
    }
}
