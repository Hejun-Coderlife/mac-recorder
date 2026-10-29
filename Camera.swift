// 摄像头画中画：录的时候屏幕上浮一个能拖动的摄像头小窗；Studio 模式下小窗不录进画面，摄像头单独录成 camera.mp4，
// 剪辑窗口里再按「摄像头」样式和「布局」轨（小窗 / 左右分屏 / 摄像头全屏 / 隐藏）叠到成片上，推近时人像不跟着放大
import AppKit
import AVFoundation
import CoreImage
import SwiftUI

// ---- track.json 里记的摄像头素材 ----
struct CameraTrack: Codable, Equatable {
    var file = "camera.mp4"
    var offset = 0.0               // camera.mp4 的第 0 秒 = raw.mp4 的第几秒（同一时钟对齐）
    var x = 0.86, y = 0.80         // 录的时候小窗中心在画面里的位置（比例，左上为原点）
    var size = 0.24                // 小窗直径 / 画面短边
    var round = true               // 圆形；false = 圆角方形
}

// ---- 剪辑窗口里的摄像头样式（project.json） ----
struct CameraSettings: Codable, Equatable {
    var show = true
    var round = true               // 圆形 / 圆角方形
    var size = 0.24                // 小窗大小：占画布短边的比例
    var x = 0.86, y = 0.80         // 小窗中心（画布里的比例，左上为原点）
    var border = 4.0               // 边框粗细（点，按画布短边 1080 折算）
    var borderColor = RGB(1, 1, 1)
    var shadow = 0.5               // 阴影 0–1
    var mirror = true              // 左右镜像（像照镜子）

    init() {}
    init(from t: CameraTrack) { x = t.x; y = t.y; size = t.size; round = t.round }
    enum CodingKeys: String, CodingKey { case show, round, size, x, y, border, borderColor, shadow, mirror }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = CameraSettings()
        func v<T: Decodable>(_ k: CodingKeys, _ def: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? def }
        show = v(.show, z.show); round = v(.round, z.round); size = v(.size, z.size); x = v(.x, z.x); y = v(.y, z.y)
        border = v(.border, z.border); borderColor = v(.borderColor, z.borderColor); shadow = v(.shadow, z.shadow); mirror = v(.mirror, z.mirror)
    }
}

// 布局轨上的一段（素材时间）：这段时间里摄像头怎么摆；不在任何一段里 = 小窗
struct CameraLayoutSeg: Codable, Equatable, Identifiable {
    enum Mode: String, Codable, CaseIterable { case pip, split, splitSwap, full, hidden }
    var id = UUID()
    var start: Double
    var end: Double
    var mode = Mode.full

    static func name(_ m: Mode) -> String {
        switch m {
        case .pip: L("小窗", "Bubble")
        case .split: L("左右分屏", "Side by side")
        case .splitSwap: L("分屏（人像在左）", "Side by side (camera left)")
        case .full: L("摄像头全屏", "Camera full")
        case .hidden: L("隐藏", "Hidden")
        }
    }
    static func icon(_ m: Mode) -> String {
        switch m {
        case .pip: "person.crop.circle"
        case .split: "rectangle.split.2x1"
        case .splitSwap: "rectangle.split.2x1.fill"
        case .full: "person.fill"
        case .hidden: "eye.slash"
        }
    }
}

// ---- 合成：每一帧在镜头推拉之后叠摄像头（素材时间查布局，切换时 0.4 秒平滑形变） ----
final class CameraLayer: @unchecked Sendable {
    let settings: CameraSettings
    private let segs: [CameraLayoutSeg]
    private let frames: CameraFrames
    private let offset: Double
    private let cw: Double, ch: Double
    private let background: CIImage       // 分屏时屏幕画面缩小，露出来的地方用成片背景
    static let transition = 0.4

    init?(dir: URL, track: Track, project p: StudioProject, canvas: CGSize, background: CIImage) {
        guard let ct = track.camera, let cs = p.camera, cs.show else { return nil }
        let url = dir.appendingPathComponent(ct.file)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        settings = cs
        segs = p.layouts.filter { $0.end > $0.start }.sorted { $0.start < $1.start }
        frames = CameraFrames(url: url)
        offset = ct.offset
        cw = canvas.width; ch = canvas.height
        self.background = background
    }

    // 某一时刻的布局（不在任何一段里 = 小窗）
    func mode(at t: Double) -> CameraLayoutSeg.Mode { segs.first { t >= $0.start && t < $0.end }?.mode ?? .pip }

    struct Geo { var screen: CGRect; var cam: CGRect; var radius: Double; var alpha: Double }

    // 小窗的位置大小（CI 坐标：左下为原点）
    func pipRect() -> CGRect { Self.pipRect(settings, cw: cw, ch: ch) }
    static func pipRect(_ s: CameraSettings, cw: Double, ch: Double) -> CGRect {
        let short = min(cw, ch), side = max(16, s.size * short), m = short * 0.02
        let cx = min(max(s.x * cw, side / 2 + m), cw - side / 2 - m)
        let cy = min(max((1 - s.y) * ch, side / 2 + m), ch - side / 2 - m)
        return CGRect(x: cx - side / 2, y: cy - side / 2, width: side, height: side)
    }

    func geo(_ m: CameraLayoutSeg.Mode) -> Geo {
        let canvas = CGRect(x: 0, y: 0, width: cw, height: ch), short = min(cw, ch)
        let pip = pipRect(), pr = settings.round ? pip.width / 2 : pip.width * 0.2
        switch m {
        case .pip: return Geo(screen: canvas, cam: pip, radius: pr, alpha: 1)
        case .hidden: return Geo(screen: canvas, cam: pip, radius: pr, alpha: 0)
        case .full: return Geo(screen: canvas, cam: canvas, radius: 0, alpha: 1)
        case .split, .splitSwap:
            // 屏幕占左边约 64%（按原比例缩进去、居中），人像占右边一条圆角竖卡；对调就左右换过来
            let mg = short * 0.04, camW = cw * 0.3
            let room = CGRect(x: mg, y: mg, width: cw - camW - mg * 3, height: ch - mg * 2)
            let k = min(room.width / cw, room.height / ch)
            var scr = CGRect(x: room.midX - cw * k / 2, y: room.midY - ch * k / 2, width: cw * k, height: ch * k)
            var cam = CGRect(x: cw - camW - mg, y: mg, width: camW, height: ch - mg * 2)
            if m == .splitSwap {
                scr.origin.x = cw - scr.maxX
                cam.origin.x = cw - cam.maxX
            }
            return Geo(screen: scr, cam: cam, radius: short * 0.03, alpha: 1)
        }
    }

    // t 时刻的几何：刚换布局的 0.4 秒里从上一种平滑变过去
    func geo(at t: Double) -> Geo {
        var changes: [(t: Double, from: CameraLayoutSeg.Mode, to: CameraLayoutSeg.Mode)] = []
        for (i, s) in segs.enumerated() {
            let before: CameraLayoutSeg.Mode = (i > 0 && segs[i - 1].end >= s.start - 1e-6) ? segs[i - 1].mode : .pip
            if before != s.mode { changes.append((s.start, before, s.mode)) }
            let after: CameraLayoutSeg.Mode = (i + 1 < segs.count && segs[i + 1].start <= s.end + 1e-6) ? segs[i + 1].mode : .pip
            if after == .pip && s.mode != .pip { changes.append((s.end, s.mode, .pip)) }
        }
        let cur = geo(mode(at: t))
        guard let c = changes.last(where: { t >= $0.t && t < $0.t + Self.transition }) else { return cur }
        let u = (t - c.t) / Self.transition, e = u < 0.5 ? 4 * u * u * u : 1 - pow(-2 * u + 2, 3) / 2   // 缓进缓出
        return Self.lerp(geo(c.from), geo(c.to), e)
    }
    static func lerp(_ a: Geo, _ b: Geo, _ u: Double) -> Geo {
        func r(_ p: CGRect, _ q: CGRect) -> CGRect {
            CGRect(x: p.minX + (q.minX - p.minX) * u, y: p.minY + (q.minY - p.minY) * u,
                   width: p.width + (q.width - p.width) * u, height: p.height + (q.height - p.height) * u)
        }
        return Geo(screen: r(a.screen, b.screen), cam: r(a.cam, b.cam), radius: a.radius + (b.radius - a.radius) * u, alpha: a.alpha + (b.alpha - a.alpha) * u)
    }

    // 合成：screen = 推拉后的成片画面（画布大小），srcTime = 这一帧在素材里的时间
    func compose(_ screen: CIImage, srcTime t: Double) -> CIImage {
        let g = geo(at: t)
        let canvas = CGRect(x: 0, y: 0, width: cw, height: ch)
        var out = screen
        if abs(g.screen.width - cw) > 0.5 || abs(g.screen.minX) > 0.5 || abs(g.screen.minY) > 0.5 {
            let k = g.screen.width / cw
            let s = screen.transformed(by: CGAffineTransform(scaleX: k, y: k).concatenating(CGAffineTransform(translationX: g.screen.minX, y: g.screen.minY)))
            out = s.composited(over: background).cropped(to: canvas)
        }
        guard g.alpha > 0.002, g.cam.width > 2, let src = frames.image(at: t - offset) else { return out }
        return Self.drawCamera(src, into: g, settings: settings, short: min(cw, ch)).composited(over: out).cropped(to: canvas)
    }

    // 摄像头画面按「铺满」裁进卡片，圆角遮罩、边框、阴影、透明度
    static func drawCamera(_ img: CIImage, into g: Geo, settings s: CameraSettings, short: Double) -> CIImage {
        var src = img
        let e0 = src.extent
        src = src.transformed(by: CGAffineTransform(translationX: -e0.minX, y: -e0.minY))
        if s.mirror { src = src.transformed(by: CGAffineTransform(scaleX: -1, y: 1).concatenating(CGAffineTransform(translationX: e0.width, y: 0))) }
        let r = g.cam, k = max(r.width / e0.width, r.height / e0.height)
        src = src.transformed(by: CGAffineTransform(scaleX: k, y: k)
            .concatenating(CGAffineTransform(translationX: r.midX - e0.width * k / 2, y: r.midY - e0.height * k / 2))).cropped(to: r)
        let radius = min(g.radius, min(r.width, r.height) / 2)
        func rounded(_ rect: CGRect, _ rad: Double, _ c: CIColor) -> CIImage {
            CIFilter(name: "CIRoundedRectangleGenerator", parameters: ["inputExtent": CIVector(cgRect: rect), "inputRadius": rad, "inputColor": c])!.outputImage!
        }
        var card = src.applyingFilter("CIBlendWithAlphaMask", parameters: [
            kCIInputBackgroundImageKey: CIImage(color: .clear), kCIInputMaskImageKey: rounded(r, radius, .white),
        ])
        let bw = s.border * short / 1080 * (radius < 1 ? 0 : 1)
        if bw > 0.3 {   // 全屏时圆角为 0：不画边框阴影
            let b = rounded(r.insetBy(dx: -bw, dy: -bw), radius + bw, CIColor(red: s.borderColor.r, green: s.borderColor.g, blue: s.borderColor.b))
            card = card.composited(over: b)
        }
        if s.shadow > 0.01 && radius >= 1 {
            let sh = rounded(r.insetBy(dx: -bw, dy: -bw).offsetBy(dx: 0, dy: -short * 0.008), radius + bw, CIColor(red: 0, green: 0, blue: 0, alpha: 0.55 * s.shadow))
                .applyingGaussianBlur(sigma: short * 0.012)
            card = card.composited(over: sh)
        }
        if g.alpha < 0.998 { card = card.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: g.alpha)]) }
        return card
    }
}

// ---- 摄像头视频按时间取帧：顺着往后读；往回跳或跳得很远就重开一个从那附近开始读（预览、导出共用） ----
final class CameraFrames: @unchecked Sendable {
    private let asset: AVURLAsset
    private let lock = NSLock()
    private var reader: AVAssetReader?
    private var out: AVAssetReaderTrackOutput?
    private var cur: CMSampleBuffer?
    private var next: CMSampleBuffer?
    private var track: AVAssetTrack?
    private var duration = 0.0

    init(url: URL) {
        asset = AVURLAsset(url: url)
        let sem = DispatchSemaphore(value: 0)
        let a = asset
        nonisolated(unsafe) var t: AVAssetTrack?, d = 0.0
        Task.detached {
            t = try? await a.loadTracks(withMediaType: .video).first
            d = (try? await a.load(.duration).seconds) ?? 0
            sem.signal()
        }
        sem.wait()
        track = t; duration = d
    }

    private func open(at t: Double) {
        reader?.cancelReading()
        reader = nil; out = nil; cur = nil; next = nil
        guard let track, let r = try? AVAssetReader(asset: asset) else { return }
        let start = max(0, t - 0.1)
        r.timeRange = CMTimeRange(start: CMTime(seconds: start, preferredTimescale: 60000), end: .positiveInfinity)
        let o = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA, kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        o.alwaysCopiesSampleData = false
        guard r.canAdd(o) else { return }
        r.add(o)
        guard r.startReading() else { return }
        reader = r; out = o
        cur = o.copyNextSampleBuffer(); next = o.copyNextSampleBuffer()
    }

    func image(at t: Double) -> CIImage? {
        lock.lock(); defer { lock.unlock() }
        guard duration > 0 else { return nil }
        let t = min(max(t, 0), duration)
        let curT = cur?.presentationTimeStamp.seconds ?? .infinity
        if reader == nil || t < curT - 0.02 || t > curT + 1.0 { open(at: t) }
        while let nx = next, nx.presentationTimeStamp.seconds <= t + 1e-4 { cur = nx; next = out?.copyNextSampleBuffer() }
        guard let s = cur, let pb = CMSampleBufferGetImageBuffer(s) else { return nil }
        return CIImage(cvPixelBuffer: pb)
    }
}

// ---- 录制：摄像头采集（预览给浮窗；Studio 模式同时写 camera.mp4） ----
final class CameraCapture: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate, @unchecked Sendable {
    let session = AVCaptureSession()
    private let output = AVCaptureVideoDataOutput()
    private let queue = DispatchQueue(label: "camera.capture")
    private var writer: AVAssetWriter?
    private var input: AVAssetWriterInput?
    private var started = false
    private(set) var firstHostSeconds = 0.0   // 写进 camera.mp4 的第一帧（主机时钟秒），和屏幕画面对齐用

    static var devices: [AVCaptureDevice] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera, .external, .continuityCamera, .deskViewCamera],
                                         mediaType: .video, position: .unspecified).devices.filter { $0.deviceType != .deskViewCamera }
    }

    func start(deviceID: String?) throws {
        session.beginConfiguration()
        session.sessionPreset = .high
        for i in session.inputs { session.removeInput(i) }
        guard let dev = Self.devices.first(where: { $0.uniqueID == deviceID }) ?? AVCaptureDevice.default(for: .video) else {
            session.commitConfiguration(); throw Studio.failure(L("没找到摄像头", "No camera found"))
        }
        let inp = try AVCaptureDeviceInput(device: dev)
        if session.canAddInput(inp) { session.addInput(inp) }
        if !session.outputs.contains(output) {
            output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
            output.alwaysDiscardsLateVideoFrames = true
            output.setSampleBufferDelegate(self, queue: queue)
            if session.canAddOutput(output) { session.addOutput(output) }
        }
        session.commitConfiguration()
        queue.async { [session] in if !session.isRunning { session.startRunning() } }
    }
    func stopSession() { queue.async { [session] in session.stopRunning() } }

    // 开始写 camera.mp4（Studio 模式）
    func beginRecording(to url: URL) throws {
        try? FileManager.default.removeItem(at: url)
        let w = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let i = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: 1280, AVVideoHeightKey: 720,
            AVVideoScalingModeKey: AVVideoScalingModeResizeAspectFill,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 6_000_000],
        ])
        i.expectsMediaDataInRealTime = true
        w.add(i)
        queue.sync { writer = w; input = i; started = false }
    }
    // 停止写；返回第一帧的主机时间（秒），没录到就 nil
    func endRecording() async -> Double? {
        let (w, i, ok, first) = queue.sync { () -> (AVAssetWriter?, AVAssetWriterInput?, Bool, Double) in
            let r = (writer, input, started, firstHostSeconds); writer = nil; input = nil; return r
        }
        guard let w, let i, ok else { w?.cancelWriting(); return nil }
        i.markAsFinished()
        await w.finishWriting()
        return w.status == .completed ? first : nil
    }

    func captureOutput(_ o: AVCaptureOutput, didOutput sb: CMSampleBuffer, from c: AVCaptureConnection) {
        guard let w = writer, let i = input else { return }
        let pts = sb.presentationTimeStamp
        if !started {
            guard w.startWriting() else { return }
            w.startSession(atSourceTime: pts)
            // 采集时钟换成主机时钟（屏幕画面的时间戳也是主机时钟）
            firstHostSeconds = CMSyncConvertTime(pts, from: session.synchronizationClock ?? CMClockGetHostTimeClock(), to: CMClockGetHostTimeClock()).seconds
            started = true
        }
        if i.isReadyForMoreMediaData { i.append(sb) }
    }
}

// ---- 屏幕上的摄像头小窗：圆形 / 圆角方形，能拖动，右键换形状和大小 ----
@MainActor final class CameraBubble: NSObject {
    static let shared = CameraBubble()
    private var panel: NSPanel?
    private var preview: AVCaptureVideoPreviewLayer?
    var round = UserDefaults.standard.object(forKey: "camRound") as? Bool ?? true { didSet { UserDefaults.standard.set(round, forKey: "camRound"); applyShape() } }
    var side = UserDefaults.standard.object(forKey: "camSide") as? Double ?? 200 { didSet { UserDefaults.standard.set(side, forKey: "camSide"); resize() } }
    var windowID: CGWindowID? { panel.map { CGWindowID($0.windowNumber) } }
    var frame: CGRect? { panel?.frame }

    func show(session: AVCaptureSession) {
        if panel == nil {
            let p = NSPanel(contentRect: CGRect(x: 0, y: 0, width: side, height: side), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            p.isFloatingPanel = true; p.level = .floating; p.hasShadow = true
            p.isMovableByWindowBackground = true; p.backgroundColor = .clear; p.isOpaque = false
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            let v = BubbleView(); v.wantsLayer = true; v.layer?.masksToBounds = true
            v.layer?.borderColor = NSColor.white.cgColor; v.layer?.borderWidth = 3
            let pl = AVCaptureVideoPreviewLayer(session: session)
            pl.videoGravity = .resizeAspectFill
            pl.connection?.automaticallyAdjustsVideoMirroring = false
            pl.connection?.isVideoMirrored = true
            v.layer?.addSublayer(pl)
            p.contentView = v
            preview = pl
            if let s = NSScreen.main?.visibleFrame {
                let x = UserDefaults.standard.object(forKey: "camX") as? Double ?? (s.maxX - side - 40)
                let y = UserDefaults.standard.object(forKey: "camY") as? Double ?? (s.minY + 40)
                p.setFrameOrigin(CGPoint(x: x, y: y))
            }
            NotificationCenter.default.addObserver(forName: NSWindow.didMoveNotification, object: p, queue: .main) { n in
                guard let w = n.object as? NSWindow else { return }
                UserDefaults.standard.set(w.frame.minX, forKey: "camX"); UserDefaults.standard.set(w.frame.minY, forKey: "camY")
            }
            panel = p
        }
        resize(); applyShape()
        panel?.orderFrontRegardless()
    }
    func hide() { panel?.orderOut(nil); panel = nil; preview = nil }

    private func resize() {
        guard let p = panel else { return }
        var f = p.frame; f.size = CGSize(width: side, height: side); p.setFrame(f, display: true)
        CATransaction.begin(); CATransaction.setDisableActions(true)
        preview?.frame = CGRect(x: 0, y: 0, width: side, height: side)
        CATransaction.commit()
        applyShape()
    }
    private func applyShape() { panel?.contentView?.layer?.cornerRadius = round ? side / 2 : side * 0.2 }

    // 录的时候小窗在画面里的位置（比例，左上为原点）；area = 录的范围（全局坐标，点，左上为原点）
    func placement(in area: CGRect) -> (x: Double, y: Double, size: Double)? {
        guard let f = panel?.frame, area.width > 0, let primary = NSScreen.screens.first else { return nil }
        let top = primary.frame.maxY - f.maxY   // AppKit 坐标（左下原点）→ 左上原点
        let cx = (f.midX - area.minX) / area.width, cy = (top + f.height / 2 - area.minY) / area.height
        return (min(max(cx, 0), 1), min(max(cy, 0), 1), f.width / min(area.width, area.height))
    }
}

final class BubbleView: NSView {
    override func menu(for event: NSEvent) -> NSMenu? {
        let m = NSMenu()
        func item(_ t: String, _ sel: Selector, _ tag: Int, on: Bool) { let i = NSMenuItem(title: t, action: sel, keyEquivalent: ""); i.target = self; i.tag = tag; i.state = on ? .on : .off; m.addItem(i) }
        MainActor.assumeIsolated {
            let b = CameraBubble.shared
            item(L("圆形", "Circle"), #selector(shape(_:)), 1, on: b.round)
            item(L("圆角方形", "Rounded square"), #selector(shape(_:)), 0, on: !b.round)
            m.addItem(.separator())
            item(L("小", "Small"), #selector(size(_:)), 140, on: b.side == 140)
            item(L("中", "Medium"), #selector(size(_:)), 200, on: b.side == 200)
            item(L("大", "Large"), #selector(size(_:)), 300, on: b.side == 300)
        }
        return m
    }
    @objc func shape(_ s: NSMenuItem) { MainActor.assumeIsolated { CameraBubble.shared.round = s.tag == 1 } }
    @objc func size(_ s: NSMenuItem) { MainActor.assumeIsolated { CameraBubble.shared.side = Double(s.tag) } }
}
