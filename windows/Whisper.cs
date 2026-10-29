// 语音转文字（Windows）：whisper.cpp 的 whisper-cli.exe（CPU 版，随包嵌在 录屏.exe 里，MIT），模型第一次用时下载
// （ggml-base 多语言，约 148 MB，存 %LOCALAPPDATA%\录屏\models，校验 sha256）：先试 huggingface.co，不行换 hf-mirror.com（国内）。
// 全在本机识别，不上传声音。纯 .NET（Process + HttpWebRequest），Mac 上的测试工具也能跑（WHISPER_CLI / WHISPER_MODEL 指过去）
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ScreenRecorder
{
    static class Whisper
    {
        public const string ModelName = "ggml-base.bin";
        public const long ModelSize = 147951465;
        public const string ModelSha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
        static readonly string[] Urls =
        {
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        };

        // whisper-cli 在哪：单 exe 解出来的运行库文件夹；开发时在 exe 旁边（或「运行库」文件夹）；WHISPER_CLI 可以指定
        public static Func<string> Locate;
        public static string CliPath
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("WHISPER_CLI");
                if (!string.IsNullOrEmpty(env)) return env;
                var p = Locate?.Invoke();
                if (p != null && File.Exists(p)) return p;
                var b = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var c in new[] { Path.Combine(b, "whisper-cli.exe"), Path.Combine(b, Studio.LibDirName, "whisper-cli.exe") }) if (File.Exists(c)) return c;
                return null;
            }
        }

        public static string ModelDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "录屏", "models");
        public static string ModelPath => Environment.GetEnvironmentVariable("WHISPER_MODEL") ?? Path.Combine(ModelDir, ModelName);
        public static bool ModelReady => File.Exists(ModelPath) && (Environment.GetEnvironmentVariable("WHISPER_MODEL") != null || new FileInfo(ModelPath).Length == ModelSize);
        public static string LastSource;   // 自测：模型从哪下的（或 cached）

        // 没有模型就下载（progress 0–1）；下完校验大小和 sha256，对了才改成正式名字
        public static void EnsureModel(Action<double> progress, CancellationToken cancel)
        {
            if (ModelReady) { LastSource = "cached"; return; }
            Directory.CreateDirectory(ModelDir);
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            Exception last = null;
            var urls = Environment.GetEnvironmentVariable("WHISPER_MODEL_URL") is string u ? new[] { u } : Urls;
            foreach (var url in urls)
            {
                cancel.ThrowIfCancellationRequested();
                var tmp = ModelPath + ".part-" + Guid.NewGuid().ToString("N");
                try
                {
                    Download(url, tmp, progress, cancel);
                    var fi = new FileInfo(tmp);
                    if (fi.Length != ModelSize) throw new Exception($"size {fi.Length} != {ModelSize}");
                    using (var s = File.OpenRead(tmp)) using (var sha = SHA256.Create())
                    {
                        var h = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                        if (h != ModelSha256) throw new Exception("sha256 mismatch " + h);
                    }
                    if (File.Exists(ModelPath)) File.Delete(ModelPath);
                    File.Move(tmp, ModelPath);
                    LastSource = url;
                    return;
                }
                catch (OperationCanceledException) { TryDelete(tmp); throw; }
                catch (Exception e) { last = e; TryDelete(tmp); }
            }
            throw new Exception(App2.L("识别模型下载失败（检查网络或代理）：", "Couldn't download the speech model (check your network / proxy): ") + last?.Message);
        }

        static void Download(string url, string file, Action<double> progress, CancellationToken cancel)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "ScreenRecorder"; req.AllowAutoRedirect = true; req.Timeout = 30000; req.ReadWriteTimeout = 60000;
            using (cancel.Register(() => { try { req.Abort(); } catch { } }))
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(file))
            {
                long total = resp.ContentLength > 0 ? resp.ContentLength : ModelSize, got = 0;
                var buf = new byte[1 << 16]; int n; var tick = Stopwatch.StartNew();
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n); got += n;
                    if (tick.ElapsedMilliseconds > 200) { progress?.Invoke(Math.Min(1, got / (double)total)); tick.Restart(); }
                }
                progress?.Invoke(1);
            }
        }

        // 识别一段 16k 单声道 wav（中文），返回 whisper 的 json（-oj）；progress 0–1
        public static string Transcribe(string wav, Action<double> progress, CancellationToken cancel)
        {
            var cli = CliPath ?? throw new Exception(App2.L("找不到语音识别程序", "Speech recognizer is missing"));
            // whisper-cli 的命令行参数按系统代码页解码：路径里有「录屏」或中文用户名就会变成 ??。
            // 所以在模型文件夹里干活，参数只给相对路径（全是英文字母）：模型、输入的临时 wav、输出都放这里
            var model = Path.GetFullPath(ModelPath);
            var dir = Path.GetDirectoryName(model);
            var job = "job-" + Guid.NewGuid().ToString("N");
            File.Copy(Path.GetFullPath(wav), Path.Combine(dir, job + ".wav"), true);
            var outJson = Path.Combine(dir, job + ".json");
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
            // --prompt：让它出简体中文、带标点，并提示内容多半和录屏、剪辑有关（这些词常被听错）；-np 不打印结果、-pp 打印进度。
            // 中文提示词要 whisper-cli 按 UTF-8 收参数（打包时给它加了 activeCodePage=UTF-8 的清单，Win10 1903 起有效），老系统就不给提示词
            var prompt = Utf8Args ? " --prompt \"以下是普通话的句子，内容和录屏、剪辑、字幕有关。\"" : "";
            var args = $"-m \"{Path.GetFileName(model)}\" -f {job}.wav -l zh -t {threads} -oj -of {job} -np -pp{prompt}";
            var psi = new ProcessStartInfo(cli, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = dir,
            };
            var err = new StringBuilder();
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        lock (err) if (err.Length < 20000) err.AppendLine(e.Data);
                        var m = Regex.Match(e.Data, @"progress\s*=\s*(\d+)%");
                        if (m.Success) progress?.Invoke(int.Parse(m.Groups[1].Value) / 100.0);
                    };
                    p.OutputDataReceived += (s, e) => { };
                    p.BeginErrorReadLine(); p.BeginOutputReadLine();
                    while (!p.WaitForExit(200))
                        if (cancel.IsCancellationRequested) { try { p.Kill(); } catch { } throw new OperationCanceledException(cancel); }
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(outJson)) throw new Exception("whisper failed (" + p.ExitCode + "): " + Head(err.ToString()) + " … args: " + args);
                }
                return File.ReadAllText(outJson, Encoding.UTF8);
            }
            finally { TryDelete(outJson); TryDelete(Path.Combine(dir, job + ".wav")); }
        }

        // Windows 10 1903（build 18362）起，exe 清单里的 activeCodePage=UTF-8 才生效；Mac 测试工具里总是 UTF-8
        static bool Utf8Args
        {
            get
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return true;
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        return int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) && b >= 18362;
                }
                catch { return false; }
            }
        }

        static string Head(string s) { s = s.Trim(); return s.Length > 300 ? s.Substring(0, 300) : s; }
        static string Tail(string s) { s = s.Trim(); return s.Length > 600 ? s.Substring(s.Length - 600) : s; }
        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
