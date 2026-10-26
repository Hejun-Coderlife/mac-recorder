// 录屏：选一个屏幕或窗口，连同电脑播放的声音（可选麦克风）一起录成 mp4
// 用苹果的 ScreenCaptureKit 拿画面和声音，AVAssetWriter 自己写成 mp4（视频 H.264；电脑声音、麦克风各一条 AAC 音轨）；全局快捷键 ⌥⌘R 开始 / 停止
// （试过 macOS 15 的 SCRecordingOutput 一步写文件：报 RPRecordingErrorDomain -5814、没有原因，改成自己写）
// 编译：./build.sh（只要命令行开发工具，不用 Xcode）
import SwiftUI
import ScreenCaptureKit
import AVFoundation
import Carbon.HIToolbox
import Accelerate

// 可录的东西：整个屏幕或某个窗口
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
        guard started else { writer.cancelWriting(); throw NSError(domain: "录屏", code: 2, userInfo: [NSLocalizedDescriptionKey: "没有录到画面（选的窗口被挡住或最小化了？）"]) }
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
            for (i, d) in c.displays.enumerated() {
                list.append(Source(id: "d\(d.displayID)", title: c.displays.count > 1 ? "整个屏幕 \(i + 1)" : "整个屏幕",
                                   detail: "\(d.width) × \(d.height)", isDisplay: true))
            }
            let me = Bundle.main.bundleIdentifier
            let wins = c.windows.filter { w in
                w.windowLayer == 0 && w.frame.width >= 200 && w.frame.height >= 150 &&
                w.owningApplication?.bundleIdentifier != me && !(w.title ?? "").isEmpty
            }.sorted { ($0.owningApplication?.applicationName ?? "") < ($1.owningApplication?.applicationName ?? "") }
            for w in wins {
                list.append(Source(id: "w\(w.windowID)", title: w.owningApplication?.applicationName ?? "窗口",
                                   detail: w.title ?? "", isDisplay: false))
            }
            sources = list
            if selection == nil || !list.contains(where: { $0.id == selection }) { selection = list.first?.id }
            if message.hasPrefix("需要") { message = "" }
        } catch {
            message = "需要屏幕录制权限：系统设置 → 隐私与安全性 → 屏幕与系统录音，打开「录屏」，然后退出并重新打开本程序"
        }
    }

    func toggle() {
        guard !busy else { return }
        Task { recording ? await stop() : await start() }
    }

    private func start() async {
        busy = true; defer { busy = false }
        await refresh()
        guard let c = content, let sel = selection else { message = "先选一个要录的屏幕或窗口"; return }
        if microphone {
            let ok = await AVCaptureDevice.requestAccess(for: .audio)
            if !ok { message = "没有麦克风权限：系统设置 → 隐私与安全性 → 麦克风，打开「录屏」"; return }
        }
        // 画面范围
        let filter: SCContentFilter
        if sel.hasPrefix("d"), let d = c.displays.first(where: { "d\($0.displayID)" == sel }) {
            // 整个屏幕：把本程序自己的窗口排除掉
            let mine = c.applications.filter { $0.bundleIdentifier == Bundle.main.bundleIdentifier }
            filter = SCContentFilter(display: d, excludingApplications: mine, exceptingWindows: [])
        } else if let w = c.windows.first(where: { "w\($0.windowID)" == sel }) {
            filter = SCContentFilter(desktopIndependentWindow: w)
        } else { message = "这个窗口已经关掉了，换一个"; await refresh(); return }

        let cfg = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        // 宽高按像素、取偶数（H.264 要求）
        cfg.width = max(2, Int(filter.contentRect.width * scale) / 2 * 2)
        cfg.height = max(2, Int(filter.contentRect.height * scale) / 2 * 2)
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
        let url = Recorder.folder.appendingPathComponent("录屏 \(fmt.string(from: Date())).mp4")
        let w: Writer
        do { w = try Writer(url: url, width: cfg.width, height: cfg.height, system: systemAudio, mic: microphone) }
        catch { message = "开始录制失败：\(error.localizedDescription)"; return }
        w.onError = { [weak self] e in Task { @MainActor in self?.fail(e) } }
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        do {
            try s.addStreamOutput(w, type: .screen, sampleHandlerQueue: w.queue)
            if systemAudio { try s.addStreamOutput(w, type: .audio, sampleHandlerQueue: w.queue) }
            if microphone { try s.addStreamOutput(w, type: .microphone, sampleHandlerQueue: w.queue) }
            try await s.startCapture()
        } catch {
            message = "开始录制失败：\(error.localizedDescription)"
            try? FileManager.default.removeItem(at: url)
            return
        }
        stream = s; writer = w; lastFile = url
        recording = true; startedAt = Date(); elapsed = 0; message = ""
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
        message = "录制出错：\(e.localizedDescription)"
        if recording { toggle() }
    }

    fileprivate func finished(_ error: Error?) {
        if let error { message = "保存失败：\(error.localizedDescription)"; return }
        if let f = lastFile, FileManager.default.fileExists(atPath: f.path) {
            message = "已保存：\(f.lastPathComponent)"
            NSWorkspace.shared.activateFileViewerSelecting([f])
        }
    }
}

extension Recorder: SCStreamDelegate {
    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in
            // 被录的窗口关掉了之类：自动停下并保存
            if self.recording { self.message = "录制中断：\(error.localizedDescription)"; self.toggle() }
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

struct ContentView: View {
    @ObservedObject var rec: Recorder
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("录什么").font(.headline)
                Spacer()
                Button("刷新列表") { Task { await rec.refresh() } }.disabled(rec.recording)
            }
            List(rec.sources, selection: $rec.selection) { s in
                HStack(spacing: 10) {
                    Image(systemName: s.isDisplay ? "display" : "macwindow").frame(width: 20)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(s.title).font(.body.weight(.medium))
                        Text(s.detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }.tag(s.id).padding(.vertical, 2)
            }
            .frame(minHeight: 220)
            .disabled(rec.recording)

            HStack(spacing: 22) {
                Toggle("录电脑声音", isOn: $rec.systemAudio)
                Toggle("录麦克风", isOn: $rec.microphone)
                Toggle("显示鼠标", isOn: $rec.showCursor)
            }.disabled(rec.recording)

            HStack(alignment: .center, spacing: 16) {
                Button(action: rec.toggle) {
                    Label(rec.recording ? "停止" : "开始录制", systemImage: rec.recording ? "stop.fill" : "record.circle")
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
                Text("⌥⌘R 开始 / 停止").font(.callout).foregroundStyle(.secondary)
            }

            HStack {
                Text(rec.message.isEmpty ? "保存到：影片 / 录屏" : rec.message)
                    .font(.callout).foregroundStyle(rec.message.contains("失败") || rec.message.contains("需要") || rec.message.contains("没有") ? .red : .secondary)
                    .lineLimit(2)
                Spacer()
                Button("打开文件夹") { NSWorkspace.shared.open(Recorder.folder) }
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
        WindowGroup("录屏") {
            ContentView(rec: rec)
                .onAppear {
                    HotKey.action = { [weak rec] in rec?.toggle() }
                    // 自测：AUTOTEST=秒数 → 录整个屏幕这么久，结果写到 /tmp/recorder_test.txt，然后退出
                    if let t = ProcessInfo.processInfo.environment["AUTOTEST"], let secs = Double(t) {
                        Task { @MainActor in
                            try? await Task.sleep(for: .seconds(1.5))
                            rec.selection = rec.sources.first(where: { $0.isDisplay })?.id
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
