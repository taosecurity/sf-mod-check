// diff step 5 (0.11.0): the files of two mods other than their plugins. Loose files pair by their path inside the mod
// and compare by size, then SHA-256. Archives (.ba2) pair by file name, as plugins do; an archive whose bytes differ
// has its files listed (Ba2Names) and each compared by a hash of its stored bytes (Ba2Contents.Hashes). A changed
// script (.pex, loose or in an archive) is read with Pex.Read and compared object by object: states and functions
// added, removed or changed, by name and count only. Nothing here prints or keeps script source, code or instructions.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SfModCheck;

/// <summary>A file in both versions whose bytes differ, with both sizes.</summary>
public sealed record FileChange(string Path, long OldSize, long NewSize);

/// <summary>One archive: in both versions (status "same" or "changed"), or only in one ("added", "removed").</summary>
public sealed class ArchiveDiff
{
    public required string Name { get; init; }
    public string? OldRel { get; init; }
    public string? NewRel { get; init; }
    /// <summary>OldRel and NewRel with their spelling on disk (for the report; the Rel paths open the files).</summary>
    public string? OldShown { get; init; }
    public string? NewShown { get; init; }
    public string Status { get; set; } = "same";
    /// <summary>GNRL or DX10 (the new version's; null when it couldn't be read).</summary>
    public string? Type { get; set; }
    public int OldCount { get; set; }
    public int NewCount { get; set; }
    public List<string> Added { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Changed { get; } = [];
    public int Same { get; set; }
    /// <summary>True when the two archives hold the same bytes (nothing inside was read).</summary>
    public bool SameBytes { get; set; }
    /// <summary>Why the files inside couldn't be compared (a damaged archive); the archive is then "changed".</summary>
    public string? Error { get; set; }
}

/// <summary>One object of a changed script: added, removed or changed, with what in it changed (names only).</summary>
public sealed class PexObjectDiff
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? ParentChange { get; set; }
    public List<string> StatesAdded { get; } = [];
    public List<string> StatesRemoved { get; } = [];
    public List<string> FunctionsAdded { get; } = [];
    public List<string> FunctionsRemoved { get; } = [];
    public List<string> FunctionsChanged { get; } = [];
    /// <summary>Other parts that differ: "variables: 1 added (X)", "properties: 1 changed (Y)", "auto state", ...</summary>
    public List<string> Other { get; } = [];
    public bool Any => ParentChange != null || StatesAdded.Count + StatesRemoved.Count + FunctionsAdded.Count + FunctionsRemoved.Count + FunctionsChanged.Count + Other.Count > 0;
}

/// <summary>A script whose bytes differ: where it is, and its objects compared (or why they couldn't be).</summary>
public sealed class ScriptDiff
{
    public required string Path { get; init; }
    /// <summary>"loose" or the archive's file name.</summary>
    public required string Where { get; init; }
    public string? Error { get; set; }
    public List<PexObjectDiff> Objects { get; } = [];
    /// <summary>The bytes differ but every object is the same (compile time, debug info or string order).</summary>
    public bool CodeSame => Error == null && Objects.Count == 0;
}

public sealed class FilesDiff
{
    public List<string> Added { get; } = [];
    public List<string> Removed { get; } = [];
    public List<FileChange> Changed { get; } = [];
    public int Same { get; set; }
    public List<ArchiveDiff> Archives { get; } = [];
    public List<ScriptDiff> Scripts { get; } = [];

    public bool Differences => Added.Count + Removed.Count + Changed.Count > 0 || Archives.Any(a => a.Status != "same");

    /// <summary>For the RESULT line: loose files plus the files inside paired archives; an archive only in one version,
    /// one whose files couldn't be listed, or one whose bytes differ while its files are all the same, counts as one file.</summary>
    public (int Added, int Removed, int Changed) FileTotals()
    {
        int a = Added.Count, r = Removed.Count, c = Changed.Count;
        foreach (var x in Archives)
            switch (x.Status)
            {
                case "added": a++; break;
                case "removed": r++; break;
                case "changed" when x.Error != null: c++; break;
                // bytes differ but every file inside is the same (only the layout changed): counts as one changed file,
                // so the RESULT line never says DIFFERENCES with 0 files changed
                case "changed" when x.Added.Count + x.Removed.Count + x.Changed.Count == 0: c++; break;
                default: a += x.Added.Count; r += x.Removed.Count; c += x.Changed.Count; break;
            }
        return (a, r, c);
    }

    // ---- compare ----

    static bool IsPlugin(string rel) => RelPath.Extension(rel).ToLowerInvariant() is ".esm" or ".esp" or ".esl";
    static bool IsBa2(string rel) => RelPath.Extension(rel).Equals(".ba2", StringComparison.OrdinalIgnoreCase);
    static bool IsPex(string rel) => RelPath.Extension(rel).Equals(".pex", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compares everything in two mods that isn't a plugin. Every pairing and decision goes to the log.</summary>
    public static FilesDiff Compare(DiffInput oldIn, DiffInput newIn)
    {
        var d = new FilesDiff();
        // loose files: by path inside the mod (Files are already lower case with backslashes). A mod whose files all sit
        // under Data\ pairs without it (triage's rule), so a download packed with a Data folder pairs with one without.
        static string Prefix(List<string> files) => files.Count > 0 && files.All(f => f.StartsWith("data\\", StringComparison.Ordinal)) ? "data\\" : "";
        string op = Prefix(oldIn.Files), np = Prefix(newIn.Files);
        if (op.Length > 0) Log.Write("files: every file of the old version is under Data\\, so loose files pair without it");
        if (np.Length > 0) Log.Write("files: every file of the new version is under Data\\, so loose files pair without it");
        var oldLoose = oldIn.Files.Where(f => !IsPlugin(f) && !IsBa2(f)).ToDictionary(f => f[op.Length..], StringComparer.OrdinalIgnoreCase);
        var newLoose = newIn.Files.Where(f => !IsPlugin(f) && !IsBa2(f)).ToDictionary(f => f[np.Length..], StringComparer.OrdinalIgnoreCase);
        foreach (var (key, rel) in oldLoose.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!newLoose.TryGetValue(key, out var nrel)) { d.Removed.Add(Spell(oldIn, rel)); Log.Write($"files: only in the old version: {rel}"); continue; }
            string a = oldIn.Full(rel), b = newIn.Full(nrel);
            if (DiffCommand.SameBytes(a, b)) { d.Same++; continue; }
            var shown = Spell(newIn, nrel);
            d.Changed.Add(new(shown, new FileInfo(a).Length, new FileInfo(b).Length));
            Log.Write($"files: changed: {nrel} ({new FileInfo(a).Length:N0} -> {new FileInfo(b).Length:N0} bytes)");
            if (IsPex(rel)) d.Scripts.Add(CompareScript(shown, "loose", () => Pex.ReadFile(a), () => Pex.ReadFile(b)));
        }
        foreach (var (key, rel) in newLoose.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!oldLoose.ContainsKey(key)) { d.Added.Add(Spell(newIn, rel)); Log.Write($"files: only in the new version: {rel}"); }
        Log.Write($"files: loose files {d.Added.Count} added, {d.Removed.Count} removed, {d.Changed.Count} changed, {d.Same} the same");
        foreach (var (input, which) in new[] { (oldIn, "old"), (newIn, "new") })
            Log.Write($"files: the {which} version's {input.Files.Count(IsPlugin)} plugin(s) and {input.Files.Count(IsBa2)} archive(s) are left out of the loose-file " +
                      "compare (plugins are compared record by record, archives file by file)");
        var onlyPex = d.Added.Count(IsPex) + d.Removed.Count(IsPex);
        if (onlyPex > 0) Log.Write($"scripts: {onlyPex} loose script(s) only in one version are listed as files added or removed; only changed scripts are read");

        // archives: by file name, as plugins; when a name repeats inside one version, every archive pairs by its path
        var oldBa2 = oldIn.Files.Where(IsBa2).ToList();
        var newBa2 = newIn.Files.Where(IsBa2).ToList();
        bool byPath = HasTwins(oldBa2) || HasTwins(newBa2);
        if (byPath) Log.Write("archives: two archives share a file name in one version, so archives are paired by their path inside the mod");
        string Key(string rel) => byPath ? rel : RelPath.FileName(rel)!;
        var om = oldBa2.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var nm = newBa2.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, orel) in om.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!nm.TryGetValue(k, out var nrel))
            {
                d.Archives.Add(new ArchiveDiff { Name = Shown(oldIn, orel, byPath), OldRel = orel, OldShown = Spell(oldIn, orel), Status = "removed" });
                Log.Write($"archives: only in the old version: {orel}");
                continue;
            }
            d.Archives.Add(CompareArchive(oldIn, newIn, orel, nrel, d.Scripts, byPath));
        }
        foreach (var (k, nrel) in nm.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            if (!om.ContainsKey(k))
            {
                d.Archives.Add(new ArchiveDiff { Name = Shown(newIn, nrel, byPath), NewRel = nrel, NewShown = Spell(newIn, nrel), Status = "added" });
                Log.Write($"archives: only in the new version: {nrel}");
            }
        return d;
    }

    /// <summary>An archive's name in the report: its file name, or its path when archives pair by path.</summary>
    static string Shown(DiffInput i, string rel, bool byPath) => byPath ? Spell(i, rel) : RelPath.FileName(Spell(i, rel))!;

    static bool HasTwins(List<string> rels) =>
        rels.GroupBy(r => RelPath.FileName(r)!, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);

    static ArchiveDiff CompareArchive(DiffInput oldIn, DiffInput newIn, string orel, string nrel, List<ScriptDiff> scripts, bool byPath)
    {
        string a = oldIn.Full(orel), b = newIn.Full(nrel);
        var x = new ArchiveDiff { Name = Shown(newIn, nrel, byPath), OldRel = orel, NewRel = nrel, OldShown = Spell(oldIn, orel), NewShown = Spell(newIn, nrel) };
        Log.Write($"archives: paired {orel} with {nrel}");
        if (DiffCommand.SameBytes(a, b))
        {
            x.SameBytes = true;
            try { x.Type = Ba2Names.Type(b); x.OldCount = x.NewCount = x.Same = Ba2Names.Read(b).Count; }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { Log.Write($"archives: {x.Name}: the same bytes, but its file list couldn't be read ({e.Message})"); }
            Log.Write($"archives: {x.Name}: the same bytes ({x.Same} files)");
            return x;
        }
        x.Status = "changed";
        Dictionary<string, Ba2Contents.Stored> oh, nh;
        try
        {
            oh = Ba2Contents.Hashes(a, out _);
            nh = Ba2Contents.Hashes(b, out var type);
            x.Type = type;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            x.Error = $"its files couldn't be listed: {e.Message.TrimEnd('.')}. {Redownload}";
            Log.Write($"archives: {x.Name}: {x.Error}");
            return x;
        }
        x.OldCount = oh.Count; x.NewCount = nh.Count;
        foreach (var (n, h) in oh.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!nh.TryGetValue(n, out var h2)) { x.Removed.Add(n); continue; }
            if (h.Hash.AsSpan().SequenceEqual(h2.Hash)) x.Same++;
            else x.Changed.Add(n);
        }
        foreach (var n in nh.Keys.Order(StringComparer.Ordinal)) if (!oh.ContainsKey(n)) x.Added.Add(n);
        Log.Write($"archives: {x.Name} ({x.Type}): {x.Added.Count} files added, {x.Removed.Count} removed, {x.Changed.Count} changed, {x.Same} the same");
        if (x.Added.Count + x.Removed.Count + x.Changed.Count == 0)
            Log.Write($"archives: {x.Name}: every file is the same; only the archive's layout differs (order, padding or the name table)");
        foreach (var n in x.Added) Log.Write($"archives: {x.Name}: only in the new version: {n}");
        foreach (var n in x.Removed) Log.Write($"archives: {x.Name}: only in the old version: {n}");
        foreach (var n in x.Changed) Log.Write($"archives: {x.Name}: changed: {n}");

        // changed scripts inside: unpacked only for the .pex entries that changed
        var want = x.Changed.Where(IsPex).ToHashSet(StringComparer.Ordinal);
        if (want.Count > 0 && x.Type == "DX10")
            Log.Write($"scripts: {x.Name} is a texture (DX10) archive, so its {want.Count} changed .pex file(s) can't be read (only general archives hold readable scripts)");
        if (want.Count > 0)
        {
            Dictionary<string, (byte[]? Data, string? Error)> Unpack(string path)
            {
                var m = new Dictionary<string, (byte[]?, string?)>(StringComparer.Ordinal);
                foreach (var (entry, data, error) in TriageScripts.ArchiveScripts(path, want.Contains)) m.TryAdd(entry, (data, error));
                return m;
            }
            Dictionary<string, (byte[]? Data, string? Error)> ou, nu;
            try { ou = Unpack(a); nu = Unpack(b); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                foreach (var w in want) scripts.Add(new ScriptDiff { Path = w, Where = x.Name, Error = $"couldn't be unpacked: {e.Message.TrimEnd('.')}. {Redownload}" });
                Log.Write($"archives: {x.Name}: its changed scripts couldn't be unpacked: {e.Message}");
                return x;
            }
            foreach (var w in want.Order(StringComparer.Ordinal))
            {
                PexFile Side(Dictionary<string, (byte[]? Data, string? Error)> m, string which) =>
                    m.TryGetValue(w, out var v) && v.Data is { } bytes ? Pex.Read(bytes, RelPath.FileName(w)!)
                        : throw new InvalidDataException($"the {which} version {(m.TryGetValue(w, out var v2) ? v2.Error : "isn't in a general archive")}");
                scripts.Add(CompareScript(w, x.Name, () => Side(ou, "old"), () => Side(nu, "new")));
            }
        }
        return x;
    }

    // ---- scripts ----

    /// <summary>Reads both versions of a changed script and compares their objects (names and counts only).</summary>
    public static ScriptDiff CompareScript(string path, string where, Func<PexFile> readOld, Func<PexFile> readNew)
    {
        var s = new ScriptDiff { Path = path, Where = where };
        PexFile a, b;
        try { a = readOld(); b = readNew(); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            s.Error = $"its objects couldn't be compared: {e.Message.TrimEnd('.')}. {Redownload}";
            Log.Write($"scripts: {path} ({where}): {s.Error}");
            return s;
        }
        s.Objects.AddRange(PexCompare.Objects(a, b));
        Log.Write($"scripts: {path} ({where}): " + (s.CodeSame ? "the bytes differ, every object is the same (compile time, debug info or string order)"
            : string.Join("; ", s.Objects.Select(o => $"{o.Name} {o.Status}" + (o.Status == "changed" ? $": {o.FunctionsChanged.Count} functions changed, " +
                $"{o.FunctionsAdded.Count} added, {o.FunctionsRemoved.Count} removed" : "")))));
        return s;
    }

    /// <summary>The fix an error about a damaged file inside a mod suggests.</summary>
    const string Redownload = "If the file is damaged, download that version of the mod again.";

    /// <summary>A path inside an input with the spelling it has on disk (Files hold lower case on Windows).</summary>
    public static string Spell(DiffInput i, string rel)
    {
        try
        {
            var full = i.Full(rel);
            if (!OperatingSystem.IsWindows()) return System.IO.Path.GetRelativePath(i.Root, full).Replace('/', '\\');
            var dir = i.Root;
            var parts = rel.Split('\\');
            var shown = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var hit = Directory.EnumerateFileSystemEntries(dir, p, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 }).FirstOrDefault();
                if (hit == null) return rel;
                var name = System.IO.Path.GetFileName(hit);
                shown.Add(name);
                dir = System.IO.Path.Combine(dir, name);
            }
            return string.Join('\\', shown);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return rel; }
    }

    // ---- report ----

    public const int FilesShown = 50, FoldersShown = 30, ScriptsShown = 50, NamesShown = 20;

    static string Folder(string path) => RelPath.DirectoryName(path) is { Length: > 0 } f ? f : "(top level)";

    static string ShowList(IEnumerable<string> items, bool all, int cap = NamesShown)
    {
        var l = items.ToList();
        var shown = all ? l : l.Take(cap).ToList();
        return string.Join(", ", shown) + (l.Count > shown.Count ? $", and {l.Count - shown.Count:N0} more" : "");
    }

    static void Folders(StringBuilder sb, string indent, IEnumerable<(string Path, string Change)> items, bool all)
    {
        var groups = items.GroupBy(x => Folder(x.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key, Added: g.Count(x => x.Change == "+"), Removed: g.Count(x => x.Change == "-"), Changed: g.Count(x => x.Change == "~")))
            .OrderByDescending(g => g.Added + g.Removed + g.Changed).ThenBy(g => g.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count == 0) return;
        sb.AppendLine($"{indent}by folder:");
        foreach (var g in all ? groups : groups.Take(FoldersShown))
        {
            var parts = new List<string>();
            if (g.Added > 0) parts.Add($"{g.Added:N0} added");
            if (g.Removed > 0) parts.Add($"{g.Removed:N0} removed");
            if (g.Changed > 0) parts.Add($"{g.Changed:N0} changed");
            sb.AppendLine($"{indent}  {g.Folder}: {string.Join(", ", parts)}");
        }
        if (!all && groups.Count > FoldersShown) sb.AppendLine($"{indent}  ... and {groups.Count - FoldersShown:N0} more folders (--all lists them all)");
    }

    static void Lines(StringBuilder sb, string indent, string title, IReadOnlyList<string> lines, bool all)
    {
        if (lines.Count == 0) return;
        sb.AppendLine($"{indent}{title} ({lines.Count:N0}):");
        foreach (var l in all ? lines : lines.Take(FilesShown)) sb.AppendLine($"{indent}  {l}");
        if (!all && lines.Count > FilesShown) sb.AppendLine($"{indent}  ... and {lines.Count - FilesShown:N0} more (--all or --json lists them all)");
    }

    public void Text(StringBuilder sb, bool all)
    {
        sb.AppendLine();
        sb.AppendLine("== Loose files (not plugins or archives; compared by size, then SHA-256) ==");
        if (Added.Count + Removed.Count + Changed.Count + Same == 0) sb.AppendLine("  (no loose files in either version)");
        else
        {
            sb.AppendLine($"  {Added.Count:N0} added, {Removed.Count:N0} removed, {Changed.Count:N0} changed; {Same:N0} the same");
            Folders(sb, "  ", Added.Select(p => (p, "+")).Concat(Removed.Select(p => (p, "-"))).Concat(Changed.Select(c => (c.Path, "~"))), all);
            Lines(sb, "  ", "added", Added, all);
            Lines(sb, "  ", "removed", Removed, all);
            Lines(sb, "  ", "changed", Changed.Select(c => $"{c.Path} ({c.OldSize:N0} -> {c.NewSize:N0} bytes)").ToList(), all);
        }

        sb.AppendLine();
        sb.AppendLine("== Archives (.ba2; each file compared by a hash of its stored bytes) ==");
        if (Archives.Count == 0) sb.AppendLine("  (no archives in either version)");
        foreach (var x in Archives)
        {
            var type = x.Type != null ? $"{x.Type}, " : "";
            switch (x.Status)
            {
                case "added": sb.AppendLine($"  added    {x.Name} (only in the new version: {x.NewShown})"); continue;
                case "removed": sb.AppendLine($"  removed  {x.Name} (only in the old version: {x.OldShown})"); continue;
                case "same": sb.AppendLine($"  same     {x.Name} ({type}{x.Same:N0} file{(x.Same == 1 ? "" : "s")}; the same bytes)"); continue;
            }
            if (x.Error != null) { sb.AppendLine($"  changed  {x.Name}: the bytes differ, and {x.Error}"); continue; }
            sb.AppendLine($"  changed  {x.Name} ({type}{x.OldCount:N0} -> {x.NewCount:N0} files): {x.Added.Count:N0} added, {x.Removed.Count:N0} removed, " +
                          $"{x.Changed.Count:N0} changed; {x.Same:N0} the same" +
                          (x.Added.Count + x.Removed.Count + x.Changed.Count == 0 ? " (every file is the same; only the archive's layout differs)" : ""));
            Folders(sb, "    ", x.Added.Select(p => (p, "+")).Concat(x.Removed.Select(p => (p, "-"))).Concat(x.Changed.Select(p => (p, "~"))), all);
            Lines(sb, "    ", "added", x.Added, all);
            Lines(sb, "    ", "removed", x.Removed, all);
            Lines(sb, "    ", "changed", x.Changed, all);
        }

        if (Scripts.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("== Scripts (changed .pex files: objects, states and functions by name; no code is shown) ==");
        foreach (var s in all ? Scripts : Scripts.Take(ScriptsShown))
        {
            var head = $"  {s.Path} ({(s.Where == "loose" ? "loose" : "in " + s.Where)})";
            if (s.Error != null) { sb.AppendLine($"{head}: {s.Error}"); continue; }
            if (s.CodeSame) { sb.AppendLine($"{head}: the same objects, states and functions (only the compile time, debug info or string order differ)"); continue; }
            sb.AppendLine($"{head}:");
            foreach (var o in s.Objects)
            {
                if (o.Status != "changed") { sb.AppendLine($"    object {o.Name}: {o.Status}"); continue; }
                sb.AppendLine($"    object {o.Name}: changed");
                if (o.ParentChange != null) sb.AppendLine($"      extends: {o.ParentChange}");
                if (o.StatesAdded.Count > 0) sb.AppendLine($"      states added ({o.StatesAdded.Count:N0}): {ShowList(o.StatesAdded, all)}");
                if (o.StatesRemoved.Count > 0) sb.AppendLine($"      states removed ({o.StatesRemoved.Count:N0}): {ShowList(o.StatesRemoved, all)}");
                if (o.FunctionsChanged.Count > 0) sb.AppendLine($"      functions changed ({o.FunctionsChanged.Count:N0}): {ShowList(o.FunctionsChanged, all)}");
                if (o.FunctionsAdded.Count > 0) sb.AppendLine($"      functions added ({o.FunctionsAdded.Count:N0}): {ShowList(o.FunctionsAdded, all)}");
                if (o.FunctionsRemoved.Count > 0) sb.AppendLine($"      functions removed ({o.FunctionsRemoved.Count:N0}): {ShowList(o.FunctionsRemoved, all)}");
                foreach (var t in o.Other) sb.AppendLine($"      {t}");
            }
        }
        if (!all && Scripts.Count > ScriptsShown) sb.AppendLine($"  ... and {Scripts.Count - ScriptsShown:N0} more scripts (--all or --json lists them all)");
    }

    static JsonArray Arr(IEnumerable<string> l) => new(l.Select(x => (JsonNode)x).ToArray());

    public void Json(JsonObject root)
    {
        root["files"] = new JsonObject
        {
            ["added"] = Arr(Added), ["removed"] = Arr(Removed),
            ["changed"] = new JsonArray(Changed.Select(c => (JsonNode)new JsonObject { ["path"] = c.Path, ["old_size"] = c.OldSize, ["new_size"] = c.NewSize }).ToArray()),
            ["same"] = Same,
        };
        root["archives"] = new JsonArray(Archives.Select(x => (JsonNode)new JsonObject
        {
            ["name"] = x.Name, ["status"] = x.Status, ["old"] = x.OldShown, ["new"] = x.NewShown, ["type"] = x.Type,
            ["old_files"] = x.OldCount, ["new_files"] = x.NewCount, ["same_bytes"] = x.SameBytes,
            ["added"] = Arr(x.Added), ["removed"] = Arr(x.Removed), ["changed"] = Arr(x.Changed), ["same"] = x.Same, ["error"] = x.Error,
        }).ToArray());
        root["scripts"] = new JsonArray(Scripts.Select(s => (JsonNode)new JsonObject
        {
            ["path"] = s.Path, ["where"] = s.Where, ["error"] = s.Error, ["code_same"] = s.CodeSame,
            ["objects"] = new JsonArray(s.Objects.Select(o => (JsonNode)new JsonObject
            {
                ["name"] = o.Name, ["status"] = o.Status, ["parent_change"] = o.ParentChange,
                ["states_added"] = Arr(o.StatesAdded), ["states_removed"] = Arr(o.StatesRemoved),
                ["functions_added"] = Arr(o.FunctionsAdded), ["functions_removed"] = Arr(o.FunctionsRemoved),
                ["functions_changed"] = Arr(o.FunctionsChanged), ["other"] = Arr(o.Other),
            }).ToArray()),
        }).ToArray());
    }
}

/// <summary>Compares the objects of two versions of a script by name. A function is keyed by its state and name (a
/// property accessor by its property and get or set) and is changed when its code, parameters, locals, return type or
/// flags differ. Only names and counts come out.</summary>
public static class PexCompare
{
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static string FunctionName(PexFunction f) =>
        f.IsPropertyAccessor ? $"{f.State}.{f.Name} (property)" : f.State.Length == 0 ? f.Name : $"{f.Name} (state {f.State})";

    public static bool SameFunction(PexFunction a, PexFunction b)
    {
        if (a.ReturnType != b.ReturnType || a.Flags != b.Flags) return false;
        if (!a.Params.SequenceEqual(b.Params) || !a.Locals.SequenceEqual(b.Locals)) return false;
        if (a.Code.Count != b.Code.Count) return false;
        for (int i = 0; i < a.Code.Count; i++)
        {
            var x = a.Code[i]; var y = b.Code[i];
            if (x.Op != y.Op || !x.Args.SequenceEqual(y.Args) || !x.Extra.SequenceEqual(y.Extra)) return false;
        }
        return true;
    }

    /// <summary>Names added, removed and changed between two lists keyed by name (case ignored, the first of a name wins).</summary>
    public static (List<string> Added, List<string> Removed, List<string> Changed) Named<T>(IEnumerable<T> a, IEnumerable<T> b, Func<T, string> name, Func<T, T, bool> same)
    {
        var am = new Dictionary<string, T>(Ci); foreach (var x in a) am.TryAdd(name(x), x);
        var bm = new Dictionary<string, T>(Ci); foreach (var x in b) bm.TryAdd(name(x), x);
        var added = bm.Keys.Where(k => !am.ContainsKey(k)).ToList();
        var removed = am.Keys.Where(k => !bm.ContainsKey(k)).ToList();
        var changed = am.Where(kv => bm.TryGetValue(kv.Key, out var y) && !same(kv.Value, y)).Select(kv => kv.Key).ToList();
        return (added, removed, changed);
    }

    static void Other<T>(PexObjectDiff d, string what, IEnumerable<T> a, IEnumerable<T> b, Func<T, string> name, Func<T, T, bool> same)
    {
        var (ad, rm, ch) = Named(a, b, name, same);
        var parts = new List<string>();
        if (ad.Count > 0) parts.Add($"{ad.Count} added ({string.Join(", ", ad.Take(10))}{(ad.Count > 10 ? ", ..." : "")})");
        if (rm.Count > 0) parts.Add($"{rm.Count} removed ({string.Join(", ", rm.Take(10))}{(rm.Count > 10 ? ", ..." : "")})");
        if (ch.Count > 0) parts.Add($"{ch.Count} changed ({string.Join(", ", ch.Take(10))}{(ch.Count > 10 ? ", ..." : "")})");
        if (parts.Count > 0) d.Other.Add($"{what}: {string.Join(", ", parts)}");
    }

    public static List<PexObjectDiff> Objects(PexFile a, PexFile b)
    {
        var result = new List<PexObjectDiff>();
        var am = new Dictionary<string, PexObject>(Ci); foreach (var o in a.Objects) am.TryAdd(o.Name, o);
        var bm = new Dictionary<string, PexObject>(Ci); foreach (var o in b.Objects) bm.TryAdd(o.Name, o);
        foreach (var o in a.Objects.Where(o => !bm.ContainsKey(o.Name))) result.Add(new PexObjectDiff { Name = o.Name, Status = "removed" });
        foreach (var o in b.Objects.Where(o => !am.ContainsKey(o.Name))) result.Add(new PexObjectDiff { Name = o.Name, Status = "added" });
        foreach (var nb in b.Objects)
        {
            if (!am.TryGetValue(nb.Name, out var oa)) continue;
            var d = new PexObjectDiff { Name = nb.Name, Status = "changed" };
            if (!Ci.Equals(oa.Parent, nb.Parent)) d.ParentChange = $"{(oa.Parent.Length == 0 ? "(nothing)" : oa.Parent)} -> {(nb.Parent.Length == 0 ? "(nothing)" : nb.Parent)}";
            var (sa, sr, _) = Named(oa.States, nb.States, s => s.Name.Length == 0 ? "(empty state)" : s.Name, (_, _) => true);
            d.StatesAdded.AddRange(sa); d.StatesRemoved.AddRange(sr);
            var (fa, fr, fc) = Named(oa.Functions(), nb.Functions(), FunctionName, SameFunction);
            d.FunctionsAdded.AddRange(fa); d.FunctionsRemoved.AddRange(fr); d.FunctionsChanged.AddRange(fc);
            Other(d, "variables", oa.Variables, nb.Variables, v => v.Name, (x, y) => x == y);
            Other(d, "properties", oa.Properties, nb.Properties, p => p.Name,
                  (x, y) => x.Type == y.Type && x.Flags == y.Flags && x.UserFlags == y.UserFlags && x.AutoVar == y.AutoVar);
            Other(d, "structs", oa.Structs, nb.Structs, s => s.Name, (x, y) => x.Members.Select(m => m with { Doc = "" }).SequenceEqual(y.Members.Select(m => m with { Doc = "" })));   // doc comments aren't code
            if (!oa.Guards.SequenceEqual(nb.Guards, Ci)) d.Other.Add("guards differ");
            if (!Ci.Equals(oa.AutoState, nb.AutoState)) d.Other.Add("the auto state differs");
            if (oa.Const != nb.Const || oa.UserFlags != nb.UserFlags) d.Other.Add("the object's flags differ");
            if (d.Any) result.Add(d);
        }
        return result;
    }
}

/// <summary>Hashes of every file's stored bytes inside a .ba2 (0.11.0 diff). GNRL file records are 36 bytes (offset
/// at 16, packed size at 24, size at 28); DX10 texture records are 24 bytes (height, width, mips, format, cube map and
/// tile mode at 16 to 23, chunk count at 13, chunk header size at 14) plus one chunk header per chunk (offset at 0,
/// packed size at 8, size at 12, first and last mip at 16 and 18). A packed size of 0 means stored as is. The hash
/// takes a file's size and its stored bytes (each chunk's sizes, mips and bytes for a texture, after its dimensions and
/// format), so nothing is unpacked; the same file packed differently counts as changed.</summary>
public static class Ba2Contents
{
    public sealed record Stored(byte[] Hash, long StoredBytes);

    public static Dictionary<string, Stored> Hashes(string path, out string type)
    {
        var names = Ba2Names.Read(path);   // checks the header and the name table
        var name = Path.GetFileName(path);
        using var f = File.OpenRead(path);
        var head = new byte[24];
        f.ReadExactly(head);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        type = Encoding.ASCII.GetString(head, 8, 4);
        var nameOfs = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
        long pos = 24 + version switch { 2 => 8, 3 => 12, _ => 0 };
        if (type != "GNRL" && type != "DX10") throw new InvalidDataException($"{name}: unsupported archive type \"{type}\" (expected GNRL or DX10)");
        if (pos > nameOfs) throw new InvalidDataException($"{name}: its name table starts inside the header");
        long length = f.Length;
        var buf = new byte[1 << 20];
        var rec = new byte[36];
        var result = new Dictionary<string, Stored>(StringComparer.Ordinal);
        int dupes = 0;
        void Data(IncrementalHash h, ulong offset, uint stored, int entry)
        {
            if (offset > (ulong)length || offset + stored > (ulong)length)
                throw new InvalidDataException($"{name}: the data of entry {entry} of {names.Count} ({names[entry - 1]}) at byte {offset} runs past the end of the file");
            long back = f.Position;
            f.Seek((long)offset, SeekOrigin.Begin);
            long left = stored;
            while (left > 0)
            {
                int n = (int)Math.Min(buf.Length, left);
                f.ReadExactly(buf, 0, n);
                h.AppendData(buf, 0, n);
                left -= n;
            }
            f.Seek(back, SeekOrigin.Begin);
        }
        f.Seek(pos, SeekOrigin.Begin);
        for (int i = 0; i < names.Count; i++)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long storedTotal = 0;
            if (type == "GNRL")
            {
                if (f.Position + 36 > nameOfs) throw new InvalidDataException($"{name}: its file records (entry {i + 1} of {names.Count}) run into the name table");
                f.ReadExactly(rec, 0, 36);
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(16));
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(24));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(28));
                h.AppendData(rec, 24, 8);   // packed size and size
                var stored = packed != 0 ? packed : size;
                Data(h, offset, stored, i + 1);
                storedTotal = stored;
            }
            else
            {
                if (f.Position + 24 > nameOfs) throw new InvalidDataException($"{name}: its texture records (entry {i + 1} of {names.Count}) run into the name table");
                f.ReadExactly(rec, 0, 24);
                int chunks = rec[13];
                int chunkHdr = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(14));
                if (chunkHdr == 0) chunkHdr = 24;
                if (chunkHdr < 20) throw new InvalidDataException($"{name}: entry {i + 1} has a chunk header size of {chunkHdr} bytes, too small for its sizes and mips");
                h.AppendData(rec, 16, 8);   // dimensions, mips, format, cube map, tile mode
                var ch = new byte[chunkHdr];
                for (int k = 0; k < chunks; k++)
                {
                    if (f.Position + chunkHdr > nameOfs) throw new InvalidDataException($"{name}: the chunks of entry {i + 1} of {names.Count} run into the name table");
                    f.ReadExactly(ch, 0, chunkHdr);
                    var offset = BinaryPrimitives.ReadUInt64LittleEndian(ch.AsSpan(0));
                    var packed = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(8));
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(12));
                    h.AppendData(ch, 8, 12);   // packed size, size, first and last mip
                    var stored = packed != 0 ? packed : size;
                    Data(h, offset, stored, i + 1);
                    storedTotal += stored;
                }
            }
            if (!result.TryAdd(names[i], new Stored(h.GetHashAndReset(), storedTotal))) dupes++;
        }
        if (dupes > 0) Log.Write($"archives: {name}: {dupes} entries repeat an earlier file name (only the first is compared)");
        return result;
    }
}
