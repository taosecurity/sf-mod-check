// 0.19.0: the keys `patch` merges a quest's lists by (StarfieldPluginCheck NOTES.md, "0.19.0 (GapFix)", Step 4).
// A stage is known by its Index, an objective by its Index, an alias by its ID (reference, location and collection
// aliases share one ID space). Condition lists are one value and are never merged entry by entry.
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

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

    // ---------- the quest's script data (VMAD), 0.19.0 step 5b ----------

    /// <summary>The result of merging two versions of a quest's script data. Whole is the reason the kept side's whole
    /// script data is used instead of a merge (null when merged); Clashes names each entry both sides changed, where
    /// the kept side's value stays.</summary>
    public sealed record VmadResult(QuestAdapter Value, List<string> Clashes, string? Whole);

    static bool Eq(object? a, object? b) => FieldCompare.DeepEq(a, b, 0);

    /// <summary>Merges the script data L changed (against its master's B) into S, the version the patch holds: attached
    /// scripts by name, their properties by name, stage fragments by (stage, index), fragment aliases by alias. keepS:
    /// S wins an entry both changed. Stage fragments merge only when both sides use one fragment script and every
    /// fragment kept is a function in it (functions: that script's function names as the game loads it, or null when it
    /// can't be read); otherwise the kept side's whole script data is used. Null when a list can't be keyed (one key
    /// twice in a version): the caller then uses the whole-value rule. All three are in-memory copies.</summary>
    public static VmadResult? MergeVmad(QuestAdapter s, QuestAdapter? b, QuestAdapter l, bool keepS, string kept, string dropped,
        string quest, string fk, Func<string, ISet<string>?> functions)
    {
        b ??= new QuestAdapter();
        var r = s.DeepCopy();
        var clashes = new List<string>();
        VmadResult WholeOf(string why) => new(keepS ? s : l, new(), why);

        // the header values
        T Three<T>(T sv, T bv, T lv, string what)
        {
            if (Eq(lv, bv)) return sv;
            if (Eq(sv, bv) || Eq(sv, lv)) return lv;
            clashes.Add(what);
            return keepS ? sv : lv;
        }
        r.Version = Three(s.Version, b.Version, l.Version, "the script data version");
        r.ObjectFormat = Three(s.ObjectFormat, b.ObjectFormat, l.ObjectFormat, "the script data object format");
        r.ExtraBindDataVersion = Three(s.ExtraBindDataVersion, b.ExtraBindDataVersion, l.ExtraBindDataVersion, "the script data bind version");

        if (!MergeKeyed(r.Scripts, b.Scripts, l.Scripts, ScriptKey, (sv, bv, lv) => MergeScript(sv, bv, lv, keepS, clashes), "script", keepS, clashes))
            return null;

        // stage fragments: one fragment script per quest
        bool lFrag = !Eq(l.Script, b.Script) || !Eq(l.Fragments, b.Fragments);
        bool sFrag = !Eq(s.Script, b.Script) || !Eq(s.Fragments, b.Fragments);
        if (lFrag && !sFrag)
        {
            r.Script = l.Script.DeepCopy();
            Set(r.Fragments, l.Fragments.Select(f => f.DeepCopy()));
        }
        else if (lFrag && sFrag)
        {
            var sName = FragmentScript(s);
            var lName = FragmentScript(l);
            var (keptName, droppedName) = keepS ? (sName, lName) : (lName, sName);
            if (sName is null || lName is null || !sName.Equals(lName, StringComparison.OrdinalIgnoreCase))
                return WholeOf($"{dropped} and {kept} attach different stage fragment scripts ({droppedName ?? "none"}, {keptName ?? "none"}) to quest {quest}; " +
                               $"a quest has one fragment script, so their fragments can't be merged: {kept}'s whole script data is kept. " +
                               $"Merge the scripts in the Creation Kit, or use --pick {fk}:VirtualMachineAdapter=<plugin>");
            var fns = functions(sName);
            if (fns is null)
                return WholeOf($"{dropped} and {kept} both change the stage fragments of quest {quest}, and its fragment script {sName} could not be read, " +
                               $"so the fragments can't be checked and merged: {kept}'s whole script data is kept (--pick {fk}:VirtualMachineAdapter=<plugin> takes another's)");
            if (s.Script is not null && l.Script is not null)
                r.Script = MergeScript(s.Script.DeepCopy(), b.Script, l.Script, keepS, clashes);
            if (!MergeKeyed(r.Fragments, b.Fragments, l.Fragments, f => $"stage {f.Stage} fragment {f.StageIndex}",
                    (sv, _, lv) => { clashes.Add($"the fragment of stage {sv.Stage} (index {sv.StageIndex})"); return keepS ? sv : lv; }, "fragment", keepS, clashes))
                return null;
            var sorted = r.Fragments.OrderBy(f => f.Stage).ThenBy(f => f.StageIndex).ToList();
            Set(r.Fragments, sorted);
            var missing = r.Fragments.Select(f => f.FragmentName?.Trim() ?? "").Where(n => n.Length > 0 && !fns.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                return WholeOf($"{dropped} and {kept} both change the stage fragments of quest {quest}, and its fragment script {sName} as the game loads it " +
                               $"has no function {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? $" and {missing.Count - 3} more" : "")}, " +
                               $"so the merged fragments would call functions that aren't there: {kept}'s whole script data is kept. " +
                               $"Merge the scripts in the Creation Kit, or use --pick {fk}:VirtualMachineAdapter=<plugin>");
        }

        // fragment aliases: one entry per alias, kept whole
        if (!MergeKeyed(r.Aliases, b.Aliases, l.Aliases, a => a.Property is { } p ? $"fragment alias {p.Alias} of {p.Object.FormKey}" : null,
                (sv, _, lv) => { clashes.Add($"the scripts of {AliasName(sv)}"); return keepS ? sv : lv; }, "fragment alias", keepS, clashes))
            return null;
        return new(r, clashes, null);
    }

    static void Set<T>(IList<T> list, IEnumerable<T> items)
    {
        var x = items.ToList();
        list.Clear();
        foreach (var i in x) list.Add(i);
    }

    static string AliasName(QuestFragmentAlias a) => a.Property is { } p ? $"alias {p.Alias}" : "an alias";

    static string ScriptKey(ScriptEntry e) => (e.Name ?? "").Trim().ToLowerInvariant();

    /// <summary>The quest's fragment script name, or null when it has none or its fragments name another one.</summary>
    static string? FragmentScript(IQuestAdapterGetter a)
    {
        var name = a.Script?.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return a.Fragments.Count == 0 ? "" : null;
        foreach (var f in a.Fragments)
            if (!string.IsNullOrWhiteSpace(f.ScriptName) && !f.ScriptName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return null;
        return name;
    }

    /// <summary>One attached script both sides changed: its flags by the field rule, its properties by name.</summary>
    static ScriptEntry MergeScript(ScriptEntry s, IScriptEntryGetter? b, IScriptEntryGetter l, bool keepS, List<string> clashes)
    {
        var bc = b?.DeepCopy() ?? new ScriptEntry { Name = l.Name };
        var lc = l.DeepCopy();
        var r = s.DeepCopy();
        if (!Eq(lc.Flags, bc.Flags))
        {
            if (Eq(s.Flags, bc.Flags) || !keepS) r.Flags = lc.Flags;
            if (!Eq(s.Flags, bc.Flags) && !Eq(s.Flags, lc.Flags)) clashes.Add($"script {s.Name}'s flags");
        }
        var name = s.Name;
        if (!MergeKeyed(r.Properties, bc.Properties, lc.Properties, p => (p.Name ?? "").Trim().ToLowerInvariant(),
                (sv, _, lv) => { clashes.Add($"script {name} property {sv.Name}"); return keepS ? sv : lv; }, $"script {name} property", keepS, clashes))
            // a script holding one property twice: the kept side's whole script
            return keepS ? r : lc;
        return r;
    }

    /// <summary>Merges list l (changed against b) into result (a copy of the kept list) by key: an entry only l
    /// changed or added goes in, one only l removed goes out, one both changed goes through both. False when a key is
    /// missing or twice in one version (result is then unchanged).</summary>
    static bool MergeKeyed<T>(IList<T> result, IReadOnlyList<T> b, IReadOnlyList<T> l, Func<T, string?> key, Func<T, T?, T, T> both,
        string what, bool keepS, List<string> clashes) where T : class
    {
        Dictionary<string, int>? Index(IReadOnlyList<T> list)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < list.Count; k++)
                if (key(list[k]) is not { } x || !d.TryAdd(x, k)) return null;
            return d;
        }
        var rk = Index(result.ToList());
        var bk = Index(b);
        var lk = Index(l);
        if (rk is null || bk is null || lk is null) return false;
        var work = result.ToList();
        int Find(string k) => work.FindIndex(x => key(x) == k);
        foreach (var L in l)
        {
            var k = key(L)!;
            var B = bk.TryGetValue(k, out var bi) ? b[bi] : null;
            if (B is not null && Eq(L, B)) continue;
            var at = Find(k);
            if (at < 0)
            {
                // L changed an entry the kept list removed: the kept side decides
                if (B is not null) { clashes.Add($"{what} {k} (one removes it, the other changes it)"); if (keepS) continue; }
                work.Add(L);
                continue;
            }
            var S = work[at];
            if (Eq(S, L)) continue;
            if (B is not null && Eq(S, B)) { work[at] = L; continue; }
            work[at] = both(S, B, L);
        }
        foreach (var (k, bi) in bk)
        {
            if (lk.ContainsKey(k)) continue;
            var at = Find(k);
            if (at < 0) continue;
            if (Eq(work[at], b[bi])) work.RemoveAt(at);
            else clashes.Add($"{what} {k} (one removes it, the other changes it)");
        }
        result.Clear();
        foreach (var x in work) result.Add(x);
        return true;
    }
}
