// sfmodcheck release (0.7.0, chunk C2): what the zips' plugins need.
//   1. script-missing  every script a zip plugin attaches (Foo:Bar and names without a namespace, read through Mutagen
//                      by Assets.Scripts) must be a .pex in the zips' loose files, an archive of a zip plugin, an
//                      archive of a plugin in a --masters-dir folder, or the game's archives (the official masters'
//                      "<name> - *.ba2", Starfield.ini's archive lists, and the archives of a master installed in Data).
//                      Loose files outside the zips don't count: a copy left in Data by an older install of the mod
//                      would otherwise hide a script the release lost. A missing path the overridden master record
//                      names too is inherited (often Bethesda's own) and not reported, as the assets check does.
//                      Without DMMDeps, model paths are checked the same way (model-missing, a WARNING).
//   2. dmmdeps         optional (--dmmdeps <exe>, SHA-256 pinned to the reviewed v0.4.1 build): run as
//                      the author's earlier Python release check runs it (a cache pass with --gameroot, then the real pass on a scratch
//                      Data folder holding the zip's loose files and its archives' contents) and its lists mapped
//                      as the earlier Python check maps them.
//   3. plugin-check    Checker.Run on every zip plugin, the zips' plugin folders searched for masters first, without
//                      the assets check (step 1 does it with the right folders, so no script is reported twice).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

public static class ReleaseNeeds
{
    /// <summary>DMMDeps v0.4.1 (dmmdeps-fat.exe), reviewed 2026-10-28; the same pin as the author's earlier Python release check.</summary>
    public const string DmmDepsSha256 = "8a5367865ef21e29b17301a3341b2195fdacbf163c151678c0c420747ff9e6f8";

    /// <summary>Seconds one DMMDeps run may take.</summary>
    public static int DmmDepsTimeoutSeconds = 900;

    /// <summary>Lines per (plugin, severity, check) taken from the plugin check, as the earlier Python check's --max 25.</summary>
    const int MaxPerCheck = 25;

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        var findings = new List<ReleaseFinding>();
        var plugins = c.Packages.SelectMany(p => p.Plugins.Select(x => (Package: p, Path: x))).ToList();
        if (plugins.Count == 0)
        {
            Log.Write("needs: no plugins in the zips; scripts, DMMDeps and the plugin check skipped");
            return findings;
        }
        var scripts = Scripts(c, plugins, findings);
        if (c.Options.DmmDeps is { } exe) DmmDeps(c, exe, scripts, findings);
        else findings.Add(new(Level.Note, "dmmdeps", "DMMDeps wasn't given (--dmmdeps), so only sf-mod-check's own check of the files the " +
                                                     "plugins need ran: attached scripts, models, the .mesh geometry of the mod's own models, materials, textures, NPC face data and the sounds of the mod's own sound banks. Voice files were not checked."));
        PluginCheck(c, plugins, findings);
        return findings;
    }

    // ---------------------------------------------------------------- 1. attached scripts (and models)

    /// <summary>What step 1 learned per plugin, for the DMMDeps step: the script names its records attach, and the
    /// paths already reported missing (not reported again by DMMDeps).</summary>
    internal sealed record PluginScripts(HashSet<string> Names, HashSet<string> Reported);

    static Dictionary<string, PluginScripts> Scripts(ReleaseContext c, List<(ReleasePackage Package, string Path)> plugins, List<ReleaseFinding> findings)
    {
        var result = new Dictionary<string, PluginScripts>(StringComparer.OrdinalIgnoreCase);
        var pluginDirs = PluginDirs(c);
        var data = Path.GetFullPath(c.DataDir);
        var external = new ExternalArchives(c.Options.MastersDirs, data);
        foreach (var ma in c.Options.MasterArchives) external.AddArchive(Path.GetFullPath(ma));
        AssetIndex? index = null;
        foreach (var (pkg, path) in plugins)
        {
            var name = Path.GetFileName(path);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            result[path] = new PluginScripts(names, reported);
            var mastersMissing = false;
            try
            {
                using var game = new GameData(data, pluginDirs.Concat(c.Options.MastersDirs));
                foreach (var m in GameData.HeaderMasters(path)) mastersMissing |= game.TryOpen(m) == null;
                // The index of the zips' own files: loose files under each zip's root and the archives named after any
                // zip plugin or its masters. Built once (the zips are the same for every plugin).
                index ??= BuildIndex(c, plugins.Select(p => p.Path), findings);
                foreach (var m in game.LoadOrder)
                    if (game.Locate(m.ModKey.FileName) is { } mp && Path.GetDirectoryName(Path.GetFullPath(mp))!.Equals(data.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        external.AddDataPlugin(m.ModKey.FileName);
                // With a master missing Mutagen can't place the plugin's FormIDs, but script names don't need them: the
                // plugin is read on its own then (the plugin check reports the missing master).
                using var mod = mastersMissing
                    ? StarfieldMod.CreateFromBinaryOverlay(path, StarfieldRelease.Starfield, new BinaryReadParameters { MasterFlagsLookup = StandIns(game, path) })
                    : game.OpenPlugin(path);
                if (mastersMissing) Log.Write($"needs: {name}: a master is missing, so the plugin is read without its masters (script names only matter here)");
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    try { names.UnionWith(Assets.Scripts(rec)); }
                    catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"needs: {name}: {Checker.Name(rec)}: could not list its scripts ({e.GetType().Name}: {e.Message})"); }
                }
                IMajorRecordGetter? Original(IMajorRecordGetter rec) =>
                    game.LinkCache.TryResolve(rec.FormKey, rec.Registration.GetterType, out var o) ? o : null;
                // names in the game's and --masters-dir archives count as found, and materials are looked up in every
                // material database those archives and the zips hold (0.14.0). Like the archive names, the databases
                // pool across the plugins of one run: a later plugin also sees an earlier plugin's masters' databases.
                index.Fallback = external.Has;
                foreach (var (ba2, entry) in external.Cdbs) index.AddCdb(ba2, entry);
                // the loose sound banks of this plugin's own zip are its own, so each is reported under the plugin it
                // ships with; the first plugin also takes those of zips with no plugin (0.14.0)
                index.OwnLooseDirs = c.Packages.Where(p => p == pkg || p.Plugins.Count == 0 && path == plugins[0].Path).Select(p => p.Root).ToList();
                var uses = new AssetUses();
                var (scriptUses, modelUses, missing, inherited) = Assets.Find(mod, index, Checker.Name, Original, uses, faces: true);
                if (c.Options.DmmDeps == null)
                    foreach (var n in uses.Notes)
                        findings.Add(new(Level.Note, "material-missing", $"{name}: {n}."));
                // DMMDeps doesn't read sound banks, so sound-missing runs with --dmmdeps too (0.14.0)
                foreach (var n in uses.SoundNotes) findings.Add(new(Level.Note, "sound-missing", $"{name}: {n}."));
                int shown = 0;
                foreach (var m in missing)
                {
                    // the game's and --masters-dir archives were already looked in, through index.Fallback (0.14.0)
                    shown++;
                    if (m.Kind == "script")
                    {
                        reported.Add(m.Path);
                        findings.Add(new(Level.Problem, "script-missing",
                            $"{name} attaches script {m.Script}, but {m.Path} is in none of {Zips(c)} loose files or archives, a --masters-dir mod's " +
                            $"archives or the game's archives (used by {Assets.Users(m.Users)}). Put the compiled script in the zip or its archive, " +
                            "or give --masters-dir the folder of the mod it comes from."));
                    }
                    else if (m.Kind == "sound")
                    {
                        reported.Add(m.Path);
                        findings.Add(new(Level.Warning, "sound-missing",
                            $"{name}: the sound bank {Assets.Users(m.Users)} streams {m.Path}, which is in none of {Zips(c)} loose files or archives, " +
                            "a --masters-dir mod's archives or the game's archives, so that sound is silent in the game. Put the .wem file in the zip " +
                            "or its archive (under sound\\soundbanks), or generate the bank again in Wwise with its media."));
                    }
                    else if (m.Kind == "mesh")
                    {
                        // DMMDeps can't see geometry a .nif names (KnownGaps), so mesh-missing runs with --dmmdeps too (0.15.0)
                        reported.Add(m.Path);
                        findings.Add(new(Level.Warning, "mesh-missing",
                            $"{name}: the model {Assets.Users(m.Users)} names the geometry file {m.Path}, which is in none of {Zips(c)} loose files or archives, " +
                            "a --masters-dir mod's archives or the game's archives, so that part of the model doesn't show in the game. Put the .mesh file in " +
                            "the zip or its archive (under geometries), or export the model again with its geometry."));
                    }
                    else if (m.Kind == "face")
                    {
                        // a NOTE (P3 in the baton: Bethesda's own NPCs aren't always baked); DMMDeps can't see face files
                        // (KnownGaps), so face-missing runs with --dmmdeps too (0.14.0)
                        reported.Add(m.Path);
                        findings.Add(new(Level.Note, "face-missing", $"{name}: {m.FaceText}. Looked in {Zips(c)} loose files and archives, " +
                                                                     "a --masters-dir mod's archives and the game's archives."));
                    }
                    else if (m.Kind == "face-stale")
                    {
                        // 0.15.0: found, but only outside the zips; a NOTE like face-missing, and with --dmmdeps too
                        // (not added to reported: the files were found)
                        findings.Add(new(Level.Note, "face-missing", $"{name}: {m.StaleFaceText}. Looked in {Zips(c)} loose files and archives."));
                    }
                    else if (c.Options.DmmDeps != null) { }   // DMMDeps reports models, materials and textures itself
                    else if (m.Kind is "texture" or "material")
                    {
                        reported.Add(m.Path);
                        findings.Add(new(Level.Warning, m.Kind + "-missing",
                            $"{name}: the {m.Kind} {m.Path} is in none of {Zips(c)} loose files or archives, a --masters-dir mod's archives " +
                            (m.Kind == "material" ? "or the game's archives, and isn't in any of their material databases " : "or the game's archives ") +
                            $"(used by {Assets.Users(m.Users)}). Put the file in the zip or its archive, or fix the path in the file that names it."));
                    }
                    else
                    {
                        reported.Add(m.Path);
                        findings.Add(new(Level.Warning, "model-missing",
                            $"{name} names the model {m.Path}, which is in none of {Zips(c)} loose files or archives, a --masters-dir mod's archives " +
                            $"or the game's archives (used by {Assets.Users(m.Users)}). Put the file in the zip or its archive, or fix the path."));
                    }
                }
                Log.Write($"needs: {name}: {scriptUses} script uses, {modelUses} model uses, {uses.Textures} texture paths, {uses.Materials} material paths, {uses.Sounds} streamed sounds, {uses.Faces} NPC faces " +
                          $"({uses.FilesRead} of its own .nif, .mat and .bnk files read), {shown} missing" +
                          (inherited > 0 ? $", {inherited} inherited from the record it overrides (not reported)" : ""));
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"needs: {name}: scripts not checked: {e}");
                // A missing master already fails the plugin check; otherwise an unchecked plugin is a problem of its own.
                findings.Add(new(mastersMissing ? Level.Warning : Level.Problem, "script-missing",
                    $"{name}: its attached scripts could not be checked ({e.GetType().Name}: {SafeText.OneLine(e.Message)}). " +
                    "Install its masters or give --masters-dir their folder; the plugin check below says more."));
            }
        }
        return result;
    }

    /// <summary>Master styles for reading a plugin whose masters aren't all installed: the opened ones as they are, a
    /// missing one as a full master (its FormIDs may then be placed wrong, which the script names don't care about).</summary>
    static LoadOrder<IModMasterStyledGetter> StandIns(GameData game, string path)
    {
        var opened = game.LoadOrder.ToDictionary(m => m.ModKey.FileName.String, m => (IModMasterStyledGetter)m, StringComparer.OrdinalIgnoreCase);
        var list = new List<IModMasterStyledGetter>(game.LoadOrder);
        foreach (var m in GameData.HeaderMasters(path))
            if (!opened.ContainsKey(m)) list.Add(new KeyedMasterStyle(ModKey.FromFileName(m), MasterStyle.Full));
        return new LoadOrder<IModMasterStyledGetter>(list);
    }

    /// <summary>The zips' possessive for the messages: "MyMod-1.0.0.zip's" or "the zips'".</summary>
    static string Zips(ReleaseContext c) => c.Packages.Count == 1 ? c.Packages[0].Name + "'s" : "the zips'";

    /// <summary>The folders holding the zips' plugins, each once, in zip order.</summary>
    static List<string> PluginDirs(ReleaseContext c) =>
        c.Packages.SelectMany(p => p.Plugins).Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static AssetIndex BuildIndex(ReleaseContext c, IEnumerable<string> zipPlugins, List<ReleaseFinding> findings)
    {
        // every zip plugin and the masters they list: an add-on's archives load with it, a main mod's with the main mod
        var names = new List<string>();
        foreach (var p in zipPlugins)
        {
            names.Add(Path.GetFileName(p));
            try { names.AddRange(GameData.HeaderMasters(p)); }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"needs: {Path.GetFileName(p)}: masters not read ({e.Message})"); }
        }
        var roots = c.Packages.Select(p => p.Root).ToList();
        var index = AssetIndex.Build(names.Distinct(StringComparer.OrdinalIgnoreCase), roots, [], "en");
        foreach (var (a, why) in index.Unreadable)
            findings.Add(new(Level.Problem, "script-missing", $"{Assets.UnreadableText(a, why)}: rebuild the archive (Archive2 or the Creation Kit)"));
        return index;
    }

    /// <summary>The names in archives outside the zips that load with the mod: each plugin's "&lt;name&gt; - *.ba2" in a
    /// --masters-dir folder, and in the Data folder the official masters' archives, Starfield.ini's archive lists and the
    /// archives of masters installed there. Only scripts, models, geometry (.mesh), materials, textures and streamed sounds are kept (memory), and every
    /// material database (.cdb) in a general archive is remembered (0.14.0).</summary>
    sealed class ExternalArchives
    {
        readonly HashSet<string> names = new(StringComparer.Ordinal);
        readonly HashSet<string> read = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> dataPlugins = new(StringComparer.OrdinalIgnoreCase);
        readonly string data;

        public ExternalArchives(IEnumerable<string> mastersDirs, string data)
        {
            this.data = data;
            foreach (var dir in mastersDirs)
                foreach (var plugin in Directory.EnumerateFiles(dir, "*.es?").Where(IsPlugin))
                    Archives(dir, Path.GetFileNameWithoutExtension(plugin));
            if (Directory.Exists(data))
            {
                foreach (var m in Checker.OfficialMasters(data)) AddDataPlugin(m);
                var ini = Path.Combine(Path.GetDirectoryName(data.TrimEnd('\\', '/')) ?? data, "Starfield.ini");
                foreach (var (_, n, _) in AssetIndex.IniArchives([ini]))
                {
                    var p = Path.Combine(data, n.TrimStart('\\', '/'));
                    if (n.IndexOf(':') < 0 && !n.Contains("..") && File.Exists(p)) Read(p);
                }
            }
        }

        static bool IsPlugin(string p) => Path.GetExtension(p).ToLowerInvariant() is ".esm" or ".esp" or ".esl";

        /// <summary>An archive named on the command line (--master-archive).</summary>
        public void AddArchive(string ba2) => Read(ba2);

        public void AddDataPlugin(string fileName)
        {
            if (dataPlugins.Add(fileName)) Archives(data, Path.GetFileNameWithoutExtension(fileName));
        }

        void Archives(string dir, string stem)
        {
            foreach (var ba2 in Directory.EnumerateFiles(dir, stem + " - *.ba2", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
                Read(ba2);
        }

        void Read(string ba2)
        {
            if (!read.Add(Path.GetFullPath(ba2))) return;
            try
            {
                var type = Ba2Names.Type(ba2);
                int n = 0;
                foreach (var name in Ba2Names.Read(ba2))
                {
                    if (type == "GNRL" && name.EndsWith(".cdb", StringComparison.Ordinal)) Cdbs.Add((ba2, name));
                    if (name.EndsWith(".pex", StringComparison.Ordinal) || name.StartsWith("meshes\\", StringComparison.Ordinal) ||
                        name.StartsWith("textures\\", StringComparison.Ordinal) || name.StartsWith("materials\\", StringComparison.Ordinal) ||
                        name.StartsWith("geometries\\", StringComparison.Ordinal) ||
                        name.StartsWith("sound\\soundbanks\\", StringComparison.Ordinal)) { names.Add(name); n++; }
                }
                Log.Write($"needs: archive {ba2}: {n} scripts, models, materials, textures and sound-bank files");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Write($"needs: archive {ba2} could not be read ({e.Message}); its files count as missing");
            }
        }

        public bool Has(string path) => names.Contains(path);

        /// <summary>Every material database (.cdb) in the general archives read, as (archive, data-relative name).</summary>
        public List<(string Archive, string Entry)> Cdbs { get; } = new();
    }

    // ---------------------------------------------------------------- 2. DMMDeps

    // Files DMMDeps can't see (named by convention, not by a string in the plugin). Same list as the earlier Python check.
    internal static readonly (Regex Rx, string Why)[] KnownGaps =
    [
        (new(@"^meshes/actors/character/facegendata/"), "FaceGen mesh (#176)"),
        (new(@"^geometries/"), "geometry used by a NIF it didn't walk (#176)"),
        (new(@"^textures/actors/character/facecustomization/"), "FaceGen texture (#176)"),
        (new(@"^materials/actors/human/faces/"), "face material (#176)"),
        (new(@"^scripts/.*\.(pex|psc)$"), "script not named in the plugin: base script via extends? (#175)"),
    ];

    // Files DMMDeps leaves out on purpose; a copy in the zip is a NOTE.
    internal static readonly (Regex Rx, string Why)[] DeliberateOmissions =
    [
        (new(@"^sound/voice/[^/]+/wwise\.dat$"), "voice wwise.dat, left out by DMMDeps by design (#176)"),
    ];

    static readonly Regex Generated = new(@"\.achlist|_deps\.(csv|json)$");

    /// <summary>A path the way the earlier Python check compares DMMDeps' lists: lower case, backslashes, no "data\" in front.</summary>
    internal static string Norm(string p)
    {
        var n = Ba2Names.Normalize(Regex.Replace(p.Trim(), @"[\\/]+", "\\"));
        return n.StartsWith("data\\", StringComparison.Ordinal) ? n[5..] : n;
    }

    static string Slash(string p) => p.Replace('\\', '/');

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    static void DmmDeps(ReleaseContext c, string exe, Dictionary<string, PluginScripts> scripts, List<ReleaseFinding> findings)
    {
        string digest;
        try { digest = Sha256(exe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            findings.Add(new(Level.Problem, "dmmdeps", $"DMMDeps {exe} can't be read ({e.Message}); give --dmmdeps the reviewed v0.4.1 dmmdeps-fat.exe"));
            return;
        }
        if (digest != DmmDepsSha256)
        {
            Log.Write($"dmmdeps: {exe} has SHA-256 {digest}, not {DmmDepsSha256}: not run");
            findings.Add(new(Level.Problem, "dmmdeps",
                $"{Path.GetFileName(exe)} is not the reviewed v0.4.1 build of DMMDeps (SHA-256 {digest}), so it was not run. " +
                "Give --dmmdeps the dmmdeps-fat.exe from DMMDeps v0.4.1, or leave --dmmdeps out."));
            return;
        }
        var data = Path.GetFullPath(c.DataDir).TrimEnd('\\', '/');
        var gameRoot = TriageCommand.HasStarfield(data) ? Path.GetDirectoryName(data) : null;
        var cachePassDone = false;
        for (int pi = 0; pi < c.Packages.Count; pi++)
        {
            var pkg = c.Packages[pi];
            if (pkg.Plugins.Count == 0) continue;
            var root = Path.Combine(c.Work, "dmmdeps", $"zip{pi}");
            var scratch = Path.Combine(root, "Data");
            if (gameRoot != null && Path.GetFullPath(scratch).StartsWith(gameRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new(Level.Problem, "dmmdeps", $"DMMDeps not run: its scratch folder {scratch} would be inside the game install"));
                return;
            }
            HashSet<string> staged;
            try { staged = Stage(pkg, scratch, findings); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                findings.Add(new(Level.Problem, "dmmdeps", $"DMMDeps not run on {pkg.Name}: its files couldn't be copied to a scratch folder ({e.Message})"));
                continue;
            }
            Directory.CreateDirectory(Path.Combine(root, "xbox"));
            Directory.CreateDirectory(Path.Combine(root, "tif"));
            var lists = new List<(string Plugin, HashSet<string> Keep, List<string> Warn, List<string> Discard)>();
            bool failed = false;
            foreach (var plugin in pkg.Plugins)
            {
                var name = Path.GetFileName(plugin);
                var copy = Path.Combine(scratch, name);
                File.Copy(plugin, copy, true);
                var stem = Path.GetFileNameWithoutExtension(name);
                // Pass 1 keeps DMMDeps' cache of the game's archives current (it re-indexes only after a game update).
                // Its output is discarded: with --gameroot DMMDeps can't see the zip's scripts (#177).
                if (!cachePassDone)
                {
                    cachePassDone = true;
                    if (gameRoot != null)
                    {
                        if (RunDmmDeps(exe, copy, root, scratch, gameRoot) is { } err) { findings.Add(new(Level.Problem, "dmmdeps", $"{name}: {err}")); failed = true; break; }
                    }
                    else if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeeOgre", "dmmdeps", "parentfile_cache.db")))
                        findings.Add(new(Level.Warning, "dmmdeps", $"no Starfield.esm in {data} and no DMMDeps cache, so DMMDeps can't recognize the game's own files; give --data the game's Data folder"));
                    foreach (var f in Directory.EnumerateFiles(scratch, stem + ".achlist*").Concat(Directory.EnumerateFiles(scratch, stem + "_deps.*"))) File.Delete(f);
                }
                // Pass 2: the real check, the scratch folder as the game root.
                if (RunDmmDeps(exe, copy, root, scratch, null) is { } err2) { findings.Add(new(Level.Problem, "dmmdeps", $"{name}: {err2}")); failed = true; break; }
                try
                {
                    var keep = ReadList(Path.Combine(scratch, stem + ".achlist")).ToHashSet(StringComparer.Ordinal);
                    var warn = ReadList(Path.Combine(scratch, stem + ".achlist_warn"));
                    var sources = ReadParentSources(Path.Combine(scratch, stem + "_deps.json"));
                    warn = SortParentMatches(name, plugin, warn, sources, keep, findings);
                    lists.Add((name, keep, warn, ReadList(Path.Combine(scratch, stem + ".achlist_discard"))));
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
                {
                    findings.Add(new(Level.Problem, "dmmdeps", $"{name}: DMMDeps' lists couldn't be read ({e.Message})"));
                    failed = true; break;
                }
            }
            if (failed) continue;
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reported = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in pkg.Plugins)
                if (scripts.TryGetValue(plugin, out var s)) { named.UnionWith(s.Names); reported[Path.GetFileName(plugin)] = s.Reported; }
            findings.AddRange(MapLists(pkg.Name, staged, lists, named, reported));
        }
    }

    /// <summary>The packaging script's mapping of DMMDeps' lists. staged: the zip's files (its archives' contents included, its
    /// plugins left out). lists: per plugin, the .achlist (keep), .achlist_warn and .achlist_discard. named: script
    /// names the zip's plugins attach. reported: per plugin, paths step 1 already reported missing.</summary>
    internal static List<ReleaseFinding> MapLists(string zip, HashSet<string> staged,
        IReadOnlyList<(string Plugin, HashSet<string> Keep, List<string> Warn, List<string> Discard)> lists,
        HashSet<string> named, IReadOnlyDictionary<string, HashSet<string>> reported)
    {
        var f = new List<ReleaseFinding>();
        var keepAll = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (plugin, keep, warn, discard) in lists)
        {
            keepAll.UnionWith(keep);
            var done = reported.GetValueOrDefault(plugin) ?? [];
            foreach (var p in keep.Where(p => !staged.Contains(p)).Order(StringComparer.Ordinal))
                if (!done.Contains(p))
                    f.Add(new(Level.Problem, "dmmdeps", $"{plugin} needs {p} (DMMDeps), but it is not in {zip}: add it to the zip or its archive"));
            foreach (var p in discard.Distinct())
                if (!done.Contains(p))
                    f.Add(new(Level.Problem, "dmmdeps", $"{plugin} references {p} (DMMDeps), but it is found nowhere, not in {zip} nor in the game's archives: " +
                                                       "add the file, or fix the reference in the plugin"));
            foreach (var p in warn.Distinct())
                f.Add(new(Level.Review, "dmmdeps", $"{plugin} uses {p}, which is also in the game's archives (DMMDeps): make sure replacing the game's copy is deliberate"));
        }
        var plugins = string.Join(", ", lists.Select(l => l.Plugin));
        int rootFiles = 0;
        foreach (var p in staged.Where(p => !keepAll.Contains(p)).Order(StringComparer.Ordinal))
        {
            // Files at the zip's root (README-<mod>.md, LICENSE, FOMOD notes) aren't game data DMMDeps could find.
            if (!p.Contains('\\')) { rootFiles++; continue; }
            var s = Slash(p);
            if (DeliberateOmissions.FirstOrDefault(x => x.Rx.IsMatch(s)).Why is { } skip)
            {
                f.Add(new(Level.Note, "dmmdeps", $"{zip} holds {p}, which DMMDeps leaves out [{skip}]"));
                continue;
            }
            // DMMDeps only finds namespaced script names (Foo:Bar). A script without a namespace that the plugin's
            // script data names is attached, not a leftover.
            var m = Regex.Match(s, @"^scripts/([^/]+)\.pex$");
            if (m.Success && named.Contains(m.Groups[1].Value))
            {
                f.Add(new(Level.Note, "dmmdeps", $"{zip} holds {p}, a script the plugin's script data names (DMMDeps misses names without a namespace)"));
                continue;
            }
            var why = KnownGaps.FirstOrDefault(x => x.Rx.IsMatch(s)).Why ?? "unreferenced: stale leftover?";
            f.Add(new(Level.Review, "dmmdeps", $"{zip} holds {p}, but DMMDeps didn't find it used by {plugins} [{why}]: check that it belongs in the release"));
        }
        if (rootFiles > 0) Log.Write($"dmmdeps: {zip}: {rootFiles} file(s) at the zip's root not compared with DMMDeps' list");
        return f;
    }

    // Archives that belong to the game itself: "<official plugin> - <part>.ba2" (Starfield's own, the DLC and the
    // Creations Bethesda ships) and the CK's sources. The plugin test is Checker.IsOfficial, through triage's
    // VanillaIndex.IsOfficialArchive, so check, triage and release share one list (0.10.1 code review round 6: a
    // hand-copied pattern also took "SFBGS003.Fixes - Main.ba2" and files that aren't archives).
    internal static bool IsOfficialArchive(string archive)
    {
        var name = ArchiveFileName(archive);
        return name.Equals("contentresources.zip", StringComparison.OrdinalIgnoreCase) || VanillaIndex.IsOfficialArchive(name);
    }

    /// <summary>The archive's file name, trimmed, with "\" read as a folder separator on every OS (Path.GetFileName
    /// splits only on "/" on Linux and macOS; 0.10.1 code review round 7). Uses triage's RelPath, so release and
    /// triage read a name the same way (round 8).</summary>
    internal static string ArchiveFileName(string archive) => RelPath.FileName(archive.Trim()) ?? "";

    /// <summary>The archive each "parentmatch" file was matched in, from DMMDeps' _deps.json (Source
    /// "parent-archive:&lt;archive&gt;"); empty when the file is missing or has none.</summary>
    internal static Dictionary<string, string> ReadParentSources(string depsJson)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(depsJson)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(depsJson));
        if (!doc.RootElement.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array) return map;
        foreach (var f in files.EnumerateArray())
        {
            if (f.TryGetProperty("Kind", out var k) && k.GetString() == "parentmatch" &&
                f.TryGetProperty("PcPath", out var p) && p.GetString() is { Length: > 0 } path &&
                f.TryGetProperty("Source", out var s) && s.GetString() is { } src &&
                src.StartsWith("parent-archive:", StringComparison.OrdinalIgnoreCase))
                map[Norm(path)] = src["parent-archive:".Length..].Trim();
        }
        return map;
    }

    /// <summary>DMMDeps' warn list says "also in a parent archive". Its second pass has no game archives in its game
    /// folder, so DMMDeps falls back to every archive its cache ever indexed, mods included, and the cache never drops
    /// one (found 2026-11-19: every file of one mod matched that mod's own archive, cached on an earlier run). So: a match
    /// in an official archive stays a "replaces the game's copy" REVIEW; a match in an archive of one of the plugin's
    /// masters is a REVIEW naming that archive; any other match is a stale cache entry, and the file counts as found
    /// (moved to keep), with one NOTE per plugin. Returns the warn list left for the usual mapping.</summary>
    internal static List<string> SortParentMatches(string name, string plugin, List<string> warn, IReadOnlyDictionary<string, string> sources,
        HashSet<string> keep, List<ReleaseFinding> findings)
    {
        var masters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var m in ReleaseRules.Masters(plugin)) masters.Add(Path.GetFileNameWithoutExtension(m)); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        var left = new List<string>();
        var stale = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in warn)
        {
            if (!sources.TryGetValue(p, out var archive) || IsOfficialArchive(archive)) { left.Add(p); continue; }
            // the file name, not DMMDeps' raw Source, so a folder in it is neither shown nor counted apart (round 8)
            var file = ArchiveFileName(archive);
            var stem = Path.GetFileNameWithoutExtension(file);
            var owner = stem.Contains(" - ") ? stem[..stem.IndexOf(" - ", StringComparison.Ordinal)] : stem;
            if (masters.Contains(owner))
                findings.Add(new(Level.Review, "dmmdeps", $"{name} uses {p}, which is also in its master's archive {file} (DMMDeps): make sure replacing the master mod's copy is deliberate"));
            else
            {
                keep.Add(p);
                stale[file] = stale.GetValueOrDefault(file) + 1;
            }
        }
        if (stale.Count > 0)
        {
            var list = string.Join(", ", stale.Select(kv => $"{kv.Key} ({kv.Value})"));
            Log.Write($"dmmdeps: {name}: matches in archives that are neither the game's nor a master's, counted as found: {list}");
            findings.Add(new(Level.Note, "dmmdeps", $"{name}: {stale.Values.Sum()} file(s) DMMDeps matched in old cached archives that aren't the game's ({list}); counted as found"));
        }
        return left;
    }

    static List<string> ReadList(string path)
    {
        if (!File.Exists(path)) return [];
        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
        return list.Select(Norm).ToList();
    }

    /// <summary>Copies the zip's loose files into the scratch Data folder (not its archives, nor DMMDeps output) and
    /// unpacks its archives there: general archives with Mutagen's reader (DMMDeps walks the NIFs and materials, so
    /// they need their contents), texture archives as empty files (only their names matter; DMMDeps doesn't read
    /// textures). A loose file wins over an archive's copy, as in game. Returns the staged set (the zip's plugins
    /// left out).</summary>
    static HashSet<string> Stage(ReleasePackage pkg, string scratch, List<ReleaseFinding> findings)
    {
        Directory.CreateDirectory(scratch);
        var root = Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var staged = new HashSet<string>(StringComparer.Ordinal);
        var plugins = pkg.Plugins.Select(p => Ba2Names.Normalize(Path.GetRelativePath(pkg.Root, p))).ToHashSet(StringComparer.Ordinal);
        // pkg.Files holds normalized names (lower case, backslashes); the files on disk keep the zip's own spelling and
        // the system's separator, which matters on Linux
        var onDisk = Directory.EnumerateFiles(pkg.Root, "*", SearchOption.AllDirectories)
            .GroupBy(f => Ba2Names.Normalize(Path.GetRelativePath(pkg.Root, f))).ToDictionary(g => g.Key, g => g.First());
        foreach (var rel in pkg.Files)
        {
            if (rel.EndsWith(".ba2", StringComparison.Ordinal) || Generated.IsMatch(Slash(rel)) || plugins.Contains(rel)) continue;
            var dest = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!onDisk.TryGetValue(rel, out var source))
            {
                findings.Add(new(Level.Problem, "dmmdeps", $"{pkg.Name}: {rel} was unpacked under another name, so DMMDeps can't see it"));
                continue;
            }
            File.Copy(source, dest, true);
            staged.Add(Norm(rel));
        }
        foreach (var ba2 in pkg.Archives)
        {
            int n = 0, skipped = 0;
            string type;
            try { type = Ba2Names.Type(ba2); }
            catch (InvalidDataException e) { findings.Add(new(Level.Problem, "dmmdeps", $"{pkg.Name}: {Path.GetFileName(ba2)} can't be read ({e.Message}), so DMMDeps can't see its files")); continue; }
            void Put(string name, Func<byte[]> bytes)
            {
                var norm = Norm(name);
                var dest = Path.GetFullPath(Path.Combine(root, norm.Replace('\\', Path.DirectorySeparatorChar)));
                if (norm.Split('\\').Any(x => x == "..") || norm.Contains(':') || !dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { skipped++; return; }
                staged.Add(norm);
                if (File.Exists(dest)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes());
                n++;
            }
            try
            {
                if (type == "DX10") foreach (var name in Ba2Names.Read(ba2)) Put(name, () => []);
                else
                {
                    var reader = Archive.CreateReader(GameRelease.Starfield, ba2);
                    foreach (var file in reader.Files) Put(file.Path, file.GetBytes);
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                findings.Add(new(Level.Problem, "dmmdeps", $"{pkg.Name}: {Path.GetFileName(ba2)} can't be unpacked ({e.GetType().Name}: {SafeText.OneLine(e.Message)}), " +
                                                           "so DMMDeps can't see its files; rebuild the archive"));
            }
            Log.Write($"dmmdeps: {pkg.Name}: {Path.GetFileName(ba2)} ({type}) unpacked: {n} files" + (skipped > 0 ? $", {skipped} names outside Data skipped" : ""));
        }
        return staged;
    }

    /// <summary>One DMMDeps run; null when it worked, else what went wrong.</summary>
    static string? RunDmmDeps(string exe, string pluginCopy, string root, string scratch, string? gameRoot)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = root,
        };
        foreach (var a in new[] { pluginCopy, "--xboxdata", Path.Combine(root, "xbox"), "--tifroot", Path.Combine(root, "tif"), "--quiet" }) psi.ArgumentList.Add(a);
        var scriptsDir = Path.Combine(scratch, "Scripts");
        if (Directory.Exists(scriptsDir) && Directory.EnumerateFiles(scriptsDir, "*.psc", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).Any())
            psi.ArgumentList.Add("--include-psc");
        if (gameRoot != null) { psi.ArgumentList.Add("--gameroot"); psi.ArgumentList.Add(gameRoot); }
        Log.Write($"dmmdeps: running {exe} {string.Join(" ", psi.ArgumentList.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        var sw = Stopwatch.StartNew();
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"DMMDeps could not be started ({e.Message})";
        }
        if (p == null) return "DMMDeps could not be started";
        using var _ = p;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(DmmDepsTimeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return $"DMMDeps did not finish in {DmmDepsTimeoutSeconds} s and was stopped; run it again (the first run after a game update indexes the game's archives)";
        }
        p.WaitForExit();
        Log.Write($"dmmdeps: exit {p.ExitCode} in {sw.Elapsed.TotalSeconds:F0} s{(gameRoot != null ? " (cache pass)" : "")}");
        if (p.ExitCode == 0) return null;
        var tail = string.Join(" ", (stdout.Result + "\n" + stderr.Result).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
        Log.Write($"dmmdeps: output: {stdout.Result}\n{stderr.Result}");
        return $"DMMDeps exited with {p.ExitCode}{(tail.Length > 0 ? $": {(tail.Length > 300 ? tail[..300] : tail)}" : "")}";
    }

    // ---------------------------------------------------------------- 3. plugin check

    static void PluginCheck(ReleaseContext c, List<(ReleasePackage Package, string Path)> plugins, List<ReleaseFinding> findings)
    {
        var dirs = PluginDirs(c).Concat(c.Options.MastersDirs).ToList();
        foreach (var (_, path) in plugins)
        {
            var name = Path.GetFileName(path);
            Result r;
            try
            {
                r = Checker.Run(new Options { Plugin = path, DataDir = c.DataDir, MastersDirs = dirs, NoAssets = true });
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"plugin-check: {name}: stopped: {e}");
                findings.Add(new(Level.Problem, "plugin-check", $"{name}: could not check it ({e.GetType().Name}: {SafeText.OneLine(e.Message)}); " +
                                                               $"run sf-mod-check check on it with --log for details"));
                continue;
            }
            foreach (var g in r.Findings.GroupBy(f => (f.Severity, f.Check)).OrderBy(g => g.Key.Severity == "ERROR" ? 0 : 1))
            {
                var level = g.Key.Severity == "ERROR" ? Level.Problem : Level.Warning;
                foreach (var f in g.Take(MaxPerCheck)) findings.Add(new(level, "plugin-check", $"{name}: [{f.Check}] {f.Text}"));
                if (g.Count() > MaxPerCheck) findings.Add(new(level, "plugin-check", $"{name}: [{g.Key.Check}] ... and {g.Count() - MaxPerCheck} more (sf-mod-check check {name} lists them all)"));
            }
            var note = r.CannotCheckLinks ? "; links not checked" : "";
            if (r.ScenesNotChecked > 0) note += $"; {r.ScenesNotChecked} official scene{(r.ScenesNotChecked == 1 ? "" : "s")} not checked ({r.ScenesNotCheckedWhy})";
            findings.Add(new(Level.Note, "plugin-check", $"{name}: {(r.Errors > 0 ? "FAILED" : "OK")}, {r.Errors} errors, {r.Warnings} warnings{note} ({r.Summary})"));
        }
    }
}
