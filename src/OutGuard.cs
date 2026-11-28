namespace SfModCheck;

/// <summary>0.28.1 (Round 3, C2 and A4): the one refusal for a file the user names with --out (text, scenes, voice-lines) or
/// --json (patch check). A typo there must not destroy a mod: a plugin, archive, string file, script or DLL name, a file
/// stream and a folder are refused. Windows drops a name's trailing dots and spaces when it opens the file, so the name is
/// read from the full path as Windows will open it.</summary>
static class OutGuard
{
    static readonly HashSet<string> ModFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".esm", ".esp", ".esl", ".ba2", ".zip", ".7z", ".rar", ".strings", ".dlstrings", ".ilstrings", ".pex", ".psc", ".dll" };

    /// <summary>The message to print and the short reason for the RESULT line, or null when the name is fine.
    /// <paramref name="what"/> is what the option writes ("a text or JSON file"), <paramref name="example"/> a good name.</summary>
    public static (string Message, string Why)? Problem(string outFile, string option, string what, string example)
    {
        string full;
        try { full = Path.GetFullPath(outFile); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ($"{option} {SafeText.OneLine(outFile)} isn't a usable path ({SafeText.OneLine(e.Message)}): give a file name, such as {example}.",
                    $"{option} isn't a usable path");
        }
        var name = Path.GetFileName(full).TrimEnd('.', ' ');
        var ext = Path.GetExtension(name.Split(':')[0]);
        if (ModFileExtensions.Contains(ext))
            return ($"{option} writes {what}, not a {SafeText.OneLine(ext)} file: give it a name such as {example}.", $"{option} names a mod file");
        if (name.Contains(':'))
            return ($"{option} {SafeText.OneLine(outFile)} names a file stream (a colon in the file name): give a plain file name, such as {example}.",
                    $"{option} names a file stream");
        if (Directory.Exists(outFile))
            return ($"{option} {SafeText.OneLine(outFile)} is a folder: give a file name, such as {SafeText.OneLine(Path.Combine(outFile, example))}.",
                    $"{option} is a folder");
        return null;
    }
}
