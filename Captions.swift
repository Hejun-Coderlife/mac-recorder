// Studio 字幕：语音转文字（本机识别普通话）→ 一句一块的字幕，一次只显示一行；预览和导出都烧进画面，也能另存 .srt
// 字幕的起止存素材时间（和推近段一样）：之后加 / 改剪掉、快放，字幕跟着它那段画面一起挪；整句被剪掉的不出
import AppKit
import AVFoundation
import CoreImage
import CoreText
import Speech

// 一句字幕（素材时间）
struct Caption: Codable, Equatable, Identifiable {
    var id = UUID()
    var start: Double
    var end: Double
    var text: String
    // 成片里从哪到哪（剪掉的部分压掉、快放的压短）
    func outRange(_ m: TimeMap) -> (start: Double, end: Double) { (m.out(start), m.out(end)) }
}

// 字幕样式：苹方、粗细、字号（占画布短边）、颜色、描边、阴影、底色条、位置
struct CaptionStyle: Codable, Equatable {
    enum Weight: String, Codable, CaseIterable { case regular, medium, semibold }
    enum Box: String, Codable, CaseIterable { case none, dark, light, glass }
    enum Position: String, Codable, CaseIterable { case bottom, top }
    var show = true
    var weight = Weight.semibold
    var size = 0.05                 // 字高约占画布短边几成（0.03–0.09）
    var color = RGB(1, 1, 1)
    var outline = true              // 黑色描边：浅色画面上也看得清
    var shadow = true
    var box = Box.none
    var position = Position.bottom
    var margin = 0.07               // 离上 / 下边多远（占画布高）
    var cx: Double?, cy: Double?    // 在画面上拖过：字幕中心在画布里的位置（比例，左上为原点）；没拖过 = 按上 / 下 + 边距

    init() {}
    enum CodingKeys: String, CodingKey { case show, weight, size, color, outline, shadow, box, position, margin, cx, cy }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = CaptionStyle()
        func v<T: Decodable>(_ k: CodingKeys, _ def: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? def }
        show = v(.show, z.show); weight = v(.weight, z.weight); size = v(.size, z.size); color = v(.color, z.color)
        outline = v(.outline, z.outline); shadow = v(.shadow, z.shadow); box = v(.box, z.box); position = v(.position, z.position); margin = v(.margin, z.margin)
        cx = try? c.decodeIfPresent(Double.self, forKey: .cx); cy = try? c.decodeIfPresent(Double.self, forKey: .cy)
    }

    var fontName: String {
        switch weight { case .regular: "PingFangSC-Regular"; case .medium: "PingFangSC-Medium"; case .semibold: "PingFangSC-Semibold" }
    }
    // 画布上的字号（像素）：竖屏（比 1 窄）时大一号，手机上看得清
    func fontPx(canvas cw: Double, _ ch: Double) -> Double {
        let base = size * min(cw, ch)
        return cw < ch ? base * 1.25 : base
    }
    // 离边多远（像素）：竖屏放底部时至少留出 14%，别被抖音等 App 底部的按钮和文案挡住
    func edge(canvas cw: Double, _ ch: Double) -> Double {
        let m = cw < ch && position == .bottom ? max(margin, 0.14) : margin
        return m * ch
    }
    // 一行最多几个字（中文按 1、英文数字按 0.55 算）：按画布宽和字号自适应，不超过 18
    func maxChars(canvas cw: Double, _ ch: Double) -> Double {
        min(18, max(8, (cw * 0.86 / fontPx(canvas: cw, ch)).rounded(.down)))
    }
}

// ---- 一帧上叠哪句字幕：画好的字幕图按文字 + 样式 + 画布缓存起来（预览和导出共用） ----
final class CaptionLayer: @unchecked Sendable {
    private struct Item { let start: Double, end: Double, text: String }
    private let items: [Item]                  // 成片时间，按开始排好
    private let map: TimeMap
    private let style: CaptionStyle
    private let cw: Double, ch: Double

    init(captions: [Caption], style: CaptionStyle, map: TimeMap, trim: (Double, Double), canvas: CGSize) {
        self.map = map; self.style = style; cw = canvas.width; ch = canvas.height
        let lo = map.out(trim.0), hi = map.out(trim.1)
        items = style.show ? captions.compactMap { c in
            let t = c.text.trimmingCharacters(in: .whitespacesAndNewlines)
            let (s, e) = c.outRange(map)
            let a = max(s, lo), b = min(e, hi)
            return t.isEmpty || b - a < 0.05 ? nil : Item(start: a, end: b, text: t)
        }.sorted { $0.start < $1.start } : []
    }
    var isEmpty: Bool { items.isEmpty }
    var keys: KeyLayer?                       // 快捷键键帽：和字幕一起叠上去
    var entries: [(start: Double, end: Double, text: String)] { items.map { ($0.start, $0.end, $0.text) } }

    // 成片时间 t 显示哪句：后开始的盖住先开始的
    func text(atOut t: Double) -> String? { items.last { t >= $0.start && t < $0.end }?.text }
    func image(atOut t: Double) -> CIImage? {
        let k = keys?.image(atOut: t)
        guard let cap = captionImage(atOut: t) else { return k }
        return k.map { $0.composited(over: cap) } ?? cap
    }
    private func captionImage(atOut t: Double) -> CIImage? {
        guard let s = text(atOut: t), let img = Self.render(s, style: style, cw: cw, ch: ch) else { return nil }
        let o = place(w: Double(img.width), h: Double(img.height))
        return CIImage(cgImage: img).transformed(by: CGAffineTransform(translationX: o.x, y: o.y))
    }
    // 字幕图左下角放哪：拖过就按拖到的中心；不然水平居中，放底部时离下边 edge，放顶部时离上边 edge（Core Image 左下为原点）
    private func place(w: Double, h: Double) -> CGPoint {
        if let x = style.cx, let y = style.cy {
            return CGPoint(x: (x * cw - w / 2).rounded(), y: ((1 - y) * ch - h / 2).rounded())
        }
        let e = style.edge(canvas: cw, ch)
        let y = style.position == .bottom ? e - h * 0.2 : ch - e - h * 0.8
        return CGPoint(x: ((cw - w) / 2).rounded(), y: y.rounded())
    }
    // 这一刻显示的那句字幕占画布哪块（预览上点选、拖动用）
    func rect(atSource t: Double) -> CGRect? {
        guard !map.inCut(t), let s = text(atOut: map.out(t)), let img = Self.render(s, style: style, cw: cw, ch: ch) else { return nil }
        let w = Double(img.width), h = Double(img.height), o = place(w: w, h: h)
        return CGRect(x: o.x, y: o.y, width: w, height: h)
    }
    func image(atSource t: Double) -> CIImage? { map.inCut(t) ? nil : image(atOut: map.out(t)) }
    // 叠到画面上（玻璃底要先把字幕后面那块画面做成透镜，所以要拿到画面）
    func compose(_ img: CIImage, atOut t: Double) -> CIImage {
        guard let c = image(atOut: t) else { return img }
        var base = img
        if style.box == .glass, let s = text(atOut: t), let cg = Self.render(s, style: style, cw: cw, ch: ch) {
            let w = Double(cg.width), h = Double(cg.height), o = place(w: w, h: h)
            let px = style.fontPx(canvas: cw, ch)
            base = OverlayLayer.lens(img, rect: CGRect(x: o.x, y: o.y, width: w, height: h), radius: px * 0.45, strength: 0.9, dark: true, alpha: 1)
        }
        return c.composited(over: base)
    }
    func compose(_ img: CIImage, atSource t: Double) -> CIImage { map.inCut(t) ? (keys?.image(atOut: map.out(t)).map { $0.composited(over: img) } ?? img) : compose(img, atOut: map.out(t)) }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: CGImage] = [:]

    // 画一行字：只一行，太长就把字缩小到放得下（不换行）
    static func render(_ text: String, style: CaptionStyle, cw: Double, ch: Double) -> CGImage? {
        let key = "\(text)|\(style.weight)|\(style.size)|\(style.color.r),\(style.color.g),\(style.color.b)|\(style.outline)|\(style.shadow)|\(style.box)|\(Int(cw))x\(Int(ch))"
        lock.lock(); if let c = cache[key] { lock.unlock(); return c }; lock.unlock()
        var px = style.fontPx(canvas: cw, ch)
        func line(_ px: Double, stroke: Bool) -> CTLine {
            let font = CTFontCreateWithName(style.fontName as CFString, px, nil)
            var a: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: style.color.cg]
            if stroke { a[NSAttributedString.Key(kCTStrokeWidthAttributeName as String)] = 14.0; a[NSAttributedString.Key(kCTStrokeColorAttributeName as String)] = CGColor(gray: 0, alpha: 0.85) }
            return CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: a))
        }
        // 连左右留白一起不超过画布宽的 90%
        // 有底时（深色 / 浅色 / 玻璃）照宣传片的字幕条：左右留白宽、圆角大、字上不描黑边
        let padK = style.box == .none ? 0.25 : 0.8
        var w = CTLineGetTypographicBounds(line(px, stroke: false), nil, nil, nil)
        let maxW = cw * 0.9
        if w + 2 * padK * px > maxW { px *= maxW / (w + 2 * padK * px) }
        let fill = line(px, stroke: false)
        var asc: CGFloat = 0, desc: CGFloat = 0
        w = CTLineGetTypographicBounds(fill, &asc, &desc, nil)
        let padX = px * padK, padY = style.box == .none ? px * 0.22 : px * 0.34
        let W = Int((w + padX * 2).rounded(.up)), H = Int((asc + desc + padY * 2).rounded(.up))
        guard W > 0, H > 0, let c = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0,
                                             space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        if style.box != .none {
            let r = CGRect(x: 0, y: 0, width: W, height: H), rad = px * 0.45
            let path = CGPath(roundedRect: r, cornerWidth: rad, cornerHeight: rad, transform: nil)
            switch style.box {
            case .dark: c.addPath(path); c.setFillColor(CGColor(srgbRed: 18 / 255, green: 18 / 255, blue: 22 / 255, alpha: 0.74)); c.fillPath()
            case .light: c.addPath(path); c.setFillColor(CGColor(gray: 1, alpha: 0.88)); c.fillPath()
            case .glass:   // 后面的画面在 compose 里做成透镜；这里压一层很淡的暗色，白字才看得清
                c.addPath(path); c.setFillColor(CGColor(gray: 0.05, alpha: 0.28)); c.fillPath()
            case .none: break
            }
        }
        let plainText = style.box != .none   // 有底就不描黑边、不加阴影（宣传片那种干净的字幕条）
        let origin = CGPoint(x: padX, y: padY + desc)
        c.setLineJoin(.round)
        if style.shadow && !plainText { c.setShadow(offset: CGSize(width: 0, height: -px * 0.04), blur: px * 0.16, color: CGColor(gray: 0, alpha: 0.55)) }
        if style.outline && !plainText {
            c.textPosition = origin; CTLineDraw(line(px, stroke: true), c)
            c.setShadow(offset: .zero, blur: 0, color: nil)   // 描边已经带了阴影，正文不再叠一层
        }
        c.textPosition = origin; CTLineDraw(fill, c)
        guard let img = c.makeImage() else { return nil }
        lock.lock(); if cache.count > 400 { cache.removeAll() }; cache[key] = img; lock.unlock()
        return img
    }
}

extension RGB { var cg: CGColor { CGColor(srgbRed: r, green: g, blue: b, alpha: 1) } }

// ---- 识别出的一个个字（或词）→ 一句句字幕：按标点、停顿断句，太长的按最大停顿拆，一次只显示一行 ----
enum CaptionSplit {
    struct Token: Equatable { var text: String; var start: Double; var end: Double }   // 成片时间
    static let breakPunct: Set<Character> = ["，", "。", "！", "？", "；", "、", "：", ",", ".", "!", "?", ";", ":", "…"]
    static let keepPunct: Set<Character> = ["？", "！", "?", "!"]   // 问号、感叹号留着，别的标点在字幕里去掉

    // 一个字算几格：中文 1，英文数字 0.55
    static func width(_ s: String) -> Double { s.reduce(0) { $0 + ($1.isASCII ? 0.55 : 1) } }

    static func make(_ tokens: [Token], maxChars: Double, pause: Double = 0.6) -> [Caption] {
        // 1) 按标点和停顿分成小句
        var clauses: [[Token]] = [], cur: [Token] = []
        func flush() { if !cur.isEmpty { clauses.append(cur); cur = [] } }
        for var t in tokens.sorted(by: { $0.start < $1.start }) {
            let s = t.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !s.isEmpty else { continue }
            // 识别器给的头一个字有时把前面的静音也算进去：一个字最长按 0.45 秒（英文单词按长度放宽）
            let core0 = s.filter { !breakPunct.contains($0) }
            if !core0.isEmpty { t.start = max(t.start, t.end - 0.45 * max(1, width(core0))) }
            if let last = cur.last, t.start - last.end > pause { flush() }
            let core = s.filter { !breakPunct.contains($0) }
            if !core.isEmpty { cur.append(Token(text: core, start: t.start, end: t.end)) }
            if let ch = s.last, breakPunct.contains(ch) {
                if keepPunct.contains(ch), !cur.isEmpty { cur[cur.count - 1].text += String(ch) }
                flush()
            }
        }
        flush()
        // 1.5) 识别结果常常没有标点：小句偏长时，在里面明显的停顿（≥ 0.25 秒）处再断开，两边都至少 4 个字
        func byPause(_ c: [Token]) -> [[Token]] {
            guard width(c.map(\.text).joined()) > maxChars * 0.6, c.count > 1 else { return [c] }
            var best = -1, gap = 0.25
            for k in 1..<c.count {
                let g = c[k].start - c[k - 1].end
                guard g >= gap, width(c[..<k].map(\.text).joined()) >= 4, width(c[k...].map(\.text).joined()) >= 4 else { continue }
                best = k; gap = g
            }
            return best < 0 ? [c] : byPause(Array(c[..<best])) + byPause(Array(c[best...]))
        }
        clauses = clauses.flatMap(byPause)
        // 2) 太长的小句拆开：在理想位置附近找停顿最大的地方断
        var lines: [[Token]] = []
        for c in clauses {
            var rest = c
            while width(rest.map(\.text).joined()) > maxChars, rest.count > 1 {
                let total = width(rest.map(\.text).joined()), parts = (total / maxChars).rounded(.up)
                let target = total / parts
                var acc = 0.0, ideal = 1
                for (i, t) in rest.enumerated() { acc += width(t.text); if acc >= target { ideal = i + 1; break } }
                ideal = min(max(1, ideal), rest.count - 1)
                // 理想位置前后 3 个字里挑停顿最长的（不超过一行）
                var best = ideal, bestGap = -1.0
                for k in max(1, ideal - 3)...min(rest.count - 1, ideal + 3) {
                    guard width(rest[..<k].map(\.text).joined()) <= maxChars else { continue }
                    let gap = rest[k].start - rest[k - 1].end
                    if gap > bestGap + 0.02 || (abs(gap - bestGap) <= 0.02 && abs(k - ideal) < abs(best - ideal)) { best = k; bestGap = gap }
                }
                lines.append(Array(rest[..<best])); rest = Array(rest[best...])
            }
            lines.append(rest)
        }
        // 3) 时间：首字开始到末字结束；末尾多留一点（不压到下一句），太短的拉到 0.7 秒
        var out: [(Double, Double, String)] = lines.map { ($0.first!.start, $0.last!.end, $0.map(\.text).joined()) }
        for i in out.indices {
            let next = i + 1 < out.count ? out[i + 1].0 : .infinity
            var e = min(out[i].1 + 0.35, next)
            if e - out[i].0 < 0.7 { e = min(out[i].0 + 0.7, next) }
            out[i].1 = max(e, out[i].1)
        }
        return out.map { Caption(start: $0.0, end: $0.1, text: $0.2) }
    }
}

// ---- 本机语音识别（普通话）：macOS 26 用 SpeechAnalyzer，老系统用 SFSpeechRecognizer（只在本机） ----
enum Transcribe {
    struct Word { var text: String; var start: Double; var end: Double }   // 音频里的时间
    enum Stage: Equatable { case preparing, downloading(Double), recognizing(Double) }

    static let locale = Locale(identifier: "zh-CN")

    static func run(_ url: URL, duration: Double, stage: @escaping @Sendable (Stage) -> Void) async throws -> [Word] {
        if #available(macOS 26, *), let loc = await SpeechTranscriber.supportedLocale(equivalentTo: locale) {
            return try await analyzer(url, locale: loc, duration: duration, stage: stage)
        }
        return try await legacy(url, stage: stage)
    }

    @available(macOS 26, *)
    private static func analyzer(_ url: URL, locale: Locale, duration: Double, stage: @escaping @Sendable (Stage) -> Void) async throws -> [Word] {
        stage(.preparing)
        let tr = SpeechTranscriber(locale: locale, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange])
        // 第一次用：系统下载普通话识别资源（几十 MB），显示进度
        if let req = try await AssetInventory.assetInstallationRequest(supporting: [tr]) {
            let p = req.progress
            let watch = Task { while !Task.isCancelled { stage(.downloading(p.fractionCompleted)); try? await Task.sleep(for: .seconds(0.2)) } }
            defer { watch.cancel() }
            try await req.downloadAndInstall()
        }
        stage(.recognizing(0))
        let an = SpeechAnalyzer(modules: [tr])
        let file = try AVAudioFile(forReading: url)
        let collect = Task { () -> [Word] in
            var out: [Word] = []
            for try await r in tr.results {
                for run in r.text.runs {
                    guard let rng = run.audioTimeRange else { continue }
                    out.append(Word(text: String(r.text[run.range].characters), start: rng.start.seconds, end: rng.end.seconds))
                }
                if duration > 0 { stage(.recognizing(min(0.99, r.range.end.seconds / duration))) }
            }
            return out
        }
        do {
            try await withTaskCancellationHandler {
                if let end = try await an.analyzeSequence(from: file) { try await an.finalizeAndFinish(through: end) } else { await an.cancelAndFinishNow() }
            } onCancel: { Task { await an.cancelAndFinishNow() } }
        } catch { collect.cancel(); throw error }
        try Task.checkCancellation()
        return try await collect.value
    }

    private static func legacy(_ url: URL, stage: @escaping @Sendable (Stage) -> Void) async throws -> [Word] {
        guard let rec = SFSpeechRecognizer(locale: locale), rec.supportsOnDeviceRecognition else {
            throw Studio.failure(L("这台 Mac 不支持在本机识别普通话", "This Mac can't recognize Mandarin on-device"))
        }
        let auth = await withCheckedContinuation { c in SFSpeechRecognizer.requestAuthorization { c.resume(returning: $0) } }
        guard auth == .authorized else {
            throw Studio.failure(L("没有语音识别权限：到「系统设置 → 隐私与安全性 → 语音识别」里打开「录屏」", "No speech recognition access: allow Screen Recorder under System Settings → Privacy & Security → Speech Recognition"))
        }
        stage(.recognizing(0))
        let req = SFSpeechURLRecognitionRequest(url: url)
        req.requiresOnDeviceRecognition = true
        req.shouldReportPartialResults = false
        req.addsPunctuation = true
        final class Box: @unchecked Sendable { var task: SFSpeechRecognitionTask?; var done = false }
        let box = Box()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<[Word], Error>) in
                box.task = rec.recognitionTask(with: req) { r, e in
                    if box.done { return }
                    if let e { box.done = true; c.resume(throwing: e); return }
                    guard let r, r.isFinal else { return }
                    box.done = true
                    c.resume(returning: r.bestTranscription.segments.map { Word(text: $0.substring, start: $0.timestamp, end: $0.timestamp + $0.duration) })
                }
            }
        } onCancel: { box.task?.cancel() }
    }
}

// ---- 识别哪段声音：录到的声音（电脑声音 + 麦克风混成一条）/ 后来录的讲解 / 两样都要；出来的字换到成片时间 ----
enum CaptionMaker {
    enum Source: String, CaseIterable { case recorded, voice, both }

    // 素材里录到的声音混成单声道 16k 的 caf（识别用）
    static func recordedAudio(_ dir: URL, to out: URL) async throws -> Double {
        let asset = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        let tracks = try await asset.loadTracks(withMediaType: .audio)
        guard !tracks.isEmpty else { return 0 }
        let r = try AVAssetReader(asset: asset)
        let o = AVAssetReaderAudioMixOutput(audioTracks: tracks, audioSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 16000, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
        ])
        r.add(o)
        guard r.startReading() else { throw r.error ?? Studio.failure("audio") }
        let fmt = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false)!
        try? FileManager.default.removeItem(at: out)
        let f = try AVAudioFile(forWriting: out, settings: fmt.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        // 按每块声音自己的时间戳写：音轨比画面晚开始（麦克风常晚几十毫秒到一两秒）、中间断过的，补上静音，
        // 识别出来的时间才是素材时间
        var n = 0
        func silence(_ k: Int) throws {
            var left = k
            while left > 0, let z = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(min(left, 16000))) {
                z.frameLength = z.frameCapacity
                memset(z.floatChannelData![0], 0, Int(z.frameLength) * 4)
                try f.write(from: z); left -= Int(z.frameLength)
            }
        }
        while let sb = o.copyNextSampleBuffer() {
            try Task.checkCancellation()
            guard let block = CMSampleBufferGetDataBuffer(sb) else { continue }
            let k = CMBlockBufferGetDataLength(block) / 4
            guard k > 0, let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(k)) else { continue }
            let at = Int((CMSampleBufferGetPresentationTimeStamp(sb).seconds * 16000).rounded())
            if at - n > 160 { try silence(at - n); n = at }   // 差 10 毫秒以上才补
            buf.frameLength = AVAudioFrameCount(k)
            _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: k * 4, destination: buf.floatChannelData![0])
            try f.write(from: buf); n += k
        }
        return Double(n) / 16000
    }

    // 识别 → 成片时间的字：录到的声音按素材时间（剪掉段里的、修剪外的不要）；讲解按它在成片里的位置
    static func tokens(dir: URL, project p: StudioProject, source: Source, stage: @escaping @Sendable (String, Double?) -> Void) async throws -> [CaptionSplit.Token] {
        let m = TimeMap(p), tmp = FileManager.default.temporaryDirectory
        var out: [CaptionSplit.Token] = []
        func progress(_ name: String, _ part: Int, _ parts: Int) -> @Sendable (Transcribe.Stage) -> Void {
            { s in
                switch s {
                case .preparing: stage(L("准备识别…", "Preparing…"), nil)
                case .downloading(let f): stage(L("第一次用：下载普通话识别资源 \(Int(f * 100))%", "First use: downloading Mandarin model \(Int(f * 100))%"), f)
                case .recognizing(let f): stage(name, (Double(part) + f) / Double(parts))
                }
            }
        }
        var jobs: [(URL, Double, (Double) -> Double?)] = []   // 音频、时长、音频时间 → 成片时间（不要的给 nil）
        if source != .voice {
            let u = tmp.appendingPathComponent("captions-\(UUID().uuidString).caf")
            let d = try await recordedAudio(dir, to: u)
            if d > 0 {
                jobs.append((u, d, { t in t < p.trimStart || t > p.trimEnd || m.inCut(t) ? nil : m.out(t) }))
            }
        }
        if source != .recorded {
            for c in p.voiceovers {
                let o0 = m.out(c.start), u = dir.appendingPathComponent(c.file)
                jobs.append((u, c.fileLength, { t in t < c.trimIn || t > c.trimOut ? nil : o0 + (t - c.trimIn) }))
            }
        }
        guard !jobs.isEmpty else { throw Studio.failure(L("没有可以识别的声音", "There's no sound to transcribe")) }
        for (k, j) in jobs.enumerated() {
            try Task.checkCancellation()
            let words = try await Transcribe.run(j.0, duration: j.1, stage: progress(L("正在识别说的话…", "Transcribing…"), k, jobs.count))
            for w in words {
                guard let s = j.2(w.start), let e = j.2(max(w.start, w.end)) else { continue }
                out.append(CaptionSplit.Token(text: w.text, start: s, end: max(s, e)))
            }
            if j.0.path.hasPrefix(tmp.path) { try? FileManager.default.removeItem(at: j.0) }
        }
        return out.sorted { $0.start < $1.start }
    }

    // 生成字幕（素材时间）：成片时间里断好句，再换回素材时间存
    static func make(dir: URL, project p: StudioProject, source: Source, canvas: CGSize, stage: @escaping @Sendable (String, Double?) -> Void) async throws -> [Caption] {
        let toks = try await tokens(dir: dir, project: p, source: source, stage: stage)
        let m = TimeMap(p)
        let caps = CaptionSplit.make(toks, maxChars: p.captionStyle.maxChars(canvas: canvas.width, canvas.height))
        return caps.map { var c = $0; c.start = m.src(c.start); c.end = m.src(c.end); return c }
    }
}

// ---- .srt（成片时间，从修剪起点算 0） ----
enum SRT {
    static func stamp(_ t: Double) -> String {
        let ms = Int((max(0, t) * 1000).rounded())
        return String(format: "%02d:%02d:%02d,%03d", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000)
    }
    static func text(_ p: StudioProject) -> String {
        let m = TimeMap(p), base = m.out(p.trimStart)
        let layer = CaptionLayer(captions: p.captions, style: { var s = p.captionStyle; s.show = true; return s }(), map: m,
                                 trim: (p.trimStart, p.trimEnd), canvas: CGSize(width: 1920, height: 1080))
        return layer.entries.enumerated().map { i, e in
            "\(i + 1)\n\(stamp(e.start - base)) --> \(stamp(e.end - base))\n\(e.text)\n"
        }.joined(separator: "\n")
    }
}

// ---- 自测 ----
// CAPTIONTEST=素材夹（/tmp 下的副本）[CAPTIONTEST_RAWAT=秒：raw.mp4 里这一刻起有一段已知的中文语音] →
// 断句规则、时间换算、.srt、旧工程兼容；再用 say 生成一段中文当讲解跑真识别、对时间；带剪掉再识别一遍；
// 导出（开 / 关字幕各一份）比对画面：字幕只在该出现的时刻、底部、只有一行
extension StudioTests {
    @MainActor static func captionTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["CAPTIONTEST"] else { return false }
        Task { @MainActor in
            var ok = true
            func check(_ n: String, _ c: Bool, _ info: String = "") { print(c ? "PASS" : "FAIL", n, info); ok = ok && c }
            func near(_ a: Double, _ b: Double, _ e: Double = 1e-6) -> Bool { abs(a - b) < e }
            // ---- 规则 ----
            let sent = "大家好，这是我自己做的录屏软件，录完以后可以在剪辑窗口里调整推近的位置，还可以加字幕自动把说的话变成文字。你觉得怎么样？"
            var toks: [CaptionSplit.Token] = []
            var t = 1.0
            for ch in sent { toks.append(.init(text: String(ch), start: t, end: t + 0.18)); t += 0.2 }
            let caps = CaptionSplit.make(toks, maxChars: 16)
            check("按标点断句、长句拆开、每行 ≤ 16 字", caps.count >= 5 && caps.allSatisfy { CaptionSplit.width($0.text) <= 16 }, caps.map(\.text).joined(separator: " | "))
            check("逗号句号去掉、问号留着", !caps.contains { $0.text.contains("，") || $0.text.contains("。") } && caps.last?.text.hasSuffix("？") == true)
            check("字没丢", caps.map(\.text).joined() == sent.filter { !"，。".contains($0) })
            check("时间：首句从第一个字开始、句与句不重叠", near(caps[0].start, 1.0) && zip(caps, caps.dropFirst()).allSatisfy { $0.end <= $1.start + 1e-9 })
            var gapT: [CaptionSplit.Token] = [.init(text: "你", start: 0, end: 0.2), .init(text: "好", start: 0.2, end: 0.4), .init(text: "再", start: 1.5, end: 1.7), .init(text: "见", start: 1.7, end: 1.9)]
            check("停顿超过 0.6 秒断开", CaptionSplit.make(gapT, maxChars: 16).map(\.text) == ["你好", "再见"])
            gapT[2].start = 0.5; gapT[2].end = 0.7; gapT[3].start = 0.7; gapT[3].end = 0.9
            check("太短的拉到 0.7 秒", CaptionSplit.make(gapT, maxChars: 16).first.map { $0.end - $0.start >= 0.7 - 1e-9 } == true)
            let st = CaptionStyle()
            check("一行字数随画布：横屏 18、竖屏更少", st.maxChars(canvas: 1660, 1080) == 18 && st.maxChars(canvas: 1080, 1920) < 16, "\(st.maxChars(canvas: 1660, 1080)) \(st.maxChars(canvas: 1080, 1920))")
            var pc = StudioProject(trimStart: 0, trimEnd: 30)
            pc.cuts = [CutSegment(start: 4, end: 6)]
            pc.captions = [Caption(start: 10, end: 12, text: "后面"), Caption(start: 4.2, end: 5.8, text: "被剪掉"), Caption(start: 1, end: 2, text: "前面")]
            let mc = TimeMap(pc)
            let layer = CaptionLayer(captions: pc.captions, style: st, map: mc, trim: (0, 30), canvas: CGSize(width: 1660, height: 1080))
            check("剪掉段前面的字幕：成片时间往前挪", layer.text(atOut: 8.5) == "后面" && layer.text(atOut: 10.5) == nil && layer.text(atOut: 1.5) == "前面")
            check("整句被剪掉的不出", !layer.entries.contains { $0.text == "被剪掉" })
            check("预览按素材时间查", layer.text(atOut: mc.out(11)) == "后面" && layer.image(atSource: 5) == nil && layer.image(atSource: 11) != nil)
            let srt = SRT.text(pc)
            check(".srt 两句、成片时间", srt.contains("1\n00:00:01,000 --> 00:00:02,000\n前面") && srt.contains("2\n00:00:08,000 --> 00:00:10,000\n后面") && !srt.contains("被剪掉"), srt)
            let r0 = 0.0...20.0
            let c0 = Caption(start: 5, end: 7, text: "x")
            check("拖两头夹在范围里、最短 0.3 秒", near(CaptionDrag.resize(c0, isStart: true, dt: 3, room: r0, minLen: 0.3).start, 6.7)
                  && near(CaptionDrag.resize(c0, isStart: false, dt: 20, room: 4...8, minLen: 0.3).end, 8))
            let old = #"{"segments":[],"trimStart":0,"trimEnd":10}"#.data(using: .utf8)!
            let po = try? JSONDecoder().decode(StudioProject.self, from: old)
            check("旧工程兼容", po?.captions == [] && po?.captionStyle == CaptionStyle())
            var pr = pc; pr.captionStyle.box = .dark; pr.captionStyle.position = .top
            let rt = try? JSONDecoder().decode(StudioProject.self, from: JSONEncoder().encode(pr))
            check("存了再读一样", rt?.captions == pr.captions && rt?.captionStyle == pr.captionStyle)
            let long = CaptionLayer.render(String(repeating: "很长的一句字幕", count: 8), style: st, cw: 1660, ch: 1080)
            check("太长的字缩小放进一行（不超出画布 90%）", long.map { Double($0.width) <= 1660 * 0.9 + 4 } == true, "\(long?.width ?? 0)")

            // ---- 真识别：讲解 ----
            let dir = URL(fileURLWithPath: path)
            let vdir = dir.appendingPathComponent("voice"); try? FileManager.default.createDirectory(at: vdir, withIntermediateDirectories: true)
            let aiff = vdir.appendingPathComponent("tts.aiff"), m4a = vdir.appendingPathComponent("tts.m4a")
            let say = Process(); say.executableURL = URL(fileURLWithPath: "/usr/bin/say")
            say.arguments = ["-v", "Tingting", "-o", aiff.path, "[[slnc 1200]]大家好，这是我自己做的录屏软件。[[slnc 500]]录完以后可以加字幕，自动把说的话变成文字。"]
            try? say.run(); say.waitUntilExit()
            let conv = Process(); conv.executableURL = URL(fileURLWithPath: "/usr/bin/afconvert")
            conv.arguments = ["-f", "m4af", "-d", "aac", aiff.path, m4a.path]
            try? conv.run(); conv.waitUntilExit()
            let pcm = (try? await VoiceMix.decode(m4a)) ?? []
            let thr: Float = 0.02
            let on0 = Double(pcm.firstIndex { abs($0) > thr } ?? 0) / 48000, on1 = Double(pcm.lastIndex { abs($0) > thr } ?? 0) / 48000
            let flen = Double(pcm.count) / 48000
            check("生成测试语音", flen > 3 && on1 > on0, String(format: "长 %.2f 秒、声音 %.2f–%.2f", flen, on0, on1))
            guard let track = try? JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json"))) else { print("CAPTIONTEST FAIL no track"); exit(1) }
            let dur = (try? await AVURLAsset(url: dir.appendingPathComponent("raw.mp4")).load(.duration).seconds) ?? 0
            var base = StudioProject.load(dir, track: track, duration: dur)
            base.trimStart = 0; base.trimEnd = dur; base.cuts = []; base.captions = []
            base.voiceovers = [VoiceClip(file: "voice/tts.m4a", start: 2.0, trimIn: 0, trimOut: flen, fileLength: flen)]
            let canvas = CGSize(width: 1660, height: 1080)
            func recog(_ p: StudioProject, _ src: CaptionMaker.Source) async -> [Caption] {
                let t0 = Date()
                do {
                    let c = try await CaptionMaker.make(dir: dir, project: p, source: src, canvas: canvas) { m, f in print("  …", m, f.map { String(format: "%.0f%%", $0 * 100) } ?? "") }
                    print(String(format: "  识别用时 %.1f 秒", Date().timeIntervalSince(t0)))
                    for x in c { print(String(format: "  %.2f–%.2f", x.start, x.end), x.text) }
                    return c
                } catch { print("  识别出错", error); return [] }
            }
            let vc = await recog(base, .voice)
            let all = vc.map(\.text).joined()
            check("讲解识别出文字", all.contains("录屏") && all.contains("字幕"), all)
            check("每句一行 ≤ 18 字", !vc.isEmpty && vc.allSatisfy { CaptionSplit.width($0.text) <= 18 })
            check("没有标点时按停顿断句（「软件」和「录完」分开）", vc.contains { $0.text.hasSuffix("软件") } && vc.contains { $0.text.hasPrefix("录完") }, vc.map(\.text).joined(separator: " | "))
            check("时间对齐：第一句开始 ≈ 讲解起点 + 语音起点", vc.first.map { abs($0.start - (2.0 + on0)) < 0.3 } == true, String(format: "%.2f vs %.2f", vc.first?.start ?? -1, 2.0 + on0))
            check("时间对齐：最后一句在语音结束附近结束", vc.last.map { $0.end > 2.0 + on1 - 0.3 && $0.end < 2.0 + on1 + 0.8 } == true, String(format: "%.2f vs %.2f", vc.last?.end ?? -1, 2.0 + on1))
            // 讲解前面剪掉 1 秒：讲解跟着往前挪，字幕的成片时间也往前 1 秒
            var bc = base; bc.cuts = [CutSegment(start: 0.5, end: 1.5)]
            let vc2 = await recog(bc, .voice)
            let mb = TimeMap(bc)
            check("剪掉后：成片时间第一句 ≈ 讲解成片起点 + 语音起点", vc2.first.map { abs(mb.out($0.start) - (1.0 + on0)) < 0.3 } == true,
                  String(format: "%.2f vs %.2f", vc2.first.map { mb.out($0.start) } ?? -1, 1.0 + on0))
            // ---- 真识别：录到的声音（raw.mp4 里已经混进了这段语音） ----
            if let at = ProcessInfo.processInfo.environment["CAPTIONTEST_RAWAT"].flatMap(Double.init) {
                var br = base; br.voiceovers = []
                let rc = await recog(br, .recorded)
                check("录到的声音识别出文字", rc.map(\.text).joined().contains("录屏"))
                check("录到的声音时间对齐", rc.first.map { abs($0.start - (at + on0)) < 0.3 } == true, String(format: "%.2f vs %.2f", rc.first?.start ?? -1, at + on0))
                // 语音中间剪掉 1 秒、后面快放 2 倍：剪掉段里的字不出，后面的按成片时间往前
                br.cuts = [CutSegment(start: at + on0 + 1.0, end: at + on0 + 2.0)]
                let rc2 = await recog(br, .recorded), mr = TimeMap(br)
                check("剪掉段里没有字幕", rc2.allSatisfy { !(mr.inCut($0.start) && mr.inCut($0.end)) } && rc2.map(\.text).joined().count < rc.map(\.text).joined().count,
                      "\(rc.map(\.text).joined().count) → \(rc2.map(\.text).joined().count)")
            }

            // ---- 导出：开 / 关字幕各一份，比画面 ----
            var ex = base
            ex.captions = vc
            ex.look.background = .color; ex.look.color = RGB(0.2, 0.3, 0.5); ex.look.blur = .off; ex.look.clickSound = false; ex.look.aspect = .wide
            ex.captionStyle = CaptionStyle()
            var off = ex; off.captionStyle.show = false
            var o = ExportOptions(); o.res = .hd; o.fps = 30; o.quality = .standard
            func export(_ p: StudioProject, _ name: String) async -> URL? {
                do { return try await Studio.render(project: dir, to: URL(fileURLWithPath: "/tmp/captiontest-\(name).mp4"), style: StudioStyle(o), edits: p) { _ in } }
                catch { print("export", name, error); return nil }
            }
            guard let uOn = await export(ex, "on"), let uOff = await export(off, "off"), let c1 = vc.first else { check("导出", false); print("CAPTIONTEST FAIL"); exit(1) }
            func frame(_ u: URL, _ t: Double) async -> CGImage? {
                let g = AVAssetImageGenerator(asset: AVURLAsset(url: u)); g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
                return try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)).image
            }
            func pixels(_ cg: CGImage) -> [UInt8] {
                var d = [UInt8](repeating: 0, count: cg.width * cg.height * 4)
                let c = CGContext(data: &d, width: cg.width, height: cg.height, bitsPerComponent: 8, bytesPerRow: cg.width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
                c.draw(cg, in: CGRect(x: 0, y: 0, width: cg.width, height: cg.height)); return d
            }
            // 两张图哪些行不一样（行号从上往下）
            func diffRows(_ a: CGImage, _ b: CGImage) -> [Int] {
                let pa = pixels(a), pb = pixels(b), w = a.width
                // 视频压缩每次都有点不一样：一行里超过 1% 的像素差得很多才算这行不一样
                return (0..<a.height).filter { y in
                    var k = 0
                    for x in 0..<w { let i = (y * w + x) * 4; if abs(Int(pa[i]) - Int(pb[i])) + abs(Int(pa[i + 1]) - Int(pb[i + 1])) > 120 { k += 1 } }
                    return k > w / 100
                }
            }
            let tMid = (c1.start + c1.end) / 2
            if let a = await frame(uOn, tMid), let b = await frame(uOff, tMid) {
                let rows = diffRows(a, b), h = Double(a.height)
                let px = CaptionStyle().fontPx(canvas: Double(a.width), h)
                let top = Double(rows.first ?? 0), bottom = Double(rows.last ?? 0)
                check("字幕出现在底部", !rows.isEmpty && top > h * 0.75, String(format: "行 %.0f–%.0f / %.0f", top, bottom, h))
                check("只有一行（字的高度 < 1.8 个字号）", !rows.isEmpty && bottom - top < px * 1.8, String(format: "高 %.0f，字号 %.0f", bottom - top, px))
                if let save = CGImageDestinationCreateWithURL(URL(fileURLWithPath: "/tmp/captiontest-frame.png") as CFURL, "public.png" as CFString, 1, nil) { CGImageDestinationAddImage(save, a, nil); CGImageDestinationFinalize(save) }
            } else { check("抽帧", false) }
            let tNone = max(0.3, (vc.first?.start ?? 2) - 1.0)
            if let a = await frame(uOn, tNone), let b = await frame(uOff, tNone) {
                check("没有字幕的时刻画面一样", diffRows(a, b).isEmpty, String(format: "%.2f 秒", tNone))
            }
            print(ok ? "CAPTIONTEST OK" : "CAPTIONTEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }
}
