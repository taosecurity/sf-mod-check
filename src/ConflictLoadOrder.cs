// Moved from StarfieldConflictCheck (LoadOrderSetup.cs, commit d336108) into sfmodcheck on 2026-11-16.
// Builds the load order: official files first, then Plugins.txt order, with every plugin after its masters.
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck.Conflict;


// Path: the file read (raw record headers are read from it for record types, 0.5.2).
public record PluginInfo(int Index, string FileName, bool Official, List<string> Masters, IStarfieldModDisposableGetter Mod)
{
    public string Path { get; init; } = "";
}

public class LoadOrderSetup
{
    // Bethesda's own files. Starfield.esm always loads first; the rest follow in this order unless Plugins.txt lists them.
    static readonly string[] OfficialOrder =
    [
        "Starfield.esm", "Constellation.esm", "OldMars.esm", "BlueprintShips-Starfield.esm",
        "SFBGS007.esm", "SFBGS008.esm", "SFBGS006.esm", "SFBGS003.esm", "SFBGS004.esm", "ShatteredSpace.esm",
    ];

    // What Bethesda's files are. Source: Arthmoor, "Starfield ESM modules - what are they?", AFK Mods
    // (https://www.afkmods.com/index.php?/topic/10297-starfield-esm-modules-what-are-they/, read 2026-10-28).
    static readonly Dictionary<string, string> OfficialNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Starfield.esm"] = "base game",
        ["Constellation.esm"] = "Premium Edition skins",
        ["OldMars.esm"] = "preorder bonus skins",
        ["BlueprintShips-Starfield.esm"] = "base game ship blueprints",
        ["ShatteredSpace.esm"] = "Shattered Space DLC",
        ["SFBGS003.esm"] = "Trackers Alliance update",
        ["SFBGS004.esm"] = "REV-8 vehicle",
        ["SFBGS006.esm"] = "empty ship habs",
        ["SFBGS007.esm"] = "gameplay options",
        ["SFBGS008.esm"] = "planet surface maps and city map markers",
        ["SFBGS00D.esm"] = "Free Lanes update",
        ["SFBGS047.esm"] = "Moon Jumper vehicle",
        ["SFBGS050.esm"] = "Terran Armada DLC",
        ["BlueprintShips-SFBGS050.esm"] = "Terran Armada DLC ship blueprints",
        ["sfta01.esm"] = "Trackers Alliance Bounties Creation", ["sfta02.esm"] = "Trackers Alliance Bounties Creation",
        ["sfta03.esm"] = "Trackers Alliance Bounties Creation", ["sfta04.esm"] = "Trackers Alliance Bounties Creation",
        ["sfta05.esm"] = "Trackers Alliance Bounties Creation", ["sfta06.esm"] = "Trackers Alliance Bounties Creation",
        ["BlueprintShips-sfta03.esm"] = "Trackers Alliance Bounties ship blueprints",
        ["BlueprintShips-sfta06.esm"] = "Trackers Alliance Bounties ship blueprints",
        ["sfbgs00b.esm"] = "Escape Creation", ["sfbgs00c.esm"] = "The Perfect Recipe Creation",
        ["sfbgs019.esm"] = "Deimog rover Creation", ["sfbgs021.esm"] = "Observatory Creation",
        ["sfbgs023.esm"] = "Starborn Gravis Suit Creation", ["sfbgs024.esm"] = "Civilian Survival Suit Creation",
        ["sfbgs01c.esm"] = "Water-Cooled Miners' Outfit Creation", ["sfbgs036.esm"] = "Beowulf weapon skin Creation",
    };

    // "SFBGS050.esm (Terran Armada DLC)" for Bethesda files, the plain file name otherwise.
    public static string Display(string name) => OfficialNames.TryGetValue(name, out var n) ? $"{name} ({n})" : name;

    public static string? OfficialName(string name) => OfficialNames.GetValueOrDefault(name);

    // Bethesda's file names exactly (0.5.0): SFBGS + 3 hex digits, sfta + 2 digits, and their BlueprintShips- files.
    // A prefix test also caught mods such as SFBGS003Fixes.esm and hid their conflicts as "Bethesda vs Bethesda".
    static readonly System.Text.RegularExpressions.Regex OfficialPattern = new(
        @"^(BlueprintShips-)?(SFBGS[0-9A-F]{3}|sfta[0-9]{2})\.esm$|^BlueprintShips-Starfield\.esm$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsOfficial(string name) =>
        OfficialOrder.Contains(name, StringComparer.OrdinalIgnoreCase) || OfficialPattern.IsMatch(name);

    public List<PluginInfo> Plugins { get; } = new();
    // Read-only outside: AddProblem is the only writer, so every problem is cleaned to one line (0.5.1).
    readonly List<string> problems = new();
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Adds a load-order problem on one line: it may quote plugin and master names from file headers (0.5.0).</summary>
    public void AddProblem(string text) => problems.Add(Impact.OneLine(text));
    public string OrderSource { get; private set; } = "";

    public static LoadOrderSetup Build(string dataDir, string? pluginsTxt, string? extraDir = null)
    {
        var setup = new LoadOrderSetup();
        List<string> listed;
        var turnedOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pluginsTxt is not null)
        {
            // Active lines start with '*'; '#' lines are comments; any other line is a plugin turned off.
            var lines = File.ReadAllLines(pluginsTxt).Select(l => l.Trim()).ToList();
            listed = lines.Where(l => l.StartsWith('*')).Select(l => l[1..]).ToList();
            foreach (var l in lines.Where(l => l.Length > 0 && !l.StartsWith('*') && !l.StartsWith('#'))) turnedOff.Add(l);
            setup.OrderSource = $"Plugins.txt ({pluginsTxt}), {listed.Count} active entries";
        }
        else
        {
            // No Plugins.txt: every plugin in Data, officials first, then alphabetical. Results depend on this guess.
            listed = Directory.EnumerateFiles(dataDir)
                .Select(Path.GetFileName)
                .Where(f => f!.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                .Select(f => f!)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            setup.OrderSource = "ASSUMED: no Plugins.txt given, so all plugins in Data in alphabetical order (officials first)";
        }

        // Vortex writes names in lower case; use the real file names from Data.
        var onDisk = Directory.EnumerateFiles(dataDir).Select(Path.GetFileName)
            .ToDictionary(f => f!, f => f!, StringComparer.OrdinalIgnoreCase);
        // Optional second folder (for example, patches not yet installed); its files win over Data's.
        var fullPath = onDisk.Keys.ToDictionary(f => f, f => Path.Combine(dataDir, f), StringComparer.OrdinalIgnoreCase);
        if (extraDir is not null)
            foreach (var f in Directory.EnumerateFiles(extraDir))
            {
                var name = Path.GetFileName(f);
                onDisk[name] = name;
                fullPath[name] = f;
            }
        var wanted = new List<string>();
        void Add(string name)
        {
            if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            if (!onDisk.TryGetValue(name, out var real)) { setup.AddProblem($"{name}: listed but not found in Data"); return; }
            wanted.Add(real);
        }
        // The game loads Bethesda's files whether or not Plugins.txt lists them (SFBGS00D etc. usually are not listed).
        foreach (var o in OfficialOrder) if (onDisk.ContainsKey(o)) Add(o);
        foreach (var n in listed.Where(IsOfficial)) Add(n);
        // Other Bethesda files in Data load too, unless Plugins.txt lists them turned off (a Creation switched off in
        // the game's Creations menu keeps its file in Data; a ship-blueprint file goes with its Creation; 0.5.0).
        foreach (var n in onDisk.Keys.Where(f => IsOfficial(f) && f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                                               && !turnedOff.Contains(f.StartsWith("BlueprintShips-", StringComparison.OrdinalIgnoreCase) ? f["BlueprintShips-".Length..] : f))
                     .OrderBy(f => f.StartsWith("BlueprintShips-", StringComparison.OrdinalIgnoreCase))  // ship blueprints need their SFBGS master first
                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)) Add(n);
        foreach (var n in listed.Where(n => !IsOfficial(n))) Add(n);

        // Open in order, pulling any master forward if it is listed later; plugins with missing masters are skipped.
        var opened = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Open(string name, Stack<string> chain)
        {
            if (opened.ContainsKey(name)) return true;
            if (skipped.Contains(name) || chain.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            var path = fullPath[name];
            List<string> masters;
            try { masters = ModHeaderMasters(path); }
            catch (Exception e) when (Fatal.Recoverable(e))   // unreadable, or a damaged header (sizes past the end): skip it, not the run (0.5.0)
            {
                setup.AddProblem($"{name}: cannot be read ({e.GetType().Name}), plugin skipped; run sf-mod-check check on it for details");
                skipped.Add(name);
                return false;
            }
            chain.Push(name);
            foreach (var m in masters)
            {
                var inOrder = wanted.Contains(m, StringComparer.OrdinalIgnoreCase);
                if (!inOrder || !Open(m, chain))
                {
                    setup.AddProblem($"{name}: master {m} is {(inOrder ? "unloadable" : "missing or inactive")}, plugin skipped");
                    skipped.Add(name);
                    chain.Pop();
                    return false;
                }
                if (wanted.FindIndex(w => w.Equals(m, StringComparison.OrdinalIgnoreCase)) > wanted.FindIndex(w => w.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    setup.AddProblem($"{name}: listed before its master {m}; the game would fail to load it, so it was analyzed after {m}");
            }
            chain.Pop();
            IStarfieldModDisposableGetter mod;
            try
            {
                mod = StarfieldMod.CreateFromBinaryOverlay(path, StarfieldRelease.Starfield,
                    new BinaryReadParameters { MasterFlagsLookup = new LoadOrder<IModMasterStyledGetter>(opened.Values.Select(p => (IModMasterStyledGetter)p.Mod)) });
            }
            catch (Exception e) when (Fatal.Recoverable(e))   // a damaged plugin is skipped, not the whole run (0.5.0)
            {
                setup.AddProblem($"{name}: cannot be opened ({e.GetType().Name}: {e.Message.Split('\n')[0]}), plugin skipped; run sf-mod-check check on it for details");
                skipped.Add(name);
                return false;
            }
            var info = new PluginInfo(opened.Count, name, IsOfficial(name), masters, mod) { Path = path };
            opened[name] = info;
            setup.Plugins.Add(info);
            return true;
        }
        foreach (var n in wanted) Open(n, new Stack<string>());
        return setup;
    }

    // Reads MAST subrecords straight from the TES4 header (same approach as itm-check).
    public static List<string> ModHeaderMasters(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.ReadBytes(4);
        var size = reader.ReadUInt32();
        reader.ReadBytes(16);
        var data = reader.ReadBytes((int)size);
        var masters = new List<string>();
        for (int i = 0; i + 6 <= data.Length; i += 6 + BitConverter.ToUInt16(data, i + 4))
        {
            if (Encoding.ASCII.GetString(data, i, 4) == "MAST")
                masters.Add(Encoding.ASCII.GetString(data, i + 6, BitConverter.ToUInt16(data, i + 4)).TrimEnd('\0'));
        }
        return masters;
    }
}
