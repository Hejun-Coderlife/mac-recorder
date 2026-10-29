using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    // 自测（不弹窗）：AUTOTEST=秒 [AUTOREGION=x,y,w,h（相对主屏幕）] [AUTOWINDOW=窗口标题里的字] [AUTOAUDIO=1 录电脑声音] [AUTOMIC=1] [AUTOOUT=输出路径]
    //   [AUTOSTUDIO=1 开 Studio 效果录，录完直接生成] [AUTOINPUT=1 录的时候自己动鼠标、点击、打字（SendInput）]
    // 只重新生成 Studio 版：STUDIO=素材文件夹 [STUDIO_OUT=输出路径] [STUDIO_FAKETRACK=1 没有 track.json 时编一条轨迹，测速度用]
    // 结果写到 %TEMP%\recorder_test.txt：第一行 OK 路径 或 FAIL 原因
    static class SelfTest
    {
        public static int Run()
        {
            string result = Path.Combine(Path.GetTempPath(), "recorder_test.txt");
            try
            {
                double secs = double.Parse(Environment.GetEnvironmentVariable("AUTOTEST"), System.Globalization.CultureInfo.InvariantCulture);
                var env = (Func<string, string>)Environment.GetEnvironmentVariable;
                var main = Screen.PrimaryScreen;
                // 录制期间让画面有变化：先开一个会动的小窗口（AUTOWINDOW=autotest 就录它）
                var f = new Form { Text = "autotest", Width = 400, Height = 300, StartPosition = FormStartPosition.Manual, Left = main.Bounds.Left + 40, Top = main.Bounds.Top + 40, TopMost = true };
                var lbl = new Label { Dock = DockStyle.Fill, Font = new System.Drawing.Font("Segoe UI", 28), TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
                var box = new TextBox { Dock = DockStyle.Bottom, Font = new System.Drawing.Font("Segoe UI", 14) };
                f.Controls.Add(lbl); f.Controls.Add(box);
                f.Show();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }

                Source src;
                if (env("AUTOREGION") is string reg)
                {
                    var p = reg.Split(',').Select(int.Parse).ToArray();
                    src = Sources.Region(main, new System.Drawing.Rectangle(p[0], p[1], p[2], p[3]));
                }
                else if (env("AUTOWINDOW") is string win)
                {
                    src = Sources.List(null, includeSelf: true).FirstOrDefault(s => s.Kind == SourceKind.Window && (s.Title + " " + s.Detail).IndexOf(win, StringComparison.OrdinalIgnoreCase) >= 0)
                          ?? throw new Exception("window not found: " + win);
                }
                else src = Sources.List(null).First(s => s.Kind == SourceKind.Display && s.DeviceName == main.DeviceName);

                string path = env("AUTOOUT") ?? App.NewFilePath();
                bool studioOn = env("AUTOSTUDIO") == "1";
                var proj = studioOn ? StudioProject.Create(Path.GetFileNameWithoutExtension(path), path, src) : null;
                var s2 = new Session(src, env("AUTOAUDIO") == "1", env("AUTOMIC") == "1", !studioOn, proj?.Raw ?? path);
                string done = null, error = null;
                var finished = new ManualResetEventSlim();
                s2.Completed += p => { done = p; finished.Set(); };
                s2.Failed += e => { error = e; finished.Set(); };
                double startClock = Clock.Now;
                proj?.Tracker.Start();
                s2.Start();
                var t0 = DateTime.Now;
                var input = env("AUTOINPUT") == "1" ? new InputScript(f, box) : null;
                while ((DateTime.Now - t0).TotalSeconds < secs && !finished.IsSet)
                {
                    double el = (DateTime.Now - t0).TotalSeconds;
                    input?.Step(el);
                    lbl.Text = el.ToString("0.00");
                    f.BackColor = System.Drawing.Color.FromArgb((int)((DateTime.Now - t0).TotalMilliseconds / 10) % 255, 120, 200);
                    Application.DoEvents();
                    Thread.Sleep(30);
                }
                f.Close();
                if (!finished.IsSet) s2.Stop();
                if (!finished.Wait(TimeSpan.FromSeconds(30))) throw new Exception("timeout waiting for recording to finish");
                if (error != null) throw new Exception(error);
                string extra = "";
                if (proj != null)
                {
                    var log = new System.Text.StringBuilder();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    proj.Finish(s2.FirstFrameAt, p => { }, m => log.AppendLine(m));
                    extra = $"studio raw={proj.Raw} first-frame-offset={(s2.FirstFrameAt - startClock):0.000}s render={sw.Elapsed.TotalSeconds:0.0}s" + Environment.NewLine + log + (input?.Log ?? "") + "cursor: " + string.Join("; ", proj.Tracker.Seen) + Environment.NewLine;
                    done = proj.Out;
                }
                File.WriteAllText(result, "OK " + done + Environment.NewLine + src.Kind + " " + src.Detail + Environment.NewLine + extra);
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(result, "FAIL " + e.Message + Environment.NewLine + e);
                return 1;
            }
        }
    
        public static int RenderOnly()
        {
            string result = Path.Combine(Path.GetTempPath(), "recorder_test.txt");
            try
            {
                string dir = Environment.GetEnvironmentVariable("STUDIO");
                string outPath = Environment.GetEnvironmentVariable("STUDIO_OUT") ?? Path.Combine(dir, "studio.mp4");
                if (!File.Exists(Path.Combine(dir, "track.json")) && Environment.GetEnvironmentVariable("STUDIO_FAKETRACK") == "1") FakeTrack(dir);
                var log = new System.Text.StringBuilder();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Studio.Render(dir, outPath, new StudioStyle(), p => { }, CursorTracker.LoadCursor, m => log.AppendLine(m));
                File.WriteAllText(result, $"OK {outPath}{Environment.NewLine}render={sw.Elapsed.TotalSeconds:0.0}s{Environment.NewLine}{log}");
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(result, "FAIL " + e.Message + Environment.NewLine + e);
                return 1;
            }
        }

        // 测速度用：鼠标绕圈走，每 5 秒点一下
        static void FakeTrack(string dir)
        {
            var info = Studio.Probe(Path.Combine(dir, "raw.mp4"));
            var t = new Track { Width = info.Width, Height = info.Height };
            for (double s = 0; s < info.Duration; s += 1.0 / 120)
                t.Moves.Add(new[] { s, info.Width * (0.5 + 0.3 * Math.Cos(s * 0.7)), info.Height * (0.5 + 0.3 * Math.Sin(s * 0.9)), 0, 0 });
            t.Cursors.Add(new CursorShape { File = "cursor-0.png", Arrow = true, W = 32, H = 32 });
            for (double s = 2; s < info.Duration - 1; s += 5)
            {
                var m = t.Moves[(int)(s * 120)];
                t.Clicks.Add(new[] { s, m[1], m[2] });
            }
            File.WriteAllText(Path.Combine(dir, "track.json"), t.ToJson());
        }

        // 录的时候按时间表动鼠标、点击、打字（真的系统输入，钩子能收到）
        sealed class InputScript
        {
            readonly Form form; readonly TextBox box;
            readonly System.Text.StringBuilder log = new System.Text.StringBuilder();
            int stage;
            public string Log => log.ToString();
            public InputScript(Form f, TextBox b) { form = f; box = b; }

            public void Step(double t)
            {
                var target = box.PointToScreen(new System.Drawing.Point(box.Width / 2, box.Height / 2));
                var start = new System.Drawing.Point(target.X + 300, target.Y + 250);
                if (t < 0.3) { Move(start); return; }
                if (t < 1.3) { double u = (t - 0.3) / 1.0; Move(Lerp(start, target, u)); return; }
                if (stage == 0 && t >= 1.5) { stage = 1; Move(target); Click(); log.AppendLine($"click {t:0.00} at {target.X},{target.Y}"); return; }
                if (stage >= 1 && stage < 9 && t >= 2.0 + (stage - 1) * 0.12) { Key((ushort)('A' + stage - 1)); log.AppendLine($"key {t:0.00}"); stage++; return; }
                var far = new System.Drawing.Point(target.X + 500, target.Y + 350);
                if (t >= 3.3 && t < 4.0) { Move(Lerp(target, far, (t - 3.3) / 0.7)); return; }
                if (stage == 9 && t >= 4.2) { stage = 10; Move(far); Click(); log.AppendLine($"click {t:0.00} at {far.X},{far.Y}"); }
            }

            static System.Drawing.Point Lerp(System.Drawing.Point a, System.Drawing.Point b, double u)
            {
                u = Math.Max(0, Math.Min(1, u)); u = u * u * (3 - 2 * u);
                return new System.Drawing.Point((int)(a.X + (b.X - a.X) * u), (int)(a.Y + (b.Y - a.Y) * u));
            }

            static void Move(System.Drawing.Point p) => SetCursorPos(p.X, p.Y);
            static void Click()
            {
                var down = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0002 } };
                var up = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0004 } };
                SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>()); Thread.Sleep(40); SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
            }
            static void Key(ushort vk)
            {
                var down = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk } };
                var up = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0002 } };
                SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>()); SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
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
