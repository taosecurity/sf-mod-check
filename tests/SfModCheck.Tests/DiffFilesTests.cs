// diff step 5 (0.11.0): loose files, archives and scripts. Every archive and script here is made up by the tests
// (TriageScriptsTests.Ba2, the DX10 writer below, PexBytes), never copied from game or mod files.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class DiffFilesTests
{
    static (int Code, string Stdout, string Stderr) Run(params string[] args) => ExeTests.RunSplit(args);

    static void Put(string root, string rel, byte[] data)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    const string Secret = "C:\\Secret\\Constant.txt";

    /// <summary>A made-up script: OnInit (its code changes with v), OnTimer (v2 adds a parameter), a Busy state in v2, and
    /// a string constant that must never reach the report.</summary>
    static byte[] Script(int v, bool debug = true) => PexBytes.Build(
    [
        new PObj("MyMod:Handler", v >= 2 ? "Quest" : "ScriptObject", Vars: [("Count", "Int", PV.Int(v >= 3 ? 9 : 1))], States:
        [
            ("", [
                new PFn("OnInit", Locals: [("::temp0", "String")], Code:
                [
                    PIns.Assign("::temp0", PV.Str(Secret)),
                    PIns.Assign("::temp0", PV.Int(v)),
                ]),
                new PFn("OnTimer", Params: v >= 2 ? [("aiTimerID", "Int")] : null),
                new PFn("Unchanged", Code: [PIns.Return(PV.None)]),
                .. (v >= 2 ? new[] { new PFn("OnLoad") } : []),
            ]),
            .. (v >= 2 ? new[] { ("Busy", new[] { new PFn("OnTimer") }) } : []),
        ]),
    ], debug: debug);

    // ---- scripts ----

    [Fact]
    public void Scripts_compare_objects_states_and_functions_by_name()
    {
        var a = Pex.Read(Script(1), "a.pex");
        var b = Pex.Read(Script(2), "b.pex");
        var o = Assert.Single(PexCompare.Objects(a, b));
        Assert.Equal(("MyMod:Handler", "changed"), (o.Name, o.Status));
        Assert.Equal("ScriptObject -> Quest", o.ParentChange);
        Assert.Equal(["Busy"], o.StatesAdded);
        Assert.Equal(new[] { "OnInit", "OnTimer" }, o.FunctionsChanged.Order());
        Assert.Equal(new[] { "OnLoad", "OnTimer (state Busy)" }, o.FunctionsAdded.Order());
        Assert.Empty(o.FunctionsRemoved);
        Assert.Empty(o.Other);

        // a variable's default changes, nothing else
        var c = Assert.Single(PexCompare.Objects(b, Pex.Read(Script(3), "c.pex")));
        Assert.Equal(["OnInit"], c.FunctionsChanged);
        Assert.Equal(["variables: 1 changed (Count)"], c.Other);

        // the same objects with other debug info: nothing to report
        Assert.Empty(PexCompare.Objects(a, Pex.Read(Script(1, debug: false), "d.pex")));

        // objects added and removed
        var other = Pex.Read(PexBytes.Build([new PObj("MyMod:Other")]), "e.pex");
        var ab = PexCompare.Objects(a, other);
        Assert.Contains(ab, x => x.Name == "MyMod:Handler" && x.Status == "removed");
        Assert.Contains(ab, x => x.Name == "MyMod:Other" && x.Status == "added");
    }

    // ---- archives ----

    /// <summary>A texture (DX10, version 1) BA2: each file one chunk with these bytes, stored as is.</summary>
    static byte[] Dx10(params (string Name, byte[] Data, ushort Width)[] files)
    {
        long dataAt = 24 + (24 + 24L) * files.Length;
        var names = new MemoryStream();
        foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Name); names.Write(BitConverter.GetBytes((ushort)b.Length)); names.Write(b); }
        long nameAt = dataAt + files.Sum(f => (long)f.Data.Length);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BTDX")); w.Write(1u); w.Write(Encoding.ASCII.GetBytes("DX10")); w.Write((uint)files.Length); w.Write((ulong)nameAt);
        long at = dataAt;
        foreach (var f in files)
        {
            w.Write(0u); w.Write(Encoding.ASCII.GetBytes("dds\0")); w.Write(0u); w.Write((byte)0); w.Write((byte)1); w.Write((ushort)24);
            w.Write((ushort)64); w.Write(f.Width); w.Write((byte)1); w.Write((byte)98); w.Write((byte)0); w.Write((byte)0);
            w.Write((ulong)at); w.Write(0u); w.Write((uint)f.Data.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write(0xBAADF00Du);
            at += f.Data.Length;
        }
        foreach (var f in files) w.Write(f.Data);
        w.Write(names.ToArray());
        w.Flush();
        return o.ToArray();
    }

    [Fact]
    public void Archive_files_hash_by_their_stored_bytes()
    {
        using var s = new Sandbox();
        string P(string n, byte[] b) { var p = Path.Combine(s.Dir, n); File.WriteAllBytes(p, b); return p; }
        var plain = Ba2Contents.Hashes(P("a.ba2", TriageScriptsTests.Ba2(("meshes/a.nif", [1, 2, 3], false), ("scripts/b.pex", [4, 5], true))), out var t);
        Assert.Equal("GNRL", t);
        Assert.Equal(new[] { "meshes\\a.nif", "scripts\\b.pex" }, plain.Keys.Order());
        // another order and the same bytes: the same hashes
        var moved = Ba2Contents.Hashes(P("b.ba2", TriageScriptsTests.Ba2(("scripts/b.pex", [4, 5], true), ("meshes/a.nif", [1, 2, 3], false))), out _);
        Assert.True(plain.All(kv => kv.Value.Hash.AsSpan().SequenceEqual(moved[kv.Key].Hash)));
        // the same file stored packed counts as changed (nothing is unpacked)
        var packed = Ba2Contents.Hashes(P("c.ba2", TriageScriptsTests.Ba2(("meshes/a.nif", [1, 2, 3], true), ("scripts/b.pex", [4, 5], true))), out _);
        Assert.False(plain["meshes\\a.nif"].Hash.AsSpan().SequenceEqual(packed["meshes\\a.nif"].Hash));

        // textures: the bytes and the dimensions count
        var tex = Ba2Contents.Hashes(P("t1.ba2", Dx10(("textures/a.dds", [9, 9, 9], 64), ("textures/b.dds", [7], 64))), out var tt);
        Assert.Equal("DX10", tt);
        var tex2 = Ba2Contents.Hashes(P("t2.ba2", Dx10(("textures/a.dds", [9, 9, 9], 128), ("textures/b.dds", [7], 64))), out _);
        Assert.False(tex["textures\\a.dds"].Hash.AsSpan().SequenceEqual(tex2["textures\\a.dds"].Hash));
        Assert.True(tex["textures\\b.dds"].Hash.AsSpan().SequenceEqual(tex2["textures\\b.dds"].Hash));

        // a record whose data runs past the end names the archive and the entry
        var bad = TriageScriptsTests.Ba2(("meshes/a.nif", [1, 2, 3], false));
        BitConverter.GetBytes(999_999UL).CopyTo(bad, 24 + 16);
        var e = Assert.Throws<InvalidDataException>(() => Ba2Contents.Hashes(P("bad.ba2", bad), out _));
        Assert.Contains("bad.ba2", e.Message);
        Assert.Contains("meshes\\a.nif", e.Message);
    }

    // ---- the whole run ----

    /// <summary>Two versions of a mod folder: the same plugin, loose files added, removed and changed, a loose script
    /// changed, and an archive with a file added, one changed, one the same and a script compiled again.</summary>
    static (string Old, string New) Mods(Sandbox s)
    {
        string oldDir = s.Sub("v1"), newDir = s.Sub("v2");
        var plugin = DiffCommandTests.Build(s, "plugin", "Mod.esm", PluginStyle.Light, (mod, _) => mod.MiscItems.AddNew(new FormKey(mod.ModKey, 0x800)).EditorID = "MOD_Misc");
        foreach (var d in new[] { oldDir, newDir }) File.Copy(plugin, Path.Combine(d, "Mod.esm"));
        Put(oldDir, "Readme.txt", Encoding.ASCII.GetBytes("one"));
        Put(newDir, "Readme.txt", Encoding.ASCII.GetBytes("two!"));
        Put(oldDir, "Docs/Old.txt", [1]);
        Put(newDir, "Docs/New.txt", [1]);
        Put(oldDir, "Same.txt", [5]);
        Put(newDir, "Same.txt", [5]);
        Put(oldDir, "Scripts/MyMod/Handler.pex", Script(1));
        Put(newDir, "Scripts/MyMod/Handler.pex", Script(2));
        Put(oldDir, "Mod - Main.ba2", TriageScriptsTests.Ba2(("meshes/a.nif", [1, 2, 3], false), ("meshes/b.nif", [4], false),
            ("scripts/mymod/other.pex", PexBytes.Build([new PObj("MyMod:Other")]), true), ("scripts/mymod/gone.pex", Script(1), true)));
        Put(newDir, "Mod - Main.ba2", TriageScriptsTests.Ba2(("meshes/a.nif", [1, 2, 3], false), ("meshes/b.nif", [4, 4], false),
            ("scripts/mymod/other.pex", PexBytes.Build([new PObj("MyMod:Other")], debug: false), true), ("meshes/c.nif", [6], false)));
        Put(oldDir, "Mod - Textures.ba2", Dx10(("textures/a.dds", [9], 64)));
        Put(newDir, "Mod - Textures.ba2", Dx10(("textures/a.dds", [9], 64)));
        return (oldDir, newDir);
    }

    [Fact]
    public void Exe_compares_loose_files_archives_and_scripts()
    {
        using var s = new Sandbox();
        var (a, b) = Mods(s);
        var r = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, r.Code);
        // loose: 1 added, 1 removed, 2 changed; archive: 1 added, 1 removed, 2 changed
        Assert.Equal("RESULT: DIFFERENCES (1 plugin compared, 0 changed, 0 added, 0 removed; records: 0 added, 0 removed, 0 changed, 0 renumbered, 0 noise; " +
                     "files: 2 added, 2 removed, 4 changed)", ExeTests.ResultLine(r.Stdout));
        var t = r.Stdout;
        Assert.Contains("  1 added, 1 removed, 2 changed; 1 the same", t);
        Assert.Contains("Docs: 1 added, 1 removed", t);
        Assert.Contains("Readme.txt (3 -> 4 bytes)", t);   // the spelling on disk
        Assert.Contains("changed  Mod - Main.ba2 (GNRL, 4 -> 4 files): 1 added, 1 removed, 2 changed; 1 the same", t);
        Assert.Contains("same     Mod - Textures.ba2 (DX10, 1 file; the same bytes)", t);
        Assert.Contains("Scripts\\MyMod\\Handler.pex (loose):", t);
        Assert.Contains("object MyMod:Handler: changed", t);
        Assert.Contains("extends: ScriptObject -> Quest", t);
        Assert.Contains("functions changed (2): OnInit, OnTimer", t);
        Assert.Contains("functions added (2): OnLoad, OnTimer (state Busy)", t);
        Assert.Contains("scripts\\mymod\\other.pex (in Mod - Main.ba2): the same objects, states and functions", t);
        Assert.DoesNotContain("Secret", t);   // never a string constant, never code
        Assert.DoesNotContain("assign", t);

        var j = Run("diff", a, b, "--quiet", "--json", "--data", s.Sub("nodata"));
        Assert.Equal(1, j.Code);
        using var doc = JsonDocument.Parse(j.Stdout[..j.Stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)]);
        var root = doc.RootElement;
        var files = root.GetProperty("files");
        Assert.Equal("Docs\\New.txt", Assert.Single(files.GetProperty("added").EnumerateArray()).GetString());
        Assert.Equal(1, files.GetProperty("same").GetInt32());
        var main = root.GetProperty("archives").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Mod - Main.ba2");
        Assert.Equal("changed", main.GetProperty("status").GetString());
        Assert.Equal(["meshes\\c.nif"], main.GetProperty("added").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["scripts\\mymod\\gone.pex"], main.GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
        var scripts = root.GetProperty("scripts").EnumerateArray().ToList();
        Assert.Equal(2, scripts.Count);
        var handler = scripts.Single(x => x.GetProperty("where").GetString() == "loose");
        Assert.Equal(["OnInit", "OnTimer"], handler.GetProperty("objects")[0].GetProperty("functions_changed").EnumerateArray().Select(x => x.GetString()).Order());
        Assert.True(scripts.Single(x => x.GetProperty("where").GetString() == "Mod - Main.ba2").GetProperty("code_same").GetBoolean());
        Assert.DoesNotContain("Secret", j.Stdout);
    }

    [Fact]
    public void Exe_same_files_exit_0_and_a_data_folder_pairs_without_it()
    {
        using var s = new Sandbox();
        var (a, _) = Mods(s);
        // the same mod zipped with a Data folder at the top
        var zip = Path.Combine(s.Dir, "v1-data.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var f in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
                z.CreateEntryFromFile(f, "Data/" + Path.GetRelativePath(a, f).Replace('\\', '/'));
        var r = Run("diff", a, zip, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(0, r.Code);
        Assert.Equal("RESULT: NO DIFFERENCES (1 plugin compared, 0 changed, 0 added, 0 removed; records: 0 added, 0 removed, 0 changed, 0 renumbered, 0 noise; " +
                     "files: 0 added, 0 removed, 0 changed)", ExeTests.ResultLine(r.Stdout));
        Assert.Contains("same     Mod - Main.ba2", r.Stdout);
        Assert.DoesNotContain("== Scripts", r.Stdout);

        // two plugins given directly: no file sections, and the JSON says null
        var p = Path.Combine(a, "Mod.esm");
        var j = Run("diff", p, p, "--quiet", "--json", "--data", s.Sub("nodata"));
        Assert.Equal(0, j.Code);
        using var doc = JsonDocument.Parse(j.Stdout[..j.Stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)]);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("files").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("archives").ValueKind);
        Assert.DoesNotContain("files:", ExeTests.ResultLine(j.Stdout));
    }

    [Fact]
    public void A_damaged_archive_is_changed_with_the_reason()
    {
        using var s = new Sandbox();
        string a = s.Sub("a"), b = s.Sub("b");
        Put(a, "X - Main.ba2", TriageScriptsTests.Ba2(("meshes/a.nif", [1], false)));
        Put(b, "X - Main.ba2", Encoding.ASCII.GetBytes("damaged, not an archive at all"));
        var r = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, r.Code);
        Assert.Contains("changed  X - Main.ba2: the bytes differ, and its files couldn't be listed:", r.Stdout);
        Assert.EndsWith("files: 0 added, 0 removed, 1 changed)", ExeTests.ResultLine(r.Stdout));
    }
}
