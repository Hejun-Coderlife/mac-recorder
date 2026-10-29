// 降噪：用系统自带的「声音隔离」音频单元（AUSoundIsolation，本机离线处理，不上传）把人声以外的杂音去掉
// 整条声音先按 100% 处理一遍存成缓存，强度只是「原声 : 处理后」按比例混：轻 85%、中 97%、强 100%——改强度不用重新处理
// 缓存放素材夹 denoise/ 下：xxx.wet.f32（处理后的单声道浮点）和按强度混好的 xxx-light.caf 等（预览、导出都读它）
import AVFoundation

enum Denoise {
    enum Level: String, Codable, CaseIterable { case off, light, medium, strong }
    static func wetShare(_ l: Level) -> Float { switch l { case .off: 0; case .light: 0.85; case .medium: 0.97; case .strong: 1 } }
    static let rate = 48000.0

    static func cacheDir(_ dir: URL) -> URL { dir.appendingPathComponent("denoise", isDirectory: true) }

    // ---- 处理一条单声道 48k 声音（同长输出） ----
    static func isolate(_ x: [Float]) throws -> [Float] {
        guard !x.isEmpty else { return [] }
        let desc = AudioComponentDescription(componentType: kAudioUnitType_Effect, componentSubType: 0x766f_6973 /* vois */,
                                             componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0)
        guard AudioComponentFindNext(nil, [desc]) != nil else { throw Studio.failure(L("这台 Mac 不支持系统降噪", "Noise reduction isn't available on this Mac")) }
        let fmt = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 1)!
        let engine = AVAudioEngine(), player = AVAudioPlayerNode(), eff = AVAudioUnitEffect(audioComponentDescription: desc)
        engine.attach(player); engine.attach(eff)
        engine.connect(player, to: eff, format: fmt)
        engine.connect(eff, to: engine.mainMixerNode, format: fmt)
        let chunk: AVAudioFrameCount = 4096
        try engine.enableManualRenderingMode(.offline, format: fmt, maximumFrameCount: chunk)
        eff.auAudioUnit.parameterTree?.parameter(withAddress: 0)?.value = 100   // 全湿：强度另外按比例混
        try engine.start()
        // 末尾补一段静音，把处理器里留着的尾巴也推出来
        let pad = Int(rate * 0.5), n = x.count
        let inBuf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(n + pad))!
        inBuf.frameLength = AVAudioFrameCount(n + pad)
        let ip = inBuf.floatChannelData![0]
        x.withUnsafeBufferPointer { ip.update(from: $0.baseAddress!, count: n) }
        for k in n..<(n + pad) { ip[k] = 0 }
        player.scheduleBuffer(inBuf, completionHandler: nil)
        player.play()
        let outBuf = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: chunk)!
        var out: [Float] = []; out.reserveCapacity(n + pad)
        while out.count < n + pad {
            let want = AVAudioFrameCount(min(Int(chunk), n + pad - out.count))
            switch try engine.renderOffline(want, to: outBuf) {
            case .success:
                let p = outBuf.floatChannelData![0]
                out.append(contentsOf: UnsafeBufferPointer(start: p, count: Int(outBuf.frameLength)))
            case .insufficientDataFromInputNode, .cannotDoInCurrentContext: continue
            case .error: throw Studio.failure(L("降噪处理失败", "Noise reduction failed"))
            @unknown default: throw Studio.failure("denoise")
            }
        }
        engine.stop()
        // 处理器自己报 0 延迟，实测会晚十几毫秒：拿处理前后最像的偏移对齐
        let lat = lag(of: out, behind: x)
        return Array(out[min(lat, out.count)..<min(lat + n, out.count)]) + [Float](repeating: 0, count: max(0, lat + n - out.count))
    }
    static let defaultLatency = 624   // macOS 26 实测（声音太小、找不到时用它）
    // 找 y 比 x 晚了多少个采样（0…100 毫秒）：先粗找（每 8 个采样）再细找
    static func lag(of y: [Float], behind x: [Float]) -> Int {
        let n = min(x.count, y.count)
        // 挑最响的 2 秒来比
        let win = min(n, Int(rate * 2)), hop = Int(rate / 4)
        var bestStart = 0, bestE = 0.0
        for st in stride(from: 0, to: max(1, n - win), by: hop) {
            var e = 0.0; for i in stride(from: st, to: st + win, by: 16) { e += Double(x[i] * x[i]) }
            if e > bestE { bestE = e; bestStart = st }
        }
        guard bestE / Double(max(1, win / 16)) > 1e-6 else { return defaultLatency }
        let maxLag = Int(rate * 0.1)
        func corr(_ l: Int, _ step: Int) -> Double {
            var c = 0.0, ex = 0.0, ey = 0.0
            for i in stride(from: bestStart, to: min(bestStart + win, n - l), by: step) { let a = Double(x[i]), b = Double(y[i + l]); c += a * b; ex += a * a; ey += b * b }
            return c / max(1e-12, (ex * ey).squareRoot())
        }
        var best = (l: defaultLatency, c: -1.0)
        for l in stride(from: 0, through: maxLag, by: 8) { let c = corr(l, 4); if c > best.c { best = (l, c) } }
        for l in max(0, best.l - 8)...min(maxLag, best.l + 8) { let c = corr(l, 1); if c > best.c { best = (l, c) } }
        return best.c > 0.2 ? best.l : defaultLatency
    }

    // ---- 要处理哪些声音 ----
    struct Source { let key: String; let channels: [[Float]]; let start: Double }   // key 做文件名；start = 音轨在素材里从第几秒开始
    // raw.mp4 里第 i 条音轨（按原声道数解出来：单声道 = 麦克风，双声道 = 电脑声音）
    static func decodeTrack(_ t: AVAssetTrack, asset: AVAsset) async throws -> (channels: [[Float]], start: Double) {
        let fd = try await t.load(.formatDescriptions).first
        let ch = Int(fd.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2) == 1 ? 1 : 2
        let r = try AVAssetReader(asset: asset)
        let o = AVAssetReaderTrackOutput(track: t, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: rate, AVNumberOfChannelsKey: ch,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
        ])
        r.add(o)
        guard r.startReading() else { throw r.error ?? Studio.failure("denoise read") }
        var chans = [[Float]](repeating: [], count: ch)
        var start: Double?
        while let sb = o.copyNextSampleBuffer() {
            if start == nil { start = CMSampleBufferGetPresentationTimeStamp(sb).seconds }
            guard let block = CMSampleBufferGetDataBuffer(sb) else { continue }
            let n = CMBlockBufferGetDataLength(block) / 4
            var buf = [Float](repeating: 0, count: n)
            _ = buf.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: n * 4, destination: $0.baseAddress!) }
            if ch == 1 { chans[0] += buf } else {
                for f in 0..<(n / 2) { chans[0].append(buf[f * 2]); chans[1].append(buf[f * 2 + 1]) }
            }
        }
        return (chans, start ?? 0)
    }

    // 处理后的缓存：没有就算一遍
    private static func wet(_ key: String, channel c: Int, _ x: [Float], dir: URL) throws -> [Float] {
        let u = cacheDir(dir).appendingPathComponent("\(key).\(c).wet.f32")
        if let d = try? Data(contentsOf: u), d.count == x.count * 4 {
            return d.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
        }
        let w = try isolate(x)
        try FileManager.default.createDirectory(at: cacheDir(dir), withIntermediateDirectories: true)
        try w.withUnsafeBytes { try Data($0).write(to: u, options: .atomic) }
        return w
    }
    // 按强度混好的文件（预览、导出读这个）
    static func mixedURL(_ key: String, _ l: Level, dir: URL) -> URL { cacheDir(dir).appendingPathComponent("\(key)-\(l.rawValue).caf") }

    private static func writeMixed(_ key: String, _ chans: [[Float]], _ l: Level, dir: URL) throws {
        let u = mixedURL(key, l, dir: dir)
        if FileManager.default.fileExists(atPath: u.path) { return }
        let k = wetShare(l)
        var mixed: [[Float]] = []
        for (c, x) in chans.enumerated() {
            let w = try wet(key, channel: c, x, dir: dir)
            var m = [Float](repeating: 0, count: x.count)
            for i in 0..<x.count { m[i] = x[i] * (1 - k) + w[i] * k }
            mixed.append(m)
        }
        try write(mixed, to: u)
    }
    static func write(_ chans: [[Float]], to u: URL) throws {
        let fmt = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: AVAudioChannelCount(chans.count), interleaved: false)!
        let n = chans.first?.count ?? 0
        let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(max(1, n)))!
        buf.frameLength = AVAudioFrameCount(n)
        for (c, x) in chans.enumerated() { x.withUnsafeBufferPointer { buf.floatChannelData![c].update(from: $0.baseAddress!, count: n) } }
        let tmp = u.deletingLastPathComponent().appendingPathComponent(".tmp-\(UUID().uuidString).caf")
        do {
            let f = try AVAudioFile(forWriting: tmp, settings: fmt.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            try f.write(from: buf)
        }
        try? FileManager.default.removeItem(at: u)
        try FileManager.default.moveItem(at: tmp, to: u)
    }

    // 素材里哪几条音轨要降噪：麦克风（单声道）都要；电脑声音（双声道）只在打开「电脑声音也降噪」时
    private static func wanted(_ t: AVAssetTrack, _ a: AudioSettings) async -> Bool {
        let fd = try? await t.load(.formatDescriptions).first
        let mono = (fd.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2) == 1
        return mono || a.denoiseSystem
    }

    // ---- 准备：按工程设置把要用的都算好（有缓存就跳过）；progress 0…1 ----
    static func prepare(dir: URL, project p: StudioProject, progress: @Sendable (Double) -> Void = { _ in }) async throws {
        let l = p.audio.denoise
        guard l != .off else { return }
        let raw = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        let tracks = try await raw.loadTracks(withMediaType: .audio)
        var jobs: [() async throws -> Void] = []
        for (i, t) in tracks.enumerated() where await wanted(t, p.audio) {
            let key = "track\(i)"
            if FileManager.default.fileExists(atPath: mixedURL(key, l, dir: dir).path) { continue }
            jobs.append {
                let (ch, _) = try await decodeTrack(t, asset: raw)
                try writeMixed(key, ch, l, dir: dir)
            }
        }
        for c in Set(p.voiceovers.map(\.file)) {
            let key = voiceKey(c)
            if FileManager.default.fileExists(atPath: mixedURL(key, l, dir: dir).path) { continue }
            let u = dir.appendingPathComponent(c)
            guard FileManager.default.fileExists(atPath: u.path) else { continue }
            jobs.append {
                let x = try await VoiceMix.decode(u)
                try writeMixed(key, [x], l, dir: dir)
            }
        }
        for (k, j) in jobs.enumerated() {
            try Task.checkCancellation()
            progress(Double(k) / Double(max(1, jobs.count)))
            try await j()
        }
        progress(1)
    }
    static func voiceKey(_ file: String) -> String { "voice-" + URL(fileURLWithPath: file).deletingPathExtension().lastPathComponent }

    // 讲解文件：降噪开着并且算好了，就读处理后的
    static func voiceURL(dir: URL, file: String, level: Level) -> URL {
        if level != .off {
            let u = mixedURL(voiceKey(file), level, dir: dir)
            if FileManager.default.fileExists(atPath: u.path) { return u }
        }
        return dir.appendingPathComponent(file)
    }

    // ---- 预览 / 导出读的素材：降噪关 → raw.mp4；开 → 合成（画面 + 声音，要降噪的音轨换成处理后的，起点和原来一样） ----
    static func asset(dir: URL, project p: StudioProject, raw: AVAsset) async throws -> AVAsset {
        let l = p.audio.denoise
        guard l != .off else { return raw }
        let comp = AVMutableComposition()
        let dur = try await raw.load(.duration)
        for v in try await raw.loadTracks(withMediaType: .video) {
            let r = try await v.load(.timeRange)
            let ct = comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
            try ct?.insertTimeRange(r, of: v, at: r.start)
            ct?.preferredTransform = try await v.load(.preferredTransform)
        }
        for (i, t) in try await raw.loadTracks(withMediaType: .audio).enumerated() {
            let r = try await t.load(.timeRange)
            let ct = comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
            let u = mixedURL("track\(i)", l, dir: dir)
            if await wanted(t, p.audio), FileManager.default.fileExists(atPath: u.path) {
                let a = AVURLAsset(url: u)
                if let st = try await a.loadTracks(withMediaType: .audio).first {
                    let sr = try await st.load(.timeRange)
                    let len = CMTimeMinimum(sr.duration, CMTimeSubtract(dur, r.start))
                    try ct?.insertTimeRange(CMTimeRange(start: .zero, duration: len), of: st, at: r.start)
                    continue
                }
            }
            try ct?.insertTimeRange(r, of: t, at: r.start)
        }
        return comp
    }
}
