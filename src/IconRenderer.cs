using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace ObsRecIndicator
{
    public static class IconRenderer
    {
        // Fractions of the icon size.
        const float DotSize = 0.42f;
        const float OverlaySize = 0.58f;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
            IntPtr[] icons, int[] ids, uint count, uint flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        /// <summary>Loads an image from .png/.jpg/.bmp/.ico or the first icon of an .exe/.dll.</summary>
        public static Bitmap LoadImage(string path, int size)
        {
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".exe" || ext == ".dll")
                {
                    var handles = new IntPtr[1];
                    var ids = new int[1];
                    if (PrivateExtractIcons(path, 0, size, size, handles, ids, 1, 0) == 0 || handles[0] == IntPtr.Zero)
                        return null;
                    try
                    {
                        using (var icon = Icon.FromHandle(handles[0]))
                            return icon.ToBitmap();
                    }
                    finally { DestroyIcon(handles[0]); }
                }
                if (ext == ".ico")
                {
                    using (var icon = new Icon(path, size, size))
                        return icon.ToBitmap();
                }
                using (var img = Image.FromFile(path))
                    return new Bitmap(img);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Stand-in when the OBS icon cannot be loaded: a dark disc.</summary>
        public static Bitmap FallbackBase(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x30)))
            using (var pen = new Pen(Color.FromArgb(0xC0, 0xC0, 0xC0), Math.Max(1f, size / 16f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(brush, 1, 1, size - 2, size - 2);
                g.DrawEllipse(pen, 1, 1, size - 2, size - 2);
            }
            return bmp;
        }

        /// <summary>The app's own icon (exe, installer, shortcuts): a dark disc with a red record dot.</summary>
        public static Bitmap AppIcon(int size)
        {
            var bmp = FallbackBase(size);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(Config.DefaultIndicatorColor))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = size * 0.5f;
                g.FillEllipse(brush, (size - d) / 2, (size - d) / 2, d, d);
            }
            return bmp;
        }

        /// <summary>Writes a multi-size .ico: 32-bit DIB entries, PNG for 256 px.</summary>
        public static void WriteIco(string path, int[] sizes, Func<int, Bitmap> render)
        {
            var images = new List<byte[]>();
            foreach (var size in sizes)
                using (var bmp = render(size))
                    images.Add(size >= 256 ? ToPng(bmp) : ToDib(bmp));

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }

        static byte[] ToPng(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] ToDib(Bitmap bmp)
        {
            int n = bmp.Width;
            int maskStride = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                // BITMAPINFOHEADER; height is doubled to cover the AND mask.
                w.Write(40); w.Write(n); w.Write(n * 2);
                w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(n * n * 4 + maskStride * n);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--)
                    for (int x = 0; x < n; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                // AND mask: all zero, the alpha channel does the work.
                w.Write(new byte[maskStride * n]);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Base icon (greyed when idle), a recording dot top-left when active,
        /// and the tracker's overlay (image or text badge) bottom-right.
        /// </summary>
        public static Bitmap Compose(int size, Bitmap baseImg, bool active, Color dotColor,
            Bitmap overlayImg, string overlayText, Color overlayColor)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                DrawImage(g, baseImg, new Rectangle(0, 0, size, size), !active);

                int o = (int)Math.Round(size * OverlaySize);
                var overlayRect = new Rectangle(size - o, size - o, o, o);
                if (overlayImg != null)
                    DrawImage(g, overlayImg, overlayRect, !active);
                else if (!string.IsNullOrEmpty(overlayText))
                    DrawBadge(g, overlayRect, overlayText, active ? overlayColor : Color.Gray);

                if (active)
                {
                    int d = (int)Math.Round(size * DotSize);
                    float outline = Math.Max(1f, size / 16f);
                    var r = new RectangleF(outline / 2, outline / 2, d, d);
                    using (var brush = new SolidBrush(dotColor))
                    using (var pen = new Pen(Color.White, outline))
                    {
                        g.FillEllipse(brush, r);
                        g.DrawEllipse(pen, r);
                    }
                }
            }
            return bmp;
        }

        static void DrawImage(Graphics g, Bitmap img, Rectangle dest, bool grey)
        {
            if (!grey)
            {
                g.DrawImage(img, dest);
                return;
            }
            var m = new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.55f, 0 },
                new[] { 0f, 0, 0, 0, 1 },
            });
            using (var attrs = new ImageAttributes())
            {
                attrs.SetColorMatrix(m);
                g.DrawImage(img, dest, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
            }
        }

        static void DrawBadge(Graphics g, Rectangle r, string text, Color color)
        {
            float radius = r.Width * 0.3f;
            using (var path = RoundedRect(r, radius))
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(Color.White, Math.Max(1f, r.Width / 12f)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            // Shrink the font until the text fits inside the badge.
            float fontPx = r.Height * (text.Length > 1 ? 0.62f : 0.78f);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var textBrush = new SolidBrush(Color.White))
            {
                Font font = null;
                try
                {
                    while (true)
                    {
                        font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        if (fontPx <= 5 || g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= r.Width * 0.9f)
                            break;
                        font.Dispose();
                        fontPx -= 1;
                    }
                    var rf = new RectangleF(r.X, r.Y + r.Height * 0.03f, r.Width, r.Height);
                    g.DrawString(text, font, textBrush, rf, fmt);
                }
                finally { if (font != null) font.Dispose(); }
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
