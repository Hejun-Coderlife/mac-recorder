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
    private var endObserver: Any?, statusObserver: NSKeyValueObservation?
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
    @Published var timelineScale = 1.0         // 时间轴横向放大（1 = 整段刚好铺满）
    @Published var timelineOffset = 0.0        // 时间轴滚出左边的像素（滚动条同步过来）
    @Published var timelineScrollSeq = 0       // 程序改了滚动位置 → 让 ScrollView 跟着滚
    var timelineViewW = 0.0                    // 时间轴可见宽度
    private var undoStack: [StudioProject] = [], redoStack: [StudioProject] = []
    private var dragBase: StudioProject?
    private var scrubber = Scrubber()          // 拖动时的 seek 排队：同时只有一个在路上，只留最新目标；拖动中不让播放器回调改播放头

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
        // 打开时按新规则重排了推近段：原来的段放进撤销栈，⌘Z 能拿回来（拿回来就算手动改过，以后不再重排）
        else if let d = try? Data(contentsOf: StudioProject.url(dir)), var old = try? JSONDecoder().decode(StudioProject.self, from: d),
                !old.segments.isEmpty, old.segments.sorted(by: { $0.start < $1.start }) != p.segments {
            old.segments.sort { $0.start < $1.start }
            old.trimStart = p.trimStart; old.trimEnd = p.trimEnd; old.look = p.look; old.segmentsEdited = true
            undoStack = [old]
        }
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
                if self.scrubber.observed(t.seconds) { self.time = t.seconds }
                let rate = self.player.rate
                if self.playing && rate == 0 { self.playing = false }
            }
        }
        // 播到结尾（或修剪的终点）时间就不走了，上面的回调不再来：另外听「播完」和播放状态，把图标换回播放
        endObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.playing = false; self.map { $0.time = $0.player.currentTime().seconds } }
        }
        statusObserver = player.observe(\.timeControlStatus) { [weak self] p, _ in
            let paused = p.timeControlStatus == .paused
            Task { @MainActor in if paused, let self, self.playing { self.playing = false; self.time = p.currentTime().seconds } }
        }
    }

    // ---- 窗口 ----
    private func show() {
        // EDITOR_WIDTH=像素：截图自测时指定窗口宽（高按 16:10 左右）
        let ew = ProcessInfo.processInfo.environment["EDITOR_WIDTH"].flatMap(Double.init) ?? 1280
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: ew, height: max(820, ew * 0.56)), styleMask: [.titled, .closable, .miniaturizable, .resizable],
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
                // EDITOR_UNDOCHECK=1：只看「打开时按新规则重排」能不能 ⌘Z 撤回，打印结果后退出
                if ProcessInfo.processInfo.environment["EDITOR_UNDOCHECK"] != nil {
                    let now = project.segments.count, could = canUndo
                    undo()
                    print("UNDOCHECK canUndo=\(could) segs \(now) -> \(project.segments.count) edited=\(project.segmentsEdited)")
                    exit(0)
                }
                selected = project.segments.first?.id
                await renderStill(at: project.segments.first.map { ($0.start + $0.end) / 2 } ?? 1)
                // EDITOR_AT_TRIM=1：把结尾修剪到 80% 处、播放头压在修剪点上，看两者重叠时分不分得清
                if ProcessInfo.processInfo.environment["EDITOR_AT_TRIM"] != nil { project.trimEnd = duration * 0.8; time = project.trimEnd }
                // EDITOR_TLZOOM=倍数：时间轴按可见区 70% 处为锚点放大，看放大后的时间轴和滚动条
                if let z = ProcessInfo.processInfo.environment["EDITOR_TLZOOM"].flatMap(Double.init) {
                    try? await Task.sleep(for: .seconds(0.3))
                    setTimelineScale(z, anchorX: timelineViewW * 0.7)
                    print("TLZOOM scale=\(timelineScale) offset=\(timelineOffset) view=\(timelineViewW)")
                }
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
        if let go = scrubber.request(t) { startSeek(go) }
    }
    // 上一个 seek 做完才发下一个（中间的目标丢掉，只追最新的），不然请求堆积，画面和播放头一会儿跳回旧位置
    private func startSeek(_ t: Double) {
        player.seek(to: CMTime(seconds: t, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                if let next = self.scrubber.seekDone() { self.startSeek(next) }
            }
        }
    }
    // 拖播放头 / 修剪把手 / 推近段期间：播放器的时间回调不许改播放头
    func scrubBegin() { scrubber.begin(); if playing { player.pause(); playing = false } }
    func scrubEnd() { scrubber.end() }
    var isDragging: Bool { dragBase != nil || scrubber.active }

    // 时间轴缩放：锚点（默认可见区正中）下的时间缩放前后留在同一位置
    func setTimelineScale(_ s: Double, anchorX: Double? = nil) {
        let w = max(timelineViewW, 1)
        let r = TimelineMath.zoom(scale: timelineScale, offset: timelineOffset, view: w, anchorX: anchorX ?? w / 2, to: s)
        guard abs(r.scale - timelineScale) > 1e-9 || abs(r.offset - timelineOffset) > 1e-9 else { return }
        timelineScale = r.scale; timelineOffset = r.offset; timelineScrollSeq += 1
    }
    func panTimeline(_ dx: Double) {
        let o = TimelineMath.clampOffset(timelineOffset + dx, scale: timelineScale, view: max(timelineViewW, 1))
        guard abs(o - timelineOffset) > 1e-9 else { return }
        timelineOffset = o; timelineScrollSeq += 1
    }
    func togglePlay() {
        if playing { player.pause(); playing = false; return }
        let now = player.currentTime().seconds   // 播到头时 time 可能没跟上，按播放器实际位置判断
        if now >= project.trimEnd - 0.05 || now < project.trimStart - 0.01 { seek(project.trimStart) }
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
        if p.segments != project.segments { p.segmentsEdited = true }
        undoStack.append(project); redoStack.removeAll()
        project = p
    }
    // 拖动：开始时记下原样，拖的过程中实时改，松手才算一步撤销
    func dragBegin() { if dragBase == nil { dragBase = project; scrubBegin() } }
    func dragUpdate(_ change: (inout StudioProject) -> Void) {
        guard let base = dragBase else { return }
        var p = base
        change(&p)
        if p.segments != base.segments { p.segmentsEdited = true }
        project = p
    }
    func dragEnd() {
        if let base = dragBase, base != project { undoStack.append(base); redoStack.removeAll() }
        dragBase = nil
        scrubEnd()
    }
    // 按自动规则重新排推近段（能撤销）；排完算「没手动改过」
    func regenerate() {
        edit { $0.segments = StudioPlan.autoSegments(track, duration: duration, zoom: $0.segments.first?.zoom ?? 2.0) }
        project.segmentsEdited = false
        selected = nil
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
        guard let pr = presets.find(name) else { return }
        edit { $0.look = pr.look }
    }
    func savePreset(_ name: String) {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, !n.hasPrefix("@") else { return }
        if let i = presets.presets.firstIndex(where: { $0.name == n }) { presets.presets[i].look = project.look }
        else { presets.presets.append(StudioPreset(name: n, look: project.look)) }
        PresetStore.save(presets)
    }
    func deletePreset(_ name: String) {
        guard !name.hasPrefix("@") else { return }   // 内置的删不掉
        presets.presets.removeAll { $0.name == name }
        if presets.defaultName == name { presets.defaultName = nil }
        PresetStore.save(presets)
    }
    func setDefaultPreset(_ name: String?) { presets.defaultName = name; PresetStore.save(presets) }
    // 当前样式对得上哪个预设：自己存的优先
    var currentPresetName: String? { (presets.presets + StudioPreset.builtIns).first { $0.look == project.look }?.name }

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
                    controlBar.padding(.horizontal, 14).padding(.vertical, 8)
                    Divider()
                    VStack(alignment: .leading, spacing: 10) {
                        if ed.selected != nil { inspector.frame(height: 30) }   // 选中推近段才出现
                        timeline.frame(height: 120)
                            .help(L("点选推近段来调整，空白处双击新加，两端黄条修剪", "Click a zoom to edit it, double-click empty space to add one, drag the yellow bars to trim"))
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

    // 顶部只留文件名和「导出」
    private var toolbar: some View {
        HStack(spacing: 12) {
            Text(ed.dir.lastPathComponent).font(.callout.weight(.medium)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
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

    // 预览和时间轴之间的一条（像 Screen Studio）：左边编辑工具，正中播放和时间，右边时间轴缩放
    private var controlBar: some View {
        ZStack {
            HStack(spacing: 10) {
                Button { ed.addSegment(at: ed.time) } label: { Label(L("推近", "Zoom"), systemImage: "plus.magnifyingglass") }
                    .help(L("在播放头处新加一段推近（镜头放大凑近看）；也可以在时间轴空白处双击", "Add a zoom-in at the playhead; you can also double-click empty space on the timeline"))
                Button { ed.regenerate() } label: { Label(L("重新自动生成", "Auto-Generate"), systemImage: "wand.and.stars") }
                    .help(L("按录制时的点击和打字重新排推近段（会替换现在的段，可以撤销 ⌘Z）", "Rebuild zooms from your clicks and typing (replaces the current zooms; undo with ⌘Z)"))
                Button { ed.undo() } label: { Image(systemName: "arrow.uturn.backward") }.disabled(!ed.canUndo).help(L("撤销上一步（⌘Z）", "Undo the last change (⌘Z)"))
                Button { ed.redo() } label: { Image(systemName: "arrow.uturn.forward") }.disabled(!ed.canRedo).help(L("重做刚撤销的那步（⌘⇧Z）", "Redo what you just undid (⌘⇧Z)"))
                Spacer()
                Group {
                    Button { ed.setTimelineScale(ed.timelineScale / 1.5) } label: { Image(systemName: "minus.magnifyingglass") }
                        .disabled(ed.timelineScale <= 1)
                    Slider(value: Binding(get: { ed.timelineScale }, set: { ed.setTimelineScale($0) }), in: TimelineMath.scaleRange).frame(width: 90)
                    Button { ed.setTimelineScale(ed.timelineScale * 1.5) } label: { Image(systemName: "plus.magnifyingglass") }
                        .disabled(ed.timelineScale >= 8)
                }
                .help(L("时间轴缩放（只改时间轴的显示，不影响成片）：也可以在时间轴上双指捏合 / 张开，或按住 ⌘ 滚动滚轮；放大后双指左右滑或滚轮翻看",
                        "Timeline zoom (display only, doesn't change the video): pinch on the timeline or hold ⌘ and scroll; when zoomed in, swipe sideways or scroll to move along"))
            }
            HStack(spacing: 12) {
                Color.clear.frame(width: 130, height: 1)   // 和右边的时间等宽，让播放键正好在中间
                Button { ed.togglePlay() } label: {
                    Image(systemName: ed.playing ? "pause.fill" : "play.fill").font(.system(size: 15, weight: .semibold)).frame(width: 34, height: 34)
                        .foregroundStyle(.white).background(Circle().fill(Color.accentColor))
                }
                .buttonStyle(.plain).help(L("播放 / 暂停（空格）", "Play / Pause (Space)"))
                Text(clock(ed.time - ed.project.trimStart) + " / " + clock(ed.project.trimEnd - ed.project.trimStart))   // 按修剪后的成片算
                    .font(.callout.monospacedDigit()).foregroundStyle(.secondary).frame(width: 130, alignment: .leading)
            }
        }
    }

    // 时间轴放大后能左右滚；播放时跟着播放头翻页。捏合 / ⌘+滚轮缩放、滚轮平移由 TimelineGestures 接住
    @State private var tlPos = ScrollPosition(x: 0)
    private var timeline: some View {
        GeometryReader { g in
            let w = g.size.width
            ScrollView(.horizontal, showsIndicators: ed.timelineScale > 1.01) {
                TimelineStrip(ed: ed).frame(width: w * ed.timelineScale, height: g.size.height)
            }
            .scrollPosition($tlPos)
            .onScrollGeometryChange(for: Double.self) { $0.contentOffset.x } action: { _, x in
                if abs(x - ed.timelineOffset) > 0.5 { ed.timelineOffset = TimelineMath.clampOffset(x, scale: ed.timelineScale, view: w) }
            }
            .background(TimelineGestures(ed: ed))
            .onAppear { ed.timelineViewW = w }
            .onChange(of: w) { _, nw in ed.timelineViewW = nw; ed.setTimelineScale(ed.timelineScale) }
            .onChange(of: ed.timelineScrollSeq) { _, _ in
                // 内容宽度这一帧才变，等布局完再滚
                let o = ed.timelineOffset
                DispatchQueue.main.async { tlPos.scrollTo(x: o) }
            }
            .onChange(of: ed.time) { _, t in
                // 播放时播放头跑出可见区就翻一页
                guard ed.playing, ed.timelineScale > 1.01 else { return }
                let x = t / max(ed.duration, 0.01) * w * ed.timelineScale
                if x < ed.timelineOffset || x > ed.timelineOffset + w - 20 { ed.panTimeline(x - w * 0.1 - ed.timelineOffset) }
            }
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
// 时间轴的触控板 / 滚轮：双指捏合或 ⌘+竖向滚轮 = 以鼠标处为锚点缩放；放大后竖向滚轮 = 左右平移（横向滑动交给 ScrollView 自己）。
// 用本地事件监听，不挡住时间轴上的点击和拖动；正在拖段落 / 播放头时不缩放
struct TimelineGestures: NSViewRepresentable {
    let ed: StudioEditor
    final class Catcher: NSView {
        weak var ed: StudioEditor?
        private var monitor: Any?
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
            guard window != nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: [.magnify, .scrollWheel]) { [weak self] e in
                guard let self, let ed = self.ed, e.window === self.window else { return e }
                let p = self.convert(e.locationInWindow, from: nil)
                guard self.bounds.contains(p) else { return e }
                return MainActor.assumeIsolated { self.handle(e, x: p.x, ed: ed) ? nil : e }
            }
        }
        @MainActor private func handle(_ e: NSEvent, x: Double, ed: StudioEditor) -> Bool {
            if ed.isDragging { return e.type == .magnify }
            if e.type == .magnify {
                ed.setTimelineScale(ed.timelineScale * (1 + e.magnification), anchorX: x); return true
            }
            let dx = e.scrollingDeltaX, dy = e.scrollingDeltaY
            if e.modifierFlags.contains(.command) {
                guard abs(dy) > abs(dx) else { return false }
                ed.setTimelineScale(ed.timelineScale * TimelineMath.wheelFactor(dy, precise: e.hasPreciseScrollingDeltas), anchorX: x); return true
            }
            // 只有竖向（鼠标滚轮）且已放大：换成左右平移；横向滑动让 ScrollView 原样处理
            guard ed.timelineScale > 1.01, abs(dy) > abs(dx) * 2 else { return false }
            ed.panTimeline(-dy * (e.hasPreciseScrollingDeltas ? 1 : 12)); return true
        }
        deinit { if let m = monitor { NSEvent.removeMonitor(m) } }
    }
    func makeNSView(context: Context) -> Catcher { let v = Catcher(); v.ed = ed; return v }
    func updateNSView(_ v: Catcher, context: Context) { v.ed = ed }
}

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
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
                        .onChanged { ed.scrubBegin(); ed.seek(tAt($0.location.x)) }
                        .onEnded { _ in ed.scrubEnd() })
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
                trimHandle(isStart: true, w: w, d: d, h: g.size.height).offset(x: x(ed.project.trimStart) - (Self.handleW - Self.bar))
                trimHandle(isStart: false, w: w, d: d, h: g.size.height).offset(x: x(ed.project.trimEnd) - Self.bar)
                // 播放头：细线 + 顶上小把手，压在最上面；深色模式白、浅色模式深灰，带一圈淡阴影，和黄色括号重叠也分得清
                Playhead(height: g.size.height).offset(x: x(ed.time) - Playhead.knobW / 2).allowsHitTesting(false)
            }
            .coordinateSpace(name: Self.space)
        }
    }
    static let space = "timeline"
    static let handleW = 16.0, bar = 4.0   // 修剪把手：可点的宽度、黄色竖杠的粗细

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

    // 修剪把手画成括号「[ 」「 ]」：竖杠贴着修剪点，上下两个小横杠朝成片那一侧
    private func trimHandle(isStart: Bool, w: Double, d: Double, h: Double) -> some View {
        TrimBracket(isStart: isStart, height: h).frame(width: Self.handleW, height: h)
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { }   // 单击把手什么都不做（别漏到底下的时间轴，把播放头叫过来）
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(Self.space))
                .onChanged { v in
                    ed.dragBegin()
                    ed.dragUpdate { p in
                        // p 是按下那一刻的工程：起点值 + 拖了多远，不会越拖越歪
                        let t = TimelineMath.trim(start: isStart, base: (p.trimStart, p.trimEnd), dx: v.translation.width, width: w, duration: d)
                        if isStart { p.trimStart = t } else { p.trimEnd = t }
                    }
                    ed.seek(isStart ? ed.project.trimStart : ed.project.trimEnd)
                }
                .onEnded { _ in ed.dragEnd() })
            .help(isStart ? L("拖动剪掉开头", "Drag to cut the start") : L("拖动剪掉结尾", "Drag to cut the end"))
    }
}

struct TrimBracket: View {
    let isStart: Bool, height: Double
    var body: some View {
        let bw = TimelineStrip.bar, w = TimelineStrip.handleW, arm = 9.0, armH = 3.0
        // 竖杠贴着修剪点（开头的在右边缘、结尾的在左边缘），上下横杠朝成片那一侧伸出去：「[」「]」
        let barX = isStart ? w - bw : 0
        let armX = isStart ? w - bw : bw - arm
        ZStack(alignment: .topLeading) {
            Color.clear
            RoundedRectangle(cornerRadius: 1.5).fill(Color.yellow).frame(width: bw, height: height).offset(x: barX)
            RoundedRectangle(cornerRadius: 1.5).fill(Color.yellow).frame(width: arm, height: armH).offset(x: isStart ? armX : armX, y: 0)
            RoundedRectangle(cornerRadius: 1.5).fill(Color.yellow).frame(width: arm, height: armH).offset(x: armX, y: height - armH)
        }
        .frame(width: w, height: height, alignment: .topLeading)
    }
}

struct Playhead: View {
    let height: Double
    static let knobW = 11.0
    @Environment(\.colorScheme) private var scheme
    var body: some View {
        let c = scheme == .dark ? Color.white : Color(white: 0.2)
        VStack(spacing: 0) {
            UnevenRoundedRectangle(topLeadingRadius: 3, bottomLeadingRadius: 5.5, bottomTrailingRadius: 5.5, topTrailingRadius: 3)
                .fill(c).frame(width: Self.knobW, height: 13)
            Rectangle().fill(c).frame(width: 1.5, height: max(0, height - 13))
        }
        .shadow(color: .black.opacity(scheme == .dark ? 0.7 : 0.35), radius: 1.2)
        .frame(width: Self.knobW, height: height, alignment: .top)
    }
}

// 拖动期间的 seek 排队（纯逻辑，好单独测）：同时只有一个 seek 在路上，新的目标只记最新一个；
// 拖动中或 seek 还没做完时，播放器的时间回调（还是旧位置）一律不采用，播放头就不会被拉回去抖
struct Scrubber {
    private(set) var active = false, inFlight = false
    private(set) var pending: Double?
    mutating func begin() { active = true }
    mutating func end() { active = false }
    // 返回现在就该发出的 seek；nil = 有一个在路上，已记下等它做完
    mutating func request(_ t: Double) -> Double? {
        if inFlight { pending = t; return nil }
        inFlight = true; return t
    }
    // 一个 seek 做完：有更新的目标就接着发它
    mutating func seekDone() -> Double? {
        if let p = pending { pending = nil; return p }
        inFlight = false; return nil
    }
    // 播放器报来的时间能不能拿来更新播放头
    func observed(_ t: Double) -> Bool { !active && !inFlight }
}

// 时间轴坐标换算（纯函数）
enum TimelineMath {
    static func time(atX px: Double, width w: Double, duration d: Double) -> Double { min(max(px / w * d, 0), d) }
    // 拖修剪把手：起点值 + 拖动距离换成的时间，夹在合法范围里
    static func trim(start isStart: Bool, base: (s: Double, e: Double), dx: Double, width w: Double, duration d: Double) -> Double {
        let dt = dx / w * d
        return isStart ? min(max(0, base.s + dt), base.e - 0.5) : max(min(d, base.e + dt), base.s + 0.5)
    }
    // 时间轴视窗：放大 scale（1–8）、offset = 内容滚出左边的像素、view = 可见宽度
    static let scaleRange = 1.0...8.0
    static func clampOffset(_ o: Double, scale s: Double, view w: Double) -> Double { min(max(o, 0), max(0, w * s - w)) }
    // 以可见区里 anchorX 处为锚点缩放：锚点下的时间在缩放前后屏幕位置不变（贴边时被夹住除外）
    static func zoom(scale s: Double, offset o: Double, view w: Double, anchorX ax: Double, to newScale: Double) -> (scale: Double, offset: Double) {
        let ns = min(max(newScale, scaleRange.lowerBound), scaleRange.upperBound)
        let f = (o + ax) / (w * s)
        return (ns, clampOffset(f * w * ns - ax, scale: ns, view: w))
    }
    // 滚轮 / 捏合换成缩放倍率
    static func wheelFactor(_ dy: Double, precise: Bool) -> Double { pow(precise ? 1.006 : 1.12, dy) }
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
        .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
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
            .onTapGesture { ed.selected = seg.id; ed.picking = false }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
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
        // 自己画标题行（不用 DisclosureGroup：里面放了下拉框时系统会把标题挤偏，几组对不齐）
        let isOpen = open.contains(id)
        return VStack(alignment: .leading, spacing: 0) {
            Button {
                withAnimation(.easeInOut(duration: 0.15)) { if isOpen { open.remove(id) } else { open.insert(id) } }
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "chevron.right").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
                        .rotationEffect(.degrees(isOpen ? 90 : 0)).frame(width: 10)
                    Text(title).font(.callout.weight(.semibold))
                    Spacer(minLength: 0)
                }.padding(.vertical, 4).contentShape(Rectangle())
            }.buttonStyle(.plain)
            if isOpen {
                VStack(alignment: .leading, spacing: 10) { content() }.frame(maxWidth: .infinity, alignment: .leading).padding(.top, 8).padding(.bottom, 10)
            }
        }
    }

    // 小按钮组（像分段控件，但能换行，不会被裁）
    private func chips<T: Hashable>(_ sel: Binding<T>, _ items: [(T, String)], columns: Int) -> some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: columns), spacing: 4) {
            ForEach(items.indices, id: \.self) { i in
                let v = items[i].0, on = sel.wrappedValue == v
                Button { sel.wrappedValue = v } label: {
                    Text(items[i].1).font(.callout).lineLimit(1).minimumScaleFactor(0.8)
                        .frame(maxWidth: .infinity).padding(.vertical, 4)
                        .background(RoundedRectangle(cornerRadius: 6).fill(on ? Color.accentColor : Color.primary.opacity(0.08)))
                        .foregroundStyle(on ? Color.white : Color.primary)
                        .contentShape(Rectangle())
                }.buttonStyle(.plain)
            }
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
        let cur = ed.currentPresetName, def = ed.presets.defaultKey
        func label(_ p: StudioPreset) -> String { p.title + (def == p.name ? L("（新录制默认）", " (default)") : "") }
        return VStack(alignment: .leading, spacing: 8) {
            Picker("", selection: Binding(get: { cur ?? "" }, set: { if !$0.isEmpty && $0 != "#hint" { ed.applyPreset($0) } })) {
                if cur == nil { Text(L("自定义（未保存）", "Custom (unsaved)")).tag("") }
                Section(L("内置", "Built-in")) {
                    ForEach(StudioPreset.builtIns) { p in Text(label(p)).tag(p.name) }
                }
                Section(L("我的预设", "My Presets")) {
                    if ed.presets.presets.isEmpty {
                        Text(L("调好样式后点「存成预设…」就能存下来", "Adjust the style, then click “Save as Preset…”")).foregroundStyle(.secondary).tag("#hint")
                    }
                    ForEach(ed.presets.presets) { p in Text(label(p)).tag(p.name) }
                }
            }.labelsHidden().frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 8) {
                Button(L("存成预设…", "Save as Preset…")) { newName = cur.flatMap { $0.hasPrefix("@") ? nil : $0 } ?? ""; naming = true }
                if let n = cur, !n.hasPrefix("@") {
                    Button(role: .destructive) { ed.deletePreset(n) } label: { Image(systemName: "trash") }.help(L("删除这个预设", "Delete this preset"))
                }
            }.controlSize(.small)
            // 新录制用哪套：显眼地写出来，当前选中的不是默认就给个按钮
            HStack(spacing: 6) {
                Image(systemName: "star.fill").foregroundStyle(.yellow).font(.caption)
                Text(L("新录制默认：", "New recordings use: ") + (ed.presets.find(def)?.title ?? "")).font(.caption)
                Spacer(minLength: 0)
                if let n = cur, n != def {
                    Button(L("设为默认", "Make Default")) { ed.setDefaultPreset(n == "@transparent" ? nil : n) }
                        .controlSize(.small)
                        .help(L("以后新录的 Studio 素材都用这套样式", "Use this style for new Studio recordings"))
                }
            }
        }
    }

    private var aspectBox: some View {
        chips(ed.look(\.aspect), [(.original, L("原始", "Original")), (.wide, "16:9"), (.tall, "9:16"), (.square, "1:1"), (.classic, "4:3")], columns: 5)
    }

    private var backgroundBox: some View {
        let look = ed.project.look
        return VStack(alignment: .leading, spacing: 10) {
            // 五个选项一排放不下（英文会被裁），排成两行的小按钮
            chips(ed.look(\.background), [
                (.transparent, L("透明", "None")), (.color, L("纯色", "Color")), (.gradient, L("渐变", "Gradient")),
                (.wallpaper, L("壁纸", "Wallpaper")), (.image, L("图片", "Image")),
            ], columns: 3)
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
                    Text(look.imageURL?.lastPathComponent ?? L("选一张图片", "Pick an image")).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                }
                if look.imageURL == nil {
                    Text(L("没选图之前按透明背景预览和导出", "Until you pick one, the background stays transparent"))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
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
            if look.clickSound {
                HStack(spacing: 6) {
                    Picker("", selection: Binding(get: { look.clickStyle }, set: { v in ed.look(\.clickStyle).wrappedValue = v; ClickPreview.play(v) })) {
                        ForEach(StudioLook.Click.allCases, id: \.self) { Text(ClickPreview.name($0)).tag($0) }
                    }.labelsHidden().frame(maxWidth: .infinity, alignment: .leading)
                    Button { ClickPreview.play(look.clickStyle) } label: { Image(systemName: "speaker.wave.2.fill") }
                        .help(L("试听", "Preview sound"))
                }.controlSize(.small)
            }
        }.toggleStyle(.checkbox).font(.callout)
    }

    private var blurBox: some View {
        VStack(alignment: .leading, spacing: 8) {
            chips(ed.look(\.blur), [(.off, L("关", "Off")), (.light, L("轻", "Light")), (.medium, L("中", "Medium")), (.strong, L("强", "Strong"))], columns: 4)
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
    // SCRUBTEST=1 → 拖动逻辑（不碰界面）：修剪把手按「起点值 + 拖动距离」算、拖动中播放头不被播放器的旧时间拉回、seek 不堆积只追最新
    @MainActor static func scrubTest() -> Bool {
        guard ProcessInfo.processInfo.environment["SCRUBTEST"] != nil else { return false }
        var ok = true
        func check(_ n: String, _ c: Bool, _ info: String = "") { print(c ? "PASS" : "FAIL", n, info); ok = ok && c }
        let w = 1200.0, d = 25.0
        // 1) 往左拖结尾把手：一串越来越大的拖动距离 → 修剪点单调往前，不回跳；拖过头被夹住
        let drags = stride(from: 0.0, through: -900, by: -7.5).map { $0 }
        let ends = drags.map { TimelineMath.trim(start: false, base: (0, 25), dx: $0, width: w, duration: d) }
        check("trimEnd monotonic", zip(ends, ends.dropFirst()).allSatisfy { $1 <= $0 }, "\(ends.first!) → \(ends.last!)")
        check("trimEnd clamped", ends.last! >= 0.5 && abs(ends.last! - (25 - 900 / w * d)) < 1e-9)
        let starts = stride(from: 0.0, through: 2000, by: 13).map { TimelineMath.trim(start: true, base: (0, 25), dx: $0, width: w, duration: d) }
        check("trimStart monotonic + clamp", zip(starts, starts.dropFirst()).allSatisfy { $1 >= $0 } && abs(starts.last! - 24.5) < 1e-9)
        // 来回拖：同一个拖动距离永远得到同一个值（跟拖动过程无关）
        let a = TimelineMath.trim(start: false, base: (0, 25), dx: -300, width: w, duration: d)
        let b = TimelineMath.trim(start: false, base: (0, 25), dx: -300, width: w, duration: d)
        check("same dx same value", a == b && abs(a - 18.75) < 1e-9)
        // 2) seek 排队 + 播放头：模拟拖动时界面要的位置 + 播放器慢吞吞地报旧时间
        var sc = Scrubber(); var shown = 16.0; var player = 16.0; var sent: [Double] = []; var jumpsBack = 0
        sc.begin()
        var inFlight: Double?
        for (k, dx) in drags.prefix(40).enumerated() {
            let t = TimelineMath.trim(start: false, base: (0, 16), dx: dx, width: w, duration: d)
            let prev = shown; shown = t
            if let go = sc.request(t) { sent.append(go); inFlight = go }
            if shown > prev + 1e-9 { jumpsBack += 1 }
            // 播放器每隔几步才做完一个 seek，期间不停报旧位置
            if sc.observed(player) { shown = player }
            if k % 4 == 3, let f = inFlight { player = f; inFlight = nil; if let next = sc.seekDone() { sent.append(next); inFlight = next } }
        }
        check("playhead never pulled back while dragging", jumpsBack == 0)
        check("stale player time ignored while dragging", !sc.observed(10))
        check("seeks coalesced", sent.count < 40 / 2, "\(sent.count) seeks for 40 drag steps")
        // 收尾：最后一个目标一定会发出去
        let last = TimelineMath.trim(start: false, base: (0, 16), dx: drags[39], width: w, duration: d)
        while let f = inFlight { player = f; inFlight = nil; if let n = sc.seekDone() { sent.append(n); inFlight = n } }
        check("final seek lands on last target", abs(player - last) < 1e-9, "\(player) vs \(last)")
        sc.end()
        check("player time accepted after drag", sc.observed(player))
        // 3) 时间轴缩放：锚点下的时间缩放前后屏幕位置不变、边界夹住、滑块（以正中为锚点）一致
        let vw = 1000.0
        for (s0, o0, ax, ns) in [(1.0, 0.0, 300.0, 3.0), (3.0, 800.0, 120.0, 5.5), (5.5, 1500.0, 900.0, 2.0), (2.0, 400.0, 500.0, 2.0 * 1.006)] {
            let r = TimelineMath.zoom(scale: s0, offset: o0, view: vw, anchorX: ax, to: ns)
            let before = (o0 + ax) / (vw * s0) * d, after = (r.offset + ax) / (vw * r.scale) * d
            let clamped = r.offset == 0 || abs(r.offset - (vw * r.scale - vw)) < 1e-9
            check("anchor stays put s=\(s0)→\(ns) ax=\(ax)", abs(before - after) < 1e-9 || clamped, String(format: "%.4f → %.4f", before, after))
        }
        let big = TimelineMath.zoom(scale: 2, offset: 0, view: vw, anchorX: 500, to: 50), small = TimelineMath.zoom(scale: 4, offset: 2500, view: vw, anchorX: 500, to: 0.2)
        check("scale clamped to 1–8", big.scale == 8 && small.scale == 1 && small.offset == 0)
        check("offset clamped", TimelineMath.clampOffset(-50, scale: 3, view: vw) == 0 && TimelineMath.clampOffset(5000, scale: 3, view: vw) == 2000)
        let edgeR = TimelineMath.zoom(scale: 3, offset: 2000, view: vw, anchorX: 1000, to: 4)   // 右边缘锚点：末尾仍贴右边
        check("right-edge anchor keeps end visible", abs(edgeR.offset - (vw * 4 - vw)) < 1e-9)
        let viaSlider = TimelineMath.zoom(scale: 2, offset: 600, view: vw, anchorX: vw / 2, to: 3)
        let mid = (600 + vw / 2) / (vw * 2) * d, mid2 = (viaSlider.offset + vw / 2) / (vw * 3) * d
        check("slider keeps center time", abs(mid - mid2) < 1e-9)
        // 捏合一小步一小步放大到 3 倍，再缩回 1 倍：回到原位
        var st = (scale: 1.0, offset: 0.0)
        for _ in 0..<200 { st = TimelineMath.zoom(scale: st.scale, offset: st.offset, view: vw, anchorX: 700, to: st.scale * 1.0055) }
        for _ in 0..<400 { st = TimelineMath.zoom(scale: st.scale, offset: st.offset, view: vw, anchorX: 700, to: st.scale / 1.0055) }
        check("pinch in then out returns to start", st.scale == 1 && st.offset == 0)
        check("wheel factor direction", TimelineMath.wheelFactor(10, precise: true) > 1 && TimelineMath.wheelFactor(-1, precise: false) < 1)
        print(ok ? "SCRUBTEST OK" : "SCRUBTEST FAIL")
        exit(ok ? 0 : 1)
    }

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

                // ---- 自动分段规则（造几份假的轨迹）----
                func fake(_ dur: Double, clicks: [(Double, Double, Double)], keys: [Double], at: (Double, Double)) -> Track {
                    var t = Track(width: 2000, height: 1200, pixelsPerPoint: 2)
                    // 鼠标：打字时停在 at，点击时在点击处
                    var pos = at
                    var ci = 0
                    for i in 0...Int(dur * 30) {
                        let tt = Double(i) / 30
                        while ci < clicks.count && clicks[ci].0 <= tt { pos = (clicks[ci].1, clicks[ci].2); ci += 1 }
                        t.moves.append([tt, pos.0, pos.1, 0, 0])
                    }
                    t.clicks = clicks.map { [$0.0, $0.1, $0.2] }; t.keys = keys
                    return t
                }
                func segList(_ segs: [ZoomSegment]) -> String { segs.map { String(format: "%.1f–%.1f", $0.start, $0.end) }.joined(separator: " ") }
                // 1) 同一处一直打字 6 秒 → 一段
                let typeKeys: [Double] = Array(stride(from: 1.2, to: 7.2, by: 0.15))
                let typing = fake(10, clicks: [(1, 1000, 600)], keys: typeKeys, at: (1000, 600))
                let s1 = StudioPlan.autoSegments(typing, duration: 10)
                check("same-place typing → one segment", s1.count == 1, segList(s1))
                // 2) 屏幕左右两头来回点，每 3 秒一下 → 多段，中间回到全景
                var pingClicks: [(Double, Double, Double)] = []
                for i in 0..<6 { let x: Double = i % 2 == 0 ? 150 : 1850; pingClicks.append((1.0 + Double(i) * 3, x, 600)) }
                let ping = fake(20, clicks: pingClicks, keys: [], at: (150, 600))
                let s2 = StudioPlan.autoSegments(ping, duration: 20)
                check("ping-pong clicks → several segments", s2.count >= 4, segList(s2))
                let pingLay = StudioLayout(track: ping, look: StudioLook())
                let pingPlan = StudioPlan.make(ping, pingLay, st, look: StudioLook(), segments: s2, frames: Int(20 * fps), duration: 20)
                var minBetween = 9.0
                for k in 0..<max(0, s2.count - 1) {
                    let a = Int(s2[k].end * fps), b = min(Int(s2[k + 1].start * fps), pingPlan.count - 1)
                    if b > a { minBetween = min(minBetween, pingPlan[a...b].map(\.z).min() ?? 9) }
                }
                check("ping-pong zooms out between", minBetween < 1.06, String(format: "min z between = %.3f", minBetween))
                // 3) 同一处连续 20 秒不停地点、打字（间隔 2 秒有空档）→ 被切开，每段不超过约 12 秒
                // 每 4 秒里前 2 秒在打字、后 2 秒停（制造 2 秒空档）
                var busyKeys: [Double] = []
                for k in stride(from: 0.5, to: 20.5, by: 0.3) { let ph: Double = k.truncatingRemainder(dividingBy: 4); if ph < 2 { busyKeys.append(k) } }
                let busy = fake(22, clicks: [(0.5, 1000, 600)], keys: busyKeys, at: (1000, 600))
                let s3 = StudioPlan.autoSegments(busy, duration: 22)
                check("20 s continuous → split", s3.count >= 2 && s3.allSatisfy { $0.end - $0.start <= 12.5 + StudioPlan.holdAfter }, segList(s3))
                // 4) 真实素材：打印自动分段
                let real = StudioPlan.autoSegments(track, duration: duration)
                print("REAL", String(format: "%.1fs", duration), "→", real.count, "segs:", segList(real))
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
                    // 推近中、镜头几乎不动（前后两帧合起来挪不到 0.05 像素）：快门模糊不该碰它
                    if still < 0, v < 0.05, plan[i].z > 1.5 { still = i }
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
                // 没有 segmentsEdited 标记 = 当作没手动改过：推近段按最新自动规则重排
                check("old project.json still loads (segments re-generated)", old.look == StudioLook() && old.segments == StudioPlan.autoSegments(track, duration: 15).map { var x = $0; x.id = old.segments.first(where: { $0.start == x.start })?.id ?? x.id; return x })
                // 手动改过的工程：推近段原样保留
                try #"{"segments":[{"id":"8C2E0F2A-0000-4000-8000-000000000001","start":1,"end":3,"zoom":2.5,"mode":"follow","fixedX":0.5,"fixedY":0.5}],"trimStart":0,"trimEnd":15,"segmentsEdited":true}"#
                    .data(using: .utf8)!.write(to: StudioProject.url(dir))
                let edited = StudioProject.load(dir, track: track, duration: 15)
                check("edited project keeps its segments", edited.segments.count == 1 && edited.segments[0].zoom == 2.5 && edited.segmentsEdited)
                try? FileManager.default.removeItem(at: StudioProject.url(dir))
            } catch { check("load \(error)", false) }
            f.presets = []; f.defaultName = nil; PresetStore.save(f)
            check("deleted", PresetStore.load().presets.isEmpty && PresetStore.defaultLook == StudioLook())
            // 内置预设：4 个、抖音竖屏是 9:16 + 1.8 倍鼠标；能设为默认；不会写进文件
            let b = StudioPreset.builtIns
            check("built-ins", b.count == 4 && b[0].look == StudioLook() && b[1].look.aspect == .tall && b[1].look.cursorScale == 1.8
                  && b[2].look.aspect == .wide && b[3].look.aspect == .square && b.allSatisfy { $0.builtIn && $0.title != $0.name })
            f.presets = b + [StudioPreset(name: "我的", look: look)]; f.defaultName = "@douyin"; PresetStore.save(f)
            let saved = (try? String(contentsOf: PresetStore.url, encoding: .utf8)) ?? ""
            check("built-ins not written to presets.json", !saved.contains("@transparent") && !saved.contains("@square") && PresetStore.load().presets.map(\.name) == ["我的"])
            check("built-in as default", PresetStore.defaultLook == b[1].look && PresetStore.load().defaultKey == "@douyin")
            check("all = built-ins + mine", PresetStore.load().all.map(\.name) == ["@transparent", "@douyin", "@tutorial", "@square", "我的"])
            f.presets = []; f.defaultName = "已删掉的"; PresetStore.save(f)
            check("missing default falls back to transparent", PresetStore.load().defaultKey == "@transparent" && PresetStore.defaultLook == StudioLook())
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

// 点击音效试听：选了就响一声
@MainActor enum ClickPreview {
    private static var player: AVAudioPlayer?
    static func name(_ c: StudioLook.Click) -> String {
        switch c {
        case .soft: L("轻点", "Soft tap")
        case .mouse: L("鼠标咔哒", "Mouse click")
        case .thock: L("低沉", "Deep thock")
        case .wood: L("木头", "Wood")
        case .pop: L("气泡", "Pop")
        case .tick: L("清脆滴答", "Crisp tick")
        }
    }
    static func play(_ c: StudioLook.Click) {
        player = try? AVAudioPlayer(data: ClickSound.wav(c))
        player?.play()
    }
}
