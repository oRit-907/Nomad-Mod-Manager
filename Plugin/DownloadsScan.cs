#if NEXUS_EDITION
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace NomadModManager
{
    /// <summary>
    /// 1.13.1: looks at the watched Downloads folders once a second on a background thread (a big Downloads folder used
    /// to make the game stutter every second while the menu waited for a download). The menu reads the newest snapshot.
    /// </summary>
    internal sealed class DownloadsScan
    {
        internal sealed class Entry
        {
            public string Path;
            public long Length;
            public DateTime Written, Created;
            public bool CanOpen;   // (only checked for archives written since the watch started)
        }

        internal sealed class Snapshot
        {
            public readonly List<Entry> Archives = new List<Entry>();
            public bool Busy;      // a browser is still writing a download (.crdownload, .part…) in the last two minutes
            public int Number;
        }

        private static readonly string[] ArchiveExt = { ".zip", ".7z", ".rar", ".dll" };
        private static readonly string[] PartialExt = { ".crdownload", ".part", ".partial", ".download" };

        private readonly object _lock = new object();
        private Snapshot _latest = new Snapshot();
        private Thread _thread;
        private volatile bool _run;
        private List<string> _dirs = new List<string>();
        private DateTime _since;
        private int _number;

        public Snapshot Latest { get { lock (_lock) return _latest; } }

        public void Start(List<string> dirs, DateTime since)
        {
            Stop();
            _dirs = new List<string>(dirs);
            _since = since;
            lock (_lock) _latest = new Snapshot();
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "NomadModManager downloads", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            _thread = null;
        }

        private void Loop()
        {
            while (_run)
            {
                try
                {
                    var snap = new Snapshot { Number = ++_number };
                    var now = DateTime.Now;
                    foreach (var dir in _dirs)
                    {
                        FileInfo[] files;
                        try { files = new DirectoryInfo(dir).GetFiles(); } catch { continue; }
                        foreach (var fi in files)
                        {
                            if (!_run) return;
                            var ext = fi.Extension.ToLowerInvariant();
                            try
                            {
                                if (Array.IndexOf(PartialExt, ext) >= 0) { if ((now - fi.LastWriteTime).TotalMinutes < 2) snap.Busy = true; continue; }
                                if (Array.IndexOf(ArchiveExt, ext) < 0) continue;
                                var written = fi.LastWriteTime;
                                if ((now - written).TotalHours >= 24 && written < _since && fi.CreationTime < _since) continue;   // old: neither new nor recent
                                var e = new Entry { Path = fi.FullName, Length = fi.Length, Written = written, Created = fi.CreationTime };
                                if ((e.Written >= _since || e.Created >= _since) && e.Length > 0)
                                {
                                    try { using (File.Open(e.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } e.CanOpen = true; } catch { }
                                }
                                snap.Archives.Add(e);
                            }
                            catch { }
                        }
                    }
                    lock (_lock) _latest = snap;
                }
                catch { }
                for (var i = 0; i < 10 && _run; i++) Thread.Sleep(100);
            }
        }
    }
}
#endif
