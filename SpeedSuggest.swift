// 建议加速：找出「很久没操作、画面也几乎不动」的空档（建议 4×）和「一直在打字」的段落（建议 2×），
// 在剪辑轨上先画成虚线建议块，用户采纳了才变成正式的快放段
import AVFoundation
import CoreImage

struct SpeedSuggestion: Equatable, Identifiable {
    enum Kind: String { case idle, typing }
    var id = UUID()
    var start: Double                   // 素材时间
    var end: Double
    var kind: Kind
    var rate: Double { kind == .idle ? 4 : 2 }
}

enum SpeedSuggest {
    static let bin = 0.25               // 每 0.25 秒看一次
    static let minIdle = 3.0, minTyping = 3.0
    static let keyGap = 1.2             // 两次按键隔得不超过这么久算一直在打
    static let frameStill: Float = 1.5  // 低分辨率灰度图平均每像素差（0–255）小于它算画面没变

    // 纯规则（自测直接喂数据）：diffs[i] = 第 i 个 0.25 秒格子（从 range.lowerBound 起）前后两张图的差
    static func find(track tr: Track, diffs: [Float], range: ClosedRange<Double>, existing: [CutSegment]) -> [SpeedSuggestion] {
        var out: [SpeedSuggestion] = []
        let a = range.lowerBound, b = range.upperBound
        let n = max(0, Int(((b - a) / bin).rounded(.down)))
        guard n > 0 else { return [] }
        // 1) 空档：格子里没有点击、按键，鼠标没怎么动，画面也没变
        var busy = [Bool](repeating: false, count: n)
        func cell(_ t: Double) -> Int? { let i = Int(((t - a) / bin).rounded(.down)); return i >= 0 && i < n ? i : nil }
        for c in tr.clicks { if let i = cell(c[0]) { busy[i] = true } }
        for k in tr.keys { if let i = cell(k) { busy[i] = true } }
        var prev: [Double]?
        for m in tr.moves {
            if let p = prev, hypot(m[1] - p[1], m[2] - p[2]) > 3 || m[4] != p[4], let i = cell(m[0]) { busy[i] = true }
            prev = m
        }
        for i in 0..<n where i < diffs.count && diffs[i] > frameStill { busy[i] = true }
        var runStart: Int?
        for i in 0...n {
            let free = i < n && !busy[i]
            if free { if runStart == nil { runStart = i } ; continue }
            if let s = runStart {
                let t0 = a + Double(s) * bin, t1 = a + Double(i) * bin
                if t1 - t0 >= minIdle { out.append(SpeedSuggestion(start: t0 + 0.3, end: t1 - 0.3, kind: .idle)) }   // 两头留一点，别把动作切掉
                runStart = nil
            }
        }
        // 2) 一直在打字：按键一下接一下（间隔 ≤ 1.2 秒）持续 ≥ 3 秒，中间点击不超过 1 次
        let keys = tr.keys.filter { $0 >= a && $0 <= b }.sorted()
        var i = 0
        while i < keys.count {
            var j = i
            while j + 1 < keys.count && keys[j + 1] - keys[j] <= keyGap { j += 1 }
            let s = keys[i], e = keys[j]
            let clicks = tr.clicks.filter { $0[0] >= s && $0[0] <= e }.count
            if e - s >= minTyping && clicks <= 1 { out.append(SpeedSuggestion(start: s + 0.3, end: e, kind: .typing)) }
            i = j + 1
        }
        // 已经剪掉 / 快放的地方不再建议；空档和打字重叠时留打字的
        out = out.filter { g in g.end - g.start >= 1.0 && !existing.contains { $0.start < g.end && $0.end > g.start } }
        out.sort { $0.start < $1.start }
        var res: [SpeedSuggestion] = []
        for g in out {
            if let l = res.last, l.end > g.start {
                if g.kind == .typing && l.kind == .idle { res[res.count - 1] = g }
                continue
            }
            res.append(g)
        }
        return res
    }

    // 画面变化：每 0.25 秒取一张 80 像素宽的小图，算和上一张的平均差
    static func frameDiffs(raw: URL, range: ClosedRange<Double>) async -> [Float] {
        let asset = AVURLAsset(url: raw)
        let g = AVAssetImageGenerator(asset: asset)
        g.maximumSize = CGSize(width: 80, height: 80)
        g.requestedTimeToleranceBefore = CMTime(seconds: 0.1, preferredTimescale: 600)
        g.requestedTimeToleranceAfter = CMTime(seconds: 0.1, preferredTimescale: 600)
        let n = max(0, Int(((range.upperBound - range.lowerBound) / bin).rounded(.down)))
        var out: [Float] = [], last: [UInt8]?
        for i in 0...n {
            let t = range.lowerBound + Double(i) * bin
            guard let cg = try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)).image else { if i > 0 { out.append(0) }; continue }
            let w = cg.width, h = cg.height
            var d = [UInt8](repeating: 0, count: w * h)
            if let c = CGContext(data: &d, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) {
                c.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
            }
            if let l = last, l.count == d.count {
                var s = 0; for k in 0..<d.count { s += abs(Int(d[k]) - Int(l[k])) }
                out.append(Float(s) / Float(max(1, d.count)))
            } else if i > 0 { out.append(99) }
            last = d
        }
        return out
    }
}
