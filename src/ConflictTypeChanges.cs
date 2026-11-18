// conflict: overrides that give a record another type (0.5.2). Until 0.5.1 such an override was skipped without a word:
// the analyzer reads each record type on its own, so the original record of the other type was never collected and the
// record had fewer than two versions. `check` reports the same thing for one plugin (its links ERROR "overrides ...,
// which is a CELL").
//
// Found by reading record headers straight from the files (RawWalker, as check's links rule does), not through Mutagen:
// only the type and FormID of each record are read, so memory stays flat on a large load order. Only the plugins that
// can matter are read: every mod, and every official file that defines an overridden record.
using Mutagen.Bethesda.Plugins;

namespace SfModCheck.Conflict;

/// <summary>First: the plugin that loads first with this FormID (whose record the game keeps), and its record type.
/// Plugin: a later mod whose record of the same FormID has another type. EditorID: that mod's record's, if it has one.</summary>
public record TypeChange(FormKey FormKey, string First, string FirstType, string Plugin, string PluginType, string? EditorID)
{
    /// <summary>"record type differs between plugins: A.esm has a CELL, B.esm an ARMO" (the finding, in every report).</summary>
    public string Text => $"record type differs between plugins: {First} has {Impact.WithArticle(FirstType)}, {Plugin} {Impact.WithArticle(PluginType)}";
}

public static class TypeChanges
{
    /// <summary>What the game does with such a record (Starfield 1.16.244, read in a disassembler, 2026-11-17): when a
    /// record's FormID already belongs to a loaded form of another type, the record is refused, silently.</summary>
    public const string Hint =
        "The game keeps whichever record loads first and silently drops the other (armor and armor addon records are the " +
        "exception: they may replace each other); for placed objects in temporary groups, the object may vanish when its " +
        "cell loads. Usually the plugin meant to add a new record and has a wrong master index or FormID: give the record " +
        "its own FormID (xEdit: right-click > Change FormID) or remove it.";

    /// <summary>Every later mod record whose type differs from the first record with the same FormID, in load order.
    /// targets: the records mods override (FormKey and the first override's Mutagen type). error: a plugin that couldn't
    /// be read for this: (plugin, a line of its own, a short form the caller can add to the plugin's own read-problem line).
    /// Every plugin passed to error was read only partly or not at all, so a record of it may be missing here.</summary>
    public static List<TypeChange> Find(IReadOnlyList<PluginInfo> plugins, IReadOnlyDictionary<FormKey, Type> targets, Action<PluginInfo, string, string> error)
    {
        var changes = new List<TypeChange>();
        if (targets.Count == 0) return changes;
        // Each file's style (full, medium, light) decides how its FormIDs are written and how its local IDs are masked.
        // Style and masters come from Mutagen, which opened the plugin for the comparison, so both passes read the same
        // master list (code review: a damaged header size gave the raw header reader no masters, with no error).
        var styleOf = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        var masters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var modKeys = new Dictionary<string, ModKey>(StringComparer.OrdinalIgnoreCase);
        var headerError = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // One wording for every problem: the line of its own and the short form share their parts (0.5.3).
        void Problem(PluginInfo p, string subject, string cause, string effect) =>
            error(p, $"{p.FileName}: {subject} could not be read ({cause}), so {effect}", $"{subject} also could not be read ({cause}), so {effect}");
        foreach (var p in plugins)
        {
            modKeys[p.FileName] = p.Mod.ModKey;
            try
            {
                var style = ((Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter)p.Mod).MasterStyle switch
                {
                    MasterStyle.Small => Style.Light, MasterStyle.Medium => Style.Medium, _ => Style.Full,
                };
                var ms = p.Mod.MasterReferences.Select(m => m.Master.FileName.String).ToList();
                styleOf[p.FileName] = style;   // both or neither (code review 0.5.3)
                masters[p.FileName] = ms;
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { headerError[p.FileName] = Impact.Summary(e); }   // reported below, when its records are read
        }
        var origins = targets.Keys.Select(k => k.ModKey).ToHashSet();
        var first = new Dictionary<FormKey, (PluginInfo Plugin, string Sig)>();
        var sigs = new Dictionary<string, string>(StringComparer.Ordinal);   // one string per record type, not per record
        foreach (var p in plugins)
        {
            if (p.Official && !origins.Contains(p.Mod.ModKey)) continue;
            if (!masters.TryGetValue(p.FileName, out var ms))
            {
                Problem(p, "its file header", headerError.GetValueOrDefault(p.FileName, "no reason given"), "its records' types were not compared");
                continue;
            }
            try
            {
                using var f = new RawFile(p.Path);
                var map = new IdMap(p.FileName, styleOf[p.FileName], ms.Select(m => (m, styleOf.GetValueOrDefault(m, Style.Full))));
                var w = new RawWalker(f, deep: false, (_, _, _) => { })
                {
                    OnRecord = rec =>
                    {
                        var (file, local, _) = map.Decode(rec.FormId);
                        if (file is null || !modKeys.TryGetValue(file, out var mk)) return;
                        var fk = new FormKey(mk, IdMap.Mask(local, styleOf.GetValueOrDefault(file, Style.Full)));
                        if (!targets.ContainsKey(fk)) return;
                        if (!sigs.TryGetValue(rec.Sig, out var sig)) sigs[rec.Sig] = sig = rec.Sig;
                        if (!first.TryGetValue(fk, out var had)) { first[fk] = (p, sig); return; }
                        if (ReferenceEquals(had.Plugin, p) || had.Sig == sig || p.Official || ArmorPair(had.Sig, sig)) return;
                        changes.Add(new(fk, had.Plugin.FileName, had.Sig, p.FileName, sig, EditorId(f, rec)));
                    },
                };
                w.Walk();
                // a damaged spot ends the walk of its file or group, so later records were never seen (code review)
                if (w.StoppedEarly is { } why)
                    Problem(p, "some of its records' types", Impact.OneLine(why), "a record type it changes there may be missing");
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Problem(p, "its records' types", Impact.Summary(e), "a record type it changes may be missing");
            }
        }
        return changes;
    }

    /// <summary>Armor (ARMO) and armor addon (ARMA) records may replace each other in the game, so a mod that swaps one
    /// for the other isn't dropped and isn't listed (code review: the finding said "dropped" next to a hint saying not).</summary>
    public static bool ArmorPair(string a, string b) => (a, b) is ("ARMO", "ARMA") or ("ARMA", "ARMO");

    /// <summary>A record's EditorID read from its EDID field, or null (none, or the record can't be read).</summary>
    static string? EditorId(RawFile f, RawRecord rec)
    {
        try
        {
            if (rec.Size > 64 * 1024 * 1024) return null;
            ReadOnlySpan<byte> body = f.Span(rec.Offset + 24, (int)rec.Size);
            if ((rec.Flags & Flags.Compressed) != 0)
            {
                if (Subrecords.Decompress(body, out _) is not { } d) return null;
                body = d;
            }
            foreach (var (type, data) in Subrecords.Parse(body, out _))
                if (type == "EDID") return Impact.OneLine(System.Text.Encoding.UTF8.GetString(data).TrimEnd('\0'));
            return null;
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
    }
}
