// Studio 剪辑的自测（纯 C#，不碰界面）：Windows 程序里用环境变量触发（见 SelfTest.cs），Mac 上用 tools/StudioHarness 跑。
// 每条打印 PASS / FAIL，最后一行 xxx OK / xxx FAIL
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    static class StudioTests
    {
        sealed class Report
        {
            public readonly StringBuilder Text = new StringBuilder();
            public bool Ok = true;
            public void Check(string name, bool cond, string info = "") { Text.AppendLine((cond ? "PASS " : "FAIL ") + name + (info.Length > 0 ? "  " + info : "")); Ok &= cond; }
            public void Note(string s) => Text.AppendLine(s);
            public string Done(string what) { Text.AppendLine(what + (Ok ? " OK" : " FAIL")); return Text.ToString(); }
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string SegList(IEnumerable<ZoomSegment> s) => string.Join(" ", s.Select(x => F(x.Start) + "–" + F(x.End)));

        // 造一份假的轨迹：鼠标打字时停在 at，点击时在点击处
        static Track Fake(double dur, (double t, double x, double y)[] clicks, double[] keys, (double x, double y) at)
        {
            var t = new Track { Width = 2000, Height = 1200, PixelsPerPoint = 2 };
            var pos = at; int ci = 0;
            for (int i = 0; i <= (int)(dur * 30); i++)
            {
                double tt = i / 30.0;
                while (ci < clicks.Length && clicks[ci].t <= tt) { pos = (clicks[ci].x, clicks[ci].y); ci++; }
                t.Moves.Add(new[] { tt, pos.x, pos.y, 0, 0 });
            }
            t.Clicks = clicks.Select(c => new[] { c.t, c.x, c.y }).ToList(); t.Keys = keys.ToList();
            t.Cursors.Add(new CursorShape { File = "cursor-0.png", Arrow = true, W = 16, H = 16 });
            return t;
        }

        // ---- PLANTEST：自动分段规则 + 给定工程的镜头计划（段内倍数、段外拉远、固定位置、修剪帧数） ----
        // dir（可选）= 真素材夹：额外打印真素材的自动分段，并用它的轨迹检查
        public static string Plan(string dir = null)
        {
            var r = new Report();
            var st = new StudioStyle(); double fps = st.Fps;
            // 1) 同一处一直打字 6 秒 → 一段
            var typeKeys = Enumerable.Range(0, 40).Select(i => 1.2 + i * 0.15).ToArray();
            var typing = Fake(10, new[] { (1.0, 1000.0, 600.0) }, typeKeys, (1000, 600));
            var s1 = StudioPlan.AutoSegments(typing, 10);
            r.Check("same-place typing -> one segment", s1.Count == 1, SegList(s1));
            // 2) 屏幕左右两头来回点，每 3 秒一下 → 多段，中间回到全景
            var ping = Enumerable.Range(0, 6).Select(i => (1.0 + i * 3, i % 2 == 0 ? 150.0 : 1850.0, 600.0)).ToArray();
            var pingT = Fake(20, ping, new double[0], (150, 600));
            var s2 = StudioPlan.AutoSegments(pingT, 20);
            r.Check("ping-pong clicks -> several segments", s2.Count >= 4, SegList(s2));
            var pingLook = new StudioLook();
            var pingLay = new StudioLayout(pingT, pingLook);
            var pingPlan = StudioPlan.Make(pingT, pingLay, st, pingLook, s2, (int)(20 * fps), 20);
            double minBetween = 9;
            for (int k = 0; k + 1 < s2.Count; k++)
            {
                int a = (int)(s2[k].End * fps), b = Math.Min((int)(s2[k + 1].Start * fps), pingPlan.Length - 1);
                for (int i = a; i <= b; i++) minBetween = Math.Min(minBetween, pingPlan[i].z);
            }
            r.Check("ping-pong zooms out between (z ~ 1)", minBetween < 1.06, "min z between = " + F(minBetween));
            // 3) 同一处连续 20 秒不停地打字（每 4 秒里前 2 秒打、后 2 秒停）→ 被切开
            var busyKeys = new List<double>();
            for (double k = 0.5; k < 20.5; k += 0.3) if (k % 4 < 2) busyKeys.Add(k);
            var busy = Fake(22, new[] { (0.5, 1000.0, 600.0) }, busyKeys.ToArray(), (1000, 600));
            var s3 = StudioPlan.AutoSegments(busy, 22);
            r.Check("20 s continuous -> split", s3.Count >= 2 && s3.All(x => x.End - x.Start <= 12.5 + StudioPlan.HoldAfter), SegList(s3));

            // 4) 给定工程：跟随段 3 倍、固定段 2 倍对准 (0.2, 0.3)、修剪 1–13 秒
            var tr = dir != null ? Studio.LoadTrack(dir, null) : Fake(16, new[] { (1.5, 400.0, 300.0), (6.0, 1500.0, 900.0), (9.0, 900.0, 500.0) }, new double[0], (400, 300));
            double dur = dir != null ? Math.Max(14.5, tr.Moves.Count > 0 ? tr.Moves.Last()[0] : 16) : 16;
            var look = new StudioLook();
            var lay = new StudioLayout(tr, look);
            var p = new StudioProject
            {
                Segments = { new ZoomSegment { Start = 1, End = 5, Zoom = 3 }, new ZoomSegment { Start = 8, End = 12, Zoom = 2, Fixed = true, FixedX = 0.2, FixedY = 0.3 } },
                TrimStart = 1, TrimEnd = 13,
            };
            var plan = StudioPlan.Make(tr, lay, st, look, p.Segments, (int)(dur * fps), dur);
            FramePlan At(double t) => plan[Math.Min(plan.Length - 1, (int)(t * fps))];
            r.Check("zoom inside follow segment", Math.Abs(At(4.5).z - 3) < 0.05, "z=" + F(At(4.5).z));
            r.Check("zoomed out between segments", At(7.5).z < 1.08, "z=" + F(At(7.5).z));
            var tf = At(11.5);
            double hw = lay.cw / (2 * 2.0 * lay.s), hh = lay.ch / (2 * 2.0 * lay.s);
            double fx = Math.Min(Math.Max(0.2 * lay.W, hw), lay.W - hw), fy = Math.Min(Math.Max((1 - 0.3) * lay.H, hh), lay.H - hh);
            double wantX = lay.card.X + fx * lay.s, wantY = lay.card.Y + fy * lay.s;
            r.Check("fixed segment zoom", Math.Abs(tf.z - 2) < 0.05, "z=" + F(tf.z));
            r.Check("fixed segment aims at point", Math.Abs(tf.sx - wantX) < lay.cw * 0.01 && Math.Abs(tf.sy - wantY) < lay.ch * 0.01, $"got ({F(tf.sx)},{F(tf.sy)}) want ({F(wantX)},{F(wantY)})");
            r.Check("after last segment zoomed out", At(Math.Min(dur - 0.1, 14.5)).z < 1.08);
            var pt = StudioPlan.SourcePoint(lay, lay.cw / 2, lay.ch / 2, tf);
            r.Check("click -> source point", Math.Abs(pt.x - fx / lay.W) < 0.01 && Math.Abs(pt.y - (1 - fy / lay.H)) < 0.01, $"({F(pt.x)}, {F(pt.y)})");
            int frames = (int)Math.Round(p.TrimEnd * fps) - (int)Math.Round(p.TrimStart * fps);
            r.Check("trim frame count", frames == 720, frames.ToString());
            if (dir != null) r.Note($"REAL {F(dur)}s -> {StudioPlan.AutoSegments(tr, dur).Count} segs: {SegList(StudioPlan.AutoSegments(tr, dur))}");
            return r.Done("PLANTEST");
        }

        // ---- PROJTEST：和 Mac 版 project.json 互通 ----
        // macJson = Mac 写的 project.json 内容（可选）；检查 Windows 读得懂、写出来带齐 Mac 要的字段、读写往返不变
        public static string Project(string macJson = null)
        {
            var r = new Report();
            if (macJson != null)
            {
                var m = StudioProject.FromJson(macJson);
                var o = (Dictionary<string, object>)Json.Parse(macJson);
                var segs = (List<object>)o["segments"];
                r.Check("mac project: segment count", m.Segments.Count == segs.Count, m.Segments.Count.ToString());
                var s0 = (Dictionary<string, object>)segs[0];
                r.Check("mac project: first segment", Math.Abs(m.Segments[0].Start - (double)s0["start"]) < 1e-9 && m.Segments[0].Id == (string)s0["id"] && m.Segments[0].Fixed == ((string)s0["mode"] == "fixed"), m.Segments[0].Id);
                r.Check("mac project: trim", Math.Abs(m.TrimEnd - (double)o["trimEnd"]) < 1e-9);
                var lk = (Dictionary<string, object>)o["look"];
                r.Check("mac project: look fields", Math.Abs(m.Look.Padding - (double)lk["padding"]) < 1e-9 && m.Look.Aspect == (string)lk["aspect"] && m.Look.Blur == (string)lk["blur"], $"aspect={m.Look.Aspect} padding={m.Look.Padding}");
                if ((string)lk["background"] == "transparent") r.Check("mac transparent -> white on Windows", m.Look.Background == "color" && m.Look.Color.Same(RGB.White));
                // 往返：Windows 写出来再读，内容一样
                var again = StudioProject.FromJson(m.ToJson());
                r.Check("roundtrip mac project", again.Same(m));
            }
            // Windows 写的工程：带齐 Mac 版解码要的字段（ZoomSegment 每个字段都要有；RGB 要 r g b）
            var p = new StudioProject { TrimStart = 0.5, TrimEnd = 9.25, SegmentsEdited = true };
            p.Segments.Add(new ZoomSegment { Start = 1, End = 3.5, Zoom = 2.4 });
            p.Segments.Add(new ZoomSegment { Start = 5, End = 7, Fixed = true, FixedX = 0.25, FixedY = 0.75 });
            p.Look.Aspect = "9:16"; p.Look.Background = "gradient"; p.Look.ImagePath = @"C:\Users\a\Pictures\b ""x"".png"; p.Look.ClickSound = true; p.Look.ClickStyle = "wood";
            var json = p.ToJson();
            var po = (Dictionary<string, object>)Json.Parse(json);
            var seg = (Dictionary<string, object>)((List<object>)po["segments"])[0];
            r.Check("segment has every key Mac needs", new[] { "id", "start", "end", "zoom", "mode", "fixedX", "fixedY" }.All(seg.ContainsKey));
            r.Check("segment id is a UUID", Guid.TryParse((string)seg["id"], out _) && ((string)seg["id"]).ToUpperInvariant() == (string)seg["id"]);
            var lo = (Dictionary<string, object>)po["look"];
            r.Check("look color is {r,g,b}", ((Dictionary<string, object>)lo["color"]).Keys.OrderBy(k => k).SequenceEqual(new[] { "b", "g", "r" }));
            r.Check("look keys", new[] { "aspect", "background", "color", "gradient0", "gradient1", "gradientAngle", "wallpaper", "imagePath", "padding", "cornerRadius", "shadow", "shadowBlur", "cursorScale", "smoothing", "hideIdleCursor", "hideAfter", "clickSound", "clickStyle", "ripple", "blur" }.All(lo.ContainsKey));
            r.Check("windows roundtrip", StudioProject.FromJson(json).Same(p));
            r.Check("path with quotes/backslashes survives", StudioProject.FromJson(json).Look.ImagePath == p.Look.ImagePath);
            r.Note("windows project.json:\n" + json);
            return r.Done("PROJTEST");
        }

        // ---- SCRUBTEST：拖动逻辑（不碰界面） ----
        public static string Scrub()
        {
            var r = new Report();
            double w = 1200, d = 25;
            // 1) 往左拖结尾把手：一串越来越大的拖动距离 → 修剪点单调往前，不回跳；拖过头被夹住
            var drags = Enumerable.Range(0, 121).Select(i => -7.5 * i).ToArray();
            var ends = drags.Select(dx => TimelineMath.Trim(false, 0, 25, dx, w, d)).ToArray();
            r.Check("trimEnd monotonic", ends.Zip(ends.Skip(1), (a, b) => b <= a).All(x => x), F(ends.First()) + " -> " + F(ends.Last()));
            r.Check("trimEnd clamped", ends.Last() >= 0.5 && Math.Abs(ends.Last() - (25 - 900 / w * d)) < 1e-9);
            var starts = Enumerable.Range(0, 154).Select(i => TimelineMath.Trim(true, 0, 25, 13.0 * i, w, d)).ToArray();
            r.Check("trimStart monotonic + clamp", starts.Zip(starts.Skip(1), (a, b) => b >= a).All(x => x) && Math.Abs(starts.Last() - 24.5) < 1e-9);
            double aa = TimelineMath.Trim(false, 0, 25, -300, w, d), bb = TimelineMath.Trim(false, 0, 25, -300, w, d);
            r.Check("same dx same value", aa == bb && Math.Abs(aa - 18.75) < 1e-9);
            // 2) seek 排队 + 播放头：模拟拖动时界面要的位置 + 播放器慢吞吞地报旧时间
            var sc = new Scrubber(); double shown = 16, player = 16; var sent = new List<double>(); int jumpsBack = 0; double? inFlight = null;
            sc.Begin();
            for (int k = 0; k < 40; k++)
            {
                double t = TimelineMath.Trim(false, 0, 16, drags[k], w, d);
                double prev = shown; shown = t;
                if (sc.Request(t) is double go) { sent.Add(go); inFlight = go; }
                if (shown > prev + 1e-9) jumpsBack++;
                if (sc.Observed()) shown = player;
                if (k % 4 == 3 && inFlight is double f) { player = f; inFlight = null; if (sc.SeekDone() is double nx) { sent.Add(nx); inFlight = nx; } }
            }
            r.Check("playhead never pulled back while dragging", jumpsBack == 0);
            r.Check("stale player time ignored while dragging", !sc.Observed());
            r.Check("seeks coalesced", sent.Count < 20, sent.Count + " seeks for 40 drag steps");
            double lastT = TimelineMath.Trim(false, 0, 16, drags[39], w, d);
            while (inFlight is double f2) { player = f2; inFlight = null; if (sc.SeekDone() is double nx) { sent.Add(nx); inFlight = nx; } }
            r.Check("final seek lands on last target", Math.Abs(player - lastT) < 1e-9, F(player) + " vs " + F(lastT));
            sc.End();
            r.Check("player time accepted after drag", sc.Observed());
            // 3) 时间轴缩放：锚点下的时间缩放前后屏幕位置不变、边界夹住、滑块（以正中为锚点）一致
            double vw = 1000;
            foreach (var (s0, o0, ax, ns) in new[] { (1.0, 0.0, 300.0, 3.0), (3.0, 800.0, 120.0, 5.5), (5.5, 1500.0, 900.0, 2.0), (2.0, 400.0, 500.0, 2.0 * 1.006) })
            {
                var z = TimelineMath.Zoom(s0, o0, vw, ax, ns);
                double before = (o0 + ax) / (vw * s0) * d, after = (z.offset + ax) / (vw * z.scale) * d;
                bool clamped = z.offset == 0 || Math.Abs(z.offset - (vw * z.scale - vw)) < 1e-9;
                r.Check($"anchor stays put s={F(s0)}->{F(ns)} ax={F(ax)}", Math.Abs(before - after) < 1e-9 || clamped, F(before) + " -> " + F(after));
            }
            var big = TimelineMath.Zoom(2, 0, vw, 500, 50); var small = TimelineMath.Zoom(4, 2500, vw, 500, 0.2);
            r.Check("scale clamped to 1–8", big.scale == 8 && small.scale == 1 && small.offset == 0);
            r.Check("offset clamped", TimelineMath.ClampOffset(-50, 3, vw) == 0 && TimelineMath.ClampOffset(5000, 3, vw) == 2000);
            var edgeR = TimelineMath.Zoom(3, 2000, vw, 1000, 4);
            r.Check("right-edge anchor keeps end visible", Math.Abs(edgeR.offset - (vw * 4 - vw)) < 1e-9);
            var stp = (scale: 1.0, offset: 0.0);
            for (int i = 0; i < 200; i++) stp = TimelineMath.Zoom(stp.scale, stp.offset, vw, 700, stp.scale * 1.0055);
            for (int i = 0; i < 400; i++) stp = TimelineMath.Zoom(stp.scale, stp.offset, vw, 700, stp.scale / 1.0055);
            r.Check("pinch in then out returns to start", stp.scale == 1 && stp.offset == 0);
            r.Check("wheel factor direction", TimelineMath.WheelFactor(1) > 1 && TimelineMath.WheelFactor(-1) < 1);
            return r.Done("SCRUBTEST");
        }

        // ---- PRESETTEST：内置预设只读、存取、默认、删除（用临时文件，不动用户的） ----
        public static string Presets()
        {
            var r = new Report();
            var keep = PresetStore.File_;
            PresetStore.File_ = Path.Combine(Path.GetTempPath(), "presets-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var s = PresetStore.Load();
                r.Check("4 built-ins, first is white", StudioPreset.BuiltIns.Length == 4 && StudioPreset.BuiltIns[0].Name == "@white" && StudioPreset.BuiltIns[0].Look.Background == "color" && StudioPreset.BuiltIns[0].Look.Color.Same(RGB.White));
                r.Check("douyin is 9:16 gradient", s.Find("@douyin").Look.Aspect == "9:16" && s.Find("@douyin").Look.Background == "gradient");
                r.Check("default is white when unset", s.DefaultKey == "@white" && s.DefaultLook.Same(new StudioLook()));
                var look = new StudioLook { Aspect = "1:1", Padding = 0.12, CursorScale = 2.2 };
                s.SavePreset("我的", look);
                var s2 = PresetStore.Load();
                r.Check("saved preset reads back", s2.Find("我的")?.Look.Same(look) == true);
                r.Check("built-ins not written to file", !File.ReadAllText(PresetStore.File_).Contains("\"@"));
                r.Check("match finds my preset", s2.Match(look) == "我的");
                s2.SetDefault("我的");
                var s3 = PresetStore.Load();
                r.Check("default preset used for new projects", s3.DefaultKey == "我的" && s3.DefaultLook.Same(look));
                s3.SetDefault("@douyin");
                r.Check("built-in can be default", PresetStore.Load().DefaultKey == "@douyin");
                s3.Delete("@douyin");
                r.Check("built-in can't be deleted", PresetStore.Load().Find("@douyin") != null);
                var s4 = PresetStore.Load(); s4.SetDefault("我的"); s4.Delete("我的");
                var s5 = PresetStore.Load();
                r.Check("deleting the default falls back to white", s5.Find("我的") == null && s5.DefaultKey == "@white");
                r.Check("order: built-ins first", s5.All.First().Name == "@white");
            }
            finally { try { File.Delete(PresetStore.File_); } catch { } PresetStore.File_ = keep; }
            return r.Done("PRESETTEST");
        }

        // ---- LOOKTEST：样式真的画出来了（画布比例、背景颜色、边距、圆角）；不用素材，造一张假画面 ----
        public static string Looks(Func<string, int, int, byte[]> loadImage = null)
        {
            var r = new Report();
            var tr = Fake(4, new[] { (1.0, 1000.0, 600.0) }, new double[0], (1000, 600));
            int W = tr.Width, H = tr.Height;
            var src = new byte[W * H * 4];
            for (int i = 0; i < src.Length; i += 4) { src[i] = 40; src[i + 1] = 160; src[i + 2] = 60; src[i + 3] = 255; }   // 原画面：绿色
            var full = new FramePlan { z = 1, shape = -1, press = 1, alpha = 1 };
            (byte b, byte g, byte rr) Px(byte[] buf, int cw, int x, int y) { int i = (y * cw + x) * 4; return (buf[i], buf[i + 1], buf[i + 2]); }
            byte[] Draw(StudioLook look, out StudioLayout lay)
            {
                var sc = new StudioScene(null, tr, look, new StudioStyle(), null, loadImage);
                lay = sc.Lay;
                var f = full; f.sx = lay.cw / 2; f.sy = lay.ch / 2;
                var dst = new byte[(int)lay.cw * (int)lay.ch * 4];
                sc.Cpu.Compose(src, f, null, dst);
                return dst;
            }
            foreach (var a in new[] { "16:9", "9:16", "1:1", "4:3" })
            {
                var lk = new StudioLook { Aspect = a };
                var lay = new StudioLayout(tr, lk);
                r.Check("aspect " + a, Math.Abs(lay.cw / lay.ch - lk.Ratio.Value) < 0.01 && (int)lay.cw % 2 == 0 && (int)lay.ch % 2 == 0, $"{lay.cw}x{lay.ch}");
            }
            var red = Draw(new StudioLook { Background = "color", Color = new RGB(1, 0, 0), Shadow = 0 }, out var l1);
            var c = Px(red, (int)l1.cw, 2, 2);
            r.Check("solid color corner", c.rr == 255 && c.g == 0 && c.b == 0, $"{c.rr},{c.g},{c.b}");
            var mid = Px(red, (int)l1.cw, (int)l1.cw / 2, (int)l1.ch / 2);
            r.Check("picture in the middle", mid.g == 160 && mid.rr == 60, $"{mid.rr},{mid.g},{mid.b}");
            var grad = Draw(new StudioLook { Background = "gradient", Gradient0 = new RGB(1, 0, 0), Gradient1 = new RGB(0, 0, 1), GradientAngle = 0, Shadow = 0 }, out var l2);
            var gl = Px(grad, (int)l2.cw, 1, (int)l2.ch / 2); var gr = Px(grad, (int)l2.cw, (int)l2.cw - 2, (int)l2.ch / 2);
            r.Check("gradient 0° red left, blue right", gl.rr > 240 && gl.b < 15 && gr.b > 240 && gr.rr < 15, $"left {gl.rr},{gl.g},{gl.b} right {gr.rr},{gr.g},{gr.b}");
            var white = Draw(new StudioLook(), out var l3);
            var w0 = Px(white, (int)l3.cw, 2, 2);
            r.Check("default background is white", w0.rr == 255 && w0.g == 255 && w0.b == 255, $"{w0.rr},{w0.g},{w0.b}");
            // 阴影：卡片正下方比四角暗；关掉阴影就不暗
            var sh = Px(white, (int)l3.cw, (int)l3.cw / 2, (int)(l3.ch - l3.card.Y + 3));
            r.Check("shadow under the card", sh.rr < 250, sh.rr.ToString());
            var noSh = Draw(new StudioLook { Shadow = 0 }, out var l4);
            var ns = Px(noSh, (int)l4.cw, (int)l4.cw / 2, (int)(l4.ch - l4.card.Y + 3));
            r.Check("no shadow when 0", ns.rr == 255, ns.rr.ToString());
            // 边距：6% → 15%，卡片左边往里挪
            var lp1 = new StudioLayout(tr, new StudioLook { Padding = 0.06 }); var lp2 = new StudioLayout(tr, new StudioLook { Padding = 0.15 });
            r.Check("padding moves the card", lp2.card.X / lp2.cw > lp1.card.X / lp1.cw + 0.03, $"{F(lp1.card.X / lp1.cw)} -> {F(lp2.card.X / lp2.cw)}");
            // 圆角：卡片左上角那一点，圆角 0 时是画面，圆角 40 时是背景
            var sq = Draw(new StudioLook { CornerRadius = 0, Shadow = 0 }, out var l5);
            var rd = Draw(new StudioLook { CornerRadius = 40, Shadow = 0 }, out var l6);
            int cx = (int)l5.card.X + 2, cy = (int)(l5.ch - l5.card.Y - l5.card.H) + 2;
            var pSq = Px(sq, (int)l5.cw, cx, cy); var pRd = Px(rd, (int)l6.cw, cx, cy);
            r.Check("corner radius", pSq.g == 160 && pRd.g == 255, $"square {pSq.rr},{pSq.g} round {pRd.rr},{pRd.g}");
            // 图片背景（给了读图的方法才测）
            if (loadImage != null)
            {
                var img = Path.Combine(Path.GetTempPath(), "looktest-bg-" + Guid.NewGuid().ToString("N") + ".png");
                if (Studio.RunQuiet($"-v error -y -f lavfi -i color=c=0x22aa66:s=320x200 -frames:v 1 \"{img}\"", 30000))
                {
                    var pic = Draw(new StudioLook { Background = "image", ImagePath = img, Shadow = 0 }, out var l7);
                    var pc = Px(pic, (int)l7.cw, 2, 2);
                    r.Check("image background", Math.Abs(pc.rr - 0x22) < 6 && Math.Abs(pc.g - 0xaa) < 6 && Math.Abs(pc.b - 0x66) < 6, $"{pc.rr},{pc.g},{pc.b}");
                    try { File.Delete(img); } catch { }
                }
                else r.Note("skip image background (ffmpeg couldn't make a test image)");
                var missing = Draw(new StudioLook { Background = "image", ImagePath = "", Shadow = 0 }, out var l8);
                var pm = Px(missing, (int)l8.cw, 2, 2);
                r.Check("image not chosen -> white", pm.rr == 255 && pm.g == 255 && pm.b == 255);
            }
            // 点击音效：6 种都有声音、长度合理
            foreach (var cs in StudioLook.Clicks)
            {
                var wv = ClickSound.Wave(cs);
                r.Check("click sound " + cs, wv.Length > 1000 && wv.Length < 6000 && wv.Max(Math.Abs) > 0.1, $"{wv.Length} samples peak {F(wv.Max(Math.Abs))}");
            }
            return r.Done("LOOKTEST");
        }

        // ---- BLURTEST：同一份素材分别不开 / 开「强」运动模糊：镜头不动的帧必须逐像素一样，镜头动得最快的帧要有拖影 ----
        public static string Blur(string dir)
        {
            var r = new Report();
            string raw = Path.Combine(dir, "raw.mp4");
            var info = Studio.Probe(raw);
            var tr = Studio.LoadTrack(dir, info);
            var proj = StudioProject.Load(dir, tr, info.Duration);
            var off = proj.Look.Clone(); off.Blur = "off";
            var strong = proj.Look.Clone(); strong.Blur = "strong";
            var st = new StudioStyle();
            var a = new StudioScene(dir, tr, off, st); var b = new StudioScene(dir, tr, strong, st);
            var plan = a.Plan(proj.Segments, info.Duration);
            // 镜头最快的一帧、完全不动的一帧
            int fast = -1, still = -1; double best = 0;
            for (int i = 1; i + 1 < plan.Length; i++)
            {
                double mv = Math.Abs(plan[i + 1].sx - plan[i - 1].sx) + Math.Abs(plan[i + 1].sy - plan[i - 1].sy) + Math.Abs(Math.Log(plan[i + 1].z / plan[i - 1].z)) * 1000;
                if (mv > best) { best = mv; fast = i; }
                if (still < 0 && mv < 0.05 && i > 10) still = i;
            }
            r.Note($"frames {plan.Length} fastest #{fast} (move {F(best)}) still #{still}");
            int W = info.Width, H = info.Height, CW = (int)a.Lay.cw, CH = (int)a.Lay.ch;
            foreach (var (name, i, wantSame) in new[] { ("still", still, true), ("fast", fast, false) })
            {
                if (i < 0) { r.Check(name + " frame found", false); continue; }
                var src = Studio.DecodeFrame(raw, i / (double)st.Fps, W, H, info.Matrix);
                if (src == null) { r.Check(name + " decode", false); continue; }
                var da = new byte[CW * CH * 4]; var db = new byte[CW * CH * 4];
                bool ba = a.Frame(a.Cpu, src, i, plan, da), bb = b.Frame(b.Cpu, src, i, plan, db);
                int maxd = 0; long sum = 0, changed = 0;
                for (int k = 0; k < da.Length; k++) { int dd = Math.Abs(da[k] - db[k]); if (dd > maxd) maxd = dd; sum += dd; if (dd > 10) changed++; }
                double mean = sum / (double)da.Length, ratio = changed / (double)da.Length;
                if (wantSame) r.Check("still frame identical with blur on", !bb && maxd == 0, $"max diff {maxd}");
                // 画面多半是白纸、平均差很小：看「变了 10 级以上的像素」占比和最大差
                else r.Check("fast frame has streak with blur on", bb && ratio > 0.001 && maxd > 40, $"blurred={bb} changed {F(ratio * 100)}% mean diff {F(mean)} max {maxd}");
            }
            return r.Done("BLURTEST");
        }
    }
}
