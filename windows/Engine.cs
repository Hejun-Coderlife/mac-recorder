using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScreenRecorderLib;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    enum SourceKind { Region, Display, Window }

    // 列表里的一项：选定区域 / 整个屏幕 / 单个窗口
    sealed class Source
    {
        public SourceKind Kind;
        public string Id, Title, Detail;
        public string DeviceName;      // 屏幕（区域也记它在哪块屏幕上）
        public Rectangle Area;         // 区域：相对那块屏幕左上角的物理像素
        public IntPtr Hwnd;            // 窗口
    }

    static class Sources
    {
        public static List<Source> List(Source region, bool includeSelf = false)
        {
            var list = new List<Source>();
            if (region != null) list.Add(region);
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                list.Add(new Source
                {
                    Kind = SourceKind.Display, Id = "d" + s.DeviceName, DeviceName = s.DeviceName,
                    Title = screens.Length > 1 ? L($"整个屏幕 {i + 1}", $"Entire Screen {i + 1}") : L("整个屏幕", "Entire Screen"),
                    Detail = $"{s.Bounds.Width} × {s.Bounds.Height}" + (s.Primary && screens.Length > 1 ? L("（主屏幕）", " (main)") : ""),
                });
            }
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (var w in Recorder.GetWindows())
            {
                try
                {
                    if (!w.IsValidWindow() || w.IsMinmimized() || string.IsNullOrWhiteSpace(w.Title)) continue;
                    GetWindowThreadProcessId(w.Handle, out uint pid);
                    if (pid == self && !includeSelf) continue;
                    string app = "";
                    try { app = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                    GetWindowRect(w.Handle, out RECT r);
                    list.Add(new Source
                    {
                        Kind = SourceKind.Window, Id = "w" + w.Handle.ToInt64(), Hwnd = w.Handle,
                        Title = string.IsNullOrEmpty(app) ? w.Title : app,
                        Detail = $"{w.Title} · {r.Right - r.Left} × {r.Bottom - r.Top}",
                    });
                }
                catch { }
            }
            return list;
        }

        public static Source Region(Screen screen, Rectangle area) => new Source
        {
            Kind = SourceKind.Region, Id = "r", DeviceName = screen.DeviceName, Area = area,
            Title = L("选定区域", "Selected Area"),
            Detail = $"{area.Width} × {area.Height}",
        };

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    }

    // 一次录制：开始、停止，结束 / 出错回调（在后台线程触发）
    sealed class Session
    {
        readonly Recorder rec;
        public string Path { get; }
        public event Action<string> Completed;   // 文件路径
        public event Action<string> Failed;      // 错误信息
        // Studio 用：原始画面第一帧出来的时刻（Clock 时钟），轨迹按它对齐
        public double FirstFrameAt => firstFrame > 0 ? firstFrame : started;
        double firstFrame, started;

        public Session(Source src, bool systemAudio, bool microphone, bool cursor, string path)
        {
            Path = path;
            RecordingSourceBase source;
            switch (src.Kind)
            {
                case SourceKind.Window:
                    source = new WindowRecordingSource(src.Hwnd) { IsCursorCaptureEnabled = cursor, IsBorderRequired = false };
                    break;
                case SourceKind.Region:
                    source = new DisplayRecordingSource(src.DeviceName)
                    {
                        IsCursorCaptureEnabled = cursor,
                        SourceRect = new ScreenRect(src.Area.X, src.Area.Y, src.Area.Width, src.Area.Height),
                    };
                    break;
                default:
                    source = new DisplayRecordingSource(src.DeviceName) { IsCursorCaptureEnabled = cursor };
                    break;
            }
            var opts = new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { source } },
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video, Stretch = StretchMode.Uniform },
                // 电脑声音和麦克风在库里混成一条音轨（抖音、微信等只放第一条音轨）
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = systemAudio || microphone,
                    IsOutputDeviceEnabled = systemAudio,
                    IsInputDeviceEnabled = microphone,
                    Bitrate = AudioBitrate.bitrate_192kbps,
                    Channels = AudioChannels.Stereo,
                },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.High },
                    Quality = 80,
                    Framerate = 60,
                    IsFixedFramerate = false,
                    IsHardwareEncodingEnabled = true,
                    IsMp4FastStartEnabled = true,
                },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = cursor },
            };
            rec = Recorder.CreateRecorder(opts);
            rec.OnRecordingComplete += (s, e) => Completed?.Invoke(e.FilePath);
            rec.OnRecordingFailed += (s, e) => Failed?.Invoke(e.Error);
            rec.OnFrameRecorded += (s, e) => { if (firstFrame == 0) firstFrame = Clock.Now; };
            rec.OnStatusChanged += (s, e) => { if (e.Status == RecorderStatus.Recording && started == 0) started = Clock.Now; };
        }

        public void Start() => rec.Record(Path);
        public void Stop() => rec.Stop();
    }
}
