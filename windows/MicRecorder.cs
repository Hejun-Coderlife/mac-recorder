// 录讲解用的麦克风录音：Windows 自带的 waveIn（winmm，不用另装库），48k 单声道 16 位，边录边写 wav、给出音量电平。
// 自测：VOICE_INJECT=某个声音文件 → 不开真麦克风，按真实时间把这个文件当成录到的声音（CI 机器上没有麦克风）
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenRecorder
{
    sealed class MicRecorder : IDisposable
    {
        public const int Rate = 48000;
        public volatile float Level;            // 最近 0.1 秒的峰值 0–1（画电平用）
        public double Seconds => samples / (double)Rate;
        public readonly DateTime StartedAt = DateTime.Now;
        long samples;
        readonly FileStream file;
        readonly Thread thread;
        volatile bool stop;
        IntPtr hwi;
        readonly IntPtr[] hdrs = new IntPtr[6], bufs = new IntPtr[6];
        const int BufSamples = Rate / 20;        // 每块 50 毫秒
        Exception failure;

        public MicRecorder(string wavPath)
        {
            file = File.Create(wavPath);
            file.Write(new byte[44], 0, 44);     // 先占位，停的时候补上头
            var inject = Environment.GetEnvironmentVariable("VOICE_INJECT");
            if (!string.IsNullOrEmpty(inject))
            {
                var pcm = Studio.DecodePcm(inject, Rate) ?? new float[0];
                thread = new Thread(() => Inject(pcm)) { IsBackground = true, Name = "mic-inject" };
            }
            else
            {
                Open();
                thread = new Thread(Poll) { IsBackground = true, Name = "mic" };
            }
            thread.Start();
        }

        void Open()
        {
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = Rate * 2, cbSize = 0 };
            int r = waveInOpen(out hwi, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0)
            {
                file.Dispose();
                throw new Exception(App.L("打不开麦克风：没有接麦克风，或者没给权限（设置 → 隐私和安全性 → 麦克风，打开「允许桌面应用访问麦克风」）",
                                          "Can't open the microphone: none connected, or no permission (Settings → Privacy & security → Microphone → allow desktop apps)") + $" ({r})");
            }
            for (int i = 0; i < hdrs.Length; i++)
            {
                bufs[i] = Marshal.AllocHGlobal(BufSamples * 2);
                hdrs[i] = Marshal.AllocHGlobal(HdrSize);
                for (int k = 0; k < HdrSize; k++) Marshal.WriteByte(hdrs[i], k, 0);
                Marshal.WriteIntPtr(hdrs[i], 0, bufs[i]);
                Marshal.WriteInt32(hdrs[i], 8, BufSamples * 2);
                waveInPrepareHeader(hwi, hdrs[i], HdrSize);
                waveInAddBuffer(hwi, hdrs[i], HdrSize);
            }
            waveInStart(hwi);
        }

        // 轮询：哪块录满了就拿出来写进文件，再还回去接着录
        void Poll()
        {
            var tmp = new byte[BufSamples * 2];
            try
            {
                while (true)
                {
                    bool any = false;
                    foreach (var h in hdrs)
                    {
                        int flags = Marshal.ReadInt32(h, 24);
                        if ((flags & WHDR_DONE) == 0) continue;
                        int n = Marshal.ReadInt32(h, 12);
                        if (n > 0) { Marshal.Copy(Marshal.ReadIntPtr(h, 0), tmp, 0, n); Write(tmp, n); }
                        any = true;
                        if (stop) continue;
                        Marshal.WriteInt32(h, 24, flags & ~WHDR_DONE);
                        Marshal.WriteInt32(h, 12, 0);
                        waveInAddBuffer(hwi, h, HdrSize);
                    }
                    if (stop && !any) break;
                    if (!any) Thread.Sleep(10);
                }
            }
            catch (Exception e) { failure = e; }
        }

        // 自测：按真实时间把文件里的声音一块一块「录」进来
        void Inject(float[] pcm)
        {
            var tmp = new byte[BufSamples * 2];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long pos = 0;
            while (!stop)
            {
                long due = (long)(sw.Elapsed.TotalSeconds * Rate);
                while (pos + BufSamples <= due)
                {
                    for (int k = 0; k < BufSamples; k++)
                    {
                        float v = pos + k < pcm.Length ? pcm[pos + k] : 0;
                        short s = (short)Math.Round(Math.Max(-1, Math.Min(1, v)) * 32767);
                        tmp[k * 2] = (byte)s; tmp[k * 2 + 1] = (byte)(s >> 8);
                    }
                    Write(tmp, BufSamples * 2); pos += BufSamples;
                }
                Thread.Sleep(10);
            }
        }

        void Write(byte[] data, int n)
        {
            lock (file) { file.Write(data, 0, n); samples += n / 2; }
            int peak = 0;
            for (int i = 0; i + 1 < n; i += 2) { int v = Math.Abs((short)(data[i] | data[i + 1] << 8)); if (v > peak) peak = v; }
            Level = peak / 32768f;
        }

        // 停：把没写完的块写完，补上 wav 头；返回录了几秒
        public double Stop()
        {
            if (stop) return Seconds;
            stop = true;
            if (hwi != IntPtr.Zero) { waveInStop(hwi); waveInReset(hwi); }
            thread.Join(3000);
            if (hwi != IntPtr.Zero)
            {
                foreach (var h in hdrs) waveInUnprepareHeader(hwi, h, HdrSize);
                waveInClose(hwi); hwi = IntPtr.Zero;
                for (int i = 0; i < hdrs.Length; i++) { Marshal.FreeHGlobal(hdrs[i]); Marshal.FreeHGlobal(bufs[i]); }
            }
            lock (file)
            {
                long data = samples * 2;
                file.Position = 0;
                var w = new BinaryWriter(file, Encoding.ASCII, true);
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((int)(36 + data)); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write((int)data);
                w.Flush();
                file.Dispose();
            }
            if (failure != null) throw failure;
            return Seconds;
        }

        public void Dispose() { try { Stop(); } catch { } }

        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const int WHDR_DONE = 1;
        static readonly int HdrSize = IntPtr.Size == 8 ? 48 : 32;   // WAVEHDR：x64 下 48 字节（dwFlags 在 24）
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr hwi, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] static extern int waveInStop(IntPtr hwi);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr hwi);
    }
}
