// 录屏 · Screen Recorder（Windows 版）
// 录制引擎：ScreenRecorderLib（Desktop Duplication / Windows.Graphics.Capture + Media Foundation 硬件编码，声音在库里混成一条音轨）
// 界面语言跟随系统（中文 / 英文）；Studio 效果见 StudioCore.cs / StudioWin.cs（背景白色、出 mp4，读写视频用 ffmpeg.exe）
// 截图见 Shots.cs，录完 / 截完右下角浮出的缩略图见 Thumb.cs
// 发布出去只有一个 录屏.exe：录制引擎、VC++ 运行库、ffmpeg 都嵌在里面，第一次打开解到 %LOCALAPPDATA%（见 Runtime.cs）
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class App
    {
        // 界面语言跟系统；APP_LANG=zh / en 可以强制（自测截中文界面用）
        public static readonly bool Zh = Environment.GetEnvironmentVariable("APP_LANG") is string lang ? lang == "zh" : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
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

        // 录好的视频和没拖走的截图都放桌面（同 Mac 自带截图）；Studio 原始素材还在「视频\录屏\Studio 素材」
        public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static string NewName() => $"{L("录屏", "Recording")} {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
        public static string NewFilePath() => Path.Combine(Desktop, NewName() + ".mp4");

        // 桌面上已有同名文件就加 (2)、(3)…
        public static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++) { var p = Path.Combine(dir, $"{name} ({i}){ext}"); if (!File.Exists(p)) return p; }
        }

        public static Icon Icon
        {
            get
            {
                using (var s = typeof(App).Assembly.GetManifestResourceStream("app.ico")) return s == null ? SystemIcons.Application : new Icon(s);
            }
        }

        // 开发时没嵌运行库的版本：ScreenRecorderLib.dll 等在 exe 旁边或旁边的「运行库」文件夹
        public const string LibDir = Studio.LibDirName;

        [STAThread]
        static int Main()
        {
            Runtime.Prepare();
            if (Runtime.Dir != null) { Studio.Locate = Runtime.FfmpegPath; Whisper.Locate = () => Path.Combine(Runtime.Dir, "whisper-cli.exe"); }
            else UseLibFolder();
            App2.Lang = L;
            return Run();   // 分开写：Main 里不能直接碰到引用 ScreenRecorderLib 的类型，否则还没装好 AssemblyResolve 就去找 dll 了
        }

        static void UseLibFolder()
        {
            var lib = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LibDir);
            if (!Directory.Exists(lib)) return;   // dll 和 exe 放在一起照样能用
            SetDllDirectory(lib);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var file = Path.Combine(lib, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Studio 合成用显卡（StudioGpu.cs）；建不起来自动回到 CPU。STUDIO_COMPOSE=gpu 时没有真显卡也用软件渲染（自测）
            Studio.GpuFactory = (lay, cpu, info) => GpuComposer.TryCreate(lay, cpu, info, Environment.GetEnvironmentVariable("STUDIO_COMPOSE") == "gpu");
            KeyLayer.Renderer = KeyCapsGdi.Render;   // 键帽用 GDI+ 画真字
            OverlayLayer.TextRenderer = OverlayGdi.Text;   // 文字标注也是
            if (Environment.GetEnvironmentVariable("W5TEST") != null) return SelfTestW5.Run();
            if (Environment.GetEnvironmentVariable("W4TEST") != null) return SelfTestW4.Run();
            if (Environment.GetEnvironmentVariable("STUDIO") != null) return SelfTest.RenderOnly();
            if (Environment.GetEnvironmentVariable("W3TEST") != null) return SelfTestW3.Run();
            if (Environment.GetEnvironmentVariable("DNDTEST") != null) return SelfTestW3.DndTest();
            if (Environment.GetEnvironmentVariable("CAMTEST") != null) return SelfTestW3.CameraRecord();
            if (Environment.GetEnvironmentVariable("STUDIOTESTS") != null) return SelfTestEditor.Core();
            if (Environment.GetEnvironmentVariable("W2TEST") != null) return SelfTestW2.Run();
            if (Environment.GetEnvironmentVariable("VOICEUITEST") != null) return SelfTestW2.VoiceUi();
            if (Environment.GetEnvironmentVariable("EDITORSHOT") != null) return SelfTestEditor.Shot();
            if (Environment.GetEnvironmentVariable("EDITORTEST") != null) return SelfTestEditor.Interactive();
            if (Environment.GetEnvironmentVariable("COUNTDOWNSHOT") != null) return SelfTestEditor.Countdown();
            if (Environment.GetEnvironmentVariable("MAINSHOT") != null) return SelfTestEditor.MainShot();
            if (Environment.GetEnvironmentVariable("ICONSTEST") != null) return SelfTestEditor.Icons();
            if (Environment.GetEnvironmentVariable("AUTOTEST") != null) return SelfTest.Run();
            if (Environment.GetEnvironmentVariable("UPDATE_TEST") != null) return Updater.SelfTest();
            // 自测：UPDATESTATE_TEST=1 → 把「已更新到」（只给一次）写到 %TEMP%\update_state.txt 后退出
            if (Environment.GetEnvironmentVariable("UPDATESTATE_TEST") != null)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "update_state.txt"), "updatedTo=" + Updater.TakeUpdatedTo() + "\r\n");
                return 0;
            }
            if (Environment.GetEnvironmentVariable("SHOTTEST") != null) return SelfTest.Shot();
            if (Environment.GetEnvironmentVariable("HOTKEY_HOG") != null) return SelfTest.Hog();
            if (Environment.GetEnvironmentVariable("HOTKEYTEST") != null) return SelfTest.Hotkeys();
            Shots.CleanTemp();
            Dnd.Restore();            // 上次录制时关了通知横幅、没来得及换回来：先换回来
            DesktopIcons.Restore();   // 上次录制时藏了桌面图标、没来得及换回来（比如崩了）：先换回来
            // 上次后台下好了新版、却没等到正常退出就被结束了：先换上，再打开新版
            if (Updater.StagedVersion != null && Updater.ApplyStaged(true)) return 0;
            // 把素材文件夹拖到 录屏.exe 上（或命令行带一个素材夹）：直接打开剪辑窗口，不开主窗口（免得和已经开着的那个抢快捷键）
            var argDir = Environment.GetCommandLineArgs().Skip(1).Select(RecentStrip.FootageOf).FirstOrDefault(d => d != null);
            if (argDir != null) { Application.Run(new StudioEditor(argDir)); Updater.ApplyStaged(Updater.RestartAfterExit); return 0; }
            Application.Run(new MainForm());
            // 正常退出：把后台下好的新版换上，下次打开就是新的（点了「现在重启」就顺便打开）
            Updater.ApplyStaged(Updater.RestartAfterExit);
            return 0;
        }
    }

    // 记住上次的开关（%APPDATA%\录屏\settings.txt，一行一个 key=value）
    static class Settings
    {
        static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "录屏", "settings.txt");

        public static bool Get(string key, bool def) => GetString(key, def ? "1" : "0") == "1";

        public static void Set(string key, bool value) => SetString(key, value ? "1" : "0");

        public static string GetString(string key, string def)
        {
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var p = line.Split(new[] { '=' }, 2);
                    if (p.Length == 2 && p[0] == key) return p[1];
                }
            }
            catch { }
            return def;
        }

        public static void SetString(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var lines = File.Exists(file) ? File.ReadAllLines(file).Where(l => !l.StartsWith(key + "=")).ToList() : new System.Collections.Generic.List<string>();
                lines.Add(key + "=" + value);
                File.WriteAllLines(file, lines);
            }
            catch { }
        }
    }
}
