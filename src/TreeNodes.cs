// 0.10.0 item 1 (2026-11-20): Story Manager and Planet Content Manager nodes, named as such in `check` (an override of
// an official node is a WARNING) and in `conflict`'s impact text. Both trees are shared by every mod that touches them:
// the Story Manager decides which quests and events start, the Planet Content Manager which content (points of
// interest, encounters) a planet gets. Two mods that edit the same official node conflict, and only the last one loaded
// keeps its change, so the conflict-free way is to add your own node to the tree.
namespace SfModCheck;

public static class TreeNodes
{
    public sealed record Kind(string Sig, string MutagenType, string Tree, string Node);

    public static readonly IReadOnlyList<Kind> All =
    [
        new("SMBN", "StoryManagerBranchNode", "Story Manager", "branch node"),
        new("SMQN", "StoryManagerQuestNode", "Story Manager", "quest node"),
        new("SMEN", "StoryManagerEventNode", "Story Manager", "event node"),
        new("PCMT", "PlanetContentManagerTree", "Planet Content Manager", "tree"),
        new("PCBN", "PlanetContentManagerBranchNode", "Planet Content Manager", "branch node"),
        new("PCCN", "PlanetContentManagerContentNode", "Planet Content Manager", "content node"),
    ];

    static readonly Dictionary<string, Kind> BySig = All.ToDictionary(k => k.Sig, StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Kind> ByType = All.ToDictionary(k => k.MutagenType);

    public static Kind? FromSig(string? sig) => sig != null && BySig.TryGetValue(sig, out var k) ? k : null;

    /// <summary>By Mutagen's record class name, as `conflict` keys its records ("StoryManagerQuestNode").</summary>
    public static Kind? FromType(string? type) => type != null && ByType.TryGetValue(type, out var k) ? k : null;

    public static string Purpose(Kind k) => k.Tree == "Story Manager"
        ? "the Story Manager decides which quests and events start, and when"
        : "the Planet Content Manager decides which content (points of interest, encounters) each planet gets";

    /// <summary>"a Story Manager quest node (the Story Manager decides ...)"</summary>
    public static string Describe(Kind k) => $"a {k.Tree} {k.Node} ({Purpose(k)})";

    public const string Alternative =
        "Every mod that edits the same official node conflicts with the others, and only the last one loaded keeps its change. " +
        "The conflict-free way is to add your own node to the tree rather than edit an official one.";

    /// <summary>The `check` WARNING for an override of an official node.</summary>
    public static string CheckText(string recordName, Kind k, string definedIn) =>
        $"{recordName} overrides an official {k.Tree} {k.Node} from {definedIn} ({Purpose(k)}). {Alternative}";
}
