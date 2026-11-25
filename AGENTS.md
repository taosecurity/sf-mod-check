# sf-mod-check: instructions for AI assistants

These steps are for an AI assistant (or anyone) helping a Starfield player or mod author use sf-mod-check. Every command
here was last run on sf-mod-check 0.16.1 (2026-11-24) before release. The [README](README.md) has the full description.
Until 0.7.0 the tool was called **sfmodcheck**: the command is now `sf-mod-check` (the exe zip also holds a
`sfmodcheck.cmd` that runs it under the old name, for one release). Old caches and `SFMODCHECK_*` variables still work.

## 1. What it is for

sf-mod-check reads one Starfield plugin (`.esm`) and reports problems that would break it in the game: damaged files,
bad headers, FormIDs outside the plugin's range, links to records that don't exist, broken conditions (an empty form
parameter, a function number neither Mutagen nor the official masters know, or an alias number the quest lacks; package
target-data conditions included) and scenes, repeated EditorIDs, deleted records, "identical to
master" (ITM) records, and edits to official Story Manager and Planet Content Manager nodes (check `trees`: a
WARNING, because every mod that edits the same official node conflicts; the fix is to add the mod's own node to the
tree instead). For a ship mod it also lists each ship's modules by system, class, weapon slots and missing
required systems (a listing in the log), and warns (check `ships`) when a ship the plugin adds has a cockpit but
lacks a required system (a ship only NPCs fly can ignore it). It is a command-line cousin of part of
xEdit's "Check for Errors".

Use it when someone has made or changed a mod and wants to know whether the plugin is sound before they play it or
release it, or when a mod misbehaves and you want to rule out a broken plugin.

Its second command, `sf-mod-check conflict`, reads a whole load order (a Plugins.txt) and reports the changes it throws
away: official DLC and update changes undone by mods, one mod's changes lost to another, ITMs, load-order problems and
overrides that change a record's type (the game then keeps only the record that loads first), each with a recommendation (a safe load-order move, an update to look for, or a small local patch, with a prompt for
building it). Use it when mods may be fighting over the same records, or after adding mods to a load order.

Its third command, `sf-mod-check release <zip>`, checks the release zip a mod author is about to upload (Nexus, Bethesda
Creations or a friend) and prints OK or FAIL: plugin names the game drops, scripts and files the mod needs that the zip
lacks, sound files packed so the game plays them silent, and the `check` checks on every plugin in the zip. Use it as
the last step before an upload.

Its fourth command, `sf-mod-check triage <mod>`, looks over someone else's mod (a downloaded .zip, .7z or .rar, a
folder, or one plugin) before it is installed or rebuilt, and writes one report (HTML, text and JSON): every file with
its SHA-256, what the mod replaces in the game's own files, its plugins (masters, records added and overridden, ITMs,
deleted records, official changes it undoes, changed fields, ONAM, missing scripts), its compiled scripts (what each
calls, whether it logs, calls worth a look) and its DLLs and EXEs (imports, SFSE version data, signature). Each
finding is a NOTE (information) or a LOOK (worth a person's look). It is triage, not a verdict: never tell the user a
mod is safe or unsafe from it. A LOOK is where a person starts reading.

Its fifth command, `sf-mod-check patch` (0.9.0), builds a local conflict patch from the same analysis as `conflict`:
one light plugin, loaded last, that copies each lost Forwardable change forward and keeps the winner's value for
Choice fields unless the user picks the other. Use it when `conflict` recommends a small local patch, for example
after adding two mods that touch the same records (`--only` limits it to those mods).

`sf-mod-check find <record>` (0.16.0) looks up one record in the load order by EditorID, FormKey or console FormID
and prints its type, name, the plugin that defines it, the plugin that wins it and its main fields. Use it whenever you
need a FormKey or want to know who wins a record, instead of writing a throwaway program to read the plugins. Since
0.16.1, `find --plugin <file>` reads one plugin and its masters from any folder (a download, a patch not yet
installed), and with no record given it lists every record that plugin holds, with each list's entries (conditions
too) on their own lines.

Don't use any of them to fix a plugin or change a load order (they never change existing files; `patch` only writes a
new plugin to its own folder), or `check` on Bethesda's own masters (Starfield.esm needs over 30 GB of memory).

## 2. Prerequisites

1. Windows 10 or 11 (x64) for the stand-alone exe: nothing else needed, .NET is built in.
2. Or, for the dotnet tool on Windows or Linux: the .NET SDK 9.0 or later (`dotnet --version` prints 9.x or higher).
3. Starfield installed, so its masters (Starfield.esm and the others in the game's `Data` folder) can be read.
   sf-mod-check only reads them.

## 3. Install

Pick one. Files come from https://github.com/taosecurity/sf-mod-check/releases (version 0.11.0 shown here).

1. **Stand-alone exe (Windows):** download `sf-mod-check-0.11.0-win-x64.zip` and its `.sha256` file, check the
   checksum, and unzip it to a folder of your choice:

       certutil -hashfile sf-mod-check-0.11.0-win-x64.zip SHA256
       mkdir C:\Tools\sf-mod-check
       tar -xf sf-mod-check-0.11.0-win-x64.zip -C C:\Tools\sf-mod-check

   Compare the printed hash with the one in `sf-mod-check-0.11.0-win-x64.zip.sha256` (it sits next to the zip on the
   Releases page). If the user has only the zip, print the hash, tell them it couldn't be compared, and ask them to
   download the `.sha256` file too. The program is then `C:\Tools\sf-mod-check\sf-mod-check.exe`.

2. **dotnet tool (Windows or Linux):** download `sf-mod-check.0.11.0.nupkg` into a folder (here `C:\Downloads`), then
   either install it for the user:

       dotnet tool install --global sf-mod-check --add-source C:\Downloads --version 0.11.0

   or into a folder of its own (nothing global changes):

       dotnet tool install sf-mod-check --tool-path C:\Tools\sf-mod-check-tool --add-source C:\Downloads --version 0.11.0

   On Linux, `sha256sum -c sf-mod-check.0.11.0.nupkg.sha256` checks the download.

## 4. Prove the install works

    sf-mod-check --version

Its first line is `sf-mod-check 0.11.0 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check`
(two credit lines follow), and it exits with code 0. A `check` prints the same first line on stderr, unless
`--quiet` is given; its stdout starts with the plugin's summary line and ends with the RESULT line. (Use the full
path, such as `C:\Tools\sf-mod-check\sf-mod-check.exe --version`, when the folder isn't on PATH.)

The examples below are written for PowerShell or cmd. In Git Bash, put Windows paths in single quotes so the
backslashes stay as they are. The first `check` also writes small caches (section 6, "Caches";
`SFMODCHECK_CACHE_DIR` moves them).

## 5. Common tasks

1. Check a plugin with a Steam install in the usual place:

       sf-mod-check check "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data\MyMod.esm"

2. Game Pass, or a Steam library on another drive: say where the game's Data folder is.

       sf-mod-check check MyMod.esm --data "D:\XboxGames\Starfield\Content\Data"

3. An add-on built next to the main plugin it needs: look for masters in the build folder first.

       sf-mod-check check out\MyMod-Addon.esm --masters-dir out

4. Mod Organizer 2: the plugin lives in `mods\<ModName>\`. Point `--data` at the real game Data folder and
   `--masters-dir` at the folder of any mod whose plugin is a master:

       sf-mod-check check "C:\Modding\MO2\mods\MyMod\MyMod.esm" --data "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data" --masters-dir "C:\Modding\MO2\mods\MainMod"

5. Something went wrong, or the result looks odd: write a troubleshooting log.

       sf-mod-check check MyMod.esm --log sf-mod-check-log.txt

   `--verbose` prints the same log lines on the screen (stderr) instead. A name with a control or invisible format
   character shows it escaped (`\n`, `\t`, `\u202E`), and the later lines of one multi-line log entry are indented
   by two spaces.

6. Fewer lines: `--max 20` (at most 20 lines per check), `--no-hints` (no HINT lines), `--quiet` (no summary line).
   The `assets` check (missing scripts, models, materials, textures and sounds; materials, textures and sounds since
   0.14.0, each a WARNING, found through the records, the plugin's own `.nif`, `.mat` and sound-bank `.bnk` files and
   the material databases, the game's and any a plugin ships) is
   on by default: `--no-assets` turns it off (for a plugin whose
   archive isn't built yet), `--ini <file>` adds an INI's archive lists (installs whose INIs aren't in the usual
   places), and `--language de` counts German voice archives instead of English. A path with a part that ends in a
   dot or a space (`meshes\armor.\helm.nif`) gets its own text: a WARNING when Windows' trimmed path is a loose file
   (it breaks once packed into a `.ba2`), else the usual missing finding with the trimmed path named (0.10.0). The
   log (`--log` or `--verbose`) also lists records that lack a field every official record of their type has; that
   listing is never a finding.

7. Check a whole load order for conflicts (Steam, the game's own `%LOCALAPPDATA%\Starfield\Plugins.txt`), then open
   `sf-mod-check-conflict\report.html`:

       sf-mod-check conflict

   Field priorities (0.10.0) are on by default. Fields xEdit rates benign or ignores (most Editor IDs, a location's
   Master reference lists) aren't counted as lost changes: the report lists them under "Benign differences (not
   counted as lost)" and the RESULT line adds ", <n> benign entries shown apart". Critical fields (a Game Setting's
   name, fields of a record injected into another plugin's FormIDs) are marked "Critical" and listed first. To count
   every differing field as a lost change, as 0.9.1 did:

       sf-mod-check conflict --no-priorities

8. Another load order or install: give the Plugins.txt, the Data folder and a report folder.

       sf-mod-check conflict --plugins "C:\Modding\MyProfile\plugins.txt" --data "D:\XboxGames\Starfield\Content\Data" --out conflict-report

9. Try a patch before installing it: put it in a folder of its own, add its line at the end of a copy of the
   Plugins.txt, and give the folder with `--extra-data` (its plugins win over the Data folder's).

       sf-mod-check conflict --plugins plugins-with-patch.txt --extra-data C:\MyBuild\out --out conflict-report-patched

10. A large load order on a PC with little memory: stop cleanly at a limit instead of slowing the PC.

        sf-mod-check conflict --max-memory 8 --log conflict-log.txt

    `--max-memory` takes a number of GB from 0.000001 to 1000000; anything else stops with exit code 2 and says so.

    If it stops at the limit (exit code 3, "RESULT: STOPPED (memory passed 8.0 GB ...)", no report written), the load
    order needs more memory than that: raise the number if the PC has it, or check a smaller load order. (The
    author's 148-plugin order peaks near 8.5 GB, so with 8 it sometimes finishes and sometimes stops.)
    `--quiet` leaves out the byline, progress and summary lines; `--verbose` prints the log on stderr.

11. `conflict` also runs the assets check (0.6.0) on every mod plugin in the load order, against the archives of every
    active plugin, the INIs' archive lists and loose files. The same options as `check` apply:

        sf-mod-check conflict --no-assets
        sf-mod-check conflict --ini "D:\MyGames\StarfieldCustom.ini" --language de

    The RESULT line then adds ", <n> missing assets (<s> scripts, <m> models)" when there are any (", <t> textures",
    ", <a> materials" and ", <d> sounds" are added when there are some, 0.14.0), and the report's "Missing assets"
    section lists each one with the plugin and the records or files using it. In conflicts.json, scripts and models
    are in `missingAssets` (`kind`: `script` or `model`), and materials, textures and sounds are in `missingFiles`
    (0.14.0, the same keys; `kind`: `material`, `texture` or `sound`). A mod's `.nif`, `.mat` or `.bnk` that can't be
    read, or a sound bank of an unknown Wwise version, is an assets-check note.
    A missing file an override inherits (the version it overrides, in the last of its masters that has the record,
    names it too; often a vanilla record) isn't reported against the override; `inheritedAssetUses` counts those uses
    and `--log` lists them. If the assets step stops part way, the report is still written and conflicts.json's
    `assetsFailed` holds the reason (null when it ran to the end); `Errors` has a line for it too, which the RESULT
    line counts as an assets-check note, not a read problem. In `check`, the same failure is one `WARNING assets`
    line and the other checks still count.

12. `conflict` counts the archives the game would load, general (GNRL) and texture (DX10) separately, even with
    `--no-assets`. The game has 256 slots for each kind; over that, the RESULT line adds
    ", WARNING <n> general archives (over 256)" or ", WARNING <n> texture archives (over 256)", and the report has a
    High recommended action. To read the counts in a script, use conflicts.json's `archiveCounts` (`gnrl`, `dx10`,
    `slots`) and `archiveLimitWarnings` (one text per registry over the limit, else empty). Each archive is counted
    by its header type (GNRL general, DX10 texture), as the game does, so a GNRL archive named `- Textures.ba2`
    counts as general. An archive whose header can't be read isn't counted; `unreadableArchives` lists it (with
    `--no-assets` too).

13. Check a release zip before uploading it (0.7.0). The result is OK or FAIL; read every PROBLEM line first:

        sf-mod-check release MyMod-1.0.0.zip

    An add-on's zip goes together with its main mod's zip, so the add-on's plugin finds the main plugin and its scripts:

        sf-mod-check release MyMod-Addon-1.0.0.zip MyMod-1.0.0.zip

    The `basic` profile (default) fails only on what breaks the mod for a player. `--profile house` also fails on
    packaging rules (a root `README.md` with no mod name, more than one plugin in a zip, `.psc` sources in the zip,
    private files such as `PERMISSION.md`), which `basic` shows as WARNING lines. A patch whose master mod's scripts
    live in that mod's archive: `--master-archive "C:\Mods\MainMod - Main.ba2"`. To also find files the plugin uses
    that the zip lacks, give the reviewed DMMDeps v0.4.1 build (any other build is refused):
    `--dmmdeps C:\Tools\dmmdeps-fat.exe`. Every line has its rule in brackets; the README lists them.

    To confirm the release is the build that was tested, and that its archives hold every staged file (0.15.0):

        sf-mod-check release MyMod-1.0.1.zip --tested MyMod-1.0.0.zip --stage C:\Build\MyMod\Data

    `--tested <zip|ba2>` compares each compiled script by meaning (not by bytes) and each plugin byte for byte with the
    tested build; a difference is a REVIEW line (rule `tested`), expected only where something changed after the test.
    `--stage <folder>` is the folder the release was built from: a staged file in no archive and not loose in the zip
    is a PROBLEM (rule `stage`). A `--tested` file or `--stage` folder inside the game folder is refused (exit 2).

    To lint scripts whose sources don't ship in the zip, with papyrus-tools v0.16.3 and the Creation Kit's sources
    (0.15.0):

        sf-mod-check release MyMod-1.0.0.zip --papyrus C:\Tools\papyrus.exe --ck-sources "C:\Games\Starfield\Tools\ContentResources.zip" --psc C:\Build\MyMod\Scripts\Source --psc-import C:\Build\OtherMod\Scripts\Source

    `--psc <folder>` is the folder above the namespace folders; it is linted instead of the zips' `Scripts\Source`.
    `--psc-import <folder>` adds another mod's sources to the imports, after the vanilla sources. Both can be given
    more than once. Findings are WARNING lines (rule `papyrus-lint`) and never fail the release. Without `--papyrus`
    and `--ck-sources` one NOTE says the folders were not used; a folder that doesn't exist is exit 2.

    Before uploading a Bethesda Creation (PC, Xbox and PlayStation), add the Creations rules (0.10.0):

        sf-mod-check release MyMod-1.0.0.zip --profile creations

    It fails on INI, DLL, EXE, ASI or SFSE files, scripts that call SFSE plugins, masters not known to be on Creations,
    texture archives with no `_xbox.ba2` twin, and a zip over 2 GB (each zip is checked on its own). It warns about other archives with no twin, PC-format sound, and
    `Debug.Notification` or `Debug.MessageBox` calls players would see (they show nothing on consoles). A script that
    calls an SFSE plugin and also calls `Game.IsPluginInstalled` is a WARNING, not a failure: confirm the guard. If that
    script tests only official plugins (Starfield, the DLC, `BlueprintShips-Starfield.esm`, `SFBGS###`, `sftaNN` and their `BlueprintShips-` files), it stays a failure (0.10.1). A master by
    another author that the user has seen on Creations for every platform: `--creations-master OtherMod.esm`. Never add
    one on your own judgement; ask the user.

    A mod built with sf-mod-build and `"xbox": true` writes the set to upload into a folder named `creations-out`
    (the plugin, the PC archive and the `_xbox.ba2` twin). Run this check on that folder, not on the PC test zip, which
    has no Xbox archive: `sf-mod-check release creations-out --profile creations`.

    To check a build before it is zipped, or a Creation built from several parts (0.15.0), give the inputs as
    `release <zip|folder|ba2|plugin>...`:

        sf-mod-check release C:\Build\MyMod\Data "C:\Build\MyMod - Textures.ba2" --profile creations --size-total

    A folder is read where it is (nothing is written into it); loose `.ba2` and plugin files are checked together as
    one package named `loose files`. The zip-only rules (`one-plugin`, `root-md`) skip them, with one NOTE (rule
    `not-a-zip`). `--size-total` counts every input of the run as one Creation for the 2 GB rule; it needs
    `--profile creations` (otherwise exit 2). A folder inside the game folder, or two loose files of the same name,
    is exit 2.

    To accept one known finding, such as a script that another author's plugin names and that ships nowhere
    (0.15.2):

        sf-mod-check release MyPatch-1.0.0.zip --accept script-missing:OtherMod.esm:OtherMod:Toggle
        sf-mod-check release MyPatch-1.0.0.zip --accept-file known-gaps.txt

    An entry is `<rule>:<plugin>:<detail>`: one of the rules `script-missing`, `model-missing`, `mesh-missing`,
    `texture-missing`, `material-missing`, `sound-missing` and `face-missing`; the plugin's file name; and the
    script's name (for `script-missing`) or the missing file's path. Case doesn't matter, and wildcards aren't
    allowed. The finding still prints, as an `ACCEPTED [rule] ...` line naming the entry, and no longer counts toward
    FAIL. An entry that matches nothing is a `WARNING [accept]` line. A malformed entry, or an `--accept-file` that
    can't be read, is exit 2. The file holds one entry per line; `#` starts a comment. Accept only a finding the
    user has looked at and agreed is a known gap; never add an entry on your own judgement.

14. Look over a downloaded mod before installing it (0.8.0). The report goes next to the download unless `--out` is
    given; open the `.html` file:

        sf-mod-check triage "C:\Downloads\SomeMod-1.2.zip"
        sf-mod-check triage "C:\Downloads\SomeMod-1.2.7z" --out C:\Reports
        sf-mod-check triage "C:\Games\MO2\mods\SomeMod"
        sf-mod-check triage SomeMod.esm --game "D:\XboxGames\Starfield\Content"

    `--game` takes the game's folder, its `Data` folder, or (Game Pass) its `Content` folder; without it Steam's usual
    folder is used, and with no game found nothing is compared with the game (the report says so). For DLLs,
    `--manalyze C:\Tools\manalyze\manalyze.exe` also runs Manalyze with offline plugins only (never VirusTotal). Read
    the LOOK lines first; summarise them for the user as "worth a look", never as faults or as proof of harm. A mod
    with no plugin (only sounds, textures or meshes, say) has nothing for `check`: the Plugins section says so, and
    the Files and Replaces vanilla sections are the ones to read. When the input and the current folder are both inside
    the game's install, triage writes nothing there and asks for `--out` (exit 2).

15. Build a conflict patch (0.9.0). The user added two mods and `conflict` says one undoes part of the other: build a
    patch for the conflicts that involve them, read the report, check the patch, then have the user install it with
    their mod manager at the end of the load order:

        sf-mod-check patch --only NewModA.esm,NewModB.esm
        sf-mod-check conflict --plugins sf-mod-check-patch\Plugins-with-patch.txt --extra-data sf-mod-check-patch
        sf-mod-check check sf-mod-check-patch\ConflictPatch.esm

    Without `--only` it patches every conflict in the load order. Read `sf-mod-check-patch\patch-report.md` with the
    user: "Choices" are fields both mods changed. A list there says "merged entry by entry" (both mods' entries are
    kept, so usually nothing to choose; if the user wants one mod's whole list instead, add
    `--pick <FormKey>:<Field>=<plugin>` for it). For any other field the winner's value is kept unless the user wants the other one,
    then run `patch` again with the `--pick` line the report prints for it, for example
    `--pick 0012AB:Starfield.esm:Value=NewModA.esm`. Since 0.13.0 a field pick works on any field: it can also keep
    one plugin's value where the patch would forward another mod's change, or add a record with no lost change (for
    example `--pick 0012AB:Starfield.esm:Water=Starfield.esm` keeps a worldspace's water as the game ships it when a
    mod filled it). Use it only when the user asks for that value. "Not built" records (dialogue,
    scenes; cells, worldspaces, placed objects and navmeshes are built since 0.13.0) must be done in xEdit; give the user the report's list. The navmesh info map (NAVI)
    is listed there too but must not be copied by hand. The `conflict` run on `Plugins-with-patch.txt` still lists
    those records, Choices that kept the winner's value, and harmless lost changes (only a reorder, letter case or
    undocumented fields; letter case includes a file name stored as raw bytes since 0.13.0), which `patch` leaves alone by design. Exit 1 means the read-back
    found a difference: tell the user not to install that patch. Other options: `--plugins`, `--mo2`, `--profile`,
    `--data`, `--extra-data`, `--max-memory`, `--log` as for `conflict`; `--out <folder>` (never the Data folder, the
    `--extra-data` folder or an MO2 mod or Overwrite folder; with `--mo2` the user adds the patch in MO2 with Create
    empty mod, as the patch report says); `--name
    <Patch.esm>` (default `ConflictPatch.esm`). Run it again after any of the patch's masters is added, removed or
    updated. `--include-official` (off by default; not with `--only`) also restores changes one official plugin loses
    to another; use it only when the user asks, since `conflict` never lists those losses and can't show them restored.

16. Compare two versions of a mod (0.11.0): two downloads, two folders, a download and a folder, or two plugins.
    Give the old version first:

        sf-mod-check diff "C:\Downloads\SomeMod-1.0.zip" "C:\Downloads\SomeMod-1.1.zip"
        sf-mod-check diff OldCopy\SomeMod.esm SomeMod.esm --json --out diff.json

    To read the result, use the exit code and the last stdout line: exit 0 and `RESULT: NO DIFFERENCES (...)` mean
    the two versions are the same (noise only), exit 1 and `RESULT: DIFFERENCES (...)` mean they differ, and exit 2
    and `RESULT: CANNOT DIFF (...)` mean it couldn't compare them (the message above it names the file and the fix).
    The RESULT line counts the plugins, records and files, for example
    `RESULT: DIFFERENCES (1 plugin compared, 1 changed, 0 added, 0 removed; records: 2 added, 1 removed, 3 changed, 0 renumbered, 0 noise; files: 1 added, 0 removed, 3 changed)`.
    For the details, read the JSON (`--json --out diff.json`; its shape is in section 6 under `diff`).
    Exit 0 means no differences, 1 differences, 2 bad input. Records whose bytes differ but whose fields are all
    equal are counted as noise, which is no difference (exit 0 when only noise differs); scripts and script properties
    are matched by name, so a new order alone is noise. `--data` gives the game's Data folder (default: Steam's
    usual one, for master EditorIDs), `--quiet`, `--verbose` and `--log <file>` work as for `check`. `--out` and `--log` never take a plugin, archive
    or script name, an input, or a path inside an input folder, and they must be different files. The report groups placed objects by cell and lists leveled-list
    entries added and removed and base-object swaps. For two mods it also compares the loose files (SHA-256), each
    archive's files (paired by name, hashed as stored) and changed scripts (objects, states and functions by name,
    never code); any file difference counts as a difference. Long text lists are cut short; `--all` lists everything (the JSON
    always does):

        sf-mod-check diff OldCopy\SomeMod.esm SomeMod.esm --all --out diff.txt

    Check a plugin a Mutagen-based tool wrote for the data Mutagen 0.54.4 loses (0.15.0; the same check as the
    `release` command's `mutagen-gap` rule):

        sf-mod-check gap out\MyMod.esm --masters-dir out

    Masters are looked for in the plugin's own folder, each `--masters-dir` in order, then `--data`. `--quiet`
    drops the byline and, when there are errors, the WARNING lines; `--verbose` and `--log <file>` work as for `check`.

17. Tell whether a conflict patch is still up to date (0.15.0). It reads the `patch-sources.json` that `patch`
    wrote beside the patch, compares it with the load order as it is now, and changes nothing:

        sf-mod-check patch check sf-mod-check-patch\ConflictPatch.esm --plugins "%LOCALAPPDATA%\Starfield\Plugins.txt"
        sf-mod-check patch check sf-mod-check-patch\ConflictPatch.esm --plugins sf-mod-check-patch\Plugins-with-patch.txt --extra-data sf-mod-check-patch --json check.json

    Use the second form before the patch is installed. Read the exit code and the last stdout line: exit 0 and
    `RESULT: UP TO DATE` (`RESULT: NOT CHECKED (the patch isn't in the load order; nothing was checked)` when the patch isn't active, also exit 0: don't read it as up to date),
    exit 1 and `RESULT: REBUILD (n reasons)` (run the same `patch` command again, or follow the `[moved]` or
    `[duplicate]` advice), exit 2 and `RESULT: CANNOT CHECK (...)` (the message above it names the file and the fix;
    no `patch-sources.json` beside the patch means it was made before 0.15.0, so make it again). The lines before
    it are `LEVEL [rule] text`: PROBLEM `[removed]`, REVIEW `[moved]`, `[updated]`, `[new-editor]`, `[duplicate]`
    and `[patch-changed]`, NOTE `[updated]` and `[patch-off]` (section 6 says what each means).

18. Look up one record (0.16.0): its FormKey, type, the plugin that defines it, the plugin that wins it in the load
    order, and its main fields. Use it instead of writing a program to read a plugin. Read only:

        sf-mod-check find CityNewAtlantisLocation --type LCTN
        sf-mod-check find 01295A:Starfield.esm --json
        sf-mod-check find 0001295A --plugins "%LOCALAPPDATA%\Starfield\Plugins.txt"

    The record is an exact EditorID (letter case ignored, no partial match), a FormKey, or a console FormID (8 hex
    digits; it depends on the load order, so prefer the FormKey in anything you write down). `--plugins`, `--data`
    and `--extra-data` give the load order as for `conflict`. Read the exit code and the last stdout line: exit 0 and
    `RESULT: FOUND n record(s)`, exit 1 and `RESULT: NOT FOUND (...)` (the `HINT:` line before it names the load
    order searched; check the spelling, or the mod isn't in that load order), exit 2 and `RESULT: CANNOT CHECK (...)`.
    With `--json`, stdout is one JSON object (section 6) and nothing else.

    For a plugin outside Data whose masters are outside Data too (0.16.1), give the plugin and the folders that
    hold its masters, with no Plugins.txt. With no record, it lists every record in the plugin (at most `--max`,
    default 100; `--max 0` = all) and ends with a summary of all of them:

        sf-mod-check find --plugin "C:\Downloads\SomePatch\SomePatch.esm" --masters-dir "C:\Downloads\SomeMod"
        sf-mod-check find 1E3F4D:Starfield.esm --plugin "C:\Downloads\SomePatch\SomePatch.esm" --masters-dir "C:\Downloads\SomeMod"

    Masters are looked for in the plugin's own folder, then each `--masters-dir` in order, then `--data` (Steam's
    Data by default); a master in none of them is exit 2. `--plugin` with `--plugins`, `--mo2` or `--extra-data`,
    and `--masters-dir` or `--max` without it, are exit 2. A list field prints its count and then one `      - ` line
    per entry (at most 50); a condition reads `[OR ]Function(parameters) operator value (run on ...)`.

## 6. Inputs and outputs

- **Input:** one plugin file per run, plus its masters, found in this order: `--masters-dir` folders (or the plugin's
  own folder when none is given), then the Data folder (`--data`, else Steam's usual
  `C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data`; with no `--data` and no game there, a plugin that
  sits in a folder holding Starfield.esm uses that folder). For `check`'s assets step, the plugin's own folder is
  always searched first for loose files and the plugin's own archives, with or without `--masters-dir` (a master's
  archives and the INIs' archives are looked for in the masters' order only), so the mod's own `<plugin> - Main.ba2` and loose files
  beside the plugin count (0.16.2).
- **Output (stdout):** a summary line; `ERROR [check] ...` lines (broken: fix before release); `WARNING [check] ...`
  lines (probably a mistake); `HINT [check] ...` lines (how to fix that check's findings in xEdit or the Creation Kit);
  a `SHIPS: <n> ships listed: ...` count line only when the plugin adds or overrides a ship (0.10.0; the per-ship
  listing of modules by system, class, weapon slots and missing systems is in the `--verbose`/`--log` log; missing
  systems on an added ship with a cockpit are a `WARNING [ships]` line); and always one last line `RESULT: OK|FAILED|CANNOT CHECK ...`. Scripts should read only the exit code and the lines
  starting with `ERROR `, `WARNING ` and `RESULT: `.
- **stderr:** notes (very large plugin, no Starfield.esm found), error messages for bad arguments, and the
  `--verbose` log lines (`log: ...`).
- **`release`:** input is one or more zips, stage folders, `.ba2` files or plugins (`release <zip|folder|ba2|plugin>...`, 0.15.0). Output (stdout) is `PROBLEM`, `REVIEW`, `WARNING` and `NOTE` lines, each as
  `<LEVEL> [rule] text`, then `ACCEPTED [rule] text (was a <LEVEL>; accepted by <where> <entry>)` lines for findings
  an `--accept` entry accepted (0.15.2), then `RESULT: OK|FAIL (n problems, n to review, n warnings)`, with
  `, n accepted` before the closing parenthesis when any were accepted (or `RESULT: CANNOT CHECK (...)`).
  Exit codes: 0 OK, 1 FAIL, 2 can't check (bad arguments such as `--size-total` without `--profile creations` or a malformed `--accept` entry, a bad zip, a missing file, an input folder, `--tested` file or `--stage` folder inside the game folder, two loose inputs of the same name, or an error that stops the check). Scripts should read the exit code and the `PROBLEM ` and `RESULT: ` lines.
- **`triage`:** input is one .zip, .7z or .rar (unpacked into a temp folder that is removed at the end;
  password-protected archives are refused), a mod folder (read where it is; the game's install, its Data or Content folder, or a folder inside them is refused) or
  one plugin (with the archives beside it that share its name). Output: `<out>\<name>-triage.html`, `.txt` and
  `.json` (default `<out>` = the input's folder, or the current folder when the input is inside a game install; when both are, nothing is written and exit 2 asks for `--out`; an `--out` inside a game install is refused the same way; a folder or `--out` path that can't be checked against the game is refused too, with a message saying so).
  The JSON has `triage_not_a_verdict: true`, `input` (name, path, kind, size, sha256), `game_data`, `game_found`,
  `incomplete` (sections that stopped early), `findings` (each `level` NOTE or LOOK, `section`, `file`, `text`) and
  `tables` (`section`, `title`, `columns`, `rows`). Since 0.17.0 the Plugins section has the table "Changes to
  official records, field by field" (columns Plugin, Record, Field, Path, Kind, Before, After): one row per changed
  value or list entry, with Kind `changed`, `added`, `removed`, `same entries, other order` (a list reordered),
  `same English text, translations dropped` or `same English text, translations added`, or `not shown` (a row that
  counts the record's rows past 200, plus 200 more for its rows of the two translation kinds). Padding members (`Unused`) get no rows; a field whose only difference is padding
  gets one `changed` row whose After ends with "(only padding differs ...)". Rows of real changes come before rows of
  translation-only fields. A list too long to match entry by entry (over 2,000 entries, no key, length changed) gets
  one `changed` row that says so. Use it, not the cut-off Before and After of "Changes to official records", to
  say what an override changed. On stdout: a summary line, one `LOOK [section] ...` line per LOOK,
  `INCOMPLETE ...` lines, and always one last line `RESULT: REPORT WRITTEN (<n> to look at, <n> notes) <html path>` or
  `RESULT: NO REPORT (...)`. Exit codes: 0 = report written (whatever it found: there is no "fail"); 2 = bad input,
  setup or an error that stops the run. Scripts should read the exit code and the JSON.
- **`gap`:** input is one plugin and its masters. Output: a summary line, `  ERROR   ...` lines (data Mutagen 0.54.4
  loses, and placed references missing from the ONAM list), `  WARNING ...` lines (other fields the master has and
  the plugin lacks; at most 200, then `  ... n more warnings`), a count line, then always one last line
  `RESULT: OK (0 errors, n warnings)`, `RESULT: FAIL (n errors, n warnings)` or `RESULT: CANNOT CHECK (...)`. A
  missing master prints `master <name> not found in <folders>` before the CANNOT CHECK line. Exit codes: 0 = no
  errors, 1 = errors, 2 = can't check (missing plugin or master, not a plugin, bad argument).
- **`find`:** input is one record (an EditorID, a FormKey or a console FormID) and a load order (`--plugins`
  or `--mo2` with `--profile`, `--data`, `--extra-data`). Output: a line naming the load order, then per record a heading
  `EditorID [FormKey] TYPE "Name"`, `  defined in:`, `  winner:`, `  also in:` (when other plugins have a copy) and
  `  fields:` (at most 40 lines), then always one last line `RESULT: FOUND n record(s) (n s)`,
  `RESULT: NOT FOUND (...)` (after a `HINT:` line) or `RESULT: CANNOT CHECK (...)`. With `--json`, stdout is one
  object instead: {`tool`, `version`, `query`, `type`, `loadOrder` {`source`, `plugins`}, `matches` [{`formKey`,
  `editorId`, `type`, `name`, `definedIn`, `winner`, `plugins`, `fields`, and since 0.16.1 `entries` {field name:
  [entry text, at most 50]}}], `result` (`found`, `not found`, `cannot run`), and `hint` or `error`}; keys are only
  ever added. With `--plugin` and no record (0.16.1), the text lists every record (at most `--max`), then
  `... and n more records (...)` when some were left out, then `<plugin> holds n records: n new, n overrides; by
  type: ...`, then `RESULT: FOUND n record(s) in <plugin>[, n listed] (n s)`; the JSON adds `plugin`, `total`,
  `listed` and `byType`, and `query` is null. Exit codes: 0 = found, 1 = not found (or a plugin with no records of
  that `--type`), 2 = can't run (bad option, no load order, missing folder or master). `--help` exits 0.
- **`diff`:** input is two plugins, or two mods (each a .zip, .7z or .rar, unpacked into a temp folder that is
  removed at the end, or a folder read where it is); plugins in two mods are paired by file name. Output: the report
  on stdout, or in the `--out` file (text, or JSON with `--json`), then always one last line on stdout:
  `RESULT: NO DIFFERENCES (...)`, `RESULT: DIFFERENCES (...)` or `RESULT: CANNOT DIFF (...)`. Exit codes: 0 = no
  differences (noise only counts as none), 1 = differences, 2 = bad input or setup. Scripts should read the exit code
  and the JSON. With `--json` and no `--out`, the RESULT line follows the JSON on stdout, so stdout is not pure JSON:
  use `--json --out diff.json` and load the file (stdout then gets only the RESULT line). The JSON shape (`schema` 1; a key that doesn't apply is null, not left out):
  - Top level: `schema`, `tool`, `old` and `new` (each `path`, `kind` plugin, folder, zip, 7z or rar, `files` (a
    count), `skipped` (a count)), `plugins`, `files`, `archives`, `scripts`, `differences` (true or false),
    `exit_code`.
  - Each of `plugins`: `name`, `status` (same, changed, added or removed), `old` and `new` (its path inside each
    input, null when it is only in one). A plugin only in one version has nothing more. A paired one also has:
    - `header`: `old` and `new` (each `type` full, medium or light, `flags` as hex, `form_version`, `masters` (each
      `name`, `type`, `found`), `hedr_records`), and `changes` (each `text`, and `real`: false = information only).
    - `records`: `old` and `new` (each `records`, `own`, `overrides`, `past_end`, `duplicates`, `unpackable`), and
      the totals `own` and `overrides`, each a count object: `added`, `removed`, `changed`, `noise`, `renumbered`,
      `same`.
    - `types`: each `type` (the record type, such as REFR) with `own` and `overrides` count objects.
    - `fields_not_compared` (why Mutagen couldn't read the plugin, or null), and `changed`: each `key`, `type`,
      `editor_id`, `override`, `fields` (the field names that differ), `not_compared`.
    - `renumbered`: each own record that kept its type and EditorID under a new FormID: `type`, `editor_id`,
      `old_key`, `new_key`.
    - `cells`: each `cell` (key), `editor_id`, `added`, `removed`, `changed` and `moved_out` (each an object of counts
      keyed by record type, such as `{"REFR": 3}`), and `records` (each `change` added, removed, changed or moved
      out, `key`, `type`, `editor_id`, `moved_from`, `moved_to`).
    - `leveled_lists`: each `key`, `type`, `editor_id`, `override`, `added` and `removed` entries (each `level`, `ref`,
      `editor_id`, `count`), and `other_entry_changes`.
    - `base_swaps`: each `key`, `type`, `editor_id`, `cell`, and `old` and `new` (each `key`, `editor_id`).
    - `editor_ids_missing`: the masters that weren't found, so their records show as FormKeys.
    - `editor_ids_damaged`: the masters that were found but stop early (damaged or cut short) before every record
      asked for, so some of their records show as FormKeys.
  - `files` (null for two plugins): `added` and `removed` (paths), `changed` (each `path`, `old_size`, `new_size`),
    and `same` (a count).
  - `archives` (null for two plugins): each `name`, `status` (same, changed, added or removed), `old`, `new` (paths
    inside each input), `type` (GNRL or DX10), `old_files`, `new_files`, `same_bytes`, `added`, `removed` and `changed`
    (file names inside), `same` (a count), `error` (why its files couldn't be listed, or null).
  - `scripts` (null for two plugins): each changed .pex as `path`, `where` (loose, or the archive's name), `error`,
    `code_same` (true when only the bytes differ), and `objects`, each `name`, `status` (added, removed or changed), `parent_change`,
    `states_added`, `states_removed`, `functions_added`, `functions_removed`, `functions_changed`, `other` (names
    only, never code).
- **Exit codes (`check`):** 0 = no errors (warnings allowed); 1 = errors; 2 = couldn't check (bad arguments, a missing file or
  folder, a file it couldn't open, or it ran out of memory: then no result is shown for an unfinished check).
- **Help (every command):** `--help`, `-h` and `<command> --help` print the help on stdout and exit 0 with no RESULT line (since 0.13.0; 0.12.0 and earlier exited 2). A bad argument still exits 2 with its RESULT line.
- **Log:** only when asked: `--log <file>` writes it to that file (a relative name is in the current folder; overwritten each run; a plugin or archive name is refused), `--verbose` to stderr. It
  holds the version, the arguments, the plugin, the Data folder and why it was chosen, where each master was found (or
  every place looked), the caches used or rebuilt, the time per stage, a count of findings per check, and on a crash
  the full error. It names folders on your PC: read it before posting it anywhere.
- **conflict input:** the active (`*`) lines of a Plugins.txt (`--plugins`, else the game's own
  `%LOCALAPPDATA%\Starfield\Plugins.txt`; with neither it stops with exit code 2), the plugins themselves from
  `--extra-data` and then the Data folder (`--data`, else Steam's usual one). Since 0.16.0, `--mo2 <MO2 instance
  folder>` (the one with `ModOrganizer.ini`) and `--profile <name>` (default: MO2's selected profile) read a Mod
  Organizer 2 profile instead: the order from its `loadorder.txt` (else `plugins.txt`), the on/off marks from
  `plugins.txt`, and plugins, archives and loose files from `--extra-data`, then MO2's Overwrite, then the enabled
  mods from the top of `modlist.txt` down, then `Documents\My Games\Starfield\Data` when it exists (0.16.1), then Data (`--data`, else `<gamePath>\Data` from the ini, else Steam's).
  `--mo2` with `--plugins`, or `--profile` without `--mo2`, is exit 2. `loadOrderSource` then starts `MO2 profile `.
  An enabled mod with no folder is a `loadOrderProblems` line. Profile-local INIs aren't read (give `--ini`). Bethesda's own files load first,
  except the official `BlueprintShips-*.esm` files, which load after every plugin, oldest file first (since 0.12.0,
  as the game does; a Plugins.txt line for one doesn't move it). A change lost to one of them is a `lost` entry
  with that file as `Winner`; its recommendation says no load order or patch brings it back, and `patch` lists the
  record under "Not built" (counted in the RESULT line's records not built) instead of building it. The report's
  Kind column says `Lost` for such a field, and since 0.15.0 the pair tables count those fields in a `Lost fields`
  column (report.html also puts an "n lost" badge on the pair; conflicts.json has no new key). The navmesh info map (`NavigationMeshInfoMap`) is the exception: it
  keeps its own note and its own "Not built" reason, because each plugin's copy lists only its own navmeshes.
  A mod with a BlueprintShips master gets a `loadOrderProblems` line but is still loaded and compared, judged
  against that master's version; `triage` reports that line as a load-order note, not as "left out". A patch whose
  copied values point into a BlueprintShips file gets it as a master and says so in the report's Notes.
- **conflict output:** a report folder (`--out`, default `sf-mod-check-conflict`) with `report.html` (to read),
  `report.md` and `conflicts.json` (for scripts: `lost`, `itms`, `loadOrderProblems`, `typeChanges`, `Errors`, and
  since 0.6.0 `assetsChecked` (false with `--no-assets`), `archivesRead`, `inheritedAssetUses` (missing-path uses
  left out because the version they override names them too), `assetsFailed` (why the assets step stopped part
  way, else null), `missingAssets` (each with `plugin`,
  `severity` ERROR for a script or WARNING for a model, `kind`, `path`, `script`, `users`, `finding`; since 0.14.0
  materials, textures and sounds are in `missingFiles`, with the same keys) and
  `unreadableArchives` (`archive`, `why`), `archiveCounts` (`gnrl`, `dx10`, `slots`; null if not counted) and
  `archiveLimitWarnings`, and since 0.10.0, unless `--no-priorities` is given, a `priority` on each `lost` entry and
  field (`normal` or `critical`), `benign` (entries shaped like `lost`, each field's `priority` `benign` or `ignore`)
  and `fieldPriorities` (`source`, `criticalEntries`, `benignEntries`)). On
  stdout a summary line and always one last line `RESULT: OK, <n> lost-change entries (...), <n> ITMs, <n> load-order
  problems (report: <path>; <seconds> s)` (with field priorities on, ", <n> benign entries shown apart" and, when
  there are any, " (<c> critical lost-change entries)" come right after the lost-change part; with ", <n> overrides that change a record's type", ", <n> missing assets
  (<s> scripts, <m> models)", ", <n> archives that can't be read" (with `--no-assets`: ", <n> archives that can't be
  read (not counted)"), ", WARNING <n> general|texture archives (over 256)", ", <n> read problems (records not
  compared ...)" and ", <n> assets-check notes (...)" before `(report:` when there are any), or `RESULT: CANNOT CHECK (...)` or `RESULT: STOPPED (...)`. Progress lines go to stderr. Stack traces that Mutagen
  (the plugin library) prints itself when it can't fully read a scene's actions, for example
  `System.ArgumentException: Unexpected header type: PAQO` (5 on the author's 157-mod order; `check` can meet them
  too), go to stderr with each line starting `mutagen: `, and to the `--log` file; stdout holds only sf-mod-check's own
  lines. sf-mod-check doesn't count these as findings, so scripts should read only the exit code and the `RESULT: `
  line (in `check`, also the `ERROR ` and `WARNING ` lines).
- **conflict exit codes:** 0 = report written (whatever it found); 2 = couldn't run (running out of memory included:
  no report); 3 = stopped at `--max-memory` (no report).
- **patch input:** the same as `conflict`, plus `--only` (plugin file names), `--pick` lines and `--include-official`. **patch output:** in
  `--out` (default `sf-mod-check-patch`): the patch plugin (light, loads last; `--name`, default
  `ConflictPatch.esm`), `patch-report.md`, `Plugins-with-patch.txt` (the load order with the patch line at the end)
  and `patch-sources.json` (see below); when the load order already holds a plugin of the patch's name, also
  `Plugins-without-patch.txt` (the order it was built from). `patch-sources.json` (0.15.0) records what the patch was built from, in schema 1, with `schema`, `tool`,
  `made`, `patch` {`name`, `size`, `sha256`}, `loadOrder.plugins` (file names only, without the patch), `masters`
  [{`name`, `position`, `size`, `modified` (UTC), `sha256`}] and `records` [{`formKey`, `type`, `editorId`, `editors`
  [{`plugin`, `hash`}]}]; `hash` is 16 hex digits of the editor's copy of the record, or null when it couldn't be
  read; a plugin saved again without changes keeps its hashes; `type` is the record type's class
  name, such as `MiscItem`. With nothing to patch, no plugin is written and an
  old one there is removed, with its `Plugins-with-patch.txt` and `patch-sources.json`. On stdout: `ERROR [patch]`
  lines for read-back differences, then always one last line `RESULT: OK, patch <path>: <n> records, <n> fields
  forwarded, <n> picks used, <n> shared lists merged, <n> choices kept the winner's value, <n> records not built, <n> masters (report: <path>;
  <s> s)`, or `RESULT: OK, nothing to patch: ...`, `RESULT: FAILED, <n> read-back differences (don't use this patch),
  ...`, `RESULT: CANNOT BUILD (...)` or `RESULT: STOPPED (...)`. Exit codes: 0 = written (or nothing to patch); 1 =
  written, but the read-back found a difference (don't use it); 2 = couldn't run; 3 = stopped at `--max-memory`.
- **patch check input** (0.15.0): `sf-mod-check patch check <patch.esm>` with `--manifest <patch-sources.json>`
  (default: the one beside the patch), `--plugins`, `--data` and `--extra-data` (the load order now, as for `patch`),
  `--json <file>`, `--quiet`, `--verbose`, `--log <file>`. Read only. **patch check output:** on stdout, PROBLEM
  lines, then REVIEW, then NOTE, each `LEVEL [rule] text`:
  PROBLEM `[removed]` (a master isn't in Data, isn't active or can't be loaded);
  REVIEW `[moved]` (a master is listed after the patch, or a plugin of the build now loads after it and changes a
  patched record; BlueprintShips files excepted);
  REVIEW `[updated]` (a master's size or SHA-256 changed and its copy of a patched record hashes differently from
  `patch-sources.json`, or it now holds a patched record it didn't; NOTE `[updated]` when none of those records changed);
  REVIEW `[new-editor]` (an active plugin that isn't a master now changes a patched record);
  REVIEW `[duplicate]` (another spelling of the patch, differing in spaces or case, is active too);
  REVIEW `[patch-changed]` (the loaded patch file isn't the manifest's build);
  NOTE `[patch-off]` (the patch isn't active; nothing else is checked).
  Then `RESULT: UP TO DATE`, `RESULT: NOT CHECKED (...)` (the patch isn't active), `RESULT: REBUILD (n reasons)` (n = PROBLEM and REVIEW lines) or
  `RESULT: CANNOT CHECK (...)`. Texts show up to 5 FormKeys. `--json` writes {`patch`, `state` (`up to date`,
  `rebuild`, `not active`, `cannot check`), `result`, `findings` [{`level`, `rule`, `plugin`, `text`, `records`
  (all FormKeys)}], and `why` for `cannot check`}. Exit codes: 0 = up to date or not active; 1 = rebuild or review;
  2 = can't check (no or unknown manifest, an unreadable patch or load order, a bad argument). `--help` exits 0.
- **Caches:** `%LOCALAPPDATA%\sfmodcheck\` on Windows, `~/.local/share/sfmodcheck/` on Linux (the folder kept its old
  name; the masters' EditorIDs, the learned list of official scenes and triage's index of the game's archive file
  names; rebuilt by themselves after a game update). The environment variable
  `SFMODCHECK_CACHE_DIR` moves them. Deleting the folder is safe.

## 7. Limits

1. It never changes, moves or deletes a plugin, the load order or anything in the game folder. It writes only its
   caches, the `--log` file, (conflict) the `--out` report folder, (patch) its `--out` folder, which may not be the
   Data folder, (triage) its three report files, and (release, triage) a temp folder holding the unpacked archive,
   which it removes at the end. It never changes the zip. Installing a patch is the user's job, with their mod manager.
2. It makes no network connections and uploads nothing. With `--dmmdeps` or `--papyrus` it starts that program, and
   only the exact builds named in the README (checked by SHA-256). With `--manalyze` it starts that program with
   offline plugins only. `triage` never loads or runs a mod's DLLs, EXEs or scripts: it reads their bytes; the
   signature check asks Windows with no revocation lookup and no download.
3. `check` checks one plugin against its masters; `conflict` compares plugin records across a load order. The
   `assets` check in both looks up the names of attached scripts, models, materials, textures, the `.mesh` geometry
   files a mod's own `.nif` files name (since 0.15.0; `release` rule `mesh-missing`) and the sounds of a mod's own
   sound banks in loose files and BA2 archives. It reads only names from a mod's own `.nif`, `.mat` and `.bnk` files,
   never geometry, texture or sound data, and it doesn't check face data, voice files or music.
   Since 0.18.0 `release` checks voice files (rule `voice-missing`, a WARNING): each dialogue line a plugin adds, other
   than player lines, needs `sound\voice\<plugin>\<voice type>\<number>.wem` in the plugin's own voice archives or
   loose files, for each voice type its speaker conditions name, or in any voice-type folder when they name none.
   `release` also looks for each new or changed NPC face's baked files (rule `face-missing`, a NOTE when none of
   the five is there, and since 0.15.0 a NOTE that the face is stale when an override changes it but every face
   file found is the master's), archives named so that the game never loads them (rule `archive-name`; since
   0.15.1 also a WARNING for a plugin's archive whose part is none of `Main`, `Textures` (each optionally numbered
   `01` to `99`) or a language's voice archive such as `Voices_en`, or whose number comes after a gap, since the game loads it only through an INI archive list), and file
   names with non-ASCII characters or loose paths over 200 characters (rule `file-name`, a WARNING; since 0.15.0
   `triage` reports the same file names as LOOKs in its Files section).
4. A patch `conflict` suggests or `patch` builds holds other authors' data: it is for the player's own game, never
   for publishing. `patch` doesn't build the records inside quests or dialogue topics (the report lists them for
   xEdit). Since 0.13.0 it builds cells and worldspaces themselves (without their other records or RNAM/OFST/CLSZ),
   and placed objects and navmeshes, each inside its cell; a cell or worldspace brought in only as a parent goes in
   as the game uses it today, with no field changed. A navmesh's mesh (geometry, NNAM, pre-cut entries) comes whole
   from one plugin; when two plugins each changed a different part of it, today's mesh is kept and the report says so.
   Mod Organizer 2 keeps mods out of the Data folder: for `conflict`, `patch` and `find`, pass `--mo2 <the MO2
   instance folder>` (and `--profile <name>` if it isn't the selected one) instead of `--plugins`. Without it, each
   MO2 mod is a load-order problem ("listed but not found in Data") and is left out. `--mo2` was tested on made-up
   MO2 folders only (0.16.0): check the report's load-order line and problems before trusting a result. `check`,
   `triage` and `patch check` don't take `--mo2`.
5. A missing master stops the link checks (the RESULT line says "links not checked"): install the master or point
   `--data` / `--masters-dir` at it, then run again.
6. `check` isn't meant for Bethesda's own masters (Starfield.esm needs over 30 GB of memory and about 4 minutes).
   `conflict` loads them but only compares the records mods change: about 2 minutes and about 9 GB at its peak for
   148 plugins.
7. Known gaps are in the README ("Known gaps" and "Traps").
8. `triage` is a first pass, not a review: it can't see what a DLL looks up while it runs, what a script does with
   values it computes, or whether a record nothing uses is dead weight. Don't report a "bug" or "malware" to a mod's
   author from a triage report alone. Files behind a link (symlink or junction) in a mod folder or beside a plugin
   are never read; the report lists each one as skipped, with one note in Files that anything inside wasn't
   checked. A missing-script LOOK (Plugins) says how many linked items were skipped, since one may hold the script,
   and so does the Scripts note when it finds no .pex at all: check what the link points to before calling a script
   missing. On Windows, the plugin
   named on the command line is read even when it is a link; on Linux and macOS it isn't. Other reparse points
   (OneDrive placeholders) are read as normal files. An archive with more than 200,000 entries is refused (exit 2).
