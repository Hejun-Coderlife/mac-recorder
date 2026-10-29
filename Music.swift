// 背景音乐：选一首音乐（拷进素材夹 music/），按成片时间铺在「音乐」轨上；音量、淡入淡出、循环到片尾，
// 有讲解或原声音在说话时自动压低（预览和导出用同一条压低曲线）
import AVFoundation
import AppKit

// 音乐轨上的一段：start 是成片时间（音乐不跟着剪掉 / 快放变速，按成片时间一直往后放）
struct MusicClip: Codable, Equatable, Identifiable {
    var id = UUID()
    var file: String                    // 相对素材夹：music/xxx.m4a
    var name: String                    // 原文件名（显示用）
    var start: Double                   // 成片时间
    var trimIn = 0.0                    // 从音乐的第几秒开始用
    var trimOut: Double                 // 用到第几秒
    var fileLength: Double
    var length: Double { max(0, trimOut - trimIn) }
}

struct MusicTrack: Codable, Equatable {
    var clips: [MusicClip] = []
    var volume = 0.5                    // 0–1.5
    var fadeIn = 1.0                    // 秒
    var fadeOut = 2.0
    var loop = true                     // 最后一段放完了从头再来，一直铺到片尾
    var duck = true                     // 有讲解 / 原声音在说话时压低
    var duckDB = -14.0                  // 压低多少（-6 到 -30 dB）
    init() {}
    enum CodingKeys: String, CodingKey { case clips, volume, fadeIn, fadeOut, loop, duck, duckDB }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = MusicTrack()
        func v<T: Decodable>(_ k: CodingKeys, _ def: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? def }
        clips = v(.clips, z.clips); volume = v(.volume, z.volume); fadeIn = v(.fadeIn, z.fadeIn); fadeOut = v(.fadeOut, z.fadeOut)
        loop = v(.loop, z.loop); duck = v(.duck, z.duck); duckDB = v(.duckDB, z.duckDB)
    }
    var duckLevel: Float { duck ? Float(pow(10, min(0, duckDB) / 20)) : 1 }

    // 实际放的每一次（成片时间）：每段放一次；开了循环时最后一段一直重复到片尾。超过片尾的截掉
    struct Occ: Equatable { let clip: MusicClip; let at: Double; let from: Double; let len: Double; let fadeIn: Bool; let fadeOut: Bool }
    func occurrences(filmEnd: Double) -> [Occ] {
        let cs = clips.filter { $0.length > 0.05 }.sorted { $0.start < $1.start }
        var out: [Occ] = []
        for (k, c) in cs.enumerated() where c.start < filmEnd {
            var at = c.start
            let repeats = loop && k == cs.count - 1
            repeat {
                let len = min(c.length, filmEnd - at)
                guard len > 0.01 else { break }
                out.append(Occ(clip: c, at: at, from: c.trimIn, len: len, fadeIn: at == c.start, fadeOut: false))
                at += c.length
            } while repeats && at < filmEnd - 0.01
            if let l = out.last, l.clip.id == c.id {   // 这一段最后一次放：淡出
                out[out.count - 1] = Occ(clip: l.clip, at: l.at, from: l.from, len: l.len, fadeIn: l.fadeIn, fadeOut: true)
            }
        }
        return out
    }
    // 某一次在它第 x 秒（0…len）的音量（不含压低）：总音量 × 淡入 × 淡出；循环接缝处 15 毫秒的小淡入淡出，免得「咔」一声
    func gain(_ o: Occ, at x: Double) -> Float {
        var g = volume
        let edge = 0.015
        if o.fadeIn { if fadeIn > 0.01 { g *= min(1, x / fadeIn) } } else { g *= min(1, x / edge) }
        let tail = o.len - x
        if o.fadeOut { if fadeOut > 0.01 { g *= min(1, max(0, tail) / fadeOut) } } else { g *= min(1, max(0, tail) / edge) }
        return Float(max(0, g))
    }
}

// 压低曲线（成片时间，每秒 100 个点）：讲解的范围 + 原声音里有人说话的地方（音量包络超过门槛）→ 压到 duckLevel；
// 说话前 0.15 秒开始压（0.12 秒压到位）、说完停 0.3 秒再慢慢回来（0.4 秒）
enum MusicDuck {
    static let rate = 100.0
    static let threshold: Float = 0.012      // 原声音 10 毫秒 RMS 超过约 -38 dBFS 算在说话

    static func curve(level: Float, filmEnd: Double, voice: [(Double, Double)], env: [Float]?, map: TimeMap) -> [Float] {
        let n = max(1, Int((filmEnd * rate).rounded(.up)) + 1)
        guard level < 0.999 else { return [Float](repeating: 1, count: n) }
        var talk = [Bool](repeating: false, count: n)
        for (a, b) in voice {
            let i0 = max(0, Int((a * rate).rounded(.down))), i1 = min(n, Int((b * rate).rounded(.up)))
            if i1 > i0 { for i in i0..<i1 { talk[i] = true } }
        }
        if let env, !env.isEmpty {
            for i in 0..<n {
                let s = map.isIdentity ? Double(i) / rate : map.src(Double(i) / rate)
                let j = Int(s * rate)
                if j >= 0 && j < env.count && env[j] > threshold { talk[i] = true }
            }
        }
        // 往前看 0.15 秒、往后留 0.3 秒
        let pre = Int(0.15 * rate), hold = Int(0.3 * rate)
        var lastTalk = -100000, nextTalk = [Int](repeating: Int.max, count: n)
        var nx = Int.max
        for i in stride(from: n - 1, through: 0, by: -1) { if talk[i] { nx = i }; nextTalk[i] = nx }
        var g: Float = 1, out = [Float](repeating: 1, count: n)
        let down = (1 - level) / Float(0.12 * rate), up = (1 - level) / Float(0.4 * rate)
        for i in 0..<n {
            if talk[i] { lastTalk = i }
            let active = i - lastTalk <= hold || (nextTalk[i] != Int.max && nextTalk[i] - i <= pre)
            let tgt: Float = active ? level : 1
            g = tgt < g ? max(tgt, g - down) : min(tgt, g + up)
            out[i] = g
        }
        return out
    }
    @inline(__always) static func at(_ c: [Float], _ t: Double) -> Float {
        guard !c.isEmpty else { return 1 }
        let x = max(0, t * rate), i = Int(x), u = Float(x - Double(i))
        if i >= c.count - 1 { return c[c.count - 1] }
        return c[i] + (c[i + 1] - c[i]) * u
    }

    // 原声音的音量包络（素材时间，每 10 毫秒一个 RMS）。有麦克风（单声道音轨）就只看麦克风，没有就看全部；按各自音量混
    static func envelope(asset: AVAsset, audio: AudioSettings) async -> [Float]? {
        guard !audio.muted, let tracks = try? await asset.loadTracks(withMediaType: .audio), !tracks.isEmpty else { return nil }
        var mics: [AVAssetTrack] = [], vols: [AVAssetTrack: Double] = [:]
        for t in tracks {
            let fd = try? await t.load(.formatDescriptions).first
            let ch = fd.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2
            if ch == 1 { mics.append(t) }
            vols[t] = ch == 1 ? audio.mic : audio.system
        }
        let use = (mics.isEmpty ? tracks : mics).filter { (vols[$0] ?? 1) > 0.001 }
        guard !use.isEmpty, let r = try? AVAssetReader(asset: asset) else { return nil }
        let o = AVAssetReaderAudioMixOutput(audioTracks: use, audioSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 16000, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
        ])
        let mix = AVMutableAudioMix()
        mix.inputParameters = use.map { t in let p = AVMutableAudioMixInputParameters(track: t); p.setVolume(Float(min(1, vols[t] ?? 1)), at: .zero); return p }
        o.audioMix = mix
        r.add(o)
        guard r.startReading() else { return nil }
        var env: [Float] = [], acc: Double = 0, cnt = 0
        var expected = 0                      // 按时间戳补齐（音轨晚开始时前面是静音）
        while let sb = o.copyNextSampleBuffer() {
            guard let block = CMSampleBufferGetDataBuffer(sb) else { continue }
            let n = CMBlockBufferGetDataLength(block) / 4
            var buf = [Float](repeating: 0, count: n)
            _ = buf.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: n * 4, destination: $0.baseAddress!) }
            let start = Int((CMSampleBufferGetPresentationTimeStamp(sb).seconds * 16000).rounded())
            while expected < start { acc += 0; cnt += 1; expected += 1; if cnt == 160 { env.append(Float((acc / 160).squareRoot())); acc = 0; cnt = 0 } }
            for v in buf {
                acc += Double(v * v); cnt += 1; expected += 1
                if cnt == 160 { env.append(Float((acc / 160).squareRoot())); acc = 0; cnt = 0 }
            }
        }
        return env
    }
}

// 导出时的背景音乐：每个文件解成 48k 立体声浮点，按成片时间混进去（乘音量、淡入淡出、压低曲线）
final class MusicMix: @unchecked Sendable {
    private let track: MusicTrack
    private let occ: [MusicTrack.Occ]
    private let pcm: [String: [Float]]          // 交错立体声
    private let duck: [Float]

    init(track: MusicTrack, occ: [MusicTrack.Occ], pcm: [String: [Float]], duck: [Float]) { self.track = track; self.occ = occ; self.pcm = pcm; self.duck = duck }

    static func load(dir: URL, project p: StudioProject, map: TimeMap, raw: AVAsset) async throws -> MusicMix? {
        let m = p.music
        guard !m.clips.isEmpty, m.volume > 0.001 else { return nil }
        let filmEnd = map.out(p.trimEnd)
        let occ = m.occurrences(filmEnd: filmEnd)
        guard !occ.isEmpty else { return nil }
        var pcm: [String: [Float]] = [:]
        for f in Set(occ.map(\.clip.file)) {
            let url = dir.appendingPathComponent(f)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            pcm[f] = try await decodeStereo(url)
        }
        let env = m.duck ? await MusicDuck.envelope(asset: raw, audio: p.audio) : nil
        let voice = p.audio.voice > 0.001 ? p.voiceovers.filter { $0.volume > 0.001 }.map { $0.outRange(map) } : []
        let duck = MusicDuck.curve(level: m.duckLevel, filmEnd: filmEnd, voice: voice, env: env, map: map)
        return MusicMix(track: m, occ: occ, pcm: pcm, duck: duck)
    }

    static func decodeStereo(_ url: URL) async throws -> [Float] {
        let asset = AVURLAsset(url: url)
        guard let t = try await asset.loadTracks(withMediaType: .audio).first else { return [] }
        let r = try AVAssetReader(asset: asset)
        let o = AVAssetReaderTrackOutput(track: t, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
        ])
        r.add(o)
        guard r.startReading() else { throw r.error ?? Studio.failure("music") }
        var out: [Float] = []
        while let sb = o.copyNextSampleBuffer() {
            guard let block = CMSampleBufferGetDataBuffer(sb) else { continue }
            let n = CMBlockBufferGetDataLength(block) / 4
            var buf = [Float](repeating: 0, count: n)
            _ = buf.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: n * 4, destination: $0.baseAddress!) }
            out += buf
        }
        return out
    }

    // 一块立体声交错 PCM（起点 = 成片第 a0 个采样）：加上落在里面的音乐
    func add(into data: inout [Float], frames n: Int, at a0: Int64) {
        let t0 = Double(a0) / 48000, t1 = Double(a0 + Int64(n)) / 48000
        for o in occ where o.at < t1 && o.at + o.len > t0 {
            guard let p = pcm[o.clip.file], !p.isEmpty else { continue }
            let frames = p.count / 2
            let occStart = Int64((o.at * 48000).rounded()), occLen = Int64((o.len * 48000).rounded()), from = Int64((o.from * 48000).rounded())
            let k0 = max(0, occStart - a0), k1 = min(Int64(n), occStart + occLen - a0)
            guard k1 > k0 else { continue }
            for k in k0..<k1 {
                let idx = a0 + k - occStart, src = Int(from + idx)
                guard src >= 0 && src < frames else { continue }
                let x = Double(idx) / 48000
                let g = track.gain(o, at: x) * MusicDuck.at(duck, Double(a0 + k) / 48000)
                data[Int(k) * 2] += p[src * 2] * g
                data[Int(k) * 2 + 1] += p[src * 2 + 1] * g
            }
        }
    }
}

// 预览里放背景音乐：每段一个 AVAudioPlayer，按成片时间找到「现在放到第几次、第几秒」，偏差超过 80 毫秒就校正；
// 音量 = 总音量 × 淡入淡出 × 压低曲线（每次报时间时更新）
@MainActor final class MusicPreview {
    private var players: [UUID: AVAudioPlayer] = [:]

    func sync(dir: URL, track m: MusicTrack, filmEnd: Double, outTime o: Double, duck: [Float], playing: Bool) {
        let occ = m.occurrences(filmEnd: filmEnd)
        let ids = Set(m.clips.map(\.id))
        for (id, p) in players where !ids.contains(id) { p.stop(); players[id] = nil }
        var active: [UUID: (MusicTrack.Occ, Double)] = [:]
        for x in occ where o >= x.at && o < x.at + x.len { active[x.clip.id] = (x, o - x.at) }
        for c in m.clips {
            guard playing, m.volume > 0.001, let (x, off) = active[c.id] else { if let p = players[c.id], p.isPlaying { p.pause() }; continue }
            if players[c.id] == nil {
                guard let p = try? AVAudioPlayer(contentsOf: dir.appendingPathComponent(c.file)) else { continue }
                p.prepareToPlay(); players[c.id] = p
            }
            guard let p = players[c.id] else { continue }
            p.volume = min(1, m.gain(x, at: off) * MusicDuck.at(duck, o))
            let pos = x.from + off
            if !p.isPlaying { p.currentTime = pos; p.play() }
            else if abs(p.currentTime - pos) > 0.08 { p.currentTime = pos }
        }
    }
    func stopAll() { for p in players.values { p.stop() }; players.removeAll() }
    func pauseAll() { for p in players.values where p.isPlaying { p.pause() } }
}
