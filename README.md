# sf-mod-check

sf-mod-check finds problems in a Starfield plugin before you play it or release it. It only reads files and never
changes a plugin, and it checks a typical mod in about 4 seconds.

Install it with `dotnet tool install --global sf-mod-check` (from nuget.org, since 0.18.0), or download the exe zip
from [Releases](https://github.com/taosecurity/sf-mod-check/releases) and unzip it ([Install](#install)). It has eight
commands:

- [`check`](#run-it) reads one plugin (`.esm`) and reports problems that would break it in the game: damaged files,
  bad headers, FormIDs outside the plugin's range, links to records that don't exist, broken conditions, scenes and
  navmesh links, repeated EditorIDs, deleted records, ITM records and edits to official Story Manager and Planet
  Content Manager nodes. After each kind of problem it says how to fix
  it in xEdit or the Creation Kit. For a ship mod, it also lists each ship's modules and the systems it lacks.
- [`conflict`](#conflicts-between-mods-sf-mod-check-conflict) reads your whole load order and reports which changes
  it throws away (official changes undone by mods, one mod's changes lost to another) and what to do about each.
- [`patch`](#build-a-conflict-patch-sf-mod-check-patch) builds a local patch that brings back the changes `conflict`
  found lost, without xEdit or an AI assistant. [`patch check`](#is-the-patch-still-up-to-date-sf-mod-check-patch-check)
  then tells you when that patch is out of date because a mod was added, removed, moved or updated.
- [`release`](#check-a-release-zip-sf-mod-check-release) checks the zip (or the stage folder) you are about to
  upload: plugin names the game drops, archives it never loads, missing scripts, models, `.mesh` geometry, materials,
  textures and sounds, sounds packed so they play silent, file names archive tools can't handle, scripts or plugins
  that differ from the build you tested, staged files left out of the archives, Creations rules, and every plugin
  inside.
- [`gap`](#check-a-plugin-for-mutagen-losses-sf-mod-check-gap) checks a plugin written with the Mutagen library for
  the data Mutagen is known to lose.
- [`triage`](#look-over-a-downloaded-mod-sf-mod-check-triage) looks over someone else's mod before you install or
  rebuild it: its files, what it replaces in the game, its plugins, scripts and DLLs, in one offline report. It is
  triage, not a verdict: it shows a person where to start reading.
- [`diff`](#compare-two-versions-of-a-mod-sf-mod-check-diff) compares two versions of a mod and lists what changed.
- [`find`](#look-up-a-record-sf-mod-check-find) looks up one record in your load order by EditorID, FormKey or
  console FormID, and says which plugin defines it, which wins it, and what its main fields hold. With `--plugin`
  it reads one plugin and its masters from any folder, and with no record given it lists every record in that plugin.

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

Two ways. Each file on the [Releases](https://github.com/taosecurity/sf-mod-check/releases) page has a
`.sha256` file next to it with its SHA-256 checksum.

- **dotnet tool from nuget.org** (Windows or Linux, needs the [.NET SDK](https://dotnet.microsoft.com/download) 9 or
  later; since 0.18.0):

      dotnet tool install --global sf-mod-check

  After that, `sf-mod-check` works from any prompt. `dotnet tool update --global sf-mod-check` installs a newer one, and
  `dotnet tool uninstall --global sf-mod-check` removes it. Without nuget.org, download `sf-mod-check.<version>.nupkg`
  from the Releases page into a folder and add `--add-source <that folder> --version <version>` to the install command.
- **Stand-alone exe** (Windows, no .NET needed): `sf-mod-check-<version>-win-x64.zip` from the Releases page. Unzip it and
  run `sf-mod-check.exe`, or drop plugins on it.

Coming from sfmodcheck 0.7.0 or older: the package has a new name, so remove the old one first with
`dotnet tool uninstall --global sfmodcheck`.

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
  add-on is checked against the main plugin you just built, not an older copy installed in Data. The plugin's own
  folder is still searched for its own archives and loose files (since 0.16.2; 0.16.1 and earlier missed them there).
- `--max`: the most lines printed per check (default 100). The counts in the RESULT line are always complete.
- `--quiet`: leave out the first summary line.
- `--no-hints`: leave out the HINT lines.
- `--no-assets`: skip the assets check (missing scripts, models, materials, textures and sounds), for example when the plugin's archive isn't
  built yet. If the assets check itself can't finish (a folder that can't be listed, a disk error), the other checks
  still count and one `WARNING assets` line says the scripts, models, materials, textures and sounds weren't checked.
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

Names taken from a plugin, a script or a zip are written on one line, here and in the findings: a control character
(a newline, a tab), a Unicode line break or an invisible format character (a right-to-left mark such as U+202E, a
zero-width space) shows as `\n`, `\t` or `\uXXXX`. Ordinary names, accented letters and dashes print as they are.
When one log entry runs over several lines (an error with its stack trace), the lines after the first are indented
by two spaces.

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

For every command, `--help`, `-h` and `<command> --help` print the help on stdout and exit 0 with no RESULT line (since 0.13.0; earlier versions exited 2). A bad argument still exits 2 with its RESULT line, also when `--help` comes after it.

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
case or undocumented fields; since 0.13.0 letter case also covers a file name stored as raw bytes, such as a city-map
component's file) or, since 0.12.0, **Lost** (an official `BlueprintShips-*.esm` file wins, and nothing
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
its author saw. A BlueprintShips file whose master is a BlueprintShips file that loads after it gets a load-order
problem as well: the files keep their order (oldest first, then by name).
A mod's change that a BlueprintShips file undoes is listed among the lost changes, with that file as the winner,
and its recommendation says so plainly: the file loads after every mod, so no load order or patch brings the change
back, and the mod's edit to that ship doesn't reach the game (the advice names the record type when it isn't a
ship). This includes a mod's edit of a ship the BlueprintShips file adds itself (the mod has that file as a master),
judged against the file's own version.
The navmesh info map is the exception: the BlueprintShips files carry a copy of it too, so they have its last copy,
but each plugin's copy lists only its own navmeshes. It isn't a record one plugin wins whole, so it keeps its own
note (it isn't patched, and shouldn't be copied by hand) instead of the "doesn't reach the game" advice.
Since 0.15.0 the pair tables (one row per winner and loser in report.md and report.html) have a **Lost fields**
column next to Choice fields, and a pair's heading in report.html gets an "n lost" badge, so a pair whose changes a
BlueprintShips file takes back stands out. Before, those fields were counted in no column. conflicts.json is
unchanged. A BlueprintShips file that a runtime tool (such as an SFSE load-order dump) lists as touching a mod's own
new ship isn't a lost change: it doesn't override that ship's record, so no row counts it.

The RESULT line's "official" count is the official changes undone by mods. A mod's change lost to a BlueprintShips
file is a mod's loss, so it is counted with the "mod vs mod" entries (the line keeps its 0.11.0 shape for scripts).

**First command** (Steam, with the game's own load order):

    sf-mod-check conflict

Then open `sf-mod-check-conflict\report.html` in a browser.

    sf-mod-check conflict [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]] [--data <Data folder>]
                        [--extra-data <folder>] [--out <folder>] [--max-memory <GB>] [--no-assets] [--no-priorities]
                        [--ini <file>]... [--language <code>] [--quiet] [--verbose] [--log <file>]

- `--plugins`: the load order to check (default: the game's own, `%LOCALAPPDATA%\Starfield\Plugins.txt`, which
  Vortex and the game's Creations menu keep up to date). Only the lines marked active (`*`) count. Bethesda's own files
  (the DLCs, updates and Creations such as the Trackers Alliance ones) are always loaded first, as the game does.
  Without `--plugins` and without the game's file, it stops and asks for one rather than guess an order.
- `--mo2 <MO2 folder>` and `--profile <name>` (0.16.0): read the load order and the mods from a Mod Organizer 2
  profile instead (see "Mod Organizer 2" below). `--mo2` takes the MO2 instance folder, the one that holds
  `ModOrganizer.ini`, `profiles` and `mods`. `--profile` defaults to the profile selected in MO2. Give `--mo2` or
  `--plugins`, not both (exit 2), and `--profile` only with `--mo2`.
- `--data`: the game's Data folder, where the plugins are read (default: Steam's usual one; with `--mo2`, the
  `Data` folder in MO2's game path first; Game Pass: `--data "C:\XboxGames\Starfield\Content\Data"`).
- `--extra-data`: a second folder of plugins, read before the Data folder (its files win), for example a patch you
  have built but not installed yet. The load order still comes from `--plugins`, so add the patch's line there too.
  With `--mo2` it wins over MO2's folders too.
- `--out`: the report folder (default `sf-mod-check-conflict` in the current folder). It gets `report.html` (the one to
  read: summary, recommended actions, each conflict with its story, a filter box, light and dark themes), `report.md`
  (the same as Markdown) and `conflicts.json` (everything, for scripts).
- `--max-memory`: stop cleanly when memory passes this many GB (exit code 3, no report), instead of slowing the PC.
  It takes a number of GB from 0.000001 to 1000000, for example 8.
- `--no-assets`, `--ini <file>`, `--language <code>`: the assets check, as for `check` (0.6.0).
  - **Where it looks:** in `conflict` it looks up every mod plugin's scripts, models, materials, textures and sounds
    in the archives of every active plugin (`<plugin> - *.ba2`), the archives the INIs list, and loose files (the `--extra-data` folder, then
    with `--mo2` MO2's Overwrite folder and the enabled mods in MO2's order, then Data). Archives are found in the
    same folders, in the same order.
  - **What it skips:** Bethesda's own plugins aren't checked. An override isn't blamed for a missing file the version
    it overrides already names (the record in the last of the plugin's masters, in load order, that has it, as in
    `check`). That one is reported once, against the plugin that added the path, or not at all when it's Bethesda's
    (conflicts.json `inheritedAssetUses` counts the uses left out).
  - **Memory:** those lookups obey `--max-memory`. Past the limit they stop, every missing path is reported, and
    "Tool limits hit" says so.
  - **Results:** the RESULT line adds ", N missing assets (S scripts, M models)" when there are any, with ", T
    textures", ", A materials" and ", D sounds" added when there are some (0.14.0; materials, textures and sounds as
    for `check`). The report's "Missing assets" section lists them by plugin (scripts, an ERROR, then models,
    materials, textures and sounds, each a WARNING), with the number of archives read and any archive that can't be
    read. Each plugin with missing files gets a recommended action. conflicts.json has the scripts and models as
    `missingAssets` (`kind` is `script` or `model`, as before 0.14.0), the materials, textures and sounds as
    `missingFiles` (since 0.14.0, the same keys, `kind` is `material`, `texture` or `sound`), and the archives that
    can't be read as `unreadableArchives`. A mod's `.nif`, `.mat`
    or `.bnk` that can't be read, a sound bank of a version the tool doesn't know, or materials that couldn't be
    checked against the game's database, are listed in "Tool limits hit" and counted as assets-check notes. With `--no-assets` the section says the check was off.
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

**Mod Organizer 2 (0.16.0):** MO2 keeps each mod in its own folder and shows one merged Data folder to the game and
to any program MO2 starts, only while that program runs, so a plain run sees only the plugins in the real Data
folder. `--mo2` reads the profile the way MO2 builds that merged folder:

    sf-mod-check conflict --mo2 "C:\Modding\MO2" --profile Default

- **The order** comes from the profile's `loadorder.txt` when it is there, and the on/off marks from its
  `plugins.txt` (with no `loadorder.txt`, `plugins.txt` gives both). Bethesda's own files are placed as the game does.
- **The files** (plugins, archives and loose files) are looked up in `--extra-data` first, then MO2's Overwrite
  folder, then each enabled mod from the top of `modlist.txt` down (MO2's own priority, highest first), then
  `Documents\My Games\Starfield\Data` when it exists (0.16.1: MO2 merges that folder into its view too), then Data.
  Disabled mods, separators and unmanaged entries are skipped.
- **The folders** come from `ModOrganizer.ini`: the selected profile, `gamePath` (its `Data` folder is used when you
  give no `--data`), and the profiles, mods and overwrite folders, with `%BASE_DIR%` filled in. A portable instance
  and a global one work the same way: give the folder that holds `ModOrganizer.ini`.
- **Problems** go in the report's load-order problems: an enabled mod with no folder, a `modlist.txt` line that isn't
  a single folder name, or a profile with no `modlist.txt` (then only Overwrite, the My Games Data folder and Data are searched).
- **Limits:** archives listed only in a profile's own INI files (MO2's profile-specific INIs) aren't read; give them
  with `--ini`. `check`, `triage` and `patch check` don't take `--mo2` yet: give `check` the plugin's path inside
  `mods\` with `--masters-dir` (see "Where your game's Data folder is"). Which of the My Games Data folder and the
  game's Data folder wins a file that is in both hasn't been checked; this tool lets the My Games copy win.
- It reads the instance only and never writes there. It was tested on made-up MO2 folders; check the report's
  load-order line (`MO2 profile <name> (<folder>), <n> active entries`) and its problems the first time.

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

**What it doesn't check:** the scripts' code and assets other than attached scripts, model paths, materials,
textures, the `.mesh` geometry of the mods' own models and the sounds of the mods' own sound banks (face data, which only `release` looks for, voice files, music; see the
`assets` entry under `check`). Fields
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
   - **A field pick works on any field** (since 0.13.0). On a field the patch would forward from another mod, it keeps
     the named plugin's value instead, and the report names the change it drops. On a record the patch wouldn't hold
     at all (the winner already has the other mod's value), it adds the record as the game uses it today, with that
     one field from the named plugin. For example, when a mod fills a worldspace's empty water field and you want the
     game's empty value back, use `--pick 0012AB:Starfield.esm:Water=Starfield.esm`. A whole-record pick still covers
     only Choice fields.
4. A record that would end up the same as today's is left out (no "identical to master" records).
5. It writes the patch, then reads it back and compares every field with the plugin it was copied from.

A record whose change is lost to an official `BlueprintShips-*.esm` file isn't built (0.12.0): the game loads those
files after every plugin, the patch too, so their version would still win. The report lists it under "Not built"
with that reason, and the RESULT line counts it among the records not built. There is nothing to do by hand for it
either. The navmesh info map keeps its own reason even when a BlueprintShips file has its last copy. When a value
the patch copies points at a record a BlueprintShips file defines, the patch gets that file as a master, and the
report's Notes say so: the game still loads that master after the patch, as it does for a mod with such a master.

    sf-mod-check patch [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]] [--data <Data folder>]
                       [--extra-data <folder>] [--out <folder>]
                       [--name <Patch.esm>] [--only <a.esm>[,<b.esm>...]] [--pick <FormKey>[:<Field>]=<plugin>]...
                       [--include-official] [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]

- `--plugins`, `--mo2`, `--profile`, `--data`, `--extra-data`, `--max-memory`, `--quiet`, `--verbose`, `--log`: as
  for `conflict`. With `--mo2`, add the patch to MO2 as a mod of its own: Create empty mod, copy the patch into the new
  mod's folder, enable it, and put the patch at the end of the plugin list (the patch report says so too). `--out` may
  not be the Data folder, the `--extra-data` folder or an MO2 mod or Overwrite folder.
- `--out`: the folder for the patch and its report (default `sf-mod-check-patch` in the current folder). It can't be
  the Data folder or the `--extra-data` folder: install the patch with your mod manager.
- `--name`: the patch's file name (default `ConflictPatch.esm`). A name that ends in an official plugin's name (such
  as `-ShatteredSpace.esm`) or starts with `BlueprintShips-` is refused, because the game drops such names from
  Plugins.txt. If your load order already has a plugin of that name (last time's patch), it is left out while the new
  one is built, and the new one replaces it.
- `--only`: patch only the conflicts that involve these plugins (file names, separated by commas; give it more than
  once if you like). It is also faster: only the records those plugins change are compared.
- `--pick`: see step 3. A pick that matches nothing gets a WARNING. For a field pick, the WARNING says why: the
  record type has no such field, the plugin named doesn't hold the record (or its partial copy doesn't set that
  field), no mod overrides the record, the record isn't built (with the reason from the "Not built" list), or a
  later `--pick` on the same field decides it. Field picks on one navmesh's mesh that name two plugins stop the run
  (exit 2): see Navmeshes below.
- `--include-official`: also bring back changes that one official plugin (the base game, a DLC or a Creation from
  Bethesda) loses to another, for example a reference one Trackers Alliance mission adds to a location and a later
  one drops. Off by default, because `conflict` doesn't list such losses either: the game ships that way, and they
  may be meant. It also compares the records only official plugins change, so the run takes a little longer. It
  can't be combined with `--only`. The report says when a patch was made with it.

**What it writes** (all in the `--out` folder): the patch; `patch-report.md` (each record and field it forwarded and
from which plugin, the Choice fields with their `--pick` lines, the records it didn't build, and the next steps; a
list both plugins changed is listed under Choices too, as "merged entry by entry", with no `--pick` line, though
`--pick <FormKey>:<Field>=<plugin>` still takes that plugin's whole list if you prefer it); and
`Plugins-with-patch.txt`, your load order with the patch at the end; and `patch-sources.json`, a record of what the
patch was built from (the load order by file name, each master's size, date and SHA-256, and for each patched record
the plugins whose copies it used, each with a short hash of that copy, so a later check can tell whether a mod update
really changed a patched record; saving a plugin again without changes keeps its hashes). When there is nothing to
patch, an old patch, its `Plugins-with-patch.txt` and its `patch-sources.json` are removed. To check the patch before
installing it:

    sf-mod-check conflict --plugins sf-mod-check-patch\Plugins-with-patch.txt --extra-data sf-mod-check-patch
    sf-mod-check check sf-mod-check-patch\ConflictPatch.esm

That `conflict` run still lists the records the patch didn't build, any Choice that kept the winner's value, and
lost changes that `conflict` already calls harmless ("probably has no visible effect": a change that only reorders a
list, changes letter case or touches undocumented fields), which the patch leaves alone.

**Cells and worldspaces** (since 0.13.0) are patched like other records, but only the record itself: a cell goes in
without its placed objects and navmeshes, and a worldspace without its cells. An exterior cell can only sit inside its
worldspace, so when the patch has no reason of its own to hold that worldspace, it goes in as the game uses it today,
with no field changed, and the report says so. A patched worldspace never carries its large-reference table (RNAM) or
cell offsets (OFST, CLSZ): they index the objects and cells of the plugin that holds them, Creation Kit overrides don't
carry them either, and a stray RNAM table in a winning worldspace has crashed the game on landing. A cell's traversals
are one value: the patch takes them whole from one plugin, never entry by entry.

**Placed objects** (since 0.13.0; REFR, ACHR and the other placed types) are patched like other records too. Each one
goes into the cell, and the cell's persistent or temporary list, where the last plugin holding it has it. A placed
object can only sit inside its cell, so when the patch has no reason of its own to hold that cell, the cell goes in as
the game uses it today, with no field changed and none of its other objects, and the report says so (an exterior
cell then brings its worldspace by the rule above).

**Navmeshes** (since 0.13.0) go into their cell the same way, with the cell as the game uses it today when the patch
has no reason of its own to hold it. A navmesh's mesh (its geometry, its NNAM data and its pre-cut entries) describes
one mesh as the Creation Kit finalized it, so the patch takes all three from one plugin, never field by field or entry
by entry. When one plugin changed one part and another plugin's copy differs in another part, a mix would be a mesh
nobody made: the patch keeps today's mesh and the report lists it under choices. The `--pick` rules for a mesh are
kept simple:

- A field `--pick` on any mesh field (`NavmeshGeometry`, `NNAM` or `PreCutMapEntries`) takes that plugin's whole
  mesh, and the report's one row says so: every other plugin's change to the mesh is dropped. A pick that names
  today's mesh and drops no change counts as used but adds no row. The plugin's copy must hold the whole mesh: a
  partial copy that lacks a part gets a "was not used" warning.
- Field picks on one navmesh's mesh that name two different plugins stop the run with exit 2 and an error naming
  both picks. Keep the one for the plugin whose mesh you want.
- A whole-record `--pick` takes the whole mesh only when the mesh is one of its Choice fields (the winner and another
  plugin both changed a mesh field) and no field pick names the mesh. Otherwise it leaves the mesh alone, as it
  leaves every forwarded field alone; if it takes nothing else, its warning says so and gives the field pick to use.
- A field pick on the mesh ranks above a whole-record pick, as on every other field. A whole-record pick left with
  nothing to take gets a "was not used" warning that says so.

**Quests and dialogue** (since 0.19.0) are merged more finely than other records:

- A quest's **stages and objectives** merge by their Index, and its **aliases** by alias ID. Each plugin's own new,
  changed and removed entries are kept. When two plugins both changed one stage or objective, its fields merge one
  level down (its log entries and targets merge entry by entry, but when both edited or removed the same log entry
  or target, the winner's whole list is kept and listed under Choices); when both changed one alias, added the same key, or
  made an alias of another kind, the winner's entry is kept and the report lists it under Choices. Before 0.19.0 these
  lists were merged as plain lists, which could write two stages with one Index; that is fixed.
- A quest's **condition lists**, a dialogue line's conditions, its responses and its script data are each one value:
  taken whole from one plugin, never entry by entry.
- A quest's **script data** merges by script name and property name. Its stage fragments merge by stage only when both
  plugins use one fragment script and that script (read from loose files, then the archives, last-loaded plugin
  first) has a function for every fragment kept; otherwise the winner's whole script data is kept with a Choice row
  that says why.
- **Dialogue topics and dialogue lines** (DIAL and INFO) are built. A line goes into its topic and a topic into its
  quest; when the patch has no reason of its own to hold that topic or quest, it goes in as the game uses it today,
  with no field changed and none of its other topics or lines, and the report's Notes say so. A topic's list of lines
  merges by line, and a line one plugin added goes right after the line before it in that plugin's list. A line in two
  topics, a topic in two quests, or a line or topic whose parent no plugin holds is not built, and the report says why.
- After a quest is built, an objective target or fragment alias that names an alias the merged quest no longer has
  (because another plugin removed or replaced it) gets a Notes line telling you to check it in xEdit.

**What it doesn't build yet:** scenes and dialogue branches (SCEN and DLBR), so the report lists them for xEdit
instead ("scenes and dialogue branches are not built by this version; copy the fields in xEdit"). The navmesh info map (NAVI) is never patched: each plugin's copy lists only its own navmeshes, so a merged copy would
hide entries.

**Rebuild it when your mods change.** The patch lists every plugin it took values from as a master. After you add,
remove or update one of them, run the same command again (the old patch is replaced). `patch check` (below) tells you
when that is needed.

### Is the patch still up to date? `sf-mod-check patch check`

Since 0.15.0, `patch check` reads the `patch-sources.json` beside a patch and compares it with your load order as it
is now. It reads files only and changes nothing.

    sf-mod-check patch check sf-mod-check-patch\ConflictPatch.esm --plugins "%LOCALAPPDATA%\Starfield\Plugins.txt"

**Example** (made-up mods: NewModA.esm was updated and changed a record the patch holds, and NewModC.esm was added):

    > sf-mod-check patch check sf-mod-check-patch\ConflictPatch.esm
    sf-mod-check 0.15.1 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check
    Loaded 160 plugins (0 load-order problems) in 1 s
    REVIEW [updated] master NewModA.esm changed since the build (size 48211 -> 48390) and changed 1 record ConflictPatch.esm holds (00E39C:Starfield.esm): rebuild ConflictPatch.esm.
    REVIEW [new-editor] new plugin NewModC.esm changes 2 records ConflictPatch.esm holds (00E39C:Starfield.esm, 0012AB:Starfield.esm): rebuild ConflictPatch.esm to include it.
    RESULT: REBUILD (2 reasons)

    sf-mod-check patch check <patch.esm> [--manifest <patch-sources.json>] [--plugins <Plugins.txt>] [--data <Data folder>]
                             [--extra-data <folder>] [--json <file>] [--quiet] [--verbose] [--log <file>]

- `--plugins`, `--data`, `--extra-data`: the load order to check against, as for `patch`. Before you install the
  patch, use `--plugins sf-mod-check-patch\Plugins-with-patch.txt --extra-data sf-mod-check-patch`.
- `--manifest`: the `patch-sources.json` to read (default: the one beside the patch).
- `--json <file>`: also writes the findings as JSON: `patch`, `state` (`up to date`, `rebuild`, `not active` or
  `cannot check`), `result` (the RESULT line), and `findings`, each with `level`, `rule`, `plugin`, `text` and
  `records` (every FormKey, where the text shows up to 5).

What it reports (each line names the plugin and what to do):

- PROBLEM `[removed]`: a master of the patch isn't in the Data folder, isn't active, or can't be loaded. The patch
  won't load without it: rebuild.
- REVIEW `[moved]`: a master now loads after the patch (the game won't load a plugin before its master), or a plugin
  that loaded before the patch when it was built now loads after it and changes a record the patch holds. Move the
  patch last, or rebuild. Official `BlueprintShips-*.esm` files always load last and are never "moved".
- REVIEW `[updated]`: a master's file changed (size or SHA-256) and its copy of at least one patched record changed
  too: rebuild. NOTE `[updated]` when the file changed but none of the patched records did. The record hashes in
  `patch-sources.json` tell the two apart, with no old copy of the mod needed.
- REVIEW `[new-editor]`: an active plugin that isn't a master of the patch now changes a record the patch holds (a new
  mod, or an update to one that didn't touch it before): rebuild to include it.
- REVIEW `[duplicate]`: another spelling of the patch's name (differing only in spaces or letter case, another build of
  it) is active too: keep only one.
- REVIEW `[patch-changed]`: the patch file in the load order isn't the build the manifest describes, so the other
  findings judge the manifest's build: make the patch again with `patch`.
- NOTE `[patch-off]`: the patch isn't in the load order, so nothing else is checked.

The last line is `RESULT: UP TO DATE`, `RESULT: REBUILD (n reasons)` (the PROBLEM and REVIEW lines),
`RESULT: NOT CHECKED (...)` (the patch isn't in the load order, so nothing was checked) or
`RESULT: CANNOT CHECK (...)`. **Exit codes:** 0 = up to date (NOTE lines only) or not checked (the patch isn't active), 1 =
something to rebuild or review, 2 = couldn't check (no `patch-sources.json`, a schema this version doesn't know, a
patch or load order that can't be read, a bad argument). `--help` prints the usage and exits 0.

**For your own game only:** the patch holds other authors' data, so don't publish it.

**Exit codes:** 0 = patch written (or nothing to patch: the RESULT line says so and no plugin is written); 1 = the
patch was written but reading it back found a difference: don't use it, and report it with a `--log` file; 2 =
couldn't run (`RESULT: CANNOT BUILD`); 3 = stopped at `--max-memory` (`RESULT: STOPPED`). The last line on stdout is
always the RESULT line, for example:

    RESULT: OK, patch C:\Mods\sf-mod-check-patch\ConflictPatch.esm: 3 records, 5 fields forwarded, 0 picks used, 1 shared lists merged, 1 choice kept the winner's value, 2 records not built, 6 masters (report: ...\patch-report.md; 9 s)

## Check a release zip: `sf-mod-check release`

Run it on the zip you are about to upload to Nexus, Bethesda Creations or a friend. It unpacks the zip into a temp
folder (always removed afterwards) and says OK or FAIL for what would break the mod for a player: a plugin name the
game drops, an archive the game never loads, a script or file the mod needs that isn't in the zip, sound files packed
so the game plays them silent, and everything `check` finds in each plugin.

    sf-mod-check release MyMod-1.0.0.zip

    sf-mod-check release <zip|folder|ba2|plugin>... [--data <Data folder>] [--masters-dir <folder>]...
                                [--master-archive <ba2>]... [--dmmdeps <exe>]
                                [--papyrus <exe> --ck-sources <ContentResources.zip>]
                                [--psc <folder>]... [--psc-import <folder>]...
                                [--profile basic|house|creations] [--creations-master <plugin>]... [--size-total]
                                [--tested <zip|ba2>]... [--stage <folder>]
                                [--accept <rule>:<plugin>:<detail>]... [--accept-file <file>]...
                                [--quiet] [--verbose] [--log <file>]

- `<zip>`: the release zip. Give an add-on's zip together with its main mod's zip, so the add-on's plugin finds
  the main plugin and its scripts.
- `<folder>`, `<ba2>`, `<plugin>` (0.15.0): to check a build before you zip it, give its stage folder (laid out like
  Data), or its `.ba2` and plugin files, instead of a zip. A folder is read where it is, and nothing is written into
  it. The loose `.ba2` and plugin files of one run are checked together as one package named `loose files`, so an
  archive and its plugin find each other wherever they sit; two loose files of the same name stop the check (exit 2).
  The zip-only rules (`one-plugin`, `root-md`) skip a folder or the loose files, and one NOTE (rule `not-a-zip`) says
  so; everything else runs as for a zip. You can mix zips, folders and loose files in one run.
- `--data`, `--masters-dir`: as for `check`. Masters and archives of mods the zip builds on go in a `--masters-dir`
  folder.
- `--master-archive <ba2>`: the archive of the mod your plugin patches. Scripts, models, materials, textures and
  sounds in it count as present
  (a patch that overrides another mod's records keeps that mod's fragment scripts there). Can be given more than once.
- `--dmmdeps <exe>`: also run DMMDeps (`dmmdeps-fat.exe`, from ZeeOgre's DevModManager) to find files the plugin uses
  that the zip lacks. sf-mod-check runs only the reviewed v0.4.1 build and refuses any other, by its SHA-256
  (`8a5367865ef21e29b17301a3341b2195fdacbf163c151678c0c420747ff9e6f8`). DMMDeps has gaps (its issues #175 to #177:
  base scripts, face files, some models), so what it can't see is a REVIEW line and never "not needed". Without it,
  one NOTE says that only sf-mod-check's own check of the files the plugins need ran (scripts, models, the `.mesh`
  geometry of the mod's own models, materials, textures, NPC face data and the sounds of the mod's own sound banks;
  the voice files are checked on their own, rule `voice-missing`). DMMDeps runs on a copy in the temp folder and takes a
  minute or more on big mods (15 minutes at most).
- `--papyrus <exe>` with `--ck-sources <ContentResources.zip>`: lint the `.psc` sources a zip ships under
  `Scripts\Source`, with papyrus-tools v0.16.3 (SHA-256 `954ec6d7559713c8cdf6aaaf20bde5d9feedf52a6404a90cfca5b2107559c4fa`)
  and the vanilla sources from the Creation Kit's `Tools\ContentResources.zip`. Every finding is a WARNING. Without
  the vanilla sources the lint is skipped with a WARNING (it would only report noise).
- `--psc <folder>` (0.15.0): a script source folder to lint instead of the zips' `Scripts\Source`, so a release that
  ships no sources can still be linted. Give the folder above the namespace folders (the one that holds `MyMod\` for
  `MyMod:Quest`). A finding's path is shown relative to this folder. Can be given more than once.
- `--psc-import <folder>` (0.15.0): sources of another mod your scripts extend or call, added to the lint's imports
  after the vanilla sources, so its names are known. Can be given more than once.
- Both need `--papyrus` and `--ck-sources`; without them one NOTE says the folders were not used. A `--psc` or
  `--psc-import` folder that doesn't exist stops the check (exit 2) and names the option and the path.
- `--profile basic` (default): only what breaks the mod in the game makes it FAIL. `--profile house`: also fails on
  packaging rules (below), which `basic` only shows as WARNING lines.
- `--profile creations` (0.10.0): the `basic` rules plus Bethesda Creations' rules for PC, Xbox and PlayStation (the
  `creations-` rules below). Use it before you upload a Creation. Packaging rules stay WARNING lines, as in `basic`.
- `--creations-master <plugin>`: with `--profile creations`, a master by another author that you have seen on
  Creations for every platform your Creation targets. It is then allowed for this run. Can be given more than once.
- `--size-total` (0.15.0): with `--profile creations`, count every input of the run as one Creation for the 2 GB
  rule, for a Creation you check in parts (a stage folder and its archives, say):
  `sf-mod-check release C:\Build\MyMod\Data "C:\Build\MyMod - Textures.ba2" --profile creations --size-total`.
  Without it each input is its own Creation. Without `--profile creations` it is a bad argument (exit 2).
- The Creations check runs on `creations-out` (0.16.0 note): a mod built with sf-mod-build and `"xbox": true` writes the set you upload (the plugin, the PC archive and its `_xbox.ba2` twin) into a folder named `creations-out`. Check that folder, `sf-mod-check release creations-out --profile creations`, not the PC test zip, which has no Xbox archive.
- `--tested <zip|ba2>` (0.15.0): the zip, or one of the `.ba2` archives, of the build you tested in the game. Each
  compiled script of the release (loose or in its archives) is compared with the tested build's copy by meaning, not
  by bytes: two builds of the same source never match byte for byte, so the comparison leaves out what changes on
  every build (the string table's order, the order of variables, properties, functions and locals, the names of
  temporary variables, the compile time, and the user and machine that built it) and keeps the code itself. Each
  plugin is compared byte for byte with the tested plugin of the same file name (a `.ba2` alone has no plugin, so
  plugins are compared only when a zip is given). A difference is a REVIEW line, since it is expected only where you
  changed something after the test. Can be given more than once (a zip and its archives, or several archives).
- `--stage <folder>` (0.15.0): the folder the release was built from, laid out like Data. Every file in it must be in
  one of the release's archives or shipped loose in the zip; one that isn't is a PROBLEM, since the game won't see it.
  Archives, `.achlist` files, DMMDeps output (`*_deps.csv`, `*_deps.json`), plugins and `Scripts\Source` are left
  out, since they never go in an archive. A file in an archive that isn't staged is a REVIEW line (an archive left
  over from an older build?). A release with no archive gets one NOTE that this check was skipped.
- `--accept <rule>:<plugin>:<detail>` (0.15.2): accept one known finding, for a plugin you ship with a known gap in
  someone else's work (a script their plugin names that ships nowhere, say):
  `sf-mod-check release MyPatch-1.0.0.zip --accept script-missing:OtherMod.esm:OtherMod:Toggle`. The rule is one
  of `script-missing`, `model-missing`, `mesh-missing`, `texture-missing`, `material-missing`, `sound-missing` and
  `face-missing`; the plugin is the plugin's file name; the detail is the script's name for `script-missing` (a
  name with a namespace keeps its colon, as above) and the missing file's path for the others (as the finding
  prints it, either slash). Case doesn't matter. Wildcards (`*`, `?`) aren't allowed. The finding still prints, as
  an `ACCEPTED` line that names the entry, and no longer counts toward FAIL or the exit code. An entry that matches
  nothing is a WARNING (rule `accept`), so a stale entry shows. A malformed entry stops the check (exit 2) and says
  what is wrong with it. Can be given more than once. A finding from DMMDeps (`dmmdeps`) or any other rule can't be
  accepted.
- `--accept-file <file>` (0.15.2): the same entries from a text file, one per line. A `#` starts a comment that runs
  to the end of the line, and blank lines are skipped. An `ACCEPTED` line names the file and the line. Can be given
  more than once, and mixed with `--accept`.
- `--quiet`, `--verbose`, `--log <file>`: as for `check`. With `--verbose` the log names each script compared and,
  for a script that differs, the first line of the listing where the two builds differ.

Output is PROBLEM lines (fail), REVIEW lines (look at them), WARNING and NOTE lines, each with its rule in brackets,
then `ACCEPTED` lines (with `--accept`) and `RESULT: OK|FAIL (n problems, n to review, n warnings)` (with
`, n accepted` added when an entry accepted a finding), or `RESULT: CANNOT CHECK (...)` when it can't check
(exit 2). Exit codes: 0 OK, 1 FAIL, 2 can't check (bad arguments such as a malformed `--accept` entry or a missing
`--accept-file`, a zip, input folder, `--tested` file or `--stage`
folder that sits inside the game folder, two loose inputs of the same name, a zip that can't be unpacked, a missing file or folder such as a `--psc` folder, or an error that stops the
check). It never writes into the zip, an input folder, the game folder or the
Data folder.

**Example** (a made-up mod). `MyMod-0.1.0.zip` holds `MyMod.esm`, `MyMod - Main.ba2`, a second archive named
`MyMod_Extra.ba2` and a loose texture `textures\mymod\café.dds`:

    > sf-mod-check release MyMod-0.1.0.zip
    sf-mod-check 0.15.1 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check
    sf-mod-check 0.15.1 release (basic profile): MyMod-0.1.0.zip (4 files, 1 plugin, 2 archives)
    PROBLEM  [archive-name] MyMod-0.1.0.zip: archive MyMod_Extra.ba2: its name has no " - " part, so the game never loads it. The game loads an archive only when it is named "<plugin> - <part>.ba2" (with spaces around the dash) beside a plugin of that name, or when an INI lists it; rename it, for example MyMod - Main.ba2 for MyMod.esm
    WARNING  [file-name] MyMod-0.1.0.zip: textures\mymod\café.dds has a character outside plain ASCII (letters with accents, other scripts, symbols); archive tools and the game may not find it. Rename it with plain letters, digits and - _ .
    NOTE     [dmmdeps] DMMDeps wasn't given (--dmmdeps), so only sf-mod-check's own check of the files the plugins need ran: attached scripts, models, the .mesh geometry of the mod's own models, materials, textures, NPC face data and the sounds of the mod's own sound banks (the voice files are checked by voice-missing).
    NOTE     [plugin-check] MyMod.esm: OK, 0 errors, 0 warnings (full master, 0 masters, 0 records, 0 overrides (0 ITM), 0 links)
    NOTE     [mutagen-gap] MyMod.esm: 0 own records, 0 overrides, 0 compared with their master's copy
    RESULT: FAIL (1 problems, 0 to review, 1 warnings)

The exit code is 1: the game would never load `MyMod_Extra.ba2`. Renaming it `MyMod - Main01.ba2` fixes the PROBLEM
(beside `MyMod - Main.ba2`, the game also loads `MyMod - Main01.ba2` to `MyMod - Main99.ba2` without gaps, and
`MyMod - Textures.ba2` and `MyMod - Textures01.ba2` onward the same way, and a voice archive for each language
such as `MyMod - Voices_en.ba2`; since 0.15.1 any other part, such as `MyMod - Sounds.ba2`, or a number after a gap
is a WARNING),
and renaming the texture (and the material that names it) to plain ASCII clears the WARNING. A missing material,
texture, `.mesh` geometry or sound shows the same way, as a WARNING line that names the file and the record, `.nif`,
`.mat` or sound bank that needs it.

| Rule | Fails in | What it means |
|---|---|---|
| `plugin-name` | basic, house | A plugin named `...-ShatteredSpace.esm`, `-Constellation`, `-Starfield`, `-OldMars` or `-SFBGS...`, or `BlueprintShips-...`: Starfield drops it from Plugins.txt and it stops loading. Rename it, for example `-ShatteredSpaceAddon.esm`. |
| `voice-compressed` | basic, house | `.wem` or `.ffxanim` stored compressed in a `.ba2`: the game plays them silent. Pack with Archive2 `-compression=None`. |
| `voice-missing` | never (WARNING) | 0.18.0: a dialogue line the plugin adds has no voice file, so it plays silent. A voiced line plays from `sound\voice\<plugin>\<voice type>\<number>.wem`: `<plugin>` is the plugin's file name and `<voice type>` the EditorID of the voice type that speaks it (both in lower case), and `<number>` is the number each response of the line carries, as 8 hex digits with the top byte set to `00` (the game uses only the low 24 bits, so a response numbered `02d68230` plays `00d68230.wem`; a file named by the whole number gets a hint to rename it). When the line's conditions on the speaker (GetIsID on an NPC, or GetIsVoiceType on a voice type or a form list of them, equal to 1) name its voice types, each one needs its file; otherwise the file must be in at least one voice-type folder. Player lines (the player isn't voiced), lines with no responses, lines that share another line's audio (SharedDialog) and overrides of other plugins' lines aren't checked. The files are the plugin's own `<plugin> - Voices_<language>.ba2` archives, each language checked on its own, plus voice files loose in the zip or in its other archives, which count for every language. Up to 25 lines are named per plugin and archive, then one line counts the rest; `--verbose` lists every line. A plugin with dialogue and no voice files at all gets one NOTE (silent, subtitles only), and so do `.wem` files with no `.ffxanim` lip file beside them (the line plays, the lips don't move), lines whose GetIsID speaker has no voice type of its own (for example an NPC that takes its voice from a template: that speaker isn't checked) and a clean result. A plugin whose masters aren't all installed (in Data, beside it in the zips or in a `--masters-dir` folder) isn't checked and gets one NOTE saying so. |
| `ba2-unreadable` | basic, house | An archive whose header or records don't add up. |
| `archive-name` | basic, house | 0.14.0: a `.ba2` the game never loads, because it isn't named `<plugin> - <part>.ba2` (with spaces around the dash, `MyMod - Main.ba2` for `MyMod.esm`) beside a plugin in the zips, in the Data folder or in a `--masters-dir` folder. The Xbox twin (`MyMod - Main_xbox.ba2`) carries the same name and passes. A WARNING instead when the zip holds no plugin, or when an INI in the zip lists the archive (it loads only through that INI). Since 0.15.1, a WARNING also when the part after `<plugin> - ` is none of `Main`, `Textures` (each optionally numbered `01` to `99`) or a language's voice archive (`MyMod - Voices_en.ba2`, as the official add-ons ship them, passes; `MyMod - Sounds.ba2` doesn't), or when a numbered archive comes after a gap (`MyMod - Main03.ba2` with no `MyMod - Main02.ba2`; the numbers start at `01`): the game doesn't load it by itself, though an INI archive list such as `sResourceIndexFileList` in the `[Archive]` section can load it on purpose. |
| `file-name` | never (WARNING) | 0.14.0: a file in the zip, or a name inside one of its archives, with a character outside plain ASCII (accented letters, other scripts, symbols), which archive tools and the game may not find; or a loose file whose path inside Data is over 200 characters, which passes Windows' 260-character limit under Steam's usual Data folder (a longer install path lowers the limit). Two kinds of files the game never loads are skipped: the zip's `fomod` folder, and the files at the top of Data other than plugins, archives and INI files (a README or an image there). Files in any other folder are checked. Up to 5 files are named per zip, then one line counts the rest; `--verbose` lists them all. |
| `script-missing`, `model-missing` | basic, house (models: WARNING) | A script a plugin attaches, or a model it names, is in none of the zip's files and archives, the `--masters-dir` mods' archives or the game's archives. |
| `material-missing`, `texture-missing` | basic, house (WARNING) | 0.14.0: a material or texture the plugin's records, or its own `.nif` and `.mat` files, name is in none of those places (a material may also be one of the game's own, in its material database). A `.nif` or `.mat` that can't be read is a NOTE. Left out with `--dmmdeps`, which reports these files itself. |
| `mesh-missing` | basic, house (WARNING) | 0.15.0: a geometry file (`geometries\...\<name>.mesh`) that one of the plugin's own `.nif` files names is in none of those places, so that part of the model doesn't show. Runs with `--dmmdeps` too, since DMMDeps doesn't follow the geometry a `.nif` names. |
| `sound-missing` | basic, house (WARNING) | 0.14.0: a sound that one of the mod's own sound banks (`sound\soundbanks\*.bnk`, loose in the zip or in its archives) streams, `sound\soundbanks\<id>.wem`, is in none of those places, so that sound is silent. A bank that can't be read, or of a Wwise version the tool doesn't know (it knows version 140, the game's own), is a NOTE. Runs with `--dmmdeps` too, since DMMDeps doesn't read sound banks. |
| `face-missing` | basic, house (NOTE) | 0.14.0: an NPC a plugin adds with its own face (head parts, tints, face morphs or morph blends), or an override that changes another plugin's NPC face, has none of its five baked face files: `meshes\actors\character\facegendata\facegeom\<plugin>\<id>.nif` and the `_ao`, `_color`, `_normal` and `_rough` `.dds` under `textures\actors\character\facecustomization\<plugin>\`. `<plugin>` is the file name of the plugin that defines the NPC and `<id>` its local ID in 8 hex digits. An NPC whose template gives its Traits isn't looked at. It is a NOTE, not a WARNING, because some of Bethesda's own NPCs have no baked face data either, and others only part of it, so one file is enough to count. Runs with `--dmmdeps` too, since DMMDeps can't see face files. Since 0.15.0, an override that changes an NPC's face, where every face file found is outside the zips (the defining plugin's own), gets a NOTE that the baked face is stale: the game shows the master's old face. |
| `dmmdeps` | basic, house | With `--dmmdeps`: a file the plugin needs isn't in the zip (PROBLEM), a file is referenced but found nowhere (PROBLEM), a file in the zip is not used (REVIEW), or also in the game's archives (REVIEW). |
| `plugin-check` | basic, house | An ERROR from the same checks as `check`, run on every plugin in the zips, or a plugin that can't be checked. |
| `mutagen-gap` | basic, house | Data the Mutagen library 0.54.4 loses when it writes a plugin, and a missing ONAM list (the game ignores edits to the master's placed references). |
| `master`, `one-plugin`, `script-source`, `root-md`, `private-file` | house only | A `BlueprintShips` master; more than one plugin in a zip; `.psc` sources in the zip; a root `.md` such as `README.md` with no mod name in it (it overwrites another mod's copy in Data; use `README-<ModName>.md`); a file such as `PERMISSION.md` that should never ship. |
| `not-a-zip` | never (NOTE) | 0.15.0: an input that is a folder or the run's loose files, not a zip, so the zip-only rules `one-plugin` and `root-md` were skipped. Run `release` on the zip you upload to check them. |
| `tested` | basic, house | 0.15.0, with `--tested`: a compiled script that can't be read for the comparison (PROBLEM), or a `--tested` zip or `.ba2` that can't be read (one PROBLEM, and nothing is compared). A script that differs from the tested build, is missing now or is new since the test, a plugin that differs, and a plugin with no namesake in the tested zip are REVIEW lines; a script the same as the tested build and a byte-identical plugin are NOTE lines. |
| `stage` | basic, house | 0.15.0, with `--stage`: a staged file in none of the release's archives and not loose in the zip (PROBLEM; the game won't see it). A file in an archive that isn't staged is a REVIEW line; a stage that matches is one NOTE. |
| `accept` | never (WARNING) | 0.15.2: an `--accept` or `--accept-file` entry that matched no finding. Check the rule, the plugin's file name and the detail against the finding, or remove the entry. |
| `papyrus-lint` | never | Lint findings (WARNING only). A NOTE when a zip or `--psc` folder holds no sources, and when `--psc` or `--psc-import` is given without `--papyrus` and `--ck-sources`. |
| `creations-ini`, `creations-sfse` | creations | An INI file, or a DLL, EXE, ASI or file in an `SFSE` folder: Creations take none of them on any platform. A mod that needs SFSE stays a PC (Nexus) mod. |
| `creations-framework` | creations | A compiled script that names a script from an SFSE plugin (ConsoleAPI, PapyrusUtil and others in `data\creations-masters.txt`). Consoles can't run SFSE, so those calls fail. A WARNING instead when the same script calls `Game.IsPluginInstalled`, so it may use the plugin only when it's there; the WARNING names the plugins the script tests, and a guard in another script isn't seen. Since 0.10.1 it stays a PROBLEM when every name the script tests is an official plugin's (Starfield, the DLC, `BlueprintShips-Starfield.esm`, `SFBGS###` and `sftaNN` plugins and their `BlueprintShips-` files: the `[official]` list in `data\creations-masters.txt`), since such a check doesn't guard the framework; a name held in a variable still counts as a guard. |
| `creations-master` | creations | A master that isn't Bethesda's (Starfield, the DLC, `BlueprintShips-Starfield.esm`, `SFBGS###` and `sftaNN` plugins and their `BlueprintShips-` files, by exact name, so `SFBGS003Fixes.esm` isn't one) and isn't known to be on Creations. Make it optional in code (`Game.IsPluginInstalled`), or allow it with `--creations-master` once you have seen it there. |
| `creations-xbox` | creations | A PC archive with textures and no `<name>_xbox.ba2` twin (the Creation Kit's Xbox pack refuses PC textures). Any other archive with no twin is a WARNING (Xbox gets no archive). |
| `creations-size` | creations | A zip whose plugins and archives come to over 2 GB, the most a Creation may hold. Each zip (or folder) is checked on its own, since each one becomes its own Creation; with `--size-total` (0.15.0) all the inputs of the run are added up as one Creation. |
| `creations-audio` | never | `.wem` or `.bnk` sound in an archive or loose (WARNING): PC-format audio is silent on Xbox and PlayStation until the Creation Kit makes their versions from WAV sources. |
| `creations-debug` | never | `Debug.Notification` or `Debug.MessageBox` that the player would see (WARNING), read from the compiled scripts. The Debug script does nothing on Xbox and PlayStation; show a Message record with `Show()` instead. A call counts as debug-only, and isn't reported, when its function's name mentions debug, trace or verbose, or when it sits inside an `If` that tests a debug switch (something so named) without negating it. |

## Check a plugin for Mutagen losses: `sf-mod-check gap`

Run it on a plugin you wrote with a tool built on the Mutagen library (0.54.4), before you test or ship it. It
compares every record the plugin overrides with its master's copy, field by field, and says what Mutagen is known to
lose when it writes a plugin. It is the same check as the `release` command's `mutagen-gap` rule, on one plugin
without a zip (0.15.0).

    sf-mod-check gap out\MyMod.esm --masters-dir out

    sf-mod-check gap <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--quiet] [--verbose] [--log <file>]

- `<plugin>`: one `.esm`, `.esp` or `.esl` file.
- `--data`: the game's Data folder (default: Steam's usual one). `--masters-dir <folder>`: another folder to look for
  masters in; can be given more than once. Masters are looked for in the plugin's own folder, then each
  `--masters-dir` in order, then the Data folder.
- `--quiet`: no byline on stderr, and no WARNING lines when there are errors. `--verbose`, `--log <file>`: as for `check`.

Output is a summary line (own records, overrides, how many were compared), then `ERROR` lines for what Mutagen
0.54.4 is known to lose (a hazard's OPDS, material swap entries, a dialogue response's TROI, traversal flags, SFTR
fields the master leaves out, and overridden placed references of full masters missing from the ONAM list, whose
edits the game ignores) and `WARNING` lines for any other field the master's copy has and the plugin's lacks (it may
be on purpose; at most 200 are shown). A count line follows, then `RESULT: OK (0 errors, n warnings)`,
`RESULT: FAIL (n errors, n warnings)` or `RESULT: CANNOT CHECK (...)`. Exit codes: 0 no errors (warnings allowed),
1 errors, 2 can't check (a missing plugin, a master in none of the folders searched, a file that isn't a plugin, or
a bad argument). It only reads the plugin and its masters.

## Look up a record: `sf-mod-check find`

Use it when you need one record's FormKey, its type, or who wins it: for example, the location your quest should
point at, or the resource a recipe needs (0.16.0). It reads your load order and changes nothing.

    sf-mod-check find CityNewAtlantisLocation
    sf-mod-check find 01295A:Starfield.esm --type LCTN
    sf-mod-check find 0001295A --plugins "%LOCALAPPDATA%\Starfield\Plugins.txt" --json
    sf-mod-check find --plugin "C:\Downloads\SomePatch\SomePatch.esm" --masters-dir "C:\Downloads\SomeMod"
    sf-mod-check find CityNewAtlantisLocation --full --versions --refs

    sf-mod-check find <EditorID | FormKey | FormID> [--type <SIG>] [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]
                      [--data <Data folder>] [--extra-data <folder>] [--full] [--versions] [--refs [--max <n>]]
                      [--json] [--quiet] [--verbose] [--log <file>]
    sf-mod-check find [<record>] --plugin <plugin file> [--masters-dir <folder>]... [--max <n>] [--type <SIG>]
                      [--data <Data folder>] [--full] [--versions] [--refs] [--json] [--quiet] [--verbose] [--log <file>]

- The record: an exact **EditorID** (letter case doesn't matter; there is no partial or fuzzy search), a **FormKey**
  (`01295A:Starfield.esm`, the form `conflict` and `patch` print), or a **FormID** of 8 hex digits as the console
  shows it (`0001295A`, with or without `0x`). A console FormID depends on the load order: its first digits pick the
  plugin by its place in the load order (full plugins `00` to `FC`, medium plugins `FD` and one more byte, light
  plugins `FE` and three more hex digits), so the same record can have another FormID in another load order.
- `--type <SIG>`: keep only records of one four-letter type, such as `LCTN` (location), `MISC` (misc item), `NPC_`
  or `QUST`. An unknown type stops with exit 2.
- `--plugins`, `--mo2`, `--profile`, `--data`, `--extra-data`: the load order, as for `conflict` (default: the game's
  own Plugins.txt and Steam's usual Data folder). A plugin in the `--extra-data` folder wins over Data's copy. With
  Mod Organizer 2, give `--mo2 <MO2 folder>` so the mods in MO2's own folders are searched too.
- `--plugin <plugin file>` (0.16.1): read only this plugin and its masters, from any folder, with no Plugins.txt.
  Use it for a downloaded mod or a patch that isn't installed. The load order is the plugin's masters (each master's
  own masters first) and then the plugin. Each master is looked for in the plugin's own folder, then in each
  `--masters-dir` in the order given, then in `--data` (default: Steam's usual Data folder). A master in none of them
  stops with exit 2 and says where to put it. Light and medium masters are read from their own headers, so a link to
  a light master's record shows that master's FormKey and EditorID. With `--plugin`, give the record as an EditorID
  or a FormKey: a console FormID stops with exit 2, because its first digits need the game's whole load order. If
  the plugin itself can't be opened, `find` stops with exit 2 and prints the load-order problem, rather than answer
  from a master's copy.
- **With no record given, `find --plugin` lists every record the plugin holds** (new records and overrides, group by group), each with the same fields as a
  lookup. It prints at most `--max <n>` records (default 100; `--max 0` lists them all), then a line that counts the
  rest, and always a summary of all of them: `<plugin> holds n records: n new, n overrides; by type: QUST 2, ...`.
  `--type` narrows the list. With a record given, it is looked up in that small load order. `--plugin` doesn't go
  with `--plugins`, `--mo2` or `--extra-data` (exit 2: use `--masters-dir`), and `--masters-dir` and `--max` need
  `--plugin`.
- `--full` (0.20.0): print every nested value of the winning copy as an indented `tree:` in place of `fields:`.
  Zeros, `False` and an enum's first value print (they are real values); empty text, empty lists, null links and
  padding the game doesn't read are left out. A list entry that is empty prints `(empty)` so the numbers stay true.
  The tree stops at 12 levels, 20,000 values and 2,000 lines; a last line counts what was cut. It works in a load
  order and with `--plugin`, also in the `--plugin` listing.
- `--versions` (0.20.0): after the fields, list every copy of the record in load order and what each copy changes,
  field by field, against the copy it was built on (the latest earlier copy among the plugin's masters that has the
  field; a change against another plugin says `(against <plugin>)`). A copy that doesn't have an earlier copy's
  plugin as a master gets a note that it may undo that plugin's changes. The RESULT line adds `, n copies`. It needs
  a record: with the `--plugin` listing it stops with exit 2.
- `--refs` (0.20.0): list the records whose **winning copy** points at this record, each with its type and the
  plugin of its winning copy (a reference placed in a cell shows `in <the cell>`; the cell itself isn't listed for it). **Only winning
  copies count**, because that is what the game uses: a copy that a later plugin overrides without the link isn't
  listed, so SFSearch's `refs2`, which also lists those copies, can show a few more. Links held in a script's struct
  members count. At most `--max <n>` records are listed (default 100; `--max 0` lists them all) and the RESULT line
  says how many there are. Records whose links can't be read are counted on one note line. In a whole load order
  this reads every plugin and takes about two minutes for a popular location; `--plugin` reads only the plugin and
  its masters. It stops with exit 2 if memory passes 16 GB. It needs a record, as `--versions` does. `--max` works
  only with the `--plugin` listing or with `--refs`.
- `--json`: print one JSON object on stdout instead of the text: `tool`, `version`, `query`, `type`, `loadOrder`
  (`source`, `plugins`), `matches` (each with `formKey`, `editorId`, `type`, `name`, `definedIn`, `winner`,
  `plugins` in load order, `fields` and, 0.16.1, `entries`: each list field's entries as text), `result` (`found`,
  `not found` or `cannot run`), and `hint` or `error` when there is one. A whole-plugin listing adds `plugin`,
  `total` (the records that match `--type`), `listed` and `byType` (every record in the plugin, counted by type), and
  its `query` is null. Since 0.20.0 each match also has `tree` (with `--full`; `treeNotShown` when a cap cut it),
  `versions` (with `--versions`) and `refs` (with `--refs`), and only with those flags.
- `--quiet`: no byline or progress on stderr. `--verbose`, `--log <file>`: as for `check`.

For each record it prints the heading `EditorID [FormKey] TYPE "Name"`, then `defined in:` (the plugin the record
comes from), `winner:` (the last plugin in the load order with a copy: the one the game uses), `also in:` (the other
plugins with a copy, when there are any) and `fields:`, the winning copy's main values. Links show as
`EditorID [FormKey]`, and empty values are left out; at most 40 field lines are shown. A list shows its count
(`(3 entries)`) and then, since 0.16.1, each entry on its own line, at most 50 per list. A condition shows as
`[OR ]Function(parameters) operator value (run on ...)`, for example:

    Conditions: (2 entries)
      - OR GetValue(LM_Value [000800:LightM.esm]) == 0 (run on Subject)
      - GetGlobalValue(BQ_Global [000801:BaseQ.esm]) == 1 (run on Subject)

A number parameter of 0 is left out, since the plugin stores an unused parameter as 0, except where 0 is a real
value: an alias function's alias number (`GetIsAliasRef(0)`) and GetStageDone's stage. A named choice such as
`GetIsSex(Male)` or `GetPos(X)` always prints.
A link to a record type that isn't fixed (a condition's "run on" reference, for example) shows its FormKey only.
Fields nested deeper than a list's entries show one level down, cut at 200 characters. A cell's references and a
quest's dialogue are records of their own and aren't listed under their parent (a whole-plugin listing lists them as
records). The last line is `RESULT: FOUND n record(s)` or, after a `HINT:` line that says what was searched,
`RESULT: NOT FOUND (...)`. Two records can share an EditorID; then both are printed.

Exit codes: 0 found, 1 not found (with `--plugin` and no record: the plugin holds no records, or none of that
`--type`), 2 couldn't run (a bad option, no load order, a missing folder or master). An EditorID search reads every
plugin in the load order: about 20 seconds for the game's own 18 plugins. A `--plugin` run reads only the plugin and
its masters, but a master as big as Starfield.esm still takes about 10 seconds.

## Dialogue lines of one NPC: `sf-mod-check lines`

Use it to see which kinds of dialogue line an NPC speaks, and which kinds it still lacks next to a vanilla companion
(0.21.0). It reads your load order and changes nothing.

    sf-mod-check lines Companion_SarahMorgan
    sf-mod-check lines MyNpc --compare Companion_SarahMorgan
    sf-mod-check lines 000801:MyMod.esm --plugin "C:\Downloads\MyMod\MyMod.esm" --type "Combat / Hit"

    sf-mod-check lines <EditorID | FormKey | FormID> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]
                        [--data <Data folder>] [--extra-data <folder>] [--compare <NPC>] [--type <name>] [--max <n>]
                        [--no-files] [--json] [--quiet] [--verbose] [--log <file>]
    sf-mod-check lines <EditorID | FormKey> --plugin <plugin file> [--masters-dir <folder>]... [...]

- The NPC is given as for `find`: an EditorID, a FormKey or a console FormID (with `--plugin`, an EditorID or a
  FormKey). `--plugins`, `--mo2`, `--profile`, `--data`, `--extra-data`, `--plugin` and `--masters-dir` work as for `find`.
- **Which lines count.** A line is the NPC's when (in this order of "found by"): its **speaker** is the NPC; a
  **condition** names the NPC (`GetIsID`; a condition of the line's quest or dialogue group counts too); a **quest
  alias** the NPC fills is named by a condition (`GetIsAliasRef`); or a **scene alias** the NPC fills is given the
  line. Lines for the NPC's **voice type** (`GetIsVoiceType`) are listed apart as **shared lines**, because any NPC
  with that voice type may say them. A negated condition doesn't make a line the NPC's. A line with no speaker, no
  speaker condition and no scene alias is **generic**: any NPC may say it, so it is counted and not listed. The
  player's own choices in the NPC's scenes are counted apart and not listed.
- **Types.** Lines are grouped by their topic's Category and Subtype, such as `Combat / Hit` or `Misc / Idle`, with a
  count per type. `--type Combat` keeps one category; `--type "Combat / Hit"` keeps one type (letter case ignored).
  It filters the table and the RESULT line too.
- **Each line** shows its FormKey, topic EditorID, response number, text (cut at 200 characters), whether its voice
  file (`wem`) and lip file (`lip`, an `.ffxanim`) exist, its face (emotion) and how it was found. The files are looked
  for under `sound\voice\<plugin>\<voice type>\` in the plugin's own archives (`<plugin> - *.ba2`) and as loose files;
  `?` means unknown (the NPC has no voice type of its own). `--no-files` skips the look-up and is faster.
- `--compare <NPC>`: count another NPC's own lines per type too, for example a vanilla companion, in a second column.
  A type the other NPC has and this NPC has no line of at all (own or shared) is marked `missing`. This is the
  coverage report: the `missing` types are the kinds of line still to write. A `--compare` NPC that can't be found is
  exit 2.
- `--max <n>`: list at most n lines (default 200; `--max 0` lists all). The counts are always complete.
- `--json`: one JSON object on stdout: `tool`, `version`, `query`, `npc`, `source`, `lines` (each with `info`,
  `topic`, `topicEditorId`, `category`, `subtype`, `response`, `text`, `emotion`, `foundBy`, `shared`, `wem`, `lip`,
  `quest` and `scene`), `counts` per type (`category`, `subtype`, `count`, `shared`, and with `--compare`
  `reference` and `missing`), `reference` (with `--compare`), `playerLines`, `genericLines` and `result`.
- `--quiet`, `--verbose`, `--log <file>`: as for `check`. Memory is limited to 16 GB (exit 2 past it).

A real run on made-up plugins:

    > sf-mod-check lines LnNpc
    sf-mod-check 0.21.0 lines: LnNpc [000802:LnBase.esm] in 2 plugins
      voice type: LnVoice [000800:LnBase.esm] (female)

    its lines:
     Combat / Hit (2 lines)
      000800:LnAddon.esm LnHit r1 "Made-up addon ouch." wem yes, lip yes, (none), condition
      00080C:LnBase.esm LnHit r1 "Made-up ouch, overridden." wem yes, lip no, (none), condition
     Misc / Hello (1 line)
      00080A:LnBase.esm LnHello r1 "Made-up hello." wem yes, lip yes, Happy, speaker

    shared lines (voice type LnVoice: any NPC with this voice type may say them):
     Misc / Idle (1 line)
      000811:LnBase.esm LnIdle r1 "Made-up shared line." wem no, lip no, (none), voice type

    types (this NPC | shared):
      Combat / Hit        2      0
      Misc / Hello        1      0
      Misc / Idle         0      1
    RESULT: FOUND 4 lines of LnNpc (1 shared, 3 types) (1 s)

Exit codes: 0 the NPC was found (even with no lines), 1 not found, 2 couldn't run (a bad option, no load order, a
missing master or `--compare` NPC, a name that is another kind of record, or one EditorID shared by several NPCs). A run on a vanilla companion in a whole load order of 158 plugins takes about
45 seconds and under 1 GB of memory; one plugin with its masters takes a few seconds.

## A plugin's voiced lines as a list: `sf-mod-check voice-lines`

Use it to get every voiced line a plugin adds as a tab-separated file in the Creation Kit's dialogue-export columns,
the list that voice tools such as Starfield Voice Cloner read (0.21.0). It reads the plugin and its masters and
changes nothing.

    sf-mod-check voice-lines "C:\Downloads\MyMod\MyMod.esm" --out MyMod-lines.tsv

    sf-mod-check voice-lines <plugin> [--out <file>] [--voice <voice type EditorID>]... [--utf8]
                                      [--data <Data folder>] [--masters-dir <folder>]... [--quiet] [--verbose] [--log <file>]

- Masters are found in the plugin's own folder, then each `--masters-dir`, then `--data` (Steam's Data by default); a
  missing master is exit 2.
- **The columns** (the header line exactly as below, tab-separated, lines end with CRLF, no byte order mark):
  `VOICE TYPE`, `FILENAME`, `RESPONSE TEXT`, `FACE`, `SCRIPT NOTES`, `SEX`, `FULL PATH`, `INFO`, `RESPONSE`.
  - `VOICE TYPE`: the voice type's EditorID, in lower case (`npcfmyvoice`).
  - `FILENAME`: the response's voice file number as **8 upper-case hex digits** with the top byte `00`, no extension
    (`0001A2B3` for `0001a2b3.wem`). It is the low 24 bits of the response's file number.
  - `RESPONSE TEXT`: the line; a field with a quote is quoted CSV-style, and tabs and other control characters
    become spaces.
  - `FACE`: the emotion keyword's EditorID without `AnimFaceArchetype` (for example `Happy`); a keyword with
    another name gives its whole EditorID; empty when the line has no emotion. `SCRIPT NOTES`: the response's script
    notes, empty when there are none.
  - `SEX`: `female` or `male` from the voice type's flag; empty when the voice type isn't known.
  - `FULL PATH`: `Data\Sound\Voice\<plugin file name>\<VOICE TYPE>\<FILENAME>.wem`
    (`Data\Sound\Voice\MyMod.esm\npcfmyvoice\0001A2B3.wem`); Voice Cloner takes the plugin from it.
    `INFO`: the line's FormKey. `RESPONSE`: the response number inside the line, from 1.
- **Encoding:** Windows-1252 by default (what the Creation Kit writes and Voice Cloner reads); a character it can't
  hold becomes `?` and a NOTE counts them. `--utf8` writes UTF-8 instead.
- **Rows:** one row per voice type and file number, ordered by voice type and then file number. A line that several
  voice types may speak gets one row per voice type. Two lines that share one file number with different texts give
  one row (the lower INFO) and a WARNING.
- **Which voice types:** from the line's conditions (`GetIsID` and `GetIsVoiceType`), else those of its dialogue
  group, else those of its quest (the first of these that names one is used), else from its speaker (the line's
  speaker, or the NPC that fills its quest alias or scene alias; scenes in the masters count for a master's topic the
  plugin adds lines to). A line that other lines share (they link it as their SharedDialog) and that names no voice
  type itself gets the voice types of the lines that share it. A line with none gets no row and a NOTE; `--voice <EditorID>` (may be repeated) gives those lines that voice type.
  An unknown `--voice` name still names the folder, with `SEX` empty and a NOTE.
- **Left out:** player lines (scene choices), lines that link a SharedDialog line (the shared line has the row), and
  responses with no voice file number.
- `--out <file>`: write the list there (default: the screen, stdout). Messages and the RESULT line go to stderr.

Exit codes: 0 written (even with no rows), 2 couldn't run (this includes a plugin whose dialogue can't be read in
full, so the list never misses lines without saying so). `--plugin`, `--plugins`, `--mo2`, `--profile` and
`--extra-data` aren't options of this command (the plugin is the argument) and stop with exit 2.

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
prints the LOOK lines and `RESULT: REPORT WRITTEN (<n> to look at, <n> notes) <path>`, or `RESULT: NO REPORT (...)`
on bad arguments, bad setup or an error that stops the run (exit 2). Exit codes: 0 report written, 2 bad input,
setup or an error that stops the run. There is no "fail" exit.

| Section | What it lists | LOOK when |
|---|---|---|
| Files | every file with its size and SHA-256 (a skipped file is listed with "link" or "same name" in place of its size, and a file whose hash couldn't be read with "?"); each `.ba2`'s contents; version numbers in names; which archives load with which plugin; archives inside the mod (not opened); FOMOD installers; files outside the usual Data layout | an archive no plugin loads; an archive that can't be read; a file name (loose, or inside one of the mod's archives) with a character outside plain ASCII, or a loose path inside Data over 200 characters (the `release` rule `file-name`, since 0.15.0; up to 5 named, then one LOOK counts the rest) |
| Replaces vanilla | loose files and archive entries whose path is also in the game's own archives | the mod replaces a game script (every quest and mod that uses it gets the mod's copy); a plugin or archive with the game's own name |
| Plugins | masters (missing, unused), light/medium/full flag, FormID range, text that isn't valid UTF-8; records added, overridden and deleted by type and origin; changed fields in official records; ONAM; attached scripts; `check`'s own findings | a missing master; ITMs; deleted official records; official changes the mod undoes (it overrides a record a later official master changed, without that master); temporary references missing from ONAM; scripts that aren't there; `check` ERRORs |
| Scripts | each `.pex` (loose or in an archive) read from its bytecode: name, parent, every function it calls, whether it logs with `Debug.Trace` | console commands; INI changes; calls to scripts in neither the mod nor the game; timers that restart themselves or start from frequent events; `GetFormFromFile` or text naming another mod's plugin; file paths that run or point outside Data |
| Binaries | each `.dll`, `.exe` and `.asi` read from its bytes: machine, build time, PDB path, ASLR/DEP/CFG, imports grouped by what they allow, exports, SFSE plugin data (game versions, Address Library or signature scanning), signature | network, process-creation and memory-injection imports; URLs and IP addresses in its strings; an SFSE DLL without `SFSEPlugin_Version`; a DLL named like a Windows DLL the game would load instead |

**Changed fields in official records (since 0.17.0).** The table "Changes to official records" lists each field the
mod changed, with its value before and after cut at 160 characters. The table below it, "Changes to official records,
field by field", gives the full difference: one row per changed value or list entry inside the field, with its path
(for example `Stages[Index 100].LogEntries[0].Entry`), its kind, and the value before and after. List entries are
matched by a member each entry has (an ID or an index); a list with no such member is matched by position when its
length is unchanged, and otherwise by equal entries, so an added or removed row's number is its place in its own list.
The kinds are:

- `changed`, `added`, `removed`: a value changed, or a list entry was added or removed.
- `same entries, other order`: a list holds the same entries in another order (one row for the whole list). The
  game usually doesn't care about the order of such lists (an alias's factions, for example).
- `same English text, translations dropped`: the text is the same in English, but the official plugin keeps it in
  every language and the mod stores it once, so the game shows the English text in every language. The reverse is
  `same English text, translations added`. Triage tells them apart by the plugins' Localized header flag. It compares
  the English text only (an official plugin's comes from its English strings files) and doesn't read the other
  languages' files.

Padding (the bytes Mutagen names `Unused`, which the game doesn't read) gets no rows of its own. A field whose only
difference is padding, such as a leveled list resaved by another tool, gets one `changed` row whose After ends with
"(only padding differs ...)".

The plugin's NOTE line counts these last two kinds, since they are often harmless, and says how many of its changed
fields differ only in their translations. Limits: at most 200 rows per record (and 200 more for the record's rows
of the two translation kinds, so those rows never crowd out a real change, even inside the same field) and 10,000 in the report (real changes first), and each value up to 2,000 characters; each cut says how much it left out. A list of more than 2,000 entries with no key to match them by, whose length changed, gets one `changed` row that says so. Records
in cells and worldspaces (placed objects, navmeshes) are compared raw, so they are named only by field type, in the
first table.

What it can't see: names a DLL looks up while it runs, what a script does with values it computes, records nothing
uses, and files behind a link (symlink or junction) inside a mod folder or beside a plugin: triage never follows one,
and the report lists each as skipped, with one note in the Files section that anything inside wasn't checked.

A missing-script LOOK in the Plugins section says how many linked items were skipped, because a skipped link may
hold the script, and so does the Scripts section's note when it finds no compiled scripts at all. On Windows, a
plugin you name yourself is read even when it is a link, because you chose it; on Linux and macOS it isn't read.

Only real links count: a OneDrive placeholder or another kind of reparse point is read as a normal file or folder.
An archive with more than 200,000 entries is refused. It reads compiled scripts as bytecode only:
nothing is decompiled. On a large mod it uses memory like `check` does (city-sized mods: about 5 GB).

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
- `--json`: the report as JSON instead of text. The JSON always lists everything. On stdout the RESULT line still
  comes last, after the JSON, as for every command. For a file that holds only the JSON (to load in a script), add
  `--out diff.json`: the file gets the JSON and stdout gets only the RESULT line.
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
A record whose bytes differ but whose fields are all equal (padding bytes, a condition's among them, field order, -0
and +0, as a resave in the Creation Kit or xEdit leaves them) is counted as noise. Noise is no difference: it is shown, but it never makes the
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
- **quest stages** (0.19.0, rules `quest-stage-unset` and `quest-stage-missing`, WARNINGs): for each quest the plugin
  adds (not an override), every stage must be set by something in the plugin: the quest's start stage (Run On Start),
  a dialogue response's, scene's or scene phase's "set parent quest stage" value, or a `SetStage` or `SetStageNoWait`
  call in a script the plugin attaches. A stage nothing sets gets one WARNING per quest that lists those stages, since
  what they do never happens. A script call that sets a stage the quest doesn't have is a WARNING too (the call does
  nothing).
  - Scripts are read from the same places as the assets check: loose files, then the plugin's own archives, then its
    masters' archives (where the game's own scripts are). A call counts when its quest is the script's own quest
    (`self` in a quest script, or `GetOwningQuest()` in a dialogue, scene, package or alias script) or a quest
    property the record sets, and its stage number is a constant, an int property the record sets, the property's
    default, or a helper function's parameter that every call in the script passes as a constant.
  - To keep false alarms down: a quest with a call whose stage number is worked out while the game runs gets one NOTE
    instead; a stage whose number a call with an unknown quest uses counts as set; and when an attached script can't
    be found or read, the unset rule becomes one NOTE for the plugin. A property's default value never raises
    `quest-stage-missing`, since defaults are often placeholders such as 0, and a negative stage number (a "none"
    placeholder such as -1) is ignored. In a quest's stage fragment script, only the fragments of stages the quest
    has are read: a fragment left over from a deleted stage never runs.
  - Stages that another mod's scripts or the game's engine set can't be seen, so read each WARNING as "check this".
    Bethesda's own plugins aren't checked, and `--no-assets` skips this check too. `--verbose` lists each quest, each
    setter found and each call left unresolved.
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
  - **Materials and textures** (0.14.0, WARNING): the materials and textures a record names (a texture set's
    material, a perk icon file, a lens flare sprite and a few others), the materials each of the plugin's own `.nif`
    files names, and the textures and parent materials each of its own `.mat` files names. "Its own" means a loose
    file a record, `.nif` or `.mat` of the plugin names, or any `.nif` or `.mat` inside an archive named after the
    plugin. Other mods' and the game's files are looked up by name only, never opened. A material also counts when
    a material database holds it: any `.cdb` file in a loaded archive or loose under `materials\`. The game's
    (`materials\materialsbeta.cdb` in `Starfield - Materials.ba2`) holds all of the game's own materials, and DLC
    and mods can ship their own (`materials\creations\<plugin>\materialsbeta.cdb`). If no database can be read, one
    WARNING says how many materials couldn't be checked, and none of them is called missing. A `.nif` or `.mat` that can't be read is one WARNING
    naming the file. Textures are found in texture archives (`<plugin> - Textures.ba2`) as well.
  - **Sounds** (0.14.0, WARNING): each Wwise sound bank (`sound\soundbanks\*.bnk`) inside an archive named after the
    plugin is read, and every sound it streams rather than holds itself must be a `sound\soundbanks\<id>.wem` file
    in the places below. Sounds held inside the bank need no file. Loose banks in the Data folder aren't read by
    `check` or `conflict`, since every mod shares that folder (`release` reads the zip's loose banks). A bank that
    can't be read, or whose Wwise version isn't 140 (the version of every bank the game ships), is one WARNING
    naming the file. Voice files and music aren't checked.
  - **Geometry** (0.15.0, WARNING): each of the plugin's own `.nif` files (as above) names the `.mesh` files that hold
    its shapes' geometry, and each must be a `geometries\...\<name>.mesh` file in the places below, else that part of
    the model doesn't show in the game. The game's own models give the name without the folder and extension
    (`<folder>\<file>`), and some mods give the whole path; both mean the same file. A shape whose geometry is stored
    inside the `.nif` names no file. A `.nif` whose shapes can't be read is one WARNING naming the file (its materials
    are still checked). Geometry that only other mods' or the game's `.nif` files name isn't checked.
  - **Face data** isn't checked here: `release` looks for it (rule `face-missing`, a NOTE), since some of the game's
    own NPCs have no baked face files either.
  - **Where a file can be:** a file counts when it is loose in the plugin's own folder, a `--masters-dir` folder or
    the Data folder, or inside an archive the game loads: `<plugin> - *.ba2` for the plugin and each of its masters (other languages' voice
    archives left out), plus the archives `Starfield.ini` and `StarfieldCustom.ini` list. The plugin's own archives
    are looked for in its own folder first; a master's archives and the INIs' archives where the masters are
    (`--masters-dir`, then Data; with no `--masters-dir`, the plugin's own folder, then Data).
  - **How it reports:** each missing file is listed once, with up to 3 records that use it. An override isn't blamed
    for a missing file the master's version of the record already names (some vanilla records attach scripts the
    game doesn't ship; `--verbose` lists each one left out).
  - **A part ending in a dot or a space** (0.10.0, such as `meshes\armor.\helm.nif`): Windows trims such a part when
    it opens a loose file. The last part loses all its trailing dots and spaces; any other part loses one trailing
    dot, unless a dot comes before it. An archive looks the name up exactly. So when the trimmed path is a loose
    file, the finding is a WARNING that says the game may load it on Windows but not once the file is packed into a
    `.ba2` (a script too, not an ERROR). Otherwise the path is missing as usual, and the finding names the trimmed
    path.
  - **Not checked yet:** `.mesh` geometry that only another mod's or the game's `.nif` names, files the game finds by name (voice
  and lip files, which only `release` checks, rule `voice-missing`; face animation, `.af`/`.hkx` animations, LOD, Wwise sounds), a script's parent and imports, whether an SFSE
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
- **quest-stage-unset**: A stage of a quest the plugin adds is never set: no start stage, dialogue response, scene or script SetStage call sets it, so its log entry and fragment never run. CK: open the quest's Quest Stages tab, then set the stage from a topic info's or scene's 'Set Parent Quest Stage' field or a script (SetStage), or mark it Run On Start. If another mod's script or the game's engine sets it, ignore this. A NOTE means a script's stage number or the script itself couldn't be read, so the stages weren't checked. A WARNING that says the check stopped is not about a stage: any error inside the check can cause it (for example a script that couldn't be read); run again with --verbose and report it.
- **quest-stage-missing**: A script sets a quest stage the quest doesn't have, so the call does nothing. CK: add the stage on the quest's Quest Stages tab, or fix the number in the script (or in the script property the record sets) and compile it again.
- **editorids**: Give each new record its own Editor ID that no master uses either. CK: open the form and change its ID. xEdit: edit the EDID field. This usually comes from duplicating a record or Copy as New Record.
- **itm**: Identical to Master (ITM) records change nothing but can undo other mods' edits to the same record. xEdit: right-click the plugin > Remove "Identical to Master" records (or run QuickAutoClean). In the CK they come from opening a form and pressing OK without changes.
- **trees**: The plugin edits an official Story Manager or Planet Content Manager node. Every mod that edits the same node conflicts, and the last one loaded wins. CK: make your own node under the official branch (Story Manager: set its parent and previous sibling) and revert your edits to the official one. xEdit: remove the override (right-click > Remove) once your own node does the job. Keep the edit only if you mean to.
- **ships**: A ship the plugin adds has a cockpit but lacks a system every official player ship has (cockpit, reactor, grav drive, engine, fuel tank, landing gear, docker, landing bay). CK: open the ship in the Spaceship Editor, add a module for each missing system and run its flight check. xEdit: add the module to the ship's Blueprint component. A ship only NPCs fly (an enemy or a quest ship) can ignore this.
- **assets**: A missing script means the record's script never runs; a missing model shows as an empty or error mesh. Pack the file into the mod's own archive (<plugin name> - Main.ba2), install it as a loose file under Data, or fix the path in the record (xEdit: the record's VMAD scripts or Model field). If the file comes from another mod, make that mod a master or a requirement. A folder or file name in the path that ends in a dot or a space works only as a loose file on Windows: remove the dot or space in the record. A missing material or texture makes a surface look wrong in the game: pack the .mat or .dds file with the mod (textures go in <plugin name> - Textures.ba2) or fix the path in the .nif or .mat that names it. A missing mesh means that part of the model doesn't show: pack the .mesh file (under geometries) with the mod, or export the model again with its geometry. A missing sound is silent in the game: pack the .wem file the sound bank streams (sound\soundbanks\<id>.wem) with the mod, or generate the bank again in Wwise. Run with --verbose to see which archives were read.
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
- Face data is looked for only by `release` (rule `face-missing`, a NOTE), and only whether any of an NPC's five
  baked files is there, not whether it matches the face in the plugin. For an override that changes another plugin's
  NPC face, a NOTE says the baked face is stale when every face file found comes from outside the zips (the master's),
  but a face file the zip ships is taken as the new face without being compared. The file names follow
  the installed mods: a light plugin's files use the 12-bit local ID, and a medium plugin's textures the 16-bit one.
- `archive-name` runs only in `release`; `triage` has its own archive LOOKs (Files section). `file-name` runs in
  both (in `triage` as LOOKs, since 0.15.0). `archive-name` doesn't read INI settings
  outside the zip, so an archive that the user's own `StarfieldCustom.ini` lists still counts as not loading.
  The 200-character limit assumes Steam's usual install folder; a deeper one lowers it.
- Voice files matched to dialogue lines are checked only by `release` (since 0.18.0, rule `voice-missing`): not
  for lines another plugin adds or that an override changes, and a line whose speaker conditions name no voice type
  passes when any voice-type folder has its file. Player lines in other plugins' scenes aren't recognized as
  player lines. Not checked: music tracks and the Wwise events records name,
  and what is inside loose files or BA2 archives beyond the material, texture and `.mesh` names in a mod's own `.nif` and
  `.mat` files and the streamed sounds in its own sound banks (the `assets` check looks up names; it doesn't read
  geometry, texture or sound data). The `.mesh` names are read from the `.nif` layout of the game's current
  version (Bethesda stream 172 and up); a shape of another layout is reported as unreadable, not as missing.
- LOD isn't checked: a `.nif` under `meshes\lod\` in a plugin's own archive is read for its materials only when a
  record names it. Generated LOD meshes name composite materials that no file or database holds.
- Sound banks are read only for Wwise bank version 140, the version of every bank in the game's own archives and of
  the mod banks tried. The tool reads each Sound object's source type, stream type and source ID; on the game's own
  banks every one of the 110,276 streamed sources had its `.wem` file (2026-11-23). A bank of another version gets a
  note instead of a guess.
- Materials are matched to the material databases by the 12-byte resource ID the game uses (a CRC32 of the
  folder and of the file name). The database is scanned for those IDs rather than parsed. On the game's own file
  this matched 935 of the 936 material paths it names in plain text and none of 3 made-up paths (2026-11-23).
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
