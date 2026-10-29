// Studio 效果（仿 Screen Studio）：录的时候画面里不录鼠标，另记一份鼠标轨迹；录完按轨迹自动放大点击、打字的地方，
// 镜头和鼠标都用弹簧曲线走得顺滑，再加透明背景、圆角、阴影，逐帧合成（Core Image 走显卡）导出成片
// 素材留在「影片/录屏/Studio 素材/<名字>/」：raw.mp4（没有鼠标的原始画面）、track.json（轨迹）、cursor-N.png（录到的指针图案）、
// project.json（推近段和修剪，剪辑窗口里改）
import AppKit
import AVFoundation
import CoreImage
import SwiftUI
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
    var fps = 60
    var bitsPerPixel = 0.12        // 码率：每像素每帧多少比特
}

struct RGB: Codable, Equatable {
    var r: Double, g: Double, b: Double
    init(_ r: Double, _ g: Double, _ b: Double) { self.r = r; self.g = g; self.b = b }
    var ci: CIColor { CIColor(red: r, green: g, blue: b) }
    var color: Color { Color(red: r, green: g, blue: b) }
    init(_ c: Color) {
        let n = NSColor(c).usingColorSpace(.sRGB) ?? .white
        r = Double(n.redComponent); g = Double(n.greenComponent); b = Double(n.blueComponent)
    }
}

// 剪辑窗口「样式」面板里能调的东西：存在 project.json 里，也能存成预设。旧工程没有这些字段时用默认值
struct StudioLook: Codable, Equatable {
    enum Aspect: String, Codable, CaseIterable { case original, wide = "16:9", tall = "9:16", square = "1:1", classic = "4:3" }
    enum Background: String, Codable, CaseIterable { case transparent, color, gradient, wallpaper, image }
    enum Blur: String, Codable, CaseIterable { case off, light, medium, strong }

    var aspect = Aspect.original
    var background = Background.transparent
    var color = RGB(0.96, 0.96, 0.97)
    var gradient0 = RGB(0.36, 0.40, 0.96), gradient1 = RGB(0.86, 0.43, 0.78)
    var gradientAngle = 135.0      // 度：0 = 从左到右，90 = 从下到上
    var wallpaper = ""             // 系统桌面图片的路径
    var imagePath = ""             // 自己选的图片
    var imageBookmark: Data?       // 图片被挪动后靠它找回来
    var padding = 0.06             // 画面四周留的边：占画布短边 0–20%
    var cornerRadius = 14.0        // 圆角（点）0–40
    var shadow = 0.45              // 阴影深浅 0–1
    var shadowBlur = 0.33          // 阴影虚化 0–1
    var cursorScale = 1.5          // 鼠标大小 0.5–3 倍
    var smoothing = 0.7            // 鼠标顺滑程度：0 = 不平滑，1 = 最顺
    var hideIdleCursor = false     // 鼠标不动几秒后自动隐藏
    var hideAfter = 2.0            // 1–5 秒
    var clickSound = false         // 点击时加一声轻响
    var ripple = true              // 点击波纹
    var blur = Blur.light          // 运动模糊：只在镜头快速移动时有

    init() {}
    enum CodingKeys: String, CodingKey {
        case aspect, background, color, gradient0, gradient1, gradientAngle, wallpaper, imagePath, imageBookmark, padding, cornerRadius,
             shadow, shadowBlur, cursorScale, smoothing, hideIdleCursor, hideAfter, clickSound, ripple, blur
    }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = StudioLook()
        func v<T: Decodable>(_ k: CodingKeys, _ def: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? def }
        aspect = v(.aspect, z.aspect); background = v(.background, z.background); color = v(.color, z.color)
        gradient0 = v(.gradient0, z.gradient0); gradient1 = v(.gradient1, z.gradient1); gradientAngle = v(.gradientAngle, z.gradientAngle)
        wallpaper = v(.wallpaper, z.wallpaper); imagePath = v(.imagePath, z.imagePath); imageBookmark = v(.imageBookmark, z.imageBookmark)
        padding = v(.padding, z.padding); cornerRadius = v(.cornerRadius, z.cornerRadius); shadow = v(.shadow, z.shadow)
        shadowBlur = v(.shadowBlur, z.shadowBlur); cursorScale = v(.cursorScale, z.cursorScale); smoothing = v(.smoothing, z.smoothing)
        hideIdleCursor = v(.hideIdleCursor, z.hideIdleCursor); hideAfter = v(.hideAfter, z.hideAfter); clickSound = v(.clickSound, z.clickSound)
        ripple = v(.ripple, z.ripple); blur = v(.blur, z.blur)
    }

    // 选了「图片 / 壁纸」但还没选到能用的图：按透明处理（预览、导出都一样）
    var transparent: Bool {
        switch background {
        case .transparent: true
        case .image: imageURL == nil
        case .wallpaper: wallpaper.isEmpty || !FileManager.default.fileExists(atPath: wallpaper)
        default: false
        }
    }
    var ratio: Double? {
        switch aspect { case .original: nil; case .wide: 16.0 / 9; case .tall: 9.0 / 16; case .square: 1; case .classic: 4.0 / 3 }
    }
    // 自己选的图片：路径找不到就用书签找
    var imageURL: URL? {
        if !imagePath.isEmpty, FileManager.default.fileExists(atPath: imagePath) { return URL(fileURLWithPath: imagePath) }
        var stale = false
        if let b = imageBookmark, let u = try? URL(resolvingBookmarkData: b, options: [.withoutUI], bookmarkDataIsStale: &stale) { return u }
        return nil
    }

    // 渐变预设
    static let gradients: [(zh: String, en: String, a: RGB, b: RGB)] = [
        ("晨雾", "Dawn", RGB(0.36, 0.40, 0.96), RGB(0.86, 0.43, 0.78)),
        ("海盐", "Sea Salt", RGB(0.56, 0.82, 0.98), RGB(0.36, 0.48, 0.94)),
        ("蜜桃", "Peach", RGB(1.00, 0.76, 0.62), RGB(0.97, 0.46, 0.62)),
        ("薄荷", "Mint", RGB(0.58, 0.91, 0.78), RGB(0.24, 0.60, 0.86)),
        ("暖沙", "Sand", RGB(0.99, 0.90, 0.74), RGB(0.94, 0.66, 0.50)),
        ("夜色", "Night", RGB(0.14, 0.15, 0.24), RGB(0.36, 0.28, 0.54)),
    ]
    // 系统自带的桌面图片（只读 /System/Library/Desktop Pictures 顶层的静态 .heic）
    static let wallpapers: [URL] = {
        let dir = URL(fileURLWithPath: "/System/Library/Desktop Pictures")
        let all = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return all.filter { ["heic", "jpg", "jpeg", "png"].contains($0.pathExtension.lowercased()) }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }()
}

// 预设：把一套样式存起来，下拉切换；「默认」用在新录的工程上
struct StudioPreset: Codable, Equatable, Identifiable { var name: String; var look: StudioLook; var id: String { name } }
enum PresetStore {
    struct File: Codable { var presets: [StudioPreset] = []; var defaultName: String? }
    static var url: URL {
        let d = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "Recorder")
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d.appendingPathComponent("presets.json")
    }
    static func load() -> File { (try? JSONDecoder().decode(File.self, from: Data(contentsOf: url))) ?? File() }
    static func save(_ f: File) { let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]; try? e.encode(f).write(to: url, options: .atomic) }
    static var defaultLook: StudioLook { let f = load(); return f.presets.first { $0.name == f.defaultName }?.look ?? StudioLook() }
}

// 画布和画面在画布上的位置（Core Image 坐标：左下角为原点、y 朝上）
struct StudioLayout {
    let W: Double, H: Double       // 原画面像素
    let cw: Double, ch: Double     // 画布像素
    let s: Double                  // 原画面缩放到画布上的比例（尽量 1:1，字不糊）
    let card: CGRect               // 画面（圆角卡片）在画布上的位置
    let radius: Double

    init(track: Track, look: StudioLook) {
        W = Double(track.width); H = Double(track.height)
        let f = min(max(look.padding, 0), 0.2), a = look.ratio ?? W / H
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
        radius = min(max(0, look.cornerRadius), 40) * track.pixelsPerPoint * s
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
    var alpha = 1.0                // 鼠标不动自动隐藏时淡出
}

// ---- 推近段（工程文件 project.json，存在素材夹里）：第一次按点击 / 打字 / 拖动自动生成，剪辑窗口里可以改 ----
struct ZoomSegment: Codable, Equatable, Identifiable {
    enum Mode: String, Codable { case follow, fixed }   // 跟着鼠标走 / 盯住一个固定位置
    var id = UUID()
    var start: Double
    var end: Double
    var zoom = 2.0
    var mode = Mode.follow
    var fixedX = 0.5, fixedY = 0.5     // 固定位置：在原画面里的比例，左上角为原点
}

struct StudioProject: Codable, Equatable {
    var segments: [ZoomSegment] = []
    var trimStart = 0.0
    var trimEnd = 0.0
    var look = StudioLook()
    var segmentsEdited = false          // 推近段手动改过：以后自动规则变了也不重算；没改过的打开时按最新规则重排

    init(segments: [ZoomSegment] = [], trimStart: Double = 0, trimEnd: Double = 0, look: StudioLook = StudioLook()) {
        self.segments = segments; self.trimStart = trimStart; self.trimEnd = trimEnd; self.look = look
    }
    enum CodingKeys: String, CodingKey { case segments, trimStart, trimEnd, look, segmentsEdited }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        segments = try c.decodeIfPresent([ZoomSegment].self, forKey: .segments) ?? []
        trimStart = try c.decodeIfPresent(Double.self, forKey: .trimStart) ?? 0
        trimEnd = try c.decodeIfPresent(Double.self, forKey: .trimEnd) ?? 0
        look = (try? c.decodeIfPresent(StudioLook.self, forKey: .look)) ?? StudioLook()   // 旧工程没有样式：用默认
        segmentsEdited = try c.decodeIfPresent(Bool.self, forKey: .segmentsEdited) ?? false   // 旧工程没有这个标记：当作没改过
    }

    static func url(_ dir: URL) -> URL { dir.appendingPathComponent("project.json") }
    // 有 project.json 就读，没有就按自动规则生成一份
    static func load(_ dir: URL, track: Track, duration: Double, zoom: Double = 2.0) -> StudioProject {
        if let d = try? Data(contentsOf: url(dir)), var p = try? JSONDecoder().decode(StudioProject.self, from: d) {
            p.trimStart = min(max(0, p.trimStart), duration)
            p.trimEnd = p.trimEnd > p.trimStart ? min(p.trimEnd, duration) : duration
            p.segments.sort { $0.start < $1.start }
            // 只写了样式、没写推近段的工程（自测用）：推近段按自动规则补上
            struct Keys: Decodable { var segments: [ZoomSegment]?; var trimEnd: Double? }
            if let k = try? JSONDecoder().decode(Keys.self, from: d) {
                if k.segments == nil || !p.segmentsEdited { p.segments = StudioPlan.autoSegments(track, duration: duration, zoom: zoom) }
                if k.trimEnd == nil { p.trimEnd = duration }
            }
            return p
        }
        return StudioProject(segments: StudioPlan.autoSegments(track, duration: duration, zoom: zoom), trimStart: 0, trimEnd: duration,
                             look: PresetStore.defaultLook)   // 新工程用默认预设
    }
    func save(_ dir: URL) throws {
        let enc = JSONEncoder(); enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: Self.url(dir), options: .atomic)
    }
}

enum StudioPlan {
    typealias Event = (t: Double, x: Double, y: Double)

    // 鼠标在 t 秒的位置（前后两次采样之间插值），y 换成朝上
    struct Mouse {
        let tr: Track, ts: [Double], H: Double
        init(_ tr: Track) { self.tr = tr; ts = tr.moves.map { $0[0] }; H = Double(tr.height) }
        func at(_ t: Double) -> (x: Double, y: Double, shape: Int)? {
            guard !ts.isEmpty else { return nil }
            var lo = 0, hi = ts.count
            while lo < hi { let m = (lo + hi) / 2; if ts[m] <= t { lo = m + 1 } else { hi = m } }
            let b = tr.moves[min(lo, ts.count - 1)], a = tr.moves[max(lo - 1, 0)]
            let u = b[0] > a[0] ? min(1, max(0, (t - a[0]) / (b[0] - a[0]))) : 0
            return (a[1] + (b[1] - a[1]) * u, H - (a[2] + (b[2] - a[2]) * u), Int(a[3]))
        }
    }

    // 值得放大的时刻：点击、打字、按住拖动（每 0.3 秒算一次）；最后 1 秒不算（多半是去点「停止」或按快捷键）。y 朝上
    static func events(_ tr: Track, duration: Double) -> [Event] {
        let H = Double(tr.height), mouse = Mouse(tr)
        var ev: [Event] = tr.clicks.map { ($0[0], $0[1], H - $0[2]) }
        for k in tr.keys { if let m = mouse.at(k) { ev.append((k, m.x, m.y)) } }
        var lastDrag = -1.0
        for m in tr.moves where m[4] == 1 && m[0] - lastDrag >= 0.3 { ev.append((m[0], m[1], H - m[2])); lastDrag = m[0] }
        return ev.filter { $0.t >= 0 && $0.t <= duration - 1 }.sorted { $0.t < $1.t }
    }

    // 自动生成推近段（参考 Screen Studio 的观感）：
    // - 事件前 0.6 秒推近，最后一个事件后停 3.5 秒；同一块地方接着操作、间隔不到 1.5 秒就连成一段（别刚拉远又推近）
    // - 下一个事件离得远（超过约 1/3 屏幕）、中间又有空：前一段收掉，拉回全景至少 1.2 秒再推近过去；
    //   来不及拉远：留在同一段，镜头平移过去，路远时边移边往后退一点（见 make）
    // - 一段最长约 8 秒：再往后有 ≥1.6 秒的空档就在那里断开、回一下全景（1 秒）；一直不停超过 10 秒就硬断
    static let zoomOutHold = 1.2, shortOut = 1.0, leadIn = 0.6, holdAfter = 3.5, mergeGap = 1.5, maxLen = 8.0, hardMax = 10.0
    static func autoSegments(_ tr: Track, duration: Double, zoom: Double = 2.0) -> [ZoomSegment] {
        let W = Double(tr.width), H = Double(tr.height)
        struct Cur { var start: Double; var end: Double; var lastT: Double; var x: Double; var y: Double; var fx: Double; var fy: Double }
        var segs: [ZoomSegment] = []
        var cur: Cur?
        func close(_ c: Cur, at end: Double) {
            segs.append(ZoomSegment(start: c.start, end: max(c.start + 0.5, end), zoom: zoom, fixedX: c.fx / W, fixedY: 1 - c.fy / H))
        }
        for e in events(tr, duration: duration) {
            let s = max(0, e.t - leadIn), end = min(duration, e.t + holdAfter)
            guard var c = cur else { cur = Cur(start: s, end: end, lastT: e.t, x: e.x, y: e.y, fx: e.x, fy: e.y); continue }
            let gap = e.t - c.lastT
            let far = max(abs(e.x - c.x) / W, abs(e.y - c.y) / H) > 0.35
            let len = e.t - c.start
            // 能在中间拉回全景：前一段最晚收在「下一段开始前 1.2 秒」，还得比上个事件晚一点
            let outEnd = s - zoomOutHold, canZoomOut = outEnd >= c.lastT + 0.4
            if s > c.end + mergeGap {
                close(c, at: min(c.end, s - zoomOutHold)); cur = Cur(start: s, end: end, lastT: e.t, x: e.x, y: e.y, fx: e.x, fy: e.y)
            } else if far && canZoomOut {
                close(c, at: outEnd); cur = Cur(start: s, end: end, lastT: e.t, x: e.x, y: e.y, fx: e.x, fy: e.y)
            } else if len > maxLen && gap >= leadIn + shortOut && s - shortOut > c.lastT {
                close(c, at: s - shortOut); cur = Cur(start: s, end: end, lastT: e.t, x: e.x, y: e.y, fx: e.x, fy: e.y)
            } else if len > hardMax {
                // 一直没空：在上个事件后马上收，回全景 1 秒，再推近（这一下的事件会稍晚一点才推到）
                let cut = c.lastT + 0.2
                close(c, at: cut); cur = Cur(start: cut + shortOut, end: max(end, cut + shortOut + 0.5), lastT: e.t, x: e.x, y: e.y, fx: e.x, fy: e.y)
            } else {
                c.end = max(c.end, end); c.lastT = e.t; c.x = e.x; c.y = e.y; cur = c
            }
        }
        if let c = cur { close(c, at: c.end) }
        return segs
    }

    // 逐帧算镜头和鼠标：从 0 秒算到结尾（修剪只决定导出哪几帧，镜头怎么走不受影响，预览和成片一致）
    static func make(_ tr: Track, _ L: StudioLayout, _ st: StudioStyle, look: StudioLook, segments: [ZoomSegment], frames n: Int, duration: Double) -> [FramePlan] {
        let W = L.W, H = L.H, dt = 1 / Double(st.fps)
        let mouse = Mouse(tr), ev = events(tr, duration: duration)
        // 鼠标顺滑：弹簧越软越顺；0 = 不平滑，直接用原位置
        let smooth = look.smoothing > 0.01, mw = 8 + (1 - min(max(look.smoothing, 0), 1)) * 72
        // 鼠标最后一次动（或点）的时刻：不动超过 hideAfter 秒就淡出
        var moved: [Double] = []
        var prev: [Double]?
        for m in tr.moves { if let p = prev, hypot(m[1] - p[1], m[2] - p[2]) > 0.5 || m[4] != p[4] { moved.append(m[0]) }; prev = m }
        moved.append(contentsOf: tr.clicks.map { $0[0] }); moved.sort()
        var mi = 0, alpha = 1.0, lastMove = 0.0
        let segs = segments.sorted { $0.start < $1.start }
        var lz = Spring(x: 0, w: 4), cx = Spring(x: W / 2, w: 4), cy = Spring(x: H / 2, w: 4)
        var mx: Spring?, my: Spring?
        var si = 0
        var cur: UUID?
        var focus = (x: W / 2, y: H / 2)
        var out = [FramePlan](repeating: FramePlan(), count: n)
        for i in 0..<n {
            let t = Double(i) * dt
            var f = FramePlan()
            let m = mouse.at(t)
            if let m {
                if mx == nil { mx = Spring(x: m.x, w: mw); my = Spring(x: m.y, w: mw) }
                mx!.step(to: m.x, dt: dt); my!.step(to: m.y, dt: dt)
                f.mx = smooth ? mx!.x : m.x; f.my = smooth ? my!.x : m.y
                // 鼠标跑到画面外面（别的屏幕）就不画
                f.shape = f.mx > -40 && f.mx < W + 40 && f.my > -40 && f.my < H + 40 ? m.shape : -1
            }
            while si < segs.count && segs[si].end <= t { si += 1 }
            var tz = 1.0, tx = W / 2, ty = H / 2
            if si < segs.count && segs[si].start <= t {
                let seg = segs[si], Z = max(1, seg.zoom)
                let hw = L.cw / (2 * Z * L.s), hh = L.ch / (2 * Z * L.s)   // 放大时看得到的原画面半宽、半高
                let segEv = ev.filter { $0.t >= seg.start && $0.t < seg.end }
                if seg.mode == .fixed {
                    focus = (seg.fixedX * W, H - seg.fixedY * H)
                } else {
                    if cur != seg.id {
                        cur = seg.id
                        if let e = segEv.first { focus = (e.x, e.y) } else if let m { focus = (m.x, m.y) }
                    }
                    // 半秒内要点的地方离镜头远：提前转过去（录完再算，所以能「预知」）
                    if let e = segEv.first(where: { $0.t >= t && $0.t <= t + 0.5 }), abs(e.x - focus.x) > hw * 0.5 || abs(e.y - focus.y) > hh * 0.5 {
                        focus = (e.x, e.y)
                    } else if let m, t >= (segEv.first?.t ?? seg.start) {
                        // 推近时镜头跟着鼠标走（像 Screen Studio）：鼠标离开中间一小块（25%）就跟，弹簧让它走得顺
                        let rx = hw * 0.25, ry = hh * 0.25
                        if m.x > focus.x + rx { focus.x = m.x - rx } else if m.x < focus.x - rx { focus.x = m.x + rx }
                        if m.y > focus.y + ry { focus.y = m.y - ry } else if m.y < focus.y - ry { focus.y = m.y + ry }
                    }
                }
                focus.x = 2 * hw >= W ? W / 2 : min(max(focus.x, hw), W - hw)
                focus.y = 2 * hh >= H ? H / 2 : min(max(focus.y, hh), H - hh)
                tz = Z; tx = focus.x; ty = focus.y
                // 镜头要走很远（来不及拉远再推近时）：边移边往后退，路越远退得越多，到了再推回去
                let travel = max(abs(tx - cx.x) / W, abs(ty - cy.x) / H)
                if travel > 0.2 { tz = 1 + (Z - 1) * min(1, max(0.2, 1 - (travel - 0.2) / 0.3)) }
            }
            lz.step(to: log(tz), dt: dt); cx.step(to: tx, dt: dt); cy.step(to: ty, dt: dt)
            f.z = exp(lz.x)
            // 镜头框不能出画布
            let ex = L.cw / (2 * f.z), ey = L.ch / (2 * f.z)
            f.sx = min(max(L.card.minX + cx.x * L.s, ex), L.cw - ex)
            f.sy = min(max(L.card.minY + cy.x * L.s, ey), L.ch - ey)
            // 点下去时指针缩一下再弹回
            for c in tr.clicks where t >= c[0] && t < c[0] + 0.25 { f.press = 1 - 0.18 * sin(.pi * (t - c[0]) / 0.25) }
            // 不动自动隐藏：淡出 0.3 秒，一动就 0.12 秒淡入
            if look.hideIdleCursor {
                while mi < moved.count && moved[mi] <= t { lastMove = moved[mi]; mi += 1 }
                let target = t - lastMove >= look.hideAfter ? 0.0 : 1.0
                alpha = target < alpha ? max(target, alpha - dt / 0.3) : min(target, alpha + dt / 0.12)
                f.alpha = alpha
            }
            out[i] = f
        }
        return out
    }
}

// ---- 导出 ----
enum Studio {
    static let failure = { (s: String) in NSError(domain: "Studio", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }

    // 成片文件名：透明背景存 HEVC .mov，其他背景存 H.264 .mp4
    static func output(_ out: URL, look: StudioLook) -> URL { out.deletingPathExtension().appendingPathExtension(look.transparent ? "mov" : "mp4") }

    // 返回实际写出的文件（扩展名跟着背景走）
    @discardableResult
    static func render(project dir: URL, to out: URL, style: StudioStyle = StudioStyle(), edits: StudioProject? = nil, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let job = try await StudioJob(project: dir, out: out, style: style, project: edits, progress: progress)
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                do { try job.run(); c.resume() } catch { c.resume(throwing: error) }
            }
        }
        return job.outURL
    }

    // 自测：STUDIO=素材文件夹 [STUDIO_OUT=输出路径] [STUDIO_ASPECT=16:9] [STUDIO_BLUR=off|light|medium|strong] → 只导出，不录，打印用时后退出
    // 样式按素材夹里的 project.json；STUDIO_ASPECT / STUDIO_BLUR 临时盖掉工程里的设置
    @MainActor static func selfTest() -> Bool {
        let env = ProcessInfo.processInfo.environment
        guard let p = env["STUDIO"] else { return false }
        let dir = URL(fileURLWithPath: p)
        let out = URL(fileURLWithPath: env["STUDIO_OUT"] ?? dir.appendingPathComponent("studio.mov").path)
        var st = StudioStyle()
        if let z = env["STUDIO_ZOOM"].flatMap(Double.init) { st.zoom = z }
        if let b = env["STUDIO_BPP"].flatMap(Double.init) { st.bitsPerPixel = b }
        Task {
            let t0 = Date()
            do {
                var edits: StudioProject?
                if env["STUDIO_ASPECT"] != nil || env["STUDIO_BLUR"] != nil {
                    let track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
                    let d = try await AVURLAsset(url: dir.appendingPathComponent("raw.mp4")).load(.duration).seconds
                    var pr = StudioProject.load(dir, track: track, duration: d, zoom: st.zoom)
                    if let a = env["STUDIO_ASPECT"].flatMap(StudioLook.Aspect.init(rawValue:)) { pr.look.aspect = a }
                    if let b = env["STUDIO_BLUR"].flatMap(StudioLook.Blur.init(rawValue:)) { pr.look.blur = b }
                    edits = pr
                }
                let u = try await render(project: dir, to: out, style: st, edits: edits) { _ in }
                print("STUDIO OK", String(format: "%.1fs", Date().timeIntervalSince(t0)), u.path)
            } catch { print("STUDIO FAIL", error) }
            NSApp.terminate(nil)
        }
        return true
    }
}

// ---- 一帧怎么画：导出和剪辑窗口的预览共用这一套，所以预览和成片一样 ----
final class StudioRenderer: @unchecked Sendable {
    // 不做色彩管理：原画面的数值原样搬过去，颜色和原录屏一致。所有渲染器共用一个（改样式时会重建渲染器）
    static let sharedContext = CIContext(mtlDevice: MTLCreateSystemDefaultDevice()!, options: [.workingColorSpace: NSNull(), .outputColorSpace: NSNull(), .workingFormat: CIFormat.RGBAh])
    let track: Track
    let lay: StudioLayout
    let st: StudioStyle
    let look: StudioLook
    let ctx = StudioRenderer.sharedContext
    private let clicks: [(t: Double, x: Double, y: Double)]
    private var cursorArt: [Int: (img: CIImage, ppp: Double, hotX: Double, hotY: Double)] = [:]
    private let arrow: (img: CIImage, ppp: Double, hotX: Double, hotY: Double)
    private let ring: CIImage
    private var background: CIImage!
    private var mask: CIImage!

    init(project dir: URL, track: Track, style: StudioStyle, look: StudioLook) {
        self.track = track; st = style; self.look = look
        lay = StudioLayout(track: track, look: look)
        let h = Double(track.height)
        clicks = track.clicks.map { ($0[0], $0[1], h - $0[2]) }
        arrow = Self.drawArrow()
        ring = Self.drawRing()
        for (i, c) in track.cursors.enumerated() where !c.arrow {
            if let img = CIImage(contentsOf: dir.appendingPathComponent(c.file)), c.w > 0 {
                cursorArt[i] = (img, img.extent.width / c.w, c.hotX, c.hotY)
            }
        }
        prepareBackground()
    }

    func plan(_ project: StudioProject, duration: Double) -> [FramePlan] {
        StudioPlan.make(track, lay, st, look: look, segments: project.segments, frames: max(1, Int(duration * Double(st.fps))), duration: duration)
    }

    // 背景 + 卡片阴影：每帧都一样，先画好存成一张图
    private func prepareBackground() {
        let canvas = lay.canvas
        var g: CIImage
        switch look.background {
        case .transparent: g = CIImage(color: .clear)
        case .color: g = CIImage(color: look.color.ci)
        case .gradient:
            // 按角度从一边渐变到另一边：两个端点放在画布中心两侧、跨过整个画布
            let a = look.gradientAngle * .pi / 180, cx = lay.cw / 2, cy = lay.ch / 2
            let r = (abs(cos(a)) * lay.cw + abs(sin(a)) * lay.ch) / 2
            g = CIFilter(name: "CILinearGradient", parameters: [
                "inputPoint0": CIVector(x: cx - cos(a) * r, y: cy - sin(a) * r), "inputColor0": look.gradient0.ci,
                "inputPoint1": CIVector(x: cx + cos(a) * r, y: cy + sin(a) * r), "inputColor1": look.gradient1.ci,
            ])!.outputImage!
        case .wallpaper, .image:
            let u = look.background == .wallpaper ? (look.wallpaper.isEmpty ? nil : URL(fileURLWithPath: look.wallpaper)) : look.imageURL
            if let u, let img = CIImage(contentsOf: u, options: [.applyOrientationProperty: true]) {
                // 铺满画布（多出来的裁掉），居中
                let e = img.extent, k = max(lay.cw / e.width, lay.ch / e.height)
                g = img.transformed(by: CGAffineTransform(translationX: -e.minX, y: -e.minY).concatenating(CGAffineTransform(scaleX: k, y: k))
                    .concatenating(CGAffineTransform(translationX: (lay.cw - e.width * k) / 2, y: (lay.ch - e.height * k) / 2)))
            } else { g = CIImage(color: .clear) }   // 还没选图 / 图片找不到：透明（look.transparent 也会是 true）
        }
        g = g.cropped(to: canvas)
        let strength = min(max(look.shadow, 0), 1), blur = min(max(look.shadowBlur, 0), 1)
        var bg = g
        if strength > 0.001 {
            let sigma = lay.cw * (0.003 + 0.033 * blur)
            let shadow = rounded(lay.card.offsetBy(dx: 0, dy: -lay.ch * (0.004 + 0.016 * blur)), lay.radius, CIColor(red: 0, green: 0, blue: 0, alpha: strength))
                .applyingGaussianBlur(sigma: max(0.5, sigma))
            bg = shadow.composited(over: g).cropped(to: canvas)
        }
        let cg = ctx.createCGImage(bg, from: canvas, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))!
        background = CIImage(cgImage: cg)
        mask = lay.radius > 0.5 ? rounded(lay.card, lay.radius, .white) : CIImage(color: .white).cropped(to: lay.card)
    }
    private func rounded(_ r: CGRect, _ radius: Double, _ color: CIColor) -> CIImage {
        CIFilter(name: "CIRoundedRectangleGenerator", parameters: ["inputExtent": CIVector(cgRect: r), "inputRadius": radius, "inputColor": color])!.outputImage!
    }

    private func camera(z: Double, sx: Double, sy: Double) -> CGAffineTransform {
        CGAffineTransform(a: z, b: 0, c: 0, d: z, tx: lay.cw / 2 - sx * z, ty: lay.ch / 2 - sy * z)
    }

    // 合成第 i 帧（plan 从 0 秒算起）：背景 → 圆角画面 → 点击波纹 → 鼠标，整幅按镜头推拉
    func frame(_ i: Int, plan: [FramePlan], source: CIImage) -> CIImage {
        let f = plan[i], W = lay.W, H = lay.H
        var src = source
        let bh = src.extent.height   // 高度是奇数时底下补过 1 像素，切掉
        src = src.cropped(to: CGRect(x: 0, y: bh - H, width: W, height: H)).transformed(by: CGAffineTransform(translationX: 0, y: -(bh - H)))
        var scene = src.transformed(by: lay.toScene).applyingFilter("CIBlendWithAlphaMask", parameters: [
            kCIInputBackgroundImageKey: background!, kCIInputMaskImageKey: mask!,
        ])
        let t = Double(i) / Double(st.fps), ppp = track.pixelsPerPoint
        // 点击波纹：0.5 秒内从 6 点扩到 28 点、渐渐淡掉
        if look.ripple {
            for c in clicks where t >= c.t && t < c.t + 0.5 {
                let u = (t - c.t) / 0.5, e = 1 - pow(1 - u, 3)
                let r = (6 + 22 * e) * ppp * look.cursorScale / 1.5, k = r / 52
                let img = ring.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: 1 - u)])
                    .transformed(by: CGAffineTransform(translationX: -64, y: -64).concatenating(CGAffineTransform(scaleX: k, y: k))
                        .concatenating(CGAffineTransform(translationX: c.x, y: c.y)).concatenating(lay.toScene))
                scene = img.composited(over: scene)
            }
        }
        if f.shape >= 0 && f.alpha > 0.002 {
            let art = track.cursors.indices.contains(f.shape) && !track.cursors[f.shape].arrow ? cursorArt[f.shape] ?? arrow : arrow
            let k = ppp * look.cursorScale * f.press / art.ppp
            let hx = art.hotX * art.ppp, hy = art.img.extent.height - art.hotY * art.ppp
            var img = art.img.transformed(by: CGAffineTransform(translationX: -hx, y: -hy).concatenating(CGAffineTransform(scaleX: k, y: k))
                .concatenating(CGAffineTransform(translationX: f.mx, y: f.my)).concatenating(lay.toScene))
            if f.alpha < 0.998 { img = img.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: f.alpha)]) }
            scene = img.composited(over: scene)
        }
        let still = scene.transformed(by: camera(z: f.z, sx: f.sx, sy: f.sy)).cropped(to: lay.canvas)
        // 运动模糊（快门式）：镜头动得快的帧，在这一帧前后一小段时间里取几个镜头位置分别画、再平均；镜头不动的帧原样不碰，字是清楚的
        guard let subs = shutter(i, plan: plan) else { return still }
        var acc: CIImage?
        for (k, c) in subs.enumerated() {
            let img = scene.transformed(by: camera(z: c.z, sx: c.sx, sy: c.sy)).cropped(to: lay.canvas)
            acc = acc.map { img.applyingFilter("CIMix", parameters: [kCIInputBackgroundImageKey: $0, kCIInputAmountKey: 1.0 / Double(k + 1)]) } ?? img
        }
        return (acc ?? still).cropped(to: lay.canvas)
    }

    // 快门子位置：镜头每帧移动（含推拉）换算成画布上的像素，超过门槛才模糊；快门角 轻 90° / 中 180° / 强 270°，取 3–8 个位置
    func shutter(_ i: Int, plan: [FramePlan]) -> [(z: Double, sx: Double, sy: Double)]? {
        let angle: Double
        switch look.blur { case .off: return nil; case .light: angle = 90; case .medium: angle = 180; case .strong: angle = 270 }
        guard plan.count > 1 else { return nil }
        let a = plan[max(i - 1, 0)], b = plan[min(i + 1, plan.count - 1)], f = plan[i]
        let span = Double(min(i + 1, plan.count - 1) - max(i - 1, 0))   // 前后相隔几帧
        let diag = hypot(lay.cw, lay.ch) / 2
        let move = (hypot((b.sx - a.sx) * f.z, (b.sy - a.sy) * f.z) + abs(log(b.z / a.z)) * diag) / span   // 每帧移动的像素
        let blurPx = move * angle / 360
        guard blurPx >= 1.5 else { return nil }
        let n = min(8, max(3, Int(blurPx / 2) + 1))
        // 在 i ± (快门/2) 帧之间按相邻帧插值：放大倍数按对数插值
        func at(_ x: Double) -> (z: Double, sx: Double, sy: Double) {
            let j = min(max(x, 0), Double(plan.count - 1)), lo = Int(j.rounded(.down)), hi = min(lo + 1, plan.count - 1), u = j - Double(lo)
            let p = plan[lo], q = plan[hi]
            return (exp(log(p.z) + (log(q.z) - log(p.z)) * u), p.sx + (q.sx - p.sx) * u, p.sy + (q.sy - p.sy) * u)
        }
        let half = angle / 360 / 2
        return (0..<n).map { at(Double(i) + (Double($0) / Double(n - 1) * 2 - 1) * half) }
    }

    // 画布上一点（左下为原点）→ 原画面里的比例（左上为原点）：在预览上点一下设固定位置用
    func sourcePoint(canvas p: CGPoint, frame f: FramePlan) -> CGPoint {
        let sx = (p.x - (lay.cw / 2 - f.sx * f.z)) / f.z, sy = (p.y - (lay.ch / 2 - f.sy * f.z)) / f.z
        let rx = (sx - lay.card.minX) / lay.s, ry = (sy - lay.card.minY) / lay.s
        return CGPoint(x: min(max(rx / lay.W, 0), 1), y: min(max(1 - ry / lay.H, 0), 1))
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

// ---- 导出成片：按工程的推近段、修剪和样式逐帧合成；透明背景写 HEVC .mov，其他背景写 H.264 .mp4 ----
final class StudioJob: @unchecked Sendable {
    private let asset: AVURLAsset
    private let videoTrack: AVAssetTrack
    private let audioTracks: [AVAssetTrack]
    let outURL: URL
    private let r: StudioRenderer
    private let plan: [FramePlan]
    private let first: Int, last: Int          // 导出第 first..<last 帧
    private let clickTimes: [Double]           // 要加点击声的时刻（秒，素材时间）；不加就是空的
    private let progress: @Sendable (Double) -> Void

    init(project dir: URL, out: URL, style: StudioStyle, project: StudioProject? = nil, progress: @escaping @Sendable (Double) -> Void) async throws {
        let track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
        asset = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        guard let vt = try await asset.loadTracks(withMediaType: .video).first else { throw Studio.failure(L("素材里没有画面", "No video in the recording")) }
        videoTrack = vt
        audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let duration = try await asset.load(.duration).seconds
        self.progress = progress
        let p = project ?? StudioProject.load(dir, track: track, duration: duration, zoom: style.zoom)
        outURL = Studio.output(out, look: p.look)
        r = StudioRenderer(project: dir, track: track, style: style, look: p.look)
        plan = r.plan(p, duration: duration)
        let fps = Double(style.fps)
        first = min(max(0, Int((p.trimStart * fps).rounded())), plan.count - 1)
        last = max(first + 1, min(plan.count, Int((p.trimEnd * fps).rounded())))
        clickTimes = p.look.clickSound ? track.clicks.map { $0[0] } : []
    }

    func run() throws {
        let st = r.st, lay = r.lay, fps = CMTimeScale(st.fps), alpha = r.look.transparent
        let range = CMTimeRange(start: CMTime(value: CMTimeValue(first), timescale: fps), end: CMTime(value: CMTimeValue(last), timescale: fps))
        let reader = try AVAssetReader(asset: asset)
        reader.timeRange = range
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

        try? FileManager.default.removeItem(at: outURL)
        let writer = try AVAssetWriter(outputURL: outURL, fileType: alpha ? .mov : .mp4)
        let w = Int(lay.cw), h = Int(lay.ch)
        var props: [String: Any] = [
            AVVideoAverageBitRateKey: max(10_000_000, Int(Double(w * h * st.fps) * st.bitsPerPixel)),
            AVVideoExpectedSourceFrameRateKey: st.fps,
            AVVideoMaxKeyFrameIntervalKey: st.fps * 2,
        ]
        if alpha { props[kVTCompressionPropertyKey_AlphaChannelMode as String] = kVTAlphaChannelMode_PremultipliedAlpha }   // Core Image 出的是预乘透明
        else { props[AVVideoProfileLevelKey] = AVVideoProfileLevelH264HighAutoLevel }
        // 标上色彩（和原始录屏一样 709），不然有的播放器按 601 解，颜色会偏
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: alpha ? AVVideoCodecType.hevcWithAlpha : AVVideoCodecType.h264, AVVideoWidthKey: w, AVVideoHeightKey: h,
            AVVideoCompressionPropertiesKey: props,
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
        ])
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vIn, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: w, kCVPixelBufferHeightKey as String: h,
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        writer.add(vIn)
        var aIn: AVAssetWriterInput?
        if aOut != nil || !clickTimes.isEmpty {
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
        writer.startSession(atSourceTime: range.start)

        let group = DispatchGroup()
        var failure: Error?
        var i = first
        let total = last - first
        group.enter()
        var videoDone = false
        vIn.requestMediaDataWhenReady(on: DispatchQueue(label: "studio.video")) { [self] in
            while vIn.isReadyForMoreMediaData && !videoDone {
                if i >= last || failure != nil { videoDone = true; vIn.markAsFinished(); group.leave(); return }
                let pts = CMTime(value: CMTimeValue(i), timescale: fps)
                while let nx = next, nx.presentationTimeStamp <= pts { cur = nx; next = vOut.copyNextSampleBuffer() }
                autoreleasepool {
                    guard let src = CMSampleBufferGetImageBuffer(cur), let pool = adaptor.pixelBufferPool else { failure = Studio.failure("frame"); return }
                    var pb: CVPixelBuffer?
                    CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
                    guard let pb else { failure = Studio.failure("pool"); return }
                    r.ctx.render(r.frame(i, plan: plan, source: CIImage(cvPixelBuffer: src)), to: pb, bounds: lay.canvas, colorSpace: nil)
                    if !adaptor.append(pb, withPresentationTime: pts) { failure = writer.error ?? Studio.failure("append") }
                }
                i += 1
                if (i - first) % 30 == 0 { progress(Double(i - first) / Double(total)) }
            }
        }
        if let aIn {
            group.enter()
            var audioDone = false
            let clicks = ClickSound(times: clickTimes)
            // 没有原声音只有点击声：自己按 0.1 秒一块生成静音 + 点击
            var genAt = Int64((range.start.seconds * 48000).rounded()), genEnd = Int64((range.end.seconds * 48000).rounded())
            aIn.requestMediaDataWhenReady(on: DispatchQueue(label: "studio.audio")) {
                while aIn.isReadyForMoreMediaData && !audioDone {
                    let sb: CMSampleBuffer?
                    if let aOut {
                        sb = aOut.copyNextSampleBuffer().map { clicks.isEmpty ? $0 : (clicks.mix(into: $0) ?? $0) }
                    } else if genAt < genEnd {
                        let n = Int(min(4800, genEnd - genAt))
                        sb = clicks.silence(frames: n, at: genAt); genAt += Int64(n)
                    } else { sb = nil }
                    guard let sb else { audioDone = true; aIn.markAsFinished(); group.leave(); return }
                    aIn.append(sb)
                }
            }
        }
        group.wait()
        if let failure { reader.cancelReading(); writer.cancelWriting(); throw failure }
        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? Studio.failure("reader") }
        let sem = DispatchSemaphore(value: 0)
        writer.endSession(atSourceTime: CMTime(value: CMTimeValue(last), timescale: fps))
        writer.finishWriting { sem.signal() }
        sem.wait()
        guard writer.status == .completed else { throw writer.error ?? Studio.failure("finish") }
        progress(1)
    }
}

// 点击音效：程序合成一声短促的「嗒」（高频衰减正弦 + 一点噪声，40 毫秒），按点击时刻混进 48k 立体声浮点 PCM
final class ClickSound: @unchecked Sendable {
    private let starts: [Int64]                // 每次点击从第几个采样开始
    private let wave: [Float]
    private var format: CMAudioFormatDescription?
    var isEmpty: Bool { starts.isEmpty }

    init(times: [Double]) {
        starts = times.map { Int64(($0 * 48000).rounded()) }.sorted()
        var g = SystemRandomNumberGenerator()
        wave = (0..<1920).map { k in
            let t = Double(k) / 48000, env = exp(-t / 0.0045)
            let tone = 0.65 * sin(2 * .pi * 3100 * t) + 0.35 * sin(2 * .pi * 1700 * t)
            return Float(env * (0.8 * tone + 0.2 * Double.random(in: -1...1, using: &g)) * 0.32)
        }
    }

    // 往一块声音里加上落在它时间段里的点击，返回新的一块
    func mix(into sb: CMSampleBuffer) -> CMSampleBuffer? {
        let n = CMSampleBufferGetNumSamples(sb)
        guard n > 0, let block = CMSampleBufferGetDataBuffer(sb), let fmt = CMSampleBufferGetFormatDescription(sb) else { return nil }
        let a0 = Int64((CMSampleBufferGetPresentationTimeStamp(sb).seconds * 48000).rounded())
        var data = [Float](repeating: 0, count: n * 2)
        let bytes = data.count * 4
        guard CMBlockBufferGetDataLength(block) >= bytes else { return nil }
        _ = data.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: bytes, destination: $0.baseAddress!) }
        guard add(&data, frames: n, at: a0) else { return sb }
        return make(data, frames: n, at: a0, format: fmt)
    }

    func silence(frames n: Int, at a0: Int64) -> CMSampleBuffer? {
        var data = [Float](repeating: 0, count: n * 2)
        _ = add(&data, frames: n, at: a0)
        if format == nil {
            var asbd = AudioStreamBasicDescription(mSampleRate: 48000, mFormatID: kAudioFormatLinearPCM, mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                                                   mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8, mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
            CMAudioFormatDescriptionCreate(allocator: nil, asbd: &asbd, layoutSize: 0, layout: nil, magicCookieSize: 0, magicCookie: nil, extensions: nil, formatDescriptionOut: &format)
        }
        guard let format else { return nil }
        return make(data, frames: n, at: a0, format: format)
    }

    private func add(_ data: inout [Float], frames n: Int, at a0: Int64) -> Bool {
        var touched = false
        for s in starts where s < a0 + Int64(n) && s + Int64(wave.count) > a0 {
            for k in 0..<wave.count {
                let j = s + Int64(k) - a0
                guard j >= 0 && j < Int64(n) else { continue }
                let v = wave[k]
                data[Int(j) * 2] = max(-1, min(1, data[Int(j) * 2] + v))
                data[Int(j) * 2 + 1] = max(-1, min(1, data[Int(j) * 2 + 1] + v))
            }
            touched = true
        }
        return touched
    }

    private func make(_ data: [Float], frames n: Int, at a0: Int64, format: CMAudioFormatDescription) -> CMSampleBuffer? {
        let bytes = data.count * 4
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: bytes, blockAllocator: nil, customBlockSource: nil,
                                                 offsetToData: 0, dataLength: bytes, flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block) == noErr, let block
        else { return nil }
        _ = data.withUnsafeBytes { CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: bytes) }
        var sb: CMSampleBuffer?
        CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: nil, dataBuffer: block, formatDescription: format, sampleCount: n,
                                                             presentationTimeStamp: CMTime(value: a0, timescale: 48000), packetDescriptions: nil, sampleBufferOut: &sb)
        return sb
    }
}
