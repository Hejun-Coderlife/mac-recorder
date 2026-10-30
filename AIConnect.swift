// 「AI 控制」窗口：告诉用户能让哪些 AI 来操作录屏、一键接上（往各家 AI 的配置文件里加一条 MCP 服务，改之前先备份），
// 以及接上以后怎么说。不支持 MCP 的 AI 用命令行：Recorder ctl 工具名 '{"参数": …}'
import AppKit
import SwiftUI

struct AIClient: Identifiable {
    enum Kind { case json(path: String, key: String, vscode: Bool), toml(path: String), cli(cmd: String), manual(steps: String) }
    let id: String
    let name: String
    let note: String
    let kind: Kind
    let installedHint: [String]          // 有这些路径之一 = 装了这个 AI

    static let exe = Bundle.main.executableURL?.path ?? "/Applications/录屏.app/Contents/MacOS/Recorder"
    static var home: String { NSHomeDirectory() }
    static var all: [AIClient] {
        [
            AIClient(id: "claude-desktop", name: "Claude（桌面版）", note: L("接好后重启 Claude", "Restart Claude afterwards"),
                     kind: .json(path: home + "/Library/Application Support/Claude/claude_desktop_config.json", key: "mcpServers", vscode: false),
                     installedHint: ["/Applications/Claude.app", home + "/Applications/Claude.app"]),
            AIClient(id: "claude-code", name: "Claude Code", note: L("终端里的 Claude；接好后新开一个会话", "Start a new session afterwards"),
                     kind: .cli(cmd: "claude"), installedHint: []),
            AIClient(id: "cursor", name: "Cursor", note: L("接好后在 Cursor 设置 → MCP 里打开", "Enable it in Cursor Settings → MCP"),
                     kind: .json(path: home + "/.cursor/mcp.json", key: "mcpServers", vscode: false),
                     installedHint: ["/Applications/Cursor.app", home + "/.cursor"]),
            AIClient(id: "vscode", name: "VS Code（Copilot）", note: L("在 Copilot 的 Agent 模式里用", "Use it in Copilot Agent mode"),
                     kind: .json(path: home + "/Library/Application Support/Code/User/mcp.json", key: "servers", vscode: true),
                     installedHint: ["/Applications/Visual Studio Code.app"]),
            AIClient(id: "windsurf", name: "Windsurf", note: "",
                     kind: .json(path: home + "/.codeium/windsurf/mcp_config.json", key: "mcpServers", vscode: false),
                     installedHint: ["/Applications/Windsurf.app", home + "/.codeium/windsurf"]),
            AIClient(id: "codex", name: "Codex（OpenAI）", note: L("ChatGPT 桌面版暂时不支持本机工具，用 Codex", "ChatGPT desktop doesn't support local tools yet; use Codex"),
                     kind: .toml(path: home + "/.codex/config.toml"), installedHint: [home + "/.codex"]),
            AIClient(id: "gemini", name: "Gemini CLI（Google）", note: "",
                     kind: .json(path: home + "/.gemini/settings.json", key: "mcpServers", vscode: false), installedHint: [home + "/.gemini"]),
            // 国内：Trae（字节，国际版 / 国内版各自的用户目录，只写 command、args，Trae 不认多余字段）
            AIClient(id: "trae", name: "Trae（字节跳动）", note: L("接好后在 Trae 的 AI 对话里用；可选豆包、DeepSeek 等模型", "Use it in Trae's AI chat"),
                     kind: .json(path: traeDir.map { $0 + "/mcp.json" } ?? home + "/Library/Application Support/Trae/User/mcp.json", key: "mcpServers", vscode: false),
                     installedHint: traeDir.map { [$0] } ?? ["/nonexistent"]),
            AIClient(id: "cherry", name: "Cherry Studio", note: L("能接 DeepSeek、通义千问、Kimi、智谱 GLM、豆包等国内模型", "Works with DeepSeek, Qwen, Kimi, GLM, Doubao…"),
                     kind: .manual(steps: L("打开 Cherry Studio → 设置 → MCP 服务器 → 添加 → 从 JSON 导入，粘贴（已复制）后确定，再在对话里打开它", "Cherry Studio → Settings → MCP Servers → Add → Import from JSON, paste (copied) and confirm, then enable it in a chat")),
                     installedHint: ["/Applications/Cherry Studio.app"]),
            AIClient(id: "lingma", name: "通义灵码（阿里）", note: L("VS Code / JetBrains / 灵码 IDE 里的通义灵码", "Tongyi Lingma in VS Code / JetBrains / Lingma IDE"),
                     kind: .manual(steps: L("打开通义灵码 → 智能体模式 → MCP 工具 → 右上角「+」或「配置文件」，把（已复制的）recorder 那一段加进 mcpServers 里保存", "Open Lingma → Agent mode → MCP tools → “+” or the config file, add the copied recorder entry under mcpServers and save")),
                     installedHint: []),
        ]
    }

    // Trae 的用户目录（国际版 Trae / 国内版 Trae CN），有哪个用哪个
    static var traeDir: String? {
        ["Trae", "Trae CN"].map { home + "/Library/Application Support/" + $0 + "/User" }.first { FileManager.default.fileExists(atPath: $0) }
    }
    static var snippet: String { "{\n  \"mcpServers\": {\n    \"recorder\": {\n      \"command\": \"\(exe)\",\n      \"args\": [\"--mcp\"]\n    }\n  }\n}" }
    // 找命令行工具（App 里 PATH 很短，自己去常见的地方找）
    static func which(_ cmd: String) -> String? {
        let dirs = ["/opt/homebrew/bin", "/usr/local/bin", home + "/.local/bin", home + "/.npm-global/bin", home + "/.claude/local", "/usr/bin"]
        return dirs.map { $0 + "/" + cmd }.first { FileManager.default.isExecutableFile(atPath: $0) }
    }
    var installed: Bool {
        switch kind {
        case .cli(let c): return Self.which(c) != nil
        case .manual: return true
        default: return installedHint.isEmpty || installedHint.contains { FileManager.default.fileExists(atPath: $0) }
        }
    }
    var connected: Bool {
        switch kind {
        case .json(let p, let key, _):
            guard let d = FileManager.default.contents(atPath: p), let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return false }
            return (j[key] as? [String: Any])?["recorder"] != nil
        case .toml(let p):
            return (try? String(contentsOfFile: p, encoding: .utf8))?.contains("[mcp_servers.recorder]") ?? false
        case .manual: return false
        case .cli:
            guard let d = FileManager.default.contents(atPath: Self.home + "/.claude.json"), let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return false }
            return (j["mcpServers"] as? [String: Any])?["recorder"] != nil
        }
    }

    // 接上：先备份原文件（同目录 *.录屏备份），再只加 / 改 recorder 这一条
    func connect() throws {
        let fm = FileManager.default
        // 只在第一次改时备份（之后再接不覆盖，留的是用户最初的原件）
        func backup(_ p: String) { if fm.fileExists(atPath: p) && !fm.fileExists(atPath: p + ".录屏备份") { try? fm.copyItem(atPath: p, toPath: p + ".录屏备份") } }
        switch kind {
        case .json(let p, let key, let vscode):
            var j: [String: Any] = [:]
            if let d = fm.contents(atPath: p), !d.isEmpty {
                guard let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else {
                    throw NSError(domain: "录屏", code: 40, userInfo: [NSLocalizedDescriptionKey: L("\(p) 不是标准 JSON（可能有注释），没敢改；请手动加", "\(p) isn't plain JSON (comments?), left it alone; add it by hand")])
                }
                j = o
            }
            backup(p)
            var servers = j[key] as? [String: Any] ?? [:]
            servers["recorder"] = vscode ? ["type": "stdio", "command": Self.exe, "args": ["--mcp"]] : ["command": Self.exe, "args": ["--mcp"]]
            j[key] = servers
            try fm.createDirectory(atPath: (p as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: j, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]).write(to: URL(fileURLWithPath: p), options: .atomic)
        case .toml(let p):
            var s = (try? String(contentsOfFile: p, encoding: .utf8)) ?? ""
            guard !s.contains("[mcp_servers.recorder]") else { return }
            backup(p)
            if !s.isEmpty && !s.hasSuffix("\n") { s += "\n" }
            s += "\n[mcp_servers.recorder]\ncommand = \"\(Self.exe)\"\nargs = [\"--mcp\"]\n"
            try fm.createDirectory(atPath: (p as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            try s.write(toFile: p, atomically: true, encoding: .utf8)
        case .manual:
            NSPasteboard.general.clearContents(); NSPasteboard.general.setString(Self.snippet, forType: .string)
        case .cli(let c):
            guard let bin = Self.which(c) else { throw NSError(domain: "录屏", code: 41, userInfo: [NSLocalizedDescriptionKey: L("没找到 \(c) 命令", "Couldn't find the \(c) command")]) }
            let p = Process(), pipe = Pipe()
            p.executableURL = URL(fileURLWithPath: bin)
            p.arguments = ["mcp", "add", "-s", "user", "recorder", "--", Self.exe, "--mcp"]
            // npm / Homebrew 装的 claude 是 node 脚本：App 里 PATH 很短找不到 node，把常见目录加上
            var env = ProcessInfo.processInfo.environment
            env["PATH"] = (["/opt/homebrew/bin", "/usr/local/bin", Self.home + "/.local/bin", Self.home + "/.npm-global/bin", Self.home + "/.claude/local", "/usr/bin", "/bin"] + [env["PATH"] ?? ""]).joined(separator: ":")
            p.environment = env
            p.standardOutput = pipe; p.standardError = pipe
            try p.run()
            let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            p.waitUntilExit()
            if p.terminationStatus != 0 && !out.contains("already exists") {
                throw NSError(domain: "录屏", code: 42, userInfo: [NSLocalizedDescriptionKey: out.trimmingCharacters(in: .whitespacesAndNewlines)])
            }
        }
    }
}

struct AIConnectView: View {
    @State private var tick = 0
    @State private var message = ""
    static let examples = [
        L("录我屏幕 20 秒，然后在我点过的地方加箭头，导出 1080p", "Record my screen for 20 seconds, add arrows where I clicked, export 1080p"),
        L("把最近一段录像开头 3 秒去掉，背景换成渐变，加上字幕", "Trim the first 3 seconds of my latest recording, use a gradient background, add captions"),
        L("给第 5 秒那个按钮画个圈，再加一句「点这里」的玻璃标签", "Circle the button at 0:05 and add a glass label saying “Click here”"),
        L("把字幕都念成配音，然后导出竖屏 9:16 发抖音", "Voice all captions, then export a 9:16 vertical video"),
    ]
    var body: some View {
        let _ = tick
        ScrollView {
        VStack(alignment: .leading, spacing: 14) {
            Text(L("让 AI 来操作录屏", "Let AI drive the recorder")).font(.title2.weight(.semibold))
            Text(L("接上以后，在 AI 里用一句话就能让它录屏、剪辑、加标注、导出；剪辑窗口开着时它改什么你都能实时看到，⌘Z 能撤销。只在这台电脑上运行，不上传。",
                   "Once connected, ask the AI in plain words to record, edit, annotate and export. With the editor open you watch every change live and can undo with ⌘Z. Runs only on this Mac."))
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            VStack(spacing: 0) {
                ForEach(AIClient.all) { c in
                    HStack(spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(c.name).font(.body.weight(.medium))
                            if !c.note.isEmpty { Text(c.note).font(.caption).foregroundStyle(.secondary) }
                        }
                        Spacer()
                        if c.connected {
                            Label(L("已接上", "Connected"), systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                        } else if !c.installed {
                            Text(L("没装", "Not installed")).foregroundStyle(.tertiary)
                        } else {
                            if case .manual(let steps) = c.kind {
                                Button(L("复制配置", "Copy Config")) {
                                    try? c.connect(); message = c.name + "：" + steps
                                }
                            } else {
                                Button(L("接上", "Connect")) {
                                    do { try c.connect(); message = L("已接上 \(c.name)。", "Connected \(c.name).") + (c.note.isEmpty ? "" : " " + c.note) }
                                    catch { message = error.localizedDescription }
                                    tick += 1
                                }.buttonStyle(.borderedProminent)
                            }
                        }
                    }
                    .padding(.vertical, 8).padding(.horizontal, 12)
                    Divider()
                }
            }
            .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.04)))
            if !message.isEmpty { Text(message).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }

            Text(L("接上以后可以这样说", "Then just ask, for example")).font(.headline)
            VStack(alignment: .leading, spacing: 6) {
                ForEach(Self.examples, id: \.self) { e in Text("「\(e)」").font(.callout).textSelection(.enabled) }
            }

            DisclosureGroup(L("其他 AI / 手动设置", "Other AI apps / manual setup")) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(L("支持 MCP 的 AI（大多数都支持）：在它的 MCP 设置里加一个本机（stdio）服务：", "Any AI app with MCP support: add a local (stdio) server:"))
                        .font(.callout).fixedSize(horizontal: false, vertical: true)
                    copyable(AIClient.snippet)
                    Text(L("用 DeepSeek：终端里的 Claude Code 换成 DeepSeek 模型时，接上「Claude Code」就能用。", "DeepSeek: if you run Claude Code with a DeepSeek model, connecting “Claude Code” covers it."))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Text(L("能跑终端命令的 AI：", "AI agents that run shell commands:")).font(.callout)
                    copyable("\"\(AIClient.exe)\" ctl list_recordings")
                    Text(L("工具：get_status、list_recordings、start_recording、stop_recording、open_project、get_project、update_project、render_frame、speak_captions、export_video（参数是 JSON，比如 ctl export_video '{\"project\": \"…\", \"resolution\": \"1080p\"}'）",
                           "Tools: get_status, list_recordings, start_recording, stop_recording, open_project, get_project, update_project, render_frame, speak_captions, export_video (JSON arguments)"))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }.padding(.top, 6)
            }
            Spacer(minLength: 0)
        }
        .padding(22)
        }
        .frame(width: 580, height: 820)
    }
    private func copyable(_ s: String) -> some View {
        HStack(alignment: .top) {
            Text(s).font(.system(.caption, design: .monospaced)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            Spacer()
            Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(s, forType: .string) } label: { Image(systemName: "doc.on.doc") }
                .buttonStyle(.borderless).help(L("复制", "Copy"))
        }
        .padding(8).background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.06)))
    }

    private static var window: NSWindow?
    @MainActor static func show() {
        if let w = window { w.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return }
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 580, height: 820), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = L("AI 控制", "AI Control")
        w.isReleasedWhenClosed = false
        w.contentView = NSHostingView(rootView: AIConnectView())
        w.center(); w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        window = w
    }
}

// 命令行：Recorder ctl 工具名 ['{"参数": …}']，结果打印成 JSON（render_frame 的图存成临时 PNG，打印路径）
enum ControlCLI {
    static func run(_ args: [String]) -> Never {
        guard let name = args.first else {
            print("用法 / usage: Recorder ctl <tool> ['{json args}']\ntools: " + ControlDocs.tools.compactMap { $0["name"] as? String }.joined(separator: ", "))
            exit(2)
        }
        var a: [String: Any] = [:]
        if args.count > 1 {
            guard let d = args[1].data(using: .utf8), let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { print("参数要是 JSON 对象"); exit(2) }
            a = o
        }
        var r = MCPBridge.forward(name, a)
        if let img = r["image_base64"] as? String, let d = Data(base64Encoded: img) {
            let u = FileManager.default.temporaryDirectory.appendingPathComponent("recorder-frame-\(UUID().uuidString.prefix(6)).png")
            try? d.write(to: u); r.removeValue(forKey: "image_base64"); r["image"] = u.path
        }
        print(MCPBridge.json(r))
        exit(r["error"] == nil ? 0 : 1)
    }
}
