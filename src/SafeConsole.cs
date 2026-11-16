// Console output with control characters shown as \xNN (2026-11-16 security review). EditorIDs, names and master
// names come from plugin files, so a mod could hide terminal escape codes in them that fake output lines or change the
// window title. Tab and line breaks pass through; everything else below 0x20, 0x7F and 0x80-0x9F is escaped.
using System.Text;

namespace SfModCheck;

public sealed class SafeConsoleWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public static void Install()
    {
        Console.SetOut(new SafeConsoleWriter(Console.Out));
        Console.SetError(new SafeConsoleWriter(Console.Error));
    }

    static bool Unsafe(char c) => (c < 0x20 && c is not ('\n' or '\r' or '\t')) || (c >= 0x7F && c <= 0x9F);

    public override void Write(char value)
    {
        if (Unsafe(value)) inner.Write($"\\x{(int)value:X2}");
        else inner.Write(value);
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        if (!value.Any(Unsafe)) { inner.Write(value); return; }
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value) if (Unsafe(c)) sb.Append($"\\x{(int)c:X2}"); else sb.Append(c);
        inner.Write(sb.ToString());
    }

    public override void WriteLine(string? value) { Write(value); inner.WriteLine(); }
    public override void Flush() => inner.Flush();
}
