// The assets check (0.6.0, PLAN-ASSETS.md): every script a record attaches must have its .pex, and every model path a
// record names must have its file, in a loose file or in an archive the game loads. A missing script is an ERROR (the
// record's behavior silently doesn't run); a missing model is a WARNING. Since 0.14.0 materials, textures and the
// streamed sounds of the plugin's own sound banks are checked too (WARNINGs; 0.14.0 design D1 to D6), and since
// 0.15.0 the .mesh geometry files its own .nif files name (a WARNING). Voice and animation files, music and script
// parents are not (README "Known gaps").
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Starfield.Assets;

namespace SfModCheck;

/// <summary>The files the game can find: loose files under the given folders and the names inside the archives it
/// loads. Built once per run.</summary>
public sealed class AssetIndex
{
    readonly HashSet<string> inArchives = new(StringComparer.Ordinal);
    readonly List<string> looseDirs;
    readonly Dictionary<string, bool> looseSeen = new(StringComparer.Ordinal);

    /// <summary>Archives read, and the ones skipped with the reason (for --verbose and the summary).</summary>
    public readonly List<string> Loaded = new();
    public readonly List<(string Archive, string Why)> Skipped = new();
    /// <summary>Archives that should load but couldn't be read (a WARNING each: their files would count as missing).</summary>
    public readonly List<(string Archive, string Why)> Unreadable = new();

    /// <summary>The archives the game would load, by registry (0.6.0 part 2, the archive-limit warning): general
    /// archives (GNRL: scripts, meshes, materials, sounds) and texture archives (DX10). The game keeps 256 slots in
    /// each (an 8-bit index; a BA2 limit test on a large load order, confirmed in Ghidra 2026-11-17). Archives whose
    /// header can't be read aren't counted (they are in Unreadable). File names, in load order.</summary>
    public readonly List<string> Gnrl = new(), Dx10 = new();

    /// <summary>Slots per registry. A 257th general archive writes past the engine's arrays; a 257th texture archive
    /// wraps to index 0 (wrong textures or crashes).</summary>
    public const int ArchiveSlots = 256;

    /// <summary>INI keys that list archives the game loads at start (Starfield.ini, [Archive]).</summary>
    public static readonly string[] IniKeys =
    {
        "sResourceIndexFileList", "sResourceStartUpArchiveList", "sResourceArchiveList", "sResourceArchiveList2",
        "sResourceArchiveMemoryCacheList",
    };

    AssetIndex(List<string> looseDirs) => this.looseDirs = looseDirs;

    /// <summary>plugins: the file names whose "&lt;name&gt; - *.ba2" archives load (check: the plugin and its masters;
    /// conflict: every active plugin). dirs: folders searched in order for loose files and archives (the Data folder
    /// last). inis: INI files read in order, a later one overriding an earlier one's keys. language: voice archives
    /// of other languages ("Voices_de") are skipped. names: false reads only each archive's type, for the archive
    /// counts (conflict --no-assets); the index then finds nothing in archives. own (check, 0.16.2): the checked
    /// plugin and its folder, searched first for that plugin's own archives and for loose files; the other plugins'
    /// archives are looked for in dirs only, in order.</summary>
    public static AssetIndex Build(IEnumerable<string> plugins, IReadOnlyList<string> dirs, IEnumerable<string> inis, string language, bool names = true,
        (string Plugin, string Dir)? own = null)
    {
        // off Windows "/g/Data" and "/g/data" may be two folders, so they stay apart (at worst one is searched twice)
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var archiveDirs = dirs.Select(Path.GetFullPath).Distinct(cmp).ToList();
        var ownDirs = own is { } ow ? archiveDirs.Prepend(Path.GetFullPath(ow.Dir)).Distinct(cmp).ToList() : archiveDirs;
        var index = new AssetIndex(ownDirs);
        var archives = new List<(string Path, string Why)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // by file name: the first folder that has it wins
        bool Take(string path, string why)
        {
            if (!taken.Add(Path.GetFileName(path))) return false;
            archives.Add((path, why));
            return true;
        }
        foreach (var plugin in plugins)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin);
            var searched = own is { } o && string.Equals(plugin, o.Plugin, StringComparison.OrdinalIgnoreCase) ? ownDirs : archiveDirs;
            foreach (var dir in searched)
            {
                if (!Directory.Exists(dir)) continue;
                // case-insensitive on every system, as the game sees names under Proton (code review 0.6.0)
                // AttributesToSkip 0 and IgnoreInaccessible false: hidden and system archives count, and a folder that
                // can't be listed stops the assets step with its documented warning, both as before (code review 0.6.0)
                foreach (var ba2 in Directory.EnumerateFiles(dir, stem + " - *.ba2", new EnumerationOptions
                             { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = false })
                             .Order(StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal))
                {
                    var suffix = Path.GetFileNameWithoutExtension(ba2)[(stem.Length + 3)..];
                    if (OtherLanguageVoices(suffix, language)) { index.Skipped.Add((Path.GetFileName(ba2), $"voices for another language (--language {language})")); continue; }
                    if (Take(ba2, $"named after {plugin}"))
                    {
                        if (!index.ownArchives.TryGetValue(plugin, out var mine)) index.ownArchives[plugin] = mine = new();
                        mine.Add(ba2);
                    }
                }
            }
        }
        foreach (var (key, name, ini) in IniArchives(inis))
        {
            var found = index.looseDirs.Select(d => index.LooseFile(d, name)).FirstOrDefault(f => f != null);
            if (found == null) { index.Skipped.Add((name, $"listed in {ini} ({key}) but not found")); continue; }
            Take(found, $"listed in {Path.GetFileName(ini)} ({key})");
        }
        foreach (var (path, why) in archives)
        {
            var name = Path.GetFileName(path);
            try
            {
                // textures (DX10) count toward the texture registry's slots; since 0.14.0 their names are read too, for
                // the texture check (the name table has the same layout)
                var type = Ba2Names.Type(path);
                if (type == "DX10") index.Dx10.Add(name);
                else if (type == "GNRL") index.Gnrl.Add(name);
                else Log.Write($"assets: archive {name} has type \"{type}\" (not GNRL or DX10): not counted toward either archive limit");
                if (!names) continue;
                foreach (var n in Ba2Names.Read(path))
                {
                    // the game serves only textures from a texture archive
                    if (type == "DX10") { if (n.EndsWith(".dds", StringComparison.Ordinal)) index.inArchives.Add(n); continue; }
                    index.inArchives.Add(n);
                    // every material database counts: the game's and the ones plugins ship (0.14.0, relay step 10)
                    if (type == "GNRL" && n.EndsWith(".cdb", StringComparison.Ordinal)) index.AddCdb(path, n);
                }
                index.Loaded.Add($"{name} ({why})");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                index.Unreadable.Add((name, e.Message));
            }
        }
        // loose material databases under each searched folder's materials folder (any case)
        if (names)
            foreach (var dir in index.looseDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var mats in Directory.EnumerateDirectories(dir, "materials", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
                        foreach (var cdb in Directory.EnumerateFiles(mats, "*.cdb", new EnumerationOptions
                                     { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true }).Order(StringComparer.Ordinal))
                            index.AddCdb(cdb, null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"assets: material databases under {dir} could not be listed ({e.Message})"); }
            }
        foreach (var a in index.Loaded) Log.Write($"assets: archive {a}");
        // one line per counted general archive, with or without --no-assets (tools\compare_ba2count.py reads them)
        foreach (var a in index.Gnrl) Log.Write($"assets: archive {a} counted: general (GNRL)");
        foreach (var a in index.Dx10) Log.Write($"assets: archive {a} counted: texture (DX10)");
        foreach (var (a, why) in index.Skipped) Log.Write($"assets: archive {a} skipped: {why}");
        foreach (var (a, why) in index.Unreadable) Log.Write($"assets: archive {a} could not be read: {why}");
        Log.Write($"assets: {index.Loaded.Count} archives read ({index.inArchives.Count} file names), {index.Skipped.Count} skipped, " +
                  $"{index.Unreadable.Count} unreadable; loose files looked for in {string.Join(", ", index.looseDirs)}");
        Log.Write($"assets: archives the game would load: {index.Gnrl.Count} general (GNRL) and {index.Dx10.Count} texture (DX10), " +
                  $"{ArchiveSlots} slots each{(names ? "" : " (types only: --no-assets)")}");
        return index;
    }

    /// <summary>"Voices_de" when the language is "en", and the like. A suffix that isn't a voice archive is kept.</summary>
    static bool OtherLanguageVoices(string suffix, string language) =>
        suffix.StartsWith("Voices_", StringComparison.OrdinalIgnoreCase) && !suffix[7..].Equals(language, StringComparison.OrdinalIgnoreCase);

    /// <summary>The archive names the INIs list, as (key, archive name, INI file). A later INI's key replaces an earlier one's.</summary>
    public static List<(string Key, string Name, string Ini)> IniArchives(IEnumerable<string> inis)
    {
        var byKey = new Dictionary<string, (string Value, string Ini)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ini in inis)
        {
            if (!File.Exists(ini)) { Log.Write($"assets: INI {ini} not found, skipped"); continue; }
            string[] lines;
            try { lines = File.ReadAllLines(ini); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"assets: INI {ini} could not be read ({e.Message}), skipped"); continue; }
            Log.Write($"assets: INI {ini}");
            string section = "";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                if (!section.Equals("Archive", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (IniKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) byKey[key] = (line[(eq + 1)..], ini);
            }
        }
        var list = new List<(string, string, string)>();
        foreach (var key in IniKeys)
            if (byKey.TryGetValue(key, out var v))
                foreach (var name in v.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
                    list.Add((key, name, v.Ini));
        return list;
    }

    /// <summary>The default INIs: Starfield.ini in the game folder (the Data folder's parent), then StarfieldCustom.ini
    /// in Documents\My Games\Starfield, then any --ini files.</summary>
    public static List<string> DefaultInis(string dataDir, IEnumerable<string> extra)
    {
        var list = new List<string>();
        var game = Path.GetDirectoryName(Path.GetFullPath(dataDir).TrimEnd('\\', '/'));
        if (game != null) list.Add(Path.Combine(game, "Starfield.ini"));
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (docs.Length > 0) list.Add(Path.Combine(docs, "My Games", "Starfield", "StarfieldCustom.ini"));
        list.AddRange(extra);
        return list;
    }

    // 0.14.0: the archives named after each plugin (its own files, opened for their .nif and .mat links), and every
    // material database (.cdb) in a loaded general archive or loose under a searched folder (relay step 10: plugins
    // ship their own, such as materials\creations\<plugin>\materialsbeta.cdb)
    readonly Dictionary<string, List<string>> ownArchives = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string File, string? Entry)> cdbs = new();
    readonly HashSet<string> cdbKeys = new(StringComparer.OrdinalIgnoreCase);
    HashSet<ulong>? cdbIds;
    bool cdbTried;
    int cdbsRead, failedLogged;   // how many of cdbs were read, and how many failures were logged
    readonly List<string> cdbFailed = new();

    /// <summary>release (0.14.0): names found outside the index (the game's and --masters-dir archives) count as found.</summary>
    public Func<string, bool>? Fallback { get; set; }

    /// <summary>Adds a material database to read: an entry (data-relative name) in a general archive, or a loose file
    /// (entry null). The same one added twice is read once.</summary>
    public void AddCdb(string file, string? entry)
    {
        if (cdbKeys.Add(Path.GetFullPath(file) + "|" + entry)) cdbs.Add((file, entry));
    }

    /// <summary>Why no material database could be used (null when one was read or none was needed yet).</summary>
    public string? CdbProblem { get; private set; }

    /// <summary>Whether a loaded material database holds this material (by its resource ID); null when no database is
    /// in a loaded archive or loose, or none could be read (CdbProblem says why). Each is read once, on first use, and
    /// their IDs joined; one added after that (release adds each plugin's Data masters' databases as it goes) is read
    /// at the next lookup (code review 0.14.0).</summary>
    public bool? InCdb(string materialPath)
    {
        if (!cdbTried || cdbsRead < cdbs.Count)
        {
            cdbTried = true;
            var failed = cdbFailed;
            var fresh = cdbs.Skip(cdbsRead).ToList();
            cdbsRead = cdbs.Count;
            foreach (var (file, entry) in fresh)
            {
                var label = entry == null ? file : $"{entry} in {Path.GetFileName(file)}";
                try
                {
                    byte[]? bytes;
                    if (entry == null)
                    {
                        if (new FileInfo(file).Length > int.MaxValue) throw new InvalidDataException("it is larger than 2 GB");
                        bytes = File.ReadAllBytes(file);
                    }
                    else bytes = AssetFiles.FromArchive(file, entry);
                    if (bytes == null) { failed.Add($"{label} (listed but not found when read)"); continue; }
                    var ids = AssetFiles.CdbIds(bytes);
                    (cdbIds ??= new()).UnionWith(ids);
                    Log.Write($"assets: material database {label}: {bytes.Length:N0} bytes, {ids.Count:N0} material IDs");
                }
                catch (Exception e) when (Fatal.Recoverable(e)) { failed.Add($"{label} ({e.GetType().Name}: {SafeText.OneLine(e.Message)})"); }
            }
            foreach (var f in failed.Skip(failedLogged)) Log.Write($"assets: material database {f} could not be read");
            failedLogged = failed.Count;
            CdbProblem = null;
            if (cdbIds != null) Log.Write($"assets: {cdbs.Count - failed.Count} material database(s) read, {cdbIds.Count:N0} material IDs in all");
            else CdbProblem = cdbs.Count == 0
                ? $"no material database (.cdb, such as the game's {AssetFiles.CdbPath}) is in a loaded archive or loose"
                : $"no material database could be read ({string.Join("; ", failed)})";
            if (CdbProblem != null) Log.Write($"assets: {CdbProblem}; materials are checked against files only");
        }
        return cdbIds?.Contains(AssetFiles.MaterialId(materialPath));
    }

    /// <summary>release (0.14.0): the zip roots whose loose sound banks are the current plugin's own and are read (its
    /// own zip's, set per plugin, so a bank is reported under the plugin it ships with). Null in check and conflict,
    /// where the loose folders are Data, shared by every mod, so only the banks in a plugin's own archives are
    /// read.</summary>
    public List<string>? OwnLooseDirs { get; set; }

    readonly HashSet<string> banksTaken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True the first time a bank (by its archive and name, or its loose path) is taken, so a bank is checked
    /// once per run even when several plugins share the loose files (release with several zips).</summary>
    public bool TakeBank(string key) => banksTaken.Add(key);

    /// <summary>The loose sound banks under OwnLooseDirs ("sound\soundbanks\*.bnk", any case), as (data-relative
    /// name, full path); a name found in an earlier folder wins.</summary>
    public List<(string Rel, string Path)> LooseBanks()
    {
        var list = new List<(string, string)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ci = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var d in OwnLooseDirs ?? [])
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                foreach (var sound in Directory.EnumerateDirectories(d, "sound", ci))
                    foreach (var banks in Directory.EnumerateDirectories(sound, "soundbanks", ci))
                        foreach (var f in Directory.EnumerateFiles(banks, "*.bnk", ci).Order(StringComparer.Ordinal))
                        {
                            var rel = Ba2Names.Normalize(@"sound\soundbanks\" + Path.GetFileName(f));
                            if (names.Add(rel)) list.Add((rel, f));
                        }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"assets: sound banks under {d} couldn't be listed ({e.Message})"); }
        }
        return list;
    }

    /// <summary>The general archives named after this plugin (its own archives), in the order found.</summary>
    public IReadOnlyList<string> OwnArchives(string plugin) => ownArchives.TryGetValue(plugin, out var l) ? l : Array.Empty<string>();

    /// <summary>The full path of a loose file for a data-relative path, or null (under the same rules as Has).</summary>
    public string? LoosePath(string relPath)
    {
        var p = Ba2Names.Normalize(relPath);
        if (Path.IsPathRooted(p) || p.Contains(':') || p.Split('\\').Any(s => s == ".." || s.Length > 0 && s != "." && (s.EndsWith('.') || s.EndsWith(' '))))
            return null;
        foreach (var d in looseDirs)
        {
            try { if (LooseFile(d, p) is { } f) return f; }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Whether the game can find this data-relative file ("scripts\foo.pex", "meshes\a\b.nif").</summary>
    public bool Has(string relPath) => Has(relPath, true);

    /// <summary>Has without Fallback: only this index's own loose folders and archives (in `release`, the zips' own
    /// files; 0.15.0, for the stale-face note).</summary>
    public bool HasOwn(string relPath) => Has(relPath, false);

    bool Has(string relPath, bool fallback)
    {
        var p = Ba2Names.Normalize(relPath);
        if (inArchives.Contains(p)) return true;
        if (fallback && Fallback?.Invoke(p) == true) return true;
        if (looseSeen.TryGetValue(p, out var known)) return known;
        // A rooted path ("c:\x\a.nif") or one with ".." points outside Data: the game won't load it, so a file that
        // happens to be there on this machine doesn't count (code review 0.6.0)
        // A part ending in a dot or space ("foo.", ".. ") never counts either: Windows trims those and Linux doesn't, so
        // the two would disagree, and ".. " would climb out of Data on Windows (code review 0.6.0)
        if (Path.IsPathRooted(p) || p.Contains(':') ||
            p.Split('\\').Any(s => s == ".." || s.Length > 0 && s != "." && (s.EndsWith('.') || s.EndsWith(' '))))
            return looseSeen[p] = false;
        var found = false;
        foreach (var d in looseDirs)
        {
            try { if (LooseFile(d, p) != null) { found = true; break; } }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }   // a path no file can have
        }
        return looseSeen[p] = found;
    }

    /// <summary>Whether a data-relative path has a part that ends in a dot or a space ("meshes\armor.\helm.nif").
    /// Such a path never counts as found loose (see Has); the assets check reports it with its own text (0.10.0).</summary>
    public static bool OddPart(string relPath) =>
        Ba2Names.Normalize(relPath).Split('\\').Any(s => s.Length > 0 && s != "." && s != ".." && (s.EndsWith('.') || s.EndsWith(' ')));

    /// <summary>The path Windows opens for a data-relative path (0.10.0, tested on Windows 11 2026-11-21 with
    /// File.Exists and cmd): the last part loses all its trailing dots and spaces ("helm.nif. " -> "helm.nif"); any
    /// other part loses one trailing dot unless a dot comes before it ("armor." -> "armor", "armor.." and "armor "
    /// stay as they are). Archives look a name up exactly, so only loose files are found this way.</summary>
    public static string WindowsTrim(string relPath)
    {
        var parts = Ba2Names.Normalize(relPath).Split('\\');
        for (int i = 0; i < parts.Length; i++)
        {
            var s = parts[i];
            if (s.Length == 0 || s == "." || s == "..") continue;
            if (i == parts.Length - 1) parts[i] = s.TrimEnd('.', ' ');
            else if (s.EndsWith('.') && (s.Length < 2 || s[^2] != '.')) parts[i] = s[..^1];
        }
        return string.Join('\\', parts);
    }

    /// <summary>For a path with a part ending in a dot or a space: the trimmed path Windows would open, when that is a
    /// loose file the game can find (under the same rules as Has); otherwise null.</summary>
    public string? TrimmedLoose(string relPath)
    {
        var p = Ba2Names.Normalize(relPath);
        if (!OddPart(p)) return null;
        var t = WindowsTrim(p);
        if (t == p || t.Length == 0 || t.EndsWith('\\') || OddPart(t)) return null;
        if (Path.IsPathRooted(t) || t.Contains(':') || t.Split('\\').Contains("..")) return null;
        foreach (var d in looseDirs)
        {
            try { if (LooseFile(d, t) != null) return t; }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    // folder -> its entries by name, matched without regard to case; several when names differ only in case (Linux and
    // macOS only)
    readonly Dictionary<string, Dictionary<string, List<string>>> listings = new(StringComparer.Ordinal);

    /// <summary>Sub-folders the Linux and macOS lookup couldn't list (each is logged once).</summary>
    public readonly List<string> Unlistable = new();

    /// <summary>The loose file for a data-relative path ("scripts\foo.pex") under a folder, or null. Windows paths
    /// ignore case and take "\" as a separator; on Linux and macOS (Proton, Wine) each part is matched without regard
    /// to case, as the game sees it, so "Scripts/Foo.pex" on disk counts (CI on ubuntu, 0.6.0). When folders differ only
    /// in case ("Scripts" and "scripts", from two mods), each is searched. One leading "\" or "/" means under Data; on
    /// Windows a drive or network path (an INI's archive list may hold one) is looked up as given, and off Windows it is
    /// not found.</summary>
    internal string? LooseFile(string dir, string rel)
    {
        static bool Sep(char c) => c is '\\' or '/';
        // a "\\server\share" name (either slash) is a network path, never a folder under Data
        var network = rel.Length >= 2 && Sep(rel[0]) && Sep(rel[1]);
        // one leading "\" or "/" ("\x.ba2" in an INI) means under Data on every system, not the drive's root (code
        // review 0.6.0; "/" since 0.10.0, which Windows reads the same way)
        if (!network && rel.Length >= 1 && Sep(rel[0])) rel = rel[1..];
        if (OperatingSystem.IsWindows()) { var w = Path.Combine(dir, rel); return File.Exists(w) ? w : null; }
        // off Windows: a network name or a Windows drive path ("C:\Mods\x.ba2", as a Proton INI may hold) has no
        // Linux path we can know (0.10.0: a "\\server" name was looked up under Data before)
        if (network || (rel.Length >= 2 && rel[1] == ':')) return null;
        // "." parts name the same folder; a ".." part is never followed (code review 0.6.0)
        var parts = rel.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        return parts.Length == 0 || parts.Contains("..") ? null : Walk(dir, parts, 0);
    }

    string? Walk(string cur, string[] parts, int i)
    {
        if (!listings.TryGetValue(cur, out var entries))
        {
            entries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(cur))
                    foreach (var e in Directory.EnumerateFileSystemEntries(cur))
                    {
                        var name = Path.GetFileName(e);
                        if (!entries.TryGetValue(name, out var same)) entries[name] = same = new List<string>();
                        same.Add(e);
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a folder it can't list has nothing found in it; the log says why its files count as missing (0.10.0)
                Unlistable.Add(cur);
                Log.Write($"assets: folder {cur} can't be listed ({e.GetType().Name}: {e.Message}); loose files under it count as missing");
            }
            listings[cur] = entries;
        }
        if (!entries.TryGetValue(parts[i], out var candidates)) return null;
        var last = i == parts.Length - 1;
        // exact case first, then a fixed order (Has passes lower-cased paths, so there it only makes the order fixed)
        foreach (var next in candidates.OrderBy(c => Path.GetFileName(c) == parts[i] ? 0 : 1).ThenBy(c => c, StringComparer.Ordinal))
        {
            if (last) { if (File.Exists(next)) return next; continue; }
            var found = Walk(next, parts, i + 1);
            if (found != null) return found;
        }
        return null;
    }
}

public static class Assets
{
    /// <summary>A script name as its compiled file: "Foo:Bar" becomes "scripts\foo\bar.pex" (the rule package-check uses).</summary>
    public static string PexPath(string script) => Ba2Names.Normalize("scripts\\" + script.Replace(':', '\\') + ".pex");

    /// <summary>Every script name a record's script data names: attached scripts and the fragment files of quests,
    /// scenes, perks, topic infos and packages. Read through Mutagen's objects, so nothing depends on the raw layout.</summary>
    public static IEnumerable<string> Scripts(IMajorRecordGetter rec)
    {
        if (rec is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } vmad }) return Array.Empty<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(vmad, names, 0);
        return names;
    }

    // Script entries carry the name; fragment blocks carry it as FileName or ScriptName. Only Mutagen's own Starfield
    // objects and lists of them are walked, a few levels deep.
    static void Walk(object? o, HashSet<string> names, int depth)
    {
        if (o is null || depth > 6) return;
        if (o is IScriptEntryGetter se && !string.IsNullOrWhiteSpace(se.Name)) names.Add(se.Name.Trim());
        if (o is string || o.GetType().IsPrimitive || o is IMajorRecordGetter && depth > 0 || o is IFormLinkGetter) return;
        if (o is System.Collections.IEnumerable list)
        {
            foreach (var x in list) Walk(x, names, depth + 1);
            return;
        }
        var t = o.GetType();
        if (t.Namespace?.StartsWith("Mutagen.Bethesda.Starfield", StringComparison.Ordinal) != true) return;
        foreach (var p in Props(t))
        {
            object? v;
            try { v = p.GetValue(o); }
            catch (Exception e) when (Fatal.Recoverable(e)) { continue; }
            if (v is string s) { if ((p.Name == "FileName" || p.Name == "ScriptName") && !string.IsNullOrWhiteSpace(s)) names.Add(s.Trim()); }
            else if (v is not null && !v.GetType().IsPrimitive && !v.GetType().IsEnum) Walk(v, names, depth + 1);
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> props = new();
    static System.Reflection.PropertyInfo[] Props(Type t) => props.GetOrAdd(t, t => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name is not ("Registration" or "StaticRegistration")).ToArray());

    /// <summary>Every model path a record names, data-relative ("meshes\a\b.nif").</summary>
    public static IEnumerable<string> Models(IMajorRecordGetter rec)
    {
        if (rec is not IAssetLinkContainerGetter c) return Array.Empty<string>();
        return c.EnumerateListedAssetLinks().Where(l => l.AssetTypeInstance is StarfieldModelAssetType)
            .Select(l => l.DataRelativePath.Path).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Ba2Names.Normalize).Distinct();
    }

    /// <summary>Checks one plugin's own records (overrides included). add: (severity, check, text). masters: the
    /// plugin's masters, to find the version an override replaces (see Find).</summary>
    public static (int Scripts, int Models) Check(IModGetter mod, AssetIndex index, Func<IMajorRecordGetter, string> name, Action<string, string, string> add,
                                                 ILinkCache? masters = null, AssetUses? uses = null)
    {
        uses ??= new AssetUses();
        var (scripts, models, missing, inherited) = Find(mod, index, name, masters == null ? null : rec => Resolve(masters, rec), uses);
        if (inherited > 0) Log.Write($"assets: {inherited} missing path use(s) not reported: the master version of the record already names them");
        foreach (var m in missing) add(m.Severity, "assets", m.Text);
        // check has no NOTE level: a file it couldn't read, or materials it couldn't check, is a WARNING (0.14.0)
        foreach (var n in uses.Notes.Concat(uses.SoundNotes)) add("WARNING", "assets", n);
        foreach (var (a, why) in index.Unreadable)
            add("WARNING", "assets", UnreadableText(a, why));
        return (scripts, models);
    }

    public static string UnreadableText(string archive, string why) => $"archive {archive} could not be read ({why}), so its files count as missing";

    /// <summary>Tests only: runs in CheckLoadOrder after the index and the archive counts (to stop the step part way).</summary>
    internal static Action? AfterIndex;

    /// <summary>conflict mode (0.6.0 part 2): the index from every plugin in the load order (their archives load, and
    /// the INIs' lists), then each mod plugin's own records checked against it. Official plugins aren't checked: their
    /// few missing files are Bethesda's and nobody can act on them (as with their ITMs). dirs: folders searched for
    /// loose files and archives, in order (the --extra-data folder, then Data).</summary>
    public static void CheckLoadOrder(IReadOnlyList<Conflict.PluginInfo> plugins, IReadOnlyList<string> dirs, IEnumerable<string> inis,
                                      string language, Conflict.Analysis analysis)
    {
        var index = AssetIndex.Build(plugins.Select(p => p.FileName), dirs, inis, language);
        analysis.AssetsChecked = true;
        analysis.ArchivesRead = index.Loaded.Count;
        analysis.UnreadableArchives.AddRange(index.Unreadable);
        SetArchiveCounts(index, analysis);
        AfterIndex?.Invoke();
        int scripts = 0, models = 0, checkedPlugins = 0, inheritedAll = 0;
        // The version an override replaces: the record in the last of the plugin's masters (in load order) that has it,
        // as check mode does with the masters' link cache (code review 2026-11-18: a DLC or master mod that changed the
        // record counts, not only the plugin that defines the FormID). One link cache per master, made only when a
        // missing path needs it (a large load order, 2026-11-18: mods that override vanilla MQ305, City_NA_Well01 and two
        // MagPerk perks carry Bethesda's own references to scripts the game doesn't ship). A cache of Starfield.esm can
        // be large, so --max-memory is checked before each lookup; past it the lookups stop and missing paths are all
        // reported (the safe side), with a note in the errors.
        var byName = plugins.ToDictionary(p => p.FileName, p => p, StringComparer.OrdinalIgnoreCase);
        var caches = new Dictionary<string, ILinkCache>(StringComparer.OrdinalIgnoreCase);
        var lookupsStopped = false;
        IMajorRecordGetter? Original(Conflict.PluginInfo p, IMajorRecordGetter rec)
        {
            if (lookupsStopped) return null;
            foreach (var master in p.Masters.Select(n => byName.GetValueOrDefault(n)).OfType<Conflict.PluginInfo>().OrderByDescending(x => x.Index))
            {
                if (Conflict.Analyzer.OverMemoryLimit())
                {
                    lookupsStopped = true;
                    caches.Clear();
                    var note = $"the assets check passed --max-memory ({Conflict.Analyzer.MaxManagedBytes / 1e9:F1} GB) while looking up the records " +
                               $"overrides replace (at {p.FileName}); from there on a missing path an override inherits is reported too";
                    analysis.Errors.Add(note);
                    analysis.AssetNotes++;
                    Log.Write("assets: " + note);
                    return null;
                }
                if (!caches.TryGetValue(master.FileName, out var cache))
                    caches[master.FileName] = cache = ((IStarfieldModGetter)master.Mod).ToImmutableLinkCache();
                if (Resolve(cache, rec) is { } o) return o;
            }
            return null;
        }
        foreach (var p in plugins)
        {
            if (p.Official) { Log.Write($"assets: {p.FileName} not checked (official plugin)"); continue; }
            checkedPlugins++;
            var uses = new AssetUses();
            var (s, m, missing, inherited) = Find(p.Mod, index, Checker.Name, rec => Original(p, rec), uses);
            scripts += s; models += m; inheritedAll += inherited;
            analysis.AssetTextureUses += uses.Textures; analysis.AssetMaterialUses += uses.Materials; analysis.AssetSoundUses += uses.Sounds;
            analysis.AssetMeshUses += uses.Meshes;
            foreach (var f in missing) analysis.MissingAssets.Add(new Conflict.AssetFinding(p.FileName, f));
            // a file it couldn't read, or materials it couldn't check, goes to "Tool limits hit" (0.14.0)
            foreach (var n in uses.Notes.Concat(uses.SoundNotes)) { analysis.Errors.Add($"assets: {p.FileName}: {n}"); analysis.AssetNotes++; }
            if (missing.Count > 0 || inherited > 0)
                Log.Write($"assets: {p.FileName}: {missing.Count} missing ({s} script uses, {m} model uses, {uses.Textures} textures, {uses.Materials} materials, {uses.Meshes} meshes, {uses.Sounds} sounds)" +
                          (inherited > 0 ? $"; {inherited} missing path use(s) not reported: the original record already names them" : ""));
        }
        caches.Clear();   // the masters' link caches aren't needed for the report
        analysis.AssetScriptUses = scripts; analysis.AssetModelUses = models; analysis.AssetsInherited = inheritedAll;
        Log.Write($"assets: {checkedPlugins} mod plugins checked, {scripts} script uses, {models} model uses, " +
                  $"{analysis.AssetTextureUses} texture paths, {analysis.AssetMaterialUses} material paths, {analysis.AssetMeshUses} mesh paths, {analysis.AssetSoundUses} streamed sounds, " +
                  $"{analysis.MissingAssets.Count} missing ({analysis.MissingAssets.Count(f => f.Asset.Kind == "script")} scripts), " +
                  $"{index.Unreadable.Count} unreadable archives");
    }

    /// <summary>conflict --no-assets: only the archive counts (each archive's 24-byte header), so the archive-limit
    /// warning still works when no asset is checked.</summary>
    public static void CountArchives(IReadOnlyList<Conflict.PluginInfo> plugins, IReadOnlyList<string> dirs, IEnumerable<string> inis,
                                     string language, Conflict.Analysis analysis)
    {
        var index = AssetIndex.Build(plugins.Select(p => p.FileName), dirs, inis, language, names: false);
        // an archive whose header can't be read isn't in either count, so the report must say so (code review 0.6.0)
        analysis.UnreadableArchives.AddRange(index.Unreadable);
        SetArchiveCounts(index, analysis);
    }

    static void SetArchiveCounts(AssetIndex index, Conflict.Analysis analysis)
    {
        analysis.ArchivesCounted = true;
        analysis.GnrlArchives = index.Gnrl.Count;
        analysis.Dx10Archives = index.Dx10.Count;
    }

    /// <summary>The archive-limit warnings (0.6.0 part 2): one per registry over the game's 256 slots, else none.</summary>
    public static List<string> ArchiveLimitWarnings(Conflict.Analysis a)
    {
        var list = new List<string>();
        if (!a.ArchivesCounted) return list;
        void One(int n, string kind, string what)
        {
            if (n > AssetIndex.ArchiveSlots)
                list.Add($"{n} {kind} archives load, over the game's {AssetIndex.ArchiveSlots} slots for them by {n - AssetIndex.ArchiveSlots}: {what}. " +
                         "Remove or merge mods that bring archives, or use a BA2 limit fix");
        }
        One(a.GnrlArchives, "general (GNRL)", "the extra ones write past the game's archive table (memory damage, crashes)");
        One(a.Dx10Archives, "texture (DX10)", "the extra ones wrap around to slot 0 (wrong textures or crashes)");
        return list;
    }

    static IMajorRecordGetter? Resolve(ILinkCache cache, IMajorRecordGetter rec) =>
        cache.TryResolve(rec.FormKey, rec.Registration.GetterType, out var o) ? o : null;

    /// <summary>One plugin's missing scripts, models, materials, textures and sounds, each path once with the records that use it. original: for an
    /// override, the version it replaces (null when unknown). A missing path that version names too is inherited, not
    /// the plugin's own mistake (often Bethesda's: vanilla records that attach scripts the game doesn't ship), so it
    /// isn't reported; Inherited counts those uses. The lookup runs only for missing paths. faces: also look for NPC face
    /// data (0.14.0; only release asks for it, see FindFaces).</summary>
    public static (int Scripts, int Models, List<MissingAsset> Missing, int Inherited) Find(IModGetter mod, AssetIndex index, Func<IMajorRecordGetter, string> name,
        Func<IMajorRecordGetter, IMajorRecordGetter?>? original = null, AssetUses? uses = null, bool faces = false)
    {
        uses ??= new AssetUses();
        int inherited = 0;
        bool Inherited(IMajorRecordGetter rec, string path, string kind)
        {
            if (original == null || rec.FormKey.ModKey == mod.ModKey) return false;
            try
            {
                var o = original(rec);
                if (o == null || ReferenceEquals(o, rec)) return false;
                var same = kind switch
                {
                    "script" => Scripts(o).Any(s => PexPath(s) == path),
                    "model" => Models(o).Contains(path),
                    _ => TexturesAndMaterials(o).Contains((kind, path)),
                };
                if (same) { inherited++; Log.Write($"assets: {name(rec)}: {path} is missing, but the record it overrides names it too: not reported"); }
                return same;
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: {name(rec)}: could not read the record it overrides ({e.GetType().Name}: {e.Message})"); return false; }
        }
        var missingScripts = new Dictionary<string, (string Script, List<string> Users)>(StringComparer.Ordinal);
        var missingModels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var missingOther = new Dictionary<(string Kind, string Path), List<string>>();
        // 0.14.0: the models found, to read for their materials, and the textures and materials still to look up,
        // each with who names it (a record, or a .nif or .mat of the plugin's own)
        var nifs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var wanted = new List<(string Kind, string Path, string User, IMajorRecordGetter? Rec)>();
        int scripts = 0, models = 0;
        var npcs = new List<INpcGetter>();
        // Records are read lazily: a damaged one stops the walk. The read check already reports it as an ERROR, so the
        // records read up to there are checked and the stop is only logged.
        try
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                IEnumerable<string> ss, ms;
                List<(string Kind, string Path)> tm;
                try { ss = Scripts(rec).ToList(); ms = Models(rec).ToList(); tm = TexturesAndMaterials(rec).ToList(); }
                catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: {name(rec)}: could not list its assets ({e.GetType().Name}: {e.Message})"); continue; }
                foreach (var s in ss)
                {
                    scripts++;
                    var pex = PexPath(s);
                    if (index.Has(pex) || Inherited(rec, pex, "script")) continue;
                    if (!missingScripts.TryGetValue(pex, out var m)) missingScripts[pex] = m = (s, new());
                    m.Users.Add(name(rec));
                }
                foreach (var p in ms)
                {
                    models++;
                    if (index.Has(p))
                    {
                        if (!nifs.TryGetValue(p, out var nu)) nifs[p] = nu = new();
                        nu.Add(name(rec));
                        continue;
                    }
                    if (Inherited(rec, p, "model")) continue;
                    if (!missingModels.TryGetValue(p, out var u)) missingModels[p] = u = new();
                    u.Add(name(rec));
                }
                foreach (var (k, p) in tm) wanted.Add((k, p, name(rec), rec));
                if (faces && rec is INpcGetter npc) npcs.Add(npc);
            }
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: stopped at a record Mutagen can't read ({e.GetType().Name}: {e.Message}); the read check lists it"); }

        // The plugin's own files (0.14.0, D2b): the .nif and .mat entries of the archives named after it, and loose
        // files, opened only when a record, a .nif or a .mat of the plugin's own names them. Other files are looked up by
        // name only.
        var own = new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal);
        var ownIn = new Dictionary<string, string>(StringComparer.Ordinal);   // entry -> its archive's file name
        foreach (var ba2 in index.OwnArchives(mod.ModKey.FileName))
        {
            // an archive the index couldn't read already has its own WARNING
            if (index.Unreadable.Any(u => u.Archive.Equals(Path.GetFileName(ba2), StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (Ba2Names.Type(ba2) != "GNRL") continue;
                foreach (var (n, bytes) in AssetFiles.ArchiveModelFiles(ba2))
                    if (own.TryAdd(n, bytes)) ownIn[n] = Path.GetFileName(ba2);
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                uses.Notes.Add($"couldn't read the files in {Path.GetFileName(ba2)} ({e.GetType().Name}: {SafeText.OneLine(e.Message)}), so the materials, textures and sounds its files name weren't checked");
            }
        }
        // generated LOD meshes (meshes\lod\) that no record names are left out, as LOD is (D8; real data 2026-11-23:
        // Shattered Space's LOD meshes name 39 composite materials found in no file or database)
        int lodSkipped = 0;
        foreach (var n in own.Keys.Where(n => n.EndsWith(".nif", StringComparison.Ordinal)))
        {
            if (n.StartsWith(@"meshes\lod\", StringComparison.Ordinal) && !nifs.ContainsKey(n)) { lodSkipped++; continue; }
            nifs.TryAdd(n, new());
        }
        if (lodSkipped > 0) Log.Write($"assets: {lodSkipped} LOD mesh(es) under meshes\\lod\\ in {mod.ModKey.FileName}'s own archives not read (LOD isn't checked)");
        // a .mat in its own archive that nothing of its own names (a material for a vanilla model) is read too, for its
        // textures (real data, 2026-11-23: a mod that ships only two .mat files)
        foreach (var n in own.Keys.Where(n => n.EndsWith(".mat", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            wanted.Add(("material", n, ownIn[n], null));
        byte[]? Open(string rel, out string? where)
        {
            where = null;
            byte[]? data = null;
            try
            {
                if (own.TryGetValue(rel, out var get)) { where = ownIn[rel]; data = get(); }
                else if (index.LoosePath(rel) is { } loose)
                {
                    where = loose;
                    if (new FileInfo(loose).Length > AssetFiles.MaxFileBytes) throw new InvalidDataException($"it is larger than {AssetFiles.MaxFileBytes / (1024 * 1024)} MB");
                    data = File.ReadAllBytes(loose);
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { uses.Notes.Add(Unread(rel, e)); return null; }
            if (data != null) uses.FilesRead++;
            return data;
        }
        foreach (var (nif, users) in nifs)
        {
            var data = Open(nif, out var where);
            if (data == null) continue;
            try
            {
                var (mats, meshes, meshProblem) = AssetFiles.NifLinks(data);
                Log.Write($"assets: {nif} (from {where}): {mats.Count} material(s), {meshes.Count} geometry file(s)");
                foreach (var m in mats) wanted.Add(("material", m, nif, null));
                // 0.15.0: the .mesh files its BSGeometry blocks name, looked up by name (kind "mesh", a WARNING)
                foreach (var m in meshes) wanted.Add(("mesh", m, nif, null));
                if (meshProblem != null) uses.Notes.Add($"couldn't read the geometry names in {nif} ({meshProblem}), so the .mesh files it names weren't checked");
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { uses.Notes.Add(Unread(nif, e)); }
        }
        var seen = new HashSet<(string, string)>();
        var matFiles = new Dictionary<string, bool>(StringComparer.Ordinal);   // material -> a file that was opened
        for (int i = 0; i < wanted.Count; i++)
        {
            var (kind, path, user, rec) = wanted[i];
            if (seen.Add((kind, path)))
            {
                if (kind == "texture") uses.Textures++; else if (kind == "mesh") uses.Meshes++; else uses.Materials++;
            }
            bool found;
            string how;
            // each material file is opened and read once, however many records or files name it (code review 0.14.0)
            var isFile = false;
            if (kind == "material" && !matFiles.TryGetValue(path, out isFile))
            {
                if (Open(path, out var where) is { } mat)
                {
                    isFile = true;
                    try
                    {
                        var (tex, parents) = AssetFiles.MatLinks(mat);
                        Log.Write($"assets: {path} (from {where}): {tex.Count} texture(s), {parents.Count} parent material(s)");
                        foreach (var t in tex) wanted.Add(("texture", t, path, null));
                        foreach (var p in parents) wanted.Add(("material", p, path, null));
                    }
                    catch (Exception e) when (Fatal.Recoverable(e) || e is JsonException) { uses.Notes.Add(Unread(path, e)); }
                }
                matFiles[path] = isFile;
            }
            if (isFile) { found = true; how = "a file of the plugin's own, read"; }
            else if (index.Has(path)) { found = true; how = "found by name"; }
            else if (kind == "material" && index.InCdb(path) is { } inCdb) { found = inCdb; how = inCdb ? "in a loaded material database" : "not a file and not in any loaded material database"; }
            else if (kind == "material") { found = true; how = "not a file; the material database couldn't be checked"; if (seen.Add(("unverified", path))) uses.Unverified.Add(path); }
            else { found = false; how = "not found"; }
            Log.Write($"assets: {kind} {path} (named by {user}): {how}");
            if (found || rec != null && Inherited(rec, path, kind)) continue;
            if (!missingOther.TryGetValue((kind, path), out var mu)) missingOther[(kind, path)] = mu = new();
            mu.Add(user);
        }
        // Sound banks of the plugin's own (0.14.0, D6): each streamed source must be a .wem the game can find. Banks in
        // its own archives, and in release its zip's loose banks too (OwnLooseDirs).
        var banks = own.Keys.Where(AssetFiles.IsBank).Order(StringComparer.Ordinal)
            .Select(n => (Rel: n, Key: ownIn[n] + "|" + n, Where: ownIn[n], Get: own[n])).ToList();
        foreach (var (rel, path) in index.LooseBanks())
            banks.Add((rel, path, path, () =>
            {
                if (new FileInfo(path).Length > AssetFiles.MaxFileBytes) throw new InvalidDataException($"it is larger than {AssetFiles.MaxFileBytes / (1024 * 1024)} MB");
                return File.ReadAllBytes(path);
            }));
        foreach (var (rel, key, where, get) in banks)
        {
            if (!index.TakeBank(key)) continue;
            uint version;
            List<uint> streamed;
            try { (version, streamed) = AssetFiles.BankSources(get()); uses.FilesRead++; }
            catch (Exception e) when (Fatal.Recoverable(e)) { uses.SoundNotes.Add(Unread(rel, e, "sounds")); continue; }
            if (!AssetFiles.ProvenBankVersions.Contains(version))
            {
                uses.SoundNotes.Add($"{rel} is a Wwise sound bank of version {version}, and only version {string.Join(", ", AssetFiles.ProvenBankVersions)} " +
                                    "(the game's own) is known, so the sounds it streams weren't checked");
                Log.Write($"assets: {rel} (from {where}): bank version {version} is not a known one; not checked");
                continue;
            }
            int lacking = 0;
            foreach (var id in streamed)
            {
                uses.Sounds++;
                var wem = AssetFiles.WemPath(id);
                if (index.Has(wem)) continue;
                lacking++;
                Log.Write($"assets: sound {wem} (streamed by {rel}): not found");
                if (!missingOther.TryGetValue(("sound", wem), out var su)) missingOther[("sound", wem)] = su = new();
                su.Add(rel);
            }
            Log.Write($"assets: {rel} (from {where}): bank version {version}, {streamed.Count} streamed sound(s), {lacking} not found");
        }
        var faceFindings = FindFaces(mod, npcs, index, name, original, uses);
        if (uses.Unverified.Count > 0)
            uses.Notes.Add($"{uses.Unverified.Count} material path(s) are neither loose files nor in a loaded archive, and {index.CdbProblem}, so they couldn't be checked " +
                           $"against material databases (for example {uses.Unverified[0]}; --verbose lists them all)");

        var missing = new List<MissingAsset>();
        foreach (var (pex, (script, users)) in missingScripts)
            missing.Add(Odd(new MissingAsset("ERROR", "script", pex, script, users.Distinct().ToList()), index));
        foreach (var (path, users) in missingModels)
            missing.Add(Odd(new MissingAsset("WARNING", "model", path, null, users.Distinct().ToList()), index));
        foreach (var ((kind, path), users) in missingOther.OrderBy(x => x.Key.Kind == "material" ? 0 : 1).ThenBy(x => x.Key.Path, StringComparer.Ordinal))
            missing.Add(Odd(new MissingAsset("WARNING", kind, path, null, users.Distinct().ToList()), index));
        missing.AddRange(faceFindings);
        return (scripts, models, missing, inherited);
    }

    /// <summary>The four face textures per NPC, after its 8-hex ID (0.14.0, D5).</summary>
    internal static readonly string[] FaceTextureSuffixes = ["_ao", "_color", "_normal", "_rough"];

    internal static string FaceNif(string plugin, uint id) => $@"meshes\actors\character\facegendata\facegeom\{plugin.ToLowerInvariant()}\{id:x8}.nif";
    internal static string FaceTexture(string plugin, uint id, string suffix) => $@"textures\actors\character\facecustomization\{plugin.ToLowerInvariant()}\{id:x8}{suffix}.dds";

    // Tints is null when the NPC has no tint subrecords (Mutagen 0.54.4).
    static bool HasFace(INpcGetter n) => n.HeadParts.Count > 0 || n.Tints?.Count > 0 || n.FaceMorphs.Count > 0 || n.MorphBlends.Count > 0;

    static bool FaceDiffers(INpcGetter a, INpcGetter b) =>
        !a.HeadParts.Select(h => h.FormKey).SequenceEqual(b.HeadParts.Select(h => h.FormKey)) || !(a.Tints ?? []).SequenceEqual(b.Tints ?? []) ||
        !a.FaceMorphs.SequenceEqual(b.FaceMorphs) || !a.MorphBlends.SequenceEqual(b.MorphBlends);

    /// <summary>Face data (0.14.0, D5; release only, as a NOTE). An NPC the plugin
    /// adds with its own head parts, tints, face morphs or morph blends, or an override that changes them, is looked
    /// up for its FaceGen files (facegeom .nif and four facecustomization textures) under the file name of the plugin
    /// that defines the FormID, named by its local ID. Not when its template gives the Traits. Reported only when none
    /// of the five files is there: Bethesda's own plugins ship partial sets (a .nif without textures), and some NPCs
    /// with none at all (real data, 2026-11-23). The ID is tried with 24, 16 and 12 bits, and for the .nif also with
    /// any third byte over the 16-bit ID: light plugins name their files by the 12-bit ID, and medium creations their
    /// textures by the 16-bit ID and their .nif by a 24-bit one (read from installed mods' face files, 2026-11-23).</summary>
    static List<MissingAsset> FindFaces(IModGetter mod, List<INpcGetter> npcs, AssetIndex index, Func<IMajorRecordGetter, string> name,
                                        Func<IMajorRecordGetter, IMajorRecordGetter?>? original, AssetUses uses)
    {
        var list = new List<MissingAsset>();
        var style = (mod as IModMasterStyledGetter)?.MasterStyle;
        foreach (var npc in npcs)
        {
            string who;
            try
            {
                who = name(npc);
                if (npc.UseTemplateActors.HasFlag(Npc.TemplateActorType.Traits)) { Log.Write($"assets: face of {who}: its template gives the Traits; not checked"); continue; }
                var added = npc.FormKey.ModKey == mod.ModKey;
                if (added ? !HasFace(npc) : (original?.Invoke(npc) is INpcGetter o && !ReferenceEquals(o, npc) ? !FaceDiffers(npc, o) : !HasFace(npc)))
                    continue;
                uses.Faces++;
                var origin = npc.FormKey.ModKey.FileName.String;
                var id = npc.FormKey.ID;
                var ids = new[] { id & 0xFFFFFF, id & 0xFFFF, id & 0xFFF }.Distinct().ToList();
                var found = ids.SelectMany(i => FaceTextureSuffixes.Select(s => FaceTexture(origin, i, s)).Prepend(FaceNif(origin, i)))
                    .Concat(Enumerable.Range(0, 256).Select(b => FaceNif(origin, ((uint)b << 16) | (id & 0xFFFF))))
                    .Where(index.Has).Distinct().ToList();
                // the name to report: the plugin's own style for its own NPCs, else the 24-bit local ID
                var shown = added && style == MasterStyle.Small ? id & 0xFFF : added && style == MasterStyle.Medium ? id & 0xFFFF : id & 0xFFFFFF;
                var nif = FaceNif(origin, shown);
                Log.Write($"assets: face of {who} ({(added ? "added" : "changed by the override")}): " +
                          (found.Count > 0 ? $"{found.Count} face file(s) found, for example {found[0]}" : $"no face files (looked for {nif} and its textures)"));
                if (found.Count == 0) list.Add(new MissingAsset("NOTE", "face", nif, null, [who]));
                // 0.15.0: an override that changes the face, while every face file found comes from outside this index's
                // own files (in release: none is in the zips, so they are the master's), shows the master's old face
                else if (!added && index.Fallback != null && !found.Any(index.HasOwn))
                {
                    Log.Write($"assets: face of {who}: the override changes the face, but none of the {found.Count} face file(s) found is the mod's own: the baked face is stale");
                    list.Add(new MissingAsset("NOTE", "face-stale", found[0], null, [who]));
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: face of {npc.FormKey}: could not be checked ({e.GetType().Name}: {e.Message})"); }
        }
        return list;
    }

    static string Unread(string rel, Exception e, string what = "materials or textures") =>
        $"couldn't read {rel} ({(e is InvalidDataException or JsonException ? "" : e.GetType().Name + ": ")}{SafeText.OneLine(e.Message)}), so the {what} it names weren't checked";

    /// <summary>The texture and material paths a record names (0.14.0, D2a): the Texture and Material asset links
    /// Mutagen 0.54.4 lists, plus the eight string fields it doesn't type as assets. Data-relative, each with its kind.</summary>
    public static IEnumerable<(string Kind, string Path)> TexturesAndMaterials(IMajorRecordGetter rec)
    {
        var list = new HashSet<(string, string)>();
        // A texture link with no .dds or .png is a name, not a file: Mutagen types Perk.PerkIcon as a texture, but
        // perks name their icon ("patch_combat_lasers"; real data, 2026-11-23)
        void T(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var t = AssetFiles.DataRel(p, "textures");
            if (t.EndsWith(".dds", StringComparison.Ordinal) || t.EndsWith(".png", StringComparison.Ordinal)) list.Add(("texture", t));
        }
        void M(string? p) { if (!string.IsNullOrWhiteSpace(p)) list.Add(("material", AssetFiles.DataRel(p, "materials"))); }
        if (rec is IAssetLinkContainerGetter c)
            foreach (var l in c.EnumerateListedAssetLinks())
            {
                if (l.AssetTypeInstance is StarfieldTextureAssetType) T(l.DataRelativePath.Path);
                else if (l.AssetTypeInstance is StarfieldMaterialAssetType) M(l.DataRelativePath.Path);
            }
        switch (rec)
        {
            case ITextureSetGetter t: M(t.Material); break;
            case IActivatorGetter a: M(a.WaterMaterial); break;
            case IBiomeGetter b: M(b.GlobalLayerMaterial); M(b.Water?.WaterMaterial); break;
            case ICellGetter cell: M(cell.GlobalDirtLayerMaterial); break;
            case ILensFlareGetter lf: foreach (var s in lf.Sprites ?? []) T(s.Texture); break;
            case IPhotoModeFeatureGetter pm: T(pm.Texture); break;
            case IShaderParticleGeometryGetter sp: T(sp.ParticleTexture); break;
        }
        return list;
    }

    /// <summary>0.10.0 (README "Known gaps" until 0.9.1): a missing path with a part ending in a dot or a space gets its
    /// own text. When the path Windows trims it to is a loose file, the game on Windows may load it, so the finding
    /// becomes a WARNING that says so (an archive lookup uses the exact name, so packing the file breaks it); otherwise
    /// it stays missing, with the trimmed name added.</summary>
    static MissingAsset Odd(MissingAsset m, AssetIndex index)
    {
        if (!AssetIndex.OddPart(m.Path)) return m;
        var t = AssetIndex.WindowsTrim(m.Path);
        var loose = index.TrimmedLoose(m.Path);
        Log.Write($"assets: {m.Path} has a part ending in a dot or a space; Windows opens {t}, which {(loose != null ? "is" : "isn't")} a loose file here");
        return m with { Severity = loose != null ? "WARNING" : m.Severity, Trimmed = t, TrimmedFound = loose != null };
    }

    internal static string Users(IReadOnlyList<string> users) => string.Join(", ", users.Take(3)) + (users.Count > 3 ? $" and {users.Count - 3} more" : "");
}

/// <summary>What Find counted beyond scripts and models (0.14.0): texture and material paths looked up, the plugin's
/// own .nif and .mat files read, the notes for files it couldn't read or materials it couldn't check (Unverified,
/// when no material database could be read).</summary>
public sealed class AssetUses
{
    public int Textures, Materials, Meshes, Sounds, Faces, FilesRead;
    public readonly List<string> Notes = new();
    /// <summary>Sound banks it couldn't read or whose version isn't a known one (kept apart: release reports them under
    /// sound-missing, which runs with --dmmdeps too).</summary>
    public readonly List<string> SoundNotes = new();
    public readonly List<string> Unverified = new();
}

/// <summary>A path no loose file or loaded archive provides. Kind: "script" (an ERROR), "model", "texture",
/// "material", "mesh" or "sound" (a WARNING; textures, materials and sounds since 0.14.0, mesh since 0.15.0; a
/// sound's Users are the banks that stream it, a mesh's the .nif files that name it). Path is data-relative and lower case; Script is the script name as the record gives it; Users are the
/// records that use it, each once, in the order found.</summary>
public sealed record MissingAsset(string Severity, string Kind, string Path, string? Script, IReadOnlyList<string> Users)
{
    /// <summary>0.10.0: for a path with a part ending in a dot or a space, the path Windows opens instead (else null),
    /// and whether that is a loose file here.</summary>
    public string? Trimmed { get; init; }
    public bool TrimmedFound { get; init; }
    string What => Kind == "script" ? $"script {Script} ({Path})" : $"{Kind} {Path}";

    /// <summary>The face text (0.14.0, D5; kind "face", a NOTE in release). Users holds the one NPC.</summary>
    public string FaceText =>
        $"NPC {Assets.Users(Users)} has its own face but no baked face data: neither {Path} nor its four facecustomization textures " +
        "(_ao, _color, _normal and _rough .dds) are in a loose file or a loaded archive. Some of Bethesda's own NPCs have none either, so this may be " +
        "intended; if the face should look the way you made it, bake it in the Creation Kit (BakeFCT and Bake Char Gen Mesh Geometry) and pack the files with the mod";

    /// <summary>The stale-face text (0.15.0, kind "face-stale", a NOTE in release): an override changes another plugin's
    /// NPC face, but the baked face files found (Path is one of them) are only the master's, not the mod's own.</summary>
    public string StaleFaceText =>
        $"the override of NPC {Assets.Users(Users)} changes its face, but the baked face files found (such as {Path}) are only the ones the " +
        "plugin that defines the NPC ships, not the mod's own, so the game shows the old face. If the face should look the way you made it, bake " +
        "it in the Creation Kit (BakeFCT and Bake Char Gen Mesh Geometry) and pack the files, under the defining plugin's name, with the mod";

    public string Text => Kind == "face" ? FaceText : Kind == "face-stale" ? StaleFaceText : Trimmed == null
        ? $"{What} is in no loose file or loaded archive; used by {Assets.Users(Users)}"
        : TrimmedFound
            ? $"{What} has a part that ends in a dot or a space. Windows trims it and opens the loose file {Trimmed}, so the game may load it on Windows, but an archive lookup uses the exact name, so the file goes missing once it is packed into a .ba2. Fix the path in the record; used by {Assets.Users(Users)}"
            : $"{What} is in no loose file or loaded archive, and it has a part that ends in a dot or a space (Windows would open {Trimmed}, which isn't there either); used by {Assets.Users(Users)}";
}
