// 0.26.0: every record of one plugin, read through Mutagen with conflict's per-group fallback (moved from
// ConflictAnalyzer.Run so overrides reads plugins the same way). A plugin that reads cleanly takes one pass; otherwise
// each top-level group is read on its own and only the unreadable group is skipped (records read before the failure
// stay: callers must tolerate seeing a record twice).
using Mutagen.Bethesda.Plugins.Records;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class RecordWalk
{
    /// <summary>A top-level group property (Quests, ArmorAddons) as the record type it holds ("quest", "armor addon").</summary>
    public static string GroupName(System.Reflection.PropertyInfo prop)
    {
        var t = prop.PropertyType.IsGenericType ? prop.PropertyType.GetGenericArguments()[0] : null;
        return t is not null && typeof(IMajorRecordGetter).IsAssignableFrom(t) ? Impact.TypeName(Analyzer.TypeShort(t)) : prop.Name;
    }

    /// <summary>Calls onRecord for each record of p. When the single pass fails, each group is read on its own and
    /// onProblem gets the group's name ("quest") and the exception for each group that can't be read. True when the
    /// single pass read the plugin cleanly; false when the per-group fallback ran.</summary>
    public static bool Each(PluginInfo p, Action<IMajorRecordGetter> onRecord, Action<string, Exception> onProblem)
    {
        try
        {
            foreach (var rec in p.Mod.EnumerateMajorRecords()) onRecord(rec);
            return true;
        }
        catch (Exception err) when (Fatal.Recoverable(err))
        {
            Log.Write($"{p.FileName}: one pass over its records failed ({err.GetType().Name}); reading each group on its own");
            foreach (var prop in p.Mod.GetType().GetProperties())
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                var what = GroupName(prop);
                object? val;
                try { val = prop.GetValue(p.Mod); }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    if (typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(prop.PropertyType)) onProblem(what, e);
                    continue;
                }
                if (val is not IMajorRecordGetterEnumerable group || val is IMajorRecordGetter || val is Mutagen.Bethesda.Plugins.Records.IModGetter) continue;
                try { foreach (var rec in group.EnumerateMajorRecords()) onRecord(rec); }
                catch (Exception e) when (Fatal.Recoverable(e)) { onProblem(what, e); }
            }
            return false;
        }
    }
}
