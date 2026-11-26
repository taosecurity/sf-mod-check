// The quest-stage check (0.19.0, GapFix G08): part of `check`, two WARNING rules, never an ERROR.
//   quest-stage-unset    a stage of a quest the plugin adds that nothing in the plugin sets: no start stage
//                        (RunOnStart), no dialogue response, scene or scene phase "set parent quest stage" value, and no
//                        SetStage or SetStageNoWait call in any script the plugin attaches
//   quest-stage-missing  a script call that sets a stage (a constant, or an int property the record sets) on a quest
//                        that has no such stage
// Scripts are read with the Pex reader: loose files first, then the plugin's own archives, then its masters' archives
// (the game's scripts are in "Starfield - Misc.ba2"). A call's target quest is resolved when it is the script's own
// quest (self, in a script on the quest), GetOwningQuest() (followed through assign and cast), or an object property
// the record's script data fills in. Quiet rules, from the vanilla survey (StarfieldPluginCheck NOTES, "0.19.0"):
// a quest with a call whose stage number can't be told gets one NOTE instead of warnings; a stage whose number a call
// with an unknown target uses counts as set; when any attached script can't be found or read, the unset rule turns into
// one NOTE for the plugin. A script property's default value counts as set but never raises quest-stage-missing (the
// defaults are often placeholders such as 0 or -1). Official plugins are skipped.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

public static class QuestStages
{
    public const string Unset = "quest-stage-unset", Missing = "quest-stage-missing";

    /// <summary>Finds and reads compiled scripts: loose files (the asset index's folders), then the archives given, in
    /// order. Each script is read once.</summary>
    public sealed class ScriptSource(AssetIndex index, IReadOnlyList<string> archives)
    {
        readonly Dictionary<string, (PexFile? Pex, string? Problem)> cache = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, IArchiveFile>?> opened = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The script, or null with the reason: "not found" or why it couldn't be read.</summary>
        public (PexFile? Pex, string? Problem) Get(string script)
        {
            if (cache.TryGetValue(script, out var c)) return c;
            var rel = Assets.PexPath(script);
            (PexFile?, string?) r;
            try
            {
                if (index.LoosePath(rel) is { } loose) r = (Pex.ReadFile(loose), null);
                else if (!index.Has(rel)) r = (null, "not found");
                else if (FromArchives(rel) is { } got) r = (Pex.Read(got.Bytes, rel), null);
                else r = (null, "not found in the archives read");
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                r = (null, SafeText.OneLine(e.Message));
            }
            Log.Write($"quest stages: script {script}: {(r.Item1 != null ? "read" : r.Item2)}");
            return cache[script] = r;
        }

        (byte[] Bytes, string Archive)? FromArchives(string rel)
        {
            foreach (var a in archives)
            {
                if (!opened.TryGetValue(a, out var files))
                {
                    files = null;
                    try
                    {
                        if (Ba2Names.Type(a) == "GNRL")
                        {
                            files = new Dictionary<string, IArchiveFile>(StringComparer.Ordinal);
                            foreach (var f in Archive.CreateReader(GameRelease.Starfield, a).Files)
                            {
                                var n = Ba2Names.Normalize(f.Path);
                                if (n.EndsWith(".pex", StringComparison.Ordinal)) files.TryAdd(n, f);
                            }
                        }
                    }
                    catch (Exception e) when (Fatal.Recoverable(e))
                    {
                        Log.Write($"quest stages: archive {Path.GetFileName(a)} couldn't be read ({e.Message})");
                    }
                    opened[a] = files;
                }
                if (files != null && files.TryGetValue(rel, out var file)) return (file.GetBytes(), a);
            }
            return null;
        }
    }

    /// <summary>The archives to look in for scripts: the plugin's own, then its masters' (last-loaded first), then the
    /// Data folder's "Starfield - Misc.ba2" if no master brought it. Within one plugin, "- Main" and "- Misc" archives
    /// come first, so the mesh and texture archives are opened only when a script isn't in those.</summary>
    public static List<string> ArchivesFor(AssetIndex index, string self, IEnumerable<string> masters, string dataDir)
    {
        static int Rank(string p)
        {
            var n = Path.GetFileNameWithoutExtension(p);
            return n.EndsWith(" - Main", StringComparison.OrdinalIgnoreCase) || n.EndsWith(" - Misc", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
        var list = new List<string>();
        foreach (var p in masters.Reverse().Prepend(self))
            list.AddRange(index.OwnArchives(p).OrderBy(Rank));
        var misc = Path.Combine(dataDir, "Starfield - Misc.ba2");
        if (File.Exists(misc) && !list.Any(a => Path.GetFileName(a).Equals("Starfield - Misc.ba2", StringComparison.OrdinalIgnoreCase))) list.Add(misc);
        return list;
    }

    // What a variable in a function holds, as far as one pass over the code can tell.
    enum OriginKind { Unknown, Self, Owning, Prop, Const, Param }
    readonly record struct Origin(OriginKind Kind, string Name = "", int Value = 0);

    /// <summary>One script attached to a record: its name, the record's property values (none for fragment script
    /// names given without an entry), the quest it belongs to, and whether "self" is that quest.</summary>
    sealed record Attached(string Script, IReadOnlyList<IScriptPropertyGetter> Properties, FormKey? Owner, bool SelfIsQuest, string Where,
        IReadOnlySet<string>? LiveFragments = null);

    /// <summary>What sets each quest stage in one plugin (QuestStages.Setters): the stages set per quest, with how
    /// (How), the quests with a SetStage call whose number is worked out at run time (Dynamic), the stage numbers a call
    /// with an unknown target quest uses, the scripts that couldn't be found or read, and the firm script calls.</summary>
    internal sealed class State
    {
        public const string StartFlag = "the start stage flag";
        public readonly Dictionary<FormKey, HashSet<int>> Set = new();
        public readonly Dictionary<(FormKey Quest, int Stage), List<string>> How = new();
        public readonly Dictionary<FormKey, int> Dynamic = new();
        public readonly HashSet<int> UnknownTargetStages = new();
        public readonly SortedDictionary<string, string> Unreadable = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Script, FormKey Quest, int Stage)> Calls = new();
        public int CallCount, AttachedCount;

        public void Mark(FormKey q, int stage, string how)
        {
            if (!Set.TryGetValue(q, out var s)) Set[q] = s = new();
            if (!How.TryGetValue((q, stage), out var h)) How[(q, stage)] = h = new();
            if (!h.Contains(how)) h.Add(how);
            if (s.Add(stage) && Log.On) Log.Write($"quest stages: {q} stage {stage} set by {how}");
        }

        /// <summary>The stage counts as set: something sets it, or a call whose quest can't be told uses its number.</summary>
        public bool IsSet(FormKey q, int stage) => Set.TryGetValue(q, out var s) && s.Contains(stage) || UnknownTargetStages.Contains(stage);
    }

    /// <summary>Runs the check on one plugin. questOf: a quest by FormKey as the game builds it (the plugin's own
    /// version, else the masters'), or null. scripts: finds a script by name.</summary>
    public static void Check(IStarfieldModGetter mod, Func<FormKey, IQuestGetter?> questOf, Func<string, (PexFile? Pex, string? Problem)> scripts,
        Func<IMajorRecordGetter, string> name, Action<string, string, string> add)
    {
        var self = mod.ModKey;
        if (Checker.IsOfficial(self.FileName.String)) { Log.Write("quest stages: skipped (an official plugin)"); return; }
        var newQuests = mod.Quests.Where(q => q.FormKey.ModKey == self && q.Stages.Count > 0).ToList();
        var st = Setters(mod, scripts, name);
        Log.Write($"quest stages: {newQuests.Count} new quest(s) with stages, {st.AttachedCount} attached script(s), {st.CallCount} stage-setting call(s), " +
                  $"{st.Unreadable.Count} script(s) not found or unreadable");
        Report(st, newQuests, questOf, name, add);
    }

    /// <summary>What sets each stage in one plugin (0.24.0: shared by `check` and `scenes`): the start stage flag,
    /// dialogue responses', scenes' and scene phases' "set parent quest stage" values, and SetStage or SetStageNoWait
    /// calls in the scripts the plugin attaches. scripts: finds a script by name.</summary>
    internal static State Setters(IStarfieldModGetter mod, Func<string, (PexFile? Pex, string? Problem)> scripts, Func<IMajorRecordGetter, string> name)
    {
        var st = new State();
        var attached = new List<Attached>();
        var topicQuest = new Dictionary<FormKey, FormKey>();
        var all = mod.EnumerateMajorRecords().ToList();
        foreach (var r in all)
            if (r is IDialogTopicGetter t && !t.Quest.IsNull)
                foreach (var resp in t.Responses) topicQuest.TryAdd(resp.FormKey, t.Quest.FormKey);

        foreach (var r in all)
        {
            switch (r)
            {
                case IQuestGetter q:
                    foreach (var s in q.Stages)
                        if (s.Flags.HasFlag(QuestStage.Flag.RunOnStart)) st.Mark(q.FormKey, s.Index, State.StartFlag);
                    if (q.VirtualMachineAdapter is { } qa)
                    {
                        foreach (var e in qa.Scripts) Attach(e, q.FormKey, true, name(q));
                        if (qa.Script is { } frag && !string.IsNullOrWhiteSpace(frag.Name))
                        {
                            // only the fragments of stages the quest has run: a deleted stage's fragment can stay in the
                            // compiled script, and its calls never run
                            var stagesHad = q.Stages.Select(s => (int)s.Index).ToHashSet();
                            var live = qa.Fragments.Where(f => stagesHad.Contains(f.Stage) && !string.IsNullOrWhiteSpace(f.FragmentName))
                                .Select(f => f.FragmentName.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            attached.Add(new(frag.Name.Trim(), frag.Properties, q.FormKey, true, name(q) + " (stage fragments)", live));
                        }
                        foreach (var al in qa.Aliases) foreach (var e in al.Scripts) Attach(e, q.FormKey, false, name(q) + " (alias)");
                    }
                    break;
                case IDialogResponsesGetter info:
                {
                    FormKey? owner = topicQuest.TryGetValue(info.FormKey, out var tq) ? tq : null;
                    if (owner is { } oq && info.SetParentQuestStage is { } sp)
                    {
                        if (sp.OnBegin >= 0) st.Mark(oq, sp.OnBegin, $"{name(info)} (on begin)");
                        if (sp.OnEnd >= 0) st.Mark(oq, sp.OnEnd, $"{name(info)} (on end)");
                    }
                    AttachAll(r, owner);
                    break;
                }
                case ISceneGetter sc:
                {
                    FormKey? owner = sc.Quest.IsNull ? null : sc.Quest.FormKey;
                    if (owner is { } oq)
                    {
                        if (sc.SetParentQuestStage is { } sp)
                        {
                            if (sp.OnBegin >= 0) st.Mark(oq, sp.OnBegin, $"{name(sc)} (on begin)");
                            if (sp.OnEnd >= 0) st.Mark(oq, sp.OnEnd, $"{name(sc)} (on end)");
                        }
                        foreach (var ph in sc.Phases)
                            if (ph.PhaseSetParentQuestStage is { } pp)
                            {
                                if (pp.OnBegin >= 0) st.Mark(oq, pp.OnBegin, $"{name(sc)} phase {ph.Name}");
                                if (pp.OnEnd >= 0) st.Mark(oq, pp.OnEnd, $"{name(sc)} phase {ph.Name}");
                            }
                    }
                    AttachAll(r, owner);
                    break;
                }
                case IPackageGetter p:
                    AttachAll(r, p.OwnerQuest.IsNull ? null : p.OwnerQuest.FormKey);
                    break;
                default:
                    AttachAll(r, null);
                    break;
            }
        }

        void Attach(IScriptEntryGetter e, FormKey? owner, bool selfIsQuest, string where)
        {
            if (!string.IsNullOrWhiteSpace(e.Name)) attached.Add(new(e.Name.Trim(), e.Properties, owner, selfIsQuest, where));
        }
        void AttachAll(IMajorRecordGetter r, FormKey? owner)
        {
            if (r is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } vm }) return;
            var entries = new List<IScriptEntryGetter>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Walk(vm, entries, names, 0);
            foreach (var e in entries) Attach(e, owner, false, name(r));
            foreach (var n in names.Where(n => !entries.Any(e => string.Equals(e.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase))))
                attached.Add(new(n, Array.Empty<IScriptPropertyGetter>(), owner, false, name(r)));
        }

        foreach (var a in attached) Scan(a, scripts, st);
        st.AttachedCount = attached.Count;
        return st;
    }

    static void Report(State st, List<IQuestGetter> newQuests, Func<FormKey, IQuestGetter?> questOf, Func<IMajorRecordGetter, string> name, Action<string, string, string> add)
    {
        // quest-stage-unset
        var unsetQuests = new List<(IQuestGetter Quest, List<int> Stages)>();
        foreach (var q in newQuests)
        {
            var have = q.Stages.Select(s => (int)s.Index).Distinct().Order().ToList();
            var set = st.Set.GetValueOrDefault(q.FormKey) ?? new HashSet<int>();
            var unset = new List<int>();
            foreach (var s in have)
            {
                if (set.Contains(s)) continue;
                if (st.UnknownTargetStages.Contains(s)) { Log.Write($"quest stages: {name(q)} stage {s}: a call whose quest can't be told uses this number, so it counts as set"); continue; }
                unset.Add(s);
            }
            Log.Write($"quest stages: {name(q)}: {have.Count} stage(s), {have.Count - unset.Count} set, unset: {(unset.Count == 0 ? "none" : string.Join(", ", unset))}");
            if (unset.Count > 0) unsetQuests.Add((q, unset));
        }
        if (unsetQuests.Count > 0 && st.Unreadable.Count > 0)
        {
            var (first, why) = st.Unreadable.First();
            add("NOTE", "quest-stage-unset", $"{st.Unreadable.Count} attached script(s) couldn't be found or read (e.g. {SafeText.OneLine(first)}: {SafeText.OneLine(why)}), so stages that nothing sets " +
                               $"weren't checked in {unsetQuests.Count} quest(s)");
        }
        else
            foreach (var (q, unset) in unsetQuests)
            {
                if (st.Dynamic.TryGetValue(q.FormKey, out var n))
                    add("NOTE", "quest-stage-unset", $"{name(q)}: {n} SetStage call(s) take the stage number from a value worked out while the game runs, so its " +
                                       $"{unset.Count} stage(s) not otherwise set ({Stages(unset)}) weren't checked");
                else
                    add("WARNING", "quest-stage-unset", $"{name(q)}: nothing in the plugin sets stage {Stages(unset)} ({unset.Count} of {q.Stages.Select(s => s.Index).Distinct().Count()}): no start stage, " +
                                          "dialogue, scene or script SetStage call, so what those stages do never happens");
            }

        // quest-stage-missing
        foreach (var (script, fk, stage) in st.Calls.Distinct())
        {
            var q = questOf(fk);
            if (q == null) { Log.Write($"quest stages: {script} sets stage {stage} on {fk}, which isn't in the plugin or its masters, so it wasn't checked"); continue; }
            if (q.Stages.Any(s => s.Index == stage)) continue;
            add("WARNING", "quest-stage-missing", $"script {SafeText.OneLine(script)} sets stage {stage} on {name(q)}, which has no stage {stage}, so the call does nothing");
        }
    }

    static bool IsBaseType(PexFile p) => p.Objects.SelectMany(o => o.Functions()).Any(f => f.IsNative);

    static string Stages(List<int> s) => s.Count <= 12 ? string.Join(", ", s) : string.Join(", ", s.Take(12)) + $" and {s.Count - 12} more";

    static void Scan(Attached a, Func<string, (PexFile? Pex, string? Problem)> scripts, State st)
    {
        var (pex, problem) = scripts(a.Script);
        if (pex == null) { st.Unreadable.TryAdd(a.Script, problem ?? "not found"); return; }
        // the script and the scripts it extends, as far as they can be read. The chain stops at a script with native
        // functions: that is one of the game's base types (Quest, ReferenceAlias, Form ...), whose own code calls
        // SetStage on whatever quest runs it and would read as calls of this one (step 2 smoke run: a false "sets
        // stage 0"). A parent that can't be read stops it too.
        var chain = new List<PexFile> { pex };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Script };
        for (var cur = pex; chain.Count < 8;)
        {
            var parent = cur.Objects.FirstOrDefault()?.Parent ?? "";
            if (parent == "" || !seen.Add(parent)) break;
            var (pp, _) = scripts(parent);
            if (pp == null || IsBaseType(pp)) break;
            chain.Add(pp);
            cur = pp;
        }
        if (chain.Count == 1 && IsBaseType(pex)) return;   // a base type attached directly has no stage logic of the plugin's
        var objProps = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        var intProps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in a.Properties)
        {
            if (p is IScriptObjectPropertyGetter op && !op.Object.IsNull) objProps[p.Name] = op.Object.FormKey;
            if (p is IScriptIntPropertyGetter ip) intProps[p.Name] = ip.Data;
        }
        var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in chain.SelectMany(c => c.Objects))
            foreach (var v in o.Variables)
                if (v.Value.Kind == PexValueKind.Int) defaults.TryAdd(v.Name, v.Value.Int);
        var functions = chain.SelectMany(c => c.Objects).SelectMany(o => o.Functions()).ToList();
        if (a.LiveFragments is { } live)
        {
            var dead = functions.Where(f => f.Name.StartsWith("Fragment_", StringComparison.OrdinalIgnoreCase) && !live.Contains(f.Name)).ToList();
            if (dead.Count > 0)
            {
                Log.Write($"quest stages: {a.Script} ({a.Where}): {dead.Count} fragment(s) no stage of the quest runs, skipped");
                functions = functions.Except(dead).ToList();
            }
        }

        foreach (var f in functions)
        {
            var origin = new Dictionary<string, Origin>(StringComparer.OrdinalIgnoreCase);
            Origin Of(PexValue v)
            {
                if (v.Kind == PexValueKind.Int) return new(OriginKind.Const, Value: v.Int);
                if (!v.IsIdent) return default;
                if (string.Equals(v.Text, "self", StringComparison.OrdinalIgnoreCase)) return new(OriginKind.Self);
                if (origin.TryGetValue(v.Text, out var o)) return o;
                if (v.Text.StartsWith("::", StringComparison.Ordinal) && v.Text.EndsWith("_var", StringComparison.OrdinalIgnoreCase)) return new(OriginKind.Prop, v.Text[2..^4]);
                var k = f.Params.FindIndex(p => p.Name.Equals(v.Text, StringComparison.OrdinalIgnoreCase));
                return k >= 0 ? new(OriginKind.Param, v.Text, k) : default;
            }
            foreach (var i in f.Code)
            {
                switch (i.Name)
                {
                    case "assign" or "cast":
                        origin[i.Args[0].Text] = Of(i.Args[1]);
                        break;
                    case "propget":
                        origin[i.Args[2].Text] = Of(i.Args[1]).Kind == OriginKind.Self ? new(OriginKind.Prop, i.Args[0].Text) : default;
                        break;
                    case "callmethod":
                    {
                        var fn = i.Args[0].Text;
                        // only the script's own GetOwningQuest is its owner quest; one called on a property or another
                        // value (an alias of some other quest) has a quest that can't be told
                        if (fn.Equals("GetOwningQuest", StringComparison.OrdinalIgnoreCase))
                        {
                            origin[i.Args[2].Text] = Of(i.Args[1]).Kind == OriginKind.Self ? new(OriginKind.Owning) : default;
                            break;
                        }
                        if (i.Args[2].IsIdent) origin[i.Args[2].Text] = default;
                        if (!fn.Equals("SetStage", StringComparison.OrdinalIgnoreCase) && !fn.Equals("SetStageNoWait", StringComparison.OrdinalIgnoreCase)) break;
                        if (i.Extra.Count == 0) break;
                        st.CallCount++;
                        var target = Of(i.Args[1]) switch
                        {
                            { Kind: OriginKind.Self } when a.SelfIsQuest => a.Owner,
                            { Kind: OriginKind.Owning } => a.Owner,
                            { Kind: OriginKind.Prop } p when objProps.TryGetValue(p.Name, out var fk) => fk,
                            _ => (FormKey?)null,
                        };
                        // the stage numbers this call can set, and whether each may raise quest-stage-missing
                        var numbers = new List<(int Stage, bool Firm)>();
                        var arg = Of(i.Extra[0]);
                        bool known = true;
                        switch (arg.Kind)
                        {
                            case OriginKind.Const: numbers.Add((arg.Value, true)); break;
                            case OriginKind.Prop when intProps.TryGetValue(arg.Name, out var iv): numbers.Add((iv, true)); break;
                            case OriginKind.Prop when defaults.TryGetValue("::" + arg.Name + "_var", out var dv) || defaults.TryGetValue(arg.Name, out dv): numbers.Add((dv, false)); break;
                            case OriginKind.Param:
                            {
                                // a helper function: the constants every call to it in this script passes (calls on
                                // self only; a same-named function of another object is another function)
                                var passed = functions.SelectMany(g => g.Code)
                                    .Where(c => c.Name == "callmethod" && c.Args[0].Text.Equals(f.Name, StringComparison.OrdinalIgnoreCase)
                                        && c.Args[1].IsIdent && string.Equals(c.Args[1].Text, "self", StringComparison.OrdinalIgnoreCase) && c.Extra.Count > arg.Value)
                                    .Select(c => c.Extra[arg.Value]).ToList();
                                if (passed.Count > 0 && passed.All(c => c.Kind == PexValueKind.Int)) numbers.AddRange(passed.Select(c => (c.Int, true)));
                                else known = false;
                                break;
                            }
                            default: known = false; break;
                        }
                        if (target is not { } t)
                        {
                            foreach (var (n, _) in numbers) st.UnknownTargetStages.Add(n);
                            Log.Write($"quest stages: {a.Script} ({a.Where}): a {fn} call's quest can't be told{(numbers.Count > 0 ? $" (stage {string.Join(", ", numbers.Select(x => x.Stage))})" : "")}");
                            break;
                        }
                        if (!known)
                        {
                            st.Dynamic[t] = st.Dynamic.GetValueOrDefault(t) + 1;
                            Log.Write($"quest stages: {a.Script} ({a.Where}): a {fn} call on {t} takes its stage number from a value worked out while the game runs");
                            break;
                        }
                        foreach (var (n, firm) in numbers)
                        {
                            // a negative number is a "none" placeholder (stages are 0 to 65535); the game's own
                            // generic scripts pass -1 from a property this way (step 3 vanilla run)
                            if (n < 0) { Log.Write($"quest stages: {a.Script} ({a.Where}): a {fn} call on {t} with stage {n} (a placeholder), ignored"); continue; }
                            st.Mark(t, n, $"script {a.Script} ({a.Where})");
                            if (firm) st.Calls.Add((a.Script, t, n));
                        }
                        break;
                    }
                    case "callstatic":
                        // (object, function, result): the result is unknown
                        if (i.Args.Count > 2 && i.Args[2].IsIdent) origin.Remove(i.Args[2].Text);
                        break;
                    case "callparent":
                        // (function, result)
                        if (i.Args.Count > 1 && i.Args[1].IsIdent) origin.Remove(i.Args[1].Text);
                        break;
                    default:
                        // any other instruction that writes its first argument leaves it unknown
                        if (i.Args.Count > 0 && i.Args[0].IsIdent && i.Name is not ("jmp" or "jmpt" or "jmpf" or "return" or "propset" or "array_setelement" or "struct_set"))
                            origin.Remove(i.Args[0].Text);
                        break;
                }
            }
        }
    }

    // Script entries anywhere in a record's script data, and fragment script names given as FileName or ScriptName
    // (as Assets.Scripts finds them).
    static void Walk(object? o, List<IScriptEntryGetter> entries, HashSet<string> names, int depth)
    {
        if (o is null || depth > 6) return;
        if (o is IScriptEntryGetter se) { if (!entries.Contains(se)) entries.Add(se); return; }
        if (o is string || o.GetType().IsPrimitive || o is IMajorRecordGetter && depth > 0 || o is IFormLinkGetter) return;
        if (o is System.Collections.IEnumerable list)
        {
            foreach (var x in list) Walk(x, entries, names, depth + 1);
            return;
        }
        var t = o.GetType();
        if (t.Namespace?.StartsWith("Mutagen.Bethesda.Starfield", StringComparison.Ordinal) != true) return;
        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.Name is "Registration" or "StaticRegistration") continue;
            object? v;
            try { v = p.GetValue(o); }
            catch (Exception e) when (Fatal.Recoverable(e)) { continue; }
            if (v is string s) { if ((p.Name == "FileName" || p.Name == "ScriptName") && !string.IsNullOrWhiteSpace(s)) names.Add(s.Trim()); }
            else if (v is not null && !v.GetType().IsPrimitive && !v.GetType().IsEnum) Walk(v, entries, names, depth + 1);
        }
    }
}
