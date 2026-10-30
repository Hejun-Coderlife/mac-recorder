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
        selectedPiece = t
        showToast(L("已在 \(clock(t)) 分割 · 点一段按 Delete 删掉", "Split at \(clock(t)) · select a part and press Delete to remove it"))
    }
    // 删掉一段：变成剪掉段（和挨着的剪掉段并成一段），时间轴上合拢
    func deletePiece(_ p: Piece) {
        edit { pr in
            pr.cuts.removeAll { $0.start >= p.start - 1e-6 && $0.end <= p.end + 1e-6 }   // 里面原来的快放段一起去掉
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
            pr.cuts.removeAll { $0.mode == .speed && $0.start >= p.start - 1e-6 && $0.end <= p.end + 1e-6 }
            if rate != 1 { pr.cuts.append(CutSegment(start: p.start, end: p.end, mode: .speed, rate: rate)); pr.cuts.sort { $0.start < $1.start } }
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
            if fast {
                HStack(spacing: 3) {
                    Image(systemName: "forward.fill").font(.system(size: 9, weight: .bold))
                    Text(piece.rate.formatted() + "×").font(.system(size: 10, weight: .bold).monospacedDigit())
                }.foregroundStyle(.white).lineLimit(1)
            }
        }
        .padding(.horizontal, 1)
        .contentShape(Rectangle())
        .onTapGesture { ed.selected = nil; ed.selectedPiece = piece.start; ed.picking = false }
        .contextMenu {
            Button(L("在播放头分割（S）", "Split at Playhead (S)")) { ed.split(at: ed.time) }
            Menu(L("速度", "Speed")) {
                ForEach([1.0, 1.5, 2, 4, 8], id: \.self) { r in Button(r.formatted() + "×" + (r == piece.rate ? " ✓" : "")) { ed.setPieceSpeed(piece, r) } }
            }
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
