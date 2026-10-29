using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    // 自动更新（后台静默）：查 GitHub 最新 Release（tag 形如 v1.3），比自己新就在后台下载、核对后放到 %LOCALAPPDATA%\录屏\update\；
    // 退出程序时把正在运行的自己改名成 .old、新的放到原来的名字（下次启动删 .old、提示「已更新」）。
    // 被强制结束没来得及换的话，下次启动先换上再自动重开一次。想马上用可以点「现在重启」
    static class Updater
    {
        public const string Repo = "Hejun-Coderlife/mac-recorder";
        public const string ExeAsset = "ScreenRecorder.exe";              // v1.3 起：单个 exe（GitHub 资源名不能用中文）
        public const string AssetName = "ScreenRecorder-Windows-x64.zip";   // v1.2 的更新器只认这个；新版找不到 exe 时也用它
        public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";
        static readonly TimeSpan Interval = TimeSpan.FromHours(6);
        public static bool RestartAfterExit;   // 点了「现在重启」

        public sealed class Release
        {
            public string Tag;      // v1.3
            public Version Version;
            public string Url;      // 单个 exe 或 zip（也可以是本地文件，自测用）
        }

        // 自己的版本：csproj 的 <Version>；UPDATE_FAKE_VERSION 可以假装是旧版（自测用）
        public static Version Current
        {
            get
            {
                var fake = Environment.GetEnvironmentVariable("UPDATE_FAKE_VERSION");
                return fake != null ? Parse(fake) : typeof(Updater).Assembly.GetName().Version;
            }
        }

        public static string Show(Version v) => v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

        // 「1.2」「v1.2.3」→ Version，各段按数字比较；不认识的返回 0.0
        public static Version Parse(string s)
        {
            var parts = Regex.Matches(s ?? "", @"\d+").Cast<Match>().Take(4).Select(m => int.Parse(m.Value)).ToList();
            while (parts.Count < 2) parts.Add(0);
            return parts.Count == 2 ? new Version(parts[0], parts[1]) : parts.Count == 3 ? new Version(parts[0], parts[1], parts[2]) : new Version(parts[0], parts[1], parts[2], parts[3]);
        }

        static int Compare(Version a, Version b)
        {
            int[] x = { a.Major, a.Minor, Math.Max(0, a.Build), Math.Max(0, a.Revision) }, y = { b.Major, b.Minor, Math.Max(0, b.Build), Math.Max(0, b.Revision) };
            for (int i = 0; i < 4; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }

        // 自动查：离上次查不到 6 小时就跳过（记在 settings.txt）
        public static bool Due
        {
            get
            {
                long.TryParse(Settings.GetString("lastUpdateCheck", "0"), out var ticks);
                return DateTime.UtcNow - new DateTime(Math.Max(0, Math.Min(ticks, DateTime.MaxValue.Ticks)), DateTimeKind.Utc) >= Interval;
            }
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new WebClient { Encoding = Encoding.UTF8 };
            c.Headers[HttpRequestHeader.UserAgent] = "ScreenRecorder-Windows/" + Show(Current);
            return c;
        }

        // 查最新版；有比自己新的就返回它，否则 null（网络出错抛异常）
        public static async Task<Release> CheckAsync()
        {
            Settings.SetString("lastUpdateCheck", DateTime.UtcNow.Ticks.ToString());
            string json;
            using (var c = Client())
            {
                c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                // CI 自测时带上令牌，免得共享 IP 撞上 GitHub 的匿名限额
                var token = Environment.GetEnvironmentVariable("UPDATE_TOKEN");
                if (!string.IsNullOrEmpty(token)) c.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                json = await c.DownloadStringTaskAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            }
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            var urls = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var url = urls.FirstOrDefault(u => u.EndsWith("/" + ExeAsset, StringComparison.OrdinalIgnoreCase))
                   ?? urls.FirstOrDefault(u => u.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase));
            if (tag == "" || url == null) return null;
            var v = Parse(tag);
            return Compare(v, Current) > 0 ? new Release { Tag = tag, Version = v, Url = url } : null;
        }

        public static string AppDir => Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);

        // 程序文件夹能不能写（装在 Program Files 之类的地方就写不了，只能手动更新）
        public static bool CanWriteAppDir()
        {
            try
            {
                var probe = Path.Combine(AppDir, ".update-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // 下载到临时文件夹，返回新的 录屏.exe（zip 的话解出里面的 录屏.exe）
        public static async Task<string> DownloadAsync(Release r, Action<int> progress)
        {
            var work = Path.Combine(Path.GetTempPath(), "录屏-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var isZip = r.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var file = Path.Combine(work, isZip ? AssetName : ExeAsset);
            if (File.Exists(r.Url)) File.Copy(r.Url, file);   // 自测：UPDATE_EXE / UPDATE_ZIP 给的本地文件
            else
                using (var c = Client())
                {
                    c.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                    await c.DownloadFileTaskAsync(new Uri(r.Url), file);
                }
            if (isZip)
            {
                var dir = Path.Combine(work, "new");
                await Task.Run(() => ZipFile.ExtractToDirectory(file, dir));
                file = Path.Combine(dir, "录屏.exe");
                if (!File.Exists(file)) throw new Exception(L("下载的安装包里没有 录屏.exe", "录屏.exe is missing from the downloaded package"));
            }
            // 起码得是个 Windows 程序（MZ 开头、不太小），免得下到一个网页之类的把自己换坏
            using (var f = File.OpenRead(file))
                if (f.Length < 100_000 || f.ReadByte() != 'M' || f.ReadByte() != 'Z') throw new Exception(L("下载的文件不对", "The downloaded file is not valid"));
            return file;
        }

        // ---- 后台下好的新版 ----
        public static string StageDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "录屏", "update");
        static string StagedExe => Path.Combine(StageDir, ExeAsset);

        static Version FileVersionOf(string exe)
        {
            var fv = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (string.IsNullOrEmpty(fv)) throw new Exception(L("下载的文件读不出版本号", "Can't read the version of the downloaded file"));
            return Parse(fv);
        }

        // 已经下好、比自己新的那个版本；没有返回 null
        public static Version StagedVersion
        {
            get
            {
                try
                {
                    if (!File.Exists(StagedExe)) return null;
                    var v = FileVersionOf(StagedExe);
                    return Compare(v, Current) > 0 ? v : null;
                }
                catch { return null; }
            }
        }

        // 下载并核对（Windows 程序、版本号读得出、不比现在旧），放进 StageDir；返回新版本号
        public static async Task<Version> StageAsync(Release r, Action<int> progress)
        {
            var exe = await DownloadAsync(r, progress);
            var v = FileVersionOf(exe);
            if (Compare(v, Current) < 0) throw new Exception(L("下载的版本比现在的还旧", "The downloaded version is older than this one"));
            try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true); } catch { }
            Directory.CreateDirectory(StageDir);
            File.Copy(exe, StagedExe, true);
            try { Directory.Delete(Path.GetDirectoryName(exe), true); } catch { }
            return v;
        }

        // 把下好的新版换上去（退出时 / 点「现在重启」/ 上次没换成的下次启动）；成功后记下「已更新到」
        public static bool ApplyStaged(bool relaunch)
        {
            var v = StagedVersion;
            if (v == null) { try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true); } catch { } return false; }
            try
            {
                Replace(StagedExe, relaunch);
                Settings.SetString("updatedTo", Show(v));
                return true;
            }
            catch
            {
                // 换不上（比如文件夹没写权限）：丢掉这个包，免得每次启动都重试
                try { Directory.Delete(StageDir, true); } catch { }
                return false;
            }
        }

        // 刚换上新版后第一次打开：返回「已更新到」的版本号（只返回一次）
        public static string TakeUpdatedTo()
        {
            var v = Settings.GetString("updatedTo", "");
            if (v == "") return null;
            Settings.SetString("updatedTo", "");
            return Compare(Parse(v), Current) == 0 ? v : null;
        }

        // 换掉自己：运行中的 exe 可以改名，先改成 .old，再把新的放到原来的名字；出错就改回去。调用方随后退出程序
        public static void Replace(string newExe, bool relaunch)
        {
            var exe = System.Windows.Forms.Application.ExecutablePath;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(newExe, exe); }
            catch { try { if (File.Exists(exe)) File.Delete(exe); File.Move(old, exe); } catch { } throw; }
            try { Directory.Delete(Path.GetDirectoryName(newExe), true); } catch { }
            if (relaunch) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppDir });
        }

        // 自测（不弹窗）：UPDATE_TEST=1 [UPDATE_FAKE_VERSION=1.0] [UPDATE_NORELAUNCH=1] [UPDATE_TOKEN=令牌] [UPDATE_EXE=本地 exe | UPDATE_ZIP=本地 zip]
        // 走后台那套：查最新版 → 下载核对放进 update 文件夹 → 模拟退出把它换上；结果写 %TEMP%\update_test.txt（STAGED… 一行，再 REPLACED OK / FAIL）
        public static int SelfTest()
        {
            var result = Path.Combine(Path.GetTempPath(), "update_test.txt");
            try { File.Delete(result); } catch { }
            try
            {
                var local = Environment.GetEnvironmentVariable("UPDATE_EXE") ?? Environment.GetEnvironmentVariable("UPDATE_ZIP");
                var r = local != null ? new Release { Tag = "v99.0", Version = new Version(99, 0), Url = Path.GetFullPath(local) } : CheckAsync().GetAwaiter().GetResult();
                if (r == null) { File.WriteAllText(result, "UPTODATE current=" + Show(Current) + "\r\n"); return 0; }
                if (!CanWriteAppDir()) { File.WriteAllText(result, "FAIL app folder not writable: " + AppDir + "\r\n"); return 1; }
                var v = StageAsync(r, _ => { }).GetAwaiter().GetResult();
                File.WriteAllText(result, $"STAGED tag={r.Tag} version={Show(v)} current={Show(Current)} from={r.Url} staged={StagedVersion != null}\r\n");
                if (!ApplyStaged(Environment.GetEnvironmentVariable("UPDATE_NORELAUNCH") == null)) throw new Exception("apply failed");
                File.AppendAllText(result, $"REPLACED OK updatedTo={Settings.GetString("updatedTo", "")} stageLeft={Directory.Exists(StageDir)}\r\n");
                return 0;
            }
            catch (Exception e)
            {
                File.AppendAllText(result, "FAIL " + e + "\r\n");
                return 1;
            }
        }
    }
}
