#!/usr/bin/env bash
# Builds the Nexus edition's zip: NomadModManager-<version>-Nexus-BepInEx5.zip (what's uploaded to
# https://www.nexusmods.com/nomaddrive/mods/51). Run from anywhere: bash nexus/build.sh
# Needs the .NET SDK, and lib/ filled in as lib/README.md says (including lib/bep6 and lib/native).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="$(sed -n 's/.*const string Version = "\([^"]*\)".*/\1/p' Plugin/Plugin.cs | head -1)"
out="nexus/out"
pkg="$out/pkg"
rm -rf "$pkg" && mkdir -p "$pkg"

dotnet build Plugin -c Release -p:NexusEdition=true
dotnet build Patcher -c Release
dotnet build BepInEx6Compat -c Release

b="$pkg/BepInEx"
mkdir -p "$b/plugins/NomadModManager" "$b/patchers" "$b/plugins/BepInEx6Compat" "$b/compat/BepInEx6" "$b/NomadModManager/native"
cp Plugin/bin/Release/NomadModManager.dll "$b/plugins/NomadModManager/"
cp lib/NomadModManager.SharpCompress.dll lib/NomadModManager.SharpCompress-LICENSE.txt "$b/plugins/NomadModManager/"
cp nexus/package/THIRD-PARTY-NOTICES.txt "$b/plugins/NomadModManager/"
cp Patcher/bin/Release/NomadModManager.ModFiles.dll "$b/patchers/"
cp BepInEx6Compat/bin/Release/BepInEx6Compat.dll "$b/plugins/BepInEx6Compat/"
for f in BepInEx.Core BepInEx.Unity.Mono BepInEx.Unity.Common BepInEx.Preloader.Core SemanticVersioning AssetRipper.Primitives; do
  cp "lib/bep6/$f.dll" "$b/compat/BepInEx6/"
done
cp nexus/package/compat-BepInEx6-README.txt "$b/compat/BepInEx6/README.txt"
cp lib/native/libwebp.dll lib/native/libsharpyuv.dll "$b/NomadModManager/native/"
cp nexus/package/libwebp-licence.txt "$b/NomadModManager/native/"

zip="$out/NomadModManager-$version-Nexus-BepInEx5.zip"
rm -f "$zip"
(cd "$pkg" && zip -qr "../$(basename "$zip")" BepInEx)
echo "$zip"
(cd "$out" && sha256sum "$(basename "$zip")")
