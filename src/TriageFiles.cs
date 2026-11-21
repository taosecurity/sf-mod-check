// Triage sections "Files" and "Replaces vanilla" (0.8.0, chunk C1).
// Files: every file with its size and SHA-256, the contents of each archive (.ba2), version numbers in names, which
// archives load with which plugin, archives inside the mod that triage doesn't open, and files outside the usual Data
// layout (program files at the top, FOMOD installers, folders the game doesn't read).
// Replaces vanilla: the mod's loose files and archive entries whose path is also in the game's own archives. The game's
// paths come from VanillaIndex (below), built from the official archives' name tables and cached under
// LearnedCache.Root\vanilla-index, keyed by each archive's name, size and modified time, so a game update rebuilds it.
// A replaced vanilla script is a LOOK; other replaced files are NOTEs, one per folder and file type, with every file in
// the table.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SfModCheck;

public static class TriageFiles
{
    /// <summary>One entry of an archive: its normalized name, its unpacked size (null when the records couldn't be
    /// read) and whether it is stored compressed.</summary>
    public readonly record struct ArchiveEntry(string Name, long? Size, bool Compressed);

    /// <summary>An archive of the mod as read: its entries, and why it couldn't be read (null when it could). Names can be
    /// readable while the file records aren't: then the entries have no sizes and RecordsError says why.</summary>
    public sealed record ArchiveListing(string Rel, List<ArchiveEntry> Entries, string? Error, string? RecordsError);

    /// <summary>Top folders the game reads under Data (from the official archives' contents, plus SFSE's and MCM's).</summary>
    public static readonly HashSet<string> DataFolders = new(StringComparer.Ordinal)
    {
        "sound", "geometries", "meshes", "textures", "scripts", "terrain", "lodsettings", "planetdata", "particles", "strings",
        "interface", "materials", "misc", "space", "distantlod", "shadersfx", "music", "video", "sfse", "mcm",
    };

    static readonly HashSet<string> PluginExts = new(StringComparer.Ordinal) { ".esm", ".esp", ".esl" };
    static readonly HashSet<string> NestedExts = new(StringComparer.Ordinal) { ".zip", ".7z", ".rar" };
    static readonly HashSet<string> BinaryExts = new(StringComparer.Ordinal) { ".dll", ".exe", ".asi" };
    // Files that sit at the top of a download without meaning anything to the game: read-mes, pictures, licenses.
    static readonly HashSet<string> DocExts = new(StringComparer.Ordinal)
    {
        ".txt", ".md", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".html", ".htm", ".url", ".rtf", ".json", ".xml", ".docx", ".csv", "",
    };

    /// <summary>Most single replaced scripts listed one LOOK each; the rest are counted in one more LOOK.</summary>
    public const int MaxScriptLooks = 25;

    public static IEnumerable<TriageFinding> Run(TriageContext c)
    {
        var findings = new List<TriageFinding>();
        var archives = ReadArchives(c);
        FilesSection(c, archives, findings);
        ReplacesVanilla(c, archives, findings);
        return findings;
    }

    // ---------------------------------------------------------------- reading

    static Dictionary<string, ArchiveListing> ReadArchives(TriageContext c)
    {
        var map = new Dictionary<string, ArchiveListing>(StringComparer.Ordinal);
        foreach (var rel in c.Files.Where(f => RelPath.Extension(f) == ".ba2"))
        {
            var full = c.Full(rel);
            try
            {
                var names = Ba2Names.Read(full);
                try
                {
                    map[rel] = new ArchiveListing(rel, ReadEntries(full), null, null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    map[rel] = new ArchiveListing(rel, names.Select(n => new ArchiveEntry(n, null, false)).ToList(), null, e.Message);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                map[rel] = new ArchiveListing(rel, new(), e.Message, null);
            }
            Log.Write($"triage files: archive {rel}: {(map[rel].Error ?? $"{map[rel].Entries.Count} entries")}");
        }
        return map;
    }

    /// <summary>Every entry of a BA2 with its unpacked size and compression. GNRL records are 36 bytes (packed size at 24,
    /// unpacked at 28); DX10 records are a 24-byte header (chunk count at 13, chunk header size at 14) plus one chunk
    /// header per chunk (packed size at 8, unpacked at 12); a texture's size is the sum of its chunks (the DDS header the
    /// game adds isn't counted). Header extras as Ba2Names.ReadEntries. Throws InvalidDataException, naming the file,
    /// when the header or records don't add up.</summary>
    public static List<ArchiveEntry> ReadEntries(string path)
    {
        var names = Ba2Names.Read(path);   // checks the header and the name table
        var name = RelPath.FileName(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var head = new byte[24];
        f.ReadExactly(head);
        var version = BitConverter.ToUInt32(head, 4);
        var type = Encoding.ASCII.GetString(head, 8, 4);
        var nameOfs = (long)BitConverter.ToUInt64(head, 16);
        long pos = 24 + version switch { 2 => 8, 3 => 12, _ => 0 };
        if (type != "GNRL" && type != "DX10") throw new InvalidDataException($"{name}: unsupported archive type \"{type}\" (expected GNRL or DX10)");
        if (pos > nameOfs) throw new InvalidDataException($"{name}: its name table starts inside the header");
        f.Seek(pos, SeekOrigin.Begin);
        var buf = new byte[36];
        void Take(int n, string what)
        {
            if (f.Position + n > nameOfs) throw new InvalidDataException($"{name}: {what} run into the name table");
            f.ReadExactly(buf, 0, n);
        }
        var result = new List<ArchiveEntry>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            bool packed = false;
            long size = 0;
            if (type == "GNRL")
            {
                Take(36, $"its file records (entry {i + 1} of {names.Count})");
                packed = BitConverter.ToUInt32(buf, 24) != 0;
                size = BitConverter.ToUInt32(buf, 28);
            }
            else
            {
                Take(24, $"its texture records (entry {i + 1} of {names.Count})");
                int chunks = buf[13];
                int chunkHdr = BitConverter.ToUInt16(buf, 14);
                if (chunkHdr == 0) chunkHdr = 24;
                if (chunkHdr < 16) throw new InvalidDataException($"{name}: entry {i + 1} has a chunk header size of {chunkHdr} bytes, too small to hold its sizes");
                for (int k = 0; k < chunks; k++)
                {
                    Take(16, $"the chunks of entry {i + 1} of {names.Count}");
                    if (BitConverter.ToUInt32(buf, 8) != 0) packed = true;
                    size += BitConverter.ToUInt32(buf, 12);
                    if (f.Position - 16 + chunkHdr > nameOfs) throw new InvalidDataException($"{name}: the chunks of entry {i + 1} of {names.Count} run into the name table");
                    f.Seek(chunkHdr - 16, SeekOrigin.Current);
                }
            }
            result.Add(new ArchiveEntry(names[i], size, packed));
        }
        return result;
    }

    /// <summary>A mod path as the game would see it under Data: the "data\" prefix dropped (c.DataPath), then one
    /// wrapper folder the game doesn't read ("MyMod\", a FOMOD option such as "00 Main\") when the next folder is Data or
    /// one the game reads.</summary>
    public static string GamePath(TriageContext c, string rel)
    {
        var p = c.DataPath(rel);
        for (int i = 0; i < 3; i++)
        {
            var slash = p.IndexOf('\\');
            if (slash < 0) return p;
            var top = p[..slash];
            if (DataFolders.Contains(top)) return p;
            var rest = p[(slash + 1)..];
            if (top == "data") { p = rest; continue; }
            var s2 = rest.IndexOf('\\');
            var next = s2 < 0 ? null : rest[..s2];
            var restIsPluginOrArchive = s2 < 0 && (PluginExts.Contains(RelPath.Extension(rest)) || RelPath.Extension(rest) == ".ba2");
            if (next != null && (DataFolders.Contains(next) || next == "data") || restIsPluginOrArchive) { p = rest; continue; }
            return p;
        }
        return p;
    }

    // ---------------------------------------------------------------- Files

    // A version number in a name: "1.2", "1_2_3", "v2", "v1.0" (not "Main01" or "1x1").
    static readonly Regex VersionRx = new(@"(?<![a-z0-9])v\d+(?:[._]\d+){0,3}(?![a-z0-9])|(?<!\d)\d+(?:[._]\d+){1,3}(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The version number in a file name's stem, or null.</summary>
    public static string? VersionIn(string fileName)
    {
        var m = VersionRx.Match(RelPath.FileNameWithoutExtension(fileName));
        return m.Success ? m.Value : null;
    }

    static void FilesSection(TriageContext c, Dictionary<string, ArchiveListing> archives, List<TriageFinding> findings)
    {
        const string S = TriageSections.Files;
        void Note(string file, string text) => findings.Add(new TriageFinding(TriageLevel.Note, S, file, text));
        void Look(string file, string text) => findings.Add(new TriageFinding(TriageLevel.Look, S, file, text));

        // every file, with size and SHA-256
        var every = c.Report.Table(S, "Every file", "File", "Size (bytes)", "SHA-256");
        long totalBytes = 0;
        // what the input holds but triage didn't read (links, a second file whose name differs only in case): a LOOK
        // each, and a row in the table too, marked "link" or "same name" (code review of 0.9.1: they were missing from
        // it; "?" stays for a file whose hash couldn't be read). A file skipped for its name's case is named by its own
        // spelling, and so is the one kept in its place (Shown), so the table shows both real files side by side.
        foreach (var (rel, why) in c.Skipped) Look(rel, why);
        static string Marker(string why) =>
            why is TriageCommand.LinkWhy or TriageCommand.LinkReachedWhy ? "link"
            : why.StartsWith(TriageCommand.CaseWhyStart, StringComparison.Ordinal) ? "same name" : "skipped";
        var rows = c.Files.Select(f => (Rel: f, Skip: (string?)null))
            .Concat(c.Skipped.Select(s => (Rel: s.File, Skip: (string?)Marker(s.Why))))
            .OrderBy(r => Ba2Names.Normalize(r.Rel), StringComparer.Ordinal).ThenBy(r => r.Skip != null).ThenBy(r => r.Rel, StringComparer.Ordinal).ToList();
        foreach (var (rel, skip) in rows)
        {
            if (skip != null) { every.Add(rel, skip, ""); continue; }
            var full = c.Full(rel);
            if (c.BehindLink(full))
            {
                // only a link leads to it (0.8.0 follow-up: this read as "couldn't be read" under a made-up path). The
                // run moves such files to Skipped first (SetAsideLinked); this is for a context made without that.
                every.Add(rel, "link", "");
                Look(rel, TriageCommand.LinkReachedWhy);
                continue;
            }
            try
            {
                var size = new FileInfo(full).Length;
                totalBytes += size;
                using var s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                every.Add(c.Shown(rel), size.ToString("N0"), Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                every.Add(c.Shown(rel), "?", "");
                Look(rel, $"couldn't be read to take its hash ({e.Message})");
            }
        }
        Log.Write($"triage files: {c.Files.Count} files hashed, {totalBytes:N0} bytes");
        // one neutral note when any link was skipped (settled design of 0.9.1, NOTES-triage.md: don't reopen). Every
        // LinkWhy entry counts, whatever it is; files reached through a link are listed above but aren't links. The
        // Plugins missing-script LOOK and the Scripts note mention the same number.
        var links = c.SkippedLinks;
        if (links > 0)
            Note("", $"{TriageContext.LinkedItems(links)} {(links == 1 ? "was" : "were")} skipped and not read (named in this section); " +
                     $"anything inside {(links == 1 ? "it" : "them")} wasn't checked. Run triage on what the link points to.");

        // archive contents
        var contents = c.Report.Table(S, "Archive contents", "Archive", "Entry", "Size (bytes)", "Stored");
        foreach (var (rel, a) in archives)
        {
            if (a.Error != null)
            {
                Look(rel, $"couldn't be read as a game archive ({a.Error}), so its contents aren't listed or compared with the game's files");
                continue;
            }
            if (a.RecordsError != null)
                Look(rel, $"its file names were read but not its file records ({a.RecordsError}), so its sizes aren't shown; the game may not read it either");
            foreach (var e in a.Entries)
                contents.Add(rel, e.Name, e.Size?.ToString("N0") ?? "?", e.Size == null ? "?" : e.Compressed ? "compressed" : "as is");
        }

        // version numbers in names (plugins, archives, programs and archives inside the mod)
        foreach (var rel in c.Files)
        {
            var ext = RelPath.Extension(rel);
            if (!PluginExts.Contains(ext) && ext != ".ba2" && !BinaryExts.Contains(ext) && !NestedExts.Contains(ext)) continue;
            if (VersionIn(RelPath.FileName(rel)) is not { } v) continue;
            if (PluginExts.Contains(ext))
                Note(rel, $"the plugin's name carries a version number ({v}). The game knows a plugin by its file name, so a later " +
                          "version under another name counts as a different plugin: saves and patches made for this one won't find it");
            else if (ext == ".ba2")
                Note(rel, $"the archive's name carries a version number ({v}); the game loads an archive by its plugin's name, so the two must match");
            else
                Note(rel, $"the file name carries a version number ({v})");
        }

        // plugins and their archives
        var plugins = c.Files.Where(f => PluginExts.Contains(RelPath.Extension(f))).ToList();
        var iniText = IniTexts(c);
        var paired = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // a plugin's name may hold " - " itself ("Foo - Patch.esm" loads "Foo - Patch - Main.ba2"), so an archive belongs to
        // the plugin with the longest stem that its name starts with, followed by " - "
        string? OwnerIn(IEnumerable<string> pluginNames, string archiveStem) =>
            pluginNames.Where(p => archiveStem.StartsWith(RelPath.FileNameWithoutExtension(p) + " - ", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Length).FirstOrDefault();
        List<string>? gamePlugins = null;
        foreach (var rel in archives.Keys)
        {
            var dir = RelPath.DirectoryName(rel) ?? "";
            var fileName = RelPath.FileName(rel);
            var stem = RelPath.FileNameWithoutExtension(rel);
            var plugin = OwnerIn(plugins.Where(p => (RelPath.DirectoryName(p) ?? "") == dir), stem);
            if (plugin != null)
            {
                if (!paired.TryGetValue(plugin, out var list)) paired[plugin] = list = new();
                list.Add(fileName);
                continue;
            }
            // named after one of the game's own plugins: the Replaces vanilla section says what that means
            if (VanillaIndex.IsOfficialArchive(fileName)) continue;
            var listedBy = iniText.Where(t => t.Text.Contains(fileName, StringComparison.OrdinalIgnoreCase)).Select(t => t.Rel).FirstOrDefault();
            if (listedBy != null)
            {
                Note(rel, $"no plugin in this mod carries its name, but {listedBy} lists it: the game loads it only if that INI's archive list is used");
                continue;
            }
            var dash = stem.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0)
            {
                Look(rel, "the game loads an archive only when it is named \"<plugin> - <part>.ba2\" beside a plugin of that name, or when an INI lists it; " +
                          "this name has no \" - \" part, so the game won't load it as it is");
                continue;
            }
            var elsewhere = OwnerIn(plugins, stem);
            gamePlugins ??= c.HasGame
                ? Directory.EnumerateFiles(c.DataDir).Select(RelPath.FileName).OfType<string>().Where(n => PluginExts.Contains(RelPath.Extension(n).ToLowerInvariant())).ToList()
                : new();
            var inGame = OwnerIn(gamePlugins, stem);
            var ownerName = elsewhere != null ? RelPath.FileNameWithoutExtension(elsewhere) : inGame != null ? RelPath.FileNameWithoutExtension(inGame) : stem[..dash];
            Look(rel, $"no plugin named {ownerName}.esm (or .esp, .esl) sits beside it in this mod, so the game won't load it" +
                      (elsewhere != null ? $" from here ({elsewhere} is in another folder of the mod)"
                       : inGame != null ? $" unless that plugin is installed ({inGame} is in the game's Data folder: this archive may be meant for another mod)"
                       : " unless a plugin of that name is installed"));
        }
        foreach (var (plugin, list) in paired.OrderBy(p => p.Key, StringComparer.Ordinal))
            Note(plugin, $"comes with {list.Count} archive{(list.Count == 1 ? "" : "s")} the game loads with it: {string.Join(", ", list.Order(StringComparer.Ordinal))}");

        // archives inside the mod
        foreach (var rel in c.Files.Where(f => NestedExts.Contains(RelPath.Extension(f))))
            Note(rel, "an archive inside the mod: triage didn't open it, so what it holds isn't in this report");

        // layout
        var fomod = c.Files.FirstOrDefault(f => f.EndsWith("fomod\\moduleconfig.xml", StringComparison.Ordinal)) ?? c.Files.FirstOrDefault(f => f.StartsWith("fomod\\", StringComparison.Ordinal) || f.Contains("\\fomod\\"));
        if (fomod != null)
            Note(fomod, "the mod has a FOMOD installer: it installs some of these files depending on the choices made, so not every file listed here ends up in the game");
        var oddFolders = new Dictionary<string, int>(StringComparer.Ordinal);
        bool hasDataFolder = c.Files.Any(f => f.StartsWith("data\\", StringComparison.Ordinal) || f.Contains("\\data\\"));
        foreach (var rel in c.Files)
        {
            var gp = GamePath(c, rel);
            var ext = RelPath.Extension(rel);
            var slash = gp.IndexOf('\\');
            var top = slash < 0 ? null : gp[..slash];
            if (top == "fomod" || rel.StartsWith("fomod\\", StringComparison.Ordinal)) continue;
            if (BinaryExts.Contains(ext))
            {
                if (gp.StartsWith("sfse\\plugins\\", StringComparison.Ordinal) && ext == ".dll") continue;   // SFSE plugins: Data\SFSE\Plugins
                bool underData = c.DataPrefix != "" || rel.StartsWith("data\\", StringComparison.Ordinal) || rel.Contains("\\data\\") ||
                                 top != null && DataFolders.Contains(top);
                if (underData)
                    Note(rel, "a program file inside Data, outside SFSE\\Plugins: the game doesn't load program files from there " +
                              "(SFSE loads only Data\\SFSE\\Plugins\\*.dll)");
                else if (hasDataFolder)
                    Note(rel, "a program file outside the mod's Data folder: a mod manager may put it in the game's own folder (beside Starfield.exe), " +
                              "where the game or a loader such as SFSE starts it, or it is a separate program you run yourself");
                else if (top == null)
                    Note(rel, "a program file at the top of the mod: depending on the mod manager it lands in Data (where the game doesn't load it) " +
                              "or in the game's own folder (beside Starfield.exe, where the game or a loader such as SFSE starts it)");
                else
                    Note(rel, $"a program file in {top}\\, a folder the game doesn't read from Data");
                continue;
            }
            if (top == null)
            {
                if ((PluginExts.Contains(ext) || ext == ".ba2") && gp != c.DataPath(rel) && RelPath.DirectoryName(c.DataPath(rel)) is { Length: > 0 } wrapper)
                {
                    Note(rel, $"sits in the folder {wrapper}\\, not at the top of the mod: a mod manager installs it only if told to use that folder " +
                              "(often an installer option, or a choice between versions)");
                    continue;
                }
                if (PluginExts.Contains(ext) || ext == ".ba2" || NestedExts.Contains(ext) || DocExts.Contains(ext)) continue;
                if (ext == ".ini")
                    Note(rel, "an INI file at the top: the game doesn't read INI files from Data; it may be meant for the game's folder or " +
                              "Documents\\My Games\\Starfield (StarfieldCustom.ini), or for a tool");
                else
                    Note(rel, "sits at the top of Data, where the game looks only for plugins (.esm .esp .esl) and archives (.ba2)");
                continue;
            }
            if (!DataFolders.Contains(top)) oddFolders[top] = oddFolders.GetValueOrDefault(top) + 1;
        }
        foreach (var (folder, n) in oddFolders.OrderBy(p => p.Key, StringComparer.Ordinal))
            Note(folder + "\\", $"a folder the game doesn't read from Data ({n} file{(n == 1 ? "" : "s")}): it may hold documents, sources or " +
                                "installer options, or the mod may be packed one folder too deep");
    }

    /// <summary>The text of the mod's .ini files (small ones only), to see whether one lists an archive.</summary>
    static List<(string Rel, string Text)> IniTexts(TriageContext c)
    {
        var list = new List<(string, string)>();
        foreach (var rel in c.Files.Where(f => RelPath.Extension(f) == ".ini"))
        {
            try
            {
                var fi = new FileInfo(c.Full(rel));
                if (fi.Length <= 1 << 20) list.Add((rel, File.ReadAllText(fi.FullName)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    // ---------------------------------------------------------------- Replaces vanilla

    /// <summary>One file of the mod that the game also has: the mod's file (a loose path, or the archive it is in), the
    /// data-relative path, and the game archive that holds the game's copy.</summary>
    public sealed record Replaced(string ModFile, bool Loose, string Path, string GameArchive);

    static void ReplacesVanilla(TriageContext c, Dictionary<string, ArchiveListing> archives, List<TriageFinding> findings)
    {
        const string S = TriageSections.ReplacesVanilla;
        void Note(string file, string text) => findings.Add(new TriageFinding(TriageLevel.Note, S, file, text));
        void Look(string file, string text) => findings.Add(new TriageFinding(TriageLevel.Look, S, file, text));

        // plugins and archives with the game's own names (no index needed)
        foreach (var rel in c.Files)
        {
            var ext = RelPath.Extension(rel);
            var name = RelPath.FileName(rel);
            if (PluginExts.Contains(ext) && Checker.IsOfficial(name))
                Look(rel, $"has the same name as the game's own plugin {RelPath.FileName(rel)}: installed, it takes the place of the game's file");
            else if (ext == ".ba2" && VanillaIndex.IsOfficialArchive(name))
                Look(rel, c.HasGame && c.DataHas(name!)
                    ? "has the same name as one of the game's own archives: installed, it overwrites the game's file"
                    : $"is named after the game's own plugin {name[..name.IndexOf(" - ", StringComparison.Ordinal)]}.esm, so the game loads it with that plugin");
        }

        if (!c.HasGame)
        {
            Note("", $"no game found ({c.DataDir} has no Starfield.esm), so nothing was compared with the game's own files. " +
                     "Give --game \"<the game's folder>\" to compare.");
            return;
        }
        var index = VanillaIndex.Get(c.DataDir);
        foreach (var (a, why) in index.Unreadable)
            Note("", $"the game's archive {a} couldn't be read ({why}), so files that replace ones in it aren't found");
        if (index.Archives.Count == 0)
        {
            Note("", $"no readable official archives (Starfield - *.ba2 and the like) in {c.DataDir}, so nothing was compared with the game's own files");
            return;
        }

        var hits = new List<Replaced>();
        foreach (var rel in c.Files)
        {
            var ext = RelPath.Extension(rel);
            if (ext == ".ba2" || PluginExts.Contains(ext)) continue;
            var gp = GamePath(c, rel);
            if (index.ArchiveOf(gp) is { } ga) hits.Add(new Replaced(rel, true, gp, ga));
        }
        foreach (var (rel, a) in archives)
        {
            if (VanillaIndex.IsOfficialArchive(RelPath.FileName(rel))) continue;   // already a LOOK above; its contents are the game's own names
            foreach (var e in a.Entries)
                if (index.ArchiveOf(e.Name) is { } ga) hits.Add(new Replaced(rel, false, e.Name, ga));
        }
        Log.Write($"triage replaces vanilla: {hits.Count} of the mod's files are also in the game's archives");

        var table = c.Report.Table(S, "Game files this mod replaces", "Mod file", "Path in the game", "Game archive");
        foreach (var h in hits) table.Add(h.ModFile, h.Path, h.GameArchive);
        if (hits.Count == 0)
        {
            Note("", $"none of the mod's files or archive entries has the path of one of the game's own files " +
                     $"(compared with {index.Count:N0} paths in the game's {index.Archives.Count} archives)");
            return;
        }

        // scripts: a LOOK each (up to MaxScriptLooks)
        var scripts = hits.Where(h => h.Path.StartsWith("scripts\\", StringComparison.Ordinal) && h.Path.EndsWith(".pex", StringComparison.Ordinal)).ToList();
        foreach (var h in scripts.Take(MaxScriptLooks))
        {
            var script = ScriptName(h.Path);
            Look(h.ModFile, (h.Loose ? "" : $"holds {h.Path}, which ") +
                            $"replaces the game's own script {script} (in {h.GameArchive}): every mod and quest that uses it gets this copy");
        }
        if (scripts.Count > MaxScriptLooks)
            Look("", $"and {scripts.Count - MaxScriptLooks} more of the game's own scripts are replaced (every one is in the table): " +
                     "every mod and quest that uses them gets these copies");

        // other files: one NOTE per (mod file or folder, top folder, type)
        foreach (var g in hits.Except(scripts).GroupBy(h => (Source: h.Loose ? "" : h.ModFile, Top: TopFolder(h.Path), Ext: RelPath.Extension(h.Path)))
                     .OrderBy(g => g.Key.Source, StringComparer.Ordinal).ThenBy(g => g.Key.Top, StringComparer.Ordinal).ThenBy(g => g.Key.Ext, StringComparer.Ordinal))
        {
            var n = g.Count();
            var where = g.Key.Top == "" ? "at the top of Data" : $"under {g.Key.Top}\\";
            if (n == 1 && g.Key.Source == "")
            {
                var one = g.First();
                Note(one.ModFile, $"replaces the game's own {one.Path} (in {one.GameArchive}): the game uses this copy instead");
                continue;
            }
            var type = (g.Key.Ext == "" ? "file" + (n == 1 ? "" : "s") + " with no extension" : g.Key.Ext + (n == 1 ? " file" : " files"));
            var examples = string.Join(", ", g.Take(3).Select(h => h.Path)) + (n > 3 ? $" and {n - 3:N0} more" : "");
            var file = g.Key.Source != "" ? g.Key.Source : g.Key.Top == "" ? "" : g.Key.Top + "\\";
            Note(file, (g.Key.Source != "" ? "holds " : "") +
                       $"{n:N0} {type} {where} that {(n == 1 ? "replaces" : "replace")} the game's own ({examples}): " +
                       $"the game uses {(n == 1 ? "this copy" : "these copies")} instead" + (n > 1 ? " (all are in the table)" : ""));
        }
    }

    static string TopFolder(string p) { var i = p.IndexOf('\\'); return i < 0 ? "" : p[..i]; }

    /// <summary>"scripts\fragments\quests\qf_foo.pex" -> "fragments:quests:qf_foo".</summary>
    public static string ScriptName(string pexPath)
    {
        var p = pexPath.StartsWith("scripts\\", StringComparison.Ordinal) ? pexPath["scripts\\".Length..] : pexPath;
        if (p.EndsWith(".pex", StringComparison.Ordinal)) p = p[..^4];
        return p.Replace('\\', ':');
    }
}

/// <summary>The paths in the game's own archives (Starfield - *.ba2 and the DLC and Creations ones whose plugin
/// Checker.IsOfficial names), from their name tables only. Kept as sorted 64-bit hashes of each normalized path (the
/// first 8 bytes of its SHA-256) plus the archive that holds it, so 1.8 million paths take about 18 MB. Cached in
/// LearnedCache.Root\vanilla-index, keyed by each archive's name, size and modified time (and IndexVersion): any change,
/// such as a game update, means the index is built again.</summary>
public sealed class VanillaIndex
{
    /// <summary>Raise when the index's contents or file format change.</summary>
    public const int IndexVersion = 1;
    const string Magic = "SFMCVIDX";

    public static string CacheDir => Path.Combine(LearnedCache.Root, "vanilla-index");

    public List<string> Archives { get; } = new();
    public List<(string Archive, string Why)> Unreadable { get; } = new();
    ulong[] hashes = [];
    ushort[] owners = [];
    public int Count => hashes.Length;
    /// <summary>The cache file (null when there was nothing to cache), whether this run built it, and how long the
    /// build or the read took.</summary>
    public string? CachePath { get; private set; }
    public bool Built { get; private set; }
    public TimeSpan Took { get; private set; }

    static readonly Dictionary<string, VanillaIndex> loaded = new(StringComparer.Ordinal);

    /// <summary>Tests: forget the indexes held in memory, so the next Get reads the cache or builds again.</summary>
    public static void ForgetLoaded() => loaded.Clear();

    /// <summary>"Starfield - Textures01.ba2", "ShatteredSpace - Main01.ba2", "SFBGS003 - Main.ba2" and the like.</summary>
    public static bool IsOfficialArchive(string fileName)
    {
        if (!fileName.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) return false;
        var dash = fileName.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 && Checker.IsOfficial(fileName[..dash] + ".esm");
    }

    /// <summary>The official archives in a Data folder, in a fixed order (Starfield's first, then by name).</summary>
    public static List<string> OfficialArchives(string dataDir) =>
        Directory.Exists(dataDir)
            ? Directory.EnumerateFiles(dataDir, "*.ba2", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 }).Select(RelPath.FileName).OfType<string>().Where(IsOfficialArchive)
                .OrderBy(n => n.StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new();

    public static ulong Hash(string normalized)
    {
        var max = Encoding.UTF8.GetMaxByteCount(normalized.Length);
        Span<byte> bytes = max <= 1024 ? stackalloc byte[max] : new byte[max];
        var n = Encoding.UTF8.GetBytes(normalized, bytes);
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(bytes[..n], h);
        return BinaryPrimitives.ReadUInt64LittleEndian(h);
    }

    /// <summary>The game archive that holds this data-relative path, or null.</summary>
    public string? ArchiveOf(string path)
    {
        var i = Array.BinarySearch(hashes, Hash(Ba2Names.Normalize(path)));
        return i >= 0 ? Archives[owners[i]] : null;
    }

    /// <summary>The index for a Data folder: from memory, the cache, or built now (and cached).</summary>
    public static VanillaIndex Get(string dataDir)
    {
        var sw = Stopwatch.StartNew();
        var full = Path.GetFullPath(dataDir);
        var names = OfficialArchives(full);
        var keyLines = new List<string> { $"# key: vanilla index {IndexVersion}" };
        foreach (var n in names)
        {
            var fi = new FileInfo(Path.Combine(full, n));
            keyLines.Add($"# archive: {fi.Name} {fi.Length} {fi.LastWriteTimeUtc.Ticks}");
        }
        var key = string.Join("\n", keyLines).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        var memo = full.ToLowerInvariant() + "|" + hash;
        if (loaded.TryGetValue(memo, out var have)) return have;
        if (names.Count == 0)
        {
            Log.Write($"vanilla index: no official archives in {full}");
            return loaded[memo] = new VanillaIndex { Took = sw.Elapsed };
        }
        var file = Path.Combine(CacheDir, $"vanilla-index.{hash}.bin");
        if (File.Exists(file))
        {
            try
            {
                var idx = ReadCache(file, key);
                if (idx != null)
                {
                    idx.CachePath = file; idx.Took = sw.Elapsed;
                    Log.Write($"vanilla index: read {file} ({idx.Count:N0} paths from {idx.Archives.Count} archives) in {sw.Elapsed.TotalSeconds:F2} s");
                    return loaded[memo] = idx;
                }
                Log.Write($"vanilla index: {file} is for other archives (a game update?), so it is built again");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException or FormatException or OverflowException)
            {
                Log.Write($"vanilla index: {file} couldn't be read ({e.Message}), so it is built again");
            }
        }
        else Log.Write($"vanilla index: no cache for these archives ({file}), so it is built now");

        Console.Error.WriteLine($"sf-mod-check: indexing the file names in the game's {names.Count} archives in {full} (once; cached in {CacheDir})");
        var built = Build(full, names);
        built.Built = true;
        try
        {
            Directory.CreateDirectory(CacheDir);
            foreach (var old in Directory.GetFiles(CacheDir, "vanilla-index.*.bin")) File.Delete(old);   // other game versions
            var tmp = file + ".tmp";
            WriteCache(tmp, key, built);
            File.Move(tmp, file, overwrite: true);
            built.CachePath = file;
            Log.Write($"vanilla index: wrote {file} ({new FileInfo(file).Length:N0} bytes)");
        }
        // a cache we can't write only costs time next run
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"vanilla index: built but couldn't write {file}: {e.Message}");
        }
        built.Took = sw.Elapsed;
        Log.Write($"vanilla index: built {built.Count:N0} paths from {built.Archives.Count} archives ({built.Unreadable.Count} unreadable) in {sw.Elapsed.TotalSeconds:F2} s");
        return loaded[memo] = built;
    }

    static VanillaIndex Build(string dataDir, List<string> names)
    {
        var idx = new VanillaIndex();
        var pairs = new List<(ulong Hash, ushort Owner)>();
        foreach (var n in names)
        {
            List<string> entries;
            try { entries = Ba2Names.Read(Path.Combine(dataDir, n)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                idx.Unreadable.Add((n, e.Message));
                Log.Write($"vanilla index: {n} couldn't be read: {e.Message}");
                continue;
            }
            if (idx.Archives.Count >= ushort.MaxValue) { idx.Unreadable.Add((n, "too many archives")); continue; }
            var owner = (ushort)idx.Archives.Count;
            idx.Archives.Add(n);
            foreach (var e in entries) pairs.Add((Hash(e), owner));
        }
        // sorted by hash; the first archive that holds a path keeps it
        pairs.Sort((a, b) => a.Hash != b.Hash ? a.Hash.CompareTo(b.Hash) : a.Owner.CompareTo(b.Owner));
        var hs = new List<ulong>(pairs.Count);
        var os = new List<ushort>(pairs.Count);
        foreach (var (h, o) in pairs)
        {
            if (hs.Count > 0 && hs[^1] == h) continue;
            hs.Add(h); os.Add(o);
        }
        idx.hashes = hs.ToArray();
        idx.owners = os.ToArray();
        return idx;
    }

    static void WriteCache(string path, string key, VanillaIndex idx)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(f, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(key);
        w.Write(idx.Archives.Count);
        foreach (var a in idx.Archives) w.Write(a);
        w.Write(idx.Unreadable.Count);
        foreach (var (a, why) in idx.Unreadable) { w.Write(a); w.Write(why); }
        w.Write(idx.hashes.Length);
        foreach (var h in idx.hashes) w.Write(h);
        foreach (var o in idx.owners) w.Write(o);
    }

    /// <summary>The cached index, or null when the cache's key differs. Throws on a damaged file.</summary>
    static VanillaIndex? ReadCache(string path, string key)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(f, Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(Magic.Length)) != Magic) throw new InvalidDataException("not a vanilla index file");
        if (r.ReadString() != key) return null;
        var idx = new VanillaIndex();
        int na = r.ReadInt32();
        if (na < 0 || na > ushort.MaxValue) throw new InvalidDataException("bad archive count");
        for (int i = 0; i < na; i++) idx.Archives.Add(r.ReadString());
        int nu = r.ReadInt32();
        if (nu < 0 || nu > ushort.MaxValue) throw new InvalidDataException("bad unreadable count");
        for (int i = 0; i < nu; i++) idx.Unreadable.Add((r.ReadString(), r.ReadString()));
        int n = r.ReadInt32();
        if (n < 0 || (long)n * 10 > f.Length) throw new InvalidDataException("bad path count");
        if (!BitConverter.IsLittleEndian) throw new InvalidDataException("big-endian systems rebuild the index");
        var hs = new ulong[n];
        var os = new ushort[n];
        var bytes = r.ReadBytes(n * 8);
        if (bytes.Length != n * 8) throw new EndOfStreamException("the hashes end early");
        Buffer.BlockCopy(bytes, 0, hs, 0, bytes.Length);
        bytes = r.ReadBytes(n * 2);
        if (bytes.Length != n * 2) throw new EndOfStreamException("the archive numbers end early");
        Buffer.BlockCopy(bytes, 0, os, 0, bytes.Length);
        for (int i = 0; i < n; i++)
        {
            if (os[i] >= na) throw new InvalidDataException("an archive number is out of range");
            if (i > 0 && hs[i] <= hs[i - 1]) throw new InvalidDataException("the hashes are out of order");
        }
        idx.hashes = hs; idx.owners = os;
        return idx;
    }
}
