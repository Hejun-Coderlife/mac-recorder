// 像剪映那样剪：在播放头处「分割」，点选一段按 Delete 删掉，时间轴上那段直接合拢不见（成片里也没了）；
// 选中一段还能改速度。删掉的地方在剪辑轨上留一个小剪刀记号，点它再按 Delete（或点「恢复」）就回来。
// 存法不变：删掉 = 一段 mode 为 cut 的 CutSegment，改速度 = mode 为 speed 的段；分割点另存在 splits 里
import SwiftUI

struct Piece: Equatable {
    let start: Double, end: Double
    let rate: Double                 // 1 = 原速
    var len: Double { end - start }
}

extension StudioEditor {
    // 时间轴上的「可见时间」：删掉（cut）的段合拢，其余按素材时间（快放的段不压缩，宽度照旧）
    var removedCuts: [CutSegment] { project.cuts.filter { $0.mode == .cut }.sorted { $0.start < $1.start } }
    func visTime(_ t: Double) -> Double {
        var v = t
        for c in removedCuts where c.start < t { v -= min(t, c.end) - c.start }
        return v
    }
    var visDuration: Double { max(0.01, visTime(duration)) }
    // 可见时间 → 素材时间：正好落在合拢处时算到删掉那段的后面
    func srcTime(vis u: Double) -> Double {
        var s = u
        for c in removedCuts { if c.start <= s + 1e-9 { s += c.end - c.start } else { break } }
        return min(max(s, 0), duration)
    }

    // 剪辑轨上的一段段：修剪范围里，按分割点和已有剪辑段的边切开；删掉的不算
    var pieces: [Piece] {
        let lo = project.trimStart, hi = project.trimEnd > lo ? project.trimEnd : duration
        var b = Set([lo, hi])
        for s in project.splits where s > lo + 0.01 && s < hi - 0.01 { b.insert(s) }
        for c in project.cuts {
            if c.start > lo + 0.01 && c.start < hi - 0.01 { b.insert(c.start) }
            if c.end > lo + 0.01 && c.end < hi - 0.01 { b.insert(c.end) }
        }
        let xs = b.sorted()
        return zip(xs, xs.dropFirst()).compactMap { s, e in
            let mid = (s + e) / 2
            if let c = project.cuts.first(where: { $0.start <= mid && mid < $0.end }) {
                return c.mode == .cut ? nil : Piece(start: s, end: e, rate: c.rate)
            }
            return Piece(start: s, end: e, rate: 1)
        }
    }
    var currentPiece: Piece? { selectedPiece.flatMap { s in pieces.first { abs($0.start - s) < 1e-6 } } }

    // 分割（⌘B / S）：在播放头处切一刀，选中后面那段
    func split(at t: Double) {
        let lo = project.trimStart, hi = project.trimEnd > lo ? project.trimEnd : duration
        guard t > lo + 0.1, t < hi - 0.1, !pieces.contains(where: { abs($0.start - t) < 0.1 || abs($0.end - t) < 0.1 }) else { NSSound.beep(); return }
        edit { $0.splits.append(t); $0.splits.sort() }
        selectedBurst = nil; selectedPiece = t
        showToast(L("已在 \(clock(t)) 分割 · 点一段按 Delete 删掉", "Split at \(clock(t)) · select a part and press Delete to remove it"))
    }
    // 把和 [s, e) 重叠的快放段切掉这一截（两头留着）：一段快放跨了好几个片段时，只动选中的那段
    static func carveSpeed(_ cuts: [CutSegment], _ s: Double, _ e: Double) -> [CutSegment] {
        var out: [CutSegment] = []
        for c in cuts {
            guard c.mode == .speed, c.end > s + 1e-6, c.start < e - 1e-6 else { out.append(c); continue }
            if c.start < s - 1e-6 { var x = c; x.id = UUID(); x.end = s; out.append(x) }
            if c.end > e + 1e-6 { var x = c; x.id = UUID(); x.start = e; out.append(x) }
        }
        return out
    }
    // 删掉一段：变成剪掉段（和挨着的剪掉段并成一段），时间轴上合拢
    func deletePiece(_ p: Piece) {
        edit { pr in
            pr.cuts = Self.carveSpeed(pr.cuts, p.start, p.end)
            var s = p.start, e = p.end
            for c in pr.cuts where c.mode == .cut && (abs(c.end - s) < 1e-6 || abs(c.start - e) < 1e-6) { s = min(s, c.start); e = max(e, c.end) }
            pr.cuts.removeAll { $0.mode == .cut && $0.start >= s - 1e-6 && $0.end <= e + 1e-6 }
            pr.cuts.append(CutSegment(start: s, end: e, mode: .cut))
            pr.cuts.sort { $0.start < $1.start }
        }
        selectedPiece = nil
        showToast(L("删掉了 \(String(format: "%.1f", p.len)) 秒 · ⌘Z 撤销，或点剪辑轨上的 ✂ 恢复", "Removed \(String(format: "%.1f", p.len))s · ⌘Z to undo, or click the ✂ mark to restore"))
        if time >= p.start && time < p.end { seek(srcTime(vis: visTime(p.start))) }
    }
    // 一段改速度：1 = 原速
    func setPieceSpeed(_ p: Piece, _ rate: Double) {
        edit { pr in
            pr.cuts = Self.carveSpeed(pr.cuts, p.start, p.end)
            if rate != 1 { pr.cuts.append(CutSegment(start: p.start, end: p.end, mode: .speed, rate: rate)) }
            pr.cuts.sort { $0.start < $1.start }
        }
        selectedPiece = p.start
    }
    // 这一段有没有打字声（被静音段整个盖住 = 没有）
    func pieceKeySound(_ p: Piece) -> Bool {
        !project.keyMutes.contains { $0.count == 2 && $0[0] <= p.start + 1e-6 && $0[1] >= p.end - 1e-6 }
    }
    func setPieceKeySound(_ p: Piece, _ on: Bool) {
        edit { pr in
            // 先去掉和这段重叠的静音部分（只裁掉这一段的范围），关的话再整段加上
            var out: [[Double]] = []
            for m in pr.keyMutes where m.count == 2 {
                if m[1] <= p.start || m[0] >= p.end { out.append(m); continue }
                if m[0] < p.start { out.append([m[0], p.start]) }
                if m[1] > p.end { out.append([p.end, m[1]]) }
            }
            if !on { out.append([p.start, p.end]) }
            pr.keyMutes = out.sorted { $0[0] < $1[0] }
        }
        selectedPiece = p.start
    }
    // 恢复删掉的那段
    func restoreCut(_ id: UUID) {
        edit { $0.cuts.removeAll { $0.id == id } }
        selected = nil
    }
}

// 剪辑轨上的一段（像剪映的片段）：点选，选中后 Delete 删、下面条里改速度
struct PieceBlock: View {
    @ObservedObject var ed: StudioEditor
    let piece: Piece
    var body: some View {
        let sel = ed.selectedPiece.map { abs($0 - piece.start) < 1e-6 } ?? false
        let fast = piece.rate != 1
        let base = fast ? Color(red: 0.20, green: 0.52, blue: 0.96) : Color(red: 0.42, green: 0.44, blue: 0.50)
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(base.opacity(sel ? 0.95 : 0.55))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.white.opacity(0.18), lineWidth: sel ? 2 : 0.5)
            if ed.project.look.typeSound && !ed.pieceKeySound(piece) {
                Image(systemName: "keyboard.badge.ellipsis").font(.system(size: 8)).foregroundStyle(.white.opacity(0.75))
                    .frame(maxWidth: .infinity, alignment: .trailing).padding(.trailing, 6)
                    .help(L("这段没有打字声", "No typing sound in this part"))
            }
            if fast {
                HStack(spacing: 3) {
                    Image(systemName: "forward.fill").font(.system(size: 9, weight: .bold))
                    Text(piece.rate.formatted() + "×").font(.system(size: 10, weight: .bold).monospacedDigit())
                }.foregroundStyle(.white).lineLimit(1)
            }
        }
        .padding(.horizontal, 1)
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = nil; ed.selectedBurst = nil; ed.selectedPiece = piece.start; ed.picking = false }
        .contextMenu {
            Button(L("在播放头分割（S）", "Split at Playhead (S)")) { ed.split(at: ed.time) }
            Menu(L("速度", "Speed")) {
                ForEach([1.0, 1.5, 2, 4, 8], id: \.self) { r in Button(r.formatted() + "×" + (r == piece.rate ? " ✓" : "")) { ed.setPieceSpeed(piece, r) } }
            }
            Button(ed.pieceKeySound(piece) ? L("这段不要打字声", "Mute Typing Sound Here") : L("这段恢复打字声", "Restore Typing Sound Here")) { ed.setPieceKeySound(piece, !ed.pieceKeySound(piece)) }
                .disabled(!ed.project.look.typeSound)
            Divider()
            Button(L("删除这段", "Delete Part"), role: .destructive) { ed.deletePiece(piece) }
        }
        .help(L("一段素材：点选后按 Delete 删掉（时间轴直接合拢），或在下面改速度；在播放头处按 S（⌘B）分割",
                "A part of the clip: select it and press Delete to remove it (the timeline closes up), or change its speed below; press S (⌘B) to split at the playhead"))
    }
}

// 删掉的地方：剪辑轨上一个小剪刀，点了选中，可以恢复
struct CutSeam: View {
    @ObservedObject var ed: StudioEditor
    let cut: CutSegment
    var body: some View {
        let sel = ed.selected == cut.id
        Image(systemName: "scissors").font(.system(size: 8, weight: .bold)).foregroundStyle(.white)
            .frame(width: 16, height: 16)
            .background(Circle().fill(Color(red: 0.92, green: 0.27, blue: 0.27)))
            .overlay(Circle().stroke(sel ? Color.white : Color.clear, lineWidth: 2))
            .contentShape(Circle())
            .onTapGesture { ed.selectedPiece = nil; ed.selected = cut.id; ed.picking = false }
            .help(L("这里删掉了 \(String(format: "%.1f", cut.end - cut.start)) 秒：点一下可以恢复", "\(String(format: "%.1f", cut.end - cut.start))s removed here: click to restore"))
    }
}

// 选中一段时下面那条：时间、删除、速度
struct PieceInspector: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        if let p = ed.currentPiece {
            HStack(spacing: 12) {
                Text(L("片段", "Clip")).font(.callout.weight(.semibold))
                Text(clock(p.start) + " – " + clock(p.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Text(L("速度", "Speed")).font(.callout)
                Picker("", selection: Binding(get: { p.rate }, set: { ed.setPieceSpeed(p, $0) })) {
                    ForEach([1.0, 1.5, 2, 4, 8], id: \.self) { Text($0.formatted() + "×").tag($0) }
                }.pickerStyle(.segmented).labelsHidden().fixedSize()
                if ed.project.look.typeSound {
                    Toggle(L("打字声", "Typing sound"), isOn: Binding(get: { ed.pieceKeySound(p) }, set: { ed.setPieceKeySound(p, $0) })).toggleStyle(.checkbox)
                        .help(L("关掉：这一段里打字不出声", "Off: no typing sound in this part"))
                }
                Text(p.rate == 1 ? L("\(String(format: "%.1f", p.len)) 秒", "\(String(format: "%.1f", p.len))s")
                                 : L("\(String(format: "%.1f", p.len)) 秒 → \(String(format: "%.1f", p.len / p.rate)) 秒", "\(String(format: "%.1f", p.len))s → \(String(format: "%.1f", p.len / p.rate))s"))
                    .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Spacer()
                Button(role: .destructive) { ed.deletePiece(p) } label: { Label(L("删除这段", "Delete Part"), systemImage: "trash") }
                    .help(L("删掉这段（Delete），时间轴直接合拢；⌘Z 撤销", "Remove this part (Delete); the timeline closes up. ⌘Z to undo"))
            }.controlSize(.small)
        }
    }
}

// ---- 音效轨：打字声按「一阵一阵」显示（按键间隔不到 1.2 秒算同一阵）----
struct KeyBurst: Equatable { let start: Double, end: Double, keys: [Double] }

extension StudioEditor {
    var keyBursts: [KeyBurst] {
        let ks = track.keys.filter { $0 >= project.trimStart && $0 < project.trimEnd && !timeMap.inCut($0) }.sorted()
        var out: [KeyBurst] = [], cur: [Double] = []
        for k in ks {
            if let l = cur.last, k - l > 1.2 { out.append(KeyBurst(start: cur[0] - 0.08, end: cur.last! + 0.2, keys: cur)); cur = [] }
            cur.append(k)
        }
        if !cur.isEmpty { out.append(KeyBurst(start: cur[0] - 0.08, end: cur.last! + 0.2, keys: cur)) }
        return out
    }
    var currentBurst: KeyBurst? { selectedBurst.flatMap { s in keyBursts.first { abs($0.start - s) < 1e-6 } } }
    // 这阵打字有没有声音（有任何一下响就算有）
    func burstOn(_ b: KeyBurst) -> Bool { b.keys.contains { project.keySounds(at: $0) } }
    func setBurstSound(_ b: KeyBurst, _ on: Bool) {
        edit { pr in
            var out: [[Double]] = []
            for m in pr.keyMutes where m.count == 2 {
                if m[1] <= b.start || m[0] >= b.end { out.append(m); continue }
                if m[0] < b.start { out.append([m[0], b.start]) }
                if m[1] > b.end { out.append([b.end, m[1]]) }
            }
            if !on { out.append([b.start, b.end]) }
            pr.keyMutes = out.sorted { $0[0] < $1[0] }
            if on && !pr.look.typeSound { pr.look.typeSound = true }
        }
        selectedBurst = b.start
        showToast(on ? L("这阵打字声恢复了", "Typing sound restored here") : L("这阵打字声去掉了 · 再按 Delete 恢复", "Typing sound removed here · press Delete again to restore"))
    }
}

extension StudioEditor {
    // 这阵打字的速度（这段视频的快放倍数；1 = 原速）
    func burstRate(_ b: KeyBurst) -> Double {
        let s = max(b.start, project.trimStart), e = min(b.end, project.trimEnd)
        return project.cuts.first { $0.mode == .speed && $0.start <= s + 0.01 && $0.end >= e - 0.01 }?.rate ?? 1
    }
    // 改这阵打字的速度：这段视频连同打字声一起快放（和剪辑轨上的快放段是同一种东西）
    func setBurstRate(_ b: KeyBurst, _ rate: Double) {
        let s = max(b.start, project.trimStart), e = min(b.end, project.trimEnd)
        guard e - s > 0.1 else { return }
        edit { pr in
            pr.cuts = Self.carveSpeed(pr.cuts, s, e)
            if rate != 1 && !pr.cuts.contains(where: { $0.mode == .cut && $0.start < e && $0.end > s }) {
                pr.cuts.append(CutSegment(start: s, end: e, mode: .speed, rate: rate))
            }
            pr.cuts.sort { $0.start < $1.start }
        }
        selectedBurst = b.start
        showToast(rate == 1 ? L("这阵打字恢复原速", "Back to normal speed") : L("这阵打字 \(rate.formatted())× 快放（画面和打字声一起）· ⌘Z 撤销", "This typing plays \(rate.formatted())× (picture and sound) · ⌘Z to undo"))
    }
}

struct KeyBurstBlock: View {
    @ObservedObject var ed: StudioEditor
    let burst: KeyBurst
    let x: (Double) -> Double
    var body: some View {
        let sel = ed.selectedBurst.map { abs($0 - burst.start) < 1e-6 } ?? false
        let on = ed.burstOn(burst)
        let tint = Color(red: 0.55, green: 0.42, blue: 0.95)
        let x0 = x(burst.start)
        ZStack(alignment: .leading) {
            RoundedRectangle(cornerRadius: 5).fill(on ? tint.opacity(sel ? 0.95 : 0.75) : Color.secondary.opacity(sel ? 0.35 : 0.18))
            RoundedRectangle(cornerRadius: 5).stroke(sel ? Color.white : Color.clear, lineWidth: 2)
            // 每一下按键一根竖线
            Canvas { ctx, size in
                for k in burst.keys {
                    let px = x(k) - x0
                    let k_on = ed.project.keySounds(at: k)   // 这一下响不响（被静音的画淡）
                    ctx.fill(Path(CGRect(x: px - 0.6, y: size.height * 0.25, width: 1.2, height: size.height * 0.5)), with: .color(.white.opacity(k_on ? 0.9 : 0.25)))
                }
            }
            if ed.burstRate(burst) != 1 {
                Text(ed.burstRate(burst).formatted() + "×").font(.system(size: 9, weight: .bold).monospacedDigit()).foregroundStyle(.white)
                    .padding(.horizontal, 3).background(Capsule().fill(Color.black.opacity(0.35)))
                    .frame(maxWidth: .infinity, alignment: .trailing).padding(.trailing, 3)
            }
            if !on {
                Image(systemName: "speaker.slash.fill").font(.system(size: 8)).foregroundStyle(.secondary).padding(.leading, 4)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = nil; ed.selectedPiece = nil; ed.selectedBurst = burst.start; ed.picking = false }
        .contextMenu {
            Button(on ? L("去掉这阵打字声", "Remove Typing Sound") : L("恢复这阵打字声", "Restore Typing Sound")) { ed.setBurstSound(burst, !on) }
            Menu(L("速度", "Speed")) {
                ForEach([1.0, 1.5, 2, 3, 4], id: \.self) { r in Button(r.formatted() + "×" + (ed.burstRate(burst) == r ? " ✓" : "")) { ed.setBurstRate(burst, r) } }
            }
        }
        .help(L("打字声：\(burst.keys.count) 下。选中按 Delete 去掉 / 恢复", "Typing: \(burst.keys.count) keys. Select and press Delete to remove / restore"))
    }
}

struct BurstInspector: View {
    @ObservedObject var ed: StudioEditor
    var body: some View {
        if let b = ed.currentBurst {
            let on = ed.burstOn(b)
            HStack(spacing: 12) {
                Label(L("打字声", "Typing sound"), systemImage: "keyboard").font(.callout.weight(.semibold))
                Text(clock(max(0, b.start)) + " – " + clock(b.end)).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                Text(L("\(b.keys.count) 下", "\(b.keys.count) keys")).font(.callout).foregroundStyle(.secondary)
                Divider().frame(height: 16)
                Text(L("速度", "Speed")).font(.callout)
                Picker("", selection: Binding(get: { ed.burstRate(b) }, set: { ed.setBurstRate(b, $0) })) {
                    ForEach([1.0, 1.5, 2, 3, 4], id: \.self) { Text($0.formatted() + "×").tag($0) }
                }.pickerStyle(.segmented).labelsHidden().fixedSize()
                    .help(L("这一段视频连同打字声一起快放", "Speeds up this part of the video together with its typing sound"))
                Spacer()
                Button(role: on ? .destructive : nil) { ed.setBurstSound(b, !on) } label: {
                    Label(on ? L("去掉这阵打字声", "Remove") : L("恢复", "Restore"), systemImage: on ? "speaker.slash" : "speaker.wave.2")
                }
            }.controlSize(.small)
        }
    }
}
