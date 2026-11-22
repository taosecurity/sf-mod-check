// Moved from StarfieldConflictCheck (LoadOrderSetup.cs, commit d336108) into sfmodcheck on 2026-11-16.
// Builds the load order: official files first, then Plugins.txt order, with every plugin after its masters, then the
// official BlueprintShips-*.esm files, oldest first, as the game loads them (0.12.0).
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
    // BlueprintShips-*.esm files aren't here: the game loads them after every plugin (0.12.0; see BlueprintShipsLast).
    static readonly string[] OfficialOrder =
    [
        "Starfield.esm", "Constellation.esm", "OldMars.esm",
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
    // \z, not $, so a name with a trailing newline isn't Bethesda's (0.10.1 code review).
    static readonly System.Text.RegularExpressions.Regex OfficialPattern = new(
        @"^(BlueprintShips-)?(SFBGS[0-9A-F]{3}|sfta[0-9]{2})\.esm\z|^BlueprintShips-Starfield\.esm\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsOfficial(string name) =>
        OfficialOrder.Contains(name, StringComparer.OrdinalIgnoreCase) || OfficialPattern.IsMatch(name);

    /// <summary>An official ship-blueprint file (BlueprintShips-Starfield.esm, BlueprintShips-SFBGS050.esm and the
    /// like). The game loads these after every plugin, oldest file first (0.12.0).</summary>
    public static bool IsBlueprint(string name) =>
        IsOfficial(name) && name.StartsWith("BlueprintShips-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The order the game gives the official BlueprintShips files it loads: after every other plugin, oldest
    /// file first by modified time, then by name (LOOT's and libloadorder's rule; sf-load-order uses the same one).
    /// A file whose time can't be read goes after the dated ones, by name.</summary>
    /// <param name="times">if given, gets each file's modified time as used for the order (null: unknown)</param>
    public static List<string> BlueprintShipsLast(IEnumerable<string> names, Func<string, string> pathOf, IDictionary<string, DateTime?>? times = null)
    {
        static (int, DateTime) Age(string path)
        {
            try { return File.Exists(path) ? (0, File.GetLastWriteTimeUtc(path)) : (1, DateTime.MinValue); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (1, DateTime.MinValue); }
        }
        var aged = names.Select(n => (Name: n, Age: Age(pathOf(n)))).ToList();
        if (times is not null) foreach (var x in aged) times[x.Name] = x.Age.Item1 == 0 ? x.Age.Item2 : null;
        return aged
            .OrderBy(x => x.Age.Item1).ThenBy(x => x.Age.Item2).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name).ToList();
    }

    /// <summary>The words in the load-order problem for a mod with a BlueprintShips master. That mod is still loaded
    /// and compared, so `triage` doesn't call it left out (0.12.0 code review).</summary>
    public const string JudgedInGameOrder = "its records were judged in the game's order";

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
        // A listed BlueprintShips line doesn't move the file: the game places those files itself (below).
        foreach (var n in listed.Where(n => IsOfficial(n) && !IsBlueprint(n))) Add(n);
        // Other Bethesda files in Data load too, unless Plugins.txt lists them turned off (a Creation switched off in
        // the game's Creations menu keeps its file in Data; 0.5.0).
        foreach (var n in onDisk.Keys.Where(f => IsOfficial(f) && !IsBlueprint(f) && f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                                               && !turnedOff.Contains(f))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) Add(n);
        foreach (var n in listed.Where(n => !IsOfficial(n))) Add(n);
        // The official BlueprintShips files load after every plugin, oldest file first (0.12.0; C13: 26 DeepScan runs,
        // LOOT, libloadorder). A ship-blueprint file goes with its plugin: left out when Plugins.txt turns that off.
        var blueprintFiles = new List<string>();
        foreach (var f in onDisk.Keys.Where(f => IsBlueprint(f) && f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)))
        {
            var owner = f["BlueprintShips-".Length..];
            if (turnedOff.Contains(owner))
                Log.Write($"load order: {f} left out: its plugin {owner} is turned off in Plugins.txt");
            else blueprintFiles.Add(f);
        }
        var blueprintTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        var blueprintLast = BlueprintShipsLast(blueprintFiles, f => fullPath[f], blueprintTimes);
        foreach (var n in blueprintLast) Add(n);
        for (int i = 0; i < blueprintLast.Count; i++)
        {
            // the time the order was made from (read once, with BlueprintShipsLast's guard; 0.12.0 code review)
            var t = blueprintTimes.GetValueOrDefault(blueprintLast[i]);
            var when = t is { } d ? d.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "time unknown";
            Log.Write($"load order: {blueprintLast[i]} goes after every plugin (BlueprintShips file {i + 1} of {blueprintLast.Count}, " +
                      $"modified {when}): the game loads BlueprintShips files last, oldest file first");
        }
        var blueprintSet = new HashSet<string>(blueprintLast, StringComparer.OrdinalIgnoreCase);

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
            // Problems about a plugin that still loads are added once it has opened, so a plugin skipped for a later
            // master never gets both "judged" and "skipped" (0.12.0 code review round 2).
            var ifLoaded = new List<string>();
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
                if (blueprintSet.Contains(m) && !blueprintSet.Contains(name))
                {
                    // Not pulled forward: its records are judged in the game's order, with the BlueprintShips file last.
                    ifLoaded.Add($"{name}: has the master {m}, but the game loads BlueprintShips files after every mod, so this plugin loads before its master; {JudgedInGameOrder}");
                    Log.Write($"load order: {name} has the BlueprintShips master {m}; {m} stays after every plugin");
                }
                else if (blueprintSet.Contains(m))
                {
                    // Two BlueprintShips files: they keep their own order (oldest first, then by name, a file whose time
                    // can't be read last), even when the master comes later in it (0.12.0 code review).
                    if (blueprintLast.FindIndex(b => b.Equals(m, StringComparison.OrdinalIgnoreCase)) > blueprintLast.FindIndex(b => b.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        ifLoaded.Add($"{name}: has the master {m}, a BlueprintShips file the game loads after it (BlueprintShips files load oldest first, then by name), so this file loads before its master; {JudgedInGameOrder}");
                        Log.Write($"load order: BlueprintShips file {name} has the BlueprintShips master {m}, which loads after it; both keep the BlueprintShips order");
                    }
                }
                else if (wanted.FindIndex(w => w.Equals(m, StringComparison.OrdinalIgnoreCase)) > wanted.FindIndex(w => w.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    ifLoaded.Add($"{name}: listed before its master {m}; the game would fail to load it, so it was analyzed after {m}");
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
            foreach (var pr in ifLoaded) setup.AddProblem(pr);
            return true;
        }
        foreach (var n in wanted) Open(n, new Stack<string>());
        // A mod with a BlueprintShips master opened that file early (Mutagen needs its masters to read it). Put the
        // BlueprintShips files back at the end, in their order, and number the plugins again.
        var ordered = setup.Plugins.Where(p => !blueprintSet.Contains(p.FileName))
            .Concat(blueprintLast.Select(b => setup.Plugins.FirstOrDefault(p => p.FileName.Equals(b, StringComparison.OrdinalIgnoreCase))).OfType<PluginInfo>())
            .Select((p, i) => p.Index == i ? p : p with { Index = i }).ToList();
        setup.Plugins.Clear();
        setup.Plugins.AddRange(ordered);
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
