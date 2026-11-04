// 文字转配音（照 Mac 版 Speak.swift / TTSEngines.swift）：把字幕念成配音，存成 voice/tts-<字幕id前8位>-<4位>.wav，放到讲解轨上、从那句字幕开始；
// 同一句再念一次会替换上次念的那段。几种声音来源：
// - local：本机 AI 声音（Kokoro 高音质版，sherpa-onnx 跑；程序嵌在 exe 里，声音模型第一次用时下载约 360MB；Apache-2.0）
// - volc / azure / openai / qwen：云端声音（用户自己填 key，按字数付费；key 存在 Windows 凭据管理器里，同 Mac 的钥匙串）
// - system：Windows 自带声音（不用下载，但比较生硬）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static ScreenRecorder.App2;

namespace ScreenRecorder
{
    static class Tts
    {
        public static readonly string[] Engines = { "local", "volc", "azure", "openai", "qwen", "system" };
        public static string Name(string e)
        {
            switch (e)
            {
                case "volc": return L("火山引擎·豆包（云端，剪映同款）", "Volcengine Doubao (cloud)");
                case "azure": return L("微软 Azure（云端）", "Microsoft Azure (cloud)");
                case "openai": return "OpenAI（" + L("云端", "cloud") + "）";
                case "qwen": return L("阿里百炼 千问（云端）", "Alibaba Qwen TTS (cloud)");
                case "system": return L("系统声音（生硬）", "Windows voice (robotic)");
                default: return L("本机 AI 声音（推荐，免费）", "On-device AI voice (free)");
            }
        }
        public static bool Cloud(string e) => e == "volc" || e == "azure" || e == "openai" || e == "qwen";
        // 申请 key 的地方
        public static (string text, string url)? KeyHelp(string e)
        {
            switch (e)
            {
                case "volc": return (L("在火山引擎「豆包语音」控制台开通语音合成，拿 AppID 和 Access Token", "Enable TTS in the Volcengine Doubao Speech console for an AppID + Access Token"), "https://console.volcengine.com/speech/app");
                case "azure": return (L("在 Azure 门户建一个「语音服务」，拿 key 和区域", "Create a Speech resource in the Azure portal for a key + region"), "https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices");
                case "openai": return (L("在 OpenAI 后台创建 API key", "Create an API key in the OpenAI dashboard"), "https://platform.openai.com/api-keys");
                case "qwen": return (L("在阿里云百炼控制台创建 API key", "Create an API key in Alibaba Model Studio"), "https://bailian.console.aliyun.com/?tab=model#/api-key");
                default: return null;
            }
        }
        // 每家可选的声音：(值, 显示名)；同 Mac 版
        public static List<(string id, string name)> Voices(string e)
        {
            switch (e)
            {
                case "local": return LocalTts.Voices;
                case "volc":
                    return new List<(string, string)> {
                        ("zh_female_shuangkuaisisi_moon_bigtts", L("爽快思思（女）", "Sisi (female)")), ("zh_male_wennuanahu_moon_bigtts", L("温暖阿虎（男）", "Ahu (male)")),
                        ("zh_female_tianmeixiaoyuan_moon_bigtts", L("甜美小源（女）", "Xiaoyuan (female)")), ("zh_male_shaonianzixin_moon_bigtts", L("少年梓辛（男）", "Zixin (male)")),
                        ("zh_female_wanwanxiaohe_moon_bigtts", L("湾湾小何（台湾腔）", "Xiaohe (Taiwanese)")), ("zh_male_jingqiangkanye_moon_bigtts", L("京腔侃爷（北京话）", "Kanye (Beijing)")),
                        ("zh_male_M392_conversation_wvae_bigtts", L("对话男声", "Conversational male")),
                        ("BV001_streaming", L("通用女声（免费）", "Standard female (free)")), ("BV002_streaming", L("通用男声（免费）", "Standard male (free)")) };
                case "azure":
                    return new List<(string, string)> {
                        ("zh-CN-YunxiNeural", L("云希（男，年轻）", "Yunxi (male)")), ("zh-CN-YunjianNeural", L("云健（男，浑厚）", "Yunjian (male)")),
                        ("zh-CN-YunyangNeural", L("云扬（男，播音）", "Yunyang (male, news)")), ("zh-CN-XiaoxiaoNeural", L("晓晓（女，温暖）", "Xiaoxiao (female)")),
                        ("zh-CN-XiaoyiNeural", L("晓伊（女，活泼）", "Xiaoyi (female)")), ("zh-CN-XiaochenNeural", L("晓辰（女，自然）", "Xiaochen (female)")),
                        ("en-US-AndrewMultilingualNeural", "Andrew (EN)"), ("en-US-AvaMultilingualNeural", "Ava (EN)") };
                case "openai":
                    return new[] { "alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse" }.Select(v => (v, char.ToUpperInvariant(v[0]) + v.Substring(1))).ToList();
                case "qwen":
                    return new List<(string, string)> {
                        ("Cherry", L("芊悦（女，阳光）", "Cherry (female)")), ("Serena", L("苏瑶（女，温柔）", "Serena (female)")), ("Ethan", L("晨煦（男，阳光）", "Ethan (male)")),
                        ("Chelsie", L("千雪（女，二次元）", "Chelsie (female)")), ("Dylan", L("晓东（北京话）", "Dylan (Beijing)")), ("Jada", L("阿珍（上海话）", "Jada (Shanghai)")), ("Sunny", L("晴儿（四川话）", "Sunny (Sichuan)")) };
                default: return SystemTts.Voices();
            }
        }

        // ---- 现在用哪种声音、哪个声音（每种各记一个）、语速 ----
        public static string Engine
        {
            get { var e = Settings.GetString("ttsEngine", "local"); return Engines.Contains(e) ? e : "local"; }
            set => Settings.SetString("ttsEngine", value);
        }
        public static string Voice(string e) => Settings.GetString("ttsVoice." + e, e == "system" ? "" : Voices(e).FirstOrDefault().id ?? "");
        public static void SetVoice(string e, string v) => Settings.SetString("ttsVoice." + e, v ?? "");
        public static double Rate
        {
            get => double.TryParse(Settings.GetString("ttsRate", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? Math.Min(Math.Max(r, 0.5), 1.6) : 1;
            set => Settings.SetString("ttsRate", Math.Min(Math.Max(value, 0.5), 1.6).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        // 念一句：按选的声音来源，存到 dir\voice\tts-xxxx.wav，返回（工程里的文件名, 时长）。
        // 每次都用新文件名：念失败时原来那段配音还在（旧文件留着，撤销也要用）
        public static (string file, double length) SynthLine(string dir, string text, string captionId, Action<string, double?> status, CancellationToken cancel)
        {
            string e = Engine, v = Voice(e);
            var id8 = new string((captionId ?? "").Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
            var stem = "voice/tts-" + id8 + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            Directory.CreateDirectory(Path.Combine(dir, "voice"));
            string full = Path.Combine(dir, stem.Replace('/', '\\') + ".wav");
            if (e == "system") SystemTts.Synth(text, v, Rate, full);
            else if (e == "local")
            {
                LocalTts.EnsureModel(f => status?.Invoke(L("第一次用：下载声音模型（约 360MB，只下一次）", "First use: downloading the voice model (~360MB, once)"), f), cancel);
                LocalTts.Synth(text, v, Rate, full, cancel);
            }
            else full = CloudTts.Synth(e, text, v, Rate, full);
            var name = "voice/" + Path.GetFileName(full);
            return (name, Length(full));
        }

        // 音频文件有多长（秒）：wav 读文件头，别的用 ffmpeg 解一遍
        public static double Length(string file)
        {
            try
            {
                using (var r = new BinaryReader(File.OpenRead(file)))
                {
                    if (new string(r.ReadChars(4)) == "RIFF")
                    {
                        r.ReadInt32(); r.ReadChars(4);
                        int byteRate = 0;
                        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                        {
                            var id = new string(r.ReadChars(4)); int size = r.ReadInt32();
                            if (id == "fmt ") { var f = r.ReadBytes(size); byteRate = BitConverter.ToInt32(f, 8); }
                            else if (id == "data") { long sz = size; if (sz <= 0 || sz > r.BaseStream.Length - r.BaseStream.Position) sz = r.BaseStream.Length - r.BaseStream.Position; return byteRate > 0 ? sz / (double)byteRate : 0; }
                            else r.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                        }
                    }
                }
            }
            catch { }
            try { var pcm = Studio.DecodePcm(file, 8000); return pcm == null ? 0 : pcm.Length / 8000.0; } catch { return 0; }
        }
    }

    // ---- key 存 Windows 凭据管理器（「控制面板 → 凭据管理器 → Windows 凭据」里看得到），名字「录屏-tts-<来源>/<格>」 ----
    static class TtsKeys
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public int Flags, Type; public string TargetName, Comment; public long LastWritten;
            public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist, AttributeCount; public IntPtr Attributes; public string TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL c, int flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, int type, int flags, out IntPtr cred);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, int type, int flags);
        [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);
        const int Generic = 1, LocalMachine = 2;

        static string Target(string e, string field) => "录屏-tts-" + e + "/" + field;
        public static string Get(string e, string field = "key")
        {
            if (!CredReadW(Target(e, field), Generic, 0, out var p)) return "";
            try
            {
                var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                if (c.CredentialBlobSize <= 0) return "";
                var b = new byte[c.CredentialBlobSize]; Marshal.Copy(c.CredentialBlob, b, 0, b.Length);
                return Encoding.UTF8.GetString(b);
            }
            finally { CredFree(p); }
        }
        public static void Set(string e, string field, string v)
        {
            CredDeleteW(Target(e, field), Generic, 0);
            if (string.IsNullOrEmpty(v)) return;
            var b = Encoding.UTF8.GetBytes(v); var h = Marshal.AllocHGlobal(b.Length);
            try
            {
                Marshal.Copy(b, 0, h, b.Length);
                var c = new CREDENTIAL { Type = Generic, TargetName = Target(e, field), CredentialBlobSize = b.Length, CredentialBlob = h, Persist = LocalMachine, UserName = field };
                CredWriteW(ref c, 0);
            }
            finally { Marshal.FreeHGlobal(h); }
        }
    }

    // ---- 本机 AI 声音（Kokoro，sherpa-onnx-offline-tts.exe）----
    static class LocalTts
    {
        // 声音：Kokoro v1.1 的编号 3–57 是中文女声、58–102 是中文男声、0–2 英文女声（同 Mac 版挑的那些）
        public static readonly List<(string id, string name)> Voices = Make();
        static List<(string, string)> Make()
        {
            var v = new List<(string, string)>();
            int[] female = { 3, 8, 14, 22, 30, 41, 50, 56 }, male = { 58, 63, 68, 74, 80, 86, 93, 100 };
            for (int i = 0; i < female.Length; i++) v.Add((female[i].ToString(), L($"女声 {i + 1}", $"Female {i + 1}")));
            for (int i = 0; i < male.Length; i++) v.Add((male[i].ToString(), L($"男声 {i + 1}", $"Male {i + 1}")));
            v.Add(("0", L("英文女声（Maple）", "English female (Maple)")));
            return v;
        }

        public const string ModelName = "kokoro-multi-lang-v1_1";   // 高音质版（不压缩，约 360MB 下载），同 Mac 版
        public const long TarballSize = 364_855_331;
        public static string Root => Environment.GetEnvironmentVariable("TTS_MODEL_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "录屏", "models");
        public static string ModelDir => Path.Combine(Root, ModelName);
        public static bool Ready => File.Exists(Path.Combine(ModelDir, "voices.bin")) && File.Exists(Path.Combine(ModelDir, "model.onnx")) && File.Exists(Path.Combine(ModelDir, "tokens.txt"));
        public static string LastSource;   // 自测：模型从哪下的（或 cached）

        // sherpa-onnx-offline-tts.exe 在哪：单 exe 解出来的运行库文件夹；开发时在 exe 旁边；SHERPA_TTS 可以指定
        public static Func<string> Locate;
        public static string Helper
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SHERPA_TTS");
                if (!string.IsNullOrEmpty(env)) return env;
                var p = Locate?.Invoke();
                if (p != null && File.Exists(p)) return p;
                var b = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var c in new[] { Path.Combine(b, "sherpa-onnx-offline-tts.exe"), Path.Combine(b, Studio.LibDirName, "sherpa-onnx-offline-tts.exe") }) if (File.Exists(c)) return c;
                return null;
            }
        }

        // 第一次用：下载声音模型。国内先走 hf-mirror 国内镜像（一个个文件下），其他地方先走 GitHub（一个压缩包）；一个不通换另一个。
        // 先下到临时文件夹、齐了再挪过去：下到一半被打断不会留下「看着齐了其实缺文件」的模型
        static readonly object gate = new object();
        public static void EnsureModel(Action<double> progress, CancellationToken cancel)
        {
            lock (gate)
            {
                if (Ready) { LastSource = "cached"; return; }
                Directory.CreateDirectory(Root);
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                bool cn = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh-CN") || System.Globalization.CultureInfo.CurrentCulture.Name == "zh-CN" || TimeZoneInfo.Local.Id == "China Standard Time";
                var tries = new List<(string name, Action run)> { ("mirror", () => MirrorFiles(progress, cancel)), ("github", () => GithubTarball(progress, cancel)) };
                if (!cn) tries.Reverse();
                if (Environment.GetEnvironmentVariable("TTS_MODEL_SOURCE") is string only) tries = tries.Where(t => t.name == only).ToList();
                Exception last = null;
                foreach (var t in tries)
                {
                    cancel.ThrowIfCancellationRequested();
                    try { t.run(); if (Ready) { LastSource = t.name; return; } }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { last = e; }
                }
                throw new Exception(L("声音模型下载失败（检查网络或代理）：", "Couldn't download the voice model (check your network / proxy): ") + last?.Message);
            }
        }

        // GitHub：一个 tar.bz2（约 360MB），用 Windows 自带的 tar.exe 解开
        static void GithubTarball(Action<double> progress, CancellationToken cancel)
        {
            var tmp = Path.Combine(Root, ".kokoro-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tar.bz2");
            var stage = Path.Combine(Root, ".unpack-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                Fetch($"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{ModelName}.tar.bz2", tmp, TarballSize, f => progress?.Invoke(f * 0.95), cancel);
                Directory.CreateDirectory(stage);
                var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                if (!File.Exists(tar)) throw new Exception("tar.exe not found");
                var psi = new ProcessStartInfo(tar, $"-xjf \"{tmp}\" -C \"{stage}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    while (!p.WaitForExit(300)) if (cancel.IsCancellationRequested) { try { p.Kill(); } catch { } throw new OperationCanceledException(cancel); }
                    if (p.ExitCode != 0) throw new Exception("tar " + p.ExitCode + " " + err.Result);
                }
                var got = Path.Combine(stage, ModelName);
                if (!File.Exists(Path.Combine(got, "voices.bin"))) throw new Exception("unpack failed");
                if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true);
                Directory.Move(got, ModelDir);
                progress?.Invoke(1);
            }
            finally { TryDelete(tmp); try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } }
        }

        // 国内镜像：先问有哪些文件，再按文件一个个下（同时下 6 个）
        static void MirrorFiles(Action<double> progress, CancellationToken cancel)
        {
            const string host = "https://hf-mirror.com", repo = "csukuangfj/" + ModelName;
            string listJson;
            using (var resp = Open($"{host}/api/models/{repo}", cancel)) using (var r = new StreamReader(resp.GetResponseStream())) listJson = r.ReadToEnd();
            var j = (Dictionary<string, object>)Json.Parse(listJson);
            var files = ((List<object>)j["siblings"]).Cast<Dictionary<string, object>>().Select(x => x["rfilename"] as string)
                .Where(f => f != null && !f.StartsWith(".") && f != "README.md").ToList();
            if (files.Count == 0) throw new Exception("empty file list");
            var stage = Path.Combine(Root, ".stage-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                int done = 0; Exception fail = null;
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancel }, (f, st) =>
                {
                    try
                    {
                        var url = host + "/" + repo + "/resolve/main/" + string.Join("/", f.Split('/').Select(Uri.EscapeDataString));
                        Fetch(url, Path.Combine(stage, f.Replace('/', '\\')), 0, null, cancel);
                        progress?.Invoke(Interlocked.Increment(ref done) / (double)files.Count);
                    }
                    catch (Exception e) { fail = e; st.Stop(); }
                });
                if (fail != null) throw fail;
                if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true);
                Directory.Move(stage, ModelDir);
            }
            finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } }
        }

        // 发 GET，跟着跳转走：.NET Framework 自己不跟 308（hf-mirror.com 现在 308 到 huggingface.co），这里手动跟，最多 8 次
        static HttpWebResponse Open(string url, CancellationToken cancel)
        {
            for (int hop = 0; ; hop++)
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "ScreenRecorder"; req.AllowAutoRedirect = true; req.Timeout = 30000; req.ReadWriteTimeout = 60000;
                using (cancel.Register(() => { try { req.Abort(); } catch { } }))
                {
                    try { return (HttpWebResponse)req.GetResponse(); }
                    catch (WebException we) when (we.Response is HttpWebResponse r && (int)r.StatusCode == 308 && hop < 8 && r.Headers["Location"] is string loc)
                    {
                        r.Dispose();
                        url = new Uri(new Uri(url), loc).ToString();
                    }
                    catch (WebException) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
                }
            }
        }

        static void Fetch(string url, string file, long size, Action<double> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var part = file + ".part";
            using (var resp = Open(url, cancel))
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(part))
            {
                if (resp.StatusCode != HttpStatusCode.OK) throw new Exception("HTTP " + (int)resp.StatusCode);
                long total = resp.ContentLength > 0 ? resp.ContentLength : size, got = 0;
                var buf = new byte[1 << 16]; int n; var tick = Stopwatch.StartNew();
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n); got += n;
                    if (progress != null && total > 0 && tick.ElapsedMilliseconds > 200) { progress(Math.Min(1, got / (double)total)); tick.Restart(); }
                }
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(part, file);
        }

        // 念成 wav（24k）：sid = 声音编号，speed 1 = 正常。
        // sherpa-onnx-offline-tts 按系统代码页收命令行参数：打包时给它加了 activeCodePage=UTF-8 的清单（Win10 1903 起有效），
        // 老系统上中文字传不过去；路径都在模型文件夹里给相对路径（全是英文字母），用户名、「录屏」这些中文不进参数
        public static void Synth(string text, string sid, double speed, string outWav, CancellationToken cancel)
        {
            var exe = Helper ?? throw new Exception(L("找不到本机配音程序", "The on-device voice program is missing"));
            if (!Whisper.Utf8Args && text.Any(c => c > 127)) throw new Exception(L("本机 AI 声音念中文要 Windows 10 1903 或更新的系统；可以换系统声音或云端声音", "The on-device voice needs Windows 10 1903 or later for Chinese; try the Windows or a cloud voice"));
            var m = ModelDir;
            var job = ".job-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav";
            string Arg(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
            text = Regex.Replace(text, @"[\r\n]+", " ").Trim();
            if (text.StartsWith("-")) text = " " + text;   // 以 - 开头的字会被当成参数
            int threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
            var args = string.Join(" ", new[]
            {
                "--debug=0", $"--num-threads={threads}",
                "--kokoro-model=model.onnx", "--kokoro-voices=voices.bin", "--kokoro-tokens=tokens.txt",
                "--kokoro-data-dir=espeak-ng-data", "--kokoro-dict-dir=dict",
                "--kokoro-lexicon=lexicon-us-en.txt,lexicon-zh.txt",
                "--tts-rule-fsts=date-zh.fst,number-zh.fst",
                "--kokoro-length-scale=" + (1 / Math.Min(Math.Max(speed, 0.5), 2)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                "--sid=" + (string.IsNullOrEmpty(sid) ? "3" : sid), "--output-filename=" + job, Arg(text),
            });
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = m,
            };
            var log = new StringBuilder();
            var jobPath = Path.Combine(m, job);
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) if (log.Length < 20000) log.AppendLine(e.Data); };
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) if (log.Length < 20000) log.AppendLine(e.Data); };
                    p.BeginErrorReadLine(); p.BeginOutputReadLine();
                    while (!p.WaitForExit(200)) if (cancel.IsCancellationRequested) { try { p.Kill(); } catch { } throw new OperationCanceledException(cancel); }
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(jobPath)) { var s = log.ToString().Trim(); throw new Exception(L("本机配音出错：", "On-device voice failed: ") + (s.Length > 300 ? s.Substring(s.Length - 300) : s)); }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outWav));
                if (File.Exists(outWav)) File.Delete(outWav);
                File.Move(jobPath, outWav);
            }
            finally { TryDelete(jobPath); }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }

    // ---- 云端声音（同 Mac 版 CloudTTS）----
    static class CloudTts
    {
        static Exception Fail(string s) => new Exception(s);
        // 念成音频文件，返回文件（扩展名按格式）
        public static string Synth(string e, string text, string voice, double speed, string basePath)
        {
            var key = TtsKeys.Get(e);
            if (key == "") throw Fail(L($"先在「文字转配音」里填 {Tts.Name(e)} 的 key", $"Enter your {Tts.Name(e)} key first"));
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            string url, ctype = "application/json", body; var headers = new Dictionary<string, string>();
            string ext = "wav";
            double sp = Math.Min(Math.Max(speed, 0.5), 2);
            switch (e)
            {
                case "volc":
                {
                    // 豆包语音合成（V1 HTTP）：AppID 填在 region 那一格，Access Token 是 key；头里是「Bearer;」加 token
                    var appid = TtsKeys.Get(e, "region");
                    if (appid == "") throw Fail(L("还要填火山引擎的 AppID", "Enter your Volcengine AppID too"));
                    url = "https://openspeech.bytedance.com/api/v1/tts";
                    headers["Authorization"] = "Bearer;" + key;
                    body = J(new Dictionary<string, object>
                    {
                        ["app"] = new Dictionary<string, object> { ["appid"] = appid, ["token"] = "access_token", ["cluster"] = "volcano_tts" },
                        ["user"] = new Dictionary<string, object> { ["uid"] = "recorder" },
                        ["audio"] = new Dictionary<string, object> { ["voice_type"] = voice, ["encoding"] = "wav", ["speed_ratio"] = sp },
                        ["request"] = new Dictionary<string, object> { ["reqid"] = Guid.NewGuid().ToString(), ["text"] = text, ["operation"] = "query" },
                    });
                    break;
                }
                case "azure":
                {
                    var region = TtsKeys.Get(e, "region"); if (region == "") region = "eastasia";
                    url = $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1";
                    headers["Ocp-Apim-Subscription-Key"] = key; headers["X-Microsoft-OutputFormat"] = "riff-24khz-16bit-mono-pcm";
                    ctype = "application/ssml+xml";
                    var lang = string.Join("-", voice.Split('-').Take(2));
                    var esc = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                    var rate = ((speed - 1) * 100).ToString("+0;-0;+0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                    body = $"<speak version='1.0' xml:lang='{lang}'><voice name='{voice}'><prosody rate='{rate}'>{esc}</prosody></voice></speak>";
                    break;
                }
                case "openai":
                    url = "https://api.openai.com/v1/audio/speech";
                    headers["Authorization"] = "Bearer " + key;
                    body = J(new Dictionary<string, object>
                    {
                        ["model"] = "gpt-4o-mini-tts", ["voice"] = voice, ["input"] = text, ["response_format"] = "wav",
                        ["instructions"] = speed > 1.1 ? "Speak a little faster than normal." : speed < 0.9 ? "Speak a little slower than normal." : "Speak naturally.",
                    });
                    break;
                case "qwen":
                    url = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
                    headers["Authorization"] = "Bearer " + key;
                    body = J(new Dictionary<string, object> { ["model"] = "qwen-tts-latest", ["input"] = new Dictionary<string, object> { ["text"] = text, ["voice"] = voice } });   // latest 才有方言声音
                    break;
                default: throw Fail("bad engine");
            }
            var (code, data) = Post(url, ctype, headers, Encoding.UTF8.GetBytes(body));
            if (code != 200) throw Fail($"{Tts.Name(e)}：HTTP {code} " + Head(data));
            var audio = data;
            if (e == "volc")
            {
                Dictionary<string, object> j;
                try { j = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(data)); } catch { throw Fail(L("火山引擎返回的不对", "Bad response from Volcengine")); }
                if (!(j.TryGetValue("code", out var c) && Convert.ToInt32(c) == 3000 && j.TryGetValue("data", out var d) && d is string b64))
                    throw Fail($"火山引擎：{(j.TryGetValue("code", out var c2) ? c2 : "")} {(j.TryGetValue("message", out var msg) ? msg : "")}");
                audio = Convert.FromBase64String(b64);
            }
            if (e == "qwen")
            {
                // 千问返回的是 JSON，里面给音频文件的地址
                string u = null;
                try
                {
                    var j = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(data));
                    u = ((j["output"] as Dictionary<string, object>)?["audio"] as Dictionary<string, object>)?["url"] as string;
                }
                catch { }
                if (string.IsNullOrEmpty(u)) throw Fail(L("千问没返回音频", "Qwen returned no audio"));
                using (var wc = new WebClient()) audio = wc.DownloadData(u);
                var x = Path.GetExtension(new Uri(u).AbsolutePath).TrimStart('.');
                if (x != "") ext = x;
            }
            var outPath = Path.ChangeExtension(basePath, ext);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, audio);
            return outPath;
        }

        static (int code, byte[] data) Post(string url, string ctype, Dictionary<string, string> headers, byte[] body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST"; req.ContentType = ctype; req.UserAgent = "Recorder"; req.Timeout = 60000; req.ReadWriteTimeout = 60000;
            foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException we) when (we.Response is HttpWebResponse r) { resp = r; }
            using (resp)
            using (var ms = new MemoryStream())
            {
                resp.GetResponseStream()?.CopyTo(ms);
                return ((int)resp.StatusCode, ms.ToArray());
            }
        }
        // 拼请求的 JSON（只用到对象、字符串、数字）
        static string J(object o)
        {
            switch (o)
            {
                case Dictionary<string, object> d: return "{" + string.Join(",", d.Select(kv => J(kv.Key) + ":" + J(kv.Value))) + "}";
                case string str:
                {
                    var b = new StringBuilder("\"");
                    foreach (var c in str)
                        switch (c)
                        {
                            case '"': b.Append("\\\""); break;
                            case '\\': b.Append("\\\\"); break;
                            case '\n': b.Append("\\n"); break;
                            case '\r': b.Append("\\r"); break;
                            case '\t': b.Append("\\t"); break;
                            default: if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                        }
                    return b.Append('"').ToString();
                }
                case bool bo: return bo ? "true" : "false";
                case IFormattable f: return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                default: return "null";
            }
        }
        static string Head(byte[] d) { var s = Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, 300)); return s; }
    }

    // ---- Windows 自带声音（System.Speech / SAPI）----
    static class SystemTts
    {
        // 中文、英文的声音，中文排前面
        public static List<(string id, string name)> Voices()
        {
            try
            {
                using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
                    return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo)
                        .Where(v => v.Culture.Name.StartsWith("zh") || v.Culture.Name.StartsWith("en"))
                        .OrderBy(v => v.Culture.Name.StartsWith("zh") ? 0 : 1).ThenBy(v => v.Name)
                        .Select(v => (v.Name, $"{v.Name}（{v.Culture.Name}）")).ToList();
            }
            catch { return new List<(string, string)>(); }
        }
        // 没选过就挑：中文字用中文声音，英文用英文声音
        public static string Pick(string text, string saved)
        {
            var all = Voices();
            if (!string.IsNullOrEmpty(saved) && all.Any(v => v.id == saved)) return saved;
            bool zh = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            return all.FirstOrDefault(v => v.name.Contains(zh ? "（zh-CN）" : "（en-US）")).id ?? all.FirstOrDefault().id;
        }
        // 语速：SAPI 的 -10–10 大约是每 10 档快 3 倍
        public static int SapiRate(double speed) => (int)Math.Round(Math.Min(10, Math.Max(-10, 10 * Math.Log(Math.Min(Math.Max(speed, 0.3), 3)) / Math.Log(3))));

        public static void Synth(string text, string voice, double speed, string outWav)
        {
            Exception err = null;
            // SAPI 要在 STA 线程里用
            var th = new Thread(() =>
            {
                try
                {
                    using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
                    {
                        var v = Pick(text, voice);
                        if (v == null) throw new Exception(L("这台电脑没有可用的系统声音", "No Windows voices are installed"));
                        s.SelectVoice(v); s.Rate = SapiRate(speed);
                        Directory.CreateDirectory(Path.GetDirectoryName(outWav));
                        s.SetOutputToWaveFile(outWav, new System.Speech.AudioFormat.SpeechAudioFormatInfo(24000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                        s.Speak(text);
                        s.SetOutputToNull();
                    }
                }
                catch (Exception e) { err = e; }
            });
            th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
            if (err != null) throw err;
            if (!File.Exists(outWav) || new FileInfo(outWav).Length < 100) throw new Exception(L("这句念不出来", "Couldn't speak this text"));
        }
    }
}
