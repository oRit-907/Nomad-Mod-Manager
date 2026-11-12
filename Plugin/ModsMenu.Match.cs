using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace NomadModManager
{
    /// <summary>Getting mods on exact versions (matching a host, going back to your own): website mods one after another, Nexus mods through the Downloader.</summary>
    internal sealed partial class ModsMenu
    {
        private void GetSiteVersions(List<MatchHost.Want> wants)
        {
            if (wants.Count == 0) return;
            SaveBackups.Create("before getting mods on a host's versions", true);
            _host.StartCoroutine(GetSiteVersionsRun(wants));
        }

        private IEnumerator GetSiteVersionsRun(List<MatchHost.Want> wants)
        {
            int ok = 0, failed = 0; var restart = false;
            foreach (var w in wants)
            {
                while (Community.Busy != 0) yield return null;   // one install at a time
                var done = false; var good = false; string msg = null;
                yield return Community.Install(w.SiteId, w.SiteVersionId, (o, m) => { good = o; msg = m; done = true; });
                if (!done) continue;
                if (good) { ok++; if (msg != null && msg.Contains("restart")) restart = true; }
                else { failed++; ModManagerPlugin.Log?.LogWarning($"Couldn't get {w.Name} v{w.Version}: {msg}"); }
            }
            _cat.Refresh();
            var text = $"Got {ok} mod(s) from {Community.HostName} on the exact version" + (failed > 0 ? $"; {failed} failed (see Activity)" : "") + ".";
            if (restart) text += " Some replace files in use: restart the game to switch over.";
            else if (_cat.ReloadItems > 0 && !_host.AtMainMenu) text += " Press Reload to apply.";
            Toast(text, failed == 0);
        }

        /// <summary>The host's Nexus mods on their exact versions: each version's file is looked up on Nexus and opened in the menu's browser.</summary>
        private void GetNexusVersions(List<MatchHost.Want> wants, string what)
        {
            if (wants.Count == 0) return;
            _host.StartCoroutine(GetNexusVersionsRun(wants, what));
        }

        private System.Collections.IEnumerator GetNexusVersionsRun(List<MatchHost.Want> wants, string what)
        {
            var items = new List<(int, int)>();
            foreach (var w in wants)
            {
                var files = new List<NexusFileInfo>();
                yield return Nexus.Files(w.NexusId, l => files = l);
                var want = (w.Version ?? "").TrimStart('v', 'V');
                var f = files.FirstOrDefault(x => x.Version.TrimStart('v', 'V') == want && x.Category == "MAIN") ?? files.FirstOrDefault(x => x.Version.TrimStart('v', 'V') == want);
                items.Add((w.NexusId, f?.Id ?? 0));   // gone from Nexus: the newest one
            }
            BrowseNexus(items, what);
        }

        private void MatchHostNow(List<SharedMod> hostMods)
        {
            var msg = MatchHost.Run(_cat, hostMods, GetSiteVersions, GetNexusVersions, out var notFound);
            if (notFound.Count > 0) msg += " Not on Nexus or the website (ask the host where they got them): " + string.Join(", ", notFound.ToArray()) + ".";
            if (_cat.ReloadItems > 0 && !_host.AtMainMenu) msg += " Press Reload to apply.";
            Toast(msg, true);
        }

        private void BackToMyMods() => Toast(MatchHost.Back(_cat, GetSiteVersions, GetNexusVersions), true);
    }
}
