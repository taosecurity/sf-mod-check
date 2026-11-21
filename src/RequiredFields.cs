// The fields check (0.10.0, README "Known gaps" until 0.9.1): a record that lacks a field every official record of its
// type has. The list is learned from the official masters with the hidden --learn-required-fields into
// data\required-fields-seen.txt (record types, field signatures and counts only), the way the condition files are.
// Raw read (no Mutagen): a field is a subrecord signature, counted once per record. Deleted records and partial
// forms (record flag 0x4000) hold only some fields on purpose, so they are left out of both the learning and the check.
using System.Diagnostics;
using System.Text;

namespace SfModCheck;

public static class RequiredFields
{
    /// <summary>A record type needs at least this many official records before any of its fields counts as required
    /// (a type with a handful of records says little about what the game needs).</summary>
    public const int MinRecords = 20;

    /// <summary>Partial form (Starfield record flag 0x4000): only the fields it holds replace the earlier version's.</summary>
    const uint Partial = 0x4000;

    /// <summary>Whether a record is checked (and counted when learning): not deleted, not a partial form.</summary>
    public static bool Counts(uint flags) => (flags & (Flags.Deleted | Partial)) == 0;

    /// <summary>record type -> (field -> how many official records of that type have it; all of them do).</summary>
    static readonly Lazy<Dictionary<string, Dictionary<string, long>>> Seen = new(() =>
    {
        using var s = typeof(RequiredFields).Assembly.GetManifestResourceStream("required-fields-seen.txt");
        return s == null ? new() : Parse(new StreamReader(s));
    });

    /// <summary>Reads the data file's lines: "&lt;record type&gt; &lt;field&gt; &lt;records&gt;".</summary>
    public static Dictionary<string, Dictionary<string, long>> Parse(TextReader rd)
    {
        var d = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        while (rd.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var p = line.Split(' ');
            if (!d.TryGetValue(p[0], out var f)) d[p[0]] = f = new(StringComparer.Ordinal);
            f[p[1]] = long.Parse(p[2]);
        }
        return d;
    }

    /// <summary>Record types with at least one required field (for the log and a test).</summary>
    public static int TypeCount => Seen.Value.Count;
    public static int FieldCount => Seen.Value.Values.Sum(f => f.Count);

    /// <summary>Findings gathered during the raw pass: (record type, field) -> how many records lack it, and a few names.</summary>
    public sealed class Findings
    {
        public readonly Dictionary<(string Sig, string Field), (int Count, List<string> Examples)> Missing = new();
        public int Checked;
    }

    /// <summary>Checks one raw record against the learned list. name: the record's display name, built only when needed.</summary>
    public static void Check(RawRecord rec, List<(string Type, byte[] Data)> fields, Func<string> name, Findings f,
                             Dictionary<string, Dictionary<string, long>>? table = null)
    {
        if (!Counts(rec.Flags)) return;
        if (!(table ?? Seen.Value).TryGetValue(rec.Sig, out var need)) return;
        f.Checked++;
        string? n = null;
        foreach (var field in need.Keys)
        {
            if (fields.Exists(x => x.Type == field)) continue;
            var k = (rec.Sig, field);
            var v = f.Missing.GetValueOrDefault(k, (0, new List<string>()));
            n ??= name();
            if (v.Item2.Count < 3) v.Item2.Add(n);
            f.Missing[k] = (v.Item1 + 1, v.Item2);
            if (Log.On) Log.Write($"fields: {n} has no {field} field (all {need[field]} official {rec.Sig} records have one)");
        }
    }

    /// <summary>One WARNING text per (record type, field), in a fixed order.</summary>
    public static IEnumerable<string> Texts(Findings f, Dictionary<string, Dictionary<string, long>>? table = null)
    {
        var t = table ?? Seen.Value;
        foreach (var ((sig, field), (count, ex)) in f.Missing.OrderBy(k => k.Key.Sig, StringComparer.Ordinal).ThenBy(k => k.Key.Field, StringComparer.Ordinal))
            yield return $"{count} {sig} record{(count == 1 ? "" : "s")} {(count == 1 ? "has" : "have")} no {field} field, which all {t[sig][field]:N0} {sig} records in the official masters have, e.g. {string.Join(", ", ex)}";
    }

    /// <summary>--learn-required-fields &lt;out&gt; [Data]: for each record type in the official masters (deleted
    /// records and partial forms left out), the fields every record has. Raw read; writes record types, field
    /// signatures and counts only.</summary>
    public static void Learn(string dataDir, string outPath)
    {
        var sw = Stopwatch.StartNew();
        var records = new Dictionary<string, long>(StringComparer.Ordinal);
        var has = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var masters = Checker.OfficialMasters(dataDir);
        long problems = 0;
        foreach (var m in masters)
        {
            using var file = new RawFile(Path.Combine(dataDir, m));
            var w = new RawWalker(file, deep: true, (_, _, _) => problems++)
            {
                OnFields = (rec, fields) =>
                {
                    if (!Counts(rec.Flags)) return;
                    records[rec.Sig] = records.GetValueOrDefault(rec.Sig) + 1;
                    if (!has.TryGetValue(rec.Sig, out var h)) has[rec.Sig] = h = new(StringComparer.Ordinal);
                    foreach (var t in fields.Select(x => x.Type).Where(x => x != "XXXX").Distinct()) h[t] = h.GetValueOrDefault(t) + 1;
                },
            };
            w.Walk();
            Console.WriteLine($"  {m}: {w.Records} records ({sw.Elapsed.TotalSeconds:F0} s)");
        }
        using var o = new StreamWriter(outPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
        o.WriteLine("# Fields every record of a type has in the official masters: <record type> <field> <records>.");
        o.WriteLine($"# Made by sf-mod-check --learn-required-fields from {string.Join(", ", masters)}.");
        o.WriteLine($"# Raw read; deleted records and partial forms left out; only record types with at least {MinRecords} records. {problems} layout problems.");
        int lines = 0;
        foreach (var (sig, n) in records.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (n < MinRecords) continue;
            foreach (var (field, c) in has[sig].OrderBy(k => k.Key, StringComparer.Ordinal))
                if (c == n) { o.WriteLine($"{sig} {field} {n}"); lines++; }
        }
        Console.WriteLine($"{records.Count} record types, {lines} required fields -> {outPath} ({sw.Elapsed.TotalSeconds:F0} s)");
    }
}
