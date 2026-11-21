// diff step 6 (0.11.0): the README's Example mods and the JSON keys AGENTS.md must list. The two versions of "MyMod"
// are made up here (BuildKit plugin, a made-up script and archive), never copied from game or mod files. To keep a copy
// for the README Example, set SFMODCHECK_DIFF_EXAMPLE to a folder: the test writes MyMod-1.0 and MyMod-1.1 there.
using System.Text;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class DiffExampleTests
{
    static void Put(string root, string rel, byte[] data)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    static MiscItem Misc(StarfieldMod mod, uint id, string editorId)
    {
        var m = mod.MiscItems.AddNew(new FormKey(mod.ModKey, id));
        m.EditorID = editorId;
        return m;
    }

    static PlacedObject Ref(StarfieldMod mod, uint id, IFormLinkGetter<IPlaceableObjectGetter> baseObj) =>
        new(new FormKey(mod.ModKey, id), StarfieldRelease.Starfield) { Base = baseObj.FormKey.ToLink<IPlaceableObjectGetter>() };

    /// <summary>A made-up script MyMod:VaultScript; version 2 changes OnActivate and adds OnLoad.</summary>
    static byte[] Script(int v) => PexBytes.Build(
    [
        new PObj("MyMod:VaultScript", "ObjectReference", States:
        [
            ("", [
                new PFn("OnActivate", Params: [("akActionRef", "ObjectReference")], Code: [PIns.Return(PV.Int(v))]),
                new PFn("OnInit"),
                .. (v >= 2 ? new[] { new PFn("OnLoad") } : []),
            ]),
        ]),
    ]);

    /// <summary>MyMod.esm, version 1.0 or 1.1: misc items, a leveled list and a cell with two placed objects. 1.1 gives
    /// MYM_Gem a keyword, removes MYM_OldToken (804), adds MYM_NewToken (805), changes the list's entries, swaps the
    /// base of one placed object and places one more.</summary>
    static string Plugin(Sandbox s, string sub, bool v11) =>
        DiffCommandTests.Build(s, sub, "MyMod.esm", PluginStyle.Full, (mod, _) =>
        {
            var kw = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x800)); kw.EditorID = "MYM_Shiny";
            var gem = Misc(mod, 0x801, "MYM_Gem");
            if (v11) gem.Keywords = [kw.ToLink<IKeywordGetter>()];
            var coin = Misc(mod, 0x802, "MYM_Coin");
            var token = v11 ? Misc(mod, 0x805, "MYM_NewToken") : Misc(mod, 0x804, "MYM_OldToken");
            var list = mod.LeveledItems.AddNew(new FormKey(mod.ModKey, 0x803));
            list.EditorID = "MYM_VaultLoot";
            list.Entries = v11
                ? [new LeveledItemEntry { Level = 1, Reference = gem.ToLink<IItemGetter>(), Count = 1 },
                   new LeveledItemEntry { Level = 1, Reference = coin.ToLink<IItemGetter>(), Count = 10 },
                   new LeveledItemEntry { Level = 5, Reference = token.ToLink<IItemGetter>(), Count = 1 }]
                : [new LeveledItemEntry { Level = 1, Reference = gem.ToLink<IItemGetter>(), Count = 1 },
                   new LeveledItemEntry { Level = 1, Reference = coin.ToLink<IItemGetter>(), Count = 5 }];
            var vault = new Cell(new FormKey(mod.ModKey, 0x810), StarfieldRelease.Starfield) { EditorID = "MYM_VaultCell" };
            vault.Temporary.Add(Ref(mod, 0x820, gem.ToLink<IPlaceableObjectGetter>()));
            vault.Temporary.Add(Ref(mod, 0x821, (v11 ? gem : coin).ToLink<IPlaceableObjectGetter>()));
            if (v11) vault.Temporary.Add(Ref(mod, 0x822, coin.ToLink<IPlaceableObjectGetter>()));
            mod.Cells.Add(new CellBlock { SubBlocks = new Noggog.ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new Noggog.ExtendedList<Cell> { vault } } } });
        });

    /// <summary>The folders MyMod-1.0 and MyMod-1.1: the plugin, a readme, a loose script and an archive whose mesh
    /// changes and gains a texture-set file.</summary>
    internal static (string Old, string New) ExampleMods(Sandbox s)
    {
        string oldDir = s.Sub("MyMod-1.0"), newDir = s.Sub("MyMod-1.1");
        File.Copy(Plugin(s, "p10", false), Path.Combine(oldDir, "MyMod.esm"));
        File.Copy(Plugin(s, "p11", true), Path.Combine(newDir, "MyMod.esm"));
        Put(oldDir, "MyMod-Readme.txt", Encoding.ASCII.GetBytes("MyMod 1.0\r\n"));
        Put(newDir, "MyMod-Readme.txt", Encoding.ASCII.GetBytes("MyMod 1.1: more coins\r\n"));
        Put(oldDir, "Scripts/MyMod/VaultScript.pex", Script(1));
        Put(newDir, "Scripts/MyMod/VaultScript.pex", Script(2));
        Put(oldDir, "MyMod - Main.ba2", TriageScriptsTests.Ba2(("meshes/mymod/vault.nif", [1, 2, 3], false), ("meshes/mymod/coin.nif", [4], false)));
        Put(newDir, "MyMod - Main.ba2", TriageScriptsTests.Ba2(("meshes/mymod/vault.nif", [1, 2, 3], false), ("meshes/mymod/coin.nif", [4, 5], false),
            ("meshes/mymod/gem.nif", [6], false)));
        if (Environment.GetEnvironmentVariable("SFMODCHECK_DIFF_EXAMPLE") is { Length: > 0 } keep)
            foreach (var d in new[] { oldDir, newDir })
                CopyTree(d, Path.Combine(keep, Path.GetFileName(d)));
        return (oldDir, newDir);
    }

    static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    [Fact]
    public void Example_mods_show_every_part_of_the_report()
    {
        using var s = new Sandbox();
        var (a, b) = ExampleMods(s);
        var r = ExeTests.RunSplit("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, r.Code);
        Assert.Equal("RESULT: DIFFERENCES (1 plugin compared, 1 changed, 0 added, 0 removed; records: 2 added, 1 removed, 3 changed, 0 renumbered, 0 noise; " +
                     "files: 1 added, 0 removed, 3 changed)", ExeTests.ResultLine(r.Stdout));
        var t = r.Stdout;
        Assert.Contains("MYM_VaultCell", t);
        Assert.Contains("MYM_VaultLoot", t);
        Assert.Contains("MYM_Coin -> ", t);   // the base-object swap
        Assert.Contains("functions added (1): OnLoad", t);
    }

    [Fact]
    public void Log_says_what_was_read_paired_and_left_out_for_every_part()
    {
        using var s = new Sandbox();
        var (a, b) = ExampleMods(s);
        var log = Path.Combine(s.Dir, "diff-log.txt");
        var r = ExeTests.RunSplit("diff", a, b, "--quiet", "--data", s.Sub("nodata"), "--log", log);
        Assert.Equal(1, r.Code);
        var t = File.ReadAllText(log);
        foreach (var want in new[]
        {
            "old input: Folder", "new input: 4 files under",                       // what was read
            "paired: mymod.esm with mymod.esm",                                     // plugins
            "MyMod.esm: header: HEDR record count: 16 -> 17 (information only, not a difference)",
            "MyMod.esm: changed MISC 000801:MyMod.esm MYM_Gem: Keywords",
            "MyMod.esm: details: 1 cells with changes",
            "files: changed: mymod-readme.txt",                                     // loose files
            "plugin(s) and 1 archive(s) are left out of the loose-file compare",
            "archives: paired mymod - main.ba2 with mymod - main.ba2",              // archives
            "archives: MyMod - Main.ba2: only in the new version: meshes\\mymod\\gem.nif",
            "archives: MyMod - Main.ba2: changed: meshes\\mymod\\coin.nif",
            "scripts: Scripts\\MyMod\\VaultScript.pex (loose): MyMod:VaultScript changed",   // scripts
            "result: differences, exit code 1",
        })
            Assert.True(t.Contains(want), $"the log lacks \"{want}\":\n{t}");
    }

    /// <summary>Every key name in the JSON of a two-folder run, at any depth.</summary>
    static SortedSet<string> Keys(JsonElement e, SortedSet<string>? into = null)
    {
        into ??= new(StringComparer.Ordinal);
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) { into.Add(p.Name); Keys(p.Value, into); }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) Keys(x, into);
        return into;
    }

    [Fact]
    public void Agents_md_lists_every_json_key()
    {
        using var s = new Sandbox();
        var (a, b) = ExampleMods(s);
        var r = ExeTests.RunSplit("diff", a, b, "--quiet", "--json", "--data", s.Sub("nodata"));
        Assert.Equal(1, r.Code);
        using var doc = JsonDocument.Parse(r.Stdout[..r.Stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)]);
        var keys = Keys(doc.RootElement);
        // the run must reach every part, or keys would be missed: cells with records, lists, swaps, files, archives, scripts
        foreach (var k in new[] { "cells", "records", "leveled_lists", "base_swaps", "files", "archives", "scripts", "functions_added", "moved_from" })
            Assert.Contains(k, keys);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var agents = File.ReadAllText(Path.Combine(dir!.FullName, "AGENTS.md"));
        // the cells' count objects are keyed by record type (REFR, NAVM), which AGENTS.md describes, not lists
        Assert.Contains("keyed by record type", agents);
        var missing = keys.Where(k => !System.Text.RegularExpressions.Regex.IsMatch(k, "^[A-Z0-9_]{4}$") && !agents.Contains("`" + k + "`")).ToList();
        Assert.True(missing.Count == 0, "AGENTS.md doesn't list these diff JSON keys (as `key`): " + string.Join(", ", missing));
    }
}
