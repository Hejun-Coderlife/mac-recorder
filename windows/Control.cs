// 让 AI 直接操作录屏（MCP，照 Mac 版 Control.swift）：
// - 录屏.exe 开着时在本机 127.0.0.1 开一个控制口（随机端口 + 随机口令，写在 %APPDATA%\录屏\control.json，别的电脑连不上）
// - 「录屏.exe --mcp」是给 Claude 这类 AI 用的 MCP 服务（标准输入输出 JSON-RPC）：把工具调用转给正在运行的录屏；没开就先打开它
// - 「录屏.exe ctl 工具名 '{json}'」：给能跑命令行、不支持 MCP 的 AI 用，结果打印成 JSON
// - 改工程走剪辑窗口（开着就直接改窗口里的工程，预览马上变、能撤销），所以 AI 改什么用户都看得见
// 接到 Claude Code：claude mcp add recorder -- "C:\…\录屏.exe" --mcp（「AI 控制」窗口里一键接）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    static class ControlInfo
    {
        // RECORDER_CONTROL_FILE：自测用另一份，别动用户正开着的录屏
        public static string File => Environment.GetEnvironmentVariable("RECORDER_CONTROL_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "录屏", "control.json");
    }

    // 写 JSON（对象、数组、字符串、数字、真假、null）
    static class JsonOut
    {
        public static string Write(object o) { var b = new StringBuilder(); W(b, o); return b.ToString(); }
        static void W(StringBuilder b, object o)
        {
            switch (o)
            {
                case null: b.Append("null"); break;
                case string s: Str(b, s); break;
                case bool v: b.Append(v ? "true" : "false"); break;
                case double d: b.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: b.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
                case int _: case long _: case short _: case byte _: case decimal _: b.Append(((IFormattable)o).ToString(null, CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> d:
                {
                    b.Append('{'); bool first = true;
                    foreach (var kv in d) { if (!first) b.Append(','); first = false; Str(b, kv.Key); b.Append(':'); W(b, kv.Value); }
                    b.Append('}'); break;
                }
                case IEnumerable e:
                {
                    b.Append('['); bool first = true;
                    foreach (var x in e) { if (!first) b.Append(','); first = false; W(b, x); }
                    b.Append(']'); break;
                }
                default: Str(b, o.ToString()); break;
            }
        }
        static void Str(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (var c in s)
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                }
            b.Append('"');
        }
    }

    // ================= 程序里：控制口 =================
    static class ControlServer
    {
        static TcpListener listener;
        static readonly string token = Guid.NewGuid().ToString();
        public static MainForm Host;

        public static void Start(MainForm host)
        {
            Host = host;
            if (listener != null) return;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Directory.CreateDirectory(Path.GetDirectoryName(ControlInfo.File));
                System.IO.File.WriteAllText(ControlInfo.File, JsonOut.Write(new Dictionary<string, object> { ["port"] = port, ["token"] = token, ["pid"] = Process.GetCurrentProcess().Id }));
                var th = new Thread(Accept) { IsBackground = true, Name = "control" };
                th.Start();
            }
            catch { listener = null; }
        }
        public static void Stop()
        {
            try { listener?.Stop(); } catch { }
            listener = null;
            // 只删自己写的那份（同时开着两个录屏时别删了另一个的）
            try { if (System.IO.File.Exists(ControlInfo.File) && System.IO.File.ReadAllText(ControlInfo.File).Contains(token)) System.IO.File.Delete(ControlInfo.File); } catch { }
        }

        static void Accept()
        {
            while (listener != null)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        // 很小的 HTTP：POST /call，头里带 X-Token，正文是 {"name": 工具名, "arguments": {...}}
        static void Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    var s = c.GetStream();
                    var buf = new MemoryStream(); var tmp = new byte[65536];
                    int headEnd = -1, len = 0; string head = "";
                    while (true)
                    {
                        int n = s.Read(tmp, 0, tmp.Length);
                        if (n <= 0) return;
                        buf.Write(tmp, 0, n);
                        var all = buf.GetBuffer();
                        if (headEnd < 0)
                        {
                            for (int i = 3; i < buf.Length; i++) if (all[i - 3] == '\r' && all[i - 2] == '\n' && all[i - 1] == '\r' && all[i] == '\n') { headEnd = i + 1; break; }
                            if (headEnd >= 0)
                            {
                                head = Encoding.UTF8.GetString(all, 0, headEnd);
                                var cl = head.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault(l => l.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase));
                                if (cl != null) int.TryParse(cl.Substring(15).Trim(), out len);
                            }
                        }
                        if (headEnd >= 0 && buf.Length - headEnd >= len) break;
                        if (buf.Length > 64 << 20) return;
                    }
                    bool ok = head.Split(new[] { "\r\n" }, StringSplitOptions.None).Any(l => l.Equals("x-token: " + token, StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, object> outD;
                    if (!ok) outD = new Dictionary<string, object> { ["error"] = "bad token" };
                    else
                    {
                        var body = Encoding.UTF8.GetString(buf.GetBuffer(), headEnd, len);
                        if (Json.Parse(body) is Dictionary<string, object> j && j.TryGetValue("name", out var nm) && nm is string name)
                            outD = ControlTools.Call(name, j.TryGetValue("arguments", out var a) && a is Dictionary<string, object> ad ? ad : new Dictionary<string, object>());
                        else outD = new Dictionary<string, object> { ["error"] = "bad request" };
                    }
                    var d = Encoding.UTF8.GetBytes(JsonOut.Write(outD));
                    var h = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {d.Length}\r\nConnection: close\r\n\r\n");
                    s.Write(h, 0, h.Length); s.Write(d, 0, d.Length); s.Flush();
                }
                catch { }
            }
        }
    }

    // ---- 各个工具（在后台线程里跑，碰界面的地方切回主线程）----
    static class ControlTools
    {
        static MainForm Host => ControlServer.Host ?? throw Fail(L("录屏窗口还没准备好", "The recorder window isn't ready"));
        static T Ui<T>(Func<T> f) { var h = Host; return h.InvokeRequired ? (T)h.Invoke(f) : f(); }
        static void Ui(Action f) => Ui(() => { f(); return 0; });
        static Exception Fail(string s) => new Exception(s);
        static Dictionary<string, object> D(params (string k, object v)[] kv) { var d = new Dictionary<string, object>(); foreach (var (k, v) in kv) d[k] = v; return d; }

        public static Dictionary<string, object> Call(string name, Dictionary<string, object> a)
        {
            try
            {
                switch (name)
                {
                    case "get_status": return Status();
                    case "list_recordings": return List();
                    case "start_recording": return StartRec(a);
                    case "stop_recording": return StopRec();
                    case "open_project": { var e = Editor(a); return D(("ok", true), ("project", Ui(() => e.Dir))); }
                    case "get_project": return GetProject(a);
                    case "update_project": return UpdateProject(a);
                    case "render_frame": return RenderFrame(a);
                    case "export_video": return Export(a);
                    case "speak_captions": return Speak(a);
                    default: return D(("error", "unknown tool " + name));
                }
            }
            catch (Exception e) { return D(("error", e.Message)); }
        }

        static IEnumerable<StudioEditor> Editors => Application.OpenForms.OfType<StudioEditor>().Where(e => !e.IsDisposed).ToList();

        static Dictionary<string, object> Status() => Ui(() =>
        {
            var h = Host;
            return D(("recording", h.CtlRecording), ("elapsed", h.CtlElapsed), ("studio_mode", h.CtlStudio),
                     ("recordings_folder", StudioSession.Folder),
                     ("open_projects", Editors.Select(e => (object)e.Dir).ToList()),
                     ("busy", h.CtlBusy || Editors.Any(e => e.Exporting || e.RecordingVoice || e.Speaking != null || e.CaptionBusy)));
        });

        static Dictionary<string, object> List()
        {
            var outL = new List<(DateTime m, Dictionary<string, object> d)>();
            foreach (var d in Directory.Exists(StudioSession.Folder) ? Directory.GetDirectories(StudioSession.Folder) : new string[0])
            {
                if (!System.IO.File.Exists(Path.Combine(d, "track.json")) || !System.IO.File.Exists(Path.Combine(d, "raw.mp4"))) continue;
                var m = Directory.GetLastWriteTime(d);
                double dur = 0; try { dur = Studio.Probe(Path.Combine(d, "raw.mp4")).Duration; } catch { }
                outL.Add((m, D(("name", Path.GetFileName(d)), ("path", d), ("modified", m.ToString("yyyy-MM-ddTHH:mm:ss")), ("duration", Math.Round(dur, 2)))));
            }
            return D(("recordings", outL.OrderByDescending(x => x.m).Take(100).Select(x => (object)x.d).ToList()));
        }

        // 开录：source = "screen"（默认，第一个屏幕）/ "screen 2" / 窗口名的一部分（如 "Chrome"）；seconds 给了就录这么久自动停，并返回结果
        static Dictionary<string, object> StartRec(Dictionary<string, object> a)
        {
            var src = ((a.TryGetValue("source", out var sv) ? sv as string : null) ?? "screen").Trim();
            string picked = Ui(() =>
            {
                var h = Host;
                if (h.CtlRecording) throw Fail(L("已经在录了", "Already recording"));
                h.CtlSet(a.TryGetValue("studio", out var st) && st is bool b1 ? b1 : (bool?)null, a.TryGetValue("microphone", out var mi) && mi is bool b2 ? b2 : (bool?)null,
                         a.TryGetValue("system_audio", out var sy) && sy is bool b3 ? b3 : (bool?)null);
                var list = h.CtlSources();
                string lower = src.ToLowerInvariant();
                var displays = list.Where(x => x.Kind == SourceKind.Display).ToList();
                Source s;
                if (lower.StartsWith("screen") || src.StartsWith("屏幕") || src.StartsWith("整个屏幕") || src == "")
                {
                    int n = int.TryParse(new string(src.Where(char.IsDigit).ToArray()), out var k) ? k : 1;
                    s = (n >= 1 && n <= displays.Count ? displays[n - 1] : displays.FirstOrDefault()) ?? throw Fail(L("没找到屏幕", "No screen found"));
                }
                else
                    s = list.FirstOrDefault(x => x.Kind == SourceKind.Window && ((x.Title ?? "").ToLowerInvariant().Contains(lower) || (x.Detail ?? "").ToLowerInvariant().Contains(lower)))
                        ?? throw Fail(L($"没找到叫「{src}」的窗口；可用的：", $"No window called “{src}”; available: ") + string.Join(L("；", "; "), list.Select(x => x.Kind == SourceKind.Window ? $"{x.Title} — {x.Detail}" : x.Title)));
                h.CtlSelect(s.Id);
                h.CtlLastProject = null;
                h.CtlToggle();
                return s.Kind == SourceKind.Window ? $"{s.Title} {s.Detail}" : s.Title;
            });
            // 等它真的开始（可能有倒计时）
            var until = DateTime.Now.AddSeconds(10);
            while (!Ui(() => Host.CtlRecording) && DateTime.Now < until) Thread.Sleep(100);
            if (!Ui(() => Host.CtlRecording)) throw Fail(Ui(() => Host.CtlMessage) is string m && m != "" ? m : L("没能开始录制", "Couldn't start recording"));
            if (a.TryGetValue("seconds", out var se) && se is double secs && secs > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(secs, 3600)));
                return StopRec();
            }
            return D(("recording", true), ("source", picked));
        }

        static Dictionary<string, object> StopRec()
        {
            Ui(() => { if (!Host.CtlRecording) throw Fail(L("现在没在录", "Not recording")); Host.CtlToggle(); });
            var until = DateTime.Now.AddSeconds(120);
            while (Ui(() => Host.CtlRecording || Host.CtlBusy) && DateTime.Now < until) Thread.Sleep(100);
            return Ui(() =>
            {
                var h = Host;
                var outD = D(("recording", false), ("message", h.CtlMessage));
                if (h.CtlLastProject != null) { outD["project"] = h.CtlLastProject; outD["note"] = "Studio footage is open in the editor; edit it with get_project / update_project, export with export_video"; }
                else if (h.CtlLastFile != null) outD["file"] = h.CtlLastFile;
                return outD;
            });
        }

        // 拿到（没开就打开）这份素材的剪辑窗口
        static StudioEditor Editor(Dictionary<string, object> a)
        {
            var p = (a.TryGetValue("project", out var v) ? v as string : null) ?? (a.TryGetValue("path", out var v2) ? v2 as string : null)
                    ?? throw Fail("missing project (the recording folder path from list_recordings)");
            var dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)).TrimEnd('\\', '/');
            if (!System.IO.File.Exists(Path.Combine(dir, "track.json"))) throw Fail(L("这个文件夹不是 Studio 素材：", "This folder isn't Studio footage: ") + dir);
            var e = Ui(() =>
            {
                var ed = Editors.FirstOrDefault(x => string.Equals(x.Dir.TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase));
                if (ed == null) { Host.CtlOpen(dir); ed = Editors.FirstOrDefault(x => string.Equals(x.Dir.TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase)); }
                return ed;
            }) ?? throw Fail(L("打不开剪辑窗口", "Couldn't open the editor"));
            var until = DateTime.Now.AddSeconds(20);
            while (!Ui(() => e.FirstFrameShown) && DateTime.Now < until) Thread.Sleep(100);
            return e;
        }

        static Dictionary<string, object> ProjectJson(StudioEditor e) => (Dictionary<string, object>)Json.Parse(Ui(() => e.Project.ToJson()));

        static Dictionary<string, object> GetProject(Dictionary<string, object> a)
        {
            var e = Editor(a);
            return Ui(() =>
            {
                var t = e.Track;
                double W = Math.Max(t.Width, 1), H = Math.Max(t.Height, 1);
                // 点击位置换成 0–1 比例（和标注、遮挡同一套坐标）
                var clicks = t.Clicks.Take(300).Select(c => (object)(c.Length >= 3 ? D(("t", c[0]), ("x", Math.Round(c[1] / W, 3)), ("y", Math.Round(c[2] / H, 3))) : D(("t", c.Length > 0 ? c[0] : 0)))).ToList();
                return D(("project", Json.Parse(e.Project.ToJson())),
                         ("info", D(("duration", e.Duration), ("width", t.Width), ("height", t.Height), ("clicks", clicks), ("key_presses", t.Keys.Take(300).Cast<object>().ToList()), ("notes", ControlDocs.ProjectNotes))));
            });
        }

        // 改工程：patch 按 JSON Merge Patch 合进去（对象逐层合并，数组整个替换），数组里的新东西缺 id / 默认字段自动补上
        static Dictionary<string, object> UpdateProject(Dictionary<string, object> a)
        {
            var e = Editor(a);
            if (!(a.TryGetValue("patch", out var pv) && pv is Dictionary<string, object> patch)) throw Fail("missing patch (fields to change, e.g. {\"look\": {\"background\": \"gradient\"}})");
            var cur = ProjectJson(e);
            var merged = (Dictionary<string, object>)Merge(cur, Fill(patch));
            StudioProject np;
            try { np = StudioProject.FromJson(JsonOut.Write(merged)); } catch (Exception ex) { throw Fail(L("改完的工程不对：", "The patched project is invalid: ") + ex.Message); }
            Ui(() => e.ReplaceProject(np));
            return D(("ok", true), ("project", ProjectJson(e)));
        }
        static object Merge(object a, object b)
        {
            if (!(a is Dictionary<string, object> ad) || !(b is Dictionary<string, object> bd)) return b;
            var r = new Dictionary<string, object>(ad);
            foreach (var kv in bd) { if (kv.Value == null) r.Remove(kv.Key); else r[kv.Key] = r.TryGetValue(kv.Key, out var old) ? Merge(old, kv.Value) : kv.Value; }
            return r;
        }
        // 补默认：AI 写数组时常常只给关键字段（同 Mac 版）
        static Dictionary<string, object> Fill(Dictionary<string, object> patch)
        {
            Dictionary<string, object> Rgb(double r, double g, double b) => D(("r", r), ("g", g), ("b", b));
            var defaults = new Dictionary<string, Dictionary<string, object>>
            {
                ["segments"] = D(("zoom", 2.0), ("mode", "follow"), ("fixedX", 0.5), ("fixedY", 0.5)),
                ["cuts"] = D(("mode", "cut"), ("rate", 4.0)),
                ["annotations"] = D(("kind", "arrow"), ("w", 0.0), ("h", 0.0), ("color", Rgb(1, 0.27, 0.23)), ("width", 5.0), ("text", ""), ("fontSize", 28.0), ("pill", true), ("drawOn", true)),
                ["redactions"] = D(("style", "blur"), ("strength", 0.6), ("color", Rgb(0.12, 0.12, 0.14)), ("corner", 8.0)),
                ["captions"] = D(), ["layouts"] = D(), ["voiceovers"] = D(),
            };
            var p = new Dictionary<string, object>(patch);
            foreach (var kv in defaults)
            {
                if (!(p.TryGetValue(kv.Key, out var arr) && arr is List<object> list)) continue;
                p[kv.Key] = list.Select(item =>
                {
                    var x = new Dictionary<string, object>(kv.Value);
                    if (item is Dictionary<string, object> it) foreach (var f in it) x[f.Key] = f.Value;
                    if (!x.ContainsKey("id")) x["id"] = Guid.NewGuid().ToString().ToUpperInvariant();
                    return (object)x;
                }).ToList();
            }
            return p;
        }

        static Dictionary<string, object> RenderFrame(Dictionary<string, object> a)
        {
            var e = Editor(a);
            var (dir, dur, now) = Ui(() => { e.SaveForControl(); return (e.Dir, e.Duration, e.Time); });
            double t = Math.Min(Math.Max(a.TryGetValue("time", out var tv) && tv is double d ? d : now, 0), dur);
            int maxW = a.TryGetValue("max_width", out var mw) && mw is double m ? (int)m : 1280;
            var png = Path.Combine(Path.GetTempPath(), "recorder-frame-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            try
            {
                StudioStill.Render(dir, t, png, "cpu", Math.Max(160, maxW), CursorTracker.LoadCursor);
                return D(("image_base64", Convert.ToBase64String(System.IO.File.ReadAllBytes(png))), ("time", t));
            }
            finally { try { System.IO.File.Delete(png); } catch { } }
        }

        static Dictionary<string, object> Speak(Dictionary<string, object> a)
        {
            var e = Editor(a);
            if (a.TryGetValue("voice", out var vv) && vv is string v && v != "")
            {
                var en = Tts.Engine; var lv = v.ToLowerInvariant();
                var m = Tts.Voices(en).FirstOrDefault(x => x.id.ToLowerInvariant() == lv || x.name.ToLowerInvariant().Contains(lv));
                if (m.id != null) Tts.SetVoice(en, m.id);
            }
            if (a.TryGetValue("rate", out var rv) && rv is double r) Tts.Rate = Math.Min(Math.Max(r, 0.5), 1.6);
            var ids = a.TryGetValue("caption_ids", out var iv) && iv is List<object> il ? il.OfType<string>().ToList() : null;
            var (before, done) = Ui(() => (e.Project.Voiceovers.Count, e.SpeakDone));
            Ui(() => e.SpeakCaptions(ids));
            if (Ui(() => e.Speaking == null && e.SpeakDone == done)) throw Fail(L("没有能念的字幕（或者正忙）", "No captions to speak (or busy)"));
            while (Ui(() => e.SpeakDone == done)) Thread.Sleep(200);
            return Ui(() =>
            {
                if (e.LastSpeakError != null) throw Fail(e.LastSpeakError);
                return D(("ok", true), ("voiceovers", e.Project.Voiceovers.Count), ("added_or_replaced", Math.Max(0, e.Project.Voiceovers.Count - before)));
            });
        }

        static Dictionary<string, object> Export(Dictionary<string, object> a)
        {
            var e = Editor(a);
            var warnings = Ui(() =>
            {
                if (e.Exporting) throw Fail(L("这份素材正在导出", "This recording is already exporting"));
                var o = e.ExportOpt.Clone();
                if (a.TryGetValue("resolution", out var rv) && rv is string r) { r = r.ToLowerInvariant().TrimEnd('p'); if (ExportOptions.Resolutions.Contains(r)) o.Resolution = r; }
                if (a.TryGetValue("fps", out var fv) && fv is double f) o.Fps = f == 30 ? 30 : 60;
                if (a.TryGetValue("quality", out var qv) && qv is string q && ExportOptions.Qualities.Contains(q)) o.Quality = q;
                if (a.TryGetValue("format", out var mv) && mv is string fm) o.Gif = fm == "gif";
                if (a.TryGetValue("codec", out var cv) && cv is string co) o.Hevc = co == "hevc";
                e.ExportOpt = o;
                var w = e.ExportIssues();   // AI 导出不弹窗，把检查结果一起返回
                e.Export();
                return w;
            });
            Thread.Sleep(200);
            while (Ui(() => e.Exporting)) Thread.Sleep(200);
            return Ui(() =>
            {
                if (e.LastExportError != null) throw Fail(e.LastExportError);
                if (e.ExportedFile == null) throw Fail(L("导出被取消了", "The export was cancelled"));
                return D(("ok", true), ("file", e.ExportedFile), ("warnings", warnings.Cast<object>().ToList()));
            });
        }
    }

    // 剪辑窗口给 AI 控制用的几个口子
    sealed partial class StudioEditor
    {
        // 整份换掉（一步撤销），同 Mac 版 edit { $0 = p }
        public void ReplaceProject(StudioProject np)
        {
            if (np.Same(Project)) return;
            if (!np.SameSegments(Project)) np.SegmentsEdited = true;
            undo.Add(Project.ToJson()); redo.Clear();
            SetProject(np);
            Changed?.Invoke();
        }
        public void SaveForControl() { if (!TestMode) { saveTimer.Stop(); SaveNow(); } }
        public string ExportedFile => exported;
    }

    // ================= MCP 服务（录屏.exe --mcp）=================
    static class McpBridge
    {
        public static int Run()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                Dictionary<string, object> msg;
                try { msg = Json.Parse(line) as Dictionary<string, object>; } catch { continue; }
                if (msg == null || !msg.TryGetValue("id", out var id) || id == null) continue;   // 通知不用回
                var method = msg.TryGetValue("method", out var mv) ? mv as string ?? "" : "";
                var prm = msg.TryGetValue("params", out var pv) ? pv as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                object result = new Dictionary<string, object>(); Dictionary<string, object> error = null;
                switch (method)
                {
                    case "initialize":
                        result = new Dictionary<string, object>
                        {
                            ["protocolVersion"] = prm.TryGetValue("protocolVersion", out var pvv) && pvv is string pvs ? pvs : "2025-06-18",
                            ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                            ["serverInfo"] = new Dictionary<string, object> { ["name"] = "recorder", ["version"] = typeof(McpBridge).Assembly.GetName().Version.ToString() },
                            ["instructions"] = ControlDocs.Instructions,
                        };
                        break;
                    case "ping": break;
                    case "tools/list": result = new Dictionary<string, object> { ["tools"] = ControlDocs.Tools }; break;
                    case "tools/call":
                    {
                        var name = prm.TryGetValue("name", out var nv) ? nv as string ?? "" : "";
                        var args = prm.TryGetValue("arguments", out var av) ? av as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                        var r = Forward(name, args);
                        List<object> Text(object o) => new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = o is string s ? s : JsonOut.Write(o) } };
                        if (r.TryGetValue("error", out var err) && err is string es)
                            result = new Dictionary<string, object> { ["content"] = Text(es), ["isError"] = true };
                        else if (r.TryGetValue("image_base64", out var img) && img is string b64)
                        {
                            var rest = new Dictionary<string, object>(r); rest.Remove("image_base64");
                            var content = new List<object> { new Dictionary<string, object> { ["type"] = "image", ["data"] = b64, ["mimeType"] = "image/png" } };
                            content.AddRange(Text(rest));
                            result = new Dictionary<string, object> { ["content"] = content };
                        }
                        else result = new Dictionary<string, object> { ["content"] = Text(r) };
                        break;
                    }
                    default: error = new Dictionary<string, object> { ["code"] = -32601, ["message"] = "Method not found: " + method }; break;
                }
                var resp = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id };
                if (error != null) resp["error"] = error; else resp["result"] = result;
                stdout.WriteLine(JsonOut.Write(resp));
            }
            return 0;
        }

        // 转给正在运行的录屏；连不上就打开它再试（最多 30 秒，第一次打开要解运行库）
        public static Dictionary<string, object> Forward(string name, Dictionary<string, object> args)
        {
            bool launched = false;
            var deadline = DateTime.Now.AddSeconds(30);
            while (true)
            {
                var (state, r) = Post(name, args);
                if (state == 1) return r;
                if (state == 2) return r;   // 已经发出去了（程序可能做了一半）：别再发一次
                if (!launched)
                {
                    launched = true;
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) }); } catch { }
                }
                if (DateTime.Now > deadline) return new Dictionary<string, object> { ["error"] = L("连不上录屏（打开「录屏」再试）", "Couldn't reach the recorder (open it and try again)") };
                Thread.Sleep(500);
            }
        }

        // 0 = 程序没开，1 = 拿到结果，2 = 发出去了但没回好
        static (int, Dictionary<string, object>) Post(string name, Dictionary<string, object> args)
        {
            Dictionary<string, object> info;
            try { info = Json.Parse(System.IO.File.ReadAllText(ControlInfo.File)) as Dictionary<string, object>; } catch { return (0, null); }
            if (info == null || !(info.TryGetValue("port", out var pv) && pv is double port) || !(info.TryGetValue("token", out var tv) && tv is string token)) return (0, null);
            if (info.TryGetValue("pid", out var idv) && idv is double pid) { try { if (Process.GetProcessById((int)pid).HasExited) return (0, null); } catch { return (0, null); } }
            TcpClient c;
            try { c = new TcpClient(); c.Connect(IPAddress.Loopback, (int)port); } catch { return (0, null); }
            using (c)
            {
                try
                {
                    var body = Encoding.UTF8.GetBytes(JsonOut.Write(new Dictionary<string, object> { ["name"] = name, ["arguments"] = args }));
                    var s = c.GetStream(); s.ReadTimeout = 3700 * 1000;
                    var head = Encoding.ASCII.GetBytes($"POST /call HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Token: {token}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length); s.Write(body, 0, body.Length); s.Flush();
                    var ms = new MemoryStream(); s.CopyTo(ms);
                    var all = Encoding.UTF8.GetString(ms.ToArray());
                    int i = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (i >= 0 && Json.Parse(all.Substring(i + 4)) is Dictionary<string, object> j) return (1, j);
                    return (2, new Dictionary<string, object> { ["error"] = L("录屏没有回应", "The recorder didn't answer") });
                }
                catch (Exception e) { return (2, new Dictionary<string, object> { ["error"] = e.Message }); }
            }
        }
    }

    // 命令行：录屏.exe ctl 工具名 ['{"参数": …}']，结果打印成 JSON（render_frame 的图存成临时 PNG，打印路径）
    static class ControlCli
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        public static int Run(string[] args)
        {
            AttachConsole(-1);   // 程序是窗口程序：在命令行里跑时接上那个命令行窗口，才印得出来
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            if (args.Length == 0)
            {
                o.WriteLine("用法 / usage: 录屏.exe ctl <tool> ['{json args}']\ntools: " + string.Join(", ", ControlDocs.Tools.Select(t => ((Dictionary<string, object>)t)["name"])));
                return 2;
            }
            var a = new Dictionary<string, object>();
            if (args.Length > 1)
            {
                try { a = (Dictionary<string, object>)Json.Parse(args[1]); } catch { o.WriteLine("参数要是 JSON 对象 / arguments must be a JSON object"); return 2; }
            }
            var r = McpBridge.Forward(args[0], a);
            if (r.TryGetValue("image_base64", out var img) && img is string b64)
            {
                var f = Path.Combine(Path.GetTempPath(), "recorder-frame-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                System.IO.File.WriteAllBytes(f, Convert.FromBase64String(b64));
                r.Remove("image_base64"); r["image"] = f;
            }
            o.WriteLine(JsonOut.Write(r));
            return r.ContainsKey("error") ? 1 : 0;
        }
    }

    // ================= 给 AI 看的说明（同 Mac 版，平台相关的几句换成 Windows）=================
    static class ControlDocs
    {
        public const string Instructions =
            "Controls the 录屏 (Screen Recorder) Windows app: record the screen, then edit the recording like Screen Studio (auto zoom, cuts, speed-ups, " +
            "captions, arrows/boxes/text, blur, backgrounds) and export. Typical flow: start_recording (with seconds) → get_project → update_project " +
            "(look at render_frame to check) → export_video. Edits show live in the app's editor window and can be undone there with Ctrl+Z.";
        public const string ProjectNotes =
            "All times are SOURCE seconds of the raw recording (0…duration). Positions x,y,w,h are fractions 0–1 of the source frame, origin top-left. " +
            "trimStart/trimEnd: kept range. segments: zoom-ins {start,end,zoom(1.2–4),mode follow|fixed,fixedX,fixedY,speed?}; set segmentsEdited=true when you change them. " +
            "cuts: {start,end,mode cut|speed,rate} (cut removes the part, speed plays it rate× faster). splits: split points shown on the clip lane. " +
            "annotations: {start,end,kind arrow|rect|ellipse|highlight|text, x,y,w,h (arrow goes from (x,y) to (x+w,y+h); text: x,y = top-left, w=h=0), " +
            "color{r,g,b 0–1}, width, text, fontSize, arrow plain|double|curved|thick|dashed, textStyle pill|glass|glassDark|dark|gradient|outline|sticker|bubble|neon|marker|plain, opacity, oval}. " +
            "redactions: {start,end,x,y,w,h,style blur|mosaic|solid|frosted|spotlight|focus,strength 0–1,corner}. " +
            "captions: {start,end,text}. captionStyle: {show,...}. look: {aspect original|16:9|9:16|1:1|4:3|custom, aspectW, aspectH, background color|gradient|wallpaper|image (no transparent on Windows), " +
            "color{r,g,b}, gradient0, gradient1, gradientAngle, padding 0–0.2, cornerRadius 0–100, shadow 0–1, cursorScale, smoothing, clickSound, clickStyle soft|mouse|thock|wood|pop|tick, " +
            "typeSound, typeStyle mechanical|laptop|clicky|thock, ripple, rippleStyle ring|glass|double|pulse|glow|focus, blur off|light|medium|strong, showKeys, zoomSpeed 0.4–2.5}. " +
            "audio: {system, mic, clicks (0–2 volumes), mute}. info.clicks are {t, x, y} in the same 0–1 coordinates; use clicks/key_presses to find where things happen.";

        static readonly Dictionary<string, object> ProjectProp = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Recording folder path (from list_recordings / stop_recording)" };
        static Dictionary<string, object> P(string type, string desc = null, object en = null)
        {
            var d = new Dictionary<string, object> { ["type"] = type };
            if (desc != null) d["description"] = desc;
            if (en != null) d["enum"] = en;
            return d;
        }
        static Dictionary<string, object> Tool(string name, string desc, Dictionary<string, object> props, params string[] required) =>
            new Dictionary<string, object> { ["name"] = name, ["description"] = desc, ["inputSchema"] = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props, ["required"] = required.ToList() } };

        public static readonly List<object> Tools = new List<object>
        {
            Tool("get_status", "Is the app recording, where recordings are saved, which projects are open in the editor.", new Dictionary<string, object>()),
            Tool("list_recordings", "List Studio recordings (newest first) with their folder path and duration. The path is what the other tools call `project`.", new Dictionary<string, object>()),
            Tool("start_recording", "Start recording the screen or a window. With `seconds`, records that long, stops, and returns the new project path.", new Dictionary<string, object>
            {
                ["source"] = P("string", "\"screen\" (default), \"screen 2\", or part of a window title / app name like \"Chrome\""),
                ["seconds"] = P("number", "Stop automatically after this many seconds"),
                ["studio"] = P("boolean", "Studio mode (cursor tracked separately, editable zooms). Default: the app's current setting"),
                ["microphone"] = P("boolean"), ["system_audio"] = P("boolean"),
            }),
            Tool("stop_recording", "Stop the current recording. Returns the project folder (Studio mode, opened in the editor) or the video file.", new Dictionary<string, object>()),
            Tool("open_project", "Open a recording in the editor window so the user can watch the edits.", new Dictionary<string, object> { ["project"] = ProjectProp }, "project"),
            Tool("get_project", "Read a recording's editable project (zooms, cuts, captions, annotations, style…) plus info: duration, size, click and key press times. Read this before editing.", new Dictionary<string, object> { ["project"] = ProjectProp }, "project"),
            Tool("update_project", "Change the project with a JSON merge patch: objects merge, arrays replace entirely (send the full array). New array items may omit id and default fields. Example: {\"look\":{\"background\":\"gradient\",\"padding\":0.08},\"annotations\":[{\"start\":3,\"end\":6,\"kind\":\"arrow\",\"x\":0.3,\"y\":0.7,\"w\":0.1,\"h\":-0.15}]}", new Dictionary<string, object>
            {
                ["project"] = ProjectProp, ["patch"] = P("object", "Fields to change, same shape as get_project's project"),
            }, "project", "patch"),
            Tool("render_frame", "Render one finished frame (with zoom, background, annotations…) as an image, to check placement.", new Dictionary<string, object>
            {
                ["project"] = ProjectProp, ["time"] = P("number", "Source time in seconds"), ["max_width"] = P("number", "Image width limit, default 1280"),
            }, "project"),
            Tool("speak_captions", "Turn captions into a spoken voiceover with the voice chosen in the app (on-device AI voice by default). Write the captions first with update_project.", new Dictionary<string, object>
            {
                ["project"] = ProjectProp,
                ["caption_ids"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = P("string"), ["description"] = "Only these captions (default: all with text)" },
                ["voice"] = P("string", "Voice of the engine chosen in the app (e.g. 女声 1, 男声 2, zh-CN-YunxiNeural, alloy, Cherry). Default: the app's current choice"),
                ["rate"] = P("number", "Speed 0.5–1.6, default 1"),
            }, "project"),
            Tool("export_video", "Export the finished video. Returns the file path when done, plus warnings from the pre-export check.", new Dictionary<string, object>
            {
                ["project"] = ProjectProp,
                ["resolution"] = P("string", null, new List<object> { "original", "4k", "1440p", "1080p", "720p" }),
                ["fps"] = P("integer", null, new List<object> { 30.0, 60.0 }), ["quality"] = P("string", null, new List<object> { "high", "standard", "small" }),
                ["format"] = P("string", null, new List<object> { "video", "gif" }), ["codec"] = P("string", "Video codec, default the app's choice", new List<object> { "h264", "hevc" }),
            }, "project"),
        };
    }
}
