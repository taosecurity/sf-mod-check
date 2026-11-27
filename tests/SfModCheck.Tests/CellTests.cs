// sf-mod-check 0.27.0: the cell command (CellCommand.cs; the 0.27.0 design notes, parts A and D). All
// plugins are made up: Base.esm holds an interior cell with persistent and temporary references, an empty second
// interior cell, and a worldspace with a persistent cell and one exterior cell; A.esm and B.esm change, move and delete
// references in it. No game files are needed.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

/// <summary>The made-up game, built once for every cell test (the runs only read it).</summary>
public sealed class CellGame : Sandbox
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    public readonly string Data;
    public readonly FormKey Vault, Other, World, Top, Ext, Marker, Boss, Crate, Moved, Gone, FlagOnly, ANew, P1, E1, E2, E3;

    public CellGame()
    {
        Data = Sub("Data");
        var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
        var xmarker = b.Statics.AddNew("B_XMarkerHeading");
        var crate = b.Containers.AddNew("B_Crate");
        var misc = b.MiscItems.AddNew("B_Misc");
        var npc = b.Npcs.AddNew("B_Npc");
        var bossType = b.LocationReferenceTypes.AddNew("B_BossRefType");
        var loc = b.Locations.AddNew("B_Loc");
        var worldLoc = b.Locations.AddNew("B_WorldLoc");

        var vault = new Cell(b) { EditorID = "B_Vault", Flags = Cell.Flag.IsInteriorCell, Name = "The Vault", Location = loc.ToNullableLink<ILocationGetter>() };
        var marker = new PlacedObject(b) { EditorID = "B_EnableMarker", Base = xmarker.ToLink<IPlaceableObjectGetter>(), MajorRecordFlagsRaw = 0x400 };
        var boss = new PlacedNpc(b) { EditorID = "B_Boss", Base = npc.ToLink<INpcGetter>(), MajorRecordFlagsRaw = 0x400, Position = new P3Float(5, 6, 7) };
        boss.LocationRefTypes = new ExtendedList<IFormLinkGetter<ILocationReferenceTypeGetter>> { bossType.ToLink<ILocationReferenceTypeGetter>() };
        vault.Persistent.Add(marker);
        vault.Persistent.Add(boss);
        var crateRef = new PlacedObject(b)
        {
            Base = crate.ToLink<IPlaceableObjectGetter>(), Position = new P3Float(1, 2, 0), MajorRecordFlagsRaw = 0x800,
            EnableParent = new EnableParent { Reference = marker.ToLink<IPlacedGetter>(), Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent },
        };
        var moved = new PlacedObject(b) { EditorID = "B_MovedMisc", Base = misc.ToLink<IPlaceableObjectGetter>() };
        var gone = new PlacedObject(b) { EditorID = "B_GoneMisc", Base = misc.ToLink<IPlaceableObjectGetter>() };
        var flagOnly = new PlacedObject(b) { EditorID = "B_FlagOnly", Base = misc.ToLink<IPlaceableObjectGetter>(), MajorRecordFlagsRaw = 0x400 };
        vault.Temporary.Add(crateRef);
        vault.Temporary.Add(moved);
        vault.Temporary.Add(gone);
        vault.Temporary.Add(flagOnly);
        vault.NavigationMeshes.Add(new NavigationMesh(b)
        {
            NavmeshGeometry = new NavmeshGeometry
            {
                Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(vault.FormKey) },
                Vertices = new ExtendedList<NavmeshVertex> { new NavmeshVertex { Point = new P3Float(0, 0, 0) } },
            },
        });
        var other = new Cell(b) { EditorID = "B_Other", Flags = Cell.Flag.IsInteriorCell };
        b.Cells.Add(new CellBlock
        {
            BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = new ExtendedList<Cell> { vault, other } } },
        });

        var world = b.Worldspaces.AddNew("B_World");
        world.Location = worldLoc.ToNullableLink<ILocationGetter>();
        var top = new Cell(b) { EditorID = "B_WorldPersistent" };
        var p1 = new PlacedObject(b) { EditorID = "B_P1", Base = xmarker.ToLink<IPlaceableObjectGetter>(), Position = new P3Float(100, 0, 0), MajorRecordFlagsRaw = 0x400 };
        top.Persistent.Add(p1);
        world.TopCell = top;
        var ext = new Cell(b) { EditorID = "B_Ext", Grid = new CellGrid { Point = new P2Int(1, 2) } };
        var e1 = new PlacedObject(b) { EditorID = "B_E1", Base = misc.ToLink<IPlaceableObjectGetter>(), Position = new P3Float(103, 4, 50) };      // exactly 5 away
        var e2 = new PlacedObject(b) { EditorID = "B_E2", Base = misc.ToLink<IPlaceableObjectGetter>(), Position = new P3Float(103.01f, 4, 0) };   // just beyond 5
        var e3 = new PlacedObject(b) { EditorID = "B_E3", Base = misc.ToLink<IPlaceableObjectGetter>(), Position = new P3Float(100, 0, 9999) };    // straight above
        ext.Temporary.Add(e1);
        ext.Temporary.Add(e2);
        ext.Temporary.Add(e3);
        world.SubCells.Add(new WorldspaceBlock
        {
            BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock,
            Items = new ExtendedList<WorldspaceSubBlock> { new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = new ExtendedList<Cell> { ext } } },
        });
        (Vault, Other, World, Top, Ext) = (vault.FormKey, other.FormKey, world.FormKey, top.FormKey, ext.FormKey);
        (Marker, Boss, Crate, Moved, Gone, FlagOnly) = (marker.FormKey, boss.FormKey, crateRef.FormKey, moved.FormKey, gone.FormKey, flagOnly.FormKey);
        (P1, E1, E2, E3) = (p1.FormKey, e1.FormKey, e2.FormKey, e3.FormKey);
        using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);

        // A.esm: changes the crate and adds a reference of its own in the vault
        using (var g = new GameData(Data))
        {
            var bm = g.Open("Base.esm");
            var cache = bm.ToImmutableLinkCache();
            var a = Plugin.New("A.esm", PluginStyle.Light, "test", "made-up mod A");
            var c = RefIn(a, cache, Crate);
            c.Position = new P3Float(1, 2, 5);
            var vaultA = (Cell)cache.ResolveAllContexts<ICell, ICellGetter>(Vault).First().GetOrAddAsOverride(a);
            var aNew = new PlacedObject(a) { EditorID = "A_New", Base = new FormLink<IPlaceableObjectGetter>(misc.FormKey) };
            vaultA.Temporary.Add(aNew);
            ANew = aNew.FormKey;
            PluginWriter.Write(a, Data, g, NoGap);
        }
        // B.esm: changes the crate again, moves B_MovedMisc to B_Other and deletes B_GoneMisc
        using (var g = new GameData(Data))
        {
            var bm = g.Open("Base.esm");
            var cache = bm.ToImmutableLinkCache();
            var bb = Plugin.New("B.esm", PluginStyle.Light, "test", "made-up mod B");
            RefIn(bb, cache, Crate).Position = new P3Float(1, 2, 7);
            RefIn(bb, cache, Gone).IsDeleted = true;
            var otherB = (Cell)cache.ResolveAllContexts<ICell, ICellGetter>(Other).First().GetOrAddAsOverride(bb);
            var movedCopy = (PlacedObject)cache.Resolve<IPlacedObjectGetter>(Moved).DeepCopy();
            movedCopy.Position = new P3Float(9, 9, 9);
            otherB.Temporary.Add(movedCopy);
            PluginWriter.Write(bb, Data, g, NoGap);
        }
    }

    static PlacedObject RefIn(StarfieldMod mod, ILinkCache<IStarfieldMod, IStarfieldModGetter> cache, FormKey fk) =>
        (PlacedObject)cache.ResolveAllContexts<IPlacedObject, IPlacedObjectGetter>(fk).First().GetOrAddAsOverride(mod);

    public string PluginsTxt()
    {
        var txt = Path.Combine(Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*Base.esm", "*A.esm", "*B.esm"]);
        return txt;
    }

    public (int Code, string Stdout, string Stderr) Run(params string[] args) =>
        ExeTests.RunSplit(["cell", .. args, "--data", Data, "--plugins", PluginsTxt(), "--quiet"]);
}

public class CellTests(CellGame game) : IClassFixture<CellGame>
{
    static string Line(string stdout, string start) =>
        stdout.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.TrimStart().StartsWith(start, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"no line starting \"{start}\" in:\n{stdout}");

    [Fact]
    public void InteriorCellByEditorId()
    {
        var (code, stdout, stderr) = game.Run("B_Vault");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"Cell B_Vault [{game.Vault}] \"The Vault\" (interior)", stdout);
        Assert.Contains("location: B_Loc [", stdout);
        Assert.DoesNotContain("(from the worldspace)", stdout);
        Assert.Contains("added by Base.esm; changed by A.esm, B.esm", stdout);
        // the marker, the boss, the crate, the flag-only reference and A's new one; moved and deleted ones aren't counted
        Assert.Contains("references: 5 (persistent 3, temporary 2; initially disabled 1), navmeshes 1", stdout);
        Assert.Contains("by base type: MISC 2, CONT 1, NPC_ 1, STAT 1", stdout);
        Assert.Contains($"enable parents: B_EnableMarker [{game.Marker}] 1 child", stdout);
        var crate = Line(stdout, $"[{game.Crate}] REFR");
        Assert.Contains("base B_Crate [", crate);
        Assert.Contains("(CONT) at (1.0, 2.0, 7.0)", crate);   // B's copy wins
        Assert.Contains($"temporary, disabled, parent B_EnableMarker [{game.Marker}] (opposite)", crate);
        Assert.Contains("; added by Base.esm; changed by A.esm, B.esm", crate);
        var boss = Line(stdout, $"B_Boss [{game.Boss}] ACHR");
        Assert.Contains("base B_Npc [", boss);
        Assert.Contains("(NPC_)", boss);
        Assert.Contains(" persistent; location ref types: B_BossRefType [", boss);
        Assert.Contains("persistent (flag only: in the temporary list)", Line(stdout, $"B_FlagOnly [{game.FlagOnly}]"));
        Assert.Contains("added by A.esm", Line(stdout, $"A_New [{game.ANew}]"));
        Assert.DoesNotContain("changed by", Line(stdout, $"A_New [{game.ANew}]"));
        Assert.Contains("Moved out (1):", stdout);
        Assert.Contains($"[{game.Moved}] to B_Other [{game.Other}] by B.esm", stdout);
        Assert.Contains("Deleted (1):", stdout);
        Assert.Contains($"B_GoneMisc [{game.Gone}] deleted by B.esm", stdout);
        Assert.DoesNotContain($"B_MovedMisc [{game.Moved}] REFR", stdout);
        Assert.Matches($@"RESULT: B_Vault \[{System.Text.RegularExpressions.Regex.Escape(game.Vault.ToString())}\] holds 5 references, 3 persistent \(\d+ s\)\s*$", stdout);
    }

    [Fact]
    public void AnchorsOrder()
    {
        var (code, stdout, _) = game.Run("B_Vault");
        Assert.Equal(0, code);
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int at = lines.IndexOf("Persistent anchors (3):");
        Assert.True(at >= 0, stdout);
        // location reference types first, then a base named like a marker, then the rest
        Assert.StartsWith($"  B_Boss [{game.Boss}] base B_Npc", lines[at + 1]);
        Assert.StartsWith($"  B_EnableMarker [{game.Marker}] base B_XMarkerHeading", lines[at + 2]);
        Assert.StartsWith($"  B_FlagOnly [{game.FlagOnly}] base B_Misc", lines[at + 3]);
    }

    [Fact]
    public void FormKeyAndConsoleFormIdFindTheSameCell()
    {
        var byKey = game.Run(game.Vault.ToString());
        Assert.True(byKey.Code == 0, byKey.Stdout + byKey.Stderr);
        Assert.Contains($"Cell B_Vault [{game.Vault}]", byKey.Stdout);
        // Base.esm is the first full plugin, so its console FormIDs start with 00
        var byFid = game.Run($"{game.Vault.ID:X8}");
        Assert.True(byFid.Code == 0, byFid.Stdout + byFid.Stderr);
        Assert.Contains($"Cell B_Vault [{game.Vault}]", byFid.Stdout);
        Assert.Contains("references: 5 ", byFid.Stdout);
    }

    [Fact]
    public void RefListsTheCellOfItsWinningCopy()
    {
        // B moved B_MovedMisc to B_Other, so --ref lists B_Other, with the reference marked
        var (code, stdout, stderr) = game.Run("--ref", "B_MovedMisc");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"Cell B_Other [{game.Other}]", stdout);
        var moved = Line(stdout, $"B_MovedMisc [{game.Moved}] REFR");
        Assert.Contains("at (9.0, 9.0, 9.0)", moved);
        Assert.Contains("changed by B.esm", moved);
        Assert.EndsWith("<- asked", moved);
        Assert.Contains("added by Base.esm; changed by B.esm", stdout);   // the cell record itself

        var (code2, stdout2, _) = game.Run("--ref", game.Boss.ToString());
        Assert.Equal(0, code2);
        Assert.Contains($"Cell B_Vault [{game.Vault}]", stdout2);
        Assert.EndsWith("<- asked", Line(stdout2, $"B_Boss [{game.Boss}]"));
        Assert.DoesNotContain("<- asked", Line(stdout2, $"B_EnableMarker [{game.Marker}] REFR"));
    }

    [Fact]
    public void NearKeepsTheEdgeAndIgnoresHeight()
    {
        var (code, stdout, stderr) = game.Run("--near", "B_World", "100", "0", "5");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"Near (100.0, 0.0) in worldspace B_World [{game.World}], radius 5.0: 3 references in 2 cells", stdout);
        Assert.Contains($"B_P1 [{game.P1}]", stdout);       // the persistent cell
        Assert.Contains($"B_E3 [{game.E3}]", stdout);       // straight above: height ignored
        Assert.Contains($"B_E1 [{game.E1}]", stdout);       // exactly at the radius: kept
        Assert.DoesNotContain($"B_E2 [{game.E2}]", stdout); // 5.0099 away: left out
        var e1 = Line(stdout, "5.0  B_E1");
        Assert.Contains($"; in B_Ext [{game.Ext}]", e1);
        Assert.Contains($"; in B_WorldPersistent [{game.Top}]", Line(stdout, "0.0  B_P1"));
        // nearest first: both at 0.0 come before B_E1
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r').TrimStart()).ToList();
        int p1 = lines.FindIndex(l => l.StartsWith("0.0  B_P1")), e3 = lines.FindIndex(l => l.StartsWith("0.0  B_E3")), e1at = lines.FindIndex(l => l.StartsWith("5.0  B_E1"));
        Assert.True(p1 >= 0 && e3 >= 0 && e1at > p1 && e1at > e3, stdout);
        Assert.Contains("RESULT: 3 references within 5.0 of (100.0, 0.0) in B_World", stdout);
    }

    [Fact]
    public void NearWithNothingInRangeIsStillFound()
    {
        var (code, stdout, _) = game.Run("--near", game.World.ToString(), "-5000", "-5000", "1");
        Assert.Equal(0, code);
        Assert.Contains("References (0): no references within 1.0.", stdout);
        Assert.Contains("RESULT: no references within 1.0 of (-5000.0, -5000.0)", stdout);
    }

    [Fact]
    public void ExteriorCellTakesTheWorldspaceLocation()
    {
        var (code, stdout, stderr) = game.Run("B_Ext");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"Cell B_Ext [{game.Ext}] (worldspace B_World [{game.World}], grid 1,2)", stdout);
        Assert.Contains("location: B_WorldLoc [", stdout);
        Assert.Contains("(from the worldspace)", stdout);
        Assert.Contains("references: 3 (persistent 0, temporary 3; initially disabled 0), navmeshes 0", stdout);
        var (code2, stdout2, _) = game.Run("B_WorldPersistent");
        Assert.Equal(0, code2);
        Assert.Contains($"(persistent cell of worldspace B_World [{game.World}])", stdout2);
    }

    [Fact]
    public void MaxCapsTheListsButNotTheCounts()
    {
        var (code, stdout, _) = game.Run("B_Vault", "--max", "1");
        Assert.Equal(0, code);
        Assert.Contains("references: 5 (persistent 3,", stdout);
        Assert.Contains("References (5):", stdout);
        Assert.Contains("... and 4 more (--max 0 lists them all)", stdout);
        Assert.Contains("Persistent anchors (3):", stdout);
        Assert.Contains("... and 2 more (--max 0 lists them all)", stdout);
        var (_, all, _) = game.Run("B_Vault", "--max", "0");
        Assert.DoesNotContain("more (--max 0", all);
    }

    [Fact]
    public void WrongTypeNotFoundAndBadArguments()
    {
        var crate = game.Run(game.Crate.ToString());
        Assert.Equal(2, crate.Code);
        Assert.Contains("is a REFR, not a cell; for a reference use --ref", crate.Stderr);
        var cellAsRef = game.Run("--ref", "B_Vault");
        Assert.Equal(2, cellAsRef.Code);
        Assert.Contains("is a CELL, not a placed reference; for a cell leave out --ref", cellAsRef.Stderr);
        var notWorld = game.Run("--near", "B_Vault", "0", "0", "10");
        Assert.Equal(2, notWorld.Code);
        Assert.Contains("is a CELL, not a worldspace", notWorld.Stderr);
        var none = game.Run("B_NoSuchCell");
        Assert.Equal(2, none.Code);
        Assert.Contains("RESULT: NOT FOUND (no cell with EditorID B_NoSuchCell in 3 plugins)", none.Stdout);
        var two = game.Run("B_Vault", "--ref", "B_Boss");
        Assert.Equal(2, two.Code);
        Assert.Contains("not two of them", two.Stderr);
        var nothing = ExeTests.RunSplit("cell", "--data", game.Data, "--plugins", game.PluginsTxt(), "--quiet");
        Assert.Equal(2, nothing.Code);
        Assert.Contains("cell needs one of", nothing.Stderr);
        foreach (var bad in new[] { "0", "-3", "abc", "NaN", "Infinity" })
        {
            var r = game.Run("--near", "B_World", "0", "0", bad);
            Assert.True(r.Code == 2, $"radius {bad}: {r.Stdout}{r.Stderr}");
            Assert.Contains("the radius must be a number above 0", r.Stderr);
        }
        var shortNear = game.Run("--near", "B_World", "0", "0");
        Assert.Equal(2, shortNear.Code);
        Assert.Contains("--near needs four values", shortNear.Stderr);
    }

    [Fact]
    public void JsonShape()
    {
        var (code, stdout, stderr) = game.Run("B_Vault", "--json");
        Assert.True(code == 0, stdout + stderr);
        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("sf-mod-check cell", root.GetProperty("tool").GetString());
        Assert.Equal("found", root.GetProperty("result").GetString());
        var cell = root.GetProperty("cell");
        Assert.Equal(game.Vault.ToString(), cell.GetProperty("formKey").GetString());
        Assert.True(cell.GetProperty("interior").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("near").ValueKind);
        var counts = root.GetProperty("counts");
        Assert.Equal(5, counts.GetProperty("references").GetInt32());
        Assert.Equal(1, counts.GetProperty("navmeshes").GetInt32());
        Assert.Equal(2, counts.GetProperty("byBaseType").GetProperty("MISC").GetInt32());
        Assert.Equal(1, counts.GetProperty("enableParents")[0].GetProperty("children").GetInt32());
        var refs = root.GetProperty("references").EnumerateArray().ToList();
        Assert.Equal(5, refs.Count);
        var crate = refs.Single(r => r.GetProperty("formKey").GetString() == game.Crate.ToString());
        Assert.True(crate.GetProperty("enableParentOpposite").GetBoolean());
        Assert.True(crate.GetProperty("initiallyDisabled").GetBoolean());
        Assert.Equal(7, crate.GetProperty("position")[2].GetDouble());
        Assert.Equal(new[] { "A.esm", "B.esm" }, crate.GetProperty("changedBy").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(crate.TryGetProperty("distance", out _));
        Assert.Equal("B.esm", root.GetProperty("movedOut")[0].GetProperty("by").GetString());
        Assert.Equal(game.Gone.ToString(), root.GetProperty("deleted")[0].GetProperty("formKey").GetString());
        Assert.Equal("B_Boss", root.GetProperty("anchors")[0].GetProperty("editorId").GetString());
        Assert.Equal(5, root.GetProperty("listed").GetInt32());
        Assert.Equal(5, root.GetProperty("total").GetInt32());

        var near = game.Run("--near", "B_World", "100", "0", "5", "--json");
        Assert.Equal(0, near.Code);
        var nr = JsonDocument.Parse(near.Stdout).RootElement;
        Assert.Equal(JsonValueKind.Null, nr.GetProperty("cell").ValueKind);
        Assert.Equal(5, nr.GetProperty("near").GetProperty("radius").GetDouble());
        Assert.Equal(5.0, nr.GetProperty("references").EnumerateArray().Last().GetProperty("distance").GetDouble());

        var ask = game.Run("--ref", "B_Boss", "--json");
        var boss = JsonDocument.Parse(ask.Stdout).RootElement.GetProperty("references").EnumerateArray()
            .Single(r => r.GetProperty("formKey").GetString() == game.Boss.ToString());
        Assert.True(boss.GetProperty("asked").GetBoolean());

        var nf = game.Run("B_NoSuchCell", "--json");
        Assert.Equal(2, nf.Code);
        Assert.Equal("not found", JsonDocument.Parse(nf.Stdout).RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void PluginWithItsMasters()
    {
        var (code, stdout, stderr) = ExeTests.RunSplit("cell", "B_Vault", "--plugin", Path.Combine(game.Data, "B.esm"), "--data", game.Data, "--quiet");
        Assert.True(code == 0, stdout + stderr);
        // B.esm and its master Base.esm: A's crate change and A's new reference aren't in this load order
        Assert.Contains("added by Base.esm; changed by B.esm", stdout);
        Assert.Contains("references: 4 (persistent 3, temporary 1; initially disabled 1)", stdout);
        var fid = ExeTests.RunSplit("cell", "0000080A", "--plugin", Path.Combine(game.Data, "B.esm"), "--data", game.Data, "--quiet");
        Assert.Equal(2, fid.Code);
        Assert.Contains("console FormID", fid.Stderr);
    }

    [Fact]
    public void VerboseLogNamesEachPluginWalked()
    {
        var log = Path.Combine(game.Dir, "cell.log");
        var (code, _, _) = ExeTests.RunSplit("cell", "B_Vault", "--data", game.Data, "--plugins", game.PluginsTxt(), "--quiet", "--log", log);
        Assert.Equal(0, code);
        var text = File.ReadAllText(log);
        Assert.Contains("cell: Base.esm: 1 wanted cell(s), 6 references", text);
        Assert.Contains("cell: A.esm: 1 wanted cell(s), 2 references", text);
        // the second walk reads only plugins that may override a reference they didn't add: B finds the moved one
        Assert.Contains("cell: A.esm: 0 copies in other cells", text);
        Assert.Contains("cell: B.esm: 1 copies in other cells", text);
        Assert.DoesNotContain("cell: Base.esm: 0 copies in other cells", text);
        Assert.Contains("cell: 5 references kept, 1 moved out, 1 deleted, 3 anchors", text);
    }
}
