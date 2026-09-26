using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopNest
{
    internal static class AppConstants
    {
        public const string ProductName = "DesktopNest";
        public const string DefaultTitle = "软件收纳";
        public const string AppUserModelId = "DesktopNest.App";
        public const string MutexName = @"Local\DesktopNest.SingleInstance";
        public const string ActivationEventName = @"Local\DesktopNest.Activate";
        public const int SettingsVersion = 1;
        public const int MaxItems = 64;
    }

    internal static class BackgroundModes
    {
        public const string Color = "color";
        public const string Gradient = "gradient";
        public const string Image = "image";
    }

    internal static class BackgroundDirections
    {
        public const string TopToBottom = "topToBottom";
        public const string BottomToTop = "bottomToTop";
        public const string LeftToRight = "leftToRight";
        public const string Diagonal = "diagonal";
    }

    internal static class BackgroundImageSources
    {
        public const string Local = "local";
        public const string Url = "url";
        public const string Wallpaper = "wallpaper";
    }

    internal sealed class BackgroundSettings
    {
        public const string DefaultColor = "#FFFFFF";
        public const string DefaultGradientFrom = "#F5F7FA";
        public const string DefaultGradientTo = "#DCE6F2";

        public BackgroundSettings()
        {
            Mode = BackgroundModes.Color;
            Color = DefaultColor;
            Gradient = new BackgroundGradientSettings();
            Image = new BackgroundImageSettings();
        }

        public string Mode { get; set; }
        public string Color { get; set; }
        public BackgroundGradientSettings Gradient { get; set; }
        public BackgroundImageSettings Image { get; set; }

        public static BackgroundSettings CreateDefault()
        {
            return new BackgroundSettings();
        }

        public void EnsureValid()
        {
            if (!String.Equals(Mode, BackgroundModes.Color, StringComparison.Ordinal)
                && !String.Equals(Mode, BackgroundModes.Gradient, StringComparison.Ordinal)
                && !String.Equals(Mode, BackgroundModes.Image, StringComparison.Ordinal))
            {
                Mode = BackgroundModes.Color;
            }

            if (String.IsNullOrWhiteSpace(Color))
            {
                Color = DefaultColor;
            }

            if (Gradient == null)
            {
                Gradient = new BackgroundGradientSettings();
            }

            Gradient.EnsureValid();

            if (Image == null)
            {
                Image = new BackgroundImageSettings();
            }

            Image.EnsureValid();
        }
    }

    internal sealed class BackgroundGradientSettings
    {
        public BackgroundGradientSettings()
        {
            From = BackgroundSettings.DefaultGradientFrom;
            To = BackgroundSettings.DefaultGradientTo;
            Direction = BackgroundDirections.TopToBottom;
        }

        public string From { get; set; }
        public string To { get; set; }
        public string Direction { get; set; }

        public void EnsureValid()
        {
            if (String.IsNullOrWhiteSpace(From))
            {
                From = BackgroundSettings.DefaultGradientFrom;
            }

            if (String.IsNullOrWhiteSpace(To))
            {
                To = BackgroundSettings.DefaultGradientTo;
            }

            if (!String.Equals(Direction, BackgroundDirections.TopToBottom, StringComparison.Ordinal)
                && !String.Equals(Direction, BackgroundDirections.BottomToTop, StringComparison.Ordinal)
                && !String.Equals(Direction, BackgroundDirections.LeftToRight, StringComparison.Ordinal)
                && !String.Equals(Direction, BackgroundDirections.Diagonal, StringComparison.Ordinal))
            {
                Direction = BackgroundDirections.TopToBottom;
            }
        }
    }

    internal sealed class BackgroundImageSettings
    {
        public BackgroundImageSettings()
        {
            Source = String.Empty;
            Path = String.Empty;
            OriginalUrl = String.Empty;
        }

        public string Source { get; set; }
        public string Path { get; set; }
        public string OriginalUrl { get; set; }

        public void EnsureValid()
        {
            if (!String.Equals(Source, BackgroundImageSources.Local, StringComparison.Ordinal)
                && !String.Equals(Source, BackgroundImageSources.Url, StringComparison.Ordinal)
                && !String.Equals(Source, BackgroundImageSources.Wallpaper, StringComparison.Ordinal))
            {
                Source = String.Empty;
            }

            if (Path == null)
            {
                Path = String.Empty;
            }

            if (OriginalUrl == null)
            {
                OriginalUrl = String.Empty;
            }
        }
    }

    internal sealed class AppSettings
    {
        public AppSettings()
        {
            Title = AppConstants.DefaultTitle;
            Version = AppConstants.SettingsVersion;
            Width = 640;
            Height = 720;
            X = -1;
            Y = -1;
            Items = new List<ShortcutItem>();
        }

        public int Version { get; set; }
        public string Title { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMaximized { get; set; }
        public double IconScale { get; set; }
        public bool FreeArrange { get; set; }
        public int Capacity { get; set; }
        public List<ShortcutItem> Items { get; set; }

        public static AppSettings CreateDefault()
        {
            return new AppSettings();
        }

        public Rectangle GetBounds()
        {
            return new Rectangle(X, Y, Width, Height);
        }

        public void SetBounds(Rectangle bounds)
        {
            X = bounds.X;
            Y = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        public void EnsureValid()
        {
            if (Version <= 0)
            {
                Version = AppConstants.SettingsVersion;
            }

            if (String.IsNullOrWhiteSpace(Title))
            {
                Title = AppConstants.DefaultTitle;
            }

            if (Width < 420)
            {
                Width = 640;
            }

            if (Height < 300)
            {
                Height = 720;
            }

            if (Double.IsNaN(IconScale) || IconScale <= 0)
            {
                IconScale = 1.0;
            }

            if (IconScale < 0.5)
            {
                IconScale = 0.5;
            }

            if (IconScale > 2.0)
            {
                IconScale = 2.0;
            }

            // 容量：0 或负数视为未设置（旧配置）→ 默认 20；1~3 钳到最小 4；上限 100
            if (Capacity <= 0)
            {
                Capacity = 20;
            }

            if (Capacity < 4)
            {
                Capacity = 4;
            }

            if (Capacity > 100)
            {
                Capacity = 100;
            }

            if (Items == null)
            {
                Items = new List<ShortcutItem>();
            }

            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i] == null)
                {
                    Items.RemoveAt(i);
                    continue;
                }

                Items[i].EnsureValid();
            }
        }
    }

    internal sealed class ShortcutItem
    {
        public ShortcutItem()
        {
            Id = Guid.NewGuid().ToString("N");
            DisplayName = String.Empty;
            ShortcutFileName = String.Empty;
            TargetPathHint = String.Empty;
            OriginalPath = String.Empty;
            Col = -1;
            Row = -1;
        }

        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string ShortcutFileName { get; set; }
        public string TargetPathHint { get; set; }
        public string OriginalPath { get; set; }
        public int Col { get; set; }
        public int Row { get; set; }

        public void EnsureValid()
        {
            if (String.IsNullOrWhiteSpace(Id))
            {
                Id = Guid.NewGuid().ToString("N");
            }

            if (DisplayName == null)
            {
                DisplayName = String.Empty;
            }

            if (ShortcutFileName == null)
            {
                ShortcutFileName = String.Empty;
            }

            if (TargetPathHint == null)
            {
                TargetPathHint = String.Empty;
            }

            if (OriginalPath == null)
            {
                OriginalPath = String.Empty;
            }

            if (Col < -1)
            {
                Col = -1;
            }

            if (Row < -1)
            {
                Row = -1;
            }
        }
    }
}
