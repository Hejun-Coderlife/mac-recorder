// 文字转配音的几种声音来源：
// - local：本机 AI 声音（Kokoro 高音质版，sherpa-onnx 跑；程序在 App 里，声音模型第一次用时下载约 360MB；Apache-2.0）
// - system：macOS 自带声音（不用下载，但比较生硬）
// - azure / openai / qwen：云端声音（最自然，用户自己填 key，按字数付费；key 存在钥匙串里）
import AVFoundation
import Foundation
import Security

enum TTSEngine: String, CaseIterable, Identifiable {
    case local, volc, azure, openai, qwen, system
    var id: String { rawValue }
    var name: String {
        switch self {
        case .local: L("本机 AI 声音（推荐，免费）", "On-device AI voice (free)")
        case .volc: L("火山引擎·豆包（云端，剪映同款）", "Volcengine Doubao (cloud)")
        case .azure: L("微软 Azure（云端）", "Microsoft Azure (cloud)")
        case .openai: "OpenAI（" + L("云端", "cloud") + "）"
        case .qwen: L("阿里百炼 千问（云端）", "Alibaba Qwen TTS (cloud)")
        case .system: L("系统声音（生硬）", "macOS voice (robotic)")
        }
    }
    var cloud: Bool { [.volc, .azure, .openai, .qwen].contains(self) }
    // 申请 key 的地方
    var keyHelp: (String, URL)? {
        switch self {
        case .volc: (L("在火山引擎「豆包语音」控制台开通语音合成，拿 AppID 和 Access Token", "Enable TTS in the Volcengine Doubao Speech console for an AppID + Access Token"), URL(string: "https://console.volcengine.com/speech/app")!)
        case .azure: (L("在 Azure 门户建一个「语音服务」，拿 key 和区域", "Create a Speech resource in the Azure portal for a key + region"), URL(string: "https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices")!)
        case .openai: (L("在 OpenAI 后台创建 API key", "Create an API key in the OpenAI dashboard"), URL(string: "https://platform.openai.com/api-keys")!)
        case .qwen: (L("在阿里云百炼控制台创建 API key", "Create an API key in Alibaba Model Studio"), URL(string: "https://bailian.console.aliyun.com/?tab=model#/api-key")!)
        default: nil
        }
    }
    // 每家可选的声音：(值, 显示名)
    @MainActor var voices: [(String, String)] {
        switch self {
        case .local: LocalTTS.voices
        case .volc: [("zh_female_shuangkuaisisi_moon_bigtts", L("爽快思思（女）", "Sisi (female)")), ("zh_male_wennuanahu_moon_bigtts", L("温暖阿虎（男）", "Ahu (male)")),
                     ("zh_female_tianmeixiaoyuan_moon_bigtts", L("甜美小源（女）", "Xiaoyuan (female)")), ("zh_male_shaonianzixin_moon_bigtts", L("少年梓辛（男）", "Zixin (male)")),
                     ("zh_female_wanwanxiaohe_moon_bigtts", L("湾湾小何（台湾腔）", "Xiaohe (Taiwanese)")), ("zh_male_jingqiangkanye_moon_bigtts", L("京腔侃爷（北京话）", "Kanye (Beijing)")),
                     ("zh_male_M392_conversation_wvae_bigtts", L("对话男声", "Conversational male")),
                     ("BV001_streaming", L("通用女声（免费）", "Standard female (free)")), ("BV002_streaming", L("通用男声（免费）", "Standard male (free)"))]
        case .azure: [("zh-CN-YunxiNeural", L("云希（男，年轻）", "Yunxi (male)")), ("zh-CN-YunjianNeural", L("云健（男，浑厚）", "Yunjian (male)")),
                      ("zh-CN-YunyangNeural", L("云扬（男，播音）", "Yunyang (male, news)")), ("zh-CN-XiaoxiaoNeural", L("晓晓（女，温暖）", "Xiaoxiao (female)")),
                      ("zh-CN-XiaoyiNeural", L("晓伊（女，活泼）", "Xiaoyi (female)")), ("zh-CN-XiaochenNeural", L("晓辰（女，自然）", "Xiaochen (female)")),
                      ("en-US-AndrewMultilingualNeural", "Andrew (EN)"), ("en-US-AvaMultilingualNeural", "Ava (EN)")]
        case .openai: ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"].map { ($0, $0.capitalized) }
        case .qwen: [("Cherry", L("芊悦（女，阳光）", "Cherry (female)")), ("Serena", L("苏瑶（女，温柔）", "Serena (female)")), ("Ethan", L("晨煦（男，阳光）", "Ethan (male)")),
                     ("Chelsie", L("千雪（女，二次元）", "Chelsie (female)")), ("Dylan", L("晓东（北京话）", "Dylan (Beijing)")), ("Jada", L("阿珍（上海话）", "Jada (Shanghai)")), ("Sunny", L("晴儿（四川话）", "Sunny (Sichuan)"))]
        case .system: Speech.voices.map { ($0.identifier, Speech.label($0)) }
        }
    }
}

// ---- key 存钥匙串 ----
enum TTSKeys {
    static func get(_ e: TTSEngine, _ field: String = "key") -> String {
        var q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "录屏-tts-\(e.rawValue)", kSecAttrAccount as String: field,
                                kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return "" }
        q.removeAll()
        return String(decoding: d, as: UTF8.self)
    }
    static func set(_ e: TTSEngine, _ field: String = "key", _ v: String) {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "录屏-tts-\(e.rawValue)", kSecAttrAccount as String: field]
        SecItemDelete(base as CFDictionary)
        guard !v.isEmpty else { return }
        var add = base; add[kSecValueData as String] = Data(v.utf8)
        SecItemAdd(add as CFDictionary, nil)
    }
}

// ---- 本机 AI 声音（Kokoro）----
@MainActor
final class LocalTTS: ObservableObject {
    static let shared = LocalTTS()
    @Published var progress: Double?          // 正在下载声音模型（0–1）
    @Published var error: String?

    // 声音：Kokoro v1.1 的编号 3–57 是中文女声、58–102 是中文男声、0–2 英文女声（挑了些听起来自然的放前面）
    static let voices: [(String, String)] = {
        var v: [(String, String)] = []
        let female = [3, 8, 14, 22, 30, 41, 50, 56], male = [58, 63, 68, 74, 80, 86, 93, 100]
        for (i, s) in female.enumerated() { v.append(("\(s)", L("女声 \(i + 1)", "Female \(i + 1)"))) }
        for (i, s) in male.enumerated() { v.append(("\(s)", L("男声 \(i + 1)", "Male \(i + 1)"))) }
        v.append(("0", L("英文女声（Maple）", "English female (Maple)")))
        return v
    }()

    static let modelName = "kokoro-multi-lang-v1_1"   // 高音质版（不压缩，约 360MB 下载）；压缩版机器味重，不用
    nonisolated static var root: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "Recorder").appendingPathComponent("tts")
    }
    nonisolated static var modelDir: URL { root.appendingPathComponent(modelName) }
    nonisolated static var ready: Bool { FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("voices.bin").path) && FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("model.onnx").path) }
    nonisolated static var helper: URL { Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/tts/sherpa-onnx-offline-tts") }

    // 第一次用：下载声音模型。国内先走 hf-mirror 国内镜像，其他地方先走 GitHub；一个不通换另一个
    private var inFlight: Task<Void, Error>?
    func ensureModel() async throws {
        if Self.ready { return }
        if let t = inFlight { return try await t.value }   // 已经在下了：等同一个
        let t = Task { try await self.downloadModel() }
        inFlight = t
        defer { inFlight = nil }
        try await t.value
    }
    private func downloadModel() async throws {
        if Self.ready { return }
        error = nil; progress = 0
        defer { progress = nil }
        let cn = Locale.current.region?.identifier == "CN" || Locale.preferredLanguages.first?.hasPrefix("zh-Hans") == true
        let tries: [() async throws -> Void] = cn ? [mirrorFiles, githubTarball] : [githubTarball, mirrorFiles]
        defer { try? FileManager.default.removeItem(at: Self.root.appendingPathComponent("kokoro-int8-multi-lang-v1_1")) }   // 以前下的压缩版不要了
        var last: Error?
        for t in tries {
            do { try await t(); if Self.ready { return } } catch { last = error }
        }
        throw last ?? NSError(domain: "录屏", code: 50, userInfo: [NSLocalizedDescriptionKey: L("声音模型下载失败", "Couldn't download the voice model")])
    }
    // GitHub：一个压缩包（147MB）
    private func githubTarball() async throws {
        let url = URL(string: "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/\(Self.modelName).tar.bz2")!
        let tmp = try await download(url, size: 364_855_331)
        // 先解到临时文件夹、全解完再挪过去：解到一半被打断不会留下「看着齐了其实缺文件」的模型
        let stage = Self.root.appendingPathComponent(".unpack-\(UUID().uuidString.prefix(6))")
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: stage); try? FileManager.default.removeItem(at: tmp) }
        let ok = try await Task.detached { () -> Bool in
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/tar"); p.arguments = ["xjf", tmp.path, "-C", stage.path]
            try p.run(); p.waitUntilExit(); return p.terminationStatus == 0
        }.value
        let got = stage.appendingPathComponent(Self.modelName)
        guard ok, FileManager.default.fileExists(atPath: got.appendingPathComponent("voices.bin").path) else { throw NSError(domain: "录屏", code: 51, userInfo: [NSLocalizedDescriptionKey: "unpack failed"]) }
        try? FileManager.default.removeItem(at: Self.modelDir)
        try FileManager.default.moveItem(at: got, to: Self.modelDir)
    }
    // 国内镜像：按文件一个个下（同时下 6 个），先下到临时文件夹，全齐了再挪过去
    private func mirrorFiles() async throws {
        let host = "https://hf-mirror.com", repo = "csukuangfj/\(Self.modelName)"
        let (d, _) = try await URLSession.shared.data(from: URL(string: "\(host)/api/models/\(repo)")!)
        guard let j = try JSONSerialization.jsonObject(with: d) as? [String: Any], let sib = j["siblings"] as? [[String: Any]] else { throw URLError(.badServerResponse) }
        let files = sib.compactMap { $0["rfilename"] as? String }.filter { !$0.hasPrefix(".") && $0 != "README.md" }
        let stage = Self.root.appendingPathComponent(".stage-\(UUID().uuidString.prefix(6))")
        defer { try? FileManager.default.removeItem(at: stage) }
        var done = 0
        try await withThrowingTaskGroup(of: Void.self) { g in
            var it = files.makeIterator(), running = 0
            func next() -> String? { it.next() }
            while running < 6, let f = next() { running += 1; g.addTask { try await Self.fetch("\(host)/\(repo)/resolve/main/\(f)", to: stage.appendingPathComponent(f)) } }
            while try await g.next() != nil {
                done += 1; progress = Double(done) / Double(files.count)
                if let f = next() { g.addTask { try await Self.fetch("\(host)/\(repo)/resolve/main/\(f)", to: stage.appendingPathComponent(f)) } }
            }
        }
        try? FileManager.default.removeItem(at: Self.modelDir)
        try FileManager.default.moveItem(at: stage, to: Self.modelDir)
    }
    nonisolated private static func fetch(_ s: String, to u: URL) async throws {
        guard let url = URL(string: s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s) else { throw URLError(.badURL) }
        let (tmp, r) = try await URLSession.shared.download(from: url)
        guard (r as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        try FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: u)
        try FileManager.default.moveItem(at: tmp, to: u)
    }
    // 大文件下载：带进度
    private func download(_ url: URL, size: Double) async throws -> URL {
        let (bytes, r) = try await URLSession.shared.bytes(from: url)
        guard (r as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        let total = r.expectedContentLength > 0 ? Double(r.expectedContentLength) : size
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("kokoro-\(UUID().uuidString.prefix(6)).tar.bz2")
        FileManager.default.createFile(atPath: tmp.path, contents: nil)
        let h = try FileHandle(forWritingTo: tmp)
        var buf = Data(); buf.reserveCapacity(1 << 20)
        var got = 0.0
        for try await b in bytes {
            buf.append(b)
            if buf.count >= 1 << 20 { try h.write(contentsOf: buf); got += Double(buf.count); buf.removeAll(keepingCapacity: true); progress = min(0.99, got / total) }
        }
        try h.write(contentsOf: buf); try h.close()
        return tmp
    }

    // 念成 wav（24k）：sid = 声音编号，speed 1 = 正常
    nonisolated static func synth(_ text: String, sid: String, speed: Double, to out: URL) async throws {
        let m = modelDir.path
        try await Task.detached {
            let p = Process(), pipe = Pipe()
            p.executableURL = helper
            p.arguments = ["--debug=0", "--num-threads=4",
                           "--kokoro-model=\(m)/model.onnx", "--kokoro-voices=\(m)/voices.bin", "--kokoro-tokens=\(m)/tokens.txt",
                           "--kokoro-data-dir=\(m)/espeak-ng-data", "--kokoro-dict-dir=\(m)/dict",
                           "--kokoro-lexicon=\(m)/lexicon-us-en.txt,\(m)/lexicon-zh.txt",
                           "--tts-rule-fsts=\(m)/date-zh.fst,\(m)/number-zh.fst",
                           "--kokoro-length-scale=\(String(format: "%.2f", 1 / min(max(speed, 0.5), 2)))",
                           "--sid=\(sid)", "--output-filename=\(out.path)", text.hasPrefix("-") ? " " + text : text]   // 以 - 开头的字会被当成参数
            p.standardOutput = pipe; p.standardError = pipe
            try p.run()
            let log = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)   // 先读完输出再等它结束（输出多时不卡死）
            p.waitUntilExit()
            guard p.terminationStatus == 0, FileManager.default.fileExists(atPath: out.path) else {
                throw NSError(domain: "录屏", code: 52, userInfo: [NSLocalizedDescriptionKey: L("本机配音出错：", "On-device voice failed: ") + String(log.suffix(300))])
            }
        }.value
    }
}

// ---- 云端声音 ----
enum CloudTTS {
    static func fail(_ s: String) -> NSError { NSError(domain: "录屏", code: 60, userInfo: [NSLocalizedDescriptionKey: s]) }
    // 念成音频文件，返回文件（扩展名按格式）
    static func synth(_ e: TTSEngine, text: String, voice: String, speed: Double, to base: URL) async throws -> URL {
        let key = TTSKeys.get(e)
        guard !key.isEmpty else { throw fail(L("先在「文字转配音」里填 \(e.name) 的 key", "Enter your \(e.name) key first")) }
        var req: URLRequest
        var ext = "wav"
        switch e {
        case .volc:
            // 豆包语音合成（V1 HTTP）：AppID 填在 region 那一格，Access Token 是 key；头里是「Bearer;」加 token
            let appid = TTSKeys.get(e, "region")
            guard !appid.isEmpty else { throw fail(L("还要填火山引擎的 AppID", "Enter your Volcengine AppID too")) }
            req = URLRequest(url: URL(string: "https://openspeech.bytedance.com/api/v1/tts")!)
            req.setValue("Bearer;\(key)", forHTTPHeaderField: "Authorization")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: [
                "app": ["appid": appid, "token": "access_token", "cluster": "volcano_tts"],
                "user": ["uid": "recorder"],
                "audio": ["voice_type": voice, "encoding": "wav", "speed_ratio": min(max(speed, 0.5), 2)],
                "request": ["reqid": UUID().uuidString, "text": text, "operation": "query"],
            ])
        case .azure:
            let region = TTSKeys.get(e, "region").isEmpty ? "eastasia" : TTSKeys.get(e, "region")
            req = URLRequest(url: URL(string: "https://\(region).tts.speech.microsoft.com/cognitiveservices/v1")!)
            req.setValue(key, forHTTPHeaderField: "Ocp-Apim-Subscription-Key")
            req.setValue("application/ssml+xml", forHTTPHeaderField: "Content-Type")
            req.setValue("riff-24khz-16bit-mono-pcm", forHTTPHeaderField: "X-Microsoft-OutputFormat")
            req.setValue("Recorder", forHTTPHeaderField: "User-Agent")
            let lang = voice.split(separator: "-").prefix(2).joined(separator: "-")
            let esc = text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;")
            let rate = String(format: "%+.0f%%", (speed - 1) * 100)
            req.httpBody = Data("<speak version='1.0' xml:lang='\(lang)'><voice name='\(voice)'><prosody rate='\(rate)'>\(esc)</prosody></voice></speak>".utf8)
        case .openai:
            req = URLRequest(url: URL(string: "https://api.openai.com/v1/audio/speech")!)
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: ["model": "gpt-4o-mini-tts", "voice": voice, "input": text, "response_format": "wav",
                                                                       "instructions": speed > 1.1 ? "Speak a little faster than normal." : speed < 0.9 ? "Speak a little slower than normal." : "Speak naturally."])
        case .qwen:
            req = URLRequest(url: URL(string: "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation")!)
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: ["model": "qwen-tts-latest", "input": ["text": text, "voice": voice]])   // latest 才有方言声音
        default: throw fail("bad engine")
        }
        req.httpMethod = "POST"; req.timeoutInterval = 60
        let (data, resp) = try await URLSession.shared.data(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 else { throw fail("\(e.name)：HTTP \(code) " + String(decoding: data.prefix(300), as: UTF8.self)) }
        var audio = data
        if e == .volc {
            guard let j = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw fail(L("火山引擎返回的不对", "Bad response from Volcengine")) }
            guard (j["code"] as? Int) == 3000, let b = (j["data"] as? String).flatMap({ Data(base64Encoded: $0) }) else {
                throw fail("火山引擎：\(j["code"] ?? "") \(j["message"] ?? "")")
            }
            audio = b
        }
        if e == .qwen {
            // 千问返回的是 JSON，里面给音频文件的地址
            guard let j = try JSONSerialization.jsonObject(with: data) as? [String: Any], let o = j["output"] as? [String: Any],
                  let a = o["audio"] as? [String: Any], let u = (a["url"] as? String).flatMap(URL.init(string:)) else { throw fail(L("千问没返回音频", "Qwen returned no audio")) }
            audio = try await URLSession.shared.data(from: u).0
            ext = u.pathExtension.isEmpty ? "wav" : u.pathExtension
        }
        let out = base.deletingPathExtension().appendingPathExtension(ext)
        try audio.write(to: out)
        return out
    }
}

// 音频文件有多长（秒）
func audioLength(_ u: URL) -> Double? {
    guard let f = try? AVAudioFile(forReading: u) else { return nil }
    return Double(f.length) / f.processingFormat.sampleRate
}
