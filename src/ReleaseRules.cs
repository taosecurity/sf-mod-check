// sfmodcheck release: zip layout and name rules, and sound files in archives (0.7.0, chunk C1). Ported from
// the author's earlier Python release check (dropped_name, FORBIDDEN_MASTERS, generic_root_md, PRIVATE_FILES,
// voice_compression).
// Engine rules (break the mod for every player):
//   plugin-name       a plugin file name Starfield removes from Plugins.txt (proven 2026-11-12 for names ending in
//                     "-ShatteredSpace.esm" and "-Constellation.esm"; the other official endings as a precaution;
//                     "BlueprintShips-" names as LOOT documents)
//   voice-compressed  .wem or .ffxanim stored compressed in a .ba2: the game plays them silent (every working voice
//                     archive stores them as is; Archive2 compresses by default)
//   ba2-unreadable    an archive whose header or records don't add up
//   archive-name      (0.14.0) a .ba2 not named "<plugin> - <part>.ba2" for a plugin in the zips, Data or --masters-dir:
//                     PROBLEM in a zip with a plugin, WARNING in one without or when an INI in the zip lists it;
//                     (0.15.1) WARNING when <part> isn't Main or Textures (optionally 01-99) or Voices_<language>,
//                     or a numbered part has a gap
//   file-name         (0.14.0, WARNING) a non-ASCII character in a loose path or a name inside an archive; a loose
//                     data-relative path over 200 characters
// House rules (RuleKind.House, a WARNING in the basic profile):
//   master            a BlueprintShips* master in the plugin's header
//   one-plugin        more than one plugin in a zip (Vortex and other managers install one zip as one mod)
//   script-source     .psc files in the zip (players need only the compiled .pex)
//   root-md           a .md at the zip's root without a hyphenated mod name (README.md lands in Data and overwrites
//                     another mod's copy)
//   private-file      permission records and screenshots, private comparison notes
// 0.15.0: a folder or the run's loose files (not a zip) skip one-plugin and root-md, with one NOTE [not-a-zip].
using System.Text;
using System.Text.RegularExpressions;

namespace SfModCheck;

public static class ReleaseRules
{
    static readonly Regex[] DroppedPluginNames =
    [
        new(@"-(starfield|constellation|oldmars|shatteredspace|sfbgs[0-9a-z]+|sfta0\d)\.es[mpl]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^blueprintships-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>True when Starfield would drop this plugin name from Plugins.txt (it ends in an official plugin's name,
    /// or starts with BlueprintShips-), so the plugin never loads. Also used by `patch` for its --name (0.9.0).</summary>
    public static bool IsDroppedPluginName(string fileName) => DroppedPluginNames.Any(r => r.IsMatch(fileName));

    static readonly Regex ForbiddenMaster = new(@"^blueprintships", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly (Regex Rx, string Why)[] PrivateFiles =
    [
        (new(@"(^|/)permission[^/]*\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "permission record (quotes an author's private message)"),
        (new(@"(^|/)[^/]*permission request[^/]*\.(png|jpe?g|tiff?|webp|gif|bmp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "permission screenshot (an author's private message)"),
        (new(@"(^|/)comparison[^/]*\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "private comparison notes"),
        (new(@"(^|/)comparison/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "private comparison folder"),
    ];

    static readonly string[] SoundExts = [".wem", ".ffxanim"];

    /// <summary>True if Starfield would drop a plugin with this file name from Plugins.txt.</summary>
    public static bool DroppedName(string fileName)
    {
        var b = Path.GetFileName(fileName.Replace('\\', '/').Split('/')[^1]);
        return DroppedPluginNames.Any(rx => rx.IsMatch(b));
    }

    /// <summary>Root-level .md names with no hyphen in the stem (README.md, LICENSE.md), from normalized paths.</summary>
    public static bool GenericRootMd(string rel) =>
        !rel.Contains('\\') && !rel.Contains('/') && rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !rel[..^3].Contains('-');

    /// <summary>Why a file must never ship, or null.</summary>
    public static string? PrivateReason(string rel)
    {
        var p = rel.Replace('\\', '/');
        foreach (var (rx, why) in PrivateFiles)
            if (rx.IsMatch(p)) return why;
        return null;
    }

    /// <summary>Master file names from the plugin's TES4 header (its MAST subrecords). Empty when the header can't
    /// be read (the plugin check reports a damaged plugin).</summary>
    public static List<string> Masters(string plugin)
    {
        var o = new List<string>();
        byte[] head;
        using (var f = File.OpenRead(plugin))
        {
            var first = new byte[24];
            if (f.ReadAtLeast(first, 24, throwOnEndOfStream: false) < 24 || Encoding.ASCII.GetString(first, 0, 4) != "TES4") return o;
            long size = BitConverter.ToUInt32(first, 4);
            if (size > f.Length - 24) size = f.Length - 24;
            head = new byte[size];
            f.ReadExactly(head);
        }
        int pos = 0;
        while (pos + 6 <= head.Length)
        {
            var sig = Encoding.ASCII.GetString(head, pos, 4);
            int n = BitConverter.ToUInt16(head, pos + 4);
            if (pos + 6 + n > head.Length) break;
            if (sig == "MAST") o.Add(Encoding.ASCII.GetString(head, pos + 6, n).TrimEnd('\0'));
            pos += 6 + n;
        }
        return o;
    }

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        var o = new List<ReleaseFinding>();
        foreach (var p in c.Packages)
        {
            var zip = p.Name;
            // the zip's own spelling of each file, for the report
            var shown = Directory.Exists(p.Root)
                ? Directory.EnumerateFiles(p.Root, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(p.Root, f))
                    .GroupBy(Ba2Names.Normalize).ToDictionary(g => g.Key, g => g.First())
                : new Dictionary<string, string>();
            string Show(string norm) => shown.TryGetValue(norm, out var s) ? s : norm;
            string Rel(string full) => Path.GetRelativePath(p.Root, full);

            // ---- engine: plugin names ----
            foreach (var pl in p.Plugins)
                if (DroppedName(pl))
                    o.Add(new(Level.Problem, "plugin-name",
                        $"{zip}: plugin name {Rel(pl)} ends in an official plugin's name (or starts with BlueprintShips-): " +
                        "Starfield removes it from Plugins.txt and it stops loading; rename it, e.g. -ShatteredSpaceAddon.esm"));

            // ---- engine: sound files in archives ----
            foreach (var a in p.Archives)
            {
                var label = $"{zip}:{Rel(a)}";
                List<Ba2Names.Entry> entries;
                try { entries = Ba2Names.ReadEntries(a); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    o.Add(new(Level.Problem, "ba2-unreadable", $"{label}: can't read the archive to check its sound files ({e.Message}); rebuild it with Archive2"));
                    continue;
                }
                var sound = entries.Where(e => SoundExts.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase))).ToList();
                if (sound.Count == 0) continue;
                var packed = sound.Where(e => e.Compressed).ToList();
                if (packed.Count > 0)
                    o.Add(new(Level.Problem, "voice-compressed",
                        $"{label}: {packed.Count} of {sound.Count} sound files (.wem/.ffxanim) are compressed (first: {packed[0].Name}); " +
                        "the game plays them silent. Repack with Archive2 -compression=None (for a mixed archive, put the sound files in their own <Mod> - Voices_en.ba2)"));
                else
                    o.Add(new(Level.Note, "voice-compressed", $"{label}: {sound.Count} sound files, all stored uncompressed"));
            }

            // ---- engine: archive names and file names (0.14.0) ----
            o.AddRange(ArchiveNames(c, p, Rel, Show));
            o.AddRange(FileNames(p, Show));

            // ---- house: BlueprintShips master ----
            foreach (var pl in p.Plugins)
            {
                List<string> masters;
                try { masters = Masters(pl); }
                catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"masters of {Path.GetFileName(pl)} not read: {e.Message}"); continue; }
                foreach (var m in masters.Where(m => ForbiddenMaster.IsMatch(m)))
                    o.Add(new(Level.Problem, "master",
                        $"{zip}: plugin {Rel(pl)} has the master {m}; a mod shouldn't depend on a BlueprintShips plugin; remove that master",
                        RuleKind.House));
            }

            // ---- 0.15.0: a folder or the loose files aren't a zip, so the zip-only rules skip them ----
            if (!p.IsZip)
            {
                Log.Write($"zip-only rules (one-plugin, root-md) skipped for {zip}: not a zip");
                o.Add(new(Level.Note, "not-a-zip",
                    $"{zip}: not a zip, so the zip-only rules (one-plugin, root-md) were skipped; run release on the zip you upload to check them"));
            }

            // ---- house: one plugin per zip ----
            if (p.IsZip && p.Plugins.Count > 1)
                o.Add(new(Level.Problem, "one-plugin",
                    $"{zip}: {p.Plugins.Count} plugins in one zip ({string.Join(", ", p.Plugins.Select(Rel))}); " +
                    "put each plugin in its own zip, such as <ModName>-<Part>-<version>.zip, so each one installs as its own mod",
                    RuleKind.House));

            // ---- house: script sources ----
            var psc = p.Files.Where(f => f.EndsWith(".psc", StringComparison.Ordinal)).ToList();
            if (psc.Count > 0)
                o.Add(new(Level.Problem, "script-source",
                    $"{zip}: {psc.Count} Papyrus script source{(psc.Count == 1 ? "" : "s")} (.psc) in the zip (first: {Show(psc[0])}); " +
                    "players need only the compiled .pex; rebuild the zip without the .psc files",
                    RuleKind.House));

            // ---- house: root .md names ----
            foreach (var f in p.Files.Where(f => p.IsZip && GenericRootMd(f)))
                o.Add(new(Level.Problem, "root-md",
                    $"{zip}: root .md without the mod's name (overwrites other mods' copy in Data): {Show(f)}; rename it, e.g. README-<ModName>.md",
                    RuleKind.House));

            // ---- house: private files ----
            foreach (var f in p.Files)
                if (PrivateReason(f) is { } why)
                    o.Add(new(Level.Problem, "private-file",
                        $"{zip}: private file in the zip, must never ship ({why}): {Show(f)}; rebuild the zip without it",
                        RuleKind.House));
        }
        return o;
    }

    // ---------------------------------------------------------------- 0.14.0: archive names and file names

    /// <summary>The longest loose data-relative path that stays under Windows' 260-character limit (MAX_PATH) in Steam's
    /// usual install: 260 less the 59 characters of "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data" and a
    /// separator.</summary>
    public const int MaxLoosePath = 200;

    /// <summary>How many files one zip's file-name findings name before the rest become one "and N more" line.</summary>
    public const int MaxNamed = 5;

    /// <summary>A path as the game sees it in Data: no "data\" in front (a zip may hold a Data folder).</summary>
    static string InData(string norm) => norm.StartsWith("data\\", StringComparison.Ordinal) ? norm[5..] : norm;

    static string DirOf(string norm) { var i = norm.LastIndexOf('\\'); return i < 0 ? "" : norm[..i]; }

    /// <summary>archive-name: the game loads a .ba2 only when it is named "&lt;plugin&gt; - &lt;part&gt;.ba2" beside that
    /// plugin, or when an INI lists it. An archive with any other name, in a zip that holds a plugin, is a PROBLEM (it
    /// never loads); in a zip with no plugin it is a WARNING (it may be meant for an INI). An archive named after a plugin
    /// in the Data folder or a --masters-dir folder (the game's own, or the mod this one patches) is fine, and one that an
    /// INI in the zip lists is a WARNING. The Xbox twin ("... - Main_xbox.ba2") carries the same name and passes the same
    /// way. 0.15.1: the game's own loader opens only the parts Main and Textures, each also numbered 01 to 99 and stopping
    /// at the first missing number (NOTES "RE: archive loader (0.15.0)"), so a plugin's archive with another part, or a
    /// numbered one after a gap, is a WARNING (an INI can still load it on purpose).</summary>
    static IEnumerable<ReleaseFinding> ArchiveNames(ReleaseContext c, ReleasePackage p, Func<string, string> rel, Func<string, string> show)
    {
        var o = new List<ReleaseFinding>();
        // numbered Main and Textures archives by folder, plugin, part and Xbox suffix (all lower case)
        var numbered = new Dictionary<(string Dir, string Plugin, string Part, string Xbox), List<(int N, string Path)>>();
        // every plugin in the zips, by its folder in Data and its stem
        var plugins = c.Packages.SelectMany(q => q.Plugins.Select(pl => InData(Ba2Names.Normalize(Path.GetRelativePath(q.Root, pl))))).ToList();
        var inis = p.Files.Where(f => f.EndsWith(".ini", StringComparison.Ordinal))
            .Select(f => (Rel: show(f), Text: SafeRead(Path.Combine(p.Root, show(f))))).ToList();
        foreach (var a in p.Archives)
        {
            var norm = InData(Ba2Names.Normalize(Path.GetRelativePath(p.Root, a)));
            var dir = DirOf(norm);
            var file = norm[(dir.Length == 0 ? 0 : dir.Length + 1)..];
            var stem = file[..^4];
            // with Foo.esm and "Foo - Patch.esm" both there, "Foo - Patch - Main.ba2" belongs to the one whose part loads
            var owners = plugins.Where(pl => DirOf(pl) == dir)
                .Select(pl => Path.GetFileNameWithoutExtension(pl[(dir.Length == 0 ? 0 : dir.Length + 1)..]))
                .Where(s => stem.StartsWith(s + " - ", StringComparison.Ordinal)).ToList();
            var owner = owners.FirstOrDefault(s => LoadedPart.IsMatch(stem[(s.Length + 3)..])) ?? owners.FirstOrDefault();
            if (owner != null)
            {
                CheckPart(a, dir, owner, stem[(owner.Length + 3)..], $"{owner} (a plugin in the zips)");
                continue;
            }
            if (dir.Length == 0 && OutsideOwner(c, stem) is { } outside)
            {
                var plugin = Path.GetFileNameWithoutExtension(outside).ToLowerInvariant();
                CheckPart(a, dir, plugin, stem[(plugin.Length + 3)..], outside);
                continue;
            }
            var listedBy = inis.FirstOrDefault(t => t.Text.Contains(file, StringComparison.OrdinalIgnoreCase)).Rel;
            var why = dir.Length > 0
                ? $"it isn't in the Data folder's top level beside a plugin of that name (it is in {dir}\\)"
                : stem.Contains(" - ") ? "no plugin in the zips has the name before its \" - \""
                : "its name has no \" - \" part";
            var fix = "The game loads an archive only when it is named \"<plugin> - <part>.ba2\" (with spaces around the dash) beside " +
                      "a plugin of that name, or when an INI lists it; rename it, for example MyMod - Main.ba2 for MyMod.esm";
            if (listedBy != null)
            {
                Log.Write($"archive-name: {rel(a)} not named after a plugin, but {listedBy} lists it: WARNING");
                o.Add(new(Level.Warning, "archive-name", $"{p.Name}: archive {rel(a)}: {why}; {listedBy} lists it, so it loads only if that INI's archive list is used. {fix}"));
            }
            else if (p.Plugins.Count > 0)
            {
                Log.Write($"archive-name: {rel(a)}: {why}: PROBLEM");
                o.Add(new(Level.Problem, "archive-name", $"{p.Name}: archive {rel(a)}: {why}, so the game never loads it. {fix}"));
            }
            else
            {
                Log.Write($"archive-name: {rel(a)}: {why}, zip has no plugin: WARNING");
                o.Add(new(Level.Warning, "archive-name", $"{p.Name}: archive {rel(a)}: {why}, and the zip holds no plugin, so it loads only if an INI lists it. {fix}"));
            }
        }
        // 0.15.1: a numbered part loads only when every number from 01 up to it is there (the game stops at the first gap)
        foreach (var g in numbered)
        {
            var have = g.Value.Select(x => x.N).ToHashSet();
            var missing = Enumerable.Range(1, 99).FirstOrDefault(n => !have.Contains(n));
            if (missing == 0) continue;   // 01 to 99 all there: no gap
            foreach (var (n, a) in g.Value.Where(x => x.N > missing).OrderBy(x => x.N))
            {
                // the missing name in this archive's own spelling: its name with the two digits replaced
                var name = Path.GetFileName(a);
                var at = name.Length - ".ba2".Length - g.Key.Xbox.Length - 2;
                var gap = $"{name[..at]}{missing:00}{name[(at + 2)..]}";
                Log.Write($"archive-name: {rel(a)}: {gap} is missing: WARNING");
                o.Add(new(Level.Warning, "archive-name", $"{p.Name}: archive {rel(a)}: the game loads numbered archives from 01 upward and stops at the first missing number, and there is no {gap}, so the game doesn't load this archive by itself. {IniNote} Otherwise number the archives 01, 02 and so on without gaps."));
            }
        }
        return o;

        // the part after "<plugin> - ": the game's loader tries only Main and Textures, each also numbered 01 to 99
        void CheckPart(string a, string dir, string plugin, string part, string loadsWith)
        {
            var m = LoadedPart.Match(part);
            if (!m.Success)
            {
                Log.Write($"archive-name: {rel(a)}: part \"{part}\" isn't Main, Textures or Voices_<language>: WARNING");
                o.Add(new(Level.Warning, "archive-name", $"{p.Name}: archive {rel(a)}: the game loads a plugin's archives by itself only when the part after \" - \" is Main or Textures (optionally numbered 01 to 99) or a voice archive such as Voices_en, so it doesn't load this one by itself. {IniNote} Otherwise rename it, for example to {FreeMainName(c, dir, Path.GetFileName(a)[..plugin.Length])}."));
                return;
            }
            Log.Write($"archive-name: {rel(a)} loads with {loadsWith}");
            if (m.Groups[2].Success)
            {
                var key = (Dir: dir, Plugin: plugin, Part: m.Groups[1].Value, Xbox: m.Groups[3].Value);
                if (!numbered.TryGetValue(key, out var list)) numbered[key] = list = [];
                list.Add((int.Parse(m.Groups[2].Value), a));
            }
        }
    }

    /// <summary>The archive parts the game's plugin loader opens by itself (names are lower case here): Main or Textures,
    /// optionally numbered 01 to 99, and the Xbox twin of each; and the voice archive of a language, Voices_&lt;language&gt;
    /// (the official add-ons ship "&lt;plugin&gt; - Voices_en.ba2", "- Voices_de" and so on, listed in no INI, and the game
    /// holds the part string " - Voices_"; not numbered).</summary>
    static readonly Regex LoadedPart = new(@"^(?:(main|textures)(0[1-9]|[1-9][0-9])?(_xbox)?|voices_[a-z][a-z0-9_]*)$", RegexOptions.CultureInvariant);

    const string IniNote = "An INI archive list (for example sResourceIndexFileList in the [Archive] section) can still load it on purpose; if that is the plan, ignore this warning.";

    /// <summary>A rename the part check can suggest that takes no archive already there (0.15.1 review round 2):
    /// "&lt;plugin&gt; - Main.ba2" when it is free, else the first free "&lt;plugin&gt; - MainNN.ba2". Taken names are the
    /// archives beside it in the zips and, at the top level, the files in the Data folder and the --masters-dir folders,
    /// so the advice never overwrites the mod's own Main archive or the patched mod's.</summary>
    static string FreeMainName(ReleaseContext c, string dir, string plugin)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in c.Packages)
            foreach (var a in q.Archives)
            {
                var n = InData(Ba2Names.Normalize(Path.GetRelativePath(q.Root, a)));
                if (DirOf(n) == dir) taken.Add(Path.GetFileName(a));
            }
        if (dir.Length == 0)
            foreach (var d in new[] { c.DataDir }.Concat(c.Options.MastersDirs).Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(d, "*.ba2")) taken.Add(Path.GetFileName(f));
        var main = $"{plugin} - Main.ba2";
        if (!taken.Contains(main)) return main;
        return Enumerable.Range(1, 99).Select(n => $"{plugin} - Main{n:00}.ba2").FirstOrDefault(s => !taken.Contains(s)) ?? main;
    }

    /// <summary>The plugin outside the zips (in the Data folder or a --masters-dir folder) that an archive's name starts
    /// with, followed by " - ", or null. When several match (Foo.esm and "Foo - Patch.esm"), the one whose part the game
    /// loads wins, else the shortest name. The stem is lower case, so plugin names are compared ignoring case (Linux file
    /// names are case-sensitive).</summary>
    static string? OutsideOwner(ReleaseContext c, string stem)
    {
        var dirs = new[] { c.DataDir }.Concat(c.Options.MastersDirs).Where(Directory.Exists).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d))
                if (Path.GetExtension(f).ToLowerInvariant() is ".esm" or ".esp" or ".esl")
                    names.TryAdd(Path.GetFileName(f), Path.GetFileName(f));
        string? first = null;   // 0.15.1: prefer the plugin whose part the game loads (see ArchiveNames)
        for (int i = stem.IndexOf(" - ", StringComparison.Ordinal); i > 0; i = stem.IndexOf(" - ", i + 1, StringComparison.Ordinal))
            foreach (var ext in new[] { ".esm", ".esp", ".esl" })
                if (names.TryGetValue(stem[..i] + ext, out var real))
                {
                    if (LoadedPart.IsMatch(stem[(i + 3)..])) return real;
                    first ??= real;
                }
        return first;
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"couldn't read {path}: {e.Message}"); return ""; }
    }

    /// <summary>file-name WARNINGs: a data-relative path (a loose file, or a name inside one of the zip's archives) with a
    /// character outside ASCII, which archive tools and the game's lower-casing don't handle safely; and a loose path
    /// over MaxLoosePath characters, which goes past Windows' 260-character limit once installed. Up to MaxNamed files
    /// are named, then one line counts the rest.</summary>
    static readonly HashSet<string> GameRootExts = new(StringComparer.OrdinalIgnoreCase) { ".esm", ".esp", ".esl", ".ba2", ".ini" };

    static IEnumerable<ReleaseFinding> FileNames(ReleasePackage p, Func<string, string> show)
    {
        var hits = new List<string>();
        foreach (var f in p.Files)
        {
            var d = InData(f);
            // files the game never loads: the zip's fomod folder, and files at the top of Data that aren't plugins,
            // archives or INI files (a README, an image); files in any other folder are checked (code review 0.14.0)
            if (d.StartsWith("fomod\\", StringComparison.Ordinal) ||
                !d.Contains('\\') && !GameRootExts.Contains(Path.GetExtension(d))) continue;
            if (d.Any(ch => ch > 0x7E))
            {
                Log.Write($"file-name: {show(f)}: non-ASCII character");
                hits.Add($"{p.Name}: {show(f)} has a character outside plain ASCII (letters with accents, other scripts, symbols); " +
                         "archive tools and the game may not find it. Rename it with plain letters, digits and - _ .");
            }
            if (d.Length > MaxLoosePath)
            {
                Log.Write($"file-name: {show(f)}: {d.Length} characters");
                hits.Add($"{p.Name}: {show(f)} is {d.Length} characters long inside Data, more than {MaxLoosePath}: installed under Steam's " +
                         "usual Data folder it passes Windows' 260-character path limit (a longer install path lowers the limit), and some " +
                         "tools won't copy it. Shorten its folder or file names");
            }
        }
        foreach (var a in p.Archives)
        {
            List<string> names;
            try { names = Ba2Names.Read(a); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }   // ba2-unreadable reports it
            foreach (var n in names.Where(n => n.Any(ch => ch > 0x7E)))
            {
                Log.Write($"file-name: {Path.GetFileName(a)}: {n}: non-ASCII character");
                hits.Add($"{p.Name}:{Path.GetRelativePath(p.Root, a)}: {n} has a character outside plain ASCII (letters with accents, other scripts, " +
                         "symbols); the game may not find it. Rename it with plain letters, digits and - _ . and pack the archive again");
            }
        }
        var o = hits.Take(MaxNamed).Select(h => new ReleaseFinding(Level.Warning, "file-name", h)).ToList();
        if (hits.Count > MaxNamed)
            o.Add(new(Level.Warning, "file-name", $"{p.Name}: and {hits.Count - MaxNamed} more file-name warnings like these (--verbose lists them all)"));
        return o;
    }
}
