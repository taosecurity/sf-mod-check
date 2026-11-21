// 0.10.0 item 2: the ships listing (Ships.cs). A made-up master holds the actor values, keywords, markers and modules
// the rule reads (named as in the official masters) and one ship; the mod adds a full ship, a ship with no fuel tank and
// no docker, a one-piece hull, and overrides the master's ship; the second test covers the ships WARNING's limits.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class ShipsTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    const string Master = "TestShipParts.esm";

    sealed class Yard : Sandbox
    {
        public readonly Dictionary<string, FormKey> Modules = new();
        public FormKey MasterShip;

        static FormLinkComponentLink Link(Keyword kw, IStarfieldMajorRecordGetter to) =>
            new() { Keyword = new FormLinkNullable<IKeywordGetter>(kw.FormKey), LinkedForm = new FormLinkNullable<IStarfieldMajorRecordGetter>(to.FormKey) };

        public Yard()
        {
            var m = Plugin.New(Master, PluginStyle.Full, "test", "made-up ship parts");
            var av = new Dictionary<string, ActorValueInformation>();
            foreach (var e in new[] { "SpaceshipMaxAvailablePower", "ShipSystemEngineHealth", "ShipSystemGravDriveHealth", "ShipSystemShieldsHealth",
                                      "SpaceshipGravJumpFuel", "SpaceshipLanderRating", "SpaceshipTargetLockTime" })
                av[e] = m.ActorValueInformation.AddNew(e);
            var classA = m.Keywords.AddNew("ShipModuleClassA");
            var classB = m.Keywords.AddNew("ShipModuleClassB");
            var weaponKw = m.Keywords.AddNew("SpaceshipPartLinkedWeapon");
            var exteriorKw = m.Keywords.AddNew("SpaceshipLinkedExterior");
            var weap = m.Weapons.AddNew("TS_ShipGun");
            var cells = new ExtendedList<Cell>();
            PackIn Exterior(string marker)
            {
                var st = m.Statics.AddNew(marker);
                var cell = new Cell(m) { EditorID = marker + "_Cell" };
                cell.Temporary.Add(new PlacedObject(m) { Base = st.ToLink<IPlaceableObjectGetter>() });
                cells.Add(cell);
                var pk = m.PackIns.AddNew(marker + "_PackIn");
                pk.Cell.SetTo(cell);
                return pk;
            }
            var dockPack = Exterior("ShipDockingOffsetMarker01");
            var bayPack = Exterior("ShipLandingOffsetMarker01");
            m.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = cells } } });

            GenericBaseForm Module(string edid, string[] avs, Keyword? cls = null, params FormLinkComponentLink[] links)
            {
                var g = m.GenericBaseForms.AddNew(edid);
                if (avs.Length > 0)
                    g.Components.Add(new PropertySheetComponent { Properties = new ExtendedList<ObjectProperty>(avs.Select(a => new ObjectProperty { ActorValue = av[a].ToLink<IActorValueInformationGetter>(), Value = 1 })) });
                if (cls != null) g.Components.Add(new KeywordFormComponent { Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { cls.ToLink<IKeywordGetter>() } });
                if (links.Length > 0) g.Components.Add(new FormLinkDataComponent { Links = new ExtendedList<FormLinkComponentLink>(links) });
                Modules[edid] = g.FormKey;
                return g;
            }
            Module("TS_Cockpit", ["SpaceshipTargetLockTime"]);
            Module("TS_Reactor_A", ["SpaceshipMaxAvailablePower"], classA);
            Module("TS_Reactor_B", ["SpaceshipMaxAvailablePower"], classB);
            Module("TS_GravDrive", ["ShipSystemGravDriveHealth"], classB);   // a class keyword on a non-reactor: the class still comes from the reactor
            Module("TS_Engine", ["ShipSystemEngineHealth"]);
            Module("TS_FuelTank", ["SpaceshipGravJumpFuel"]);
            Module("TS_Lander", ["SpaceshipLanderRating"]);
            Module("TS_Shield", ["ShipSystemShieldsHealth"]);
            Module("TS_Docker", [], null, Link(exteriorKw, dockPack));
            Module("TS_Bay", [], null, Link(exteriorKw, bayPack));
            Module("TS_Weapon", [], null, Link(weaponKw, weap));
            Module("TS_Hab", []);
            Module("TS_Hull", ["SpaceshipTargetLockTime", "SpaceshipMaxAvailablePower", "ShipSystemEngineHealth"], classA);
            MasterShip = Ship(m, "TS_MasterShip", Full, (1, 2, 3)).FormKey;
            using var gd = new GameData(Dir);
            PluginWriter.Write(m, Dir, gd, NoGap);
        }

        public static readonly string[] Full =
            ["TS_Cockpit", "TS_Reactor_A", "TS_GravDrive", "TS_Engine", "TS_Engine", "TS_FuelTank", "TS_Lander", "TS_Docker", "TS_Bay", "TS_Shield", "TS_Weapon", "TS_Hab"];

        /// <summary>A ship base form made of the named modules (part IDs 1, 2, ... in order) with the given slots.</summary>
        public GenericBaseForm Ship(StarfieldMod into, string edid, string[] modules, (int, int, int) slots, FormKey? key = null)
        {
            var g = key is { } k ? new GenericBaseForm(k, StarfieldRelease.Starfield) { EditorID = edid } : into.GenericBaseForms.AddNew(edid);
            g.Components.Add(new BlueprintComponent
            {
                Items = new ExtendedList<BlueprintComponentItem>(modules.Select((n, i) => new BlueprintComponentItem
                    { BaseItem = new FormLink<IGenericBaseFormGetter>(Modules[n]), PartID = (uint)(i + 1) })),
            });
            g.Components.Add(new SpaceshipWeaponBindingsComponent { Slot1 = slots.Item1, Slot2 = slots.Item2, Slot3 = slots.Item3 });
            if (key != null) into.GenericBaseForms.Add(g);
            return g;
        }

        public string Mod(Action<StarfieldMod> build, string name = "shipmod.esm")
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up ship mod");
            build(mod);
            using var g = new GameData(Dir);
            g.Open(Master);
            var dir = Sub("build");
            PluginWriter.Write(mod, dir, g, NoGap);
            var final = Path.Combine(Dir, name);
            File.Move(Path.Combine(dir, name), final);
            return final;
        }

        public Result Check(string path) => Checker.Run(new Options { Plugin = path, DataDir = Dir, MastersDirs = [Dir], NoAssets = true });
    }

    [Fact]
    public void Ships_are_listed_by_system_with_class_slots_and_missing_systems()
    {
        using var y = new Yard();
        var path = y.Mod(m =>
        {
            y.Ship(m, "TS_FullShip", Yard.Full, (11, 2, 0));   // slot 1: the weapon; slot 2: the reactor; slot 3: empty
            y.Ship(m, "TS_NoFuelNoDocker", ["TS_Cockpit", "TS_Reactor_B", "TS_GravDrive", "TS_Engine", "TS_Lander", "TS_Bay", "TS_Weapon"], (7, 99, -1));
            y.Ship(m, "TS_StarbornLike", ["TS_Hull", "TS_Weapon"], (2, 0, 0));
            y.Ship(m, "TS_MasterShip", Yard.Full.Where(n => n != "TS_Shield").ToArray(), (11, 0, 0), y.MasterShip);
        });
        var r = y.Check(path);
        Assert.Equal(4, r.ShipListings.Count);
        // Only the added ship with a cockpit and missing systems warns (not the complete one, the one-piece hull or the override).
        var warn = Assert.Single(r.Findings, f => f.Check == "ships");
        Assert.Equal("WARNING", warn.Severity);
        Assert.StartsWith("TS_NoFuelNoDocker [GBFM:", warn.Text);
        Assert.EndsWith("has a cockpit but lacks required ship systems: fuel tank, docker. Every official player ship has them, " +
                        "and the Spaceship Editor's flight check asks for them; if only NPCs fly this ship, ignore this", warn.Text);
        var by = r.ShipListings.ToDictionary(s => s.Ship.Split(' ')[0]);

        var full = by["TS_FullShip"];
        Assert.True(full.Complete);
        Assert.Equal("A", full.Class);
        Assert.Equal("added", full.Origin);
        Assert.Equal(["part 11 = TS_Weapon", "part 2 = TS_Reactor_A (not a weapon)", "none"], full.Slots);
        var line = Ships.Line(full);
        Assert.Contains("(added, 12 modules): class A; cockpit: TS_Cockpit; reactor: TS_Reactor_A; grav drive: TS_GravDrive; engine: TS_Engine x2; fuel tank: TS_FuelTank;", line);
        Assert.Contains("docker: TS_Docker; landing bay: TS_Bay; shield: TS_Shield; weapon: TS_Weapon;", line);
        Assert.EndsWith("weapon slots: 1 = part 11 = TS_Weapon, 2 = part 2 = TS_Reactor_A (not a weapon), 3 = none; every required system present", line);

        var gap = by["TS_NoFuelNoDocker"];
        Assert.Equal(["fuel", "docker"], gap.Missing);
        Assert.Equal("B", gap.Class);
        Assert.Contains("fuel tank: none;", Ships.Line(gap));
        Assert.EndsWith("weapon slots: 1 = part 7 = TS_Weapon, 2 = part 99 (no such part), 3 = none; missing required: fuel tank, docker; no shield (listed only)", Ships.Line(gap));

        var hull = by["TS_StarbornLike"];
        Assert.Empty(hull.Missing);
        Assert.False(hull.Complete);
        Assert.EndsWith("one-piece hull (TS_Hull covers 3 systems), systems not checked", Ships.Line(hull));

        var ov = by["TS_MasterShip"];
        Assert.Equal($"overrides {Master}", ov.Origin);
        Assert.Empty(ov.Missing);
        Assert.True(ov.NoShield);
        Assert.EndsWith("every required system present; no shield (listed only)", Ships.Line(ov));

        Assert.Equal("SHIPS: 4 ships listed: 2 with every required system, 1 missing a required system, 1 one-piece hull (not checked). " +
                     "The listing (modules by system, class, weapon slots, missing systems) is in the log: run again with --verbose or --log <file>.",
                     Ships.Summary(r.ShipListings));
    }

    [Fact]
    public void The_ships_warning_skips_overrides_ships_without_a_cockpit_and_official_plugins()
    {
        using var y = new Yard();
        string[] noLander = Yard.Full.Where(n => n != "TS_Lander").ToArray();
        string[] noCockpit = Yard.Full.Where(n => n is not ("TS_Cockpit" or "TS_FuelTank")).ToArray();
        void Build(StarfieldMod m)
        {
            y.Ship(m, "TS_Complete", Yard.Full, (11, 0, 0));
            y.Ship(m, "TS_NoLander", noLander, (10, 0, 0));
            y.Ship(m, "TS_Turret", noCockpit, (0, 0, 0));                              // no cockpit: not a flyable ship, no WARNING
            y.Ship(m, "TS_MasterShip", noLander, (0, 0, 0), y.MasterShip);             // an override lacking a system: listing only
        }
        var r = y.Check(y.Mod(Build));
        Assert.Equal(4, r.ShipListings.Count);
        var warn = Assert.Single(r.Findings, f => f.Check == "ships");
        Assert.StartsWith("TS_NoLander [GBFM:", warn.Text);
        Assert.Contains("lacks required ship system: landing gear.", warn.Text);
        var by = r.ShipListings.ToDictionary(s => s.Ship.Split(' ')[0]);
        Assert.Equal(["lander"], by["TS_MasterShip"].Missing);
        Assert.False(by["TS_MasterShip"].Warns);
        Assert.Equal(["cockpit", "fuel"], by["TS_Turret"].Missing);
        Assert.False(by["TS_Turret"].Warns);

        // The same ships in a plugin with an official name: listed, never a finding.
        using var y2 = new Yard();
        void Build2(StarfieldMod m) { y2.Ship(m, "TS_NoLander", noLander, (10, 0, 0)); }
        var official = y2.Check(y2.Mod(Build2, "SFBGSFFE.esm"));
        Assert.Single(official.ShipListings);
        Assert.True(official.ShipListings[0].Warns);
        Assert.DoesNotContain(official.Findings, f => f.Check == "ships");
    }

    [Fact]
    public void A_plugin_with_no_ship_has_no_listing_and_no_ships_line()
    {
        using var y = new Yard();
        var r = y.Check(y.Mod(m => m.Keywords.AddNew("TS_NotAShip")));
        Assert.Empty(r.ShipListings);
        Assert.Null(Ships.Summary(r.ShipListings));
    }
}
