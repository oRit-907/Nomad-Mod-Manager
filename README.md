# Nomad Mod Manager (in-game Mods menu)

A BepInEx 5 plugin for Nomad Drive by AkoRit that adds a **Mods** button to the main menu and pause menu: find, install,
update and switch mods on and off without a restart, and play online without getting kicked.
This repo has all of the Mods menu: its source (`Plugin/`, `Patcher/`, `Shared/`, `Lang/`), the in-game browser the
website edition uses (`NomadBrowser/`), and its releases. (Until now it lived in
[NomadModDownloader](https://github.com/oRit-907/NomadModDownloader), `mod/NomadModManager`; the older history and
releases are there.)

## Two editions

| | Website edition | Nexus edition |
|---|---|---|
| Where | [nomaddrivemods.online/mod/1](https://nomaddrivemods.online/mod/1) | [Nexus Mods](https://www.nexusmods.com/nomaddrive/mods/51) |
| Build | `dotnet build Plugin -c Release` | `dotnet build Plugin -c Release -p:NexusEdition=true` |
| Mods from | the website (Browse mods, packs, Subscribe) and Nexus | Nexus only (Browse mods) |
| Nexus installs | in the menu's own browser (NomadBrowser, downloaded once from GitHub) | in your own web browser: the menu installs the file from your Downloads folder (.zip, .7z, .rar, .dll) |
| Updates | updates itself from the website | never updates itself; shows Nexus updates |
| Internet | the website, GitHub, Nexus | Nexus Mods only |

Everything that differs is behind `Edition.Nexus` / `#if NEXUS_EDITION` (see `Plugin/Edition.cs`).
The Nexus zip also carries BepInEx 6 Compatibility (`BepInEx/plugins/BepInEx6Compat`, source in
[NomadModDownloader/mod/BepInEx6Compat](https://github.com/oRit-907/NomadModDownloader/tree/main/mod/BepInEx6Compat))
with BepInEx 6.0.0-be.697's runtime (LGPL-2.1, unchanged) in `BepInEx/compat/BepInEx6`, and
`NomadModManager.SharpCompress.dll` (SharpCompress 0.24.0, MIT, made by `tools/scpatch`).

**What it does** is in [`README.txt`](README.txt): the website edition's description in the site's BBCode, attached to
every release.

## Releases

Push a branch `menu-release/vX.Y.Z` with a `release-files/` folder (the zips, `README.txt`, an optional `notes.md`,
and **`SHA256SUMS` last**). `.github/workflows/menu-release.yml` checks the hashes, creates release
`mods-menu-vX.Y.Z` and deletes the branch. The in-game browser has its own: `.github/workflows/browser-release.yml`.

## Install

Unzip `NomadModManager-<version>-BepInEx5.zip` into the Nomad Drive folder (it contains a `BepInEx` folder). 

| File | Goes to |
|---|---|
| `NomadModManager.dll` | `BepInEx\plugins\NomadModManager\` |
| `NomadModManager.ModFiles.dll` | `BepInEx\patchers\` (applies queued changes at launch) |
| `libwebp.dll`, `libsharpyuv.dll` | `BepInEx\NomadModManager\native\` (decodes mod pictures, which are often WebP) |

## Mod authors

The menu checks a new DLL before starting it without a restart. To decide for your mod yourself, add one line:

```csharp
[assembly: System.Reflection.AssemblyMetadata("NomadHotLoad", "true")]   // or "false" to always ask for a restart
```

## What it does (1.1.0)

- **Browse mods** by category and sort. Open a mod for its full page: picture, description, every file and version
  with changelogs, and its requirements (install them together with "Install with requirements").
- **Installed**, grouped by author, with a search box and **Update all**. Mods that didn't start say why (missing or
  switched-off requirements, conflicts, a newer copy, Unity code the game left out, errors) with a one-click fix.
- **Settings** for each mod: switches, choices, numbers (with a drag bar when the mod gives a range), text and key
  rebinding. Running mods take changes right away; the rest read them from their config file at their next start.
- **Profiles**: save which mods are on and switch sets in one go (kept in `BepInEx\NomadModManager\profiles.json`).
- **Changes** waiting for a restart, with Undo.
- Opens from the **Mods** button on the main menu and in the pause (Esc) menu. An optional hotkey (`Hotkey` in the config) opens it anywhere.
  Works with a **controller**: d-pad/stick to move, A to press, B back, LB/RB to switch pages, right stick to scroll.

Installs, updates and specific versions are handed to Nomad Mod Downloader (2.6.8 or newer for several at once and
for specific versions).

## BepInEx 5 only

The menu is built for BepInEx 5 only. The BepInEx 6 builds (`Plugin6/`, `Patcher6/`) were removed in 1.6.2; mods built
for BepInEx 6 still load through BepInEx 6 support (`../BepInEx6Compat`), set up by Nomad Mod Downloader.

## Building

Needs the .NET 8 SDK. The game's own DLLs aren't in this repo, so copy them from your install first:

1. `lib/`: everything in `Nomad Drive_Data\Managed\` and a BepInEx 5 `BepInEx\core\`.
2. `lib/sys/` (see [`lib/README.md`](lib/README.md)): `mscorlib.dll`, `System.dll` and `System.Core.dll` from `Nomad Drive_Data\Managed\`, plus `netstandard.dll` from the .NET Framework 4.7.2 reference assemblies' `Facades` folder.

The plugin compiles against the game's own code-stripped system libraries on purpose, so anything Unity stripped fails at build time instead of in the game.

```
dotnet build Patcher -c Release -o out/Patcher     # BepInEx 5
dotnet build Plugin -c Release -o out/Plugin
```

The zip holds `BepInEx/plugins/NomadModManager/NomadModManager.dll`, `BepInEx/patchers/NomadModManager.ModFiles.dll`
and `BepInEx/NomadModManager/native/` (libwebp) from the build.

libwebp and libsharpyuv are Google's official builds (BSD 3-Clause), from the `Imazen.WebP.NativeRuntime.win-x64` 1.6.1 package.

## New in 1.2.0

- **Multiplayer**: in a lobby, every player who has this menu shares their mod list on their lobby membership (an EOS member attribute, like the game's display name), and everyone's mods are compared: what you're missing (with Install), what's on another version, what they're missing. Nothing is sent over the game's connection, so players without the menu aren't affected. Share codes ("NMM:…", copied to the clipboard) compare mod lists outside a lobby.
- **Conflict finder**: running mods that rewrite the same game method (Harmony prefixes/transpilers from two mods) are flagged on their Installed rows.
- **Save backups**: saves (Unity's persistent data folder) are copied to `BepInEx\NomadModManager\save-backups` before the menu changes mods (at most every 5 minutes, last 10 kept), with Restore (main menu only; the current saves are backed up first).
- **Activity page**: what the menu did (`activity.log`), this session's errors and warnings from LogOutput.log, and the save backups.
- **Settings**: a find box and Reset all.
- **Languages**: the menu follows the game's language (config `General/Language`), using Nomad Mod Downloader's translations plus `Lang/*.json`.
- **Endorse** opens the mod in Nomad Mod Downloader 2.7.2+ (logged in, Endorse highlighted).
- The update count shows on the Mods button in the pause menu too, and Nexus is checked once at the main menu so the count is there before opening the menu.
