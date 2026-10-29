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
        readonly CheckBox sysAudio = new CheckBox(), mic = new CheckBox(), cursor = new CheckBox(), studio = new CheckBox();
        readonly Button recordBtn = new Button(), shotBtn = new Button(), regionBtn = new Button(), refreshBtn = new Button(), folderBtn = new Button();
        readonly Label timeLabel = new Label(), status = new Label(), hint = new Label();
        readonly Timer timer = new Timer { Interval = 250 };
        readonly LinkLabel updateLink = new LinkLabel(), checkLink = new LinkLabel();
        Updater.Release pending;    // 查到的新版本，还没装
        bool updating;              // 正在下载新版本

        List<Source> sources = new List<Source>();
        Source region;
        string selectedId;
        Session session;
        StudioProject project;      // 开着 Studio 效果录的这一次
        DateTime startedAt;
        bool busy;

        const int HotkeyId = 1, ShotHotkeyId = 2;
        string lastFile;            // 刚存好的视频，「打开所在文件夹」时选中它

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

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            // Studio 效果：画面里不录鼠标、另记轨迹，录完自动放大点击处、平滑鼠标、加白色背景圆角阴影（见 StudioCore.cs）
            Check(studio, L("Studio 效果", "Studio effects"), "studio", false);
            studio.CheckedChanged += (s, e) => UpdateUi();
            opts.Controls.AddRange(new Control[] { sysAudio, mic, cursor, studio });
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
            // 截图：和录制并排，录制中也能截
            shotBtn.Text = L("截图", "Screenshot");
            shotBtn.AutoSize = true;
            shotBtn.MinimumSize = new Size(96, 44);
            shotBtn.Margin = new Padding(0, 6, 12, 6);
            shotBtn.Click += (s, e) => Screenshot();
            timeLabel.AutoSize = true; timeLabel.Anchor = AnchorStyles.Left;
            timeLabel.Font = new Font("Consolas", Font.Size + 6);
            timeLabel.ForeColor = Color.FromArgb(220, 40, 40);
            hint.Text = L("Ctrl+Alt+R 录屏 · Ctrl+Alt+S 截图", "Ctrl+Alt+R record · Ctrl+Alt+S screenshot");
            hint.AutoSize = true; hint.Anchor = AnchorStyles.Right; hint.ForeColor = SystemColors.GrayText;
            rec.ColumnCount = 4;
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.Controls.Add(recordBtn, 0, 0); rec.Controls.Add(shotBtn, 1, 0); rec.Controls.Add(timeLabel, 2, 0); rec.Controls.Add(hint, 3, 0);
            root.Controls.Add(rec);

            // 状态 + 打开文件夹
            var bottom = Row();
            status.AutoSize = true; status.Anchor = AnchorStyles.Left; status.MaximumSize = new Size(440, 0);
            Btn(folderBtn, L("打开所在文件夹", "Show in Folder"), (s, e) => ShowInFolder());
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(folderBtn, 1, 0);
            root.Controls.Add(bottom);

            // 更新：有新版本时左边出「有新版本 vX.Y · 更新」，右边常驻「检查更新」
            var upd = Row();
            updateLink.AutoSize = true; updateLink.Anchor = AnchorStyles.Left; updateLink.Visible = false;
            updateLink.LinkClicked += (s, e) => InstallUpdate();
            checkLink.AutoSize = true; checkLink.Anchor = AnchorStyles.Right; checkLink.Text = L("检查更新", "Check for Updates");
            checkLink.LinkClicked += (s, e) => CheckUpdates(true);
            upd.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            upd.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            upd.Controls.Add(updateLink, 0, 0); upd.Controls.Add(checkLink, 1, 0);
            upd.Margin = new Padding(0, 4, 0, 0);
            root.Controls.Add(upd);

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
            sysAudio.Enabled = mic.Enabled = studio.Enabled = !rec && !busy;
            cursor.Enabled = !rec && !busy && !studio.Checked;   // Studio 模式鼠标后期再画
            recordBtn.Enabled = !busy && !updating && (rec || selectedId != null);
            recordBtn.Text = rec ? "■  " + L("停止", "Stop") : "●  " + L("开始录制", "Record");
            recordBtn.BackColor = rec ? Color.FromArgb(220, 50, 50) : (recordBtn.Enabled ? Color.FromArgb(0, 103, 192) : Color.FromArgb(160, 170, 180));
            timeLabel.Visible = rec;
            checkLink.Enabled = !updating;
            // 录制中 / 生成 Studio 中不提示更新，空下来再显示
            updateLink.Visible = !rec && !busy && updateLink.Text != "";
        }

        // ---- 自动更新（见 Updater.cs）----
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 打开几秒后在后台查一次；离上次查不到 6 小时就不查
            var t = new Timer { Interval = 3000 };
            t.Tick += (s, a) => { t.Stop(); t.Dispose(); if (Updater.Due) CheckUpdates(false); };
            t.Start();
        }

        // 一行字，withLink = 最后的「更新」两个字可以点
        void ShowUpdate(string text, bool withLink)
        {
            updateLink.Text = text;
            var word = L("更新", "Update");
            updateLink.LinkArea = withLink && text.EndsWith(word) ? new LinkArea(text.Length - word.Length, word.Length) : new LinkArea(0, 0);
            updateLink.Visible = session == null && !busy;
        }

        async void CheckUpdates(bool manual)
        {
            if (updating) return;
            if (manual) ShowUpdate(L("正在检查更新…", "Checking for updates…"), false);
            try
            {
                var r = await Updater.CheckAsync();
                if (r != null) { pending = r; ShowUpdate(L($"有新版本 {r.Tag} · 更新", $"New version {r.Tag} available · Update"), true); }
                else if (manual) ShowUpdate(L($"已是最新版本（v{Updater.Show(Updater.Current)}）", $"You're up to date (v{Updater.Show(Updater.Current)})"), false);
            }
            catch (Exception e)
            {
                if (manual) ShowUpdate(L("检查更新失败：", "Couldn't check for updates: ") + e.Message, false);
            }
        }

        async void InstallUpdate()
        {
            var r = pending;
            if (r == null || updating) return;
            if (session != null || busy) { Say(L("录完再更新", "Finish recording before updating"), true); return; }
            if (!Updater.CanWriteAppDir())
            {
                // 装在 Program Files 之类没写权限的地方：只能手动下载
                ShowUpdate(L("程序所在文件夹不能写入，请手动下载新版（已打开下载页）", "Can't write to the app folder; please download the new version manually (page opened)"), false);
                Open(Updater.ReleasesPage);
                return;
            }
            updating = true; UpdateUi();
            ShowUpdate(L("正在下载新版本… 0%", "Downloading update… 0%"), false);
            try
            {
                var dir = await Updater.DownloadAsync(r, p => BeginInvoke((Action)(() => ShowUpdate(L($"正在下载新版本… {p}%", $"Downloading update… {p}%"), false))));
                ShowUpdate(L("正在安装新版本，马上重新打开…", "Installing, the app will reopen…"), false);
                Updater.LaunchInstaller(dir, true);
                Close();
            }
            catch (Exception e)
            {
                updating = false; UpdateUi();
                ShowUpdate(L("更新失败：", "Update failed: ") + e.Message, false);
            }
        }

        void Say(string msg, bool error = false)
        {
            status.Text = string.IsNullOrEmpty(msg) ? L("视频和截图都存到桌面", "Recordings and screenshots go to the Desktop") : msg;
            status.ForeColor = error ? Color.FromArgb(200, 30, 30) : SystemColors.GrayText;
        }

        void Toggle()
        {
            if (busy || updating) return;
            if (session != null) { Stop(); return; }
            var src = sources.FirstOrDefault(s => s.Id == selectedId);
            if (src == null) { Say(L("先选一个要录的屏幕或窗口", "Pick a screen or window to record first"), true); return; }
            if (src.Kind == SourceKind.Window && !IsWindow(src.Hwnd)) { Say(L("这个窗口已经关掉了，换一个", "That window is gone, pick another one"), true); Refresh_(); return; }
            if (src.Kind != SourceKind.Window && !Screen.AllScreens.Any(s => s.DeviceName == src.DeviceName))
            {
                Say(src.Kind == SourceKind.Region ? L("那块屏幕不见了，重新选一下区域", "That screen is gone, select the area again") : L("那块屏幕不见了，刷新一下列表", "That screen is gone, refresh the list"), true);
                return;
            }
            bool studioOn = studio.Checked;
            if (studioOn && !StudioProject.Available) { Say(L("找不到 ffmpeg.exe，Studio 效果用不了（应该在「运行库」文件夹里，整个文件夹要和 录屏.exe 放在一起）", "ffmpeg.exe is missing from the 运行库 folder next to the app, Studio effects can't run"), true); return; }
            string name = NewName(), outPath = Unique(Path.Combine(Desktop, name + ".mp4"));
            // Studio 模式：原始画面写进素材文件夹，成片录完再生成到 outPath
            StudioProject proj = studioOn ? StudioProject.Create(name, outPath, src) : null;
            Session s;
            try
            {
                s = new Session(src, sysAudio.Checked, mic.Checked, cursor.Checked && !studioOn, proj?.Raw ?? outPath);
                s.Completed += path => BeginInvoke((Action)(() => Finished(path, null)));
                s.Failed += err => BeginInvoke((Action)(() => Finished(null, err)));
                proj?.Tracker.Start();   // 比画面早一点开始记，第一帧前后都有鼠标位置
                s.Start();
            }
            catch (Exception e)
            {
                proj?.Tracker.Stop();
                if (proj != null) try { Directory.Delete(proj.Dir, true); } catch { }
                Say(L("开始录制失败：", "Couldn't start recording: ") + e.Message, true);
                return;
            }
            session = s; project = proj;
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
            var s = session; var proj = project;
            session = null; project = null; busy = false; timer.Stop();
            UpdateUi();
            if (error != null)
            {
                proj?.Tracker.Stop();
                Say((wasStopping ? L("保存失败：", "Save failed: ") : L("录制中断：", "Recording interrupted: ")) + error, true);
                return;
            }
            if (proj != null) { RenderStudio(proj, s.FirstFrameAt); return; }
            Saved(path);
        }

        // 存好了：右下角浮出缩略图（拖进别的软件 / 单击播放），不再自动打开播放器
        void Saved(string path)
        {
            System.Media.SystemSounds.Exclamation.Play();
            lastFile = path;
            Say(L("已存到桌面：", "Saved to Desktop: ") + Path.GetFileName(path));
            System.Threading.Tasks.Task.Run(() => Thumb.VideoFrame(path))
                .ContinueWith(t => BeginInvoke((Action)(() => Thumb.ShowVideo(path, t.Result))));
        }

        void ShowInFolder()
        {
            if (lastFile != null && File.Exists(lastFile)) { try { Process.Start("explorer.exe", $"/select,\"{lastFile}\""); } catch { } }
            else Open(Desktop);
        }

        // 截图：定格屏幕 → 拖框 → 剪贴板 + 右下角缩略图
        void Screenshot()
        {
            if (Shots.Active) return;
            Shots.Start(bmp => { using (bmp) Shots.Deliver(bmp); });
        }

        // 存轨迹，按轨迹生成 Studio 版（后台线程；进度显示在状态栏）
        void RenderStudio(StudioProject proj, double firstFrameAt)
        {
            busy = true; UpdateUi();
            Say(L("正在生成 Studio 版…", "Rendering Studio version…"));
            System.Threading.Tasks.Task.Run(() =>
            {
                string err = null;
                try
                {
                    proj.Finish(firstFrameAt, p => BeginInvoke((Action)(() => Say(L($"正在生成 Studio 版… {(int)(p * 100)}%", $"Rendering Studio version… {(int)(p * 100)}%")))));
                }
                catch (Exception e) { err = e.Message; }
                BeginInvoke((Action)(() =>
                {
                    busy = false; UpdateUi();
                    if (err != null)
                    {
                        Say(L("生成 Studio 版失败（原始素材在「Studio 素材」文件夹）：", "Studio render failed (raw footage is in the “Studio 素材” folder): ") + err, true);
                        return;
                    }
                    Saved(proj.Out);
                }));
            });
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
            if (!RegisterHotKey(Handle, ShotHotkeyId, 0x0001 | 0x0002 | 0x4000, (uint)Keys.S))
                Say(L("Ctrl+Alt+S 被别的程序占用了，截图只能点「截图」按钮", "Ctrl+Alt+S is taken by another app; use the Screenshot button"), true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && session == null && e.CloseReason == CloseReason.UserClosing)
            {
                // 正在生成 Studio 版：关掉会留下半截文件，先别关
                Say(L("正在生成 Studio 版，等它做完再关", "Still rendering the Studio version, close it when it's done"), true);
                e.Cancel = true;
                return;
            }
            if (session != null)
            {
                // 正在录：先停下保存好再退出
                try { session.Stop(); } catch { }
                System.Threading.Thread.Sleep(1500);
            }
            UnregisterHotKey(Handle, HotkeyId);
            UnregisterHotKey(Handle, ShotHotkeyId);
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) Toggle();   // WM_HOTKEY
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == ShotHotkeyId) Screenshot();
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    }
}
