// sf-mod-check find --full on a dialogue topic (0.22.0, item A): the topic's lines (its INFO children, which are records
// of their own and so aren't in the tree), each with the plugin that wins it and each response's number, text, emotion
// and face-animation fields. The INFOs are gathered from every copy of the topic in load order (an INFO override always
// sits inside a copy of its topic), so a later plugin's copy of an INFO replaces the earlier one and a mod's new INFO is
// listed after the ones it joins. Text and JSON both print from the same TopicLines. Design: NOTES "## 0.22.0 (SmcLookup)".
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

/// <summary>One response of a line: its number (1 up, the order in the INFO), its text and its emotion and face fields
/// as (Mutagen name, text) pairs, in a fixed order.</summary>
public sealed record TopicResponse(int Number, string Text, List<(string Name, string Value)> Details);

/// <summary>One INFO of the topic as the game uses it: Winner is the winning copy, but when that is a partial form with no
/// lines, Responses and SharedFrom come from the earlier copy named by LinesFrom (and a partial form with no EditorID keeps
/// the earlier one). SharedFrom: the INFO whose lines it uses when it has none of its own (SharedDialog), or null.</summary>
public sealed record TopicLine(FormKey FormKey, string? EditorId, string Winner, List<TopicResponse> Responses, string? SharedFrom, bool Deleted = false)
{
    /// <summary>0.22.0: the winner is a partial form with no responses, so its lines come from this earlier plugin.</summary>
    public string? LinesFrom { get; init; }
}

/// <summary>What --full lists under one topic.</summary>
public sealed class TopicLines
{
    public List<TopicLine> Records = new();
    public int Total => Records.Count;
    /// <summary>Copies of the topic whose INFOs couldn't be read.</summary>
    public int Unreadable;
    /// <summary>The --plugin file, or null in a load order.</summary>
    public string? PluginMode;
}

public static class TopicInfos
{
    /// <summary>The INFOs the text view lists at most (settable from tests; restore it afterwards); JSON lists up to MaxJson.</summary>
    internal static int MaxListed = 200;
    public const int MaxJson = 2000;

    /// <summary>The topic's lines from its copies (in load order; the last copy of each INFO wins).</summary>
    public static TopicLines Collect(IEnumerable<(PluginInfo Plugin, IMajorRecordGetter Rec)> copies, Func<IFormLinkGetter, string> link, string? pluginMode)
    {
        var sw = Stopwatch.StartNew();
        var res = new TopicLines { PluginMode = pluginMode };
        var order = new List<FormKey>();
        // Info: the winning copy (its Deleted flag); Lines: the copy whose responses or SharedDialog are shown; EditorId:
        // the winner's own, or for a partial form with none, the earlier copy's (as find --refs; code review round 5).
        var winners = new Dictionary<FormKey, (string Plugin, IDialogResponsesGetter Info, IDialogResponsesGetter Lines, string? LinesFrom, string? EditorId)>();
        FormKey topicKey = default;
        foreach (var (plugin, rec) in copies)
        {
            if (rec is not IDialogTopicGetter topic) continue;
            topicKey = topic.FormKey;
            try
            {
                foreach (var info in topic.Responses)
                {
                    if (!winners.TryGetValue(info.FormKey, out var earlier)) order.Add(info.FormKey);
                    // A partial form with no responses merges with the copy before it, so the game keeps that copy's
                    // lines, its own or shared ones (as find --refs reads partial forms; code review 0.22.0 rounds 3, 4).
                    else if (FieldCompare.IsPartial(info) && info.Responses.Count == 0 && info.SharedDialog.IsNull
                             && (earlier.Lines.Responses.Count > 0 || !earlier.Lines.SharedDialog.IsNull))
                    {
                        winners[info.FormKey] = (plugin.FileName, info, earlier.Lines, earlier.LinesFrom ?? earlier.Plugin,
                                                 string.IsNullOrEmpty(info.EditorID) ? earlier.EditorId : info.EditorID);
                        continue;
                    }
                    // any other partial form with no EditorID keeps the earlier one too (Provides: empty means "keep")
                    var edid = string.IsNullOrEmpty(info.EditorID) && earlier.Info != null && FieldCompare.IsPartial(info) ? earlier.EditorId : info.EditorID;
                    winners[info.FormKey] = (plugin.FileName, info, info, null, edid);
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                res.Unreadable++;
                Log.Write($"full: {topic.FormKey}: {plugin.FileName}'s lines couldn't be read ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})");
            }
        }
        foreach (var k in order)
        {
            var (plugin, info, linesCopy, from, edid) = winners[k];
            res.Records.Add(Line(info, linesCopy, edid, plugin, link) with { LinesFrom = from });
        }
        Log.Write($"full: {topicKey}: {res.Total} lines (INFOs), {res.Records.Sum(r => r.Responses.Count)} responses, {res.Unreadable} copies unreadable, {sw.Elapsed.TotalSeconds:F1} s");
        return res;
    }

    /// <summary>One line: the FormKey and Deleted flag from the winning copy, the EditorID given (the winner's, or an
    /// earlier copy's under a partial form with none), the responses and SharedDialog from linesCopy (the same copy
    /// unless the winner is a partial form with no lines).</summary>
    static TopicLine Line(IDialogResponsesGetter info, IDialogResponsesGetter linesCopy, string? editorId, string plugin, Func<IFormLinkGetter, string> link)
    {
        var responses = new List<TopicResponse>();
        string? shared = null;
        try
        {
            for (int i = 0; i < linesCopy.Responses.Count; i++) responses.Add(Response(i + 1, linesCopy.Responses[i], link));
            if (linesCopy.Responses.Count == 0 && !linesCopy.SharedDialog.IsNull) shared = link(linesCopy.SharedDialog);
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            responses.Add(new TopicResponse(responses.Count + 1, $"(unreadable: {e.GetType().Name})", new()));
        }
        return new TopicLine(info.FormKey, string.IsNullOrEmpty(editorId) ? null : SafeText.OneLine(editorId), plugin, responses, shared,
                             info.IsDeleted);
    }

    static TopicResponse Response(int number, IDialogResponseGetter r, Func<IFormLinkGetter, string> link)
    {
        var d = new List<(string, string)>();
        if (!r.Emotion.IsNull) d.Add(("Emotion", link(r.Emotion)));
        d.Add(("EmotionOut", Convert.ToString(r.EmotionOut, CultureInfo.InvariantCulture)!));
        if (r.Animation is { } a)
        {
            if (!a.FaceArchetype.IsNull) d.Add(("Animation.FaceArchetype", link(a.FaceArchetype)));
            if (!a.BodyArchetype.IsNull) d.Add(("Animation.BodyArchetype", link(a.BodyArchetype)));
            if (!a.Flavor.IsNull) d.Add(("Animation.Flavor", link(a.Flavor)));
            if (!a.NpcAnimation.IsNull) d.Add(("Animation.NpcAnimation", link(a.NpcAnimation)));
        }
        var text = SafeText.OneLine(r.ResponseText?.String ?? "") ?? "";
        if (text.Length > TriageDetail.MaxValue)
        {
            int cut = char.IsHighSurrogate(text[TriageDetail.MaxValue - 1]) ? TriageDetail.MaxValue - 1 : TriageDetail.MaxValue;
            text = text[..cut] + "...";
        }
        return new TopicResponse(number, text, d);
    }

    /// <summary>The text block: a header, then per INFO one line and one line per response (with its details below).</summary>
    public static List<string> Lines(TopicLines t, int jsonMax = MaxJson)
    {
        var where = t.PluginMode != null ? $", in {SafeText.OneLine(t.PluginMode)} and its masters" : "";
        var lines = new List<string> { $"  lines: {t.Total} INFO{(t.Total == 1 ? "" : "s")} (winning copies{where})" };
        foreach (var l in t.Records.Take(MaxListed))
        {
            lines.Add($"    {l.EditorId ?? "(no EditorID)"} [{l.FormKey}], winner {l.Winner}{(l.Deleted ? " (DELETED by its winner: the game doesn't use this line)" : "")}" +
                      (l.LinesFrom != null ? $" (a partial form; its lines come from {SafeText.OneLine(l.LinesFrom)})" : ""));
            if (l.SharedFrom != null) lines.Add($"      (no responses of its own: it uses the lines of {l.SharedFrom})");
            else if (l.Responses.Count == 0) lines.Add("      (no responses)");
            foreach (var r in l.Responses)
            {
                lines.Add($"      {r.Number}: \"{r.Text}\"");
                if (r.Details.Count > 0) lines.Add($"         {string.Join(", ", r.Details.Select(x => $"{x.Name}: {x.Value}"))}");
            }
        }
        if (t.Total > MaxListed)
        {
            var more = t.Total - MaxListed;
            lines.Add($"    ... and {more} more INFO{(more == 1 ? "" : "s")} not shown (--json --full lists up to {jsonMax.ToString("N0", CultureInfo.InvariantCulture)})");
        }
        if (t.Unreadable > 0) lines.Add($"    note: the lines of {t.Unreadable} cop{(t.Unreadable == 1 ? "y" : "ies")} of the topic couldn't be read (--verbose says why)");
        return lines;
    }

    /// <summary>The JSON view: { total, records: [{ formKey, editorId, winner, responses: [{ number, text, Emotion, ... }],
    /// sharedFrom?, deleted?, linesFrom? }], notShown?, unreadable? }. At most max records.</summary>
    public static JsonObject Json(TopicLines t, int max = MaxJson)
    {
        var recs = new JsonArray();
        foreach (var l in t.Records.Take(max))
        {
            var rs = new JsonArray();
            foreach (var r in l.Responses)
            {
                var o = new JsonObject { ["number"] = r.Number, ["text"] = r.Text };
                foreach (var (n, v) in r.Details) o[n] = v;
                rs.Add(o);
            }
            var rec = new JsonObject { ["formKey"] = l.FormKey.ToString(), ["editorId"] = l.EditorId, ["winner"] = l.Winner, ["responses"] = rs };
            if (l.SharedFrom != null) rec["sharedFrom"] = l.SharedFrom;
            if (l.Deleted) rec["deleted"] = true;
            if (l.LinesFrom != null) rec["linesFrom"] = l.LinesFrom;
            recs.Add(rec);
        }
        var j = new JsonObject { ["total"] = t.Total, ["records"] = recs };
        if (t.Total > max) j["notShown"] = t.Total - max;
        if (t.Unreadable > 0) j["unreadable"] = t.Unreadable;
        return j;
    }
}
