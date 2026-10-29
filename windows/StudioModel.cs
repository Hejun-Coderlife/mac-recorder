// Studio 剪辑的数据：工程文件 project.json（推近段、修剪、样式）、样式预设、推近段的自动生成规则、时间轴坐标换算、点击音效。
// 照 Mac 版 Studio.swift / StudioEditor.swift 移植，格式一样：同一份素材 Mac、Windows 都能打开、接着改。
// 只用纯 C#（不碰 WinForms），所以也能在 Mac 上用 .NET 8 跑（tools/StudioHarness）。
// 和 Mac 版的区别：Windows 没有透明背景（用户定的：白底出 mp4），默认背景是白色；Mac 做的工程如果是透明背景，在 Windows 上按白色画
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    // ---- 颜色（0–1） ----
    struct RGB
    {
        public double R, G, B;
        public RGB(double r, double g, double b) { R = r; G = g; B = b; }
        public static readonly RGB White = new RGB(1, 1, 1);
        public bool Same(RGB o) => Math.Abs(R - o.R) < 1e-6 && Math.Abs(G - o.G) < 1e-6 && Math.Abs(B - o.B) < 1e-6;
    }

    // 剪辑窗口「样式」面板里能调的东西：存在 project.json 里，也能存成预设。旧工程没有这些字段时用默认值
    sealed class StudioLook
    {
        // 名字和 Mac 版 project.json 里的字一样
        public static readonly string[] Aspects = { "original", "16:9", "9:16", "1:1", "4:3" };
        public static readonly string[] Backgrounds = { "color", "gradient", "wallpaper", "image" };   // Mac 还有 transparent（Windows 当白色）
        public static readonly string[] Blurs = { "off", "light", "medium", "strong" };
        public static readonly string[] Clicks = { "soft", "mouse", "thock", "wood", "pop", "tick" };

        public string Aspect = "original";
        public string Background = "color";
        public RGB Color = RGB.White;
        public RGB Gradient0 = new RGB(0.36, 0.40, 0.96), Gradient1 = new RGB(0.86, 0.43, 0.78);
        public double GradientAngle = 135;     // 度：0 = 从左到右，90 = 从下到上
        public string Wallpaper = "";          // 系统壁纸的路径
        public string ImagePath = "";          // 自己选的图片
        public string ImageBookmark;           // Mac 版的书签（Windows 用不上，原样留着）
        public double Padding = 0.06;          // 画面四周留的边：占画布短边 0–20%
        public double CornerRadius = 14;       // 圆角（点）0–40
        public double Shadow = 0.45;           // 阴影深浅 0–1
        public double ShadowBlur = 0.33;       // 阴影虚化 0–1
        public double CursorScale = 1.5;       // 鼠标大小 0.5–3 倍
        public double Smoothing = 0.7;         // 鼠标顺滑程度：0 = 不平滑，1 = 最顺
        public bool HideIdleCursor;            // 鼠标不动几秒后自动隐藏
        public double HideAfter = 2;           // 1–5 秒
        public bool ClickSound;                // 点击时加一声轻响
        public string ClickStyle = "soft";
        public bool Ripple = true;             // 点击波纹
        public string Blur = "light";          // 运动模糊：只在镜头快速移动时有

        public StudioLook Clone() => FromJson(ToJson());
        public bool Same(StudioLook o) => o != null && ToJson() == o.ToJson();

        public double? Ratio
        {
            get
            {
                switch (Aspect) { case "16:9": return 16.0 / 9; case "9:16": return 9.0 / 16; case "1:1": return 1; case "4:3": return 4.0 / 3; default: return null; }
            }
        }

        // 实际用哪张图当背景：选了「壁纸 / 图片」但文件找不到 → null（按白色画）
        public string BackgroundImage
        {
            get
            {
                string p = Background == "wallpaper" ? Wallpaper : Background == "image" ? ImagePath : null;
                return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
            }
        }

        public static readonly (string zh, string en, RGB a, RGB b)[] Gradients =
        {
            ("晨雾", "Dawn", new RGB(0.36, 0.40, 0.96), new RGB(0.86, 0.43, 0.78)),
            ("海盐", "Sea Salt", new RGB(0.56, 0.82, 0.98), new RGB(0.36, 0.48, 0.94)),
            ("蜜桃", "Peach", new RGB(1.00, 0.76, 0.62), new RGB(0.97, 0.46, 0.62)),
            ("薄荷", "Mint", new RGB(0.58, 0.91, 0.78), new RGB(0.24, 0.60, 0.86)),
            ("暖沙", "Sand", new RGB(0.99, 0.90, 0.74), new RGB(0.94, 0.66, 0.50)),
            ("夜色", "Night", new RGB(0.14, 0.15, 0.24), new RGB(0.36, 0.28, 0.54)),
        };

        // Windows 自带的壁纸（C:\Windows\Web\Wallpaper 下各主题文件夹里的 jpg / png）
        public static List<string> SystemWallpapers()
        {
            var list = new List<string>();
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");
                if (Directory.Exists(root))
                    list = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                        .Where(f => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { }
            return list;
        }

        // ---- JSON（键名同 Mac 版，按字母排） ----
        public string ToJson()
        {
            var w = new JsonW();
            w.Begin();
            w.Str("aspect", Aspect); w.Str("background", Background);
            w.Str("blur", Blur); w.Bool("clickSound", ClickSound); w.Str("clickStyle", ClickStyle);
            w.Rgb("color", Color); w.Num("cornerRadius", CornerRadius); w.Num("cursorScale", CursorScale);
            w.Rgb("gradient0", Gradient0); w.Rgb("gradient1", Gradient1); w.Num("gradientAngle", GradientAngle);
            w.Num("hideAfter", HideAfter); w.Bool("hideIdleCursor", HideIdleCursor);
            if (ImageBookmark != null) w.Str("imageBookmark", ImageBookmark);
            w.Str("imagePath", ImagePath); w.Num("padding", Padding); w.Bool("ripple", Ripple);
            w.Num("shadow", Shadow); w.Num("shadowBlur", ShadowBlur); w.Num("smoothing", Smoothing); w.Str("wallpaper", Wallpaper);
            w.End();
            return w.ToString();
        }

        public static StudioLook FromJson(string json) => From(Json.Parse(json) as Dictionary<string, object>);

        public static StudioLook From(Dictionary<string, object> o)
        {
            var z = new StudioLook();
            if (o == null) return z;
            var j = new JsonR(o);
            z.Aspect = j.Str("aspect", z.Aspect, Aspects);
            var bg = j.Str("background", "color");
            z.Background = bg == "transparent" ? "color" : Backgrounds.Contains(bg) ? bg : "color";
            // Mac 的透明背景：Windows 上按白色画
            z.Color = bg == "transparent" ? RGB.White : j.Rgb("color", z.Color);
            z.Gradient0 = j.Rgb("gradient0", z.Gradient0); z.Gradient1 = j.Rgb("gradient1", z.Gradient1);
            z.GradientAngle = j.Num("gradientAngle", z.GradientAngle);
            z.Wallpaper = j.Str("wallpaper", z.Wallpaper); z.ImagePath = j.Str("imagePath", z.ImagePath);
            z.ImageBookmark = j.Has("imageBookmark") ? j.Str("imageBookmark", null) : null;
            z.Padding = j.Num("padding", z.Padding); z.CornerRadius = j.Num("cornerRadius", z.CornerRadius);
            z.Shadow = j.Num("shadow", z.Shadow); z.ShadowBlur = j.Num("shadowBlur", z.ShadowBlur);
            z.CursorScale = j.Num("cursorScale", z.CursorScale); z.Smoothing = j.Num("smoothing", z.Smoothing);
            z.HideIdleCursor = j.Bool("hideIdleCursor", z.HideIdleCursor); z.HideAfter = j.Num("hideAfter", z.HideAfter);
            z.ClickSound = j.Bool("clickSound", z.ClickSound); z.ClickStyle = j.Str("clickStyle", z.ClickStyle, Clicks);
            z.Ripple = j.Bool("ripple", z.Ripple); z.Blur = j.Str("blur", z.Blur, Blurs);
            return z;
        }
    }

    // ---- 推近段：第一次按点击 / 打字 / 拖动自动生成，剪辑窗口里可以改 ----
    sealed class ZoomSegment
    {
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public double Start, End;
        public double Zoom = 2.0;
        public bool Fixed;                     // false = 跟着鼠标走，true = 盯住固定位置
        public double FixedX = 0.5, FixedY = 0.5;   // 固定位置：在原画面里的比例，左上角为原点

        public ZoomSegment Clone() => (ZoomSegment)MemberwiseClone();
    }

    // 工程文件 project.json（存在素材夹里），格式同 Mac 版
    sealed class StudioProject
    {
        public List<ZoomSegment> Segments = new List<ZoomSegment>();
        public double TrimStart, TrimEnd;
        public StudioLook Look = new StudioLook();
        public bool SegmentsEdited;            // 推近段手动改过：以后自动规则变了也不重算；没改过的打开时按最新规则重排

        public StudioProject Clone() => FromJson(ToJson());
        public bool Same(StudioProject o) => o != null && ToJson() == o.ToJson();
        public bool SameSegments(StudioProject o) => SegJson() == o.SegJson();
        string SegJson() { var w = new JsonW(); w.BeginArray("s"); foreach (var s in Segments) WriteSeg(w, s); w.EndArray(); return w.ToString(); }

        public static string PathIn(string dir) => System.IO.Path.Combine(dir, "project.json");

        public string ToJson()
        {
            var w = new JsonW(pretty: true);
            w.Begin();
            w.Raw("look", Look.ToJson());
            w.BeginArray("segments"); foreach (var s in Segments) WriteSeg(w, s); w.EndArray();
            w.Bool("segmentsEdited", SegmentsEdited);
            w.Num("trimEnd", TrimEnd); w.Num("trimStart", TrimStart);
            w.End();
            return w.ToString();
        }

        static void WriteSeg(JsonW w, ZoomSegment s)
        {
            w.BeginItem();
            w.Num("end", s.End); w.Num("fixedX", s.FixedX); w.Num("fixedY", s.FixedY); w.Str("id", s.Id);
            w.Str("mode", s.Fixed ? "fixed" : "follow"); w.Num("start", s.Start); w.Num("zoom", s.Zoom);
            w.End();
        }

        public static StudioProject FromJson(string json)
        {
            var o = Json.Parse(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var j = new JsonR(o);
            var p = new StudioProject
            {
                TrimStart = j.Num("trimStart", 0), TrimEnd = j.Num("trimEnd", 0), SegmentsEdited = j.Bool("segmentsEdited", false),
                Look = StudioLook.From(o.TryGetValue("look", out var l) ? l as Dictionary<string, object> : null),
            };
            if (o.TryGetValue("segments", out var sv) && sv is List<object> segs)
                foreach (var x in segs.OfType<Dictionary<string, object>>())
                {
                    var r = new JsonR(x);
                    p.Segments.Add(new ZoomSegment
                    {
                        Id = r.Str("id", Guid.NewGuid().ToString().ToUpperInvariant()), Start = r.Num("start", 0), End = r.Num("end", 0), Zoom = r.Num("zoom", 2),
                        Fixed = r.Str("mode", "follow") == "fixed", FixedX = r.Num("fixedX", 0.5), FixedY = r.Num("fixedY", 0.5),
                    });
                }
            return p;
        }

        // 有 project.json 就读（没手动改过推近段的按最新规则重排），没有就按自动规则生成一份；新工程用默认预设
        public static StudioProject Load(string dir, Track track, double duration, double zoom = 2.0, StudioLook defaultLook = null)
        {
            var file = PathIn(dir);
            if (File.Exists(file))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var p = FromJson(text);
                    var o = Json.Parse(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    p.TrimStart = Math.Min(Math.Max(0, p.TrimStart), duration);
                    p.TrimEnd = p.TrimEnd > p.TrimStart ? Math.Min(p.TrimEnd, duration) : duration;
                    if (!o.ContainsKey("trimEnd")) p.TrimEnd = duration;
                    p.Segments = p.Segments.OrderBy(s => s.Start).ToList();
                    if (!o.ContainsKey("segments") || !p.SegmentsEdited) p.Segments = StudioPlan.AutoSegments(track, duration, zoom);
                    return p;
                }
                catch { }
            }
            return new StudioProject { Segments = StudioPlan.AutoSegments(track, duration, zoom), TrimStart = 0, TrimEnd = duration, Look = (defaultLook ?? new StudioLook()).Clone() };
        }

        public void Save(string dir)
        {
            var file = PathIn(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
        }
    }

    // ---- 预设：把一套样式存起来，下拉切换；「默认」用在新录的工程上（%APPDATA%\录屏\presets.json，格式同 Mac 版） ----
    sealed class StudioPreset
    {
        public string Name;                    // 内置预设以「@」开头（固定、不随语言变，存默认时用它）
        public StudioLook Look;
        public bool BuiltIn => Name.StartsWith("@");
        public string Title(Func<string, string, string> L)
        {
            switch (Name)
            {
                case "@white": return L("白色背景", "White");
                case "@douyin": return L("抖音竖屏", "Vertical (TikTok)");
                case "@tutorial": return L("教程横屏", "Tutorial (16:9)");
                case "@square": return L("方形", "Square");
                default: return Name;
            }
        }

        // 内置预设：只读，不写进 presets.json（Windows 的第一个是白色背景；Mac 那边是透明背景）
        public static readonly StudioPreset[] BuiltIns = MakeBuiltIns();
        static StudioPreset[] MakeBuiltIns()
        {
            var g = StudioLook.Gradients;
            var tall = new StudioLook { Aspect = "9:16", Background = "gradient", Gradient0 = g[2].a, Gradient1 = g[2].b, GradientAngle = 160, CursorScale = 1.8, Padding = 0.05 };
            var wide = new StudioLook { Aspect = "16:9", Background = "gradient", Gradient0 = g[1].a, Gradient1 = g[1].b, GradientAngle = 135 };
            var square = new StudioLook { Aspect = "1:1", Background = "gradient", Gradient0 = g[3].a, Gradient1 = g[3].b, GradientAngle = 135 };
            return new[]
            {
                new StudioPreset { Name = "@white", Look = new StudioLook() }, new StudioPreset { Name = "@douyin", Look = tall },
                new StudioPreset { Name = "@tutorial", Look = wide }, new StudioPreset { Name = "@square", Look = square },
            };
        }
    }

    sealed class PresetStore
    {
        public List<StudioPreset> Presets = new List<StudioPreset>();   // 自己存的
        public string DefaultName;

        public IEnumerable<StudioPreset> All => StudioPreset.BuiltIns.Concat(Presets);
        public StudioPreset Find(string name) => All.FirstOrDefault(p => p.Name == name);
        // 新录的工程用哪个：没设过（或设的那个被删了）= 白色背景
        public string DefaultKey => DefaultName != null && Find(DefaultName) != null ? DefaultName : "@white";
        public StudioLook DefaultLook => Find(DefaultKey)?.Look.Clone() ?? new StudioLook();
        // 当前样式对得上哪个预设：自己存的优先
        public string Match(StudioLook look) => Presets.Concat(StudioPreset.BuiltIns).FirstOrDefault(p => p.Look.Same(look))?.Name;

        // 自测可以改路径
        public static string File_ = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "录屏", "presets.json");

        public static PresetStore Load()
        {
            var s = new PresetStore();
            try
            {
                if (!File.Exists(File_)) return s;
                var o = Json.Parse(File.ReadAllText(File_)) as Dictionary<string, object>;
                if (o == null) return s;
                if (o.TryGetValue("defaultName", out var d) && d is string ds) s.DefaultName = ds;
                if (o.TryGetValue("presets", out var pv) && pv is List<object> list)
                    foreach (var x in list.OfType<Dictionary<string, object>>())
                        if (x.TryGetValue("name", out var n) && n is string ns && !ns.StartsWith("@"))
                            s.Presets.Add(new StudioPreset { Name = ns, Look = StudioLook.From(x.TryGetValue("look", out var l) ? l as Dictionary<string, object> : null) });
            }
            catch { }
            return s;
        }

        public void Save()
        {
            var w = new JsonW(pretty: true);
            w.Begin();
            if (DefaultName != null) w.Str("defaultName", DefaultName);
            w.BeginArray("presets");
            foreach (var p in Presets.Where(p => !p.BuiltIn)) { w.BeginItem(); w.Raw("look", p.Look.ToJson()); w.Str("name", p.Name); w.End(); }   // 内置的不写进文件
            w.EndArray();
            w.End();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File_));
            File.WriteAllText(File_, w.ToString(), new UTF8Encoding(false));
        }

        public void SavePreset(string name, StudioLook look)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.StartsWith("@")) return;
            var p = Presets.FirstOrDefault(x => x.Name == name);
            if (p != null) p.Look = look.Clone(); else Presets.Add(new StudioPreset { Name = name, Look = look.Clone() });
            Save();
        }

        public void Delete(string name)
        {
            if (name == null || name.StartsWith("@")) return;   // 内置的删不掉
            Presets.RemoveAll(p => p.Name == name);
            if (DefaultName == name) DefaultName = null;
            Save();
        }

        public void SetDefault(string name) { DefaultName = name == "@white" ? null : name; Save(); }
    }

    // ---- 时间轴坐标换算（纯函数，好单独测） ----
    static class TimelineMath
    {
        public static double Time(double px, double w, double d) => Math.Min(Math.Max(px / w * d, 0), d);
        // 拖修剪把手：按下时的值 + 拖动距离换成的时间，夹在合法范围里（不会越拖越歪）
        public static double Trim(bool isStart, double baseS, double baseE, double dx, double w, double d)
        {
            double dt = dx / w * d;
            return isStart ? Math.Min(Math.Max(0, baseS + dt), baseE - 0.5) : Math.Max(Math.Min(d, baseE + dt), baseS + 0.5);
        }
        // 时间轴视窗：放大 scale（1–8）、offset = 内容滚出左边的像素、view = 可见宽度
        public const double MinScale = 1, MaxScale = 8;
        public static double ClampOffset(double o, double s, double w) => Math.Min(Math.Max(o, 0), Math.Max(0, w * s - w));
        // 以可见区里 anchorX 处为锚点缩放：锚点下的时间在缩放前后屏幕位置不变（贴边时被夹住除外）
        public static (double scale, double offset) Zoom(double s, double o, double w, double ax, double newScale)
        {
            double ns = Math.Min(Math.Max(newScale, MinScale), MaxScale);
            double f = (o + ax) / (w * s);
            return (ns, ClampOffset(f * w * ns - ax, ns, w));
        }
        // 滚轮一格（120）换成缩放倍率；精确触控板给的是小数
        public static double WheelFactor(double notches) => Math.Pow(1.12, notches);
    }

    // 拖动期间的 seek 排队（纯逻辑）：同时只有一个 seek 在路上，新的目标只记最新一个；
    // 拖动中或 seek 还没做完时，播放器报来的时间（还是旧位置）一律不采用，播放头就不会被拉回去抖
    sealed class Scrubber
    {
        public bool Active { get; private set; }
        public bool InFlight { get; private set; }
        double? pending;
        public void Begin() => Active = true;
        public void End() => Active = false;
        // 返回现在就该发出的 seek；null = 有一个在路上，已记下等它做完
        public double? Request(double t) { if (InFlight) { pending = t; return null; } InFlight = true; return t; }
        // 一个 seek 做完：有更新的目标就接着发它
        public double? SeekDone() { if (pending is double p) { pending = null; return p; } InFlight = false; return null; }
        // 播放器报来的时间能不能拿来更新播放头
        public bool Observed() => !Active && !InFlight;
    }

    // ---- 点击音效：程序合成的几种短声（衰减正弦 + 一点噪声，几十毫秒），公式同 Mac 版 ----
    static class ClickSound
    {
        public static float[] Wave(string style, int sampleRate = 48000)
        {
            var rnd = new Random(7);
            double Noise() => rnd.NextDouble() * 2 - 1;
            double Burst(double t, double decay, double f, double amp) => amp * Math.Exp(-t / decay) * (0.6 * Math.Sin(2 * Math.PI * f * t) + 0.4 * Noise());
            int n; Func<double, double> fn;
            switch (style)
            {
                case "mouse":   // 鼠标咔哒：按下 + 60 毫秒后松开
                    n = 4800; fn = t => (t < 0.02 ? Burst(t, 0.0018, 2600, 0.45) : 0) + (t >= 0.06 && t < 0.08 ? Burst(t - 0.06, 0.0015, 3200, 0.25) : 0); break;
                case "thock":   // 低沉的「咚」
                    n = 4800; fn = t => Math.Exp(-t / 0.012) * (0.6 * Math.Sin(2 * Math.PI * 190 * t) + 0.3 * Math.Sin(2 * Math.PI * 620 * t)) * 0.5 + Burst(t, 0.002, 1800, 0.12); break;
                case "wood":    // 木头（像木鱼）
                    n = 5760; fn = t => Math.Exp(-t / 0.018) * (0.6 * Math.Sin(2 * Math.PI * 880 * t) + 0.25 * Math.Sin(2 * Math.PI * 2350 * t)) * 0.42; break;
                case "pop":     // 气泡「啵」：音高往上扬
                    n = 2160; fn = t => { double e = Math.Pow(Math.Sin(Math.PI * t / 0.045), 2); return 0.4 * e * Math.Sin(2 * Math.PI * (500 + 1200 * t / 0.045) * t); }; break;
                case "tick":    // 清脆的「嘀」
                    n = 1440; fn = t => Math.Exp(-t / 0.0025) * (0.7 * Math.Sin(2 * Math.PI * 5200 * t) + 0.3 * Math.Sin(2 * Math.PI * 7400 * t)) * 0.3; break;
                default:        // 轻点：短促的「嗒」
                    n = 1920; fn = t => Math.Exp(-t / 0.0045) * (0.8 * (0.65 * Math.Sin(2 * Math.PI * 3100 * t) + 0.35 * Math.Sin(2 * Math.PI * 1700 * t)) + 0.2 * Noise()) * 0.32; break;
            }
            n = n * sampleRate / 48000;
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)fn(i / (double)sampleRate);
            return w;
        }

        // 整条点击声（单声道 48k、16 位 WAV）：times 是相对开头的秒数，length 秒长
        public static byte[] Track(IEnumerable<double> times, double length, string style)
        {
            const int sr = 48000;
            int n = Math.Max(1, (int)Math.Ceiling(length * sr));
            var buf = new float[n];
            var wave = Wave(style);
            foreach (var t in times)
            {
                int s = (int)Math.Round(t * sr);
                for (int k = 0; k < wave.Length; k++) { int j = s + k; if (j >= 0 && j < n) buf[j] += wave[k]; }
            }
            return Wav(buf, sr);
        }

        public static byte[] Wav(float[] samples, int sr = 48000)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + samples.Length * 2); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(samples.Length * 2);
                foreach (var v in samples) bw.Write((short)Math.Round(Math.Max(-1, Math.Min(1, v)) * 32767));
                bw.Flush();
                return ms.ToArray();
            }
        }

        // 试听用：一声
        public static byte[] One(string style) => Wav(Wave(style));
    }

    // ---- 小 JSON 读写帮手 ----
    sealed class JsonR
    {
        readonly Dictionary<string, object> o;
        public JsonR(Dictionary<string, object> o) { this.o = o ?? new Dictionary<string, object>(); }
        public bool Has(string k) => o.ContainsKey(k) && o[k] != null;
        public double Num(string k, double def) => o.TryGetValue(k, out var v) && v is double d && !double.IsNaN(d) ? d : def;
        public bool Bool(string k, bool def) => o.TryGetValue(k, out var v) && v is bool b ? b : def;
        public string Str(string k, string def, string[] allowed = null) => o.TryGetValue(k, out var v) && v is string s && (allowed == null || allowed.Contains(s)) ? s : def;
        public RGB Rgb(string k, RGB def)
        {
            if (!(o.TryGetValue(k, out var v) && v is Dictionary<string, object> c)) return def;
            var j = new JsonR(c);
            return new RGB(j.Num("r", def.R), j.Num("g", def.G), j.Num("b", def.B));
        }
    }

    sealed class JsonW
    {
        readonly StringBuilder sb = new StringBuilder();
        readonly bool pretty;
        readonly Stack<bool> first = new Stack<bool>();
        public JsonW(bool pretty = false) { this.pretty = pretty; }
        public override string ToString() => sb.ToString();

        void Indent() { if (pretty) sb.Append('\n').Append(' ', first.Count * 2); }
        void Key(string k)
        {
            if (!first.Peek()) sb.Append(',');
            first.Pop(); first.Push(false);
            Indent();
            Quote(k); sb.Append(pretty ? " : " : ":");
        }
        void Quote(string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
        public void Begin() { sb.Append('{'); first.Push(true); }
        public void End() { bool empty = first.Pop(); if (!empty) Indent(); sb.Append('}'); }
        public void BeginArray(string k) { Key(k); sb.Append('['); first.Push(true); }
        public void EndArray() { bool empty = first.Pop(); if (!empty) Indent(); sb.Append(']'); }
        public void BeginItem() { if (!first.Peek()) sb.Append(','); first.Pop(); first.Push(false); Indent(); Begin(); }
        public void Str(string k, string v) { Key(k); Quote(v); }
        public void Num(string k, double v) { Key(k); sb.Append(N(v)); }
        public void Bool(string k, bool v) { Key(k); sb.Append(v ? "true" : "false"); }
        public void Raw(string k, string json) { Key(k); sb.Append(json); }
        public void Rgb(string k, RGB c) { Key(k); sb.Append("{\"b\":").Append(N(c.B)).Append(",\"g\":").Append(N(c.G)).Append(",\"r\":").Append(N(c.R)).Append('}'); }
        public static string N(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("R", CultureInfo.InvariantCulture);
    }
}
