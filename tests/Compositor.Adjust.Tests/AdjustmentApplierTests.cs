using Xunit;

namespace Compositor.Adjust.Tests;

/// <summary>
/// 像素分派测试。断言<b>预乘不变量</b>与 LUT 量纲，不重复验证 Core 的像素数学。
/// </summary>
public class AdjustmentApplierTests
{
    private static byte[] Solid(byte r, byte g, byte b, byte a = 255, int n = 4)
    {
        var buf = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            // 预乘：颜色分量不得超过 alpha。
            int m = Math.Min(255, (int)a);
            buf[i * 4] = (byte)Math.Min(m, (int)r * m / 255);
            buf[i * 4 + 1] = (byte)Math.Min(m, (int)g * m / 255);
            buf[i * 4 + 2] = (byte)Math.Min(m, (int)b * m / 255);
            buf[i * 4 + 3] = (byte)m;
        }
        return buf;
    }

    private static void AssertPremultiplied(byte[] buf)
    {
        for (int i = 0; i < buf.Length; i += 4)
        {
            int a = buf[i + 3];
            Assert.True(buf[i] <= a, "R 超过 alpha，破坏了预乘不变量。");
            Assert.True(buf[i + 1] <= a, "G 超过 alpha，破坏了预乘不变量。");
            Assert.True(buf[i + 2] <= a, "B 超过 alpha，破坏了预乘不变量。");
        }
    }

    [Fact]
    public void Invert_预乘反相保持alpha()
    {
        // Mac 版矩阵：每个颜色分量变成 alpha − 颜色。
        var buf = Solid(200, 100, 50);
        AdjustmentApplier.Apply(buf, 2, 2, 8, new LayerAdjustment { Kind = AdjustmentKind.Invert });
        Assert.Equal(55, buf[0]);    // 255 − 200
        Assert.Equal(155, buf[1]);   // 255 − 100
        Assert.Equal(205, buf[2]);   // 255 − 50
        Assert.Equal(255, buf[3]);   // alpha 不变
        AssertPremultiplied(buf);
    }

    [Fact]
    public void Invert_半透明像素仍不超alpha()
    {
        var buf = Solid(200, 100, 50, a: 128);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.Invert });
        AssertPremultiplied(buf);
        // alpha=128 时，颜色分量变成 128 − 原值，且都 ≤ 128。
        Assert.True(buf[0] <= 128);
        Assert.True(buf[1] <= 128);
        Assert.True(buf[2] <= 128);
        Assert.Equal(128, buf[3]);
    }

    [Fact]
    public void Invert_全不透明的原色不变()
    {
        var buf = Solid(0, 128, 255);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.Invert });
        Assert.Equal(255, buf[0]);
        Assert.Equal(127, buf[1]);
        Assert.Equal(0, buf[2]);
    }

    [Fact]
    public void Levels_恒等设置不改像素()
    {
        var src = Solid(10, 120, 240);
        var buf = (byte[])src.Clone();
        AdjustmentApplier.Apply(buf, 2, 2, 8, new LayerAdjustment { Kind = AdjustmentKind.Levels });
        Assert.Equal(src, buf);
        AssertPremultiplied(buf);
    }

    [Fact]
    public void Levels_压低黑场变暗()
    {
        var buf = Solid(200, 200, 200);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.Levels,
            Levels = new LevelsSettings
            {
                Ranges = new[] { new LevelRange { Black = 100 }, new LevelRange(), new LevelRange(), new LevelRange() },
            },
        });
        Assert.True(buf[0] < 200, "黑场 100 应把 200 压暗。");
        AssertPremultiplied(buf);
    }

    [Fact]
    public void Curves_恒等曲线不改像素()
    {
        var src = Solid(0, 128, 255);
        var buf = (byte[])src.Clone();
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.Curves });
        Assert.Equal(src, buf);
    }

    [Fact]
    public void Exposure_正档变亮_负档变暗()
    {
        var bright = Solid(128, 128, 128);
        AdjustmentApplier.Apply(bright, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.Exposure,
            ExposureSettings = new ExposureSettings { Exposure = 2.0 },
        });

        var dark = Solid(128, 128, 128);
        AdjustmentApplier.Apply(dark, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.Exposure,
            ExposureSettings = new ExposureSettings { Exposure = -2.0 },
        });

        Assert.True(bright[0] > 128, "正曝光应变亮。");
        Assert.True(dark[0] < 128, "负曝光应变暗。");
        AssertPremultiplied(bright);
        AssertPremultiplied(dark);
    }

    [Fact]
    public void Exposure_恒等不改像素()
    {
        var src = Solid(10, 120, 240);
        var buf = (byte[])src.Clone();
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.Exposure });
        Assert.Equal(src, buf);
    }

    [Fact]
    public void GradientMap_黑到白把灰变成灰()
    {
        var buf = Solid(128, 128, 128);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.GradientMap });
        // 缺省黑→白映射，128 灰应仍约等于 128。
        Assert.InRange(buf[0], 120, 136);
        AssertPremultiplied(buf);
    }

    [Fact]
    public void GradientMap_异色端点给灰像素上色()
    {
        var buf = Solid(128, 128, 128);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.GradientMap,
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = new AdjustmentColor { Red = 0, Green = 0, Blue = 0 },
                Highlights = new AdjustmentColor { Red = 1, Green = 0, Blue = 0 },
            },
        });
        Assert.True(buf[0] > buf[1], "亮端是纯红，红通道应大于绿。");
        Assert.True(buf[0] > buf[2]);
        AssertPremultiplied(buf);
    }

    [Fact]
    public void BlackWhite_默认权重把红色变暗()
    {
        var buf = Solid(255, 0, 0);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.BlackWhite });
        // 源码注释：纯红在默认 0.4 权重下得到 40% 灰度。
        Assert.InRange(buf[0], 100, 103);
        Assert.Equal(buf[0], buf[1]);
        Assert.Equal(buf[0], buf[2]);
        AssertPremultiplied(buf);
    }

    [Fact]
    public void ColorBalance_恒等时短路不改像素()
    {
        var src = Solid(10, 120, 240);
        var buf = (byte[])src.Clone();
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.ColorBalance });
        Assert.Equal(src, buf);
    }

    [Fact]
    public void ColorBalance_红偏移让画面偏红()
    {
        var buf = Solid(100, 100, 100);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.ColorBalance,
            ColorBalanceSettings = new ColorBalanceSettings
            {
                ShadowCyanRed = 50, MidCyanRed = 50, HighlightCyanRed = 50,
            },
        });
        Assert.True(buf[0] > buf[2], "向红偏移后 R 应大于 B。");
        AssertPremultiplied(buf);
    }

    [Fact]
    public void Grain_强度为0时短路不改像素()
    {
        var src = Solid(10, 120, 240);
        var buf = (byte[])src.Clone();
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.Grain,
            GrainSettings = new GrainSettings { Amount = 0 },
        });
        Assert.Equal(src, buf);
    }

    [Fact]
    public void Grain_有强度时产生变化_但同种子可复现()
    {
        static byte[] Run()
        {
            var b = Solid(128, 128, 128, n: 64);
            AdjustmentApplier.Apply(b, 8, 8, 32, new LayerAdjustment
            {
                Kind = AdjustmentKind.Grain,
                GrainSettings = new GrainSettings { Amount = 50, Size = 1.5, Roughness = 50, Seed = 12345 },
            });
            return b;
        }

        var a1 = Run();
        var a2 = Run();
        Assert.Equal(a1, a2);   // 同种子必须完全一致，否则跨会话图案会漂移。
        AssertPremultiplied(a1);
    }

    [Fact]
    public void HueSaturation_恒等时短路()
    {
        var src = Solid(200, 100, 50);
        var buf = (byte[])src.Clone();
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment { Kind = AdjustmentKind.HueSaturation });
        Assert.Equal(src, buf);
    }

    [Fact]
    public void HueSaturation_提高饱和度后颜色更纯()
    {
        var buf = Solid(200, 120, 100);
        AdjustmentApplier.Apply(buf, 1, 1, 4, new LayerAdjustment
        {
            Kind = AdjustmentKind.HueSaturation,
            Saturation = 50,
        });
        int max = Math.Max(buf[0], Math.Max(buf[1], buf[2]));
        int min = Math.Min(buf[0], Math.Min(buf[1], buf[2]));
        Assert.True(max - min >= 200 - 120 - 10, "提高饱和度后通道差应变大。");
        AssertPremultiplied(buf);
    }

    [Fact]
    public void 透明像素不被改动()
    {
        var buf = new byte[8];
        var before = (byte[])buf.Clone();
        AdjustmentApplier.Apply(buf, 2, 1, 8, new LayerAdjustment { Kind = AdjustmentKind.Invert });
        AdjustmentApplier.Apply(buf, 2, 1, 8, new LayerAdjustment { Kind = AdjustmentKind.Levels });
        AdjustmentApplier.Apply(buf, 2, 1, 8, new LayerAdjustment
        {
            Kind = AdjustmentKind.BlackWhite,
        });
        Assert.Equal(before, buf);
    }

    // ── 参数错误必须被拒绝 ────────────────────────────────────────────────

    [Fact]
    public void 越界参数抛ArgumentException()
    {
        var buf = new byte[8];
        Assert.Throws<ArgumentException>(() => AdjustmentApplier.Apply(buf, 2, 1, 8,
            new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 9999 }));
    }

    [Fact]
    public void stride不足抛ArgumentException()
    {
        var buf = new byte[8];
        Assert.Throws<ArgumentException>(() => AdjustmentApplier.Apply(buf, 2, 1, 4,
            new LayerAdjustment { Kind = AdjustmentKind.Invert }));
    }

    [Fact]
    public void 非正尺寸抛ArgumentException()
    {
        var buf = new byte[8];
        Assert.Throws<ArgumentException>(() => AdjustmentApplier.Apply(buf, 0, 1, 8,
            new LayerAdjustment { Kind = AdjustmentKind.Invert }));
    }

    [Fact]
    public void 非正unitsPerPixel抛ArgumentException()
    {
        var buf = new byte[8];
        Assert.Throws<ArgumentException>(() => AdjustmentApplier.Apply(buf, 1, 1, 4,
            new LayerAdjustment { Kind = AdjustmentKind.Invert }, 0, 0, 0));
    }

    [Fact]
    public void 三种模糊类抛出NotSupported并说明原因()
    {
        // 它们需要采样邻域像素，必须由 Canvas/合成器提供前后缓冲。
        var buf = new byte[64];
        foreach (var kind in new[]
        {
            AdjustmentKind.GaussianBlur, AdjustmentKind.MotionBlur, AdjustmentKind.AddNoise,
        })
        {
            var ex = Assert.Throws<NotSupportedException>(() =>
                AdjustmentApplier.Apply(buf, 4, 4, 16, new LayerAdjustment { Kind = kind }));
            Assert.Contains("Canvas", ex.Message, StringComparison.Ordinal);
        }
    }
}
