// Moved from StarfieldConflictCheck (Report.cs, commit d336108) into sfmodcheck on 2026-11-16.
// Writes report.md (for reading) and conflicts.json (for the next steps: load-order advice and patch rules).
using System.Text;
using System.Text.Json;

namespace SfModCheck.Conflict;


public static class Report
{
    const int RowsPerGroup = 100;

    public static void Write(string outDir, LoadOrderSetup lo, Analysis a)
    {
        Directory.CreateDirectory(outDir);
        var md = new StringBuilder();
        var official = a.Lost.Where(l => l.LoserOfficial).ToList();
        var modVsMod = a.Lost.Where(l => !l.LoserOfficial).ToList();

        md.AppendLine("# Starfield conflict report");
        md.AppendLine();
        md.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Load order: {lo.OrderSource}.");
        md.AppendLine();
        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine("| | Count |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Plugins loaded | {lo.Plugins.Count} ({lo.Plugins.Count(p => p.Official)} official, {lo.Plugins.Count(p => !p.Official)} mods) |");
        md.AppendLine($"| Records overridden by mods | {a.RecordsOverriddenByMods} |");
        md.AppendLine($"| Records where an official change is lost | {official.Select(l => l.FormKey).Distinct().Count()} |");
        md.AppendLine($"| Records where one mod's change is lost to another mod | {modVsMod.Select(l => l.FormKey).Distinct().Count()} |");
        md.AppendLine($"| Overrides that change nothing (ITM) | {a.Itms.Count} |");
        md.AppendLine($"| Load-order problems | {lo.Problems.Count} |");
        md.AppendLine();
        md.AppendLine("How to read the tables: **Forwardable** means the winning plugin did not mean to change that field; it just carries an older value, so a patch can copy the lost value forward safely. **Choice** means both plugins changed the same field, so someone has to pick. **No effect** means the versions differ only in list order, letter case or undocumented fields.");
        md.AppendLine();

        var allGroups = a.Lost.GroupBy(l => (l.Winner, l.Loser)).ToList();
        var recs = allGroups.SelectMany(g => Recommend.ForGroup(g, lo, a)).OrderBy(r => r.Priority).ToList();
        if (Recommend.Combined(allGroups, lo, a) is { } combined) recs.Insert(0, combined);
        md.AppendLine("## Recommended actions");
        md.AppendLine();
        if (recs.Count == 0) md.AppendLine("Nothing needs doing.");
        foreach (var r in recs)
        {
            md.AppendLine($"- **{r.Level}** — {(r.Winner.Length > 0 ? $"{LoadOrderSetup.Display(r.Winner)} over {LoadOrderSetup.Display(r.Loser)}" : "All patchable conflicts")}: {r.Text}");
            if (r.Prompt is not null)
            {
                // Indented fenced block so it stays inside the list item.
                md.AppendLine().AppendLine("  Prompt for an AI assistant:").AppendLine().AppendLine("  ```text");
                foreach (var line in r.Prompt.Replace("\r", "").Split('\n')) md.AppendLine("  " + line);
                md.AppendLine("  ```").AppendLine();
            }
        }
        if (a.Itms.Count > 0) md.AppendLine($"- **Low** — Optional: {a.Itms.Count} ITM override(s) in {string.Join(", ", a.Itms.Select(i => i.Plugin).Distinct())}. Harmless here; xEdit's Quick Auto Clean can remove them.");
        md.AppendLine();

        if (lo.Problems.Count > 0)
        {
            md.AppendLine("## Load-order problems");
            md.AppendLine();
            foreach (var p in lo.Problems) md.AppendLine($"- {Impact.OneLine(p)}");
            md.AppendLine();
        }

        md.AppendLine("## Official changes lost to mods");
        md.AppendLine();
        md.AppendLine("A mod built without a DLC or update as a master overrides a record that the DLC or update also changed. Where the mod wins, the official change is undone in game.");
        md.AppendLine();
        WriteGroups(md, official, lo, a);

        md.AppendLine("## Mod changes lost to other mods");
        md.AppendLine();
        WriteGroups(md, modVsMod, lo, a);

        md.AppendLine("## Overrides that change nothing (ITM)");
        md.AppendLine();
        md.AppendLine("Harmless on their own, but they can undo another plugin's change if they load later. Often a sign of an outdated or carelessly saved plugin.");
        md.AppendLine();
        md.AppendLine("| Plugin | ITM records | Examples |");
        md.AppendLine("|---|---|---|");
        foreach (var g in a.Itms.GroupBy(i => i.Plugin).OrderByDescending(g => g.Count()))
            md.AppendLine($"| {g.Key} | {g.Count()} | {string.Join(", ", g.Take(5).Select(i => $"{i.RecordType} {Md(Impact.OneLine(i.EditorID ?? i.FormKey.ToString()))}"))} |");
        md.AppendLine();

        md.AppendLine("## Load order used");
        md.AppendLine();
        md.AppendLine("| # | Plugin | Official | Masters |");
        md.AppendLine("|---|---|---|---|");
        foreach (var p in lo.Plugins)
            md.AppendLine($"| {p.Index} | {p.FileName} | {(p.Official ? "yes: " + LoadOrderSetup.OfficialName(p.FileName) : "")} | {string.Join(", ", p.Masters)} |");
        md.AppendLine();

        md.AppendLine("## What this report does not check");
        md.AppendLine();
        md.AppendLine($"- Fields ignored as bookkeeping or engine-generated: {string.Join(", ", FieldCompare.IgnoredFields.Order())}; also the 3 unused bytes after a cell grid's land flags (XCLC), which hold junk in the official files.");
        md.AppendLine("- Child records (cell references, quest dialogue, navmeshes) are checked as records of their own, not as part of their parent.");
        md.AppendLine("- Scripts, assets (meshes, textures, sounds) and loose files are not checked; only plugin records.");
        md.AppendLine();

        if (a.UnreadableFields.Count > 0 || a.Errors.Count > 0)
        {
            md.AppendLine("## Tool limits hit");
            md.AppendLine();
            md.AppendLine("Fields Mutagen could not read were skipped, so conflicts in them are not reported.");
            md.AppendLine();
            foreach (var (k, v) in a.UnreadableFields.OrderByDescending(kv => kv.Value)) md.AppendLine($"- {k}: {v} times");
            foreach (var e in a.Errors.Take(50)) md.AppendLine($"- {e}");
            if (a.Errors.Count > 50) md.AppendLine($"- … and {a.Errors.Count - 50} more errors (see conflicts.json)");
            md.AppendLine();
        }

        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
        HtmlReport.Write(Path.Combine(outDir, "report.html"), lo, a);
        var json = new
        {
            generated = DateTime.Now,
            loadOrder = lo.Plugins.Select(p => new { p.Index, p.FileName, p.Official, p.Masters }),
            loadOrderSource = lo.OrderSource,
            loadOrderProblems = lo.Problems,
            a.RecordsOverriddenByMods,
            lost = a.Lost.Select(l => new { formKey = l.FormKey.ToString(), l.RecordType, l.EditorID, l.Loser, l.LoserOfficial, l.Winner, l.Fields }),
            itms = a.Itms.Select(i => new { i.Plugin, formKey = i.FormKey.ToString(), i.RecordType, i.EditorID }),
            a.UnreadableFields,
            a.Errors,
        };
        File.WriteAllText(Path.Combine(outDir, "conflicts.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Cut(string s) => Md(s.Length > 160 ? s[..160] + "…" : s);

    // A table cell from plugin text: a "|" would end the cell early (0.5.0 code review).
    static string Md(string? s) => (s ?? "").Replace("|", "\\|");

    static void WriteGroups(StringBuilder md, List<LostChange> items, LoadOrderSetup lo, Analysis a)
    {
        if (items.Count == 0) { md.AppendLine("None found."); md.AppendLine(); return; }
        md.AppendLine("| Winning plugin | Loses changes from | Records | Forwardable fields | Choice fields | No effect |");
        md.AppendLine("|---|---|---|---|---|---|");
        var groups = items.GroupBy(l => (l.Winner, l.Loser)).OrderByDescending(g => g.Count()).ToList();
        foreach (var g in groups)
            md.AppendLine($"| {LoadOrderSetup.Display(g.Key.Winner)} | {LoadOrderSetup.Display(g.Key.Loser)} | {g.Count()} | {g.Sum(l => l.Fields.Count(f => !f.WinnerChangedToo && !Impact.IsMinor(f)))} | {g.Sum(l => l.Fields.Count(f => f.WinnerChangedToo && !Impact.IsMinor(f)))} | {g.Sum(l => l.Fields.Count(Impact.IsMinor))} |");
        md.AppendLine();
        foreach (var g in groups)
        {
            md.AppendLine($"### {LoadOrderSetup.Display(g.Key.Winner)} over {LoadOrderSetup.Display(g.Key.Loser)}");
            md.AppendLine();
            md.AppendLine("**Impact:**");
            md.AppendLine();
            foreach (var line in Impact.ForGroup(g)) md.AppendLine($"- {line}");
            var recs = Recommend.ForGroup(g, lo, a);
            if (recs.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("**Recommendation:**");
                md.AppendLine();
                foreach (var r in recs.OrderBy(r => r.Priority)) md.AppendLine($"- **{r.Level}** — {r.Text}");
            }
            md.AppendLine();
            md.AppendLine("| Record | Field | Kind | Impact | Lost value | Winning value |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var l in g.Take(RowsPerGroup))
                foreach (var f in l.Fields)
                    md.AppendLine($"| {l.RecordType} {Md(Impact.OneLine(l.EditorID))} `{l.FormKey}` | {f.Field} | {(Impact.IsMinor(f) ? "No effect" : f.WinnerChangedToo ? "Choice" : "Forwardable")} | {Impact.Flat(Impact.ForField(l, f)).Replace("|", "/")} | {Cut(f.LoserValue)} | {Cut(f.WinnerValue)} |");
            if (g.Count() > RowsPerGroup) md.AppendLine($"\n… {g.Count() - RowsPerGroup} more records in conflicts.json");
            md.AppendLine();
        }
    }
}
