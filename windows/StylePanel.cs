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
            Section(L("运动模糊", "Motion Blur"), Box(
                Chips(new[] { ("off", L("关", "Off")), ("light", L("轻", "Light")), ("medium", L("中", "Medium")), ("strong", L("强", "Strong")) }, 4, l => l.Blur, (l, v) => l.Blur = v),
                Caption(L("只在镜头快速推拉、移动时有一点拖影，镜头停着时字是清楚的", "Only adds a little streak while the camera moves fast; text stays sharp when it's still"))));
            Sync();
        }

        StudioLook Look => ed.Project.Look;

        // ---- 分组：自己画的标题行（▸ / ▾ + 粗体字），点一下折叠 ----
        void Section(string title, Control content)
        {
            var head = new Label { Text = "▾  " + title, AutoSize = false, Width = W, Height = 26, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 0, 0), Cursor = Cursors.Hand };
            content.Margin = new Padding(0, 4, 0, 10);
            head.Click += (s, e) => { content.Visible = !content.Visible; head.Text = (content.Visible ? "▾  " : "▸  ") + title; };
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
            var t = new TableLayoutPanel { ColumnCount = columns, AutoSize = true, Width = W, Margin = new Padding(0), Padding = new Padding(0) };
            for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            var buttons = new List<(Button b, string v)>();
            foreach (var (v, text) in items)
            {
                var b = new Button { Text = text, Dock = DockStyle.Fill, Height = 28, Margin = new Padding(0, 0, 4, 4), FlatStyle = FlatStyle.Flat, Font = Font, TabStop = false };
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
                var b = new Button { Width = 38, Height = 26, Margin = new Padding(0, 0, 6, 6), FlatStyle = FlatStyle.Flat, TabStop = false };
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
            tips.SetToolTip(sound, L("导出时在每次点击处加一声轻响（预览里听不到，选声音时会响一下给你听）", "Adds a soft click at each click when exporting (not in the preview; picking a sound plays it)"));
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
            var sig = Look.ToJson() + "|" + ed.Presets.DefaultKey + "|" + string.Join(",", ed.Presets.Presets.Select(p => p.Name));
            if (sig == lastSig) return;
            lastSig = sig;
            syncing = true;
            SuspendLayout();
            try { foreach (var s in syncs) s(); }
            finally { syncing = false; ResumeLayout(); }
        }
    }
}
