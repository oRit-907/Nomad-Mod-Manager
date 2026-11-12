using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;
using Newtonsoft.Json.Linq;

namespace NomadModManager
{
    internal enum ModState
    {
        Running,          // loaded at startup
        StartedNow,       // hot-loaded from the Mods menu, no restart
        Disabled,
        Settling,         // just appeared, waiting for the files to finish copying
        NeedsRestart,     // installed, but can't start until the game restarts
        WillDisable,      // queued for next launch
        WillEnable,
        WillRemove,
        Library,          // support DLLs only
        NotLoaded,        // has DLLs that BepInEx didn't load (see the log)
        Failed            // hot-load threw
    }

    internal sealed class ModEntry
    {
        public string Rel = "";            // BepInEx/plugins/<entry>
        public string Full = "";
        public bool IsFolder;
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Summary = "";
        public string Category = Categories.Misc;
        public ModState State;
        public string Note = "";
        public bool UpdateStaged;          // a newer copy is waiting for the next launch
        public bool InUse;                 // one of its DLLs is loaded in the game
        public bool LiveLater;             // its waiting change applies by itself at the main menu, no restart
        public bool IsSelf;
        public int NexusId;
        public string NexusVersion = "";     // the newest version where it came from (Nexus or the website)
        public int SiteId, SiteVersionId;    // installed from the community website (nomaddrivemods.online)
        public int SiteLatestId;
        public string SiteInstalledVersion = "";            // the website version that was installed (its DLL may say otherwise)
        public string UpdateFrom = "Nexus";
        // Both places a mod can come from, checked against each other:
        public string SiteLatest = "", NexusLatest = "";   // newest version on the website / on Nexus ("" = not there or not checked yet)
        public int AltSiteId;                              // installed from Nexus, but also on the website
        public bool UpdateFromOther;                       // the newer version is on the other site than the one it was installed from
        private bool _updateAvailable;
        /// <summary>A newer version is out. Never for BepInEx's own parts: those are kept as they are (see IsLoader).</summary>
        public bool UpdateAvailable { get => _updateAvailable && !IsLoader; set => _updateAvailable = value; }

        /// <summary>
        /// Part of BepInEx itself (its compatibility layers, BepInExPack): the mod loader everything else runs on.
        /// It can't be switched off, removed or updated from the menu, since that would break every mod at once.
        /// </summary>
        public bool IsLoader => IsLoaderName(Name) || IsLoaderName(System.IO.Path.GetFileName(Full)) || NexusId == 7;

        public static bool IsLoaderName(string n)
        {
            n = (n ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").Replace(".", "");
            return n.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) || n.IndexOf("UnityCompat", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase) || n.IndexOf("BepInExPack", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public readonly List<string> Dlls = new List<string>();
        public readonly List<string> OffDlls = new List<string>();
        public readonly List<string> Guids = new List<string>();

        public bool CanRestartFix => !LiveLater && (State == ModState.NeedsRestart || State == ModState.WillDisable || State == ModState.WillEnable || State == ModState.WillRemove || State == ModState.Failed || UpdateStaged);
    }

    /// <summary>Everything in BepInEx\plugins, what state it's in, and the hot-load bookkeeping.</summary>
    internal sealed class ModCatalog
    {
        public List<ModEntry> Mods { get; private set; } = new List<ModEntry>();
        public event Action<string, bool> Toast; // message, good

        private readonly HashSet<string> _startupDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HotVerdict> _verdicts = new Dictionary<string, HotVerdict>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hotLoaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // dll -> error ("" = ok)
        private readonly Dictionary<string, (long Size, DateTime Time, int Seen)> _settle = new Dictionary<string, (long, DateTime, int)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (DateTime Time, string Name, string Version)> _meta = new Dictionary<string, (DateTime, string, string)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, (string Version, string Author, string Name, string Summary)> _nexus = new Dictionary<int, (string, string, string, string)>();
        private readonly Dictionary<int, SiteMod> _site = new Dictionary<int, SiteMod>();
        private JArray _downloaderManifest;
        private DateTime _manifestTime;

        public bool AutoStart = true;

        // Live on/off (mods with [assembly: AssemblyMetadata("NomadHotLoad", "true")])
        private readonly Dictionary<string, (DateTime Time, bool Plugin, bool Live)> _inspect = new Dictionary<string, (DateTime, bool, bool)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stoppedLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // stopped now, file switched off at next launch
        private readonly HashSet<string> _ranThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // mod entries that were loaded at some point
        private readonly Dictionary<string, DateTime> _swappedStaged = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); // staged update already running live
        public string GameRoot => Paths.GameRootPath;

        public void SnapshotStartup()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
                    _startupDlls.Add(Path.GetFullPath(f));
            }
            catch { }
        }

        public IEnumerable<int> NexusIds => Mods.Where(m => m.NexusId > 0).Select(m => m.NexusId).Distinct();
        public IEnumerable<int> SiteIds => Mods.Where(m => m.SiteId > 0).Select(m => m.SiteId).Distinct();
        /// <summary>For finding the website's copies of mods installed from Nexus: their Nexus ids, and "name~author" keys.</summary>
        public IEnumerable<int> NexusOnlyIds => Mods.Where(m => m.SiteId == 0 && m.NexusId > 0 && !m.IsSelf).Select(m => m.NexusId).Distinct();
        public IEnumerable<string> NexusOnlyNames => Mods.Where(m => m.SiteId == 0 && !m.IsSelf && m.Author.Length > 0 && m.Author != UnknownAuthor)
                                                         .Select(m => NameKey(m.Name) + "~" + NameKey(m.Author)).Where(k => k.Length > 4 && !k.EndsWith("~")).Distinct();
        public bool KnowsNexus(int id) => _nexus.ContainsKey(id);
        /// <summary>Look at new DLLs again (another copy of the same mod was just removed, so one that was blocked may start now).</summary>
        public void ForgetVerdicts() => _verdicts.Clear();
        public bool IsSiteInstalled(int siteId) => Mods.Any(m => m.SiteId == siteId && m.State != ModState.WillRemove);
        public ModEntry SiteEntry(int siteId) => Mods.FirstOrDefault(m => m.SiteId == siteId && m.State != ModState.WillRemove);
        public void SetSiteInfo(SiteMod m) { if (m != null && m.Id > 0) _site[m.Id] = m; }
        public bool IsInstalled(int nexusId) => Mods.Any(m => m.NexusId == nexusId && m.State != ModState.WillRemove);

        public const string UnknownAuthor = "Unknown author";

        /// <summary>Nexus mods seen while browsing, by simplified name, so mods added by hand can be matched to their page.</summary>
        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>();

        public static string NameKey(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in name ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        public void LearnNames(IEnumerable<NexusMod> mods)
        {
            foreach (var m in mods)
            {
                var k = NameKey(m.Name);
                if (k.Length >= 4 && !_byName.ContainsKey(k)) _byName[k] = m.Id;
            }
        }

        public void SetNexusInfo(int id, string version, string author, string name, string summary = null)
        {
            var keep = summary ?? (_nexus.TryGetValue(id, out var old) ? old.Summary : "");
            _nexus[id] = (version ?? "", author ?? "", name ?? "", keep ?? "");
        }

        public int RestartItems => Mods.Count(m => m.CanRestartFix && !IsHotReady(m));
        /// <summary>Changes the Reload button can do now. In an online game that's every mod except ones that change multiplayer.</summary>
        public int ReloadItems => Mods.Count(m => (m.LiveLater || IsHotReady(m)) && !OnlineHold(m));
        /// <summary>Waiting changes to mods that change multiplayer: in an online game they happen back at the main menu.</summary>
        public int HeldOnline => Mods.Count(m => (m.LiveLater || IsHotReady(m)) && OnlineHold(m));
        public static bool IsHotReady(ModEntry m) => m.State == ModState.NeedsRestart && (m.Note.StartsWith("Starting") || m.Note == "Ready to start");
        public int UpdateCount => Mods.Count(m => m.UpdateAvailable && !m.UpdateStaged);

        /// <summary>
        /// Both places mods come from (the website and Nexus) have answered at least once. Until then no update is offered,
        /// so a mod on both is only offered when its versions have been compared on both.
        /// </summary>
        public bool UpdatesChecked;

        // ------------------------------------------------------------------
        // Scan

        public void Refresh()
        {
            ReadDownloaderManifest();
            var pending = Pending.Read(GameRoot);
            var staged = Pending.StagedFiles(GameRoot).Select(Path.GetFullPath).ToList();
            var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) loadedPaths.Add(Path.GetFullPath(a.Location)); } catch { }
            }

            var list = new List<ModEntry>();
            if (!Directory.Exists(Paths.PluginPath)) { Mods = list; return; }
            var fromSite = Community.Installed();

            foreach (var entry in Directory.GetFileSystemEntries(Paths.PluginPath))
            {
                var full = Path.GetFullPath(entry);
                var isDir = Directory.Exists(full);
                var name = Path.GetFileName(full);
                if (!isDir)
                {
                    if (name.EndsWith(".dll" + Pending.OffSuffix, StringComparison.OrdinalIgnoreCase)) full = full.Substring(0, full.Length - Pending.OffSuffix.Length);
                    else if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Any(m => m.Full.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
                }

                var m = new ModEntry { Full = full, IsFolder = isDir, Rel = "BepInEx/plugins/" + Path.GetFileName(full) };
                var files = isDir ? SafeFiles(full) : new List<string> { full, full + Pending.OffSuffix }.Where(File.Exists).ToList();
                m.Dlls.AddRange(files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
                m.OffDlls.AddRange(files.Where(f => f.EndsWith(".dll" + Pending.OffSuffix, StringComparison.OrdinalIgnoreCase)));
                if (m.Dlls.Count == 0 && m.OffDlls.Count == 0) continue;
                m.UpdateStaged = staged.Any(s => (isDir ? s.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                        : s.StartsWith(full, StringComparison.OrdinalIgnoreCase)) && !AlreadyLive(s));
                m.InUse = m.Dlls.Concat(m.OffDlls.Select(d => d.Substring(0, d.Length - Pending.OffSuffix.Length))).Any(d => loadedPaths.Contains(d));

                // Plugins running from this entry
                foreach (var info in Bep.Plugins.Values)
                {
                    if (info?.Location == null || info.Instance == null) continue;
                    var loc = Path.GetFullPath(info.Location);
                    if (loc.Equals(full, StringComparison.OrdinalIgnoreCase) || loc.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        m.Guids.Add(info.Metadata.GUID);
                        if (m.Name.Length == 0) { m.Name = info.Metadata.Name; m.Version = info.Metadata.Version.ToString(); }
                    }
                }
                // ...and the ones BepInEx 6 support started (mods built for BepInEx 6)
                foreach (var o in Bep.OtherPlugins)
                {
                    if (!o.Running || o.Location.Length == 0 || m.Guids.Contains(o.Guid)) continue;
                    string loc;
                    try { loc = Path.GetFullPath(o.Location); } catch { continue; }
                    if (loc.Equals(full, StringComparison.OrdinalIgnoreCase) || loc.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        m.Guids.Add(o.Guid);
                        if (m.Name.Length == 0) { m.Name = o.Name; m.Version = o.Version; }
                    }
                }
                if (m.Name.Length == 0) ReadMeta(m);
                if (m.Name.Length == 0) m.Name = Path.GetFileNameWithoutExtension(full);
                m.IsSelf = m.Guids.Contains(ModManagerPlugin.Guid);

                var site = Community.OwnerOf(fromSite, m.Rel);
                if (site != null)
                {
                    // Installed from the community website: updates come from there.
                    m.SiteId = site.Id; m.SiteVersionId = site.VersionId; m.UpdateFrom = Community.HostName;
                    m.SiteInstalledVersion = site.Version ?? "";
                    if (m.Version.Length == 0) m.Version = site.Version;
                    if (site.Author.Length > 0) m.Author = site.Author;
                    if (_site.TryGetValue(site.Id, out var latest))
                    {
                        m.SiteLatestId = latest.VersionId;
                        m.NexusVersion = latest.Version;
                        m.Summary = latest.Summary;
                        m.UpdateAvailable = Community.SiteNewer(latest.VersionId, latest.Version, site.VersionId, m.Version);
                    }
                }
                else MatchDownloader(m);
                if (m.SiteId == 0 && m.NexusId == 0 && _byName.TryGetValue(NameKey(m.Name), out var byName)) m.NexusId = byName;
                if (m.IsSelf && m.NexusId == 0) m.NexusId = Nexus.DownloaderModId; // this menu ships on Nomad Mod Downloader's page
                if (m.SiteId == 0 && m.NexusId > 0 && _nexus.TryGetValue(m.NexusId, out var nx))
                {
                    m.NexusVersion = nx.Version;
                    if (nx.Author.Length > 0) m.Author = nx.Author;
                    m.Summary = nx.Summary;
                    // The page's version is the downloader's, not this menu's: the downloader checks this menu's file itself.
                    m.UpdateAvailable = !m.IsSelf && IsNewer(nx.Version, m.Version);
                }
                if (!m.IsSelf) CrossCheck(m);
                if (!UpdatesChecked) m.UpdateAvailable = false;   // (not until both checks are back)
                m.Category = m.IsSelf ? "Tools & Frameworks"
                    : m.SiteId > 0 && _site.TryGetValue(m.SiteId, out var sm) ? sm.Category
                    : m.NexusId > 0 && _nexus.TryGetValue(m.NexusId, out var nn) && nn.Name.Length > 0 ? Categories.Classify(nn.Name, nn.Summary)
                    : Categories.Classify(m.Name, string.Join(" ", m.Guids.ToArray()));

                // State
                var op = pending.FirstOrDefault(o => o.Value.Equals(m.Rel, StringComparison.OrdinalIgnoreCase)).Key;
                if (m.InUse) _ranThisSession.Add(full);
                if (_stoppedLive.Contains(full) && m.Guids.Count == 0) { m.State = ModState.Disabled; m.Note = "Off now — no restart needed"; }
                else if (op == "remove") m.State = ModState.WillRemove;
                else if (op == "disable") m.State = ModState.WillDisable;
                else if (op == "enable") m.State = ModState.WillEnable;
                else if (m.Guids.Count > 0)
                {
                    var hot = m.Dlls.Any(d => _hotLoaded.TryGetValue(d, out var err) && err.Length == 0);
                    m.State = hot ? ModState.StartedNow : ModState.Running;
                    if (m.Dlls.Count == 0 && m.OffDlls.Count > 0) m.State = ModState.WillDisable; // renamed by hand while running
                }
                else if (m.Dlls.Count == 0) m.State = ModState.Disabled;
                else ClassifyNotRunning(m);

                if (m.State == ModState.WillDisable || m.State == ModState.WillEnable || m.State == ModState.WillRemove || m.UpdateStaged)
                {
                    var why = _stoppedLive.Contains(full) ? "" : LiveBlocker(m, stopping: m.State == ModState.WillDisable || m.State == ModState.WillRemove);
                    m.LiveLater = why == null || why == AtMenu;
                }
                list.Add(m);
            }

            Mods = list.OrderBy(x => Categories.Rank(x.Category)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A mod with DLLs that BepInEx isn't running: brand new (hot-load candidate), a library, or something that failed at startup.</summary>
        private void ClassifyNotRunning(ModEntry m)
        {
            var fresh = m.Dlls.Where(d => !_startupDlls.Contains(d)).ToList();
            if (fresh.Count == 0)
            {
                var anyPlugin = m.Dlls.Select(GetVerdict).Any(v => v != null && !v.NotAPlugin);
                m.State = anyPlugin ? ModState.NotLoaded : ModState.Library;
                m.Note = anyPlugin ? "BepInEx didn't start it — check BepInEx\\LogOutput.log" : "Support files only";
                return;
            }

            foreach (var d in fresh)
            {
                if (_hotLoaded.TryGetValue(d, out var err) && err.Length > 0) { m.State = ModState.Failed; m.Note = err; return; }
            }
            if (fresh.Any(d => !Settled(d))) { m.State = ModState.Settling; m.Note = "Finishing install…"; return; }

            var verdicts = fresh.Select(GetVerdict).Where(v => v != null).ToList();
            var plugins = verdicts.Where(v => !v.NotAPlugin).ToList();
            if (plugins.Count == 0) { m.State = ModState.Library; m.Note = "Support files only"; return; }
            if (plugins.All(v => v.WrongGame)) { m.State = ModState.NotLoaded; m.Note = "Made for a different game"; return; }
            var blocked = plugins.FirstOrDefault(v => !v.CanLoad && !v.WrongGame);
            if (blocked != null) { m.State = ModState.NeedsRestart; m.Note = blocked.Reason; return; }
            m.State = ModState.NeedsRestart;
            m.Note = AutoStart ? "Starting…" : "Ready to start";
        }

        /// <summary>Starts new mods that can be loaded without a restart. Called from the poll loop.</summary>
        public void StartNewMods(bool force = false, bool liveOnly = false)
        {
            if (!AutoStart && !force) return;
            foreach (var m in Mods.Where(x => x.State == ModState.NeedsRestart && (x.Note.StartsWith("Starting") || x.Note == "Ready to start")
                                              && (!liveOnly || CanGoLive(x))).ToList())
                TryStart(m);
        }

        /// <summary>
        /// In an online game, mods that change what's sent between players aren't started, stopped or swapped mid-session:
        /// the other players would suddenly be out of step and get dropped. They change when you're back at the main menu.
        /// </summary>
        public bool OnlineHold(ModEntry m) => LobbyMods.InLobby && !(AtMainMenu?.Invoke() ?? false) && NetCheck.AffectsCached(m);

        public void TryStart(ModEntry m)
        {
            if (OnlineHold(m)) { m.Note = "Starting when you leave the online game (it changes multiplayer)"; return; }
            var fresh = m.Dlls.Where(d => !_startupDlls.Contains(d) && !_hotLoaded.ContainsKey(d)).ToList();
            var verdicts = fresh.Select(GetVerdict).Where(v => v != null && v.CanLoad).ToList();
            var started = new List<string>();
            foreach (var v in verdicts)
            {
                try
                {
                    started.AddRange(v.Fresh || Inspect(v.Dll).Live ? HotLoader.LoadFresh(new[] { (v.Dll, v.Dll) }) : HotLoader.Load(v));
                    _ranThisSession.Add(m.Full);
                    _hotLoaded[v.Dll] = "";
                }
                catch (Exception e)
                {
                    var msg = e.InnerException?.Message ?? e.Message;
                    _hotLoaded[v.Dll] = "Couldn't start: " + msg;
                    ModManagerPlugin.Log.LogError($"Hot-load of {Path.GetFileName(v.Dll)} failed: {e}");
                    Toast?.Invoke($"{m.Name} couldn't start now — it will start after a restart.", false);
                }
            }
            foreach (var d in fresh) _startupDlls.Remove(d);
            if (started.Count > 0)
            {
                ModManagerPlugin.Log.LogMessage("Started without a restart: " + string.Join(", ", started.ToArray()));
                Toast?.Invoke($"{m.Name} is on — no restart needed.", true);
            }
            Refresh();
        }

        // ------------------------------------------------------------------
        // Actions

        private bool Protected(ModEntry m, string what)
        {
            if (!m.IsLoader) return false;
            Toast?.Invoke($"{m.Name} is part of BepInEx, which every mod runs on, so it can't be {what} here. To repair or update BepInEx, " + (Edition.Nexus ? "reinstall BepInEx for Nomad Drive from Nexus Mods." : "use Check BepInEx.") + "", false);
            return true;
        }

        public void Disable(ModEntry m)
        {
            if (Protected(m, "switched off")) return;
            SaveBackups.Create("before switching off " + m.Name, true);
            Activity.Add("Switched off " + m.Name);
            if (m.State == ModState.WillEnable) { Pending.Cancel(GameRoot, "enable", m.Rel); Refresh(); return; }
            if (m.Guids.Count > 0)
            {
                var why = LiveBlocker(m, stopping: true);
                if (why == null) { DisableLive(m); Refresh(); return; }
                Pending.Add(GameRoot, "disable", m.Rel);
                Toast?.Invoke(why == AtMenu ? $"{m.Name} turns off when you go to the main menu — no restart needed." : $"{m.Name} turns off when you restart ({why}).", true);
            }
            else if (m.InUse)
            {
                Pending.Add(GameRoot, "disable", m.Rel);
                Toast?.Invoke($"{m.Name} turns off when you restart.", true);
            }
            else
            {
                try { Pending.SetEnabled(m.Full, false); }
                catch { Pending.Add(GameRoot, "disable", m.Rel); }
            }
            Refresh();
        }

        public void Enable(ModEntry m)
        {
            SaveBackups.Create("before switching on " + m.Name, true);
            Activity.Add("Switched on " + m.Name);
            if (_stoppedLive.Remove(m.Full))
            {
                // Stopped live but its file couldn't be switched off: just start it again.
                Pending.Cancel(GameRoot, "disable", m.Rel);
                Refresh();
                StartLive(Find(m) ?? m);
                return;
            }
            if (m.State == ModState.WillDisable) { Pending.Cancel(GameRoot, "disable", m.Rel); Refresh(); return; }
            if (m.InUse || _ranThisSession.Contains(m.Full))
            {
                // Ran earlier this session: a fresh copy starts in its place.
                var why = LiveBlocker(m);
                if (why == null)
                {
                    try { Pending.SetEnabled(m.Full, true); }
                    catch { Pending.Add(GameRoot, "enable", m.Rel); Toast?.Invoke($"{m.Name} turns on when you restart.", true); Refresh(); return; }
                    Refresh();
                    StartLive(Find(m) ?? m);
                    return;
                }
                Pending.Add(GameRoot, "enable", m.Rel);
                Toast?.Invoke(why == AtMenu ? $"{m.Name} turns on when you go to the main menu — no restart needed." : $"{m.Name} turns on when you restart ({why}).", true);
                Refresh();
                return;
            }
            try
            {
                Pending.SetEnabled(m.Full, true);
                foreach (var d in m.OffDlls) { var on = d.Substring(0, d.Length - Pending.OffSuffix.Length); _startupDlls.Remove(on); MarkSettled(on); }
            }
            catch { Pending.Add(GameRoot, "enable", m.Rel); }
            Refresh();
            var now = Mods.FirstOrDefault(x => x.Full.Equals(m.Full, StringComparison.OrdinalIgnoreCase));
            if (now != null && now.State == ModState.NeedsRestart && now.Note.StartsWith("Starting")) TryStart(now);
            else if (now != null && now.State == ModState.NeedsRestart) Toast?.Invoke($"{m.Name} needs a restart to turn on ({now.Note}).", false);
        }

        public void Remove(ModEntry m)
        {
            if (Protected(m, "removed")) return;
            SaveBackups.Create("before removing " + m.Name, true);
            Activity.Add("Removed " + m.Name);
            if (m.Guids.Count > 0 || m.InUse)
            {
                var why = m.Guids.Count > 0 ? LiveBlocker(m, stopping: true) : "its files are in use";
                if (why == null) { RemoveLive(m); Refresh(); return; }
                Pending.Add(GameRoot, "remove", m.Rel);
                Toast?.Invoke(why == AtMenu ? $"{m.Name} is removed when you go to the main menu — no restart needed." : $"{m.Name} is removed when you restart.", true);
            }
            else DeleteFiles(m);
            Refresh();
        }

        public void UndoRemove(ModEntry m)
        {
            Pending.Cancel(GameRoot, "remove", m.Rel);
            Activity.Add("Kept " + m.Name + " (undid removing it)");
            Refresh();
        }

        private void DeleteFiles(ModEntry m)
        {
            try
            {
                if (m.IsFolder) Directory.Delete(m.Full, true);
                foreach (var f in new[] { m.Full, m.Full + Pending.OffSuffix }) if (File.Exists(f)) File.Delete(f);
                Toast?.Invoke($"Removed {m.Name}.", true);
            }
            catch { Pending.Add(GameRoot, "remove", m.Rel); Toast?.Invoke($"{m.Name} is removed when you restart.", true); }
        }

        /// <summary>The game holds this file open (it was loaded from disk at launch), so it can't be deleted or replaced now.</summary>
        private static bool Locked(string file)
        {
            try { using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (FileNotFoundException) { return false; }
            catch { return true; }
        }

        // ------------------------------------------------------------------
        // Refresh instead of restart
        //
        // Switching a mod off, on, removing it or updating it happens in the running game:
        // - mods that say they support it ([assembly: AssemblyMetadata("NomadHotLoad", "true")]): straight away, anywhere;
        // - other mods: at the main menu (the world isn't loaded, so they set up the same as at launch).
        //   Changes made during play wait there and apply by themselves when you go back to the main menu.
        // Only a few things still need a real restart: the Mods menu itself, BepInEx 5 mods running through
        // BepInEx 5 Compatibility, preloader patchers and mods that patch the game's startup code.

        public const string AtMenu = "at the main menu";

        /// <summary>Set by the plugin: is the main menu showing?</summary>
        public Func<bool> AtMainMenu = () => false;

        private ModEntry Find(ModEntry m) => Mods.FirstOrDefault(x => x.Full.Equals(m.Full, StringComparison.OrdinalIgnoreCase));

        private (bool Plugin, bool Live) Inspect(string dll)
        {
            DateTime t;
            try { t = File.GetLastWriteTimeUtc(dll); } catch { return (false, false); }
            if (_inspect.TryGetValue(dll, out var c) && c.Time == t) return (c.Plugin, c.Live);
            var r = HotLoader.Inspect(dll);
            _inspect[dll] = (t, r.Plugin, r.Live);
            return r;
        }

        private readonly Dictionary<string, (DateTime Time, string Why)> _blockers = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private string Blocker(string dll)
        {
            DateTime t;
            try { t = File.GetLastWriteTimeUtc(dll); } catch { return null; }
            if (_blockers.TryGetValue(dll, out var c) && c.Time == t) return c.Why;
            var why = HotLoader.RefreshBlocker(dll);
            _blockers[dll] = (t, why);
            return why;
        }

        /// <summary>Every plugin DLL of the mod says it can be stopped and started any time.</summary>
        public bool CanGoLive(ModEntry m) => LiveBlocker(m, anywhereOnly: true) == null;

        /// <summary>
        /// Null when the change can happen right now; <see cref="AtMenu"/> when it can happen without a restart
        /// once you're at the main menu; otherwise why it needs a restart.
        /// </summary>
        public string LiveBlocker(ModEntry m, bool stopping = false, bool anywhereOnly = false)
        {
            if (m.IsSelf) return "the Mods menu itself";
            if (OnlineHold(m)) return AtMenu;
            var other = Bep.OtherDict(out _);
            if (m.Guids.Any(g => !Bep.Plugins.ContainsKey(g) && (other == null || !other.Contains(g)))) return "started by another loader";
            var dlls = m.Dlls.Concat(m.OffDlls).ToList();
            var staged = dlls.Select(d => d + Pending.NewSuffix).Where(File.Exists).ToList();
            var plugins = dlls.Concat(staged).Where(d => Inspect(d).Plugin).ToList();
            if (plugins.Count == 0) return m.InUse ? "its files are in use" : "Support files only";
            if (stopping)
            {
                var needs = DependentsOf(m, hardOnly: true).Select(x => x.Name).ToList();
                if (needs.Count > 0) return "needed by " + string.Join(", ", needs.ToArray());
            }
            if (plugins.All(d => Inspect(d).Live)) return null;
            if (anywhereOnly) return AtMenu;
            // A mod that patches startup code (Awake, Start…) can still be reloaded if it ran at launch: those patches
            // already did their job then. Turning one on that never ran this session needs a real restart.
            var ranBefore = m.InUse || m.Guids.Count > 0 || _ranThisSession.Contains(m.Full);
            var why = plugins.Select(Blocker).FirstOrDefault(x => x != null && !(ranBefore && x.StartsWith("Changes game startup code")));
            if (why != null) return why;
            return AtMainMenu() || _reloading ? null : AtMenu;
        }

        private bool _reloading;

        /// <summary>
        /// The Reload button: does everything that's waiting right now, without a restart — switch-offs, switch-ons,
        /// removals, updates and new mods — even in the middle of a drive. Returns how many changes still need a real restart.
        /// </summary>
        public int ReloadNow()
        {
            _reloading = true;
            try
            {
                Refresh();
                StartNewMods(force: true);
                ApplyWaiting();
                Refresh();
            }
            finally { _reloading = false; }
            var left = RestartItems;
            var held = HeldOnline;
            var msg = left == 0 ? "Mods reloaded — no restart needed." : $"Mods reloaded. {left} change(s) still need a restart.";
            if (held > 0) msg += $" {held} multiplayer mod(s) change when you're back at the main menu.";
            Toast?.Invoke(msg, left == 0);
            return left;
        }

        /// <summary>Running mods that list one of this mod's plugins as a dependency.</summary>
        private List<ModEntry> DependentsOf(ModEntry m, bool hardOnly)
        {
            var mine = new HashSet<string>(m.Guids);
            if (mine.Count == 0) return new List<ModEntry>();
            return Mods.Where(x => x != m && x.Guids.Count > 0 && x.Guids.Any(g =>
                Bep.Plugins.TryGetValue(g, out var info) && info != null && info.Dependencies.Any(d => mine.Contains(d.DependencyGUID)
                    && (!hardOnly || (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0)))).ToList();
        }

        private void StopLive(ModEntry m)
        {
            _ranThisSession.Add(m.Full);
            var names = HotLoader.Stop(m.Guids);
            foreach (var d in m.Dlls) _hotLoaded.Remove(d);
            if (names.Count > 0) ModManagerPlugin.Log.LogMessage("Stopped without a restart: " + string.Join(", ", names.ToArray()));
        }

        private void DisableLive(ModEntry m)
        {
            StopLive(m);
            Pending.Cancel(GameRoot, "disable", m.Rel);
            try { Pending.SetEnabled(m.Full, false); }
            catch { Pending.Add(GameRoot, "disable", m.Rel); _stoppedLive.Add(m.Full); }
            Toast?.Invoke($"{m.Name} is off — no restart needed.", true);
        }

        private void RemoveLive(ModEntry m)
        {
            StopLive(m);
            Pending.Cancel(GameRoot, "remove", m.Rel);
            if (m.Dlls.Concat(m.OffDlls).Any(Locked))
            {
                // The game holds the DLL open: it's stopped now, the files go at the next launch.
                Pending.Add(GameRoot, "remove", m.Rel);
                _stoppedLive.Add(m.Full);
                Toast?.Invoke($"{m.Name} is off now; its files are deleted next time the game starts.", true);
            }
            else DeleteFiles(m);
        }

        private List<string> StagedFor(ModEntry m) =>
            Pending.StagedFiles(GameRoot).Select(Path.GetFullPath)
                   .Where(s => m.IsFolder ? s.StartsWith(m.Full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                          : s.StartsWith(m.Full, StringComparison.OrdinalIgnoreCase)).ToList();

        private bool AlreadyLive(string staged)
        {
            try { return _swappedStaged.TryGetValue(staged, out var t) && t == File.GetLastWriteTimeUtc(staged); } catch { return false; }
        }

        /// <summary>Starts the plugin DLLs of these mods fresh, in one batch (so mods that use each other are linked up), using waiting updates.</summary>
        private bool StartLive(ModEntry m, bool update = false, List<ModEntry> with = null)
        {
            var mods = new List<ModEntry> { m };
            if (with != null) mods.AddRange(with.Where(x => x != m));
            var items = new List<(string Dll, string From)>();
            foreach (var mod in mods)
                foreach (var d in mod.Dlls)
                {
                    var staged = d + Pending.NewSuffix;
                    var from = File.Exists(staged) ? staged : d;
                    if (Inspect(from).Plugin) items.Add((d, from));
                }
            var started = new List<string>();
            string error = null;
            if (items.Count > 0)
            {
                try
                {
                    started = HotLoader.LoadFresh(items);
                    foreach (var it in items)
                    {
                        _hotLoaded[it.Dll] = "";
                        _startupDlls.Remove(it.Dll);
                        if (it.From != it.Dll) { try { _swappedStaged[it.From] = File.GetLastWriteTimeUtc(it.From); } catch { } }
                    }
                }
                catch (Exception e)
                {
                    error = e.InnerException?.Message ?? e.Message;
                    foreach (var it in items) { _hotLoaded[it.Dll] = "Couldn't start: " + error; _startupDlls.Remove(it.Dll); }
                    ModManagerPlugin.Log.LogWarning("Couldn't start " + m.Name + " without a restart: " + e);
                }
            }
            foreach (var mod in mods) _ranThisSession.Add(mod.Full);
            Refresh();
            if (started.Count == 0) { Toast?.Invoke($"{m.Name} couldn't start now ({error ?? "nothing to start"}). Restart the game to turn it on.", false); return false; }
            ModManagerPlugin.Log.LogMessage((update ? "Updated" : "Started") + " without a restart: " + string.Join(", ", started.ToArray()));
            Toast?.Invoke(update ? $"{m.Name} is updated — no restart needed." : $"{m.Name} is on — no restart needed.", true);
            return true;
        }

        /// <summary>
        /// Applies, without a restart, what's waiting: downloaded updates of running mods, and switch-offs, switch-ons
        /// and removals made during play. Mods that support it go any time; others when the main menu is up.
        /// Called from the poll loop.
        /// </summary>
        public void ApplyWaiting()
        {
            // Updates for running mods.
            foreach (var m in Mods.Where(x => x.UpdateStaged && (x.State == ModState.Running || x.State == ModState.StartedNow)).ToList())
            {
                if (LiveBlocker(m) != null) continue;
                var staged = StagedFor(m);
                if (staged.Count == 0 || staged.Any(s => !Settled(s))) continue;
                var dependents = DependentsOf(m, hardOnly: false).Where(x => LiveBlocker(x) == null).ToList();
                StopLive(m);
                foreach (var dep in dependents) StopLive(dep);
                foreach (var s in staged)
                {
                    var target = s.Substring(0, s.Length - Pending.NewSuffix.Length);
                    try
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(s, target);
                    }
                    catch { } // the game holds it open: the new version runs from memory, the file is swapped at the next launch
                }
                Refresh();
                StartLive(Find(m) ?? m, update: true, with: dependents.Select(d => Find(d) ?? d).ToList());
            }

            // Switch-offs, switch-ons and removals that were waiting for the main menu.
            foreach (var m in Mods.Where(x => x.State == ModState.WillDisable || x.State == ModState.WillEnable || x.State == ModState.WillRemove).ToList())
            {
                if (_stoppedLive.Contains(m.Full)) continue; // already off; only its file waits for the next launch
                if (m.State == ModState.WillEnable && !(m.InUse || _ranThisSession.Contains(m.Full))) continue;
                var why = LiveBlocker(m, stopping: m.State != ModState.WillEnable);
                if (why != null) continue;
                if (m.State == ModState.WillDisable && m.Guids.Count > 0) { DisableLive(m); }
                else if (m.State == ModState.WillRemove && m.Guids.Count > 0) { RemoveLive(m); }
                else if (m.State == ModState.WillEnable)
                {
                    Pending.Cancel(GameRoot, "enable", m.Rel);
                    try { Pending.SetEnabled(m.Full, true); } catch { Pending.Add(GameRoot, "enable", m.Rel); continue; }
                    Refresh();
                    StartLive(Find(m) ?? m);
                }
                else continue;
                Refresh();
            }
        }

        // ------------------------------------------------------------------
        // Helpers

        private HotVerdict GetVerdict(string dll)
        {
            if (_verdicts.TryGetValue(dll, out var v)) return v;
            try { v = HotLoader.Check(dll); }
            catch (Exception e) { v = new HotVerdict { Dll = dll, Reason = "Couldn't inspect: " + e.Message }; }
            _verdicts[dll] = v;
            return v;
        }

        /// <summary>A new DLL counts as settled once its size and time haven't changed for two polls.</summary>
        private bool Settled(string dll)
        {
            try
            {
                var fi = new FileInfo(dll);
                var key = (fi.Length, fi.LastWriteTimeUtc);
                if (_settle.TryGetValue(dll, out var s) && s.Size == key.Length && s.Time == key.LastWriteTimeUtc)
                {
                    if (s.Seen >= 1) return (DateTime.UtcNow - key.LastWriteTimeUtc).TotalSeconds > 1.5;
                    _settle[dll] = (s.Size, s.Time, s.Seen + 1);
                    return false;
                }
                _settle[dll] = (key.Length, key.LastWriteTimeUtc, 0);
                _verdicts.Remove(dll);
                return false;
            }
            catch { return false; }
        }

        private void MarkSettled(string dll) => _settle.Remove(dll);

        private void ReadMeta(ModEntry m)
        {
            var dll = m.Dlls.Concat(m.OffDlls).FirstOrDefault(d => GetVerdictName(d) != null);
            if (dll == null) return;
            var meta = _meta[dll];
            m.Name = meta.Name; m.Version = meta.Version;
        }

        /// <summary>Reads the [BepInPlugin] name/version from a DLL that isn't running (cached by file time).</summary>
        private string GetVerdictName(string dll)
        {
            try
            {
                var t = File.GetLastWriteTimeUtc(dll);
                if (_meta.TryGetValue(dll, out var c) && c.Time == t) return c.Name;
                string name = null, ver = "";
                using (var asm = AssemblyDefinition.ReadAssembly(dll, TypeLoader.ReaderParameters))
                {
                    foreach (var type in asm.MainModule.Types)
                    {
                        var a = type.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == "BepInEx.BepInPlugin" && x.ConstructorArguments.Count == 3);
                        if (a == null) continue;
                        name = a.ConstructorArguments[1].Value as string;
                        ver = a.ConstructorArguments[2].Value as string ?? "";
                        break;
                    }
                }
                _meta[dll] = (t, name, ver);
                return name;
            }
            catch { return null; }
        }

        private void ReadDownloaderManifest()
        {
            // Nomad Mod Downloader's installs, plus the Nexus mods installed from this menu's own browser (nexus.json).
            var all = new JArray();
            try
            {
                var f = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NomadModDownloader"), "installed.json");
                if (File.Exists(f))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (_appManifest == null || t != _manifestTime) { _appManifest = (JArray)JObject.Parse(File.ReadAllText(f))["Mods"]; _manifestTime = t; }
                    foreach (var m in _appManifest ?? new JArray()) all.Add(m.DeepClone());
                }
                else _appManifest = null;
            }
            catch { _appManifest = null; }
            foreach (var m in Community.NexusInstalled()) all.Add(m);
            _downloaderManifest = all.Count > 0 ? all : null;
        }

        private JArray _appManifest;

        private void MatchDownloader(ModEntry m)
        {
            if (_downloaderManifest == null) return;
            foreach (var mod in _downloaderManifest.OfType<JObject>())
            {
                var files = mod["Files"] as JArray;
                if (files == null) continue;
                var hit = files.Select(f => ((string)f ?? "").Replace('\\', '/'))
                               .Any(f => f.Equals(m.Rel, StringComparison.OrdinalIgnoreCase) || f.StartsWith(m.Rel + "/", StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;
                m.NexusId = (int?)mod["ModId"] ?? 0;
                var v = (string)mod["Version"];
                if (!string.IsNullOrEmpty(v) && m.Version.Length == 0) m.Version = v;
                if (m.NexusId > 0 && !string.IsNullOrEmpty(v)) m.Version = v; // compare against what was downloaded, not the DLL's own number
                return;
            }
        }

        private static List<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToList(); }
            catch { return new List<string>(); }
        }

        public static bool IsNewer(string latest, string current) => ModVersion.IsNewer(latest, current);

        /// <summary>
        /// Looks for the same mod on the other site (the website's copy of a Nexus mod, or the Nexus page of a website mod)
        /// and offers whichever is newer. The match is the Nexus page the author linked on the website, or else the same
        /// name by the same author.
        /// </summary>
        private void CrossCheck(ModEntry m)
        {
            SiteMod siteMod = null;
            if (m.SiteId > 0) _site.TryGetValue(m.SiteId, out siteMod);
            else
            {
                var key = NameKey(m.Name);
                var author = NameKey(m.Author);
                siteMod = _site.Values.FirstOrDefault(s => m.NexusId > 0 && s.NexusId == m.NexusId)
                          ?? (author.Length > 0 ? _site.Values.FirstOrDefault(s => NameKey(s.Name) == key && NameKey(s.Author) == author) : null);
                if (siteMod != null) m.AltSiteId = siteMod.Id;
            }
            if (m.NexusId == 0 && siteMod != null && siteMod.NexusId > 0) m.NexusId = siteMod.NexusId; // so the Nexus check looks it up
            m.SiteLatest = siteMod?.Version ?? "";
            m.NexusLatest = m.NexusId > 0 && _nexus.TryGetValue(m.NexusId, out var nx) ? nx.Version : "";

            // What's installed: the higher of the DLL's own number and the version it was installed as (authors don't
            // always change the DLL's number), so an update is only offered for a version higher than that.
            var have = ModVersion.IsNewer(m.SiteInstalledVersion, m.Version) ? m.SiteInstalledVersion : m.Version;
            var siteNewer = siteMod != null && (m.SiteId > 0 ? Community.SiteNewer(siteMod.VersionId, siteMod.Version, m.SiteVersionId, have)
                                                             : ModVersion.IsNewer(siteMod.Version, have));
            var nexusNewer = m.NexusLatest.Length > 0 && ModVersion.IsNewer(m.NexusLatest, have);
            if (siteNewer && nexusNewer)
            {
                // Both are newer: take the newest. The same version on both: the website's (virus-scanned, installs right here).
                if (ModVersion.IsNewer(m.NexusLatest, siteMod.Version)) siteNewer = false;
                else nexusNewer = false;
            }
            var installedFromSite = m.SiteId > 0;
            if (siteNewer)
            {
                m.UpdateAvailable = true;
                m.NexusVersion = siteMod.Version;
                m.UpdateFrom = Community.HostName;
                m.UpdateFromOther = !installedFromSite;
                m.SiteLatestId = siteMod.VersionId;
            }
            else if (nexusNewer)
            {
                m.UpdateAvailable = true;
                m.NexusVersion = m.NexusLatest;
                m.UpdateFrom = "Nexus";
                m.UpdateFromOther = installedFromSite;
            }
            else
            {
                m.UpdateAvailable = false;
                m.UpdateFromOther = false;
            }
        }

    }
}
