// The troubleshooting log (0.4.0 step 6, 2026-11-09): what sfmodcheck read, where it found each master, which caches
// it used or rebuilt, how long each stage took, and the full error when it fails. Off unless asked for:
//   --verbose      log lines on stderr, each starting "log: "
//   --log <file>   the same lines, with times, written to that file
// It never writes to stdout, so the ERROR / WARNING / HINT / RESULT lines are the same with or without it.
// 0.4.1: a log file that stops working is closed with a note and the check carries on; --log refuses plugin names.
using System.Diagnostics;
using System.Text;

namespace SfModCheck;

public static class Log
{
    static bool toStderr;
    static StreamWriter? file;
    static string filePath = "";
    static readonly Stopwatch clock = Stopwatch.StartNew();

    public static bool On => toStderr || file != null;

    public static void ToStderr() => toStderr = true;

    /// <summary>Opens the log file (overwrites it). Returns an error text naming the file and the fix, or null.
    /// Refuses a plugin or archive name (.esm .esp .esl .ba2) and an existing file that starts like a plugin, so a
    /// mistyped --log can't overwrite a mod.</summary>
    public static string? ToFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (dir != null && !Directory.Exists(dir))
                return $"can't write the log file {full}: the folder {dir} doesn't exist. Make the folder, or give --log a path in a folder that exists.";
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is ".esm" or ".esp" or ".esl" or ".ba2" || LooksLikePlugin(full))
                return $"won't write the log to {full}: that is a plugin or archive, and the log would overwrite it. " +
                       "Give --log a .txt file, for example --log sfmodcheck-log.txt";
            file = new StreamWriter(full, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            filePath = full;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"can't write the log file {path}: {e.Message} Give --log a path you can write to, such as a file in your Documents folder.";
        }
    }

    static bool LooksLikePlugin(string full)
    {
        try
        {
            if (!File.Exists(full)) return false;
            using var fs = File.OpenRead(full);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 && b[0] == 'T' && b[1] == 'E' && b[2] == 'S' && b[3] == '4';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Writes a log line. A log file that stops working (disk full, network share gone) is closed with one
    /// note on stderr, and the check carries on, so its result is the same with or without the log.</summary>
    public static void Write(string text)
    {
        if (!On) return;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (toStderr) Console.Error.WriteLine($"log: {line}");
            if (file == null) continue;
            try { file.WriteLine($"{clock.Elapsed.TotalSeconds,8:F2}s  {line}"); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                var f = file;
                file = null;
                try { f.Dispose(); } catch (Exception) { /* already broken */ }
                Console.Error.WriteLine($"note: stopped writing the log file {filePath}: {e.Message} The check carries on.");
            }
        }
    }

    /// <summary>A file's size and modified time (UTC), for the log.</summary>
    public static string Describe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.FullName} ({fi.Length:N0} bytes, modified {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC)" : $"{path} (not found)";
        }
        catch (Exception e) { return $"{path} ({e.GetType().Name}: {e.Message})"; }
    }

    public static void Close()
    {
        try { file?.Dispose(); } catch (Exception) { /* closing a broken log must not change the exit code */ }
        file = null;
    }
}
