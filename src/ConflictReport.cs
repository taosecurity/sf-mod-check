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
        md.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Load order: {Md(lo.OrderSource)}.");
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
        md.AppendLine($"| Overrides that change a record's type | {a.TypeChanges.Count} |");
        md.AppendLine($"| Missing scripts and models | {AssetsCount(a)} |");
        if (a.ArchivesCounted) md.AppendLine($"| Archives loaded (general / texture; {AssetIndex.ArchiveSlots} slots each) | {ArchiveCountText(a)} |");
        md.AppendLine();
        md.AppendLine("How to read the tables: **Forwardable** means the winning plugin did not mean to change that field; it just carries an older value, so a patch can copy the lost value forward safely. **Choice** means both plugins changed the same field, so someone has to pick. **No effect** means the versions differ only in list order, letter case or undocumented fields.");
        md.AppendLine();

        var allGroups = a.Lost.GroupBy(l => (l.Winner, l.Loser)).ToList();
        var recs = allGroups.SelectMany(g => Recommend.ForGroup(g, lo, a)).OrderBy(r => r.Priority).ToList();
        if (Recommend.Combined(allGroups, lo, a) is { } combined) recs.Insert(0, combined);
        md.AppendLine("## Recommended actions");
        md.AppendLine();
        var assetActions = AssetActions(a);
        if (recs.Count == 0 && a.TypeChanges.Count == 0 && assetActions.Count == 0) md.AppendLine("Nothing needs doing.");
        // the archive limit (High: a crash risk) comes first, the other asset actions after the conflicts (code review 2026-11-18)
        foreach (var r in assetActions.Where(IsArchiveLimit))
            md.AppendLine($"- **{r.Level}** — {Md(r.Winner)}: {Md(r.Text)} (\"Missing assets\" below).");
        foreach (var r in recs)
        {
            md.AppendLine($"- **{r.Level}** — {(r.Winner.Length > 0 ? $"{Md(LoadOrderSetup.Display(r.Winner))} over {Md(LoadOrderSetup.Display(r.Loser))}" : "All patchable conflicts")}: {Md(r.Text)}");
            if (r.Prompt is not null)
            {
                // Indented fenced block so it stays inside the list item.
                md.AppendLine().AppendLine("  Prompt for an AI assistant:").AppendLine().AppendLine("  ```text");
                foreach (var line in r.Prompt.Replace("\r", "").Split('\n')) md.AppendLine("  " + line);
                md.AppendLine("  ```").AppendLine();
            }
        }
        foreach (var g in TypeChangesByPlugin(a))
            md.AppendLine($"- **Medium** — {Md(g.Key)}: {g.Count()} override{(g.Count() == 1 ? "" : "s")} change{(g.Count() == 1 ? "s" : "")} a record's type, so the game drops one of the two records (\"Overrides that change a record's type\" below).");
        foreach (var r in assetActions.Where(r => !IsArchiveLimit(r)))
            md.AppendLine($"- **{r.Level}** — {(r.Winner.Length > 0 ? Md(r.Winner) + ": " : "")}{Md(r.Text)} (\"Missing assets\" below).");
        if (a.Itms.Count > 0) md.AppendLine($"- **Low** — Optional: {a.Itms.Count} ITM override(s) in {Md(string.Join(", ", a.Itms.Select(i => i.Plugin).Distinct()))}. Harmless here; xEdit's Quick Auto Clean can remove them.");
        md.AppendLine();

        if (lo.Problems.Count > 0)
        {
            md.AppendLine("## Load-order problems");
            md.AppendLine();
            foreach (var p in lo.Problems) md.AppendLine($"- {Md(p)}");   // one line already (LoadOrderSetup.AddProblem)
            md.AppendLine();
        }

        if (a.TypeChanges.Count > 0)
        {
            md.AppendLine("## Overrides that change a record's type");
            md.AppendLine();
            md.AppendLine($"A plugin has a record whose FormID belongs to an earlier record of another type. {Md(TypeChanges.Hint)}");
            md.AppendLine();
            md.AppendLine("| Record | Plugin | Finding |");
            md.AppendLine("|---|---|---|");
            foreach (var t in a.TypeChanges)
                md.AppendLine($"| {Code(t.FormKey.ToString())} {Md(t.EditorID)} | {Md(t.Plugin)} | {Md(t.Text)} |");
            md.AppendLine();
        }

        md.AppendLine("## Missing assets");
        md.AppendLine();
        md.AppendLine(Md(AssetsIntro(a)));
        md.AppendLine();
        if (a.ArchivesCounted)
        {
            md.AppendLine(Md(ArchiveCountIntro(a)));
            md.AppendLine();
            foreach (var w in Assets.ArchiveLimitWarnings(a)) md.AppendLine($"- **WARNING** {Md(w)}.");
            if (Assets.ArchiveLimitWarnings(a).Count > 0) md.AppendLine();
        }
        if (a.AssetsChecked)
        {
            md.AppendLine(Md(Hints.For("assets")));
            md.AppendLine();
            foreach (var (archive, why) in a.UnreadableArchives)
                md.AppendLine($"- **WARNING** archive {Md(archive)} can't be read ({Md(Impact.OneLine(why))}), so its files count as missing.");
            if (a.UnreadableArchives.Count > 0) md.AppendLine();
            if (a.MissingAssets.Count == 0) { md.AppendLine(a.AssetsFailed != null ? "None found before the check stopped." : "None found."); md.AppendLine(); }
            foreach (var g in AssetsByPlugin(a))
            {
                md.AppendLine($"### {Md(g.Key)}");
                md.AppendLine();
                md.AppendLine("| Severity | Kind | File | Used by |");
                md.AppendLine("|---|---|---|---|");
                foreach (var f in g.Take(RowsPerGroup))
                    md.AppendLine($"| {f.Severity} | {f.Kind} | {(f.Script is null ? "" : Md(f.Script) + " ")}{Code(f.Path)} | {Md(Assets.Users(f.Users))} |");
                if (g.Count() > RowsPerGroup) md.AppendLine($"\n… {g.Count() - RowsPerGroup} more in conflicts.json");
                md.AppendLine();
            }
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
            md.AppendLine($"| {Md(g.Key)} | {g.Count()} | {string.Join(", ", g.Take(5).Select(i => $"{Md(i.RecordType)} {Md(Impact.OneLine(i.EditorID ?? i.FormKey.ToString()))}"))} |");
        md.AppendLine();

        md.AppendLine("## Load order used");
        md.AppendLine();
        md.AppendLine("| # | Plugin | Official | Masters |");
        md.AppendLine("|---|---|---|---|");
        foreach (var p in lo.Plugins)
            md.AppendLine($"| {p.Index} | {Md(p.FileName)} | {(p.Official ? "yes: " + LoadOrderSetup.OfficialName(p.FileName) : "")} | {Md(Impact.OneLine(string.Join(", ", p.Masters)))} |");
        md.AppendLine();

        md.AppendLine("## What this report does not check");
        md.AppendLine();
        md.AppendLine($"- Fields ignored as bookkeeping or engine-generated: {string.Join(", ", FieldCompare.IgnoredFields.Order())}; also the 3 unused bytes after a cell grid's land flags (XCLC), which hold junk in the official files.");
        md.AppendLine("- Child records (cell references, quest dialogue, navmeshes) are checked as records of their own, not as part of their parent.");
        md.AppendLine($"- {Md(AssetsNotChecked(a))}");
        md.AppendLine();

        if (a.UnreadableFields.Count > 0 || a.Errors.Count > 0)
        {
            md.AppendLine("## Tool limits hit");
            md.AppendLine();
            md.AppendLine("Fields Mutagen could not read were skipped, so conflicts in them are not reported.");
            md.AppendLine();
            foreach (var (k, v) in a.UnreadableFields.OrderByDescending(kv => kv.Value)) md.AppendLine($"- {Md(k)}: {v} times");
            foreach (var e in a.Errors.Take(50)) md.AppendLine($"- {Md(Impact.OneLine(e))}");
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
            typeChanges = a.TypeChanges.Select(t => new { formKey = t.FormKey.ToString(), t.EditorID, t.Plugin, t.PluginType, t.First, t.FirstType, finding = t.Text }),
            // 0.6.0: assetsChecked is false with --no-assets (then the lists are empty because nothing was checked)
            assetsChecked = a.AssetsChecked,
            archivesRead = a.ArchivesRead,
            inheritedAssetUses = a.AssetsInherited,
            // why the assets step or the archive count stopped part way, else null (also in errors)
            assetsFailed = a.AssetsFailed,
            missingAssets = AssetsByPlugin(a).SelectMany(g => g).Select(f => new { plugin = f.Plugin, severity = f.Severity, kind = f.Kind, path = f.Path, script = f.Script, users = f.Users, finding = f.Text }),
            unreadableArchives = a.UnreadableArchives.Select(u => new { archive = u.Archive, why = u.Why }),
            // 0.6.0: the archives the game would load per registry (null when not counted); slots is the game's limit
            archiveCounts = a.ArchivesCounted ? new { gnrl = a.GnrlArchives, dx10 = a.Dx10Archives, slots = AssetIndex.ArchiveSlots } : null,
            archiveLimitWarnings = Assets.ArchiveLimitWarnings(a),
        };
        File.WriteAllText(Path.Combine(outDir, "conflicts.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Cut(string s) => Md(s.Length > 160 ? s[..160] + "…" : s);

    /// <summary>Type-changing overrides by plugin, in load order (one recommendation per plugin, in both reports).</summary>
    public static List<IGrouping<string, TypeChange>> TypeChangesByPlugin(Analysis a) => a.TypeChanges.GroupBy(t => t.Plugin).ToList();

    // The assets check (0.6.0 part 2), shared by report.md, report.html and conflicts.json.

    /// <summary>One missing script or model with its plugin, flattened for the reports.</summary>
    public record AssetRow(string Plugin, string Severity, string Kind, string Path, string? Script, IReadOnlyList<string> Users, string Text);

    /// <summary>Missing assets by plugin in load order; in each plugin the scripts (ERROR) before the models (WARNING),
    /// each kind in path order.</summary>
    public static List<IGrouping<string, AssetRow>> AssetsByPlugin(Analysis a) =>
        a.MissingAssets.GroupBy(f => f.Plugin)
            .SelectMany(g => g.OrderBy(f => f.Asset.Kind == "script" ? 0 : 1).ThenBy(f => f.Asset.Path, StringComparer.Ordinal))
            .Select(f => new AssetRow(f.Plugin, f.Asset.Severity, f.Asset.Kind, f.Asset.Path, f.Asset.Script, f.Asset.Users, f.Asset.Text))
            .GroupBy(r => r.Plugin).ToList();

    /// <summary>The summary count: "3 (1 script, 2 models)", "0" or "not checked (--no-assets)".</summary>
    public static string AssetsCount(Analysis a)
    {
        if (!a.AssetsChecked) return a.AssetsFailed != null ? "not checked (the step stopped)" : "not checked (--no-assets)";
        if (a.MissingAssets.Count == 0) return "0";
        var s = a.MissingAssets.Count(f => f.Asset.Kind == "script");
        var m = a.MissingAssets.Count - s;
        return $"{a.MissingAssets.Count} ({s} script{(s == 1 ? "" : "s")}, {m} model{(m == 1 ? "" : "s")})";
    }

    /// <summary>The section's first paragraph: what was checked, or that the check was off.</summary>
    public static string AssetsIntro(Analysis a) => !a.AssetsChecked
        ? (a.AssetsFailed != null ? $"Not checked: the step stopped ({a.AssetsFailed}); \"Tool limits hit\" below has the details." : "Not checked: the run used --no-assets.")
        : (a.AssetsFailed != null ? $"The check stopped part way ({a.AssetsFailed}), so this list may be incomplete. " : "") +
          "Every script a mod plugin's records attach needs its .pex, and every model a record names needs its file, as a " +
          "loose file under Data or inside an archive the game loads (the archives of every active plugin and those the " +
          $"game's INI lists). Checked {a.AssetScriptUses} script use{(a.AssetScriptUses == 1 ? "" : "s")} and {a.AssetModelUses} " +
          $"model use{(a.AssetModelUses == 1 ? "" : "s")} in the mod plugins against {a.ArchivesRead} archive{(a.ArchivesRead == 1 ? "" : "s")} " +
          "and the loose files. A missing script is an ERROR (that record's script never runs); a missing model is a WARNING " +
          "(an empty or error mesh in game). Official plugins are not checked, and neither is a missing path an override " +
          "inherits: when the version it overrides names the same path, the mistake isn't the override's" +
          (a.AssetsInherited > 0 ? $" ({a.AssetsInherited} such use{(a.AssetsInherited == 1 ? "" : "s")} left out; --verbose lists them)." : ".");

    /// <summary>The summary cell: "102 / 151", with "(over the limit)" when either is over 256.</summary>
    public static string ArchiveCountText(Analysis a) =>
        $"{a.GnrlArchives} / {a.Dx10Archives}{(Assets.ArchiveLimitWarnings(a).Count > 0 ? " (over the limit)" : "")}";

    /// <summary>The archive-count paragraph of the "Missing assets" section (shown with --no-assets too).</summary>
    public static string ArchiveCountIntro(Analysis a) =>
        $"Archives the game loads for this load order: {a.GnrlArchives} general (GNRL) and {a.Dx10Archives} texture (DX10). " +
        $"The game has {AssetIndex.ArchiveSlots} slots for each kind; more than that leads to memory damage, wrong textures or crashes. " +
        "Counted: the archives named after each active plugin and those the game's INI lists (other languages' voice archives don't load and aren't counted). " +
        "Each archive is counted by its header type (GNRL general, DX10 texture), as the game does, so a GNRL archive named \"- Textures.ba2\" counts as general." +
        (a.UnreadableArchives.Count is var u and > 0
            ? $" {u} archive{(u == 1 ? "" : "s")} couldn't be read and {(u == 1 ? "isn't" : "aren't")} in either count, so the true counts may be higher: " +
              string.Join(", ", a.UnreadableArchives.Take(3).Select(x => x.Archive)) + (u > 3 ? $" and {u - 3} more" : "") + "."
            : "");

    /// <summary>The "What this report does not check" line about assets.</summary>
    public static string AssetsNotChecked(Analysis a) => a.AssetsChecked
        ? "Assets other than scripts and models (textures, materials, .mesh geometry, sounds, voice and animation files) and script parents are not checked, and neither are official plugins' scripts and models."
        : a.AssetsFailed != null
            ? $"Scripts, models and other assets are not checked: the step stopped ({a.AssetsFailed})."
            : "Scripts, models and other assets are not checked (--no-assets); only plugin records.";

    /// <summary>Recommended actions for the assets check: one per plugin (Medium when a script is missing, Low for
    /// models only), one per archive that can't be read (Medium), and first one per archive registry over the game's
    /// 256 slots (High, Winner "Archive limit"). Winner holds the plugin or archive name.</summary>
    public static bool IsArchiveLimit(Recommendation r) => r.Winner == "Archive limit" && r.Loser.Length == 0;

    public static List<Recommendation> AssetActions(Analysis a)
    {
        var list = new List<Recommendation>();
        foreach (var w in Assets.ArchiveLimitWarnings(a))
            list.Add(new Recommendation(1, "Archive limit", "", w));
        foreach (var g in AssetsByPlugin(a))
        {
            var s = g.Count(f => f.Kind == "script");
            var m = g.Count() - s;
            var parts = new List<string>();
            if (s > 0) parts.Add($"{s} missing script{(s == 1 ? "" : "s")}");
            if (m > 0) parts.Add($"{m} missing model{(m == 1 ? "" : "s")}");
            list.Add(new Recommendation(s > 0 ? 2 : 3, g.Key, "",
                $"{string.Join(" and ", parts)}; {(s > 0 ? "those records' scripts never run" : "those records show an empty or error mesh")}. Reinstall the mod or ask its author for the missing files"));
        }
        foreach (var (archive, _) in a.UnreadableArchives)
            list.Add(new Recommendation(2, archive, "", "this archive can't be read, so its files count as missing. Check the file (reinstall the mod that brings it)"));
        return list;
    }

    // Plugin text in report.md, in table cells, list items and headings (0.5.1: everywhere): "|" would end a cell early,
    // < > could start HTML, [ ] a link, and ` * _ a code span or emphasis (an EditorID with a backtick paired with the
    // FormKey's code span). Each character is escaped once, so an escape never gets escaped again.
    public static string Md(string? s)
    {
        var sb = new StringBuilder((s ?? "").Length);
        foreach (var c in s ?? "")
        {
            if (c is '\\' or '|' or '<' or '>' or '[' or ']' or '`' or '*' or '_') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // A code span that holds any text (a FormKey's file name may contain backticks): the fence is one backtick longer
    // than the longest run inside, padded with spaces when the text starts or ends with one (CommonMark). Backslashes
    // are literal in a code span, but GitHub's tables still split on "|", so that one is escaped.
    public static string Code(string s)
    {
        int run = 0, longest = 0;
        foreach (var c in s) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', longest + 1);
        var pad = s.StartsWith('`') || s.EndsWith('`') ? " " : "";
        return fence + pad + s.Replace("|", "\\|") + pad + fence;
    }

    static void WriteGroups(StringBuilder md, List<LostChange> items, LoadOrderSetup lo, Analysis a)
    {
        if (items.Count == 0) { md.AppendLine("None found."); md.AppendLine(); return; }
        md.AppendLine("| Winning plugin | Loses changes from | Records | Forwardable fields | Choice fields | No effect |");
        md.AppendLine("|---|---|---|---|---|---|");
        var groups = items.GroupBy(l => (l.Winner, l.Loser)).OrderByDescending(g => g.Count()).ToList();
        foreach (var g in groups)
            md.AppendLine($"| {Md(LoadOrderSetup.Display(g.Key.Winner))} | {Md(LoadOrderSetup.Display(g.Key.Loser))} | {g.Count()} | {g.Sum(l => l.Fields.Count(f => !f.WinnerChangedToo && !Impact.IsMinor(f)))} | {g.Sum(l => l.Fields.Count(f => f.WinnerChangedToo && !Impact.IsMinor(f)))} | {g.Sum(l => l.Fields.Count(Impact.IsMinor))} |");
        md.AppendLine();
        foreach (var g in groups)
        {
            md.AppendLine($"### {Md(LoadOrderSetup.Display(g.Key.Winner))} over {Md(LoadOrderSetup.Display(g.Key.Loser))}");
            md.AppendLine();
            md.AppendLine("**Impact:**");
            md.AppendLine();
            foreach (var line in Impact.ForGroup(g)) md.AppendLine($"- {Md(line)}");
            var recs = Recommend.ForGroup(g, lo, a);
            if (recs.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("**Recommendation:**");
                md.AppendLine();
                foreach (var r in recs.OrderBy(r => r.Priority)) md.AppendLine($"- **{r.Level}** — {Md(r.Text)}");
            }
            md.AppendLine();
            md.AppendLine("| Record | Field | Kind | Impact | Lost value | Winning value |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var l in g.Take(RowsPerGroup))
                foreach (var f in l.Fields)
                    md.AppendLine($"| {Md(l.RecordType)} {Md(Impact.OneLine(l.EditorID))} {Code(l.FormKey.ToString())} | {Md(f.Field)} | {(Impact.IsMinor(f) ? "No effect" : f.WinnerChangedToo ? "Choice" : "Forwardable")} | {Md(Impact.Flat(Impact.ForField(l, f)))} | {Cut(f.LoserValue)} | {Cut(f.WinnerValue)} |");
            if (g.Count() > RowsPerGroup) md.AppendLine($"\n… {g.Count() - RowsPerGroup} more records in conflicts.json");
            md.AppendLine();
        }
    }
}
