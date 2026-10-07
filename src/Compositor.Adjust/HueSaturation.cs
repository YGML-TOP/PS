namespace Compositor.Adjust;

/// <summary>六个色域加 Master，对应 Photoshop 的 Cmd+U。</summary>
/// <remarks>移植自 <c>HueSaturation.swift:5-22</c> 的 <c>ColorRange</c>。</remarks>
public enum ColorRange
{
    /// <summary>主调整，作用于全部颜色。</summary>
    Master,

    /// <summary>红。</summary>
    Reds,

    /// <summary>黄。</summary>
    Yellows,

    /// <summary>绿。</summary>
    Greens,

    /// <summary>青。</summary>
    Cyans,

    /// <summary>蓝。</summary>
    Blues,

    /// <summary>洋红。</summary>
    Magentas,
}

/// <summary>
/// 一个色带，以度为单位，在 360 处环绕：<see cref="RangeStart"/>–<see cref="RangeEnd"/> 之间满强度，
/// 向 <see cref="FalloffStart"/> 与 <see cref="FalloffEnd"/> 线性淡出到 0。
/// </summary>
/// <remarks>移植自 <c>HueSaturation.swift:26-53</c> 的 <c>HueBand</c>。</remarks>
public sealed record HueBand
{
    /// <summary>淡出起点（度）。</summary>
    public double FalloffStart { get; init; }

    /// <summary>满强度起点（度）。</summary>
    public double RangeStart { get; init; }

    /// <summary>满强度终点（度）。</summary>
    public double RangeEnd { get; init; }

    /// <summary>淡出终点（度）。</summary>
    public double FalloffEnd { get; init; }

    /// <summary>四个手柄是否全部有限。<b>不要求落在 0–360</b>，色带按模 360 环绕。</summary>
    public bool IsValid => Handles.All(double.IsFinite);

    /// <summary>四个手柄，顺序为淡出起点、满强度起、满强度止、淡出终点。</summary>
    public double[] Handles => [FalloffStart, RangeStart, RangeEnd, FalloffEnd];

    /// <summary>从 <paramref name="from"/> 正向到 <paramref name="to"/> 的角度差，恒为 0–360。</summary>
    public static double Forward(double from, double to)
    {
        double delta = (to - from) % 360.0;
        return delta < 0 ? delta + 360.0 : delta;
    }

    /// <summary>本色带对某个色相的权重 0–1。</summary>
    /// <param name="hue">色相（度）。</param>
    public double WeightOf(double hue)
    {
        double span = Forward(FalloffStart, FalloffEnd);
        if (span <= 0) return 1.0;   // Master 覆盖一切。

        double position = Forward(FalloffStart, hue);
        if (position > span) return 0.0;

        double rampIn = Forward(FalloffStart, RangeStart);
        double plateauEnd = Forward(FalloffStart, RangeEnd);
        if (position < rampIn) return rampIn > 0 ? position / rampIn : 1.0;
        if (position <= plateauEnd) return 1.0;

        double rampOut = span - plateauEnd;
        return rampOut > 0 ? (span - position) / rampOut : 1.0;
    }

    /// <summary>Photoshop 的出厂色带：淡出起点、满强度起、满强度止、淡出终点（度）。</summary>
    public static HueBand DefaultFor(ColorRange range) => range switch
    {
        ColorRange.Master => new HueBand { FalloffStart = 0, RangeStart = 0, RangeEnd = 360, FalloffEnd = 360 },
        ColorRange.Reds => new HueBand { FalloffStart = 315, RangeStart = 345, RangeEnd = 15, FalloffEnd = 45 },
        ColorRange.Yellows => new HueBand { FalloffStart = 15, RangeStart = 45, RangeEnd = 75, FalloffEnd = 105 },
        ColorRange.Greens => new HueBand { FalloffStart = 75, RangeStart = 105, RangeEnd = 135, FalloffEnd = 165 },
        ColorRange.Cyans => new HueBand { FalloffStart = 135, RangeStart = 165, RangeEnd = 195, FalloffEnd = 225 },
        ColorRange.Blues => new HueBand { FalloffStart = 195, RangeStart = 225, RangeEnd = 255, FalloffEnd = 285 },
        ColorRange.Magentas => new HueBand { FalloffStart = 255, RangeStart = 285, RangeEnd = 315, FalloffEnd = 345 },
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };
}

/// <summary>单个色域的调整值。</summary>
/// <remarks>移植自 <c>HueSaturation.swift:162-166</c> 的 <c>RangeAdjustment</c>。</remarks>
public sealed record RangeAdjustment
{
    /// <summary>色相偏移，合法范围 <c>|值| ≤ 360</c>，默认 0。</summary>
    public double Hue { get; init; }

    /// <summary>饱和度偏移，合法范围 <c>|值| ≤ 100</c>，默认 0。</summary>
    public double Saturation { get; init; }

    /// <summary>明度偏移，合法范围 <c>|值| ≤ 100</c>，默认 0。</summary>
    public double Lightness { get; init; }

    /// <summary>是否等于全零（恒等）。</summary>
    public bool IsIdentity => Hue == 0.0 && Saturation == 0.0 && Lightness == 0.0;

    /// <summary>三个偏移是否全部合法。</summary>
    public bool IsValid
        => ExposureSettings.InRange(Hue, -360.0, 360.0)
        && ExposureSettings.InRange(Saturation, -100.0, 100.0)
        && ExposureSettings.InRange(Lightness, -100.0, 100.0);
}

/// <summary>
/// 色相/饱和度设置。色相 <c>−180…180</c>（着色时 0…360）、饱和度 <c>−100…100</c>（着色时 0…100）、
/// 明度 <c>−100…100</c>。每个色域各存一份；Master 作用于全部。
/// </summary>
/// <remarks>移植自 <c>HueSaturation.swift:170-214</c> 的 <c>HueSaturationSettings</c>。</remarks>
public sealed record HueSaturationSettings
{
    /// <summary>滑块当前编辑的色域。</summary>
    public ColorRange Range { get; init; } = ColorRange.Master;

    /// <summary>是否着色（把画面压成单色）。</summary>
    public bool Colorize { get; init; }

    /// <summary>是否把选中色域的调整<b>反作用于色带之外</b>的颜色。</summary>
    public bool InvertRange { get; init; }

    /// <summary>逐色域的调整值。未出现的色域按全零处理。</summary>
    public IReadOnlyDictionary<ColorRange, RangeAdjustment> Adjustments { get; init; }
        = new Dictionary<ColorRange, RangeAdjustment>();

    /// <summary>逐色域的色带。未出现的色域用 <see cref="HueBand.DefaultFor"/>。</summary>
    public IReadOnlyDictionary<ColorRange, HueBand> Bands { get; init; }
        = Enum.GetValues<ColorRange>().ToDictionary(r => r, HueBand.DefaultFor);

    /// <summary>Photoshop 在打开着色时的起点：色相 0、饱和度 25、明度 0。</summary>
    public static HueSaturationSettings ColorizeStart { get; } = new()
    {
        Colorize = true,
        Adjustments = new Dictionary<ColorRange, RangeAdjustment>
        {
            [ColorRange.Master] = new RangeAdjustment { Saturation = 25.0 },
        },
    };

    /// <summary>未着色且所有色域均为零值时，是恒等调整。</summary>
    public bool IsIdentity
        => !Colorize && Adjustments.Values.All(a => a.IsIdentity);

    /// <summary>所有色域的调整值与色带是否全部合法。</summary>
    public bool IsValid
        => Adjustments.Values.All(a => a.IsValid) && Bands.Values.All(b => b.IsValid);

    /// <summary>当前色域的调整值；未设置时为全零。</summary>
    public RangeAdjustment Current
        => Adjustments.TryGetValue(Range, out var a) ? a : new RangeAdjustment();

    /// <summary>当前色域的色带；未设置时用出厂值。</summary>
    public HueBand CurrentBand
        => Bands.TryGetValue(Range, out var b) ? b : HueBand.DefaultFor(Range);

    /// <summary>某色域对某色相的作用权重 0–1。</summary>
    /// <param name="colorRange">色域。</param>
    /// <param name="hue">色相（度）。</param>
    public double WeightOf(ColorRange colorRange, double hue)
    {
        if (colorRange == ColorRange.Master) return 1.0;
        var band = Bands.TryGetValue(colorRange, out var b) ? b : HueBand.DefaultFor(colorRange);
        double weight = band.WeightOf(hue);
        return InvertRange && colorRange == Range ? 1.0 - weight : weight;
    }
}
