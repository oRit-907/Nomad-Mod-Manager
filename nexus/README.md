# Nexus edition

The Mods menu's edition for [Nexus Mods](https://www.nexusmods.com/nomaddrive/mods/51). It's the same source as the
website edition (`Plugin/`), built with `-p:NexusEdition=true`; everything that differs is behind `Edition.Nexus` /
`#if NEXUS_EDITION` (see `Plugin/Edition.cs`, and `Plugin/ModsMenu.NexusDownloads.cs` for its installs).

What the Nexus edition leaves out, so it follows Nexus's rules: the website (Browse mods from nomaddrivemods.online,
packs, accounts, invite links, the community button), the in-game browser, self-updates and every download of its own.
Install opens the mod's Nexus page in the player's web browser; the player downloads there, and the menu installs the
file from their Downloads folder. It talks to Nexus Mods only, with `Application-Name: NomadModManager` and
`Application-Version` on every request.

## Build the zip

```
bash nexus/build.sh
```

It builds `Plugin` (Nexus edition), `Patcher` and `BepInEx6Compat`, and packs `nexus/out/NomadModManager-<version>-Nexus-BepInEx5.zip`:

| In the zip | From |
|---|---|
| `BepInEx/plugins/NomadModManager/NomadModManager.dll` | `Plugin` (`-p:NexusEdition=true`) |
| `BepInEx/plugins/NomadModManager/NomadModManager.SharpCompress.dll` + licence | `tools/scpatch` (SharpCompress 0.24.0, MIT) |
| `BepInEx/plugins/NomadModManager/THIRD-PARTY-NOTICES.txt` | `nexus/package/` |
| `BepInEx/patchers/NomadModManager.ModFiles.dll` | `Patcher` |
| `BepInEx/plugins/BepInEx6Compat/BepInEx6Compat.dll` | `BepInEx6Compat` (by AkoRit) |
| `BepInEx/compat/BepInEx6/*.dll` | BepInEx 6.0.0-be.697's runtime, unchanged (LGPL-2.1; SemanticVersioning and AssetRipper.Primitives MIT), in `lib/bep6` |
| `BepInEx/NomadModManager/native/libwebp.dll`, `libsharpyuv.dll` | Google's official builds (BSD), from the `Imazen.WebP.NativeRuntime.win-x64` 1.6.1 package, in `lib/native` |

## Files here

- `build.sh`: builds the zip.
- `description.txt`: the Nexus page's description (BBCode).
- `package/`: the text files that go in the zip (third-party notices, the BepInEx 6 runtime's README, libwebp's licence).

## Sign in with Nexus

`Plugin/NexusAccount.cs`: OAuth 2.0 with PKCE (Nexus's sign-in page; the answer comes back to
`http://127.0.0.1:47380/nexus-callback`). It's off until Nexus registers the app and its client id goes in
`NexusAccount.ClientIdBuiltIn`. `-p:TestKey=true` makes a private test build that can use a personal API key instead;
those builds are never released (Nexus doesn't allow personal keys in public apps).
