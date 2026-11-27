// sf-mod-check 0.26.0: the summary command (SummaryCommand.cs), parts C.1 to C.6 (header and masters, records per type,
// ITMs, changed fields, cells touched and placed references per worldspace). All plugins are made up: a made-up Starfield.esm stands in for the official file.
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace SfModCheck.Tests;

public class SummaryTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Data\Starfield.esm: keywords S_A and S_B, misc items S_Misc (value 5) and S_Part (named, value 7).
    /// mods\Mod.esm: adds M_New; overrides S_A unchanged (an ITM), S_B deleted, S_Misc with value 50, and S_Part as a
    /// partial form with no name and value 70. mods\Mod2.esm (masters Starfield.esm and Mod.esm): adds M2_New and
    /// overrides M_New unchanged (an ITM).</summary>
    sealed class Set : Sandbox
    {
        public readonly string Data, ModDir, Tmp;
        public readonly FormKey SA, SB, SMisc, SPart, MNew;

        public Set()
        {
            Data = Sub("Data");
            ModDir = Sub("mods");
            Tmp = Sub("tmp");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            SA = s.Keywords.AddNew("S_A").FormKey;
            SB = s.Keywords.AddNew("S_B").FormKey;
            var misc = s.MiscItems.AddNew("S_Misc");
            misc.Value = 5;
            SMisc = misc.FormKey;
            var part = s.MiscItems.AddNew("S_Part");
            part.Name = "Part";
            part.Value = 7;
            SPart = part.FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(s, Data, g, NoGap);
            using (var g = new GameData(Data, [ModDir]))
            {
                var sf = g.Open("Starfield.esm");
                var m = Plugin.New("Mod.esm", PluginStyle.Full, "test", "made-up mod");
                MNew = m.Keywords.AddNew("M_New").FormKey;
                m.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_A"));
                m.Keywords.GetOrAddAsOverride(sf.Keywords.First(k => k.EditorID == "S_B")).IsDeleted = true;
                m.MiscItems.GetOrAddAsOverride(sf.MiscItems.First(k => k.EditorID == "S_Misc")).Value = 50;
                var p = m.MiscItems.GetOrAddAsOverride(sf.MiscItems.First(k => k.EditorID == "S_Part"));
                p.Name = null;
                p.Value = 70;
                p.MajorRecordFlagsRaw |= 0x4000;   // a partial form: the empty name keeps the earlier value
                PluginWriter.Write(m, ModDir, g, NoGap);
            }
            using (var g = new GameData(Data, [ModDir]))
            {
                var mod = g.Open("Mod.esm");
                var m2 = Plugin.New("Mod2.esm", PluginStyle.Full, "test", "made-up second mod");
                m2.Keywords.AddNew("M2_New");
                m2.Keywords.GetOrAddAsOverride(mod.Keywords.First(k => k.EditorID == "M_New"));
                PluginWriter.Write(m2, ModDir, g, NoGap);
            }
        }

        public string Zip(string name, params string[] plugins)
        {
            var zip = Path.Combine(Sub("zips"), name);
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var p in plugins) z.CreateEntryFromFile(Path.Combine(ModDir, p), "Data/" + p);
            return zip;
        }

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

        public (int Code, string Stdout, string Stderr) Summary(string plugin, params string[] extra) =>
            Run(new[] { "summary", plugin, "--data", Data }.Concat(extra).ToArray());
    }

    static List<string> Lines(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    [Fact]
    public void Counts_per_type_with_deleted_overrides_and_the_masters_match_the_made_up_plugin()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"));
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("Mod.esm (full plugin)", lines);
        Assert.Contains("  Masters (1):", lines);
        Assert.Contains(lines, l => l.StartsWith("    Starfield.esm (full), in ") && l.EndsWith("Data"));
        Assert.Contains("  Records: 5 records (1 new, 4 overrides, 1 of them deleted)", lines);
        Assert.Contains("    KYWD: 1 new, 2 overrides (1 deleted), 1 ITM", lines);
        Assert.Contains("    MISC: 0 new, 2 overrides", lines);
        Assert.Contains("RESULT: Mod.esm: 5 records (1 new, 4 overrides, 1 deleted), 1 ITM, 1 field changed in 2 overrides", stdout);
    }

    [Fact]
    public void An_itm_is_counted_and_named_and_check_finds_the_same_itm()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"));
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("  ITMs (overrides identical to their master version): 1", lines);
        Assert.Contains("    KYWD (1)", lines);
        Assert.Contains($"      {s.SA} S_A", lines);
        // the deleted override is not an ITM, and its empty fields are not changes
        Assert.DoesNotContain(lines, l => l.Contains(s.SB.ToString()));

        // check (FindItms with no callback) finds the same one ITM
        var (_, co, ce) = s.Run("check", Path.Combine(s.ModDir, "Mod.esm"), "--data", s.Data, "--no-assets");
        var itmLines = Lines(co + ce).Where(l => l.Contains("identical to the master version it overrides")).ToList();
        Assert.True(itmLines.Count == 1, co + ce);
        Assert.Contains("S_A", itmLines[0]);
    }

    [Fact]
    public void A_changed_field_is_counted_with_its_values_and_a_partial_forms_empty_field_is_not()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"));
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("  Changed fields: 1 field changed in 2 overrides", lines);
        Assert.Contains("    MISC Value: 2 overrides", lines);
        Assert.Contains($"      {s.SMisc} S_Misc: 5 -> 50", lines);
        Assert.Contains($"      {s.SPart} S_Part: 7 -> 70", lines);
        // the partial form's empty name keeps the master's: not a change; nor is an unchanged field
        Assert.DoesNotContain(lines, l => l.StartsWith("    MISC Name"));
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^    KYWD [A-Za-z]+: "));

        // --max 1 cuts the examples with the "and more" line
        var (c1, o1, e1) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"), "--max", "1");
        Assert.True(c1 == 0, o1 + e1);
        Assert.Contains("      ... and 1 more (raise --max to see them; --max 0 lists them all)", Lines(o1));
    }

    [Fact]
    public void An_archive_with_two_plugins_gives_two_summaries_in_load_order()
    {
        using var s = new Set();
        var zip = s.Zip("Pair-1.0.zip", "Mod2.esm", "Mod.esm");
        var (code, stdout, stderr) = s.Run("summary", zip, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        var first = lines.IndexOf("Mod.esm (full plugin, from Pair-1.0.zip)");
        var second = lines.IndexOf("Mod2.esm (full plugin, from Pair-1.0.zip)");
        Assert.True(first >= 0 && second > first, stdout);
        Assert.Contains("    Mod.esm (full), in Pair-1.0.zip", lines);
        Assert.Contains($"      {s.MNew} M_New", lines);
        Assert.Contains("RESULT: Mod.esm: 5 records (1 new, 4 overrides, 1 deleted), 1 ITM, 1 field changed in 2 overrides; " +
                        "Mod2.esm: 2 records (1 new, 1 override, 0 deleted), 1 ITM, 0 fields changed in 0 overrides", stdout);
        Assert.False(Directory.EnumerateFileSystemEntries(s.Tmp).Any(), "the temp folder was left behind");
    }

    [Fact]
    public void Json_shape_and_the_cannot_run_cases()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"), "--json");
        Assert.True(code == 0, stdout + stderr);
        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("sf-mod-check summary", root.GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("version").ValueKind);
        Assert.Equal("done", root.GetProperty("result").GetString());
        var p = root.GetProperty("plugins").EnumerateArray().Single();
        Assert.Equal("Mod.esm", p.GetProperty("name").GetString());
        Assert.Equal("full", p.GetProperty("style").GetString());
        var master = p.GetProperty("masters").EnumerateArray().Single();
        Assert.Equal("Starfield.esm", master.GetProperty("name").GetString());
        Assert.Equal("full", master.GetProperty("style").GetString());
        Assert.True(master.GetProperty("found").GetBoolean());
        Assert.Equal(5, p.GetProperty("records").GetInt32());
        var kywd = p.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "KYWD");
        Assert.Equal(1, kywd.GetProperty("new").GetInt32());
        Assert.Equal(2, kywd.GetProperty("override").GetInt32());
        Assert.Equal(1, kywd.GetProperty("deleted").GetInt32());
        Assert.Equal(1, kywd.GetProperty("itm").GetInt32());
        var itms = p.GetProperty("itms");
        Assert.Equal(1, itms.GetProperty("count").GetInt32());
        Assert.Equal(0, itms.GetProperty("notCompared").GetInt32());
        Assert.Equal(JsonValueKind.Number, itms.GetProperty("noMaster").ValueKind);
        var ex = itms.GetProperty("examples").EnumerateArray().Single();
        Assert.Equal(s.SA.ToString(), ex.GetProperty("formKey").GetString());
        Assert.Equal("S_A", ex.GetProperty("editorId").GetString());
        Assert.Equal("KYWD", ex.GetProperty("type").GetString());
        var field = p.GetProperty("fields").EnumerateArray().Single();
        Assert.Equal("MISC", field.GetProperty("type").GetString());
        Assert.Equal("Value", field.GetProperty("field").GetString());
        Assert.Equal(2, field.GetProperty("count").GetInt32());
        var fx = field.GetProperty("examples").EnumerateArray().Single(e => e.GetProperty("formKey").GetString() == s.SMisc.ToString());
        Assert.Equal("5", fx.GetProperty("from").GetString());
        Assert.Equal("50", fx.GetProperty("to").GetString());
        Assert.Equal(JsonValueKind.Array, p.GetProperty("unread").ValueKind);

        // can't run: a file that doesn't exist (JSON and text), no input, two inputs, a load-order option
        var (cx, ox, _) = s.Summary(Path.Combine(s.ModDir, "Nope.esm"), "--json");
        Assert.Equal(2, cx);
        var cant = JsonDocument.Parse(ox).RootElement;
        Assert.Equal("cannot run", cant.GetProperty("result").GetString());
        Assert.Equal("sf-mod-check summary", cant.GetProperty("tool").GetString());
        var (c0, o0, _) = s.Run("summary", "--data", s.Data);
        Assert.Equal(2, c0);
        Assert.Contains("RESULT: CANNOT CHECK (no plugin or archive given)", o0);
        var (c2, o2, _) = s.Run("summary", Path.Combine(s.ModDir, "Mod.esm"), Path.Combine(s.ModDir, "Mod2.esm"));
        Assert.Equal(2, c2);
        Assert.Contains("RESULT: CANNOT CHECK (more than one plugin or archive given)", o2);
        var (cp, op, ep) = s.Run("summary", Path.Combine(s.ModDir, "Mod.esm"), "--plugins", "x.txt");
        Assert.Equal(2, cp);
        Assert.Contains("summary doesn't take --plugins", ep);
        Assert.Contains("RESULT: CANNOT CHECK", op);
    }

    /// <summary>C.5 and C.6. Data\Starfield.esm: a static S_Stat and a misc item S_Misc; worldspace S_World with the
    /// exterior cell S_Ext holding R1 (base S_Stat) and R2 (base S_Misc); the interior cell S_Int holding R3. mods\Places.esm:
    /// moves R1, deletes R2, adds a reference in S_Ext whose base is its own misc item M_Misc, and drops the cell's EditorID
    /// (so it comes from the master); adds a reference in S_Int whose base is missing; adds its own interior cell M_Int
    /// with a reference to S_Stat.</summary>
    sealed class PlaceSet : Sandbox
    {
        public readonly string Data, ModDir, Tmp;
        public readonly FormKey World, Ext, Int, MInt;

        public PlaceSet()
        {
            Data = Sub("Data");
            ModDir = Sub("mods");
            Tmp = Sub("tmp");
            var s = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            var stat = s.Statics.AddNew("S_Stat");
            var misc = s.MiscItems.AddNew("S_Misc");
            var world = s.Worldspaces.AddNew("S_World");
            var ext = new Mutagen.Bethesda.Starfield.Cell(s) { EditorID = "S_Ext", Grid = new Mutagen.Bethesda.Starfield.CellGrid { Point = new Noggog.P2Int(1, 2) } };
            var r1 = new Mutagen.Bethesda.Starfield.PlacedObject(s) { Base = stat.ToLink<Mutagen.Bethesda.Starfield.IPlaceableObjectGetter>() };
            var r2 = new Mutagen.Bethesda.Starfield.PlacedObject(s) { Base = misc.ToLink<Mutagen.Bethesda.Starfield.IPlaceableObjectGetter>() };
            ext.Temporary.Add(r1);
            ext.Temporary.Add(r2);
            world.SubCells.Add(new Mutagen.Bethesda.Starfield.WorldspaceBlock
            {
                BlockNumberX = 0, BlockNumberY = 0, GroupType = Mutagen.Bethesda.Starfield.GroupTypeEnum.ExteriorCellBlock,
                Items = new Noggog.ExtendedList<Mutagen.Bethesda.Starfield.WorldspaceSubBlock> { new() { BlockNumberX = 0, BlockNumberY = 0,
                    GroupType = Mutagen.Bethesda.Starfield.GroupTypeEnum.ExteriorCellSubBlock, Items = new Noggog.ExtendedList<Mutagen.Bethesda.Starfield.Cell> { ext } } },
            });
            var inner = new Mutagen.Bethesda.Starfield.Cell(s) { EditorID = "S_Int", Flags = Mutagen.Bethesda.Starfield.Cell.Flag.IsInteriorCell };
            inner.Temporary.Add(new Mutagen.Bethesda.Starfield.PlacedObject(s) { Base = misc.ToLink<Mutagen.Bethesda.Starfield.IPlaceableObjectGetter>() });
            s.Cells.Add(InteriorBlock(inner));
            (World, Ext, Int) = (world.FormKey, ext.FormKey, inner.FormKey);
            using (var g = new GameData(Data)) PluginWriter.Write(s, Data, g, NoGap);
            using (var g = new GameData(Data, [ModDir]))
            {
                var sf = g.Open("Starfield.esm");
                var cache = sf.ToImmutableLinkCache<Mutagen.Bethesda.Starfield.IStarfieldMod, Mutagen.Bethesda.Starfield.IStarfieldModGetter>();
                var m = Plugin.New("Places.esm", PluginStyle.Full, "test", "made-up mod");
                var mMisc = m.MiscItems.AddNew("M_Misc");
                T Over<T, TGetter>(FormKey fk) where T : class, Mutagen.Bethesda.Starfield.IStarfieldMajorRecord, TGetter
                                                where TGetter : class, Mutagen.Bethesda.Starfield.IStarfieldMajorRecordGetter =>
                    (T)cache.ResolveAllContexts<T, TGetter>(fk).First().GetOrAddAsOverride(m);
                var o1 = Over<Mutagen.Bethesda.Starfield.IPlacedObject, Mutagen.Bethesda.Starfield.IPlacedObjectGetter>(r1.FormKey);
                o1.Position = new Noggog.P3Float(1, 2, 3);
                Over<Mutagen.Bethesda.Starfield.IPlacedObject, Mutagen.Bethesda.Starfield.IPlacedObjectGetter>(r2.FormKey).IsDeleted = true;
                var cExt = Over<Mutagen.Bethesda.Starfield.ICell, Mutagen.Bethesda.Starfield.ICellGetter>(ext.FormKey);
                cExt.EditorID = null;
                cExt.Temporary.Add(new Mutagen.Bethesda.Starfield.PlacedObject(m) { Base = mMisc.ToLink<Mutagen.Bethesda.Starfield.IPlaceableObjectGetter>() });
                var cInt = Over<Mutagen.Bethesda.Starfield.ICell, Mutagen.Bethesda.Starfield.ICellGetter>(inner.FormKey);
                cInt.Temporary.Add(new Mutagen.Bethesda.Starfield.PlacedObject(m)
                    { Base = new FormLink<Mutagen.Bethesda.Starfield.IPlaceableObjectGetter>(new FormKey(ModKey.FromFileName("Starfield.esm"), 0xABCDE)) });
                var own = new Mutagen.Bethesda.Starfield.Cell(m) { EditorID = "M_Int", Flags = Mutagen.Bethesda.Starfield.Cell.Flag.IsInteriorCell };
                own.Temporary.Add(new Mutagen.Bethesda.Starfield.PlacedObject(m) { Base = stat.ToLink<Mutagen.Bethesda.Starfield.IPlaceableObjectGetter>() });
                m.Cells.First().SubBlocks.First().Cells.Add(own);
                MInt = own.FormKey;
                PluginWriter.Write(m, ModDir, g, NoGap);
            }
        }

        static Mutagen.Bethesda.Starfield.CellBlock InteriorBlock(Mutagen.Bethesda.Starfield.Cell c) => new()
        {
            BlockNumber = 0, GroupType = Mutagen.Bethesda.Starfield.GroupTypeEnum.InteriorCellBlock,
            SubBlocks = new Noggog.ExtendedList<Mutagen.Bethesda.Starfield.CellSubBlock> { new() { BlockNumber = 0,
                GroupType = Mutagen.Bethesda.Starfield.GroupTypeEnum.InteriorCellSubBlock, Cells = new Noggog.ExtendedList<Mutagen.Bethesda.Starfield.Cell> { c } } },
        };

        public (int Code, string Stdout, string Stderr) Summary(params string[] extra)
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sf-mod-check.exe" : "sf-mod-check");
            var psi = File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(dir, "sf-mod-check.dll") } };
            foreach (var a in new[] { "summary", Path.Combine(ModDir, "Places.esm"), "--data", Data }.Concat(extra)) psi.ArgumentList.Add(a);
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
    }

    [Fact]
    public void Cells_touched_per_worldspace_and_in_the_interiors_with_their_record_counts()
    {
        using var s = new PlaceSet();
        var (code, stdout, stderr) = s.Summary();
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        Assert.Contains("  Cells touched: 3 cells (1 worldspace, 2 interior)", lines);
        // the worldspace and the exterior cell are overrides; the cell's EditorID comes from the master (the mod's has none)
        Assert.Contains($"    Worldspace {s.World} S_World (override): 1 cell, 4 records", lines);
        Assert.Contains($"      {s.Ext} S_Ext (override): 4 records", lines);
        Assert.Contains("    Interiors: 2 cells, 4 records", lines);
        Assert.Contains($"      {s.Int} S_Int (override): 2 records", lines);
        Assert.Contains($"      {s.MInt} M_Int (new): 2 records", lines);
        Assert.EndsWith(", 3 cells touched", lines.Single(l => l.StartsWith("RESULT: ")));

        // --max 1 caps the cells listed per worldspace; JSON lists them all
        var (c1, o1, e1) = s.Summary("--max", "1");
        Assert.True(c1 == 0, o1 + e1);
        Assert.Contains("      ... and 1 more cells (raise --max to see them; --max 0 lists them all)", Lines(o1));
        var (cj, oj, ej) = s.Summary("--json", "--max", "1");
        Assert.True(cj == 0, oj + ej);
        var p = JsonDocument.Parse(oj).RootElement.GetProperty("plugins").EnumerateArray().Single();
        var w = p.GetProperty("worldspaces").EnumerateArray().Single();
        Assert.Equal(s.World.ToString(), w.GetProperty("formKey").GetString());
        Assert.Equal("S_World", w.GetProperty("editorId").GetString());
        Assert.False(w.GetProperty("own").GetBoolean());
        var ext = w.GetProperty("cells").EnumerateArray().Single();
        Assert.Equal(s.Ext.ToString(), ext.GetProperty("formKey").GetString());
        Assert.Equal("S_Ext", ext.GetProperty("editorId").GetString());
        Assert.False(ext.GetProperty("own").GetBoolean());
        Assert.Equal(4, ext.GetProperty("records").GetInt32());
        var interiors = p.GetProperty("interiors").GetProperty("cells").EnumerateArray().ToList();
        Assert.Equal(2, interiors.Count);
        Assert.Contains(interiors, c => c.GetProperty("formKey").GetString() == s.MInt.ToString() && c.GetProperty("own").GetBoolean());
    }

    [Fact]
    public void Placed_references_per_worldspace_by_base_type_with_a_missing_base()
    {
        using var s = new PlaceSet();
        var (code, stdout, stderr) = s.Summary();
        Assert.True(code == 0, stdout + stderr);
        var lines = Lines(stdout);
        // S_World: R1 moved (STAT), R2 deleted (MISC), a new one with the mod's own misc item as its base
        Assert.True(lines.Contains("      Placed references: 1 new, 2 overrides (1 deleted); by base type: MISC 2, STAT 1"), stdout);
        // interiors: a new one with a base found nowhere, and the new cell's one with S_Stat
        Assert.True(lines.Contains("      Placed references: 2 new, 0 overrides; by base type: (base not found) 1, STAT 1"), stdout);

        var (cj, oj, ej) = s.Summary("--json");
        Assert.True(cj == 0, oj + ej);
        var p = JsonDocument.Parse(oj).RootElement.GetProperty("plugins").EnumerateArray().Single();
        var placed = p.GetProperty("worldspaces").EnumerateArray().Single().GetProperty("placed");
        Assert.Equal(1, placed.GetProperty("new").GetInt32());
        Assert.Equal(2, placed.GetProperty("override").GetInt32());
        Assert.Equal(1, placed.GetProperty("deleted").GetInt32());
        Assert.Equal(2, placed.GetProperty("byBase").GetProperty("MISC").GetInt32());
        Assert.Equal(1, placed.GetProperty("byBase").GetProperty("STAT").GetInt32());
        var inPlaced = p.GetProperty("interiors").GetProperty("placed");
        Assert.Equal(2, inPlaced.GetProperty("new").GetInt32());
        Assert.Equal(0, inPlaced.GetProperty("override").GetInt32());
        Assert.Equal(1, inPlaced.GetProperty("byBase").GetProperty("(base not found)").GetInt32());

        // a plugin with no cells: the line says none, and the JSON has empty lists
        using var plain = new Set();
        var (c0, o0, e0) = plain.Summary(Path.Combine(plain.ModDir, "Mod.esm"));
        Assert.True(c0 == 0, o0 + e0);
        Assert.Contains("  Cells touched: none", Lines(o0));
        var (cn, on, en) = plain.Summary(Path.Combine(plain.ModDir, "Mod.esm"), "--json");
        Assert.True(cn == 0, on + en);
        var pn = JsonDocument.Parse(on).RootElement.GetProperty("plugins").EnumerateArray().Single();
        Assert.Empty(pn.GetProperty("worldspaces").EnumerateArray());
        Assert.Empty(pn.GetProperty("interiors").GetProperty("cells").EnumerateArray());
        Assert.Equal(0, pn.GetProperty("interiors").GetProperty("placed").GetProperty("new").GetInt32());
    }

    /// <summary>Plan step 5: every key of C.8's JSON (with the step 4 additions) is there, with no others, each with the
    /// right kind, on a plugin with ITMs and changed fields, on one with cells and placed references, and from an archive.</summary>
    [Fact]
    public void Json_has_every_key_of_the_design_with_its_kind()
    {
        static void Plugin(JsonElement p)
        {
            JsonShape.Exactly(p, "plugins[]", "name:string", "style:string", "fromArchive:string?", "masters:array", "records:number", "types:array",
                "itms:object", "changedOverrides:number", "fields:array", "worldspaces:array", "interiors:object", "unread:array");
            foreach (var m in p.GetProperty("masters").EnumerateArray()) JsonShape.Exactly(m, "masters[]", "name:string", "style:string?", "found:bool");
            foreach (var t in p.GetProperty("types").EnumerateArray())
                JsonShape.Exactly(t, "types[]", "type:string", "new:number", "override:number", "deleted:number", "itm:number");
            var itms = JsonShape.Exactly(p.GetProperty("itms"), "itms", "count:number", "notCompared:number", "noMaster:number", "examples:array");
            foreach (var e in itms.GetProperty("examples").EnumerateArray()) JsonShape.Exactly(e, "itms.examples[]", "formKey:string", "editorId:string?", "type:string");
            foreach (var f in p.GetProperty("fields").EnumerateArray())
            {
                JsonShape.Exactly(f, "fields[]", "type:string", "field:string", "count:number", "examples:array");
                foreach (var e in f.GetProperty("examples").EnumerateArray())
                    JsonShape.Exactly(e, "fields.examples[]", "formKey:string", "editorId:string?", "from:string", "to:string");
            }
            void Place(JsonElement where, string name)
            {
                foreach (var c in where.GetProperty("cells").EnumerateArray())
                    JsonShape.Exactly(c, name + ".cells[]", "formKey:string", "editorId:string?", "own:bool", "records:number");
                var placed = JsonShape.Exactly(where.GetProperty("placed"), name + ".placed", "new:number", "override:number", "deleted:number", "byBase:object");
                foreach (var b in placed.GetProperty("byBase").EnumerateObject()) Assert.Equal(JsonValueKind.Number, b.Value.ValueKind);
            }
            foreach (var w in p.GetProperty("worldspaces").EnumerateArray())
            {
                JsonShape.Exactly(w, "worldspaces[]", "formKey:string", "editorId:string?", "own:bool", "cells:array", "placed:object");
                Place(w, "worldspaces[]");
            }
            Place(JsonShape.Exactly(p.GetProperty("interiors"), "interiors", "cells:array", "placed:object"), "interiors");
            JsonShape.AllOf(p.GetProperty("unread"), "string", "unread");
        }
        JsonElement Root(string stdout)
        {
            var root = JsonShape.Exactly(JsonDocument.Parse(stdout).RootElement, "root",
                "tool:string", "version:string", "plugins:array", "loadOrderProblems:array", "result:string");
            JsonShape.AllOf(root.GetProperty("loadOrderProblems"), "string", "loadOrderProblems");
            foreach (var p in root.GetProperty("plugins").EnumerateArray()) Plugin(p);
            return root;
        }

        using var s = new Set();
        var (code, stdout, stderr) = s.Summary(Path.Combine(s.ModDir, "Mod.esm"), "--json");
        Assert.True(code == 0, stdout + stderr);
        var mod = Root(stdout).GetProperty("plugins")[0];
        Assert.NotEmpty(mod.GetProperty("itms").GetProperty("examples").EnumerateArray());   // the shapes above were really walked
        Assert.NotEmpty(mod.GetProperty("fields").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, mod.GetProperty("fromArchive").ValueKind);

        var (cz, oz, ez) = s.Run("summary", s.Zip("Pair-1.0.zip", "Mod2.esm", "Mod.esm"), "--data", s.Data, "--json");
        Assert.True(cz == 0, oz + ez);
        var pair = Root(oz).GetProperty("plugins").EnumerateArray().ToList();
        Assert.Equal(2, pair.Count);
        Assert.All(pair, p => Assert.Equal("Pair-1.0.zip", p.GetProperty("fromArchive").GetString()));

        using var places = new PlaceSet();
        var (cp, op, ep) = places.Summary("--json");
        Assert.True(cp == 0, op + ep);
        var pl = Root(op).GetProperty("plugins")[0];
        Assert.NotEmpty(pl.GetProperty("worldspaces").EnumerateArray());
        Assert.NotEmpty(pl.GetProperty("interiors").GetProperty("cells").EnumerateArray());

        var (cx, ox, ex) = s.Summary(Path.Combine(s.ModDir, "Nope.esm"), "--json");
        Assert.True(cx == 2, ox + ex);
        JsonShape.Exactly(JsonDocument.Parse(ox).RootElement, "cannot run", "tool:string", "version:string", "result:string", "error:string");
    }

    [Fact]
    public void Help_exits_0_and_the_top_level_help_names_summary()
    {
        using var s = new Set();
        var (code, stdout, _) = s.Run("summary", "--help");
        Assert.Equal(0, code);
        Assert.Contains("usage: sf-mod-check summary", stdout);
        var (_, top, topErr) = s.Run("--help");
        Assert.Contains("sf-mod-check summary", top + topErr);
    }
}
