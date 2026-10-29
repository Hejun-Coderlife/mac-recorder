// 画布上的后期层（照 Mac 版 Keys.swift / Camera.swift）：在镜头推拉之后、按画布像素叠上去，所以推近时不跟着放大
//   · 快捷键键帽：录制时记下的组合键（只记带 Ctrl / Alt / Win 的组合和 Esc、方向键这类功能键，不记打的字），约 1.2 秒后淡出
//   · 摄像头画中画：camera.mp4 按「摄像头」样式和「布局」轨（小窗 / 左右分屏 / 人像在左 / 摄像头全屏 / 隐藏）叠上去，换布局时 0.4 秒平滑形变
// 导出（CPU 出 BGRA、显卡出 NV12）和剪辑窗口的预览（BGRA）共用这一套，所以预览和成片一样
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScreenRecorder
{
    // ---- track.json 里记的快捷键（同 Mac 版 KeyStroke：t = 离开头几秒，keys = ["Ctrl", "Shift", "C"]） ----
    sealed class KeyStroke
    {
        public double T;
        public string[] Keys = new string[0];

        public string ToJson()
        {
            var sb = new StringBuilder("{\"keys\":[");
            sb.Append(string.Join(",", Keys.Select(k => "\"" + k.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")));
            sb.Append("],\"t\":").Append(T.ToString("0.###", CultureInfo.InvariantCulture)).Append('}');
            return sb.ToString();
        }
        public static KeyStroke From(Dictionary<string, object> o)
        {
            if (o == null || !(o.TryGetValue("keys", out var k) && k is List<object> l)) return null;
            var j = new JsonR(o);
            var keys = l.OfType<string>().ToArray();
            return keys.Length == 0 ? null : new KeyStroke { T = j.Num("t", 0), Keys = keys };
        }
    }

    // 按下的这一键要不要记、记成什么（纯函数，好测）：普通打字（没有 Ctrl / Alt / Win、也不是功能键）返回 null
    static class KeyLabel
    {
        static readonly HashSet<int> Mods = new HashSet<int> { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x14, 0x90, 0x91 };   // Shift Ctrl Alt Win，左右；大写锁定、NumLock、ScrollLock
        static readonly Dictionary<int, string> Named = new Dictionary<int, string>
        {
            { 0x1B, "Esc" }, { 0x09, "Tab" }, { 0x0D, "Enter" }, { 0x08, "Backspace" }, { 0x2E, "Delete" }, { 0x2D, "Insert" },
            { 0x25, "←" }, { 0x26, "↑" }, { 0x27, "→" }, { 0x28, "↓" }, { 0x24, "Home" }, { 0x23, "End" }, { 0x21, "PgUp" }, { 0x22, "PgDn" },
            { 0x20, "Space" }, { 0x2C, "PrtSc" },
            { 0xBA, ";" }, { 0xBB, "=" }, { 0xBC, "," }, { 0xBD, "-" }, { 0xBE, "." }, { 0xBF, "/" }, { 0xC0, "`" }, { 0xDB, "[" }, { 0xDC, "\\" }, { 0xDD, "]" }, { 0xDE, "'" },
        };
        // 单按也记的功能键：Esc、Tab、回车、退格、Delete、方向键、Home/End/翻页、F1–F24、PrtSc
        static bool Standalone(int vk) => vk == 0x1B || vk == 0x09 || vk == 0x0D || vk == 0x08 || vk == 0x2E || (vk >= 0x21 && vk <= 0x28) || (vk >= 0x70 && vk <= 0x87) || vk == 0x2C;

        public static string[] Label(int vk, bool ctrl, bool alt, bool shift, bool win)
        {
            if (Mods.Contains(vk)) return null;
            if (!(ctrl || alt || win || Standalone(vk))) return null;
            var k = new List<string>();
            if (win) k.Add("Win"); if (ctrl) k.Add("Ctrl"); if (alt) k.Add("Alt"); if (shift) k.Add("Shift");
            k.Add(Name(vk));
            return k.ToArray();
        }

        public static string Name(int vk)
        {
            if (Named.TryGetValue(vk, out var n)) return n;
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();                 // A–Z
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();                 // 0–9
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);                   // 小键盘数字
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);                     // F1–F24
            return "#" + vk.ToString("X2");
        }
    }

    // ---- track.json 里记的摄像头素材（同 Mac 版 CameraTrack） ----
    sealed class CameraTrack
    {
        public string File = "camera.mp4";
        public double Offset;                 // camera.mp4 的第 0 秒 = raw.mp4 的第几秒（同一时钟对齐）
        public double X = 0.86, Y = 0.80;     // 录的时候小窗中心在画面里的位置（比例，左上为原点）
        public double Size = 0.24;            // 小窗直径 / 画面短边
        public bool Round = true;

        public string ToJson()
        {
            var w = new JsonW(); w.Begin();
            w.Str("file", File); w.Num("offset", Offset); w.Bool("round", Round); w.Num("size", Size); w.Num("x", X); w.Num("y", Y);
            w.End(); return w.ToString();
        }
        public static CameraTrack From(Dictionary<string, object> o)
        {
            var j = new JsonR(o); var z = new CameraTrack();
            return new CameraTrack { File = j.Str("file", z.File), Offset = j.Num("offset", 0), X = j.Num("x", z.X), Y = j.Num("y", z.Y), Size = j.Num("size", z.Size), Round = j.Bool("round", z.Round) };
        }
    }

    // ---- 剪辑窗口里的摄像头样式（project.json 的 camera，同 Mac 版 CameraSettings） ----
    sealed class CameraSettings
    {
        public bool Show = true;
        public bool Round = true;             // 圆形 / 圆角方形
        public double Size = 0.24;            // 小窗大小：占画布短边的比例
        public double X = 0.86, Y = 0.80;     // 小窗中心（画布里的比例，左上为原点）
        public double Border = 4;             // 边框粗细（点，按画布短边 1080 折算）
        public RGB BorderColor = RGB.White;
        public double Shadow = 0.5;           // 阴影 0–1
        public bool Mirror = true;            // 左右镜像（像照镜子）
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        static readonly string[] Known = { "show", "round", "size", "x", "y", "border", "borderColor", "shadow", "mirror" };

        public CameraSettings Clone() { var c = (CameraSettings)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        public static CameraSettings From(CameraTrack t) => new CameraSettings { X = t.X, Y = t.Y, Size = t.Size, Round = t.Round };
        public void Write(JsonW w)
        {
            w.Begin();
            w.Num("border", Border); w.Rgb("borderColor", BorderColor); w.Bool("mirror", Mirror); w.Bool("round", Round);
            w.Num("shadow", Shadow); w.Bool("show", Show); w.Num("size", Size); w.Num("x", X); w.Num("y", Y);
            w.Extra(Extra); w.End();
        }
        public static CameraSettings From(Dictionary<string, object> o)
        {
            var j = new JsonR(o); var z = new CameraSettings();
            return new CameraSettings
            {
                Show = j.Bool("show", z.Show), Round = j.Bool("round", z.Round), Size = j.Num("size", z.Size), X = j.Num("x", z.X), Y = j.Num("y", z.Y),
                Border = j.Num("border", z.Border), BorderColor = j.Rgb("borderColor", z.BorderColor), Shadow = j.Num("shadow", z.Shadow), Mirror = j.Bool("mirror", z.Mirror),
                Extra = JsonR.Unknown(o, Known),
            };
        }
    }

    // 布局轨上的一段（素材时间）：这段时间里摄像头怎么摆；不在任何一段里 = 小窗。mode 的字同 Mac 版
    sealed class CameraLayoutSeg
    {
        public static readonly string[] Modes = { "pip", "split", "splitSwap", "full", "hidden" };
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public double Start, End;
        public string Mode = "full";
        public Dictionary<string, object> Extra = new Dictionary<string, object>();

        public CameraLayoutSeg Clone() { var c = (CameraLayoutSeg)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        public void Write(JsonW w)
        {
            w.BeginItem(); w.Num("end", End); w.Str("id", Id); w.Str("mode", Mode); w.Num("start", Start); w.Extra(Extra); w.End();
        }
        public static CameraLayoutSeg From(Dictionary<string, object> o)
        {
            var j = new JsonR(o);
            return new CameraLayoutSeg
            {
                Id = j.Str("id", Guid.NewGuid().ToString().ToUpperInvariant()), Start = j.Num("start", 0), End = j.Num("end", 0), Mode = j.Str("mode", "full", Modes),
                Extra = JsonR.Unknown(o, new[] { "id", "start", "end", "mode" }),
            };
        }
        public static string Name(string m, Func<string, string, string> L)
        {
            switch (m)
            {
                case "pip": return L("小窗", "Bubble");
                case "split": return L("左右分屏", "Side by side");
                case "splitSwap": return L("分屏（人像在左）", "Side by side (camera left)");
                case "full": return L("摄像头全屏", "Camera full");
                default: return L("隐藏", "Hidden");
            }
        }
    }

    // ---- 一块要叠到画布上的图：BGRA 预乘（0–1），左上角落在画布 (X, Y) ----
    sealed class Layer
    {
        public int X, Y, W, H;
        public float[] Px;
    }

    // ---- 快捷键键帽：时间用素材时间存、按成片时间查（同 Mac 版 KeyLayer） ----
    sealed class KeyLayer
    {
        struct Item { public double Start; public string[] Keys; }
        readonly List<Item> items;
        readonly StudioLook look;
        readonly double cw, ch, raised;
        public const double Hold = 1.2, FadeIn = 0.1, FadeOut = 0.3;
        // 画一排键帽（px = 键帽高，像素）：Windows 程序用 GDI+ 画真字；在 Mac 上跑核心自测时用不带字的方块
        public static Func<string[], bool, double, Sprite> Renderer = PlainRender;

        public KeyLayer(List<KeyStroke> strokes, StudioLook look, TimeMap map, double trimStart, double trimEnd, double cw, double ch, bool captionsShown)
        {
            this.look = look; this.cw = cw; this.ch = ch;
            raised = captionsShown ? 0.11 : 0;
            items = (strokes ?? new List<KeyStroke>()).Where(k => k.T >= trimStart && k.T < trimEnd && !map.InCut(k.T))
                .Select(k => new Item { Start = map.Out(k.T), Keys = k.Keys }).OrderBy(k => k.Start).ToList();
        }
        public static KeyLayer Make(Track track, StudioProject p, TimeMap map, double cw, double ch, bool captionsShown)
        {
            if (!p.Look.ShowKeys || track?.Shortcuts == null || track.Shortcuts.Count == 0) return null;
            var k = new KeyLayer(track.Shortcuts, p.Look, map, p.TrimStart, p.TrimEnd, cw, ch, captionsShown);
            return k.items.Count == 0 ? null : k;
        }

        // 成片时间 t：最近按的那一下（下一下一按就换掉），淡入淡出；返回键帽、它在画布上的位置（左上角）、透明度
        public (string[] keys, double alpha, double age)? At(double t)
        {
            int i = items.FindLastIndex(x => x.Start <= t);
            if (i < 0) return null;
            var it = items[i]; double age = t - it.Start;
            double next = i + 1 < items.Count ? items[i + 1].Start : double.PositiveInfinity;
            double end = Math.Min(it.Start + Hold, next);
            if (age >= end - it.Start) return null;
            double a = Math.Min(1, age / FadeIn);
            if (next > it.Start + Hold) a = Math.Min(a, (end - t) / FadeOut);
            if (a <= 0.01) return null;
            return (it.Keys, a, age);
        }

        public double KeyPx => look.KeysSize * Math.Min(cw, ch) * (cw < ch ? 1.2 : 1);

        // 键帽在画布上的位置（左上为原点）
        public (int x, int y) Place(int w, int h, double age)
        {
            double m = Math.Min(cw, ch) * 0.05, x;
            switch (look.KeysAt) { case "bottomLeft": x = m; break; case "bottomRight": x = cw - w - m; break; default: x = (cw - w) / 2; break; }
            double bottom = ch * (0.06 + (look.KeysAt == "bottom" ? raised : 0));
            double y = ch - bottom - h + (1 - Math.Min(1, age / 0.18)) * h * 0.25;   // 进场时从下面轻轻浮上来
            return ((int)Math.Round(x), (int)Math.Round(y));
        }

        public Layer LayerAt(double t, out double alpha)
        {
            alpha = 0;
            var a = At(t);
            if (a == null) return null;
            var sp = Renderer?.Invoke(a.Value.keys, look.KeysDark, KeyPx);
            if (sp == null) return null;
            var (x, y) = Place(sp.W, sp.H, a.Value.age);
            alpha = a.Value.alpha;
            return new Layer { X = x, Y = y, W = sp.W, H = sp.H, Px = sp.Px };
        }

        // 没有字体时的键帽：底板 + 每键一个圆角方块（大小、位置和真的一样，自测够用）
        static Sprite PlainRender(string[] keys, bool dark, double px)
        {
            double h = px, gap = px * 0.18, pad = px * 0.28, sh = px * 0.3;
            var widths = keys.Select(k => Math.Max(h, k.Length * px * 0.3 + px * 0.5)).ToArray();
            int W = (int)Math.Ceiling(widths.Sum() + gap * (keys.Length - 1) + pad * 2 + sh * 2), H = (int)Math.Ceiling(h + pad * 2 + sh * 2);
            var s = new Sprite { W = W, H = H, Px = new float[W * H * 4], Ppp = 1 };
            float pc = dark ? 0.1f : 0.98f, pa = dark ? 0.82f : 0.92f, kc = dark ? 1f : 0f, ka = dark ? 0.14f : 0.07f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double px0 = x + 0.5, py0 = y + 0.5;
                    double dp = Canvas.RoundRectDist(px0, py0, sh, sh, W - sh * 2, H - sh * 2, px * 0.42);
                    float a = (float)Math.Max(0, Math.Min(1, 0.5 - dp)) * pa; float c = pc * a;
                    double kx = sh + pad;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        double dk = Canvas.RoundRectDist(px0, py0, kx, sh + pad, widths[i], h, px * 0.22);
                        float ca = (float)Math.Max(0, Math.Min(1, 0.5 - dk)) * ka;
                        c = kc * ca + c * (1 - ca); a = ca + a * (1 - ca);
                        kx += widths[i] + gap;
                    }
                    int o = (y * W + x) * 4; s.Px[o] = s.Px[o + 1] = s.Px[o + 2] = c; s.Px[o + 3] = a;
                }
            return s;
        }
    }

    // ---- 摄像头视频按时间取帧（BGRA，最大 1280 宽）：顺着往后读；往回跳或跳得很远就从那附近重开（预览、导出共用） ----
    sealed class CameraFrames : IDisposable
    {
        readonly string path;
        public readonly int W, H;
        public readonly double Duration;
        const int Fps = 30;
        Process proc; Stream stream;
        double start; int idx = -1; byte[] cur, next;
        readonly object gate = new object();

        public CameraFrames(string path)
        {
            this.path = path;
            var info = Studio.Probe(path);
            Duration = info.Duration;
            double k = Math.Min(1, 1280.0 / Math.Max(1, info.Width));
            W = Math.Max(2, (int)Math.Round(info.Width * k / 2) * 2); H = Math.Max(2, (int)Math.Round(info.Height * k / 2) * 2);
        }

        void Open(double t)
        {
            Close();
            start = Math.Max(0, t - 0.05); idx = -1; cur = null; next = null;
            proc = Studio.Start(string.Format(CultureInfo.InvariantCulture,
                "-v error -ss {0:0.###} -i \"{1}\" -an -sn -vf \"setpts=PTS-STARTPTS,fps={2},scale={3}:{4},format=bgra\" -f rawvideo -pix_fmt bgra pipe:1", start, path, Fps, W, H), false);
            proc.ErrorDataReceived += (s, e) => { }; proc.BeginErrorReadLine();
            stream = proc.StandardOutput.BaseStream;
            next = ReadFrame();
        }

        byte[] ReadFrame()
        {
            var buf = new byte[W * H * 4]; int got = 0;
            while (got < buf.Length) { int k = stream.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
            return got == buf.Length ? buf : null;
        }

        // 摄像头视频第 t 秒那一帧（之前最近的一帧）；读不到返回 null
        public byte[] At(double t)
        {
            lock (gate)
            {
                if (Duration <= 0) return null;
                t = Math.Min(Math.Max(t, 0), Math.Max(0, Duration - 0.01));
                double curT = idx >= 0 ? start + idx / (double)Fps : double.NegativeInfinity;
                if (proc == null || t < curT - 0.02 || t > curT + 1.0 || (cur == null && next == null)) Open(t);
                while (next != null && start + (idx + 1) / (double)Fps <= t + 1e-4) { cur = next; idx++; next = ReadFrame(); }
                return cur ?? next;
            }
        }

        void Close()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { proc?.Dispose(); } catch { }
            proc = null; stream = null;
        }
        public void Dispose() { lock (gate) Close(); }
    }

    // ---- 摄像头画中画：每一帧在镜头推拉之后叠上去（素材时间查布局，切换时 0.4 秒平滑形变；同 Mac 版 CameraLayer） ----
    sealed class CameraLayer : IDisposable
    {
        public readonly CameraSettings S;
        readonly List<CameraLayoutSeg> segs;
        readonly CameraFrames frames;
        readonly double offset, cw, ch;
        public const double Transition = 0.4;
        public struct Geo { public double Sx, Sy, Sw, Sh; public double Cx, Cy, Cw, Ch; public double Radius, Alpha; }   // 屏幕画面放哪、人像卡片放哪（画布像素，左上为原点）

        public CameraLayer(CameraSettings s, List<CameraLayoutSeg> layouts, CameraFrames frames, double offset, double cw, double ch)
        {
            S = s; this.frames = frames; this.offset = offset; this.cw = cw; this.ch = ch;
            segs = (layouts ?? new List<CameraLayoutSeg>()).Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
        }

        public static CameraLayer Make(string dir, Track track, StudioProject p, double cw, double ch)
        {
            if (dir == null || track?.Camera == null || p.Camera == null || !p.Camera.Show) return null;
            var file = Path.Combine(dir, track.Camera.File);
            if (!File.Exists(file)) return null;
            CameraFrames f;
            try { f = new CameraFrames(file); } catch { return null; }
            if (f.Duration <= 0) { f.Dispose(); return null; }
            return new CameraLayer(p.Camera, p.Layouts, f, track.Camera.Offset, cw, ch);
        }

        public string ModeAt(double t) => segs.FirstOrDefault(x => t >= x.Start && t < x.End)?.Mode ?? "pip";

        // 小窗的位置大小（画布像素，左上为原点）
        public static (double x, double y, double side) PipRect(CameraSettings s, double cw, double ch)
        {
            double sh = Math.Min(cw, ch), side = Math.Max(16, s.Size * sh), m = sh * 0.02;
            double cx = Math.Min(Math.Max(s.X * cw, side / 2 + m), cw - side / 2 - m);
            double cy = Math.Min(Math.Max(s.Y * ch, side / 2 + m), ch - side / 2 - m);
            return (cx - side / 2, cy - side / 2, side);
        }

        public Geo GeoOf(string m)
        {
            double sh = Math.Min(cw, ch);
            var (px, py, side) = PipRect(S, cw, ch);
            double pr = S.Round ? side / 2 : side * 0.2;
            var g = new Geo { Sx = 0, Sy = 0, Sw = cw, Sh = ch, Cx = px, Cy = py, Cw = side, Ch = side, Radius = pr, Alpha = 1 };
            switch (m)
            {
                case "hidden": g.Alpha = 0; return g;
                case "full": g.Cx = 0; g.Cy = 0; g.Cw = cw; g.Ch = ch; g.Radius = 0; return g;
                case "split":
                case "splitSwap":
                    // 屏幕占左边（按原比例缩进去、居中），人像占右边一条圆角竖卡；对调就左右换过来（同 Mac 版）
                    double mg = sh * 0.04, camW = cw * 0.3;
                    double rx = mg, ry = mg, rw = cw - camW - mg * 3, rh = ch - mg * 2;
                    double k = Math.Min(rw / cw, rh / ch);
                    g.Sw = cw * k; g.Sh = ch * k; g.Sx = rx + rw / 2 - g.Sw / 2; g.Sy = ry + rh / 2 - g.Sh / 2;
                    g.Cx = cw - camW - mg; g.Cy = mg; g.Cw = camW; g.Ch = ch - mg * 2; g.Radius = sh * 0.03;
                    if (m == "splitSwap") { g.Sx = cw - (g.Sx + g.Sw); g.Cx = cw - (g.Cx + g.Cw); }
                    return g;
                default: return g;
            }
        }

        // t 时刻（素材时间）的几何：刚换布局的 0.4 秒里从上一种平滑变过去
        public Geo GeoAt(double t)
        {
            var changes = new List<(double t, string from, string to)>();
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                string before = i > 0 && segs[i - 1].End >= s.Start - 1e-6 ? segs[i - 1].Mode : "pip";
                if (before != s.Mode) changes.Add((s.Start, before, s.Mode));
                string after = i + 1 < segs.Count && segs[i + 1].Start <= s.End + 1e-6 ? segs[i + 1].Mode : "pip";
                if (after == "pip" && s.Mode != "pip") changes.Add((s.End, s.Mode, "pip"));
            }
            var cur = GeoOf(ModeAt(t));
            var c = changes.LastOrDefault(x => t >= x.t && t < x.t + Transition);
            if (c.from == null) return cur;
            double u = (t - c.t) / Transition, e = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;   // 缓进缓出
            return Lerp(GeoOf(c.from), GeoOf(c.to), e);
        }

        static Geo Lerp(Geo a, Geo b, double u)
        {
            double L(double p, double q) => p + (q - p) * u;
            return new Geo { Sx = L(a.Sx, b.Sx), Sy = L(a.Sy, b.Sy), Sw = L(a.Sw, b.Sw), Sh = L(a.Sh, b.Sh), Cx = L(a.Cx, b.Cx), Cy = L(a.Cy, b.Cy), Cw = L(a.Cw, b.Cw), Ch = L(a.Ch, b.Ch), Radius = L(a.Radius, b.Radius), Alpha = L(a.Alpha, b.Alpha) };
        }

        public byte[] Frame(double srcT, out int fw, out int fh) { fw = frames.W; fh = frames.H; return frames.At(srcT - offset); }

        // 人像卡片：摄像头画面按「铺满」裁进卡片，圆角遮罩、边框、阴影（同 Mac 版 drawCamera）
        public Layer Card(Geo g, byte[] cam, int fw, int fh)
        {
            double short_ = Math.Min(cw, ch);
            double radius = Math.Min(g.Radius, Math.Min(g.Cw, g.Ch) / 2);
            double bw = S.Border * short_ / 1080 * (radius < 1 ? 0 : 1);
            bool border = bw > 0.3, shadow = S.Shadow > 0.01 && radius >= 1;
            double sig = short_ * 0.012, dy = short_ * 0.008;
            double ext = (border ? bw : 0) + (shadow ? sig * 3 + dy : 0) + 1;
            int x0 = Math.Max(0, (int)Math.Floor(g.Cx - ext)), y0 = Math.Max(0, (int)Math.Floor(g.Cy - ext));
            int x1 = Math.Min((int)cw, (int)Math.Ceiling(g.Cx + g.Cw + ext)), y1 = Math.Min((int)ch, (int)Math.Ceiling(g.Cy + g.Ch + ext));
            if (x1 <= x0 || y1 <= y0) return null;
            int W = x1 - x0, H = y1 - y0;
            var px = new float[W * H * 4];
            double k = Math.Max(g.Cw / fw, g.Ch / fh), mx = g.Cx + g.Cw / 2, my = g.Cy + g.Ch / 2;
            float br = (float)S.BorderColor.R, bg = (float)S.BorderColor.G, bb = (float)S.BorderColor.B;
            double shA = 0.55 * Math.Min(1, Math.Max(0, S.Shadow));
            bool mirror = S.Mirror;
            Parallel.For(0, H, yy =>
            {
                double py = y0 + yy + 0.5;
                for (int xx = 0; xx < W; xx++)
                {
                    double pxx = x0 + xx + 0.5;
                    double d = Canvas.RoundRectDist(pxx, py, g.Cx, g.Cy, g.Cw, g.Ch, radius);
                    float ci = (float)Math.Max(0, Math.Min(1, 0.5 - d));
                    float cb = border ? (float)Math.Max(0, Math.Min(1, 0.5 - (d - bw))) : 0;
                    float sa = 0;
                    if (shadow)
                    {
                        double ds = Canvas.RoundRectDist(pxx, py - dy, g.Cx - bw, g.Cy - bw, g.Cw + bw * 2, g.Ch + bw * 2, radius + bw);
                        sa = (float)(shA * 0.5 * Canvas.Erfc(ds / (sig * 1.41421356)));
                    }
                    if (ci <= 0 && cb <= 0 && sa <= 0.002f) continue;
                    // 从下往上：阴影（黑）→ 边框 → 摄像头画面
                    float a = sa, cB = 0, cG = 0, cR = 0;
                    if (cb > 0) { cB = bb * cb + cB * (1 - cb); cG = bg * cb + cG * (1 - cb); cR = br * cb + cR * (1 - cb); a = cb + a * (1 - cb); }
                    if (ci > 0)
                    {
                        double u = (pxx - mx) / k + fw / 2.0 - 0.5, v = (py - my) / k + fh / 2.0 - 0.5;
                        if (mirror) u = fw - 1 - u;
                        Canvas.Sample(cam, fw, fh, u, v, out float sb, out float sg, out float sr);
                        cB = sb * ci + cB * (1 - ci); cG = sg * ci + cG * (1 - ci); cR = sr * ci + cR * (1 - ci); a = ci + a * (1 - ci);
                    }
                    int o = (yy * W + xx) * 4; px[o] = cB; px[o + 1] = cG; px[o + 2] = cR; px[o + 3] = a;
                }
            });
            return new Layer { X = x0, Y = y0, W = W, H = H, Px = px };
        }

        public void Dispose() => frames.Dispose();
    }

    // ---- 把后期层叠到合成好的画布上（BGRA 或 NV12），导出和预览共用 ----
    sealed class Canvas : IDisposable
    {
        public readonly int CW, CH;
        public readonly CameraLayer Camera;
        public readonly KeyLayer Keys;
        readonly byte[] plainBgra;            // 不带卡片阴影的背景（分屏时屏幕画面缩小，露出来的地方）
        byte[] plainNv12, tmp;

        Canvas(int cw, int ch, CameraLayer cam, KeyLayer keys, byte[] plainBg) { CW = cw; CH = ch; Camera = cam; Keys = keys; plainBgra = plainBg; }

        // 没有要叠的就返回 null（导出、预览照旧）
        public static Canvas Make(string dir, Track track, StudioProject p, TimeMap map, int cw, int ch, bool captionsShown, Func<byte[]> plainBackground)
        {
            var keys = KeyLayer.Make(track, p, map, cw, ch, captionsShown);
            var cam = CameraLayer.Make(dir, track, p, cw, ch);
            if (keys == null && cam == null) return null;
            return new Canvas(cw, ch, cam, keys, cam != null ? plainBackground?.Invoke() : null);
        }

        // dst：合成好的画布（BGRA 或 NV12）；srcT = 这一帧在素材里的时间，outT = 成片时间（键帽按成片时间）
        public void Apply(byte[] dst, bool nv12, double srcT, double outT)
        {
            if (Camera != null)
            {
                var g = Camera.GeoAt(srcT);
                bool shrink = Math.Abs(g.Sw - CW) > 0.5 || Math.Abs(g.Sx) > 0.5 || Math.Abs(g.Sy) > 0.5;
                if (shrink && plainBgra != null) Shrink(dst, nv12, g);
                if (g.Alpha > 0.002 && g.Cw > 2)
                {
                    var cam = Camera.Frame(srcT, out int fw, out int fh);
                    if (cam != null) { var card = Camera.Card(g, cam, fw, fh); if (card != null) Blend(dst, nv12, card, g.Alpha); }
                }
            }
            if (Keys != null)
            {
                var l = Keys.LayerAt(outT, out double a);
                if (l != null) Blend(dst, nv12, l, a);
            }
        }

        // 屏幕画面（整张画布）缩进 g 的屏幕框，四周露出不带阴影的背景
        void Shrink(byte[] dst, bool nv12, CameraLayer.Geo g)
        {
            if (tmp == null || tmp.Length != dst.Length) tmp = new byte[dst.Length];
            Buffer.BlockCopy(dst, 0, tmp, 0, dst.Length);
            if (!nv12)
            {
                Buffer.BlockCopy(plainBgra, 0, dst, 0, dst.Length);
                ScalePlane(tmp, CW, CH, 4, dst, CW, CH, g.Sx, g.Sy, g.Sw, g.Sh);
            }
            else
            {
                if (plainNv12 == null) plainNv12 = ToNv12(plainBgra, CW, CH);
                Buffer.BlockCopy(plainNv12, 0, dst, 0, dst.Length);
                ScalePlane(tmp, CW, CH, 1, dst, CW, CH, g.Sx, g.Sy, g.Sw, g.Sh);   // Y
                int uvOff = CW * CH;
                ScalePlane(tmp, CW / 2, CH / 2, 2, dst, CW / 2, CH / 2, g.Sx / 2, g.Sy / 2, g.Sw / 2, g.Sh / 2, uvOff);
            }
        }

        // 把整张 src 平面（w×h，每像素 bpp 字节）双线性缩进 dst 的 (rx, ry, rw, rh)
        static void ScalePlane(byte[] src, int w, int h, int bpp, byte[] dst, int dw, int dh, double rx, double ry, double rw, double rh, int off = 0)
        {
            int x0 = Math.Max(0, (int)Math.Floor(rx)), y0 = Math.Max(0, (int)Math.Floor(ry));
            int x1 = Math.Min(dw, (int)Math.Ceiling(rx + rw)), y1 = Math.Min(dh, (int)Math.Ceiling(ry + rh));
            double kx = w / rw, ky = h / rh;
            Parallel.For(y0, y1, Y =>
            {
                double v = (Y + 0.5 - ry) * ky - 0.5;
                double covY = Math.Max(0, Math.Min(1, Math.Min(Y + 1, ry + rh) - Math.Max(Y, ry)));
                int v0 = (int)Math.Floor(v); double tv = v - v0;
                if (v0 < 0) { v0 = 0; tv = 0; } else if (v0 > h - 2) { v0 = Math.Max(0, h - 2); tv = 1; }
                for (int X = x0; X < x1; X++)
                {
                    double covX = Math.Max(0, Math.Min(1, Math.Min(X + 1, rx + rw) - Math.Max(X, rx)));
                    double cov = covX * covY;
                    if (cov <= 0) continue;
                    double u = (X + 0.5 - rx) * kx - 0.5;
                    int u0 = (int)Math.Floor(u); double tu = u - u0;
                    if (u0 < 0) { u0 = 0; tu = 0; } else if (u0 > w - 2) { u0 = Math.Max(0, w - 2); tu = 1; }
                    int i00 = off + (v0 * w + u0) * bpp, i01 = i00 + bpp, i10 = i00 + w * bpp, i11 = i10 + bpp;
                    int d = off + (Y * dw + X) * bpp;
                    for (int c = 0; c < bpp; c++)
                    {
                        if (bpp == 4 && c == 3) { dst[d + 3] = 255; continue; }
                        double s = (src[i00 + c] * (1 - tu) + src[i01 + c] * tu) * (1 - tv) + (src[i10 + c] * (1 - tu) + src[i11 + c] * tu) * tv;
                        dst[d + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(s * cov + dst[d + c] * (1 - cov))));
                    }
                }
            });
        }

        // 预乘 BGRA 小图叠到画布上（总透明度 alpha）
        public void Blend(byte[] dst, bool nv12, Layer l, double alpha)
        {
            float al = (float)alpha;
            int x0 = Math.Max(0, l.X), y0 = Math.Max(0, l.Y), x1 = Math.Min(CW, l.X + l.W), y1 = Math.Min(CH, l.Y + l.H);
            if (x1 <= x0 || y1 <= y0) return;
            if (!nv12)
            {
                Parallel.For(y0, y1, Y =>
                {
                    for (int X = x0; X < x1; X++)
                    {
                        int s = ((Y - l.Y) * l.W + (X - l.X)) * 4; float a = l.Px[s + 3] * al;
                        if (a <= 0.0005f) continue;
                        int d = (Y * CW + X) * 4;
                        for (int c = 0; c < 3; c++) dst[d + c] = (byte)Math.Max(0, Math.Min(255, l.Px[s + c] * al * 255 + dst[d + c] * (1 - a) + 0.5f));
                    }
                });
                return;
            }
            // NV12（BT.709 有限范围，同显卡版输出）：Y 逐像素，UV 每 2×2 一个
            Parallel.For(y0, y1, Y =>
            {
                for (int X = x0; X < x1; X++)
                {
                    int s = ((Y - l.Y) * l.W + (X - l.X)) * 4; float a = l.Px[s + 3] * al;
                    if (a <= 0.0005f) continue;
                    float b = l.Px[s] * al, g = l.Px[s + 1] * al, r = l.Px[s + 2] * al;
                    float y = 16 * a + 219 * (0.2126f * r + 0.7152f * g + 0.0722f * b);
                    int d = Y * CW + X;
                    dst[d] = (byte)Math.Max(0, Math.Min(255, y + dst[d] * (1 - a) + 0.5f));
                }
            });
            int uv = CW * CH;
            Parallel.For(y0 / 2, (y1 + 1) / 2, Yc =>
            {
                for (int Xc = x0 / 2; Xc < (x1 + 1) / 2; Xc++)
                {
                    float sa = 0, su = 0, sv = 0; int n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int X = Xc * 2 + dx, Y = Yc * 2 + dy; n++;
                            if (X < l.X || Y < l.Y || X >= l.X + l.W || Y >= l.Y + l.H || X >= CW || Y >= CH) continue;
                            int s = ((Y - l.Y) * l.W + (X - l.X)) * 4; float a = l.Px[s + 3] * al;
                            if (a <= 0) continue;
                            float b = l.Px[s] * al, g = l.Px[s + 1] * al, r = l.Px[s + 2] * al;
                            float yy = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                            sa += a; su += 128 * a + 224 * (b - yy) / 1.8556f; sv += 128 * a + 224 * (r - yy) / 1.5748f;
                        }
                    if (sa <= 0.0005f) continue;
                    sa /= n; su /= n; sv /= n;
                    int d = uv + (Yc * (CW / 2) + Xc) * 2;
                    if (Xc >= CW / 2 || Yc >= CH / 2) continue;
                    dst[d] = (byte)Math.Max(0, Math.Min(255, su + dst[d] * (1 - sa) + 0.5f));
                    dst[d + 1] = (byte)Math.Max(0, Math.Min(255, sv + dst[d + 1] * (1 - sa) + 0.5f));
                }
            });
        }

        // BGRA（全范围）→ NV12（BT.709 有限范围）
        public static byte[] ToNv12(byte[] bgra, int w, int h)
        {
            var o = new byte[w * h * 3 / 2];
            Parallel.For(0, h, Y =>
            {
                for (int X = 0; X < w; X++)
                {
                    int i = (Y * w + X) * 4; float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;
                    o[Y * w + X] = (byte)Math.Round(16 + 219 * (0.2126f * r + 0.7152f * g + 0.0722f * b));
                }
            });
            int uv = w * h;
            Parallel.For(0, h / 2, Yc =>
            {
                for (int Xc = 0; Xc < w / 2; Xc++)
                {
                    float su = 0, sv = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                        {
                            int i = ((Yc * 2 + dy) * w + Xc * 2 + dx) * 4; float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;
                            float yy = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                            su += 128 + 224 * (b - yy) / 1.8556f; sv += 128 + 224 * (r - yy) / 1.5748f;
                        }
                    int d = uv + (Yc * (w / 2) + Xc) * 2;
                    o[d] = (byte)Math.Max(0, Math.Min(255, Math.Round(su / 4))); o[d + 1] = (byte)Math.Max(0, Math.Min(255, Math.Round(sv / 4)));
                }
            });
            return o;
        }

        // 点到圆角矩形的有符号距离（里面为负）
        public static double RoundRectDist(double px, double py, double x, double y, double w, double h, double r)
        {
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
            double qx = Math.Abs(px - (x + w / 2)) - (w / 2 - r), qy = Math.Abs(py - (y + h / 2)) - (h / 2 - r);
            return qx > 0 && qy > 0 ? Math.Sqrt(qx * qx + qy * qy) - r : Math.Max(qx, qy) - r;
        }

        // 双线性取样（BGRA 字节 → 0–1）
        public static void Sample(byte[] img, int w, int h, double u, double v, out float b, out float g, out float r)
        {
            int u0 = (int)Math.Floor(u), v0 = (int)Math.Floor(v); float tu = (float)(u - u0), tv = (float)(v - v0);
            if (u0 < 0) { u0 = 0; tu = 0; } else if (u0 > w - 2) { u0 = w - 2; tu = 1; }
            if (v0 < 0) { v0 = 0; tv = 0; } else if (v0 > h - 2) { v0 = h - 2; tv = 1; }
            int i00 = (v0 * w + u0) * 4, i01 = i00 + 4, i10 = i00 + w * 4, i11 = i10 + 4;
            float w00 = (1 - tu) * (1 - tv), w01 = tu * (1 - tv), w10 = (1 - tu) * tv, w11 = tu * tv;
            b = (img[i00] * w00 + img[i01] * w01 + img[i10] * w10 + img[i11] * w11) / 255f;
            g = (img[i00 + 1] * w00 + img[i01 + 1] * w01 + img[i10 + 1] * w10 + img[i11 + 1] * w11) / 255f;
            r = (img[i00 + 2] * w00 + img[i01 + 2] * w01 + img[i10 + 2] * w10 + img[i11 + 2] * w11) / 255f;
        }

        // 互补误差函数（阴影边缘的柔和过渡，Abramowitz–Stegun 7.1.26）
        public static double Erfc(double x)
        {
            double z = Math.Abs(x), t = 1 / (1 + 0.3275911 * z);
            double y = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-z * z);
            return x >= 0 ? y : 2 - y;
        }

        public void Dispose() => Camera?.Dispose();
    }
}
