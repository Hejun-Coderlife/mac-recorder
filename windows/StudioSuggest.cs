// 建议加速（照 Mac 版 SpeedSuggest.swift）：找出「很久没操作、画面也几乎不动」的空档（建议 4×）和「一直在打字」的段落（建议 2×），
// 在剪辑轨上先画成虚线建议块，用户采纳了才变成正式的快放段；建议不存进工程
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ScreenRecorder
{
    sealed class SpeedSuggestion
    {
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public double Start, End;                 // 素材时间
        public bool Typing;                       // false = 空档，true = 打字
        public double Rate => Typing ? 2 : 4;
    }

    static class SpeedSuggest
    {
        public const double Bin = 0.25;           // 每 0.25 秒看一次
        public const double MinIdle = 3.0, MinTyping = 3.0;
        public const double KeyGap = 1.2;         // 两次按键隔得不超过这么久算一直在打
        public const float FrameStill = 1.5f;     // 低分辨率灰度图平均每像素差（0–255）小于它算画面没变

        // 纯规则（自测直接喂数据）：diffs[i] = 第 i 个 0.25 秒格子（从 a 起）前后两张图的差
        public static List<SpeedSuggestion> Find(Track tr, float[] diffs, double a, double b, List<CutSegment> existing)
        {
            var outL = new List<SpeedSuggestion>();
            int n = Math.Max(0, (int)Math.Floor((b - a) / Bin));
            if (n <= 0) return outL;
            // 1) 空档：格子里没有点击、按键，鼠标没怎么动，画面也没变
            var busy = new bool[n];
            int? Cell(double t) { int i = (int)Math.Floor((t - a) / Bin); return i >= 0 && i < n ? i : (int?)null; }
            foreach (var c in tr.Clicks) if (Cell(c[0]) is int i) busy[i] = true;
            foreach (var k in tr.Keys) if (Cell(k) is int i) busy[i] = true;
            double[] prev = null;
            foreach (var m in tr.Moves)
            {
                if (prev != null && (Math.Sqrt((m[1] - prev[1]) * (m[1] - prev[1]) + (m[2] - prev[2]) * (m[2] - prev[2])) > 3 || m[4] != prev[4]) && Cell(m[0]) is int i) busy[i] = true;
                prev = m;
            }
            for (int i = 0; i < n && diffs != null && i < diffs.Length; i++) if (diffs[i] > FrameStill) busy[i] = true;
            int? runStart = null;
            for (int i = 0; i <= n; i++)
            {
                bool free = i < n && !busy[i];
                if (free) { if (runStart == null) runStart = i; continue; }
                if (runStart is int s)
                {
                    double t0 = a + s * Bin, t1 = a + i * Bin;
                    if (t1 - t0 >= MinIdle) outL.Add(new SpeedSuggestion { Start = t0 + 0.3, End = t1 - 0.3, Typing = false });   // 两头留一点，别把动作切掉
                    runStart = null;
                }
            }
            // 2) 一直在打字：按键一下接一下（间隔 ≤ 1.2 秒）持续 ≥ 3 秒，中间点击不超过 1 次
            var keys = tr.Keys.Where(k => k >= a && k <= b).OrderBy(k => k).ToList();
            int ii = 0;
            while (ii < keys.Count)
            {
                int j = ii;
                while (j + 1 < keys.Count && keys[j + 1] - keys[j] <= KeyGap) j++;
                double s = keys[ii], e = keys[j];
                int clicks = tr.Clicks.Count(c => c[0] >= s && c[0] <= e);
                if (e - s >= MinTyping && clicks <= 1) outL.Add(new SpeedSuggestion { Start = s + 0.3, End = e, Typing = true });
                ii = j + 1;
            }
            // 已经剪掉 / 快放的地方不再建议；空档和打字重叠时留打字的
            outL = outL.Where(g => g.End - g.Start >= 1.0 && !(existing ?? new List<CutSegment>()).Any(c => c.Start < g.End && c.End > g.Start)).OrderBy(g => g.Start).ToList();
            var res = new List<SpeedSuggestion>();
            foreach (var g in outL)
            {
                if (res.Count > 0 && res[res.Count - 1].End > g.Start)
                {
                    if (g.Typing && !res[res.Count - 1].Typing) res[res.Count - 1] = g;
                    continue;
                }
                res.Add(g);
            }
            return res;
        }

        // 画面变化：每 0.25 秒取一张 80 像素宽的灰度小图，算和上一张的平均差（ffmpeg 一次解完）
        public static float[] FrameDiffs(string raw, double a, double b)
        {
            int n = Math.Max(0, (int)Math.Floor((b - a) / Bin));
            var outL = new List<float>();
            if (n <= 0) return outL.ToArray();
            const int w = 80, h = 48;
            var psi = new ProcessStartInfo(Studio.FfmpegPath, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "-v error -ss {0:0.###} -i \"{1}\" -t {2:0.###} -an -vf \"fps=4,scale={3}:{4}:flags=area,format=gray\" -f rawvideo -pix_fmt gray pipe:1", a, raw, b - a + Bin, w, h))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                var st = p.StandardOutput.BaseStream;
                byte[] last = null;
                while (outL.Count < n)
                {
                    var buf = new byte[w * h]; int got = 0;
                    while (got < buf.Length) { int k = st.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                    if (got < buf.Length) break;
                    if (last != null) { long s = 0; for (int i = 0; i < buf.Length; i++) s += Math.Abs(buf[i] - last[i]); outL.Add(s / (float)buf.Length); }
                    last = buf;
                }
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            while (outL.Count < n) outL.Add(0);
            return outL.ToArray();
        }
    }
}
