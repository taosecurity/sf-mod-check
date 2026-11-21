// sfmodcheck release --profile creations (0.10.0): what stands in the way of a Bethesda Creation for PC, Xbox and
// PlayStation. Runs only in the creations profile; the basic rules run as well, and house rules stay WARNINGs.
// The rules (written for sf-mod-check from the author's earlier Creations readiness check):
//   creations-ini        PROBLEM  an INI file (Creations take none, on any platform)
//   creations-sfse       PROBLEM  a DLL or EXE, or anything in an SFSE folder (consoles can't run them)
//   creations-framework  PROBLEM  a compiled script that names a script from an SFSE plugin (data\creations-masters.txt
//                                 [frameworks]); found in the script's string table, so calls and casts both count
//   creations-master     PROBLEM  a master that isn't Bethesda's and isn't known to be on Creations
//   creations-xbox       PROBLEM  a PC archive with textures and no "<name>_xbox.ba2" twin (the Creation Kit's Xbox
//                                 pack refuses PC textures); WARNING for any other archive with no twin
//   creations-audio      WARNING  .wem or .bnk sound, in an archive or loose: PC-format audio is silent on consoles
//                                 until the Creation Kit makes their versions from WAV sources
//   creations-size       PROBLEM  plugins and archives over 2 GB
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
    static readonly Regex DebugWord = new("debug|trace|verbose", RegexOptions.IgnoreCase);

    /// <summary>The embedded data\creations-masters.txt: its [official] patterns, [masters] and [frameworks].</summary>
    public sealed record MasterList(List<string> Official, List<string> Masters, List<string> Frameworks);

    static MasterList? list;

    public static MasterList List => list ??= Parse(ReadEmbedded());

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
        List.Official.Any(p => Wildcard(p).IsMatch(master))
        || List.Masters.Concat(extra).Any(x => x.Equals(master, StringComparison.OrdinalIgnoreCase));

    static Regex Wildcard(string p) => new("^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    /// <summary>The Xbox twin's file name of a PC archive: "MyMod - Main.ba2" gives "MyMod - Main_xbox.ba2".</summary>
    public static string XboxTwin(string archive) => Path.GetFileNameWithoutExtension(archive) + "_xbox.ba2";

    public static bool IsXbox(string archive) => Path.GetFileNameWithoutExtension(archive).EndsWith("_xbox", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        if (c.Options.Profile != "creations") return [];
        var f = new List<ReleaseFinding>();
        void Add(Level l, string rule, string text) { f.Add(new(l, rule, text)); Log.Write($"creations: {ReleaseCommand.Label(l).Trim()} [{rule}] {text}"); }

        long total = 0;
        foreach (var p in c.Packages)
        {
            // files no platform takes
            var sfse = new List<string>();
            foreach (var rel in p.Files)
            {
                if (rel.EndsWith(".ini", StringComparison.Ordinal))
                    Add(Level.Problem, "creations-ini", $"{p.Name}:{rel}: an INI file. Creations take no INI files on any platform; set the values from the plugin or a script instead.");
                if (rel.EndsWith(".dll", StringComparison.Ordinal) || rel.EndsWith(".exe", StringComparison.Ordinal) || ("\\" + rel).Contains("\\sfse\\", StringComparison.Ordinal))
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
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { h = null; Log.Write($"creations: {name}: header not read ({e.Message})"); }
                if (h == null) continue;   // the plugin check reports a plugin it can't read
                Log.Write($"creations: {p.Name}:{name}: masters {string.Join(", ", h.Masters)}");
                foreach (var m in h.Masters)
                {
                    if (List.Official.Any(o => Wildcard(o).IsMatch(m))) continue;
                    if (Allowed(m, c.Options.CreationsMasters))
                        Add(Level.Note, "creations-master", $"{name}: master {m} is allowed as a Creation ({(c.Options.CreationsMasters.Contains(m, StringComparer.OrdinalIgnoreCase) ? "--creations-master" : "the built-in list")}).");
                    else
                        Add(Level.Problem, "creations-master", $"{name}: master {m} isn't known to be on Creations. A Creation may only require Bethesda's plugins and other Creations; make it optional in code (Game.IsPluginInstalled), or, once you have seen {m} on Creations for every platform, run again with --creations-master {m}.");
                }
                total += new FileInfo(plugin).Length;
            }

            // archives: Xbox twins, textures, sound, frameworks named in compiled scripts
            var names = p.Archives.Select(a => Path.GetFileName(a)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in p.Archives)
            {
                var an = Path.GetFileName(a);
                total += new FileInfo(a).Length;
                List<string> inside;
                try { inside = Ba2Names.Read(a); }
                catch (Exception e) when (e is InvalidDataException or IOException) { Log.Write($"creations: {an}: not read ({e.Message})"); continue; }   // ba2-unreadable reports it
                if (IsXbox(an)) { Add(Level.Note, "creations-xbox", $"{p.Name}: Xbox archive present: {an}."); continue; }
                var twin = XboxTwin(an);
                int textures = inside.Count(n => n.EndsWith(".dds", StringComparison.Ordinal));
                if (!names.Contains(twin))
                {
                    if (textures > 0)
                        Add(Level.Problem, "creations-xbox", $"{p.Name}:{an}: {textures} texture{(textures == 1 ? "" : "s")} and no {twin}. The Creation Kit's Xbox pack refuses PC textures (\"Invalid platform\"); build the Xbox archive with Archive2's Xbox settings.");
                    else
                        Add(Level.Warning, "creations-xbox", $"{p.Name}:{an}: no {twin}, so Xbox gets no archive (the Creation Kit warns \"Creation for XBox that has no archives\"); build the Xbox archive with Archive2's Xbox settings.");
                }
                int sound = inside.Count(n => SoundExts.Any(e => n.EndsWith(e, StringComparison.Ordinal)));
                if (sound > 0)
                    Add(Level.Warning, "creations-audio", $"{p.Name}:{an}: {sound} sound file{(sound == 1 ? "" : "s")}. PC-format audio is silent on Xbox and PlayStation; let the Creation Kit make their versions from WAV sources, or use the game's own sounds.");
                Log.Write($"creations: {an}: {inside.Count} files, {textures} textures, {sound} sounds, twin {(names.Contains(twin) ? "present" : "missing")}");
            }

            // compiled scripts, loose and in archives
            foreach (var (label, data, error) in Scripts(p))
            {
                if (data == null) { Log.Write($"creations: {label}: not read ({error})"); continue; }
                PexFile pex;
                try { pex = Pex.Read(data, label); }
                catch (InvalidDataException e) { Log.Write($"creations: {label}: not a readable script ({e.Message})"); continue; }
                var fw = Frameworks(pex);
                if (fw.Count > 0)
                    Add(Level.Problem, "creations-framework", $"{label}: names {string.Join(", ", fw)}, which come{(fw.Count == 1 ? "s" : "")} from SFSE plugins. Creations can't run SFSE, so those calls fail; remove them, or keep the mod on PC (Nexus).");
                foreach (var (obj, fn) in PlayerDebugCalls(pex))
                    Add(Level.Warning, "creations-debug", $"{label}: {obj}.{fn} calls Debug.Notification or Debug.MessageBox outside debug-only code. The Debug script does nothing on Xbox and PlayStation, so players there see nothing; show a Message record with Show() instead.");
            }
        }
        if (total > SizeLimit)
            Add(Level.Problem, "creations-size", $"the plugins and archives total {total / (1024.0 * 1024 * 1024):F2} GB, over the 2 GB a Creation may hold.");
        else
            Add(Level.Note, "creations-size", $"plugins and archives: {(total < 1024 * 1024 ? $"{total / 1024.0:F1} KB" : $"{total / (1024.0 * 1024):F1} MB")} (a Creation may hold 2 GB).");
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
        foreach (var a in p.Archives)
        {
            List<(string Entry, byte[]? Data, string? Error)> s;
            try { s = TriageScripts.ArchiveScripts(a); }
            catch (Exception e) when (e is InvalidDataException or IOException) { Log.Write($"creations: {Path.GetFileName(a)}: scripts not listed ({e.Message})"); continue; }
            foreach (var (entry, data, err) in s) yield return ($"{Path.GetFileName(a)}:{entry}", data, err);
        }
    }

    /// <summary>The SFSE framework names a script's string table holds: a name exactly, or as the start of a
    /// namespaced type ("ConsoleAPI:Utils"), compared without case.</summary>
    public static List<string> Frameworks(PexFile pex) =>
        List.Frameworks.Where(fw => pex.Strings.Any(s => s.Equals(fw, StringComparison.OrdinalIgnoreCase)
                                                        || s.StartsWith(fw + ":", StringComparison.OrdinalIgnoreCase))).ToList();

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
