// sfmodcheck release --tested and --stage (0.15.0, plan step 6; NOTES "### 0.15.0 design", parts 2 and 3).
//   --tested <zip|ba2> (repeatable): the build that was tested in game. The new release's scripts (loose .pex and the
//     .pex in its general archives) are compared with the tested build's by meaning (Pex.Listing, a port of
//     sf-pex-tools' pex_compare.py listing), its plugins byte for byte with the tested plugin of the same file name.
//   --stage <dir>: the folder the release was built from (maps to Data). Its files are checked against the names in the
//     release's archives: a staged file in no archive (and not shipped loose) is a PROBLEM, since the game won't see it.
// Read only: a tested zip's archives are copied into the release's work folder under made-up names, nothing else is
// written. The levels are package-check's: REVIEW for differences (never a failure), PROBLEM for what can't be read
// and for staged files the archives lack.
using System.IO.Compression;
using System.Security.Cryptography;

namespace SfModCheck;

public static class ReleaseTested
{
    public const string TestedRule = "tested", StageRule = "stage";

    static readonly string[] PluginExts = [".esm", ".esp", ".esl"];

    static bool IsPlugin(string norm) => PluginExts.Any(e => norm.EndsWith(e, StringComparison.Ordinal));

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        var findings = new List<ReleaseFinding>();
        if (c.Options.Tested.Count > 0) Tested(c, findings);
        if (c.Options.Stage != null) Stage(c, findings);
        return findings;
    }

    // ---------------------------------------------------------------- --tested

    /// <summary>One script: its bytes, or why they couldn't be read; Label names where it came from.</summary>
    sealed record Script(string Label, byte[]? Data, string? Error);

    static void Tested(ReleaseContext c, List<ReleaseFinding> findings)
    {
        // the new release's scripts: archives first, then loose files, which the game reads over an archive's copy
        var fresh = new Dictionary<string, Script>(StringComparer.Ordinal);
        foreach (var pkg in c.Packages)
        {
            foreach (var ba2 in pkg.Archives)
            {
                var label = $"{pkg.Name}:{Path.GetRelativePath(pkg.Root, ba2)}";
                try
                {
                    foreach (var (entry, data, error) in TriageScripts.ArchiveScripts(ba2))
                        fresh.TryAdd(entry, new Script($"{label}:{entry}", data, error));
                }
                // an archive that can't be read is already a PROBLEM of the zip rules (ba2-unreadable)
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Log.Write($"tested: scripts of {label} not listed: {e.Message}");
                }
            }
            foreach (var rel in pkg.Files.Where(f => f.EndsWith(".pex", StringComparison.Ordinal)))
                fresh[rel] = new Script($"{pkg.Name}:{rel}", ReadLoose(Path.Combine(pkg.Root, rel.Replace('\\', Path.DirectorySeparatorChar)), out var why), why);
        }

        // the tested build's scripts and plugins
        var tested = new Dictionary<string, Script>(StringComparer.Ordinal);
        var testedPlugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // file name -> SHA-256
        for (int t = 0; t < c.Options.Tested.Count; t++)
        {
            var path = Path.GetFullPath(c.Options.Tested[t]);
            var name = Path.GetFileName(path);
            try
            {
                if (path.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) AddArchive(tested, path, name);
                else ReadTestedZip(c, t, path, tested, testedPlugins);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log.Write($"tested: {path}: {e}");
                findings.Add(new(Level.Problem, TestedRule, $"the tested build can't be read, so no tested-build comparison ran: {name}: {e.Message} " +
                    "(give --tested the zip or .ba2 of the build you tested)"));
                return;
            }
        }
        Log.Write($"tested: {fresh.Count} scripts in the release, {tested.Count} scripts and {testedPlugins.Count} plugins in the tested build");

        // plugins: byte for byte
        if (c.Options.Tested.Any(t => !t.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)))
            foreach (var plugin in c.AllPlugins)
            {
                var pname = Path.GetFileName(plugin);
                if (!testedPlugins.TryGetValue(pname, out var testedHash))
                {
                    findings.Add(new(Level.Review, TestedRule, $"no plugin named {pname} in the tested build; plugin not compared"));
                    continue;
                }
                string hash;
                using (var f = File.OpenRead(plugin)) hash = Convert.ToHexString(SHA256.HashData(f));
                Log.Write($"tested: plugin {pname}: release {hash[..16]}, tested {testedHash[..16]}");
                findings.Add(hash == testedHash
                    ? new(Level.Note, TestedRule, $"plugin is byte-identical to the tested build: {pname}")
                    : new(Level.Review, TestedRule, $"plugin differs from the tested build (expected only if its records changed): {pname}"));
            }
        else
            Log.Write("tested: only archives given, so the plugins were not compared");

        // scripts: by meaning
        foreach (var key in fresh.Keys.Union(tested.Keys).Order(StringComparer.Ordinal))
        {
            if (!fresh.TryGetValue(key, out var now)) { findings.Add(new(Level.Review, TestedRule, $"script in the tested build, missing now: {key}")); continue; }
            if (!tested.TryGetValue(key, out var then)) { findings.Add(new(Level.Review, TestedRule, $"script not in the tested build (new since the test): {key}")); continue; }
            List<string> a, b;
            try { a = ListingOf(then); b = ListingOf(now); }
            catch (InvalidDataException e)
            {
                findings.Add(new(Level.Problem, TestedRule, $"script can't be read for the tested-build comparison: {e.Message}"));
                continue;
            }
            bool same = a.SequenceEqual(b, StringComparer.Ordinal);
            Log.Write($"tested: {key}: {(same ? "same" : "different")} ({then.Label} against {now.Label})");
            if (!same && Log.On)
            {
                var first = Enumerable.Range(0, Math.Max(a.Count, b.Count)).First(i => i >= a.Count || i >= b.Count || a[i] != b[i]);
                Log.Write($"tested: {key}: first difference at listing line {first + 1}: tested \"{(first < a.Count ? a[first] : "(end)")}\", release \"{(first < b.Count ? b[first] : "(end)")}\"");
            }
            findings.Add(same
                ? new(Level.Note, TestedRule, $"script same as the tested build: {key}")
                : new(Level.Review, TestedRule, $"script differs from the tested build (expected only if its source changed): {key}"));
        }
    }

    static List<string> ListingOf(Script s)
    {
        if (s.Data == null) throw new InvalidDataException($"{s.Label}: {s.Error}");
        return Pex.Listing(Pex.Read(s.Data, s.Label));
    }

    static byte[]? ReadLoose(string path, out string? why)
    {
        why = null;
        try
        {
            if (new FileInfo(path).Length > Pex.MaxBytes) { why = "it is larger than any compiled script"; return null; }
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = e.Message; return null; }
    }

    static void AddArchive(Dictionary<string, Script> into, string ba2, string label)
    {
        foreach (var (entry, data, error) in TriageScripts.ArchiveScripts(ba2))
            into.TryAdd(entry, new Script($"{label}:{entry}", data, error));
    }

    /// <summary>A tested zip, read with the release zips' limits: only its .pex, .ba2 and plugin entries are read. An
    /// archive is copied into the work folder under a made-up name (never the entry's own path), a plugin is hashed as
    /// it is read, a script is kept in memory.</summary>
    static void ReadTestedZip(ReleaseContext c, int t, string zip, Dictionary<string, Script> scripts, Dictionary<string, string> plugins)
    {
        var name = Path.GetFileName(zip);
        using var z = ZipFile.OpenRead(zip);
        if (z.Entries.Count > ReleaseCommand.MaxEntries) throw new InvalidDataException(ReleaseCommand.TooManyEntries(z.Entries.Count, "zip"));
        long total = 0;
        var loose = new Dictionary<string, Script>(StringComparer.Ordinal);
        int k = 0;
        foreach (var e in z.Entries)
        {
            var entryName = e.FullName.Replace('\\', '/');
            if (entryName.EndsWith('/')) continue;
            var norm = Ba2Names.Normalize(entryName);
            bool pex = norm.EndsWith(".pex", StringComparison.Ordinal), ba2 = norm.EndsWith(".ba2", StringComparison.Ordinal), plugin = IsPlugin(norm);
            if (!pex && !ba2 && !plugin) continue;
            total += e.Length;
            if (total > ReleaseCommand.MaxUnpackedBytes)
                throw new InvalidDataException($"its scripts, archives and plugins unpack to more than the {ReleaseCommand.MaxUnpackedBytes / (1024 * 1024 * 1024)} GB this check allows");
            if (pex)
            {
                if (e.Length > Pex.MaxBytes) { loose[norm] = new Script($"{name}:{norm}", null, "it is larger than any compiled script"); continue; }
                loose[norm] = new Script($"{name}:{norm}", ReadEntry(e, name), null);
            }
            else if (plugin)
            {
                using var s = new CappedStream(e.Open(), e.Length, e.FullName, name);
                plugins[RelPath.FileName(norm)!] = Convert.ToHexString(SHA256.HashData(s));
            }
            else
            {
                var copy = Path.Combine(c.Work, $"tested{t}-{k++}.ba2");
                using (var input = new CappedStream(e.Open(), e.Length, e.FullName, name))
                using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
                    input.CopyTo(output);
                AddArchive(scripts, copy, $"{name}:{norm}");
            }
        }
        foreach (var (key, s) in loose) scripts[key] = s;   // loose files win over an archive's copy, as in game
        Log.Write($"tested: read {zip}");
    }

    static byte[] ReadEntry(ZipArchiveEntry e, string zip)
    {
        using var s = new CappedStream(e.Open(), e.Length, e.FullName, zip);
        var m = new MemoryStream((int)e.Length);
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>A zip entry's stream that refuses to give more bytes than the zip says the entry holds.</summary>
    sealed class CappedStream(Stream inner, long length, string entry, string zip) : Stream
    {
        long read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            read += n;
            if (read > length || (n == 0 && read != length))
                throw new InvalidDataException($"{zip}: the entry \"{entry}\" unpacks to a different size than the zip says");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    // ---------------------------------------------------------------- --stage

    /// <summary>The stage's files as normalized paths, without what never goes in an archive: archives, achlists,
    /// DMMDeps output, plugins and script sources.</summary>
    public static SortedSet<string> StagedFiles(string stage)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(stage, "*", opts))
        {
            var norm = Ba2Names.Normalize(Path.GetRelativePath(stage, f));
            if (norm.EndsWith(".ba2", StringComparison.Ordinal) || norm.EndsWith(".achlist", StringComparison.Ordinal)
                || norm.EndsWith("_deps.csv", StringComparison.Ordinal) || norm.EndsWith("_deps.json", StringComparison.Ordinal)
                || IsPlugin(norm) || norm.StartsWith(@"scripts\source\", StringComparison.Ordinal))
                continue;
            set.Add(norm);
        }
        return set;
    }

    static void Stage(ReleaseContext c, List<ReleaseFinding> findings)
    {
        var stage = Path.GetFullPath(c.Options.Stage!);
        var archives = c.Packages.SelectMany(p => p.Archives.Select(a => (p, a))).ToList();
        if (archives.Count == 0)
        {
            findings.Add(new(Level.Note, StageRule, "the release has no archive (.ba2), so the stage check was skipped"));
            return;
        }
        var inArchives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, a) in archives)
        {
            try { inArchives.UnionWith(Ba2Names.Read(a)); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // the zip rules already report the archive (ba2-unreadable); its names are unknown, so nothing is compared
                Log.Write($"stage: {p.Name}:{Path.GetFileName(a)} can't be read: {e.Message}");
                findings.Add(new(Level.Note, StageRule, $"the stage check was skipped: {p.Name}:{Path.GetRelativePath(p.Root, a)} can't be read"));
                return;
            }
        }
        var loose = new HashSet<string>(c.Packages.SelectMany(p => p.Files), StringComparer.Ordinal);
        SortedSet<string> staged;
        try { staged = StagedFiles(stage); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            findings.Add(new(Level.Problem, StageRule, $"the stage folder {stage} can't be read: {e.Message} (check that you can open every folder in it)"));
            return;
        }
        int missing = 0, extra = 0;
        foreach (var f in staged.Where(f => !inArchives.Contains(f) && !loose.Contains(f)))
        {
            missing++;
            findings.Add(new(Level.Problem, StageRule, $"staged, missing from the archives: {f} (the game won't see it; pack it again, or ship it loose in the zip)"));
        }
        foreach (var f in inArchives.Where(f => !staged.Contains(f)).Order(StringComparer.Ordinal))
        {
            extra++;
            findings.Add(new(Level.Review, StageRule, $"in an archive, not staged (a stale archive?): {f}"));
        }
        Log.Write($"stage: {stage}: {staged.Count} files staged, {inArchives.Count} in {archives.Count} archives, {loose.Count} in the zips; {missing} missing from the archives, {extra} not staged");
        if (missing == 0 && extra == 0)
            findings.Add(new(Level.Note, StageRule, $"stage matches the archives: {staged.Count(inArchives.Contains)} files"));
    }
}
