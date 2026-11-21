// 0.10.0 item 2 (2026-11-21): the ship group. For every ship base form a plugin adds or overrides (a generic base form
// with a BlueprintComponent), list its modules by system, its class and its three weapon slots, and say which of the
// systems a flyable ship needs are missing, like the Spaceship Editor's flight check.
//
// The rule comes from the official masters (NOTES.md, "0.10.0 relay", "Ship group: the rule"): every one of the 239
// standard official player ships has a cockpit, reactor, grav drive, engine, fuel tank, landing gear, docker and landing
// bay. A module's systems are read from its own data, never from its EditorID: its property-sheet actor values, a form
// link with keyword SpaceshipPartLinkedWeapon, and the docking and landing offset markers its exterior packin places.
// The ship's class is the reactor's ShipModuleClass* keyword (the game reads it from the reactor:
// ShipReactorHasClassKeyword).
//
// The listing goes to the log, plus one count line in the normal output. The WARNING (check "ships"; user's answer,
// 2026-11-21, option a): every ship base form the plugin adds (its own FormKey, not an override) that has a cockpit and
// lacks a required system, naming the missing systems; its text says a ship only NPCs fly can ignore it, because a
// record doesn't say who flies it (39 official NPC ships with cockpits lack a system). Official plugins aren't flagged.
// One-piece hulls stay listing only. A missing shield and the weapon slots never warn: the flight check's need for a
// shield is unknown, and 219 of 245 official player ships have a slot that points at a non-weapon part.
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

public static class Ships
{
    /// <summary>Systems in listing order: key and the words the listing uses.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Systems =
    [
        ("cockpit", "cockpit"), ("reactor", "reactor"), ("gravdrive", "grav drive"), ("engine", "engine"),
        ("fuel", "fuel tank"), ("lander", "landing gear"), ("docker", "docker"), ("bay", "landing bay"),
        ("shield", "shield"), ("weapon", "weapon"),
    ];

    /// <summary>The systems every standard official player ship has (shield and weapons are listed, not required).</summary>
    public static readonly IReadOnlyList<string> Required = ["cockpit", "reactor", "gravdrive", "engine", "fuel", "lander", "docker", "bay"];

    static readonly Dictionary<string, string> ByActorValue = new(StringComparer.Ordinal)
    {
        ["SpaceshipMaxAvailablePower"] = "reactor",
        ["ShipSystemEngineHealth"] = "engine",
        ["ShipSystemGravDriveHealth"] = "gravdrive",
        ["ShipSystemShieldsHealth"] = "shield",
        ["SpaceshipGravJumpFuel"] = "fuel",
        ["SpaceshipLanderRating"] = "lander",
        ["SpaceshipTargetLockTime"] = "cockpit",
    };

    public static string Label(string key) => Systems.First(s => s.Key == key).Label;

    public sealed record Module(string Name, HashSet<string> Systems, string? Class);

    public sealed record Listing(string Ship, string Origin, string? Class, IReadOnlyList<(string System, List<string> Modules)> BySystem,
        IReadOnlyList<string> Slots, IReadOnlyList<string> Missing, bool NoShield, string? OnePiece, int ModuleCount)
    {
        public bool Complete => OnePiece == null && Missing.Count == 0;

        public bool HasCockpit => BySystem.Any(x => x.System == "cockpit" && x.Modules.Count > 0);

        /// <summary>The WARNING rule: a ship the plugin adds, with a cockpit, lacking a required system (never a
        /// one-piece hull, whose Missing is empty).</summary>
        public bool Warns => Origin == "added" && HasCockpit && OnePiece == null && Missing.Count > 0;
    }

    /// <summary>The WARNING text for a ship that <see cref="Listing.Warns"/>.</summary>
    public static string CheckText(Listing s) =>
        $"{s.Ship} has a cockpit but lacks required ship system{(s.Missing.Count == 1 ? "" : "s")}: {string.Join(", ", s.Missing.Select(Label))}. " +
        "Every official player ship has them, and the Spaceship Editor's flight check asks for them; if only NPCs fly this ship, ignore this";

    /// <summary>Two-level lookup: the plugin's own records first (its modules and its overrides win), then the masters.</summary>
    sealed class Resolver(ILinkCache own, ILinkCache masters)
    {
        public bool Get<T>(FormKey fk, out T rec) where T : class, IMajorRecordGetter
        {
            if (own.TryResolve<T>(fk, out var a)) { rec = a; return true; }
            if (masters.TryResolve<T>(fk, out var b)) { rec = b; return true; }
            rec = null!;
            return false;
        }
    }

    public static bool IsShip(IGenericBaseFormGetter g) => g.Components.OfType<IBlueprintComponentGetter>().Any();

    /// <summary>The listing for every ship base form in the plugin (added or overridden). Empty when it has none,
    /// in which case no link cache is built.</summary>
    public static List<Listing> List(IStarfieldModGetter mod, ILinkCache masters)
    {
        var ships = mod.GenericBaseForms.Where(IsShip).ToList();
        var result = new List<Listing>();
        if (ships.Count == 0) return result;
        var r = new Resolver(mod.ToImmutableLinkCache(), masters);
        var modules = new Dictionary<FormKey, Module>();
        Module ModuleOf(FormKey fk) => modules.TryGetValue(fk, out var m) ? m
            : modules[fk] = r.Get<IGenericBaseFormGetter>(fk, out var g) ? Classify(g, r) : new Module($"(not found: {fk})", [], null);

        foreach (var s in ships.OrderBy(s => s.EditorID, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.FormKey.ToString()))
        {
            var bp = s.Components.OfType<IBlueprintComponentGetter>().First();
            var bySystem = Systems.ToDictionary(x => x.Key, _ => new List<string>());
            string? reactorClass = null;
            Module? onePiece = null;
            var byPart = new Dictionary<uint, Module>();
            foreach (var it in bp.Items ?? [])
            {
                var m = ModuleOf(it.BaseItem.FormKey);
                byPart.TryAdd(it.PartID, m);
                foreach (var x in m.Systems) bySystem[x].Add(m.Name);
                if (m.Systems.Contains("reactor")) reactorClass ??= m.Class;
                if (m.Systems.Count(x => x != "weapon") >= 3) onePiece ??= m;
            }
            var wb = s.Components.OfType<ISpaceshipWeaponBindingsComponentGetter>().FirstOrDefault();
            var slots = wb == null ? new List<string>() : new[] { wb.Slot1, wb.Slot2, wb.Slot3 }.Select(v =>
                v is 0 or -1 ? "none"
                : byPart.TryGetValue((uint)v, out var m) ? $"part {v} = {m.Name}{(m.Systems.Contains("weapon") ? "" : " (not a weapon)")}"
                : $"part {v} (no such part)").ToList();
            var origin = s.FormKey.ModKey == mod.ModKey ? "added" : $"overrides {s.FormKey.ModKey.FileName}";
            result.Add(new Listing(Checker.Name(s), origin, reactorClass,
                Systems.Select(x => (x.Key, bySystem[x.Key])).ToList(), slots,
                onePiece != null ? [] : Required.Where(x => bySystem[x].Count == 0).ToList(),
                bySystem["shield"].Count == 0,
                onePiece == null ? null : $"{onePiece.Name} covers {onePiece.Systems.Count(x => x != "weapon")} systems",
                bp.Items?.Count ?? 0));
        }
        return result;
    }

    static Module Classify(IGenericBaseFormGetter m, Resolver r)
    {
        var sys = new HashSet<string>();
        string? cls = null;
        foreach (var ps in m.Components.OfType<IPropertySheetComponentGetter>())
            foreach (var p in ps.Properties ?? [])
                if (r.Get<IActorValueInformationGetter>(p.ActorValue.FormKey, out var av) && av.EditorID is { } e && ByActorValue.TryGetValue(e, out var s))
                    sys.Add(s);
        foreach (var kc in m.Components.OfType<IKeywordFormComponentGetter>())
            foreach (var k in kc.Keywords ?? [])
                if (r.Get<IKeywordGetter>(k.FormKey, out var kw) && kw.EditorID is { } e && e.StartsWith("ShipModuleClass", StringComparison.Ordinal))
                    cls = e["ShipModuleClass".Length..];
        foreach (var l in m.Components.OfType<IFormLinkDataComponentGetter>().SelectMany(c => c.Links ?? []))
        {
            if (!r.Get<IKeywordGetter>(l.Keyword.FormKey, out var kw)) continue;
            if (kw.EditorID == "SpaceshipPartLinkedWeapon") sys.Add("weapon");
            if (kw.EditorID != "SpaceshipLinkedExterior") continue;
            if (!r.Get<IPackInGetter>(l.LinkedForm.FormKey, out var pk) || !r.Get<ICellGetter>(pk.Cell.FormKey, out var cell)) continue;
            foreach (var placed in cell.Temporary.Concat(cell.Persistent))
                // The two markers are statics (official masters); a typed lookup keeps the cache from indexing every record type.
                if (placed is IPlacedObjectGetter po && r.Get<IStaticGetter>(po.Base.FormKey, out var b))
                {
                    if (b.EditorID == "ShipDockingOffsetMarker01") sys.Add("docker");
                    if (b.EditorID == "ShipLandingOffsetMarker01") sys.Add("bay");
                }
        }
        return new Module(m.EditorID ?? m.FormKey.ToString(), sys, cls);
    }

    /// <summary>"A x2, B" in first-seen order.</summary>
    static string Counted(List<string> names) =>
        string.Join(", ", names.GroupBy(n => n).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}"));

    /// <summary>The log line for one ship.</summary>
    public static string Line(Listing s)
    {
        var head = $"ships: {s.Ship} ({s.Origin}, {s.ModuleCount} module{(s.ModuleCount == 1 ? "" : "s")}): class {s.Class ?? "unknown (no reactor class keyword)"}";
        var systems = string.Join("; ", s.BySystem.Select(x => $"{Label(x.System)}: {(x.Modules.Count == 0 ? "none" : Counted(x.Modules))}"));
        var slots = s.Slots.Count == 0 ? "weapon slots: none (no weapon bindings)" : "weapon slots: " + string.Join(", ", s.Slots.Select((v, i) => $"{i + 1} = {v}"));
        var verdict = s.OnePiece != null ? $"one-piece hull ({s.OnePiece}), systems not checked"
            : s.Missing.Count == 0 ? "every required system present" : "missing required: " + string.Join(", ", s.Missing.Select(Label));
        if (s.OnePiece == null && s.NoShield) verdict += "; no shield (listed only)";
        return $"{head}; {systems}; {slots}; {verdict}";
    }

    /// <summary>The count line in the normal output, or null when the plugin has no ship.</summary>
    public static string? Summary(IReadOnlyList<Listing> ships)
    {
        if (ships.Count == 0) return null;
        int complete = ships.Count(s => s.Complete), onePiece = ships.Count(s => s.OnePiece != null), missing = ships.Count - complete - onePiece;
        var parts = new List<string> { $"{complete} with every required system" };
        if (missing > 0) parts.Add($"{missing} missing a required system");
        if (onePiece > 0) parts.Add($"{onePiece} one-piece hull{(onePiece == 1 ? "" : "s")} (not checked)");
        return $"SHIPS: {ships.Count} ship{(ships.Count == 1 ? "" : "s")} listed: {string.Join(", ", parts)}. " +
               "The listing (modules by system, class, weapon slots, missing systems) is in the log: run again with --verbose or --log <file>.";
    }
}
