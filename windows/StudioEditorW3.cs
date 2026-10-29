// 剪辑窗口的 W3 部分（照 Mac 版）：预览里叠快捷键键帽和摄像头画中画（和导出同一套 Canvas），在预览上拖摄像头小窗，
// 时间轴「布局」轨（小窗 / 左右分屏 / 人像在左 / 摄像头全屏 / 隐藏）的增删改
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class StudioEditor
    {
        public string SelectedLayout;          // 选中的布局段 id
        CameraFrames camFrames;                // 预览取摄像头帧（一直开着一个，顺着读）
        readonly object camGate = new object();

        // 素材里有没有摄像头（camera.mp4 在）
        bool? hasCam;
        public bool HasCamera { get { if (hasCam == null) hasCam = Track.Camera != null && File.Exists(Path.Combine(Dir, Track.Camera.File)); return hasCam.Value; } }

        void InitW3()
        {
            preview.GrabCamera = GrabCamera; preview.MoveCamera = MoveCamera; preview.ReleaseCamera = () => DragEnd();
            FormClosed += (s, e) => { lock (camGate) { camFrames?.Dispose(); camFrames = null; } };
        }

        // ---- 预览：合成好的画布上叠键帽、摄像头（同导出） ----
        void ApplyCanvas(StudioScene sc, byte[] dst, double t)
        {
            var p = Project;
            bool keys = p.Look.ShowKeys && Track.Shortcuts != null && Track.Shortcuts.Count > 0;
            bool cam = HasCamera && p.Camera != null && p.Camera.Show;
            if (!keys && !cam) return;
            int CW = (int)sc.Lay.cw, CH = (int)sc.Lay.ch;
            var map = new TimeMap(p);
            KeyLayer kl = keys ? KeyLayer.Make(Track, p, map, CW, CH, p.CaptionStyle.Show && p.Captions.Count > 0) : null;
            CameraLayer cl = null;
            if (cam)
                lock (camGate)
                {
                    if (camFrames == null) try { camFrames = new CameraFrames(Path.Combine(Dir, Track.Camera.File)); } catch { camFrames = null; }
                    if (camFrames != null) cl = new CameraLayer(p.Camera, p.Layouts, camFrames, Track.Camera.Offset, CW, CH);
                }
            var canvas = Canvas.Shared(CW, CH, cl, kl, cl != null ? sc.Cpu.PlainBackground() : null);
            lock (camGate) canvas.Apply(dst, false, t, map.Out(t));
        }

        // ---- 在预览上拖摄像头小窗（只在当时是「小窗」布局时）；x、y = 预览画面里的比例 ----
        double grabDx, grabDy;
        public bool GrabCamera(double x, double y)
        {
            var p = Project;
            if (!HasCamera || p.Camera == null || !p.Camera.Show || Picking) return false;
            if ((p.Layouts.FirstOrDefault(s => Time >= s.Start && Time < s.End)?.Mode ?? "pip") != "pip") return false;
            StudioScene sc; lock (sceneGate) sc = scene;
            if (sc == null) return false;
            double cw = sc.Lay.cw, ch = sc.Lay.ch;
            var (px, py, side) = CameraLayer.PipRect(p.Camera, cw, ch);
            double mx = x * cw, my = y * ch;
            if (mx < px || mx > px + side || my < py || my > py + side) return false;
            grabDx = (px + side / 2) / cw - x; grabDy = (py + side / 2) / ch - y;
            DragBegin();
            return true;
        }

        public void MoveCamera(double x, double y)
        {
            double nx = Math.Min(1, Math.Max(0, x + grabDx)), ny = Math.Min(1, Math.Max(0, y + grabDy));
            DragUpdate(p => { if (p.Camera != null) { p.Camera.X = nx; p.Camera.Y = ny; } });
        }

        // ---- 摄像头样式（能撤销） ----
        public void EditCamera(Action<CameraSettings> change) => Edit(p => { if (p.Camera == null) p.Camera = Track.Camera != null ? CameraSettings.From(Track.Camera) : new CameraSettings(); change(p.Camera); });
        public void DragCamera(Action<CameraSettings> change) { DragBegin(); DragUpdate(p => { if (p.Camera == null) p.Camera = new CameraSettings(); change(p.Camera); }); }

        // ---- 布局轨 ----
        // 段左右能伸到哪（不和别的布局段重叠）
        public (double lo, double hi) LayoutRoom(string id, StudioProject p)
        {
            var me = p.Layouts.FirstOrDefault(s => s.Id == id);
            double lo = 0, hi = Duration;
            if (me == null) return (lo, hi);
            foreach (var s in p.Layouts)
            {
                if (s.Id == id) continue;
                if (s.End <= me.Start + 1e-6) lo = Math.Max(lo, s.End);
                if (s.Start >= me.End - 1e-6) hi = Math.Min(hi, s.Start);
            }
            return (lo, hi);
        }

        // 在 t 处加一段（默认 2 秒「摄像头全屏」），挤不下就缩短
        public void AddLayout(double t)
        {
            if (!HasCamera) return;
            double lo = 0, hi = Duration;
            foreach (var s in Project.Layouts)
            {
                if (t >= s.Start && t < s.End) return;   // 已经在一段里
                if (s.End <= t) lo = Math.Max(lo, s.End);
                if (s.Start > t) hi = Math.Min(hi, s.Start);
            }
            if (hi - lo < 0.3) return;
            double len = Math.Min(2, hi - lo);
            double s0 = Math.Max(lo, Math.Min(t, hi - len));
            var n = new CameraLayoutSeg { Start = s0, End = s0 + len, Mode = "full" };
            Edit(p => { if (p.Camera == null && Track.Camera != null) p.Camera = CameraSettings.From(Track.Camera); p.Layouts.Add(n); p.Layouts = p.Layouts.OrderBy(x => x.Start).ToList(); });
            Select("layout", n.Id);
        }

        public void SetLayoutMode(string mode)
        {
            var id = SelectedLayout;
            Edit(p => { var s = p.Layouts.FirstOrDefault(x => x.Id == id); if (s != null) s.Mode = mode; });
            Touch();
        }

        // ---- 布局属性条：选中布局段才出现 ----
        readonly Panel layInspector = new Panel();
        readonly Label layTitle = new Label(), layTime = new Label();
        readonly Dictionary<string, Button> layButtons = new Dictionary<string, Button>();
        readonly Button layDelete = new Button();

        void BuildLayoutInspector()
        {
            layInspector.Dock = DockStyle.Top; layInspector.Height = 34; layInspector.Visible = false;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            layTitle.Text = L("布局", "Layout"); layTitle.AutoSize = true; layTitle.Font = new Font(Font, FontStyle.Bold); layTitle.Margin = new Padding(0, 8, 8, 0);
            layTime.AutoSize = true; layTime.ForeColor = Theme.Secondary; layTime.Margin = new Padding(0, 8, 14, 0);
            flow.Controls.Add(layTitle); flow.Controls.Add(layTime);
            foreach (var m in CameraLayoutSeg.Modes)
            {
                var b = new Button(); var mode = m;
                Flat(b, CameraLayoutSeg.Name(m, L), (s, e) => SetLayoutMode(mode));
                layButtons[m] = b; flow.Controls.Add(b);
            }
            Flat(layDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected());
            layInspector.Controls.Add(flow);
            layDelete.Dock = DockStyle.Right;
            layInspector.Controls.Add(layDelete);
        }

        void SyncLayoutInspector()
        {
            var seg = Project.Layouts.FirstOrDefault(s => s.Id == SelectedLayout);
            layInspector.Visible = seg != null;
            if (seg == null) return;
            layTime.Text = Clock(seg.Start) + " – " + Clock(seg.End);
            foreach (var kv in layButtons) { bool on = kv.Key == seg.Mode; kv.Value.BackColor = on ? Theme.Accent : Theme.Control; kv.Value.ForeColor = on ? Color.White : Theme.Text; }
        }

        bool DeleteSelectedLayout()
        {
            if (SelectedLayout == null) return false;
            var id = SelectedLayout;
            Edit(p => p.Layouts.RemoveAll(s => s.Id == id));
            Select(null, null);
            return true;
        }
    }
}
