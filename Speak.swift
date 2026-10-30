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

extension StudioEditor {
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
                let name = "voice/tts-" + c.id.uuidString.prefix(8).lowercased() + ".m4a"
                do {
                    let len = try await Speech.render(c.text, voice: Speech.pick(for: c.text, saved: ttsVoice), rate: Float(ttsRate), to: dir.appendingPathComponent(name))
                    voicePeaks[name] = nil
                    made.append(VoiceClip(file: name, start: c.start, trimIn: 0, trimOut: len, fileLength: len))
                } catch {
                    showToast(L("念不出来：", "Couldn't speak: ") + error.localizedDescription)
                }
            }
            speaking = nil
            guard !made.isEmpty else { return }
            // 一次放进去（一步撤销）；同一句以前念过的那段换掉
            let files = Set(made.map(\.file))
            edit { p in
                p.voiceovers.removeAll { files.contains($0.file) }
                for v in made { p.voiceovers = VoiceClip.place(v, into: p.voiceovers, map: TimeMap(p)) }
            }
            for v in made { loadPeaks(v.file) }
            showToast(L("念好了 \(made.count) 句，放在讲解轨上 · ⌘Z 撤销", "Spoke \(made.count) captions onto the voiceover lane · ⌘Z to undo"))
        }
    }
}

// 字幕菜单里「文字转配音」那块
struct SpeakBox: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(L("文字转配音", "Text to Speech")).font(.headline)
            Picker(L("声音", "Voice"), selection: $ed.ttsVoice) {
                Text(L("自动（按字幕语言挑最好的）", "Auto (best for the language)")).tag("")
                ForEach(Speech.voices, id: \.identifier) { Text(Speech.label($0)).tag($0.identifier) }
            }
            HStack {
                Text(L("语速", "Speed")).font(.callout)
                Slider(value: $ed.ttsRate, in: 0.6...1.4).controlSize(.small)
                Text(String(format: "%.1f×", ed.ttsRate)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            Text(L("把字幕念出来放到讲解轨上（本机系统声音，不上传）。先「加一句」写好字，或者用上面的识别。想要更自然的声音：系统设置 → 辅助功能 → 朗读内容 → 系统声音，下载「高级」的。",
                   "Speaks the captions onto the voiceover lane (macOS voices, on this Mac). Add captions first, or transcribe above. For more natural voices: System Settings → Accessibility → Spoken Content → System Voice, download a Premium one."))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button(L("全部念成配音", "Speak All")) { ed.speakCaptions() }
                    .buttonStyle(.borderedProminent).disabled(ed.project.captions.isEmpty || ed.speaking != nil)
                if let p = ed.speaking { ProgressView(value: p).frame(width: 80) }
                Spacer()
                Button(L("试听", "Preview")) { ed.previewVoice() }
            }
        }
    }
}

extension StudioEditor {
    func previewVoice() {
        let text = project.captions.first(where: { !$0.text.isEmpty })?.text ?? L("你好，这是配音试听。", "Hello, this is a voice preview.")
        let u = AVSpeechUtterance(string: text)
        u.voice = Speech.pick(for: text, saved: ttsVoice)
        u.rate = AVSpeechUtteranceDefaultSpeechRate * Float(ttsRate)
        previewSynth.stopSpeaking(at: .immediate)
        previewSynth.speak(u)
    }
}
