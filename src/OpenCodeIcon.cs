using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AIUsageTray
{
    internal static class OpenCodeIcon
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();

        internal static Icon Get(int pixels, bool dark)
        {
            string key = pixels.ToString(System.Globalization.CultureInfo.InvariantCulture) + (dark ? "d" : "l");
            lock (Gate)
            {
                Icon cached;
                if (!Cache.TryGetValue(key, out cached))
                {
                    cached = Render(pixels, dark);
                    Cache.Add(key, cached);
                }
                return (Icon)cached.Clone();
            }
        }

        internal static void ClearCache()
        {
            lock (Gate)
            {
                foreach (Icon icon in Cache.Values) icon.Dispose();
                Cache.Clear();
            }
        }

        private static Icon Render(int pixels, bool dark)
        {
            Color ink = dark ? Color.White : Color.FromArgb(32, 32, 32);
            using (Bitmap small = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb))
            using (Graphics output = Graphics.FromImage(small))
            using (Bitmap large = RenderLarge(pixels, ink))
            {
                output.InterpolationMode = InterpolationMode.HighQualityBicubic;
                output.PixelOffsetMode = PixelOffsetMode.HighQuality;
                output.DrawImage(large, new Rectangle(0, 0, pixels, pixels));
                // Keep the background transparent and the mark fully opaque.
                for (int y = 0; y < pixels; y++)
                    for (int x = 0; x < pixels; x++)
                        small.SetPixel(x, y, small.GetPixel(x, y).A < 64 ? Color.Transparent : ink);
                IntPtr handle = small.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { Native.DestroyIcon(handle); }
            }
        }

        private static Bitmap RenderLarge(int pixels, Color ink)
        {
            const int scale = 8;
            int size = Math.Max(1, pixels) * scale;
            Bitmap large = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(large))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float inset = size * 0.10f;
                float border = Math.Max(size * 0.09f, scale * 1.2f);
                float inner = size * 0.32f;
                using (Pen pen = new Pen(ink, border))
                using (SolidBrush brush = new SolidBrush(ink))
                {
                    pen.Alignment = PenAlignment.Inset;
                    graphics.DrawRectangle(pen, inset, inset, size - inset * 2f, size - inset * 2f);
                    graphics.FillRectangle(brush, (size - inner) / 2f, (size - inner) / 2f, inner, inner);
                }
            }
            return large;
        }
    }
}
