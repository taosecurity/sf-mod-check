// The assets check (0.6.0, PLAN-ASSETS.md): every script a record attaches must have its .pex, and every model path a
// record names must have its file, in a loose file or in an archive the game loads. A missing script is an ERROR (the
// record's behavior silently doesn't run); a missing model is a WARNING. Only scripts and models are checked in this
// version: textures, materials, .mesh geometry, voice and animation files, and script parents are not (README).
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
    /// each (an 8-bit index; StarWarsGenesis NOTES "BA2 limit test", confirmed in Ghidra 2026-11-17). Archives whose
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
    /// counts (conflict --no-assets); the index then finds nothing in archives.</summary>
    public static AssetIndex Build(IEnumerable<string> plugins, IReadOnlyList<string> dirs, IEnumerable<string> inis, string language, bool names = true)
    {
        // off Windows "/g/Data" and "/g/data" may be two folders, so they stay apart (at worst one is searched twice)
        var index = new AssetIndex(dirs.Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList());
        var archives = new List<(string Path, string Why)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // by file name: the first folder that has it wins
        void Take(string path, string why)
        {
            if (taken.Add(Path.GetFileName(path))) archives.Add((path, why));
        }
        var unlistable = new HashSet<string>(StringComparer.Ordinal);   // logged once each, not once per plugin
        foreach (var plugin in plugins)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin);
            foreach (var dir in index.looseDirs)
            {
                if (!Directory.Exists(dir)) continue;
                // case-insensitive on every system, as the game sees names under Proton (code review 0.6.0)
                // AttributesToSkip 0: hidden and system archives count, as before; a folder that can't be listed is
                // skipped with its reason, not the end of the step (code review 0.6.0)
                List<string> found;
                try
                {
                    found = Directory.EnumerateFiles(dir, stem + " - *.ba2", new EnumerationOptions
                            { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = false })
                        .Order(StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal).ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (unlistable.Add(dir)) Log.Write($"assets: folder {dir} can't be listed, its archives not counted ({e.Message})");
                    continue;
                }
                foreach (var ba2 in found)
                {
                    var suffix = Path.GetFileNameWithoutExtension(ba2)[(stem.Length + 3)..];
                    if (OtherLanguageVoices(suffix, language)) { index.Skipped.Add((Path.GetFileName(ba2), $"voices for another language (--language {language})")); continue; }
                    Take(ba2, $"named after {plugin}");
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
                // textures (DX10) hold no scripts or models: their names aren't needed, but they count toward the
                // texture registry's slots
                var type = Ba2Names.Type(path);
                if (type == "DX10") { index.Dx10.Add(name); index.Skipped.Add((name, "texture archive (DX10): no scripts or models")); continue; }
                if (type == "GNRL") index.Gnrl.Add(name);
                else Log.Write($"assets: archive {name} has type \"{type}\" (not GNRL or DX10): not counted toward either archive limit");
                if (!names) continue;
                foreach (var n in Ba2Names.Read(path)) index.inArchives.Add(n);
                index.Loaded.Add($"{name} ({why})");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                index.Unreadable.Add((name, e.Message));
            }
        }
        foreach (var a in index.Loaded) Log.Write($"assets: archive {a}");
        // one line per counted general archive, with or without --no-assets (tools\compare_ba2count.py reads them)
        foreach (var a in index.Gnrl) Log.Write($"assets: archive {a} counted: general (GNRL)");
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

    /// <summary>Whether the game can find this data-relative file ("scripts\foo.pex", "meshes\a\b.nif").</summary>
    public bool Has(string relPath)
    {
        var p = Ba2Names.Normalize(relPath);
        if (inArchives.Contains(p)) return true;
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

    // folder -> its entries by name, matched without regard to case; several when names differ only in case (Linux and
    // macOS only)
    readonly Dictionary<string, Dictionary<string, List<string>>> listings = new(StringComparer.Ordinal);

    /// <summary>The loose file for a data-relative path ("scripts\foo.pex") under a folder, or null. Windows paths
    /// ignore case and take "\" as a separator; on Linux and macOS (Proton, Wine) each part is matched without regard
    /// to case, as the game sees it, so "Scripts/Foo.pex" on disk counts (CI on ubuntu, 0.6.0). When folders differ only
    /// in case ("Scripts" and "scripts", from two mods), each is searched. A rooted name (an INI's archive list may
    /// hold one) is looked up as given; a Windows drive path off Windows is not found.</summary>
    internal string? LooseFile(string dir, string rel)
    {
        // one leading "\" ("\x.ba2" in an INI) means under Data on every system, not the drive's root (code review 0.6.0)
        if (rel.StartsWith('\\') && !rel.StartsWith(@"\\")) rel = rel.TrimStart('\\');
        if (OperatingSystem.IsWindows() || Path.IsPathRooted(rel)) { var w = Path.Combine(dir, rel); return File.Exists(w) ? w : null; }
        // a Windows drive path ("C:\Mods\x.ba2", as a Proton INI may hold) has no Linux path we can know
        if (rel.Length >= 2 && rel[1] == ':') return null;
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a folder it can't list has nothing found in it
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
                                                 ILinkCache? masters = null)
    {
        var (scripts, models, missing, inherited) = Find(mod, index, name, masters == null ? null : rec => Resolve(masters, rec));
        if (inherited > 0) Log.Write($"assets: {inherited} missing path use(s) not reported: the master version of the record already names them");
        foreach (var m in missing) add(m.Severity, "assets", m.Text);
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
        // missing path needs it (StarWarsGenesis 2026-11-18: mods that override vanilla MQ305, City_NA_Well01 and two
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
            var (s, m, missing, inherited) = Find(p.Mod, index, Checker.Name, rec => Original(p, rec));
            scripts += s; models += m; inheritedAll += inherited;
            foreach (var f in missing) analysis.MissingAssets.Add(new Conflict.AssetFinding(p.FileName, f));
            if (missing.Count > 0 || inherited > 0)
                Log.Write($"assets: {p.FileName}: {missing.Count} missing ({s} script uses, {m} model uses)" +
                          (inherited > 0 ? $"; {inherited} missing path use(s) not reported: the original record already names them" : ""));
        }
        caches.Clear();   // the masters' link caches aren't needed for the report
        analysis.AssetScriptUses = scripts; analysis.AssetModelUses = models; analysis.AssetsInherited = inheritedAll;
        Log.Write($"assets: {checkedPlugins} mod plugins checked, {scripts} script uses, {models} model uses, " +
                  $"{analysis.MissingAssets.Count} missing ({analysis.MissingAssets.Count(f => f.Asset.Kind == "script")} scripts), " +
                  $"{index.Unreadable.Count} unreadable archives");
    }

    /// <summary>conflict --no-assets: only the archive counts (each archive's 24-byte header), so the archive-limit
    /// warning still works when scripts and models aren't checked.</summary>
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

    /// <summary>One plugin's missing scripts and models, each path once with the records that use it. original: for an
    /// override, the version it replaces (null when unknown). A missing path that version names too is inherited, not
    /// the plugin's own mistake (often Bethesda's: vanilla records that attach scripts the game doesn't ship), so it
    /// isn't reported; Inherited counts those uses. The lookup runs only for missing paths.</summary>
    public static (int Scripts, int Models, List<MissingAsset> Missing, int Inherited) Find(IModGetter mod, AssetIndex index, Func<IMajorRecordGetter, string> name,
        Func<IMajorRecordGetter, IMajorRecordGetter?>? original = null)
    {
        int inherited = 0;
        bool Inherited(IMajorRecordGetter rec, string path, bool script)
        {
            if (original == null || rec.FormKey.ModKey == mod.ModKey) return false;
            try
            {
                var o = original(rec);
                if (o == null || ReferenceEquals(o, rec)) return false;
                var same = script ? Scripts(o).Any(s => PexPath(s) == path) : Models(o).Contains(path);
                if (same) { inherited++; Log.Write($"assets: {name(rec)}: {path} is missing, but the record it overrides names it too: not reported"); }
                return same;
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: {name(rec)}: could not read the record it overrides ({e.GetType().Name}: {e.Message})"); return false; }
        }
        var missingScripts = new Dictionary<string, (string Script, List<string> Users)>(StringComparer.Ordinal);
        var missingModels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int scripts = 0, models = 0;
        // Records are read lazily: a damaged one stops the walk. The read check already reports it as an ERROR, so the
        // records read up to there are checked and the stop is only logged.
        try
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                IEnumerable<string> ss, ms;
                try { ss = Scripts(rec).ToList(); ms = Models(rec).ToList(); }
                catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: {name(rec)}: could not list its assets ({e.GetType().Name}: {e.Message})"); continue; }
                foreach (var s in ss)
                {
                    scripts++;
                    var pex = PexPath(s);
                    if (index.Has(pex) || Inherited(rec, pex, true)) continue;
                    if (!missingScripts.TryGetValue(pex, out var m)) missingScripts[pex] = m = (s, new());
                    m.Users.Add(name(rec));
                }
                foreach (var p in ms)
                {
                    models++;
                    if (index.Has(p) || Inherited(rec, p, false)) continue;
                    if (!missingModels.TryGetValue(p, out var u)) missingModels[p] = u = new();
                    u.Add(name(rec));
                }
            }
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"assets: stopped at a record Mutagen can't read ({e.GetType().Name}: {e.Message}); the read check lists it"); }
        var missing = new List<MissingAsset>();
        foreach (var (pex, (script, users)) in missingScripts)
            missing.Add(new MissingAsset("ERROR", "script", pex, script, users.Distinct().ToList()));
        foreach (var (path, users) in missingModels)
            missing.Add(new MissingAsset("WARNING", "model", path, null, users.Distinct().ToList()));
        return (scripts, models, missing, inherited);
    }

    internal static string Users(IReadOnlyList<string> users) => string.Join(", ", users.Take(3)) + (users.Count > 3 ? $" and {users.Count - 3} more" : "");
}

/// <summary>A script or model path no loose file or loaded archive provides. Kind: "script" (an ERROR) or "model" (a
/// WARNING). Path is data-relative and lower case; Script is the script name as the record gives it; Users are the
/// records that use it, each once, in the order found.</summary>
public sealed record MissingAsset(string Severity, string Kind, string Path, string? Script, IReadOnlyList<string> Users)
{
    public string Text => Kind == "script"
        ? $"script {Script} ({Path}) is in no loose file or loaded archive; used by {Assets.Users(Users)}"
        : $"model {Path} is in no loose file or loaded archive; used by {Assets.Users(Users)}";
}
