// sf-mod-check 0.26.0: archive input (ArchiveInput.cs, LoadOrderInput.cs). A .zip, .7z or .rar given to --plugin or
// --extra-data is unpacked into the run's own temp folder with triage's guards, plugins only for find, overrides and
// summary and in full for conflict, lines and scenes; patch and voice-lines refuse it. Each test fails on 0.25.0, where --plugin <zip> was read
// as a plugin header and --extra-data <zip> as a missing folder. All plugins are made up.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using SharpCompress.Writers.SevenZip;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

[Collection(ReleaseStatics.Name)]   // changes ReleaseCommand.MaxUnpackedBytes
public class ArchiveInputTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Data\ holds Base.esm (keyword B_Kw) and no Starfield.esm. mod\ holds Mod.esm (master Base.esm; keywords
    /// M_One and M_Two), Lib.esm (keyword L_Kw) and Top.esm (master Lib.esm; keyword T_Kw).</summary>
    sealed class Set : Sandbox
    {
        public readonly string Data, ModDir, Tmp;

        public Set()
        {
            Data = Sub("Data");
            ModDir = Sub("mod");
            Tmp = Sub("tmp");
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base");
            b.Keywords.AddNew("B_Kw");
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            var lib = Plugin.New("Lib.esm", PluginStyle.Full, "test", "made-up library");
            lib.Keywords.AddNew("L_Kw");
            using (var g = new GameData(ModDir)) PluginWriter.Write(lib, ModDir, g, NoGap);
            using (var g = new GameData(Data))
            {
                var baseMod = g.Open("Base.esm");
                var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up mod");
                m.Keywords.GetOrAddAsOverride(baseMod.Keywords.First());
                m.Keywords.AddNew("M_One");
                m.Keywords.AddNew("M_Two");
                PluginWriter.Write(m, ModDir, g, NoGap);
            }
            using (var g = new GameData(ModDir))
            {
                var libMod = g.Open("Lib.esm");
                var t = Plugin.New("Top.esm", PluginStyle.Full, "test", "made-up top");
                t.Keywords.GetOrAddAsOverride(libMod.Keywords.First());
                t.Keywords.AddNew("T_Kw");
                PluginWriter.Write(t, ModDir, g, NoGap);
            }
        }

        public byte[] Bytes(string plugin) => File.ReadAllBytes(Path.Combine(ModDir, plugin));

        public string Zip(string name, params (string Path, byte[] Data)[] entries)
        {
            var zip = Path.Combine(Sub("zips"), name);
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var (p, d) in entries)
                using (var w = z.CreateEntry(p).Open()) w.Write(d);
            return zip;
        }

        public string SevenZip(string name, params (string Path, byte[] Data)[] entries)
        {
            var path = Path.Combine(Sub("zips"), name);
            using (var f = File.Create(path))
            using (var w = new SevenZipWriter(f, new SevenZipWriterOptions()))
                foreach (var (p, d) in entries) w.Write(p, new MemoryStream(d), null);
            return path;
        }

        /// <summary>Runs sf-mod-check with its temp folder in Tmp, so a test can see what it leaves there.</summary>
        public (int Code, string Stdout, string Stderr) Run(params string[] args)
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sf-mod-check.exe" : "sf-mod-check");
            var psi = File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(dir, "sf-mod-check.dll") } };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["SFMODCHECK_CACHE_DIR"] = Path.Combine(TestSetup.RunDir, "exe-cache");
            psi.Environment["TMP"] = psi.Environment["TEMP"] = psi.Environment["TMPDIR"] = Tmp;
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(120_000), "sf-mod-check didn't finish in 2 minutes");
            return (p.ExitCode, stdout.Result, stderr.Result);
        }

        /// <summary>True when a run left nothing in Tmp (its temp folder was deleted).</summary>
        public void TmpIsEmpty() =>
            Assert.True(!Directory.EnumerateFileSystemEntries(Tmp).Any(), "left in the temp folder: " + string.Join(", ", Directory.EnumerateFileSystemEntries(Tmp)));

        /// <summary>A LoadOrderInput resolved in this process on the given options (Data is always this set's).</summary>
        public LoadOrderInput Input(bool allowPlugin, ArchiveInput.Use use, params string[] args)
        {
            var input = new LoadOrderInput(allowPlugin, use, "test");
            var a = args.Concat(["--data", Data]).ToArray();
            for (int i = 0; i < a.Length; i++) Assert.True(input.TryOption(a, ref i, "usage") == true, a[i]);
            return input;
        }
    }

    /// <summary>The record lines of find's whole-plugin listing (the lines that name a record of the set).</summary>
    static List<string> Records(string stdout) =>
        stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains("M_One") || l.Contains("M_Two") || l.StartsWith("RESULT")).ToList();

    [Fact]
    public void A_zip_a_7z_and_a_rar_give_the_same_find_listing_as_the_plugin_itself()
    {
        using var s = new Set();
        var mod = s.Bytes("Mod.esm");
        var (code0, plain, err0) = s.Run("find", "--plugin", Path.Combine(s.ModDir, "Mod.esm"), "--data", s.Data);
        Assert.True(code0 == 0, plain + err0);
        var want = Records(plain);
        Assert.Equal(3, want.Count);   // M_One, M_Two and the RESULT line
        // the plugin at the top, under Data\ and under <ModName>\, with a file that isn't a plugin beside it
        var zip = s.Zip("Mod-1.0.zip", ("Mod.esm", mod), ("readme.txt", "x"u8.ToArray()));
        var seven = s.SevenZip("Mod-1.0.7z", ("Data/Mod.esm", mod), ("Data/Textures/a.dds", "dds"u8.ToArray()));
        // no free tool writes .rar: a zip named .rar takes the .7z and .rar unpacker (SharpCompress reads it by its content)
        var rar = Path.Combine(s.Dir, "zips", "Mod-1.0.rar");
        File.Copy(s.Zip("ForRar.zip", ("MyMod/Mod.esm", mod)), rar);
        foreach (var archive in new[] { zip, seven, rar })
        {
            var (code, stdout, stderr) = s.Run("find", "--plugin", archive, "--data", s.Data, "--verbose");
            Assert.True(code == 0, archive + "\n" + stdout + stderr);
            Assert.Equal(want, Records(stdout));
            Assert.Contains("archive input: ", stderr);   // --verbose logs the unpack
            Assert.Contains("every entry checked", stderr);
            s.TmpIsEmpty();
        }
        // a record looked up by EditorID through the archive
        var (c2, o2, e2) = s.Run("find", "M_Two", "--plugin", zip, "--data", s.Data);
        Assert.True(c2 == 0, o2 + e2);
        Assert.Contains("Mod.esm (from Mod-1.0.zip) and its 1 master", o2 + e2);
        s.TmpIsEmpty();
    }

    [Fact]
    public void An_archive_with_no_plugin_or_plugins_in_two_folders_is_refused_and_leaves_nothing()
    {
        using var s = new Set();
        var none = s.Zip("Textures.zip", ("Textures/a.dds", "dds"u8.ToArray()));
        var (code, stdout, stderr) = s.Run("find", "--plugin", none, "--data", s.Data);
        Assert.Equal(2, code);
        Assert.Contains("can't read --plugin Textures.zip: it holds no plugin (.esm, .esp or .esl)", stderr);
        Assert.Contains("RESULT: CANNOT CHECK (--plugin Textures.zip can't be read)", stdout);
        s.TmpIsEmpty();

        var fomod = s.Zip("Options.zip", ("Option A/Mod.esm", s.Bytes("Mod.esm")), ("Option B/Mod.esm", s.Bytes("Mod.esm")));
        (code, stdout, stderr) = s.Run("find", "--plugin", fomod, "--data", s.Data);
        Assert.Equal(2, code);
        Assert.Contains("its plugins sit in 2 folders (Option A, Option B)", stderr);
        Assert.Contains("give the folder you would install, or the plugin itself", stderr);
        s.TmpIsEmpty();

        // a file named .zip that isn't one: the reader's message ends in a full stop, ours must not add a second
        var fake = Path.Combine(Path.GetDirectoryName(none)!, "Fake.zip");
        File.WriteAllText(fake, "not a zip");
        (code, stdout, stderr) = s.Run("find", "--plugin", fake, "--data", s.Data);
        Assert.Equal(2, code);
        Assert.Contains("can't read --plugin Fake.zip:", stderr);
        Assert.DoesNotContain("..", stderr);
        s.TmpIsEmpty();
    }

    [Fact]
    public void Crafted_entries_are_refused_and_nothing_lands_outside_the_temp_folder()
    {
        using var s = new Set();
        var mod = s.Bytes("Mod.esm");
        var cases = new (string Archive, string Why)[]
        {
            (s.Zip("Up.zip", ("Mod.esm", mod), ("../evil.txt", "x"u8.ToArray())), "points outside the zip's own folders"),
            (s.Zip("Rooted.zip", ("Mod.esm", mod), ("/evil.txt", "x"u8.ToArray())), "points outside the zip's own folders"),
            (s.Zip("Twice.zip", ("Mod.esm", mod), ("Textures/A.dds", "x"u8.ToArray()), ("textures/a.dds", "y"u8.ToArray())), "twice (names differ only in case or slashes)"),
            (s.SevenZip("Up.7z", ("Mod.esm", mod), ("../evil.txt", "x"u8.ToArray())), "points outside the archive's own folders"),
            (s.SevenZip("Twice.7z", ("Mod.esm", mod), ("A.txt", "x"u8.ToArray()), ("a.txt", "y"u8.ToArray())), "twice (names differ only in case or slashes)"),
        };
        foreach (var (archive, why) in cases)
        {
            var (code, stdout, stderr) = s.Run("find", "--plugin", archive, "--data", s.Data);
            Assert.True(code == 2, archive + "\n" + stdout + stderr);
            Assert.Contains($"can't read --plugin {Path.GetFileName(archive)}: ", stderr);
            Assert.Contains(why, stderr);   // a guard on a file that isn't a plugin, which isn't written, still refuses
            s.TmpIsEmpty();
        }
        Assert.False(File.Exists(Path.Combine(s.Dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(s.Dir, "zips", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(s.Tmp, "evil.txt")));
    }

    [Fact]
    public void The_plugins_only_unpack_writes_only_plugins_and_still_counts_every_entry_against_the_size_cap()
    {
        using var s = new Set();
        var mod = s.Bytes("Mod.esm");
        var zip = s.Zip("Mod.zip", ("Data/Mod.esm", mod), ("Data/Textures/a.dds", new byte[5000]));
        var seven = s.SevenZip("Mod.7z", ("Data/Mod.esm", mod), ("Data/Textures/a.dds", new byte[5000]));
        foreach (var archive in new[] { zip, seven })
        {
            var dest = Path.Combine(s.Dir, "out-" + Path.GetExtension(archive)[1..]);
            var u = ArchiveInput.Open(archive, dest, ArchiveInput.Use.PluginsOnly, out var why);
            Assert.True(u != null, why);
            Assert.Equal(["Mod.esm"], u!.Plugins);
            Assert.Equal(Path.Combine(Path.GetFullPath(dest), "Data"), u.Folder);
            Assert.False(File.Exists(Path.Combine(dest, "Data", "Textures", "a.dds")));
            var all = ArchiveInput.Open(archive, dest + "-all", ArchiveInput.Use.Everything, out why);
            Assert.True(all != null, why);
            Assert.True(File.Exists(Path.Combine(dest + "-all", "Data", "Textures", "a.dds")));

            // the cap counts the texture too: the plugin alone fits under it, the whole archive doesn't
            var old = ReleaseCommand.MaxUnpackedBytes;
            ReleaseCommand.MaxUnpackedBytes = mod.Length + 1000;
            try
            {
                Assert.Null(ArchiveInput.Open(archive, dest + "-cap", ArchiveInput.Use.PluginsOnly, out why));
                Assert.Contains("more than the", why);
                Assert.False(File.Exists(Path.Combine(dest + "-cap", "Data", "Mod.esm")));
            }
            finally { ReleaseCommand.MaxUnpackedBytes = old; }
        }
    }

    /// <summary>Code review round 2: a localized plugin's names and texts are in loose .strings files or in its BA2, so the
    /// plugins-only unpack writes those too (all but texture BA2s); without them every name read as "".</summary>
    [Fact]
    public void The_plugins_only_unpack_also_writes_strings_files_and_the_non_texture_ba2s()
    {
        using var s = new Set();
        var mod = s.Bytes("Mod.esm");
        var zip = s.Zip("Loc.zip", ("Data/Mod.esm", mod), ("Data/Strings/Mod_en.strings", new byte[10]),
            ("Data/Strings/Mod_en.dlstrings", new byte[10]), ("Data/Strings/Mod_en.ilstrings", new byte[10]),
            ("Data/Mod - Main.ba2", new byte[10]), ("Data/Mod - Textures.ba2", new byte[10]), ("Data/Textures/a.dds", new byte[10]),
            ("Data/Scripts/a.pex", new byte[10]));
        var dest = Path.Combine(s.Dir, "out-loc");
        var u = ArchiveInput.Open(zip, dest, ArchiveInput.Use.PluginsOnly, out var why);
        Assert.True(u != null, why);
        Assert.Equal(["Mod.esm"], u!.Plugins);
        var written = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dest, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Data/Mod - Main.ba2", "Data/Mod.esm", "Data/Strings/Mod_en.dlstrings", "Data/Strings/Mod_en.ilstrings", "Data/Strings/Mod_en.strings"], written);
    }

    /// <summary>Plan step 5: a drive-letter entry (zip and 7z), and a zip whose entries carry the encrypted flag, built in
    /// the test so it runs on every system (the 7-Zip fixtures below need Windows and 7-Zip).</summary>
    [Fact]
    public void Drive_letter_entries_and_a_zip_marked_encrypted_are_refused_on_every_system()
    {
        using var s = new Set();
        var mod = s.Bytes("Mod.esm");
        var cases = new (string Archive, string Why)[]
        {
            (s.Zip("Drive.zip", ("Mod.esm", mod), ("C:/evil.txt", "x"u8.ToArray())), "points outside the zip's own folders"),
            (s.Zip("DriveRel.zip", ("Mod.esm", mod), ("C:evil.txt", "x"u8.ToArray())), "points outside the zip's own folders"),
        }.ToList();
        // SharpCompress's 7z writer drops a drive from a name, so the 7z case needs 7-Zip's -spf (full paths kept)
        var sevenZip = @"C:\Program Files\7-Zip\7z.exe";
        if (OperatingSystem.IsWindows() && File.Exists(sevenZip))
        {
            var seven = Path.Combine(s.Dir, "zips", "Drive.7z");
            var evil = Path.Combine(s.Dir, "evil-src.txt");
            File.WriteAllText(evil, "x");
            foreach (var f in new[] { Path.Combine(s.ModDir, "Mod.esm"), evil })
            {
                var psi = new ProcessStartInfo(sevenZip) { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var a in new[] { "a", "-spf", seven, Path.GetFullPath(f) }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            cases.Add((seven, "points outside the archive's own folders"));
        }
        foreach (var (archive, why) in cases)
        {
            var (code, stdout, stderr) = s.Run("find", "--plugin", archive, "--data", s.Data);
            Assert.True(code == 2, archive + "\n" + stdout + stderr);
            Assert.Contains(why, stderr);
            s.TmpIsEmpty();
        }

        // set the "encrypted" bit (general purpose flag bit 0) in every local and central header of a plain zip
        var zip = s.Zip("Marked.zip", ("Mod.esm", mod));
        var bytes = File.ReadAllBytes(zip);
        int marked = 0;
        for (int i = 0; i + 10 < bytes.Length; i++)
        {
            if (bytes[i] != 'P' || bytes[i + 1] != 'K') continue;
            if (bytes[i + 2] == 3 && bytes[i + 3] == 4) { bytes[i + 6] |= 1; marked++; }
            else if (bytes[i + 2] == 1 && bytes[i + 3] == 2) { bytes[i + 8] |= 1; marked++; }
        }
        Assert.Equal(2, marked);
        File.WriteAllBytes(zip, bytes);
        var (c, o, e) = s.Run("find", "--plugin", zip, "--data", s.Data);
        Assert.True(c == 2, o + e);
        Assert.Contains("the entry \"Mod.esm\" is password-protected", e);
        s.TmpIsEmpty();
    }

    /// <summary>Plan step 5: conflict, lines and scenes take an archive, open it, and leave no temp folder behind. They
    /// write every file (review round 1: lines looks up voice files and scenes scripts in the archive), while find
    /// writes only the plugins.</summary>
    [Fact]
    public void Conflict_lines_and_scenes_take_an_archive_and_leave_no_temp_folder()
    {
        using var s = new Set();
        var zip = s.Zip("Pack.zip", ("Data/Mod.esm", s.Bytes("Mod.esm")), ("Data/Textures/a.dds", "dds"u8.ToArray()));
        var plugins = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllText(plugins, "*Base.esm\n");
        var runs = new[]
        {
            new[] { "conflict", "--plugins", plugins, "--extra-data", zip, "--data", s.Data, "--out", Path.Combine(s.Dir, "conflict-out"), "--verbose" },
            new[] { "lines", "M_One", "--plugin", zip, "--data", s.Data, "--verbose" },
            new[] { "scenes", "M_One", "--plugin", zip, "--data", s.Data, "--verbose" },
        };
        foreach (var args in runs)
        {
            var (code, stdout, stderr) = s.Run(args);
            Assert.True(stderr.Contains("archive input: "), args[0] + " didn't unpack the archive\n" + stdout + stderr);
            Assert.DoesNotContain("needs the mod installed or unpacked", stderr);
            Assert.DoesNotContain("can't read --", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.True(code is 0 or 1 or 2, args[0] + " exit " + code);
            Assert.True(stderr.Contains("Pack.zip (Zip, every file)"), args[0] + " didn't write every file\n" + stderr);
            s.TmpIsEmpty();
        }
        var (fc, fo, fe) = s.Run("find", "--plugin", zip, "--data", s.Data, "--verbose");
        Assert.True(fc == 0, fo + fe);
        Assert.Contains("Pack.zip (Zip, plugins and their strings only)", fe);
        s.TmpIsEmpty();
    }

    [Fact]
    public void An_encrypted_zip_or_7z_is_refused()
    {
        // 7-Zip makes the fixtures when it is installed (as the triage test of an encrypted 7z)
        var sevenZip = @"C:\Program Files\7-Zip\7z.exe";
        if (!OperatingSystem.IsWindows() || !File.Exists(sevenZip)) return;
        using var s = new Set();
        void Pack(params string[] args)
        {
            var psi = new ProcessStartInfo(sevenZip) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        var zip = Path.Combine(s.Dir, "Locked.zip");
        var seven = Path.Combine(s.Dir, "Locked.7z");
        Pack("a", "-tzip", "-pSecret1", zip, Path.Combine(s.ModDir, "Mod.esm"));
        Pack("a", "-p" + "Secret1", seven, Path.Combine(s.ModDir, "Mod.esm"));
        foreach (var archive in new[] { zip, seven })
        {
            var (code, stdout, stderr) = s.Run("find", "--plugin", archive, "--data", s.Data);
            Assert.True(code == 2, archive + "\n" + stdout + stderr);
            Assert.Contains("password-protected", stderr);
            s.TmpIsEmpty();
        }
    }

    [Fact]
    public void Plugin_with_an_archive_of_two_plugins_orders_the_master_first_and_names_the_last()
    {
        using var s = new Set();
        var zip = s.Zip("Pair.zip", ("Top.esm", s.Bytes("Top.esm")), ("Lib.esm", s.Bytes("Lib.esm")));
        using (var input = s.Input(true, ArchiveInput.Use.PluginsOnly, "--plugin", zip))
        {
            Assert.True(input.Resolve(out var why), why);
            Assert.Equal(["Lib.esm", "Top.esm"], input.Order().Listed);
            Assert.Equal("Top.esm", Path.GetFileName(input.Plugin));
            Assert.Equal(["Lib.esm", "Top.esm"], input.ArchivePlugins);
            Assert.Contains("the 2 plugins of Pair.zip and their 0 masters", input.Order().Source);
        }
        // the exe finds a record of either plugin
        var (code, stdout, stderr) = s.Run("find", "L_Kw", "--plugin", zip, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("Pair.zip holds 2 plugins (Lib.esm, Top.esm)", stderr);
        s.TmpIsEmpty();
    }

    [Fact]
    public void Extra_data_with_an_archive_adds_its_inactive_plugins_at_the_end_and_keeps_an_active_one_in_place()
    {
        using var s = new Set();
        var plugins = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllText(plugins, "*Lib.esm\n*Base.esm\n");
        // Lib.esm is active; Mod.esm and Top.esm aren't, so they go last, alphabetically (Lib.esm, Top's master, is already in)
        var zip = s.Zip("Pack.zip", ("Data/Top.esm", s.Bytes("Top.esm")), ("Data/Lib.esm", s.Bytes("Lib.esm")), ("Data/Mod.esm", s.Bytes("Mod.esm")),
                        ("Data/Textures/a.dds", "dds"u8.ToArray()));
        using (var input = s.Input(false, ArchiveInput.Use.PluginsOnly, "--plugins", plugins, "--extra-data", zip))
        {
            Assert.True(input.Resolve(out var why), why);
            Assert.Equal(["Lib.esm", "Base.esm", "Mod.esm", "Top.esm"], input.Order().Listed);
            Assert.Contains("then 2 plugins of Pack.zip not active in it", input.Order().Source);
            Assert.Contains($"--extra-data \"{Path.GetFullPath(zip)}\"", input.RerunOptions());   // the archive, not the temp folder
            Assert.False(File.Exists(Path.Combine(input.ExtraDir!, "Textures", "a.dds")));
        }
        // conflict unpacks every file, so its asset lookups see the archive's loose texture
        using (var input = s.Input(false, ArchiveInput.Use.Everything, "--plugins", plugins, "--extra-data", zip))
        {
            Assert.True(input.Resolve(out var why), why);
            Assert.True(File.Exists(Path.Combine(input.AssetDirs[0], "Textures", "a.dds")));
        }
        // the exe: a master inside the archive comes before its plugin, and the note names each added plugin
        File.WriteAllText(plugins, "*Base.esm\n");
        var (code, stdout, stderr) = s.Run("find", "T_Kw", "--plugins", plugins, "--extra-data", zip, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("note: Lib.esm from Pack.zip isn't active in Plugins.txt, so it is read as the last plugin", stderr);
        Assert.Contains("note: Top.esm from Pack.zip isn't active in Plugins.txt", stderr);
        s.TmpIsEmpty();
        // a folder given to --extra-data keeps the 0.25.0 rule: a plugin Plugins.txt doesn't list isn't loaded
        var (c2, o2, e2) = s.Run("find", "T_Kw", "--plugins", plugins, "--extra-data", s.ModDir, "--data", s.Data);
        Assert.True(c2 == 1, o2 + e2);
    }

    [Fact]
    public void Patch_and_voice_lines_refuse_an_archive()
    {
        using var s = new Set();
        var zip = s.Zip("Mod.zip", ("Mod.esm", s.Bytes("Mod.esm")));
        var plugins = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllText(plugins, "*Base.esm\n");
        var (code, stdout, stderr) = s.Run("patch", "--plugins", plugins, "--extra-data", zip, "--data", s.Data, "--out", Path.Combine(s.Dir, "out"));
        Assert.True(code == 2, stdout + stderr);
        Assert.Contains("patch needs the mod installed or unpacked: give the folder, not the archive (--extra-data Mod.zip)", stderr);
        (code, stdout, stderr) = s.Run("voice-lines", zip, "--data", s.Data);
        Assert.True(code == 2, stdout + stderr);
        // code review round 3: the plugin's own option asks for the unpacked plugin file, not a folder (a folder is refused there)
        // code review round 4: voice-lines takes the plugin as its first argument, so the message names no --plugin
        Assert.Contains("voice-lines needs the mod installed or unpacked: give the unpacked plugin file, not the archive Mod.zip.", stderr);
        Assert.DoesNotContain("--plugin", stderr);
        s.TmpIsEmpty();
    }
}
