# Changelog

## 0.16.0 (not released yet)

### What's new

- A new command, `find`, looks up one record in your load order and changes nothing. Give it an exact EditorID (letter case doesn't matter), a FormKey such as `01295A:Starfield.esm`, or an 8-digit FormID as the game's console shows it, such as `0001295A`.
- For each record it finds, it prints the EditorID, FormKey, record type and name, the plugin that defines the record, the plugin that wins it in the load order, the other plugins with a copy, and the winning copy's main fields (links as `EditorID [FormKey]`, lists as a count, at most 40 lines).
- `--type <SIG>` keeps only records of one type, such as `LCTN` or `MISC`. `--json` prints one JSON object instead of the text.
- The load order comes from `--plugins` (default: the game's own Plugins.txt), `--data` and `--extra-data`, the same options `conflict` reads, or from an MO2 profile with `--mo2` (below).
- A console FormID is read the way the game numbers plugins: full plugins `00` to `FC`, medium plugins `FD` and one more byte, light plugins `FE` and three more hex digits, from each plugin's header flags (a `.esl` file is always light).
- Exit codes: 0 found, 1 not found (with a `HINT:` line that says what was searched), 2 couldn't run. `find --help` exits 0.
- `conflict`, `patch` and `find` read a Mod Organizer 2 profile with `--mo2 <MO2 folder>` and `--profile <name>` (default: the profile selected in MO2). Before, they saw only the real Data folder, so under MO2 they missed every mod. The rules follow sf-load-order 0.2.2.
- With `--mo2`, the order comes from the profile's `loadorder.txt` (else its `plugins.txt`), and the on/off marks from `plugins.txt`. Plugins, archives and loose files are looked up in `--extra-data`, then MO2's Overwrite folder, then each enabled mod from the top of `modlist.txt` down, then Data. The Data folder is `--data`, else the `Data` folder in MO2's game path, else Steam's usual one.
- An enabled mod with no folder, or a profile with no `modlist.txt`, is a load-order problem in the report. `--mo2` with `--plugins`, or `--profile` without `--mo2`, stops with exit code 2.
- With `--mo2`, the patch report says to add the patch in MO2 with Create empty mod. `--out` may not be an MO2 mod or Overwrite folder.
- Not yet: archives listed only in a profile's own INI files, and `--mo2` for `check`, `triage` and `patch check`.
- The README and AGENTS.md now say the Creations check runs on the `creations-out` folder that sf-mod-build writes, and AGENTS.md's "last run" note names this version.

### Example

A made-up `FindBase.esm` defines the misc item `FB_Misc` ("Made-up Widget", value 5), and two made-up mods, `FindMedium.esm` and `FindLight.esm`, change its value. All three are in `Plugins.txt`:

    sf-mod-check find fb_misc --data Data --plugins Plugins.txt

What it prints on stdout (a real run, with the folder shortened):

    sf-mod-check 0.16.0 find: fb_misc in 3 plugins (Plugins.txt (...\example\Plugins.txt), 3 active entries)

    FB_Misc [000800:FindBase.esm] MISC "Made-up Widget"
      defined in: FindBase.esm
      winner: FindLight.esm
      also in: FindMedium.esm
      fields:
        Value: 9
    RESULT: FOUND 1 record (1 s)

The exit code is 0. A misspelled EditorID (`FB_Misk`) prints a `HINT:` line and `RESULT: NOT FOUND (no record with EditorID FB_Misk in 3 plugins)`, with exit code 1.

## 0.15.2 (2026-11-23)

### What's new

- `release` takes `--accept <rule>:<plugin>:<detail>` to accept one known finding, for a mod that ships a plugin with a known gap in someone else's work, such as a script another author's plugin names that ships nowhere: `--accept script-missing:OtherMod.esm:OtherMod:Toggle`.
- `--accept-file <file>` reads the same entries from a text file, one per line. A `#` starts a comment, and blank lines are skipped.
- An entry names one rule (`script-missing`, `model-missing`, `mesh-missing`, `texture-missing`, `material-missing`, `sound-missing` or `face-missing`), the plugin's file name, and the script's name or the missing file's path. Case doesn't matter, and wildcards aren't allowed.
- An accepted finding still prints, as an `ACCEPTED` line that names the entry and where it came from, and it no longer counts toward FAIL or the exit code. The RESULT line adds `, n accepted` when an entry accepted a finding.
- An entry that matches nothing is a WARNING (rule `accept`), so a stale entry shows. A malformed entry, or an `--accept-file` that can't be read, stops the check (exit 2) and says what is wrong.

### Example

A made-up `MyMod-1.0.0.zip` ships `OtherMod.esm`, whose author's records attach the scripts `OtherMod:Toggle` and `OtherModDoorTest`, and neither script ships anywhere. Without entries the check fails (exit 1) with two `PROBLEM  [script-missing]` lines. `known-gaps.txt` holds:

    # OtherMod.esm by another author names two scripts that ship nowhere
    script-missing:OtherMod.esm:OtherMod:Toggle
    script-missing:OtherMod.esm:OtherModDoorTest
    script-missing:OtherMod.esm:OtherMod:OldName   # left from an older version

    sf-mod-check release MyMod-1.0.0.zip --accept-file known-gaps.txt

What it prints (a real run, with the first line left out):

    sf-mod-check 0.15.2 release (basic profile): MyMod-1.0.0.zip (1 files, 1 plugins, 0 archives)
    WARNING  [accept] accept entry matched nothing: script-missing:OtherMod.esm:OtherMod:OldName (from known-gaps.txt line 4). Check the rule, the plugin's file name and the detail against the finding, or remove the entry.
    NOTE     [dmmdeps] DMMDeps wasn't given (--dmmdeps), so only sf-mod-check's own check of the files the plugins need ran: attached scripts, models, the .mesh geometry of the mod's own models, materials, textures, NPC face data and the sounds of the mod's own sound banks. Voice files were not checked.
    NOTE     [plugin-check] OtherMod.esm: OK, 0 errors, 0 warnings (light master, 0 masters, 1 records, 0 overrides (0 ITM), 0 links)
    NOTE     [mutagen-gap] OtherMod.esm: 1 own records, 0 overrides, 0 compared with their master's copy
    ACCEPTED [script-missing] OtherMod.esm attaches script OtherMod:Toggle, but scripts\othermod\toggle.pex is in none of MyMod-1.0.0.zip's loose files or archives, a --masters-dir mod's archives or the game's archives (used by RN_Misc [MISC:000800:OtherMod.esm]). Put the compiled script in the zip or its archive, or give --masters-dir the folder of the mod it comes from. (was a PROBLEM; accepted by known-gaps.txt line 2 script-missing:OtherMod.esm:OtherMod:Toggle)
    ACCEPTED [script-missing] OtherMod.esm attaches script OtherModDoorTest, but scripts\othermoddoortest.pex is in none of MyMod-1.0.0.zip's loose files or archives, a --masters-dir mod's archives or the game's archives (used by RN_Misc [MISC:000800:OtherMod.esm]). Put the compiled script in the zip or its archive, or give --masters-dir the folder of the mod it comes from. (was a PROBLEM; accepted by known-gaps.txt line 3 script-missing:OtherMod.esm:OtherModDoorTest)
    RESULT: OK (0 problems, 0 to review, 1 warnings, 2 accepted)

The exit code is 0. The third entry matched nothing, so it is a WARNING.

### Changed

- `--version` says 0.15.2.

## 0.15.1 (2026-11-23)

### What's new

- The `release` rule `archive-name` now warns about a plugin's archive that the game doesn't load by itself. By itself the game opens only the parts `Main` and `Textures` after `<plugin> - `, each also numbered `01` to `99` (it stops at the first missing number), and the voice archive of each language, such as `Voices_en`.
- An archive such as `MyMod - Sounds.ba2` is a WARNING: the part isn't `Main`, `Textures` or a voice archive.
- A numbered archive after a gap is a WARNING: with `MyMod - Main01.ba2` and `MyMod - Main03.ba2` but no `MyMod - Main02.ba2`, the game loads `Main01` only. The numbers start at `01` even when the unnumbered archive is there, so `MyMod - Textures02.ba2` without `Textures01` is a WARNING too.
- The suggested new name is never an archive that is already there: `MyMod - Main.ba2` when it is free, else the first free `MyMod - MainNN.ba2` (for a patch's archive named after another mod, that mod's archives in the Data folder and the `--masters-dir` folders count too). An Xbox archive keeps its suffix (`MyMod - Sounds_xbox.ba2` is told `MyMod - Main_xbox.ba2`), and two archives of one zip are never told the same name.
- These are WARNINGs, not failures, because an INI archive list (for example `sResourceIndexFileList` in the `[Archive]` section) can load such an archive on purpose.
- A language's voice archive (`MyMod - Voices_en.ba2`, `MyMod - Voices_de.ba2`), named the way the official add-ons ship theirs, passes too.
- The Xbox twins (`MyMod - Main_xbox.ba2`) still pass, and the 0.14.0 PROBLEM and WARNING cases are unchanged.
- With both `Foo.esm` and `Foo - Patch.esm` in the zips, `Foo - Patch - Main.ba2` counts as the archive of `Foo - Patch.esm`.

### Example

A made-up `MyMod-0.1.0.zip` holds `MyMod.esm`, `MyMod - Main.ba2`, `MyMod - Main01.ba2`, `MyMod - Main03.ba2` and `MyMod - Sounds.ba2`:

    sf-mod-check release MyMod-0.1.0.zip

What it prints (a real run, with the first line left out):

    sf-mod-check 0.15.1 release (basic profile): MyMod-0.1.0.zip (5 files, 1 plugins, 4 archives)
    WARNING  [archive-name] MyMod-0.1.0.zip: archive MyMod - Sounds.ba2: the game loads a plugin's archives by itself only when the part after " - " is Main or Textures (optionally numbered 01 to 99) or a voice archive such as Voices_en, so it doesn't load this one by itself. An INI archive list (for example sResourceIndexFileList in the [Archive] section) can still load it on purpose; if that is the plan, ignore this warning. Otherwise rename it, for example to MyMod - Main02.ba2.
    WARNING  [archive-name] MyMod-0.1.0.zip: archive MyMod - Main03.ba2: the game loads numbered archives from 01 upward and stops at the first missing number, and there is no MyMod - Main02.ba2, so the game doesn't load this archive by itself. An INI archive list (for example sResourceIndexFileList in the [Archive] section) can still load it on purpose; if that is the plan, ignore this warning. Otherwise number the archives 01, 02 and so on without gaps.
    NOTE     [dmmdeps] DMMDeps wasn't given (--dmmdeps), so only sf-mod-check's own check of the files the plugins need ran: attached scripts, models, the .mesh geometry of the mod's own models, materials, textures, NPC face data and the sounds of the mod's own sound banks. Voice files were not checked.
    NOTE     [plugin-check] MyMod.esm: OK, 0 errors, 0 warnings (full master, 0 masters, 0 records, 0 overrides (0 ITM), 0 links)
    NOTE     [mutagen-gap] MyMod.esm: 0 own records, 0 overrides, 0 compared with their master's copy
    RESULT: OK (0 problems, 0 to review, 2 warnings)

The exit code is 0, since WARNINGs don't fail a release. 0.15.0 printed neither WARNING.

### Changed

- `--version` says 0.15.1.

## 0.15.0 (2026-11-23)

### What's new

- A new command, `sf-mod-check gap <plugin>`, runs the Mutagen gap check (the `release` command's `mutagen-gap` rule) on one plugin without a zip. It prints the same lines as before and then a RESULT line: `RESULT: OK (0 errors, n warnings)`, `RESULT: FAIL (n errors, n warnings)` or `RESULT: CANNOT CHECK (...)`. Exit codes are 0 for no errors, 1 for errors and 2 when it can't check, such as a missing master.
- Masters are looked for in the plugin's own folder, then each `--masters-dir` in order, then `--data`. The other options are `--quiet`, `--verbose` and `--log <file>`.
- `release --tested <zip|ba2>` compares the release with the build you tested in the game (rule `tested`). Compiled scripts are compared by meaning, leaving out what changes on every build (the string table's order, the order of variables, properties, functions and locals, temporary variable names, the compile time, the user and machine). Plugins are compared byte for byte. A difference is a REVIEW line; a script that can't be read is a PROBLEM, and a `--tested` zip or `.ba2` that can't be read is one PROBLEM and nothing is compared.
- `release --stage <folder>` checks the folder the release was built from against its archives (rule `stage`). A staged file in no archive and not loose in the zip is a PROBLEM, and a file in an archive that isn't staged is a REVIEW line.
- `release --psc <folder>` lints script sources that don't ship in the zip: the folder is linted instead of the zips' `Scripts\Source`, and a finding's path is shown relative to it. `release --psc-import <folder>` adds another mod's sources to the lint's imports, after the vanilla sources. Both can be given more than once and need `--papyrus` and `--ck-sources`; without them one NOTE says the folders were not used. A folder that doesn't exist stops the check with exit 2.
- `release <zip|folder|ba2|plugin>...` takes a stage folder, a `.ba2` or a plugin as well as zips, so you can check a build before you zip it. A folder is read where it is and nothing is written into it. The loose `.ba2` and plugin files of one run are checked together as one package named `loose files`. The zip-only rules (`one-plugin`, `root-md`) skip a folder or the loose files, and one NOTE (rule `not-a-zip`) says so. A folder inside the game folder is refused (exit 2), as is a run with two loose files of the same name.
- `patch` also writes `patch-sources.json` beside the patch: what the patch was built from. It holds the patch's size and SHA-256, the load order by file name (never paths), each master's position, size, date and SHA-256, and for each patched record the plugins whose copies it used, each with a 16-digit hash of that copy. A plugin saved again without changes keeps its hashes, and any changed field changes them, so a later check can tell which patched records a mod update really changed. With nothing to patch, an old `patch-sources.json` is removed with the old patch.
- A new command, `sf-mod-check patch check <patch.esm>`, tells you whether a conflict patch is still up to date. It reads the `patch-sources.json` beside the patch and the load order as it is now, and changes nothing. It reports a master that is gone or off (PROBLEM `[removed]`), a master or plugin that now loads after the patch (`[moved]`; official BlueprintShips files always load last and are never reported), a master whose update changed a patched record (`[updated]`; a NOTE when the update left those records alone), a plugin that now changes a patched record (`[new-editor]`), another spelling of the patch that is active too (`[duplicate]`), a patch file that isn't the manifest's build (`[patch-changed]`) and a patch that isn't active (NOTE `[patch-off]`). It ends with `RESULT: UP TO DATE`, `RESULT: REBUILD (n reasons)` or `RESULT: CANNOT CHECK (...)`, exits 0, 1 or 2, and writes the findings as JSON with `--json <file>`. `patch --help` names it.
- `release --profile creations --size-total` counts every input of the run as one Creation for the 2 GB rule (`creations-size`), for a Creation you build from several parts. Without it, each input is its own Creation, as before. `--size-total` without `--profile creations` is a bad argument (exit 2).
- The `conflict` report's pair tables (report.md and report.html) have a new **Lost fields** column: the fields a mod loses to an official `BlueprintShips-*.esm` file, which no load order or patch brings back. Before, those fields were counted in no column. In report.html such a pair's heading also gets an "n lost" badge. conflicts.json is unchanged, so sf-load-order reads it as before.
- The `assets` check (in `check`, `conflict` and `release`) now looks for the `.mesh` geometry files that a mod's own `.nif` files name, each a WARNING when missing, because that part of the model doesn't show in the game. The names come from the `.nif` file's shapes, and both the game's short form (`<folder>\<file>`) and a full `geometries\...\<name>.mesh` path are understood. A shape whose geometry is inside the `.nif` names no file. A `.nif` whose shapes can't be read is one WARNING (a NOTE in `release`) naming the file.
- `release` has a new rule, `mesh-missing` (WARNING). It runs with `--dmmdeps` too, since DMMDeps doesn't follow the geometry a `.nif` names. The NOTE shown when `--dmmdeps` isn't given now lists the `.mesh` geometry among what was checked.
- `conflict`: a missing mesh counts in the RESULT line and the report's summary ("1 mesh"), and its action says "parts of those models don't show". conflicts.json lists it under `missingFiles` with `kind` `mesh`; `missingAssets` keeps only scripts and models, so sf-load-order counts as before. The report's "What this report does not check" line no longer lists the mods' own `.mesh` geometry.
- `triage` reports the `release` rule `file-name` too, as LOOKs in its Files section: a file name, loose or inside one of the mod's archives, with a character outside plain ASCII, and a loose path inside Data over 200 characters. The same files are skipped as in `release` (the `fomod` folder, and files at the top of Data other than plugins, archives and INI files). Up to 5 are named, then one LOOK counts the rest; `--verbose` logs each one.
- `release` rule `face-missing` has a second NOTE: an override that changes another plugin's NPC face, while every baked face file found comes from outside the zips (the defining plugin's own), shows the old face in the game, so the NOTE says the baked face is stale and how to bake it again.

### Example

Check a made-up release against the build you tested and the folder you built it from. `MyMod-0.1.0.zip` is the build you tested in the game. `MyMod-0.1.1.zip` is the release: its `MyMod.esm` was saved again after the test, and its `MyMod - Main.ba2` holds `misc\mymod\notes.txt`. The `Stage` folder holds that file and `misc\mymod\ranks.txt`, which was left out of the archive:

    sf-mod-check release MyMod-0.1.1.zip --tested MyMod-0.1.0.zip --stage Stage

What it prints (a real run, with the first line left out):

    sf-mod-check 0.15.0 release (basic profile): MyMod-0.1.1.zip (2 files, 1 plugins, 1 archives)
    PROBLEM  [stage] staged, missing from the archives: misc\mymod\ranks.txt (the game won't see it; pack it again, or ship it loose in the zip)
    REVIEW   [tested] plugin differs from the tested build (expected only if its records changed): MyMod.esm
    NOTE     [dmmdeps] DMMDeps wasn't given (--dmmdeps), so only sf-mod-check's own check of the files the plugins need ran: attached scripts, models, the .mesh geometry of the mod's own models, materials, textures, NPC face data and the sounds of the mod's own sound banks. Voice files were not checked.
    NOTE     [plugin-check] MyMod.esm: OK, 0 errors, 0 warnings (full master, 0 masters, 0 records, 0 overrides (0 ITM), 0 links)
    NOTE     [mutagen-gap] MyMod.esm: 0 own records, 0 overrides, 0 compared with their master's copy
    RESULT: FAIL (1 problems, 1 to review, 0 warnings)

The exit code is 1. 0.14.0 had neither option, and on the release zip alone it said OK.

### Changed

- The README's opening list now names all seven commands and what `release` checks, and its `release` section has a worked example of the `archive-name` and `file-name` rules.
- `--version` says 0.15.0.

## 0.14.0 (2026-11-23)

### Added

- The `assets` check (in `check` and `conflict`) looks for materials and textures, each a WARNING when missing.
- It reads the materials a record names (a texture set's material and a few other fields) and the textures it names (a perk icon file, a lens flare sprite, a particle texture).
- It reads the material names in the plugin's own `.nif` files, and the textures and parent materials in its own `.mat` files.
- "Its own" means a loose file that a record, `.nif` or `.mat` of the plugin names, or any `.nif` or `.mat` in an archive named after the plugin. Other mods' and the game's files are looked up by name only.
- A material also counts when a material database holds it: any `.cdb` file in a loaded archive or loose under `materials\`. The game's (`materials\materialsbeta.cdb`) holds all of its own materials, and DLC and mods ship their own (`materials\creations\<plugin>\materialsbeta.cdb`).
- `--verbose` logs each database read with its count of material IDs.
- LOD meshes (`meshes\lod\`) in a plugin's own archive are read only when a record names one, since LOD isn't checked.
- If no database can be read, one WARNING says how many materials couldn't be checked, and none of them is called missing.
- A `.nif` or `.mat` that can't be read is one WARNING naming the file, never a crash.
- Texture archives (`<plugin> - Textures.ba2`) are read for their texture names. Before, only their type was read, for the archive count.
- `release` has two new rules, `material-missing` and `texture-missing` (WARNING). They are left out with `--dmmdeps`, which reports these files itself.
- The `assets` check reads each Wwise sound bank (`.bnk`) in an archive named after the plugin. Every sound the bank streams must be a `sound\soundbanks\<id>.wem` file the game can find, else a WARNING.
- Sounds held inside the bank need no file. Only bank version 140, the game's own, is read; a bank of another version, or one that can't be read, is one WARNING naming the file.
- `release` has a new rule, `sound-missing` (WARNING), which also reads the zip's loose sound banks. It runs with `--dmmdeps` too, since DMMDeps doesn't read sound banks.
- `release` has a new rule, `face-missing` (NOTE). An NPC a plugin adds with its own face, or an override that changes an NPC's face, should ship its baked face files: a `facegeom` `.nif` and four `facecustomization` textures, named after the defining plugin and the NPC's local ID.
- It is a NOTE only when none of the five files is there, because some of Bethesda's own NPCs have none or only part of them. NPCs whose template gives their Traits aren't looked at. It runs with `--dmmdeps` too, since DMMDeps can't see face files.
- `check` and `conflict` don't look for face data.
- `release` has a new rule, `archive-name` (PROBLEM). A `.ba2` that isn't named `<plugin> - <part>.ba2` beside a plugin in the zips, the Data folder or a `--masters-dir` folder never loads. The Xbox twin `<plugin> - <part>_xbox.ba2` passes.
- It is a WARNING instead in a zip that holds no plugin, or when an INI in the zip lists the archive.
- `release` has a new rule, `file-name` (WARNING): a file or a name inside an archive with a non-ASCII character, or a loose path inside Data over 200 characters (Windows' 260-character limit under Steam's usual Data folder). The zip's `fomod` folder is skipped, and so are the files at the top of Data other than plugins, archives and INI files (a README or an image there). Files in any other folder are checked. Up to 5 are named per zip, then one line counts the rest.
- `conflict`: the RESULT line and the report's summary add textures, materials and sounds to the missing-asset count when there are some. conflicts.json lists these in a new top-level key, `missingFiles` (the same item keys as `missingAssets`; `kind` is `material`, `texture` or `sound`). `missingAssets` keeps only scripts and models, so tools that read it, such as sf-load-order 0.1.3, count as before.

### Changed

- A texture link with no `.dds` or `.png` ending is a name, not a file, so it isn't looked up. Perks name their icon that way.
- `release`'s NOTE when `--dmmdeps` isn't given, and the conflict report's "What this report does not check" line, now name what the assets check covers.
- `conflict`: the summary row "Missing scripts and models" in report.md is now "Missing assets", like the HTML report's card and section.
- `conflict`: a missing-asset action names each kind and what it does in the game, for example "2 missing models and 1 missing material; those records show an empty or error mesh and those surfaces look wrong".
- The `WARNING assets` line of a check that stopped, and `conflict`'s matching "Tool limits hit" line, now name materials, textures and sounds as well as scripts and models. So do `conflict`'s progress line on stderr ("Checked scripts, models, materials, textures and sounds: ...") and the README's `--master-archive` text.
- `--version` says 0.14.0.

### Example

Check a made-up mod zip before release. `MyMod-0.1.0.zip` holds `MyMod.esm`, `MyMod - Main.ba2` with one model, `meshes\mymod\crate.nif`, whose material the mod doesn't ship, and a second archive named `MyMod_Extra.ba2`:

    sf-mod-check release MyMod-0.1.0.zip

What it prints (a real run, with the first line left out):

    sf-mod-check 0.14.0 release (basic profile): MyMod-0.1.0.zip (3 files, 1 plugins, 2 archives)
    PROBLEM  [archive-name] MyMod-0.1.0.zip: archive MyMod_Extra.ba2: its name has no " - " part, so the game never loads it. The game loads an archive only when it is named "<plugin> - <part>.ba2" (with spaces around the dash) beside a plugin of that name, or when an INI lists it; rename it, for example MyMod - Main.ba2 for MyMod.esm
    WARNING  [material-missing] MyMod.esm: the material materials\mymod\crate.mat is in none of MyMod-0.1.0.zip's loose files or archives, a --masters-dir mod's archives or the game's archives, and isn't in any of their material databases (used by meshes\mymod\crate.nif). Put the file in the zip or its archive, or fix the path in the file that names it.
    NOTE     [dmmdeps] DMMDeps wasn't given (--dmmdeps), so only sf-mod-check's own check of the files the plugins need ran: attached scripts, models, materials, textures, NPC face data and the sounds of the mod's own sound banks. Voice files and .mesh geometry were not checked.
    NOTE     [plugin-check] MyMod.esm: OK, 0 errors, 0 warnings (full master, 0 masters, 0 records, 0 overrides (0 ITM), 0 links)
    NOTE     [mutagen-gap] MyMod.esm: 0 own records, 0 overrides, 0 compared with their master's copy
    RESULT: FAIL (1 problems, 0 to review, 1 warnings)

The exit code is 1. With 0.13.0 the same zip passed with no warnings, although the game never loads `MyMod_Extra.ba2` and the crate has no material.

## 0.13.0 (2026-11-23)

### Changed

- `--help`, `-h` and `<command> --help` print the help on stdout and exit 0, with no RESULT line, for every command.
- Before, they exited 2, and `check`, `conflict` and `patch` added a `RESULT: CANNOT ...` line.
- A bad argument still exits 2 with its RESULT line, also when `--help` comes after it.
- `--version` says 0.13.0.
- `diff --json`: the README, AGENTS.md and the help now say that the RESULT line follows the JSON on stdout. For a file with only the JSON, add `--out <file>`. The output itself is unchanged.
- One rule for names from a plugin, script or zip, everywhere: control, line-break and invisible format characters print as `\n`, `\t` or `\uXXXX`.
- `conflict`, `patch`, `diff`, the reports and conflicts.json used to turn those characters into spaces; they now show the escape, as `release --profile creations` did.
- `release` escapes them in every step's findings, the header line and the RESULT line, not only in the creations step.
- The RESULT line of every command escapes them too when it quotes an error, so it stays one line.
- The `--log` and `--verbose` lines escape them too, and the later lines of one multi-line entry (a stack trace) are indented by two spaces.
- Ordinary names, accented letters and dashes print as before.
- `conflict` rates a file name stored as raw bytes (a city-map component's file) that differs only in letter case as "No effect", as it does a text field. `patch` leaves such a change alone.
- Raw bytes count as a file name only when they hold printable text of three characters or more with a dot or a path separator, and only letters in that text may differ.

### Added

- `patch` builds cells and worldspaces: the record itself, without a cell's placed objects and navmeshes or a worldspace's cells.
- An exterior cell brings its worldspace into the patch as the game uses it today, with no field changed; the report says so.
- A patched worldspace never carries RNAM, OFST or CLSZ, as Creation Kit overrides don't.
- A cell's traversals are taken whole from one plugin, never merged entry by entry.
- `patch` builds placed objects (REFR, ACHR and the other placed types), each in its cell's persistent or temporary list.
- A placed object brings its cell into the patch as the game uses it today, with no field changed and none of its other objects.
- `patch` builds navmeshes, each in its cell, with the cell brought in as the game uses it today when needed.
- A navmesh's mesh (geometry, NNAM, pre-cut entries) comes whole from one plugin, never field by field.
- When two plugins each changed a different part of one navmesh's mesh, today's mesh is kept and listed under choices.
- The `--pick` line printed for such a navmesh takes that plugin's whole mesh when you run it.
- A field `--pick` on any mesh field takes that plugin's whole mesh, with one report row saying the other plugins' mesh changes are dropped.
- A mesh `--pick` that names today's mesh and drops no change adds no row; one on a partial copy lacking part of the mesh isn't used, and says why.
- Field picks on one navmesh's mesh that name two plugins stop the run with exit 2 and an error naming both.
- A whole-record `--pick` takes a navmesh's mesh only when the mesh is one of its Choice fields and no field pick names the mesh.
- A field `--pick` (`<FormKey>:<Field>=<plugin>`) now works on any field, not only on Choice fields.
- It keeps that plugin's value where the patch would forward another mod's change; the report names the change it drops.
- A field `--pick` on a record with no lost change adds that record, as the game uses it today with the one field picked.
- Example: keep a worldspace's water empty when a mod filled it, with `--pick 0012AB:Starfield.esm:Water=Starfield.esm`.
- A pick that can't be used says why: no such field, the plugin doesn't hold the record, no mod overrides it, or the record isn't built (and why not).
- Of two field picks on one field, the later one decides, and the earlier one gets a "was not used" warning.
- In a merged component list, a component's kind counts as its identity only while no version holds two of that kind.
- With `--include-official`, cells, worldspaces, placed objects and navmeshes one official plugin loses to another are restored too.
- `--verbose` logs where each patched cell, placed object and navmesh goes, and each cell or worldspace added as a parent.
- `--verbose` also logs each placed object and navmesh left out because its cell's worldspace couldn't be read again.
- A parent cell or worldspace that can't be built leaves the patch with its objects, each listed under "Not built" with the reason; the rest is still written.
- A `--pick` on an object that leaves this way gets a "was not used" warning with that reason, and its report row goes.
- `patch --verbose` logs one line for every record it decides on: built (with the fields forwarded and the lists merged), left out as equal to today's, or not built and why.
- Dialogue and scenes are still listed under "Not built".

### Fixed

- `release` and `triage`: a bad argument (an unknown option, a missing or wrong value, an extra input or no input) now ends with a RESULT line, as the other commands do.
- That line is `RESULT: CANNOT CHECK (bad arguments; see the message above)` for `release` and `RESULT: NO REPORT (bad arguments; see the message above)` for `triage`. The exit code stays 2.
- `diff`: a condition (CTDA) whose only difference is its padding bytes is now noise, as the README said. Before, the record counted as changed (field `Conditions`) and `diff` exited 1.
- The padding left out is the 3 bytes after a condition's operator and the 2 bytes after its function index.
- A condition's parameters, the third one included, are still compared. `check` and `conflict` are unchanged.
- `release --profile creations`: Bethesda's own masters are now matched by exact name shape (`SFBGS` plus three hex digits, `sfta` plus two digits, and their `BlueprintShips-` files), as `check` does.
- Before, the `SFBGS*` and `SFTA*` patterns also let a mod such as `SFBGS003Fixes.esm` pass as official.
- The `[official]` patterns in `data\creations-masters.txt` now read `?` and `[...]` classes as Python's fnmatch does.
- Classes are read in lower case, as fnmatch on Windows does, so `[A-z]` means a to z, and `/` and `\` count as one character.
- One difference from fnmatch, on purpose: a name with a newline never matches, not even through `*`, `?` or `[!x]`.

### Example

Build a patch for a made-up load order in which two mods change different fields of one exterior cell, and a third mod changes only the cell's worldspace (the tests build these files):

    sf-mod-check patch --data Data --plugins Plugins.txt --out patch

What it prints (a real run, with the first line left out):

    Loaded 4 plugins (0 load-order problems) in 0 s
    5 records overridden by mods
    RESULT: OK, patch ...\patch\ConflictPatch.esm: 2 records, 1 fields forwarded, 0 picks used, 0 shared lists merged, 0 choices kept the winner's value, 0 records not built, 4 masters (report: ...\patch\patch-report.md; 1 s)

The report's list of records in the patch:

    - Cell B_Ext [000807:Base.esm]
      - WaterHeight from ModA.esm: changes water height from not set to 7
    - Worldspace B_World [000804:Base.esm]
      - the parent of patched cells, as the game uses it today (no field changed)

The worldspace keeps the third mod's change, and it goes in without RNAM, OFST and CLSZ. On the same files, 0.12.0 listed the cell under "Not built", so the change from `ModA.esm` stayed lost.

## 0.12.0 (2026-11-21)

### Changed

- `conflict`, `patch` and `triage` load the official `BlueprintShips-*.esm` files (Bethesda's ship blueprints) where the game does: after every plugin, oldest file first (by the file's modified time, then by name). Earlier versions loaded them with Bethesda's other files, before every mod, so a mod's change to a ship that a BlueprintShips file also changes was shown as winning when the game undoes it. A Plugins.txt line for a BlueprintShips file doesn't move it, as in the game.
- A mod that has a BlueprintShips file as a master gets a load-order problem ("the game loads BlueprintShips files after every mod, so this plugin loads before its master"). Its records are judged in the game's order; the master isn't moved forward for it. What it changes is judged against that master's version, which its author saw, so a change it makes to a base game ship the BlueprintShips file also changes is listed as lost, and a copy of the BlueprintShips file's values is an ITM. `triage` reports that line as a load-order note, not as "left out of the comparison".
- A BlueprintShips file whose master is a BlueprintShips file that loads after it keeps the BlueprintShips order (oldest first, then by name) and gets a load-order problem that says so. Problems about a plugin that still loads are added only once it has opened, so a plugin skipped for a missing master gets only the "plugin skipped" line.
- `patch`: when a copied value points at a record a BlueprintShips file defines, the patch gets that file as a master, and the report's Notes say that the game still loads it after the patch.
- `--verbose` and `--log` record where each BlueprintShips file went and why, and each one left out because its plugin is turned off.
- `conflict`: a mod's change lost to a BlueprintShips file gets its own recommendation: the file loads after every mod, so no load order or patch brings the change back, and the mod's edit to that ship doesn't reach the game (the advice names the record type when it isn't a ship). No move or patch is offered for it.
- `conflict`: a mod's edit of a record that a BlueprintShips file adds itself (the mod has that file as a master) is now listed as lost to that file, judged against the file's own version. Before, the mod's copy loaded first and was taken as the original, so the lost edit wasn't listed.
- `patch`: a record whose change is lost to a BlueprintShips file isn't built, because the patch loads before that file too. It is listed under "Not built" with the reason and counted in the RESULT line's records not built. The "Not built" heading no longer says every entry can be done in xEdit.
- `conflict`: a field lost to a BlueprintShips file is labeled **Lost** in the report tables (not Choice or Forwardable), and its summary line says there is nothing to do, instead of calling it a choice. The Forwardable and Choice counts leave such fields out.
- The navmesh info map (NAVI) keeps its own handling when a BlueprintShips file has its last copy: each plugin's copy lists only its own navmeshes, so `conflict` gives it the navmesh info map's note (not patched, don't copy it by hand) instead of the "doesn't reach the game" advice, and `patch` gives it the navmesh info map's reason.
- The RESULT line keeps its shape. Its "official" count is still the official changes undone by mods, so a mod's change lost to a BlueprintShips file is counted with the "mod vs mod" entries.
- `--version` says 0.12.0.

### Example

Check a made-up load order in which a mod changes a ship that `BlueprintShips-Starfield.esm` also changes (the tests build these files; the Plugins.txt even lists the BlueprintShips file before the mod):

    sf-mod-check conflict --data Data --plugins Plugins.txt --out report

What it prints (a real run, with the first three lines left out):

    Loaded 4 plugins (0 load-order problems) in 0 s
    1 records overridden by mods
    Checked scripts and models: 0 missing, 0 archives read
    sf-mod-check 0.12.0 conflict: 4 plugins (3 official, 1 mods), 1 records overridden by mods
    RESULT: OK, 1 lost-change entries (0 official, 1 mod vs mod), 0 benign entries shown apart, 0 ITMs, 0 load-order problems (report: ...\report\report.html; 1 s)

The report loads `BlueprintShips-Starfield.esm` last, after `ModA.esm`, and gives this recommendation:

    Medium: BlueprintShips-Starfield.esm (base game ship blueprints) loads after every mod, so no load order or patch brings this change back; the edit ModA.esm makes to this ship doesn't reach the game.

The ship's changed field is labeled **Lost** in the report's table. On the same files, 0.11.0 loaded the BlueprintShips file first and reported the opposite: `ModA.esm` winning, and the official change undone (`1 official, 0 mod vs mod`).

## 0.11.0 (2026-11-21)

### Added

- A new command, `diff <old> <new>`, compares two versions of a plugin, or of a mod (a .zip, .7z or .rar download, or a folder). Plugins in two mods are paired by file name, and a plugin in only one of them is listed as added or removed. Its options are `--data`, `--json`, `--out`, `--quiet`, `--verbose` and `--log`. It exits with 0 for no differences, 1 for differences and 2 for bad input or setup.
- `diff` compares records by their fields, not only their bytes, and names the top-level fields that changed. Own records that kept their type and EditorID under a new FormID are listed as renumbered (old and new FormID). A record whose bytes differ but whose fields are all equal (padding bytes, field order, -0 and +0) is counted as noise, which is no difference: a version that differs only by noise exits 0. Scripts and script properties are matched by name, since the game finds them by name, so scripts or properties that only changed places are noise; a changed name, flag or value, or one added or removed, is a change. Names are compared exactly (a change of letter case alone is a change, although the game ignores case), and an array property's elements keep their order.
- `diff` has detail views: placed objects and the other cell records grouped by their cell (added, removed, changed and moved to another cell), leveled-list entries added and removed (level, reference, count), and base-object swaps. EditorIDs of master records come from the masters in the mod or the Data folder (`--data`).
- `diff` is read-only: `--out` and `--log` never write onto an input, a plugin, an archive or a script, or inside an input folder (symbolic links and junctions on the way are followed for this check, also for the inputs; a path through a mounted volume or a device path is refused, and so is an NTFS stream name such as `A.esm:r.txt` or `mod::$INDEX_ALLOCATION\r.txt`; a link whose target doesn't exist yet is followed too, and a path is refused when this account can't look inside a folder on the way, so a link there can't be seen; hard links and 8.3 short names aren't detected), and they must be different files. The log is checked before it is opened, so a bad `--log` never empties an input.
- The `diff` option `--all` lists everything in the text report, including each cell's records (long lists are cut short otherwise; the JSON always lists everything).
- `diff` compares the other files of two mods: loose files by SHA-256, each archive's files (archives paired by name, each file hashed as stored), and changed scripts (.pex, loose or in an archive) by their objects, states and functions, names only. Any file difference is a difference; the RESULT line counts the files added, removed and changed. JSON keys `files`, `archives` and `scripts`.
- `diff` with `--verbose` or `--log` records what it read, how it paired plugins, archives and loose files, what it left out and why, each header change, renumbered record, changed record and changed file, and the result. Errors name the file and say what to do. The README has an Example with real output, and AGENTS.md lists every JSON key.

### Example

Compare two versions of a mod (here two folders of a small made-up mod, built by the tests):

    sf-mod-check diff MyMod-1.0 MyMod-1.1

What it prints (a real run, with the loose-file, archive and script sections left out):

    sf-mod-check 0.11.0 diff: MyMod-1.0 -> MyMod-1.1
    ...
    == MyMod.esm ==
      old: full, 0 masters, 8 records (8 own)
      new: full, 0 masters, 9 records (9 own)
      header: HEDR record count: 16 -> 17 (information only)
      header: HEDR next object ID: 822 -> 823 (information only)
      own records: 2 added, 1 removed, 3 changed; 4 the same
    ...
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
    ...
    RESULT: DIFFERENCES (1 plugin compared, 1 changed, 0 added, 0 removed; records: 2 added, 1 removed, 3 changed, 0 renumbered, 0 noise; files: 1 added, 0 removed, 3 changed)

The full output and a JSON excerpt are in the README, section "Compare two versions of a mod".

## 0.10.1 (2026-11-21)

### Changed

- `release --profile creations`: a script that names an SFSE plugin's script and calls `Game.IsPluginInstalled` only for official plugins (Starfield, the DLC, `BlueprintShips-Starfield.esm`, `SFBGS*` and `SFTA*` plugins) is a PROBLEM again, not a WARNING. A check for an official plugin doesn't guard the framework, so the framework calls still fail on consoles. The PROBLEM names the plugins the script tests. A script that tests any other plugin, or a name held in a variable, is still a WARNING.

### Fixed

- `release --profile creations`: a name that ends with a newline (`"Starfield.esm\n"`) no longer matches an official plugin's name.
- The same fix applies to the official-name checks of `check` and `conflict` (ITM and load-order rules).
- `release --profile creations`: official names now match the same way on every PC language (a Turkish Windows didn't match `BLUEPRINTSHIPS-STARFIELD.ESM`).
- `release`: the plugin-name rule (names Starfield drops from Plugins.txt), the `BlueprintShips` master rule, the private-file rule (permission records, comparison notes) and the game-script-source rule also match the same way on every PC language. On a Turkish Windows, `MyMod-CONSTELLATION.ESM` and `PERMISSION.md` slipped through.
- `release --dmmdeps`: the game's own archives (`BLUEPRINTSHIPS-STARFIELD - Main.ba2`, `CONTENTRESOURCES.ZIP`) are also recognized the same way on every PC language.
- `release --dmmdeps`: game archives are now told apart by the same official names `check` uses. Archives of `BlueprintShips-SFBGS*` plugins and of `sfta10` and later now count as the game's own, and a mod archive such as `SFBGSExtras - Main.ba2` no longer does.
- `release --dmmdeps`: a game archive must now be a `.ba2` named `<official plugin> - <part>.ba2`, the same test `triage` uses. Mod archives such as `SFBGS003.Fixes - Main.ba2` or `Starfield.Extras - Main.ba2`, and files that aren't archives, no longer count as the game's own.
- `release --dmmdeps`: on Linux and macOS, an archive DMMDeps names with a Windows folder (`Data\Starfield - Textures01.ba2`) is now recognized as the game's or a master's archive, and spaces or a line break after the name no longer hide a master's archive. Findings name such an archive by its file name, and one archive written with and without a folder is counted once.
- `triage`: script text that ends with a newline (`"MyMod.esm\n"`) is no longer reported as a plugin name, and file names match the same way on every PC language.

### Example

Check a mod's release zip against the Creations rules:

    sf-mod-check release --profile creations MyMod-0.1.0.zip

What it prints (a real run on a test mod's zip, its file names changed to MyMod here, with the NOTE lines and the second warning left out; this mod has no SFSE calls, so its findings are the same as in 0.10.0):

    sf-mod-check 0.10.1 release (creations profile): MyMod-0.1.0.zip (5 files, 1 plugins, 1 archives)
    WARNING  [creations-xbox] MyMod-0.1.0.zip:MyMod - Main.ba2: no MyMod - Main_xbox.ba2, so Xbox gets no archive (the Creation Kit warns "Creation for XBox that has no archives"); build the Xbox archive with Archive2's Xbox settings.
    RESULT: OK (0 problems, 0 to review, 2 warnings)

A mod whose script calls ConsoleAPI after checking only `Game.IsPluginInstalled("ShatteredSpace.esm")` now gets a `creations-framework` PROBLEM that says its check tests only official plugins, which doesn't guard the framework.

## 0.10.0 (2026-11-21)

### What's new

This release adds three checks to `check` (Story Manager and Planet Content Manager node overrides, ship systems, and condition aliases and functions), a `creations` profile for `release`, and an opt-in `patch` option for changes one official plugin loses to another. `conflict` now uses xEdit's field conflict priorities by default, so benign differences are shown apart instead of counted as lost changes.

- `check` has a new check, `trees`. It gives one WARNING for each override of an official Story Manager node (SMBN, SMQN, SMEN) or Planet Content Manager node (PCMT, PCBN, PCCN). Every mod that edits the same official node conflicts with the others, so the warning says the conflict-free way: add your own node to the tree rather than edit an official one. Bethesda's own plugins aren't flagged.
- `conflict` names these records as Story Manager or Planet Content Manager nodes in its impact text, with the same advice.
- `check` warns about a condition whose function number sf-mod-check doesn't know. Known means in Mutagen's list (607 functions) or used by the official masters, which use a few numbers Mutagen lacks; those come from a new data file, `data\condition-functions-seen.txt`. Before, such a condition went unreported.
- `check` lists ships. For every ship a plugin adds or overrides, the log gets its modules by system, its class (from the reactor), its three weapon slots and the required systems it lacks (cockpit, reactor, grav drive, engine, fuel tank, landing gear, docker, landing bay). The normal output gets one `SHIPS:` count line, only for plugins with ships. A new check, `ships`, gives one WARNING for each ship the plugin adds that has a cockpit but lacks a required system, and names the missing systems. A ship's record doesn't say who flies it, so the warning says a ship only NPCs fly can ignore it. Overrides, one-piece hulls (like the Starborn ships) and Bethesda's own plugins aren't flagged, and a missing shield is listed only.
- `release` has a new profile, `--profile creations`, for Bethesda Creations on PC, Xbox and PlayStation. It runs the `basic` rules plus the Creations ones. It fails on INI, DLL, EXE, ASI or SFSE files, on compiled scripts that name a script from an SFSE plugin, on masters that aren't Bethesda's and aren't known to be on Creations, on texture archives with no `_xbox.ba2` twin, and on a zip of more than 2 GB. A script that names an SFSE plugin's script and also calls `Game.IsPluginInstalled` is a WARNING instead, since it may use the plugin only when it's there; the WARNING names the plugins the script tests. It warns about other archives with no twin, `.wem` or `.bnk` sound (PC-format audio is silent on consoles), and `Debug.Notification` or `Debug.MessageBox` calls the player would see, read from the compiled scripts.
- `--creations-master <plugin>` allows another author's master that you have seen on Creations. The built-in list is a new data file, `data\creations-masters.txt` (taken 2026-11-18).
- `patch` has a new option, `--include-official`, off by default. It also brings back changes one official plugin loses to another, such as a reference one Trackers Alliance mission adds to a location and a later one drops. Without it, `patch` builds exactly what 0.9.1 built. `conflict` still doesn't list such losses.
- `check` gives a script or model path with a part that ends in a dot or a space (`meshes\armor.\helm.nif`) its own text. Windows trims such a part when it opens a loose file, and an archive doesn't. When the trimmed path is a loose file, the finding is a WARNING saying the game may load it on Windows but not once it is packed into a `.ba2`; otherwise the path is missing as before, and the finding names the trimmed path. Before, such a path was always reported missing.
- `check` now sees the conditions inside a package's target data (75 in 14 official packages, mostly the creature `CCT_*` packages), so the empty-parameter and unknown-function rules cover them. Mutagen read them all along; sf-mod-check's condition walk didn't enter a package's data inputs. `data\condition-params-seen.txt` was relearned with them.
- `check` warns about a condition in a quest, a dialogue response or a package that names an alias number its quest doesn't have (the quest itself, the response's topic quest, or the package's owner quest). The official masters have 229 such conditions, so a use an official version of the same record already has isn't flagged. Before, only scenes were checked for this.
- `--verbose` and `--log` list each record that lacks a field every official record of its type has, from a new data file, `data\required-fields-seen.txt`. It is a listing, not a finding, because many working mods lack fields the Creation Kit always writes.
- `conflict` uses xEdit's field conflict priorities, on by default. A lost field xEdit rates benign (most Editor IDs, a location's Master reference lists, a few tree links) or ignores is no longer counted as a lost change: the reports list it apart under "Benign differences (not counted as lost)", and conflicts.json has a new `benign` array. A critical field (a Game Setting's name, the fields of a record injected into another plugin's FormIDs) is marked "Critical" and its record comes first. A location's Added and Removed reference lists stay normal, unlike in xEdit, because quest aliases read them. So do the Conditions of Story Manager and Planet Content Manager nodes, because they decide when a quest starts or what a planet gets. The table is a new data file, `data\field-priorities.txt`, made from xEdit 4.1.5r's Starfield definitions (MPL-2.0), with its generator in `data\gen\`.
- `conflict --no-priorities` counts every differing field as a lost change, as 0.9.1 did. `patch` and `triage` don't use the priorities.
- `--verbose` and `--log` record each tree-node override and each unknown condition function the checks met, and each Creations rule's reading (masters, archives, scripts).

### Example

Check a load order of 158 plugins for lost changes, with field priorities on (the default):

    sf-mod-check conflict --plugins C:\temp\loadorder.txt --out C:\temp\conflict

What it prints (a real run; the paths are shortened):

    sf-mod-check 0.10.0 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check
    sf-mod-check 0.10.0 conflict: 158 plugins (18 official, 140 mods), 2558 records overridden by mods
    RESULT: OK, 62 lost-change entries (27 official, 35 mod vs mod), 0 benign entries shown apart, 11 ITMs, 0 load-order problems, 53 missing assets (4 scripts, 49 models) (report: C:\temp\conflict\report.html; 129 s)

In this load order every lost field is one xEdit rates normal, so the counts match `--no-priorities`. When a lost field is benign, the RESULT line counts it under "benign entries shown apart", and when one is critical, it adds "(N critical lost-change entries)".

### Fixes

- Stack traces that Mutagen prints itself (`Unexpected header type: PAQO`, for scene actions it can't fully read) no longer land on stdout. They go to stderr, each line starting `mutagen: `, and to the `--log` file, so stdout holds only sf-mod-check's own lines.
- An archive name in an INI that starts with one `/` (`/Extra - Main.ba2`) means a file under Data, as one leading `\` already did. Before, it was looked up at the root of the drive.
- On Linux and macOS, a network name in an INI (`\\server\share\x.ba2`) is no longer looked up under Data. It has no Linux or macOS path sf-mod-check can know, so it is "listed but not found", as a Windows drive path already was.
- On Linux and macOS, when a folder under Data can't be listed, the log names it and says its loose files count as missing. Before, those files were missing with no reason given.
- Tests that only mean something on one kind of system are reported as skipped on the others, not as passed.
- Triage on Linux and macOS: a plugin named on the command line that is itself a symlink is now named as a link and counted in the skipped-link note. Before, it was called "reached only through a link" and the count said 0.
- Triage on Linux and macOS: two links whose names differ only in case keep their own spellings, so each gets its own LOOK. Before, both were shown under one lower-case name.
- Triage's first line says "1 file" for one file, not "1 files".
- `check` (and triage) needs about half the memory on mods that override cells and records inside them. The ITM step looked cells up in two ways, and Mutagen kept a large index of Starfield.esm's cells for each; now it uses one. On one large mod that overrides many cells, the peak fell from 4.25 GB to 2.35 GB, with the same findings.
- The `links` hint is shorter, and the README's long `conditions` and `ships` entries are split into short bullets. The advice is the same.
- README "Build from source" names the BuildKit version the lock files really pin (0.3.1), not 0.2.0.

### Not yet

- Asset checks (textures, materials, sounds, face data and the Xbox texture rule) wait on a test on an Xbox.
- Whether the game opens only exact `<plugin> - Main.ba2` names or searches the folder is still open, so the `<plugin> - *.ba2` rule stays as it was.
- Whether a lost change to a Story Manager or Planet Content Manager node's conditions should count as a normal lost change (xEdit rates it benign) is still being decided.
- A record that lacks a field every official record of its type has is listed in the log only, not reported as a finding.

## 0.9.1 (2026-11-20)

### What's new

This release fixes the small items left open after 0.8.0, mostly in `triage`. Nothing in `check`, `conflict` or `patch` gives different findings, and no option changed.

`triage` now says plainly when it skipped a file because the file is a link (a symlink or a junction), instead of reporting it as a file that couldn't be read. It also unpacks scripts stored with LZ4 compression inside a mod's archives, so those scripts are checked like any other.

### Example

Triage a mod folder that holds a `readme.txt` and a `Textures` folder that is a junction to another folder:

    sf-mod-check triage "C:\temp\Mods\SomeMod" --out C:\temp\Mods\reports

What it prints (a real run):

    sf-mod-check 0.9.1 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check
    sf-mod-check 0.9.1 triage: SomeMod (1 files; triage, not a verdict)
    LOOK [Files] textures: skipped: it is a link (symlink or junction), and triage never follows links, so what it points to isn't checked. Run triage on the folder it points to if you want that checked
    RESULT: REPORT WRITTEN (1 to look at, 4 notes) C:\temp\Mods\reports\SomeMod-triage.html

Two of the four notes in the report are about the junction. The Files section says "1 linked item was skipped and not read (named in this section); anything inside it wasn't checked. Run triage on what the link points to." The Scripts section says "The files triage read hold no compiled scripts (.pex); 1 linked item was skipped, see Files."

### Fixes

- `triage` and `release` refuse an archive that holds more than 200,000 entries, before they unpack anything. A crafted archive of millions of empty files used to take a very long time to unpack and clean up. The message says what to do: if it really is one mod, unpack it yourself and run `triage` on the folder.
- `triage` never follows a link beside a plugin. On Windows, a `.ba2` next to the plugin that was a link to another file used to be read; now it is skipped, as it already was in a folder. On Windows, the plugin you name yourself is still read even when it is a link (on Linux and macOS it isn't; see "Not yet").
- Only real links (symbolic links and junctions) are skipped. Windows also marks OneDrive Files On-Demand placeholders, deduplicated files and app aliases as reparse points; `triage` reads those as normal files and folders, so a mod kept in a OneDrive folder is checked in full.
- Every link `triage` skips is listed in the Files section as a LOOK that says it is a link. A file reached only through a link used to show as a file that couldn't be read, under a made-up path. Such a file now gets that one LOOK only, not a second "couldn't be read" from the Plugins or Scripts section.
- Skipped files (links, and a file whose name differs from another's only in case) are rows in the Files section's "Every file" table, marked "link" or "same name" in place of their size. A "?" there still means a file whose hash couldn't be read.
- On Linux, two files in a mod folder whose names differ only in case (`Scripts/a.pex` and `scripts/a.pex`) are no longer read as one file twice. One of them is checked (the all-lower-case one if there is one), and the Files section names both by their own spellings: the LOOK for the skipped file says which file was checked in its place, and the "Every file" table shows both. The same file listed twice under one spelling is not reported as two.
- When `triage` skipped at least one link (any file or folder), the Files section adds one note: how many linked items were skipped, that anything inside them wasn't checked, and that you can run `triage` on what the link points to. Files reached through a link are listed but don't count as links.
- A missing-script LOOK in the Plugins section keeps its full wording, so a script that is really missing is still reported as one that doesn't run. When links were skipped, it adds how many, since one of them may hold the script.
- When the Scripts section finds no compiled scripts and links were skipped, it says that the files `triage` read hold none and how many linked items were skipped, instead of saying the mod has none.
- The Scripts section of `triage` unpacks archive entries packed with LZ4. They used to be named in the report and skipped. An entry that unpacks to more bytes than its archive says is reported as a size mismatch with both sizes, not as damaged data.
- `check`, `release` and `conflict` find a lower-case `starfield.esm` on Linux, as `triage` already did, so they no longer print "no Starfield.esm" for such a Data folder.
- On Windows, `triage` and `release` count an error as a problem of the PC (a locked file, a full disk) only when it carries a real Windows error code. An unrelated error code that happened to share the last four hex digits used to count too; it only changed the wording of the message.
- `triage` checks once whether the game is in the Data folder, and the report header and the sections use that one answer. Before, a `Starfield.esm` removed during the run could make the two disagree.

### Checked and still fine

- The zip unpacker names a file and a folder with the same name (a zip holding both `Data` and `Data/b.txt`) and says which entry to rename. This was listed as open after 0.8.0 but had already been fixed in 0.8.0's review rounds; a new test proves it both ways round.
- report.md escapes Markdown characters in plugin names in the load-order problem list and in the ITM table, not only in the tables the 0.5.0 notes named. That has been true since 0.5.1; a new test proves it.
- When the Data folder holds a lower-case `starfield.esm` (possible on Linux), every later step finds it: each one either takes the name from the folder's own listing or matches names ignoring case. A new test proves it.

### Build

- The LZ4 package that the Scripts section uses is now pinned in the project file (K4os.Compression.LZ4 1.3.8, MIT), at the version the build already used through Mutagen. No other package changed.

### Documentation

- The README and AGENTS.md say exactly which links `triage` skips: links inside a mod folder and beside a plugin. On Windows, the plugin named on the command line is read even when it is a link.

- The README's "Known gaps" now says that Mutagen 0.54.4 writes 0 for a quest's `Unknown` value (the header of the quest's dialogue and scene group), and that the `mutagen-gap` rule doesn't report it.
- The README and AGENTS.md now say plainly what happens when you run `conflict` or `patch` outside Mod Organizer 2: every MO2 mod shows as "listed but not found in Data" and is left out. Running sf-mod-check from inside MO2 should work but hasn't been tried.

### Not yet

- The ITM step still loads whole cells, so `check` and `triage` on very large mods (city-sized) still peak at about 5 GB of memory.
- On Linux, only the Files section names a case twin by its own spelling. The other sections, and a LOOK for a file whose hash couldn't be read, name the checked file by its lower-case name.
- On Linux, a real file and a link whose names differ only in case show under one lower-case name.
- On Linux, a case twin reached only through a linked parent folder is named by its lower-case name in its LOOK, not by its own spelling.
- On Linux and macOS, a plugin named on the command line that is a symlink isn't read. When a folder holds both `Foo.esm` and `foo.esm`, `triage` may read `foo.esm` even if you named `Foo.esm`. Both were already true before 0.9.1.
- The fix line for an archive with too many entries is the same for `release` and `triage`, so it speaks of "the archive" in both.

## 0.9.0 (2026-11-20)

### What's new

**New command: `sf-mod-check patch`.** It builds a local conflict patch for your load order, with no AI assistant and no xEdit. You add two new mods, `conflict` says one undoes part of the other, and `patch` writes one small light plugin that loads last and brings the lost changes back.

`conflict` now names `sf-mod-check patch` first in a forwardable conflict's recommendation, before xEdit and the AI prompt.

### Example

Patch only the conflicts that involve the two mods you just added:

    sf-mod-check patch --only NewModA.esm,NewModB.esm

What it prints (a real run on a 158-plugin load order; mod names changed):

    sf-mod-check 0.9.0 by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sf-mod-check
    Loaded 158 plugins (0 load-order problems) in 0 s
    25 records overridden by mods
    RESULT: OK, patch sf-mod-check-patch\ConflictPatch.esm: 13 records, 25 fields forwarded, 0 picks used,
    18 shared lists merged, 0 choices kept the winner's value, 0 records not built, 11 masters
    (report: sf-mod-check-patch\patch-report.md; 38 s)

A line from `patch-report.md`:

    - AffinityEvent COM_Event_Action_Steal [00E39C:Starfield.esm]
      - RequiredFaction from NewModA.esm: changes required faction from CurrentFollowerFaction (faction)
        to NewModA_CommentsFaction (faction)

Check the patch before you install it, then install `ConflictPatch.esm` with your mod manager at the end of the load order:

    sf-mod-check check sf-mod-check-patch\ConflictPatch.esm
    sf-mod-check conflict --plugins sf-mod-check-patch\Plugins-with-patch.txt --extra-data sf-mod-check-patch

### How it builds each record

- It starts from the record as the game uses it today (Starfield's partial forms filled in from earlier plugins).
- It copies every **Forwardable** field forward from the plugin that lost it.
- It merges lists entry by entry: each plugin's own additions and removals count, also when both plugins changed the list. A location's base-game lists stay as the base game and its DLCs have them.
- Each other **Choice** field (both plugins changed it) keeps the winner's value. The report prints a ready-made `--pick <FormKey>:<Field>=<plugin>` line for each one, and `--pick <FormKey>=<plugin>` takes a whole record.
- A record that would equal today's winner is left out (no ITMs).
- It reads the written patch back and compares every field with its source. Exit 1 means a difference was found: don't use that patch.

### Options and output

- `--only a.esm,b.esm` limits the patch to conflicts that involve those plugins (and is faster).
- `--out` (default `sf-mod-check-patch`) gets the patch, `patch-report.md` and `Plugins-with-patch.txt` (your load order with the patch at the end). It never writes to the Data folder or Plugins.txt.
- `--name` (default `ConflictPatch.esm`) refuses names the game would drop from Plugins.txt. An older copy of the patch in the load order is left out while the new one is built.
- Text from plugins in `patch-report.md` is escaped the same way as in `conflict`'s report, so a mod's text can't become a link, an image or HTML in a Markdown viewer.

### Not built yet

- Records that live inside other records aren't built yet: cells, worldspaces, placed objects, navmeshes, dialogue and scenes. The report lists them for xEdit.
- The navmesh info map (NAVI) is never patched, because each plugin's copy lists only its own navmeshes.
- Lost changes that `conflict` already calls harmless (only a reorder, letter case or undocumented fields) are left alone.

### Fixes found before release

- A patched quest whose winning version has dialogue now reads back clean. Its `Unknown` value lives in the dialogue group's header, which the patch doesn't copy.
- The report's Choices heading counts the lists merged entry by entry, and says a `--pick` takes one plugin's whole list.

## 0.8.0 (2026-11-20)

- **Renamed: sfmodcheck is now sf-mod-check.** The exe is `sf-mod-check.exe`, the dotnet tool package and command are
  `sf-mod-check`, and the GitHub repo is taosecurity/sf-mod-check (the old address forwards). The exe zip also holds
  `sfmodcheck.cmd`, which runs `sf-mod-check.exe` under the old name; it will be dropped in a later release. The cache
  folder (`%LOCALAPPDATA%\sfmodcheck`) and the `SFMODCHECK_*` environment variables keep their names. dotnet tool
  users: `dotnet tool uninstall --global sfmodcheck`, then install `sf-mod-check`. `conflict`'s default report folder
  is now `sf-mod-check-conflict`, and the example log name is `sf-mod-check-log.txt`.
- A new command, `sf-mod-check triage <mod>`, writes a first-pass, offline report on someone else's mod (a .zip, .7z or .rar
  download, a folder, or one plugin) as HTML, text and JSON. It is triage, not a verdict: each finding is a
  NOTE or a LOOK (worth a person's look) with the file and the reason. Its sections are Files (every file with its SHA-256,
  archive contents, versions in names, archives no plugin loads), Replaces vanilla (loose files and archive entries
  that replace the game's own, from a cached index of the game's archive names), Plugins (header, records added,
  overridden and deleted, ITMs, official changes undone, changed fields, ONAM, missing scripts, `check`'s findings),
  Scripts (each `.pex` read from its bytecode: calls, logging, calls worth a look; nothing is decompiled) and Binaries
  (each DLL, EXE and ASI plugin read from its bytes: imports by group, URLs, SFSE plugin version data, PDB path, signature checked
  offline, and optionally Manalyze with offline plugins only). It never runs the mod's code and never uploads
  anything. It exits with 0 when the report is written and 2 for bad input. Reading `.7z` and `.rar` uses SharpCompress 0.50.7 (MIT).
- `check` (and `release`, `conflict`'s ITM count): when a lookup of an overridden cell's master version comes back
  empty, its placed references and navmeshes are now counted in the "couldn't be compared" WARNING. Before, they
  counted as "not identical" with no warning. This was seen only with the .NET heap capped at 1 GB (a large test mod: 1 warning
  instead of 95); a normal run is unchanged.
- `check`: Bethesda's own plugins are recognized by their exact names (as `conflict` has done since 0.5.0). Before, any
  name starting with `SFBGS` or `BlueprintShips-` counted as official, so a mod named like `SFBGS003Fixes.esm` had its
  ITMs counted but not listed.

## 0.7.0 (2026-11-19)

- A new command, `sfmodcheck release <zip>...`, gives one OK or FAIL for the zip you are about to upload. It unpacks each zip
  into a temp folder (removed at the end) and checks: plugin names the game drops from Plugins.txt; sound files
  (`.wem`, `.ffxanim`) stored compressed in an archive (the game plays them silent); archives that can't be read;
  scripts a plugin attaches (and, without DMMDeps, models it names) that are in none of the zip's files and archives,
  a `--masters-dir` mod's archives or the game's; the `check` checks on every plugin in the zips; and the data
  Mutagen 0.54.4 loses when it writes a plugin, plus a missing ONAM list. Output is PROBLEM, REVIEW, WARNING and
  NOTE lines with a `[rule]` tag, then `RESULT: OK|FAIL`; exit 0, 1 or 2.
- `--profile basic` (default) fails only on what breaks the mod in the game. `--profile house` also fails on packaging
  rules: a BlueprintShips master, more than one plugin per zip, `.psc` sources in the zip, a root `.md` without the
  mod's name, files that must never ship (permission records, private comparison notes). In `basic` these are
  WARNING lines.
- An optional `--dmmdeps <exe>` runs DMMDeps (the reviewed v0.4.1 build only, by SHA-256) on a copy of the zip's
  contents to find files a plugin needs that the zip lacks; its known gaps (its issues #175 to #177) are REVIEW lines.
  It stops after 15 minutes. Without it, one NOTE says only scripts and models were checked.
- An optional `--papyrus <exe> --ck-sources <ContentResources.zip>` runs a Papyrus lint of the `.psc` sources a zip ships,
  with WARNING lines only; it is skipped with a WARNING when papyrus-tools isn't the tested v0.16.3 build or the vanilla sources
  are missing.
- `--master-archive <ba2>` (repeatable) names the archive of a mod the plugin patches; its scripts and models count as
  present.
- The gap check (data Mutagen 0.54.4 loses when writing, missing ONAM) is built in; its lines start with the plugin
  name. A master that isn't installed is one WARNING ("gap check could not run on <plugin>: master X not found").
- DMMDeps' parent-file matches are no longer taken from stale cached copies of the mod's own archives: before, a mod
  whose archive had been cached before (an earlier build of the same mod) came out with every file "also in the
  game's archives" (2,299 REVIEW lines on one mod; now 18).
- An archive that can't be read is one PROBLEM, not two.

## 0.6.0 (2026-11-19)

- `check` has a new `assets` check: every script a record attaches (attached scripts, and the fragment and alias scripts
  of quests, scenes, perks, topic infos and packages) must have its `.pex`, an ERROR when missing (the script never
  runs); every model path a record names must have its file, a WARNING when missing. A file counts when it is loose in
  the Data folder (or a `--masters-dir` folder) or inside an archive the game loads: `<plugin> - *.ba2` for the plugin
  and its masters (other languages' voice archives left out) plus the archives `Starfield.ini` and
  `StarfieldCustom.ini` list. Archive names are read from each archive's name table; nothing is unpacked. The new options are
  `--no-assets`, `--ini <file>` and `--language <code>`. These aren't checked yet: textures and materials, `.mesh` geometry,
  files the game finds by name (voice, lip and face animation, animations, LOD, Wwise), a script's parents and
  imports, SFSE plugins, and which copy of a file wins (README).
- `conflict` has the `assets` check too: every mod plugin in the load order is checked against the archives of every
  active plugin, the INIs' archive lists and loose files (the `--extra-data` folder, then Data); Bethesda's own
  plugins aren't checked. It takes the same options as `check` (`--no-assets`, `--ini`, `--language`). The RESULT line adds
  ", N missing assets (S scripts, M models)" and ", N archives that can't be read" when there are any. report.html
  and report.md get a "Missing assets" section (by plugin, scripts before models, with the archives read, any archive
  that can't be read, and how to fix it), a Summary row, and a recommended action per plugin (Medium when a script is
  missing, Low for models only) and per unreadable archive (Medium). conflicts.json gets `assetsChecked`,
  `archivesRead`, `missingAssets` (plugin, severity, kind, path, script, users, finding) and `unreadableArchives`
  (archive, why). With `--no-assets` the section says the check was off.
- In both modes of `assets`, a missing script or model in an override isn't reported when the version it overrides names
  the same path (the masters' winning version: in `conflict`, the record in the last of the plugin's masters, in
  load order, that has it). This was found on a large load order, where four mods were blamed for scripts
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
  Folders that differ only in case (`Scripts` and `scripts`) are both searched, a plugin's `<plugin> - *.ba2`
  archives are found whatever their case, Data folders whose names differ only in case stay apart, and an INI's
  `C:\...` archive path is reported not found. On every system, a record path with a part ending in a dot or space
  (`foo.`, `.. `) never counts as found, and an INI archive name with one leading `\` is looked up under Data.
- README "Known gaps" now says that a plugin's archives are every `<plugin> - *.ba2`, which can be a little wider than what the
  game opens.
- `conflict` counts the archives the game would load, general (GNRL) and texture (DX10) separately, and warns when
  either is over the game's 256 slots: the RESULT line adds ", WARNING N general archives (over 256)" (or texture),
  the report gets a WARNING in "Missing assets" and a High recommended action. The counts are always in the report's
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
- The text now uses "a"/"an" before record signatures spelled out letter by letter: "an FLST", "an LVLI", "an MGEF", "an FSTP" (before:
  "a FLST"). Signatures said as words keep the first-letter rule ("a MISC", "a LCTN"). Every Starfield signature that
  could take "an" is sorted into one list or the other, and a test fails on a new one.
- `--version` and the byline read the version from the build, so the program can't disagree with the package.
- `conflict`: the report-cleanup log line after a failed write names the full error; a missing report folder is no
  longer logged as a problem.
- Docs: README and AGENTS.md now say that stdout can hold stack traces Mutagen prints itself for scene actions it
  can't fully read (`Unexpected header type: PAQO`; unchanged behavior, also in 0.5.3), and that scripts should read
  only the exit code and the RESULT line.

## 0.5.3 (2026-11-18)

This release has the fixes from the 0.5.2 code reviews.

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
- Code: one helper now makes exception summaries, and the itm property lists are cached once, not twice.

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

One rule now covers errors that must stop a run. A record or field that can't be read is still skipped and listed, as before.
Running out of memory (and conflict's `--max-memory` stop, and a thread running out of stack) now always ends the run:
`check` with `RESULT: CANNOT CHECK` and exit code 2; `conflict` with `RESULT: CANNOT CHECK` and exit code 2 and no
report (`--max-memory` keeps exit code 3). In 0.5.1 some inner catches turned such an error into "unreadable" text and
carried on, so a report could look whole when it wasn't. In `check`, this covers opening the masters and the plugin,
reading each record and its links, the conditions, scenes and EditorIDs of each record, learning the official scene
list, the EditorID check, the condition walk, the itm comparison and the land-flags text. In `conflict`, it covers reading each
plugin's header and opening it, every catch in the comparison, the field hash and value text, the impact text (the
field walk, value descriptions and entry names), the land-flags text and the new record-type pass. An error wrapped in
another counts (inner exceptions are followed, and every error inside an AggregateException, not only the first).
Catches that take only named file errors (the log, the caches, zlib data) can't catch these, so they are unchanged.
The RESULT line after such an error is built once the run's memory is released, with a fixed text if that fails too.

Code: check's itm comparison and conflict's field comparison are one comparer (DeepCompare) with six options where
the two differ, so each keeps exactly its earlier answers: proved by running the 0.5.1 code beside the new one on
awkward values and on every field of real records in the tests, and by identical `check` output on all 18 official
masters and four mods and identical `conflict` findings on a 149-plugin load order. No option changed. There are 175 tests (17 new; one 0.5.1 test moved into the new ones). After a run that runs out of memory while writing its report, the report files are removed, so a half-written report never stays behind.

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

These are the other `conflict` fixes, from the 0.5.0 code review:
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

Code: `check` and `conflict` share one set of command-line helpers (Cli.cs); the load-order problem list is read-only
outside the code that cleans each entry. The new skip-and-carry-on catches in conflict let an out-of-memory error (and the --max-memory stop) through, so those still end the run. No option or output format changed. There are 159 tests (17 new).

## 0.5.0 (2026-11-16)

A new command, `sfmodcheck conflict`, reads a whole load order (a Plugins.txt) and reports the changes it throws away
(official changes undone by mods, one mod's changes lost to another, ITMs, load-order problems), labels each lost
field Forwardable, Choice or No effect, and recommends what to do (a safe load-order move, an update to look for, or a
local patch with a prompt for building it). It writes a report folder with `report.html`, `report.md` and `conflicts.json`.
Its options are `--plugins`, `--data`, `--extra-data`, `--out`, `--max-memory`, `--quiet`, `--verbose` and `--log`, and its exit codes are
0, 2 and 3.

It comes from an older private tool of the author's (in use since 2026-10-28), after a fix of its memory
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

These fixes come from the release's code review (`conflict`):
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
  stay on one line in report.md; the text says "mods (item modification)" instead of "mod (item modification)s".
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
land-flag change went unseen. Both now compare the grid point and the land-flag byte only. The findings are the same as before
on six test load orders.

## 0.4.1 (2026-11-16)

This is a security and bug-fix release, from a code review and a security review. It is built on BuildKit 0.3.1.

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

This is the first public release. It is built on Mutagen.Bethesda.Starfield 0.54.4 (through BuildKit 0.2.0).

- `sfmodcheck check <plugin>` runs the structure, header, ids, read, links, conditions, dialogue, scenes, navi, editorids,
  itm and records checks, with ERROR / WARNING lines, one RESULT line and exit codes 0, 1 and 2.
- HINT lines: after each check's findings, one line says how to fix them in xEdit or the Creation Kit. `--no-hints`
  turns them off.
- The official scene list is learned from your own Data folder and cached in `%LOCALAPPDATA%\sfmodcheck\`.
- A note on stderr appears before checking an official master or a plugin with more than 500,000 records (Starfield.esm
  needs over 30 GB of memory).
- Drag and drop: a plugin dropped on `sfmodcheck.exe` is checked in its own window, which stays open at the end. With
  no `--data` and no game in Steam's usual folder, a plugin inside a Data folder uses that folder.
- There are two downloads: a dotnet tool package and a stand-alone `sfmodcheck.exe` (win-x64, .NET built in).
- Troubleshooting log: `--log <file>` writes what sfmodcheck read (plugin, Data folder and why, where each master was
  found, caches used or rebuilt), the time per stage, the findings per check and any error in full; `--verbose` prints
  the same on stderr. The findings and the RESULT line don't change.
- Errors for wrong arguments name the file or option and the fix (a missing option value or a bad `--max` used to end
  in a stack trace). A `--data` folder that doesn't exist stops at once with exit code 2.
- `SFMODCHECK_CACHE_DIR` moves the caches.
- AGENTS.md gives step-by-step install and use instructions for AI assistants (also in both downloads). The README has notes for
  Game Pass, Vortex and Mod Organizer 2 installs.

Earlier versions (0.1.0 to 0.3.0, 2026-11-06 to 2026-11-08) were private, under the name StarfieldPluginCheck.
