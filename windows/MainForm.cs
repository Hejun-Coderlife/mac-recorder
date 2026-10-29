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
        readonly CheckBox sysAudio = new CheckBox(), mic = new CheckBox(), cursor = new CheckBox(), studio = new CheckBox(), hideIcons = new CheckBox();
        readonly ComboBox countdown = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
        bool counting;              // 正在开录倒计时
        readonly Button recordBtn = new Button(), shotBtn = new Button(), regionBtn = new Button(), refreshBtn = new Button(), folderBtn = new Button(), footageBtn = new Button();
        readonly Label timeLabel = new Label(), status = new Label();
        readonly Button recKeyBtn = new Button(), shotKeyBtn = new Button();   // 显示当前快捷键，点一下就改
        readonly ToolTip tips = new ToolTip();
        readonly Timer timer = new Timer { Interval = 250 };
        readonly LinkLabel updateLink = new LinkLabel(), checkLink = new LinkLabel();
        bool updating;              // 正在查 / 下载新版本

        List<Source> sources = new List<Source>();
        Source region;
        string selectedId;
        Session session;
        StudioSession project;      // 开着 Studio 效果录的这一次
        DateTime startedAt;
        bool busy;

        const int HotkeyId = 1, ShotHotkeyId = 2;
        public Hotkey RecordKey = Hotkey.Load(Hotkey.RecordSetting, Hotkey.DefaultRecord), ShotKey = Hotkey.Load(Hotkey.ShotSetting, Hotkey.DefaultShot);
        int capturing;              // 正在等用户按新快捷键：HotkeyId / ShotHotkeyId；0 = 没在改
        string lastFile;            // 刚存好的视频，「打开所在文件夹」时选中它

        public MainForm()
        {
            Text = Name;
            Icon = App.Icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(640, 510);
            MinimumSize = new Size(600, 470);
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
            // 开录倒计时、录制时隐藏桌面图标
            var cdLabel = new Label { Text = L("开录倒计时", "Countdown"), AutoSize = true, Margin = new Padding(0, 4, 4, 0) };
            countdown.Items.AddRange(new object[] { L("关", "Off"), L("3 秒", "3 s"), L("5 秒", "5 s") });
            countdown.SelectedIndex = Math.Max(0, Math.Min(2, new[] { "0", "3", "5" }.ToList().IndexOf(Settings.GetString("countdown", "0"))));
            countdown.SelectedIndexChanged += (s, e) => Settings.SetString("countdown", new[] { "0", "3", "5" }[countdown.SelectedIndex]);
            countdown.Margin = new Padding(0, 0, 22, 0);
            Check(hideIcons, L("录制时隐藏桌面图标", "Hide desktop icons while recording"), "hideDesktopIcons", false);
            tips.SetToolTip(hideIcons, L("录的时候先把桌面上的图标藏起来，录完自动恢复", "Hides the icons on your desktop while recording, and brings them back afterwards"));
            var opts2 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4), WrapContents = true };
            opts2.Controls.AddRange(new Control[] { cdLabel, countdown, hideIcons });
            root.RowCount = 7;
            root.RowStyles.Insert(3, new RowStyle(SizeType.AutoSize));
            root.Controls.Add(opts);
            root.Controls.Add(opts2);

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
            // 快捷键：「录屏 [Ctrl+Alt+R]  截图 [Ctrl+Alt+X]」，点按钮后按新的组合键
            var keys = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false, Margin = new Padding(0) };
            foreach (var (label, btn, id) in new[] { (L("录屏", "Record"), recKeyBtn, HotkeyId), (L("截图", "Screenshot"), shotKeyBtn, ShotHotkeyId) })
            {
                keys.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 2, 0) });
                btn.AutoSize = true; btn.Padding = new Padding(4, 0, 4, 0); btn.Margin = new Padding(0, 0, 0, 0);
                btn.FlatStyle = FlatStyle.System;
                btn.Click += (s, e) => BeginCapture(id);
                tips.SetToolTip(btn, L("点一下，再按新的快捷键（Esc 取消）", "Click, then press a new shortcut (Esc to cancel)"));
                keys.Controls.Add(btn);
            }
            ShowKeys();
            rec.ColumnCount = 4;
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rec.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rec.Controls.Add(recordBtn, 0, 0); rec.Controls.Add(shotBtn, 1, 0); rec.Controls.Add(timeLabel, 2, 0); rec.Controls.Add(keys, 3, 0);
            root.Controls.Add(rec);

            // 状态 + 打开文件夹
            var bottom = Row();
            status.AutoSize = true; status.Anchor = AnchorStyles.Left; status.MaximumSize = new Size(440, 0);
            Btn(folderBtn, L("打开所在文件夹", "Show in Folder"), (s, e) => ShowInFolder());
            // 以前录的 Studio 素材：选一个文件夹，打开剪辑窗口接着改
            Btn(footageBtn, L("打开 Studio 素材…", "Open Studio Footage…"), (s, e) => OpenFootage());
            tips.SetToolTip(footageBtn, L("选「视频\\录屏\\Studio 素材」里的一个文件夹，打开剪辑窗口接着改", "Pick a folder in Videos\\录屏\\Studio 素材 to edit it again"));
            footageBtn.Margin = new Padding(0, 3, 8, 3);
            bottom.ColumnCount = 3;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(footageBtn, 1, 0); bottom.Controls.Add(folderBtn, 2, 0);
            root.Controls.Add(bottom);

            // 更新：有新版本时左边出「有新版本 vX.Y · 更新」，右边常驻「检查更新」
            var upd = Row();
            updateLink.AutoSize = true; updateLink.Anchor = AnchorStyles.Left; updateLink.Visible = false;
            updateLink.LinkClicked += (s, e) => onLink?.Invoke();
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
            sysAudio.Enabled = mic.Enabled = studio.Enabled = hideIcons.Enabled = countdown.Enabled = !rec && !busy && !counting;
            cursor.Enabled = !rec && !busy && !studio.Checked;   // Studio 模式鼠标后期再画
            recordBtn.Enabled = !busy && (rec || selectedId != null);   // 后台在下新版也照样能录
            recordBtn.Text = rec ? "■  " + L("停止", "Stop") : counting ? L("取消倒计时", "Cancel") : "●  " + L("开始录制", "Record");
            recordBtn.BackColor = rec ? Color.FromArgb(220, 50, 50) : (recordBtn.Enabled ? Color.FromArgb(0, 103, 192) : Color.FromArgb(160, 170, 180));
            timeLabel.Visible = rec;
            checkLink.Enabled = !updating;
            // 录制中 / 生成 Studio 中不提示更新，空下来再显示
            updateLink.Visible = !rec && !busy && updateLink.Text != "";
        }

        // ---- 自动更新（见 Updater.cs）：后台查、后台下，退出时换上 ----
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var done = Updater.TakeUpdatedTo();
            if (done != null) ShowUpdate(L($"已更新到 v{done}", $"Updated to v{done}"), null);
            else if (Updater.StagedVersion is Version v) ShowReady(v);
            // 打开 3 秒后看一眼，之后每 30 分钟看一眼；离上次查不到 6 小时、正在录 / 生成时都不查
            var first = new Timer { Interval = 3000 };
            first.Tick += (s, a) => { first.Stop(); first.Dispose(); AutoCheck(); };
            first.Start();
            var every = new Timer { Interval = 30 * 60 * 1000 };
            every.Tick += (s, a) => AutoCheck();
            every.Start();
        }

        void AutoCheck()
        {
            if (Updater.Due && session == null && !busy) CheckUpdates(false);
        }

        // 一行字；link = 末尾可以点的那几个字（null = 不能点），点了做 onLink
        Action onLink;
        void ShowUpdate(string text, string link, Action action = null)
        {
            updateLink.Text = text;
            updateLink.LinkArea = link != null && text.EndsWith(link) ? new LinkArea(text.Length - link.Length, link.Length) : new LinkArea(0, 0);
            onLink = action;
            updateLink.Visible = session == null && !busy;
        }

        void ShowReady(Version v)
        {
            var word = L("现在重启", "Restart Now");
            ShowUpdate(L($"新版本 v{Updater.Show(v)} 已下载，退出后自动换上 · ", $"Version {Updater.Show(v)} is downloaded and will be installed when you quit · ") + word, word, RestartNow);
        }

        // manual = 点了「检查更新」：显示进度和结果；后台的出错就算了，下次再试
        async void CheckUpdates(bool manual)
        {
            if (updating) return;
            updating = true; UpdateUi();
            if (manual) ShowUpdate(L("正在检查更新…", "Checking for updates…"), null);
            try
            {
                var r = await Updater.CheckAsync();
                var staged = Updater.StagedVersion;
                if (r == null) { if (manual) ShowUpdate(L($"已是最新版本（v{Updater.Show(Updater.Current)}）", $"You're up to date (v{Updater.Show(Updater.Current)})"), null); }
                else if (staged != null && staged >= r.Version) ShowReady(staged);
                else if (!Updater.CanWriteAppDir())
                {
                    // 装在 Program Files 之类没写权限的地方：只能手动下载
                    var word = L("去下载", "Download");
                    ShowUpdate(L($"有新版本 {r.Tag}，程序所在文件夹不能写入，请手动下载 · ", $"Version {r.Tag} is available, but the app folder isn't writable · ") + word, word, () => Open(Updater.ReleasesPage));
                }
                else
                {
                    var v = await Updater.StageAsync(r, p => { if (manual) BeginInvoke((Action)(() => ShowUpdate(L($"正在下载新版本… {p}%", $"Downloading update… {p}%"), null))); });
                    ShowReady(v);
                }
            }
            catch (Exception e)
            {
                if (manual) ShowUpdate(L("更新失败：", "Update failed: ") + e.Message, null);
            }
            finally { updating = false; UpdateUi(); }
        }

        void RestartNow()
        {
            if (session != null || busy) { Say(L("录完再重启", "Finish recording before restarting"), true); return; }
            Updater.RestartAfterExit = true;
            Close();
        }

        void Say(string msg, bool error = false)
        {
            status.Text = string.IsNullOrEmpty(msg) ? L("视频和截图都存到桌面", "Recordings and screenshots go to the Desktop") : msg;
            status.ForeColor = error ? Color.FromArgb(200, 30, 30) : SystemColors.GrayText;
        }

        void Toggle()
        {
            if (busy) return;
            if (session != null) { Stop(); return; }
            if (counting) { CountdownForm.Current?.Cancel(); return; }   // 倒计时中再按一次 = 取消
            var src = sources.FirstOrDefault(s => s.Id == selectedId);
            if (src == null) { Say(L("先选一个要录的屏幕或窗口", "Pick a screen or window to record first"), true); return; }
            if (src.Kind == SourceKind.Window && !IsWindow(src.Hwnd)) { Say(L("这个窗口已经关掉了，换一个", "That window is gone, pick another one"), true); Refresh_(); return; }
            if (src.Kind != SourceKind.Window && !Screen.AllScreens.Any(s => s.DeviceName == src.DeviceName))
            {
                Say(src.Kind == SourceKind.Region ? L("那块屏幕不见了，重新选一下区域", "That screen is gone, select the area again") : L("那块屏幕不见了，刷新一下列表", "That screen is gone, refresh the list"), true);
                return;
            }
            bool studioOn = studio.Checked;
            int secs = new[] { 0, 3, 5 }[Math.Max(0, countdown.SelectedIndex)];
            if (secs > 0)
            {
                counting = true; UpdateUi();
                Say(L($"{secs} 秒后开录（Esc 或再按一次取消）", $"Recording starts in {secs} s (Esc or press again to cancel)"));
                var scr = src.Kind == SourceKind.Window ? Screen.FromHandle(src.Hwnd) : Screen.AllScreens.FirstOrDefault(x => x.DeviceName == src.DeviceName) ?? Screen.PrimaryScreen;
                CountdownForm.Run(secs, scr, () => { counting = false; UpdateUi(); Begin(src); }, () => { counting = false; UpdateUi(); Say(L("已取消", "Cancelled")); });
                return;
            }
            Begin(src);
        }

        // 真正开录（倒计时完）
        void Begin(Source src)
        {
            bool studioOn = studio.Checked;
            if (studioOn && !StudioSession.Available) { Say(L("找不到 ffmpeg，Studio 效果用不了（请重新下载 录屏.exe）", "ffmpeg is missing, Studio effects can't run (please download the app again)"), true); return; }
            string name = NewName(), outPath = Unique(Path.Combine(Desktop, name + ".mp4"));
            // Studio 模式：原始画面写进素材文件夹，成片录完再生成到 outPath
            StudioSession proj = studioOn ? StudioSession.Create(name, outPath, src) : null;
            Session s;
            try
            {
                s = new Session(src, sysAudio.Checked, mic.Checked, cursor.Checked && !studioOn, proj?.Raw ?? outPath);
                s.Completed += path => BeginInvoke((Action)(() => Finished(path, null)));
                s.Failed += err => BeginInvoke((Action)(() => Finished(null, err)));
                if (hideIcons.Checked && src.Kind != SourceKind.Window) DesktopIcons.Hide();   // 只录某个窗口时桌面本来就不在画面里
                proj?.Tracker.Start();   // 比画面早一点开始记，第一帧前后都有鼠标位置
                s.Start();
            }
            catch (Exception e)
            {
                proj?.Tracker.Stop();
                DesktopIcons.Restore();
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
            DesktopIcons.Restore();
            UpdateUi();
            if (error != null)
            {
                proj?.Tracker.Stop();
                Say((wasStopping ? L("保存失败：", "Save failed: ") : L("录制中断：", "Recording interrupted: ")) + error, true);
                return;
            }
            if (proj != null) { OpenStudio(proj, s.FirstFrameAt); return; }
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

        // Studio 录完：存轨迹，打开剪辑窗口（推近、修剪、样式都在那里调，点「导出」出成片到桌面）
        void OpenStudio(StudioSession proj, double firstFrameAt)
        {
            busy = true; UpdateUi();
            Say(L("正在打开剪辑窗口…", "Opening the editor…"));
            System.Threading.Tasks.Task.Run(() =>
            {
                string err = null;
                try { proj.SaveTrack(firstFrameAt); }
                catch (Exception e) { err = e.Message; }
                BeginInvoke((Action)(() =>
                {
                    busy = false; UpdateUi();
                    if (err != null) { Say(L("保存 Studio 素材失败：", "Couldn't save the Studio footage: ") + err, true); return; }
                    ShowEditor(proj.Dir, proj.Out);
                }));
            });
        }

        void ShowEditor(string dir, string outPath = null)
        {
            try
            {
                var ed = StudioEditor.Open(dir, outPath);
                ed.Exported -= OnExported; ed.Exported += OnExported;
                Say(L("剪辑窗口已打开：调好后点右上角「导出」", "Editor opened: click Export (top right) when you're done"));
            }
            catch (Exception e) { Say(L("打开剪辑窗口失败：", "Couldn't open the editor: ") + e.Message, true); }
        }

        void OnExported(string path) { if (!IsDisposed) BeginInvoke((Action)(() => Saved(path))); }

        // 「打开 Studio 素材…」：选一个素材文件夹（里面要有 raw.mp4 和 track.json）
        void OpenFootage()
        {
            using (var dlg = new FolderBrowserDialog { Description = L("选一个 Studio 素材文件夹", "Choose a Studio footage folder"), SelectedPath = StudioSession.Folder, ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var dir = dlg.SelectedPath;
                if (!File.Exists(Path.Combine(dir, "raw.mp4")) || !File.Exists(Path.Combine(dir, "track.json")))
                {
                    Say(L("这个文件夹不是 Studio 素材（里面要有 raw.mp4 和 track.json）", "That folder isn't Studio footage (it needs raw.mp4 and track.json)"), true);
                    return;
                }
                if (!StudioSession.Available) { Say(L("找不到 ffmpeg，Studio 剪辑用不了（请重新下载 录屏.exe）", "ffmpeg is missing, the Studio editor can't run"), true); return; }
                ShowEditor(dir);
            }
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
            // 快捷键被别的程序占了（比如 QQ）：窗口底部写一行，右下角再弹个通知，告诉用户怎么改
            var taken = new System.Collections.Generic.List<string>();
            if (!Register(HotkeyId, RecordKey)) taken.Add(L($"录屏快捷键 {RecordKey}", $"Record shortcut {RecordKey}"));
            if (!Register(ShotHotkeyId, ShotKey)) taken.Add(L($"截图快捷键 {ShotKey}", $"Screenshot shortcut {ShotKey}"));
            HotkeyProblems = taken;
            if (taken.Count > 0)
            {
                var what = string.Join(L("、", ", "), taken);
                Say(L($"{what} 被别的程序占用了，点窗口右边的快捷键按钮换一个", $"{what} is taken by another app; click the shortcut button on the right to change it"), true);
                Notice.Show(this, L("快捷键被占用", "Shortcut unavailable"),
                    L($"{what} 被别的程序（比如 QQ）占用了。打开录屏窗口，点右边的快捷键按钮，再按一个新的组合键就行。",
                      $"{what} is used by another app. Open Screen Recorder, click the shortcut button and press a new combination."));
            }
        }

        public System.Collections.Generic.List<string> HotkeyProblems = new System.Collections.Generic.List<string>();
        public string StatusText => status.Text;
        public Button ShotKeyButton => shotKeyBtn;

        bool Register(int id, Hotkey k) => RegisterHotKey(Handle, id, k.Mods | 0x4000, (uint)k.Key);   // 0x4000 = 按住不重复触发

        void ShowKeys()
        {
            recKeyBtn.Text = capturing == HotkeyId ? L("按下新快捷键…", "Press keys…") : RecordKey.ToString();
            shotKeyBtn.Text = capturing == ShotHotkeyId ? L("按下新快捷键…", "Press keys…") : ShotKey.ToString();
        }

        // 点了快捷键按钮：先把两个快捷键都放掉（不然按原来的组合会直接触发），等用户按新的
        public void BeginCapture(int id)
        {
            if (capturing != 0) EndCapture();
            capturing = id;
            UnregisterHotKey(Handle, HotkeyId); UnregisterHotKey(Handle, ShotHotkeyId);
            ShowKeys();
            Say(L("按下新的组合键（要带 Ctrl、Alt、Shift 或 Win），Esc 取消", "Press the new combination (with Ctrl, Alt, Shift or Win); Esc cancels"));
            Activate();
        }

        void EndCapture()
        {
            capturing = 0;
            Register(HotkeyId, RecordKey); Register(ShotHotkeyId, ShotKey);
            ShowKeys();
        }

        // 用户按了一个组合：注册成功才保存，被占了就保留原来的
        internal void ApplyCapture(Hotkey k)
        {
            int id = capturing;
            var other = id == HotkeyId ? ShotKey : RecordKey;
            capturing = 0;
            if (k.SameAs(other)) { EndCapture(); Say(L($"{k} 已经是另一个快捷键了，换一个", $"{k} is already the other shortcut; pick another"), true); return; }
            if (!Register(id, k)) { EndCapture(); Say(L($"{k} 已被别的程序占用，换一个", $"{k} is taken by another app; pick another"), true); return; }
            Register(id == HotkeyId ? ShotHotkeyId : HotkeyId, other);
            if (id == HotkeyId) { RecordKey = k; k.Save(Hotkey.RecordSetting); }
            else { ShotKey = k; k.Save(Hotkey.ShotSetting); }
            ShowKeys();
            Say(L($"已改成 {k}", $"Changed to {k}"));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (capturing == 0) return base.ProcessCmdKey(ref msg, keyData);
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape && (keyData & Keys.Modifiers) == 0) { EndCapture(); Say(""); return true; }
            if (Hotkey.IsModifierKey(key)) return true;   // 还在按修饰键，等真正的键
            uint mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= Hotkey.Ctrl;
            if ((keyData & Keys.Alt) != 0) mods |= Hotkey.Alt;
            if ((keyData & Keys.Shift) != 0) mods |= Hotkey.Shift;
            if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0) mods |= Hotkey.Win;
            if (mods == 0) { Say(L("要带 Ctrl、Alt、Shift 或 Win 中的一个，再按一次", "Include Ctrl, Alt, Shift or Win; try again"), true); return true; }
            ApplyCapture(new Hotkey(mods, key));
            return true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (capturing != 0) { EndCapture(); Say(""); }   // 切走了就算取消
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
            DesktopIcons.Restore();
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
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    }
}
