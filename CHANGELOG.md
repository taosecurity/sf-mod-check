# Changelog

## 0.5.0 (2026-11-16)

New command `sfmodcheck conflict`: reads a whole load order (a Plugins.txt) and reports the changes it throws away
(official changes undone by mods, one mod's changes lost to another, ITMs, load-order problems), labels each lost
field Forwardable, Choice or No effect, and recommends what to do (a safe load-order move, an update to look for, or a
local patch with a prompt for building it). Report folder with `report.html`, `report.md` and `conflicts.json`.
Options `--plugins`, `--data`, `--extra-data`, `--out`, `--max-memory`, `--quiet`, `--verbose`, `--log`; exit codes
0, 2 and 3.

It comes from the author's private StarfieldConflictCheck (in use since 2026-10-28), after a fix of its memory
runaway on 2026-11-16. Before that fix, a load order with city cells and navmeshes grew past 10 GB and didn't finish.
Three things held the memory: Mutagen's link cache kept every placed object, cell and navmesh of every plugin once
one was looked up (and still missed the originals of most placed objects, so they were never compared); naming record
references in the report did whole-record lookups in the same cache; and describing changes to long lists compared
every entry with every other one. Now each plugin is read once per record type, keeping only the records mods change;
names come from an EditorID-only lookup; and long lists are matched by hash, confirmed entry by entry. On the
author's 148-plugin load order it runs in about 2 minutes, peaking at 8.4 to 8.8 GB, with the same findings as the old version
wherever the old version could finish.

Safety: plugin names in Plugins.txt and in plugin headers are only matched against files that exist in the Data folder
(and `--extra-data`), so a path-like name is reported as not found and never opened. All text from plugins is
HTML-encoded in the report, and the report loads nothing from the web.

Tested in game (2026-11-16): the author's own merged patches for a 148-plugin load order, which `conflict` reports
as losing nothing, were played through a short smoke test (two cities, NPCs, companion reactions) with no crash and
no save problems.

Security (from the release's security review): the report's ready-to-paste prompt for an AI assistant copies record
names and values from plugins. A crafted mod could have hidden instructions there, on lines of their own, for an
assistant that can run commands. Now EditorIDs in the prompt keep only EditorID characters, copied values lose line
breaks, control characters, quotes and backticks and are quoted, and the prompt says they are data, not instructions.
Text from plugins also stays on one line everywhere in report.md and report.html.

Fixes from the release's code review (`conflict`):
- A Bethesda Creation switched off in Plugins.txt (its file stays in Data) is no longer analyzed as if loaded,
  nor is its ship-blueprint file.
- Bethesda's files are recognized by their exact names (SFBGS + 3 hex digits, sfta + 2 digits, their
  BlueprintShips- files), so a mod named, say, SFBGS003Fixes.esm is checked like any other mod.
- A damaged plugin that can't be opened, or whose header is damaged, is skipped with a load-order problem, and the
  rest is still checked.
- A record version with a field that couldn't be read or compared is never reported as an ITM (cleaning it could
  delete a real change), and failed comparisons are counted under "Tool limits hit" instead of being dropped.
- Names and texts compare by their text, as in `check`, whether stored in the plugin or in .strings files.
- The suggested patch name has no spaces and never ends in an official plugin's name (the game removes such names
  from Plugins.txt): `ConflictPatch-<winner>-<loser>-Patch.esm`.
- Record type names that start with I (IdleMarker, Ingestible, ImpactDataSet) are no longer cut short in messages.
- In long lists (over 500 entries, such as a worldspace's navmesh map), entries holding the same bytes from
  different files now match, so the examples of missing entries name the ones really missing.
- Cell grids show their point and land flags without the junk bytes, and a change description only mentions the
  land flags when their real byte differs.
- Plugin text containing the page's menu placeholder no longer gets the menu pasted into it; load-order problems
  stay on one line in report.md; "mods (item modification)" instead of "mod (item modification)s".
- `--max-memory` takes a number of GB from 0.000001 to 1000000 and says so for anything else (NaN, Infinity,
  values so small they would mean no limit).
- Byte data (navmeshes, undocumented blobs) compares and hashes by its bytes in one step, as in `check`.
- A cell grid described as a whole shows its point and real land flags; plural record names follow English rules
  ("classes", "lenses", "categories").
- `--max-memory` is also checked while a record type is being read, not only while records are compared.
- The RESULT line counts read problems as "read problems (records not compared ...)".
- A `|` in plugin text no longer breaks the tables in report.md; plugin file names in the AI prompt are cleaned
  like the rest of the plugin text; record IDs in report.html are HTML-encoded; section links in report.html are
  unique for pairs with similar names.

Fix (both commands): an empty option value (`--data ""`, an unset variable in a script) or an empty plugin name now
says which option is empty and how to fix it, instead of stopping with "ArgumentException: The path is empty".

Fix (both commands): a cell's grid (XCLC) holds X, Y, one byte of land flags and 3 unused bytes. Mutagen reads the
flags and the unused bytes as one value, and Bethesda's files leave junk in the unused bytes. `check` could call
such a cell override different from its master when it isn't, so a cell ITM could be missed (no cell was affected in
practice, because cells that hold placed objects are skipped). `conflict` skipped the flags completely, so a real
land-flag change went unseen. Both now compare the grid point and the land-flag byte only. Same findings as before
on six test load orders.

## 0.4.1 (2026-11-16)

Security and bug-fix release, from a code review and a security review. Built on BuildKit 0.3.1.

Security:
- A plugin whose master list holds a path instead of a file name (a network path such as `\\server\share\x.esm`,
  a drive or `..`) gets a `[header]` ERROR and that master is never looked up. Before this, checking such a
  crafted mod could make Windows contact another computer and send the user's login hash.
- Text from plugins (EditorIDs, names, master names) is printed with control characters shown as `\xNN`.
- `--log` refuses a plugin or archive name (.esm .esp .esl .ba2) and any file that starts like a plugin, so a
  mistyped option can't overwrite a mod.

Fixes:
- Links: a container's own links were sometimes dropped when a grandchild record had a link to the same record
  (for example a quest condition and a dialogue condition on the same NPC), so they went unchecked.
- The conditions and scenes rules missed condition lists that sit behind recursive data types.
- The "reuses a master's EditorID" warning missed EditorIDs with non-ASCII letters (masters were read as Latin-1,
  the plugin as UTF-8). The EditorID cache is rebuilt once.
- Every exit-2 run now ends with a `RESULT: CANNOT CHECK` line (a missing plugin, bad options, an unreadable file),
  as AGENTS.md says.
- A `--masters-dir` folder that doesn't exist is exit 2 with a message, not a silent "master not installed".
- With no `--data` and no game in Steam's usual folder, the message says to use `--data`.
- A log file that stops working (disk full, network share gone) no longer stops the check.
- `SFMODCHECK_CACHE_DIR` applies to every mode, and a bad value is a clear message.
- Faster: log text is only built when logging is on; master headers are read once.

Attribution:
- The tool says who made it: one line on stderr at the start of each check (not with `--quiet`), `--version` with
  credits, and the author and copyright in the exe's file properties.

## 0.4.0 (2026-11-09)

First public release. Built on Mutagen.Bethesda.Starfield 0.54.4 (through BuildKit 0.2.0).

- `sfmodcheck check <plugin>`: structure, header, ids, read, links, conditions, dialogue, scenes, navi, editorids,
  itm and records checks, with ERROR / WARNING lines, one RESULT line and exit codes 0, 1 and 2.
- HINT lines: after each check's findings, one line on how to fix them in xEdit or the Creation Kit. `--no-hints`
  turns them off.
- The official scene list is learned from your own Data folder and cached in `%LOCALAPPDATA%\sfmodcheck\`.
- A note on stderr before checking an official master or a plugin with more than 500,000 records (Starfield.esm
  needs over 30 GB of memory).
- Drag and drop: a plugin dropped on `sfmodcheck.exe` is checked in its own window, which stays open at the end. With
  no `--data` and no game in Steam's usual folder, a plugin inside a Data folder uses that folder.
- Two downloads: a dotnet tool package and a stand-alone `sfmodcheck.exe` (win-x64, .NET built in).
- Troubleshooting log: `--log <file>` writes what sfmodcheck read (plugin, Data folder and why, where each master was
  found, caches used or rebuilt), the time per stage, the findings per check and any error in full; `--verbose` prints
  the same on stderr. The findings and the RESULT line don't change.
- Errors for wrong arguments name the file or option and the fix (a missing option value or a bad `--max` used to end
  in a stack trace). A `--data` folder that doesn't exist stops at once with exit code 2.
- `SFMODCHECK_CACHE_DIR` moves the caches.
- AGENTS.md: step-by-step install and use instructions for AI assistants (also in both downloads). README notes for
  Game Pass, Vortex and Mod Organizer 2 installs.

Earlier versions (0.1.0 to 0.3.0, 2026-11-06 to 2026-11-08) were private, under the name StarfieldPluginCheck.
