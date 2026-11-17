# Privacy

sfmodcheck runs on your own computer. It collects no data and sends nothing anywhere.

## What it does not do

- It makes no network connections.
- It has no telemetry, analytics, crash reporting or update checks.
- It never changes a plugin or any game file.

## What it reads

- the plugin you ask it to check;
- that plugin's masters, from the folders you pass with `--masters-dir`, the plugin's own folder and the game's Data
  folder (`--data`, or Steam's usual Data folder);
- the official masters in the Data folder (Starfield.esm and the other Bethesda plugins), to learn the official scene
  list and the masters' EditorIDs;
- for `conflict`: the load order file you give with `--plugins` (or the game's own
  `%LOCALAPPDATA%\Starfield\Plugins.txt`), and every plugin it lists, from `--extra-data` and the Data folder.

## What it writes

`conflict` writes its report (`report.html`, `report.md`, `conflicts.json`) to the folder you name with `--out`
(`sfmodcheck-conflict` in the current folder by default). The report lists your load order and the plugin and folder
paths it used, and it quotes record data from the plugins, other authors' work included; check it before you share
it. The report page loads nothing from the web.

`check` writes only its own caches, in `%LOCALAPPDATA%\sfmodcheck\` (on Linux `~/.local/share/sfmodcheck/`; the environment variable
`SFMODCHECK_CACHE_DIR` moves them):

- `edid-cache\`: the EditorIDs of the masters it has read, one file per master, keyed by the master's name, size and
  modified time;
- `scene-quirks\`: the list of official scenes the scenes check skips, learned from your official masters.

They hold data from your own game files only, and stay on your computer. Delete the folder at any time; sfmodcheck
rebuilds what it needs on the next check.

It prints its results to the console. It writes a log file only when you ask for one with `--log <file>`, to the file
you name (`--verbose` prints the same lines on the console instead). The log lists the paths of the plugin, its
masters, the Data folder and the caches, so it can include your Windows user name; read it before you share it.

## Other programs it starts

None.

## Personal data

sfmodcheck handles none. Its output shows file names and folder paths you gave it, and the records inside the
plugin; if you post the output somewhere, check those paths first (they can include your Windows user name).

## Downloading it

Downloading sfmodcheck from GitHub is covered by GitHub's own
[privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement). The dotnet
tool install fetches nothing from the network when you install from the downloaded package with `--add-source`.
sfmodcheck's author receives only the public download counts GitHub shows.

## Questions

Open an issue at https://github.com/taosecurity/sfmodcheck/issues.
