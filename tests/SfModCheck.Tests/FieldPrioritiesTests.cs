// 0.10.0 item 7 (plan step 8b, first part): the field-priority table loader and the rating of lost changes.
using Mutagen.Bethesda.Plugins;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class FieldPrioritiesTests
{
    static readonly FieldPriorityTable T = FieldPriorityTable.Load();

    static LostField F(string name) => new(name, false, "a", "b", new() { new($"{name} differs", false) }, "a", new(), new());

    static LostChange L(string type, string formKey, string origin, params string[] fields) =>
        new(FormKey.Factory(formKey), type, "Ed", null, origin, "Loser.esm", false, "Winner.esm", false, fields.Select(F).ToList());

    [Fact]
    public void The_built_in_table_loads_with_the_generator_counts()
    {
        // data\field-priorities.txt header: 203 record types, 2,796 field rows, 5 header rows
        Assert.Equal(203, T.Signatures.Count);
        Assert.Equal(2796, T.Fields.Count);
        Assert.Equal(5, T.Header.Count);
        Assert.Equal(2, T.BenignIfAdded.Count);
        Assert.Equal(6, T.Dynamic.Count);
        Assert.Equal("REFR", T.SignatureOf("PlacedObject"));
        Assert.Equal("GMST", T.SignatureOf("GameSettingFloat"));
    }

    [Fact]
    public void Table_values_and_the_default()
    {
        Assert.Equal(FieldPriority.Benign, T.Of("Keyword", "EditorID"));
        Assert.Equal(FieldPriority.Critical, T.Of("GameSettingInt", "EditorID"));
        Assert.Equal(FieldPriority.Normal, T.Of("Keyword", "NoSuchField"));      // unlisted: normal
        Assert.Equal(FieldPriority.Normal, T.Of("NoSuchClass", "EditorID"));      // unknown type: normal
        Assert.Equal(FieldPriority.Normal, T.Of("Keyword", "MajorRecordFlagsRaw")); // the header's MajorFlags row
        Assert.Equal(FieldPriority.Ignore, T.Of("Keyword", "FormVersion"));       // header row
    }

    [Fact]
    public void Location_reference_lists_stay_normal_but_the_master_lists_are_benign()
    {
        Assert.Equal(FieldPriority.Normal, T.Of("Location", "AddedPersistLocationReferences"));
        Assert.Equal(FieldPriority.Normal, T.Of("Location", "RemovedUniqueNpcs"));
        Assert.Equal(FieldPriority.Benign, T.Of("Location", "MasterPersistLocationReferences"));
        Assert.Equal(FieldPriority.Benign, T.Of("Location", "EditorID"));
    }

    [Fact]
    public void Benign_if_added_and_dynamic_fields_keep_their_static_value_in_a_lost_change()
    {
        Assert.Contains(("REFR", "Location"), T.BenignIfAdded);
        Assert.Equal(FieldPriority.Normal, T.Of("PlacedObject", "Location"));
        Assert.Equal("wbNAVMEdgeLinksGetCP", T.Dynamic[("NAVM", "NavmeshGeometry")]);
        Assert.Equal(FieldPriority.Normal, T.Of("NavigationMesh", "NavmeshGeometry"));
    }

    [Fact]
    public void Injected_records_raise_normal_fields_to_critical_except_GMST_and_DFOB()
    {
        Assert.Equal(FieldPriority.Critical, T.Of("Keyword", "Color", injected: true));
        Assert.Equal(FieldPriority.Benign, T.Of("Keyword", "EditorID", injected: true));      // benign stays benign
        Assert.Equal(FieldPriority.Normal, T.Of("DefaultObject", "Object", injected: true));
        Assert.Equal(FieldPriority.Normal, T.Of("GameSettingFloat", "Data", injected: true));
        Assert.True(FieldPriorityTable.IsInjected(L("Keyword", "000801:Starfield.esm", "Mod.esm", "Color")));
        Assert.False(FieldPriorityTable.IsInjected(L("Keyword", "000801:Starfield.esm", "starfield.esm", "Color")));
    }

    [Fact]
    public void The_analyzer_answers_unknown_when_the_owner_wasnt_read_in_full()
    {
        // 0.10.0 code review round 3: the three answers the analyzer gives a lost change
        Assert.False(Analyzer.Injection(ownerHoldsAVersion: true, ownerNotReadInFull: true));
        Assert.False(Analyzer.Injection(ownerHoldsAVersion: true, ownerNotReadInFull: false));
        Assert.Null(Analyzer.Injection(ownerHoldsAVersion: false, ownerNotReadInFull: true));
        Assert.True(Analyzer.Injection(ownerHoldsAVersion: false, ownerNotReadInFull: false));
    }

    [Fact]
    public void The_analyzers_injection_answer_wins_over_the_first_version()
    {
        // 0.10.0 code review: Starfield.esm holds the record (as another type, say), so the pass's first version is a mod's; not an injection
        var ownerHoldsIt = L("Keyword", "000801:Starfield.esm", "ModA.esm", "Color") with { Injected = false };
        Assert.False(FieldPriorityTable.IsInjected(ownerHoldsIt));
        Assert.Equal(FieldPriority.Normal, T.Top(ownerHoldsIt));
        Assert.True(FieldPriorityTable.IsInjected(L("Keyword", "000801:Starfield.esm", "Starfield.esm", "Color") with { Injected = true }));
        // round 2: no answer (owner read only partly) falls back to the first version, so a possible injection stays critical
        var unknown = L("Keyword", "000801:Starfield.esm", "ModA.esm", "Color") with { Injected = null };
        Assert.Equal(FieldPriority.Critical, T.Top(unknown));
    }

    [Fact]
    public void SplitLost_moves_benign_fields_apart_and_puts_critical_entries_first()
    {
        var plain = L("Keyword", "000801:Starfield.esm", "Starfield.esm", "Color", "EditorID");
        var onlyBenign = L("Keyword", "000802:Starfield.esm", "Starfield.esm", "EditorID");
        var crit = L("GameSettingInt", "000803:Starfield.esm", "Starfield.esm", "EditorID");
        var s = T.SplitLost([plain, onlyBenign, crit]);
        Assert.Equal(new[] { "000803:Starfield.esm", "000801:Starfield.esm" }, s.Kept.Select(l => l.FormKey.ToString()));
        Assert.Equal(new[] { "Color" }, s.Kept[1].Fields.Select(f => f.Field));
        Assert.Equal(FieldPriority.Critical, s.Top[s.Kept[0]]);
        Assert.Equal(FieldPriority.Normal, s.Top[s.Kept[1]]);
        Assert.Equal(new[] { "000801:Starfield.esm", "000802:Starfield.esm" }, s.Quiet.Select(l => l.FormKey.ToString()));
        Assert.All(s.Quiet, q => Assert.Equal(new[] { "EditorID" }, q.Fields.Select(f => f.Field)));
        Assert.Same(onlyBenign, s.Quiet[1]);   // an entry wholly on one side is passed through
        Assert.Same(crit, s.Kept[0]);
        Assert.Equal(FieldPriority.Critical, T.Top(crit));
    }

    [Theory]
    [InlineData("KYWD Color loud", "unknown priority 'loud'")]
    [InlineData("type Keyword", "expected 'type")]
    [InlineData("KYWD Color normal shiny", "unknown flag 'shiny'")]
    [InlineData("Keyword Color normal", "expected '<record type>")]
    public void A_bad_line_names_the_file_and_line(string bad, string why)
    {
        var e = Assert.Throws<FormatException>(() => FieldPriorityTable.Parse(new StringReader("# c\n" + bad + "\n"), "t.txt"));
        Assert.Contains("t.txt line 2", e.Message);
        Assert.Contains(why, e.Message);
    }
}
