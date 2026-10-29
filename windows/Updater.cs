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
    // 自动更新：查 GitHub 最新 Release（tag 形如 v1.2），比自己新就提示；点「更新」才下载 zip、解压，
    // 交给一个隐藏的 PowerShell 脚本：等本程序退出 → 覆盖程序文件夹 → 重新打开
    static class Updater
    {
        public const string Repo = "Hejun-Coderlife/mac-recorder";
        public const string AssetName = "ScreenRecorder-Windows-x64.zip";
        public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";
        static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        public sealed class Release
        {
            public string Tag;      // v1.2
            public Version Version;
            public string ZipUrl;
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
            var url = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>()
                .Select(m => m.Groups[1].Value).FirstOrDefault(u => u.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase));
            if (tag == "" || url == null) return null;
            var v = Parse(tag);
            return Compare(v, Current) > 0 ? new Release { Tag = tag, Version = v, ZipUrl = url } : null;
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

        // 下载 + 解压到临时文件夹，返回解出来的文件夹（里面一定有 录屏.exe）
        public static async Task<string> DownloadAsync(Release r, Action<int> progress)
        {
            var work = Path.Combine(Path.GetTempPath(), "录屏-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, AssetName);
            using (var c = Client())
            {
                c.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                await c.DownloadFileTaskAsync(new Uri(r.ZipUrl), zip);
            }
            var dir = Path.Combine(work, "new");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir));
            if (!File.Exists(Path.Combine(dir, "录屏.exe"))) throw new Exception(L("下载的安装包里没有 录屏.exe", "录屏.exe is missing from the downloaded package"));
            return dir;
        }

        // 交给隐藏的 PowerShell：等本进程退出 → 覆盖复制（失败重试）→ 重新打开 → 删临时文件。调用方随后退出程序
        public static void LaunchInstaller(string newDir, bool relaunch, string resultFile = null)
        {
            var work = Path.GetDirectoryName(newDir);
            var script = Path.Combine(work, "install.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));   // 带 BOM，Windows PowerShell 5.1 才认中文路径
            var exe = System.Windows.Forms.Application.ExecutablePath;
            string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
            var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File {Q(script)} -ProcId {Process.GetCurrentProcess().Id} " +
                       $"-Src {Q(newDir)} -Dst {Q(AppDir)} -Exe {Q(exe)} -Work {Q(work)} -Relaunch {(relaunch ? 1 : 0)} -Result {Q(resultFile ?? "")}";
            Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        const string Script = @"param([int]$ProcId, [string]$Src, [string]$Dst, [string]$Exe, [string]$Work, [int]$Relaunch, [string]$Result)
function Log($s) { if ($Result) { Add-Content -LiteralPath $Result -Value $s -Encoding UTF8 } }
try { Wait-Process -Id $ProcId -Timeout 60 -ErrorAction Stop } catch { }
Start-Sleep -Milliseconds 300
$ok = $false; $err = ''
for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
  try {
    Get-ChildItem -LiteralPath $Src | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Dst -Recurse -Force -ErrorAction Stop }
    # 程序被改过名字的话，也把新的 录屏.exe 盖到原来的名字上，重新打开的还是新版
    $name = Split-Path -Leaf $Exe
    if ($name -ne '录屏.exe') {
      Copy-Item -LiteralPath (Join-Path $Src '录屏.exe') -Destination $Exe -Force -ErrorAction Stop
      $cfg = Join-Path $Src '录屏.exe.config'
      if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination ($Exe + '.config') -Force -ErrorAction Stop }
    }
    $ok = $true
  } catch { $err = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
}
if ($ok) { Log 'COPIED OK' } else { Log ('COPY FAIL ' + $err) }
if ($Relaunch -eq 1) { Start-Process -FilePath $Exe -WorkingDirectory $Dst }
Start-Sleep -Seconds 1
Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
";

        // 自测（不弹窗）：UPDATE_TEST=1 [UPDATE_FAKE_VERSION=1.0] [UPDATE_NORELAUNCH=1] [UPDATE_TOKEN=令牌]
        // 查最新版 → 下载 → 解压 → 交给安装脚本覆盖当前文件夹后退出；结果写 %TEMP%\update_test.txt
        //（本程序写 STAGED/UPTODATE/FAIL 一行，安装脚本做完再追加 COPIED OK 或 COPY FAIL）
        public static int SelfTest()
        {
            var result = Path.Combine(Path.GetTempPath(), "update_test.txt");
            try { File.Delete(result); } catch { }
            try
            {
                var r = CheckAsync().GetAwaiter().GetResult();
                if (r == null) { File.WriteAllText(result, "UPTODATE current=" + Show(Current) + "\r\n"); return 0; }
                if (!CanWriteAppDir()) { File.WriteAllText(result, "FAIL app folder not writable: " + AppDir + "\r\n"); return 1; }
                var dir = DownloadAsync(r, _ => { }).GetAwaiter().GetResult();
                File.WriteAllText(result, $"STAGED tag={r.Tag} current={Show(Current)} dir={dir}\r\n");
                LaunchInstaller(dir, Environment.GetEnvironmentVariable("UPDATE_NORELAUNCH") == null, result);
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(result, "FAIL " + e + "\r\n");
                return 1;
            }
        }
    }
}
