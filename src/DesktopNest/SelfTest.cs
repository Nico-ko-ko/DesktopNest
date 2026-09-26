using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DesktopNest
{
    internal static class SelfTest
    {
        public static int Run()
        {
            AttachParentConsole();
            List<string> failures = new List<string>();
            string testRoot = Path.Combine(
                Path.GetTempPath(),
                "DesktopNest-SelfTest-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(testRoot);
                RunTests(testRoot, failures);
            }
            catch (Exception ex)
            {
                failures.Add("未处理异常：" + ex);
            }
            finally
            {
                TryDeleteDirectory(testRoot);
            }

            if (failures.Count == 0)
            {
                Console.WriteLine("DesktopNest self-test: PASS");
                return 0;
            }

            Console.WriteLine("DesktopNest self-test: FAIL");
            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine("- " + failures[i]);
            }

            return 1;
        }

        private static void RunTests(string testRoot, List<string> failures)
        {
            AppSettingsStore store = new AppSettingsStore(testRoot);

            AppSettings defaults = AppSettings.CreateDefault();
            Check(defaults.Version == AppConstants.SettingsVersion, "默认设置版本错误。", failures);
            Check(defaults.Items != null, "默认设置缺少项目列表。", failures);
            Check(String.Equals(defaults.Title, AppConstants.DefaultTitle, StringComparison.Ordinal),
                "默认工具名称错误。",
                failures);

            ShortcutItem item = new ShortcutItem();
            item.DisplayName = "测试项目";
            item.ShortcutFileName = item.Id + ".lnk";
            defaults.Items.Add(item);

            defaults.Title = "自检工具";
            defaults.SetBounds(new Rectangle(120, 80, 700, 460));
            defaults.IsMaximized = true;
            store.Save(defaults);

            string warning;
            AppSettings loaded = store.Load(out warning);
            Check(String.IsNullOrEmpty(warning), "正常配置加载不应产生警告。", failures);
            Check(String.Equals(loaded.Title, defaults.Title, StringComparison.Ordinal),
                "JSON 反序列化后的工具名称不一致。",
                failures);
            Check(loaded.Items.Count == 1, "JSON 反序列化后的项目数量不一致。", failures);
            Check(String.Equals(loaded.Items[0].Id, item.Id, StringComparison.Ordinal),
                "JSON 反序列化后的项目 ID 不一致。",
                failures);
            Check(loaded.GetBounds() == defaults.GetBounds(), "原子写入后窗口坐标不一致。", failures);
            Check(loaded.IsMaximized, "原子写入后最大化状态不一致。", failures);

            ShortcutItem second = new ShortcutItem();
            Check(!String.Equals(item.Id, second.Id, StringComparison.Ordinal),
                "唯一 ID 生成重复。",
                failures);

            int before = loaded.Items.Count;
            loaded.Items.Add(second);
            store.Save(loaded);
            loaded = store.Load(out warning);
            Check(loaded.Items.Count == before + 1, "添加项目失败。", failures);
            loaded.Items.RemoveAt(loaded.Items.Count - 1);
            store.Save(loaded);
            loaded = store.Load(out warning);
            Check(loaded.Items.Count == before, "删除项目失败。", failures);

            File.WriteAllText(store.SettingsPath, "{ broken json", Encoding.UTF8);
            AppSettings recovered = store.Load(out warning);
            string[] backups = Directory.GetFiles(testRoot, "data.corrupt.*.json");
            Check(!String.IsNullOrWhiteSpace(warning), "损坏 JSON 未返回警告。", failures);
            Check(backups.Length == 1, "损坏 JSON 未生成唯一备份。", failures);
            Check(recovered.Items.Count == 0, "损坏 JSON 未恢复为默认配置。", failures);

            BackgroundSettingsStore backgroundStore = new BackgroundSettingsStore(
                Path.Combine(testRoot, "config"));
            BackgroundSettings backgroundDefaults = backgroundStore.Load(out warning);
            Check(String.IsNullOrEmpty(warning), "默认背景配置不应产生警告。", failures);
            Check(File.Exists(backgroundStore.SettingsPath),
                "默认背景配置未写入 background.json。",
                failures);
            Check(String.Equals(
                    backgroundDefaults.Mode,
                    BackgroundModes.Color,
                    StringComparison.Ordinal),
                "默认背景模式错误。",
                failures);

            backgroundDefaults.Mode = BackgroundModes.Gradient;
            backgroundDefaults.Color = "#336699";
            backgroundDefaults.Gradient.From = "#FF0000";
            backgroundDefaults.Gradient.To = "#0000FF";
            backgroundDefaults.Gradient.Direction = BackgroundDirections.TopToBottom;
            backgroundDefaults.Image.Source = BackgroundImageSources.Url;
            backgroundDefaults.Image.Path = Path.Combine(
                backgroundStore.CacheDirectory,
                "url_cache.png");
            backgroundDefaults.Image.OriginalUrl = "https://example.com/a.png";
            backgroundStore.Save(backgroundDefaults);

            BackgroundSettings loadedBackground = backgroundStore.Load(out warning);
            Check(String.IsNullOrEmpty(warning), "正常背景配置加载不应产生警告。", failures);
            Check(String.Equals(
                    loadedBackground.Mode,
                    BackgroundModes.Gradient,
                    StringComparison.Ordinal),
                "背景模式未正确持久化。",
                failures);
            Check(String.Equals(
                    loadedBackground.Gradient.From,
                    "#FF0000",
                    StringComparison.Ordinal)
                && String.Equals(
                    loadedBackground.Gradient.To,
                    "#0000FF",
                    StringComparison.Ordinal),
                "渐变颜色未正确持久化。",
                failures);
            Check(String.Equals(
                    loadedBackground.Image.OriginalUrl,
                    "https://example.com/a.png",
                    StringComparison.Ordinal),
                "背景图片链接未正确持久化。",
                failures);

            string backgroundJson = File.ReadAllText(
                backgroundStore.SettingsPath,
                Encoding.UTF8);
            Check(backgroundJson.Contains("\"mode\"")
                && backgroundJson.Contains("\"gradient\"")
                && backgroundJson.Contains("\"originalUrl\""),
                "background.json 字段名不符合约定。",
                failures);

            File.WriteAllText(backgroundStore.SettingsPath, "{", Encoding.UTF8);
            BackgroundSettings recoveredBackground = backgroundStore.Load(out warning);
            Check(!String.IsNullOrWhiteSpace(warning),
                "损坏背景配置未返回警告。",
                failures);
            Check(String.Equals(
                    recoveredBackground.Mode,
                    BackgroundModes.Color,
                    StringComparison.Ordinal),
                "损坏背景配置未恢复为默认模式。",
                failures);
            Check(File.Exists(backgroundStore.SettingsPath),
                "损坏背景配置未重建。",
                failures);

            List<Rectangle> screens = new List<Rectangle>();
            screens.Add(new Rectangle(0, 0, 1920, 1080));
            Rectangle normalized = WindowPlacement.Normalize(
                new Rectangle(50000, 50000, 640, 420),
                screens,
                screens[0]);
            Check(Rectangle.Intersect(normalized, screens[0]).Width >= 64
                && Rectangle.Intersect(normalized, screens[0]).Height >= 64,
                "屏幕外窗口未移回主屏幕。",
                failures);
            Check(normalized.Width == 640 && normalized.Height == 420,
                "窗口坐标归一化改变了有效尺寸。",
                failures);
        }

        private static void AttachParentConsole()
        {
            try
            {
                NativeMethods.AttachConsole(-1);
                StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                stdout.AutoFlush = true;
                Console.SetOut(stdout);

                StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
                stderr.AutoFlush = true;
                Console.SetError(stderr);
            }
            catch
            {
            }
        }

        private static void Check(bool condition, string message, List<string> failures)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
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
