// Triage section "Scripts" (0.8.0, chunk C3). Every compiled script (.pex) of the mod, loose or inside its .ba2
// archives, is read from its bytecode with Pex.cs (nothing is decompiled and no source is written). For each script the
// report gives its name, its parent, every function it calls grouped by the called script or type, and whether it logs
// with Debug.Trace. Calls worth a person's look are listed one per row:
//   - Game.GetFormFromFile with a plugin name (the plugin it reads records from), Game.IsPluginInstalled (which plugin)
//   - console commands, INI and game-setting reads and changes, debug functions that work like console commands, its
//     own log file
//   - calls to scripts that are neither in the mod nor in the game (another mod's, or a script extender's)
//   - timers and registrations started from events that fire often (OnTimer, OnUpdate, OnHit ...) or at every game
//     load (OnPlayerLoadGame)
//   - plugin names (.esm .esp .esl) and file names written into the script as text
// The function names checked come from the game's own compiled scripts (read by name from their string tables) and
// the usual script-extender names; no game source is used. The text says what was found, never a judgement.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SfModCheck;

public static class TriageScripts
{
    const string S = TriageSections.Scripts;

    /// <summary>A script's bytes from the mod: File is its mod-relative path, or "archive.ba2:scripts\x.pex" for one
    /// inside an archive. Error is set (and Data null) when it couldn't be taken out.</summary>
    public sealed record Source(string File, byte[]? Data, string? Error);

    /// <summary>One call or string worth a look, before findings are grouped per script.</summary>
    sealed record Hit(TriageLevel Level, string File, string Key, string What, string Where, string Call, string Detail);

    static readonly HashSet<string> FrequentEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnTimer", "OnTimerGameTime", "OnUpdate", "OnUpdateGameTime", "OnPlayerLoadGame", "OnHit", "OnItemAdded",
        "OnItemRemoved", "OnAnimationEvent", "OnMagicEffectApply", "OnMenuOpenCloseEvent", "OnCombatStateChanged",
        "OnLocationChange", "OnCellLoad", "OnLoad", "OnEnterBleedout", "OnPlayerFastTravelEnd",
    };
    /// <summary>Events that run again when they start a timer: the timer loops.</summary>
    static readonly HashSet<string> LoopEvents = new(StringComparer.OrdinalIgnoreCase) { "OnTimer", "OnTimerGameTime", "OnUpdate", "OnUpdateGameTime" };
    static readonly HashSet<string> TimerCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartTimer", "StartTimerGameTime", "RegisterForUpdate", "RegisterForSingleUpdate", "RegisterForUpdateGameTime",
        "RegisterForSingleUpdateGameTime",
    };
    /// <summary>Debug functions that work like console commands or end the game (the native functions of the game's
    /// own Debug script, by name; Debug.ExecuteConsole is caught by the console rule).</summary>
    static readonly HashSet<string> DebugToggles = new(StringComparer.OrdinalIgnoreCase)
    {
        "QuitGame", "SetGodMode", "EnableAI", "EnableCollisions", "EnableDetection", "EnableMenus", "CenterOnCell",
        "CenterOnCellAndWait", "PlayerMoveToAndWait",
    };

    static readonly Regex PluginName = new(@"^[^\\/:*?""<>|\r\n]{1,200}\.(esm|esp|esl)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex FileName = new(@"^[^*?""<>|\r\n]{0,240}[^\\/\s.]\.(txt|ini|json|toml|yaml|yml|xml|csv|log|cfg|dll|exe|bat|cmd|ps1|vbs|js|py|lua|pex|psc|ba2|nif|dds|wav|wem|swf|gfx|mat|bin|dat|sav|zip|7z|rar)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Runnable = new(@"\.(dll|exe|bat|cmd|ps1|vbs|js|py|lua)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex OutsidePath = new(@"^[A-Za-z]:[\\/]|(^|[\\/])\.\.[\\/]");

    public static IEnumerable<TriageFinding> Run(TriageContext c)
    {
        var findings = new List<TriageFinding>();
        var sources = Gather(c, findings);
        var scriptTable = c.Report.Table(S, "Scripts", "Script", "Name", "Parent", "Functions called", "Calls", "Logs");
        var callTable = c.Report.Table(S, "Calls worth a look", "Level", "Script", "In", "Call", "Detail");
        if (sources.Count == 0)
        {
            // settled design of 0.9.1: with a skipped link the sentence says only what was read, and points to Files
            var links = c.SkippedLinks;
            findings.Add(new(TriageLevel.Note, S, "", links > 0
                ? $"The files triage read hold no compiled scripts (.pex); {TriageContext.LinkedItems(links)} {(links == 1 ? "was" : "were")} skipped, see Files."
                : "The mod has no compiled scripts (.pex), loose or in its archives."));
            return findings;
        }

        var read = new List<(string File, PexFile Pex)>();
        foreach (var s in sources)
        {
            if (s.Data == null)
            {
                findings.Add(new(TriageLevel.Look, S, s.File, $"{(s.File.Contains(".ba2:", StringComparison.Ordinal) ? "couldn't be taken out of its archive, so it" : "")} wasn't read: {s.Error}".TrimStart()));
                scriptTable.Add(s.File, "", "", "(not read)", "", "");
                continue;
            }
            try
            {
                var pex = Pex.Read(s.Data, s.File);
                read.Add((s.File, pex));
                Log.Write($"scripts: read {s.File}: PEX {pex.Version} game {pex.GameId}, {pex.Strings.Count} strings, {pex.Objects.Count} object(s)");
            }
            catch (InvalidDataException e)
            {
                findings.Add(new(TriageLevel.Look, S, s.File, $"can't be read as a compiled script: {e.Message}"));
                scriptTable.Add(s.File, "", "", "(not read)", "", "");
            }
        }

        // the mod's own plugins and script types, and the game's script types (by name, from its archives)
        var ownPlugins = new HashSet<string>(c.Files.Select(f => RelPath.FileName(f)).Where(f => PluginName.IsMatch(f)), StringComparer.OrdinalIgnoreCase);
        var ownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources) ownTypes.Add(TypeOfPath(s.File));
        foreach (var (_, pex) in read) foreach (var o in pex.Objects) ownTypes.Add(o.Name);
        HashSet<string>? gameTypes = null;
        if (c.HasGame)
        {
            try { gameTypes = GameScriptTypes(c.DataDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                findings.Add(new(TriageLevel.Note, S, "", $"the game's script archives couldn't be read ({e.Message}), so calls to other scripts weren't compared with the game's"));
            }
        }
        else
            findings.Add(new(TriageLevel.Note, S, "", "no game was found, so calls to other scripts weren't compared with the game's own scripts (give --game)"));

        var hits = new List<Hit>();
        var noLog = new List<string>();
        int withCode = 0;
        foreach (var (file, pex) in read)
        {
            if (!pex.IsStarfield)
                findings.Add(new(TriageLevel.Look, S, file, $"is a PEX {pex.Version} script (game id {pex.GameId}): Fallout 4's version, not Starfield's 3.12"));
            var calls = Pex.Calls(pex).ToList();
            var obj = pex.Objects.FirstOrDefault();
            bool hasCode = pex.Objects.SelectMany(o => o.Functions()).Any(f => f.Code.Count > 0);
            bool logs = calls.Any(IsLogCall);
            if (hasCode) { withCode++; if (!logs) noLog.Add(file); }
            scriptTable.Add(file, obj?.Name ?? TypeOfPath(file), obj?.Parent ?? "", Grouped(calls), calls.Count.ToString(),
                !hasCode ? "no code" : logs ? "yes" : "no");
            Rules(file, pex, calls, ownPlugins, ownTypes, gameTypes, hits);
        }

        if (withCode > 0)
            findings.Add(new(TriageLevel.Note, S, "", noLog.Count == 0
                ? $"All {withCode} scripts with code log with Debug.Trace (the Papyrus log shows what they do)."
                : $"{withCode - noLog.Count} of {withCode} scripts with code log with Debug.Trace; these don't, so the Papyrus log won't show what they do: {string.Join(", ", noLog)}"));

        foreach (var h in hits) callTable.Add(h.Level == TriageLevel.Look ? "LOOK" : "NOTE", h.File, h.Where, h.Call, h.Detail);
        foreach (var g in hits.GroupBy(h => (h.File, h.Level, h.Key)))
        {
            var first = g.First();
            var where = g.Select(h => h.Where).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var n = g.Count();
            findings.Add(new(first.Level, S, first.File,
                $"{first.What} ({(n == 1 ? "1 place" : $"{n} places")}, in {string.Join(", ", where.Take(8))}{(where.Count > 8 ? $" and {where.Count - 8} more" : "")})"));
        }
        Log.Write($"scripts: {sources.Count} found, {read.Count} read, {hits.Count} calls or strings worth a look");
        return findings;
    }

    static bool IsLogCall(Pex.Call c) =>
        c.Kind == "static" && c.Type.Equals("Debug", StringComparison.OrdinalIgnoreCase) && c.Name.StartsWith("Trace", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Debug: Trace (3); Game: GetPlayer; ObjectReference: MoveTo (2)": every called function, grouped by the
    /// called type, each with its count when above one. Case is ignored when grouping (Papyrus ignores it); the first
    /// spelling is shown.</summary>
    public static string Grouped(IEnumerable<Pex.Call> calls)
    {
        var parts = calls.GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().Type + ": " + string.Join(", ", g.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                .Select(n => n.Count() > 1 ? $"{n.First().Name} ({n.Count()})" : n.First().Name)));
        return string.Join("; ", parts);
    }

    static string Where(PexFunction f) =>
        f.IsPropertyAccessor ? $"{f.State}.{f.Name}" : f.State == "" ? f.Name : $"{f.Name} (state {f.State})";

    static string? StrArg(Pex.Call c, int i) => i < c.Args.Count && c.Args[i].IsStr ? c.Args[i].Text : null;

    static string Num(PexValue v) => v.Kind switch
    {
        PexValueKind.Int => v.Int.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PexValueKind.Float => v.Float.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        _ => "",
    };

    static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    static void Rules(string file, PexFile pex, List<Pex.Call> calls, HashSet<string> ownPlugins, HashSet<string> ownTypes,
                      HashSet<string>? gameTypes, List<Hit> hits)
    {
        var namedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Whose(string plugin) => ownPlugins.Contains(plugin) ? "its own plugin " : Checker.IsOfficial(plugin) ? "the official plugin " : "";
        foreach (var c in calls)
        {
            var at = Where(c.Caller);
            var call = $"{c.Type}.{c.Name}";
            bool isGame = Is(c.Type, "Game"), isDebug = Is(c.Type, "Debug");

            if (isGame && Is(c.Name, "GetFormFromFile"))
            {
                if (StrArg(c, 1) is { } plugin)
                {
                    namedPlugins.Add(plugin);
                    bool other = Whose(plugin) == "";
                    hits.Add(new(other ? TriageLevel.Look : TriageLevel.Note, file, "gff:" + plugin.ToLowerInvariant(),
                        $"reads records from {Whose(plugin)}{plugin} with Game.GetFormFromFile{(other ? ", so it uses that plugin when it is installed" : "")}",
                        at, call, $"{plugin}, form {(c.Args.Count > 0 ? FormId(c.Args[0]) : "?")}"));
                }
                else
                    hits.Add(new(TriageLevel.Note, file, "gff:var", "reads records with Game.GetFormFromFile from a plugin whose name isn't written in the script",
                        at, call, "plugin name from a variable"));
                continue;
            }
            if (isGame && Is(c.Name, "IsPluginInstalled"))
            {
                var plugin = StrArg(c, 0);
                if (plugin != null) namedPlugins.Add(plugin);
                hits.Add(new(TriageLevel.Note, file, "ipi:" + (plugin ?? "").ToLowerInvariant(),
                    plugin != null ? $"checks whether {plugin} is installed (Game.IsPluginInstalled)"
                                   : "checks whether a plugin is installed (Game.IsPluginInstalled), with a name that isn't written in the script",
                    at, call, plugin ?? "plugin name from a variable"));
                continue;
            }
            if (c.Name.Contains("Console", StringComparison.OrdinalIgnoreCase) || Is(c.Name, "ExecuteCommand") || Is(c.Name, "RunCommand"))
            {
                var cmd = StrArg(c, 0);
                hits.Add(new(TriageLevel.Look, file, "console:" + call.ToLowerInvariant(), $"runs console commands with {call}",
                    at, call, cmd != null ? $"command \"{cmd}\"" : "command from a variable"));
            }
            else if (c.Name.StartsWith("GetINI", StringComparison.OrdinalIgnoreCase) || c.Name.StartsWith("SetINI", StringComparison.OrdinalIgnoreCase))
            {
                bool set = c.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase);
                var key = StrArg(c, 0);
                hits.Add(new(TriageLevel.Look, file, "ini:" + call.ToLowerInvariant(), $"{(set ? "changes" : "reads")} INI settings with {call}",
                    at, call, key != null ? $"setting \"{key}\"" : "setting name from a variable"));
            }
            else if (isGame && c.Name.StartsWith("SetGameSetting", StringComparison.OrdinalIgnoreCase))
            {
                var key = StrArg(c, 0);
                hits.Add(new(TriageLevel.Look, file, "gmst:" + call.ToLowerInvariant(), $"changes game settings with {call}",
                    at, call, key != null ? $"setting \"{key}\"" : "setting name from a variable"));
            }
            else if ((isDebug && DebugToggles.Contains(c.Name)) || (isGame && Is(c.Name, "QuitToMainMenu")))
                hits.Add(new(TriageLevel.Look, file, "debug:" + c.Name.ToLowerInvariant(), $"calls {call}, which works like a console command or ends the game",
                    at, call, string.Join(", ", c.Args.Select(a => a.ToString()))));
            else if (isDebug && (Is(c.Name, "OpenUserLog") || Is(c.Name, "TraceUser")))
            {
                var log = StrArg(c, 0);
                hits.Add(new(TriageLevel.Note, file, "userlog", "writes its own Papyrus log file (Debug.OpenUserLog or TraceUser, under Logs\\Script\\User)",
                    at, call, log != null ? $"log \"{log}\"" : "log name from a variable"));
            }

            if (!c.Caller.IsPropertyAccessor && FrequentEvents.Contains(c.Caller.Name))
            {
                if (TimerCalls.Contains(c.Name))
                {
                    var interval = c.Args.Count > 0 ? Num(c.Args[0]) : "";
                    var unit = c.Name.Contains("GameTime", StringComparison.OrdinalIgnoreCase) ? "game hours" : "seconds";
                    string why = LoopEvents.Contains(c.Caller.Name) ? "the event starts itself again, so it repeats for as long as the script runs"
                        : Is(c.Caller.Name, "OnPlayerLoadGame") ? "it starts again at every game load"
                        : "it starts each time the event fires";
                    hits.Add(new(TriageLevel.Look, file, $"timer:{c.Caller.Name.ToLowerInvariant()}:{c.Name.ToLowerInvariant()}",
                        $"{c.Caller.Name} calls {c.Name}{(interval != "" ? $" with {interval} {unit}" : "")}: {why}",
                        at, call, interval != "" ? $"interval {interval} {unit}" : "interval from a variable"));
                }
                else if (c.Name.StartsWith("RegisterFor", StringComparison.OrdinalIgnoreCase))
                {
                    bool load = Is(c.Caller.Name, "OnPlayerLoadGame");
                    hits.Add(new(load ? TriageLevel.Note : TriageLevel.Look, file, $"register:{c.Caller.Name.ToLowerInvariant()}:{c.Name.ToLowerInvariant()}",
                        load ? $"OnPlayerLoadGame calls {c.Name}: it registers again at every game load"
                             : $"{c.Caller.Name} calls {c.Name}: it registers again each time the event fires", at, call, ""));
                }
            }

            if (gameTypes != null && c.Type != "?" && !ownTypes.Contains(c.Type) && !gameTypes.Contains(c.Type))
                hits.Add(new(TriageLevel.Look, file, "ext:" + c.Type.ToLowerInvariant(),
                    $"calls {c.Type}, a script that is neither in the mod nor in the game (another mod's, or a script extender's)",
                    at, call, ""));
        }

        // plugin and file names written into the script as text (those named in the calls above aren't repeated)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, f, text) in Pex.StringConstants(pex))
        {
            if (!seen.Add(text) || namedPlugins.Contains(text)) continue;
            var at = f != null ? Where(f) : "a variable's starting value";
            if (PluginName.IsMatch(text))
            {
                bool other = Whose(text) == "";
                hits.Add(new(other ? TriageLevel.Look : TriageLevel.Note, file, "pluginname:" + text.ToLowerInvariant(),
                    $"names {Whose(text)}{text} in its text", at, "(text)", text));
            }
            else if (FileName.IsMatch(text) || OutsidePath.IsMatch(text))
            {
                bool look = Runnable.IsMatch(text) || OutsidePath.IsMatch(text);
                hits.Add(new(look ? TriageLevel.Look : TriageLevel.Note, file, "filename:" + text.ToLowerInvariant(),
                    $"names the file \"{text}\" in its text", at, "(text)", text));
            }
        }
    }

    static string FormId(PexValue v) => v.Kind == PexValueKind.Int ? $"0x{v.Int:X6}" : v.Kind == PexValueKind.Ident ? "from a variable" : v.ToString();

    /// <summary>"scripts\foo\bar.pex" (or "x.ba2:scripts\foo\bar.pex") -> "foo:bar", the script type the game knows it as.</summary>
    public static string TypeOfPath(string file)
    {
        var p = file;
        var colon = p.LastIndexOf(".ba2:", StringComparison.OrdinalIgnoreCase);
        if (colon >= 0) p = p[(colon + 5)..];
        p = Ba2Names.Normalize(p);
        var i = p.StartsWith("scripts\\", StringComparison.Ordinal) ? 0 : p.IndexOf("\\scripts\\", StringComparison.Ordinal) is var j and >= 0 ? j + 1 : -1;
        p = i >= 0 ? p[(i + 8)..] : RelPath.FileName(p);
        if (p.EndsWith(".pex", StringComparison.Ordinal)) p = p[..^4];
        return p.Replace('\\', ':');
    }

    // ---- where the scripts are ----

    static List<Source> Gather(TriageContext c, List<TriageFinding> findings)
    {
        var list = new List<Source>();
        foreach (var rel in c.Files)
        {
            var ext = RelPath.Extension(rel);
            if (ext == ".pex")
            {
                var full = c.Full(rel);
                // one script that can't be read is named and skipped, as in an archive (0.8.0 code review)
                try
                {
                    var len = new FileInfo(full).Length;
                    list.Add(len > Pex.MaxBytes
                        ? new Source(rel, null, $"it is {len:N0} bytes, larger than any compiled script")
                        : new Source(rel, File.ReadAllBytes(full), null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { list.Add(new Source(rel, null, $"it couldn't be opened ({e.Message})")); }
            }
            else if (ext == ".ba2")
            {
                try
                {
                    foreach (var (entry, data, error) in ArchiveScripts(c.Full(rel)))
                        list.Add(new Source($"{rel}:{entry}", data, error));
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)   // EndOfStream is an IOException; a locked archive costs only itself (0.8.0 code review)
                {
                    findings.Add(new(TriageLevel.Look, S, rel, $"its scripts couldn't be listed, so none were read from it: {e.Message}"));
                }
            }
        }
        return list;
    }

    /// <summary>The .pex entries of a general (GNRL) archive with their unpacked bytes; a texture (DX10) archive has
    /// none. The file records (36 bytes each: offset at 16, packed size at 24, size at 28) follow the header (24
    /// bytes, plus 8 in version 2 and 12 in version 3, whose last 4 name the compression: 3 = LZ4 block, else zlib).
    /// Both are unpacked (LZ4 since 0.9.1); a damaged entry comes back with an error and the rest are still read. Throws InvalidDataException when the header or records don't add up.</summary>
    public static List<(string Entry, byte[]? Data, string? Error)> ArchiveScripts(string path)
    {
        var name = RelPath.FileName(path);
        var names = Ba2Names.Read(path);  // checks the header and the name table
        var result = new List<(string, byte[]?, string?)>();
        if (!names.Any(n => n.EndsWith(".pex", StringComparison.Ordinal))) return result;
        using var f = File.OpenRead(path);
        var head = new byte[36];
        f.ReadExactly(head, 0, 24);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        var type = System.Text.Encoding.ASCII.GetString(head, 8, 4);
        var nameOfs = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
        if (type != "GNRL") return result;
        uint method = 0;
        int extra = version switch { 2 => 8, 3 => 12, _ => 0 };
        if (extra > 0)
        {
            f.ReadExactly(head, 24, extra);
            if (version == 3) method = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32));
        }
        long recStart = 24 + extra;
        if (recStart + 36L * names.Count > nameOfs) throw new InvalidDataException($"{name}: its file records run into the name table");
        var rec = new byte[36L * names.Count];
        f.Seek(recStart, SeekOrigin.Begin);
        f.ReadExactly(rec);
        for (int i = 0; i < names.Count; i++)
        {
            if (!names[i].EndsWith(".pex", StringComparison.Ordinal)) continue;
            var r = rec.AsSpan(36 * i, 36);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(r[24..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
            var stored = packed != 0 ? packed : size;
            if (size > Pex.MaxBytes || stored > Pex.MaxBytes) { result.Add((names[i], null, $"it says it is {Math.Max(size, stored):N0} bytes, larger than any compiled script")); continue; }
            if (offset > (ulong)f.Length || offset + stored > (ulong)f.Length) { result.Add((names[i], null, $"its data at byte {offset} runs past the end of {name}")); continue; }
            var raw = new byte[stored];
            f.Seek((long)offset, SeekOrigin.Begin);
            f.ReadExactly(raw);
            if (packed == 0) { result.Add((names[i], raw, null)); continue; }
            if (method == 3)
            {
                // an LZ4 block (0.8.0 follow-up: these were named and skipped). First into the size the record says
                // plus one spare byte; a block that doesn't fit is decoded again into a buffer large enough for
                // whatever it holds, so one that unpacks to more than its record says is told as a size mismatch, not
                // as damage (code review of 0.9.1). LZ4 can't grow data more than 255 times, and no compiled script is
                // larger than Pex.MaxBytes, so the second buffer needs no more than either.
                var outBytes = new byte[size + 1];
                var got = Lz4Decode(raw, outBytes);
                if (got < 0)
                {
                    var room = (int)Math.Min(Pex.MaxBytes + 1L, (long)raw.Length * 255 + 16);
                    if (room > outBytes.Length)
                    {
                        var big = new byte[room];
                        var bigGot = Lz4Decode(raw, big);
                        if (bigGot >= 0) got = bigGot;
                        else if (room > Pex.MaxBytes) { result.Add((names[i], null, $"it won't unpack (LZ4: the data is damaged, or it unpacks to more than {Pex.MaxBytes:N0} bytes, larger than any compiled script)")); continue; }
                    }
                }
                if (got < 0) result.Add((names[i], null, "it won't unpack (LZ4: the data is damaged)"));
                else if (got != size) result.Add((names[i], null, $"it unpacks to a different size than the {size:N0} bytes {name} says ({got:N0} bytes)"));
                else result.Add((names[i], outBytes[..(int)size], null));
                continue;
            }
            try
            {
                var outBytes = new byte[size];
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                int got = 0, n;
                while (got < outBytes.Length && (n = z.Read(outBytes, got, outBytes.Length - got)) > 0) got += n;
                if (got != size || z.Read(new byte[1], 0, 1) != 0)
                    result.Add((names[i], null, $"it unpacks to a different size than the {size:N0} bytes {name} says"));
                else
                    result.Add((names[i], outBytes, null));
            }
            catch (InvalidDataException e) { result.Add((names[i], null, $"it won't unpack (zlib: {e.Message})")); }
        }
        return result;
    }

    /// <summary>Decodes one LZ4 block into target: the bytes written, or -1 when it is damaged or doesn't fit.</summary>
    static int Lz4Decode(byte[] raw, byte[] target)
    {
        try { return K4os.Compression.LZ4.LZ4Codec.Decode(raw, target); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException) { return -1; }
    }

    // ---- the game's own scripts ----

    static readonly Dictionary<string, HashSet<string>> gameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The script types in the game's own archives ("Starfield - *.ba2" and the official plugins' archives),
    /// by name from their name tables only.</summary>
    public static HashSet<string> GameScriptTypes(string dataDir)
    {
        var key = Path.GetFullPath(dataDir);
        lock (gameCache)
        {
            if (gameCache.TryGetValue(key, out var hit)) return hit;
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int archives = 0;
            foreach (var ba2 in Directory.EnumerateFiles(dataDir, "*.ba2", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 }))
            {
                var n = RelPath.FileName(ba2);
                var dash = n.IndexOf(" - ", StringComparison.Ordinal);
                var owner = dash > 0 ? n[..dash] + ".esm" : "";
                if (!n.StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase) && !(owner != "" && Checker.IsOfficial(owner))) continue;
                if (Ba2Names.Type(ba2) != "GNRL") continue;
                archives++;
                foreach (var e in Ba2Names.Read(ba2))
                    if (e.EndsWith(".pex", StringComparison.Ordinal)) types.Add(TypeOfPath(e));
            }
            Log.Write($"scripts: {types.Count} script types in {archives} of the game's general archives");
            gameCache[key] = types;
            return types;
        }
    }
}
