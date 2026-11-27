// sf-mod-check cell (0.27.0): what is in a cell or near a point, who added and changed each placed reference, and the
// persistent references a quest alias or a MoveTo can use. Read only.
//   sf-mod-check cell <cell EditorID | FormKey | FormID> [load-order options] [--max <n>] [--json] [--quiet] [--verbose] [--log <file>]
//   sf-mod-check cell --ref <reference EditorID | FormKey | FormID> [...]
//   sf-mod-check cell --near <worldspace EditorID | FormKey | FormID> <x> <y> <radius> [...]
// The walk (PatchCommand.AllCellsWithWorld) reads, in each plugin, the cells wanted and every placed reference in their
// persistent and temporary lists; a second walk reads the other cells of the plugins that may override those references,
// so a reference a later plugin moved shows in its new cell. Each reference's fields come from its winning copy.
// Exit 0 = the cell (or point) was found and listed, 2 = could not run (bad arguments, not found, wrong type).
// Design: the 0.27.0 design notes, part A (cell).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Records.Mapping;
using Mutagen.Bethesda.Starfield;
using Noggog;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class CellCommand
{
    public const string Usage =
        "usage: sf-mod-check cell <cell EditorID | FormKey | FormID> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]\n" +
        "                         [--data <Data folder>] [--extra-data <folder or archive>] [--max <n>] [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "       sf-mod-check cell --ref <reference EditorID | FormKey | FormID> [...]\n" +
        "       sf-mod-check cell --near <worldspace EditorID | FormKey | FormID> <x> <y> <radius> [...]\n" +
        "       (instead of --plugins or --mo2: --plugin <plugin file or archive> [--masters-dir <folder>]...)\n" +
        "  Lists what a cell holds: every placed reference (its base, position, persistent and initially disabled marks,\n" +
        "  enable parent and location reference types), the plugin that added it and the plugins that changed it, the\n" +
        "  references a later plugin moved out or deleted, counts by base type and by enable parent, and the persistent\n" +
        "  references (\"anchors\") a quest alias or a MoveTo can use. Each reference shows its winning copy.\n" +
        "  <cell>    the cell's EditorID (letter case ignored), FormKey (0008CF:ShatteredSpace.esm) or console FormID\n" +
        "  --ref     list the cell that holds this placed reference (where its winning copy sits), the reference marked\n" +
        "  --near    list the references within <radius> of the point (<x>, <y>) in the worldspace, nearest first, from\n" +
        "            its persistent cell and every exterior cell. Distance is flat (X and Y; height is ignored) in the\n" +
        "            game's units, and a reference exactly at the radius counts\n" +
        "  --plugin  read only this plugin (or the plugins of a .zip, .7z or .rar) and its masters, with no Plugins.txt;\n" +
        "            give the cell as an EditorID or FormKey there (a console FormID needs the whole load order)\n" +
        "  --max     list at most this many references, and at most this many anchors (default 100; 0 lists them all);\n" +
        "            the counts always cover every reference\n" +
        "Exit 0 = the cell (or point) was found and listed, 2 = could not run (bad arguments, not found, wrong type).";

    public const int DefaultMax = 100;
    /// <summary>The enable parents the text lists at most (largest first); --json lists them all.</summary>
    public const int MaxParentsText = 20;
    const int PersistentFlag = 0x400, DisabledFlag = 0x800;

    /// <summary>One plugin's copy of a placed reference, and the cell it sits in there.</summary>
    internal sealed record Copy(PluginInfo Plugin, IPlacedGetter Rec, FormKey Cell, bool InPersistentList);

    /// <summary>A placed reference and its copies in load order.</summary>
    internal sealed class Ref
    {
        public required FormKey Key;
        public List<Copy> Copies = new();
        public Copy Winner => Copies[^1];
    }

    /// <summary>What the walk found: the references, the wanted cells' own copies, navmeshes per cell, cell names.</summary>
    internal sealed class Walk
    {
        public Dictionary<FormKey, Ref> Refs = new();
        public List<(PluginInfo Plugin, ICellGetter Cell, IWorldspaceGetter? World, bool Top)> CellCopies = new();
        public Dictionary<FormKey, HashSet<FormKey>> Navmeshes = new();
        public Dictionary<FormKey, string> CellNames = new();
        public HashSet<FormKey> WantedCells = new();
        public List<string> Unread = new();
    }

    /// <summary>One listed reference, ready to print.</summary>
    internal sealed class Row
    {
        public required FormKey Key;
        public required string Type;
        public string? EditorId;
        public string? Base, BaseType;
        public P3Float? Position, Rotation;
        public bool Persistent, Flagged, InPersistentList, Disabled, ParentOpposite, Asked, MarkerBase;
        public string? Parent;
        public FormKey? ParentKey;
        public List<string> LocationRefTypes = new();
        public required string AddedBy;
        public bool AddedByLoaded;
        public List<string> ChangedBy = new();
        public required FormKey Cell;
        public double? Distance;
    }

    enum Kind { EditorId, FormKey, FormId }
    enum Mode { Cell, Ref, Near }

    public static int Run(string[] a, string version, string byline)
    {
        bool json = a.Contains("--json");
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) CannotRun(json, version, cantWhy);
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => json
                ? CannotRunJson(version, $"{e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}")
                : $"RESULT: CANNOT CHECK ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static void CannotRun(bool json, string version, string why) =>
        Console.WriteLine(json ? CannotRunJson(version, why) : $"RESULT: CANNOT CHECK ({why})");

    static string CannotRunJson(string version, string why) => new JsonObject
    {
        ["tool"] = "sf-mod-check cell", ["version"] = version, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(Indented);

    static readonly Regex ConsoleId = new(@"^(0x)?[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant);

    /// <summary>A number for --near: invariant culture, finite.</summary>
    static bool Number(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? cellArg = null, refArg = null, nearArg = null, logFile = null;
        double nx = 0, ny = 0, radius = 0;
        bool quiet = false, verbose = false, json = false;
        int max = DefaultMax;
        using var input = new LoadOrderInput(allowPlugin: true, ArchiveInput.Use.PluginsOnly, "cell");
        for (int i = 0; i < a.Length; i++)
        {
            if (input.TryOption(a, ref i, Usage) is { } taken) { if (!taken) return 2; continue; }
            switch (a[i])
            {
                case "--max":
                    if (Cli.Value(a, ref i, "a number of references (0 = all)", Usage) is not { } mx) return 2;
                    if (!int.TryParse(mx, NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    {
                        Console.Error.WriteLine($"--max needs a whole number of references, 0 or more (0 lists them all), not \"{SafeText.OneLine(mx)}\".");
                        cantWhy = "--max needs a whole number";
                        return 2;
                    }
                    break;
                case "--ref":
                    if (refArg != null) { Console.Error.WriteLine("--ref is given twice: cell lists one cell per run."); return 2; }
                    if (Cli.Value(a, ref i, "a placed reference (EditorID, FormKey or console FormID)", Usage, "0001A2B3") is not { } rv) return 2;
                    refArg = rv.Trim(); break;
                case "--near":
                    if (nearArg != null) { Console.Error.WriteLine("--near is given twice: cell lists one point per run."); return 2; }
                    if (Cli.Value(a, ref i, "a worldspace, then x, y and a radius", Usage, "SFBGS001DazraWorld -107.7 -184.2 500") is not { } ws) return 2;
                    nearArg = ws.Trim();
                    if (i + 3 >= a.Length || a.Skip(i + 1).Take(3).Any(v => v.StartsWith("--")))
                    {
                        Console.Error.WriteLine("--near needs four values: the worldspace, x, y and the radius, for example --near SFBGS001DazraWorld -107.7 -184.2 500.");
                        Console.Error.WriteLine(Usage);
                        cantWhy = "--near needs a worldspace, x, y and a radius";
                        return 2;
                    }
                    string xs = a[++i], ys = a[++i], rs = a[++i];
                    if (!Number(xs, out nx) || !Number(ys, out ny))
                    {
                        Console.Error.WriteLine($"--near: x and y must be numbers in the game's units (a dot for decimals, such as -107.7), not \"{SafeText.OneLine(xs)}\" and \"{SafeText.OneLine(ys)}\".");
                        cantWhy = "--near x and y must be numbers";
                        return 2;
                    }
                    if (!Number(rs, out radius) || radius <= 0)
                    {
                        Console.Error.WriteLine($"--near: the radius must be a number above 0 in the game's units (such as 500), not \"{SafeText.OneLine(rs)}\".");
                        cantWhy = "--near needs a radius above 0";
                        return 2;
                    }
                    break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--json": json = true; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine($"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    if (cellArg != null)
                    {
                        Console.Error.WriteLine($"cell lists one cell per run: got {SafeText.OneLine(cellArg)} and {SafeText.OneLine(a[i])}. Run it once for each cell.");
                        return 2;
                    }
                    cellArg = a[i].Trim();
                    break;
            }
        }
        int given = (cellArg != null ? 1 : 0) + (refArg != null ? 1 : 0) + (nearArg != null ? 1 : 0);
        if (given != 1)
        {
            Console.Error.WriteLine(given == 0
                ? "cell needs one of: a cell (EditorID, FormKey or console FormID), --ref <reference> or --near <worldspace> <x> <y> <radius>."
                : "cell takes one of a cell, --ref or --near per run, not two of them.");
            Console.Error.WriteLine(Usage);
            cantWhy = given == 0 ? "no cell, --ref or --near given" : "more than one of a cell, --ref and --near";
            return 2;
        }
        var mode = cellArg != null ? Mode.Cell : refArg != null ? Mode.Ref : Mode.Near;
        var q = (cellArg ?? refArg ?? nearArg)!;
        if (FindCommand.EmptyForcedEditorId(q))
        {
            Console.Error.WriteLine($"{SafeText.OneLine(q)} {FindCommand.EmptyEditorIdText}");
            cantWhy = "an empty EditorID";
            return 2;
        }
        var forcedEid = FindCommand.ForcedEditorId(q);
        if (forcedEid != null) q = forcedEid;
        var kind = forcedEid != null ? Kind.EditorId : ConsoleId.IsMatch(q) ? Kind.FormId : q.Contains(':') ? Kind.FormKey : Kind.EditorId;
        FormKey key = default;
        if (kind == Kind.FormKey && !FormKey.TryFactory(q, out key))
        {
            Console.Error.WriteLine($"{SafeText.OneLine(q)} isn't a FormKey: give six hex digits, a colon and the plugin's file name, for example 0008CF:ShatteredSpace.esm.");
            cantWhy = "not a FormKey";
            return 2;
        }
        if (kind == Kind.FormId && input.Plugin != null)
        {
            Console.Error.WriteLine($"{SafeText.OneLine(q)} is a console FormID, which depends on the game's whole load order, and --plugin loads only the plugin and its masters. " +
                                    "Give the record's FormKey (six hex digits, a colon and the plugin's file name, for example 000800:MyMod.esm) or its EditorID, or leave out --plugin and give --plugins or --mo2.");
            cantWhy = "a console FormID with --plugin";
            return 2;
        }

        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} cell on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Check that this account can read that file or folder, close any program that has a plugin open (xEdit, the Creation Kit), and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        try
        {
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            if (!quiet && !json) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");
            if (input.Plugin != null && FindCommand.Unloaded(lo, input.Plugin) is { } unloadedWhy) { cantWhy = unloadedWhy; return 2; }
            var plugins = lo.Plugins.OrderBy(p => p.Index).ToList();
            var unread = new List<string>();

            // 1. the record asked for, as a FormKey
            string? notFoundWhy = null;
            var what = mode switch { Mode.Cell => "cell", Mode.Ref => "placed reference", _ => "worldspace" };
            if (kind == Kind.FormId)
            {
                var fid = uint.Parse(q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (FindCommand.FromConsoleId(fid, plugins, out notFoundWhy) is { } fk) { key = fk; Log.Write($"console FormID {fid:X8} is {fk}"); }
            }
            else if (kind == Kind.FormKey && !plugins.Any(p => p.FileName.Equals(key.ModKey.FileName, StringComparison.OrdinalIgnoreCase)))
                notFoundWhy = $"{key.ModKey.FileName} isn't in this load order";
            FormKey? asked = null;
            FormKey? found = null;
            if (notFoundWhy == null)
            {
                var keys = kind == Kind.EditorId ? ByEditorId(mode, q, plugins, unread) : new List<FormKey> { key };
                if (keys.Count > 1)
                {
                    Console.Error.WriteLine($"{keys.Count} {what}s have the EditorID {SafeText.OneLine(q)}: {string.Join(", ", keys)}. Give the FormKey of the one you want.");
                    cantWhy = $"{keys.Count} {what}s have that EditorID";
                    return 2;
                }
                if (keys.Count == 1)
                {
                    if (mode == Mode.Ref)
                    {
                        asked = keys[0];
                        found = CellOfRef(keys[0], plugins, unread);
                    }
                    else if (mode == Mode.Near) found = HasWorld(keys[0], plugins, unread) ? keys[0] : null;
                    else found = keys[0];   // checked by the walk below
                }
            }
            Walk? walk = null;
            if (found is { } target && mode != Mode.Near)
            {
                walk = Collect(plugins, (c, w) => c.FormKey == target, unread);
                if (walk.CellCopies.Count == 0) walk = null;
            }
            else if (found is { } world)
                walk = Collect(plugins, (c, w) => w?.FormKey == world, unread);
            if (walk == null)
            {
                foreach (var u in unread) Console.Error.WriteLine($"note: {u}");
                return NotFound(mode, kind, q, key, asked, found, notFoundWhy, plugins, lo, input, json, version, sw, unread, out resultPrinted, out cantWhy);
            }
            foreach (var u in unread) Console.Error.WriteLine($"note: {u}");

            // 2. the rows
            var ids = plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>(LinkCachePreferences.OnlyIdentifiers());
            var bases = new Dictionary<(FormKey, Type), (string Text, string? Sig, string? EditorId)>();
            var loaded = plugins.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = new List<Row>();
            var movedOut = new List<(FormKey Key, FormKey To, string By)>();
            var deleted = new List<(FormKey Key, string By, Row Row)>();
            foreach (var r in walk.Refs.Values)
            {
                var win = r.Winner;
                if (!walk.WantedCells.Contains(win.Cell))
                {
                    if (mode != Mode.Near) movedOut.Add((r.Key, win.Cell, win.Plugin.FileName));
                    continue;
                }
                var row = MakeRow(r, ids, bases, loaded);
                if (mode == Mode.Near)
                {
                    if (row.Position is not { } pos) continue;
                    var d = Math.Sqrt((pos.X - nx) * (pos.X - nx) + (pos.Y - ny) * (pos.Y - ny));
                    if (d > radius) continue;
                    row.Distance = d;
                }
                row.Asked = asked == r.Key;
                if (win.Rec.IsDeleted) deleted.Add((r.Key, win.Plugin.FileName, row));
                else rows.Add(row);
            }
            // enable parents are named from the walk (a placed reference's EditorID), not the link cache, which would
            // index every placed reference of every plugin
            foreach (var row in rows.Concat(deleted.Select(d => d.Row)))
                if (row.ParentKey is { } pk)
                    row.Parent = walk.Refs.TryGetValue(pk, out var pr) && EditorIdOf(pr.Winner.Rec) is { Length: > 0 } pe ? $"{SafeText.OneLine(pe)} [{pk}]" : $"[{pk}]";
            rows = mode == Mode.Near
                ? rows.OrderBy(x => x.Distance).ThenBy(x => x.Key.ToString(), StringComparer.Ordinal).ToList()
                : rows.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToList();
            movedOut = movedOut.OrderBy(m => m.Key.ToString(), StringComparer.Ordinal).ToList();
            deleted = deleted.OrderBy(d => d.Key.ToString(), StringComparer.Ordinal).ToList();
            var anchors = rows.Where(x => x.Persistent)
                .OrderBy(x => x.LocationRefTypes.Count > 0 ? 0 : x.MarkerBase ? 1 : 2)
                .ThenBy(x => x.Key.ToString(), StringComparer.Ordinal).ToList();
            var cellsWithRows = rows.Select(x => x.Cell).ToHashSet();
            int navmeshes = mode == Mode.Near
                ? walk.Navmeshes.Where(n => cellsWithRows.Contains(n.Key)).Sum(n => n.Value.Count)
                : walk.Navmeshes.Values.Sum(n => n.Count);
            var byBase = rows.GroupBy(x => x.BaseType ?? "unknown").Select(g => (Type: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Type, StringComparer.Ordinal).ToList();
            var parents = rows.Where(x => x.Parent != null).GroupBy(x => x.Parent!).Select(g => (Parent: g.Key, Children: g.Count()))
                .OrderByDescending(g => g.Children).ThenBy(g => g.Parent, StringComparer.Ordinal).ToList();
            var shown = max == 0 ? rows : rows.Take(max).ToList();
            var shownAnchors = max == 0 ? anchors : anchors.Take(max).ToList();
            Log.Write($"cell: {rows.Count} references kept, {movedOut.Count} moved out, {deleted.Count} deleted, {anchors.Count} anchors, {sw.Elapsed.TotalSeconds:F1} s");

            string CellText(FormKey c) => walk.CellNames.TryGetValue(c, out var n) && n.Length > 0 ? $"{SafeText.OneLine(n)} [{c}]" : $"[{c}]";
            var info = mode == Mode.Near ? null : CellInfo(walk, ids, plugins);
            string? worldText = null;
            if (mode == Mode.Near)
            {
                var ws = WorldCopies(found!.Value, plugins);
                worldText = ws.Count > 0 && EditorIdOf(FromCopies(ws, w => EditorIdOf(w) is { Length: > 0 })) is { Length: > 0 } we ? $"{SafeText.OneLine(we)} [{found}]" : $"[{found}]";
            }
            resultPrinted = true;
            var source = lo.OrderSource;
            if (json)
            {
                Console.WriteLine(Json(version, source, plugins.Count, info, mode == Mode.Near ? (worldText!, nx, ny, radius) : null,
                    rows, shown, movedOut.Select(m => (m.Key, CellText(m.To), m.By)).ToList(), deleted.Select(d => (d.Key, d.By)).ToList(),
                    shownAnchors, navmeshes, byBase, parents, CellText, lo.Problems.Select(p => $"load-order problem: {p}").Concat(unread).ToList(), input.LeftOut));
            }
            else
            {
                if (!quiet) Console.WriteLine($"sf-mod-check {version} cell: {SafeText.OneLine(q)} in {plugins.Count} plugins ({SafeText.OneLine(source)})");
                if (info != null)
                {
                    Console.WriteLine($"Cell {info.Text}{(info.Name != null ? $" \"{info.Name}\"" : "")} ({info.Where})");
                    if (info.Location != null) Console.WriteLine($"  location: {info.Location}{(info.LocationFromWorld ? " (from the worldspace)" : "")}");
                    Console.WriteLine($"  added by {info.AddedBy}{(info.AddedByLoaded ? "" : " (not loaded)")}" + (info.ChangedBy.Count > 0 ? $"; changed by {string.Join(", ", info.ChangedBy)}" : ""));
                }
                else
                    Console.WriteLine($"Near ({F(nx)}, {F(ny)}) in worldspace {worldText}, radius {F(radius)}: {rows.Count} reference{S(rows.Count)} in {cellsWithRows.Count} cell{S(cellsWithRows.Count)}");
                int pers = rows.Count(x => x.Persistent), dis = rows.Count(x => x.Disabled);
                Console.WriteLine($"  references: {rows.Count} (persistent {pers}, temporary {rows.Count - pers}; initially disabled {dis}), navmeshes {navmeshes}");
                if (byBase.Count > 0) Console.WriteLine($"  by base type: {string.Join(", ", byBase.Select(b => $"{b.Type} {b.Count}"))}");
                if (parents.Count > 0)
                    Console.WriteLine($"  enable parents: {string.Join(", ", parents.Take(MaxParentsText).Select(p => $"{p.Parent} {p.Children} child{(p.Children == 1 ? "" : "ren")}"))}" +
                                      (parents.Count > MaxParentsText ? $", and {parents.Count - MaxParentsText} more (--json lists them all)" : ""));
                Console.WriteLine(rows.Count == 0 && mode == Mode.Near ? $"References (0): no references within {F(radius)}." : $"References ({rows.Count}):");
                foreach (var row in shown) Console.WriteLine("  " + RowText(row, mode == Mode.Near, CellText));
                if (rows.Count > shown.Count) Console.WriteLine($"  ... and {rows.Count - shown.Count} more (--max 0 lists them all)");
                Console.WriteLine(movedOut.Count == 0 ? "Moved out (0)." : $"Moved out ({movedOut.Count}):");
                foreach (var m in movedOut) Console.WriteLine($"  [{m.Key}] to {CellText(m.To)} by {SafeText.OneLine(m.By)}");
                Console.WriteLine(deleted.Count == 0 ? "Deleted (0)." : $"Deleted ({deleted.Count}):");
                foreach (var d in deleted) Console.WriteLine($"  {RefName(d.Row)} deleted by {SafeText.OneLine(d.By)}");
                Console.WriteLine(anchors.Count == 0 ? "Persistent anchors (0)." : $"Persistent anchors ({anchors.Count}):");
                foreach (var x in shownAnchors) Console.WriteLine("  " + AnchorText(x));
                if (anchors.Count > shownAnchors.Count) Console.WriteLine($"  ... and {anchors.Count - shownAnchors.Count} more (--max 0 lists them all)");
                foreach (var l in input.LeftOut) Console.WriteLine($"left out: {l.Input}: {l.Why}");
                foreach (var u in lo.Problems) Console.WriteLine($"not read: load-order problem: {u}");
                foreach (var u in unread) Console.WriteLine($"not read: {u}");
                Console.WriteLine(mode == Mode.Near
                    ? (rows.Count == 0 ? $"RESULT: no references within {F(radius)} of ({F(nx)}, {F(ny)}) in {worldText}"
                                       : $"RESULT: {rows.Count} reference{S(rows.Count)} within {F(radius)} of ({F(nx)}, {F(ny)}) in {worldText} ({sw.Elapsed.TotalSeconds:F0} s)")
                    : $"RESULT: {info!.Text} holds {rows.Count} reference{S(rows.Count)}, {anchors.Count} persistent ({sw.Elapsed.TotalSeconds:F0} s)");
            }
            Log.Write($"result: exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    static string S(int n) => n == 1 ? "" : "s";
    static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string Pos(P3Float p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";

    static string RefName(Row r) => r.EditorId is { Length: > 0 } e ? $"{SafeText.OneLine(e)} [{r.Key}]" : $"[{r.Key}]";

    /// <summary>A reference's text line (the design's shape); with near, the distance first and the cell it sits in.</summary>
    internal static string RowText(Row r, bool near, Func<FormKey, string> cellText)
    {
        // persistent from the flag (0x400) and from the cell's persistent list; a mod can set one without the other
        var marks = new List<string>
        {
            !r.Persistent ? "temporary"
            : r.Flagged && r.InPersistentList ? "persistent"
            : r.Flagged ? "persistent (flag only: in the temporary list)"
            : "persistent (in the persistent list, flag not set)",
        };
        if (r.Disabled) marks.Add("disabled");
        if (r.Parent != null) marks.Add($"parent {r.Parent}{(r.ParentOpposite ? " (opposite)" : "")}");
        var text = (near ? $"{F(r.Distance ?? 0),8}  " : "") + $"{RefName(r)} {r.Type}" +
                   (r.Base != null ? $" base {r.Base}{(r.BaseType != null ? $" ({r.BaseType})" : "")}" : "") +
                   (r.Position is { } p ? $" at {Pos(p)}" : "") + $" {string.Join(", ", marks)}" +
                   (r.LocationRefTypes.Count > 0 ? $"; location ref types: {string.Join(", ", r.LocationRefTypes)}" : "") +
                   (near ? $"; in {cellText(r.Cell)}" : "") +
                   $"; added by {SafeText.OneLine(r.AddedBy)}{(r.AddedByLoaded ? "" : " (not loaded)")}" +
                   (r.ChangedBy.Count > 0 ? $"; changed by {string.Join(", ", r.ChangedBy.Select(SafeText.OneLine))}" : "");
        return r.Asked ? text + "   <- asked" : text;
    }

    static string AnchorText(Row r) =>
        $"{RefName(r)}" + (r.Base != null ? $" base {r.Base}{(r.BaseType != null ? $" ({r.BaseType})" : "")}" : $" {r.Type}") +
        (r.Position is { } p ? $" at {Pos(p)}" : "") +
        (r.LocationRefTypes.Count > 0 ? $"; location ref types: {string.Join(", ", r.LocationRefTypes)}" : "");

    static string? EditorIdOf(IMajorRecordGetter r)
    {
        try { return r.EditorID; }
        catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
    }

    static string SigOf(Type t) => Checker.SigsOf(t)?.Order(StringComparer.Ordinal).FirstOrDefault() ?? Analyzer.TypeShort(t);

    /// <summary>The placed reference's base (what it places), or null for a type without one.</summary>
    internal static IFormLinkGetter? BaseOf(IPlacedGetter r) => r switch
    {
        IPlacedObjectGetter o => o.Base,
        IPlacedNpcGetter n => n.Base,
        IPlacedHazardGetter h => h.Hazard,
        IPlacedMissileGetter m => m.Projectile,
        IPlacedArrowGetter m => m.Projectile,
        IPlacedBeamGetter m => m.Projectile,
        IPlacedFlameGetter m => m.Projectile,
        IPlacedConeGetter m => m.Projectile,
        IPlacedBarrierGetter m => m.Projectile,
        IPlacedTrapGetter m => m.Projectile,
        _ => null,
    };

    static IEnableParentGetter? EnableParentOf(IPlacedGetter r) => r switch
    {
        IPlacedObjectGetter o => o.EnableParent,
        IPlacedNpcGetter n => n.EnableParent,
        IAPlacedTrapGetter t => t.EnableParent,
        _ => null,
    };

    static IReadOnlyList<IFormLinkGetter<ILocationReferenceTypeGetter>>? LocationRefTypesOf(IPlacedGetter r) => r switch
    {
        IPlacedObjectGetter o => o.LocationRefTypes,
        IPlacedNpcGetter n => n.LocationRefTypes,
        IAPlacedTrapGetter t => t.LocationRefTypes,
        _ => null,
    };

    static Row MakeRow(Ref r, ILinkCache ids, Dictionary<(FormKey, Type), (string Text, string? Sig, string? EditorId)> bases, HashSet<string> loaded)
    {
        var win = r.Winner;
        // A deleted copy keeps no fields (not even its EditorID), so a deleted reference shows its last copy before that.
        var rec = win.Rec.IsDeleted ? r.Copies.LastOrDefault(c => !c.Rec.IsDeleted)?.Rec ?? win.Rec : win.Rec;
        var origin = r.Key.ModKey.FileName.String;
        var row = new Row
        {
            Key = r.Key, Type = SigOf(rec.GetType()), Cell = win.Cell,
            EditorId = r.Copies.Select(c => EditorIdOf(c.Rec)).LastOrDefault(e => !string.IsNullOrEmpty(e)),
            AddedBy = origin, AddedByLoaded = loaded.Contains(origin),
            InPersistentList = win.InPersistentList,
            Flagged = (rec.MajorRecordFlagsRaw & PersistentFlag) != 0,
            Persistent = (rec.MajorRecordFlagsRaw & PersistentFlag) != 0 || win.InPersistentList,
            Disabled = (rec.MajorRecordFlagsRaw & DisabledFlag) != 0,
        };
        row.ChangedBy = r.Copies.Select(c => c.Plugin).Where(p => !p.FileName.Equals(origin, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => p.Index).OrderBy(p => p.Index).Select(p => p.FileName).ToList();
        try
        {
            if (rec is IPositionRotationGetter pr) (row.Position, row.Rotation) = (pr.Position, pr.Rotation);
            if (BaseOf(rec) is { } b && !b.FormKey.IsNull)
            {
                var bk = (b.FormKey, b.Type);
                if (!bases.TryGetValue(bk, out var bi))
                {
                    bi = ($"[{b.FormKey}]", null, null);
                    try
                    {
                        if (ids.TryResolveIdentifier(b.FormKey, BaseTypes(b.Type), out var eid, out var foundType))
                            bi = (eid is { Length: > 0 } ? $"{SafeText.OneLine(eid)} [{b.FormKey}]" : $"[{b.FormKey}]", foundType != null ? SigOf(foundType) : null, eid);
                        else Log.Write($"cell: base {b.FormKey} ({Analyzer.TypeShort(b.Type)}) not found in the load order");
                    }
                    catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"cell: base {b.FormKey} can't be named ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"); }
                    bases[bk] = bi;
                }
                (row.Base, row.BaseType) = (bi.Text, bi.Sig);
                row.MarkerBase = bi.EditorId?.Contains("Marker", StringComparison.OrdinalIgnoreCase) == true;
            }
            if (EnableParentOf(rec) is { } ep && !ep.Reference.FormKey.IsNull)
            {
                row.ParentKey = ep.Reference.FormKey;
                row.ParentOpposite = ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
            }
            if (LocationRefTypesOf(rec) is { } lrt)
                row.LocationRefTypes = lrt.Where(l => !l.FormKey.IsNull).Select(l => FindCommand.LinkText(l, ids)).ToList();
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"cell: {r.Key} in {win.Plugin.FileName}: a field can't be read ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"); }
        return row;
    }

    static readonly Dictionary<Type, Type[]> concreteFor = new();

    /// <summary>The record types a base link may name: the link type's own, then every base object type. Mutagen 0.54.4
    /// maps a placed object's base link (IPlaceableObjectGetter) to texture sets only, while statics, containers, misc
    /// items and the rest carry IBaseObjectGetter (checked 2026-11-27 on made-up plugins).</summary>
    internal static Type[] BaseTypes(Type linkType) => Concrete(linkType).Concat(Concrete(typeof(IBaseObjectGetter))).Distinct().ToArray();

    /// <summary>The concrete record getter types a link type covers. A base link names a link interface
    /// (IPlaceableObjectGetter) that the record getters don't inherit from, so Mutagen's own mapping lists them.</summary>
    static Type[] Concrete(Type linkType)
    {
        lock (concreteFor)
        {
            if (concreteFor.TryGetValue(linkType, out var known)) return known;
            var map = LinkInterfaceMapping.Instance.InterfaceToObjectTypes(GameCategory.Starfield);
            var result = map.TryGetValue(linkType, out var regs)
                ? regs.Registrations.Select(r => r.GetterType).ToArray()
                : Analyzer.AllConcrete.Value.Where(linkType.IsAssignableFrom).ToArray();
            return concreteFor[linkType] = result.Length > 0 ? result : new[] { linkType };
        }
    }

    /// <summary>The records of the asked kind whose EditorID is q (letter case ignored), from the cells, the placed
    /// references or the worldspaces alone, so no other record type is read.</summary>
    static List<FormKey> ByEditorId(Mode mode, string q, List<PluginInfo> plugins, List<string> unread)
    {
        var keys = new HashSet<FormKey>();
        bool Eq(IMajorRecordGetter r) => string.Equals(EditorIdOf(r), q, StringComparison.OrdinalIgnoreCase);
        foreach (var p in plugins)
        {
            Analyzer.Where = $"looking for EditorID {q} in {p.FileName}";
            try
            {
                if (mode == Mode.Near)
                {
                    foreach (var w in p.Mod.Worldspaces) if (Eq(w)) keys.Add(w.FormKey);
                    continue;
                }
                foreach (var c in PatchCommand.AllCells(p.Mod))
                {
                    if (mode == Mode.Cell) { if (Eq(c)) keys.Add(c.FormKey); continue; }
                    foreach (var r in c.Persistent) if (Eq(r)) keys.Add(r.FormKey);
                    foreach (var r in c.Temporary) if (Eq(r)) keys.Add(r.FormKey);
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add(Unread(p, e)); }
        }
        Log.Write($"EditorID {q}: {keys.Count} match(es)");
        return keys.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();
    }

    static string Unread(PluginInfo p, Exception e) =>
        $"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later cells weren't searched";

    static bool MayHold(PluginInfo p, ModKey origin) =>
        p.FileName.Equals(origin.FileName, StringComparison.OrdinalIgnoreCase)
        || p.Masters.Contains(origin.FileName.String, StringComparer.OrdinalIgnoreCase);

    /// <summary>--ref: the cell that holds the reference's winning copy (the last plugin with a copy), or null.</summary>
    static FormKey? CellOfRef(FormKey rk, List<PluginInfo> plugins, List<string> unread)
    {
        FormKey? cell = null;
        foreach (var p in plugins.Where(p => MayHold(p, rk.ModKey)))
        {
            Analyzer.Where = $"looking for {rk} in {p.FileName}";
            try
            {
                foreach (var c in PatchCommand.AllCells(p.Mod))
                    if (c.Persistent.Any(r => r.FormKey == rk) || c.Temporary.Any(r => r.FormKey == rk)) cell = c.FormKey;
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add(Unread(p, e)); }
        }
        Log.Write($"--ref {rk}: {(cell != null ? $"winning copy in cell {cell}" : "no placed copy found")}");
        return cell;
    }

    static bool HasWorld(FormKey wk, List<PluginInfo> plugins, List<string> unread) => LastWorld(wk, plugins, unread) != null;

    /// <summary>The worldspace's winning copy (the last plugin that holds it), or null.</summary>
    static IWorldspaceGetter? LastWorld(FormKey wk, List<PluginInfo> plugins, List<string>? unread = null) =>
        WorldCopies(wk, plugins, unread) is { Count: > 0 } l ? l[^1] : null;

    /// <summary>Every plugin's copy of the worldspace, in load order.</summary>
    static List<IWorldspaceGetter> WorldCopies(FormKey wk, List<PluginInfo> plugins, List<string>? unread = null)
    {
        var copies = new List<IWorldspaceGetter>();
        foreach (var p in plugins.Where(p => MayHold(p, wk.ModKey)))
            try
            {
                IWorldspaceGetter? last = null;
                foreach (var w in p.Mod.Worldspaces) if (w.FormKey == wk) last = w;
                if (last != null) copies.Add(last);
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread?.Add(Unread(p, e)); }
        return copies;
    }

    /// <summary>The last copy that has a field, back to the last full copy: a partial copy (the 0x4000 flag; official
    /// plugins ship partial cells and worldspaces) leaves out the fields it doesn't change.</summary>
    static T FromCopies<T>(IReadOnlyList<T> copies, Func<T, bool> has) where T : IMajorRecordGetter
    {
        for (int i = copies.Count - 1; i >= 0; i--)
        {
            var c = copies[i];
            try { if (has(c)) return c; }
            catch (Exception e) when (Fatal.Recoverable(e)) { }
            if (!FieldCompare.IsPartial(c)) return c;
        }
        return copies[^1];
    }

    /// <summary>The walk: first every wanted cell in every plugin (their references, navmeshes and the cells' own copies),
    /// then the other cells of the plugins that may override those references (any plugin with a reference's plugin as
    /// a master), so a reference moved to another cell is seen there. A reference's own plugin isn't read again: its
    /// copy is the first, so it never decides where the winning copy sits.</summary>
    internal static Walk Collect(List<PluginInfo> plugins, Func<ICellGetter, IWorldspaceGetter?, bool> wanted, List<string> unread)
    {
        var walk = new Walk { Unread = unread };
        void Add(PluginInfo p, IPlacedGetter r, FormKey cell, bool persistent)
        {
            if (!walk.Refs.TryGetValue(r.FormKey, out var e)) walk.Refs[r.FormKey] = e = new Ref { Key = r.FormKey };
            e.Copies.RemoveAll(c => c.Plugin.Index == p.Index);   // a plugin that lists it twice: its last copy
            e.Copies.Add(new(p, r, cell, persistent));
        }
        foreach (var p in plugins)
        {
            var sw = Stopwatch.StartNew();
            int cells = 0, refs = 0;
            Analyzer.Where = $"reading the cells in {p.FileName}";
            try
            {
                foreach (var (c, w, top) in PatchCommand.AllCellsWithWorld(p.Mod))
                {
                    if (!wanted(c, w)) continue;
                    cells++;
                    walk.WantedCells.Add(c.FormKey);
                    walk.CellCopies.Add((p, c, w, top));
                    if (EditorIdOf(c) is { } ce) walk.CellNames[c.FormKey] = ce;
                    foreach (var r in c.Persistent) { Add(p, r, c.FormKey, true); refs++; }
                    foreach (var r in c.Temporary) { Add(p, r, c.FormKey, false); refs++; }
                    if (c.NavigationMeshes.Count > 0)
                    {
                        if (!walk.Navmeshes.TryGetValue(c.FormKey, out var set)) walk.Navmeshes[c.FormKey] = set = new();
                        foreach (var n in c.NavigationMeshes) set.Add(n.FormKey);
                    }
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add(Unread(p, e)); }
            if (cells > 0) Log.Write($"cell: {p.FileName}: {cells} wanted cell(s), {refs} references, {sw.Elapsed.TotalSeconds:F1} s");
        }
        // the cell list's FormKeys are those of the wanted cells; the walk's dictionary keeps the first copy's spelling
        var origins = walk.Refs.Keys.Select(k => k.ModKey).Distinct().ToList();
        var tracked = walk.Refs.Keys.ToHashSet();
        foreach (var p in plugins)
        {
            if (!origins.Any(o => !p.FileName.Equals(o.FileName, StringComparison.OrdinalIgnoreCase) && MayHold(p, o))) continue;
            var sw = Stopwatch.StartNew();
            int moved = 0;
            Analyzer.Where = $"looking for moved references in {p.FileName}";
            try
            {
                foreach (var (c, w, _) in PatchCommand.AllCellsWithWorld(p.Mod))
                {
                    if (walk.WantedCells.Contains(c.FormKey)) continue;
                    bool any = false;
                    foreach (var r in c.Persistent) if (tracked.Contains(r.FormKey)) { Add(p, r, c.FormKey, true); moved++; any = true; }
                    foreach (var r in c.Temporary) if (tracked.Contains(r.FormKey)) { Add(p, r, c.FormKey, false); moved++; any = true; }
                    if (any && EditorIdOf(c) is { } ce) walk.CellNames[c.FormKey] = ce;
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add(Unread(p, e)); }
            Log.Write($"cell: {p.FileName}: {moved} copies in other cells, {sw.Elapsed.TotalSeconds:F1} s");
        }
        foreach (var r in walk.Refs.Values) r.Copies.Sort((x, y) => x.Plugin.Index.CompareTo(y.Plugin.Index));
        return walk;
    }

    internal sealed record Info(string Text, string? Name, string Where, string? Location, bool LocationFromWorld,
                                string AddedBy, bool AddedByLoaded, List<string> ChangedBy, FormKey Key, string? EditorId,
                                bool Interior, string? World, int[]? Grid);

    /// <summary>The cell's own lines, from its winning copy (the last plugin with a copy). A partial winning copy
    /// (official plugins ship some: CityNeonCore in SFBGS050.esm) leaves out the fields it doesn't change, so each of
    /// those comes from the last copy that has it, back to the last full copy.</summary>
    static Info CellInfo(Walk walk, ILinkCache ids, List<PluginInfo> plugins)
    {
        var (_, cell, world, top) = walk.CellCopies[^1];
        var cellCopies = walk.CellCopies.Select(c => c.Cell).ToList();
        ICellGetter From(Func<ICellGetter, bool> has) => FromCopies(cellCopies, has);
        var key = cell.FormKey;
        var eid = EditorIdOf(From(c => EditorIdOf(c) is { Length: > 0 }));
        string? name = null;
        try { name = From(c => c.Name?.String is { Length: > 0 }).Name?.String is { Length: > 0 } n ? SafeText.OneLine(n) : null; }
        catch (Exception e) when (Fatal.Recoverable(e)) { }
        var locCell = From(c => !c.Location.IsNull);
        string? worldText = null, location = null;
        bool fromWorld = false;
        int[]? grid = null;
        if (world != null)
        {
            var worlds = WorldCopies(world.FormKey, plugins);
            if (worlds.Count == 0) worlds.Add(world);
            var we = EditorIdOf(FromCopies(worlds, w => EditorIdOf(w) is { Length: > 0 }));
            worldText = we is { Length: > 0 } ? $"{SafeText.OneLine(we)} [{world.FormKey}]" : $"[{world.FormKey}]";
            if (!top && From(c => c.Grid != null).Grid is { } g) grid = new[] { g.Point.X, g.Point.Y };
            var wl = FromCopies(worlds, w => !w.Location.IsNull).Location;
            if (locCell.Location.IsNull && !wl.IsNull) { location = FindCommand.LinkText(wl, ids); fromWorld = true; }
        }
        if (!locCell.Location.IsNull) location = FindCommand.LinkText(locCell.Location, ids);
        var where = world == null ? "interior"
            : top ? $"persistent cell of worldspace {worldText}"
            : grid != null ? $"worldspace {worldText}, grid {grid[0]},{grid[1]}" : $"worldspace {worldText}";
        var origin = key.ModKey.FileName.String;
        var changed = walk.CellCopies.Select(c => c.Plugin).Where(p => !p.FileName.Equals(origin, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => p.Index).OrderBy(p => p.Index).Select(p => p.FileName).ToList();
        return new(eid is { Length: > 0 } ? $"{SafeText.OneLine(eid)} [{key}]" : $"[{key}]", name, where, location, fromWorld,
                   origin, plugins.Any(p => p.FileName.Equals(origin, StringComparison.OrdinalIgnoreCase)), changed, key, eid,
                   world == null, worldText, grid);
    }

    /// <summary>Not found, or found but not a cell (a placed reference, a worldspace): the record's type is looked up
    /// through find's own lookup so the message names it.</summary>
    static int NotFound(Mode mode, Kind kind, string q, FormKey key, FormKey? asked, FormKey? found, string? notFoundWhy,
                        List<PluginInfo> plugins, LoadOrderSetup lo, LoadOrderInput input, bool json, string version, Stopwatch sw,
                        List<string> unread, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        var what = mode switch { Mode.Cell => "cell", Mode.Ref => "placed reference", _ => "worldspace" };
        // (a placed reference always sits in a cell, so --ref with no cell found means no placed reference has the key)
        string? otherType = null;
        bool placed = false, isCell = false;
        if (notFoundWhy == null)
        {
            List<FindCommand.Match> other;
            var findUnread = new List<string>();
            if (kind == Kind.EditorId) other = FindCommand.ByEditorId(q, null, plugins, findUnread);
            else other = FindCommand.Copies(new HashSet<FormKey> { key }, new(), plugins, findUnread);
            if (other.Count > 0) otherType = string.Join(", ", other.Select(m => m.Type).Distinct());
            placed = other.Any(m => m.Winner.Rec is IPlacedGetter);
            isCell = other.Any(m => m.Winner.Rec is ICellGetter);
        }
        if (otherType != null)
        {
            var hint = mode switch
            {
                Mode.Cell when placed => "; for a reference use --ref",
                Mode.Ref when isCell => "; for a cell leave out --ref",
                _ => "",
            };
            Console.Error.WriteLine($"{SafeText.OneLine(q)} is a {otherType}, not a {what}{hint}.");
            cantWhy = $"{SafeText.OneLine(q)} is a {otherType}, not a {what}";
            return 2;
        }
        var text = kind switch
        {
            Kind.EditorId => $"no {what} with EditorID {SafeText.OneLine(q)}",
            Kind.FormKey => $"no {what} {SafeText.OneLine(q)}",
            _ => $"no {what} with FormID {SafeText.OneLine(q)}",
        } + $" in {plugins.Count} plugins" + (notFoundWhy != null ? $": {notFoundWhy}" : "");
        var hintText = (kind == Kind.EditorId ? "EditorIDs must match exactly (letter case doesn't matter). " : "") +
                       $"The load order searched was {lo.OrderSource}" +
                       (lo.Problems.Count > 0 ? $", with {lo.Problems.Count} load-order problems (plugins that didn't load are not searched; --verbose lists them)" : "") + ". " +
                       (input.Plugin != null
                           ? "--plugin searches only that plugin and its masters; to search your whole load order, leave out --plugin (and give --plugins or --mo2)."
                           : "If the mod isn't in that load order, give its Plugins.txt with --plugins (with Mod Organizer 2: --mo2 <MO2 folder>), or name the folder that holds the plugin with --extra-data.") +
                       (kind == Kind.FormId ? " A console FormID depends on the load order; the FormKey form (0008CF:ShatteredSpace.esm) doesn't." : "");
        resultPrinted = true;
        cantWhy = text;
        if (json)
            Console.WriteLine(new JsonObject
            {
                ["tool"] = "sf-mod-check cell", ["version"] = version, ["result"] = "not found", ["error"] = text, ["hint"] = hintText,
            }.ToJsonString(Indented));
        else
        {
            Console.WriteLine($"HINT: {hintText}");
            Console.WriteLine($"RESULT: NOT FOUND ({text})");
        }
        Log.Write($"result: not found, exit code 2, {sw.Elapsed.TotalSeconds:F1} s");
        return 2;
    }

    static JsonArray Arr(IEnumerable<string> s) => new(s.Select(x => (JsonNode)x).ToArray());
    static JsonArray? Vec(P3Float? p) => p is { } v ? new JsonArray(v.X, v.Y, v.Z) : null;

    static string Json(string version, string source, int pluginCount, Info? info, (string World, double X, double Y, double Radius)? near,
                       List<Row> rows, List<Row> shown, List<(FormKey Key, string To, string By)> movedOut, List<(FormKey Key, string By)> deleted,
                       List<Row> anchors, int navmeshes, List<(string Type, int Count)> byBase, List<(string Parent, int Children)> parents,
                       Func<FormKey, string> cellText, List<string> unread, List<LoadOrderInput.LeftOutInput> leftOut)
    {
        var bb = new JsonObject();
        foreach (var (t, n) in byBase) bb[t] = n;
        int pers = rows.Count(x => x.Persistent);
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check cell",
            ["version"] = version,
            ["loadOrder"] = new JsonObject { ["source"] = source, ["plugins"] = pluginCount },
            ["cell"] = info == null ? null : new JsonObject
            {
                ["formKey"] = info.Key.ToString(), ["editorId"] = info.EditorId, ["name"] = info.Name, ["interior"] = info.Interior,
                ["worldspace"] = info.World, ["grid"] = info.Grid == null ? null : new JsonArray(info.Grid[0], info.Grid[1]),
                ["location"] = info.Location, ["locationFromWorldspace"] = info.LocationFromWorld,
                ["addedBy"] = info.AddedBy, ["changedBy"] = Arr(info.ChangedBy),
            },
            ["near"] = near is { } np ? new JsonObject { ["worldspace"] = np.World, ["x"] = np.X, ["y"] = np.Y, ["radius"] = np.Radius } : null,
            ["counts"] = new JsonObject
            {
                ["references"] = rows.Count, ["persistent"] = pers, ["temporary"] = rows.Count - pers,
                ["initiallyDisabled"] = rows.Count(x => x.Disabled), ["navmeshes"] = navmeshes, ["byBaseType"] = bb,
                ["enableParents"] = new JsonArray(parents.Select(p => (JsonNode)new JsonObject { ["ref"] = p.Parent, ["children"] = p.Children }).ToArray()),
            },
            ["references"] = new JsonArray(shown.Select(r =>
            {
                var o = new JsonObject
                {
                    ["formKey"] = r.Key.ToString(), ["type"] = r.Type, ["editorId"] = r.EditorId, ["base"] = r.Base, ["baseType"] = r.BaseType,
                    ["position"] = Vec(r.Position), ["rotation"] = Vec(r.Rotation),
                    ["persistent"] = r.Persistent, ["inPersistentList"] = r.InPersistentList, ["initiallyDisabled"] = r.Disabled,
                    ["enableParent"] = r.Parent, ["enableParentOpposite"] = r.ParentOpposite, ["locationRefTypes"] = Arr(r.LocationRefTypes),
                    ["addedBy"] = r.AddedBy, ["addedByLoaded"] = r.AddedByLoaded, ["changedBy"] = Arr(r.ChangedBy), ["cell"] = cellText(r.Cell),
                };
                if (near != null) o["distance"] = Math.Round(r.Distance ?? 0, 3);
                if (r.Asked) o["asked"] = true;
                return (JsonNode)o;
            }).ToArray()),
            ["movedOut"] = new JsonArray(movedOut.Select(m => (JsonNode)new JsonObject { ["formKey"] = m.Key.ToString(), ["toCell"] = m.To, ["by"] = m.By }).ToArray()),
            ["deleted"] = new JsonArray(deleted.Select(d => (JsonNode)new JsonObject { ["formKey"] = d.Key.ToString(), ["by"] = d.By }).ToArray()),
            ["anchors"] = new JsonArray(anchors.Select(r => (JsonNode)new JsonObject
            {
                ["formKey"] = r.Key.ToString(), ["editorId"] = r.EditorId, ["base"] = r.Base, ["position"] = Vec(r.Position),
                ["locationRefTypes"] = Arr(r.LocationRefTypes),
            }).ToArray()),
            ["listed"] = shown.Count,
            ["total"] = rows.Count,
            ["leftOut"] = new JsonArray(leftOut.Select(l => (JsonNode)new JsonObject { ["input"] = l.Input, ["why"] = l.Why }).ToArray()),
            ["unread"] = Arr(unread),
            ["result"] = "found",
        };
        return root.ToJsonString(Indented);
    }
}
