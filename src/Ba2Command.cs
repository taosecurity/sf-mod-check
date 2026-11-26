// sf-mod-check ba2 (0.22.0): list the files in a BA2 archive, and extract one file or a glob of them into a folder.
// It replaces one-off readers (sf-mod-build's voice spike ba2_pull.py) and SFSearch's archive2 extract for our use.
// Design: StarfieldPluginCheck NOTES.md, "0.22.0 (SmcLookup)", "Step 5".
//   sf-mod-check ba2 list <archive> [<pattern>] [--quiet] [--verbose] [--log <file>]
//   sf-mod-check ba2 extract <archive> <pattern> --out <folder> [--force] [--max-size <GB>] [--quiet] [--verbose] [--log <file>]
// General (GNRL) archives are extracted: stored entries, zlib entries and (version 3 headers that say so) LZ4 blocks.
// Texture (DX10) archives are listed but not extracted (their DDS headers would have to be rebuilt).
// An archive's file names are untrusted: a name that would leave --out (.., a rooted, drive or network path, a
// reserved Windows name, a control character, a part ending in a dot or space) is refused, an existing file is never
// overwritten without --force, a folder link (junction or symbolic link) inside --out is never followed, and the
// files' total size is checked against --max-size before anything is written.
// Exit codes: list 0 = listed (1 = the pattern matched no file); extract 0 = every match written, 1 = some refused or
// failed (the rest written), 2 = could not run (no archive, damaged header, no match, texture archive, over the cap).
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SfModCheck;

public static class Ba2Command
{
    public const string Usage =
        "usage: sf-mod-check ba2 list <archive> [<pattern>] [--quiet] [--verbose] [--log <file>]\n" +
        "       sf-mod-check ba2 extract <archive> <pattern> --out <folder> [--force] [--max-size <GB>] [--quiet] [--verbose] [--log <file>]\n" +
        "  list      prints each file in the archive (or each one the pattern matches): its size, how it is stored\n" +
        "            (stored, zlib, lz4 or texture) and its name.\n" +
        "  extract   writes each file the pattern matches under --out, keeping the archive's folders. General archives\n" +
        "            only; texture (DX10) archives can be listed but not extracted.\n" +
        "  <pattern> a file name inside the archive (\"sound\\voice\\mymod.esm\\npcmvoice\\0001a2b3_1.wem\"), or a glob:\n" +
        "            * matches any characters (folders included) and ? one character. Case doesn't matter, and / is\n" +
        "            the same as \\. Quote it so the shell doesn't expand it.\n" +
        "  --out       the folder to write to (made if it doesn't exist)\n" +
        "  --force     overwrite files that already exist (default: an existing file is left alone and reported)\n" +
        "  --max-size  the most the files may add up to, in GB (default 2). Checked before anything is written.\n" +
        "File names that would leave --out (.., rooted, drive or network paths, reserved Windows names) are refused.\n" +
        "Exit: list 0 = listed, 1 = nothing matched, 2 = could not run; extract 0 = all written, 1 = some refused or\n" +
        "failed, 2 = could not run (no archive, damaged archive, nothing matched, texture archive, over --max-size).";

    /// <summary>The default --max-size: 2 GB.</summary>
    public const long DefaultMaxBytes = 2_000_000_000L;

    /// <summary>A compressed entry is unpacked in memory (an LZ4 block needs the whole of it); larger ones are refused.</summary>
    public const long MaxPackedInMemory = 1L << 30;

    /// <summary>One file in the archive. RawName is as the archive stores it (untrusted); Name as Ba2Names gives it.
    /// Size is the unpacked size; Packed is 0 for a stored entry. A texture entry's Size is its chunks' total.</summary>
    public sealed record Item(string RawName, string Name, ulong Offset, uint Packed, long Size);

    /// <summary>An archive's header and file list.</summary>
    public sealed record Index(uint Version, string Type, uint Method, List<Item> Items)
    {
        public string HowStored(Item it) => Type == "DX10" ? "texture" : it.Packed == 0 ? "stored" : Method == 3 ? "lz4" : "zlib";
    }

    public static int Run(string[] a, string version, string byline)
    {
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) Console.WriteLine($"RESULT: CANNOT CHECK ({cantWhy})");
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT CHECK ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        if (a.Length == 0 || a[0] is "-h" or "--help") { Console.WriteLine(Usage); cantWhy = "no ba2 command given"; return a.Length == 0 ? 2 : 0; }
        var mode = a[0];
        if (mode is not ("list" or "extract"))
        {
            Console.Error.WriteLine($"ba2 needs list or extract first, not \"{SafeText.OneLine(mode)}\", for example: sf-mod-check ba2 list \"MyMod - Main.ba2\"");
            Console.Error.WriteLine(Usage);
            cantWhy = "unknown ba2 command";
            return 2;
        }
        string? archive = null, pattern = null, outDir = null, logFile = null;
        bool force = false, quiet = false, verbose = false;
        long maxBytes = DefaultMaxBytes;
        for (int i = 1; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--out" when mode == "extract":
                    if (Cli.Value(a, ref i, "a folder to write the files to", Usage) is not { } o) return 2;
                    outDir = o; break;
                case "--force" when mode == "extract": force = true; break;
                case "--max-size" when mode == "extract":
                    if (Cli.Value(a, ref i, "a size in GB", Usage) is not { } m) return 2;
                    if (!double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) || !(gb > 0) || gb > 1_000_000)
                    {
                        Console.Error.WriteLine($"--max-size needs a size in GB above 0, not \"{SafeText.OneLine(m)}\", for example: --max-size 8");
                        return 2;
                    }
                    maxBytes = (long)(gb * 1_000_000_000d); break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine(a[i] is "--out" or "--force" or "--max-size"
                            ? $"{a[i]} is for ba2 extract; ba2 list only reads the archive."
                            : $"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    if (archive == null) archive = a[i];
                    else if (pattern == null) pattern = a[i];
                    else { Console.Error.WriteLine($"ba2 {mode} takes one archive and one pattern; got another: {SafeText.OneLine(a[i])}"); Console.Error.WriteLine(Usage); return 2; }
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(archive))
        {
            Console.Error.WriteLine($"ba2 {mode} needs the archive file, for example: sf-mod-check ba2 {mode} \"MyMod - Main.ba2\"{(mode == "extract" ? " \"scripts\\*.pex\" --out extracted" : "")}");
            Console.Error.WriteLine(Usage);
            cantWhy = "no archive given";
            return 2;
        }
        if (mode == "extract" && string.IsNullOrWhiteSpace(pattern))
        {
            Console.Error.WriteLine("ba2 extract needs a file name or a glob after the archive, for example: sf-mod-check ba2 extract \"MyMod - Main.ba2\" \"scripts\\*.pex\" --out extracted");
            cantWhy = "no pattern given";
            return 2;
        }
        if (mode == "extract" && string.IsNullOrWhiteSpace(outDir))
        {
            Console.Error.WriteLine("ba2 extract needs --out <folder>, the folder to write the files to, for example: --out extracted");
            cantWhy = "no --out folder given";
            return 2;
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} ba2 {mode} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        if (!File.Exists(archive))
        {
            Console.Error.WriteLine($"ba2 {mode}: there is no archive file {SafeText.OneLine(archive)}. Give the path of the .ba2 file.");
            cantWhy = $"no archive file {SafeText.OneLine(Path.GetFileName(archive))}";
            return 2;
        }
        Index index;
        try { index = Read(archive); }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"ba2 {mode}: {SafeText.OneLine(e.Message)}");
            cantWhy = "the archive can't be read";
            return 2;
        }
        Log.Write($"{Path.GetFileName(archive)}: version {index.Version}, type {index.Type}, compression {(index.Method == 3 ? "LZ4" : "zlib")}, {index.Items.Count} files");
        var match = pattern == null ? null : Glob(pattern);
        var picked = index.Items.Where(it => match == null || match.IsMatch(it.Name)).ToList();
        Log.Write($"pattern {(pattern == null ? "(none)" : SafeText.OneLine(pattern))}: {picked.Count} of {index.Items.Count} files");
        resultPrinted = true;
        if (mode == "list")
        {
            foreach (var it in picked) Console.WriteLine($"{it.Size,14:N0}  {index.HowStored(it),-7}  {SafeText.OneLine(it.RawName)}");
            Console.WriteLine(picked.Count == 0
                ? $"RESULT: NOT FOUND (no file in {SafeText.OneLine(Path.GetFileName(archive))} matches {SafeText.OneLine(pattern)}; run ba2 list without a pattern to see every name)"
                : $"RESULT: {picked.Count:N0} of {index.Items.Count:N0} files listed ({index.Type})");
            return picked.Count == 0 ? 1 : 0;
        }
        if (index.Type != "GNRL")
        {
            Console.WriteLine($"RESULT: CANNOT CHECK ({SafeText.OneLine(Path.GetFileName(archive))} is a texture (DX10) archive; ba2 extract reads general archives only, because a texture's DDS header would have to be rebuilt. ba2 list shows its names.)");
            return 2;
        }
        if (picked.Count == 0)
        {
            Console.WriteLine($"RESULT: CANNOT CHECK (no file in {SafeText.OneLine(Path.GetFileName(archive))} matches {SafeText.OneLine(pattern)}; run ba2 list to see the names)");
            return 2;
        }
        return Extract(archive, index, picked, outDir!, force, maxBytes);
    }

    static int Extract(string archive, Index index, List<Item> picked, string outDir, bool force, long maxBytes)
    {
        string outFull;
        try
        {
            outFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
            Directory.CreateDirectory(outFull);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.WriteLine($"RESULT: CANNOT CHECK (can't use the --out folder {SafeText.OneLine(outDir)}: {SafeText.OneLine(e.Message)})");
            return 2;
        }
        var archiveFull = Path.GetFullPath(archive);
        int errors = 0;
        var safe = new List<(Item It, string Dest)>();
        foreach (var it in picked)
        {
            string? dest = null;
            var why = UnsafeReason(it.RawName) ?? Escapes(outFull, it.RawName, out dest);
            if (why == null && string.Equals(dest, archiveFull, StringComparison.OrdinalIgnoreCase)) why = "it would overwrite the archive being read";
            if (why != null)
            {
                Console.WriteLine($"ERROR: {SafeText.OneLine(it.RawName)}: refused, {why}");
                Log.Write($"refused {SafeText.OneLine(it.RawName)}: {why}");
                errors++;
                continue;
            }
            safe.Add((it, dest!));
        }
        long total = safe.Sum(s => s.It.Size);
        if (total > maxBytes)
        {
            Console.WriteLine($"RESULT: CANNOT CHECK (the {safe.Count:N0} matching files add up to {total:N0} bytes, more than --max-size allows ({maxBytes:N0} bytes); nothing was written. Use a narrower pattern or a larger --max-size.)");
            return 2;
        }
        int written = 0;
        long bytes = 0;
        using var f = File.OpenRead(archive);
        foreach (var (it, dest) in safe)
        {
            var why = WriteOne(f, index, it, outFull, dest, force);
            if (why != null)
            {
                Console.WriteLine($"ERROR: {SafeText.OneLine(it.RawName)}: {why}");
                Log.Write($"not written {SafeText.OneLine(it.RawName)}: {why}");
                errors++;
                continue;
            }
            Console.WriteLine($"wrote {SafeText.OneLine(Path.GetRelativePath(outFull, dest))} ({it.Size:N0} bytes)");
            Log.Write($"wrote {dest} ({it.Size} bytes, {index.HowStored(it)})");
            written++;
            bytes += it.Size;
        }
        Console.WriteLine($"RESULT: {(errors == 0 ? "OK" : "ERRORS")} (wrote {written:N0} of {picked.Count:N0} files, {bytes:N0} bytes, to {SafeText.OneLine(outFull)}{(errors > 0 ? $"; {errors:N0} not written, see the ERROR lines" : "")})");
        return errors == 0 ? 0 : 1;
    }

    /// <summary>Writes one entry; null when written, else why not. The file is made new (never through an existing
    /// file unless force, and never through a link); a partly written file is removed.</summary>
    static string? WriteOne(FileStream f, Index index, Item it, string outFull, string dest, bool force)
    {
        try
        {
            var dir = Path.GetDirectoryName(dest)!;
            if (LinkOnTheWay(outFull, dir) is { } link) return $"refused, the folder {SafeText.OneLine(link)} inside --out is a link (junction or symbolic link), which isn't followed";
            Directory.CreateDirectory(dir);
            if (LinkOnTheWay(outFull, dir) is { } link2) return $"refused, the folder {SafeText.OneLine(link2)} inside --out is a link (junction or symbolic link), which isn't followed";
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                if (!force) return "not written, the file already exists (use --force to overwrite)";
                var attrs = File.GetAttributes(dest);
                if (attrs.HasFlag(FileAttributes.Directory)) return "not written, a folder of that name exists";
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) return "refused, the existing file is a link, which isn't followed";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"not written ({SafeText.OneLine(e.Message)})";
        }
        var stored = it.Packed != 0 ? it.Packed : (ulong)it.Size;
        if (it.Offset > (ulong)f.Length || it.Offset + stored > (ulong)f.Length) return $"not written, its data at byte {it.Offset} runs past the end of the archive";
        if (it.Packed != 0 && (it.Packed > MaxPackedInMemory || it.Size > MaxPackedInMemory)) return $"not written, a compressed file larger than {MaxPackedInMemory:N0} bytes isn't unpacked";
        FileStream? o = null;
        try
        {
            o = new FileStream(dest, force ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
            f.Seek((long)it.Offset, SeekOrigin.Begin);
            string? why = null;
            if (it.Packed == 0) why = Copy(f, o, it.Size);
            else
            {
                var raw = new byte[it.Packed];
                f.ReadExactly(raw);
                if (index.Method == 3)
                {
                    var data = new byte[it.Size];
                    int got;
                    try { got = K4os.Compression.LZ4.LZ4Codec.Decode(raw, data); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException) { got = -1; }
                    if (got < 0) why = "not written, it won't unpack (LZ4: the data is damaged, or it unpacks to more than its record says)";
                    else if (got != it.Size) why = $"not written, it unpacks to {got:N0} bytes, not the {it.Size:N0} its record says";
                    else o.Write(data);
                }
                else
                {
                    try
                    {
                        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                        why = Copy(z, o, it.Size);
                        if (why == null && z.Read(new byte[1], 0, 1) != 0) why = $"not written, it unpacks to more than the {it.Size:N0} bytes its record says";
                    }
                    catch (InvalidDataException e) { why = $"not written, it won't unpack (zlib: {SafeText.OneLine(e.Message)})"; }
                }
            }
            o.Dispose();
            if (why != null) TryDelete(dest);
            return why;
        }
        catch (IOException e) when (!force && File.Exists(dest) && o == null)
        {
            return $"not written, the file already exists (use --force to overwrite; {SafeText.OneLine(e.Message)})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            o?.Dispose();
            if (o != null) TryDelete(dest);
            return $"not written ({SafeText.OneLine(e.Message)})";
        }
    }

    static string? Copy(Stream from, Stream to, long size)
    {
        var buf = new byte[1 << 16];
        long left = size;
        while (left > 0)
        {
            int n = from.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) return $"not written, it holds less than the {size:N0} bytes its record says";
            to.Write(buf, 0, n);
            left -= n;
        }
        return null;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"couldn't remove the partly written {path}: {e.Message}"); }
    }

    /// <summary>The first existing folder from outFull's children down to dir that is a link, or null.</summary>
    static string? LinkOnTheWay(string outFull, string dir)
    {
        var rel = Path.GetRelativePath(outFull, dir);
        if (rel == ".") return null;
        var at = outFull;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar))
        {
            at = Path.Combine(at, part);
            if (!Directory.Exists(at)) return null;
            if (new DirectoryInfo(at).Attributes.HasFlag(FileAttributes.ReparsePoint)) return Path.GetRelativePath(outFull, at);
        }
        return null;
    }

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Why an archive's file name can't be written under --out, or null when it can. Checked on the name as
    /// the archive stores it, on every system (an archive made on one is extracted on another).</summary>
    public static string? UnsafeReason(string raw)
    {
        if (raw.Length == 0) return "it has an empty name";
        if (raw.Any(c => c < 0x20 || c == 0x7F)) return "its name has a control character";
        if (raw.Contains(':')) return "its name has a ':' (a drive or a stream)";
        if (raw[0] is '\\' or '/') return "its name starts at the root of a drive or a network path";
        foreach (var part in raw.Split('\\', '/'))
        {
            if (part.Length == 0) return "its name has an empty folder part";
            if (part is "." or "..") return "its name climbs out of the folder (. or ..)";
            if (part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return "its name has a character Windows doesn't allow (< > \" | ? *)";
            if (part.EndsWith('.') || part.EndsWith(' ')) return "a part of its name ends in a dot or a space, which Windows drops";
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) return $"its name uses the reserved Windows device name {stem}";
        }
        return null;
    }

    /// <summary>The file under outFull that name goes to; returns why not when the full path isn't inside outFull.</summary>
    static string? Escapes(string outFull, string raw, out string? dest)
    {
        dest = null;
        try
        {
            var p = Path.GetFullPath(Path.Combine(outFull, raw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (!p.StartsWith(outFull + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return "its name leads outside the --out folder";
            dest = p;
            return null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"its name isn't a usable path ({SafeText.OneLine(e.Message)})";
        }
    }

    /// <summary>A glob over names as Ba2Names gives them: * any characters (folders included), ? one; case-insensitive,
    /// / the same as \.</summary>
    public static Regex Glob(string pattern)
    {
        var p = Ba2Names.Normalize(pattern);
        var sb = new StringBuilder("^");
        foreach (var c in p) sb.Append(c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) });
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    /// <summary>The archive's header and file list. GNRL records are 36 bytes (offset at 16, packed size at 24, size
    /// at 28); DX10 records are 24 bytes (chunk count at 13, chunk header size at 14) plus their chunks (unpacked size
    /// at 12). The header is 24 bytes, plus 8 in version 2 and 12 in version 3, whose last 4 name the compression
    /// (3 = LZ4 block, else zlib). Throws InvalidDataException, naming the file, when they don't add up.</summary>
    public static Index Read(string path)
    {
        var file = Path.GetFileName(path);
        using var f = File.OpenRead(path);
        var head = new byte[36];
        if (f.ReadAtLeast(head.AsSpan(0, 24), 24, throwOnEndOfStream: false) < 24 || Encoding.ASCII.GetString(head, 0, 4) != "BTDX")
            throw new InvalidDataException($"{file} is not a BA2 archive (no BTDX header)");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        var type = Encoding.ASCII.GetString(head, 8, 4);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        var nameOfs = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
        if (type != "GNRL" && type != "DX10") throw new InvalidDataException($"{file}: unsupported archive type \"{SafeText.OneLine(type)}\" (expected GNRL or DX10)");
        int extra = version switch { 2 => 8, 3 => 12, _ => 0 };
        uint method = 0;
        if (extra > 0)
        {
            if (f.ReadAtLeast(head.AsMemory(24, extra).Span, extra, throwOnEndOfStream: false) < extra) throw new InvalidDataException($"{file}: its header is cut short");
            if (version == 3) method = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32));
        }
        if (nameOfs == 0 && count > 0) throw new InvalidDataException($"{file} has no name table");
        if (nameOfs > (ulong)f.Length) throw new InvalidDataException($"{file}: its name table offset {nameOfs} is past the end of the file ({f.Length} bytes)");
        long recStart = 24 + extra;
        if (count > 0 && (long)nameOfs < recStart) throw new InvalidDataException($"{file}: its name table starts inside the header");
        long names = f.Length - (long)nameOfs;
        if (count > names / 2) throw new InvalidDataException($"{file}: its header says {count} files, more than its name table can hold");
        // names
        if (names > int.MaxValue) throw new InvalidDataException($"{file}: its name table is too large ({names} bytes)");
        var raw = new List<string>((int)count);
        if (count > 0)
        {
            f.Seek((long)nameOfs, SeekOrigin.Begin);
            var data = new byte[names];
            f.ReadExactly(data);
            int pos = 0;
            for (uint i = 0; i < count; i++)
            {
                if (pos + 2 > data.Length) throw new InvalidDataException($"{file}: its name table ends after {i} of {count} names");
                int n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
                pos += 2;
                if (pos + n > data.Length) throw new InvalidDataException($"{file}: name {i + 1} of {count} runs past the end of the file");
                raw.Add(Encoding.UTF8.GetString(data, pos, n));
                pos += n;
            }
        }
        // records
        var items = new List<Item>(raw.Count);
        f.Seek(recStart, SeekOrigin.Begin);
        var buf = new byte[36];
        void Take(int n, string what)
        {
            if (f.Position + n > (long)nameOfs) throw new InvalidDataException($"{file}: {what} run into the name table");
            f.ReadExactly(buf, 0, n);
        }
        for (int i = 0; i < raw.Count; i++)
        {
            if (type == "GNRL")
            {
                Take(36, $"its file records (entry {i + 1} of {raw.Count})");
                items.Add(new Item(raw[i], Ba2Names.Normalize(raw[i]), BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(16)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(24)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(28))));
                continue;
            }
            Take(24, $"its texture records (entry {i + 1} of {raw.Count})");
            int chunks = buf[13];
            int chunkHdr = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(14));
            if (chunkHdr == 0) chunkHdr = 24;
            if (chunkHdr < 16) throw new InvalidDataException($"{file}: entry {i + 1} has a chunk header size of {chunkHdr} bytes, too small to hold its sizes");
            long size = 0;
            for (int k = 0; k < chunks; k++)
            {
                Take(16, $"the chunks of entry {i + 1} of {raw.Count}");
                size += BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12));
                if (f.Position - 16 + chunkHdr > (long)nameOfs) throw new InvalidDataException($"{file}: the chunks of entry {i + 1} of {raw.Count} run into the name table");
                f.Seek(chunkHdr - 16, SeekOrigin.Current);
            }
            items.Add(new Item(raw[i], Ba2Names.Normalize(raw[i]), 0, 0, size));
        }
        return new Index(version, type, method, items);
    }
}
