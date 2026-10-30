// 像剪映那样剪（照 Mac 版 Pieces.swift）：在播放头处「分割」，点选一段按 Delete 删掉，时间轴上那段直接合拢（成片里也没了）；
// 选中一段还能改速度。删掉的地方在剪辑轨上留一个小剪刀记号，点它再按 Delete（或点「恢复」）就回来。
// 存法同 Mac：删掉 = mode 为 cut 的 CutSegment，改速度 = mode 为 speed 的段；分割点存 splits，不要打字声的段存 keyMutes。
// 只用纯 C#（不碰 WinForms），自测能直接喂数据
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenRecorder
{
    struct Piece
    {
        public double Start, End, Rate;          // Rate 1 = 原速
        public double Len => End - Start;
    }

    // 音效轨上的「一阵打字」：按键间隔不到 1.2 秒算同一阵
    sealed class KeyBurst
    {
        public double Start, End;
        public List<double> Keys = new List<double>();
    }

    static class Pieces
    {
        // ---- 时间轴的「可见时间」：删掉（cut）的段合拢，其余按素材时间（快放的段不压缩，宽度照旧） ----
        public static List<CutSegment> Removed(StudioProject p) => p.Cuts.Where(c => !c.Speed).OrderBy(c => c.Start).ToList();
        public static double VisTime(StudioProject p, double t)
        {
            double v = t;
            foreach (var c in Removed(p)) if (c.Start < t) v -= Math.Min(t, c.End) - c.Start;
            return v;
        }
        public static double VisDuration(StudioProject p, double duration) => Math.Max(0.01, VisTime(p, duration));
        // 可见时间 → 素材时间：正好落在合拢处时算到删掉那段的后面
        public static double SrcTime(StudioProject p, double u, double duration)
        {
            double s = u;
            foreach (var c in Removed(p)) { if (c.Start <= s + 1e-9) s += c.End - c.Start; else break; }
            return Math.Min(Math.Max(s, 0), duration);
        }
        // 时间轴上拖了多远（可见时间）→ 素材时间要挪多少：按拖动开始时那一点换算，跨过剪刀记号也跟手
        public static double SrcDelta(StudioProject p, double anchor, double dVis, double duration) => SrcTime(p, VisTime(p, anchor) + dVis, duration) - anchor;

        // ---- 剪辑轨上的一段段：修剪范围里，按分割点和已有剪辑段的边切开；删掉的不算 ----
        public static List<Piece> List(StudioProject p, double duration)
        {
            double lo = p.TrimStart, hi = p.TrimEnd > lo ? p.TrimEnd : duration;
            var b = new SortedSet<double> { lo, hi };
            foreach (var s in p.Splits) if (s > lo + 0.01 && s < hi - 0.01) b.Add(s);
            foreach (var c in p.Cuts)
            {
                if (c.Start > lo + 0.01 && c.Start < hi - 0.01) b.Add(c.Start);
                if (c.End > lo + 0.01 && c.End < hi - 0.01) b.Add(c.End);
            }
            var xs = b.ToList(); var outL = new List<Piece>();
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                double s = xs[i], e = xs[i + 1], mid = (s + e) / 2;
                var c = p.Cuts.FirstOrDefault(x => x.Start <= mid && mid < x.End);
                if (c != null && !c.Speed) continue;
                outL.Add(new Piece { Start = s, End = e, Rate = c != null ? c.Rate : 1 });
            }
            return outL;
        }

        // 在 t 处切一刀（太靠近已有的边就不切）；切了返回 true
        public static bool Split(StudioProject p, double t, double duration)
        {
            double lo = p.TrimStart, hi = p.TrimEnd > lo ? p.TrimEnd : duration;
            if (!(t > lo + 0.1 && t < hi - 0.1) || List(p, duration).Any(x => Math.Abs(x.Start - t) < 0.1 || Math.Abs(x.End - t) < 0.1)) return false;
            p.Splits.Add(t); p.Splits.Sort();
            return true;
        }

        // 把和 [s, e) 重叠的快放段切掉这一截（两头留着）：一段快放跨了好几个片段时，只动选中的那段
        public static List<CutSegment> CarveSpeed(List<CutSegment> cuts, double s, double e)
        {
            var outL = new List<CutSegment>();
            foreach (var c in cuts)
            {
                if (!c.Speed || c.End <= s + 1e-6 || c.Start >= e - 1e-6) { outL.Add(c); continue; }
                if (c.Start < s - 1e-6) { var x = c.Clone(); x.Id = Guid.NewGuid().ToString().ToUpperInvariant(); x.End = s; outL.Add(x); }
                if (c.End > e + 1e-6) { var x = c.Clone(); x.Id = Guid.NewGuid().ToString().ToUpperInvariant(); x.Start = e; outL.Add(x); }
            }
            return outL;
        }

        // 删掉一段：变成剪掉段（和挨着的剪掉段并成一段），时间轴上合拢
        public static void Delete(StudioProject p, double s0, double e0)
        {
            p.Cuts = CarveSpeed(p.Cuts, s0, e0);
            double s = s0, e = e0;
            foreach (var c in p.Cuts.Where(c => !c.Speed && (Math.Abs(c.End - s) < 1e-6 || Math.Abs(c.Start - e) < 1e-6)).ToList()) { s = Math.Min(s, c.Start); e = Math.Max(e, c.End); }
            p.Cuts.RemoveAll(c => !c.Speed && c.Start >= s - 1e-6 && c.End <= e + 1e-6);
            p.Cuts.Add(new CutSegment { Start = s, End = e });
            p.Cuts = p.Cuts.OrderBy(c => c.Start).ToList();
        }

        // 一段改速度：1 = 原速
        public static void SetSpeed(StudioProject p, double s, double e, double rate)
        {
            p.Cuts = CarveSpeed(p.Cuts, s, e);
            if (Math.Abs(rate - 1) > 1e-6 && !p.Cuts.Any(c => !c.Speed && c.Start < e && c.End > s))
                p.Cuts.Add(new CutSegment { Start = s, End = e, Speed = true, Rate = rate });
            p.Cuts = p.Cuts.OrderBy(c => c.Start).ToList();
        }

        // ---- 打字声 ----
        // 这一刻（素材时间）的按键要不要响：总开关开着、不在静音段里、（勾了的话）不在加速段里
        public static bool KeySounds(StudioProject p, double t)
        {
            if (!LookBool(p, "typeSound")) return false;
            if (p.KeyMutes.Any(m => m.Length == 2 && t >= m[0] && t < m[1])) return false;
            if (LookBool(p, "typeMuteFast") && p.Cuts.Any(c => c.Speed && t >= c.Start && t < c.End)) return false;
            return true;
        }
        // 打字声的开关存在样式里（Windows 的样式还没有这些字段：在 Extra 里，原样留着）
        public static bool LookBool(StudioProject p, string key) => p.Look.Extra.TryGetValue(key, out var v) && v is bool b && b;
        public static void SetLookBool(StudioProject p, string key, bool v) => p.Look.Extra[key] = v;

        // 按键分成一阵一阵（修剪范围里、不在删掉的段里）
        public static List<KeyBurst> Bursts(Track tr, StudioProject p)
        {
            var map = new TimeMap(p);
            var ks = tr.Keys.Where(k => k >= p.TrimStart && k < p.TrimEnd && !map.InCut(k)).OrderBy(k => k).ToList();
            var outL = new List<KeyBurst>(); var cur = new List<double>();
            void Flush() { if (cur.Count > 0) outL.Add(new KeyBurst { Start = cur[0] - 0.08, End = cur[cur.Count - 1] + 0.2, Keys = cur.ToList() }); cur.Clear(); }
            foreach (var k in ks) { if (cur.Count > 0 && k - cur[cur.Count - 1] > 1.2) Flush(); cur.Add(k); }
            Flush();
            return outL;
        }
        // 这阵打字有没有声音（有任何一下响就算有）
        public static bool BurstOn(StudioProject p, KeyBurst b) => b.Keys.Any(k => KeySounds(p, k));
        public static void SetBurstSound(StudioProject p, KeyBurst b, bool on)
        {
            var outL = new List<double[]>();
            foreach (var m in p.KeyMutes.Where(m => m.Length == 2))
            {
                if (m[1] <= b.Start || m[0] >= b.End) { outL.Add(m); continue; }
                if (m[0] < b.Start) outL.Add(new[] { m[0], b.Start });
                if (m[1] > b.End) outL.Add(new[] { b.End, m[1] });
            }
            if (!on) outL.Add(new[] { b.Start, b.End });
            p.KeyMutes = outL.OrderBy(m => m[0]).ToList();
            if (on && !LookBool(p, "typeSound")) SetLookBool(p, "typeSound", true);
        }
        // 这阵打字的速度（这段视频的快放倍数；1 = 原速）
        public static double BurstRate(StudioProject p, KeyBurst b)
        {
            double s = Math.Max(b.Start, p.TrimStart), e = Math.Min(b.End, p.TrimEnd);
            return p.Cuts.FirstOrDefault(c => c.Speed && c.Start <= s + 0.01 && c.End >= e - 0.01)?.Rate ?? 1;
        }
        public static void SetBurstRate(StudioProject p, KeyBurst b, double rate)
        {
            double s = Math.Max(b.Start, p.TrimStart), e = Math.Min(b.End, p.TrimEnd);
            if (e - s > 0.1) SetSpeed(p, s, e, rate);
        }
    }
}
