// Moved from StarfieldConflictCheck (HtmlReport.cs, commit d336108) into sfmodcheck on 2026-11-16.
// Writes report.html: the same content as report.md in a single self-contained page that is easier to read.
using System.Text;
using static System.Net.WebUtility;

namespace SfModCheck.Conflict;


public static class HtmlReport
{
    public static void Write(string path, LoadOrderSetup lo, Analysis a)
    {
        var official = a.Lost.Where(l => l.LoserOfficial).ToList();
        var modVsMod = a.Lost.Where(l => !l.LoserOfficial).ToList();
        var h = new StringBuilder();
        var nav = Nav(lo, a, official, modVsMod);   // made first, so the page is never copied to place it (0.5.1)
        // The page body; Write() wraps it into a full document. Nothing is loaded from the web (PRIVACY.md): the fonts fall
        // back to the system's own when Public Sans and JetBrains Mono aren't installed.
        h.Append($$"""
            <title>Starfield Conflict Report</title>
            <style>
            /* Layout: a single reading column of conflict cards; summary and actions first, detail collapsed. */
            :root { --bg:#f6f7f9; --panel:#ffffff; --text:#1b1d24; --muted:#5f6472; --line:#dfe2e8; --code:#eef0f4;
                    --fwd:#1f7a4d; --fwd-bg:#e3f4ea; --choice:#a15c00; --choice-bg:#fbefd9; --bad:#b3261e; --accent:#3557c4;
                    --font-body:"Public Sans",system-ui,-apple-system,"Segoe UI",sans-serif; --font-mono:"JetBrains Mono",ui-monospace,Consolas,monospace; }
            @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#14161b; --panel:#1d2027; --text:#eceef3; --muted:#a2a7b4; --line:#323642;
                    --code:#272a33; --fwd:#7fd6a5; --fwd-bg:#1c3a2a; --choice:#f2bf6c; --choice-bg:#3d2f17; --bad:#ff8a80; --accent:#8fa8ff; color-scheme:dark; } }
            :root[data-theme="dark"] { --bg:#14161b; --panel:#1d2027; --text:#eceef3; --muted:#a2a7b4; --line:#323642;
                    --code:#272a33; --fwd:#7fd6a5; --fwd-bg:#1c3a2a; --choice:#f2bf6c; --choice-bg:#3d2f17; --bad:#ff8a80; --accent:#8fa8ff; color-scheme:dark; }
            * { box-sizing:border-box; }
            body { margin:0; background:var(--bg); color:var(--text); font:15px/1.5 var(--font-body); }
            /* Sidebar menu on the left, report on the right; the menu becomes a scrollable bar on narrow screens. */
            .layout { display:grid; grid-template-columns:250px minmax(0, 1fr); gap:32px; max-width:1480px; margin:0 auto;
                      padding-left:max(16px, env(safe-area-inset-left, 0px)); padding-right:max(16px, env(safe-area-inset-right, 0px)); }
            main { min-width:0; padding-block:24px 64px; }
            nav.side { position:sticky; top:env(safe-area-inset-top, 0px); align-self:start; max-height:100vh; overflow-y:auto;
                       padding-block:24px; font-size:14px; }
            nav.side .brand { font-weight:700; font-size:15px; margin:2px 0 14px; padding:0 10px; }
            nav.side ul { list-style:none; margin:0; padding:0; display:grid; grid-template-columns:minmax(0, 1fr); gap:2px; }
            nav.side li, nav.side a { min-width:0; }
            nav.side span.name { flex:1 1 auto; min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            nav.side span.l1, nav.side span.l2 { display:block; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            nav.side span.l1 { color:var(--text); } nav.side span.l2 { font-size:12px; color:var(--muted); }
            nav.side ul.pairs a { align-items:center; }
            nav.side a { display:flex; align-items:baseline; gap:8px; padding:5px 10px; border-radius:6px; color:var(--text); }
            nav.side a:hover { background:var(--code); text-decoration:none; }
            nav.side a[aria-current="true"] { background:var(--code); color:var(--accent); font-weight:600; }
            nav.side .count { margin-left:auto; color:var(--muted); font-size:12px; font-variant-numeric:tabular-nums; }
            nav.side ul.pairs { margin:2px 0 8px 10px; padding-left:8px; border-left:1px solid var(--line); }
            nav.side ul.pairs a { font-size:13px; color:var(--muted); padding:3px 8px; }
            nav.side ul.pairs a span.name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; min-width:0; }
            .dot { width:8px; height:8px; border-radius:50%; flex:none; align-self:center; background:var(--line); }
            .dot.p1 { background:var(--bad); } .dot.p2 { background:var(--choice); } .dot.p3 { background:var(--muted); }
            h2[id] { scroll-margin-top:16px; }
            @media (max-width:900px) {
              .layout { grid-template-columns:minmax(0, 1fr); gap:0; }
              nav.side { position:static; max-height:none; padding-block:12px 0; border-bottom:1px solid var(--line); }
              nav.side .brand, nav.side ul.pairs { display:none; }
              nav.side > ul { display:flex; gap:6px; overflow-x:auto; padding-bottom:10px; }
              nav.side > ul > li > a { white-space:nowrap; border:1px solid var(--line); }
            }
            h1, h2 { text-wrap:balance; }
            :focus-visible { outline:2px solid var(--accent); outline-offset:2px; }
            h1 { font-size:26px; margin:0 0 4px; } h2 { font-size:20px; margin:36px 0 8px; } .muted { color:var(--muted); }
            .cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr)); gap:12px; margin:20px 0; }
            .card { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:14px; }
            .card .n { font-size:28px; font-weight:650; } .card .l { color:var(--muted); font-size:13px; }
            .card.warn .n { color:var(--bad); }
            .explain { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:12px 16px; }
            table { width:100%; border-collapse:collapse; background:var(--panel); border:1px solid var(--line); border-radius:10px; overflow:hidden; }
            th, td { text-align:left; vertical-align:top; padding:7px 10px; border-bottom:1px solid var(--line); }
            th { font-size:13px; color:var(--muted); font-weight:600; }
            td.num { text-align:right; font-variant-numeric:tabular-nums; }
            details.pair { background:var(--panel); border:1px solid var(--line); border-radius:10px; margin:10px 0; }
            details.pair > summary { cursor:pointer; padding:12px 16px; font-weight:600; }
            details.pair > div { padding:0 16px 16px; overflow-x:auto; }
            details.pair table { border:none; }
            .scroll { overflow-x:auto; border-radius:10px; }
            tr.rec td { background:var(--code); font-weight:600; }
            tr.impact td { font-size:13.5px; padding-top:0; border-bottom:1px solid var(--line); }
            tr.impact + tr.impact td, tr:has(+ tr.impact) td { border-bottom:none; }
            dl.story { display:grid; grid-template-columns:minmax(120px, 220px) 1fr; gap:4px 14px; margin:2px 0 6px; font-size:13.5px; }
            dl.story dt { color:var(--muted); font-weight:600; overflow-wrap:anywhere; } dl.story dd { margin:0; overflow-wrap:anywhere; }
            @media (max-width:640px) { dl.story { grid-template-columns:1fr; } dl.story dd { margin-bottom:6px; } }
            .impact-box { border-left:4px solid var(--accent); background:var(--code); border-radius:6px; padding:10px 14px; margin:0 0 14px; }
            details.prompt { margin:6px 0 2px; } details.prompt summary { cursor:pointer; color:var(--accent); font-size:13px; font-weight:600; }
            details.prompt pre { white-space:pre-wrap; overflow-wrap:anywhere; background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:10px 12px; margin:6px 0 0; font:12.5px/1.45 var(--font-mono); }
            button.copy { font:inherit; font-size:12px; padding:3px 10px; border-radius:6px; border:1px solid var(--line); background:var(--panel); color:var(--text); cursor:pointer; margin-left:8px; }
            a { color:var(--accent); text-decoration:none; } a:hover { text-decoration:underline; }
            .rec-box { border-left:4px solid var(--fwd); background:var(--fwd-bg); border-radius:6px; padding:10px 14px; margin:0 0 14px; }
            .rec-box ul, ul.recs { margin:6px 0 0; padding-left:20px; } .rec-box li, ul.recs li { margin:6px 0; }
            .badge.p1 { color:#fff; background:var(--bad); } .badge.p2 { color:var(--choice); background:var(--choice-bg); } .badge.p3 { color:var(--muted); border:1px solid var(--line); }
            .impact-box ul { margin:6px 0 0; padding-left:20px; } .impact-box li { margin:4px 0; }
            code, .val { font-family:var(--font-mono); font-size:12.5px; }
            .val { white-space:pre-wrap; overflow-wrap:anywhere; max-width:420px; }
            .val details summary { cursor:pointer; color:var(--accent); }
            .badge { display:inline-block; font-size:12px; font-weight:600; padding:1px 8px; border-radius:99px; white-space:nowrap; }
            .badge.fwd { color:var(--fwd); background:var(--fwd-bg); } .badge.choice { color:var(--choice); background:var(--choice-bg); }
            .badge.minor { color:var(--muted); border:1px solid var(--line); }
            .badge.off { color:var(--accent); border:1px solid var(--accent); }
            input[type=search] { width:100%; max-width:420px; padding:8px 12px; border:1px solid var(--line); border-radius:8px; background:var(--panel); color:var(--text); font:inherit; }
            ul.problems li { margin:4px 0; }
            .hidden { display:none; }
            </style><div class="layout"><nav class="side" aria-label="Report sections">{{nav}}</nav><main>
            <h1 id="summary">Starfield conflict report</h1>
            <div class="muted">Generated {{DateTime.Now:yyyy-MM-dd HH:mm}} · Load order: {{HtmlEncode(lo.OrderSource)}}</div>
            <div class="cards">
            """);
        Card(h, lo.Plugins.Count.ToString(), $"plugins loaded ({lo.Plugins.Count(p => p.Official)} official, {lo.Plugins.Count(p => !p.Official)} mods)");
        Card(h, a.RecordsOverriddenByMods.ToString(), "records changed by mods");
        Card(h, official.Select(l => l.FormKey).Distinct().Count().ToString(), "records losing an official change", official.Count > 0);
        Card(h, modVsMod.Select(l => l.FormKey).Distinct().Count().ToString(), "records losing a mod's change", modVsMod.Count > 0);
        if (a.PrioritiesOn) Card(h, a.Benign.Sum(l => l.Fields.Count).ToString(), "benign differences shown apart (xEdit field priorities)");
        Card(h, a.Itms.Count.ToString(), "overrides that change nothing (ITM)");
        Card(h, lo.Problems.Count.ToString(), "load-order problems", lo.Problems.Count > 0);
        Card(h, a.TypeChanges.Count.ToString(), "overrides that change a record's type", a.TypeChanges.Count > 0);
        Card(h, a.AssetsChecked ? a.MissingAssets.Count.ToString() : "off", a.AssetsChecked ? "missing scripts and models" : $"missing scripts and models: not checked ({(a.AssetsFailed != null ? "the step stopped" : "--no-assets")})",
             a.MissingAssets.Count > 0 || a.UnreadableArchives.Count > 0 || a.AssetsFailed != null);
        if (a.ArchivesCounted)
            Card(h, $"{a.GnrlArchives} / {a.Dx10Archives}", $"archives loaded (general / texture; {AssetIndex.ArchiveSlots} slots each)", Assets.ArchiveLimitWarnings(a).Count > 0);
        h.Append("""
            </div>
            <div class="explain">Each lost change is labeled
            <span class="badge fwd">Forwardable</span>: the winning plugin did not mean to change that field, it only carries an older value, so a patch can restore the lost value safely; or
            <span class="badge choice">Choice</span>: both plugins changed the same field, so you have to pick one; or
            <span class="badge minor">No effect</span>: the versions differ only in list order, letter case or undocumented fields; or
            <span class="badge minor">Lost</span>: an official BlueprintShips file wins; the game loads it after every mod, so no load order or patch brings the change back.</div>
            <p><input type="search" id="filter" placeholder="Filter by plugin, record, EditorID or field…" aria-label="Filter"></p>
            """);

        // All recommendations, most important first, each linking to its conflict section.
        var allGroups = a.Lost.GroupBy(l => (l.Winner, l.Loser)).ToList();
        var recs = allGroups.SelectMany(g => Recommend.ForGroup(g, lo, a)).OrderBy(r => r.Priority).ToList();
        if (Recommend.Combined(allGroups, lo, a) is { } combined) recs.Insert(0, combined);
        h.Append("<h2 id=\"actions\">Recommended actions</h2>");
        var assetActions = Report.AssetActions(a);
        if (recs.Count == 0 && a.TypeChanges.Count == 0 && assetActions.Count == 0) h.Append("<p>Nothing needs doing.</p>");
        else
        {
            h.Append("<ul class=\"recs\">");
            // the archive limit (High: a crash risk) first, as in report.md (code review 2026-11-18)
            foreach (var r in assetActions.Where(Report.IsArchiveLimit))
                h.Append($"<li class=\"f\"><span class=\"badge p{r.Priority}\">{r.Level}</span> <a href=\"#assets\">{HtmlEncode(r.Winner)}</a>: {HtmlEncode(r.Text)}.</li>");
            foreach (var r in recs)
                h.Append($"<li class=\"f\"><span class=\"badge p{r.Priority}\">{r.Level}</span> " +
                         (r.Winner.Length > 0 ? $"<a href=\"#{Anchor(r.Winner, r.Loser)}\">{HtmlEncode(LoadOrderSetup.Display(r.Winner))} over {HtmlEncode(LoadOrderSetup.Display(r.Loser))}</a>: " : "<strong>All patchable conflicts</strong>: ") +
                         $"{HtmlEncode(r.Text)}{PromptBlock(r)}</li>");
            foreach (var g in Report.TypeChangesByPlugin(a))
                h.Append($"<li class=\"f\"><span class=\"badge p2\">Medium</span> <a href=\"#type-changes\">{HtmlEncode(g.Key)}</a>: {g.Count()} override{(g.Count() == 1 ? "" : "s")} " +
                         $"change{(g.Count() == 1 ? "s" : "")} a record's type, so the game drops one of the two records.</li>");
            foreach (var r in assetActions.Where(r => !Report.IsArchiveLimit(r)))
                h.Append($"<li class=\"f\"><span class=\"badge p{r.Priority}\">{r.Level}</span> <a href=\"#assets\">{HtmlEncode(r.Winner)}</a>: {HtmlEncode(r.Text)}.</li>");
            if (a.Itms.Count > 0)
                h.Append($"<li><span class=\"badge p3\">Low</span> Optional: {a.Itms.Count} override{(a.Itms.Count == 1 ? "" : "s")} that change nothing (ITM) in {HtmlEncode(string.Join(", ", a.Itms.Select(i => i.Plugin).Distinct()))}. Harmless here; xEdit's Quick Auto Clean can remove them.</li>");
            h.Append("</ul>");
        }

        if (lo.Problems.Count > 0)
        {
            h.Append("<h2 id=\"problems\">Load-order problems</h2><ul class=\"problems\">");
            foreach (var p in lo.Problems) h.Append($"<li>{HtmlEncode(p)}</li>");
            h.Append("</ul>");
        }

        if (a.TypeChanges.Count > 0)
        {
            h.Append("<h2 id=\"type-changes\">Overrides that change a record's type</h2>");
            h.Append($"<p class=\"muted\">A plugin has a record whose FormID belongs to an earlier record of another type. {HtmlEncode(TypeChanges.Hint)}</p>");
            h.Append("<div class=\"scroll\"><table><tr><th>Record</th><th>Plugin</th><th>Finding</th></tr>");
            foreach (var t in a.TypeChanges)
                h.Append($"<tr class=\"f\"><td><code>{HtmlEncode(t.FormKey.ToString())}</code> {HtmlEncode(t.EditorID ?? "")}</td><td>{HtmlEncode(t.Plugin)}</td><td>{HtmlEncode(t.Text)}</td></tr>");
            h.Append("</table></div>");
        }

        h.Append("<h2 id=\"assets\">Missing assets</h2>");
        h.Append($"<p class=\"muted\">{HtmlEncode(Report.AssetsIntro(a))}</p>");
        if (a.ArchivesCounted)
        {
            h.Append($"<p class=\"muted\">{HtmlEncode(Report.ArchiveCountIntro(a))}</p>");
            if (Assets.ArchiveLimitWarnings(a) is { Count: > 0 } limits)
            {
                h.Append("<ul class=\"problems\">");
                foreach (var w in limits)
                    h.Append($"<li class=\"f\"><span class=\"badge p2\">WARNING</span> {HtmlEncode(w)}.</li>");
                h.Append("</ul>");
            }
        }
        if (a.AssetsChecked)
        {
            h.Append($"<p class=\"muted\">{HtmlEncode(Hints.For("assets") ?? "")}</p>");
            if (a.UnreadableArchives.Count > 0)
            {
                h.Append("<ul class=\"problems\">");
                foreach (var (archive, why) in a.UnreadableArchives)
                    h.Append($"<li class=\"f\"><span class=\"badge p2\">WARNING</span> archive {HtmlEncode(archive)} can't be read ({HtmlEncode(why)}), so its files count as missing.</li>");
                h.Append("</ul>");
            }
            if (a.MissingAssets.Count == 0) h.Append(a.AssetsFailed != null ? "<p>None found before the check stopped.</p>" : "<p>None found.</p>");
            else
            {
                h.Append("<div class=\"scroll\"><table><tr><th>Plugin</th><th>Severity</th><th>Kind</th><th>File</th><th>Used by</th></tr>");
                foreach (var g in Report.AssetsByPlugin(a))
                    foreach (var f in g)
                        h.Append($"<tr class=\"f\"><td>{HtmlEncode(f.Plugin)}</td><td><span class=\"badge {(f.Kind == "script" ? "p1" : "p2")}\">{f.Severity}</span></td><td>{f.Kind}</td>" +
                                 $"<td>{(f.Script is null ? "" : HtmlEncode(f.Script) + " ")}<code>{HtmlEncode(f.Path)}</code></td><td>{HtmlEncode(Assets.Users(f.Users))}</td></tr>");
                h.Append("</table></div>");
            }
        }

        h.Append("<h2 id=\"official\">Official changes lost to mods</h2><p class=\"muted\">A mod built without a DLC or update as a master overrides a record that the DLC or update also changed. Where the mod wins, the official change is undone in game.</p>");
        Groups(h, official, lo, a);
        h.Append("<h2 id=\"mod-vs-mod\">Mod changes lost to other mods</h2><p class=\"muted\">A later mod overrides a record that an earlier mod changed, without having that mod as a master.</p>");
        Groups(h, modVsMod, lo, a);

        if (a.PrioritiesOn)
        {
            h.Append($"<h2 id=\"benign\">Benign differences (not counted as lost)</h2><p class=\"muted\">{HtmlEncode(Report.BenignIntro)}</p>");
            if (a.Benign.Count == 0) h.Append("<p>None found.</p>");
            else
            {
                var table = FieldPriorityTable.Load();
                h.Append("<details class=\"pair\"><summary>Show ").Append(Report.BenignCount(a)).Append("</summary><div><table><tr><th>Record</th><th>Winning plugin</th><th>Loses changes from</th><th>Field</th><th>Priority</th><th>Lost value</th><th>Winning value</th></tr>");
                foreach (var l in a.Benign)
                    foreach (var f in l.Fields)
                        h.Append($"<tr class=\"f\"><td>{HtmlEncode(l.RecordType)} {HtmlEncode(l.EditorID ?? "")} <code>{HtmlEncode(l.FormKey.ToString())}</code></td><td>{HtmlEncode(LoadOrderSetup.Display(l.Winner))}</td>" +
                                 $"<td>{HtmlEncode(LoadOrderSetup.Display(l.Loser))}</td><td>{HtmlEncode(f.Field)}</td><td><span class=\"badge minor\">{table.NameOf(l, f)}</span></td>" +
                                 $"<td class=\"val\">{Value(f.LoserValue)}</td><td class=\"val\">{Value(f.WinnerValue)}</td></tr>");
                h.Append("</table></div></details>");
            }
        }

        h.Append("<h2 id=\"itm\">Overrides that change nothing (ITM)</h2><p class=\"muted\">Harmless on their own, but they can undo another plugin's change if they load later.</p>");
        if (a.Itms.Count == 0) h.Append("<p>None found.</p>");
        else
        {
            h.Append("<div class=\"scroll\"><table><tr><th>Plugin</th><th>Record</th><th>FormKey</th></tr>");
            foreach (var i in a.Itms.OrderBy(i => i.Plugin))
                h.Append($"<tr class=\"f\"><td>{HtmlEncode(i.Plugin)}</td><td>{HtmlEncode(i.RecordType)} {HtmlEncode(i.EditorID ?? "")}</td><td><code>{HtmlEncode(i.FormKey.ToString())}</code></td></tr>");
            h.Append("</table></div>");
        }

        h.Append("<h2 id=\"load-order\">Load order used</h2><details class=\"pair\"><summary>Show all ").Append(lo.Plugins.Count).Append(" plugins</summary><div><table><tr><th>#</th><th>Plugin</th><th></th><th>Masters</th></tr>");
        foreach (var p in lo.Plugins)
            h.Append($"<tr><td class=\"num\">{p.Index}</td><td>{HtmlEncode(p.FileName)}</td><td>{(p.Official ? $"<span class=\"badge off\">official</span> <span class=\"muted\">{HtmlEncode(LoadOrderSetup.OfficialName(p.FileName) ?? "")}</span>" : "")}</td><td class=\"muted\">{HtmlEncode(string.Join(", ", p.Masters))}</td></tr>");
        h.Append("</table></div></details>");

        h.Append("<h2 id=\"not-checked\">What this report does not check</h2><ul class=\"muted\">");
        h.Append($"<li>Fields ignored as bookkeeping or engine-generated: {HtmlEncode(string.Join(", ", FieldCompare.IgnoredFields.Order()))}; also the 3 unused bytes after a cell grid's land flags (XCLC), which hold junk in the official files.</li>");
        h.Append("<li>Child records (cell references, quest dialogue, navmeshes) are checked as records of their own, not as part of their parent.</li>");
        h.Append($"<li>{HtmlEncode(Report.AssetsNotChecked(a))}</li>");
        if (a.PrioritiesOn) h.Append($"<li>{HtmlEncode(Report.PrioritiesNote)}</li>");
        h.Append("</ul>");
        if (a.UnreadableFields.Count > 0 || a.Errors.Count > 0)
        {
            h.Append("<h2 id=\"tool-limits\">Tool limits hit</h2><ul>");
            foreach (var (k, v) in a.UnreadableFields.OrderByDescending(kv => kv.Value)) h.Append($"<li>{HtmlEncode(k)}: {v} times</li>");
            foreach (var e in a.Errors) h.Append($"<li>{HtmlEncode(e)}</li>");
            h.Append("</ul>");
        }

        // Filter: hide record blocks and rows that don't match; open the conflict sections that still have matches.
        h.Append("""
            <script>
            // Copy buttons: clipboard API where allowed, otherwise select-and-copy.
            document.querySelectorAll('button.copy').forEach(b => b.addEventListener('click', async () => {
              const pre = b.parentElement.querySelector('pre');
              try { await navigator.clipboard.writeText(pre.textContent); }
              catch { const r = document.createRange(); r.selectNodeContents(pre); const s = getSelection(); s.removeAllRanges(); s.addRange(r); document.execCommand('copy'); }
              b.textContent = 'Copied'; setTimeout(() => b.textContent = 'Copy', 1500);
            }));
            // Highlight the menu entry for the section being read.
            const navLinks = [...document.querySelectorAll('nav.side a[data-section]')];
            const heads = navLinks.map(l => document.getElementById(l.dataset.section)).filter(Boolean);
            const markCurrent = () => {
              let cur = heads[0];
              for (const hd of heads) if (hd.getBoundingClientRect().top <= 120) cur = hd;
              navLinks.forEach(l => l.setAttribute('aria-current', String(l.dataset.section === cur?.id)));
            };
            addEventListener('scroll', markCurrent, { passive: true }); markCurrent();
            // Links in "Recommended actions" open the conflict section they point to.
            const openTarget = () => { const t = location.hash && document.getElementById(location.hash.slice(1)); if (t && t.tagName === 'DETAILS') { t.open = true; t.scrollIntoView(); } };
            window.addEventListener('hashchange', openTarget); openTarget();
            const box = document.getElementById('filter');
            box.addEventListener('input', () => {
              const q = box.value.trim().toLowerCase();
              document.querySelectorAll('tbody.rec-block, tr.f').forEach(el => el.classList.toggle('hidden', q !== '' && !el.textContent.toLowerCase().includes(q)));
              document.querySelectorAll('details.pair[data-pair]').forEach(d => {
                const hit = q === '' || d.querySelector('summary').textContent.toLowerCase().includes(q) || d.querySelector('tbody.rec-block:not(.hidden)');
                d.classList.toggle('hidden', !hit);
                if (q !== '' && hit) d.open = true;
              });
            });
            </script>
            </main></div>
            """);
        File.WriteAllText(path, "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\"></head><body>\n" + h + "\n</body></html>");
    }

    // The left menu: every section, plus each conflict pair under its section with a dot for its most urgent recommendation.
    static string Nav(LoadOrderSetup lo, Analysis a, List<LostChange> official, List<LostChange> modVsMod)
    {
        var n = new StringBuilder("<div class=\"brand\">Conflict report</div><ul>");
        void Item(string id, string label, int? count = null) =>
            n.Append($"<li><a href=\"#{id}\" data-section=\"{id}\"><span class=\"name\">{HtmlEncode(label)}</span>{(count is null ? "" : $"<span class=\"count\">{count}</span>")}</a>");
        void Pairs(List<LostChange> items)
        {
            if (items.Count == 0) return;
            n.Append("<ul class=\"pairs\">");
            foreach (var g in items.GroupBy(l => (l.Winner, l.Loser)).OrderByDescending(g => g.Count()))
            {
                var recs = Recommend.ForGroup(g, lo, a);
                var level = recs.Count == 0 ? "" : $" p{recs.Min(r => r.Priority)}";
                // Two lines: what loses its changes (friendly name for official files), then who overrides it.
                var loser = LoadOrderSetup.OfficialName(g.Key.Loser) ?? Path.GetFileNameWithoutExtension(g.Key.Loser);
                var winner = Path.GetFileNameWithoutExtension(g.Key.Winner);
                n.Append($"<li><a href=\"#{Anchor(g.Key.Winner, g.Key.Loser)}\" title=\"{HtmlEncode($"{g.Key.Winner} overrides {g.Key.Loser}")}\"><span class=\"dot{level}\"></span>" +
                         $"<span class=\"name\"><span class=\"l1\">{HtmlEncode(loser)}</span><span class=\"l2\">overridden by {HtmlEncode(winner)}</span></span>" +
                         $"<span class=\"count\">{g.Count()}</span></a></li>");
            }
            n.Append("</ul>");
        }
        var groups = a.Lost.GroupBy(l => (l.Winner, l.Loser)).ToList();
        var actions = groups.Sum(g => Recommend.ForGroup(g, lo, a).Count) + (Recommend.Combined(groups, lo, a) is null ? 0 : 1) + (a.Itms.Count > 0 ? 1 : 0)
                      + Report.TypeChangesByPlugin(a).Count + Report.AssetActions(a).Count;
        Item("summary", "Summary"); n.Append("</li>");
        Item("actions", "Recommended actions", actions); n.Append("</li>");
        if (lo.Problems.Count > 0) { Item("problems", "Load-order problems", lo.Problems.Count); n.Append("</li>"); }
        if (a.TypeChanges.Count > 0) { Item("type-changes", "Record type changes", a.TypeChanges.Count); n.Append("</li>"); }
        // the section also lists unreadable archives and the archive-limit warnings: count them too (code review 2026-11-18)
        var limits = Assets.ArchiveLimitWarnings(a).Count;
        Item("assets", "Missing assets", a.AssetsChecked ? a.MissingAssets.Count + a.UnreadableArchives.Count + limits : limits + a.UnreadableArchives.Count is var c and > 0 ? c : null); n.Append("</li>");
        Item("official", "Official changes lost", official.Select(l => l.FormKey).Distinct().Count()); Pairs(official); n.Append("</li>");
        Item("mod-vs-mod", "Mod changes lost", modVsMod.Select(l => l.FormKey).Distinct().Count()); Pairs(modVsMod); n.Append("</li>");
        if (a.PrioritiesOn) { Item("benign", "Benign differences", a.Benign.Sum(l => l.Fields.Count)); n.Append("</li>"); }
        Item("itm", "ITM records", a.Itms.Count); n.Append("</li>");
        Item("load-order", "Load order used", lo.Plugins.Count); n.Append("</li>");
        Item("not-checked", "Not checked"); n.Append("</li>");
        if (a.UnreadableFields.Count > 0 || a.Errors.Count > 0) { Item("tool-limits", "Tool limits hit"); n.Append("</li>"); }
        return n.Append("</ul>").ToString();
    }

    static void Card(StringBuilder h, string n, string label, bool warn = false) =>
        h.Append($"<div class=\"card{(warn ? " warn" : "")}\"><div class=\"n\">{n}</div><div class=\"l\">{HtmlEncode(label)}</div></div>");

    // A collapsible, copyable prompt for an AI coding assistant under a recommendation that has one.
    static string PromptBlock(Recommendation r) => r.Prompt is null ? "" :
        $"<details class=\"prompt\"><summary>Prompt for an AI assistant</summary><button class=\"copy\" type=\"button\">Copy</button><pre>{HtmlEncode(r.Prompt)}</pre></details>";

    // Readable part plus a short hash of the exact pair, so pairs such as (A-B.esm, C.esm) and (A.esm, B-C.esm) never
    // share an id (0.5.0 code review). The hash is FNV-1a over the lower-case names: stable from run to run.
    public static string Anchor(string winner, string loser)
    {
        uint h = 2166136261;
        foreach (var c in (winner + "\u0001" + loser).ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return "c-" + System.Text.RegularExpressions.Regex.Replace(winner + "-" + loser, "[^A-Za-z0-9]+", "-") + "-" + h.ToString("x8");
    }

    // The Kind column's badge (Impact.Kind; "Lost" since 0.12.0 for a BlueprintShips winner).
    static string KindBadge(string kind) => kind switch
    {
        "Forwardable" => "<span class=\"badge fwd\">Forwardable</span>",
        "Choice" => "<span class=\"badge choice\">Choice</span>",
        _ => $"<span class=\"badge minor\">{kind}</span>",
    };

    static void Groups(StringBuilder h, List<LostChange> items, LoadOrderSetup lo, Analysis a)
    {
        if (items.Count == 0) { h.Append("<p>None found.</p>"); return; }
        var groups = items.GroupBy(l => (l.Winner, l.Loser)).OrderByDescending(g => g.Count()).ToList();
        h.Append("<div class=\"scroll\"><table><tr><th>Winning plugin</th><th>Loses changes from</th><th>Records</th><th>Forwardable fields</th><th>Choice fields</th><th>No effect</th></tr>");
        foreach (var g in groups)
            h.Append($"<tr class=\"f\"><td>{HtmlEncode(LoadOrderSetup.Display(g.Key.Winner))}</td><td>{HtmlEncode(LoadOrderSetup.Display(g.Key.Loser))}</td><td class=\"num\">{g.Count()}</td>" +
                     $"<td class=\"num\">{g.Sum(l => l.Fields.Count(f => Impact.Kind(l, f) == "Forwardable"))}</td><td class=\"num\">{g.Sum(l => l.Fields.Count(f => Impact.Kind(l, f) == "Choice"))}</td><td class=\"num\">{g.Sum(l => l.Fields.Count(Impact.IsMinor))}</td></tr>");
        h.Append("</table></div>");
        foreach (var g in groups)
        {
            var fwd = g.Sum(l => l.Fields.Count(f => Impact.Kind(l, f) == "Forwardable"));
            var choice = g.Sum(l => l.Fields.Count(f => Impact.Kind(l, f) == "Choice"));
            var minor = g.Sum(l => l.Fields.Count(Impact.IsMinor));
            h.Append($"<details class=\"pair\" data-pair id=\"{Anchor(g.Key.Winner, g.Key.Loser)}\"><summary>{HtmlEncode(LoadOrderSetup.Display(g.Key.Winner))} overrides {HtmlEncode(LoadOrderSetup.Display(g.Key.Loser))} · {g.Count()} record{(g.Count() == 1 ? "" : "s")}");
            if (fwd > 0) h.Append($" <span class=\"badge fwd\">{fwd} forwardable</span>");
            if (choice > 0) h.Append($" <span class=\"badge choice\">{choice} choice</span>");
            if (minor > 0) h.Append($" <span class=\"badge minor\">{minor} no effect</span>");
            h.Append($"</summary><div><p class=\"muted\">{HtmlEncode(LoadOrderSetup.Display(g.Key.Winner))} loads later and does not have {HtmlEncode(LoadOrderSetup.Display(g.Key.Loser))} as a master, so these changes from {HtmlEncode(LoadOrderSetup.Display(g.Key.Loser))} do not reach the game.</p>");
            h.Append("<div class=\"impact-box\"><strong>Impact</strong><ul>");
            foreach (var line in Impact.ForGroup(g)) h.Append($"<li>{HtmlEncode(line)}</li>");
            h.Append("</ul></div>");
            var recs = Recommend.ForGroup(g, lo, a);
            if (recs.Count > 0)
            {
                h.Append("<div class=\"rec-box\"><strong>Recommendation</strong><ul>");
                foreach (var r in recs.OrderBy(r => r.Priority)) h.Append($"<li><span class=\"badge p{r.Priority}\">{r.Level}</span> {HtmlEncode(r.Text)}{PromptBlock(r)}</li>");
                h.Append("</ul></div>");
            }
            h.Append("<table><thead><tr><th>Field</th><th>Kind</th><th>Lost value</th><th>Winning value</th></tr></thead>");
            // critical entries first when priorities are on (always false with --no-priorities, so 0.9.1's order)
            foreach (var l in g.OrderBy(l => l.Fields.Any(a.CriticalFields.Contains) ? 0 : 1).ThenBy(l => l.RecordType).ThenBy(l => l.EditorID))
            {
                h.Append($"<tbody class=\"rec-block\"><tr class=\"rec\"><td colspan=\"4\">{HtmlEncode(l.RecordType)} {HtmlEncode(l.EditorID ?? "")} <code>{HtmlEncode(l.FormKey.ToString())}</code></td></tr>");
                foreach (var f in l.Fields)
                    h.Append($"<tr><td>{HtmlEncode(f.Field)}</td><td>{KindBadge(Impact.Kind(l, f))}" +
                             $"{(a.CriticalFields.Contains(f) ? " <span class=\"badge p1\">Critical</span>" : "")}</td>" +
                             $"<td class=\"val\">{Value(f.LoserValue)}</td><td class=\"val\">{Value(f.WinnerValue)}</td></tr>" +
                             ImpactRow(Impact.ForField(l, f), l));
                h.Append("</tbody>");
            }
            h.Append("</table></div></details>");
        }
    }

    // What this is / original / what each plugin does / result, as a labeled block under the field row.
    static string ImpactRow(ImpactText t, LostChange l) =>
        "<tr class=\"impact\"><td colspan=\"4\"><dl class=\"story\">" +
        $"<dt>What this is</dt><dd>{HtmlEncode(t.What)}</dd>" +
        $"<dt>Original</dt><dd>{HtmlEncode(t.Original)}</dd>" +
        $"<dt>{HtmlEncode(LoadOrderSetup.Display(l.Loser))}</dt><dd>{HtmlEncode(t.LoserDid)}</dd>" +
        $"<dt>{HtmlEncode(LoadOrderSetup.Display(l.Winner))}</dt><dd>{HtmlEncode(t.WinnerDid)}</dd>" +
        $"<dt>Result in game</dt><dd><strong>{HtmlEncode(t.Result)}</strong></dd>" +
        "</dl></td></tr>";

    // Short values inline; long ones collapsed behind their first line.
    static string Value(string v) => v.Length <= 140
        ? HtmlEncode(v)
        : $"<details><summary>{HtmlEncode(v[..110])}…</summary>{HtmlEncode(v)}</details>";
}
