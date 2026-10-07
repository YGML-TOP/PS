namespace Compositor.Adjust;

/// <summary>色阶作用的通道。索引即 <c>LevelsSettings.Ranges</c> 的下标。</summary>
public enum LevelsChannel
{
    /// <summary>合成 RGB 调整（<c>index == 0</c>）。</summary>
    Rgb,

    /// <summary>红通道（<c>index == 1</c>）。</summary>
    Red,

    /// <summary>绿通道（<c>index == 2</c>）。</summary>
    Green,

    /// <summary>蓝通道（<c>index == 3</c>）。</summary>
    Blue,
}

/// <summary>单通道的色阶范围。</summary>
/// <remarks>
/// 移植自 <c>Levels.swift:8-31</c> 的 <c>LevelRange</c>。
/// </remarks>
public sealed record LevelRange
{
    /// <summary>输入黑色的下限（含）。</summary>
    public const double BlackMin = 0.0;

    /// <summary>输入黑色的上限（含），留出 1 给白场。</summary>
    public const double BlackMax = 254.0;

    /// <summary>伽马的下限（含）。</summary>
    public const double GammaMin = 0.1;

    /// <summary>伽马的上限（含）。</summary>
    public const double GammaMax = 9.99;

    /// <summary>输入黑场，合法范围 <c>0…254</c>，默认 0。</summary>
    public double Black { get; init; }

    /// <summary>输入白场，合法范围 <c>(黑场+1)…255</c>，默认 255。<b>依赖 <see cref="Black"/></b>。</summary>
    public double White { get; init; } = 255.0;

    /// <summary>中间调伽马，合法范围 <c>0.1…9.99</c>，默认 1。</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>输出黑场，合法范围 <c>0…255</c>，默认 0。</summary>
    public double OutputBlack { get; init; }

    /// <summary>输出白场，合法范围 <c>0…255</c>，默认 255。</summary>
    public double OutputWhite { get; init; } = 255.0;

    /// <summary>该范围是否自洽合法。</summary>
    /// <remarks>
    /// <b>白场依赖黑场</b>：<c>white ∈ [black+1, 255]</c>。
    /// 因此校验必须按「先定黑、再定白」的顺序，不能各自独立判断。
    /// </remarks>
    public bool IsValid
    {
        get
        {
            if (!ExposureSettings.InRange(Black, BlackMin, BlackMax)) return false;
            if (!double.IsFinite(White) || White < Black + 1.0 || White > 255.0) return false;
            if (!ExposureSettings.InRange(Gamma, GammaMin, GammaMax)) return false;
            return ExposureSettings.InRange(OutputBlack, 0.0, 255.0)
                && ExposureSettings.InRange(OutputWhite, 0.0, 255.0);
        }
    }

    /// <summary>夹回合法范围：黑先夹，白再按夹好的黑夹。</summary>
    public LevelRange Normalized
    {
        get
        {
            double black = ExposureSettings.Clamp(Black, BlackMin, BlackMax, 0.0);
            return this with
            {
                Black = black,
                White = ExposureSettings.Clamp(White, black + 1.0, 255.0, 255.0),
                Gamma = ExposureSettings.Clamp(Gamma, GammaMin, GammaMax, 1.0),
                OutputBlack = ExposureSettings.Clamp(OutputBlack, 0.0, 255.0, 0.0),
                OutputWhite = ExposureSettings.Clamp(OutputWhite, 0.0, 255.0, 255.0),
            };
        }
    }

    /// <summary>是否等于出厂默认（恒等）。</summary>
    public bool IsIdentity => this == new LevelRange();

    /// <summary>对单个 0…1 的值应用本段色阶，返回 0…1。</summary>
    public double Apply(double value)
    {
        var s = Normalized;
        double input = Math.Min(1.0, Math.Max(0.0, (value * 255.0 - s.Black) / (s.White - s.Black)));
        return (s.OutputBlack + Math.Pow(input, 1.0 / s.Gamma) * (s.OutputWhite - s.OutputBlack)) / 255.0;
    }
}

/// <summary>色阶设置：4 个通道的范围，前 3 个是个别通道，第 4 个是合成 RGB 调整。</summary>
/// <remarks>
/// 移植自 <c>Levels.swift:32-44</c> 的 <c>LevelsSettings</c>。
/// </remarks>
public sealed record LevelsSettings
{
    /// <summary>通道范围数组的长度，<b>必须恰为 4</b>（R/G/B + 合成）。</summary>
    public const int ChannelCount = 4;

    /// <summary>通道范围，顺序为合成 RGB、红、绿、蓝。缺省为四段出厂值。</summary>
    public IReadOnlyList<LevelRange> Ranges { get; init; } = MakeDefaults();

    private static IReadOnlyList<LevelRange> MakeDefaults()
    {
        var list = new LevelRange[ChannelCount];
        for (int i = 0; i < ChannelCount; i++) list[i] = new LevelRange();
        return list;
    }

    /// <summary>四个范围是否齐备且各自合法。<b>任一元素为 <see langword="null"/> 即不合法。</b></summary>
    public bool IsValid
        => Ranges.Count == ChannelCount && Ranges.All(r => r is not null && r.IsValid);

    /// <summary>四段全部为出厂默认（恒等调整）。</summary>
    public bool IsIdentity
        => Ranges.Count == ChannelCount && Ranges.All(r => r is not null && r.IsIdentity);

    /// <summary>把每一段夹回合法范围。</summary>
    public LevelsSettings Normalized => this with
    {
        Ranges = Ranges.Select(r => r.Normalized).ToArray(),
    };

    /// <summary>
    /// 构造 768 项查找表（3×256），供 <c>LevelsPixels.Apply</c> 使用。
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>量纲是 0…1，不是 0…255。</b>见 <c>LevelsPixels.Apply</c> 的说明。</para>
    /// <para>每段为「个别通道调整后，再过一遍合成 RGB 调整」
    /// （Swift <c>Levels.swift:42</c>：<c>ranges[0].apply(ranges[i].apply(value))</c>）。</para>
    /// </remarks>
    public float[] BuildTable()
    {
        var normalized = Normalized;
        var t = new float[768];
        for (int channel = 0; channel < 3; channel++)
        {
            var individual = normalized.Ranges[channel + 1];
            var composite = normalized.Ranges[0];
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0;
                v = individual.Apply(v);
                v = composite.Apply(v);
                t[channel * 256 + i] = (float)Math.Min(1.0, Math.Max(0.0, v));
            }
        }
        return t;
    }
}

/// <summary>曲线上的一个控制点。</summary>
/// <remarks>移植自 <c>Curves.swift:3-6</c> 的 <c>CurvePoint</c>。</remarks>
/// <param name="X">横坐标，合法范围 0–255。</param>
/// <param name="Y">纵坐标，合法范围 0–255。</param>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>曲线的形状保持三次 Hermite 插值，段间不产生过冲。</summary>
/// <remarks>移植自 <c>Curves.swift:7-42</c> 的 <c>CurvesSettings</c>。</remarks>
public sealed record CurvesSettings
{
    /// <summary>每通道控制点数的下限（含）。</summary>
    public const int MinPointsPerChannel = 2;

    /// <summary>每通道控制点数的上限（含）。</summary>
    public const int MaxPointsPerChannel = 32;

    /// <summary>通道数，<b>必须恰为 4</b>（R/G/B + 合成）。</summary>
    public const int ChannelCount = 4;

    private static readonly CurvePoint[] DefaultChannel =
        [new(0, 0), new(255, 255)];

    /// <summary>四条通道的控制点序列，每条点数须在 2–32。</summary>
    public IReadOnlyList<IReadOnlyList<CurvePoint>> Channels { get; init; } = MakeDefaults();

    private static IReadOnlyList<IReadOnlyList<CurvePoint>> MakeDefaults()
    {
        var list = new IReadOnlyList<CurvePoint>[ChannelCount];
        for (int i = 0; i < ChannelCount; i++) list[i] = DefaultChannel;
        return list;
    }

    /// <summary>四条通道是否全部合法。</summary>
    /// <remarks>
    /// 每条通道须满足：点数 2–32；首点 x 恰为 0、末点 x 恰为 255；
    /// 全部 x/y 有限且落在 0–255；<b>x 严格递增</b>（分母不得为 0）。
    /// </remarks>
    public bool IsValid
        => Channels.Count == ChannelCount && Channels.All(IsChannelValid);

    private static bool IsChannelValid(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count is < MinPointsPerChannel or > MaxPointsPerChannel) return false;
        if (points[0].X != 0.0 || points[^1].X != 255.0) return false;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return false;
            if (p.X is < 0.0 or > 255.0 || p.Y is < 0.0 or > 255.0) return false;
            if (i > 0 && points[i - 1].X >= p.X) return false;
        }
        return true;
    }

    /// <summary>对 0–255 的输入求 0–255 的输出。</summary>
    /// <param name="x">输入值。</param>
    /// <param name="channel">通道下标，0 为合成 RGB。</param>
    public double Value(double x, int channel)
    {
        var p = Channels[channel];
        int n = p.Count;
        int i = 0;
        for (int k = n - 1; k >= 0; k--)
        {
            if (p[k].X <= x) { i = Math.Clamp(k, 0, n - 2); break; }
        }

        var d = new double[n - 1];
        for (int k = 0; k < n - 1; k++) d[k] = (p[k + 1].Y - p[k].Y) / (p[k + 1].X - p[k].X);

        double Slope(int j)
        {
            if (j == 0) return d[0];
            if (j == n - 1) return d[^1];
            if (d[j - 1] * d[j] <= 0) return 0.0;
            return 2.0 / (1.0 / d[j - 1] + 1.0 / d[j]);
        }

        double h = p[i + 1].X - p[i].X;
        double t = Math.Min(1.0, Math.Max(0.0, (x - p[i].X) / h));
        double y = (2 * t * t * t - 3 * t * t + 1) * p[i].Y
                 + (t * t * t - 2 * t * t + t) * h * Slope(i)
                 + (-2 * t * t * t + 3 * t * t) * p[i + 1].Y
                 + (t * t * t - t * t) * h * Slope(i + 1);
        return Math.Min(255.0, Math.Max(0.0, y));
    }

    /// <summary>
    /// 构造 768 项查找表（3×256），供 <c>LevelsPixels.Apply</c> 使用。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>量纲是 0…1，不是 0…255。</b>
    /// 与 Swift <c>Curves.swift:37</c> 一致：三个个别通道各自过自己的曲线，
    /// 合成通道（<c>channel: 0</c>）不参与本表——Mac 版把三条通道的表
    /// 各算一遍再串接，此处照抄同一形态。
    /// </remarks>
    public float[] BuildTable()
    {
        var t = new float[768];
        for (int channel = 1; channel <= 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                t[(channel - 1) * 256 + i] = (float)(Value(i, channel) / 255.0);
            }
        }
        return t;
    }
}
