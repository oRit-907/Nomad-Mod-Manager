using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EvilCore.UI.MainMenu.Panels;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[assembly: System.Reflection.AssemblyMetadata("NomadHotLoad", "false")]

namespace NomadModManager
{
    [BepInPlugin(Guid, "Nomad Mod Manager", Version)]
    public class ModManagerPlugin : BaseUnityPlugin
    {
        public const string Guid = "akorit.nomadmodmanager";
        public const string Version = "1.11.0";

        internal static ModManagerPlugin I;
        internal static ManualLogSource Log;

        internal ModCatalog Catalog;
        internal ModsMenu Menu;
        internal ConfigEntry<bool> AutoStart;
        internal ConfigEntry<float> Scale;
        internal ConfigEntry<string> OpenKey;
        internal ConfigEntry<bool> OpenWithController;
        private UnityEngine.InputSystem.Key _openKey = UnityEngine.InputSystem.Key.F8;

        /// <summary>The game's main menu panel, while it exists.</summary>
        internal MainMenuPanel MainMenu;
        internal bool AtMainMenu => MainMenu != null && MainMenu.isActiveAndEnabled;

        /// <summary>The game's pause (Esc) menu, while it exists.</summary>
        internal EvilCore.UI.GameMenu.Panels.GameMenuRootPanel PauseMenu;

        private void Awake()
        {
            I = this;
            Log = Logger;
            AutoStart = Config.Bind("General", "StartNewModsAutomatically", true,
                "Start newly installed mods right away when they can run without a restart (only at the main menu).");
            Scale = Config.Bind("General", "MenuScale", 1f, "Size of the Mods menu (1 = 1080p size).");
            OpenKey = Config.Bind("Controls", "Hotkey", "None",
                "Optional key that opens the Mods menu anywhere, also during play (a key name like F8, Home or M). " +
                "Off by default: the Mods button is on the main menu and in the pause (Esc) menu.");
            OpenWithController = Config.Bind("Controls", "HoldViewToOpen", false,
                "Hold the controller's View (Back) button for a second to open the Mods menu.");
            ReadOpenKey();
            OpenKey.SettingChanged += (_, __) => ReadOpenKey();

            Catalog = new ModCatalog();
            Catalog.SnapshotStartup();
            Catalog.AutoStart = AutoStart.Value;
            Menu = new ModsMenu(this, Catalog);
            Catalog.Toast += Menu.Toast;

            var harmony = new Harmony(Guid);
            try { harmony.PatchAll(typeof(MainMenuStartPatch)); }
            catch (Exception e) { Logger.LogError("Couldn't hook the main menu: " + e); }
            try { harmony.PatchAll(typeof(PauseMenuStartPatch)); }
            catch (Exception e) { Logger.LogError("Couldn't hook the pause menu: " + e); }
            try { harmony.PatchAll(typeof(LobbyListPatches)); }
            catch (Exception e) { Logger.LogWarning("Couldn't hook the lobby list (mod badges and the join check are off): " + e.Message); }
            try { harmony.PatchAll(typeof(LobbyFilter)); }
            catch (Exception e) { Logger.LogWarning("Couldn't hook the lobby list's rows (mods filters are off): " + e.Message); }
            LobbyMods.Left += () => { try { HostSettings.Restore(); } catch { } };

            try { Directory.CreateDirectory(Pending.Dir(Paths.GameRootPath)); } catch { }
            PatcherNames.Tidy();
            if (!PatcherNames.ModFilesInstalled)
                Logger.LogWarning("NomadModManager.ModFiles.dll is missing from BepInEx\\patchers — queued changes won't be applied at launch.");

            Language = Config.Bind("General", "Language", "Game",
                "The Mods menu's language: Game (follow the game's language), or en, de, es, fr, it, ja, ko, pl, pt, ru, tr, zh.");
            EndOnQuit = Config.Bind("General", "EndGameAfterQuit", true,
                "Make sure the game really ends after you quit. Some mods leave work running that keeps the game open in the background (Steam still shows it as running).");
            Application.quitting += OnQuitting;
#if NEXUS_EDITION
            // The Nexus edition: Nexus Mods only. Files are downloaded by the player in their own web browser.
            DownloadsFolder = Config.Bind("Nexus", "DownloadsFolder", "",
                "Where your web browser saves downloads, if it isn't your usual Downloads folder. The Mods menu installs a Nexus mod once its file appears there.");
            StartCoroutine(Poll());
#else
            CommunitySite = Config.Bind("Community", "Website", Community.DefaultSite,
                "The Nomad Drive Mods community website the Browse mods page reads from. Its mods install straight from the game.");
            SetSite();
            CommunitySite.SettingChanged += (_, __) => SetSite();

            BrowserAutoGet = Config.Bind("Nexus", "DownloadBrowserByItself", true,
                "Get the Mods menu's browser for Nexus Mods by itself in the background (a one-time download, about 170 MB, then small updates), so Install is ready right away. Off: it's downloaded the first time you press Install.");
            StartCoroutine(Poll());
            StartCoroutine(PrepareBrowser());
#endif
            Logger.LogInfo("Nomad Mod Manager loaded. Open it with the Mods button on the main menu or in the pause menu.");
        }

        internal ConfigEntry<bool> EndOnQuit;
        internal ConfigEntry<string> DownloadsFolder;
        internal ConfigEntry<bool> BrowserAutoGet;

        /// <summary>
        /// Gets (or updates) the in-game browser in the background a little after the game starts, so Nexus installs
        /// are ready without a "Get the browser" step. Quietly retried at the next start if it fails.
        /// </summary>
        private IEnumerator PrepareBrowser()
        {
            yield return new WaitForSecondsRealtime(25f);   // after the game has loaded
            if (BrowserAutoGet == null || !BrowserAutoGet.Value) yield break;
            if (Application.platform != RuntimePlatform.WindowsPlayer || InGameBrowser.Installed || InGameBrowser.Fetching) yield break;
            Logger.LogInfo(InGameBrowser.HasBase ? "Updating the in-game browser in the background." : "Getting the in-game browser for Nexus Mods in the background (one time, about 170 MB).");
            yield return InGameBrowser.Fetch((ok, msg) => { if (ok) Logger.LogInfo("The in-game browser is ready."); });
        }
        internal ConfigEntry<string> CommunitySite;

        private void SetSite()
        {
            var site = (CommunitySite.Value ?? "").Trim();
            if (site.Length == 0) site = Community.DefaultSite;
            if (!site.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !site.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) site = "https://" + site;
            // Accept the address of any page on the site.
            var cut = site.IndexOf("index.php", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) site = site.Substring(0, cut);
            Community.Site = site.TrimEnd('/') + "/";
        }
        internal ConfigEntry<string> Language;
        private static bool _quitWatch;

        /// <summary>
        /// Unity has finished quitting at this point except for whatever other mods left running.
        /// If the process is still here a few seconds later, something is holding it open: end it.
        /// </summary>
        private void OnQuitting()
        {
            try { InGameBrowser.Close(); } catch { }
            if (_quitWatch || EndOnQuit == null || !EndOnQuit.Value) return;
            _quitWatch = true;
            Logger.LogInfo("Game is quitting; will make sure it ends.");
            var t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(6000);
                try
                {
                    File.AppendAllText(Path.Combine(Pending.Dir(Paths.GameRootPath), "quit.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  The game was still running 6 seconds after quitting, so Nomad Mod Manager ended it.\r\n");
                }
                catch { }
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
                catch { try { Environment.Exit(0); } catch { } }
            }) { IsBackground = true, Name = "NomadModManager quit watch" };
            t.Start();
        }

        private IEnumerator Poll()
        {
            yield return new WaitForSecondsRealtime(1f);
            while (true)
            {
                try
                {
                    Catalog.AutoStart = AutoStart.Value;
                    Catalog.AtMainMenu = () => AtMainMenu;
                    Catalog.Refresh();
                    // New mods start only at the main menu: nothing of the world exists yet, so they set up like at launch.
                    // Mods that support live on/off start any time, and their downloaded updates swap in while you play.
                    if (AtMainMenu) Catalog.StartNewMods(); else Catalog.StartNewMods(liveOnly: true);
                    Catalog.ApplyWaiting();
                    // The update count on the Mods buttons: check Nexus once, quietly, when the main menu is up.
                    if (AtMainMenu && !Menu.CheckedUpdates && Time.realtimeSinceStartup > 8f) Menu.CheckUpdates();
                    MenuButton.UpdateBadge(Catalog);
                }
                catch (Exception e) { Logger.LogWarning("Mod scan: " + e.Message); }
                yield return new WaitForSecondsRealtime(Menu.IsOpen ? 1f : 2f);
            }
        }

        private void ReadOpenKey()
        {
            var v = (OpenKey.Value ?? "").Trim();
            try { _openKey = v.Length == 0 || v.Equals("None", StringComparison.OrdinalIgnoreCase) ? UnityEngine.InputSystem.Key.None : (UnityEngine.InputSystem.Key)Enum.Parse(typeof(UnityEngine.InputSystem.Key), v, true); }
            catch
            {
                // KeyCode-style names ("Alpha1", "Return") map to the Input System's.
                _openKey = UnityEngine.InputSystem.Key.F8;
                foreach (UnityEngine.InputSystem.Key k in Enum.GetValues(typeof(UnityEngine.InputSystem.Key)))
                    if (string.Equals(ModsMenu.KeyCodeName(k), v, StringComparison.OrdinalIgnoreCase)) { _openKey = k; break; }
            }
        }

        private void Update()
        {
            if (AtMainMenu) { MenuButton.KeepLayout(); CommunityButton.Keep(); }
            if (PauseMenu != null && PauseMenu.isActiveAndEnabled) PauseButton.KeepLayout();
            // Opened from the main menu and the game has moved on (a save is loading), or the pause menu closed: close.
            if (Menu.IsOpen && Menu.OpenedFromMainMenu && !AtMainMenu) Menu.Close();
            if (Menu.IsOpen && Menu.OpenedFromPause && (PauseMenu == null || !PauseMenu.isActiveAndEnabled)) Menu.Close();
            try { LobbyMods.Tick(Catalog); } catch { }
            try { NetGuard.Tick(); } catch { }
            try { JoinDoctor.Tick(); } catch { }
            try { SafeJoin.Tick(); } catch { }
            try { Lang.Update(Language.Value); } catch { }
            try { Menu.Tick(_openKey, OpenWithController.Value); }
            catch (Exception e) { Logger.LogWarning("Mods menu input: " + e.Message); }
        }

        private void OnGUI()
        {
            try { Menu.Draw(); }
            catch (Exception e) { Logger.LogWarning("Mods menu draw: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "Start")]
    internal static class MainMenuStartPatch
    {
        private static void Postfix(MainMenuPanel __instance)
        {
            ModManagerPlugin.I.MainMenu = __instance;
            try { MenuButton.Add(__instance); }
            catch (Exception e) { ModManagerPlugin.Log.LogError("Couldn't add the Mods button: " + e); }
        }
    }

    [HarmonyPatch(typeof(EvilCore.UI.GameMenu.Panels.GameMenuRootPanel), "Start")]
    internal static class PauseMenuStartPatch
    {
        private static void Postfix(EvilCore.UI.GameMenu.Panels.GameMenuRootPanel __instance)
        {
            ModManagerPlugin.I.PauseMenu = __instance;
            try { PauseButton.Add(__instance); }
            catch (Exception e) { ModManagerPlugin.Log.LogError("Couldn't add the Mods button to the pause menu: " + e); }
        }
    }

    /// <summary>A "Mods" button in the pause (Esc) menu, right under Settings, cloned from it so it matches.</summary>
    internal static class PauseButton
    {
        private const string ObjectName = "NomadModManager_PauseModsButton";
        private static readonly List<(RectTransform Rt, Vector3 Original, Vector3 Target)> Moved = new List<(RectTransform, Vector3, Vector3)>();

        private static Vector3 Center(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            return (c[0] + c[2]) / 2f;
        }

        /// <summary>If the game puts a re-spaced item back (when the pause menu opens again), move it again.</summary>
        public static void KeepLayout()
        {
            foreach (var (rt, original, target) in Moved)
                if (rt != null && (rt.localPosition - original).sqrMagnitude < 0.01f) rt.localPosition = target;
        }

        public static void Add(EvilCore.UI.GameMenu.Panels.GameMenuRootPanel panel)
        {
            var source = Traverse.Create(panel).Field("settingsButton").GetValue<Button>()
                         ?? Traverse.Create(panel).Field("resumeButton").GetValue<Button>();
            if (source == null) { ModManagerPlugin.Log.LogWarning("Pause menu has no Settings button to copy."); return; }
            var parent = source.transform.parent;
            if (parent.Find(ObjectName) != null) return;

            var go = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            go.name = ObjectName;
            go.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1); // just under Settings
            MenuButton.PauseLabel = MenuButton.Relabel(go, source);
            MenuButton.UpdateBadge(ModManagerPlugin.I.Catalog, force: true);
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => ModManagerPlugin.I.Menu.OpenFromPause(panel));
            button.interactable = true;
            MenuButton.LinkNavigation(button, source, below: true);

            // Without a layout group the items are placed by position (each in its own container): space the column
            // out again with Mods in it, keeping its top and bottom where they are (the "Stuck?" cards sit just below).
            if (parent.GetComponent<LayoutGroup>() == null)
            {
                Canvas.ForceUpdateCanvases();
                var setRt = (RectTransform)source.transform;
                var cx = Center(setRt).x;
                var tol = 60f * Mathf.Max(0.5f, setRt.lossyScale.x);
                var column = panel.GetComponentsInChildren<Button>(true)
                    .Where(x => x != button && x.gameObject.activeInHierarchy && Mathf.Abs(Center((RectTransform)x.transform).x - cx) < tol)
                    .Select(x => (RectTransform)x.transform)
                    .OrderByDescending(rt => Center(rt).y).ToList();
                var at = column.IndexOf(setRt);
                if (at >= 0 && column.Count >= 2)
                {
                    var top = Center(column[0]).y;
                    var bottom = Center(column[column.Count - 1]).y;
                    var modsRt = (RectTransform)go.transform;
                    column.Insert(at + 1, modsRt);
                    modsRt.position = setRt.position;
                    var gap = (top - bottom) / (column.Count - 1);
                    Moved.Clear();
                    for (var i = 0; i < column.Count; i++)
                    {
                        var rt = column[i];
                        var original = rt.localPosition;
                        rt.position += new Vector3(0, (top - i * gap) - Center(rt).y, 0);
                        if (rt != modsRt) Moved.Add((rt, original, rt.localPosition));
                    }
                    ModManagerPlugin.Log.LogInfo($"Pause menu: {column.Count} items re-spaced {gap:0} px apart ({string.Join(", ", column.Select(rt => rt.name).ToArray())}).");
                }
            }
            ModManagerPlugin.Log.LogInfo($"Added the Mods button to the pause menu (under {source.name}, in {parent.name}{(parent.GetComponent<LayoutGroup>() != null ? ", layout group" : ", placed by position")}).");
        }
    }

    /// <summary>A "Mods" button cloned from the game's own Settings button, so it matches the menu exactly.</summary>
    internal static class MenuButton
    {
        private const string ObjectName = "NomadModManager_ModsButton";
        private static TMP_Text _label;
        private static string _base = "Mods";
        private static string _shown;

        public static void Add(MainMenuPanel panel)
        {
            var source = Traverse.Create(panel).Field("settingsButton").GetValue<Button>()
                         ?? Traverse.Create(panel).Field("quitButton").GetValue<Button>();
            if (source == null) { ModManagerPlugin.Log.LogWarning("Main menu has no Settings button to copy."); return; }
            var parent = source.transform.parent;
            if (parent.Find(ObjectName) != null) return;

            var go = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            go.name = ObjectName;
            go.transform.SetSiblingIndex(source.transform.GetSiblingIndex()); // just above Settings

            _label = Relabel(go, source);
            var original = source.GetComponentInChildren<TMP_Text>(true)?.text ?? "";
            _base = original.Length > 0 && original == original.ToUpperInvariant() ? "MODS" : "Mods";
            _shown = null;
            UpdateBadge(ModManagerPlugin.I.Catalog);

            // The menu finishes laying itself out after Start, so make room a few frames later.
            go.SetActive(false);
            ModManagerPlugin.I.StartCoroutine(MakeRoomLater(panel, source, go));

            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => ModManagerPlugin.I.Menu.Open(panel));
            button.interactable = true;
            LinkNavigation(button, source, below: false);
            ModManagerPlugin.Log.LogInfo("Added the Mods button to the main menu.");
        }

        /// <summary>Drops the copied label's localization (so it keeps our text) and says "Mods". Returns the label.</summary>
        internal static TMP_Text Relabel(GameObject go, Button source)
        {
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (c != null && c.GetType().Name.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.DestroyImmediate(c);
            var label = go.GetComponentInChildren<TMP_Text>(true);
            var original = source.GetComponentInChildren<TMP_Text>(true)?.text ?? "";
            if (label != null) label.text = original.Length > 0 && original == original.ToUpperInvariant() ? "MODS" : "Mods";
            return label;
        }

        /// <summary>
        /// Puts the new button into the menu's controller/keyboard navigation next to <paramref name="source"/>.
        /// A copy keeps Settings' explicit links, so nothing would lead to it otherwise.
        /// </summary>
        internal static void LinkNavigation(Button mods, Button source, bool below)
        {
            var nav = source.navigation;
            if (nav.mode != Navigation.Mode.Explicit) return; // automatic navigation finds it by position
            var mine = nav;
            if (below)
            {
                var next = nav.selectOnDown;
                mine.selectOnUp = source; mine.selectOnDown = next;
                nav.selectOnDown = mods; source.navigation = nav;
                if (next != null) { var n = next.navigation; if (n.mode == Navigation.Mode.Explicit) { n.selectOnUp = mods; next.navigation = n; } }
            }
            else
            {
                var prev = nav.selectOnUp;
                mine.selectOnUp = prev; mine.selectOnDown = source;
                nav.selectOnUp = mods; source.navigation = nav;
                if (prev != null) { var n = prev.navigation; if (n.mode == Navigation.Mode.Explicit) { n.selectOnDown = mods; prev.navigation = n; } }
            }
            mods.navigation = mine;
        }

        /// <summary>Where each moved button should be, and where the game first put it (to spot the game resetting it).</summary>
        private static readonly List<(RectTransform Rt, Vector3 Original, Vector3 Target)> Moved = new List<(RectTransform, Vector3, Vector3)>();

        /// <summary>
        /// The menu places its buttons by position, not with a layout group, so a new button would sit on top of Settings.
        /// Puts Mods where Settings was and moves Settings and every button under it down one row.
        /// </summary>
        private static System.Collections.IEnumerator MakeRoomLater(MainMenuPanel panel, Button settings, GameObject mods)
        {
            for (var i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
            if (panel == null || mods == null) yield break;
            try { Canvas.ForceUpdateCanvases(); MakeRoom(panel, settings, mods); }
            catch (Exception e) { ModManagerPlugin.Log.LogError("Couldn't make room for the Mods button: " + e); }
            mods.SetActive(true);
        }

        private static void MakeRoom(MainMenuPanel panel, Button settings, GameObject mods)
        {
            Moved.Clear();
            var parent = settings.transform.parent;
            if (parent.GetComponent<LayoutGroup>() != null) return; // a layout group already makes room

            var setRt = (RectTransform)settings.transform;
            var setY = CenterY(setRt);

            // The menu's own buttons, plus anything else clickable lined up under them.
            var tr = Traverse.Create(panel);
            var known = new[] { "singlePlayerButton", "multiplayerButton", "joinButton", "loadGameButton", "settingsButton", "reportProblemButton", "quitButton" }
                .Select(f => tr.Field(f).GetValue<Button>()).Where(b => b != null).ToList();
            float leftEdge = float.MaxValue, rightEdge = float.MinValue; // (Enumerable.Min/Max on floats are stripped from the game)
            foreach (var b in known)
            {
                var c = Corners((RectTransform)b.transform);
                leftEdge = Mathf.Min(leftEdge, c[0].x);
                rightEdge = Mathf.Max(rightEdge, c[2].x);
            }
            var extra = panel.GetComponentsInChildren<Button>(false)
                .Where(b => !known.Contains(b) && b.gameObject != mods)
                .Where(b => { var c = Corners((RectTransform)b.transform); return c[2].x > leftEdge && c[0].x < rightEdge; });
            foreach (var b in known.Concat(extra))
            {
                var rt = (RectTransform)b.transform;
                ModManagerPlugin.Log.LogInfo($"  menu item {b.name}: y {CenterY(rt):0}, parent {rt.parent?.name}{(rt.parent?.GetComponent<LayoutGroup>() != null ? " (layout)" : "")}, active {b.gameObject.activeInHierarchy}");
            }
            var column = known.Concat(extra).Where(b => b != settings && b.gameObject != mods).Select(b => (RectTransform)b.transform).ToList();

            // Row height: the gap to the closest button above Settings (or below, if there's none above).
            var above = column.Where(rt => CenterY(rt) > setY + 1f).OrderBy(CenterY).FirstOrDefault();
            var below = column.Where(rt => CenterY(rt) < setY - 1f).OrderByDescending(CenterY).FirstOrDefault();
            float step;
            if (above != null) step = CenterY(above) - setY;
            else if (below != null) step = setY - CenterY(below);
            else step = setRt.rect.height * setRt.lossyScale.y * 1.1f;

            ((RectTransform)mods.transform).position = setRt.position;

            // Move Settings and everything under it. A button inside its own layout container is moved by its container.
            var toMove = new List<RectTransform>();
            foreach (var rt in column.Where(rt => CenterY(rt) < setY - 1f).Concat(new[] { setRt }))
            {
                var target = rt;
                var p = rt.parent as RectTransform;
                if (p != null && p != parent && p.GetComponent<LayoutGroup>() != null) target = p;
                if (!toMove.Contains(target)) toMove.Add(target);
            }
            foreach (var rt in toMove)
            {
                var original = rt.localPosition;
                rt.position = rt.position - new Vector3(0, step, 0);
                Moved.Add((rt, original, rt.localPosition));
            }
            ModManagerPlugin.Log.LogInfo($"Made room for Mods: moved {toMove.Count} item(s) down {step:0} px.");
        }

        private static Vector3[] Corners(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            return c;
        }

        private static float CenterY(RectTransform rt)
        {
            var c = Corners(rt);
            return (c[0].y + c[1].y) / 2f;
        }

        /// <summary>If the game puts a moved button back (e.g. when the menu is shown again), move it again.</summary>
        public static void KeepLayout()
        {
            foreach (var (rt, original, target) in Moved)
                if (rt != null && (rt.localPosition - original).sqrMagnitude < 0.01f) rt.localPosition = target;
        }

        internal static TMP_Text PauseLabel;
        private static string _pauseShown;

        /// <summary>"Mods (2)" on both Mods buttons when updates are waiting.</summary>
        public static void UpdateBadge(ModCatalog catalog, bool force = false)
        {
            var n = catalog.UpdateCount;
            var text = n > 0 ? $"{_base} ({n})" : _base;
            if (PauseLabel != null && (force || text != _pauseShown))
            {
                var orig = PauseLabel.text ?? "";
                var upper = orig.Length > 0 && orig == orig.ToUpperInvariant();
                PauseLabel.text = upper ? text.ToUpperInvariant() : text;
                _pauseShown = text;
            }
            if (_label == null) return;
            if (text == _shown) return;
            _label.text = text;
            _shown = text;
        }
    }
}
