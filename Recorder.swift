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
    private(set) var startSeconds = 0.0   // 第一帧的时间（主机时钟），鼠标轨迹按它对齐
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
            if !started { writer.startSession(atSourceTime: sb.presentationTimeStamp); startSeconds = sb.presentationTimeStamp.seconds; started = true }
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

    var hasTwoAudioTracks: Bool { sysAudio != nil && micAudio != nil }

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
    // Studio 效果：画面里不录鼠标、另记轨迹，录完自动放大点击处、平滑鼠标、加背景（见 Studio.swift）
    @Published var studio = UserDefaults.standard.bool(forKey: "studio") { didSet { UserDefaults.standard.set(studio, forKey: "studio") } }
    // 开录倒计时：0 = 不倒计时，3 / 5 秒
    @Published var countdown = UserDefaults.standard.integer(forKey: "countdown") { didSet { UserDefaults.standard.set(countdown, forKey: "countdown") } }
    // 录整个屏幕 / 区域时不录桌面上的图标（只排除访达画图标的那层窗口，桌面图片照录）
    @Published var hideDesktopIcons = UserDefaults.standard.bool(forKey: "hideDesktopIcons") { didSet { UserDefaults.standard.set(hideDesktopIcons, forKey: "hideDesktopIcons") } }
    @Published var counting = false
    private var countdownCancelled = false
    private var needsPermission = false
    @Published var lastFile: URL?

    private var content: SCShareableContent?
    private var stream: SCStream?
    private var writer: Writer?
    private var timer: Timer?
    private var startedAt: Date?
    private var tracker: CursorTracker?
    private var project: URL?             // Studio 素材文件夹

    static let folder: URL = {
        let u = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0].appendingPathComponent("录屏", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }()
    static let studioFolder = folder.appendingPathComponent("Studio 素材", isDirectory: true)

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

    // 盖上遮罩让用户调一个框，点「录制」就录这块；列表里会留下「选定区域」方便下次直接录
    func pickRegion() {
        RegionPicker.pick(initial: region) { [weak self] r in
            guard let self else { return }
            self.region = r; self.selection = "r"
            self.toggle()   // 点了「录制」就直接开录
        }
    }

    func toggle() {
        if counting { countdownCancelled = true; return }   // 倒计时中再按一次 = 取消
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
        // 倒计时（自测时不倒计时）：倒计时窗口是本程序的，录屏时本来就被排除在外；Esc 或再按一次快捷键取消
        if countdown > 0 && ProcessInfo.processInfo.environment["AUTOTEST"] == nil {
            var display: CGDirectDisplayID?
            if sel == "r" { display = region?.display } else if sel.hasPrefix("d") { display = CGDirectDisplayID(sel.dropFirst()) }
            countdownCancelled = false; counting = true
            let go = await Countdown.run(seconds: countdown, display: display) { [weak self] in self?.countdownCancelled ?? true }
            counting = false
            if !go { say(L("已取消", "Cancelled")); return }
        }
        // 画面范围
        let filter: SCContentFilter
        var crop: CGRect?
        let area: () -> CGRect?               // 录的范围在全局坐标里的位置（点，左上角为原点），给鼠标轨迹换算用
        if sel == "r" {
            guard let r = region, let d = c.displays.first(where: { $0.displayID == r.display }) else {
                say(L("那块屏幕不见了，重新选一下区域", "That screen is gone, select the area again"), error: true); return
            }
            filter = await displayFilter(d, content: c)
            crop = r.rect
            let a = r.rect.offsetBy(dx: CGDisplayBounds(r.display).minX, dy: CGDisplayBounds(r.display).minY)
            area = { a }
        } else if sel.hasPrefix("d"), let d = c.displays.first(where: { "d\($0.displayID)" == sel }) {
            // 整个屏幕：把本程序自己的窗口排除掉
            filter = await displayFilter(d, content: c)
            let a = CGDisplayBounds(d.displayID)
            area = { a }
        } else if let w = c.windows.first(where: { "w\($0.windowID)" == sel }) {
            filter = SCContentFilter(desktopIndependentWindow: w)
            let id = w.windowID
            area = {   // 窗口可能被拖动，每次现查位置
                guard let info = (CGWindowListCopyWindowInfo(.optionIncludingWindow, id) as? [[String: Any]])?.first,
                      let b = info[kCGWindowBounds as String] as! CFDictionary? else { return nil }
                return CGRect(dictionaryRepresentation: b)
            }
        } else { say(L("这个窗口已经关掉了，换一个", "That window is gone, pick another one"), error: true); await refresh(); return }

        let cfg = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        // 宽高按像素；H.264 要偶数，奇数时往上补 1 像素（边上留一条空白），画面按原尺寸放、不缩放
        // （以前往下取偶数：差 1 像素也会把整个画面重新缩放一遍，字全糊了。Retina 屏乘 2 本来就是偶数，只有 1 倍屏的奇数窗口会补）
        let size = crop?.size ?? filter.contentRect.size
        let pw = max(2, Int((size.width * scale).rounded())), ph = max(2, Int((size.height * scale).rounded()))
        cfg.width = (pw + 1) / 2 * 2
        cfg.height = (ph + 1) / 2 * 2
        if cfg.width != pw || cfg.height != ph { cfg.destinationRect = CGRect(origin: .zero, size: size) }
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
        let studioOn = studio
        cfg.showsCursor = showCursor && !studioOn   // Studio 模式鼠标后期再画
        cfg.capturesAudio = systemAudio
        cfg.sampleRate = 48000
        cfg.channelCount = 2
        cfg.excludesCurrentProcessAudio = true
        cfg.captureMicrophone = microphone

        let fmt = DateFormatter(); fmt.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let name = "\(L("录屏", "Recording")) \(fmt.string(from: Date()))"
        let url = Recorder.folder.appendingPathComponent(name + (studioOn ? ".mov" : ".mp4"))   // Studio 版背景透明，只能存 mov
        // Studio 模式：原始画面写进素材文件夹，成片录完再生成到 url
        var rawURL = url, dir: URL?
        if studioOn {
            let d = Recorder.studioFolder.appendingPathComponent(name, isDirectory: true)
            try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
            rawURL = d.appendingPathComponent("raw.mp4"); dir = d
        }
        let w: Writer
        do { w = try Writer(url: rawURL, width: cfg.width, height: cfg.height, system: systemAudio, mic: microphone) }
        catch { say(L("开始录制失败：", "Couldn't start recording: ") + error.localizedDescription, error: true); return }
        let tr = studioOn ? CursorTracker(size: CGSize(width: pw, height: ph), pixelsPerPoint: Double(scale), area: area) : nil
        w.onError = { [weak self] e in Task { @MainActor in self?.fail(e) } }
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        do {
            try s.addStreamOutput(w, type: .screen, sampleHandlerQueue: w.queue)
            if systemAudio { try s.addStreamOutput(w, type: .audio, sampleHandlerQueue: w.queue) }
            if microphone { try s.addStreamOutput(w, type: .microphone, sampleHandlerQueue: w.queue) }
            tr?.start()   // 比画面早一点开始记，第一帧前后都有鼠标位置
            try await s.startCapture()
        } catch {
            tr?.stop()
            say(L("开始录制失败：", "Couldn't start recording: ") + error.localizedDescription, error: true)
            try? FileManager.default.removeItem(at: rawURL)
            if let dir { try? FileManager.default.removeItem(at: dir) }
            return
        }
        stream = s; writer = w; lastFile = url; tracker = tr; project = dir
        recording = true; startedAt = Date(); elapsed = 0; say("")
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor in if let self, let t = self.startedAt { self.elapsed = Date().timeIntervalSince(t) } }
        }
        NSSound(named: "Tink")?.play()
    }

    // 录整个屏幕 / 区域的范围：排除本程序；开了「隐藏桌面图标」再排除访达画桌面图标的那层窗口
    private func displayFilter(_ d: SCDisplay, content c: SCShareableContent) async -> SCContentFilter {
        let me = Bundle.main.bundleIdentifier
        guard hideDesktopIcons, let all = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) else {
            return SCContentFilter(display: d, excludingApplications: c.applications.filter { $0.bundleIdentifier == me }, exceptingWindows: [])
        }
        let skip = all.windows.filter { $0.owningApplication?.bundleIdentifier == me || Self.isDesktopIcons($0) }
        return SCContentFilter(display: d, excludingWindows: skip)
    }
    // 访达的桌面图标窗口：层级在普通窗口之下（桌面图片是程序坞 / 壁纸进程画的，不是访达，不会被排除）
    nonisolated static func isDesktopIcons(_ w: SCWindow) -> Bool {
        w.owningApplication?.bundleIdentifier == "com.apple.finder" && w.windowLayer < 0
    }

    private func stop() async {
        busy = true; defer { busy = false }
        timer?.invalidate(); timer = nil
        if let s = stream { try? await s.stopCapture() }
        let w = writer, tr = tracker, dir = project
        stream = nil; writer = nil; tracker = nil; project = nil; recording = false
        tr?.stop()
        NSSound(named: "Pop")?.play()
        guard let w else { return }
        do { try await w.finish() } catch { finished(error); return }
        // Studio 模式：存轨迹，打开剪辑窗口（自动生成推近段，调好再导出；声音在导出时混成一条）
        if let tr, let dir, let out = lastFile {
            if ProcessInfo.processInfo.environment["AUTOTEST"] == nil {
                do { try tr.save(to: dir, start: w.startSeconds) }
                catch { say(L("保存 Studio 素材失败：", "Couldn't save Studio footage: ") + error.localizedDescription, error: true); return }
                say(L("已打开 Studio 剪辑窗口，调好后点「导出」", "Opened the Studio editor — adjust, then click Export"))
                await StudioEditor.open(dir)
                return
            }
            say(L("正在生成 Studio 版…", "Rendering Studio version…"))
            do {
                try tr.save(to: dir, start: w.startSeconds)
                lastFile = try await Studio.render(project: dir, to: out) { p in
                    Task { @MainActor [weak self] in self?.say(L("正在生成 Studio 版… \(Int(p * 100))%", "Rendering Studio version… \(Int(p * 100))%")) }
                }
            } catch {
                say(L("生成 Studio 版失败（原始素材在「Studio 素材」文件夹）：", "Studio render failed (raw footage is in the “Studio 素材” folder): ") + error.localizedDescription, error: true)
                return
            }
            finished(nil); return
        }
        // 电脑声音和麦克风都录了：合成一条音轨（不然很多播放器、网站只放第一条，麦克风的声音会丢）
        if w.hasTwoAudioTracks, let f = lastFile {
            say(L("正在合成声音…", "Mixing audio…"))
            do { try await AudioMerge.run(f) }
            catch { say(L("合成声音失败，视频里电脑声音和麦克风还是两条音轨：", "Couldn't mix audio, the video keeps two audio tracks: ") + error.localizedDescription, error: true); return }
        }
        finished(nil)
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
            NSWorkspace.shared.open(f)   // 录完直接用默认播放器打开
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

// ---- 录完把「电脑声音」「麦克风」两条音轨混成一条：抖音、微信、很多网页播放器只放第一条音轨，麦克风会丢 ----
//      画面原样拷贝、不重新压缩，只重新编码声音（几秒钟）；先写到同目录的临时文件，成功了再替换原文件
enum AudioMerge {
    static func run(_ url: URL) async throws {
        let asset = AVURLAsset(url: url)
        let audio = try await asset.loadTracks(withMediaType: .audio)
        guard let vt = try await asset.loadTracks(withMediaType: .video).first, audio.count >= 2 else { return }
        let tmp = url.deletingLastPathComponent().appendingPathComponent(".mixing-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: tmp) }

        let reader = try AVAssetReader(asset: asset)
        let vOut = AVAssetReaderTrackOutput(track: vt, outputSettings: nil)
        let aOut = AVAssetReaderAudioMixOutput(audioTracks: audio, audioSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false,
        ])
        reader.add(vOut); reader.add(aOut)

        let writer = try AVAssetWriter(outputURL: tmp, fileType: .mp4)
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: try await vt.load(.formatDescriptions).first)
        vIn.transform = try await vt.load(.preferredTransform)
        let aIn = AVAssetWriterInput(mediaType: .audio, outputSettings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192_000,
        ])
        writer.add(vIn); writer.add(aIn)
        guard reader.startReading() else { throw reader.error ?? NSError(domain: "录屏", code: 10) }
        guard writer.startWriting() else { throw writer.error ?? NSError(domain: "录屏", code: 11) }
        writer.startSession(atSourceTime: .zero)   // 读出来的时间都从 0 开始（文件里的编辑表已经换算好）

        await withTaskGroup(of: Void.self) { g in
            g.addTask { await pump(vOut, into: vIn, label: "video") }
            g.addTask { await pump(aOut, into: aIn, label: "audio") }
        }
        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? NSError(domain: "录屏", code: 12) }
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? NSError(domain: "录屏", code: 13) }
        _ = try FileManager.default.replaceItemAt(url, withItemAt: tmp)
    }

    // 把一路读出的样本全部写进对应的输入，读完为止
    private static func pump(_ output: AVAssetReaderOutput, into input: AVAssetWriterInput, label: String) async {
        await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
            input.requestMediaDataWhenReady(on: DispatchQueue(label: "recorder.mix.\(label)")) {
                while input.isReadyForMoreMediaData {
                    guard let sb = output.copyNextSampleBuffer() else { input.markAsFinished(); c.resume(); return }
                    input.append(sb)
                }
            }
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

// ---- 选区域（仿系统截图 ⌘⇧5）：每块屏幕盖一层遮罩，一打开就有个框；拖框内移动、拖八个点改大小、框外拖画新框
//      底下工具栏：✕ 取消 / 尺寸 / 录制；回车 = 录制，Esc = 取消，框内双击 = 录制 ----
@MainActor
final class RegionPicker {
    private static var current: RegionPicker?
    // 自测：PICKERSHOT=图片路径 → 不显示、不抢焦点，把选区界面画成图片后退出
    static let testShot = ProcessInfo.processInfo.environment["PICKERSHOT"]
    private var windows: [NSWindow] = []
    private var views: [RegionView] = []
    private(set) var active: RegionView?        // 框在哪块屏幕上
    private lazy var bar = PickerBar(picker: self)
    private let done: (Region) -> Void

    static func pick(initial: Region?, _ done: @escaping (Region) -> Void) {
        current?.close()
        let p = RegionPicker(done: done)
        current = p
        p.open(initial)
    }
    private init(done: @escaping (Region) -> Void) { self.done = done }

    private func open(_ initial: Region?) {
        for screen in NSScreen.screens {
            let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID ?? CGMainDisplayID()
            let w = OverlayWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
            w.level = .screenSaver
            w.isOpaque = false; w.backgroundColor = .clear; w.hasShadow = false
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            let v = RegionView(frame: CGRect(origin: .zero, size: screen.frame.size), picker: self, display: id)
            w.contentView = v
            windows.append(w); views.append(v)
            // 上次选过的框还在这块屏幕上就沿用
            if let r = initial, r.display == id { v.rect = v.flip(r.rect); active = v }
        }
        if active == nil {
            // 默认：鼠标所在屏幕中间 60% 的框
            let mouse = NSEvent.mouseLocation
            let i = NSScreen.screens.firstIndex { NSMouseInRect(mouse, $0.frame, false) } ?? 0
            let v = views[i], b = v.bounds
            v.rect = CGRect(x: b.width * 0.2, y: b.height * 0.2, width: b.width * 0.6, height: b.height * 0.6).integral
            active = v
        }
        if Self.testShot == nil {
            windows.forEach { $0.orderFrontRegardless() }
            NSApp.activate(ignoringOtherApps: true)
        }
        setActive(active!)
    }

    // 框换到另一块屏幕：原来那块的框去掉，工具栏跟过去
    func setActive(_ v: RegionView) {
        if let old = active, old !== v { old.rect = nil }
        active = v
        bar.removeFromSuperview(); v.addSubview(bar); bar.place(in: v.bounds)
        v.window?.makeKey(); v.window?.makeFirstResponder(v)
        changed()
    }
    static func snapshot(to path: String) {
        guard let v = current?.active, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
        v.cacheDisplay(in: v.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }
    func changed() { bar.size = active?.rect?.size ?? .zero }

    func confirm() {
        guard let v = active, let r = v.rect, r.width >= 20, r.height >= 20 else { NSSound.beep(); return }
        let region = Region(display: v.display, rect: v.flip(r))
        close()
        done(region)
    }
    func close() {
        let ws = windows
        ws.forEach { $0.orderOut(nil) }
        windows = []; views = []
        DispatchQueue.main.async { _ = ws }   // 别在窗口自己的鼠标事件里把它释放掉
        if Self.current === self { Self.current = nil }
    }
}

final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

// 底部工具栏
final class PickerBar: NSVisualEffectView {
    private weak var picker: RegionPicker?
    private let label = NSTextField(labelWithString: "")
    var size: CGSize = .zero { didSet { label.stringValue = "\(Int(size.width)) × \(Int(size.height))" } }

    init(picker: RegionPicker) {
        self.picker = picker
        super.init(frame: CGRect(x: 0, y: 0, width: 300, height: 52))
        appearance = NSAppearance(named: .darkAqua)
        material = .hudWindow; blendingMode = .behindWindow; state = .active
        wantsLayer = true; layer?.cornerRadius = 12
        let close = NSButton(image: NSImage(systemSymbolName: "xmark.circle.fill", accessibilityDescription: L("取消", "Cancel"))!,
                             target: self, action: #selector(cancel))
        close.isBordered = false; close.contentTintColor = .secondaryLabelColor
        close.symbolConfiguration = .init(pointSize: 18, weight: .regular)
        close.keyEquivalent = "\u{1b}"   // Esc
        label.font = .monospacedDigitSystemFont(ofSize: 13, weight: .medium)
        label.alignment = .center
        label.widthAnchor.constraint(equalToConstant: 110).isActive = true
        let rec = NSButton(title: L("录制", "Record"), target: self, action: #selector(record))
        rec.bezelStyle = .push; rec.controlSize = .large
        rec.keyEquivalent = "\r"            // 回车；同时让按钮显示成蓝色
        let stack = NSStackView(views: [close, label, rec])
        stack.spacing = 14; stack.alignment = .centerY
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([stack.centerXAnchor.constraint(equalTo: centerXAnchor),
                                     stack.centerYAnchor.constraint(equalTo: centerYAnchor)])
        setFrameSize(CGSize(width: stack.fittingSize.width + 28, height: 52))
    }
    required init?(coder: NSCoder) { fatalError() }

    func place(in b: CGRect) { setFrameOrigin(CGPoint(x: (b.width - frame.width) / 2, y: 70)) }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .arrow) }
    @objc private func cancel() { picker?.close() }
    @objc private func record() { picker?.confirm() }
}

final class RegionView: NSView {
    unowned let picker: RegionPicker
    let display: CGDirectDisplayID
    var rect: CGRect? { didSet { needsDisplay = true; window?.invalidateCursorRects(for: self) } }

    private enum Drag { case new(CGPoint, CGRect?), move(CGPoint, CGRect), resize(Int, CGRect) }
    private var drag: Drag?

    init(frame: CGRect, picker: RegionPicker, display: CGDirectDisplayID) {
        self.picker = picker; self.display = display
        super.init(frame: frame)
    }
    required init?(coder: NSCoder) { fatalError() }

    // 视图坐标左下角为原点、录屏要左上角为原点，两边互换是同一个公式
    func flip(_ r: CGRect) -> CGRect { CGRect(x: r.minX, y: bounds.height - r.maxY, width: r.width, height: r.height) }

    // 八个控制点：0 左下、1 下中、2 右下、3 右中、4 右上、5 上中、6 左上、7 左中
    private func handles(_ r: CGRect) -> [CGPoint] {
        [CGPoint(x: r.minX, y: r.minY), CGPoint(x: r.midX, y: r.minY), CGPoint(x: r.maxX, y: r.minY), CGPoint(x: r.maxX, y: r.midY),
         CGPoint(x: r.maxX, y: r.maxY), CGPoint(x: r.midX, y: r.maxY), CGPoint(x: r.minX, y: r.maxY), CGPoint(x: r.minX, y: r.midY)]
    }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
        guard let r = rect else { return }
        addCursorRect(r.insetBy(dx: 8, dy: 8), cursor: .openHand)
        for (i, h) in handles(r).enumerated() {
            let c: NSCursor = i == 1 || i == 5 ? .resizeUpDown : i == 3 || i == 7 ? .resizeLeftRight : .crosshair
            addCursorRect(CGRect(x: h.x - 8, y: h.y - 8, width: 16, height: 16), cursor: c)
        }
    }

    override func mouseDown(with e: NSEvent) {
        if picker.active !== self { picker.setActive(self) }
        let p = convert(e.locationInWindow, from: nil)
        if e.clickCount == 2, rect?.contains(p) == true { picker.confirm(); return }
        if let r = rect, let i = handles(r).firstIndex(where: { hypot($0.x - p.x, $0.y - p.y) <= 10 }) {
            drag = .resize(i, r)
        } else if let r = rect, r.contains(p) {
            drag = .move(p, r); NSCursor.closedHand.set()
        } else {
            drag = .new(p, rect)
        }
    }
    override func mouseDragged(with e: NSEvent) {
        var p = convert(e.locationInWindow, from: nil)
        p.x = min(max(p.x, 0), bounds.width); p.y = min(max(p.y, 0), bounds.height)
        switch drag {
        case .new(let s, _):
            rect = CGRect(x: min(s.x, p.x), y: min(s.y, p.y), width: abs(p.x - s.x), height: abs(p.y - s.y)).integral
        case .move(let s, let r):
            var n = r.offsetBy(dx: p.x - s.x, dy: p.y - s.y)
            n.origin.x = min(max(n.minX, 0), bounds.width - n.width)
            n.origin.y = min(max(n.minY, 0), bounds.height - n.height)
            rect = n.integral
        case .resize(let i, let r):
            var x0 = r.minX, x1 = r.maxX, y0 = r.minY, y1 = r.maxY
            if [0, 6, 7].contains(i) { x0 = p.x }
            if [2, 3, 4].contains(i) { x1 = p.x }
            if [0, 1, 2].contains(i) { y0 = p.y }
            if [4, 5, 6].contains(i) { y1 = p.y }
            rect = CGRect(x: min(x0, x1), y: min(y0, y1), width: abs(x1 - x0), height: abs(y1 - y0)).integral
        case nil: return
        }
        picker.changed()
    }
    override func mouseUp(with e: NSEvent) {
        // 框外只点一下没拖（或拖得太小）：保留原来的框
        if case .new(_, let old) = drag, (rect?.width ?? 0) < 20 || (rect?.height ?? 0) < 20 { rect = old; picker.changed() }
        drag = nil
        window?.invalidateCursorRects(for: self)
    }
    override func cancelOperation(_ sender: Any?) { picker.close() }

    override func draw(_ dirtyRect: NSRect) {
        // 框外稍微压暗；框内几乎透明（完全透明的地方鼠标会点穿到下面的程序）
        NSColor(white: 0, alpha: 0.25).setFill()
        let dim = NSBezierPath(rect: bounds)
        if let r = rect { dim.append(NSBezierPath(rect: r)); dim.windingRule = .evenOdd }
        dim.fill()
        guard let r = rect else { return }
        NSColor(white: 0, alpha: 0.01).setFill(); r.fill()
        // 虚线框：先描一道深色底，浅色背景上也看得清
        let edge = NSBezierPath(rect: r.insetBy(dx: -0.5, dy: -0.5))
        NSColor(white: 0, alpha: 0.4).setStroke(); edge.lineWidth = 3; edge.stroke()
        NSColor.white.setStroke(); edge.lineWidth = 1.5; edge.setLineDash([6, 4], count: 2, phase: 0); edge.stroke()
        for h in handles(r) {
            let dot = NSBezierPath(ovalIn: CGRect(x: h.x - 5, y: h.y - 5, width: 10, height: 10))
            NSColor.white.setFill(); dot.fill()
            NSColor(white: 0, alpha: 0.35).setStroke(); dot.lineWidth = 1; dot.stroke()
        }
    }
}

struct ContentView: View {
    @ObservedObject var rec: Recorder
    @ObservedObject var updater = Updater.shared
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
                Toggle(L("显示鼠标", "Show cursor"), isOn: $rec.showCursor).disabled(rec.studio)
                Toggle(L("Studio 效果", "Studio effects"), isOn: $rec.studio)
                    .help(L("自动放大点击和打字的地方、鼠标更顺滑、加背景和圆角阴影；录完自动生成", "Auto-zoom on clicks and typing, smooth cursor, background with rounded corners and shadow; rendered after recording"))
            }.disabled(rec.recording || rec.busy)
            HStack(spacing: 22) {
                Picker(L("倒计时", "Countdown"), selection: $rec.countdown) {
                    Text(L("关", "Off")).tag(0)
                    Text(L("3 秒", "3 s")).tag(3)
                    Text(L("5 秒", "5 s")).tag(5)
                }.pickerStyle(.segmented).frame(width: 220)
                Toggle(L("隐藏桌面图标", "Hide desktop icons"), isOn: $rec.hideDesktopIcons)
                    .help(L("录整个屏幕或区域时，画面里不出现桌面上的图标（桌面本身不动）", "Desktop icons are left out of full-screen and area recordings (your desktop isn't changed)"))
            }.disabled(rec.recording || rec.busy)

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
                Button(L("打开 Studio 素材…", "Open Studio Footage…")) { openStudioFootage() }
                Button(L("打开文件夹", "Open Folder")) { NSWorkspace.shared.open(Recorder.folder) }
            }
            if !rec.recording && !rec.busy { UpdateBar(updater: updater) }
        }
        .padding(18)
        .frame(minWidth: 560, minHeight: 440)
        .task { await rec.refresh() }
    }
}

// 选一份以前的 Studio 素材，重新打开剪辑窗口
@MainActor func openStudioFootage() {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.allowsMultipleSelection = false
    panel.directoryURL = Recorder.studioFolder
    panel.message = L("选一个 Studio 素材文件夹", "Choose a Studio footage folder")
    guard panel.runModal() == .OK, let dir = panel.url else { return }
    guard FileManager.default.fileExists(atPath: dir.appendingPathComponent("track.json").path) else {
        let a = NSAlert(); a.messageText = L("这个文件夹不是 Studio 素材", "This folder isn't Studio footage")
        a.informativeText = L("Studio 素材在「影片 / 录屏 / Studio 素材」里，每次录制一个文件夹。", "Studio footage lives in Movies / 录屏 / Studio 素材, one folder per recording.")
        a.runModal(); return
    }
    Task { await StudioEditor.open(dir) }
}

// 窗口底部一行：后台下好了新版 / 刚更新过 / 手动检查的结果
struct UpdateBar: View {
    @ObservedObject var updater: Updater
    var body: some View {
        switch updater.state {
        case .idle: EmptyView()
        case .ready(let v):
            HStack(spacing: 8) {
                Image(systemName: "arrow.down.circle.fill").foregroundStyle(Color.accentColor)
                Text(L("新版本 v\(v) 已下载，退出后自动换上", "Version \(v) is downloaded and will be installed when you quit"))
                Spacer()
                Button(L("更新说明", "What's New")) { NSWorkspace.shared.open(updater.releasePage) }
                Button(L("现在重启", "Restart Now")) { updater.restartNow() }
            }.font(.callout)
        case .updated(let v): Text(L("已更新到 v\(v)", "Updated to \(v)")).font(.callout).foregroundStyle(.secondary)
        case .working(let t): HStack(spacing: 8) { ProgressView().controlSize(.small); Text(t).font(.callout).foregroundStyle(.secondary) }
        case .upToDate: Text(L("已是最新版本 v\(updater.current)", "You're up to date (\(updater.current))")).font(.callout).foregroundStyle(.secondary)
        case .failed(let t): Text(t).font(.callout).foregroundStyle(.red).lineLimit(2)
        }
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
                    if Studio.selfTest() || Updater.selfTest() || StudioTests.planTest() || StudioTests.editorShot() || StudioTests.blurTest() || StudioTests.presetTest() || Countdown.selfTest() || Recorder.iconsTest() { return }
                    if ProcessInfo.processInfo.environment["AUTOTEST"] == nil {
                        Updater.shared.isBusy = { [weak rec] in rec.map { $0.recording || $0.busy } ?? false }
                        Updater.shared.start()
                    }
                    // 自测：MIXTEST=视频路径 → 把这个文件的两条音轨合成一条（会改动文件，先复制一份再测），然后退出
                    if let path = ProcessInfo.processInfo.environment["MIXTEST"] {
                        Task { @MainActor in
                            do { try await AudioMerge.run(URL(fileURLWithPath: path)); print("MIX OK") } catch { print("MIX FAIL", error) }
                            NSApp.terminate(nil)
                        }
                        return
                    }
                    if let path = RegionPicker.testShot {
                        Task { @MainActor in
                            rec.pickRegion()
                            try? await Task.sleep(for: .seconds(0.5))
                            RegionPicker.snapshot(to: path)
                            NSApp.terminate(nil)
                        }
                    }
                    // 自测：AUTOTEST=秒数 → 录整个屏幕这么久，结果写到 /tmp/recorder_test.txt，然后退出
                    if let t = ProcessInfo.processInfo.environment["AUTOTEST"], let secs = Double(t) {
                        Task { @MainActor in
                            try? await Task.sleep(for: .seconds(1.5))
                            rec.selection = rec.sources.first(where: { $0.isDisplay })?.id
                            // AUTOWINDOW=程序名 → 改成录这个程序的第一个窗口
                            if let app = ProcessInfo.processInfo.environment["AUTOWINDOW"] {
                                rec.selection = rec.sources.first(where: { !$0.isDisplay && $0.title == app })?.id
                            }
                            // AUTOREGION=x,y,宽,高 → 改成录主屏幕上的这块区域
                            if let a = ProcessInfo.processInfo.environment["AUTOREGION"]?.split(separator: ",").compactMap({ Double($0) }), a.count == 4 {
                                rec.region = Region(display: CGMainDisplayID(), rect: CGRect(x: a[0], y: a[1], width: a[2], height: a[3]))
                                rec.selection = "r"
                            }
                            // AUTOSTUDIO=1 → 开 Studio 效果录（测完恢复原来的开关）
                            let wasStudio = rec.studio
                            if ProcessInfo.processInfo.environment["AUTOSTUDIO"] != nil { rec.studio = true }
                            rec.toggle()
                            try? await Task.sleep(for: .seconds(secs + 1))
                            rec.toggle()
                            try? await Task.sleep(for: .seconds(3))
                            while rec.busy { try? await Task.sleep(for: .seconds(0.5)) }
                            rec.studio = wasStudio
                            try? "\(rec.sources.map(\.title))\n\(rec.message)\n\(rec.lastFile?.path ?? "")".write(toFile: "/tmp/recorder_test.txt", atomically: true, encoding: .utf8)
                            NSApp.terminate(nil)
                        }
                    }
                }
        }
        .windowResizability(.contentSize)
        .commands {
            CommandGroup(after: .appInfo) {
                Button(L("检查更新…", "Check for Updates…")) { Task { await Updater.shared.check(manual: true) } }
            }
        }
    }
}

// ---- 开录倒计时：屏幕中间大号数字 3、2、1，一秒一个，淡入淡出 ----
@MainActor
enum Countdown {
    final class Model: ObservableObject { @Published var n = 3; @Published var pulse = false }
    struct V: View {
        @ObservedObject var m: Model
        var body: some View {
            ZStack {
                Circle().fill(.black.opacity(0.55)).frame(width: 190, height: 190)
                Text("\(m.n)").font(.system(size: 110, weight: .semibold, design: .rounded)).monospacedDigit().foregroundStyle(.white)
                    .scaleEffect(m.pulse ? 1 : 0.7).opacity(m.pulse ? 1 : 0)
            }.frame(width: 220, height: 220)
        }
    }
    static func panel(_ m: Model, on screen: NSScreen?) -> NSPanel {
        let p = NSPanel(contentRect: CGRect(x: 0, y: 0, width: 220, height: 220), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false; p.backgroundColor = .clear; p.hasShadow = false
        p.level = .screenSaver; p.ignoresMouseEvents = true; p.sharingType = .none
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.contentView = NSHostingView(rootView: V(m: m))
        if let f = (screen ?? NSScreen.main)?.frame { p.setFrameOrigin(CGPoint(x: f.midX - 110, y: f.midY - 110)) }
        return p
    }
    // 倒计时走完返回 true；被取消（Esc / 再按快捷键）返回 false
    static func run(seconds: Int, display: CGDirectDisplayID?, cancelled: @escaping () -> Bool) async -> Bool {
        let screen = NSScreen.screens.first { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == display }
        let m = Model(), p = panel(m, on: screen)
        var esc = false
        let mon = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in if e.keyCode == 53 { esc = true; return nil }; return e }
        defer { if let mon { NSEvent.removeMonitor(mon) }; p.orderOut(nil) }
        p.orderFrontRegardless()
        for n in stride(from: seconds, through: 1, by: -1) {
            m.n = n; m.pulse = false
            withAnimation(.easeOut(duration: 0.25)) { m.pulse = true }
            for k in 0..<20 {
                if esc || cancelled() { return false }
                if k == 15 { withAnimation(.easeIn(duration: 0.2)) { m.pulse = false } }
                try? await Task.sleep(for: .milliseconds(50))
            }
        }
        return !(esc || cancelled())
    }
    // 自测：COUNTDOWNSHOT=图片路径 → 不上屏，把倒计时画面画成图片后退出
    static func selfTest() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["COUNTDOWNSHOT"] else { return false }
        let m = Model(); m.n = 3; m.pulse = true
        let v = NSHostingView(rootView: V(m: m).background(Color(white: 0.85)))
        v.frame = CGRect(x: 0, y: 0, width: 220, height: 220)
        let w = NSWindow(contentRect: v.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        w.contentView = v; w.setFrameOrigin(CGPoint(x: -20000, y: -20000)); w.orderBack(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            if let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
                v.cacheDisplay(in: v.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
            }
            print("COUNTDOWNSHOT OK", path); exit(0)
        }
        return true
    }
}

extension Recorder {
    // 自测：ICONSTEST=1 → 列出「隐藏桌面图标」会排除的窗口（访达的桌面层）后退出
    static func iconsTest() -> Bool {
        guard ProcessInfo.processInfo.environment["ICONSTEST"] != nil else { return false }
        Task {
            do {
                let all = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                for w in all.windows where w.windowLayer < 0 || w.owningApplication?.bundleIdentifier == "com.apple.finder" {
                    print(isDesktopIcons(w) ? "EXCLUDE" : "keep   ", "layer", w.windowLayer, w.owningApplication?.bundleIdentifier ?? "?", "\"\(w.title ?? "")\"", Int(w.frame.width), "x", Int(w.frame.height))
                }
                print("ICONSTEST OK")
            } catch { print("ICONSTEST FAIL", error) }
            exit(0)
        }
        return true
    }
}

