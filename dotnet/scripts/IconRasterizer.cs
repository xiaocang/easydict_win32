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

        /// <summary>
        /// Square crop around opaque pixels so a tray slot is mostly artwork.
        /// <paramref name="padFraction"/> is extra transparent margin on each side,
        /// relative to the longer side of the artwork. It leaves room for a dark outline.
        /// </summary>
        public static Bitmap TightSquare(Image source, double padFraction)
        {
            if (padFraction < 0 || padFraction > 0.2)
                throw new ArgumentOutOfRangeException("padFraction");

            using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            {
                bitmap.SetResolution(96, 96);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
                }

                var minX = bitmap.Width;
                var minY = bitmap.Height;
                var maxX = -1;
                var maxY = -1;
                var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var stride = data.Stride;
                    var bytes = new byte[stride * bitmap.Height];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    for (var y = 0; y < bitmap.Height; y++)
                    {
                        var row = y * stride;
                        for (var x = 0; x < bitmap.Width; x++)
                        {
                            if (bytes[row + x * 4 + 3] < 16) continue;
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                if (maxX < minX || maxY < minY)
                    throw new InvalidOperationException("Icon has no opaque pixels to crop.");

                var contentWidth = maxX - minX + 1;
                var contentHeight = maxY - minY + 1;
                var longSide = Math.Max(contentWidth, contentHeight);
                var pad = Math.Max(1, (int)Math.Round(longSide * padFraction));
                var side = longSide + pad * 2;
                var square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
                square.SetResolution(96, 96);
                using (var graphics = Graphics.FromImage(square))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(bitmap,
                        new Rectangle((side - contentWidth) / 2, (side - contentHeight) / 2, contentWidth, contentHeight),
                        new Rectangle(minX, minY, contentWidth, contentHeight),
                        GraphicsUnit.Pixel);
                }
                return square;
            }
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
