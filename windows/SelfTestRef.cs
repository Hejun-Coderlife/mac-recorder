// 画面对照自测（windows/tests/reference，见那里的 README）：
//   REFTEST=对照包目录 → 每个 case 把 footage/ 复制一份、case 的 json 存成 project.json，按 index.json 渲染同一秒（1920 宽），
//   CPU 版存 win/<同名>.png、显卡版存 %TEMP%\reftest\gpu\；再和 mac/ 的标准图比、显卡和 CPU 互相比，
//   并排图（左 Mac | 右 Windows）存 %TEMP%\reftest\side\。结果写 %TEMP%\ref_test.txt
//   REFTEST_ONLY=名字里含这段的才跑（比如 overlays@text）；REFTEST_COMPOSE=cpu → 只跑 CPU
// 差值 = 两张图逐像素 RGB 差的平均（0–255）；按区域看最大的一格（64×64），字体不同的地方会有一点差，样式画错会差很多
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder
{
    static class SelfTestRef
    {
        // 每张图允许的差（整幅平均、最差的一格）；字体（苹方 / 微软雅黑）和编码的差都在里面。算法改了、差超过这个就失败
        public const double MaxMean = 6, MaxCell = 60, MaxGpuMean = 1.5, MaxGpuCell = 12;

        public static int Run()
        {
            string refDir = Environment.GetEnvironmentVariable("REFTEST"), only = Environment.GetEnvironmentVariable("REFTEST_ONLY");
            bool gpu = Environment.GetEnvironmentVariable("REFTEST_COMPOSE") != "cpu";
            string result = Path.Combine(Path.GetTempPath(), "ref_test.txt"), tmp = Path.Combine(Path.GetTempPath(), "reftest");
            var sb = new StringBuilder(); bool ok = true;
            void Line(string s) { sb.AppendLine(s); File.WriteAllText(result, sb.ToString()); }
            try
            {
                string win = Path.Combine(refDir, "win"), gdir = Path.Combine(tmp, "gpu"), side = Path.Combine(tmp, "side");
                foreach (var d in new[] { win, gdir, side }) Directory.CreateDirectory(d);
                var index = ((List<object>)Json.Parse(File.ReadAllText(Path.Combine(refDir, "index.json")))).Cast<Dictionary<string, object>>().ToList();
                var work = new Dictionary<string, string>();
                foreach (var e in index)
                {
                    string cs = (string)e["case"], png = Path.GetFileName((string)e["png"]);
                    if (only != null && png.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    double t = Convert.ToDouble(e["time"], CultureInfo.InvariantCulture);
                    if (!work.TryGetValue(cs, out var dir))
                    {
                        dir = Path.Combine(tmp, "work", cs);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        Directory.CreateDirectory(dir);
                        foreach (var f in Directory.GetFiles(Path.Combine(refDir, "footage"))) if (Path.GetFileName(f) != "bg.png") File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                        File.Copy(Path.Combine(refDir, "cases", cs + ".json"), StudioProject.PathIn(dir));
                        work[cs] = dir;
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string wp = Path.Combine(win, png), gp = Path.Combine(gdir, png), mp = Path.Combine(refDir, "mac", png);
                    StudioStill.Render(dir, t, wp, "cpu", 1920, CursorTracker.LoadCursor);
                    double cpuS = sw.Elapsed.TotalSeconds;
                    var (mean, cell) = Diff(mp, wp);
                    bool pass = mean <= MaxMean && cell <= MaxCell;
                    string g = "";
                    if (gpu)
                    {
                        string name = StudioStill.Render(dir, t, gp, "gpu", 1920, CursorTracker.LoadCursor);
                        var (gm, gc) = Diff(wp, gp);
                        bool gpass = gm <= MaxGpuMean && gc <= MaxGpuCell;
                        pass &= gpass;
                        g = string.Format(CultureInfo.InvariantCulture, "  gpu({0}) vs cpu mean={1:0.00} cell={2:0.0}{3}", name, gm, gc, gpass ? "" : " ← 显卡和 CPU 不一致");
                    }
                    SideBySide(mp, wp, Path.Combine(side, png));
                    ok &= pass;
                    Line(string.Format(CultureInfo.InvariantCulture, "{0} {1}  mac vs win mean={2:0.00} cell={3:0.0}  ({4:0.0}s){5}", pass ? "PASS" : "FAIL", png, mean, cell, cpuS, g));
                }
                Line(ok ? "REFTEST OK" : "REFTEST FAILED");
            }
            catch (Exception ex) { Line("FAIL exception " + ex); ok = false; }
            return ok ? 0 : 1;
        }

        static byte[] Pixels(string file, out int w, out int h)
        {
            using (var src = new Bitmap(file))
            using (var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Black); g.DrawImage(src, 0, 0, src.Width, src.Height); }   // Mac 的图带透明通道：垫黑底（成片本来就不透明）
                w = bmp.Width; h = bmp.Height;
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * w * 4, w * 4);
                bmp.UnlockBits(bd);
                return px;
            }
        }

        // 整幅平均差、64×64 格里平均差最大的那格
        public static (double mean, double cell) Diff(string a, string b)
        {
            var pa = Pixels(a, out int w, out int h); var pb = Pixels(b, out int w2, out int h2);
            if (w != w2 || h != h2) return (255, 255);
            const int C = 64; int cx = (w + C - 1) / C, cy = (h + C - 1) / C;
            var cs = new double[cx * cy]; var cn = new int[cx * cy]; double tot = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    double d = (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2])) / 3.0;
                    tot += d; int k = (y / C) * cx + x / C; cs[k] += d; cn[k]++;
                }
            double worst = 0; for (int k = 0; k < cs.Length; k++) if (cn[k] > 0) worst = Math.Max(worst, cs[k] / cn[k]);
            return (tot / (w * (double)h), worst);
        }

        public static void SideBySide(string mac, string win, string outPng)
        {
            using (var a = new Bitmap(mac)) using (var b = new Bitmap(win))
            {
                int gap = 16, top = 44, w = a.Width + gap + b.Width, h = Math.Max(a.Height, b.Height) + top;
                using (var o = new Bitmap(w, h))
                using (var g = Graphics.FromImage(o))
                using (var f = new Font("Microsoft YaHei UI", 24, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    g.Clear(Color.FromArgb(30, 30, 32));
                    g.DrawImage(a, 0, top, a.Width, a.Height); g.DrawImage(b, a.Width + gap, top, b.Width, b.Height);
                    g.DrawString("Mac", f, Brushes.White, 8, 6); g.DrawString("Windows", f, Brushes.White, a.Width + gap + 8, 6);
                    o.Save(outPng, ImageFormat.Png);
                }
            }
        }
    }
}
