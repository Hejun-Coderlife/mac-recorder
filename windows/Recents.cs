// 主窗口下方「最近的 Studio 素材」（照 Mac 版 Recents.swift）：最近 8 份，缩略图 + 名字 + 时长 + 日期，横向滚动；
// 单击打开剪辑窗口，右键「在资源管理器中显示 / 移到回收站」（删除前确认）。缩略图在后台用 ffmpeg 抽第 1 秒那一帧
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed class RecentStrip : Panel
    {
        public const int Max = 8, TileW = 136, TileH = 84;
        readonly FlowLayoutPanel flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(0), Margin = new Padding(0) };
        readonly Label head = new Label();
        readonly ToolTip tips = new ToolTip();
        public event Action<string> OpenRequested;
        public event Action<string> Message;
        string sig;
        static readonly Dictionary<string, (Image img, double dur)> cache = new Dictionary<string, (Image, double)>(StringComparer.OrdinalIgnoreCase);

        public RecentStrip()
        {
            Height = TileH + 66;
            head.Text = L("最近的 Studio 素材", "Recent Studio Footage"); head.AutoSize = true; head.Dock = DockStyle.Top; head.Padding = new Padding(0, 0, 0, 4);
            head.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            Controls.Add(flow); Controls.Add(head);
        }

        // 素材夹：里面有 raw.mp4 和 track.json
        public static bool IsFootage(string dir) =>
            !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && File.Exists(Path.Combine(dir, "raw.mp4")) && File.Exists(Path.Combine(dir, "track.json"));

        // 拖进来的是素材夹，或素材夹里的某个文件 → 素材夹；不是就 null
        public static string FootageOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IsFootage(path)) return path;
            var d = Path.GetDirectoryName(path);
            return IsFootage(d) ? d : null;
        }

        public static List<string> List()
        {
            try
            {
                var root = StudioSession.Folder;
                return Directory.GetDirectories(root).Where(IsFootage)
                    .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "raw.mp4"))).Take(Max).ToList();
            }
            catch { return new List<string>(); }
        }

        // 重新列一遍（窗口拿到焦点、录完、导出完都会叫）；没变就不动
        public void Reload()
        {
            var dirs = List();
            var s = string.Join("|", dirs);
            if (s == sig) return;
            sig = s;
            Visible = dirs.Count > 0;
            flow.SuspendLayout();
            foreach (Control c in flow.Controls.Cast<Control>().ToList()) { flow.Controls.Remove(c); c.Dispose(); }
            foreach (var d in dirs) flow.Controls.Add(Tile(d));
            flow.ResumeLayout();
        }
        public int Count => flow.Controls.Count;

        Control Tile(string dir)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var p = new Panel { Width = TileW, Height = TileH + 40, Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand, Tag = dir };
            var pic = new PictureBox { Width = TileW, Height = TileH, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(228, 228, 231), Location = new Point(0, 0), Cursor = Cursors.Hand };
            var t1 = new Label { Text = name, AutoSize = false, AutoEllipsis = true, Width = TileW, Height = 18, Location = new Point(0, TileH + 3) };
            var t2 = new Label { Text = File.GetLastWriteTime(Path.Combine(dir, "raw.mp4")).ToString("MM-dd HH:mm"), AutoSize = false, Width = TileW, Height = 16, Location = new Point(0, TileH + 21), ForeColor = SystemColors.GrayText };
            p.Controls.Add(pic); p.Controls.Add(t1); p.Controls.Add(t2);
            tips.SetToolTip(pic, L("单击打开剪辑窗口；右键更多", "Click to edit; right-click for more"));
            foreach (Control c in new Control[] { p, pic, t1, t2 })
                c.MouseUp += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(dir);
                    else if (e.Button == MouseButtons.Right) Menu(dir, (Control)s, e.Location);
                };
            if (cache.TryGetValue(dir, out var c0)) { pic.Image = c0.img; t2.Text += "  ·  " + Dur(c0.dur); }
            else
                Task.Run(() =>
                {
                    Image img = null; double dur = 0;
                    try
                    {
                        var raw = Path.Combine(dir, "raw.mp4");
                        var info = Studio.Probe(raw); dur = info.Duration;
                        double k = Math.Min(TileW * 2.0 / info.Width, TileH * 2.0 / info.Height);
                        int w = Math.Max(2, (int)(info.Width * k) / 2 * 2), h = Math.Max(2, (int)(info.Height * k) / 2 * 2);
                        var px = Studio.DecodeFrame(raw, Math.Min(1, info.Duration / 2), w, h, info.Matrix);
                        if (px != null)
                        {
                            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
                            bmp.UnlockBits(bd);
                            img = bmp;
                        }
                    }
                    catch { }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            cache[dir] = (img, dur);
                            if (pic.IsDisposed) return;
                            pic.Image = img; t2.Text += "  ·  " + Dur(dur);
                        }));
                    }
                    catch { }
                });
            return p;
        }

        static string Dur(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

        void Menu(string dir, Control at, Point pt)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(L("打开剪辑", "Edit"), null, (s, e) => OpenRequested?.Invoke(dir));
            m.Items.Add(L("在资源管理器中显示", "Show in Explorer"), null, (s, e) => { try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { } });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(L("移到回收站…", "Move to Recycle Bin…"), null, (s, e) =>
            {
                var name = Path.GetFileName(dir);
                if (MessageBox.Show(FindForm(), L($"把「{name}」移到回收站？\n（原始素材和剪辑记录都会删掉，已经导出的视频不受影响）", $"Move “{name}” to the Recycle Bin?\n(The raw footage and edits are removed; exported videos are not affected)"),
                    App.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                if (Recycle(dir)) { cache.Remove(dir); sig = null; Reload(); Message?.Invoke(L("已移到回收站", "Moved to the Recycle Bin")); }
                else Message?.Invoke(L("删除失败：可能正在剪辑窗口里打开", "Couldn't delete it (is it open in the editor?)"));
            });
            m.Show(at, pt);
        }

        // 移到回收站（能还原）
        public static bool Recycle(string path)
        {
            var op = new SHFILEOPSTRUCT { wFunc = 3 /* FO_DELETE */, pFrom = path + "\0\0", fFlags = 0x0040 | 0x0010 | 0x0004 /* ALLOWUNDO | NOCONFIRMATION | SILENT */ };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !Directory.Exists(path);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings; [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    }
}
