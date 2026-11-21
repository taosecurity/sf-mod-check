// 0.10.0 item 7 (plan step 8b, first part): xEdit-style field conflict priorities for `conflict`.
// The table is data\field-priorities.txt (made by data\gen\gen_field_priorities.py from xEdit's Starfield definitions,
// MPL-2.0; see NOTICE). This file loads it and rates the fields of each lost change:
//
//   ignore    never counts (xEdit doesn't compare it)
//   benign    a harmless difference (most Editor IDs, location reference lists): shown apart, not a lost change
//   normal    an ordinary difference (every field the table doesn't list)
//   critical  ranks first (the GMST Editor ID, and every normal-or-higher field of an injected record)
//
// Run-time rules on top of the table (ported from the private tracker's priorities.py):
//   - injected records: when the record's first version isn't in the plugin that owns its FormID, every normal or
//     higher field becomes critical, except in GMST and DFOB (xEdit's rule).
//   - benign-if-added (Location on placed references): benign only when the earlier copy lacks the value and no later
//     plugin sets another one. A lost change always has a later plugin with another value, so these stay normal here.
//   - dynamic callbacks: wbModelInfoGetCP gives ignore only below form version 38, which no Starfield record uses;
//     wbNAVMEdgeLinksGetCP is normal with xEdit's default options. Both keep the table's static value.
//   - our own departure: LCTN Added*/Removed* reference lists stay normal (xEdit says benign), because quest aliases
//     that pick a reference by its location ref type read them, so losing one can change what a quest finds.
//   - a second departure (user, 2026-11-21): the Conditions of Story Manager and Planet Content Manager nodes (SMBN,
//     SMQN, SMEN, PCBN, PCMT) stay normal (xEdit says benign), because they decide when a quest starts or what
//     content a planet gets. The tree links (PreviousSibling, Parent, Previous) keep xEdit's rating.


namespace SfModCheck.Conflict;

public enum FieldPriority { Ignore = 0, Benign = 1, Normal = 2, Critical = 3 }

public sealed class FieldPriorityTable
{
    public readonly Dictionary<string, string> Signatures = new(StringComparer.Ordinal);   // Mutagen class -> SIG
    public readonly Dictionary<(string Sig, string Field), FieldPriority> Fields = new();
    public readonly HashSet<(string Sig, string Field)> BenignIfAdded = new();
    public readonly Dictionary<(string Sig, string Field), string> Dynamic = new();
    public readonly Dictionary<string, FieldPriority> Header = new(StringComparer.Ordinal);

    static readonly Lazy<FieldPriorityTable> Builtin = new(() =>
    {
        using var s = typeof(FieldPriorityTable).Assembly.GetManifestResourceStream("field-priorities.txt")
            ?? throw new InvalidOperationException("the built-in data file field-priorities.txt is missing from this build; rebuild sf-mod-check, or run conflict with --no-priorities");
        using var rd = new StreamReader(s);
        return Parse(rd, "field-priorities.txt");
    });

    /// <summary>The table embedded in sf-mod-check (data\field-priorities.txt).</summary>
    public static FieldPriorityTable Load() => Builtin.Value;

    static FieldPriority ParsePriority(string s, string where) => s switch
    {
        "ignore" => FieldPriority.Ignore,
        "benign" => FieldPriority.Benign,
        "normal" => FieldPriority.Normal,
        "critical" => FieldPriority.Critical,
        _ => throw new FormatException($"{where}: unknown priority '{s}' (expected ignore, benign, normal or critical)"),
    };

    /// <summary>Reads the table format: '#' comments, 'type &lt;Mutagen class&gt; &lt;SIG&gt;', 'header &lt;field&gt; &lt;priority&gt;'
    /// and '&lt;SIG&gt; &lt;Mutagen field&gt; &lt;priority&gt; [benign-if-added] [dynamic=&lt;callback&gt;]'. A bad line names the file
    /// and line number.</summary>
    public static FieldPriorityTable Parse(TextReader rd, string name)
    {
        var t = new FieldPriorityTable();
        int n = 0;
        while (rd.ReadLine() is { } raw)
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var where = $"{name} line {n}";
            if (p[0] == "type")
            {
                if (p.Length != 3) throw new FormatException($"{where}: expected 'type <Mutagen class> <record type>'");
                t.Signatures[p[1]] = p[2];
            }
            else if (p[0] == "header")
            {
                if (p.Length != 3) throw new FormatException($"{where}: expected 'header <field> <priority>'");
                t.Header[p[1]] = ParsePriority(p[2], where);
            }
            else
            {
                if (p.Length < 3 || p[0].Length != 4) throw new FormatException($"{where}: expected '<record type> <field> <priority> [flags]'");
                var key = (p[0], p[1]);
                t.Fields[key] = ParsePriority(p[2], where);
                foreach (var flag in p.Skip(3))
                {
                    if (flag == "benign-if-added") t.BenignIfAdded.Add(key);
                    else if (flag.StartsWith("dynamic=")) t.Dynamic[key] = flag["dynamic=".Length..];
                    else throw new FormatException($"{where}: unknown flag '{flag}'");
                }
            }
        }
        return t;
    }

    // Record types the injected-record rule leaves alone (xEdit's own exceptions).
    static readonly HashSet<string> InjectExempt = ["GMST", "DFOB"];

    /// <summary>The record signature for a Mutagen record class name (LostChange.RecordType), or null.</summary>
    public string? SignatureOf(string mutagenClass) => Signatures.GetValueOrDefault(mutagenClass);

    /// <summary>Story Manager and Planet Content Manager nodes whose Conditions stay normal (xEdit says benign).</summary>
    static readonly HashSet<string> TreeNodeConditions = new(StringComparer.Ordinal) { "SMBN", "SMQN", "SMEN", "PCBN", "PCMT" };

    /// <summary>One field's priority in a lost change, with the run-time rules (see the top of this file).
    /// injected: the record's first version isn't in the plugin that owns its FormID.</summary>

    public FieldPriority Of(string mutagenClass, string field, bool injected = false)
    {
        var sig = SignatureOf(mutagenClass);
        FieldPriority p;
        if (sig == "LCTN" && (field.StartsWith("Added", StringComparison.Ordinal) || field.StartsWith("Removed", StringComparison.Ordinal)))
            p = FieldPriority.Normal;   // our departure from xEdit (quest aliases read these lists)
        else if (field == "Conditions" && sig is not null && TreeNodeConditions.Contains(sig))
            p = FieldPriority.Normal;   // second departure (user, 2026-11-21): they decide when a quest starts or content spawns
        else if (sig is not null && Fields.TryGetValue((sig, field), out var fp))
            p = fp;                     // benign-if-added and the dynamic callbacks keep the static value in a lost change
        else if (Header.TryGetValue(field == "MajorRecordFlagsRaw" ? "MajorFlags" : field, out var hp))
            p = hp;                     // conflict compares the header flags as MajorRecordFlagsRaw
        else
            p = FieldPriority.Normal;   // xEdit's default, cpNormal
        if (injected && p >= FieldPriority.Normal && !(sig is not null && InjectExempt.Contains(sig)))
            p = FieldPriority.Critical;
        return p;
    }

    /// <summary>True when the plugin that owns the record's FormID doesn't hold it: the analyzer's answer when it gave
    /// one (false when the owner holds a version, of this type or another), else (owner read only partly, or no
    /// answer) the first version (Origin) isn't in the owning plugin, so a possible injection counts as one.</summary>
    public static bool IsInjected(LostChange l) =>
        l.Injected ?? !string.Equals(l.Origin, l.FormKey.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase);

    /// <summary>The highest priority among a lost change's fields.</summary>
    public FieldPriority Top(LostChange l)
    {
        var inj = IsInjected(l);
        return l.Fields.Count == 0 ? FieldPriority.Ignore : l.Fields.Max(f => Of(l.RecordType, f.Field, inj));
    }

    /// <summary>Splits lost changes by priority: Kept holds each entry's normal and critical fields (entries with a
    /// critical field first, otherwise in the given order), Quiet holds the ignore and benign fields as entries of
    /// their own (same order). An entry with no field left on a side isn't on that side. Each entry's fields keep their order.</summary>
    public Split SplitLost(IEnumerable<LostChange> lost)
    {
        var kept = new List<LostChange>();
        var critical = new List<LostChange>();
        var quiet = new List<LostChange>();
        var top = new Dictionary<LostChange, FieldPriority>(ReferenceEqualityComparer.Instance);
        foreach (var l in lost)
        {
            var inj = IsInjected(l);
            var rated = l.Fields.Select(f => (f, p: Of(l.RecordType, f.Field, inj))).ToList();
            var loud = rated.Where(r => r.p >= FieldPriority.Normal).ToList();
            var soft = rated.Where(r => r.p < FieldPriority.Normal).Select(r => r.f).ToList();
            if (loud.Count > 0)
            {
                var k = loud.Count == l.Fields.Count ? l : l with { Fields = loud.Select(r => r.f).ToList() };
                var t = loud.Max(r => r.p);
                top[k] = t;
                (t == FieldPriority.Critical ? critical : kept).Add(k);
            }
            if (soft.Count > 0) quiet.Add(soft.Count == l.Fields.Count ? l : l with { Fields = soft });
        }
        critical.AddRange(kept);
        return new Split(critical, quiet, top);
    }

    /// <summary>Applies the priorities to a conflict analysis (never to patch or triage): Lost becomes SplitLost's
    /// Kept, Benign its Quiet, CriticalFields the critical fields of the kept entries, and PrioritiesOn is set.</summary>
    public void Apply(Analysis a)
    {
        var s = SplitLost(a.Lost);
        a.Lost.Clear();
        a.Lost.AddRange(s.Kept);
        a.Benign.Clear();
        a.Benign.AddRange(s.Quiet);
        a.CriticalFields.Clear();
        foreach (var l in s.Kept.Where(l => s.Top[l] == FieldPriority.Critical))
        {
            var inj = IsInjected(l);
            foreach (var f in l.Fields.Where(f => Of(l.RecordType, f.Field, inj) == FieldPriority.Critical)) a.CriticalFields.Add(f);
        }
        a.PrioritiesOn = true;
    }

    /// <summary>"ignore" or "benign" (any priority as its lower-case name) for one field of a lost change.</summary>
    public string NameOf(LostChange l, LostField f) => Of(l.RecordType, f.Field, IsInjected(l)).ToString().ToLowerInvariant();

    /// <summary>SplitLost's result. Top: each kept entry's highest priority (Normal or Critical).</summary>
    public sealed record Split(List<LostChange> Kept, List<LostChange> Quiet, IReadOnlyDictionary<LostChange, FieldPriority> Top);
}
