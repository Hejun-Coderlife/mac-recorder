// Studio 效果（仿 Screen Studio）：录的时候画面里不录鼠标，另记一份鼠标轨迹；录完按轨迹自动放大点击、打字的地方，
// 镜头和鼠标都用弹簧曲线走得顺滑，再加透明背景、圆角、阴影，逐帧合成（Core Image 走显卡）导出成片
// 素材留在「影片/录屏/Studio 素材/<名字>/」：raw.mp4（没有鼠标的原始画面）、track.json（轨迹）、cursor-N.png（录到的指针图案），以后剪辑界面要用
import AppKit
import AVFoundation
import CoreImage
import VideoToolbox

// ---- 轨迹文件 track.json ----
struct Track: Codable {
    var width: Int                  // 画面内容像素（不含为凑偶数补的那 1 像素）
    var height: Int
    var pixelsPerPoint: Double      // Retina 屏 = 2
    var moves: [[Double]] = []      // [秒, x, y, 指针图案编号, 左键按着 1/0]；x、y 是画面像素，左上角为原点
    var clicks: [[Double]] = []     // [秒, x, y]
    var keys: [Double] = []         // 按键的时刻（秒）
    var cursors: [CursorShape] = []
}
// 指针图案：热点（点下去的那个点）和大小按「点」；arrow = 标准箭头，导出时换成矢量画的高清箭头
struct CursorShape: Codable { var file: String; var hotX: Double; var hotY: Double; var w: Double; var h: Double; var arrow: Bool }

// ---- 录制时记鼠标：每秒 120 次记位置和左右键，顺便从「距上次按键几秒」看出有没有在打字（不需要输入监控权限） ----
final class CursorTracker: @unchecked Sendable {
    private let queue = DispatchQueue(label: "recorder.cursor", qos: .userInteractive)
    private var timer: DispatchSourceTimer?
    private var shapeTimer: Timer?
    private let lock = NSLock()
    private var shape = 0                       // 当前指针图案编号：主线程写，采样线程读
    private var shapePNGs: [Data] = []          // 主线程
    private var shapes: [CursorShape] = []
    private var samples: [[Double]] = []        // 以下都在采样线程上
    private var clicks: [[Double]] = []
    private var keys: [Double] = []
    private var wasDown = false
    private var lastKeyAgo = 0.0
    private var area: CGRect?
    private var tick = 0
    private let areaOf: () -> CGRect?           // 录的范围（全局坐标，点，左上角为原点）；窗口会被拖动，所以每 0.1 秒重新问一次
    private let size: CGSize
    private let ppp: Double
    private let arrowPNG = CursorTracker.png(.arrow)

    init(size: CGSize, pixelsPerPoint: Double, area: @escaping () -> CGRect?) {
        self.size = size; ppp = pixelsPerPoint; areaOf = area
    }

    func start() {
        sampleShape()
        shapeTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in self?.sampleShape() }
        lastKeyAgo = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: .keyDown)
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .nanoseconds(1_000_000_000 / 120), leeway: .milliseconds(1))
        t.setEventHandler { [weak self] in self?.sample() }
        t.resume(); timer = t
    }
    func stop() {
        shapeTimer?.invalidate(); shapeTimer = nil
        timer?.cancel(); timer = nil
        queue.sync {}
    }

    private func sample() {
        let now = CMClockGetTime(CMClockGetHostTimeClock()).seconds   // 和录屏画面的时间戳同一个时钟
        if tick % 12 == 0 { area = areaOf() }
        tick += 1
        guard let a = area, a.width > 0, a.height > 0, let loc = CGEvent(source: nil)?.location else { return }
        let x = (loc.x - a.minX) / a.width * size.width, y = (loc.y - a.minY) / a.height * size.height
        let left = CGEventSource.buttonState(.combinedSessionState, button: .left)
        let down = left || CGEventSource.buttonState(.combinedSessionState, button: .right)
        lock.lock(); let s = shape; lock.unlock()
        samples.append([now, x, y, Double(s), left ? 1 : 0])
        if down && !wasDown { clicks.append([now, x, y]) }
        wasDown = down
        let ago = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: .keyDown)
        if ago < lastKeyAgo - 0.001 { keys.append(now - ago) }   // 「距上次按键」变短了 = 又按了一下
        lastKeyAgo = ago
    }

    // 系统当前的指针长什么样（箭头、手、文字光标…）：每种存一张图，编号记进轨迹
    private func sampleShape() {
        guard let c = NSCursor.currentSystem, let png = Self.png(c) else { return }
        let i: Int
        if let found = shapePNGs.firstIndex(of: png) { i = found } else {
            i = shapePNGs.count; shapePNGs.append(png)
            shapes.append(CursorShape(file: "cursor-\(i).png", hotX: c.hotSpot.x, hotY: c.hotSpot.y,
                                      w: c.image.size.width, h: c.image.size.height, arrow: png == arrowPNG))
        }
        lock.lock(); shape = i; lock.unlock()
    }
    static func png(_ c: NSCursor) -> Data? {
        let img = c.image, w = Int(img.size.width * 2), h = Int(img.size.height * 2)
        guard w > 0, h > 0, let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4,
                                                       hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        rep.size = img.size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        img.draw(in: CGRect(origin: .zero, size: img.size))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }

    // 写 track.json 和指针图案；start = 录屏第一帧的时间（同一时钟），轨迹时间都换成「离开头几秒」
    func save(to dir: URL, start: Double) throws {
        func r(_ v: Double, _ k: Double = 100) -> Double { (v * k).rounded() / k }
        var t = Track(width: Int(size.width), height: Int(size.height), pixelsPerPoint: ppp)
        t.moves = samples.map { [r($0[0] - start, 1000), r($0[1], 10), r($0[2], 10), $0[3], $0[4]] }
        t.clicks = clicks.map { [r($0[0] - start, 1000), r($0[1], 10), r($0[2], 10)] }
        t.keys = keys.map { r($0 - start, 1000) }
        t.cursors = shapes
        for (i, png) in shapePNGs.enumerated() { try png.write(to: dir.appendingPathComponent(shapes[i].file)) }
        let enc = JSONEncoder(); enc.outputFormatting = [.sortedKeys]
        try enc.encode(t).write(to: dir.appendingPathComponent("track.json"))
    }
}

// ---- 成片的样子 ----
struct StudioStyle {
    var zoom = 2.0                 // 放大时镜头推近几倍
    var padding = 0.06             // 画面四周留给背景的边（占画布短边）
    var cursorScale = 1.5          // 鼠标画大一点
    var fps = 60
    var motionBlur = false         // 镜头动时加运动模糊：实测会把字糊掉（清晰度只剩 2~3 成），默认关
    var bitsPerPixel = 0.12        // 码率：每像素每帧多少比特
    var aspect: Double? = nil      // 画布宽高比；nil = 跟原画面一样
}

// 画布和画面在画布上的位置（Core Image 坐标：左下角为原点、y 朝上）
struct StudioLayout {
    let W: Double, H: Double       // 原画面像素
    let cw: Double, ch: Double     // 画布像素
    let s: Double                  // 原画面缩放到画布上的比例（尽量 1:1，字不糊）
    let card: CGRect               // 画面（圆角卡片）在画布上的位置
    let radius: Double

    init(track: Track, style: StudioStyle) {
        W = Double(track.width); H = Double(track.height)
        let f = style.padding, a = style.aspect ?? W / H
        // 先按「画面 1:1 放得下、四周留 f 的边」算画布
        var cw: Double, ch: Double
        if a >= 1 { ch = max(H / (1 - 2 * f), W / (a - 2 * f)); cw = ch * a }
        else { cw = max(W / (1 - 2 * f), H / (1 / a - 2 * f)); ch = cw / a }
        // H.264 硬件编码上限：边长 4096、约 890 万像素；超了整体缩小
        let k = min(1, 4096 / max(cw, ch), (8_800_000 / (cw * ch)).squareRoot())
        cw = (cw * k / 2).rounded(.down) * 2; ch = (ch * k / 2).rounded(.down) * 2
        self.cw = cw; self.ch = ch
        let pad = f * min(cw, ch)
        s = min((cw - 2 * pad) / W, (ch - 2 * pad) / H)
        card = CGRect(x: ((cw - W * s) / 2).rounded(), y: ((ch - H * s) / 2).rounded(), width: W * s, height: H * s)
        radius = max(8, card.width * 0.011)
    }
    var canvas: CGRect { CGRect(x: 0, y: 0, width: cw, height: ch) }
    var toScene: CGAffineTransform { CGAffineTransform(a: s, b: 0, c: 0, d: s, tx: card.minX, ty: card.minY) }
}

// 临界阻尼弹簧：往目标走，起步快、到位前慢下来，不过冲（精确解，步长多大都稳）
struct Spring {
    var x: Double, v = 0.0
    let w: Double                  // 越大越快；大约 4/w 秒到位
    mutating func step(to g: Double, dt: Double) {
        let y = x - g, e = exp(-w * dt), k = v + w * y
        x = g + (y + k * dt) * e
        v = (v - w * k * dt) * e
    }
}

// 每一帧的镜头和鼠标
struct FramePlan {
    var z = 1.0                    // 镜头放大倍数（1 = 全景）
    var sx = 0.0, sy = 0.0         // 镜头对准的画布坐标
    var mx = 0.0, my = 0.0         // 鼠标在原画面上的位置（y 朝上）
    var shape = -1                 // 指针图案；-1 = 不画
    var press = 1.0                // 点击时指针缩一下
}

enum StudioPlan {
    typealias Event = (t: Double, x: Double, y: Double)

    static func make(_ tr: Track, _ L: StudioLayout, _ st: StudioStyle, frames n: Int, duration: Double) -> [FramePlan] {
        let W = L.W, H = L.H, dt = 1 / Double(st.fps)
        let ts = tr.moves.map { $0[0] }
        // 鼠标在 t 秒的位置（前后两次采样之间插值），y 换成朝上
        func mouse(_ t: Double) -> (x: Double, y: Double, shape: Int)? {
            guard !ts.isEmpty else { return nil }
            var lo = 0, hi = ts.count
            while lo < hi { let m = (lo + hi) / 2; if ts[m] <= t { lo = m + 1 } else { hi = m } }
            let b = tr.moves[min(lo, ts.count - 1)], a = tr.moves[max(lo - 1, 0)]
            let u = b[0] > a[0] ? min(1, max(0, (t - a[0]) / (b[0] - a[0]))) : 0
            return (a[1] + (b[1] - a[1]) * u, H - (a[2] + (b[2] - a[2]) * u), Int(a[3]))
        }

        // 1) 值得放大的时刻：点击、打字、按住拖动（每 0.3 秒算一次）；最后 1 秒不算（多半是去点「停止」或按快捷键）
        var ev: [Event] = tr.clicks.map { ($0[0], $0[1], H - $0[2]) }
        for k in tr.keys { if let m = mouse(k) { ev.append((k, m.x, m.y)) } }
        var lastDrag = -1.0
        for m in tr.moves where m[4] == 1 && m[0] - lastDrag >= 0.3 { ev.append((m[0], m[1], H - m[2])); lastDrag = m[0] }
        ev = ev.filter { $0.t >= 0 && $0.t <= duration - 1 }.sorted { $0.t < $1.t }

        // 2) 合成放大段：事件前 0.6 秒开始推近，最后一个事件后停 3.5 秒；两段之间不到 1.5 秒就连成一段（别刚拉远又推近）
        struct Seg { var start: Double; var end: Double; var ev: [Event] }
        var segs: [Seg] = []
        for e in ev {
            let s = max(0, e.t - 0.6), end = e.t + 3.5
            if var last = segs.last, s <= last.end + 1.5 { last.end = max(last.end, end); last.ev.append(e); segs[segs.count - 1] = last }
            else { segs.append(Seg(start: s, end: end, ev: [e])) }
        }

        // 3) 逐帧：定目标，弹簧去追
        let Z = st.zoom
        let hw = L.cw / (2 * Z * L.s), hh = L.ch / (2 * Z * L.s)   // 放大时看得到的原画面半宽、半高
        var lz = Spring(x: 0, w: 4), cx = Spring(x: W / 2, w: 4), cy = Spring(x: H / 2, w: 4)
        var mx: Spring?, my: Spring?
        var si = 0, cur = -1
        var focus = (x: W / 2, y: H / 2)
        var out = [FramePlan](repeating: FramePlan(), count: n)
        for i in 0..<n {
            let t = Double(i) * dt
            var f = FramePlan()
            let m = mouse(t)
            if let m {
                if mx == nil { mx = Spring(x: m.x, w: 30); my = Spring(x: m.y, w: 30) }
                mx!.step(to: m.x, dt: dt); my!.step(to: m.y, dt: dt)
                f.mx = mx!.x; f.my = my!.x
                // 鼠标跑到画面外面（别的屏幕）就不画
                f.shape = f.mx > -40 && f.mx < W + 40 && f.my > -40 && f.my < H + 40 ? m.shape : -1
            }
            while si < segs.count && segs[si].end <= t { si += 1 }
            var tz = 1.0, tx = W / 2, ty = H / 2
            if si < segs.count && segs[si].start <= t {
                let seg = segs[si]
                if cur != si { cur = si; focus = (seg.ev[0].x, seg.ev[0].y) }
                // 半秒内要点的地方离镜头远：提前转过去（录完再算，所以能「预知」）
                if let e = seg.ev.first(where: { $0.t >= t && $0.t <= t + 0.5 }), abs(e.x - focus.x) > hw * 0.5 || abs(e.y - focus.y) > hh * 0.5 {
                    focus = (e.x, e.y)
                } else if let m, t >= seg.ev[0].t {
                    // 推近时镜头跟着鼠标走（像 Screen Studio）：鼠标离开中间一小块（25%）就跟，弹簧让它走得顺
                    let rx = hw * 0.25, ry = hh * 0.25
                    if m.x > focus.x + rx { focus.x = m.x - rx } else if m.x < focus.x - rx { focus.x = m.x + rx }
                    if m.y > focus.y + ry { focus.y = m.y - ry } else if m.y < focus.y - ry { focus.y = m.y + ry }
                }
                focus.x = 2 * hw >= W ? W / 2 : min(max(focus.x, hw), W - hw)
                focus.y = 2 * hh >= H ? H / 2 : min(max(focus.y, hh), H - hh)
                tz = Z; tx = focus.x; ty = focus.y
            }
            lz.step(to: log(tz), dt: dt); cx.step(to: tx, dt: dt); cy.step(to: ty, dt: dt)
            f.z = exp(lz.x)
            // 镜头框不能出画布
            let ex = L.cw / (2 * f.z), ey = L.ch / (2 * f.z)
            f.sx = min(max(L.card.minX + cx.x * L.s, ex), L.cw - ex)
            f.sy = min(max(L.card.minY + cy.x * L.s, ey), L.ch - ey)
            // 点下去时指针缩一下再弹回
            for c in tr.clicks where t >= c[0] && t < c[0] + 0.25 { f.press = 1 - 0.18 * sin(.pi * (t - c[0]) / 0.25) }
            out[i] = f
        }
        return out
    }
}

// ---- 导出 ----
enum Studio {
    static let failure = { (s: String) in NSError(domain: "Studio", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }

    static func render(project dir: URL, to out: URL, style: StudioStyle = StudioStyle(), progress: @escaping @Sendable (Double) -> Void) async throws {
        let job = try await StudioJob(project: dir, out: out, style: style, progress: progress)
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                do { try job.run(); c.resume() } catch { c.resume(throwing: error) }
            }
        }
    }

    // 自测：STUDIO=素材文件夹 [STUDIO_OUT=输出路径] [STUDIO_ASPECT=16:9] → 只导出，不录，打印用时后退出
    @MainActor static func selfTest() -> Bool {
        let env = ProcessInfo.processInfo.environment
        guard let p = env["STUDIO"] else { return false }
        let dir = URL(fileURLWithPath: p)
        let out = URL(fileURLWithPath: env["STUDIO_OUT"] ?? dir.appendingPathComponent("studio.mov").path)
        var st = StudioStyle()
        if env["STUDIO_BLUR"] != nil { st.motionBlur = true }
        if let z = env["STUDIO_ZOOM"].flatMap(Double.init) { st.zoom = z }
        if let b = env["STUDIO_BPP"].flatMap(Double.init) { st.bitsPerPixel = b }
        if let a = env["STUDIO_ASPECT"]?.split(separator: ":").compactMap({ Double($0) }), a.count == 2 { st.aspect = a[0] / a[1] }
        Task {
            let t0 = Date()
            do { try await render(project: dir, to: out, style: st) { _ in }; print("STUDIO OK", String(format: "%.1fs", Date().timeIntervalSince(t0)), out.path) }
            catch { print("STUDIO FAIL", error) }
            NSApp.terminate(nil)
        }
        return true
    }
}

final class StudioJob: @unchecked Sendable {
    private let asset: AVURLAsset
    private let videoTrack: AVAssetTrack
    private let audioTracks: [AVAssetTrack]
    private let out: URL
    private let track: Track
    private let lay: StudioLayout
    private let st: StudioStyle
    private let plan: [FramePlan]
    private let clicks: [(t: Double, x: Double, y: Double)]
    private let progress: @Sendable (Double) -> Void
    private let ctx: CIContext
    private var cursorArt: [Int: (img: CIImage, ppp: Double, hotX: Double, hotY: Double)] = [:]
    private let arrow: (img: CIImage, ppp: Double, hotX: Double, hotY: Double)
    private let ring: CIImage
    private var background: CIImage!
    private var mask: CIImage!

    init(project dir: URL, out: URL, style: StudioStyle, progress: @escaping @Sendable (Double) -> Void) async throws {
        track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
        asset = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        guard let vt = try await asset.loadTracks(withMediaType: .video).first else { throw Studio.failure(L("素材里没有画面", "No video in the recording")) }
        videoTrack = vt
        audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let duration = try await asset.load(.duration).seconds
        self.out = out; st = style; self.progress = progress
        lay = StudioLayout(track: track, style: style)
        plan = StudioPlan.make(track, lay, style, frames: max(1, Int(duration * Double(style.fps))), duration: duration)
        let h = Double(track.height)
        clicks = track.clicks.map { ($0[0], $0[1], h - $0[2]) }
        // 不做色彩管理：原画面的数值原样搬过去，颜色和原录屏一致
        ctx = CIContext(mtlDevice: MTLCreateSystemDefaultDevice()!, options: [.workingColorSpace: NSNull(), .outputColorSpace: NSNull(), .workingFormat: CIFormat.RGBAh])
        arrow = Self.drawArrow()
        ring = Self.drawRing()
        for (i, c) in track.cursors.enumerated() where !c.arrow {
            if let img = CIImage(contentsOf: dir.appendingPathComponent(c.file)), c.w > 0 {
                cursorArt[i] = (img, img.extent.width / c.w, c.hotX, c.hotY)
            }
        }
        prepareBackground()
    }

    // 透明背景 + 卡片阴影：每帧都一样，先画好存成一张图
    private func prepareBackground() {
        let g = CIImage(color: .clear).cropped(to: lay.canvas)
        let shadow = rounded(lay.card.offsetBy(dx: 0, dy: -lay.ch * 0.012), lay.radius, CIColor(red: 0, green: 0, blue: 0, alpha: 0.45))
            .applyingGaussianBlur(sigma: lay.cw * 0.014)
        let bg = shadow.composited(over: g).cropped(to: lay.canvas)
        let cg = ctx.createCGImage(bg, from: lay.canvas, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))!
        background = CIImage(cgImage: cg)
        mask = rounded(lay.card, lay.radius, .white)
    }
    private func rounded(_ r: CGRect, _ radius: Double, _ color: CIColor) -> CIImage {
        CIFilter(name: "CIRoundedRectangleGenerator", parameters: ["inputExtent": CIVector(cgRect: r), "inputRadius": radius, "inputColor": color])!.outputImage!
    }

    func run() throws {
        let reader = try AVAssetReader(asset: asset)
        let vOut = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        vOut.alwaysCopiesSampleData = false
        reader.add(vOut)
        var aOut: AVAssetReaderAudioMixOutput?
        if !audioTracks.isEmpty {
            // 电脑声音、麦克风几条音轨在这里混成一条
            let o = AVAssetReaderAudioMixOutput(audioTracks: audioTracks, audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
            ])
            reader.add(o); aOut = o
        }

        try? FileManager.default.removeItem(at: out)
        let writer = try AVAssetWriter(outputURL: out, fileType: .mov)   // 背景透明：H.264/mp4 存不了透明，用带透明通道的 HEVC
        let w = Int(lay.cw), h = Int(lay.ch)
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.hevcWithAlpha, AVVideoWidthKey: w, AVVideoHeightKey: h,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: max(10_000_000, Int(Double(w * h * st.fps) * st.bitsPerPixel)),
                AVVideoExpectedSourceFrameRateKey: st.fps,
                AVVideoMaxKeyFrameIntervalKey: st.fps * 2,
                kVTCompressionPropertyKey_AlphaChannelMode as String: kVTAlphaChannelMode_PremultipliedAlpha,   // Core Image 出的是预乘透明
            ],
        ])
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vIn, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: w, kCVPixelBufferHeightKey as String: h,
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        writer.add(vIn)
        var aIn: AVAssetWriterInput?
        if aOut != nil {
            let a = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192_000,
            ])
            writer.add(a); aIn = a
        }
        guard reader.startReading() else { throw reader.error ?? Studio.failure("reader") }
        guard writer.startWriting() else { throw writer.error ?? Studio.failure("writer") }

        // 原画面是「有变化才有新帧」，成片固定每秒 60 帧：每帧用当时最新的那张原画面
        guard var cur = vOut.copyNextSampleBuffer() else { throw Studio.failure(L("素材里没有画面", "No video in the recording")) }
        var next = vOut.copyNextSampleBuffer()
        let t0 = cur.presentationTimeStamp
        writer.startSession(atSourceTime: t0)

        let group = DispatchGroup()
        var failure: Error?
        var i = 0
        let total = plan.count
        group.enter()
        var videoDone = false
        vIn.requestMediaDataWhenReady(on: DispatchQueue(label: "studio.video")) { [self] in
            while vIn.isReadyForMoreMediaData && !videoDone {
                if i >= total || failure != nil { videoDone = true; vIn.markAsFinished(); group.leave(); return }
                let pts = CMTimeAdd(t0, CMTime(value: CMTimeValue(i), timescale: CMTimeScale(st.fps)))
                while let nx = next, nx.presentationTimeStamp <= pts { cur = nx; next = vOut.copyNextSampleBuffer() }
                autoreleasepool {
                    guard let src = CMSampleBufferGetImageBuffer(cur), let pool = adaptor.pixelBufferPool else { failure = Studio.failure("frame"); return }
                    var pb: CVPixelBuffer?
                    CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
                    guard let pb else { failure = Studio.failure("pool"); return }
                    ctx.render(frame(i, src), to: pb, bounds: lay.canvas, colorSpace: nil)
                    if !adaptor.append(pb, withPresentationTime: pts) { failure = writer.error ?? Studio.failure("append") }
                }
                i += 1
                if i % 30 == 0 { progress(Double(i) / Double(total)) }
            }
        }
        if let aOut, let aIn {
            group.enter()
            var audioDone = false
            aIn.requestMediaDataWhenReady(on: DispatchQueue(label: "studio.audio")) {
                while aIn.isReadyForMoreMediaData && !audioDone {
                    guard let sb = aOut.copyNextSampleBuffer() else { audioDone = true; aIn.markAsFinished(); group.leave(); return }
                    aIn.append(sb)
                }
            }
        }
        group.wait()
        if let failure { reader.cancelReading(); writer.cancelWriting(); throw failure }
        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? Studio.failure("reader") }
        let sem = DispatchSemaphore(value: 0)
        writer.finishWriting { sem.signal() }
        sem.wait()
        guard writer.status == .completed else { throw writer.error ?? Studio.failure("finish") }
        progress(1)
    }

    // 合成一帧：背景 → 圆角画面 → 点击波纹 → 鼠标，整幅按镜头推拉，镜头动得快时加一点运动模糊
    private func frame(_ i: Int, _ px: CVPixelBuffer) -> CIImage {
        let f = plan[i], W = lay.W, H = lay.H
        var src = CIImage(cvPixelBuffer: px)
        let bh = src.extent.height   // 高度是奇数时底下补过 1 像素，切掉
        src = src.cropped(to: CGRect(x: 0, y: bh - H, width: W, height: H)).transformed(by: CGAffineTransform(translationX: 0, y: -(bh - H)))
        var scene = src.transformed(by: lay.toScene).applyingFilter("CIBlendWithAlphaMask", parameters: [
            kCIInputBackgroundImageKey: background!, kCIInputMaskImageKey: mask!,
        ])
        let t = Double(i) / Double(st.fps), ppp = track.pixelsPerPoint
        // 点击波纹：0.5 秒内从 6 点扩到 28 点、渐渐淡掉
        for c in clicks where t >= c.t && t < c.t + 0.5 {
            let u = (t - c.t) / 0.5, e = 1 - pow(1 - u, 3)
            let r = (6 + 22 * e) * ppp * st.cursorScale / 1.5, k = r / 52
            let img = ring.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: 1 - u)])
                .transformed(by: CGAffineTransform(translationX: -64, y: -64).concatenating(CGAffineTransform(scaleX: k, y: k))
                    .concatenating(CGAffineTransform(translationX: c.x, y: c.y)).concatenating(lay.toScene))
            scene = img.composited(over: scene)
        }
        if f.shape >= 0 {
            let art = track.cursors.indices.contains(f.shape) && !track.cursors[f.shape].arrow ? cursorArt[f.shape] ?? arrow : arrow
            let k = ppp * st.cursorScale * f.press / art.ppp
            let hx = art.hotX * art.ppp, hy = art.img.extent.height - art.hotY * art.ppp
            let img = art.img.transformed(by: CGAffineTransform(translationX: -hx, y: -hy).concatenating(CGAffineTransform(scaleX: k, y: k))
                .concatenating(CGAffineTransform(translationX: f.mx, y: f.my)).concatenating(lay.toScene))
            scene = img.composited(over: scene)
        }
        let cam = CGAffineTransform(a: f.z, b: 0, c: 0, d: f.z, tx: lay.cw / 2 - f.sx * f.z, ty: lay.ch / 2 - f.sy * f.z)
        var img = scene.transformed(by: cam).cropped(to: lay.canvas)
        if st.motionBlur, i > 0 {
            let p = plan[i - 1]
            let dx = (f.sx - p.sx) * f.z, dy = (f.sy - p.sy) * f.z, d = hypot(dx, dy), dz = abs(log(f.z / p.z))
            if d > 3 {
                img = img.clampedToExtent().applyingFilter("CIMotionBlur", parameters: [kCIInputRadiusKey: min(d * 0.15, 10), kCIInputAngleKey: atan2(dy, dx)]).cropped(to: lay.canvas)
            }
            if dz > 0.003 {
                img = img.clampedToExtent().applyingFilter("CIZoomBlur", parameters: [
                    kCIInputCenterKey: CIVector(x: lay.cw / 2, y: lay.ch / 2), kCIInputAmountKey: min(dz * lay.cw * 0.04, 6),
                ]).cropped(to: lay.canvas)
            }
        }
        return img
    }

    // 矢量画的 macOS 箭头（8 倍精度），放大推近也清楚
    private static func drawArrow() -> (img: CIImage, ppp: Double, hotX: Double, hotY: Double) {
        let k = 8.0, pad = 4.0, w = 12.0 + pad * 2, h = 19.0 + pad * 2
        let c = CGContext(data: nil, width: Int(w * k), height: Int(h * k), bitsPerComponent: 8, bytesPerRow: 0,
                          space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        c.scaleBy(x: k, y: k)
        c.translateBy(x: pad, y: h - pad); c.scaleBy(x: 1, y: -1)   // 下面按「尖在 (0,0)、y 朝下」画
        let path = CGMutablePath()
        // 形状照系统箭头量的（尖到尾约 15 点）
        path.addLines(between: [CGPoint(x: 0, y: 0), CGPoint(x: 0, y: 12.8), CGPoint(x: 3.0, y: 10.0), CGPoint(x: 5.2, y: 15.2),
                                CGPoint(x: 7.6, y: 14.2), CGPoint(x: 5.5, y: 9.3), CGPoint(x: 9.3, y: 9.3)])
        path.closeSubpath()
        c.setShadow(offset: CGSize(width: 0, height: -1.0 * k), blur: 2.5 * k, color: CGColor(gray: 0, alpha: 0.35))
        c.addPath(path); c.setLineWidth(2.2); c.setLineJoin(.round)
        c.setStrokeColor(CGColor(gray: 1, alpha: 1)); c.setFillColor(CGColor(gray: 1, alpha: 1))
        c.drawPath(using: .fillStroke)
        c.setShadow(offset: .zero, blur: 0, color: nil)
        c.addPath(path); c.setFillColor(CGColor(gray: 0, alpha: 1)); c.fillPath()
        return (CIImage(cgImage: c.makeImage()!), k, pad, pad)
    }

    // 点击波纹：白圈 + 一圈淡淡的深色描边，深浅背景上都看得见（128 像素，圆心 64、半径 52）
    private static func drawRing() -> CIImage {
        let c = CGContext(data: nil, width: 128, height: 128, bitsPerComponent: 8, bytesPerRow: 0,
                          space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let r = CGRect(x: 12, y: 12, width: 104, height: 104)
        c.setFillColor(CGColor(gray: 1, alpha: 0.18)); c.fillEllipse(in: r)
        c.setStrokeColor(CGColor(gray: 0, alpha: 0.25)); c.setLineWidth(10); c.strokeEllipse(in: r)
        c.setStrokeColor(CGColor(gray: 1, alpha: 0.95)); c.setLineWidth(6); c.strokeEllipse(in: r)
        return CIImage(cgImage: c.makeImage()!)
    }
}
