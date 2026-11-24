// An MO2 (Mod Organizer 2) profile as a load order (0.16.0, for conflict, patch and find): MO2 keeps every mod in its
// own folder and shows the game one merged Data folder only while the game runs, so without this the tools saw only
// the real Data folder and missed every mod. The rules follow sf-load-order 0.2.2's from_mo2 (its README, "MO2"):
//   - ModOrganizer.ini: selected_profile (the default profile), gamePath, and profiles_directory, mod_directory and
//     overwrite_directory with %BASE_DIR% replaced by base_directory (else the instance folder); @ByteArray(...) is
//     unwrapped, and the first value of a key wins.
//   - The profile's loadorder.txt gives the order when it is there; plugins.txt gives the on/off marks ('*').
//   - Files are looked up in Overwrite first, then each enabled mod folder from the top of modlist.txt down (the top
//     line wins in MO2), then Data. Disabled mods ('-'), separators and unmanaged lines ('*') are skipped.
// Read only: nothing in the instance is written.
using System.Text.RegularExpressions;
using SfModCheck.Conflict;

namespace SfModCheck;

public sealed class Mo2Profile
{
    /// <summary>How an MO2 load order's source line starts (the report's load-order line).</summary>
    public const string SourcePrefix = "MO2 profile ";

    public string Instance { get; private init; } = "";
    public string Profile { get; private init; } = "";
    public string ProfileDir { get; private init; } = "";
    public string PluginsTxt { get; private init; } = "";
    /// <summary>MO2's gamePath (the game's folder), if the ini has one.</summary>
    public string? GamePath { get; private init; }
    /// <summary>Folders whose files win over Data's, highest priority first: Overwrite, then the enabled mods from the
    /// top of modlist.txt down. Only folders that exist.</summary>
    public List<string> ModDirs { get; } = new();
    /// <summary>Load-order problems found in the profile (an enabled mod with no folder, no modlist.txt).</summary>
    public List<string> Problems { get; } = new();
    public LoadOrderSetup.PluginOrder Order { get; private set; } = new([], new(StringComparer.OrdinalIgnoreCase), "");

    static readonly Regex IniLine = new(@"^\s*([^=\[;#]+?)\s*=\s*(.*)$", RegexOptions.CultureInvariant);
    static readonly Regex ByteArray = new(@"^@ByteArray\((.*)\)$", RegexOptions.CultureInvariant);

    /// <summary>{key: value} from ModOrganizer.ini, keys in any case; QSettings' escapes undone (doubled backslashes,
    /// quotes, \xNN for non-ASCII text) and @ByteArray(...) unwrapped. The first value of a key wins. A missing ini gives an empty map.</summary>
    public static Dictionary<string, string> ReadIni(string instance)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(instance, "ModOrganizer.ini");
        if (!File.Exists(path)) { Log.Write($"MO2: no {path}"); return map; }
        foreach (var line in File.ReadAllLines(path))
        {
            var m = IniLine.Match(line);
            if (!m.Success) continue;
            var v = Unescape(m.Groups[2].Value.Trim());
            if (ByteArray.Match(v) is { Success: true } b) v = Utf8FromBytes(b.Groups[1].Value);
            else if (v.StartsWith("@@", StringComparison.Ordinal)) v = v[1..];
            map.TryAdd(m.Groups[1].Value, v);
        }
        return map;
    }

    /// <summary>Undoes QSettings' ini escaping: quotes (QSettings quotes a value with a comma, a semicolon or spaces at
    /// either end) are dropped, \\ \" \' \t \n \r are unescaped, and \xNNNN gives the character with that code, which
    /// is how Qt 5's MO2 writes non-ASCII text (C:/Users/J\xf6rg).</summary>
    static string Unescape(string v)
    {
        var sb = new System.Text.StringBuilder(v.Length);
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (c == '"') continue;
            if (c != '\\' || i + 1 >= v.Length) { sb.Append(c); continue; }
            var e = v[++i];
            if (e == 'x')
            {
                var j = i + 1;
                var code = 0;
                while (j < v.Length && j - i <= 4 && Uri.IsHexDigit(v[j])) code = code * 16 + Convert.ToInt32(v[j++].ToString(), 16);
                if (j == i + 1) { sb.Append('\\').Append('x'); continue; }
                sb.Append((char)code);
                i = j - 1;
                continue;
            }
            sb.Append(e switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => e });
        }
        return sb.ToString();
    }

    /// <summary>A @ByteArray's text holds the value's UTF-8 bytes one per character (Qt 5 writes the bytes above 0x7F
    /// as \xNN); decode them. Text that isn't such bytes (a file saved as UTF-8 text) is kept as it is.</summary>
    static string Utf8FromBytes(string s)
    {
        if (s.All(ch => ch < 0x80) || s.Any(ch => ch > 0xFF)) return s;
        try { return new System.Text.UTF8Encoding(false, true).GetString(s.Select(ch => (byte)ch).ToArray()); }
        catch (System.Text.DecoderFallbackException) { return s; }
    }

    /// <summary>A single folder name: not empty, no folder separators, not "." or "..", no characters Windows refuses
    /// in a file name (so a modlist.txt or --profile value can't point outside the folder it names a child of).</summary>
    public static bool IsFolderName(string name) =>
        name.Length > 0 && name.Trim() == name && name != "." && name != ".." && name.IndexOfAny(BadNameChars) < 0;

    static readonly char[] BadNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    static string IniDir(Dictionary<string, string> ini, string key, string instance, string fallback)
    {
        var baseDir = ini.TryGetValue("base_directory", out var b) && b.Length > 0 ? b : instance;
        // MO2's own default for a missing key is %BASE_DIR%/<fallback>, so a moved base directory moves these too.
        if (!ini.TryGetValue(key, out var v) || v.Length == 0) v = Path.Combine(baseDir, fallback);
        else v = v.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(instance, v));
    }

    /// <summary>[(name, enabled)] from modlist.txt, top line (highest priority) first; separators and unmanaged
    /// entries left out.</summary>
    public static List<(string Name, bool On)> ReadModlist(string path)
    {
        var mods = new List<(string, bool)>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length < 2 || line[0] is not ('+' or '-') || line[1..].EndsWith("_separator", StringComparison.Ordinal)) continue;
            mods.Add((line[1..], line[0] == '+'));
        }
        return mods;
    }

    /// <summary>Reads an MO2 profile. Null (with the message for the user in error) when it can't be used.</summary>
    public static Mo2Profile? Read(string instance, string? profileOption, out string error)
    {
        error = "";
        instance = Path.GetFullPath(instance);
        if (!Directory.Exists(instance))
        {
            error = $"there is no MO2 folder at {instance}. Give --mo2 the MO2 instance folder, the one that holds ModOrganizer.ini, profiles and mods.";
            return null;
        }
        var ini = ReadIni(instance);
        var iniPath = Path.Combine(instance, "ModOrganizer.ini");
        var profile = profileOption ?? ini.GetValueOrDefault("selected_profile");
        var profiles = IniDir(ini, "profiles_directory", instance, "profiles");
        if (string.IsNullOrEmpty(profile))
        {
            error = $"no --profile given and no profile selected in {iniPath}. Run again with --profile <name> (a folder in {profiles}).";
            return null;
        }
        if (!IsFolderName(profile))
        {
            error = $"the MO2 profile name \"{SafeText.OneLine(profile)}\" (from {(profileOption != null ? "--profile" : iniPath)}) isn't a single folder name. " +
                    $"Run again with --profile and one of the folder names in {profiles}.";
            return null;
        }
        var pdir = Path.Combine(profiles, profile);
        var ptxt = Path.Combine(pdir, "plugins.txt");
        if (!Directory.Exists(pdir))
        {
            error = $"there is no MO2 profile folder {pdir}. Run again with --profile and one of the folder names in {profiles}.";
            return null;
        }
        if (!File.Exists(ptxt))
        {
            error = $"there is no plugins.txt in the MO2 profile folder {pdir}. Open the profile in MO2 once so it writes the file, or pick another profile with --profile.";
            return null;
        }
        var game = ini.GetValueOrDefault("gamePath");
        var mo = new Mo2Profile { Instance = instance, Profile = profile, ProfileDir = pdir, PluginsTxt = ptxt, GamePath = string.IsNullOrEmpty(game) ? null : game };
        var modsDir = IniDir(ini, "mod_directory", instance, "mods");
        var overwrite = IniDir(ini, "overwrite_directory", instance, "overwrite");
        Log.Write($"MO2: instance {instance}, profile {profile} ({(profileOption != null ? "from --profile" : "selected_profile in ModOrganizer.ini")}), mods {modsDir}, overwrite {overwrite}");

        var modlist = Path.Combine(pdir, "modlist.txt");
        if (Directory.Exists(overwrite)) mo.ModDirs.Add(overwrite);   // MO2's Overwrite folder wins over every mod
        if (File.Exists(modlist))
        {
            foreach (var (name, on) in ReadModlist(modlist))
            {
                if (!on) continue;
                if (!IsFolderName(name))
                {
                    mo.Problems.Add($"MO2 mod \"{name}\" in modlist.txt isn't a single folder name, so its folder wasn't read; remove that line or let MO2 write the file again");
                    continue;
                }
                var folder = Path.Combine(modsDir, name);
                if (Directory.Exists(folder)) mo.ModDirs.Add(folder);
                else mo.Problems.Add($"MO2 mod {name} is enabled but has no folder in {modsDir}; reinstall it in MO2 or disable it");
            }
            Log.Write($"MO2: {mo.ModDirs.Count} folders win over Data (Overwrite first, then enabled mods from the top of modlist.txt)");
        }
        else mo.Problems.Add($"no modlist.txt in {pdir}, so no mod folders were searched (only Overwrite and Data); open the profile in MO2 once so it writes the file");

        // loadorder.txt holds the order (turned-off plugins too); plugins.txt holds the on/off marks.
        var marks = LoadOrderSetup.ReadPluginsTxt(ptxt);
        var active = new HashSet<string>(marks.Listed, StringComparer.OrdinalIgnoreCase);
        var lopath = Path.Combine(pdir, "loadorder.txt");
        List<string> listed;
        if (File.Exists(lopath))
        {
            var order = File.ReadAllLines(lopath).Select(l => l.Trim().TrimStart('*').Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            var known = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
            // MO2 doesn't write the official files it always loads (Bethesda's, Creations) to plugins.txt: they keep
            // their loadorder.txt place unless plugins.txt turns them off (0.16.0 code review round 4).
            listed = order.Where(n => active.Contains(n) || (LoadOrderSetup.IsOfficial(n) && !marks.TurnedOff.Contains(n))).Concat(marks.Listed.Where(n => !known.Contains(n))).ToList();
            Log.Write($"MO2: order from {lopath}, on/off from {ptxt}");
        }
        else
        {
            listed = marks.Listed;
            Log.Write($"MO2: no loadorder.txt; order and on/off from {ptxt}");
        }
        mo.Order = new(listed, marks.TurnedOff, $"{SourcePrefix}{profile} ({instance}), {listed.Count} active entries");
        return mo;
    }
}
