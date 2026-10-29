// 剪辑窗口右边的「样式」面板（照 Mac 版 StylePanel）：预设、画面比例、背景、画面（边距圆角阴影）、鼠标、运动模糊。
// 分组标题自己画（可折叠、对齐）；改了马上进工程（能撤销，滑块拖一次算一步），预览跟着变
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed class StylePanel : Panel
    {
        readonly StudioEditor ed;
        readonly FlowLayoutPanel stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(15, 12, 15, 12), Margin = new Padding(0) };
        readonly ToolTip tips = new ToolTip { InitialDelay = 400 };
        readonly List<Action> syncs = new List<Action>();
        bool syncing;
        string lastSig;
        const int W = 250;   // 面板 300 宽：左右各留 15、再给竖滚动条留 20，不会出横向滚动条
        public int ContentWidth => W;

        public StylePanel(StudioEditor ed)
        {
            this.ed = ed;
            AutoScroll = true; BackColor = Theme.Window;
            HorizontalScroll.Enabled = false; HorizontalScroll.Visible = false;
            Controls.Add(stack);
            Section(L("预设", "Presets"), PresetsBox());
            Section(L("画面比例", "Aspect Ratio"), Chips(new[] { ("original", L("原始", "Original")), ("16:9", "16:9"), ("9:16", "9:16"), ("1:1", "1:1"), ("4:3", "4:3") }, 5, l => l.Aspect, (l, v) => l.Aspect = v));
            Section(L("背景", "Background"), BackgroundBox());
            Section(L("画面", "Frame"), Box(
                Slider(L("边距", "Padding"), 0, 0.2, l => l.Padding, (l, v) => l.Padding = v, v => $"{Math.Round(v * 100)}%"),
                Slider(L("圆角", "Corners"), 0, 40, l => l.CornerRadius, (l, v) => l.CornerRadius = v, v => $"{Math.Round(v)}"),
                Slider(L("阴影", "Shadow"), 0, 1, l => l.Shadow, (l, v) => l.Shadow = v, v => $"{Math.Round(v * 100)}%"),
                Slider(L("阴影虚化", "Softness"), 0, 1, l => l.ShadowBlur, (l, v) => l.ShadowBlur = v, v => $"{Math.Round(v * 100)}%")));
            Section(L("鼠标", "Cursor"), CursorBox());
            Section(L("快捷键", "Shortcuts"), KeysBox());
            if (ed.HasCamera) Section(L("摄像头", "Camera"), CameraBox());
            Section(L("声音", "Sound"), SoundBox());
            Section(L("字幕", "Captions"), CaptionBox());
            Section(L("运动模糊", "Motion Blur"), Box(
                Chips(new[] { ("off", L("关", "Off")), ("light", L("轻", "Light")), ("medium", L("中", "Medium")), ("strong", L("强", "Strong")) }, 4, l => l.Blur, (l, v) => l.Blur = v),
                Caption(L("只在镜头快速推拉、移动时有一点拖影，镜头停着时字是清楚的", "Only adds a little streak while the camera moves fast; text stays sharp when it's still"))));
            Sync();
        }

        StudioLook Look => ed.Project.Look;

        // ---- 分组：自己画的标题行（▸ / ▾ + 粗体字），点一下折叠 ----
        readonly Dictionary<string, Control> heads = new Dictionary<string, Control>();
        // 自测截图用：把某一组滚到面板顶上
        public void ScrollTo(string title)
        {
            foreach (var kv in heads)
                if (kv.Key.Contains(title) && kv.Value != null) { int y = kv.Value.Top + stack.Top - AutoScrollPosition.Y; AutoScrollPosition = new Point(0, Math.Max(0, y - 8)); return; }
        }

        void Section(string title, Control content)
        {
            var head = new Label { Text = "▾  " + title, AutoSize = false, Width = W, Height = 26, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 0, 0), Cursor = Cursors.Hand };
            content.Margin = new Padding(0, 4, 0, 10);
            head.Click += (s, e) => { content.Visible = !content.Visible; head.Text = (content.Visible ? "▾  " : "▸  ") + title; };
            heads[title] = head;
            stack.Controls.Add(head); stack.Controls.Add(content);
        }

        static FlowLayoutPanel Box(params Control[] items)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = W, Margin = new Padding(0), Padding = new Padding(0) };
            foreach (var c in items) { c.Margin = new Padding(0, 0, 0, 6); f.Controls.Add(c); }
            return f;
        }

        Label Caption(string text) => new Label { Text = text, AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Theme.Secondary, Font = new Font(Font.FontFamily, Font.Size - 1) };

        // 小按钮组（像分段控件，能换行，不会被裁）
        Control Chips(IList<(string value, string text)> items, int columns, Func<StudioLook, string> get, Action<StudioLook, string> set)
        {
            // 固定宽度（不跟着按钮文字撑大），按钮在格子里缩放；一行放 columns 个
            int rows = (items.Count + columns - 1) / columns;
            var t = new TableLayoutPanel { ColumnCount = columns, RowCount = rows, AutoSize = false, Width = W, Height = rows * 32, Margin = new Padding(0), Padding = new Padding(0) };
            for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var buttons = new List<(Button b, string v)>();
            foreach (var (v, text) in items)
            {
                var b = new Button { Text = text, Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(0, 0, 4, 4), FlatStyle = FlatStyle.Flat, Font = Font, TabStop = false, AutoEllipsis = true, Padding = new Padding(0) };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => { if (!syncing) ed.EditLook(l => set(l, v)); };
                buttons.Add((b, v)); t.Controls.Add(b);
            }
            syncs.Add(() =>
            {
                var cur = get(Look);
                foreach (var (b, v) in buttons) { bool on = v == cur; b.BackColor = on ? Theme.Accent : Color.FromArgb(232, 232, 236); b.ForeColor = on ? Color.White : Theme.Text; }
            });
            return t;
        }

        // 一行：名字 + 滑块 + 数值；拖一次算一步撤销
        Control Slider(string title, double min, double max, Func<StudioLook, double> get, Action<StudioLook, double> set, Func<double, string> fmt)
        {
            var row = new Panel { Width = W, Height = 28, Margin = new Padding(0) };
            var name = new Label { Text = title, AutoSize = false, Width = 72, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(0, 0) };
            var bar = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false, Width = W - 72 - 46, Height = 28, Location = new Point(72, 2), TabStop = false };
            var val = new Label { AutoSize = false, Width = 46, Height = 28, TextAlign = ContentAlignment.MiddleRight, Location = new Point(W - 46, 0), ForeColor = Theme.Secondary };
            bar.Scroll += (s, e) => { if (syncing) return; double v = min + (max - min) * bar.Value / 1000.0; val.Text = fmt(v); ed.DragLook(l => set(l, v)); };
            bar.MouseUp += (s, e) => ed.DragEnd();
            bar.KeyUp += (s, e) => ed.DragEnd();
            row.Controls.Add(name); row.Controls.Add(bar); row.Controls.Add(val);
            syncs.Add(() =>
            {
                double v = get(Look);
                int iv = (int)Math.Round(Math.Min(1, Math.Max(0, (v - min) / (max - min))) * 1000);
                if (!ed.IsDragging && bar.Value != iv) bar.Value = iv;
                val.Text = fmt(v);
            });
            return row;
        }

        CheckBox Check(string text, Func<StudioLook, bool> get, Action<StudioLook, bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0) };
            c.CheckedChanged += (s, e) => { if (!syncing) ed.EditLook(l => set(l, c.Checked)); };
            syncs.Add(() => { if (c.Checked != get(Look)) c.Checked = get(Look); });
            return c;
        }

        // 色块按钮：点了弹系统取色器
        Control ColorRow(string title, Func<StudioLook, RGB> get, Action<StudioLook, RGB> set)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            var name = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            var sw = new Button { Width = 44, Height = 24, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 12, 0), TabStop = false };
            sw.FlatAppearance.BorderColor = Theme.Line;
            sw.Click += (s, e) =>
            {
                var c = get(Look);
                using (var dlg = new ColorDialog { FullOpen = true, Color = Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)) })
                    if (dlg.ShowDialog(this) == DialogResult.OK) ed.EditLook(l => set(l, new RGB(dlg.Color.R / 255.0, dlg.Color.G / 255.0, dlg.Color.B / 255.0)));
            };
            row.Controls.Add(name); row.Controls.Add(sw);
            syncs.Add(() => { var c = get(Look); sw.BackColor = Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)); });
            return row;
        }

        // ---- 预设 ----
        sealed class Item { public string Key, Text; public bool Header, Hint; public override string ToString() => Text; }
        readonly ComboBox presetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, Width = W, IntegralHeight = false, MaxDropDownItems = 14 };
        readonly Button saveBtn = new Button(), delBtn = new Button(), defBtn = new Button();
        readonly Label defLabel = new Label();

        Control PresetsBox()
        {
            presetBox.ItemHeight = 22;
            presetBox.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var it = (Item)presetBox.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) != 0 && !it.Header && !it.Hint && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var bg = new SolidBrush(sel ? SystemColors.Highlight : SystemColors.Window)) e.Graphics.FillRectangle(bg, e.Bounds);
                var f = it.Header ? new Font(Font.FontFamily, Font.Size - 1, FontStyle.Bold) : Font;
                var col = it.Header || it.Hint ? SystemColors.GrayText : sel ? SystemColors.HighlightText : SystemColors.WindowText;
                int indent = it.Header || (e.State & DrawItemState.ComboBoxEdit) != 0 ? 4 : 14;
                TextRenderer.DrawText(e.Graphics, it.Text, f, new Rectangle(e.Bounds.X + indent, e.Bounds.Y, e.Bounds.Width - indent, e.Bounds.Height), col, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (it.Header) f.Dispose();
            };
            presetBox.SelectionChangeCommitted += (s, e) =>
            {
                var it = presetBox.SelectedItem as Item;
                if (it == null || it.Header || it.Hint || it.Key == "") { lastSig = null; Sync(); return; }   // 标题、说明不能选
                ed.ApplyPreset(it.Key);
            };
            Small(saveBtn, L("存成预设…", "Save as Preset…"), (s, e) => SavePreset());
            Small(delBtn, "🗑", (s, e) => { var n = ed.Presets.Match(Look); if (n != null && !n.StartsWith("@")) { ed.Presets.Delete(n); lastSig = null; Sync(); } });
            tips.SetToolTip(delBtn, L("删除这个预设", "Delete this preset"));
            Small(defBtn, L("设为默认", "Make Default"), (s, e) => { var n = ed.Presets.Match(Look); if (n != null) { ed.Presets.SetDefault(n); lastSig = null; Sync(); } });
            tips.SetToolTip(defBtn, L("以后新录的 Studio 素材都用这套样式", "Use this style for new Studio recordings"));
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            btns.Controls.Add(saveBtn); btns.Controls.Add(delBtn);
            var def = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0), MaximumSize = new Size(W, 0) };
            defLabel.AutoSize = true; defLabel.Margin = new Padding(0, 6, 6, 0); defLabel.Font = new Font(Font.FontFamily, Font.Size - 1);
            def.Controls.Add(defLabel); def.Controls.Add(defBtn);
            syncs.Add(SyncPresets);
            return Box(presetBox, btns, def);
        }

        void Small(Button b, string text, EventHandler click)
        {
            b.Text = text; b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Theme.Line;
            b.BackColor = Theme.Control; b.Margin = new Padding(0, 0, 6, 0); b.Padding = new Padding(4, 0, 4, 0); b.MinimumSize = new Size(0, 26); b.Click += click; b.TabStop = false;
        }

        void SyncPresets()
        {
            var s = ed.Presets; var cur = s.Match(Look); string def = s.DefaultKey;
            string Label(StudioPreset p) => p.Title(L) + (p.Name == def ? L("（新录制默认）", " (default)") : "");
            var items = new List<Item>();
            if (cur == null) items.Add(new Item { Key = "", Text = L("自定义（未保存）", "Custom (unsaved)") });
            items.Add(new Item { Header = true, Text = L("内置", "Built-in") });
            items.AddRange(StudioPreset.BuiltIns.Select(p => new Item { Key = p.Name, Text = Label(p) }));
            items.Add(new Item { Header = true, Text = L("我的预设", "My Presets") });
            if (s.Presets.Count == 0) items.Add(new Item { Hint = true, Text = L("调好样式后点「存成预设…」就能存下来", "Adjust the style, then click “Save as Preset…”") });
            items.AddRange(s.Presets.Select(p => new Item { Key = p.Name, Text = Label(p) }));
            presetBox.BeginUpdate();
            presetBox.Items.Clear();
            foreach (var it in items) presetBox.Items.Add(it);
            presetBox.SelectedIndex = items.FindIndex(it => !it.Header && !it.Hint && it.Key == (cur ?? ""));
            presetBox.EndUpdate();
            delBtn.Visible = cur != null && !cur.StartsWith("@");
            defLabel.Text = "★ " + L("新录制默认：", "New recordings use: ") + (s.Find(def)?.Title(L) ?? "");
            defBtn.Visible = cur != null && cur != def;
        }

        void SavePreset()
        {
            var cur = ed.Presets.Match(Look);
            using (var f = new Form { Text = L("存成预设", "Save as Preset"), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(320, 120), Font = Font, ShowInTaskbar = false })
            {
                var lbl = new Label { Text = L("名字（同名的会被覆盖）", "Name (a preset with the same name is replaced)"), AutoSize = true, Location = new Point(14, 14) };
                var box = new TextBox { Text = cur != null && !cur.StartsWith("@") ? cur : "", Location = new Point(14, 38), Width = 292 };
                var ok = new Button { Text = L("存", "Save"), DialogResult = DialogResult.OK, Location = new Point(150, 78), Width = 74 };
                var cancel = new Button { Text = L("取消", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(232, 78), Width = 74 };
                f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) == DialogResult.OK && box.Text.Trim().Length > 0) { ed.Presets.SavePreset(box.Text, Look); lastSig = null; Sync(); }
            }
        }

        // ---- 背景 ----
        readonly Panel colorSub = new FlowLayoutPanel { AutoSize = true, Width = W, Margin = new Padding(0) };
        readonly FlowLayoutPanel gradSub = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = W, Margin = new Padding(0) };
        readonly FlowLayoutPanel wallSub = new FlowLayoutPanel { AutoSize = true, Width = W, MaximumSize = new Size(W, 0), WrapContents = true, Margin = new Padding(0) };
        readonly FlowLayoutPanel imageSub = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = W, Margin = new Padding(0) };
        readonly List<(Button b, int i)> gradSwatches = new List<(Button, int)>();
        readonly List<(PictureBox p, string file)> walls = new List<(PictureBox, string)>();
        bool wallsLoaded;
        readonly Label imageName = new Label();

        Control BackgroundBox()
        {
            var chips = Chips(new[] { ("color", L("纯色", "Color")), ("gradient", L("渐变", "Gradient")), ("wallpaper", L("壁纸", "Wallpaper")), ("image", L("图片", "Image")) }, 4, l => l.Background, (l, v) => l.Background = v);
            colorSub.Controls.Add(ColorRow(L("颜色", "Color"), l => l.Color, (l, c) => l.Color = c));
            // 渐变：6 个预设 + 起点 / 终点 + 角度
            var sw = new FlowLayoutPanel { AutoSize = true, Width = W, Margin = new Padding(0), WrapContents = true, MaximumSize = new Size(W, 0) };
            for (int i = 0; i < StudioLook.Gradients.Length; i++)
            {
                int k = i; var g = StudioLook.Gradients[i];
                var b = new Button { Width = 36, Height = 26, Margin = new Padding(0, 0, 5, 6), FlatStyle = FlatStyle.Flat, TabStop = false };
                b.Paint += (s, e) =>
                {
                    using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(b.ClientRectangle, C(g.a), C(g.b), 315f)) e.Graphics.FillRectangle(br, b.ClientRectangle);
                    bool on = Look.Gradient0.Same(g.a) && Look.Gradient1.Same(g.b);
                    using (var pen = new Pen(on ? Theme.Accent : Theme.Line, on ? 3 : 1)) e.Graphics.DrawRectangle(pen, on ? 1 : 0, on ? 1 : 0, b.Width - (on ? 3 : 1), b.Height - (on ? 3 : 1));
                };
                b.Click += (s, e) => ed.EditLook(l => { l.Gradient0 = g.a; l.Gradient1 = g.b; });
                tips.SetToolTip(b, L(g.zh, g.en));
                gradSwatches.Add((b, k)); sw.Controls.Add(b);
            }
            var ends = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            ends.Controls.Add(ColorRow(L("起点", "From"), l => l.Gradient0, (l, c) => l.Gradient0 = c));
            ends.Controls.Add(ColorRow(L("终点", "To"), l => l.Gradient1, (l, c) => l.Gradient1 = c));
            gradSub.Controls.Add(sw); gradSub.Controls.Add(ends);
            gradSub.Controls.Add(Slider(L("角度", "Angle"), 0, 360, l => l.GradientAngle, (l, v) => l.GradientAngle = v, v => $"{Math.Round(v)}°"));
            // 图片
            var pick = new Button(); Small(pick, L("选图片…", "Choose…"), (s, e) => ChooseImage());
            imageName.AutoSize = true; imageName.MaximumSize = new Size(W - 90, 0); imageName.ForeColor = Theme.Secondary; imageName.Margin = new Padding(0, 6, 0, 0);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.Add(pick); row.Controls.Add(imageName);
            imageSub.Controls.Add(row);
            var hint = Caption(L("没选图之前按白色背景预览和导出", "Until you pick one, the background stays white"));
            imageSub.Controls.Add(hint);
            syncs.Add(() =>
            {
                var l = Look;
                colorSub.Visible = l.Background == "color"; gradSub.Visible = l.Background == "gradient"; wallSub.Visible = l.Background == "wallpaper"; imageSub.Visible = l.Background == "image";
                foreach (var (b, _) in gradSwatches) b.Invalidate();
                if (wallSub.Visible) LoadWallpapers();
                foreach (var (p, f) in walls) p.BorderStyle = string.Equals(f, l.Wallpaper, StringComparison.OrdinalIgnoreCase) ? BorderStyle.Fixed3D : BorderStyle.None;
                var img = l.BackgroundImage;
                imageName.Text = l.Background == "image" && img != null ? Path.GetFileName(img) : L("选一张图片", "Pick an image");
                hint.Visible = l.Background == "image" && img == null;
            });
            return Box(chips, colorSub, gradSub, wallSub, imageSub);
        }

        static Color C(RGB c) => Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255));

        void ChooseImage()
        {
            using (var dlg = new OpenFileDialog { Title = L("选一张图片当背景", "Choose an image for the background"), Filter = L("图片", "Images") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp|*.*|*.*" })
                if (dlg.ShowDialog(this) == DialogResult.OK) ed.EditLook(l => { l.Background = "image"; l.ImagePath = dlg.FileName; l.ImageBookmark = null; });
        }

        // 壁纸缩略图：第一次切到「壁纸」时在后台读
        void LoadWallpapers()
        {
            if (wallsLoaded) return;
            wallsLoaded = true;
            var files = StudioLook.SystemWallpapers();
            if (files.Count == 0) { wallSub.Controls.Add(Caption(L("这台电脑上没找到系统壁纸，可以选「图片」用自己的", "No system wallpapers found; use “Image” to pick your own"))); return; }
            foreach (var f in files.Take(24))
            {
                var p = new PictureBox { Width = 84, Height = 52, Margin = new Padding(0, 0, 4, 4), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Under, Cursor = Cursors.Hand };
                var file = f;
                p.Click += (s, e) => ed.EditLook(l => { l.Background = "wallpaper"; l.Wallpaper = file; });
                tips.SetToolTip(p, Path.GetFileNameWithoutExtension(f));
                walls.Add((p, f)); wallSub.Controls.Add(p);
                Task.Run(() =>
                {
                    try
                    {
                        using (var img = Image.FromFile(file))
                        {
                            var th = new Bitmap(168, 104);
                            using (var g = Graphics.FromImage(th))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                float k = Math.Max(168f / img.Width, 104f / img.Height);
                                g.DrawImage(img, (168 - img.Width * k) / 2, (104 - img.Height * k) / 2, img.Width * k, img.Height * k);
                            }
                            BeginInvoke((Action)(() => { if (!p.IsDisposed) p.Image = th; else th.Dispose(); }));
                        }
                    }
                    catch { }
                });
            }
        }

        // ---- 鼠标 ----
        Control CursorBox()
        {
            var hide = Check(L("不动时自动隐藏", "Hide when idle"), l => l.HideIdleCursor, (l, v) => l.HideIdleCursor = v);
            var after = Slider(L("隐藏前", "After"), 1, 5, l => l.HideAfter, (l, v) => l.HideAfter = v, v => L($"{v:0.0} 秒", $"{v:0.0}s"));
            var ripple = Check(L("点击波纹", "Click ripple"), l => l.Ripple, (l, v) => l.Ripple = v);
            var sound = Check(L("点击音效", "Click sound"), l => l.ClickSound, (l, v) => l.ClickSound = v);
            tips.SetToolTip(sound, L("每次点击处加一声轻响（预览和导出都有；选声音时会响一下给你听）", "Adds a soft click at each click (in the preview and the export; picking a sound plays it)"));
            var names = new[] { ("soft", L("轻点", "Soft tap")), ("mouse", L("鼠标咔哒", "Mouse click")), ("thock", L("低沉", "Deep thock")), ("wood", L("木头", "Wood")), ("pop", L("气泡", "Pop")), ("tick", L("清脆滴答", "Crisp tick")) };
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W - 48, Margin = new Padding(0, 0, 6, 0) };
            foreach (var (_, n) in names) box.Items.Add(n);
            box.SelectionChangeCommitted += (s, e) => { var v = names[box.SelectedIndex].Item1; ed.EditLook(l => l.ClickStyle = v); PlayClick(v); };
            var play = new Button(); Small(play, "🔊", (s, e) => PlayClick(Look.ClickStyle));
            tips.SetToolTip(play, L("试听", "Preview sound"));
            var soundRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            soundRow.Controls.Add(box); soundRow.Controls.Add(play);
            syncs.Add(() =>
            {
                after.Visible = Look.HideIdleCursor;
                soundRow.Visible = Look.ClickSound;
                int i = Array.FindIndex(names, n => n.Item1 == Look.ClickStyle);
                if (box.SelectedIndex != i) box.SelectedIndex = Math.Max(0, i);
            });
            return Box(
                Slider(L("大小", "Size"), 0.5, 3, l => l.CursorScale, (l, v) => l.CursorScale = v, v => $"{v:0.0}×"),
                Slider(L("顺滑", "Smoothing"), 0, 1, l => l.Smoothing, (l, v) => l.Smoothing = v, v => v < 0.01 ? L("不平滑", "Off") : $"{Math.Round(v * 100)}%"),
                hide, after, ripple, sound, soundRow);
        }

        // ---- 声音：一键静音录到的声音、电脑声音 / 麦克风各自的音量（0–200%）、点击声音量；改了预览声音会在后台重做（约一两秒） ----
        Control SoundBox()
        {
            var a = ed.Info.AudioChannels;
            var items = new List<Control>();
            var mute = new CheckBox { Text = L("录到的声音静音", "Mute recorded sound"), AutoSize = true, Margin = new Padding(0) };
            mute.CheckedChanged += (s, e) => { if (!syncing) ed.Edit(p => p.Audio.Muted = mute.Checked); };
            syncs.Add(() => { if (mute.Checked != ed.Project.Audio.Muted) mute.Checked = ed.Project.Audio.Muted; });
            Func<double, string> pct = v => $"{Math.Round(v * 100)}%";
            var tracks = new List<Control>();
            if (a.Count == 1) tracks.Add(SliderP(L("录到的", "Recorded"), 0, 2, p => p.Audio.System, (p, v) => p.Audio.System = v, pct));
            else
            {
                if (a.Any(c => c != 1)) tracks.Add(SliderP(L("电脑声音", "Computer"), 0, 2, p => p.Audio.System, (p, v) => p.Audio.System = v, pct));
                if (a.Any(c => c == 1)) tracks.Add(SliderP(L("麦克风", "Microphone"), 0, 2, p => p.Audio.Mic, (p, v) => p.Audio.Mic = v, pct));
            }
            if (a.Count > 0) { items.Add(mute); items.AddRange(tracks); }
            else items.Add(Caption(L("这段录制没有声音", "This recording has no sound")));
            var clicks = SliderP(L("点击声", "Clicks"), 0, 2, p => p.Audio.Clicks, (p, v) => p.Audio.Clicks = v, pct);
            items.Add(clicks);
            // 讲解：总音量、讲解时压低原声音；录讲解的两个开关（记在设置里，不进工程）
            items.Add(SliderP(L("讲解", "Voice-over"), 0, 2, p => p.Audio.Voice, (p, v) => p.Audio.Voice = v, pct));
            var duck = CheckP(L("讲解时自动压低原声音", "Lower the original under voice-over"), p => p.Audio.Duck, (p, v) => p.Audio.Duck = v);
            tips.SetToolTip(duck, L("有讲解的地方把录到的声音压低约 12 dB，讲完慢慢回来", "Lowers the recorded sound by about 12 dB while you talk, then brings it back"));
            items.Add(duck);
            items.Add(SettingCheck(L("录讲解前倒数 3 秒", "Count down 3 s before recording voice-over"), "voiceCountdown", true));
            items.Add(SettingCheck(L("录讲解时原声音静音", "Mute the original while recording voice-over"), "voiceMute", false));
            // 降噪：麦克风和讲解；Mac 录的电脑声音是单独一条，可以另外打开
            items.Add(new Label { Text = L("降噪", "Noise reduction"), AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
            items.Add(ChipsP(new[] { ("off", L("关", "Off")), ("light", L("轻", "Light")), ("medium", L("中", "Medium")), ("strong", L("强", "Strong")) }, 4, p => p.Audio.Denoise, (p, v) => p.Audio.Denoise = v));
            if (a.Count > 1 && a.Any(c => c != 1)) items.Add(CheckP(L("电脑声音也降噪", "Also clean the computer audio"), p => p.Audio.DenoiseSystem, (p, v) => p.Audio.DenoiseSystem = v));
            items.Add(Caption(a.Count > 1 ? L("去掉键盘、风扇、空调这类持续的杂音；作用在麦克风和讲解上", "Removes steady background noise (fans, hum); applies to the microphone and voice-overs")
                                          : L("去掉键盘、风扇、空调这类持续的杂音；作用在录到的声音（含麦克风）和讲解上", "Removes steady background noise (fans, hum); applies to the recorded sound and voice-overs")));
            items.Add(Caption(L("预览里听到的就是成片的声音；改了音量、剪辑后稍等一两秒生效", "The preview plays the final mix; after changing volume or cuts it updates in a second or two")));
            syncs.Add(() =>
            {
                foreach (var t in tracks) t.Enabled = !ed.Project.Audio.Muted;
                clicks.Visible = ed.Project.Look.ClickSound;
            });
            return Box(items.ToArray());
        }

        // ---- 字幕样式：显示、粗细、字号、颜色、描边、阴影、底色条、位置（同 Mac 版 CaptionStyle） ----
        Control CaptionBox()
        {
            CaptionStyle C(StudioProject p) => p.CaptionStyle;
            var items = new List<Control>
            {
                CheckP(L("显示字幕", "Show captions"), p => C(p).Show, (p, v) => C(p).Show = v),
                ChipsP(new[] { ("regular", L("常规", "Regular")), ("medium", L("中粗", "Medium")), ("semibold", L("粗", "Bold")) }, 3, p => C(p).Weight, (p, v) => C(p).Weight = v),
                SliderP(L("字号", "Size"), 0.03, 0.09, p => C(p).Size, (p, v) => C(p).Size = v, v => $"{Math.Round(v * 100)}%"),
                ChipsP(new[] { ("white", L("白字", "White")), ("yellow", L("黄字", "Yellow")), ("black", L("黑字", "Black")) }, 3,
                       p => C(p).Color.Same(RGB.White) ? "white" : C(p).Color.Same(new RGB(1, 0.85, 0.2)) ? "yellow" : C(p).Color.Same(new RGB(0.1, 0.1, 0.1)) ? "black" : "",
                       (p, v) => C(p).Color = v == "yellow" ? new RGB(1, 0.85, 0.2) : v == "black" ? new RGB(0.1, 0.1, 0.1) : RGB.White),
                CheckP(L("黑色描边", "Outline"), p => C(p).Outline, (p, v) => C(p).Outline = v),
                CheckP(L("阴影", "Shadow"), p => C(p).Shadow, (p, v) => C(p).Shadow = v),
                new Label { Text = L("底色条", "Background bar"), AutoSize = true, Margin = new Padding(0, 6, 0, 2) },
                ChipsP(new[] { ("none", L("无", "None")), ("dark", L("深色", "Dark")), ("light", L("浅色", "Light")) }, 3, p => C(p).Box, (p, v) => C(p).Box = v),
                ChipsP(new[] { ("bottom", L("在底部", "Bottom")), ("top", L("在顶部", "Top")) }, 2, p => C(p).Position, (p, v) => C(p).Position = v),
                SliderP(L("离边", "Margin"), 0.02, 0.2, p => C(p).Margin, (p, v) => C(p).Margin = v, v => $"{Math.Round(v * 100)}%"),
                Caption(L("点播放栏的「字幕」自动把说的话变成文字（在本机识别，不上传）；时间轴上双击字幕块改字", "Use Captions in the play bar to turn speech into text (on this PC, nothing uploaded); double-click a caption on the timeline to edit it")),
            };
            return Box(items.ToArray());
        }

        // ---- 快捷键键帽：显示、位置、大小、深浅（同 Mac 版） ----
        Control KeysBox()
        {
            var items = new List<Control>
            {
                Check(L("在画面上显示按的快捷键", "Show shortcuts on screen"), l => l.ShowKeys, (l, v) => l.ShowKeys = v),
                Chips(new[] { ("bottomLeft", L("左下", "Left")), ("bottom", L("底部居中", "Centre")), ("bottomRight", L("右下", "Right")) }, 3, l => l.KeysAt, (l, v) => l.KeysAt = v),
                Slider(L("大小", "Size"), 0.03, 0.08, l => l.KeysSize, (l, v) => l.KeysSize = v, v => $"{Math.Round(v * 100)}%"),
                Chips(new[] { ("dark", L("深色键帽", "Dark")), ("light", L("浅色键帽", "Light")) }, 2, l => l.KeysDark ? "dark" : "light", (l, v) => l.KeysDark = v == "dark"),
                Caption(ed.Track.Shortcuts == null
                    ? L("这段素材录的时候还没有记快捷键（新录的才有）", "This recording has no shortcut data (new recordings do)")
                    : L($"录的时候按过 {ed.Track.Shortcuts.Count} 次快捷键（只记 Ctrl / Alt / Win 组合和 Esc、方向键这类功能键，不记打的字）",
                        $"{ed.Track.Shortcuts.Count} shortcuts recorded (only Ctrl / Alt / Win combos and keys like Esc or arrows; normal typing isn't recorded)")),
            };
            return Box(items.ToArray());
        }

        // ---- 摄像头：显示、形状、大小、位置、边框、阴影、镜像（同 Mac 版）；在预览上按住小窗也能拖 ----
        Control CameraBox()
        {
            CameraSettings C(StudioProject p) => p.Camera ?? (p.Camera = ed.Track.Camera != null ? CameraSettings.From(ed.Track.Camera) : new CameraSettings());
            var corners = new[] { ("tl", L("左上", "Top left"), 0.14, 0.2), ("tr", L("右上", "Top right"), 0.86, 0.2), ("bl", L("左下", "Bottom left"), 0.14, 0.8), ("br", L("右下", "Bottom right"), 0.86, 0.8) };
            var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            var swatch = new Button { Width = 44, Height = 24, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 12, 0), TabStop = false };
            swatch.FlatAppearance.BorderColor = Theme.Line;
            swatch.Click += (s, e) =>
            {
                var c = C(ed.Project).BorderColor;
                using (var dlg = new ColorDialog { FullOpen = true, Color = Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)) })
                    if (dlg.ShowDialog(this) == DialogResult.OK) ed.EditCamera(cs => cs.BorderColor = new RGB(dlg.Color.R / 255.0, dlg.Color.G / 255.0, dlg.Color.B / 255.0));
            };
            colorRow.Controls.Add(new Label { Text = L("边框颜色", "Border color"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) }); colorRow.Controls.Add(swatch);
            syncs.Add(() => { var c = C(ed.Project).BorderColor; swatch.BackColor = Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)); });
            var items = new List<Control>
            {
                CheckP(L("显示摄像头画面", "Show camera"), p => C(p).Show, (p, v) => C(p).Show = v),
                ChipsP(new[] { ("round", L("圆形", "Circle")), ("square", L("圆角方形", "Rounded")) }, 2, p => C(p).Round ? "round" : "square", (p, v) => C(p).Round = v == "round"),
                SliderP(L("大小", "Size"), 0.1, 0.5, p => C(p).Size, (p, v) => C(p).Size = v, v => $"{Math.Round(v * 100)}%"),
                ChipsP(corners.Select(c => (c.Item1, c.Item2)).ToList(), 2,
                       p => corners.FirstOrDefault(c => Math.Abs(C(p).X - c.Item3) < 0.02 && Math.Abs(C(p).Y - c.Item4) < 0.02).Item1 ?? "",
                       (p, v) => { var c = corners.First(x => x.Item1 == v); C(p).X = c.Item3; C(p).Y = c.Item4; }),
                SliderP(L("边框", "Border"), 0, 12, p => C(p).Border, (p, v) => C(p).Border = v, v => $"{Math.Round(v)}"),
                colorRow,
                SliderP(L("阴影", "Shadow"), 0, 1, p => C(p).Shadow, (p, v) => C(p).Shadow = v, v => $"{Math.Round(v * 100)}%"),
                CheckP(L("镜像（像照镜子）", "Mirror"), p => C(p).Mirror, (p, v) => C(p).Mirror = v),
                Caption(L("在预览上按住小窗可以拖到任意位置；时间轴「布局」轨上双击加一段，可以切换全屏 / 分屏 / 隐藏；推近时人像不跟着放大",
                          "Drag the bubble in the preview to move it; double-click the Layout lane to switch to full / split / hidden; the camera isn't magnified when zooming")),
            };
            return Box(items.ToArray());
        }

        // 勾选框：改工程里样式以外的东西（声音、字幕样式），能撤销
        CheckBox CheckP(string text, Func<StudioProject, bool> get, Action<StudioProject, bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0) };
            c.CheckedChanged += (s, e) => { if (!syncing) ed.Edit(p => set(p, c.Checked)); };
            syncs.Add(() => { if (c.Checked != get(ed.Project)) c.Checked = get(ed.Project); });
            return c;
        }

        // 勾选框：记在设置里的开关（录讲解的倒数、静音）
        CheckBox SettingCheck(string text, string key, bool def)
        {
            var c = new CheckBox { Text = text, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0), Checked = Settings.Get(key, def) };
            c.CheckedChanged += (s, e) => Settings.Set(key, c.Checked);
            return c;
        }

        // 小按钮组：改工程里样式以外的东西
        Control ChipsP(IList<(string value, string text)> items, int columns, Func<StudioProject, string> get, Action<StudioProject, string> set)
        {
            int rows = (items.Count + columns - 1) / columns;
            var t = new TableLayoutPanel { ColumnCount = columns, RowCount = rows, AutoSize = false, Width = W, Height = rows * 32, Margin = new Padding(0), Padding = new Padding(0) };
            for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var buttons = new List<(Button b, string v)>();
            foreach (var (v, text) in items)
            {
                var b = new Button { Text = text, Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(0, 0, 4, 4), FlatStyle = FlatStyle.Flat, Font = Font, TabStop = false, AutoEllipsis = true, Padding = new Padding(0) };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => { if (!syncing) ed.Edit(p => set(p, v)); };
                buttons.Add((b, v)); t.Controls.Add(b);
            }
            syncs.Add(() =>
            {
                var cur = get(ed.Project);
                foreach (var (b, v) in buttons) { bool on = v == cur; b.BackColor = on ? Theme.Accent : Color.FromArgb(232, 232, 236); b.ForeColor = on ? Color.White : Theme.Text; }
            });
            return t;
        }

        // 同 Slider，但改的是工程里样式以外的东西（声音）
        Control SliderP(string title, double min, double max, Func<StudioProject, double> get, Action<StudioProject, double> set, Func<double, string> fmt)
        {
            var row = new Panel { Width = W, Height = 28, Margin = new Padding(0) };
            var name = new Label { Text = title, AutoSize = false, Width = 72, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(0, 0) };
            var bar = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false, Width = W - 72 - 46, Height = 28, Location = new Point(72, 2), TabStop = false };
            var val = new Label { AutoSize = false, Width = 46, Height = 28, TextAlign = ContentAlignment.MiddleRight, Location = new Point(W - 46, 0), ForeColor = Theme.Secondary };
            bar.Scroll += (s, e) => { if (syncing) return; double v = min + (max - min) * bar.Value / 1000.0; val.Text = fmt(v); ed.DragBegin(); ed.DragUpdate(p => set(p, v)); };
            bar.MouseUp += (s, e) => ed.DragEnd();
            bar.KeyUp += (s, e) => ed.DragEnd();
            row.Controls.Add(name); row.Controls.Add(bar); row.Controls.Add(val);
            syncs.Add(() =>
            {
                double v = get(ed.Project);
                int iv = (int)Math.Round(Math.Min(1, Math.Max(0, (v - min) / (max - min))) * 1000);
                if (!ed.IsDragging && bar.Value != iv) bar.Value = iv;
                val.Text = fmt(v);
            });
            return row;
        }

        static System.Media.SoundPlayer clickPlayer;
        public static void PlayClick(string style)
        {
            try
            {
                clickPlayer?.Dispose();
                clickPlayer = new System.Media.SoundPlayer(new MemoryStream(ClickSound.One(style)));
                clickPlayer.Play();
            }
            catch { }
        }

        // 工程或预设变了：把各个控件的值对上（只在样式真变了时才做）
        public void Sync()
        {
            var cw = new JsonW(); if (ed.Project.Camera != null) ed.Project.Camera.Write(cw);
            var sig = Look.ToJson() + "|" + cw + "|" + ed.Project.Audio.ToJson() + "|" + ed.Project.CaptionStyle.ToJson() + "|" + ed.Presets.DefaultKey + "|" + string.Join(",", ed.Presets.Presets.Select(p => p.Name));
            if (sig == lastSig) return;
            lastSig = sig;
            syncing = true;
            SuspendLayout();
            try { foreach (var s in syncs) s(); }
            finally { syncing = false; ResumeLayout(); }
        }
    }
}
