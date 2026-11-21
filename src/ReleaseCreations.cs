// sfmodcheck release --profile creations (0.10.0): what stands in the way of a Bethesda Creation for PC, Xbox and
// PlayStation. Runs only in the creations profile; the basic rules run as well, and house rules stay WARNINGs.
// The rules (written for sf-mod-check from the author's earlier Creations readiness check):
//   creations-ini        PROBLEM  an INI file (Creations take none, on any platform)
//   creations-sfse       PROBLEM  a DLL, EXE or ASI, or anything in an SFSE folder (consoles can't run them)
//   creations-framework  PROBLEM  a compiled script that names a script from an SFSE plugin (data\creations-masters.txt
//                                 [frameworks]); found in the script's string table, so calls and casts both count.
//                                 WARNING instead when the same script calls Game.IsPluginInstalled (user, 2026-11-21),
//                                 unless every name it tests is an official plugin's, given as a string (0.10.1)
//   creations-master     PROBLEM  a master that isn't Bethesda's and isn't known to be on Creations
//   creations-xbox       PROBLEM  a PC archive with textures and no "<name>_xbox.ba2" twin (the Creation Kit's Xbox
//                                 pack refuses PC textures); WARNING for any other archive with no twin
//   creations-audio      WARNING  .wem or .bnk sound, in an archive or loose: PC-format audio is silent on consoles
//                                 until the Creation Kit makes their versions from WAV sources
//   creations-size       PROBLEM  a zip's plugins and archives over 2 GB (each zip is its own Creation)
//   creations-debug      WARNING  Debug.Notification or Debug.MessageBox the player would see: the Debug script does
//                                 nothing on Xbox and PlayStation. Read from the compiled scripts (never the source). A
//                                 call counts as debug-only when its function's name mentions debug, trace or verbose,
//                                 or when it sits inside an If whose test reads something so named (a debug switch),
//                                 not negated.
using System.Text.RegularExpressions;

namespace SfModCheck;

public static class ReleaseCreations
{
    public const long SizeLimit = 2L * 1024 * 1024 * 1024;
    static readonly string[] SoundExts = [".wem", ".bnk"];
    // programs and loaders no console runs (.asi: a plugin loader's DLL; code review)
    static readonly string[] ProgramExts = [".dll", ".exe", ".asi"];
    static readonly Regex DebugWord = new("debug|trace|verbose", RegexOptions.IgnoreCase);

    /// <summary>The embedded data\creations-masters.txt: its [official] patterns, [masters] and [frameworks].</summary>
    public sealed record MasterList(List<string> Official, List<string> Masters, List<string> Frameworks);

    // Lazy: safe if two threads ask first (code review round 2); PublicationOnly so a failed load isn't cached (round 3)
    static readonly Lazy<MasterList> list = new(() => Parse(ReadEmbedded()), LazyThreadSafetyMode.PublicationOnly);

    static string Files(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    public static MasterList List => list.Value;

    static string ReadEmbedded()
    {
        using var s = typeof(ReleaseCreations).Assembly.GetManifestResourceStream("creations-masters.txt")
            ?? throw new InvalidOperationException("the built-in data file creations-masters.txt is missing from this build");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static MasterList Parse(string text)
    {
        var m = new MasterList(new(), new(), new());
        List<string>? into = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                into = line.ToLowerInvariant() switch { "[official]" => m.Official, "[masters]" => m.Masters, "[frameworks]" => m.Frameworks, _ => null };
                continue;
            }
            into?.Add(line);
        }
        return m;
    }

    /// <summary>True for a master a Creation may require: Bethesda's own (by pattern), one listed in [masters], or one
    /// given with --creations-master.</summary>
    public static bool Allowed(string master, IEnumerable<string> extra) =>
        IsOfficial(master)
        || List.Masters.Concat(extra).Any(x => x.Equals(master, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a master that matches one of the [official] patterns (built once; code review).</summary>
    public static bool IsOfficial(string master) => OfficialPatterns.Value.Any(r => r.IsMatch(master));

    static readonly Lazy<List<Regex>> OfficialPatterns = new(() => List.Official.Select(Wildcard).ToList(), LazyThreadSafetyMode.PublicationOnly);

    static Regex Wildcard(string p) => new("^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    /// <summary>The Xbox twin's file name of a PC archive: "MyMod - Main.ba2" gives "MyMod - Main_xbox.ba2".</summary>
    public static string XboxTwin(string archive) => Path.GetFileNameWithoutExtension(archive) + "_xbox.ba2";

    /// <summary>The PC archive's file name of an Xbox twin: "MyMod - Main_xbox.ba2" gives "MyMod - Main.ba2".</summary>
    public static string PcTwin(string xboxArchive)
    {
        var stem = Path.GetFileNameWithoutExtension(xboxArchive);
        return stem[..^"_xbox".Length] + ".ba2";
    }

    /// <summary>The full path of a PC archive's Xbox twin: the same folder (code review round 3: two folders of a zip
    /// can hold archives of the same name, and each pairs only with its own folder's twin).</summary>
    static string XboxTwinPath(string archive) => Path.Combine(Path.GetDirectoryName(archive) ?? "", XboxTwin(archive));

    /// <summary>The full path of an Xbox twin's PC archive, in the same folder.</summary>
    static string PcTwinPath(string xboxArchive) => Path.Combine(Path.GetDirectoryName(xboxArchive) ?? "", PcTwin(xboxArchive));

    /// <summary>An archive as messages name it: its path in the zip ("Opt - Main.ba2" at the top, "OptionA\Opt - Main.ba2"
    /// in a folder), so archives of the same name in two folders can be told apart (code review round 4).</summary>
    static string ArchiveLabel(ReleasePackage p, string archive) => Path.GetRelativePath(p.Root, archive);

    public static bool IsXbox(string archive) => Path.GetFileNameWithoutExtension(archive).EndsWith("_xbox", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        if (c.Options.Profile != "creations") return [];
        var f = new List<ReleaseFinding>();
        void Add(Level l, string rule, string text) { text = OneLine(text); f.Add(new(l, rule, text)); Log.Write($"creations: {ReleaseCommand.Label(l).Trim()} [{rule}] {text}"); }

        foreach (var p in c.Packages)
        {
            // each zip is its own Creation, so the 2 GB limit is per zip (code review)
            long total = 0;
            // files no platform takes
            var sfse = new List<string>();
            foreach (var rel in p.Files)
            {
                if (rel.EndsWith(".ini", StringComparison.Ordinal))
                    Add(Level.Problem, "creations-ini", $"{p.Name}:{rel}: an INI file. Creations take no INI files on any platform; set the values from the plugin or a script instead.");
                if (ProgramExts.Any(e => rel.EndsWith(e, StringComparison.Ordinal)) || ("\\" + rel).Contains("\\sfse\\", StringComparison.Ordinal))
                    sfse.Add(rel);
                if (SoundExts.Any(e => rel.EndsWith(e, StringComparison.Ordinal)))
                    Add(Level.Warning, "creations-audio", $"{p.Name}:{rel}: a loose sound file. PC-format audio is silent on Xbox and PlayStation; let the Creation Kit make their versions from WAV sources, or offer the Creation on PC only.");
            }
            if (sfse.Count > 0)
                Add(Level.Problem, "creations-sfse", $"{p.Name}: {sfse.Count} SFSE or program file{(sfse.Count == 1 ? "" : "s")} (first: {sfse[0]}). Creations can't run SFSE plugins or programs on any platform; a mod that needs them stays a PC (Nexus) mod.");

            // masters
            foreach (var plugin in p.Plugins)
            {
                var name = Path.GetFileName(plugin);
                RawHeader? h;
                try { h = RawHeader.Read(plugin); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { h = null; Log.Write($"creations: {p.Name}:{name}: header not read ({e.Message})"); }
                if (h == null) continue;   // the plugin check reports a plugin it can't read
                Log.Write($"creations: {p.Name}:{name}: masters {string.Join(", ", h.Masters)}");
                foreach (var m in h.Masters)
                {
                    if (IsOfficial(m)) continue;
                    if (Allowed(m, c.Options.CreationsMasters))
                        Add(Level.Note, "creations-master", $"{p.Name}:{name}: master {m} is allowed as a Creation ({(c.Options.CreationsMasters.Contains(m, StringComparer.OrdinalIgnoreCase) ? "--creations-master" : "the built-in list")}).");
                    else
                        Add(Level.Problem, "creations-master", $"{p.Name}:{name}: master {m} isn't known to be on Creations. A Creation may only require Bethesda's plugins and other Creations; make it optional in code (Game.IsPluginInstalled), or, once you have seen {m} on Creations for every platform, run again with --creations-master {m}.");
                }
                total += new FileInfo(plugin).Length;
            }

            // archives: Xbox twins, textures, sound, frameworks named in compiled scripts
            // keyed by full path: two folders of a zip can hold archives of the same name (code review round 3)
            var paths = p.Archives.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // each archive's file list, read once; null when it can't be read (ba2-unreadable reports it)
            var lists = new Dictionary<string, List<string>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in p.Archives)
            {
                try { lists[a] = Ba2Names.Read(a); }
                // UnauthorizedAccessException too, as ReleaseRules' ba2-unreadable does (code review round 5)
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { lists[a] = null; Log.Write($"creations: {p.Name}:{ArchiveLabel(p, a)}: not read ({e.Message})"); }
            }
            int SoundIn(string path) => lists.GetValueOrDefault(path) is { } l ? l.Count(n => SoundExts.Any(e => n.EndsWith(e, StringComparison.Ordinal))) : 0;
            foreach (var a in p.Archives)
            {
                var name = Path.GetFileName(a);   // for the twin rules
                var an = ArchiveLabel(p, a);       // for messages: the path in the zip, so two folders' archives differ (round 4)
                total += new FileInfo(a).Length;
                if (lists[a] is not { } inside)
                {
                    // an unreadable PC archive still needs its Xbox twin (round 4); its textures can't be counted, and the
                    // message says so (round 5). ba2-unreadable already reports every unreadable archive as a PROBLEM,
                    // an Xbox one too, so an unreadable Xbox archive gets only a log line here.
                    if (!IsXbox(name) && !paths.Contains(XboxTwinPath(a)))
                        Add(Level.Warning, "creations-xbox", $"{p.Name}:{an}: no {XboxTwin(name)}, so Xbox gets no archive (the Creation Kit warns \"Creation for XBox that has no archives\"), and this archive can't be read, so its textures weren't checked; rebuild it, then build the Xbox archive with Archive2's Xbox settings.");
                    else if (IsXbox(name))
                        Log.Write($"creations: {p.Name}:{an}: Xbox archive not read, so its sound wasn't checked (ba2-unreadable reports it)");
                    continue;
                }
                // sound is counted in every archive, the Xbox one too (code review: copied PC sound there is the case
                // this rule is for); a PC archive and its Xbox twin that both hold sound share one WARNING (round 2)
                int sound = inside.Count(n => SoundExts.Any(e => n.EndsWith(e, StringComparison.Ordinal)));
                bool pairShared = IsXbox(name) ? SoundIn(PcTwinPath(a)) > 0 : SoundIn(XboxTwinPath(a)) > 0;
                if (sound > 0 && IsXbox(name) && pairShared)
                    Log.Write($"creations: {p.Name}:{an}: {sound} sounds, reported with its PC twin {ArchiveLabel(p, PcTwinPath(a))}");
                else if (sound > 0 && pairShared)
                    Add(Level.Warning, "creations-audio", $"{p.Name}:{an} ({Files(sound, "sound file")}) and {ArchiveLabel(p, XboxTwinPath(a))} ({Files(SoundIn(XboxTwinPath(a)), "sound file")}). PC-format audio is silent on Xbox and PlayStation; let the Creation Kit make their versions from WAV sources, or use the game's own sounds.");
                else if (sound > 0)
                    Add(Level.Warning, "creations-audio", $"{p.Name}:{an}: {Files(sound, "sound file")}. PC-format audio is silent on Xbox and PlayStation; let the Creation Kit make their versions from WAV sources, or use the game's own sounds.");
                if (IsXbox(name))
                {
                    Add(Level.Note, "creations-xbox", $"{p.Name}: Xbox archive present: {an}.");
                    Log.Write($"creations: {p.Name}:{an}: {inside.Count} files, {sound} sounds (Xbox archive)");
                    continue;
                }
                var twin = XboxTwin(name);
                int textures = inside.Count(n => n.EndsWith(".dds", StringComparison.Ordinal));
                bool hasTwin = paths.Contains(XboxTwinPath(a));
                if (!hasTwin)
                {
                    if (textures > 0)
                        Add(Level.Problem, "creations-xbox", $"{p.Name}:{an}: {textures} texture{(textures == 1 ? "" : "s")} and no {twin}. The Creation Kit's Xbox pack refuses PC textures (\"Invalid platform\"); build the Xbox archive with Archive2's Xbox settings.");
                    else
                        Add(Level.Warning, "creations-xbox", $"{p.Name}:{an}: no {twin}, so Xbox gets no archive (the Creation Kit warns \"Creation for XBox that has no archives\"); build the Xbox archive with Archive2's Xbox settings.");
                }
                Log.Write($"creations: {p.Name}:{an}: {inside.Count} files, {textures} textures, {sound} sounds, twin {(hasTwin ? "present" : "missing")}");
            }

            // compiled scripts, loose and in archives
            foreach (var (label, data, error) in Scripts(p))
            {
                if (data == null) { Log.Write(OneLine($"creations: {label}: not read ({error})")); continue; }
                PexFile pex;
                try { pex = Pex.Read(data, label); }
                catch (InvalidDataException e) { Log.Write(OneLine($"creations: {label}: not a readable script ({e.Message})")); continue; }
                var fw = Frameworks(pex);
                var names = fw.Count > 0 ? IsPluginInstalledNames(pex) : null;
                var tested = names == null ? null : Targets(names);
                // user, 2026-11-21 (0.10.1, option b): a check that tests only official plugins (every name a string
                // matching [official]) doesn't guard an SFSE framework, so it stays a PROBLEM.
                var officialOnly = names != null && names.All(n => n != null && IsOfficial(n));
                if (names != null) Log.Write(OneLine($"creations: {label}: Game.IsPluginInstalled tests {string.Join(", ", tested!)}{(officialOnly ? " (official plugins only: not a framework guard)" : "")}"));
                if (tested != null && officialOnly)
                    Add(Level.Problem, "creations-framework", $"{label}: names {string.Join(", ", fw)}, which come{(fw.Count == 1 ? "s" : "")} from SFSE plugins. Its Game.IsPluginInstalled calls test only official plugins ({string.Join(", ", tested)}), which doesn't guard the framework. Creations can't run SFSE, so those calls fail; remove them, guard them with Game.IsPluginInstalled on the framework's own plugin in this script, or keep the mod on PC (Nexus).");
                else if (tested != null)
                    // user, 2026-11-21: a script that checks Game.IsPluginInstalled may use the framework only when
                    // it's there (optional in code), so a WARNING to confirm the guard. The text names what the script
                    // tests, so a check for an unrelated plugin is easy to spot (code review round 7). creations-check.py
                    // has the same levels but matches the name anywhere in the script's bytes, not only a real call.
                    Add(Level.Warning, "creations-framework", $"{label}: names {string.Join(", ", fw)}, which come{(fw.Count == 1 ? "s" : "")} from SFSE plugins, and also calls Game.IsPluginInstalled (testing {string.Join(", ", tested)}). Creations can't run SFSE; make sure every framework call runs only when that check finds the framework's plugin.");
                else if (fw.Count > 0)
                    Add(Level.Problem, "creations-framework", $"{label}: names {string.Join(", ", fw)}, which come{(fw.Count == 1 ? "s" : "")} from SFSE plugins. Creations can't run SFSE, so those calls fail; remove them, guard them with Game.IsPluginInstalled in this script (a guard in another script isn't seen), or keep the mod on PC (Nexus).");
                foreach (var (obj, fn) in PlayerDebugCalls(pex))
                    Add(Level.Warning, "creations-debug", $"{label}: {obj}.{fn} calls Debug.Notification or Debug.MessageBox outside debug-only code. The Debug script does nothing on Xbox and PlayStation, so players there see nothing; show a Message record with Show() instead.");
            }
            if (total > SizeLimit)
                Add(Level.Problem, "creations-size", $"{p.Name}: the plugins and archives total {total / (1024.0 * 1024 * 1024):F2} GB, over the 2 GB a Creation may hold.");
            else
                Add(Level.Note, "creations-size", $"{p.Name}: plugins and archives: {(total < 1024 * 1024 ? $"{total / 1024.0:F1} KB" : $"{total / (1024.0 * 1024):F1} MB")} (a Creation may hold 2 GB).");
        }
        return f;
    }

    /// <summary>Every compiled script in the zip: loose .pex files and the .pex entries of its general archives.</summary>
    static IEnumerable<(string Label, byte[]? Data, string? Error)> Scripts(ReleasePackage p)
    {
        foreach (var file in Directory.EnumerateFiles(p.Root, "*", SearchOption.AllDirectories).Where(x => x.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(p.Root, file);
            byte[]? data = null; string? err = null;
            try
            {
                if (new FileInfo(file).Length > Pex.MaxBytes) err = "larger than any compiled script";
                else data = File.ReadAllBytes(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { err = e.Message; }
            yield return ($"{p.Name}:{rel}", data, err);
        }
        // An Xbox archive's script that its PC twin also holds, with the same bytes, is skipped so each finding is
        // reported once (code review). Anything else in the twin is read: a script only it holds, a different copy,
        // or every script when the PC archive can't be read (round 2).
        var read = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);   // PC archive's full path -> entry -> bytes
        foreach (var a in p.Archives.OrderBy(x => IsXbox(Path.GetFileName(x)) ? 1 : 0))   // PC archives first
        {
            var name = Path.GetFileName(a);
            var an = ArchiveLabel(p, a);   // the path in the zip (round 4)
            List<(string Entry, byte[]? Data, string? Error)> s;
            try { s = TriageScripts.ArchiveScripts(a); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { Log.Write($"creations: {p.Name}:{an}: scripts not listed ({e.Message})"); continue; }
            var twin = IsXbox(name) ? read.GetValueOrDefault(PcTwinPath(a)) : null;   // the same folder's PC archive (round 3)
            if (!IsXbox(name)) read[a] = s.Where(x => x.Data != null).GroupBy(x => x.Entry, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Data!, StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            foreach (var (entry, data, err) in s)
            {
                if (twin != null && data != null && twin.TryGetValue(entry, out var pc) && pc.AsSpan().SequenceEqual(data)) { skipped++; continue; }
                yield return ($"{p.Name}:{an}:{entry}", data, err);   // the zip's name too, as loose scripts have (round 5)
            }
            if (skipped > 0) Log.Write($"creations: {p.Name}:{an}: {skipped} script{(skipped == 1 ? "" : "s")} also in its PC twin {ArchiveLabel(p, PcTwinPath(a))}, read there");
        }
    }

    /// <summary>The SFSE framework names a script's string table holds: a name exactly, or as the start of a
    /// namespaced type ("ConsoleAPI:Utils"), compared without case.</summary>
    public static List<string> Frameworks(PexFile pex) =>
        List.Frameworks.Where(fw => pex.Strings.Any(s => s.Equals(fw, StringComparison.OrdinalIgnoreCase)
                                                        || s.StartsWith(fw + ":", StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>What the script's Game.IsPluginInstalled calls test (a callstatic on Game, names compared without
    /// case): each plugin name given as a string, in quotes, and "a name held in a variable" for any other argument;
    /// once each, in code order. Null when the script makes no such call.</summary>
    public static List<string>? IsPluginInstalledTargets(PexFile pex) =>
        IsPluginInstalledNames(pex) is { } names ? Targets(names) : null;

    /// <summary>The raw names the script's Game.IsPluginInstalled calls test, once each in code order, null for a
    /// name held in a variable (0.10.1). Null when the script makes no such call.</summary>
    public static List<string?>? IsPluginInstalledNames(PexFile pex)
    {
        var calls = Pex.Calls(pex).Where(c => c.Kind == "static"
            && c.Type.Equals("Game", StringComparison.OrdinalIgnoreCase)
            && c.Name.Equals("IsPluginInstalled", StringComparison.OrdinalIgnoreCase)).ToList();
        if (calls.Count == 0) return null;
        // Distinct on the names themselves, before Quoted cuts them (round 9: two long names sharing their first
        // 80 characters would otherwise show as one).
        return calls.Select(c => c.Args.Count > 0 && c.Args[0].IsStr ? c.Args[0].Text : null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<string> Targets(List<string?> names) =>
        names.Select(t => t == null ? "a name held in a variable" : Quoted(t)).ToList();

    /// <summary>A string from a script, in quotes, safe for a one-line finding: backslashes and quotes escaped, the
    /// characters <see cref="OneLine"/> escapes escaped the same way, and cut to 80 characters with "..." (never
    /// inside a surrogate pair). Code review round 8: a name with a newline split the WARNING in two.</summary>
    public static string Quoted(string s)
    {
        const int Max = 80;
        var cut = s.Length > Max;
        var keep = cut ? (char.IsHighSurrogate(s[Max - 1]) ? Max - 1 : Max) : s.Length;
        var sb = new System.Text.StringBuilder("\"");
        foreach (var ch in s.AsSpan(0, keep))
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                _ => Escape(ch) ?? ch.ToString(),
            });
        if (cut) sb.Append("...");
        return sb.Append('"').ToString();
    }

    /// <summary>Text for a one-line finding or log line: control characters (Cc), the Unicode line and paragraph
    /// separators (Zl, Zp) and every format character (Cf: the bidi controls such as U+202E and U+061C, zero-width
    /// characters, U+FEFF) are written as \n, \r, \t or \uXXXX, so names taken from a script or a zip can't split a
    /// finding, hide characters or reorder it on screen (code review rounds 9 and 10). Everything else, backslashes
    /// in paths included, stays as it is.</summary>
    public static string OneLine(string s)
    {
        if (!s.Any(c => Escape(c) != null)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var ch in s) sb.Append(Escape(ch) ?? ch.ToString());
        return sb.ToString();
    }

    static string? Escape(char ch) => ch switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ when char.IsControl(ch) => $"\\u{(int)ch:x4}",
        _ => char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.Format ? $"\\u{(int)ch:x4}" : null,
    };

    /// <summary>(object, function) for each Debug.Notification or Debug.MessageBox call the player would see: not in a
    /// function named for debugging, and not inside a block that runs only when a debug switch is on. A switch is
    /// tracked through the instructions that test it: a value read from something whose name mentions debug, trace or
    /// verbose is "on when true"; a not, or a comparison with 0, False or None, turns it around; a jmpf on an "on
    /// when true" value (or a jmpt on a turned-around one) skips the block when the switch is off, so the block is
    /// debug-only.</summary>
    public static List<(string Object, string Function)> PlayerDebugCalls(PexFile pex)
    {
        var result = new List<(string, string)>();
        foreach (var o in pex.Objects)
            foreach (var fn in o.Functions())
            {
                if (fn.Code.Count == 0) continue;
                var fnName = fn.IsPropertyAccessor ? fn.State + "." + fn.Name : fn.Name;
                bool fnGated = DebugWord.IsMatch(fn.Name) || (fn.IsPropertyAccessor && DebugWord.IsMatch(fn.State));
                var gated = new bool[fn.Code.Count];
                var sw = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // variable -> +1 on when true, -1 on when false
                int Of(PexValue v) => !v.IsIdent ? 0 : sw.TryGetValue(v.Text, out var p) ? p : DebugWord.IsMatch(v.Text) ? 1 : 0;
                void Set(PexValue dest, int p) { if (!dest.IsIdent) return; if (p == 0) sw.Remove(dest.Text); else sw[dest.Text] = p; }
                for (int i = 0; i < fn.Code.Count; i++)
                {
                    var ins = fn.Code[i];
                    var a = ins.Args;
                    switch (ins.Name)
                    {
                        case "callmethod" when a.Count >= 3:
                            Set(a[2], DebugWord.IsMatch(a[0].Text) || Of(a[1]) != 0 ? 1 : 0);
                            break;
                        case "callstatic" when a.Count >= 3:
                            Set(a[2], DebugWord.IsMatch(a[1].Text) && !a[0].Text.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                            break;
                        case "propget" when a.Count >= 3:
                            Set(a[2], DebugWord.IsMatch(a[0].Text) || Of(a[1]) != 0 ? 1 : 0);
                            break;
                        case "assign" or "cast" when a.Count >= 2:
                            Set(a[0], Of(a[1]));
                            break;
                        case "not" when a.Count >= 2:
                            Set(a[0], -Of(a[1]));
                            break;
                        case "cmp_eq" when a.Count >= 3:
                        {
                            int l = Of(a[1]), r = Of(a[2]);
                            int p = l != 0 ? l : r;
                            var other = l != 0 ? a[2] : a[1];
                            Set(a[0], p == 0 ? 0 : IsFalseish(other) ? -p : p);
                            break;
                        }
                        case "cmp_gt" or "cmp_ge" or "cmp_lt" or "cmp_le" when a.Count >= 3:
                            Set(a[0], Of(a[1]) != 0 || Of(a[2]) != 0 ? 1 : 0);   // "If Debug_Level > 0"
                            break;
                        case "jmpf" or "jmpt" when a.Count >= 2 && a[1].Kind == PexValueKind.Int:
                        {
                            int p = Of(a[0]);
                            bool onlyWhenOn = ins.Name == "jmpf" ? p > 0 : p < 0;
                            int end = i + a[1].Int;
                            if (onlyWhenOn && end > i)
                                for (int k = i + 1; k < Math.Min(end, fn.Code.Count); k++) gated[k] = true;
                            break;
                        }
                    }
                    if (ins.Name == "callstatic" && a.Count >= 2 && a[0].Text.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                        && (a[1].Text.Equals("Notification", StringComparison.OrdinalIgnoreCase) || a[1].Text.Equals("MessageBox", StringComparison.OrdinalIgnoreCase))
                        && !fnGated && !gated[i])
                        result.Add((o.Name, fnName));
                }
            }
        return result.Distinct().ToList();
    }

    static bool IsFalseish(PexValue v) => v.Kind switch
    {
        PexValueKind.None => true,
        PexValueKind.Int or PexValueKind.Bool => v.Int == 0,
        PexValueKind.Float => v.Float == 0f,
        _ => false,
    };
}
