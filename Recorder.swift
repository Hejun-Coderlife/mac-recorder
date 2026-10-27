// 录屏：选一个屏幕或窗口，连同电脑播放的声音（可选麦克风）一起录成 mp4
// 用苹果的 ScreenCaptureKit 拿画面和声音，AVAssetWriter 自己写成 mp4（视频 H.264；电脑声音、麦克风各一条 AAC 音轨）；全局快捷键 ⌥⌘R 开始 / 停止
// （试过 macOS 15 的 SCRecordingOutput 一步写文件：报 RPRecordingErrorDomain -5814、没有原因，改成自己写）
// 编译：./build.sh（只要命令行开发工具，不用 Xcode）
import SwiftUI
import ScreenCaptureKit
import AVFoundation
import Carbon.HIToolbox
import Accelerate

// 界面语言跟随系统：系统首选语言是中文就显示中文，否则英文
let isZh = Locale.preferredLanguages.first?.hasPrefix("zh") ?? false
func L(_ zh: String, _ en: String) -> String { isZh ? zh : en }
let appName = L("录屏", "Screen Recorder")

// 框选的区域：在哪块屏幕上、框的位置（点，左上角为原点）
struct Region { let display: CGDirectDisplayID; let rect: CGRect }

// 可录的东西：整个屏幕、框选区域或某个窗口
struct Source: Identifiable, Hashable {
    let id: String
    let title: String
    let detail: String
    let isDisplay: Bool
}

// ---- 写文件：在采集线程上收画面、声音，按时间写进 mp4 ----
final class Writer: NSObject, SCStreamOutput, @unchecked Sendable {
    let queue = DispatchQueue(label: "recorder.samples")
    private let writer: AVAssetWriter
    private let video: AVAssetWriterInput
    private let sysAudio: AVAssetWriterInput?
    private let micAudio: AVAssetWriterInput?
    private var started = false
    var onError: ((Error) -> Void)?
    // 亮度校正：苹果自带录屏把屏幕的 2.2 伽马按 2.4 重新编码（暗部、中间调更深），我们按同样的曲线改亮度平面，颜色看起来和它一样
    // （实测：苹果的数值 ≈ 我们的数值 ^ 1.09；GAMMA 环境变量用来试验）
    private let lut: [UInt8] = {
        let k = Double(ProcessInfo.processInfo.environment["GAMMA"] ?? "") ?? 1.09
        return (0..<256).map { y -> UInt8 in
            let v = min(1, max(0, (Double(y) - 16) / 219))   // 视频范围 16~235
            return UInt8(clamping: Int((16 + 219 * pow(v, k)).rounded()))
        }
    }()

    init(url: URL, width: Int, height: Int, system: Bool, mic: Bool) throws {
        writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width, AVVideoHeightKey: height,
            // 码率和苹果自带录屏一样：约 0.18 比特/像素/帧（2940×1912 @60 ≈ 60 Mbps）；以前按 6 比特/像素算，只有一半，画面一动就糊
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: max(8_000_000, Int(Double(width * height) * 60 * 0.18)),
                AVVideoExpectedSourceFrameRateKey: 60,
                AVVideoMaxKeyFrameIntervalKey: 120,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
            ],
        ].merging(ProcessInfo.processInfo.environment["WRITERCOLOR"] == "709" ? [
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
        ] : [:]) { a, _ in a })
        video.expectsMediaDataInRealTime = true
        writer.add(video)
        func audio(_ ch: Int) -> AVAssetWriterInput {
            let a = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: ch, AVEncoderBitRateKey: ch == 2 ? 192_000 : 96_000,
            ])
            a.expectsMediaDataInRealTime = true
            return a
        }
        sysAudio = system ? audio(2) : nil
        micAudio = mic ? audio(1) : nil
        if let a = sysAudio { writer.add(a) }
        if let a = micAudio { writer.add(a) }
        super.init()
        guard writer.startWriting() else { throw writer.error ?? NSError(domain: "录屏", code: 1) }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sb.isValid, writer.status == .writing else {
            if writer.status == .failed, let e = writer.error { onError?(e); onError = nil }
            return
        }
        switch type {
        case .screen:
            // 只要完整的新画面（画面没变时系统会发「空闲帧」，跳过）
            guard let att = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
                  let raw = att.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete else { return }
            if !started { writer.startSession(atSourceTime: sb.presentationTimeStamp); started = true }
            if let px = CMSampleBufferGetImageBuffer(sb) { gammaY(px) }
            if video.isReadyForMoreMediaData { video.append(sb) }
        case .audio:
            if started, let a = sysAudio, a.isReadyForMoreMediaData { a.append(sb) }
        case .microphone:
            if started, let a = micAudio, a.isReadyForMoreMediaData { a.append(sb) }
        @unknown default: break
        }
    }

    // 亮度平面（第 0 平面）原地查表
    private func gammaY(_ px: CVPixelBuffer) {
        guard CVPixelBufferLockBaseAddress(px, []) == kCVReturnSuccess else { return }
        defer { CVPixelBufferUnlockBaseAddress(px, []) }
        guard let base = CVPixelBufferGetBaseAddressOfPlane(px, 0) else { return }
        var buf = vImage_Buffer(data: base, height: vImagePixelCount(CVPixelBufferGetHeightOfPlane(px, 0)),
                                width: vImagePixelCount(CVPixelBufferGetWidthOfPlane(px, 0)), rowBytes: CVPixelBufferGetBytesPerRowOfPlane(px, 0))
        lut.withUnsafeBufferPointer { _ = vImageTableLookUp_Planar8(&buf, &buf, $0.baseAddress!, vImage_Flags(kvImageNoFlags)) }
    }

    func finish() async throws {
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in queue.async { c.resume() } }   // 等采集线程上排着的样本写完
        guard started else { writer.cancelWriting(); throw NSError(domain: "录屏", code: 2, userInfo: [NSLocalizedDescriptionKey: L("没有录到画面（选的窗口被挡住或最小化了？）", "No frames captured (is the window covered or minimized?)")]) }
        video.markAsFinished(); sysAudio?.markAsFinished(); micAudio?.markAsFinished()
        await writer.finishWriting()
        if writer.status != .completed { throw writer.error ?? NSError(domain: "录屏", code: 3) }
    }
}

@MainActor
final class Recorder: NSObject, ObservableObject {
    @Published var sources: [Source] = []
    @Published var selection: String?
    @Published var systemAudio = true
    @Published var microphone = false
    @Published var showCursor = true
    @Published var recording = false
    @Published var busy = false
    @Published var elapsed: TimeInterval = 0
    @Published var message = ""
    @Published var isError = false        // 提示是不是出错（标红）
    @Published var region: Region?        // 框选的区域（列表里 id = "r"）
    private var needsPermission = false
    @Published var lastFile: URL?

    private var content: SCShareableContent?
    private var stream: SCStream?
    private var writer: Writer?
    private var timer: Timer?
    private var startedAt: Date?

    static let folder: URL = {
        let u = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0].appendingPathComponent("录屏", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }()

    // 读一遍当前有哪些屏幕、窗口（第一次会弹系统的屏幕录制授权）
    func refresh() async {
        do {
            let c = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
            content = c
            var list: [Source] = []
            if let r = region {
                list.append(Source(id: "r", title: L("选定区域", "Selected Area"),
                                   detail: "\(Int(r.rect.width)) × \(Int(r.rect.height))", isDisplay: false))
            }
            for (i, d) in c.displays.enumerated() {
                list.append(Source(id: "d\(d.displayID)", title: c.displays.count > 1 ? L("整个屏幕 \(i + 1)", "Entire Screen \(i + 1)") : L("整个屏幕", "Entire Screen"),
                                   detail: "\(d.width) × \(d.height)", isDisplay: true))
            }
            let me = Bundle.main.bundleIdentifier
            let wins = c.windows.filter { w in
                w.windowLayer == 0 && w.frame.width >= 200 && w.frame.height >= 150 &&
                w.owningApplication?.bundleIdentifier != me && !(w.title ?? "").isEmpty
            }.sorted { ($0.owningApplication?.applicationName ?? "") < ($1.owningApplication?.applicationName ?? "") }
            for w in wins {
                list.append(Source(id: "w\(w.windowID)", title: w.owningApplication?.applicationName ?? L("窗口", "Window"),
                                   detail: w.title ?? "", isDisplay: false))
            }
            sources = list
            if selection == nil || !list.contains(where: { $0.id == selection }) { selection = list.first?.id }
            if needsPermission { needsPermission = false; say("") }
        } catch {
            needsPermission = true
            say(L("需要屏幕录制权限：系统设置 → 隐私与安全性 → 屏幕与系统录音，打开「\(appName)」，然后退出并重新打开本程序",
                  "Screen recording permission needed: System Settings → Privacy & Security → Screen & System Audio Recording, turn on “\(appName)”, then quit and reopen this app"), error: true)
        }
    }

    // 底部提示：error = true 时标红
    func say(_ text: String, error: Bool = false) {
        message = text; isError = error
    }

    // 盖上遮罩让用户拖一个框，选好后列表里出现「选定区域」并选中它
    func pickRegion() {
        RegionPicker.pick { [weak self] r in
            guard let self else { return }
            self.region = r; self.selection = "r"
            Task { await self.refresh() }
        }
    }

    func toggle() {
        guard !busy else { return }
        Task { recording ? await stop() : await start() }
    }

    private func start() async {
        busy = true; defer { busy = false }
        await refresh()
        guard let c = content, let sel = selection else { say(L("先选一个要录的屏幕或窗口", "Pick a screen or window to record first"), error: true); return }
        if microphone {
            let ok = await AVCaptureDevice.requestAccess(for: .audio)
            if !ok { say(L("没有麦克风权限：系统设置 → 隐私与安全性 → 麦克风，打开「\(appName)」",
                         "No microphone permission: System Settings → Privacy & Security → Microphone, turn on “\(appName)”"), error: true); return }
        }
        // 画面范围
        let filter: SCContentFilter
        var crop: CGRect?
        if sel == "r" {
            guard let r = region, let d = c.displays.first(where: { $0.displayID == r.display }) else {
                say(L("那块屏幕不见了，重新选一下区域", "That screen is gone, select the area again"), error: true); return
            }
            let mine = c.applications.filter { $0.bundleIdentifier == Bundle.main.bundleIdentifier }
            filter = SCContentFilter(display: d, excludingApplications: mine, exceptingWindows: [])
            crop = r.rect
        } else if sel.hasPrefix("d"), let d = c.displays.first(where: { "d\($0.displayID)" == sel }) {
            // 整个屏幕：把本程序自己的窗口排除掉
            let mine = c.applications.filter { $0.bundleIdentifier == Bundle.main.bundleIdentifier }
            filter = SCContentFilter(display: d, excludingApplications: mine, exceptingWindows: [])
        } else if let w = c.windows.first(where: { "w\($0.windowID)" == sel }) {
            filter = SCContentFilter(desktopIndependentWindow: w)
        } else { say(L("这个窗口已经关掉了，换一个", "That window is gone, pick another one"), error: true); await refresh(); return }

        let cfg = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        // 宽高按像素、取偶数（H.264 要求）
        let size = crop?.size ?? filter.contentRect.size
        cfg.width = max(2, Int(size.width * scale) / 2 * 2)
        cfg.height = max(2, Int(size.height * scale) / 2 * 2)
        if let crop { cfg.sourceRect = crop }
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        cfg.queueDepth = 6
        // 色彩：COLORMODE 环境变量用来对比试验（默认 = 试验选出来的那种）
        cfg.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        cfg.colorMatrix = CGDisplayStream.yCbCrMatrix_ITU_R_709_2
        switch ProcessInfo.processInfo.environment["COLORMODE"] ?? "srgb" {
        case "709": cfg.colorSpaceName = CGColorSpace.itur_709
        case "p3": cfg.colorSpaceName = CGColorSpace.displayP3
        case "dcip3": cfg.colorSpaceName = CGColorSpace.dcip3
        case "adobe": cfg.colorSpaceName = CGColorSpace.adobeRGB1998
        case "none": break
        default: cfg.colorSpaceName = CGColorSpace.sRGB
        }
        cfg.showsCursor = showCursor
        cfg.capturesAudio = systemAudio
        cfg.sampleRate = 48000
        cfg.channelCount = 2
        cfg.excludesCurrentProcessAudio = true
        cfg.captureMicrophone = microphone

        let fmt = DateFormatter(); fmt.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let url = Recorder.folder.appendingPathComponent("\(L("录屏", "Recording")) \(fmt.string(from: Date())).mp4")
        let w: Writer
        do { w = try Writer(url: url, width: cfg.width, height: cfg.height, system: systemAudio, mic: microphone) }
        catch { say(L("开始录制失败：", "Couldn't start recording: ") + error.localizedDescription, error: true); return }
        w.onError = { [weak self] e in Task { @MainActor in self?.fail(e) } }
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        do {
            try s.addStreamOutput(w, type: .screen, sampleHandlerQueue: w.queue)
            if systemAudio { try s.addStreamOutput(w, type: .audio, sampleHandlerQueue: w.queue) }
            if microphone { try s.addStreamOutput(w, type: .microphone, sampleHandlerQueue: w.queue) }
            try await s.startCapture()
        } catch {
            say(L("开始录制失败：", "Couldn't start recording: ") + error.localizedDescription, error: true)
            try? FileManager.default.removeItem(at: url)
            return
        }
        stream = s; writer = w; lastFile = url
        recording = true; startedAt = Date(); elapsed = 0; say("")
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor in if let self, let t = self.startedAt { self.elapsed = Date().timeIntervalSince(t) } }
        }
        NSSound(named: "Tink")?.play()
    }

    private func stop() async {
        busy = true; defer { busy = false }
        timer?.invalidate(); timer = nil
        if let s = stream { try? await s.stopCapture() }
        let w = writer
        stream = nil; writer = nil; recording = false
        NSSound(named: "Pop")?.play()
        guard let w else { return }
        do { try await w.finish(); finished(nil) } catch { finished(error) }
    }
    // 录到一半出错（写文件失败、窗口关了）：停下，提示原因
    fileprivate func fail(_ e: Error) {
        say(L("录制出错：", "Recording error: ") + e.localizedDescription, error: true)
        if recording { toggle() }
    }

    fileprivate func finished(_ error: Error?) {
        if let error { say(L("保存失败：", "Save failed: ") + error.localizedDescription, error: true); return }
        if let f = lastFile, FileManager.default.fileExists(atPath: f.path) {
            say(L("已保存：", "Saved: ") + f.lastPathComponent)
            NSWorkspace.shared.activateFileViewerSelecting([f])
        }
    }
}

extension Recorder: SCStreamDelegate {
    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in
            // 被录的窗口关掉了之类：自动停下并保存
            if self.recording { self.say(L("录制中断：", "Recording interrupted: ") + error.localizedDescription, error: true); self.toggle() }
        }
    }
}

// ---- 全局快捷键 ⌥⌘R（游戏锁住鼠标、点不到按钮时用）：Carbon 的热键，不需要辅助功能权限 ----
final class HotKey {
    static var action: (() -> Void)?
    private var ref: EventHotKeyRef?
    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { HotKey.action?() }
            return noErr
        }, 1, &spec, nil, nil)
        let id = EventHotKeyID(signature: OSType(0x5243_5244), id: 1)   // 'RCRD'
        RegisterEventHotKey(UInt32(kVK_ANSI_R), UInt32(optionKey | cmdKey), id, GetApplicationEventTarget(), 0, &ref)
    }
}

// ---- 选区域：每块屏幕盖一层半透明遮罩，按住拖一个框，松手就选好；Esc 取消 ----
final class RegionPicker {
    private static var windows: [NSWindow] = []
    static func pick(_ done: @escaping (Region) -> Void) {
        close()
        for screen in NSScreen.screens {
            let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID ?? CGMainDisplayID()
            let w = OverlayWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
            w.level = .screenSaver
            w.isOpaque = false; w.backgroundColor = .clear; w.hasShadow = false
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            let v = RegionView(frame: CGRect(origin: .zero, size: screen.frame.size))
            v.onDone = { rect in
                DispatchQueue.main.async { close() }   // 别在这个窗口自己的鼠标事件里把它关掉
                guard let rect, rect.width >= 20, rect.height >= 20 else { return }
                // 视图坐标左下角为原点，录屏要左上角为原点
                done(Region(display: id, rect: CGRect(x: rect.minX, y: screen.frame.height - rect.maxY, width: rect.width, height: rect.height)))
            }
            w.contentView = v
            w.orderFrontRegardless()
            if NSMouseInRect(NSEvent.mouseLocation, screen.frame, false) { w.makeKey() }   // 鼠标所在的屏幕接收 Esc
            windows.append(w)
        }
        NSApp.activate(ignoringOtherApps: true)
    }
    static func close() { windows.forEach { $0.orderOut(nil) }; windows = [] }
}

final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

final class RegionView: NSView {
    var onDone: ((CGRect?) -> Void)?
    private var start: CGPoint?
    private var rect: CGRect?

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func viewDidMoveToWindow() { window?.makeFirstResponder(self) }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }

    override func mouseDown(with e: NSEvent) {
        window?.makeKey(); window?.makeFirstResponder(self)
        start = convert(e.locationInWindow, from: nil); rect = nil; needsDisplay = true
    }
    override func mouseDragged(with e: NSEvent) {
        guard let s = start else { return }
        let p = convert(e.locationInWindow, from: nil)
        rect = CGRect(x: min(s.x, p.x), y: min(s.y, p.y), width: abs(p.x - s.x), height: abs(p.y - s.y)).integral
        needsDisplay = true
    }
    override func mouseUp(with e: NSEvent) { if start != nil { onDone?(rect) } }
    override func keyDown(with e: NSEvent) {
        if e.keyCode == UInt16(kVK_Escape) { onDone?(nil) } else { super.keyDown(with: e) }
    }
    override func cancelOperation(_ sender: Any?) { onDone?(nil) }

    override func draw(_ dirtyRect: NSRect) {
        // 框外变暗；框内几乎透明（完全透明的地方鼠标会点穿到下面的程序）
        NSColor(white: 0, alpha: 0.35).setFill()
        let dim = NSBezierPath(rect: bounds)
        if let r = rect { dim.append(NSBezierPath(rect: r)); dim.windingRule = .evenOdd }
        dim.fill()
        if let r = rect {
            NSColor(white: 0, alpha: 0.01).setFill(); r.fill()
            NSColor.white.setStroke()
            let b = NSBezierPath(rect: r.insetBy(dx: -1, dy: -1)); b.lineWidth = 2; b.stroke()
            let below = r.minY - 28 >= 0
            tag("\(Int(r.width)) × \(Int(r.height))", at: CGPoint(x: r.minX, y: below ? r.minY - 28 : r.maxY + 6))
        } else {
            let text = L("拖一个框，选择要录的区域　·　Esc 取消", "Drag to select the area to record  ·  Esc to cancel")
            let size = (text as NSString).size(withAttributes: Self.attrs(18))
            tag(text, at: CGPoint(x: bounds.midX - size.width / 2 - 12, y: bounds.midY), font: 18)
        }
    }
    // 黑底白字的小标签
    private func tag(_ text: String, at p: CGPoint, font: CGFloat = 13) {
        let a = Self.attrs(font)
        let size = (text as NSString).size(withAttributes: a)
        let box = CGRect(x: p.x, y: p.y, width: size.width + 24, height: size.height + 8)
        NSColor(white: 0, alpha: 0.75).setFill()
        NSBezierPath(roundedRect: box, xRadius: 6, yRadius: 6).fill()
        (text as NSString).draw(at: CGPoint(x: box.minX + 12, y: box.minY + 4), withAttributes: a)
    }
    private static func attrs(_ size: CGFloat) -> [NSAttributedString.Key: Any] {
        [.font: NSFont.monospacedDigitSystemFont(ofSize: size, weight: .medium), .foregroundColor: NSColor.white]
    }
}

struct ContentView: View {
    @ObservedObject var rec: Recorder
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text(L("录什么", "What to record")).font(.headline)
                Spacer()
                Button(L("选区域…", "Select Area…")) { rec.pickRegion() }.disabled(rec.recording)
                Button(L("刷新列表", "Refresh")) { Task { await rec.refresh() } }.disabled(rec.recording)
            }
            List(rec.sources, selection: $rec.selection) { s in
                HStack(spacing: 10) {
                    Image(systemName: s.id == "r" ? "rectangle.dashed" : s.isDisplay ? "display" : "macwindow").frame(width: 20)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(s.title).font(.body.weight(.medium))
                        Text(s.detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }.tag(s.id).padding(.vertical, 2)
            }
            .frame(minHeight: 220)
            .disabled(rec.recording)

            HStack(spacing: 22) {
                Toggle(L("录电脑声音", "System audio"), isOn: $rec.systemAudio)
                Toggle(L("录麦克风", "Microphone"), isOn: $rec.microphone)
                Toggle(L("显示鼠标", "Show cursor"), isOn: $rec.showCursor)
            }.disabled(rec.recording)

            HStack(alignment: .center, spacing: 16) {
                Button(action: rec.toggle) {
                    Label(rec.recording ? L("停止", "Stop") : L("开始录制", "Record"), systemImage: rec.recording ? "stop.fill" : "record.circle")
                        .font(.title3.weight(.semibold)).frame(minWidth: 140)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .tint(rec.recording ? .red : .accentColor)
                .disabled(rec.busy || rec.selection == nil)

                if rec.recording {
                    Circle().fill(.red).frame(width: 10, height: 10)
                    Text(String(format: "%02d:%02d", Int(rec.elapsed) / 60, Int(rec.elapsed) % 60))
                        .font(.title2.monospacedDigit())
                }
                Spacer()
                Text(L("⌥⌘R 开始 / 停止", "⌥⌘R start / stop")).font(.callout).foregroundStyle(.secondary)
            }

            HStack {
                Text(rec.message.isEmpty ? L("保存到：影片 / ", "Saved to: Movies / ") + Recorder.folder.lastPathComponent : rec.message)
                    .font(.callout).foregroundStyle(rec.isError ? .red : .secondary)
                    .lineLimit(2)
                Spacer()
                Button(L("打开文件夹", "Open Folder")) { NSWorkspace.shared.open(Recorder.folder) }
            }
        }
        .padding(18)
        .frame(minWidth: 560, minHeight: 440)
        .task { await rec.refresh() }
    }
}

@main
struct RecorderApp: App {
    @StateObject private var rec = Recorder()
    private let hotKey = HotKey()
    var body: some Scene {
        WindowGroup(appName) {
            ContentView(rec: rec)
                .onAppear {
                    HotKey.action = { [weak rec] in rec?.toggle() }
                    // 自测：AUTOTEST=秒数 → 录整个屏幕这么久，结果写到 /tmp/recorder_test.txt，然后退出
                    if let t = ProcessInfo.processInfo.environment["AUTOTEST"], let secs = Double(t) {
                        Task { @MainActor in
                            try? await Task.sleep(for: .seconds(1.5))
                            rec.selection = rec.sources.first(where: { $0.isDisplay })?.id
                            // AUTOREGION=x,y,宽,高 → 改成录主屏幕上的这块区域
                            if let a = ProcessInfo.processInfo.environment["AUTOREGION"]?.split(separator: ",").compactMap({ Double($0) }), a.count == 4 {
                                rec.region = Region(display: CGMainDisplayID(), rect: CGRect(x: a[0], y: a[1], width: a[2], height: a[3]))
                                rec.selection = "r"
                            }
                            rec.toggle()
                            try? await Task.sleep(for: .seconds(secs + 1))
                            rec.toggle()
                            try? await Task.sleep(for: .seconds(3))
                            try? "\(rec.message)\n\(rec.lastFile?.path ?? "")".write(toFile: "/tmp/recorder_test.txt", atomically: true, encoding: .utf8)
                            NSApp.terminate(nil)
                        }
                    }
                }
        }
        .windowResizability(.contentSize)
    }
}
