// 主窗口的「最近的 Studio 素材」、把素材文件夹拖到程序坞图标 / 主窗口就打开剪辑，以及录制时自动开勿扰
import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

// ---- 最近的素材 ----
struct Footage: Identifiable, Equatable {
    let dir: URL
    var id: URL { dir }
    var name: String { dir.lastPathComponent }
    let date: Date
    var duration: Double?
    var thumb: NSImage?
    static func == (a: Footage, b: Footage) -> Bool { a.dir == b.dir && a.date == b.date && a.duration == b.duration && (a.thumb == nil) == (b.thumb == nil) }
    // 是不是一份 Studio 素材（有轨迹和原始画面）
    static func valid(_ dir: URL) -> Bool {
        let fm = FileManager.default
        return fm.fileExists(atPath: dir.appendingPathComponent("track.json").path) && fm.fileExists(atPath: dir.appendingPathComponent("raw.mp4").path)
    }
}

@MainActor final class RecentFootage: ObservableObject {
    static let shared = RecentFootage()
    @Published var items: [Footage] = []
    private var thumbs: [URL: (NSImage, Double)] = [:]
    static let limit = 8

    func refresh() {
        let fm = FileManager.default
        let dirs = (try? fm.contentsOfDirectory(at: Recorder.studioFolder, includingPropertiesForKeys: [.contentModificationDateKey], options: [.skipsHiddenFiles])) ?? []
        let list = dirs.filter(Footage.valid).map { d -> Footage in
            let raw = d.appendingPathComponent("raw.mp4")
            let date = (try? raw.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            return Footage(dir: d, date: date, duration: thumbs[d]?.1, thumb: thumbs[d]?.0)
        }.sorted { $0.date > $1.date }.prefix(Self.limit)
        items = Array(list)
        for f in items where thumbs[f.dir] == nil { Task { await loadThumb(f.dir) } }
    }
    private func loadThumb(_ d: URL) async {
        let a = AVURLAsset(url: d.appendingPathComponent("raw.mp4"))
        let dur = (try? await a.load(.duration).seconds) ?? 0
        let g = AVAssetImageGenerator(asset: a)
        g.maximumSize = CGSize(width: 320, height: 200); g.appliesPreferredTrackTransform = true
        let at = CMTime(seconds: min(1, dur / 2), preferredTimescale: 600)
        guard let (cg, _) = try? await g.image(at: at) else { return }
        let img = NSImage(cgImage: cg, size: CGSize(width: cg.width, height: cg.height))
        thumbs[d] = (img, dur)
        if let i = items.firstIndex(where: { $0.dir == d }) { items[i].thumb = img; items[i].duration = dur }
    }
    // 移到废纸篓（先确认）
    func trash(_ f: Footage) {
        let a = NSAlert()
        a.messageText = L("把「\(f.name)」移到废纸篓？", "Move “\(f.name)” to the Trash?")
        a.informativeText = L("这份素材删了就不能再打开剪辑（已经导出的成片不受影响）。", "You won't be able to edit this footage again (exported videos aren't affected).")
        a.addButton(withTitle: L("移到废纸篓", "Move to Trash")); a.addButton(withTitle: L("取消", "Cancel"))
        guard a.runModal() == .alertFirstButtonReturn else { return }
        try? FileManager.default.trashItem(at: f.dir, resultingItemURL: nil)
        thumbs[f.dir] = nil
        refresh()
    }
}

struct RecentFootageView: View {
    @ObservedObject var recents = RecentFootage.shared
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(L("最近的 Studio 素材", "Recent Studio Footage")).font(.headline)
                Spacer()
                Button(L("打开其他…", "Open Other…")) { openStudioFootage() }.controlSize(.small)
            }
            if recents.items.isEmpty {
                Text(L("开着「Studio 效果」录一段，这里就会出现；也可以把素材文件夹拖到这里或程序坞图标上", "Record with Studio effects on and it shows up here — or drop a footage folder here or onto the Dock icon"))
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else {
                ScrollView(.horizontal, showsIndicators: true) {
                    HStack(spacing: 12) {
                        ForEach(recents.items) { f in FootageCard(f: f) }
                    }.padding(.bottom, 6)
                }
            }
        }
    }
}

struct FootageCard: View {
    let f: Footage
    @State private var hover = false
    var body: some View {
        Button { Task { await StudioEditor.open(f.dir) } } label: {
            VStack(alignment: .leading, spacing: 4) {
                ZStack(alignment: .bottomTrailing) {
                    Group {
                        if let t = f.thumb { Image(nsImage: t).resizable().aspectRatio(contentMode: .fill) }
                        else { Rectangle().fill(Color.primary.opacity(0.08)).overlay(ProgressView().controlSize(.small)) }
                    }
                    .frame(width: 150, height: 92).clipped()
                    if let d = f.duration {
                        Text(String(format: "%d:%02d", Int(d) / 60, Int(d) % 60)).font(.caption2.monospacedDigit().weight(.semibold))
                            .padding(.horizontal, 5).padding(.vertical, 2).background(.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 4)).foregroundStyle(.white)
                            .padding(5)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(hover ? Color.accentColor : Color.primary.opacity(0.12), lineWidth: hover ? 2 : 1))
                Text(f.name.replacingOccurrences(of: "Recording ", with: "").replacingOccurrences(of: "录屏 ", with: ""))
                    .font(.caption).lineLimit(1).frame(width: 150, alignment: .leading)
                Text(f.date.formatted(date: .abbreviated, time: .shortened)).font(.caption2).foregroundStyle(.secondary)
            }
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(L("点一下打开剪辑窗口", "Click to open in the editor"))
        .contextMenu {
            Button(L("打开剪辑", "Open in Editor")) { Task { await StudioEditor.open(f.dir) } }
            Button(L("在访达中显示", "Show in Finder")) { NSWorkspace.shared.activateFileViewerSelecting([f.dir]) }
            Divider()
            Button(L("移到废纸篓…", "Move to Trash…"), role: .destructive) { RecentFootage.shared.trash(f) }
        }
    }
}

// ---- 拖到程序坞图标 / 主窗口：素材文件夹就打开剪辑 ----
@MainActor enum FootageOpener {
    // 返回：这些里面有没有能打开的
    @discardableResult static func open(_ urls: [URL]) -> Bool {
        var any = false
        for u in urls {
            var d = u
            if !Footage.valid(d), Footage.valid(u.deletingLastPathComponent()) { d = u.deletingLastPathComponent() }   // 拖的是素材夹里的文件
            guard Footage.valid(d) else { continue }
            any = true
            Task { await StudioEditor.open(d) }
        }
        if !any && !urls.isEmpty {
            let a = NSAlert(); a.messageText = L("这不是 Studio 素材", "This isn't Studio footage")
            a.informativeText = L("Studio 素材在「影片 / 录屏 / Studio 素材」里，每次录制一个文件夹。", "Studio footage lives in Movies / 录屏 / Studio 素材, one folder per recording.")
            a.runModal()
        }
        return any
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func application(_ app: NSApplication, open urls: [URL]) {
        MainActor.assumeIsolated { _ = FootageOpener.open(urls.filter(\.isFileURL)) }
    }
    func applicationDidFinishLaunching(_ n: Notification) {
        MainActor.assumeIsolated { Focus.recoverIfNeeded() }
    }
}

// ---- 录制时自动开勿扰 ----
// macOS 没有给普通程序开关「专注模式」的接口，只能借快捷指令：用户点一次「设置」，导入两个快捷指令（开 / 关勿扰），
// 之后录制开始时跑「录屏·开启勿扰」，结束时跑「录屏·关闭勿扰」。程序中途崩了，下次打开时补跑一次关闭
@MainActor enum Focus {
    static let onName = L("录屏·开启勿扰", "Recorder · DND On")
    static let offName = L("录屏·关闭勿扰", "Recorder · DND Off")
    nonisolated(unsafe) static var installed: Bool?
    static var enabled: Bool {
        get { UserDefaults.standard.bool(forKey: "dndWhileRecording") }
        set { UserDefaults.standard.set(newValue, forKey: "dndWhileRecording") }
    }
    private static var active: Bool {
        get { UserDefaults.standard.bool(forKey: "dndActive") }
        set { UserDefaults.standard.set(newValue, forKey: "dndActive") }
    }

    // 两个快捷指令都在不在（每次看主窗口时查一遍，很快）
    static func check() async -> Bool {
        let out = await run("/usr/bin/shortcuts", ["list"]) ?? ""
        let names = Set(out.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) })
        let ok = names.contains(onName) && names.contains(offName)
        installed = ok
        return ok
    }
    // 生成两个快捷指令文件，签名后用「快捷指令」打开（会问要不要添加，点两次「添加快捷指令」）
    static func install() async {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("recorder-focus-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for (name, on) in [(onName, true), (offName, false)] {
            let wf: [String: Any] = [
                "WFWorkflowClientVersion": "2302.0.4", "WFWorkflowMinimumClientVersion": 900, "WFWorkflowMinimumClientVersionString": "900",
                "WFWorkflowIcon": ["WFWorkflowIconStartColor": 4_282_601_983, "WFWorkflowIconGlyphNumber": 59511],
                "WFWorkflowImportQuestions": [], "WFWorkflowTypes": [], "WFWorkflowInputContentItemClasses": [], "WFWorkflowOutputContentItemClasses": [],
                "WFWorkflowActions": [[
                    "WFWorkflowActionIdentifier": "is.workflow.actions.dnd.set",
                    "WFWorkflowActionParameters": ["Enabled": on ? 1 : 0,
                                                   "FocusModes": ["Identifier": "com.apple.donotdisturb.mode.default", "DisplayString": L("勿扰模式", "Do Not Disturb")]],
                ]],
            ]
            let raw = dir.appendingPathComponent("\(name).unsigned.shortcut"), signed = dir.appendingPathComponent("\(name).shortcut")
            guard let d = try? PropertyListSerialization.data(fromPropertyList: wf, format: .binary, options: 0), (try? d.write(to: raw)) != nil else { continue }
            _ = await run("/usr/bin/shortcuts", ["sign", "--mode", "people-who-know-me", "-i", raw.path, "-o", signed.path])
            if FileManager.default.fileExists(atPath: signed.path) { NSWorkspace.shared.open(signed) }
        }
    }
    static func begin() {
        guard enabled, installed == true else { return }
        active = true
        Task { _ = await run("/usr/bin/shortcuts", ["run", onName]) }
    }
    static func end() {
        guard active else { return }
        active = false
        Task { _ = await run("/usr/bin/shortcuts", ["run", offName]) }
    }
    // 上次录到一半程序没了：勿扰还开着，关掉
    static func recoverIfNeeded() { if active { end() } }

    nonisolated static func run(_ exe: String, _ args: [String]) async -> String? {
        await withCheckedContinuation { c in
            DispatchQueue.global().async {
                let p = Process(), pipe = Pipe()
                p.executableURL = URL(fileURLWithPath: exe); p.arguments = args
                p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
                do { try p.run() } catch { c.resume(returning: nil); return }
                let d = pipe.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                c.resume(returning: p.terminationStatus == 0 ? String(decoding: d, as: UTF8.self) : nil)
            }
        }
    }
}

// 主窗口里的勿扰开关：没装快捷指令时给「设置…」
struct FocusToggle: View {
    @State private var on = Focus.enabled
    @State private var ready = Focus.installed
    var body: some View {
        HStack(spacing: 6) {
            Toggle(L("录制时开启勿扰", "Do Not Disturb while recording"), isOn: $on)
                .onChange(of: on) { _, v in Focus.enabled = v; if v { Task { ready = await Focus.check() } } }
                .help(L("录的时候不弹通知；录完恢复。借两个快捷指令开关勿扰模式", "No notifications pop up while recording; restored afterwards. Uses two Shortcuts to toggle Do Not Disturb"))
            if on && ready == false {
                Button(L("设置…", "Set Up…")) { Task { await Focus.install(); try? await Task.sleep(for: .seconds(8)); ready = await Focus.check() } }
                    .controlSize(.small)
                    .help(L("会打开「快捷指令」，点两次「添加快捷指令」就好", "Opens Shortcuts — click “Add Shortcut” twice"))
            }
        }
        .task { ready = await Focus.check() }
    }
}

// 自测：MAINSHOT=图片路径 → 不抢前台，把主窗口画成图片（看「最近的 Studio 素材」那一栏）后退出
@MainActor enum MainShot {
    static func run() -> Bool {
        guard let path = ProcessInfo.processInfo.environment["MAINSHOT"] else { return false }
        NSApp.setActivationPolicy(.accessory)
        let dark = ProcessInfo.processInfo.environment["EDITOR_APPEARANCE"] == "dark"
        let v = NSHostingView(rootView: ContentView(rec: Recorder()).frame(width: 720, height: 760).background(Color(nsColor: .windowBackgroundColor)))
        v.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let w = NSWindow(contentRect: CGRect(x: -20000, y: -20000, width: 720, height: 760), styleMask: [.borderless], backing: .buffered, defer: false)
        w.contentView = v; w.orderBack(nil)
        Task { @MainActor in
            RecentFootage.shared.refresh()
            try? await Task.sleep(for: .seconds(3))
            v.layoutSubtreeIfNeeded()
            if let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
                v.cacheDisplay(in: v.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
                print("MAINSHOT OK", path)
            }
            exit(0)
        }
        return true
    }
}
