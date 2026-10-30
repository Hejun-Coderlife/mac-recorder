// 剪辑窗口「D 批」：背景音乐（选文件、音乐轨、预览）、建议加速（找空档和打字段、采纳 / 忽略）、推近速度
import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

extension StudioEditor {
    // ---- 背景音乐 ----
    var filmEnd: Double { timeMap.out(project.trimEnd) }
    func musicIndex(_ id: UUID?) -> Int? { project.music.clips.firstIndex { $0.id == id } }

    // 原声音的音量包络：打开时在后台算一次（压低音乐用）
    func loadMusicEnv() {
        let a = asset, audio = project.audio
        Task {
            let env = await MusicDuck.envelope(asset: a, audio: audio)
            self.musicEnv = env
            self.musicDuckKey = ""
        }
    }
    // 压低曲线（成片时间）：相关设置变了才重算
    func musicDuckCurve() -> [Float] {
        let p = project, m = timeMap
        let key = "\(p.music.duckLevel)|\(p.trimEnd)|\(p.cuts.map { "\($0.start),\($0.end),\($0.mode),\($0.rate)" })|\(p.voiceovers.map { "\($0.start),\($0.trimIn),\($0.trimOut),\($0.volume)" })|\(p.audio.voice)|\(p.audio.muted)|\(musicEnv?.count ?? -1)"
        if key != musicDuckKey {
            let voice = p.audio.voice > 0.001 ? p.voiceovers.filter { $0.volume > 0.001 }.map { $0.outRange(m) } : []
            musicDuckCache = MusicDuck.curve(level: p.music.duckLevel, filmEnd: m.out(p.trimEnd), voice: voice, env: p.audio.muted ? nil : musicEnv, map: m)
            musicDuckKey = key
        }
        return musicDuckCache
    }
    func musicTick(outTime o: Double) {
        guard !project.music.clips.isEmpty else { musicPlayer.stopAll(); return }
        musicPlayer.sync(dir: dir, track: project.music, filmEnd: filmEnd, outTime: o, duck: musicDuckCurve(),
                         playing: playing && StudioEditor.testShot == nil)
    }

    // 选一首音乐：拷进素材夹 music/（原文件挪走了也不怕），放在已有音乐的后面（没有就从片头开始）
    func chooseMusic() {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.audio, .mp3, .mpeg4Audio, .wav, .aiff]
        p.allowsMultipleSelection = false
        p.message = L("选一首背景音乐（mp3 / m4a / wav / aac）", "Choose background music (mp3 / m4a / wav / aac)")
        guard p.runModal() == .OK, let u = p.url else { return }
        Task { await addMusic(from: u) }
    }
    func addMusic(from u: URL) async {
        let mdir = dir.appendingPathComponent("music")
        try? FileManager.default.createDirectory(at: mdir, withIntermediateDirectories: true)
        let dst = mdir.appendingPathComponent(UUID().uuidString.prefix(8) + "-" + u.lastPathComponent)
        do { try FileManager.default.copyItem(at: u, to: dst) } catch { NSSound.beep(); return }
        guard let len = try? await AVURLAsset(url: dst).load(.duration).seconds, len > 0.2 else {
            try? FileManager.default.removeItem(at: dst); NSSound.beep(); return
        }
        let at = project.music.clips.map { $0.start + $0.length }.max() ?? project.trimStart
        let c = MusicClip(file: "music/" + dst.lastPathComponent, name: u.deletingPathExtension().lastPathComponent, start: min(at, max(project.trimStart, filmEnd - 0.5)),
                          trimIn: 0, trimOut: len, fileLength: len)
        edit { $0.music.clips.append(c) }
        selected = c.id
        loadPeaks(c.file)
    }
    func musicSlider(_ kp: WritableKeyPath<MusicTrack, Double>) -> Binding<Double> {
        Binding(get: { self.project.music[keyPath: kp] }, set: { v in self.dragBegin(); self.dragUpdate { $0.music[keyPath: kp] = v } })
    }
    func musicToggle(_ kp: WritableKeyPath<MusicTrack, Bool>) -> Binding<Bool> {
        Binding(get: { self.project.music[keyPath: kp] }, set: { v in self.edit { $0.music[keyPath: kp] = v } })
    }

    // ---- 建议加速 ----
    func suggestSpeed() {
        guard !suggesting else { return }
        suggesting = true; suggestNone = false; suggestions = []; selected = nil
        let r = project.trimStart...max(project.trimStart, project.trimEnd), raw = dir.appendingPathComponent("raw.mp4"), tr = track, cuts = project.cuts
        Task {
            let diffs = await SpeedSuggest.frameDiffs(raw: raw, range: r)
            let found = SpeedSuggest.find(track: tr, diffs: diffs, range: r, existing: cuts)
            self.suggestions = found
            self.suggesting = false
            if found.isEmpty {
                self.suggestNone = true
                try? await Task.sleep(for: .seconds(5))
                self.suggestNone = false
            }
        }
    }
    // 采纳：变成正式的快放段（能撤销）；和已有剪辑段重叠的跳过
    func acceptSuggestions(_ ids: [UUID]) {
        let pick = suggestions.filter { ids.contains($0.id) }
        edit { p in
            for g in pick where !p.cuts.contains(where: { $0.start < g.end && $0.end > g.start }) {
                p.cuts.append(CutSegment(start: max(g.start, p.trimStart), end: min(g.end, p.trimEnd), mode: .speed, rate: g.rate))
            }
            p.cuts.sort { $0.start < $1.start }
        }
        suggestions.removeAll { ids.contains($0.id) }
    }
    func ignoreSuggestions(_ ids: [UUID]) { suggestions.removeAll { ids.contains($0.id) } }
}

// ---- 视图 ----

// 音乐轨上的一段：拖中间挪（按成片时间），拖两头剪掉音乐的开头 / 结尾；单击选中
struct MusicBlock: View {
    @ObservedObject var ed: StudioEditor
    let clip: MusicClip
    let w: Double, d: Double
    let peaks: [Float]
    static let tint = Color(red: 0.86, green: 0.48, blue: 0.18)
    private let minLen = 0.5

    var body: some View {
        let sel = ed.selected == clip.id
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(Self.tint.opacity(sel ? 0.95 : 0.72))
            Canvas { ctx, size in
                guard !peaks.isEmpty else { return }
                let a = Int(clip.trimIn * 100), b = min(peaks.count, Int(clip.trimOut * 100))
                guard b > a else { return }
                let n = Int(size.width / 2), mid = size.height / 2
                var p = Path()
                for k in 0..<max(1, n) {
                    let i0 = a + (b - a) * k / max(1, n), i1 = max(i0 + 1, a + (b - a) * (k + 1) / max(1, n))
                    let v = Double(peaks[i0..<min(b, i1)].max() ?? 0)
                    let hh = max(0.5, min(1, v * 1.4) * (size.height / 2 - 3))
                    p.addRect(CGRect(x: Double(k) * 2, y: mid - hh, width: 1.2, height: hh * 2))
                }
                ctx.fill(p, with: .color(.white.opacity(0.6)))
            }
            .clipShape(RoundedRectangle(cornerRadius: 5))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.clear, lineWidth: 2)
            HStack(spacing: 3) {
                HStack(spacing: 3) {
                    Image(systemName: "music.note").font(.system(size: 9, weight: .bold))
                    Text(clip.name).font(.system(size: 9, weight: .semibold)).lineLimit(1)
                }
                .padding(.horizontal, 4).padding(.vertical, 1).background(Capsule().fill(Self.tint))
                Spacer(minLength: 0)
            }
            .foregroundStyle(.white).padding(.horizontal, 10)
            HStack { edge(isStart: true); Spacer(minLength: 0); edge(isStart: false) }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = clip.id; ed.picking = false }
        .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .named(TimelineStrip.space))
            .onChanged { v in
                ed.selected = clip.id
                ed.dragBegin()
                let dt = v.translation.width / w * d
                ed.dragUpdate { p in Self.move(&p, id: clip.id, dt: dt, duration: d) }
            }
            .onEnded { _ in ed.dragEnd() })
        .help(L("背景音乐：拖动挪位置，拖两头剪掉不要的开头结尾", "Music: drag to move, drag the ends to trim"))
    }

    // 按住时的工程 p：音乐的起点（成片时间）跟着时间轴上的位置（素材时间）走
    static func move(_ p: inout StudioProject, id: UUID, dt: Double, duration: Double) {
        guard let i = p.music.clips.firstIndex(where: { $0.id == id }) else { return }
        let m = TimeMap(p), c = p.music.clips[i]
        let src = min(max(0, m.src(c.start) + dt), duration)
        p.music.clips[i].start = max(0, m.out(src))
    }
    static func trim(_ p: inout StudioProject, id: UUID, isStart: Bool, dt: Double, minLen: Double) {
        guard let i = p.music.clips.firstIndex(where: { $0.id == id }) else { return }
        let m = TimeMap(p), c = p.music.clips[i]
        if isStart {
            let newStart = m.out(max(0, m.src(c.start) + dt))
            let delta = min(max(newStart - c.start, -c.trimIn), c.length - minLen)
            p.music.clips[i].start = c.start + delta; p.music.clips[i].trimIn = c.trimIn + delta
        } else {
            let endOut = m.out(max(0, m.src(c.start + c.length) + dt))
            p.music.clips[i].trimOut = min(max(c.trimIn + minLen, c.trimIn + (endOut - c.start)), c.fileLength)
        }
    }

    private func edge(isStart: Bool) -> some View {
        Rectangle().fill(Color.white.opacity(0.001)).frame(width: 8)
            .overlay(Capsule().fill(Color.white.opacity(0.9)).frame(width: 2.5, height: 10))
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .onTapGesture { ed.selected = clip.id; ed.picking = false }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named(TimelineStrip.space))
                .onChanged { v in
                    ed.selected = clip.id
                    ed.dragBegin()
                    let dt = v.translation.width / w * d
                    ed.dragUpdate { p in Self.trim(&p, id: clip.id, isStart: isStart, dt: dt, minLen: minLen) }
                }
                .onEnded { _ in ed.dragEnd() })
    }
}

// 选中音乐时的属性条：名字、成片里从哪到哪、删除
struct MusicInspector: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        if let i = ed.musicIndex(ed.selected) {
            let c = ed.project.music.clips[i]
            HStack(spacing: 14) {
                Label(c.name, systemImage: "music.note").font(.callout.weight(.semibold)).lineLimit(1)
                Text(clock(c.start) + " – " + clock(c.start + c.length)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Text(L("（成片时间）", "(output time)")).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(role: .destructive) { ed.deleteSelected() } label: { Label(L("删除", "Delete"), systemImage: "trash") }
            }
        }
    }
}

// 建议块：虚线框 + 半透明；够宽时块上直接有 ✓ / ✕
struct SuggestionBlock: View {
    @ObservedObject var ed: StudioEditor
    let sug: SpeedSuggestion
    var body: some View {
        let tint = Color(red: 0.20, green: 0.52, blue: 0.96)
        GeometryReader { g in
            ZStack {
                RoundedRectangle(cornerRadius: 5).fill(tint.opacity(0.16))
                RoundedRectangle(cornerRadius: 5).stroke(tint.opacity(0.9), style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                HStack(spacing: 4) {
                    Image(systemName: sug.kind == .idle ? "hourglass" : "keyboard").font(.system(size: 9, weight: .bold))
                    if g.size.width > 70 { Text(String(format: "%.0f×", sug.rate)).font(.system(size: 10, weight: .bold).monospacedDigit()) }
                    Spacer(minLength: 0)
                    if g.size.width > 54 {
                        Button { ed.acceptSuggestions([sug.id]) } label: { Image(systemName: "checkmark.circle.fill") }
                            .buttonStyle(.plain).help(L("采纳：这段改成快放", "Apply: speed this part up"))
                        Button { ed.ignoreSuggestions([sug.id]) } label: { Image(systemName: "xmark.circle.fill") }
                            .buttonStyle(.plain).help(L("忽略", "Ignore"))
                    }
                }
                .font(.system(size: 11)).foregroundStyle(tint).padding(.horizontal, 5)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.seek(sug.start) }
        .help(sug.kind == .idle ? L("建议 4× 快放：这里很久没操作、画面也没变", "Suggested 4×: nothing happens here for a while")
                                : L("建议 2× 快放：这里一直在打字", "Suggested 2×: long typing"))
    }
}

// 建议加速的总条：找到几处、全部采纳 / 全部忽略
struct SuggestionBar: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "gauge.with.dots.needle.67percent").foregroundStyle(Color.accentColor)
            if ed.suggesting {
                ProgressView().controlSize(.small)
                Text(L("正在找可以加速的地方…", "Looking for parts to speed up…")).font(.callout).foregroundStyle(.secondary)
            } else if ed.suggestions.isEmpty {
                Text(L("没找到要加速的地方：这段录制里没有长时间不动的空档，也没有长时间打字", "Nothing to speed up: no long idle stretches or long typing in this recording"))
                    .font(.callout).foregroundStyle(.secondary)
                Spacer()
                Button(L("好", "OK")) { ed.suggestNone = false }
            } else {
                let idle = ed.suggestions.filter { $0.kind == .idle }.count, typing = ed.suggestions.count - idle
                Text(L("找到 \(ed.suggestions.count) 处可以加速（空档 \(idle)、打字 \(typing)）", "Found \(ed.suggestions.count) parts to speed up (\(idle) idle, \(typing) typing)"))
                    .font(.callout)
                Spacer()
                Button(L("全部采纳", "Apply All")) { ed.acceptSuggestions(ed.suggestions.map(\.id)) }.buttonStyle(.borderedProminent)
                Button(L("全部忽略", "Ignore All")) { ed.ignoreSuggestions(ed.suggestions.map(\.id)) }
            }
            if ed.suggesting { Spacer() }
        }
    }
}

// 样式面板「推近」：全局推近速度（滑块，0.4× 很慢 … 2.5× 很快，1× 标准）
struct ZoomSpeedBox: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Text(L("推近速度", "Zoom speed")).font(.callout)
                Spacer(minLength: 0)
                Text(String(format: "%.1f×", ed.project.look.zoomSpeed)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            Slider(value: ed.lookSlider(\.zoomSpeed), in: 0.4...2.5, step: 0.1, onEditingChanged: { if !$0 { ed.dragEnd() } }).controlSize(.small)
            HStack {
                Text(L("慢", "Slow")); Spacer()
                Button(L("标准", "Normal")) { ed.edit { $0.look.zoomSpeed = 1 } }.buttonStyle(.plain).foregroundStyle(Color.accentColor)
                Spacer(); Text(L("快", "Fast"))
            }.font(.caption2).foregroundStyle(.secondary)
            Text(L("镜头推近、拉远、跟着鼠标走的快慢；选中某段推近还能单独设", "How fast the camera zooms in, out and follows; you can also set it per zoom"))
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).padding(.top, 2)
        }
    }
}
enum ZoomSpeed {
    static let all: [(v: Double, name: String)] = [(0.6, L("慢", "Slow")), (1.0, L("标准", "Normal")), (1.7, L("快", "Fast"))]
}

// 推近段属性条里：这一段的速度（跟全局 / 慢 / 标准 / 快）
struct ZoomSpeedPicker: View {
    @ObservedObject var ed: StudioEditor
    let index: Int
    var body: some View {
        let cur = ed.project.segments.indices.contains(index) ? ed.project.segments[index].speed : nil
        Picker(L("速度", "Speed"), selection: Binding(get: { cur ?? -1 }, set: { v in ed.edit { $0.segments[index].speed = v < 0 ? nil : v } })) {
            Text(L("跟全局", "Default")).tag(-1.0)
            ForEach(ZoomSpeed.all, id: \.v) { Text($0.name).tag($0.v) }
        }
        .frame(width: 130)
        .help(L("只改这一段推近的快慢", "Speed for this zoom only"))
    }
}

// 样式面板「背景音乐」
struct MusicBox: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        let m = ed.project.music
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Button(m.clips.isEmpty ? L("选择音乐…", "Choose Music…") : L("再加一首…", "Add Another…")) { ed.chooseMusic() }
                Spacer()
            }
            if m.clips.isEmpty {
                Text(L("选一首 mp3 / m4a / wav，会出现在时间轴最下面的「音乐」轨", "Pick an mp3 / m4a / wav; it appears on the Music lane at the bottom of the timeline"))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else {
                row(L("音量", "Volume"), \.volume, 0...1.5) { "\(Int(($0 * 100).rounded()))%" }
                row(L("淡入", "Fade in"), \.fadeIn, 0...5) { String(format: L("%.1f 秒", "%.1fs"), $0) }
                row(L("淡出", "Fade out"), \.fadeOut, 0...5) { String(format: L("%.1f 秒", "%.1fs"), $0) }
                Toggle(L("循环到片尾", "Loop to the end"), isOn: ed.musicToggle(\.loop))
                    .help(L("最后一首放完从头再来，一直铺到片尾", "The last song repeats until the video ends"))
                Toggle(L("说话时自动压低音乐", "Lower music when someone talks"), isOn: ed.musicToggle(\.duck))
                    .help(L("有讲解、或录到的声音里有人说话时，音乐自动变小，说完慢慢回来", "When there's narration or speech in the recording, the music drops and comes back afterwards"))
                if m.duck { row(L("压低", "Lower by"), \.duckDB, -30 ... -6) { String(format: "%.0f dB", $0) } }
            }
        }.toggleStyle(.checkbox).font(.callout)
    }
    private func row(_ title: String, _ kp: WritableKeyPath<MusicTrack, Double>, _ r: ClosedRange<Double>, _ fmt: @escaping (Double) -> String) -> some View {
        HStack(spacing: 8) {
            Text(title).font(.callout).frame(width: 62, alignment: .leading)
            Slider(value: ed.musicSlider(kp), in: r, onEditingChanged: { if !$0 { ed.dragEnd() } }).controlSize(.small)
            Text(fmt(ed.project.music[keyPath: kp])).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
        }
    }
}
