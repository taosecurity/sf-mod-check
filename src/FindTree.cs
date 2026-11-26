// sf-mod-check find --full (0.20.0): every nested value of a record as a tree of nodes. The text view and the JSON view
// both print from the same nodes, so they can't drift apart. Design: NOTES.md "Step 3 design", the "--full" part.
// What prints: every member except null objects, null links, empty strings, empty lists and empty byte arrays; numbers,
// enums and booleans print even when 0 or false. Mutagen's padding members (Unused, Unused2, a condition's CTDA padding)
// are left out. Links print as "EditorID [FormKey]", conditions as one line each, byte arrays as their length and up to
// 64 bytes in hex. Caps (safety only): 12 levels deep, 20,000 nodes per record (JSON), 2,000 text lines per record.
using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SfModCheck.Conflict;

namespace SfModCheck;

/// <summary>One node of a --full tree: a member or a list entry ("[i]"). A leaf has Text and no Children; a list has
/// Text "(N entries)" and its entries as Children; an object has no Text and its members as Children.</summary>
internal sealed class TreeNode
{
    public required string Name;
    public string? Text;
    public List<TreeNode>? Children;
    public bool IsList;
}

internal static class FindTree
{
    /// <summary>The caps (settable from tests; restore them afterwards).</summary>
    internal static int MaxDepth = 12, MaxLines = 2000, MaxNodes = 20000;
    public const int MaxBytesShown = 64;

    sealed class Budget { public int Left; public int NotShown; }

    /// <summary>The record's tree: its top-level fields (as find's fields block, EditorID and Name in the heading, child
    /// records left out) and everything below them. NotShown counts the nodes past the node cap.</summary>
    public static (List<TreeNode> Nodes, int NotShown) Build(IMajorRecordGetter rec, Func<IFormLinkGetter, string> link)
    {
        var b = new Budget { Left = MaxNodes };
        var nodes = new List<TreeNode>();
        var t = rec.GetType();
        foreach (var p in FieldCompare.Fields(t))
        {
            if (p.Name is "EditorID" or "Name" || DeepCompare.IsPadding(t, p.Name)) continue;
            object? v;
            try { v = FieldCompare.Get(p, rec); }
            catch (Exception e) when (Fatal.Recoverable(e)) { Add(nodes, new TreeNode { Name = p.Name, Text = $"(unreadable: {e.GetType().Name})" }, b); continue; }
            Walk(p.Name, v, link, 0, false, b, nodes);
        }
        return (nodes, b.NotShown);
    }

    /// <summary>Adds a leaf while the budget lasts; past it the leaf is counted as not shown.</summary>
    static int Add(List<TreeNode>? into, TreeNode leaf, Budget b)
    {
        if (into == null) return 1;
        if (b.Left <= 0) { b.NotShown++; return 1; }
        b.Left--;
        into.Add(leaf);
        return 1;
    }

    /// <summary>Walks one value and returns how many nodes it makes (0 when it is left out). into == null counts only
    /// (past the node cap): nothing is kept and links aren't named.</summary>
    static int Walk(string name, object? v, Func<IFormLinkGetter, string> link, int depth, bool entry, Budget b, List<TreeNode>? into)
    {
        string? leaf;
        try { leaf = LeafText(v, into == null ? null : link, out var skip); if (skip) return entry ? Add(into, new TreeNode { Name = name, Text = "(empty)" }, b) : 0; }
        catch (Exception x) when (Fatal.Recoverable(x)) { leaf = $"(unreadable: {x.GetType().Name})"; }
        if (leaf != null) return Add(into, new TreeNode { Name = name, Text = leaf }, b);

        // a list or a Mutagen object: a node with children
        if (depth >= MaxDepth) return Add(into, new TreeNode { Name = name, Text = $"(deeper than {MaxDepth} levels, not shown)" }, b);
        bool counting = into == null || b.Left <= 0;
        TreeNode? node = null;
        if (!counting) { node = new TreeNode { Name = name, Children = new() }; b.Left--; into!.Add(node); }
        var kids = node?.Children;
        int made = 0;
        if (v is IEnumerable e)
        {
            List<object?> items;
            try { items = e.Cast<object?>().ToList(); }
            catch (Exception x) when (Fatal.Recoverable(x))
            {
                if (node != null) { node.Children = null; node.Text = $"(unreadable: {x.GetType().Name})"; }
                return Counted(1);
            }
            if (node != null) { node.IsList = true; node.Text = $"({items.Count} {(items.Count == 1 ? "entry" : "entries")})"; }
            for (int i = 0; i < items.Count; i++) made += Walk($"[{i}]", items[i], link, depth + 1, true, b, kids);
            return Counted(1 + made);
        }
        var t = v!.GetType();
        foreach (var p in FieldCompare.NestedProperties(t))
        {
            if (DeepCompare.IsPadding(t, p.Name)) continue;
            object? pv;
            try { pv = p.GetValue(v); }
            catch (Exception x) when (Fatal.Recoverable(x)) { made += Add(kids, new TreeNode { Name = p.Name, Text = $"(unreadable: {x.GetType().Name})" }, b); continue; }
            made += Walk(p.Name, pv, link, depth + 1, false, b, kids);
        }
        if (made == 0)
        {
            // nothing in it to print: left out like a null object (a list entry says "(empty)")
            if (node != null) { into!.Remove(node); b.Left++; }
            return entry ? Add(into, new TreeNode { Name = name, Text = "(empty)" }, b) : 0;
        }
        return Counted(1 + made);

        int Counted(int n)
        {
            // the point where building stopped: this node and everything below it are not shown
            if (into != null && node == null) b.NotShown += n;
            return n;
        }
    }

    /// <summary>A value's leaf text, or null when it has children (a list or a Mutagen object). skip: an empty value,
    /// left out. link == null (counting only): links aren't named.</summary>
    static string? LeafText(object? v, Func<IFormLinkGetter, string>? link, out bool skip)
    {
        skip = false;
        switch (v)
        {
            case null: skip = true; return null;
            case ITranslatedStringGetter ts:
                if (string.IsNullOrEmpty(ts.String)) { skip = true; return null; }
                return Quote(ts.String);
            case IAssetLinkGetter al:
                if (string.IsNullOrEmpty(al.GivenPath)) { skip = true; return null; }
                return Quote(al.GivenPath);
            case IFormLinkGetter fl:
                if (fl.IsNull) { skip = true; return null; }
                return link?.Invoke(fl) ?? "";
            case string s:
                if (s.Length == 0) { skip = true; return null; }
                return Quote(s);
            case IConditionGetter c: return link == null ? "" : FindCommand.ConditionText(c, link);
            case Guid g:
                // the plain GUID, as before 0.23.0 (the JSON view keeps it); the text view adds the manifest form (Lines)
                return g.ToString();
            case IEnumerable<byte> bytes:
            {
                var arr = bytes.ToArray();
                if (arr.Length == 0) { skip = true; return null; }
                var hex = string.Join(" ", arr.Take(MaxBytesShown).Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
                return $"({arr.Length} bytes) {hex}{(arr.Length > MaxBytesShown ? " ..." : "")}";
            }
            case IEnumerable list:
                if (!list.Cast<object?>().Any()) skip = true;
                return null;
        }
        var t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is decimal) return SafeText.OneLine(Convert.ToString(v, CultureInfo.InvariantCulture));
        if (t.Namespace?.StartsWith("Mutagen") != true) return SafeText.OneLine(v.ToString());
        return null;
    }

    static string Quote(string s)
    {
        var one = SafeText.OneLine(s);
        return "\"" + (one.Length > TriageDetail.MaxValue ? one[..TriageDetail.MaxValue] + "..." : one) + "\"";
    }

    /// <summary>0.23.0, text view only: a sound GUID (a SoundReference's Start or Stop, a WWED's Start or End) also in
    /// the form a Wwise bank manifest (.json) writes it, so it can be looked up there (.NET's own form never matches).
    /// The JSON view keeps the plain GUID, as before (code review 0.23.0 round 2). Quoted strings never parse.</summary>
    static string ManifestForm(TreeNode n) =>
        n.Children == null && Guid.TryParseExact(n.Text, "D", out var g) && g != Guid.Empty
            ? $" (manifest form {AssetFiles.SoundGuidText(AssetFiles.SoundGuid(g))})" : "";

    /// <summary>The text view: two spaces per level from indent, at most MaxLines lines, then one line with how many more.</summary>
    public static List<string> Lines(List<TreeNode> nodes, int notShown, string indent)
    {
        var all = new List<string>();
        void Put(TreeNode n, string ind)
        {
            all.Add(n.Children == null || n.IsList ? $"{ind}{n.Name}: {n.Text}{ManifestForm(n)}" : $"{ind}{n.Name}:");
            if (n.Children != null) foreach (var c in n.Children) Put(c, ind + "  ");
        }
        foreach (var n in nodes) Put(n, indent);
        if (all.Count <= MaxLines && notShown == 0) return all;
        var more = Math.Max(0, all.Count - MaxLines) + notShown;
        var kept = all.Take(MaxLines).ToList();
        kept.Add($"{indent}... and {more} more line{(more == 1 ? "" : "s")} not shown (--json --full has up to {MaxNodes.ToString("N0", CultureInfo.InvariantCulture)} nodes)");
        return kept;
    }

    /// <summary>The JSON view: an object node is an object of its members, a list an array, a leaf its text.</summary>
    public static JsonObject Json(List<TreeNode> nodes)
    {
        var o = new JsonObject();
        foreach (var n in nodes) o[Unique(o, n.Name)] = Node(n);
        return o;
    }

    static JsonNode? Node(TreeNode n)
    {
        if (n.Children == null) return n.Text;
        if (n.IsList) return new JsonArray(n.Children.Select(Node).ToArray());
        return Json(n.Children);
    }

    static string Unique(JsonObject o, string name)
    {
        if (!o.ContainsKey(name)) return name;
        for (int i = 2; ; i++) if (!o.ContainsKey($"{name}#{i}")) return $"{name}#{i}";
    }
}
