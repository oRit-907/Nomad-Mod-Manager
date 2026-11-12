using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// "Export logs for mod devs": one zip with everything a mod author asks for when something breaks — the BepInEx
    /// log, Unity's Player.log (this session and the last), the mod list with versions and where each came from,
    /// what changes multiplayer, errors this session, recent activity, the last drop report and the loader's config.
    /// Your Windows user name is replaced with &lt;user&gt; in every text file, so the zip is safe to post.
    /// </summary>
    internal static class LogExport
    {
        public static string Run(ModCatalog cat)
        {
            var root = Paths.GameRootPath;
            var files = new List<(string Name, byte[] Data)>();
            void Text(string name, string text) => files.Add((name, Encoding.UTF8.GetBytes(Redact(text))));
            void Copy(string name, string path)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs)) Text(name, r.ReadToEnd());
                }
                catch (Exception e) { Text(name + ".unreadable.txt", "Couldn't read " + path + ": " + e.Message); }
            }

            Text("README.txt", Readme());
            Text("system.txt", SystemInfo2());
            Text("mods.txt", ModsText(cat));
            Text("errors-this-session.txt", string.Join("\n", SessionErrors.Read().Select(e => $"[{e.Level}] {e.Source}: {e.Text}" + (e.Count > 1 ? $"  (x{e.Count})" : "")).ToArray()));
            Text("activity.txt", string.Join("\n", Activity.Recent(400).Select(a => a.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + a.Text).ToArray()));
            Text("multiplayer.txt", Multiplayer());
            Copy("BepInEx/LogOutput.log", StartupLog.LogPath);
            Copy("BepInEx/LogOutput.log.1", Path.Combine(Paths.BepInExRootPath, "LogOutput.log.1"));
            Copy("BepInEx/BepInEx.cfg", Path.Combine(Paths.ConfigPath, "BepInEx.cfg"));
            Copy("doorstop_config.ini", Path.Combine(root, "doorstop_config.ini"));
            string player = null;
            try { player = Application.consoleLogPath; } catch { }
            Copy("Unity/Player.log", player);
            if (!string.IsNullOrEmpty(player)) Copy("Unity/Player-prev.log", Path.Combine(Path.GetDirectoryName(player) ?? "", "Player-prev.log"));
            Copy("NomadModManager/pending.txt", Path.Combine(Pending.Dir(root), "pending.txt"));

            var name = "NomadDrive-logs-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".zip";
            string dir = null;
            try { dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = root;
            var path = Path.Combine(dir, name);
            Zip.Write(path, files);
            Activity.Add("Exported logs to " + path);
            return path;
        }

        private static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length > 1)
            {
                text = text.Replace("\\" + user + "\\", "\\<user>\\").Replace("/" + user + "/", "/<user>/");
                var home = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(home)) text = text.Replace(home, "C:\\Users\\<user>");
            }
            return text;
        }

        private static string Readme() =>
            "Nomad Drive logs, exported by the Mods menu (Nomad Mod Manager " + ModManagerPlugin.Version + ") on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".\n\n" +
            "Send this zip to the mod's author (Nexus Bugs tab, the website's Report a bug, or Discord).\n" +
            "Your Windows user name was replaced with <user>.\n\n" +
            "system.txt               game, Unity, BepInEx and Windows versions\n" +
            "mods.txt                 every mod: on/off, version, where it came from, its plugins, whether it changes multiplayer\n" +
            "errors-this-session.txt  errors and warnings from the BepInEx log, grouped\n" +
            "activity.txt             installs, updates and switches made in the Mods menu\n" +
            "multiplayer.txt          the lobby, other players' mods, the last drop report, Safe join\n" +
            "BepInEx/LogOutput.log    the full BepInEx log of this session (.1 = the one before)\n" +
            "Unity/Player.log         Unity's own log (Player-prev.log = the session before)\n";

        private static string SystemInfo2()
        {
            var sb = new StringBuilder();
            void L(string k, object v) => sb.Append(k.PadRight(22)).Append(v).Append('\n');
            L("Game version", Application.version);
            L("Unity", Application.unityVersion);
            L("BepInEx", typeof(Chainloader).Assembly.GetName().Version);
            L("Mods menu", ModManagerPlugin.Version);
            try { L("Windows", SystemInfo.operatingSystem); L("CPU", SystemInfo.processorType); L("RAM (MB)", SystemInfo.systemMemorySize); L("GPU", SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVersion + ")"); } catch { }
            L("Screen", Screen.width + "x" + Screen.height);
            L("Exported", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            L("Time in game (s)", Mathf.RoundToInt(Time.realtimeSinceStartup));
            return sb.ToString();
        }

        private static string ModsText(ModCatalog cat)
        {
            var sb = new StringBuilder();
            if (cat != null)
            {
                foreach (var m in cat.Mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var src = m.SiteId > 0 ? $"website #{m.SiteId} (file {m.SiteVersionId})" : m.NexusId > 0 ? $"Nexus #{m.NexusId}" : "installed by hand";
                    sb.Append(Profiles.IsOn(m) ? "[on]  " : "[off] ").Append(m.Name).Append("  v").Append(m.Version.TrimStart('v', 'V'))
                      .Append("  — ").Append(src).Append("  — by ").Append(m.Author).Append('\n');
                    sb.Append("      state: ").Append(m.State).Append(m.InUse ? ", loaded" : "").Append(m.UpdateAvailable ? ", update available (" + m.NexusVersion + ")" : "").Append('\n');
                    var why = NetCheck.Why(m);
                    if (why != NetCheck.None) sb.Append("      multiplayer: ").Append(NetCheck.Explain(why)).Append('\n');
                    foreach (var d in m.Dlls) sb.Append("      dll: ").Append(Rel(d)).Append('\n');
                    foreach (var g in m.Guids) sb.Append("      plugin: ").Append(g).Append('\n');
                    if (m.Note.Length > 0) sb.Append("      note: ").Append(m.Note).Append('\n');
                }
            }
            sb.Append("\nLoaded plugins (BepInEx):\n");
            foreach (var p in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.Name))
                sb.Append("  ").Append(p.Metadata.Name).Append(" ").Append(p.Metadata.Version).Append("  [").Append(p.Metadata.GUID).Append("]  ").Append(Rel(p.Location)).Append('\n');
            return sb.ToString();
        }

        private static string Multiplayer()
        {
            var sb = new StringBuilder();
            sb.Append("In a lobby: ").Append(LobbyMods.InLobby).Append(LobbyMods.InLobby ? (LobbyMods.IsHost ? " (host)" : " (guest)") : "").Append('\n');
            if (LobbyMods.Error != null) sb.Append("Lobby check off: ").Append(LobbyMods.Error).Append('\n');
            foreach (var p in LobbyMods.Players)
                sb.Append("Player ").Append(p.Name).Append(p.IsHost ? " (host)" : "").Append(p.IsMe ? " (you)" : "").Append(": ")
                  .Append(p.Mods == null ? "no Mods menu" : string.Join(", ", p.Mods.Select(m => m.Name + " v" + m.Version + (m.Net ? " [mp]" : "")).ToArray())).Append('\n');
            sb.Append("Safe join: ").Append(SafeJoin.On ? "on" : "off").Append(SafeJoin.Engaged ? ", active — paused: " + string.Join(", ", SafeJoin.PausedMods.ToArray()) : "").Append('\n');
            if (NetGuard.Held.Count > 0) sb.Append("Held back this game: ").Append(string.Join(", ", NetGuard.Held.ToArray())).Append('\n');
            var d = JoinDoctor.Last;
            if (d != null)
            {
                sb.Append("\nLast drop: ").Append(d.When.ToString("HH:mm")).Append(" joining ").Append(d.Host).Append(" after ").Append(Mathf.RoundToInt(d.Seconds)).Append(" s\n");
                sb.Append(d.Title).Append("\n").Append(d.Why).Append('\n');
                foreach (var f in d.Fixes) sb.Append("- ").Append(f).Append('\n');
                foreach (var l in d.Log) sb.Append("  ").Append(l).Append('\n');
            }
            return sb.ToString();
        }

        private static string Rel(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var root = Paths.GameRootPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }
    }

    /// <summary>A plain zip writer (files stored, not compressed): the game's Mono doesn't ship one.</summary>
    internal static class Zip
    {
        private static uint[] _crc;

        private static uint Crc32(byte[] data)
        {
            if (_crc == null)
            {
                _crc = new uint[256];
                for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; _crc[n] = c; }
            }
            var crc = 0xFFFFFFFFu;
            foreach (var b in data) crc = _crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        public static void Write(string path, List<(string Name, byte[] Data)> files)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                var central = new List<(byte[] Name, uint Crc, int Size, int Offset)>();
                var now = DateTime.Now;
                var dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
                var dosDate = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
                foreach (var (name, data) in files)
                {
                    var nb = Encoding.UTF8.GetBytes(name.Replace('\\', '/'));
                    var crc = Crc32(data);
                    var offset = (int)fs.Position;
                    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)0);   // stored, UTF-8 names
                    w.Write(dosTime); w.Write(dosDate); w.Write(crc); w.Write(data.Length); w.Write(data.Length);
                    w.Write((ushort)nb.Length); w.Write((ushort)0); w.Write(nb); w.Write(data);
                    central.Add((nb, crc, data.Length, offset));
                }
                var cdStart = (int)fs.Position;
                foreach (var (nb, crc, size, offset) in central)
                {
                    w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)0);
                    w.Write(dosTime); w.Write(dosDate); w.Write(crc); w.Write(size); w.Write(size);
                    w.Write((ushort)nb.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(offset); w.Write(nb);
                }
                var cdSize = (int)fs.Position - cdStart;
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)central.Count); w.Write((ushort)central.Count);
                w.Write(cdSize); w.Write(cdStart); w.Write((ushort)0);
            }
        }
    }
}
