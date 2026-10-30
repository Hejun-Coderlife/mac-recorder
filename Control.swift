// 让 AI 直接操作录屏（MCP）：
// - 录屏.app 开着时在本机 127.0.0.1 开一个控制口（随机端口 + 随机口令，写在 ~/Library/Application Support/<bundle id>/control.json，别的电脑连不上）
// - 「Recorder --mcp」是给 Claude 用的 MCP 服务（标准输入输出 JSON-RPC）：把工具调用转给正在运行的录屏.app；没开就先在后台打开它
// - 改工程走剪辑窗口（开着就直接改窗口里的工程，预览马上变、能撤销），所以 AI 改什么用户都看得见
// 接到 Claude Code：claude mcp add recorder -- /Applications/录屏.app/Contents/MacOS/Recorder --mcp
import AppKit
import AVFoundation
import Network
import SwiftUI

// ---- 程序入口：带 --mcp 就只当 MCP 服务，不开界面 ----
@main
enum Main {
    static func main() {
        if CommandLine.arguments.contains("--mcp") { MCPBridge.run() }
        if let i = CommandLine.arguments.firstIndex(of: "ctl") { ControlCLI.run(Array(CommandLine.arguments[(i + 1)...])) }
        RecorderApp.main()
    }
}

enum ControlInfo {
    static var file: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "Recorder").appendingPathComponent("control.json")
    }
}

// ================= App 里：控制口 =================
@MainActor
final class ControlServer {
    static let shared = ControlServer()
    private var listener: NWListener?
    private let token = UUID().uuidString
    weak var recorder: Recorder?

    func start(_ rec: Recorder) {
        recorder = rec
        guard listener == nil else { return }
        let params = NWParameters.tcp
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: "127.0.0.1", port: .any)
        guard let l = try? NWListener(using: params) else { return }
        l.newConnectionHandler = { c in Task { @MainActor in ControlServer.shared.serve(c) } }
        l.stateUpdateHandler = { s in
            if case .ready = s, let port = l.port?.rawValue {
                Task { @MainActor in ControlServer.shared.writeInfo(port: Int(port)) }
            }
        }
        l.start(queue: .main)
        listener = l
    }
    private func writeInfo(port: Int) {
        let f = ControlInfo.file
        try? FileManager.default.createDirectory(at: f.deletingLastPathComponent(), withIntermediateDirectories: true)
        let d = try? JSONSerialization.data(withJSONObject: ["port": port, "token": token, "pid": Int(getpid())])
        FileManager.default.createFile(atPath: f.path, contents: d, attributes: [.posixPermissions: 0o600])
    }

    // 很小的 HTTP：POST /call，头里带 X-Token，正文是 {"name": 工具名, "arguments": {...}}
    private func serve(_ c: NWConnection) {
        c.start(queue: .main)
        var buf = Data()
        func readMore() {
            c.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { data, _, done, err in
                if let data { buf.append(data) }
                if let r = buf.range(of: Data("\r\n\r\n".utf8)) {
                    let head = String(decoding: buf[..<r.lowerBound], as: UTF8.self)
                    let len = head.split(separator: "\r\n").first { $0.lowercased().hasPrefix("content-length:") }
                        .flatMap { Int($0.split(separator: ":")[1].trimmingCharacters(in: .whitespaces)) } ?? 0
                    let body = buf[r.upperBound...]
                    if body.count >= len {
                        let ok = head.split(separator: "\r\n").contains { $0.lowercased() == "x-token: \(self.token.lowercased())" }
                        Task { @MainActor in
                            let out: [String: Any]
                            if !ok { out = ["error": "bad token"] }
                            else if let j = try? JSONSerialization.jsonObject(with: Data(body.prefix(len))) as? [String: Any], let name = j["name"] as? String {
                                out = await ControlTools.call(name, j["arguments"] as? [String: Any] ?? [:])
                            } else { out = ["error": "bad request"] }
                            let d = (try? JSONSerialization.data(withJSONObject: out)) ?? Data("{}".utf8)
                            var resp = Data("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: \(d.count)\r\nConnection: close\r\n\r\n".utf8)
                            resp.append(d)
                            c.send(content: resp, completion: .contentProcessed { _ in c.cancel() })
                        }
                        return
                    }
                }
                if done || err != nil { c.cancel(); return }
                readMore()
            }
        }
        readMore()
    }
}

// ---- 各个工具（在 App 里、主线程上做）----
@MainActor
enum ControlTools {
    static func call(_ name: String, _ a: [String: Any]) async -> [String: Any] {
        do {
            switch name {
            case "get_status": return status()
            case "list_recordings": return try await list()
            case "start_recording": return try await startRec(a)
            case "stop_recording": return try await stopRec()
            case "open_project": let e = try await editor(a); return ["ok": true, "project": e.dir.path]
            case "get_project": return try await getProject(a)
            case "update_project": return try await updateProject(a)
            case "render_frame": return try await renderFrame(a)
            case "export_video": return try await export(a)
            case "speak_captions": return try await speak(a)
            default: return ["error": "unknown tool \(name)"]
            }
        } catch {
            return ["error": error.localizedDescription]
        }
    }
    static func fail(_ s: String) -> NSError { NSError(domain: "录屏", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }

    static func status() -> [String: Any] {
        let r = ControlServer.shared.recorder
        return ["recording": r?.recording ?? false, "elapsed": r?.elapsed ?? 0, "studio_mode": r?.studio ?? false,
                "recordings_folder": Recorder.studioFolder.path,
                "open_projects": StudioEditor.open.keys.map(\.path),
                "busy": (r?.busy ?? false) || StudioEditor.open.values.contains { $0.exportProgress != nil || $0.isRecordingVoice || $0.speaking != nil }]
    }

    static func list() async throws -> [String: Any] {
        let fm = FileManager.default
        let dirs = (try? fm.contentsOfDirectory(at: Recorder.studioFolder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        var out: [[String: Any]] = []
        for d in dirs where fm.fileExists(atPath: d.appendingPathComponent("track.json").path) {
            let date = (try? d.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            let dur = (try? await AVURLAsset(url: d.appendingPathComponent("raw.mp4")).load(.duration).seconds) ?? 0
            out.append(["name": d.lastPathComponent, "path": d.path, "modified": ISO8601DateFormatter().string(from: date), "duration": dur])
        }
        out.sort { ($0["modified"] as? String ?? "") > ($1["modified"] as? String ?? "") }
        return ["recordings": out]
    }

    // 开录：source = "screen"（默认，第一个屏幕）/ "screen 2" / 应用或窗口名（如 "Safari"）；seconds 给了就录这么久自动停，并返回结果
    static func startRec(_ a: [String: Any]) async throws -> [String: Any] {
        guard let r = ControlServer.shared.recorder else { throw fail("录屏窗口还没准备好") }
        guard !r.recording else { throw fail("已经在录了") }
        if let s = a["studio"] as? Bool { r.studio = s }
        if let m = a["microphone"] as? Bool { r.microphone = m }
        if let s = a["system_audio"] as? Bool { r.systemAudio = s }
        await r.refresh()
        let src = (a["source"] as? String ?? "screen").trimmingCharacters(in: .whitespaces)
        let lower = src.lowercased()
        let displays = r.sources.filter(\.isDisplay)
        if lower.hasPrefix("screen") || src.hasPrefix("屏幕") || src.isEmpty {
            let n = Int(lower.filter(\.isNumber)) ?? 1
            guard let d = displays[safe: n - 1] ?? displays.first else { throw fail("没找到屏幕（可能还没给屏幕录制权限）") }
            r.selection = d.id
        } else if let w = r.sources.first(where: { !$0.isDisplay && ($0.title.lowercased().contains(lower) || $0.detail.lowercased().contains(lower)) }) {
            r.selection = w.id
        } else {
            throw fail("没找到叫「\(src)」的窗口；可用的：" + r.sources.map { $0.isDisplay ? $0.title : "\($0.title) — \($0.detail)" }.joined(separator: "；"))
        }
        r.toggle()
        // 等它真的开始（可能有倒计时）
        for _ in 0..<100 where !r.recording { try await Task.sleep(for: .milliseconds(100)) }
        guard r.recording else { throw fail(r.message.isEmpty ? "没能开始录制" : r.message) }
        if let secs = a["seconds"] as? Double, secs > 0 {
            try await Task.sleep(for: .seconds(min(secs, 3600)))
            return try await stopRec()
        }
        return ["recording": true, "source": r.sources.first { $0.id == r.selection }.map { "\($0.title) \($0.detail)" } ?? src]
    }
    static func stopRec() async throws -> [String: Any] {
        guard let r = ControlServer.shared.recorder else { throw fail("录屏窗口还没准备好") }
        guard r.recording else { throw fail("现在没在录") }
        r.lastProject = nil
        r.toggle()
        for _ in 0..<600 where r.recording || r.busy { try await Task.sleep(for: .milliseconds(100)) }
        var out: [String: Any] = ["recording": false, "message": r.message]
        if let p = r.lastProject { out["project"] = p.path; out["note"] = "Studio 素材已在剪辑窗口打开；用 get_project / update_project 改，export_video 导出" }
        else if let f = r.lastFile { out["file"] = f.path }
        return out
    }

    // 拿到（没开就打开）这份素材的剪辑窗口
    static func editor(_ a: [String: Any]) async throws -> StudioEditor {
        guard let p = a["project"] as? String ?? a["path"] as? String else { throw fail("缺 project（素材文件夹路径，list_recordings 里的 path）") }
        let dir = URL(fileURLWithPath: (p as NSString).expandingTildeInPath).standardizedFileURL
        guard FileManager.default.fileExists(atPath: dir.appendingPathComponent("track.json").path) else { throw fail("这个文件夹不是 Studio 素材：\(dir.path)") }
        if StudioEditor.open[dir] == nil { await StudioEditor.open(dir) }
        guard let e = StudioEditor.open[dir] else { throw fail("打不开剪辑窗口") }
        return e
    }

    static func projectJSON(_ p: StudioProject) throws -> Any {
        try JSONSerialization.jsonObject(with: JSONEncoder().encode(p))
    }
    static func getProject(_ a: [String: Any]) async throws -> [String: Any] {
        let e = try await editor(a)
        let t = e.track
        // 点击位置换成 0–1 比例（和标注、遮挡同一套坐标）
        let W = Double(max(t.width, 1)), H = Double(max(t.height, 1))
        let clicks = t.clicks.prefix(300).map { c in c.count >= 3 ? ["t": c[0], "x": (c[1] / W * 1000).rounded() / 1000, "y": (c[2] / H * 1000).rounded() / 1000] : ["t": c.first ?? 0] }
        return [
            "project": try projectJSON(e.project),
            "info": [
                "duration": e.duration, "width": t.width, "height": t.height,
                "clicks": clicks, "key_presses": Array(t.keys.prefix(300)),
                "notes": ControlDocs.projectNotes,
            ] as [String: Any],
        ]
    }

    // 改工程：patch 按 JSON Merge Patch 合进去（对象逐层合并，数组整个替换），数组里的新东西缺 id / 默认字段自动补上
    static func updateProject(_ a: [String: Any]) async throws -> [String: Any] {
        let e = try await editor(a)
        guard let patch = a["patch"] as? [String: Any] else { throw fail("缺 patch（要改的字段，比如 {\"look\": {\"background\": \"gradient\"}}）") }
        guard var cur = try projectJSON(e.project) as? [String: Any] else { throw fail("读不了现在的工程") }
        cur = merge(cur, fill(patch)) as? [String: Any] ?? cur
        let data = try JSONSerialization.data(withJSONObject: cur)
        let p: StudioProject
        do { p = try JSONDecoder().decode(StudioProject.self, from: data) } catch { throw fail("改完的工程不对：\(error)") }
        e.edit { $0 = p }
        return ["ok": true, "project": try projectJSON(e.project)]
    }
    static func merge(_ a: Any, _ b: Any) -> Any {
        guard let ad = a as? [String: Any], let bd = b as? [String: Any] else { return b }
        var r = ad
        for (k, v) in bd { if v is NSNull { r.removeValue(forKey: k) } else { r[k] = r[k].map { merge($0, v) } ?? v } }
        return r
    }
    // 补默认：AI 写数组时常常只给关键字段
    static func fill(_ patch: [String: Any]) -> [String: Any] {
        let defaults: [String: [String: Any]] = [
            "segments": ["zoom": 2.0, "mode": "follow", "fixedX": 0.5, "fixedY": 0.5],
            "cuts": ["mode": "cut", "rate": 4.0],
            "annotations": ["kind": "arrow", "w": 0.0, "h": 0.0, "color": ["r": 1.0, "g": 0.27, "b": 0.23], "width": 5.0, "text": "", "fontSize": 28.0, "pill": true, "drawOn": true],
            "redactions": ["style": "blur", "strength": 0.6, "color": ["r": 0.12, "g": 0.12, "b": 0.14], "corner": 8.0],
            "captions": [:], "layouts": [:], "voiceovers": [:],
        ]
        var p = patch
        for (k, def) in defaults {
            guard let arr = p[k] as? [[String: Any]] else { continue }
            p[k] = arr.map { item in
                var x = def.merging(item) { $1 }
                if x["id"] == nil { x["id"] = UUID().uuidString }
                return x
            }
        }
        return p
    }

    static func renderFrame(_ a: [String: Any]) async throws -> [String: Any] {
        let e = try await editor(a)
        let t = min(max(a["time"] as? Double ?? e.time, 0), e.duration)
        guard let png = await e.stillPNG(at: t, maxWidth: a["max_width"] as? Double ?? 1280) else { throw fail("这一帧画不出来") }
        return ["image_base64": png.base64EncodedString(), "time": t]
    }

    static func speak(_ a: [String: Any]) async throws -> [String: Any] {
        let e = try await editor(a)
        if let v = a["voice"] as? String { e.ttsVoice = Speech.voices.first { $0.name.lowercased() == v.lowercased() || $0.identifier == v }?.identifier ?? e.ttsVoice }
        if let r = a["rate"] as? Double { e.ttsRate = min(max(r, 0.5), 1.6) }
        let ids = (a["caption_ids"] as? [String])?.compactMap(UUID.init(uuidString:))
        let before = e.project.voiceovers.count
        e.speakCaptions(ids)
        try await Task.sleep(for: .milliseconds(100))
        while e.speaking != nil { try await Task.sleep(for: .milliseconds(200)) }
        return ["ok": true, "voiceovers": e.project.voiceovers.count, "added_or_replaced": max(0, e.project.voiceovers.count - before)]
    }
    static func export(_ a: [String: Any]) async throws -> [String: Any] {
        let e = try await editor(a)
        var o = e.exportOptions
        if let r = (a["resolution"] as? String).flatMap(ExportOptions.Res.init(rawValue:)) { o.res = r }
        if let f = a["fps"] as? Int { o.fps = f == 30 ? 30 : 60 }
        if let q = (a["quality"] as? String).flatMap(ExportOptions.Quality.init(rawValue:)) { o.quality = q }
        if let f = (a["format"] as? String).flatMap(ExportOptions.Format.init(rawValue:)) { o.format = f }
        e.exportOptions = o
        guard e.exportProgress == nil else { throw fail("这份素材正在导出") }
        e.export()
        while e.exportProgress != nil { try await Task.sleep(for: .milliseconds(200)) }
        if let err = e.exportError { throw fail(err) }
        guard let u = e.exported else { throw fail("导出被取消了") }
        return ["ok": true, "file": u.path]
    }
}

extension Array { subscript(safe i: Int) -> Element? { indices.contains(i) ? self[i] : nil } }

// ================= MCP 服务（Recorder --mcp）=================
enum MCPBridge {
    static func run() -> Never {
        setvbuf(stdout, nil, _IOLBF, 0)
        while let line = readLine(strippingNewline: true) {
            guard let d = line.data(using: .utf8), let msg = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { continue }
            let id = msg["id"], method = msg["method"] as? String ?? ""
            guard id != nil else { continue }   // 通知不用回
            var result: Any = [String: Any]()
            var error: [String: Any]?
            switch method {
            case "initialize":
                result = ["protocolVersion": (msg["params"] as? [String: Any])?["protocolVersion"] as? String ?? "2025-06-18",
                          "capabilities": ["tools": [String: Any]()],
                          "serverInfo": ["name": "recorder", "version": Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1"],
                          "instructions": ControlDocs.instructions]
            case "ping": result = [String: Any]()
            case "tools/list": result = ["tools": ControlDocs.tools]
            case "tools/call":
                let p = msg["params"] as? [String: Any] ?? [:]
                let name = p["name"] as? String ?? "", args = p["arguments"] as? [String: Any] ?? [:]
                let r = forward(name, args)
                if let err = r["error"] as? String {
                    result = ["content": [["type": "text", "text": err]], "isError": true]
                } else if let img = r["image_base64"] as? String {
                    var rest = r; rest.removeValue(forKey: "image_base64")
                    result = ["content": [["type": "image", "data": img, "mimeType": "image/png"], ["type": "text", "text": json(rest)]]]
                } else {
                    result = ["content": [["type": "text", "text": json(r)]]]
                }
            default: error = ["code": -32601, "message": "Method not found: \(method)"]
            }
            var resp: [String: Any] = ["jsonrpc": "2.0", "id": id!]
            if let error { resp["error"] = error } else { resp["result"] = result }
            print(json(resp))
        }
        exit(0)
    }
    static func json(_ o: Any) -> String {
        guard let d = try? JSONSerialization.data(withJSONObject: o, options: [.sortedKeys, .withoutEscapingSlashes]) else { return "{}" }
        return String(decoding: d, as: UTF8.self)
    }

    // 转给正在运行的录屏.app；连不上就在后台打开它再试（最多 20 秒）
    static func forward(_ name: String, _ args: [String: Any]) -> [String: Any] {
        var launched = false
        let deadline = Date().addingTimeInterval(20)
        while true {
            if let r = post(name, args) { return r }
            if !launched {
                launched = true
                let app = Bundle.main.bundleURL
                let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/open"); p.arguments = ["-g", app.path]
                try? p.run(); p.waitUntilExit()
            }
            if Date() > deadline { return ["error": "连不上录屏 App（打开「录屏」再试）"] }
            Thread.sleep(forTimeInterval: 0.5)
        }
    }
    static func post(_ name: String, _ args: [String: Any]) -> [String: Any]? {
        guard let d = try? Data(contentsOf: ControlInfo.file), let info = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
              let port = info["port"] as? Int, let token = info["token"] as? String,
              let pid = info["pid"] as? Int, kill(pid_t(pid), 0) == 0 else { return nil }
        var req = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/call")!)
        req.httpMethod = "POST"; req.setValue(token, forHTTPHeaderField: "X-Token"); req.timeoutInterval = 3700
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["name": name, "arguments": args])
        let sem = DispatchSemaphore(value: 0)
        var out: [String: Any]?
        URLSession.shared.dataTask(with: req) { data, _, _ in
            out = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            sem.signal()
        }.resume()
        sem.wait()
        return out
    }
}

// ================= 给 AI 看的说明 =================
enum ControlDocs {
    static let instructions = """
    Controls the 录屏 (Screen Recorder) Mac app: record the screen, then edit the recording like Screen Studio (auto zoom, cuts, speed-ups, \
    captions, arrows/boxes/text, blur, backgrounds) and export. Typical flow: start_recording (with seconds) → get_project → update_project \
    (look at render_frame to check) → export_video. Edits show live in the app's editor window and can be undone there with ⌘Z.
    """
    static let projectNotes = """
    All times are SOURCE seconds of the raw recording (0…duration). Positions x,y,w,h are fractions 0–1 of the source frame, origin top-left. \
    trimStart/trimEnd: kept range. segments: zoom-ins {start,end,zoom(1.2–4),mode follow|fixed,fixedX,fixedY,speed?}; set segmentsEdited=true when you change them. \
    cuts: {start,end,mode cut|speed,rate} (cut removes the part, speed plays it rate× faster). splits: split points shown on the clip lane. \
    annotations: {start,end,kind arrow|rect|ellipse|highlight|text, x,y,w,h (arrow goes from (x,y) to (x+w,y+h); text: x,y = top-left, w=h=0), \
    color{r,g,b 0–1}, width, text, fontSize, arrow plain|double|curved|thick|dashed, textStyle pill|glass|dark|outline|bubble|plain, opacity, oval}. \
    redactions: {start,end,x,y,w,h,style blur|mosaic|solid|frosted|spotlight|focus,strength 0–1,corner}. \
    captions: {start,end,text}. captionStyle: {show,...}. look: {aspect original|16:9|9:16|1:1|4:3|custom, aspectW, aspectH, background transparent|color|gradient|wallpaper|image, \
    color{r,g,b}, gradient0, gradient1, gradientAngle, padding 0–0.2, cornerRadius 0–100, shadow 0–1, cursorScale, smoothing, clickSound, clickStyle soft|mouse|thock|wood|pop|tick, \
    typeSound, typeStyle mechanical|laptop|clicky|thock, ripple, blur off|light|medium|strong, showKeys, zoomSpeed 0.4–2.5}. \
    audio: {system, mic, clicks (0–2 volumes), mute}. info.clicks are {t, x, y} in the same 0–1 coordinates; use clicks/key_presses to find where things happen.
    """
    static let tools: [[String: Any]] = [
        tool("get_status", "Is the app recording, where recordings are saved, which projects are open in the editor.", [:]),
        tool("list_recordings", "List Studio recordings (newest first) with their folder path and duration. The path is what the other tools call `project`.", [:]),
        tool("start_recording", "Start recording the screen or a window. With `seconds`, records that long, stops, and returns the new project path.", [
            "source": ["type": "string", "description": "\"screen\" (default), \"screen 2\", or part of an app/window name like \"Safari\""],
            "seconds": ["type": "number", "description": "Stop automatically after this many seconds"],
            "studio": ["type": "boolean", "description": "Studio mode (cursor tracked separately, editable zooms). Default: the app's current setting"],
            "microphone": ["type": "boolean"], "system_audio": ["type": "boolean"],
        ]),
        tool("stop_recording", "Stop the current recording. Returns the project folder (Studio mode, opened in the editor) or the video file.", [:]),
        tool("open_project", "Open a recording in the editor window so the user can watch the edits.", ["project": projectProp], required: ["project"]),
        tool("get_project", "Read a recording's editable project (zooms, cuts, captions, annotations, style…) plus info: duration, size, click and key press times. Read this before editing.", ["project": projectProp], required: ["project"]),
        tool("update_project", "Change the project with a JSON merge patch: objects merge, arrays replace entirely (send the full array). New array items may omit id and default fields. Example: {\"look\":{\"background\":\"gradient\",\"padding\":0.08},\"annotations\":[{\"start\":3,\"end\":6,\"kind\":\"arrow\",\"x\":0.3,\"y\":0.7,\"w\":0.1,\"h\":-0.15}]}", [
            "project": projectProp, "patch": ["type": "object", "description": "Fields to change, same shape as get_project's project"],
        ], required: ["project", "patch"]),
        tool("render_frame", "Render one finished frame (with zoom, background, annotations…) as an image, to check placement.", [
            "project": projectProp, "time": ["type": "number", "description": "Source time in seconds"],
            "max_width": ["type": "number", "description": "Image width limit, default 1280"],
        ], required: ["project"]),
        tool("speak_captions", "Turn captions into a spoken voiceover with a macOS voice (on device). Write the captions first with update_project.", [
            "project": projectProp,
            "caption_ids": ["type": "array", "items": ["type": "string"], "description": "Only these captions (default: all with text)"],
            "voice": ["type": "string", "description": "Voice name, e.g. Tingting, Meijia, Samantha (default: best for the language)"],
            "rate": ["type": "number", "description": "Speed 0.5–1.6, default 1"],
        ], required: ["project"]),
        tool("export_video", "Export the finished video. Returns the file path when done.", [
            "project": projectProp,
            "resolution": ["type": "string", "enum": ["original", "4k", "1440p", "1080p", "720p"]],
            "fps": ["type": "integer", "enum": [30, 60]], "quality": ["type": "string", "enum": ["high", "standard", "small"]],
            "format": ["type": "string", "enum": ["video", "gif"]],
        ], required: ["project"]),
    ]
    static let projectProp: [String: Any] = ["type": "string", "description": "Recording folder path (from list_recordings / stop_recording)"]
    static func tool(_ name: String, _ desc: String, _ props: [String: Any], required: [String] = []) -> [String: Any] {
        ["name": name, "description": desc, "inputSchema": ["type": "object", "properties": props, "required": required] as [String: Any]]
    }
}
