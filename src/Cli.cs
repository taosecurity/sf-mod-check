// Command-line helpers shared by check (Program.cs) and conflict (ConflictCommand.cs); until 0.5.1 each had its own copy.
namespace SfModCheck;

public static class Cli
{
    // Steam's usual Data folder, the default when no --data is given.
    public const string SteamData = @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";

    /// <summary>The value after option a[i] (and moves i past it); null, after an error naming the option, an example
    /// and the usage, when it is missing. An empty value ("" from a script, or a variable that wasn't set) counts as
    /// missing (0.5.0).</summary>
    public static string? Value(string[] a, ref int i, string what, string usage)
    {
        if (i + 1 < a.Length && !a[i + 1].StartsWith("--") && a[i + 1].Trim().Length > 0) return a[++i];
        Console.Error.WriteLine($"{a[i]} needs {what} after it{(i + 1 < a.Length && a[i + 1].Trim().Length == 0 ? " (it was given an empty value)" : "")}, for example: {a[i]} {Example(a[i])}");
        Console.Error.WriteLine(usage);
        return null;
    }

    /// <summary>An example value for an option, for the error messages.</summary>
    public static string Example(string option) => option switch
    {
        "--plugins" => "\"%LOCALAPPDATA%\\Starfield\\Plugins.txt\"",
        "--data" => "\"D:\\XboxGames\\Starfield\\Content\\Data\"",
        "--masters-dir" => "\"C:\\MyBuild\\out\"",
        "--extra-data" => "\"C:\\MyBuild\\out\"",
        "--out" => "conflict-report",
        "--max" => "20",
        "--max-memory" => "8",
        "--log" => "sfmodcheck-log.txt",
        _ => "<value>",
    };
}
