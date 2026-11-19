# Changelog

## 0.6.0 (2026-11-19)

- New `assets` check in `check`: every script a record attaches (attached scripts, and the fragment and alias scripts
  of quests, scenes, perks, topic infos and packages) must have its `.pex`, an ERROR when missing (the script never
  runs); every model path a record names must have its file, a WARNING when missing. A file counts when it is loose in
  the Data folder (or a `--masters-dir` folder) or inside an archive the game loads: `<plugin> - *.ba2` for the plugin
  and its masters (other languages' voice archives left out) plus the archives `Starfield.ini` and
  `StarfieldCustom.ini` list. Archive names are read from each archive's name table; nothing is unpacked. New options
  `--no-assets`, `--ini <file>` and `--language <code>`. Not checked yet: textures and materials, `.mesh` geometry,
  files the game finds by name (voice, lip and face animation, animations, LOD, Wwise), a script's parents and
  imports, SFSE plugins, and which copy of a file wins (README).
- The `assets` check in `conflict` too: every mod plugin in the load order is checked against the archives of every
  active plugin, the INIs' archive lists and loose files (the `--extra-data` folder, then Data); Bethesda's own
  plugins aren't checked. Same options as `check` (`--no-assets`, `--ini`, `--language`). The RESULT line adds
  ", N missing assets (S scripts, M models)" and ", N archives that can't be read" when there are any. report.html
  and report.md get a "Missing assets" section (by plugin, scripts before models, with the archives read, any archive
  that can't be read, and how to fix it), a Summary row, and a recommended action per plugin (Medium when a script is
  missing, Low for models only) and per unreadable archive (Medium). conflicts.json gets `assetsChecked`,
  `archivesRead`, `missingAssets` (plugin, severity, kind, path, script, users, finding) and `unreadableArchives`
  (archive, why). With `--no-assets` the section says the check was off.
- `assets`, both modes: a missing script or model in an override isn't reported when the version it overrides names
  the same path (the masters' winning version: in `conflict`, the record in the last of the plugin's masters, in
  load order, that has it). Found on the Star Wars Genesis load order, where four mods were blamed for scripts
  vanilla MQ305, City_NA_Well01 and two MagPerk perks attach and the game doesn't ship. `--verbose`/`--log` lists
  each use left out; conflicts.json gets `inheritedAssetUses` and the report's "Missing assets" intro says how many.
  In `conflict` these lookups obey `--max-memory`: past it they stop and every missing path is reported, with a line
  in "Tool limits hit".
- `conflict`: if the assets step (or the archive count) stops part way, the report is still written instead of
  CANNOT CHECK; the "Missing assets" section and "Tool limits hit" say why, and conflicts.json gets `assetsFailed`
  (null when it ran to the end). The `--log` has one "counted: general (GNRL)" line per counted general archive.
- Code review (0.6.0): in `check`, an assets step that can't finish (a folder it can't list) is one
  `WARNING assets` line instead of CANNOT CHECK, so the other checks' results stand. In `conflict`, an archive whose
  header can't be read is listed with `--no-assets` too (RESULT ", N archives that can't be read (not counted)",
  `unreadableArchives`), and the archive paragraph names it; the RESULT line counts the assets step's own notes as
  ", N assets-check notes", not as read problems; "Missing assets" says "None found before the check stopped." when
  the step stopped; the step-stopped warning shows with `--quiet`, like the archive-limit warning. A model or
  script path that is rooted (`C:\...`) or uses `..` never counts as found, since the game looks only under Data.
- `--language` takes the 6-letter code `zhhans`, which its own message listed but refused (both modes).
- `assets` on Linux and macOS: loose files are found without regard to case and with `/` folders (`Scripts/Foo.pex`
  counts for `scripts\foo.pex`), as the game sees them under Proton; before, no loose file was ever found there.
- README "Known gaps": a plugin's archives are every `<plugin> - *.ba2`, which can be a little wider than what the
  game opens.
- `conflict` counts the archives the game would load, general (GNRL) and texture (DX10) separately, and warns when
  either is over the game's 256 slots: the RESULT line adds ", WARNING N general archives (over 256)" (or texture),
  the report a WARNING in "Missing assets" and a High recommended action. The counts are always in the report's
  Summary and in conflicts.json (`archiveCounts` gnrl, dx10, slots; `archiveLimitWarnings`), with `--no-assets` too
  (headers only). The list matches Mutagen's `GetApplicableArchivePaths` for each plugin, less other languages'
  voice archives (test). Each archive is counted by its header type (GNRL general, DX10 texture), as the game does
  (checked in the game's code for 1.16.244), so a GNRL archive named "- Textures.ba2" counts as general; the
  report's archive paragraph says so.
- `conflict`: when a plugin's records of one type can't all be read and it sits in the middle of a record's versions
  (it doesn't win, no later version has it as a master, and every later version is a full record), the record is
  still compared, and "Tool limits hit" now says "N other ... records were compared without its version": its own
  entries (a change a later plugin overrides, an ITM, a reorder cost) can't be listed. Before, this went unsaid.
  Records where its version would win, be a later version's base, or still win fields that the partial forms after
  it leave alone (or that a later version couldn't read) are skipped and counted as not compared (the last two cases
  are new; 0.5.3 compared those without it).
  Every plugin that stops a comparison is named, not only the first. The RESULT line's "read problems" count includes
  these lines.
- `conflict`: the one-time read that tells which records a partly-read plugin overrides is now named in the "where it
  stopped" message and held to `--max-memory` during the read, like every other read.
- `conflict`: the "record type has no known signature" fallback is gone. Every Starfield record type has one (a test
  checks them all); a future Mutagen without one stops with a clear message instead of guessing.
- "a"/"an" before record signatures spelled out letter by letter: "an FLST", "an LVLI", "an MGEF", "an FSTP" (before:
  "a FLST"). Signatures said as words keep the first-letter rule ("a MISC", "a LCTN"). Every Starfield signature that
  could take "an" is sorted into one list or the other, and a test fails on a new one.
- `--version` and the byline read the version from the build, so the program can't disagree with the package.
- `conflict`: the report-cleanup log line after a failed write names the full error; a missing report folder is no
  longer logged as a problem.
- Docs: README and AGENTS.md now say that stdout can hold stack traces Mutagen prints itself for scene actions it
  can't fully read (`Unexpected header type: PAQO`; unchanged behavior, also in 0.5.3), and that scripts should read
  only the exit code and the RESULT line.

## 0.5.3 (2026-11-18)

Fixes from the 0.5.2 code reviews.

- `conflict`: a report that fails part way (a full disk, a report file another program holds open) no longer leaves new
  and old report files mixed. All three files are removed on any failure, as they already were when memory ran out;
  a file that can't be removed is named on screen and in the log, so nobody trusts it.
- `conflict`: when the record-type pass can't read a plugin (a damaged spot or file header), that plugin may hold the
  first version of a record as another type, which the game keeps. Later versions of such a record are no longer
  compared against each other (they could read as an ITM or a lost change against the wrong base); they are counted
  under "Tool limits hit" ("N other ... records were not compared, because X may hold the first version of them as
  another record type").
- `conflict`: "N other records were not compared" no longer counts records the game drops anyway (versions of a
  record whose FormID belongs to an earlier record of another type), nor records with only one version (nothing is
  compared for those). Whether a version is dropped now follows the first record's type as the record-type pass read
  it; where that pass couldn't read an earlier plugin that may hold the record, the record is counted, not
  compared. (A record type with no known signature, which Mutagen 0.54.4 never has, falls back to 0.5.2's rule and its
  skipped records with two or more versions are listed under "Tool limits hit".)
- `check`: an armor record on an armor addon's FormID (or the other way round) is now a links WARNING, not an ERROR.
  The game lets those two replace each other, and `conflict` already left that swap out; it is still usually a wrong
  FormID, so it stays on the list. Any other type change is still an ERROR.
- `check`: type names read "an ACHR", "an OTFT", "an NPC_" (were "a ACHR").
- `conflict`: the record-type pass's notes under "Tool limits hit" share one wording ("its file header could not be
  read (reason), so ..."; joined to the plugin's own line as "... also could not be read (reason), so ..."), and a file
  header that can't be read now gives its reason.
- Code: one helper for exception summaries; the itm property lists are cached once, not twice.

## 0.5.2 (2026-11-17)

`conflict` reports overrides that change a record's type. A mod record with the FormID of an earlier record of another
type (for example an armor record on a DLC cell's ID, after a wrong master index) used to be skipped without a word:
the comparison reads one record type at a time, so the earlier record of the other type was never read. Now the
record types are read straight from the file headers (only each record's type and FormID, so memory stays flat) and
every such override is listed as "record type differs between plugins: <first plugin> has a CELL, <mod> an ARMO", in
report.html, report.md (a new section and a Summary row) and conflicts.json (`typeChanges`), with a Medium
recommendation per plugin; the RESULT line adds the count when there are any. The text says what the game does with
such a record: it keeps whichever record loads first and silently drops the other; for placed objects in temporary
groups, the object may vanish when its cell loads. Armor and armor addon records may replace each other in the game,
so a swap between those two isn't listed. An ordinary override of the same record that loads after the type-changing
one is now compared as well (before, it was skipped too), while the records the game drops are not compared with each
other. A plugin whose record types can't all be read says so under "Tool limits hit", on the same line as its other
read problem when it has one. `check` already reported these as a links ERROR; its links hint now carries the same
explanation.

One rule for errors that must stop a run. A record or field that can't be read is still skipped and listed, as before.
Running out of memory (and conflict's `--max-memory` stop, and a thread running out of stack) now always ends the run:
`check` with `RESULT: CANNOT CHECK` and exit code 2; `conflict` with `RESULT: CANNOT CHECK` and exit code 2 and no
report (`--max-memory` keeps exit code 3). In 0.5.1 some inner catches turned such an error into "unreadable" text and
carried on, so a report could look whole when it wasn't. Covered, in `check`: opening the masters and the plugin,
reading each record and its links, the conditions, scenes and EditorIDs of each record, learning the official scene
list, the EditorID check, the condition walk, the itm comparison and the land-flags text. In `conflict`: reading each
plugin's header and opening it, every catch in the comparison, the field hash and value text, the impact text (the
field walk, value descriptions and entry names), the land-flags text and the new record-type pass. An error wrapped in
another counts (inner exceptions are followed, and every error inside an AggregateException, not only the first).
Catches that take only named file errors (the log, the caches, zlib data) can't catch these, so they are unchanged.
The RESULT line after such an error is built once the run's memory is released, with a fixed text if that fails too.

Code: check's itm comparison and conflict's field comparison are one comparer (DeepCompare) with six options where
the two differ, so each keeps exactly its earlier answers: proved by running the 0.5.1 code beside the new one on
awkward values and on every field of real records in the tests, and by identical `check` output on all 18 official
masters and four mods and identical `conflict` findings on a 149-plugin load order. No option changed. 175 tests (17 new; one 0.5.1 test moved into the new ones). After a run that runs out of memory while writing its report, the report files are removed, so a half-written report never stays behind.

## 0.5.1 (2026-11-17)

`conflict` no longer stops at the first record it can't read. On a 157-plugin load order (a large overhaul with many
add-ons), one record that Mutagen 0.54.4 can't parse (a component name its enum doesn't know) ended the whole run with
`CANNOT CHECK`. Now a plugin whose records can't all be read is read group by group: only the group holding the bad
record is skipped (records read before it are kept), and the report's "Tool limits hit" lists it once, with the
record type and, where Mutagen names it, the record. Because that plugin's later records of the type are unknown, a
record it may also change (one from its own masters, where its version would win or be the base of a later one) is not
compared either: "Tool limits hit" counts those, so no conflict is reported with the wrong winner. A record whose
versions can't be compared is skipped the same way, with none of its results kept. If a run still stops, the RESULT
line names the plugin and record it was reading or comparing.

Other `conflict` fixes, from the 0.5.0 code review:
- report.md escapes plugin text everywhere it appears (load-order problems, recommendations, headings, the summary
  and ITM tables, impact lines, "Tool limits hit"), and also escapes backtick, `*` and `_`, so a name can't start a
  code span, emphasis, a link or HTML, or break a table. FormKeys sit in a code span that holds any backticks.
- A part of a value that can't be read while it is described (a cell grid's land flags, for example) is counted under
  "Tool limits hit" instead of silently dropping out of the text; the text then says "land flags unreadable".
- A list that was reordered and also edited is described by the entries that went and came, not as every shifted
  entry "changed". Short byte blobs show their bytes in hex ("01 00 00 00") instead of "a list of 4"; a changed long
  one gives its length and the bytes from where it first differs. "has an extra an entry" now reads "has an extra entry".
- Speed: recommendations are worked out once per conflict pair (they were worked out about 8 times), and report.html
  is no longer copied in memory to place its menu.

Code: one set of command-line helpers for `check` and `conflict` (Cli.cs); the load-order problem list is read-only
outside the code that cleans each entry. The new skip-and-carry-on catches in conflict let an out-of-memory error (and the --max-memory stop) through, so those still end the run. No option or output format changed. 159 tests (17 new).

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
- A cell grid described as a whole shows its point and real land flags. Counts of record types read "2 weapons" for
  the common types and "2 instance naming rules records" for the rest, instead of guessed plurals.
- report.md escapes `|`, `<`, `>`, `[`, `]` and `\` in the Load order table (plugin and master names) and in the
  record, lost-value and winning-value cells of the lost-change tables. Other report.md text (headings, the
  Impact column, the problem list, the ITM table's plugin cell) is kept on one line but not yet escaped, so a
  crafted plugin name can still show as Markdown formatting there; report.html is encoded everywhere.
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
