namespace Compositor.Adjust;

/// <summary>
/// 随调整层一起存储的直通 sRGB 颜色，每通道 0–1。
/// </summary>
/// <remarks>移植自 <c>ImageAdjustments.swift:20-33</c> 的 <c>AdjustmentColor</c>。</remarks>
public sealed record AdjustmentColor
{
    /// <summary>红通道，合法范围 0–1 且必须有限。</summary>
    public double Red { get; init; }

    /// <summary>绿通道，合法范围 0–1 且必须有限。</summary>
    public double Green { get; init; }

    /// <summary>蓝通道，合法范围 0–1 且必须有限。</summary>
    public double Blue { get; init; }

    /// <summary>黑色。三个通道均为 0。</summary>
    public static AdjustmentColor Black { get; } = new() { Red = 0, Green = 0, Blue = 0 };

    /// <summary>白色。三个通道均为 1。</summary>
    public static AdjustmentColor White { get; } = new() { Red = 1, Green = 1, Blue = 1 };

    /// <summary>三个通道是否全部有限且落在 0–1。</summary>
    public bool IsValid
        => InUnit(Red) && InUnit(Green) && InUnit(Blue);

    /// <summary>把越界或非有限的分量夹到 0–1，非有限者取 0。</summary>
    public AdjustmentColor Clamped => new()
    {
        Red = ClampUnit(Red),
        Green = ClampUnit(Green),
        Blue = ClampUnit(Blue),
    };

    internal static bool InUnit(double v) => double.IsFinite(v) && v is >= 0.0 and <= 1.0;

    internal static double ClampUnit(double v) => !double.IsFinite(v) ? 0.0 : Math.Clamp(v, 0.0, 1.0);
}

/// <summary>
/// 曝光：<c>exposure</c>（档）缩放线性光、<c>offset</c> 平移，再由 <c>gamma</c> 校正弯曲结果。
/// 同一条曲线作用于每个通道，alpha 保持不变。
/// </summary>
/// <remarks>
/// 移植自 <c>ImageAdjustments.swift:37-71</c> 的 <c>ExposureSettings</c>。
/// </remarks>
public sealed record ExposureSettings
{
    /// <summary>曝光档位的下限（含）。</summary>
    public const double ExposureMin = -20.0;

    /// <summary>曝光档位的上限（含）。</summary>
    public const double ExposureMax = 20.0;

    /// <summary>线性光偏移的下限（含）。</summary>
    public const double OffsetMin = -0.5;

    /// <summary>线性光偏移的上限（含）。</summary>
    public const double OffsetMax = 0.5;

    /// <summary>伽马的下限（含）。</summary>
    public const double GammaMin = 0.01;

    /// <summary>伽马的上限（含）。</summary>
    public const double GammaMax = 9.99;

    /// <summary>曝光档位，合法范围 <c>−20…20</c>，默认 0。</summary>
    public double Exposure { get; init; }

    /// <summary>在线性光中相加的偏移，合法范围 <c>−0.5…0.5</c>，默认 0。</summary>
    public double Offset { get; init; }

    /// <summary>伽马校正，合法范围 <c>0.01…9.99</c>，默认 1；大于 1 提亮中间调。</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>三个参数是否全部落在合法范围内。</summary>
    public bool IsValid
        => InRange(Exposure, ExposureMin, ExposureMax)
        && InRange(Offset, OffsetMin, OffsetMax)
        && InRange(Gamma, GammaMin, GammaMax);

    /// <summary>把越界或非有限的值夹回范围内，非有限者取该字段的默认值。</summary>
    public ExposureSettings Normalized => new()
    {
        Exposure = Clamp(Exposure, ExposureMin, ExposureMax, 0.0),
        Offset = Clamp(Offset, OffsetMin, OffsetMax, 0.0),
        Gamma = Clamp(Gamma, GammaMin, GammaMax, 1.0),
    };

    /// <summary>
    /// 构造 768 项查找表（3×256，R/G/B 各一段），供 <c>LevelsPixels.Apply</c> 使用。
    /// </summary>
    /// <remarks>
    /// <para>量纲是 <b>0…1 而非 0…255</b>——见 <c>LevelsPixels.Apply</c> 的说明。
    /// 传入 0…255 的表不是「效果更强」，是「全屏过曝」。</para>
    /// <para>🔴 <b>曝光在线性光域</b>：先把输入解码到线性，缩放与偏移后经伽马再编码回 sRGB。
    /// 这与 <c>docs/color-space.md</c> 的裁决一致（曝光档数在线性）。</para>
    /// </remarks>
    public float[] BuildTable()
    {
        var t = new float[768];
        double scale = Math.Pow(2.0, Exposure);
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                double encoded = i / 255.0;
                double linear = encoded <= 0.04045
                    ? encoded / 12.92
                    : Math.Pow((encoded + 0.055) / 1.055, 2.4);
                linear = Math.Pow(Math.Max(0.0, linear * scale + Offset), 1.0 / Gamma);
                double output = linear <= 0.0031308
                    ? linear * 12.92
                    : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
                t[channel * 256 + i] = (float)Math.Min(1.0, Math.Max(0.0, output));
            }
        }
        return t;
    }

    internal static bool InRange(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;

    internal static double Clamp(double v, double lo, double hi, double fallback)
        => !double.IsFinite(v) ? fallback : Math.Clamp(v, lo, hi);
}

/// <summary>
/// 渐变映射：像素亮度在 <c>shadows</c> 与 <c>highlights</c> 之间取色，<c>reversed</c> 时反向；alpha 保持不变。
/// </summary>
/// <remarks>
/// 移植自 <c>ImageAdjustments.swift:75-109</c> 的 <c>GradientMapSettings</c>。
/// 逐字插值在<b>编码 sRGB 域</b>进行（<c>color-space.md</c> §2.1 <c>adjust_gradient_map</c> 为编码 sRGB）。
/// </remarks>
public sealed record GradientMapSettings
{
    /// <summary>暗端颜色，默认纯黑。</summary>
    public AdjustmentColor Shadows { get; init; } = AdjustmentColor.Black;

    /// <summary>亮端颜色，默认纯白。</summary>
    public AdjustmentColor Highlights { get; init; } = AdjustmentColor.White;

    /// <summary>是否反向（暗端用 <see cref="Highlights"/>）。</summary>
    public bool Reversed { get; init; }

    /// <summary>两个端点颜色是否合法。</summary>
    public bool IsValid => Shadows.IsValid && Highlights.IsValid;

    /// <summary>把越界或非有限的分量夹回 0–1。</summary>
    public GradientMapSettings Normalized => this with
    {
        Shadows = Shadows.Clamped,
        Highlights = Highlights.Clamped,
    };

    /// <summary>按 <see cref="Reversed"/> 决定实际生效的暗端与亮端。</summary>
    public (AdjustmentColor Dark, AdjustmentColor Light) Ends
        => Reversed ? (Highlights, Shadows) : (Shadows, Highlights);

    /// <summary>
    /// 构造 768 项查找表（256 项 × RGB），供 <c>AdjustPixels.GradientMap</c> 使用。
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>布局必须是「每项连续 3 字节」</b>：C 函数按 <c>color = level * 3</c> 索引，
    /// 然后读 <c>table[color]</c> / <c>table[color+1]</c> / <c>table[color+2]</c>
    /// （<c>AdjustPixels.cs:74-78</c>）。<b>不是</b>「R 段 256、G 段 256、B 段 256」——
    /// 那样填表会读错通道，128 灰会被染成蓝色。</para>
    /// <para>逐字插值并 <c>rounded()</c> 后夹到 0–255，与 Swift <c>ImageAdjustments.swift:92-104</c> 一致。</para>
    /// </remarks>
    public byte[] BuildTable()
    {
        var (dark, light) = Ends;
        var table = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            table[i * 3] = Mix(dark.Red, light.Red, t);
            table[i * 3 + 1] = Mix(dark.Green, light.Green, t);
            table[i * 3 + 2] = Mix(dark.Blue, light.Blue, t);
        }
        return table;

        static byte Mix(double from, double to, double t)
        {
            double value = from + (to - from) * t;
            return (byte)Math.Clamp(Math.Round(value * 255.0, MidpointRounding.AwayFromZero), 0.0, 255.0);
        }
    }
}

/// <summary>
/// 黑白：不是去饱和，而是选择各色族变成多亮的灰。红 40%、黄 60% 是默认值，
/// 正是它让肤色与树叶在转换后仍可区分，而纯亮度转换会把它们压平。
/// </summary>
/// <remarks>移植自 <c>ImageAdjustments.swift:114-141</c> 的 <c>BlackWhiteSettings</c>。</remarks>
public sealed record BlackWhiteSettings
{
    /// <summary>六个色族权重的下限（含）。</summary>
    public const double RangeMin = -200.0;

    /// <summary>六个色族权重的上限（含）。</summary>
    public const double RangeMax = 300.0;

    /// <summary>红色权重，合法范围 <c>−200…300</c>，默认 40。</summary>
    public double Reds { get; init; } = 40.0;

    /// <summary>黄色权重，默认 60。</summary>
    public double Yellows { get; init; } = 60.0;

    /// <summary>绿色权重，默认 40。</summary>
    public double Greens { get; init; } = 40.0;

    /// <summary>青色权重，默认 60。</summary>
    public double Cyans { get; init; } = 60.0;

    /// <summary>蓝色权重，默认 20。</summary>
    public double Blues { get; init; } = 20.0;

    /// <summary>洋红权重，默认 80。</summary>
    public double Magentas { get; init; } = 80.0;

    /// <summary>是否给结果着色（做旧照片或蓝晒效果）。</summary>
    public bool Tint { get; init; }

    /// <summary>单色调色相（度），合法范围 <c>0…360</c>，默认 40。</summary>
    public double TintHue { get; init; } = 40.0;

    /// <summary>单色调饱和度，合法范围 <c>0…100</c>，默认 20。</summary>
    public double TintSaturation { get; init; } = 20.0;

    /// <summary>全部参数是否合法。</summary>
    public bool IsValid
        => AllWeights.All(v => ExposureSettings.InRange(v, RangeMin, RangeMax))
        && ExposureSettings.InRange(TintHue, 0.0, 360.0)
        && ExposureSettings.InRange(TintSaturation, 0.0, 100.0);

    private double[] AllWeights => [Reds, Yellows, Greens, Cyans, Blues, Magentas];

    /// <summary>
    /// 构造 6 项权重（已除以 100），顺序固定为红、黄、绿、青、蓝、洋红。
    /// </summary>
    /// <remarks>与 <c>AdjustPixels.BlackWhite</c> 的 <c>weights</c> 顺序严格一致（<c>AdjustPixels.cs:209-213</c>）。</remarks>
    public float[] BuildWeights()
    {
        var w = new float[6];
        double[] all = AllWeights;
        for (int i = 0; i < 6; i++) w[i] = (float)(all[i] / 100.0);
        return w;
    }
}

/// <summary>
/// 色彩平衡：阴影/中间调/高光三段各自向相对色轴的一端偏移。
/// 保持明度会把每个像素的亮度还原回去，因此暖色偏移不会顺带把画面提亮。
/// </summary>
/// <remarks>移植自 <c>ImageAdjustments.swift:146-176</c> 的 <c>ColorBalanceSettings</c>。</remarks>
public sealed record ColorBalanceSettings
{
    /// <summary>九个轴向偏移的范围下限（含）。</summary>
    public const double RangeMin = -100.0;

    /// <summary>九个轴向偏移的范围上限（含）。</summary>
    public const double RangeMax = 100.0;

    /// <summary>阴影段：青 ↔ 红。</summary>
    public double ShadowCyanRed { get; init; }

    /// <summary>阴影段：洋红 ↔ 绿。</summary>
    public double ShadowMagentaGreen { get; init; }

    /// <summary>阴影段：黄 ↔ 蓝。</summary>
    public double ShadowYellowBlue { get; init; }

    /// <summary>中间调段：青 ↔ 红。</summary>
    public double MidCyanRed { get; init; }

    /// <summary>中间调段：洋红 ↔ 绿。</summary>
    public double MidMagentaGreen { get; init; }

    /// <summary>中间调段：黄 ↔ 蓝。</summary>
    public double MidYellowBlue { get; init; }

    /// <summary>高光段：青 ↔ 红。</summary>
    public double HighlightCyanRed { get; init; }

    /// <summary>高光段：洋红 ↔ 绿。</summary>
    public double HighlightMagentaGreen { get; init; }

    /// <summary>高光段：黄 ↔ 蓝。</summary>
    public double HighlightYellowBlue { get; init; }

    /// <summary>是否保持明度，默认 <see langword="true"/>。</summary>
    public bool PreserveLuminosity { get; init; } = true;

    /// <summary>九个轴是否全部合法。</summary>
    public bool IsValid => All().All(v => ExposureSettings.InRange(v, RangeMin, RangeMax));

    /// <summary>九项轴值，顺序为阴影三段、中间调三段、高光三段。</summary>
    public double[] All() =>
    [
        ShadowCyanRed, ShadowMagentaGreen, ShadowYellowBlue,
        MidCyanRed, MidMagentaGreen, MidYellowBlue,
        HighlightCyanRed, HighlightMagentaGreen, HighlightYellowBlue,
    ];

    /// <summary>九项全为 0 时是恒等变换（<c>isIdentity</c>，可跳过计算）。</summary>
    public bool IsIdentity => All().All(v => v == 0.0);

    /// <summary>构造阴影段 3 项偏移（已除以 100）。</summary>
    public float[] BuildShadows() => [ToUnit(ShadowCyanRed), ToUnit(ShadowMagentaGreen), ToUnit(ShadowYellowBlue)];

    /// <summary>构造中间调段 3 项偏移（已除以 100）。</summary>
    public float[] BuildMidtones() => [ToUnit(MidCyanRed), ToUnit(MidMagentaGreen), ToUnit(MidYellowBlue)];

    /// <summary>构造高光段 3 项偏移（已除以 100）。</summary>
    public float[] BuildHighlights() => [ToUnit(HighlightCyanRed), ToUnit(HighlightMagentaGreen), ToUnit(HighlightYellowBlue)];

    private static float ToUnit(double v) => (float)(v / 100.0);
}

/// <summary>
/// 胶片颗粒：亮度噪点，中间调最强。图案由 <see cref="Seed"/> 固定在文档空间，
/// 因此画布平移或局部重绘时颗粒不会跑位。
/// </summary>
/// <remarks>移植自 <c>ImageAdjustments.swift:180-209</c> 的 <c>GrainSettings</c>。</remarks>
public sealed record GrainSettings
{
    /// <summary>强度的下限（含）。</summary>
    public const double AmountMin = 0.0;

    /// <summary>强度的上限（含）。</summary>
    public const double AmountMax = 100.0;

    /// <summary>颗粒尺寸（文档单位）的下限（含）。</summary>
    public const double SizeMin = 0.5;

    /// <summary>颗粒尺寸（文档单位）的上限（含）。</summary>
    public const double SizeMax = 20.0;

    /// <summary>粗糙度的下限（含）。</summary>
    public const double RoughnessMin = 0.0;

    /// <summary>粗糙度的上限（含）。</summary>
    public const double RoughnessMax = 100.0;

    /// <summary>强度，合法范围 <c>0…100</c>，默认 25。</summary>
    public double Amount { get; init; } = 25.0;

    /// <summary>颗粒尺寸，合法范围 <c>0.5…20</c> 文档单位，默认 1.5。</summary>
    public double Size { get; init; } = 1.5;

    /// <summary>粗糙度，合法范围 <c>0…100</c>，默认 50；数值越高细节越细碎。</summary>
    public double Roughness { get; init; } = 50.0;

    /// <summary>颗粒图案的种子，使图案在会话之间保持稳定。</summary>
    public uint Seed { get; init; }

    /// <summary>三项参数是否全部合法。</summary>
    public bool IsValid
        => ExposureSettings.InRange(Amount, AmountMin, AmountMax)
        && ExposureSettings.InRange(Size, SizeMin, SizeMax)
        && ExposureSettings.InRange(Roughness, RoughnessMin, RoughnessMax);

    /// <summary>把越界或非有限的值夹回范围内。</summary>
    public GrainSettings Normalized => new()
    {
        Amount = ExposureSettings.Clamp(Amount, AmountMin, AmountMax, 25.0),
        Size = ExposureSettings.Clamp(Size, SizeMin, SizeMax, 1.5),
        Roughness = ExposureSettings.Clamp(Roughness, RoughnessMin, RoughnessMax, 50.0),
        Seed = Seed,
    };
}
