using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopNest
{
    internal sealed class IconService : IDisposable
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;

        private readonly Dictionary<string, Icon> cache;

        public IconService()
        {
            cache = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
        }

        public Icon GetIcon(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                return (Icon)SystemIcons.Application.Clone();
            }

            string key = path;
            try
            {
                key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks.ToString();
            }
            catch
            {
            }

            Icon cached;
            if (cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            Icon icon = ExtractIcon(path);
            cache[key] = icon;
            return icon;
        }

        public void Dispose()
        {
            foreach (Icon icon in cache.Values)
            {
                icon.Dispose();
            }

            cache.Clear();
        }

        private static Icon ExtractIcon(string path)
        {
            IntPtr iconHandle = IntPtr.Zero;
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint result = SHGetFileInfo(
                    path,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | SHGFI_LARGEICON);

                iconHandle = info.hIcon;
                if (result != 0 && iconHandle != IntPtr.Zero)
                {
                    using (Icon borrowed = Icon.FromHandle(iconHandle))
                    {
                        return (Icon)borrowed.Clone();
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(iconHandle);
                }
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
