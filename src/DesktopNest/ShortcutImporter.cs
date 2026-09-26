using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopNest
{
    internal sealed class ImportedShortcut
    {
        public ShortcutItem Item { get; set; }
        public string StoredPath { get; set; }
    }

    internal sealed class ShortcutImporter
    {
        private readonly string itemsDirectory;

        public ShortcutImporter(string itemsDirectory)
        {
            this.itemsDirectory = itemsDirectory;
            Directory.CreateDirectory(itemsDirectory);
        }

        public ImportedShortcut Import(string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("没有可添加的文件。", "sourcePath");
            }

            bool isDirectory = Directory.Exists(sourcePath);
            if (!isDirectory && !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("要添加的文件不存在。", sourcePath);
            }

            string id = Guid.NewGuid().ToString("N");
            string extension = Path.GetExtension(sourcePath);
            bool moveFromDesktop = IsOnDesktop(sourcePath) && !isDirectory;
            bool copyExisting = String.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);

            string storedFileName;
            if (moveFromDesktop)
            {
                storedFileName = isDirectory
                    ? id
                    : (String.IsNullOrWhiteSpace(extension) ? id : id + extension);
            }
            else
            {
                string storedExtension = String.Equals(
                    extension,
                    ".url",
                    StringComparison.OrdinalIgnoreCase)
                    ? ".url"
                    : ".lnk";
                storedFileName = id + storedExtension;
            }

            string storedPath = Path.Combine(itemsDirectory, storedFileName);
            string targetHint = String.Empty;
            string originalPath = String.Empty;

            try
            {
                if (moveFromDesktop)
                {
                    File.Copy(sourcePath, storedPath, false);
                    originalPath = sourcePath;
                    if (String.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        targetHint = TryGetTargetPath(storedPath);
                    }
                    else if (String.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                    {
                        targetHint = TryReadInternetShortcutUrl(storedPath);
                    }
                }
                else if (copyExisting)
                {
                    File.Copy(sourcePath, storedPath, false);
                    if (String.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        targetHint = TryGetTargetPath(sourcePath);
                    }
                    else
                    {
                        targetHint = TryReadInternetShortcutUrl(sourcePath);
                    }
                }
                else
                {
                    CreateWindowsShortcut(sourcePath, storedPath);
                    targetHint = sourcePath;
                }
            }
            catch
            {
                TryDelete(storedPath);
                throw;
            }

            ShortcutItem item = new ShortcutItem();
            item.Id = id;
            item.DisplayName = GetDisplayName(sourcePath);
            item.ShortcutFileName = storedFileName;
            item.TargetPathHint = targetHint ?? String.Empty;
            item.OriginalPath = originalPath;

            ImportedShortcut result = new ImportedShortcut();
            result.Item = item;
            result.StoredPath = storedPath;
            return result;
        }

        public static string TryGetTargetPath(string shortcutPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return String.Empty;
                }

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });

                object target = shortcut.GetType().InvokeMember(
                    "TargetPath",
                    BindingFlags.GetProperty,
                    null,
                    shortcut,
                    null);

                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
                return target == null ? String.Empty : Convert.ToString(target);
            }
            catch
            {
                return String.Empty;
            }
        }

        private static void CreateWindowsShortcut(string sourcePath, string storedPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("当前系统无法创建 Windows 快捷方式。");
            }

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { storedPath });

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { sourcePath });

                string workingDirectory = Directory.Exists(sourcePath)
                    ? sourcePath
                    : Path.GetDirectoryName(sourcePath);
                if (!String.IsNullOrWhiteSpace(workingDirectory))
                {
                    shortcutType.InvokeMember(
                        "WorkingDirectory",
                        BindingFlags.SetProperty,
                        null,
                        shortcut,
                        new object[] { workingDirectory });
                }

                shortcutType.InvokeMember(
                    "IconLocation",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { sourcePath + ",0" });

                shortcutType.InvokeMember(
                    "Save",
                    BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static string TryReadInternetShortcutUrl(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        return lines[i].Substring(4).Trim();
                    }
                }
            }
            catch
            {
            }

            return String.Empty;
        }

        private static string GetDisplayName(string sourcePath)
        {
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            if (String.IsNullOrWhiteSpace(name))
            {
                DirectoryInfo directory = new DirectoryInfo(sourcePath);
                name = directory.Name;
            }

            return String.IsNullOrWhiteSpace(name) ? "未命名" : name;
        }

        public static bool IsOnDesktop(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string commonDesktop = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonDesktopDirectory);

            return IsPathUnderDirectory(fullPath, desktop)
                || IsPathUnderDirectory(fullPath, commonDesktop);
        }

        private static bool IsPathUnderDirectory(string fullPath, string directory)
        {
            if (String.IsNullOrWhiteSpace(fullPath) || String.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(fullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.FinalReleaseComObject(value);
                }
                catch
                {
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }
    }
}
