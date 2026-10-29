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

        // 剪辑窗口预览用：整体缩小（画面、轨迹、像素密度一起缩），合成出来的构图和原尺寸一样
        public Track Scaled(int w, int h)
        {
            double kx = w / (double)Width, ky = h / (double)Height;
            return new Track
            {
                Width = w, Height = h, PixelsPerPoint = PixelsPerPoint * (kx + ky) / 2, Keys = Keys.ToList(), Cursors = Cursors,
                Moves = Moves.Select(m => new[] { m[0], m[1] * kx, m[2] * ky, m[3], m[4] }).ToList(),
                Clicks = Clicks.Select(c => new[] { c[0], c[1] * kx, c[2] * ky }).ToList(),
            };
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

    // ---- 成片的规格（样式在 StudioLook 里，存在工程文件） ----
    sealed class StudioStyle
    {
        public double Zoom = 2.0;            // 自动生成推近段时放大几倍
        public int Fps = 60;
        public double BitsPerPixel = 0.12;   // 码率：每像素每帧多少比特
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

        // extra：导出选了较小分辨率时整个画布再缩小这么多（导出时直接按目标尺寸画，不是先画大再缩）
        public StudioLayout(Track track, StudioLook look, double extra = 1)
        {
            W = track.Width; H = track.Height;
            double f = Math.Min(Math.Max(look.Padding, 0), 0.2), a = look.Ratio ?? W / H;
            // 先按「画面 1:1 放得下、四周留 f 的边」算画布
            double cw, ch;
            if (a >= 1) { ch = Math.Max(H / (1 - 2 * f), W / (a - 2 * f)); cw = ch * a; }
            else { cw = Math.Max(W / (1 - 2 * f), H / (1 / a - 2 * f)); ch = cw / a; }
            // 和 Mac 版一样限制在边长 4096、约 890 万像素以内（播放器、手机兼容）
            double k = Math.Min(1, Math.Min(4096 / Math.Max(cw, ch), Math.Sqrt(8_800_000 / (cw * ch)))) * extra;
            cw = Math.Max(2, Math.Floor(cw * k / 2) * 2); ch = Math.Max(2, Math.Floor(ch * k / 2) * 2);
            this.cw = cw; this.ch = ch;
            double pad = f * Math.Min(cw, ch);
            s = Math.Min((cw - 2 * pad) / W, (ch - 2 * pad) / H);
            card = new RectD(Math.Round((cw - W * s) / 2), Math.Round((ch - H * s) / 2), W * s, H * s);
            radius = Math.Min(Math.Max(0, look.CornerRadius), 40) * track.PixelsPerPoint * s;
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
        public double alpha;                 // 鼠标不动自动隐藏时淡出
    }

    static class StudioPlan
    {
        public struct Ev { public double t, x, y; public Ev(double t, double x, double y) { this.t = t; this.x = x; this.y = y; } }

        // 鼠标在 t 秒的位置（前后两次采样之间插值），y 换成朝上
        public sealed class Mouse
        {
            readonly Track tr; readonly double[] ts; readonly double H;
            public Mouse(Track tr) { this.tr = tr; ts = tr.Moves.Select(m => m[0]).ToArray(); H = tr.Height; }
            public bool At(double t, out double mx, out double my, out int shape)
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
        }

        // 值得放大的时刻：点击、打字、按住拖动（每 0.3 秒算一次）；最后 1 秒不算（多半是去点「停止」或按快捷键）。y 朝上
        public static List<Ev> Events(Track tr, double duration)
        {
            double H = tr.Height; var mouse = new Mouse(tr);
            var ev = tr.Clicks.Select(c => new Ev(c[0], c[1], H - c[2])).ToList();
            foreach (var k in tr.Keys) if (mouse.At(k, out var kx, out var ky, out _)) ev.Add(new Ev(k, kx, ky));
            double lastDrag = -1;
            foreach (var m in tr.Moves) if (m[4] == 1 && m[0] - lastDrag >= 0.3) { ev.Add(new Ev(m[0], m[1], H - m[2])); lastDrag = m[0]; }
            return ev.Where(e => e.t >= 0 && e.t <= duration - 1).OrderBy(e => e.t).ToList();
        }

        // 自动生成推近段（参考 Screen Studio 的观感，规则同 Mac 版）：
        // - 事件前 0.6 秒推近，最后一个事件后停 3.5 秒；同一块地方接着操作、间隔不到 1.5 秒就连成一段（别刚拉远又推近）
        // - 下一个事件离得远（超过约 1/3 屏幕）、中间又有空：前一段收掉，拉回全景至少 1.2 秒再推近过去；
        //   来不及拉远：留在同一段，镜头平移过去，路远时边移边往后退一点（见 Make）
        // - 一段最长约 8 秒：再往后有 ≥1.6 秒的空档就在那里断开、回一下全景（1 秒）；一直不停超过 10 秒就硬断
        public const double ZoomOutHold = 1.2, ShortOut = 1.0, LeadIn = 0.6, HoldAfter = 3.5, MergeGap = 1.5, MaxLen = 8.0, HardMax = 10.0;
        sealed class Cur { public double start, end, lastT, x, y, fx, fy; }

        public static List<ZoomSegment> AutoSegments(Track tr, double duration, double zoom = 2.0)
        {
            double W = tr.Width, H = tr.Height;
            var segs = new List<ZoomSegment>();
            Cur cur = null;
            void Close(Cur c, double end) => segs.Add(new ZoomSegment { Start = c.start, End = Math.Max(c.start + 0.5, end), Zoom = zoom, FixedX = c.fx / W, FixedY = 1 - c.fy / H });
            Cur New(double st, double en, Ev e) => new Cur { start = st, end = en, lastT = e.t, x = e.x, y = e.y, fx = e.x, fy = e.y };
            foreach (var e in Events(tr, duration))
            {
                double s = Math.Max(0, e.t - LeadIn), end = Math.Min(duration, e.t + HoldAfter);
                if (cur == null) { cur = New(s, end, e); continue; }
                var c = cur;
                double gap = e.t - c.lastT;
                bool far = Math.Max(Math.Abs(e.x - c.x) / W, Math.Abs(e.y - c.y) / H) > 0.35;
                double len = e.t - c.start;
                // 能在中间拉回全景：前一段最晚收在「下一段开始前 1.2 秒」，还得比上个事件晚一点
                double outEnd = s - ZoomOutHold; bool canZoomOut = outEnd >= c.lastT + 0.4;
                if (s > c.end + MergeGap) { Close(c, Math.Min(c.end, s - ZoomOutHold)); cur = New(s, end, e); }
                else if (far && canZoomOut) { Close(c, outEnd); cur = New(s, end, e); }
                else if (len > MaxLen && gap >= LeadIn + ShortOut && s - ShortOut > c.lastT) { Close(c, s - ShortOut); cur = New(s, end, e); }
                else if (len > HardMax)
                {
                    // 一直没空：在上个事件后马上收，回全景 1 秒，再推近（这一下的事件会稍晚一点才推到）
                    double cut = c.lastT + 0.2;
                    Close(c, cut); cur = New(cut + ShortOut, Math.Max(end, cut + ShortOut + 0.5), e);
                }
                else { c.end = Math.Max(c.end, end); c.lastT = e.t; c.x = e.x; c.y = e.y; }
            }
            if (cur != null) Close(cur, cur.end);
            return segs;
        }

        // 逐帧算镜头和鼠标：从 0 秒算到结尾（修剪只决定导出哪几帧，镜头怎么走不受影响，预览和成片一致）
        public static FramePlan[] Make(Track tr, StudioLayout L, StudioStyle st, StudioLook look, List<ZoomSegment> segments, int n, double duration)
        {
            double W = L.W, H = L.H, dt = 1.0 / st.Fps;
            var mouse = new Mouse(tr); var ev = Events(tr, duration);
            // 鼠标顺滑：弹簧越软越顺；0 = 不平滑，直接用原位置
            bool smooth = look.Smoothing > 0.01; double mw = 8 + (1 - Math.Min(Math.Max(look.Smoothing, 0), 1)) * 72;
            // 鼠标最后一次动（或点）的时刻：不动超过 hideAfter 秒就淡出
            var moved = new List<double>();
            double[] prev = null;
            foreach (var m in tr.Moves) { if (prev != null && (Math.Sqrt((m[1] - prev[1]) * (m[1] - prev[1]) + (m[2] - prev[2]) * (m[2] - prev[2])) > 0.5 || m[4] != prev[4])) moved.Add(m[0]); prev = m; }
            moved.AddRange(tr.Clicks.Select(c => c[0])); moved.Sort();
            int mi = 0; double alpha = 1, lastMove = 0;
            var segs = segments.OrderBy(x => x.Start).ToList();
            var lz = new Spring(0, 4); var cx = new Spring(W / 2, 4); var cy = new Spring(H / 2, 4);
            Spring mxs = default, mys = default; bool haveMouse = false;
            int si = 0; string cur = null;
            double fx = W / 2, fy = H / 2;
            var outp = new FramePlan[n];
            for (int i = 0; i < n; i++)
            {
                double t = i * dt;
                var f = new FramePlan { z = 1, shape = -1, press = 1, alpha = 1 };
                bool hasM = mouse.At(t, out var mxv, out var myv, out var mshape);
                if (hasM)
                {
                    if (!haveMouse) { mxs = new Spring(mxv, mw); mys = new Spring(myv, mw); haveMouse = true; }
                    mxs.Step(mxv, dt); mys.Step(myv, dt);
                    f.mx = smooth ? mxs.x : mxv; f.my = smooth ? mys.x : myv;
                    // 鼠标跑到画面外面（别的屏幕）就不画
                    f.shape = f.mx > -40 && f.mx < W + 40 && f.my > -40 && f.my < H + 40 ? mshape : -1;
                }
                while (si < segs.Count && segs[si].End <= t) si++;
                double tz = 1, tx = W / 2, ty = H / 2;
                if (si < segs.Count && segs[si].Start <= t)
                {
                    var seg = segs[si]; double Z = Math.Max(1, seg.Zoom);
                    double hw = L.cw / (2 * Z * L.s), hh = L.ch / (2 * Z * L.s);   // 放大时看得到的原画面半宽、半高
                    var segEv = ev.Where(e => e.t >= seg.Start && e.t < seg.End).ToList();
                    if (seg.Fixed) { fx = seg.FixedX * W; fy = H - seg.FixedY * H; }
                    else
                    {
                        if (cur != seg.Id)
                        {
                            cur = seg.Id;
                            if (segEv.Count > 0) { fx = segEv[0].x; fy = segEv[0].y; } else if (hasM) { fx = mxv; fy = myv; }
                        }
                        // 半秒内要点的地方离镜头远：提前转过去（录完再算，所以能「预知」）
                        var near = segEv.Where(e => e.t >= t && e.t <= t + 0.5).Take(1).ToList();
                        if (near.Count > 0 && (Math.Abs(near[0].x - fx) > hw * 0.5 || Math.Abs(near[0].y - fy) > hh * 0.5)) { fx = near[0].x; fy = near[0].y; }
                        else if (hasM && t >= (segEv.Count > 0 ? segEv[0].t : seg.Start))
                        {
                            // 推近时镜头跟着鼠标走（像 Screen Studio）：鼠标离开中间一小块（25%）就跟，弹簧让它走得顺
                            double rx = hw * 0.25, ry = hh * 0.25;
                            if (mxv > fx + rx) fx = mxv - rx; else if (mxv < fx - rx) fx = mxv + rx;
                            if (myv > fy + ry) fy = myv - ry; else if (myv < fy - ry) fy = myv + ry;
                        }
                    }
                    fx = 2 * hw >= W ? W / 2 : Math.Min(Math.Max(fx, hw), W - hw);
                    fy = 2 * hh >= H ? H / 2 : Math.Min(Math.Max(fy, hh), H - hh);
                    tz = Z; tx = fx; ty = fy;
                    // 镜头要走很远（来不及拉远再推近时）：边移边往后退，路越远退得越多，到了再推回去
                    double travel = Math.Max(Math.Abs(tx - cx.x) / W, Math.Abs(ty - cy.x) / H);
                    if (travel > 0.2) tz = 1 + (Z - 1) * Math.Min(1, Math.Max(0.2, 1 - (travel - 0.2) / 0.3));
                }
                lz.Step(Math.Log(tz), dt); cx.Step(tx, dt); cy.Step(ty, dt);
                f.z = Math.Exp(lz.x);
                // 镜头框不能出画布
                double ex = L.cw / (2 * f.z), ey = L.ch / (2 * f.z);
                f.sx = Math.Min(Math.Max(L.card.X + cx.x * L.s, ex), L.cw - ex);
                f.sy = Math.Min(Math.Max(L.card.Y + cy.x * L.s, ey), L.ch - ey);
                // 点下去时指针缩一下再弹回
                foreach (var c in tr.Clicks) if (t >= c[0] && t < c[0] + 0.25) f.press = 1 - 0.18 * Math.Sin(Math.PI * (t - c[0]) / 0.25);
                // 不动自动隐藏：淡出 0.3 秒，一动就 0.12 秒淡入
                if (look.HideIdleCursor)
                {
                    while (mi < moved.Count && moved[mi] <= t) { lastMove = moved[mi]; mi++; }
                    double target = t - lastMove >= look.HideAfter ? 0 : 1;
                    alpha = target < alpha ? Math.Max(target, alpha - dt / 0.3) : Math.Min(target, alpha + dt / 0.12);
                    f.alpha = alpha;
                }
                outp[i] = f;
            }
            return outp;
        }

        // 快门子位置（运动模糊）：镜头每帧移动（含推拉）换算成画布上的像素，超过门槛才模糊；快门角 轻 90° / 中 180° / 强 270°，取 3–8 个位置。
        // 返回 null = 这帧不模糊（镜头不动的帧原样不碰，字是清楚的）
        public static FramePlan[] Shutter(string blur, int i, FramePlan[] plan, StudioLayout lay)
        {
            double angle;
            switch (blur) { case "light": angle = 90; break; case "medium": angle = 180; break; case "strong": angle = 270; break; default: return null; }
            if (plan.Length <= 1) return null;
            int ia = Math.Max(i - 1, 0), ib = Math.Min(i + 1, plan.Length - 1);
            FramePlan a = plan[ia], b = plan[ib], f = plan[i];
            double span = ib - ia;
            double diag = Math.Sqrt(lay.cw * lay.cw + lay.ch * lay.ch) / 2;
            double move = (Math.Sqrt(Math.Pow((b.sx - a.sx) * f.z, 2) + Math.Pow((b.sy - a.sy) * f.z, 2)) + Math.Abs(Math.Log(b.z / a.z)) * diag) / span;   // 每帧移动的像素
            double blurPx = move * angle / 360;
            if (blurPx < 1.5) return null;
            int n = Math.Min(8, Math.Max(3, (int)(blurPx / 2) + 1));
            double half = angle / 360 / 2;
            var outp = new FramePlan[n];
            for (int k = 0; k < n; k++)
            {
                // 在 i ± (快门/2) 帧之间按相邻帧插值：放大倍数按对数插值
                double x = i + ((double)k / (n - 1) * 2 - 1) * half;
                double j = Math.Min(Math.Max(x, 0), plan.Length - 1); int lo = (int)Math.Floor(j), hi = Math.Min(lo + 1, plan.Length - 1); double u = j - lo;
                var p = plan[lo]; var q = plan[hi];
                var r = f;
                r.z = Math.Exp(Math.Log(p.z) + (Math.Log(q.z) - Math.Log(p.z)) * u); r.sx = p.sx + (q.sx - p.sx) * u; r.sy = p.sy + (q.sy - p.sy) * u;
                outp[k] = r;
            }
            return outp;
        }

        // 画布上一点（左上为原点，像素）→ 原画面里的比例（左上为原点）：在预览上点一下设固定位置用
        public static (double x, double y) SourcePoint(StudioLayout lay, double px, double pyDown, FramePlan f)
        {
            double py = lay.ch - pyDown;   // 换成 y 朝上
            double sx = (px - (lay.cw / 2 - f.sx * f.z)) / f.z, sy = (py - (lay.ch / 2 - f.sy * f.z)) / f.z;
            double rx = (sx - lay.card.X) / lay.s, ry = (sy - lay.card.Y) / lay.s;
            return (Math.Min(Math.Max(rx / lay.W, 0), 1), Math.Min(Math.Max(1 - ry / lay.H, 0), 1));
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
        // 运动模糊：按几个镜头位置分别画、再平均（只在镜头快速移动的帧用）
        void ComposeShutter(byte[] src, FramePlan[] subs, List<Compositor.Overlay> overlays, byte[] dst);
    }

    // 逐帧合成：背景 → 阴影 → 圆角画面 → 点击波纹 → 鼠标，整幅按镜头推拉（都在 y 朝下的坐标里算）
    sealed class Compositor : IComposer
    {
        public string Name => "CPU";
        public bool Gpu => false;
        public bool Nv12In => false;
        public bool Nv12Out => false;
        public void Dispose() { }
        public byte[] Background => bg;      // 背景 + 阴影（显卡版直接拿去当贴图，两边一模一样）

        readonly StudioLayout L;
        readonly int W, H, CW, CH;
        readonly byte[] bg;                  // 背景 + 卡片阴影，每帧一样
        public readonly double cardL, cardT, cardW, cardH, ccx, ccy, r;
        readonly int[] ui; readonly float[] fu, qxs; readonly int[] bxs;   // 每列预算好的取样位置

        // loadImage(路径, 宽, 高) → 铺满这么大的 BGRA（多出来的裁掉）；null = 读不了，按纯色画
        public Compositor(StudioLayout lay, StudioLook look, Func<string, int, int, byte[]> loadImage = null)
        {
            L = lay; W = (int)lay.W; H = (int)lay.H; CW = (int)lay.cw; CH = (int)lay.ch;
            cardL = lay.card.X; cardW = lay.card.W; cardH = lay.card.H; cardT = lay.ch - lay.card.Y - cardH;
            ccx = cardL + cardW / 2; ccy = cardT + cardH / 2; r = lay.radius;
            ui = new int[CW]; fu = new float[CW]; qxs = new float[CW]; bxs = new int[CW];
            bg = MakeBackground(look ?? new StudioLook(), loadImage ?? Studio.LoadCover);
        }

        // 背景（纯色 / 渐变 / 图片）上叠卡片阴影：圆角矩形往下挪一点、模糊、按深浅压暗（四分之一分辨率算再放大，阴影本来就是糊的）。参数同 Mac 版
        byte[] MakeBackground(StudioLook look, Func<string, int, int, byte[]> loadImage)
        {
            var outp = new byte[CW * CH * 4];
            // 1) 底色
            byte[] img = null;
            var file = look.BackgroundImage;
            if (file != null) { try { img = loadImage(file, CW, CH); } catch { img = null; } }
            if (img != null && img.Length == outp.Length) Buffer.BlockCopy(img, 0, outp, 0, outp.Length);
            else if (look.Background == "gradient")
            {
                // 按角度从一边渐变到另一边：两个端点放在画布中心两侧、跨过整个画布（y 朝上算，同 Mac 版 CILinearGradient）
                double ang = look.GradientAngle * Math.PI / 180, dxu = Math.Cos(ang), dyu = Math.Sin(ang);
                double rr = (Math.Abs(dxu) * L.cw + Math.Abs(dyu) * L.ch) / 2;
                RGB c0 = look.Gradient0, c1 = look.Gradient1;
                Parallel.For(0, CH, Y =>
                {
                    double yu = L.ch - (Y + 0.5) - L.ch / 2;
                    for (int X = 0; X < CW; X++)
                    {
                        double xu = X + 0.5 - L.cw / 2;
                        double t = rr > 0 ? Math.Min(1, Math.Max(0, ((xu * dxu + yu * dyu) + rr) / (2 * rr))) : 0;
                        int i = (Y * CW + X) * 4;
                        outp[i] = B255(c0.B + (c1.B - c0.B) * t); outp[i + 1] = B255(c0.G + (c1.G - c0.G) * t); outp[i + 2] = B255(c0.R + (c1.R - c0.R) * t); outp[i + 3] = 255;
                    }
                });
            }
            else
            {
                // 纯色（选了图片 / 壁纸但读不到也走这里：白色）
                var c = look.Background == "color" ? look.Color : RGB.White;
                byte b = B255(c.B), g = B255(c.G), rd = B255(c.R);
                for (int i = 0; i < outp.Length; i += 4) { outp[i] = b; outp[i + 1] = g; outp[i + 2] = rd; outp[i + 3] = 255; }
            }
            // 2) 阴影
            double strength = Math.Min(Math.Max(look.Shadow, 0), 1), blur = Math.Min(Math.Max(look.ShadowBlur, 0), 1);
            if (strength < 0.001) return outp;
            int q = 4, w = (CW + q - 1) / q, h = (CH + q - 1) / q;
            var m = new float[w * h];
            double dy = L.ch * (0.004 + 0.016 * blur);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double px = (x + 0.5) * q, py = (y + 0.5) * q - dy;
                    double d = RoundRectDist(px, py);
                    m[y * w + x] = (float)Math.Max(0, Math.Min(1, 0.5 - d / q));
                }
            Sprites.Blur(m, w, h, Math.Max(0.5, L.cw * (0.003 + 0.033 * blur)) / q);
            Parallel.For(0, CH, Y =>
            {
                double fy = (Y + 0.5) / q - 0.5; int y0 = Math.Max(0, Math.Min(h - 1, (int)Math.Floor(fy))), y1 = Math.Min(h - 1, y0 + 1); float ty = (float)Math.Max(0, Math.Min(1, fy - y0));
                for (int X = 0; X < CW; X++)
                {
                    double fx = (X + 0.5) / q - 0.5; int x0 = Math.Max(0, Math.Min(w - 1, (int)Math.Floor(fx))), x1 = Math.Min(w - 1, x0 + 1); float tx = (float)Math.Max(0, Math.Min(1, fx - x0));
                    float a = (m[y0 * w + x0] * (1 - tx) + m[y0 * w + x1] * tx) * (1 - ty) + (m[y1 * w + x0] * (1 - tx) + m[y1 * w + x1] * tx) * ty;
                    double k = 1 - strength * a;
                    int i = (Y * CW + X) * 4;
                    outp[i] = (byte)Math.Round(outp[i] * k); outp[i + 1] = (byte)Math.Round(outp[i + 1] * k); outp[i + 2] = (byte)Math.Round(outp[i + 2] * k);
                }
            });
            return outp;
        }

        static byte B255(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);

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

        // 运动模糊：每个子位置画一张，逐字节平均（BGRA，A 都是 255）
        byte[] tmp; int[] acc;
        public void ComposeShutter(byte[] src, FramePlan[] subs, List<Overlay> overlays, byte[] dst)
        {
            int len = CW * CH * 4;
            if (tmp == null) { tmp = new byte[len]; acc = new int[len]; }
            Array.Clear(acc, 0, len);
            foreach (var f in subs)
            {
                Compose(src, f, overlays, tmp);
                Parallel.For(0, CH, Y => { int o = Y * CW * 4, e = o + CW * 4; for (int i = o; i < e; i++) acc[i] += tmp[i]; });
            }
            int n = subs.Length, half = n / 2;
            Parallel.For(0, CH, Y => { int o = Y * CW * 4, e = o + CW * 4; for (int i = o; i < e; i++) dst[i] = (byte)((acc[i] + half) / n); });
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

    // ---- 一帧怎么画：导出和剪辑窗口的预览共用这一套，所以预览和成片一样 ----
    sealed class StudioScene
    {
        public readonly Track Track;
        public readonly StudioLayout Lay;
        public readonly StudioLook Look;
        public readonly StudioStyle St;
        public readonly Compositor Cpu;
        readonly Sprite arrow = Sprites.Arrow(), ring = Sprites.Ring();
        readonly Dictionary<int, Sprite> art = new Dictionary<int, Sprite>();

        public StudioScene(string dir, Track track, StudioLook look, StudioStyle st, Func<string, CursorShape, double, Sprite> loadCursor = null, Func<string, int, int, byte[]> loadImage = null, StudioLayout layout = null)
        {
            Track = track; Look = look; St = st;
            Lay = layout ?? new StudioLayout(track, look);
            Cpu = new Compositor(Lay, look, loadImage);
            for (int i = 0; i < track.Cursors.Count; i++)
            {
                var c = track.Cursors[i];
                if (c.Arrow || loadCursor == null || dir == null) continue;
                try { var sp = loadCursor(Path.Combine(dir, c.File), c, track.PixelsPerPoint); if (sp != null) art[i] = sp; } catch { }
            }
        }

        public FramePlan[] Plan(List<ZoomSegment> segments, double duration, int frames = 0) =>
            StudioPlan.Make(Track, Lay, St, Look, segments, frames > 0 ? frames : Math.Max(1, (int)(duration * St.Fps)), duration);

        // 第 i 帧（从 0 秒算）的点击波纹和鼠标
        public List<Compositor.Overlay> Overlays(int i, FramePlan f)
        {
            double t = i / (double)St.Fps, ppp = Track.PixelsPerPoint;
            var ov = new List<Compositor.Overlay>();
            // 点击波纹：0.5 秒内从 6 点扩到 28 点、渐渐淡掉
            if (Look.Ripple)
                foreach (var c in Track.Clicks)
                    if (t >= c[0] && t < c[0] + 0.5)
                    {
                        double u = (t - c[0]) / 0.5, e = 1 - Math.Pow(1 - u, 3);
                        double rr = (6 + 22 * e) * ppp * Look.CursorScale / 1.5;
                        ov.Add(new Compositor.Overlay { S = ring, X = c[1], Y = c[2], K = rr / 52, Alpha = 1 - u });
                    }
            if (f.shape >= 0 && f.alpha > 0.002)
            {
                var sp = art.TryGetValue(f.shape, out var a) ? a : arrow;
                ov.Add(new Compositor.Overlay { S = sp, X = f.mx, Y = Lay.H - f.my, K = ppp * Look.CursorScale * f.press / sp.Ppp, Alpha = f.alpha });
            }
            return ov;
        }

        // 合成第 i 帧；返回 true = 这帧加了运动模糊
        public bool Frame(IComposer comp, byte[] src, int i, FramePlan[] plan, byte[] dst)
        {
            var f = plan[Math.Min(Math.Max(i, 0), plan.Length - 1)];
            var ov = Overlays(i, f);
            var subs = StudioPlan.Shutter(Look.Blur, Math.Min(Math.Max(i, 0), plan.Length - 1), plan, Lay);
            if (subs == null) { comp.Compose(src, f, ov, dst); return false; }
            comp.ComposeShutter(src, subs, ov, dst);
            return true;
        }
    }

    // ---- 导出：ffmpeg 解码 → 合成 → ffmpeg 编码 ----
    sealed class VideoInfo
    {
        public int Width, Height; public double Duration; public bool HasAudio; public string Matrix = "bt709";
        public List<int> AudioChannels = new List<int>();   // 每条音轨几个声道（Mac 录的：双声道 = 电脑声音、单声道 = 麦克风；Windows 录的只有一条，两样混在一起）
        public VideoInfo With(int w, int h) { var c = (VideoInfo)MemberwiseClone(); c.Width = w; c.Height = h; return c; }
    }

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
            foreach (Match a in Regex.Matches(err, @"Stream #\d+:\d+.*?: Audio: ([^\r\n]*)"))
            {
                var ch = a.Groups[1].Value;
                info.AudioChannels.Add(Regex.IsMatch(ch, @"\bmono\b") ? 1 : Regex.Match(ch, @"\b(\d+) channels\b") is Match m && m.Success ? int.Parse(m.Groups[1].Value) : 2);
            }
            info.HasAudio = info.AudioChannels.Count > 0;
            return info;
        }

        // 显卡合成：Windows 程序启动时设好（StudioGpu.cs）；没设（Mac 上的测试工具）或建不起来就用 CPU
        public static Func<StudioLayout, Compositor, VideoInfo, IComposer> GpuFactory;

        // 读轨迹；轨迹按画面像素记，万一尺寸对不上（比如手动换过 raw.mp4）就按比例换算
        public static Track LoadTrack(string dir, VideoInfo info)
        {
            var track = Track.FromJson(File.ReadAllText(Path.Combine(dir, "track.json")));
            if (info != null && (track.Width != info.Width || track.Height != info.Height))
            {
                double kx = info.Width / (double)track.Width, ky = info.Height / (double)track.Height;
                foreach (var m in track.Moves) { m[1] *= kx; m[2] *= ky; }
                foreach (var c in track.Clicks) { c[1] *= kx; c[2] *= ky; }
                track.Width = info.Width; track.Height = info.Height;
            }
            return track;
        }

        // 图片铺满 w×h（多出来的裁掉、居中），BGRA；交给 ffmpeg 读，jpg / png / bmp / webp 都行
        public static byte[] LoadCover(string file, int w, int h)
        {
            var psi = new ProcessStartInfo(FfmpegPath, $"-v error -i \"{file}\" -frames:v 1 -vf \"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},format=bgra\" -f rawvideo -pix_fmt bgra pipe:1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                var buf = new byte[w * h * 4]; int got = 0; var st = p.StandardOutput.BaseStream;
                while (got < buf.Length) { int k = st.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                p.WaitForExit();
                return got == buf.Length ? buf : null;
            }
        }

        // 剪辑窗口预览：t 秒那一帧原画面，缩到 w×h 的 BGRA（拿不到返回 null）
        public static byte[] DecodeFrame(string raw, double t, int w, int h, string matrix = "bt709")
        {
            var psi = new ProcessStartInfo(FfmpegPath, string.Format(CultureInfo.InvariantCulture,
                "-v error -ss {0:0.###} -i \"{1}\" -an -sn -frames:v 1 -vf \"scale={2}:{3}:in_color_matrix={4}:in_range=tv:out_range=pc,format=bgra\" -f rawvideo -pix_fmt bgra pipe:1", Math.Max(0, t), raw, w, h, matrix))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                var buf = new byte[w * h * 4]; int got = 0; var st = p.StandardOutput.BaseStream;
                while (got < buf.Length) { int k = st.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                p.WaitForExit();
                return got == buf.Length ? buf : null;
            }
        }

        // 预览播放：从 t0 秒开始按 fps 连续解码，缩到 w×h 的 BGRA（调用方读 StandardOutput，用完 Kill）
        public static Process StartDecode(string raw, double t0, int w, int h, int fps, string matrix = "bt709") =>
            Start(string.Format(CultureInfo.InvariantCulture,
                "-v error -ss {0:0.###} -i \"{1}\" -an -sn -vf \"setpts=PTS-STARTPTS,fps={5},scale={2}:{3}:in_color_matrix={4}:in_range=tv:out_range=pc,format=bgra\" -f rawvideo -pix_fmt bgra pipe:1",
                Math.Max(0, t0), raw, w, h, matrix, fps), false);

        // 把原始素材的声音抽成 wav（预览播放用）
        public static bool ExtractAudio(string raw, string wav) =>
            RunQuiet($"-v error -y -i \"{raw}\" -vn -ac 2 -ar 48000 -c:a pcm_s16le \"{wav}\"", 120000) && File.Exists(wav);

        // 导出（剪辑窗口的「导出」、自测都走这里）：按工程的推近段、剪掉 / 快放、修剪、样式、声音逐帧合成 → H.264 mp4（或 GIF）
        // project = null：读素材夹里的 project.json（没有就按自动规则）；opt = null：原始分辨率、60 帧、高画质（旧行为）
        // cancel：中途取消，删掉写了一半的文件，抛 OperationCanceledException
        // 环境变量（自测用）：STUDIO_COMPOSE=cpu|gpu（gpu = 必须用显卡，失败就报错）；STUDIO_ENCODER=libx264|h264_nvenc|…；STUDIO_HWDEC=0
        public static RenderStats Render(string dir, string outPath, StudioStyle st, Action<double> progress, Func<string, CursorShape, double, Sprite> loadCursor = null, Action<string> log = null, StudioProject project = null,
                                         ExportOptions opt = null, CancellationToken cancel = default)
        {
            if (opt != null && opt.Gif) return RenderGif(dir, outPath, st, progress, loadCursor, log, project, opt, cancel);
            var total = Stopwatch.StartNew();
            var stats = new RenderStats();
            string raw = Path.Combine(dir, "raw.mp4");
            var info = Probe(raw);
            var track = LoadTrack(dir, info);
            var proj = project ?? StudioProject.Load(dir, track, info.Duration, st.Zoom);
            if (opt != null) st = new StudioStyle { Zoom = st.Zoom, Fps = opt.Fps, BitsPerPixel = opt.BitsPerPixel };
            // 分辨率：按原始画布算缩放 k；k 小于一半时先把原画面缩到「画布的 2 倍」再合成（推近 2 倍字也清楚，又不至于缩太狠起锯齿）
            var lay0 = new StudioLayout(track, proj.Look);
            double k = opt?.CanvasScale(lay0.cw, lay0.ch) ?? 1, kd = Math.Min(1, 2 * k);
            int W = info.Width, H = info.Height;
            if (kd < 0.999) { W = Math.Max(2, (int)Math.Round(info.Width * kd / 2) * 2); H = Math.Max(2, (int)Math.Round(info.Height * kd / 2) * 2); track = track.Scaled(W, H); }
            // 剪掉 / 快放：轨迹和推近段都换到成片时间再算镜头（预览按素材时间，剪切处走法略有不同，成片更顺；同 Mac 版）
            var map = new TimeMap(proj);
            var wtrack = map.Warp(track); var wproj = map.Warp(proj);
            double outDur = map.Out(info.Duration);
            var lay = new StudioLayout(track, proj.Look, k / kd);
            var scene = new StudioScene(dir, wtrack, proj.Look, st, loadCursor, null, lay);
            int nAll = Math.Max(1, (int)(outDur * st.Fps));
            var plan = scene.Plan(wproj.Segments, outDur, nAll);
            // 修剪：导出成片第 first..<last 帧（镜头按整段算，和预览一致）
            int first = Math.Min(Math.Max(0, (int)Math.Round(proj.TrimStart * st.Fps)), nAll - 1);
            int last = Math.Max(first + 1, Math.Min(nAll, wproj.TrimEnd > proj.TrimStart ? (int)Math.Round(wproj.TrimEnd * st.Fps) : nAll));
            int n = last - first;
            double t0 = first / (double)st.Fps, len = n / (double)st.Fps;
            // 自测：STUDIO_PLANDUMP=文件 → 每帧写「秒,放大倍数,鼠标离画面中心多远（横、竖，按半个画面算）,镜头横 / 竖是否顶到画布边（1/0）」
            var dump = Environment.GetEnvironmentVariable("STUDIO_PLANDUMP");
            if (dump != null)
                File.WriteAllLines(dump, plan.Select((f, i) =>
                {
                    double ex = lay.cw / (2 * f.z), ey = lay.ch / (2 * f.z);
                    bool edgeX = f.sx - ex < 2 || lay.cw - ex - f.sx < 2, edgeY = f.sy - ey < 2 || lay.ch - ey - f.sy < 2;
                    return string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3},{3:F3},{4},{5}",
                        i / (double)st.Fps, f.z, (lay.card.X + f.mx * lay.s - f.sx) * f.z / (lay.cw / 2), (lay.card.Y + f.my * lay.s - f.sy) * f.z / (lay.ch / 2), edgeX ? 1 : 0, edgeY ? 1 : 0);
                }));
            var cpu = scene.Cpu;
            int CW = (int)lay.cw, CH = (int)lay.ch;
            log?.Invoke(string.Format(CultureInfo.InvariantCulture, "source {0}x{1} {2:0.00}s audio={3} matrix={4} → canvas {5}x{6} frames {7} (trim {8:0.00}–{9:0.00}s) segments {10} clicks {11} keys {12} aspect={13} background={14} blur={15} clickSound={16} fps={17} res={18} decode={19}x{20} cuts={21} out={22:0.00}s",
                info.Width, info.Height, info.Duration, info.HasAudio, info.Matrix, CW, CH, n, proj.TrimStart, proj.TrimEnd, wproj.Segments.Count, wtrack.Clicks.Count, wtrack.Keys.Count, proj.Look.Aspect, proj.Look.Background, proj.Look.Blur,
                proj.Look.ClickSound ? proj.Look.ClickStyle : "off", st.Fps, opt?.Resolution ?? "original", W, H, map.Pieces.Count, len));

            // 合成用显卡还是 CPU
            string want = (Environment.GetEnvironmentVariable("STUDIO_COMPOSE") ?? "").ToLowerInvariant();
            IComposer comp = cpu;
            if (want != "cpu" && GpuFactory != null)
            {
                try { comp = GpuFactory(lay, cpu, info.With(W, H)) ?? cpu; }
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
            // 原画面从修剪起点（素材时间）开始按帧率连续解；成片第 i 帧要素材的第 round((src(i) − 起点) × fps) 帧（剪掉的跳过、快放的隔几帧取一帧）
            double s0 = proj.TrimStart;
            string ss = string.Format(CultureInfo.InvariantCulture, "-ss {0:0.######} ", s0);
            string scale = kd < 0.999 ? $"scale={W}:{H}:flags=lanczos," : "";
            var dec = comp.Nv12In
                ? Start($"-v error {hw}{ss}-i \"{raw}\" -an -sn -vf \"setpts=PTS-STARTPTS,fps={st.Fps}{(scale.Length > 0 ? "," + scale.TrimEnd(',') : "")}\" -f rawvideo -pix_fmt nv12 pipe:1", false)
                : Start($"-v error {hw}{ss}-i \"{raw}\" -an -sn -vf \"setpts=PTS-STARTPTS,fps={st.Fps},{scale}scale=in_color_matrix={mat}:in_range=tv:out_range=pc,format=bgra\" -f rawvideo -pix_fmt bgra pipe:1", false);
            long rate = Math.Max(opt?.MinRate ?? 10_000_000, (long)(CW * (double)CH * st.Fps * st.BitsPerPixel));
            // 声音：原声（剪掉 / 快放 / 修剪、按音量）+ 点击音效 混成一条，软限幅
            var clickTimes = proj.Look.ClickSound && proj.Audio.Clicks > 0.001 ? wtrack.Clicks.Select(c => c[0] - t0).Where(t => t >= 0 && t < len).ToList() : new List<double>();
            var aud = AudioGraph(raw, info, proj, map, clickTimes, len, 1);
            string lenS = len.ToString("0.######", CultureInfo.InvariantCulture);
            if (aud != null) log?.Invoke("audio graph: " + aud.Graph);
            string audio = aud == null ? "-map 0:v" : $"{aud.Inputs} -filter_complex \"{aud.Graph}\" -map 0:v -map \"[aout]\" -c:a aac -b:a 192k";
            bool x264 = encoder == "libx264";
            string input = comp.Nv12Out
                ? $"-f rawvideo -pix_fmt nv12 -s {CW}x{CH} -r {st.Fps} -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 -i pipe:0"
                : $"-f rawvideo -pix_fmt bgra -s {CW}x{CH} -r {st.Fps} -i pipe:0";
            string vf = comp.Nv12Out ? (x264 ? "-vf format=yuv420p " : "")
                                     : $"-vf \"scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format={(x264 ? "yuv420p" : "nv12")}\" ";
            var enc = Start($"-v error -y {input} {audio} {vf}{EncoderArgs(encoder, rate, st.Fps, comp.Gpu)} " +
                            $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -t {lenS} -movflags +faststart \"{outPath}\"", true);
            var decErr = new StringBuilder(); var encErr = new StringBuilder();
            dec.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (decErr) decErr.AppendLine(e.Data); }; dec.BeginErrorReadLine();
            enc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (encErr) encErr.AppendLine(e.Data); }; enc.BeginErrorReadLine();

            int frameBytes = comp.Nv12In ? W * H * 3 / 2 : W * H * 4, outBytes = comp.Nv12Out ? CW * CH * 3 / 2 : CW * CH * 4;
            var srcFree = new BlockingCollection<byte[]>(); var srcFull = new BlockingCollection<byte[]>(4);
            var dstFree = new BlockingCollection<byte[]>(); var dstFull = new BlockingCollection<byte[]>(3);
            for (int i = 0; i < 5; i++) srcFree.Add(new byte[frameBytes]);
            for (int i = 0; i < 4; i++) dstFree.Add(new byte[outBytes]);
            Exception failure = null;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var swRead = new Stopwatch(); var swWrite = new Stopwatch();

            // 读线程：解码出来的原画面（从修剪起点开始，按帧率）
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
                        while (got < frameBytes) { int kk = stream.Read(buf, got, frameBytes - got); if (kk <= 0) break; got += kk; }
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

            byte[] cur = null; int curIdx = -1;
            var sw = Stopwatch.StartNew();
            var swIn = new Stopwatch(); var swComp = new Stopwatch(); var swOut = new Stopwatch();
            int blurred = 0;
            try
            {
                for (int kf = 0; kf < n; kf++)
                {
                    if (failure != null) throw failure;
                    cancel.ThrowIfCancellationRequested();
                    int i = first + kf;
                    // 这一帧要素材的第几帧（从修剪起点算）；原画面不够就沿用最后一帧
                    int want_ = map.IsIdentity ? kf : (int)Math.Round((map.Src(i / (double)st.Fps) - s0) * st.Fps);
                    swIn.Start();
                    while (curIdx < want_ || cur == null)
                    {
                        if (!srcFull.TryTake(out var next, Timeout.Infinite, cts.Token)) break;
                        if (cur != null) srcFree.Add(cur);
                        cur = next; curIdx++;
                    }
                    swIn.Stop();
                    if (cur == null) throw new Exception("no frames decoded: " + decErr.ToString().Trim());
                    swOut.Start(); var dst = dstFree.Take(cts.Token); swOut.Stop();
                    swComp.Start(); if (scene.Frame(comp, cur, i, plan, dst)) blurred++; swComp.Stop();
                    swOut.Start(); dstFull.Add(dst, cts.Token); swOut.Stop();
                    if (kf % 30 == 0) progress?.Invoke(kf / (double)n);
                }
                dstFull.CompleteAdding();
                swOut.Start(); writer.Join(); swOut.Stop();
                if (failure != null) throw failure;
                swOut.Start(); while (!enc.WaitForExit(200)) cancel.ThrowIfCancellationRequested(); swOut.Stop();
                cts.Cancel();
                try { if (!dec.HasExited) dec.Kill(); } catch { }
                if (enc.ExitCode != 0) throw new Exception("encoder failed: " + encErr.ToString().Trim());
            }
            catch
            {
                cts.Cancel();
                try { dstFull.CompleteAdding(); } catch { }
                try { if (!dec.HasExited) dec.Kill(); } catch { }
                try { if (!enc.HasExited) enc.Kill(); enc.WaitForExit(3000); } catch { }
                for (int tries = 0; tries < 10 && File.Exists(outPath); tries++) { try { File.Delete(outPath); } catch { Thread.Sleep(100); } }
                if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                if (failure != null && !(failure is OperationCanceledException)) throw new Exception(failure.Message + " " + encErr.ToString().Trim(), failure);
                throw;
            }
            finally
            {
                if (comp != cpu) comp.Dispose();
                aud?.Cleanup();
            }
            stats.Frames = n; stats.Width = CW; stats.Height = CH; stats.Duration = len;
            stats.RenderSeconds = sw.Elapsed.TotalSeconds; stats.TotalSeconds = total.Elapsed.TotalSeconds;
            stats.WaitDecode = swIn.Elapsed.TotalSeconds; stats.Compose = swComp.Elapsed.TotalSeconds; stats.WaitEncode = swOut.Elapsed.TotalSeconds;
            stats.PipeRead = swRead.Elapsed.TotalSeconds; stats.PipeWrite = swWrite.Elapsed.TotalSeconds;
            log?.Invoke(stats.ToString());
            log?.Invoke($"rendered {n} frames in {sw.Elapsed.TotalSeconds:0.0}s ({n / sw.Elapsed.TotalSeconds:0.0} fps, {sw.Elapsed.TotalSeconds / Math.Max(0.01, len):0.00}× realtime), motion-blurred frames {blurred}");
            try { File.AppendAllText(Path.Combine(dir, "render.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + stats + Environment.NewLine); } catch { }
            progress?.Invoke(1);
            return stats;
        }

        // GIF：先按 720p / 30 帧出一份 mp4，再用 ffmpeg 两步调色板（palettegen / paletteuse）转成宽 ≤ 960、15 帧的 GIF；没有声音
        static RenderStats RenderGif(string dir, string outPath, StudioStyle st, Action<double> progress, Func<string, CursorShape, double, Sprite> loadCursor, Action<string> log, StudioProject project, ExportOptions opt, CancellationToken cancel)
        {
            var mp4 = Path.Combine(Path.GetTempPath(), "studio-gif-" + Guid.NewGuid().ToString("N") + ".mp4");
            var o = opt.Clone(); o.Gif = false; o.Resolution = "720"; o.Fps = 30; o.Quality = "standard";
            try
            {
                var stats = Render(dir, mp4, st, v => progress?.Invoke(v * 0.85), loadCursor, log, project, o, cancel);
                cancel.ThrowIfCancellationRequested();
                var p = Start($"-v error -y -i \"{mp4}\" -vf \"fps=15,scale='min(960,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -loop 0 \"{outPath}\"", false);
                var err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); }; p.BeginErrorReadLine();
                p.OutputDataReceived += (s, e) => { }; p.BeginOutputReadLine();
                while (!p.WaitForExit(200))
                    if (cancel.IsCancellationRequested) { try { p.Kill(); p.WaitForExit(3000); } catch { } try { File.Delete(outPath); } catch { } throw new OperationCanceledException(cancel); }
                if (p.ExitCode != 0) { try { File.Delete(outPath); } catch { } throw new Exception("gif failed: " + err.ToString().Trim()); }
                progress?.Invoke(1);
                return stats;
            }
            finally { try { File.Delete(mp4); } catch { } }
        }

        // ---- 声音（导出和剪辑窗口的预览共用，所以预览听到的就是成片的声音）----
        // 原声每条音轨：按修剪 / 剪掉 / 快放切块（快放用 atempo，音调不变）→ 接起来 → 乘音量；再混上点击音效 → 限幅（不破音）
        public sealed class AudioPlan
        {
            public string Inputs, Graph; readonly List<string> temp = new List<string>();   // Graph：直接放命令行（新版 ffmpeg 去掉了 -filter_complex_script）
            public void Temp(string f) => temp.Add(f);
            public void Cleanup() { foreach (var f in temp) try { File.Delete(f); } catch { } }
        }

        // firstInput：raw 在 ffmpeg 里是第几个输入（导出时 0 是画面管道，raw 是 1）；clickTimes 是成片里相对开头的秒数
        public static AudioPlan AudioGraph(string raw, VideoInfo info, StudioProject proj, TimeMap map, List<double> clickTimes, double len, int firstInput)
        {
            var a = proj.Audio;
            var spans = map.Spans(proj.TrimStart, proj.TrimEnd);
            var streams = new List<(int idx, double vol)>();
            if (!a.Muted)
                for (int i = 0; i < info.AudioChannels.Count; i++)
                {
                    double v = TrackVolume(a, info, i);
                    if (v > 0.001) streams.Add((i, v));
                }
            bool clicks = clickTimes != null && clickTimes.Count > 0;
            if (streams.Count == 0 && !clicks) return null;
            var plan = new AudioPlan();
            var g = new StringBuilder(); var mix = new List<string>();
            string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            const string fmt = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
            foreach (var (idx, vol) in streams)
            {
                string src = $"[{firstInput}:a:{idx}]aresample=48000:async=1:first_pts=0,{fmt}";
                if (spans.Count == 1) g.Append(src).Append(Piece(spans[0], "")).Append($",volume={F(vol)}[s{idx}];\n");
                else
                {
                    g.Append(src).Append($",asplit={spans.Count}");
                    for (int j = 0; j < spans.Count; j++) g.Append($"[s{idx}p{j}]");
                    g.Append(";\n");
                    for (int j = 0; j < spans.Count; j++) g.Append($"[s{idx}p{j}]").Append(Piece(spans[j], "").TrimStart(',')).Append($"[s{idx}q{j}];\n");
                    for (int j = 0; j < spans.Count; j++) g.Append($"[s{idx}q{j}]");
                    g.Append($"concat=n={spans.Count}:v=0:a=1,volume={F(vol)}[s{idx}];\n");
                }
                mix.Add($"[s{idx}]");
            }
            string inputs = $"-i \"{raw}\"";
            if (clicks)
            {
                var wav = Path.Combine(Path.GetTempPath(), "studio-clicks-" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(wav, ClickSound.Track(clickTimes, len, proj.Look.ClickStyle));
                plan.Temp(wav);
                int ci = streams.Count > 0 ? firstInput + 1 : firstInput;
                if (streams.Count == 0) inputs = $"-i \"{wav}\""; else inputs += $" -i \"{wav}\"";
                g.Append($"[{ci}:a:0]{fmt},volume={F(proj.Audio.Clicks)}[clk];\n");
                mix.Add("[clk]");
            }
            // 限幅（防破音）：超过 100% 时用 ffmpeg 的前瞻限幅器压在 0.9 以内，不自动抬音量；正常音量不受影响
            string soft = $"alimiter=limit=0.9:attack=2:release=60:level=0:latency=1,{fmt}";
            if (mix.Count == 1) g.Append(mix[0]).Append(soft).Append("[aout]");
            else g.Append(string.Concat(mix)).Append($"amix=inputs={mix.Count}:duration=longest:normalize=0,{soft}[aout]");
            plan.Inputs = inputs; plan.Graph = g.ToString().Replace("\n", "");
            return plan;

            string Piece((double start, double end, double rate) sp, string pre)
            {
                var t = $",atrim=start={F(sp.start)}:end={F(sp.end)},asetpts=PTS-STARTPTS";
                if (sp.rate > 1.0001) t += $",atempo={F(Math.Min(100, sp.rate))}";
                return t;
            }
        }

        // 第 i 条音轨用哪个音量：Windows 录的只有一条（电脑声音和麦克风混在一起）→ 用「电脑声音」；Mac 录的单声道 = 麦克风
        public static double TrackVolume(AudioSettings a, VideoInfo info, int i) =>
            info.AudioChannels.Count > 1 && info.AudioChannels[i] == 1 ? a.Mic : a.System;

        // 剪辑窗口预览用的整条声音（修剪范围、按成片时间，剪掉 / 快放 / 音量 / 点击声都已经做好），写成 wav
        public static bool RenderAudio(string dir, VideoInfo info, Track track, StudioProject proj, string wav)
        {
            var raw = Path.Combine(dir, "raw.mp4");
            var map = new TimeMap(proj);
            double t0 = proj.TrimStart, len = Math.Max(0.05, map.Out(proj.TrimEnd) - t0);
            var clickTimes = proj.Look.ClickSound && proj.Audio.Clicks > 0.001 ? map.Warp(track).Clicks.Select(c => c[0] - t0).Where(t => t >= 0 && t < len).ToList() : new List<double>();
            var aud = AudioGraph(raw, info, proj, map, clickTimes, len, 0);
            try
            {
                string args = aud == null
                    ? $"-v error -y -f lavfi -i anullsrc=r=48000:cl=stereo -t {len.ToString("0.###", CultureInfo.InvariantCulture)} -c:a pcm_s16le \"{wav}\""
                    : $"-v error -y {aud.Inputs} -filter_complex \"{aud.Graph}\" -map \"[aout]\" -t {len.ToString("0.######", CultureInfo.InvariantCulture)} -ac 2 -ar 48000 -c:a pcm_s16le \"{wav}\"";
                return RunQuiet(args, 180000) && File.Exists(wav);
            }
            finally { aud?.Cleanup(); }
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

        public static bool RunQuiet(string args, int timeoutMs)
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

        public static Process Start(string args, bool stdin)
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
