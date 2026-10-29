// Studio 剪辑窗口（像 Screen Studio）：上面实时预览（和成片同一套合成），下面时间轴上的推近段可以拖长缩短、挪动、删掉、
// 新加，选中后调放大倍数、改「跟着鼠标 / 固定位置」；两头可以修剪；改动自动存进素材夹的 project.json，「导出」出成片
import AppKit
import AVFoundation
import CoreImage
import SwiftUI

// 预览合成在后台线程读最新的镜头计划：改了段落就换一份
final class PlanBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: [FramePlan] = []
    var plan: [FramePlan] { lock.lock(); defer { lock.unlock() }; return value }
    func set(_ p: [FramePlan]) { lock.lock(); value = p; lock.unlock() }
}

@MainActor
final class StudioEditor: NSObject, ObservableObject, NSWindowDelegate {
    private static var open: [URL: StudioEditor] = [:]
    // 自测：EDITORSHOT=图片路径 [EDITOR=素材夹] → 不抢前台，把剪辑窗口画成图片后退出
    static let testShot = ProcessInfo.processInfo.environment["EDITORSHOT"]

    let dir: URL, out: URL
    let track: Track
    let duration: Double
    let renderer: StudioRenderer
    let fps: Double
    let player: AVPlayer
    private let asset: AVURLAsset
    private let box = PlanBox()
    private var window: NSWindow?
    private var timeObserver: Any?
    private var keyMonitor: Any?
    private var saveWork: DispatchWorkItem?
    private var refreshPending = false

    @Published var project: StudioProject { didSet { if project != oldValue { replan(); scheduleSave() } } }
    @Published var selected: UUID?
    @Published var time = 0.0
    @Published var playing = false
    @Published var picking = false             // 正在等用户在预览上点固定位置
    @Published var exportProgress: Double?
    @Published var exported: URL?
    @Published var exportError: String?
    @Published var still: NSImage?             // 截图自测时用静态画面代替播放器
    private var undoStack: [StudioProject] = [], redoStack: [StudioProject] = []
    private var dragBase: StudioProject?

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }
    var plan: [FramePlan] { box.plan }
    var events: [Double] { (track.clicks.map { $0[0] } + track.keys).filter { $0 >= 0 && $0 <= duration } }

    static func open(_ dir: URL) async {
        if let e = open[dir.standardizedFileURL] { e.window?.makeKeyAndOrderFront(nil); return }
        do {
            let e = try await StudioEditor(dir: dir)
            open[dir.standardizedFileURL] = e
            e.show()
        } catch {
            let a = NSAlert()
            a.messageText = L("打不开这份 Studio 素材", "Couldn't open this Studio footage")
            a.informativeText = error.localizedDescription
            a.runModal()
        }
    }

    private init(dir: URL) async throws {
        self.dir = dir
        out = Recorder.folder.appendingPathComponent(dir.lastPathComponent + ".mov")
        track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
        asset = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        duration = try await asset.load(.duration).seconds
        let st = StudioStyle()
        fps = Double(st.fps)
        renderer = StudioRenderer(project: dir, track: track, style: st)
        project = StudioProject.load(dir, track: track, duration: duration)
        player = AVPlayer()
        super.init()
        if !FileManager.default.fileExists(atPath: StudioProject.url(dir).path) { try? project.save(dir) }
        replan()
        let item = AVPlayerItem(asset: asset)
        item.videoComposition = makeComposition()
        item.forwardPlaybackEndTime = CMTime(seconds: project.trimEnd, preferredTimescale: 600)
        player.replaceCurrentItem(with: item)
        player.actionAtItemEnd = .pause
        seek(project.trimStart)
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 30), queue: .main) { [weak self] t in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.time = t.seconds
                let rate = self.player.rate
                if self.playing && rate == 0 { self.playing = false }
            }
        }
    }

    // ---- 窗口 ----
    private func show() {
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 1120, height: 780), styleMask: [.titled, .closable, .miniaturizable, .resizable],
                         backing: .buffered, defer: false)
        w.title = "Studio · " + dir.lastPathComponent
        w.isReleasedWhenClosed = false
        w.contentMinSize = CGSize(width: 820, height: 600)
        w.contentView = NSHostingView(rootView: EditorView(ed: self))
        w.delegate = self
        window = w
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self, e.window === self.window else { return e }
            return self.key(e) ? nil : e
        }
        if let path = Self.testShot {
            NSApp.setActivationPolicy(.accessory)
            w.setFrameOrigin(CGPoint(x: -20000, y: -20000))
            w.orderBack(nil)
            Task { @MainActor in
                selected = project.segments.first?.id
                await renderStill(at: project.segments.first.map { ($0.start + $0.end) / 2 } ?? 1)
                try? await Task.sleep(for: .seconds(0.6))
                if let v = w.contentView, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
                    v.cacheDisplay(in: v.bounds, to: rep)
                    try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
                }
                print("EDITORSHOT OK", path)
                exit(0)
            }
            return
        }
        w.center()
        w.setFrameAutosaveName("StudioEditor")
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        player.pause()
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        saveNow()
        Self.open[dir.standardizedFileURL] = nil
    }

    // 空格 播放/暂停，←/→ 逐帧，Delete 删选中的段，⌘Z / ⌘⇧Z 撤销重做
    private func key(_ e: NSEvent) -> Bool {
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        switch e.keyCode {
        case 49 where !cmd: togglePlay(); return true
        case 123 where !cmd: step(-1); return true
        case 124 where !cmd: step(1); return true
        case 51, 117: deleteSelected(); return true
        case 6 where cmd: shift ? redo() : undo(); return true
        case 53: if picking { picking = false; return true }; return false
        default: return false
        }
    }

    // ---- 预览 ----
    private func makeComposition() -> AVVideoComposition {
        let r = renderer, box = self.box, fps = self.fps
        let lay = r.lay, k = min(1, 1600 / max(lay.cw, lay.ch))          // 预览缩小一点，播放流畅
        let size = CGSize(width: (lay.cw * k / 2).rounded() * 2, height: (lay.ch * k / 2).rounded() * 2)
        let bg = CIImage(color: CIColor(red: 0.86, green: 0.86, blue: 0.88)).cropped(to: CGRect(origin: .zero, size: size))   // 透明的地方衬一层浅灰
        let c = AVMutableVideoComposition(asset: asset) { req in
            let plan = box.plan
            guard !plan.isEmpty else { req.finish(with: req.sourceImage, context: nil); return }
            let i = min(max(Int((req.compositionTime.seconds * fps).rounded()), 0), plan.count - 1)
            let img = r.frame(i, plan: plan, source: req.sourceImage).transformed(by: CGAffineTransform(scaleX: k, y: k))
            req.finish(with: img.composited(over: bg).cropped(to: bg.extent), context: nil)
        }
        c.renderSize = size
        c.frameDuration = CMTime(value: 1, timescale: 30)
        return c
    }

    private func replan() {
        box.set(renderer.plan(project, duration: duration))
        player.currentItem?.forwardPlaybackEndTime = CMTime(seconds: project.trimEnd, preferredTimescale: 600)
        // 拖的时候别每次都重建：一帧里最多刷新一次
        guard !refreshPending else { return }
        refreshPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0 / 20) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.refreshPending = false
                self.player.currentItem?.videoComposition = self.makeComposition()
                if !self.playing { self.seek(self.time) }
                if self.still != nil { Task { await self.renderStill(at: self.time) } }
            }
        }
    }

    // 截图自测：同一个合成函数画一张静态画面
    private func renderStill(at t: Double) async {
        let g = AVAssetImageGenerator(asset: asset)
        g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
        guard let (cg, _) = try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)) else { return }
        let plan = box.plan, i = min(max(Int((t * fps).rounded()), 0), plan.count - 1)
        let lay = renderer.lay
        let img = renderer.frame(i, plan: plan, source: CIImage(cgImage: cg))
            .composited(over: CIImage(color: CIColor(red: 0.86, green: 0.86, blue: 0.88)).cropped(to: lay.canvas))
        let ctx = CIContext()
        if let out = ctx.createCGImage(img, from: lay.canvas) { still = NSImage(cgImage: out, size: CGSize(width: lay.cw, height: lay.ch)) }
        time = t
    }

    // ---- 播放 ----
    func seek(_ t: Double) {
        let t = min(max(t, 0), duration)
        time = t
        player.seek(to: CMTime(seconds: t, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
    }
    func togglePlay() {
        if playing { player.pause(); playing = false; return }
        if time >= project.trimEnd - 0.05 || time < project.trimStart { seek(project.trimStart) }
        player.play(); playing = true
    }
    func step(_ n: Int) {
        player.pause(); playing = false
        seek(((time * fps).rounded() + Double(n)) / fps)
    }

    // ---- 编辑（都能撤销） ----
    func edit(_ change: (inout StudioProject) -> Void) {
        var p = project
        change(&p)
        guard p != project else { return }
        undoStack.append(project); redoStack.removeAll()
        project = p
    }
    // 拖动：开始时记下原样，拖的过程中实时改，松手才算一步撤销
    func dragBegin() { if dragBase == nil { dragBase = project } }
    func dragUpdate(_ change: (inout StudioProject) -> Void) {
        guard let base = dragBase else { return }
        var p = base
        change(&p)
        project = p
    }
    func dragEnd() {
        if let base = dragBase, base != project { undoStack.append(base); redoStack.removeAll() }
        dragBase = nil
    }
    func undo() { guard let p = undoStack.popLast() else { return }; redoStack.append(project); project = p }
    func redo() { guard let p = redoStack.popLast() else { return }; undoStack.append(project); project = p }

    func index(_ id: UUID?) -> Int? { project.segments.firstIndex { $0.id == id } }
    // 某一段能占的范围：不压到前后两段、不出修剪范围
    func room(for id: UUID, in p: StudioProject) -> ClosedRange<Double> {
        let segs = p.segments.sorted { $0.start < $1.start }
        guard let i = segs.firstIndex(where: { $0.id == id }) else { return p.trimStart...p.trimEnd }
        let lo = max(p.trimStart, i > 0 ? segs[i - 1].end : 0)
        let hi = min(p.trimEnd, i + 1 < segs.count ? segs[i + 1].start : duration)
        return lo...max(lo, hi)
    }

    func addSegment(at t: Double) {
        let len = 2.0
        var p = project
        // 找播放头所在的空隙
        let segs = p.segments.sorted { $0.start < $1.start }
        guard !segs.contains(where: { t >= $0.start && t < $0.end }) else { NSSound.beep(); return }
        let lo = max(p.trimStart, segs.last(where: { $0.end <= t })?.end ?? 0)
        let hi = min(p.trimEnd, segs.first(where: { $0.start > t })?.start ?? duration)
        guard hi - lo >= 0.3 else { NSSound.beep(); return }
        var s = max(lo, t - len / 2), e = min(hi, s + len)
        s = max(lo, e - len); e = min(hi, s + len)
        let m = StudioPlan.Mouse(track).at(t)
        let seg = ZoomSegment(start: s, end: e, fixedX: (m?.x ?? renderer.lay.W / 2) / renderer.lay.W, fixedY: 1 - (m?.y ?? renderer.lay.H / 2) / renderer.lay.H)
        p.segments.append(seg); p.segments.sort { $0.start < $1.start }
        edit { $0 = p }
        selected = seg.id
    }
    func deleteSelected() {
        guard let i = index(selected) else { return }
        edit { $0.segments.remove(at: i) }
        selected = nil
    }

    // 在预览上点一下：设成选中那段的固定位置。p = 预览画面里的比例（左上为原点）
    func pick(_ p: CGPoint) {
        guard picking, let i = index(selected) else { return }
        let lay = renderer.lay, plan = box.plan
        let fi = min(max(Int((time * fps).rounded()), 0), plan.count - 1)
        let src = renderer.sourcePoint(canvas: CGPoint(x: p.x * lay.cw, y: (1 - p.y) * lay.ch), frame: plan[fi])
        edit { $0.segments[i].mode = .fixed; $0.segments[i].fixedX = src.x; $0.segments[i].fixedY = src.y }
        picking = false
    }

    // ---- 存盘、导出 ----
    private func scheduleSave() {
        saveWork?.cancel()
        let w = DispatchWorkItem { [weak self] in MainActor.assumeIsolated { self?.saveNow() } }
        saveWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: w)
    }
    private func saveNow() { saveWork?.cancel(); saveWork = nil; if Self.testShot == nil { try? project.save(dir) } }

    func export() {
        guard exportProgress == nil else { return }
        saveNow()
        exported = nil; exportError = nil; exportProgress = 0
        let dir = self.dir, out = self.out, p = project
        Task {
            do {
                try await Studio.render(project: dir, to: out, edits: p) { v in
                    Task { @MainActor [weak self] in self?.exportProgress = v }
                }
                exported = out
            } catch {
                exportError = L("导出失败：", "Export failed: ") + error.localizedDescription
            }
            exportProgress = nil
        }
    }
}

// ---- 界面 ----
struct EditorView: View {
    @ObservedObject var ed: StudioEditor

    var body: some View {
        VStack(spacing: 0) {
            toolbar.padding(.horizontal, 14).padding(.vertical, 10)
            Divider()
            preview.padding(14)
            Divider()
            VStack(alignment: .leading, spacing: 10) {
                inspector.frame(height: 30)
                TimelineStrip(ed: ed).frame(height: 120)
            }
            .padding(14)
        }
        .frame(minWidth: 820, minHeight: 600)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var toolbar: some View {
        HStack(spacing: 12) {
            Button { ed.togglePlay() } label: { Image(systemName: ed.playing ? "pause.fill" : "play.fill").frame(width: 18) }
                .help(L("播放 / 暂停（空格）", "Play / Pause (Space)"))
            Text(clock(ed.time - ed.project.trimStart) + " / " + clock(ed.project.trimEnd - ed.project.trimStart))   // 按修剪后的成片算
                .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
            Divider().frame(height: 18)
            Button { ed.addSegment(at: ed.time) } label: { Label(L("推近", "Zoom"), systemImage: "plus.magnifyingglass") }
                .help(L("在播放头处加一段推近（也可以在时间轴空白处双击）", "Add a zoom at the playhead (or double-click the timeline)"))
            Button { ed.undo() } label: { Image(systemName: "arrow.uturn.backward") }.disabled(!ed.canUndo).help(L("撤销 ⌘Z", "Undo ⌘Z"))
            Button { ed.redo() } label: { Image(systemName: "arrow.uturn.forward") }.disabled(!ed.canRedo).help(L("重做 ⌘⇧Z", "Redo ⌘⇧Z"))
            Spacer()
            if let p = ed.exportProgress {
                ProgressView(value: p).frame(width: 160)
                Text("\(Int(p * 100))%").font(.callout.monospacedDigit()).foregroundStyle(.secondary)
            } else if let u = ed.exported {
                Text(L("已导出", "Exported")).font(.callout).foregroundStyle(.secondary)
                Button(L("打开", "Open")) { NSWorkspace.shared.open(u) }
                Button(L("在访达中显示", "Show in Finder")) { NSWorkspace.shared.activateFileViewerSelecting([u]) }
            } else if let e = ed.exportError {
                Text(e).font(.callout).foregroundStyle(.red).lineLimit(1)
            }
            Button(L("导出", "Export")) { ed.export() }.buttonStyle(.borderedProminent).disabled(ed.exportProgress != nil)
        }
    }

    private var preview: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .underPageBackgroundColor))
            if let img = ed.still {
                Image(nsImage: img).resizable().aspectRatio(contentMode: .fit)
            } else {
                PlayerPane(player: ed.player, picking: ed.picking) { ed.pick($0) }
            }
            if ed.picking {
                VStack {
                    Text(L("在画面上点一下，设成这段推近盯住的位置（Esc 取消）", "Click the picture to set where this zoom looks (Esc to cancel)"))
                        .font(.callout).padding(.horizontal, 12).padding(.vertical, 6)
                        .background(.regularMaterial, in: Capsule())
                    Spacer()
                }.padding(10).allowsHitTesting(false)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private var inspector: some View {
        if let i = ed.index(ed.selected) {
            let seg = ed.project.segments[i]
            HStack(spacing: 14) {
                Text(L("推近", "Zoom")).font(.callout.weight(.semibold))
                Text(clock(seg.start) + " – " + clock(seg.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Text(L("放大", "Scale")).font(.callout)
                Slider(value: Binding(get: { seg.zoom }, set: { v in ed.dragBegin(); ed.dragUpdate { $0.segments[i].zoom = (v * 10).rounded() / 10 } }),
                       in: 1.2...4, onEditingChanged: { if !$0 { ed.dragEnd() } })
                    .frame(width: 160)
                Text(String(format: "%.1f×", seg.zoom)).font(.callout.monospacedDigit()).frame(width: 40, alignment: .leading)
                Picker("", selection: Binding(get: { seg.mode }, set: { m in ed.edit { $0.segments[i].mode = m } })) {
                    Text(L("跟着鼠标", "Follow cursor")).tag(ZoomSegment.Mode.follow)
                    Text(L("固定位置", "Fixed position")).tag(ZoomSegment.Mode.fixed)
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 200)
                if seg.mode == .fixed {
                    Button(ed.picking ? L("点画面…", "Click the picture…") : L("选位置", "Set Position")) { ed.picking.toggle() }
                }
                Spacer()
                Button(role: .destructive) { ed.deleteSelected() } label: { Label(L("删除", "Delete"), systemImage: "trash") }
            }
        } else {
            HStack {
                Text(L("点选时间轴上的推近段来调整；拖两头改长短，拖中间挪位置；空白处双击加一段。两端的竖条是修剪。",
                       "Select a zoom on the timeline to edit it; drag its ends to resize or its middle to move; double-click empty space to add one. The bars at both ends trim the video."))
                    .font(.callout).foregroundStyle(.secondary)
                Spacer()
            }
        }
    }
}

func clock(_ t: Double) -> String {
    let t = max(0, t)
    return String(format: "%d:%02d.%d", Int(t) / 60, Int(t) % 60, Int((t * 10).truncatingRemainder(dividingBy: 10)))
}

// 播放器画面；选固定位置时把点击换算成画面里的比例
struct PlayerPane: NSViewRepresentable {
    let player: AVPlayer
    var picking: Bool
    let onPick: (CGPoint) -> Void
    func makeNSView(context: Context) -> PlayerNSView { let v = PlayerNSView(); v.layerP.player = player; v.onPick = onPick; return v }
    func updateNSView(_ v: PlayerNSView, context: Context) { v.picking = picking; v.onPick = onPick }
}
final class PlayerNSView: NSView {
    let layerP = AVPlayerLayer()
    var picking = false { didSet { window?.invalidateCursorRects(for: self) } }
    var onPick: ((CGPoint) -> Void)?
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer = CALayer()
        layerP.videoGravity = .resizeAspect
        layer?.addSublayer(layerP)
    }
    required init?(coder: NSCoder) { fatalError() }
    override func layout() { super.layout(); CATransaction.begin(); CATransaction.setDisableActions(true); layerP.frame = bounds; CATransaction.commit() }
    override func resetCursorRects() { if picking { addCursorRect(bounds, cursor: .crosshair) } }
    override func mouseDown(with e: NSEvent) {
        guard picking else { return }
        let p = convert(e.locationInWindow, from: nil), r = layerP.videoRect
        guard r.width > 0, r.contains(p) else { return }
        onPick?(CGPoint(x: (p.x - r.minX) / r.width, y: 1 - (p.y - r.minY) / r.height))
    }
}

// ---- 时间轴：刻度、事件小点、推近段、修剪把手、播放头 ----
struct TimelineStrip: View {
    @ObservedObject var ed: StudioEditor
    private let rulerH = 22.0, segY = 40.0, segH = 46.0
    private let accent = Color(red: 0.42, green: 0.36, blue: 0.95)

    var body: some View {
        GeometryReader { g in
            let w = g.size.width, d = max(ed.duration, 0.01)
            let x = { (t: Double) in t / d * w }
            let tAt = { (px: Double) in min(max(px / w * d, 0), d) }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .controlBackgroundColor))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color(nsColor: .separatorColor)))
                    .gesture(SpatialTapGesture(count: 2).onEnded { ed.addSegment(at: tAt($0.location.x)) }
                        .exclusively(before: SpatialTapGesture().onEnded { ed.selected = nil; ed.picking = false; ed.seek(tAt($0.location.x)) }))
                ruler(w: w, d: d, x: x)
                    .contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 0).onChanged { ed.player.pause(); ed.playing = false; ed.seek(tAt($0.location.x)) })
                // 点击、打字的时刻
                ForEach(Array(ed.events.enumerated()), id: \.offset) { _, t in
                    Circle().fill(Color.secondary.opacity(0.55)).frame(width: 4, height: 4).position(x: x(t), y: rulerH + 9)
                }.allowsHitTesting(false)
                ForEach(ed.project.segments) { seg in
                    SegmentBlock(ed: ed, seg: seg, w: w, d: d, accent: accent).frame(width: max(6, x(seg.end) - x(seg.start)), height: segH)
                        .offset(x: x(seg.start), y: segY)
                }
                // 修剪范围外压暗
                Rectangle().fill(Color.black.opacity(0.28)).frame(width: max(0, x(ed.project.trimStart)), height: g.size.height).allowsHitTesting(false)
                Rectangle().fill(Color.black.opacity(0.28)).frame(width: max(0, w - x(ed.project.trimEnd)), height: g.size.height)
                    .offset(x: x(ed.project.trimEnd)).allowsHitTesting(false)
                trimHandle(isStart: true, w: w, d: d, h: g.size.height).offset(x: x(ed.project.trimStart) - 5)
                trimHandle(isStart: false, w: w, d: d, h: g.size.height).offset(x: x(ed.project.trimEnd) - 5)
                // 播放头
                Rectangle().fill(Color.red).frame(width: 2, height: g.size.height).offset(x: x(ed.time) - 1).allowsHitTesting(false)
                Path { p in let px = x(ed.time); p.move(to: CGPoint(x: px - 6, y: 0)); p.addLine(to: CGPoint(x: px + 6, y: 0)); p.addLine(to: CGPoint(x: px, y: 8)); p.closeSubpath() }
                    .fill(Color.red).allowsHitTesting(false)
            }
        }
    }

    private func ruler(w: Double, d: Double, x: @escaping (Double) -> Double) -> some View {
        let step: Double = d > 120 ? 10 : d > 40 ? 5 : d > 12 ? 2 : 1
        return ZStack(alignment: .topLeading) {
            Color.clear.frame(width: w, height: rulerH)
            ForEach(0...Int(d / step), id: \.self) { k in
                let t = Double(k) * step
                VStack(alignment: .leading, spacing: 1) {
                    Rectangle().fill(Color.secondary.opacity(0.5)).frame(width: 1, height: 6)
                    Text("\(Int(t) / 60):\(String(format: "%02d", Int(t) % 60))").font(.system(size: 9).monospacedDigit()).foregroundStyle(.secondary)
                }.offset(x: x(t))
            }
        }.frame(width: w, height: rulerH, alignment: .topLeading)
    }

    private func trimHandle(isStart: Bool, w: Double, d: Double, h: Double) -> some View {
        RoundedRectangle(cornerRadius: 3).fill(Color.yellow).frame(width: 10, height: h)
            .overlay(Capsule().fill(Color.black.opacity(0.45)).frame(width: 2, height: 18))
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .local)
                .onChanged { v in
                    ed.dragBegin()
                    let dt = v.translation.width / w * d
                    ed.dragUpdate { p in
                        if isStart { p.trimStart = min(max(0, p.trimStart + dt), p.trimEnd - 0.5) }
                        else { p.trimEnd = max(min(ed.duration, p.trimEnd + dt), p.trimStart + 0.5) }
                    }
                    ed.seek(isStart ? ed.project.trimStart : ed.project.trimEnd)
                }
                .onEnded { _ in ed.dragEnd() })
            .help(isStart ? L("拖动修剪开头", "Drag to trim the start") : L("拖动修剪结尾", "Drag to trim the end"))
    }
}

struct SegmentBlock: View {
    @ObservedObject var ed: StudioEditor
    let seg: ZoomSegment
    let w: Double, d: Double
    let accent: Color
    private let minLen = 0.3

    var body: some View {
        let sel = ed.selected == seg.id
        ZStack {
            RoundedRectangle(cornerRadius: 7).fill(accent.opacity(sel ? 0.95 : 0.6))
                .overlay(RoundedRectangle(cornerRadius: 7).stroke(sel ? Color.white : Color.clear, lineWidth: 2))
            HStack(spacing: 3) {
                Image(systemName: seg.mode == .follow ? "cursorarrow.motionlines" : "scope").font(.system(size: 10, weight: .semibold))
                Text(String(format: "%.1f×", seg.zoom)).font(.system(size: 11, weight: .semibold).monospacedDigit())
            }
            .foregroundStyle(.white).lineLimit(1).padding(.horizontal, 10)
            HStack {
                edge(isStart: true)
                Spacer(minLength: 0)
                edge(isStart: false)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = seg.id; ed.picking = false }
        .gesture(DragGesture(minimumDistance: 2)
            .onChanged { v in
                ed.selected = seg.id
                ed.dragBegin()
                let dt = v.translation.width / w * d
                ed.dragUpdate { p in
                    guard let i = p.segments.firstIndex(where: { $0.id == seg.id }) else { return }
                    let base = p.segments[i], r = ed.room(for: seg.id, in: p), len = base.end - base.start
                    let s = min(max(base.start + dt, r.lowerBound), r.upperBound - len)
                    p.segments[i].start = s; p.segments[i].end = s + len
                }
            }
            .onEnded { _ in ed.dragEnd() })
    }

    private func edge(isStart: Bool) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 9)
            .overlay(Capsule().fill(Color.white.opacity(0.85)).frame(width: 3, height: 16))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .gesture(DragGesture(minimumDistance: 1)
                .onChanged { v in
                    ed.selected = seg.id
                    ed.dragBegin()
                    let dt = v.translation.width / w * d
                    ed.dragUpdate { p in
                        guard let i = p.segments.firstIndex(where: { $0.id == seg.id }) else { return }
                        let r = ed.room(for: seg.id, in: p)
                        if isStart { p.segments[i].start = min(max(p.segments[i].start + dt, r.lowerBound), p.segments[i].end - minLen) }
                        else { p.segments[i].end = max(min(p.segments[i].end + dt, r.upperBound), p.segments[i].start + minLen) }
                    }
                }
                .onEnded { _ in ed.dragEnd() })
    }
}

// ---- 自测 ----
enum StudioTests {
    // PLANTEST=素材夹 → 给一份改过的工程，看镜头计划对不对（段内放大倍数、段外拉远、固定位置、修剪帧数），打印 PASS/FAIL 后退出
    @MainActor static func planTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["PLANTEST"] else { return false }
        Task { @MainActor in
            var ok = true
            func check(_ name: String, _ cond: Bool, _ info: String = "") { print(cond ? "PASS" : "FAIL", name, info); ok = ok && cond }
            do {
                let dir = URL(fileURLWithPath: path)
                let track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
                let duration = try await AVURLAsset(url: dir.appendingPathComponent("raw.mp4")).load(.duration).seconds
                let st = StudioStyle(), r = StudioRenderer(project: dir, track: track, style: st), lay = r.lay
                let auto = StudioProject.load(dir, track: track, duration: duration)
                check("auto segments", !auto.segments.isEmpty, "\(auto.segments.count) segs")
                let p = StudioProject(segments: [
                    ZoomSegment(start: 1, end: 5, zoom: 3.0),
                    ZoomSegment(start: 8, end: 12, zoom: 2.0, mode: .fixed, fixedX: 0.2, fixedY: 0.3),
                ], trimStart: 1, trimEnd: 13)
                let plan = r.plan(p, duration: duration), fps = Double(st.fps)
                let f = { (t: Double) in plan[Int(t * fps)] }
                check("zoom inside follow segment", abs(f(4.5).z - 3.0) < 0.05, String(format: "z=%.3f", f(4.5).z))
                check("zoomed out between segments", f(7.5).z < 1.08, String(format: "z=%.3f", f(7.5).z))
                // 期望值按计划里的规则算：目标先在原画面里夹住（推近后的视野不出画面），再换到画布
                let tf = f(11.5), hw = lay.cw / (2 * 2.0 * lay.s), hh = lay.ch / (2 * 2.0 * lay.s)
                let fx = min(max(0.2 * lay.W, hw), lay.W - hw), fy = min(max((1 - 0.3) * lay.H, hh), lay.H - hh)
                let wantX = lay.card.minX + fx * lay.s, wantY = lay.card.minY + fy * lay.s
                check("fixed segment zoom", abs(tf.z - 2.0) < 0.05, String(format: "z=%.3f", tf.z))
                check("fixed segment aims at point", abs(tf.sx - wantX) < lay.cw * 0.01 && abs(tf.sy - wantY) < lay.ch * 0.01,
                      String(format: "got (%.0f,%.0f) want (%.0f,%.0f)", tf.sx, tf.sy, wantX, wantY))
                check("after last segment zoomed out", f(min(duration - 0.1, 14.5)).z < 1.08)
                // 在预览画面正中点一下 → 换算回原画面的位置，应该就是镜头对准的那点
                let pt = r.sourcePoint(canvas: CGPoint(x: lay.cw / 2, y: lay.ch / 2), frame: tf)
                check("click → source point", abs(pt.x - fx / lay.W) < 0.01 && abs(pt.y - (1 - fy / lay.H)) < 0.01, String(format: "(%.3f, %.3f)", pt.x, pt.y))
                let frames = Int((p.trimEnd * fps).rounded()) - Int((p.trimStart * fps).rounded())
                check("trim frame count", frames == 720, "\(frames)")
            } catch { check("load", false, "\(error)") }
            print(ok ? "PLANTEST OK" : "PLANTEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }

    // EDITORSHOT=图片路径 EDITOR=素材夹 → 打开剪辑窗口（不抢前台）画成图片
    @MainActor static func editorShot() -> Bool {
        guard StudioEditor.testShot != nil, let p = ProcessInfo.processInfo.environment["EDITOR"] else { return false }
        Task { await StudioEditor.open(URL(fileURLWithPath: p)) }
        return true
    }
}
