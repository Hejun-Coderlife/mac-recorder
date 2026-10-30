// 遮挡（模糊 / 马赛克 / 色块）和标注（箭头、框、圈、高亮、文字）：都画在原画面上（位置用原画面比例、时间用素材时间），
// 所以推近时跟着画面一起放大挪动，像是录的时候就在那里。合成顺序：原画面 → 遮挡 → 标注 → 点击波纹、鼠标 → 镜头推拉
import AppKit
import CoreImage
import CoreText

// 遮挡一块：x、y、w、h 是原画面里的比例（左上为原点）
struct Redaction: Codable, Equatable, Identifiable {
    enum Style: String, Codable, CaseIterable { case blur, mosaic, solid, frosted, spotlight, focus }
    var id = UUID()
    var start: Double
    var end: Double
    var x: Double, y: Double, w: Double, h: Double
    var style = Style.blur
    var strength = 0.6               // 0–1：模糊多糊 / 马赛克格子多大
    var color = RGB(0.12, 0.12, 0.14) // 色块的颜色
    var corner = 8.0                 // 圆角（原画面的点）

    var rect: CGRect { CGRect(x: x, y: y, width: w, height: h) }
    static func name(_ s: Style) -> String {
        switch s {
        case .blur: L("模糊", "Blur"); case .mosaic: L("马赛克", "Mosaic"); case .solid: L("色块", "Solid")
        case .frosted: L("毛玻璃", "Frosted"); case .spotlight: L("聚光灯", "Spotlight"); case .focus: L("聚焦", "Focus")
        }
    }
    // 聚光灯 / 聚焦：处理的是框外面（四周变暗 / 四周模糊），框里保持原样
    var outside: Bool { style == .spotlight || style == .focus }
    static func hint(_ s: Style) -> String {
        switch s {
        case .blur: L("框里模糊", "Blurs inside the box"); case .mosaic: L("框里打马赛克", "Pixelates inside the box")
        case .solid: L("框里盖一块纯色", "Covers the box with a color"); case .frosted: L("框里像磨砂玻璃：模糊再蒙一层白", "Frosted glass: blurred and lightened")
        case .spotlight: L("框外面变暗，突出框里", "Darkens everything outside the box"); case .focus: L("框外面模糊，突出框里", "Blurs everything outside the box")
        }
    }
}

// 标注：箭头从 (x, y) 指到 (x + w, y + h)；框 / 圈 / 高亮是这个矩形；文字的左上角在 (x, y)，大小按字算
struct Annotation: Codable, Equatable, Identifiable {
    enum Kind: String, Codable, CaseIterable { case arrow, rect, ellipse, highlight, text }
    var id = UUID()
    var start: Double
    var end: Double
    var kind = Kind.arrow
    var x: Double, y: Double, w: Double, h: Double
    var color = RGB(1.0, 0.27, 0.23)
    var width = 5.0                  // 线宽（原画面的点）
    var text = ""
    var fontSize = 28.0              // 字号（原画面的点）
    var pill = true                  // 文字带底色胶囊
    var drawOn = true                // 箭头像笔画出来那样出现
    var arrow: ArrowStyle?           // 箭头样式（没有 = 普通）；可选字段，旧工程照样能读
    var oval: Bool?                  // 圈：true = 椭圆（随便拉）；没有 = 正圆
    var textStyle: TextLook?         // 文字标签样式（没有 = 看 pill：胶囊 / 只有字）
    enum TextLook: String, Codable, CaseIterable { case pill, glass, glassDark, dark, gradient, outline, sticker, bubble, neon, marker, plain }
    var textLook: TextLook { textStyle ?? (pill ? .pill : .plain) }
    static func textLookName(_ t: TextLook) -> String {
        switch t {
        case .pill: L("彩色胶囊", "Pill"); case .glass: L("玻璃（浅）", "Glass"); case .glassDark: L("玻璃（深）", "Dark glass")
        case .dark: L("深色", "Dark"); case .gradient: L("渐变", "Gradient"); case .outline: L("描边", "Outline")
        case .sticker: L("贴纸", "Sticker"); case .bubble: L("对话气泡", "Bubble"); case .neon: L("霓虹", "Neon")
        case .marker: L("荧光笔", "Marker"); case .plain: L("只有字", "Plain")
        }
    }
    var opacity: Double?             // 不透明度 0.1–1（没有 = 高亮 35%、其他 100%）
    enum ArrowStyle: String, Codable, CaseIterable { case plain, double, curved, thick, dashed }
    var alpha: Double { min(max(opacity ?? (kind == .highlight ? 0.35 : 1), 0.05), 1) }
    static func arrowName(_ s: ArrowStyle) -> String {
        switch s {
        case .plain: L("普通", "Plain"); case .double: L("双向", "Double"); case .curved: L("弧形", "Curved")
        case .thick: L("粗箭头", "Bold"); case .dashed: L("虚线", "Dashed")
        }
    }

    static func name(_ k: Kind) -> String {
        switch k {
        case .arrow: L("箭头", "Arrow"); case .rect: L("框", "Box"); case .ellipse: L("圈", "Circle")
        case .highlight: L("高亮", "Highlight"); case .text: L("文字", "Text")
        }
    }
    static func icon(_ k: Kind) -> String {
        switch k {
        case .arrow: "arrow.up.right"; case .rect: "rectangle"; case .ellipse: "circle"
        case .highlight: "highlighter"; case .text: "textformat"
        }
    }
    static let palette: [RGB] = [RGB(1.0, 0.27, 0.23), RGB(1.0, 0.62, 0.04), RGB(1.0, 0.84, 0.04), RGB(0.20, 0.78, 0.35),
                                 RGB(0.04, 0.52, 1.0), RGB(0.69, 0.32, 0.87), RGB(1, 1, 1), RGB(0.11, 0.11, 0.12)]
}

// 出现、消失：淡入淡出 0.25 秒；箭头的「画出来」0.4 秒
enum OverlayAnim {
    static let fade = 0.25, draw = 0.4
    static func alpha(_ t: Double, _ s: Double, _ e: Double) -> Double {
        guard t >= s, t < e else { return 0 }
        let f = min(fade, (e - s) / 2)
        return min(1, (t - s) / f, (e - t) / f)
    }
    static func drawProgress(_ t: Double, _ s: Double) -> Double {
        let u = min(max((t - s) / draw, 0), 1)
        return 1 - pow(1 - u, 3)
    }
}

final class OverlayLayer: @unchecked Sendable {
    let redactions: [Redaction]
    let annotations: [Annotation]
    var isEmpty: Bool { redactions.isEmpty && annotations.isEmpty }
    init(_ p: StudioProject) { redactions = p.redactions; annotations = p.annotations }

    // 一块画布：原画面比例（左上原点）→ 这块画布上的坐标（左下原点）。src = 原画面像素；scene = 镜头推拉之前的画布
    struct Space {
        let x0: Double, y0: Double, w: Double, h: Double
        func point(_ n: CGPoint) -> CGPoint { CGPoint(x: x0 + n.x * w, y: y0 + (1 - n.y) * h) }
        func rect(_ n: CGRect) -> CGRect {
            let a = point(CGPoint(x: n.minX, y: n.minY)), b = point(CGPoint(x: n.maxX, y: n.maxY))
            return CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
        }
        static func source(_ lay: StudioLayout) -> Space { Space(x0: 0, y0: 0, w: lay.W, h: lay.H) }
        static func scene(_ lay: StudioLayout) -> Space { Space(x0: lay.card.minX, y0: lay.card.minY, w: lay.W * lay.s, h: lay.H * lay.s) }
    }
    static func scene(_ n: CGPoint, _ lay: StudioLayout) -> CGPoint { Space.scene(lay).point(n) }
    static func sceneRect(_ n: CGRect, _ lay: StudioLayout) -> CGRect { Space.scene(lay).rect(n) }

    // 画在原画面（src，原画面像素、左下原点）上，再交给后面一次性缩放、推拉——这样推近时整幅只重采样一次，字不会因为加了遮挡变软。
    // t = 素材时间；scale = 标注按多少倍画（≈ 原画面到成片的放大倍数，推近也清楚）
    func apply(_ src: CIImage, t: Double, lay: StudioLayout, ppp: Double, scale: Double) -> CIImage {
        var img = src
        let sp = Space.source(lay), unit = ppp   // 原画面 1 点 = ppp 个原画面像素
        for r in redactions where t >= r.start && t < r.end {
            img = Self.redact(img, r, space: sp, unit: unit)
        }
        for a in annotations {
            let al = OverlayAnim.alpha(t, a.start, a.end)
            guard al > 0.002, let (pic, origin) = Self.draw(a, t: t, space: sp, unit: unit, scale: min(4, max(0.5, scale))) else { continue }
            if a.kind == .text && (a.textLook == .glass || a.textLook == .glassDark) { img = Self.glass(img, a, space: sp, unit: unit, alpha: al) }
            var ci = CIImage(cgImage: pic.image).transformed(by: CGAffineTransform(scaleX: 1 / pic.scale, y: 1 / pic.scale)
                .concatenating(CGAffineTransform(translationX: origin.x, y: origin.y)))
            if al < 0.998 { ci = ci.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: al)]) }
            img = ci.composited(over: img)
        }
        return img.cropped(to: src.extent)
    }

    // 玻璃标签后面那块（像苹果 Liquid Glass）：中间几乎是透明的（只糊一点点），越靠边越像透镜把画面放大、弯过来；没有描边
    static func glass(_ img: CIImage, _ a: Annotation, space sp: Space, unit: Double, alpha: Double) -> CIImage {
        let sz = textBox(a, unit: unit), tl = sp.point(CGPoint(x: a.x, y: a.y))
        let R = CGRect(x: tl.x, y: tl.y - sz.height, width: sz.width, height: sz.height).integral
        guard R.width > 2, R.height > 2 else { return img }
        return lens(img, rect: R, radius: R.height / 2, strength: 1, dark: a.textLook == .glassDark, alpha: alpha)
    }
    // 一块玻璃透镜（像苹果 Liquid Glass）：中间清楚不糊；越靠边，越往外取背景——背景的线条到边上会顺着形状弯过去、挤在边缘。
    // 每个点按离边多远、往哪个方向算该取哪儿（圆角矩形的距离场），strength 越大弯得越厉害
    nonisolated(unsafe) static let lensKernel = CIWarpKernel(source: """
    kernel vec2 lens(vec4 r, float rad, float edge, float depth) {
      vec2 p = destCoord();
      vec2 c = r.xy + r.zw * 0.5;
      vec2 q = abs(p - c) - (r.zw * 0.5 - vec2(rad));
      float d = -(length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - rad);
      if (d <= 0.0) return p;
      vec2 n = (q.x > 0.0 && q.y > 0.0) ? normalize(q) : (q.x > q.y ? vec2(1.0, 0.0) : vec2(0.0, 1.0));
      n = n * sign(p - c);
      float t = clamp(1.0 - d / edge, 0.0, 1.0);
      return p + n * depth * t * t;
    }
    """)
    static func lens(_ img: CIImage, rect R: CGRect, radius rad: Double, strength: Double, dark: Bool, alpha: Double) -> CIImage {
        let s = min(R.width, R.height)
        guard s > 2 else { return img }
        let edge = s * 0.5, depth = s * 0.4 * strength
        let src = img.clampedToExtent()
        var fx = (lensKernel?.apply(extent: R, roiCallback: { _, r in r.insetBy(dx: -depth - 2, dy: -depth - 2) }, image: src,
                                    arguments: [CIVector(cgRect: R), min(rad, s / 2), edge, depth]) ?? src.cropped(to: R))
            .applyingFilter("CIColorControls", parameters: [kCIInputSaturationKey: 1.15, kCIInputBrightnessKey: dark ? -0.06 : 0.035])
            .cropped(to: R)
        if alpha < 0.998 { fx = fx.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: alpha)]) }
        let shape = CIFilter(name: "CIRoundedRectangleGenerator", parameters: ["inputExtent": CIVector(cgRect: R), "inputRadius": max(0, min(rad, s / 2)), "inputColor": CIColor.white])!.outputImage!
            .applyingGaussianBlur(sigma: max(0.4, s * 0.006)).cropped(to: R)
        return fx.composited(over: img).applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: img, kCIInputMaskImageKey: shape.composited(over: CIImage(color: .black).cropped(to: img.extent))])
    }

    static func redact(_ img: CIImage, _ r: Redaction, space sp: Space, unit: Double) -> CIImage {
        let R = sp.rect(r.rect).integral
        guard R.width >= 2, R.height >= 2 else { return img }
        let k = min(max(r.strength, 0), 1)
        let effect: CIImage
        switch r.style {
        case .blur:
            effect = img.clampedToExtent().applyingGaussianBlur(sigma: (3 + 22 * k) * unit).cropped(to: R)
        case .mosaic:
            effect = img.clampedToExtent().applyingFilter("CIPixellate", parameters: [
                kCIInputCenterKey: CIVector(x: R.minX, y: R.minY), kCIInputScaleKey: max(2, (6 + 30 * k) * unit),
            ]).cropped(to: R)
        case .solid:
            effect = CIImage(color: r.color.ci).cropped(to: R)
        case .frosted:
            let b = img.clampedToExtent().applyingGaussianBlur(sigma: (8 + 26 * k) * unit)
                .applyingFilter("CIColorControls", parameters: [kCIInputSaturationKey: 1.15, kCIInputBrightnessKey: 0.04])
            effect = CIImage(color: CIColor(red: 1, green: 1, blue: 1, alpha: 0.28 + 0.2 * k)).composited(over: b).cropped(to: R)
        case .spotlight:
            effect = CIImage(color: CIColor(red: 0, green: 0, blue: 0, alpha: 0.45 + 0.45 * k)).composited(over: img).cropped(to: img.extent)
        case .focus:
            effect = img.clampedToExtent().applyingGaussianBlur(sigma: (4 + 22 * k) * unit).cropped(to: img.extent)
        }
        let rad = min(r.corner * unit, min(R.width, R.height) / 2)
        let mask = rad > 0.5
            ? CIFilter(name: "CIRoundedRectangleGenerator", parameters: ["inputExtent": CIVector(cgRect: R), "inputRadius": rad, "inputColor": CIColor.white])!.outputImage!
            : CIImage(color: .white).cropped(to: R)
        if r.outside {   // 框里是原画面，框外是效果
            return img.applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: effect, kCIInputMaskImageKey: mask.composited(over: CIImage(color: .black).cropped(to: img.extent))])
        }
        return effect.applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: img, kCIInputMaskImageKey: mask])
    }

    // 文字标注的大小（画布像素）：给画的时候和编辑框用
    static func textBox(_ a: Annotation, unit: Double) -> CGSize {
        let line = textLine(a, px: a.fontSize * unit)
        var asc: CGFloat = 0, desc: CGFloat = 0
        let w = CTLineGetTypographicBounds(line, &asc, &desc, nil)
        let look = a.textLook, px0 = a.fontSize * unit
        let pad = look == .plain ? 0 : look == .marker ? px0 * 0.2 : look == .neon ? px0 * 0.35 : look == .sticker ? px0 * 0.5 : px0 * 0.45
        let tail = look == .bubble ? a.fontSize * unit * 0.4 : 0     // 气泡下面的小尾巴
        return CGSize(width: max(w, a.fontSize * unit) + pad * 2, height: asc + desc + pad * 1.2 + tail)
    }
    private static func textLine(_ a: Annotation, px: Double) -> CTLine {
        let font = CTFontCreateWithName("PingFangSC-Semibold" as CFString, max(4, px), nil)
        let own = CGColor(srgbRed: a.color.r, green: a.color.g, blue: a.color.b, alpha: 1)
        let light = 0.299 * a.color.r + 0.587 * a.color.g + 0.114 * a.color.b > 0.75
        let color: CGColor
        switch a.textLook {
        case .pill, .bubble: color = Self.onColor(a.color)
        case .dark: color = light ? own : CGColor(gray: 1, alpha: 1)
        case .glass, .marker: color = CGColor(srgbRed: 0.11, green: 0.11, blue: 0.12, alpha: 1)
        case .glassDark, .gradient: color = CGColor(gray: 1, alpha: 1)
        case .neon: color = light ? CGColor(srgbRed: 0.2, green: 0.9, blue: 1, alpha: 1) : own
        case .sticker: color = light ? CGColor(srgbRed: 0.11, green: 0.11, blue: 0.12, alpha: 1) : own
        case .outline: color = light ? CGColor(srgbRed: 0.11, green: 0.11, blue: 0.12, alpha: 1) : own
        case .plain: color = own
        }
        let s = a.text.isEmpty ? L("文字", "Text") : a.text
        return CTLineCreateWithAttributedString(NSAttributedString(string: s, attributes: [.font: font, .foregroundColor: color]))
    }
    // 底色胶囊上的字：深底白字、浅底黑字
    private static func onColor(_ c: RGB) -> CGColor {
        let l = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b
        return l > 0.6 ? CGColor(srgbRed: 0.1, green: 0.1, blue: 0.11, alpha: 1) : CGColor(gray: 1, alpha: 1)
    }
    // 标注在原画面比例里占的框（编辑框、命中用）；箭头是两个端点围成的框
    // unit = 原画面 1 点在原画面里多少像素（track.pixelsPerPoint）
    static func normBounds(_ a: Annotation, lay: StudioLayout, unit: Double) -> CGRect {
        if a.kind == .text {
            let sz = textBox(a, unit: unit)
            return CGRect(x: a.x, y: a.y, width: sz.width / lay.W, height: sz.height / lay.H)
        }
        return CGRect(x: min(a.x, a.x + a.w), y: min(a.y, a.y + a.h), width: abs(a.w), height: abs(a.h))
    }

    struct Pic { let image: CGImage; let scale: Double }
    // 把一个标注画成一张小图（只画它占的那块，按镜头放大倍数画，推近也清楚）；返回图和它左下角在画布上的位置
    static func draw(_ a: Annotation, t: Double, space sp: Space, unit: Double, scale k: Double) -> (Pic, CGPoint)? {
        let lw = max(1, a.width * unit)
        var box: CGRect
        switch a.kind {
        case .arrow:
            let p0 = sp.point(CGPoint(x: a.x, y: a.y)), p1 = sp.point(CGPoint(x: a.x + a.w, y: a.y + a.h))
            box = CGRect(x: min(p0.x, p1.x), y: min(p0.y, p1.y), width: abs(p1.x - p0.x), height: abs(p1.y - p0.y)).insetBy(dx: -lw * 6, dy: -lw * 6)
            if a.arrow == .curved {   // 弧形往外鼓：把控制点也框进来
                let len = hypot(p1.x - p0.x, p1.y - p0.y), m = CGPoint(x: (p0.x + p1.x) / 2, y: (p0.y + p1.y) / 2)
                if len > 0 { box = box.union(CGRect(x: m.x - (p1.y - p0.y) * 0.22, y: m.y + (p1.x - p0.x) * 0.22, width: 0, height: 0).insetBy(dx: -lw * 6, dy: -lw * 6)) }
            }
        case .text:
            let sz = textBox(a, unit: unit), tl = sp.point(CGPoint(x: a.x, y: a.y))
            box = CGRect(x: tl.x, y: tl.y - sz.height, width: sz.width, height: sz.height).insetBy(dx: -lw * 2, dy: -lw * 2)
        default:
            box = sp.rect(CGRect(x: a.x, y: a.y, width: a.w, height: a.h).standardized).insetBy(dx: -lw * 2, dy: -lw * 2)
        }
        box = box.integral
        let pw = Int((box.width * k).rounded(.up)), ph = Int((box.height * k).rounded(.up))
        guard pw > 1, ph > 1, pw * ph < 40_000_000 else { return nil }
        guard let c = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        c.scaleBy(x: k, y: k); c.translateBy(x: -box.minX, y: -box.minY)
        if a.kind != .highlight { c.setAlpha(a.alpha) }   // 高亮自己按透明度填色
        c.setLineCap(.round); c.setLineJoin(.round)
        let col = CGColor(srgbRed: a.color.r, green: a.color.g, blue: a.color.b, alpha: 1)
        let shadow = CGColor(gray: 0, alpha: 0.35)
        switch a.kind {
        case .arrow:
            let p0 = sp.point(CGPoint(x: a.x, y: a.y)), p1 = sp.point(CGPoint(x: a.x + a.w, y: a.y + a.h))
            let u = a.drawOn ? OverlayAnim.drawProgress(t, a.start) : 1
            let len = hypot(p1.x - p0.x, p1.y - p0.y)
            guard len > 1 else { return nil }
            let style = a.arrow ?? .plain
            // 杆子先取成一串点（弧形是二次曲线，往左边鼓 22%），画出来时只画到第 u 处
            let nx = -(p1.y - p0.y) / len, ny = (p1.x - p0.x) / len
            let ctl = CGPoint(x: (p0.x + p1.x) / 2 + nx * len * 0.22, y: (p0.y + p1.y) / 2 + ny * len * 0.22)
            func at(_ v: Double) -> CGPoint {
                if style != .curved { return CGPoint(x: p0.x + (p1.x - p0.x) * v, y: p0.y + (p1.y - p0.y) * v) }
                let w = 1 - v
                return CGPoint(x: w * w * p0.x + 2 * w * v * ctl.x + v * v * p1.x, y: w * w * p0.y + 2 * w * v * ctl.y + v * v * p1.y)
            }
            let steps = style == .curved ? 48 : 1
            let pts = (0...steps).map { at(Double($0) / Double(steps) * u) }
            let thick = style == .thick
            let head = min(len * 0.45, lw * (thick ? 6.0 : 4.2))
            let hs = head * min(1, max(0, (u - 0.6) / 0.4))     // 箭头头部在画到最后 25% 时长出来
            c.setShadow(offset: CGSize(width: 0, height: -lw * 0.4), blur: lw * 1.2, color: shadow)
            c.setStrokeColor(col); c.setFillColor(col); c.setLineWidth(thick ? lw * 2.4 : lw)
            if style == .dashed { c.setLineDash(phase: 0, lengths: [lw * 2.2, lw * 1.8]) }
            if thick { c.setLineCap(.butt) }
            // 头部朝向：杆子最后一小段的方向；杆子收到头部根部
            func headAt(_ tip: CGPoint, from prev: CGPoint, size: Double) {
                let d = hypot(tip.x - prev.x, tip.y - prev.y)
                guard size > 0.5, d > 0 else { return }
                let dx = (tip.x - prev.x) / d, dy = (tip.y - prev.y) / d
                let bx = tip.x - dx * size, by = tip.y - dy * size, wx = -dy * size * (thick ? 0.7 : 0.55), wy = dx * size * (thick ? 0.7 : 0.55)
                c.setLineDash(phase: 0, lengths: [])
                c.move(to: tip); c.addLine(to: CGPoint(x: bx + wx, y: by + wy)); c.addLine(to: CGPoint(x: bx - wx, y: by - wy)); c.closePath(); c.fillPath()
            }
            var shaft = pts
            let tip = pts.last!, prev = pts.count > 2 ? pts[pts.count - 3] : pts.first!
            if hs > 0.5, hypot(tip.x - pts.first!.x, tip.y - pts.first!.y) > head {
                let d = hypot(tip.x - prev.x, tip.y - prev.y)
                if d > 0 { shaft[shaft.count - 1] = CGPoint(x: tip.x - (tip.x - prev.x) / d * head * 0.6, y: tip.y - (tip.y - prev.y) / d * head * 0.6) }
            }
            if style == .double && hs > 0.5, shaft.count > 1 {
                let q = shaft[1], d = hypot(q.x - p0.x, q.y - p0.y)
                if d > 0 { shaft[0] = CGPoint(x: p0.x + (q.x - p0.x) / d * head * 0.6, y: p0.y + (q.y - p0.y) / d * head * 0.6) }
            }
            c.addLines(between: shaft); c.strokePath()
            headAt(tip, from: prev, size: hs)
            if style == .double, pts.count > 1 { headAt(p0, from: pts[min(2, pts.count - 1)], size: hs) }
        case .rect, .ellipse:
            let r = sp.rect(CGRect(x: a.x, y: a.y, width: a.w, height: a.h).standardized)
            c.setShadow(offset: CGSize(width: 0, height: -lw * 0.3), blur: lw, color: shadow)
            c.setStrokeColor(col); c.setLineWidth(lw)
            if a.kind == .rect { c.addPath(CGPath(roundedRect: r, cornerWidth: min(lw * 1.5, r.width / 2), cornerHeight: min(lw * 1.5, r.height / 2), transform: nil)) }
            else { c.addEllipse(in: r) }
            c.strokePath()
        case .highlight:
            let r = sp.rect(CGRect(x: a.x, y: a.y, width: a.w, height: a.h).standardized)
            c.setFillColor(CGColor(srgbRed: a.color.r, green: a.color.g, blue: a.color.b, alpha: a.alpha))
            c.addPath(CGPath(roundedRect: r, cornerWidth: min(6 * unit, r.width / 2), cornerHeight: min(6 * unit, r.height / 2), transform: nil)); c.fillPath()
        case .text:
            let sz = textBox(a, unit: unit), tl = sp.point(CGPoint(x: a.x, y: a.y))
            let r = CGRect(x: tl.x, y: tl.y - sz.height, width: sz.width, height: sz.height)
            let px = a.fontSize * unit, line = textLine(a, px: px)
            var asc: CGFloat = 0, desc: CGFloat = 0
            let tw = CTLineGetTypographicBounds(line, &asc, &desc, nil)
            let look = a.textLook, tail = look == .bubble ? px * 0.4 : 0
            let body = CGRect(x: r.minX, y: r.minY + tail, width: r.width, height: r.height - tail)   // 左下原点：尾巴在下面
            let capsule = CGPath(roundedRect: body, cornerWidth: body.height / 2, cornerHeight: body.height / 2, transform: nil)
            let soft = CGColor(gray: 0, alpha: 0.3)
            switch look {
            case .pill:
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.06), blur: px * 0.3, color: soft)
                c.setFillColor(col); c.addPath(capsule); c.fillPath()
            case .bubble:
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.06), blur: px * 0.3, color: soft)
                let rr = CGPath(roundedRect: body, cornerWidth: body.height * 0.32, cornerHeight: body.height * 0.32, transform: nil)
                c.setFillColor(col); c.addPath(rr)
                c.move(to: CGPoint(x: body.minX + body.height * 0.45, y: body.minY + 1)); c.addLine(to: CGPoint(x: body.minX + body.height * 0.3, y: r.minY))
                c.addLine(to: CGPoint(x: body.minX + body.height * 0.95, y: body.minY + 1)); c.closePath(); c.fillPath()
            case .dark:
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.06), blur: px * 0.35, color: soft)
                c.setFillColor(CGColor(gray: 0.08, alpha: 0.78)); c.addPath(capsule); c.fillPath()
            case .glass, .glassDark:
                // 像苹果 Liquid Glass：背后的画面在 apply 里用透镜弯过（边缘折射）。这里只加体积感，不画描边线：
                // 浮起的柔影、边缘一圈往里渐淡的内阴影、上沿一点柔光
                let dark = look == .glassDark
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.16), blur: px * 1.0, color: CGColor(gray: 0, alpha: dark ? 0.32 : 0.2))
                c.setFillColor(dark ? CGColor(gray: 0.06, alpha: 0.3) : CGColor(gray: 1, alpha: 0.05)); c.addPath(capsule); c.fillPath()
                c.setShadow(offset: .zero, blur: 0, color: nil)
                let outer = CGPath(rect: body.insetBy(dx: -px * 2, dy: -px * 2), transform: nil)
                func inner(_ color: CGColor, offset: CGSize, blur: Double) {
                    c.saveGState(); c.addPath(capsule); c.clip()
                    c.setShadow(offset: offset, blur: blur, color: color)
                    c.addPath(outer); c.addPath(capsule); c.setFillColor(CGColor(gray: 0, alpha: 1)); c.fillPath(using: .evenOdd)
                    c.restoreGState()
                }
                inner(CGColor(gray: 0, alpha: dark ? 0.35 : 0.16), offset: CGSize(width: 0, height: -px * 0.04), blur: px * 0.35)   // 边缘内阴影
                inner(CGColor(gray: 1, alpha: dark ? 0.35 : 0.75), offset: CGSize(width: 0, height: -px * 0.06), blur: px * 0.12)  // 上沿柔光
            case .gradient:
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.06), blur: px * 0.35, color: soft)
                c.addPath(capsule); c.fillPath(); c.setShadow(offset: .zero, blur: 0, color: nil)
                c.saveGState(); c.addPath(capsule); c.clip()
                // 从选的颜色渐变到它偏紫一点、亮一点的颜色
                let c2 = CGColor(srgbRed: min(1, a.color.r * 0.7 + 0.35), green: a.color.g * 0.55 + 0.1, blue: min(1, a.color.b * 0.6 + 0.6), alpha: 1)
                let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!, colors: [col, c2] as CFArray, locations: [0, 1])!
                c.drawLinearGradient(g, start: CGPoint(x: body.minX, y: body.midY), end: CGPoint(x: body.maxX, y: body.midY), options: [])
                c.restoreGState()
            case .sticker:
                // 贴纸：白底 + 粗白边 + 投影，有点歪
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.08), blur: px * 0.35, color: CGColor(gray: 0, alpha: 0.35))
                let rr = CGPath(roundedRect: body, cornerWidth: body.height * 0.28, cornerHeight: body.height * 0.28, transform: nil)
                c.setFillColor(CGColor(gray: 1, alpha: 1)); c.addPath(rr); c.fillPath()
                c.setShadow(offset: .zero, blur: 0, color: nil)
                c.setStrokeColor(col); c.setLineWidth(max(1.5, px * 0.07))
                c.addPath(CGPath(roundedRect: body.insetBy(dx: px * 0.14, dy: px * 0.14), cornerWidth: body.height * 0.2, cornerHeight: body.height * 0.2, transform: nil)); c.strokePath()
            case .neon:
                // 霓虹：字发光（在下面画字时用彩色光晕）
                break
            case .marker:
                // 荧光笔：字的下半截刷一道半透明的色
                c.setFillColor(CGColor(srgbRed: a.color.r, green: a.color.g, blue: a.color.b, alpha: 0.5))
                let m = CGRect(x: body.minX, y: body.minY + body.height * 0.08, width: body.width, height: body.height * 0.5)
                c.addPath(CGPath(roundedRect: m, cornerWidth: m.height * 0.25, cornerHeight: m.height * 0.25, transform: nil)); c.fillPath()
            case .outline:
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.05), blur: px * 0.3, color: CGColor(gray: 0, alpha: 0.18))
                c.setFillColor(CGColor(gray: 1, alpha: 0.92)); c.addPath(capsule); c.fillPath()
                c.setShadow(offset: .zero, blur: 0, color: nil)
                c.setLineWidth(max(1.5, px * 0.08)); c.setStrokeColor(col)
                c.addPath(CGPath(roundedRect: body.insetBy(dx: px * 0.04, dy: px * 0.04), cornerWidth: body.height / 2 - px * 0.04, cornerHeight: body.height / 2 - px * 0.04, transform: nil)); c.strokePath()
            case .plain:
                c.setShadow(offset: CGSize(width: 0, height: -px * 0.05), blur: px * 0.25, color: CGColor(gray: 0, alpha: 0.55))
            }
            if look != .plain && look != .neon { c.setShadow(offset: .zero, blur: 0, color: nil) }
            c.textPosition = CGPoint(x: body.midX - tw / 2, y: body.midY - (asc - desc) / 2)
            if look == .neon {
                c.setShadow(offset: .zero, blur: px * 0.55, color: col); CTLineDraw(line, c)
                c.textPosition = CGPoint(x: body.midX - tw / 2, y: body.midY - (asc - desc) / 2)
                c.setShadow(offset: .zero, blur: px * 0.2, color: col)
            }
            CTLineDraw(line, c)
        }
        guard let img = c.makeImage() else { return nil }
        return (Pic(image: img, scale: k), box.origin)
    }
}

// ---- 自测：OVERLAYTEST=素材夹（/tmp 下的副本）→ 旧工程兼容；导出「有 / 没有遮挡和标注」两份比画面：
// 遮挡区域里糊掉 / 打上马赛克、区域外不变、推近时遮挡跟着画面放大挪动；标注出现在该在的位置和时间段、淡入、箭头画出来 ----
import AVFoundation
extension StudioTests {
    @MainActor static func overlayTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["OVERLAYTEST"] else { return false }
        Task { @MainActor in
            var ok = true
            func check(_ n: String, _ c: Bool, _ info: String = "") { print(c ? "PASS" : "FAIL", n, info); ok = ok && c }
            let old = #"{"segments":[],"trimStart":0,"trimEnd":10}"#.data(using: .utf8)!
            let po = try? JSONDecoder().decode(StudioProject.self, from: old)
            check("旧工程兼容", po?.redactions == [] && po?.annotations == [])
            var pr = StudioProject(trimStart: 0, trimEnd: 10)
            pr.redactions = [Redaction(start: 1, end: 2, x: 0.1, y: 0.2, w: 0.3, h: 0.1, style: .mosaic)]
            pr.annotations = [Annotation(start: 1, end: 3, kind: .text, x: 0.5, y: 0.5, w: 0, h: 0, text: "你好", pill: false)]
            let rt = try? JSONDecoder().decode(StudioProject.self, from: JSONEncoder().encode(pr))
            check("存了再读一样", rt?.redactions == pr.redactions && rt?.annotations == pr.annotations)
            check("淡入淡出", abs(OverlayAnim.alpha(1.1, 1, 3) - 0.4) < 1e-9 && OverlayAnim.alpha(2, 1, 3) == 1 && OverlayAnim.alpha(3, 1, 3) == 0)

            let dir = URL(fileURLWithPath: path)
            guard let track = try? JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json"))) else { print("OVERLAYTEST FAIL no track"); exit(1) }
            let dur = (try? await AVURLAsset(url: dir.appendingPathComponent("raw.mp4")).load(.duration).seconds) ?? 0
            guard dur >= 15 else { print("OVERLAYTEST FAIL 素材要 ≥ 15 秒"); exit(1) }
            var base = StudioProject.load(dir, track: track, duration: dur)
            base.trimStart = 0; base.trimEnd = 15; base.cuts = []; base.captions = []; base.voiceovers = []; base.layouts = []
            base.camera?.show = false
            base.look.background = .color; base.look.color = RGB(0.2, 0.3, 0.5); base.look.blur = .off; base.look.clickSound = false
            base.look.ripple = false; base.look.aspect = .wide; base.look.hideIdleCursor = false
            // 0–10 秒全景，10.5–15 秒固定推近 2 倍到马赛克那块的中间
            let mos = CGRect(x: 0.30, y: 0.30, width: 0.25, height: 0.20)
            base.segments = [ZoomSegment(start: 10.5, end: 15, zoom: 2, mode: .fixed, fixedX: mos.midX, fixedY: mos.midY)]
            base.segmentsEdited = true
            var ov = base
            let blurR = CGRect(x: 0.10, y: 0.15, width: 0.30, height: 0.25)
            ov.redactions = [Redaction(start: 0.5, end: 2.5, x: blurR.minX, y: blurR.minY, w: blurR.width, h: blurR.height, style: .blur, strength: 0.8),
                             Redaction(start: 11, end: 14, x: mos.minX, y: mos.minY, w: mos.width, h: mos.height, style: .mosaic, strength: 0.7)]
            let boxR = CGRect(x: 0.55, y: 0.20, width: 0.30, height: 0.30)
            ov.annotations = [Annotation(start: 3.0, end: 5.0, kind: .rect, x: boxR.minX, y: boxR.minY, w: boxR.width, h: boxR.height),
                              Annotation(start: 5.5, end: 7.5, kind: .arrow, x: 0.20, y: 0.70, w: 0.25, h: -0.20, color: RGB(0.04, 0.52, 1)),
                              Annotation(start: 8.0, end: 10.0, kind: .text, x: 0.60, y: 0.65, w: 0, h: 0, text: "测试标注", fontSize: 32)]
            var o = ExportOptions(); o.res = .sd; o.fps = 30; o.quality = .high
            func export(_ p: StudioProject, _ name: String) async -> URL? {
                do { return try await Studio.render(project: dir, to: URL(fileURLWithPath: "/tmp/overlaytest-\(name).mp4"), style: StudioStyle(o), edits: p) { _ in } }
                catch { print("export", name, error); return nil }
            }
            guard let uA = await export(base, "plain"), let uB = await export(ov, "marked") else { check("导出", false); print("OVERLAYTEST FAIL"); exit(1) }
            // 预测位置：同样的渲染器和镜头计划，原画面比例 → 成片像素（左上原点）
            let r = StudioRenderer(project: dir, track: track, style: StudioStyle(o), look: base.look)
            let plan = r.plan(base, duration: dur), lay = r.lay
            func outRect(_ n: CGRect, at t: Double) -> CGRect {
                let f = plan[min(Int((t * 30).rounded()), plan.count - 1)]
                let s = OverlayLayer.sceneRect(n, lay)
                func m(_ p: CGPoint) -> CGPoint { CGPoint(x: (p.x - f.sx) * f.z + lay.cw / 2, y: lay.ch - ((p.y - f.sy) * f.z + lay.ch / 2)) }
                let a = m(CGPoint(x: s.minX, y: s.minY)), b = m(CGPoint(x: s.maxX, y: s.maxY))
                return CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
            }
            func frame(_ u: URL, _ t: Double) async -> CGImage? {
                let g = AVAssetImageGenerator(asset: AVURLAsset(url: u)); g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
                return try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)).image
            }
            func px(_ cg: CGImage) -> [UInt8] {
                var d = [UInt8](repeating: 0, count: cg.width * cg.height * 4)
                let c = CGContext(data: &d, width: cg.width, height: cg.height, bitsPerComponent: 8, bytesPerRow: cg.width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
                c.draw(cg, in: CGRect(x: 0, y: 0, width: cg.width, height: cg.height)); return d
            }
            struct Cmp { var inMean = 0.0, outFrac = 0.0, bbox = CGRect.null, gradA = 0.0, gradB = 0.0, diffN = 0 }
            // 两帧比较：区域内平均差、区域外（往外让 pad 像素）明显不同的像素占比、所有明显不同像素的外框、区域内的细节（相邻像素差）
            func compare(_ t: Double, region: CGRect, pad: Double = 6) async -> Cmp? {
                guard let a = await frame(uA, t), let b = await frame(uB, t) else { return nil }
                let pa = px(a), pb = px(b), w = a.width, h = a.height
                var c = Cmp(), inSum = 0.0, inN = 0, outBig = 0, outN = 0
                let big = region.insetBy(dx: -pad, dy: -pad), small = region.insetBy(dx: pad, dy: pad)
                // 变化的外框：一行 / 一列里有超过 0.5% 的像素明显不同才算（视频压缩会让零星像素变一点）
                var rowN = [Int](repeating: 0, count: h), colN = [Int](repeating: 0, count: w)
                for y in 0..<h { for x in 0..<w {
                    let i = (y * w + x) * 4
                    let d = abs(Int(pa[i]) - Int(pb[i])) + abs(Int(pa[i + 1]) - Int(pb[i + 1])) + abs(Int(pa[i + 2]) - Int(pb[i + 2]))
                    let p = CGPoint(x: Double(x) + 0.5, y: Double(y) + 0.5)
                    if d > 45 { c.diffN += 1 }
                    if d > 90 { rowN[y] += 1; colN[x] += 1 }
                    if small.contains(p) {
                        inSum += Double(d); inN += 1
                        if x + 1 < w { let j = i + 4
                            c.gradA += Double(abs(Int(pa[i]) - Int(pa[j]))); c.gradB += Double(abs(Int(pb[i]) - Int(pb[j]))) }
                    } else if !big.contains(p) { outN += 1; if d > 90 { outBig += 1 } }
                }}
                let rows = (0..<h).filter { rowN[$0] > max(2, w / 200) }, cols = (0..<w).filter { colN[$0] > max(2, h / 200) }
                if let y0 = rows.first, let y1 = rows.last, let x0 = cols.first, let x1 = cols.last {
                    c.bbox = CGRect(x: x0, y: y0, width: x1 - x0 + 1, height: y1 - y0 + 1)
                }
                c.inMean = inN > 0 ? inSum / Double(inN) : 0
                c.outFrac = outN > 0 ? Double(outBig) / Double(outN) : 0
                if let dst = CGImageDestinationCreateWithURL(URL(fileURLWithPath: String(format: "/tmp/overlaytest-%.1f.png", t)) as CFURL, "public.png" as CFString, 1, nil) {
                    CGImageDestinationAddImage(dst, b, nil); CGImageDestinationFinalize(dst)
                }
                return c
            }
            func near(_ a: CGRect, _ b: CGRect, _ tol: Double) -> Bool {
                abs(a.minX - b.minX) < tol && abs(a.minY - b.minY) < tol && abs(a.maxX - b.maxX) < tol && abs(a.maxY - b.maxY) < tol
            }
            func f(_ r: CGRect) -> String { String(format: "(%.0f,%.0f)-(%.0f,%.0f)", r.minX, r.minY, r.maxX, r.maxY) }

            // 模糊：区域里细节少了、区域外不变
            let rb = outRect(blurR, at: 1.5)
            if let c = await compare(1.5, region: rb) {
                check("模糊：区域里变了", c.inMean > 6, String(format: "平均差 %.1f", c.inMean))
                check("模糊：区域里细节变少", c.gradB < c.gradA * 0.6, String(format: "细节 %.0f → %.0f", c.gradA, c.gradB))
                check("模糊：区域外不变", c.outFrac < 0.002, String(format: "%.4f", c.outFrac))
            } else { check("抽帧 1.5", false) }
            if let c = await compare(2.9, region: rb) { check("遮挡时间段外不遮", c.diffN < 50, "\(c.diffN) 个像素") }
            // 推近时的马赛克：位置跟着画面放大挪动
            let rm = outRect(mos, at: 13.0)
            let rmFull = outRect(mos, at: 9.9)
            check("推近后遮挡框在成片里变大（预测）", rm.width > rmFull.width * 1.6, f(rmFull) + " → " + f(rm))
            if let c = await compare(13.0, region: rm) {
                check("推近时马赛克区域里变了", c.inMean > 6, String(format: "平均差 %.1f", c.inMean))
                check("推近时马赛克区域外不变", c.outFrac < 0.003, String(format: "%.4f", c.outFrac))
                // 遮挡框上下两条压在空白处的话打码前后一样，所以只要求：变化都在预测框里、左右两边对得上
                check("推近时变化的范围在推近后的遮挡框里、宽度对得上", rm.insetBy(dx: -8, dy: -8).contains(c.bbox) && abs(c.bbox.minX - rm.minX) < 8 && abs(c.bbox.maxX - rm.maxX) < 8,
                      "实际 " + f(c.bbox) + " 预测 " + f(rm))
            } else { check("抽帧 13", false) }
            // 框：变化的外框 ≈ 预测的框（加线宽）
            let rr = outRect(boxR, at: 4.0)
            if let c = await compare(4.0, region: rr, pad: 12) {
                check("框出现在该在的位置", near(c.bbox, rr, 14), "实际 " + f(c.bbox) + " 预测 " + f(rr))
                check("框里面不变（只画边）", c.inMean < 4, String(format: "里面平均差 %.1f", c.inMean))
            } else { check("抽帧 4", false) }
            if let c1 = await compare(3.07, region: rr, pad: 12), let c2 = await compare(4.0, region: rr, pad: 12) {
                check("淡入：刚出现时比完全出现时淡", c1.diffN < c2.diffN, "\(c1.diffN) < \(c2.diffN)")
            }
            if let c = await compare(5.2, region: rr) { check("标注时间段外不画", c.diffN < 50, "\(c.diffN) 个像素") }
            // 箭头：画出来的过程中范围比画完小
            let ra = outRect(CGRect(x: 0.20, y: 0.50, width: 0.25, height: 0.20), at: 6.5)
            if let c1 = await compare(5.64, region: .zero), let c2 = await compare(6.5, region: .zero) {
                check("箭头位置", near(c2.bbox, ra, 26), "实际 " + f(c2.bbox) + " 预测 " + f(ra))
                check("箭头画出来：开头一段短", c1.bbox.width < c2.bbox.width * 0.75, String(format: "%.0f < %.0f", c1.bbox.width, c2.bbox.width))
            } else { check("抽帧 箭头", false) }
            // 文字：左上角在 (0.60, 0.65)
            let tl = outRect(CGRect(x: 0.60, y: 0.65, width: 0.001, height: 0.001), at: 9.0)
            if let c = await compare(9.0, region: .zero) {
                check("文字出现在左上角附近", abs(c.bbox.minX - tl.minX) < 20 && abs(c.bbox.minY - tl.minY) < 20 && c.bbox.width > 40, "外框 " + f(c.bbox) + " 左上 " + f(tl))
            } else { check("抽帧 文字", false) }
            print(ok ? "OVERLAYTEST OK" : "OVERLAYTEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }
}
