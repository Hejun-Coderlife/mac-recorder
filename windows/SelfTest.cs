using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    // 自测（不弹窗）：AUTOTEST=秒 [AUTOREGION=x,y,w,h（相对主屏幕）] [AUTOWINDOW=窗口标题里的字] [AUTOAUDIO=1 录电脑声音] [AUTOMIC=1] [AUTOOUT=输出路径]
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
                f.Controls.Add(lbl);
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
                var s2 = new Session(src, env("AUTOAUDIO") == "1", env("AUTOMIC") == "1", true, path);
                string done = null, error = null;
                var finished = new ManualResetEventSlim();
                s2.Completed += p => { done = p; finished.Set(); };
                s2.Failed += e => { error = e; finished.Set(); };
                s2.Start();
                var t0 = DateTime.Now;
                while ((DateTime.Now - t0).TotalSeconds < secs && !finished.IsSet)
                {
                    lbl.Text = (DateTime.Now - t0).TotalSeconds.ToString("0.00");
                    f.BackColor = System.Drawing.Color.FromArgb((int)((DateTime.Now - t0).TotalMilliseconds / 10) % 255, 120, 200);
                    Application.DoEvents();
                    Thread.Sleep(30);
                }
                f.Close();
                if (!finished.IsSet) s2.Stop();
                if (!finished.Wait(TimeSpan.FromSeconds(30))) throw new Exception("timeout waiting for recording to finish");
                if (error != null) throw new Exception(error);
                File.WriteAllText(result, "OK " + done + Environment.NewLine + src.Kind + " " + src.Detail + Environment.NewLine);
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(result, "FAIL " + e.Message + Environment.NewLine + e);
                return 1;
            }
        }
    }
}
