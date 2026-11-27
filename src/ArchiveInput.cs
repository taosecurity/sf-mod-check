// Archive input (0.26.0): a mod archive (.zip, .7z or .rar) given to --plugin or --extra-data is unpacked into the
// run's own temp folder with triage's guarded unpacker (ReleaseCommand.Unpack for a zip, TriageCommand.UnpackOther for
// a 7z or rar), so every guard comes along: no entry outside the folder, no name twice, the size and entry caps, no
// encrypted archive. Commands that read only plugins have only the plugins written; every entry is still checked.
// LoadOrderInput owns the temp folder and deletes it when the command ends.
namespace SfModCheck;

public static class ArchiveInput
{
    /// <summary>What LoadOrderInput does with an archive given to --plugin or --extra-data.</summary>
    public enum Use
    {
        /// <summary>Refused with "needs the mod installed or unpacked" (patch, voice-lines).</summary>
        Refuse,
        /// <summary>Unpacked, plugins only (find, lines, scenes, overrides, summary).</summary>
        PluginsOnly,
        /// <summary>Unpacked in full (conflict: it reads the archive's textures, sounds and BA2s too).</summary>
        Everything,
    }

    /// <summary>The plugin folder an archive unpacked to, and its plugins' file names (sorted, letter case ignored).</summary>
    public sealed record Unpacked(string Archive, string Folder, List<string> Plugins);

    /// <summary>True for a file named .zip, .7z or .rar (any case) that isn't a folder.</summary>
    public static bool IsArchive(string path) =>
        TriageCommand.KindOf(path) is TriageInputKind.Zip or TriageInputKind.SevenZip or TriageInputKind.Rar && !Directory.Exists(path);

    /// <summary>True for a plugin's name (.esm, .esp or .esl, any case; a folder part is allowed).</summary>
    public static bool IsPluginName(string name) => TriageCommand.KindOf(name.TrimEnd('/', '\\')) is TriageInputKind.Plugin;

    /// <summary>Unpacks the archive into dest (a new folder of the run's temp folder) and finds its plugin folder.
    /// Null, with the reason in why, when the archive can't be used: a guard refused it, it can't be read, it holds no
    /// plugin, or its plugins sit in more than one folder.</summary>
    public static Unpacked? Open(string archive, string dest, Use use, out string why)
    {
        why = "";
        var kind = TriageCommand.KindOf(archive)!.Value;
        var shown = SafeText.OneLine(Path.GetFileName(archive));
        Log.Write($"archive input: {shown} ({kind}, {(use == Use.Everything ? "every file" : "plugins only")}) into {dest}");
        try
        {
            var (_, files) = TriageCommand.Gather(kind, archive, dest, write: use == Use.Everything ? null : IsPluginName);
            Log.Write($"archive input: {shown}: every entry checked, {files.Count} file{(files.Count == 1 ? "" : "s")} written");
        }
        catch (Exception e) when (TriageCommand.IsFileSystem(e) || e is InvalidDataException or SharpCompress.Common.InvalidFormatException
                                      or SharpCompress.Common.ArchiveException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"archive input: {shown} refused or unreadable: {e}");
            why = SafeText.OneLine(e.Message);
            return null;
        }
        // the plugins at any depth (the top, Data\ or <ModName>\); the unpacker made every file, so there are no links
        var plugins = Directory.EnumerateFiles(dest, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(f => IsPluginName(Path.GetFileName(f))).ToList();
        if (plugins.Count == 0)
        {
            why = "it holds no plugin (.esm, .esp or .esl)";
            return null;
        }
        var folders = plugins.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.Ordinal).ToList();
        if (folders.Count > 1)
        {
            var list = string.Join(", ", folders.Select(f => SafeText.OneLine(Path.GetRelativePath(dest, f) is "." ? "the top folder" : Path.GetRelativePath(dest, f))));
            why = $"its plugins sit in {folders.Count} folders ({list}), as in an installer with options (such as a FOMOD). " +
                  "Unpack it and give the folder you would install, or the plugin itself";
            return null;
        }
        var names = plugins.Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Log.Write($"archive input: {shown}: plugin folder {folders[0]}: {string.Join(", ", names.Select(SafeText.OneLine))}");
        return new(archive, folders[0], names);
    }
}
