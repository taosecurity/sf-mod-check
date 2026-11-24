// The load order a read-only command looks at (0.16.0, first used by find): the options --plugins, --data and
// --extra-data, their defaults (Steam's Data folder, the game's own Plugins.txt) and the checks on them, in one place.
// The MO2 input (--mo2, --profile) plugs in here: it adds options in TryOption, its folders in OverDirs and its order
// in Build. conflict, patch and triage still read their own options (they predate this class).
using SfModCheck.Conflict;

namespace SfModCheck;

public sealed class LoadOrderInput
{
    public string? PluginsTxt { get; private set; }
    public string DataDir { get; private set; } = "";
    public string? ExtraDir { get; private set; }
    string? dataOption;

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
            default: return null;
        }
    }

    /// <summary>Fills in the defaults and checks that the folders and files are there. False (with the message
    /// printed to stderr and logged, and the RESULT line's reason in cantWhy) when the command can't run.</summary>
    public bool Resolve(out string cantWhy)
    {
        cantWhy = "";
        var dataWhy = dataOption != null ? "from --data" : "Steam's usual folder (no --data given)";
        var data = dataOption ?? Cli.SteamData;
        if (!Directory.Exists(data))
        {
            cantWhy = $"the Data folder {Path.GetFullPath(data)} doesn't exist";
            Console.Error.WriteLine($"can't find the game's Data folder: {Path.GetFullPath(data)} doesn't exist ({dataWhy}). Run again with " +
                                    "--data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data; a path with spaces needs quotes).");
            Log.Write($"stopped: no Data folder at {Path.GetFullPath(data)} ({dataWhy})");
            return false;
        }
        DataDir = Path.GetFullPath(data);
        Log.Write($"Data folder: {DataDir}: {dataWhy}; Starfield.esm {(TriageCommand.HasStarfield(DataDir) ? "found there" : "NOT there")}");
        if (!TriageCommand.HasStarfield(DataDir))
            Console.Error.WriteLine($"note: no Starfield.esm in {DataDir}. If that isn't your game's Data folder, run again with --data \"<the game's Data folder>\".");

        // Without --plugins or the game's own Plugins.txt it stops: guessing an order gives wrong answers.
        string pluginsWhy;
        if (PluginsTxt != null) pluginsWhy = "from --plugins";
        else if (File.Exists(GamePluginsTxt())) { PluginsTxt = GamePluginsTxt(); pluginsWhy = "the game's own Plugins.txt (no --plugins given)"; }
        else
        {
            cantWhy = "no load order: give --plugins";
            Console.Error.WriteLine($"can't find your load order: no --plugins given and no game Plugins.txt at {GamePluginsTxt()}. Run again with " +
                                    "--plugins \"<the Plugins.txt your mod manager uses>\".");
            Log.Write($"stopped: no --plugins and no {GamePluginsTxt()}");
            return false;
        }
        if (!File.Exists(PluginsTxt))
        {
            cantWhy = $"the Plugins.txt {Path.GetFullPath(PluginsTxt)} doesn't exist";
            Console.Error.WriteLine($"can't read the load order: {Path.GetFullPath(PluginsTxt)} doesn't exist. Give --plugins the Plugins.txt your mod " +
                                    "manager uses (the game's is %LOCALAPPDATA%\\Starfield\\Plugins.txt).");
            Log.Write($"stopped: no Plugins.txt at {Path.GetFullPath(PluginsTxt)}");
            return false;
        }
        PluginsTxt = Path.GetFullPath(PluginsTxt);
        Log.Write($"load order: {PluginsTxt}: {pluginsWhy}");
        if (ExtraDir != null)
        {
            if (!Directory.Exists(ExtraDir))
            {
                cantWhy = $"the --extra-data folder {Path.GetFullPath(ExtraDir)} doesn't exist";
                Console.Error.WriteLine($"can't use --extra-data {Path.GetFullPath(ExtraDir)}: that folder doesn't exist. Check the path (a path with spaces needs quotes).");
                Log.Write($"stopped: --extra-data {Path.GetFullPath(ExtraDir)} doesn't exist");
                return false;
            }
            ExtraDir = Path.GetFullPath(ExtraDir);
            Log.Write($"extra plugin folder (its files win over Data's): {ExtraDir}");
        }
        return true;
    }

    /// <summary>Folders whose plugins win over Data's, highest priority first.</summary>
    public IReadOnlyList<string> OverDirs => ExtraDir is null ? [] : [ExtraDir];

    /// <summary>Opens the load order (after Resolve).</summary>
    public LoadOrderSetup Build() => LoadOrderSetup.Build(DataDir, PluginsTxt, OverDirs);
}
