using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopNest
{
    internal static class WindowPlacement
    {
        public const int MinimumWidth = 420;
        public const int MinimumHeight = 300;

        public static Rectangle Normalize(
            Rectangle requested,
            IList<Rectangle> visibleBounds,
            Rectangle primaryBounds)
        {
            int width = Math.Max(MinimumWidth, requested.Width);
            int height = Math.Max(MinimumHeight, requested.Height);

            if (primaryBounds.Width > 0)
            {
                width = Math.Min(width, Math.Max(MinimumWidth, primaryBounds.Width));
            }

            if (primaryBounds.Height > 0)
            {
                height = Math.Min(height, Math.Max(MinimumHeight, primaryBounds.Height));
            }

            Rectangle normalized = new Rectangle(requested.X, requested.Y, width, height);
            if (IsVisibleEnough(normalized, visibleBounds))
            {
                return normalized;
            }

            int x = primaryBounds.Left + Math.Max(0, (primaryBounds.Width - width) / 2);
            int y = primaryBounds.Top + Math.Max(0, (primaryBounds.Height - height) / 2);
            return new Rectangle(x, y, width, height);
        }

        private static bool IsVisibleEnough(Rectangle bounds, IList<Rectangle> visibleBounds)
        {
            if (visibleBounds == null || visibleBounds.Count == 0)
            {
                return false;
            }

            const int minimumVisible = 64;
            for (int i = 0; i < visibleBounds.Count; i++)
            {
                Rectangle intersection = Rectangle.Intersect(bounds, visibleBounds[i]);
                if (intersection.Width >= minimumVisible && intersection.Height >= minimumVisible)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
