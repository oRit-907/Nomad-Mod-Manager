using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// Nexus serves thumbnails as WebP, which Unity's Texture2D.LoadImage can't read. This decodes them with the
    /// official libwebp (BepInEx\NomadModManager\native\libwebp.dll + libsharpyuv.dll), loaded on first use.
    /// </summary>
    internal static class WebP
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private const uint LoadWithAlteredSearchPath = 0x8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DecodeRgba(IntPtr data, UIntPtr size, out int width, out int height);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFn(IntPtr ptr);

        private static bool _tried;
        private static DecodeRgba _decode;
        private static FreeFn _free;

        public static bool IsWebP(byte[] d) =>
            d != null && d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

        private static bool Load()
        {
            if (_tried) return _decode != null;
            _tried = true;
            try
            {
                var dir = Path.Combine(Pending.Dir(Paths.GameRootPath), "native");
                // (1.16.0) On Ignite Loader the menu can still be installed in the old BepInEx folder, with the two
                // libraries from its zip next to it there, while its data folder is Ignite Loader's.
                try
                {
                    var own = Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(WebP).Assembly.Location) ?? "", "data"), "native");
                    if (!File.Exists(Path.Combine(dir, "libwebp.dll")) && File.Exists(Path.Combine(own, "libwebp.dll"))) dir = own;
                }
                catch { }
                var lib = Path.Combine(dir, "libwebp.dll");
                // An update that came while the game ran leaves the new copies as *.nmm-new, and the change patcher
                // only finishes those under plugins, patchers and config. Nothing has loaded these yet: finish them here.
                try
                {
                    if (Directory.Exists(dir))
                        foreach (var staged in Directory.GetFiles(dir, "*" + Pending.NewSuffix))
                        {
                            var target = staged.Substring(0, staged.Length - Pending.NewSuffix.Length);
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(staged, target);
                        }
                }
                catch (Exception e) { ModManagerPlugin.Log.LogWarning("Thumbnails: couldn't finish the libwebp update: " + e.Message); }
                if (!File.Exists(lib)) { ModManagerPlugin.Log.LogWarning("Thumbnails: libwebp.dll is missing from " + dir); return false; }
                // Altered search path: libwebp finds libsharpyuv.dll in its own folder.
                var h = LoadLibraryExW(lib, IntPtr.Zero, LoadWithAlteredSearchPath);
                if (h == IntPtr.Zero) { ModManagerPlugin.Log.LogWarning("Thumbnails: couldn't load libwebp (error " + Marshal.GetLastWin32Error() + ")"); return false; }
                var dec = GetProcAddress(h, "WebPDecodeRGBA");
                var free = GetProcAddress(h, "WebPFree");
                if (dec == IntPtr.Zero || free == IntPtr.Zero) return false;
                _decode = (DecodeRgba)Marshal.GetDelegateForFunctionPointer(dec, typeof(DecodeRgba));
                _free = (FreeFn)Marshal.GetDelegateForFunctionPointer(free, typeof(FreeFn));
                return true;
            }
            catch (Exception e)
            {
                ModManagerPlugin.Log.LogWarning("Thumbnails: " + e.Message);
                _decode = null;
                return false;
            }
        }

        /// <summary>Decodes WebP bytes into a new texture, or null.</summary>
        public static Texture2D Decode(byte[] data)
        {
            if (!Load()) return null;
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            IntPtr pixels = IntPtr.Zero;
            try
            {
                pixels = _decode(pin.AddrOfPinnedObject(), (UIntPtr)(ulong)data.Length, out var w, out var h);
                if (pixels == IntPtr.Zero || w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
                var stride = w * 4;
                var raw = new byte[stride * h];
                // libwebp is top-down, Unity textures are bottom-up.
                for (var y = 0; y < h; y++)
                    Marshal.Copy(new IntPtr(pixels.ToInt64() + (long)y * stride), raw, (h - 1 - y) * stride, stride);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(raw);
                tex.Apply(false, true);
                return tex;
            }
            catch (Exception e)
            {
                ModManagerPlugin.Log.LogWarning("Thumbnail decode failed: " + e.Message);
                return null;
            }
            finally
            {
                if (pixels != IntPtr.Zero) try { _free(pixels); } catch { }
                pin.Free();
            }
        }

        /// <summary>
        /// 1.16.4: decodes to bottom-up RGBA bytes, shrunk by halves until it is at most <paramref name="maxW"/> wide.
        /// Touches no Unity object, so it can run away from the game's thread; the texture is made from it afterwards.
        /// </summary>
        public static byte[] DecodeRaw(byte[] data, int maxW, out int w, out int h)
        {
            w = h = 0;
            if (!Load()) return null;
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            IntPtr pixels = IntPtr.Zero;
            try
            {
                pixels = _decode(pin.AddrOfPinnedObject(), (UIntPtr)(ulong)data.Length, out w, out h);
                if (pixels == IntPtr.Zero || w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
                var stride = w * 4;
                var raw = new byte[stride * h];
                for (var y = 0; y < h; y++)
                    Marshal.Copy(new IntPtr(pixels.ToInt64() + (long)y * stride), raw, (h - 1 - y) * stride, stride);
                while (w > maxW && w >= 2 && h >= 2) raw = Half(raw, ref w, ref h);
                return raw;
            }
            catch { return null; }
            finally
            {
                if (pixels != IntPtr.Zero) try { _free(pixels); } catch { }
                pin.Free();
            }
        }

        /// <summary>Half the width and height, each pixel the average of four.</summary>
        private static byte[] Half(byte[] src, ref int w, ref int h)
        {
            int nw = w / 2, nh = h / 2;
            var dst = new byte[nw * nh * 4];
            for (var y = 0; y < nh; y++)
                for (var x = 0; x < nw; x++)
                {
                    int a = (2 * y * w + 2 * x) * 4, b = a + 4, c = a + w * 4, d = c + 4, o = (y * nw + x) * 4;
                    for (var k = 0; k < 4; k++) dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) >> 2);
                }
            w = nw; h = nh;
            return dst;
        }
    }
}
