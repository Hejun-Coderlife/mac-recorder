// 字幕转配音：用系统自带的声音（本机，不上传）把字幕念出来，存成 voice/tts-xxxxxxxx.m4a，放到讲解轨上、从那句字幕开始。
// 同一句再念一次会替换上次念的那段。声音在「系统设置 → 辅助功能 → 朗读内容 → 系统声音」里能下载更好听的（高级 / 增强）
import AVFoundation
import SwiftUI

@MainActor
enum Speech {
    // 可选的声音：中文、英文，好的排前面
    static var voices: [AVSpeechSynthesisVoice] {
        AVSpeechSynthesisVoice.speechVoices()
            .filter { $0.language.hasPrefix("zh") || $0.language.hasPrefix("en") }
            .sorted { a, b in
                if a.language.hasPrefix("zh") != b.language.hasPrefix("zh") { return a.language.hasPrefix("zh") }
                if a.quality != b.quality { return a.quality.rawValue > b.quality.rawValue }
                return a.name < b.name
            }
    }
    static func label(_ v: AVSpeechSynthesisVoice) -> String {
        let q = v.quality == .premium ? L(" · 高级", " · Premium") : v.quality == .enhanced ? L(" · 增强", " · Enhanced") : ""
        return "\(v.name)（\(v.language)）\(q)"
    }
    // 没选过就挑：中文字幕用最好的中文声音，英文用最好的英文声音
    static func pick(for text: String, saved: String) -> AVSpeechSynthesisVoice? {
        if !saved.isEmpty, let v = AVSpeechSynthesisVoice(identifier: saved) { return v }
        let zh = text.unicodeScalars.contains { (0x4E00...0x9FFF).contains($0.value) }
        return voices.first { $0.language.hasPrefix(zh ? "zh-CN" : "en-US") } ?? voices.first
    }

    private static var keep: AVSpeechSynthesizer?
    // 念成文件；返回时长（秒）
    static func render(_ text: String, voice: AVSpeechSynthesisVoice?, rate: Float, to url: URL) async throws -> Double {
        let syn = AVSpeechSynthesizer(); keep = syn
        let u = AVSpeechUtterance(string: text)
        u.voice = voice
        u.rate = AVSpeechUtteranceDefaultSpeechRate * rate
        var file: AVAudioFile?
        var frames: AVAudioFramePosition = 0
        var failure: Error?
        try? FileManager.default.removeItem(at: url)
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            var finished = false
            syn.write(u) { buf in
                guard !finished else { return }
                guard let pcm = buf as? AVAudioPCMBuffer, pcm.frameLength > 0 else { finished = true; done.resume(); return }
                do {
                    if file == nil {
                        file = try AVAudioFile(forWriting: url, settings: [
                            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: pcm.format.sampleRate,
                            AVNumberOfChannelsKey: Int(pcm.format.channelCount),
                        ], commonFormat: pcm.format.commonFormat, interleaved: pcm.format.isInterleaved)
                    }
                    try file?.write(from: pcm); frames += AVAudioFramePosition(pcm.frameLength)
                } catch { failure = error; finished = true; done.resume() }
            }
        }
        keep = nil
        let rate = file?.processingFormat.sampleRate ?? 1
        file?.close()   // 收尾写好 m4a（不然文件头不完整，读不出来）
        if let failure { throw failure }
        guard file != nil, frames > 0 else { throw NSError(domain: "录屏", code: 30, userInfo: [NSLocalizedDescriptionKey: L("这句念不出来", "Couldn't speak this text")]) }
        return Double(frames) / rate
    }
}

// 现在用哪种声音、哪个声音（每种各记一个）
@MainActor enum TTSPrefs {
    static var engine: TTSEngine {
        get { TTSEngine(rawValue: UserDefaults.standard.string(forKey: "ttsEngine") ?? "") ?? .local }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: "ttsEngine") }
    }
    static func voice(_ e: TTSEngine) -> String {
        UserDefaults.standard.string(forKey: "ttsVoice.\(e.rawValue)") ?? (e == .system ? "" : e.voices.first?.0 ?? "")
    }
    static func setVoice(_ e: TTSEngine, _ v: String) { UserDefaults.standard.set(v, forKey: "ttsVoice.\(e.rawValue)") }
}

extension StudioEditor {
    // 念一句：按选的声音来源，存到 voice/tts-xxxx.(wav|m4a)，返回（文件名, 时长）
    func synthLine(_ text: String, id: UUID) async throws -> (String, Double) {
        let e = TTSPrefs.engine, v = TTSPrefs.voice(e), stem = "voice/tts-" + id.uuidString.prefix(8).lowercased()
        // 同一句以前念过（可能是别的格式）：先删
        for ext in ["wav", "m4a", "mp3"] { try? FileManager.default.removeItem(at: dir.appendingPathComponent(stem + "." + ext)) }
        switch e {
        case .system:
            let name = stem + ".m4a"
            let len = try await Speech.render(text, voice: Speech.pick(for: text, saved: v), rate: Float(ttsRate), to: dir.appendingPathComponent(name))
            return (name, len)
        case .local:
            try await LocalTTS.shared.ensureModel()
            let name = stem + ".wav"
            try await LocalTTS.synth(text, sid: v, speed: ttsRate, to: dir.appendingPathComponent(name))
            return (name, audioLength(dir.appendingPathComponent(name)) ?? 0)
        default:
            let u = try await CloudTTS.synth(e, text: text, voice: v, speed: ttsRate, to: dir.appendingPathComponent(stem + ".wav"))
            return ("voice/" + u.lastPathComponent, audioLength(u) ?? 0)
        }
    }

    // 把这些字幕念成配音（不给 = 全部有字的）；进度在 speaking 里
    func speakCaptions(_ ids: [UUID]? = nil) {
        guard speaking == nil else { return }
        let caps = project.captions.filter { (ids == nil || ids!.contains($0.id)) && !$0.text.trimmingCharacters(in: .whitespaces).isEmpty }
        guard !caps.isEmpty else { NSSound.beep(); return }
        let vdir = dir.appendingPathComponent("voice", isDirectory: true)
        try? FileManager.default.createDirectory(at: vdir, withIntermediateDirectories: true)
        speaking = 0
        Task {
            var made: [VoiceClip] = []
            for (k, c) in caps.enumerated() {
                speaking = Double(k) / Double(caps.count)
                do {
                    let (name, len) = try await synthLine(c.text, id: c.id)
                    guard len > 0.05 else { continue }
                    voicePeaks[name] = nil
                    made.append(VoiceClip(file: name, start: c.start, trimIn: 0, trimOut: len, fileLength: len))
                } catch {
                    showToast(L("念不出来：", "Couldn't speak: ") + error.localizedDescription)
                    break
                }
            }
            speaking = nil
            guard !made.isEmpty else { return }
            // 一次放进去（一步撤销）；同一句以前念过的那段换掉
            let stems = Set(made.map { ($0.file as NSString).deletingPathExtension })
            edit { p in
                p.voiceovers.removeAll { stems.contains(($0.file as NSString).deletingPathExtension) }
                for v in made { p.voiceovers = VoiceClip.place(v, into: p.voiceovers, map: TimeMap(p)) }
            }
            for v in made { loadPeaks(v.file) }
            showToast(L("念好了 \(made.count) 句，放在讲解轨上 · ⌘Z 撤销", "Spoke \(made.count) captions onto the voiceover lane · ⌘Z to undo"))
        }
    }

    // 试听：用现在选的声音念第一句字幕（没有就念一句示例）
    func previewVoice() {
        let text = project.captions.first(where: { !$0.text.isEmpty })?.text ?? L("大家好，这是配音试听。", "Hello, this is a voice preview.")
        previewSynth.stopSpeaking(at: .immediate); previewPlayer?.stop()
        if TTSPrefs.engine == .system {
            let u = AVSpeechUtterance(string: text)
            u.voice = Speech.pick(for: text, saved: TTSPrefs.voice(.system))
            u.rate = AVSpeechUtteranceDefaultSpeechRate * Float(ttsRate)
            previewSynth.speak(u); return
        }
        previewing = true
        Task {
            defer { previewing = false }
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("tts-preview-\(UUID().uuidString.prefix(6)).wav")
            do {
                let e = TTSPrefs.engine
                var u = tmp
                if e == .local { try await LocalTTS.shared.ensureModel(); try await LocalTTS.synth(text, sid: TTSPrefs.voice(e), speed: ttsRate, to: tmp) }
                else { u = try await CloudTTS.synth(e, text: text, voice: TTSPrefs.voice(e), speed: ttsRate, to: tmp) }
                previewPlayer = try AVAudioPlayer(contentsOf: u); previewPlayer?.play()
            } catch { showToast(error.localizedDescription) }
        }
    }
}

// 字幕菜单里「文字转配音」那块：选声音来源 → 选声音 →（云端）填 key → 试听 / 全部念
struct SpeakBox: View {
    @ObservedObject var ed: StudioEditor
    @ObservedObject private var local = LocalTTS.shared
    @State private var engine = TTSPrefs.engine
    @State private var voice = TTSPrefs.voice(TTSPrefs.engine)
    @State private var key = TTSKeys.get(TTSPrefs.engine)
    @State private var region = TTSKeys.get(TTSPrefs.engine, "region")
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(L("文字转配音", "Text to Speech")).font(.headline)
            Picker(L("来源", "Engine"), selection: $engine) {
                ForEach(TTSEngine.allCases) { Text($0.name).tag($0) }
            }
            .onChange(of: engine) { _, e in TTSPrefs.engine = e; voice = TTSPrefs.voice(e); key = TTSKeys.get(e); region = TTSKeys.get(e, "region") }
            Picker(L("声音", "Voice"), selection: $voice) {
                if engine == .system { Text(L("自动（按字幕语言挑最好的）", "Auto (best for the language)")).tag("") }
                ForEach(engine.voices, id: \.0) { Text($0.1).tag($0.0) }
            }
            .onChange(of: voice) { _, v in TTSPrefs.setVoice(engine, v) }
            if engine.cloud {
                SecureField(engine == .volc ? L("Access Token", "Access Token") : L("API key", "API key"), text: $key).textFieldStyle(.roundedBorder)
                    .onChange(of: key) { _, v in TTSKeys.set(engine, "key", v.trimmingCharacters(in: .whitespaces)) }
                if engine == .azure || engine == .volc {
                    TextField(engine == .volc ? L("AppID", "AppID") : L("区域（比如 eastasia、eastus）", "Region (e.g. eastasia, eastus)"), text: $region).textFieldStyle(.roundedBorder)
                        .onChange(of: region) { _, v in TTSKeys.set(engine, "region", v.trimmingCharacters(in: .whitespaces)) }
                }
                if let (t, u) = engine.keyHelp { Link(t + " ↗", destination: u).font(.caption) }
                Text(L("key 只存在这台电脑的钥匙串里；念的时候把字幕文字发给这家服务，按字数收费（一般几分钱一段）。", "The key stays in this Mac's Keychain; caption text is sent to this service when speaking, billed per character."))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else if engine == .local {
                if let p = local.progress {
                    HStack { ProgressView(value: p).frame(width: 120); Text(L("正在下载声音模型（约 360MB，只下一次）", "Downloading the voice model (~360MB, once)")).font(.caption) }
                } else {
                    Text(LocalTTS.ready ? L("在这台电脑上用 AI 念，不上传、不收费。", "AI voice on this Mac — nothing uploaded, free.")
                                        : L("第一次用会下载约 360MB 的声音模型（只下一次），之后不联网也能用。", "First use downloads a ~360MB voice model once; works offline afterwards."))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack {
                Text(L("语速", "Speed")).font(.callout)
                Slider(value: $ed.ttsRate, in: 0.6...1.4).controlSize(.small)
                Text(String(format: "%.1f×", ed.ttsRate)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            HStack {
                Button(L("全部念成配音", "Speak All")) { ed.speakCaptions() }
                    .buttonStyle(.borderedProminent).disabled(ed.project.captions.isEmpty || ed.speaking != nil)
                if let p = ed.speaking { ProgressView(value: p).frame(width: 80) }
                Spacer()
                Button(ed.previewing ? L("生成中…", "Working…") : L("试听", "Preview")) { ed.previewVoice() }.disabled(ed.previewing)
            }
        }
    }
}
