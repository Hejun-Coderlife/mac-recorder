// Studio 剪辑窗口（像 Screen Studio）：上面实时预览（和成片同一套合成），下面时间轴上的推近段可以拖长缩短、挪动、删掉、
// 新加，选中后调放大倍数、改「跟着鼠标 / 固定位置」；两头可以修剪；改动自动存进素材夹的 project.json，「导出」出成片
import AppKit
import AVFoundation
import CoreImage
import MediaToolbox
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
    // 字幕（素材时间查：预览按素材时间播）
    private var cap: CaptionLayer?
    var captions: CaptionLayer? { lock.lock(); defer { lock.unlock() }; return cap }
    func setCaptions(_ c: CaptionLayer?) { lock.lock(); cap = c; lock.unlock() }
    // 摄像头画中画（素材时间查）
    private var cam: CameraLayer?
    var camera: CameraLayer? { lock.lock(); defer { lock.unlock() }; return cam }
    func setCamera(_ c: CameraLayer?) { lock.lock(); cam = c; lock.unlock() }
    // 遮挡、标注（素材时间查）
    private var ov: OverlayLayer?
    var overlay: OverlayLayer? { lock.lock(); defer { lock.unlock() }; return ov }
    func setOverlay(_ o: OverlayLayer?) { lock.lock(); ov = o; lock.unlock() }
    // 正在播放：只有播的时候加运动模糊（暂停编辑时画面要清楚）
    private var isPlaying = false
    var playing: Bool { get { lock.lock(); defer { lock.unlock() }; return isPlaying } set { lock.lock(); isPlaying = newValue; lock.unlock() } }
}

@MainActor
final class StudioEditor: NSObject, ObservableObject, NSWindowDelegate {
    static var open: [URL: StudioEditor] = [:]
    // 自测：EDITORSHOT=图片路径 [EDITOR=素材夹] → 不抢前台，把剪辑窗口画成图片后退出
    static let testShot = ProcessInfo.processInfo.environment["EDITORSHOT"]

    let dir: URL
    let track: Track
    let duration: Double
    let style = StudioStyle()
    let fps: Double
    let player: AVPlayer
    let asset: AVURLAsset
    private var previewAsset: AVAsset          // 预览播的：raw.mp4，开了降噪是「画面 + 降噪后的声音」
    private let box: PlanBox
    var renderer: StudioRenderer { box.renderer }
    // 成片路径：透明背景 .mov，其他 .mp4
    var out: URL { Studio.output(Recorder.folder.appendingPathComponent(dir.lastPathComponent + ".mov"), look: project.look) }
    private(set) var window: NSWindow?
    private var timeObserver: Any?
    private var endObserver: Any?, statusObserver: NSKeyValueObservation?
    private var keyMonitor: Any?
    private var boundaryObserver: Any?         // 播到剪掉 / 快放的段、点击时刻时叫一下
    private var gains: [GainBox] = []          // 预览每条音轨的音量（拖滑块实时生效）
    let audioChannels: [Int]                   // 每条音轨几个声道：2 = 电脑声音，1 = 麦克风
    private var saveWork: DispatchWorkItem?
    private var refreshPending = false

    @Published var project: StudioProject { didSet { if project != oldValue { replan(only: Self.overlayOnly(oldValue, project)); scheduleSave() } } }
    // 只改了遮挡 / 标注（拖它们的时候）：镜头计划、声音都不用重算，只换叠加层、重画这一帧——拖起来不卡
    static func overlayOnly(_ a: StudioProject, _ b: StudioProject) -> Bool {
        var x = a, y = b
        x.annotations = []; y.annotations = []; x.redactions = []; y.redactions = []
        return x == y
    }
    @Published var selectedPiece: Double?                // 剪辑轨上选中的那段（起点）
    @Published var selected: UUID? { didSet { if selected != nil { selectedPiece = nil }; if let n = editingNote, selected != n { endNote() } } }   // 选了别的：正在改的字先收好
    @Published var time = 0.0
    @Published var playing = false { didSet { box.playing = playing; if !playing { musicPlayer.pauseAll(); if oldValue { redrawPausedFrame() } } } }
    @Published var picking = false             // 正在等用户在预览上点固定位置
    @Published var exportProgress: Double?
    @Published var exported: URL?
    @Published var exportError: String?
    @Published var exportCopied = false
    @Published var exportOptions = ExportOptions.saved { didSet { ExportOptions.saved = exportOptions } }
    private var exportTask: Task<Void, Never>?
    @Published var still: NSImage?             // 截图自测时用静态画面代替播放器
    @Published var presets = PresetStore.load()
    @Published var timelineScale = 1.0         // 时间轴横向放大（1 = 整段刚好铺满）
    @Published var timelineOffset = 0.0        // 时间轴滚出左边的像素（滚动条同步过来）
    @Published var timelineScrollSeq = 0       // 程序改了滚动位置 → 让 ScrollView 跟着滚
    var timelineViewW = 0.0                    // 时间轴可见宽度
    // 录讲解
    struct VoiceRec { var start: Double; var file: String; var offset = 0.0; var elapsed = 0.0; var level: Float = 0 }
    @Published var voiceRec: VoiceRec?         // 正在录：从素材哪一刻开始、录了多久、音量
    @Published var countdown: Int?             // 录之前倒数 3-2-1
    @Published var voicePeaks: [String: [Float]] = [:]   // 每个录音文件的波形（每秒 100 个点）
    private var recorder: AVAudioRecorder?
    private var countdownTask: Task<Void, Never>?
    private let voicePlayer = VoicePreview()
    let musicPlayer = MusicPreview()          // 背景音乐预览
    var musicEnv: [Float]?                    // 原声音的音量包络（压低音乐用；打开时算一次）
    var musicDuckKey = ""                     // 压低曲线缓存：工程里相关的东西变了才重算
    var musicDuckCache: [Float] = []
    @Published var suggestions: [SpeedSuggestion] = []   // 「建议加速」找到的段（还没采纳）
    @Published var suggesting = false
    var isRecordingVoice: Bool { voiceRec != nil }
    @Published var speaking: Double?                     // 字幕转配音进行中（0–1）
    @Published var ttsVoice = UserDefaults.standard.string(forKey: "ttsVoice") ?? "" { didSet { UserDefaults.standard.set(ttsVoice, forKey: "ttsVoice") } }
    @Published var ttsRate = UserDefaults.standard.object(forKey: "ttsRate") as? Double ?? 1.0 { didSet { UserDefaults.standard.set(ttsRate, forKey: "ttsRate") } }
    let previewSynth = AVSpeechSynthesizer()
    @Published var revealSection: String?                // 让样式面板展开并滚到某一节
    @Published var suggestNone = false                   // 点了「建议加速」但没找到：提示条显示几秒
    private var duckNow: Float = 1             // 预览里原声音现在压到多少（有讲解时 0.25）
    static var countdownOn: Bool { UserDefaults.standard.object(forKey: "voiceCountdown") as? Bool ?? true }
    static var muteWhileRecording: Bool { UserDefaults.standard.object(forKey: "voiceMuteOriginal") as? Bool ?? true }
    // 字幕
    struct CaptionJob: Equatable { var message: String; var progress: Double? }
    @Published var captionJob: CaptionJob?     // 正在识别：说明 + 进度（nil = 还不知道多少）
    @Published var captionError: String?
    @Published var editingCaption: UUID?       // 要把光标放进字幕输入框的那句（双击 / 新加）
    @Published var drawing: OverlayTool?       // 正在等用户在预览上拖出遮挡 / 标注
    @Published var editingNote: UUID?          // 正在预览上直接改字的文字标注
    private var captionTask: Task<Void, Never>?
    // 降噪：改了强度先在后台处理声音（有缓存很快），好了换预览
    @Published var denoiseJob: Double?
    @Published var denoiseError: String?
    private var denoiseTask: Task<Void, Never>?
    private var previewKey = "off"             // 预览现在用的是哪一版声音
    private var undoStack: [StudioProject] = [], redoStack: [StudioProject] = []
    private(set) var dragBase: StudioProject?
    private var scrubber = Scrubber()          // 拖动时的 seek 排队：同时只有一个在路上，只留最新目标；拖动中不让播放器回调改播放头

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }
    var plan: [FramePlan] { box.plan }
    var events: [Double] { (track.clicks.map { $0[0] } + track.keys).filter { $0 >= 0 && $0 <= duration } }
    var timeMap: TimeMap { TimeMap(project) }
    var hasSystemAudio: Bool { audioChannels.contains { $0 != 1 } }
    var hasMicAudio: Bool { audioChannels.contains(1) }

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
        previewAsset = asset
        duration = try await asset.load(.duration).seconds
        fps = Double(StudioStyle().fps)
        let p = StudioProject.load(dir, track: track, duration: duration)
        project = p
        box = PlanBox(StudioRenderer(project: dir, track: track, style: StudioStyle(), look: p.look))
        player = AVPlayer()
        var chans: [Int] = []
        let aTracks = try await asset.loadTracks(withMediaType: .audio)
        for t in aTracks {
            let fd = try await t.load(.formatDescriptions).first
            chans.append(Int(fd.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2))
        }
        audioChannels = chans
        super.init()
        gains = aTracks.map { _ in GainBox() }
        if !FileManager.default.fileExists(atPath: StudioProject.url(dir).path) { try? project.save(dir) }
        // 打开时按新规则重排了推近段：原来的段放进撤销栈，⌘Z 能拿回来（拿回来就算手动改过，以后不再重排）
        else if let d = try? Data(contentsOf: StudioProject.url(dir)), var old = try? JSONDecoder().decode(StudioProject.self, from: d),
                !old.segments.isEmpty, old.segments.sorted(by: { $0.start < $1.start }) != p.segments {
            old.segments.sort { $0.start < $1.start }
            old.trimStart = p.trimStart; old.trimEnd = p.trimEnd; old.look = p.look; old.segmentsEdited = true
            undoStack = [old]
        }
        box.set(box.renderer.plan(project, duration: duration), renderer: box.renderer)
        box.setCaptions(captionLayer(box.renderer))
        box.setCamera(cameraLayer(box.renderer))
        box.setOverlay(OverlayLayer(project))
        let item = AVPlayerItem(asset: asset)
        item.videoComposition = makeComposition()
        item.audioMix = PreviewAudio.mix(tracks: aTracks, gains: gains)
        item.audioTimePitchAlgorithm = .timeDomain   // 快放时声音不变尖
        updateGains()
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
                self.syncPlayback()
                self.voiceTick()
            }
        }
        resetBoundaries()
        for c in project.voiceovers { loadPeaks(c.file) }
        refreshDenoise()   // 工程里开着降噪：打开时就换成处理后的声音
        loadMusicEnv()
        for c in project.music.clips { loadPeaks(c.file) }
        // 播到结尾（或修剪的终点）时间就不走了，上面的回调不再来：另外听「播完」和播放状态，把图标换回播放
        observeEnd(item)
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
                // EDITOR_DEMOCUTS=1：放一段「剪掉」和一段「4× 加速」并选中加速那段，看剪辑轨和属性条
                if ProcessInfo.processInfo.environment["EDITOR_DEMOCUTS"] != nil {
                    let d = duration
                    project.cuts = [CutSegment(start: d * 0.18, end: d * 0.27), CutSegment(start: d * 0.52, end: d * 0.66, mode: .speed, rate: 4)]
                    project.look.clickSound = true
                    selected = project.cuts[1].id
                }
                // EDITOR_VOICEDEMO=1：合成一段测试录音，放两段讲解（第二段 70% 音量）并选中第一段，看讲解轨和属性条
                if ProcessInfo.processInfo.environment["EDITOR_VOICEDEMO"] != nil {
                    let f = dir.appendingPathComponent("voice/demo.m4a")
                    try? FileManager.default.createDirectory(at: f.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try? StudioTests.writeTestVoice(f, length: 6, bursts: stride(from: 0.2, to: 5.8, by: 0.23).map { $0 })
                    let d = duration
                    var b = VoiceClip(file: "voice/demo.m4a", start: d * 0.1, trimIn: 0, trimOut: 5, fileLength: 6)
                    project.voiceovers = [b]
                    b = VoiceClip(file: "voice/demo.m4a", start: d * 0.62, trimIn: 0.5, trimOut: 4.5, fileLength: 6, volume: 0.7)
                    project.voiceovers.append(b)
                    selected = project.voiceovers[0].id
                    loadPeaks("voice/demo.m4a")
                }
                // EDITOR_MUSICDEMO=music|suggest：合成一段音乐放到音乐轨（选中它）；suggest 再放两个建议加速块（不选中，露出总条）
                if let k = ProcessInfo.processInfo.environment["EDITOR_MUSICDEMO"] {
                    let f = dir.appendingPathComponent("music/demo.wav")
                    try? FileManager.default.createDirectory(at: f.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try? StudioTests.writeChord(f, length: 8, amp: 0.5)
                    let d = duration
                    project.music.clips = [MusicClip(file: "music/demo.wav", name: "Demo Song", start: d * 0.05, trimIn: 0, trimOut: 8, fileLength: 8)]
                    project.music.volume = 0.6
                    loadPeaks("music/demo.wav")
                    if k == "suggest" {
                        selected = nil
                        suggestions = [SpeedSuggestion(start: d * 0.3, end: d * 0.45, kind: .idle), SpeedSuggestion(start: d * 0.62, end: d * 0.74, kind: .typing)]
                    } else { selected = project.music.clips[0].id }
                }
                // EDITOR_OVERLAYDEMO=blur|mosaic|arrow|text：在截图那一刻放一块遮挡、一个框、一个箭头、一段文字，选中指定的那个
                if let which = ProcessInfo.processInfo.environment["EDITOR_OVERLAYDEMO"] {
                    let t = project.segments.first.map { ($0.start + $0.end) / 2 } ?? 1, d = duration
                    var r = Redaction(start: max(0, t - 2), end: min(d, t + 2), x: 0.08, y: 0.22, w: 0.30, h: 0.18, style: which == "mosaic" ? .mosaic : .blur)
                    r.strength = 0.7
                    project.redactions = [r, Redaction(start: d * 0.7, end: d * 0.85, x: 0.5, y: 0.5, w: 0.2, h: 0.1, style: .solid)]
                    project.annotations = [
                        Annotation(start: max(0, t - 1.5), end: min(d, t + 2), kind: .rect, x: 0.55, y: 0.18, w: 0.30, h: 0.22),
                        Annotation(start: max(0, t - 1), end: min(d, t + 2), kind: .arrow, x: 0.40, y: 0.70, w: 0.14, h: -0.18, color: RGB(0.04, 0.52, 1)),
                        Annotation(start: max(0, t - 1), end: min(d, t + 1.5), kind: .text, x: 0.58, y: 0.62, w: 0, h: 0, text: L("点这里新建任务", "Click here"), fontSize: 24),
                        Annotation(start: d * 0.6, end: d * 0.7, kind: .highlight, x: 0.2, y: 0.2, w: 0.3, h: 0.08, color: RGB(1.0, 0.84, 0.04)),
                    ]
                    selected = which == "arrow" ? project.annotations[1].id : which == "text" ? project.annotations[2].id : project.redactions[0].id
                }
                // EDITOR_VOICEREC=1：假装正在录讲解（不开麦克风），看录音中的按钮、提示条和实时长出的那块
                if ProcessInfo.processInfo.environment["EDITOR_VOICEREC"] != nil {
                    voiceRec = VoiceRec(start: duration * 0.35, file: "voice/x.m4a", offset: 0.05, elapsed: 3.4, level: 0.62)
                    selected = nil
                }
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
        if ProcessInfo.processInfo.environment["EDITOR_LIVE"] != nil { NSApp.setActivationPolicy(.accessory); w.orderBack(nil); return }
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        if voiceRec != nil { stopVoice() }
        cancelCaptions()
        countdownTask?.cancel(); countdown = nil
        voicePlayer.stopAll()
        musicPlayer.stopAll()
        player.pause()
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        if let boundaryObserver { player.removeTimeObserver(boundaryObserver) }
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        saveNow()
        Self.open[dir.standardizedFileURL] = nil
    }

    // 空格 播放/暂停，←/→ 逐帧，Delete 删选中的段，⌘Z / ⌘⇧Z 撤销重做
    private func key(_ e: NSEvent) -> Bool {
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        // 正在输入框里打字（预览上改文字标注等）：按键都交给输入框
        if window?.firstResponder is NSTextView { return false }
        // 录讲解 / 倒数中：空格或 Esc 停下
        if (voiceRec != nil || countdown != nil) && (e.keyCode == 49 || e.keyCode == 53) { toggleVoice(); return true }
        switch e.keyCode {
        case 49 where !cmd: togglePlay(); return true
        case 123 where !cmd: step(-1); return true
        case 124 where !cmd: step(1); return true
        case 51, 117: deleteSelected(); return true
        case 6 where cmd: shift ? redo() : undo(); return true
        case 2 where cmd: duplicateOverlay(); return true
        case 1 where !cmd, 11 where cmd: split(at: time); return true   // S / ⌘B 分割
        case 53: if picking { picking = false; return true }; if drawing != nil { drawing = nil; return true }; return false
        default: return false
        }
    }

    private func observeEnd(_ item: AVPlayerItem) {
        if let o = endObserver { NotificationCenter.default.removeObserver(o) }
        endObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.playing = false; self.map { $0.time = $0.player.currentTime().seconds } }
        }
    }

    // ---- 降噪：强度 / 「电脑声音也降噪」变了，或新录了讲解，后台算好声音再换预览 ----
    private func refreshDenoise() {
        let a = project.audio
        let want = a.denoise == .off ? "off" : "\(a.denoise.rawValue)|\(a.denoiseSystem)"
        let voiceMissing = a.denoise != .off && project.voiceovers.contains {
            !FileManager.default.fileExists(atPath: Denoise.mixedURL(Denoise.voiceKey($0.file), a.denoise, dir: dir).path)
        }
        guard want != previewKey || voiceMissing else { return }
        denoiseTask?.cancel(); denoiseError = nil
        denoiseJob = a.denoise == .off ? nil : 0
        let p = project
        denoiseTask = Task { await self.runDenoise(p, want: want) }
    }
    private func runDenoise(_ p: StudioProject, want: String) async {
        do {
            if p.audio.denoise != .off {
                let progress = DenoiseProgress(self)
                try await Denoise.prepare(dir: dir, project: p, progress: progress.report)
            }
            if Task.isCancelled { return }
            if want != previewKey { previewKey = want; await rebuildItem(p) }
            denoiseJob = nil
        } catch is CancellationError {
        } catch {
            denoiseJob = nil
            denoiseError = L("降噪失败：", "Denoise failed: ") + error.localizedDescription
        }
    }
    func setDenoiseProgress(_ v: Double) { if denoiseJob != nil { denoiseJob = v } }
    // 换一个预览用的播放内容（画面合成、音量处理都跟着换）
    private func rebuildItem(_ p: StudioProject) async {
        guard let src = try? await Denoise.asset(dir: dir, project: p, raw: asset) else { return }
        previewAsset = src
        let aTracks = (try? await src.loadTracks(withMediaType: .audio)) ?? []
        gains = aTracks.map { _ in GainBox() }
        let item = AVPlayerItem(asset: src)
        item.videoComposition = makeComposition()
        item.audioMix = PreviewAudio.mix(tracks: aTracks, gains: gains)
        item.audioTimePitchAlgorithm = .timeDomain
        item.forwardPlaybackEndTime = CMTime(seconds: project.trimEnd, preferredTimescale: 600)
        updateGains()
        let t = player.currentTime()
        if playing { player.pause(); playing = false }
        player.replaceCurrentItem(with: item)
        observeEnd(item)
        await player.seek(to: t, toleranceBefore: .zero, toleranceAfter: .zero)
        resetBoundaries()
    }

    // ---- 预览 ----
    private func makeComposition() -> AVVideoComposition {
        let box = self.box, fps = self.fps
        let lay = box.renderer.lay, k = min(1, 1600 / max(lay.cw, lay.ch))          // 预览缩小一点，播放流畅
        let size = CGSize(width: (lay.cw * k / 2).rounded() * 2, height: (lay.ch * k / 2).rounded() * 2)
        let bg = CIImage(color: CIColor(red: 0.86, green: 0.86, blue: 0.88)).cropped(to: CGRect(origin: .zero, size: size))   // 透明的地方衬一层浅灰
        let c = AVMutableVideoComposition(asset: previewAsset) { req in
            let (r, plan) = box.both
            guard !plan.isEmpty else { req.finish(with: req.sourceImage, context: nil); return }
            let i = min(max(Int((req.compositionTime.seconds * fps).rounded()), 0), plan.count - 1)
            let kk = min(1, 1600 / max(r.lay.cw, r.lay.ch))   // 样式刚改、合成还没换时按新画布缩放
            var img = r.frame(i, plan: plan, source: req.sourceImage, overlay: box.overlay, srcTime: req.compositionTime.seconds, motionBlur: box.playing)
            if let cam = box.camera { img = cam.compose(img, srcTime: req.compositionTime.seconds) }
            if let c = box.captions?.image(atSource: req.compositionTime.seconds) { img = c.composited(over: img) }
            img = img.transformed(by: CGAffineTransform(scaleX: kk, y: kk))
            req.finish(with: img.composited(over: bg).cropped(to: bg.extent), context: nil)
        }
        c.renderSize = size
        c.frameDuration = CMTime(value: 1, timescale: 30)
        return c
    }

    private func replan(only overlay: Bool = false) {
        if overlay {
            box.setOverlay(OverlayLayer(project))
            if !playing { redrawPaused() }
            return
        }
        player.currentItem?.forwardPlaybackEndTime = CMTime(seconds: project.trimEnd, preferredTimescale: 600)
        // 拖的时候别每次都重建：每秒最多刷新 30 次。样式变了要换渲染器（画布、背景都可能变）
        guard !refreshPending else { return }
        refreshPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0 / 30) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.refreshPending = false
                let p = self.project
                var r = self.box.renderer
                let newLook = r.look != p.look
                if newLook { r = StudioRenderer(project: self.dir, track: self.track, style: self.style, look: p.look) }
                self.box.set(r.plan(p, duration: self.duration), renderer: r)
                // 字幕层、摄像头层：用到的东西没变就不重建（拖推近段时它们都不变）
                let capKey = CapKey(captions: p.captions, style: p.captionStyle, look: p.look, trim: [p.trimStart, p.trimEnd], cuts: p.cuts)
                if newLook || capKey != self.capKey { self.capKey = capKey; self.box.setCaptions(self.captionLayer(r)) }
                let camKey = CamKey(camera: p.camera, layouts: p.layouts, look: p.look)
                if newLook || camKey != self.camKey { self.camKey = camKey; self.box.setCamera(self.cameraLayer(r)) }
                self.box.setOverlay(OverlayLayer(p))
                if self.dragBase != nil && !self.playing {
                    // 拖动中：只重画预览（用缓存的那一帧直接合成，不让播放器重新解码）；声音、边界松手后再弄
                    self.needsAudioRefresh = true
                    Task { await self.renderDragFrame() }
                    return
                }
                self.player.currentItem?.videoComposition = self.makeComposition()
                self.refreshDenoise()
                self.updateGains()
                self.resetBoundaries()
                self.needsAudioRefresh = false
                if !self.playing { self.seek(self.time) }
                if self.still != nil { Task { await self.renderStill(at: self.time) } }
                if self.dragFrame != nil { DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { MainActor.assumeIsolated { if self.dragBase == nil { self.dragFrame = nil } } } }
            }
        }
    }
    struct CapKey: Equatable { let captions: [Caption]; let style: CaptionStyle; let look: StudioLook; let trim: [Double]; let cuts: [CutSegment] }
    struct CamKey: Equatable { let camera: CameraSettings?; let layouts: [CameraLayoutSeg]; let look: StudioLook }

    // 画一帧成片（给 AI 看）：和截图自测同一套合成，缩到 maxWidth 宽，返回 PNG
    func stillPNG(at t: Double, maxWidth: Double) async -> Data? {
        let g = AVAssetImageGenerator(asset: asset)
        g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
        guard let (cg, _) = try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)) else { return nil }
        let (r, plan) = box.both, i = min(max(Int((t * fps).rounded()), 0), plan.count - 1)
        let lay = r.lay
        var fr = r.frame(i, plan: plan, source: CIImage(cgImage: cg), overlay: box.overlay, srcTime: t)
        if let cam = box.camera { fr = cam.compose(fr, srcTime: t) }
        if let c = box.captions?.image(atSource: t) { fr = c.composited(over: fr) }
        let k = min(1, maxWidth / lay.cw)
        let img = fr.composited(over: CIImage(color: CIColor(red: 0.86, green: 0.86, blue: 0.88)).cropped(to: lay.canvas))
            .transformed(by: CGAffineTransform(scaleX: k, y: k))
        guard let out = CIContext().createCGImage(img, from: img.extent.integral) else { return nil }
        return NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:])
    }

    // 暂停时让播放器重画当前这一帧（叠加层换了）：每秒最多 60 次，跟手
    private var redrawPending = false
    private func redrawPaused() {
        guard !redrawPending else { return }
        redrawPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0 / 60) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.redrawPending = false
                if self.still != nil { Task { await self.renderStill(at: self.time) } }
                else if self.dragBase != nil { Task { await self.renderDragFrame() } }   // 拖动中：用缓存的这一帧直接合成，不让播放器重新解码
                else {
                    self.player.currentItem?.videoComposition = self.makeComposition(); self.seek(self.time)
                    // 播放器画好新的这一帧再把盖着的图拿掉，免得闪一下旧画面
                    if self.dragFrame != nil { DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { MainActor.assumeIsolated { if self.dragBase == nil { self.dragFrame = nil } } } }
                }
            }
        }
    }

    // 拖动遮挡 / 标注时盖在播放器上的画面（缩到预览大小，快）；松手后换回播放器
    @Published var dragFrame: NSImage?
    private var capKey: CapKey?, camKey: CamKey?, needsAudioRefresh = false
    // 预览放大（像剪映）：1 = 适应窗口；pan = 放大后挪了多少（预览上的点，右 / 下为正）
    @Published var previewZoom = 1.0
    @Published var previewPan = CGSize.zero
    var previewSize = CGSize(width: 1, height: 1)
    func setPreviewZoom(_ z: Double, anchor: CGPoint? = nil) {
        let nz = min(max(z, 1), 6), oz = previewZoom
        // 以鼠标处为中心放大：那一点在放大前后不动
        if let a = anchor {
            let c = CGPoint(x: previewSize.width / 2, y: previewSize.height / 2)
            let px = a.x - c.x - previewPan.width, py = a.y - c.y - previewPan.height
            previewPan.width -= px * (nz / oz - 1); previewPan.height -= py * (nz / oz - 1)
        }
        previewZoom = nz
        clampPan()
    }
    func panPreview(_ d: CGSize) { previewPan.width += d.width; previewPan.height += d.height; clampPan() }
    private func clampPan() {
        let mx = previewSize.width * (previewZoom - 1) / 2, my = previewSize.height * (previewZoom - 1) / 2
        previewPan = CGSize(width: min(max(previewPan.width, -mx), mx), height: min(max(previewPan.height, -my), my))
    }
    private var cachedSource: (t: Double, img: CIImage)?
    private func renderDragFrame() async {
        let t = time
        if cachedSource?.t != t {
            let g = AVAssetImageGenerator(asset: asset)
            g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
            guard let (cg, _) = try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)) else { return }
            cachedSource = (t, CIImage(cgImage: cg))
        }
        guard let src = cachedSource?.img, dragBase != nil else { return }
        let (r, plan) = box.both, i = min(max(Int((t * fps).rounded()), 0), plan.count - 1)
        let lay = r.lay
        var fr = r.frame(i, plan: plan, source: src, overlay: box.overlay, srcTime: t, motionBlur: false)
        if let cam = box.camera { fr = cam.compose(fr, srcTime: t) }
        if let c = box.captions?.image(atSource: t) { fr = c.composited(over: fr) }
        let k = min(1, 1600 / lay.cw)
        let img = fr.cropped(to: lay.canvas).transformed(by: CGAffineTransform(scaleX: k, y: k))
        guard let out = dragCtx.createCGImage(img, from: img.extent.integral) else { return }
        dragFrame = NSImage(cgImage: out, size: CGSize(width: lay.cw, height: lay.ch))
    }
    private let dragCtx = CIContext(options: [.cacheIntermediates: false])
    func redrawPausedFrame() { player.currentItem?.videoComposition = makeComposition(); seek(time) }   // 停下时这一帧不带运动模糊重画
    func dragFinished() {
        if needsAudioRefresh { replan() }               // 拖完把声音、边界、播放器都更新一遍
        else if dragFrame != nil { redrawPaused() }
    }

    // 截图自测：同一个合成函数画一张静态画面
    private func renderStill(at t: Double) async {
        let g = AVAssetImageGenerator(asset: asset)
        g.requestedTimeToleranceBefore = .zero; g.requestedTimeToleranceAfter = .zero
        guard let (cg, _) = try? await g.image(at: CMTime(seconds: t, preferredTimescale: 600)) else { return }
        let (r, plan) = box.both, i = min(max(Int((t * fps).rounded()), 0), plan.count - 1)
        let lay = r.lay
        var fr = r.frame(i, plan: plan, source: CIImage(cgImage: cg), overlay: box.overlay, srcTime: t)
        if let cam = box.camera { fr = cam.compose(fr, srcTime: t) }
        if let c = box.captions?.image(atSource: t) { fr = c.composited(over: fr) }
        let img = fr.composited(over: CIImage(color: CIColor(red: 0.86, green: 0.86, blue: 0.88)).cropped(to: lay.canvas))
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
        if voiceRec != nil { stopVoice(); return }
        if playing { player.pause(); playing = false; voiceTick(); return }
        let now = player.currentTime().seconds   // 播到头时 time 可能没跟上，按播放器实际位置判断
        if now >= project.trimEnd - 0.05 || now < project.trimStart - 0.01 { seek(project.trimStart) }
        player.play(); playing = true
        syncPlayback()
    }

    // 播放中：落在剪掉的段里就跳到段尾，落在快放的段里就按倍数放，其他地方正常速度
    func syncPlayback() {
        guard playing, player.rate != 0 else { return }
        let t = player.currentTime().seconds
        if let pc = timeMap.piece(at: t + 0.001) {
            if pc.rate == 0 {
                player.seek(to: CMTime(seconds: pc.end, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
            } else if abs(Double(player.rate) - pc.rate) > 0.01 { player.rate = Float(pc.rate) }
        } else if abs(player.rate - 1) > 0.01 { player.rate = 1 }
    }
    // 剪辑段的起止和点击时刻挂到播放器上：播到那一刻马上处理（比每秒 30 次的回调准）
    private func resetBoundaries() {
        if let boundaryObserver { player.removeTimeObserver(boundaryObserver); self.boundaryObserver = nil }
        let map = timeMap
        var times = map.pieces.flatMap { [$0.start, $0.end] }
        let clickAt = project.look.clickSound
            ? track.clicks.map { $0[0] }.filter { $0 >= project.trimStart && $0 < project.trimEnd && !map.inCut($0) } : []
        let keyAt = project.look.typeSound
            ? track.keys.filter { $0 >= project.trimStart && $0 < project.trimEnd && !map.inCut($0) } : []
        times += clickAt + keyAt
        guard !times.isEmpty else { return }
        let clicks = Set(clickAt.map { Int(($0 * 1000).rounded()) }), keys = keyAt
        boundaryObserver = player.addBoundaryTimeObserver(forTimes: times.map { NSValue(time: CMTime(seconds: $0, preferredTimescale: 6000)) }, queue: .main) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.playing else { return }
                let t = self.player.currentTime().seconds
                // 点击声：跟着预览一起响（导出时也是同一种、同样音量）
                if clicks.contains(where: { abs(Double($0) / 1000 - t) < 0.06 }) {
                    ClickPreview.play(self.project.look.clickStyle, volume: self.project.audio.clicks)
                }
                if keys.contains(where: { abs($0 - t) < 0.03 }) { ClickPreview.playKey(self.project.look.typeStyle, volume: self.project.audio.clicks) }
                self.syncPlayback()
            }
        }
    }
    // 预览的音量：每条音轨按声道数认电脑声音 / 麦克风，静音就是 0
    private func updateGains() {
        let a = project.audio
        let k: Float = voiceRec != nil && Self.muteWhileRecording ? 0 : duckNow   // 录讲解时原声音静音；有讲解的地方压低
        for (i, g) in gains.enumerated() {
            let v = a.muted ? 0 : (audioChannels.indices.contains(i) && audioChannels[i] == 1 ? a.mic : a.system)
            g.gain = Float(v) * k
        }
    }

    // ---- 录讲解 ----
    // 每次播放器报时间（每秒 30 次）：讲解跟着画面放、原声音按讲解压低；录音中更新时长和音量、播到头就停
    func voiceTick() {
        let map = timeMap, o = map.out(player.currentTime().seconds)
        let mute = voiceRec != nil && Self.muteWhileRecording
        musicTick(outTime: o)
        let active = voicePlayer.sync(dir: dir, clips: project.voiceovers, map: map, outTime: o, level: project.audio.denoise,
                                      playing: playing && Self.testShot == nil, master: mute ? 0 : project.audio.voice)
        var d: Float = 1
        if project.audio.duck, !project.voiceovers.isEmpty {
            d = VoicePreview.duckGain(outTime: o, clips: project.voiceovers, map: map)
        }
        if !active && !playing { d = 1 }
        if abs(d - duckNow) > 0.001 { duckNow = d; updateGains() }
        if var r = voiceRec, let rec = recorder {
            rec.updateMeters()
            r.elapsed = max(0, rec.currentTime - r.offset)
            r.level = max(0, min(1, (rec.averagePower(forChannel: 0) + 50) / 50))
            voiceRec = r
            if !playing || player.currentTime().seconds >= project.trimEnd - 0.02 { stopVoice() }
        }
    }

    func toggleVoice() {
        if countdown != nil { countdownTask?.cancel(); countdownTask = nil; countdown = nil; return }
        if voiceRec != nil { stopVoice(); return }
        Task { await startVoice() }
    }

    private func startVoice() async {
        // 第一次用：要麦克风权限
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: break
        case .notDetermined:
            guard await AVCaptureDevice.requestAccess(for: .audio) else { return }
        default:
            let a = NSAlert()
            a.messageText = L("没有麦克风权限", "No microphone access")
            a.informativeText = L("到「系统设置 → 隐私与安全性 → 麦克风」里打开「录屏」，再来录讲解。", "Turn on Screen Recorder under System Settings → Privacy & Security → Microphone, then try again.")
            a.runModal(); return
        }
        if playing { player.pause(); playing = false }
        if Self.countdownOn {
            countdownTask = Task { @MainActor in
                for n in [3, 2, 1] {
                    countdown = n
                    try? await Task.sleep(for: .seconds(1))
                    if Task.isCancelled { return }
                }
                countdown = nil
                await beginVoice()
            }
        } else { await beginVoice() }
    }

    private func beginVoice() async {
        // 从播放头开始（落在剪掉的段里就从段尾开始；在结尾附近就从头开始）
        var s0 = time
        if s0 < project.trimStart || s0 >= project.trimEnd - 0.3 { s0 = project.trimStart }
        if let pc = timeMap.piece(at: s0), pc.rate == 0 { s0 = pc.end }
        let vdir = dir.appendingPathComponent("voice", isDirectory: true)
        try? FileManager.default.createDirectory(at: vdir, withIntermediateDirectories: true)
        let name = "voice/" + UUID().uuidString.prefix(8).lowercased() + ".m4a"
        guard let rec = try? AVAudioRecorder(url: dir.appendingPathComponent(name), settings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 128_000,
        ]) else { NSSound.beep(); return }
        rec.isMeteringEnabled = true
        await player.seek(to: CMTime(seconds: s0, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        time = s0
        guard rec.record() else { NSSound.beep(); return }
        recorder = rec
        // 录音先开、画面紧接着开始播：开播那一刻录音已经录了多久 = 讲解从录音的第几秒开始用，两边就对齐了
        player.play(); playing = true
        voiceRec = VoiceRec(start: s0, file: name, offset: rec.currentTime)
        updateGains()
        syncPlayback()
    }

    func stopVoice() {
        guard let r = voiceRec, let rec = recorder else { voiceRec = nil; return }
        let len = max(0, rec.currentTime - r.offset), total = rec.currentTime
        rec.stop(); recorder = nil
        player.pause(); playing = false
        voiceRec = nil
        updateGains()
        guard len >= 0.3 else { try? FileManager.default.removeItem(at: dir.appendingPathComponent(r.file)); return }
        addVoice(VoiceClip(file: r.file, start: r.start, trimIn: r.offset, trimOut: r.offset + len, fileLength: total))
    }
    // 一段讲解放进工程（新录的盖住重叠的旧讲解；能撤销）
    func addVoice(_ c: VoiceClip) {
        edit { $0.voiceovers = VoiceClip.place(c, into: $0.voiceovers, map: TimeMap($0)) }
        selected = c.id
        loadPeaks(c.file)
    }
    func voiceIndex(_ id: UUID?) -> Int? { project.voiceovers.firstIndex { $0.id == id } }
    // 波形：每秒 100 个点（取绝对值最大）
    func loadPeaks(_ file: String) {
        guard voicePeaks[file] == nil else { return }
        voicePeaks[file] = []
        let url = dir.appendingPathComponent(file)
        Task.detached {
            let pcm = (try? await VoiceMix.decode(url)) ?? []
            var pk: [Float] = []
            stride(from: 0, to: pcm.count, by: 480).forEach { i in pk.append(pcm[i..<min(pcm.count, i + 480)].reduce(0) { max($0, abs($1)) }) }
            let done = pk
            await MainActor.run { self.voicePeaks[file] = done }
        }
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
        dragFinished()
    }
    // 按自动规则重新排推近段（能撤销）；排完算「没手动改过」
    func regenerate() {
        let old = project.segments.map { ($0.start, $0.end) }
        let segs = StudioPlan.autoSegments(track, duration: duration, zoom: project.segments.first?.zoom ?? 2.0)
        edit { $0.segments = segs }
        project.segmentsEdited = false
        selected = nil
        // 告诉用户发生了什么（之前点了常常看不出变化：自动排的和现在的一样）
        let same = old.count == segs.count && zip(old, segs).allSatisfy { abs($0.0 - $1.start) < 0.05 && abs($0.1 - $1.end) < 0.05 }
        showToast(same ? L("已按点击和打字重新排推近：和现在的一样，没有变化", "Zooms regenerated from your clicks and typing: same as before")
                       : L("已按点击和打字重新排了 \(segs.count) 段推近（原来 \(old.count) 段）· ⌘Z 撤销", "Regenerated \(segs.count) zooms (was \(old.count)) · ⌘Z to undo"))
    }
    @Published var toast: String?
    private var toastTask: Task<Void, Never>?
    func showToast(_ s: String) {
        toast = s; toastTask?.cancel()
        toastTask = Task { try? await Task.sleep(for: .seconds(3.5)); if !Task.isCancelled { toast = nil } }
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
        if let p = currentPiece { deletePiece(p); return }
        if let i = cutIndex(selected), project.cuts[i].mode == .cut { restoreCut(project.cuts[i].id); return }   // 选中小剪刀按 Delete = 恢复
        if let i = redactionIndex(selected) { edit { $0.redactions.remove(at: i) }; selected = nil; return }
        if let i = annotationIndex(selected) { edit { $0.annotations.remove(at: i) }; selected = nil; return }
        if let i = captionIndex(selected) { edit { $0.captions.remove(at: i) }; selected = nil; return }
        if let i = cutIndex(selected) { edit { $0.cuts.remove(at: i) }; selected = nil; return }
        if let i = layoutIndex(selected) { edit { $0.layouts.remove(at: i) }; selected = nil; return }
        if let i = voiceIndex(selected) { edit { $0.voiceovers.remove(at: i) }; selected = nil; return }
        if let i = musicIndex(selected) { edit { $0.music.clips.remove(at: i) }; selected = nil; return }
        guard let i = index(selected) else { return }
        edit { $0.segments.remove(at: i) }
        selected = nil
    }

    // ---- 剪掉 / 快放的段 ----
    func cutIndex(_ id: UUID?) -> Int? { project.cuts.firstIndex { $0.id == id } }
    // 某一段剪辑能占的范围：不压到别的剪辑段、不出修剪范围
    func cutRoom(for id: UUID, in p: StudioProject) -> ClosedRange<Double> {
        let cs = p.cuts.sorted { $0.start < $1.start }
        guard let i = cs.firstIndex(where: { $0.id == id }) else { return p.trimStart...p.trimEnd }
        let lo = max(p.trimStart, i > 0 ? cs[i - 1].end : 0)
        let hi = min(p.trimEnd, i + 1 < cs.count ? cs[i + 1].start : duration)
        return lo...max(lo, hi)
    }
    // 在播放头处放一段「剪掉」（默认 2 秒），放不下就缩短
    func addCut(at t: Double) {
        let len = 2.0
        let cs = project.cuts.sorted { $0.start < $1.start }
        guard !cs.contains(where: { t >= $0.start && t < $0.end }) else { NSSound.beep(); return }
        let lo = max(project.trimStart, cs.last(where: { $0.end <= t })?.end ?? 0)
        let hi = min(project.trimEnd, cs.first(where: { $0.start > t })?.start ?? duration)
        guard hi - lo >= 0.2 else { NSSound.beep(); return }
        var s = max(lo, t), e = min(hi, s + len)
        s = max(lo, e - len); e = min(hi, s + len)
        let c = CutSegment(start: s, end: e)
        edit { $0.cuts.append(c); $0.cuts.sort { $0.start < $1.start } }
        selected = c.id
    }

    // ---- 摄像头画中画 ----
    var hasCamera: Bool { track.camera != nil }
    func cameraLayer(_ r: StudioRenderer) -> CameraLayer? {
        CameraLayer(dir: dir, track: track, project: project, canvas: CGSize(width: r.lay.cw, height: r.lay.ch), background: r.background)
    }
    func camSet<T: Equatable>(_ kp: WritableKeyPath<CameraSettings, T>) -> Binding<T> {
        Binding(get: { (self.project.camera ?? CameraSettings())[keyPath: kp] },
                set: { v in self.edit { var c = $0.camera ?? CameraSettings(); c[keyPath: kp] = v; $0.camera = c } })
    }
    func camSlider(_ kp: WritableKeyPath<CameraSettings, Double>) -> Binding<Double> {
        Binding(get: { (self.project.camera ?? CameraSettings())[keyPath: kp] },
                set: { v in self.dragBegin(); self.dragUpdate { var c = $0.camera ?? CameraSettings(); c[keyPath: kp] = v; $0.camera = c } })
    }
    // 小窗在预览里的位置（比例，左上为原点），给拖动命中用
    var cameraPipRect: CGRect? {
        guard hasCamera, let c = project.camera, c.show else { return nil }
        let lay = renderer.lay, r = CameraLayer.pipRect(c, cw: lay.cw, ch: lay.ch)
        return CGRect(x: r.minX / lay.cw, y: 1 - r.maxY / lay.ch, width: r.width / lay.cw, height: r.height / lay.ch)
    }
    // 在预览上拖小窗：d = 拖了多远（预览里的比例）
    func moveCamera(by d: CGSize, phase: Int) {
        switch phase {
        case 0: dragBegin(); revealSection = "camera"
        case 1: dragUpdate { p in
            var c = p.camera ?? CameraSettings()
            c.x = min(max(c.x + d.width, 0), 1); c.y = min(max(c.y + d.height, 0), 1)
            p.camera = c
        }
        default: dragEnd()
        }
    }
    func layoutIndex(_ id: UUID?) -> Int? { project.layouts.firstIndex { $0.id == id } }
    func layoutRoom(for id: UUID, in p: StudioProject) -> ClosedRange<Double> {
        let ls = p.layouts.sorted { $0.start < $1.start }
        guard let i = ls.firstIndex(where: { $0.id == id }) else { return 0...duration }
        let lo = i > 0 ? ls[i - 1].end : 0, hi = i + 1 < ls.count ? ls[i + 1].start : duration
        return lo...max(lo, hi)
    }
    // 在 t 处放一段布局（默认「摄像头全屏」2 秒），放不下就缩短
    func addLayout(at t: Double) {
        let len = 2.0, ls = project.layouts.sorted { $0.start < $1.start }
        guard !ls.contains(where: { t >= $0.start && t < $0.end }) else { NSSound.beep(); return }
        let lo = ls.last(where: { $0.end <= t })?.end ?? 0, hi = ls.first(where: { $0.start > t })?.start ?? duration
        guard hi - lo >= 0.3 else { NSSound.beep(); return }
        var s = max(lo, t), e = min(hi, s + len)
        s = max(lo, e - len); e = min(hi, s + len)
        let g = CameraLayoutSeg(start: s, end: e)
        edit { $0.layouts.append(g); $0.layouts.sort { $0.start < $1.start } }
        selected = g.id
    }

    // ---- 字幕 ----
    func captionLayer(_ r: StudioRenderer) -> CaptionLayer {
        let c = CaptionLayer(captions: project.captions, style: project.captionStyle, map: timeMap, trim: (project.trimStart, project.trimEnd),
                             canvas: CGSize(width: r.lay.cw, height: r.lay.ch))
        c.keys = KeyLayer(strokes: track.shortcuts, look: project.look, map: timeMap, trim: (project.trimStart, project.trimEnd),
                          canvas: CGSize(width: r.lay.cw, height: r.lay.ch), captionsShown: !c.isEmpty)
        return c
    }
    var hasShortcuts: Bool { !(track.shortcuts ?? []).isEmpty }
    func captionIndex(_ id: UUID?) -> Int? { project.captions.firstIndex { $0.id == id } }
    // 一句字幕能占的范围（素材时间）：不压到前后两句、不出修剪范围
    func captionRoom(for id: UUID, in p: StudioProject) -> ClosedRange<Double> {
        let cs = p.captions.sorted { $0.start < $1.start }
        guard let i = cs.firstIndex(where: { $0.id == id }) else { return p.trimStart...p.trimEnd }
        let lo = max(p.trimStart, i > 0 ? cs[i - 1].end : 0)
        let hi = min(p.trimEnd, i + 1 < cs.count ? cs[i + 1].start : duration)
        return lo...max(lo, hi)
    }
    // 在 t 处新加一句空白字幕（默认 2 秒，放不下就缩短），马上弹出改字
    func addCaption(at t: Double) {
        let cs = project.captions.sorted { $0.start < $1.start }
        guard !cs.contains(where: { t >= $0.start && t < $0.end }) else { NSSound.beep(); return }
        let lo = max(project.trimStart, cs.last(where: { $0.end <= t })?.end ?? 0)
        let hi = min(project.trimEnd, cs.first(where: { $0.start > t })?.start ?? duration)
        guard hi - lo >= 0.3 else { NSSound.beep(); return }
        var s = max(lo, t), e = min(hi, s + 2)
        s = max(lo, e - 2); e = min(hi, s + 2)
        let c = Caption(start: s, end: e, text: "")
        edit { $0.captions.append(c); $0.captions.sort { $0.start < $1.start } }
        selected = c.id; editingCaption = c.id
    }
    func setCaptionText(_ id: UUID, _ text: String) {
        guard let i = captionIndex(id) else { return }
        let t = text.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces)
        edit { $0.captions[i].text = t }
    }
    // 在播放头处拆成两句：字按时间比例分
    func splitCaption(_ id: UUID, at t: Double) {
        guard let i = captionIndex(id) else { return }
        let c = project.captions[i]
        guard t > c.start + 0.15, t < c.end - 0.15 else { NSSound.beep(); return }
        let chars = Array(c.text), k = min(max(1, Int((Double(chars.count) * (t - c.start) / (c.end - c.start)).rounded())), max(1, chars.count - 1))
        var a = c, b = Caption(start: t, end: c.end, text: chars.count > 1 ? String(chars[k...]) : "")
        a.end = t; a.text = chars.count > 1 ? String(chars[..<k]) : c.text
        b.id = UUID()
        edit { $0.captions[i] = a; $0.captions.append(b); $0.captions.sort { $0.start < $1.start } }
        selected = b.id
    }
    // 和下一句合成一句
    func mergeCaption(_ id: UUID) {
        let cs = project.captions.sorted { $0.start < $1.start }
        guard let i = cs.firstIndex(where: { $0.id == id }), i + 1 < cs.count else { NSSound.beep(); return }
        var a = cs[i]; let b = cs[i + 1]
        let ascii = (a.text.last?.isASCII ?? false) && (b.text.first?.isASCII ?? false)
        a.text = a.text + (ascii ? " " : "") + b.text; a.end = b.end
        edit { p in p.captions = cs.enumerated().compactMap { $0.offset == i ? a : $0.offset == i + 1 ? nil : $0.element } }
        selected = a.id
    }
    var hasRecordedAudio: Bool { !audioChannels.isEmpty }
    // 语音转文字：识别完整个替换成新的字幕（能撤销）
    func generateCaptions(_ source: CaptionMaker.Source) {
        guard captionJob == nil else { return }
        captionError = nil
        captionJob = CaptionJob(message: L("准备识别…", "Preparing…"), progress: nil)
        saveNow()
        let dir = self.dir, p = project, canvas = CGSize(width: renderer.lay.cw, height: renderer.lay.ch)
        captionTask = Task {
            do {
                let caps = try await CaptionMaker.make(dir: dir, project: p, source: source, canvas: canvas) { msg, f in
                    Task { @MainActor [weak self] in if self?.captionJob != nil { self?.captionJob = CaptionJob(message: msg, progress: f) } }
                }
                if caps.isEmpty { captionError = L("没听出说的话", "No speech was found") }
                else { edit { $0.captions = caps }; selected = nil }
            } catch is CancellationError {
            } catch {
                if !Task.isCancelled { captionError = L("识别失败：", "Transcription failed: ") + error.localizedDescription }
            }
            captionJob = nil; captionTask = nil
        }
    }
    func cancelCaptions() { captionTask?.cancel(); captionTask = nil; captionJob = nil }
    // 另存 .srt（成片时间）：给剪映、抖音、B 站用
    func exportSRT() {
        let p = NSSavePanel()
        p.allowedContentTypes = [.init(filenameExtension: "srt") ?? .plainText]
        p.nameFieldStringValue = dir.lastPathComponent + ".srt"
        p.directoryURL = Recorder.folder
        guard p.runModal() == .OK, let u = p.url else { return }
        do { try SRT.text(project).write(to: u, atomically: true, encoding: .utf8) }
        catch { captionError = error.localizedDescription }
    }
    func capStyle<T: Equatable>(_ kp: WritableKeyPath<CaptionStyle, T>) -> Binding<T> {
        Binding(get: { self.project.captionStyle[keyPath: kp] }, set: { v in self.edit { $0.captionStyle[keyPath: kp] = v } })
    }
    func capSlider(_ kp: WritableKeyPath<CaptionStyle, Double>) -> Binding<Double> {
        Binding(get: { self.project.captionStyle[keyPath: kp] }, set: { v in self.dragBegin(); self.dragUpdate { $0.captionStyle[keyPath: kp] = v } })
    }

    // 预览上画遮挡 / 标注用：播放头这一刻的镜头，原画面比例 ↔ 画布坐标
    func overlayMapping() -> OverlayMapping {
        let (r, plan) = box.both
        let f = plan.isEmpty ? FramePlan() : plan[min(max(Int((time * fps).rounded()), 0), plan.count - 1)]
        return OverlayMapping(lay: r.lay, f: plan.isEmpty ? nil : f, ppp: track.pixelsPerPoint)
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
    // 声音设置（音量滑块拖一次算一步撤销）
    func audioSlider(_ kp: WritableKeyPath<AudioSettings, Double>) -> Binding<Double> {
        Binding(get: { self.project.audio[keyPath: kp] }, set: { v in self.dragBegin(); self.dragUpdate { $0.audio[keyPath: kp] = v } })
    }
    func audioPick<T: Equatable>(_ kp: WritableKeyPath<AudioSettings, T>) -> Binding<T> {
        Binding(get: { self.project.audio[keyPath: kp] }, set: { v in self.edit { $0.audio[keyPath: kp] = v } })
    }
    func audioToggle(_ kp: WritableKeyPath<AudioSettings, Bool>) -> Binding<Bool> {
        Binding(get: { self.project.audio[keyPath: kp] }, set: { v in self.edit { $0.audio[keyPath: kp] = v } })
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
        exported = nil; exportError = nil; exportCopied = false; exportProgress = 0
        let dir = self.dir, out = self.out, p = project, o = exportOptions
        exportTask = Task {
            do {
                let u = try await Studio.render(project: dir, to: out, style: StudioStyle(o), edits: p) { v in
                    Task { @MainActor [weak self] in if self?.exportProgress != nil { self?.exportProgress = v } }
                }
                exported = u
                // 导出后放进剪贴板：在微信、访达里直接粘贴就是这个文件
                if o.copyToClipboard {
                    NSPasteboard.general.clearContents()
                    exportCopied = NSPasteboard.general.writeObjects([u as NSURL])
                }
            } catch is CancellationError {
            } catch {
                exportError = L("导出失败：", "Export failed: ") + error.localizedDescription
            }
            exportProgress = nil; exportTask = nil
        }
    }

    func cancelExport() { exportTask?.cancel() }
}

// ---- 界面 ----
struct EditorView: View {
    @ObservedObject var ed: StudioEditor
    @AppStorage("editorTimelineH") private var timelineH = 300.0
    @State var showCaptionMenu = false
    @State var captionSource = CaptionMaker.Source.recorded

    var body: some View {
        VStack(spacing: 0) {
            toolbar.padding(.horizontal, 14).padding(.vertical, 10)
            Divider()
            HStack(spacing: 0) {
                VStack(spacing: 0) {
                    preview.clipShape(RoundedRectangle(cornerRadius: 8)).padding(14)
                    // 预览和时间轴之间：上下拖动改两边的高度（记住）
                    SplitHandle(height: $timelineH)
                    controlBar.padding(.horizontal, 14).padding(.vertical, 8)
                    Divider()
                    VStack(alignment: .leading, spacing: 10) {
                        if ed.currentPiece != nil { PieceInspector(ed: ed).frame(height: 30) }   // 选中剪辑轨上的一段
                        else if ed.redactionIndex(ed.selected) != nil { RedactionInspector(ed: ed).frame(height: 30) }   // 选中遮挡
                        else if ed.annotationIndex(ed.selected) != nil { AnnotationInspector(ed: ed).frame(height: 30) }   // 选中标注
                        else if ed.captionIndex(ed.selected) != nil { captionInspector.frame(height: 30) }   // 选中字幕
                        else if ed.cutIndex(ed.selected) != nil { cutInspector.frame(height: 30) }   // 选中剪辑段
                        else if ed.layoutIndex(ed.selected) != nil { layoutInspector.frame(height: 30) }   // 选中布局段
                        else if ed.voiceIndex(ed.selected) != nil { voiceInspector.frame(height: 30) }   // 选中讲解
                        else if ed.musicIndex(ed.selected) != nil { MusicInspector(ed: ed).frame(height: 30) }   // 选中音乐
                        else if ed.selected != nil { inspector.frame(height: 30) }   // 选中推近段才出现
                        else if !ed.suggestions.isEmpty || ed.suggesting || ed.suggestNone { SuggestionBar(ed: ed).frame(height: 30) }   // 建议加速
                        // 左边轨道名 + 时间轴；轨道多了（叠在一起的标注各占一行）就上下滚
                        let total = TimelineLanes(ed).total
                        ScrollView(.vertical) {
                            HStack(spacing: 6) {
                                TrackHeaders(ed: ed).frame(width: 62, height: total)
                                timeline.frame(height: total)
                            }
                        }
                        .scrollIndicators(total > timelineH ? .visible : .hidden)
                        .frame(height: timelineH)
                            .help(L("从上到下：剪掉 / 快放、推近、讲解、字幕、遮挡、标注、音乐；点选来调整，空白处双击新加推近（字幕那条双击新加字幕），两端黄条修剪",
                                   "Top to bottom: cut / speed-up, zooms, voiceovers, captions, redactions, annotations, music. Click to edit, double-click empty space to add a zoom (a caption on the caption lane), drag the yellow bars to trim"))
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
                Button(L("取消", "Cancel")) { ed.cancelExport() }
            } else if let u = ed.exported {
                Text(ed.exportCopied ? L("已导出，已复制到剪贴板", "Exported and copied") : L("已导出", "Exported")).font(.callout).foregroundStyle(.secondary)
                Button(L("打开", "Open")) { NSWorkspace.shared.open(u) }
                Button(L("在访达中显示", "Show in Finder")) { NSWorkspace.shared.activateFileViewerSelecting([u]) }
            } else if let e = ed.exportError {
                Text(e).font(.callout).foregroundStyle(.red).lineLimit(1)
            }
            ExportOptionsButton(ed: ed).disabled(ed.exportProgress != nil)
            Button(L("导出", "Export")) { ed.export() }.buttonStyle(.borderedProminent).disabled(ed.exportProgress != nil)
        }
    }

    // 预览和时间轴之间的一条（像 Screen Studio）：左边编辑工具，正中播放和时间，右边时间轴缩放
    @State private var barW = 2000.0
    private var controlBar: some View {
        ZStack {
            HStack(spacing: 10) {
                Button { ed.addSegment(at: ed.time) } label: { Label(L("推近", "Zoom"), systemImage: "plus.magnifyingglass") }
                    .help(L("在播放头处新加一段推近（镜头放大凑近看）；也可以在时间轴空白处双击", "Add a zoom-in at the playhead; you can also double-click empty space on the timeline"))
                Button { ed.split(at: ed.time) } label: { Label(L("分割", "Split"), systemImage: "scissors") }
                    .help(L("在播放头处切一刀（S 或 ⌘B）：点选切出来的一段按 Delete 删掉，时间轴直接合拢；也能给那段改速度", "Split at the playhead (S or ⌘B): select a part and press Delete to remove it, the timeline closes up; you can also change its speed"))
                voiceButton
                captionButton
                OverlayButtons(ed: ed)
                Spacer()
                // 右半边：自动生成、撤销重做（只放图标，鼠标停上去有说明），再是时间轴缩放
                Button { ed.suggestSpeed() } label: { Image(systemName: "gauge.with.dots.needle.67percent") }
                    .disabled(ed.suggesting)
                    .help(L("建议加速：找出很久没操作、画面也不动的空档（建议 4×）和一直在打字的地方（建议 2×），在剪辑轨上用虚线标出来，点 ✓ 采纳",
                            "Suggest speed-ups: finds long idle stretches (4×) and long typing (2×) and marks them on the cut lane with dashed outlines; click ✓ to apply"))
                Button { ed.regenerate() } label: { Image(systemName: "wand.and.stars") }
                    .help(L("重新自动生成：按录制时的点击和打字重新排推近段（会替换现在的段，可以撤销 ⌘Z）", "Auto-generate: rebuild zooms from your clicks and typing (replaces the current zooms; undo with ⌘Z)"))
                Button { ed.undo() } label: { Image(systemName: "arrow.uturn.backward") }.disabled(!ed.canUndo).help(L("撤销上一步（⌘Z）", "Undo the last change (⌘Z)"))
                Button { ed.redo() } label: { Image(systemName: "arrow.uturn.forward") }.disabled(!ed.canRedo).help(L("重做刚撤销的那步（⌘⇧Z）", "Redo what you just undid (⌘⇧Z)"))
                Divider().frame(height: 16)
                Group {
                    Button { ed.setTimelineScale(ed.timelineScale / 1.5) } label: { Image(systemName: "minus.magnifyingglass") }
                        .disabled(ed.timelineScale <= 1)
                    if barW >= 1180 {   // 窄窗口只留 ± 两个按钮
                        Slider(value: Binding(get: { ed.timelineScale }, set: { ed.setTimelineScale($0) }), in: TimelineMath.scaleRange).frame(width: 90)
                    }
                    Text(String(format: "%.1f×", ed.timelineScale)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 30, alignment: .leading)
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
                let m = ed.timeMap
                Text(clock(m.out(ed.time) - ed.project.trimStart) + " / " + clock(m.out(ed.project.trimEnd) - ed.project.trimStart))   // 按成片算（修剪、剪掉、快放之后）
                    .font(.callout.monospacedDigit()).foregroundStyle(.secondary).frame(width: 130, alignment: .leading)
            }
        }
        // 窗口窄了（左边带字的按钮会碰到正中的播放键）：左边只留图标，鼠标停上去有说明
        .labelStyle(BarLabel(compact: barW < 1180))
        .background(GeometryReader { g in Color.clear.onAppear { barW = g.size.width }.onChange(of: g.size.width) { _, w in barW = w } })
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
                let x = ed.visTime(t) / ed.visDuration * w * ed.timelineScale
                if x < ed.timelineOffset || x > ed.timelineOffset + w - 20 { ed.panTimeline(x - w * 0.1 - ed.timelineOffset) }
            }
        }
    }

    private var preview: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .underPageBackgroundColor))
            if let img = ed.still {
                Image(nsImage: img).resizable().aspectRatio(contentMode: .fit).scaleEffect(ed.previewZoom).offset(ed.previewPan)
            } else {
                PlayerPane(player: ed.player, picking: ed.picking, camRect: ed.cameraPipRect, onPick: { ed.pick($0) }, onTap: { ed.tapPreview($0) },
                           onCamMove: { d, ph in ed.moveCamera(by: d, phase: ph) }, zoom: ed.previewZoom, pan: ed.previewPan)
            if let f = ed.dragFrame { Image(nsImage: f).resizable().aspectRatio(contentMode: .fit).scaleEffect(ed.previewZoom).offset(ed.previewPan).allowsHitTesting(false) }
            }
            // 遮挡、标注：选中的那个出编辑框，「遮挡 / 标注」按下后在画面上拖出来
            OverlayCanvas(ed: ed)
            PreviewZoomBar(ed: ed)
            if let t = ed.toast {
                VStack {
                    Spacer()
                    Text(t).font(.callout).padding(.horizontal, 14).padding(.vertical, 8)
                        .background(.ultraThinMaterial, in: Capsule()).shadow(radius: 6).padding(.bottom, 14)
                }.allowsHitTesting(false).transition(.opacity)
            }
            if let d = ed.drawing {
                VStack {
                    Text(d.hint).font(.callout).padding(.horizontal, 12).padding(.vertical, 6)
                        .background(.regularMaterial, in: Capsule())
                    Spacer()
                }.padding(10).allowsHitTesting(false)
            }
            if ed.picking {
                VStack {
                    Text(L("在画面上点一下，设成这段推近盯住的位置（Esc 取消）", "Click the picture to set where this zoom looks (Esc to cancel)"))
                        .font(.callout).padding(.horizontal, 12).padding(.vertical, 6)
                        .background(.regularMaterial, in: Capsule())
                    Spacer()
                }.padding(10).allowsHitTesting(false)
            }
            // 录讲解：倒数大数字；录音中左上角红点 + 时长 + 音量
            if let n = ed.countdown {
                Text("\(n)").font(.system(size: 96, weight: .bold, design: .rounded)).foregroundStyle(.white)
                    .frame(width: 170, height: 170).background(Circle().fill(.black.opacity(0.45)))
                    .allowsHitTesting(false)
            }
            if let r = ed.voiceRec {
                VStack {
                    HStack(spacing: 8) {
                        Circle().fill(Color.red).frame(width: 10, height: 10)
                        Text(L("正在录讲解", "Recording voiceover") + "  " + clock(r.elapsed)).font(.callout.monospacedDigit().weight(.medium))
                        LevelMeter(level: r.level).frame(width: 70, height: 6)
                        Text(L("空格 / Esc 停止", "Space / Esc to stop")).font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(.horizontal, 12).padding(.vertical, 6).background(.regularMaterial, in: Capsule())
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
                ZoomSpeedPicker(ed: ed, index: i)
                Spacer()
                Button(role: .destructive) { ed.deleteSelected() } label: { Label(L("删除", "Delete"), systemImage: "trash") }
            }
        }
    }
}

extension EditorView {
    // 录讲解：没在录 = 麦克风按钮；倒数中 / 录音中 = 红色「停止」，旁边是时长和音量
    @ViewBuilder var voiceButton: some View {
        if ed.voiceRec != nil || ed.countdown != nil {
            Button { ed.toggleVoice() } label: {
                Label(L("停止", "Stop") + (ed.voiceRec.map { " " + clock($0.elapsed) } ?? ""), systemImage: "stop.circle.fill")
                    .monospacedDigit()
            }
            .tint(.red).buttonStyle(.borderedProminent)
            .help(L("停止录讲解（空格 / Esc）", "Stop recording (Space / Esc)"))
        } else {
            Button { ed.toggleVoice() } label: { Label(L("录讲解", "Voiceover"), systemImage: "mic") }
                .help(L("从播放头开始边播边录你的讲解（麦克风）；录好的在时间轴最下面一条，可以挪、剪两头、调音量", "Record your narration with the microphone while the video plays from the playhead; it appears on the bottom lane where you can move it, trim its ends and set its volume"))
        }
    }

    // 字幕：点开选识别哪段声音，识别中显示进度、能取消；另存 .srt
    @ViewBuilder var captionButton: some View {
        if let j = ed.captionJob {
            HStack(spacing: 6) {
                if let f = j.progress { ProgressView(value: f).frame(width: 70) } else { ProgressView().controlSize(.small) }
                Text(j.message).font(.caption).foregroundStyle(.secondary).lineLimit(1).frame(maxWidth: 190, alignment: .leading)
                Button(L("取消", "Cancel")) { ed.cancelCaptions() }.controlSize(.small)
            }
        } else {
            Button { showCaptionMenu.toggle() } label: { Label(L("字幕", "Captions"), systemImage: "captions.bubble") }
                .help(L("把说的话自动变成字幕（在本机识别普通话），一次只显示一行；也能另存 .srt", "Turn speech into captions (Mandarin, on this Mac), one line at a time; you can also save an .srt"))
                .popover(isPresented: $showCaptionMenu, arrowEdge: .bottom) { captionMenu.padding(14).frame(width: 330) }
        }
    }
    @ViewBuilder private var captionMenu: some View {
        let hasRec = ed.hasRecordedAudio, hasVoice = !ed.project.voiceovers.isEmpty
        VStack(alignment: .leading, spacing: 10) {
            Text(L("语音转字幕", "Speech to Captions")).font(.headline)
            Picker(L("识别", "From"), selection: $captionSource) {
                Text(L("录到的声音", "Recorded sound")).tag(CaptionMaker.Source.recorded).disabled(!hasRec)
                Text(L("讲解", "Voiceover")).tag(CaptionMaker.Source.voice).disabled(!hasVoice)
                Text(L("两样都要", "Both")).tag(CaptionMaker.Source.both).disabled(!hasRec || !hasVoice)
            }
            Text(L("在本机识别普通话，不上传。第一次用时系统会下载识别资源。会替换现在的字幕（可以撤销 ⌘Z）。",
                   "Recognizes Mandarin on this Mac, nothing is uploaded. The first time, macOS downloads the speech model. Replaces the current captions (undo with ⌘Z)."))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if let e = ed.captionError { Text(e).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            HStack {
                Button(L("开始识别", "Transcribe")) { showCaptionMenu = false; ed.generateCaptions(captionSource) }
                    .buttonStyle(.borderedProminent).disabled(!hasRec && !hasVoice)
                Spacer()
                Button(L("加一句", "Add")) { showCaptionMenu = false; ed.addCaption(at: ed.time) }
                Button(L("存 .srt…", "Save .srt…")) { showCaptionMenu = false; ed.exportSRT() }.disabled(ed.project.captions.isEmpty)
            }
            Divider()
            SpeakBox(ed: ed)
        }
        .onAppear { if !hasRec && hasVoice { captionSource = .voice } else if hasRec && captionSource != .recorded && !hasVoice { captionSource = .recorded } }
    }

    // 选中字幕：时间、文字、改字 / 拆分 / 合并 / 删除
    @ViewBuilder var captionInspector: some View {
        if let i = ed.captionIndex(ed.selected) {
            let c = ed.project.captions[i]
            HStack(spacing: 12) {
                Text(L("字幕", "Caption")).font(.callout.weight(.semibold))
                Text(clock(c.start) + " – " + clock(c.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                CaptionField(ed: ed, cap: c).frame(minWidth: 180, maxWidth: 420)
                Spacer(minLength: 6)
                Button { ed.splitCaption(c.id, at: ed.time) } label: { Label(L("拆分", "Split"), systemImage: "scissors") }
                    .disabled(!(ed.time > c.start + 0.15 && ed.time < c.end - 0.15))
                    .help(L("在播放头处拆成两句", "Split into two at the playhead"))
                Button { ed.mergeCaption(c.id) } label: { Label(L("合并", "Merge"), systemImage: "arrow.right.to.line") }
                    .help(L("和下一句合成一句", "Merge with the next caption"))
                Button { ed.speakCaptions([c.id]) } label: { Label(L("念成配音", "Speak"), systemImage: "waveform") }
                    .disabled(c.text.isEmpty || ed.speaking != nil)
                    .help(L("用系统声音把这句念出来，放到讲解轨上（声音在「字幕」菜单里选）", "Speak this caption onto the voiceover lane (choose the voice in the Captions menu)"))
                Button(role: .destructive) { ed.deleteSelected() } label: { Image(systemName: "trash") }.help(L("删除这句", "Delete this caption"))
            }
        }
    }

    // 选中讲解：时间、音量、删除
    @ViewBuilder var voiceInspector: some View {
        if let i = ed.voiceIndex(ed.selected) {
            let c = ed.project.voiceovers[i], m = ed.timeMap
            HStack(spacing: 14) {
                Text(L("讲解", "Voiceover")).font(.callout.weight(.semibold))
                Text(clock(c.start) + " – " + clock(c.srcEnd(m)) + "  ·  " + String(format: L("%.1f 秒", "%.1fs"), c.length))
                    .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Text(L("音量", "Volume")).font(.callout)
                Slider(value: Binding(get: { c.volume }, set: { v in ed.dragBegin(); ed.dragUpdate { $0.voiceovers[i].volume = v } }),
                       in: 0...2, onEditingChanged: { if !$0 { ed.dragEnd() } })
                    .frame(width: 160)
                Text(c.volume < 0.005 ? L("静音", "Off") : "\(Int((c.volume * 100).rounded()))%").font(.callout.monospacedDigit()).frame(width: 44, alignment: .leading)
                Spacer()
                Button(role: .destructive) { ed.deleteSelected() } label: { Label(L("删除", "Delete"), systemImage: "trash") }
            }
        }
    }

    // 选中剪辑段：剪掉 / 加速几倍，原来多长、成片里多长
    @ViewBuilder var layoutInspector: some View {
        if let i = ed.layoutIndex(ed.selected) {
            let g = ed.project.layouts[i]
            HStack(spacing: 14) {
                Text(L("布局", "Layout")).font(.callout.weight(.semibold))
                Text(clock(g.start) + " – " + clock(g.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Picker("", selection: Binding(get: { g.mode }, set: { m in ed.edit { $0.layouts[i].mode = m } })) {
                    ForEach(CameraLayoutSeg.Mode.allCases, id: \.self) { m in Label(CameraLayoutSeg.name(m), systemImage: CameraLayoutSeg.icon(m)).tag(m) }
                }
                .labelsHidden().frame(width: 200)
                Spacer()
                Button(role: .destructive) { ed.deleteSelected() } label: { Label(L("删除", "Delete"), systemImage: "trash") }
            }
        }
    }

    @ViewBuilder var cutInspector: some View {
        if let i = ed.cutIndex(ed.selected), ed.project.cuts[i].mode == .cut {
            // 删掉的段（剪辑轨上的小剪刀）：只能恢复
            let c = ed.project.cuts[i]
            HStack(spacing: 14) {
                Image(systemName: "scissors").foregroundStyle(.red)
                Text(L("这里删掉了 \(String(format: "%.1f", c.end - c.start)) 秒（素材 \(clock(c.start)) – \(clock(c.end))）", "\(String(format: "%.1f", c.end - c.start))s removed here (\(clock(c.start)) – \(clock(c.end)))"))
                    .font(.callout).foregroundStyle(.secondary)
                Spacer()
                Button { ed.restoreCut(c.id) } label: { Label(L("恢复这段", "Restore"), systemImage: "arrow.uturn.backward") }
            }.controlSize(.small)
        } else if let i = ed.cutIndex(ed.selected) {
            let c = ed.project.cuts[i], len = c.end - c.start
            HStack(spacing: 14) {
                Text(L("剪辑", "Edit")).font(.callout.weight(.semibold))
                Text(clock(c.start) + " – " + clock(c.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Picker("", selection: Binding(get: { c.mode }, set: { m in ed.edit { $0.cuts[i].mode = m } })) {
                    Text(L("剪掉", "Cut out")).tag(CutSegment.Mode.cut)
                    Text(L("加速", "Speed up")).tag(CutSegment.Mode.speed)
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 150)
                if c.mode == .speed {
                    Picker("", selection: Binding(get: { c.rate }, set: { r in ed.edit { $0.cuts[i].rate = r } })) {
                        Text("2×").tag(2.0); Text("4×").tag(4.0); Text("8×").tag(8.0)
                    }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 130)
                }
                Text(c.mode == .cut ? L("成片里去掉 \(String(format: "%.1f", len)) 秒", "Removes \(String(format: "%.1f", len))s")
                                    : L("\(String(format: "%.1f", len)) 秒 → \(String(format: "%.1f", len / c.rate)) 秒", "\(String(format: "%.1f", len))s → \(String(format: "%.1f", len / c.rate))s"))
                    .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
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
    var camRect: CGRect? = nil                       // 摄像头小窗在画面里的位置（比例，左上为原点），能拖
    let onPick: (CGPoint) -> Void
    var onTap: ((CGPoint?) -> Void)? = nil
    var onCamMove: ((CGSize, Int) -> Void)? = nil   // 拖了多远（画面比例）；0 开始 / 1 拖动中 / 2 松手
    var zoom = 1.0, pan = CGSize.zero
    func makeNSView(context: Context) -> PlayerNSView { let v = PlayerNSView(); v.layerP.player = player; v.onPick = onPick; v.onTap = onTap; return v }
    func updateNSView(_ v: PlayerNSView, context: Context) { v.picking = picking; v.onPick = onPick; v.onTap = onTap; v.camRect = camRect; v.onCamMove = onCamMove
        if v.zoom != zoom || v.pan != pan { v.zoom = zoom; v.pan = pan; v.needsLayout = true } }
}
final class PlayerNSView: NSView {
    let layerP = AVPlayerLayer()
    var picking = false { didSet { window?.invalidateCursorRects(for: self) } }
    var onPick: ((CGPoint) -> Void)?
    var onTap: ((CGPoint?) -> Void)?         // 普通点一下：画面里的比例（左上原点），点在画面外 = nil
    var camRect: CGRect? { didSet { if camRect != oldValue { window?.invalidateCursorRects(for: self) } } }
    var onCamMove: ((CGSize, Int) -> Void)?
    private var camDragFrom: CGPoint?
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer = CALayer()
        layerP.videoGravity = .resizeAspect
        layer?.addSublayer(layerP)
    }
    required init?(coder: NSCoder) { fatalError() }
    var zoom = 1.0, pan = CGSize.zero
    // 画面在本视图里的位置（放大后画面层比视图大，要换算过来）
    private var videoRectInView: CGRect { layer.map { $0.convert(layerP.videoRect, from: layerP) } ?? layerP.videoRect }
    override func layout() {
        super.layout(); CATransaction.begin(); CATransaction.setDisableActions(true)
        // 预览放大：画面层按中心放大、再挪 pan（SwiftUI 的 y 朝下，这里 y 朝上）
        let w = bounds.width * zoom, h = bounds.height * zoom
        layerP.frame = CGRect(x: bounds.midX - w / 2 + pan.width, y: bounds.midY - h / 2 - pan.height, width: w, height: h)
        CATransaction.commit()
    }
    // 画面比例（左上原点）的矩形 → 本视图坐标
    private func viewRect(_ n: CGRect) -> CGRect? {
        let r = videoRectInView
        guard r.width > 0 else { return nil }
        return CGRect(x: r.minX + n.minX * r.width, y: r.minY + (1 - n.maxY) * r.height, width: n.width * r.width, height: n.height * r.height)
    }
    override func resetCursorRects() {
        if picking { addCursorRect(bounds, cursor: .crosshair) }
        else if let c = camRect, let v = viewRect(c) { addCursorRect(v, cursor: .openHand) }
    }
    override func mouseDown(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil), r = videoRectInView
        if !picking, let c = camRect, let v = viewRect(c), v.contains(p) {
            camDragFrom = p; onCamMove?(.zero, 0); NSCursor.closedHand.push(); return
        }
        guard picking else {
            onTap?(r.width > 0 && r.contains(p) ? CGPoint(x: (p.x - r.minX) / r.width, y: 1 - (p.y - r.minY) / r.height) : nil)
            return
        }
        guard r.width > 0, r.contains(p) else { return }
        onPick?(CGPoint(x: (p.x - r.minX) / r.width, y: 1 - (p.y - r.minY) / r.height))
    }
    override func mouseDragged(with e: NSEvent) {
        guard let s = camDragFrom else { return }
        let p = convert(e.locationInWindow, from: nil), r = videoRectInView
        guard r.width > 0 else { return }
        onCamMove?(CGSize(width: (p.x - s.x) / r.width, height: -(p.y - s.y) / r.height), 1)
    }
    override func mouseUp(with e: NSEvent) {
        guard camDragFrom != nil else { return }
        camDragFrom = nil; NSCursor.pop(); onCamMove?(.zero, 2)
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

// 时间轴各条轨的纵向位置（遮挡 / 标注按叠了几层算高度）
struct TimelineLanes {
    static let row = 20.0, gap = 2.0
    let redY = 176.0, redH: Double, annY: Double, annH: Double, musY: Double, layY: Double, total: Double
    let redRows: Int, annRows: Int
    @MainActor init(_ ed: StudioEditor) {
        redRows = max(1, OverlayBlock.pack(ed.project.redactions.map { ($0.id, $0.start, $0.end) }).values.map { $0.1 }.max() ?? 1)
        annRows = max(1, OverlayBlock.pack(ed.project.annotations.map { ($0.id, $0.start, $0.end) }).values.map { $0.1 }.max() ?? 1)
        func h(_ n: Int) -> Double { Double(n) * Self.row + Double(n - 1) * Self.gap }
        redH = h(redRows); annY = redY + redH + 4; annH = h(annRows)
        musY = annY + annH + 6; layY = musY + 26
        total = (ed.hasCamera ? layY + 22 : musY + 22) + 4
    }
}

// 时间轴左边的轨道名（像剪映左边那列）
struct TrackHeaders: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        let ln = TimelineLanes(ed)
        let items: [(Double, Double, String, String)] = [
            (38, 20, "scissors", L("片段", "Clips")), (64, 40, "plus.magnifyingglass", L("推近", "Zoom")),
            (112, 26, "mic", L("讲解", "Voice")), (144, 26, "captions.bubble", L("字幕", "Captions")),
            (ln.redY, ln.redH, "eye.slash", L("遮挡", "Redact")), (ln.annY, ln.annH, "pencil.tip.crop.circle", L("标注", "Notes")),
            (ln.musY, 22, "music.note", L("音乐", "Music")),
        ] + (ed.hasCamera ? [(ln.layY, 22, "person.crop.circle", L("小窗", "Camera"))] : [])
        ZStack(alignment: .topLeading) {
            Color.clear
            ForEach(items, id: \.0) { y, h, icon, name in
                HStack(spacing: 3) {
                    Image(systemName: icon).font(.system(size: 9))
                    Text(name).font(.system(size: 10)).lineLimit(1).minimumScaleFactor(0.7)
                }
                .foregroundStyle(.secondary)
                .frame(width: 62, height: h, alignment: .leading)
                .offset(y: y)
            }
        }
    }
}

struct TimelineStrip: View {
    @ObservedObject var ed: StudioEditor
    // 各条轨的位置：遮挡、标注叠在一起时各占一整行（像剪映 / Premiere），轨道跟着变高
    private var lanes: TimelineLanes { TimelineLanes(ed) }
    private let rulerH = 22.0, cutY = 38.0, cutH = 20.0, segY = 64.0, segH = 40.0, voiceY = 112.0, voiceH = 26.0, capY = 144.0, capH = 26.0
    private var redY: Double { lanes.redY }
    private var redH: Double { lanes.redH }
    private var annY: Double { lanes.annY }
    private var annH: Double { lanes.annH }
    private var musY: Double { lanes.musY }
    private let musH = 22.0, layH = 22.0
    private var layY: Double { lanes.layY }
    private let accent = Color(red: 0.42, green: 0.36, blue: 0.95)

    var body: some View {
        GeometryReader { g in
            // 删掉的段在时间轴上合拢：横坐标按「可见时间」排；d = 可见总长（拖动时 像素 → 秒 也按它换算）
            let w = g.size.width, d = ed.visDuration
            let x = { (t: Double) in ed.visTime(t) / d * w }
            let tAt = { (px: Double) in ed.srcTime(vis: min(max(px / w * d, 0), d)) }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .controlBackgroundColor))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color(nsColor: .separatorColor)))
                    .gesture(SpatialTapGesture(count: 2).onEnded { ed.addSegment(at: tAt($0.location.x)) }
                        .exclusively(before: SpatialTapGesture().onEnded { ed.selected = nil; ed.picking = false; ed.seek(tAt($0.location.x)) }))
                    .contextMenu {
                        Button(L("在播放头分割（S）", "Split at Playhead (S)")) { ed.split(at: ed.time) }
                        Button(L("在播放头加推近", "Add Zoom at Playhead")) { ed.addSegment(at: ed.time) }
                        Button(L("在播放头加字幕", "Add Caption at Playhead")) { ed.addCaption(at: ed.time) }
                        Divider()
                        Button(L("全部字幕念成配音", "Speak All Captions")) { ed.speakCaptions() }.disabled(ed.project.captions.isEmpty || ed.speaking != nil)
                        Button(L("建议加速", "Suggest Speed-ups")) { ed.suggestSpeed() }
                        Button(L("重新自动生成推近", "Regenerate Zooms")) { ed.regenerate() }
                    }
                ruler(w: w, d: d, x: { $0 / d * w })
                    .contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
                        .onChanged { ed.scrubBegin(); ed.seek(tAt($0.location.x)) }
                        .onEnded { _ in ed.scrubEnd() })
                // 点击、打字的时刻
                ForEach(Array(ed.events.enumerated()), id: \.offset) { _, t in
                    Circle().fill(Color.secondary.opacity(0.55)).frame(width: 4, height: 4).position(x: x(t), y: rulerH + 9)
                }.allowsHitTesting(false)
                // 剪辑轨：剪掉（红斜纹）/ 快放（蓝斜纹 + 倍数）
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: cutH)
                    .offset(x: 2, y: cutY).allowsHitTesting(false)
                ForEach(ed.pieces, id: \.start) { p in
                    PieceBlock(ed: ed, piece: p).frame(width: max(4, x(p.end) - x(p.start)), height: cutH)
                        .offset(x: x(p.start), y: cutY)
                }
                ForEach(ed.removedCuts) { c in
                    CutSeam(ed: ed, cut: c).offset(x: x(c.start) - 8, y: cutY + (cutH - 16) / 2)
                }
                // 建议加速：虚线半透明，块上直接点 ✓ 采纳 / ✕ 忽略
                ForEach(ed.suggestions) { g in
                    SuggestionBlock(ed: ed, sug: g).frame(width: max(6, x(g.end) - x(g.start)), height: cutH)
                        .offset(x: x(g.start), y: cutY)
                }
                ForEach(ed.project.segments) { seg in
                    SegmentBlock(ed: ed, seg: seg, w: w, d: d, accent: accent).frame(width: max(6, x(seg.end) - x(seg.start)), height: segH)
                        .offset(x: x(seg.start), y: segY)
                }
                // 讲解轨：每段一块（带波形）；录音中那段实时长出来
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: voiceH)
                    .offset(x: 2, y: voiceY).allowsHitTesting(false)
                let m = ed.timeMap
                ForEach(ed.project.voiceovers) { c in
                    VoiceBlock(ed: ed, clip: c, w: w, d: d, peaks: ed.voicePeaks[c.file] ?? [])
                        .frame(width: max(6, x(c.srcEnd(m)) - x(c.start)), height: voiceH)
                        .offset(x: x(c.start), y: voiceY)
                }
                if let r = ed.voiceRec {
                    let e = m.src(m.out(r.start) + r.elapsed)
                    RoundedRectangle(cornerRadius: 5).fill(Color.red.opacity(0.75))
                        .overlay(HStack(spacing: 4) { Image(systemName: "mic.fill"); Text(clock(r.elapsed)).monospacedDigit() }
                            .font(.system(size: 10, weight: .bold)).foregroundStyle(.white).padding(.horizontal, 6), alignment: .leading)
                        .clipped()
                        .frame(width: max(6, x(e) - x(r.start)), height: voiceH)
                        .offset(x: x(r.start), y: voiceY).allowsHitTesting(false)
                }
                // 字幕轨：每句一块；这一条的空白处双击新加一句
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: capH)
                    .contentShape(Rectangle())
                    .gesture(SpatialTapGesture(count: 2).onEnded { ed.addCaption(at: tAt($0.location.x + 2)) }
                        .exclusively(before: SpatialTapGesture().onEnded { ed.selected = nil; ed.picking = false; ed.seek(tAt($0.location.x + 2)) }))
                    .offset(x: 2, y: capY)
                ForEach(ed.project.captions) { c in
                    CaptionBlock(ed: ed, cap: c, w: w, d: d).frame(width: max(6, x(c.end) - x(c.start)), height: capH)
                        .offset(x: x(c.start), y: capY)
                }
                // 遮挡轨、标注轨：每个一块，可以叠在一起
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: redH)
                    .offset(x: 2, y: redY).allowsHitTesting(false)
                let rp = OverlayBlock.pack(ed.project.redactions.map { ($0.id, $0.start, $0.end) })
                ForEach(ed.project.redactions) { r in
                    let row = rp[r.id]?.0 ?? 0
                    OverlayBlock(ed: ed, id: r.id, lane: .redaction, w: w, d: d).frame(width: max(6, x(r.end) - x(r.start)), height: TimelineLanes.row)
                        .offset(x: x(r.start), y: redY + Double(row) * (TimelineLanes.row + TimelineLanes.gap))
                }
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: annH)
                    .offset(x: 2, y: annY).allowsHitTesting(false)
                let ap = OverlayBlock.pack(ed.project.annotations.map { ($0.id, $0.start, $0.end) })
                ForEach(ed.project.annotations) { a in
                    let row = ap[a.id]?.0 ?? 0
                    OverlayBlock(ed: ed, id: a.id, lane: .annotation, w: w, d: d).frame(width: max(6, x(a.end) - x(a.start)), height: TimelineLanes.row)
                        .offset(x: x(a.start), y: annY + Double(row) * (TimelineLanes.row + TimelineLanes.gap))
                }
                // 音乐轨：每段一块（带波形）；开了循环时最后一段之后淡色画出「一直重复到片尾」
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: musH)
                    .overlay(Text(ed.project.music.clips.isEmpty ? L("音乐（在「声音」里选一首）", "Music (choose one under Sound)") : "")
                        .font(.system(size: 9)).foregroundStyle(.secondary).padding(.leading, 8), alignment: .leading)
                    .offset(x: 2, y: musY).allowsHitTesting(false)
                let fe = m.out(ed.project.trimEnd)
                ForEach(ed.project.music.occurrences(filmEnd: fe).filter { $0.at > $0.clip.start + 0.001 }, id: \.at) { o in
                    let a = x(m.src(o.at)), b = x(m.src(o.at + o.len))
                    RoundedRectangle(cornerRadius: 5).fill(MusicBlock.tint.opacity(0.28))
                        .overlay(Image(systemName: "repeat").font(.system(size: 9, weight: .bold)).foregroundStyle(.white.opacity(0.9)).padding(.leading, 6), alignment: .leading)
                        .frame(width: max(4, b - a), height: musH).offset(x: a, y: musY).allowsHitTesting(false)
                }
                ForEach(ed.project.music.clips) { c in
                    let a = x(m.src(c.start)), b = x(m.src(min(c.start + c.length, max(fe, c.start + 0.05))))
                    MusicBlock(ed: ed, clip: c, w: w, d: d, peaks: ed.voicePeaks[c.file] ?? []).frame(width: max(6, b - a), height: musH)
                        .offset(x: a, y: musY)
                }
                // 布局轨（有摄像头才有）：每段一块；空白处双击新加一段
                if ed.hasCamera {
                    RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.07)).frame(width: w - 4, height: layH)
                        .overlay(Text(L("小窗", "Bubble")).font(.system(size: 9)).foregroundStyle(.secondary).padding(.leading, 8), alignment: .leading)
                        .contentShape(Rectangle())
                        .gesture(SpatialTapGesture(count: 2).onEnded { ed.addLayout(at: tAt($0.location.x + 2)) }
                            .exclusively(before: SpatialTapGesture().onEnded { ed.selected = nil; ed.picking = false; ed.seek(tAt($0.location.x + 2)) }))
                        .offset(x: 2, y: layY)
                    ForEach(ed.project.layouts) { g in
                        LayoutBlock(ed: ed, seg: g, w: w, d: d).frame(width: max(6, x(g.end) - x(g.start)), height: layH)
                            .offset(x: x(g.start), y: layY)
                    }
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
                        // 时间轴按可见时间排（删掉的段合拢）：把拖动距离加在可见时间上，再换回素材时间
                        let raw = ed.srcTime(vis: ed.visTime(isStart ? p.trimStart : p.trimEnd) + v.translation.width / w * d)
                        let t = isStart ? min(max(0, raw), p.trimEnd - 0.5) : max(min(ed.duration, raw), p.trimStart + 0.5)
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

// 剪辑轨上的一段：拖中间挪、拖两头改范围，单击选中
// 布局轨上的一段：颜色按布局分，拖中间挪、拖两头改长短
struct LayoutBlock: View {
    @ObservedObject var ed: StudioEditor
    let seg: CameraLayoutSeg
    let w: Double, d: Double
    private let minLen = 0.3

    var body: some View {
        let sel = ed.selected == seg.id
        let base: Color = switch seg.mode {
        case .pip: Color(red: 0.55, green: 0.55, blue: 0.62)
        case .split, .splitSwap: Color(red: 0.16, green: 0.62, blue: 0.56)
        case .full: Color(red: 0.93, green: 0.55, blue: 0.18)
        case .hidden: Color(red: 0.40, green: 0.40, blue: 0.45)
        }
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(base.opacity(sel ? 0.95 : 0.78))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.clear, lineWidth: 2)
            HStack(spacing: 3) {
                Image(systemName: CameraLayoutSeg.icon(seg.mode)).font(.system(size: 9, weight: .bold))
                Text(CameraLayoutSeg.name(seg.mode)).font(.system(size: 10, weight: .semibold))
            }.foregroundStyle(.white).lineLimit(1).padding(.horizontal, 8)
            HStack { edge(isStart: true); Spacer(minLength: 0); edge(isStart: false) }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = seg.id; ed.picking = false }
        .contextMenu { Button(L("删除这段布局", "Delete Layout Part"), role: .destructive) { ed.selected = seg.id; ed.deleteSelected() } }
        .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
            .onChanged { v in ed.selected = seg.id; ed.dragBegin(); moveBy(v.translation.width / w * d) }
            .onEnded { _ in ed.dragEnd() })
        .help(L("这段时间摄像头的摆法；选中后在上面改", "How the camera is laid out here; select it to change"))
    }
    private func moveBy(_ dt: Double) {
        ed.dragUpdate { p in
            guard let i = p.layouts.firstIndex(where: { $0.id == seg.id }) else { return }
            let b = p.layouts[i], r = ed.layoutRoom(for: seg.id, in: p), len = b.end - b.start
            let s = min(max(b.start + dt, r.lowerBound), r.upperBound - len)
            p.layouts[i].start = s; p.layouts[i].end = s + len
        }
    }
    private func resize(_ isStart: Bool, _ dt: Double) {
        ed.dragUpdate { p in
            guard let i = p.layouts.firstIndex(where: { $0.id == seg.id }) else { return }
            let r = ed.layoutRoom(for: seg.id, in: p)
            if isStart { p.layouts[i].start = min(max(p.layouts[i].start + dt, r.lowerBound), p.layouts[i].end - minLen) }
            else { p.layouts[i].end = max(min(p.layouts[i].end + dt, r.upperBound), p.layouts[i].start + minLen) }
        }
    }
    private func edge(isStart: Bool) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8)
            .overlay(Capsule().fill(Color.white.opacity(0.9)).frame(width: 2.5, height: 9))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { ed.selected = seg.id; ed.picking = false }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
                .onChanged { v in ed.selected = seg.id; ed.dragBegin(); resize(isStart, v.translation.width / w * d) }
                .onEnded { _ in ed.dragEnd() })
    }
}

struct CutBlock: View {
    @ObservedObject var ed: StudioEditor
    let cut: CutSegment
    let w: Double, d: Double
    private let minLen = 0.2

    var body: some View {
        let sel = ed.selected == cut.id
        let base = cut.mode == .cut ? Color(red: 0.92, green: 0.27, blue: 0.27) : Color(red: 0.20, green: 0.52, blue: 0.96)
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(base.opacity(sel ? 0.95 : 0.75))
            // 斜纹
            Canvas { ctx, size in
                var p = Path()
                var x = -size.height
                while x < size.width { p.move(to: CGPoint(x: x, y: size.height)); p.addLine(to: CGPoint(x: x + size.height, y: 0)); x += 8 }
                ctx.stroke(p, with: .color(.white.opacity(0.28)), lineWidth: 2.5)
            }
            .clipShape(RoundedRectangle(cornerRadius: 5))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.clear, lineWidth: 2)
            HStack(spacing: 3) {
                Image(systemName: cut.mode == .cut ? "scissors" : "forward.fill").font(.system(size: 9, weight: .bold))
                if cut.mode == .speed { Text(String(format: "%.0f×", cut.rate)).font(.system(size: 10, weight: .bold).monospacedDigit()) }
            }
            .foregroundStyle(.white).lineLimit(1).padding(.horizontal, 8)
            HStack {
                edge(isStart: true)
                Spacer(minLength: 0)
                edge(isStart: false)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = cut.id; ed.picking = false }
        .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
            .onChanged { v in
                ed.selected = cut.id
                ed.dragBegin()
                let dt = v.translation.width / w * d
                ed.dragUpdate { p in
                    guard let i = p.cuts.firstIndex(where: { $0.id == cut.id }) else { return }
                    let b = p.cuts[i], r = ed.cutRoom(for: cut.id, in: p), len = b.end - b.start
                    let s = min(max(b.start + dt, r.lowerBound), r.upperBound - len)
                    p.cuts[i].start = s; p.cuts[i].end = s + len
                }
            }
            .onEnded { _ in ed.dragEnd() })
        .help(cut.mode == .cut ? L("这段会被剪掉，预览时直接跳过", "This part is cut out; the preview skips it")
                               : L("这段会快放", "This part plays sped up"))
    }

    private func edge(isStart: Bool) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8)
            .overlay(Capsule().fill(Color.white.opacity(0.9)).frame(width: 2.5, height: 10))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { ed.selected = cut.id; ed.picking = false }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
                .onChanged { v in
                    ed.selected = cut.id
                    ed.dragBegin()
                    let dt = v.translation.width / w * d
                    ed.dragUpdate { p in
                        guard let i = p.cuts.firstIndex(where: { $0.id == cut.id }) else { return }
                        let r = ed.cutRoom(for: cut.id, in: p)
                        if isStart { p.cuts[i].start = min(max(p.cuts[i].start + dt, r.lowerBound), p.cuts[i].end - minLen) }
                        else { p.cuts[i].end = max(min(p.cuts[i].end + dt, r.upperBound), p.cuts[i].start + minLen) }
                    }
                }
                .onEnded { _ in ed.dragEnd() })
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
        .contextMenu {
            Menu(L("放大", "Zoom")) {
                ForEach([1.5, 2.0, 2.5, 3.0], id: \.self) { z in
                    Button(String(format: "%.1f×", z)) { if let i = ed.index(seg.id) { ed.edit { $0.segments[i].zoom = z } } }
                }
            }
            Button(seg.mode == .follow ? L("改成固定位置", "Fixed Position") : L("改成跟着鼠标", "Follow Cursor")) {
                if let i = ed.index(seg.id) { ed.edit { $0.segments[i].mode = seg.mode == .follow ? .fixed : .follow } }
            }
            Divider()
            Button(L("删除这段推近", "Delete Zoom"), role: .destructive) { ed.selected = seg.id; ed.deleteSelected() }
        }
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

// 讲解轨上的一段：拖中间挪、拖两头剪掉讲解的开头 / 结尾（按成片时间换算），单击选中
struct VoiceBlock: View {
    @ObservedObject var ed: StudioEditor
    let clip: VoiceClip
    let w: Double, d: Double
    let peaks: [Float]
    private let minLen = 0.2
    private let tint = Color(red: 0.13, green: 0.62, blue: 0.52)

    var body: some View {
        let sel = ed.selected == clip.id
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(tint.opacity(sel ? 0.95 : 0.7))
            // 波形：只画用到的那一截（trimIn…trimOut）
            Canvas { ctx, size in
                guard !peaks.isEmpty else { return }
                let a = Int(clip.trimIn * 100), b = min(peaks.count, Int(clip.trimOut * 100))
                guard b > a else { return }
                let n = Int(size.width / 2), mid = size.height / 2
                var p = Path()
                for k in 0..<max(1, n) {
                    let i0 = a + (b - a) * k / max(1, n), i1 = max(i0 + 1, a + (b - a) * (k + 1) / max(1, n))
                    let v = Double(peaks[i0..<min(b, i1)].max() ?? 0)
                    let hh = max(0.5, min(1, v * 1.6) * (size.height / 2 - 3))
                    p.addRect(CGRect(x: Double(k) * 2, y: mid - hh, width: 1.2, height: hh * 2))
                }
                ctx.fill(p, with: .color(.white.opacity(0.75)))
            }
            .clipShape(RoundedRectangle(cornerRadius: 5))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.clear, lineWidth: 2)
            HStack(spacing: 3) {
                HStack(spacing: 2) {
                    Image(systemName: "mic.fill").font(.system(size: 9, weight: .bold))
                    if abs(clip.volume - 1) > 0.01 { Text("\(Int((clip.volume * 100).rounded()))%").font(.system(size: 9, weight: .bold).monospacedDigit()) }
                }
                .padding(.horizontal, 4).padding(.vertical, 1).background(Capsule().fill(tint))   // 压在波形上也看得清
                Spacer(minLength: 0)
            }
            .foregroundStyle(.white).lineLimit(1).padding(.horizontal, 10)
            HStack {
                edge(isStart: true)
                Spacer(minLength: 0)
                edge(isStart: false)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = clip.id; ed.picking = false }
        .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
            .onChanged { v in
                ed.selected = clip.id
                ed.dragBegin()
                let dt = v.translation.width / w * d
                ed.dragUpdate { p in
                    guard let i = p.voiceovers.firstIndex(where: { $0.id == clip.id }) else { return }
                    p.voiceovers[i].start = VoiceDrag.move(clip, dt: dt, duration: ed.duration)
                }
            }
            .onEnded { _ in ed.dragEnd() })
        .help(L("讲解：拖动挪位置，拖两头剪掉不要的开头结尾", "Voiceover: drag to move, drag the ends to trim"))
        .contextMenu {
            Button(L("删除这段讲解", "Delete Voiceover"), role: .destructive) { ed.selected = clip.id; ed.deleteSelected() }
        }
    }

    private func edge(isStart: Bool) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8)
            .overlay(Capsule().fill(Color.white.opacity(0.9)).frame(width: 2.5, height: 12))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { ed.selected = clip.id; ed.picking = false }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
                .onChanged { v in
                    ed.selected = clip.id
                    ed.dragBegin()
                    let dt = v.translation.width / w * d
                    ed.dragUpdate { p in
                        guard let i = p.voiceovers.firstIndex(where: { $0.id == clip.id }) else { return }
                        p.voiceovers[i] = VoiceDrag.trim(p.voiceovers[i], isStart: isStart, dt: dt, map: TimeMap(p), minLen: minLen)
                    }
                }
                .onEnded { _ in ed.dragEnd() })
    }
}

// 字幕轨上的一句：显示文字；拖中间挪、拖两头改起止（不压到前后两句），单击选中、双击改字
struct CaptionBlock: View {
    @ObservedObject var ed: StudioEditor
    let cap: Caption
    let w: Double, d: Double
    private let minLen = 0.3
    private let tint = Color(red: 0.96, green: 0.62, blue: 0.12)

    var body: some View {
        let sel = ed.selected == cap.id
        ZStack(alignment: .leading) {
            RoundedRectangle(cornerRadius: 5).fill(tint.opacity(sel ? 0.95 : 0.72))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.clear, lineWidth: 2)
            if ed.editingCaption == cap.id {
                // 双击 / 新加：直接在黄色块里打字（像剪映），回车或点别处完成，Esc 收起
                CaptionField(ed: ed, cap: cap, inBlock: true).padding(.horizontal, 9)
            } else {
                Text(cap.text.isEmpty ? L("双击写字幕", "Double-click to type") : cap.text)
                    .font(.system(size: 10.5, weight: .semibold)).foregroundStyle(.white.opacity(cap.text.isEmpty ? 0.7 : 1))
                    .lineLimit(1).truncationMode(.tail).padding(.horizontal, 9)
            }
            HStack {
                edge(isStart: true)
                Spacer(minLength: 0)
                edge(isStart: false)
            }
        }
        .clipped()
        .contentShape(Rectangle())
        .gesture(TapGesture(count: 2).onEnded { ed.selected = cap.id; ed.editingCaption = cap.id }
            .exclusively(before: TapGesture().onEnded { ed.selected = cap.id; ed.picking = false }))
        .contextMenu {
            Button(L("改字", "Edit Text")) { ed.selected = cap.id; ed.editingCaption = cap.id }
            Button(L("念成配音", "Speak as Voiceover")) { ed.speakCaptions([cap.id]) }.disabled(cap.text.isEmpty || ed.speaking != nil)
            Button(L("全部字幕念成配音", "Speak All Captions")) { ed.speakCaptions() }.disabled(ed.speaking != nil)
            Divider()
            Button(L("在播放头拆分", "Split at Playhead")) { ed.splitCaption(cap.id, at: ed.time) }.disabled(!(ed.time > cap.start + 0.15 && ed.time < cap.end - 0.15))
            Button(L("和下一句合并", "Merge with Next")) { ed.mergeCaption(cap.id) }
            Divider()
            Button(L("删除", "Delete"), role: .destructive) { ed.selected = cap.id; ed.deleteSelected() }
        }
        .simultaneousGesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
            .onChanged { v in
                ed.selected = cap.id
                ed.dragBegin()
                let dt = v.translation.width / w * d
                ed.dragUpdate { p in
                    guard let i = p.captions.firstIndex(where: { $0.id == cap.id }) else { return }
                    let b = p.captions[i], r = ed.captionRoom(for: cap.id, in: p), len = b.end - b.start
                    let s = min(max(b.start + dt, r.lowerBound), r.upperBound - len)
                    p.captions[i].start = s; p.captions[i].end = s + len
                }
            }
            .onEnded { _ in ed.dragEnd() })
        .help(cap.text.isEmpty ? L("空白字幕：双击写字", "Empty caption: double-click to type") : cap.text)
    }

    private func edge(isStart: Bool) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8)
            .overlay(Capsule().fill(Color.white.opacity(0.9)).frame(width: 2.5, height: 12))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { ed.selected = cap.id; ed.picking = false }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
                .onChanged { v in
                    ed.selected = cap.id
                    ed.dragBegin()
                    let dt = v.translation.width / w * d
                    ed.dragUpdate { p in
                        guard let i = p.captions.firstIndex(where: { $0.id == cap.id }) else { return }
                        p.captions[i] = CaptionDrag.resize(p.captions[i], isStart: isStart, dt: dt, room: ed.captionRoom(for: cap.id, in: p), minLen: minLen)
                    }
                }
                .onEnded { _ in ed.dragEnd() })
    }
}

// 拖字幕两头（纯函数，好测）：按「按下时的值 + 拖了多远」，夹在前后两句之间
enum CaptionDrag {
    static func resize(_ c: Caption, isStart: Bool, dt: Double, room r: ClosedRange<Double>, minLen: Double) -> Caption {
        var n = c
        if isStart { n.start = min(max(c.start + dt, r.lowerBound), c.end - minLen) }
        else { n.end = max(min(c.end + dt, r.upperBound), c.start + minLen) }
        return n
    }
}

// 拖讲解（纯函数，好测）：dt 是时间轴上拖过的素材时间
enum VoiceDrag {
    static func move(_ c: VoiceClip, dt: Double, duration d: Double) -> Double { min(max(0, c.start + dt), max(0, d - 0.05)) }
    // 拖开头：讲解前面那截不要了（锚点跟着往后挪）；拖结尾：后面那截不要了。按成片时间算，夹在录音文件范围里
    static func trim(_ c: VoiceClip, isStart: Bool, dt: Double, map m: TimeMap, minLen: Double) -> VoiceClip {
        var r = c
        if isStart {
            let newStart = max(0, c.start + dt)
            var dIn = m.out(newStart) - m.out(c.start)
            dIn = min(max(dIn, -c.trimIn), c.length - minLen)
            r.trimIn = c.trimIn + dIn
            r.start = m.src(m.out(c.start) + dIn)
        } else {
            let e = c.srcEnd(m) + dt
            let len = min(max(m.out(e) - m.out(c.start), minLen), c.fileLength - c.trimIn)
            r.trimOut = c.trimIn + len
        }
        return r
    }
}

// 录音音量条
struct LevelMeter: View {
    let level: Float
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.secondary.opacity(0.25))
                Capsule().fill(level > 0.9 ? Color.red : Color.green).frame(width: g.size.width * Double(level))
            }
        }
    }
}

struct BarLabel: LabelStyle {
    let compact: Bool
    func makeBody(configuration: Configuration) -> some View {
        if compact { configuration.icon } else { HStack(spacing: 5) { configuration.icon; configuration.title } }
    }
}

// ---- 右边的「样式」面板：预设、画面比例、背景、画面、鼠标、运动模糊 ----
struct StylePanel: View {
    @ObservedObject var ed: StudioEditor
    @State private var naming = false
    @State private var newName = ""
    @AppStorage("voiceCountdown") private var voiceCountdown = true
    @AppStorage("voiceMuteOriginal") private var voiceMuteOriginal = true
    @State private var open: Set<String> = ProcessInfo.processInfo.environment["EDITOR_OPEN"].map { Set($0.split(separator: ",").map(String.init)) }
        ?? ["preset", "aspect", "bg", "frame", "zoom", "cursor", "keys", "audio", "music", "camera", "caption", "blur"]

    var body: some View {
        ScrollViewReader { sv in
        ScrollView {
            VStack(alignment: .leading, spacing: 4) {
                section("preset", L("预设", "Presets")) { presetsBox }
                section("aspect", L("画面比例", "Aspect Ratio")) { aspectBox }
                section("bg", L("背景", "Background")) { backgroundBox }
                section("frame", L("画面", "Frame")) { frameBox }
                section("zoom", L("推近", "Zoom")) { ZoomSpeedBox(ed: ed) }
                section("cursor", L("鼠标", "Cursor")) { cursorBox }
                section("keys", L("快捷键", "Keystrokes")) { keysBox }
                section("audio", L("声音", "Sound")) { audioBox }
                section("music", L("背景音乐", "Music")) { MusicBox(ed: ed) }
                if ed.hasCamera { section("camera", L("摄像头", "Camera")) { cameraBox }.id("camera") }
                section("caption", L("字幕", "Captions")) { captionBox }
                section("blur", L("运动模糊", "Motion Blur")) { blurBox }
            }
            .frame(width: 270, alignment: .leading)   // 面板 300 宽，左右各留 15
            .padding(.horizontal, 15).padding(.vertical, 12)
        }
        // 在预览上点了摄像头小窗：展开「摄像头」设置并滚过去（形状、大小在那里改）
        .onChange(of: ed.revealSection) { _, k in
            guard let k else { return }
            open.insert(k)
            withAnimation(.easeInOut(duration: 0.25)) { sv.scrollTo(k, anchor: .top) }
            ed.revealSection = nil
        }
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
        VStack(alignment: .leading, spacing: 8) {
            chips(ed.look(\.aspect), [(.original, L("原始", "Original")), (.wide, "16:9"), (.tall, "9:16"), (.square, "1:1"), (.classic, "4:3"), (.custom, L("自定义", "Custom"))], columns: 3)
            if ed.project.look.aspect == .custom {
                // 自己填宽 : 高，或点常用的
                HStack(spacing: 6) {
                    TextField("", value: ed.look(\.aspectW), format: .number.precision(.fractionLength(0...2))).frame(width: 52).multilineTextAlignment(.center)
                    Text(":").foregroundStyle(.secondary)
                    TextField("", value: ed.look(\.aspectH), format: .number.precision(.fractionLength(0...2))).frame(width: 52).multilineTextAlignment(.center)
                    Spacer(minLength: 0)
                    Menu(L("常用", "Common")) {
                        ForEach([(21.0, 9.0), (2.35, 1.0), (3.0, 4.0), (4.0, 5.0), (3.0, 2.0), (2.0, 1.0)], id: \.0) { w, h in
                            Button("\(w.formatted()) : \(h.formatted())") { ed.edit { $0.look.aspectW = w; $0.look.aspectH = h } }
                        }
                    }.fixedSize()
                }.textFieldStyle(.roundedBorder).controlSize(.small)
                Text(L("宽和高的比例，比如 21 : 9、4 : 5", "Width : height, e.g. 21 : 9 or 4 : 5")).font(.caption).foregroundStyle(.secondary)
            }
        }
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
            slider(L("圆角", "Corners"), \.cornerRadius, 0...100) { "\(Int($0.rounded()))" }
            slider(L("阴影", "Shadow"), \.shadow, 0...1) { "\(Int(($0 * 100).rounded()))%" }
            slider(L("阴影虚化", "Softness"), \.shadowBlur, 0...1) { "\(Int(($0 * 100).rounded()))%" }
        }
    }

    // 显示快捷键：开关、位置、大小、深浅
    // 摄像头：只有录的时候开了摄像头的素材才有这一组
    private var cameraBox: some View {
        let c = ed.project.camera ?? CameraSettings()
        return VStack(alignment: .leading, spacing: 8) {
            Toggle(L("显示摄像头", "Show camera"), isOn: ed.camSet(\.show))
            if c.show {
                chips(ed.camSet(\.round), [(true, L("圆形", "Circle")), (false, L("圆角方形", "Rounded"))], columns: 2)
                camSlider(L("大小", "Size"), \.size, 0.1...0.5) { "\(Int(($0 * 100).rounded()))%" }
                Text(L("位置：在预览上直接拖动小窗，或点四个角", "Position: drag the bubble in the preview, or pick a corner"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 4) {
                    ForEach(Array([(0.14, 0.2, "arrow.up.left"), (0.86, 0.2, "arrow.up.right"), (0.14, 0.8, "arrow.down.left"), (0.86, 0.8, "arrow.down.right")].enumerated()), id: \.offset) { _, v in
                        Button { ed.edit { var k = $0.camera ?? CameraSettings(); k.x = v.0; k.y = v.1; $0.camera = k } } label: {
                            Image(systemName: v.2).frame(maxWidth: .infinity)
                        }.controlSize(.small)
                    }
                }
                camSlider(L("边框", "Border"), \.border, 0...12) { "\(Int($0.rounded()))" }
                if c.border > 0.3 {
                    ColorPicker(L("边框颜色", "Border color"), selection: Binding(get: { c.borderColor.color }, set: { v in ed.edit { var k = $0.camera ?? CameraSettings(); k.borderColor = RGB(v); $0.camera = k } }))
                }
                camSlider(L("阴影", "Shadow"), \.shadow, 0...1) { "\(Int(($0 * 100).rounded()))%" }
                Toggle(L("左右镜像", "Mirror"), isOn: ed.camSet(\.mirror))
                Text(L("时间轴最下面的「布局」轨：双击加一段，换成分屏、摄像头全屏或隐藏", "The Layout lane at the bottom of the timeline: double-click to add a part, then switch it to side-by-side, camera full or hidden"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }.toggleStyle(.checkbox).font(.callout)
    }
    private func camSlider(_ title: String, _ kp: WritableKeyPath<CameraSettings, Double>, _ range: ClosedRange<Double>, _ fmt: @escaping (Double) -> String) -> some View {
        HStack(spacing: 8) {
            Text(title).font(.callout).frame(width: 62, alignment: .leading)
            Slider(value: ed.camSlider(kp), in: range, onEditingChanged: { if !$0 { ed.dragEnd() } }).controlSize(.small)
            Text(fmt((ed.project.camera ?? CameraSettings())[keyPath: kp])).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 40, alignment: .trailing)
        }
    }

    private var keysBox: some View {
        let look = ed.project.look
        return VStack(alignment: .leading, spacing: 8) {
            Toggle(L("显示按的快捷键", "Show keystrokes"), isOn: ed.look(\.showKeys))
                .help(L("画面上浮出 ⌘ ⇧ C 这样的键帽，约 1.2 秒后淡出；只显示带 ⌘⌃⌥ 的组合和 Esc、方向键这类功能键，打的字不会显示",
                        "Shows key caps like ⌘ ⇧ C for about 1.2 s; only combos with ⌘⌃⌥ and keys like Esc or arrows — typed text is never shown"))
            if !ed.hasShortcuts {
                Text(L("这段录制没有记到快捷键。新录的才会记；给录屏开「输入监控」权限会记得更全", "No keystrokes were recorded in this clip. New recordings capture them; allowing Input Monitoring makes it more complete"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            if look.showKeys {
                chips(ed.look(\.keysAt), [(.bottomLeft, L("左下", "Left")), (.bottom, L("底部居中", "Center")), (.bottomRight, L("右下", "Right"))], columns: 3)
                slider(L("大小", "Size"), \.keysSize, 0.03...0.08) { "\(Int(($0 * 1000).rounded()))" }
                chips(ed.look(\.keysDark), [(true, L("深色", "Dark")), (false, L("浅色", "Light"))], columns: 2)
            }
        }.toggleStyle(.checkbox).font(.callout)
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
                .help(L("每次点击时在鼠标处冒一下，让观众看清点了哪里", "A little effect at each click so viewers see where you clicked"))
            if look.ripple {
                chips(ed.look(\.rippleStyle), [(.ring, L("圆圈", "Ring")), (.glass, L("玻璃", "Glass")), (.double, L("双圈", "Double")),
                                               (.pulse, L("圆点", "Dot")), (.glow, L("光晕", "Glow")), (.focus, L("对焦", "Focus"))], columns: 3)
                if [.ring, .double, .focus].contains(look.rippleStyle) {
                    slider(L("线粗", "Thickness"), \.rippleWidth, 0.4...3) { String(format: "%.1f×", $0) }
                }
            }
            Toggle(L("点击音效", "Click sound"), isOn: ed.look(\.clickSound))
                .help(L("每次点击处加一声轻响，预览和导出都有；音量在「声音」里调", "Adds a soft click at each click, in the preview and the export; set its volume under Sound"))
            if look.clickSound {
                HStack(spacing: 6) {
                    Picker("", selection: Binding(get: { look.clickStyle }, set: { v in ed.look(\.clickStyle).wrappedValue = v; ClickPreview.play(v) })) {
                        ForEach(StudioLook.Click.allCases, id: \.self) { Text(ClickPreview.name($0)).tag($0) }
                    }.labelsHidden().frame(maxWidth: .infinity, alignment: .leading)
                    Button { ClickPreview.play(look.clickStyle) } label: { Image(systemName: "speaker.wave.2.fill") }
                        .help(L("试听", "Preview sound"))
                }.controlSize(.small)
            }
            Toggle(L("打字声", "Typing sound"), isOn: Binding(get: { look.typeSound }, set: { v in ed.look(\.typeSound).wrappedValue = v; if v { ClickPreview.playTyping(look.typeStyle) } }))
                .help(L("录制时每按一下键盘，成片里加一声键盘声，预览和导出都有；音量和点击声一起在「声音」里调",
                        "Adds a keyboard sound for every key you pressed while recording, in the preview and the export; volume is under Sound with the click sound"))
            if look.typeSound {
                HStack(spacing: 6) {
                    Picker("", selection: Binding(get: { look.typeStyle }, set: { v in ed.look(\.typeStyle).wrappedValue = v; ClickPreview.playTyping(v) })) {
                        ForEach(StudioLook.Typing.allCases, id: \.self) { Text(ClickSound.typingName($0)).tag($0) }
                    }.labelsHidden().frame(maxWidth: .infinity, alignment: .leading)
                    Button { ClickPreview.playTyping(look.typeStyle) } label: { Image(systemName: "speaker.wave.2.fill") }.help(L("试听", "Preview sound"))
                }.controlSize(.small)
            }
            Toggle(L("结尾鼠标回到起点", "Return cursor to start at the end"), isOn: ed.look(\.cursorLoop))
                .help(L("最后一小段鼠标慢慢走回第一帧的位置，首尾接得上，适合做循环播放的视频", "In the last moment the cursor glides back to where it started, so the video loops seamlessly"))
            if look.cursorLoop {
                slider(L("回去用时", "Duration"), \.cursorLoopSec, 0.5...2) { String(format: L("%.1f 秒", "%.1fs"), $0) }
                Toggle(L("镜头也拉回全景", "Also zoom back out"), isOn: ed.look(\.loopZoomOut))
            }
        }.toggleStyle(.checkbox).font(.callout)
    }

    // 声音：录到的电脑声音 / 麦克风各自的音量、静音，点击声音量
    private var audioBox: some View {
        let a = ed.project.audio
        func pct(_ v: Double) -> String { v < 0.005 ? L("静音", "Off") : "\(Int((v * 100).rounded()))%" }
        return VStack(alignment: .leading, spacing: 8) {
            if ed.audioChannels.isEmpty {
                Text(L("这段录制没有录到声音", "This recording has no sound")).font(.caption).foregroundStyle(.secondary)
            } else {
                Toggle(L("录到的声音静音", "Mute recorded sound"), isOn: ed.audioToggle(\.muted))
                if ed.hasSystemAudio { audioSlider(L("电脑声音", "Computer"), \.system, pct).disabled(a.muted).opacity(a.muted ? 0.45 : 1) }
                if ed.hasMicAudio { audioSlider(L("麦克风", "Microphone"), \.mic, pct).disabled(a.muted).opacity(a.muted ? 0.45 : 1) }
            }
            if ed.hasMicAudio || !ed.project.voiceovers.isEmpty || (ed.hasSystemAudio && a.denoiseSystem) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(L("降噪", "Noise reduction")).font(.callout)
                    chips(ed.audioPick(\.denoise), [(.off, L("关", "Off")), (.light, L("轻", "Light")), (.medium, L("中", "Medium")), (.strong, L("强", "Strong"))], columns: 4)
                }
                .help(L("去掉键盘声、风扇、空调这类杂音，只留人声（麦克风和讲解；系统自带的声音隔离，在本机处理）",
                        "Removes keyboard, fan and room noise and keeps your voice (microphone and voiceover; Apple's built-in voice isolation, on this Mac)"))
            }
            if ed.hasSystemAudio && a.denoise != .off {
                Toggle(L("电脑声音也降噪", "Also denoise computer sound"), isOn: ed.audioToggle(\.denoiseSystem))
                    .help(L("默认不处理电脑声音：里面的音乐、视频声音会被当成杂音削掉", "Off by default: music and video sound would be treated as noise"))
            }
            if let j = ed.denoiseJob {
                HStack(spacing: 6) {
                    ProgressView(value: j).controlSize(.small)
                    Text(L("正在降噪…", "Denoising…")).font(.caption).foregroundStyle(.secondary)
                }
            }
            if let e = ed.denoiseError { Text(e).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            if ed.project.look.clickSound {
                audioSlider(L("点击声", "Clicks"), \.clicks, pct)
            }
            if !ed.project.voiceovers.isEmpty {
                audioSlider(L("讲解", "Voiceover"), \.voice, pct)
                Toggle(L("讲解时自动压低原声音", "Lower recorded sound under voiceover"), isOn: ed.audioToggle(\.duck))
                    .help(L("有讲解的地方，原来录到的声音压低到约四分之一（-12 dB），讲解更清楚", "Where there's narration, the recorded sound drops to about a quarter (-12 dB) so your voice is clear"))
            }
            Toggle(L("录讲解前倒数 3 秒", "Count down 3 s before voiceover"), isOn: $voiceCountdown)
            Toggle(L("录讲解时原声音静音", "Mute recorded sound while recording voiceover"), isOn: $voiceMuteOriginal)
                .help(L("录的时候不放原来的声音，免得被麦克风录进去（播放和导出不受影响）", "Silences the recorded sound while you narrate so the mic doesn't pick it up (playback and export are unaffected)"))
            Text(L("最大 200%，太响会自动压住、不会爆音", "Up to 200%; loud parts are softened so they don't distort"))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }.toggleStyle(.checkbox).font(.callout)
    }
    private func audioSlider(_ title: String, _ kp: WritableKeyPath<AudioSettings, Double>, _ fmt: @escaping (Double) -> String) -> some View {
        HStack(spacing: 8) {
            Text(title).font(.callout).frame(width: 62, alignment: .leading)
            Slider(value: ed.audioSlider(kp), in: 0...2, onEditingChanged: { if !$0 { ed.dragEnd() } }).controlSize(.small)
            Text(fmt(ed.project.audio[keyPath: kp])).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 40, alignment: .trailing)
        }
    }

    // 字幕样式：显示开关、粗细、字号、颜色、描边阴影、底色条、位置
    private var captionBox: some View {
        let cs = ed.project.captionStyle
        let colors: [(RGB, String)] = [(RGB(1, 1, 1), L("白", "White")), (RGB(1, 0.86, 0.2), L("黄", "Yellow")), (RGB(0.1, 0.1, 0.1), L("黑", "Black"))]
        return VStack(alignment: .leading, spacing: 8) {
            Toggle(L("显示字幕", "Show captions"), isOn: ed.capStyle(\.show))
            if ed.project.captions.isEmpty {
                Text(L("还没有字幕：点播放栏的「字幕」自动识别，或在时间轴最下面一条双击新加", "No captions yet: click Captions in the play bar to transcribe, or double-click the bottom lane to add one"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Group {
                chips(ed.capStyle(\.weight), [(.regular, L("常规", "Regular")), (.medium, L("中粗", "Medium")), (.semibold, L("粗", "Bold"))], columns: 3)
                capSlider(L("字号", "Size"), \.size, 0.03...0.09) { "\(Int(($0 * 1000).rounded()))" }
                HStack(spacing: 6) {
                    Text(L("颜色", "Color")).font(.callout).frame(width: 62, alignment: .leading)
                    ForEach(colors.indices, id: \.self) { k in
                        let (c, name) = colors[k], on = cs.color == c
                        Button { ed.capStyle(\.color).wrappedValue = c } label: {
                            Circle().fill(c.color).frame(width: 18, height: 18)
                                .overlay(Circle().stroke(on ? Color.accentColor : Color.secondary.opacity(0.4), lineWidth: on ? 2.5 : 1))
                        }.buttonStyle(.plain).help(name)
                    }
                    ColorPicker("", selection: Binding(get: { cs.color.color }, set: { ed.capStyle(\.color).wrappedValue = RGB($0) }), supportsOpacity: false).labelsHidden()
                }
                HStack(spacing: 14) {
                    Toggle(L("描边", "Outline"), isOn: ed.capStyle(\.outline))
                    Toggle(L("阴影", "Shadow"), isOn: ed.capStyle(\.shadow))
                }
                Text(L("底色条", "Background bar")).font(.caption).foregroundStyle(.secondary)
                chips(ed.capStyle(\.box), [(.none, L("无", "None")), (.dark, L("深色", "Dark")), (.light, L("浅色", "Light"))], columns: 3)
                Text(L("位置", "Position")).font(.caption).foregroundStyle(.secondary)
                chips(ed.capStyle(\.position), [(.bottom, L("底部", "Bottom")), (.top, L("顶部", "Top"))], columns: 2)
                capSlider(L("离边", "Margin"), \.margin, 0.02...0.25) { "\(Int(($0 * 100).rounded()))%" }
                Text(L("一次只显示一行；竖屏时字自动大一号，放底部时让开抖音等 App 底部的按钮", "One line at a time; in vertical video the text is a size bigger and, at the bottom, kept clear of TikTok-style buttons"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .disabled(!cs.show).opacity(cs.show ? 1 : 0.45)
        }.toggleStyle(.checkbox).font(.callout)
    }
    private func capSlider(_ title: String, _ kp: WritableKeyPath<CaptionStyle, Double>, _ range: ClosedRange<Double>, _ fmt: @escaping (Double) -> String) -> some View {
        HStack(spacing: 8) {
            Text(title).font(.callout).frame(width: 62, alignment: .leading)
            Slider(value: ed.capSlider(kp), in: range, onEditingChanged: { if !$0 { ed.dragEnd() } }).controlSize(.small)
            Text(fmt(ed.project.captionStyle[keyPath: kp])).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 40, alignment: .trailing)
        }
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
    // CUTTEST=1 → 剪掉 / 快放的时间换算（不碰界面）：素材 ↔ 成片来回换、剪掉段里的点击去掉、推近段跟着挪、旧工程读得进来
    @MainActor static func cutTest() -> Bool {
        guard ProcessInfo.processInfo.environment["CUTTEST"] != nil else { return false }
        var ok = true
        func check(_ n: String, _ c: Bool, _ info: String = "") { print(c ? "PASS" : "FAIL", n, info); ok = ok && c }
        func near(_ a: Double, _ b: Double) -> Bool { abs(a - b) < 1e-9 }
        var p = StudioProject(segments: [ZoomSegment(start: 1, end: 4), ZoomSegment(start: 5.5, end: 7), ZoomSegment(start: 12, end: 16)], trimStart: 0.5, trimEnd: 30)
        p.cuts = [CutSegment(start: 5, end: 8), CutSegment(start: 10, end: 14, mode: .speed, rate: 4)]
        let m = TimeMap(p)
        check("剪掉前不变", near(m.out(3), 3) && near(m.src(3), 3))
        check("剪掉段压成 0", near(m.out(5), 5) && near(m.out(6.5), 5) && near(m.out(8), 5))
        check("剪掉后往前挪 3 秒", near(m.out(9), 6) && near(m.src(6), 9))
        check("快放段 4 倍", near(m.out(12), 7 + 0.5) && near(m.src(7.5), 12) && near(m.out(14), 8))
        check("成片总长", near(m.out(30) - 0.5, 30 - 0.5 - 3 - 3), "\(m.out(30))")
        let probes = stride(from: 0.0, through: 24.0, by: 0.37).map { $0 }
        check("成片→素材→成片 来回一样", probes.allSatisfy { abs(m.out(m.src($0)) - $0) < 1e-9 })
        check("成片时间单调", zip(probes, probes.dropFirst()).allSatisfy { m.src($0.0) < m.src($0.1) })
        var tr = Track(width: 100, height: 100, pixelsPerPoint: 2)
        tr.clicks = [[3, 1, 1], [6, 1, 1], [12, 1, 1], [20, 1, 1]]; tr.keys = [7, 9]
        let wt = m.warp(tr)
        check("剪掉段里的点击 / 按键去掉", wt.clicks.map { $0[0] } == [3, 7.5, 14] && wt.keys == [6], "\(wt.clicks.map { $0[0] }) \(wt.keys)")
        let wp = m.warp(p)
        check("推近段跟着挪，整段被剪掉的去掉", wp.segments.count == 2 && near(wp.segments[1].start, 7.5) && near(wp.segments[1].end, 10) && near(wp.trimEnd, 24), "\(wp.segments.map { ($0.start, $0.end) })")
        check("没剪辑 = 原样", TimeMap(StudioProject(trimStart: 0, trimEnd: 10)).isIdentity)
        // 旧工程（没有 cuts / audio 字段）读得进来，存了再读一样
        let old = #"{"segments":[],"trimStart":0,"trimEnd":10}"#.data(using: .utf8)!
        let po = try? JSONDecoder().decode(StudioProject.self, from: old)
        check("旧工程兼容", po?.cuts == [] && po?.audio == AudioSettings())
        let rt = try? JSONDecoder().decode(StudioProject.self, from: JSONEncoder().encode(p))
        check("cuts 存了再读一样", rt?.cuts == p.cuts)
        print(ok ? "CUTTEST OK" : "CUTTEST FAIL")
        exit(ok ? 0 : 1)
    }

    // VOICETEST=素材夹（raw.mp4 的声音最好是一段持续的测试音，用来量压低）→ 讲解的规则 + 真导出：
    // 放置 / 覆盖、锚点跟着剪辑走、拖两头、压低曲线、旧工程兼容；再合成一段测试录音（2 kHz 短促音），导出后量出现的时刻、音量、压低
    @MainActor static func voiceTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["VOICETEST"] else { return false }
        Task { @MainActor in
            var ok = true
            func check(_ n: String, _ c: Bool, _ info: String = "") { print(c ? "PASS" : "FAIL", n, info); ok = ok && c }
            func near(_ a: Double, _ b: Double, _ e: Double = 1e-6) -> Bool { abs(a - b) < e }
            // ---- 规则 ----
            let id0 = StudioProject(trimStart: 0, trimEnd: 30), m0 = TimeMap(id0)
            let A = VoiceClip(file: "a", start: 2, trimIn: 0, trimOut: 4, fileLength: 4)
            func cl(_ s: Double, _ len: Double) -> VoiceClip { VoiceClip(file: "n", start: s, trimIn: 0, trimOut: len, fileLength: 10) }
            var r = VoiceClip.place(cl(3, 1), into: [A], map: m0)
            check("新录的在中间：旧的剪成两段", r.count == 3 && near(r[0].trimOut, 1) && near(r[2].start, 4) && near(r[2].trimIn, 2) && near(r[2].trimOut, 4) && r[0].id != r[2].id,
                  "\(r.map { ($0.file, $0.start, $0.trimIn, $0.trimOut) })")
            r = VoiceClip.place(cl(1, 2), into: [A], map: m0)
            check("盖住旧的开头", r.count == 2 && near(r[1].start, 3) && near(r[1].trimIn, 1) && r[1].id == A.id, "\(r.map { ($0.file, $0.start, $0.trimIn) })")
            r = VoiceClip.place(cl(5, 3), into: [A], map: m0)
            check("盖住旧的结尾", r.count == 2 && near(r[0].trimOut, 3) && r[0].id == A.id)
            r = VoiceClip.place(cl(1, 6), into: [A], map: m0)
            check("整段盖住：旧的去掉", r.count == 1 && r[0].file == "n")
            r = VoiceClip.place(cl(7, 1), into: [A], map: m0)
            check("不重叠：都留着", r.count == 2)
            var pc = StudioProject(trimStart: 0, trimEnd: 30)
            pc.cuts = [CutSegment(start: 4, end: 6), CutSegment(start: 11, end: 12)]
            let mc = TimeMap(pc), B = VoiceClip(file: "b", start: 10, trimIn: 0, trimOut: 3, fileLength: 3)
            check("讲解前面剪掉 2 秒：讲解跟着开头的画面往前挪", near(B.outRange(mc).start, 8) && near(B.outRange(mc).end, 11))
            check("讲解中间的画面被剪：讲解照样连着放（长度不变）、时间轴上块变长", near(B.outRange(mc).end - B.outRange(mc).start, 3) && near(B.srcEnd(mc), 14), "\(B.srcEnd(mc))")
            let t1 = VoiceDrag.trim(B, isStart: true, dt: 0.5, map: mc, minLen: 0.2)
            check("拖开头：去掉前面 0.5 秒", near(t1.trimIn, 0.5) && near(t1.start, 10.5) && near(t1.outRange(mc).end, 11))
            let t2 = VoiceDrag.trim(B, isStart: false, dt: -3.5, map: mc, minLen: 0.2)   // 14 → 10.5，跨过剪掉的 11–12：成片里只剩 0.5 秒
            check("拖结尾（跨过剪掉段）按成片时间算", near(t2.trimOut, 0.5), "\(t2.trimOut)")
            let t2b = VoiceDrag.trim(B, isStart: false, dt: -2.5, map: mc, minLen: 0.2)  // 停在剪掉段里：按剪掉段的起点算
            check("拖结尾停在剪掉段里", near(t2b.trimOut, 1.0), "\(t2b.trimOut)")
            let t3 = VoiceDrag.trim(B, isStart: false, dt: 9, map: mc, minLen: 0.2)
            check("拖结尾不超出录音长度", near(t3.trimOut, 3))
            let t4 = VoiceDrag.trim(B, isStart: true, dt: -5, map: mc, minLen: 0.2)
            check("拖开头不早于录音开头", near(t4.trimIn, 0) && near(t4.start, 10))
            let dm = VoiceMix.spansOnly([(2, 4)], duck: true)
            check("压低曲线", dm.duckGain(Int64(3 * 48000)) == VoiceMix.duckLevel && dm.duckGain(Int64(1 * 48000)) == 1 && dm.duckGain(Int64(5 * 48000)) == 1
                  && abs(dm.duckGain(Int64(1.925 * 48000)) - 0.625) < 0.02 && dm.duckGain(Int64(4.1 * 48000)) < 0.6 && dm.duckGain(Int64(4.1 * 48000)) > 0.25)
            check("不压低时恒为 1", VoiceMix.spansOnly([(2, 4)], duck: false).duckGain(Int64(3 * 48000)) == 1)
            let old = #"{"segments":[],"trimStart":0,"trimEnd":10,"audio":{"system":1,"mic":1,"clicks":1,"muted":false}}"#.data(using: .utf8)!
            let po = try? JSONDecoder().decode(StudioProject.self, from: old)
            check("旧工程兼容", po?.voiceovers == [] && po?.audio.voice == 1 && po?.audio.duck == true)
            var pr = id0; pr.voiceovers = [B]; pr.audio.duck = false; pr.audio.voice = 1.5
            let rt = try? JSONDecoder().decode(StudioProject.self, from: JSONEncoder().encode(pr))
            check("存了再读一样", rt?.voiceovers == pr.voiceovers && rt?.audio == pr.audio)

            // ---- 真导出 ----
            let dir = URL(fileURLWithPath: path)
            let vdir = dir.appendingPathComponent("voice"); try? FileManager.default.createDirectory(at: vdir, withIntermediateDirectories: true)
            let burstAt = [0.5, 1.5, 2.5, 3.5]
            do {
                try writeTestVoice(vdir.appendingPathComponent("t.m4a"), length: 4.0, bursts: burstAt)
            } catch { check("写测试录音", false, "\(error)"); print("VOICETEST FAIL"); exit(1) }
            guard let track = try? JSONDecoder().decode(Track.self, from: Data(contentsOf: dir.appendingPathComponent("track.json"))) else { print("VOICETEST FAIL no track"); exit(1) }
            let dur = (try? await AVURLAsset(url: dir.appendingPathComponent("raw.mp4")).load(.duration).seconds) ?? 0
            var base = StudioProject.load(dir, track: track, duration: dur)
            base.trimStart = 0; base.trimEnd = dur; base.cuts = []; base.look.clickSound = false
            base.look.background = .color; base.look.blur = .off
            var o = ExportOptions(); o.res = .sd; o.fps = 30; o.quality = .small
            let clip = VoiceClip(file: "voice/t.m4a", start: 3.0, trimIn: 0.2, trimOut: 4.0, fileLength: 4.0)
            func export(_ p: StudioProject, _ name: String) async -> [Float] {
                let out = URL(fileURLWithPath: "/tmp/voicetest-\(name).mp4")
                do {
                    let u = try await Studio.render(project: dir, to: out, style: StudioStyle(o), edits: p) { _ in }
                    return (try? await VoiceMix.decode(u)) ?? []
                } catch { print("export", name, error); return [] }
            }
            // 讲解（2 kHz、0.5 振幅、60 毫秒）的起点：安静 0.2 秒以上后第一次超过 0.12
            func onsets(_ a: [Float], from: Double = 0) -> [Double] {
                var res: [Double] = [], quiet = 0
                for (i, v) in a.enumerated() where Double(i) / 48000 >= from {
                    if abs(v) > 0.12 { if quiet > 9600 { res.append(Double(i) / 48000) }; quiet = 0 } else { quiet += 1 }
                }
                return res
            }
            func peak(_ a: [Float], _ t: Double) -> Float { let i = Int(t * 48000); return a[max(0, i - 480)..<min(a.count, i + 4800)].map(abs).max() ?? 0 }
            func rms(_ a: [Float], _ t0: Double, _ t1: Double) -> Double {
                let s = a[Int(t0 * 48000)..<min(a.count, Int(t1 * 48000))]; return (s.reduce(0) { $0 + Double($1 * $1) } / Double(max(1, s.count))).squareRoot()
            }
            func times(_ got: [Double], _ want: [Double]) -> (Bool, String) {
                let err = zip(got, want).map { abs($0 - $1) }.max() ?? 99
                return (got.count == want.count && err < 0.02, "got \(got.map { String(format: "%.3f", $0) }) want \(want) maxErr \(String(format: "%.1f", err * 1000))ms")
            }
            // 1) 只有讲解（原声音静音）：出现时刻
            var p1 = base; p1.audio.muted = true; p1.voiceovers = [clip]
            let a1 = await export(p1, "plain")
            let want1 = burstAt.filter { $0 >= 0.2 }.map { 3.0 + $0 - 0.2 }
            var (c, info) = times(onsets(a1, from: 1), want1); check("讲解出现在成片正确时刻", c, info)
            // 2) 讲解前面剪掉 1 秒 + 讲解中间的画面剪掉 1 秒：整体前移 1 秒、中间不受影响
            var p2 = p1; p2.cuts = [CutSegment(start: 1.0, end: 2.0), CutSegment(start: 4.0, end: 5.0)]
            let a2 = await export(p2, "cuts")
            (c, info) = times(onsets(a2, from: 1), want1.map { $0 - 1 }); check("剪辑后讲解按锚点前移、中间连着放", c, info)
            // 3) 音量：这段 50%
            var p3 = p1; p3.voiceovers[0].volume = 0.5
            let a3 = await export(p3, "vol")
            let r3 = peak(a3, want1[1]) / max(1e-6, peak(a1, want1[1]))
            check("音量 50%", abs(r3 - 0.5) < 0.06, String(format: "ratio %.3f", r3))
            // 4) 重叠：新录一段盖住 4.0–5.0（成片时间），旧的那一声 4.3 消失、新的 4.5 出现
            var p4 = p1
            p4.voiceovers = VoiceClip.place(VoiceClip(file: "voice/t.m4a", start: 4.0, trimIn: 0, trimOut: 1.0, fileLength: 4.0), into: p4.voiceovers, map: TimeMap(p4))
            let a4 = await export(p4, "overlap")
            (c, info) = times(onsets(a4, from: 1), [3.3, 4.5, 5.3, 6.3]); check("重叠部分以新录的为准", c && p4.voiceovers.count == 3, info)
            // 5) 压低：原声音是持续测试音时，讲解范围里（两声之间）是外面的约 1/4；关掉压低就一样
            var p5 = base; p5.audio.muted = false; p5.audio.system = 1; p5.voiceovers = [clip]; p5.audio.duck = true
            let a5 = await export(p5, "duck")
            var p6 = p5; p6.audio.duck = false
            let a6 = await export(p6, "noduck")
            let outside = rms(a5, 9.0, 9.5)
            if outside > 0.01 {
                let rIn = rms(a5, 3.7, 4.1) / outside, rOff = rms(a6, 3.7, 4.1) / max(1e-6, rms(a6, 9.0, 9.5))
                check("讲解时原声音压低约 -12 dB", abs(rIn - Double(VoiceMix.duckLevel)) < 0.05, String(format: "in/out %.3f", rIn))
                check("关掉压低：原声音不变", abs(rOff - 1) < 0.1, String(format: "in/out %.3f", rOff))
                check("压低时讲解本身照常", abs(peak(a5, want1[1]) - peak(a1, want1[1])) < 0.12)
            } else { print("SKIP 压低（素材原声音太小，用带测试音的 raw.mp4 再测）", outside) }
            check("导出都有声音", !a1.isEmpty && !a2.isEmpty && !a3.isEmpty && !a4.isEmpty)
            print(ok ? "VOICETEST OK" : "VOICETEST FAIL")
            exit(ok ? 0 : 1)
        }
        return true
    }

    // 合成一段测试录音（像 AVAudioRecorder 录出来的一样：AAC m4a、48k 单声道）：几声 2 kHz、60 毫秒的短促音，其余安静
    static func writeTestVoice(_ url: URL, length: Double, bursts: [Double]) throws {
        try? FileManager.default.removeItem(at: url)
        let f = try AVAudioFile(forWriting: url, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 128_000],
                                commonFormat: .pcmFormatFloat32, interleaved: false)
        let n = AVAudioFrameCount(length * 48000)
        guard let buf = AVAudioPCMBuffer(pcmFormat: f.processingFormat, frameCapacity: n) else { throw Studio.failure("buffer") }
        buf.frameLength = n
        let d = buf.floatChannelData![0]
        for i in 0..<Int(n) {
            let t = Double(i) / 48000
            d[i] = bursts.contains { t >= $0 && t < $0 + 0.06 } ? Float(0.5 * sin(2 * .pi * 2000 * t)) : 0
        }
        try f.write(from: buf)
        f.close()
    }

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
                // 跟随段里镜头远距离移动时会故意先退一点再推回去（见 StudioPlan「边移边往后退」），所以看段内最高能到 3 倍、且不超过
                let zs = stride(from: 2.0, through: 4.9, by: 0.1).map { f($0).z }
                check("zoom inside follow segment", abs((zs.max() ?? 0) - 3.0) < 0.05 && zs.allSatisfy { $0 <= 3.05 }, String(format: "max z=%.3f", zs.max() ?? 0))
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
        // EDITOR_LIVE=素材夹：正常打开剪辑窗口（真播放器、放到最后面不抢前台），暂停状态下改样式 / 加标注，打印窗口号给 screencapture -l 截真画面
        if let p = ProcessInfo.processInfo.environment["EDITOR_LIVE"] {
            Task {
                await StudioEditor.open(URL(fileURLWithPath: p))
                guard let ed = StudioEditor.open.values.first, let w = ed.window else { exit(1) }
                w.orderBack(nil)
                try? await Task.sleep(for: .seconds(2.5))
                print("LIVE before", w.windowNumber); fflush(stdout)
                try? await Task.sleep(for: .seconds(2.5))
                ed.edit { $0.look.cornerRadius = 90 }
                ed.addAnnotation(.ellipse, OverlayGeo(x: 0.3, y: 0.3, w: 0.3, h: 0.3))
                ed.addAnnotation(.arrow, OverlayGeo(x: 0.15, y: 0.8, w: 0.15, h: -0.2))
                ed.addAnnotation(.text, OverlayGeo(x: 0.62, y: 0.7, w: 0, h: 0))
                ed.typeNote(ed.editingNote!, "点这里")
                ed.endNote()
                ed.addCaption(at: 1.0)
                try? await Task.sleep(for: .seconds(1.5))
                print("LIVE after", w.windowNumber); fflush(stdout)
                try? await Task.sleep(for: .seconds(3))
                exit(0)
            }
            return true
        }
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
    private static var keyPlayers: [AVAudioPlayer] = []
    private static var keyPlayersStyle: StudioLook.Typing?
    private static var keyN = 0
    // 一声按键（连打时几个播放器轮着用，前一声还没放完也不会被掐断）
    static func playKey(_ style: StudioLook.Typing, volume: Double = 1) {
        guard StudioEditor.testShot == nil else { return }
        if keyPlayersStyle != style { keyPlayers = (0..<8).compactMap { try? AVAudioPlayer(data: ClickSound.keyWav(style, $0 % 4)) }; keyPlayersStyle = style }
        guard !keyPlayers.isEmpty else { return }
        let p = keyPlayers[keyN % keyPlayers.count]; keyN += 1
        p.volume = Float(min(max(volume, 0), 1)); p.currentTime = 0; p.play()
    }
    // 试听：像打了一小串字
    static func playTyping(_ style: StudioLook.Typing) {
        for d in [0, 0.13, 0.24, 0.41, 0.52, 0.66] {
            DispatchQueue.main.asyncAfter(deadline: .now() + d) { MainActor.assumeIsolated { playKey(style) } }
        }
    }
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
    static func play(_ c: StudioLook.Click, volume: Double = 1) {
        guard StudioEditor.testShot == nil else { return }   // 截图自测时不出声
        player = try? AVAudioPlayer(data: ClickSound.wav(c))
        player?.volume = Float(min(max(volume, 0), 1))
        player?.play()
    }
}

// 预览音量：每条音轨挂一个处理器，按 GainBox 的音量放大 / 缩小（超过 100% 也行，带软限幅）；拖滑块只改数字，不用重建播放器
final class GainBox: @unchecked Sendable { var gain: Float = 1; var cur: Float = 1 }   // gain = 目标，cur = 处理器里正在用的（慢慢追过去，压低 / 恢复不咔嗒）
enum PreviewAudio {
    static func mix(tracks: [AVAssetTrack], gains: [GainBox]) -> AVAudioMix {
        let m = AVMutableAudioMix()
        m.inputParameters = zip(tracks, gains).map { t, g in
            let ip = AVMutableAudioMixInputParameters(track: t)
            var cb = MTAudioProcessingTapCallbacks(
                version: kMTAudioProcessingTapCallbacksVersion_0,
                clientInfo: Unmanaged.passRetained(g).toOpaque(),
                init: { _, info, storage in storage.pointee = info },
                finalize: { tap in Unmanaged<GainBox>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).release() },
                prepare: nil, unprepare: nil,
                process: { tap, frames, _, buffers, framesOut, flagsOut in
                    guard MTAudioProcessingTapGetSourceAudio(tap, frames, buffers, flagsOut, nil, framesOut) == noErr else { return }
                    let box = Unmanaged<GainBox>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                    let g0 = box.cur, g1 = g0 + max(-0.12, min(0.12, box.gain - g0))   // 每块最多变 0.12：压低 / 恢复约 0.15 秒
                    box.cur = g1
                    guard abs(g0 - 1) > 0.001 || abs(g1 - 1) > 0.001 else { return }
                    for b in UnsafeMutableAudioBufferListPointer(buffers) {
                        guard let p = b.mData?.assumingMemoryBound(to: Float.self) else { continue }
                        let n = Int(b.mDataByteSize) / 4
                        for k in 0..<n { p[k] = ClickSound.softLimit(p[k] * (g0 + (g1 - g0) * Float(k) / Float(max(1, n)))) }
                    }
                })
            var tap: MTAudioProcessingTap?
            if MTAudioProcessingTapCreate(kCFAllocatorDefault, &cb, kMTAudioProcessingTapCreationFlag_PostEffects, &tap) == noErr { ip.audioTapProcessor = tap }
            return ip
        }
        return m
    }
}

// 「导出」左边的小按钮：写着当前设置（1440p · 60 帧），点开改分辨率、帧率、画质、格式、导出后复制
struct ExportOptionsButton: View {
    @ObservedObject var ed: StudioEditor
    @State private var open = false

    static func resName(_ r: ExportOptions.Res) -> String {
        switch r { case .original: L("原始大小", "Original"); case .uhd: "4K"; case .qhd: "1440p"; case .hd: "1080p"; case .sd: "720p" }
    }
    private var summary: String {
        let o = ed.exportOptions
        return o.format == .gif ? "GIF" : "\(Self.resName(o.res)) · \(o.fps)" + L(" 帧", "fps")
    }

    var body: some View {
        Button { open.toggle() } label: {
            HStack(spacing: 4) { Image(systemName: "slider.horizontal.3"); Text(summary).font(.callout.monospacedDigit()) }
        }
        .help(L("导出设置：分辨率、帧率、画质、格式", "Export settings: resolution, frame rate, quality, format"))
        .popover(isPresented: $open, arrowEdge: .bottom) { ExportOptionsPanel(o: $ed.exportOptions) }
    }
}

struct ExportOptionsPanel: View {
    @Binding var o: ExportOptions
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("导出设置", "Export Settings")).font(.headline)
            Picker(L("格式", "Format"), selection: $o.format) {
                Text(L("视频", "Video")).tag(ExportOptions.Format.video)
                Text("GIF").tag(ExportOptions.Format.gif)
            }.pickerStyle(.segmented)
            if o.format == .video {
                Picker(L("分辨率", "Resolution"), selection: $o.res) {
                    ForEach(ExportOptions.Res.allCases, id: \.self) { Text(ExportOptionsButton.resName($0)).tag($0) }
                }
                Picker(L("帧率", "Frame rate"), selection: $o.fps) {
                    Text(L("60 帧（更顺）", "60 fps (smoother)")).tag(60)
                    Text(L("30 帧（更快更小）", "30 fps (faster, smaller)")).tag(30)
                }
                Picker(L("画质", "Quality"), selection: $o.quality) {
                    Text(L("高", "High")).tag(ExportOptions.Quality.high)
                    Text(L("标准", "Standard")).tag(ExportOptions.Quality.standard)
                    Text(L("小文件", "Small file")).tag(ExportOptions.Quality.small)
                }
                Text(L("分辨率越小、帧率越低，导出越快。发微信、抖音选 1080p 就够；透明背景导出 .mov，其他导出 .mp4",
                       "Lower resolution and frame rate export faster. 1080p is plenty for social media; transparent backgrounds export .mov, others .mp4"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else {
                Text(L("GIF：宽 960、每秒 15 帧、没有声音；透明背景会铺成白色。适合发很短的片段",
                       "GIF: 960 wide, 15 fps, no sound; transparent backgrounds become white. Best for short clips"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Toggle(isOn: $o.copyToClipboard) {
                Text(L("导出后复制到剪贴板（可以直接粘贴到微信、访达）", "Copy to clipboard after export (paste into chats or Finder)"))
                    .fixedSize(horizontal: false, vertical: true)
            }.toggleStyle(.checkbox)
        }
        .padding(16).frame(width: 320)
    }

    // 自测：EXPORTOPTSSHOT=图片路径 [EXPORTOPTS_GIF=1] → 不开窗口，把导出设置面板画成图片后退出
    @MainActor static func selfTest() -> Bool {
        let env = ProcessInfo.processInfo.environment
        guard let path = env["EXPORTOPTSSHOT"] else { return false }
        var o = ExportOptions(); if env["EXPORTOPTS_GIF"] != nil { o.format = .gif }
        // 系统控件（下拉框、复选框）只有放进真窗口才画得出来：窗口放到屏幕外，不抢前台
        NSApp.setActivationPolicy(.accessory)
        let v = NSHostingView(rootView: ExportOptionsPanel(o: .constant(o)).background(Color(nsColor: .windowBackgroundColor)))
        if let a = env["EDITOR_APPEARANCE"] { v.appearance = NSAppearance(named: a == "dark" ? .darkAqua : .aqua) }
        v.frame.size = v.fittingSize
        let w = NSWindow(contentRect: v.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        w.contentView = v; w.setFrameOrigin(CGPoint(x: -20000, y: -20000)); w.orderBack(nil)
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(0.5))
            if let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
                v.cacheDisplay(in: v.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path)); print("EXPORTOPTSSHOT OK", path)
            }
            exit(0)
        }
        return true
    }
}

// 预览里放讲解：每段一个 AVAudioPlayer，按成片时间对齐画面（落后 / 超前 80 毫秒以上就校正一下）
@MainActor final class VoicePreview {
    private var players: [UUID: (file: String, p: AVAudioPlayer)] = [:]

    // 返回：现在有没有讲解在响
    @discardableResult
    func sync(dir: URL, clips: [VoiceClip], map: TimeMap, outTime o: Double, level: Denoise.Level = .off, playing: Bool, master: Double) -> Bool {
        var any = false
        let ids = Set(clips.map(\.id))
        for (id, e) in players where !ids.contains(id) { e.p.stop(); players[id] = nil }
        for c in clips {
            let (os, oe) = c.outRange(map)
            let on = playing && master > 0.001 && o >= os && o < oe
            if !on { if let e = players[c.id], e.p.isPlaying { e.p.pause() }; continue }
            any = true
            let url = Denoise.voiceURL(dir: dir, file: c.file, level: level)   // 降噪开着就放处理后的
            if players[c.id]?.file != url.path {
                players[c.id]?.p.stop()
                guard let p = try? AVAudioPlayer(contentsOf: url) else { continue }
                p.prepareToPlay(); players[c.id] = (url.path, p)
            }
            guard let p = players[c.id]?.p else { continue }
            p.volume = Float(min(1, c.volume * master))
            let pos = c.trimIn + (o - os)
            if !p.isPlaying { p.currentTime = pos; p.play() }
            else if abs(p.currentTime - pos) > 0.08 { p.currentTime = pos }
        }
        return any
    }
    func stopAll() { for e in players.values { e.p.stop() }; players.removeAll() }

    // 预览里原声音压多少（和导出同一条曲线，只是按每秒 30 次取样）
    static func duckGain(outTime o: Double, clips: [VoiceClip], map: TimeMap) -> Float {
        VoiceMix.spansOnly(clips.map { $0.outRange(map) }, duck: true).duckGain(Int64(o * 48000))
    }
}

// 降噪进度：后台线程报上来，主线程更新进度条
final class DenoiseProgress: @unchecked Sendable {
    private weak var ed: StudioEditor?
    @MainActor init(_ e: StudioEditor) { ed = e }
    @Sendable func report(_ v: Double) { DispatchQueue.main.async { MainActor.assumeIsolated { self.ed?.setDenoiseProgress(v) } } }
}

// 选中字幕时上面那条里的输入框：直接打字，预览马上变（整次改字算一步撤销）；双击字幕块 / 新加一句时光标自动放进来
struct CaptionField: View {
    @ObservedObject var ed: StudioEditor
    let cap: Caption
    var inBlock = false          // true = 长在时间轴的黄色字幕块里（双击 / 新加时光标放这儿）
    @State private var text = ""
    @FocusState private var focused: Bool
    var body: some View {
        TextField(L("在这里写字幕（回车完成）", "Type the caption (Return to finish)"), text: $text)
            .textFieldStyle(inBlock ? AnyTextFieldStyle(.plain) : AnyTextFieldStyle(.roundedBorder))
            .font(inBlock ? .system(size: 10.5, weight: .semibold) : .callout)
            .foregroundStyle(inBlock ? Color.white : Color.primary)
            .focused($focused)
            .onAppear { text = cap.text; if inBlock && ed.editingCaption == cap.id { DispatchQueue.main.async { focused = true } } }
            .onChange(of: cap.id) { _, _ in text = cap.text }
            .onChange(of: cap.text) { _, v in if !focused { text = v } }   // 另一处改了：跟上
            .onChange(of: text) { _, v in
                guard v != cap.text, let i = ed.captionIndex(cap.id) else { return }
                ed.dragBegin(); ed.dragUpdate { $0.captions[i].text = v.replacingOccurrences(of: "\n", with: " ") }
            }
            .onSubmit { focused = false }
            .onChange(of: focused) { _, f in if !f { ed.dragEnd(); if inBlock { ed.editingCaption = nil } } }
            .onExitCommand { focused = false }
    }
}
struct AnyTextFieldStyle: TextFieldStyle {
    private let make: (TextField<_Label>) -> AnyView
    init<S: TextFieldStyle>(_ s: S) { make = { AnyView($0.textFieldStyle(s)) } }
    func _body(configuration: TextField<_Label>) -> some View { make(configuration) }
}

// 预览右下角：放大倍数、−、+、适应；预览上双指捏合 / ⌘+滚轮放大，放大后双指滑动（或滚轮）平移
struct PreviewZoomBar: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .bottomTrailing) {
                PreviewGestures(ed: ed).allowsHitTesting(false)
                HStack(spacing: 6) {
                    Button { ed.setPreviewZoom(ed.previewZoom / 1.25) } label: { Image(systemName: "minus") }.disabled(ed.previewZoom <= 1)
                    Text("\(Int((ed.previewZoom * 100).rounded()))%").font(.caption.monospacedDigit()).frame(width: 40)
                    Button { ed.setPreviewZoom(ed.previewZoom * 1.25) } label: { Image(systemName: "plus") }
                    if ed.previewZoom > 1.001 {
                        Button(L("适应", "Fit")) { withAnimation(.easeOut(duration: 0.2)) { ed.previewZoom = 1; ed.previewPan = .zero } }
                    }
                }
                .buttonStyle(.borderless).controlSize(.small)
                .padding(.horizontal, 10).padding(.vertical, 5)
                .background(.ultraThinMaterial, in: Capsule())
                .padding(10)
                .opacity(ed.previewZoom > 1.001 || hover ? 1 : 0.55)
                .onHover { hover = $0 }
                .help(L("预览放大：双指捏合或按住 ⌘ 滚动；放大后双指滑动平移（只放大预览，不影响成片）", "Zoom the preview: pinch or ⌘-scroll; when zoomed, two-finger scroll to pan (preview only)"))
            }
            .onAppear { ed.previewSize = g.size }
            .onChange(of: g.size) { _, v in ed.previewSize = v }
        }
    }
    @State private var hover = false
}

struct PreviewGestures: NSViewRepresentable {
    let ed: StudioEditor
    final class Catcher: NSView {
        weak var ed: StudioEditor?
        private var monitor: Any?
        override func hitTest(_ p: NSPoint) -> NSView? { nil }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
            guard window != nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: [.magnify, .scrollWheel]) { [weak self] e in
                guard let self, let ed = self.ed, e.window === self.window else { return e }
                let p = self.convert(e.locationInWindow, from: nil)
                guard self.bounds.contains(p) else { return e }
                let a = CGPoint(x: p.x, y: self.bounds.height - p.y)   // SwiftUI 坐标（y 朝下）
                return MainActor.assumeIsolated {
                    if e.type == .magnify { ed.setPreviewZoom(ed.previewZoom * (1 + e.magnification), anchor: a); return nil }
                    if e.modifierFlags.contains(.command) {
                        ed.setPreviewZoom(ed.previewZoom * TimelineMath.wheelFactor(e.scrollingDeltaY, precise: e.hasPreciseScrollingDeltas), anchor: a); return nil
                    }
                    guard ed.previewZoom > 1.001 else { return e }
                    let k = e.hasPreciseScrollingDeltas ? 1.0 : 12.0
                    ed.panPreview(CGSize(width: e.scrollingDeltaX * k, height: e.scrollingDeltaY * k)); return nil
                }
            }
        }
        deinit { if let m = monitor { NSEvent.removeMonitor(m) } }
    }
    func makeNSView(context: Context) -> Catcher { let v = Catcher(); v.ed = ed; return v }
    func updateNSView(_ v: Catcher, context: Context) { v.ed = ed }
}

// 预览和时间轴中间那条：上下拖改时间轴高度（往上拖 = 时间轴变高、预览变小），双击恢复默认
struct SplitHandle: View {
    @Binding var height: Double
    @State private var start: Double?
    var body: some View {
        ZStack {
            Divider()
            Capsule().fill(Color.secondary.opacity(0.5)).frame(width: 36, height: 4)
        }
        .frame(height: 9)
        .contentShape(Rectangle())
        .onHover { inside in if inside { NSCursor.resizeUpDown.push() } else { NSCursor.pop() } }
        .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .global)   // 用窗口坐标：把手自己跟着动，局部坐标会来回跳（画面晃）
            .onChanged { v in
                if start == nil { start = height }
                height = min(max((start ?? height) - v.translation.height, 110), 640)
            }
            .onEnded { _ in start = nil })
        .onTapGesture(count: 2) { withAnimation(.easeOut(duration: 0.2)) { height = 300 } }
        .help(L("上下拖动：调预览和时间轴的大小；双击恢复", "Drag up or down to resize the preview and timeline; double-click to reset"))
    }
}
