// sfmodcheck release: the optional Papyrus lint (0.7.0, chunk C4), a port of papyrus_lint and vanilla_psc in our
// the author's earlier Python release check. It runs only with both --papyrus <papyrus.exe> (papyrus-tools) and --ck-sources <the Creation
// Kit's Tools\ContentResources.zip>, on the .psc files each zip ships under Scripts\Source. Every finding is a WARNING
// (the lint never fails a release); a NOTE gives papyrus's own summary line.
// The vanilla sources (Scripts\Source\*.psc and *.flg in ContentResources.zip) are extracted once per zip version (size
// and modified time) into <LearnedCache.Root>\vanilla-psc\<size>-<unix time>, through a ".partial" folder renamed when
// complete, so a run that stops halfway never leaves a cache that looks finished.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SfModCheck;

public static class ReleaseLint
{
    const string Rule = "papyrus-lint";

    // papyrus.exe is pinned to the build we tested (papyrus-tools v0.16.3, commit 9636a64), because this step reads its
    // output: the JSON field names (severity, code, name, path, line, message), the lint names turned off below and the
    // summary line on stderr all belong to that version. Another build may rename a field or a lint, and then findings
    // would be lost or misread without a word; refusing it makes the gap visible.
    public const string PapyrusSha256 = "954ec6d7559713c8cdf6aaaf20bde5d9feedf52a6404a90cfca5b2107559c4fa";
    public const string PapyrusVersion = "v0.16.3";

    /// <summary>Lints turned off: naming style. Bethesda's own scripts use PascalCase, and renaming a property breaks
    /// the plugin's bindings to it.</summary>
    public static readonly string[] LintsOff = ["naming/locals", "naming/variables", "naming/functions", "names/case"];

    public static int TimeoutSeconds = 600;

    static ReleaseFinding Warn(string text) => new(Level.Warning, Rule, text, RuleKind.Engine);
    static ReleaseFinding Note(string text) => new(Level.Note, Rule, text, RuleKind.Engine);

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        var o = c.Options;
        if (o.Papyrus == null || o.CkSources == null) yield break;   // ReleaseCommand prints a note when only one is given

        // ---- the mods' sources ----
        var roots = new List<string>();
        foreach (var p in c.Packages)
        {
            var src = FindSourceDir(p.Root);
            if (src != null && Directory.EnumerateFiles(src, "*.psc", SearchOption.AllDirectories).Any())
            {
                roots.Add(src);
                Log.Write($"papyrus lint: sources in {p.Name}: {src}");
            }
            else yield return Note($"no script sources in {p.Name}; Papyrus lint skipped for it");
        }
        if (roots.Count == 0) yield break;

        // ---- papyrus.exe ----
        var exe = Path.GetFullPath(o.Papyrus);
        string digest;
        try { digest = Sha256(exe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            digest = "";
            Log.Write($"papyrus lint: can't read {exe}: {e.Message}");
        }
        if (!digest.Equals(PapyrusSha256, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn(digest.Length == 0
                ? $"Papyrus lint skipped: can't read {exe}. Check the --papyrus path."
                : $"Papyrus lint skipped: {exe} isn't the tested papyrus-tools {PapyrusVersion} build (SHA-256 {digest[..12]}..., " +
                  $"wanted {PapyrusSha256[..12]}...). Give --papyrus the {PapyrusVersion} papyrus.exe.");
            yield break;
        }

        // ---- vanilla sources ----
        string vanilla;
        string? vanillaError = null;
        try { vanilla = VanillaSources(Path.GetFullPath(o.CkSources)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            vanilla = "";
            vanillaError = e.Message;
        }
        if (vanillaError != null)
        {
            yield return Warn($"Papyrus lint skipped: can't use the vanilla sources in {Path.GetFullPath(o.CkSources)}: {vanillaError}");
            yield break;
        }

        // ---- the papyrus project ----
        var proj = Path.Combine(c.Work, "papyrus-lint");
        Directory.CreateDirectory(proj);
        string? setupError = null;
        try
        {
            for (int i = 0; i < roots.Count; i++) CopySources(roots[i], Path.Combine(proj, $"Source{i}"));
            File.WriteAllText(Path.Combine(proj, "papyrus.toml"), Toml(vanilla, roots.Count), new UTF8Encoding(false));
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            setupError = e.Message;
        }
        if (setupError != null)
        {
            yield return Warn($"Papyrus lint skipped: can't copy the script sources to its work folder: {setupError}");
            yield break;
        }
        Log.Write($"papyrus lint: running {exe} check in {proj} (imports {vanilla})");

        int exit = 0; string stdout = "", stderr = ""; bool timedOut = false; string? startError = null;
        try { (exit, stdout, stderr, timedOut) = Exec(exe, proj); }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            startError = e.Message;
        }
        if (startError != null)
        {
            yield return Warn($"Papyrus lint skipped: {exe} could not be started ({startError}).");
            yield break;
        }
        if (timedOut)
        {
            yield return Warn($"Papyrus lint skipped: {exe} didn't finish in {TimeoutSeconds} s.");
            yield break;
        }
        Log.Write($"papyrus lint: exit {exit}; stderr: {stderr.Trim()}");
        var lines = Lines(stdout, proj);
        if (lines == null)
        {
            yield return Warn($"papyrus lint: unreadable output (exit {exit}): {Head(stderr.Trim(), 300)}");
            yield break;
        }
        foreach (var l in lines) yield return Warn(l);
        var err = stderr.Trim();
        yield return Note($"papyrus lint: {(err.Length > 0 ? err.Replace("\r", "").Split('\n')[^1] : "ran")}");
    }

    static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>The package's Scripts\Source folder, matched without regard to case (Linux file names are
    /// case-sensitive and zips spell it either way). Null if there is none.</summary>
    static string? FindSourceDir(string root)
    {
        string? Child(string dir, string name) =>
            Directory.Exists(dir)
                ? Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase))
                : null;
        var scripts = Child(root, "scripts");
        return scripts == null ? null : Child(scripts, "source");
    }

    /// <summary>Copies a source tree without compiled .pex files, as the earlier Python check does.</summary>
    static void CopySources(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    static readonly JsonSerializerOptions TomlString = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>papyrus.toml as the earlier Python check writes it. A JSON string is a valid TOML basic string.</summary>
    public static string Toml(string vanilla, int sources)
    {
        var toml = new List<string>
        {
            "game = \"starfield\"",
            "include-fragments = true",
            "imports = [" + JsonSerializer.Serialize(vanilla.Replace('\\', '/'), TomlString) + "]",
            "sources = [" + string.Join(", ", Enumerable.Range(0, sources).Select(i => $"\"Source{i}\"")) + "]",
            "",
            "[lints]",
        };
        toml.AddRange(LintsOff.Select(n => $"\"{n}\" = \"allow\""));
        return string.Join("\n", toml) + "\n";
    }

    static (int Exit, string Stdout, string Stderr, bool TimedOut) Exec(string exe, string cwd)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "check", "--message-format", "json", "." }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException($"can't start {exe}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return (-1, "", "", true);
        }
        p.WaitForExit();
        return (p.ExitCode, outTask.Result, errTask.Result, false);
    }

    /// <summary>Turns papyrus's JSON findings into the earlier Python check's lines:
    /// "papyrus SEVERITY CODE NAME: PATH:LINE: MESSAGE  [papyrus explain CODE]", the path relative to the copied source
    /// folder (the SourceN folder dropped). Null when the output isn't a JSON list of findings.</summary>
    public static List<string>? Lines(string stdout, string proj)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(stdout); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var lines = new List<string>();
            var root = Path.GetFullPath(proj).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) return null;
                string Get(string k, string dflt) =>
                    !f.TryGetProperty(k, out var v) ? dflt
                    : v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt
                    : v.ValueKind == JsonValueKind.Null ? "None"
                    : v.GetRawText();
                var code = Get("code", "");
                lines.Add($"papyrus {Get("severity", "?")} {code} {Get("name", "")}: " +
                          $"{RelPath(Get("path", "?"), root)}:{Get("line", "?")}: {Get("message", "")}" +
                          $"  [papyrus explain {code}]");
            }
            return lines;
        }
    }

    static string RelPath(string path, string root)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;
        var parts = full[root.Length..].Split('\\', '/').Where(s => s.Length > 0).ToArray();
        return parts.Length <= 1 ? "." : string.Join(Path.DirectorySeparatorChar, parts.Skip(1));   // drop SourceN
    }

    static readonly Regex VanillaEntry = new(@"^Scripts[/\\]Source[/\\](.+\.(?:psc|flg))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The folder of vanilla Papyrus sources from the Creation Kit's ContentResources.zip, extracted once per
    /// zip version (size and modified time) under LearnedCache.Root. Throws InvalidDataException when an entry would
    /// land outside the folder or the zip holds no Scripts\Source\Quest.psc.</summary>
    public static string VanillaSources(string ckZip)
    {
        var fi = new FileInfo(ckZip);
        var cacheDir = Path.Combine(LearnedCache.Root, "vanilla-psc");
        var dest = Path.Combine(cacheDir, $"{fi.Length}-{new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()}");
        if (File.Exists(Path.Combine(dest, "Quest.psc")))
        {
            Log.Write($"papyrus lint: vanilla sources cached in {dest}");
            return dest;
        }
        Console.Error.WriteLine($"sf-mod-check: extracting the vanilla Papyrus sources from {ckZip} (once; cached in {cacheDir})");
        // remove what runs that were stopped left behind (a day old or more, so a run in progress is never touched)
        try
        {
            foreach (var old in Directory.EnumerateDirectories(cacheDir, Path.GetFileName(dest) + ".partial*"))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(old) > TimeSpan.FromDays(1)) Directory.Delete(old, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var tmp = dest + $".partial-{Environment.ProcessId}";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var tmpRoot = Path.GetFullPath(tmp).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int n = 0;
        try
        {
            using (var z = ZipFile.OpenRead(ckZip))
            {
                foreach (var e in z.Entries)
                {
                    var m = VanillaEntry.Match(e.FullName);
                    if (!m.Success) continue;
                    var rel = m.Groups[1].Value.Replace('\\', '/');
                    if (rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Any(s => s == ".."))
                        throw new InvalidDataException($"the entry \"{e.FullName}\" points outside the Scripts\\Source folder");
                    var target = Path.GetFullPath(Path.Combine(tmpRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(tmpRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"the entry \"{e.FullName}\" points outside the Scripts\\Source folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                    n++;
                }
            }
            if (!File.Exists(Path.Combine(tmp, "Quest.psc")))
                throw new InvalidDataException("it holds no Scripts\\Source\\Quest.psc (give --ck-sources the Creation Kit's Tools\\ContentResources.zip)");
        }
        catch
        {
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        if (File.Exists(Path.Combine(dest, "Quest.psc")))
        {
            // another run finished first: use its copy
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return dest;
        }
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(tmp, dest);
        Log.Write($"papyrus lint: extracted {n} vanilla sources to {dest}");
        return dest;
    }
}
