// W3 的自测（CI 上跑）：
//   W3TEST=工作文件夹 → 规则（快捷键过滤、JSON、键帽时间、摄像头几何）+ 真导出（CPU 一遍、显卡 WARP 一遍：键帽、摄像头小窗 / 布局 / 推近不放大 / 剪切同步）
//       + GDI 键帽画出字来 + 剪辑窗口里加布局段、换布局、在预览上拖小窗；顺手留一份 demo 素材夹（work\demo）给截图用；结果写 %TEMP%\w3_test.txt
//   DNDTEST=all|begin|restored [DND_KEY=测试用的注册表路径] → 勿扰：关通知横幅、记原值、换回来（含「中途崩了，下次打开再换回来」）；结果写 %TEMP%\dnd_test.txt
//   CAMTEST=文件夹 CAMERA_INJECT=视频 → 摄像头采集（用视频文件代替摄像头）：写 camera.mp4、预览帧、第一帧时刻、小窗位置换算；结果写 %TEMP%\cam_test.txt
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class SelfTestW3
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);
        static void Pump(double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); } }
        static bool PumpUntil(Func<bool> c, double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { if (c()) return true; Application.DoEvents(); Thread.Sleep(10); } return c(); }

        public static int Run()
        {
            var sb = new StringBuilder(); bool ok = true;
            void Add(string res) { sb.Append(res); ok &= res.TrimEnd().EndsWith(" OK"); File.WriteAllText(Tmp("w3_test.txt"), sb.ToString()); }
            void Check(string n, bool c, string info = "") { sb.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; File.WriteAllText(Tmp("w3_test.txt"), sb.ToString()); }
            try
            {
                string work = Env("W3TEST");
                Add(StudioTestsW3.Rules());
                // GDI 键帽：有字（比没字的方块多出很多不同的像素）
                var real = KeyCapsGdi.Render(new[] { "Ctrl", "Shift", "C" }, true, 40);
                int bright = 0; for (int i = 0; i < real.W * real.H; i++) if (real.Px[i * 4 + 2] > 0.6f && real.Px[i * 4 + 3] > 0.6f) bright++;
                Check("GDI keycap has text (white glyph pixels)", real.W > 150 && bright > 60, $"{real.W}x{real.H}, {bright} bright px");
                Add(StudioTestsW3.Export(work, "cpu"));
                Add(StudioTestsW3.Export(work, "gpu"));
                // 显卡和 CPU 合成同一个摄像头分屏帧：差不多一样
                string a = Path.Combine(work, "camera-layoutscpu.mp4"), b = Path.Combine(work, "camera-layoutsgpu.mp4");
                if (File.Exists(a) && File.Exists(b))
                {
                    var info = Studio.Probe(a);
                    double worst = 0;
                    foreach (var t in new[] { 1.0, 3.0, 6.0 })
                    {
                        var fa = Studio.DecodeFrame(a, t, info.Width, info.Height); var fb = Studio.DecodeFrame(b, t, info.Width, info.Height);
                        double sum = 0; for (int i = 0; i < fa.Length; i += 4) sum += Math.Abs(fa[i] - fb[i]) + Math.Abs(fa[i + 1] - fb[i + 1]) + Math.Abs(fa[i + 2] - fb[i + 2]);
                        worst = Math.Max(worst, sum / (fa.Length / 4 * 3));
                    }
                    Check("GPU (NV12) and CPU (BGRA) give the same camera / layout picture", worst < 3, $"mean abs diff {worst:0.00}");
                }
                // demo 素材夹给截图：从 CPU 那份复制
                string src = Path.Combine(work, "srccpu"), demo = Path.Combine(work, "demo");
                if (Directory.Exists(demo)) Directory.Delete(demo, true);
                Directory.CreateDirectory(demo);
                foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(demo, Path.GetFileName(f)));
                Editor(demo, Check);
            }
            catch (Exception e) { sb.AppendLine("FAIL exception " + e); ok = false; }
            File.WriteAllText(Tmp("w3_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        // 剪辑窗口：布局轨加一段、换布局、删；在预览上拖小窗；关掉存回工程
        static void Editor(string dir, Action<string, bool, string> Check)
        {
            StudioEditor.TestMode = false;   // 要真的存工程
            var ed = new StudioEditor(dir) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
            ed.ClientSize = new Size(1280, 800);
            ed.Show();
            PumpUntil(() => ed.FirstFrameShown, 30);
            Check("editor knows the footage has a camera", ed.HasCamera && ed.Project.Camera != null, "");
            ed.AddLayout(2.0); Pump(0.3);
            var l = ed.Project.Layouts.FirstOrDefault();
            Check("add a layout segment (2 s, camera full)", l != null && Math.Abs(l.Start - 2) < 0.01 && Math.Abs(l.End - 4) < 0.01 && l.Mode == "full" && ed.SelectedLayout == l.Id, l == null ? "" : $"{l.Start}-{l.End} {l.Mode}");
            ed.SetLayoutMode("split"); Pump(0.3);
            Check("switch it to side by side", ed.Project.Layouts.FirstOrDefault()?.Mode == "split", "");
            ed.AddLayout(3.0); Pump(0.2);
            Check("no second segment inside the first", ed.Project.Layouts.Count == 1, "");
            ed.Undo(); Pump(0.2);
            Check("undo the mode change", ed.Project.Layouts.FirstOrDefault()?.Mode == "full", "");
            ed.Select("layout", ed.Project.Layouts.FirstOrDefault()?.Id); ed.DeleteSelected(); Pump(0.2);
            Check("delete the segment", ed.Project.Layouts.Count == 0, "");
            // 拖小窗：在小窗中心按下，往左上挪
            ed.Seek(1.0); Pump(1.0);
            var cs = ed.Project.Camera; double x0 = cs.X, y0 = cs.Y;
            bool grabbed = ed.GrabCamera(cs.X, cs.Y);
            ed.MoveCamera(cs.X - 0.3, cs.Y - 0.2); ed.MoveCamera(x0 - 0.4, y0 - 0.3); ed.DragEnd(); Pump(0.5);
            Check("drag the camera bubble in the preview", grabbed && Math.Abs(ed.Project.Camera.X - (x0 - 0.4)) < 0.02 && Math.Abs(ed.Project.Camera.Y - (y0 - 0.3)) < 0.02, $"{ed.Project.Camera.X:0.00},{ed.Project.Camera.Y:0.00}");
            Check("clicking outside the bubble doesn't grab it", !ed.GrabCamera(0.05, 0.05), "");
            ed.Undo(); Pump(0.3);
            Check("undo the drag (one step)", Math.Abs(ed.Project.Camera.X - x0) < 0.001, "");
            ed.Close(); Pump(0.5);
            var saved = StudioProject.FromJson(File.ReadAllText(StudioProject.PathIn(dir)));
            Check("project saved with camera settings", saved.Camera != null && Math.Abs(saved.Camera.X - x0) < 0.001, "");
            try { File.Delete(StudioProject.PathIn(dir)); } catch { }   // 截图从默认工程开始
        }

        // ---- 勿扰 ----
        public static int DndTest()
        {
            var log = new StringBuilder(); bool ok = true;
            void Check(string n, bool c, string info = "") { log.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; }
            string mode = Env("DNDTEST");
            if (Env("DND_KEY") is string k) Dnd.KeyPath = k;
            string V() => Convert.ToString(Dnd.Current ?? "absent");
            try
            {
                switch (mode)
                {
                    case "begin":   // 开录后「崩了」：只关、不换回来
                        log.AppendLine("before " + V());
                        Check("toast banners turned off", Dnd.Begin() && V() == "0", V());
                        Check("original value remembered", Settings.GetString("dndRestore", "") != "", Settings.GetString("dndRestore", ""));
                        break;
                    case "restored":   // 下次打开：Program 一开始就做的那步
                        Dnd.Restore();
                        Check("restored on next launch", V() == (Env("DND_EXPECT") ?? "1"), V());
                        Check("marker cleared", Settings.GetString("dndRestore", "") == "");
                        break;
                    default:
                        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Dnd.KeyPath)) key.SetValue("ToastEnabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        Dnd.Begin();
                        Check("on: ToastEnabled = 0", V() == "0", V());
                        Dnd.Begin();   // 录两次也不会把「原值」记成 0
                        Dnd.Restore();
                        Check("off: back to 1", V() == "1", V());
                        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Dnd.KeyPath)) key.DeleteValue("ToastEnabled", false);
                        Dnd.Begin(); Dnd.Restore();
                        Check("value that wasn't there is removed again", V() == "absent", V());
                        Dnd.Restore();
                        Check("restore without begin does nothing", V() == "absent", V());
                        break;
                }
            }
            catch (Exception e) { Check("exception", false, e.ToString()); }
            File.WriteAllText(Tmp("dnd_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + log);
            return ok ? 0 : 1;
        }

        // ---- 摄像头采集（视频文件代替摄像头） ----
        public static int CameraRecord()
        {
            var log = new StringBuilder(); bool ok = true;
            void Check(string n, bool c, string info = "") { log.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; }
            try
            {
                string dir = Env("CAMTEST"); Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "camera.mp4");
                if (File.Exists(file)) File.Delete(file);
                int frames = 0;
                var bubble = new CameraBubble(); bubble.Show(); bubble.ExcludeFromCapture(true);
                double t0 = Clock.Now;
                var c = CameraCapture.Start("Test camera", file);
                c.Frame += f => { frames++; bubble.Push(f); };
                Pump(3.0);
                c.Stop();
                Check("preview frames arrive (~20 per second)", frames >= 30, frames.ToString());
                Check("first frame time recorded", c.FirstFrameAt > t0 && c.FirstFrameAt - t0 < 3, $"{c.FirstFrameAt - t0:0.00}s after start");
                var info = File.Exists(file) ? Studio.Probe(file) : null;
                Check("camera.mp4 written and closed properly (q)", info != null && info.Duration > 2 && info.Duration < 4.5, $"{info?.Duration:0.00}s {info?.Width}x{info?.Height} {c.Error}");
                var area = new Rectangle(bubble.Left - 100, bubble.Top - 50, 1000, 600);
                var ct = bubble.TrackIn(area, 0.4);
                Check("bubble position as a fraction of the recorded area", Math.Abs(ct.X - (100 + bubble.Width / 2.0) / 1000) < 0.002 && Math.Abs(ct.Y - (50 + bubble.Height / 2.0) / 600) < 0.002 && Math.Abs(ct.Size - bubble.Width / 600.0) < 0.002 && ct.Offset == 0.4,
                      $"{ct.X:0.000},{ct.Y:0.000} size {ct.Size:0.000}");
                if (Env("CAMSHOT") is string shot)
                    using (var bmp = new Bitmap(bubble.Width, bubble.Height)) { bubble.DrawToBitmap(bmp, new Rectangle(0, 0, bubble.Width, bubble.Height)); bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png); }
                bubble.Close();
                Check("camera device list runs (may be empty on CI)", CameraDevices.List() != null);
            }
            catch (Exception e) { Check("exception", false, e.ToString()); }
            File.WriteAllText(Tmp("cam_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + log);
            return ok ? 0 : 1;
        }
    }
}
