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
            string key = text + "|" + px + "|" + look + "|" + a.Width * unit + "|" + a.Alpha + "|" + a.Color.R + "," + a.Color.G + "," + a.Color.B + "|" + FontName;
            lock (cache) if (cache.TryGetValue(key, out var c)) return c;
            var sp = Label(a, text, px, unit, look);
            lock (cache) { if (cache.Count > 200) cache.Clear(); cache[key] = sp; }
            return sp;
        }

        static Color Rgb(double r, double g, double b, double a = 1) => Color.FromArgb((int)Math.Round(a * 255), (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));

        // 一个文字标签的小图（照 Mac 版 Overlays.swift 的 draw() 里 .text 那段，11 种样式各一个 case）：
        // 四周留 2 倍线宽（同 Mac 的 box），HotX / HotY = 标签左上角、BoxW / BoxH = 标签多大；阴影的 offset 往下、blur 按 2σ；
        // 玻璃后面的透镜在 OverlayLayer.Apply 里做（要拿到画面）；不透明度（opacity）乘在整张图上（同 c.setAlpha）
        static Sprite Label(Annotation a, string text, float px, double unit, string look)
        {
            using (var font = new Font(FontName, px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var (bw, bh, tw, asc, desc) = Box(text, font, px, look);
                int m = (int)Math.Ceiling(Math.Max(1, a.Width * unit) * 2);
                int W = (int)Math.Ceiling(bw) + 2 * m, H = (int)Math.Ceiling(bh) + 2 * m;
                var o = new float[W * H * 4];
                double tail = look == "bubble" ? px * 0.4 : 0;
                var body = new RectangleF(m, m, (float)bw, (float)(bh - tail));   // 气泡的小尾巴在下面
                var r = a.Color; var col = Rgb(r.R, r.G, r.B);
                double l = 0.299 * r.R + 0.587 * r.G + 0.114 * r.B;
                bool light = l > 0.75;
                var ink = Rgb(0.11, 0.11, 0.12);
                Color fg;
                switch (look)
                {
                    case "pill": case "bubble": fg = l > 0.6 ? Rgb(0.1, 0.1, 0.11) : Color.White; break;
                    case "dark": fg = light ? col : Color.White; break;
                    case "glass": case "marker": fg = ink; break;
                    case "glassDark": case "gradient": fg = Color.White; break;
                    case "neon": fg = light ? Rgb(0.2, 0.9, 1) : col; break;
                    case "sticker": case "outline": fg = light ? ink : col; break;
                    default: fg = col; break;
                }
                GraphicsPath Capsule(RectangleF rr) => Theme.Round(rr, rr.Height / 2);
                const double soft = 0.3;
                switch (look)
                {
                    case "pill":
                        Shadowed(o, W, H, g => { using (var p = Capsule(body)) using (var b = new SolidBrush(col)) g.FillPath(b, p); }, px * 0.06, px * 0.3, soft);
                        break;
                    case "bubble":
                        Shadowed(o, W, H, g =>
                        {
                            using (var p = Theme.Round(body, body.Height * 0.32f)) using (var b = new SolidBrush(col))
                            {
                                g.FillPath(b, p);
                                float bot = body.Bottom, h0 = body.Height;
                                g.FillPolygon(b, new[] { new PointF(body.X + h0 * 0.45f, bot - 1), new PointF(body.X + h0 * 0.3f, (float)(m + bh)), new PointF(body.X + h0 * 0.95f, bot - 1) });
                            }
                        }, px * 0.06, px * 0.3, soft);
                        break;
                    case "dark":
                        Shadowed(o, W, H, g => { using (var p = Capsule(body)) using (var b = new SolidBrush(Rgb(0.08, 0.08, 0.08, 0.78))) g.FillPath(b, p); }, px * 0.06, px * 0.35, soft);
                        break;
                    case "glass": case "glassDark":
                        Glass.Chrome(o, W, H, body.X, body.Y, body.Width, body.Height, px, look == "glassDark");
                        break;
                    case "gradient":
                    {
                        // 先填一个胶囊（系统默认黑色）带影子，再在胶囊里画渐变：从选的颜色到它偏紫、亮一点的颜色
                        Shadowed(o, W, H, g => { using (var p = Capsule(body)) g.FillPath(Brushes.Black, p); }, px * 0.06, px * 0.35, soft);
                        var c2 = Rgb(Math.Min(1, r.R * 0.7 + 0.35), r.G * 0.55 + 0.1, Math.Min(1, r.B * 0.6 + 0.6));
                        Paint(o, W, H, g =>
                        {
                            using (var p = Capsule(body))
                            using (var lg = new LinearGradientBrush(new PointF(body.Left - 0.5f, 0), new PointF(body.Right + 0.5f, 0), col, c2)) g.FillPath(lg, p);
                        });
                        break;
                    }
                    case "sticker":
                        // 贴纸：白底 + 投影，里面一圈彩色描边
                        Shadowed(o, W, H, g => { using (var p = Theme.Round(body, body.Height * 0.28f)) g.FillPath(Brushes.White, p); }, px * 0.08, px * 0.35, 0.35);
                        Paint(o, W, H, g =>
                        {
                            float ins = px * 0.14f; var rr = RectangleF.Inflate(body, -ins, -ins);
                            using (var p = Theme.Round(rr, body.Height * 0.2f)) using (var pen = new Pen(col, Math.Max(1.5f, px * 0.07f))) g.DrawPath(pen, p);
                        });
                        break;
                    case "marker":
                    {
                        // 荧光笔：字的下半截刷一道半透明的色（左下原点：从底边往上 8% 起、高 50%）
                        var mk = new RectangleF(body.X, body.Bottom - body.Height * 0.08f - body.Height * 0.5f, body.Width, body.Height * 0.5f);
                        Paint(o, W, H, g => { using (var p = Theme.Round(mk, mk.Height * 0.25f)) using (var b = new SolidBrush(Rgb(r.R, r.G, r.B, 0.5))) g.FillPath(b, p); });
                        break;
                    }
                    case "outline":
                        Shadowed(o, W, H, g => { using (var p = Capsule(body)) using (var b = new SolidBrush(Rgb(1, 1, 1, 0.92))) g.FillPath(b, p); }, px * 0.05, px * 0.3, 0.18);
                        Paint(o, W, H, g =>
                        {
                            float ins = px * 0.04f; var rr = RectangleF.Inflate(body, -ins, -ins);
                            using (var p = Theme.Round(rr, body.Height / 2 - ins)) using (var pen = new Pen(col, Math.Max(1.5f, px * 0.08f))) g.DrawPath(pen, p);
                        });
                        break;
                }
                // 字：水平居中、上下沿居中（Mac：textPosition = (midX − 字宽 / 2, midY − (asc − desc) / 2)，左下原点）
                float tx = (float)(body.X + body.Width / 2 - tw / 2), ty = (float)(body.Y + body.Height / 2 - (asc + desc) / 2);
                Action<Graphics> drawText = g =>
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces })
                    using (var b = new SolidBrush(fg)) g.DrawString(text, font, b, tx, ty, sf);
                };
                if (look == "neon")
                {
                    // 霓虹：字发光——先带大光晕（blur 0.55 字号）画一遍，再带小光晕（0.2 字号）画一遍
                    Shadowed(o, W, H, drawText, 0, px * 0.55, 1, col);
                    Shadowed(o, W, H, drawText, 0, px * 0.2, 1, col);
                }
                else if (look == "plain") Shadowed(o, W, H, drawText, px * 0.05, px * 0.25, 0.55);
                else Paint(o, W, H, drawText);
                double al = a.Alpha;
                if (al < 0.999) for (int i = 0; i < o.Length; i++) o[i] *= (float)al;
                return new Sprite { W = W, H = H, Px = o, Ppp = 1, HotX = m, HotY = m, BoxW = bw, BoxH = bh };
            }
        }

        // 画一层叠上去（sRGB 里「盖上去」，同 Core Graphics）
        static void Paint(float[] o, int W, int H, Action<Graphics> draw)
        {
            var layer = new float[W * H * 4];
            Over(layer, W, H, draw);
            for (int i = 0; i < W * H; i++)
            {
                float a = layer[i * 4 + 3]; if (a <= 0) continue;
                for (int k = 0; k < 4; k++) o[i * 4 + k] = layer[i * 4 + k] + o[i * 4 + k] * (1 - a);
            }
        }

        // 带影子画一层（同 CGContext.setShadow）：影子 = 这一层的不透明度往下挪 dy、高斯 σ = blur / 2、乘 shadowA、颜色 sc（默认黑），垫在这一层下面
        static void Shadowed(float[] o, int W, int H, Action<Graphics> draw, double dy, double blur, double shadowA, Color? sc = null)
        {
            var layer = new float[W * H * 4];
            Over(layer, W, H, draw);
            var sh = new float[W * H];
            int off = (int)Math.Round(dy);
            for (int y = 0; y < H; y++) { int sy = y - off; if (sy < 0 || sy >= H) continue; for (int x = 0; x < W; x++) sh[y * W + x] = layer[(sy * W + x) * 4 + 3]; }
            Sprites.Blur(sh, W, H, blur / 2);
            var c = sc ?? Color.Black; float cb = c.B / 255f, cg = c.G / 255f, cr = c.R / 255f;
            for (int i = 0; i < W * H; i++)
            {
                float s = (float)(sh[i] * shadowA);
                if (s > 0)
                {
                    o[i * 4] = cb * s + o[i * 4] * (1 - s); o[i * 4 + 1] = cg * s + o[i * 4 + 1] * (1 - s); o[i * 4 + 2] = cr * s + o[i * 4 + 2] * (1 - s);
                    o[i * 4 + 3] = s + o[i * 4 + 3] * (1 - s);
                }
                float a = layer[i * 4 + 3]; if (a <= 0) continue;
                for (int k = 0; k < 4; k++) o[i * 4 + k] = layer[i * 4 + k] + o[i * 4 + k] * (1 - a);
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
