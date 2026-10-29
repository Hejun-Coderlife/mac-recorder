// 自动更新（后台静默）：打开时和开着的时候每 6 小时查一次 GitHub 最新 Release，有新版就在后台下载、
// 核对好先放着；退出程序时换成新版，下次打开就是新的。正在录制 / 生成时不下载。想马上用可以点「现在重启」
import AppKit

@MainActor final class Updater: ObservableObject {
    static let shared = Updater(repo: "Hejun-Coderlife/mac-recorder", asset: "ScreenRecorder.dmg")

    enum State: Equatable { case idle, working(String), ready(String), updated(String), failed(String), upToDate }
    @Published var state: State = .idle

    let repo: String, asset: String
    var isBusy: () -> Bool = { false }          // 正在录制 / 生成 Studio 时返回 true
    private var timer: Timer?
    init(repo: String, asset: String) { self.repo = repo; self.asset = asset }

    private var env: [String: String] { ProcessInfo.processInfo.environment }
    var current: String { env["UPDATE_FAKE_VERSION"] ?? (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0") }
    var releasePage: URL { URL(string: "https://github.com/\(repo)/releases/latest")! }

    // 下好的新版放这里：退出时拿它换掉当前 .app
    private var stageDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "Recorder").appendingPathComponent("Update")
    }
    private var stagedApp: URL { stageDir.appendingPathComponent("new.app") }
    private var stagedVersion: String? {
        guard FileManager.default.fileExists(atPath: stagedApp.path) else { return nil }
        return Bundle(url: stagedApp)?.infoDictionary?["CFBundleShortVersionString"] as? String
    }

    // 版本号逐段按数字比：1.10 比 1.9 新
    static func newer(_ a: String, than b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }

    func start() {
        // 上次退出时换上了新版：提示一句
        if let v = UserDefaults.standard.string(forKey: "updatedTo") {
            UserDefaults.standard.removeObject(forKey: "updatedTo")
            if v == current { state = .updated(v) }
        }
        if let v = stagedVersion {
            if Self.newer(v, than: current) { state = .ready(v) } else { try? FileManager.default.removeItem(at: stageDir) }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { _ in
            MainActor.assumeIsolated { _ = Updater.shared.applyStaged() }
        }
        tick()
        timer = Timer.scheduledTimer(withTimeInterval: 30 * 60, repeats: true) { _ in Task { @MainActor in Updater.shared.tick() } }
    }

    // 每半小时看一眼：离上次查超过 6 小时、又没在忙，就查一次
    private func tick() {
        let key = "updateCheckedAt", now = Date().timeIntervalSince1970
        guard !isBusy(), now - UserDefaults.standard.double(forKey: key) >= 6 * 3600 else { return }
        if case .working = state { return }
        UserDefaults.standard.set(now, forKey: key)
        Task { await check(manual: false) }
    }

    func check(manual: Bool) async {
        if manual { state = .working(L("正在检查更新…", "Checking for updates…")) }
        do {
            var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/\(env["UPDATE_RELEASE"] ?? "latest")")!)   // 自测可指定 tags/v1.2
            req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            req.timeoutInterval = 20
            let (data, _) = try await URLSession.shared.data(for: req)
            struct Rel: Decodable { let tag_name: String; let assets: [Asset] }
            struct Asset: Decodable { let name: String; let browser_download_url: URL }
            let rel = try JSONDecoder().decode(Rel.self, from: data)
            let v = rel.tag_name.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            guard Self.newer(v, than: current), let a = rel.assets.first(where: { $0.name == asset }) else {
                if manual { state = .upToDate } else if case .working = state { state = .idle }
                return
            }
            if let s = stagedVersion, !Self.newer(v, than: s) { state = .ready(s); return }   // 已经下好了
            try await stage(a.browser_download_url, version: v, manual: manual)
            state = .ready(v)
        } catch {
            // 后台查不到、下不动就算了，下次再试；手动检查才提示
            if manual { state = .failed(L("更新失败：", "Update failed: ") + error.localizedDescription) }
            else if case .working = state { state = .idle }
        }
    }

    // 下载 → 挂载 dmg（或解 zip）→ 拷出 .app → 核对签名、程序、版本 → 放到 stageDir
    private func stage(_ url: URL, version v: String, manual: Bool) async throws {
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("update-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: work) }
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        if manual { state = .working(L("正在下载 v\(v)…", "Downloading v\(v)…")) }
        let (tmp, resp) = try await URLSession.shared.download(from: url)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw Self.fail(L("下载失败", "Download failed")) }
        let pkg = work.appendingPathComponent(asset)
        try fm.moveItem(at: tmp, to: pkg)
        let staged = work.appendingPathComponent("new.app")
        try await Task.detached {
            if pkg.pathExtension == "dmg" {
                let mnt = work.appendingPathComponent("mnt")
                try Self.run("/usr/bin/hdiutil", ["attach", pkg.path, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mnt.path])
                defer { _ = try? Self.run("/usr/bin/hdiutil", ["detach", mnt.path, "-force"]) }
                guard let app = try fm.contentsOfDirectory(atPath: mnt.path).first(where: { $0.hasSuffix(".app") })
                else { throw Self.fail(L("安装包里没有程序", "No app in the package")) }
                try Self.run("/usr/bin/ditto", [mnt.appendingPathComponent(app).path, staged.path])
            } else {
                let out = work.appendingPathComponent("unzip")
                try Self.run("/usr/bin/ditto", ["-x", "-k", pkg.path, out.path])
                guard let app = try fm.contentsOfDirectory(atPath: out.path).first(where: { $0.hasSuffix(".app") })
                else { throw Self.fail(L("安装包里没有程序", "No app in the package")) }
                try fm.moveItem(at: out.appendingPathComponent(app), to: staged)
            }
            try Self.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", staged.path])
        }.value
        // 必须是同一个程序、而且不比现在的旧
        let info = Bundle(url: staged)?.infoDictionary
        guard info?["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              let nv = info?["CFBundleShortVersionString"] as? String, !Self.newer(current, than: nv)
        else { throw Self.fail(L("下载的安装包不对", "The downloaded package doesn't match")) }
        try? fm.removeItem(at: stageDir)
        try fm.createDirectory(at: stageDir, withIntermediateDirectories: true)
        try fm.moveItem(at: staged, to: stagedApp)
    }

    // 退出时（或点「现在重启」）把下好的新版换上去
    @discardableResult func applyStaged() -> Bool {
        guard let v = stagedVersion else { return false }
        do {
            _ = try FileManager.default.replaceItemAt(Bundle.main.bundleURL, withItemAt: stagedApp)
            try? FileManager.default.removeItem(at: stageDir)
            UserDefaults.standard.set(v, forKey: "updatedTo")
            UserDefaults.standard.synchronize()
            return true
        } catch {
            return false
        }
    }

    func restartNow() {
        guard !isBusy(), applyStaged() else { return }
        // 等这个进程退出后再打开新版
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while kill -0 \(getpid()) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"", Bundle.main.bundleURL.path]
        try? p.run()
        NSApp.terminate(nil)
    }

    // 自测：UPDATE_TEST=1 [UPDATE_FAKE_VERSION=1.0] [UPDATE_RELEASE=tags/v1.2] → 后台那套流程：查、下载、放好，再模拟退出换上去；结果写 /tmp/update_test.txt 后退出
    static func selfTest() -> Bool {
        guard ProcessInfo.processInfo.environment["UPDATE_TEST"] != nil else { return false }
        Task { @MainActor in
            let u = Updater.shared
            await u.check(manual: false)
            var line = "check: \(u.state)"
            if case .ready = u.state { line += "\napply: \(u.applyStaged())" }
            try? line.write(toFile: "/tmp/update_test.txt", atomically: true, encoding: .utf8)
            exit(0)   // 不走 terminate，免得退出钩子再换一次
        }
        return true
    }

    nonisolated static func fail(_ s: String) -> NSError { NSError(domain: "Updater", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }
    @discardableResult nonisolated static func run(_ exe: String, _ args: [String]) throws -> String {
        let p = Process(), pipe = Pipe()
        p.executableURL = URL(fileURLWithPath: exe); p.arguments = args
        p.standardOutput = pipe; p.standardError = pipe
        try p.run()
        let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw fail("\(URL(fileURLWithPath: exe).lastPathComponent): \(out.trimmingCharacters(in: .whitespacesAndNewlines))") }
        return out
    }
}
