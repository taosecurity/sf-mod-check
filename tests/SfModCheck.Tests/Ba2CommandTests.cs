// sf-mod-check ba2 list / extract (0.22.0, F) on made-up archives built here: one file, a glob, stored, zlib and LZ4
// entries, a missing file (exit 2), every kind of path escape refused, no overwrite without --force, the size cap,
// a texture archive (listed, not extracted) and a damaged entry. Each fails on 0.21.0, which has no ba2 command.
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class Ba2CommandTests
{
    enum Pack { Stored, Zlib, Lz4 }

    /// <summary>A general (GNRL) BA2: version 1 with stored and zlib entries, or version 3 naming LZ4 (compression 3)
    /// when any entry is LZ4. sizeFix changes the size an entry's record states (a damaged archive).</summary>
    static byte[] Gnrl(params (string Name, byte[] Data, Pack How)[] files) => Gnrl(null, files);

    static byte[] Gnrl(Func<int, uint, uint>? sizeFix, params (string Name, byte[] Data, Pack How)[] files)
    {
        bool lz4 = files.Any(f => f.How == Pack.Lz4);
        var stored = files.Select(f =>
        {
            switch (f.How)
            {
                case Pack.Zlib:
                    var m = new MemoryStream();
                    using (var z = new ZLibStream(m, CompressionLevel.Optimal, leaveOpen: true)) z.Write(f.Data);
                    return m.ToArray();
                case Pack.Lz4:
                    var t = new byte[LZ4Codec.MaximumOutputSize(f.Data.Length)];
                    return t[..LZ4Codec.Encode(f.Data, t)];
                default: return f.Data;
            }
        }).ToList();
        int head = lz4 ? 36 : 24;
        long dataAt = head + 36L * files.Length;
        long nameAt = dataAt + stored.Sum(s => (long)s.Length);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BTDX")); w.Write(lz4 ? 3u : 1u); w.Write(Encoding.ASCII.GetBytes("GNRL")); w.Write((uint)files.Length); w.Write((ulong)nameAt);
        if (lz4) { w.Write(0UL); w.Write(3u); }
        long at = dataAt;
        for (int i = 0; i < files.Length; i++)
        {
            var size = (uint)files[i].Data.Length;
            if (sizeFix != null) size = sizeFix(i, size);
            w.Write(0u); w.Write(Encoding.ASCII.GetBytes("wem\0")); w.Write(0u); w.Write(0x100100u);
            w.Write((ulong)at); w.Write(files[i].How == Pack.Stored ? 0u : (uint)stored[i].Length); w.Write(size); w.Write(0xBAADF00Du);
            at += stored[i].Length;
        }
        foreach (var s in stored) w.Write(s);
        foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Name); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        return o.ToArray();
    }

    static byte[] Bytes(string s, int repeat = 1) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(s, repeat)));

    static readonly byte[] A = Bytes("made-up voice line A ", 50), B = Bytes("made-up voice line B ", 80), C = Bytes("script ", 300);

    sealed class Box : Sandbox
    {
        public string Archive => Path.Combine(Dir, "Made - Main.ba2");
        public string Out => Path.Combine(Dir, "out");
        public Box(byte[]? ba2 = null) => File.WriteAllBytes(Archive, ba2 ?? Gnrl(
            (@"sound\voice\made.esm\npcmvoice\0001a2b3_1.wem", A, Pack.Stored),
            (@"Sound\Voice\Made.esm\NpcFVoice\0001A2B4_1.wem", B, Pack.Zlib),
            (@"scripts\made\quest.pex", C, Pack.Zlib)));
        public (int Code, string Stdout, string Stderr) Ba2(params string[] args) => ExeTests.RunSplit(["ba2", .. args]);
        public string OutFile(string rel) => Path.Combine(Out, rel.Replace('\\', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void List_shows_every_file_with_its_size_and_storage_and_a_pattern_narrows_it()
    {
        using var b = new Box();
        var (code, stdout, _) = b.Ba2("list", b.Archive);
        Assert.Equal(0, code);
        Assert.Contains("1,050  stored   sound\\voice\\made.esm\\npcmvoice\\0001a2b3_1.wem", stdout);
        Assert.Contains("zlib     Sound\\Voice\\Made.esm\\NpcFVoice\\0001A2B4_1.wem", stdout);   // the archive's own case
        Assert.Contains("RESULT: 3 of 3 files listed (GNRL)", stdout);
        var some = b.Ba2("list", b.Archive, "sound/voice/*.WEM");
        Assert.Equal(0, some.Code);
        Assert.Contains("RESULT: 2 of 3 files listed", some.Stdout);
        Assert.DoesNotContain("quest.pex", some.Stdout);
        var none = b.Ba2("list", b.Archive, "*.nif");
        Assert.Equal(1, none.Code);
        Assert.Contains("RESULT: NOT FOUND", none.Stdout);
    }

    [Fact]
    public void Extract_writes_one_named_file_under_out_keeping_its_folders()
    {
        using var b = new Box();
        var (code, stdout, _) = b.Ba2("extract", b.Archive, @"sound\voice\made.esm\npcmvoice\0001a2b3_1.wem", "--out", b.Out);
        Assert.Equal(0, code);
        Assert.Equal(A, File.ReadAllBytes(b.OutFile(@"sound\voice\made.esm\npcmvoice\0001a2b3_1.wem")));
        Assert.Contains("RESULT: OK (wrote 1 of 1 files, 1,050 bytes", stdout);
        Assert.Single(Directory.EnumerateFiles(b.Out, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Extract_with_a_glob_writes_each_match_stored_and_zlib()
    {
        using var b = new Box();
        var (code, stdout, stderr) = b.Ba2("extract", b.Archive, "*.wem", "--out", b.Out, "--verbose");
        Assert.Equal(0, code);
        Assert.Equal(A, File.ReadAllBytes(b.OutFile(@"sound\voice\made.esm\npcmvoice\0001a2b3_1.wem")));
        Assert.Equal(B, File.ReadAllBytes(b.OutFile(@"Sound\Voice\Made.esm\NpcFVoice\0001A2B4_1.wem")));
        Assert.False(File.Exists(b.OutFile(@"scripts\made\quest.pex")));
        Assert.Contains("wrote 2 of 2 files", stdout);
        Assert.Contains("log: pattern *.wem: 2 of 3 files", stderr);   // --verbose logs the new path
        Assert.Contains("log: wrote ", stderr);
    }

    [Fact]
    public void Extract_unpacks_lz4_entries_of_a_version_3_archive()
    {
        using var b = new Box(Gnrl((@"scripts\made\a.pex", C, Pack.Lz4), (@"scripts\made\b.pex", A, Pack.Stored)));
        var (code, stdout, _) = b.Ba2("extract", b.Archive, @"scripts\made\*", "--out", b.Out);
        Assert.True(code == 0, stdout);
        Assert.Equal(C, File.ReadAllBytes(b.OutFile(@"scripts\made\a.pex")));
        Assert.Equal(A, File.ReadAllBytes(b.OutFile(@"scripts\made\b.pex")));
        Assert.Contains("lz4", b.Ba2("list", b.Archive).Stdout);
    }

    [Fact]
    public void A_missing_file_or_archive_is_exit_2_and_writes_nothing()
    {
        using var b = new Box();
        var (code, stdout, _) = b.Ba2("extract", b.Archive, @"sound\voice\nothere.wem", "--out", b.Out);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (no file in Made - Main.ba2 matches", stdout);
        Assert.False(Directory.Exists(b.Out) && Directory.EnumerateFileSystemEntries(b.Out).Any());
        var noArchive = b.Ba2("extract", Path.Combine(b.Dir, "none.ba2"), "*", "--out", b.Out);
        Assert.Equal(2, noArchive.Code);
        Assert.Contains("there is no archive file", noArchive.Stderr);
        Assert.Equal(2, b.Ba2("extract", b.Archive, "*").Code);   // no --out
        File.WriteAllText(Path.Combine(b.Dir, "bad.ba2"), "not an archive at all, just text");
        var bad = b.Ba2("list", Path.Combine(b.Dir, "bad.ba2"));
        Assert.Equal(2, bad.Code);
        Assert.Contains("not a BA2 archive", bad.Stderr);
    }

    [Theory]
    [InlineData(@"..\evil.txt", "climbs out")]
    [InlineData(@"sound\..\..\evil.txt", "climbs out")]
    [InlineData(@"\windows\evil.txt", "root")]
    [InlineData(@"/etc/evil.txt", "root")]
    [InlineData(@"\\server\share\evil.txt", "root")]
    [InlineData(@"C:\evil.txt", "':'")]
    [InlineData(@"C:evil.txt", "':'")]
    [InlineData(@"sound\evil.txt:stream", "':'")]
    [InlineData(@"sound\CON", "reserved")]
    [InlineData(@"sound\nul.txt", "reserved")]
    [InlineData(@"sound\com1.wem", "reserved")]
    [InlineData(@"sound\evil.", "dot or a space")]
    [InlineData(@"sound \evil.txt", "dot or a space")]
    [InlineData("sound\\ev\nil.txt", "control character")]
    [InlineData(@"sound\\evil.txt", "empty folder")]
    [InlineData(@"sound\ev?il.txt", "doesn't allow")]
    public void A_name_that_would_leave_out_is_refused_and_the_rest_are_written(string evil, string why)
    {
        using var b = new Box(Gnrl((evil, A, Pack.Stored), (@"sound\fine.wem", B, Pack.Stored)));
        var (code, stdout, _) = b.Ba2("extract", b.Archive, "*", "--out", Path.Combine(b.Dir, "deep", "out"));
        Assert.Equal(1, code);
        Assert.Contains("ERROR: ", stdout);
        Assert.Contains(why, stdout);
        Assert.Contains("wrote 1 of 2 files", stdout);
        Assert.True(File.Exists(Path.Combine(b.Dir, "deep", "out", "sound", "fine.wem")));
        // nothing outside the out folder: the sandbox holds the archive and deep\out only
        Assert.Equal([b.Archive], Directory.EnumerateFiles(b.Dir).ToArray());
        Assert.Equal([Path.Combine(b.Dir, "deep", "out")], Directory.EnumerateDirectories(Path.Combine(b.Dir, "deep")).ToArray());
        Assert.False(File.Exists(Path.Combine(b.Dir, "evil.txt")));
        Assert.NotNull(Ba2Command.UnsafeReason(evil));
    }

    [Fact]
    public void A_folder_link_inside_out_is_not_followed()
    {
        using var b = new Box(Gnrl((@"sound\evil.wem", A, Pack.Stored)));
        var elsewhere = b.Sub("elsewhere");
        Directory.CreateDirectory(b.Out);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // a junction needs no special right on Windows (a symbolic link does)
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(b.Out, "sound")}\" \"{elsewhere}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                p.WaitForExit();
                if (p.ExitCode != 0) return;
            }
            else Directory.CreateSymbolicLink(Path.Combine(b.Out, "sound"), elsewhere);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return; }
        var (code, stdout, _) = b.Ba2("extract", b.Archive, "*", "--out", b.Out);
        Assert.Equal(1, code);
        Assert.Contains("is a link (junction or symbolic link), which isn't followed", stdout);
        Assert.Empty(Directory.EnumerateFiles(elsewhere));
    }

    [Fact]
    public void Ordinary_names_are_safe()
    {
        Assert.Null(Ba2Command.UnsafeReason(@"sound\voice\made.esm\npcmvoice\0001a2b3_1.wem"));
        Assert.Null(Ba2Command.UnsafeReason("meshes/made/console.nif"));   // "console" isn't CON
        Assert.Null(Ba2Command.UnsafeReason(@"scripts\fragments\quests\qf_made_0100.pex"));
    }

    [Fact]
    public void An_existing_file_is_kept_without_force_and_replaced_with_it()
    {
        using var b = new Box();
        var dest = b.OutFile(@"sound\voice\made.esm\npcmvoice\0001a2b3_1.wem");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "mine");
        var (code, stdout, _) = b.Ba2("extract", b.Archive, "*.wem", "--out", b.Out);
        Assert.Equal(1, code);
        Assert.Contains("already exists (use --force to overwrite)", stdout);
        Assert.Equal("mine", File.ReadAllText(dest));
        Assert.Equal(B, File.ReadAllBytes(b.OutFile(@"Sound\Voice\Made.esm\NpcFVoice\0001A2B4_1.wem")));   // the other one still written
        var forced = b.Ba2("extract", b.Archive, "*.wem", "--out", b.Out, "--force");
        Assert.Equal(0, forced.Code);
        Assert.Equal(A, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Over_the_size_cap_nothing_is_written()
    {
        using var b = new Box();
        // the three files are 1,050 + 1,680 + 2,100 bytes; a cap of 0.000002 GB is 2,000 bytes
        var (code, stdout, _) = b.Ba2("extract", b.Archive, "*", "--out", b.Out, "--max-size", "0.000002");
        Assert.Equal(2, code);
        Assert.Contains("add up to 4,830 bytes, more than --max-size allows (2,000 bytes); nothing was written", stdout);
        Assert.Empty(Directory.EnumerateFiles(b.Out, "*", SearchOption.AllDirectories));
        Assert.Equal(0, b.Ba2("extract", b.Archive, "*", "--out", b.Out, "--max-size", "0.000005").Code);
        Assert.Equal(2, b.Ba2("extract", b.Archive, "*", "--out", b.Out, "--max-size", "zero").Code);
    }

    [Fact]
    public void A_damaged_entry_is_not_written_and_leaves_no_partial_file()
    {
        // the zlib entry's record says one byte more than it holds; the LZ4 archive's says one byte less
        using var b = new Box(Gnrl((i, s) => i == 0 ? s + 1 : s, (@"sound\a.wem", A, Pack.Zlib), (@"sound\b.wem", B, Pack.Stored)));
        var (code, stdout, _) = b.Ba2("extract", b.Archive, "*", "--out", b.Out);
        Assert.Equal(1, code);
        Assert.Contains(@"ERROR: sound\a.wem: not written, it holds less than", stdout);
        Assert.False(File.Exists(b.OutFile(@"sound\a.wem")));
        Assert.True(File.Exists(b.OutFile(@"sound\b.wem")));
        using var l = new Box(Gnrl((i, s) => s - 1, (@"sound\c.pex", C, Pack.Lz4)));
        var lz = l.Ba2("extract", l.Archive, "*", "--out", l.Out);
        Assert.Equal(1, lz.Code);
        Assert.True(lz.Stdout.Contains("won't unpack (LZ4") || lz.Stdout.Contains("unpacks to"), lz.Stdout);
        Assert.False(File.Exists(l.OutFile(@"sound\c.pex")));
    }

    [Fact]
    public void A_texture_archive_is_listed_but_not_extracted()
    {
        using var b = new Box(AssetsTests.Ba2("DX10", @"textures\made\a.dds", @"textures\made\b.dds"));
        var list = b.Ba2("list", b.Archive);
        Assert.Equal(0, list.Code);
        Assert.Contains(@"texture  textures\made\a.dds", list.Stdout);
        var x = b.Ba2("extract", b.Archive, "*", "--out", b.Out);
        Assert.Equal(2, x.Code);
        Assert.Contains("texture (DX10) archive", x.Stdout);
    }

    [Fact]
    public void Help_and_bad_commands()
    {
        using var b = new Box();
        var h = b.Ba2("--help");
        Assert.Equal(0, h.Code);
        Assert.Contains("ba2 extract <archive> <pattern> --out <folder>", h.Stdout);
        Assert.Equal(2, b.Ba2("unpack", b.Archive).Code);
        Assert.Equal(2, b.Ba2("list", b.Archive, "--out", b.Out).Code);   // --out is for extract
        Assert.Contains("ba2 list", ExeTests.RunSplit(["--help"]).Stdout + ExeTests.RunSplit(["--help"]).Stderr);
    }
}
