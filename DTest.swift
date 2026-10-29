// ---- 自测：DTEST=素材夹（/tmp 下的副本，≥ 20 秒）→ 背景音乐、结尾鼠标回到起点、推近速度、建议加速 ----
// 规则直接喂数据；音乐用合成的正弦和弦真导出，量出现时刻、淡入淡出、循环、压低、软限幅；建议加速采纳后导出量时长
import AVFoundation
import AppKit

extension StudioTests {
    @MainActor static func dTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["DTEST"] else { return false }
        Task { @MainActor in
            var ok = true
            func check(_ n: String, _ c: Bool, _ info: String = "") { print(c ? "PASS" : "FAIL", n, info); ok = ok && c }
            func near(_ a: Double, _ b: Double, _ e: Double = 1e-6) -> Bool { abs(a - b) < e }

            // ---- 1. 音乐的规则 ----
            var mt = MusicTrack(); mt.clips = [MusicClip(file: "m", name: "m", start: 2, trimIn: 1, trimOut: 4, fileLength: 5)]
            var oc = mt.occurrences(filmEnd: 10)
            check("循环：2、5、8 秒各放一次，最后一次截到片尾", oc.count == 3 && near(oc[0].at, 2) && near(oc[1].at, 5) && near(oc[2].at, 8) && near(oc[2].len, 2) && near(oc[1].from, 1),
                  "\(oc.map { ($0.at, $0.len) })")
            check("只有第一次淡入、最后一次淡出", oc[0].fadeIn && !oc[1].fadeIn && oc[2].fadeOut && !oc[0].fadeOut)
            mt.loop = false; oc = mt.occurrences(filmEnd: 10)
            check("不循环：只放一次", oc.count == 1 && oc[0].fadeOut)
            mt.volume = 0.8; mt.fadeIn = 1; mt.fadeOut = 2
            check("淡入一半时音量一半", abs(Double(mt.gain(oc[0], at: 0.5)) - 0.4) < 1e-4 && abs(Double(mt.gain(oc[0], at: 1.0)) - 0.8) < 1e-4 && abs(Double(mt.gain(oc[0], at: 2.0)) - 0.4) < 1e-4)
            let lvl = Float(pow(10, -14.0 / 20))
            let cv = MusicDuck.curve(level: lvl, filmEnd: 12, voice: [(3, 5)], env: nil, map: TimeMap(StudioProject(trimStart: 0, trimEnd: 12)))
            func at(_ t: Double) -> Float { MusicDuck.at(cv, t) }
            check("压低曲线：讲解里压到 -14 dB、外面不压", abs(at(4) - lvl) < 1e-4 && at(2) == 1 && at(7) == 1, "\(at(4))")
            check("讲解前 0.15 秒开始压、0.12 秒压到位", at(2.84) == 1 && at(2.93) < 0.6 && abs(at(2.98) - lvl) < 0.02, "\(at(2.84)) \(at(2.93)) \(at(2.98))")
            check("讲完停 0.3 秒再 0.4 秒回来", abs(at(5.25) - lvl) < 1e-3 && at(5.5) > lvl + 0.1 && at(5.5) < 0.9 && at(5.8) > 0.99, "\(at(5.25)) \(at(5.5)) \(at(5.8))")
            let env: [Float] = (0..<1200).map { i in (i >= 600 && i < 700) ? 0.05 : 0.001 }   // 6–7 秒原声音在说话
            let cv2 = MusicDuck.curve(level: lvl, filmEnd: 12, voice: [], env: env, map: TimeMap(StudioProject(trimStart: 0, trimEnd: 12)))
            check("原声音说话时也压低", abs(MusicDuck.at(cv2, 6.5) - lvl) < 1e-3 && MusicDuck.at(cv2, 4) == 1)
            var pc = StudioProject(trimStart: 0, trimEnd: 12); pc.cuts = [CutSegment(start: 1, end: 3)]   // 素材 1–3 秒剪掉：素材 6 秒 = 成片 4 秒
            let cv3 = MusicDuck.curve(level: lvl, filmEnd: 10, voice: [], env: env, map: TimeMap(pc))
            check("有剪掉段时按成片时间压", abs(MusicDuck.at(cv3, 4.5) - lvl) < 1e-3 && MusicDuck.at(cv3, 6.5) > 0.99)
            let old = #"{"segments":[],"trimStart":0,"trimEnd":10,"look":{"aspect":"16:9"}}"#.data(using: .utf8)!
            let po = try? JSONDecoder().decode(StudioProject.self, from: old)
            check("旧工程兼容", po?.music == MusicTrack() && po?.look.zoomSpeed == 1 && po?.look.cursorLoop == false && po?.look.aspect == .wide)
            var pr = StudioProject(trimStart: 0, trimEnd: 10); pr.music = mt; pr.music.duckDB = -20; pr.look.zoomSpeed = 1.7; pr.look.cursorLoop = true
            pr.segments = [ZoomSegment(start: 1, end: 2, speed: 0.6)]
            let rt = try? JSONDecoder().decode(StudioProject.self, from: JSONEncoder().encode(pr))
            check("存了再读一样", rt?.music == pr.music && rt?.look == pr.look && rt?.segments.first?.speed == 0.6)

            // ---- 2. 推近速度、结尾鼠标回到起点（镜头计划） ----
            func fake(_ dur: Double, clicks: [(Double, Double, Double)], keys: [Double] = [], moves: [(Double, Double, Double)]) -> Track {
                var t = Track(width: 1600, height: 1000, pixelsPerPoint: 2)
                t.moves = moves.map { [$0.0, $0.1, $0.2, 0, 0] }
                t.clicks = clicks.map { [$0.0, $0.1, $0.2] }
                t.keys = keys
                t.cursors = [CursorShape(file: "", hotX: 0, hotY: 0, w: 16, h: 16, arrow: true)]
                return t
            }
            let mv = stride(from: 0.0, through: 20, by: 0.1).map { t in (t, 300 + 50 * t, 200 + 20 * t) }   // 鼠标一直慢慢往右下走
            let tr = fake(20, clicks: [(2, 400, 240), (12, 900, 440)], moves: mv)
            let st = StudioStyle(), fps = Double(st.fps)
            let lay = StudioLayout(track: tr, look: StudioLook(), maxShort: st.maxShort, maxWidth: st.maxWidth)
            func arrive(_ plan: [FramePlan], from t0: Double, z: Double) -> Double {
                for i in Int(t0 * fps)..<plan.count where plan[i].z >= z { return Double(i) / fps - t0 }
                return 99
            }
            let segs = [ZoomSegment(start: 1, end: 6, zoom: 2, mode: .fixed, fixedX: 0.3, fixedY: 0.3),
                        ZoomSegment(start: 11, end: 16, zoom: 2, mode: .fixed, fixedX: 0.6, fixedY: 0.5)]
            var times: [Double] = []
            for sp in [0.6, 1.0, 1.7] {
                var lk = StudioLook(); lk.zoomSpeed = sp
                let pl = StudioPlan.make(tr, lay, st, look: lk, segments: segs, frames: Int(20 * fps), duration: 20)
                times.append(arrive(pl, from: 1, z: 1.9))
            }
            check("推近速度：慢 > 标准 > 快（到 1.9 倍用时）", times[0] > times[1] && times[1] > times[2], times.map { String(format: "%.2fs", $0) }.joined(separator: " / "))
            var s2 = segs; s2[1].speed = 1.7
            let base = StudioPlan.make(tr, lay, st, look: StudioLook(), segments: segs, frames: Int(20 * fps), duration: 20)
            let over = StudioPlan.make(tr, lay, st, look: StudioLook(), segments: s2, frames: Int(20 * fps), duration: 20)
            let za1 = arrive(base, from: 1, z: 1.9), zb1 = arrive(over, from: 1, z: 1.9), za2 = arrive(base, from: 11, z: 1.9), zb2 = arrive(over, from: 11, z: 1.9)
            check("单段覆盖只影响那一段", near(za1, zb1, 1e-9) && zb2 < za2 - 0.05, String(format: "段1 %.2f/%.2f 段2 %.2f/%.2f", za1, zb1, za2, zb2))
            var lk = StudioLook(); lk.cursorLoop = true; lk.cursorLoopSec = 1; lk.loopZoomOut = true; lk.smoothing = 0
            let segEnd = [ZoomSegment(start: 15, end: 20, zoom: 2.5, mode: .fixed, fixedX: 0.7, fixedY: 0.6)]
            let lp = StudioPlan.make(tr, lay, st, look: lk, segments: segEnd, frames: Int(20 * fps), duration: 20, range: (3, 18))
            let first = lp[Int(3 * fps)], last = lp[Int(18 * fps) - 1], mid = lp[Int(17.5 * fps)], before = lp[Int(16.9 * fps)]
            check("最后一帧鼠标回到起点", hypot(last.mx - first.mx, last.my - first.my) < 0.5, String(format: "%.2f px", hypot(last.mx - first.mx, last.my - first.my)))
            let raw = StudioPlan.make(tr, lay, st, look: { var l = lk; l.cursorLoop = false; return l }(), segments: segEnd, frames: Int(20 * fps), duration: 20, range: (3, 18))
            check("回去之前鼠标照原轨迹", abs(before.mx - raw[Int(16.9 * fps)].mx) < 1e-6)
            let midRaw = raw[Int(17.5 * fps)], u = (mid.mx - midRaw.mx) / (first.mx - midRaw.mx)
            check("半路上走了大约一半（缓进缓出）", u > 0.35 && u < 0.65, String(format: "%.2f", u))
            check("镜头回到全景", abs(last.z - 1) < 1e-6 && abs(last.sx - lay.cw / 2) < 0.5 && raw[Int(18 * fps) - 1].z > 2, String(format: "z %.3f", last.z))

            // ---- 3. 建议加速（规则） ----
            let keys = stride(from: 9.0, through: 14.0, by: 0.3).map { $0 }
            let mv2: [(Double, Double, Double)] = [(0, 500, 500), (1, 520, 500), (1.9, 800, 600), (2.0, 800, 600), (8.6, 800, 600), (8.7, 830, 610), (15, 830, 610)]
            let st2 = fake(20, clicks: [(1.5, 520, 500), (8.8, 830, 610)], keys: keys, moves: mv2)
            let range = 0.0...18.0
            var diffs = [Float](repeating: 0, count: 72)
            for i in 0..<72 { let t = Double(i) * 0.25; if t < 2 || (t >= 8.5 && t < 14.5) || t >= 16 { diffs[i] = 8 } }   // 2–8.5 秒、14.5–16 秒画面不动
            let sg = SpeedSuggest.find(track: st2, diffs: diffs, range: range, existing: [])
            let idle = sg.first { $0.kind == .idle }, typ = sg.first { $0.kind == .typing }
            check("找到空档（约 2–8.5 秒，4×）", idle.map { near($0.start, 2.3, 0.3) && near($0.end, 8.2, 0.3) && $0.rate == 4 } ?? false, "\(sg.map { ($0.kind.rawValue, $0.start, $0.end) })")
            check("找到打字段（9–13.8 秒，2×）", typ.map { near($0.start, 9.3, 0.05) && near($0.end, 13.8, 0.05) && $0.rate == 2 } ?? false)
            check("不到 3 秒的空档不建议", !sg.contains { $0.start > 14 })
            let sg2 = SpeedSuggest.find(track: st2, diffs: diffs, range: range, existing: [CutSegment(start: 5, end: 6)])
            check("已经剪过的地方不再建议", !sg2.contains { $0.kind == .idle })

            // ---- 4. 真导出 ----
            let dir = URL(fileURLWithPath: path)
            guard let track = try? JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json"))) else { print("DTEST FAIL no track"); exit(1) }
            let dur = (try? await AVURLAsset(url: dir.appendingPathComponent("raw.mp4")).load(.duration).seconds) ?? 0
            guard dur >= 20 else { print("DTEST FAIL 素材要 ≥ 20 秒"); exit(1) }
            let mdir = dir.appendingPathComponent("music"); try? FileManager.default.createDirectory(at: mdir, withIntermediateDirectories: true)
            let vdir = dir.appendingPathComponent("voice"); try? FileManager.default.createDirectory(at: vdir, withIntermediateDirectories: true)
            do {
                try writeChord(mdir.appendingPathComponent("chord.wav"), length: 4, amp: 0.3)
                try writeChord(mdir.appendingPathComponent("loud.wav"), length: 4, amp: 0.9)
                try writeTestVoice(vdir.appendingPathComponent("d.m4a"), length: 3, bursts: [0.5, 1.5, 2.5])
            } catch { check("写测试音频", false, "\(error)"); print("DTEST FAIL"); exit(1) }
            var pb = StudioProject.load(dir, track: track, duration: dur)
            pb.trimStart = 0; pb.trimEnd = 15; pb.cuts = []; pb.voiceovers = []; pb.captions = []; pb.redactions = []; pb.annotations = []; pb.layouts = []
            pb.camera?.show = false
            pb.audio.muted = true; pb.look.clickSound = false; pb.look.background = .color; pb.look.blur = .off
            var mu = MusicTrack(); mu.volume = 1; mu.fadeIn = 0.5; mu.fadeOut = 1; mu.loop = true; mu.duck = false
            mu.clips = [MusicClip(file: "music/chord.wav", name: "chord", start: 2, trimIn: 0, trimOut: 4, fileLength: 4)]
            pb.music = mu
            var o = ExportOptions(); o.res = .sd; o.fps = 30; o.quality = .small
            func export(_ p: StudioProject, _ name: String) async -> (URL?, [Float]) {
                do {
                    let u = try await Studio.render(project: dir, to: URL(fileURLWithPath: "/tmp/dtest-\(name).mp4"), style: StudioStyle(o), edits: p) { _ in }
                    return (u, (try? await VoiceMix.decode(u)) ?? [])
                } catch { print("export", name, error); return (nil, []) }
            }
            func rms(_ a: [Float], _ t0: Double, _ t1: Double) -> Double {
                let i0 = min(a.count, Int(t0 * 48000)), i1 = min(a.count, Int(t1 * 48000))
                guard i1 > i0 else { return 0 }
                return (a[i0..<i1].reduce(0) { $0 + Double($1 * $1) } / Double(i1 - i0)).squareRoot()
            }
            let (_, a1) = await export(pb, "music")
            let full = rms(a1, 3.0, 3.8)
            check("音乐出现在 2 秒（之前没声音）", rms(a1, 0.5, 1.8) < 0.002 && full > 0.05, String(format: "前 %.4f 全 %.3f", rms(a1, 0.5, 1.8), full))
            check("淡入：2.25 秒约一半", abs(rms(a1, 2.2, 2.3) / full - 0.5) < 0.12, String(format: "%.2f", rms(a1, 2.2, 2.3) / full))
            check("循环：第二遍（6–10 秒）、第三遍（10–14 秒）照样响", abs(rms(a1, 7.0, 7.8) / full - 1) < 0.1 && abs(rms(a1, 11.0, 11.8) / full - 1) < 0.1)
            check("淡出：片尾前 0.5 秒约一半", abs(rms(a1, 14.45, 14.55) / full - 0.5) < 0.15, String(format: "%.2f", rms(a1, 14.45, 14.55) / full))
            let peak1 = a1.map(abs).max() ?? 0
            var pl = pb; pl.music.volume = 1.5; pl.music.clips[0].file = "music/loud.wav"   // 0.9 振幅 × 150%：不限幅会到 1.35
            let (u2, a2) = await export(pl, "loud")
            // 量峰值要按立体声读（解成单声道时左右相加会多出 √2）
            let st2v = u2 == nil ? [] : ((try? await MusicMix.decodeStereo(u2!)) ?? [])
            let peak2 = st2v.map(abs).max() ?? 9
            check("音量 150% 的响音乐也不削波（软限幅）", peak2 < 0.99 && peak2 > 0.8 && !a2.isEmpty, String(format: "立体声峰值 %.3f（不限幅应为 1.35；普通音量单声道 %.3f）", peak2, peak1))
            // 压低：讲解锚在素材 8 秒，3 秒长（三声 2 kHz 短促音在 8.5 / 9.5 / 10.5），取两声之间的空隙量音乐
            var pd = pb; pd.music.duck = true; pd.music.duckDB = -14
            pd.voiceovers = [VoiceClip(file: "voice/d.m4a", start: 8.0, trimIn: 0, trimOut: 3, fileLength: 3)]
            let (_, a3) = await export(pd, "duck")
            var pn = pd; pn.music.duck = false
            let (_, a4) = await export(pn, "noduck")
            let rIn = rms(a3, 8.8, 9.3) / max(1e-9, rms(a4, 8.8, 9.3)), rOut = rms(a3, 12.2, 12.8) / max(1e-9, rms(a4, 12.2, 12.8))
            check("讲解时音乐压低约 -14 dB", abs(rIn - Double(lvl)) < 0.04, String(format: "in %.3f (want %.3f)", rIn, Double(lvl)))
            check("讲解外音乐不压", abs(rOut - 1) < 0.05, String(format: "out %.3f", rOut))
            check("导出都有声音", !a1.isEmpty && !a2.isEmpty && !a3.isEmpty)

            // ---- 5. 建议加速：真素材分析 + 采纳后导出时长 ----
            let r = 0.0...min(dur, 30)
            let df = await SpeedSuggest.frameDiffs(raw: dir.appendingPathComponent("raw.mp4"), range: r)
            let real = SpeedSuggest.find(track: track, diffs: df, range: r, existing: [])
            print("INFO 真素材建议：", real.map { String(format: "%@ %.2f–%.2f %.0f×", $0.kind.rawValue, $0.start, $0.end, $0.rate) }, "画面差中位数", df.sorted()[df.count / 2])
            check("画面差算出来了", df.count == Int((r.upperBound - r.lowerBound) / 0.25))
            // 采纳（用规则那组的两个建议，按真素材时间放）→ 导出时长 = 修剪长度 − 省下的
            var pa = pb; pa.music = MusicTrack(); pa.trimEnd = 18
            let picks = [SpeedSuggestion(start: 2.3, end: 8.2, kind: .idle), SpeedSuggestion(start: 9.3, end: 14.0, kind: .typing)]
            pa.cuts = picks.map { CutSegment(start: $0.start, end: $0.end, mode: .speed, rate: $0.rate) }
            let want = 18 - picks.reduce(0) { $0 + ($1.end - $1.start) * (1 - 1 / $1.rate) }
            let (ua, _) = await export(pa, "accepted")
            var got = 0.0
            if let ua { got = (try? await AVURLAsset(url: ua).load(.duration).seconds) ?? 0 }
            check("采纳后导出时长对", abs(got - want) < 1.0 / 30 + 0.02, String(format: "got %.3f want %.3f", got, want))

            print(ok ? "DTEST OK" : "DTEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }

    // 4 秒的 A 大三和弦（220 / 277 / 330 Hz，各 amp/3），立体声 48k WAV
    static func writeChord(_ url: URL, length: Double, amp: Float) throws {
        try? FileManager.default.removeItem(at: url)
        let n = Int(length * 48000)
        let fmt = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
        let f = try AVAudioFile(forWriting: url, settings: fmt.settings)
        let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(n))!
        buf.frameLength = AVAudioFrameCount(n)
        for i in 0..<n {
            let t = Double(i) / 48000
            let v = Float(sin(2 * .pi * 220 * t) + sin(2 * .pi * 277.18 * t) + sin(2 * .pi * 329.63 * t)) * amp / 3
            buf.floatChannelData![0][i] = v; buf.floatChannelData![1][i] = v
        }
        try f.write(from: buf)
    }
}
