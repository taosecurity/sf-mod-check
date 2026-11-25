// 0.19.0: the keys `patch` merges a quest's lists by (StarfieldPluginCheck NOTES.md, "0.19.0 (GapFix)", Step 4).
// A stage is known by its Index, an objective by its Index, an alias by its ID (reference, location and collection
// aliases share one ID space). Condition lists are one value and are never merged entry by entry.
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

public static class QuestMerge
{
    /// <summary>The quest list fields merged by key.</summary>
    public static readonly HashSet<string> KeyedFields = new(StringComparer.Ordinal) { "Stages", "Objectives", "Aliases" };

    /// <summary>What a keyed field's entries are known by, for report text.</summary>
    public static string KeyName(string field) => field switch
    {
        "Stages" => "stage Index",
        "Objectives" => "objective Index",
        _ => "alias ID",
    };

    /// <summary>Whether a list type holds conditions (a list that is one value).</summary>
    public static bool IsConditionList(Type t)
    {
        var elem = t.IsGenericType ? t.GetGenericArguments().LastOrDefault() : null;
        return elem is not null && typeof(IConditionGetter).IsAssignableFrom(elem);
    }

    /// <summary>An entry's key ("stage 10", "objective 20", "alias 3"), or null when it has none.</summary>
    public static string? KeyOf(object? x) => x switch
    {
        IQuestStageGetter s => $"stage {s.Index}",
        IQuestObjectiveGetter o => $"objective {o.Index}",
        IQuestReferenceAliasGetter r => $"alias {r.ID}",
        IQuestLocationAliasGetter l => $"alias {l.ID}",
        IQuestCollectionAliasGetter c => c.Collection.Count > 0 && c.Collection[0].ID is { } id ? $"alias {id}" : null,
        _ => null,
    };

    /// <summary>A stage's or objective's Index, for the written order.</summary>
    public static int IndexOf(object? x) => x switch
    {
        IQuestStageGetter s => s.Index,
        IQuestObjectiveGetter o => o.Index,
        _ => 0,
    };

    public static string AliasKind(object? x) => x switch
    {
        IQuestReferenceAliasGetter => "reference",
        IQuestLocationAliasGetter => "location",
        IQuestCollectionAliasGetter => "collection",
        _ => "unknown",
    };

    /// <summary>Each entry's position by key; null when an entry has no key or a key appears twice.</summary>
    public static Dictionary<string, int>? Keys(List<object?> list)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < list.Count; k++)
            if (KeyOf(list[k]) is not { } key || !d.TryAdd(key, k)) return null;
        return d;
    }

    /// <summary>An in-memory copy of a stage or objective (the patch builds a merged one from it).</summary>
    public static object CopyEntry(object x) => x switch
    {
        IQuestStageGetter s => s.DeepCopy(),
        IQuestObjectiveGetter o => o.DeepCopy(),
        _ => throw new InvalidOperationException($"no copy for {x.GetType().Name}"),
    };
}
