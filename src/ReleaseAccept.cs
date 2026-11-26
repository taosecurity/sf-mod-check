// --accept and --accept-file for the release command (0.15.2): accept one known finding, exactly.
//   --accept <rule>:<plugin>:<detail>      for example --accept script-missing:OtherMod.esm:OtherMod:Toggle
//   --accept-file <file>                   one entry per line; "#" starts a comment, blank lines are skipped
// An entry names one rule that reports one missing file, one plugin's file name and the finding's detail (the script
// name for script-missing, the file's path for the others), compared without regard to case. No wildcards. An accepted
// finding still prints, as ACCEPTED with the entry that accepted it, and no longer counts toward FAIL or the exit code.
// An entry that matches nothing prints a WARNING (it doesn't fail the run). A malformed entry stops the run (exit 2).
namespace SfModCheck;

public sealed record AcceptEntry(string Rule, string Plugin, string Detail, string Source)
{
    /// <summary>The entry as the user wrote it, in its normal form.</summary>
    public string Text => $"{Rule}:{Plugin}:{Detail}";

    public bool Matches(ReleaseFinding f) =>
        f.Plugin != null && f.Subject != null &&
        f.Rule.Equals(Rule, StringComparison.OrdinalIgnoreCase) &&
        f.Plugin.Equals(Plugin, StringComparison.OrdinalIgnoreCase) &&
        f.Subject.Replace('/', '\\').Equals(Detail.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);   // a path with either slash
}

public static class ReleaseAccept
{
    /// <summary>The rules whose findings name one plugin and one missing file, so an entry can name them exactly.</summary>
    public static readonly string[] Rules =
        ["script-missing", "model-missing", "mesh-missing", "texture-missing", "material-missing", "sound-missing", "face-missing", "sound-event-missing", "sound-bank-manifest-missing"];

    const string Form = "an --accept entry is <rule>:<plugin>:<detail>, for example script-missing:OtherMod.esm:OtherMod:Toggle";

    /// <summary>Parses one entry; returns null and sets error (naming the entry and the fix) when it is malformed.</summary>
    public static AcceptEntry? Parse(string raw, string source, out string? error)
    {
        error = null;
        var text = raw.Trim();
        var where = source == "--accept" ? "--accept" : source;
        string Bad(string why) => $"{where}: the entry \"{SafeText.OneLine(text)}\" {why}. {char.ToUpper(Form[0])}{Form[1..]}.";
        var first = text.IndexOf(':');
        var second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        if (second < 0) { error = Bad("doesn't have three parts"); return null; }
        var rule = text[..first].Trim();
        var plugin = text[(first + 1)..second].Trim();
        var detail = text[(second + 1)..].Trim();   // a script name may hold colons (Foo:Bar), so the rest is the detail
        if (rule.Length == 0 || plugin.Length == 0 || detail.Length == 0) { error = Bad("has an empty part"); return null; }
        if (text.IndexOfAny(['*', '?']) >= 0) { error = Bad("holds a wildcard (* or ?); name the one rule, plugin and detail exactly"); return null; }
        if (!Rules.Contains(rule, StringComparer.OrdinalIgnoreCase))
        {
            error = Bad($"names the rule \"{SafeText.OneLine(rule)}\", which can't be accepted; the rules that can are {string.Join(", ", Rules)}");
            return null;
        }
        if (Path.GetExtension(plugin).ToLowerInvariant() is not (".esm" or ".esp" or ".esl") || plugin.IndexOfAny(['\\', '/']) >= 0)
        {
            error = Bad($"names \"{SafeText.OneLine(plugin)}\" as the plugin; give the plugin's file name only, for example OtherMod.esm");
            return null;
        }
        return new AcceptEntry(rule.ToLowerInvariant(), plugin, detail, source);
    }

    /// <summary>Reads an --accept-file: one entry per line, "#" to the end of the line is a comment. Returns null and
    /// sets error when the file can't be read or an entry in it is malformed.</summary>
    public static List<AcceptEntry>? ReadFile(string path, out string? error)
    {
        error = null;
        var full = Path.GetFullPath(path);
        string[] lines;
        try { lines = File.ReadAllLines(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = File.Exists(full) || Directory.Exists(full)
                ? $"can't read --accept-file {full}: {e.Message} Give a text file with one entry per line."
                : $"can't use --accept-file {full}: no such file. A path with spaces needs quotes around it.";
            return null;
        }
        var result = new List<AcceptEntry>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            if (line.Trim().Length == 0) continue;
            if (Parse(line, $"{Path.GetFileName(full)} line {i + 1}", out error) is not { } entry) return null;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>Marks each finding an entry matches (Accepted set to the entry) and adds a WARNING for each entry that
    /// matched nothing. Findings are compared after the profile is applied.</summary>
    public static List<ReleaseFinding> Apply(List<ReleaseFinding> findings, IReadOnlyList<AcceptEntry> entries)
    {
        if (entries.Count == 0) return findings;
        var used = new HashSet<AcceptEntry>();
        var result = new List<ReleaseFinding>(findings.Count);
        foreach (var f in findings)
        {
            var e = entries.FirstOrDefault(x => x.Matches(f));
            if (e != null) { used.Add(e); result.Add(f with { Accepted = e }); Log.Write($"accepted by {e.Source} {e.Text}: [{f.Rule}] {f.Text}"); }
            else result.Add(f);
        }
        foreach (var e in entries.DistinctBy(x => x.Text.ToLowerInvariant()))
            if (!entries.Where(x => x.Text.Equals(e.Text, StringComparison.OrdinalIgnoreCase)).Any(used.Contains))
                result.Add(new(Level.Warning, "accept", $"accept entry matched nothing: {e.Text} (from {e.Source}). Check the rule, the plugin's file name and the detail against the finding, or remove the entry."));
        return result;
    }
}
