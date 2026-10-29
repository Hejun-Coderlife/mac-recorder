// 单个 exe：录制引擎 ScreenRecorderLib.dll、VC++ 运行库、ffmpeg.exe 都压缩后嵌在 录屏.exe 里，
// 第一次打开解到 %LOCALAPPDATA%\录屏\runtime\<版本-大小>\（解完才改成正式名字，不会留下半截），以后直接用。
// 小的几个 dll 启动时同步解（一眨眼）；ffmpeg 大（约 170MB），放后台解，Studio / 视频缩略图用到时再等它
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ScreenRecorder
{
    static class Runtime
    {
        const string Prefix = "rt/", Ffmpeg = "ffmpeg.exe";
        public static string Dir { get; private set; }   // 解出来的文件夹；没嵌资源（开发时的老布局）时为 null
        public static double ExtractSeconds { get; private set; }
        static Task ffmpegTask;

        static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "录屏", "runtime");

        // 在碰到任何引用 ScreenRecorderLib 的类型之前调用
        public static void Prepare()
        {
            var asm = typeof(Runtime).Assembly;
            var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix)).ToArray();
            if (names.Length == 0) return;
            var sw = Stopwatch.StartNew();
            long size = names.Sum(n => { using (var s = asm.GetManifestResourceStream(n)) return s.Length; });
            var v = asm.GetName().Version;
            Dir = Path.Combine(Root, $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}-{size:x}");

            if (!File.Exists(Path.Combine(Dir, ".complete")))
            {
                var tmp = Dir + ".tmp-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(tmp);
                foreach (var n in names.Where(n => Name(n) != Ffmpeg)) Unpack(asm, n, Path.Combine(tmp, Name(n)));
                File.WriteAllText(Path.Combine(tmp, ".complete"), "");
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); Directory.Move(tmp, Dir); }
                catch { if (!File.Exists(Path.Combine(Dir, ".complete"))) throw; TryDelete(tmp); }   // 同时开了两个：别人已经解好了
            }
            ExtractSeconds = sw.Elapsed.TotalSeconds;

            var ff = names.FirstOrDefault(n => Name(n) == Ffmpeg);
            if (ff != null && !File.Exists(Path.Combine(Dir, Ffmpeg)))
                ffmpegTask = Task.Run(() =>
                {
                    var tmp = Path.Combine(Dir, Ffmpeg + ".tmp-" + Guid.NewGuid().ToString("N"));
                    Unpack(asm, ff, tmp);
                    try { File.Move(tmp, Path.Combine(Dir, Ffmpeg)); } catch { TryDelete(tmp); }
                });

            SetDllDirectory(Dir);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var file = Path.Combine(Dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            Task.Run(() => CleanUp());
        }

        // ffmpeg 的路径；后台还没解完就等它
        public static string FfmpegPath()
        {
            if (Dir == null) return null;
            try { ffmpegTask?.Wait(); } catch { }
            return Path.Combine(Dir, Ffmpeg);
        }

        static string Name(string resource) => resource.Substring(Prefix.Length, resource.Length - Prefix.Length - ".gz".Length);

        static void Unpack(Assembly asm, string resource, string file)
        {
            using (var s = asm.GetManifestResourceStream(resource))
            using (var gz = new GZipStream(s, CompressionMode.Decompress))
            using (var f = File.Create(file)) gz.CopyTo(f, 1 << 20);
        }

        // 后台收拾：老版本解出来的运行库、自动更新换下来的 .old、老安装包（多个文件那种）留在 exe 旁边的东西
        static void CleanUp()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Root))
                    if (!string.Equals(d, Dir, StringComparison.OrdinalIgnoreCase)) TryDelete(d);
            }
            catch { }
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var dir = Path.GetDirectoryName(exe);
            // v1.2 的「运行库」文件夹（认里面有 ScreenRecorderLib.dll 才删）
            var lib = Path.Combine(dir, "运行库");
            if (File.Exists(Path.Combine(lib, "ScreenRecorderLib.dll"))) TryDelete(lib);
            // v1.1 散在 exe 旁边的 dll / ffmpeg（认 ScreenRecorderLib.dll 在才算是我们的）
            if (File.Exists(Path.Combine(dir, "ScreenRecorderLib.dll")))
                foreach (var f in new[] { "ScreenRecorderLib.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "concrt140.dll", "ffmpeg.exe", "ffmpeg-LICENSE.txt" })
                    TryDelete(Path.Combine(dir, f));
            foreach (var f in new[] { "录屏.exe.config", Path.GetFileName(exe) + ".config" }) TryDelete(Path.Combine(dir, f));
            // 老安装包里的说明（认开头是我们写的那行才删，免得删掉桌面上别人的 README）
            var readme = Path.Combine(dir, "README.txt");
            try { if (File.Exists(readme) && File.ReadLines(readme).FirstOrDefault()?.StartsWith("录屏 · Screen Recorder") == true) File.Delete(readme); } catch { }
            // 自动更新时被换下来的旧 exe（那个进程可能还没完全退出，多试几次）
            for (int i = 0; i < 10 && File.Exists(exe + ".old"); i++) { TryDelete(exe + ".old"); if (File.Exists(exe + ".old")) System.Threading.Thread.Sleep(1000); }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    }
}
