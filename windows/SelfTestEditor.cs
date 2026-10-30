// Studio 剪辑窗口相关的自测（CI 上跑）：
//   STUDIOTESTS=1 [STUDIOTESTS_DIR=素材夹] [MACPROJECT=Mac 写的 project.json] → 纯逻辑的几组（见 StudioTests.cs），结果写 %TEMP%\studio_tests.txt
//   EDITORSHOT=png路径 EDITOR=素材夹 [EDITOR_WIDTH=1440] [EDITOR_SELECT=1 选中第一段] [EDITOR_TLZOOM=倍数] [EDITOR_SECTION=wallpaper|gradient …] → 把剪辑窗口画成图后退出
//   EDITORTEST=素材夹 → 真鼠标键盘（SendInput）操作剪辑窗口：拖修剪把手（不回跳）、撤销、双击加推近段、空格播放、Ctrl+滚轮缩放、导出；结果写 %TEMP%\editor_test.txt
//   COUNTDOWNSHOT=png路径 → 3 秒倒计时窗口画成图，再验证 Esc 取消；结果写 %TEMP%\countdown_test.txt
//   ICONSTEST=1 → 藏桌面图标、模拟崩溃没换回来、下次打开时恢复；结果写 %TEMP%\icons_test.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class SelfTestEditor
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);

        static void Pump(double secs)
        {
            var until = DateTime.Now.AddSeconds(secs);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); }
        }

        static bool PumpUntil(Func<bool> cond, double secs)
        {
            var until = DateTime.Now.AddSeconds(secs);
            while (DateTime.Now < until) { if (cond()) return true; Application.DoEvents(); Thread.Sleep(10); }
            return cond();
        }

        public static int Core()
        {
            var sb = new StringBuilder(); bool ok = true;
            try
            {
                string dir = Env("STUDIOTESTS_DIR"), mac = Env("MACPROJECT");
                foreach (var res in new[] { StudioTests.Plan(dir), StudioTests.Project(mac != null && File.Exists(mac) ? File.ReadAllText(mac) : null), StudioTests.Scrub(), StudioTests.Presets(), StudioTests.Looks(Studio.LoadCover), StudioTests.Cuts() })
                { sb.Append(res); ok &= res.TrimEnd().EndsWith(" OK"); }
                if (dir != null) { var b = StudioTests.Blur(dir); sb.Append(b); ok &= b.TrimEnd().EndsWith(" OK"); }
            }
            catch (Exception e) { sb.AppendLine("FAIL exception " + e); ok = false; }
            File.WriteAllText(Tmp("studio_tests.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        // ---- 剪辑窗口截图 ----
        public static int Shot()
        {
            try
            {
                StudioEditor.TestMode = true;
                var ed = new StudioEditor(Env("EDITOR"));
                int w = int.TryParse(Env("EDITOR_WIDTH"), out var ww) ? ww : 1440;
                ed.StartPosition = FormStartPosition.Manual; ed.Location = new Point(0, 0);
                ed.ClientSize = new Size(w, w * 10 / 16);
                ed.Show();
                PumpUntil(() => ed.FirstFrameShown, 30);
                if (Env("EDITOR_SELECT") == "1" && ed.Project.Segments.Count > 0) { ed.Selected = ed.Project.Segments[0].Id; ed.Touch(); }
                if (double.TryParse(Env("EDITOR_TLZOOM"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) ed.SetTimelineScale(z, ed.Timeline.ViewWidth * 0.6);
                if (Env("EDITOR_LOOK") is string lj) { var nl = StudioLook.FromJson(lj); ed.Edit(p => p.Look = nl); Pump(2); }
                // EDITOR_DEMOCUTS=1：放一段剪掉、一段 4× 快放，选中快放那段；声音分组滚到顶上
                if (Env("EDITOR_DEMOCUTS") == "1")
                {
                    double d = ed.Duration;
                    ed.Edit(p => { p.Cuts.Add(new CutSegment { Start = d * 0.2, End = d * 0.3 }); p.Cuts.Add(new CutSegment { Start = d * 0.5, End = d * 0.65, Speed = true, Rate = 4 }); p.Audio.System = 1.4; p.Look.ClickSound = true; });
                    ed.SelectedCut = ed.Project.Cuts[1].Id; ed.Selected = null; ed.Touch();
                    ed.ScrollStyleTo(App.L("声音", "Sound"));
                    Pump(2);
                }
                // EDITOR_W3DEMO=keys|split|camera：快捷键键帽 / 分屏布局（选中）/ 摄像头小窗，样式面板滚到对应分组
                if (Env("EDITOR_W3DEMO") is string w3)
                {
                    ed.Edit(p => { p.Look.ShowKeys = true; if (ed.HasCamera) { p.Layouts.Clear(); p.Layouts.Add(new CameraLayoutSeg { Start = 5, End = 7, Mode = "split" }); p.Layouts.Add(new CameraLayoutSeg { Start = 8, End = 9, Mode = "hidden" }); } });
                    if (w3 == "keys") { ed.Seek(1.3); ed.ScrollStyleTo(App.L("快捷键", "Shortcuts")); }
                    else if (w3 == "split") { ed.Select("layout", ed.Project.Layouts.FirstOrDefault()?.Id); ed.Seek(6.0); ed.ScrollStyleTo(App.L("摄像头", "Camera")); }
                    else { ed.Seek(3.0); ed.ScrollStyleTo(App.L("摄像头", "Camera")); }
                    Pump(3);
                }
                // EDITOR_W4DEMO=overlays|music|suggest：遮挡 + 标注（选中文字）/ 背景音乐轨（选中，声音分组）/ 建议加速的虚线块
                if (Env("EDITOR_W4DEMO") is string w4)
                {
                    ed.Edit(p =>
                    {
                        p.Redactions.Add(new Redaction { Start = 0.5, End = 4, X = 0.08, Y = 0.12, W = 0.3, H = 0.22, Style = "blur", Strength = 0.8 });
                        p.Redactions.Add(new Redaction { Start = 2, End = 6, X = 0.62, Y = 0.62, W = 0.25, H = 0.2, Style = "mosaic", Strength = 0.6 });
                        p.Annotations.Add(new Annotation { Start = 1, End = 5, Kind = "rect", X = 0.45, Y = 0.15, W = 0.3, H = 0.3 });
                        p.Annotations.Add(new Annotation { Start = 2, End = 6, Kind = "arrow", X = 0.2, Y = 0.75, W = 0.22, H = -0.2, Color = Annotation.Palette[4] });
                        p.Annotations.Add(new Annotation { Start = 2.5, End = 7, Kind = "text", X = 0.45, Y = 0.5, Text = App.L("点这里", "Click here"), FontSize = 30, Color = Annotation.Palette[1] });
                        p.Annotations.Add(new Annotation { Start = 8, End = 10, Kind = "highlight", X = 0.3, Y = 0.3, W = 0.4, H = 0.1, Color = Annotation.Palette[2] });
                        if (w4 == "music" && File.Exists(Path.Combine(ed.Dir, "music", "tone.wav")))
                            p.Music.Clips.Add(new MusicClip { File = "music/tone.wav", Name = "tone", Start = 2, TrimIn = 0, TrimOut = 3, FileLength = 3 });
                    });
                    if (w4 == "overlays") { ed.Select("ann", ed.Project.Annotations[2].Id); ed.Seek(3.0); ed.ScrollStyleTo(App.L("推近", "Zoom")); }
                    else if (w4 == "music") { ed.Select("music", ed.Project.Music.Clips.FirstOrDefault()?.Id); ed.Seek(3.0); ed.ScrollStyleTo(App.L("声音", "Sound")); }
                    else { ed.Seek(2.2); ed.SuggestSpeed(); PumpUntil(() => !ed.Suggesting, 60); ed.ScrollStyleTo(App.L("鼠标", "Cursor")); }
                    Pump(3);
                }
                // EDITOR_W5DEMO=clips|lanes：剪映式片段（分割、2× 快放、删掉一段留剪刀）+ 打字声轨（选中一阵）/ 叠在一起的遮挡标注各占一行 + 预览放大
                if (Env("EDITOR_W5DEMO") is string w5)
                {
                    double d = Math.Min(ed.Duration, ed.Project.TrimEnd);
                    if (ed.Track.Keys.Count == 0) ed.Track.Keys.AddRange(new[] { d * 0.12, d * 0.14, d * 0.16, d * 0.19, d * 0.6, d * 0.62, d * 0.64, d * 0.67, d * 0.7 });
                    if (w5 == "clips")
                    {
                        ed.Split(d * 0.25); ed.Split(d * 0.4); ed.Split(d * 0.55); ed.Split(d * 0.75);
                        var pcs = ed.PieceList;
                        ed.SetPieceSpeed(pcs.First(x => Math.Abs(x.Start - d * 0.55) < 0.05), 2);
                        ed.DeletePiece(ed.PieceList.First(x => Math.Abs(x.Start - d * 0.25) < 0.05));
                        var b = Pieces.Bursts(ed.Track, ed.Project).LastOrDefault();
                        if (b != null) { ed.Edit(p => Pieces.SetLookBool(p, "typeSound", true)); ed.SelectBurst(b.Start); }
                        ed.Seek(d * 0.6);
                    }
                    else
                    {
                        ed.Edit(p =>
                        {
                            p.Redactions.Add(new Redaction { Start = 0.5, End = 4, X = 0.08, Y = 0.12, W = 0.3, H = 0.22, Style = "blur", Strength = 0.8 });
                            p.Redactions.Add(new Redaction { Start = 2, End = 6, X = 0.62, Y = 0.62, W = 0.25, H = 0.2, Style = "mosaic", Strength = 0.6 });
                            for (int i = 0; i < 4; i++) p.Annotations.Add(new Annotation { Start = 1 + i * 0.6, End = 4 + i * 0.6, Kind = i % 2 == 0 ? "rect" : "text", X = 0.1 + i * 0.15, Y = 0.3, W = 0.12, H = 0.12, Text = "Hi " + i, FontSize = 28 });
                        });
                        ed.Seek(3.0); Pump(1);
                        ed.Preview.SetZoom(2f, new Point(ed.Preview.Width / 2, ed.Preview.Height / 2));
                    }
                    ed.ScrollStyleTo(App.L("推近", "Zoom"));
                    Pump(3);
                }
                Pump(1.5);
                using (var bmp = new Bitmap(ed.Width, ed.Height))
                {
                    ed.DrawToBitmap(bmp, new Rectangle(0, 0, ed.Width, ed.Height));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Env("EDITORSHOT"))));
                    bmp.Save(Env("EDITORSHOT"), ImageFormat.Png);
                }
                File.WriteAllText(Tmp("editorshot.txt"), $"OK firstFrame={ed.FirstFrameShown} segments={ed.Project.Segments.Count}");
                ed.Close();
                return ed.FirstFrameShown ? 0 : 1;
            }
            catch (Exception e) { File.WriteAllText(Tmp("editorshot.txt"), "FAIL " + e); return 1; }
        }

        // ---- 真鼠标键盘操作剪辑窗口 ----
        public static int Interactive()
        {
            var log = new StringBuilder(); bool ok = true;
            void Check(string n, bool c, string info = "") { log.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; }
            StudioEditor ed = null;
            try
            {
                var dir = Env("EDITORTEST");
                try { File.Delete(StudioProject.PathIn(dir)); } catch { }
                ed = new StudioEditor(dir, Path.Combine(Path.GetTempPath(), "editor-export.mp4"));
                ed.StartPosition = FormStartPosition.Manual; ed.Location = new Point(10, 10); ed.ClientSize = new Size(1240, 760);
                ed.TopMost = true;
                ed.Show(); ed.Activate();
                Check("first preview frame", PumpUntil(() => ed.FirstFrameShown, 30));
                var tl = ed.Timeline;
                Point Screen(float x, float y) => tl.PointToScreen(new Point((int)Math.Round(x), (int)Math.Round(y)));
                double d = ed.Duration;

                // 1) 拖结尾修剪把手往左 30%：拖动中 trimEnd 只减不增（不回跳），松手后算一步撤销
                double before = ed.Project.TrimEnd;
                float hx = tl.X(before) - 2, hy = tl.ClientSize.Height / 2f;   // 结尾把手的竖杠在修剪点左边
                In.Move(Screen(hx, hy)); Pump(0.2); In.Down(); Pump(0.05);
                var seen = new List<double>();
                int steps = 30; float dx = -tl.ClientSize.Width * 0.3f;
                for (int i = 1; i <= steps; i++) { In.Move(Screen(hx + dx * i / steps, hy)); Pump(0.03); seen.Add(ed.Project.TrimEnd); }
                In.Up(); Pump(0.3);
                bool mono = seen.Zip(seen.Skip(1), (a, b) => b <= a + 1e-9).All(x => x);
                Check("trim drag moves the end", ed.Project.TrimEnd < before - d * 0.15, $"{before:0.00} -> {ed.Project.TrimEnd:0.00}");
                Check("trim drag never jumps back", mono, string.Join(",", seen.Select(v => v.ToString("0.00"))));
                Check("playhead parked on trim end", Math.Abs(ed.Time - ed.Project.TrimEnd) < 0.2, $"time {ed.Time:0.00}");
                double trimmed = ed.Project.TrimEnd;
                In.Combo(0x11, 0x5A); Pump(0.3);   // Ctrl+Z
                Check("Ctrl+Z restores trim", Math.Abs(ed.Project.TrimEnd - before) < 1e-6, $"{ed.Project.TrimEnd:0.00}");
                In.Combo(0x11, 0x59); Pump(0.3);   // Ctrl+Y
                Check("Ctrl+Y redoes trim", Math.Abs(ed.Project.TrimEnd - trimmed) < 1e-6);

                // 2) 时间轴空白处双击：加一段推近
                int segs = ed.Project.Segments.Count;
                float gapX = -1;
                for (float x = tl.X(ed.Project.TrimStart) + 20; x < tl.X(ed.Project.TrimEnd) - 20; x += 4)
                {
                    double t = (x + tl.Offset) / (tl.ViewWidth * tl.Scale) * d;
                    if (!ed.Project.Segments.Any(s => t >= s.Start - 1.2 && t <= s.End + 1.2)) { gapX = x; break; }
                }
                if (gapX < 0) { ed.Edit(p => p.Segments.Clear()); Pump(0.3); segs = 0; gapX = tl.X((ed.Project.TrimStart + ed.Project.TrimEnd) / 2); }
                In.Move(Screen(gapX, 60)); Pump(0.15); In.Click(); Pump(0.05); In.Click(); Pump(0.5);
                Check("double-click adds a zoom", ed.Project.Segments.Count == segs + 1, $"{segs} -> {ed.Project.Segments.Count}");
                Check("new zoom selected", ed.Selected != null && ed.Project.Segments.Any(s => s.Id == ed.Selected));

                // 3) 拖新加那段的右边把手往右，再撤销
                var sg = ed.Project.Segments.FirstOrDefault(s => s.Id == ed.Selected);
                if (sg != null)
                {
                    double end0 = sg.End; float ex = Math.Max(tl.X(sg.Start) + 6, tl.X(sg.End)) - 4;
                    In.Move(Screen(ex, 86)); Pump(0.15); In.Down();
                    for (int i = 1; i <= 10; i++) { In.Move(Screen(ex + 4 * i, 86)); Pump(0.03); }
                    In.Up(); Pump(0.3);
                    var sg2 = ed.Project.Segments.First(s => s.Id == sg.Id);
                    Check("drag zoom edge", sg2.End > end0 + 0.05, $"{end0:0.00} -> {sg2.End:0.00}");
                    Check("project marked edited", ed.Project.SegmentsEdited);
                }

                // 3b) 剪映式剪辑：剪辑轨上双击 = 分割；播放头处按 S 再切一刀；点选中间那段按 Delete 删掉（时间轴合拢）；
                //     Ctrl+Z / Ctrl+Y；播放穿过删掉的段；点小剪刀按 Delete 恢复
                double ta = ed.Project.TrimStart + (ed.Project.TrimEnd - ed.Project.TrimStart) * 0.3, tb = ed.Project.TrimStart + (ed.Project.TrimEnd - ed.Project.TrimStart) * 0.45;
                int splits0 = ed.Project.Splits.Count;
                In.Move(Screen(tl.X(ta), 44)); Pump(0.15); In.Click(); Pump(0.05); In.Click(); Pump(0.5);
                Check("double-click on the clip lane splits there", ed.Project.Splits.Count == splits0 + 1 && ed.SelectedPiece != null, $"{splits0} -> {ed.Project.Splits.Count}");
                ed.Seek(tb); Pump(0.3);
                In.Key(0x53); Pump(0.4);   // S
                Check("S splits at the playhead", ed.Project.Splits.Count == splits0 + 2 && ed.Project.Splits.Any(x => Math.Abs(x - tb) < 0.05), string.Join(",", ed.Project.Splits.Select(x => x.ToString("0.00"))));
                var mid = ed.PieceList.FirstOrDefault(x => x.Start > ta - 0.1 && x.End < tb + 0.1 && x.Start < tb);
                In.Move(Screen((tl.X(mid.Start) + tl.X(mid.End)) / 2, 44)); Pump(0.15); In.Click(); Pump(0.4);
                Check("click selects the clip", ed.SelectedPiece is double spc && Math.Abs(spc - mid.Start) < 0.05, $"{ed.SelectedPiece}");
                In.Key(0x2E); Pump(0.5);   // Delete
                var removed = ed.Project.Cuts.FirstOrDefault(c => !c.Speed && Math.Abs(c.Start - mid.Start) < 0.05);
                Check("Delete removes the clip (a cut)", removed != null && Math.Abs(removed.End - mid.End) < 0.05, string.Join(" ", ed.Project.Cuts.Select(c => $"{(c.Speed ? "speed" : "cut")}[{c.Start:0.00},{c.End:0.00}]")));
                Check("timeline closes up", removed != null && Math.Abs(tl.X(removed.End) - tl.X(removed.Start)) < 1.5, removed == null ? "" : $"{tl.X(removed.Start):0.0} {tl.X(removed.End):0.0}");
                In.Combo(0x11, 0x5A); Pump(0.3);
                Check("Ctrl+Z brings the clip back", !ed.Project.Cuts.Any(c => !c.Speed));
                In.Combo(0x11, 0x59); Pump(0.3);
                var cut = ed.Project.Cuts.FirstOrDefault(c => !c.Speed);
                Check("Ctrl+Y removes it again", cut != null);
                if (cut != null)
                {
                    // 播放穿过删掉的段：播放头不会停在删掉的段里面
                    ed.Seek(Math.Max(ed.Project.TrimStart, cut.Start - 0.6)); Pump(0.5);
                    var times = new List<double>();
                    In.Key(0x20);
                    var until = DateTime.Now.AddSeconds(2.2);
                    while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(15); if (ed.Playing) times.Add(ed.Time); }
                    In.Key(0x20); Pump(0.3);
                    bool skipped = times.Count > 5 && times.All(t => t < cut.Start + 0.02 || t >= cut.End - 0.02) && times.Max() > cut.End;
                    Check("playback skips the removed clip", skipped, $"cut {cut.Start:0.00}-{cut.End:0.00}, saw {times.FirstOrDefault():0.00}..{(times.Count > 0 ? times.Max() : 0):0.00} ({times.Count} ticks)");
                    // 小剪刀：点选 → 属性条「恢复」；按 Delete 恢复，再 Ctrl+Z 删回去（后面的检查要用到剪掉段）
                    In.Move(Screen(tl.X(cut.Start), 44)); Pump(0.15); In.Click(); Pump(0.4);
                    Check("click the scissors mark selects the removed part", ed.SelectedCut == cut.Id, $"{ed.SelectedCut}");
                    In.Key(0x2E); Pump(0.4);
                    Check("Delete on the scissors mark restores it", !ed.Project.Cuts.Any(c => !c.Speed));
                    In.Combo(0x11, 0x5A); Pump(0.3);
                    Check("undo the restore", ed.Project.Cuts.Any(c => !c.Speed));
                }
                // 3c) 改音量：预览声音在后台重做
                int ready = ed.AudioReady;
                ed.Edit(p => p.Audio.System = 0.5);
                Check("preview audio rebuilt after a volume change", PumpUntil(() => ed.AudioReady > ready, 30), $"ready {ready} -> {ed.AudioReady}");

                // 4) 空格播放 1 秒，播放头往前走；再按停
                ed.Seek(ed.Project.TrimStart); Pump(0.5);
                double t0 = ed.Time;
                In.Key(0x20); Pump(1.2);
                Check("space starts playback", ed.Playing && ed.Time > t0 + 0.4, $"time {t0:0.00} -> {ed.Time:0.00}");
                In.Key(0x20); Pump(0.3);
                Check("space pauses", !ed.Playing);
                // ← → 逐帧
                double t1 = ed.Time; In.Key(0x27); Pump(0.3);
                Check("right arrow steps one frame", Math.Abs(ed.Time - (Math.Round(t1 * 60) + 1) / 60) < 1e-6, $"{t1:0.000} -> {ed.Time:0.000}");

                // 5) Ctrl + 滚轮在时间轴上：放大
                In.Move(Screen(tl.ClientSize.Width * 0.6f, 60)); Pump(0.1);
                In.KeyDown(0x11); for (int i = 0; i < 5; i++) { In.Wheel(120); Pump(0.05); } In.KeyUp(0x11); Pump(0.3);
                Check("ctrl+wheel zooms the timeline", tl.Scale > 1.5, $"scale {tl.Scale:0.00}");
                double off = tl.Offset; In.Wheel(-240); Pump(0.3);
                Check("wheel pans when zoomed", Math.Abs(tl.Offset - off) > 1, $"offset {off:0} -> {tl.Offset:0}");

                // 6) 样式：切到「渐变」，工程里变了，预览重画
                ed.EditLook(l => { l.Background = "gradient"; l.Aspect = "1:1"; }); Pump(1.5);
                Check("style change goes into the project", ed.Project.Look.Background == "gradient" && ed.Project.Look.Aspect == "1:1");

                // 7) 导出：成片时长 = 修剪后的长度、1:1
                ed.Export();
                bool done = PumpUntil(() => !ed.Exporting, 180);
                var outFile = Directory.GetFiles(Path.GetTempPath(), "editor-export*.mp4").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                Check("export finished", done && outFile != null, ed.ExportStatusText);
                if (outFile != null)
                {
                    var info = Studio.Probe(outFile);
                    double want = new TimeMap(ed.Project).Out(ed.Project.TrimEnd) - ed.Project.TrimStart;   // 剪掉的不算
                    Check("export length = trimmed length", Math.Abs(info.Duration - want) < 0.15, $"{info.Duration:0.00} vs {want:0.00}");
                    Check("export is 1:1", Math.Abs(info.Width - info.Height) <= 2, $"{info.Width}x{info.Height}");
                    log.AppendLine("export " + outFile);
                }
                // 关窗口时存工程
                ed.Close(); Pump(0.5);
                var saved = StudioProject.FromJson(File.ReadAllText(StudioProject.PathIn(dir)));
                Check("project.json saved on close", saved.Look.Background == "gradient" && saved.SegmentsEdited);
                Check("cuts and audio saved", saved.Cuts.Count >= 1 && Math.Abs(saved.Audio.System - 0.5) < 1e-9, $"cuts {saved.Cuts.Count} system {saved.Audio.System}");
            }
            catch (Exception e) { log.AppendLine("FAIL exception " + e); ok = false; }
            finally { try { ed?.Close(); } catch { } }
            File.WriteAllText(Tmp("editor_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + log);
            return ok ? 0 : 1;
        }

        // ---- 主窗口截图（最近的 Studio 素材）：MAINSHOT=图片路径 ----
        public static int MainShot()
        {
            try
            {
                var f = new MainForm();
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
                f.Show();
                PumpUntil(() => false, 4);   // 等缩略图在后台做完
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(Env("MAINSHOT"), ImageFormat.Png);
                }
                int n = f.Recents.Count; bool vis = f.Recents.Visible;
                File.WriteAllText(Tmp("mainshot.txt"), $"{(n > 0 && vis ? "OK" : "FAIL")} recents={n} visible={vis} list={string.Join(" | ", RecentStrip.List().Select(Path.GetFileName))}");
                f.Close();
                return n > 0 ? 0 : 1;
            }
            catch (Exception e) { File.WriteAllText(Tmp("mainshot.txt"), "FAIL " + e); return 1; }
        }

        // ---- 倒计时 ----
        public static int Countdown()
        {
            var log = new StringBuilder(); bool ok = true;
            try
            {
                bool done = false, cancelled = false;
                CountdownForm.Run(3, Screen.PrimaryScreen, () => done = true, () => cancelled = true);
                Pump(1.3);
                var f = CountdownForm.Current;
                if (f != null)
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(Env("COUNTDOWNSHOT"), ImageFormat.Png);
                    }
                ok &= f != null;
                PumpUntil(() => done || cancelled, 4);
                log.AppendLine($"run1 done={done} cancelled={cancelled}");
                ok &= done && !cancelled;
                // Esc 取消
                done = cancelled = false;
                CountdownForm.Run(3, Screen.PrimaryScreen, () => done = true, () => cancelled = true);
                Pump(0.6);
                CountdownForm.Current?.Activate(); Pump(0.2);
                In.Key(0x1B);
                PumpUntil(() => done || cancelled, 2);
                if (!cancelled && CountdownForm.Current != null) { log.AppendLine("Esc not delivered, calling Cancel()"); CountdownForm.Current.Cancel(); }
                log.AppendLine($"run2 done={done} cancelled={cancelled}");
                ok &= cancelled && !done;
            }
            catch (Exception e) { log.AppendLine("exception " + e); ok = false; }
            File.WriteAllText(Tmp("countdown_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + log);
            return ok ? 0 : 1;
        }

        // ---- 桌面图标 ----
        public static int Icons()
        {
            var log = new StringBuilder(); bool ok = true;
            try
            {
                if (!DesktopIcons.Available) { File.WriteAllText(Tmp("icons_test.txt"), "SKIP no desktop icon view on this machine"); return 0; }
                Settings.Set("desktopIconsHidden", false);
                bool v0 = DesktopIcons.Visible;
                log.AppendLine("visible at start: " + v0);
                if (!v0) { log.AppendLine("icons were hidden already; showing them first"); Settings.Set("desktopIconsHidden", true); DesktopIcons.Restore(); }
                bool hid = DesktopIcons.Hide();
                log.AppendLine($"hide -> {hid}, visible={DesktopIcons.Visible}, flag={Settings.Get("desktopIconsHidden", false)}");
                ok &= hid && !DesktopIcons.Visible && Settings.Get("desktopIconsHidden", false);
                // 模拟崩溃：不换回来，下次启动（Program 里）调 Restore
                DesktopIcons.Restore();
                log.AppendLine($"restore -> visible={DesktopIcons.Visible}, flag={Settings.Get("desktopIconsHidden", false)}");
                ok &= DesktopIcons.Visible && !Settings.Get("desktopIconsHidden", false);
                // 本来就藏着：不动、也不记
                Settings.Set("desktopIconsHidden", true); DesktopIcons.Restore();
                log.AppendLine("restore twice is harmless: visible=" + DesktopIcons.Visible);
                ok &= DesktopIcons.Visible;
            }
            catch (Exception e) { log.AppendLine("exception " + e); ok = false; }
            File.WriteAllText(Tmp("icons_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + log);
            return ok ? 0 : 1;
        }

        // 真的系统输入（SendInput）
        internal static class In
        {
            public static void Move(Point p) => SetCursorPos(p.X, p.Y);
            public static void Down() => Mouse(0x0002);
            public static void Up() => Mouse(0x0004);
            public static void Click() { Down(); Thread.Sleep(30); Up(); }
            public static void Wheel(int delta) => Mouse(0x0800, delta);
            public static void Key(ushort vk) { KeyDown(vk); KeyUp(vk); }
            public static void KeyDown(ushort vk) => Kbd(vk, 0);
            public static void KeyUp(ushort vk) => Kbd(vk, 2);
            public static void Combo(ushort mod, ushort vk) { KeyDown(mod); Thread.Sleep(20); Key(vk); Thread.Sleep(20); KeyUp(mod); }

            static void Mouse(uint flags, int data = 0)
            {
                var i = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags, mouseData = (uint)data } };
                SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
            }
            static void Kbd(ushort vk, uint flags)
            {
                var i = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
                SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
            }
            [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
            [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
            [StructLayout(LayoutKind.Explicit)] struct INPUT
            {
                [FieldOffset(0)] public uint type;
                [FieldOffset(8)] public MOUSEINPUT mi;
                [FieldOffset(8)] public KEYBDINPUT ki;
            }
            [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        }
    }
}
