// 玻璃透镜（玻璃文字标签、玻璃字幕共用）和「线性光」叠图：照 Mac 版 Overlays.swift 的 lens() / lensKernel / drawGlassChrome()。
// Mac 版的遮挡、标注、字幕都是 Core Image 合成的，Core Image 默认在线性光里调色、叠图（不是在 sRGB 数值上），
// 所以同样的「白 5%」「亮度 +0.035」在 Windows 上也要换到线性光里算，颜色才对得上（实测：蓝底 (0,13,255) 透过玻璃 → (72,74,255)）。
// 纯 C#（不碰 WinForms），Mac 上的 .NET 8 小工具也能跑
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ScreenRecorder
{
    static class Lin
    {
        static readonly float[] toLin = new float[256];
        static readonly byte[] toSrgb = new byte[4097];
        static Lin()
        {
            for (int i = 0; i < 256; i++) toLin[i] = (float)Decode(i / 255.0);
            for (int i = 0; i <= 4096; i++) toSrgb[i] = (byte)Math.Round(Encode(i / 4096.0) * 255);
        }
        public static double Decode(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        public static double Encode(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        public static float Of(byte b) => toLin[b];
        static readonly float[] toLinF = Enumerable.Range(0, 4097).Select(i => (float)Decode(i / 4096.0)).ToArray();
        public static float Of(float srgb01)
        {
            if (!(srgb01 > 0)) return 0;
            if (srgb01 >= 1) return 1;
            float f = srgb01 * 4096; int i = (int)f; float t = f - i;
            return toLinF[i] + (toLinF[i + 1] - toLinF[i]) * t;
        }
        // 线性光 → sRGB 字节（超出 0–1 的截掉，同 Core Image 最后输出）
        public static byte Byte(float lin)
        {
            if (!(lin > 0)) return 0;
            if (lin >= 1) return 255;
            float f = lin * 4096; int i = (int)f; float t = f - i;
            return i >= 4096 ? (byte)255 : (byte)Math.Round(toSrgb[i] + (toSrgb[i + 1] - toSrgb[i]) * t);
        }
    }

    static class Glass
    {
        // 圆角矩形（中心 cx, cy、半宽 hw、半高 hh、圆角 r）的有符号距离，外面为正
        public static double Sdf(double px, double py, double cx, double cy, double hw, double hh, double r)
        {
            double qx = Math.Abs(px - cx) - (hw - r), qy = Math.Abs(py - cy) - (hh - r);
            return Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - r;
        }

        // 标准正态分布函数（高斯模糊过的直边：离边 d、σ → 覆盖多少）
        public static double Phi(double x)
        {
            // Abramowitz–Stegun 7.1.26，误差 < 1.5e-7
            double z = Math.Abs(x) / Math.Sqrt(2), t = 1 / (1 + 0.3275911 * z);
            double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
            return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
        }

        // 一块玻璃透镜，直接改 img（BGRA，W×H，左上原点）的 (rx, ry, rw, rh) 这块：中间不变形，越靠边越往外取背景，背景线条到边上弯过去。
        // 照 Mac 版 lens()：edge = 短边 × 0.5、depth = 短边 × 0.4 × strength；取样 = p + n × depth × t²；之后饱和度 ×1.15、亮度 +0.035（深色 −0.06）；
        // 圆角遮罩边缘高斯 σ = max(0.4, 短边 × 0.006)。都在线性光里算
        // 结果写进 area（线性光、不截断，同 Core Image 的扩展范围：饱和度提高后可能是负数，叠了体积感再截），area 是画面 (ax, ay) 起 aw×ah 的一块（Load 读出来的）
        public static void Lens(byte[] img, int W, int H, int rx, int ry, int rw, int rh, double rad, double strength, bool dark, double alpha, float[] area, int ax, int ay, int aw, int ah)
        {
            double s = Math.Min(rw, rh);
            if (s <= 2) return;
            double edge = s * 0.5, depth = s * 0.4 * strength, bright = dark ? -0.06 : 0.035;
            rad = Math.Max(0, Math.Min(rad, s / 2));
            double cx = rx + rw / 2.0, cy = ry + rh / 2.0, hw = rw / 2.0, hh = rh / 2.0;
            double sig = Math.Sqrt(Math.Pow(Math.Max(0.4, s * 0.006), 2) + 1 / 12.0);   // 遮罩：系统圆角矩形本身约 1 像素的抗锯齿 + 高斯
            // 取样范围：这块往外 depth + 2，画面外按边缘延伸（同 clampedToExtent）；先换成线性光
            int m = (int)Math.Ceiling(depth) + 2;
            int ex0 = rx - m, ey0 = ry - m, ew = rw + 2 * m, eh = rh + 2 * m;
            var src = new float[ew * eh * 3];
            Parallel.For(0, eh, y =>
            {
                int sy = Math.Min(H - 1, Math.Max(0, ey0 + y));
                for (int x = 0; x < ew; x++)
                {
                    int sx = Math.Min(W - 1, Math.Max(0, ex0 + x)), i = (sy * W + sx) * 4, o = (y * ew + x) * 3;
                    src[o] = Lin.Of(img[i]); src[o + 1] = Lin.Of(img[i + 1]); src[o + 2] = Lin.Of(img[i + 2]);
                }
            });
            int x0 = Math.Max(Math.Max(0, rx), ax), y0 = Math.Max(Math.Max(0, ry), ay), x1 = Math.Min(Math.Min(W, rx + rw), ax + aw), y1 = Math.Min(Math.Min(H, ry + rh), ay + ah);
            Parallel.For(y0, y1, y =>
            {
                for (int x = x0; x < x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double qx = Math.Abs(px - cx) - (hw - rad), qy = Math.Abs(py - cy) - (hh - rad);
                    double sdf = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - rad;
                    double d = -sdf, sx = px, sy = py;
                    if (d > 0)
                    {
                        double nx, ny;
                        if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); nx = qx / l; ny = qy / l; }
                        else if (qx > qy) { nx = 1; ny = 0; } else { nx = 0; ny = 1; }
                        nx *= Math.Sign(px - cx); ny *= Math.Sign(py - cy);
                        double t = Math.Min(1, Math.Max(0, 1 - d / edge));
                        sx = px + nx * depth * t * t; sy = py + ny * depth * t * t;
                    }
                    // 双线性取样（像素中心在 .5）
                    double fx = sx - 0.5 - ex0, fy = sy - 0.5 - ey0;
                    fx = Math.Min(ew - 1.001, Math.Max(0, fx)); fy = Math.Min(eh - 1.001, Math.Max(0, fy));
                    int ix = (int)fx, iy = (int)fy; float tx = (float)(fx - ix), ty = (float)(fy - iy);
                    int a = (iy * ew + ix) * 3, b = a + 3, c = a + ew * 3, e = c + 3;
                    float B = (src[a] * (1 - tx) + src[b] * tx) * (1 - ty) + (src[c] * (1 - tx) + src[e] * tx) * ty;
                    float G = (src[a + 1] * (1 - tx) + src[b + 1] * tx) * (1 - ty) + (src[c + 1] * (1 - tx) + src[e + 1] * tx) * ty;
                    float R = (src[a + 2] * (1 - tx) + src[b + 2] * tx) * (1 - ty) + (src[c + 2] * (1 - tx) + src[e + 2] * tx) * ty;
                    // CIColorControls：先饱和度（按亮度插值）再加亮度
                    float lum = 0.2125f * R + 0.7154f * G + 0.0721f * B;
                    R = lum + 1.15f * (R - lum) + (float)bright; G = lum + 1.15f * (G - lum) + (float)bright; B = lum + 1.15f * (B - lum) + (float)bright;
                    int k = ((y - ay) * aw + (x - ax)) * 3, so = ((y - ey0) * ew + (x - ex0)) * 3;
                    float ob = src[so], og = src[so + 1], or = src[so + 2];
                    float mk = (float)(Phi(-sdf / sig) * alpha);
                    area[k] = ob + (B - ob) * mk; area[k + 1] = og + (G - og) * mk; area[k + 2] = or + (R - or) * mk;
                }
            });
        }

        // 玻璃的体积感，画进一张预乘 sRGB 小图（px = sw×sh×4，B G R A），body = 胶囊在小图里的位置（左上原点）、fontPx = 字号像素。
        // 照 drawGlassChrome：浮起的柔影（offset 往下 0.16px、blur 1.0px、黑 20% / 深 32%，随底色的透明度）、底色（白 5% / 灰 0.06 × 30%）、
        // 边缘内阴影（黑 16% / 35%，blur 0.35px，往下 0.04px）、上沿柔光（白 75% / 35%，blur 0.12px，往下 0.06px）。没有描边线。
        // 系统画阴影的 blur 按 2σ 算；裁到胶囊里画「外框减胶囊」那圈时，抗锯齿的边上会漏一点点黑（照样留着）
        public static void Chrome(float[] px, int sw, int sh, double bx, double by, double bw, double bh, double fontPx, bool dark)
        {
            double cx = bx + bw / 2, cy = by + bh / 2, hw = bw / 2, hh = bh / 2, r = bh / 2;
            double shA = dark ? 0.32 : 0.2, fillA = dark ? 0.3 : 0.05, fillC = dark ? 0.06 : 1;
            double s1 = fontPx * 1.0 / 2, o1 = fontPx * 0.16;
            double s2 = Math.Max(0.3, fontPx * 0.35 / 2), o2 = fontPx * 0.04, a2 = dark ? 0.35 : 0.16;
            double s3 = Math.Max(0.3, fontPx * 0.12 / 2), o3 = fontPx * 0.06, a3 = dark ? 0.35 : 0.75;
            Parallel.For(0, sh, y =>
            {
                for (int x = 0; x < sw; x++)
                {
                    double qx = x + 0.5, qy = y + 0.5;
                    double cov = Math.Min(1, Math.Max(0, 0.5 - Sdf(qx, qy, cx, cy, hw, hh, r)));
                    // 预乘：c = 颜色 × a（灰度，三个通道一样）
                    double C = 0, A = 0;
                    void Over(double col, double a) { C = col * a + C * (1 - a); A = a + A * (1 - a); }
                    Over(0, shA * fillA * Phi(-Sdf(qx, qy - o1, cx, cy, hw, hh, r) / s1));   // 浮起的影子（透过半透明的底也看得见）
                    Over(fillC, fillA * cov);
                    if (cov > 0)
                    {
                        Over(0, a2 * Phi(Sdf(qx, qy - o2, cx, cy, hw, hh, r) / s2) * cov);
                        Over(0, (1 - cov) * cov);
                        Over(1, a3 * Phi(Sdf(qx, qy - o3, cx, cy, hw, hh, r) / s3) * cov);
                        Over(0, (1 - cov) * cov);
                    }
                    int o = (y * sw + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = (float)C; px[o + 3] = (float)A;
                }
            });
        }

        // 画面的一块（ax, ay 起 aw×ah，超出画面的部分不用）换成线性光，B G R 三个通道
        public static float[] Load(byte[] img, int W, int H, int ax, int ay, int aw, int ah)
        {
            var a = new float[aw * ah * 3];
            Parallel.For(0, ah, y =>
            {
                int sy = ay + y; if (sy < 0 || sy >= H) return;
                for (int x = 0; x < aw; x++)
                {
                    int sx = ax + x; if (sx < 0 || sx >= W) continue;
                    int i = (sy * W + sx) * 4, o = (y * aw + x) * 3;
                    a[o] = Lin.Of(img[i]); a[o + 1] = Lin.Of(img[i + 1]); a[o + 2] = Lin.Of(img[i + 2]);
                }
            });
            return a;
        }
        public static void Store(float[] a, byte[] img, int W, int H, int ax, int ay, int aw, int ah)
        {
            Parallel.For(0, ah, y =>
            {
                int sy = ay + y; if (sy < 0 || sy >= H) return;
                for (int x = 0; x < aw; x++)
                {
                    int sx = ax + x; if (sx < 0 || sx >= W) continue;
                    int i = (sy * W + sx) * 4, o = (y * aw + x) * 3;
                    img[i] = Lin.Byte(a[o]); img[i + 1] = Lin.Byte(a[o + 1]); img[i + 2] = Lin.Byte(a[o + 2]);
                }
            });
        }
        // 预乘 sRGB 的小图（左上角在这块的 ox, oy）在线性光里叠上去（同 Core Image 的 composited(over:)），alpha = 整体透明度
        public static void Composite(float[] a, int aw, int ah, Sprite sp, int ox, int oy, double alpha)
        {
            float al = (float)alpha;
            int ys = Math.Max(0, -oy), ye = Math.Min(sp.H, ah - oy), xs = Math.Max(0, -ox), xe = Math.Min(sp.W, aw - ox);
            if (ys >= ye || xs >= xe) return;
            Parallel.For(ys, ye, y =>
            {
                for (int x = xs; x < xe; x++)
                {
                    int s = (y * sp.W + x) * 4; float sa = sp.Px[s + 3];
                    if (sa <= 0.0005f) continue;
                    float A = sa * al, inv = 1 / sa;
                    int d = ((oy + y) * aw + ox + x) * 3;
                    for (int c = 0; c < 3; c++) a[d + c] = Lin.Of(Math.Min(1f, sp.Px[s + c] * inv)) * A + a[d + c] * (1 - A);
                }
            });
        }
        public static void BlendLinear(byte[] img, int W, int H, Sprite sp, int x0, int y0, double alpha)
        {
            var a = Load(img, W, H, x0, y0, sp.W, sp.H);
            Composite(a, sp.W, sp.H, sp, 0, 0, alpha);
            Store(a, img, W, H, x0, y0, sp.W, sp.H);
        }
    }
}
