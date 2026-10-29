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
        public static readonly string[] Styles = { "blur", "mosaic", "solid" };
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
        public static string Name(string s, Func<string, string, string> L) => s == "mosaic" ? L("马赛克", "Mosaic") : s == "solid" ? L("色块", "Solid") : L("模糊", "Blur");
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
                if (sp != null) Blend(dst, W, H, sp, x0, y0, al);
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

        static void Redact(byte[] src, byte[] dst, int W, int H, Redaction r, double unit)
        {
            var (x0, y0, x1, y1) = PixelRect(r, W, H);
            int rw = x1 - x0, rh = y1 - y0;
            if (rw < 2 || rh < 2) return;
            double k = Math.Min(Math.Max(r.Strength, 0), 1);
            // 效果图（只算这一块）：BGR 三个通道
            var eff = new float[rw * rh * 3];
            if (r.Style == "blur")
            {
                double sigma = (3 + 22 * k) * unit;
                int m = (int)Math.Ceiling(sigma * 3);
                int ex0 = Math.Max(0, x0 - m), ey0 = Math.Max(0, y0 - m), ex1 = Math.Min(W, x1 + m), ey1 = Math.Min(H, y1 + m);   // 往外多取一圈（画面边上按边缘延伸，同 Mac 的 clampedToExtent）
                int ew = ex1 - ex0, eh = ey1 - ey0;
                for (int c = 0; c < 3; c++)
                {
                    var ch = new float[ew * eh];
                    for (int y = 0; y < eh; y++) { int so = ((ey0 + y) * W + ex0) * 4 + c; for (int x = 0; x < ew; x++) ch[y * ew + x] = src[so + x * 4]; }
                    Sprites.Blur(ch, ew, eh, sigma);
                    for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++) eff[(y * rw + x) * 3 + c] = ch[(y0 - ey0 + y) * ew + (x0 - ex0 + x)];
                }
            }
            else if (r.Style == "mosaic")
            {
                // 格子从遮挡框的左上角排起；每格取格子里的平均色（格子伸出框外的部分照样取原画面，边上不会突兀）
                int cell = (int)Math.Max(2, Math.Round((6 + 30 * k) * unit));
                for (int cy = y0; cy < y1; cy += cell)
                    for (int cx = x0; cx < x1; cx += cell)
                    {
                        int gx1 = Math.Min(W, cx + cell), gy1 = Math.Min(H, cy + cell);
                        double sb = 0, sg = 0, sr = 0; int n = 0;
                        for (int y = cy; y < gy1; y++) for (int x = cx; x < gx1; x++) { int i = (y * W + x) * 4; sb += src[i]; sg += src[i + 1]; sr += src[i + 2]; n++; }
                        if (n == 0) continue;
                        float b = (float)(sb / n), g = (float)(sg / n), rr = (float)(sr / n);
                        for (int y = cy; y < Math.Min(y1, gy1); y++) for (int x = cx; x < Math.Min(x1, gx1); x++) { int o = ((y - y0) * rw + (x - x0)) * 3; eff[o] = b; eff[o + 1] = g; eff[o + 2] = rr; }
                    }
            }
            else
            {
                float b = (float)(r.Color.B * 255), g = (float)(r.Color.G * 255), rr = (float)(r.Color.R * 255);
                for (int i = 0; i < rw * rh; i++) { eff[i * 3] = b; eff[i * 3 + 1] = g; eff[i * 3 + 2] = rr; }
            }
            // 圆角遮罩（抗锯齿）：框外原样
            double rad = Math.Min(r.Corner * unit, Math.Min(rw, rh) / 2.0);
            double cxr = x0 + rw / 2.0, cyr = y0 + rh / 2.0;
            Parallel.For(0, rh, y =>
            {
                for (int x = 0; x < rw; x++)
                {
                    double px = x0 + x + 0.5, py = y0 + y + 0.5;
                    double cov = rad > 0.5 ? Math.Min(1, Math.Max(0, 0.5 - RoundRectDist(px, py, cxr, cyr, rw / 2.0, rh / 2.0, rad))) : 1;
                    if (cov <= 0) continue;
                    int i = ((y0 + y) * W + x0 + x) * 4, o = (y * rw + x) * 3;
                    for (int c = 0; c < 3; c++) dst[i + c] = (byte)Math.Round(Math.Min(255, Math.Max(0, eff[o + c] * cov + dst[i + c] * (1 - cov))));
                }
            });
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
                return (ts, (int)Math.Round(a.X * W), (int)Math.Round(a.Y * H));
            }
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
                double tw = ts?.W ?? a.FontSize * unit * Math.Max(1, (a.Text ?? "").Length), th = ts?.H ?? a.FontSize * unit * 1.4;
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
