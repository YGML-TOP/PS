using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Compositor.Adjust.Tests;

/// <summary>JSON 键名与往返。键名逐字取自 Swift <c>Codable</c> 字段名。</summary>
public class AdjustmentJsonTests
{
    [Fact]
    public void 缺省调整层往返后不变()
    {
        var a = new LayerAdjustment { Kind = AdjustmentKind.Levels };
        var back = AdjustmentJson.Read(AdjustmentKind.Levels, AdjustmentJson.Write(a));
        Assert.Equal(a.Kind, back.Kind);
        Assert.Equal(0.0, back.Hue);
        Assert.Equal(0.0, back.Saturation);
        Assert.Equal(0.0, back.Lightness);
        Assert.False(back.Colorize);
        Assert.Null(back.BlurRadius);
        Assert.Null(back.Levels);
    }

    [Fact]
    public void 扁平字段往返()
    {
        var a = new LayerAdjustment
        {
            Kind = AdjustmentKind.MotionBlur,
            Hue = 30, Saturation = -20, Lightness = 10, Colorize = true,
            MotionAngle = -45, MotionDistance = 100, BlurRadius = 12.5,
        };
        var back = AdjustmentJson.Read(AdjustmentKind.MotionBlur, AdjustmentJson.Write(a));
        Assert.Equal(30.0, back.Hue);
        Assert.Equal(-20.0, back.Saturation);
        Assert.Equal(10.0, back.Lightness);
        Assert.True(back.Colorize);
        Assert.Equal(-45.0, back.MotionAngle);
        Assert.Equal(100.0, back.MotionDistance);
        Assert.Equal(12.5, back.BlurRadius);
    }

    [Fact]
    public void 噪点四个字段往返()
    {
        var a = new LayerAdjustment
        {
            Kind = AdjustmentKind.AddNoise,
            NoiseAmount = 250.0, NoiseGaussian = true, NoiseMonochromatic = true, NoiseSeed = 4294967295u,
        };
        var back = AdjustmentJson.Read(AdjustmentKind.AddNoise, AdjustmentJson.Write(a));
        Assert.Equal(250.0, back.NoiseAmount);
        Assert.True(back.NoiseGaussian);
        Assert.True(back.NoiseMonochromatic);
        Assert.Equal(uint.MaxValue, back.NoiseSeed);
    }

    [Fact]
    public void 各嵌套结构往返()
    {
        var a = new LayerAdjustment
        {
            Kind = AdjustmentKind.Curves,
            HsvSettings = new HueSaturationSettings
            {
                Colorize = true,
                Adjustments = new Dictionary<ColorRange, RangeAdjustment>
                {
                    [ColorRange.Reds] = new RangeAdjustment { Hue = 10, Saturation = 5, Lightness = 1 },
                },
            },
            Levels = new LevelsSettings
            {
                Ranges = new[] { new LevelRange { Black = 10, White = 240, Gamma = 1.2 } },
            },
            Curves = new CurvesSettings
            {
                Channels =
                [
                    [new CurvePoint(0, 0), new CurvePoint(128, 100), new CurvePoint(255, 255)],
                    [new CurvePoint(0, 0), new CurvePoint(255, 255)],
                    [new CurvePoint(0, 0), new CurvePoint(255, 255)],
                    [new CurvePoint(0, 0), new CurvePoint(255, 255)],
                ],
            },
            ExposureSettings = new ExposureSettings { Exposure = 1.5, Offset = 0.1, Gamma = 2.0 },
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = new AdjustmentColor { Red = 0.1, Green = 0.2, Blue = 0.3 },
                Highlights = new AdjustmentColor { Red = 0.9, Green = 0.8, Blue = 0.7 },
                Reversed = true,
            },
            GrainSettings = new GrainSettings { Amount = 30, Size = 2.5, Roughness = 70, Seed = 42 },
            BlackWhiteSettings = new BlackWhiteSettings { Reds = 50, Tint = true, TintHue = 200, TintSaturation = 30 },
            ColorBalanceSettings = new ColorBalanceSettings
            {
                ShadowCyanRed = -10, MidYellowBlue = 20, HighlightMagentaGreen = -30,
                PreserveLuminosity = false,
            },
        };

        var back = AdjustmentJson.Read(AdjustmentKind.Curves, AdjustmentJson.Write(a));

        Assert.True(back.HsvSettings!.Colorize);
        Assert.Equal(10.0, back.HsvSettings.Adjustments[ColorRange.Reds].Hue);

        Assert.Equal(10.0, back.Levels!.Ranges[0].Black);
        Assert.Equal(1.2, back.Levels.Ranges[0].Gamma);

        Assert.Equal(3, back.Curves!.Channels[0].Count);
        Assert.Equal(100.0, back.Curves.Channels[0][1].Y);

        Assert.Equal(1.5, back.ExposureSettings!.Exposure);
        Assert.Equal(2.0, back.ExposureSettings.Gamma);

        Assert.Equal(0.1, back.GradientMapSettings!.Shadows.Red);
        Assert.True(back.GradientMapSettings.Reversed);

        Assert.Equal(2.5, back.GrainSettings!.Size);
        Assert.Equal(42u, back.GrainSettings.Seed);

        Assert.Equal(50.0, back.BlackWhiteSettings!.Reds);
        Assert.True(back.BlackWhiteSettings.Tint);
        Assert.False(back.ColorBalanceSettings!.PreserveLuminosity);
        Assert.Equal(20.0, back.ColorBalanceSettings.MidYellowBlue);
    }

    [Fact]
    public void 键名逐字为Swift原名()
    {
        var o = AdjustmentJson.Write(new LayerAdjustment
        {
            Kind = AdjustmentKind.Grain,
            Hue = 1, Saturation = 2, Lightness = 3, Colorize = true,
            BlurRadius = 4, MotionAngle = 5, MotionDistance = 6,
            NoiseAmount = 7, NoiseGaussian = true, NoiseMonochromatic = false, NoiseSeed = 8,
        });

        foreach (var key in new[]
        {
            "hue", "saturation", "lightness", "colorize", "blurRadius", "motionAngle",
            "motionDistance", "noiseAmount", "noiseGaussian", "noiseMonochromatic", "noiseSeed",
        })
        {
            Assert.True(o.ContainsKey(key), $"缺少键 {key}");
        }
    }

    [Fact]
    public void 嵌套键名逐字为Swift原名()
    {
        var a = new LayerAdjustment
        {
            Kind = AdjustmentKind.Levels,
            HsvSettings = new HueSaturationSettings(),
            Levels = new LevelsSettings(), Curves = new CurvesSettings(),
            ExposureSettings = new ExposureSettings(),
            GradientMapSettings = new GradientMapSettings(),
            GrainSettings = new GrainSettings(),
            BlackWhiteSettings = new BlackWhiteSettings(),
            ColorBalanceSettings = new ColorBalanceSettings(),
        };
        var o = AdjustmentJson.Write(a);

        foreach (var key in new[]
        {
            "hsvSettings", "levels", "curves", "exposureSettings", "gradientMapSettings",
            "grainSettings", "blackWhiteSettings", "colorBalanceSettings",
        })
        {
            Assert.True(o.ContainsKey(key), $"缺少键 {key}");
        }

        var bw = (JsonObject)o["blackWhiteSettings"]!;
        foreach (var key in new[]
        {
            "reds", "yellows", "greens", "cyans", "blues", "magentas",
            "tint", "tintHue", "tintSaturation",
        })
        {
            Assert.True(bw.ContainsKey(key), $"blackWhiteSettings 缺少键 {key}");
        }
    }

    // ── 越界与非有限值必须被拒绝 ──────────────────────────────────────────

    [Fact]
    public void 非有限浮点值被拒绝()
    {
        var o = new JsonObject { ["hue"] = double.NaN };
        Assert.Throws<JsonException>(() => AdjustmentJson.Read(AdjustmentKind.HueSaturation, o));
    }

    [Fact]
    public void 类型不符被拒绝()
    {
        var o = new JsonObject { ["hue"] = "不是数字" };
        Assert.Throws<JsonException>(() => AdjustmentJson.Read(AdjustmentKind.HueSaturation, o));
    }

    [Fact]
    public void 负数种子被拒绝()
    {
        var o = new JsonObject { ["noiseSeed"] = -1.0 };
        Assert.Throws<JsonException>(() => AdjustmentJson.Read(AdjustmentKind.AddNoise, o));
    }

    [Fact]
    public void 小数种子被拒绝()
    {
        var o = new JsonObject { ["noiseSeed"] = 1.5 };
        Assert.Throws<JsonException>(() => AdjustmentJson.Read(AdjustmentKind.AddNoise, o));
    }

    [Fact]
    public void 缺省的键走缺省值_不报错()
    {
        // 旧工程在这些键出现之前就保存过，缺省必须解出「与当时完全一致」的结果。
        var o = new JsonObject();
        var a = AdjustmentJson.Read(AdjustmentKind.GradientMap, o);
        Assert.Equal(0.0, a.Hue);
        Assert.Null(a.BlurRadius);
        Assert.Equal(10.0, a.ResolvedGaussianRadius);
    }
}
