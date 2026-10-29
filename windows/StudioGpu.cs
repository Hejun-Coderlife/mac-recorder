// Studio 合成的显卡版（D3D11，Vortice）：和 StudioCore.cs 里的 CPU 版算法一样（同一张白底阴影图、同样的取样位置和圆角），
// 只是每个像素交给显卡的像素着色器算；原画面直接收 NV12（解码器不用再转 BGRA），画完在显卡上转成 NV12 交给编码器。
// 没有真显卡（只有「Microsoft 基本显示适配器」/ 软件渲染）就不用它，回到 CPU 版；自测时 STUDIO_COMPOSE=gpu 可以强制用软件渲染（WARP）验证结果
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenRecorder
{
    sealed class GpuComposer : IComposer
    {
        const string Hlsl = @"
cbuffer C : register(b0)
{
    float2 canvas; float2 src;
    float z; float sx; float sy; float s;
    float cardL; float cardT; float cardW; float cardH;
    float ccx; float ccy; float r; float nv12;
    float bt601; float p0; float p1; float p2;
    float4 ov;    // 热点在画布上的位置 x、y，小图一个像素在画布上多大，透明度
    float4 ovs;   // 小图宽、高，热点 x、y（小图像素）
};
Texture2D<float4> bgT : register(t0);
Texture2D<float4> srcT : register(t1);
Texture2D<float> yT : register(t2);
Texture2D<float2> uvT : register(t3);
Texture2D<float4> spT : register(t4);
Texture2D<float4> compT : register(t5);
SamplerState lin : register(s0);    // 双线性、边缘外取边缘
SamplerState linB : register(s1);   // 双线性、边缘外透明

float4 VSFull(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 VSQuad(uint id : SV_VertexID) : SV_Position
{
    float x0 = ov.x - ovs.z * ov.z - 2, y0 = ov.y - ovs.w * ov.z - 2;
    float x1 = ov.x + (ovs.x - ovs.z) * ov.z + 2, y1 = ov.y + (ovs.y - ovs.w) * ov.z + 2;
    float2 p = float2((id & 1) ? x1 : x0, (id & 2) ? y1 : y0);
    return float4(p.x / canvas.x * 2 - 1, 1 - p.y / canvas.y * 2, 0, 1);
}

float3 SrcAt(float2 uv)
{
    if (nv12 > 0.5)
    {
        float Y = (yT.Sample(lin, uv) * 255 - 16) / 219;
        float2 c = (uvT.Sample(lin, uv) * 255 - 128) / 224;
        float3 rgb = bt601 > 0.5
            ? float3(Y + 1.402 * c.y, Y - 0.344136 * c.x - 0.714136 * c.y, Y + 1.772 * c.x)
            : float3(Y + 1.5748 * c.y, Y - 0.187324 * c.x - 0.468124 * c.y, Y + 1.8556 * c.x);
        return saturate(rgb);
    }
    return srcT.Sample(lin, uv).rgb;
}

// 白底阴影 → 圆角画面（同 CPU 版 Compositor.Compose）
float4 PSMain(float4 pos : SV_Position) : SV_Target
{
    float px = (pos.x - canvas.x * 0.5) / z + sx;
    float py = (pos.y - canvas.y * 0.5) / z + sy;
    float qx = abs(px - ccx) - (cardW * 0.5 - r), qy = abs(py - ccy) - (cardH * 0.5 - r);
    float dist = (qx > 0 && qy > 0) ? sqrt(qx * qx + qy * qy) - r : max(qx, qy) - r;
    float cov = 0.5 - dist * z;
    int2 bp = int2(clamp((int)px, 0, (int)canvas.x - 1), clamp((int)py, 0, (int)canvas.y - 1));
    float3 bg = bgT.Load(int3(bp, 0)).rgb;
    if (cov <= 0) return float4(bg, 1);
    float2 uv = float2((px - cardL) / (s * src.x), (py - cardT) / (s * src.y));
    return float4(lerp(bg, SrcAt(uv), saturate(cov)), 1);
}

// 波纹、鼠标：预乘透明，叠上去（同 CPU 版 Compositor.Draw）
float4 PSOverlay(float4 pos : SV_Position) : SV_Target
{
    float u = (pos.x - ov.x) / ov.z + ovs.z;
    float v = (pos.y - ov.y) / ov.z + ovs.w;
    float4 c = spT.Sample(linB, float2(u / ovs.x, v / ovs.y)) * ov.w;
    if (c.a <= 0.001) discard;
    return c;
}

// 转 NV12（BT.709、16-235），和 CPU 版交给 ffmpeg 转的口径一样
float PSY(float4 pos : SV_Position) : SV_Target
{
    float3 c = compT.Load(int3(int2(pos.xy), 0)).rgb;
    return (16 + 219 * dot(c, float3(0.2126, 0.7152, 0.0722))) / 255;
}

float2 PSUV(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy) * 2;
    float3 c = (compT.Load(int3(p, 0)).rgb + compT.Load(int3(p + int2(1, 0), 0)).rgb
              + compT.Load(int3(p + int2(0, 1), 0)).rgb + compT.Load(int3(p + int2(1, 1), 0)).rgb) * 0.25;
    float Y = dot(c, float3(0.2126, 0.7152, 0.0722));
    return float2(128 + 224 * (c.b - Y) / 1.8556, 128 + 224 * (c.r - Y) / 1.5748) / 255;
}
";

        [StructLayout(LayoutKind.Sequential)]
        struct Consts
        {
            public float cw, ch, w, h;
            public float z, sx, sy, s;
            public float cardL, cardT, cardW, cardH;
            public float ccx, ccy, r, nv12;
            public float bt601, p0, p1, p2;
            public float ox, oy, ok, oa;
            public float sw, sh, hx, hy;
        }

        public string Name { get; }
        public bool Gpu => true;
        public bool Nv12In { get; }
        public bool Nv12Out => true;

        readonly StudioLayout L;
        readonly int W, H, CW, CH;
        readonly ID3D11Device dev;
        readonly ID3D11DeviceContext ctx;
        readonly ID3D11VertexShader vsFull, vsQuad;
        readonly ID3D11PixelShader psMain, psOverlay, psY, psUV;
        readonly ID3D11SamplerState sLin, sBorder;
        readonly ID3D11BlendState opaque, premul;
        readonly ID3D11Buffer cb;
        readonly ID3D11Texture2D bgTex, srcTex, yTex, uvTex, compTex, yOut, uvOut, yStage, uvStage;
        readonly ID3D11ShaderResourceView bgSrv, srcSrv, ySrv, uvSrv, compSrv;
        readonly ID3D11RenderTargetView compRtv, yRtv, uvRtv;
        readonly Dictionary<Sprite, (ID3D11Texture2D tex, ID3D11ShaderResourceView srv)> sprites = new Dictionary<Sprite, (ID3D11Texture2D, ID3D11ShaderResourceView)>();
        Consts c;

        // 建得起来才返回；allowSoftware = 没有真显卡时也用软件渲染（自测）
        public static GpuComposer TryCreate(StudioLayout lay, Compositor cpu, VideoInfo info, bool allowSoftware)
        {
            return new GpuComposer(lay, cpu, info, allowSoftware);
        }

        GpuComposer(StudioLayout lay, Compositor cpu, VideoInfo info, bool allowSoftware)
        {
            L = lay; W = info.Width; H = info.Height; CW = (int)lay.cw; CH = (int)lay.ch;
            Nv12In = W % 2 == 0 && H % 2 == 0;

            // 挑一块真显卡（跳过软件适配器和「Microsoft 基本显示适配器」）
            IDXGIAdapter1 adapter = null; string adapterName = null;
            using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
            {
                for (int i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
                {
                    var d = a.Description1;
                    bool soft = (d.Flags & AdapterFlags.Software) != 0 || (d.VendorId == 0x1414 && d.DeviceId == 0x8c);
                    if (!soft) { adapter = a; adapterName = d.Description; break; }
                    a.Dispose();
                }
            }
            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            if (adapter != null)
            {
                var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out dev, out ctx);
                adapter.Dispose();
                if (hr.Failure) throw new Exception("D3D11CreateDevice failed " + hr);
                Name = "GPU " + adapterName.Trim();
            }
            else
            {
                if (!allowSoftware) throw new Exception("no hardware GPU");
                var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out dev, out ctx);
                if (hr.Failure) throw new Exception("WARP device failed " + hr);
                Name = "GPU WARP (software)";
            }

            vsFull = dev.CreateVertexShader(Compile("VSFull", "vs_4_0"));
            vsQuad = dev.CreateVertexShader(Compile("VSQuad", "vs_4_0"));
            psMain = dev.CreatePixelShader(Compile("PSMain", "ps_4_0"));
            psOverlay = dev.CreatePixelShader(Compile("PSOverlay", "ps_4_0"));
            psY = dev.CreatePixelShader(Compile("PSY", "ps_4_0"));
            psUV = dev.CreatePixelShader(Compile("PSUV", "ps_4_0"));

            sLin = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0));
            var transparent = new Color4(0, 0, 0, 0);
            sBorder = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Border, TextureAddressMode.Border, TextureAddressMode.Border, 0, 1, ComparisonFunction.Never, ref transparent, 0, 0));
            opaque = dev.CreateBlendState(BlendDescription.Opaque);
            var bd = BlendDescription.Opaque;
            bd.RenderTarget[0].BlendEnable = true;
            bd.RenderTarget[0].SourceBlend = Blend.One; bd.RenderTarget[0].DestinationBlend = Blend.InverseSourceAlpha; bd.RenderTarget[0].BlendOperation = BlendOperation.Add;
            bd.RenderTarget[0].SourceBlendAlpha = Blend.One; bd.RenderTarget[0].DestinationBlendAlpha = Blend.InverseSourceAlpha; bd.RenderTarget[0].BlendOperationAlpha = BlendOperation.Add;
            bd.RenderTarget[0].RenderTargetWriteMask = ColorWriteEnable.All;
            premul = dev.CreateBlendState(bd);
            cb = dev.CreateBuffer(new BufferDescription(Marshal.SizeOf<Consts>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));

            // 白底阴影：直接用 CPU 版算好的那张（BGRA 字节）
            bgTex = Tex(Format.B8G8R8A8_UNorm, CW, CH, BindFlags.ShaderResource);
            Upload(bgTex, cpu.Background, 0, CW * 4);
            bgSrv = dev.CreateShaderResourceView(bgTex, null);
            if (Nv12In)
            {
                yTex = Tex(Format.R8_UNorm, W, H, BindFlags.ShaderResource); ySrv = dev.CreateShaderResourceView(yTex, null);
                uvTex = Tex(Format.R8G8_UNorm, W / 2, H / 2, BindFlags.ShaderResource); uvSrv = dev.CreateShaderResourceView(uvTex, null);
            }
            else { srcTex = Tex(Format.B8G8R8A8_UNorm, W, H, BindFlags.ShaderResource); srcSrv = dev.CreateShaderResourceView(srcTex, null); }
            compTex = Tex(Format.R8G8B8A8_UNorm, CW, CH, BindFlags.ShaderResource | BindFlags.RenderTarget);
            compSrv = dev.CreateShaderResourceView(compTex, null); compRtv = dev.CreateRenderTargetView(compTex, null);
            yOut = Tex(Format.R8_UNorm, CW, CH, BindFlags.RenderTarget); yRtv = dev.CreateRenderTargetView(yOut, null);
            uvOut = Tex(Format.R8G8_UNorm, CW / 2, CH / 2, BindFlags.RenderTarget); uvRtv = dev.CreateRenderTargetView(uvOut, null);
            yStage = dev.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, CW, CH, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            uvStage = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8_UNorm, CW / 2, CH / 2, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));

            c = new Consts
            {
                cw = CW, ch = CH, w = W, h = H, s = (float)lay.s,
                cardL = (float)cpu.cardL, cardT = (float)cpu.cardT, cardW = (float)cpu.cardW, cardH = (float)cpu.cardH,
                ccx = (float)cpu.ccx, ccy = (float)cpu.ccy, r = (float)cpu.r, nv12 = Nv12In ? 1 : 0, bt601 = info.Matrix == "bt601" ? 1 : 0,
            };
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            ctx.PSSetSampler(0, sLin); ctx.PSSetSampler(1, sBorder);
            ctx.PSSetConstantBuffer(0, cb);
            ctx.VSSetConstantBuffer(0, cb);   // 波纹、鼠标的方块位置在顶点着色器里算
        }

        static byte[] Compile(string entry, string profile)
        {
            var hr = Compiler.Compile(Hlsl, entry, "studio.hlsl", profile, out Blob blob, out Blob err);
            if (hr.Failure || blob == null) throw new Exception("shader " + entry + ": " + (err != null ? Marshal.PtrToStringAnsi(err.BufferPointer) : hr.ToString()));
            var bytes = blob.AsBytes(); blob.Dispose(); err?.Dispose();
            return bytes;
        }

        ID3D11Texture2D Tex(Format f, int w, int h, BindFlags bind) =>
            dev.CreateTexture2D(new Texture2DDescription(f, w, h, 1, 1, bind, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));

        unsafe void Upload(ID3D11Texture2D tex, byte[] data, int offset, int pitch)
        {
            fixed (byte* p = data) ctx.UpdateSubresource(new MappedSubresource((IntPtr)(p + offset), pitch, 0), tex, 0);
        }

        // 小图（预乘透明的浮点 BGRA）→ 半精度浮点贴图，第一次用到时建
        unsafe ID3D11ShaderResourceView SpriteSrv(Sprite sp)
        {
            if (sprites.TryGetValue(sp, out var e)) return e.srv;
            var half = new ushort[sp.W * sp.H * 4];
            for (int i = 0; i < sp.W * sp.H; i++)
            {
                half[i * 4] = Half(sp.Px[i * 4 + 2]); half[i * 4 + 1] = Half(sp.Px[i * 4 + 1]); half[i * 4 + 2] = Half(sp.Px[i * 4]); half[i * 4 + 3] = Half(sp.Px[i * 4 + 3]);
            }
            var tex = Tex(Format.R16G16B16A16_Float, sp.W, sp.H, BindFlags.ShaderResource);
            fixed (ushort* p = half) ctx.UpdateSubresource(new MappedSubresource((IntPtr)p, sp.W * 8, 0), tex, 0);
            var srv = dev.CreateShaderResourceView(tex, null);
            sprites[sp] = (tex, srv);
            return srv;
        }

        static ushort Half(float f)
        {
            uint x = (uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
            uint sign = (x >> 16) & 0x8000; int exp = (int)((x >> 23) & 0xff) - 127 + 15; uint man = x & 0x7fffff;
            if (exp <= 0) return (ushort)sign;
            if (exp >= 31) return (ushort)(sign | 0x7c00);
            return (ushort)(sign | ((uint)exp << 10) | ((man + 0x1000) >> 13));
        }

        void SetConsts() => ctx.UpdateSubresource(ref c, cb, 0, 0, 0, null);

        public unsafe void Compose(byte[] src, FramePlan f, List<Compositor.Overlay> overlays, byte[] dst)
        {
            // 原画面传上去
            if (Nv12In) { Upload(yTex, src, 0, W); Upload(uvTex, src, W * H, W); }
            else Upload(srcTex, src, 0, W * 4);

            c.z = (float)f.z; c.sx = (float)f.sx; c.sy = (float)(L.ch - f.sy);
            SetConsts();
            ctx.OMSetRenderTargets(compRtv, null);
            ctx.RSSetViewport(0, 0, CW, CH, 0, 1);
            ctx.OMSetBlendState(opaque);
            ctx.VSSetShader(vsFull); ctx.PSSetShader(psMain);
            ctx.PSSetShaderResource(0, bgSrv);
            if (Nv12In) { ctx.PSSetShaderResource(2, ySrv); ctx.PSSetShaderResource(3, uvSrv); } else ctx.PSSetShaderResource(1, srcSrv);
            ctx.Draw(3, 0);

            // 波纹、鼠标（坐标换算同 CPU 版 Compositor.Draw）
            if (overlays != null && overlays.Count > 0)
            {
                ctx.OMSetBlendState(premul);
                ctx.VSSetShader(vsQuad); ctx.PSSetShader(psOverlay);
                double z = f.z, sy = L.ch - f.sy, s = L.s;
                foreach (var o in overlays)
                {
                    var sp = o.S;
                    c.ox = (float)((c.cardL + o.X * s - f.sx) * z + CW / 2.0);
                    c.oy = (float)((c.cardT + o.Y * s - sy) * z + CH / 2.0);
                    c.ok = (float)(o.K * s * z); c.oa = (float)o.Alpha;
                    c.sw = sp.W; c.sh = sp.H; c.hx = (float)sp.HotX; c.hy = (float)sp.HotY;
                    SetConsts();
                    ctx.PSSetShaderResource(4, SpriteSrv(sp));
                    ctx.Draw(4, 0);
                }
            }

            // 转 NV12
            ctx.OMSetBlendState(opaque);
            ctx.VSSetShader(vsFull);
            ctx.OMSetRenderTargets(yRtv, null);
            ctx.PSSetShaderResource(5, compSrv);
            ctx.PSSetShader(psY);
            ctx.Draw(3, 0);
            ctx.OMSetRenderTargets(uvRtv, null);
            ctx.RSSetViewport(0, 0, CW / 2, CH / 2, 0, 1);
            ctx.PSSetShader(psUV);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(5, null);   // 下一帧要往 compTex 里画，先解绑

            ctx.CopyResource(yStage, yOut);
            ctx.CopyResource(uvStage, uvOut);
            ReadBack(yStage, dst, 0, CW, CH);
            ReadBack(uvStage, dst, CW * CH, CW, CH / 2);
        }

        unsafe void ReadBack(ID3D11Texture2D stage, byte[] dst, int offset, int rowBytes, int rows)
        {
            var m = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                fixed (byte* d0 = dst)
                {
                    byte* s = (byte*)m.DataPointer, d = d0 + offset;
                    for (int y = 0; y < rows; y++) Buffer.MemoryCopy(s + (long)y * m.RowPitch, d + (long)y * rowBytes, rowBytes, rowBytes);
                }
            }
            finally { ctx.Unmap(stage, 0); }
        }

        public void Dispose()
        {
            foreach (var e in sprites.Values) { e.srv.Dispose(); e.tex.Dispose(); }
            foreach (var d in new IDisposable[] { bgSrv, srcSrv, ySrv, uvSrv, compSrv, compRtv, yRtv, uvRtv, bgTex, srcTex, yTex, uvTex, compTex, yOut, uvOut, yStage, uvStage,
                                                  cb, opaque, premul, sLin, sBorder, vsFull, vsQuad, psMain, psOverlay, psY, psUV, ctx, dev })
                d?.Dispose();
        }
    }
}
