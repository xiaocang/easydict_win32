using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Easydict.IconTools
{
    public static class IconRasterizer
    {
        public static Bitmap Render(Image source, int size, bool dark, bool smoothDarkOutline = false)
        {
            if (dark && smoothDarkOutline)
            {
                const int supersampling = 4;
                using (var large = Render(source, size * supersampling, false))
                using (var outlined = AddOutline(large, Math.Max(2, size * supersampling / 128), true))
                    return Render(outlined, size, false);
            }
            var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            scaled.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Explicit pixel rectangles: never scale by source/desktop DPI.
                graphics.DrawImage(source, new Rectangle(0, 0, size, size),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
            if (!dark) return scaled;
            using (scaled)
                return AddOutline(scaled, Math.Max(1, size / 64), false);
        }

        private static Bitmap AddOutline(Bitmap scaled, int radius, bool circular)
        {
            var size = scaled.Width;
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            result.SetResolution(96, 96);
            var sourceAlpha = new byte[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                sourceAlpha[y * size + x] = scaled.GetPixel(x, y).A;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var alpha = 0;
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (circular && dx * dx + dy * dy > radius * radius) continue;
                    if (x + dx >= 0 && x + dx < size && y + dy >= 0 && y + dy < size)
                        alpha = Math.Max(alpha, sourceAlpha[(y + dy) * size + x + dx]);
                }
                // Do not expand near-transparent export noise into a light halo.
                if (circular && alpha <= 8) alpha = 0;
                result.SetPixel(x, y, Color.FromArgb(alpha, 240, 244, 250));
            }
            using (var graphics = Graphics.FromImage(result))
                graphics.DrawImage(scaled, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel);
            return result;
        }
    }
}
