// Studio 效果（仿 Screen Studio）的合成部分：照 Mac 版 Studio.swift 移植，只用纯 C#（不碰 WinForms / GDI+），
// 所以也能在 Mac 上用 .NET 8 跑（tools/StudioHarness）对着真素材调。
// 和 Mac 版的区别：背景是纯白色（Mac 版透明）；出 H.264 mp4；读写视频交给同目录的 ffmpeg.exe（管道传 BGRA 原始帧）；
// 合成默认走显卡（Windows 程序里的 StudioGpu.cs，D3D11），没有显卡时用这里的多线程逐像素算（双线性取样）；
// 编码优先用显卡编码器（N 卡 / Intel / AMD / Windows 自带），都不行用 libx264。
// 轨迹文件 track.json 格式和 Mac 版一样，Mac 录的素材也能拿来生成。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenRecorder
{
    // ---- 轨迹文件 track.json ----
    sealed class CursorShape
    {
        public string File;
        public double HotX, HotY, W, H;      // 热点和大小按「点」（像素 ÷ pixelsPerPoint）
        public bool Arrow;                   // 标准箭头：导出时换成矢量画的高清箭头
    }

    sealed class Track
    {
        public int Width, Height;            // 画面像素
        public double PixelsPerPoint = 1;    // 150% 缩放 = 1.5
        public List<double[]> Moves = new List<double[]>();    // [秒, x, y, 指针图案编号(-1 = 隐藏), 左键按着 1/0]；画面像素，左上角为原点
        public List<double[]> Clicks = new List<double[]>();   // [秒, x, y]
        public List<double> Keys = new List<double>();         // 按键的时刻（秒）
        public List<CursorShape> Cursors = new List<CursorShape>();

        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"clicks\":[").Append(string.Join(",", Clicks.Select(c => "[" + string.Join(",", c.Select(N)) + "]"))).Append("],");
            sb.Append("\"cursors\":[").Append(string.Join(",", Cursors.Select(c =>
                $"{{\"arrow\":{(c.Arrow ? "true" : "false")},\"file\":\"{c.File}\",\"h\":{N(c.H)},\"hotX\":{N(c.HotX)},\"hotY\":{N(c.HotY)},\"w\":{N(c.W)}}}"))).Append("],");
            sb.Append("\"height\":").Append(Height).Append(',');
            sb.Append("\"keys\":[").Append(string.Join(",", Keys.Select(N))).Append("],");
            sb.Append("\"moves\":[").Append(string.Join(",", Moves.Select(m => "[" + string.Join(",", m.Select(N)) + "]"))).Append("],");
            sb.Append("\"pixelsPerPoint\":").Append(N(PixelsPerPoint)).Append(',');
            sb.Append("\"width\":").Append(Width).Append('}');
            return sb.ToString();
        }

        public static Track FromJson(string json)
        {
            var o = (Dictionary<string, object>)Json.Parse(json);
            double D(object v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);
            List<object> A(string k) => o.TryGetValue(k, out var v) && v is List<object> l ? l : new List<object>();
            var t = new Track { Width = (int)D(o["width"]), Height = (int)D(o["height"]), PixelsPerPoint = o.TryGetValue("pixelsPerPoint", out var p) ? D(p) : 1 };
            t.Moves = A("moves").Select(m => ((List<object>)m).Select(D).ToArray()).ToList();
            t.Clicks = A("clicks").Select(m => ((List<object>)m).Select(D).ToArray()).ToList();
            t.Keys = A("keys").Select(D).ToList();
            t.Cursors = A("cursors").Select(x =>
            {
                var c = (Dictionary<string, object>)x;
                return new CursorShape { File = (string)c["file"], HotX = D(c["hotX"]), HotY = D(c["hotY"]), W = D(c["w"]), H = D(c["h"]), Arrow = c.TryGetValue("arrow", out var a) && a is bool b && b };
            }).ToList();
            return t;
        }
    }

    // 够读 track.json 的小 JSON 解析器：对象 → Dictionary，数组 → List，数字 → double
    static class Json
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); return v; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i); i++;   // ':'
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i++] == '}') return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i++] == ']') return l;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int j = i;
            while (j < s.Length && "+-0123456789.eE".IndexOf(s[j]) >= 0) j++;
            var num = double.Parse(s.Substring(i, j - i), CultureInfo.InvariantCulture);
            i = j;
            return num;
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder(); i++;   // '"'
            while (s[i] != '"')
            {
                if (s[i] == '\\')
                {
                    i++;
                    char e = s[i];
                    if (e == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                    else sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e == 'b' ? '\b' : e == 'f' ? '\f' : e);
                }
                else sb.Append(s[i]);
                i++;
            }
            i++;
            return sb.ToString();
        }
    }

    // ---- 成片的样子 ----
    sealed class StudioStyle
    {
        public double Zoom = 2.0;            // 放大时镜头推近几倍
        public double Padding = 0.06;        // 画面四周留给背景的边（占画布短边）
        public double CursorScale = 1.5;     // 鼠标画大一点
        public int Fps = 60;
        public double BitsPerPixel = 0.12;   // 码率：每像素每帧多少比特
        public double? Aspect;               // 画布宽高比；null = 跟原画面一样
    }

    struct RectD
    {
        public double X, Y, W, H;
        public RectD(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    }

    // 画布和画面在画布上的位置（和 Mac 版一样用「左下角为原点、y 朝上」算，合成时再翻成 y 朝下）
    sealed class StudioLayout
    {
        public readonly double W, H, cw, ch, s, radius;
        public readonly RectD card;

        public StudioLayout(Track track, StudioStyle style)
        {
            W = track.Width; H = track.Height;
            double f = style.Padding, a = style.Aspect ?? W / H;
            double cw, ch;
            if (a >= 1) { ch = Math.Max(H / (1 - 2 * f), W / (a - 2 * f)); cw = ch * a; }
            else { cw = Math.Max(W / (1 - 2 * f), H / (1 / a - 2 * f)); ch = cw / a; }
            // 和 Mac 版一样限制在边长 4096、约 890 万像素以内（播放器、手机兼容）
            double k = Math.Min(1, Math.Min(4096 / Math.Max(cw, ch), Math.Sqrt(8_800_000 / (cw * ch))));
            cw = Math.Floor(cw * k / 2) * 2; ch = Math.Floor(ch * k / 2) * 2;
            this.cw = cw; this.ch = ch;
            double pad = f * Math.Min(cw, ch);
            s = Math.Min((cw - 2 * pad) / W, (ch - 2 * pad) / H);
            card = new RectD(Math.Round((cw - W * s) / 2), Math.Round((ch - H * s) / 2), W * s, H * s);
            radius = Math.Max(8, card.W * 0.011);
        }
    }

    // 临界阻尼弹簧：往目标走，起步快、到位前慢下来，不过冲（精确解，步长多大都稳）
    struct Spring
    {
        public double x, v;
        readonly double w;
        public Spring(double x, double w) { this.x = x; v = 0; this.w = w; }
        public void Step(double g, double dt)
        {
            double y = x - g, e = Math.Exp(-w * dt), k = v + w * y;
            x = g + (y + k * dt) * e;
            v = (v - w * k * dt) * e;
        }
    }

    // 每一帧的镜头和鼠标（y 朝上，同 Mac 版）
    struct FramePlan
    {
        public double z, sx, sy;             // 镜头放大倍数、对准的画布坐标
        public double mx, my;                // 鼠标在原画面上的位置
        public int shape;                    // 指针图案；-1 = 不画
        public double press;                 // 点击时指针缩一下
    }

    static class StudioPlan
    {
        struct Ev { public double t, x, y; public Ev(double t, double x, double y) { this.t = t; this.x = x; this.y = y; } }
        sealed class Seg { public double start, end; public List<Ev> ev = new List<Ev>(); }

        public static FramePlan[] Make(Track tr, StudioLayout L, StudioStyle st, int n, double duration)
        {
            double W = L.W, H = L.H, dt = 1.0 / st.Fps;
            var ts = tr.Moves.Select(m => m[0]).ToArray();
            // 鼠标在 t 秒的位置（前后两次采样之间插值），y 换成朝上
            bool Mouse(double t, out double mx, out double my, out int shape)
            {
                mx = my = 0; shape = -1;
                if (ts.Length == 0) return false;
                int lo = 0, hi = ts.Length;
                while (lo < hi) { int m = (lo + hi) / 2; if (ts[m] <= t) lo = m + 1; else hi = m; }
                var b = tr.Moves[Math.Min(lo, ts.Length - 1)]; var a = tr.Moves[Math.Max(lo - 1, 0)];
                double u = b[0] > a[0] ? Math.Min(1, Math.Max(0, (t - a[0]) / (b[0] - a[0]))) : 0;
                mx = a[1] + (b[1] - a[1]) * u; my = H - (a[2] + (b[2] - a[2]) * u); shape = (int)a[3];
                return true;
            }

            // 1) 值得放大的时刻：点击、打字、按住拖动（每 0.3 秒算一次）；最后 1 秒不算（多半是去点「停止」或按快捷键）
            var ev = tr.Clicks.Select(c => new Ev(c[0], c[1], H - c[2])).ToList();
            foreach (var k in tr.Keys) if (Mouse(k, out var kx, out var ky, out _)) ev.Add(new Ev(k, kx, ky));
            double lastDrag = -1;
            foreach (var m in tr.Moves) if (m[4] == 1 && m[0] - lastDrag >= 0.3) { ev.Add(new Ev(m[0], m[1], H - m[2])); lastDrag = m[0]; }
            ev = ev.Where(e => e.t >= 0 && e.t <= duration - 1).OrderBy(e => e.t).ToList();

            // 2) 合成放大段：事件前 0.6 秒开始推近，最后一个事件后停 3.5 秒；两段之间不到 1.5 秒就连成一段
            var segs = new List<Seg>();
            foreach (var e in ev)
            {
                double s = Math.Max(0, e.t - 0.6), end = e.t + 3.5;
                var last = segs.LastOrDefault();
                if (last != null && s <= last.end + 1.5) { last.end = Math.Max(last.end, end); last.ev.Add(e); }
                else { var g = new Seg { start = s, end = end }; g.ev.Add(e); segs.Add(g); }
            }

            // 3) 逐帧：定目标，弹簧去追
            double Z = st.Zoom;
            double hw = L.cw / (2 * Z * L.s), hh = L.ch / (2 * Z * L.s);   // 放大时看得到的原画面半宽、半高
            var lz = new Spring(0, 4); var cx = new Spring(W / 2, 4); var cy = new Spring(H / 2, 4);
            Spring mxs = default, mys = default; bool haveMouse = false;
            int si = 0, cur = -1;
            double fx = W / 2, fy = H / 2;
            var outp = new FramePlan[n];
            for (int i = 0; i < n; i++)
            {
                double t = i * dt;
                var f = new FramePlan { z = 1, shape = -1, press = 1 };
                bool hasM = Mouse(t, out var mxv, out var myv, out var mshape);
                if (hasM)
                {
                    if (!haveMouse) { mxs = new Spring(mxv, 30); mys = new Spring(myv, 30); haveMouse = true; }
                    mxs.Step(mxv, dt); mys.Step(myv, dt);
                    f.mx = mxs.x; f.my = mys.x;
                    // 鼠标跑到画面外面（别的屏幕）就不画
                    f.shape = f.mx > -40 && f.mx < W + 40 && f.my > -40 && f.my < H + 40 ? mshape : -1;
                }
                while (si < segs.Count && segs[si].end <= t) si++;
                double tz = 1, tx = W / 2, ty = H / 2;
                if (si < segs.Count && segs[si].start <= t)
                {
                    var seg = segs[si];
                    if (cur != si) { cur = si; fx = seg.ev[0].x; fy = seg.ev[0].y; }
                    // 半秒内要点的地方离镜头远：提前转过去（录完再算，所以能「预知」）
                    var near = seg.ev.Where(e => e.t >= t && e.t <= t + 0.5).Take(1).ToList();
                    if (near.Count > 0 && (Math.Abs(near[0].x - fx) > hw * 0.5 || Math.Abs(near[0].y - fy) > hh * 0.5)) { fx = near[0].x; fy = near[0].y; }
                    else if (hasM && t >= seg.ev[0].t)
                    {
                        // 推近时镜头跟着鼠标走（像 Screen Studio）：鼠标离开中间一小块（25%）就跟，弹簧让它走得顺
                        double rx = hw * 0.25, ry = hh * 0.25;
                        if (mxv > fx + rx) fx = mxv - rx; else if (mxv < fx - rx) fx = mxv + rx;
                        if (myv > fy + ry) fy = myv - ry; else if (myv < fy - ry) fy = myv + ry;
                    }
                    fx = 2 * hw >= W ? W / 2 : Math.Min(Math.Max(fx, hw), W - hw);
                    fy = 2 * hh >= H ? H / 2 : Math.Min(Math.Max(fy, hh), H - hh);
                    tz = Z; tx = fx; ty = fy;
                }
                lz.Step(Math.Log(tz), dt); cx.Step(tx, dt); cy.Step(ty, dt);
                f.z = Math.Exp(lz.x);
                // 镜头框不能出画布
                double ex = L.cw / (2 * f.z), ey = L.ch / (2 * f.z);
                f.sx = Math.Min(Math.Max(L.card.X + cx.x * L.s, ex), L.cw - ex);
                f.sy = Math.Min(Math.Max(L.card.Y + cy.x * L.s, ey), L.ch - ey);
                // 点下去时指针缩一下再弹回
                foreach (var c in tr.Clicks) if (t >= c[0] && t < c[0] + 0.25) f.press = 1 - 0.18 * Math.Sin(Math.PI * (t - c[0]) / 0.25);
                outp[i] = f;
            }
            return outp;
        }
    }

    // 预乘透明的 BGRA 小图（鼠标、波纹）；ppp = 每「点」几个像素，hot = 热点像素坐标（左上角为原点、y 朝下）
    sealed class Sprite
    {
        public int W, H;
        public float[] Px;                   // W*H*4，B G R A，预乘
        public double Ppp, HotX, HotY;
    }

    static class Sprites
    {
        // 照 Windows 标准箭头的外形画（尖到尾约 18 点），白边黑芯，8 倍精度，放大推近也清楚
        public static Sprite Arrow()
        {
            const double k = 8, pad = 4, lw = 12, lh = 19;
            var poly = new[] { (0.0, 0.0), (0.0, 16.0), (3.8, 12.4), (6.3, 18.2), (8.7, 17.2), (6.3, 11.5), (11.3, 11.5) };
            int w = (int)((lw + pad * 2) * k), h = (int)((lh + pad * 2) * k);
            var fill = new float[w * h];       // 黑芯覆盖率
            var shape = new float[w * h];      // 整个指针（含白边）覆盖率，用来做阴影
            const int ss = 4;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int inF = 0, inS = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            double px = (x + (sx + 0.5) / ss) / k - pad, py = (y + (sy + 0.5) / ss) / k - pad;
                            bool inside = InPoly(poly, px, py);
                            if (inside) { inF++; inS++; }
                            else if (DistToPoly(poly, px, py) <= 1.1) inS++;   // 描边 2.2 点，外面露出一半
                        }
                    fill[y * w + x] = inF / (float)(ss * ss);
                    shape[y * w + x] = inS / (float)(ss * ss);
                }
            });
            // 阴影：往下 1 点、模糊 2.5 点、35% 黑
            var sh = new float[w * h];
            int off = (int)(1.0 * k);
            for (int y = off; y < h; y++) Array.Copy(shape, (y - off) * w, sh, y * w, w);
            Blur(sh, w, h, 2.5 * k / 2);
            var px2 = new float[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                float a = shape[i], f = fill[i], sa = sh[i] * 0.35f;
                // 白色描边层（a），黑芯盖在上面（f），都在阴影上面
                float white = a - f;                 // 只露白边的那部分
                float outA = a + sa * (1 - a);
                float c = white;                     // 预乘颜色：白边 = 1，黑芯 = 0，阴影 = 0
                px2[i * 4] = px2[i * 4 + 1] = px2[i * 4 + 2] = c;
                px2[i * 4 + 3] = outA;
            }
            return new Sprite { W = w, H = h, Px = px2, Ppp = k, HotX = pad * k, HotY = pad * k };
        }

        // 点击波纹：白圈 + 一圈淡淡的深色描边，深浅背景上都看得见（128 像素，圆心 64、半径 52）
        public static Sprite Ring()
        {
            const int n = 128, ss = 4;
            var px = new float[n * n * 4];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float b = 0, g = 0, r = 0, a = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            double dx = x + (sx + 0.5) / ss - 64, dy = y + (sy + 0.5) / ss - 64, d = Math.Sqrt(dx * dx + dy * dy);
                            // 由下往上叠：淡白填充 → 深色宽描边 → 白色细描边
                            float cr = 0, ca = 0;
                            void Over(float col, float al) { cr = col * al + cr * (1 - al); ca = al + ca * (1 - al); }
                            if (d <= 52) Over(1, 0.18f);
                            if (Math.Abs(d - 52) <= 5) Over(0, 0.25f);
                            if (Math.Abs(d - 52) <= 3) Over(1, 0.95f);
                            r += cr; a += ca;
                        }
                    float m = 1f / (ss * ss);
                    int i = (y * n + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = r * m; px[i + 3] = a * m;
                }
            return new Sprite { W = n, H = n, Px = px, Ppp = 1, HotX = 64, HotY = 64 };
        }

        // 从 BGRA（非预乘）字节建图
        public static Sprite FromBgra(byte[] bgra, int w, int h, double ppp, double hotX, double hotY)
        {
            var px = new float[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                float a = bgra[i * 4 + 3] / 255f;
                px[i * 4] = bgra[i * 4] / 255f * a; px[i * 4 + 1] = bgra[i * 4 + 1] / 255f * a; px[i * 4 + 2] = bgra[i * 4 + 2] / 255f * a; px[i * 4 + 3] = a;
            }
            return new Sprite { W = w, H = h, Px = px, Ppp = ppp, HotX = hotX, HotY = hotY };
        }

        static bool InPoly((double, double)[] p, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                var (xi, yi) = p[i]; var (xj, yj) = p[j];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
            }
            return c;
        }

        static double DistToPoly((double, double)[] p, double x, double y)
        {
            double best = double.MaxValue;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                var (ax, ay) = p[j]; var (bx, by) = p[i];
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double u = l2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / l2)) : 0;
                double qx = ax + u * dx - x, qy = ay + u * dy - y;
                best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
            }
            return best;
        }

        // 高斯模糊（三次盒式模糊近似）
        public static void Blur(float[] a, int w, int h, double sigma)
        {
            if (sigma < 0.5) return;
            // 三个盒子的宽度（Ivan Kutskir 的近似）
            double wIdeal = Math.Sqrt(12 * sigma * sigma / 3 + 1);
            int wl = (int)Math.Floor(wIdeal); if (wl % 2 == 0) wl--;
            int wu = wl + 2;
            double mIdeal = (12 * sigma * sigma - 3 * wl * wl - 12 * wl - 9) / (-4.0 * wl - 4);
            int m = (int)Math.Round(mIdeal);
            var tmp = new float[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                int r = ((pass < m ? wl : wu) - 1) / 2;
                BoxH(a, tmp, w, h, r); BoxV(tmp, a, w, h, r);
            }
        }

        static void BoxH(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            Parallel.For(0, h, y =>
            {
                int o = y * w;
                float acc = 0;
                for (int x = -r - 1; x < r; x++) acc += src[o + Math.Min(Math.Max(x, 0), w - 1)];
                for (int x = 0; x < w; x++)
                {
                    acc += src[o + Math.Min(x + r, w - 1)] - src[o + Math.Max(x - r - 1, 0)];
                    dst[o + x] = acc * inv;
                }
            });
        }

        static void BoxV(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            Parallel.For(0, w, x =>
            {
                float acc = 0;
                for (int y = -r - 1; y < r; y++) acc += src[Math.Min(Math.Max(y, 0), h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    acc += src[Math.Min(y + r, h - 1) * w + x] - src[Math.Max(y - r - 1, 0) * w + x];
                    dst[y * w + x] = acc * inv;
                }
            });
        }
    }

    // 合成器：CPU 版（Compositor）在这个文件；显卡版在 Windows 程序的 StudioGpu.cs（Studio.GpuFactory 指过去）
    interface IComposer : IDisposable
    {
        string Name { get; }                 // 写进日志：「CPU」/「显卡 NVIDIA …」
        bool Gpu { get; }
        bool Nv12In { get; }                 // 要 NV12 原画面（否则 BGRA）
        bool Nv12Out { get; }                // 出 NV12 画布（否则 BGRA）
        void Compose(byte[] src, FramePlan f, List<Compositor.Overlay> overlays, byte[] dst);
    }

    // 逐帧合成：白底 → 阴影 → 圆角画面 → 点击波纹 → 鼠标，整幅按镜头推拉（都在 y 朝下的坐标里算）
    sealed class Compositor : IComposer
    {
        public string Name => "CPU";
        public bool Gpu => false;
        public bool Nv12In => false;
        public bool Nv12Out => false;
        public void Dispose() { }
        public byte[] Background => bg;      // 白底 + 阴影（显卡版直接拿去当贴图，两边一模一样）

        readonly StudioLayout L;
        readonly int W, H, CW, CH;
        readonly byte[] bg;                  // 白底 + 卡片阴影，每帧一样
        public readonly double cardL, cardT, cardW, cardH, ccx, ccy, r;
        readonly int[] ui; readonly float[] fu, qxs; readonly int[] bxs;   // 每列预算好的取样位置

        public Compositor(StudioLayout lay)
        {
            L = lay; W = (int)lay.W; H = (int)lay.H; CW = (int)lay.cw; CH = (int)lay.ch;
            cardL = lay.card.X; cardW = lay.card.W; cardH = lay.card.H; cardT = lay.ch - lay.card.Y - cardH;
            ccx = cardL + cardW / 2; ccy = cardT + cardH / 2; r = lay.radius;
            ui = new int[CW]; fu = new float[CW]; qxs = new float[CW]; bxs = new int[CW];
            bg = MakeBackground();
        }

        // 白底上的卡片阴影：圆角矩形往下挪 1.2% 画布高、模糊、45% 黑（四分之一分辨率算再放大，阴影本来就是糊的）
        byte[] MakeBackground()
        {
            int q = 4, w = (CW + q - 1) / q, h = (CH + q - 1) / q;
            var m = new float[w * h];
            double dy = L.ch * 0.012;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double px = (x + 0.5) * q, py = (y + 0.5) * q - dy;
                    double d = RoundRectDist(px, py);
                    m[y * w + x] = (float)Math.Max(0, Math.Min(1, 0.5 - d / q));
                }
            Sprites.Blur(m, w, h, L.cw * 0.014 / q);
            var outp = new byte[CW * CH * 4];
            Parallel.For(0, CH, Y =>
            {
                double fy = (Y + 0.5) / q - 0.5; int y0 = Math.Max(0, Math.Min(h - 1, (int)Math.Floor(fy))), y1 = Math.Min(h - 1, y0 + 1); float ty = (float)Math.Max(0, Math.Min(1, fy - y0));
                for (int X = 0; X < CW; X++)
                {
                    double fx = (X + 0.5) / q - 0.5; int x0 = Math.Max(0, Math.Min(w - 1, (int)Math.Floor(fx))), x1 = Math.Min(w - 1, x0 + 1); float tx = (float)Math.Max(0, Math.Min(1, fx - x0));
                    float a = (m[y0 * w + x0] * (1 - tx) + m[y0 * w + x1] * tx) * (1 - ty) + (m[y1 * w + x0] * (1 - tx) + m[y1 * w + x1] * tx) * ty;
                    byte v = (byte)Math.Round(255 * (1 - 0.45 * a));
                    int i = (Y * CW + X) * 4;
                    outp[i] = outp[i + 1] = outp[i + 2] = v; outp[i + 3] = 255;
                }
            });
            return outp;
        }

        double RoundRectDist(double px, double py)
        {
            double qx = Math.Abs(px - ccx) - (cardW / 2 - r), qy = Math.Abs(py - ccy) - (cardH / 2 - r);
            return qx > 0 && qy > 0 ? Math.Sqrt(qx * qx + qy * qy) - r : Math.Max(qx, qy) - r;
        }

        public struct Overlay { public Sprite S; public double X, Y, K, Alpha; }   // X、Y = 热点落在原画面的哪（像素，y 朝下）；K = 每个小图像素占几个原画面像素

        // src：原画面 W×H BGRA；dst：画布 CW×CH BGRA
        public unsafe void Compose(byte[] src, FramePlan f, List<Overlay> overlays, byte[] dst)
        {
            double z = f.z, sx = f.sx, sy = L.ch - f.sy, s = L.s;
            float zf = (float)z;
            for (int X = 0; X < CW; X++)
            {
                double px = (X + 0.5 - CW / 2.0) / z + sx;
                double u = (px - cardL) / s - 0.5;
                int u0 = (int)Math.Floor(u); float t = (float)(u - u0);
                if (u0 < 0) { u0 = 0; t = 0; } else if (u0 > W - 2) { u0 = W - 2; t = 1; }
                ui[X] = u0; fu[X] = t;
                qxs[X] = (float)(Math.Abs(px - ccx) - (cardW / 2 - r));
                bxs[X] = Math.Max(0, Math.Min(CW - 1, (int)px));
            }
            float rf = (float)r;
            fixed (byte* sp0 = src, dp0 = dst, bp0 = bg)
            {
                byte* sp = sp0, dp = dp0, bp = bp0;
                Parallel.For(0, CH, Y =>
                {
                    double py = (Y + 0.5 - CH / 2.0) / z + sy;
                    double v = (py - cardT) / s - 0.5;
                    int v0 = (int)Math.Floor(v); float tv = (float)(v - v0);
                    if (v0 < 0) { v0 = 0; tv = 0; } else if (v0 > H - 2) { v0 = H - 2; tv = 1; }
                    float qy = (float)(Math.Abs(py - ccy) - (cardH / 2 - r));
                    int by = Math.Max(0, Math.Min(CH - 1, (int)py));
                    byte* row0 = sp + (long)v0 * W * 4, row1 = row0 + W * 4;
                    byte* brow = bp + (long)by * CW * 4;
                    byte* d = dp + (long)Y * CW * 4;
                    for (int X = 0; X < CW; X++, d += 4)
                    {
                        float qx = qxs[X];
                        float dist = qx > 0 && qy > 0 ? (float)Math.Sqrt(qx * qx + qy * qy) - rf : Math.Max(qx, qy) - rf;
                        float cov = 0.5f - dist * zf;
                        byte* b = brow + bxs[X] * 4;
                        if (cov <= 0) { *(uint*)d = *(uint*)b; continue; }
                        int o = ui[X] * 4; float tu = fu[X];
                        byte* p00 = row0 + o, p10 = row1 + o;
                        float w00 = (1 - tu) * (1 - tv), w01 = tu * (1 - tv), w10 = (1 - tu) * tv, w11 = tu * tv;
                        float cb = p00[0] * w00 + p00[4] * w01 + p10[0] * w10 + p10[4] * w11;
                        float cg = p00[1] * w00 + p00[5] * w01 + p10[1] * w10 + p10[5] * w11;
                        float cr = p00[2] * w00 + p00[6] * w01 + p10[2] * w10 + p10[6] * w11;
                        if (cov < 1)
                        {
                            cb = cb * cov + b[0] * (1 - cov); cg = cg * cov + b[1] * (1 - cov); cr = cr * cov + b[2] * (1 - cov);
                        }
                        d[0] = (byte)(cb + 0.5f); d[1] = (byte)(cg + 0.5f); d[2] = (byte)(cr + 0.5f); d[3] = 255;
                    }
                });
            }
            if (overlays != null) foreach (var o in overlays) Draw(o, f, dst);
        }

        // 小图（波纹、鼠标）按镜头一起放大，叠到画布上
        void Draw(Overlay o, FramePlan f, byte[] dst)
        {
            double z = f.z, sy = L.ch - f.sy, s = L.s;
            double ox = (cardL + o.X * s - f.sx) * z + CW / 2.0, oy = (cardT + o.Y * s - sy) * z + CH / 2.0;   // 热点在画布上的位置
            double K = o.K * s * z;                                                                                // 小图一个像素在画布上多大
            var sp = o.S;
            int x0 = (int)Math.Floor(ox - sp.HotX * K), y0 = (int)Math.Floor(oy - sp.HotY * K);
            int x1 = (int)Math.Ceiling(ox + (sp.W - sp.HotX) * K), y1 = (int)Math.Ceiling(oy + (sp.H - sp.HotY) * K);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(CW, x1); y1 = Math.Min(CH, y1);
            if (x0 >= x1 || y0 >= y1) return;
            float al = (float)o.Alpha;
            for (int Y = y0; Y < y1; Y++)
            {
                double v = (Y + 0.5 - oy) / K + sp.HotY - 0.5;
                int v0 = (int)Math.Floor(v); float tv = (float)(v - v0);
                for (int X = x0; X < x1; X++)
                {
                    double u = (X + 0.5 - ox) / K + sp.HotX - 0.5;
                    int u0 = (int)Math.Floor(u); float tu = (float)(u - u0);
                    float b = 0, g = 0, rr = 0, a = 0;
                    void Tap(int xx, int yy, float wgt)
                    {
                        if (xx < 0 || yy < 0 || xx >= sp.W || yy >= sp.H || wgt == 0) return;
                        int i = (yy * sp.W + xx) * 4;
                        b += sp.Px[i] * wgt; g += sp.Px[i + 1] * wgt; rr += sp.Px[i + 2] * wgt; a += sp.Px[i + 3] * wgt;
                    }
                    Tap(u0, v0, (1 - tu) * (1 - tv)); Tap(u0 + 1, v0, tu * (1 - tv)); Tap(u0, v0 + 1, (1 - tu) * tv); Tap(u0 + 1, v0 + 1, tu * tv);
                    a *= al;
                    if (a <= 0.001f) continue;
                    int di = (Y * CW + X) * 4;
                    dst[di] = (byte)Math.Min(255, b * al * 255 + dst[di] * (1 - a) + 0.5f);
                    dst[di + 1] = (byte)Math.Min(255, g * al * 255 + dst[di + 1] * (1 - a) + 0.5f);
                    dst[di + 2] = (byte)Math.Min(255, rr * al * 255 + dst[di + 2] * (1 - a) + 0.5f);
                }
            }
        }
    }

    // ---- 导出：ffmpeg 解码 → 合成 → ffmpeg 编码 ----
    sealed class VideoInfo { public int Width, Height; public double Duration; public bool HasAudio; public string Matrix = "bt709"; }

    // 一次生成的用时（写进素材文件夹的 render.log、状态栏显示「用时 N 秒（显卡）」）
    sealed class RenderStats
    {
        public string Composer = "CPU", Encoder = "libx264";
        public bool Gpu, HwDecode;
        public int Frames, Width, Height;
        public double Duration, TotalSeconds, RenderSeconds, ProbeSeconds, WaitDecode, Compose, WaitEncode, PipeRead, PipeWrite;
        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "compose={0} encoder={1} hwdecode={2} canvas={3}x{4} frames={5} duration={6:0.00}s total={7:0.00}s render={8:0.00}s ({9:0.00}x realtime) probe={10:0.00}s wait-decode={11:0.00}s compose-time={12:0.00}s wait-encode={13:0.00}s pipe-read={14:0.00}s pipe-write={15:0.00}s",
            Composer, Encoder, HwDecode, Width, Height, Frames, Duration, TotalSeconds, RenderSeconds, Duration > 0 ? TotalSeconds / Duration : 0, ProbeSeconds, WaitDecode, Compose, WaitEncode, PipeRead, PipeWrite);
    }

    static class Studio
    {
        // 没嵌运行库时：ffmpeg 在旁边的「运行库」文件夹或和 exe 放一起
        public const string LibDirName = "运行库";
        // Windows 版单 exe：Locate 指向解出来的 ffmpeg（还在后台解就等它）；Mac 上的测试工具直接给路径
        public static Func<string> Locate;
        static string ffmpeg;
        public static string FfmpegPath { get => ffmpeg ?? (ffmpeg = Locate?.Invoke() ?? FindFfmpeg()); set => ffmpeg = value; }

        static string FindFfmpeg()
        {
            string name = OperatingSystemIsWindows() ? "ffmpeg.exe" : "ffmpeg", baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var lib = Path.Combine(baseDir, LibDirName, name);
            return File.Exists(lib) ? lib : Path.Combine(baseDir, name);
        }

        static bool OperatingSystemIsWindows() => Path.DirectorySeparatorChar == '\\';

        public static VideoInfo Probe(string file)
        {
            var psi = new ProcessStartInfo(FfmpegPath, $"-hide_banner -i \"{file}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            string err;
            using (var p = Process.Start(psi)) { err = p.StandardError.ReadToEnd(); p.WaitForExit(); }
            var info = new VideoInfo();
            var d = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!d.Success) throw new Exception("can't read video: " + err.Trim().Split('\n').LastOrDefault());
            info.Duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
            var v = Regex.Match(err, @"Stream #\d+:\d+.*?: Video: (.*)");
            if (!v.Success) throw new Exception("no video stream");
            var sz = Regex.Match(v.Groups[1].Value, @"\b(\d{2,5})x(\d{2,5})\b");
            info.Width = int.Parse(sz.Groups[1].Value); info.Height = int.Parse(sz.Groups[2].Value);
            if (Regex.IsMatch(v.Groups[1].Value, @"\((?:tv|pc), (?:bt470bg|smpte170m)")) info.Matrix = "bt601";
            info.HasAudio = Regex.IsMatch(err, @"Stream #\d+:\d+.*?: Audio:");
            return info;
        }

        // dir 里有 raw.mp4、track.json、cursor-N.png；loadCursor 读指针图（null = 一律画矢量箭头）
        // 显卡合成：Windows 程序启动时设好（StudioGpu.cs）；没设（Mac 上的测试工具）或建不起来就用 CPU
        public static Func<StudioLayout, Compositor, VideoInfo, IComposer> GpuFactory;

        // dir 里有 raw.mp4、track.json、cursor-N.png；loadCursor 读指针图（null = 一律画矢量箭头）
        // 环境变量（自测用）：STUDIO_COMPOSE=cpu|gpu（gpu = 必须用显卡，失败就报错）；STUDIO_ENCODER=libx264|h264_nvenc|…；STUDIO_HWDEC=0
        public static RenderStats Render(string dir, string outPath, StudioStyle st, Action<double> progress, Func<string, CursorShape, double, Sprite> loadCursor = null, Action<string> log = null)
        {
            var total = Stopwatch.StartNew();
            var stats = new RenderStats();
            string raw = Path.Combine(dir, "raw.mp4");
            var info = Probe(raw);
            var track = Track.FromJson(File.ReadAllText(Path.Combine(dir, "track.json")));
            // 轨迹按画面像素记；万一尺寸对不上（比如手动换过 raw.mp4）就按比例换算
            if (track.Width != info.Width || track.Height != info.Height)
            {
                double kx = info.Width / (double)track.Width, ky = info.Height / (double)track.Height;
                foreach (var m in track.Moves) { m[1] *= kx; m[2] *= ky; }
                foreach (var c in track.Clicks) { c[1] *= kx; c[2] *= ky; }
                track.Width = info.Width; track.Height = info.Height;
            }
            var lay = new StudioLayout(track, st);
            int n = Math.Max(1, (int)(info.Duration * st.Fps));
            var plan = StudioPlan.Make(track, lay, st, n, info.Duration);
            // 自测：STUDIO_PLANDUMP=文件 → 每帧写「秒,放大倍数,鼠标离画面中心多远（横、竖，按半个画面算）」
            var dump = Environment.GetEnvironmentVariable("STUDIO_PLANDUMP");
            if (dump != null)
                File.WriteAllLines(dump, plan.Select((f, i) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3},{3:F3}",
                    i / (double)st.Fps, f.z, (lay.card.X + f.mx * lay.s - f.sx) * f.z / (lay.cw / 2), (lay.card.Y + f.my * lay.s - f.sy) * f.z / (lay.ch / 2))));
            var cpu = new Compositor(lay);
            var arrow = Sprites.Arrow(); var ring = Sprites.Ring();
            var art = new Dictionary<int, Sprite>();
            for (int i = 0; i < track.Cursors.Count; i++)
            {
                var c = track.Cursors[i];
                if (c.Arrow || loadCursor == null) continue;
                try { var sp = loadCursor(Path.Combine(dir, c.File), c, track.PixelsPerPoint); if (sp != null) art[i] = sp; } catch { }
            }
            int W = info.Width, H = info.Height, CW = (int)lay.cw, CH = (int)lay.ch;
            log?.Invoke($"source {W}x{H} {info.Duration:0.00}s audio={info.HasAudio} matrix={info.Matrix} → canvas {CW}x{CH} frames {n} clicks {track.Clicks.Count} keys {track.Keys.Count}");

            // 合成用显卡还是 CPU
            string want = (Environment.GetEnvironmentVariable("STUDIO_COMPOSE") ?? "").ToLowerInvariant();
            IComposer comp = cpu;
            if (want != "cpu" && GpuFactory != null)
            {
                try { comp = GpuFactory(lay, cpu, info) ?? cpu; }
                catch (Exception e) { if (want == "gpu") throw new Exception("GPU compositor failed: " + e.Message, e); log?.Invoke("gpu compositor unavailable: " + e.Message); }
            }
            else if (want == "gpu") throw new Exception("GPU compositor not available");
            stats.Composer = comp.Name; stats.Gpu = comp.Gpu;

            // 编码器：先挑能用的显卡编码器（N 卡 / Intel / AMD / Windows 自带），都不行用 libx264
            var sw0 = Stopwatch.StartNew();
            string encoder = PickEncoder(log);
            bool hwdec = Environment.GetEnvironmentVariable("STUDIO_HWDEC") != "0" && HwDecodeWorks(raw);
            stats.ProbeSeconds = sw0.Elapsed.TotalSeconds;
            stats.Encoder = encoder; stats.HwDecode = hwdec;
            log?.Invoke($"compose={comp.Name} encoder={encoder} hwdecode={hwdec} (probe {stats.ProbeSeconds:0.00}s)");

            string mat = info.Matrix;
            string hw = hwdec ? "-hwaccel auto " : "";
            var dec = comp.Nv12In
                ? Start($"-v error {hw}-i \"{raw}\" -an -sn -vf \"setpts=PTS-STARTPTS,fps={st.Fps}\" -f rawvideo -pix_fmt nv12 pipe:1", false)
                : Start($"-v error {hw}-i \"{raw}\" -an -sn -vf \"setpts=PTS-STARTPTS,fps={st.Fps},scale=in_color_matrix={mat}:in_range=tv:out_range=pc,format=bgra\" -f rawvideo -pix_fmt bgra pipe:1", false);
            long rate = Math.Max(10_000_000, (long)(CW * (double)CH * st.Fps * st.BitsPerPixel));
            string audio = info.HasAudio ? $"-i \"{raw}\" -map 0:v -map 1:a:0 -c:a aac -b:a 192k" : "-map 0:v";
            bool x264 = encoder == "libx264";
            string input = comp.Nv12Out
                ? $"-f rawvideo -pix_fmt nv12 -s {CW}x{CH} -r {st.Fps} -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 -i pipe:0"
                : $"-f rawvideo -pix_fmt bgra -s {CW}x{CH} -r {st.Fps} -i pipe:0";
            string vf = comp.Nv12Out ? (x264 ? "-vf format=yuv420p " : "")
                                     : $"-vf \"scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format={(x264 ? "yuv420p" : "nv12")}\" ";
            var enc = Start($"-v error -y {input} {audio} {vf}{EncoderArgs(encoder, rate, st.Fps, comp.Gpu)} " +
                            $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -movflags +faststart \"{outPath}\"", true);
            var decErr = new StringBuilder(); var encErr = new StringBuilder();
            dec.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (decErr) decErr.AppendLine(e.Data); }; dec.BeginErrorReadLine();
            enc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (encErr) encErr.AppendLine(e.Data); }; enc.BeginErrorReadLine();

            int frameBytes = comp.Nv12In ? W * H * 3 / 2 : W * H * 4, outBytes = comp.Nv12Out ? CW * CH * 3 / 2 : CW * CH * 4;
            var srcFree = new BlockingCollection<byte[]>(); var srcFull = new BlockingCollection<byte[]>(4);
            var dstFree = new BlockingCollection<byte[]>(); var dstFull = new BlockingCollection<byte[]>(3);
            for (int i = 0; i < 5; i++) srcFree.Add(new byte[frameBytes]);
            for (int i = 0; i < 4; i++) dstFree.Add(new byte[outBytes]);
            Exception failure = null;
            var cts = new CancellationTokenSource();
            var swRead = new Stopwatch(); var swWrite = new Stopwatch();

            // 读线程：解码出来的 60fps 原画面
            var reader = new Thread(() =>
            {
                try
                {
                    var stream = dec.StandardOutput.BaseStream;
                    while (!cts.IsCancellationRequested)
                    {
                        var buf = srcFree.Take(cts.Token);
                        int got = 0;
                        swRead.Start();
                        while (got < frameBytes) { int k = stream.Read(buf, got, frameBytes - got); if (k <= 0) break; got += k; }
                        swRead.Stop();
                        if (got < frameBytes) { srcFree.Add(buf); break; }
                        srcFull.Add(buf, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { failure = failure ?? e; }
                finally { srcFull.CompleteAdding(); }
            }) { IsBackground = true, Name = "studio-read" };
            // 写线程：合成好的画布交给编码器
            var writer = new Thread(() =>
            {
                try
                {
                    var stream = enc.StandardInput.BaseStream;
                    foreach (var buf in dstFull.GetConsumingEnumerable()) { swWrite.Start(); stream.Write(buf, 0, outBytes); swWrite.Stop(); dstFree.Add(buf); }
                    stream.Flush(); stream.Close();
                }
                catch (Exception e) { failure = failure ?? e; cts.Cancel(); }
            }) { IsBackground = true, Name = "studio-write" };
            reader.Start(); writer.Start();

            var clicks = track.Clicks;
            double ppp = track.PixelsPerPoint;
            byte[] cur = null;
            var sw = Stopwatch.StartNew();
            var swIn = new Stopwatch(); var swComp = new Stopwatch(); var swOut = new Stopwatch();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    if (failure != null) throw failure;
                    // 原画面不够就沿用最后一帧（录到最后画面没变化时，原始文件里最后一帧会早于结尾）
                    swIn.Start();
                    if (srcFull.TryTake(out var next, Timeout.Infinite, cts.Token)) { if (cur != null) srcFree.Add(cur); cur = next; }
                    swIn.Stop();
                    if (cur == null) throw new Exception("no frames decoded: " + decErr.ToString().Trim());
                    var f = plan[i];
                    double t = i / (double)st.Fps;
                    var ov = new List<Compositor.Overlay>();
                    // 点击波纹：0.5 秒内从 6 点扩到 28 点、渐渐淡掉
                    foreach (var c in clicks)
                        if (t >= c[0] && t < c[0] + 0.5)
                        {
                            double u = (t - c[0]) / 0.5, e = 1 - Math.Pow(1 - u, 3);
                            double rr = (6 + 22 * e) * ppp * st.CursorScale / 1.5;
                            ov.Add(new Compositor.Overlay { S = ring, X = c[1], Y = c[2], K = rr / 52, Alpha = 1 - u });
                        }
                    if (f.shape >= 0)
                    {
                        var sp = art.TryGetValue(f.shape, out var a) ? a : arrow;
                        ov.Add(new Compositor.Overlay { S = sp, X = f.mx, Y = H - f.my, K = ppp * st.CursorScale * f.press / sp.Ppp, Alpha = 1 });
                    }
                    swOut.Start(); var dst = dstFree.Take(cts.Token); swOut.Stop();
                    swComp.Start(); comp.Compose(cur, f, ov, dst); swComp.Stop();
                    swOut.Start(); dstFull.Add(dst, cts.Token); swOut.Stop();
                    if (i % 30 == 0) progress?.Invoke(i / (double)n);
                }
                dstFull.CompleteAdding();
                swOut.Start(); writer.Join(); swOut.Stop();
                if (failure != null) throw failure;
                swOut.Start(); enc.WaitForExit(); swOut.Stop();
                cts.Cancel();
                try { if (!dec.HasExited) dec.Kill(); } catch { }
                if (enc.ExitCode != 0) throw new Exception("encoder failed: " + encErr.ToString().Trim());
            }
            catch
            {
                cts.Cancel();
                dstFull.CompleteAdding();
                try { if (!dec.HasExited) dec.Kill(); } catch { }
                try { if (!enc.HasExited) enc.Kill(); } catch { }
                try { File.Delete(outPath); } catch { }
                if (failure != null && !(failure is OperationCanceledException)) throw new Exception(failure.Message + " " + encErr.ToString().Trim(), failure);
                throw;
            }
            finally { if (comp != cpu) comp.Dispose(); }
            stats.Frames = n; stats.Width = CW; stats.Height = CH; stats.Duration = info.Duration;
            stats.RenderSeconds = sw.Elapsed.TotalSeconds; stats.TotalSeconds = total.Elapsed.TotalSeconds;
            stats.WaitDecode = swIn.Elapsed.TotalSeconds; stats.Compose = swComp.Elapsed.TotalSeconds; stats.WaitEncode = swOut.Elapsed.TotalSeconds;
            stats.PipeRead = swRead.Elapsed.TotalSeconds; stats.PipeWrite = swWrite.Elapsed.TotalSeconds;
            log?.Invoke(stats.ToString());
            log?.Invoke($"rendered {n} frames in {sw.Elapsed.TotalSeconds:0.0}s ({n / sw.Elapsed.TotalSeconds:0.0} fps, {sw.Elapsed.TotalSeconds / info.Duration:0.00}× realtime)");
            try { File.AppendAllText(Path.Combine(dir, "render.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + stats + Environment.NewLine); } catch { }
            progress?.Invoke(1);
            return stats;
        }

        // ---- 编码器 ----
        static readonly string[] HwEncoders = { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf" };
        static string pickedEncoder;

        // 按顺序真编一小段，能编的第一个就用（同一次运行里记住结果）
        public static string PickEncoder(Action<string> log = null)
        {
            var forced = Environment.GetEnvironmentVariable("STUDIO_ENCODER");
            if (!string.IsNullOrEmpty(forced)) return forced;
            if (pickedEncoder != null) return pickedEncoder;
            foreach (var e in HwEncoders)
            {
                var sw = Stopwatch.StartNew();
                bool ok = EncoderWorks(e);
                log?.Invoke($"probe {e}: {(ok ? "ok" : "no")} ({sw.Elapsed.TotalSeconds:0.00}s)");
                if (ok) return pickedEncoder = e;
            }
            return pickedEncoder = "libx264";
        }

        public static bool EncoderWorks(string encoder)
        {
            try
            {
                return RunQuiet($"-v error -f lavfi -i color=c=gray:s=640x360:r=60:d=0.5 -vf format=nv12 {EncoderArgs(encoder, 2_000_000, 60, true)} -f null -", 20000);
            }
            catch { return false; }
        }

        // 各编码器的参数：码率和原来一样（每像素每帧 0.12 比特，最少 10 Mbps），速度优先、字要清楚
        public static string EncoderArgs(string encoder, long rate, int fps, bool cpuIsFree)
        {
            string rc = $"-b:v {rate} -maxrate {rate * 3 / 2} -bufsize {rate * 2} -g {fps * 2}";
            switch (encoder)
            {
                case "h264_nvenc": return $"-c:v h264_nvenc -preset p4 -rc vbr {rc}";
                case "h264_qsv": return $"-c:v h264_qsv -preset faster {rc}";
                case "h264_amf": return $"-c:v h264_amf -usage transcoding -quality balanced -rc vbr_peak {rc}";
                case "h264_mf": return $"-c:v h264_mf -hw_encoding 1 -rate_control pc_vbr -b:v {rate} -g {fps * 2}";
                // CPU 编码：合成在显卡上时 CPU 空着，用 veryfast；合成也在 CPU 上就用 superfast，别和合成抢
                default: return $"-c:v libx264 -preset {(cpuIsFree ? "veryfast" : "superfast")} {rc}";
            }
        }

        // 显卡解码能不能用：解前几帧试试（失败 ffmpeg 会退回软解，这里只防它直接报错退出）
        static bool HwDecodeWorks(string raw)
        {
            try { return RunQuiet($"-v error -hwaccel auto -i \"{raw}\" -an -frames:v 5 -f null -", 15000); }
            catch { return false; }
        }

        static bool RunQuiet(string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(FfmpegPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                p.OutputDataReceived += (s, e) => { }; p.BeginOutputReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
        }

        static Process Start(string args, bool stdin)
        {
            var psi = new ProcessStartInfo(FfmpegPath, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = !stdin, RedirectStandardInput = stdin,
            };
            return Process.Start(psi);
        }
    }
}
