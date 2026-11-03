// 渲染成片的某一帧（对照 Mac 版 AI 控制的 render_frame）：和导出同一套——遮挡 / 标注画进原画面 → 合成（显卡或 CPU）→ 画布后期层 → 字幕（同一份 ASS 烧进去）。
// 给画面对照自测用（windows/tests/reference：同一份素材、同一份工程、同一秒，和 Mac 版的标准图比）
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScreenRecorder
{
    static class StudioStill
    {
        // srcT = 素材时间（秒）；compose = "cpu" | "gpu"（gpu 建不起来就报错）；maxWidth > 0 时按比例缩到这么宽；输出 PNG
        public static string Render(string dir, double srcT, string png, string compose, int maxWidth, Func<string, CursorShape, double, Sprite> loadCursor = null)
        {
            string raw = Path.Combine(dir, "raw.mp4");
            var info = Studio.Probe(raw);
            var track = Studio.LoadTrack(dir, info);
            var proj = StudioProject.Load(dir, track, info.Duration);
            var st = new StudioStyle();
            var map = new TimeMap(proj);
            var wtrack = map.Warp(track); var wproj = map.Warp(proj);
            double outDur = map.Out(info.Duration), outT = map.Out(srcT);
            var lay = new StudioLayout(track, proj.Look);
            var scene = new StudioScene(dir, wtrack, proj.Look, st, loadCursor, null, lay);
            int nAll = Math.Max(1, (int)(outDur * st.Fps));
            double outEnd = wproj.TrimEnd > proj.TrimStart ? wproj.TrimEnd : outDur;
            var plan = scene.Plan(wproj.Segments, outDur, nAll, (proj.TrimStart, outEnd));
            int i = Math.Min(nAll - 1, Math.Max(0, (int)Math.Round(outT * st.Fps)));
            int W = info.Width, H = info.Height, CW = (int)lay.cw, CH = (int)lay.ch;

            var cpu = scene.Cpu;
            IComposer comp = cpu;
            if (compose == "gpu")
            {
                var vi = info.With(W, H); vi.ForceBgra = true;
                comp = Studio.GpuFactory?.Invoke(lay, cpu, vi) ?? throw new Exception("GPU compositor not available");
            }
            string assFile = null;
            try
            {
                var src = Studio.DecodeFrame(raw, srcT, W, H, info.Matrix) ?? throw new Exception("decode failed");
                var overlay = OverlayLayer.Make(proj);
                var marked = new byte[src.Length];
                if (overlay != null && overlay.Apply(src, marked, W, H, srcT, wtrack.PixelsPerPoint)) src = marked;
                var dst = new byte[comp.Nv12Out ? CW * CH * 3 / 2 : CW * CH * 4];
                scene.Frame(comp, src, i, plan, dst);
                var capTrack = new CaptionTrack(proj.Captions, proj.CaptionStyle.Show, map, proj.TrimStart, proj.TrimEnd);
                using (var post = Canvas.Make(dir, track, proj, map, CW, CH, !capTrack.IsEmpty, () => cpu.PlainBackground()))
                    post?.Apply(dst, comp.Nv12Out, srcT, outT);

                // 字幕和导出一样用 ASS 烧（成片时间从修剪起点算 0）；最后转 RGB、缩到 maxWidth 存 PNG
                string vf = "";
                if (!capTrack.IsEmpty && CaptionLayer.Renderer == null)   // 同导出：有 GDI 时字幕在画布层里画好了
                {
                    bool win = Path.DirectorySeparatorChar == '\\';
                    string fontsDir = win ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts") : "/System/Library/Fonts";
                    assFile = Path.Combine(Path.GetTempPath(), "studio-still-" + Guid.NewGuid().ToString("N") + ".ass");
                    File.WriteAllText(assFile, CaptionAss.Build(capTrack, proj.CaptionStyle, proj.TrimStart, CW, CH, CaptionAss.FontName(fontsDir)), new UTF8Encoding(false));
                    vf = $"setpts={(outT - proj.TrimStart).ToString("0.######", CultureInfo.InvariantCulture)}/TB,ass=filename='{CaptionAss.FilterPath(assFile)}':fontsdir='{CaptionAss.FilterPath(fontsDir)}',";
                }
                string input = comp.Nv12Out
                    ? $"-f rawvideo -pix_fmt nv12 -s {CW}x{CH} -color_range tv -colorspace bt709 -i pipe:0"
                    : $"-f rawvideo -pix_fmt bgra -s {CW}x{CH} -i pipe:0";
                string toRgb = comp.Nv12Out ? "scale=in_color_matrix=bt709:in_range=tv:out_range=pc," : "";
                string scale = maxWidth > 0 && CW > maxWidth ? $"scale={maxWidth}:-2:flags=lanczos," : "";
                var enc = Studio.Start($"-v error -y {input} -vf \"{toRgb}{vf}{scale}format=rgb24\" -frames:v 1 \"{png}\"", true);
                var err = new StringBuilder();
                enc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); }; enc.BeginErrorReadLine();
                using (var stdin = enc.StandardInput.BaseStream) stdin.Write(dst, 0, dst.Length);
                enc.WaitForExit();
                if (enc.ExitCode != 0 || !File.Exists(png)) throw new Exception("png encode failed: " + err.ToString().Trim());
                return comp.Name;
            }
            finally
            {
                if (comp != cpu) comp.Dispose();
                if (assFile != null) try { File.Delete(assFile); } catch { }
            }
        }
    }
}
