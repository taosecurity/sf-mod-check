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
// House rules (RuleKind.House, a WARNING in the basic profile):
//   master            a BlueprintShips* master in the plugin's header
//   one-plugin        more than one plugin in a zip (Vortex and other managers install one zip as one mod)
//   script-source     .psc files in the zip (players need only the compiled .pex)
//   root-md           a .md at the zip's root without a hyphenated mod name (README.md lands in Data and overwrites
//                     another mod's copy)
//   private-file      permission records and screenshots, private comparison notes
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

            // ---- house: one plugin per zip ----
            if (p.Plugins.Count > 1)
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
            foreach (var f in p.Files.Where(GenericRootMd))
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
}
