Makes lib/NomadModManager.SharpCompress.dll (lib is not in git):
  dotnet run --project mod/NomadModManager/tools/scpatch -- SharpCompress.dll mod/NomadModManager/lib/NomadModManager.SharpCompress.dll
with SharpCompress.dll from the SharpCompress 0.24.0 NuGet package (lib/net45). Ship it and NomadModManager.SharpCompress-LICENSE.txt next to NomadModManager.dll.
