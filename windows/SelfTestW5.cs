// W5 的自测（CI 上跑）：
//   W5TEST=工作文件夹 W5SRC=素材夹（W4 留下的 demo）→ 规则（StudioTestsW5：Mac 新字段往返、分割 / 删段 / 跨段快放、可见时间、打字声）
//       + 剪辑窗口真鼠标键盘：点打字声块选中、Delete 去掉 / 再 Delete 恢复、给一阵打字改速度、撤销；
//         叠在一起的标注各占一行（时间轴变高、能上下滚）；一键加速（打字 / 空档）有提示；预览放大 / 适应；
//         分割有提示；关掉存回工程（keyMutes / splits 写进去）；结果写 %TEMP%\w5_test.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using In = ScreenRecorder.SelfTestEditor.In;

namespace ScreenRecorder
{
    static class SelfTestW5
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);
        static void Pump(double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); } }
        static bool PumpUntil(Func<bool> c, double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { if (c()) return true; Application.DoEvents(); Thread.Sleep(10); } return c(); }

        public static int Run()
        {
            var sb = new StringBuilder(); bool ok = true;
            void Add(string res) { sb.Append(res); ok &= res.TrimEnd().EndsWith(" OK"); File.WriteAllText(Tmp("w5_test.txt"), sb.ToString()); }
            void Check(string n, bool c, string info = "") { sb.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; File.WriteAllText(Tmp("w5_test.txt"), sb.ToString()); }
            try
            {
                Add(StudioTestsW5.Rules());
                string work = Env("W5TEST"), src = Env("W5SRC");
                string dir = Path.Combine(work, "w5demo");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                try { File.Delete(StudioProject.PathIn(dir)); } catch { }
                Editor(dir, Check);
            }
            catch (Exception e) { sb.AppendLine("FAIL exception " + e); ok = false; }
            File.WriteAllText(Tmp("w5_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        static void Editor(string dir, Action<string, bool, string> Check)
        {
            StudioEditor.TestMode = false;   // 要真的存工程
            var ed = new StudioEditor(dir) { StartPosition = FormStartPosition.Manual, Location = new Point(10, 10), TopMost = true };
            ed.ClientSize = new Size(1280, 860);
            ed.Show(); ed.Activate();
            Check("first preview frame", PumpUntil(() => ed.FirstFrameShown, 30), "");
            var tl = ed.Timeline; var pv = ed.Preview;
            Point TL(PointF p) => tl.PointToScreen(new Point((int)Math.Round(p.X), (int)Math.Round(p.Y)));
            double d = Math.Min(ed.Duration, ed.Project.TrimEnd);

            // 1) 打字声轨：造两阵打字，点选一阵 → Delete 去掉 → 再 Delete 恢复；改速度 → 撤销
            ed.Track.Keys.Clear();
            ed.Track.Keys.AddRange(new[] { d * 0.10, d * 0.12, d * 0.14, d * 0.16, d * 0.6, d * 0.62, d * 0.64 });
            ed.Edit(p => Pieces.SetLookBool(p, "typeSound", true)); Pump(0.3);
            var bursts = Pieces.Bursts(ed.Track, ed.Project);
            Check("keys grouped into two bursts", bursts.Count == 2, bursts.Count.ToString());
            if (bursts.Count == 2)
            {
                var b = bursts[1];
                In.Move(TL(tl.BurstAt((b.Keys[0] + b.Keys[b.Keys.Count - 1]) / 2))); Pump(0.15); In.Click(); Pump(0.4);
                Check("click selects the typing burst", ed.SelectedBurst is double sb0 && Math.Abs(sb0 - b.Start) < 1e-6, $"{ed.SelectedBurst}");
                In.Key(0x2E); Pump(0.4);
                Check("Delete removes its typing sound", ed.Project.KeyMutes.Count == 1 && !Pieces.BurstOn(ed.Project, b), $"{ed.Project.KeyMutes.Count} mutes");
                Check("toast tells what happened", (ed.LastToast ?? "").Length > 0, ed.LastToast ?? "");
                In.Key(0x2E); Pump(0.4);
                Check("Delete again restores it", ed.Project.KeyMutes.Count == 0 && Pieces.BurstOn(ed.Project, b), "");
                ed.SetBurstRate(b, 3); Pump(0.3);
                Check("burst speed 3×", Math.Abs(Pieces.BurstRate(ed.Project, b) - 3) < 1e-6 && ed.Project.Cuts.Any(c => c.Speed && Math.Abs(c.Rate - 3) < 1e-6), "");
                In.Combo(0x11, 0x5A); Pump(0.3);
                Check("Ctrl+Z undoes the burst speed", !ed.Project.Cuts.Any(c => c.Speed), "");
                ed.SetBurstSound(b, false); Pump(0.2);   // 留一阵静音，关掉后看存没存
            }

            // 2) 叠在一起的标注各占一行：时间轴变高，能上下滚
            int h0 = tl.ContentHeight;
            ed.Edit(p => { for (int i = 0; i < 4; i++) p.Annotations.Add(new Annotation { Start = 1 + i * 0.3, End = 4 + i * 0.3, Kind = "rect", X = 0.1 + i * 0.1, Y = 0.3, W = 0.1, H = 0.1 }); });
            Pump(0.4);
            Check("overlapping annotations get their own rows", tl.ContentHeight >= h0 + 3 * 18, $"{h0} -> {tl.ContentHeight}");
            Check("timeline sized to its content", tl.Height == tl.ContentHeight, $"{tl.Height} vs {tl.ContentHeight}");

            // 3) 分割有提示；片段改速度
            ed.Split(d * 0.3); Pump(0.3);
            Check("split shows a toast", (ed.LastToast ?? "").Contains("0:"), ed.LastToast ?? "");
            ed.Split(d * 0.5); Pump(0.3);
            var pc = ed.PieceList.FirstOrDefault(x => Math.Abs(x.Start - d * 0.3) < 0.05);
            ed.SetPieceSpeed(pc, 2); Pump(0.3);
            Check("piece speed 2×", ed.PieceList.Any(x => Math.Abs(x.Start - d * 0.3) < 0.05 && Math.Abs(x.Rate - 2) < 1e-6), "");
            In.Combo(0x11, 0x5A); Pump(0.3);

            // 4) 一键加速：喂两条「打字」建议，直接加成 3× 快放并提示
            var fake = new List<SpeedSuggestion> { new SpeedSuggestion { Start = d * 0.70, End = d * 0.78, Typing = true }, new SpeedSuggestion { Start = d * 0.82, End = d * 0.9, Typing = true } };
            int cuts0 = ed.Project.Cuts.Count;
            ed.ApplyAutoSpeed(fake, true, 3); Pump(0.3);
            Check("auto speed-up adds 3× parts", ed.Project.Cuts.Count == cuts0 + 2 && ed.Project.Cuts.Count(c => c.Speed && Math.Abs(c.Rate - 3) < 1e-6) == 2, $"{ed.Project.Cuts.Count}");
            Check("auto speed-up toast", (ed.LastToast ?? "").Contains("3"), ed.LastToast ?? "");
            ed.ApplyAutoSpeed(fake, true, 3); Pump(0.2);
            Check("already sped up: no double", ed.Project.Cuts.Count == cuts0 + 2, "");

            // 5) 预览放大 / 适应
            float w0 = pv.ImageRect.Width;
            pv.SetZoom(2f, new Point(pv.Width / 2, pv.Height / 2)); Pump(0.4);
            Check("preview zoom 2×", Math.Abs(pv.ImageRect.Width - w0 * 2) < 3, $"{w0:0} -> {pv.ImageRect.Width:0}");
            pv.SetZoom(1f); Pump(0.3);
            Check("fit back", Math.Abs(pv.ImageRect.Width - w0) < 2, "");

            ed.Close(); Pump(1.0);
            var saved = File.Exists(StudioProject.PathIn(dir)) ? File.ReadAllText(StudioProject.PathIn(dir)) : "";
            Check("saved project has keyMutes and splits", saved.Contains("\"keyMutes\"") && saved.Contains("\"splits\""), saved.Length.ToString());
        }
    }
}
