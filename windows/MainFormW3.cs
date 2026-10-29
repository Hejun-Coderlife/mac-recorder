// 主窗口的 W3 部分：「摄像头」开关 + 选摄像头（开着时屏幕上浮一个圆形小窗）、「录制时勿扰」
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class MainForm
    {
        readonly CheckBox camera = new CheckBox(), dnd = new CheckBox();
        readonly ComboBox camDevice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        CameraBubble bubble;
        CameraCapture cam;          // 开着的摄像头（预览给小窗；录 Studio 时同时写 camera.mp4）

        Control BuildW3Options()
        {
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4), WrapContents = true };
            camera.Text = L("摄像头", "Camera"); camera.AutoSize = true; camera.Margin = new Padding(0, 4, 6, 0);
            tips.SetToolTip(camera, L("屏幕上浮一个圆形摄像头小窗（能拖，右键换形状大小）。开着 Studio 效果录时摄像头单独录，剪辑时还能换布局（分屏 / 全屏）；普通录制小窗直接录进画面（只录某个窗口时录不到小窗）",
                                      "Shows a round camera bubble you can drag (right-click for shape / size). With Studio effects the camera is recorded separately so you can change the layout later; otherwise the bubble is recorded as part of the screen (not when recording a single window)"));
            camDevice.Margin = new Padding(0, 0, 22, 0); camDevice.Visible = false;
            dnd.Text = L("录制时勿扰（不弹通知）", "Do not disturb while recording"); dnd.AutoSize = true; dnd.Margin = new Padding(0, 4, 0, 0);
            dnd.Checked = Settings.Get("dnd", false);
            dnd.CheckedChanged += (s, e) => Settings.Set("dnd", dnd.Checked);
            tips.SetToolTip(dnd, L("录制期间临时关掉系统通知横幅，录完自动换回来；Windows 11 的「勿扰」开关本身不受影响，想更彻底可以手动打开系统的「勿扰」",
                                   "Turns off notification banners while recording and restores them afterwards. The Windows 11 Do Not Disturb switch itself isn't changed; turn it on yourself to be completely sure"));
            camera.Checked = Settings.Get("camera", false);
            camera.CheckedChanged += (s, e) => { Settings.Set("camera", camera.Checked); if (camera.Checked) OpenCamera(); else CloseCamera(); };
            camDevice.SelectedIndexChanged += (s, e) => { if (camDevice.SelectedItem is string d) { Settings.SetString("cameraDevice", d); if (camera.Checked && cam != null && session == null) { CloseCamera(); OpenCamera(); } } };
            row.Controls.AddRange(new Control[] { camera, camDevice, dnd });
            Shown += (s, e) => { if (camera.Checked) OpenCamera(); };
            return row;
        }

        string Device => camDevice.SelectedItem as string ?? Settings.GetString("cameraDevice", "");

        void FillDevices()
        {
            var list = Environment.GetEnvironmentVariable("CAMERA_INJECT") != null ? new System.Collections.Generic.List<string> { "Test camera" } : CameraDevices.List();
            camDevice.Items.Clear();
            foreach (var d in list) camDevice.Items.Add(d);
            var want = Settings.GetString("cameraDevice", "");
            camDevice.SelectedIndex = camDevice.Items.Count == 0 ? -1 : Math.Max(0, camDevice.Items.IndexOf(want));
            camDevice.Visible = camDevice.Items.Count > 1;   // 只有一个摄像头就不用选
        }

        // 打开摄像头只预览（小窗）
        void OpenCamera()
        {
            if (cam != null) return;
            if (camDevice.Items.Count == 0) FillDevices();
            if (camDevice.Items.Count == 0) { Say(L("没找到摄像头", "No camera found"), true); camera.Checked = false; return; }
            ShowBubble();
            StartCapture(null);
        }

        void ShowBubble()
        {
            if (bubble == null || bubble.IsDisposed) bubble = new CameraBubble();
            if (!bubble.Visible) bubble.Show();
        }

        void StartCapture(string file)
        {
            try
            {
                cam = CameraCapture.Start(Device, file);
                var b = bubble;
                cam.Frame += f => b?.Push(f);
            }
            catch (Exception e) { cam = null; Say(L("打开摄像头失败：", "Couldn't open the camera: ") + e.Message, true); }
        }

        public void CloseCamera()
        {
            try { cam?.Stop(); } catch { }
            cam = null;
            if (bubble != null && !bubble.IsDisposed) { bubble.Close(); bubble.Dispose(); }
            bubble = null;
        }

        // 开录：Studio 模式下摄像头重开、同时写 camera.mp4，小窗不录进画面；普通录制只要小窗在屏幕上
        void StartCameraRecording(StudioSession proj)
        {
            if (!camera.Checked) return;
            if (camDevice.Items.Count == 0) FillDevices();
            if (camDevice.Items.Count == 0) return;
            ShowBubble();
            bubble.ExcludeFromCapture(proj != null);
            if (proj != null)
            {
                try { cam?.Stop(); } catch { }
                cam = null;
                StartCapture(Path.Combine(proj.Dir, "camera.mp4"));
            }
            else if (cam == null) StartCapture(null);
        }

        // 开录失败：写了一半的 camera.mp4 不要了，摄像头开回只预览
        void AbortCameraRecording()
        {
            if (cam?.File != null) { try { cam.Stop(); } catch { } cam = null; if (camera.Checked) StartCapture(null); }
            bubble?.ExcludeFromCapture(false);
        }

        // 录完：Studio 模式下停掉写文件的那路，记下 camera.mp4 和原始画面差几秒、小窗在录制范围里的位置；再开回只预览
        void StopCameraRecording(StudioSession proj, Source src, double firstFrameAt)
        {
            if (proj == null || cam == null || cam.File == null) { bubble?.ExcludeFromCapture(false); return; }
            var c = cam; cam = null;
            c.Stop();
            if (File.Exists(c.File) && c.FirstFrameAt > 0 && bubble != null && src != null)
            {
                var area = CursorTracker.AreaOf(src)() ?? Screen.PrimaryScreen.Bounds;
                proj.Camera = bubble.TrackIn(area, c.FirstFrameAt - firstFrameAt);
            }
            bubble?.ExcludeFromCapture(false);
            if (camera.Checked && !IsDisposed) StartCapture(null);
        }
    }
}
