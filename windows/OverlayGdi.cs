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
            string look = a.TextLook;
            string key = text + "|" + px + "|" + look + "|" + a.Width * unit + "|" + a.Color.R + "," + a.Color.G + "," + a.Color.B + "|" + FontName;
            lock (cache) if (cache.TryGetValue(key, out var c)) return c;
            if (look == "glass" || look == "glassDark")
            {
                var gs = GlassLabel(a, text, px, unit, look == "glassDark");
                lock (cache) { if (cache.Count > 200) cache.Clear(); cache[key] = gs; }
                return gs;
            }
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
                    s.BoxW = W; s.BoxH = H;
                    lock (cache) { if (cache.Count > 200) cache.Clear(); cache[key] = s; }
                    return s;
                }
            }
        }

        // 标签多大（照 Mac 版 textBox）：宽 = max(字宽, 字号) + 左右留白，高 = 字的上下沿 + 留白 × 1.2；留白按样式
        public static (double w, double h, double tw, double asc, double desc) Box(string text, Font font, float px, string look)
        {
            var fam = font.FontFamily; var st = font.Style;
            double em = fam.GetEmHeight(st), asc = px * fam.GetCellAscent(st) / em, desc = px * fam.GetCellDescent(st) / em;
            double tw;
            using (var probe = new Bitmap(1, 1)) using (var g = Graphics.FromImage(probe))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces })
                tw = g.MeasureString(text, font, PointF.Empty, sf).Width;
            double pad = look == "plain" ? 0 : look == "marker" ? px * 0.2 : look == "neon" ? px * 0.35 : look == "sticker" ? px * 0.5 : px * 0.45;
            double tail = look == "bubble" ? px * 0.4 : 0;
            return (Math.Max(tw, px) + pad * 2, asc + desc + pad * 1.2 + tail, tw, asc, desc);
        }

        // 玻璃标签的小图：四周留 2 倍线宽画阴影（同 Mac 版 draw() 的 box），中间是体积感（Glass.Chrome）+ 深灰字（深色玻璃白字）；
        // 后面的透镜在 OverlayLayer.Apply 里做（要拿到画面）
        static Sprite GlassLabel(Annotation a, string text, float px, double unit, bool dark)
        {
            using (var font = new Font(FontName, px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var (bw, bh, tw, asc, desc) = Box(text, font, px, dark ? "glassDark" : "glass");
                int m = (int)Math.Ceiling(Math.Max(1, a.Width * unit) * 2);
                int W = (int)Math.Ceiling(bw) + 2 * m, H = (int)Math.Ceiling(bh) + 2 * m;
                var px4 = new float[W * H * 4];
                Glass.Chrome(px4, W, H, m, m, bw, bh, px, dark);
                var fg = dark ? Color.White : Color.FromArgb(28, 28, 31);   // (0.11, 0.11, 0.12)
                using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces })
                    using (var tb = new SolidBrush(fg))
                    {
                        g.TextRenderingHint = TextRenderingHint.AntiAlias;
                        // 字水平居中、上下沿居中（同 Mac：textPosition = (midX − 字宽 / 2, midY − (asc − desc) / 2)，左下原点）
                        g.DrawString(text, font, tb, (float)(m + bw / 2 - tw / 2), (float)(m + bh / 2 - (asc + desc) / 2), sf);
                    }
                    var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var bytes = new byte[W * H * 4];
                    for (int y = 0; y < H; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, bytes, y * W * 4, W * 4);
                    bmp.UnlockBits(bd);
                    // 字叠在体积感上面（小图里按 sRGB 叠，同 Core Graphics）
                    for (int i = 0; i < W * H; i++)
                    {
                        float ta = bytes[i * 4 + 3] / 255f;
                        if (ta <= 0) continue;
                        for (int c = 0; c < 3; c++) px4[i * 4 + c] = bytes[i * 4 + c] / 255f * ta + px4[i * 4 + c] * (1 - ta);
                        px4[i * 4 + 3] = ta + px4[i * 4 + 3] * (1 - ta);
                    }
                }
                return new Sprite { W = W, H = H, Px = px4, Ppp = 1, HotX = m, HotY = m, BoxW = bw, BoxH = bh };
            }
        }
        // ---- 字幕图（照 Mac 版 CaptionLayer.render）：一行字，太长就缩小字号放进画布宽的 90%（连左右留白）；
        // 有底（深色 / 浅色 / 玻璃）时：左右留白 0.8 字号（玻璃 0.45）、圆角 0.45 字号、字不描边不加阴影；玻璃底四周再留 0.9 字号画浮起的影子 ----
        static readonly Dictionary<string, Sprite> capCache = new Dictionary<string, Sprite>();
        public static Sprite Caption(string text, CaptionStyle st, double cw, double ch)
        {
            string key = text + "|" + st.ToJson() + "|" + (int)cw + "x" + (int)ch + "|" + FontName;
            lock (capCache) if (capCache.TryGetValue(key, out var c)) return c;
            var style = st.Weight == "regular" ? FontStyle.Regular : FontStyle.Bold;
            float px0 = (float)st.FontPx(cw, ch), px = px0;
            bool none = st.Box == "none", glass = st.Box == "glass";
            double padK = none ? 0.25 : glass ? 0.45 : 0.8;
            using (var fam = new FontFamily(FontName))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces })
            using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe))
            {
                double Width(float p) { using (var f = new Font(fam, p, style, GraphicsUnit.Pixel)) return pg.MeasureString(text, f, PointF.Empty, sf).Width; }
                double w = Width(px), maxW = cw * 0.9;
                if (w + 2 * padK * px > maxW) { px = (float)(px * maxW / (w + 2 * padK * px)); w = Width(px); }
                double em = fam.GetEmHeight(style), asc = px * fam.GetCellAscent(style) / em, desc = px * fam.GetCellDescent(style) / em;
                double padX = px * padK, padY = px * (none ? 0.22 : glass ? 0.3 : 0.34);
                int gm = glass ? (int)Math.Round(px0 * 0.9) : 0;
                int bw = (int)Math.Ceiling(w + padX * 2), bh = (int)Math.Ceiling(asc + desc + padY * 2);
                int W = bw + 2 * gm, H = bh + 2 * gm;
                if (W <= 0 || H <= 0) return null;
                var outPx = new float[W * H * 4];
                if (glass) Glass.Chrome(outPx, W, H, gm, gm, bw, bh, px, false);
                else if (!none)
                    Over(outPx, W, H, g =>
                    {
                        using (var path = Theme.Round(new RectangleF(gm, gm, bw, bh), px * 0.45f))
                        using (var b = new SolidBrush(st.Box == "dark" ? Color.FromArgb((int)Math.Round(0.74 * 255), 18, 18, 22) : Color.FromArgb((int)Math.Round(0.88 * 255), 255, 255, 255))) g.FillPath(b, path);
                    });
                // 字：基线离底边 留白 + 下沿（Mac 左下原点的 origin.y = gm + padY + desc）
                float top = (float)(H - gm - padY - desc - asc), left = (float)(gm + padX);
                using (var path = new GraphicsPath())
                {
                    path.AddString(text, fam, (int)style, px, new PointF(left, top), sf);
                    bool plain = !none;
                    var col = glass ? Color.FromArgb(28, 28, 31) : Color.FromArgb((int)Math.Round(st.Color.R * 255), (int)Math.Round(st.Color.G * 255), (int)Math.Round(st.Color.B * 255));
                    float strokeW = px * 0.14f;   // kCTStrokeWidth 14 = 字号的 14%（描在字的轮廓上，里外各一半）
                    if (st.Shadow && !plain)
                    {
                        // 影子：往下 0.04 字号、blur 0.16 字号（σ = 一半）、黑 55%，跟着描边（没描边就跟着字）
                        var mask = new float[W * H];
                        Over(null, W, H, g =>
                        {
                            if (st.Outline) using (var pen = new Pen(Color.White, strokeW) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                            g.FillPath(Brushes.White, path);
                        }, mask, 0, px * 0.04f);
                        Sprites.Blur(mask, W, H, px * 0.16 / 2);
                        for (int i = 0; i < W * H; i++) { float a = mask[i] * 0.55f; if (a <= 0) continue; for (int k = 0; k < 3; k++) outPx[i * 4 + k] *= 1 - a; outPx[i * 4 + 3] = a + outPx[i * 4 + 3] * (1 - a); }
                    }
                    Over(outPx, W, H, g =>
                    {
                        if (st.Outline && !plain) using (var pen = new Pen(Color.FromArgb((int)Math.Round(0.85 * 255), 0, 0, 0), strokeW) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                        using (var b = new SolidBrush(col)) g.FillPath(b, path);
                    });
                }
                var sp = new Sprite { W = W, H = H, Px = outPx, Ppp = 1, HotX = gm, HotY = gm, BoxW = bw, BoxH = bh };
                lock (capCache) { if (capCache.Count > 100) capCache.Clear(); capCache[key] = sp; }
                return sp;
            }
        }

        // 用 GDI+ 画一层（抗锯齿），按 sRGB「盖上去」叠进预乘小图 dst；mask != null 时只取这一层的不透明度（往下挪 dy）
        static void Over(float[] dst, int W, int H, Action<Graphics> draw, float[] mask = null, float dx = 0, float dy = 0)
        {
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (dx != 0 || dy != 0) g.TranslateTransform(dx, dy);
                    draw(g);
                }
                var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var b = new byte[W * H * 4];
                for (int y = 0; y < H; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, b, y * W * 4, W * 4);
                bmp.UnlockBits(bd);
                for (int i = 0; i < W * H; i++)
                {
                    float a = b[i * 4 + 3] / 255f;
                    if (mask != null) { mask[i] = a; continue; }
                    if (a <= 0) continue;
                    for (int k = 0; k < 3; k++) dst[i * 4 + k] = b[i * 4 + k] / 255f * a + dst[i * 4 + k] * (1 - a);
                    dst[i * 4 + 3] = a + dst[i * 4 + 3] * (1 - a);
                }
            }
        }
    }
}
