using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Adjust;

/// <summary>
/// 一个调整层的全部参数。
/// </summary>
/// <remarks>
/// <para>移植自 <c>LayerAdjustment.swift:45-183</c> 的 <c>LayerAdjustment</c>。</para>
/// <para>🔴 <b>结构要点：扁平字段与嵌套结构是<b>并列</b>的</b>，不是嵌套关系。
/// 依据 <c>LayerAdjustment.swift:47-71</c>——<c>hsvSettings</c> / <c>exposureSettings</c> /
/// <c>blurRadius</c> 等全部挂在同一个 <c>LayerAdjustment</c> 上。
/// 这正是 <c>docs/project-format.md:29</c> 说的「该记录携带所有种类的设置，
/// 每一项都可缺省、缺省即恒等调整」。</para>
/// <para><b>为什么大量字段是可选的</b>：工程在这些调整出现之前就已保存过。
/// 缺省必须解出「与当时完全一致」的结果，否则旧工程一打开就变样。</para>
/// </remarks>
public sealed record LayerAdjustment
{
    /// <summary>调整层种类。</summary>
    public AdjustmentKind Kind { get; init; }

    // ── 扁平字段（对应 Swift 同名字段，与嵌套结构并列）─────────────────────────

    /// <summary>色相偏移，<c>|值| ≤ 360</c>，默认 0。<b>旧工程兼容字段</b>：新工程写 <see cref="HsvSettings"/>。</summary>
    public double Hue { get; init; }

    /// <summary>饱和度偏移，<c>|值| ≤ 100</c>，默认 0。</summary>
    public double Saturation { get; init; }

    /// <summary>明度偏移，<c>|值| ≤ 100</c>，默认 0。</summary>
    public double Lightness { get; init; }

    /// <summary>是否着色，默认 <see langword="false"/>。</summary>
    public bool Colorize { get; init; }

    /// <summary>高斯模糊半径，<c>0.1…250</c> 文档像素。<b>缺省 10</b>（见 <see cref="DefaultGaussianRadius"/>）。</summary>
    public double? BlurRadius { get; init; }

    /// <summary>动感模糊方向，<c>−90…90</c> 度，缺省 0。</summary>
    public double? MotionAngle { get; init; }

    /// <summary>动感模糊拖尾长度，<c>1…2000</c> 文档像素，缺省 10。</summary>
    public double? MotionDistance { get; init; }

    /// <summary>添加噪点强度，<c>0.1…400</c>，缺省 10。</summary>
    public double? NoiseAmount { get; init; }

    /// <summary>噪点是否用高斯分布，缺省 <see langword="false"/>。</summary>
    public bool? NoiseGaussian { get; init; }

    /// <summary>噪点是否单色（只改亮度），缺省 <see langword="false"/>。</summary>
    public bool? NoiseMonochromatic { get; init; }

    /// <summary>噪点种子，缺省 0。</summary>
    public uint? NoiseSeed { get; init; }

    // ── 嵌套结构（各为可选，缺省即恒等）─────────────────────────────────────

    /// <summary>色相/饱和度的完整设置。缺省时由扁平字段合成，见 <see cref="ResolvedHsv"/>。</summary>
    public HueSaturationSettings? HsvSettings { get; init; }

    /// <summary>色阶设置，缺省为四段出厂值。</summary>
    public LevelsSettings? Levels { get; init; }

    /// <summary>曲线设置，缺省为四条出厂曲线。</summary>
    public CurvesSettings? Curves { get; init; }

    /// <summary>曝光设置，缺省为恒等。</summary>
    public ExposureSettings? ExposureSettings { get; init; }

    /// <summary>渐变映射设置，缺省为黑→白。</summary>
    public GradientMapSettings? GradientMapSettings { get; init; }

    /// <summary>颗粒设置，缺省为 amount 25 / size 1.5 / roughness 50。</summary>
    public GrainSettings? GrainSettings { get; init; }

    /// <summary>黑白设置，缺省为 Photoshop 出厂权重。</summary>
    public BlackWhiteSettings? BlackWhiteSettings { get; init; }

    /// <summary>色彩平衡设置，缺省为九轴全零且保持明度。</summary>
    public ColorBalanceSettings? ColorBalanceSettings { get; init; }

    // ── 缺省值（照抄 Swift 的 `??` 右侧表达式）───────────────────────────────

    /// <summary>调整层的高斯模糊缺省半径是 <b>10</b>（<c>LayerAdjustment.swift:93</c> 的 <c>blurRadius ?? 10</c>）。</summary>
    public const double DefaultGaussianRadius = 10.0;

    /// <summary>🎯 <b>滤镜菜单的高斯模糊缺省半径是 1</b>（<c>Filters.swift:43</c> 的 <c>var radius: Double = 1</c>）。
    /// 与 <see cref="DefaultGaussianRadius"/> <b>刻意不同</b>，照抄源码不统一。</summary>
    public const double DefaultFilterRadius = 1.0;

    /// <summary>动感模糊方向缺省 0（<c>LayerAdjustment.swift:98</c>）。</summary>
    public const double DefaultMotionAngle = 0.0;

    /// <summary>动感模糊拖尾缺省 10（<c>LayerAdjustment.swift:102</c>）。</summary>
    public const double DefaultMotionDistance = 10.0;

    /// <summary>添加噪点强度缺省 10（<c>LayerAdjustment.swift:106</c>）。</summary>
    public const double DefaultNoiseAmount = 10.0;

    /// <summary>噪点高斯分布缺省 <see langword="false"/>（<c>LayerAdjustment.swift:110</c>）。</summary>
    public const bool DefaultNoiseGaussian = false;

    /// <summary>噪点单色缺省 <see langword="false"/>（<c>LayerAdjustment.swift:114</c>）。</summary>
    public const bool DefaultNoiseMonochromatic = false;

    /// <summary>噪点种子缺省 0（<c>LayerAdjustment.swift:118</c>）。</summary>
    public const uint DefaultNoiseSeed = 0;

    // ── 解析后的缺省值访问器（对应 Swift 的 `resolved…` 计算属性）──────────────

    /// <summary>色相/饱和度设置：缺省时用扁平字段合成（<c>LayerAdjustment.swift:53-55</c>）。</summary>
    public HueSaturationSettings ResolvedHsv
        => HsvSettings ?? new HueSaturationSettings
        {
            Colorize = Colorize,
            Adjustments = new Dictionary<ColorRange, RangeAdjustment>
            {
                [ColorRange.Master] = new RangeAdjustment
                {
                    Hue = Hue, Saturation = Saturation, Lightness = Lightness,
                },
            },
        };

    /// <summary>曝光设置，缺省为恒等。</summary>
    public ExposureSettings ResolvedExposure => ExposureSettings ?? new ExposureSettings();

    /// <summary>渐变映射设置，缺省为黑→白。</summary>
    public GradientMapSettings ResolvedGradientMap => GradientMapSettings ?? new GradientMapSettings();

    /// <summary>颗粒设置，缺省为出厂值。</summary>
    public GrainSettings ResolvedGrain => GrainSettings ?? new GrainSettings();

    /// <summary>黑白设置，缺省为出厂值。</summary>
    public BlackWhiteSettings ResolvedBlackWhite => BlackWhiteSettings ?? new BlackWhiteSettings();

    /// <summary>色彩平衡设置，缺省为恒等。</summary>
    public ColorBalanceSettings ResolvedColorBalance => ColorBalanceSettings ?? new ColorBalanceSettings();

    /// <summary>高斯模糊半径，缺省 <see cref="DefaultGaussianRadius"/>（<b>10</b>）。</summary>
    public double ResolvedGaussianRadius => BlurRadius ?? DefaultGaussianRadius;

    /// <summary>动感模糊方向，缺省 <see cref="DefaultMotionAngle"/>。</summary>
    public double ResolvedMotionAngle => MotionAngle ?? DefaultMotionAngle;

    /// <summary>动感模糊拖尾，缺省 <see cref="DefaultMotionDistance"/>。</summary>
    public double ResolvedMotionDistance => MotionDistance ?? DefaultMotionDistance;

    /// <summary>噪点强度，缺省 <see cref="DefaultNoiseAmount"/>。</summary>
    public double ResolvedNoiseAmount => NoiseAmount ?? DefaultNoiseAmount;

    /// <summary>噪点是否高斯分布，缺省 <see cref="DefaultNoiseGaussian"/>。</summary>
    public bool ResolvedNoiseGaussian => NoiseGaussian ?? DefaultNoiseGaussian;

    /// <summary>噪点是否单色，缺省 <see cref="DefaultNoiseMonochromatic"/>。</summary>
    public bool ResolvedNoiseMonochromatic => NoiseMonochromatic ?? DefaultNoiseMonochromatic;

    /// <summary>噪点种子，缺省 <see cref="DefaultNoiseSeed"/>。</summary>
    public uint ResolvedNoiseSeed => NoiseSeed ?? DefaultNoiseSeed;

    /// <summary>
    /// 局部重绘取样时需要的文档像素余量。
    /// </summary>
    /// <remarks>移植自 <c>LayerAdjustment.swift:121-127</c>。模糊类会采样到脏矩形之外。</remarks>
    public double SamplingMargin => Kind switch
    {
        AdjustmentKind.GaussianBlur => ResolvedGaussianRadius * 3.0 + 2.0,
        AdjustmentKind.MotionBlur => ResolvedMotionDistance / 2.0 + 2.0,
        _ => 0.0,
    };

    /// <summary>
    /// 全部参数是否合法。移植自 <c>LayerAdjustment.swift:128-141</c> 的 <c>isValid</c>。
    /// </summary>
    /// <remarks>
    /// <para>照抄要点：Swift 用 <c>&amp;&amp;</c> 串联<b>所有</b>子结构的校验，
    /// 无论当前 <see cref="Kind"/> 是什么。这是有意的——
    /// 调整层可以在两种 kind 之间切换，此前留下的设置仍须合法。</para>
    /// </remarks>
    public bool IsValid
        => ExposureSettings.InRange(Hue, -360.0, 360.0)
        && ExposureSettings.InRange(Saturation, -100.0, 100.0)
        && ExposureSettings.InRange(Lightness, -100.0, 100.0)
        && ResolvedHsv.Adjustments.Values.All(a => a.IsValid)
        && ResolvedHsv.Bands.Values.All(b => b.IsValid)
        && (Levels?.Ranges.Count == null || (Levels.Ranges.Count == LevelsSettings.ChannelCount
            && Levels.Ranges.All(r => r == r.Normalized)))
        && (Curves?.IsValid ?? true)
        && ResolvedExposure.IsValid
        && ResolvedGradientMap.IsValid
        && ResolvedGrain.IsValid
        && ResolvedBlackWhite.IsValid
        && ResolvedColorBalance.IsValid
        && ExposureSettings.InRange(ResolvedGaussianRadius, 0.1, 250.0)
        && ExposureSettings.InRange(ResolvedMotionAngle, -90.0, 90.0)
        && ExposureSettings.InRange(ResolvedMotionDistance, 1.0, 2000.0)
        && ExposureSettings.InRange(ResolvedNoiseAmount, 0.1, 400.0);

    /// <summary>把 <see cref="AdjustmentKind"/> 转成 manifest 字面量。</summary>
    public string KindLiteral => AdjustmentKindStrings.ToLiteral(Kind);
}

/// <summary>调整层参数的 JSON 读写。键名逐字取自 Swift <c>Codable</c> 的字段名。</summary>
/// <remarks>
/// <para>依据 <c>docs/project-format.md:29,33</c>：<c>hue</c> / <c>saturation</c> / <c>lightness</c> /
/// <c>colorize</c> / <c>hsvSettings</c> / <c>levels</c> / <c>curves</c> / <c>exposureSettings</c> /
/// <c>gradientMapSettings</c> / <c>grainSettings</c> / <c>blackWhiteSettings</c> /
/// <c>colorBalanceSettings</c> / <c>blurRadius</c> / <c>motionAngle</c> / <c>motionDistance</c> /
/// <c>noiseAmount</c> / <c>noiseGaussian</c> / <c>noiseMonochromatic</c> / <c>noiseSeed</c>。</para>
/// <para>🔴 <b>字面量里的 <c>&amp;</c> 与 <c>/</c> 不转义</b>：JSON 字符串中它们是普通字符，
/// Swift <c>Codable</c> 输出的就是 <c>"Black &amp; White"</c> 与 <c>"Hue/Saturation"</c> 原文。</para>
/// </remarks>
public static class AdjustmentJson
{
    /// <summary>把 <see cref="Compositor.Core.AdjustmentSpec.Settings"/> 解析为 <see cref="LayerAdjustment"/>。</summary>
    /// <param name="kind">调整层种类。</param>
    /// <param name="settings">原始 JSON 对象。</param>
    /// <exception cref="JsonException">键存在但类型不符，或数值越界／非有限。</exception>
    public static LayerAdjustment Read(AdjustmentKind kind, JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var a = new LayerAdjustment { Kind = kind };

        a = a with
        {
            Hue = Dbl(settings, "hue", 0.0),
            Saturation = Dbl(settings, "saturation", 0.0),
            Lightness = Dbl(settings, "lightness", 0.0),
            Colorize = Bool(settings, "colorize", false),
            BlurRadius = DblOrNull(settings, "blurRadius"),
            MotionAngle = DblOrNull(settings, "motionAngle"),
            MotionDistance = DblOrNull(settings, "motionDistance"),
            NoiseAmount = DblOrNull(settings, "noiseAmount"),
            NoiseGaussian = BoolOrNull(settings, "noiseGaussian"),
            NoiseMonochromatic = BoolOrNull(settings, "noiseMonochromatic"),
            NoiseSeed = UIntOrNull(settings, "noiseSeed"),
        };

        if (TryObject(settings, "hsvSettings") is { } hsv) a = a with { HsvSettings = ReadHsv(hsv) };
        if (TryObject(settings, "levels") is { } lv) a = a with { Levels = ReadLevels(lv) };
        if (TryObject(settings, "curves") is { } cv) a = a with { Curves = ReadCurves(cv) };
        if (TryObject(settings, "exposureSettings") is { } ex) a = a with { ExposureSettings = ReadExposure(ex) };
        if (TryObject(settings, "gradientMapSettings") is { } gm) a = a with { GradientMapSettings = ReadGradientMap(gm) };
        if (TryObject(settings, "grainSettings") is { } gr) a = a with { GrainSettings = ReadGrain(gr) };
        if (TryObject(settings, "blackWhiteSettings") is { } bw) a = a with { BlackWhiteSettings = ReadBlackWhite(bw) };
        if (TryObject(settings, "colorBalanceSettings") is { } cb) a = a with { ColorBalanceSettings = ReadColorBalance(cb) };

        return a;
    }

    /// <summary>把 <see cref="LayerAdjustment"/> 写回 <see cref="Compositor.Core.AdjustmentSpec.Settings"/>。</summary>
    /// <param name="a">调整层参数。</param>
    /// <remarks>只写非缺省的键——Swift <c>Codable</c> 也用 <c>encodeIfPresent</c>，少写的键等价于缺省。</remarks>
    public static JsonObject Write(LayerAdjustment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var o = new JsonObject();
        Put(o, "hue", a.Hue);
        Put(o, "saturation", a.Saturation);
        Put(o, "lightness", a.Lightness);
        Put(o, "colorize", a.Colorize);
        PutIf(o, "blurRadius", a.BlurRadius);
        PutIf(o, "motionAngle", a.MotionAngle);
        PutIf(o, "motionDistance", a.MotionDistance);
        PutIf(o, "noiseAmount", a.NoiseAmount);
        PutIf(o, "noiseGaussian", a.NoiseGaussian);
        PutIf(o, "noiseMonochromatic", a.NoiseMonochromatic);
        PutIf(o, "noiseSeed", a.NoiseSeed);
        if (a.HsvSettings is { } h) o["hsvSettings"] = WriteHsv(h);
        if (a.Levels is { } l) o["levels"] = WriteLevels(l);
        if (a.Curves is { } c) o["curves"] = WriteCurves(c);
        if (a.ExposureSettings is { } e) o["exposureSettings"] = WriteExposure(e);
        if (a.GradientMapSettings is { } g) o["gradientMapSettings"] = WriteGradientMap(g);
        if (a.GrainSettings is { } n) o["grainSettings"] = WriteGrain(n);
        if (a.BlackWhiteSettings is { } b) o["blackWhiteSettings"] = WriteBlackWhite(b);
        if (a.ColorBalanceSettings is { } cb) o["colorBalanceSettings"] = WriteColorBalance(cb);
        return o;
    }

    // ── 子结构读写 ────────────────────────────────────────────────────────────

    private static HueSaturationSettings ReadHsv(JsonObject o) => new()
    {
        Range = Enum.TryParse<ColorRange>(Str(o, "range") ?? "Master", out var r) ? r : ColorRange.Master,
        Colorize = Bool(o, "colorize", false),
        InvertRange = Bool(o, "invertRange", false),
        Adjustments = ReadMap<ColorRange, RangeAdjustment>(o, "adjustments", r => new RangeAdjustment
        {
            Hue = Dbl(r, "hue", 0.0),
            Saturation = Dbl(r, "saturation", 0.0),
            Lightness = Dbl(r, "lightness", 0.0),
        }),
        Bands = ReadMap<ColorRange, HueBand>(o, "bands", b => new HueBand
        {
            FalloffStart = Dbl(b, "falloffStart", 0),
            RangeStart = Dbl(b, "rangeStart", 0),
            RangeEnd = Dbl(b, "rangeEnd", 0),
            FalloffEnd = Dbl(b, "falloffEnd", 0),
        }),
    };

    private static JsonObject WriteHsv(HueSaturationSettings s)
    {
        var o = new JsonObject { ["range"] = s.Range.ToString(), ["colorize"] = s.Colorize };
        if (s.InvertRange) o["invertRange"] = true;
        var adj = new JsonObject();
        foreach (var (k, v) in s.Adjustments)
        {
            adj[k.ToString()] = new JsonObject
            {
                ["hue"] = v.Hue, ["saturation"] = v.Saturation, ["lightness"] = v.Lightness,
            };
        }
        o["adjustments"] = adj;
        var bands = new JsonObject();
        foreach (var (k, v) in s.Bands)
        {
            bands[k.ToString()] = new JsonObject
            {
                ["falloffStart"] = v.FalloffStart, ["rangeStart"] = v.RangeStart,
                ["rangeEnd"] = v.RangeEnd, ["falloffEnd"] = v.FalloffEnd,
            };
        }
        o["bands"] = bands;
        return o;
    }

    private static LevelsSettings ReadLevels(JsonObject o)
    {
        var ranges = new List<LevelRange>();
        if (o["ranges"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                var r = node as JsonObject ?? throw Bad("levels.ranges");
                ranges.Add(new LevelRange
                {
                    Black = Dbl(r, "black", 0.0),
                    White = Dbl(r, "white", 255.0),
                    Gamma = Dbl(r, "gamma", 1.0),
                    OutputBlack = Dbl(r, "outputBlack", 0.0),
                    OutputWhite = Dbl(r, "outputWhite", 255.0),
                });
            }
        }
        return new LevelsSettings { Ranges = ranges };
    }

    private static JsonObject WriteLevels(LevelsSettings s)
    {
        var ranges = new JsonArray();
        foreach (var r in s.Ranges)
        {
            ranges.Add(new JsonObject
            {
                ["black"] = r.Black, ["white"] = r.White, ["gamma"] = r.Gamma,
                ["outputBlack"] = r.OutputBlack, ["outputWhite"] = r.OutputWhite,
            });
        }
        return new JsonObject { ["ranges"] = ranges };
    }

    private static CurvesSettings ReadCurves(JsonObject o)
    {
        var channels = new List<IReadOnlyList<CurvePoint>>();
        if (o["channels"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                var pts = new List<CurvePoint>();
                if (node is JsonArray pa)
                {
                    foreach (var p in pa)
                    {
                        if (p is not JsonObject po) throw Bad("curves.channels");
                        pts.Add(new CurvePoint(Dbl(po, "x", 0), Dbl(po, "y", 0)));
                    }
                }
                channels.Add(pts);
            }
        }
        return new CurvesSettings { Channels = channels };
    }

    private static JsonObject WriteCurves(CurvesSettings s)
    {
        var arr = new JsonArray();
        foreach (var pts in s.Channels)
        {
            var pa = new JsonArray();
            foreach (var p in pts) pa.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
            arr.Add(pa);
        }
        return new JsonObject { ["channels"] = arr };
    }

    private static ExposureSettings ReadExposure(JsonObject o) => new()
    {
        Exposure = Dbl(o, "exposure", 0.0),
        Offset = Dbl(o, "offset", 0.0),
        Gamma = Dbl(o, "gamma", 1.0),
    };

    private static JsonObject WriteExposure(ExposureSettings s) => new()
    {
        ["exposure"] = s.Exposure, ["offset"] = s.Offset, ["gamma"] = s.Gamma,
    };

    private static GradientMapSettings ReadGradientMap(JsonObject o) => new()
    {
        Shadows = ReadColor(TryObject(o, "shadows"), AdjustmentColor.Black),
        Highlights = ReadColor(TryObject(o, "highlights"), AdjustmentColor.White),
        Reversed = Bool(o, "reversed", false),
    };

    private static JsonObject WriteGradientMap(GradientMapSettings s) => new()
    {
        ["shadows"] = WriteColor(s.Shadows),
        ["highlights"] = WriteColor(s.Highlights),
        ["reversed"] = s.Reversed,
    };

    private static AdjustmentColor ReadColor(JsonObject? o, AdjustmentColor fallback)
    {
        if (o is null) return fallback;
        return new AdjustmentColor
        {
            Red = Dbl(o, "red", 0.0),
            Green = Dbl(o, "green", 0.0),
            Blue = Dbl(o, "blue", 0.0),
        };
    }

    private static JsonObject WriteColor(AdjustmentColor c) => new()
    {
        ["red"] = c.Red, ["green"] = c.Green, ["blue"] = c.Blue,
    };

    private static GrainSettings ReadGrain(JsonObject o) => new()
    {
        Amount = Dbl(o, "amount", 25.0),
        Size = Dbl(o, "size", 1.5),
        Roughness = Dbl(o, "roughness", 50.0),
        Seed = (uint)Dbl(o, "seed", 0.0),
    };

    private static JsonObject WriteGrain(GrainSettings s) => new()
    {
        ["amount"] = s.Amount, ["size"] = s.Size,
        ["roughness"] = s.Roughness, ["seed"] = (double)s.Seed,
    };

    private static BlackWhiteSettings ReadBlackWhite(JsonObject o) => new()
    {
        Reds = Dbl(o, "reds", 40.0),
        Yellows = Dbl(o, "yellows", 60.0),
        Greens = Dbl(o, "greens", 40.0),
        Cyans = Dbl(o, "cyans", 60.0),
        Blues = Dbl(o, "blues", 20.0),
        Magentas = Dbl(o, "magentas", 80.0),
        Tint = Bool(o, "tint", false),
        TintHue = Dbl(o, "tintHue", 40.0),
        TintSaturation = Dbl(o, "tintSaturation", 20.0),
    };

    private static JsonObject WriteBlackWhite(BlackWhiteSettings s) => new()
    {
        ["reds"] = s.Reds, ["yellows"] = s.Yellows, ["greens"] = s.Greens,
        ["cyans"] = s.Cyans, ["blues"] = s.Blues, ["magentas"] = s.Magentas,
        ["tint"] = s.Tint, ["tintHue"] = s.TintHue, ["tintSaturation"] = s.TintSaturation,
    };

    private static ColorBalanceSettings ReadColorBalance(JsonObject o) => new()
    {
        ShadowCyanRed = Dbl(o, "shadowCyanRed", 0.0),
        ShadowMagentaGreen = Dbl(o, "shadowMagentaGreen", 0.0),
        ShadowYellowBlue = Dbl(o, "shadowYellowBlue", 0.0),
        MidCyanRed = Dbl(o, "midCyanRed", 0.0),
        MidMagentaGreen = Dbl(o, "midMagentaGreen", 0.0),
        MidYellowBlue = Dbl(o, "midYellowBlue", 0.0),
        HighlightCyanRed = Dbl(o, "highlightCyanRed", 0.0),
        HighlightMagentaGreen = Dbl(o, "highlightMagentaGreen", 0.0),
        HighlightYellowBlue = Dbl(o, "highlightYellowBlue", 0.0),
        PreserveLuminosity = Bool(o, "preserveLuminosity", true),
    };

    private static JsonObject WriteColorBalance(ColorBalanceSettings s) => new()
    {
        ["shadowCyanRed"] = s.ShadowCyanRed,
        ["shadowMagentaGreen"] = s.ShadowMagentaGreen,
        ["shadowYellowBlue"] = s.ShadowYellowBlue,
        ["midCyanRed"] = s.MidCyanRed,
        ["midMagentaGreen"] = s.MidMagentaGreen,
        ["midYellowBlue"] = s.MidYellowBlue,
        ["highlightCyanRed"] = s.HighlightCyanRed,
        ["highlightMagentaGreen"] = s.HighlightMagentaGreen,
        ["highlightYellowBlue"] = s.HighlightYellowBlue,
        ["preserveLuminosity"] = s.PreserveLuminosity,
    };

    // ── JSON 标量工具 ─────────────────────────────────────────────────────────

    private static Dictionary<TKey, TValue> ReadMap<TKey, TValue>(
        JsonObject o, string key, Func<JsonObject, TValue> read)
        where TKey : struct, Enum
    {
        var map = new Dictionary<TKey, TValue>();
        if (TryObject(o, key) is not { } src) return map;
        foreach (var (k, v) in src)
        {
            if (v is JsonObject vo && Enum.TryParse<TKey>(k, out var parsed)) map[parsed] = read(vo);
        }
        return map;
    }

    private static JsonObject? TryObject(JsonObject o, string key) => o[key] as JsonObject;

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();

    private static double Dbl(JsonObject o, string key, double fallback)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d))
        {
            if (!double.IsFinite(d)) throw Bad($"{key}={d}（非有限值）");
            return d;
        }
        throw Bad($"{key}（不是数字）");
    }

    private static double? DblOrNull(JsonObject o, string key)
        => o[key] is null ? null : Dbl(o, key, 0.0);

    private static bool Bool(JsonObject o, string key, bool fallback)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static bool? BoolOrNull(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static uint? UIntOrNull(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (!v.TryGetValue<double>(out var d)) throw Bad($"{key}（不是数字）");
        if (!double.IsFinite(d) || d < 0 || d > uint.MaxValue || d != Math.Floor(d))
        {
            throw Bad($"{key}={d}（不是合法的 UInt32）");
        }
        return (uint)d;
    }

    private static void Put(JsonObject o, string key, double v) => o[key] = v;

    private static void Put(JsonObject o, string key, bool v) => o[key] = v;

    private static void PutIf(JsonObject o, string key, double? v) { if (v is { } d) o[key] = d; }

    private static void PutIf(JsonObject o, string key, bool? v) { if (v is { } b) o[key] = b; }

    private static void PutIf(JsonObject o, string key, uint? v) { if (v is { } n) o[key] = (double)n; }

    private static JsonException Bad(string what)
        => new($"调整层参数非法：{what}。越界或非有限值必须被拒绝，不做静默回退。");
}
