using System.Runtime.InteropServices;
using System.Text;
using Frostbar.Core.Shell;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// The lens's GPU work (ADR-016, S31), on the shared device of a monitor (<see cref="SurfaceFactory"/>): a picture of
/// the screen into which a plate copies what it reads from the newest capture frame as it is drawn, a smooth pyramid
/// of it under it (drawn again only where the picture changed), and a pixel shader that draws a plate's backdrop from
/// them into a composition drawing surface. Near the rim it samples farther out along the rim's normal (what lies
/// beyond the edge bends into it), splits the colours a little there, frosts (the pyramid through a cubic B-spline)
/// and saturates. D3D11 through raw vtables, as SurfaceFactory.
/// Every use of the context holds the factory's <see cref="SurfaceFactory.Gpu"/>.
/// </summary>
internal sealed unsafe class LensGpu : IDisposable
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    static extern int D3DCompile(byte[] src, nuint size, string? name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    [StructLayout(LayoutKind.Sequential)]
    struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)]
    struct SamplerDesc { public int Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy; public int ComparisonFunc; public float B0, B1, B2, B3, MinLod, MaxLod; }
    [StructLayout(LayoutKind.Sequential)] struct Viewport { public float X, Y, W, H, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)] struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public float SizeX, SizeY, Radius, Bezel;
        public float OriginX, OriginY, SrcW, SrcH;
        public float Strength, Frost, Saturation, Dispersion;
        public float Lod, Magnify, Squeeze, Gain;
        public float Knee, Slope, Fresnel, FresnelWidth;
        public float Smear, Contrast, EnvOffset, Dark;
        public float Top, TopSlope, Contact, Roll;
        public float Specular, LinePx, Dither, Levels;
        public float RimBend, RimWidth, LightTurn, Inset;
        public float TintR, TintG, TintB, TintA;
        public float JoinBlend, OthersCount, Pad0, Pad1;
        public fixed float OthersR[8];
        public fixed float Others[32];
    }

    [StructLayout(LayoutKind.Sequential)] struct SrvDesc { public uint Format, ViewDimension, MostDetailedMip, MipLevels, Pad0, Pad1; }
    [StructLayout(LayoutKind.Sequential)] struct RtvDesc { public uint Format, ViewDimension, MipSlice, Pad0, Pad1; }

    const uint Bgra8 = 87 /*B8G8R8A8_UNORM*/, Rgba16F = 10 /*R16G16B16A16_FLOAT*/;

    /// <summary>The smooth pyramid's levels under the picture (levels 1 to 5: a half to a 32nd across), where the picture is large enough.</summary>
    const int PyramidLevels = 5;

    static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // Liquid Glass as Apple's pictures show it (Frostbar.Core's LensLook; the user's "B4", 2026-10-18; Lab mock liquid draws
    // the same per pixel on the CPU): a dome that magnifies and squeezes towards the rim, frost stretched along the rim
    // near it, light gathered (brighter, more vivid), dark glass holding down the bright (light glass raising the dark),
    // and the band inside the rim lit by what is around it.
    const string Hlsl = """
        cbuffer P : register(b0)
        {
            float2 Size; float Radius; float Bezel;
            float2 Origin; float2 SrcSize;
            float Strength; float Frost; float Saturation; float Dispersion;
            float Lod; float Magnify; float Squeeze; float Gain;
            float Knee; float Slope; float Fresnel; float FresnelWidth;
            float Smear; float Contrast; float EnvOffset; float Dark;
            float Top; float TopSlope; float Contact; float Roll;
            float Specular; float LinePx; float Dither; float Levels;
            float RimBend; float RimWidth; float LightTurn; float Inset;
            float4 TintColor;
            float JoinBlend; float OthersCount; float Pad0; float Pad1;
            float4 OthersR[2];
            float4 Others[8];
        };
        Texture2D Src : register(t0); // the picture of the screen (to a pass of the pyramid: the level above)
        Texture2D Pyr : register(t1); // its smooth pyramid: level l (1 and up) is Pyr's mip l - 1
        SamplerState S : register(s0);
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V vs(uint id : SV_VertexID)
        {
            V o;
            float2 t = float2((id << 1) & 2, id & 2);
            o.uv = t;
            o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }
        float rounded(float2 p, float2 b, float r)
        {
            float2 q = abs(p) - b + r;
            return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
        }
        // A level of the pyramid from the one above (Size: this level's, SrcSize: that one's): a [1 3 3 1] tent each way,
        // four bilinear taps 0.75 of its texels out. (Box mips kept a point's place within a texel: the frost of a
        // point of light changed shape with every pixel it moved, Lab mock frost, 2026-11-10.)
        float4 down(V i) : SV_Target
        {
            float2 uv = i.pos.xy / Size, o = 0.75 / SrcSize;
            return (Src.SampleLevel(S, uv - o, 0) + Src.SampleLevel(S, uv + float2(o.x, -o.y), 0)
                  + Src.SampleLevel(S, uv + float2(-o.x, o.y), 0) + Src.SampleLevel(S, uv + o, 0)) * 0.25;
        }
        // A level read through a cubic B-spline (four bilinear taps): smooth where bilinear shows the texels.
        float3 spline(float2 px, float l)
        {
            float2 size = max(1, floor(SrcSize * exp2(-l)));
            float2 st = px / SrcSize * size - 0.5;
            float2 i = floor(st), f = st - i, f2 = f * f, f3 = f2 * f;
            float2 w0 = (1 - f) * (1 - f) * (1 - f) / 6, w1 = (3 * f3 - 6 * f2 + 4) / 6, w3 = f3 / 6;
            float2 w2 = 1 - w0 - w1 - w3;
            float2 g[2] = { w0 + w1, w2 + w3 };
            float2 h[2] = { (i - 0.5 + w1 / g[0]) / size, (i + 1.5 + w3 / g[1]) / size };
            float3 acc = 0;
            [loop] for (int k = 0; k < 4; k++)
            {
                float2 uv = float2(h[k & 1].x, h[k >> 1].y);
                float3 c = l < 0.5 ? Src.SampleLevel(S, uv, 0).rgb : Pyr.SampleLevel(S, uv, l - 1).rgb;
                acc += c * (g[k & 1].x * g[k >> 1].y);
            }
            return acc;
        }
        // The variance (px²) a level through the spline spreads a point by: the tents above it, and the spline's third of a texel².
        float spreadOf(float l) { return 0.5833333 * exp2(2 * l) - 0.25; }
        static const float Five[5] = { 0.0625, 0.25, 0.375, 0.25, 0.0625 };
        // The frost (Core's LensLook.SigmaPerFrost; Lab's ShaderMirror, which must agree): what is behind spread as a
        // Gaussian of 0.6 × Frost pixels would, between the two levels of the pyramid whose spreads are either side of
        // it, by variance (under level 0's own spread: between the picture as it is and level 0 through the spline; at
        // 0 the picture as it is). Round, and longer along the rim by `along` (what is behind smeared along it): five
        // taps (1 4 6 4 1) a stride apart add the stride's square to the spread that way, no farther apart than 1.5
        // sigma (they stay one). In loops: written out, the shader took a second to compile.
        float3 frost(float2 px, float2 tg, float along)
        {
            float sigma = 0.6 * Frost, want = sigma * sigma;
            float stride = min(0.6 * sqrt(along * (along + 2 * Frost)), 1.5 * sigma);
            // The lower of the two levels (-1: the picture as it is) and how far towards the upper.
            float la = -1, f = saturate(want / 0.3333333);
            if (want > 0.3333333)
            {
                la = clamp(floor(0.5 * log2((want + 0.25) / 0.5833333)), 0, max(Levels - 1, 0));
                f = saturate((want - spreadOf(la)) / (spreadOf(la + 1) - spreadOf(la)));
            }
            int taps = stride < 0.5 ? 0 : 2;
            float3 acc = 0;
            [loop] for (int k = -taps; k <= taps; k++)
            {
                float2 q = px + tg * (stride * k);
                float3 two[2];
                [loop] for (int j = 0; j < 2; j++)
                {
                    float l = la + j;
                    if (l < 0) two[j] = Src.SampleLevel(S, q / SrcSize, 0).rgb;
                    else two[j] = spline(q, l);
                }
                acc += lerp(two[0], two[1], f) * (taps == 0 ? 1 : Five[k + 2]);
            }
            return acc;
        }
        static const float3 Only[3] = { float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1) };
        static const float3 Luma = float3(0.2126, 0.7152, 0.0722);
        // The rim's lines (Core's LensLook.LineAt and DarkAt, which must agree) and where across a pixel they are taken.
        static const float2 RimSamples[4] = { float2(0.125, 0.375), float2(-0.375, 0.125), float2(-0.125, -0.375), float2(0.375, -0.125) };
        float ridgeAt(float depth, float w, float c0)
        {
            float q = (c0 - depth) / (0.55 * w);
            return depth < c0 ? exp(-q * q) : exp(-(depth - c0) / w);
        }
        float darkAt(float depth) { return saturate((1.25 - depth) / 0.75); }
        float3 roll(float3 v) { return v <= 0.8 ? v : 0.8 + 0.2 * (1 - exp(-(v - 0.8) / 0.2)); }
        // The plate's shape (Core's LiquidJoin, which must agree): its rounded rectangle, Inset inside the drawn area;
        // joined with its neighbours' (their boxes in Others: centre and half size, their radii in OthersR) by a smooth
        // minimum of JoinBlend pixels: glass near glass runs together like drops of water (2026-11-10).
        float boxAt(float2 q, float2 centre, float2 halfSize, float rad)
        {
            return rounded(q - centre, halfSize, min(rad, min(halfSize.x, halfSize.y)));
        }
        float selfAt(float2 q)
        {
            float2 c = Size * 0.5;
            return boxAt(q, c, c - Inset, Radius);
        }
        float othersAt(float2 q)
        {
            float d = 1e9;
            [loop] for (int k = 0; k < (int)OthersCount; k++) d = min(d, boxAt(q, Others[k].xy, Others[k].zw, OthersR[k >> 2][k & 3]));
            return d;
        }
        // Where the shape is taken about a pixel: its middle, half a pixel each way (the rim's normal), and the rim's
        // lines' four points (RimSamples). In one loop: written out nine times, the shader took twice as long to compile.
        static const float2 ShapeAt[9] = { float2(0, 0), float2(0.5, 0), float2(-0.5, 0), float2(0, 0.5), float2(0, -0.5),
            float2(0.125, 0.375), float2(-0.375, 0.125), float2(-0.125, -0.375), float2(0.375, -0.125) };
        float4 ps(V i) : SV_Target
        {
            float2 p = i.uv * Size;
            float shapes[9];
            float mine = 1; // the pixel is nearer this plate than any neighbour it is joined with
            [loop] for (int t = 0; t < 9; t++)
            {
                float2 q = p + ShapeAt[t];
                float a = selfAt(q);
                if (OthersCount > 0.5)
                {
                    float b = othersAt(q);
                    float h = max(JoinBlend - abs(a - b), 0) / max(JoinBlend, 1e-3);
                    if (t == 0 && a > b) mine = 0;
                    a = min(a, b) - h * h * JoinBlend * 0.25;
                }
                shapes[t] = a;
            }
            float d = shapes[0];
            float depth = -d;
            float u = 1 - saturate(depth / Bezel);
            float2 g = float2(shapes[1] - shapes[2], shapes[3] - shapes[4]);
            float2 n = g / max(length(g), 1e-4);
            float2 tg = float2(-n.y, n.x);
            // A dome: what is behind read nearer the middle (larger), squeezed towards the rim, meeting what is outside
            // at the rim; in the rim's last fifth it reads a little beyond. Without a dome, the old bend at the rim.
            float bend;
            if (Magnify > 1)
                bend = Strength * pow(saturate((u - 0.8) / 0.2), 2) - Bezel * u * (1 - 1 / Magnify) * (1 - pow(u, Squeeze));
            else bend = Strength * u * u;
            // The rim's own bend (Core's LensLook.RimBendAt, which must agree): what is behind read farther in, most at the
            // edge. Its colours do not split (Apple's glass shows none there; split by the dome's 0.12 a bend of 24 DIPs
            // drew a blue line round the rim, the candidates' photos of 2026-11-10).
            float rim = 0;
            if (RimBend > 0 && RimWidth > 0)
            {
                float k = saturate(1 - depth / RimWidth);
                rim = RimBend * k * k;
            }
            float along = Smear * smoothstep(0.55, 1, u);
            float2 at = Origin + p;
            // The colours split along the dome's bend; under a quarter of a pixel of it nothing shows (one reading), coming in to half a pixel.
            float split = Dispersion * smoothstep(0.25, 0.5, abs(bend) * Dispersion);
            int parts = abs(bend) * split > 0.001 ? 3 : 1;
            float3 col = 0;
            [loop] for (int ch = 0; ch < parts; ch++)
            {
                float3 part = frost(at + n * (bend * (parts == 1 ? 1 : 1 + split * (1 - ch)) - rim), tg, along);
                col += parts == 1 ? part : part * Only[ch];
            }
            float l = dot(col, Luma);
            col = max(0, lerp(l.xxx, col, Saturation));
            col = 0.5 + (col - 0.5) * Contrast;
            // Rolled off above 0.8 by Roll (1; less on a plate part of the way in: at 0 what is behind is left as it is).
            float3 gained = max(0, col) * Gain;
            col = lerp(gained, roll(gained), Roll);
            // Dark glass holds the bright down (its hue kept); light glass raises the dark.
            float lc = max(dot(col, Luma), 1e-3);
            if (Dark > 0.5) { if (lc > Knee) col *= (Knee + (lc - Knee) * Slope) / lc; }
            else
            {
                if (lc < Knee) col += (Knee - (Knee - lc) * Slope) - lc;
                // And holds its brightest a little under white: over a white window it stays a surface.
                float lt = max(dot(col, Luma), 1e-3);
                if (lt > Top) col *= (Top + (lt - Top) * TopSlope) / lt;
            }
            // What lies just outside the rim (a level coarser): the band's light and the dark line's.
            float3 env = (Fresnel > 0 || Contact > 0) ? Pyr.SampleLevel(S, (at + n * EnvOffset) / SrcSize, min(Lod, max(Levels - 1, 0))).rgb : 0;
            // The band inside the rim, lit by what is around it (its hue at full brightness, half way to white), more on
            // the top and bottom rims (Apple's own glass: lit from above, the bottom catching it again).
            // Where the light falls from (Core's LensLook.TurnedY, which must agree): the rim's normal turned by LightTurn (0 at
            // rest: lit from above); a plate coming by its lens has its light travel round its rim.
            float turnS, turnC;
            sincos(LightTurn, turnS, turnC);
            float ny = n.x * turnS + n.y * turnC;
            float vert = min(1, abs(ny));
            if (Fresnel > 0)
            {
                float top = max(max(env.r, env.g), max(env.b, 0.05));
                float3 lightIn = 0.5 + 0.5 * env / top;
                float facing = pow(vert, 2.2);
                float band = Fresnel * exp(-max(0, depth - 1) / max(FresnelWidth, 1)) * saturate(depth - 0.5) * (0.55 + 0.45 * facing);
                col = lerp(col, lightIn, band);
            }
            // The rim as Apple's own glass shows it (Core's LensLook.Rim and AtRim, which must agree; HIG Materials'
            // regular glass measured, 2026-11-06): just inside the edge a bright line, what is there lifted towards white,
            // most on the top rim (0.82 of it on the bottom, 0.4 on the sides); the edge's own pixel a dark line, the
            // darker of the glass and what is outside it kept to ContactKeeps. Before, the edge was only lit by what is
            // around it, +10 of 255 over a dark wallpaper where Apple's is +117, and had no dark line.
            // The two lines are under a pixel wide: taken at four points across the pixel (Core's RimSamples), each made
            // as strong as one point gives it on a straight rim. At the middle alone they stepped a pixel at a time along
            // a rounded corner (2026-11-08, the user's "ギザギザを直す").
            if (Specular > 0 || Contact > 0)
            {
                float lit = 0.4 + (ny < 0 ? 0.6 : 0.42) * vert * vert * vert;
                float w = max(LinePx, 0.5), c0 = 1 + 0.5 * w;
                float ridge = 0, dark = 0, ridgeStraight = 0, darkStraight = 0; // not "line": a keyword in HLSL
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float ds = -shapes[5 + k];
                    ridge += ridgeAt(ds, w, c0);
                    dark += darkAt(ds);
                    ridgeStraight += ridgeAt(c0 + RimSamples[k].y, w, c0);
                    darkStraight += darkAt(0.5 + RimSamples[k].y);
                }
                col = lerp(col, 1, saturate(Specular * lit * min(1, ridge / ridgeStraight)));
                col = lerp(col, min(col, env) * 0.58, Contact * min(1, dark / darkStraight));
            }
            // Half an 8-bit level of noise either way (interleaved gradient noise): the frost's slow gradients on dark
            // glass do not step. None on a plate that is not there yet (Dither comes with its presence).
            col += (frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715)))) - 0.5) * Dither / 255;
            // Joined with its neighbours (Core's LensJoin): the plate has no clip of its own then, so the lens draws its
            // tint and its silhouette, the part of the joined shape nearer this plate than any neighbour (each draws its own).
            if (Inset > 0)
            {
                col = lerp(col, TintColor.rgb, TintColor.a);
                float alpha = saturate(0.5 - d) * mine;
                return float4(saturate(col) * alpha, alpha);
            }
            return float4(saturate(col), 1);
        }
        """;

    static Task<(byte[] vs, byte[] ps, byte[] down)>? _compiled;

    /// <summary>
    /// The shaders' bytecode, compiled once per process off the UI thread (d3dcompiler_47 ships with Windows 10 and
    /// later; the bytecode outlives devices). The pixel shader takes about 45 ms to compile (2026-10-18, Lab `lens
    /// compile`), on top of loading the compiler: done at start-up on the UI thread with the capture's start, the
    /// installed shell's Dock took 120 ms longer to make. UI thread (it is started there, the first time it is asked).
    /// </summary>
    public static Task<(byte[] vs, byte[] ps, byte[] down)> Compiled => _compiled ??= Task.Run(() =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var code = (Compile("vs", "vs_5_0"), Compile("ps", "ps_5_0"), Compile("down", "ps_5_0"));
        Infrastructure.Log.Debug($"lens: shaders compiled in {sw.Elapsed.TotalMilliseconds:0} ms");
        return code;
    });

    readonly SurfaceFactory _f;
    readonly IntPtr _vs, _ps, _down, _sampler, _constants;
    IntPtr _screen, _screenView;

    // The smooth pyramid under the picture (2026-11-10): half the picture's size and down, 16 bits a channel (8 bits
    // stepped the frost's slow gradients on dark glass, more so after the glass's gain). Each level is drawn from the
    // one above (`down`), only where the picture changed since.
    IntPtr _pyramid, _pyramidView;
    readonly IntPtr[] _levelView = new IntPtr[PyramidLevels], _levelTarget = new IntPtr[PyramidLevels];
    readonly int[] _levelW = new int[PyramidLevels], _levelH = new int[PyramidLevels];
    int _levels;
    readonly List<ScreenRect> _stale = [];
    bool _staleAll;

    /// <summary>The picture of the screen's size, in the monitor's pixels (0 × 0 before <see cref="Size"/>).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Something has been copied in since the picture was made (before that it is empty, and a lens drawn from it black).</summary>
    public bool Ready { get; private set; }

    /// <summary>The GPU's things on <paramref name="f"/>'s device. Only once <see cref="Compiled"/> has completed.</summary>
    public LensGpu(SurfaceFactory f)
    {
        _f = f;
        var (vsCode, psCode, downCode) = Compiled.Result;
        IntPtr device = f.Device;
        var dv = *(void***)device;
        IntPtr shader;
        fixed (byte* p = vsCode) Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)dv[12])(device, p, (nuint)vsCode.Length, IntPtr.Zero, &shader)); // CreateVertexShader
        _vs = shader;
        fixed (byte* p = psCode) Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)dv[15])(device, p, (nuint)psCode.Length, IntPtr.Zero, &shader)); // CreatePixelShader
        _ps = shader;
        fixed (byte* p = downCode) Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)dv[15])(device, p, (nuint)downCode.Length, IntPtr.Zero, &shader)); // CreatePixelShader
        _down = shader;
        var sd = new SamplerDesc { Filter = 0x15 /*MIN_MAG_MIP_LINEAR*/, AddressU = 3, AddressV = 3, AddressW = 3 /*CLAMP*/, MaxAnisotropy = 1, ComparisonFunc = 1 /*NEVER*/, MaxLod = float.MaxValue };
        IntPtr sampler;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, SamplerDesc*, IntPtr*, int>)dv[23])(device, &sd, &sampler)); // CreateSamplerState
        _sampler = sampler;
        var bd = new BufferDesc { ByteWidth = (uint)sizeof(Constants), BindFlags = 0x4 /*CONSTANT_BUFFER*/ };
        IntPtr buffer;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, BufferDesc*, IntPtr, IntPtr*, int>)dv[3])(device, &bd, IntPtr.Zero, &buffer)); // CreateBuffer
        _constants = buffer;
    }

    static byte[] Compile(string entry, string target)
    {
        var src = Encoding.ASCII.GetBytes(Hlsl);
        int hr = D3DCompile(src, (nuint)src.Length, "lens", IntPtr.Zero, IntPtr.Zero, entry, target, 1u << 15 /*OPTIMIZATION_LEVEL3*/, 0, out IntPtr code, out IntPtr errors);
        if (hr < 0)
        {
            string msg = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors)) : "";
            if (errors != IntPtr.Zero) Marshal.Release(errors);
            throw new InvalidOperationException($"lens: D3DCompile {entry} 0x{hr:X8}: {msg}");
        }
        if (errors != IntPtr.Zero) Marshal.Release(errors);
        var bytes = new byte[BlobSize(code)];
        Marshal.Copy(BlobPointer(code), bytes, 0, bytes.Length);
        Marshal.Release(code);
        return bytes;
    }

    static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged<IntPtr, IntPtr>)(*(void***)blob)[3])(blob);
    static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged<IntPtr, nuint>)(*(void***)blob)[4])(blob);

    /// <summary>Makes the picture of the screen <paramref name="w"/> × <paramref name="h"/> (anew when the size changes; empty until copied into). Holds Gpu.</summary>
    public void Size(int w, int h)
    {
        if (_screen != IntPtr.Zero && w == Width && h == Height) return;
        ReleaseScreen();
        var td = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Bgra8, SampleCount = 1, BindFlags = 0x8 /*SHADER_RESOURCE*/ };
        IntPtr device = _f.Device, tex, view;
        var dv = *(void***)device;
        var createTexture = (delegate* unmanaged<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)dv[5]; // CreateTexture2D
        var createView = (delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)dv[7]; // CreateShaderResourceView
        var createTarget = (delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)dv[9]; // CreateRenderTargetView
        Marshal.ThrowExceptionForHR(createTexture(device, &td, IntPtr.Zero, &tex));
        _screen = tex;
        Marshal.ThrowExceptionForHR(createView(device, tex, null, &view));
        _screenView = view;
        Width = w;
        Height = h;
        try
        {
            // The pyramid: as many levels as halve to at least 2 pixels a side (five on any screen).
            int pw = Math.Max(1, w / 2), ph = Math.Max(1, h / 2);
            _levels = 0;
            for (int lw = pw, lh = ph; _levels < PyramidLevels && lw >= 2 && lh >= 2; lw /= 2, lh /= 2) { _levelW[_levels] = lw; _levelH[_levels] = lh; _levels++; }
            if (_levels > 0)
            {
                var pd = new Texture2DDesc { Width = (uint)pw, Height = (uint)ph, MipLevels = (uint)_levels, ArraySize = 1, Format = Rgba16F, SampleCount = 1, BindFlags = 0x8 | 0x20 /*SHADER_RESOURCE|RENDER_TARGET*/ };
                Marshal.ThrowExceptionForHR(createTexture(device, &pd, IntPtr.Zero, &tex));
                _pyramid = tex;
                Marshal.ThrowExceptionForHR(createView(device, tex, null, &view));
                _pyramidView = view;
                for (int k = 0; k < _levels; k++)
                {
                    var sd = new SrvDesc { Format = Rgba16F, ViewDimension = 4 /*TEXTURE2D*/, MostDetailedMip = (uint)k, MipLevels = 1 };
                    Marshal.ThrowExceptionForHR(createView(device, tex, &sd, &view));
                    _levelView[k] = view;
                    var rd = new RtvDesc { Format = Rgba16F, ViewDimension = 4 /*TEXTURE2D*/, MipSlice = (uint)k };
                    Marshal.ThrowExceptionForHR(createTarget(device, tex, &rd, &view));
                    _levelTarget[k] = view;
                }
            }
        }
        catch { ReleaseScreen(); throw; }
        _stale.Clear();
        _staleAll = true;
        Ready = false;
    }

    /// <summary>Copies the parts of <paramref name="frame"/> (a capture texture on this device) that changed, or all of it. Holds Gpu.</summary>
    public void Copy(IntPtr frame, IReadOnlyList<ScreenRect>? changed)
    {
        if (_screen == IntPtr.Zero) return;
        var cv = *(void***)_f.Context;
        var copy = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)cv[46]; // CopySubresourceRegion
        if (changed == null)
        {
            var all = new Box { Right = (uint)Width, Bottom = (uint)Height, Back = 1 };
            copy(_f.Context, _screen, 0, 0, 0, 0, frame, 0, &all);
            _staleAll = true;
        }
        else
        {
            foreach (var r in changed)
            {
                int l = Math.Max(0, r.Left), t = Math.Max(0, r.Top), rr = Math.Min(Width, r.Right), b = Math.Min(Height, r.Bottom);
                if (rr <= l || b <= t) continue;
                var box = new Box { Left = (uint)l, Top = (uint)t, Right = (uint)rr, Bottom = (uint)b, Back = 1 };
                copy(_f.Context, _screen, 0, (uint)l, (uint)t, 0, frame, 0, &box);
                if (_stale.Count < 16) _stale.Add(new ScreenRect(l, t, rr, b)); else _staleAll = true;
            }
        }
        Ready = true;
    }

    /// <summary>
    /// The pyramid's levels drawn again where the picture changed since they were (each from the level above, a texel
    /// more all round: a level's texel reads two of the one above either side). Before, GenerateMips made every level
    /// of the whole screen again at each plate's drawing. Holds Gpu.
    /// </summary>
    void MakePyramid()
    {
        if (_pyramid == IntPtr.Zero || !_staleAll && _stale.Count == 0) return;
        if (_staleAll) { _stale.Clear(); _stale.Add(new ScreenRect(0, 0, Width, Height)); }
        IntPtr context = _f.Context;
        var cv = *(void***)context;
        IntPtr samp = _sampler, cb = _constants, none = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, IntPtr, void>)cv[17])(context, IntPtr.Zero); // IASetInputLayout
        ((delegate* unmanaged<IntPtr, uint, void>)cv[24])(context, 4 /*TRIANGLELIST*/); // IASetPrimitiveTopology
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)cv[11])(context, _vs, null, 0); // VSSetShader
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)cv[9])(context, _down, null, 0); // PSSetShader
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[10])(context, 0, 1, &samp); // PSSetSamplers
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[16])(context, 0, 1, &cb); // PSSetConstantBuffers
        foreach (var r in _stale)
        {
            int l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom, sw = Width, sh = Height;
            for (int k = 0; k < _levels; k++)
            {
                int dw = _levelW[k], dh = _levelH[k];
                l = Math.Max(0, l / 2 - 1);
                t = Math.Max(0, t / 2 - 1);
                rr = Math.Min(dw, (rr + 1) / 2 + 1);
                b = Math.Min(dh, (b + 1) / 2 + 1);
                if (rr <= l || b <= t) break;
                var c = new Constants { SizeX = dw, SizeY = dh, SrcW = sw, SrcH = sh };
                ((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, Constants*, uint, uint, void>)cv[48])(context, _constants, 0, IntPtr.Zero, &c, 0, 0); // UpdateSubresource
                var vp = new Viewport { X = l, Y = t, W = rr - l, H = b - t, MaxDepth = 1 };
                IntPtr target = _levelTarget[k], from = k == 0 ? _screenView : _levelView[k - 1];
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[8])(context, 0, 1, &none); // PSSetShaderResources: the last level read is this one's target next
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)cv[33])(context, 1, &target, IntPtr.Zero); // OMSetRenderTargets
                ((delegate* unmanaged<IntPtr, uint, Viewport*, void>)cv[44])(context, 1, &vp); // RSSetViewports
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[8])(context, 0, 1, &from); // PSSetShaderResources
                ((delegate* unmanaged<IntPtr, uint, uint, void>)cv[13])(context, 3, 0); // Draw
                sw = dw;
                sh = dh;
            }
        }
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)cv[33])(context, 0, null, IntPtr.Zero); // OMSetRenderTargets
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[8])(context, 0, 1, &none); // PSSetShaderResources
        _stale.Clear();
        _staleAll = false;
    }

    // What the glass's tone is read from (item 4): the pyramid's level 3 (an eighth across, each texel a soft mean of
    // about 8 × 8 pixels) copied, a plate at a time, into a texture the CPU reads.
    const int ToneLevel = 3;
    IntPtr _toneStaging;
    int _toneW, _toneH;

    /// <summary>
    /// Puts what is behind a plate (<paramref name="plate"/>: pixels on the monitor) where <see cref="Brightness"/>
    /// reads it: the picture's eighth-size level, its mipmaps made first when stale. Holds Gpu.
    /// </summary>
    public void Sample(ScreenRect plate)
    {
        if (_screen == IntPtr.Zero || !Ready || _levels < ToneLevel) return;
        IntPtr context = _f.Context, device = _f.Device;
        var cv = *(void***)context;
        MakePyramid();
        int w = _levelW[ToneLevel - 1], h = _levelH[ToneLevel - 1];
        if (_toneStaging == IntPtr.Zero || _toneW != w || _toneH != h)
        {
            if (_toneStaging != IntPtr.Zero) Marshal.Release(_toneStaging);
            _toneStaging = IntPtr.Zero;
            var td = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Rgba16F, SampleCount = 1, Usage = 3 /*STAGING*/, CpuAccessFlags = 0x20000 /*READ*/ };
            IntPtr tex;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)(*(void***)device)[5])(device, &td, IntPtr.Zero, &tex)); // CreateTexture2D
            _toneStaging = tex;
            _toneW = w;
            _toneH = h;
        }
        var b = ToneBox(plate);
        if (b.Right <= b.Left || b.Bottom <= b.Top) return;
        var box = new Box { Left = (uint)b.Left, Top = (uint)b.Top, Right = (uint)b.Right, Bottom = (uint)b.Bottom, Back = 1 };
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)cv[46])(context, _toneStaging, 0, box.Left, box.Top, 0, _pyramid, ToneLevel - 1, &box); // CopySubresourceRegion
    }

    /// <summary>A plate's texels on the eighth-size level: those wholly inside it (the outer ones for a plate under 16 px).</summary>
    ScreenRect ToneBox(ScreenRect p)
    {
        int s = 1 << ToneLevel;
        int l = (p.Left + s - 1) / s, t = (p.Top + s - 1) / s, r = p.Right / s, b = p.Bottom / s;
        if (r <= l || b <= t) { l = p.Left / s; t = p.Top / s; r = (p.Right + s - 1) / s; b = (p.Bottom + s - 1) / s; }
        return new ScreenRect(Math.Clamp(l, 0, _toneW), Math.Clamp(t, 0, _toneH), Math.Clamp(r, 0, _toneW), Math.Clamp(b, 0, _toneH));
    }

    /// <summary>
    /// How bright what is behind each plate is (the mean of sRGB luma, 0 to 1; NaN where nothing was sampled), as sampled
    /// last (<see cref="Sample"/>). False while the GPU is still copying, when <paramref name="wait"/> is false (read
    /// again a moment later): reading then would hold the UI thread until it is done. Holds Gpu.
    /// </summary>
    public bool Brightness(IReadOnlyList<ScreenRect> plates, Span<double> luma, Span<double> deviation, bool wait)
    {
        luma.Fill(double.NaN);
        deviation.Fill(double.NaN);
        if (_toneStaging == IntPtr.Zero) return true;
        IntPtr context = _f.Context;
        var cv = *(void***)context;
        Mapped m;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, Mapped*, int>)cv[14])(context, _toneStaging, 0, 1 /*READ*/, wait ? 0u : 0x100000u /*DO_NOT_WAIT*/, &m); // Map
        if (hr == unchecked((int)0x887A000A)) return false; // DXGI_ERROR_WAS_STILL_DRAWING
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            for (int i = 0; i < plates.Count; i++)
            {
                var b = ToneBox(plates[i]);
                // The mean, and how far the brightness varies over the plate (its standard deviation: what a shadow's
                // depth goes by, Core's ShadowDepth).
                double sum = 0, squares = 0;
                int n = 0;
                for (int y = b.Top; y < b.Bottom; y++)
                {
                    Half* row = (Half*)((byte*)m.Data + (long)y * m.RowPitch);
                    for (int x = b.Left; x < b.Right; x++, n++)
                    {
                        double l = Math.Clamp(0.2126 * (float)row[x * 4] + 0.7152 * (float)row[x * 4 + 1] + 0.0722 * (float)row[x * 4 + 2], 0, 1); // R, G, B
                        sum += l;
                        squares += l * l;
                    }
                }
                if (n > 0)
                {
                    luma[i] = sum / n;
                    deviation[i] = Math.Sqrt(Math.Max(0, squares / n - luma[i] * luma[i]));
                }
            }
        }
        finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)cv[15])(context, _toneStaging, 0); } // Unmap
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

    /// <summary>
    /// Draws a plate's backdrop into the top left <paramref name="w"/> × <paramref name="h"/> pixels of
    /// <paramref name="surface"/>; <paramref name="x"/>, <paramref name="y"/>: the plate's top left on the monitor.
    /// False before the screen has been copied. Holds Gpu.
    /// </summary>
    public bool Draw(CompositionDrawingSurface surface, int w, int h, float x, float y, float radius, LensLook look, LensJoin? join = null)
    {
        if (_screenView == IntPtr.Zero || !Ready || w <= 0 || h <= 0) return false;
        IntPtr device = _f.Device;
        MakePyramid();
        IntPtr unk = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in IID_ICompositionDrawingSurfaceInterop, out IntPtr interop));
        try
        {
            var iv = *(void***)interop;
            IntPtr texture;
            POINT offset;
            Guid iid = IID_ID3D11Texture2D;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, POINT*, int>)iv[3])(interop, IntPtr.Zero, &iid, &texture, &offset)); // BeginDraw
            try
            {
                IntPtr rtv;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)(*(void***)device)[9])(device, texture, IntPtr.Zero, &rtv)); // CreateRenderTargetView
                try { Lens(rtv, offset.X, offset.Y, w, h, x, y, radius, look, join); }
                finally { Marshal.Release(rtv); }
            }
            finally
            {
                Marshal.Release(texture);
                Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, int>)iv[4])(interop)); // EndDraw
            }
        }
        finally { Marshal.Release(interop); }
        return true;
    }

    /// <summary>The lens of a plate drawn into <paramref name="target"/> at (<paramref name="left"/>, <paramref name="top"/>) of it. Holds Gpu.</summary>
    void Lens(IntPtr target, int left, int top, int w, int h, float x, float y, float radius, LensLook look, LensJoin? join = null)
    {
        IntPtr context = _f.Context;
        var cv = *(void***)context;
        var k = new Constants
        {
            SizeX = w, SizeY = h, Radius = radius, Bezel = Math.Max(1, look.Bezel),
            OriginX = x, OriginY = y, SrcW = Width, SrcH = Height,
            Strength = look.Strength, Frost = look.Frost, Saturation = look.Saturation, Dispersion = look.Dispersion, Lod = look.Lod,
            Magnify = look.Magnify, Squeeze = look.Squeeze, Gain = look.Gain > 0 ? look.Gain : 1,
            Knee = look.Knee, Slope = look.Slope, Fresnel = look.Fresnel, FresnelWidth = look.FresnelWidth,
            Smear = look.Smear, Contrast = look.Contrast > 0 ? look.Contrast : 1, EnvOffset = look.EnvOffset, Dark = look.Dark ? 1 : 0,
            Top = look.Top > 0 ? look.Top : 1, TopSlope = look.TopSlope > 0 ? look.TopSlope : 1, Contact = look.Contact,
            Roll = look.Roll, Specular = look.Specular, LinePx = look.LinePx, Dither = look.Dither, Levels = _levels,
            RimBend = look.RimBend, RimWidth = look.RimWidth, LightTurn = look.LightTurn,
        };
        // Joined with its neighbours (Core's LensJoin): the plate lies Margin inside the drawn area; its tint is drawn here.
        if (join != null)
        {
            k.Inset = join.Margin;
            (k.TintR, k.TintG, k.TintB, k.TintA) = join.Tint;
            k.JoinBlend = join.Blend;
            int n = Math.Min(join.Others.Count, LensJoin.Most);
            k.OthersCount = n;
            for (int i = 0; i < n; i++)
            {
                var o = join.Others[i];
                k.Others[4 * i] = o.X; k.Others[4 * i + 1] = o.Y; k.Others[4 * i + 2] = o.HalfW; k.Others[4 * i + 3] = o.HalfH;
                k.OthersR[i] = o.Radius;
            }
        }
        ((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, Constants*, uint, uint, void>)cv[48])(context, _constants, 0, IntPtr.Zero, &k, 0, 0); // UpdateSubresource
        var vp = new Viewport { X = left, Y = top, W = w, H = h, MaxDepth = 1 };
        IntPtr samp = _sampler, cb = _constants;
        var views = stackalloc IntPtr[2] { _screenView, _pyramidView };
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)cv[33])(context, 1, &target, IntPtr.Zero); // OMSetRenderTargets
        ((delegate* unmanaged<IntPtr, uint, Viewport*, void>)cv[44])(context, 1, &vp); // RSSetViewports
        ((delegate* unmanaged<IntPtr, IntPtr, void>)cv[17])(context, IntPtr.Zero); // IASetInputLayout
        ((delegate* unmanaged<IntPtr, uint, void>)cv[24])(context, 4 /*TRIANGLELIST*/); // IASetPrimitiveTopology
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)cv[11])(context, _vs, null, 0); // VSSetShader
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)cv[9])(context, _ps, null, 0); // PSSetShader
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[8])(context, 0, 2, views); // PSSetShaderResources
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[10])(context, 0, 1, &samp); // PSSetSamplers
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[16])(context, 0, 1, &cb); // PSSetConstantBuffers
        ((delegate* unmanaged<IntPtr, uint, uint, void>)cv[13])(context, 3, 0); // Draw
        // Let the target and the pictures go (before EndDraw, and before the next copy writes the picture).
        ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)cv[33])(context, 0, null, IntPtr.Zero); // OMSetRenderTargets
        views[0] = views[1] = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)cv[8])(context, 0, 2, views); // PSSetShaderResources
    }

    /// <summary>
    /// A plate's lens over a picture given as bytes (B, G, R, A; <paramref name="w"/> × <paramref name="h"/>), drawn by
    /// the GPU as the shell draws it and read back (the plate's pixels, B, G, R, A): what Lab compares its computed
    /// picture of the shader with (<c>Lab lens shell-render</c>), so that the candidates' pictures show what the screen
    /// will. No window and no capture. Holds Gpu itself.
    /// </summary>
    internal byte[] Offscreen(byte[] bgra, int w, int h, ScreenRect plate, float radius, LensLook look, LensJoin? join = null)
    {
        lock (_f.Gpu)
        {
            Size(w, h);
            IntPtr context = _f.Context, device = _f.Device;
            var cv = *(void***)context;
            var dv = *(void***)device;
            fixed (byte* p = bgra)
                ((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, byte*, uint, uint, void>)cv[48])(context, _screen, 0, IntPtr.Zero, p, (uint)(w * 4), 0); // UpdateSubresource
            _staleAll = true;
            Ready = true;
            MakePyramid();
            int pw = plate.Right - plate.Left, ph = plate.Bottom - plate.Top;
            var td = new Texture2DDesc { Width = (uint)pw, Height = (uint)ph, MipLevels = 1, ArraySize = 1, Format = Bgra8, SampleCount = 1, BindFlags = 0x20 /*RENDER_TARGET*/ };
            IntPtr drawn = IntPtr.Zero, staging = IntPtr.Zero, rtv = IntPtr.Zero;
            var createTexture = (delegate* unmanaged<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)dv[5]; // CreateTexture2D
            try
            {
                Marshal.ThrowExceptionForHR(createTexture(device, &td, IntPtr.Zero, &drawn));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)dv[9])(device, drawn, IntPtr.Zero, &rtv)); // CreateRenderTargetView
                Lens(rtv, 0, 0, pw, ph, plate.Left, plate.Top, radius, look, join);
                td.BindFlags = 0;
                td.Usage = 3; /*STAGING*/
                td.CpuAccessFlags = 0x20000; /*READ*/
                Marshal.ThrowExceptionForHR(createTexture(device, &td, IntPtr.Zero, &staging));
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)cv[47])(context, staging, drawn); // CopyResource
                Mapped m;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, Mapped*, int>)cv[14])(context, staging, 0, 1 /*READ*/, 0, &m)); // Map
                var result = new byte[pw * ph * 4];
                for (int row = 0; row < ph; row++)
                    Marshal.Copy(m.Data + row * (int)m.RowPitch, result, row * pw * 4, pw * 4);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)cv[15])(context, staging, 0); // Unmap
                return result;
            }
            finally
            {
                foreach (var p in new[] { rtv, staging, drawn })
                    if (p != IntPtr.Zero) Marshal.Release(p);
            }
        }
    }

    /// <summary>A drawing surface for a lens of up to <paramref name="w"/> × <paramref name="h"/> pixels.</summary>
    public CompositionDrawingSurface Surface(int w, int h) =>
        _f.Graphics.CreateDrawingSurface(new Windows.Foundation.Size(w, h), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);

    void ReleaseScreen()
    {
        for (int k = 0; k < PyramidLevels; k++)
        {
            if (_levelView[k] != IntPtr.Zero) { Marshal.Release(_levelView[k]); _levelView[k] = IntPtr.Zero; }
            if (_levelTarget[k] != IntPtr.Zero) { Marshal.Release(_levelTarget[k]); _levelTarget[k] = IntPtr.Zero; }
        }
        _levels = 0;
        if (_pyramidView != IntPtr.Zero) { Marshal.Release(_pyramidView); _pyramidView = IntPtr.Zero; }
        if (_pyramid != IntPtr.Zero) { Marshal.Release(_pyramid); _pyramid = IntPtr.Zero; }
        if (_screenView != IntPtr.Zero) { Marshal.Release(_screenView); _screenView = IntPtr.Zero; }
        if (_screen != IntPtr.Zero) { Marshal.Release(_screen); _screen = IntPtr.Zero; }
        Width = Height = 0;
    }

    public void Dispose()
    {
        ReleaseScreen();
        if (_toneStaging != IntPtr.Zero) { Marshal.Release(_toneStaging); _toneStaging = IntPtr.Zero; }
        foreach (var p in new[] { _constants, _sampler, _down, _ps, _vs })
            if (p != IntPtr.Zero) Marshal.Release(p);
    }
}
