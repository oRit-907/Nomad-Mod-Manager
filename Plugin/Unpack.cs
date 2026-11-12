using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;

namespace NomadModManager
{
    /// <summary>
    /// Reads .7z and .rar mod archives (many Nexus mods only come as one), with SharpCompress 0.24 (MIT licence, by Adam
    /// Hathcock), shipped as NomadModManager.SharpCompress.dll: renamed so it never clashes with another mod's copy, and
    /// without the one call the game's trimmed .NET lacks. Only stream reading is used: SharpCompress's own helpers that
    /// write files rely on parts the game left out. Mods are small, so every file is read into memory, then placed like a zip's.
    /// </summary>
    internal static class Unpack
    {
        private const long MaxTotal = 1024L * 1024 * 1024;   // 1 GB unpacked at most

        public static bool Handles(string ext) => ext == ".7z" || ext == ".rar";

        /// <summary>Each file in the archive (folders left out) by its path inside it, with "/" separators.</summary>
        public static Dictionary<string, byte[]> ReadAll(string path)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using (var fs = File.OpenRead(path))
            using (IArchive archive = ext == ".7z" ? (IArchive)SevenZipArchive.Open(fs) : RarArchive.Open(fs))
            using (var reader = archive.ExtractAllEntries())   // one pass, the fast way for solid archives
            {
                long total = 0;
                while (reader.MoveToNextEntry())
                {
                    var e = reader.Entry;
                    if (e.IsDirectory) continue;
                    if (e.IsEncrypted) throw new InvalidOperationException("This archive has a password, so it can't be unpacked here.");
                    var name = (e.Key ?? "").Replace('\\', '/').TrimStart('/');
                    if (name.Length == 0) continue;
                    using (var ms = new MemoryStream())
                    {
                        reader.WriteEntryTo(ms);
                        total += ms.Length;
                        if (total > MaxTotal) throw new InvalidOperationException("This archive unpacks to more than 1 GB, which is too big for a mod.");
                        files[name] = ms.ToArray();
                    }
                }
            }
            return files;
        }
    }
}
