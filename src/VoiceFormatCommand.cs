// sf-mod-check voice-format (0.23.0): a report of a plugin's voice files: how many .wem files there are of each codec,
// channel count and sample rate (read from each file's RIFF "fmt " chunk), and whether each one's lip file (.ffxanim,
// same path) is in an archive or loose. Design: StarfieldPluginCheck NOTES.md, "0.23.0 (SmcLookup)", "Step 3".
//   sf-mod-check voice-format <plugin | archive.ba2 | folder>... [--quiet] [--verbose] [--log <file>]
// It is a report, never a rule: no format is flagged. The game ships mono Wwise Vorbis voice lines at 32, 44.1 and
// 48 kHz (sf-mod-build NOTES.md, "Voice sample rates in the game's own files"), so every rate is just counted.
// Inputs: a plugin reads its own archives ("<plugin name> - *.ba2" in its folder) and its loose sound\voice\<plugin>\
// files; an archive reads its .wem files, and finds lip files in it and its plugin's other archives (the same
// "<name> - " start) and loose beside the .wem; a folder reads the loose .wem files under it (under its sound\voice\
// when it has one). Only the first 4 KB of a stored file is read; a compressed entry is unpacked in memory (at most
// MaxEntryBytes). Names are untrusted: they are only printed (one line each), never used as paths, except loose
// files, which are found by listing the folder (folder links are not followed).
// Exit 0 = report written (some sources may be unreadable, each named), 2 = could not run (no input, or none readable).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SfModCheck;

public static class VoiceFormatCommand
{
    public const string Usage =
        "usage: sf-mod-check voice-format <plugin | archive.ba2 | folder>... [--quiet] [--verbose] [--log <file>]\n" +
        "  Reports a mod's voice files (.wem): how many there are of each codec, channel count and sample rate, read from\n" +
        "  each file's header, and how many have their lip file (.ffxanim) in an archive, loose, or not at all.\n" +
        "  A report, not a check: no format is flagged (the game itself ships voice lines at 32, 44.1 and 48 kHz).\n" +
        "  <plugin>   reads the plugin's archives (\"<plugin name> - *.ba2\" in its folder) and loose sound\\voice\\<plugin>\\\n" +
        "  <archive>  reads one .ba2; lip files are looked for in it and the other archives of its plugin\n" +
        "  <folder>   reads the loose .wem files under the folder (its sound\\voice\\ folder when it has one)\n" +
        "Exit 0 = report written, 2 = could not run (no input, or no input could be read).";

    /// <summary>The most of one .wem read for its header.</summary>
    public const int HeadBytes = 4096;

    /// <summary>A compressed archive entry larger than this is not unpacked (counted as unreadable).</summary>
    public const long MaxEntryBytes = 64L << 20;

    /// <summary>A .wem's format: the RIFF fmt chunk's format tag, channel count and sample rate.</summary>
    public readonly record struct WemFormat(ushort Tag, int Channels, uint Rate)
    {
        public string Codec => Tag switch
        {
            0xFFFF => "wwise-vorbis",
            0xFFFE => "pcm (extensible)",
            0x0001 => "pcm",
            0x0002 => "adpcm",
            0x3040 or 0x3041 => "opus",
            _ => $"0x{Tag:x4}",
        };
        public string ChannelText => Channels switch { 1 => "mono", 2 => "stereo", _ => $"{Channels} channels" };
    }

    /// <summary>The format in a .wem's first bytes, or null with the reason. RIFF is little-endian, RIFX big-endian.</summary>
    public static WemFormat? ReadFormat(ReadOnlySpan<byte> head, out string? why)
    {
        why = null;
        if (head.Length < 12) { why = "it is shorter than a RIFF header"; return null; }
        bool big = head[..4].SequenceEqual("RIFX"u8);
        if (!big && !head[..4].SequenceEqual("RIFF"u8)) { why = "it is not a RIFF file"; return null; }
        if (!head[8..12].SequenceEqual("WAVE"u8)) { why = "its RIFF type is not WAVE"; return null; }
        uint U32(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        ushort U16(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
        long i = 12;
        while (i + 8 <= head.Length)
        {
            var id = head.Slice((int)i, 4);
            uint size = U32(head.Slice((int)i + 4, 4));
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 8 || i + 16 > head.Length) { why = "its fmt chunk is too short"; return null; }
                var f = new WemFormat(U16(head.Slice((int)i + 8, 2)), U16(head.Slice((int)i + 10, 2)), U32(head.Slice((int)i + 12, 4)));
                if (f.Channels == 0 || f.Rate == 0) { why = "its fmt chunk says 0 channels or a sample rate of 0"; return null; }
                return f;
            }
            i += 8L + size + (size & 1);
        }
        why = $"it has no fmt chunk in its first {Math.Min(head.Length, HeadBytes):N0} bytes";
        return null;
    }

    /// <summary>One source's report.</summary>
    internal sealed class Report
    {
        public required string Label { get; init; }
        public readonly Dictionary<WemFormat, (int Count, string Example)> Formats = new();
        public readonly List<string> NotRead = new();          // archives or folders that couldn't be read, with why
        public readonly List<(string Name, string Why)> Unreadable = new();
        public int Wems, Compressed, LipInArchive, LipLoose;
        public readonly List<string> NoLip = new();
        public int ArchivesRead, LooseFiles;
    }

    internal sealed record Source(string Label, List<string> Archives, List<string> LipArchives, string? LooseRoot, string? LooseVoice);

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
        var inputs = new List<string>();
        string? logFile = null;
        bool quiet = false, verbose = false;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a[i].StartsWith("--")) { Console.Error.WriteLine($"unknown option {a[i]}"); Console.Error.WriteLine(Usage); return 2; }
                    inputs.Add(a[i]); break;
            }
        }
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("voice-format needs a plugin, an archive or a folder, for example: sf-mod-check voice-format \"MyMod.esm\"");
            Console.Error.WriteLine(Usage);
            cantWhy = "no input given";
            return 2;
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} voice-format on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        var sources = new List<Source>();
        foreach (var input in inputs)
        {
            var s = SourceOf(input, out var why);
            if (s == null) { Console.Error.WriteLine($"voice-format: {SafeText.OneLine(why)}"); cantWhy = SafeText.OneLine(why)!; return 2; }
            sources.Add(s);
        }
        resultPrinted = true;
        int read = 0, total = 0, noLip = 0, unreadable = 0;
        var formats = new HashSet<WemFormat>();
        foreach (var s in sources)
        {
            var sw = Stopwatch.StartNew();
            var r = Build(s);
            Log.Write($"voice-format: {s.Label}: {r.Wems} .wem in {r.ArchivesRead} archives and {r.LooseFiles} loose files, {sw.Elapsed.TotalSeconds:F1} s");
            Print(r);
            if (r.ArchivesRead > 0 || s.LooseVoice != null || r.NotRead.Count == 0) read++;
            total += r.Wems; noLip += r.NoLip.Count; unreadable += r.Unreadable.Count;
            formats.UnionWith(r.Formats.Keys);
        }
        if (read == 0)
        {
            Console.WriteLine($"RESULT: CANNOT CHECK (no input could be read; see the lines above)");
            return 2;
        }
        Console.WriteLine($"RESULT: REPORT ({total:N0} voice {(total == 1 ? "file" : "files")} in {sources.Count} {(sources.Count == 1 ? "input" : "inputs")}, " +
                          $"{formats.Count} {(formats.Count == 1 ? "format" : "formats")}, {noLip:N0} with no lip file, {unreadable:N0} unreadable)");
        return 0;
    }

    static readonly string[] PluginExts = [".esm", ".esp", ".esl"];

    /// <summary>What one input reads; null with the reason when it isn't a plugin, archive or folder that exists.</summary>
    static Source? SourceOf(string input, out string why)
    {
        why = "";
        if (Directory.Exists(input))
        {
            var root = Path.GetFullPath(input);
            var voice = CiDir(root, "sound", "voice") ?? root;
            return new Source($"{Path.GetFileName(root.TrimEnd('\\', '/'))} (folder)", [], [], root, voice);
        }
        if (!File.Exists(input)) { why = $"there is no file or folder {input}. Give a plugin, a .ba2 archive or a folder."; return null; }
        var full = Path.GetFullPath(input);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext == ".ba2")
        {
            var cut = name.IndexOf(" - ", StringComparison.Ordinal);
            var siblings = cut > 0 ? ArchivesStarting(dir, name[..(cut + 3)]) : [full];
            if (!siblings.Contains(full, StringComparer.OrdinalIgnoreCase)) siblings.Add(full);
            return new Source($"{name} (archive)", [full], siblings, dir, null);
        }
        if (PluginExts.Contains(ext))
        {
            var own = ArchivesStarting(dir, Path.GetFileNameWithoutExtension(name) + " - ");
            return new Source($"{name} (plugin, {own.Count} {(own.Count == 1 ? "archive" : "archives")})", own, own, dir, CiDir(dir, "sound", "voice", name));
        }
        why = $"{name} is not a plugin (.esm, .esp, .esl), a .ba2 archive or a folder";
        return null;
    }

    static List<string> ArchivesStarting(string dir, string start)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.ba2").Where(f => Path.GetFileName(f).StartsWith(start, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"voice-format: {dir} not listed ({e.Message})"); return []; }
    }

    static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive,
    };

    internal static Report Build(Source s)
    {
        var r = new Report { Label = s.Label };
        var lips = new HashSet<string>(StringComparer.Ordinal);
        var wems = new List<(string Name, Func<byte[]> Head)>();
        var opened = new List<FileStream>();
        try
        {
            foreach (var path in s.LipArchives)
            {
                Ba2Command.Index index;
                try { index = Ba2Command.Read(path); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // Ba2Command.Read's messages already name the archive
                    var file = Path.GetFileName(path);
                    r.NotRead.Add(e.Message.StartsWith(file, StringComparison.OrdinalIgnoreCase) ? e.Message : $"{file}: {e.Message}");
                    continue;
                }
                foreach (var it in index.Items) if (it.Name.EndsWith(".ffxanim", StringComparison.Ordinal)) lips.Add(it.Name);
                if (!s.Archives.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                r.ArchivesRead++;
                if (index.Type != "GNRL") continue;
                FileStream f;
                try { f = File.OpenRead(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r.NotRead.Add($"{Path.GetFileName(path)}: {e.Message}"); continue; }
                opened.Add(f);
                int n = 0;
                foreach (var it in index.Items.Where(it => it.Name.StartsWith(@"sound\voice\", StringComparison.Ordinal) && it.Name.EndsWith(".wem", StringComparison.Ordinal))
                             .OrderBy(it => it.Offset))
                {
                    n++;
                    if (it.Packed != 0) r.Compressed++;
                    var (file, idx, item) = (f, index, it);
                    wems.Add((it.Name, () => ArchiveHead(file, idx, item)));
                }
                Log.Write($"voice-format: {Path.GetFileName(path)}: {index.Items.Count} files, {n} voice .wem");
            }
            var looseLips = new HashSet<string>(StringComparer.Ordinal);
            if (s.LooseVoice != null && s.LooseRoot != null)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(s.LooseVoice, "*", Walk))
                    {
                        var rel = Ba2Names.Normalize(Path.GetRelativePath(s.LooseRoot, file));
                        if (rel.EndsWith(".ffxanim", StringComparison.Ordinal)) looseLips.Add(rel);
                        else if (rel.EndsWith(".wem", StringComparison.Ordinal)) { r.LooseFiles++; var p = file; wems.Add((rel, () => LooseHead(p))); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r.NotRead.Add($"{s.LooseVoice}: not listed in full ({e.Message})"); }
            }
            foreach (var (name, head) in wems)
            {
                r.Wems++;
                var lip = name[..^4] + ".ffxanim";
                if (looseLips.Contains(lip)) r.LipLoose++;
                else if (lips.Contains(lip)) r.LipInArchive++;
                else r.NoLip.Add(name);
                byte[] bytes;
                try { bytes = head(); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { r.Unreadable.Add((name, e.Message)); continue; }
                if (ReadFormat(bytes, out var why) is not { } fmt) { r.Unreadable.Add((name, why!)); continue; }
                r.Formats[fmt] = r.Formats.TryGetValue(fmt, out var had) ? (had.Count + 1, had.Example) : (1, name);
            }
        }
        finally { foreach (var f in opened) f.Dispose(); }
        return r;
    }

    static byte[] ArchiveHead(FileStream f, Ba2Command.Index index, Ba2Command.Item it)
    {
        if (it.Packed != 0)
        {
            var all = Ba2Command.EntryBytes(f, index, it, MaxEntryBytes);
            return all.Length > HeadBytes ? all[..HeadBytes] : all;
        }
        if (it.Offset + (ulong)it.Size > (ulong)f.Length) throw new InvalidDataException($"its data at byte {it.Offset} runs past the end of the archive");
        var head = new byte[Math.Min(it.Size, HeadBytes)];
        f.Seek((long)it.Offset, SeekOrigin.Begin);
        f.ReadExactly(head);
        return head;
    }

    static byte[] LooseHead(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[(int)Math.Min(f.Length, HeadBytes)];
        f.ReadExactly(head);
        return head;
    }

    static void Print(Report r)
    {
        var o = new StringBuilder();
        o.AppendLine($"{SafeText.OneLine(r.Label)}: {r.Wems:N0} voice {(r.Wems == 1 ? "file" : "files")} (.wem)");
        foreach (var n in r.NotRead) o.AppendLine($"  not read: {SafeText.OneLine(n)}");
        foreach (var (f, (count, example)) in r.Formats.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key.Rate))
            o.AppendLine($"  {f.Codec,-16} {f.ChannelText,-10} {f.Rate,7:N0} Hz  {count,8:N0}   e.g. {SafeText.OneLine(example)}");
        if (r.Wems > 0)
        {
            o.AppendLine($"  lip files (.ffxanim): {r.LipInArchive:N0} in an archive, {r.LipLoose:N0} loose, {r.NoLip.Count:N0} with none" +
                         (r.NoLip.Count > 0 ? $" (first: {SafeText.OneLine(r.NoLip[0])}; those lines play, but the lips don't move)" : ""));
            if (r.Compressed > 0) o.AppendLine($"  {r.Compressed:N0} stored compressed in the archive (release reports that as voice-compressed)");
        }
        if (r.Unreadable.Count > 0)
            o.AppendLine($"  unreadable: {r.Unreadable.Count:N0} (first: {SafeText.OneLine(r.Unreadable[0].Name)}: {SafeText.OneLine(r.Unreadable[0].Why)})");
        foreach (var (name, why) in r.Unreadable.Skip(1)) Log.Write($"voice-format: unreadable {name}: {why}");
        foreach (var name in r.NoLip.Skip(1)) Log.Write($"voice-format: no lip file for {name}");
        Console.Write(o.ToString());
    }

    static string? CiDir(string root, params string[] parts) => VoiceFileIndex.CiDir(root, parts);
}
