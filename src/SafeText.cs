// One rule for text that came from a plugin, a script, a zip or an exception and is printed or logged on one line
// (0.13.0, code review round 10 of 0.10.0, findings 1, 2 and 6): before 0.13.0 the conflict code turned control
// characters into spaces (Conflict.Impact.OneLine) while the creations step escaped them (ReleaseCreations.OneLine).
namespace SfModCheck;

public static class SafeText
{
    /// <summary>Text for a one-line finding, report cell or log line: control characters (Cc), the Unicode line and
    /// paragraph separators (Zl, Zp) and every format character (Cf: the bidi controls such as U+202E and U+061C,
    /// zero-width characters, U+FEFF) are written as \n, \r, \t or \uXXXX, so a name taken from a plugin, a script
    /// or a zip can't split a line, start a new Markdown line, hide characters or reorder the line on screen.
    /// Everything else (letters, accents, dashes, spaces, backslashes in paths) stays as it is.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(s))]
    public static string? OneLine(string? s)
    {
        if (s is null) return null;
        if (!s.Any(c => Escape(c) != null)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var ch in s) sb.Append(Escape(ch) ?? ch.ToString());
        return sb.ToString();
    }

    /// <summary>The escape <see cref="OneLine"/> writes for one character, or null when it stays as it is.</summary>
    public static string? Escape(char ch) => ch switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ when char.IsControl(ch) => $"\\u{(int)ch:x4}",
        _ => char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.Format ? $"\\u{(int)ch:x4}" : null,
    };
}
