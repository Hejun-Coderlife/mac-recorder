// AI 控制的自测（CI 上跑；不动鼠标键盘）：CTLTEST=素材夹（有 raw.mp4 / track.json）
//   开主窗口（控制口起来）→ 起一个「录屏.exe --mcp」子进程，按 MCP 发 JSON-RPC：initialize、tools/list、
//   get_status、list_recordings（带上素材夹那份）、open_project、get_project、update_project（加箭头 + 渐变背景，剪辑窗口里能撤销）、
//   render_frame（PNG）、export_video（720p，返回文件和导出前检查的提醒）、start_recording 2 秒（普通录制，返回文件）、坏口令被拒；
//   「录屏.exe ctl get_status」命令行；「AI 控制」接上 JSON / TOML 两种配置（先备份、只加 recorder 一条）。
//   控制口信息写到临时文件（RECORDER_CONTROL_FILE），不碰用户正开着的录屏；结果写 %TEMP%\ctl_test.txt
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class SelfTestControl
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);

        public static int Run()
        {
            var sb = new StringBuilder(); bool ok = true;
            void Check(string n, bool c, string info = "") { lock (sb) { sb.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; File.WriteAllText(Tmp("ctl_test.txt"), sb.ToString()); } }
            var work = Path.Combine(Path.GetTempPath(), "ctl-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(work);
            Environment.SetEnvironmentVariable("RECORDER_CONTROL_FILE", Path.Combine(work, "control.json"));
            // 素材放进「Studio 素材」文件夹，list_recordings 才列得到
            var dir = Path.Combine(StudioSession.Folder, "ctl-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(Env("CTLTEST"))) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            try { File.Delete(StudioProject.PathIn(dir)); } catch { }
            MainForm form = null; Exception fatal = null; bool done = false;
            try
            {
                StudioEditor.TestMode = false;
                form = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(20, 20) };
                form.Show();
                var until0 = DateTime.Now.AddSeconds(10);
                while (!File.Exists(Path.Combine(work, "control.json")) && DateTime.Now < until0) { Application.DoEvents(); Thread.Sleep(20); }
                Check("control port up (control.json with port + token + pid)", File.Exists(Path.Combine(work, "control.json")), "");
                var th = new Thread(() => { try { Steps(dir, work, Check); } catch (Exception e) { fatal = e; } finally { done = true; } }) { IsBackground = true };
                th.Start();
                var until = DateTime.Now.AddMinutes(8);
                while (!done && DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); }
                if (!done) Check("finished in time", false, "");
                if (fatal != null) Check("no exception", false, fatal.ToString());
                // CTLSHOT=图片路径：「AI 控制」窗口截图
                if (Env("CTLSHOT") is string shot)
                {
                    AIConnectForm.ShowFor(form);
                    for (int i = 0; i < 60; i++) { Application.DoEvents(); Thread.Sleep(10); }
                    var af = Application.OpenForms.OfType<AIConnectForm>().FirstOrDefault();
                    if (af != null) using (var bmp = new Bitmap(af.Width, af.Height)) { af.DrawToBitmap(bmp, new Rectangle(0, 0, af.Width, af.Height)); bmp.Save(shot); af.Close(); }
                }
            }
            catch (Exception e) { Check("no exception", false, e.ToString()); }
            finally
            {
                foreach (var ed in Application.OpenForms.OfType<StudioEditor>().ToList()) try { ed.Close(); } catch { }
                try { form?.Close(); } catch { }
                for (int i = 0; i < 50; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Check("control.json removed on quit", !File.Exists(Path.Combine(work, "control.json")), "");
                try { Directory.Delete(dir, true); } catch { }
                try { Directory.Delete(work, true); } catch { }
                // 导出到桌面的测试视频（刚导完可能还被缩略图占着，多试几次）
                foreach (var f in Directory.GetFiles(App.Desktop, Path.GetFileName(dir) + "*.mp4"))
                    for (int i = 0; i < 20 && File.Exists(f); i++) { try { File.Delete(f); } catch { Thread.Sleep(250); } }
            }
            File.WriteAllText(Tmp("ctl_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        // 一个 MCP 客户端：起子进程，一问一答
        sealed class Mcp : IDisposable
        {
            readonly Process p; int id;
            public Mcp()
            {
                p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--mcp")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                });
                p.StandardInput.AutoFlush = true;
            }
            public Dictionary<string, object> Ask(string method, Dictionary<string, object> prm = null)
            {
                var msg = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = (double)(++id), ["method"] = method, ["params"] = prm ?? new Dictionary<string, object>() };
                var bytes = new UTF8Encoding(false).GetBytes(JsonOut.Write(msg) + "\n");
                p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.BaseStream.Flush();
                var line = p.StandardOutput.ReadLine();
                return line == null ? null : (Dictionary<string, object>)Json.Parse(line);
            }
            // tools/call → 工具结果（文字那段解出来的 JSON；isError 时是 {error}）+ 有没有图
            public (Dictionary<string, object> r, bool image, bool isError) Call(string name, Dictionary<string, object> args = null)
            {
                var resp = Ask("tools/call", new Dictionary<string, object> { ["name"] = name, ["arguments"] = args ?? new Dictionary<string, object>() });
                var res = (Dictionary<string, object>)resp["result"];
                var content = ((List<object>)res["content"]).Cast<Dictionary<string, object>>().ToList();
                bool isErr = res.TryGetValue("isError", out var e) && e is bool b && b;
                var text = content.LastOrDefault(c => (string)c["type"] == "text")?["text"] as string ?? "";
                var r = isErr ? new Dictionary<string, object> { ["error"] = text } : (Dictionary<string, object>)Json.Parse(text);
                return (r, content.Any(c => (string)c["type"] == "image" && ((string)c["data"]).Length > 1000), isErr);
            }
            public void Dispose() { try { p.StandardInput.Close(); if (!p.WaitForExit(5000)) p.Kill(); } catch { } p.Dispose(); }
        }

        static void Steps(string dir, string work, Action<string, bool, string> Check)
        {
            using (var m = new Mcp())
            {
                var init = m.Ask("initialize", new Dictionary<string, object> { ["protocolVersion"] = "2025-06-18" });
                var ir = init?["result"] as Dictionary<string, object>;
                Check("MCP initialize: server name + instructions", ir != null && ((Dictionary<string, object>)ir["serverInfo"])["name"] as string == "recorder" && ((string)ir["instructions"]).Contains("Windows"), "");
                var tools = ((List<object>)((Dictionary<string, object>)m.Ask("tools/list")["result"])["tools"]).Cast<Dictionary<string, object>>().Select(t => (string)t["name"]).ToList();
                Check("tools/list: the 10 Mac tools", new[] { "get_status", "list_recordings", "start_recording", "stop_recording", "open_project", "get_project", "update_project", "render_frame", "speak_captions", "export_video" }.All(tools.Contains), string.Join(",", tools));
                var unknown = m.Ask("bogus/method");
                Check("unknown method → JSON-RPC error", unknown?.ContainsKey("error") == true, "");

                var (st, _, _) = m.Call("get_status");
                Check("get_status", st.TryGetValue("recording", out var rec) && rec is bool rb && !rb && (st["recordings_folder"] as string) == StudioSession.Folder, JsonOut.Write(st));
                var (ls, _, _) = m.Call("list_recordings");
                var mine = ((List<object>)ls["recordings"]).Cast<Dictionary<string, object>>().FirstOrDefault(x => string.Equals(x["path"] as string, dir, StringComparison.OrdinalIgnoreCase));
                Check("list_recordings includes the test footage with its duration", mine != null && (double)mine["duration"] > 1, mine == null ? JsonOut.Write(ls).Substring(0, Math.Min(300, JsonOut.Write(ls).Length)) : JsonOut.Write(mine));

                var P = new Dictionary<string, object> { ["project"] = dir };
                var (op, _, _) = m.Call("open_project", P);
                Check("open_project opens the editor", op.ContainsKey("ok") && Application.OpenForms.OfType<StudioEditor>().Any(), JsonOut.Write(op));
                var (gp, _, _) = m.Call("get_project", P);
                var info = gp.TryGetValue("info", out var iv) ? iv as Dictionary<string, object> : null;
                Check("get_project: project + info (duration, size, clicks, notes)", gp.ContainsKey("project") && info != null && (double)info["duration"] > 1 && (double)info["width"] > 0 && info.ContainsKey("clicks") && ((string)info["notes"]).Contains("annotations"), "");

                var patch = (Dictionary<string, object>)Json.Parse("{\"look\":{\"background\":\"gradient\",\"padding\":0.08},\"annotations\":[{\"start\":0.5,\"end\":3,\"kind\":\"arrow\",\"x\":0.3,\"y\":0.7,\"w\":0.1,\"h\":-0.15,\"arrow\":\"curved\"},{\"start\":0.5,\"end\":3,\"kind\":\"text\",\"x\":0.5,\"y\":0.4,\"text\":\"hello\",\"textStyle\":\"glass\"}]}");
                var (up, _, upErr) = m.Call("update_project", new Dictionary<string, object> { ["project"] = dir, ["patch"] = patch });
                var ed = Application.OpenForms.OfType<StudioEditor>().FirstOrDefault();
                StudioProject live = null; ed?.Invoke((Action)(() => live = ed.Project.Clone()));
                Check("update_project: merged into the open editor (defaults + ids filled in)", !upErr && live != null && live.Look.Background == "gradient" && Math.Abs(live.Look.Padding - 0.08) < 1e-9 && live.Annotations.Count == 2
                      && live.Annotations[0].Arrow == "curved" && live.Annotations[0].Width == 5 && live.Annotations.All(a => !string.IsNullOrEmpty(a.Id)) && live.Annotations[1].TextStyle == "glass",
                      upErr ? JsonOut.Write(up) : $"bg={live?.Look.Background} anns={live?.Annotations.Count}");
                var (bad, _, badErr) = m.Call("update_project", new Dictionary<string, object> { ["project"] = dir });
                Check("update_project without patch → error", badErr, JsonOut.Write(bad));

                var (fr, img, _) = m.Call("render_frame", new Dictionary<string, object> { ["project"] = dir, ["time"] = 1.0, ["max_width"] = 640.0 });
                Check("render_frame returns a PNG image", img && fr.ContainsKey("time"), JsonOut.Write(fr));

                var (ex, _, exErr) = m.Call("export_video", new Dictionary<string, object> { ["project"] = dir, ["resolution"] = "720p", ["fps"] = 30.0 });
                var file = ex.TryGetValue("file", out var fv) ? fv as string : null;
                Check("export_video: file + pre-export warnings (placeholder 'hello')", !exErr && file != null && File.Exists(file) && new FileInfo(file).Length > 10000
                      && ((List<object>)ex["warnings"]).OfType<string>().Any(w => w.Contains("hello")), JsonOut.Write(ex));
                if (file != null) try { File.Delete(file); } catch { }

                // 撤销：剪辑窗口里 Ctrl+Z 回到 AI 改之前
                ed?.Invoke((Action)(() => ed.Undo()));
                ed?.Invoke((Action)(() => live = ed.Project.Clone()));
                Check("undo in the editor reverts the AI edit", live != null && live.Annotations.Count == 0 && live.Look.Background != "gradient", "");

                // 普通录制 2 秒：返回文件（CTL_NOREC=1 跳过：在别人正用着的电脑上跑时别录屏）
                if (Env("CTL_NOREC") != "1")
                {
                    var (sr, _, srErr) = m.Call("start_recording", new Dictionary<string, object> { ["seconds"] = 2.0, ["studio"] = false, ["microphone"] = false, ["system_audio"] = false });
                    var recFile = sr.TryGetValue("file", out var rf) ? rf as string : null;
                    Check("start_recording 2 s (plain) → file", !srErr && recFile != null && File.Exists(recFile), JsonOut.Write(sr));
                    if (recFile != null) try { File.Delete(recFile); } catch { }
                }
                var (nr, _, nrErr) = m.Call("stop_recording");
                Check("stop_recording when not recording → error", nrErr, JsonOut.Write(nr));
                var (w, _, wErr) = m.Call("start_recording", new Dictionary<string, object> { ["source"] = "no-such-window-xyz" });
                Check("unknown window → error listing what's available", wErr && ((string)w["error"]).Contains("no-such-window-xyz"), JsonOut.Write(w));
            }

            // 坏口令被拒
            var ci = (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(work, "control.json")));
            using (var c = new TcpClient())
            {
                c.Connect("127.0.0.1", (int)(double)ci["port"]);
                var body = Encoding.UTF8.GetBytes("{\"name\":\"get_status\",\"arguments\":{}}");
                var head = Encoding.ASCII.GetBytes($"POST /call HTTP/1.1\r\nX-Token: wrong\r\nContent-Length: {body.Length}\r\n\r\n");
                var s = c.GetStream(); s.Write(head, 0, head.Length); s.Write(body, 0, body.Length);
                var resp = new StreamReader(s).ReadToEnd();
                Check("wrong token is refused", resp.Contains("bad token"), "");
            }

            // 命令行：录屏.exe ctl get_status
            var psi = new ProcessStartInfo(Application.ExecutablePath, "ctl get_status") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(30000);
                Check("ctl get_status prints JSON", p.ExitCode == 0 && o.Contains("\"recording\":false"), o.Trim());
            }

            // 「AI 控制」接上：JSON（已有别的服务 → 先备份、只加 recorder）、TOML
            var jf = Path.Combine(work, "cfg", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jf));
            File.WriteAllText(jf, "{\"mcpServers\":{\"other\":{\"command\":\"x\"}},\"keep\":1}");
            var jc = new AIClient { Id = "t", Name = "t", Note = "", Kind = AIClient.Kinds.Json, Path = jf, Key = "mcpServers" };
            jc.Connect();
            var jj = (Dictionary<string, object>)Json.Parse(File.ReadAllText(jf));
            var srv = (Dictionary<string, object>)jj["mcpServers"];
            Check("AI Control → JSON config: backup kept, other server kept, recorder added", File.Exists(jf + ".录屏备份") && srv.ContainsKey("other") && jj.ContainsKey("keep") && jc.Connected
                  && ((Dictionary<string, object>)srv["recorder"])["command"] as string == Application.ExecutablePath, File.ReadAllText(jf));
            var tf = Path.Combine(work, "cfg", "config.toml");
            File.WriteAllText(tf, "model = \"x\"");
            var tc = new AIClient { Id = "t2", Name = "t2", Note = "", Kind = AIClient.Kinds.Toml, Path = tf };
            tc.Connect(); tc.Connect();
            var tt = File.ReadAllText(tf);
            Check("AI Control → TOML config: added once, Windows path kept literally", tc.Connected && tt.Split(new[] { "[mcp_servers.recorder]" }, StringSplitOptions.None).Length == 2 && tt.Contains("command = '" + Application.ExecutablePath + "'"), tt);
            File.WriteAllText(jf, "{ // comment\n}");
            var jc2 = new AIClient { Id = "t3", Name = "t3", Note = "", Kind = AIClient.Kinds.Json, Path = jf, Key = "mcpServers" };
            string err = null; try { jc2.Connect(); } catch (Exception e) { err = e.Message; }
            Check("JSON with comments is left alone", err != null && File.ReadAllText(jf).Contains("comment"), err ?? "");
        }
    }
}
