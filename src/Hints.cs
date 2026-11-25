// One "how to fix" line per check, in Creation Kit and xEdit terms (step 5a, 2026-11-09). Printed once per check,
// after that check's last finding, as "HINT [check] ...". --no-hints turns them off. Findings never depend on this
// table: it only adds text after them. Every check name the checker can report must have an entry here (a test asserts it).
namespace SfModCheck;

public static class Hints
{
    public static readonly IReadOnlyDictionary<string, string> ByCheck = new Dictionary<string, string>
    {
        ["structure"] =
            "The file itself is damaged (or a tool wrote it wrongly). Open it in xEdit: if it loads, save it again from " +
            "xEdit or the Creation Kit; if it doesn't, rebuild it from your CK project, a backup or your builder.",
        ["header"] =
            "xEdit: select the File Header record. Missing master: install it, or remove it with Clean Masters (only " +
            "when nothing uses it). Master order: right-click > Sort Masters. Flags: an .esm needs the ESM (Master) " +
            "flag, and only one of Light (ESL) or Medium. Next Object ID: edit HEDR > Next Object ID in the File Header so " +
            "it is above the plugin's highest own FormID.",
        ["ids"] =
            "Own FormIDs must fit the plugin type (light 000-FFF, medium 0000-FFFF). xEdit: Compact FormIDs for ESL / " +
            "for Medium, or take the Light or Medium flag off. A master index past the master list is a broken " +
            "FormID: add the missing master in xEdit, or rebuild the record.",
        ["read"] =
            "Mutagen (the library sf-mod-check reads plugins with) can't read this record, so the game may not either. " +
            "Look at the named record in xEdit for errors in red, fields from another game or a wrong field size, " +
            "then fix the field or make the record again in the CK.",
        ["assets"] =
            "A missing script means the record's script never runs; a missing model shows as an empty or error " +
            "mesh. Pack the file into the mod's own archive (<plugin name> - Main.ba2), install it as a loose file " +
            "under Data, or fix the path in the record (xEdit: the record's VMAD scripts or Model field). If the file " +
            "comes from another mod, make that mod a master or a requirement. A folder or file name in the path that " +
            "ends in a dot or a space works only as a loose file on Windows: remove the dot or space in the record. " +
            "A missing material or texture makes a surface look wrong in the game: pack the .mat or .dds file with the mod " +
            "(textures go in <plugin name> - Textures.ba2) or fix the path in the .nif or .mat that names it. " +
            "A missing mesh means that part of the model doesn't show: pack the .mesh file (under geometries) with the mod, " +
            "or export the model again with its geometry. " +
            "A missing sound is silent in the game: pack the .wem file the sound bank streams (sound\\soundbanks\\<id>.wem) " +
            "with the mod, or generate the bank again in Wwise. " +
            "Run with --verbose to see which archives " +
            "were read.",
        ["records"] =
            "Deleted records can crash the game when the game or another mod still uses them. xEdit: on placed " +
            "references (REFR, ACHR) use right-click > Undelete and Disable References. For other types, remove " +
            "the deleting override in xEdit (right-click > Remove) unless you really mean it. In the CK, disable " +
            "instead of deleting vanilla objects.",
        ["links"] =
            "A field points at a record that doesn't exist (often a missing master), is the wrong type or is deleted. " +
            "xEdit: point the named record's field at an existing record of the right type, or clear it. CK: re-pick " +
            "it in the form's drop-down and save. 'Injected record': the plugin adds a record in a master's FormID " +
            "range, fine only if you meant it. 'Overrides ..., which is a <type>': the record has a master record's " +
            "FormID but another type. The game keeps whichever record loads first and silently drops the other " +
            "(armor and armor addon may replace each other). Give it its own FormID (xEdit: right-click > Change " +
            "FormID) or remove it.",
        ["navi"] =
            "The navmesh info map (NAVI) points at a navmesh that doesn't exist, isn't a navmesh or is deleted. CK: " +
            "load the cell, select its navmesh and Finalize it, then save. If the plugin deletes a navmesh that others " +
            "link to, restore it instead. xEdit can't safely fix navmesh data by hand.",
        ["conditions"] =
            "A condition leaves its form parameter empty, where the game's own uses of that function always fill it, " +
            "uses a function number the game doesn't have, or names an alias its quest doesn't have. CK: open the " +
            "record's condition list, edit the condition and pick the form, a real function or one of the quest's " +
            "aliases (or remove the condition). xEdit: fill in the condition's Parameter #1/#2 field, or fix its " +
            "Function or alias number.",
        ["dialogue"] =
            "A dialogue response has an empty Emotion. CK: open the topic info, edit the response and pick an " +
            "emotion (Neutral is fine). xEdit: set the response's TRDA Emotion to a real value.",
        ["scenes"] =
            "A scene uses a quest alias its quest doesn't have, or has no quest. CK: open the scene's quest, check " +
            "the Quest Aliases tab, then fix each scene actor, action and condition that names the alias (or set " +
            "the scene's quest). xEdit: compare the scene's alias IDs with the quest's alias list.",
        ["quest-stage-unset"] =
            "A stage of a quest the plugin adds is never set: no start stage, dialogue response, scene or script " +
            "SetStage call sets it, so its log entry and fragment never run. CK: open the quest's Quest Stages tab, " +
            "then set the stage from a topic info's or scene's 'Set Parent Quest Stage' field or a script (SetStage), " +
            "or mark it Run On Start. If another mod's script or the game's engine sets it, ignore this. A NOTE means " +
            "a script's stage number or the script itself couldn't be read, so the stages weren't checked. A WARNING that " +
            "says the check stopped is not about a stage: a script couldn't be read; run again with --verbose and report it.",
        ["quest-stage-missing"] =
            "A script sets a quest stage the quest doesn't have, so the call does nothing. CK: add the stage on the " +
            "quest's Quest Stages tab, or fix the number in the script (or in the script property the record sets) and " +
            "compile it again.",
        ["editorids"] =
            "Give each new record its own Editor ID that no master uses either. CK: open the form and change its " +
            "ID. xEdit: edit the EDID field. This usually comes from duplicating a record or Copy as New Record.",
        ["trees"] =
            "The plugin edits an official Story Manager or Planet Content Manager node. Every mod that edits the same " +
            "node conflicts, and the last one loaded wins. CK: make your own node under the official branch (Story " +
            "Manager: set its parent and previous sibling) and revert your edits to the official one. xEdit: remove " +
            "the override (right-click > Remove) once your own node does the job. Keep the edit only if you mean to.",
        ["ships"] =
            "A ship the plugin adds has a cockpit but lacks a system every official player ship has (cockpit, " +
            "reactor, grav drive, engine, fuel tank, landing gear, docker, landing bay). CK: open the ship in the " +
            "Spaceship Editor, add a module for each missing system and run its flight check. xEdit: add the module " +
            "to the ship's Blueprint component. A ship only NPCs fly (an enemy or a quest ship) can ignore this.",
        ["itm"] =
            "Identical to Master (ITM) records change nothing but can undo other mods' edits to the same record. " +
            "xEdit: right-click the plugin > Remove \"Identical to Master\" records (or run QuickAutoClean). In the " +
            "CK they come from opening a form and pressing OK without changes.",
    };

    /// <summary>The hint for a check, or null if none (never expected: a test checks that every check has one).</summary>
    public static string? For(string check) => ByCheck.TryGetValue(check, out var h) ? h : null;
}
