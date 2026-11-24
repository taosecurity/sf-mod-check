// The load order a read-only command looks at (0.16.0, used by find, conflict and patch): the options --plugins,
// --data, --extra-data, --mo2 and --profile, their defaults (Steam's Data folder, the game's own Plugins.txt) and the
// checks on them, in one place. With --mo2 the order comes from an MO2 profile (Mo2Profile.cs) and the plugins,
// archives and loose files are looked up in --extra-data, then MO2's Overwrite, then the enabled mods from the top of
// modlist.txt down, then Data. patch check and triage still read their own options.
using SfModCheck.Conflict;

namespace SfModCheck;

public sealed class LoadOrderInput
{
    /// <summary>The Plugins.txt the order comes from (with --mo2: the profile's plugins.txt).</summary>
    public string? PluginsTxt { get; private set; }
    public string DataDir { get; private set; } = "";
    public string? ExtraDir { get; private set; }
    /// <summary>The MO2 profile, with --mo2 (after Resolve).</summary>
    public Mo2Profile? Mo2 { get; private set; }
    /// <summary>With --plugin (0.16.1, find only): the plugin's full path (after Resolve); the load order is then its
    /// masters (each master's own masters first) and the plugin, with no Plugins.txt.</summary>
    public string? Plugin { get; private set; }
    /// <summary>--masters-dir folders, in the order given (full paths after Resolve).</summary>
    public List<string> MastersDirs { get; } = new();
    string? dataOption, mo2Option, profileOption;
    PluginOrderOfOne? single;
    readonly bool allowPlugin;

    /// <param name="allowPlugin">take --plugin and --masters-dir (find, 0.16.1); other commands leave them to their own
    /// unknown-option message</param>
    public LoadOrderInput(bool allowPlugin = false) => this.allowPlugin = allowPlugin;

    sealed record PluginOrderOfOne(LoadOrderSetup.PluginOrder Order, List<string> Dirs);

    /// <summary>The game's own load order file (the game writes it; Vortex and the in-game Creations menu update it).</summary>
    public static string GamePluginsTxt() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starfield", "Plugins.txt");

    /// <summary>Takes a[i] if it is a load-order option: true when taken (i is moved past its value), false when its
    /// value is missing (the error is printed), null when a[i] isn't a load-order option.</summary>
    public bool? TryOption(string[] a, ref int i, string usage)
    {
        switch (a[i])
        {
            case "--plugins":
                if (Cli.Value(a, ref i, "a Plugins.txt file", usage) is not { } p) return false;
                PluginsTxt = p; return true;
            case "--data":
                if (Cli.Value(a, ref i, "the game's Data folder", usage) is not { } d) return false;
                dataOption = d; return true;
            case "--extra-data":
                if (Cli.Value(a, ref i, "a folder", usage) is not { } x) return false;
                ExtraDir = x; return true;
            case "--mo2":
                if (Cli.Value(a, ref i, "the MO2 instance folder (the one with ModOrganizer.ini)", usage) is not { } m) return false;
                mo2Option = m; return true;
            case "--profile":
                if (Cli.Value(a, ref i, "an MO2 profile name", usage) is not { } pr) return false;
                profileOption = pr; return true;
            case "--plugin" when allowPlugin:
                if (Cli.Value(a, ref i, "a plugin file (.esm, .esp or .esl)", usage) is not { } pl) return false;
                Plugin = pl; return true;
            case "--masters-dir" when allowPlugin:
                if (Cli.Value(a, ref i, "a folder that holds the plugin's masters", usage) is not { } md) return false;
                MastersDirs.Add(md); return true;
            default: return null;
        }
    }

    static bool Stop(out string cantWhy, string why, string message, string log)
    {
        cantWhy = why;
        Console.Error.WriteLine(message);
        Log.Write($"stopped: {log}");
        return false;
    }

    /// <summary>Fills in the defaults and checks that the folders and files are there. False (with the message
    /// printed to stderr and logged, and the RESULT line's reason in cantWhy) when the command can't run.</summary>
    public bool Resolve(out string cantWhy)
    {
        cantWhy = "";
        // Option mixes that would quietly ignore one of the options.
        if (Plugin != null && (PluginsTxt != null || mo2Option != null || profileOption != null))
            return Stop(out cantWhy, "--plugin with --plugins or --mo2: give one load order",
                "--plugin and --plugins (or --mo2) both give a load order: give one of them. --plugin reads the plugin and its masters only, " +
                "with no Plugins.txt; --plugins or --mo2 read your whole load order.", "--plugin with --plugins, --mo2 or --profile");
        if (Plugin != null && ExtraDir != null)
            return Stop(out cantWhy, "--plugin with --extra-data: use --masters-dir",
                "--extra-data adds a folder to a whole load order, so it doesn't go with --plugin. To say where the plugin's masters are, " +
                "use --masters-dir \"<folder>\" (it can be given more than once).", "--plugin with --extra-data");
        if (MastersDirs.Count > 0 && Plugin == null)
            return Stop(out cantWhy, "--masters-dir without --plugin",
                "--masters-dir says where a --plugin's masters are, so it needs --plugin \"<plugin file>\" too. For a whole load order, " +
                "give --plugins \"<Plugins.txt>\" and the folder that holds the extra plugins with --extra-data.", "--masters-dir without --plugin");
        if (mo2Option != null && PluginsTxt != null)
            return Stop(out cantWhy, "--mo2 with --plugins: give one load order",
                "--mo2 and --plugins both give a load order: give one of them (--mo2 reads the MO2 profile's own plugins.txt).", "--mo2 with --plugins");
        if (profileOption != null && mo2Option == null)
            return Stop(out cantWhy, "--profile without --mo2",
                "--profile names an MO2 profile, so it needs --mo2 \"<the MO2 instance folder>\" too (the folder with ModOrganizer.ini).", "--profile without --mo2");
        if (mo2Option != null)
        {
            string error;
            try { Mo2 = Mo2Profile.Read(mo2Option, profileOption, out error); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Mo2 = null;
                error = $"a file in it can't be read ({e.GetType().Name}: {SafeText.OneLine(e.Message)}). Close MO2 if it is writing the profile, and check you may read the folder.";
            }
            if (Mo2 is null)
                return Stop(out cantWhy, "the MO2 profile can't be used", $"can't use the MO2 profile: {error}", $"MO2: {error}");
        }

        var gameData = Mo2?.GamePath is { } gp ? Path.Combine(gp, "Data") : null;
        string data, dataWhy;
        if (dataOption != null) { data = dataOption; dataWhy = "from --data"; }
        else if (gameData != null && Directory.Exists(gameData)) { data = gameData; dataWhy = "MO2's gamePath (no --data given)"; }
        else
        {
            data = Cli.SteamData;
            dataWhy = gameData != null ? $"Steam's usual folder (no --data given; MO2's gamePath has no {gameData})" : "Steam's usual folder (no --data given)";
            // --plugin with every master in the plugin's folder or a --masters-dir needs no game folder.
            if (Plugin != null && !Directory.Exists(data) && Path.GetDirectoryName(Path.GetFullPath(Plugin)) is { } own && Directory.Exists(own))
            {
                data = own;
                dataWhy = $"the plugin's own folder (no --data given and no {Cli.SteamData})";
            }
        }
        if (!Directory.Exists(data))
            return Stop(out cantWhy, $"the Data folder {Path.GetFullPath(data)} doesn't exist",
                $"can't find the game's Data folder: {Path.GetFullPath(data)} doesn't exist ({dataWhy}). Run again with " +
                "--data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data; a path with spaces needs quotes).",
                $"no Data folder at {Path.GetFullPath(data)} ({dataWhy})");
        DataDir = Path.GetFullPath(data);
        Log.Write($"Data folder: {DataDir}: {dataWhy}; Starfield.esm {(TriageCommand.HasStarfield(DataDir) ? "found there" : "NOT there")}");
        if (!TriageCommand.HasStarfield(DataDir) && Plugin == null)
            Console.Error.WriteLine($"note: no Starfield.esm in {DataDir}. If that isn't your game's Data folder, run again with --data \"<the game's Data folder>\".");

        if (Plugin != null) return ResolvePlugin(out cantWhy);
        if (Mo2 != null)
        {
            PluginsTxt = Mo2.PluginsTxt;
            Log.Write($"load order: {Mo2.Order.Source}");
        }
        else
        {
            // Without --plugins or the game's own Plugins.txt it stops: guessing an order gives wrong answers.
            string pluginsWhy;
            if (PluginsTxt != null) pluginsWhy = "from --plugins";
            else if (File.Exists(GamePluginsTxt())) { PluginsTxt = GamePluginsTxt(); pluginsWhy = "the game's own Plugins.txt (no --plugins given)"; }
            else
                return Stop(out cantWhy, "no load order: give --plugins",
                    $"can't find your load order: no --plugins given and no game Plugins.txt at {GamePluginsTxt()}. Run again with " +
                    "--plugins \"<the Plugins.txt your mod manager uses>\" (with MO2, give --mo2 \"<the MO2 instance folder>\" instead).",
                    $"no --plugins and no {GamePluginsTxt()}");
            if (!File.Exists(PluginsTxt))
                return Stop(out cantWhy, $"the Plugins.txt {Path.GetFullPath(PluginsTxt)} doesn't exist",
                    $"can't read the load order: {Path.GetFullPath(PluginsTxt)} doesn't exist. Give --plugins the Plugins.txt your mod " +
                    "manager uses (the game's is %LOCALAPPDATA%\\Starfield\\Plugins.txt; with MO2, give --mo2 \"<the MO2 instance folder>\" instead).",
                    $"no Plugins.txt at {Path.GetFullPath(PluginsTxt)}");
            PluginsTxt = Path.GetFullPath(PluginsTxt);
            Log.Write($"load order: {PluginsTxt}: {pluginsWhy}");
        }
        if (ExtraDir != null)
        {
            if (!Directory.Exists(ExtraDir))
                return Stop(out cantWhy, $"the --extra-data folder {Path.GetFullPath(ExtraDir)} doesn't exist",
                    $"can't use --extra-data {Path.GetFullPath(ExtraDir)}: that folder doesn't exist. Check the path (a path with spaces needs quotes).",
                    $"--extra-data {Path.GetFullPath(ExtraDir)} doesn't exist");
            ExtraDir = Path.GetFullPath(ExtraDir);
            Log.Write($"extra plugin folder (its files win over Data's{(Mo2 != null ? " and MO2's" : "")}): {ExtraDir}");
        }
        return true;
    }

    /// <summary>--plugin (0.16.1): checks the plugin and the --masters-dir folders, and makes the load order: the masters
    /// in MAST order (each master's own masters first), then the plugin. Each master is looked up in the plugin's own
    /// folder, then each --masters-dir in order, then Data (the order check and gap use).</summary>
    bool ResolvePlugin(out string cantWhy)
    {
        cantWhy = "";
        var full = Path.GetFullPath(Plugin!);
        if (Directory.Exists(full))
            return Stop(out cantWhy, $"--plugin {full} is a folder",
                $"can't read --plugin {full}: that is a folder. Give the plugin file itself (.esm, .esp or .esl), for example \"{Path.Combine(full, "MyMod.esm")}\".",
                $"--plugin {full} is a folder");
        if (!File.Exists(full))
            return Stop(out cantWhy, $"the plugin {full} doesn't exist",
                $"can't read --plugin {full}: that file doesn't exist. Check the path (a path with spaces needs quotes).", $"--plugin {full} doesn't exist");
        Plugin = full;
        for (int i = 0; i < MastersDirs.Count; i++)
        {
            if (!Directory.Exists(MastersDirs[i]))
                return Stop(out cantWhy, $"the --masters-dir folder {Path.GetFullPath(MastersDirs[i])} doesn't exist",
                    $"can't use --masters-dir {Path.GetFullPath(MastersDirs[i])}: that folder doesn't exist. Give a folder that holds the plugin's masters.",
                    $"--masters-dir {Path.GetFullPath(MastersDirs[i])} doesn't exist");
            MastersDirs[i] = Path.GetFullPath(MastersDirs[i]);
        }
        var dirs = new List<string> { Path.GetDirectoryName(full)! };
        foreach (var d in MastersDirs.Append(DataDir))
            if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        string? Locate(string name) => dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

        var order = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? missing = null, missingFor = null, unreadable = null;
        void Visit(string name, string path)
        {
            if (missing != null || unreadable != null || order.Contains(name, StringComparer.OrdinalIgnoreCase) || !visiting.Add(name)) return;
            List<string> masters;
            try { masters = LoadOrderSetup.ModHeaderMasters(path); }
            catch (Exception e) when (Fatal.Recoverable(e)) { unreadable = $"{path} ({e.GetType().Name})"; return; }
            foreach (var m in masters)
            {
                // a master name is a file name; one with a folder part is never looked up outside the folders above
                if ((Mo2Profile.IsFolderName(m) ? Locate(m) : null) is not { } mp) { missing ??= m; missingFor ??= name; return; }
                Visit(m, mp);
            }
            order.Add(Path.GetFileName(path));
        }
        Visit(Path.GetFileName(full), full);
        if (unreadable != null)
            return Stop(out cantWhy, "a plugin header can't be read",
                $"can't read the plugin header of {unreadable}. Run sf-mod-check check on it for details.", $"header of {unreadable} unreadable");
        if (missing != null)
            return Stop(out cantWhy, $"master {SafeText.OneLine(missing)} not found",
                $"can't find {SafeText.OneLine(missingFor!)}'s master {SafeText.OneLine(missing)} in {string.Join(", ", dirs)}. " +
                $"Put {SafeText.OneLine(missing)} in the plugin's folder, or give its folder with --masters-dir, or the game's Data folder with --data.",
                $"master {missing} of {missingFor} not found in {string.Join(", ", dirs)}");
        var n = order.Count - 1;
        single = new(new(order, new(StringComparer.OrdinalIgnoreCase), $"{Path.GetFileName(full)} and its {n} master{(n == 1 ? "" : "s")}", Exact: true),
                     dirs.Take(dirs.Count - (dirs[^1].Equals(DataDir, StringComparison.OrdinalIgnoreCase) ? 1 : 0)).ToList());
        Log.Write($"load order (--plugin): {string.Join(", ", order)}; masters looked up in {string.Join(", ", dirs)}");
        return true;
    }

    /// <summary>Folders whose plugins, archives and loose files win over Data's, highest priority first: --extra-data,
    /// then (with --mo2) MO2's Overwrite and the enabled mods from the top of modlist.txt down; with --plugin, the
    /// plugin's own folder and each --masters-dir.</summary>
    public IReadOnlyList<string> OverDirs => single?.Dirs ??
        (ExtraDir is null ? [] : new[] { ExtraDir }).Concat(Mo2?.ModDirs ?? []).ToList();

    /// <summary>Folders searched for archives and loose files, first match wins: OverDirs, then Data.</summary>
    public List<string> AssetDirs => OverDirs.Append(DataDir).ToList();

    /// <summary>The load order as a list (after Resolve).</summary>
    public LoadOrderSetup.PluginOrder Order() => single?.Order ?? Mo2?.Order ?? LoadOrderSetup.ReadPluginsTxt(PluginsTxt!);

    /// <summary>Opens the load order (after Resolve); order: a changed copy of Order() (patch leaves out its own
    /// older copy). The MO2 profile's problems are added to the load-order problems.</summary>
    public LoadOrderSetup Build(LoadOrderSetup.PluginOrder? order = null)
    {
        var lo = LoadOrderSetup.Build(DataDir, order ?? Order(), OverDirs);
        foreach (var p in Mo2?.Problems ?? []) lo.AddProblem(p);
        return lo;
    }
}
