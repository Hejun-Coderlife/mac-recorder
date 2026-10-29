// 录屏 · Screen Recorder（Windows 版）
// 录制引擎：ScreenRecorderLib（Desktop Duplication / Windows.Graphics.Capture + Media Foundation 硬件编码，声音在库里混成一条音轨）
// 界面语言跟随系统（中文 / 英文）；Studio 效果见 StudioCore.cs / StudioWin.cs（背景白色、出 mp4，读写视频用同目录的 ffmpeg.exe）
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class App
    {
        public static readonly bool Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        public static string L(string zh, string en) => Zh ? zh : en;
        public static string Name => L("录屏", "Screen Recorder");

        // 保存文件夹固定叫「录屏」，不随语言变（同 Mac 版）
        public static string Folder
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "录屏");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string NewName() => $"{L("录屏", "Recording")} {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
        public static string NewFilePath() => Path.Combine(Folder, NewName() + ".mp4");

        public static Icon Icon
        {
            get
            {
                using (var s = typeof(App).Assembly.GetManifestResourceStream("app.ico")) return s == null ? SystemIcons.Application : new Icon(s);
            }
        }

        [STAThread]
        static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (Environment.GetEnvironmentVariable("STUDIO") != null) return SelfTest.RenderOnly();
            if (Environment.GetEnvironmentVariable("AUTOTEST") != null) return SelfTest.Run();
            Application.Run(new MainForm());
            return 0;
        }
    }

    // 记住上次的开关（%APPDATA%\录屏\settings.txt，一行一个 key=value）
    static class Settings
    {
        static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "录屏", "settings.txt");

        public static bool Get(string key, bool def)
        {
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var p = line.Split(new[] { '=' }, 2);
                    if (p.Length == 2 && p[0] == key) return p[1] == "1";
                }
            }
            catch { }
            return def;
        }

        public static void Set(string key, bool value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var lines = File.Exists(file) ? File.ReadAllLines(file).Where(l => !l.StartsWith(key + "=")).ToList() : new System.Collections.Generic.List<string>();
                lines.Add(key + "=" + (value ? "1" : "0"));
                File.WriteAllLines(file, lines);
            }
            catch { }
        }
    }
}
