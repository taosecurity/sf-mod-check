// Moved from StarfieldConflictCheck (Impact.cs, commit d336108) into sfmodcheck on 2026-11-16.
// Plain-English impact statements: what a lost change means in game, with the exact differences spelled out.
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;

namespace SfModCheck.Conflict;


// Minor: only list order or undocumented "Unknown" fields differ, which probably has no effect in game.
public record Difference(string Text, bool Minor);

// The parts of an impact statement: what the record is, where it started, what each plugin does, and the result.
public record ImpactText(string What, string Original, string LoserDid, string WinnerDid, string Result);

// One structural difference between two values; rendered as "in game" text or as "what a plugin changed" text.
record RawDiff(string Path, string Kind, string From, string To, bool Minor);

public static class Impact
{
    /// <summary>"a CELL", "an ARMO", "an OTFT", "an NPC_": a record signature with its article (0.5.3: check said
    /// "a ACHR"; conflict already picked a/an). Signatures are read as words, so the first letter decides, except NPC_,
    /// which everyone reads as letters ("an N-P-C").</summary>
    public static string WithArticle(string sig) =>
        ("AEIOU".Contains(char.ToUpperInvariant(sig.FirstOrDefault())) || sig.StartsWith("NPC", StringComparison.OrdinalIgnoreCase) ? "an " : "a ") + sig;

    /// <summary>An exception as one short line, "IOException: first line of its message" (0.5.3: one helper for every
    /// read-problem note in conflict).</summary>
    public static string Summary(Exception e) => $"{e.GetType().Name}: {OneLine(e.Message.Split('\n')[0])}";

    // What a field means to a player, by field name. Anything not listed falls back to its split-up name.
    static readonly Dictionary<string, string> FieldMeaning = new()
    {
        ["Name"] = "the name shown in game",
        ["EditorID"] = "its editor ID (an internal name with no effect in game)",
        ["MapMarker"] = "its map marker (the name and icon shown on the map)",
        ["VirtualMachineAdapter"] = "the scripts attached to it",
        ["Conditions"] = "the conditions that control when it applies",
        ["DialogConditions"] = "the conditions that control when its dialogue can play",
        ["Aliases"] = "the quest aliases (the actors, objects and places the quest tracks)",
        ["Stages"] = "the quest stages",
        ["Objectives"] = "the quest objectives shown in the mission log",
        ["Keywords"] = "its keywords (tags other game systems check)",
        ["Items"] = "its contents",
        ["Value"] = "its value in credits",
        ["Weight"] = "its weight",
        ["Model"] = "its 3D model",
        ["Effects"] = "its effects",
        ["Components"] = "its component data (for worlds: surface, biome and content settings)",
        ["Location"] = "the location it belongs to",
        ["LocationReference"] = "the location it belongs to",
        ["Data"] = "its main settings",
        ["MajorRecordFlagsRaw"] = "its record flags (persistence, disabled state and similar)",
        ["Base"] = "what object this is (its base object)",
        ["Position"] = "its position",
        ["Rotation"] = "its rotation",
        ["Placement"] = "its position and rotation",
        ["EnableParent"] = "what enables or disables it",
        ["ActorReactions"] = "which companions react and how strongly",
        ["RequiredFaction"] = "the faction required for the reaction",
        ["Factions"] = "its faction memberships",
        ["Outfit"] = "its outfit",
        ["Perks"] = "its perks",
        ["Spells"] = "its spells and abilities",
        ["Packages"] = "its AI packages (daily behaviour)",
        ["QuestType"] = "the quest category",
        ["LightingTemplate"] = "its lighting template",
        ["NumTraversalFluffBytes"] = "its pathing data",
        ["AddedSpecialReferences"] = "the special references added to this location (key objects such as bosses, map markers, doors or quest targets that the location tracks by role)",
        ["MasterSpecialReferences"] = "the special references this location inherits from the master files",
        ["AddedPersistLocationReferences"] = "the persistent references added to this location",
        ["MasterPersistLocationReferences"] = "the persistent references this location inherits",
        ["AddedUniqueNpcs"] = "the unique NPCs added to this location",
        ["MasterUniqueNpcs"] = "the unique NPCs this location inherits",
        ["LocationCellUniqueReferences"] = "the unique NPCs placed in this location's cells",
        ["Flags"] = "its flags",
    };

    // What a record type is, for the "What this is" line.
    static readonly Dictionary<string, string> TypeMeaning = new()
    {
        ["Location"] = "a named place (a city, outpost, ship or planet area) that quests, encounters and the map use",
        ["PlacedObject"] = "one specific object placed in the world",
        ["PlacedNpc"] = "one specific NPC placed in the world",
        ["Quest"] = "a quest or background quest that runs game logic",
        ["Worldspace"] = "a world: a planet surface or other large exterior",
        ["Cell"] = "an interior, or one piece of a world",
        ["AffinityEvent"] = "an event that companions react to",
        ["DialogResponses"] = "a line of dialogue",
        ["DialogTopic"] = "a dialogue topic",
        ["Scene"] = "a scripted scene",
        ["Npc"] = "an NPC or creature template",
        ["FormList"] = "a list of records that scripts and conditions check",
        ["GameSettingFloat"] = "an engine setting", ["GameSettingInt"] = "an engine setting", ["GameSettingUInt"] = "an engine setting",
    };

    // Short nouns for summary lines ("the game uses X's scripts instead of Y's").
    static readonly Dictionary<string, string> FieldNoun = new()
    {
        ["Name"] = "name", ["MapMarker"] = "map marker", ["VirtualMachineAdapter"] = "scripts", ["Conditions"] = "conditions",
        ["DialogConditions"] = "dialogue conditions", ["Aliases"] = "quest aliases", ["Stages"] = "quest stages",
        ["Objectives"] = "objectives", ["Components"] = "component data", ["MajorRecordFlagsRaw"] = "record flags",
        ["Data"] = "main settings", ["Base"] = "base object", ["ActorReactions"] = "companion reactions",
    };

    static readonly Dictionary<string, string> TypeNames = new()
    {
        ["PlacedObject"] = "placed object", ["PlacedNpc"] = "placed NPC", ["Npc"] = "NPC", ["Cell"] = "cell",
        ["Worldspace"] = "world", ["Quest"] = "quest", ["AffinityEvent"] = "companion reaction event",
        ["DialogResponses"] = "dialogue line", ["DialogTopic"] = "dialogue topic", ["Scene"] = "scene",
        ["GameSettingFloat"] = "game setting", ["GameSettingInt"] = "game setting", ["GameSettingUInt"] = "game setting",
        ["GameSettingBool"] = "game setting", ["GameSettingString"] = "game setting", ["FormList"] = "form list",
        ["LeveledItem"] = "leveled list", ["LeveledNpc"] = "leveled NPC list", ["Weapon"] = "weapon", ["Armor"] = "armor piece",
        ["Location"] = "location", ["Container"] = "container", ["Perk"] = "perk", ["Spell"] = "spell", ["MagicEffect"] = "magic effect",
        ["ObjectModification"] = "mod (item modification)", ["Terminal"] = "terminal", ["Book"] = "book", ["Faction"] = "faction",
    };

    static string D(string plugin) => LoadOrderSetup.Display(plugin);

    public static string Noun(string field) => FieldNoun.TryGetValue(field, out var n) ? n : Words(field);

    public static string Meaning(string field) => FieldMeaning.TryGetValue(field, out var m) ? m : "its " + Words(field);

    public static string TypeName(string recordType, int count = 1)
    {
        var n = TypeNames.TryGetValue(recordType, out var t) ? t : Words(recordType);
        if (count == 1) return n;
        if (t is null) return n + " records";   // not in the table: no guessing at English plurals (0.5.0)
        var cut = n.IndexOf(" (", StringComparison.Ordinal);   // "mod (item modification)" -> "mods (item modification)"
        return cut < 0 ? n + "s" : n[..cut] + "s" + n[cut..];   // the table holds regular singular nouns only
    }

    static string Words(string camel) => Regex.Replace(camel, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();

    // The four-part impact for a single lost field.
    public static ImpactText ForField(LostChange l, LostField f)
    {
        var what = TypeMeaning.TryGetValue(l.RecordType, out var tm) ? tm : TypeName(l.RecordType);
        var id = l.RecordName is { Length: > 0 } rn ? $"\"{rn}\" ({l.EditorID ?? l.FormKey.ToString()})" : (l.EditorID ?? l.FormKey.ToString());
        var whatLine = $"{Cap(TypeName(l.RecordType))} {id}: {what}. The conflict is in {Meaning(f.Field)}.";
        if (l.EditorID is { } ed && Regex.IsMatch(ed, "(Debug|Test|Dev)(?![a-z])"))
            whatLine += " Its editor ID marks it as a developer test record, so it probably never matters in normal play.";
        var orig = f.Original.Length > 450 ? f.Original[..450] + "…" : f.Original;
        var original = $"In {(l.Origin.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) ? "the base game (Starfield.esm)" : D(l.Origin))}: {orig}.";
        string Did(string plugin, List<Difference> changes) => changes.Count == 0
            ? $"{D(plugin)} keeps the base game value."
            : $"{D(plugin)}: " + string.Join("; ", changes.Select(c => c.Text)) + ".";
        var loserDid = Did(l.Loser, f.LoserChanges);
        var winnerDid = Did(l.Winner, f.WinnerChanges);

        string result;
        if (IsMinor(f))
            result = f.LoserChanges.Count > 0 && f.LoserChanges.All(d => d.Minor)
                ? $"{D(l.Winner)} wins, so the change from {D(l.Loser)} does not happen, but that change only reorders a list, changes letter case or touches undocumented fields. This probably has no visible effect. No action needed."
                : $"{D(l.Winner)} wins, but its version differs from {D(l.Loser)} only in list order, letter case or undocumented fields, so this probably has no visible effect. No action needed.";
        else
        {
            var lost = f.Differences.Count == 0 ? "" : " Compared with what " + D(l.Loser) + " wanted: " + string.Join("; ", f.Differences.Select(d => d.Text)) + ".";
            result = f.WinnerChangedToo
                ? $"{D(l.Winner)} loads later, so its version is used and the change from {D(l.Loser)} does not happen.{lost} Keep it as is if you prefer the {D(l.Winner)} version; otherwise a patch has to combine the two."
                : $"{D(l.Winner)} loads later but did not mean to change this; it carries the older value, so the change from {D(l.Loser)} does not happen.{lost} A patch can restore it without affecting the rest of {D(l.Winner)}.";
            if (l.LoserOfficial) result += $" This undoes part of the official {D(l.Loser)}.";
        }
        return new(whatLine, original, loserDid, winnerDid, result);
    }

    // Losing this field costs nothing visible: the loser only reordered or touched undocumented data, or the versions differ only that way.
    public static bool IsMinor(LostField f) =>
        (f.LoserChanges.Count > 0 && f.LoserChanges.All(d => d.Minor)) ||
        (f.Differences.Count > 0 && f.Differences.All(d => d.Minor));

    public static string Flat(ImpactText t) => $"{t.What} {t.Original} {t.LoserDid} {t.WinnerDid} Result: {t.Result}";

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // Short summary for a whole winner/loser pair.
    public static List<string> ForGroup(IGrouping<(string Winner, string Loser), LostChange> g)
    {
        var lines = new List<string>();
        var byField = g.SelectMany(l => l.Fields.Select(f => (l, f)))
            .GroupBy(x => (x.f.Field, x.l.RecordType))
            .OrderByDescending(x => x.Count());
        foreach (var fg in byField)
        {
            var n = fg.Count();
            var fwd = fg.Count(x => !x.f.WinnerChangedToo);
            var example = fg.SelectMany(x => x.f.LoserChanges).Where(d => !d.Minor).OrderBy(d => d.Text.Length).FirstOrDefault();
            var line = $"{n} {TypeName(fg.Key.RecordType, n)}: the game uses the {Noun(fg.Key.Field)} from {D(g.Key.Winner)} instead of the version from {D(g.Key.Loser)}";
            if (example is not null && !fg.All(x => IsMinor(x.f))) line += $", so this change from {D(g.Key.Loser)} does not happen: it {example.Text}";
            if (fg.All(x => IsMinor(x.f))) line += ". The difference is only list order, letter case or undocumented fields, so this probably has no visible effect. No action needed.";
            else line += fwd == n ? ". A patch can restore these." : fwd == 0 ? ". Both plugins changed this, so it is a choice." : $". {fwd} can be restored by a patch; the rest are a choice.";
            lines.Add(line);
        }
        return lines;
    }

    // What differs between the lost value and the one in game, worded from the game's side.
    public static List<Difference> Differences(string field, object? lost, object? inGame, Func<IFormLinkGetter, string> name, Action? unreadable = null) =>
        Raw(field, lost, inGame, name, unreadable).Select(d => new Difference(d.Kind switch
        {
            "changed" => $"{d.Path} is {d.To} in game instead of {d.From}{(d.Minor ? " (an undocumented field)" : "")}",
            "removed" => $"{d.Path} is missing {d.From} in game",
            "added" => $"{d.Path} has an extra {(d.To.StartsWith("an ") ? d.To[3..] : d.To)} in game",   // not "an extra an entry" (0.5.1)
            "reordered" => $"{d.Path} has the same entries in a different order",
            "case" => $"{d.Path} differs only in letter case",
            "undoc" => $"{d.Path} differs only in undocumented fields",
            _ => $"{d.Path} has {d.To} entries in game instead of {d.From}",
        }, d.Minor)).ToList();

    // What a plugin changed relative to the base game, worded as actions.
    public static List<Difference> Changes(string field, object? from, object? to, Func<IFormLinkGetter, string> name, Action? unreadable = null) =>
        Raw(field, from, to, name, unreadable).Select(d => new Difference(d.Kind switch
        {
            "changed" => $"changes {d.Path} from {d.From} to {d.To}{(d.Minor ? " (an undocumented field)" : "")}",
            "removed" => $"removes {d.From} from {d.Path}",
            "added" => $"adds {d.To} to {d.Path}",
            "reordered" => $"reorders {d.Path} (same entries)",
            "case" => $"changes only the letter case of {d.Path}",
            "undoc" => $"changes only undocumented fields of {d.Path}",
            _ => $"changes {d.Path} from {d.From} to {d.To} entries",
        }, d.Minor)).ToList();

    // A readable rendering of a whole value ("3 entries: A; B; C").
    public static string Describe(object? v, Func<IFormLinkGetter, string> name, int depth = 0)
    {
        if (v is null) return "not set";
        if (v is Mutagen.Bethesda.Starfield.ICellGridGetter g) return $"point {g.Point}, land flags {SfModCheck.Overrides.LandFlagsText(g)}";   // without the junk bytes (0.5.0)
        if (IsLeaf(v)) return Plain(v, name);
        if (v is IEnumerable e)
        {
            var items = e.Cast<object?>().ToList();
            if (items.Count == 0) return "empty";
            var shown = items.Take(6).Select(i => Describe(i, name, depth + 1));
            return $"{items.Count} {(items.Count == 1 ? "entry" : "entries")}: " + string.Join("; ", shown) + (items.Count > 6 ? $"; and {items.Count - 6} more" : "");
        }
        if (depth >= 3) return TypeLabel(v);
        var parts = new List<string>();
        foreach (var p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.Name is "StaticRegistration" or "Registration" || p.Name.StartsWith("Unknown")) continue;
            object? pv;
            try { pv = p.GetValue(v); } catch (Exception err) when (Fatal.Recoverable(err)) { continue; }
            if (FieldCompare.IsEmpty(pv)) continue;
            parts.Add($"{Words(p.Name)} {Describe(pv, name, depth + 1)}");
            if (parts.Count >= 5) break;
        }
        return parts.Count == 0 ? TypeLabel(v) : string.Join(", ", parts);
    }

    // unreadable: called when a part of the value can't be read during the walk, so the caller can count it under
    // "Tool limits hit" instead of the part silently dropping out of the text (0.5.1). Kept in Overrides.OnUnreadable,
    // which LandFlagsText also calls; without one given, the caller's own (if any) stays in force.
    static List<RawDiff> Raw(string field, object? a, object? b, Func<IFormLinkGetter, string> name, Action? unreadable)
    {
        var res = new List<RawDiff>();
        var outer = SfModCheck.Overrides.OnUnreadable;
        SfModCheck.Overrides.OnUnreadable = unreadable ?? outer;
        try { Walk(a, b, Noun(field).Replace(' ', '_'), 0, res, name); }
        finally { SfModCheck.Overrides.OnUnreadable = outer; }
        return res;
    }

    static void Walk(object? a, object? b, string path, int depth, List<RawDiff> res, Func<IFormLinkGetter, string> name)
    {
        if (res.Count >= 6 || FieldCompare.DeepEq(a, b, 0)) return;
        // An unset list next to a filled one reads as "adds/removes entries", not "changes from not set to ...".
        if (a is null && b is IEnumerable && !IsLeaf(b)) a = Array.Empty<object>();
        if (b is null && a is IEnumerable && !IsLeaf(a)) b = Array.Empty<object>();
        // Localized strings are enumerable (language -> text) but read as one value.
        if (a is IEnumerable ea && b is IEnumerable eb && !IsLeaf(a) && !IsLeaf(b))
        {
            var la = ea.Cast<object?>().ToList(); var lb = eb.Cast<object?>().ToList();
            // Same entries in the same order once undocumented fields are ignored (condition padding, for example).
            if (la.Count == lb.Count && la.Zip(lb).All(z => FieldCompare.DeepEq(z.First, z.Second, 0, ignoreUnknown: true)))
            {
                res.Add(new(Label(path), "undoc", "", "", true));
                return;
            }
            List<object?> missing, extra;
            if (Math.Max(la.Count, lb.Count) <= LongList)
            {
                missing = la.Where(x => !lb.Any(y => FieldCompare.DeepEq(x, y, 0))).ToList();
                extra = lb.Where(y => !la.Any(x => FieldCompare.DeepEq(x, y, 0))).ToList();
            }
            else
            {
                // Same rule, but each entry is only compared with the entries that have the same hash.
                var (ha, hb) = (Hashes(ea, la), Hashes(eb, lb));
                missing = NotIn(la, ha, lb, hb); extra = NotIn(lb, hb, la, ha);
            }
            if (missing.Count == 0 && extra.Count == 0) { res.Add(new(Label(path), "reordered", "", "", true)); return; }
            // Same length with a few entries changed in place: describe each entry's change; otherwise list what is
            // missing or extra. A list that was also reordered is paired by entry name below (0.5.1): index by index,
            // every shifted entry read as "changed".
            if (la.Count == lb.Count && missing.Count <= 2 && ChangedInPlace(la, lb) <= 2)
            {
                for (int i = 0; i < la.Count; i++) Walk(la[i], lb[i], $"{path} {ItemName(la[i], i, name)}", depth + 1, res, name);
                return;
            }
            // An entry removed and re-added under the same name (stage 415, a script) was edited: describe the edit.
            // Extra entries' names are worked out once (they were recomputed for every missing entry before).
            var extraNames = extra.Select(x => ItemName(x, -1, name)).ToList();
            foreach (var m in missing.ToList())
            {
                var key = ItemName(m, -1, name);
                var at = extraNames.IndexOf(key);
                var twin = at < 0 ? null : extra[at];
                if (twin is null || key.StartsWith('(')) continue;
                missing.Remove(m);
                var gone = extra.IndexOf(twin);  // List.Remove's choice (first Equals match), as before
                extra.RemoveAt(gone); extraNames.RemoveAt(gone);
                Walk(m, twin, $"{path} {key}", depth + 1, res, name);
            }
            foreach (var m in missing.Take(3)) res.Add(new(Label(path), "removed", Entry(m, name), "", false));
            foreach (var x in extra.Take(3)) res.Add(new(Label(path), "added", "", Entry(x, name), false));
            if (missing.Count + extra.Count > 6 && la.Count != lb.Count) res.Add(new(Label(path), "count", la.Count.ToString(), lb.Count.ToString(), false));
            return;
        }
        if (a is null || b is null || IsLeaf(a) || depth >= 4 || a.GetType() != b.GetType())
        {
            if (FieldCompare.IsEmpty(a) && FieldCompare.IsEmpty(b)) return;  // "" vs not set: no real change
            // Script names and paths are case-insensitive, so a letter-case-only difference changes nothing.
            if (a is string sa && b is string sb && sa.Equals(sb, StringComparison.OrdinalIgnoreCase))
            {
                res.Add(new(Label(path), "case", "", "", true));
                return;
            }
            // Top-level field paths come through Noun() in lower case ("unknown"), nested ones keep Mutagen's case.
            var minor = path.Split(' ').Any(t => t.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase));
            // long byte blobs of the same length would both read "N bytes of data": show where they differ (code review)
            var (from, to) = a is not null && b is not null && BlobPair(a, b) is { } pair ? pair : (Describe(a, name, 1), Describe(b, name, 1));
            res.Add(new(Label(path), "changed", from, to, minor));
            return;
        }
        foreach (var p in a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.Name is "StaticRegistration" or "Registration") continue;
            if (p.Name == "Flags" && a is Mutagen.Bethesda.Starfield.ICellGridGetter ga && b is Mutagen.Bethesda.Starfield.ICellGridGetter gb)
            {   // XCLC: only the land-flag byte, read typed (0.5.0)
                uint fa, fb;
                try { (fa, fb) = (SfModCheck.Overrides.LandFlags(ga), SfModCheck.Overrides.LandFlags(gb)); } catch (Exception e) when (Fatal.Recoverable(e)) { SfModCheck.Overrides.OnUnreadable?.Invoke(); continue; }
                if (fa != fb) res.Add(new(Label($"{path} land flags"), "changed", $"0x{fa:X2}", $"0x{fb:X2}", false));
                continue;
            }
            object? va, vb;
            try { va = p.GetValue(a); vb = p.GetValue(b); } catch (Exception e) when (Fatal.Recoverable(e)) { SfModCheck.Overrides.OnUnreadable?.Invoke(); continue; }
            Walk(va, vb, $"{path} {p.Name}", depth + 1, res, name);
        }
    }

    // Lists longer than this find their missing and extra entries through hash groups. Comparing every entry with every
    // other one never finished on the navmesh info map (CHANGELOG.md, 0.5.0). Checked on 2026-11-16 by forcing every
    // list over 5 entries through this path: the same findings and the same text, word for word, on two load orders.
    const int LongList = 500;

    // Entry hashes per list object, worked out once: each version's field value is passed in as the same object every
    // time, while the entries read from it are new objects on every read.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, int[]> HashCache = new();
    static int[] Hashes(IEnumerable list, List<object?> items) =>
        HashCache.GetValue(list, _ => items.Select(x => FieldCompare.DeepHash(x, 0)).ToArray());

    // Entries of a with no DeepEq match in b, as the plain rule finds them; only entries with the same hash are compared.
    // DeepEq-equal entries share a hash (DeepHash walks the same way), unless a type's own Equals calls two entries
    // equal while their contents differ; such an entry would be listed as missing, never hidden.
    static List<object?> NotIn(List<object?> a, int[] ha, List<object?> b, int[] hb)
    {
        var groups = new Dictionary<int, List<int>>();
        for (int j = 0; j < b.Count; j++)
        {
            if (!groups.TryGetValue(hb[j], out var g)) groups[hb[j]] = g = new();
            g.Add(j);
        }
        var res = new List<object?>();
        for (int i = 0; i < a.Count; i++)
            if (!(groups.TryGetValue(ha[i], out var g) && g.Any(j => FieldCompare.DeepEq(a[i], b[j], 0)))) res.Add(a[i]);
        return res;
    }

    // How many positions hold different entries in two lists of the same length (counting stops at 3).
    static int ChangedInPlace(List<object?> a, List<object?> b)
    {
        int n = 0;
        for (int i = 0; i < a.Count && n < 3; i++) if (!FieldCompare.DeepEq(a[i], b[i], 0)) n++;
        return n;
    }

    static string Entry(object? item, Func<IFormLinkGetter, string> name)
    {
        var n = ItemName(item, -1, name);
        return n.StartsWith('(') ? $"an entry {n}" : $"entry {n}";
    }

    // Byte blobs are one value too (0.5.1): byte[] was walked byte by byte, as boxed entries; the slice types were
    // already leaves (value types).
    static bool IsLeaf(object v) => v is string or IFormLinkGetter or ITranslatedStringGetter or byte[] || v.GetType().IsValueType;

    // A byte blob as text: its bytes in hex when short ("01 00 00 00"), else its length; null when v isn't a blob.
    static string? BlobText(object v)
    {
        if (!SfModCheck.Overrides.TryBlob(v, out var s)) return null;
        return Short(s) ?? $"{s.Length} bytes of data";
    }

    // A blob of 16 bytes or fewer in full ("no bytes", or its hex); null for a longer one.
    static string? Short(ReadOnlySpan<byte> s) => s.Length == 0 ? "no bytes" : s.Length <= 16 ? Hex(s) : null;

    static string Hex(ReadOnlySpan<byte> s) => BitConverter.ToString(s.ToArray()).Replace('-', ' ');

    // Two byte blobs, one of them over 16 bytes, as "N bytes (from byte K: 01 02 …)": the length alone reads the same
    // for both. Null when they aren't both blobs or are both short (BlobText shows those in full).
    static (string From, string To)? BlobPair(object a, object b)
    {
        if (!SfModCheck.Overrides.TryBlob(a, out var sa) || !SfModCheck.Overrides.TryBlob(b, out var sb) || (sa.Length <= 16 && sb.Length <= 16)) return null;
        int at = 0;
        while (at < sa.Length && at < sb.Length && sa[at] == sb[at]) at++;
        return (Part(sa, at), Part(sb, at));
    }

    static string Part(ReadOnlySpan<byte> s, int at) => Short(s) ??   // a short one in full, as BlobText
        $"{s.Length} bytes of data" + (at < s.Length ? $" (from byte {at}: {Hex(s.Slice(at, Math.Min(8, s.Length - at)))}{(s.Length - at > 8 ? " …" : "")})" : "");

    static string TypeLabel(object v) => Words(v.GetType().Name.Replace("BinaryOverlay", ""));

    // " MapMarker Name" -> "map marker name"; quoted names, FormKeys and parenthesized entries keep their case.
    static string Label(string path)
    {
        var parts = path.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.StartsWith('"') || t.StartsWith('#') || t.StartsWith('(') || t.Contains(':') || t.EndsWith(')') ? t : Words(t).Replace('_', ' '));
        // Drop repeated words ("scripts scripts" when the field and its list share a name).
        var words = string.Join(" ", parts).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = string.Join(" ", words.Where((w, i) => i == 0 || !w.Equals(words[i - 1], StringComparison.OrdinalIgnoreCase)));
        return s.Length > 0 ? s : "the value";
    }

    // Names list entries by their most telling property (script name, stage, actor...), else a short description.
    static string ItemName(object? item, int index, Func<IFormLinkGetter, string> name)
    {
        if (item is null) return index >= 0 ? $"#{index + 1}" : "(empty)";
        foreach (var prop in new[] { "Name", "EditorID", "Stage", "Index", "ScriptName", "Actor", "Ref", "Reference", "Marker", "Target", "Item", "Keyword" })
        {
            var p = item.GetType().GetProperty(prop);
            object? v;
            try { v = p?.GetValue(item); } catch (Exception e) when (Fatal.Recoverable(e)) { continue; }
            if (v is string s && s.Length > 0) return $"\"{OneLine(s)}\"";
            if (v is IFormLinkGetter fl && !fl.IsNull) return $"({Words(prop)} {OneLine(name(fl))})";
            if (v is not null && v.GetType().IsPrimitive) return $"{prop} {v}";
        }
        var d = Describe(item, name, 1);
        return d.Length > 120 ? $"({d[..120]}…)" : $"({d})";
    }

    // Text from a plugin stays on one line in the reports, so a crafted value can't start a new Markdown line or list
    // item (0.5.0 security review); control characters and Unicode line breaks become spaces.
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(s))]
    public static string? OneLine(string? s) => s is null ? null :
        string.Concat(s.Select(c => char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c));

    static string Plain(object? v, Func<IFormLinkGetter, string> name) => v switch
    {
        null => "not set",
        IFormLinkGetter fl => fl.IsNull ? "not set" : OneLine(name(fl)),
        ITranslatedStringGetter ts => $"\"{OneLine(ts.String)}\"",
        string s => $"\"{OneLine(s)}\"",
        _ when BlobText(v) is { } blob => blob,
        IEnumerable e => $"a list of {e.Cast<object?>().Count()}",
        _ when v.GetType().IsValueType => v.ToString() ?? "",
        _ => TypeLabel(v),
    };
}
