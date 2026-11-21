// 0.9.1: the follow-ups left from 0.8.0 (NOTES-triage.md, "Review rounds and follow-ups"): a cap on archive entries,
// Starfield.esm found ignoring case on Linux in check, release and conflict, links never followed beside a plugin and
// named as skipped, two files whose names differ only in case, LZ4 entries in the Scripts section, HResults that
// aren't Win32 codes, and one answer for "is the game there" in triage.
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using SharpCompress.Writers.SevenZip;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

[Collection(ReleaseStatics.Name)]   // changes ReleaseCommand.MaxEntries
public class FollowUps091Tests
{
    static string MakeZip(Sandbox s, string name, params (string Path, string Text)[] entries)
    {
        var zip = Path.Combine(s.Sub("zips"), name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, t) in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(p).Open());
            w.Write(t);
        }
        return zip;
    }

    static string Make7z(Sandbox s, string name, params (string Path, string Text)[] entries)
    {
        var path = Path.Combine(s.Sub("zips"), name);
        using (var f = File.Create(path))
        using (var w = new SevenZipWriter(f, new SevenZipWriterOptions()))
            foreach (var (p, t) in entries)
                w.Write(p, new MemoryStream(Encoding.UTF8.GetBytes(t)), null);
        return path;
    }

    static (string Path, string Text)[] Five => [("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"), ("d.txt", "4"), ("e.txt", "5")];

    [Fact]
    public void An_archive_with_more_entries_than_the_cap_is_refused()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Many.zip", Five);
        var sevenZip = Make7z(s, "Many.7z", Five);
        var old = ReleaseCommand.MaxEntries;
        ReleaseCommand.MaxEntries = 4;
        try
        {
            var z = Assert.Throws<InvalidDataException>(() => ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "z")));
            Assert.Contains("it holds 5 entries, more than the 4 this check allows", z.Message);
            Assert.Contains("unpack it yourself and run sf-mod-check triage on the unpacked folder", z.Message);   // the fix (code review of 0.9.1)
            Assert.False(Directory.Exists(Path.Combine(s.Dir, "z", "a.txt")) || File.Exists(Path.Combine(s.Dir, "z", "a.txt")));   // refused before anything is written
            var x = Assert.Throws<InvalidDataException>(() => TriageCommand.UnpackOther(sevenZip, Path.Combine(s.Dir, "x")));
            Assert.True(TriageCommand.IsRefusal(x));   // the guard's own words, not "can't be read"
            Assert.Contains("more than the 4 this check allows", x.Message);
            Assert.False(File.Exists(Path.Combine(s.Dir, "x", "a.txt")));
            // at the cap is fine
            ReleaseCommand.MaxEntries = 5;
            Assert.Equal(5, ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "z2")).Files.Count);
            Assert.Equal(5, TriageCommand.UnpackOther(sevenZip, Path.Combine(s.Dir, "x2")).Count);
        }
        finally { ReleaseCommand.MaxEntries = old; }
        Assert.Equal(200_000, ReleaseCommand.MaxEntries);
    }

    [Fact]
    public void The_zip_unpacker_names_a_file_and_folder_clash()
    {
        // listed as open after 0.8.0; fixed in its seventeenth review round (ReleaseCommand.NameClash). Proof both ways round.
        using var s = new Sandbox();
        var fileFirst = MakeZip(s, "FileFirst.zip", ("Data", "x"), ("Data/b.txt", "y"));
        var e1 = Assert.Throws<InvalidDataException>(() => ReleaseCommand.Unpack(fileFirst, Path.Combine(s.Dir, "a")));
        Assert.Contains("\"Data/b.txt\" sits in a folder that the archive also holds as a file", e1.Message);
        var folderFirst = MakeZip(s, "FolderFirst.zip", ("Data/b.txt", "y"), ("Data", "x"));
        var e2 = Assert.Throws<InvalidDataException>(() => ReleaseCommand.Unpack(folderFirst, Path.Combine(s.Dir, "b")));
        Assert.Contains("\"Data\" has the same name as a folder", e2.Message);
    }

    [Fact]
    public void Check_and_conflict_find_a_lower_case_starfield_esm()
    {
        // On Linux a Data folder can hold "starfield.esm"; the "no Starfield.esm" note must not appear (0.8.0 follow-up).
        // On Windows names ignore case anyway, so this passes there as well; the control proves the note is real.
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var none = s.Sub("NoGame");
        File.WriteAllText(Path.Combine(data, "starfield.esm"), "");
        Assert.True(TriageCommand.HasStarfield(data));
        var missing = Path.Combine(s.Dir, "no-such-Plugins.txt");
        var (_, _, withGame) = ExeTests.RunSplit("conflict", "--data", data, "--plugins", missing);
        Assert.DoesNotContain("no Starfield.esm", withGame);
        var (_, _, without) = ExeTests.RunSplit("conflict", "--data", none, "--plugins", missing);
        Assert.Contains("no Starfield.esm", without);
    }

    /// <summary>Makes a link, or returns false where this account can't (Windows without Developer Mode can't make
    /// symlinks; a folder junction needs no rights).</summary>
    static bool TryLink(string link, string target, bool folder)
    {
        try
        {
            if (folder && OperatingSystem.IsWindows())
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                p.WaitForExit();
                return p.ExitCode == 0 && Directory.Exists(link);
            }
            if (folder) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void A_folder_input_lists_its_links_as_skipped_and_never_goes_into_them()
    {
        using var s = new Sandbox();
        var root = s.Sub("mod");
        File.WriteAllText(Path.Combine(root, "Mod.esm"), "x");
        var outside = s.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "secret.pex"), "decoy");
        Assert.True(TryLink(Path.Combine(root, "Scripts"), outside, folder: true), "a folder link (junction or symlink) couldn't be made");
        var fileLink = TryLink(Path.Combine(root, "Linked.esm"), Path.Combine(outside, "secret.pex"), folder: false);

        var skipped = new List<(string File, string Why)>();
        var (_, files) = TriageCommand.Gather(TriageInputKind.Folder, root, "unused", skipped);
        Assert.Equal(["mod.esm"], files);   // nothing from inside the linked folder, nor the linked file
        Assert.Contains(skipped, x => x.File == "scripts" && x.Why.StartsWith("skipped: it is a link"));
        if (fileLink) Assert.Contains(skipped, x => x.File == "linked.esm");
        Assert.Equal(fileLink ? 2 : 1, skipped.Count);

        // and the report says so, in the Files section
        var c = Context(root, files, skipped);
        var f = TriageFiles.Run(c).ToList();
        Assert.Contains(f, x => x.Level == TriageLevel.Look && x.Section == TriageSections.Files && x.File == "scripts" && x.Text.Contains("never follows links"));
    }

    [Fact]
    public void A_linked_archive_beside_a_plugin_is_skipped_on_every_system()
    {
        using var s = new Sandbox();
        var dir = s.Sub("data");
        File.WriteAllText(Path.Combine(dir, "Foo.esm"), "x");
        File.WriteAllText(Path.Combine(dir, "Foo - Main.ba2"), "real");
        var elsewhere = Path.Combine(s.Sub("elsewhere"), "decoy.ba2");
        File.WriteAllText(elsewhere, "decoy");
        if (!TryLink(Path.Combine(dir, "Foo - Textures.ba2"), elsewhere, folder: false))
            return;   // Windows without symlink rights: the Linux CI run covers it
        var skipped = new List<(string File, string Why)>();
        var (_, files) = TriageCommand.Gather(TriageInputKind.Plugin, Path.Combine(dir, "Foo.esm"), "unused", skipped);
        Assert.Equal(["foo - main.ba2", "foo.esm"], files);
        var link = Assert.Single(skipped);
        Assert.Equal("foo - textures.ba2", link.File);
        Assert.Contains("never follows links", link.Why);
    }

    [Fact]
    public void A_file_reached_only_through_a_link_is_named_as_skipped()
    {
        // Full's own lookup refuses links on Linux and macOS (on Windows the folder walk already leaves them out)
        if (OperatingSystem.IsWindows()) return;
        using var s = new Sandbox();
        var root = s.Sub("mod");
        var outside = Path.Combine(s.Sub("elsewhere"), "decoy.esm");
        File.WriteAllText(outside, "decoy");
        File.CreateSymbolicLink(Path.Combine(root, "only.esm"), outside);
        var c = Context(root, ["only.esm"], []);
        Assert.True(c.BehindLink(c.Full("only.esm")));
        var f = TriageFiles.Run(c).ToList();
        var look = Assert.Single(f, x => x.File == "only.esm" && x.Level == TriageLevel.Look);
        Assert.StartsWith("skipped: it is reached only through a link", look.Text);
        Assert.DoesNotContain(f, x => x.Text.Contains(TriageContext.LinkNotFollowed));
    }

    [Fact]
    public void Two_files_whose_names_differ_only_in_case_are_read_once_and_named()
    {
        var skipped = new List<(string File, string Why)>();
        using var s = new Sandbox();
        var root = s.Sub("mod");
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        File.WriteAllText(Path.Combine(root, "Scripts", "a.pex"), "A");
        File.WriteAllText(Path.Combine(root, "b.esm"), "b");
        bool caseSensitive = !File.Exists(Path.Combine(root, "scripts", "a.pex"));
        if (caseSensitive)
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "a.pex"), "a");
        }
        var (_, files) = TriageCommand.Gather(TriageInputKind.Folder, root, "unused", skipped);
        Assert.Equal(["b.esm", "scripts\\a.pex"], files);   // one name each, never the same name twice
        if (caseSensitive)
        {
            // each named by its own spelling: the lower-case one is kept, the other skipped (code review of 0.9.1)
            var clash = Assert.Single(skipped);
            Assert.Equal("Scripts\\a.pex", clash.File);
            Assert.Equal(TriageCommand.CaseWhyFor("scripts\\a.pex"), clash.Why);
            Assert.StartsWith("skipped: it has the same name as scripts\\a.pex apart from upper and lower case", clash.Why);
        }
        else Assert.Empty(skipped);
    }

    [Fact]
    public void Case_twins_are_named_by_their_own_spellings_in_the_look_and_the_table()
    {
        // code review of 0.9.1: on Linux both files showed as the lower-case name, so the report couldn't say which one
        // was read. Neither spelling is all lower case here, so the kept one is the first in ordinal order.
        using var s = new Sandbox();
        var root = s.Sub("mod");
        Directory.CreateDirectory(Path.Combine(root, "SCRIPTS"));
        File.WriteAllText(Path.Combine(root, "SCRIPTS", "a.pex"), "kept");
        if (File.Exists(Path.Combine(root, "Scripts", "A.pex")) || Directory.Exists(Path.Combine(root, "Scripts")))
            return;   // names ignore case here (Windows, macOS's default): two such files can't exist
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        File.WriteAllText(Path.Combine(root, "Scripts", "A.pex"), "skipped");
        File.WriteAllText(Path.Combine(root, "b.esm"), "b");

        var skipped = new List<(string File, string Why)>();
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var (_, files) = TriageCommand.Gather(TriageInputKind.Folder, root, "unused", skipped, kept);
        Assert.Equal(["b.esm", "scripts\\a.pex"], files);
        Assert.Equal("SCRIPTS\\a.pex", kept["scripts\\a.pex"]);
        var twin = Assert.Single(skipped);
        Assert.Equal("Scripts\\A.pex", twin.File);
        Assert.Contains("same name as SCRIPTS\\a.pex", twin.Why);

        var c = new TriageContext
        {
            Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = root, Root = root, Files = files,
            DataPrefix = "", DataDir = Path.Combine(root, "no-game"), Work = root, Report = new TriageReport { InputName = "Mod" },
            Skipped = skipped, CaseKept = kept,
        };
        Assert.Equal("kept", File.ReadAllText(c.Full("scripts\\a.pex")));   // Full reads the kept spelling, not the first match
        var f = TriageFiles.Run(c).ToList();
        var look = Assert.Single(f, x => x.Level == TriageLevel.Look && x.File == "Scripts\\A.pex");
        Assert.Contains("only SCRIPTS\\a.pex is checked", look.Text);
        var every = c.Report.Tables.Single(t => t.Title == "Every file");
        Assert.Equal(["b.esm", "SCRIPTS\\a.pex", "Scripts\\A.pex"], every.Rows.Select(r => r[0]));
        Assert.Equal("4", every.Rows[1][1]);   // "kept" is 4 bytes: the kept file was hashed
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("kept"))).ToLowerInvariant(), every.Rows[1][2]);
        Assert.Equal("same name", every.Rows[2][1]);
    }

    [Fact]
    public void Skipped_links_get_one_neutral_note_in_files_and_scripts_keeps_its_normal_note()
    {
        // simplify round of 0.9.1: the Scripts section gives its normal finding; the Files section says once that linked
        // items weren't read
        using var s = new Sandbox();
        var root = s.Sub("mod");
        File.WriteAllText(Path.Combine(root, "foo.esm"), "x");
        var c = Context(root, ["foo.esm"], [("foo - main.ba2", TriageCommand.LinkWhy), ("scripts", TriageCommand.LinkWhy), ("readme.txt", TriageCommand.LinkWhy)]);
        Assert.Equal("The mod has no compiled scripts (.pex), loose or in its archives.", Assert.Single(TriageScripts.Run(c), x => x.File == "").Text);
        var note = Assert.Single(TriageFiles.Run(c), x => x.Level == TriageLevel.Note && x.Text.Contains("linked item"));
        Assert.Equal(TriageSections.Files, note.Section);
        Assert.Equal("2 linked items were skipped and not read (each is named in this section); any scripts or assets inside them weren't checked. " +
                     "Run triage on what the link points to.", note.Text);   // the .txt can't hold scripts or assets: not counted
        // a file skipped for another reason (here its name's case) gets no such note
        var other = Context(root, ["foo.esm"], [("Foo.esm", TriageCommand.CaseWhyFor("foo.esm"))]);
        Assert.DoesNotContain(TriageFiles.Run(other), x => x.Text.Contains("linked item"));
    }

    [Fact]
    public void A_folder_scan_asks_once_per_reparse_point()
    {
        // code review of 0.9.1: the transform and both predicates each asked, so a reparse point's log line came up to three
        // times. The scan's cache asks once per path, and a plain file never asks.
        using var s = new Sandbox();
        var root = s.Sub("mod");
        var plain = Path.Combine(root, "a.esm");
        File.WriteAllText(plain, "a");
        var outside = s.Sub("outside");
        var link = Path.Combine(root, "Linked");
        Assert.True(TryLink(link, outside, folder: true), "a folder link (junction or symlink) couldn't be made");
        var cache = new TriageCommand.LinkCache();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(cache.Is(link, File.GetAttributes(link)));
            Assert.False(cache.Is(plain, File.GetAttributes(plain)));
        }
        Assert.Equal(1, cache.Queries);
        // and the scan still lists the link once
        var skipped = new List<(string File, string Why)>();
        TriageCommand.Gather(TriageInputKind.Folder, root, "unused", skipped);
        Assert.Single(skipped, x => x.File == "linked");
    }

    [Fact]
    public void Triage_uses_one_answer_for_whether_the_game_is_there()
    {
        // The report header and the sections read the same value (0.8.0 follow-up): a value the run gives wins over
        // what the folder holds now, both ways round.
        using var s = new Sandbox();
        var data = s.Sub("Data");
        File.WriteAllText(Path.Combine(data, "Starfield.esm"), "");
        Assert.False(new TriageContext
        {
            Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = "x", Root = "r", Files = [],
            DataPrefix = "", DataDir = data, Work = "w", Report = new TriageReport(), HasGame = false,
        }.HasGame);
        Assert.True(new TriageContext
        {
            Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = "x", Root = "r", Files = [],
            DataPrefix = "", DataDir = s.Sub("empty"), Work = "w", Report = new TriageReport(), HasGame = true,
        }.HasGame);
        // without one it is worked out from the folder, as before
        Assert.True(new TriageContext
        {
            Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = "x", Root = "r", Files = [],
            DataPrefix = "", DataDir = data, Work = "w", Report = new TriageReport(),
        }.HasGame);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]    // ERROR_SHARING_VIOLATION as .NET makes it
    [InlineData(unchecked((int)0x80070070), true)]    // ERROR_DISK_FULL
    [InlineData(32, true)]                            // a plain Win32 code
    [InlineData(unchecked((int)0x80040020), false)]   // another facility with the same low word
    [InlineData(unchecked((int)0x80131620), false)]   // COR_E_IO, a plain IOException
    public void Only_win32_hresults_count_as_a_problem_of_the_pc_on_windows(int hresult, bool pc)
    {
        Assert.Equal(pc ? hresult & 0xFFFF : -1, TriageCommand.Win32Code(hresult));
        if (OperatingSystem.IsWindows()) Assert.Equal(pc, TriageCommand.IsPcProblem(new IOException("made up", hresult)));
    }

    /// <summary>A general (GNRL) BA2 of version 3 whose header names LZ4 (compression 3); each entry's packed bytes are
    /// given as they are, with the size the record states.</summary>
    static byte[] Ba2Lz4(params (string Name, byte[] Packed, uint Size)[] files)
    {
        long dataAt = 36 + 36L * files.Length;
        long nameAt = dataAt + files.Sum(f => (long)f.Packed.Length);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BTDX")); w.Write(3u); w.Write(Encoding.ASCII.GetBytes("GNRL")); w.Write((uint)files.Length); w.Write((ulong)nameAt);
        w.Write(0UL); w.Write(3u);   // version 3's 12 more bytes; the last 4 name the compression
        long at = dataAt;
        foreach (var f in files)
        {
            w.Write(0u); w.Write(Encoding.ASCII.GetBytes("pex\0")); w.Write(0u); w.Write(0x100100u);
            w.Write((ulong)at); w.Write((uint)f.Packed.Length); w.Write(f.Size); w.Write(0xBAADF00Du);
            at += f.Packed.Length;
        }
        foreach (var f in files) w.Write(f.Packed);
        foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Name); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        return o.ToArray();
    }

    static byte[] Lz4(byte[] data)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var n = LZ4Codec.Encode(data, target);
        return target[..n];
    }

    [Fact]
    public void Lz4_entries_in_an_archive_are_unpacked_for_the_scripts_section()
    {
        using var s = new Sandbox();
        // repeats, so the block holds matches as well as literals
        var script = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ScriptName Foo extends Quest\n", 200)));
        var packed = Lz4(script);
        Assert.True(packed.Length < script.Length / 4);
        var ba2 = Path.Combine(s.Sub("mod"), "Foo - Main.ba2");
        File.WriteAllBytes(ba2, Ba2Lz4(
            ("scripts/foo.pex", packed, (uint)script.Length),
            ("scripts/short.pex", packed, (uint)script.Length - 1),            // says one byte less than it holds
            ("scripts/broken.pex", [0xF0, 0x01, 0x02], 100)));                // a literal run past the end of the data
        var list = TriageScripts.ArchiveScripts(ba2);
        Assert.Equal(3, list.Count);
        var ok = list.Single(x => x.Entry == @"scripts\foo.pex");   // names as Ba2Names.Normalize gives them
        Assert.Null(ok.Error);
        Assert.Equal(script, ok.Data);
        Assert.Contains("unpacks to a different size", list.Single(x => x.Entry == @"scripts\short.pex").Error);
        Assert.Equal("it won't unpack (LZ4: the data is damaged)", list.Single(x => x.Entry == @"scripts\broken.pex").Error);
    }

    // ---- code review of 0.9.1 ----

    [Fact]
    public void Only_real_links_count_as_links_not_every_reparse_point()
    {
        // Windows marks OneDrive placeholders, deduplicated files and app aliases as reparse points too. A path that has
        // the ReparsePoint attribute but no link target is read as a normal file or folder.
        using var s = new Sandbox();
        var root = s.Sub("mod");
        var plain = Path.Combine(root, "Plain.esm");
        File.WriteAllText(plain, "x");
        var folder = s.Sub("mod/Scripts");
        Assert.False(TriageCommand.IsRealLink(plain, File.GetAttributes(plain)));
        // what a OneDrive placeholder looks like to the attribute test alone
        Assert.False(TriageCommand.IsRealLink(plain, File.GetAttributes(plain) | FileAttributes.ReparsePoint));
        Assert.False(TriageCommand.IsRealLink(folder, File.GetAttributes(folder) | FileAttributes.ReparsePoint));

        // a real reparse point that isn't a link, where Windows has one: an app alias (IO_REPARSE_TAG_APPEXECLINK)
        if (OperatingSystem.IsWindows())
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
            var alias = Directory.Exists(apps) ? Directory.EnumerateFiles(apps, "*.exe").FirstOrDefault(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) : null;
            if (alias != null) Assert.False(TriageCommand.IsRealLink(alias, File.GetAttributes(alias)), $"{alias} counted as a link");
        }

        // a real link does count, file and folder (where this account can make one)
        var outside = s.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "decoy.pex"), "decoy");
        var dirLink = Path.Combine(root, "Linked");
        Assert.True(TryLink(dirLink, outside, folder: true), "a folder link (junction or symlink) couldn't be made");
        Assert.True(TriageCommand.IsRealLink(dirLink, File.GetAttributes(dirLink)));
        var fileLink = Path.Combine(root, "Linked.esm");
        if (TryLink(fileLink, Path.Combine(outside, "decoy.pex"), folder: false))
            Assert.True(TriageCommand.IsRealLink(fileLink, File.GetAttributes(fileLink)));

        // and the folder walk keeps the plain files and folders, skipping only the links
        var skipped = new List<(string File, string Why)>();
        File.WriteAllText(Path.Combine(folder, "a.pex"), "a");
        var (_, files) = TriageCommand.Gather(TriageInputKind.Folder, root, "unused", skipped);
        Assert.Equal(["plain.esm", "scripts\\a.pex"], files);
        Assert.All(skipped, x => Assert.Equal(TriageCommand.LinkWhy, x.Why));
        Assert.Contains(skipped, x => x.File == "linked");
    }

    [Fact]
    public void One_name_per_file_does_not_need_sorted_input()
    {
        var skipped = new List<(string File, string Why)>();
        var kept = new Dictionary<string, string>();
        var unique = TriageCommand.OnePerName(["scripts\\b.pex", "a.esm", "scripts\\b.pex", "A.esm"], skipped, kept);
        Assert.Equal(["a.esm", "scripts\\b.pex"], unique);
        // the same spelling twice is one file, not a case twin (simplify round of 0.9.1)
        Assert.Equal([("A.esm", TriageCommand.CaseWhyFor("a.esm"))], skipped);
        Assert.False(kept.ContainsKey("scripts\\b.pex"));
        Assert.All(skipped, x => Assert.StartsWith("skipped: ", x.Why));   // like LinkWhy (code review of 0.9.1)
        Assert.Equal("a.esm", kept["a.esm"]);   // the all-lower-case spelling is the one kept
    }

    [Fact]
    public void Full_reads_the_kept_spelling_on_every_system()
    {
        // simplify round of 0.9.1: on Windows Full built its path from the lower-case name, ignoring CaseKept. Two real
        // twins on Windows need a case-sensitive folder (admin), so this checks the spelling Full gives on each system.
        using var s = new Sandbox();
        var root = s.Sub("mod");
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        File.WriteAllText(Path.Combine(root, "Scripts", "Foo.pex"), "x");
        var c = Context(root, ["scripts\\foo.pex"], []);
        c.CaseKept["scripts\\foo.pex"] = "Scripts\\Foo.pex";
        Assert.Equal(Path.Combine(root, "Scripts", "Foo.pex"), c.Full("scripts\\foo.pex"));
    }

    [Fact]
    public void An_lz4_entry_longer_than_its_record_is_a_size_mismatch_not_damage()
    {
        using var s = new Sandbox();
        var script = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ScriptName Foo extends Quest\n", 200)));
        var packed = Lz4(script);
        var ba2 = Path.Combine(s.Sub("mod"), "Foo - Main.ba2");
        File.WriteAllBytes(ba2, Ba2Lz4(
            ("scripts/long.pex", packed, (uint)script.Length - 100),   // holds 100 bytes more than it says
            ("scripts/short.pex", packed, (uint)script.Length + 100)));  // holds 100 bytes fewer
        var list = TriageScripts.ArchiveScripts(ba2);
        var longer = list.Single(x => x.Entry == @"scripts\long.pex").Error;
        Assert.Equal($"it unpacks to a different size than the {script.Length - 100:N0} bytes Foo - Main.ba2 says ({script.Length:N0} bytes)", longer);
        Assert.Contains("unpacks to a different size", list.Single(x => x.Entry == @"scripts\short.pex").Error);
    }

    [Fact]
    public void Skipped_files_are_rows_in_the_every_file_table()
    {
        using var s = new Sandbox();
        var root = s.Sub("mod");
        File.WriteAllText(Path.Combine(root, "b.esm"), "b");
        var c = Context(root, ["b.esm"], [("a.esm", TriageCommand.LinkWhy), ("C.pex", TriageCommand.CaseWhyFor("c.pex"))]);
        _ = TriageFiles.Run(c).ToList();
        var every = c.Report.Tables.Single(t => t.Title == "Every file");
        Assert.Equal(["a.esm", "b.esm", "C.pex"], every.Rows.Select(r => r[0]));
        Assert.Equal("link", every.Rows[0][1]);
        Assert.Equal("1", every.Rows[1][1]);
        Assert.Equal("same name", every.Rows[2][1]);   // not "?", which means a hash that couldn't be read
    }

    [Fact]
    public void A_file_reached_only_through_a_link_gets_one_look_in_the_whole_report()
    {
        if (OperatingSystem.IsWindows()) return;   // Full's lookup refuses links on Linux and macOS only (see above)
        using var s = new Sandbox();
        var root = s.Sub("mod");
        var outside = s.Sub("elsewhere");
        File.WriteAllText(Path.Combine(outside, "only.esm"), "decoy");
        // Full names a link only where the lower-case path exists (else the file is simply missing), so lower case here
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(outside, "a.pex"), "decoy");
        File.CreateSymbolicLink(Path.Combine(root, "only.esm"), Path.Combine(outside, "only.esm"));
        File.CreateSymbolicLink(Path.Combine(root, "scripts", "a.pex"), Path.Combine(outside, "a.pex"));
        var c = Context(root, ["only.esm", "scripts\\a.pex"], []);
        Assert.Equal(2, c.SetAsideLinked());
        Assert.Empty(c.Files);
        Assert.All(c.Skipped, x => Assert.Equal(TriageCommand.LinkReachedWhy, x.Why));
        var all = new List<TriageFinding>();
        foreach (var (_, run) in TriageCommand.Sections) all.AddRange(run(c));
        foreach (var f in new[] { "only.esm", "scripts\\a.pex" })
        {
            var look = Assert.Single(all, x => x.File == f && x.Level == TriageLevel.Look);
            Assert.Equal(TriageCommand.LinkReachedWhy, look.Text);
        }
    }

    [Fact]
    public void Starfield_esm_is_found_with_its_own_spelling()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        Assert.Null(TriageCommand.StarfieldFile(data));
        File.WriteAllText(Path.Combine(data, "starfield.esm"), "");
        var found = TriageCommand.StarfieldFile(data);
        Assert.NotNull(found);
        Assert.True(File.Exists(found));
        // the later lookups ignore case or take the name from a listing: DataFind, DataHas, OfficialMasters
        var c = Context(s.Sub("mod"), [], []);
        var g = new TriageContext
        {
            Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = c.Root, Root = c.Root, Files = [],
            DataPrefix = "", DataDir = data, Work = c.Root, Report = new TriageReport(),
        };
        Assert.True(g.HasGame);
        Assert.True(g.DataHas("Starfield.esm"));
        Assert.True(File.Exists(g.DataFind("Starfield.esm")));
        var official = Assert.Single(Checker.OfficialMasters(data));
        Assert.True(File.Exists(Path.Combine(data, official)));
        if (!OperatingSystem.IsWindows()) Assert.Equal("starfield.esm", Path.GetFileName(found));
    }

    static TriageContext Context(string root, List<string> files, List<(string File, string Why)> skipped) => new()
    {
        Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = root, Root = root, Files = files,
        DataPrefix = "", DataDir = Path.Combine(root, "no-game"), Work = root, Report = new TriageReport { InputName = "Mod" },
        Skipped = skipped,
    };
}
