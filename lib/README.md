# lib (not in git)

The build references these, which can't be shared here (they belong to the game, Unity and others). Copy them in:

- `lib/sys/`: `mscorlib.dll`, `System.dll`, `System.Core.dll`, `netstandard.dll` from `Nomad Drive\Nomad Drive_Data\Managed`
  (the game's own, so anything the game left out fails at build time).
- From `Nomad Drive_Data\Managed`: `Assembly-CSharp.dll`, `EvilCore*.dll`, `com.Epic.OnlineServices.dll`, `Newtonsoft.Json.dll`,
  `Unity.InputSystem.dll`, `Unity.Localization.dll`, `Unity.TextMeshPro.dll`, `UnityEngine*.dll`, `UniTask.dll`, `VContainer.dll`,
  `PrimeTween.Runtime.dll`, `Sirenix.*.dll`.
- From BepInEx 5.4.23.x (`BepInEx\core`): `BepInEx.dll`, `BepInEx.Preloader.dll`, `BepInEx.Harmony.dll`, `0Harmony.dll`,
  `0Harmony20.dll`, `HarmonyXInterop.dll`, `Mono.Cecil*.dll`, `MonoMod.*.dll`.
- `NomadModManager.SharpCompress.dll` and its licence: made by `tools/scpatch` (see its README).
