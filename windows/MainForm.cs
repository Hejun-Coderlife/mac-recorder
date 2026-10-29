using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed class MainForm : Form
    {
        readonly ListView list = new ListView();
        readonly CheckBox sysAudio = new CheckBox(), mic = new CheckBox(), cursor = new CheckBox();
        readonly Button recordBtn = new Button(), regionBtn = new Button(), refreshBtn = new Button(), folderBtn = new Button();
        readonly Label timeLabel = new Label(), status = new Label(), hint = new Label();
        readonly Timer timer = new Timer { Interval = 250 };

        List<Source> sources = new List<Source>();
        Source region;
        string selectedId;
        Session session;
        DateTime startedAt;
        bool busy;

        const int HotkeyId = 1;

        public MainForm()
        {
            Text = Name;
            Icon = App.Icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(600, 470);
            MinimumSize = new Size(560, 440);
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // 第一行：录什么 + 选区域 / 刷新列表
            var top = Row();
            var head = new Label { Text = L("录什么", "What to record"), AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) };
            Btn(regionBtn, L("选区域…", "Select Area…"), (s, e) => PickRegion());
            Btn(refreshBtn, L("刷新列表", "Refresh"), (s, e) => Refresh_());
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(head, 0, 0); top.Controls.Add(regionBtn, 1, 0); top.Controls.Add(refreshBtn, 2, 0);
            root.Controls.Add(top);

            // 列表：名字 + 说明
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.HeaderStyle = ColumnHeaderStyle.None;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("", 170);
            list.Columns.Add("", 360);
            list.SmallImageList = MakeIcons();
            list.SelectedIndexChanged += (s, e) => { if (list.SelectedItems.Count > 0) selectedId = (string)list.SelectedItems[0].Tag; UpdateUi(); };
            list.Resize += (s, e) => list.Columns[1].Width = Math.Max(100, list.ClientSize.Width - list.Columns[0].Width - 4);
            root.Controls.Add(list);

            // 开关
            var opts = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 4), WrapContents = true };
            Check(sysAudio, L("录电脑声音", "System audio"), "systemAudio", true);
            Check(mic, L("录麦克风", "Microphone"), "microphone", false);
            Check(cursor, L("显示鼠标", "Show cursor"), "showCursor", true);
            opts.Controls.AddRange(new Control[] { sysAudio, mic, cursor });
            root.Controls.Add(opts);

            // 录制按钮 + 计时 + 快捷键提示
            var rec = Row();
            recordBtn.Font = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold);
            recordBtn.AutoSize = true;
            recordBtn.MinimumSize = new Size(160, 44);
            recordBtn.FlatStyle = FlatStyle.Flat;
            recordBtn.FlatAppearance.BorderSize = 0;
            recordBtn.ForeColor = Color.White;
            recordBtn.Margin = new Padding(0, 6, 12, 6);
            recordBtn.Click += (s, e) => Toggle();
            timeLabel.AutoSize = true; timeLabel.Anchor = AnchorStyles.Left;
            timeLabel.Font = new Font("Consolas", Font.Size + 6);
            timeLabel.ForeColor = Color.FromArgb(220, 40, 40);
            hint.Text = L("Ctrl+Alt+R 开始 / 停止", "Ctrl+Alt+R start / stop");
            hint.AutoSize = true; hint.Anchor = AnchorStyles.Right; hint.ForeColor = SystemColors.GrayText;
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.Controls.Add(recordBtn, 0, 0); rec.Controls.Add(timeLabel, 1, 0); rec.Controls.Add(hint, 2, 0);
            root.Controls.Add(rec);

            // 状态 + 打开文件夹
            var bottom = Row();
            status.AutoSize = true; status.Anchor = AnchorStyles.Left; status.MaximumSize = new Size(440, 0);
            Btn(folderBtn, L("打开文件夹", "Open Folder"), (s, e) => Open(Folder));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(folderBtn, 1, 0);
            root.Controls.Add(bottom);

            timer.Tick += (s, e) => { if (session != null) timeLabel.Text = "● " + (DateTime.Now - startedAt).ToString(@"mm\:ss"); };
            Say("");
            Refresh_();
        }

        static TableLayoutPanel Row() => new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };

        static void Btn(Button b, string text, EventHandler click)
        {
            b.Text = text; b.AutoSize = true; b.Padding = new Padding(8, 2, 8, 2); b.Anchor = AnchorStyles.Right; b.Click += click;
        }

        void Check(CheckBox c, string text, string key, bool def)
        {
            c.Text = text; c.AutoSize = true; c.Margin = new Padding(0, 0, 22, 0);
            c.Checked = Settings.Get(key, def);
            c.CheckedChanged += (s, e) => Settings.Set(key, c.Checked);
        }

        // 列表图标：虚线框 = 区域，显示器 = 屏幕，窗口 = 窗口
        ImageList MakeIcons()
        {
            int sz = (int)Math.Round(20 * DeviceDpi / 96.0);
            var il = new ImageList { ImageSize = new Size(sz, sz), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var kind in new[] { "r", "d", "w" })
            {
                var bmp = new Bitmap(sz, sz);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(Color.FromArgb(90, 90, 90), Math.Max(1.2f, sz / 14f)))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float u = sz / 20f;
                    if (kind == "r") { pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash; g.DrawRectangle(pen, 2 * u, 4 * u, 16 * u, 12 * u); }
                    else if (kind == "d") { g.DrawRectangle(pen, 2 * u, 3 * u, 16 * u, 11 * u); g.DrawLine(pen, 10 * u, 14 * u, 10 * u, 17 * u); g.DrawLine(pen, 6 * u, 17 * u, 14 * u, 17 * u); }
                    else { g.DrawRectangle(pen, 2 * u, 3 * u, 16 * u, 14 * u); g.DrawLine(pen, 2 * u, 7 * u, 18 * u, 7 * u); }
                }
                il.Images.Add(kind, bmp);
            }
            return il;
        }

        void Refresh_()
        {
            try { sources = Sources.List(region); }
            catch (Exception e) { Say(L("读取屏幕和窗口列表失败：", "Couldn't list screens and windows: ") + e.Message, true); return; }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var s in sources)
            {
                var it = new ListViewItem(new[] { s.Title, s.Detail }) { Tag = s.Id, ImageKey = s.Kind == SourceKind.Region ? "r" : s.Kind == SourceKind.Display ? "d" : "w" };
                list.Items.Add(it);
            }
            list.EndUpdate();
            if (selectedId == null || !sources.Any(s => s.Id == selectedId)) selectedId = sources.FirstOrDefault(s => s.Kind == SourceKind.Display)?.Id;
            foreach (ListViewItem it in list.Items) if ((string)it.Tag == selectedId) { it.Selected = true; it.EnsureVisible(); }
            UpdateUi();
        }

        void PickRegion()
        {
            var initial = region;
            RegionPicker.Pick(initial, (screen, area, recordNow) =>
            {
                region = Sources.Region(screen, area);
                selectedId = "r";
                Refresh_();
                if (recordNow) Toggle();
            });
        }

        void UpdateUi()
        {
            bool rec = session != null;
            list.Enabled = !rec && !busy;
            regionBtn.Enabled = refreshBtn.Enabled = !rec && !busy;
            sysAudio.Enabled = mic.Enabled = cursor.Enabled = !rec && !busy;
            recordBtn.Enabled = !busy && (rec || selectedId != null);
            recordBtn.Text = rec ? "■  " + L("停止", "Stop") : "●  " + L("开始录制", "Record");
            recordBtn.BackColor = rec ? Color.FromArgb(220, 50, 50) : (recordBtn.Enabled ? Color.FromArgb(0, 103, 192) : Color.FromArgb(160, 170, 180));
            timeLabel.Visible = rec;
        }

        void Say(string msg, bool error = false)
        {
            status.Text = string.IsNullOrEmpty(msg) ? L("保存到：视频 \\ ", "Saved to: Videos \\ ") + Path.GetFileName(Folder) : msg;
            status.ForeColor = error ? Color.FromArgb(200, 30, 30) : SystemColors.GrayText;
        }

        void Toggle()
        {
            if (busy) return;
            if (session != null) { Stop(); return; }
            var src = sources.FirstOrDefault(s => s.Id == selectedId);
            if (src == null) { Say(L("先选一个要录的屏幕或窗口", "Pick a screen or window to record first"), true); return; }
            if (src.Kind == SourceKind.Window && !IsWindow(src.Hwnd)) { Say(L("这个窗口已经关掉了，换一个", "That window is gone, pick another one"), true); Refresh_(); return; }
            if (src.Kind != SourceKind.Window && !Screen.AllScreens.Any(s => s.DeviceName == src.DeviceName))
            {
                Say(src.Kind == SourceKind.Region ? L("那块屏幕不见了，重新选一下区域", "That screen is gone, select the area again") : L("那块屏幕不见了，刷新一下列表", "That screen is gone, refresh the list"), true);
                return;
            }
            Session s;
            try
            {
                s = new Session(src, sysAudio.Checked, mic.Checked, cursor.Checked, NewFilePath());
                s.Completed += path => BeginInvoke((Action)(() => Finished(path, null)));
                s.Failed += err => BeginInvoke((Action)(() => Finished(null, err)));
                s.Start();
            }
            catch (Exception e) { Say(L("开始录制失败：", "Couldn't start recording: ") + e.Message, true); return; }
            session = s;
            startedAt = DateTime.Now;
            timeLabel.Text = "● 00:00";
            timer.Start();
            Say(L("正在录制…", "Recording…"));
            System.Media.SystemSounds.Asterisk.Play();
            UpdateUi();
        }

        void Stop()
        {
            busy = true;
            timer.Stop();
            Say(L("正在保存…", "Saving…"));
            UpdateUi();
            try { session.Stop(); }
            catch (Exception e) { Finished(null, e.Message); }
        }

        void Finished(string path, string error)
        {
            if (session == null) return;
            bool wasStopping = busy;
            session = null; busy = false; timer.Stop();
            UpdateUi();
            if (error != null)
            {
                Say((wasStopping ? L("保存失败：", "Save failed: ") : L("录制中断：", "Recording interrupted: ")) + error, true);
                return;
            }
            System.Media.SystemSounds.Exclamation.Play();
            Say(L("已保存：", "Saved: ") + Path.GetFileName(path));
            Open(path);   // 录完直接用默认播放器打开
        }

        static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 自己的窗口不出现在录出来的画面里（Windows 10 2004 及以后）
            SetWindowDisplayAffinity(Handle, 0x11);
            if (!RegisterHotKey(Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, (uint)Keys.R))   // Alt+Ctrl，不重复触发
                Say(L("Ctrl+Alt+R 被别的程序占用了，只能点按钮开始 / 停止", "Ctrl+Alt+R is taken by another app; use the button to start / stop"), true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (session != null)
            {
                // 正在录：先停下保存好再退出
                try { session.Stop(); } catch { }
                System.Threading.Thread.Sleep(1500);
            }
            UnregisterHotKey(Handle, HotkeyId);
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) Toggle();   // WM_HOTKEY
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    }
}
