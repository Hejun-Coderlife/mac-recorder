// 背景音乐（照 Mac 版 Music.swift）：选一首音乐（拷进素材夹 music\），按成片时间铺在「音乐」轨上；音量、淡入淡出、循环到片尾，
// 有讲解或原声音在说话时自动压低（预览和导出用同一条压低曲线）。
// 导出 / 预览声音：这里先把音乐按成片时间混成一条立体声 wav，再交给声音滤镜图和原声、讲解、点击声一起混、限幅
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ScreenRecorder
{
    // 音乐轨上的一段：start 是成片时间（音乐不跟着剪掉 / 快放变速，按成片时间一直往后放）；字段名同 Mac 版
    sealed class MusicClip
    {
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public string File = "";                  // 相对素材夹：music\xxx.m4a（存的时候用 /，Mac 也认）
        public string Name = "";                  // 原文件名（显示用）
        public double Start;                      // 成片时间
        public double TrimIn;                     // 从音乐的第几秒开始用
        public double TrimOut;                    // 用到第几秒
        public double FileLength;
        public double Length => Math.Max(0, TrimOut - TrimIn);
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        static readonly string[] Known = { "id", "file", "name", "start", "trimIn", "trimOut", "fileLength" };

        public MusicClip Clone() { var c = (MusicClip)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        public void Write(JsonW w)
        {
            w.BeginItem();
            w.Str("file", File); w.Num("fileLength", FileLength); w.Str("id", Id); w.Str("name", Name); w.Num("start", Start); w.Num("trimIn", TrimIn); w.Num("trimOut", TrimOut);
            w.Extra(Extra); w.End();
        }
        public static MusicClip From(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var j = new JsonR(o);
            return new MusicClip
            {
                Id = j.Str("id", Guid.NewGuid().ToString().ToUpperInvariant()), File = j.Str("file", ""), Name = j.Str("name", ""), Start = j.Num("start", 0),
                TrimIn = j.Num("trimIn", 0), TrimOut = j.Num("trimOut", 0), FileLength = j.Num("fileLength", 0), Extra = JsonR.Unknown(o, Known),
            };
        }
    }

    sealed class MusicTrack
    {
        public List<MusicClip> Clips = new List<MusicClip>();
        public double Volume = 0.5;               // 0–1.5
        public double FadeIn = 1.0, FadeOut = 2.0; // 秒
        public bool Loop = true;                  // 最后一段放完了从头再来，一直铺到片尾
        public bool Duck = true;                  // 有讲解 / 原声音在说话时压低
        public double DuckDB = -14;               // 压低多少（-6 到 -30 dB）
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        static readonly string[] Known = { "clips", "volume", "fadeIn", "fadeOut", "loop", "duck", "duckDB" };

        public float DuckLevel => Duck ? (float)Math.Pow(10, Math.Min(0, DuckDB) / 20) : 1f;

        public void Write(JsonW w)
        {
            w.Begin();
            w.BeginArray("clips"); foreach (var c in Clips) c.Write(w); w.EndArray();
            w.Bool("duck", Duck); w.Num("duckDB", DuckDB); w.Num("fadeIn", FadeIn); w.Num("fadeOut", FadeOut); w.Bool("loop", Loop); w.Num("volume", Volume);
            w.Extra(Extra); w.End();
        }
        public string ToJson() { var w = new JsonW(); Write(w); return w.ToString(); }
        public static MusicTrack From(Dictionary<string, object> o)
        {
            var z = new MusicTrack();
            if (o == null) return z;
            var j = new JsonR(o);
            if (o.TryGetValue("clips", out var cv) && cv is List<object> cs) z.Clips = cs.OfType<Dictionary<string, object>>().Select(MusicClip.From).Where(c => c != null).ToList();
            z.Volume = j.Num("volume", z.Volume); z.FadeIn = j.Num("fadeIn", z.FadeIn); z.FadeOut = j.Num("fadeOut", z.FadeOut);
            z.Loop = j.Bool("loop", z.Loop); z.Duck = j.Bool("duck", z.Duck); z.DuckDB = j.Num("duckDB", z.DuckDB);
            z.Extra = JsonR.Unknown(o, Known);
            return z;
        }

        // 实际放的每一次（成片时间）：每段放一次；开了循环时最后一段一直重复到片尾。超过片尾的截掉
        public struct Occ { public MusicClip Clip; public double At, From, Len; public bool FadeIn, FadeOut; }
        public List<Occ> Occurrences(double filmEnd)
        {
            var cs = Clips.Where(c => c.Length > 0.05).OrderBy(c => c.Start).ToList();
            var outL = new List<Occ>();
            for (int k = 0; k < cs.Count; k++)
            {
                var c = cs[k];
                if (c.Start >= filmEnd) continue;
                double at = c.Start;
                bool repeats = Loop && k == cs.Count - 1;
                do
                {
                    double len = Math.Min(c.Length, filmEnd - at);
                    if (len <= 0.01) break;
                    outL.Add(new Occ { Clip = c, At = at, From = c.TrimIn, Len = len, FadeIn = at == c.Start, FadeOut = false });
                    at += c.Length;
                } while (repeats && at < filmEnd - 0.01);
                if (outL.Count > 0 && outL[outL.Count - 1].Clip.Id == c.Id) { var l = outL[outL.Count - 1]; l.FadeOut = true; outL[outL.Count - 1] = l; }   // 这一段最后一次放：淡出
            }
            return outL;
        }

        // 某一次在它第 x 秒（0…len）的音量（不含压低）：总音量 × 淡入 × 淡出；循环接缝处 15 毫秒的小淡入淡出，免得「咔」一声
        public float Gain(Occ o, double x)
        {
            double g = Volume, edge = 0.015;
            if (o.FadeIn) { if (FadeIn > 0.01) g *= Math.Min(1, x / FadeIn); } else g *= Math.Min(1, x / edge);
            double tail = o.Len - x;
            if (o.FadeOut) { if (FadeOut > 0.01) g *= Math.Min(1, Math.Max(0, tail) / FadeOut); } else g *= Math.Min(1, Math.Max(0, tail) / edge);
            return (float)Math.Max(0, g);
        }
    }

    // 压低曲线（成片时间，每秒 100 个点）：讲解的范围 + 原声音里有人说话的地方（音量包络超过门槛）→ 压到 duckLevel；
    // 说话前 0.15 秒开始压（0.12 秒压到位）、说完停 0.3 秒再慢慢回来（0.4 秒）。同 Mac 版
    static class MusicDuck
    {
        public const double Rate = 100;
        public const float Threshold = 0.012f;   // 原声音 10 毫秒 RMS 超过约 -38 dBFS 算在说话

        public static float[] Curve(float level, double filmEnd, List<(double a, double b)> voice, float[] env, TimeMap map)
        {
            int n = Math.Max(1, (int)Math.Ceiling(filmEnd * Rate) + 1);
            var outp = new float[n];
            if (level >= 0.999f) { for (int i = 0; i < n; i++) outp[i] = 1; return outp; }
            var talk = new bool[n];
            foreach (var (a, b) in voice ?? new List<(double, double)>())
            {
                int i0 = Math.Max(0, (int)Math.Floor(a * Rate)), i1 = Math.Min(n, (int)Math.Ceiling(b * Rate));
                for (int i = i0; i < i1; i++) talk[i] = true;
            }
            if (env != null && env.Length > 0)
                for (int i = 0; i < n; i++)
                {
                    double s = map == null || map.IsIdentity ? i / Rate : map.Src(i / Rate);
                    int j = (int)(s * Rate);
                    if (j >= 0 && j < env.Length && env[j] > Threshold) talk[i] = true;
                }
            int pre = (int)(0.15 * Rate), hold = (int)(0.3 * Rate);
            var nextTalk = new int[n]; int nx = int.MaxValue;
            for (int i = n - 1; i >= 0; i--) { if (talk[i]) nx = i; nextTalk[i] = nx; }
            int lastTalk = -100000; float g = 1;
            float down = (1 - level) / (float)(0.12 * Rate), up = (1 - level) / (float)(0.4 * Rate);
            for (int i = 0; i < n; i++)
            {
                if (talk[i]) lastTalk = i;
                bool active = i - lastTalk <= hold || (nextTalk[i] != int.MaxValue && nextTalk[i] - i <= pre);
                float tgt = active ? level : 1;
                g = tgt < g ? Math.Max(tgt, g - down) : Math.Min(tgt, g + up);
                outp[i] = g;
            }
            return outp;
        }

        public static float At(float[] c, double t)
        {
            if (c == null || c.Length == 0) return 1;
            double x = Math.Max(0, t * Rate); int i = (int)x; float u = (float)(x - i);
            if (i >= c.Length - 1) return c[c.Length - 1];
            return c[i] + (c[i + 1] - c[i]) * u;
        }

        // 原声音的音量包络（素材时间，每 10 毫秒一个 RMS，16 kHz 单声道）。有麦克风（Mac 录的单声道那条）就只看麦克风，没有就看全部；按各自音量
        public static float[] Envelope(string raw, VideoInfo info, AudioSettings a)
        {
            if (a.Muted || info.AudioChannels.Count == 0) return null;
            var idx = new List<int>();
            bool mac2 = info.AudioChannels.Count > 1;
            for (int i = 0; i < info.AudioChannels.Count; i++) if (mac2 && info.AudioChannels[i] == 1) idx.Add(i);
            if (idx.Count == 0) for (int i = 0; i < info.AudioChannels.Count; i++) idx.Add(i);
            float[] sum = null;
            foreach (int i in idx)
            {
                double vol = Math.Min(1, Studio.TrackVolume(a, info, i));
                if (vol < 0.001) continue;
                var pcm = Studio.DecodePcm(raw, 16000, null, i);
                if (pcm == null) continue;
                if (sum == null) sum = new float[pcm.Length];
                for (int k = 0; k < Math.Min(sum.Length, pcm.Length); k++) sum[k] += pcm[k] * (float)vol;
            }
            if (sum == null) return null;
            var env = new float[sum.Length / 160];
            for (int w = 0; w < env.Length; w++) { double acc = 0; for (int k = 0; k < 160; k++) { double v = sum[w * 160 + k]; acc += v * v; } env[w] = (float)Math.Sqrt(acc / 160); }
            return env;
        }
    }

    static class MusicMix
    {
        // 一个音乐文件解成 48k 立体声交错浮点
        public static float[] DecodeStereo(string file)
        {
            var psi = new ProcessStartInfo(Studio.FfmpegPath, $"-v error -i \"{file}\" -vn -map 0:a:0 -ac 2 -ar 48000 -f f32le pipe:1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { }; p.BeginErrorReadLine();
                using (var ms = new MemoryStream()) { p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit(); var b = ms.ToArray(); if (b.Length < 8) return null; var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, f.Length * 4); return f; }
            }
        }

        // 时长（秒）：选音乐时记下 fileLength
        // 音乐文件多长（Studio.Probe 要有画面，纯音频会报错，这里只读 Duration 那行）
        public static double Duration(string file)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Studio.FfmpegPath, $"-hide_banner -i \"{file}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
                string err;
                using (var p = System.Diagnostics.Process.Start(psi)) { err = p.StandardError.ReadToEnd(); p.WaitForExit(); }
                var d = System.Text.RegularExpressions.Regex.Match(err, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
                return d.Success ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            }
            catch { return 0; }
        }

        // 音乐按成片时间混成一条：从成片 t0 起、len 秒，立体声交错 48k（乘音量、淡入淡出、压低曲线）；没有音乐返回 null
        public static float[] Build(string dir, StudioProject p, TimeMap map, double t0, double len, List<(double a, double b)> voice, float[] env)
        {
            var m = p.Music;
            if (m == null || m.Clips.Count == 0 || m.Volume < 0.001) return null;
            double filmEnd = map.Out(p.TrimEnd);
            var occ = m.Occurrences(filmEnd);
            if (occ.Count == 0) return null;
            var pcm = new Dictionary<string, float[]>();
            foreach (var f in occ.Select(o => o.Clip.File).Distinct())
            {
                var path = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                var d = DecodeStereo(path);
                if (d != null) pcm[f] = d;
            }
            if (pcm.Count == 0) return null;
            var duck = m.Duck ? MusicDuck.Curve(m.DuckLevel, filmEnd, voice, env, map) : null;
            const int sr = 48000;
            int n = Math.Max(1, (int)Math.Ceiling(len * sr));
            var outp = new float[n * 2];
            bool any = false;
            foreach (var o in occ)
            {
                if (!pcm.TryGetValue(o.Clip.File, out var src)) continue;
                int frames = src.Length / 2;
                long occStart = (long)Math.Round(o.At * sr), occLen = (long)Math.Round(o.Len * sr), from = (long)Math.Round(o.From * sr), a0 = (long)Math.Round(t0 * sr);
                long k0 = Math.Max(0, occStart - a0), k1 = Math.Min(n, occStart + occLen - a0);
                for (long k = k0; k < k1; k++)
                {
                    long idx = a0 + k - occStart, s = from + idx;
                    if (s < 0 || s >= frames) continue;
                    float g = m.Gain(o, idx / (double)sr) * (duck != null ? MusicDuck.At(duck, (a0 + k) / (double)sr) : 1);
                    outp[k * 2] += src[s * 2] * g; outp[k * 2 + 1] += src[s * 2 + 1] * g;
                    any = true;
                }
            }
            return any ? outp : null;
        }

        // 立体声浮点 → wav（32 位浮点，48k：音量开到 150% 也不在这里削掉，后面统一限幅）
        public static byte[] Wav(float[] stereo, int sr = 48000)
        {
            int n = stereo.Length;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + n * 4); w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); w.Write(16); w.Write((short)3); w.Write((short)2); w.Write(sr); w.Write(sr * 8); w.Write((short)8); w.Write((short)32);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(n * 4);
                foreach (var v in stereo) w.Write(v);
                return ms.ToArray();
            }
        }
    }
}
