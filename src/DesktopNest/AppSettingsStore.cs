using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DesktopNest
{
    internal sealed class AppSettingsStore
    {
        private readonly JavaScriptSerializer serializer;

        public AppSettingsStore(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            ItemsDirectory = Path.Combine(rootDirectory, "items");
            SettingsPath = Path.Combine(rootDirectory, "data.json");
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            EnsureDirectories();
        }

        public string RootDirectory { get; private set; }
        public string ItemsDirectory { get; private set; }
        public string SettingsPath { get; private set; }

        public AppSettings Load(out string warning)
        {
            warning = null;
            AppSettings settings;

            if (!File.Exists(SettingsPath))
            {
                settings = AppSettings.CreateDefault();
                settings.EnsureValid();
                return settings;
            }

            try
            {
                string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                settings = serializer.Deserialize<AppSettings>(json);
                if (settings == null)
                {
                    throw new InvalidDataException("配置文件内容为空。");
                }

                if (settings.Version != AppConstants.SettingsVersion)
                {
                    string backupPath = BackupBrokenSettings();
                    settings = AppSettings.CreateDefault();
                    settings.EnsureValid();
                    warning = "配置版本不兼容，原文件已备份到：" + Environment.NewLine + backupPath;
                    return settings;
                }

                settings.EnsureValid();
                return settings;
            }
            catch (Exception ex)
            {
                string backupPath = BackupBrokenSettings();
                settings = AppSettings.CreateDefault();
                settings.EnsureValid();
                warning = "配置文件损坏，已恢复默认设置。" + Environment.NewLine
                    + "原因：" + ex.Message + Environment.NewLine
                    + "原文件备份：" + backupPath;
                return settings;
            }
        }

        public string GetStoredPath(ShortcutItem item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.ShortcutFileName))
            {
                return String.Empty;
            }

            return Path.Combine(ItemsDirectory, item.ShortcutFileName);
        }

        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            settings.EnsureValid();
            EnsureDirectories();

            string json = serializer.Serialize(settings);
            string tempPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (FileStream stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(tempPath, SettingsPath, null, true);
                }
                else
                {
                    File.Move(tempPath, SettingsPath);
                }
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(ItemsDirectory);
        }

        private string BackupBrokenSettings()
        {
            string backupPath = Path.Combine(
                RootDirectory,
                "data.corrupt." + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");

            try
            {
                File.Move(SettingsPath, backupPath);
            }
            catch
            {
                File.Copy(SettingsPath, backupPath, true);
                File.Delete(SettingsPath);
            }

            return backupPath;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class BackgroundSettingsStore
    {
        private readonly JavaScriptSerializer serializer;

        public BackgroundSettingsStore(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            SettingsPath = Path.Combine(rootDirectory, "background.json");
            CacheDirectory = Path.Combine(rootDirectory, "bg_cache");
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            EnsureDirectories();
        }

        public string RootDirectory { get; private set; }
        public string SettingsPath { get; private set; }
        public string CacheDirectory { get; private set; }

        public BackgroundSettings Load(out string warning)
        {
            warning = null;

            if (!File.Exists(SettingsPath))
            {
                BackgroundSettings defaults = BackgroundSettings.CreateDefault();
                TrySaveDefaults(defaults, ref warning);
                return defaults;
            }

            try
            {
                string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                Dictionary<string, object> root =
                    serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null)
                {
                    throw new InvalidDataException("背景配置文件内容为空。");
                }

                BackgroundSettings settings = FromDictionary(root);
                settings.EnsureValid();
                return settings;
            }
            catch (Exception ex)
            {
                string backupPath = BackupBrokenSettings();
                BackgroundSettings defaults = BackgroundSettings.CreateDefault();
                warning = "背景配置损坏，已恢复默认外观。"
                    + Environment.NewLine
                    + "原因：" + ex.Message
                    + Environment.NewLine
                    + "原文件备份：" + backupPath;
                TrySaveDefaults(defaults, ref warning);
                return defaults;
            }
        }

        public void Save(BackgroundSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            settings.EnsureValid();
            EnsureDirectories();

            string json = serializer.Serialize(ToDictionary(settings));
            string tempPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (FileStream stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(tempPath, SettingsPath, null, true);
                }
                else
                {
                    File.Move(tempPath, SettingsPath);
                }
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private static Dictionary<string, object> ToDictionary(BackgroundSettings settings)
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["mode"] = settings.Mode;
            root["color"] = settings.Color;

            Dictionary<string, object> gradient = new Dictionary<string, object>();
            gradient["from"] = settings.Gradient.From;
            gradient["to"] = settings.Gradient.To;
            gradient["direction"] = settings.Gradient.Direction;
            root["gradient"] = gradient;

            Dictionary<string, object> image = new Dictionary<string, object>();
            image["source"] = settings.Image.Source;
            image["path"] = settings.Image.Path;
            image["originalUrl"] = settings.Image.OriginalUrl;
            root["image"] = image;
            return root;
        }

        private static BackgroundSettings FromDictionary(
            Dictionary<string, object> root)
        {
            BackgroundSettings settings = BackgroundSettings.CreateDefault();
            settings.Mode = GetString(root, "mode", settings.Mode);
            settings.Color = GetString(root, "color", settings.Color);

            Dictionary<string, object> gradient = GetDictionary(root, "gradient");
            if (gradient != null)
            {
                settings.Gradient.From = GetString(
                    gradient,
                    "from",
                    settings.Gradient.From);
                settings.Gradient.To = GetString(
                    gradient,
                    "to",
                    settings.Gradient.To);
                settings.Gradient.Direction = GetString(
                    gradient,
                    "direction",
                    settings.Gradient.Direction);
            }

            Dictionary<string, object> image = GetDictionary(root, "image");
            if (image != null)
            {
                settings.Image.Source = GetString(
                    image,
                    "source",
                    settings.Image.Source);
                settings.Image.Path = GetString(
                    image,
                    "path",
                    settings.Image.Path);
                settings.Image.OriginalUrl = GetString(
                    image,
                    "originalUrl",
                    settings.Image.OriginalUrl);
            }

            return settings;
        }

        private static string GetString(
            Dictionary<string, object> dictionary,
            string key,
            string fallback)
        {
            object value;
            if (dictionary == null || !TryGetValue(dictionary, key, out value) || value == null)
            {
                return fallback;
            }

            string text = value as string;
            return text == null ? fallback : text;
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> dictionary,
            string key)
        {
            object value;
            if (dictionary == null || !TryGetValue(dictionary, key, out value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        private static bool TryGetValue(
            Dictionary<string, object> dictionary,
            string key,
            out object value)
        {
            if (dictionary.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (String.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(CacheDirectory);
        }

        private void TrySaveDefaults(
            BackgroundSettings defaults,
            ref string warning)
        {
            try
            {
                Save(defaults);
            }
            catch (Exception ex)
            {
                string saveWarning = "背景配置无法写入，当前使用默认外观。"
                    + Environment.NewLine
                    + "原因：" + ex.Message;
                warning = String.IsNullOrWhiteSpace(warning)
                    ? saveWarning
                    : warning + Environment.NewLine + saveWarning;
            }
        }

        private string BackupBrokenSettings()
        {
            string backupPath = Path.Combine(
                RootDirectory,
                "background.corrupt."
                    + DateTime.Now.ToString("yyyyMMdd-HHmmssfff")
                    + ".json");

            try
            {
                File.Move(SettingsPath, backupPath);
            }
            catch
            {
                File.Copy(SettingsPath, backupPath, true);
                TryDelete(SettingsPath);
            }

            return backupPath;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
