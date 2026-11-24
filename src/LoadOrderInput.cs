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
    string? dataOption, mo2Option, profileOption;

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
        }
        if (!Directory.Exists(data))
            return Stop(out cantWhy, $"the Data folder {Path.GetFullPath(data)} doesn't exist",
                $"can't find the game's Data folder: {Path.GetFullPath(data)} doesn't exist ({dataWhy}). Run again with " +
                "--data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data; a path with spaces needs quotes).",
                $"no Data folder at {Path.GetFullPath(data)} ({dataWhy})");
        DataDir = Path.GetFullPath(data);
        Log.Write($"Data folder: {DataDir}: {dataWhy}; Starfield.esm {(TriageCommand.HasStarfield(DataDir) ? "found there" : "NOT there")}");
        if (!TriageCommand.HasStarfield(DataDir))
            Console.Error.WriteLine($"note: no Starfield.esm in {DataDir}. If that isn't your game's Data folder, run again with --data \"<the game's Data folder>\".");

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

    /// <summary>Folders whose plugins, archives and loose files win over Data's, highest priority first: --extra-data,
    /// then (with --mo2) MO2's Overwrite and the enabled mods from the top of modlist.txt down.</summary>
    public IReadOnlyList<string> OverDirs =>
        (ExtraDir is null ? [] : new[] { ExtraDir }).Concat(Mo2?.ModDirs ?? []).ToList();

    /// <summary>Folders searched for archives and loose files, first match wins: OverDirs, then Data.</summary>
    public List<string> AssetDirs => OverDirs.Append(DataDir).ToList();

    /// <summary>The load order as a list (after Resolve).</summary>
    public LoadOrderSetup.PluginOrder Order() => Mo2?.Order ?? LoadOrderSetup.ReadPluginsTxt(PluginsTxt!);

    /// <summary>Opens the load order (after Resolve); order: a changed copy of Order() (patch leaves out its own
    /// older copy). The MO2 profile's problems are added to the load-order problems.</summary>
    public LoadOrderSetup Build(LoadOrderSetup.PluginOrder? order = null)
    {
        var lo = LoadOrderSetup.Build(DataDir, order ?? Order(), OverDirs);
        foreach (var p in Mo2?.Problems ?? []) lo.AddProblem(p);
        return lo;
    }
}
