// 遮挡（模糊 / 马赛克 / 色块）和标注（箭头、框、圈、高亮、文字）（照 Mac 版 Overlays.swift）：都画在原画面上（位置用原画面比例、
// 时间用素材时间），所以推近时跟着画面一起放大挪动，像是录的时候就在那里。合成顺序：原画面 → 遮挡 → 标注 → 点击波纹、鼠标 → 镜头推拉。
// 直接改原画面（BGRA）再交给合成：整幅只重采样一次（显卡、CPU 两条路一样），不会因为加了遮挡字变软。
// 纯 C#（不碰 WinForms），Mac 上的 .NET 8 小工具也能跑；文字标注的真字由 Windows 程序用 GDI+ 画（TextRenderer 指过去）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ScreenRecorder
{
    // 遮挡一块：x、y、w、h 是原画面里的比例（左上为原点）；字段名同 Mac 版
    sealed class Redaction
    {
        public static readonly string[] Styles = { "blur", "mosaic", "solid", "frosted", "spotlight", "focus" };   // 后三种是 Mac 新加的：毛玻璃、聚光灯（框外变暗）、聚焦（框外模糊）
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public double Start, End;
        public double X, Y, W, H;
        public string Style = "blur";
        public double Strength = 0.6;               // 0–1：模糊多糊 / 马赛克格子多大
        public RGB Color = new RGB(0.12, 0.12, 0.14);  // 色块的颜色
        public double Corner = 8;                   // 圆角（原画面的点）
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        static readonly string[] Known = { "id", "start", "end", "x", "y", "w", "h", "style", "strength", "color", "corner" };

        public Redaction Clone() { var c = (Redaction)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        public void Write(JsonW w)
        {
            w.BeginItem();
            w.Rgb("color", Color); w.Num("corner", Corner); w.Num("end", End); w.Num("h", H); w.Str("id", Id); w.Num("start", Start);
            w.Num("strength", Strength); w.Str("style", Style); w.Num("w", W); w.Num("x", X); w.Num("y", Y);
            w.Extra(Extra); w.End();
        }
        public static Redaction From(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var j = new JsonR(o); var z = new Redaction();
            return new Redaction
            {
                Id = j.Str("id", z.Id), Start = j.Num("start", 0), End = j.Num("end", 0), X = j.Num("x", 0), Y = j.Num("y", 0), W = j.Num("w", 0), H = j.Num("h", 0),
                Style = j.Str("style", z.Style, Styles), Strength = j.Num("strength", z.Strength), Color = j.Rgb("color", z.Color), Corner = j.Num("corner", z.Corner),
                Extra = JsonR.Unknown(o, Known),
            };
        }
        public static string Name(string s, Func<string, string, string> L)
        {
            switch (s)
            {
                case "mosaic": return L("马赛克", "Mosaic"); case "solid": return L("色块", "Solid"); case "frosted": return L("毛玻璃", "Frosted");
                case "spotlight": return L("聚光灯", "Spotlight"); case "focus": return L("聚焦", "Focus"); default: return L("模糊", "Blur");
            }
        }
        public static string Hint(string s, Func<string, string, string> L)
        {
            switch (s)
            {
                case "mosaic": return L("框里打马赛克", "Pixelates inside the box"); case "solid": return L("框里盖一块纯色", "Covers the box with a color");
                case "frosted": return L("框里像磨砂玻璃：模糊再蒙一层白", "Frosted glass: blurred and lightened");
                case "spotlight": return L("框外面变暗，突出框里", "Darkens everything outside the box"); case "focus": return L("框外面模糊，突出框里", "Blurs everything outside the box");
                default: return L("框里模糊", "Blurs inside the box");
            }
        }
    }

    // 标注：箭头从 (x, y) 指到 (x + w, y + h)；框 / 圈 / 高亮是这个矩形；文字的左上角在 (x, y)，大小按字算；字段名同 Mac 版
    sealed class Annotation
    {
        public static readonly string[] Kinds = { "arrow", "rect", "ellipse", "highlight", "text" };
        public static readonly RGB[] Palette = { new RGB(1.0, 0.27, 0.23), new RGB(1.0, 0.62, 0.04), new RGB(1.0, 0.84, 0.04), new RGB(0.20, 0.78, 0.35),
                                                 new RGB(0.04, 0.52, 1.0), new RGB(0.69, 0.32, 0.87), new RGB(1, 1, 1), new RGB(0.11, 0.11, 0.12) };
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public double Start, End;
        public string Kind = "arrow";
        public double X, Y, W, H;
        public RGB Color = new RGB(1.0, 0.27, 0.23);
        public double Width = 5;                    // 线宽（原画面的点）
        public string Text = "";
        public double FontSize = 28;                // 字号（原画面的点）
        public bool Pill = true;                    // 文字带底色胶囊
        public bool DrawOn = true;                  // 箭头像笔画出来那样出现
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        static readonly string[] Known = { "id", "start", "end", "kind", "x", "y", "w", "h", "color", "width", "text", "fontSize", "pill", "drawOn" };

        public static readonly string[] TextLooks = { "pill", "glass", "glassDark", "dark", "gradient", "outline", "sticker", "bubble", "neon", "marker", "plain" };
        public static readonly string[] ArrowStyles = { "plain", "double", "curved", "thick", "dashed" };
        // Mac 版后加的可选字段：没有 = 旧行为。放在 Extra 里（原样存回），这里只是按名字读写
        public string TextStyle { get => ExtraStr("textStyle", TextLooks); set => SetExtra("textStyle", value); }   // 文字标签样式
        public string Arrow { get => ExtraStr("arrow", ArrowStyles); set => SetExtra("arrow", value); }            // 箭头样式
        public bool? Oval { get => Extra.TryGetValue("oval", out var v) && v is bool b ? b : (bool?)null; set => SetExtra("oval", value); }   // 圈：true = 椭圆；没有 = 正圆
        public double? Opacity { get => Extra.TryGetValue("opacity", out var v) && v is double d ? d : (double?)null; set => SetExtra("opacity", value); }
        public string TextLook => TextStyle ?? (Pill ? "pill" : "plain");
        // 不透明度：没有 = 高亮 35%、其他 100%（同 Mac 版 alpha）
        public double Alpha => Math.Min(Math.Max(Opacity ?? (Kind == "highlight" ? 0.35 : 1), 0.05), 1);
        string ExtraStr(string k, string[] allowed) => Extra.TryGetValue(k, out var v) && v is string s && allowed.Contains(s) ? s : null;
        void SetExtra(string k, object v) { if (v == null) Extra.Remove(k); else Extra[k] = v; }

        public Annotation Clone() { var c = (Annotation)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        public void Write(JsonW w)
        {
            w.BeginItem();
            w.Rgb("color", Color); w.Bool("drawOn", DrawOn); w.Num("end", End); w.Num("fontSize", FontSize); w.Num("h", H); w.Str("id", Id); w.Str("kind", Kind);
            w.Bool("pill", Pill); w.Num("start", Start); w.Str("text", Text); w.Num("w", W); w.Num("width", Width); w.Num("x", X); w.Num("y", Y);
            w.Extra(Extra); w.End();
        }
        public static Annotation From(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var j = new JsonR(o); var z = new Annotation();
            return new Annotation
            {
                Id = j.Str("id", z.Id), Start = j.Num("start", 0), End = j.Num("end", 0), Kind = j.Str("kind", z.Kind, Kinds),
                X = j.Num("x", 0), Y = j.Num("y", 0), W = j.Num("w", 0), H = j.Num("h", 0), Color = j.Rgb("color", z.Color), Width = j.Num("width", z.Width),
                Text = j.Str("text", ""), FontSize = j.Num("fontSize", z.FontSize), Pill = j.Bool("pill", z.Pill), DrawOn = j.Bool("drawOn", z.DrawOn),
                Extra = JsonR.Unknown(o, Known),
            };
        }
        public static string Name(string k, Func<string, string, string> L)
        {
            switch (k) { case "arrow": return L("箭头", "Arrow"); case "rect": return L("框", "Box"); case "ellipse": return L("圈", "Circle"); case "highlight": return L("高亮", "Highlight"); default: return L("文字", "Text"); }
        }
        // 名字同 Mac 版 textLookName / arrowName
        public static string TextLookName(string t, Func<string, string, string> L)
        {
            switch (t)
            {
                case "glass": return L("玻璃（浅）", "Glass"); case "glassDark": return L("玻璃（深）", "Dark glass"); case "dark": return L("深色", "Dark");
                case "gradient": return L("渐变", "Gradient"); case "outline": return L("描边", "Outline"); case "sticker": return L("贴纸", "Sticker");
                case "bubble": return L("对话气泡", "Bubble"); case "neon": return L("霓虹", "Neon"); case "marker": return L("荧光笔", "Marker");
                case "plain": return L("只有字", "Plain"); default: return L("彩色胶囊", "Pill");
            }
        }
        public static string ArrowName(string s, Func<string, string, string> L)
        {
            switch (s)
            {
                case "double": return L("双向", "Double"); case "curved": return L("弧形", "Curved"); case "thick": return L("粗箭头", "Bold");
                case "dashed": return L("虚线", "Dashed"); default: return L("普通", "Plain");
            }
        }
    }

    // 出现、消失：淡入淡出 0.25 秒；箭头的「画出来」0.4 秒（同 Mac 版）
    static class OverlayAnim
    {
        public const double Fade = 0.25, Draw = 0.4;
        public static double Alpha(double t, double s, double e)
        {
            if (t < s || t >= e) return 0;
            double f = Math.Min(Fade, (e - s) / 2);
            return Math.Min(1, Math.Min((t - s) / f, (e - t) / f));
        }
        public static double DrawProgress(double t, double s)
        {
            double u = Math.Min(Math.Max((t - s) / Draw, 0), 1);
            return 1 - Math.Pow(1 - u, 3);
        }
    }

    // 把遮挡、标注画进原画面（BGRA，W×H，y 朝下）。unit = 原画面 1 点占几个像素（track.PixelsPerPoint，预览缩小后的也行）
    sealed class OverlayLayer
    {
        readonly List<Redaction> reds;
        readonly List<Annotation> anns;
        public bool IsEmpty => reds.Count == 0 && anns.Count == 0;
        // 文字标注画成一张小图（预乘 BGRA，Ppp = 1：一个小图像素 = 一个原画面像素）；null = 不画字（Mac 上的核心自测）
        public static Func<Annotation, double, Sprite> TextRenderer = PlainText;
        // 箭头、框、圈、高亮：Windows 程序用 GDI+ 照 Mac 版画（OverlayGdi.Shape：5 种箭头、虚线、弧线）；null = 下面的简化画法（Mac 上的核心自测）
        public static Func<Annotation, double, int, int, double, (Sprite sp, int x0, int y0)> ShapeRenderer;

        public OverlayLayer(StudioProject p) { reds = p.Redactions.ToList(); anns = p.Annotations.ToList(); }
        public static OverlayLayer Make(StudioProject p) { var o = new OverlayLayer(p); return o.IsEmpty ? null : o; }

        // 这一刻有没有要画的（没有就不用复制原画面）
        public bool ActiveAt(double t) => reds.Any(r => t >= r.Start && t < r.End) || anns.Any(a => OverlayAnim.Alpha(t, a.Start, a.End) > 0.002);

        // src 原样不动，画好的放进 dst（同样大小）；返回 false = 这一刻什么都没有，dst 没碰
        public bool Apply(byte[] src, byte[] dst, int W, int H, double t, double unit)
        {
            if (!ActiveAt(t)) return false;
            Buffer.BlockCopy(src, 0, dst, 0, W * H * 4);
            foreach (var r in reds) if (t >= r.Start && t < r.End) Redact(src, dst, W, H, r, unit);
            foreach (var a in anns)
            {
                double al = OverlayAnim.Alpha(t, a.Start, a.End);
                if (al <= 0.002) continue;
                var (sp, x0, y0) = Draw(a, t, W, H, unit);
                if (sp == null) continue;
                // 玻璃标签：先把标签后面那块画面做成透镜，再叠上体积感和字（同 Mac 版 glass()）
                // 同 Mac 版：都在线性光里叠
                var area = Glass.Load(dst, W, H, x0, y0, sp.W, sp.H);
                if (a.Kind == "text" && (a.TextLook == "glass" || a.TextLook == "glassDark") && sp.BoxW > 2)
                {
                    int bx = x0 + (int)sp.HotX, by = y0 + (int)sp.HotY;
                    Glass.Lens(dst, W, H, bx, by, (int)Math.Ceiling(sp.BoxW), (int)Math.Ceiling(sp.BoxH), Math.Ceiling(sp.BoxH) / 2, 1, a.TextLook == "glassDark", al, area, x0, y0, sp.W, sp.H);
                }
                Glass.Composite(area, sp.W, sp.H, sp, 0, 0, al);
                Glass.Store(area, dst, W, H, x0, y0, sp.W, sp.H);
            }
            return true;
        }

        // ---- 遮挡 ----
        public static (int x0, int y0, int x1, int y1) PixelRect(Redaction r, int W, int H)
        {
            double ax = r.X * W, ay = r.Y * H, bx = (r.X + r.W) * W, by = (r.Y + r.H) * H;
            int x0 = (int)Math.Floor(Math.Min(ax, bx)), y0 = (int)Math.Floor(Math.Min(ay, by)), x1 = (int)Math.Ceiling(Math.Max(ax, bx)), y1 = (int)Math.Ceiling(Math.Max(ay, by));
            return (Math.Max(0, x0), Math.Max(0, y0), Math.Min(W, x1), Math.Min(H, y1));
        }

        // 照 Mac 版 redact()：效果从当前画面（前面的遮挡已经画上）算，在线性光里（同 Core Image），k = strength：
        // blur 高斯 σ = (3 + 22k)·unit；mosaic 格子 max(2, (6 + 30k)·unit)、从框左下角排起、取格子中心那一点的颜色（CIPixellate）；solid 纯色；
        // frosted 高斯 σ = (8 + 26k)·unit → 饱和度 ×1.15、亮度 +0.04 → 蒙白 (0.28 + 0.2k)；
        // spotlight 框外蒙黑 (0.45 + 0.45k)；focus 框外高斯 σ = (4 + 22k)·unit；圆角 = min(corner·unit, 短边 / 2)
        static void Redact(byte[] src, byte[] dst, int W, int H, Redaction r, double unit)
        {
            var (x0, y0, x1, y1) = PixelRect(r, W, H);
            int rw = x1 - x0, rh = y1 - y0;
            if (rw < 2 || rh < 2) return;
            double k = Math.Min(Math.Max(r.Strength, 0), 1);
            bool outside = r.Style == "spotlight" || r.Style == "focus";
            // 效果算在哪块：框里的样式只算框，框外的样式算整幅
            int ex0 = outside ? 0 : x0, ey0 = outside ? 0 : y0, ew = outside ? W : rw, eh = outside ? H : rh;
            float[] eff;
            switch (r.Style)
            {
                case "blur": eff = Blurred(dst, W, H, ex0, ey0, ew, eh, (3 + 22 * k) * unit); break;
                case "focus": eff = Blurred(dst, W, H, ex0, ey0, ew, eh, (4 + 22 * k) * unit); break;
                case "frosted":
                {
                    eff = Blurred(dst, W, H, ex0, ey0, ew, eh, (8 + 26 * k) * unit);
                    float wa = (float)(0.28 + 0.2 * k);
                    for (int i = 0; i < ew * eh; i++)
                    {
                        float B = eff[i * 3], G = eff[i * 3 + 1], R = eff[i * 3 + 2], lum = 0.2125f * R + 0.7154f * G + 0.0721f * B;
                        B = lum + 1.15f * (B - lum) + 0.04f; G = lum + 1.15f * (G - lum) + 0.04f; R = lum + 1.15f * (R - lum) + 0.04f;
                        eff[i * 3] = B * (1 - wa) + wa; eff[i * 3 + 1] = G * (1 - wa) + wa; eff[i * 3 + 2] = R * (1 - wa) + wa;
                    }
                    break;
                }
                case "mosaic":
                {
                    double cell = Math.Max(2, (6 + 30 * k) * unit);
                    var img = Glass.Load(dst, W, H, 0, 0, W, H);   // 取样可能落在框外（格子伸出框），按整幅取、画面外按边缘
                    eff = new float[ew * eh * 3];
                    Parallel.For(0, eh, y =>
                    {
                        // 格子从框的左下角排起（Core Image 左下原点），每格取格子中心那一点（双线性，同 CIPixellate）
                        double fromBottom = y1 - (ey0 + y + 0.5);
                        double cy = y1 - (Math.Floor(fromBottom / cell) + 0.5) * cell;
                        for (int x = 0; x < ew; x++)
                        {
                            double cx = x0 + (Math.Floor((ex0 + x + 0.5 - x0) / cell) + 0.5) * cell;
                            double fx = Math.Min(W - 1.0, Math.Max(0, cx - 0.5)), fy = Math.Min(H - 1.0, Math.Max(0, cy - 0.5));
                            int ix = Math.Min(W - 2, (int)fx), iy = Math.Min(H - 2, (int)fy); float tx = (float)(fx - ix), ty = (float)(fy - iy);
                            int i00 = (iy * W + ix) * 3, i01 = i00 + 3, i10 = i00 + W * 3, i11 = i10 + 3, o = (y * ew + x) * 3;
                            for (int c = 0; c < 3; c++)
                                eff[o + c] = (img[i00 + c] * (1 - tx) + img[i01 + c] * tx) * (1 - ty) + (img[i10 + c] * (1 - tx) + img[i11 + c] * tx) * ty;
                        }
                    });
                    break;
                }
                case "spotlight":
                    eff = null;   // 框外蒙黑：只和原来的颜色有关，下面查表
                    break;
                default:
                {
                    eff = new float[ew * eh * 3];
                    float b = Lin.Of((float)r.Color.B), g = Lin.Of((float)r.Color.G), rr = Lin.Of((float)r.Color.R);
                    for (int i = 0; i < ew * eh; i++) { eff[i * 3] = b; eff[i * 3 + 1] = g; eff[i * 3 + 2] = rr; }
                    break;
                }
            }
            // 圆角遮罩（系统的圆角矩形，抗锯齿）；没圆角就是整块
            double rad = Math.Min(r.Corner * unit, Math.Min(rw, rh) / 2.0);
            double cxr = x0 + rw / 2.0, cyr = y0 + rh / 2.0;
            byte[] dark = null;
            if (eff == null)
            {
                float keep = (float)(1 - (0.45 + 0.45 * k));
                dark = new byte[256]; for (int i = 0; i < 256; i++) dark[i] = Lin.Byte(Lin.Of((byte)i) * keep);
            }
            Parallel.For(ey0, ey0 + eh, y =>
            {
                for (int x = ex0; x < ex0 + ew; x++)
                {
                    double m = 0;
                    if (x >= x0 && x < x1 && y >= y0 && y < y1)
                        m = rad > 0.5 ? Math.Min(1, Math.Max(0, 0.5 - RoundRectDist(x + 0.5, y + 0.5, cxr, cyr, rw / 2.0, rh / 2.0, rad))) : 1;
                    double we = outside ? 1 - m : m;   // 效果占多少
                    if (we <= 0) continue;
                    int i = (y * W + x) * 4, o = ((y - ey0) * ew + (x - ex0)) * 3;
                    if (dark != null)
                    {
                        if (we >= 1) { dst[i] = dark[dst[i]]; dst[i + 1] = dark[dst[i + 1]]; dst[i + 2] = dark[dst[i + 2]]; }
                        else for (int c = 0; c < 3; c++) dst[i + c] = Lin.Byte((float)(Lin.Of(dark[dst[i + c]]) * we + Lin.Of(dst[i + c]) * (1 - we)));
                        continue;
                    }
                    for (int c = 0; c < 3; c++) dst[i + c] = Lin.Byte((float)(eff[o + c] * we + Lin.Of(dst[i + c]) * (1 - we)));
                }
            });
        }

        // 当前画面的一块（线性光）做高斯模糊：往外多取 3σ，画面外按边缘延伸（同 clampedToExtent）。
        // σ 大时先按 f×f 求平均缩小、在小图上模糊（σ / f）、再双线性放大回来：看不出差别，聚焦（整幅、σ ≈ 17）从 80 ms 降到十几 ms
        static float[] Blurred(byte[] img, int W, int H, int x0, int y0, int w, int h, double sigma)
        {
            int m = (int)Math.Ceiling(sigma * 3);
            int f = Math.Max(1, (int)Math.Floor(sigma / 3));
            int bx0 = x0 - m, by0 = y0 - m, bw = w + 2 * m, bh = h + 2 * m;
            int dw = (bw + f - 1) / f, dh = (bh + f - 1) / f;
            var chs = new float[3][];
            for (int c = 0; c < 3; c++) chs[c] = new float[dw * dh];
            float inv = 1f / (f * f);
            Parallel.For(0, dh, y =>
            {
                for (int x = 0; x < dw; x++)
                {
                    float sb = 0, sg = 0, sr = 0;
                    for (int yy = 0; yy < f; yy++)
                    {
                        int sy = Math.Min(H - 1, Math.Max(0, by0 + y * f + yy));
                        for (int xx = 0; xx < f; xx++)
                        {
                            int sx = Math.Min(W - 1, Math.Max(0, bx0 + x * f + xx)), i = (sy * W + sx) * 4;
                            sb += Lin.Of(img[i]); sg += Lin.Of(img[i + 1]); sr += Lin.Of(img[i + 2]);
                        }
                    }
                    int o = y * dw + x; chs[0][o] = sb * inv; chs[1][o] = sg * inv; chs[2][o] = sr * inv;
                }
            });
            double sd = Math.Sqrt(Math.Max(0.25, sigma * sigma - (f * f - 1) / 12.0)) / f;   // 求平均本身也糊了一点，扣掉
            Parallel.For(0, 3, c => Sprites.Blur(chs[c], dw, dh, sd));
            var o3 = new float[w * h * 3];
            Parallel.For(0, h, y =>
            {
                double fy = Math.Min(dh - 1.0, Math.Max(0, (y + m + 0.5) / f - 0.5));
                int iy = Math.Min(dh - 2, (int)fy); if (iy < 0) iy = 0; float ty = dh < 2 ? 0 : (float)(fy - iy);
                int iy1 = Math.Min(dh - 1, iy + 1);
                for (int x = 0; x < w; x++)
                {
                    double fx = Math.Min(dw - 1.0, Math.Max(0, (x + m + 0.5) / f - 0.5));
                    int ix = Math.Min(dw - 2, (int)fx); if (ix < 0) ix = 0; float tx = dw < 2 ? 0 : (float)(fx - ix);
                    int ix1 = Math.Min(dw - 1, ix + 1);
                    for (int c = 0; c < 3; c++)
                    {
                        var a = chs[c];
                        o3[(y * w + x) * 3 + c] = (a[iy * dw + ix] * (1 - tx) + a[iy * dw + ix1] * tx) * (1 - ty) + (a[iy1 * dw + ix] * (1 - tx) + a[iy1 * dw + ix1] * tx) * ty;
                    }
                }
            });
            return o3;
        }

        // 圆角矩形（中心 cx, cy、半宽 hw、半高 hh、圆角 r）的有符号距离
        static double RoundRectDist(double px, double py, double cx, double cy, double hw, double hh, double r)
        {
            double qx = Math.Abs(px - cx) - (hw - r), qy = Math.Abs(py - cy) - (hh - r);
            return qx > 0 && qy > 0 ? Math.Sqrt(qx * qx + qy * qy) - r : Math.Max(qx, qy) - r;
        }

        // ---- 标注：画成一张小图（只画它占的那块），返回小图和它左上角在原画面里的像素位置 ----
        public static (Sprite sp, int x0, int y0) Draw(Annotation a, double t, int W, int H, double unit)
        {
            double lw = Math.Max(1, a.Width * unit);
            if (a.Kind == "text")
            {
                var ts = TextRenderer?.Invoke(a, unit);
                if (ts == null) return (null, 0, 0);
                return (ts, (int)Math.Round(a.X * W) - (int)ts.HotX, (int)Math.Round(a.Y * H) - (int)ts.HotY);
            }
            if (ShapeRenderer != null) return ShapeRenderer(a, t, W, H, unit);
            double ax = a.X * W, ay = a.Y * H, bx = (a.X + a.W) * W, by = (a.Y + a.H) * H;
            double minx = Math.Min(ax, bx), miny = Math.Min(ay, by), maxx = Math.Max(ax, bx), maxy = Math.Max(ay, by);
            double pad = lw * 4 + 4;
            int x0 = (int)Math.Floor(minx - pad), y0 = (int)Math.Floor(miny - pad), x1 = (int)Math.Ceiling(maxx + pad), y1 = (int)Math.Ceiling(maxy + pad);
            int sw = x1 - x0, sh = y1 - y0;
            if (sw < 2 || sh < 2 || (long)sw * sh > 40_000_000) return (null, 0, 0);
            var cov = new float[sw * sh];     // 形状（0–1）
            double R = a.Color.R, G = a.Color.G, B = a.Color.B, fillAlpha = 1;
            double shOff = lw * 0.35, shBlur = lw * 0.9, shA = 0.35;
            switch (a.Kind)
            {
                case "arrow":
                {
                    double u = a.DrawOn ? OverlayAnim.DrawProgress(t, a.Start) : 1;
                    double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                    if (len <= 1) return (null, 0, 0);
                    double dx = (bx - ax) / len, dy = (by - ay) / len, head = Math.Min(len * 0.45, lw * 4.2);
                    double tipx = ax + (bx - ax) * u, tipy = ay + (by - ay) * u;
                    // 杆子画到箭头根部；头部在画到最后 40% 时长出来
                    double ex = len * u > head ? tipx - dx * head * 0.6 : tipx, ey = len * u > head ? tipy - dy * head * 0.6 : tipy;
                    double hs = head * Math.Min(1, Math.Max(0, (u - 0.6) / 0.4));
                    double hbx = tipx - dx * hs, hby = tipy - dy * hs, wx = -dy * hs * 0.55, wy = dx * hs * 0.55;
                    var tri = new[] { (tipx, tipy), (hbx + wx, hby + wy), (hbx - wx, hby - wy) };
                    Parallel.For(0, sh, y =>
                    {
                        for (int x = 0; x < sw; x++)
                        {
                            double px = x0 + x + 0.5, py = y0 + y + 0.5;
                            double d = SegDist(px, py, ax, ay, ex, ey) - lw / 2;
                            if (hs > 0.5) d = Math.Min(d, TriDist(px, py, tri));
                            cov[y * sw + x] = (float)Math.Min(1, Math.Max(0, 0.5 - d));
                        }
                    });
                    shOff = lw * 0.4; shBlur = lw * 1.2;
                    break;
                }
                case "rect":
                case "ellipse":
                {
                    double cx = (minx + maxx) / 2, cy = (miny + maxy) / 2, hw = (maxx - minx) / 2, hh = (maxy - miny) / 2;
                    double rr = Math.Min(lw * 1.5, Math.Min(hw, hh));
                    bool ell = a.Kind == "ellipse";
                    Parallel.For(0, sh, y =>
                    {
                        for (int x = 0; x < sw; x++)
                        {
                            double px = x0 + x + 0.5, py = y0 + y + 0.5;
                            double d = ell ? EllipseDist(px - cx, py - cy, Math.Max(0.5, hw), Math.Max(0.5, hh)) : RoundRectDist(px, py, cx, cy, hw, hh, rr);
                            cov[y * sw + x] = (float)Math.Min(1, Math.Max(0, 0.5 - (Math.Abs(d) - lw / 2)));
                        }
                    });
                    shOff = lw * 0.3; shBlur = lw;
                    break;
                }
                default:   // highlight：半透明色块，没有阴影
                {
                    double cx = (minx + maxx) / 2, cy = (miny + maxy) / 2, hw = (maxx - minx) / 2, hh = (maxy - miny) / 2;
                    double rr = Math.Min(6 * unit, Math.Min(hw, hh));
                    Parallel.For(0, sh, y =>
                    {
                        for (int x = 0; x < sw; x++)
                        {
                            double px = x0 + x + 0.5, py = y0 + y + 0.5;
                            cov[y * sw + x] = (float)Math.Min(1, Math.Max(0, 0.5 - RoundRectDist(px, py, cx, cy, hw, hh, rr)));
                        }
                    });
                    fillAlpha = 0.32; shA = 0;
                    break;
                }
            }
            // 阴影：形状往下挪一点、模糊、黑色 35%，垫在形状下面
            float[] shadow = null;
            if (shA > 0)
            {
                shadow = new float[sw * sh];
                int off = (int)Math.Round(shOff);
                for (int y = 0; y < sh; y++) { int sy = y - off; if (sy < 0 || sy >= sh) continue; Array.Copy(cov, sy * sw, shadow, y * sw, sw); }
                Sprites.Blur(shadow, sw, sh, shBlur / 2);
            }
            var s = new Sprite { W = sw, H = sh, Px = new float[sw * sh * 4], Ppp = 1 };
            for (int i = 0; i < sw * sh; i++)
            {
                float fa = (float)(cov[i] * fillAlpha);
                float sa = shadow != null ? (float)(shadow[i] * shA) * (1 - fa) : 0;
                float A = fa + sa;
                int o = i * 4;
                s.Px[o] = (float)(B * fa); s.Px[o + 1] = (float)(G * fa); s.Px[o + 2] = (float)(R * fa); s.Px[o + 3] = A;
            }
            return (s, x0, y0);
        }

        static double SegDist(double px, double py, double ax, double ay, double bx, double by)
        {
            double vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy;
            double u = l2 > 1e-9 ? Math.Min(1, Math.Max(0, ((px - ax) * vx + (py - ay) * vy) / l2)) : 0;
            double qx = ax + vx * u - px, qy = ay + vy * u - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        // 三角形的有符号距离（里面为负）
        static double TriDist(double px, double py, (double x, double y)[] t)
        {
            double d = double.MaxValue; bool inside = true;
            double sign = Cross(t[0], t[1], t[2]) >= 0 ? 1 : -1;
            for (int i = 0; i < 3; i++)
            {
                var a = t[i]; var b = t[(i + 1) % 3];
                d = Math.Min(d, SegDist(px, py, a.x, a.y, b.x, b.y));
                if (sign * ((b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x)) < 0) inside = false;
            }
            return inside ? -d : d;
        }
        static double Cross((double x, double y) a, (double x, double y) b, (double x, double y) c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        // 椭圆的近似有符号距离（够画描边用）
        static double EllipseDist(double x, double y, double a, double b)
        {
            double k1 = Math.Sqrt(x * x / (a * a) + y * y / (b * b)), k2 = Math.Sqrt(x * x / (a * a * a * a) + y * y / (b * b * b * b));
            return k2 < 1e-9 ? -Math.Min(a, b) : k1 * (k1 - 1) / k2;
        }

        // 小图（预乘）叠到原画面上
        static void Blend(byte[] dst, int W, int H, Sprite sp, int x0, int y0, double alpha)
        {
            float al = (float)alpha;
            int ys = Math.Max(0, -y0), ye = Math.Min(sp.H, H - y0), xs = Math.Max(0, -x0), xe = Math.Min(sp.W, W - x0);
            if (ys >= ye || xs >= xe) return;
            Parallel.For(ys, ye, y =>
            {
                for (int x = xs; x < xe; x++)
                {
                    int s = (y * sp.W + x) * 4; float a = sp.Px[s + 3] * al;
                    if (a <= 0.001f) continue;
                    int d = ((y0 + y) * W + x0 + x) * 4;
                    for (int c = 0; c < 3; c++) dst[d + c] = (byte)Math.Min(255, sp.Px[s + c] * al * 255 + dst[d + c] * (1 - a) + 0.5f);
                }
            });
        }

        // 标注在原画面比例里占的框（编辑框、命中用；左上为原点）；箭头是两个端点围成的框；文字按画出来的大小
        public static (double x, double y, double w, double h) NormBounds(Annotation a, int W, int H, double unit)
        {
            if (a.Kind == "text")
            {
                var ts = TextRenderer?.Invoke(a, unit);
                double tw = ts != null ? (ts.BoxW > 0 ? ts.BoxW : ts.W) : a.FontSize * unit * Math.Max(1, (a.Text ?? "").Length), th = ts != null ? (ts.BoxH > 0 ? ts.BoxH : ts.H) : a.FontSize * unit * 1.4;
                return (a.X, a.Y, tw / W, th / H);
            }
            return (Math.Min(a.X, a.X + a.W), Math.Min(a.Y, a.Y + a.H), Math.Abs(a.W), Math.Abs(a.H));
        }

        // 没有字体时的文字：底色胶囊里一排小方块（大小和真的差不多，自测够用）
        static Sprite PlainText(Annotation a, double unit)
        {
            double px = Math.Max(4, a.FontSize * unit);
            int n = Math.Max(1, (string.IsNullOrEmpty(a.Text) ? 2 : a.Text.Length));
            double pad = a.Pill ? px * 0.45 : 0;
            int W = (int)Math.Ceiling(n * px + pad * 2), H = (int)Math.Ceiling(px * 1.3 + pad * 1.2);
            var s = new Sprite { W = W, H = H, Px = new float[W * H * 4], Ppp = 1 };
            var c = a.Color; double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            double fr = a.Pill ? (l > 0.6 ? 0.1 : 1) : c.R, fg = a.Pill ? (l > 0.6 ? 0.1 : 1) : c.G, fb = a.Pill ? (l > 0.6 ? 0.11 : 1) : c.B;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double pxl = x + 0.5, pyl = y + 0.5;
                    float pa = a.Pill ? (float)Math.Min(1, Math.Max(0, 0.5 - RoundRectDist(pxl, pyl, W / 2.0, H / 2.0, W / 2.0, H / 2.0, H / 2.0))) : 0;
                    double gx = (pxl - pad) / px, gy = (pyl - (H - px) / 2) / px;
                    float ga = gx >= 0 && gx < n && gy >= 0.15 && gy < 0.85 && gx - Math.Floor(gx) > 0.12 && gx - Math.Floor(gx) < 0.88 ? 1 : 0;
                    float b = (float)(c.B * pa), g = (float)(c.G * pa), r = (float)(c.R * pa), A = pa;
                    if (ga > 0) { b = (float)fb; g = (float)fg; r = (float)fr; A = 1; }
                    int o = (y * W + x) * 4; s.Px[o] = b; s.Px[o + 1] = g; s.Px[o + 2] = r; s.Px[o + 3] = A;
                }
            return s;
        }
    }
}
