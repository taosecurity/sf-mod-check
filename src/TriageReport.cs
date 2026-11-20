// The triage report (0.8.0): findings plus one table per kind of fact, written as .html, .txt and .json. Every page and
// file opens with "triage, not a verdict". Text from the mod (file names, plugin strings) is untrusted: the HTML escapes
// all of it and the text forms drop control characters.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SfModCheck;

/// <summary>A table in one section of the report ("Files", "Every file"). Sections add rows with Add.</summary>
public sealed class TriageTable
{
    public required string Section { get; init; }
    public required string Title { get; init; }
    public required List<string> Columns { get; init; }
    public List<List<string>> Rows { get; } = new();

    public void Add(params string[] cells)
    {
        if (cells.Length != Columns.Count)
            throw new ArgumentException($"table \"{Title}\" has {Columns.Count} columns, not {cells.Length}");
        Rows.Add(cells.ToList());
    }
}

public sealed class TriageReport
{
    public const string Banner =
        "Triage, not a verdict. This report lists what the mod contains and what is worth a person's look. " +
        "It doesn't say whether the mod is safe or good: a LOOK line is a place to start reading, not a fault.";

    public string Tool { get; init; } = "sf-mod-check";
    public string InputName { get; init; } = "";
    public string InputPath { get; init; } = "";
    public TriageInputKind InputKind { get; init; }
    public long? InputSize { get; private set; }
    public string? InputSha256 { get; private set; }
    public string DataDir { get; init; } = "";
    public bool HasGame { get; init; }
    public DateTimeOffset Made { get; init; } = DateTimeOffset.Now;
    public List<TriageFinding> Findings { get; } = new();
    public List<TriageTable> Tables { get; } = new();
    /// <summary>Sections that stopped early, one line each; shown at the top.</summary>
    public List<string> Incomplete { get; } = new();

    public void SetInputFile(string path)
    {
        InputSize = new FileInfo(path).Length;
        using var s = File.OpenRead(path);
        InputSha256 = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>A new table in a section; sections keep the returned table and Add rows to it.</summary>
    public TriageTable Table(string section, string title, params string[] columns)
    {
        var t = new TriageTable { Section = section, Title = title, Columns = columns.ToList() };
        Tables.Add(t);
        return t;
    }

    /// <summary>Section names in report order: the known ones first, then any other a section used.</summary>
    IEnumerable<string> SectionsUsed() =>
        TriageSections.Order.Concat(Findings.Select(f => f.Section).Concat(Tables.Select(t => t.Section))
            .Where(s => !TriageSections.Order.Contains(s)).Distinct());

    IEnumerable<TriageFinding> Ordered(string section) =>
        Findings.Where(f => f.Section == section).OrderBy(f => f.Level == TriageLevel.Look ? 0 : 1);

    /// <summary>Writes stem.html, stem.txt and stem.json; returns their paths, the HTML first.</summary>
    public List<string> WriteAll(string stem)
    {
        var paths = new List<string> { stem + ".html", stem + ".txt", stem + ".json" };
        File.WriteAllText(paths[0], Html(), new UTF8Encoding(false));
        File.WriteAllText(paths[1], Text(), new UTF8Encoding(false));
        File.WriteAllText(paths[2], Json(), new UTF8Encoding(false));
        return paths;
    }

    /// <summary>Control characters (other than tab) become spaces, so a crafted name can't move the cursor or end a line.</summary>
    public static string Clean(string s)
    {
        if (!s.Any(c => char.IsControl(c) && c != '\t')) return s;
        var b = new StringBuilder(s.Length);
        foreach (var c in s) b.Append(char.IsControl(c) && c != '\t' ? ' ' : c);
        return b.ToString();
    }

    IEnumerable<(string Key, string Value)> Facts()
    {
        yield return ("Mod", InputName);
        yield return ("Path", InputPath);
        yield return ("Kind", InputKind switch
        {
            TriageInputKind.SevenZip => "7z archive",
            TriageInputKind.Rar => "rar archive",
            TriageInputKind.Zip => "zip archive",
            TriageInputKind.Folder => "folder",
            _ => "plugin",
        });
        if (InputSize is { } size) yield return ("Size", $"{size:N0} bytes");
        if (InputSha256 != null) yield return ("SHA-256", InputSha256);
        yield return ("Game Data folder", HasGame ? DataDir : $"{DataDir} (no Starfield.esm there: nothing was compared with the game)");
        yield return ("Made", $"{Made:yyyy-MM-dd HH:mm zzz} by {Tool}");
        yield return ("Findings", $"{Findings.Count(f => f.Level == TriageLevel.Look)} to look at, {Findings.Count(f => f.Level == TriageLevel.Note)} notes");
    }

    // ---- text ----

    public string Text()
    {
        var b = new StringBuilder();
        b.AppendLine($"Triage of {Clean(InputName)}");
        b.AppendLine();
        b.AppendLine(Banner);
        b.AppendLine();
        foreach (var (k, v) in Facts()) b.AppendLine($"{k + ":",-18}{Clean(v)}");
        foreach (var i in Incomplete) b.AppendLine($"INCOMPLETE        {Clean(i)}");
        foreach (var s in SectionsUsed())
        {
            var findings = Ordered(s).ToList();
            var tables = Tables.Where(t => t.Section == s).ToList();
            b.AppendLine();
            b.AppendLine($"== {s} ==");
            if (findings.Count == 0 && tables.Count == 0) { b.AppendLine("(nothing found)"); continue; }
            foreach (var f in findings)
                b.AppendLine($"{(f.Level == TriageLevel.Look ? "LOOK" : "NOTE")}  {(f.File != "" ? Clean(f.File) + ": " : "")}{Clean(f.Text)}");
            foreach (var t in tables)
            {
                b.AppendLine();
                b.AppendLine($"-- {Clean(t.Title)} ({t.Rows.Count} rows) --");
                if (t.Rows.Count == 0) continue;
                var widths = t.Columns.Select((c, i) => Math.Min(60, Math.Max(c.Length, t.Rows.Max(r => Clean(r[i]).Length)))).ToList();
                b.AppendLine(string.Join("  ", t.Columns.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
                foreach (var r in t.Rows)
                    b.AppendLine(string.Join("  ", r.Select((c, i) => Clean(c).PadRight(widths[i]))).TrimEnd());
            }
        }
        return b.ToString();
    }

    // ---- JSON ----

    public string Json()
    {
        var doc = new Dictionary<string, object?>
        {
            ["tool"] = Tool,
            ["triage_not_a_verdict"] = true,
            ["banner"] = Banner,
            ["made"] = Made.ToString("o"),
            ["input"] = new Dictionary<string, object?>
            {
                ["name"] = InputName, ["path"] = InputPath, ["kind"] = InputKind.ToString(),
                ["size"] = InputSize, ["sha256"] = InputSha256,
            },
            ["game_data"] = DataDir,
            ["game_found"] = HasGame,
            ["incomplete"] = Incomplete,
            ["findings"] = Findings.Select(f => new Dictionary<string, string>
            {
                ["level"] = f.Level == TriageLevel.Look ? "LOOK" : "NOTE", ["section"] = f.Section, ["file"] = f.File, ["text"] = f.Text,
            }).ToList(),
            ["tables"] = Tables.Select(t => new Dictionary<string, object>
            {
                ["section"] = t.Section, ["title"] = t.Title, ["columns"] = t.Columns, ["rows"] = t.Rows,
            }).ToList(),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- HTML ----

    static string H(string s) => WebUtility.HtmlEncode(Clean(s));

    static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    const string Css = """
        :root { --bg:#fbfaf7; --fg:#1d1f21; --muted:#5f6368; --line:#d9d6cf; --card:#ffffff; --look:#9a5b00; --lookbg:#fff4e0;
                --note:#2f5d8a; --notebg:#eef4fa; --banner:#f3efe6; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#17191c; --fg:#e6e3dd; --muted:#a3a7ad; --line:#363a40; --card:#1f2226; --look:#f0b45c; --lookbg:#3a2c14;
                  --note:#8fbbe6; --notebg:#1b2a3a; --banner:#25282c; } }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 "Segoe UI", system-ui, sans-serif; }
        main { padding:16px 24px 48px; }
        h1 { font-size:1.5em; margin:.2em 0 .4em; }
        h2 { font-size:1.2em; margin:1.6em 0 .4em; border-bottom:1px solid var(--line); padding-bottom:.2em; }
        h3 { font-size:1em; margin:1.2em 0 .3em; color:var(--muted); }
        .banner { background:var(--banner); border-left:4px solid var(--note); padding:10px 14px; margin:8px 0 16px; }
        .incomplete { background:var(--lookbg); border-left:4px solid var(--look); padding:8px 14px; margin:4px 0; }
        dl { display:grid; grid-template-columns:max-content 1fr; gap:2px 16px; margin:0; }
        dt { color:var(--muted); } dd { margin:0; overflow-wrap:anywhere; }
        nav { margin:12px 0; } nav a { margin-right:14px; color:var(--note); }
        ul.f { list-style:none; padding:0; margin:0; }
        ul.f li { padding:4px 10px; margin:3px 0; border-radius:4px; overflow-wrap:anywhere; }
        li.look { background:var(--lookbg); } li.note { background:var(--notebg); }
        .lvl { font-weight:600; font-size:.85em; margin-right:8px; }
        li.look .lvl { color:var(--look); } li.note .lvl { color:var(--note); }
        code { font-family:Consolas, "Cascadia Mono", monospace; font-size:.92em; }
        .wrap { overflow-x:auto; }
        table { border-collapse:collapse; background:var(--card); font-size:.9em; }
        th, td { border:1px solid var(--line); padding:3px 8px; text-align:left; vertical-align:top; }
        td { overflow-wrap:anywhere; } th { position:sticky; top:0; background:var(--banner); }
        .none { color:var(--muted); }
        @media (max-width:600px) { main { padding:12px 16px 32px; } dl { grid-template-columns:1fr; } }
        """;

    public string Html()
    {
        var b = new StringBuilder();
        b.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        b.AppendLine($"<title>Triage: {H(InputName)}</title><style>{Css}</style></head><body><main>");
        b.AppendLine($"<h1>Triage of {H(InputName)}</h1>");
        b.AppendLine($"<div class=\"banner\"><strong>Triage, not a verdict.</strong> {H(Banner["Triage, not a verdict. ".Length..])}</div>");
        b.AppendLine("<dl>");
        foreach (var (k, v) in Facts()) b.AppendLine($"<dt>{H(k)}</dt><dd>{(k is "SHA-256" or "Path" ? $"<code>{H(v)}</code>" : H(v))}</dd>");
        b.AppendLine("</dl>");
        foreach (var i in Incomplete) b.AppendLine($"<div class=\"incomplete\">Incomplete: {H(i)}</div>");
        var sections = SectionsUsed().ToList();
        b.AppendLine("<nav>" + string.Join("", sections.Select(s =>
        {
            int looks = Findings.Count(f => f.Section == s && f.Level == TriageLevel.Look);
            return $"<a href=\"#{Slug(s)}\">{H(s)}{(looks > 0 ? $" ({looks} to look at)" : "")}</a>";
        })) + "</nav>");
        foreach (var s in sections)
        {
            b.AppendLine($"<h2 id=\"{Slug(s)}\">{H(s)}</h2>");
            var findings = Ordered(s).ToList();
            var tables = Tables.Where(t => t.Section == s).ToList();
            if (findings.Count == 0 && tables.Count == 0) { b.AppendLine("<p class=\"none\">Nothing found.</p>"); continue; }
            if (findings.Count > 0)
            {
                b.AppendLine("<ul class=\"f\">");
                foreach (var f in findings)
                {
                    var look = f.Level == TriageLevel.Look;
                    b.AppendLine($"<li class=\"{(look ? "look" : "note")}\"><span class=\"lvl\">{(look ? "LOOK" : "NOTE")}</span>" +
                                 $"{(f.File != "" ? $"<code>{H(f.File)}</code>: " : "")}{H(f.Text)}</li>");
                }
                b.AppendLine("</ul>");
            }
            foreach (var t in tables)
            {
                b.AppendLine($"<h3>{H(t.Title)} ({t.Rows.Count})</h3>");
                if (t.Rows.Count == 0) { b.AppendLine("<p class=\"none\">None.</p>"); continue; }
                b.AppendLine("<div class=\"wrap\"><table><thead><tr>" + string.Join("", t.Columns.Select(c => $"<th>{H(c)}</th>")) + "</tr></thead><tbody>");
                foreach (var r in t.Rows) b.AppendLine("<tr>" + string.Join("", r.Select(c => $"<td>{H(c)}</td>")) + "</tr>");
                b.AppendLine("</tbody></table></div>");
            }
        }
        b.AppendLine("</main></body></html>");
        return b.ToString();
    }
}
