# sfmodcheck: instructions for AI assistants

These steps are for an AI assistant (or anyone) helping a Starfield player or mod author use sfmodcheck. Every command
here was run once on sfmodcheck 0.5.3 before release. The [README](README.md) has the full description.

## 1. What it is for

sfmodcheck reads one Starfield plugin (`.esm`) and reports problems that would break it in the game: damaged files,
bad headers, FormIDs outside the plugin's range, links to records that don't exist, broken conditions and scenes,
repeated EditorIDs, deleted records and "identical to master" (ITM) records. It is a command-line cousin of part of
xEdit's "Check for Errors".

Use it when someone has made or changed a mod and wants to know whether the plugin is sound before they play it or
release it, or when a mod misbehaves and you want to rule out a broken plugin.

Its second command, `sfmodcheck conflict`, reads a whole load order (a Plugins.txt) and reports the changes it throws
away: official DLC and update changes undone by mods, one mod's changes lost to another, ITMs, load-order problems and
overrides that change a record's type (the game then keeps only the record that loads first), each with a recommendation (a safe load-order move, an update to look for, or a small local patch, with a prompt for
building it). Use it when mods may be fighting over the same records, or after adding mods to a load order.

Don't use either to fix a plugin or a load order (they never change files), or `check` on Bethesda's own masters
(Starfield.esm needs over 30 GB of memory).

## 2. Prerequisites

1. Windows 10 or 11 (x64) for the stand-alone exe: nothing else needed, .NET is built in.
2. Or, for the dotnet tool on Windows or Linux: the .NET SDK 9.0 or later (`dotnet --version` prints 9.x or higher).
3. Starfield installed, so its masters (Starfield.esm and the others in the game's `Data` folder) can be read.
   sfmodcheck only reads them.

## 3. Install

Pick one. Files come from https://github.com/taosecurity/sfmodcheck/releases (version 0.5.3 shown here).

1. **Stand-alone exe (Windows):** download `sfmodcheck-0.5.3-win-x64.zip` and its `.sha256` file, check the
   checksum, and unzip it to a folder of your choice:

       certutil -hashfile sfmodcheck-0.5.3-win-x64.zip SHA256
       mkdir C:\Tools\sfmodcheck
       tar -xf sfmodcheck-0.5.3-win-x64.zip -C C:\Tools\sfmodcheck

   Compare the printed hash with the one in `sfmodcheck-0.5.3-win-x64.zip.sha256`. The program is then
   `C:\Tools\sfmodcheck\sfmodcheck.exe`.

2. **dotnet tool (Windows or Linux):** download `sfmodcheck.0.5.3.nupkg` into a folder (here `C:\Downloads`), then
   either install it for the user:

       dotnet tool install --global sfmodcheck --add-source C:\Downloads --version 0.5.3

   or into a folder of its own (nothing global changes):

       dotnet tool install sfmodcheck --tool-path C:\Tools\sfmodcheck-tool --add-source C:\Downloads --version 0.5.3

   On Linux, `sha256sum -c sfmodcheck.0.5.3.nupkg.sha256` checks the download.

## 4. Prove the install works

    sfmodcheck --version

Its first line is `sfmodcheck 0.5.3 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sfmodcheck`
(two credit lines follow), and it exits with code 0. A `check` prints the same first line on stderr, unless
`--quiet` is given. (Use the full path, such as `C:\Tools\sfmodcheck\sfmodcheck.exe
--version`, when the folder isn't on PATH.)

## 5. Common tasks

1. Check a plugin with a Steam install in the usual place:

       sfmodcheck check "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data\MyMod.esm"

2. Game Pass, or a Steam library on another drive: say where the game's Data folder is.

       sfmodcheck check MyMod.esm --data "D:\XboxGames\Starfield\Content\Data"

3. An add-on built next to the main plugin it needs: look for masters in the build folder first.

       sfmodcheck check out\MyMod-Addon.esm --masters-dir out

4. Mod Organizer 2: the plugin lives in `mods\<ModName>\`. Point `--data` at the real game Data folder and
   `--masters-dir` at the folder of any mod whose plugin is a master:

       sfmodcheck check "C:\Modding\MO2\mods\MyMod\MyMod.esm" --data "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data" --masters-dir "C:\Modding\MO2\mods\MainMod"

5. Something went wrong, or the result looks odd: write a troubleshooting log.

       sfmodcheck check MyMod.esm --log sfmodcheck-log.txt

   `--verbose` prints the same log lines on the screen (stderr) instead.

6. Fewer lines: `--max 20` (at most 20 lines per check), `--no-hints` (no HINT lines), `--quiet` (no summary line).

7. Check a whole load order for conflicts (Steam, the game's own `%LOCALAPPDATA%\Starfield\Plugins.txt`), then open
   `sfmodcheck-conflict\report.html`:

       sfmodcheck conflict

8. Another load order or install: give the Plugins.txt, the Data folder and a report folder.

       sfmodcheck conflict --plugins "C:\Modding\MyProfile\plugins.txt" --data "D:\XboxGames\Starfield\Content\Data" --out conflict-report

9. Try a patch before installing it: put it in a folder of its own, add its line at the end of a copy of the
   Plugins.txt, and give the folder with `--extra-data` (its plugins win over the Data folder's).

       sfmodcheck conflict --plugins plugins-with-patch.txt --extra-data C:\MyBuild\out --out conflict-report-patched

10. A large load order on a PC with little memory: stop cleanly at a limit instead of slowing the PC.

        sfmodcheck conflict --max-memory 8 --log conflict-log.txt

    `--max-memory` takes a number of GB from 0.000001 to 1000000; anything else stops with exit code 2 and says so.

    If it stops at the limit (exit code 3, "RESULT: STOPPED (memory passed 8.0 GB ...)", no report written), the load
    order needs more memory than that: raise the number if the PC has it, or check a smaller load order. (The
    author's 148-plugin order peaks near 8.5 GB, so with 8 it sometimes finishes and sometimes stops.)
    `--quiet` leaves out the byline, progress and summary lines; `--verbose` prints the log on stderr.

## 6. Inputs and outputs

- **Input:** one plugin file per run, plus its masters, found in this order: `--masters-dir` folders (or the plugin's
  own folder when none is given), then the Data folder (`--data`, else Steam's usual
  `C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data`; with no `--data` and no game there, a plugin that
  sits in a folder holding Starfield.esm uses that folder).
- **Output (stdout):** a summary line; `ERROR [check] ...` lines (broken: fix before release); `WARNING [check] ...`
  lines (probably a mistake); `HINT [check] ...` lines (how to fix that check's findings in xEdit or the Creation Kit);
  and always one last line `RESULT: OK|FAILED|CANNOT CHECK ...`. Scripts should read only the exit code and the lines
  starting with `ERROR `, `WARNING ` and `RESULT: `.
- **stderr:** notes (very large plugin, no Starfield.esm found), error messages for bad arguments, and the
  `--verbose` log lines (`log: ...`).
- **Exit codes:** 0 = no errors (warnings allowed); 1 = errors; 2 = couldn't check (bad arguments, a missing file or
  folder, a file it couldn't open, or it ran out of memory: then no result is shown for an unfinished check).
- **Log:** only when asked: `--log <file>` writes it to that file (overwritten each run; a plugin or archive name is refused), `--verbose` to stderr. It
  holds the version, the arguments, the plugin, the Data folder and why it was chosen, where each master was found (or
  every place looked), the caches used or rebuilt, the time per stage, a count of findings per check, and on a crash
  the full error. It names folders on your PC: read it before posting it anywhere.
- **conflict input:** the active (`*`) lines of a Plugins.txt (`--plugins`, else the game's own
  `%LOCALAPPDATA%\Starfield\Plugins.txt`; with neither it stops with exit code 2), the plugins themselves from
  `--extra-data` and then the Data folder (`--data`, else Steam's usual one). Bethesda's own files load first.
- **conflict output:** a report folder (`--out`, default `sfmodcheck-conflict`) with `report.html` (to read),
  `report.md` and `conflicts.json` (for scripts: `lost`, `itms`, `loadOrderProblems`, `typeChanges`, `Errors`). On
  stdout a summary line and always one last line `RESULT: OK, <n> lost-change entries (...), <n> ITMs, <n> load-order
  problems (report: <path>; <seconds> s)` (with ", <n> overrides that change a record's type" before `(report:`
  when there are any), or `RESULT: CANNOT CHECK (...)` or `RESULT: STOPPED (...)`. Progress lines go to stderr.
- **conflict exit codes:** 0 = report written (whatever it found); 2 = couldn't run (running out of memory included:
  no report); 3 = stopped at `--max-memory` (no report).
- **Caches:** `%LOCALAPPDATA%\sfmodcheck\` on Windows, `~/.local/share/sfmodcheck/` on Linux (the masters' EditorIDs and
  the learned list of official scenes; rebuilt by themselves after a game update). The environment variable
  `SFMODCHECK_CACHE_DIR` moves them. Deleting the folder is safe.

## 7. Limits

1. It never changes, moves or deletes a plugin, the load order or anything in the game folder. It writes only its
   caches, the `--log` file and (conflict) the `--out` report folder.
2. It makes no network connections and uploads nothing.
3. `check` checks one plugin against its masters; `conflict` compares plugin records across a load order. Neither
   checks loose files, BA2 archives or scripts.
4. A patch `conflict` suggests holds other authors' data: it is for the player's own game, never for publishing.
   Mod Organizer 2 keeps mods out of the Data folder, so run `conflict` from MO2 (as an executable) or it won't see
   them (not yet tried).
5. A missing master stops the link checks (the RESULT line says "links not checked"): install the master or point
   `--data` / `--masters-dir` at it, then run again.
6. `check` isn't meant for Bethesda's own masters (Starfield.esm needs over 30 GB of memory and about 4 minutes).
   `conflict` loads them but only compares the records mods change: about 2 minutes and about 9 GB at its peak for
   148 plugins.
7. Known gaps are in the README ("Known gaps" and "Traps").
