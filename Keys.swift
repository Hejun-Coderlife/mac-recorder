// 显示快捷键：录制时记下按的组合键（只记带 ⌘⌃⌥ 的组合和 Esc、Tab、回车、方向键、F 键这类功能键——普通打字一律不记），
// 剪辑 / 导出时在画面上浮出 macOS 风格的键帽「⌘ ⇧ C」，约 1.2 秒后淡出
// 有「输入监控」权限时用系统的按键监听（最准）；没有时每秒 120 次查一次按键状态（够用，极快的按键可能漏）
import AppKit
import Carbon
import CoreImage
import CoreText
import CoreMedia

struct KeyStroke: Codable, Equatable {
    var t: Double                   // 秒（录制时是主机时钟，存盘时换成离开头几秒）
    var keys: [String]              // ["⌘", "⇧", "C"]
}

// ---- 录制时记快捷键 ----
final class KeyCapture: @unchecked Sendable {
    private let lock = NSLock()
    private var list: [KeyStroke] = []
    private var tap: CFMachPort?, source: CFRunLoopSource?
    private var lastDown: Set<Int> = []
    var strokes: [KeyStroke] { lock.lock(); defer { lock.unlock() }; return list }
    static var hasPermission: Bool { CGPreflightListenEventAccess() }
    // 没权限时问一次（系统会弹「输入监控」的设置提示）
    static func request() { _ = CGRequestListenEventAccess() }

    private static let modKeys: Set<Int> = [54, 55, 56, 57, 58, 59, 60, 61, 62, 63]   // ⌘⇧⇪⌥⌃ 左右、fn
    private static let named: [Int: String] = [
        36: "↩", 76: "⌤", 48: "⇥", 49: L("空格", "Space"), 51: "⌫", 117: "⌦", 53: "esc",
        123: "←", 124: "→", 125: "↓", 126: "↑", 115: "↖", 119: "↘", 116: "⇞", 121: "⇟",
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9", 109: "F10", 103: "F11", 111: "F12",
    ]
    private static let standalone: Set<Int> = [53, 48, 36, 76, 51, 117, 123, 124, 125, 126, 115, 119, 116, 121,
                                               122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111]

    // 这一下要不要记、记成什么；普通打字（没有 ⌘⌃⌥、也不是功能键）返回 nil
    static func label(code: Int, flags: CGEventFlags) -> [String]? {
        guard !modKeys.contains(code) else { return nil }
        let cmd = flags.contains(.maskCommand), ctl = flags.contains(.maskControl), opt = flags.contains(.maskAlternate), sh = flags.contains(.maskShift)
        guard cmd || ctl || opt || standalone.contains(code) else { return nil }
        var k: [String] = []
        if ctl { k.append("⌃") }; if opt { k.append("⌥") }; if sh { k.append("⇧") }; if cmd { k.append("⌘") }
        k.append(named[code] ?? char(code) ?? "?")
        return k
    }
    // 按当前键盘布局把键位换成字（不带修饰键，大写）
    static func char(_ code: Int) -> String? {
        guard let src = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let ptr = TISGetInputSourceProperty(src, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let data = Unmanaged<CFData>.fromOpaque(ptr).takeUnretainedValue() as Data
        var dead: UInt32 = 0, len = 0
        var chars = [UniChar](repeating: 0, count: 4)
        let ok = data.withUnsafeBytes { raw -> OSStatus in
            UCKeyTranslate(raw.bindMemory(to: UCKeyboardLayout.self).baseAddress!, UInt16(code), UInt16(kUCKeyActionDisplay), 0,
                           UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysBit), &dead, 4, &len, &chars)
        }
        guard ok == noErr, len > 0 else { return nil }
        let s = String(utf16CodeUnits: chars, count: len).uppercased()
        return s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : s
    }

    private func add(_ k: [String]) {
        let now = CMClockGetTime(CMClockGetHostTimeClock()).seconds
        lock.lock(); list.append(KeyStroke(t: now, keys: k)); lock.unlock()
    }

    func start() {
        guard Self.hasPermission else { return }   // 没权限：靠 poll() 查按键状态
        let mask = CGEventMask(1 << CGEventType.keyDown.rawValue)
        let me = Unmanaged.passUnretained(self).toOpaque()
        tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly, eventsOfInterest: mask, callback: { _, type, e, refcon in
            if type == .keyDown, let refcon, e.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
                let me = Unmanaged<KeyCapture>.fromOpaque(refcon).takeUnretainedValue()
                if let k = KeyCapture.label(code: Int(e.getIntegerValueField(.keyboardEventKeycode)), flags: e.flags) { me.add(k) }
            }
            return Unmanaged.passUnretained(e)
        }, userInfo: me)
        if let tap {
            source = CFMachPortCreateRunLoopSource(nil, tap, 0)
            CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
            CGEvent.tapEnable(tap: tap, enable: true)
        }
    }
    var tapping: Bool { tap != nil }
    func stop() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        tap = nil; source = nil
    }
    // 没有监听权限时：鼠标采样线程每次看到「又按了一下」就调这个，查现在按着哪些键
    func poll() {
        guard tap == nil else { return }
        let flags = CGEventSource.flagsState(.combinedSessionState)
        var down: Set<Int> = []
        for c in 0..<128 where !Self.modKeys.contains(c) && CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(c)) { down.insert(c) }
        for c in down.subtracting(lastDown).sorted() { if let k = Self.label(code: c, flags: flags) { add(k) } }
        lastDown = down
    }
}

// ---- 画面上浮出键帽（预览和导出共用；时间用素材时间存、按成片时间查） ----
final class KeyLayer: @unchecked Sendable {
    private struct Item { let start: Double, keys: [String] }
    private let items: [Item]
    private let look: StudioLook
    private let cw: Double, ch: Double
    private let raised: Double            // 底部居中时往上让开字幕
    static let hold = 1.2, fadeIn = 0.1, fadeOut = 0.3

    init?(strokes: [KeyStroke]?, look: StudioLook, map: TimeMap, trim: (Double, Double), canvas: CGSize, captionsShown: Bool) {
        guard look.showKeys, let s = strokes, !s.isEmpty else { return nil }
        self.look = look; cw = canvas.width; ch = canvas.height
        raised = captionsShown ? 0.11 : 0
        items = s.compactMap { k in
            guard k.t >= trim.0, k.t < trim.1, !map.inCut(k.t) else { return nil }
            return Item(start: map.out(k.t), keys: k.keys)
        }.sorted { $0.start < $1.start }
        if items.isEmpty { return nil }
    }

    // 成片时间 t：最近按的那一下（下一下一按就换掉），淡入淡出
    func image(atOut t: Double) -> CIImage? {
        guard let i = items.lastIndex(where: { $0.start <= t }) else { return nil }
        let it = items[i], age = t - it.start
        let next = i + 1 < items.count ? items[i + 1].start : .infinity
        let end = min(it.start + Self.hold, next)
        guard age < end - it.start else { return nil }
        var a = min(1, age / Self.fadeIn)
        if next > it.start + Self.hold { a = min(a, (end - t) / Self.fadeOut) }
        guard a > 0.01, let img = Self.render(it.keys, dark: look.keysDark, px: look.keysSize * min(cw, ch) * (cw < ch ? 1.2 : 1)) else { return nil }
        let w = Double(img.width), h = Double(img.height), m = min(cw, ch) * 0.05
        let x: Double
        switch look.keysAt {
        case .bottom: x = (cw - w) / 2
        case .bottomLeft: x = m
        case .bottomRight: x = cw - w - m
        }
        let y = ch * (0.06 + (look.keysAt == .bottom ? raised : 0)) + (1 - min(1, age / 0.18)) * -h * 0.25   // 进场时轻轻上浮
        var ci = CIImage(cgImage: img).transformed(by: CGAffineTransform(translationX: x.rounded(), y: y.rounded()))
        if a < 0.999 { ci = ci.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: a)]) }
        return ci
    }
    func image(atSource t: Double, map: TimeMap) -> CIImage? { map.inCut(t) ? nil : image(atOut: map.out(t)) }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: CGImage] = [:]

    // 一排键帽：每个键一个圆角方块，⌘ ⇧ 这类和字母一样大，长的（esc、空格、F12）自动变宽
    static func render(_ keys: [String], dark: Bool, px: Double) -> CGImage? {
        let key = "\(keys.joined(separator: " "))|\(dark)|\(Int(px))"
        lock.lock(); if let c = cache[key] { lock.unlock(); return c }; lock.unlock()
        let h = px, gap = px * 0.18, pad = px * 0.28, font = CTFontCreateWithName("SFProText-Medium" as CFString, px * 0.48, nil)
        let fg = dark ? CGColor(gray: 1, alpha: 1) : CGColor(gray: 0.1, alpha: 1)
        let lines = keys.map { CTLineCreateWithAttributedString(NSAttributedString(string: $0, attributes: [.font: font, .foregroundColor: fg])) }
        let widths = lines.map { max(h, CTLineGetTypographicBounds($0, nil, nil, nil) + px * 0.5) }
        let shadowPad = px * 0.3
        let W = Int((widths.reduce(0, +) + gap * Double(keys.count - 1) + pad * 2 + shadowPad * 2).rounded(.up))
        let H = Int((h + pad * 2 + shadowPad * 2).rounded(.up))
        guard let c = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        // 底板：一条胶囊把几个键托住
        let plate = CGRect(x: shadowPad, y: shadowPad, width: Double(W) - shadowPad * 2, height: Double(H) - shadowPad * 2)
        c.saveGState()
        c.setShadow(offset: CGSize(width: 0, height: -px * 0.06), blur: px * 0.35, color: CGColor(gray: 0, alpha: 0.35))
        c.addPath(CGPath(roundedRect: plate, cornerWidth: px * 0.42, cornerHeight: px * 0.42, transform: nil))
        c.setFillColor(dark ? CGColor(gray: 0.1, alpha: 0.82) : CGColor(gray: 0.98, alpha: 0.92)); c.fillPath()
        c.restoreGState()
        var x = plate.minX + pad
        for (i, l) in lines.enumerated() {
            let r = CGRect(x: x, y: plate.minY + pad, width: widths[i], height: h)
            c.addPath(CGPath(roundedRect: r, cornerWidth: px * 0.22, cornerHeight: px * 0.22, transform: nil))
            c.setFillColor(dark ? CGColor(gray: 1, alpha: 0.14) : CGColor(gray: 0, alpha: 0.07)); c.fillPath()
            c.addPath(CGPath(roundedRect: r.insetBy(dx: 0.5, dy: 0.5), cornerWidth: px * 0.22, cornerHeight: px * 0.22, transform: nil))
            c.setStrokeColor(dark ? CGColor(gray: 1, alpha: 0.18) : CGColor(gray: 0, alpha: 0.12)); c.setLineWidth(1); c.strokePath()
            var asc: CGFloat = 0, desc: CGFloat = 0
            let lw = CTLineGetTypographicBounds(l, &asc, &desc, nil)
            c.textPosition = CGPoint(x: r.midX - lw / 2, y: r.midY - (asc - desc) / 2)
            CTLineDraw(l, c)
            x += widths[i] + gap
        }
        guard let img = c.makeImage() else { return nil }
        lock.lock(); if cache.count > 200 { cache.removeAll() }; cache[key] = img; lock.unlock()
        return img
    }
}

// ---- 自测：BATCHATEST=1 [DENOISE_WAV=干净人声.wav] → 降噪指标、快捷键规则、键帽出现时间，打印后退出 ----
extension StudioTests {
    @MainActor static func batchATest() -> Bool {
        let env = ProcessInfo.processInfo.environment
        guard env["BATCHATEST"] != nil else { return false }
        Task.detached {
            var fails = 0
            func check(_ ok: Bool, _ msg: String) { print(ok ? "PASS" : "FAIL", msg); if !ok { fails += 1 } }
            // 1) 快捷键规则：带 ⌘⌃⌥ 的组合、功能键记；普通打字（含只按 ⇧）不记
            check(KeyCapture.label(code: 8, flags: .maskCommand) == ["⌘", "C"], "⌘C → ⌘ C")
            check(KeyCapture.label(code: 8, flags: [.maskCommand, .maskShift]) == ["⇧", "⌘", "C"], "⌘⇧C → ⇧ ⌘ C")
            check(KeyCapture.label(code: 0, flags: []) == nil, "a alone not recorded")
            check(KeyCapture.label(code: 0, flags: .maskShift) == nil, "⇧A not recorded")
            check(KeyCapture.label(code: 53, flags: []) == ["esc"], "Esc recorded")
            check(KeyCapture.label(code: 123, flags: .maskAlternate) == ["⌥", "←"], "⌥← recorded")
            check(KeyCapture.label(code: 56, flags: .maskShift) == nil, "modifier key alone not recorded")
            // 2) 键帽什么时候出现：按下后 1.2 秒内有，之后没有；落在剪掉段里的不出；下一下一按就换
            var look = StudioLook(); look.showKeys = true
            var p = StudioProject(segments: [], trimStart: 0, trimEnd: 20, look: look)
            p.cuts = [CutSegment(start: 5, end: 7)]
            let m = TimeMap(p)
            let strokes = [KeyStroke(t: 1, keys: ["⌘", "C"]), KeyStroke(t: 1.5, keys: ["⌘", "V"]), KeyStroke(t: 6, keys: ["⌘", "Z"]), KeyStroke(t: 10, keys: ["esc"])]
            if let kl = KeyLayer(strokes: strokes, look: look, map: m, trim: (0, 20), canvas: CGSize(width: 1920, height: 1080), captionsShown: false) {
                check(kl.image(atOut: 0.9) == nil, "nothing before first stroke")
                check(kl.image(atOut: 1.2) != nil, "⌘C shown at 1.2")
                check(kl.image(atOut: 2.2) != nil, "⌘V shown at 2.2 (replaced ⌘C at 1.5)")
                check(kl.image(atOut: 2.9) == nil, "gone 1.2 s after ⌘V")
                check(kl.image(atOut: m.out(6) + 0.3) == nil || m.out(10) - m.out(6) < 1.2, "stroke inside cut not shown")
                check(kl.image(atOut: m.out(10) + 0.5) != nil, "esc shown at its out time")
            } else { check(false, "KeyLayer built") }
            var off = look; off.showKeys = false
            check(KeyLayer(strokes: strokes, look: off, map: m, trim: (0, 20), canvas: CGSize(width: 1920, height: 1080), captionsShown: false) == nil, "switch off → no layer")
            // 3) 降噪：干净人声 + 白噪声 + 120Hz 嗡声，看噪声段能量降多少、人声段 SNR 提升多少，各强度单调
            if let wav = env["DENOISE_WAV"] {
                do {
                    let clean = try await VoiceMix.decode(URL(fileURLWithPath: wav))
                    let lead = 48000 * 2   // 前 2 秒只有噪声
                    var g = SystemRandomNumberGenerator()
                    let n = clean.count + lead
                    var noise = [Float](repeating: 0, count: n)
                    for i in 0..<n { noise[i] = Float.random(in: -1...1, using: &g) * 0.035 + 0.03 * sin(2 * .pi * 120 * Float(i) / 48000) }
                    let sig = (0..<n).map { i in (i >= lead ? clean[i - lead] : 0) }
                    let x = zip(sig, noise).map { $0 + $1 }
                    func db(_ a: ArraySlice<Float>) -> Double { 10 * log10(max(1e-12, a.reduce(0) { $0 + Double($1 * $1) } / Double(max(1, a.count)))) }
                    let t0 = Date()
                    let wet = try Denoise.isolate(x)
                    print(String(format: "isolate %.1f s audio in %.2f s", Double(n) / 48000, Date().timeIntervalSince(t0)))
                    // 对齐：找处理后和干净人声最相关的偏移（±50 ms）
                    var best = (lag: 0, c: -Double.infinity)
                    for lag in stride(from: -2400, through: 2400, by: 48) {
                        var c = 0.0
                        for i in stride(from: lead + 48000, to: min(n, lead + 48000 * 4), by: 4) where i + lag >= 0 && i + lag < n { c += Double(wet[i + lag] * sig[i]) }
                        if c > best.c { best = (lag, c) }
                    }
                    print("alignment lag (samples):", best.lag)
                    check(abs(best.lag) <= 96, "processed audio aligned (|lag| ≤ 2 ms)")
                    let inNoise = db(x[0..<lead - 4800])
                    func snr(_ y: [Float]) -> Double {
                        var s = 0.0, e = 0.0
                        for i in lead..<n { s += Double(sig[i] * sig[i]); let d = Double(y[i] - sig[i]); e += d * d }
                        return 10 * log10(s / max(1e-12, e))
                    }
                    print(String(format: "input: noise segment %.1f dB, speech SNR %.1f dB", inNoise, snr(x)))
                    var prev = 0.0
                    for l in [Denoise.Level.light, .medium, .strong] {
                        let k = Denoise.wetShare(l)
                        let y = zip(x, wet).map { $0 * (1 - k) + $1 * k }
                        let drop = inNoise - db(y[0..<lead - 4800])
                        print(String(format: "%@: noise down %.1f dB, speech SNR %.1f dB", l.rawValue, drop, snr(y)))
                        check(drop >= prev - 0.5, "\(l.rawValue) monotonic"); prev = drop
                        if l != .light { check(drop >= 15, "\(l.rawValue) noise drop ≥ 15 dB") }
                    }
                } catch { check(false, "denoise ran: \(error)") }
            }
            print(fails == 0 ? "BATCHATEST ALL PASS" : "BATCHATEST \(fails) FAIL")
            exit(fails == 0 ? 0 : 1)
        }
        return true
    }
}
