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
    var shortcuts: [KeyStroke]?     // 按的快捷键（只记带 ⌘⌃⌥ 的组合和 Esc、方向键这类功能键，不记打的字）；旧素材没有
    var camera: CameraTrack?        // 单独录的摄像头（camera.mp4）；没开摄像头的素材没有
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
    let keyCap = KeyCapture()                   // 快捷键（显示键帽用）

    init(size: CGSize, pixelsPerPoint: Double, area: @escaping () -> CGRect?) {
        self.size = size; ppp = pixelsPerPoint; areaOf = area
    }

    func start() {
        sampleShape()
        shapeTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in self?.sampleShape() }
        lastKeyAgo = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: .keyDown)
        keyCap.start()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .nanoseconds(1_000_000_000 / 120), leeway: .milliseconds(1))
        t.setEventHandler { [weak self] in self?.sample() }
        t.resume(); timer = t
    }
    func stop() {
        shapeTimer?.invalidate(); shapeTimer = nil
        timer?.cancel(); timer = nil
        queue.sync {}
        keyCap.stop()
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
        if ago < lastKeyAgo - 0.001 { keys.append(now - ago); keyCap.poll() }   // 「距上次按键」变短了 = 又按了一下（没监听权限时顺便查是哪个组合键）
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
        t.shortcuts = keyCap.strokes.map { KeyStroke(t: r($0.t - start, 1000), keys: $0.keys) }
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
    var minBitRate = 10_000_000    // 码率下限
    var maxShort: Double?          // 成片短边最多多少像素（nil = 原始大小，画面 1:1）
    var maxWidth: Double?          // 成片宽度最多多少（GIF 用）
    var gif = false                // 导出 GIF

    // 按导出选项设好：分辨率、帧率、画质、格式
    init(_ o: ExportOptions? = nil) {
        guard let o else { return }
        if o.format == .gif {
            gif = true; fps = 15; maxWidth = 960
        } else {
            fps = o.fps == 30 ? 30 : 60
            maxShort = o.res.shortEdge
            switch o.quality {
            case .high: bitsPerPixel = 0.12; minBitRate = 10_000_000
            case .standard: bitsPerPixel = 0.07; minBitRate = 6_000_000
            case .small: bitsPerPixel = 0.035; minBitRate = 3_000_000
            }
        }
    }
}

// 剪辑窗口「导出」旁边的选项；记住上次选的
struct ExportOptions: Codable, Equatable {
    enum Res: String, Codable, CaseIterable {
        case original, uhd = "4k", qhd = "1440p", hd = "1080p", sd = "720p"
        var shortEdge: Double? { switch self { case .original: nil; case .uhd: 2160; case .qhd: 1440; case .hd: 1080; case .sd: 720 } }
    }
    enum Quality: String, Codable, CaseIterable { case high, standard, small }
    enum Format: String, Codable, CaseIterable { case video, gif }
    // 默认 1440p：推近时字照样清楚（放大 2 倍时原画面 1 像素≈成片 1.3 像素），像素只有 Retina 原始大小的四成多，导出快一倍多、文件小
    var res = Res.qhd
    var fps = 60
    var quality = Quality.high
    var format = Format.video
    var copyToClipboard = false

    init() {}
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = ExportOptions()
        res = (try? c.decodeIfPresent(Res.self, forKey: .res)) ?? z.res
        fps = (try? c.decodeIfPresent(Int.self, forKey: .fps)) ?? z.fps
        quality = (try? c.decodeIfPresent(Quality.self, forKey: .quality)) ?? z.quality
        format = (try? c.decodeIfPresent(Format.self, forKey: .format)) ?? z.format
        copyToClipboard = (try? c.decodeIfPresent(Bool.self, forKey: .copyToClipboard)) ?? z.copyToClipboard
    }
    private static let key = "studioExportOptions"
    static var saved: ExportOptions {
        get { UserDefaults.standard.data(forKey: key).flatMap { try? JSONDecoder().decode(ExportOptions.self, from: $0) } ?? ExportOptions() }
        set { UserDefaults.standard.set(try? JSONEncoder().encode(newValue), forKey: key) }
    }
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
    enum Aspect: String, Codable, CaseIterable { case original, wide = "16:9", tall = "9:16", square = "1:1", classic = "4:3", custom }
    enum Background: String, Codable, CaseIterable { case transparent, color, gradient, wallpaper, image }
    enum Blur: String, Codable, CaseIterable { case off, light, medium, strong }
    enum Click: String, Codable, CaseIterable { case soft, mouse, thock, wood, pop, tick }

    var aspect = Aspect.original
    var aspectW = 21.0, aspectH = 9.0   // 自定义比例（宽:高）
    var background = Background.transparent
    var color = RGB(0.96, 0.96, 0.97)
    var gradient0 = RGB(0.36, 0.40, 0.96), gradient1 = RGB(0.86, 0.43, 0.78)
    var gradientAngle = 135.0      // 度：0 = 从左到右，90 = 从下到上
    var wallpaper = ""             // 系统桌面图片的路径
    var imagePath = ""             // 自己选的图片
    var imageBookmark: Data?       // 图片被挪动后靠它找回来
    var padding = 0.06             // 画面四周留的边：占画布短边 0–20%
    var cornerRadius = 14.0        // 圆角（点）0–100
    var shadow = 0.45              // 阴影深浅 0–1
    var shadowBlur = 0.33          // 阴影虚化 0–1
    var cursorScale = 1.5          // 鼠标大小 0.5–3 倍
    var smoothing = 0.7            // 鼠标顺滑程度：0 = 不平滑，1 = 最顺
    var hideIdleCursor = false     // 鼠标不动几秒后自动隐藏
    var hideAfter = 2.0            // 1–5 秒
    var clickSound = false         // 点击时加一声轻响
    var clickStyle = Click.soft    // 哪种声音：轻点 / 鼠标咔哒 / 低沉 / 木头 / 气泡 / 清脆滴答
    var typeSound = false          // 打字时每按一下加一声键盘声
    var typeStyle = Typing.mechanical
    var typeMuteFast = false        // 加速的片段不要打字声
    enum Typing: String, Codable, CaseIterable { case mechanical, laptop, clicky, thock }   // 都是真键盘录音（清脆 / 低沉由机械键盘升降调）
    var ripple = true              // 点击波纹
    var rippleStyle = Ripple.ring  // 波纹样子
    var rippleWidth = 1.0          // 圆圈类波纹的线粗（倍数 0.4–3）
    enum Ripple: String, Codable, CaseIterable { case ring, glass, double, pulse, glow, focus }
    var blur = Blur.light          // 运动模糊：只在镜头快速移动时有
    enum KeysAt: String, Codable, CaseIterable { case bottom, bottomLeft, bottomRight }
    var showKeys = false           // 画面上显示按的快捷键（⌘ ⇧ C 这种键帽）
    var keysAt = KeysAt.bottom
    var keysSize = 0.045           // 键帽高约占画布短边几成（0.03–0.08）
    var keysDark = true            // 深色键帽（白字）/ 浅色键帽（黑字）
    var zoomSpeed = 1.0            // 推近速度：镜头弹簧快慢的倍数（慢 0.6 / 标准 1 / 快 1.7）
    var cursorLoop = false         // 结尾鼠标回到起点（做循环视频）
    var cursorLoopSec = 1.0        // 最后几秒往回走：0.5–2
    var loopZoomOut = true         // 回到起点时镜头也拉回全景

    init() {}
    enum CodingKeys: String, CodingKey {
        case aspect, aspectW, aspectH, background, color, gradient0, gradient1, gradientAngle, wallpaper, imagePath, imageBookmark, padding, cornerRadius,
             shadow, shadowBlur, cursorScale, smoothing, hideIdleCursor, hideAfter, clickSound, clickStyle, typeSound, typeStyle, typeMuteFast, ripple, rippleStyle, rippleWidth, blur,
             showKeys, keysAt, keysSize, keysDark, zoomSpeed, cursorLoop, cursorLoopSec, loopZoomOut
    }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = StudioLook()
        func v<T: Decodable>(_ k: CodingKeys, _ def: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? def }
        aspect = v(.aspect, z.aspect); aspectW = v(.aspectW, z.aspectW); aspectH = v(.aspectH, z.aspectH); background = v(.background, z.background); color = v(.color, z.color)
        gradient0 = v(.gradient0, z.gradient0); gradient1 = v(.gradient1, z.gradient1); gradientAngle = v(.gradientAngle, z.gradientAngle)
        wallpaper = v(.wallpaper, z.wallpaper); imagePath = v(.imagePath, z.imagePath); imageBookmark = v(.imageBookmark, z.imageBookmark)
        padding = v(.padding, z.padding); cornerRadius = v(.cornerRadius, z.cornerRadius); shadow = v(.shadow, z.shadow)
        shadowBlur = v(.shadowBlur, z.shadowBlur); cursorScale = v(.cursorScale, z.cursorScale); smoothing = v(.smoothing, z.smoothing)
        hideIdleCursor = v(.hideIdleCursor, z.hideIdleCursor); hideAfter = v(.hideAfter, z.hideAfter); clickSound = v(.clickSound, z.clickSound)
        clickStyle = v(.clickStyle, z.clickStyle); typeSound = v(.typeSound, z.typeSound); typeStyle = v(.typeStyle, z.typeStyle); typeMuteFast = v(.typeMuteFast, z.typeMuteFast)
        ripple = v(.ripple, z.ripple); rippleStyle = v(.rippleStyle, z.rippleStyle); rippleWidth = v(.rippleWidth, z.rippleWidth); blur = v(.blur, z.blur)
        showKeys = v(.showKeys, z.showKeys); keysAt = v(.keysAt, z.keysAt); keysSize = v(.keysSize, z.keysSize); keysDark = v(.keysDark, z.keysDark)
        zoomSpeed = v(.zoomSpeed, z.zoomSpeed); cursorLoop = v(.cursorLoop, z.cursorLoop); cursorLoopSec = v(.cursorLoopSec, z.cursorLoopSec)
        loopZoomOut = v(.loopZoomOut, z.loopZoomOut)
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
        switch aspect {
        case .original: nil; case .wide: 16.0 / 9; case .tall: 9.0 / 16; case .square: 1; case .classic: 4.0 / 3
        case .custom: aspectW > 0 && aspectH > 0 ? min(max(aspectW / aspectH, 0.25), 4) : nil
        }
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
struct StudioPreset: Codable, Equatable, Identifiable {
    var name: String; var look: StudioLook
    var id: String { name }
    // 内置预设的 name 以「@」开头（固定、不随语言变，存默认时用它），显示用 title
    var builtIn: Bool { name.hasPrefix("@") }
    var title: String { Self.titles[name] ?? name }
    static let titles: [String: String] = [
        "@transparent": L("透明背景", "Transparent"), "@douyin": L("抖音竖屏", "Vertical (TikTok)"),
        "@tutorial": L("教程横屏", "Tutorial (16:9)"), "@square": L("方形", "Square"),
    ]
    // 内置预设：只读，不写进 presets.json
    static let builtIns: [StudioPreset] = {
        let g = StudioLook.gradients
        var tall = StudioLook(); tall.aspect = .tall; tall.background = .gradient
        tall.gradient0 = g[2].a; tall.gradient1 = g[2].b; tall.gradientAngle = 160; tall.cursorScale = 1.8; tall.padding = 0.05
        var wide = StudioLook(); wide.aspect = .wide; wide.background = .gradient
        wide.gradient0 = g[1].a; wide.gradient1 = g[1].b; wide.gradientAngle = 135
        var square = StudioLook(); square.aspect = .square; square.background = .gradient
        square.gradient0 = g[3].a; square.gradient1 = g[3].b; square.gradientAngle = 135
        return [StudioPreset(name: "@transparent", look: StudioLook()), StudioPreset(name: "@douyin", look: tall),
                StudioPreset(name: "@tutorial", look: wide), StudioPreset(name: "@square", look: square)]
    }()
}
enum PresetStore {
    struct File: Codable {
        var presets: [StudioPreset] = []; var defaultName: String?
        var all: [StudioPreset] { StudioPreset.builtIns + presets }
        func find(_ name: String) -> StudioPreset? { all.first { $0.name == name } }
        // 新录的工程用哪个：没设过 = 透明背景
        var defaultKey: String { defaultName.flatMap { find($0) != nil ? $0 : nil } ?? "@transparent" }
    }
    static var url: URL {
        let d = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "Recorder")
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d.appendingPathComponent("presets.json")
    }
    static func load() -> File { (try? JSONDecoder().decode(File.self, from: Data(contentsOf: url))) ?? File() }
    static func save(_ f: File) {
        var f = f; f.presets.removeAll { $0.builtIn }   // 内置的不写进文件
        let e = JSONEncoder(); e.outputFormatting = [.prettyPrinted, .sortedKeys]; try? e.encode(f).write(to: url, options: .atomic)
    }
    static var defaultLook: StudioLook { let f = load(); return f.find(f.defaultKey)?.look ?? StudioLook() }
}

// 画布和画面在画布上的位置（Core Image 坐标：左下角为原点、y 朝上）
struct StudioLayout {
    let W: Double, H: Double       // 原画面像素
    let cw: Double, ch: Double     // 画布像素
    let s: Double                  // 原画面缩放到画布上的比例（尽量 1:1，字不糊）
    let card: CGRect               // 画面（圆角卡片）在画布上的位置
    let radius: Double

    init(track: Track, look: StudioLook, maxShort: Double? = nil, maxWidth: Double? = nil) {
        W = Double(track.width); H = Double(track.height)
        let f = min(max(look.padding, 0), 0.2), a = look.ratio ?? W / H
        // 先按「画面 1:1 放得下、四周留 f 的边」算画布
        var cw: Double, ch: Double
        if a >= 1 { ch = max(H / (1 - 2 * f), W / (a - 2 * f)); cw = ch * a }
        else { cw = max(W / (1 - 2 * f), H / (1 / a - 2 * f)); ch = cw / a }
        // H.264 硬件编码上限：边长 4096、约 890 万像素；超了整体缩小
        var k = min(1, 4096 / max(cw, ch), (8_800_000 / (cw * ch)).squareRoot())
        // 导出选了较小的分辨率：整体按比例缩（只缩不放）。所有东西都按缩小后的画布画，不是画大了再缩
        if let m = maxShort { k = min(k, m / min(cw, ch)) }
        if let m = maxWidth { k = min(k, m / cw) }
        cw = (cw * k / 2).rounded(.down) * 2; ch = (ch * k / 2).rounded(.down) * 2
        self.cw = cw; self.ch = ch
        let pad = f * min(cw, ch)
        s = min((cw - 2 * pad) / W, (ch - 2 * pad) / H)
        card = CGRect(x: ((cw - W * s) / 2).rounded(), y: ((ch - H * s) / 2).rounded(), width: W * s, height: H * s)
        radius = min(max(0, look.cornerRadius), 100) * track.pixelsPerPoint * s
    }
    var canvas: CGRect { CGRect(x: 0, y: 0, width: cw, height: ch) }
    var toScene: CGAffineTransform { CGAffineTransform(a: s, b: 0, c: 0, d: s, tx: card.minX, ty: card.minY) }
}

// 临界阻尼弹簧：往目标走，起步快、到位前慢下来，不过冲（精确解，步长多大都稳）
struct Spring {
    var x: Double, v = 0.0
    var w: Double                  // 越大越快；大约 4/w 秒到位（推近速度可以中途改）
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
    var speed: Double?                 // 这一段的推近速度（nil = 跟全局「推近速度」）
}

// 中间剪掉的一段 / 快放的一段（素材时间）
struct CutSegment: Codable, Equatable, Identifiable {
    enum Mode: String, Codable { case cut, speed }
    var id = UUID()
    var start: Double
    var end: Double
    var mode = Mode.cut
    var rate = 4.0                       // 快放几倍：2 / 4 / 8
}

// 声音：录到的电脑声音 / 麦克风各自的音量（0–2），点击声音量，一键静音；讲解总音量、讲解时压低原声音
struct AudioSettings: Codable, Equatable {
    var system = 1.0
    var mic = 1.0
    var clicks = 1.0
    var muted = false
    var voice = 1.0                     // 后来录的讲解：总音量
    var duck = true                     // 有讲解的地方把原声音压低（约 -12 dB）
    var denoise = Denoise.Level.off     // 降噪：麦克风和讲解（系统自带的声音隔离，本机处理）
    var denoiseSystem = false           // 电脑声音也降噪（默认不动：音乐、视频里的声音会被当成杂音削掉）
    init() {}
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self), z = AudioSettings()
        system = (try? c.decodeIfPresent(Double.self, forKey: .system)) ?? z.system
        mic = (try? c.decodeIfPresent(Double.self, forKey: .mic)) ?? z.mic
        clicks = (try? c.decodeIfPresent(Double.self, forKey: .clicks)) ?? z.clicks
        muted = (try? c.decodeIfPresent(Bool.self, forKey: .muted)) ?? z.muted
        voice = (try? c.decodeIfPresent(Double.self, forKey: .voice)) ?? z.voice
        duck = (try? c.decodeIfPresent(Bool.self, forKey: .duck)) ?? z.duck
        denoise = (try? c.decodeIfPresent(Denoise.Level.self, forKey: .denoise)) ?? z.denoise
        denoiseSystem = (try? c.decodeIfPresent(Bool.self, forKey: .denoiseSystem)) ?? z.denoiseSystem
    }
}

// 剪辑时另外录的一段讲解（素材夹 voice/ 下的 m4a）。
// 锚点 start 是素材时间：讲解「从素材这一刻开始」，之后按成片时间（真实时间）一直往后放 length 秒——
// 录的时候本来就是看着剪过的预览边看边说的。之后在讲解前面加 / 改剪掉、快放，讲解跟着它开头那一刻的画面一起挪；
// 讲解中间那段画面再被剪掉 / 快放，讲解本身不剪不变速，照样连着放完
struct VoiceClip: Codable, Equatable, Identifiable {
    var id = UUID()
    var file: String                    // 相对素材夹：voice/xxx.m4a
    var start: Double                   // 素材时间
    var trimIn = 0.0                    // 从录音的第几秒开始用
    var trimOut: Double                 // 用到录音的第几秒
    var fileLength: Double              // 录音文件总长（拖两头时别超出）
    var volume = 1.0                    // 这一段的音量 0–2
    var length: Double { max(0, trimOut - trimIn) }

    // 成片里从哪到哪（成片时间）
    func outRange(_ m: TimeMap) -> (start: Double, end: Double) { let s = m.out(start); return (s, s + length) }
    // 时间轴上画到哪（素材时间）：讲解结尾那一刻对应素材的哪儿
    func srcEnd(_ m: TimeMap) -> Double { m.src(m.out(start) + length) }

    // 新录的一段放进来：和旧的重叠的部分以新的为准（旧段被剪短、剪成两段或整段去掉），按成片时间算
    static func place(_ n: VoiceClip, into list: [VoiceClip], map m: TimeMap) -> [VoiceClip] {
        let (ns, ne) = n.outRange(m)
        var out: [VoiceClip] = []
        for o in list {
            let (os, oe) = o.outRange(m)
            if oe <= ns + 1e-6 || os >= ne - 1e-6 { out.append(o); continue }        // 不重叠
            if os < ns {                                                             // 前面露出来的一截
                var a = o; a.trimOut = o.trimIn + (ns - os)
                if a.length >= 0.05 { out.append(a) }
            }
            if oe > ne {                                                             // 后面露出来的一截
                var b = o; b.id = os < ns ? UUID() : o.id   // 被剪成两段时后一截换个新 id
                b.trimIn = o.trimIn + (ne - os); b.start = m.src(ne)
                if b.length >= 0.05 { out.append(b) }
            }
        }
        out.append(n)
        return out.sorted { $0.outRange(m).start < $1.outRange(m).start }
    }
}

extension StudioProject {
    // 这一刻（素材时间）的按键要不要响：总开关开着、不在静音段里、（勾了的话）不在加速段里
    func keySounds(at t: Double) -> Bool {
        guard look.typeSound else { return false }
        if keyMutes.contains(where: { $0.count == 2 && t >= $0[0] && t < $0[1] }) { return false }
        if look.typeMuteFast && cuts.contains(where: { $0.mode == .speed && t >= $0.start && t < $0.end }) { return false }
        return true
    }
}

struct StudioProject: Codable, Equatable {
    var segments: [ZoomSegment] = []
    var trimStart = 0.0
    var trimEnd = 0.0
    var look = StudioLook()
    var segmentsEdited = false          // 推近段手动改过：以后自动规则变了也不重算；没改过的打开时按最新规则重排
    var cuts: [CutSegment] = []         // 中间剪掉 / 快放的段（Windows 版还没有，打开后会原样留着）
    var audio = AudioSettings()
    var voiceovers: [VoiceClip] = []    // 后来录的讲解（Windows 版还没有，打开后会原样留着）
    var captions: [Caption] = []        // 字幕（素材时间；Windows 版还没有，打开后会原样留着）
    var captionStyle = CaptionStyle()
    var camera: CameraSettings?         // 摄像头样式；素材里有摄像头、工程里还没有时按录制时的小窗位置生成（Windows 版还没有）
    var layouts: [CameraLayoutSeg] = []  // 布局轨：不在任何一段里 = 小窗
    var redactions: [Redaction] = []    // 遮挡（模糊 / 马赛克 / 色块；原画面位置、素材时间；Windows 版还没有）
    var annotations: [Annotation] = []  // 标注（箭头 / 框 / 圈 / 高亮 / 文字；Windows 版还没有）
    var music = MusicTrack()            // 背景音乐（成片时间；Windows 版还没有）
    var splits: [Double] = []           // 剪辑轨上的分割点（素材时间；只影响怎么切成一段段，不改成片）
    var keyMutes: [[Double]] = []       // 这些段（素材时间 [起, 止]）里不要打字声

    init(segments: [ZoomSegment] = [], trimStart: Double = 0, trimEnd: Double = 0, look: StudioLook = StudioLook()) {
        self.segments = segments; self.trimStart = trimStart; self.trimEnd = trimEnd; self.look = look
    }
    enum CodingKeys: String, CodingKey { case segments, trimStart, trimEnd, look, segmentsEdited, cuts, audio, voiceovers, captions, captionStyle, camera, layouts, redactions, annotations, music, splits, keyMutes }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        segments = try c.decodeIfPresent([ZoomSegment].self, forKey: .segments) ?? []
        trimStart = try c.decodeIfPresent(Double.self, forKey: .trimStart) ?? 0
        trimEnd = try c.decodeIfPresent(Double.self, forKey: .trimEnd) ?? 0
        look = (try? c.decodeIfPresent(StudioLook.self, forKey: .look)) ?? StudioLook()   // 旧工程没有样式：用默认
        segmentsEdited = try c.decodeIfPresent(Bool.self, forKey: .segmentsEdited) ?? false   // 旧工程没有这个标记：当作没改过
        cuts = (try? c.decodeIfPresent([CutSegment].self, forKey: .cuts)) ?? []
        audio = (try? c.decodeIfPresent(AudioSettings.self, forKey: .audio)) ?? AudioSettings()
        voiceovers = (try? c.decodeIfPresent([VoiceClip].self, forKey: .voiceovers)) ?? []
        captions = (try? c.decodeIfPresent([Caption].self, forKey: .captions)) ?? []
        captionStyle = (try? c.decodeIfPresent(CaptionStyle.self, forKey: .captionStyle)) ?? CaptionStyle()
        camera = try? c.decodeIfPresent(CameraSettings.self, forKey: .camera)
        layouts = (try? c.decodeIfPresent([CameraLayoutSeg].self, forKey: .layouts)) ?? []
        redactions = (try? c.decodeIfPresent([Redaction].self, forKey: .redactions)) ?? []
        annotations = (try? c.decodeIfPresent([Annotation].self, forKey: .annotations)) ?? []
        music = (try? c.decodeIfPresent(MusicTrack.self, forKey: .music)) ?? MusicTrack()
        splits = (try? c.decodeIfPresent([Double].self, forKey: .splits)) ?? []
        keyMutes = (try? c.decodeIfPresent([[Double]].self, forKey: .keyMutes)) ?? []
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
            if p.camera == nil, let ct = track.camera { p.camera = CameraSettings(from: ct) }
            return p
        }
        var p = StudioProject(segments: StudioPlan.autoSegments(track, duration: duration, zoom: zoom), trimStart: 0, trimEnd: duration,
                              look: PresetStore.defaultLook)   // 新工程用默认预设
        if let ct = track.camera { p.camera = CameraSettings(from: ct) }
        return p
    }
    func save(_ dir: URL) throws {
        let enc = JSONEncoder(); enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: Self.url(dir), options: .atomic)
    }
}

// ---- 素材时间 ↔ 成片时间：剪掉的段压成 0 长，快放的段按倍数压短；第一处剪辑之前两边一样（修剪的起点也不变） ----
struct TimeMap {
    struct Piece { let start: Double, end: Double, rate: Double }   // rate 0 = 剪掉
    let pieces: [Piece]
    var isIdentity: Bool { pieces.isEmpty }

    init(_ p: StudioProject) {
        var out: [Piece] = []
        for c in p.cuts.sorted(by: { $0.start < $1.start }) {
            let s = max(c.start, p.trimStart, out.last?.end ?? -1), e = min(c.end, p.trimEnd)
            guard e - s > 1.0 / 240 else { continue }
            out.append(Piece(start: s, end: e, rate: c.mode == .cut ? 0 : max(1, c.rate)))
        }
        pieces = out
    }
    private static func outLen(_ p: Piece) -> Double { p.rate == 0 ? 0 : (p.end - p.start) / p.rate }

    // 素材时间 → 成片时间
    func out(_ s: Double) -> Double {
        var d = 0.0
        for p in pieces {
            if s <= p.start { break }
            let e = min(s, p.end), len = e - p.start
            d += len - (p.rate == 0 ? 0 : len / p.rate)
        }
        return s - d
    }
    // 成片时间 → 素材时间（落在剪掉处时给剪掉段的终点）
    func src(_ o: Double) -> Double {
        var d = 0.0
        for p in pieces {
            let os = p.start - d, ol = Self.outLen(p)
            if o < os { return o + d }
            if o < os + ol { return p.start + (o - os) * p.rate }
            d += (p.end - p.start) - ol
        }
        return o + d
    }
    func piece(at s: Double) -> Piece? { pieces.first { s >= $0.start && s < $0.end } }
    func inCut(_ s: Double) -> Bool { piece(at: s)?.rate == 0 }

    // 轨迹换到成片时间：剪掉段里的鼠标、点击、按键去掉，其余按新时间
    func warp(_ t: Track) -> Track {
        guard !isIdentity else { return t }
        var w = t
        w.moves = t.moves.filter { !inCut($0[0]) }.map { var m = $0; m[0] = out(m[0]); return m }
        w.clicks = t.clicks.filter { !inCut($0[0]) }.map { var c = $0; c[0] = out(c[0]); return c }
        w.keys = t.keys.filter { !inCut($0) }.map { out($0) }
        return w
    }
    // 工程换到成片时间：推近段起止按新时间（整段被剪掉的去掉），修剪终点按新时间
    func warp(_ p: StudioProject) -> StudioProject {
        guard !isIdentity else { return p }
        var w = p
        w.segments = p.segments.compactMap { g in
            var g = g; g.start = out(g.start); g.end = out(g.end)
            return g.end - g.start > 0.1 ? g : nil
        }
        w.trimEnd = out(p.trimEnd)
        w.cuts = []
        return w
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
    // range = 成片从哪导到哪（修剪起止）：「结尾鼠标回到起点」用
    static func make(_ tr: Track, _ L: StudioLayout, _ st: StudioStyle, look: StudioLook, segments: [ZoomSegment], frames n: Int, duration: Double,
                     range: (Double, Double)? = nil) -> [FramePlan] {
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
        // 推近速度：镜头弹簧的强度。全局按样式；某一段单独设了就用它（段结束后拉远的那 2 秒也按它，免得拉远突然变速）
        let baseW = 4 * min(max(look.zoomSpeed, 0.3), 3)
        var lz = Spring(x: 0, w: baseW), cx = Spring(x: W / 2, w: baseW), cy = Spring(x: H / 2, w: baseW)
        var segW = baseW, segWUntil = -1.0
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
            let inSeg = si < segs.count && segs[si].start <= t
            if inSeg { segW = segs[si].speed.map { 4 * min(max($0, 0.3), 3) } ?? baseW; segWUntil = segs[si].end + 2 }
            let w = inSeg || t < segWUntil ? segW : baseW
            lz.w = w; cx.w = w; cy.w = w
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
        if look.cursorLoop, let range { loopBack(&out, range: range, fps: Double(st.fps), seconds: look.cursorLoopSec, zoomOut: look.loopZoomOut, L) }
        return out
    }

    // 结尾鼠标回到起点：最后 seconds 秒，鼠标从当时的位置缓缓（缓进缓出）走回修剪起点那一帧的位置；
    // zoomOut 时镜头同样缓缓回到全景，最后一帧和第一帧接得上，做循环视频
    static func loopBack(_ out: inout [FramePlan], range: (Double, Double), fps: Double, seconds: Double, zoomOut: Bool, _ L: StudioLayout) {
        guard !out.isEmpty else { return }
        let first = min(max(0, Int((range.0 * fps).rounded())), out.count - 1)
        let last = min(out.count - 1, max(first, Int((range.1 * fps).rounded()) - 1))   // 导出的最后一帧
        let len = max(1, Int((min(max(seconds, 0.2), 4) * fps).rounded()))
        let from = max(first + 1, last - len)
        guard last > from else { return }
        let home = out[first]
        let fullX = L.cw / 2, fullY = L.ch / 2
        for i in from...last {
            let u = Double(i - from) / Double(last - from)
            let e = u * u * (3 - 2 * u)                         // 缓进缓出
            var f = out[i]
            f.mx += (home.mx - f.mx) * e; f.my += (home.my - f.my) * e
            f.alpha += (max(home.alpha, 1) - f.alpha) * e      // 回到起点时鼠标是显示着的
            if home.shape >= 0 { f.shape = e > 0.5 ? home.shape : (f.shape >= 0 ? f.shape : home.shape) }
            if zoomOut {
                let lz = log(f.z) * (1 - e)                    // 放大倍数按对数插值回 1
                f.z = exp(lz); f.sx += (fullX - f.sx) * e; f.sy += (fullY - f.sy) * e
                let ex = L.cw / (2 * f.z), ey = L.ch / (2 * f.z)
                f.sx = min(max(f.sx, ex), L.cw - ex); f.sy = min(max(f.sy, ey), L.ch - ey)
            }
            out[i] = f
        }
    }
}

// ---- 导出 ----
enum Studio {
    static let failure = { (s: String) in NSError(domain: "Studio", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }

    // 成片文件名：透明背景存 HEVC .mov，其他背景存 H.264 .mp4
    static func output(_ out: URL, look: StudioLook, gif: Bool = false) -> URL {
        out.deletingPathExtension().appendingPathExtension(gif ? "gif" : look.transparent ? "mov" : "mp4")
    }

    // 返回实际写出的文件（扩展名跟着背景走）
    @discardableResult
    static func render(project dir: URL, to out: URL, style: StudioStyle = StudioStyle(), edits: StudioProject? = nil, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let job = try await StudioJob(project: dir, out: out, style: style, project: edits, progress: progress)
        // 取消导出（Task 被取消）：合成线程停下、写了一半的文件删掉
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
                DispatchQueue.global(qos: .userInitiated).async {
                    do { try job.run(); c.resume() } catch { c.resume(throwing: error) }
                }
            }
        } onCancel: { job.cancel() }
        return job.outURL
    }

    // 自测：STUDIO=素材文件夹 [STUDIO_OUT=输出路径] [STUDIO_ASPECT=16:9] [STUDIO_BLUR=off|light|medium|strong] → 只导出，不录，打印用时后退出
    // 样式按素材夹里的 project.json；STUDIO_ASPECT / STUDIO_BLUR 临时盖掉工程里的设置
    @MainActor static func selfTest() -> Bool {
        let env = ProcessInfo.processInfo.environment
        guard let p = env["STUDIO"] else { return false }
        let dir = URL(fileURLWithPath: p)
        let out = URL(fileURLWithPath: env["STUDIO_OUT"] ?? dir.appendingPathComponent("studio.mov").path)
        // STUDIO_RES=original|4k|1440p|1080p|720p STUDIO_FPS=30|60 STUDIO_QUALITY=high|standard|small STUDIO_FORMAT=video|gif（不给 = 原始大小、60 帧、高画质）
        var o: ExportOptions?
        if env["STUDIO_RES"] != nil || env["STUDIO_FPS"] != nil || env["STUDIO_QUALITY"] != nil || env["STUDIO_FORMAT"] != nil {
            var x = ExportOptions(); x.res = .original
            if let v = env["STUDIO_RES"].flatMap(ExportOptions.Res.init(rawValue:)) { x.res = v }
            if let v = env["STUDIO_FPS"].flatMap(Int.init) { x.fps = v }
            if let v = env["STUDIO_QUALITY"].flatMap(ExportOptions.Quality.init(rawValue:)) { x.quality = v }
            if let v = env["STUDIO_FORMAT"].flatMap(ExportOptions.Format.init(rawValue:)) { x.format = v }
            o = x
        }
        var st = StudioStyle(o)
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
                // STUDIO_CANCEL=秒：导出开始这么久后取消，看能不能干净停下（文件删掉）
                if let c = env["STUDIO_CANCEL"].flatMap(Double.init) {
                    let job = Task { try await render(project: dir, to: out, style: st, edits: edits) { _ in } }
                    try? await Task.sleep(for: .seconds(c)); job.cancel()
                    let r = await job.result
                    print("STUDIO CANCEL", String(format: "%.1fs", Date().timeIntervalSince(t0)), (try? r.get()) == nil ? "cancelled" : "finished")
                    NSApp.terminate(nil); return
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
    private let rippleArt: [StudioLook.Ripple: CIImage]
    private(set) var background: CIImage!   // 背景 + 卡片阴影（分屏布局时露出来的地方也用它）
    private var mask: CIImage!

    init(project dir: URL, track: Track, style: StudioStyle, look: StudioLook) {
        self.track = track; st = style; self.look = look
        lay = StudioLayout(track: track, look: look, maxShort: style.maxShort, maxWidth: style.maxWidth)
        let h = Double(track.height)
        clicks = track.clicks.map { ($0[0], $0[1], h - $0[2]) }
        arrow = Self.drawArrow()
        ring = Self.drawRing()
        rippleArt = Dictionary(uniqueKeysWithValues: StudioLook.Ripple.allCases.map { ($0, Self.drawRipple($0, width: look.rippleWidth)) })
        for (i, c) in track.cursors.enumerated() where !c.arrow {
            if let img = CIImage(contentsOf: dir.appendingPathComponent(c.file)), c.w > 0 {
                cursorArt[i] = (img, img.extent.width / c.w, c.hotX, c.hotY)
            }
        }
        prepareBackground()
    }

    func plan(_ project: StudioProject, duration: Double) -> [FramePlan] {
        StudioPlan.make(track, lay, st, look: look, segments: project.segments, frames: max(1, Int(duration * Double(st.fps))), duration: duration,
                        range: (project.trimStart, project.trimEnd > project.trimStart ? project.trimEnd : duration))
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
    // overlay = 遮挡和标注（按素材时间 srcTime 查；没给就当素材时间 = 成片时间）
    func frame(_ i: Int, plan: [FramePlan], source: CIImage, overlay: OverlayLayer? = nil, srcTime: Double? = nil, motionBlur: Bool = true) -> CIImage {
        let f = plan[i], W = lay.W, H = lay.H
        var src = source
        let bh = src.extent.height   // 高度是奇数时底下补过 1 像素，切掉
        src = src.cropped(to: CGRect(x: 0, y: bh - H, width: W, height: H)).transformed(by: CGAffineTransform(translationX: 0, y: -(bh - H)))
        // 遮挡、标注画在原画面上（推近时跟着一起放大；这样整幅只缩放一次，加了遮挡字也不变软）；鼠标和波纹在它们上面
        if let overlay, !overlay.isEmpty {
            src = overlay.apply(src, t: srcTime ?? Double(i) / Double(st.fps), lay: lay, ppp: track.pixelsPerPoint, scale: lay.s * f.z)
        }
        // 成片里原画面缩得比较小（全景 + 导出分辨率小）：先用 Lanczos 缩好再贴，字不发虚、不闪；1:1 附近不碰，结果和原来一样
        var toScene = lay.toScene
        let eff = lay.s * f.z
        if eff < 0.75 {
            src = src.applyingFilter("CILanczosScaleTransform", parameters: [kCIInputScaleKey: eff, kCIInputAspectRatioKey: 1.0])
            toScene = CGAffineTransform(scaleX: 1 / eff, y: 1 / eff).concatenating(toScene)
        }
        var scene = src.transformed(by: toScene).applyingFilter("CIBlendWithAlphaMask", parameters: [
            kCIInputBackgroundImageKey: background!, kCIInputMaskImageKey: mask!,
        ])
        let t = Double(i) / Double(st.fps), ppp = track.pixelsPerPoint
        // 点击波纹：0.5 秒内从 6 点扩到 28 点、渐渐淡掉
        if look.ripple {
            let style = look.rippleStyle, dur = style == .focus ? 0.7 : style == .double ? 0.65 : style == .glass ? 0.6 : 0.5
            for c in clicks where t >= c.t && t < c.t + dur {
                let u = (t - c.t) / dur, e = 1 - pow(1 - u, 3)
                // 每种：(从多大扩到多大（点）, 透明度)；double 是两圈错开 0.15 秒
                var layers: [(r: Double, a: Double)] = []
                switch style {
                case .ring: layers = [(6 + 22 * e, 1 - u)]
                case .glass: layers = []
                case .double:
                    layers = [(6 + 24 * e, 1 - u)]
                    let u2 = max(0, (t - c.t - 0.15) / (dur - 0.15)), e2 = 1 - pow(1 - u2, 3)
                    if u2 > 0 { layers.append((6 + 24 * e2, 1 - u2)) }
                case .pulse: layers = [(10 + 14 * e, (1 - u) * 0.9)]
                case .glow: layers = [(14 + 16 * e, (1 - u) * 0.85)]
                case .focus: layers = [(46 - 30 * e, min(1, u * 4) * (1 - u))]   // 从大往点击处收
                }
                if style == .glass {
                    // 玻璃波纹（像苹果）：一圈折射波往外扩、慢慢变弱；没有边线，只有经过处画面被弯折，外加一点点亮，空白处也看得出
                    let k = ppp * look.cursorScale / 1.5 * lay.s
                    let p = CGPoint(x: c.x, y: c.y).applying(lay.toScene)
                    let r = (6 + 38 * e) * k, w = (10 - 3 * u) * k, amp = 1 - u   // amp：变形强度 0–1
                    scene = OverlayLayer.rippleWarp(scene, center: p, radius: r, width: w, amplitude: amp)
                    let ring = CIFilter(name: "CIRadialGradient", parameters: ["inputCenter": CIVector(x: p.x, y: p.y), "inputRadius0": r, "inputRadius1": r + w,
                                                                             "inputColor0": CIColor(red: 1, green: 1, blue: 1, alpha: 0.16 * (1 - u)), "inputColor1": CIColor(red: 1, green: 1, blue: 1, alpha: 0)])!.outputImage!
                    let inner = CIFilter(name: "CIRadialGradient", parameters: ["inputCenter": CIVector(x: p.x, y: p.y), "inputRadius0": max(0, r - w), "inputRadius1": r,
                                                                              "inputColor0": CIColor(red: 1, green: 1, blue: 1, alpha: 0), "inputColor1": CIColor(red: 1, green: 1, blue: 1, alpha: 0.16 * (1 - u))])!.outputImage!
                    let mask = CIFilter(name: "CIRadialGradient", parameters: ["inputCenter": CIVector(x: p.x, y: p.y), "inputRadius0": r - 0.5, "inputRadius1": r + 0.5,
                                                                             "inputColor0": CIColor.black, "inputColor1": CIColor.white])!.outputImage!
                    let band = ring.applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: inner, kCIInputMaskImageKey: mask])
                        .cropped(to: CGRect(x: p.x - r - w, y: p.y - r - w, width: (r + w) * 2, height: (r + w) * 2))
                    scene = band.composited(over: scene)
                }
                let art = rippleArt[style] ?? ring
                for (rad, al) in layers {
                    let r = rad * ppp * look.cursorScale / 1.5, k = r / 52
                    let img = art.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: al)])
                        .transformed(by: CGAffineTransform(translationX: -64, y: -64).concatenating(CGAffineTransform(scaleX: k, y: k))
                            .concatenating(CGAffineTransform(translationX: c.x, y: c.y)).concatenating(lay.toScene))
                    scene = img.composited(over: scene)
                }
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
        guard motionBlur, let subs = shutter(i, plan: plan) else { return still }   // 暂停在编辑时不糊：只有播放 / 导出才加运动模糊
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
        switch look.blur { case .off: return nil; case .light: angle = 120; case .medium: angle = 220; case .strong: angle = 360 }
        guard plan.count > 1 else { return nil }
        let a = plan[max(i - 1, 0)], b = plan[min(i + 1, plan.count - 1)], f = plan[i]
        let span = Double(min(i + 1, plan.count - 1) - max(i - 1, 0))   // 前后相隔几帧
        let diag = hypot(lay.cw, lay.ch) / 2
        let move = (hypot((b.sx - a.sx) * f.z, (b.sy - a.sy) * f.z) + abs(log(b.z / a.z)) * diag) / span   // 每帧移动的像素
        let blurPx = move * angle / 360
        guard blurPx >= 1.5 else { return nil }
        let n = min(12, max(3, Int(blurPx / 2) + 1))
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
    // 各种点击波纹的底图（128×128，半径 52 的圆为基准，按时间缩放、淡出）
    static func drawRipple(_ style: StudioLook.Ripple, width k: Double = 1) -> CIImage {
        let c = CGContext(data: nil, width: 128, height: 128, bitsPerComponent: 8, bytesPerRow: 0,
                          space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let r = CGRect(x: 12, y: 12, width: 104, height: 104), sp = CGColorSpace(name: CGColorSpace.sRGB)!
        switch style {
        case .ring, .double:
            return drawRing(width: k)
        case .pulse:   // 实心圆点
            c.setFillColor(CGColor(gray: 0, alpha: 0.18)); c.fillEllipse(in: r.insetBy(dx: -3, dy: -3))
            c.setFillColor(CGColor(gray: 1, alpha: 0.85)); c.fillEllipse(in: r)
        case .glow:    // 柔和的光晕
            let g = CGGradient(colorsSpace: sp, colors: [CGColor(srgbRed: 1, green: 0.95, blue: 0.6, alpha: 0.9), CGColor(srgbRed: 1, green: 0.85, blue: 0.3, alpha: 0)] as CFArray, locations: [0, 1])!
            c.drawRadialGradient(g, startCenter: CGPoint(x: 64, y: 64), startRadius: 0, endCenter: CGPoint(x: 64, y: 64), endRadius: 62, options: [])
        case .glass:   // 玻璃波纹直接在画面上做透镜，不用底图
            break
        case .focus:   // 往里收的细圈（像对焦）
            let fr = r.insetBy(dx: 3 * k, dy: 3 * k)
            c.setStrokeColor(CGColor(gray: 0, alpha: 0.3)); c.setLineWidth(7 * k); c.strokeEllipse(in: fr)
            c.setStrokeColor(CGColor(srgbRed: 0.2, green: 0.6, blue: 1, alpha: 1)); c.setLineWidth(4 * k); c.strokeEllipse(in: fr)
        }
        return CIImage(cgImage: c.makeImage()!)
    }
    private static func drawRing(width k: Double = 1) -> CIImage {
        let c = CGContext(data: nil, width: 128, height: 128, bitsPerComponent: 8, bytesPerRow: 0,
                          space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let k = min(max(k, 0.4), 3)
        let r = CGRect(x: 12, y: 12, width: 104, height: 104).insetBy(dx: max(0, (k - 1) * 4), dy: max(0, (k - 1) * 4))   // 粗了往里收，不出图
        c.setFillColor(CGColor(gray: 1, alpha: 0.18)); c.fillEllipse(in: r)
        c.setStrokeColor(CGColor(gray: 0, alpha: 0.25)); c.setLineWidth(10 * k); c.strokeEllipse(in: r)
        c.setStrokeColor(CGColor(gray: 1, alpha: 0.95)); c.setLineWidth(6 * k); c.strokeEllipse(in: r)
        return CIImage(cgImage: c.makeImage()!)
    }
}

// ---- 导出成片：按工程的推近段、修剪和样式逐帧合成；透明背景写 HEVC .mov，其他背景写 H.264 .mp4 ----
final class StudioJob: @unchecked Sendable {
    private let asset: AVAsset                 // raw.mp4；开了降噪时是「画面 + 降噪后的声音」的合成
    private let videoTrack: AVAssetTrack
    private let audioTracks: [AVAssetTrack]
    let outURL: URL
    private let r: StudioRenderer
    private let plan: [FramePlan]
    private let first: Int, last: Int          // 导出第 first..<last 帧（成片时间）
    private let clickTimes: [Double]           // 要加点击声的时刻（秒，成片时间）；不加就是空的
    private let keyTimes: [Double]             // 要加打字声的时刻（同上）
    private let typeStyle: StudioLook.Typing
    private let clickStyle: StudioLook.Click
    private let map: TimeMap                   // 剪掉 / 快放：成片第 i 帧取素材哪个时刻
    private let srcEnd: Double                 // 素材里读到哪（修剪终点，素材时间）
    private let audio: AudioSettings
    private let trackVolumes: [Double]         // 每条音轨的音量（按声道数认：双声道 = 电脑声音，单声道 = 麦克风）
    private let voice: VoiceMix?               // 后来录的讲解（GIF 不要声音）
    private let music: MusicMix?               // 背景音乐（GIF 不要声音）
    private let captions: CaptionLayer         // 字幕：烧进画面（成片时间）
    private let camLayer: CameraLayer?         // 摄像头画中画（素材时间查布局和画面）
    private let overlay: OverlayLayer          // 遮挡、标注（素材时间）
    private let progress: @Sendable (Double) -> Void

    init(project dir: URL, out: URL, style: StudioStyle, project: StudioProject? = nil, progress: @escaping @Sendable (Double) -> Void) async throws {
        let track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
        let raw = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        let duration = try await raw.load(.duration).seconds
        self.progress = progress
        let p = project ?? StudioProject.load(dir, track: track, duration: duration, zoom: style.zoom)
        // 降噪：先把要用的声音处理好（有缓存就很快），再读「画面 + 处理后的声音」
        var src: AVAsset = raw
        if p.audio.denoise != .off && !style.gif {
            try await Denoise.prepare(dir: dir, project: p)
            src = try await Denoise.asset(dir: dir, project: p, raw: raw)
        }
        asset = src
        guard let vt = try await src.loadTracks(withMediaType: .video).first else { throw Studio.failure(L("素材里没有画面", "No video in the recording")) }
        videoTrack = vt
        audioTracks = try await src.loadTracks(withMediaType: .audio)
        outURL = Studio.output(out, look: p.look, gif: style.gif)
        // 中间剪掉 / 快放：轨迹、推近段都换到成片时间再算镜头，镜头在剪切处照样顺着弹簧走，不会突然跳
        map = TimeMap(p)
        let wTrack = map.warp(track), wp = map.warp(p)
        r = StudioRenderer(project: dir, track: wTrack, style: style, look: p.look)
        plan = r.plan(wp, duration: map.isIdentity ? duration : map.out(duration) + 1e-6)   // 换算后的时长有浮点误差，别少算最后一帧
        let fps = Double(style.fps)
        first = min(max(0, Int((p.trimStart * fps).rounded())), plan.count - 1)
        last = max(first + 1, min(plan.count, Int((wp.trimEnd * fps).rounded())))
        srcEnd = p.trimEnd
        clickTimes = p.look.clickSound ? wTrack.clicks.map { $0[0] } : []
        let km = TimeMap(p); keyTimes = track.keys.filter { p.keySounds(at: $0) && !km.inCut($0) }.map { km.out($0) }
        clickStyle = p.look.clickStyle
        typeStyle = p.look.typeStyle
        audio = p.audio
        var vols: [Double] = []
        for t in audioTracks {
            let fd = try await t.load(.formatDescriptions).first
            let ch = fd.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2
            vols.append(ch == 1 ? p.audio.mic : p.audio.system)
        }
        trackVolumes = vols
        voice = style.gif ? nil : try await VoiceMix.load(dir: dir, project: p, map: map)
        music = style.gif ? nil : try await MusicMix.load(dir: dir, project: p, map: map, raw: raw)
        camLayer = CameraLayer(dir: dir, track: track, project: p, canvas: CGSize(width: r.lay.cw, height: r.lay.ch), background: r.background)
        overlay = OverlayLayer(p)
        captions = CaptionLayer(captions: p.captions, style: p.captionStyle, map: map, trim: (p.trimStart, p.trimEnd), canvas: CGSize(width: r.lay.cw, height: r.lay.ch))
        captions.keys = KeyLayer(strokes: track.shortcuts, look: p.look, map: map, trim: (p.trimStart, p.trimEnd),
                                 canvas: CGSize(width: r.lay.cw, height: r.lay.ch), captionsShown: !captions.isEmpty)
    }
    // 第 i 帧（成片时间）对应素材哪个时刻
    private func srcAt(_ i: Int) -> Double { let o = Double(i) / Double(r.st.fps); return map.isIdentity ? o : map.src(o) }
    // 第 i 帧（成片时间）叠上摄像头、字幕
    private func withCaption(_ img: CIImage, _ i: Int) -> CIImage {
        let o = Double(i) / Double(r.st.fps)
        var img = img
        if let camLayer { img = camLayer.compose(img, srcTime: map.isIdentity ? o : map.src(o)) }
        return captions.compose(img, atOut: o)
    }

    private let lock = NSLock()
    private var _cancelled = false
    var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return _cancelled }
    func cancel() { lock.lock(); _cancelled = true; lock.unlock() }
    private static let profiling = ProcessInfo.processInfo.environment["STUDIO_PROFILE"] != nil

    func run() throws {
        if r.st.gif { try runGIF(); return }
        let st = r.st, lay = r.lay, fps = CMTimeScale(st.fps), alpha = r.look.transparent
        let range = CMTimeRange(start: CMTime(value: CMTimeValue(first), timescale: fps), end: CMTime(value: CMTimeValue(last), timescale: fps))
        let reader = try AVAssetReader(asset: asset)
        // 读素材：从修剪起点读到修剪终点（素材时间；有剪掉 / 快放时比成片长）
        reader.timeRange = map.isIdentity ? range : CMTimeRange(start: range.start, end: CMTime(seconds: srcEnd, preferredTimescale: 60000))
        let vOut = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        vOut.alwaysCopiesSampleData = false
        reader.add(vOut)
        var aOut: AVAssetReaderAudioMixOutput?
        let maxVol = trackVolumes.max() ?? 0
        if !audioTracks.isEmpty && !audio.muted && maxVol > 0.001 {
            // 电脑声音、麦克风几条音轨在这里混成一条；各自音量按比例给混音器，超过 100% 的部分后面整体放大（带软限幅）
            let o = AVAssetReaderAudioMixOutput(audioTracks: audioTracks, audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
            ])
            let mix = AVMutableAudioMix()
            mix.inputParameters = zip(audioTracks, trackVolumes).map { t, v in
                let ip = AVMutableAudioMixInputParameters(track: t); ip.setVolume(Float(v / maxVol), at: .zero); return ip
            }
            o.audioMix = mix
            reader.add(o); aOut = o
        }

        try? FileManager.default.removeItem(at: outURL)
        let writer = try AVAssetWriter(outputURL: outURL, fileType: alpha ? .mov : .mp4)
        let w = Int(lay.cw), h = Int(lay.ch)
        var props: [String: Any] = [
            AVVideoAverageBitRateKey: max(st.minBitRate, Int(Double(w * h * st.fps) * st.bitsPerPixel)),
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
        if aOut != nil || !clickTimes.isEmpty || !keyTimes.isEmpty || voice != nil || music != nil {
            let a = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192_000,
            ])
            writer.add(a); aIn = a
        }
        guard reader.startReading() else { throw reader.error ?? Studio.failure("reader") }
        guard writer.startWriting() else { throw writer.error ?? Studio.failure("writer") }

        // 原画面是「有变化才有新帧」，成片固定帧率：每帧用当时最新的那张原画面
        guard var cur = vOut.copyNextSampleBuffer() else { throw Studio.failure(L("素材里没有画面", "No video in the recording")) }
        var next = vOut.copyNextSampleBuffer()
        writer.startSession(atSourceTime: range.start)

        // 流水线：合成线程按顺序取原画面、交给显卡异步画（最多 depth 帧同时在画），
        // 写入这边等最早那帧画完就交给编码器——取画面、显卡合成、编码三件事同时进行，谁也不干等谁
        let depth = 4
        // 从 0 开始再放 depth 个名额：取消时名额没还齐，信号量按初始值 0 判断就不会在释放时崩
        let slots = DispatchSemaphore(value: 0), filled = DispatchSemaphore(value: 0)
        for _ in 0..<depth { slots.signal() }
        var ring: [(i: Int, pb: CVPixelBuffer, task: CIRenderTask?)] = []
        var renderDone = false
        var failure: Error?
        func fail(_ e: Error) { lock.lock(); if failure == nil { failure = e }; lock.unlock() }
        func failed() -> Bool { lock.lock(); defer { lock.unlock() }; return failure != nil || _cancelled }
        var tDec = 0.0, tBuild = 0.0, tWait = 0.0, tAppend = 0.0
        let total = last - first

        let renderThread = Thread { [self] in
            for i in first..<last {
                slots.wait()
                if failed() { break }
                // 成片第 i 帧要素材里哪个时刻的画面（剪掉的跳过、快放的跳着取）
                let pts = map.isIdentity ? CMTime(value: CMTimeValue(i), timescale: fps)
                    : CMTime(seconds: map.src(Double(i) / Double(fps)), preferredTimescale: 60000)
                var t0 = CACurrentMediaTime()
                while let nx = next, nx.presentationTimeStamp <= pts { cur = nx; next = vOut.copyNextSampleBuffer() }
                tDec += CACurrentMediaTime() - t0
                let ok: Bool = autoreleasepool {
                    guard let src = CMSampleBufferGetImageBuffer(cur), let pool = adaptor.pixelBufferPool else { fail(Studio.failure("frame")); return false }
                    var pb: CVPixelBuffer?
                    CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
                    guard let pb else { fail(Studio.failure("pool")); return false }
                    t0 = CACurrentMediaTime()
                    let img = withCaption(r.frame(i, plan: plan, source: CIImage(cvPixelBuffer: src), overlay: overlay, srcTime: srcAt(i)), i)
                    let dest = CIRenderDestination(pixelBuffer: pb)
                    dest.colorSpace = nil
                    let task: CIRenderTask
                    do { task = try r.ctx.startTask(toRender: img, from: lay.canvas, to: dest, at: .zero) } catch { fail(error); return false }
                    tBuild += CACurrentMediaTime() - t0
                    lock.lock(); ring.append((i, pb, task)); lock.unlock()
                    filled.signal()
                    return true
                }
                if !ok { break }
            }
            lock.lock(); renderDone = true; lock.unlock()
            filled.signal()
        }
        renderThread.qualityOfService = .userInitiated

        let group = DispatchGroup()
        group.enter()
        var videoDone = false, written = 0
        vIn.requestMediaDataWhenReady(on: DispatchQueue(label: "studio.video")) { [self] in
            while vIn.isReadyForMoreMediaData && !videoDone {
                filled.wait()
                lock.lock()
                let item = ring.isEmpty ? nil : ring.removeFirst()
                let done = renderDone && item == nil
                lock.unlock()
                guard let item, !done else {
                    videoDone = true; vIn.markAsFinished(); group.leave(); return
                }
                if failed() { slots.signal(); continue }   // 出错 / 取消：剩下的帧丢掉，等合成线程收尾
                var t0 = CACurrentMediaTime()
                do { _ = try item.task?.waitUntilCompleted() } catch { fail(error) }
                tWait += CACurrentMediaTime() - t0; t0 = CACurrentMediaTime()
                if !adaptor.append(item.pb, withPresentationTime: CMTime(value: CMTimeValue(item.i), timescale: fps)) { fail(writer.error ?? Studio.failure("append")) }
                tAppend += CACurrentMediaTime() - t0
                slots.signal()
                written += 1
                if written % 30 == 0 { progress(Double(written) / Double(total)) }
            }
        }
        renderThread.start()
        if let aIn {
            group.enter()
            var audioDone = false
            let clicks = ClickSound(times: clickTimes, style: clickStyle, volume: audio.clicks, keyTimes: keyTimes, keyStyle: typeStyle)
            let warp = AudioWarp(map: map, gain: Float(maxVol), clicks: clicks, voice: voice, music: music)
            // 没有原声音（或原声音先读完了）：自己按 0.1 秒一块生成静音 + 点击 + 讲解，一直到成片结尾
            var genAt = Int64((range.start.seconds * 48000).rounded()), genEnd = Int64((range.end.seconds * 48000).rounded())
            var origDone = aOut == nil
            aIn.requestMediaDataWhenReady(on: DispatchQueue(label: "studio.audio")) { [self] in
                while aIn.isReadyForMoreMediaData && !audioDone {
                    var end = false
                    var sb: CMSampleBuffer?
                    if failed() { end = true }
                    else if !origDone, let aOut {
                        // 原声音：剪掉的扔掉、快放的压短，再调音量、压低（有讲解时）、混讲解和点击声、软限幅
                        if let raw = aOut.copyNextSampleBuffer() { sb = warp.process(raw) }
                        else { origDone = true; if let at = warp.outAt { genAt = max(genAt, at) }; if voice == nil && music == nil { end = true } }
                    } else if genAt < genEnd {
                        let n = Int(min(4800, genEnd - genAt))
                        sb = clicks.silence(frames: n, at: genAt, voice: voice, music: music); genAt += Int64(n)
                    } else { end = true }
                    if end { audioDone = true; aIn.markAsFinished(); group.leave(); return }
                    if let sb { aIn.append(sb) }
                }
            }
        }
        group.wait()
        while !renderThread.isFinished { usleep(1000) }
        if cancelled {
            reader.cancelReading(); writer.cancelWriting(); try? FileManager.default.removeItem(at: outURL)
            throw CancellationError()
        }
        if let failure { reader.cancelReading(); writer.cancelWriting(); throw failure }
        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? Studio.failure("reader") }
        let sem = DispatchSemaphore(value: 0)
        writer.endSession(atSourceTime: CMTime(value: CMTimeValue(last), timescale: fps))
        writer.finishWriting { sem.signal() }
        sem.wait()
        guard writer.status == .completed else { throw writer.error ?? Studio.failure("finish") }
        progress(1)
        if Self.profiling {
            print(String(format: "PROFILE %dx%d %dfps frames %d | decode %.1fs  build+submit %.1fs  wait-gpu %.1fs  append %.1fs", w, h, st.fps, written, tDec, tBuild, tWait, tAppend))
        }
    }

    // GIF：宽不超过 960、每秒 15 帧；透明背景铺白（GIF 的透明只有全透 / 不透，阴影会很难看）
    private func runGIF() throws {
        let lay = r.lay, fps = CMTimeScale(r.st.fps)
        let range = CMTimeRange(start: CMTime(value: CMTimeValue(first), timescale: fps), end: CMTime(value: CMTimeValue(last), timescale: fps))
        let reader = try AVAssetReader(asset: asset)
        reader.timeRange = map.isIdentity ? range : CMTimeRange(start: range.start, end: CMTime(seconds: srcEnd, preferredTimescale: 60000))
        let vOut = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        vOut.alwaysCopiesSampleData = false
        reader.add(vOut)
        guard reader.startReading() else { throw reader.error ?? Studio.failure("reader") }
        guard var cur = vOut.copyNextSampleBuffer() else { throw Studio.failure(L("素材里没有画面", "No video in the recording")) }
        var next = vOut.copyNextSampleBuffer()
        try? FileManager.default.removeItem(at: outURL)
        let total = last - first
        guard let dst = CGImageDestinationCreateWithURL(outURL as CFURL, "com.compuserve.gif" as CFString, total, nil) else { throw Studio.failure("gif") }
        CGImageDestinationSetProperties(dst, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
        let frameProps = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: 1.0 / Double(r.st.fps), kCGImagePropertyGIFUnclampedDelayTime: 1.0 / Double(r.st.fps)]] as CFDictionary
        let white = CIImage(color: .white).cropped(to: lay.canvas), srgb = CGColorSpace(name: CGColorSpace.sRGB)!
        for i in first..<last {
            if cancelled { break }
            let pts = map.isIdentity ? CMTime(value: CMTimeValue(i), timescale: fps)
                : CMTime(seconds: map.src(Double(i) / Double(fps)), preferredTimescale: 60000)
            while let nx = next, nx.presentationTimeStamp <= pts { cur = nx; next = vOut.copyNextSampleBuffer() }
            try autoreleasepool {
                guard let src = CMSampleBufferGetImageBuffer(cur) else { throw Studio.failure("frame") }
                let img = withCaption(r.frame(i, plan: plan, source: CIImage(cvPixelBuffer: src), overlay: overlay, srcTime: srcAt(i)), i).composited(over: white)
                guard let cg = r.ctx.createCGImage(img, from: lay.canvas, format: .RGBA8, colorSpace: srgb) else { throw Studio.failure("gif frame") }
                CGImageDestinationAddImage(dst, cg, frameProps)
            }
            if (i - first) % 15 == 0 { progress(Double(i - first) / Double(total) * 0.9) }
        }
        reader.cancelReading()
        if cancelled { try? FileManager.default.removeItem(at: outURL); throw CancellationError() }
        guard CGImageDestinationFinalize(dst) else { throw Studio.failure("gif") }
        progress(1)
    }
}

// 点击音效：程序合成的几种短声（衰减正弦 + 一点噪声，几十毫秒），按点击时刻混进 48k 立体声浮点 PCM
final class ClickSound: @unchecked Sendable {
    private let starts: [(at: Int64, w: Int)]  // 每一声从第几个采样开始、用哪个波形
    private let waves: [[Float]]               // 0 = 点击声；1… = 几种略有不同的按键声（轮着用，听着不机械）
    private var format: CMAudioFormatDescription?
    var isEmpty: Bool { starts.isEmpty }

    init(times: [Double], style: StudioLook.Click = .soft, volume: Double = 1, keyTimes: [Double] = [], keyStyle: StudioLook.Typing = .mechanical) {
        let v = Float(max(0, volume))
        let keys = Self.keyWaves(keyStyle).map { w in w.map { $0 * v } }, nk = keys.count
        waves = [Self.wave(style).map { $0 * v }] + keys
        starts = (times.map { (Int64(($0 * 48000).rounded()), 0) } + (nk > 0 ? keyTimes : []).enumerated().map { (Int64(($0.element * 48000).rounded()), 1 + ($0.offset * 3 + $0.offset / 2) % max(nk, 1)) })
            .sorted { $0.0 < $1.0 }
    }

    // 打字声：真键盘录音（App 里 Resources/keys/*.wav，48k 单声道），每种有好几下，连打时轮着用，听着不机械
    nonisolated(unsafe) private static var keyCache: [StudioLook.Typing: [[Float]]] = [:]
    private static let keyLock = NSLock()
    static func keyWaves(_ style: StudioLook.Typing) -> [[Float]] {
        keyLock.lock(); defer { keyLock.unlock() }
        if let w = keyCache[style] { return w }
        let prefix: String, n: Int
        switch style { case .mechanical: prefix = "old"; n = 6; case .laptop: prefix = "mac"; n = 7; case .clicky: prefix = "hi"; n = 6; case .thock: prefix = "lo"; n = 6 }
        let waves: [[Float]] = (1...n).compactMap { k in
            guard let url = Bundle.main.url(forResource: String(format: "%@%02d", prefix, k), withExtension: "wav", subdirectory: "keys"),
                  let f = try? AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false),
                  let buf = AVAudioPCMBuffer(pcmFormat: f.processingFormat, frameCapacity: AVAudioFrameCount(f.length)),
                  (try? f.read(into: buf)) != nil, let ch = buf.floatChannelData?[0] else { return nil }
            return (0..<Int(buf.frameLength)).map { ch[$0] * 0.4 }   // 录音本身很响：压到和点击声差不多
        }
        keyCache[style] = waves
        return waves
    }

    static func typingName(_ s: StudioLook.Typing) -> String {
        switch s {
        case .mechanical: L("机械键盘", "Mechanical"); case .laptop: L("苹果键盘", "Apple keyboard")
        case .clicky: L("清脆机械", "Crisp mechanical"); case .thock: L("低沉 thock", "Deep thock")
        }
    }

    // 试听 / 预览用：一声按键包成 WAV
    static func keyWav(_ s: StudioLook.Typing, _ k: Int) -> Data { let w = keyWaves(s); return wavData(w.isEmpty ? [0] : w[k % w.count]) }

    // 超过 ±0.8 的部分柔和地压到 ±0.95 以内（音量调大、和点击声叠加时不爆音；留点余量，AAC 编码后也不会超过 1）
    @inline(__always) static func softLimit(_ x: Float) -> Float {
        let a = abs(x)
        guard a > 0.8 else { return x }
        let y = 0.8 + 0.15 * tanh((a - 0.8) / 0.15)
        return x < 0 ? -y : y
    }
    // 外面处理好的一块（立体声交错浮点，起点是成片第 a0 个采样）：加上落在里面的点击
    func add(into data: inout [Float], frames n: Int, at a0: Int64) { _ = add(&data, frames: n, at: a0) }

    // 一声的波形（单声道，48k）
    static func wave(_ style: StudioLook.Click) -> [Float] {
        var g = SystemRandomNumberGenerator()
        func noise() -> Double { Double.random(in: -1...1, using: &g) }
        func burst(_ t: Double, _ decay: Double, _ f: Double, _ amp: Double) -> Double {
            amp * exp(-t / decay) * (0.6 * sin(2 * .pi * f * t) + 0.4 * noise())
        }
        let n: Int
        let f: (Double) -> Double
        switch style {
        case .soft:   // 轻点：短促的「嗒」
            n = 1920
            f = { t in exp(-t / 0.0045) * (0.8 * (0.65 * sin(2 * .pi * 3100 * t) + 0.35 * sin(2 * .pi * 1700 * t)) + 0.2 * noise()) * 0.32 }
        case .mouse:  // 鼠标咔哒：按下 + 60 毫秒后松开
            n = 4800
            f = { t in (t < 0.02 ? burst(t, 0.0018, 2600, 0.45) : 0) + (t >= 0.06 && t < 0.08 ? burst(t - 0.06, 0.0015, 3200, 0.25) : 0) }
        case .thock:  // 低沉的「咚」
            n = 4800
            f = { t in exp(-t / 0.012) * (0.6 * sin(2 * .pi * 190 * t) + 0.3 * sin(2 * .pi * 620 * t)) * 0.5 + burst(t, 0.002, 1800, 0.12) }
        case .wood:   // 木头（像木鱼）
            n = 5760
            f = { t in exp(-t / 0.018) * (0.6 * sin(2 * .pi * 880 * t) + 0.25 * sin(2 * .pi * 2350 * t)) * 0.42 }
        case .pop:    // 气泡「啵」：音高往上扬
            n = 2160
            f = { t in let e = pow(sin(.pi * t / 0.045), 2); return 0.4 * e * sin(2 * .pi * (500 + 1200 * t / 0.045) * t) }
        case .tick:   // 清脆的「嘀」
            n = 1440
            f = { t in exp(-t / 0.0025) * (0.7 * sin(2 * .pi * 5200 * t) + 0.3 * sin(2 * .pi * 7400 * t)) * 0.3 }
        }
        return (0..<n).map { Float(f(Double($0) / 48000)) }
    }

    // 试听用：一声包成 WAV
    static func wav(_ style: StudioLook.Click) -> Data { wavData(wave(style)) }
    static func wavData(_ w: [Float]) -> Data {
        let pcm = w.map { Int16(max(-1, min(1, $0)) * 32767) }
        var d = Data()
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        d.append(contentsOf: Array("RIFF".utf8)); u32(UInt32(36 + pcm.count * 2)); d.append(contentsOf: Array("WAVEfmt ".utf8))
        u32(16); u16(1); u16(1); u32(48000); u32(96000); u16(2); u16(16)
        d.append(contentsOf: Array("data".utf8)); u32(UInt32(pcm.count * 2))
        pcm.withUnsafeBytes { d.append(contentsOf: $0) }
        return d
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
        for k in data.indices { data[k] = Self.softLimit(data[k]) }
        return make(data, frames: n, at: a0, format: fmt)
    }

    func silence(frames n: Int, at a0: Int64, voice: VoiceMix? = nil, music: MusicMix? = nil) -> CMSampleBuffer? {
        var data = [Float](repeating: 0, count: n * 2)
        _ = add(&data, frames: n, at: a0)
        voice?.add(into: &data, frames: n, at: a0)
        music?.add(into: &data, frames: n, at: a0)
        for k in data.indices { data[k] = Self.softLimit(data[k]) }
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
        for (s, wi) in starts where s < a0 + Int64(n) && s + Int64(waves[wi].count) > a0 {
            let wave = waves[wi]
            for k in 0..<wave.count {
                let j = s + Int64(k) - a0
                guard j >= 0 && j < Int64(n) else { continue }
                let v = wave[k]
                data[Int(j) * 2] += v
                data[Int(j) * 2 + 1] += v
            }
            touched = true
        }
        return touched
    }

    func make(_ data: [Float], frames n: Int, at a0: Int64, format: CMAudioFormatDescription) -> CMSampleBuffer? {
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

// 导出时的声音：按素材时间读进来的一块块 PCM → 剪掉段扔掉、快放段每 rate 个采样取平均压短 → 乘音量 → 混点击声 → 软限幅，
// 时间戳按成片时间连续往后排
final class AudioWarp: @unchecked Sendable {
    private let map: TimeMap, gain: Float, clicks: ClickSound, voice: VoiceMix?, music: MusicMix?
    private(set) var outAt: Int64?             // 下一个输出采样在成片里的位置
    private var pi = 0                         // 当前看到第几个剪辑段
    private var sumL: Float = 0, sumR: Float = 0, cnt = 0

    init(map: TimeMap, gain: Float, clicks: ClickSound, voice: VoiceMix? = nil, music: MusicMix? = nil) {
        self.map = map; self.gain = gain; self.clicks = clicks; self.voice = voice; self.music = music
    }

    func process(_ sb: CMSampleBuffer) -> CMSampleBuffer? {
        let n = CMSampleBufferGetNumSamples(sb)
        guard n > 0, let block = CMSampleBufferGetDataBuffer(sb), let fmt = CMSampleBufferGetFormatDescription(sb) else { return nil }
        let a0 = Int64((CMSampleBufferGetPresentationTimeStamp(sb).seconds * 48000).rounded())
        var src = [Float](repeating: 0, count: n * 2)
        let bytes = src.count * 4
        guard CMBlockBufferGetDataLength(block) >= bytes else { return nil }
        _ = src.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: bytes, destination: $0.baseAddress!) }
        if outAt == nil { outAt = Int64((map.out(Double(a0) / 48000) * 48000).rounded()) }
        var out: [Float]
        if map.isIdentity { out = src }
        else {
            out = []; out.reserveCapacity(src.count)
            let ps = map.pieces
            for j in 0..<n {
                let t = Double(a0 + Int64(j)) / 48000
                while pi < ps.count && t >= ps[pi].end { pi += 1; sumL = 0; sumR = 0; cnt = 0 }
                let l = src[j * 2], r = src[j * 2 + 1]
                if pi < ps.count && t >= ps[pi].start {
                    let rate = ps[pi].rate
                    if rate == 0 { continue }
                    sumL += l; sumR += r; cnt += 1
                    if Double(cnt) >= rate { out.append(sumL / Float(cnt)); out.append(sumR / Float(cnt)); sumL = 0; sumR = 0; cnt = 0 }
                } else { out.append(l); out.append(r) }
            }
        }
        let frames = out.count / 2
        guard frames > 0, let at = outAt else { return nil }
        if gain != 1 { for k in out.indices { out[k] *= gain } }
        if let voice {
            voice.duck(&out, frames: frames, at: at)          // 有讲解的地方原声音压低
            voice.add(into: &out, frames: frames, at: at)
        }
        music?.add(into: &out, frames: frames, at: at)
        clicks.add(into: &out, frames: frames, at: at)
        for k in out.indices { out[k] = ClickSound.softLimit(out[k]) }
        outAt = at + Int64(frames)
        return clicks.make(out, frames: frames, at: at, format: fmt)
    }
}

// 导出时的讲解：每段解成 48k 浮点、按成片时间放好，和原声音、点击声一起混；
// 有讲解的地方把原声音压低约 -12 dB（讲解开始前 0.15 秒慢慢压下去，结束后 0.35 秒慢慢回来）
final class VoiceMix: @unchecked Sendable {
    private struct Piece { let at: Int64; let pcm: [Float] }   // 单声道，起点 = 成片第 at 个采样
    private let pieces: [Piece]
    private let spans: [(Int64, Int64)]        // 讲解占的范围（成片采样，左闭右开）
    let duckTo: Float                          // 压到多少：1 = 不压
    static let attack = 0.15, release = 0.35
    static let duckLevel: Float = 0.25         // -12 dB

    private init(pieces: [Piece], spans: [(Int64, Int64)]? = nil, duck: Bool) {
        self.pieces = pieces
        self.spans = spans ?? pieces.map { ($0.at, $0.at + Int64($0.pcm.count)) }
        duckTo = duck ? Self.duckLevel : 1
    }

    static func load(dir: URL, project p: StudioProject, map: TimeMap) async throws -> VoiceMix? {
        var pcs: [Piece] = []
        for c in p.voiceovers where c.length > 0.01 && c.volume > 0.001 && p.audio.voice > 0.001 {
            let url = Denoise.voiceURL(dir: dir, file: c.file, level: p.audio.denoise)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            let all = try await decode(url)
            let a = min(all.count, max(0, Int((c.trimIn * 48000).rounded()))), b = min(all.count, max(a, Int((c.trimOut * 48000).rounded())))
            let g = Float(c.volume * p.audio.voice)
            pcs.append(Piece(at: Int64((c.outRange(map).start * 48000).rounded()), pcm: all[a..<b].map { $0 * g }))
        }
        return pcs.isEmpty ? nil : VoiceMix(pieces: pcs, duck: p.audio.duck)
    }
    // 只给时间段（不读文件）：自测压低曲线用
    static func spansOnly(_ r: [(Double, Double)], duck: Bool) -> VoiceMix {
        VoiceMix(pieces: [], spans: r.map { (Int64(($0.0 * 48000).rounded()), Int64(($0.1 * 48000).rounded())) }, duck: duck)
    }

    // 整个录音文件解成 48k 单声道浮点
    static func decode(_ url: URL) async throws -> [Float] {
        let asset = AVURLAsset(url: url)
        guard let t = try await asset.loadTracks(withMediaType: .audio).first else { return [] }
        let r = try AVAssetReader(asset: asset)
        let o = AVAssetReaderTrackOutput(track: t, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
        ])
        r.add(o)
        guard r.startReading() else { throw r.error ?? Studio.failure("voice") }
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

    // 第 s 个采样（成片）原声音该乘多少
    func duckGain(_ s: Int64) -> Float {
        guard duckTo < 1 else { return 1 }
        var w = 0.0
        for (a, b) in spans {
            if s >= a && s < b { return duckTo }
            if s < a { let t = Double(a - s) / 48000; if t < Self.attack { w = max(w, 1 - t / Self.attack) } }
            else { let t = Double(s - b) / 48000; if t < Self.release { w = max(w, 1 - t / Self.release) } }
        }
        return 1 - (1 - duckTo) * Float(w)
    }
    // 一块立体声交错 PCM（起点 = 成片第 a0 个采样）：原声音按压低曲线乘上去
    func duck(_ data: inout [Float], frames n: Int, at a0: Int64) {
        guard duckTo < 1 else { return }
        let pad = Int64(Self.release * 48000) + 1
        guard spans.contains(where: { $0.0 - pad < a0 + Int64(n) && $0.1 + pad > a0 }) else { return }
        for f in 0..<n {
            let g = duckGain(a0 + Int64(f))
            if g < 1 { data[f * 2] *= g; data[f * 2 + 1] *= g }
        }
    }
    // 把落在这一块里的讲解加进去
    func add(into data: inout [Float], frames n: Int, at a0: Int64) {
        for p in pieces where p.at < a0 + Int64(n) && p.at + Int64(p.pcm.count) > a0 {
            let from = max(0, Int(a0 - p.at)), to = min(p.pcm.count, Int(a0 + Int64(n) - p.at))
            for k in from..<to {
                let j = Int(p.at + Int64(k) - a0), v = p.pcm[k]
                data[j * 2] += v; data[j * 2 + 1] += v
            }
        }
    }
}

