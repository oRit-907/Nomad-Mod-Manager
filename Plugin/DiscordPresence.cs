using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// Discord Rich Presence (1.17.0): "Playing Nomad Drive · Playing with 11 mods · On the road" on the player's Discord
    /// profile, with a button to get the mods.
    ///
    /// It talks to the Discord app on this PC through Discord's local pipe (\\.\pipe\discord-ipc-N), the same way
    /// Discord's own Rich Presence library does, so no DLL is shipped and nothing goes over the internet from the game.
    /// All of it runs on its own thread: if Discord isn't running it quietly tries again every 20 seconds, and the game
    /// never waits for it. Join codes and lobby ids are never shown.
    /// </summary>
    internal static class DiscordPresence
    {
        /// <summary>The Discord application ("Nomad Drive") whose name shows as "Playing …". The config can set another.</summary>
        public const string BuiltInAppId = "1574333064459583528";

        public static string Status { get; private set; } = "Off";
        public static bool Connected { get; private set; }

        private static Thread _thread;
        private static volatile bool _stop;
        private static volatile string _want;     // the activity JSON to show ("" = clear it)
        private static string _appId = "";
        private static readonly long StartedAt = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        private static float _nextBuild;

        private static ModManagerPlugin P => ModManagerPlugin.I;

        public static string AppId
        {
            get
            {
                var c = P?.DiscordAppId?.Value?.Trim() ?? "";
                return c.Length > 0 ? c : BuiltInAppId;
            }
        }

        /// <summary>Called every frame from the plugin: builds what to show every few seconds and starts or stops the thread.</summary>
        public static void Tick(ModCatalog cat)
        {
            if (Time.realtimeSinceStartup < _nextBuild) return;
            _nextBuild = Time.realtimeSinceStartup + 5f;
            var on = P?.DiscordOn != null && P.DiscordOn.Value && Application.platform == RuntimePlatform.WindowsPlayer;
            var id = AppId;
            if (!on || id.Length == 0 || !id.All(char.IsDigit))
            {
                Status = !on ? "Off" : "Needs the Discord application id (Settings › Discord › ApplicationId)";
                if (_thread != null) Shutdown();
                return;
            }
            if (_thread != null && id != _appId) Shutdown();   // a different application: connect again
            _want = Build(cat);
            if (_thread == null)
            {
                _appId = id;
                _stop = false;
                _thread = new Thread(Run) { IsBackground = true, Name = "NomadModManager Discord" };
                _thread.Start();
            }
        }

        /// <summary>What shows on the profile, worked out on the game's thread.</summary>
        private static string Build(ModCatalog cat)
        {
            var mods = 0;
            try { mods = cat.Mods.Count(m => !m.IsSelf && !m.IsLoader && (m.State == ModState.Running || m.State == ModState.StartedNow)); } catch { }
            string state;
            var at = P != null && P.AtMainMenu;
            if (P.DiscordShowGame.Value && LobbyMods.InLobby)
            {
                var others = Math.Max(0, LobbyMods.Players.Count - 1);
                state = (LobbyMods.IsHost ? "Hosting a game" : "In a multiplayer game") + (others > 0 ? $" · {others + 1} players" : "");
            }
            else state = at ? "At the main menu" : P.DiscordShowGame.Value ? "On the road" : "Playing";

            var activity = new JObject
            {
                ["state"] = state,
                ["timestamps"] = new JObject { ["start"] = StartedAt },
            };
            if (P.DiscordShowMods.Value) activity["details"] = mods == 1 ? "Playing with 1 mod" : $"Playing with {mods} mods";
            var image = (P.DiscordImage?.Value ?? "").Trim();
            if (image.Length > 0)
                activity["assets"] = new JObject { ["large_image"] = image, ["large_text"] = "Nomad Drive, modded with Nomad Mod Manager" };
            if (P.DiscordButtons.Value)
            {
                var url = Edition.Nexus ? $"https://www.nexusmods.com/{Nexus.GameDomain}" : Community.Site;
                activity["buttons"] = new JArray(new JObject { ["label"] = "Get the mods", ["url"] = url });
            }
            return activity.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Clears the profile and stops (at quit, or when it's switched off).</summary>
        public static void Shutdown()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            try { t?.Join(1500); } catch { }
            Connected = false;
        }

        // ------------------------------------------------------------------ the pipe (its own thread)

        private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

        private static void Run()
        {
            FileStream pipe = null;
            string sent = null;
            var logged = false;
            var refreshAt = DateTime.MinValue;
            while (!_stop)
            {
                try
                {
                    if (pipe == null)
                    {
                        pipe = Open();
                        if (pipe == null) { Status = "Waiting for the Discord app"; Connected = false; Sleep(20000); continue; }
                        Write(pipe, OpHandshake, new JObject { ["v"] = 1, ["client_id"] = _appId }.ToString(Newtonsoft.Json.Formatting.None));
                        var (op, body) = Read(pipe);
                        if (op != OpFrame || !body.Contains("\"READY\""))
                        {
                            Status = "Discord didn't accept the application id";
                            if (!logged) { logged = true; ModManagerPlugin.Log.LogWarning("Discord: " + Clip(body)); }
                            Close(ref pipe); Sleep(60000); continue;
                        }
                        Connected = true; Status = "Showing on Discord"; sent = null;
                        ModManagerPlugin.Log.LogInfo("Discord: connected, showing the game on your profile.");
                    }
                    var want = _want ?? "";
                    // Sent again every few minutes too, in case Discord was restarted with the same pipe.
                    if (want != sent || DateTime.UtcNow > refreshAt)
                    {
                        Send(pipe, want.Length > 0 ? JObject.Parse(want) : null);
                        sent = want;
                        refreshAt = DateTime.UtcNow.AddMinutes(5);
                    }
                    Sleep(1000);
                }
                catch (Exception e)
                {
                    if (Connected) ModManagerPlugin.Log.LogInfo("Discord: the connection ended (" + e.Message + "); trying again soon.");
                    Connected = false; Status = "Waiting for the Discord app";
                    Close(ref pipe);
                    Sleep(20000);
                }
            }
            // Leaving: take the game off the profile.
            if (pipe != null)
            {
                try { Send(pipe, null); Write(pipe, OpClose, "{}"); } catch { }
                Close(ref pipe);
            }
            Status = "Off";
        }

        private static void Send(FileStream pipe, JObject activity)
        {
            var msg = new JObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JObject { ["pid"] = Pid(), ["activity"] = activity ?? (JToken)JValue.CreateNull() },
                ["nonce"] = Guid.NewGuid().ToString(),
            };
            Write(pipe, OpFrame, msg.ToString(Newtonsoft.Json.Formatting.None));
            // Discord answers every command; read it so the pipe doesn't fill up. A ping is answered and then the answer read.
            for (var i = 0; i < 3; i++)
            {
                var (op, body) = Read(pipe);
                if (op == OpPing) { Write(pipe, OpPong, body); continue; }
                if (op == OpClose) throw new IOException("Discord closed the connection: " + Clip(body));
                if (body.Contains("\"evt\":\"ERROR\"")) ModManagerPlugin.Log.LogWarning("Discord: " + Clip(body));
                break;
            }
        }

        private static int Pid() { try { return System.Diagnostics.Process.GetCurrentProcess().Id; } catch { return 0; } }

        private static void Sleep(int ms)
        {
            for (var t = 0; t < ms && !_stop; t += 250) Thread.Sleep(250);
        }

        private static string Clip(string s) => s == null ? "" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;

        private static void Close(ref FileStream pipe)
        {
            try { pipe?.Dispose(); } catch { }
            pipe = null;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        private static FileStream Open()
        {
            for (var i = 0; i < 10; i++)
            {
                SafeFileHandle h;
                try { h = CreateFileW(@"\\.\pipe\discord-ipc-" + i, 0xC0000000 /* read + write */, 0, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero); }
                catch (Exception e) { ModManagerPlugin.Log.LogWarning("Discord: can't open Discord's pipe here (" + e.Message + ")."); _stop = true; return null; }
                if (h == null || h.IsInvalid) { h?.Dispose(); continue; }
                return new FileStream(h, FileAccess.ReadWrite, 4096, false);
            }
            return null;
        }

        private static void Write(FileStream pipe, int op, string json)
        {
            var body = Encoding.UTF8.GetBytes(json ?? "");
            var buf = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(buf, 0);
            BitConverter.GetBytes(body.Length).CopyTo(buf, 4);
            body.CopyTo(buf, 8);
            pipe.Write(buf, 0, buf.Length);
            pipe.Flush();
        }

        private static (int Op, string Body) Read(FileStream pipe)
        {
            var head = ReadExactly(pipe, 8);
            var op = BitConverter.ToInt32(head, 0);
            var len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 1 << 20) throw new IOException("Discord sent a frame that doesn't make sense.");
            return (op, Encoding.UTF8.GetString(ReadExactly(pipe, len)));
        }

        private static byte[] ReadExactly(FileStream pipe, int n)
        {
            var buf = new byte[n];
            var got = 0;
            while (got < n)
            {
                var r = pipe.Read(buf, got, n - got);
                if (r <= 0) throw new IOException("Discord closed the connection.");
                got += r;
            }
            return buf;
        }
    }
}
