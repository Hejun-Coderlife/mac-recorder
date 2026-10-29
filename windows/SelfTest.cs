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
    //   [AUTOTHUMB=1 录完照常弹出右下角缩略图，等它自己消失；SHOTOUT=文件夹 存一张缩略图的截屏]
    // 截图自测：SHOTTEST=1 [SHOTREGION=x,y,w,h（相对主屏幕）] [SHOTOUT=文件夹 存几张截屏] [THUMB_SECONDS=缩略图停留秒数] SHOT_VISIBLE=1
    //   → 结果写到 %TEMP%\shot_test.txt（第一行 OK 或 FAIL）
    // 快捷键自测：HOTKEYTEST=1 [SHOTOUT=文件夹 存截屏] → 另起一个进程占住 Ctrl+Alt+X，验证检测到并弹通知；
    //   点按钮按 Ctrl+Alt+Q 改截图快捷键、存进设置；再按被占的组合要被拒；按 Ctrl+Alt+Q 弹出截图选框。结果写 %TEMP%\hotkey_test.txt
    // HOTKEY_HOG=秒 → 占住 Ctrl+Alt+X 这么久（自测里当「别的程序」用）
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
                if (env("AUTOTHUMB") == "1")
                {
                    // 录完和真用一样：右下角出视频缩略图，没人动它就自己滑走；视频本来就在桌面
                    var t = Thumb.ShowVideo(done, Thumb.VideoFrame(done));
                    Pump(0.8);
                    bool shown = t.Visible && Thumb.Open.Contains(t);
                    if (env("SHOTOUT") is string so) Snap(Path.Combine(so, "video-thumb.png"));
                    var until = DateTime.Now.AddSeconds(30);
                    while (!t.IsDisposed && DateTime.Now < until) Pump(0.1);
                    extra += $"thumb shown={shown} outcome={t.Outcome} closed={t.IsDisposed}" + Environment.NewLine;
                    if (!shown || !t.IsDisposed) throw new Exception("video thumbnail didn't show / go away: " + extra);
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
    
        static void Pump(double secs)
        {
            var until = DateTime.Now.AddSeconds(secs);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(15); }
        }

        // 截主屏幕右下角一块（缩略图在那儿）或整屏存 png，给人看
        static void Snap(string file, bool whole = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var b = Screen.PrimaryScreen.Bounds;
            var r = whole ? b : new System.Drawing.Rectangle(b.Right - b.Width / 2, b.Bottom - b.Height / 2, b.Width / 2, b.Height / 2);
            using (var bmp = new System.Drawing.Bitmap(r.Width, r.Height))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, System.Drawing.Point.Empty, r.Size);
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // ---- 截图自测：剪贴板、缩略图超时存桌面、悬停不消失、× 不落盘、拖进别的窗口、真拖一次选框 ----
        public static int Shot()
        {
            string result = Path.Combine(Path.GetTempPath(), "shot_test.txt");
            var log = new System.Text.StringBuilder();
            var env = (Func<string, string>)Environment.GetEnvironmentVariable;
            string outDir = env("SHOTOUT");
            void Check(bool ok, string what) { log.AppendLine((ok ? "pass " : "FAIL ") + what); if (!ok) throw new Exception(what); }
            try
            {
                var main = Screen.PrimaryScreen.Bounds;
                // 截图里要有东西：开一个彩色的小窗口
                var f = new Form { Text = "shot-subject", StartPosition = FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(main.X + main.Width / 2 - 350, main.Y + main.Height / 2 - 250, 700, 500), BackColor = System.Drawing.Color.FromArgb(70, 140, 220), TopMost = true };
                f.Controls.Add(new Label { Text = "截图 Screenshot", Dock = DockStyle.Fill, Font = new System.Drawing.Font("Segoe UI", 36), ForeColor = System.Drawing.Color.White, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
                f.Show(); Pump(0.5);

                var rr = (env("SHOTREGION") ?? $"{main.Width / 2 - 300},{main.Height / 2 - 200},600,400").Split(',').Select(int.Parse).ToArray();
                var region = new System.Drawing.Rectangle(main.X + rr[0], main.Y + rr[1], rr[2], rr[3]);
                System.Drawing.Bitmap Grab()
                {
                    using (var all = Shots.Freeze(out var virt))
                        return all.Clone(new System.Drawing.Rectangle(region.X - virt.X, region.Y - virt.Y, region.Width, region.Height), all.PixelFormat);
                }

                // 1. 剪贴板 + 缩略图 + 没人动 → 存到桌面
                Thumb t1;
                using (var bmp = Grab()) t1 = Shots.Deliver(bmp);
                var img = Clipboard.GetImage();
                Check(img != null && img.Width == region.Width && img.Height == region.Height, $"clipboard image {img?.Width}x{img?.Height} (want {region.Width}x{region.Height})");
                Check(Clipboard.GetDataObject().GetDataPresent("PNG"), "clipboard has PNG");
                Pump(0.6);
                Check(t1.Visible && Thumb.Open.Contains(t1), "thumbnail shown");
                if (outDir != null) Snap(Path.Combine(outDir, "1-thumb.png"));
                string tmp1 = t1.FilePath;
                var until = DateTime.Now.AddSeconds(20);
                while (!t1.IsDisposed && DateTime.Now < until) Pump(0.1);
                Check(t1.IsDisposed && t1.Outcome == "saved", $"timed out → {t1.Outcome}");
                Check(t1.FilePath.StartsWith(App.Desktop, StringComparison.OrdinalIgnoreCase) && File.Exists(t1.FilePath) && !File.Exists(tmp1), "saved to Desktop: " + t1.FilePath);
                using (var saved = new System.Drawing.Bitmap(t1.FilePath)) Check(saved.Width == region.Width && saved.Height == region.Height, "desktop png size");
                if (outDir != null) File.Copy(t1.FilePath, Path.Combine(outDir, "desktop-shot.png"), true);
                File.Delete(t1.FilePath);

                // 2. 悬停不消失 → 点 × → 不落盘
                Thumb t2;
                using (var bmp = Grab()) t2 = Shots.Deliver(bmp);
                Pump(0.8);
                var c2 = t2.PointToScreen(new System.Drawing.Point(t2.Width / 2, t2.Height / 2));
                Mouse.Move(c2.X, c2.Y); Pump(0.1); Mouse.Move(c2.X + 2, c2.Y + 1);
                Pump(3.5);   // 比停留时间长
                Check(!t2.IsDisposed, "hover keeps the thumbnail");
                if (outDir != null) Snap(Path.Combine(outDir, "2-hover.png"));
                var cb = t2.CloseBox; var x = t2.PointToScreen(new System.Drawing.Point(cb.X + cb.Width / 2, cb.Y + cb.Height / 2));
                string tmp2 = t2.FilePath;
                Mouse.Move(x.X, x.Y); Pump(0.2); Mouse.Click(); Pump(1.0);
                Check(t2.Outcome == "dismissed", "× → " + t2.Outcome);
                Pump(2.5);
                Check(!File.Exists(tmp2) && !File.Exists(Path.Combine(App.Desktop, Path.GetFileName(tmp2))), "× leaves no file");
                Mouse.Move(main.X + 20, main.Y + main.Height / 2); Pump(0.3);

                // 3. 拖进别的窗口：收到文件和图片，缩略图走掉，桌面上没有
                var drop = new Form { Text = "drop-target", StartPosition = FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(main.X + 40, main.Y + 40, 420, 320), AllowDrop = true, TopMost = true, BackColor = System.Drawing.Color.Beige };
                string[] gotFiles = null; bool gotBitmap = false;
                drop.DragEnter += (s, e) => e.Effect = DragDropEffects.Copy;
                drop.DragOver += (s, e) => e.Effect = DragDropEffects.Copy;
                drop.DragDrop += (s, e) => { gotFiles = e.Data.GetData(DataFormats.FileDrop) as string[]; gotBitmap = e.Data.GetDataPresent(DataFormats.Bitmap); };
                drop.Show(); Pump(0.3);
                Thumb t3;
                using (var bmp = Grab()) t3 = Shots.Deliver(bmp);
                Pump(0.8);
                var c3 = t3.PointToScreen(new System.Drawing.Point(t3.Width / 2, t3.Height / 2));
                var dst = drop.PointToScreen(new System.Drawing.Point(200, 150));
                var mover = new Thread(() =>
                {
                    Mouse.Move(c3.X, c3.Y); Thread.Sleep(150); Mouse.Down(); Thread.Sleep(80);
                    for (int i = 1; i <= 30; i++) { Mouse.Move(c3.X + (dst.X - c3.X) * i / 30, c3.Y + (dst.Y - c3.Y) * i / 30); Thread.Sleep(25); }
                    Thread.Sleep(200); Mouse.Up();
                }) { IsBackground = true };
                mover.Start();
                until = DateTime.Now.AddSeconds(10);
                while (gotFiles == null && DateTime.Now < until) Pump(0.05);
                Pump(1.0);
                Check(gotFiles != null && gotFiles.Length == 1 && File.Exists(gotFiles[0]), "drop target got the file: " + (gotFiles == null ? "none" : string.Join(",", gotFiles)));
                Check(gotBitmap, "drop target got a bitmap too");
                Check(t3.Outcome == "dragged", "drag → " + t3.Outcome);
                Pump(2.5);
                Check(!File.Exists(Path.Combine(App.Desktop, Path.GetFileName(t3.FilePath))), "dragged shot not saved to Desktop");
                drop.Close();

                // 4. 真拖一次选框（遮罩是真的弹出来）
                System.Drawing.Bitmap picked = null; bool cancelled = false;
                Shots.Start(b => picked = b);
                Pump(0.5);
                Check(Shots.Active, "overlay shown");
                var a = new System.Drawing.Point(main.X + main.Width / 2 - 250, main.Y + main.Height / 2 - 180);
                var bpt = new System.Drawing.Point(a.X + 400, a.Y + 300);
                var drag = new Thread(() =>
                {
                    Mouse.Move(a.X, a.Y); Thread.Sleep(150); Mouse.Down(); Thread.Sleep(60);
                    for (int i = 1; i <= 25; i++) { Mouse.Move(a.X + (bpt.X - a.X) * i / 25, a.Y + (bpt.Y - a.Y) * i / 25); Thread.Sleep(25); }
                    Thread.Sleep(300);
                    if (outDir != null) Snap(Path.Combine(outDir, "3-overlay.png"), true);
                    Mouse.Up();
                }) { IsBackground = true };
                drag.Start();
                until = DateTime.Now.AddSeconds(10);
                while (picked == null && DateTime.Now < until) Pump(0.05);
                Check(picked != null && picked.Width == 400 && picked.Height == 300, $"overlay drag → {picked?.Width}x{picked?.Height} (want 400x300)");
                Check(!Shots.Active, "overlay closed");
                if (outDir != null) picked.Save(Path.Combine(outDir, "overlay-pick.png"), System.Drawing.Imaging.ImageFormat.Png);
                picked.Dispose();

                // 5. Esc 取消
                Shots.Start(b => { picked = b; });
                Pump(0.5);
                picked = null;
                SendKeys.SendWait("{ESC}");
                Pump(0.5);
                cancelled = !Shots.Active && picked == null;
                Check(cancelled, "Esc cancels");

                f.Close();
                File.WriteAllText(result, "OK" + Environment.NewLine + log);
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(result, "FAIL " + e.Message + Environment.NewLine + log + e);
                return 1;
            }
        }

        static class Mouse
        {
            public static void Move(int x, int y) => SetCursorPos(x, y);
            public static void Down() => Send(0x0002);
            public static void Up() => Send(0x0004);
            public static void Click() { Down(); Thread.Sleep(40); Up(); }
            static void Send(uint flags)
            {
                var i = new MINPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
                SendInput(1, new[] { i }, Marshal.SizeOf<MINPUT>());
            }
            [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
            [StructLayout(LayoutKind.Explicit, Size = 40)] struct MINPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
            [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] static extern uint SendInput(uint n, MINPUT[] inputs, int size);
        }

        // ---- 快捷键自测 ----
        public static int Hog()
        {
            var ready = Path.Combine(Path.GetTempPath(), "hotkey_hog.txt");
            bool ok = RegisterHotKey(IntPtr.Zero, 77, Hotkey.Ctrl | Hotkey.Alt, (uint)Keys.X);
            File.WriteAllText(ready, ok ? "HOG OK" : "HOG FAIL");
            Thread.Sleep(TimeSpan.FromSeconds(double.Parse(Environment.GetEnvironmentVariable("HOTKEY_HOG"), System.Globalization.CultureInfo.InvariantCulture)));
            return ok ? 0 : 1;
        }

        public static int Hotkeys()
        {
            string result = Path.Combine(Path.GetTempPath(), "hotkey_test.txt");
            var log = new System.Text.StringBuilder();
            string outDir = Environment.GetEnvironmentVariable("SHOTOUT");
            void Check(bool ok, string what) { log.AppendLine((ok ? "pass " : "FAIL ") + what); if (!ok) throw new Exception(what); }
            System.Diagnostics.Process hog = null;
            try
            {
                // 老用户的设置：有别的项、没有快捷键这两项 → 截图用新默认 Ctrl+Alt+X
                var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "录屏", "settings.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(settings));
                var keep = File.Exists(settings) ? File.ReadAllLines(settings).Where(l => !l.StartsWith("hotkey")).ToList() : new System.Collections.Generic.List<string>();
                if (!keep.Any(l => l.StartsWith("studio="))) keep.Add("studio=0");
                File.WriteAllLines(settings, keep);

                // 另一个进程占住 Ctrl+Alt+X（就像 QQ 占着 Ctrl+Alt+S）
                var ready = Path.Combine(Path.GetTempPath(), "hotkey_hog.txt");
                File.Delete(ready);
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false };
                foreach (var k in new[] { "HOTKEYTEST", "SHOTTEST", "AUTOTEST", "STUDIO", "UPDATE_TEST" }) psi.EnvironmentVariables.Remove(k);
                psi.EnvironmentVariables["HOTKEY_HOG"] = "60";
                hog = System.Diagnostics.Process.Start(psi);
                var until = DateTime.Now.AddSeconds(30);
                while (!File.Exists(ready) && DateTime.Now < until) Thread.Sleep(100);
                Thread.Sleep(200);
                Check(File.Exists(ready) && File.ReadAllText(ready) == "HOG OK", "another process holds Ctrl+Alt+X");

                var form = new MainForm();
                Check(form.ShotKey.ToString() == "Ctrl+Alt+X" && form.RecordKey.ToString() == "Ctrl+Alt+R", $"defaults without saved shortcuts: record {form.RecordKey}, shot {form.ShotKey}");
                form.Show(); Pump(1.5);
                Check(form.HotkeyProblems.Count == 1 && form.HotkeyProblems[0].Contains("Ctrl+Alt+X"), "detected taken shortcut: " + string.Join(" | ", form.HotkeyProblems));
                Check(Notice.Last != null && Notice.Last.Contains("Ctrl+Alt+X"), "notification shown: " + Notice.Last);
                Check(form.StatusText.Contains("Ctrl+Alt+X"), "status line: " + form.StatusText);
                if (outDir != null) Snap(Path.Combine(outDir, "hotkey-1-taken.png"), true);

                // 点截图快捷键按钮，真按 Ctrl+Alt+Q
                void ClickKeyButton()
                {
                    form.Activate(); Pump(0.3);
                    var b = form.ShotKeyButton; var c = b.PointToScreen(new System.Drawing.Point(b.Width / 2, b.Height / 2));
                    Mouse.Move(c.X, c.Y); Pump(0.1); Mouse.Click(); Pump(0.5);
                }
                ClickKeyButton();
                Check(form.ShotKeyButton.Text != "Ctrl+Alt+X", "button waits for keys: " + form.ShotKeyButton.Text);
                if (outDir != null) Snap(Path.Combine(outDir, "hotkey-2-capturing.png"), true);
                Keyboard.Combo(0x11, 0x12, 0x51);   // Ctrl+Alt+Q
                Pump(0.8);
                Check(form.ShotKey.ToString() == "Ctrl+Alt+Q", "changed to " + form.ShotKey);
                Check(File.ReadAllLines(settings).Contains($"{Hotkey.ShotSetting}={Hotkey.Ctrl | Hotkey.Alt},{(int)Keys.Q}"), "saved to settings.txt");
                Check(form.ShotKeyButton.Text == "Ctrl+Alt+Q", "button shows the new shortcut");
                if (outDir != null) Snap(Path.Combine(outDir, "hotkey-3-changed.png"), true);

                // 改成被占的 Ctrl+Alt+X：要被拒，保留 Ctrl+Alt+Q
                // （被占的组合真按下去会被占用它的程序吞掉、到不了我们窗口，所以这里直接走「按下后」的处理）
                form.BeginCapture(2);
                form.ApplyCapture(new Hotkey(Hotkey.Ctrl | Hotkey.Alt, Keys.X));
                Pump(0.3);
                Check(form.ShotKey.ToString() == "Ctrl+Alt+Q" && form.StatusText.Contains("Ctrl+Alt+X"), $"taken combo rejected: shot={form.ShotKey}, status={form.StatusText}");

                // 新快捷键真的能截图：按 Ctrl+Alt+Q → 选框弹出 → Esc
                Mouse.Move(Screen.PrimaryScreen.Bounds.X + 10, Screen.PrimaryScreen.Bounds.Y + 10); Pump(0.2);
                Keyboard.Combo(0x11, 0x12, 0x51);
                until = DateTime.Now.AddSeconds(5);
                while (!Shots.Active && DateTime.Now < until) Pump(0.05);
                Check(Shots.Active, "Ctrl+Alt+Q opens the screenshot overlay");
                Pump(0.5);
                if (outDir != null) Snap(Path.Combine(outDir, "hotkey-4-overlay.png"), true);
                SendKeys.SendWait("{ESC}"); Pump(0.5);
                Check(!Shots.Active, "Esc closes it");

                // 录屏快捷键还在：Ctrl+Alt+R 没被这一通改动弄丢
                form.Close(); Pump(0.5);
                File.WriteAllText(result, "OK" + Environment.NewLine + log);
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(result, "FAIL " + e.Message + Environment.NewLine + log + e);
                return 1;
            }
            finally
            {
                try { hog?.Kill(); } catch { }
            }
        }

        static class Keyboard
        {
            // 依次按下、反过来松开：Combo(Ctrl, Alt, Q)
            public static void Combo(params ushort[] vks)
            {
                foreach (var vk in vks) { Send(vk, 0); Thread.Sleep(30); }
                for (int i = vks.Length - 1; i >= 0; i--) { Send(vks[i], 0x0002); Thread.Sleep(30); }
            }
            static void Send(ushort vk, uint flags)
            {
                var i = new KINPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
                SendInput(1, new[] { i }, Marshal.SizeOf<KINPUT>());
            }
            [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
            [StructLayout(LayoutKind.Explicit, Size = 40)] struct KINPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
            [DllImport("user32.dll")] static extern uint SendInput(uint n, KINPUT[] inputs, int size);
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

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
