// sf-mod-check patch (0.9.0): builds a local conflict patch for a load order, with no AI and no xEdit.
// It runs the same analysis as `conflict`, then writes one light plugin that loads last and, for every record with a
// lost change:
//   - starts from the record as the game uses it today (each field from the last plugin that supplies it, so partial
//     forms are filled in), written as a full record;
//   - copies every Forwardable field forward from the plugin that lost it (the winner didn't change that field, so
//     nothing the winner meant is undone);
//   - for Choice fields (both plugins changed it) keeps the winner's value unless --pick names another plugin.
// Since 0.13.0 a field --pick works on any field: it keeps the named plugin's value where a change would be forwarded,
// and on a record with no lost change it adds the record as the game uses it today with that one field picked.
// Cells, worldspaces, placed objects and navmeshes are built since 0.13.0 (each record itself only; a placed object's or
// navmesh's cell and an exterior cell's worldspace come along as the game uses them today). The records inside quests or
// dialogue topics are not built in this version: the report lists them for xEdit. Nor is the navmesh info map (NAVI), whose copies
// each list only one plugin's navmeshes. Records that end up equal to today's winner are
// left out (no ITMs). It reads plugins and writes only its output folder: never the Data folder, never Plugins.txt.
//   sf-mod-check patch [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]
//                      [--name <Patch.esm>] [--only <a.esm>[,<b.esm>...]]... [--pick <FormKey>[:<Field>]=<plugin>]...
//                      [--include-official] [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]
// --include-official (0.10.0, off by default) also restores changes one official plugin loses to another.
// Exit 0 = patch written (or nothing to patch), 1 = written, but reading it back found a difference (don't use it),
// 2 = could not run, 3 = stopped at --max-memory. Every run ends with one RESULT line.
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Impact = SfModCheck.Conflict.Impact;

namespace SfModCheck;

public static class PatchCommand
{
    public const string Usage =
        "usage: sf-mod-check patch [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]\n" +
        "                          [--name <Patch.esm>] [--only <a.esm>[,<b.esm>...]]... [--pick <FormKey>[:<Field>]=<plugin>]...\n" +
        "                          [--include-official] [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]";

    public const string DefaultName = "ConflictPatch.esm";
    const int PartialFlag = FieldCompare.PartialFormFlag;
    const int CompressedFlag = 0x40000;

    static string GamePluginsTxt() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starfield", "Plugins.txt");

    /// <summary>A --pick: use this plugin's value for one field of a record, or for all its Choice fields (Field null).</summary>
    public record Pick(FormKey FormKey, string? Field, string Plugin)
    {
        public bool Used { get; set; }
        /// <summary>Why a pick that was looked at gave way after all (a later --pick on the same field decides).</summary>
        public string? Overruled { get; set; }
        /// <summary>Why a pick could not be used (its "was not used" warning says this instead of the general text).</summary>
        public string? Why { get; set; }
    }

    /// <summary>The "was not used" warning for a pick nothing took.</summary>
    internal static string NotUsedWarning(Pick pk) =>
        $"WARNING: --pick {pk.FormKey}{(pk.Field is null ? "" : ":" + pk.Field)}={pk.Plugin} was not used: " +
        (pk.Why ?? (pk.Field is null
            ? $"that record has no Choice field that {pk.Plugin} supplies, or the record isn't patched"
            : $"that record has no field {pk.Field} that {pk.Plugin} supplies, or the record isn't patched"));

    /// <summary>Field --pick options on the mesh of one navmesh that name two plugins. A navmesh's mesh comes whole from
    /// one plugin, so the run stops with this message rather than choosing one of them (code review 0.13.0, round 7:
    /// the simple rule that replaced the per-part pick ranking of rounds 3 to 6).</summary>
    public sealed class MeshPickConflict(string message) : Exception(message);

    /// <summary>Parses "064F3C:Starfield.esm=ModA.esm" (whole record) or "064F3C:Starfield.esm:Value=ModA.esm" (one
    /// field). Null when it can't be read.</summary>
    public static Pick? ParsePick(string text)
    {
        var eq = text.LastIndexOf('=');
        if (eq <= 0 || eq == text.Length - 1) return null;
        var left = text[..eq].Trim();
        var plugin = text[(eq + 1)..].Trim();
        if (!GameData.IsPlainPluginName(plugin)) return null;
        if (FormKey.TryFactory(left, out var whole)) return new(whole, null, plugin);
        var colon = left.LastIndexOf(':');
        if (colon <= 0) return null;
        var field = left[(colon + 1)..].Trim();
        if (field.Length == 0 || !field.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;
        return FormKey.TryFactory(left[..colon], out var fk) ? new(fk, field, plugin) : null;
    }

    // Record kinds that live inside another record's group and aren't built yet: dialogue and scenes (inside a quest or
    // topic). Cells and worldspaces are built since 0.13.0 (only the record itself, never its placed objects, navmeshes
    // or blocks; see CellOnly and WorldOnly below), and so are placed objects and navmeshes: each one goes into its
    // cell's Persistent, Temporary or NavigationMeshes list, with the cell as the game uses it today when the cell isn't
    // a patch record of its own. A navmesh's mesh (geometry, NNAM, pre-cut entries) is one value: see NavmeshUnit.
    static readonly Type[] NotBuilt =
    [
        typeof(IDialogTopicGetter), typeof(IDialogResponsesGetter), typeof(ISceneGetter),
        typeof(IDialogBranchGetter),
    ];

    // 0.13.0: a cell goes into the patch without its placed objects and navmeshes (records of their own), and a
    // worldspace without its cells, its persistent cell, its large-reference table (RNAM) and its cell offsets (OFST,
    // CLSZ). RNAM, OFST and CLSZ index the objects and cells of the plugin that holds them; a patch has none of those,
    // and Creation Kit overrides carry none either. A winning worldspace with a few RNAM entries made every landing at
    // one city crash in the game's cell loader (2026-11-07, the in-game-proven cells builder's notes), so they're
    // always left out. The compare already ignores all three (FieldCompare's Ignored list).
    static readonly Cell.TranslationMask CellOnly = new(true) { Persistent = false, Temporary = false, NavigationMeshes = false };
    static readonly Worldspace.TranslationMask WorldOnly = new(true)
        { SubCells = false, TopCell = false, LargeReferences = false, OffsetData = false, CellSizeData = false };

    /// <summary>A copy of one version of a record, holding only the record itself (0.13.0: a cell without its child
    /// records, a worldspace without its cells and RNAM/OFST/CLSZ).</summary>
    public static IMajorRecord CopyOf(IMajorRecordGetter r) => r switch
    {
        ICellGetter c => c.DeepCopy(CellOnly),
        IWorldspaceGetter w => w.DeepCopy(WorldOnly),
        _ => (IMajorRecord)r.DeepCopy(),
    };

    /// <summary>Where a cell sits in the plugin that holds it: an interior cell block and sub-block, or a worldspace's
    /// persistent cell, or an exterior block and sub-block of a worldspace (0.13.0).</summary>
    public sealed record CellPlace(FormKey? World, bool Persistent, int Block, int SubBlock, short X, short Y, short SubX, short SubY)
    {
        public override string ToString() => World is null ? $"interior block {Block}, sub-block {SubBlock}"
            : Persistent ? $"the persistent cell of worldspace {World}" : $"worldspace {World}, block {X},{Y}, sub-block {SubX},{SubY}";
    }

    /// <summary>Where a placed object or navmesh sits in the plugin that holds it: its cell, and that cell's persistent
    /// or temporary list, or its navmeshes (0.13.0).</summary>
    public sealed record RefPlace(FormKey Cell, bool Persistent, bool Navmesh = false)
    {
        public override string ToString() => $"cell {Cell}, {(Navmesh ? "navmeshes" : Persistent ? "persistent references" : "temporary references")}";
    }

    /// <summary>A record that is written inside a cell: a placed object or a navmesh (0.13.0).</summary>
    public static bool IsCellChild(IMajorRecordGetter r) => r is IPlacedGetter or INavigationMeshGetter;

    /// <summary>A navmesh's mesh: its geometry (NVNM), its NNAM data and its pre-cut map entries. They describe one mesh
    /// as the Creation Kit finalized it, so the patch takes all three from one version, never field by field or entry
    /// by entry (0.13.0).</summary>
    public static readonly string[] NavmeshUnit = ["NavmeshGeometry", "NNAM", "PreCutMapEntries"];

    /// <summary>A record in a log line: its type, its EditorID when it has one (one line, escaped), and its FormKey.</summary>
    static string LogName(string recordType, string? editorId, FormKey fk) =>
        string.IsNullOrEmpty(editorId) ? $"{recordType} [{fk}]" : $"{recordType} {SafeText.OneLine(editorId)} [{fk}]";

    /// <summary>Why a record type can't be built, or null when it can.</summary>
    public static string? WhyNotBuilt(Type getter)
    {
        // The navmesh info map (NAVI): each plugin's copy lists only the navmeshes that plugin adds or changes (on
        // a large load order, 2026-11-20: Starfield.esm 56,631 entries, sfta03.esm 515, a mod 16), so it is not a record
        // one plugin wins whole. A merged copy built here came out with fewer navmeshes than Starfield.esm's own.
        if (typeof(INavigationMeshInfoMapGetter).IsAssignableFrom(getter))
            return "the navmesh info map isn't patched: each plugin's copy lists only its own navmeshes, so a merged copy would hide entries; " +
                   "don't copy it by hand either (the Creation Kit rebuilds it when a navmesh is finalized)";
        var hit = NotBuilt.FirstOrDefault(t => t.IsAssignableFrom(getter));
        if (hit is null) return null;
        return "dialogue and scenes (records inside a quest or dialogue topic) are not built by this version; copy the fields in xEdit";
    }

    /// <summary>Why a record that loses to an official BlueprintShips file isn't built (0.12.0).</summary>
    public static string BlueprintNotBuilt(string blueprint) =>
        $"{LoadOrderSetup.Display(blueprint)} loads after every mod, this patch too, so no patch or load order brings this change back; " +
        "nothing to do by hand either";

    static Type? GetterOf(string className)
    {
        var asm = typeof(StarfieldMod).Assembly;
        return asm.GetType($"Mutagen.Bethesda.Starfield.I{className}Getter");
    }

    public static int Run(string[] a, string version, string byline)
    {
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) Console.WriteLine($"RESULT: CANNOT BUILD ({cantWhy})");
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () =>
            {
                var where = Analyzer.Where.Length > 0 ? $" while {Analyzer.Where}" : "";
                var rec = Analyzer.RecordOf(e) is { Length: > 0 } r ? $" at {r}" : "";
                return $"RESULT: CANNOT BUILD ({e.GetType().Name}{rec}{where}: {SafeText.OneLine(e.Message.Split('\n')[0])})";
            }));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static bool SamePath(string x, string y) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(y)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        Analyzer.MaxManagedBytes = 0; Analyzer.Progress = null; Analyzer.Where = "";
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? pluginsTxt = null, dataDir = null, extraDir = null, outDir = null, logFile = null;
        var name = DefaultName;
        double maxGb = 0;
        bool quiet = false, verbose = false, includeOfficial = false;
        var only = new List<string>();
        var picks = new List<Pick>();
        string? Value(ref int i, string what) => Cli.Value(a, ref i, what, Usage);
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--plugins":
                    if (Value(ref i, "a Plugins.txt file") is not { } p) return 2;
                    pluginsTxt = p; break;
                case "--data":
                    if (Value(ref i, "the game's Data folder") is not { } d) return 2;
                    dataDir = d; break;
                case "--extra-data":
                    if (Value(ref i, "a folder") is not { } x) return 2;
                    extraDir = x; break;
                case "--out":
                    if (Value(ref i, "a folder") is not { } o) return 2;
                    outDir = o; break;
                case "--name":
                    if (Value(ref i, "a plugin file name such as MyPatch.esm") is not { } n) return 2;
                    name = n; break;
                case "--only":
                    if (Value(ref i, "plugin file names, separated by commas") is not { } ol) return 2;
                    only.AddRange(ol.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--pick":
                    if (Value(ref i, "<FormKey>=<plugin> or <FormKey>:<Field>=<plugin>") is not { } pk) return 2;
                    if (ParsePick(pk) is not { } pick)
                    {
                        Console.Error.WriteLine($"--pick needs <FormKey>=<plugin> or <FormKey>:<Field>=<plugin>, for example --pick 064F3C:Starfield.esm=ModA.esm " +
                                                $"or --pick 064F3C:Starfield.esm:Value=ModA.esm (the patch report prints ready-made lines), not \"{SafeText.OneLine(pk)}\"");
                        return 2;
                    }
                    picks.Add(pick); break;
                case "--max-memory":
                    if (Value(ref i, "a number of GB") is not { } m) return 2;
                    if (!double.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out maxGb) || !double.IsFinite(maxGb) || maxGb < 0.000001 || maxGb > 1_000_000)
                    {
                        Console.Error.WriteLine($"--max-memory needs a number of GB from 0.000001 to 1000000, not \"{m}\", for example: --max-memory 8");
                        return 2;
                    }
                    break;
                case "--log":
                    if (Value(ref i, "a file name") is not { } lf) return 2;
                    logFile = lf; break;
                case "--verbose": verbose = true; break;
                case "--include-official": includeOfficial = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help":   // help that was asked for: stdout, exit 0, no RESULT line (0.13.0)
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine(a[i].StartsWith("--") ? $"unknown option {a[i]}" : $"unexpected argument {a[i]} (patch reads the whole load order; give its Plugins.txt with --plugins)");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} patch on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        if (includeOfficial && only.Count > 0)
        {
            cantWhy = "--include-official with --only";
            Console.Error.WriteLine("--include-official can't be used with --only: --only patches only the named plugins' changes, and --include-official " +
                                    "adds changes one official plugin loses to another. Use one or the other.");
            return 2;
        }

        // The patch's name: a plain plugin file name the game keeps in Plugins.txt.
        if (!GameData.IsPlainPluginName(name) || !name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
        {
            cantWhy = "bad --name";
            Console.Error.WriteLine($"--name needs a plain plugin file name ending in .esm or .esp, with no folder, for example --name MyPatch.esm (not \"{SafeText.OneLine(name)}\")");
            return 2;
        }
        if (ReleaseRules.IsDroppedPluginName(name))
        {
            cantWhy = "bad --name";
            Console.Error.WriteLine($"--name {name} ends in an official plugin's name (or starts with BlueprintShips-): Starfield removes such names from Plugins.txt " +
                                    "and the patch would never load. Pick another name, for example ConflictPatch.esm.");
            return 2;
        }

        var dataWhy = dataDir != null ? "from --data" : "Steam's usual folder (no --data given)";
        dataDir ??= Cli.SteamData;
        if (!Directory.Exists(dataDir))
        {
            cantWhy = $"the Data folder {Path.GetFullPath(dataDir)} doesn't exist";
            Console.Error.WriteLine($"can't find the game's Data folder: {Path.GetFullPath(dataDir)} doesn't exist ({dataWhy}). Run again with " +
                                    "--data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data; a path with spaces needs quotes).");
            return 2;
        }
        dataDir = Path.GetFullPath(dataDir);
        Log.Write($"Data folder: {dataDir}: {dataWhy}");

        string pluginsWhy;
        if (pluginsTxt != null) pluginsWhy = "from --plugins";
        else if (File.Exists(GamePluginsTxt())) { pluginsTxt = GamePluginsTxt(); pluginsWhy = "the game's own Plugins.txt (no --plugins given)"; }
        else
        {
            cantWhy = "no load order: give --plugins";
            Console.Error.WriteLine($"can't find your load order: no --plugins given and no game Plugins.txt at {GamePluginsTxt()}. Run again with " +
                                    "--plugins \"<the Plugins.txt your mod manager uses>\" (MO2 keeps one per profile, in the profile's folder).");
            return 2;
        }
        if (!File.Exists(pluginsTxt))
        {
            cantWhy = $"the Plugins.txt {Path.GetFullPath(pluginsTxt)} doesn't exist";
            Console.Error.WriteLine($"can't read the load order: {Path.GetFullPath(pluginsTxt)} doesn't exist. Give --plugins the Plugins.txt your mod " +
                                    "manager uses (the game's is %LOCALAPPDATA%\\Starfield\\Plugins.txt; MO2 keeps one per profile).");
            return 2;
        }
        pluginsTxt = Path.GetFullPath(pluginsTxt);
        Log.Write($"load order: {pluginsTxt}: {pluginsWhy}");
        if (extraDir != null)
        {
            if (!Directory.Exists(extraDir))
            {
                cantWhy = $"the --extra-data folder {Path.GetFullPath(extraDir)} doesn't exist";
                Console.Error.WriteLine($"can't use --extra-data {Path.GetFullPath(extraDir)}: that folder doesn't exist. Check the path (a path with spaces needs quotes).");
                return 2;
            }
            extraDir = Path.GetFullPath(extraDir);
            Log.Write($"extra plugin folder (its files win over Data's): {extraDir}");
        }
        outDir = Path.GetFullPath(outDir ?? "sf-mod-check-patch");
        // Never into the game's folders: the patch goes in with the mod manager, like any other mod.
        if (SamePath(outDir, dataDir) || (extraDir != null && SamePath(outDir, extraDir)))
        {
            cantWhy = "--out is a plugin folder";
            Console.Error.WriteLine($"--out {outDir} is the Data folder (or the --extra-data folder). Give --out a folder of its own; then install the patch with your mod manager.");
            return 2;
        }
        Log.Write($"output folder: {outDir}");
        if (maxGb > 0) { Analyzer.MaxManagedBytes = (long)(maxGb * 1e9); Log.Write($"memory limit: {maxGb.ToString(CultureInfo.InvariantCulture)} GB"); }

        // The load order without an older copy of this patch: the new patch replaces it, and must not be built on it.
        var lines = File.ReadAllLines(pluginsTxt);
        var withoutPatch = lines.Where(l => !string.Equals(l.Trim().TrimStart('*').Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        Directory.CreateDirectory(outDir);
        var orderTxt = pluginsTxt;
        if (withoutPatch.Count != lines.Length)
        {
            orderTxt = Path.Combine(outDir, "Plugins-without-patch.txt");
            File.WriteAllLines(orderTxt, withoutPatch);
            if (!quiet) Console.Error.WriteLine($"note: your load order already has {name}; it is left out while the new one is built (the new one replaces it)");
            Log.Write($"{name} is in the load order: analyzed without it ({orderTxt})");
        }

        var sw = Stopwatch.StartNew();
        Analyzer.Info = quiet ? _ => { } : Console.Error.WriteLine;
        if (Log.On) Analyzer.Progress = Log.Write;
        LoadOrderSetup lo;
        try { lo = LoadOrderSetup.Build(dataDir, orderTxt, extraDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Close any program that has a plugin open (xEdit, the Creation Kit) and try again.");
            return 2;
        }
        try
        {
            if (!quiet) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            var byName = lo.Plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);
            // --only names must be loaded plugins (spelled any way)
            var onlySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in only)
            {
                if (!byName.TryGetValue(o, out var op))
                {
                    cantWhy = $"--only {SafeText.OneLine(o)} is not in the load order";
                    Console.Error.WriteLine($"--only {SafeText.OneLine(o)}: that plugin isn't active in {orderTxt} (or it couldn't be loaded; see the load-order problems with --verbose). " +
                                            "Give the file names of active plugins, separated by commas.");
                    return 2;
                }
                onlySet.Add(op.FileName);
            }
            foreach (var pk in picks.Where(pk => !byName.ContainsKey(pk.Plugin)))
            {
                cantWhy = $"--pick names {SafeText.OneLine(pk.Plugin)}, which is not in the load order";
                Console.Error.WriteLine($"--pick {pk.FormKey}{(pk.Field is null ? "" : ":" + pk.Field)}={SafeText.OneLine(pk.Plugin)}: that plugin isn't active in the load order.");
                return 2;
            }

            Analysis analysis;
            // --include-official (0.10.0): also restore changes one official plugin loses to another, for the record types
            // a patch can build (the rest would only fill the "Not built" list).
            Func<Type, bool>? officialTypes = includeOfficial ? t => WhyNotBuilt(t) is null : null;
            if (includeOfficial) Log.Write("patch: --include-official: changes one official plugin loses to another are restored too");
            // a field pick may name a record with no lost change (0.13.0): the analysis notes its type
            var pickKeys = picks.Where(pk => pk.Field is not null).Select(pk => pk.FormKey).ToHashSet();
            try { analysis = Analyzer.Run(lo.Plugins, onlySet.Count > 0 ? onlySet : null, officialTypes: officialTypes, wantTypes: pickKeys); }
            catch (MemoryLimitException e)
            {
                resultPrinted = true;
                Console.WriteLine($"RESULT: STOPPED ({SafeText.OneLine(e.Message)}; no patch written)");
                Console.Error.WriteLine("Try a smaller load order, use --only, or raise --max-memory if this computer has the memory.");
                return 3;
            }
            Analyzer.Where = "working out the patch";
            Plan plan;
            try { plan = Build(lo, analysis, onlySet, picks); }
            catch (MeshPickConflict e)
            {
                cantWhy = "two --pick options name different plugins for one navmesh's mesh";
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            plan.IncludeOfficial = includeOfficial;
            foreach (var pk in picks.Where(pk => !pk.Used))
                Console.Error.WriteLine(NotUsedWarning(pk));
            foreach (var pk in picks.Where(pk => pk.Used && pk.Overruled is not null))
                Console.Error.WriteLine($"WARNING: --pick {pk.FormKey}{(pk.Field is null ? "" : ":" + pk.Field)}={pk.Plugin} was not used: {pk.Overruled}");

            var reportPath = Path.Combine(outDir, "patch-report.md");
            var patchPath = Path.Combine(outDir, name);
            var withPatchTxt = Path.Combine(outDir, "Plugins-with-patch.txt");
            if (plan.Records.Count == 0)
            {
                // nothing to build: remove an old patch file from an earlier run, so nobody installs it by mistake
                if (File.Exists(patchPath)) File.Delete(patchPath);
                // and its load order with the patch line, which would name a plugin that is gone
                if (File.Exists(withPatchTxt)) File.Delete(withPatchTxt);
                WriteReport(reportPath, plan, lo, name, null, null, version);
                resultPrinted = true;
                Console.WriteLine($"RESULT: OK, nothing to patch: {plan.Forwarded} forwardable fields, {plan.NotBuilt.Count} records not built " +
                                  $"(report: {reportPath}; {sw.Elapsed.TotalSeconds:F0} s)");
                return 0;
            }

            Analyzer.Where = $"writing {patchPath}";
            var masters = WritePatch(plan, lo, dataDir, extraDir, outDir, name);
            File.WriteAllLines(withPatchTxt, withoutPatch.Append("*" + name));
            Analyzer.Where = $"reading {patchPath} back";
            var mismatches = ReadBack(patchPath, plan, lo);
            WriteReport(reportPath, plan, lo, name, masters, mismatches, version);
            foreach (var m in mismatches.Take(10)) Console.WriteLine($"ERROR [patch] {m}");
            resultPrinted = true;
            var choiceKept = plan.Choices.Count(c => !c.Picked && !c.Forward);
            var listsMerged = plan.Choices.Count(c => c.Merged);
            var head = mismatches.Count == 0 ? "RESULT: OK" : $"RESULT: FAILED, {mismatches.Count} read-back differences (don't use this patch)";
            Console.WriteLine($"{head}, patch {patchPath}: {plan.Records.Count} records, {plan.Forwarded} fields forwarded, {plan.Choices.Count(c => c.Picked && !c.Merged)} picks used, {listsMerged} shared lists merged, " +
                              $"{choiceKept} choice{(choiceKept == 1 ? "" : "s")} kept the winner's value, {plan.NotBuilt.Count} record{(plan.NotBuilt.Count == 1 ? "" : "s")} not built, " +
                              $"{masters.Count} masters (report: {reportPath}; {sw.Elapsed.TotalSeconds:F0} s)");
            Fatal.AfterResult(() => Log.Write($"result: {plan.Records.Count} records, {plan.Forwarded} forwarded, {mismatches.Count} read-back differences, {sw.Elapsed.TotalSeconds:F1} s"));
            return mismatches.Count == 0 ? 0 : 1;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    // ---------- the plan ----------

    /// <param name="Picked">set by a --pick (counted as a pick, not as a forwarded field)</param>
    public sealed record ForwardedField(string Field, string From, string Text, bool Picked = false);
    /// <param name="Forward">two losing plugins changed a field the winner left alone: the later one's value is used
    /// (not the winner's), unless a --pick names the other</param>
    public sealed record ChoiceField(FormKey FormKey, string RecordType, string? EditorID, string Field, string Winner, string Other, bool Picked, string Text, bool Merged = false, bool Forward = false);
    public sealed record NotBuiltRecord(FormKey FormKey, string RecordType, string? EditorID, string Why, List<string> Fields)
    {
        /// <summary>It loses to an official BlueprintShips file, so no patch can bring it back (0.12.0).</summary>
        public bool Blueprint { get; init; }
    }
    /// <param name="Sources">for each field copied whole, the plugin's version of the record its value came from</param>
    /// <param name="Merged">for each list field merged entry by entry, its entries as the plugins hold them</param>
    public sealed record PatchRecord(FormKey FormKey, string RecordType, string? EditorID, IMajorRecord Record, List<ForwardedField> Fields, List<string> Plugins,
        Dictionary<string, IMajorRecordGetter> Sources, Dictionary<string, List<object?>> Merged)
    {
        /// <summary>A worldspace that is in the patch only because a patched exterior cell sits in it, or a cell only
        /// because a patched placed object sits in it: the record as the game uses it today, with no field changed
        /// (0.13.0).</summary>
        public bool Parent { get; init; }
    }

    public sealed class Plan
    {
        public List<PatchRecord> Records { get; } = new();
        /// <summary>Where each patched cell goes (0.13.0).</summary>
        public Dictionary<FormKey, CellPlace> Places { get; } = new();
        /// <summary>Which cell (and which of its lists) each patched placed object goes in (0.13.0).</summary>
        public Dictionary<FormKey, RefPlace> RefPlaces { get; } = new();
        public List<ChoiceField> Choices { get; } = new();
        public List<NotBuiltRecord> NotBuilt { get; } = new();
        public List<string> Notes { get; } = new();
        // made with --include-official: some records restore a change one official plugin loses to another
        public bool IncludeOfficial { get; set; }
        public int Forwarded => Records.Sum(r => r.Fields.Count(f => !f.Picked));
    }

    /// <summary>Works out every patch record from the analysis. Reads only the records it needs, one record type at a
    /// time (as the analyzer does), so memory stays near the analysis's own.</summary>
    public static Plan Build(LoadOrderSetup lo, Analysis analysis, ISet<string> only, List<Pick> picks)
    {
        var plan = new Plan();
        var relevant = analysis.Lost
            .Where(l => only.Count == 0 || only.Contains(l.Loser) || only.Contains(l.Winner))
            .Where(l => l.Fields.Any(f => !Impact.IsMinor(f)))
            .ToList();
        // records with a lost change are patched; a field --pick may add another record (below), and a whole-record
        // --pick on any other record is reported as unused
        var byRecord = relevant.GroupBy(l => l.FormKey).ToList();
        var targets = new Dictionary<FormKey, (Type Getter, string ClassName)>();
        foreach (var g in byRecord)
        {
            var cls = g.First().RecordType;
            var getter = GetterOf(cls);
            var fwdFields = g.SelectMany(l => l.Fields.Where(f => !Impact.IsMinor(f)).Select(f => f.Field)).Distinct().ToList();
            // 0.12.0: a change lost to an official BlueprintShips file can't be patched. The game loads those files
            // after every plugin, the patch too, so their version would still win. The navmesh info map keeps its own
            // reason (WhyNotBuilt below), as it always has (step 4b).
            if (!Impact.IsNavi(cls) && g.Select(l => l.Winner).FirstOrDefault(LoadOrderSetup.IsBlueprint) is { } blueprint)
            {
                plan.NotBuilt.Add(new(g.Key, cls, g.First().EditorID, BlueprintNotBuilt(blueprint), fwdFields) { Blueprint = true });
                Log.Write($"patch: {g.Key} not built: it loses to {blueprint}, which the game loads after every plugin");
                continue;
            }
            if (getter is null)
            {
                plan.NotBuilt.Add(new(g.Key, cls, g.First().EditorID, $"sf-mod-check doesn't know the record type {cls}", fwdFields));
                Log.Write($"patch: {LogName(cls, g.First().EditorID, g.Key)} not built: sf-mod-check doesn't know the record type");
                continue;
            }
            if (WhyNotBuilt(getter) is { } why)
            {
                plan.NotBuilt.Add(new(g.Key, cls, g.First().EditorID, why, fwdFields));
                Log.Write($"patch: {LogName(cls, g.First().EditorID, g.Key)} not built: {why}");
                continue;
            }
            targets[g.Key] = (getter, cls);
        }
        // 0.13.0: a field --pick may name a record with no lost change, to keep one plugin's value of a field the winner
        // took from another mod (three city worldspaces where one mod filled an empty water field). The record then goes
        // in as the game uses it today, with that one field from the plugin picked.
        var lostKeys = byRecord.Select(g => g.Key).ToHashSet();
        var pickOnly = new HashSet<FormKey>();
        foreach (var fk in picks.Where(pk => pk.Field is not null).Select(pk => pk.FormKey).Distinct())
        {
            if (lostKeys.Contains(fk)) continue;
            string? whyNot = null;
            if (!analysis.WantedTypes.TryGetValue(fk, out var t))
                whyNot = only.Count > 0 ? "none of the --only plugins overrides that record" : "no mod in the load order overrides that record";
            else if (WhyNotBuilt(t) is { } why) whyNot = why;
            if (whyNot is not null)
            {
                foreach (var pk in picks.Where(pk => pk.FormKey == fk && pk.Field is not null)) pk.Why = whyNot;
                Log.Write($"patch: --pick on {fk}: {whyNot}");
                continue;
            }
            targets[fk] = (t!, Analyzer.TypeShort(t!));
            pickOnly.Add(fk);
        }

        // Each plugin's masters, transitively (file names), as the analyzer works them out.
        var byFile = lo.Plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);
        var masterMemo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> MastersOf(PluginInfo p)
        {
            if (masterMemo.TryGetValue(p.FileName, out var set)) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in p.Masters)
                if (byFile.TryGetValue(m, out var mp)) { set.Add(mp.FileName); set.UnionWith(MastersOf(mp)); }
            return masterMemo[p.FileName] = set;
        }

        // A record reference in report text, as its EditorID (else its FormKey). The EditorID-only cache is made only
        // when a merged list's first new entry is worded, so a patch with no merged lists never builds it.
        ILinkCache? idCache = null;
        string Name(IFormLinkGetter link)
        {
            try
            {
                idCache ??= lo.Plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>(LinkCachePreferences.OnlyIdentifiers());
                if (idCache.TryResolveIdentifier(link.FormKey, link.Type, out var editorId) && !string.IsNullOrEmpty(editorId)) return editorId;
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"patch: no EditorID for {link.FormKey}: {e.Message}"); }
            return link.FormKey.ToString();
        }

        // Every version of each target, in load order.
        var versions = new Dictionary<FormKey, List<(PluginInfo Plugin, IMajorRecordGetter Rec)>>();
        foreach (var byType in targets.GroupBy(t => t.Value.Getter))
        {
            var want = byType.Select(t => t.Key).ToHashSet();
            foreach (var p in lo.Plugins)
            {
                Analyzer.Where = $"reading {byType.First().Value.ClassName} records from {p.FileName} for the patch";
                foreach (var rec in p.Mod.EnumerateMajorRecords(byType.Key))
                {
                    if (!want.Contains(rec.FormKey)) continue;
                    if (!versions.TryGetValue(rec.FormKey, out var list)) versions[rec.FormKey] = list = new();
                    if (list.Count == 0 || list[^1].Plugin != p) list.Add((p, rec));
                }
            }
        }

        // 0.13.0: where each cell sits (interior block, or which worldspace and exterior block), from the last plugin
        // that holds it.
        var cellTargets = targets.Where(t => t.Value.Getter == typeof(ICellGetter)).Select(t => t.Key).ToHashSet();
        if (cellTargets.Count > 0)
            foreach (var p in lo.Plugins)
            {
                Analyzer.Where = $"finding where cells sit in {p.FileName} for the patch";
                FindPlaces(p.Mod, cellTargets, plan.Places);
            }
        // 0.13.0: which cell (persistent or temporary list, or its navmeshes) each placed object and navmesh sits in, from
        // the last plugin that holds it.
        var refTargets = targets.Where(t => typeof(IPlacedGetter).IsAssignableFrom(t.Value.Getter) || t.Value.Getter == typeof(INavigationMeshGetter))
            .Select(t => t.Key).ToHashSet();
        if (refTargets.Count > 0)
            foreach (var p in lo.Plugins)
            {
                Analyzer.Where = $"finding the cells of placed objects and navmeshes in {p.FileName} for the patch";
                foreach (var c in AllCells(p.Mod))
                {
                    foreach (var r in c.Persistent) if (refTargets.Contains(r.FormKey)) plan.RefPlaces[r.FormKey] = new(c.FormKey, true);
                    foreach (var r in c.Temporary) if (refTargets.Contains(r.FormKey)) plan.RefPlaces[r.FormKey] = new(c.FormKey, false);
                    foreach (var n in c.NavigationMeshes) if (refTargets.Contains(n.FormKey)) plan.RefPlaces[n.FormKey] = new(c.FormKey, false, true);
                }
            }

        foreach (var g in byRecord)
        {
            if (!targets.ContainsKey(g.Key)) continue;
            if (!versions.TryGetValue(g.Key, out var vs) || vs.Count == 0)
            {
                plan.NotBuilt.Add(new(g.Key, g.First().RecordType, g.First().EditorID, "its versions could not be read again", new()));
                Log.Write($"patch: {g.First().RecordType} {g.Key} not built: its versions could not be read again");
                continue;
            }
            if (cellTargets.Contains(g.Key) && !plan.Places.ContainsKey(g.Key))
            {
                plan.NotBuilt.Add(new(g.Key, g.First().RecordType, g.First().EditorID, "the cell's place (an interior block or a worldspace) could not be found", new()));
                Log.Write($"patch: cell {g.Key} not built: no interior block or worldspace holds it");
                continue;
            }
            if (refTargets.Contains(g.Key) && !plan.RefPlaces.ContainsKey(g.Key))
            {
                var kind = targets[g.Key].Getter == typeof(INavigationMeshGetter) ? "navmesh" : "placed object";
                plan.NotBuilt.Add(new(g.Key, g.First().RecordType, g.First().EditorID, $"the {kind}'s cell could not be found", new()));
                Log.Write($"patch: {kind} {g.Key} not built: no cell holds it");
                continue;
            }
            try { BuildRecord(g.Key, g.ToList(), vs, picks, plan, MastersOf, Name, g.First().RecordType, g.First().EditorID); }
            catch (Exception e) when (Fatal.Recoverable(e) && e is not MeshPickConflict)
            {
                plan.NotBuilt.Add(new(g.Key, g.First().RecordType, g.First().EditorID, $"it could not be built ({e.GetType().Name}: {SafeText.OneLine(e.Message)})", new()));
                Log.Write($"patch: {g.Key} not built: {e}");
            }
        }
        // the records only a field --pick names (above): built the same way, with no lost change to forward
        foreach (var fk in pickOnly)
        {
            string? whyNot = null;
            if (!versions.TryGetValue(fk, out var vs) || vs.Count == 0) whyNot = "its versions could not be read again";
            else if (LoadOrderSetup.IsBlueprint(vs[^1].Plugin.FileName))
                whyNot = $"{LoadOrderSetup.Display(vs[^1].Plugin.FileName)} has its last copy and loads after every plugin, this patch too";
            else if (cellTargets.Contains(fk) && !plan.Places.ContainsKey(fk)) whyNot = "the cell's place (an interior block or a worldspace) could not be found";
            else if (refTargets.Contains(fk) && !plan.RefPlaces.ContainsKey(fk)) whyNot = "the cell that holds it could not be found";
            else
                try { BuildRecord(fk, new(), vs, picks, plan, MastersOf, Name, targets[fk].ClassName, SafeText.OneLine(vs[^1].Rec.EditorID)); }
                catch (Exception e) when (Fatal.Recoverable(e) && e is not MeshPickConflict)
                {
                    whyNot = $"the record could not be built ({e.GetType().Name}: {SafeText.OneLine(e.Message)})";
                    Log.Write($"patch: {fk} (named by --pick) not built: {e}");
                }
            if (whyNot is null) continue;
            // nothing of this record was built, so none of its picks was used
            foreach (var pk in picks.Where(pk => pk.FormKey == fk && pk.Field is not null)) { pk.Used = false; pk.Overruled = null; pk.Why = whyNot; }
            Log.Write($"patch: --pick on {fk}: {whyNot}");
        }

        // 0.13.0: a placed object or navmesh can only be written inside its cell, so a cell the patch doesn't hold yet goes
        // in as the game uses it today (the same rule as a worldspace below): no other mod's change to the cell is undone.
        foreach (var r in plan.Records.Where(r => IsCellChild(r.Record)))
            Log.Write($"patch: {(r.Record is INavigationMeshGetter ? "navmesh" : "placed object")} {r.EditorID} [{r.FormKey}] goes in {PlaceOf(plan, r)}");
        var planKeys = plan.Records.Select(r => r.FormKey).ToHashSet();
        var cellsNeeded = plan.Records.Where(r => IsCellChild(r.Record)).Select(r => PlaceOf(plan, r).Cell)
            .Where(c => !planKeys.Contains(c)).ToHashSet();
        if (cellsNeeded.Count > 0)
        {
            var cv = new Dictionary<FormKey, List<(PluginInfo Plugin, IMajorRecordGetter Rec)>>();
            foreach (var p in lo.Plugins)
            {
                Analyzer.Where = $"reading the parent cells of placed objects from {p.FileName} for the patch";
                foreach (var c in AllCells(p.Mod))
                    if (cellsNeeded.Contains(c.FormKey))
                    {
                        if (!cv.TryGetValue(c.FormKey, out var list)) cv[c.FormKey] = list = new();
                        if (list.Count == 0 || list[^1].Plugin != p) list.Add((p, c));
                    }
                FindPlaces(p.Mod, cellsNeeded, plan.Places);
            }
            foreach (var c in cellsNeeded)
            {
                var refs = plan.Records.Where(r => IsCellChild(r.Record) && PlaceOf(plan, r).Cell == c).ToList();
                // A cell that can't go in (its versions can't be read again, no plugin places it, or composing today's
                // record fails) takes its objects out with it, each with a row that says which, so one bad cell never
                // stops the whole patch (code review 0.13.0, round 7).
                string? whyNot = !cv.TryGetValue(c, out var vs) || vs.Count == 0 ? $"its cell {c} could not be read again"
                    : !plan.Places.ContainsKey(c) ? $"its cell {c} sits in no interior block or worldspace that a plugin lists"
                    : null;
                IMajorRecord? rec = null;
                Dictionary<string, int>? source = null;
                if (whyNot is null)
                    try { (rec, source, _) = Compose(vs!, new Copies(vs!)); }
                    catch (Exception e) when (Fatal.Recoverable(e))
                    {
                        whyNot = $"its cell {c} could not be built ({e.GetType().Name}: {SafeText.OneLine(e.Message)})";
                        Log.Write($"patch: parent cell {c} could not be built: {e}");
                    }
                if (whyNot is not null)
                {
                    foreach (var o in refs)
                    {
                        plan.Records.Remove(o);
                        plan.NotBuilt.Add(new(o.FormKey, o.RecordType, o.EditorID, whyNot, new()));
                        Log.Write($"patch: {o.RecordType} {o.FormKey} not built: {whyNot}");
                    }
                    continue;
                }
                var cvs = vs!;
                var edid = cvs[^1].Rec.EditorID;
                plan.Records.Add(new(c, "Cell", edid, rec!, new(), cvs.Select(v => v.Plugin.FileName).ToList(),
                    source!.ToDictionary(s => s.Key, s => cvs[s.Value].Rec), new()) { Parent = true });
                plan.Notes.RemoveAll(n => n.Contains($"[{c}]") && n.EndsWith("so it is left out"));
                NoteNotBuiltParent(plan, c, ChildrenText(refs.Select(r => r.Record)));
                plan.Notes.Add($"Cell {edid} [{c}] is in the patch as the parent of {ChildrenText(refs.Select(r => r.Record))}: " +
                               "the record as the game uses it today, with no field changed");
                Log.Write($"patch: cell {edid} [{c}] added as the parent of {ChildrenText(refs.Select(r => r.Record))}, as the game uses it today (from {cvs.Count} versions), in {plan.Places[c]}");
            }
        }

        // 0.13.0: an exterior cell can only be written inside its worldspace, so a worldspace the patch doesn't hold yet
        // goes in as the game uses it today (each field from the last plugin that supplies it): no other mod's change to
        // the worldspace is undone, and nothing of its own is changed.
        foreach (var r in plan.Records.Where(r => r.Record is ICellGetter).ToList())
            Log.Write($"patch: cell {r.EditorID} [{r.FormKey}] goes in {plan.Places[r.FormKey]}");
        planKeys = plan.Records.Select(r => r.FormKey).ToHashSet();
        var worldsNeeded = plan.Records.Where(r => r.Record is ICellGetter).Select(r => plan.Places[r.FormKey].World).OfType<FormKey>()
            .Where(w => !planKeys.Contains(w)).ToHashSet();
        if (worldsNeeded.Count > 0)
        {
            var wv = new Dictionary<FormKey, List<(PluginInfo Plugin, IMajorRecordGetter Rec)>>();
            foreach (var p in lo.Plugins)
            {
                Analyzer.Where = $"reading the worldspaces of patched cells from {p.FileName} for the patch";
                foreach (var w in p.Mod.Worldspaces)
                    if (worldsNeeded.Contains(w.FormKey))
                    {
                        if (!wv.TryGetValue(w.FormKey, out var list)) wv[w.FormKey] = list = new();
                        list.Add((p, w));
                    }
            }
            foreach (var w in worldsNeeded)
            {
                var cells = plan.Records.Where(r => r.Record is ICellGetter && plan.Places[r.FormKey].World == w).ToList();
                if (!wv.TryGetValue(w, out var vs) || vs.Count == 0)
                {
                    // can't happen when the cell was found in it; keep the patch writable by leaving its cells out
                    DropCellsOfLostWorld(plan, w, cells);
                    continue;
                }
                IMajorRecord rec;
                Dictionary<string, int> source;
                // composing today's worldspace can fail as a record's own build can: its cells then leave the patch, each
                // explained, and the rest of the patch is still written (code review 0.13.0, round 7)
                try { (rec, source, _) = Compose(vs, new Copies(vs)); }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    Log.Write($"patch: parent worldspace {w} could not be built: {e}");
                    DropCellsOfLostWorld(plan, w, cells, $"could not be built ({e.GetType().Name}: {SafeText.OneLine(e.Message)})");
                    continue;
                }
                var edid = vs[^1].Rec.EditorID;
                plan.Records.Add(new(w, "Worldspace", edid, rec, new(), vs.Select(v => v.Plugin.FileName).ToList(),
                    source.ToDictionary(s => s.Key, s => vs[s.Value].Rec), new()) { Parent = true });
                // it may have been left out above as equal to today's record; it is in now, as the cells' parent
                plan.Notes.RemoveAll(n => n.Contains($"[{w}]") && n.EndsWith("so it is left out"));
                // a worldspace whose own build failed keeps its "Not built" row, which now says it is in only as a parent
                NoteNotBuiltParent(plan, w, $"{cells.Count} patched cell{(cells.Count == 1 ? "" : "s")}");
                plan.Notes.Add($"Worldspace {edid} [{w}] is in the patch as the parent of {cells.Count} patched cell{(cells.Count == 1 ? "" : "s")}: " +
                               "the record as the game uses it today, with no field changed");
                Log.Write($"patch: worldspace {edid} [{w}] added as the parent of {cells.Count} cell(s), as the game uses it today (from {vs.Count} versions)");
            }
        }
        if (plan.Records.Any(r => r.Record is IWorldspaceGetter))
            plan.Notes.Add("Worldspaces go in without their large-reference table (RNAM) and cell offsets (OFST, CLSZ), as Creation Kit overrides do: " +
                           "they index objects and cells of the plugin that holds them, which a patch doesn't have");
        // a record built first and then left out with its parent cell or worldspace takes its picks' rows with it: those
        // picks weren't used after all (code review 0.13.0, round 8)
        var inPatch = plan.Records.Select(r => r.FormKey).ToHashSet();
        var leftOut = plan.NotBuilt.Select(r => r.FormKey).Where(k => !inPatch.Contains(k)).ToHashSet();
        plan.Choices.RemoveAll(c => leftOut.Contains(c.FormKey));
        foreach (var pk in picks.Where(pk => pk.Used && leftOut.Contains(pk.FormKey)))
        {
            pk.Used = false;
            pk.Overruled = null;
            pk.Why = null;
        }
        // a pick on a record that isn't built (dialogue, NAVI, a BlueprintShips winner, a cell with no place) says so in
        // its "was not used" warning, rather than the general text (code review 0.13.0, round 7)
        foreach (var pk in picks.Where(pk => !pk.Used && pk.Why is null))
            if (plan.NotBuilt.FirstOrDefault(r => r.FormKey == pk.FormKey) is { } nb)
                pk.Why = $"that record isn't built: {nb.Why}";
        return plan;
    }

    /// <summary>A cell (or worldspace) whose own build failed (a "Not built" row) can still be needed as the parent of
    /// patched placed objects or navmeshes (or of patched cells). It then goes in as the game uses it today, and its row
    /// says so, so the report doesn't seem to list it twice for no reason: its own lost changes still need redoing (code
    /// review 0.13.0, rounds 1 and 7).</summary>
    internal static void NoteNotBuiltParent(Plan plan, FormKey cell, string children)
    {
        for (int k = 0; k < plan.NotBuilt.Count; k++)
            if (plan.NotBuilt[k].FormKey == cell)
                plan.NotBuilt[k] = plan.NotBuilt[k] with { Why = plan.NotBuilt[k].Why + ParentNoteMid + $"{children}, " +
                    "as the game uses it today, so its own lost changes are still not patched" };
    }

    const string ParentNoteMid = "; it is in the patch only as the parent of ";

    /// <summary>The place of a planned placed object or navmesh. Every one gets its place when it is planned, so a missing
    /// one is a bug in sf-mod-check: it stops the run with a message that names the record and says to report it, rather
    /// than a bare KeyNotFoundException or an object written without its cell (code review 0.13.0, round 5).</summary>
    internal static RefPlace PlaceOf(Plan plan, PatchRecord r) =>
        plan.RefPlaces.TryGetValue(r.FormKey, out var at) ? at
        : throw new InvalidOperationException($"{r.RecordType} {(r.EditorID is { Length: > 0 } id ? SafeText.OneLine(id) + " " : "")}[{r.FormKey}] has no planned place in a cell. " +
                                              "This is a bug in sf-mod-check: please report it with the --log file");

    /// <summary>The version that brought in the value version <paramref name="at"/> holds: the earliest of the versions
    /// before it that hold the same value without a break (0 when it is the record's first version's value), so a plugin
    /// that only carried another plugin's value forward isn't credited with the change; a field --pick's row uses it to
    /// name whose change it drops (code review 0.13.0, round 5).</summary>
    internal static int IntroducedAt(Func<int, object?> held, int at)
    {
        var v = held(at);
        // the first version's value, held again after a plugin changed it, is nobody's change (code review 0.13.0, round 6)
        if (FieldCompare.DeepEq(held(0), v, 0)) return 0;
        while (at > 0 && FieldCompare.DeepEq(held(at - 1), v, 0)) at--;
        return at;
    }

    /// <summary>The worldspace of these patched cells could not be read again: the cells and their placed objects and
    /// navmeshes leave the patch, each explained once. A cell with lost changes of its own gets a "Not built" row (or its
    /// existing row corrected); a cell that was there only as a parent gets a note instead, since it had no changes of
    /// its own to redo and its objects' rows say what was lost (code review 0.13.0, rounds 2 and 3).</summary>
    internal static void DropCellsOfLostWorld(Plan plan, FormKey world, List<PatchRecord> cells, string failed = "could not be read again")
    {
        foreach (var c in cells)
        {
            plan.Records.Remove(c);
            // every planned placed object and navmesh has its place (set when it was planned), as in the parent-cell step:
            // a missing one is a bug and fails loudly rather than leaving an object without its cell (round 4)
            var objs = plan.Records.Where(o => IsCellChild(o.Record) && PlaceOf(plan, o).Cell == c.FormKey).ToList();
            // a parent cell noted earlier as "in the patch only as the parent" gets its one row corrected
            if (!NoteParentDropped(plan, c.FormKey, world, failed))
            {
                if (c.Parent)
                    plan.Notes.Add($"Cell {c.EditorID} [{c.FormKey}] was needed only as the parent of {ChildrenText(objs.Select(o => o.Record))}, " +
                                   $"but its worldspace {world} {failed}, so it is left out with them (it has no changes of its own)");
                else
                    plan.NotBuilt.Add(new(c.FormKey, c.RecordType, c.EditorID, $"its worldspace {world} {failed}", new()));
            }
            Log.Write($"patch: cell {c.EditorID} [{c.FormKey}] left out: its worldspace {world} {failed}");
            foreach (var o in objs)
            {
                plan.Records.Remove(o);
                plan.NotBuilt.Add(new(o.FormKey, o.RecordType, o.EditorID, $"the worldspace {world} of its cell {failed}", new()));
                Log.Write($"patch: {o.RecordType} {o.FormKey} not built: the worldspace {world} of its cell {failed}");
            }
        }
    }

    /// <summary>A parent cell left out after all, because its worldspace could not be read again (or built, <paramref
    /// name="failed"/>): its note in Notes goes, and a "Not built" row NoteNotBuiltParent marked is corrected instead of a
    /// second row being added. True when such a row was there (code review 0.13.0, rounds 2 and 7).</summary>
    internal static bool NoteParentDropped(Plan plan, FormKey cell, FormKey world, string failed = "could not be read again")
    {
        plan.Notes.RemoveAll(n => n.StartsWith("Cell ") && n.Contains($"[{cell}] is in the patch as the parent of "));
        bool had = false;
        for (int k = 0; k < plan.NotBuilt.Count; k++)
            if (plan.NotBuilt[k].FormKey == cell)
            {
                var why = plan.NotBuilt[k].Why;
                var at = why.IndexOf(ParentNoteMid, StringComparison.Ordinal);
                if (at >= 0) why = why[..at];
                plan.NotBuilt[k] = plan.NotBuilt[k] with { Why = why + $"; it was needed as the parent of patched objects, but its worldspace {world} {failed}, so it isn't in the patch" };
                had = true;
            }
        return had;
    }

    /// <summary>"2 patched placed objects and 1 patched navmesh": what a parent cell is in the patch for.</summary>
    internal static string ChildrenText(IEnumerable<IMajorRecordGetter> children)
    {
        var list = children.ToList();
        int navs = list.Count(x => x is INavigationMeshGetter), refs = list.Count - navs;
        var parts = new List<string>();
        if (refs > 0) parts.Add($"{refs} patched placed object{(refs == 1 ? "" : "s")}");
        if (navs > 0) parts.Add($"{navs} patched navmesh{(navs == 1 ? "" : "es")}");
        // a parent whose children already left the plan (round 4): never an empty "the parent of , ..."
        return parts.Count > 0 ? string.Join(" and ", parts) : "patched objects";
    }

    /// <summary>Every cell one plugin holds: interior cells, and each worldspace's persistent cell and exterior cells.</summary>
    static IEnumerable<ICellGetter> AllCells(IStarfieldModGetter mod)
    {
        foreach (var b in mod.Cells)
            foreach (var s in b.SubBlocks)
                foreach (var c in s.Cells) yield return c;
        foreach (var w in mod.Worldspaces)
        {
            if (w.TopCell is { } tc) yield return tc;
            foreach (var b in w.SubCells)
                foreach (var s in b.Items)
                    foreach (var c in s.Items) yield return c;
        }
    }

    /// <summary>Records where each wanted cell sits in one plugin (an interior block, a worldspace's persistent cell or
    /// an exterior block). A later plugin's place replaces an earlier one's.</summary>
    static void FindPlaces(IStarfieldModGetter mod, HashSet<FormKey> want, Dictionary<FormKey, CellPlace> places)
    {
        foreach (var b in mod.Cells)
            foreach (var s in b.SubBlocks)
                foreach (var c in s.Cells)
                    if (want.Contains(c.FormKey)) places[c.FormKey] = new(null, false, b.BlockNumber, s.BlockNumber, 0, 0, 0, 0);
        foreach (var w in mod.Worldspaces)
        {
            if (w.TopCell is { } tc && want.Contains(tc.FormKey)) places[tc.FormKey] = new(w.FormKey, true, 0, 0, 0, 0, 0, 0);
            foreach (var b in w.SubCells)
                foreach (var s in b.Items)
                    foreach (var c in s.Items)
                        if (want.Contains(c.FormKey))
                            places[c.FormKey] = new(w.FormKey, false, 0, 0, b.BlockNumberX, b.BlockNumberY, s.BlockNumberX, s.BlockNumberY);
        }
    }

    // A deep copy per version, made only when one of its fields is needed.
    sealed class Copies(List<(PluginInfo Plugin, IMajorRecordGetter Rec)> versions)
    {
        readonly Dictionary<int, IMajorRecord> made = new();
        public IMajorRecord Of(int i) => made.TryGetValue(i, out var c) ? c : made[i] = CopyOf(versions[i].Rec);
    }

    /// <summary>Tests only: makes Compose fail for the records it returns true for, in the calling test's own flow
    /// (AsyncLocal), so the paths that keep the patch writable when a record can't be built can be run (code review
    /// 0.13.0, round 7). Always null in a real run.</summary>
    internal static readonly AsyncLocal<Func<FormKey, bool>?> ComposeFailsForTest = new();

    /// <summary>The record as the game uses it today: the last full version, with each later (partial) version's own
    /// fields laid over it. Source says which version each field's value comes from (an index into vs).</summary>
    static (IMajorRecord Rec, Dictionary<string, int> Source, PropertyInfo[] Fields) Compose(List<(PluginInfo Plugin, IMajorRecordGetter Rec)> vs, Copies copies)
    {
        if (ComposeFailsForTest.Value is { } fails && fails(vs[^1].Rec.FormKey))
            throw new InvalidOperationException("made to fail by a test");
        int baseIdx = vs.FindLastIndex(v => !FieldCompare.IsPartial(v.Rec));
        if (baseIdx < 0) baseIdx = 0;
        var rec = CopyOf(vs[baseIdx].Rec);
        // Only fields the record really stores: a reader's own bookkeeping (QuestBinaryOverlay.QuestSubGroupTriggerSpecs,
        // where in the file a group starts) has no counterpart in the record and is never copied.
        var getterFields = FieldCompare.Fields(vs[^1].Rec.GetType())
            .Where(p => p.Name == "MajorRecordFlagsRaw" || rec.GetType().GetProperty(p.Name) is not null).ToArray();
        // Values are compared as the plugins hold them, the way conflict compares them, never as Mutagen's in-memory
        // copies (an empty list and no list read back the same).
        var source = getterFields.ToDictionary(p => p.Name, _ => baseIdx);
        for (int j = baseIdx + 1; j < vs.Count; j++)
            foreach (var p in getterFields)
                if (FieldCompare.Provides(vs[j].Rec, p, out _))
                {
                    if (!SetField(rec, p.Name, copies.Of(j))) throw new InvalidOperationException($"field {p.Name} can't be set");
                    source[p.Name] = j;
                }
        rec.MajorRecordFlagsRaw &= ~PartialFlag;
        return (rec, source, getterFields);
    }

    static void BuildRecord(FormKey fk, List<LostChange> lost, List<(PluginInfo Plugin, IMajorRecordGetter Rec)> vs, List<Pick> picks, Plan plan,
        Func<PluginInfo, HashSet<string>> mastersOf, Func<IFormLinkGetter, string> name, string recordType, string? editorId)
    {
        var copies = new Copies(vs);
        var (rec, source, getterFields) = Compose(vs, copies);
        var byName = getterFields.ToDictionary(p => p.Name);
        var today = new Dictionary<string, int>(source);
        object? Held(string field, int i) => FieldCompare.Get(byName[field], vs[i].Rec);

        int IndexOf(string plugin) => vs.FindIndex(v => v.Plugin.FileName.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        var forwarded = new List<ForwardedField>();
        var setBy = new Dictionary<string, string>(StringComparer.Ordinal);
        // the record's type and EditorID for report lines (a record only a field --pick names has no lost change)
        var first = (RecordType: recordType, EditorID: editorId);
        // List fields are merged entry by entry (the rule of the author's in-game-proven merge builder, 2026-11-06):
        // each plugin's own additions are added and its own removals removed, against the version it was built on, so
        // two plugins that both add entries (a DLC and a mod adding references to one location) both keep them.
        // A cell's traversals (XTV2) are one value and are never merged: each entry carries data Mutagen calls Unknown*,
        // and merging entry by entry kept the game's version of entries a DLC had changed (the cells builder, 2026-11-10).
        var merged = new Dictionary<string, List<(object? Held, object? Mut, int Origin)>>(StringComparer.Ordinal);
        bool isNavmesh = vs[^1].Rec is INavigationMeshGetter;
        bool Mergeable(string field) => field != "Traversals" && !(isNavmesh && NavmeshUnit.Contains(field)) && rec.GetType().GetProperty(field) is { } mp
            && mp.PropertyType != typeof(byte[]) && typeof(IList).IsAssignableFrom(mp.PropertyType);
        static List<object?> Items(object? v) => v is IEnumerable e and not string ? e.Cast<object?>().ToList() : new();
        List<object?> MutItems(int i, string field) => Items(copies.Of(i).GetType().GetProperty(field)!.GetValue(copies.Of(i)));
        // City locations hold thousands of entries: each entry's hash is worked out once, and lookups go through Bags
        // (hash buckets), so a merge stays near linear (the first try, comparing every pair, ran for over 10 minutes).
        var hashes = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        int H(object? x) => x is null ? 0 : hashes.TryGetValue(x, out var h) ? h : hashes[x] = FieldCompare.DeepHash(x, 0);
        bool Same(object? x, object? y) => H(x) == H(y) && FieldCompare.DeepEq(x, y, 0);
        Bag BagOf(IEnumerable<object?> items) { var b = new Bag(H); foreach (var x in items) b.Add(x); return b; }
        // Entries that stand for one thing, so a list holds one of them: a companion reaction per actor. Location lists
        // can name one reference twice (a marker with two reference types), so they have no identity.
        // 0.13.0: a record holds one component of each kind (the city cells on 2026-11-22: every version had exactly one
        // CityMapsUsageComponent), so a component's kind is its identity. Without it, two versions of one component
        // whose file name differs only in letter case both went in, a duplicate. A kind Mutagen doesn't know gets none.
        // Code review 0.13.0, round 1: only while no version's list holds two components of that kind (multiKinds, set
        // per field by Merge, round 2); a kind that appears twice anywhere has no identity, so a second one is added, not swapped in.
        var multiKinds = new HashSet<string>();
        var multiByField = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? Identity(object? x) => x switch
        {
            IActorReactionGetter ar => "Actor:" + ar.Actor.FormKey,
            IAComponentGetter c when ComponentKind(c) is { } kind && !multiKinds.Contains(kind) => "Component:" + kind,
            _ => null,
        };
        static void AddMultiKinds(HashSet<string> into, IEnumerable<object?> list)
        {
            foreach (var g in list.OfType<IAComponentGetter>().Select(ComponentKind).OfType<string>().GroupBy(k => k))
                if (g.Count() > 1) into.Add(g.Key);
        }
        bool isLocation = vs[^1].Rec is ILocationGetter;
        // A location's Master* lists hold the base game's own entries, and full-copy mods leave them empty: the patch
        // takes them from the last official version that has them, as the in-game-proven builder does.
        if (isLocation)
            foreach (var p in getterFields.Where(p => p.Name.StartsWith("Master") && Mergeable(p.Name)))
            {
                var o = Enumerable.Range(0, vs.Count).LastOrDefault(j => vs[j].Plugin.Official && FieldCompare.Provides(vs[j].Rec, p, out _), -1);
                if (o < 0 || o == source[p.Name]) continue;
                if (!SetField(rec, p.Name, copies.Of(o))) throw new InvalidOperationException($"field {p.Name} can't be set");
                source[p.Name] = o;
            }
        PropertyInfo? MasterTwin(string field) => isLocation && field.StartsWith("Added") && byName.TryGetValue("Master" + field[5..], out var t) ? t : null;
        // The versions plugin li was built on that hold field (its own masters, transitively), latest last.
        List<int> MasterVersions(int li, PropertyInfo p) => Enumerable.Range(0, li)
            .Where(j => mastersOf(vs[li].Plugin).Contains(vs[j].Plugin.FileName) && FieldCompare.Provides(vs[j].Rec, p, out _)).ToList();
        // What one plugin's merge really did to the list the patch held before it: report lines are written from this,
        // not from the plugin's change against its master (an entry the winner or the Master list already has adds nothing).
        (int Added, int Changed, int Removed, object? FirstAdded) did = default;
        var seeded = new List<(string Field, int Origin, object? Held)>();
        bool Merge(string field, int li)
        {
            did = default;
            // Every way to fail is checked before anything is changed, so a false leaves merged and seeded as they were
            // (the caller then replaces the list whole, or keeps the winner's).
            var lHeld = Items(Held(field, li));
            var lMut = MutItems(li, field);
            if (lHeld.Count != lMut.Count) return false;
            var twin = MasterTwin(field);
            var inTwin = BagOf(twin is null ? new List<object?>() : Items(FieldCompare.Get(twin, vs[source[twin.Name]].Rec)));
            if (!merged.TryGetValue(field, out var state))
            {
                var s = source[field];
                var held = Items(Held(field, s));
                var mut = MutItems(s, field);
                if (held.Count != mut.Count) return false;   // can't line the two up: replace the list whole instead
                state = held.Zip(mut, (h, m) => (h, m, s)).ToList();
                var newSeeds = new List<(string Field, int Origin, object? Held)>();
                // A location's Added* lists: the official files store them as partial copies, and the last one doesn't
                // repeat the others' entries, so start from every official version's own entries.
                if (twin is not null)
                {
                    var have = BagOf(state.Select(x => x.Held));
                    for (int j = 0; j < vs.Count; j++)
                    {
                        if (!vs[j].Plugin.Official || !FieldCompare.Provides(vs[j].Rec, byName[field], out _)) continue;
                        var oh = Items(Held(field, j));
                        var om = MutItems(j, field);
                        if (oh.Count != om.Count) return false;
                        for (int k = 0; k < oh.Count; k++)
                            if (!have.Contains(oh[k]) && !inTwin.Contains(oh[k])) { state.Add((oh[k], om[k], j)); have.Add(oh[k]); newSeeds.Add((field, j, oh[k])); }
                    }
                }
                seeded.RemoveAll(x => x.Field == field);   // a list replaced whole and merged again starts over
                seeded.AddRange(newSeeds);
                merged[field] = state;
            }
            // entries the seeding above took from this plugin count as its own additions (worded once, in its line)
            var mine = seeded.Where(x => x.Field == field && x.Origin == li).ToList();
            if (mine.Count > 0)
            {
                seeded.RemoveAll(x => x.Field == field && x.Origin == li);
                did = (mine.Count, 0, 0, mine[0].Held);
            }
            var mv = MasterVersions(li, byName[field]);
            var mvBags = mv.Select(j => BagOf(Items(Held(field, j)))).ToList();
            var baseHeld = mv.Count > 0 ? Items(Held(field, mv[^1])) : new();
            var baseBag = mv.Count > 0 ? mvBags[^1] : new Bag(H);
            var lBag = BagOf(lHeld);
            var stateBag = BagOf(state.Select(x => x.Held));
            // round 2: worked out once per field over every version, so the rule can't change between one plugin's
            // merge and the next (an earlier swap would then stand while a later plugin's merge added a second one)
            if (!multiByField.TryGetValue(field, out var mk))
            {
                mk = new HashSet<string>();
                for (int j = 0; j < vs.Count; j++) AddMultiKinds(mk, Items(Held(field, j)));
                multiByField[field] = mk;
            }
            multiKinds.Clear();
            multiKinds.UnionWith(mk);
            for (int k = 0; k < lHeld.Count; k++)
            {
                if (baseBag.Contains(lHeld[k]) || stateBag.Contains(lHeld[k])) continue;
                if (inTwin.Contains(lHeld[k])) continue;   // a full copy listing a base-game entry again
                var id = Identity(lHeld[k]);
                var at = id is null ? -1 : state.FindIndex(x => Identity(x.Held) == id);
                if (at < 0)
                {
                    state.Add((lHeld[k], lMut[k], li)); stateBag.Add(lHeld[k]);
                    did = (did.Added + 1, did.Changed, did.Removed, did.FirstAdded ?? lHeld[k]);
                }
                // the later plugin's version of that entry, or this one's when the entry there is still the one it changed
                else if (state[at].Origin < li || baseBag.Contains(state[at].Held))
                {
                    stateBag.Remove(state[at].Held);
                    state[at] = (lHeld[k], lMut[k], li);
                    stateBag.Add(lHeld[k]);
                    did.Changed++;
                }
            }
            // an entry counts as removed only when every version the plugin was built on has it
            foreach (var b in baseHeld)
                if (!lBag.Contains(b) && mvBags.All(m => m.Contains(b)))
                {
                    // a removed entry replaced by its own new version (same identity) was handled as a change above
                    if (Identity(b) is { } bid && lHeld.Any(x => Identity(x) == bid)) continue;
                    if (!stateBag.Contains(b)) continue;
                    var at = state.FindIndex(x => Same(x.Held, b));
                    if (at >= 0) { stateBag.Remove(state[at].Held); state.RemoveAt(at); did.Removed++; }
                }
            return true;
        }
        // One line for the report: what the merge added and removed for this plugin, over the list the patch already had.
        string MergedText(string field, string winner)
        {
            if (did.Added == 0 && did.Changed == 0 && did.Removed == 0) return SafeText.OneLine($"nothing new over {winner}'s list");
            var parts = new List<string>();
            if (did.Added > 0)
            {
                var first = Impact.Changes(field, new List<object?>(), new List<object?> { did.FirstAdded }, name).FirstOrDefault()?.Text ?? "";
                first = first.StartsWith("adds ") ? first[5..] : first;
                parts.Add($"adds {did.Added} {(did.Added == 1 ? "entry" : "entries")}{(first.Length > 0 ? $" (first: {first})" : "")}");
            }
            if (did.Changed > 0) parts.Add($"changes {did.Changed} {(did.Changed == 1 ? "entry" : "entries")}");
            if (did.Removed > 0) parts.Add($"removes {did.Removed} {(did.Removed == 1 ? "entry" : "entries")}");
            return SafeText.OneLine(string.Join(" and ", parts));
        }
        // Forwardable fields, earlier losers first, so a later loser's value wins a field two of them changed.
        foreach (var l in lost.OrderBy(l => IndexOf(l.Loser)))
        {
            var li = IndexOf(l.Loser);
            if (li < 0) continue;
            foreach (var f in l.Fields.Where(f => !f.WinnerChangedToo && !Impact.IsMinor(f)))
            {
                if (!byName.ContainsKey(f.Field)) { Log.Write($"patch: {fk} field {f.Field} is not stored in the record; not copied"); continue; }
                if (Mergeable(f.Field) && Merge(f.Field, li))
                {
                    setBy[f.Field] = l.Loser;
                    forwarded.Add(new(f.Field, l.Loser, MergedText(f.Field, l.Winner)));
                    continue;
                }
                // an earlier loser's merge is thrown away here, so its additions are gone even when the lists held look alike
                var hadMerge = merged.Remove(f.Field);
                if (setBy.TryGetValue(f.Field, out var prev) && (hadMerge || !FieldCompare.DeepEq(Held(f.Field, source[f.Field]), Held(f.Field, li), 0)))
                    plan.Choices.Add(new(fk, l.RecordType, l.EditorID, f.Field, l.Loser, prev, false,
                        $"{prev} and {l.Loser} both changed {f.Field} and the winner {l.Winner} kept neither; the patch uses {l.Loser}'s (it loads later)", Forward: true));
                // the whole value is replaced: only this plugin's line stays (two losers with one value count once)
                forwarded.RemoveAll(x => x.Field == f.Field);
                if (!SetField(rec, f.Field, copies.Of(li))) throw new InvalidOperationException($"field {f.Field} can't be set");
                source[f.Field] = li;
                setBy[f.Field] = l.Loser;
                var change = f.LoserChanges.Where(d => !d.Minor).Select(d => d.Text).FirstOrDefault() ?? "";
                forwarded.Add(new(f.Field, l.Loser, SafeText.OneLine(change)));
            }
        }
        // A navmesh's mesh fields take no pick here or in the loops below: the navmesh step at the end applies the one
        // mesh pick rule (code review 0.13.0, round 7).
        bool MeshField(string field) => isNavmesh && NavmeshUnit.Contains(field);
        // A --pick on a field two losers both changed: the named plugin's whole value is used instead of the later one's.
        foreach (var field in plan.Choices.Where(c => c.FormKey == fk && c.Forward && !c.Picked).Select(c => c.Field).Distinct().ToList())
        {
            if (MeshField(field)) continue;
            var fp = byName[field];
            var pick = picks.Where(pk => pk.FormKey == fk && (pk.Field is null || pk.Field == field))
                .Where(pk => IndexOf(pk.Plugin) is var pi && pi >= 0 && FieldCompare.Provides(vs[pi].Rec, fp, out _))
                .OrderBy(pk => pk.Field is null ? 0 : 1).LastOrDefault();
            if (pick is null) continue;
            pick.Used = true;
            var pIdx = IndexOf(pick.Plugin);
            merged.Remove(field);
            if (!SetField(rec, field, copies.Of(pIdx))) throw new InvalidOperationException($"field {field} can't be set");
            source[field] = pIdx;
            setBy[field] = pick.Plugin;
            forwarded.RemoveAll(x => x.Field == field);
            forwarded.Add(new(field, pick.Plugin, "picked with --pick", Picked: true));
            var rows = plan.Choices.Where(c => c.FormKey == fk && c.Field == field && c.Forward).ToList();
            plan.Choices.RemoveAll(rows.Contains);
            var r0 = rows[0];
            plan.Choices.Add(new(fk, r0.RecordType, r0.EditorID, field, rows[^1].Winner, pick.Plugin, true, $"--pick: {pick.Plugin}'s value is used", Forward: true));
        }
        // Choice fields: the winner's value stays unless a --pick names another plugin. A list both changed is merged
        // entry by entry instead (each one's additions and removals), unless a --pick takes one plugin's whole list.
        foreach (var l in lost.OrderBy(l => IndexOf(l.Loser)))
            foreach (var f in l.Fields.Where(f => f.WinnerChangedToo && !Impact.IsMinor(f)))
            {
                if (plan.Choices.Any(c => c.FormKey == fk && c.Field == f.Field && !c.Forward
                    && ((c.Picked && !c.Merged) || c.Other.Equals(l.Loser, StringComparison.OrdinalIgnoreCase)))) continue;
                // a field pick wins over a whole-record pick; the last one given wins among equals
                if (!byName.TryGetValue(f.Field, out var field)) { Log.Write($"patch: {fk} field {f.Field} is not stored in the record; not copied"); continue; }
                var pick = MeshField(f.Field) ? null : picks.Where(pk => pk.FormKey == fk && (pk.Field is null || pk.Field == f.Field))
                    .Where(pk => IndexOf(pk.Plugin) is var pi && pi >= 0 && FieldCompare.Provides(vs[pi].Rec, field, out _))
                    .OrderBy(pk => pk.Field is null ? 0 : 1).LastOrDefault();
                var change = SafeText.OneLine(f.LoserChanges.Where(d => !d.Minor).Select(d => d.Text).FirstOrDefault() ?? "");
                if (pick is not null)
                {
                    pick.Used = true;
                    merged.Remove(f.Field);   // a pick takes that plugin's whole value
                    if (!SetField(rec, f.Field, copies.Of(IndexOf(pick.Plugin)))) throw new InvalidOperationException($"field {f.Field} can't be set");
                    source[f.Field] = IndexOf(pick.Plugin);
                    setBy[f.Field] = pick.Plugin;
                    forwarded.RemoveAll(x => x.Field == f.Field);
                    var pickedWinner = pick.Plugin.Equals(l.Winner, StringComparison.OrdinalIgnoreCase);
                    if (!pickedWinner) forwarded.Add(new(f.Field, pick.Plugin, "picked with --pick", Picked: true));
                    // the row names the plugin picked (with three versions, a pick of the last loser is credited to it)
                    plan.Choices.Add(new(fk, l.RecordType, l.EditorID, f.Field, l.Winner, pickedWinner ? l.Loser : pick.Plugin, true, $"--pick: {pick.Plugin}'s value is used"));
                    continue;
                }
                var li = IndexOf(l.Loser);
                if (li >= 0 && Mergeable(f.Field) && Merge(f.Field, li))
                {
                    setBy[f.Field] = l.Loser;
                    forwarded.Add(new(f.Field, l.Loser, $"merged entry by entry with {l.Winner}'s list (both changed it): {MergedText(f.Field, l.Winner)}"));
                    plan.Choices.Add(new(fk, l.RecordType, l.EditorID, f.Field, l.Winner, l.Loser, true,
                        $"both changed the list; merged entry by entry (an entry both changed keeps the later plugin's version)", Merged: true));
                    continue;
                }
                plan.Choices.Add(new(fk, l.RecordType, l.EditorID, f.Field, l.Winner, l.Loser, false,
                    $"both changed it; the winner {l.Winner}'s value is kept{(change.Length > 0 ? $" ({l.Loser} {change})" : "")}"));
            }

        // 0.13.0: a field --pick the loops above didn't take keeps the named plugin's value on any field: one a loser's
        // change was forwarded into, one nobody lost, or one of a record only the pick names. This is how a patch keeps a
        // field as the game ships it when the winner took another mod's value there (a mod that filled three city
        // worldspaces' empty water field). Among picks on one field the last one given decides. A navmesh's mesh fields
        // keep their own rule below.
        foreach (var byField in picks.Where(pk => pk.FormKey == fk && pk.Field is not null && !pk.Used && pk.Why is null
                     && !(isNavmesh && NavmeshUnit.Contains(pk.Field))).GroupBy(pk => pk.Field!).ToList())
        {
            var field = byField.Key;
            if (!byName.TryGetValue(field, out var fp))
            {
                foreach (var pk in byField) pk.Why = $"{first.RecordType} records have no field {field} (the patch report and conflict's report name the fields)";
                continue;
            }
            foreach (var pk in byField)
                pk.Why = IndexOf(pk.Plugin) is var pi && pi < 0 ? $"{pk.Plugin} doesn't hold that record"
                    : !FieldCompare.Provides(vs[pi].Rec, fp, out _) ? $"{pk.Plugin}'s copy of that record is partial and doesn't set {field}" : null;
            var usable = byField.Where(pk => pk.Why is null).ToList();
            if (usable.Count == 0) continue;
            var pick = usable[^1];
            foreach (var earlier in usable.SkipLast(1)) { earlier.Used = true; earlier.Overruled = $"a later --pick on the same field names {pick.Plugin}"; }
            var pIdx = IndexOf(pick.Plugin);
            pick.Used = true;
            var winner = vs[^1].Plugin.FileName;
            var rowsGone = plan.Choices.Where(c => c.FormKey == fk && c.Field == field && !c.Picked).ToList();
            var dropped = forwarded.Where(x => x.Field == field && !x.Picked).Select(x => x.From).Concat(rowsGone.Select(c => c.Other))
                .Where(x => !x.Equals(pick.Plugin, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan.Choices.RemoveAll(rowsGone.Contains);
            merged.Remove(field);
            if (!SetField(rec, field, copies.Of(pIdx))) throw new InvalidOperationException($"field {field} can't be set");
            source[field] = pIdx;
            setBy[field] = pick.Plugin;
            forwarded.RemoveAll(x => x.Field == field);
            var asToday = FieldCompare.DeepEq(Held(field, pIdx), Held(field, today[field]), 0);
            // the pick replaces today's value: the plugin that brought that value in is named too (none when it is the
            // first version's value)
            if (!asToday && IntroducedAt(i => Held(field, i), today[field]) is var by && by > 0
                && !vs[by].Plugin.FileName.Equals(pick.Plugin, StringComparison.OrdinalIgnoreCase)
                && !dropped.Contains(vs[by].Plugin.FileName, StringComparer.OrdinalIgnoreCase))
                dropped.Add(vs[by].Plugin.FileName);
            if (!asToday) forwarded.Add(new(field, pick.Plugin, "picked with --pick", Picked: true));
            var gone = dropped.Count > 0 ? $"; {string.Join(" and ", dropped)}'s change to {field} is dropped" : "";
            // a pick that changes nothing counts as taken but adds no row (as on Choice fields)
            if (!asToday || dropped.Count > 0)
                plan.Choices.Add(new(fk, first.RecordType, first.EditorID, field, winner, asToday ? dropped[^1] : pick.Plugin, true, $"--pick: {pick.Plugin}'s value is used{gone}"));
            Log.Write($"patch: {LogName(first.RecordType, first.EditorID, fk)}: --pick keeps {pick.Plugin}'s {field}{(asToday ? " (today's value)" : "")}{gone}");
        }

        // 0.13.0: a navmesh's mesh (NavmeshUnit: geometry, NNAM, pre-cut entries) is one value, taken whole from one
        // version: the pre-cut entries index the geometry's triangles, so a mix would be a mesh no one made. The --pick
        // rule is kept simple (code review 0.13.0, round 7, which replaced the per-part pick ranking of rounds 3 to 6):
        // - a field --pick on any mesh field takes that plugin's whole mesh;
        // - field picks on the mesh that name two plugins stop the run with one error naming both (MeshPickConflict);
        // - a whole-record --pick takes the whole mesh only when the mesh is one of its Choice fields (the winner changed
        //   a mesh field a loser changed too) and no field pick names the mesh;
        // - with no pick the mesh follows its geometry's version, unless the other parts there differ from where they'd
        //   come from: then today's mesh is kept and the report says so.
        if (isNavmesh)
        {
            var unit = NavmeshUnit.Where(byName.ContainsKey).ToList();
            // the whole mesh is copied from the picked plugin, so its copy must hold every part (a partial copy holding only
            // NNAM would leave the geometry empty; code review 0.13.0, round 8)
            bool HoldsMesh(Pick pk) => IndexOf(pk.Plugin) is var pi && pi >= 0 && unit.All(f => FieldCompare.Provides(vs[pi].Rec, byName[f], out _));
            var fieldPicks = picks.Where(pk => pk.FormKey == fk && pk.Field is not null && NavmeshUnit.Contains(pk.Field)).ToList();
            foreach (var pk in fieldPicks.Where(pk => !HoldsMesh(pk)))
                pk.Why = IndexOf(pk.Plugin) < 0 ? $"{pk.Plugin} doesn't hold that navmesh" : $"{pk.Plugin}'s copy of that navmesh is partial and doesn't hold its whole mesh (geometry, NNAM and pre-cut entries)";
            var usable = fieldPicks.Where(HoldsMesh).ToList();
            var named = usable.GroupBy(pk => vs[IndexOf(pk.Plugin)].Plugin.FileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (named.Count > 1)
                throw new MeshPickConflict(
                    $"--pick {fk}:{named[0].Field}={named[0].Plugin} and --pick {fk}:{named[1].Field}={named[1].Plugin} name two plugins for the mesh of navmesh " +
                    $"{(string.IsNullOrEmpty(first.EditorID) ? "" : SafeText.OneLine(first.EditorID) + " ")}[{fk}]. A navmesh's geometry, NNAM and pre-cut entries come whole " +
                    "from one plugin: keep only the --pick that names the plugin whose mesh you want, and run again.");
            bool meshIsChoice = lost.Any(l => l.Fields.Any(f => f.WinnerChangedToo && !Impact.IsMinor(f) && unit.Contains(f.Field)));
            var wholePicks = picks.Where(pk => pk.FormKey == fk && pk.Field is null && HoldsMesh(pk)).ToList();
            var decides = usable.LastOrDefault() ?? (meshIsChoice ? wholePicks.LastOrDefault() : null);
            // a whole-record pick left with nothing to take, on a navmesh whose mesh isn't a Choice field, says why (code
            // review 0.13.0, round 8)
            if (decides is null)
                foreach (var pk in wholePicks.Where(pk => !pk.Used))
                    pk.Why ??= "that navmesh's mesh isn't a field the winner and another plugin both changed, so a whole-record --pick leaves it alone; " +
                               $"to take {pk.Plugin}'s mesh, use --pick {fk}:NavmeshGeometry={pk.Plugin}";
            if (decides is not null)
            {
                var di = IndexOf(decides.Plugin);
                var picked = vs[di].Plugin.FileName;
                foreach (var pk in usable) pk.Used = true;
                decides.Used = true;
                // a whole-record pick that took nothing else gives way to the field pick, and its warning says why; one that
                // names the same plugin got the mesh it asked for (code review 0.13.0, round 8)
                if (usable.Count > 0 && meshIsChoice)
                    foreach (var pk in wholePicks.Where(pk => !pk.Used))
                        if (vs[IndexOf(pk.Plugin)].Plugin.FileName.Equals(picked, StringComparison.OrdinalIgnoreCase)) pk.Used = true;
                        else pk.Why = $"the --pick on {decides.Field} ({picked}) decides that navmesh's whole mesh, and a field --pick ranks above a whole-record --pick";
                // the plugins whose change to the mesh goes: the row names one of them when the winner itself is picked
                var meshGone = forwarded.Where(x => unit.Contains(x.Field)).Select(x => x.From)
                    .Concat(plan.Choices.Where(c => c.FormKey == fk && unit.Contains(c.Field)).Select(c => c.Other))
                    .Where(n => !n.Equals(picked, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                forwarded.RemoveAll(x => unit.Contains(x.Field));
                plan.Choices.RemoveAll(c => c.FormKey == fk && unit.Contains(c.Field));
                foreach (var f in unit)
                {
                    if (source[f] != di && !SetField(rec, f, copies.Of(di))) throw new InvalidOperationException($"field {f} can't be set");
                    source[f] = di;
                    if (di != today[f] && !FieldCompare.DeepEq(Held(f, di), Held(f, today[f]), 0))
                        forwarded.Add(new(f, picked, "picked with --pick (part of the navmesh's mesh)", Picked: true));
                }
                // a pick that changes nothing counts as taken but adds no row, as on every other field (code review 0.13.0, round 8)
                bool meshChanged = forwarded.Any(x => unit.Contains(x.Field) && x.Picked);
                if (meshChanged || meshGone.Count > 0)
                    plan.Choices.Add(new(fk, first.RecordType, first.EditorID, unit[0], vs[^1].Plugin.FileName,
                        (!meshChanged || picked.Equals(vs[^1].Plugin.FileName, StringComparison.OrdinalIgnoreCase)) && meshGone.Count > 0 ? meshGone[^1] : picked, true,
                        $"--pick: {picked}'s whole mesh is used (a navmesh's geometry, NNAM and pre-cut entries come from one plugin, so other plugins' changes to them are dropped)"));
                Log.Write($"patch: navmesh {fk}: --pick takes {picked}'s whole mesh (geometry, NNAM, pre-cut entries){(meshChanged || meshGone.Count > 0 ? "" : ", which is today's mesh")}");
            }
            else
            {
                int u0 = unit.Contains("NavmeshGeometry") ? source["NavmeshGeometry"] : -1;
                bool mixed = u0 >= 0 && unit.Any(f => source[f] != u0 && !FieldCompare.DeepEq(Held(f, source[f]), Held(f, u0), 0));
                if (mixed)
                {
                    var who = forwarded.Where(x => unit.Contains(x.Field)).Select(x => x.From).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var f in unit)
                    {
                        if (source[f] != today[f] && !SetField(rec, f, copies.Of(today[f]))) throw new InvalidOperationException($"field {f} can't be set");
                        source[f] = today[f];
                    }
                    forwarded.RemoveAll(x => unit.Contains(x.Field));
                    if (who.Count > 0)
                        plan.Choices.Add(new(fk, first.RecordType, first.EditorID, "NavmeshGeometry", vs[^1].Plugin.FileName, who[^1], false,
                            $"the navmesh's geometry, NNAM and pre-cut entries are one mesh, and {string.Join(" and ", who)} changed only part of it while " +
                            $"another plugin's copy differs in the rest; today's mesh is kept (a mix would be a mesh no one made; redo the change in the Creation Kit if it is needed)"));
                    Log.Write($"patch: navmesh {fk}: its mesh would mix versions ({string.Join(", ", unit.Select(f => $"{f} from {vs[source[f]].Plugin.FileName}"))}); today's mesh is kept");
                }
                else if (u0 >= 0)
                {
                    foreach (var f in unit.Where(f => source[f] != u0))
                    {
                        if (!SetField(rec, f, copies.Of(u0))) throw new InvalidOperationException($"field {f} can't be set");
                        source[f] = u0;
                    }
                    Log.Write($"patch: navmesh {fk}: its mesh (geometry, NNAM, pre-cut entries) comes whole from {vs[u0].Plugin.FileName}");
                }
            }
        }

        // The merged lists go into the record (same order as their entries were collected).
        foreach (var (field, state) in merged)
        {
            var p = rec.GetType().GetProperty(field)!;
            var target = p.GetValue(rec) as IList;
            if (target is null)
            {
                if (state.Count == 0) continue;
                if (p.SetMethod is not { IsPublic: true }) throw new InvalidOperationException($"field {field} can't be set");
                target = (IList)System.Activator.CreateInstance(p.PropertyType)!;
                p.SetValue(rec, target);
            }
            target.Clear();
            foreach (var x in state) target.Add(x.Mut);
        }
        bool ListSame(List<object?> a, List<object?> b)
        {
            if (a.Count != b.Count) return false;
            // as multisets: each entry of a takes one matching entry of b ([x,x,y] is not [x,y,y])
            var bb = BagOf(b);
            foreach (var x in a)
            {
                if (!bb.Contains(x)) return false;
                bb.Remove(x);
            }
            return true;
        }
        // Nothing differs from today's record: leave it out (an ITM would only add risk).
        var changed = getterFields.Any(p => merged.TryGetValue(p.Name, out var st)
            ? !ListSame(st.Select(x => x.Held).ToList(), Items(Held(p.Name, today[p.Name])))
            : source[p.Name] != today[p.Name] && !FieldCompare.DeepEq(Held(p.Name, source[p.Name]), Held(p.Name, today[p.Name]), 0));
        if (!changed)
        {
            plan.Notes.Add($"{first.RecordType} {first.EditorID} [{fk}]: the patched record would equal today's, so it is left out");
            Log.Write($"patch: {LogName(first.RecordType, first.EditorID, fk)} left out: the patched record would equal today's");
            return;
        }
        // A quest's dialogue and scenes are records of their own: only the quest record itself goes in.
        ClearChildRecords(rec);
        var plugins = vs.Select(v => v.Plugin.FileName).ToList();
        Log.Write($"patch: {LogName(first.RecordType, first.EditorID, fk)} built: {forwarded.Count} field(s) forwarded" +
                  (forwarded.Count > 0 ? $" ({string.Join(", ", forwarded.Select(f => $"{f.Field} from {f.From}").Distinct())})" : "") +
                  (merged.Count > 0 ? $", {merged.Count} list(s) merged ({string.Join(", ", merged.Keys)})" : ""));
        plan.Records.Add(new(fk, first.RecordType, first.EditorID, rec, forwarded, plugins,
            source.Where(s => !merged.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => vs[s.Value].Rec),
            merged.ToDictionary(m => m.Key, m => m.Value.Select(x => x.Held).ToList())));
    }

    /// <summary>List entries in hash buckets: Contains is a bucket lookup plus a deep compare (FieldCompare.DeepEq) on
    /// the few entries with the same hash.</summary>
    sealed class Bag(Func<object?, int> hash)
    {
        readonly Dictionary<int, List<object?>> buckets = new();
        public void Add(object? x)
        {
            var h = hash(x);
            if (!buckets.TryGetValue(h, out var list)) buckets[h] = list = new();
            list.Add(x);
        }
        public bool Contains(object? x) => buckets.TryGetValue(hash(x), out var list) && list.Any(y => ReferenceEquals(x, y) || FieldCompare.DeepEq(x, y, 0));
        public void Remove(object? x)
        {
            if (!buckets.TryGetValue(hash(x), out var list)) return;
            var at = list.FindIndex(y => ReferenceEquals(x, y) || FieldCompare.DeepEq(x, y, 0));
            if (at >= 0) list.RemoveAt(at);
        }
    }

    /// <summary>Sets rec.field to src's value (src is a deep copy, so nothing is shared with a plugin). False when the
    /// field can't be set.</summary>
    static bool SetField(IMajorRecord rec, string field, IMajorRecord src)
    {
        if (field == "MajorRecordFlagsRaw")
        {
            rec.MajorRecordFlagsRaw = (rec.MajorRecordFlagsRaw & CompressedFlag) | (src.MajorRecordFlagsRaw & ~(CompressedFlag | PartialFlag));
            return true;
        }
        var p = rec.GetType().GetProperty(field);
        var sp = src.GetType().GetProperty(field);
        if (p is null || sp is null) return false;
        var v = sp.GetValue(src);
        if (p.SetMethod is { IsPublic: true }) { p.SetValue(rec, v); return true; }
        if (p.GetValue(rec) is IList target)
        {
            target.Clear();
            if (v is IEnumerable items) foreach (var x in items) target.Add(x);
            return true;
        }
        return false;
    }

    static void ClearChildRecords(IMajorRecord rec)
    {
        foreach (var p in rec.GetType().GetProperties())
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            var t = p.PropertyType;
            var elem = t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t) ? t.GetGenericArguments().LastOrDefault() : null;
            if (elem is not null && typeof(IMajorRecordGetter).IsAssignableFrom(elem) && p.GetValue(rec) is IList list) list.Clear();
        }
    }

    // ---------- writing ----------

    static List<string> WritePatch(Plan plan, LoadOrderSetup lo, string dataDir, string? extraDir, string outDir, string name)
    {
        var patch = Plugin.New(name, PluginStyle.Light, "sf-mod-check patch", "Local conflict patch for one load order. For your own game only: it holds other authors' data.");
        if (name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) patch.ModHeader.Flags &= ~StarfieldModHeader.HeaderFlag.Master;
        // Worldspaces go in before the cells: an exterior cell is written inside its worldspace's block (0.13.0).
        foreach (var r in plan.Records.Where(r => r.Record is not Cell and not IPlaced and not NavigationMesh))
        {
            var group = ((IMod)patch).TryGetTopLevelGroup(r.Record.GetType())
                ?? throw new InvalidOperationException($"no top-level group for {r.RecordType}");
            group.AddUntyped(r.Record);
        }
        foreach (var r in plan.Records.Where(r => r.Record is Cell))
            Place(patch, (Cell)r.Record, plan.Places[r.FormKey]);
        // and each placed object into its cell's persistent or temporary list, each navmesh into its cell's navmeshes
        // (the cell is in the patch by now)
        var cellsIn = plan.Records.Where(r => r.Record is Cell).ToDictionary(r => r.FormKey, r => (Cell)r.Record);
        foreach (var r in plan.Records.Where(r => r.Record is IPlaced or NavigationMesh))
        {
            var at = PlaceOf(plan, r);
            var cell = cellsIn.GetValueOrDefault(at.Cell)
                ?? throw new InvalidOperationException($"{r.RecordType} {r.FormKey} sits in cell {at.Cell}, which isn't in the patch");
            if (r.Record is NavigationMesh n) cell.NavigationMeshes.Add(n);
            else (at.Persistent ? cell.Persistent : cell.Temporary).Add((IPlaced)r.Record);
        }
        // Masters: every plugin that has a version of a patched record (so the patch loads after all of them), and
        // every plugin a copied value points into, in load order.
        var index = lo.Plugins.ToDictionary(p => p.FileName, p => p.Index, StringComparer.OrdinalIgnoreCase);
        var masterSet = new HashSet<string>(plan.Records.SelectMany(r => r.Plugins), StringComparer.OrdinalIgnoreCase);
        foreach (var r in plan.Records) masterSet.Add(r.FormKey.ModKey.FileName);
        foreach (var l in patch.EnumerateFormLinks()) if (!l.IsNull) masterSet.Add(l.FormKey.ModKey.FileName);
        var missing = masterSet.Where(m => !index.ContainsKey(m)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"the patch would need masters that aren't loaded: {string.Join(", ", missing.Select(m => SafeText.OneLine(m)))}");
        var masters = masterSet.OrderBy(m => index[m]).Select(m => lo.Plugins[index[m]].FileName).ToList();
        // A BlueprintShips file can become a master (a copied value points at a record it defines, or it holds a
        // version of a patched record). That file still loads after the patch, as after every plugin, so say so in
        // the report (0.12.0 code review, rounds 1 and 2).
        foreach (var m in masters.Where(LoadOrderSetup.IsBlueprint))
        {
            var why = new List<string>();
            if (plan.Records.Any(r => r.Plugins.Contains(m, StringComparer.OrdinalIgnoreCase) || r.FormKey.ModKey.FileName.String.Equals(m, StringComparison.OrdinalIgnoreCase)))
                why.Add("it has a version of a patched record");
            if (patch.EnumerateFormLinks().Any(l => !l.IsNull && l.FormKey.ModKey.FileName.String.Equals(m, StringComparison.OrdinalIgnoreCase)))
                why.Add("a value the patch copies points at a record that file defines");
            plan.Notes.Add($"the patch has the master {m}, because {string.Join(" and ", why)}; " +
                           "the game loads BlueprintShips files after every plugin, so the patch loads before this master, " +
                           "as a mod with such a master does");
            Log.Write($"patch: master {m} is a BlueprintShips file, which loads after the patch");
        }

        using var game = new GameData(dataDir, extraDir != null ? [extraDir] : null);
        foreach (var m in masters) game.Open(m);
        // PluginWriter reports on standard output; that goes to the log, so the RESULT line stays the program's own.
        var before = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            PluginWriter.Write(patch, outDir, game, new WriteOptions
            {
                Masters = masters.Select(m => ModKey.FromFileName(m)).ToList(),
                GapCheck = false,   // the gap check is a separate script; sf-mod-check check covers the plugin instead
            });
        }
        finally
        {
            Console.SetOut(before);
            foreach (var line in captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Write("writer: " + line.TrimEnd());
        }
        return masters;
    }

    /// <summary>A component's kind (its class, the same for a plugin's copy and an in-memory one), or null for a kind
    /// Mutagen reads only as unknown data.</summary>
    static string? ComponentKind(IAComponentGetter c)
    {
        var n = c.GetType().Name;
        if (n.EndsWith("BinaryOverlay", StringComparison.Ordinal)) n = n[..^"BinaryOverlay".Length];
        return n.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ? null : n;
    }

    /// <summary>Puts a patched cell where the plugin it comes from has it: an interior block and sub-block, a
    /// worldspace's persistent cell, or an exterior block and sub-block of its worldspace (already in the patch).</summary>
    static void Place(StarfieldMod patch, Cell cell, CellPlace at)
    {
        if (at.World is not { } wk)
        {
            var block = patch.Cells.FirstOrDefault(b => b.BlockNumber == at.Block);
            if (block is null) patch.Cells.Add(block = new CellBlock { BlockNumber = at.Block, GroupType = GroupTypeEnum.InteriorCellBlock });
            var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == at.SubBlock);
            if (sub is null) block.SubBlocks.Add(sub = new CellSubBlock { BlockNumber = at.SubBlock, GroupType = GroupTypeEnum.InteriorCellSubBlock });
            sub.Cells.Add(cell);
            return;
        }
        var world = patch.Worldspaces.FirstOrDefault(w => w.FormKey == wk)
            ?? throw new InvalidOperationException($"cell {cell.FormKey} sits in worldspace {wk}, which isn't in the patch");
        if (at.Persistent) { world.TopCell = cell; return; }
        var wb = world.SubCells.FirstOrDefault(b => b.BlockNumberX == at.X && b.BlockNumberY == at.Y);
        if (wb is null) world.SubCells.Add(wb = new WorldspaceBlock { BlockNumberX = at.X, BlockNumberY = at.Y, GroupType = GroupTypeEnum.ExteriorCellBlock });
        var ws = wb.Items.FirstOrDefault(s => s.BlockNumberX == at.SubX && s.BlockNumberY == at.SubY);
        if (ws is null) wb.Items.Add(ws = new WorldspaceSubBlock { BlockNumberX = at.SubX, BlockNumberY = at.SubY, GroupType = GroupTypeEnum.ExteriorCellSubBlock });
        ws.Items.Add(cell);
    }

    /// <summary>Opens the written patch and compares every field of every record with what was meant. Returns one line
    /// per difference.</summary>
    static List<string> ReadBack(string path, Plan plan, LoadOrderSetup lo)
    {
        var diffs = new List<string>();
        using var written = StarfieldMod.CreateFromBinaryOverlay(path, StarfieldRelease.Starfield,
            new BinaryReadParameters { MasterFlagsLookup = new LoadOrder<IModMasterStyledGetter>(lo.Plugins.Select(p => (IModMasterStyledGetter)p.Mod)) });
        var got = written.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
        foreach (var r in plan.Records)
        {
            if (!got.TryGetValue(r.FormKey, out var w)) { diffs.Add($"{r.RecordType} {r.EditorID} [{r.FormKey}] is missing from the written patch"); continue; }
            foreach (var p in FieldCompare.Fields(w.GetType()))
            {
                // a merged list: the written entries, in order, against the entries collected from the plugins
                if (r.Merged.TryGetValue(p.Name, out var want))
                {
                    var have = FieldCompare.Get(p, w) is IEnumerable e ? e.Cast<object?>().ToList() : new();
                    if (have.Count != want.Count || have.Zip(want).Any(x => !FieldCompare.DeepEq(x.First, x.Second, 0)))
                        diffs.Add($"{r.RecordType} {r.EditorID} [{r.FormKey}]: merged list {p.Name} reads back differently ({have.Count} entries, {want.Count} meant)");
                    continue;
                }
                // the written value against the plugin version it was copied from (both read from files)
                if (!r.Sources.TryGetValue(p.Name, out var src)) continue;   // a reader's bookkeeping, not a stored field (see BuildRecord)
                // A quest's Unknown is read from the header of its dialogue group, which the patch never writes (child
                // records aren't built), so it always reads back 0 (0.9.0 clean-folder test, 2026-11-20). Nothing is lost:
                // with no dialogue group in the patch there is no header to hold it, and the quest's dialogue (and its
                // group) still comes from the plugins that have one. Mutagen 0.54.4 writes 0 there for every quest it
                // writes, so the in-game-proven mergepatch patches did the same. Skipped only in that case: a written
                // quest with no dialogue and Unknown 0; any other mismatch is still reported.
                if (w is IQuestGetter wq && p.Name == nameof(IQuestGetter.Unknown) && wq.Unknown == 0 && wq.DialogTopics.Count == 0) continue;
                var sp = src.GetType() == w.GetType() ? p : src.GetType().GetProperty(p.Name);
                if (sp is null || !FieldCompare.DeepEq(FieldCompare.Get(p, w), FieldCompare.Get(sp, src), 0))
                    diffs.Add($"{r.RecordType} {r.EditorID} [{r.FormKey}]: field {p.Name} reads back differently from the plugin it was copied from");
            }
        }
        if (got.Count != plan.Records.Count) diffs.Add($"the written patch holds {got.Count} records, not {plan.Records.Count}");
        return diffs;
    }

    // ---------- the report ----------

    // Plugin text (EditorIDs, field values, plugin file names) in patch-report.md: one line, then the same escaping as
    // conflict's report.md (Report.Md in ConflictReport.cs: \ | < > [ ] ` * _), so a mod's text can't make a link, an image or HTML
    // when the report is opened in a Markdown viewer (/security-review 0.9.0).
    static string Md(string? s) => SfModCheck.Conflict.Report.Md(SafeText.OneLine(s ?? ""));
    static string Md(FormKey k) => Md(k.ToString());

    static void WriteReport(string path, Plan plan, LoadOrderSetup lo, string name, List<string>? masters, List<string>? mismatches, string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Conflict patch: {name}");
        sb.AppendLine();
        sb.AppendLine($"Made by sf-mod-check {version} patch on {DateTime.Now:yyyy-MM-dd HH:mm}, for this load order only ({lo.OrderSource}).");
        sb.AppendLine("For your own game only: the patch holds other authors' data, so don't publish it.");
        sb.AppendLine();
        if (mismatches is { Count: > 0 })
        {
            sb.AppendLine("## Don't use this patch");
            sb.AppendLine();
            sb.AppendLine("Reading the written plugin back found differences from what was built. Run again with --log and report the log.");
            foreach (var m in mismatches) sb.AppendLine($"- {Md(m)}");
            sb.AppendLine();
        }
        if (plan.Records.Count == 0)
        {
            sb.AppendLine("Nothing to patch: no lost change could be brought back by a patch built here.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Next steps");
            sb.AppendLine();
            sb.AppendLine($"1. Install `{name}` from this folder with your mod manager (zip it, or add it as a mod), and put it at the end of the load order.");
            sb.AppendLine("2. Check it: `sf-mod-check check " + name + "` (with --data if your game isn't Steam's), then run `sf-mod-check conflict` again: these lost changes should be gone.");
            sb.AppendLine("   Before installing, `sf-mod-check conflict --plugins Plugins-with-patch.txt --extra-data <this folder>` does the same check.");
            if (plan.IncludeOfficial)
                sb.AppendLine("   Made with --include-official: it also restores changes one official plugin loses to another. `conflict` never lists those, so it can't show them restored.");
            sb.AppendLine("3. Try the affected places in game. Build the patch again whenever you add, remove or update a mod it lists as a master.");
            sb.AppendLine();
            sb.AppendLine($"Masters ({masters?.Count ?? 0}, in load order): {string.Join(", ", (masters ?? new()).Select(m => Md(m)))}");
            sb.AppendLine();
            sb.AppendLine($"## Records in the patch ({plan.Records.Count})");
            sb.AppendLine();
            foreach (var r in plan.Records)
            {
                sb.AppendLine($"- {Md(r.RecordType)} {Md(r.EditorID)} [{Md(r.FormKey)}]");
                if (r.Parent)
                {
                    var kids = r.Record is ICellGetter
                        ? plan.Records.Where(o => IsCellChild(o.Record) && PlaceOf(plan, o).Cell == r.FormKey).ToList()
                        : new();
                    var what = r.Record is not ICellGetter ? "cells"
                        : kids.Any(k => k.Record is INavigationMeshGetter) && kids.Any(k => k.Record is not INavigationMeshGetter) ? "placed objects and navmeshes"
                        : kids.Any(k => k.Record is INavigationMeshGetter) ? "navmeshes" : "placed objects";
                    sb.AppendLine($"  - the parent of patched {what}, as the game uses it today (no field changed)");
                }
                foreach (var f in r.Fields) sb.AppendLine($"  - {Md(f.Field)} from {Md(f.From)}{(f.Text.Length > 0 ? $": {Md(f.Text)}" : "")}");
            }
            sb.AppendLine();
        }
        var kept = plan.Choices.Count(c => !c.Picked && !c.Forward);
        var later = plan.Choices.Count(c => !c.Picked && c.Forward);
        var mergedLists = plan.Choices.Count(c => c.Merged);
        if (plan.Choices.Count > 0)
        {
            sb.AppendLine($"## Choices ({plan.Choices.Count}; {kept} kept the winner's value{(later > 0 ? $", {later} used the later of two losing plugins' values" : "")}{(mergedLists > 0 ? $", {mergedLists} lists merged entry by entry" : "")})");
            sb.AppendLine();
            sb.AppendLine("Both plugins changed these fields, so there is no safe automatic answer. To use the other plugin's value, run the same command again with the --pick line shown.");
            if (mergedLists > 0)
                sb.AppendLine("A list marked \"merged entry by entry\" keeps both plugins' entries and has no --pick line; to use one plugin's whole list instead, add `--pick <FormKey>:<Field>=<plugin>`.");
            sb.AppendLine();
            foreach (var c in plan.Choices)
            {
                sb.AppendLine($"- {Md(c.RecordType)} {Md(c.EditorID)} [{Md(c.FormKey)}], {Md(c.Field)}: {Md(c.Text)}");
                // the ready-to-copy option sits in a code span, which shows its text as is (no escapes to strip)
                if (!c.Picked) sb.AppendLine($"  - to use {Md(c.Other)}'s: {SfModCheck.Conflict.Report.Code(SafeText.OneLine($"--pick {c.FormKey}:{c.Field}={c.Other}"))}");
            }
            sb.AppendLine();
        }
        if (plan.NotBuilt.Count > 0)
        {
            // 0.12.0: records lost to a BlueprintShips file can't be done in xEdit either; their reason says so
            sb.AppendLine(plan.NotBuilt.All(n => n.Blueprint)
                ? $"## Not built ({plan.NotBuilt.Count}): no patch can restore these"
                : $"## Not built ({plan.NotBuilt.Count}): do these by hand in xEdit, except where the reason says otherwise");
            sb.AppendLine();
            foreach (var n in plan.NotBuilt)
                sb.AppendLine($"- {Md(n.RecordType)} {Md(n.EditorID)} [{Md(n.FormKey)}]{(n.Fields.Count > 0 ? $" ({string.Join(", ", n.Fields.Select(f => Md(f)))})" : "")}: {Md(n.Why)}");
            sb.AppendLine();
        }
        if (plan.Notes.Count > 0)
        {
            sb.AppendLine("## Notes");
            sb.AppendLine();
            foreach (var n in plan.Notes) sb.AppendLine($"- {Md(n)}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}
