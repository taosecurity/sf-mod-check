// The PE reader (0.8.0, triage chunk C4). Every PE file here is made byte by byte by PeBuilder: no outside binaries.
using System.Buffers.Binary;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

/// <summary>Builds a small PE file: headers plus one ".rdata" section holding the import, delay-load, export and debug
/// tables, any extra data and strings. Not runnable (no code); only for the reader.</summary>
public sealed class PeBuilder
{
    public bool Is64 { get; set; } = true;
    public ushort Machine { get; set; } = PeFile.MachineAmd64;
    public bool Dll { get; set; } = true;
    public uint Time { get; set; } = 1_767_225_600;   // 2026-01-01 00:00:00 UTC
    public ushort DllCharacteristics { get; set; } = 0x0160;   // high-entropy ASLR, ASLR, DEP
    public List<(string Dll, object[] Functions, bool Delayed)> Imports { get; } = new();
    /// <summary>Exports: name and the bytes it points at (put in the section).</summary>
    public List<(string Name, byte[] Data)> Exports { get; } = new();
    public string? Pdb { get; set; }
    public List<string> Ascii { get; } = new();
    public List<string> Wide { get; } = new();

    const uint SecRva = 0x1000, SecRaw = 0x400;
    readonly List<byte> sec = new();
    readonly (uint Rva, uint Size)[] dirs = new (uint, uint)[16];

    uint Put(byte[] b, int align = 8)
    {
        while (sec.Count % align != 0) sec.Add(0);
        var rva = SecRva + (uint)sec.Count;
        sec.AddRange(b);
        return rva;
    }
    uint PutString(string s) => Put([.. Encoding.ASCII.GetBytes(s), 0], 2);
    static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    byte[] Thunk(ulong v) => Is64 ? U64(v) : U32((uint)v);

    /// <summary>The finished file. Also returns, through RvaOf, where each export's data landed.</summary>
    public byte[] Build()
    {
        sec.Clear();
        // imports
        var normal = Imports.Where(i => !i.Delayed).ToList();
        var delayed = Imports.Where(i => i.Delayed).ToList();
        uint ThunkList(object[] fns)
        {
            var entries = new List<ulong>();
            foreach (var f in fns)
            {
                if (f is string name)
                {
                    var hint = new List<byte> { 0, 0 };
                    hint.AddRange(Encoding.ASCII.GetBytes(name)); hint.Add(0);
                    entries.Add(Put(hint.ToArray(), 2));
                }
                else entries.Add((Is64 ? 0x8000000000000000UL : 0x80000000UL) | Convert.ToUInt16(f));
            }
            var list = new List<byte>();
            foreach (var e in entries) list.AddRange(Thunk(e));
            list.AddRange(Thunk(0));
            return Put(list.ToArray());
        }
        if (normal.Count > 0)
        {
            var descs = new List<byte>();
            foreach (var (dll, fns, _) in normal)
            {
                var name = PutString(dll);
                var ilt = ThunkList(fns);
                var iat = ThunkList(fns);
                descs.AddRange(U32(ilt)); descs.AddRange(U32(0)); descs.AddRange(U32(0)); descs.AddRange(U32(name)); descs.AddRange(U32(iat));
            }
            descs.AddRange(new byte[20]);
            dirs[1] = (Put(descs.ToArray(), 4), (uint)descs.Count);
        }
        if (delayed.Count > 0)
        {
            var descs = new List<byte>();
            foreach (var (dll, fns, _) in delayed)
            {
                var name = PutString(dll);
                var hmod = Put(new byte[8]);
                var names = ThunkList(fns);
                var iat = ThunkList(fns);
                foreach (var v in new uint[] { 1, name, hmod, iat, names, 0, 0, 0 }) descs.AddRange(U32(v));
            }
            descs.AddRange(new byte[32]);
            dirs[13] = (Put(descs.ToArray(), 4), (uint)descs.Count);
        }
        // exports (names sorted, as linkers write them)
        if (Exports.Count > 0)
        {
            var dllName = PutString("Fixture.dll");
            var ordered = Exports.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            var funcRvas = ordered.Select(e => Put(e.Data, 16)).ToList();
            var nameRvas = ordered.Select(e => PutString(e.Name)).ToList();
            var funcs = Put(funcRvas.SelectMany(U32).ToArray(), 4);
            var names = Put(nameRvas.SelectMany(U32).ToArray(), 4);
            var ords = Put(Enumerable.Range(0, ordered.Count).SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray(), 2);
            var dir = new List<byte>();
            foreach (var v in new uint[] { 0, Time, 0, dllName, 1, (uint)ordered.Count, (uint)ordered.Count, funcs, names, ords }) dir.AddRange(U32(v));
            dirs[0] = (Put(dir.ToArray(), 4), 40);
        }
        // debug: CodeView RSDS
        if (Pdb != null)
        {
            var cv = new List<byte>(Encoding.ASCII.GetBytes("RSDS"));
            cv.AddRange(Enumerable.Repeat((byte)0xAB, 16)); cv.AddRange(U32(1));
            cv.AddRange(Encoding.UTF8.GetBytes(Pdb)); cv.Add(0);
            var cvRva = Put(cv.ToArray(), 4);
            var entry = new List<byte>();
            foreach (var v in new uint[] { 0, Time, 0, 2, (uint)cv.Count, cvRva, SecRaw + (cvRva - SecRva) }) entry.AddRange(U32(v));
            dirs[6] = (Put(entry.ToArray(), 4), 28);
        }
        foreach (var s in Ascii) Put([0, .. Encoding.ASCII.GetBytes(s), 0], 1);
        foreach (var s in Wide) Put([0, 0, .. Encoding.Unicode.GetBytes(s), 0, 0], 2);
        while (sec.Count % 0x200 != 0 || sec.Count == 0) sec.Add(0);

        // headers
        int optSize = Is64 ? 240 : 224;
        var file = new byte[SecRaw + sec.Count];
        file[0] = (byte)'M'; file[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x3c), 0x80);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(file, 0x80);
        int coff = 0x84;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff), Machine);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff + 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(coff + 4), Time);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff + 16), (ushort)optSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff + 18), (ushort)(0x0002 | (Is64 ? 0x0020 : 0x0100) | (Dll ? 0x2000 : 0)));
        int opt = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(opt), (ushort)(Is64 ? 0x20b : 0x10b));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 16), 0);   // no entry point
        if (Is64) BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(opt + 24), 0x180000000);
        else BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 28), 0x10000000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 32), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 36), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 56), SecRva + (uint)((sec.Count + 0xfff) & ~0xfff));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 60), SecRaw);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(opt + 68), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(opt + 70), DllCharacteristics);
        int dirStart = Is64 ? 112 : 96;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirStart - 4), 16);
        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirStart + i * 8), dirs[i].Rva);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirStart + i * 8 + 4), dirs[i].Size);
        }
        int s0 = opt + optSize;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(file, s0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s0 + 8), (uint)sec.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s0 + 12), SecRva);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s0 + 16), (uint)sec.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s0 + 20), SecRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(s0 + 36), 0x40000040);
        sec.ToArray().CopyTo(file, (int)SecRaw);
        return file;
    }

    /// <summary>The file offset of the data directory entry i (for tests that break a table on purpose).</summary>
    public int DirOffset(int i) => 0x84 + 20 + (Is64 ? 112 : 96) + i * 8;

    /// <summary>A fake SFSEPluginVersionData (SFSE's public PluginAPI.h layout, 604 bytes).</summary>
    public static byte[] SfseVersion(string name, string author, uint pluginVersion, uint address, uint structure, uint[] games, uint seMin = 0, uint dataVersion = 1)
    {
        var b = new byte[604];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), dataVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), pluginVersion);
        Encoding.ASCII.GetBytes(name).CopyTo(b, 8);
        Encoding.ASCII.GetBytes(author).CopyTo(b, 264);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(520), address);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(524), structure);
        for (int i = 0; i < games.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(528 + i * 4), games[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(592), seMin);
        return b;
    }

    /// <summary>MAKE_EXE_VERSION_EX.</summary>
    public static uint Packed(uint major, uint minor, uint build, uint sub = 0) => (major << 24) | (minor << 16) | (build << 4) | sub;
}

public class PeTests
{
    static PeBuilder Sample()
    {
        var b = new PeBuilder();
        b.Imports.Add(("KERNEL32.dll", ["GetProcAddress", "LoadLibraryW", (ushort)17], false));
        b.Imports.Add(("WS2_32.dll", ["connect", "send"], false));
        b.Imports.Add(("dbghelp.dll", ["MiniDumpWriteDump"], true));
        b.Exports.Add(("SFSEPlugin_Load", [0xC3]));
        b.Exports.Add(("SFSEPlugin_Version", PeBuilder.SfseVersion("Fake", "Someone", 3, 0, 0, [PeBuilder.Packed(1, 16, 244)])));
        b.Pdb = @"F:\dev\fake\build\Fake.pdb";
        b.Ascii.Add("see https://example.com/fake for help");
        b.Wide.Add("WideStringHere");
        b.Ascii.Add("short");
        return b;
    }

    [Fact]
    public void Reads_headers_imports_exports_and_pdb()
    {
        var pe = PeFile.Parse(Sample().Build(), "fake.dll");
        Assert.Empty(pe.Problems);
        Assert.True(pe.Is64);
        Assert.Equal("x64", pe.MachineName);
        Assert.True(pe.IsDll);
        Assert.False(pe.IsExe);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), pe.BuildTime);
        Assert.True(pe.Aslr); Assert.True(pe.Dep); Assert.False(pe.Cfg);
        Assert.Equal("Windows GUI", pe.SubsystemName);
        Assert.Single(pe.Sections);
        Assert.Equal(".rdata", pe.Sections[0].Name);

        Assert.Contains(pe.Imports, i => i.Dll == "WS2_32.dll" && i.Name == "connect" && !i.Delayed);
        Assert.Contains(pe.Imports, i => i.Dll == "KERNEL32.dll" && i.Name == null && i.Ordinal == 17 && i.Display == "#17");
        Assert.Contains(pe.Imports, i => i.Dll == "dbghelp.dll" && i.Name == "MiniDumpWriteDump" && i.Delayed);
        Assert.Equal(6, pe.Imports.Count);

        Assert.Equal("Fixture.dll", pe.ExportDllName);
        Assert.Equal(["SFSEPlugin_Load", "SFSEPlugin_Version"], pe.Exports.Select(e => e.Name).ToArray());
        var v = pe.Export("SFSEPlugin_Version")!;
        var data = pe.BytesAt(v.Rva, 604)!;
        Assert.Equal("Fake", Encoding.ASCII.GetString(data, 8, 4));

        Assert.Equal(@"F:\dev\fake\build\Fake.pdb", pe.PdbPath);
        Assert.Null(pe.Certificate);
        Assert.False(pe.IsDotNet);
    }

    [Fact]
    public void Reads_a_32_bit_file()
    {
        var b = new PeBuilder { Is64 = false, Machine = PeFile.MachineI386, Dll = false };
        b.Imports.Add(("KERNEL32.dll", ["CreateProcessA", (ushort)5], false));
        var pe = PeFile.Parse(b.Build());
        Assert.False(pe.Is64);
        Assert.True(pe.IsExe);
        Assert.Equal("x86 (32-bit)", pe.MachineName);
        Assert.Contains(pe.Imports, i => i.Name == "CreateProcessA");
        Assert.Contains(pe.Imports, i => i.Ordinal == 5);
    }

    [Fact]
    public void Finds_ascii_and_wide_strings_of_six_or_more()
    {
        var pe = PeFile.Parse(Sample().Build());
        var strings = pe.Strings();
        Assert.Contains(strings, s => s.Text == "see https://example.com/fake for help" && !s.Wide);
        Assert.Contains(strings, s => s.Text == "WideStringHere" && s.Wide);
        Assert.DoesNotContain(strings, s => s.Text == "short");
        Assert.True(strings.Zip(strings.Skip(1)).All(p => p.First.Offset <= p.Second.Offset));

        var capped = PeFile.FindStrings(Encoding.ASCII.GetBytes(new string('a', 5000)), maxChars: 100);
        Assert.Single(capped);
        Assert.Equal(100, capped[0].Text.Length);
        Assert.Equal(3, PeFile.FindStrings(Encoding.ASCII.GetBytes("aaaaaa\0bbbbbb\0cccccc\0dddddd"), maxCount: 3).Count);
    }

    [Fact]
    public void Truncated_or_wrong_files_say_where()
    {
        var good = Sample().Build();
        var e = Assert.Throws<InvalidDataException>(() => PeFile.Parse(good[..0x100]));
        Assert.Contains("offset 0x", e.Message);
        Assert.Contains("too short", Assert.Throws<InvalidDataException>(() => PeFile.Parse(new byte[10])).Message);
        Assert.Contains("MZ", Assert.Throws<InvalidDataException>(() => PeFile.Parse(Encoding.ASCII.GetBytes(new string('x', 200)))).Message);

        var badPtr = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badPtr.AsSpan(0x3c), 0x7fffffff);
        Assert.Contains("offset 0x3C", Assert.Throws<InvalidDataException>(() => PeFile.Parse(badPtr)).Message);

        var manySections = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(manySections.AsSpan(0x86), 60000);
        Assert.Contains("sections", Assert.Throws<InvalidDataException>(() => PeFile.Parse(manySections)).Message);

        var badMagic = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(badMagic.AsSpan(0x98), 0x1234);
        Assert.Contains("magic", Assert.Throws<InvalidDataException>(() => PeFile.Parse(badMagic)).Message);
    }

    [Fact]
    public void A_crafted_table_is_skipped_with_its_place()
    {
        var b = Sample();
        var good = b.Build();

        // the import table points far outside the file
        var imp = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(imp.AsSpan(b.DirOffset(1)), 0x7fff0000);
        var pe = PeFile.Parse(imp);
        Assert.Contains(pe.Problems, p => p.StartsWith("import table:") && p.Contains("RVA 0x7FFF0000"));
        Assert.DoesNotContain(pe.Imports, i => !i.Delayed);
        Assert.Equal(2, pe.Exports.Count);   // the rest is still read

        // the export table claims four billion entries
        var exp = (byte[])good.Clone();
        var (erva, _) = PeFile.Parse(good).Directory(0);
        var eoff = (int)PeFile.Parse(good).TryOffset(erva, 40)!;
        BinaryPrimitives.WriteUInt32LittleEndian(exp.AsSpan(eoff + 20), 0xffffffff);
        Assert.Contains(PeFile.Parse(exp).Problems, p => p.StartsWith("export table:") && p.Contains("offset 0x"));

        // the debug entry's raw pointer is past the end
        var dbg = (byte[])good.Clone();
        var (drva, _) = PeFile.Parse(good).Directory(6);
        var doff = (int)PeFile.Parse(good).TryOffset(drva, 28)!;
        BinaryPrimitives.WriteUInt32LittleEndian(dbg.AsSpan(doff + 24), 0x7ffffff0);
        var dpe = PeFile.Parse(dbg);
        Assert.Null(dpe.PdbPath);
        Assert.Contains(dpe.Problems, p => p.StartsWith("debug directory:"));
    }

    [Fact]
    public void An_import_list_with_no_end_stops_at_the_file_end()
    {
        // one import list filled to the end of the file with ordinal entries and no zero: the reader must stop, not loop
        var b = new PeBuilder();
        b.Imports.Add(("KERNEL32.dll", ["GetProcAddress"], false));
        var file = b.Build();
        var pe = PeFile.Parse(file);
        var (rva, _) = pe.Directory(1);
        var desc = (int)pe.TryOffset(rva, 20)!;
        var ilt = (int)pe.TryOffset(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(desc)), 8)!;
        for (int o = ilt; o + 8 <= file.Length; o += 8) BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(o), 0x8000000000000001UL);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bad = PeFile.Parse(file);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(bad.Problems, p => p.StartsWith("import table:"));
    }

    [Fact]
    public void Random_damage_gives_only_InvalidDataException()
    {
        var good = Sample().Build();
        var rng = new Random(4242);
        for (int round = 0; round < 3000; round++)
        {
            var b = (byte[])good.Clone();
            int flips = rng.Next(1, 12);
            for (int i = 0; i < flips; i++)
            {
                // mostly hit the headers and tables, where damage matters
                int at = rng.Next(4) == 0 ? rng.Next(b.Length) : rng.Next(Math.Min(b.Length, 0x400 + 0x400));
                b[at] = (byte)rng.Next(256);
            }
            if (rng.Next(10) == 0) b = b[..rng.Next(b.Length)];
            try
            {
                var pe = PeFile.Parse(b);
                _ = pe.Strings();
            }
            catch (InvalidDataException) { }
        }
    }
}
