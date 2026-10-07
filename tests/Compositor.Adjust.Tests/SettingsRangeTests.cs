using Xunit;

namespace Compositor.Adjust.Tests;

/// <summary>参数范围逐条断言。范围全部取自 Swift 源码的 <c>isValid</c>，非估算。</summary>
public class SettingsRangeTests
{
    // ── 曝光（ImageAdjustments.swift:38-40）────────────────────────────────

    [Theory]
    [InlineData(-20.0, 0.0, 1.0, true)]
    [InlineData(20.0, 0.0, 1.0, true)]
    [InlineData(0.0, -0.5, 1.0, true)]
    [InlineData(0.0, 0.5, 1.0, true)]
    [InlineData(0.0, 0.0, 0.01, true)]
    [InlineData(0.0, 0.0, 9.99, true)]
    [InlineData(20.1, 0.0, 1.0, false)]
    [InlineData(-20.1, 0.0, 1.0, false)]
    [InlineData(0.0, 0.51, 1.0, false)]
    [InlineData(0.0, 0.0, 10.0, false)]
    [InlineData(0.0, 0.0, 0.009, false)]
    public void Exposure_范围(double exposure, double offset, double gamma, bool expected)
    {
        var s = new ExposureSettings { Exposure = exposure, Offset = offset, Gamma = gamma };
        Assert.Equal(expected, s.IsValid);
    }

    [Fact]
    public void Exposure_缺省为恒等()
    {
        var s = new ExposureSettings();
        Assert.Equal(0.0, s.Exposure);
        Assert.Equal(0.0, s.Offset);
        Assert.Equal(1.0, s.Gamma);
        Assert.True(s.IsValid);
    }

    [Fact]
    public void Exposure_非有限值被拒()
    {
        Assert.False(new ExposureSettings { Exposure = double.NaN }.IsValid);
        Assert.False(new ExposureSettings { Exposure = double.PositiveInfinity }.IsValid);
        Assert.False(new ExposureSettings { Gamma = double.NegativeInfinity }.IsValid);
    }

    [Fact]
    public void Exposure_查找表量纲是0到1()
    {
        // LevelsPixels.Apply 的 LUT 是 0…1，传 0…255 会全屏过曝。
        var table = new ExposureSettings().BuildTable();
        Assert.Equal(768, table.Length);
        Assert.All(table, v => Assert.InRange(v, 0.0f, 1.0f));
        // 恒等曝光下，0 → 0、255 → 1。
        Assert.Equal(0.0f, table[0], 3);
        Assert.Equal(1.0f, table[255], 3);
    }

    // ── 黑白（ImageAdjustments.swift:115-130）──────────────────────────────

    [Theory]
    [InlineData(-200.0, true)]
    [InlineData(300.0, true)]
    [InlineData(40.0, true)]
    [InlineData(-200.1, false)]
    [InlineData(300.1, false)]
    public void BlackWhite_六色权重范围(double w, bool expected)
    {
        var s = new BlackWhiteSettings { Reds = w };
        Assert.Equal(expected, s.IsValid);
    }

    [Fact]
    public void BlackWhite_默认是Photoshop出厂权重()
    {
        var s = new BlackWhiteSettings();
        Assert.Equal(40.0, s.Reds);
        Assert.Equal(60.0, s.Yellows);
        Assert.Equal(40.0, s.Greens);
        Assert.Equal(60.0, s.Cyans);
        Assert.Equal(20.0, s.Blues);
        Assert.Equal(80.0, s.Magentas);
        Assert.True(s.IsValid);
    }

    [Fact]
    public void BlackWhite_单色调范围()
    {
        Assert.True(new BlackWhiteSettings { TintHue = 0, TintSaturation = 0 }.IsValid);
        Assert.True(new BlackWhiteSettings { TintHue = 360, TintSaturation = 100 }.IsValid);
        Assert.False(new BlackWhiteSettings { TintHue = 361 }.IsValid);
        Assert.False(new BlackWhiteSettings { TintHue = -1 }.IsValid);
        Assert.False(new BlackWhiteSettings { TintSaturation = 101 }.IsValid);
    }

    [Fact]
    public void BlackWhite_权重顺序为红黄绿青蓝洋红()
    {
        // 顺序必须与 AdjustPixels.BlackWhite 的 weights 索引一致（AdjustPixels.cs:209-213）。
        var s = new BlackWhiteSettings
        {
            Reds = 1, Yellows = 2, Greens = 3, Cyans = 4, Blues = 5, Magentas = 6,
        };
        var w = s.BuildWeights();
        Assert.Equal(6, w.Length);
        for (int i = 0; i < 6; i++) Assert.Equal((i + 1) / 100.0f, w[i], 5);
    }

    // ── 色彩平衡（ImageAdjustments.swift:147）──────────────────────────────

    [Theory]
    [InlineData(-100.0, true)]
    [InlineData(100.0, true)]
    [InlineData(0.0, true)]
    [InlineData(-100.1, false)]
    [InlineData(100.1, false)]
    public void ColorBalance_九轴范围(double v, bool expected)
        => Assert.Equal(expected, new ColorBalanceSettings { ShadowCyanRed = v }.IsValid);

    [Fact]
    public void ColorBalance_九轴各字段都被校验()
    {
        // 逐一确认九个字段都接进了 IsValid，而不是只校验了第一个。
        var baseSettings = new ColorBalanceSettings();
        var mutated = new ColorBalanceSettings[]
        {
            baseSettings with { ShadowCyanRed = 200 },
            baseSettings with { ShadowMagentaGreen = 200 },
            baseSettings with { ShadowYellowBlue = 200 },
            baseSettings with { MidCyanRed = 200 },
            baseSettings with { MidMagentaGreen = 200 },
            baseSettings with { MidYellowBlue = 200 },
            baseSettings with { HighlightCyanRed = 200 },
            baseSettings with { HighlightMagentaGreen = 200 },
            baseSettings with { HighlightYellowBlue = 200 },
        };

        Assert.True(baseSettings.IsValid, "未改动的设置应当合法。");
        foreach (var s in mutated)
        {
            Assert.False(s.IsValid, "有一个轴没被校验到。");
        }
    }

    [Fact]
    public void ColorBalance_默认保持明度为真()
    {
        Assert.True(new ColorBalanceSettings().PreserveLuminosity);
        Assert.True(new ColorBalanceSettings().IsIdentity);
    }

    [Fact]
    public void ColorBalance_非零即非恒等()
        => Assert.False(new ColorBalanceSettings { MidCyanRed = 1.0 }.IsIdentity);

    // ── 颗粒（ImageAdjustments.swift:181-183）──────────────────────────────

    [Theory]
    [InlineData(0.0, 1.5, 0.0, true)]
    [InlineData(100.0, 20.0, 100.0, true)]
    [InlineData(-0.1, 1.5, 50.0, false)]
    [InlineData(100.1, 1.5, 50.0, false)]
    [InlineData(25.0, 0.4, 50.0, false)]
    [InlineData(25.0, 20.1, 50.0, false)]
    [InlineData(25.0, 1.5, -0.1, false)]
    [InlineData(25.0, 1.5, 100.1, false)]
    public void Grain_范围(double amount, double size, double rough, bool expected)
        => Assert.Equal(expected, new GrainSettings { Amount = amount, Size = size, Roughness = rough }.IsValid);

    [Fact]
    public void Grain_默认值为25_1p5_50()
    {
        var g = new GrainSettings();
        Assert.Equal(25.0, g.Amount);
        Assert.Equal(1.5, g.Size);
        Assert.Equal(50.0, g.Roughness);
        Assert.Equal(0u, g.Seed);
    }

    // ── 渐变映射（ImageAdjustments.swift:28）───────────────────────────────

    [Fact]
    public void GradientMap_端点颜色须在0到1()
    {
        Assert.True(new GradientMapSettings().IsValid);
        Assert.False(new GradientMapSettings
        {
            Shadows = new AdjustmentColor { Red = 1.1, Green = 0, Blue = 0 },
        }.IsValid);
        Assert.False(new GradientMapSettings
        {
            Highlights = new AdjustmentColor { Red = double.NaN, Green = 1, Blue = 1 },
        }.IsValid);
    }

    [Fact]
    public void GradientMap_查表为768项且按每项连续3字节排列()
    {
        // 🔴 布局必须与 AdjustPixels.GradientMap 的 color = level*3 兼容。
        // 缺省黑→白：level 0 → 三通道全 0，level 255 → 三通道全 255。
        var t = new GradientMapSettings().BuildTable();
        Assert.Equal(768, t.Length);
        Assert.Equal(0, t[0]);
        Assert.Equal(0, t[1]);
        Assert.Equal(0, t[2]);
        Assert.Equal(255, t[255 * 3]);
        Assert.Equal(255, t[255 * 3 + 1]);
        Assert.Equal(255, t[255 * 3 + 2]);
    }

    [Fact]
    public void GradientMap_查表每项三字节同属一个亮度级()
    {
        // 128 灰 → 黑到白的中点，三通道都应是 128；通道不得串位。
        var t = new GradientMapSettings().BuildTable();
        Assert.Equal(128, t[128 * 3]);
        Assert.Equal(128, t[128 * 3 + 1]);
        Assert.Equal(128, t[128 * 3 + 2]);
    }

    [Fact]
    public void GradientMap_反向时两端对调()
    {
        // 缺省 Shadows=黑(0,0,0)、Highlights=白(1,1,1)。
        // Reversed 时 Ends 返回 (highlights, shadows)，即暗端变白、亮端变黑。
        var s = new GradientMapSettings { Reversed = true };
        var (dark, light) = s.Ends;
        Assert.Equal(1.0, dark.Red, 6);
        Assert.Equal(0.0, light.Red, 6);

        var t = s.BuildTable();
        // level 0 取暗端色 = 白：三个通道都应是 255。
        Assert.Equal(255, (int)t[0]);
        Assert.Equal(255, (int)t[1]);
        Assert.Equal(255, (int)t[2]);
        // level 255 取亮端色 = 黑：三个通道都应是 0。
        Assert.Equal(0, (int)t[255 * 3]);
        Assert.Equal(0, (int)t[255 * 3 + 1]);
        Assert.Equal(0, (int)t[255 * 3 + 2]);
    }

    // ── 色阶（Levels.swift:19-23）─────────────────────────────────────────

    [Fact]
    public void LevelRange_出厂默认合法且为恒等()
    {
        var r = new LevelRange();
        Assert.True(r.IsValid);
        Assert.True(r.IsIdentity);
        Assert.Equal(0.0, r.Black);
        Assert.Equal(255.0, r.White);
        Assert.Equal(1.0, r.Gamma);
    }

    [Fact]
    public void LevelRange_黑场上限254()
    {
        Assert.True(new LevelRange { Black = 254 }.IsValid);
        Assert.False(new LevelRange { Black = 255 }.IsValid);
        Assert.False(new LevelRange { Black = -1 }.IsValid);
    }

    [Fact]
    public void LevelRange_白场依赖黑场()
    {
        // white ∈ [black+1, 255]：这是「白场依赖黑场」，不能各自独立判断。
        Assert.True(new LevelRange { Black = 100, White = 101 }.IsValid);
        Assert.False(new LevelRange { Black = 100, White = 100 }.IsValid);
        Assert.False(new LevelRange { Black = 100, White = 99 }.IsValid);
        Assert.False(new LevelRange { Black = 100, White = 256 }.IsValid);
    }

    [Fact]
    public void LevelRange_伽马范围0p1到9p99()
    {
        Assert.True(new LevelRange { Gamma = 0.1 }.IsValid);
        Assert.True(new LevelRange { Gamma = 9.99 }.IsValid);
        Assert.False(new LevelRange { Gamma = 0.09 }.IsValid);
        Assert.False(new LevelRange { Gamma = 10.0 }.IsValid);
    }

    [Fact]
    public void LevelRange_输出黑场白场范围0到255()
    {
        Assert.True(new LevelRange { OutputBlack = 0, OutputWhite = 255 }.IsValid);
        Assert.False(new LevelRange { OutputBlack = -1 }.IsValid);
        Assert.False(new LevelRange { OutputWhite = 256 }.IsValid);
    }

    [Fact]
    public void LevelRange_非有限值被拒()
    {
        Assert.False(new LevelRange { Black = double.NaN }.IsValid);
        Assert.False(new LevelRange { White = double.PositiveInfinity }.IsValid);
        Assert.False(new LevelRange { Gamma = double.NaN }.IsValid);
    }

    [Fact]
    public void Levels_必须恰有4段()
    {
        Assert.True(new LevelsSettings().IsValid);
        Assert.False(new LevelsSettings { Ranges = new[] { new LevelRange() } }.IsValid);
        Assert.False(new LevelsSettings
        {
            Ranges = new[] { new LevelRange(), new LevelRange(), new LevelRange() },
        }.IsValid);
    }

    [Fact]
    public void Levels_查表量纲是0到1()
    {
        var t = new LevelsSettings().BuildTable();
        Assert.Equal(768, t.Length);
        Assert.All(t, v => Assert.InRange(v, 0.0f, 1.0f));
    }

    [Fact]
    public void Levels_查表是R_G_B各一段_与GradientMap的每项三字节不同()
    {
        // 🔴 两种 LUT 布局不同，混用会串通道：
        //   LevelsPixels.Apply  → R 段[0..255]、G 段[256..511]、B 段[512..767]
        //   AdjustPixels.GradientMap → 每项连续 3 字节，按 level*3 索引
        // 恒等色阶下两段通道的值应完全相同，据此可分辨有没有建错布局。
        var t = new LevelsSettings().BuildTable();
        Assert.Equal(t[0], t[256], 5);
        Assert.Equal(t[0], t[512], 5);
        Assert.Equal(t[255], t[511], 5);
        Assert.Equal(t[255], t[767], 5);
    }

    // ── 曲线（Curves.swift:11-14）─────────────────────────────────────────

    [Fact]
    public void Curves_默认四条通道各两个点()
    {
        var c = new CurvesSettings();
        Assert.True(c.IsValid);
        Assert.Equal(4, c.Channels.Count);
        Assert.All(c.Channels, ch => Assert.Equal(2, ch.Count));
    }

    [Fact]
    public void Curves_点数须在2到32()
    {
        static IReadOnlyList<IReadOnlyList<CurvePoint>> Make(int n)
        {
            var pts = new List<CurvePoint>();
            for (int i = 0; i < n; i++) pts.Add(new CurvePoint(i * 255.0 / (n - 1), i * 255.0 / (n - 1)));
            return [pts, pts, pts, pts];
        }
        Assert.True(new CurvesSettings { Channels = Make(2) }.IsValid);
        Assert.True(new CurvesSettings { Channels = Make(32) }.IsValid);
        Assert.False(new CurvesSettings { Channels = Make(1) }.IsValid);
        Assert.False(new CurvesSettings { Channels = Make(33) }.IsValid);
    }

    [Fact]
    public void Curves_首末点x须恰为0和255()
    {
        var bad = new List<IReadOnlyList<CurvePoint>>
        {
            new CurvePoint[] { new(1, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(254, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
        };
        Assert.False(new CurvesSettings { Channels = bad }.IsValid);
    }

    [Fact]
    public void Curves_x必须严格递增()
    {
        // 相等的 x 会让斜率分母为 0。
        var bad = new List<IReadOnlyList<CurvePoint>>
        {
            new CurvePoint[] { new(0, 0), new(100, 10), new(100, 20), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
        };
        Assert.False(new CurvesSettings { Channels = bad }.IsValid);
    }

    [Fact]
    public void Curves_坐标须在0到255且有限()
    {
        var bad = new List<IReadOnlyList<CurvePoint>>
        {
            new CurvePoint[] { new(0, 0), new(255, 300) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
        };
        Assert.False(new CurvesSettings { Channels = bad }.IsValid);

        var nan = new List<IReadOnlyList<CurvePoint>>
        {
            new CurvePoint[] { new(0, 0), new(255, double.NaN) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
            new CurvePoint[] { new(0, 0), new(255, 255) },
        };
        Assert.False(new CurvesSettings { Channels = nan }.IsValid);
    }

    [Fact]
    public void Curves_恒等曲线0进255出()
    {
        var c = new CurvesSettings();
        Assert.Equal(0.0, c.Value(0, 0), 3);
        Assert.Equal(255.0, c.Value(255, 0), 3);
        Assert.Equal(128.0, c.Value(128, 0), 1);
    }

    [Fact]
    public void Curves_查表量纲是0到1()
    {
        var t = new CurvesSettings().BuildTable();
        Assert.Equal(768, t.Length);
        Assert.All(t, v => Assert.InRange(v, 0.0f, 1.0f));
    }

    // ── 色相/饱和度（LayerAdjustment.swift:129-134）───────────────────────

    [Theory]
    [InlineData(360.0, 100.0, 100.0, true)]
    [InlineData(-360.0, -100.0, -100.0, true)]
    [InlineData(360.1, 0.0, 0.0, false)]
    [InlineData(0.0, 100.1, 0.0, false)]
    [InlineData(0.0, 0.0, 100.1, false)]
    [InlineData(double.NaN, 0.0, 0.0, false)]
    public void Hsv_扁平字段范围(double h, double s, double l, bool expected)
    {
        var a = new LayerAdjustment { Kind = AdjustmentKind.HueSaturation, Hue = h, Saturation = s, Lightness = l };
        Assert.Equal(expected, a.IsValid);
    }

    [Fact]
    public void RangeAdjustment_逐色域范围()
    {
        Assert.True(new RangeAdjustment { Hue = 360, Saturation = 100, Lightness = 100 }.IsValid);
        Assert.False(new RangeAdjustment { Hue = 361 }.IsValid);
        Assert.False(new RangeAdjustment { Saturation = 101 }.IsValid);
        Assert.False(new RangeAdjustment { Lightness = -101 }.IsValid);
        Assert.True(new RangeAdjustment().IsIdentity);
    }

    [Fact]
    public void HueBand_手柄只要求有限_不要求落在0到360()
    {
        // 色带按模 360 环绕，handles 可以是 400 这样的值。
        Assert.True(new HueBand { FalloffStart = 400, RangeStart = 450, RangeEnd = 500, FalloffEnd = 560 }.IsValid);
        Assert.False(new HueBand { FalloffStart = double.NaN, RangeStart = 0, RangeEnd = 0, FalloffEnd = 360 }.IsValid);
    }

    [Fact]
    public void ColorRange_共7个_含Master()
        => Assert.Equal(7, Enum.GetValues<ColorRange>().Length);

    [Fact]
    public void HueBand_默认色带符合Photoshop()
    {
        var reds = HueBand.DefaultFor(ColorRange.Reds);
        Assert.Equal(315, reds.FalloffStart);
        Assert.Equal(345, reds.RangeStart);
        Assert.Equal(15, reds.RangeEnd);
        Assert.Equal(45, reds.FalloffEnd);
    }

    [Fact]
    public void HueBand_红色带跨0度时仍然生效()
    {
        // 红色带是 315→45，跨 360 环绕；色相 0（正红）应满强度。
        var reds = HueBand.DefaultFor(ColorRange.Reds);
        Assert.Equal(1.0, reds.WeightOf(0), 3);
        Assert.Equal(0.0, reds.WeightOf(180), 3);
    }

    [Fact]
    public void HueBand_Forward_恒为0到360()
    {
        Assert.Equal(10, HueBand.Forward(0, 10), 6);
        Assert.Equal(350, HueBand.Forward(10, 0), 6);
        Assert.Equal(45, HueBand.Forward(315, 0), 6);
    }

    [Fact]
    public void Hsv_Master权重恒为1()
    {
        var s = new HueSaturationSettings();
        Assert.Equal(1.0, s.WeightOf(ColorRange.Master, 123), 6);
    }

    [Fact]
    public void Hsv_未着色且全零为恒等()
        => Assert.True(new HueSaturationSettings().IsIdentity);

    [Fact]
    public void Hsv_着色即使数值为零也不是恒等()
        => Assert.False(new HueSaturationSettings { Colorize = true }.IsIdentity);

    // ── 顶层扁平字段的 v9 范围（LayerAdjustment.swift:137-140）────────────

    [Theory]
    [InlineData(0.1, 0.0, 10.0, 10.0, true)]
    [InlineData(250.0, 90.0, 2000.0, 400.0, true)]
    [InlineData(0.09, 0.0, 10.0, 10.0, false)]
    [InlineData(250.1, 0.0, 10.0, 10.0, false)]
    [InlineData(10.0, 90.1, 10.0, 10.0, false)]
    [InlineData(10.0, -90.1, 10.0, 10.0, false)]
    [InlineData(10.0, 0.0, 0.99, 10.0, false)]
    [InlineData(10.0, 0.0, 2000.1, 10.0, false)]
    [InlineData(10.0, 0.0, 10.0, 0.09, false)]
    [InlineData(10.0, 0.0, 10.0, 400.1, false)]
    public void 顶层v9字段范围(double radius, double angle, double distance, double amount, bool expected)
    {
        var a = new LayerAdjustment
        {
            Kind = AdjustmentKind.GaussianBlur,
            BlurRadius = radius, MotionAngle = angle, MotionDistance = distance, NoiseAmount = amount,
        };
        Assert.Equal(expected, a.IsValid);
    }

    [Fact]
    public void 缺省值照抄Swift的null合并()
    {
        // LayerAdjustment.swift:92-119 的 ?? 右侧。
        var a = new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur };
        Assert.Equal(10.0, a.ResolvedGaussianRadius);   // 调整层缺省 10
        Assert.Equal(0.0, a.ResolvedMotionAngle);
        Assert.Equal(10.0, a.ResolvedMotionDistance);
        Assert.Equal(10.0, a.ResolvedNoiseAmount);
        Assert.False(a.ResolvedNoiseGaussian);
        Assert.False(a.ResolvedNoiseMonochromatic);
        Assert.Equal(0u, a.ResolvedNoiseSeed);
    }

    [Fact]
    public void 调整层缺省半径10_与滤镜菜单的1不同()
    {
        // 这是「看起来该统一其实不该」的差异，照抄源码不合并。
        Assert.Equal(10.0, LayerAdjustment.DefaultGaussianRadius);
        Assert.Equal(1.0, LayerAdjustment.DefaultFilterRadius);
        Assert.NotEqual(
            LayerAdjustment.DefaultGaussianRadius,
            LayerAdjustment.DefaultFilterRadius);
    }

    [Fact]
    public void 缺省值本身全部合法()
        => Assert.True(new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur }.IsValid);

    [Fact]
    public void SamplingMargin_只有两类模糊非零()
    {
        Assert.Equal(10.0 * 3.0 + 2.0, new LayerAdjustment
        {
            Kind = AdjustmentKind.GaussianBlur, BlurRadius = 10.0,
        }.SamplingMargin, 6);

        Assert.Equal(10.0 / 2.0 + 2.0, new LayerAdjustment
        {
            Kind = AdjustmentKind.MotionBlur, MotionDistance = 10.0,
        }.SamplingMargin, 6);

        Assert.Equal(0.0, new LayerAdjustment { Kind = AdjustmentKind.Invert }.SamplingMargin);
        Assert.Equal(0.0, new LayerAdjustment { Kind = AdjustmentKind.Levels }.SamplingMargin);
    }

    [Fact]
    public void IsValid_校验所有子结构_不论当前kind是什么()
    {
        // 照抄 Swift：isValid 用 && 串联所有子结构，不按 kind 分支。
        // 调整层可在两种 kind 间切换，此前留下的设置仍须合法。
        var a = new LayerAdjustment
        {
            Kind = AdjustmentKind.Levels,
            BlackWhiteSettings = new BlackWhiteSettings { Reds = 9999 },
        };
        Assert.False(a.IsValid);
    }
}
