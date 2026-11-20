// sf-mod-check patch (0.9.0): builds a local conflict patch for a load order, with no AI and no xEdit.
// It runs the same analysis as `conflict`, then writes one light plugin that loads last and, for every record with a
// lost change:
//   - starts from the record as the game uses it today (each field from the last plugin that supplies it, so partial
//     forms are filled in), written as a full record;
//   - copies every Forwardable field forward from the plugin that lost it (the winner didn't change that field, so
//     nothing the winner meant is undone);
//   - for Choice fields (both plugins changed it) keeps the winner's value unless --pick names another plugin.
// Records that live inside cells, worldspaces, quests or dialogue topics (placed objects, navmeshes, cells, dialogue)
// are not built in this version: the report lists them for xEdit. Nor is the navmesh info map (NAVI), whose copies
// each list only one plugin's navmeshes. Records that end up equal to today's winner are
// left out (no ITMs). It reads plugins and writes only its output folder: never the Data folder, never Plugins.txt.
//   sf-mod-check patch [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]
//                      [--name <Patch.esm>] [--only <a.esm>[,<b.esm>...]]... [--pick <FormKey>[:<Field>]=<plugin>]...
//                      [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]
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
        "                          [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]";

    public const string DefaultName = "ConflictPatch.esm";
    const int PartialFlag = FieldCompare.PartialFormFlag;
    const int CompressedFlag = 0x40000;

    static string GamePluginsTxt() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starfield", "Plugins.txt");

    /// <summary>A --pick: use this plugin's value for one field of a record, or for all its Choice fields (Field null).</summary>
    public record Pick(FormKey FormKey, string? Field, string Plugin)
    {
        public bool Used { get; set; }
    }

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

    // Record kinds that live inside another record's group. Building them needs the parent record too (a cell's or a
    // worldspace's header), which can undo other mods' changes to the parent, so this version leaves them to xEdit.
    static readonly Type[] NotBuilt =
    [
        typeof(ICellGetter), typeof(IWorldspaceGetter), typeof(IPlacedGetter), typeof(INavigationMeshGetter),
        typeof(IDialogTopicGetter), typeof(IDialogResponsesGetter), typeof(ISceneGetter),
        typeof(IDialogBranchGetter),
    ];

    /// <summary>Why a record type can't be built, or null when it can.</summary>
    public static string? WhyNotBuilt(Type getter)
    {
        // The navmesh info map (NAVI): each plugin's copy lists only the navmeshes that plugin adds or changes (on
        // Zacha-Test, 2026-11-20: Starfield.esm 56,631 entries, sfta03.esm 515, POIR-Test.esm 16), so it is not a record
        // one plugin wins whole. A merged copy built here came out with fewer navmeshes than Starfield.esm's own.
        if (typeof(INavigationMeshInfoMapGetter).IsAssignableFrom(getter))
            return "the navmesh info map isn't patched: each plugin's copy lists only its own navmeshes, so a merged copy would hide entries; " +
                   "don't copy it by hand either (the Creation Kit rebuilds it when a navmesh is finalized)";
        var hit = NotBuilt.FirstOrDefault(t => t.IsAssignableFrom(getter));
        if (hit is null) return null;
        return hit == typeof(ICellGetter) || hit == typeof(IWorldspaceGetter)
            ? "cells and worldspaces are not built by this version (their placed objects and blocks would come along); copy the fields in xEdit"
            : "records that live inside a cell, worldspace, quest or dialogue topic are not built by this version; copy the fields in xEdit";
    }

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
                return $"RESULT: CANNOT BUILD ({e.GetType().Name}{rec}{where}: {Impact.OneLine(e.Message.Split('\n')[0])})";
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
        bool quiet = false, verbose = false;
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
                                                $"or --pick 064F3C:Starfield.esm:Value=ModA.esm (the patch report prints ready-made lines), not \"{Impact.OneLine(pk)}\"");
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
                case "--quiet": quiet = true; break;
                case "-h": case "--help":
                    Console.Error.WriteLine(Usage);
                    return 2;
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

        // The patch's name: a plain plugin file name the game keeps in Plugins.txt.
        if (!GameData.IsPlainPluginName(name) || !name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
        {
            cantWhy = "bad --name";
            Console.Error.WriteLine($"--name needs a plain plugin file name ending in .esm or .esp, with no folder, for example --name MyPatch.esm (not \"{Impact.OneLine(name)}\")");
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
                    cantWhy = $"--only {Impact.OneLine(o)} is not in the load order";
                    Console.Error.WriteLine($"--only {Impact.OneLine(o)}: that plugin isn't active in {orderTxt} (or it couldn't be loaded; see the load-order problems with --verbose). " +
                                            "Give the file names of active plugins, separated by commas.");
                    return 2;
                }
                onlySet.Add(op.FileName);
            }
            foreach (var pk in picks.Where(pk => !byName.ContainsKey(pk.Plugin)))
            {
                cantWhy = $"--pick names {Impact.OneLine(pk.Plugin)}, which is not in the load order";
                Console.Error.WriteLine($"--pick {pk.FormKey}{(pk.Field is null ? "" : ":" + pk.Field)}={Impact.OneLine(pk.Plugin)}: that plugin isn't active in the load order.");
                return 2;
            }

            Analysis analysis;
            try { analysis = Analyzer.Run(lo.Plugins, onlySet.Count > 0 ? onlySet : null); }
            catch (MemoryLimitException e)
            {
                resultPrinted = true;
                Console.WriteLine($"RESULT: STOPPED ({e.Message}; no patch written)");
                Console.Error.WriteLine("Try a smaller load order, use --only, or raise --max-memory if this computer has the memory.");
                return 3;
            }
            Analyzer.Where = "working out the patch";
            var plan = Build(lo, analysis, onlySet, picks);
            foreach (var pk in picks.Where(pk => !pk.Used))
                Console.Error.WriteLine($"WARNING: --pick {pk.FormKey}{(pk.Field is null ? "" : ":" + pk.Field)}={pk.Plugin} was not used: that record has no Choice field " +
                                        $"{(pk.Field is null ? "" : pk.Field + " ")}that {pk.Plugin} supplies, or the record isn't patched");

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
    public sealed record NotBuiltRecord(FormKey FormKey, string RecordType, string? EditorID, string Why, List<string> Fields);
    /// <param name="Sources">for each field copied whole, the plugin's version of the record its value came from</param>
    /// <param name="Merged">for each list field merged entry by entry, its entries as the plugins hold them</param>
    public sealed record PatchRecord(FormKey FormKey, string RecordType, string? EditorID, IMajorRecord Record, List<ForwardedField> Fields, List<string> Plugins,
        Dictionary<string, IMajorRecordGetter> Sources, Dictionary<string, List<object?>> Merged);

    public sealed class Plan
    {
        public List<PatchRecord> Records { get; } = new();
        public List<ChoiceField> Choices { get; } = new();
        public List<NotBuiltRecord> NotBuilt { get; } = new();
        public List<string> Notes { get; } = new();
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
        // only records with a lost change are patched: a --pick on any other record is reported as unused
        var byRecord = relevant.GroupBy(l => l.FormKey).ToList();
        var targets = new Dictionary<FormKey, (Type Getter, string ClassName)>();
        foreach (var g in byRecord)
        {
            var cls = g.First().RecordType;
            var getter = GetterOf(cls);
            var fwdFields = g.SelectMany(l => l.Fields.Where(f => !Impact.IsMinor(f)).Select(f => f.Field)).Distinct().ToList();
            if (getter is null)
            {
                plan.NotBuilt.Add(new(g.Key, cls, g.First().EditorID, $"sf-mod-check doesn't know the record type {cls}", fwdFields));
                continue;
            }
            if (WhyNotBuilt(getter) is { } why)
            {
                plan.NotBuilt.Add(new(g.Key, cls, g.First().EditorID, why, fwdFields));
                continue;
            }
            targets[g.Key] = (getter, cls);
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

        foreach (var g in byRecord)
        {
            if (!targets.ContainsKey(g.Key)) continue;
            if (!versions.TryGetValue(g.Key, out var vs) || vs.Count == 0)
            {
                plan.NotBuilt.Add(new(g.Key, g.First().RecordType, g.First().EditorID, "its versions could not be read again", new()));
                continue;
            }
            try { BuildRecord(g.Key, g.ToList(), vs, picks, plan, MastersOf, Name); }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                plan.NotBuilt.Add(new(g.Key, g.First().RecordType, g.First().EditorID, $"it could not be built ({e.GetType().Name}: {Impact.OneLine(e.Message)})", new()));
                Log.Write($"patch: {g.Key} not built: {e}");
            }
        }
        return plan;
    }

    // A deep copy per version, made only when one of its fields is needed.
    sealed class Copies(List<(PluginInfo Plugin, IMajorRecordGetter Rec)> versions)
    {
        readonly Dictionary<int, IMajorRecord> made = new();
        public IMajorRecord Of(int i) => made.TryGetValue(i, out var c) ? c : made[i] = (IMajorRecord)versions[i].Rec.DeepCopy();
    }

    static void BuildRecord(FormKey fk, List<LostChange> lost, List<(PluginInfo Plugin, IMajorRecordGetter Rec)> vs, List<Pick> picks, Plan plan,
        Func<PluginInfo, HashSet<string>> mastersOf, Func<IFormLinkGetter, string> name)
    {
        var copies = new Copies(vs);
        // Start from the last full version, then lay each later (partial) version's own fields over it: the record as
        // the game uses it today.
        int baseIdx = vs.FindLastIndex(v => !FieldCompare.IsPartial(v.Rec));
        if (baseIdx < 0) baseIdx = 0;
        var rec = (IMajorRecord)vs[baseIdx].Rec.DeepCopy();
        // Only fields the record really stores: a reader's own bookkeeping (QuestBinaryOverlay.QuestSubGroupTriggerSpecs,
        // where in the file a group starts) has no counterpart in the record and is never copied.
        var getterFields = FieldCompare.Fields(vs[^1].Rec.GetType())
            .Where(p => p.Name == "MajorRecordFlagsRaw" || rec.GetType().GetProperty(p.Name) is not null).ToArray();
        var byName = getterFields.ToDictionary(p => p.Name);
        // Where each field's value comes from (an index into vs). Values are compared as the plugins hold them, the way
        // conflict compares them, never as Mutagen's in-memory copies (an empty list and no list read back the same).
        var source = getterFields.ToDictionary(p => p.Name, _ => baseIdx);
        for (int j = baseIdx + 1; j < vs.Count; j++)
            foreach (var p in getterFields)
                if (FieldCompare.Provides(vs[j].Rec, p, out _))
                {
                    if (!SetField(rec, p.Name, copies.Of(j))) throw new InvalidOperationException($"field {p.Name} can't be set");
                    source[p.Name] = j;
                }
        rec.MajorRecordFlagsRaw &= ~PartialFlag;
        var today = new Dictionary<string, int>(source);
        object? Held(string field, int i) => FieldCompare.Get(byName[field], vs[i].Rec);

        int IndexOf(string plugin) => vs.FindIndex(v => v.Plugin.FileName.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        var forwarded = new List<ForwardedField>();
        var setBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = lost[0];
        // List fields are merged entry by entry (the rule of the author's in-game-proven merge builder, 2026-11-06):
        // each plugin's own additions are added and its own removals removed, against the version it was built on, so
        // two plugins that both add entries (a DLC and a mod adding references to one location) both keep them.
        // A cell's traversals are one value and are never merged (they don't reach the patch anyway: cells aren't built).
        var merged = new Dictionary<string, List<(object? Held, object? Mut, int Origin)>>(StringComparer.Ordinal);
        bool Mergeable(string field) => field != "Traversals" && rec.GetType().GetProperty(field) is { } mp
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
        static string? Identity(object? x) => x is IActorReactionGetter ar ? "Actor:" + ar.Actor.FormKey : null;
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
            if (did.Added == 0 && did.Changed == 0 && did.Removed == 0) return Impact.OneLine($"nothing new over {winner}'s list");
            var parts = new List<string>();
            if (did.Added > 0)
            {
                var first = Impact.Changes(field, new List<object?>(), new List<object?> { did.FirstAdded }, name).FirstOrDefault()?.Text ?? "";
                first = first.StartsWith("adds ") ? first[5..] : first;
                parts.Add($"adds {did.Added} {(did.Added == 1 ? "entry" : "entries")}{(first.Length > 0 ? $" (first: {first})" : "")}");
            }
            if (did.Changed > 0) parts.Add($"changes {did.Changed} {(did.Changed == 1 ? "entry" : "entries")}");
            if (did.Removed > 0) parts.Add($"removes {did.Removed} {(did.Removed == 1 ? "entry" : "entries")}");
            return Impact.OneLine(string.Join(" and ", parts));
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
                forwarded.Add(new(f.Field, l.Loser, Impact.OneLine(change)));
            }
        }
        // A --pick on a field two losers both changed: the named plugin's whole value is used instead of the later one's.
        foreach (var field in plan.Choices.Where(c => c.FormKey == fk && c.Forward && !c.Picked).Select(c => c.Field).Distinct().ToList())
        {
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
                var pick = picks.Where(pk => pk.FormKey == fk && (pk.Field is null || pk.Field == f.Field))
                    .Where(pk => IndexOf(pk.Plugin) is var pi && pi >= 0 && FieldCompare.Provides(vs[pi].Rec, field, out _))
                    .OrderBy(pk => pk.Field is null ? 0 : 1).LastOrDefault();
                var change = Impact.OneLine(f.LoserChanges.Where(d => !d.Minor).Select(d => d.Text).FirstOrDefault() ?? "");
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
            return;
        }
        // A quest's dialogue and scenes are records of their own: only the quest record itself goes in.
        ClearChildRecords(rec);
        var plugins = vs.Select(v => v.Plugin.FileName).ToList();
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
        foreach (var r in plan.Records)
        {
            var group = ((IMod)patch).TryGetTopLevelGroup(r.Record.GetType())
                ?? throw new InvalidOperationException($"no top-level group for {r.RecordType}");
            group.AddUntyped(r.Record);
        }
        // Masters: every plugin that has a version of a patched record (so the patch loads after all of them), and
        // every plugin a copied value points into, in load order.
        var index = lo.Plugins.ToDictionary(p => p.FileName, p => p.Index, StringComparer.OrdinalIgnoreCase);
        var masterSet = new HashSet<string>(plan.Records.SelectMany(r => r.Plugins), StringComparer.OrdinalIgnoreCase);
        foreach (var r in plan.Records) masterSet.Add(r.FormKey.ModKey.FileName);
        foreach (var l in patch.EnumerateFormLinks()) if (!l.IsNull) masterSet.Add(l.FormKey.ModKey.FileName);
        var missing = masterSet.Where(m => !index.ContainsKey(m)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"the patch would need masters that aren't loaded: {string.Join(", ", missing.Select(Impact.OneLine))}");
        var masters = masterSet.OrderBy(m => index[m]).Select(m => lo.Plugins[index[m]].FileName).ToList();

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
                var sp = src.GetType() == w.GetType() ? p : src.GetType().GetProperty(p.Name);
                if (sp is null || !FieldCompare.DeepEq(FieldCompare.Get(p, w), FieldCompare.Get(sp, src), 0))
                    diffs.Add($"{r.RecordType} {r.EditorID} [{r.FormKey}]: field {p.Name} reads back differently from the plugin it was copied from");
            }
        }
        if (got.Count != plan.Records.Count) diffs.Add($"the written patch holds {got.Count} records, not {plan.Records.Count}");
        return diffs;
    }

    // ---------- the report ----------

    static string Md(string? s) => Impact.OneLine(s ?? "").Replace("|", "/").Replace("`", "'").Replace("<", "&lt;");

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
            sb.AppendLine("3. Try the affected places in game. Build the patch again whenever you add, remove or update a mod it lists as a master.");
            sb.AppendLine();
            sb.AppendLine($"Masters ({masters?.Count ?? 0}, in load order): {string.Join(", ", (masters ?? new()).Select(Md))}");
            sb.AppendLine();
            sb.AppendLine($"## Records in the patch ({plan.Records.Count})");
            sb.AppendLine();
            foreach (var r in plan.Records)
            {
                sb.AppendLine($"- {Md(r.RecordType)} {Md(r.EditorID)} [{r.FormKey}]");
                foreach (var f in r.Fields) sb.AppendLine($"  - {Md(f.Field)} from {Md(f.From)}{(f.Text.Length > 0 ? $": {Md(f.Text)}" : "")}");
            }
            sb.AppendLine();
        }
        var kept = plan.Choices.Count(c => !c.Picked && !c.Forward);
        var later = plan.Choices.Count(c => !c.Picked && c.Forward);
        if (plan.Choices.Count > 0)
        {
            sb.AppendLine($"## Choices ({plan.Choices.Count}; {kept} kept the winner's value{(later > 0 ? $", {later} used the later of two losing plugins' values" : "")})");
            sb.AppendLine();
            sb.AppendLine("Both plugins changed these fields, so there is no safe automatic answer. To use the other plugin's value, run the same command again with the --pick line shown.");
            sb.AppendLine();
            foreach (var c in plan.Choices)
            {
                sb.AppendLine($"- {Md(c.RecordType)} {Md(c.EditorID)} [{c.FormKey}], {Md(c.Field)}: {Md(c.Text)}");
                if (!c.Picked) sb.AppendLine($"  - to use {Md(c.Other)}'s: `--pick {c.FormKey}:{Md(c.Field)}={Md(c.Other)}`");
            }
            sb.AppendLine();
        }
        if (plan.NotBuilt.Count > 0)
        {
            sb.AppendLine($"## Not built ({plan.NotBuilt.Count}): do these by hand in xEdit");
            sb.AppendLine();
            foreach (var n in plan.NotBuilt)
                sb.AppendLine($"- {Md(n.RecordType)} {Md(n.EditorID)} [{n.FormKey}]{(n.Fields.Count > 0 ? $" ({string.Join(", ", n.Fields.Select(Md))})" : "")}: {Md(n.Why)}");
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
