// W3 的核心自测（不碰界面，Mac 上的 .NET 8 小工具和 Windows 程序共用）：
//   Rules()  快捷键过滤、track.json / project.json 的快捷键、摄像头、布局（含 Mac 写的格式）、键帽出现时间、摄像头几何和过渡
//   Export() 真导出：键帽出现的时间和位置、普通打字不出、剪掉段里不出；摄像头小窗位置 / 形状 / 边框、推近时不放大、
//            各布局和过渡、剪掉 / 快放后摄像头和屏幕同步（摄像头画面亮度 = 秒 × 20，读回来就知道取的是哪一秒）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    static class StudioTestsW3
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
        static bool Near(double a, double b, double e = 1e-6) => Math.Abs(a - b) < e;
        static string J(string[] k) => k == null ? "null" : string.Join("+", k);

        public static string Rules()
        {
            var r = new Report();
            // 1) 记哪些键
            r.Check("Ctrl+C recorded", J(KeyLabel.Label(0x43, true, false, false, false)) == "Ctrl+C");
            r.Check("Win+Shift+S recorded in Win/Ctrl/Alt/Shift order", J(KeyLabel.Label(0x53, false, false, true, true)) == "Win+Shift+S");
            r.Check("Ctrl+Alt+Delete", J(KeyLabel.Label(0x2E, true, true, false, false)) == "Ctrl+Alt+Delete");
            r.Check("plain typing (A, Shift+A, 5, space) not recorded",
                KeyLabel.Label(0x41, false, false, false, false) == null && KeyLabel.Label(0x41, false, false, true, false) == null &&
                KeyLabel.Label(0x35, false, false, false, false) == null && KeyLabel.Label(0x20, false, false, false, false) == null);
            r.Check("modifier key alone not recorded", KeyLabel.Label(0x11, true, false, false, false) == null && KeyLabel.Label(0xA0, false, false, true, false) == null && KeyLabel.Label(0x5B, false, false, false, true) == null);
            r.Check("function keys alone recorded (Esc, Tab, Enter, Backspace, Delete, arrows, F5)",
                J(KeyLabel.Label(0x1B, false, false, false, false)) == "Esc" && J(KeyLabel.Label(0x09, false, false, false, false)) == "Tab" &&
                J(KeyLabel.Label(0x0D, false, false, false, false)) == "Enter" && J(KeyLabel.Label(0x08, false, false, false, false)) == "Backspace" &&
                J(KeyLabel.Label(0x2E, false, false, false, false)) == "Delete" && J(KeyLabel.Label(0x25, false, false, false, false)) == "←" &&
                J(KeyLabel.Label(0x74, false, false, false, false)) == "F5");
            r.Check("Ctrl+Shift+Z, Alt+Tab, Ctrl+/", J(KeyLabel.Label(0x5A, true, false, true, false)) == "Ctrl+Shift+Z" && J(KeyLabel.Label(0x09, false, true, false, false)) == "Alt+Tab" && J(KeyLabel.Label(0xBF, true, false, false, false)) == "Ctrl+/");

            // 2) track.json：快捷键、摄像头，存了再读一样；Mac 写的（⌘ 符号）也读得进
            var t = new Track { Width = 640, Height = 360, Shortcuts = new List<KeyStroke> { new KeyStroke { T = 1.25, Keys = new[] { "Ctrl", "C" } } }, Camera = new CameraTrack { Offset = 0.5, X = 0.8, Y = 0.7, Size = 0.3, Round = false } };
            var t2 = Track.FromJson(t.ToJson());
            r.Check("track.json round-trip (shortcuts + camera)", t2.Shortcuts?.Count == 1 && J(t2.Shortcuts[0].Keys) == "Ctrl+C" && Near(t2.Shortcuts[0].T, 1.25) &&
                    t2.Camera != null && Near(t2.Camera.Offset, 0.5) && Near(t2.Camera.X, 0.8) && Near(t2.Camera.Size, 0.3) && !t2.Camera.Round && t2.Camera.File == "camera.mp4");
            var mac = Track.FromJson("{\"width\":100,\"height\":50,\"pixelsPerPoint\":2,\"moves\":[],\"clicks\":[],\"keys\":[],\"cursors\":[],\"shortcuts\":[{\"t\":2.5,\"keys\":[\"⌘\",\"⇧\",\"4\"]}],\"camera\":{\"file\":\"camera.mp4\",\"offset\":0.25,\"x\":0.86,\"y\":0.8,\"size\":0.24,\"round\":true}}");
            r.Check("Mac track.json (⌘ ⇧ 4, camera) readable", mac.Shortcuts?.Count == 1 && J(mac.Shortcuts[0].Keys) == "⌘+⇧+4" && mac.Camera != null && Near(mac.Camera.Offset, 0.25));
            r.Check("old track.json without shortcuts / camera", Track.FromJson("{\"width\":10,\"height\":10}").Shortcuts == null && Track.FromJson("{\"width\":10,\"height\":10}").Camera == null);

            // 3) project.json：键帽样式、摄像头样式、布局；Mac 格式（布局 id 是 UUID）
            var p = new StudioProject { TrimStart = 0, TrimEnd = 20 };
            p.Look.ShowKeys = true; p.Look.KeysAt = "bottomRight"; p.Look.KeysSize = 0.06; p.Look.KeysDark = false;
            p.Camera = new CameraSettings { Size = 0.3, X = 0.2, Y = 0.25, Border = 6, BorderColor = new RGB(1, 0, 0), Shadow = 0.3, Mirror = false, Round = false };
            p.Layouts.Add(new CameraLayoutSeg { Start = 2, End = 4, Mode = "split" });
            var p2 = StudioProject.FromJson(p.ToJson());
            r.Check("project round-trip: keycap look", p2.Look.ShowKeys && p2.Look.KeysAt == "bottomRight" && Near(p2.Look.KeysSize, 0.06) && !p2.Look.KeysDark);
            r.Check("project round-trip: camera + layouts", p2.Camera != null && Near(p2.Camera.Size, 0.3) && Near(p2.Camera.X, 0.2) && p2.Camera.BorderColor.Same(new RGB(1, 0, 0)) && !p2.Camera.Mirror && !p2.Camera.Round &&
                    p2.Layouts.Count == 1 && p2.Layouts[0].Mode == "split" && Near(p2.Layouts[0].Start, 2) && p2.Layouts[0].Id == p.Layouts[0].Id);
            r.Check("project: layouts / camera no longer kept as unknown extras", !p2.Extra.ContainsKey("camera") && !p2.Extra.ContainsKey("layouts"));
            var macp = StudioProject.FromJson("{\"look\":{\"showKeys\":true,\"keysAt\":\"bottomLeft\",\"keysSize\":0.05,\"keysDark\":true,\"futureLookKey\":\"fast\"},\"camera\":{\"show\":true,\"round\":true,\"size\":0.24,\"x\":0.86,\"y\":0.8,\"border\":4,\"borderColor\":{\"r\":1,\"g\":1,\"b\":1},\"shadow\":0.5,\"mirror\":true},\"layouts\":[{\"id\":\"8E1D2C5A-1111-2222-3333-444455556666\",\"start\":1,\"end\":3,\"mode\":\"splitSwap\"}],\"segments\":[],\"trimStart\":0,\"trimEnd\":10}");
            r.Check("Mac project.json: keycaps / camera / layouts read", macp.Look.ShowKeys && macp.Look.KeysAt == "bottomLeft" && macp.Camera != null && macp.Layouts.Count == 1 && macp.Layouts[0].Mode == "splitSwap" &&
                    macp.Layouts[0].Id == "8E1D2C5A-1111-2222-3333-444455556666" && macp.Look.Extra.ContainsKey("futureLookKey"));
            var back = StudioProject.FromJson(macp.ToJson());
            r.Check("Mac project re-saved keeps everything", back.Layouts.Count == 1 && back.Layouts[0].Id == macp.Layouts[0].Id && back.Look.Extra.ContainsKey("futureLookKey") && back.Camera != null);
            var tr = new Track { Width = 100, Height = 100, Camera = new CameraTrack { X = 0.1, Y = 0.2, Size = 0.15, Round = false } };
            var dir = Path.Combine(Path.GetTempPath(), "w3-load-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var lp = StudioProject.Load(dir, tr, 10);
            r.Check("new project gets camera style from the recorded bubble", lp.Camera != null && Near(lp.Camera.X, 0.1) && Near(lp.Camera.Y, 0.2) && Near(lp.Camera.Size, 0.15) && !lp.Camera.Round);
            try { Directory.Delete(dir, true); } catch { }

            // 4) 键帽出现：1.2 秒后消失、下一下一按就换；剪掉段里的不出；修剪范围外不出
            var strokes = new List<KeyStroke> { new KeyStroke { T = 1, Keys = new[] { "Ctrl", "C" } }, new KeyStroke { T = 1.5, Keys = new[] { "Ctrl", "V" } }, new KeyStroke { T = 5, Keys = new[] { "Esc" } }, new KeyStroke { T = 8, Keys = new[] { "Tab" } }, new KeyStroke { T = 12, Keys = new[] { "F1" } } };
            var kp = new StudioProject { TrimStart = 0, TrimEnd = 11 }; kp.Cuts.Add(new CutSegment { Start = 7.5, End = 8.5 });
            var map = new TimeMap(kp);
            var kl = new KeyLayer(strokes, kp.Look, map, kp.TrimStart, kp.TrimEnd, 1280, 720, false);
            r.Check("keycap shows Ctrl+C at 1.05 s (fading in)", kl.At(1.05) is var a1 && a1 != null && J(a1.Value.keys) == "Ctrl+C" && a1.Value.alpha < 1);
            r.Check("next stroke replaces it at 1.5 s", kl.At(1.6) is var a2 && a2 != null && J(a2.Value.keys) == "Ctrl+V");
            r.Check("gone 1.2 s after the last stroke", kl.At(2.75) == null && kl.At(5.5) != null && kl.At(6.3) == null);
            r.Check("stroke inside a cut never shows; outside trim never shows", Enumerable.Range(0, 200).All(i => { var x = kl.At(i * 0.06); return x == null || J(x.Value.keys) != "Tab" && J(x.Value.keys) != "F1"; }));
            var kpos = new StudioLook { ShowKeys = true, KeysAt = "bottomLeft" };
            var kl2 = new KeyLayer(strokes, kpos, map, 0, 11, 1280, 720, true);
            var (lx, ly) = kl2.Place(200, 60, 1);
            r.Check("bottom-left position", lx == (int)Math.Round(720 * 0.05) && ly == (int)Math.Round(720 - 720 * 0.06 - 60), $"{lx},{ly}");
            var kpc = new StudioLook { ShowKeys = true, KeysAt = "bottom" };
            var (cx1, cy1) = new KeyLayer(strokes, kpc, map, 0, 11, 1280, 720, true).Place(200, 60, 1);
            var (cx2, cy2) = new KeyLayer(strokes, kpc, map, 0, 11, 1280, 720, false).Place(200, 60, 1);
            r.Check("centred keycaps move up out of the way of captions", cx1 == 540 && cy1 < cy2 - 70, $"{cy1} vs {cy2}");

            // 5) 摄像头几何：小窗、分屏、过渡
            var cs = new CameraSettings { X = 0.86, Y = 0.8, Size = 0.24 };
            var cl = new CameraLayer(cs, new List<CameraLayoutSeg> { new CameraLayoutSeg { Start = 2, End = 4, Mode = "full" }, new CameraLayoutSeg { Start = 4, End = 6, Mode = "split" }, new CameraLayoutSeg { Start = 8, End = 9, Mode = "hidden" } }, null, 0, 1280, 720);
            var (px, py, side) = CameraLayer.PipRect(cs, 1280, 720);
            var g0 = cl.GeoAt(1);
            r.Check("bubble: 24% of the short side, centred at (86%, 80%)", Near(side, 0.24 * 720) && Near(px + side / 2, 0.86 * 1280) && Near(py + side / 2, 0.8 * 720) && Near(g0.Cw, side) && Near(g0.Sw, 1280), $"{F(px)},{F(py)} {F(side)}");
            r.Check("bubble pushed back inside near the edge", CameraLayer.PipRect(new CameraSettings { X = 1, Y = 1, Size = 0.3 }, 1280, 720) is var e && e.x + e.side <= 1280 - 720 * 0.02 + 1e-6 && e.y + e.side <= 720 - 720 * 0.02 + 1e-6);
            var gf = cl.GeoAt(3);
            r.Check("full: camera covers the canvas", Near(gf.Cx, 0) && Near(gf.Cw, 1280) && Near(gf.Ch, 720) && Near(gf.Radius, 0));
            var gs = cl.GeoAt(5);
            r.Check("split: screen shrunk on the left, camera card on the right", gs.Sw < 1280 * 0.7 && gs.Sx < 100 && gs.Cx > 800 && Near(gs.Cw, 1280 * 0.3) && gs.Sw / gs.Sh > 1.77 && gs.Sw / gs.Sh < 1.78, $"screen {F(gs.Sx)},{F(gs.Sy)} {F(gs.Sw)}x{F(gs.Sh)} cam {F(gs.Cx)}");
            var gt = cl.GeoAt(2.2);
            r.Check("transition: halfway between bubble and full 0.2 s in", gt.Cw > side + 50 && gt.Cw < 1280 - 50, F(gt.Cw));
            r.Check("transition done after 0.4 s", Near(cl.GeoAt(2.41).Cw, 1280, 1e-3));
            r.Check("hidden fades out; back to the bubble after the last segment", cl.GeoAt(8.5).Alpha < 0.01 && Near(cl.GeoAt(9.5).Alpha, 1) && Near(cl.GeoAt(9.5).Cw, side));
            var sw = new CameraLayer(cs, new List<CameraLayoutSeg> { new CameraLayoutSeg { Start = 0, End = 5, Mode = "splitSwap" } }, null, 0, 1280, 720).GeoAt(3);
            r.Check("split (camera left) mirrors it", sw.Cx < 100 && sw.Sx > 300);
            return r.Done("W3 RULES");
        }

        // 读回成片第 t 秒一帧（BGRA）
        static byte[] Grab(string file, double t, int w, int h) => Studio.DecodeFrame(file, t, w, h, "bt709");
        static (int r, int g, int b) Px(byte[] f, int w, int x, int y) { int i = (y * w + x) * 4; return (f[i + 2], f[i + 1], f[i]); }
        // 两帧的差异框（任一通道差 > thr）
        static (int x0, int y0, int x1, int y1, int n) Diff(byte[] a, byte[] b, int w, int h, int thr = 30)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1, n = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (Math.Abs(a[i] - b[i]) > thr || Math.Abs(a[i + 1] - b[i + 1]) > thr || Math.Abs(a[i + 2] - b[i + 2]) > thr)
                    { n++; x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
                }
            return (x0, y0, x1, y1, n);
        }
        static double Luma(byte[] f, int w, int x, int y, int rad = 3)
        {
            double s = 0; int n = 0;
            for (int dy = -rad; dy <= rad; dy++) for (int dx = -rad; dx <= rad; dx++) { var (r, g, b) = Px(f, w, x + dx, y + dy); s += 0.2126 * r + 0.7152 * g + 0.0722 * b; n++; }
            return s / n;
        }

        // 真导出。compose：null = 按默认（有显卡用显卡），"cpu" / "gpu" 强制
        public static string Export(string work, string compose = null)
        {
            var r = new Report();
            string prevCompose = Environment.GetEnvironmentVariable("STUDIO_COMPOSE");
            if (compose != null) Environment.SetEnvironmentVariable("STUDIO_COMPOSE", compose);
            try
            {
                Directory.CreateDirectory(work);
                string dir = Path.Combine(work, "src" + (compose ?? ""));
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                // 屏幕素材：12 秒灰画面，画面里有一块会动的白条（看推近和剪切用）；摄像头：320×240，整幅亮度 = 秒 × 20（素材时间 = 摄像头时间 + 0.5）
                Studio.RunQuiet($"-v error -y -f lavfi -i \"color=c=0x606060:s=640x360:r=30:d=12\" -vf \"drawbox=x='mod(t*40,600)':y=150:w=40:h=60:color=white:t=fill\" -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{Path.Combine(dir, "raw.mp4")}\"", 120000);
                Studio.RunQuiet($"-v error -y -f lavfi -i \"color=c=black:s=320x240:r=30:d=12,geq=lum='min(250,T*20)':cb=128:cr=128\" -c:v libx264 -preset ultrafast -crf 8 -pix_fmt yuv420p \"{Path.Combine(dir, "camera.mp4")}\"", 120000);
                var track = new Track { Width = 640, Height = 360, PixelsPerPoint = 1, Moves = new List<double[]> { new double[] { 0, 320, 180, 0, 0 } },
                    Cursors = new List<CursorShape> { new CursorShape { File = "c.png", W = 16, H = 16, Arrow = true } },
                    Shortcuts = new List<KeyStroke> { new KeyStroke { T = 1.0, Keys = new[] { "Ctrl", "C" } }, new KeyStroke { T = 5.5, Keys = new[] { "Ctrl", "V" } } },
                    Camera = new CameraTrack { Offset = 0.5, X = 0.86, Y = 0.8, Size = 0.24 } };
                File.WriteAllText(Path.Combine(dir, "track.json"), track.ToJson());
                r.Check("test footage made", File.Exists(Path.Combine(dir, "raw.mp4")) && File.Exists(Path.Combine(dir, "camera.mp4")));
                var opt = new ExportOptions { Resolution = "original", Fps = 30, Quality = "high" };
                var info = Studio.Probe(Path.Combine(dir, "raw.mp4"));
                StudioProject Base(bool keys, bool cam)
                {
                    var p = new StudioProject { TrimStart = 0, TrimEnd = 12, SegmentsEdited = true };
                    p.Look.Blur = "off"; p.Look.ClickSound = false; p.Look.Ripple = false; p.Look.Shadow = 0.45;
                    p.Look.ShowKeys = keys;
                    p.Camera = cam ? CameraSettings.From(track.Camera) : null;
                    if (p.Camera != null) { p.Camera.Mirror = false; p.Camera.Border = 4; p.Camera.Shadow = 0.5; }
                    return p;
                }
                string Render(StudioProject p, string name)
                {
                    var outF = Path.Combine(work, name + (compose ?? "") + ".mp4");
                    try { var stx = Studio.Render(dir, outF, new StudioStyle(), _ => { }, null, null, p, opt); r.Note($"  {name}: composer {stx.Composer}"); }
                    catch (Exception e) { r.Check("export " + name, false, e.Message); return null; }
                    return outF;
                }
                var plain = Render(Base(false, false), "plain");
                var vinfo = plain != null ? Studio.Probe(plain) : null;
                int W = vinfo?.Width ?? 0, H = vinfo?.Height ?? 0;
                r.Note($"canvas {W}x{H}");

                // ---- 键帽 ----
                var keys = Render(Base(true, false), "keys");
                if (plain != null && keys != null)
                {
                    var d1 = Diff(Grab(plain, 1.4, W, H), Grab(keys, 1.4, W, H), W, H);
                    double cxk = (d1.x0 + d1.x1) / 2.0;
                    r.Check("keycap appears 0.4 s after Ctrl+C, centred at the bottom", d1.n > 200 && Math.Abs(cxk - W / 2.0) < 6 && d1.y1 > H * 0.8 && d1.y1 < H - H * 0.04, $"box {d1.x0},{d1.y0}–{d1.x1},{d1.y1} n={d1.n}");
                    var d2 = Diff(Grab(plain, 3.0, W, H), Grab(keys, 3.0, W, H), W, H);
                    r.Check("keycap gone at 3 s (1.2 s hold)", d2.n < 20, $"n={d2.n}");
                    var d0 = Diff(Grab(plain, 0.5, W, H), Grab(keys, 0.5, W, H), W, H);
                    r.Check("nothing before the first shortcut", d0.n < 20, $"n={d0.n}");
                    // 剪掉 5–6 秒（Ctrl+V 在 5.5 秒）：成片里不出现
                    var pc = Base(true, false); pc.Cuts.Add(new CutSegment { Start = 5, End = 6 });
                    var pcn = Base(false, false); pcn.Cuts.Add(new CutSegment { Start = 5, End = 6 });
                    var kc = Render(pc, "keys-cut"); var kcn = Render(pcn, "keys-cut-plain");
                    if (kc != null && kcn != null)
                    {
                        int hits = 0;
                        foreach (var tt in new[] { 4.8, 5.0, 5.2, 5.4 }) if (Diff(Grab(kcn, tt, W, H), Grab(kc, tt, W, H), W, H).n > 50) hits++;
                        r.Check("shortcut inside a cut never shows", hits == 0, $"hits={hits}");
                    }
                    // 位置：右下
                    var pr = Base(true, false); pr.Look.KeysAt = "bottomRight";
                    var kr = Render(pr, "keys-right");
                    if (kr != null) { var d3 = Diff(Grab(plain, 1.4, W, H), Grab(kr, 1.4, W, H), W, H); r.Check("bottom-right keycap", d3.n > 200 && d3.x1 > W * 0.9 && d3.x0 > W * 0.6, $"box {d3.x0}–{d3.x1}"); }
                }

                // ---- 摄像头 ----
                var cam = Render(Base(false, true), "camera");
                if (plain != null && cam != null)
                {
                    var (ex, ey, eside) = CameraLayer.PipRect(Base(false, true).Camera, W, H);
                    var fp = Grab(plain, 3.0, W, H); var fc = Grab(cam, 3.0, W, H);
                    var d = Diff(fp, fc, W, H, 12);
                    r.Check("bubble where expected (bottom right, 24% of short side; border / shadow around)", d.n > 500 && Math.Abs((d.x0 + d.x1) / 2.0 - (ex + eside / 2)) < 6 && d.x1 - d.x0 >= eside - 2 && d.x1 - d.x0 < eside + 40,
                            $"box {d.x0},{d.y0}–{d.x1},{d.y1}, expected centre {F(ex + eside / 2)},{F(ey + eside / 2)} side {F(eside)}");
                    int cxp = (int)(ex + eside / 2), cyp = (int)(ey + eside / 2);
                    double lum = Luma(fc, W, cxp, cyp);
                    // 摄像头时间 = 3 − 0.5 = 2.5 秒 → 亮度 ≈ 50（有限范围解码，留点余量）
                    r.Check("bubble shows the camera at source time − offset (3 − 0.5 = 2.5 s)", Math.Abs((lum * 219 / 255 + 16) / 20 - 2.5) < 0.15, F((lum * 219 / 255 + 16) / 20) + " s");
                    // 圆形：外接框四角是底图（和不带摄像头一样）
                    var (cr, cg, cb) = Px(fc, W, (int)ex + 3, (int)ey + 3); var (pr_, pg, pb) = Px(fp, W, (int)ex + 3, (int)ey + 3);
                    r.Check("round: corners of the bounding box untouched", Math.Abs(cr - pr_) < 12 && Math.Abs(cg - pg) < 12 && Math.Abs(cb - pb) < 12, $"{cr},{cg},{cb} vs {pr_},{pg},{pb}");
                    var (tr_, tg, tb) = Px(fc, W, cxp, (int)Math.Floor(ey) - 1);
                    r.Check("white border at the top of the circle", tr_ > 200 && tg > 200 && tb > 200, $"{tr_},{tg},{tb}");
                    // 圆角方形：同一个角是摄像头画面
                    var psq = Base(false, true); psq.Camera.Round = false;
                    var csq = Render(psq, "camera-square");
                    if (csq != null) { double lc = Luma(Grab(csq, 3.0, W, H), W, (int)ex + (int)(eside * 0.18), (int)ey + (int)(eside * 0.18), 1); r.Check("rounded square: near-corner pixel is camera", Math.Abs(lc - Luma(fc, W, cxp, cyp)) < 15, F(lc)); }
                    // 推近：小窗大小不变
                    var pz = Base(false, true); pz.Segments.Add(new ZoomSegment { Start = 4, End = 9, Zoom = 2.5, Fixed = true, FixedX = 0.3, FixedY = 0.3 });
                    var pzn = Base(false, false); pzn.Segments.Add(new ZoomSegment { Start = 4, End = 9, Zoom = 2.5, Fixed = true, FixedX = 0.3, FixedY = 0.3 });
                    var cz = Render(pz, "camera-zoom"); var czn = Render(pzn, "camera-zoom-plain");
                    if (cz != null && czn != null)
                    {
                        // 量摄像头画面本身（中心那一行亮度和中心一样的像素有多宽），不量阴影：推近后小窗底下的画面变了，阴影外沿跟着变
                        int Disc(byte[] f) { double lc = Luma(f, W, cxp, cyp, 1); int a0 = cxp, b0 = cxp; while (a0 > 0 && Math.Abs(Luma(f, W, a0 - 1, cyp, 0) - lc) < 10) a0--; while (b0 < W - 1 && Math.Abs(Luma(f, W, b0 + 1, cyp, 0) - lc) < 10) b0++; return b0 - a0 + 1; }
                        int wz = Disc(Grab(cz, 6.5, W, H)), wn = Disc(Grab(cam, 6.5, W, H));
                        r.Check("zoomed in: bubble the same size (not magnified)", Math.Abs(wz - wn) <= 3 && Math.Abs(wn - eside) < 8, $"{wz} vs {wn} (side {F(eside)})");
                    }
                    // 布局：2–4 全屏、5–7 分屏、8–9 隐藏
                    var pl = Base(false, true);
                    pl.Layouts.Add(new CameraLayoutSeg { Start = 2, End = 4, Mode = "full" });
                    pl.Layouts.Add(new CameraLayoutSeg { Start = 5, End = 7, Mode = "split" });
                    pl.Layouts.Add(new CameraLayoutSeg { Start = 8, End = 9, Mode = "hidden" });
                    var cl = Render(pl, "camera-layouts");
                    if (cl != null)
                    {
                        var f3 = Grab(cl, 3.0, W, H);
                        double l1 = Luma(f3, W, W / 4, H / 4), l2 = Luma(f3, W, W * 3 / 4, H * 3 / 4);
                        r.Check("full: camera everywhere (same brightness at two corners of the canvas)", Math.Abs(l1 - l2) < 8 && Math.Abs(l1 - Luma(fc, W, cxp, cyp)) < 12, $"{F(l1)} {F(l2)}");
                        var f21 = Grab(cl, 2.2, W, H);
                        var dt = Diff(fp, f21, W, H, 12);
                        r.Check("transition: 0.2 s after the switch the card is between bubble and full size", dt.x1 - dt.x0 > eside + 40 && dt.n < W * H * 0.97, $"{dt.x1 - dt.x0}px");
                        var f6 = Grab(cl, 6.0, W, H);
                        var pplain = Base(false, false); pplain.Look.Shadow = 0;
                        // 分屏：右边一条摄像头竖卡，左边是缩小的屏幕画面（白条在里面），四周是背景
                        var c = new CameraLayer(pl.Camera, pl.Layouts, null, 0, W, H).GeoAt(6.0);
                        double lcam = Luma(f6, W, (int)(c.Cx + c.Cw / 2), (int)(c.Cy + c.Ch / 2));
                        double lscr = Luma(f6, W, (int)(c.Sx + c.Sw / 2), (int)(c.Sy + c.Sh * 0.2));
                        r.Check("split: camera card on the right", Math.Abs(lcam - Luma(Grab(cam, 6.0, W, H), W, cxp, cyp)) < 15, $"{F(lcam)}");
                        r.Check("split: shrunk screen on the left (grey footage)", Math.Abs(lscr - Luma(fp, W, W / 2, (int)(H * 0.2))) < 25, F(lscr));
                        var f85 = Grab(cl, 8.6, W, H);
                        r.Check("hidden: no camera", Diff(Grab(plain, 8.6, W, H), f85, W, H, 12).n < 30);
                    }
                    // 剪掉 / 快放后摄像头和屏幕同步：剪掉 4–6，7–9 快放 4×（成片 t → 素材 → 摄像头 = 素材 − 0.5）
                    var ps = Base(false, true); ps.Layouts.Add(new CameraLayoutSeg { Start = 0, End = 12, Mode = "full" });
                    ps.Cuts.Add(new CutSegment { Start = 4, End = 6 }); ps.Cuts.Add(new CutSegment { Start = 7, End = 9, Speed = true, Rate = 4 });
                    var csync = Render(ps, "camera-sync");
                    if (csync != null)
                    {
                        var map = new TimeMap(ps);
                        var errs = new List<string>(); bool good = true;
                        // 摄像头画面的 Y（有限范围码值）= 秒 × 20；读回来的是全范围 RGB：Y = 亮度 × 219/255 + 16
                        foreach (var to in new[] { 3.0, 4.5, 5.2, 5.6, 6.5 })
                        {
                            double camT = (Luma(Grab(csync, to, W, H), W, W / 2, H / 2) * 219 / 255 + 16) / 20, want = map.Src(to) - 0.5;
                            errs.Add($"{F(to)}→{F(camT)}({F(want)})"); good &= Math.Abs(camT - want) < 0.15;
                        }
                        r.Check("camera follows cuts / speed-up (camera time = source − offset)", good, string.Join(" ", errs));
                    }
                }
            }
            catch (Exception e) { r.Check("exception", false, e.ToString()); }
            finally { Environment.SetEnvironmentVariable("STUDIO_COMPOSE", prevCompose); }
            return r.Done("W3 EXPORT" + (compose != null ? " (" + compose + ")" : ""));
        }
    }
}
