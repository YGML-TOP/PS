namespace Compositor.Core.Pixels;

/// <summary>
/// Camera Raw 分部：色彩空间转换、影调分级（高光/阴影/白场/黑场）、基础调色、白平衡、剪切检查叠加。
/// </summary>
/// <remarks>
/// <para>直译自 <c>AdjustPixels.c</c> 的 L198–L349 与 L502–L522。
/// 与其余三个分部共享 <see cref="AdjustPixels"/> 的私有辅助（<c>CameraClamp</c> / <c>Rec709</c> /
/// <c>ScaleLuminance</c> / <c>WritePremultiplied</c>），因此本文件不能单独编译，必须与其余分部一同引用。</para>
/// <para><b>色彩空间</b>：本模块的曝光、对比度工作在<b>线性光</b>（Camera Raw 的做法），
/// 而其他模块（黑白、色彩平衡、色彩混合器）工作在 sRGB 编码值。
/// 混用二者是色彩类 bug 的高发地，见铁律 3。</para>
/// </remarks>
public static partial class AdjustPixels
{
    // ══════════════════════ 色彩空间与影调的共用原语 ══════════════════════

    /// <summary>把值夹到 0…1（原 C 的 <c>camera_clamp</c>，与夹 0…255 的 <c>clamp255</c> 是两个不同函数）。</summary>
    private static double CameraClamp(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    /// <summary>sRGB 编码值 → 线性光。</summary>
    private static double SrgbToLinear(double encoded)
    {
        if (encoded <= 0.04045) return encoded / 12.92;
        return Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    /// <summary>线性光 → sRGB 编码值。</summary>
    private static double LinearToSrgb(double linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 1;
        if (linear <= 0.0031308) return linear * 12.92;
        return 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
    }

    /// <summary>Rec. 709 亮度。</summary>
    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>
    /// 移动 r/g/b 使其 Rec. 709 亮度变成 <paramref name="target"/>，色相保持不变。
    /// 纯黑无法缩放，因此此时改为涂上该亮度的中性光。
    /// </summary>
    private static void ScaleLuminance(ref double r, ref double g, ref double b, double target)
    {
        target = CameraClamp(target);
        double y = Rec709(r, g, b);
        if (Math.Abs(target - y) < 1e-8) return;
        if (y < 1e-8)
        {
            if (target > y) r = g = b = target;
            return;
        }
        double scale = target / y;
        r = CameraClamp(r * scale);
        g = CameraClamp(g * scale);
        b = CameraClamp(b * scale);
    }

    private static double ToneHighlights(double y, double amount)
    {
        double t = CameraClamp((y - 0.5) / 0.5);
        double weight = t * t;
        if (amount >= 0) return CameraClamp(y + amount * weight * (1.0 - y));
        return CameraClamp(y + amount * weight * (y - 0.5));
    }

    private static double ToneShadows(double y, double amount)
    {
        double t = CameraClamp((0.5 - y) / 0.5);
        double weight = t * t;
        if (amount >= 0) return CameraClamp(y + amount * weight * (0.5 - y));
        return CameraClamp(y + amount * weight * y);
    }

    /// <summary>最亮的四分之一是白场：+1 把 0.875 映到 1，−1 把 0.75 以上全部压到 0.75。</summary>
    private static double ToneWhites(double y, double amount)
    {
        if (y <= 0.75) return y;
        return CameraClamp(0.75 + (y - 0.75) * (1.0 + amount));
    }

    /// <summary>最暗的四分之一是黑场：负值压向 0，正值抬向 0.25。</summary>
    private static double ToneBlacks(double y, double amount)
    {
        if (y >= 0.25) return y;
        return CameraClamp(0.25 + (y - 0.25) * (1.0 - amount));
    }

    /// <summary>自然饱和度 + 饱和度。自然饱和度会保护肤色区间（色相 10°–50°）不被过度加艳。</summary>
    private static void VibranceAndSaturation(ref double r, ref double g, ref double b, double vibrance, double saturation)
    {
        double lum = Rec709(r, g, b);
        double maxc = Math.Max(r, Math.Max(g, b));
        double minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        double sat = maxc <= 1e-8 ? 0 : chroma / maxc;

        double hue = 0;
        if (chroma > 1e-8)
        {
            // ⚠ 括号位置必须与原 C 一致：`60.0 * fmod((g-b)/chroma, 6.0)`。
            // 写成 `(60.0 * (g-b)/chroma) % 6.0` 数学上不同——前者落在 [0,360)，后者落在 [0,6)，色相会全错。
            if (r >= g && r >= b) hue = 60.0 * ((g - b) / chroma % 6.0);
            else if (g >= r && g >= b) hue = 60.0 * ((b - r) / chroma + 2.0);
            else hue = 60.0 * ((r - g) / chroma + 4.0);
            if (hue < 0) hue += 360.0;
        }

        double skin = 0;
        if (hue >= 10.0 && hue <= 50.0)
        {
            skin = hue <= 30.0 ? (hue - 10.0) / 20.0 : (50.0 - hue) / 20.0;
            skin *= CameraClamp((sat - 0.15) / 0.35);
        }

        double amount = vibrance * (1.0 - sat);
        if (vibrance > 0) amount *= 1.0 - 0.7 * skin;

        double factor = 1.0 + amount;
        r = CameraClamp(lum + (r - lum) * factor);
        g = CameraClamp(lum + (g - lum) * factor);
        b = CameraClamp(lum + (b - lum) * factor);

        lum = Rec709(r, g, b);
        factor = 1.0 + saturation;
        r = CameraClamp(lum + (r - lum) * factor);
        g = CameraClamp(lum + (g - lum) * factor);
        b = CameraClamp(lum + (b - lum) * factor);
    }

    /// <summary>
    /// 把直通 RGB 按 alpha 预乘后写回像素。<b>不写 alpha</b>。
    /// </summary>
    /// <remarks>
    /// <para>原 C 的 <c>static void write_premultiplied(uint8_t *p, double r, double g, double b, double alpha)</c>。
    /// C 里 <c>p</c> 是<b>指向当前像素的指针</b>——调用方在循环里已经做过 <c>rgba + o</c>，
    /// 所以函数体里写的是 <c>p[0] p[1] p[2]</c>，<b>不带像素起始下标参数</b>。</para>
    /// <para>C# 禁 <c>unsafe</c>（<c>Directory.Build.props</c> 的 <c>AllowUnsafeBlocks=false</c>），
    /// 没有指针可用，等价写法是「缓冲 + 像素起始下标」这对参数：<c>Span&lt;byte&gt; rgba</c> 对应
    /// C 的 <c>uint8_t *</c> 形参本身，<c>int p</c> 对应那个<b>已被加进指针的偏移量</b>。
    /// 两者合起来正好等价于 <c>&amp;rgba[p]</c>。</para>
    /// <para>⚠️ <b>签名不是随手定的</b>：C 版形参个数就是 5 个（指针算 1 个），
    /// 本版也必须是 5 个<b>颜色参数 + 缓冲 + 下标 = 6 个形参</b>，少一个就会把
    /// <c>alpha</c> 错绑到下标上——这正是初版把它写成 5 参时报的 13 处 CS1503。</para>
    /// <para>夹到 [0, alpha] 保证了「RGB ≤ alpha」这条预乘不变量，
    /// 因此<b>凡是要改颜色，就必须走本函数</b>，不要直接写 <c>rgba[p..p+2]</c>。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素缓冲，原地修改。</param>
    /// <param name="p">当前像素的<b>起始字节下标</b>（等价于 C 的指针 <c>p</c>）。</param>
    /// <param name="r">直通红（未预乘，0…1 或更宽）。</param>
    /// <param name="g">直通绿。</param>
    /// <param name="b">直通蓝。</param>
    /// <param name="alpha">该像素的覆盖度，即 <c>rgba[p+3]</c> 的值。</param>
    private static void WritePremultiplied(Span<byte> rgba, int p, double r, double g, double b, double alpha)
    {
        rgba[p] = (byte)Math.Min(alpha, Math.Max(0.0, CSemantics.Round(r * alpha)));
        rgba[p + 1] = (byte)Math.Min(alpha, Math.Max(0.0, CSemantics.Round(g * alpha)));
        rgba[p + 2] = (byte)Math.Min(alpha, Math.Max(0.0, CSemantics.Round(b * alpha)));
    }

    // ══════════════════════ 基础调色（Light & Color）══════════════════════

    /// <summary>
    /// Camera Raw 的「光效 + 颜色」组：白平衡 → 曝光 → 对比度 → 高光 → 阴影 → 白色 → 黑色 → 自然饱和度 → 饱和度。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw</c>，顺序不可调换：白平衡在曝光之前，
    /// 影调分级在对比度之后，四条分级依次收窄影响范围。
    /// <paramref name="clipping"/> 是检查视图：
    /// 0 = 正常出图，1 = 高光剪切视图（被切通道在黑底上点亮），2 = 阴影剪切视图（被切通道在白底上变暗）。
    /// alpha 保持不变。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="redGain">白平衡红通道增益（调用方按色温算好）。</param>
    /// <param name="greenGain">白平衡绿通道增益。</param>
    /// <param name="blueGain">白平衡蓝通道增益。</param>
    /// <param name="exposure">曝光，线性光的档数，范围 −5…5。</param>
    /// <param name="contrast">以中灰为轴的对比度，范围 −100…100。</param>
    /// <param name="highlights">高光，范围 −100…100。</param>
    /// <param name="shadows">阴影，范围 −100…100。</param>
    /// <param name="whites">白色，范围 −100…100。</param>
    /// <param name="blacks">黑色，范围 −100…100。</param>
    /// <param name="vibrance">自然饱和度，范围 −100…100。</param>
    /// <param name="saturation">饱和度，范围 −100…100。</param>
    /// <param name="clipping">0 正常出图 / 1 高光剪切视图 / 2 阴影剪切视图。</param>
    public static void CameraRaw(
        Span<byte> rgba, int width, int height, int stride,
        double redGain, double greenGain, double blueGain, double exposure, double contrast,
        double highlights, double shadows, double whites, double blacks,
        double vibrance, double saturation, int clipping)
    {
        double light = Math.Pow(2.0, exposure);
        double contrastScale = 1.0 + contrast / 100.0;
        double highlightAmount = highlights / 100.0;
        double shadowAmount = shadows / 100.0;
        double whiteAmount = whites / 100.0;
        double blackAmount = blacks / 100.0;
        double vibranceAmount = vibrance / 100.0;
        double saturationAmount = saturation / 100.0;

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;

                double r = Math.Min(255.0, rgba[p] * 255.0 / alpha) / 255.0;
                double g = Math.Min(255.0, rgba[p + 1] * 255.0 / alpha) / 255.0;
                double b = Math.Min(255.0, rgba[p + 2] * 255.0 / alpha) / 255.0;

                r = CameraClamp(SrgbToLinear(r) * redGain * light);
                g = CameraClamp(SrgbToLinear(g) * greenGain * light);
                b = CameraClamp(SrgbToLinear(b) * blueGain * light);

                r = CameraClamp(0.5 + (LinearToSrgb(r) - 0.5) * contrastScale);
                g = CameraClamp(0.5 + (LinearToSrgb(g) - 0.5) * contrastScale);
                b = CameraClamp(0.5 + (LinearToSrgb(b) - 0.5) * contrastScale);

                ScaleLuminance(ref r, ref g, ref b, ToneHighlights(Rec709(r, g, b), highlightAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneShadows(Rec709(r, g, b), shadowAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneWhites(Rec709(r, g, b), whiteAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneBlacks(Rec709(r, g, b), blackAmount));
                VibranceAndSaturation(ref r, ref g, ref b, vibranceAmount, saturationAmount);

                if (clipping == 1)
                {
                    bool rc = r >= 254.5 / 255.0, gc = g >= 254.5 / 255.0, bc = b >= 254.5 / 255.0;
                    r = rc ? 1 : 0;
                    g = gc ? 1 : 0;
                    b = bc ? 1 : 0;
                }
                else if (clipping == 2)
                {
                    bool rc = r <= 0.5 / 255.0, gc = g <= 0.5 / 255.0, bc = b <= 0.5 / 255.0;
                    if (rc || gc || bc)
                    {
                        r = rc ? 0 : 1;
                        g = gc ? 0 : 1;
                        b = bc ? 0 : 1;
                    }
                    else
                    {
                        r = g = b = 1;
                    }
                }

                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    /// <summary>
    /// 剪切检查叠加：被切到的高光压暗、阴影提亮，仅供预览。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_clip_overlay</c>。在<b>已分级</b>的图像之上叠加，
    /// 因此它必须排在 <see cref="CameraRaw"/> 之后调用，否则显示的剪切位置不是成片位置。
    /// alpha 保持不变。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="shadows">非 0 时标出被切到 0 的阴影（偏蓝）。</param>
    /// <param name="highlights">非 0 时标出被切到 255 的高光（偏红）。</param>
    public static void CameraRawClipOverlay(Span<byte> rgba, int width, int height, int stride, int shadows, int highlights)
    {
        if (shadows == 0 && highlights == 0) return;

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;

                double r = Math.Min(1.0, rgba[p] / alpha);
                double g = Math.Min(1.0, rgba[p + 1] / alpha);
                double b = Math.Min(1.0, rgba[p + 2] / alpha);

                if (shadows != 0 && (r <= 0.5 / 255.0 || g <= 0.5 / 255.0 || b <= 0.5 / 255.0))
                {
                    r *= 0.35; g *= 0.35; b = b * 0.35 + 0.65;
                }
                if (highlights != 0 && (r >= 254.5 / 255.0 || g >= 254.5 / 255.0 || b >= 254.5 / 255.0))
                {
                    r = r * 0.35 + 0.65; g *= 0.35; b *= 0.35;
                }

                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }
}