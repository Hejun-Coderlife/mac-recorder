// W4 的自测（CI 上跑）：
//   W4TEST=工作文件夹 → 规则（工程字段往返 / Mac 工程、淡入淡出、音乐循环和压低曲线、推近速度、单段覆盖、鼠标回到起点、建议加速）
//       + GDI 文字标注画出字来 + 真导出（CPU 一遍、显卡 WARP 一遍：遮挡、标注、音乐、限幅、采纳建议后的时长）+ 显卡和 CPU 的遮挡 / 标注画面一样
//       + 剪辑窗口真鼠标键盘：在预览上拖出遮挡、拖着挪、撤销 / 重做；点一下放文字、打字回车改字、Ctrl+D 复制；加音乐、在音乐轨上拖；
//         建议加速、点 ✓ 采纳、撤销；关掉存回工程（新字段都在）；顺手留一份 demo 素材夹（work\demo）给截图用；结果写 %TEMP%\w4_test.txt
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using In = ScreenRecorder.SelfTestEditor.In;

namespace ScreenRecorder
{
    static class SelfTestW4
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);
        static void Pump(double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); } }
        static bool PumpUntil(Func<bool> c, double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { if (c()) return true; Application.DoEvents(); Thread.Sleep(10); } return c(); }

        public static int Run()
        {
            var sb = new StringBuilder(); bool ok = true;
            void Add(string res) { sb.Append(res); ok &= res.TrimEnd().EndsWith(" OK"); File.WriteAllText(Tmp("w4_test.txt"), sb.ToString()); }
            void Check(string n, bool c, string info = "") { sb.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; File.WriteAllText(Tmp("w4_test.txt"), sb.ToString()); }
            try
            {
                string work = Env("W4TEST");
                Add(StudioTestsW4.Rules());
                // GDI 文字标注：有字（胶囊上白色的字形像素）、大小跟字号走
                var a = new Annotation { Kind = "text", Text = "测试 Test", FontSize = 32, Color = new RGB(0.04, 0.52, 1) };
                var ts = OverlayGdi.Text(a, 1);
                int bright = 0; for (int i = 0; i < ts.W * ts.H; i++) if (ts.Px[i * 4] > 0.85f && ts.Px[i * 4 + 1] > 0.85f && ts.Px[i * 4 + 2] > 0.85f) bright++;
                Check("GDI text annotation has glyphs (white on the pill)", ts.W > 120 && ts.H > 36 && ts.H < 80 && bright > 80, $"{ts.W}x{ts.H}, {bright} white px");
                a.FontSize = 64; var big = OverlayGdi.Text(a, 1);
                Check("text annotation scales with font size", big.H > ts.H * 1.7 && big.W > ts.W * 1.7, $"{big.W}x{big.H}");
                Add(StudioTestsW4.Export(work, "cpu"));
                Add(StudioTestsW4.Export(work, "gpu"));
                // 显卡和 CPU：同一帧的遮挡 / 标注差不多一样
                string ca = Path.Combine(work, "w4-markedcpu.mp4"), cb = Path.Combine(work, "w4-markedgpu.mp4");
                if (File.Exists(ca) && File.Exists(cb))
                {
                    var info = Studio.Probe(ca);
                    double worst = 0;
                    foreach (var t in new[] { 1.5, 4.0, 6.5, 9.0, 11.5 })
                    {
                        var fa = Studio.DecodeFrame(ca, t, info.Width, info.Height); var fb = Studio.DecodeFrame(cb, t, info.Width, info.Height);
                        double sum = 0; for (int i = 0; i < fa.Length; i += 4) sum += Math.Abs(fa[i] - fb[i]) + Math.Abs(fa[i + 1] - fb[i + 1]) + Math.Abs(fa[i + 2] - fb[i + 2]);
                        worst = Math.Max(worst, sum / (fa.Length / 4 * 3));
                    }
                    Check("GPU and CPU give the same redactions / annotations", worst < 3, $"mean abs diff {worst:0.00}");
                }
                else Check("GPU and CPU marked exports exist", false, ca + " / " + cb);
                string src = Path.Combine(work, "srccpu"), demo = Path.Combine(work, "demo");
                if (Directory.Exists(demo)) Directory.Delete(demo, true);
                Directory.CreateDirectory(demo);
                foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(demo, Path.GetFileName(f)));
                try { File.Delete(StudioProject.PathIn(demo)); } catch { }
                Directory.CreateDirectory(Path.Combine(demo, "music"));
                foreach (var f in Directory.GetFiles(Path.Combine(src, "music"))) File.Copy(f, Path.Combine(demo, "music", Path.GetFileName(f)));
                string tone = Path.Combine(src, "music", "tone.wav");
                Editor(demo, tone, Check);
            }
            catch (Exception e) { sb.AppendLine("FAIL exception " + e); ok = false; }
            File.WriteAllText(Tmp("w4_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        static void Editor(string dir, string tone, Action<string, bool, string> Check)
        {
            StudioEditor.TestMode = false;   // 要真的存工程
            var ed = new StudioEditor(dir) { StartPosition = FormStartPosition.Manual, Location = new Point(10, 10), TopMost = true };
            ed.ClientSize = new Size(1280, 860);
            ed.Show(); ed.Activate();
            Check("first preview frame", PumpUntil(() => ed.FirstFrameShown, 30), "");
            var pv = ed.Preview; var tl = ed.Timeline;
            Point PV(double vx, double vy) { var r = pv.ImageRect; return pv.PointToScreen(new Point((int)Math.Round(r.X + vx * r.Width), (int)Math.Round(r.Y + vy * r.Height))); }
            Point TL(PointF p) => tl.PointToScreen(new Point((int)Math.Round(p.X), (int)Math.Round(p.Y)));
            void Drag(Point a, Point b, int steps = 12)
            {
                In.Move(a); Pump(0.15); In.Down(); Pump(0.08);
                for (int i = 1; i <= steps; i++) { In.Move(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps)); Pump(0.03); }
                Pump(0.05); In.Up(); Pump(0.4);
            }
            ed.Seek(1.0); Pump(1.0);
            Check("preview picture laid out", pv.ImageRect.Width > 200, $"{pv.ImageRect.Width:0}x{pv.ImageRect.Height:0}");

            // 1) 遮挡：选工具 → 在预览上拖出来
            ed.StartTool("redact"); Pump(0.2);
            var (n0x, n0y) = ed.FromView(0.30, 0.30); var (n1x, n1y) = ed.FromView(0.55, 0.50);
            Drag(PV(0.30, 0.30), PV(0.55, 0.50));
            var r = ed.Project.Redactions.FirstOrDefault();
            Check("drag out a redaction on the preview", r != null && Math.Abs(r.X - n0x) < 0.02 && Math.Abs(r.Y - n0y) < 0.02 && Math.Abs(r.X + r.W - n1x) < 0.02 && Math.Abs(r.Y + r.H - n1y) < 0.02 && ed.SelectedRed == r.Id && ed.Tool == null,
                r == null ? "none" : $"{r.X:0.000},{r.Y:0.000} {r.W:0.000}x{r.H:0.000} want {n0x:0.000},{n0y:0.000}-{n1x:0.000},{n1y:0.000}");
            Check("redaction starts at the playhead and lasts 3 s", r != null && Math.Abs(r.Start - 1.0) < 0.1 && Math.Abs(r.End - r.Start - 3) < 0.1, r == null ? "" : $"{r.Start:0.00}-{r.End:0.00}");
            // 拖中间挪一挪（一步撤销）
            if (r != null)
            {
                double x0 = r.X;
                Drag(PV(0.42, 0.40), PV(0.52, 0.40));
                var r2 = ed.Project.Redactions.FirstOrDefault();
                var (m0, _) = ed.FromView(0.42, 0.40); var (m1, _) = ed.FromView(0.52, 0.40);
                Check("drag the redaction to move it", r2 != null && Math.Abs(r2.X - (x0 + m1 - m0)) < 0.02 && Math.Abs(r2.W - r.W) < 1e-6, r2 == null ? "" : $"x {x0:0.000} → {r2.X:0.000}");
                In.Combo(0x11, 0x5A); Pump(0.3);   // Ctrl+Z
                Check("undo the move", Math.Abs((ed.Project.Redactions.FirstOrDefault()?.X ?? -1) - x0) < 1e-6, "");
                In.Combo(0x11, 0x5A); Pump(0.3);
                Check("undo the redaction", ed.Project.Redactions.Count == 0, "");
                In.Combo(0x11, 0x59); Pump(0.3);   // Ctrl+Y
                Check("redo brings it back", ed.Project.Redactions.Count == 1, "");
            }
            // 预览里看得到：画在原画上（遮挡区域变了、外面没变）
            ed.Seek(1.5); Pump(1.0);

            // 2) 文字标注：点一下 → 属性条里打字、回车；双击再改；Ctrl+D 复制
            ed.Seek(5.0); Pump(0.6);
            ed.StartTool("text"); Pump(0.2);
            In.Move(PV(0.20, 0.70)); Pump(0.15); In.Click(); Pump(0.6);
            var t = ed.Project.Annotations.FirstOrDefault();
            Check("click places a text annotation and opens its text box", t != null && t.Kind == "text" && ed.EditingAnnText, t == null ? "none" : $"editing={ed.EditingAnnText}");
            foreach (ushort vk in new ushort[] { 0x48, 0x45, 0x4C, 0x4C, 0x4F }) { In.Key(vk); Pump(0.04); }   // HELLO
            In.Key(0x0D); Pump(0.4);
            t = ed.Project.Annotations.FirstOrDefault();
            Check("typed text is saved on Enter", t != null && t.Text.Equals("hello", StringComparison.OrdinalIgnoreCase), t?.Text ?? "");
            if (t != null)
            {
                // 双击文字再改
                var bx = OverlayLayer.NormBounds(t, ed.Track.Width, ed.Track.Height, ed.Track.PixelsPerPoint);
                var (vx, vy) = ed.ToView(bx.x + bx.w / 2, bx.y + bx.h / 2);
                ed.Select(null, null); Pump(0.2);
                In.Move(PV(vx, vy)); Pump(0.15); In.Click(); Thread.Sleep(40); In.Click(); Pump(0.5);
                Check("double-click the text to edit it", ed.SelectedAnn == t.Id && ed.EditingAnnText, $"selected={ed.SelectedAnn == t.Id} editing={ed.EditingAnnText}");
                foreach (ushort vk in new ushort[] { 0x48, 0x49 }) { In.Key(vk); Pump(0.04); }   // HI
                In.Key(0x0D); Pump(0.4);
                Check("edited text", ed.Project.Annotations.FirstOrDefault()?.Text.Equals("hi", StringComparison.OrdinalIgnoreCase) == true, ed.Project.Annotations.FirstOrDefault()?.Text ?? "");
                ed.Select("ann", t.Id); Pump(0.2);
                In.Combo(0x11, 0x44); Pump(0.4);   // Ctrl+D
                Check("Ctrl+D duplicates the annotation", ed.Project.Annotations.Count == 2 && ed.SelectedAnn != t.Id, $"{ed.Project.Annotations.Count}");
            }
            // 箭头：从尾拖到头
            ed.StartTool("arrow"); Pump(0.2);
            Drag(PV(0.60, 0.70), PV(0.75, 0.45));
            var ar = ed.Project.Annotations.LastOrDefault();
            Check("drag out an arrow (tail → head)", ar != null && ar.Kind == "arrow" && ar.W > 0.05 && ar.H < -0.05, ar == null ? "" : $"{ar.W:0.000},{ar.H:0.000}");

            // 3) 背景音乐：加一首、在音乐轨上往右拖
            var clip = ed.AddMusic(tone); Pump(0.8);
            Check("add music: copied into music\\ and placed on the music lane", clip != null && File.Exists(Path.Combine(dir, clip.File.Replace('/', '\\'))) && Math.Abs(clip.FileLength - 3) < 0.1 && ed.SelectedMusic == clip.Id,
                clip == null ? "none" : $"{clip.File} {clip.FileLength:0.00}s at {clip.Start:0.00}");
            if (clip != null)
            {
                double s0 = ed.Project.Music.Clips[0].Start;
                var c = tl.MusicAt(clip.Id);
                float dx = tl.X(2.0) - tl.X(0);
                Drag(TL(c), TL(new PointF(c.X + dx, c.Y)));
                double s1 = ed.Project.Music.Clips[0].Start;
                Check("drag the music block right by ~2 s", Math.Abs(s1 - s0 - 2) < 0.25, $"{s0:0.00} → {s1:0.00}");
            }

            // 4) 建议加速：12 秒后画面静止 → 一个空档建议；点 ✓ 采纳，撤销
            ed.SuggestSpeed();
            PumpUntil(() => !ed.Suggesting, 60); Pump(0.4);
            var sg = ed.Suggestions.FirstOrDefault(x => !x.Typing);
            Check("suggest speed-ups finds the still part", sg != null && sg.Start >= 11.9 && sg.End > 14.5, sg == null ? $"{ed.Suggestions.Count}" : $"{sg.Start:0.00}-{sg.End:0.00}");
            if (sg != null)
            {
                int cuts = ed.Project.Cuts.Count;
                In.Move(TL(tl.SugAcceptAt(sg.Id))); Pump(0.2); In.Click(); Pump(0.5);
                var cut = ed.Project.Cuts.FirstOrDefault(x => x.Speed && Math.Abs(x.Start - sg.Start) < 0.01);
                Check("click ✓ accepts it as a 4× speed-up", cut != null && cut.Rate == 4 && ed.Project.Cuts.Count == cuts + 1 && !ed.Suggestions.Any(x => x.Id == sg.Id), $"{ed.Project.Cuts.Count} cuts");
                ed.Select(null, null); ed.Timeline.Focus(); Pump(0.2);
                In.Combo(0x11, 0x5A); Pump(0.3);
                Check("undo the accepted suggestion", ed.Project.Cuts.Count == cuts, $"{ed.Project.Cuts.Count}");
                In.Combo(0x11, 0x59); Pump(0.3);
            }
            // 5) 推近速度、鼠标回到起点
            ed.EditLook(l => { l.ZoomSpeed = 1.7; l.CursorLoop = true; l.CursorLoopSec = 1.5; }); Pump(0.3);
            if (ed.Project.Segments.Count > 0) { ed.Select("seg", ed.Project.Segments[0].Id); ed.SetSegSpeed(0.6); Pump(0.2); }

            // 6) 关掉存回工程，新字段都在
            ed.Close(); Pump(0.8);
            var saved = StudioProject.FromJson(File.ReadAllText(StudioProject.PathIn(dir)));
            Check("project saved with redactions / annotations / music / cut", saved.Redactions.Count == 1 && saved.Annotations.Count == 3 && saved.Music.Clips.Count == 1 && saved.Cuts.Any(x => x.Speed && x.Rate == 4),
                $"red {saved.Redactions.Count} ann {saved.Annotations.Count} music {saved.Music.Clips.Count} cuts {saved.Cuts.Count}");
            Check("project saved with zoom speed + cursor loop", Math.Abs(saved.Look.ZoomSpeed - 1.7) < 1e-6 && saved.Look.CursorLoop && Math.Abs(saved.Look.CursorLoopSec - 1.5) < 1e-6 && (saved.Segments.Count == 0 || saved.Segments[0].Speed == 0.6), "");
            var json = File.ReadAllText(StudioProject.PathIn(dir));
            Check("saved JSON uses the Mac field names", new[] { "\"redactions\"", "\"annotations\"", "\"music\"", "\"zoomSpeed\"", "\"cursorLoop\"", "\"cursorLoopSec\"", "\"duckDB\"", "\"trimIn\"" }.All(k => json.Contains(k)), "");
            // 截图用的工程：留着（截图自己再摆）
            try { File.Delete(StudioProject.PathIn(dir)); } catch { }
        }
    }
}
