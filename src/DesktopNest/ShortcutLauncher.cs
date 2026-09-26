using System;
using System.Diagnostics;
using System.IO;

namespace DesktopNest
{
    internal sealed class LaunchResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal sealed class ShortcutLauncher
    {
        public LaunchResult Open(string storedPath, string targetHint)
        {
            LaunchResult result = new LaunchResult();

            if (String.IsNullOrWhiteSpace(storedPath)
                || (!File.Exists(storedPath) && !Directory.Exists(storedPath)))
            {
                result.Success = false;
                result.ErrorMessage = "快捷方式副本已丢失。";
                return result;
            }

            if (String.Equals(Path.GetExtension(storedPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string targetPath = ShortcutImporter.TryGetTargetPath(storedPath);
                if (String.IsNullOrWhiteSpace(targetPath))
                {
                    targetPath = targetHint;
                }

                if (!String.IsNullOrWhiteSpace(targetPath)
                    && !File.Exists(targetPath)
                    && !Directory.Exists(targetPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "目标已不存在：" + Environment.NewLine + targetPath;
                    return result;
                }
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = storedPath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "启动失败：" + ex.Message;
                return result;
            }
        }
    }
}
