// Studio 剪辑窗口（像 Screen Studio）：上面实时预览（和成片同一套合成），下面时间轴上的推近段可以拖长缩短、挪动、删掉、
// 新加，选中后调放大倍数、改「跟着鼠标 / 固定位置」；两头可以修剪；改动自动存进素材夹的 project.json，「导出」出成片
import AppKit
import AVFoundation
import CoreImage
import SwiftUI

// 预览合成在后台线程读最新的渲染器和镜头计划：改了段落 / 样式就换一份（两样一起换，免得对不上）
final class PlanBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: [FramePlan] = []
    private var r: StudioRenderer
    init(_ r: StudioRenderer) { self.r = r }
    var plan: [FramePlan] { lock.lock(); defer { lock.unlock() }; return value }
    var renderer: StudioRenderer { lock.lock(); defer { lock.unlock() }; return r }
    var both: (StudioRenderer, [FramePlan]) { lock.lock(); defer { lock.unlock() }; return (r, value) }
    func set(_ p: [FramePlan], renderer: StudioRenderer) { lock.lock(); value = p; r = renderer; lock.unlock() }
}

@MainActor
final class StudioEditor: NSObject, ObservableObject, NSWindowDelegate {
    private static var open: [URL: StudioEditor] = [:]
    // 自测：EDITORSHOT=图片路径 [EDITOR=素材夹] → 不抢前台，把剪辑窗口画成图片后退出
    static let testShot = ProcessInfo.processInfo.environment["EDITORSHOT"]

    let dir: URL
    let track: Track
    let duration: Double
    let style = StudioStyle()
    let fps: Double
    let player: AVPlayer
    private let asset: AVURLAsset
    private let box: PlanBox
    var renderer: StudioRenderer { box.renderer }
    // 成片路径：透明背景 .mov，其他 .mp4
    var out: URL { Studio.output(Recorder.folder.appendingPathComponent(dir.lastPathComponent + ".mov"), look: project.look) }
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
    @Published var presets = PresetStore.load()
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
        track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
        asset = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
        duration = try await asset.load(.duration).seconds
        fps = Double(StudioStyle().fps)
        let p = StudioProject.load(dir, track: track, duration: duration)
        project = p
        box = PlanBox(StudioRenderer(project: dir, track: track, style: StudioStyle(), look: p.look))
        player = AVPlayer()
        super.init()
        if !FileManager.default.fileExists(atPath: StudioProject.url(dir).path) { try? project.save(dir) }
        box.set(box.renderer.plan(project, duration: duration), renderer: box.renderer)
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
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 1280, height: 820), styleMask: [.titled, .closable, .miniaturizable, .resizable],
                         backing: .buffered, defer: false)
        w.title = "Studio · " + dir.lastPathComponent
        w.isReleasedWhenClosed = false
        w.contentMinSize = CGSize(width: 1000, height: 640)
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
        let box = self.box, fps = self.fps
        let lay = box.renderer.lay, k = min(1, 1600 / max(lay.cw, lay.ch))          // 预览缩小一点，播放流畅
        let size = CGSize(width: (lay.cw * k / 2).rounded() * 2, height: (lay.ch * k / 2).rounded() * 2)
        let bg = CIImage(color: CIColor(red: 0.86, green: 0.86, blue: 0.88)).cropped(to: CGRect(origin: .zero, size: size))   // 透明的地方衬一层浅灰
        let c = AVMutableVideoComposition(asset: asset) { req in
            let (r, plan) = box.both
            guard !plan.isEmpty else { req.finish(with: req.sourceImage, context: nil); return }
            let i = min(max(Int((req.compositionTime.seconds * fps).rounded()), 0), plan.count - 1)
            let kk = min(1, 1600 / max(r.lay.cw, r.lay.ch))   // 样式刚改、合成还没换时按新画布缩放
            let img = r.frame(i, plan: plan, source: req.sourceImage).transformed(by: CGAffineTransform(scaleX: kk, y: kk))
            req.finish(with: img.composited(over: bg).cropped(to: bg.extent), context: nil)
        }
        c.renderSize = size
        c.frameDuration = CMTime(value: 1, timescale: 30)
        return c
    }

    private func replan() {
        player.currentItem?.forwardPlaybackEndTime = CMTime(seconds: project.trimEnd, preferredTimescale: 600)
        // 拖的时候别每次都重建：每秒最多刷新 20 次。样式变了要换渲染器（画布、背景都可能变）
        guard !refreshPending else { return }
        refreshPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0 / 20) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.refreshPending = false
                var r = self.box.renderer
                if r.look != self.project.look { r = StudioRenderer(project: self.dir, track: self.track, style: self.style, look: self.project.look) }
                self.box.set(r.plan(self.project, duration: self.duration), renderer: r)
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
        let (r, plan) = box.both, i = min(max(Int((t * fps).rounded()), 0), plan.count - 1)
        let lay = r.lay
        let img = r.frame(i, plan: plan, source: CIImage(cgImage: cg))
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
        let (r, plan) = box.both, lay = r.lay
        let fi = min(max(Int((time * fps).rounded()), 0), plan.count - 1)
        let src = r.sourcePoint(canvas: CGPoint(x: p.x * lay.cw, y: (1 - p.y) * lay.ch), frame: plan[fi])
        edit { $0.segments[i].mode = .fixed; $0.segments[i].fixedX = src.x; $0.segments[i].fixedY = src.y }
        picking = false
    }

    // ---- 样式（都能撤销；滑块拖一次算一步） ----
    func look<T: Equatable>(_ kp: WritableKeyPath<StudioLook, T>) -> Binding<T> {
        Binding(get: { self.project.look[keyPath: kp] }, set: { v in self.edit { $0.look[keyPath: kp] = v } })
    }
    func lookSlider(_ kp: WritableKeyPath<StudioLook, Double>) -> Binding<Double> {
        Binding(get: { self.project.look[keyPath: kp] }, set: { v in self.dragBegin(); self.dragUpdate { $0.look[keyPath: kp] = v } })
    }
    func chooseImage() {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.image]; p.allowsMultipleSelection = false
        p.message = L("选一张图片当背景", "Choose an image for the background")
        guard p.runModal() == .OK, let u = p.url else { return }
        let bm = try? u.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil)
        edit { $0.look.background = .image; $0.look.imagePath = u.path; $0.look.imageBookmark = bm }
    }

    // ---- 预设：存在 ~/Library/Application Support/<bundle id>/presets.json ----
    func applyPreset(_ name: String) {
        guard let pr = presets.presets.first(where: { $0.name == name }) else { return }
        edit { $0.look = pr.look }
    }
    func savePreset(_ name: String) {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty else { return }
        if let i = presets.presets.firstIndex(where: { $0.name == n }) { presets.presets[i].look = project.look }
        else { presets.presets.append(StudioPreset(name: n, look: project.look)) }
        PresetStore.save(presets)
    }
    func deletePreset(_ name: String) {
        presets.presets.removeAll { $0.name == name }
        if presets.defaultName == name { presets.defaultName = nil }
        PresetStore.save(presets)
    }
    func setDefaultPreset(_ name: String?) { presets.defaultName = name; PresetStore.save(presets) }
    var currentPresetName: String? { presets.presets.first { $0.look == project.look }?.name }

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
                exported = try await Studio.render(project: dir, to: out, edits: p) { v in
                    Task { @MainActor [weak self] in self?.exportProgress = v }
                }
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
            HStack(spacing: 0) {
                VStack(spacing: 0) {
                    preview.padding(14)
                    Divider()
                    VStack(alignment: .leading, spacing: 10) {
                        inspector.frame(height: 30)
                        TimelineStrip(ed: ed).frame(height: 120)
                    }
                    .padding(14)
                }
                Divider()
                StylePanel(ed: ed).frame(width: 300)
            }
        }
        .frame(minWidth: 1000, minHeight: 640)
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
                        .offset(x: k == 0 ? 7 : 0)   // 0:00 别被左边的修剪竖条挡住
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

// ---- 右边的「样式」面板：预设、画面比例、背景、画面、鼠标、运动模糊 ----
struct StylePanel: View {
    @ObservedObject var ed: StudioEditor
    @State private var naming = false
    @State private var newName = ""
    @State private var open: Set<String> = ["preset", "aspect", "bg", "frame", "cursor", "blur"]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 4) {
                section("preset", L("预设", "Presets")) { presetsBox }
                section("aspect", L("画面比例", "Aspect Ratio")) { aspectBox }
                section("bg", L("背景", "Background")) { backgroundBox }
                section("frame", L("画面", "Frame")) { frameBox }
                section("cursor", L("鼠标", "Cursor")) { cursorBox }
                section("blur", L("运动模糊", "Motion Blur")) { blurBox }
            }
            .frame(width: 270, alignment: .leading)   // 面板 300 宽，左右各留 15
            .padding(.horizontal, 15).padding(.vertical, 12)
        }
        .alert(L("存成预设", "Save as Preset"), isPresented: $naming) {
            TextField(L("名字", "Name"), text: $newName)
            Button(L("存", "Save")) { ed.savePreset(newName) }
            Button(L("取消", "Cancel"), role: .cancel) {}
        } message: { Text(L("同名的会被覆盖", "A preset with the same name is replaced")) }
    }

    private func section<C: View>(_ id: String, _ title: String, @ViewBuilder _ content: () -> C) -> some View {
        let body = content()
        return DisclosureGroup(isExpanded: Binding(get: { open.contains(id) }, set: { if $0 { open.insert(id) } else { open.remove(id) } })) {
            VStack(alignment: .leading, spacing: 10) { body }.padding(.top, 8).padding(.bottom, 10)
        } label: {
            Text(title).font(.callout.weight(.semibold))
        }
    }

    // 一行：名字 + 滑块 + 数值
    private func slider(_ title: String, _ kp: WritableKeyPath<StudioLook, Double>, _ range: ClosedRange<Double>, step: Double? = nil, _ fmt: @escaping (Double) -> String) -> some View {
        HStack(spacing: 8) {
            Text(title).font(.callout).frame(width: 62, alignment: .leading)
            Slider(value: ed.lookSlider(kp), in: range, onEditingChanged: { if !$0 { ed.dragEnd() } }).controlSize(.small)
            Text(fmt(ed.project.look[keyPath: kp])).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 40, alignment: .trailing)
        }
    }

    private var presetsBox: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("", selection: Binding(get: { ed.currentPresetName ?? "" }, set: { if !$0.isEmpty { ed.applyPreset($0) } })) {
                Text(L("（未保存的样式）", "(Unsaved style)")).tag("")
                ForEach(ed.presets.presets) { p in
                    Text(p.name + (ed.presets.defaultName == p.name ? L("（默认）", " (Default)") : "")).tag(p.name)
                }
            }.labelsHidden()
            HStack(spacing: 8) {
                Button(L("存成预设…", "Save…")) { newName = ed.currentPresetName ?? ""; naming = true }
                if let n = ed.currentPresetName {
                    Button(ed.presets.defaultName == n ? L("取消默认", "Unset Default") : L("设为默认", "Make Default")) {
                        ed.setDefaultPreset(ed.presets.defaultName == n ? nil : n)
                    }
                    Button(role: .destructive) { ed.deletePreset(n) } label: { Image(systemName: "trash") }.help(L("删除这个预设", "Delete this preset"))
                }
            }.controlSize(.small)
            Text(L("「默认」预设会用在以后新录的 Studio 素材上", "The default preset is used for new Studio recordings"))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private var aspectBox: some View {
        Picker("", selection: ed.look(\.aspect)) {
            Text(L("原始", "Original")).tag(StudioLook.Aspect.original)
            Text("16:9").tag(StudioLook.Aspect.wide)
            Text("9:16").tag(StudioLook.Aspect.tall)
            Text("1:1").tag(StudioLook.Aspect.square)
            Text("4:3").tag(StudioLook.Aspect.classic)
        }.pickerStyle(.segmented).labelsHidden()
    }

    private var backgroundBox: some View {
        let look = ed.project.look
        return VStack(alignment: .leading, spacing: 10) {
            Picker("", selection: ed.look(\.background)) {
                Text(L("透明", "None")).tag(StudioLook.Background.transparent)
                Text(L("纯色", "Color")).tag(StudioLook.Background.color)
                Text(L("渐变", "Gradient")).tag(StudioLook.Background.gradient)
                Text(L("壁纸", "Wallpaper")).tag(StudioLook.Background.wallpaper)
                Text(L("图片", "Image")).tag(StudioLook.Background.image)
            }.pickerStyle(.segmented).labelsHidden()
            switch look.background {
            case .transparent:
                Text(L("透明背景导出成 .mov（HEVC 带透明），放进剪辑软件可以叠在别的画面上；其他背景导出成 .mp4",
                       "Transparent exports as .mov (HEVC with alpha) for overlaying in an editor; other backgrounds export as .mp4"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            case .color:
                ColorPicker(L("颜色", "Color"), selection: Binding(get: { look.color.color }, set: { c in ed.edit { $0.look.color = RGB(c) } }), supportsOpacity: false)
            case .gradient:
                LazyVGrid(columns: Array(repeating: GridItem(.fixed(38), spacing: 6), count: 6), spacing: 6) {
                    ForEach(Array(StudioLook.gradients.enumerated()), id: \.offset) { _, g in
                        let on = look.gradient0 == g.a && look.gradient1 == g.b
                        RoundedRectangle(cornerRadius: 6)
                            .fill(LinearGradient(colors: [g.a.color, g.b.color], startPoint: .bottomLeading, endPoint: .topTrailing))
                            .frame(width: 38, height: 26)
                            .overlay(RoundedRectangle(cornerRadius: 6).stroke(on ? Color.accentColor : Color.primary.opacity(0.15), lineWidth: on ? 2 : 1))
                            .onTapGesture { ed.edit { $0.look.gradient0 = g.a; $0.look.gradient1 = g.b } }
                            .help(L(g.zh, g.en))
                    }
                }
                HStack(spacing: 14) {
                    ColorPicker(L("起点", "From"), selection: Binding(get: { look.gradient0.color }, set: { c in ed.edit { $0.look.gradient0 = RGB(c) } }), supportsOpacity: false)
                    ColorPicker(L("终点", "To"), selection: Binding(get: { look.gradient1.color }, set: { c in ed.edit { $0.look.gradient1 = RGB(c) } }), supportsOpacity: false)
                }.font(.callout)
                slider(L("角度", "Angle"), \.gradientAngle, 0...360) { "\(Int($0))°" }
            case .wallpaper:
                LazyVGrid(columns: Array(repeating: GridItem(.fixed(84), spacing: 6), count: 3), spacing: 6) {
                    ForEach(StudioLook.wallpapers, id: \.path) { u in
                        let on = look.wallpaper == u.path
                        WallpaperThumb(url: u)
                            .frame(width: 84, height: 52).clipShape(RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6).stroke(on ? Color.accentColor : Color.primary.opacity(0.15), lineWidth: on ? 2 : 1))
                            .onTapGesture { ed.edit { $0.look.wallpaper = u.path } }
                            .help(u.deletingPathExtension().lastPathComponent)
                    }
                }
            case .image:
                HStack(spacing: 8) {
                    Button(L("选图片…", "Choose…")) { ed.chooseImage() }
                    Text(look.imageURL?.lastPathComponent ?? L("还没选", "None yet")).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                }
            }
        }
    }

    private var frameBox: some View {
        VStack(alignment: .leading, spacing: 8) {
            slider(L("边距", "Padding"), \.padding, 0...0.2) { "\(Int(($0 * 100).rounded()))%" }
            slider(L("圆角", "Corners"), \.cornerRadius, 0...40) { "\(Int($0.rounded()))" }
            slider(L("阴影", "Shadow"), \.shadow, 0...1) { "\(Int(($0 * 100).rounded()))%" }
            slider(L("阴影虚化", "Softness"), \.shadowBlur, 0...1) { "\(Int(($0 * 100).rounded()))%" }
        }
    }

    private var cursorBox: some View {
        let look = ed.project.look
        return VStack(alignment: .leading, spacing: 8) {
            slider(L("大小", "Size"), \.cursorScale, 0.5...3) { String(format: "%.1f×", $0) }
            slider(L("顺滑", "Smoothing"), \.smoothing, 0...1) { $0 < 0.01 ? L("不平滑", "Off") : "\(Int(($0 * 100).rounded()))%" }
            Toggle(L("不动时自动隐藏", "Hide when idle"), isOn: ed.look(\.hideIdleCursor))
            if look.hideIdleCursor {
                slider(L("隐藏前", "After"), \.hideAfter, 1...5) { String(format: L("%.1f 秒", "%.1fs"), $0) }
            }
            Toggle(L("点击波纹", "Click ripple"), isOn: ed.look(\.ripple))
            Toggle(L("点击音效", "Click sound"), isOn: ed.look(\.clickSound))
                .help(L("导出时在每次点击处加一声轻响（预览里听不到）", "Adds a soft click sound at each click when exporting (not in the preview)"))
        }.toggleStyle(.checkbox).font(.callout)
    }

    private var blurBox: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("", selection: ed.look(\.blur)) {
                Text(L("关", "Off")).tag(StudioLook.Blur.off)
                Text(L("轻", "Light")).tag(StudioLook.Blur.light)
                Text(L("中", "Medium")).tag(StudioLook.Blur.medium)
                Text(L("强", "Strong")).tag(StudioLook.Blur.strong)
            }.pickerStyle(.segmented).labelsHidden()
            Text(L("只在镜头快速推拉、移动时有一点拖影，镜头停着时字是清楚的",
                   "Only adds a little streak while the camera moves fast; text stays sharp when it's still"))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }
}

// 壁纸缩略图：系统有现成的小图就用，没有就从原图快速生成，存起来
struct WallpaperThumb: View {
    let url: URL
    @State private var img: NSImage?
    private static var cache: [URL: NSImage] = [:]
    var body: some View {
        ZStack {
            Color.secondary.opacity(0.15)
            if let img { Image(nsImage: img).resizable().aspectRatio(contentMode: .fill) }
        }
        .task(id: url) {
            if let c = Self.cache[url] { img = c; return }
            let u = url
            let made = await Task.detached { () -> NSImage? in
                let thumb = u.deletingLastPathComponent().appendingPathComponent(".thumbnails").appendingPathComponent(u.lastPathComponent)
                let src = FileManager.default.fileExists(atPath: thumb.path) ? thumb : u
                guard let s = CGImageSourceCreateWithURL(src as CFURL, nil),
                      let cg = CGImageSourceCreateThumbnailAtIndex(s, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 240] as CFDictionary)
                else { return nil }
                return NSImage(cgImage: cg, size: CGSize(width: cg.width, height: cg.height))
            }.value
            if let made { Self.cache[url] = made; img = made }
        }
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
                let st = StudioStyle(), r = StudioRenderer(project: dir, track: track, style: st, look: StudioLook()), lay = r.lay
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

    // BLURTEST=素材夹 [BLURTEST_OUT=目录] → 同一帧分别不开 / 开「强」运动模糊画出来比：镜头不动的帧必须逐像素一样，快速推拉的帧要有拖影
    @MainActor static func blurTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["BLURTEST"] else { return false }
        let outDir = ProcessInfo.processInfo.environment["BLURTEST_OUT"] ?? "/tmp"
        Task { @MainActor in
            var ok = true
            do {
                let dir = URL(fileURLWithPath: path)
                let track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
                let asset = AVURLAsset(url: dir.appendingPathComponent("raw.mp4"))
                let duration = try await asset.load(.duration).seconds
                var look = StudioLook(); look.background = .color
                let proj = StudioProject(segments: StudioPlan.autoSegments(track, duration: duration), trimStart: 0, trimEnd: duration, look: look)
                look.blur = .off; let r0 = StudioRenderer(project: dir, track: track, style: StudioStyle(), look: look)
                look.blur = .strong; let r1 = StudioRenderer(project: dir, track: track, style: StudioStyle(), look: look)
                let plan = r0.plan(proj, duration: duration)
                // 找一帧镜头最快的、一帧镜头完全不动的
                var fast = 1, fastV = 0.0, still = -1
                for i in 1..<(plan.count - 1) {
                    let a = plan[i - 1], b = plan[i + 1], v = hypot(b.sx - a.sx, b.sy - a.sy) + abs(log(b.z / a.z)) * 2000
                    if v > fastV { fastV = v; fast = i }
                    if still < 0, v < 1e-6, plan[i].z > 1.5 { still = i }
                }
                let g = AVAssetImageGenerator(asset: asset); g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
                func bitmap(_ r: StudioRenderer, _ i: Int) async throws -> (Data, CGImage) {
                    let (cg, _) = try await g.image(at: CMTime(value: CMTimeValue(i), timescale: 60))
                    let img = r.frame(i, plan: plan, source: CIImage(cgImage: cg))
                    let out = r.ctx.createCGImage(img, from: r.lay.canvas, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))!
                    return (out.dataProvider!.data! as Data, out)
                }
                func diff(_ a: Data, _ b: Data) -> (max: Int, mean: Double) {
                    var m = 0, sum = 0
                    a.withUnsafeBytes { pa in b.withUnsafeBytes { pb in
                        let x = pa.bindMemory(to: UInt8.self), y = pb.bindMemory(to: UInt8.self)
                        for k in stride(from: 0, to: min(x.count, y.count), by: 1) { let d = abs(Int(x[k]) - Int(y[k])); m = max(m, d); sum += d }
                    } }
                    return (m, Double(sum) / Double(max(1, a.count)))
                }
                print("fast frame", fast, String(format: "t=%.2f", Double(fast) / 60), "subsamples", r1.shutter(fast, plan: plan)?.count ?? 0, "still frame", still)
                if still >= 0 {
                    let (a, _) = try await bitmap(r0, still), (b, _) = try await bitmap(r1, still)
                    let d = diff(a, b); print(d.max == 0 ? "PASS" : "FAIL", "still frame identical", "maxdiff", d.max); ok = ok && d.max == 0
                } else { print("FAIL no still zoomed frame"); ok = false }
                let (a, ca) = try await bitmap(r0, fast), (b, cb) = try await bitmap(r1, fast)
                let d = diff(a, b); print(d.mean > 0.5 ? "PASS" : "FAIL", "fast frame blurred", String(format: "mean diff %.2f max %d", d.mean, d.max)); ok = ok && d.mean > 0.5
                for (n, c) in [("blur-off", ca), ("blur-strong", cb)] {
                    let rep = NSBitmapImageRep(cgImage: c)
                    try rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: outDir).appendingPathComponent("\(n).png"))
                }
            } catch { print("FAIL", error); ok = false }
            print(ok ? "BLURTEST OK" : "BLURTEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }

    // PRESETTEST=素材夹（没有 project.json 的副本）→ 存预设、设默认、新工程用上默认、删掉；跑完恢复原来的 presets.json
    @MainActor static func presetTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["PRESETTEST"] else { return false }
        Task { @MainActor in
            var ok = true
            func check(_ n: String, _ c: Bool) { print(c ? "PASS" : "FAIL", n); ok = ok && c }
            let backup = try? Data(contentsOf: PresetStore.url)
            func restore() { if let backup { try? backup.write(to: PresetStore.url) } else { try? FileManager.default.removeItem(at: PresetStore.url) } }
            var look = StudioLook(); look.aspect = .tall; look.background = .gradient; look.cursorScale = 2.5; look.blur = .medium
            var f = PresetStore.File(); f.presets = [StudioPreset(name: "竖屏测试", look: look)]; f.defaultName = "竖屏测试"
            PresetStore.save(f)
            let back = PresetStore.load()
            check("saved and read back", back.presets.first?.look == look && back.defaultName == "竖屏测试")
            do {
                let dir = URL(fileURLWithPath: path)
                let track = try JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json")))
                try? FileManager.default.removeItem(at: StudioProject.url(dir))
                let p = StudioProject.load(dir, track: track, duration: 15)
                check("new project uses default preset", p.look == look)
                // 旧工程（没有 look 字段）读进来用默认样式、推近段不丢
                try #"{"segments":[{"id":"8C2E0F2A-0000-4000-8000-000000000001","start":1,"end":3,"zoom":2.5,"mode":"follow","fixedX":0.5,"fixedY":0.5}],"trimStart":0,"trimEnd":15}"#
                    .data(using: .utf8)!.write(to: StudioProject.url(dir))
                let old = StudioProject.load(dir, track: track, duration: 15)
                check("old project.json still loads", old.segments.count == 1 && old.segments[0].zoom == 2.5 && old.look == StudioLook())
                try? FileManager.default.removeItem(at: StudioProject.url(dir))
            } catch { check("load \(error)", false) }
            f.presets = []; f.defaultName = nil; PresetStore.save(f)
            check("deleted", PresetStore.load().presets.isEmpty && PresetStore.defaultLook == StudioLook())
            restore()   // exit 不会跑 defer，所以在这里恢复
            print(ok ? "PRESETTEST OK" : "PRESETTEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }

    // EDITORSHOT=图片路径 EDITOR=素材夹 → 打开剪辑窗口（不抢前台）画成图片
    @MainActor static func editorShot() -> Bool {
        guard StudioEditor.testShot != nil, let p = ProcessInfo.processInfo.environment["EDITOR"] else { return false }
        // EDITOR_APPEARANCE=dark|light 强制深色 / 浅色
        switch ProcessInfo.processInfo.environment["EDITOR_APPEARANCE"] {
        case "dark": NSApp.appearance = NSAppearance(named: .darkAqua)
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        default: break
        }
        Task { await StudioEditor.open(URL(fileURLWithPath: p)) }
        return true
    }
}
