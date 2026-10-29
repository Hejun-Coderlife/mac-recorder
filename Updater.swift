// 自动更新：启动后在后台查 GitHub 最新 Release（6 小时内查过就不查），有新版就在窗口底部提示，
// 点「更新」才下载安装包，替换当前这个 .app 后重新打开。不会悄悄自己装
import AppKit

@MainActor final class Updater: ObservableObject {
    static let shared = Updater(repo: "Hejun-Coderlife/mac-recorder", asset: "ScreenRecorder.dmg")

    enum State: Equatable { case idle, available(String), working(String), failed(String), upToDate }
    @Published var state: State = .idle

    let repo: String, asset: String
    private var download: URL?
    init(repo: String, asset: String) { self.repo = repo; self.asset = asset }

    private var env: [String: String] { ProcessInfo.processInfo.environment }
    var current: String { env["UPDATE_FAKE_VERSION"] ?? (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0") }
    var releasePage: URL { URL(string: "https://github.com/\(repo)/releases/latest")! }

    // 版本号逐段按数字比：1.10 比 1.9 新
    static func newer(_ a: String, than b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }

    func checkOnLaunch() {
        let key = "updateCheckedAt", now = Date().timeIntervalSince1970
        if env["UPDATE_TEST"] == nil, now - UserDefaults.standard.double(forKey: key) < 6 * 3600 { return }
        UserDefaults.standard.set(now, forKey: key)
        Task { await check(manual: false) }
    }

    func check(manual: Bool) async {
        if manual { state = .working(L("正在检查更新…", "Checking for updates…")) }
        do {
            var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!)
            req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            req.timeoutInterval = 20
            let (data, _) = try await URLSession.shared.data(for: req)
            struct Rel: Decodable { let tag_name: String; let assets: [Asset] }
            struct Asset: Decodable { let name: String; let browser_download_url: URL }
            let rel = try JSONDecoder().decode(Rel.self, from: data)
            let v = rel.tag_name.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            guard Self.newer(v, than: current), let a = rel.assets.first(where: { $0.name == asset }) else {
                state = manual ? .upToDate : .idle; return
            }
            download = a.browser_download_url
            state = .available(v)
        } catch {
            state = manual ? .failed(L("检查更新失败：", "Couldn't check for updates: ") + error.localizedDescription) : .idle
        }
    }

    // 下载 → 挂载 dmg（或解 zip）→ 拷出 .app → 核对签名和版本 → 换掉当前这个 .app → 重新打开
    func install() async {
        guard case .available(let v) = state, let url = download else { return }
        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("update-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: work) }
        do {
            try fm.createDirectory(at: work, withIntermediateDirectories: true)
            state = .working(L("正在下载 v\(v)…", "Downloading v\(v)…"))
            let (tmp, resp) = try await URLSession.shared.download(from: url)
            guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw Self.fail(L("下载失败", "Download failed")) }
            let pkg = work.appendingPathComponent(asset)
            try fm.moveItem(at: tmp, to: pkg)

            state = .working(L("正在安装 v\(v)…", "Installing v\(v)…"))
            let staged = work.appendingPathComponent("new.app")
            let target = Bundle.main.bundleURL
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
            _ = try fm.replaceItemAt(target, withItemAt: staged)

            if env["UPDATE_NORELAUNCH"] != nil { state = .upToDate; return }
            // 等这个进程退出后再打开新版
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/sh")
            p.arguments = ["-c", "while kill -0 \(getpid()) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"", target.path]
            try p.run()
            NSApp.terminate(nil)
        } catch {
            state = .failed(L("更新失败：", "Update failed: ") + error.localizedDescription)
        }
    }

    // 自测：UPDATE_TEST=1 [UPDATE_FAKE_VERSION=1.0] [UPDATE_NORELAUNCH=1] → 查、下载、换掉当前 .app，结果写 /tmp/update_test.txt 后退出
    static func selfTest() -> Bool {
        guard ProcessInfo.processInfo.environment["UPDATE_TEST"] != nil else { return false }
        Task { @MainActor in
            let u = Updater.shared
            await u.check(manual: true)
            var line = "check: \(u.state)"
            if case .available = u.state { await u.install(); line += "\ninstall: \(u.state)" }
            try? line.write(toFile: "/tmp/update_test.txt", atomically: true, encoding: .utf8)
            NSApp.terminate(nil)
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
