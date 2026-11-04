// 「AI 控制」窗口（照 Mac 版 AIConnect.swift）：告诉用户能让哪些 AI 来操作录屏、一键接上
// （往各家 AI 的配置文件里加一条 MCP 服务，改之前先备份成 *.录屏备份），以及接上以后怎么说。
// 不支持 MCP 的 AI 用命令行：录屏.exe ctl 工具名 '{"参数": …}'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed class AIClient
    {
        public enum Kinds { Json, Toml, Cli, Manual }
        public string Id, Name, Note, Path, Key, Cmd, ConfigDir, Steps;
        public Kinds Kind; public bool VsCode;
        public string[] InstalledHint = new string[0];   // 有这些路径之一 = 装了这个 AI（空 = 不查）

        public static string Exe => Application.ExecutablePath;
        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static List<AIClient> All
        {
            get
            {
                var trae = new[] { "Trae", "Trae CN" }.Select(n => System.IO.Path.Combine(AppData, n, "User")).FirstOrDefault(Directory.Exists);
                return new List<AIClient>
                {
                    new AIClient { Id = "claude-desktop", Name = L("Claude（桌面版）", "Claude (desktop)"), Note = L("接好后完全退出 Claude 再打开（托盘里也要退）", "Fully quit Claude afterwards (tray too) and reopen"), Kind = Kinds.Json,
                                   Path = System.IO.Path.Combine(AppData, "Claude", "claude_desktop_config.json"), Key = "mcpServers",
                                   InstalledHint = new[] { System.IO.Path.Combine(Local, "AnthropicClaude"), System.IO.Path.Combine(AppData, "Claude"), System.IO.Path.Combine(Local, "Programs", "Claude") } },
                    new AIClient { Id = "claude-code", Name = "Claude Code", Note = L("终端里的 Claude；接好后新开一个会话", "Start a new session afterwards"), Kind = Kinds.Cli, Cmd = "claude" },
                    // 用 DeepSeek 跑的 Claude Code（clds）：用单独的配置目录 ~/.claude-ds，要单独接（同 Mac 版；没有这个目录就不显示）
                    new AIClient { Id = "clds", Name = "DeepSeek（clds）", Note = L("终端里的 clds：DeepSeek 模型跑的 Claude Code；接好后新开一个会话", "clds: Claude Code running DeepSeek; start a new session afterwards"), Kind = Kinds.Cli, Cmd = "claude",
                                   ConfigDir = System.IO.Path.Combine(Home, ".claude-ds"), InstalledHint = new[] { System.IO.Path.Combine(Home, ".claude-ds") } },
                    new AIClient { Id = "cursor", Name = "Cursor", Note = L("接好后在 Cursor 设置 → MCP 里打开", "Enable it in Cursor Settings → MCP"), Kind = Kinds.Json,
                                   Path = System.IO.Path.Combine(Home, ".cursor", "mcp.json"), Key = "mcpServers",
                                   InstalledHint = new[] { System.IO.Path.Combine(Local, "Programs", "cursor"), System.IO.Path.Combine(Home, ".cursor") } },
                    new AIClient { Id = "vscode", Name = "VS Code（Copilot）", Note = L("在 Copilot 的 Agent 模式里用", "Use it in Copilot Agent mode"), Kind = Kinds.Json, VsCode = true,
                                   Path = System.IO.Path.Combine(AppData, "Code", "User", "mcp.json"), Key = "servers",
                                   InstalledHint = new[] { System.IO.Path.Combine(Local, "Programs", "Microsoft VS Code"), System.IO.Path.Combine(AppData, "Code") } },
                    new AIClient { Id = "windsurf", Name = "Windsurf", Note = "", Kind = Kinds.Json, Path = System.IO.Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"), Key = "mcpServers",
                                   InstalledHint = new[] { System.IO.Path.Combine(Local, "Programs", "Windsurf"), System.IO.Path.Combine(Home, ".codeium", "windsurf") } },
                    new AIClient { Id = "codex", Name = "Codex（OpenAI）", Note = L("ChatGPT 桌面版暂时不支持本机工具，用 Codex", "ChatGPT desktop doesn't support local tools yet; use Codex"), Kind = Kinds.Toml,
                                   Path = System.IO.Path.Combine(Home, ".codex", "config.toml"), InstalledHint = new[] { System.IO.Path.Combine(Home, ".codex") } },
                    new AIClient { Id = "gemini", Name = L("Gemini CLI（Google）", "Gemini CLI (Google)"), Note = "", Kind = Kinds.Json, Path = System.IO.Path.Combine(Home, ".gemini", "settings.json"), Key = "mcpServers",
                                   InstalledHint = new[] { System.IO.Path.Combine(Home, ".gemini") } },
                    // 国内：Trae（字节，国际版 / 国内版各自的用户目录，只写 command、args，Trae 不认多余字段）
                    new AIClient { Id = "trae", Name = L("Trae（字节跳动）", "Trae (ByteDance)"), Note = L("接好后在 Trae 的 AI 对话里用；可选豆包、DeepSeek 等模型", "Use it in Trae's AI chat"), Kind = Kinds.Json,
                                   Path = System.IO.Path.Combine(trae ?? System.IO.Path.Combine(AppData, "Trae", "User"), "mcp.json"), Key = "mcpServers", InstalledHint = new[] { trae ?? "|none|" } },
                    new AIClient { Id = "cherry", Name = "Cherry Studio", Note = L("能接 DeepSeek、通义千问、Kimi、智谱 GLM、豆包等国内模型", "Works with DeepSeek, Qwen, Kimi, GLM, Doubao…"), Kind = Kinds.Manual,
                                   Steps = L("打开 Cherry Studio → 设置 → MCP 服务器 → 添加 → 从 JSON 导入，粘贴（已复制）后确定，再在对话里打开它", "Cherry Studio → Settings → MCP Servers → Add → Import from JSON, paste (copied) and confirm, then enable it in a chat") },
                    new AIClient { Id = "lingma", Name = L("通义灵码（阿里）", "Tongyi Lingma (Alibaba)"), Note = L("VS Code / JetBrains / 灵码 IDE 里的通义灵码", "Tongyi Lingma in VS Code / JetBrains / Lingma IDE"), Kind = Kinds.Manual,
                                   Steps = L("打开通义灵码 → 智能体模式 → MCP 工具 → 右上角「+」或「配置文件」，把（已复制的）recorder 那一段加进 mcpServers 里保存", "Open Lingma → Agent mode → MCP tools → “+” or the config file, add the copied recorder entry under mcpServers and save") },
                };
            }
        }

        public static string Snippet => "{\n  \"mcpServers\": {\n    \"recorder\": {\n      \"command\": " + JsonOut.Write(Exe) + ",\n      \"args\": [\"--mcp\"]\n    }\n  }\n}";

        // 找命令行工具：PATH 里，再加几个常见的地方（npm 全局、Claude Code 自己装的）
        public static string Which(string cmd)
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Trim() != "").ToList();
            dirs.AddRange(new[] { System.IO.Path.Combine(Home, ".local", "bin"), System.IO.Path.Combine(AppData, "npm"), System.IO.Path.Combine(Home, ".claude", "local") });
            foreach (var d in dirs)
                foreach (var ext in new[] { ".exe", ".cmd", ".bat", "" })
                {
                    try { var p = System.IO.Path.Combine(d.Trim().Trim('"'), cmd + ext); if (File.Exists(p)) return p; } catch { }
                }
            return null;
        }

        public bool Installed
        {
            get
            {
                switch (Kind)
                {
                    case Kinds.Cli: return Which(Cmd) != null && (ConfigDir == null || InstalledHint.Any(p => File.Exists(p) || Directory.Exists(p)));
                    case Kinds.Manual: return true;
                    default: return InstalledHint.Length == 0 || InstalledHint.Any(p => File.Exists(p) || Directory.Exists(p));
                }
            }
        }

        public bool Connected
        {
            get
            {
                try
                {
                    switch (Kind)
                    {
                        case Kinds.Json: return File.Exists(Path) && Json.Parse(File.ReadAllText(Path)) is Dictionary<string, object> j && j.TryGetValue(Key, out var s) && s is Dictionary<string, object> sd && sd.ContainsKey("recorder");
                        case Kinds.Toml: return File.Exists(Path) && File.ReadAllText(Path).Contains("[mcp_servers.recorder]");
                        case Kinds.Cli:
                        {
                            var f = System.IO.Path.Combine(ConfigDir ?? Home, ".claude.json");
                            return File.Exists(f) && Json.Parse(File.ReadAllText(f)) is Dictionary<string, object> cj && cj.TryGetValue("mcpServers", out var cs) && cs is Dictionary<string, object> csd && csd.ContainsKey("recorder");
                        }
                        default: return false;
                    }
                }
                catch { return false; }
            }
        }

        // 接上：先备份原文件（同目录 *.录屏备份，只在第一次改时备份，留的是用户最初的原件），再只加 / 改 recorder 这一条
        public void Connect()
        {
            void Backup(string p) { if (File.Exists(p) && !File.Exists(p + ".录屏备份")) File.Copy(p, p + ".录屏备份"); }
            switch (Kind)
            {
                case Kinds.Json:
                {
                    var j = new Dictionary<string, object>();
                    if (File.Exists(Path) && File.ReadAllText(Path).Trim() != "")
                    {
                        object o = null;
                        try { o = Json.Parse(File.ReadAllText(Path)); } catch { }
                        if (!(o is Dictionary<string, object> od)) throw new Exception(L($"{Path} 不是标准 JSON（可能有注释），没敢改；请手动加", $"{Path} isn't plain JSON (comments?), left it alone; add it by hand"));
                        j = od;
                    }
                    Backup(Path);
                    var servers = j.TryGetValue(Key, out var s) && s is Dictionary<string, object> sd ? sd : new Dictionary<string, object>();
                    var entry = new Dictionary<string, object>();
                    if (VsCode) entry["type"] = "stdio";
                    entry["command"] = Exe; entry["args"] = new List<object> { "--mcp" };
                    servers["recorder"] = entry; j[Key] = servers;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    var tmp = Path + ".tmp";
                    File.WriteAllText(tmp, Pretty(j), new UTF8Encoding(false));
                    if (File.Exists(Path)) File.Replace(tmp, Path, null); else File.Move(tmp, Path);
                    break;
                }
                case Kinds.Toml:
                {
                    var s = File.Exists(Path) ? File.ReadAllText(Path) : "";
                    if (s.Contains("[mcp_servers.recorder]")) return;
                    Backup(Path);
                    if (s != "" && !s.EndsWith("\n")) s += "\n";
                    // TOML 的单引号字符串不转义反斜杠，Windows 路径原样放
                    s += "\n[mcp_servers.recorder]\ncommand = '" + Exe + "'\nargs = [\"--mcp\"]\n";
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    File.WriteAllText(Path, s, new UTF8Encoding(false));
                    break;
                }
                case Kinds.Manual: Clipboard.SetText(Snippet); break;
                case Kinds.Cli:
                {
                    var bin = Which(Cmd) ?? throw new Exception(L($"没找到 {Cmd} 命令", $"Couldn't find the {Cmd} command"));
                    // npm 装的 claude 是 .cmd：交给 cmd.exe 跑
                    bool script = bin.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || bin.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                    var args = $"mcp add -s user recorder -- \"{Exe}\" --mcp";
                    var psi = script ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{bin}\" {args}\"") : new ProcessStartInfo(bin, args);
                    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
                    if (ConfigDir != null) psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = ConfigDir;   // clds 那套单独的配置
                    using (var p = Process.Start(psi))
                    {
                        var err = p.StandardError.ReadToEndAsync(); var outS = p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } throw new Exception("claude mcp add timed out"); }
                        var all = (outS + err.Result).Trim();
                        if (p.ExitCode != 0 && !all.Contains("already exists")) throw new Exception(all);
                    }
                    break;
                }
            }
        }

        // 缩进好的 JSON（配置文件人还要看）
        static string Pretty(object o, string ind = "")
        {
            var i2 = ind + "  ";
            switch (o)
            {
                case Dictionary<string, object> d when d.Count > 0:
                    return "{\n" + string.Join(",\n", d.Select(kv => i2 + JsonOut.Write(kv.Key) + ": " + Pretty(kv.Value, i2))) + "\n" + ind + "}";
                case List<object> l when l.Count > 0:
                    return "[\n" + string.Join(",\n", l.Select(x => i2 + Pretty(x, i2))) + "\n" + ind + "]";
                default: return JsonOut.Write(o);
            }
        }
    }

    sealed class AIConnectForm : Form
    {
        static AIConnectForm open;
        public static void ShowFor(IWin32Window owner)
        {
            if (open != null && !open.IsDisposed) { open.Activate(); return; }
            open = new AIConnectForm(); open.Show(owner);
        }

        static readonly string[] Examples =
        {
            L("录我屏幕 20 秒，然后在我点过的地方加箭头，导出 1080p", "Record my screen for 20 seconds, add arrows where I clicked, export 1080p"),
            L("把最近一段录像开头 3 秒去掉，背景换成渐变，加上字幕", "Trim the first 3 seconds of my latest recording, use a gradient background, add captions"),
            L("给第 5 秒那个按钮画个圈，再加一句「点这里」的玻璃标签", "Circle the button at 0:05 and add a glass label saying “Click here”"),
            L("把字幕都念成配音，然后导出竖屏 9:16 发抖音", "Voice all captions, then export a 9:16 vertical video"),
        };

        readonly FlowLayoutPanel flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(20, 16, 20, 16) };
        readonly Label message = new Label();
        const int W = 540;

        AIConnectForm()
        {
            Text = L("AI 控制", "AI Control"); Icon = App.Icon; StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f); BackColor = Theme.Window; ClientSize = new Size(W + 60, 720); MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedSingle;
            Controls.Add(flow);
            Build();
            FormClosed += (s, e) => open = null;
        }

        Label Lbl(string t, float size = 0, bool bold = false, bool sub = false) => new Label
        {
            Text = t, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 0, 0, 8),
            Font = new Font(Font.FontFamily, size > 0 ? size : Font.Size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = sub ? Theme.Secondary : Theme.Text,
        };

        void Build()
        {
            flow.SuspendLayout();
            flow.Controls.Clear();
            flow.Controls.Add(Lbl(L("让 AI 来操作录屏", "Let AI drive the recorder"), 14, true));
            flow.Controls.Add(Lbl(L("接上以后，在 AI 里用一句话就能让它录屏、剪辑、加标注、导出；剪辑窗口开着时它改什么你都能实时看到，Ctrl+Z 能撤销。只在这台电脑上运行，不上传。",
                                    "Once connected, ask the AI in plain words to record, edit, annotate and export. With the editor open you watch every change live and can undo with Ctrl+Z. Runs only on this PC."), 0, false, true));
            var box = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = W, BackColor = Color.FromArgb(246, 246, 248), Margin = new Padding(0, 4, 0, 10), Padding = new Padding(10, 4, 10, 4) };
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W - 150));
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            foreach (var c in AIClient.All.Where(x => x.Id != "clds" || x.Installed))
            {
                var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
                left.Controls.Add(new Label { Text = c.Name, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0) });
                if (c.Note != "") left.Controls.Add(new Label { Text = c.Note, AutoSize = true, MaximumSize = new Size(W - 160, 0), ForeColor = Theme.Secondary, Margin = new Padding(0, 2, 0, 0) });
                Control right;
                if (c.Connected) right = new Label { Text = "✓ " + L("已接上", "Connected"), AutoSize = true, ForeColor = Color.FromArgb(30, 150, 60), Anchor = AnchorStyles.Right };
                else if (!c.Installed) right = new Label { Text = L("没装", "Not installed"), AutoSize = true, ForeColor = Color.FromArgb(170, 170, 175), Anchor = AnchorStyles.Right };
                else
                {
                    var b = new Button { Text = c.Kind == AIClient.Kinds.Manual ? L("复制配置", "Copy Config") : L("接上", "Connect"), AutoSize = true, Anchor = AnchorStyles.Right, FlatStyle = FlatStyle.Flat, MinimumSize = new Size(80, 28) };
                    if (c.Kind != AIClient.Kinds.Manual) { b.BackColor = Theme.Accent; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; }
                    var cc = c;
                    b.Click += (s, e) =>
                    {
                        try
                        {
                            Cursor = Cursors.WaitCursor;
                            cc.Connect();
                            message.Text = cc.Kind == AIClient.Kinds.Manual ? cc.Name + "：" + cc.Steps : L($"已接上 {cc.Name}。", $"Connected {cc.Name}.") + (cc.Note == "" ? "" : " " + cc.Note);
                        }
                        catch (Exception ex) { message.Text = ex.Message; }
                        finally { Cursor = Cursors.Default; }
                        var keep = message.Text; Build(); message.Text = keep;
                    };
                    right = b;
                }
                right.Margin = new Padding(0, 8, 0, 8);
                box.Controls.Add(left); box.Controls.Add(right);
            }
            flow.Controls.Add(box);
            message.AutoSize = true; message.MaximumSize = new Size(W, 0); message.ForeColor = Theme.Secondary; message.Margin = new Padding(0, 0, 0, 10);
            flow.Controls.Add(message);

            flow.Controls.Add(Lbl(L("接上以后可以这样说", "Then just ask, for example"), 10.5f, true));
            foreach (var e in Examples) flow.Controls.Add(Lbl("「" + e + "」"));

            flow.Controls.Add(Lbl(L("其他 AI / 手动设置", "Other AI apps / manual setup"), 10.5f, true));
            flow.Controls.Add(Lbl(L("支持 MCP 的 AI（大多数都支持）：在它的 MCP 设置里加一个本机（stdio）服务：", "Any AI app with MCP support: add a local (stdio) server:")));
            flow.Controls.Add(Copyable(AIClient.Snippet));
            flow.Controls.Add(Lbl(L("用 DeepSeek：终端里的 Claude Code 换成 DeepSeek 模型时，接上「Claude Code」就能用。", "DeepSeek: if you run Claude Code with a DeepSeek model, connecting “Claude Code” covers it."), 0, false, true));
            flow.Controls.Add(Lbl(L("能跑终端命令的 AI：", "AI agents that run shell commands:")));
            flow.Controls.Add(Copyable("\"" + AIClient.Exe + "\" ctl list_recordings"));
            flow.Controls.Add(Lbl(L("工具：get_status、list_recordings、start_recording、stop_recording、open_project、get_project、update_project、render_frame、speak_captions、export_video（参数是 JSON，比如 ctl export_video \"{\\\"project\\\": \\\"…\\\", \\\"resolution\\\": \\\"1080p\\\"}\"）",
                                    "Tools: get_status, list_recordings, start_recording, stop_recording, open_project, get_project, update_project, render_frame, speak_captions, export_video (JSON arguments)"), 0, false, true));
            flow.ResumeLayout();
        }

        Control Copyable(string s)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = W, BackColor = Color.FromArgb(240, 240, 243), Margin = new Padding(0, 0, 0, 8), Padding = new Padding(8) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W - 70)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            row.Controls.Add(new TextBox { Text = s.Replace("\n", Environment.NewLine), Multiline = s.Contains("\n"), ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(240, 240, 243), Font = new Font("Consolas", 9f), Width = W - 80, Height = s.Contains("\n") ? 16 * (s.Count(c => c == '\n') + 1) : 18, Margin = new Padding(0) });
            var b = new Button { Text = L("复制", "Copy"), AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
            b.Click += (x, e) => { try { Clipboard.SetText(s); message.Text = L("已复制", "Copied"); } catch { } };
            row.Controls.Add(b);
            return row;
        }
    }
}
