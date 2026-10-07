using Compositor.Core.Pixels;

namespace Compositor.Adjust;

/// <summary>
/// 调整层的像素分派：把 <see cref="LayerAdjustment"/> 的参数接到 Core 已直译的 C 函数上。
/// </summary>
/// <remarks>
/// <para><b>本类是「框架层」而非「算术层」</b>——像素数学全部来自
/// <c>Compositor.Core.Pixels.AdjustPixels</c>（直译自 <c>Rendering/AdjustPixels.c</c>），
/// 本类只负责：① 按 <see cref="AdjustmentKind"/> 选对函数 ② 把参数换算到 C 函数的量纲
/// ③ 校验 ④ 维护预乘不变量。</para>
/// <para>🔴 <b>预乘铁律</b>：输入输出均为预乘 RGBA8，alpha=0 的像素跳过不动。
/// C 函数内部自行反预乘／乘回，本类不额外干预。</para>
/// <para>🔴 <b>工作色彩空间 = 编码 sRGB</b>（铁律 3 裁决，见 <c>docs/color-space.md</c>）。
/// 唯一例外是曝光与 <c>CameraRaw</c> 的白平衡／曝光两步，在线性光域。</para>
/// </remarks>
public static class AdjustmentApplier
{
    /// <summary>
    /// 对一块预乘 RGBA8 像素应用一个调整层，原地修改。
    /// </summary>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数，须 ≥ <c>width*4</c>。</param>
    /// <param name="adjustment">调整层参数。</param>
    /// <param name="originX">文档坐标原点的 X 分量（颗粒需要，保证图案固定在文档空间）。</param>
    /// <param name="originY">文档坐标原点的 Y 分量。</param>
    /// <param name="unitsPerPixel">每像素对应的文档单位数，须 &gt; 0。</param>
    /// <exception cref="ArgumentException">参数非法（越界或非有限）。</exception>
    public static void Apply(
        Span<byte> rgba, int width, int height, int stride,
        LayerAdjustment adjustment,
        double originX = 0.0, double originY = 0.0, double unitsPerPixel = 1.0)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        if (width <= 0 || height <= 0) throw new ArgumentException("尺寸必须为正", nameof(width));
        if (stride < width * 4) throw new ArgumentException("stride 不足一行像素", nameof(stride));
        if (!double.IsFinite(unitsPerPixel) || unitsPerPixel <= 0)
        {
            throw new ArgumentException("unitsPerPixel 必须为正有限值", nameof(unitsPerPixel));
        }
        if (!adjustment.IsValid) throw new ArgumentException("调整层参数越界或非有限", nameof(adjustment));

        switch (adjustment.Kind)
        {
            case AdjustmentKind.Invert:
                ApplyInvert(rgba, width, height, stride);
                break;

            case AdjustmentKind.HueSaturation:
                ApplyHueSaturation(rgba, width, height, stride, adjustment.ResolvedHsv);
                break;

            case AdjustmentKind.Levels:
                AdjustPixels_Levels(rgba, width, height, stride, adjustment);
                break;

            case AdjustmentKind.Curves:
                AdjustPixels_Curves(rgba, width, height, stride, adjustment);
                break;

            case AdjustmentKind.Exposure:
                ApplyExposure(rgba, width, height, stride, adjustment.ResolvedExposure);
                break;

            case AdjustmentKind.GradientMap:
                AdjustPixels.GradientMap(rgba, width, height, stride, adjustment.ResolvedGradientMap.BuildTable());
                break;

            case AdjustmentKind.Grain:
                ApplyGrain(rgba, width, height, stride, adjustment.ResolvedGrain, originX, originY, unitsPerPixel);
                break;

            case AdjustmentKind.BlackWhite:
                ApplyBlackWhite(rgba, width, height, stride, adjustment.ResolvedBlackWhite);
                break;

            case AdjustmentKind.ColorBalance:
                ApplyColorBalance(rgba, width, height, stride, adjustment.ResolvedColorBalance);
                break;

            case AdjustmentKind.GaussianBlur:
            case AdjustmentKind.MotionBlur:
            case AdjustmentKind.AddNoise:
                throw new NotSupportedException(
                    $"{adjustment.Kind} 需要采样邻域像素，由 Canvas/合成器提供前后缓冲；" +
                    "本类只处理逐像素调整。见 docs/ 下的分派说明。");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(adjustment), adjustment.Kind, "未实现的调整层种类。");
        }
    }

    // ── 接到 Core C 函数的四种 ────────────────────────────────────────────────

    private static void AdjustPixels_Levels(Span<byte> rgba, int width, int height, int stride, LayerAdjustment a)
    {
        var levels = a.Levels ?? new LevelsSettings();
        LevelsPixels.Apply(rgba, width * height, levels.BuildTable());
    }

    private static void AdjustPixels_Curves(Span<byte> rgba, int width, int height, int stride, LayerAdjustment a)
    {
        var curves = a.Curves ?? new CurvesSettings();
        LevelsPixels.Apply(rgba, width * height, curves.BuildTable());
    }

    private static void ApplyExposure(Span<byte> rgba, int width, int height, int stride, ExposureSettings e)
    {
        // ExposureSettings.BuildTable 已在内部解码到线性、缩放、再编码回 sRGB，
        // 与 Swift ImageAdjustments.swift:54-63 的 table 同形；量纲 0…1。
        LevelsPixels.Apply(rgba, width * height, e.BuildTable());
    }

    private static void ApplyGrain(Span<byte> rgba, int width, int height, int stride,
        GrainSettings g, double originX, double originY, double unitsPerPixel)
    {
        if (g.Amount <= 0) return;   // Swift ImageAdjustments.swift:203 同样的短路。
        AdjustPixels.Grain(rgba, width, height, stride,
            g.Amount, g.Size, g.Roughness, g.Seed, originX, originY, unitsPerPixel);
    }

    private static void ApplyBlackWhite(Span<byte> rgba, int width, int height, int stride, BlackWhiteSettings b)
    {
        AdjustPixels.BlackWhite(rgba, width, height, stride,
            b.BuildWeights(), b.Tint ? 1 : 0, b.TintHue, b.TintSaturation / 100.0);
    }

    private static void ApplyColorBalance(Span<byte> rgba, int width, int height, int stride, ColorBalanceSettings c)
    {
        if (c.IsIdentity) return;   // Swift ImageAdjustments.swift:167 同样的短路。
        AdjustPixels.ColorBalance(rgba, width, height, stride,
            c.BuildShadows(), c.BuildMidtones(), c.BuildHighlights(), c.PreserveLuminosity ? 1 : 0);
    }

    // ── 自行实现（Mac 版也不在 C 层）────────────────────────────────────────

    /// <summary>
    /// 反相。预乘 RGBA 下每个颜色分量变成 <c>alpha − 颜色</c>，透明得以保留。
    /// </summary>
    /// <remarks>
    /// 移植自 <c>PixelInvert.swift:29-36</c> 的 <c>vImageMatrixMultiply_ARGB8888</c> 矩阵
    /// <c>[-256,0,0,0 / 0,-256,0,0 / 0,0,-256,0 / 256,256,256,256] ÷ 256</c>。
    /// Mac 版用 vImage 一次向量化过；此处按同一矩阵逐像素展开，<b>数学等价</b>。
    /// </remarks>
    private static void ApplyInvert(Span<byte> rgba, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                byte alpha = rgba[p + 3];
                // alpha − color，保持预乘不变量：结果仍 ≤ alpha。
                rgba[p] = (byte)(alpha - rgba[p]);
                rgba[p + 1] = (byte)(alpha - rgba[p + 1]);
                rgba[p + 2] = (byte)(alpha - rgba[p + 2]);
            }
        }
    }

    /// <summary>
    /// 色相/饱和度调整。逐色域加权后作用，Master 权重恒为 1。
    /// </summary>
    /// <remarks>
    /// 移植自 <c>HueSaturation.swift</c> 的 <c>HueSaturationFilter</c>。
    /// <para>🔴 <b>已知差异</b>：Core 的 <c>AdjustPixels.RgbToHsl</c> / <c>HslToRgb</c>
    /// 是 <c>private</c>，本类按同一数学自行展开（见 <c>docs/known-differences.md</c>）。
    /// 两者算法一致，但<b>没有共享同一份代码</b>——这是要向总管理报告的可见性缺口。</para>
    /// </remarks>
    private static void ApplyHueSaturation(Span<byte> rgba, int width, int height, int stride, HueSaturationSettings s)
    {
        if (s.IsIdentity) return;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                byte alpha = rgba[p + 3];
                if (alpha == 0) continue;

                // 反预乘到直通域 0…1。
                double r = rgba[p] * 255.0 / alpha / 255.0;
                double g = rgba[p + 1] * 255.0 / alpha / 255.0;
                double b = rgba[p + 2] * 255.0 / alpha / 255.0;

                RgbToHsl(r, g, b, out double h, out double sat, out double l);

                double hue = h * 360.0;
                double satOut = sat;
                double lightOut = l;

                // 逐色域加权累加。
                foreach (var (colorRange, adj) in s.Adjustments)
                {
                    if (adj.IsIdentity) continue;
                    double w = s.WeightOf(colorRange, hue);
                    if (w <= 0) continue;
                    satOut += adj.Saturation / 100.0 * w;
                    lightOut += adj.Lightness / 100.0 * w;
                }

                if (s.Colorize)
                {
                    // 着色：色相锁定为 Master 的色相偏移，饱和度/明度取该值。
                    var c = s.Adjustments.TryGetValue(ColorRange.Master, out var m) ? m : new RangeAdjustment();
                    hue = Hue.Rotate360(hue, c.Hue);
                    satOut = Math.Max(0.0, c.Saturation / 100.0);
                    lightOut = l + c.Lightness / 100.0;
                }
                else if (s.Adjustments.TryGetValue(ColorRange.Master, out var master))
                {
                    hue = Hue.Rotate360(hue, master.Hue);
                }

                satOut = Math.Clamp(satOut, 0.0, 1.0);
                lightOut = Math.Clamp(lightOut, 0.0, 1.0);

                HslToRgb(hue / 360.0, satOut, lightOut, ref r, ref g, ref b);

                // 乘回 alpha，恢复预乘不变量。
                rgba[p] = ClampToAlpha(r * alpha, alpha);
                rgba[p + 1] = ClampToAlpha(g * alpha, alpha);
                rgba[p + 2] = ClampToAlpha(b * alpha, alpha);
            }
        }
    }

    private static byte ClampToAlpha(double value, int alpha)
    {
        int v = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (v < 0) v = 0;
        if (v > alpha) v = alpha;
        return (byte)v;
    }

    /// <summary>直通 RGB(0…1) → HSL，<paramref name="h"/> 归一到 0…1。</summary>
    /// <remarks>与 <c>AdjustPixels.Curve.cs:60-72</c> 的 <c>rgb_to_hsl</c> 同一数学。</remarks>
    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        l = (maxc + minc) * 0.5;
        double d = maxc - minc;
        if (d < 1e-6) { h = 0; s = 0; return; }
        s = d / (1.0 - Math.Abs(2.0 * l - 1.0));
        if (maxc == r) h = (g - b) / d % 6.0;
        else if (maxc == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        h /= 6.0;
        if (h < 0) h += 1;
    }

    /// <summary>HSL → 直通 RGB(0…1)。</summary>
    /// <remarks>与 <c>AdjustPixels.Curve.cs:86-94</c> 的 <c>hsl_to_rgb</c> 同一数学。</remarks>
    private static void HslToRgb(double h, double s, double l, ref double r, ref double g, ref double b)
    {
        if (s <= 1e-6) { r = g = b = l; return; }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3.0);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3.0);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }
}

/// <summary>色相的模 360 运算。</summary>
internal static class Hue
{
    /// <summary>把色相加偏移后归一到 0…360。</summary>
    internal static double Rotate360(double hue, double delta)
    {
        double h = (hue + delta) % 360.0;
        return h < 0 ? h + 360.0 : h;
    }
}
