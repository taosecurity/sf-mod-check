using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

/// <summary>The links a record's script properties hold inside struct values. Mutagen's EnumerateFormLinks skips them
/// (found by the 0.20.0 real-data run: SFSearch's refs2 listed 11 quests whose only link to the location was a struct
/// member), so `find --refs` adds them. Plain object properties and lists are not repeated here: Mutagen does them.</summary>
static class ScriptStructLinks
{
    /// <summary>Every non-null link held in a struct member of the record's scripts (record and alias scripts). Every
    /// script-holding record type counts: plain scripted records, quests, dialogue lines, scenes, packages, perks and
    /// terminal menus each have their own adapter type, all of them an IAVirtualMachineAdapterGetter.</summary>
    public static IEnumerable<IFormLinkGetter> Of(IMajorRecordGetter rec)
    {
        if (rec is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } vm }) yield break;
        foreach (var s in vm.Scripts) foreach (var l in InScript(s)) yield return l;
        if (vm is IQuestAdapterGetter q)
            foreach (var a in q.Aliases) foreach (var s in a.Scripts) foreach (var l in InScript(s)) yield return l;
    }

    static IEnumerable<IFormLinkGetter> InScript(IScriptEntryGetter s)
    {
        foreach (var p in s.Properties)
        {
            if (p is IScriptStructPropertyGetter sp)
                foreach (var e in sp.Members) foreach (var l in InMembers(e.Properties)) yield return l;
            else if (p is IScriptStructListPropertyGetter sl)
                foreach (var e in sl.Structs) foreach (var l in InMembers(e.Members)) yield return l;
        }
    }

    /// <summary>Everything linked from members of a struct, including members that are structs themselves.</summary>
    static IEnumerable<IFormLinkGetter> InMembers(IEnumerable<IScriptPropertyGetter> members)
    {
        foreach (var m in members)
        {
            switch (m)
            {
                case IScriptObjectPropertyGetter o when !o.Object.IsNull: yield return o.Object; break;
                case IScriptObjectListPropertyGetter ol:
                    foreach (var x in ol.Objects) if (!x.Object.IsNull) yield return x.Object;
                    break;
                case IScriptStructPropertyGetter sp:
                    foreach (var e in sp.Members) foreach (var l in InMembers(e.Properties)) yield return l;
                    break;
                case IScriptStructListPropertyGetter sl:
                    foreach (var e in sl.Structs) foreach (var l in InMembers(e.Members)) yield return l;
                    break;
            }
        }
    }
}
