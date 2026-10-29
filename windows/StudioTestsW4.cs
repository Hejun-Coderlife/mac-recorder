// W4 的核心自测（不碰界面，Mac 上的 .NET 8 小工具和 Windows 程序共用；用例照 Mac 版 OVERLAYTEST / DTEST）：
//   Rules()  工程字段（遮挡、标注、背景音乐、推近速度、回到起点）存了再读一样、Mac 写的格式读得进；淡入淡出；音乐循环 / 淡入淡出 / 压低曲线；
//            推近速度（慢 / 标准 / 快 到位时间、单段覆盖）；结尾回到起点；建议加速的规则
//   Export() 真导出：遮挡（模糊 / 马赛克）区域里变了、区域外不变、推近时跟着画面放大挪动；框 / 箭头 / 文字的位置、时间、淡入、箭头画出来；
//            背景音乐的时间、淡入淡出、循环、讲解时压低、限幅；画面变化（建议加速）和采纳后导出的时长
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    static class StudioTestsW4
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

        // ---- 规则 ----
        public static string Rules()
        {
            var r = new Report();
            // 1) 工程字段：存了再读一样
            var p = new StudioProject { TrimStart = 0, TrimEnd = 20 };
            p.Redactions.Add(new Redaction { Start = 1, End = 2, X = 0.1, Y = 0.2, W = 0.3, H = 0.1, Style = "mosaic", Strength = 0.7, Corner = 4, Color = new RGB(0.5, 0.1, 0.2) });
            p.Annotations.Add(new Annotation { Start = 1, End = 3, Kind = "text", X = 0.5, Y = 0.5, Text = "你好", Pill = false, FontSize = 32 });
            p.Annotations.Add(new Annotation { Start = 2, End = 4, Kind = "arrow", X = 0.2, Y = 0.7, W = 0.25, H = -0.2, Color = new RGB(0.04, 0.52, 1), DrawOn = false, Width = 7 });
            p.Music.Clips.Add(new MusicClip { File = "music/a.m4a", Name = "a.m4a", Start = 2, TrimIn = 1, TrimOut = 4, FileLength = 30 });
            p.Music.Volume = 0.8; p.Music.DuckDB = -20; p.Music.Loop = false; p.Music.FadeIn = 0.5;
            p.Look.ZoomSpeed = 1.7; p.Look.CursorLoop = true; p.Look.CursorLoopSec = 1.5; p.Look.LoopZoomOut = false;
            p.Segments.Add(new ZoomSegment { Start = 1, End = 3, Speed = 0.6 });
            p.Segments.Add(new ZoomSegment { Start = 5, End = 7 });
            var p2 = StudioProject.FromJson(p.ToJson());
            r.Check("project.json round-trip (redactions, annotations, music, zoom speed, cursor loop, per-zoom speed)",
                p2.ToJson() == p.ToJson() && p2.Redactions.Count == 1 && p2.Redactions[0].Style == "mosaic" && Near(p2.Redactions[0].Color.R, 0.5) &&
                p2.Annotations.Count == 2 && p2.Annotations[0].Text == "你好" && !p2.Annotations[0].Pill && p2.Annotations[1].Kind == "arrow" && Near(p2.Annotations[1].H, -0.2) &&
                p2.Music.Clips.Count == 1 && Near(p2.Music.Clips[0].TrimOut, 4) && Near(p2.Music.DuckDB, -20) && !p2.Music.Loop &&
                Near(p2.Look.ZoomSpeed, 1.7) && p2.Look.CursorLoop && Near(p2.Look.CursorLoopSec, 1.5) && !p2.Look.LoopZoomOut &&
                p2.Segments[0].Speed == 0.6 && p2.Segments[1].Speed == null);
            r.Check("these fields are not left in Extra", !p2.Extra.ContainsKey("redactions") && !p2.Extra.ContainsKey("music") && !p2.Look.Extra.ContainsKey("zoomSpeed") && !p2.Segments[0].Extra.ContainsKey("speed"));
            // Mac 写的（UUID、键按 Mac 的名字）
            const string mac = "{\"trimStart\":0,\"trimEnd\":10,\"segments\":[{\"id\":\"6F1A2B3C-0000-4000-8000-000000000001\",\"start\":1,\"end\":3,\"zoom\":2.5,\"mode\":\"follow\",\"fixedX\":0.5,\"fixedY\":0.5,\"speed\":1.7}]," +
                "\"look\":{\"zoomSpeed\":0.6,\"cursorLoop\":true,\"cursorLoopSec\":2,\"loopZoomOut\":true,\"background\":\"transparent\"}," +
                "\"redactions\":[{\"id\":\"6F1A2B3C-0000-4000-8000-000000000002\",\"start\":0.5,\"end\":2.5,\"x\":0.1,\"y\":0.15,\"w\":0.3,\"h\":0.25,\"style\":\"blur\",\"strength\":0.8,\"color\":{\"r\":0.12,\"g\":0.12,\"b\":0.14},\"corner\":8}]," +
                "\"annotations\":[{\"id\":\"6F1A2B3C-0000-4000-8000-000000000003\",\"start\":3,\"end\":5,\"kind\":\"rect\",\"x\":0.55,\"y\":0.2,\"w\":0.3,\"h\":0.3,\"color\":{\"r\":1,\"g\":0.27,\"b\":0.23},\"width\":5,\"text\":\"\",\"fontSize\":28,\"pill\":true,\"drawOn\":true}]," +
                "\"music\":{\"clips\":[{\"id\":\"6F1A2B3C-0000-4000-8000-000000000004\",\"file\":\"music/x.m4a\",\"name\":\"x.m4a\",\"start\":2,\"trimIn\":0,\"trimOut\":3,\"fileLength\":3}],\"volume\":0.5,\"fadeIn\":1,\"fadeOut\":2,\"loop\":true,\"duck\":true,\"duckDB\":-14}}";
            var pm = StudioProject.FromJson(mac);
            r.Check("Mac project.json (redaction / annotation / music / zoom speed / loop) readable",
                pm.Redactions.Count == 1 && pm.Redactions[0].Id == "6F1A2B3C-0000-4000-8000-000000000002" && Near(pm.Redactions[0].Strength, 0.8) &&
                pm.Annotations.Count == 1 && pm.Annotations[0].Kind == "rect" && pm.Music.Clips.Count == 1 && pm.Music.Clips[0].File == "music/x.m4a" &&
                Near(pm.Look.ZoomSpeed, 0.6) && pm.Look.CursorLoop && Near(pm.Look.CursorLoopSec, 2) && pm.Segments[0].Speed == 1.7);
            var old = StudioProject.FromJson("{\"segments\":[],\"trimStart\":0,\"trimEnd\":10}");
            r.Check("old project.json: no overlays / music, default zoom speed", old.Redactions.Count == 0 && old.Annotations.Count == 0 && old.Music.Clips.Count == 0 && Near(old.Look.ZoomSpeed, 1) && !old.Look.CursorLoop);

            // 2) 淡入淡出、箭头画出来（同 Mac）
            r.Check("fade in / out", Near(OverlayAnim.Alpha(1.1, 1, 3), 0.4, 1e-9) && OverlayAnim.Alpha(2, 1, 3) == 1 && OverlayAnim.Alpha(3, 1, 3) == 0 && OverlayAnim.Alpha(0.9, 1, 3) == 0);
            r.Check("arrow draw-on progress", OverlayAnim.DrawProgress(1, 1) == 0 && Near(OverlayAnim.DrawProgress(1.4, 1), 1) && OverlayAnim.DrawProgress(1.1, 1) > 0.5);

            // 3) 音乐：循环点、淡入淡出、截到片尾
            var m = new MusicTrack { Volume = 1, FadeIn = 1, FadeOut = 2, Loop = true };
            m.Clips.Add(new MusicClip { Start = 2, TrimIn = 0, TrimOut = 3, FileLength = 3 });
            var occ = m.Occurrences(10);
            r.Check("music loops at 2 / 5 / 8 s and the last one is cut at the end", occ.Count == 3 && Near(occ[0].At, 2) && Near(occ[1].At, 5) && Near(occ[2].At, 8) && Near(occ[2].Len, 2),
                string.Join(",", occ.Select(o => F(o.At) + "+" + F(o.Len))));
            r.Check("only the first fades in, only the last fades out", occ[0].FadeIn && !occ[1].FadeIn && !occ[2].FadeIn && occ[2].FadeOut && !occ[0].FadeOut && !occ[1].FadeOut);
            r.Check("fade-in halfway = half volume", Near(m.Gain(occ[0], 0.5), 0.5, 1e-6) && Near(m.Gain(occ[1], 0.5), 1, 1e-6));
            r.Check("fade-out 0.5 s before the end ≈ 0.25", Near(m.Gain(occ[2], 1.5), 0.25, 1e-6));
            var m2 = new MusicTrack { Loop = false }; m2.Clips.Add(new MusicClip { Start = 2, TrimOut = 3 });
            r.Check("loop off: played once", m2.Occurrences(10).Count == 1);

            // 4) 压低曲线：讲解里是 -14 dB（0.1995），说话前 0.15 秒开始压、讲完停 0.3 秒、0.4 秒回来
            float lv = (float)Math.Pow(10, -14 / 20.0);
            var curve = MusicDuck.Curve(lv, 10, new List<(double, double)> { (3, 5) }, null, null);
            r.Check("ducked inside the voice-over", Near(MusicDuck.At(curve, 4), lv, 1e-3), F(MusicDuck.At(curve, 4)));
            r.Check("starts lowering 0.15 s before", MusicDuck.At(curve, 2.8) > 0.999 && MusicDuck.At(curve, 2.9) < 0.95, F(MusicDuck.At(curve, 2.9)));
            r.Check("holds 0.3 s after, back within 0.4 s", Near(MusicDuck.At(curve, 5.28), lv, 1e-3) && MusicDuck.At(curve, 5.5) > lv + 0.05 && MusicDuck.At(curve, 5.75) > 0.999,
                F(MusicDuck.At(curve, 5.5)) + " " + F(MusicDuck.At(curve, 5.75)));
            var env = new float[1000]; for (int i = 700; i < 800; i++) env[i] = 0.05f;   // 原声音 7–8 秒在说话
            var curve2 = MusicDuck.Curve(lv, 10, new List<(double, double)>(), env, null);
            r.Check("also ducked where the recording has speech", Near(MusicDuck.At(curve2, 7.5), lv, 1e-3) && MusicDuck.At(curve2, 6) > 0.999);
            r.Check("duck off = flat 1", new MusicTrack { Duck = false }.DuckLevel == 1f && MusicDuck.Curve(1f, 5, new List<(double, double)> { (1, 2) }, null, null).All(v => v == 1f));

            // 5) 推近速度：慢 / 标准 / 快 到 1.9 倍用的时间单调；单段覆盖只影响那一段
            var tr = new Track { Width = 1280, Height = 720, PixelsPerPoint = 1, Moves = new List<double[]> { new double[] { 0, 640, 360, 0, 0 } } };
            var lay = new StudioLayout(tr, new StudioLook());
            double Reach(double speed, double? segSpeed, double segStart, int which)
            {
                var look = new StudioLook { ZoomSpeed = speed };
                var segs = new List<ZoomSegment> { new ZoomSegment { Start = 1, End = 4, Zoom = 2, Fixed = true }, new ZoomSegment { Start = 8, End = 11, Zoom = 2, Fixed = true, Speed = segSpeed } };
                var pl = StudioPlan.Make(tr, lay, new StudioStyle { Fps = 60 }, look, segs, 60 * 14, 14);
                double st0 = which == 0 ? 1 : 8;
                for (int i = (int)(st0 * 60); i < pl.Length; i++) if (pl[i].z >= 1.9) return i / 60.0 - st0;
                return 99;
            }
            double slow = Reach(0.6, null, 1, 0), std = Reach(1, null, 1, 0), fast = Reach(1.7, null, 1, 0);
            r.Check("zoom speed: slow > standard > fast to reach 1.9×", slow > std && std > fast, $"{F(slow)} / {F(std)} / {F(fast)} s");
            r.Check("standard ≈ Mac (1.05 s)", Math.Abs(std - 1.05) < 0.08, F(std));
            double a1 = Reach(1, 1.7, 1, 0), a2 = Reach(1, 1.7, 1, 1), b2 = Reach(1, null, 1, 1);
            r.Check("per-zoom speed only changes that zoom", Near(a1, std, 0.02) && a2 < b2 - 0.2, $"first {F(a1)} (std {F(std)}), second {F(b2)} → {F(a2)}");

            // 6) 结尾鼠标回到起点：最后一帧鼠标 = 第一帧，镜头回全景
            var tr2 = new Track { Width = 1280, Height = 720, PixelsPerPoint = 1, Moves = new List<double[]> { new double[] { 0, 100, 100, 0, 0 }, new double[] { 5, 1100, 600, 0, 0 }, new double[] { 10, 1100, 600, 0, 0 } } };
            var lookL = new StudioLook { CursorLoop = true, CursorLoopSec = 1, LoopZoomOut = true, Smoothing = 0 };
            var segsL = new List<ZoomSegment> { new ZoomSegment { Start = 6, End = 10, Zoom = 2, Fixed = true, FixedX = 0.8, FixedY = 0.8 } };
            var pl0 = StudioPlan.Make(tr2, lay, new StudioStyle { Fps = 30 }, new StudioLook { Smoothing = 0 }, segsL, 300, 10, (0, 10));
            var plL = StudioPlan.Make(tr2, lay, new StudioStyle { Fps = 30 }, lookL, segsL, 300, 10, (0, 10));
            r.Check("loop: last frame cursor = first frame", Near(plL[299].mx, plL[0].mx, 0.01) && Near(plL[299].my, plL[0].my, 0.01), $"{F(plL[299].mx)},{F(plL[299].my)} vs {F(plL[0].mx)},{F(plL[0].my)}");
            r.Check("loop: camera back to full at the end", Near(plL[299].z, 1, 1e-3) && pl0[299].z > 1.5, $"z {F(plL[299].z)} (without {F(pl0[299].z)})");
            r.Check("loop: nothing changes before the last second", Near(plL[260].mx, pl0[260].mx, 1e-9) && Near(plL[260].z, pl0[260].z, 1e-9));
            int mid = 270 + 14; double frac = (plL[mid].mx - pl0[mid].mx) / (plL[0].mx - pl0[mid].mx);
            r.Check("loop: about halfway back halfway through", frac > 0.35 && frac < 0.65, F(frac));

            // 7) 建议加速：空档（4×）、打字（2×）、不到 3 秒不建议、已经剪过的地方不建议
            var trS = new Track { Width = 100, Height = 100, Moves = new List<double[]> { new double[] { 0, 10, 10, 0, 0 }, new double[] { 2, 50, 50, 0, 0 } } };
            for (double k = 9.3; k <= 13.8; k += 0.5) trS.Keys.Add(Math.Round(k, 2));
            trS.Keys.Add(16); trS.Keys.Add(16.8);   // 很短的一下：不算
            var diffs = new float[(int)(20 / SpeedSuggest.Bin)];
            for (int i = 0; i < diffs.Length; i++) diffs[i] = i * SpeedSuggest.Bin >= 8.25 && i * SpeedSuggest.Bin < 9.0 ? 10 : 0;   // 8.25–9 秒画面在动
            var sug = SpeedSuggest.Find(trS, diffs, 0, 20, new List<CutSegment>());
            var idle = sug.FirstOrDefault(g => !g.Typing); var typ = sug.FirstOrDefault(g => g.Typing);
            r.Check("idle gap → 4× suggestion", idle != null && idle.Rate == 4 && idle.Start >= 2 && idle.Start < 2.8 && idle.End <= 8.25 && idle.End > 7.5, idle == null ? "none" : $"{F(idle.Start)}–{F(idle.End)}");
            r.Check("typing → 2× suggestion", typ != null && typ.Rate == 2 && Near(typ.Start, 9.6, 0.05) && Near(typ.End, 13.8, 0.05), typ == null ? "none" : $"{F(typ.Start)}–{F(typ.End)}");
            r.Check("short bursts not suggested", !sug.Any(g => g.Start > 15 && g.Start < 17.5 && g.Typing));
            var sug2 = SpeedSuggest.Find(trS, diffs, 0, 20, new List<CutSegment> { new CutSegment { Start = 10, End = 11 } });
            r.Check("not over existing cuts", !sug2.Any(g => g.Typing && g.Start < 11 && g.End > 10));
            return r.Done("W4 rules");
        }

        // ---- 真导出 ----
        static byte[] Grab(string file, double t, int w, int h) => Studio.DecodeFrame(file, t, w, h, "bt709");
        struct Cmp { public double InMean, OutFrac, GradA, GradB; public int X0, Y0, X1, Y1, N; }
        // 两帧比较：region（左上原点像素）里平均差、往外让 pad 像素后外面明显不同的占比、变化的外框、区域里的细节（相邻像素差）
        // near：只在 region 往外 pad 像素的范围里找变化（别把视频压缩在别处的零星差异算进外框）
        static Cmp Compare(byte[] a, byte[] b, int w, int h, (double x0, double y0, double x1, double y1) region, double pad = 6, bool near = false)
        {
            var c = new Cmp { X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1 };
            double inSum = 0; int inN = 0, outBig = 0, outN = 0;
            var rowN = new int[h]; var colN = new int[w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    int d = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                    double px = x + 0.5, py = y + 0.5;
                    if (d > 45) c.N++;
                    bool inBig = px > region.x0 - pad && px < region.x1 + pad && py > region.y0 - pad && py < region.y1 + pad;
                    if (d > 90 && (!near || inBig)) { rowN[y]++; colN[x]++; }
                    bool inSmall = px > region.x0 + pad && px < region.x1 - pad && py > region.y0 + pad && py < region.y1 - pad;
                    if (inSmall)
                    {
                        inSum += d; inN++;
                        if (x + 1 < w) { c.GradA += Math.Abs(a[i] - a[i + 4]); c.GradB += Math.Abs(b[i] - b[i + 4]); }
                    }
                    else if (!inBig) { outN++; if (d > 90) outBig++; }
                }
            for (int y = 0; y < h; y++) if (rowN[y] > Math.Max(2, w / 200)) { c.Y0 = Math.Min(c.Y0, y); c.Y1 = Math.Max(c.Y1, y); }
            for (int x = 0; x < w; x++) if (colN[x] > Math.Max(2, h / 200)) { c.X0 = Math.Min(c.X0, x); c.X1 = Math.Max(c.X1, x); }
            c.InMean = inN > 0 ? inSum / inN : 0; c.OutFrac = outN > 0 ? outBig / (double)outN : 0;
            return c;
        }

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
                // 屏幕素材：16 秒、640×360 的测试图（细节多，模糊、马赛克看得出来）；12 秒以后画面静止（建议加速用）
                Studio.RunQuiet($"-v error -y -f lavfi -i \"testsrc2=s=640x360:r=30:d=12\" -f lavfi -i \"color=c=0x707070:s=640x360:r=30:d=4\" " +
                                "-filter_complex \"[0:v]noise=alls=12:allf=u[n];[1:v]drawbox=x=100:y=100:w=200:h=120:color=white:t=fill[s];[n][s]concat=n=2:v=1:a=0,format=yuv420p\" " +
                                $"-c:v libx264 -preset ultrafast -crf 12 \"{Path.Combine(dir, "raw.mp4")}\"", 180000);
                var track = new Track { Width = 640, Height = 360, PixelsPerPoint = 1, Moves = new List<double[]> { new double[] { 0, 320, 180, -1, 0 } } };
                File.WriteAllText(Path.Combine(dir, "track.json"), track.ToJson());
                r.Check("test footage made", File.Exists(Path.Combine(dir, "raw.mp4")));
                var opt = new ExportOptions { Resolution = "original", Fps = 30, Quality = "high" };
                StudioProject Base()
                {
                    var p = new StudioProject { TrimStart = 0, TrimEnd = 15, SegmentsEdited = true };
                    p.Look.Blur = "off"; p.Look.ClickSound = false; p.Look.Ripple = false; p.Look.Aspect = "16:9"; p.Look.Background = "color"; p.Look.Color = new RGB(0.2, 0.3, 0.5);
                    return p;
                }
                string Render(StudioProject p, string name)
                {
                    var outF = Path.Combine(work, name + (compose ?? "") + ".mp4");
                    try { var stx = Studio.Render(dir, outF, new StudioStyle(), _ => { }, null, null, p, opt); r.Note($"  {name}: composer {stx.Composer}"); }
                    catch (Exception e) { r.Check("export " + name, false, e.Message); return null; }
                    return outF;
                }
                // 0–10 秒全景，10.5–12 秒固定推近 2 倍到马赛克那块的中间
                var mos = (x: 0.30, y: 0.30, w: 0.25, h: 0.20);
                StudioProject Zoomed() { var p = Base(); p.Segments.Add(new ZoomSegment { Start = 10.5, End = 12, Zoom = 2, Fixed = true, FixedX = mos.x + mos.w / 2, FixedY = mos.y + mos.h / 2 }); return p; }
                var plainP = Zoomed();
                // 显卡有遮挡 / 标注时原画面按 BGRA 传（没有时走 NV12，色彩换算差一点点）：对照组也放一个永远不出现的遮挡，两份走同一条路，只比遮挡本身
                plainP.Redactions.Add(new Redaction { Start = 20, End = 21, X = 0, Y = 0, W = 0.1, H = 0.1 });
                var ov = Zoomed();
                var blurR = (x: 0.10, y: 0.15, w: 0.30, h: 0.25);
                ov.Redactions.Add(new Redaction { Start = 0.5, End = 2.5, X = blurR.x, Y = blurR.y, W = blurR.w, H = blurR.h, Style = "blur", Strength = 0.8 });
                ov.Redactions.Add(new Redaction { Start = 10.6, End = 11.9, X = mos.x, Y = mos.y, W = mos.w, H = mos.h, Style = "mosaic", Strength = 0.7 });
                var boxR = (x: 0.55, y: 0.20, w: 0.30, h: 0.30);
                ov.Annotations.Add(new Annotation { Start = 3.0, End = 5.0, Kind = "rect", X = boxR.x, Y = boxR.y, W = boxR.w, H = boxR.h, Color = new RGB(1, 0.27, 0.23) });
                ov.Annotations.Add(new Annotation { Start = 5.5, End = 7.5, Kind = "arrow", X = 0.20, Y = 0.70, W = 0.25, H = -0.20, Color = new RGB(0.04, 0.52, 1) });
                ov.Annotations.Add(new Annotation { Start = 8.0, End = 10.0, Kind = "text", X = 0.60, Y = 0.65, Text = "测试标注", FontSize = 32, Color = new RGB(1, 0.84, 0.04) });
                var uA = Render(plainP, "w4-plain"); var uB = Render(ov, "w4-marked");
                if (uA != null && uB != null)
                {
                    var vi = Studio.Probe(uA); int W = vi.Width, H = vi.Height;
                    // 预测位置：同一套布局和镜头计划，原画面比例 → 成片像素（左上原点）
                    var lay = new StudioLayout(track, plainP.Look);
                    var sc = new StudioScene(dir, track, plainP.Look, new StudioStyle { Fps = 30 }, null, null, lay);
                    var plan = sc.Plan(plainP.Segments, 16, 16 * 30, (0, 15));
                    (double x0, double y0, double x1, double y1) Out((double x, double y, double w, double h) n, double t)
                    {
                        var f = plan[Math.Min((int)Math.Round(t * 30), plan.Length - 1)];
                        (double, double) M(double nx, double ny)
                        {
                            double X = lay.card.X + nx * lay.W * lay.s, Y = lay.card.Y + (1 - ny) * lay.H * lay.s;   // 画布，y 朝上
                            return ((X - f.sx) * f.z + lay.cw / 2, lay.ch - ((Y - f.sy) * f.z + lay.ch / 2));
                        }
                        var (ax, ay) = M(n.x, n.y); var (bx, by) = M(n.x + n.w, n.y + n.h);
                        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
                    }
                    string Fr((double x0, double y0, double x1, double y1) q) => $"({F(q.x0)},{F(q.y0)})-({F(q.x1)},{F(q.y1)})";
                    string Fb(Cmp c) => $"({c.X0},{c.Y0})-({c.X1},{c.Y1})";
                    bool NearR(Cmp c, (double x0, double y0, double x1, double y1) q, double tol) => Math.Abs(c.X0 - q.x0) < tol && Math.Abs(c.Y0 - q.y0) < tol && Math.Abs(c.X1 - q.x1) < tol && Math.Abs(c.Y1 - q.y1) < tol;
                    // 模糊
                    var rb = Out(blurR, 1.5);
                    var c1 = Compare(Grab(uA, 1.5, W, H), Grab(uB, 1.5, W, H), W, H, rb);
                    r.Check("blur: region changed", c1.InMean > 6, $"mean diff {c1.InMean:0.0}");
                    r.Check("blur: less detail inside", c1.GradB < c1.GradA * 0.6, $"detail {c1.GradA:0} → {c1.GradB:0}");
                    r.Check("blur: outside unchanged", c1.OutFrac < 0.002, $"{c1.OutFrac:0.0000}");
                    var c2 = Compare(Grab(uA, 2.9, W, H), Grab(uB, 2.9, W, H), W, H, rb);
                    r.Check("no redaction outside its time", c2.N < 50, $"{c2.N} px");
                    // 推近时的马赛克：跟着画面放大挪动
                    var rm = Out(mos, 11.5); var rmFull = Out(mos, 9.9);
                    r.Check("zoomed redaction is bigger on screen (predicted)", rm.x1 - rm.x0 > (rmFull.x1 - rmFull.x0) * 1.6, Fr(rmFull) + " → " + Fr(rm));
                    var c3 = Compare(Grab(uA, 11.5, W, H), Grab(uB, 11.5, W, H), W, H, rm);
                    r.Check("zoomed mosaic: region changed", c3.InMean > 6, $"mean diff {c3.InMean:0.0}");
                    r.Check("zoomed mosaic: outside unchanged", c3.OutFrac < 0.003, $"{c3.OutFrac:0.0000}");
                    // 格子落在纯色色带上时打码前后一样，所以只要求：变化都在预测框里、左边对得上、占了大半宽度
                    r.Check("zoomed mosaic: changes are inside the zoomed box, left edge matches", c3.X0 >= rm.x0 - 8 && c3.X1 <= rm.x1 + 8 && c3.Y0 >= rm.y0 - 8 && c3.Y1 <= rm.y1 + 8 && Math.Abs(c3.X0 - rm.x0) < 8 && c3.X1 - c3.X0 > (rm.x1 - rm.x0) * 0.6,
                        "actual " + Fb(c3) + " predicted " + Fr(rm));
                    // 框
                    var rr = Out(boxR, 4.0);
                    var c4 = Compare(Grab(uA, 4.0, W, H), Grab(uB, 4.0, W, H), W, H, rr, 12);
                    r.Check("box where it should be", NearR(c4, rr, 14), "actual " + Fb(c4) + " predicted " + Fr(rr));
                    r.Check("inside the box unchanged (outline only)", c4.InMean < 4, $"inside mean diff {c4.InMean:0.0}");
                    var c5 = Compare(Grab(uA, 3.07, W, H), Grab(uB, 3.07, W, H), W, H, rr, 12);
                    r.Check("fade-in: fainter right after it appears", c5.N < c4.N, $"{c5.N} < {c4.N}");
                    var c6 = Compare(Grab(uA, 5.2, W, H), Grab(uB, 5.2, W, H), W, H, rr);
                    r.Check("no annotation outside its time", c6.N < 50, $"{c6.N} px");
                    // 箭头：位置、画出来的过程中短
                    var ra = Out((0.20, 0.50, 0.25, 0.20), 6.5);
                    var c7 = Compare(Grab(uA, 5.6, W, H), Grab(uB, 5.6, W, H), W, H, ra, 40, true);
                    var c8 = Compare(Grab(uA, 6.5, W, H), Grab(uB, 6.5, W, H), W, H, ra, 40, true);
                    r.Check("arrow position", NearR(c8, ra, 26), "actual " + Fb(c8) + " predicted " + Fr(ra));
                    r.Check("arrow draws on: shorter at first", c7.X1 - c7.X0 < (c8.X1 - c8.X0) * 0.75, $"{c7.X1 - c7.X0} < {c8.X1 - c8.X0}");
                    // 文字：左上角在 (0.60, 0.65)
                    var tl = Out((0.60, 0.65, 0.001, 0.001), 9.0);
                    var c9 = Compare(Grab(uA, 9.0, W, H), Grab(uB, 9.0, W, H), W, H, (0, 0, 0, 0));
                    r.Check("text near its top-left corner", Math.Abs(c9.X0 - tl.x0) < 20 && Math.Abs(c9.Y0 - tl.y0) < 20 && c9.X1 - c9.X0 > 40, "box " + Fb(c9) + " top-left " + Fr(tl));
                }

                // ---- 背景音乐：一段 3 秒的 440 Hz（振幅 0.5），2 秒开始、循环、淡入 1 秒、淡出 2 秒；6–7 秒有一段（无声的）讲解 → 压低 ----
                Directory.CreateDirectory(Path.Combine(dir, "music")); Directory.CreateDirectory(Path.Combine(dir, "voice"));
                Studio.RunQuiet($"-v error -y -f lavfi -i \"aevalsrc=0.5*sin(2*PI*440*t):s=48000:d=3\" -ac 2 -c:a pcm_s16le \"{Path.Combine(dir, "music", "tone.wav")}\"", 60000);
                Studio.RunQuiet($"-v error -y -f lavfi -i \"anullsrc=r=48000:cl=mono\" -t 1 -c:a pcm_s16le \"{Path.Combine(dir, "voice", "quiet.wav")}\"", 60000);
                double toneLen = MusicMix.Duration(Path.Combine(dir, "music", "tone.wav"));
                r.Check("music file length read (audio only, no picture)", Math.Abs(toneLen - 3) < 0.05, F(toneLen));
                var mp = Base(); mp.TrimEnd = 12;
                mp.Music.Clips.Add(new MusicClip { File = "music/tone.wav", Name = "tone.wav", Start = 2, TrimIn = 0, TrimOut = 3, FileLength = 3 });
                mp.Music.Volume = 1; mp.Music.FadeIn = 1; mp.Music.FadeOut = 2; mp.Music.Loop = true; mp.Music.Duck = true; mp.Music.DuckDB = -14;
                mp.Voiceovers.Add(new VoiceClip { File = "voice/quiet.wav", Start = 6, TrimIn = 0, TrimOut = 1, FileLength = 1 });
                var uM = Render(mp, "w4-music");
                if (uM != null)
                {
                    var pcm = Studio.DecodePcm(uM, 48000);
                    double Rms(double t0, double t1) { int a = (int)(t0 * 48000), b = Math.Min(pcm.Length, (int)(t1 * 48000)); double s = 0; for (int i = a; i < b; i++) s += pcm[i] * pcm[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
                    double full = 0.5 / Math.Sqrt(2);   // 0.5 振幅正弦的 RMS
                    r.Check("music silent before 2 s", Rms(0.5, 1.8) < 0.002, F(Rms(0.5, 1.8)));
                    r.Check("fade-in: half volume 0.5 s in", Math.Abs(Rms(2.45, 2.55) / full - 0.5) < 0.08, F(Rms(2.45, 2.55) / full));
                    r.Check("full volume after the fade-in", Math.Abs(Rms(3.5, 4.5) / full - 1) < 0.06, F(Rms(3.5, 4.5) / full));
                    r.Check("ducked to -14 dB under the voice-over (6–7 s)", Math.Abs(Rms(6.3, 6.8) / full - 0.1995) < 0.03, F(Rms(6.3, 6.8) / full));
                    r.Check("loops: still playing at 9 s", Rms(8.6, 9.0) / full > 0.8, F(Rms(8.6, 9.0) / full));
                    r.Check("fades out towards the end", Rms(11.4, 11.6) / full < 0.35 && Rms(11.4, 11.6) / full > 0.1, F(Rms(11.4, 11.6) / full));
                    // 限幅：0.9 振幅、150% 音量 → 峰值压在 0.95 以下
                    Studio.RunQuiet($"-v error -y -f lavfi -i \"aevalsrc=0.9*sin(2*PI*440*t):s=48000:d=3\" -ac 2 -c:a pcm_s16le \"{Path.Combine(dir, "music", "loud.wav")}\"", 60000);
                    var lp = Base(); lp.TrimEnd = 6; lp.Music.Clips.Add(new MusicClip { File = "music/loud.wav", Start = 1, TrimOut = 3, FileLength = 3 }); lp.Music.Volume = 1.5; lp.Music.FadeIn = 0; lp.Music.Duck = false;
                    var uL = Render(lp, "w4-loud");
                    if (uL != null)
                    {
                        var lpcm = MusicMix.DecodeStereo(uL); double peak = lpcm.Skip(96000).Take(192000).Max(v => Math.Abs(v));   // 立体声（单声道下混会把两路加起来）
                        r.Check("music at 150% is limited (no clipping)", peak < 0.96 && peak > 0.6, F(peak));
                    }
                }

                // ---- 建议加速：12 秒以后画面静止 → 空档；采纳后导出时长 ----
                var diffs = SpeedSuggest.FrameDiffs(Path.Combine(dir, "raw.mp4"), 0, 16);
                r.Check("frame diffs: moving then still", diffs.Length == 64 && diffs.Take(40).Average() > SpeedSuggest.FrameStill && diffs.Skip(50).Take(12).Max() < SpeedSuggest.FrameStill,
                    $"n={diffs.Length} moving avg {diffs.Take(40).Average():0.0} still max {diffs.Skip(50).Take(12).DefaultIfEmpty(99).Max():0.00}");
                var sug = SpeedSuggest.Find(track, diffs, 0, 16, new List<CutSegment>());
                var idle = sug.FirstOrDefault(g => !g.Typing);
                r.Check("idle suggestion over the still part", idle != null && idle.Start >= 12 && idle.Start < 12.6 && idle.End > 15, idle == null ? "none" : $"{F(idle.Start)}–{F(idle.End)}");
                if (idle != null)
                {
                    var ap = Base(); ap.TrimEnd = 16; ap.Cuts.Add(new CutSegment { Start = idle.Start, End = idle.End, Speed = true, Rate = idle.Rate });
                    var uS = Render(ap, "w4-accepted");
                    if (uS != null)
                    {
                        double want = 16 - (idle.End - idle.Start) * (1 - 1 / idle.Rate), got = Studio.Probe(uS).Duration;
                        r.Check("accepted suggestion: export length", Math.Abs(got - want) < 0.07, $"{F(got)} s, expected {F(want)}");
                    }
                }
            }
            catch (Exception e) { r.Check("exception", false, e.ToString()); }
            finally { Environment.SetEnvironmentVariable("STUDIO_COMPOSE", prevCompose); }
            return r.Done("W4 export" + (compose != null ? " (" + compose + ")" : ""));
        }
    }
}
