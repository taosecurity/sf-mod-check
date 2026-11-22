# sf-mod-check

sf-mod-check finds problems in a Starfield plugin before you play it or release it. It only reads files and never
changes a plugin, and it checks a typical mod in about 4 seconds.

Download the exe zip from [Releases](https://github.com/taosecurity/sf-mod-check/releases) and unzip it. It has five
commands:

- [`check`](#run-it) reads one plugin (`.esm`) and reports problems that would break it in the game: damaged files,
  bad headers, FormIDs outside the plugin's range, links to records that don't exist, broken conditions, scenes and
  navmesh links, repeated EditorIDs, deleted records, ITM records and edits to official Story Manager and Planet
  Content Manager nodes. After each kind of problem it says how to fix
  it in xEdit or the Creation Kit. For a ship mod, it also lists each ship's modules and the systems it lacks.
- [`conflict`](#conflicts-between-mods-sf-mod-check-conflict) reads your whole load order and reports which changes
  it throws away (official changes undone by mods, one mod's changes lost to another) and what to do about each.
- [`patch`](#build-a-conflict-patch-sf-mod-check-patch) builds a local patch that brings back the changes `conflict`
  found lost, without xEdit or an AI assistant.
- [`release`](#check-a-release-zip-sf-mod-check-release) checks the zip you are about to upload: plugin names the
  game drops, missing scripts and files, sounds packed so they play silent, and every plugin inside.
- [`triage`](#look-over-a-downloaded-mod-sf-mod-check-triage) looks over someone else's mod before you install or
  rebuild it: its files, what it replaces in the game, its plugins, scripts and DLLs, in one offline report. It is
  triage, not a verdict: it shows a person where to start reading.

**First command:** drag `MyMod.esm` onto `sf-mod-check.exe`. Or, from a prompt:

    sf-mod-check check MyMod.esm

For AI assistants: step-by-step instructions are in [AGENTS.md](AGENTS.md).

## Example

Check one plugin:

    > sf-mod-check check ConflictPatch.esm
    sf-mod-check 0.9.0 check: ConflictPatch.esm (light master, 11 masters, 13 records, 13 overrides (0 ITM), 5038 links)
    RESULT: OK, 0 errors, 0 warnings (ConflictPatch.esm: 13 records, 5038 links; 4.0 s)

Check a whole load order for lost changes, then build a patch for the two mods you just added:

    > sf-mod-check conflict
    sf-mod-check 0.9.0 conflict: 159 plugins (18 official, 141 mods), 2558 records overridden by mods
    RESULT: OK, 37 lost-change entries (24 official, 13 mod vs mod), 11 ITMs, 0 load-order problems, ...

    > sf-mod-check patch --only NewModA.esm,NewModB.esm
    RESULT: OK, patch sf-mod-check-patch\ConflictPatch.esm: 13 records, 25 fields forwarded, ... 11 masters

Each command also writes a report that says what it found and what to do about it. The sections below show each
command in full.

**Renamed:** this tool was called **sfmodcheck** until 0.7.0. The command is now `sf-mod-check`; the exe zip still
holds a `sfmodcheck.cmd` that runs it under the old name for one more release. Your caches and the `SFMODCHECK_*`
environment variables keep working.

It does the same kind of job as part of xEdit's "Check for Errors", but from the command line, so it fits a build
script or a release checklist, and it gives each problem one line that says what is wrong and where. It is built on
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) 0.54.4.

What each version added is in [CHANGELOG.md](CHANGELOG.md); 0.9.0 added the `patch` command. sf-mod-check has been
used on its author's own mods since 2026-11-07, and reports from other mods are welcome (see
[Issues](https://github.com/taosecurity/sf-mod-check/issues)).

## Quick start (Creation Kit and xEdit authors)

1. Download `sf-mod-check-<version>-win-x64.zip` from the [Releases](https://github.com/taosecurity/sf-mod-check/releases)
   page and unzip it anywhere. It holds one `sf-mod-check.exe` with .NET built in, so nothing else needs installing.
2. Drag your plugin (`MyMod.esm`) onto `sf-mod-check.exe`. A window opens, prints the result and stays open until you
   press Enter.
3. Read the lines that start with ERROR (the mod is broken) and WARNING (probably a mistake). After each kind of
   problem, a HINT line says how to fix it in xEdit or the Creation Kit. The last line says OK or FAILED.

sf-mod-check looks for the game's masters (Starfield.esm and the others) in Steam's usual Data folder,
`C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data`. If your game is somewhere else (Game Pass, a Steam
library on another drive), either drop a plugin that sits in the game's own Data folder (sf-mod-check then uses that
folder and says so), or run it from a command prompt with `--data`:

    sf-mod-check.exe check MyMod.esm --data "D:\XboxGames\Starfield\Content\Data"

Masters that aren't in the Data folder are also looked for in the plugin's own folder. See
[Where your game's Data folder is](#where-your-games-data-folder-is) for Game Pass, Vortex and Mod Organizer 2.

## Where your game's Data folder is

sf-mod-check needs the game's masters (Starfield.esm, the DLC and Creations masters your plugin lists). It reads them
from the game's Data folder and from the folders given with `--masters-dir`, and never changes them.

- **Steam:** `C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data` is the default, so nothing is needed. A
  Steam library on another drive: `--data "E:\SteamLibrary\steamapps\common\Starfield\Data"`.
- **Game Pass (Xbox app):** the game usually sits in `<drive>:\XboxGames\Starfield\Content`, so use
  `--data "C:\XboxGames\Starfield\Content\Data"` (change the drive letter to the drive the game is installed on).
- **Vortex:** Vortex deploys mods into the game's own Data folder, so the Steam or Game Pass line above applies. To
  check a plugin before deploying it, give its path in Vortex's staging folder and keep `--data` pointing at the
  game's Data folder.
- **Mod Organizer 2:** MO2 keeps each mod in its own folder (`mods\<ModName>\`) and leaves the game's Data folder
  alone. Give the plugin's path inside `mods\`, `--data` the game's real Data folder, and `--masters-dir` the folder of
  any mod whose plugin is one of your masters (once per folder):

      sf-mod-check check "C:\Modding\MO2\mods\MyMod\MyMod.esm" --data "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data" --masters-dir "C:\Modding\MO2\mods\MainMod"

  (Programs started from MO2's own Run menu see the mods through its virtual Data folder, so adding sf-mod-check there
  as an executable with the arguments `check <plugin>` should also work; we haven't tried it.)

Without `--data`, when Steam's folder has no Starfield.esm, a plugin that sits in a folder holding Starfield.esm uses
that folder (the output says so). When a plugin needs Starfield.esm and none is found, a note on stderr says to use
`--data`.

## Install

Two ways, both from the [Releases](https://github.com/taosecurity/sf-mod-check/releases) page. Each file there has a
`.sha256` file next to it with its SHA-256 checksum.

- **Stand-alone exe** (Windows, no .NET needed): `sf-mod-check-<version>-win-x64.zip`. Unzip it and run `sf-mod-check.exe`,
  or drop plugins on it.
- **dotnet tool** (Windows or Linux, needs the [.NET SDK](https://dotnet.microsoft.com/download) 9 or later):
  download `sf-mod-check.<version>.nupkg` into a folder, then

      dotnet tool install --global sf-mod-check --add-source <that folder> --version <version>

  After that, `sf-mod-check` works from any prompt. `dotnet tool update --global sf-mod-check --add-source <folder>`
  installs a newer one, and `dotnet tool uninstall --global sf-mod-check` removes it. Coming from sfmodcheck 0.7.0 or
  older: the package has a new name, so remove the old one first with `dotnet tool uninstall --global sfmodcheck`.

## Words used here

- **Plugin:** a mod's data file (`.esm`, `.esp` or `.esl`). It adds or changes records in the game.
- **Master:** a plugin that another plugin needs loaded before it. Starfield.esm is the master of nearly every mod.
- **Light plugin (light master):** a plugin flagged to use a small slot, so it doesn't count toward the limit of
  about 250 full plugins.
- **Load order:** the order the game loads plugins in. When two plugins change the same record, the one loaded later
  wins.
- **Record:** one thing in a plugin, such as a weapon, a quest, a location or a setting.
- **FormID:** a record's number, such as `0002B560`. The first two digits depend on the load order.
- **EditorID:** a record's readable name, such as `Ammo_556`, used by the Creation Kit and xEdit.
- **Override:** a later plugin's copy of a record from an earlier one, which replaces it in the game.
- **ITM ("identical to master"):** a record a plugin copies without changing anything. It can undo other mods'
  changes, so it is best removed.
- **Partial form:** a Starfield override that carries only the fields it changes. The game takes the other fields
  from earlier plugins.
- **Navmesh:** the invisible map of a cell's floor that tells characters where they can walk.
- **Papyrus:** Starfield's scripting language. Scripts are written as `.psc` source files and compiled into `.pex`
  files that the game runs.
- **BA2:** Bethesda's archive format. Mods pack their scripts, models and textures into `.ba2` files.
- **SFSE:** the Starfield Script Extender, which loads `.dll` plugins that add features the game lacks.
- **Creation Kit (CK):** Bethesda's official editor for making Starfield mods.
- **xEdit (SF1Edit):** a community tool for viewing and editing plugins record by record.
- **Mutagen:** a .NET library for reading and writing Bethesda plugins, which sf-mod-check is built on.

## Run it

    sf-mod-check check <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--max <lines per check>] [--quiet] [--no-hints]
                              [--no-assets] [--ini <file>]... [--language <code>] [--verbose] [--log <file>]
    sf-mod-check --version

- `--data`: the game's Data folder (default: Steam's usual one, see above).
- `--masters-dir`: a folder searched for masters before the Data folder; can be given more than once. Use it so an
  add-on is checked against the main plugin you just built, not an older copy installed in Data.
- `--max`: the most lines printed per check (default 100). The counts in the RESULT line are always complete.
- `--quiet`: leave out the first summary line.
- `--no-hints`: leave out the HINT lines.
- `--no-assets`: skip the assets check (missing scripts and models), for example when the plugin's archive isn't
  built yet. If the assets check itself can't finish (a folder that can't be listed, a disk error), the other checks
  still count and one `WARNING assets` line says the scripts and models weren't checked.
- `--ini <file>`: also read this INI's `[Archive]` lists for the archives the game loads, after `Starfield.ini` in the
  game folder and `StarfieldCustom.ini` in `Documents\My Games\Starfield`; can be given more than once. Use it for an
  install whose INIs are elsewhere.
- `--language <code>`: the voice language whose archives count (`<plugin> - Voices_<code>.ba2`; default `en`;
  the game's codes are en, de, fr, es, it, ja, pl, ptbr and zhhans).
- `--verbose`: print the troubleshooting log on stderr (lines start with `log: `). See below.
- `--log <file>`: write the troubleshooting log to that file. See below.

`sf-mod-check <plugin>` without the word `check` does the same.

A wrong argument (a missing value, `--max` with something that isn't a number, a plugin or Data folder that doesn't
exist, a log file it can't write) stops with exit code 2 and a message that names the file or option and what to do.

### Troubleshooting log

When something goes wrong, or a result looks odd, run the same check again with a log:

    sf-mod-check check MyMod.esm --log sf-mod-check-log.txt

The log goes only where you ask: `--log <file>` writes it to that file (a new file each run, in the current folder
unless you give a full path), and `--verbose` prints the same lines on stderr instead. Nothing is logged without one
of them. The findings and the RESULT line are the same with or without the log.

What it records, with the time of each line: the sf-mod-check, Windows (or Linux) and .NET versions; the arguments; the
plugin's path, size and date and its header (light, medium or full; its masters); which Data folder was used and why
(`--data`, Steam's usual folder, or the plugin's own folder); for each master, where it was found (or every place
looked when it wasn't); each cache read, rebuilt or not writable, with its path; how long each stage took; the number
of findings per check; and, when sf-mod-check itself fails, the full error with its stack trace.

The log names folders on your PC (your user name may be in them). Read it before you attach it to an
[issue](https://github.com/taosecurity/sf-mod-check/issues).

For a dropped plugin (drag and drop), open a command prompt in the exe's folder and run the command above with the
plugin's full path.

### What the output looks like

    sf-mod-check 0.6.0 check: MyMod.esm (light master, 1 masters, 18 records, 2 overrides (1 ITM), 44 links)
    ERROR [links] MyMod_Misc [MISC:000801:MyMod.esm] links to 000FF0:MyMod.esm, which doesn't exist (expects Keyword)
    HINT [links] A field points at a record that doesn't exist, ...
    WARNING [itm] SomeKeyword [KYWD:0A1B2C:Starfield.esm] is identical to the master version it overrides (ITM)
    HINT [itm] Identical to Master (ITM) records change nothing but can undo other mods' edits ...
    RESULT: FAILED, 1 errors, 1 warnings (MyMod.esm: 18 records, 44 links; 3.9 s)

- `ERROR [check] ...`: the plugin is broken; fix it before release.
- `WARNING [check] ...`: probably a mistake, or a risk; look at it.
- `HINT [check] ...`: how to fix that check's findings, once per check (not part of the result).
- `SHIPS: <n> ships listed: ...` (0.10.0): only when the plugin adds or overrides a ship. It counts the ships with
  every required system, those missing one, and one-piece hulls. The full listing is in the log (see the ships check).
- `RESULT: OK|FAILED, <n> errors, <n> warnings (...)`: always the last line. It adds "links not checked" when a
  master is missing, and "official scenes not checked" when the scene list couldn't be learned (see the scenes check).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | no errors (warnings are allowed) |
| 1 | errors |
| 2 | couldn't check: bad arguments, a missing file, a file it couldn't open, or it ran out of memory (the RESULT line then says `CANNOT CHECK`) |

A record or field Mutagen can't read is listed as a finding and the check carries on. Running out of memory is
different: the check stops with `CANNOT CHECK` and exit code 2, so a result is never shown for a check that didn't
finish.

Scripts should read the exit code and the lines that start with `ERROR `, `WARNING ` and `RESULT: `. Other lines
(the summary, HINT lines, notes on stderr) may change between versions.

## Conflicts between mods: `sf-mod-check conflict`

`check` looks at one plugin. `conflict` reads your whole load order and tells you which changes it throws away:

- **official changes undone by mods:** a mod built without a DLC or update as a master overrides a record that the
  DLC or update also changed, and wins, so the official change is lost in game;
- **one mod's changes lost to another:** two mods change the same record and the later one doesn't have the earlier
  one as a master;
- **ITMs** (overrides identical to the record they override) and **load-order problems** (missing masters, plugins
  listed before their masters, a mod with a `BlueprintShips-*.esm` master, files it can't read);
- **overrides that change a record's type** (since 0.5.2): a mod's record has the FormID of an earlier record of
  another type, for example an armor record on a Shattered Space cell's ID. The game keeps whichever record loads
  first and silently drops the other (armor and armor addon records may replace each other, so that swap isn't listed); for placed objects in
  temporary groups, the object may vanish when its cell loads. The report lists each one as "record type differs
  between plugins: <first plugin> has a CELL, <mod> an ARMO".

Each lost field is labeled **Forwardable** (the winner didn't mean to change it, so a patch can copy the lost value
forward safely), **Choice** (both changed it, so you pick), **No effect** (they differ only in list order, letter
case or undocumented fields) or, since 0.12.0, **Lost** (an official `BlueprintShips-*.esm` file wins, and nothing
can bring the change back, so there is nothing to choose). Each conflict gets a recommendation: load-order moves when one is safe, an update to
look for, or a small local patch. [`sf-mod-check patch`](#build-a-conflict-patch-sf-mod-check-patch) builds that
patch for you (0.9.0); the report also gives the steps for xEdit and a prompt you can give to an AI coding assistant.
`conflict` only reads plugins: it never changes one, and it never changes your load order.

**The load order it uses** is the game's: `Starfield.esm` and Bethesda's other files first, then your Plugins.txt
order (each plugin after its masters), then the official `BlueprintShips-*.esm` files (Bethesda's ship blueprints)
after every plugin, oldest file first (by the file's modified time, then by name). That last rule is new in 0.12.0;
earlier versions put the BlueprintShips files with Bethesda's other files. A Plugins.txt line for a BlueprintShips
file doesn't move it, and a BlueprintShips file is left out when its plugin is turned off. A mod that has a
BlueprintShips file as a master gets a load-order problem, because the game loads that master after the mod. It is
still loaded and compared (`triage` says so too), and what it changes is judged against that master's version, which
its author saw. A BlueprintShips file whose master is a newer BlueprintShips file gets a load-order problem as well:
the files keep their oldest-first order.
A mod's change that a BlueprintShips file undoes is listed among the lost changes, with that file as the winner,
and its recommendation says so plainly: the file loads after every mod, so no load order or patch brings the change
back, and the mod's edit to that ship doesn't reach the game (the advice names the record type when it isn't a
ship). This includes a mod's edit of a ship the BlueprintShips file adds itself (the mod has that file as a master),
judged against the file's own version.
The navmesh info map is the exception: the BlueprintShips files carry a copy of it too, so they have its last copy,
but each plugin's copy lists only its own navmeshes. It isn't a record one plugin wins whole, so it keeps its own
note (it isn't patched, and shouldn't be copied by hand) instead of the "doesn't reach the game" advice.

The RESULT line's "official" count is the official changes undone by mods. A mod's change lost to a BlueprintShips
file is a mod's loss, so it is counted with the "mod vs mod" entries (the line keeps its 0.11.0 shape for scripts).

**First command** (Steam, with the game's own load order):

    sf-mod-check conflict

Then open `sf-mod-check-conflict\report.html` in a browser.

    sf-mod-check conflict [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]
                        [--max-memory <GB>] [--no-assets] [--no-priorities] [--ini <file>]... [--language <code>]
                        [--quiet] [--verbose] [--log <file>]

- `--plugins`: the load order to check (default: the game's own, `%LOCALAPPDATA%\Starfield\Plugins.txt`, which
  Vortex and the game's Creations menu keep up to date). Only the lines marked active (`*`) count. Bethesda's own files
  (the DLCs, updates and Creations such as the Trackers Alliance ones) are always loaded first, as the game does.
  Without `--plugins` and without the game's file, it stops and asks for one rather than guess an order.
- `--data`: the game's Data folder, where the plugins are read (default: Steam's usual one; Game Pass:
  `--data "C:\XboxGames\Starfield\Content\Data"`).
- `--extra-data`: a second folder of plugins, read before the Data folder (its files win), for example a patch you
  have built but not installed yet. The load order still comes from `--plugins`, so add the patch's line there too.
- `--out`: the report folder (default `sf-mod-check-conflict` in the current folder). It gets `report.html` (the one to
  read: summary, recommended actions, each conflict with its story, a filter box, light and dark themes), `report.md`
  (the same as Markdown) and `conflicts.json` (everything, for scripts).
- `--max-memory`: stop cleanly when memory passes this many GB (exit code 3, no report), instead of slowing the PC.
  It takes a number of GB from 0.000001 to 1000000, for example 8.
- `--no-assets`, `--ini <file>`, `--language <code>`: the assets check, as for `check` (0.6.0).
  - **Where it looks:** in `conflict` it looks up every mod plugin's scripts and models in the archives of every
    active plugin (`<plugin> - *.ba2`), the archives the INIs list, and loose files (the `--extra-data` folder, then
    Data).
  - **What it skips:** Bethesda's own plugins aren't checked. An override isn't blamed for a missing file the version
    it overrides already names (the record in the last of the plugin's masters, in load order, that has it, as in
    `check`). That one is reported once, against the plugin that added the path, or not at all when it's Bethesda's
    (conflicts.json `inheritedAssetUses` counts the uses left out).
  - **Memory:** those lookups obey `--max-memory`. Past the limit they stop, every missing path is reported, and
    "Tool limits hit" says so.
  - **Results:** the RESULT line adds ", N missing assets (S scripts, M models)" when there are any. The report's
    "Missing assets" section lists them by plugin (scripts, an ERROR, before models, a WARNING), with the number of
    archives read and any archive that can't be read. Each plugin with missing files gets a recommended action, and
    conflicts.json has them as `missingAssets` and `unreadableArchives`. With `--no-assets` the section says the
    check was off.
  - **If the step stops:** if the assets step itself stops (a folder that can't be listed, a disk error), the report
    is still written. The section and "Tool limits hit" say why, and conflicts.json has the reason as `assetsFailed`
    (else null). The RESULT line counts that note (and a `--max-memory` stop in the assets lookups) as ", N
    assets-check notes", apart from the read problems, since every record was still compared. The warning on stderr
    shows with `--quiet` too.
- **The archive limit (0.6.0):** `conflict` also counts the archives the game would load for the load order.
  - **What it counts:** the same list (every active plugin's `<plugin> - *.ba2` and the INIs' lists, other
    languages' voice archives left out), separately for general archives (GNRL: scripts, meshes, materials, sounds)
    and texture archives (DX10). Each archive is counted by its header type, as the game does, not by its name: a
    GNRL archive named `- Textures.ba2` counts as general.
  - **Why it matters:** the game has 256 slots for each kind. A 257th general archive damages the game's memory, and
    a 257th texture archive wraps around to slot 0 (wrong textures or crashes).
  - **Over the limit:** when either count is over 256, the RESULT line adds ", WARNING N general archives (over
    256)" or ", WARNING N texture archives (over 256)". The report gets a WARNING and a High recommended action,
    listed first (remove or merge mods that bring archives, or use a BA2 limit fix).
  - **Where the counts are:** always in the report's Summary and "Missing assets" section and in conflicts.json
    (`archiveCounts`: `gnrl`, `dx10`, `slots`; `archiveLimitWarnings`). `--no-assets` still counts them (it reads
    only each archive's header).
  - **Unreadable archives:** an archive whose header can't be read isn't counted. The archive paragraph names it and
    says the true counts may be higher, and with `--no-assets` the RESULT line adds ", N archives that can't be read
    (not counted)".
- **Field priorities (0.10.0):** `conflict` rates each lost field the way xEdit does, from a table made from
  xEdit's Starfield definitions (`data\field-priorities.txt`; see "Build from source").
  - **Benign and ignored fields:** a field xEdit rates benign (most Editor IDs, the Master reference lists a location
    keeps, a few tree links) or doesn't compare (ignore) isn't counted as a lost change. The
    report lists it apart, under "Benign differences (not counted as lost)", and conflicts.json has it in `benign`.
    A record whose only lost fields are benign gets no recommended action.
  - **Critical fields:** a field xEdit rates critical (a Game Setting's name, and every field of a record injected
    into another plugin's FormIDs) is marked "Critical" and its record comes first in its section.
  - **Two departures from xEdit:** a location's Added and Removed reference lists stay normal, because quest aliases
    read them.
    The Conditions of Story Manager and Planet Content Manager nodes stay normal too, because they decide when a
    quest starts or what a planet gets.
  - **Results:** the RESULT line adds ", N benign entries shown apart" (and " (C critical lost-change entries)" when
    there are any). In conflicts.json each `lost` entry and field has a `priority` (`normal` or `critical`), each
    `benign` field has `benign` or `ignore`, and `fieldPriorities` has the counts.
  - **`patch` and `triage` don't use the priorities:** `patch` still restores a benign field along with the rest.
- `--no-priorities`: count every differing field as a lost change, as 0.9.1 did. The lost changes and counts are
  then the same as 0.9.1's, with no benign section, no Critical marks and no new conflicts.json keys.
- `--quiet`: leave out the byline, the progress lines and the summary line.
- `--verbose` and `--log <file>`: the troubleshooting log, as for `check`. It records the Data folder and load order
  used and why, the plugins loaded, each load-order problem, one line per record type (records, versions and memory),
  whether field priorities were on, each entry shown apart as benign and each critical entry, and the result.

**Mod Organizer 2 (not tested):** MO2 keeps each mod in its own folder, so the game's Data folder doesn't hold your
mods' plugins.

- Run outside MO2, `conflict` (and `patch`) sees only the plugins in the real Data folder. Every MO2 mod in your load
  order shows up as a load-order problem ("listed but not found in Data") and is left out, so the results are
  incomplete.
- The way that should work is to add sf-mod-check to MO2 as an executable (arguments `conflict --plugins
  "<MO2>\profiles\<profile>\plugins.txt" --out "<a folder outside MO2>"`) and start it from MO2, so it sees the mods
  through MO2's virtual Data folder.
- We haven't tried that yet, so check the report's load-order problems before you trust a result.

**Time and memory:** on the author's PC, 148 plugins (about 2,650 records changed by mods, including city cells and
navmeshes) took about 2 minutes and 8.4 to 8.8 GB at its peak; a small load order takes seconds. Large numbers of changed cells,
placed objects and navmeshes cost the most. `--max-memory` sets a hard stop.

**Exit codes:** 0 = report written (whatever it found); 2 = couldn't run (bad arguments, a missing folder or file,
a report it couldn't write, or it ran out of memory; the RESULT line says `CANNOT CHECK` and, when it stopped while
reading or comparing, which plugin and record it was on; no report is written); 3 = stopped at `--max-memory`
(`RESULT: STOPPED`). The last line on stdout is
always the RESULT line, for example:

    RESULT: OK, 29 lost-change entries (19 official, 10 mod vs mod), 103 ITMs, 0 load-order problems (report: ...\sf-mod-check-conflict\report.html; 122 s)

Mutagen (the plugin library) prints a stack trace itself when it can't fully read a scene's actions, such as
`System.ArgumentException: Unexpected header type: PAQO`. sf-mod-check sends those lines to stderr, each starting
`mutagen: `, and to the `--log` file, so stdout holds only sf-mod-check's own lines. It doesn't count or report them
as findings yet. A script should still read only the exit code and the RESULT line.

**Plugins and records it can't read:** a plugin that can't be opened at all is skipped and listed as a load-order
problem. Running out of memory is never skipped that way: the run stops with `CANNOT CHECK` and writes no report.

A record that Mutagen 0.54.4 can't read (a field written by a newer Creation Kit, or a damaged one) skips only
that plugin's group of records of that type, and the rest of the load order is still checked. The report's "Tool
limits hit" section lists the plugin, the record type and, where Mutagen names it, the record, once each. The RESULT
line then adds "read problems (records not compared ...)". Run `sf-mod-check check` on that plugin to see the record.

**What it doesn't check:** the scripts' code and assets other than attached scripts and model paths (textures,
materials, sounds; see the `assets` entry under `check`). Fields
that are bookkeeping or that the Creation Kit regenerates are skipped (the report lists them). A patch it suggests is
for your own game: it holds other authors' data, so don't publish it. To see its working for one record, set the
environment variable `SFMODCHECK_DEBUG_RECORD` to that record's FormKey (for example `064F3C:Starfield.esm`).

## Build a conflict patch: `sf-mod-check patch`

You added two new mods and `conflict` says one undoes part of the other. `patch` builds the fix: one small light
plugin that loads last and brings the lost changes back. You don't need xEdit or an AI assistant.

**First command** (Steam, with the game's own load order; the two mods are the ones you just added):

    sf-mod-check patch --only NewModA.esm,NewModB.esm

Then read `sf-mod-check-patch\patch-report.md`, install `sf-mod-check-patch\ConflictPatch.esm` with your mod manager
like any other mod, and put it at the end of the load order. Leave out `--only` to patch every conflict in the load
order at once.

**Example** (a real run on a 158-plugin load order; mod names changed):

    > sf-mod-check patch --only NewModA.esm,NewModB.esm
    sf-mod-check 0.9.0 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check
    Loaded 158 plugins (0 load-order problems) in 0 s
    25 records overridden by mods
    RESULT: OK, patch sf-mod-check-patch\ConflictPatch.esm: 13 records, 25 fields forwarded, 0 picks used,
    18 shared lists merged, 0 choices kept the winner's value, 0 records not built, 11 masters
    (report: sf-mod-check-patch\patch-report.md; 38 s)

The report lists each patched record and what came from where, for example:

    - AffinityEvent COM_Event_Action_Steal [00E39C:Starfield.esm]
      - RequiredFaction from NewModA.esm: changes required faction from CurrentFollowerFaction (faction)
        to NewModA_CommentsFaction (faction)
      - ActorReactions from NewModB.esm: merged entry by entry with NewModA.esm's list (both changed it):
        adds 1 entry (first: an entry (actor NewModB_Companion) to companion reactions)

How it builds each record:

1. It starts from the record as the game uses it today: each field from the last plugin that has it (a Starfield
   partial form only carries the fields it changes, so the others come from the plugins before it).
2. It copies each **Forwardable** field forward from the plugin that lost it. The winner didn't change that field, so
   nothing the winner meant is undone.
   - **Lists** (keywords, a location's references, a companion's reactions) are merged entry by entry instead. Each
     plugin's own additions are added and its own removals removed, measured against the version that plugin was
     built on, so two mods that both add entries keep both.
   - **A list both plugins changed** is merged the same way rather than treated as a choice. Where both changed one
     entry (one companion's reaction), the later plugin's version stays. A location's base-game lists are kept as
     the base game and its DLCs have them.
3. For each other **Choice** field (both plugins changed it) it keeps the winner's value, as the game does now. The report
   prints a ready-made `--pick` line for each one. Run the command again with the lines you want, for example
   `--pick 0012AB:Starfield.esm:Value=NewModB.esm` (one field) or `--pick 0012AB:Starfield.esm=NewModB.esm` (every Choice
   field of that record).
4. A record that would end up the same as today's is left out (no "identical to master" records).
5. It writes the patch, then reads it back and compares every field with the plugin it was copied from.

A record whose change is lost to an official `BlueprintShips-*.esm` file isn't built (0.12.0): the game loads those
files after every plugin, the patch too, so their version would still win. The report lists it under "Not built"
with that reason, and the RESULT line counts it among the records not built. There is nothing to do by hand for it
either. The navmesh info map keeps its own reason even when a BlueprintShips file has its last copy. When a value
the patch copies points at a record a BlueprintShips file defines, the patch gets that file as a master, and the
report's Notes say so: the game still loads that master after the patch, as it does for a mod with such a master.

    sf-mod-check patch [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]
                       [--name <Patch.esm>] [--only <a.esm>[,<b.esm>...]] [--pick <FormKey>[:<Field>]=<plugin>]...
                       [--include-official] [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]

- `--plugins`, `--data`, `--extra-data`, `--max-memory`, `--quiet`, `--verbose`, `--log`: as for `conflict`.
- `--out`: the folder for the patch and its report (default `sf-mod-check-patch` in the current folder). It can't be
  the Data folder or the `--extra-data` folder: install the patch with your mod manager.
- `--name`: the patch's file name (default `ConflictPatch.esm`). A name that ends in an official plugin's name (such
  as `-ShatteredSpace.esm`) or starts with `BlueprintShips-` is refused, because the game drops such names from
  Plugins.txt. If your load order already has a plugin of that name (last time's patch), it is left out while the new
  one is built, and the new one replaces it.
- `--only`: patch only the conflicts that involve these plugins (file names, separated by commas; give it more than
  once if you like). It is also faster: only the records those plugins change are compared.
- `--pick`: see step 3. A pick that matches nothing gets a WARNING.
- `--include-official`: also bring back changes that one official plugin (the base game, a DLC or a Creation from
  Bethesda) loses to another, for example a reference one Trackers Alliance mission adds to a location and a later
  one drops. Off by default, because `conflict` doesn't list such losses either: the game ships that way, and they
  may be meant. It also compares the records only official plugins change, so the run takes a little longer. It
  can't be combined with `--only`. The report says when a patch was made with it.

**What it writes** (all in the `--out` folder): the patch; `patch-report.md` (each record and field it forwarded and
from which plugin, the Choice fields with their `--pick` lines, the records it didn't build, and the next steps; a
list both plugins changed is listed under Choices too, as "merged entry by entry", with no `--pick` line, though
`--pick <FormKey>:<Field>=<plugin>` still takes that plugin's whole list if you prefer it); and
`Plugins-with-patch.txt`, your load order with the patch at the end. To check the patch before installing it:

    sf-mod-check conflict --plugins sf-mod-check-patch\Plugins-with-patch.txt --extra-data sf-mod-check-patch
    sf-mod-check check sf-mod-check-patch\ConflictPatch.esm

That `conflict` run still lists the records the patch didn't build, any Choice that kept the winner's value, and
lost changes that `conflict` already calls harmless ("probably has no visible effect": a change that only reorders a
list, changes letter case or touches undocumented fields), which the patch leaves alone.

**What it doesn't build yet:** records that live inside other records: cells, worldspaces, placed objects, navmeshes,
dialogue topics, dialogue lines and scenes. Patching those means copying the parent record too (a cell's or a
worldspace's header), which can undo other mods' changes to it, so the report lists them for xEdit instead. A quest's
own fields are patched; its dialogue and scenes are not copied into the patch. The navmesh info map (NAVI) is never
patched: each plugin's copy lists only its own navmeshes, so a merged copy would hide entries.

**Rebuild it when your mods change.** The patch lists every plugin it took values from as a master. After you add,
remove or update one of them, run the same command again (the old patch is replaced).

**For your own game only:** the patch holds other authors' data, so don't publish it.

**Exit codes:** 0 = patch written (or nothing to patch: the RESULT line says so and no plugin is written); 1 = the
patch was written but reading it back found a difference: don't use it, and report it with a `--log` file; 2 =
couldn't run (`RESULT: CANNOT BUILD`); 3 = stopped at `--max-memory` (`RESULT: STOPPED`). The last line on stdout is
always the RESULT line, for example:

    RESULT: OK, patch C:\Mods\sf-mod-check-patch\ConflictPatch.esm: 3 records, 5 fields forwarded, 0 picks used, 1 shared lists merged, 1 choice kept the winner's value, 2 records not built, 6 masters (report: ...\patch-report.md; 9 s)

## Check a release zip: `sf-mod-check release`

Run it on the zip you are about to upload to Nexus, Bethesda Creations or a friend. It unpacks the zip into a temp
folder (always removed afterwards) and says OK or FAIL for what would break the mod for a player: a plugin name the
game drops, a script or file the mod needs that isn't in the zip, sound files packed so the game plays them silent,
and everything `check` finds in each plugin.

    sf-mod-check release MyMod-1.0.0.zip

    sf-mod-check release <zip>... [--data <Data folder>] [--masters-dir <folder>]... [--master-archive <ba2>]...
                                [--dmmdeps <exe>] [--papyrus <exe> --ck-sources <ContentResources.zip>]
                                [--profile basic|house|creations] [--creations-master <plugin>]...
                                [--quiet] [--verbose] [--log <file>]

- `<zip>`: the release zip. Give an add-on's zip together with its main mod's zip, so the add-on's plugin finds
  the main plugin and its scripts.
- `--data`, `--masters-dir`: as for `check`. Masters and archives of mods the zip builds on go in a `--masters-dir`
  folder.
- `--master-archive <ba2>`: the archive of the mod your plugin patches. Scripts and models in it count as present
  (a patch that overrides another mod's records keeps that mod's fragment scripts there). Can be given more than once.
- `--dmmdeps <exe>`: also run DMMDeps (`dmmdeps-fat.exe`, from ZeeOgre's DevModManager) to find files the plugin uses
  that the zip lacks. sf-mod-check runs only the reviewed v0.4.1 build and refuses any other, by its SHA-256
  (`8a5367865ef21e29b17301a3341b2195fdacbf163c151678c0c420747ff9e6f8`). DMMDeps has gaps (its issues #175 to #177:
  base scripts, face files, some models), so what it can't see is a REVIEW line and never "not needed". Without it,
  one NOTE says that only the scripts and models were checked. DMMDeps runs on a copy in the temp folder and takes a
  minute or more on big mods (15 minutes at most).
- `--papyrus <exe>` with `--ck-sources <ContentResources.zip>`: lint the `.psc` sources a zip ships under
  `Scripts\Source`, with papyrus-tools v0.16.3 (SHA-256 `954ec6d7559713c8cdf6aaaf20bde5d9feedf52a6404a90cfca5b2107559c4fa`)
  and the vanilla sources from the Creation Kit's `Tools\ContentResources.zip`. Every finding is a WARNING. Without
  the vanilla sources the lint is skipped with a WARNING (it would only report noise).
- `--profile basic` (default): only what breaks the mod in the game makes it FAIL. `--profile house`: also fails on
  packaging rules (below), which `basic` only shows as WARNING lines.
- `--profile creations` (0.10.0): the `basic` rules plus Bethesda Creations' rules for PC, Xbox and PlayStation (the
  `creations-` rules below). Use it before you upload a Creation. Packaging rules stay WARNING lines, as in `basic`.
- `--creations-master <plugin>`: with `--profile creations`, a master by another author that you have seen on
  Creations for every platform your Creation targets. It is then allowed for this run. Can be given more than once.
- `--quiet`, `--verbose`, `--log <file>`: as for `check`.

Output is PROBLEM lines (fail), REVIEW lines (look at them), WARNING and NOTE lines, each with its rule in brackets,
then `RESULT: OK|FAIL (n problems, n to review, n warnings)`. Exit codes: 0 OK, 1 FAIL, 2 can't check (bad
arguments, a zip that can't be unpacked or sits inside the game folder, a missing file). It never writes into the
zip, the game folder or the Data folder.

| Rule | Fails in | What it means |
|---|---|---|
| `plugin-name` | basic, house | A plugin named `...-ShatteredSpace.esm`, `-Constellation`, `-Starfield`, `-OldMars` or `-SFBGS...`, or `BlueprintShips-...`: Starfield drops it from Plugins.txt and it stops loading. Rename it, for example `-ShatteredSpaceAddon.esm`. |
| `voice-compressed` | basic, house | `.wem` or `.ffxanim` stored compressed in a `.ba2`: the game plays them silent. Pack with Archive2 `-compression=None`. |
| `ba2-unreadable` | basic, house | An archive whose header or records don't add up. |
| `script-missing`, `model-missing` | basic, house (models: WARNING) | A script a plugin attaches, or a model it names, is in none of the zip's files and archives, the `--masters-dir` mods' archives or the game's archives. |
| `dmmdeps` | basic, house | With `--dmmdeps`: a file the plugin needs isn't in the zip (PROBLEM), a file is referenced but found nowhere (PROBLEM), a file in the zip is not used (REVIEW), or also in the game's archives (REVIEW). |
| `plugin-check` | basic, house | An ERROR from the same checks as `check`, run on every plugin in the zips, or a plugin that can't be checked. |
| `mutagen-gap` | basic, house | Data the Mutagen library 0.54.4 loses when it writes a plugin, and a missing ONAM list (the game ignores edits to the master's placed references). |
| `master`, `one-plugin`, `script-source`, `root-md`, `private-file` | house only | A `BlueprintShips` master; more than one plugin in a zip; `.psc` sources in the zip; a root `.md` such as `README.md` with no mod name in it (it overwrites another mod's copy in Data; use `README-<ModName>.md`); a file such as `PERMISSION.md` that should never ship. |
| `papyrus-lint` | never | Lint findings (WARNING only). |
| `creations-ini`, `creations-sfse` | creations | An INI file, or a DLL, EXE, ASI or file in an `SFSE` folder: Creations take none of them on any platform. A mod that needs SFSE stays a PC (Nexus) mod. |
| `creations-framework` | creations | A compiled script that names a script from an SFSE plugin (ConsoleAPI, PapyrusUtil and others in `data\creations-masters.txt`). Consoles can't run SFSE, so those calls fail. A WARNING instead when the same script calls `Game.IsPluginInstalled`, so it may use the plugin only when it's there; the WARNING names the plugins the script tests, and a guard in another script isn't seen. Since 0.10.1 it stays a PROBLEM when every name the script tests is an official plugin's (Starfield, the DLC, `BlueprintShips-Starfield.esm`, `SFBGS*` and `SFTA*` plugins: the `[official]` list in `data\creations-masters.txt`), since such a check doesn't guard the framework; a name held in a variable still counts as a guard. |
| `creations-master` | creations | A master that isn't Bethesda's (Starfield, the DLC, `BlueprintShips-Starfield.esm`, `SFBGS*` and `SFTA*` plugins) and isn't known to be on Creations. Make it optional in code (`Game.IsPluginInstalled`), or allow it with `--creations-master` once you have seen it there. |
| `creations-xbox` | creations | A PC archive with textures and no `<name>_xbox.ba2` twin (the Creation Kit's Xbox pack refuses PC textures). Any other archive with no twin is a WARNING (Xbox gets no archive). |
| `creations-size` | creations | A zip whose plugins and archives come to over 2 GB, the most a Creation may hold. Each zip is checked on its own, since each one becomes its own Creation. |
| `creations-audio` | never | `.wem` or `.bnk` sound in an archive or loose (WARNING): PC-format audio is silent on Xbox and PlayStation until the Creation Kit makes their versions from WAV sources. |
| `creations-debug` | never | `Debug.Notification` or `Debug.MessageBox` that the player would see (WARNING), read from the compiled scripts. The Debug script does nothing on Xbox and PlayStation; show a Message record with `Show()` instead. A call counts as debug-only, and isn't reported, when its function's name mentions debug, trace or verbose, or when it sits inside an `If` that tests a debug switch (something so named) without negating it. |

## Look over a downloaded mod: `sf-mod-check triage`

Run it on a mod you downloaded, before you install it or build on it. It writes one report that lists what the mod
holds and what is worth a person's look, offline, without running anything from the mod.

    sf-mod-check triage SomeMod-1.2.zip

    sf-mod-check triage <mod.zip | mod.7z | mod.rar | mod folder | plugin> [--game <folder>] [--out <folder>]
                        [--manalyze <manalyze.exe>] [--quiet] [--verbose] [--log <file>]

**It is triage, not a verdict.** Every finding is a NOTE (information) or a LOOK (worth a person's look), and each
names the file and says why. A LOOK is where to start reading, not a fault: a mod that replaces a game script on
purpose gets one, and so does a DLL that talks to the network. The report never says a mod is safe, unsafe or
harmful; you decide that after reading.

- `<mod>`: a `.zip`, `.7z` or `.rar` download (unpacked into a temp folder that is always removed; password-protected
  archives are refused), a mod folder (for example `MO2\mods\SomeMod`, read where it is; never the game's own folders, and never a folder whose path can't be checked against them), or one plugin (with the
  `<Plugin> - *.ba2` archives beside it).
- `--game <folder>`: the game's folder, its `Data` folder, or for Game Pass its `Content` folder. Without it, Steam's
  usual folder is used. With no game found, nothing is compared with the game and the report says so.
- `--out <folder>`: where the report goes (default: next to the input, or the current folder when the input is
  inside a game install; when both are, triage writes nothing and asks for `--out`, and an `--out` inside a game install, or one whose path can't be checked, is refused too).
- `--manalyze <exe>`: also run [Manalyze](https://github.com/JusticeRage/Manalyze) on each DLL and EXE, with its
  offline plugins only (never VirusTotal, never anything that fetches from the network), for at most 2 minutes each.
  Where Manalyze's own text calls a file "malware", the report says "binary" and marks the line as reworded.
- `--quiet`, `--verbose`, `--log <file>`: as for `check`.

It writes `<name>-triage.html` (to read), `<name>-triage.txt` and `<name>-triage.json` (for scripts). On stdout it
prints the LOOK lines and `RESULT: REPORT WRITTEN (<n> to look at, <n> notes) <path>`. Exit codes: 0 report written,
2 bad input or setup. There is no "fail" exit.

| Section | What it lists | LOOK when |
|---|---|---|
| Files | every file with its size and SHA-256 (a skipped file is listed with "link" or "same name" in place of its size, and a file whose hash couldn't be read with "?"); each `.ba2`'s contents; version numbers in names; which archives load with which plugin; archives inside the mod (not opened); FOMOD installers; files outside the usual Data layout | an archive no plugin loads; an archive that can't be read |
| Replaces vanilla | loose files and archive entries whose path is also in the game's own archives | the mod replaces a game script (every quest and mod that uses it gets the mod's copy); a plugin or archive with the game's own name |
| Plugins | masters (missing, unused), light/medium/full flag, FormID range, text that isn't valid UTF-8; records added, overridden and deleted by type and origin; changed fields in official records; ONAM; attached scripts; `check`'s own findings | a missing master; ITMs; deleted official records; official changes the mod undoes (it overrides a record a later official master changed, without that master); temporary references missing from ONAM; scripts that aren't there; `check` ERRORs |
| Scripts | each `.pex` (loose or in an archive) read from its bytecode: name, parent, every function it calls, whether it logs with `Debug.Trace` | console commands; INI changes; calls to scripts in neither the mod nor the game; timers that restart themselves or start from frequent events; `GetFormFromFile` or text naming another mod's plugin; file paths that run or point outside Data |
| Binaries | each `.dll`, `.exe` and `.asi` read from its bytes: machine, build time, PDB path, ASLR/DEP/CFG, imports grouped by what they allow, exports, SFSE plugin data (game versions, Address Library or signature scanning), signature | network, process-creation and memory-injection imports; URLs and IP addresses in its strings; an SFSE DLL without `SFSEPlugin_Version`; a DLL named like a Windows DLL the game would load instead |

What it can't see: names a DLL looks up while it runs, what a script does with values it computes, records nothing
uses, and files behind a link (symlink or junction) inside a mod folder or beside a plugin: triage never follows one,
and the report lists each as skipped, with one note in the Files section that anything inside wasn't checked.

A missing-script LOOK in the Plugins section says how many linked items were skipped, because a skipped link may
hold the script, and so does the Scripts section's note when it finds no compiled scripts at all. On Windows, a
plugin you name yourself is read even when it is a link, because you chose it; on Linux and macOS it isn't read.

Only real links count: a OneDrive placeholder or another kind of reparse point is read as a normal file or folder.
An archive with more than 200,000 entries is refused. It reads compiled scripts as bytecode only:
nothing is decompiled. On a large mod it uses memory like `check` does (NAPG-sized mods: about 5 GB).

## Compare two versions of a mod: `sf-mod-check diff`

Run it when a mod you use or build on has a new version, to see what changed between the two.

    sf-mod-check diff MyMod-1.0.zip MyMod-1.1.zip

    sf-mod-check diff <old> <new> [--data <Data folder>] [--json] [--out <file>] [--all] [--quiet] [--verbose] [--log <file>]

- `<old>` and `<new>`: two plugins (`.esm`, `.esp` or `.esl`), or two mods, each a `.zip`, `.7z` or `.rar` download
  or a mod folder (a zip and a folder can be compared). An archive is unpacked into a temp folder that is always
  removed. Plugins in two mods are paired by file name, and a plugin in only one of them is listed as added or
  removed. Two plugins you name directly are compared whatever their names.
- `--data <folder>`: the game's Data folder, for the EditorIDs of records in the masters (default: Steam's usual
  folder).
- `--json`: the report as JSON instead of text. The JSON always lists everything.
- `--out <file>`: write the report to this file; stdout then gets only the RESULT line. It never writes over a
  plugin, an archive or a script, and never inside an input folder.
- `--all`: list everything in the text report. Without it, long lists stop after a set number of lines (40 cells,
  25 leveled lists, 20 entries per list, 50 base-object swaps, 100 changed records) and say how many were left out.
  With it, each cell's records are listed too.
- `--quiet`, `--verbose`, `--log <file>`: as for `check`. Like `--out`, the log never goes onto an input, a plugin,
  an archive or a script, or inside an input folder, and `--out` and `--log` must be different files.

For each plugin the report counts the records added, removed, changed and renumbered per record type, split into the
plugin's own records and its overrides of master records, lists the changed records with the fields that differ, and
lists the renumbered records (same type and EditorID, new FormID) with their old and new FormIDs.
A record whose bytes differ but whose fields are all equal (padding bytes, field order, -0 and +0, as a resave in the
Creation Kit or xEdit leaves them) is counted as noise. Noise is no difference: it is shown, but it never makes the
result DIFFERENCES.

Scripts attached to a record, and each script's properties, are matched by name, because the game
finds them by name: scripts or properties that only moved to another place in the list are noise, while a changed
name, flag or value, or a script or property added or removed, is a change. Names are compared exactly, so a name
whose letter case alone changed counts as a change, although the game ignores case. The elements of an array
property keep their order: a reordered array is a change.

Three views follow the counts:

- **By cell**: placed objects, navmeshes and the other records that sit in a cell, grouped by their cell, with the
  cell's EditorID and a summary such as `+REFR 197, -REFR 87, ~REFR 8` (`+` added, `-` removed, `~` changed or moved
  in, `>` moved out to another cell). The cells with the most changes come first.
- **Leveled lists with changed entries**: for each leveled list (items, NPCs, base forms, pack-ins and the others) whose
  entries changed, the entries added and removed, each with its level, the record it gives and its count.
- **Base-object swaps**: placed objects whose base object changed, with the old and the new base.

EditorIDs of records in the masters come from the master files: first a master in the mod itself, then the game's
Data folder (`--data`). When a master isn't found, its records are shown by FormKey only, and the report says so. A damaged master gives the names it can, and the report says which master is damaged.

For two mods, three more sections follow the plugins:

- **Loose files**: every file that isn't a plugin or an archive, paired by its path inside the mod and compared by
  size, then SHA-256: added, removed and changed, with a summary per folder first.
- **Archives**: each .ba2 is paired by file name, as plugins are. Archives with the same bytes are "same". For the
  others, the files inside are listed and each is compared by a hash of its stored bytes (nothing is unpacked), with
  the files added, removed and changed and a summary per folder. A file stored with other compression counts as
  changed.
- **Scripts**: each changed .pex, loose or inside an archive, is read and compared object by object: the states and
  functions added, removed or changed (a function changed means its code, parameters, locals, return type or flags
  differ), and whether its parent, variables, properties or structs differ. Only names and counts are shown, never
  the code. A script whose bytes differ but whose objects are all the same (compiled again, other debug info) says so.

Any file difference makes the result DIFFERENCES, and the RESULT line adds the files added, removed and changed (loose
files plus the files inside archives).

It is read-only: it never writes next to its inputs. The last line on stdout is `RESULT: NO DIFFERENCES (...)`,
`RESULT: DIFFERENCES (...)` or `RESULT: CANNOT DIFF (...)`. Exit codes: 0 no differences (noise counts as none), 1
differences, 2 bad input or setup.

### Example

Two versions of a small made-up mod, MyMod 1.0 and 1.1, each a folder with `MyMod.esm`, a readme, a loose script and
`MyMod - Main.ba2` (the tests build them: `DiffExampleTests`). In 1.1 a misc item gained a keyword, one item was
replaced by another, the vault's leveled list changed, a placed object got another base object, one more object was
placed, the script gained a function, and the archive gained a mesh. Run from the folder that holds both:

    sf-mod-check diff MyMod-1.0 MyMod-1.1

The real output (exit code 1):

    sf-mod-check 0.11.0 diff: MyMod-1.0 -> MyMod-1.1
    old: M:\MyMod-1.0 (folder)
    new: M:\MyMod-1.1 (folder)

    == Plugins ==
      changed  MyMod.esm

    == MyMod.esm ==
      old: full, 0 masters, 8 records (8 own)
      new: full, 0 masters, 9 records (9 own)
      header: HEDR record count: 16 -> 17 (information only)
      header: HEDR next object ID: 822 -> 823 (information only)
      own records: 2 added, 1 removed, 3 changed; 4 the same
      overrides:   -; 0 the same
      by record type (changed = fields differ; noise = the bytes differ but every field is equal, which is no difference):
        LVLI  own: 1 changed  |  overrides: -
        MISC  own: 1 added, 1 removed, 1 changed  |  overrides: -
        REFR  own: 1 added, 1 changed  |  overrides: -
      by cell (1 cell, 2 records; + added, - removed, ~ changed, > moved out):
        000810:MyMod.esm MYM_VaultCell: +REFR 1, ~REFR 1
        (--all lists each cell's records)
      leveled lists with changed entries (1):
        LVLI 000803:MyMod.esm MYM_VaultLoot: 2 added, 1 removed
          + level 1, 000802:MyMod.esm MYM_Coin, count 10
          + level 5, 000805:MyMod.esm MYM_NewToken, count 1
          - level 1, 000802:MyMod.esm MYM_Coin, count 5
      base-object swaps (1; placed objects whose base object changed):
        REFR 000821:MyMod.esm in 000810:MyMod.esm MYM_VaultCell: 000802:MyMod.esm MYM_Coin -> 000801:MyMod.esm MYM_Gem
      changed records (3; the fields that differ):
        MISC 000801:MyMod.esm MYM_Gem: Keywords
        LVLI 000803:MyMod.esm MYM_VaultLoot: Entries
        REFR 000821:MyMod.esm (no EditorID): Base

    == Loose files (not plugins or archives; compared by size, then SHA-256) ==
      0 added, 0 removed, 2 changed; 0 the same
    ...
      changed (2):
        MyMod-Readme.txt (11 -> 23 bytes)
        Scripts\MyMod\VaultScript.pex (249 -> 285 bytes)

    == Archives (.ba2; each file compared by a hash of its stored bytes) ==
      changed  MyMod - Main.ba2 (GNRL, 2 -> 3 files): 1 added, 0 removed, 1 changed; 1 the same
    ...
        added (1):
          meshes\mymod\gem.nif
        changed (1):
          meshes\mymod\coin.nif

    == Scripts (changed .pex files: objects, states and functions by name; no code is shown) ==
      Scripts\MyMod\VaultScript.pex (loose):
        object MyMod:VaultScript: changed
          functions changed (1): OnActivate
          functions added (1): OnLoad
    RESULT: DIFFERENCES (1 plugin compared, 1 changed, 0 added, 0 removed; records: 2 added, 1 removed, 3 changed, 0 renumbered, 0 noise; files: 1 added, 0 removed, 3 changed)

The same run with `--json --out diff.json` writes the whole report as JSON (its full shape is in
[AGENTS.md](AGENTS.md), section 6). An excerpt:

    {
      "schema": 1,
      "tool": "sf-mod-check 0.11.0",
    ...
      "plugins": [
        {
          "name": "MyMod.esm",
          "status": "changed",
    ...
          "changed": [
            {
              "key": "000801:MyMod.esm",
              "type": "MISC",
              "editor_id": "MYM_Gem",
              "override": false,
              "fields": [
                "Keywords"
              ],
              "not_compared": null
            },
    ...
          "base_swaps": [
            {
              "key": "000821:MyMod.esm",
              "type": "REFR",
              "editor_id": null,
              "cell": "000810:MyMod.esm",
              "old": {
                "key": "000802:MyMod.esm",
                "editor_id": "MYM_Coin"
              },
              "new": {
                "key": "000801:MyMod.esm",
                "editor_id": "MYM_Gem"
              }
            }
          ]
    ...
      "differences": true,
      "exit_code": 1
    }

## What it checks

Each finding names its check in brackets.

- **structure**: the raw file layout: groups where the official masters put them, sizes that add up, and compressed
  records that really are zlib data of the stored size.
- **header**: the file header (TES4). HEDR is there and 12 bytes; not both the Light and the Medium flag; an .esm
  without the Master flag (WARNING); masters missing, listed twice, or the plugin listing itself (ERROR); a master
  listed before its own master (WARNING); a Next Object ID lower than the IDs in use (WARNING; the Creation Kit's
  800/0 placeholder is fine).
- **ids**: FormIDs. A master index past the end of the master list, the plugin's own IDs past the light (FFF) or
  medium (FFFF) range, and duplicate records.
- **read**: records Mutagen can't read.
- **links**: every link points to a record that exists in the plugin or its masters, of a type that field can hold.
  A link to a deleted record is a WARNING; an override of a record no master has is a WARNING ("injected"); an
  override that changes the record's type is an ERROR (the game keeps whichever record loads first and silently drops
  the other), except an armor record on an armor addon's FormID or the other way round, a WARNING (the game lets
  those two replace each other, but it is usually a wrong FormID). Records the game's engine creates itself (such as PlayerRef) count as existing.
- **conditions**: a condition whose form parameter is empty where the official masters always fill it for that
  function (WARNING: the condition tests nothing), and a condition whose function number sf-mod-check doesn't know
  (WARNING).
  - Known functions: those in Mutagen's list (607 functions) or used by the official masters (they use one number
    Mutagen lacks, 966, so the list is learned from them too). A function added by a game update newer than this
    tool would also be flagged.
  - Aliases (0.10.0): a condition in a quest, a dialogue response or a package that names an alias number its quest
    doesn't have (WARNING: the condition is never true). The quest is the quest itself, the response's topic quest or
    the package's owner quest. Both alias uses are read: a condition that runs on an alias, and a function such as
    GetIsAliasRef whose parameter is an alias.
  - The official masters have 229 such alias conditions (mostly in radiant dialogue), so a use an official version
    of the same record already has isn't flagged.
  - Since 0.10.0 the conditions inside a package's target data (the creature `CCT_*` packages) are checked too.
- **trees** (0.10.0): an override of an official Story Manager node (SMBN, SMQN, SMEN) or Planet Content Manager node
  (PCMT, PCBN, PCCN), one WARNING each. These trees decide which quests and events start and what content each planet
  gets. Every mod that edits the same official node conflicts with the others, so the conflict-free way is to add your
  own node to the tree rather than edit an official one. Bethesda's own plugins aren't flagged (the DLCs extend the
  base game's trees on purpose). `conflict` names these records the same way and gives the same advice.
- **ships** (0.10.0): a listing, plus one WARNING per added ship that lacks a required system.
  - The listing: for every ship base form the plugin adds or overrides, the log (`--verbose` or `--log`) gets one
    line with its modules by system (cockpit, reactor, grav drive, engine, fuel tank, landing gear, docker, landing
    bay, shield, weapons), its class (from the reactor's class keyword), its three weapon slots resolved to modules,
    and the required systems it lacks. The normal output gets one `SHIPS:` count line.
  - Required means what every standard official player ship has: cockpit, reactor, grav drive, engine, fuel tank,
    landing gear, docker and landing bay. A module's systems are read from its own data (its actor values, its
    weapon link, and the docking and landing markers in its exterior packin), never from its EditorID.
  - A ship with one module that covers three or more systems (like the Starborn ships) is listed as a one-piece hull
    and not checked.
  - The WARNING: each ship the plugin adds (not an override) that has a cockpit but lacks a required system gets one
    WARNING naming the missing systems. A ship's record doesn't say whether players fly it, and many official NPC
    ships lack landing gear or a landing bay on purpose, so the warning says a ship only NPCs fly can ignore it.
    Bethesda's own plugins aren't flagged.
  - A missing shield and the weapon slots are listed only, because many official player ships have a weapon slot
    that points at a non-weapon part.
- **dialogue**: a response with Emotion 0, where the official masters use "none" or an emotion keyword (WARNING).
- **scenes**: the quest aliases a scene uses must exist in its quest. Actors, actions and dialogue targets that name a
  missing alias are ERRORs; alias conditions that do are WARNINGs; a scene with no quest is a WARNING because it can't
  run. A few dozen official scenes break this rule on purpose. sf-mod-check learns which ones from the official masters
  in your Data folder the first time it meets an official scene (about 3 seconds, once), and skips them.
- **navi**: the navmesh traversal entries in the navmesh info map (NAVI) point to navmeshes that exist and aren't
  deleted.
- **editorids**: an EditorID used by two or more new records, or a new record whose EditorID a master already uses
  (WARNINGs). Scripts, the console and tools find records by EditorID, so they find only one of them.
- **itm**: an override identical to the master version it overrides ("identical to master", ITM), one WARNING each,
  and the count in the summary line. Fields are compared by value, so field order and unread padding don't count. A
  parent kept only because it holds the plugin's child records (a cell with placed references, a quest with dialogue)
  isn't reported, the same as xEdit's "Remove ITM". Bethesda's own plugins get the count only.
- **records**: deleted records, one WARNING per record type with the count and a few IDs. Bethesda's masters delete
  records too, so it's a WARNING, not an ERROR; the risk is another mod or the game still using a deleted record.
- **assets** (0.6.0): every script a record attaches (its scripts, and the fragment and alias scripts of quests,
  scenes, perks, topic infos and packages) must have its `.pex` under `scripts\` (ERROR: the script never runs), and
  every model path a record names must have its file under `meshes\` (WARNING).
  - **Where a file can be:** a file counts when it is loose in the Data folder (or a `--masters-dir` folder) or inside
    an archive the game loads: `<plugin> - *.ba2` for the plugin and each of its masters (other languages' voice
    archives left out), plus the archives `Starfield.ini` and `StarfieldCustom.ini` list.
  - **How it reports:** each missing file is listed once, with up to 3 records that use it. An override isn't blamed
    for a missing file the master's version of the record already names (some vanilla records attach scripts the
    game doesn't ship; `--verbose` lists each one left out).
  - **A part ending in a dot or a space** (0.10.0, such as `meshes\armor.\helm.nif`): Windows trims such a part when
    it opens a loose file. The last part loses all its trailing dots and spaces; any other part loses one trailing
    dot, unless a dot comes before it. An archive looks the name up exactly. So when the trimmed path is a loose
    file, the finding is a WARNING that says the game may load it on Windows but not once the file is packed into a
    `.ba2` (a script too, not an ERROR). Otherwise the path is missing as usual, and the finding names the trimmed
    path.
  - **Not checked yet:** textures and materials (a mesh's `.mat` and `.dds` files), `.mesh` geometry, files the game finds by name (voice,
  lip and face animation, `.af`/`.hkx` animations, LOD, Wwise sounds), a script's parent and imports, whether an SFSE
  plugin a native script needs is installed, and which copy of a file wins. Tested on Steam installs only.

## For Creation Kit and xEdit authors

You don't need a build script: drop a plugin on `sf-mod-check.exe` (see [Quick start](#quick-start-creation-kit-and-xedit-authors)),
or run `sf-mod-check check MyMod.esm --data <your Data folder>` from a prompt for Game Pass and other installs.

What each check's findings mean and how to fix them. These are the same lines sf-mod-check prints as HINT lines:

- **structure**: The file itself is damaged (or a tool wrote it wrongly). Open it in xEdit: if it loads, save it again from xEdit or the Creation Kit; if it doesn't, rebuild it from your CK project, a backup or your builder.
- **header**: xEdit: select the File Header record. Missing master: install it, or remove it with Clean Masters (only when nothing uses it). Master order: right-click > Sort Masters. Flags: an .esm needs the ESM (Master) flag, and only one of Light (ESL) or Medium. Next Object ID: edit HEDR > Next Object ID in the File Header so it is above the plugin's highest own FormID.
- **ids**: Own FormIDs must fit the plugin type (light 000-FFF, medium 0000-FFFF). xEdit: Compact FormIDs for ESL / for Medium, or take the Light or Medium flag off. A master index past the master list is a broken FormID: add the missing master in xEdit, or rebuild the record.
- **read**: Mutagen (the library sf-mod-check reads plugins with) can't read this record, so the game may not either. Look at the named record in xEdit for errors in red, fields from another game or a wrong field size, then fix the field or make the record again in the CK.
- **links**: A field points at a record that doesn't exist (often a missing master), is the wrong type or is deleted. xEdit: point the named record's field at an existing record of the right type, or clear it. CK: re-pick it in the form's drop-down and save. 'Injected record': the plugin adds a record in a master's FormID range, fine only if you meant it. 'Overrides ..., which is a <type>': the record has a master record's FormID but another type. The game keeps whichever record loads first and silently drops the other (armor and armor addon may replace each other). Give it its own FormID (xEdit: right-click > Change FormID) or remove it.
- **navi**: The navmesh info map (NAVI) points at a navmesh that doesn't exist, isn't a navmesh or is deleted. CK: load the cell, select its navmesh and Finalize it, then save. If the plugin deletes a navmesh that others link to, restore it instead. xEdit can't safely fix navmesh data by hand.
- **conditions**: A condition leaves its form parameter empty, where the game's own uses of that function always fill it, uses a function number the game doesn't have, or names an alias its quest doesn't have. CK: open the record's condition list, edit the condition and pick the form, a real function or one of the quest's aliases (or remove the condition). xEdit: fill in the condition's Parameter #1/#2 field, or fix its Function or alias number.
- **dialogue**: A dialogue response has an empty Emotion. CK: open the topic info, edit the response and pick an emotion (Neutral is fine). xEdit: set the response's TRDA Emotion to a real value.
- **scenes**: A scene uses a quest alias its quest doesn't have, or has no quest. CK: open the scene's quest, check the Quest Aliases tab, then fix each scene actor, action and condition that names the alias (or set the scene's quest). xEdit: compare the scene's alias IDs with the quest's alias list.
- **editorids**: Give each new record its own Editor ID that no master uses either. CK: open the form and change its ID. xEdit: edit the EDID field. This usually comes from duplicating a record or Copy as New Record.
- **itm**: Identical to Master (ITM) records change nothing but can undo other mods' edits to the same record. xEdit: right-click the plugin > Remove "Identical to Master" records (or run QuickAutoClean). In the CK they come from opening a form and pressing OK without changes.
- **trees**: The plugin edits an official Story Manager or Planet Content Manager node. Every mod that edits the same node conflicts, and the last one loaded wins. CK: make your own node under the official branch (Story Manager: set its parent and previous sibling) and revert your edits to the official one. xEdit: remove the override (right-click > Remove) once your own node does the job. Keep the edit only if you mean to.
- **ships**: A ship the plugin adds has a cockpit but lacks a system every official player ship has (cockpit, reactor, grav drive, engine, fuel tank, landing gear, docker, landing bay). CK: open the ship in the Spaceship Editor, add a module for each missing system and run its flight check. xEdit: add the module to the ship's Blueprint component. A ship only NPCs fly (an enemy or a quest ship) can ignore this.
- **assets**: A missing script means the record's script never runs; a missing model shows as an empty or error mesh. Pack the file into the mod's own archive (<plugin name> - Main.ba2), install it as a loose file under Data, or fix the path in the record (xEdit: the record's VMAD scripts or Model field). If the file comes from another mod, make that mod a master or a requirement. A folder or file name in the path that ends in a dot or a space works only as a loose file on Windows: remove the dot or space in the record. Run with --verbose to see which archives were read.
- **records**: Deleted records can crash the game when the game or another mod still uses them. xEdit: on placed references (REFR, ACHR) use right-click > Undelete and Disable References. For other types, remove the deleting override in xEdit (right-click > Remove) unless you really mean it. In the CK, disable instead of deleting vanilla objects.

Each finding names the record by its EditorID (where it has one) and `[TYPE:FormID:Plugin]`, where FormID is the
record's ID within the plugin that defines it, so you can find it with xEdit's FormID search or the Creation Kit's
filter.

## Memory, time and caches

- A typical mod takes about 4 seconds and under 1 GB of memory: memory follows the size of the plugin being checked,
  not of its masters.
- Checking Bethesda's own masters is possible but heavy: Starfield.esm itself (3.8 million records) takes about 4
  minutes and over 30 GB of memory. sf-mod-check prints a note on stderr before it starts on an official master or any
  plugin with more than 500,000 records.
- Caches live in `%LOCALAPPDATA%\sfmodcheck\` (on Linux `~/.local/share/sfmodcheck/`; the folder kept its name from
  before the rename, so caches built by older versions are used): `edid-cache\` (the masters'
  EditorIDs, about 7 MB for Starfield.esm), `scene-quirks\` (the learned official scene list, a few KB) and
  `vanilla-index\` (triage's list of the file names in the game's own archives, about 13 MB). Each is
  keyed by the master's name, size and modified time, so a game update rebuilds it on the next check. Delete the
  folder to clear them. Set the environment variable `SFMODCHECK_CACHE_DIR` to keep them somewhere else (for example
  next to the exe for a portable copy).

## Traps (wrong answers with no error)

- **A master changed kind** (full, medium or light) after the plugin was built: its FormIDs then point at other
  records. sf-mod-check sees this only when an ID lands past a master list or past the plugin's own range.
- **Partial forms** (record flag 0x4000) hold only some fields on purpose; the scenes check uses the last full version
  of a quest for that reason.
- **Mutagen 0.54.4's link-type lists are incomplete** for Starfield, so "wrong type" also accepts any type the
  official masters themselves link to (`data\link-types-seen.txt`). After a game update that list can be out of date
  until a new sf-mod-check release relearns it.
- **BlueprintShips files load last** (0.12.0): the game loads the official `BlueprintShips-*.esm` files after every
  plugin, whatever Plugins.txt says, oldest file first. A mod's edit of a ship that such a file also holds never
  reaches the game, and no patch can fix that. sf-mod-check 0.11.0 and earlier put those files before every mod and
  showed such a mod as the winner; `conflict` now lists the change as lost and `patch` leaves it out.
- **An add-on's masters:** without `--masters-dir`, an add-on is checked against the main plugin installed in Data,
  which may be an older build than the one you just made.

## Known gaps

- A form parameter flagged "use aliases" isn't checked as an alias number: in the official masters many of them hold
  values such as -2 that can't be alias numbers, so Mutagen 0.54.4's reading of that flag isn't reliable enough for a
  rule. Conditions on other record types (perks, spells, condition forms) that name an alias aren't checked either,
  because those records have no quest of their own.
- In NAVI, only the traversal entries are read directly; edge, preferred and door links come through Mutagen and are
  checked as links.
- "Missing required field" is a listing, not a finding (0.10.0). The log (`--verbose` or `--log`) lists each record
  that lacks a field every official record of its type has (learned from the official masters into
  `data\required-fields-seen.txt`). It isn't a WARNING because many mods that work in the game lack fields the
  Creation Kit always writes (such as a dialogue topic's info count), so "every official record has it" doesn't show
  the game needs it.
- Not checked: face data, and what is inside loose files or BA2 archives (the `assets` check only looks up the names
  of attached scripts and models).
- `check` checks one plugin against its masters. Conflicts between mods are `sf-mod-check conflict`'s job.
- The `mutagen-gap` rule compares fields only. A quest's `Unknown` value in Mutagen isn't a field: it is the last 4
  bytes of the header of the quest's child group (its dialogue and scenes). Mutagen 0.54.4 writes 0 there whatever
  the value was, so a Mutagen-built plugin that overrides a quest with dialogue has 0 there, and the rule doesn't
  report it. No effect in the game has been seen. `patch` writes no child group for a quest, so it doesn't carry the
  value either.
- A plugin's archives are every `<plugin> - *.ba2` (Mutagen's rule). That also takes archives with any suffix
  (`- Main_xbox.ba2`, `- Main01.ba2`) and a longer plugin's archives (`Foo - Patch - Main.ba2` counts for `Foo.esm`
  when `Foo - Patch.esm` isn't active), so the archive count and the archives searched for assets can be a little
  wider than what the game opens.

## Build from source

Needs the [.NET SDK](https://dotnet.microsoft.com/download) 9 or later.

    dotnet build src -c Release
    dotnet test tests/SfModCheck.Tests -c Release

Package versions are fixed by the lock files (`packages.public.lock.json`): Mutagen comes through
[BuildKit](https://github.com/taosecurity/BuildKit) 0.3.1 from nuget.org. The tool package and the stand-alone exe:

    dotnet pack src -c Release -p:SfModCheckPack=true -o out
    dotnet publish src -c Release -r win-x64 -p:SfModCheckStandalone=true -o out/exe

The tests make every master and plugin they check in a temp folder, so they need no game files. A test also checks
that every option in `src/Program.cs` is named in this README and in AGENTS.md. Five of them need
the real Starfield.esm (engine-made records and the official scene list) and are skipped when it isn't found; set
`SFMODCHECK_TEST_DATA` to your Data folder to run them. The tests never touch your real caches.

### Data files built into the exe

- `data\engine-forms.txt`: records the game's engine creates, which plugins may link to without any master having
  them. From xEdit's hardcoded list; see [NOTICE](NOTICE).
- `data\link-types-seen.txt`: for each Mutagen link type, the record types the official masters link to, and how often.
- `data\condition-params-seen.txt`: for each condition function and parameter, how often the official masters use it
  and how often they leave it empty.
- `data\condition-functions-seen.txt` (0.10.0): the condition function numbers the official masters use that
  Mutagen's list lacks, with how often, so the `conditions` check doesn't flag them.
- `data\required-fields-seen.txt` (0.10.0): for each record type with at least 20 official records, the fields
  every one of them has (record types, field signatures and counts only), for the fields listing in the log.
- `data\creations-masters.txt` (0.10.0): for `release --profile creations`, the masters a Creation may require
  (Bethesda's plugins by name pattern, taken 2026-11-18) and the script names that come from SFSE plugins.

The link-type, the two condition and the required-fields files hold counts, numbers, field signatures and record
type names only. They are relearned from the official masters after a game update with the hidden commands
`--learn-link-types <out> [Data]`, `--learn-conditions <out> [Data]`, `--learn-condition-functions <out> [Data]` and
`--learn-required-fields <out> [Data]`, then built in again.

- `data\field-priorities.txt` (0.10.0): xEdit's conflict priority (ignore, benign, normal or critical) for each
  field of each Starfield record type, under Mutagen's field names, used by `conflict` (see "Field priorities"). It holds priorities as data only, no xEdit code
  and no game data, and stays under the Mozilla Public License 2.0; see [NOTICE](NOTICE).

The field-priority table is made from source code, not from the game. To make it again (Python 3.10 or later, git):

    python -I data/gen/gen_field_priorities.py --xedit-core <TES5Edit clone>/Core --mutagen-repo <Mutagen clone>

The xEdit clone should be at commit b0e67b16 (release 4.1.5r); the Mutagen clone needs commit 0188012c (the
Mutagen.Bethesda.Starfield 0.54.4 package), which is read with `git archive`, so its checkout can be anywhere.
`--json <file>` also writes the full view (every xEdit member and the values inside it), `--log <file>` or
`--verbose` records what was read and decided, and `python -I data/gen/test_gen_field_priorities.py` is the
generator's self-test. Exit codes: 0 written, 1 an input is missing (the message names it), 2 a source isn't given.

## Credits and license

sf-mod-check is © 2026 Richard Bejtlich, under the GNU General Public License version 3 ([LICENSE](LICENSE)).
`data\engine-forms.txt` is derived from xEdit's hardcoded Starfield list (TES5Edit, by ElminsterAU and the xEdit team)
and stays under the Mozilla Public License 2.0; see [NOTICE](NOTICE). Field conflict priorities derived from xEdit
(https://github.com/TES5Edit/TES5Edit), MPL-2.0: `data\field-priorities.txt` keeps that license. Mutagen is by Noggog and the Mutagen
contributors (GPL-3.0). See [PRIVACY.md](PRIVACY.md) for what the tool reads and writes (it makes no network
connections), and [CHANGELOG.md](CHANGELOG.md) for changes.
