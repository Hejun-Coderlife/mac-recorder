// 剪辑窗口：导出前自检（照 Mac 版 exportIssues / exportChecked）：容易漏的问题列出来——占位文字、空字幕、没声音、
// 原始大分辨率、文件很大、摄像头小窗压住标注；有问题先问「去修改 / 仍要导出」。
// Mac 版还查「透明背景导出成 .mov」：Windows 版没有透明背景，这条不用查
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class StudioEditor
    {
        static readonly HashSet<string> Placeholders = new HashSet<string> { "text", "hello", "123", "文字", "test", "测试", "点这里" };

        public List<string> ExportIssues()
        {
            var o = ExportOpt; var p = Project;
            var outL = new List<string>();
            var ph = p.Annotations.Where(a => a.Kind == "text" && Placeholders.Contains((a.Text ?? "").Trim().ToLowerInvariant())).ToList();
            if (ph.Count > 0)
            {
                var names = ph.Select(a => a.Text).Distinct().OrderBy(x => x, StringComparer.Ordinal);
                outL.Add(L($"有 {ph.Count} 个文字标注看起来是测试用的（{string.Join("、", names)}）", $"{ph.Count} text notes look like placeholders ({string.Join(", ", names)})"));
            }
            int emptyCaps = p.Captions.Count(c => string.IsNullOrWhiteSpace(c.Text));
            if (emptyCaps > 0) outL.Add(L($"有 {emptyCaps} 句字幕是空的", $"{emptyCaps} captions are empty"));
            var lay = new StudioLayout(Track, p.Look);
            if (!o.Gif)
            {
                var a = p.Audio;
                bool recorded = Info.AudioChannels.Count > 0 && !a.Muted && (a.System > 0.01 || a.Mic > 0.01);
                if (!recorded && p.Voiceovers.Count == 0 && p.Music.Clips.Count == 0 && !(p.Look.ClickSound || Pieces.LookBool(p, "typeSound")))
                    outL.Add(L("成片没有任何声音", "The video will have no sound"));
                if (o.Resolution == "original" && Math.Min(lay.cw, lay.ch) > 1500)
                    outL.Add(L($"分辨率是「原始大小」（{(int)lay.cw}×{(int)lay.ch}）：文件大、导出慢，发出去一般选 1080p 或 1440p 就够", $"Resolution is Original ({(int)lay.cw}×{(int)lay.ch}): large and slow; 1080p or 1440p is usually enough"));
                var map = new TimeMap(p);
                double secs = map.Out(p.TrimEnd) - map.Out(p.TrimStart);
                double mb = EstimatedBytes(o, lay, secs) / 1e6;
                if (mb > 500) outL.Add(L($"预计文件约 {(int)mb} MB，比较大", $"Estimated size about {(int)mb} MB"));
            }
            // 摄像头小窗压住了标注（同一时间、位置重叠；那段布局是「隐藏」就不算）
            if (HasCamera && p.Camera != null && p.Camera.Show)
            {
                var (px, py, side) = CameraLayer.PipRect(p.Camera, lay.cw, lay.ch);
                double cx0 = px / lay.cw, cy0 = py / lay.ch, cx1 = (px + side) / lay.cw, cy1 = (py + side) / lay.ch;
                int hit = p.Annotations.Count(an =>
                {
                    double nx = Math.Min(an.X, an.X + an.W), ny = Math.Min(an.Y, an.Y + an.H), nw = an.Kind == "text" ? 0.1 : Math.Abs(an.W), nh = an.Kind == "text" ? 0.05 : Math.Abs(an.H);
                    // 标注的位置是原画面比例，换成画布比例再比
                    double x0 = (lay.card.X + nx * lay.W * lay.s) / lay.cw, y0 = 1 - (lay.card.Y + (1 - ny) * lay.H * lay.s) / lay.ch;
                    double x1 = x0 + nw * lay.W * lay.s / lay.cw, y1 = y0 + nh * lay.H * lay.s / lay.ch;
                    bool overlap = x0 < cx1 && x1 > cx0 && y0 < cy1 && y1 > cy0;
                    return overlap && !p.Layouts.Any(l => l.Start <= an.Start && l.End >= an.End && l.Mode == "hidden");
                });
                if (hit > 0) outL.Add(L($"摄像头小窗压住了 {hit} 个标注，可以把小窗拖到别的角落", $"The camera bubble covers {hit} annotations; drag it to another corner"));
            }
            return outL;
        }

        // 预计文件大小：视频码率同导出（按画布像素、帧率、画质，不低于最低码率）+ 192k 声音
        static double EstimatedBytes(ExportOptions o, StudioLayout lay, double secs)
        {
            double k = o.CanvasScale(lay.cw, lay.ch), w = Math.Round(lay.cw * k), h = Math.Round(lay.ch * k);
            double vb = Math.Max(o.MinRate, w * h * o.Fps * o.BitsPerPixel);
            return (vb + 192_000) / 8 * Math.Max(0, secs);
        }

        // 点「导出」：有问题先问（去修改 / 仍要导出）
        public static Func<List<string>, bool> AskExportAnyway;   // 自测用：代替弹框
        public void ExportChecked()
        {
            var issues = ExportIssues();
            if (issues.Count == 0 || TestMode) { Export(); return; }
            bool go;
            if (AskExportAnyway != null) go = AskExportAnyway(issues);
            else
            {
                var text = string.Join(Environment.NewLine, issues.Select(x => "• " + x));
                using (var d = new TaskDialogLite(L($"导出前检查到 {issues.Count} 个问题", $"{issues.Count} things to check before exporting"), text, L("去修改", "Go Back"), L("仍要导出", "Export Anyway")))
                    go = d.ShowDialog(this) == DialogResult.Yes;
            }
            if (go) Export();
        }
    }

    // 两个按钮的小提示框（系统 MessageBox 的按钮字改不了）：「去修改」是默认，Esc 也是它
    sealed class TaskDialogLite : Form
    {
        public TaskDialogLite(string title, string text, string back, string go)
        {
            Text = L("录屏", "Recorder"); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi; Font = new System.Drawing.Font("Microsoft YaHei UI", 9f); BackColor = Theme.Window;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(18, 14, 18, 12);
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            flow.Controls.Add(new Label { Text = title, AutoSize = true, Font = new System.Drawing.Font(Font.FontFamily, 11f, System.Drawing.FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
            flow.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0), Margin = new Padding(0, 0, 0, 14) });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            var b1 = new Button { Text = back, DialogResult = DialogResult.No, AutoSize = true, MinimumSize = new System.Drawing.Size(90, 30) };
            var b2 = new Button { Text = go, DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new System.Drawing.Size(90, 30) };
            row.Controls.Add(b1); row.Controls.Add(b2);
            flow.Controls.Add(row);
            Controls.Add(flow);
            AcceptButton = b1; CancelButton = b1;
        }
    }
}
