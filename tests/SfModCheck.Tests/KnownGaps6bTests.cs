// 0.10.0 item 5b (README "Known gaps" until 0.9.1): conditions inside package target data, and alias numbers in the
// conditions of quests, dialogue responses and packages.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class KnownGaps6bTests
{
    static PackageDataTarget TargetWith(params Condition[] conds)
    {
        var t = new PackageDataTarget(new PackageTargetSelf());
        t.Conditions.AddRange(conds);
        return t;
    }

    [Fact]
    public void The_walker_finds_conditions_in_package_target_data()
    {
        var mod = new StarfieldMod(ModKey.FromFileName("SfmWalk.esm"), StarfieldRelease.Starfield);
        var pk = new Package(mod) { EditorID = "SFM_Package" };
        pk.Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 0 }, ComparisonValue = 1 });
        pk.Data[0] = TargetWith(new ConditionFloat { Data = new GetDeadConditionData(), ComparisonValue = 1 },
            new ConditionFloat { Data = new GetInFactionConditionData(), ComparisonValue = 1 });
        pk.Data[1] = new PackageDataBool { Data = true };
        var seen = new List<string>();
        ConditionWalker.ForEach(pk, c => seen.Add(c.Data.Function.ToString()));
        Assert.Equal(new[] { "GetIsAliasRef", "GetDead", "GetInFaction" }, seen);
    }

    static Package AddPackage(StarfieldMod m, params Condition[] targetConds)
    {
        var pk = new Package(m) { EditorID = "SFM_Package", OwnerQuest = m.Quests.First().ToNullableLink<IQuestGetter>() };
        pk.Data[0] = TargetWith(targetConds);
        m.Packages.Add(pk);
        return pk;
    }

    [Fact]
    public void A_package_target_condition_with_an_unknown_function_is_found()
    {
        using var w = new World();
        var p = w.Build("pkgfn", m => AddPackage(m, new ConditionFloat { Data = new UnknownConditionData { Function = (Condition.Function)9999 }, ComparisonValue = 1 }));
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "conditions", "SFM_Package");
        Expect.Finding(r, "WARNING", "conditions", "condition function 9999 isn't one sf-mod-check knows");
        Assert.DoesNotContain(r.Findings, f => f.Check == "read");
    }

    [Fact]
    public void A_package_target_condition_with_an_empty_form_parameter_is_found()
    {
        using var w = new World();
        var p = w.Build("pkgempty", m => AddPackage(m, new ConditionFloat { Data = new GetInCurrentLocationFormListConditionData(), ComparisonValue = 1 }));
        Expect.Finding(w.Check(p), "WARNING", "conditions", "SFM_Package");
    }

    [Fact]
    public void Alias_numbers_in_quest_response_and_package_conditions_must_be_in_the_quest()
    {
        using var w = new World();
        var p = w.Build("condalias", m =>
        {
            var q = m.Quests.First();
            q.DialogConditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 11 }, ComparisonValue = 1 });
            q.DialogTopics.First().Responses.First().Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 12 }, ComparisonValue = 1 });
            AddPackage(m, new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 13 }, ComparisonValue = 1 });
        });
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "conditions", "SFM_Quest");
        Expect.Finding(r, "WARNING", "conditions", "condition GetIsAliasRef tests alias 11, which its quest SFM_Quest");
        Expect.Finding(r, "WARNING", "conditions", "SFM_Info");
        Expect.Finding(r, "WARNING", "conditions", "tests alias 12, which its quest SFM_Quest");
        Expect.Finding(r, "WARNING", "conditions", "tests alias 13, which its quest SFM_Quest");
        Assert.DoesNotContain(r.Findings, f => f.Check == "scenes");
    }

    [Fact]
    public void Aliases_the_quest_has_and_packages_without_an_owner_quest_are_fine()
    {
        using var w = new World();
        var p = w.Build("condaliasok", m =>
        {
            var q = m.Quests.First();
            q.DialogConditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 0 }, ComparisonValue = 1 });
            q.DialogTopics.First().Responses.First().Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 0 }, ComparisonValue = 1 });
            AddPackage(m, new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 0 }, ComparisonValue = 1 });
            var loose = new Package(m) { EditorID = "SFM_LoosePackage" };   // no owner quest: no quest to check against
            loose.Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 40 }, ComparisonValue = 1 });
            m.Packages.Add(loose);
        });
        Expect.No(w.Check(p), "conditions");
    }

    [Fact]
    public void A_ref_collection_aliases_numbers_count_as_the_quests_aliases()
    {
        // 0.10.0 code review: AliasIds skipped collection aliases, so a condition on one warned falsely
        var mod = new StarfieldMod(ModKey.FromFileName("SfmColl.esm"), StarfieldRelease.Starfield);
        var cq = mod.Quests.AddNew("SFM_CollQuest");
        (cq.Aliases ??= new()).Add(new QuestCollectionAlias { Collection = { new CollectionAlias { ID = 5, ReferenceAlias = new QuestReferenceAlias { ID = 6 } } } });
        Assert.Equal(new uint[] { 5, 6 }, Rules.AliasIds(cq).Order());

        using var w = new World();
        var p = w.Build("condaliascoll", m =>
        {
            var q = m.Quests.First();
            q.Aliases!.Add(new QuestCollectionAlias { Collection = { new CollectionAlias { ID = 5, ReferenceAlias = new QuestReferenceAlias { ID = 6, Name = "SFM_Coll" } } } });
            q.DialogConditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 5 }, ComparisonValue = 1 });
            q.DialogTopics.First().Responses.First().Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 6 }, ComparisonValue = 1 });
        });
        Expect.No(w.Check(p), "conditions");
    }

    [Fact]
    public void Official_alias_uses_are_asked_for_only_for_official_records()
    {
        var mod = new StarfieldMod(ModKey.FromFileName("SfmWalk.esm"), StarfieldRelease.Starfield);
        var q = mod.Quests.AddNew("SFM_Quest");
        // a new record of a non-official plugin has no official versions, so the cache isn't asked
        Assert.Empty(Rules.OfficialAliasUses(null!, q, _ => false));
    }
}
