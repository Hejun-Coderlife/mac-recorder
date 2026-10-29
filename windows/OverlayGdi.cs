// 文字标注的真字（GDI+，微软雅黑 粗）：照 Mac 版 Overlays.swift 的 textBox / textLine——底色胶囊里深底白字、浅底黑字；不带胶囊时字带一点阴影
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    static class OverlayGdi
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();
        public static string FontName = "Microsoft YaHei UI";

        public static Sprite Text(Annotation a, double unit)
        {
            var text = string.IsNullOrEmpty(a.Text) ? L("文字", "Text") : a.Text;
            float px = (float)Math.Max(4, a.FontSize * unit);
            string key = text + "|" + px + "|" + a.Pill + "|" + a.Color.R + "," + a.Color.G + "," + a.Color.B + "|" + FontName;
            lock (cache) if (cache.TryGetValue(key, out var c)) return c;
            using (var font = new Font(FontName, px, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
            {
                var size = pg.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
                float pad = a.Pill ? px * 0.45f : 0, lineH = font.GetHeight(pg);
                int W = (int)Math.Ceiling(Math.Max(size.Width, px) + pad * 2 + (a.Pill ? 0 : px * 0.2f)), H = (int)Math.Ceiling(lineH + pad * 1.2f + (a.Pill ? 0 : px * 0.15f));
                var col = Color.FromArgb((int)Math.Round(a.Color.R * 255), (int)Math.Round(a.Color.G * 255), (int)Math.Round(a.Color.B * 255));
                double l = 0.299 * a.Color.R + 0.587 * a.Color.G + 0.114 * a.Color.B;
                var fg = a.Pill ? (l > 0.6 ? Color.FromArgb(26, 26, 28) : Color.White) : col;
                using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias;
                        var box = new RectangleF(0, 0, W, H - (a.Pill ? Math.Max(1, px * 0.06f) : 0));
                        var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        if (a.Pill)
                        {
                            using (var sp = Theme.Round(new RectangleF(box.X, box.Y + px * 0.06f, box.Width, box.Height), box.Height / 2))
                            using (var sb = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillPath(sb, sp);
                            using (var pp = Theme.Round(box, box.Height / 2))
                            using (var pb = new SolidBrush(col)) g.FillPath(pb, pp);
                        }
                        else
                            using (var shb = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                                g.DrawString(text, font, shb, new RectangleF(box.X + px * 0.03f, box.Y + px * 0.06f, box.Width, box.Height), sf);
                        using (var tb = new SolidBrush(fg)) g.DrawString(text, font, tb, box, sf);
                    }
                    var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var bytes = new byte[W * H * 4];
                    for (int y = 0; y < H; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, bytes, y * W * 4, W * 4);
                    bmp.UnlockBits(bd);
                    var s = Sprites.FromBgra(bytes, W, H, 1, 0, 0);
                    lock (cache) { if (cache.Count > 200) cache.Clear(); cache[key] = s; }
                    return s;
                }
            }
        }
    }
}
