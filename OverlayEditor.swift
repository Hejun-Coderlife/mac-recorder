// 剪辑窗口里的遮挡 / 标注：播放栏「遮挡」「标注」按钮 → 在预览上拖出来；选中的在预览上有编辑框（8 个点改大小、拖中间挪、箭头拖两头），
// 时间轴「遮挡」「标注」两条轨上拖动改时间；下面的属性条改样式。全部能撤销
import AppKit
import SwiftUI

enum OverlayTool: Equatable {
    case redact
    case annotate(Annotation.Kind)
    var hint: String {
        switch self {
        case .redact: L("在画面上拖出要遮住的地方（Esc 取消）", "Drag over what to hide (Esc to cancel)")
        case .annotate(.arrow): L("从箭头尾巴拖到要指的地方（Esc 取消）", "Drag from the tail to where the arrow points (Esc to cancel)")
        case .annotate(.text): L("在画面上点一下放文字（Esc 取消）", "Click where the text goes (Esc to cancel)")
        case .annotate: L("在画面上拖出一块（Esc 取消）", "Drag out an area (Esc to cancel)")
        }
    }
}

// 预览画面 ↔ 原画面比例（左上为原点），按播放头这一刻的镜头
struct OverlayMapping {
    let lay: StudioLayout
    let f: FramePlan?
    let ppp: Double
    // 原画面比例 → 画布（左下原点）
    func canvas(_ n: CGPoint) -> CGPoint {
        let s = OverlayLayer.Space.scene(lay).point(n)
        guard let f else { return s }
        return CGPoint(x: (s.x - f.sx) * f.z + lay.cw / 2, y: (s.y - f.sy) * f.z + lay.ch / 2)
    }
    func norm(canvas c: CGPoint) -> CGPoint {
        let s = f.map { CGPoint(x: (c.x - lay.cw / 2) / $0.z + $0.sx, y: (c.y - lay.ch / 2) / $0.z + $0.sy) } ?? c
        return CGPoint(x: (s.x - lay.card.minX) / (lay.W * lay.s), y: 1 - (s.y - lay.card.minY) / (lay.H * lay.s))
    }
    // 画布按比例放进 size 里的位置（和播放器、截图一样居中等比）
    static func fit(_ c: CGSize, in size: CGSize) -> CGRect {
        guard c.width > 0, c.height > 0, size.width > 0, size.height > 0 else { return .zero }
        let k = min(size.width / c.width, size.height / c.height)
        return CGRect(x: (size.width - c.width * k) / 2, y: (size.height - c.height * k) / 2, width: c.width * k, height: c.height * k)
    }
    func view(_ n: CGPoint, fit: CGRect) -> CGPoint {
        let c = canvas(n)
        return CGPoint(x: fit.minX + c.x / lay.cw * fit.width, y: fit.minY + (1 - c.y / lay.ch) * fit.height)
    }
    func norm(view v: CGPoint, fit: CGRect) -> CGPoint {
        norm(canvas: CGPoint(x: (v.x - fit.minX) / fit.width * lay.cw, y: (1 - (v.y - fit.minY) / fit.height) * lay.ch))
    }
    // 预览上挪了 d（点）= 原画面比例挪了多少
    func normDelta(_ d: CGSize, fit: CGRect) -> CGSize {
        let a = norm(view: .zero, fit: fit), b = norm(view: CGPoint(x: d.width, y: d.height), fit: fit)
        return CGSize(width: b.x - a.x, height: b.y - a.y)
    }
}

// 遮挡 / 标注的几何（x、y、w、h，原画面比例；箭头的 w、h 可以是负的）
struct OverlayGeo { var x: Double, y: Double, w: Double, h: Double }

extension StudioEditor {
    func redactionIndex(_ id: UUID?) -> Int? { project.redactions.firstIndex { $0.id == id } }
    func annotationIndex(_ id: UUID?) -> Int? { project.annotations.firstIndex { $0.id == id } }

    // 新加的默认从播放头开始 3 秒（放不下就往前挪）
    private func newRange() -> (Double, Double) {
        let lo = project.trimStart, hi = project.trimEnd > lo ? project.trimEnd : duration
        var s = min(max(time, lo), hi), e = min(hi, s + 3)
        if e - s < 1 { s = max(lo, e - 3) }
        return (s, max(e, s + 0.3))
    }
    func addRedaction(_ r: CGRect) {
        let (s, e) = newRange()
        let x = Redaction(start: s, end: e, x: r.minX, y: r.minY, w: r.width, h: r.height)
        edit { $0.redactions.append(x) }
        selectOverlay(x.id)
    }
    func addAnnotation(_ kind: Annotation.Kind, _ g: OverlayGeo) {
        let (s, e) = newRange()
        var a = Annotation(start: s, end: e, kind: kind, x: g.x, y: g.y, w: g.w, h: g.h)
        if kind == .highlight { a.color = RGB(1.0, 0.84, 0.04) }
        edit { $0.annotations.append(a) }
        selectOverlay(a.id)
        if kind == .text { beginNote(a.id) }
    }
    // 正圆：原画面里宽 = 高（按像素），比例坐标下 w * W = h * H
    func circleRect(center c: CGPoint, diameterW d: Double) -> CGRect {
        let h = d * Double(track.width) / Double(max(track.height, 1))
        return CGRect(x: c.x - d / 2, y: c.y - h / 2, width: d, height: h)
    }
    // 拖把手时保持正圆：拖角按大的一边、拖边按那条边；左 / 上边被拖时右 / 下边不动
    func keepCircle(_ g: inout OverlayGeo, movedLeft: Bool, movedTop: Bool, edgeOnly: Bool, horizontal: Bool) {
        let W = Double(track.width), H = Double(max(track.height, 1))
        let right = g.x + g.w, bottom = g.y + g.h
        let side = edgeOnly ? (horizontal ? g.w * W : g.h * H) : max(g.w * W, g.h * H)
        g.w = side / W; g.h = side / H
        if movedLeft { g.x = right - g.w }
        if movedTop { g.y = bottom - g.h }
    }
    // 在预览上点一下：点中正在显示的标注 / 遮挡就选中它（上面的优先），点空白处取消选中。p = 画布里的比例（左上原点）
    func tapPreview(_ p: CGPoint?) {
        guard let p else { selected = nil; return }
        let m = overlayMapping()
        let n = m.norm(canvas: CGPoint(x: p.x * m.lay.cw, y: (1 - p.y) * m.lay.ch))
        let ptsW = Double(track.width) / max(track.pixelsPerPoint, 0.01), ptsH = Double(track.height) / max(track.pixelsPerPoint, 0.01)
        let tol = 0.012 / (m.f?.z ?? 1)
        func near(_ a: CGPoint, _ b: CGPoint) -> Double {   // 点到线段的距离（原画面比例）
            let dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy
            let k = l2 > 0 ? min(max(((n.x - a.x) * dx + (n.y - a.y) * dy) / l2, 0), 1) : 0
            return hypot(n.x - (a.x + dx * k), n.y - (a.y + dy * k))
        }
        for a in project.annotations.reversed() where time >= a.start && time < a.end {
            let hit: Bool
            switch a.kind {
            case .arrow:
                let a0 = CGPoint(x: a.x, y: a.y), a1 = CGPoint(x: a.x + a.w, y: a.y + a.h)
                hit = near(a0, a1) < tol * 2 + (a.arrow == .curved ? hypot(a.w, a.h) * 0.22 : 0)
            case .text:
                let sz = OverlayLayer.textBox(a, unit: 1)
                hit = CGRect(x: a.x, y: a.y, width: sz.width / ptsW, height: sz.height / ptsH).insetBy(dx: -tol, dy: -tol).contains(n)
            default:
                hit = CGRect(x: a.x, y: a.y, width: a.w, height: a.h).standardized.insetBy(dx: -tol, dy: -tol).contains(n)
            }
            if hit { selected = a.id; return }
        }
        for r in project.redactions.reversed() where time >= r.start && time < r.end && r.rect.insetBy(dx: -tol, dy: -tol).contains(n) {
            selected = r.id; return
        }
        selected = nil
    }
    // 文字标注直接在预览上改：打字时实时进工程（整次改字算一步撤销），回车 / 点别处收好，Esc 退回原来的字；改完是空的就删掉
    func beginNote(_ id: UUID) { dragBegin(); editingNote = id }
    func typeNote(_ id: UUID, _ text: String) {
        guard dragBase != nil else { return }
        dragUpdate { p in if let i = p.annotations.firstIndex(where: { $0.id == id }) { p.annotations[i].text = text.replacingOccurrences(of: "\n", with: " ") } }
    }
    func endNote(cancel: Bool = false) {
        guard let id = editingNote else { return }
        editingNote = nil
        if cancel, let base = dragBase { project = base }
        if let i = annotationIndex(id), project.annotations[i].text.trimmingCharacters(in: .whitespaces).isEmpty {
            dragUpdate { $0.annotations.removeAll { $0.id == id } }
            selected = nil
        }
        dragEnd()
    }
    func setNoteText(_ id: UUID, _ text: String) {
        guard let i = annotationIndex(id) else { return }
        edit { $0.annotations[i].text = text.replacingOccurrences(of: "\n", with: " ") }
    }
    // 选中：播放头不在它的时间段里就跳过去，预览上马上看得到
    func selectOverlay(_ id: UUID) {
        selected = id; picking = false; drawing = nil
        let range: (Double, Double)? = project.redactions.first { $0.id == id }.map { ($0.start, $0.end) } ?? project.annotations.first { $0.id == id }.map { ($0.start, $0.end) }
        // 刚出现的那一下是淡入 / 箭头正在画（第一帧几乎看不见）：播放头停在出现动画之后
        if let (s, e) = range {
            let ready = s + min(OverlayAnim.draw + 0.05, (e - s) / 2)
            if !(time >= ready && time < e) { seek(ready) }
        }
    }
    // 复制一份（⌘D）：时间往后挪到它结束处，位置稍微错开
    func duplicateOverlay() {
        if let i = redactionIndex(selected) {
            var r = project.redactions[i]; r.id = UUID()
            let len = r.end - r.start; r.start = min(r.end, duration - 0.3); r.end = min(duration, r.start + len)
            edit { $0.redactions.append(r) }; selectOverlay(r.id)
        } else if let i = annotationIndex(selected) {
            var a = project.annotations[i]; a.id = UUID()
            let len = a.end - a.start; a.start = min(a.end, duration - 0.3); a.end = min(duration, a.start + len)
            edit { $0.annotations.append(a) }; selectOverlay(a.id)
        }
    }
    // 改位置 / 大小：拖动开始那一刻的工程 + 这次拖了多远
    func dragOverlayGeo(_ id: UUID, _ change: @escaping (inout OverlayGeo) -> Void) {
        dragBegin()
        dragUpdate { p in
            if let i = p.redactions.firstIndex(where: { $0.id == id }) {
                let r = p.redactions[i]; var g = OverlayGeo(x: r.x, y: r.y, w: r.w, h: r.h); change(&g)
                p.redactions[i].x = g.x; p.redactions[i].y = g.y; p.redactions[i].w = g.w; p.redactions[i].h = g.h
            } else if let i = p.annotations.firstIndex(where: { $0.id == id }) {
                let a = p.annotations[i]; var g = OverlayGeo(x: a.x, y: a.y, w: a.w, h: a.h); change(&g)
                p.annotations[i].x = g.x; p.annotations[i].y = g.y; p.annotations[i].w = g.w; p.annotations[i].h = g.h
            }
        }
    }
    // 时间轴上拖：move = 整段挪，否则改开头 / 结尾
    // 整段挪到从 t 开始（长度不变）
    func moveOverlay(_ id: UUID, to t: Double) {
        edit { p in
            if let i = p.redactions.firstIndex(where: { $0.id == id }) { let l = p.redactions[i].end - p.redactions[i].start; p.redactions[i].start = min(t, duration - l); p.redactions[i].end = p.redactions[i].start + l }
            if let i = p.annotations.firstIndex(where: { $0.id == id }) { let l = p.annotations[i].end - p.annotations[i].start; p.annotations[i].start = min(t, duration - l); p.annotations[i].end = p.annotations[i].start + l }
        }
        selectOverlay(id)
    }
    // 拖文字的角：字号 = 拖之前的 × k（12–160）
    func dragNoteScale(_ id: UUID, _ k: Double) {
        dragBegin()
        guard let base = dragBase?.annotations.first(where: { $0.id == id }) else { return }
        dragUpdate { p in if let i = p.annotations.firstIndex(where: { $0.id == id }) { p.annotations[i].fontSize = min(max(base.fontSize * k, 12), 160) } }
    }
    func dragOverlayTime(_ id: UUID, edge: Int, dt: Double) {
        dragBegin()
        let d = duration, minLen = 0.3
        func apply(_ s0: Double, _ e0: Double) -> (Double, Double) {
            var s = s0, e = e0
            switch edge {
            case 0: let len = e - s; s = min(max(0, s + dt), d - len); e = s + len
            case -1: s = min(max(0, s + dt), e - minLen)
            default: e = max(min(d, e + dt), s + minLen)
            }
            return (s, e)
        }
        dragUpdate { p in
            if let i = p.redactions.firstIndex(where: { $0.id == id }) {
                (p.redactions[i].start, p.redactions[i].end) = apply(p.redactions[i].start, p.redactions[i].end)
            } else if let i = p.annotations.firstIndex(where: { $0.id == id }) {
                (p.annotations[i].start, p.annotations[i].end) = apply(p.annotations[i].start, p.annotations[i].end)
            }
        }
    }
    // 属性条用：选中的那个遮挡 / 标注的某个属性（按钮一次算一步；滑块拖一次算一步）
    func red<T: Equatable>(_ kp: WritableKeyPath<Redaction, T>) -> Binding<T> {
        Binding(get: { self.redactionIndex(self.selected).map { self.project.redactions[$0][keyPath: kp] } ?? Redaction(start: 0, end: 1, x: 0, y: 0, w: 0, h: 0)[keyPath: kp] },
                set: { v in if let i = self.redactionIndex(self.selected) { self.edit { $0.redactions[i][keyPath: kp] = v } } })
    }
    func redSlider(_ kp: WritableKeyPath<Redaction, Double>) -> Binding<Double> {
        Binding(get: { self.redactionIndex(self.selected).map { self.project.redactions[$0][keyPath: kp] } ?? 0 },
                set: { v in guard let i = self.redactionIndex(self.selected) else { return }; self.dragBegin(); self.dragUpdate { $0.redactions[i][keyPath: kp] = v } })
    }
    func note<T: Equatable>(_ kp: WritableKeyPath<Annotation, T>) -> Binding<T> {
        Binding(get: { self.annotationIndex(self.selected).map { self.project.annotations[$0][keyPath: kp] } ?? Annotation(start: 0, end: 1, x: 0, y: 0, w: 0, h: 0)[keyPath: kp] },
                set: { v in if let i = self.annotationIndex(self.selected) { self.edit { $0.annotations[i][keyPath: kp] = v } } })
    }
    func noteSlider(_ kp: WritableKeyPath<Annotation, Double>) -> Binding<Double> {
        Binding(get: { self.annotationIndex(self.selected).map { self.project.annotations[$0][keyPath: kp] } ?? 0 },
                set: { v in guard let i = self.annotationIndex(self.selected) else { return }; self.dragBegin(); self.dragUpdate { $0.annotations[i][keyPath: kp] = v } })
    }
}

// ---- 播放栏上的「标注」：第一项是遮挡（模糊 / 马赛克），下面是箭头、框、圈、高亮、文字 ----
struct OverlayButtons: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        Menu {
            Button { ed.drawing = .redact; ed.picking = false } label: { Label(L("遮挡（模糊 / 马赛克）", "Redact (blur / mosaic)"), systemImage: "eye.slash") }
            Divider()
            ForEach(Annotation.Kind.allCases, id: \.self) { k in
                Button { ed.drawing = .annotate(k); ed.picking = false } label: { Label(Annotation.name(k), systemImage: Annotation.icon(k)) }
            }
        } label: { Label(L("标注", "Annotate"), systemImage: "pencil.tip.crop.circle") }
        .fixedSize()
        .help(L("遮住不想给人看的地方（模糊、马赛克、色块），或加箭头、框、圈、高亮、文字：选好后在预览上拖出来",
                "Hide something (blur, mosaic, solid block) or add an arrow, box, circle, highlight or text: pick one, then drag it out in the preview"))
    }
}

// ---- 预览上的编辑层 ----
struct OverlayCanvas: View {
    @ObservedObject var ed: StudioEditor
    @State private var from: CGPoint?
    @State private var to: CGPoint?
    static let space = "overlayCanvas"

    var body: some View {
        GeometryReader { g in
            let m = ed.overlayMapping()
            let fit0 = OverlayMapping.fit(CGSize(width: m.lay.cw, height: m.lay.ch), in: g.size)
            // 预览放大了：编辑框跟着画面一起放大、挪动
            let z = ed.previewZoom, pn = ed.previewPan
            let fit = CGRect(x: g.size.width / 2 + (fit0.minX - g.size.width / 2) * z + pn.width, y: g.size.height / 2 + (fit0.minY - g.size.height / 2) * z + pn.height,
                             width: fit0.width * z, height: fit0.height * z)
            ZStack(alignment: .topLeading) {
                Color.clear
                if let tool = ed.drawing { drawLayer(tool, m: m, fit: fit, size: g.size) }
                else if let sel = selection(m) { SelectionBox(ed: ed, sel: sel, m: m, fit: fit) }
                if let id = ed.editingNote, let a = ed.project.annotations.first(where: { $0.id == id }) { NoteField(ed: ed, note: a, m: m, fit: fit) }
            }
            .coordinateSpace(name: Self.space)
        }
        .clipped()   // 推近时编辑框可能伸到预览外面：只画预览里的部分
    }

    struct Sel { let id: UUID; let geo: OverlayGeo; let kind: Annotation.Kind?; let active: Bool }
    private func selection(_ m: OverlayMapping) -> Sel? {
        if let i = ed.redactionIndex(ed.selected) {
            let r = ed.project.redactions[i]
            return Sel(id: r.id, geo: OverlayGeo(x: r.x, y: r.y, w: r.w, h: r.h), kind: nil, active: ed.time >= r.start && ed.time < r.end)
        }
        if let i = ed.annotationIndex(ed.selected) {
            let a = ed.project.annotations[i]
            var g = OverlayGeo(x: a.x, y: a.y, w: a.w, h: a.h)
            if a.kind == .text { let b = OverlayLayer.normBounds(a, lay: m.lay, unit: m.ppp); g.w = b.width; g.h = b.height }
            return Sel(id: a.id, geo: g, kind: a.kind, active: ed.time >= a.start && ed.time < a.end)
        }
        return nil
    }

    @ViewBuilder private func drawLayer(_ tool: OverlayTool, m: OverlayMapping, fit: CGRect, size: CGSize) -> some View {
        Color.white.opacity(0.001).frame(width: size.width, height: size.height).contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.crosshair.push() } else { NSCursor.pop() } }
            .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
                .onChanged { v in if from == nil { from = v.startLocation }; to = v.location }
                .onEnded { v in finish(tool, start: v.startLocation, end: v.location, m: m, fit: fit); from = nil; to = nil })
        if let a = from, let b = to {
            if tool == .annotate(.arrow) {
                Path { p in p.move(to: a); p.addLine(to: b) }.stroke(Color.white, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
                    .shadow(color: .black.opacity(0.5), radius: 1).allowsHitTesting(false)
            } else if tool == .annotate(.ellipse) {
                // 圈默认是正圆：按拖得远的那个方向定直径
                let side = max(abs(b.x - a.x), abs(b.y - a.y)), ox = b.x < a.x ? a.x - side : a.x, oy = b.y < a.y ? a.y - side : a.y
                Circle().stroke(Color.white, style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
                    .background(Circle().fill(Color.accentColor.opacity(0.12)))
                    .shadow(color: .black.opacity(0.5), radius: 1)
                    .frame(width: side, height: side).offset(x: ox, y: oy).allowsHitTesting(false)
            } else {
                Rectangle().stroke(Color.white, style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
                    .background(Rectangle().fill(Color.accentColor.opacity(0.12)))
                    .shadow(color: .black.opacity(0.5), radius: 1)
                    .frame(width: abs(b.x - a.x), height: abs(b.y - a.y)).offset(x: min(a.x, b.x), y: min(a.y, b.y)).allowsHitTesting(false)
            }
        }
    }

    private func finish(_ tool: OverlayTool, start: CGPoint, end: CGPoint, m: OverlayMapping, fit: CGRect) {
        guard fit.width > 0 else { return }
        let n0 = m.norm(view: start, fit: fit), n1 = m.norm(view: end, fit: fit)
        let tiny = hypot(end.x - start.x, end.y - start.y) < 8
        func clamp(_ v: Double) -> Double { min(max(v, 0), 1) }
        // 没拖、只点了一下：以这点为中心放一块默认大小的
        func rect(_ dw: Double, _ dh: Double) -> CGRect {
            if tiny { return CGRect(x: clamp(n0.x - dw / 2), y: clamp(n0.y - dh / 2), width: dw, height: dh) }
            let x0 = clamp(min(n0.x, n1.x)), y0 = clamp(min(n0.y, n1.y)), x1 = clamp(max(n0.x, n1.x)), y1 = clamp(max(n0.y, n1.y))
            return CGRect(x: x0, y: y0, width: max(0.01, x1 - x0), height: max(0.01, y1 - y0))
        }
        switch tool {
        case .redact: ed.addRedaction(rect(0.24, 0.10))
        case .annotate(.arrow):
            let a = tiny ? CGPoint(x: n0.x - 0.12, y: n0.y + 0.10) : n0
            ed.addAnnotation(.arrow, OverlayGeo(x: a.x, y: a.y, w: n0.x - a.x + (tiny ? 0 : n1.x - n0.x), h: n0.y - a.y + (tiny ? 0 : n1.y - n0.y)))
        case .annotate(.text): ed.addAnnotation(.text, OverlayGeo(x: clamp(n0.x), y: clamp(n0.y), w: 0, h: 0))
        case .annotate(.ellipse):
            // 正圆：预览上是正方形（预览和原画面等比），换成原画面比例
            var e = end
            if !tiny {
                let side = max(abs(end.x - start.x), abs(end.y - start.y))
                e = CGPoint(x: start.x + (end.x < start.x ? -side : side), y: start.y + (end.y < start.y ? -side : side))
            }
            let n1c = m.norm(view: e, fit: fit)
            let r = tiny ? ed.circleRect(center: n0, diameterW: 0.16)
                         : CGRect(x: min(n0.x, n1c.x), y: min(n0.y, n1c.y), width: max(0.01, abs(n1c.x - n0.x)), height: max(0.01, abs(n1c.y - n0.y)))
            ed.addAnnotation(.ellipse, OverlayGeo(x: r.minX, y: r.minY, w: r.width, h: r.height))
        case .annotate(let k):
            let r = rect(0.22, 0.14)
            ed.addAnnotation(k, OverlayGeo(x: r.minX, y: r.minY, w: r.width, h: r.height))
        }
        ed.drawing = nil
    }
}

// 选中的遮挡 / 标注在预览上的编辑框：框（或箭头的两端）+ 把手；不在它的时间段里时画成虚线淡一点
struct SelectionBox: View {
    @ObservedObject var ed: StudioEditor
    let sel: OverlayCanvas.Sel
    let m: OverlayMapping
    let fit: CGRect
    private let knob = 9.0

    var body: some View {
        let g = sel.geo
        let p0 = m.view(CGPoint(x: g.x, y: g.y), fit: fit), p1 = m.view(CGPoint(x: g.x + g.w, y: g.y + g.h), fit: fit)
        let r = CGRect(x: min(p0.x, p1.x), y: min(p0.y, p1.y), width: abs(p1.x - p0.x), height: abs(p1.y - p0.y))
        let style = StrokeStyle(lineWidth: 1.5, dash: sel.active ? [] : [5, 4])
        ZStack(alignment: .topLeading) {
            if sel.kind == .arrow {
                Path { p in p.move(to: p0); p.addLine(to: p1) }.stroke(Color.accentColor.opacity(0.9), style: style).allowsHitTesting(false)
                // 沿着箭头拖 = 整个挪
                Path { p in p.move(to: p0); p.addLine(to: p1) }.stroke(Color.white.opacity(0.001), lineWidth: 16)
                    .contentShape(Path { p in p.move(to: p0); p.addLine(to: p1) }.strokedPath(StrokeStyle(lineWidth: 16)))
                    .gesture(moveGesture)
                handle(at: p0) { d, g in g.x += d.width; g.y += d.height; g.w -= d.width; g.h -= d.height }
                handle(at: p1) { d, g in g.w += d.width; g.h += d.height }
            } else {
                Rectangle().stroke(Color.accentColor, style: style)
                    .background(Rectangle().fill(Color.white.opacity(0.001)))
                    .frame(width: max(r.width, 4), height: max(r.height, 4)).offset(x: r.minX, y: r.minY)
                    .onHover { inside in if inside { NSCursor.openHand.push() } else { NSCursor.pop() } }
                    .gesture(moveGesture)
                    .simultaneousGesture(TapGesture(count: 2).onEnded { if sel.kind == .text { ed.beginNote(sel.id) } })
                if sel.kind == .text {
                    // 文字：拖四个角改字号（离中心越远字越大）
                    ForEach(0..<4, id: \.self) { k in
                        let sx = k % 2 == 0 ? -1.0 : 1.0, sy = k < 2 ? -1.0 : 1.0
                        Circle().fill(Color.white).overlay(Circle().stroke(Color.accentColor, lineWidth: 1.5))
                            .frame(width: knob, height: knob).shadow(color: .black.opacity(0.35), radius: 1)
                            .contentShape(Rectangle().inset(by: -4))
                            .offset(x: (sx < 0 ? r.minX : r.maxX) - knob / 2, y: (sy < 0 ? r.minY : r.maxY) - knob / 2)
                            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(OverlayCanvas.space))
                                .onChanged { v in
                                    let grow = (v.translation.width * sx + v.translation.height * sy) / 2
                                    ed.dragNoteScale(sel.id, max(0.2, 1 + grow / max(r.width / 2, 10)))
                                }
                                .onEnded { _ in ed.dragEnd() })
                    }
                }
                if sel.kind != .text {
                    let circle = sel.kind == .ellipse && ed.project.annotations.first(where: { $0.id == sel.id })?.oval != true
                    ForEach(0..<8, id: \.self) { k in
                        let hx = [-1, 0, 1, 1, 1, 0, -1, -1][k], hy = [-1, -1, -1, 0, 1, 1, 1, 0][k]
                        handle(at: CGPoint(x: r.midX + Double(hx) * r.width / 2, y: r.midY + Double(hy) * r.height / 2)) { d, g in
                            // 框在原画面里 y 朝下，和预览一样：左 / 上边挪起点，右 / 下边改大小
                            if hx < 0 { g.x += d.width; g.w -= d.width } else if hx > 0 { g.w += d.width }
                            if hy < 0 { g.y += d.height; g.h -= d.height } else if hy > 0 { g.h += d.height }
                            if g.w < 0.01 { g.w = 0.01 }; if g.h < 0.01 { g.h = 0.01 }
                            if circle { ed.keepCircle(&g, movedLeft: hx < 0, movedTop: hy < 0, edgeOnly: hx == 0 || hy == 0, horizontal: hy == 0) }
                        }
                    }
                }
            }
        }
    }

    private var moveGesture: some Gesture {
        DragGesture(minimumDistance: 2, coordinateSpace: .named(OverlayCanvas.space))
            .onChanged { v in
                let d = m.normDelta(v.translation, fit: fit)
                ed.dragOverlayGeo(sel.id) { g in g.x += d.width; g.y += d.height }
            }
            .onEnded { _ in ed.dragEnd() }
    }
    private func handle(at p: CGPoint, _ change: @escaping (CGSize, inout OverlayGeo) -> Void) -> some View {
        Circle().fill(Color.white).overlay(Circle().stroke(Color.accentColor, lineWidth: 1.5))
            .frame(width: knob, height: knob)
            .shadow(color: .black.opacity(0.35), radius: 1)
            .contentShape(Rectangle().inset(by: -4))
            .offset(x: p.x - knob / 2, y: p.y - knob / 2)
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(OverlayCanvas.space))
                .onChanged { v in
                    let d = m.normDelta(v.translation, fit: fit)
                    ed.dragOverlayGeo(sel.id) { g in change(d, &g) }
                }
                .onEnded { _ in ed.dragEnd() })
    }
}

// ---- 时间轴上的一块 ----
struct OverlayBlock: View {
    enum Lane { case redaction, annotation }
    @ObservedObject var ed: StudioEditor
    let id: UUID
    let lane: Lane
    let w: Double, d: Double
    var compact = false     // 和别的叠在一起、分了几行：只放图标

    // 时间上叠在一起的排成几行（贪心：放进第一行放得下的）；返回每个的（第几行，一共几行，最多 3 行）
    static func pack(_ items: [(UUID, Double, Double)]) -> [UUID: (Int, Int)] {
        var ends: [Double] = [], row: [UUID: Int] = [:]
        for (id, s, e) in items.sorted(by: { $0.1 < $1.1 }) {
            if let k = ends.firstIndex(where: { $0 <= s + 1e-6 }) { ends[k] = e; row[id] = k }
            else { ends.append(e); row[id] = ends.count - 1 }   // 叠在一起的各占一行，轨道跟着变高
        }
        let n = max(1, ends.count)
        return row.mapValues { ($0, n) }
    }

    var body: some View {
        let sel = ed.selected == id
        let info = describe()
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(info.color.opacity(sel ? 0.95 : 0.72))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.white.opacity(0.25), lineWidth: sel ? 2 : 0.5)
            HStack(spacing: 3) {
                Image(systemName: info.icon).font(.system(size: compact ? 7 : 9, weight: .bold))
                if !compact { Text(info.label).font(.system(size: 10, weight: .semibold)) }
            }.foregroundStyle(.white).lineLimit(1).padding(.horizontal, 8)
            HStack { edge(-1); Spacer(minLength: 0); edge(1) }
        }
        .clipped()
        .contentShape(Rectangle())
        .gesture(TapGesture(count: 2).onEnded { if info.isText { ed.selectOverlay(id); ed.beginNote(id) } }
            .exclusively(before: TapGesture().onEnded { ed.selectOverlay(id) }))
        .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
            .onChanged { v in if ed.selected != id { ed.selected = id }; ed.dragOverlayTime(id, edge: 0, dt: v.translation.width / w * d) }
            .onEnded { _ in ed.dragEnd() })
        .contextMenu {
            if info.isText { Button(L("改字", "Edit Text")) { ed.selectOverlay(id); ed.beginNote(id) } }
            Button(L("复制一份", "Duplicate")) { ed.selectOverlay(id); ed.duplicateOverlay() }
            Button(L("移到播放头", "Move to Playhead")) { ed.moveOverlay(id, to: ed.time) }
            Divider()
            Button(L("删除", "Delete"), role: .destructive) { ed.selected = id; ed.deleteSelected() }
        }
        .help(lane == .redaction ? L("遮挡：这段时间画面上这一块被遮住；选中后在预览上改位置大小", "Redaction: hides part of the picture here; select it to move or resize it in the preview")
                                 : L("标注：这段时间画面上显示；选中后在预览上改位置，双击文字改内容", "Annotation shown during this time; select it to move it in the preview, double-click text to edit"))
    }

    private func describe() -> (color: Color, icon: String, label: String, isText: Bool) {
        if lane == .redaction, let r = ed.project.redactions.first(where: { $0.id == id }) {
            return (Color(red: 0.36, green: 0.38, blue: 0.46), "eye.slash", Redaction.name(r.style), false)
        }
        if let a = ed.project.annotations.first(where: { $0.id == id }) {
            let c = a.color, light = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b > 0.75
            let col = light ? Color(red: 0.45, green: 0.45, blue: 0.5) : c.color
            return (col, Annotation.icon(a.kind), a.kind == .text && !a.text.isEmpty ? a.text : Annotation.name(a.kind), a.kind == .text)
        }
        return (.gray, "questionmark", "", false)
    }
    private func edge(_ e: Int) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8)
            .overlay(Capsule().fill(Color.white.opacity(0.9)).frame(width: 2.5, height: compact ? 5 : 8))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { ed.selectOverlay(id) }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
                .onChanged { v in if ed.selected != id { ed.selected = id }; ed.dragOverlayTime(id, edge: e, dt: v.translation.width / w * d) }
                .onEnded { _ in ed.dragEnd() })
    }
}

// ---- 属性条 ----
struct RedactionInspector: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        if let i = ed.redactionIndex(ed.selected) {
            let r = ed.project.redactions[i]
            HStack(spacing: 12) {
                Text(L("遮挡", "Redact")).font(.callout.weight(.semibold))
                Text(clock(r.start) + " – " + clock(r.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Picker("", selection: ed.red(\.style)) {
                    ForEach(Redaction.Style.allCases, id: \.self) { Text(Redaction.name($0)).tag($0) }
                }.pickerStyle(.segmented).labelsHidden().fixedSize().help(Redaction.hint(r.style))
                if r.style == .solid {
                    ColorPicker(L("颜色", "Color"), selection: Binding(get: { r.color.color }, set: { c in ed.red(\.color).wrappedValue = RGB(c) }), supportsOpacity: false)
                        .fixedSize()
                } else {
                    Text(L("强度", "Strength")).font(.callout)
                    Slider(value: ed.redSlider(\.strength), in: 0...1, onEditingChanged: { if !$0 { ed.dragEnd() } }).frame(width: 110)
                }
                Text(L("圆角", "Corners")).font(.callout)
                Slider(value: ed.redSlider(\.corner), in: 0...80, onEditingChanged: { if !$0 { ed.dragEnd() } }).frame(width: 80)
                Spacer()
                OverlayActions(ed: ed)
            }.controlSize(.small)
        }
    }
}

struct AnnotationInspector: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        if let i = ed.annotationIndex(ed.selected) {
            let a = ed.project.annotations[i]
            HStack(spacing: 10) {
                Label(Annotation.name(a.kind), systemImage: Annotation.icon(a.kind)).font(.callout.weight(.semibold))
                Text(clock(a.start) + " – " + clock(a.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                HStack(spacing: 4) {
                    ForEach(Annotation.palette.indices, id: \.self) { k in
                        let c = Annotation.palette[k]
                        Button { ed.note(\.color).wrappedValue = c } label: {
                            Circle().fill(c.color).frame(width: 14, height: 14)
                                .overlay(Circle().stroke(a.color == c ? Color.accentColor : Color.secondary.opacity(0.4), lineWidth: a.color == c ? 2 : 0.5))
                        }.buttonStyle(.plain)
                    }
                    ColorPicker("", selection: Binding(get: { a.color.color }, set: { c in ed.note(\.color).wrappedValue = RGB(c) }), supportsOpacity: false)
                        .labelsHidden().fixedSize()
                }
                switch a.kind {
                case .text:
                    Button(L("改文字", "Edit Text")) { ed.beginNote(a.id) }
                    Text(L("字号", "Size")).font(.callout)
                    Slider(value: ed.noteSlider(\.fontSize), in: 12...160, onEditingChanged: { if !$0 { ed.dragEnd() } }).frame(width: 80)
                    Picker("", selection: Binding(get: { a.textLook }, set: { v in ed.edit { $0.annotations[i].textStyle = v; $0.annotations[i].pill = v != .plain } })) {
                        ForEach(Annotation.TextLook.allCases, id: \.self) { Text(Annotation.textLookName($0)).tag($0) }
                    }.labelsHidden().fixedSize()
                case .highlight:
                    EmptyView()
                default:
                    if a.kind == .ellipse {
                        Picker("", selection: Binding(get: { a.oval == true }, set: { v in
                            ed.edit { p in
                                p.annotations[i].oval = v ? true : nil
                                if !v {   // 改回正圆：按宽定直径，中心不动
                                    var g = OverlayGeo(x: p.annotations[i].x, y: p.annotations[i].y, w: p.annotations[i].w, h: p.annotations[i].h)
                                    let cy = g.y + g.h / 2; ed.keepCircle(&g, movedLeft: false, movedTop: false, edgeOnly: true, horizontal: true); g.y = cy - g.h / 2
                                    p.annotations[i].x = g.x; p.annotations[i].y = g.y; p.annotations[i].w = g.w; p.annotations[i].h = g.h
                                }
                            }
                        })) { Text(L("正圆", "Circle")).tag(false); Text(L("椭圆", "Oval")).tag(true) }.pickerStyle(.segmented).fixedSize()
                    }
                    if a.kind == .arrow {
                        Picker("", selection: Binding(get: { a.arrow ?? .plain }, set: { v in ed.edit { $0.annotations[i].arrow = v == .plain ? nil : v } })) {
                            ForEach(Annotation.ArrowStyle.allCases, id: \.self) { Text(Annotation.arrowName($0)).tag($0) }
                        }.labelsHidden().fixedSize()
                    }
                    Text(L("线宽", "Width")).font(.callout)
                    Slider(value: ed.noteSlider(\.width), in: 1...16, onEditingChanged: { if !$0 { ed.dragEnd() } }).frame(width: 70)
                    if a.kind == .arrow { Toggle(L("画出来", "Draw on"), isOn: ed.note(\.drawOn)).toggleStyle(.checkbox).help(L("出现时像笔画出来一样", "Animate the arrow being drawn")) }
                }
                Text(L("不透明度", "Opacity")).font(.callout)
                Slider(value: Binding(get: { a.alpha }, set: { v in ed.dragBegin(); ed.dragUpdate { $0.annotations[i].opacity = v } }), in: 0.1...1,
                       onEditingChanged: { if !$0 { ed.dragEnd() } }).frame(width: 70)
                Text("\(Int((a.alpha * 100).rounded()))%").font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 32, alignment: .leading)
                Spacer()
                OverlayActions(ed: ed)
            }.controlSize(.small)
        }
    }
}

struct OverlayActions: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        Button { ed.duplicateOverlay() } label: { Image(systemName: "plus.square.on.square") }.help(L("复制一份（⌘D）", "Duplicate (⌘D)"))
        Button(role: .destructive) { ed.deleteSelected() } label: { Label(L("删除", "Delete"), systemImage: "trash") }
    }
}

extension NoteField {
    @ViewBuilder func fieldBack(_ col: Color) -> some View {
        switch note.textLook {
        case .pill, .bubble: Capsule().fill(col)
        case .dark: Capsule().fill(Color.black.opacity(0.78))
        case .glass: Capsule().fill(.ultraThinMaterial).overlay(Capsule().stroke(Color.white.opacity(0.7), lineWidth: 1))
        case .glassDark: Capsule().fill(.ultraThinMaterial).overlay(Capsule().fill(Color.black.opacity(0.35))).overlay(Capsule().stroke(Color.white.opacity(0.5), lineWidth: 1))
        case .gradient: Capsule().fill(LinearGradient(colors: [col, col.opacity(0.6)], startPoint: .leading, endPoint: .trailing))
        case .sticker: RoundedRectangle(cornerRadius: 8).fill(Color.white).overlay(RoundedRectangle(cornerRadius: 6).stroke(col, lineWidth: 2).padding(4))
        case .neon: Capsule().fill(Color.black.opacity(0.25))
        case .marker: Capsule().fill(col.opacity(0.45))
        case .outline: Capsule().fill(Color.white.opacity(0.92)).overlay(Capsule().stroke(col, lineWidth: 2))
        case .plain: Capsule().fill(Color.black.opacity(0.2))
        }
    }
}

// 预览上直接打字的文字标注：盖在那条标注上，样子和成片里一样（胶囊底色 + 白字）
struct NoteField: View {
    @ObservedObject var ed: StudioEditor
    let note: Annotation
    let m: OverlayMapping
    let fit: CGRect
    @State private var text = ""
    @FocusState private var focused: Bool
    var body: some View {
        // 原画面的点 → 预览上的点（含推近倍数）
        let k = m.ppp * m.lay.s * (m.f?.z ?? 1) * fit.width / m.lay.cw
        let px = max(9, note.fontSize * k), p = m.view(CGPoint(x: note.x, y: note.y), fit: fit)
        let col = Color(red: note.color.r, green: note.color.g, blue: note.color.b)
        TextField(L("输入文字", "Type here"), text: $text)
            .textFieldStyle(.plain).font(.custom("PingFangSC-Semibold", size: px))
            .foregroundStyle([.pill, .bubble, .dark, .glassDark, .gradient].contains(note.textLook) ? Color.white : [.glass, .marker].contains(note.textLook) ? Color.black : col).focused($focused)
            .fixedSize()
            .frame(minWidth: px * 3)
            .padding(.horizontal, note.textLook != .plain ? px * 0.45 : 2).padding(.vertical, note.textLook != .plain ? px * 0.27 : 1)
            .background(fieldBack(col))
            .overlay(Capsule().stroke(Color.accentColor, lineWidth: 1.5).padding(-3))
            .offset(x: p.x, y: p.y)
            .onAppear { text = note.text; DispatchQueue.main.async { focused = true } }
            .onChange(of: text) { _, v in ed.typeNote(note.id, v) }
            .onSubmit { ed.endNote() }
            .onExitCommand { ed.endNote(cancel: true) }
            .onChange(of: focused) { _, f in if !f { ed.endNote() } }
    }
}
