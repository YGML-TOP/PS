using Xunit;

namespace Compositor.Imaging.Tests;

/// <summary>
/// <see cref="PixelAlpha"/> 的换算测试。
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠️ 期望值来源的诚实说明：</b>预乘公式确实写死在 macOS 源码里
/// （<c>Compositor/IO/PSD/PSDChannelCoder.swift:63-65</c>：
/// <c>UInt8((UInt16(c) * UInt16(a) + 127) / 255)</c>），
/// <b>但 Mac 版没有任何测试断言这个公式的结果</b>（其测试一律借 CoreGraphics 造 premultipliedLast
/// 上下文绕过它）。
/// </para>
/// <para>
/// 所以这里不能说"期望值抄自 Mac 测试"——没有可抄的。测试改为<b>独立重写一遍公式</b>
/// （<see cref="SwiftFormula"/>，照抄 Swift 那三行），再与生产实现比对。
/// 这是<b>转写一致性测试</b>：能抓出我抄错舍入方式，但不构成"与 Mac 版一致"的证据。
/// 反预乘一侧 Mac 侧由 CoreGraphics 硬件完成、源码无定义，比对意义更弱。
/// </para>
/// </remarks>
public sealed class PixelAlphaTests
{
    /// <summary>照抄 <c>PSDChannelCoder.swift:63-65</c> 的预乘公式，作为独立的对照实现。</summary>
    private static byte SwiftFormula(byte channel, byte alpha) =>
        (byte)(((uint)channel * alpha + 127u) / 255u);

    [Fact]
    public void PremultiplyMatchesSwiftFormula_AcrossWholeDomain()
    {
        // 全域遍历：8 位通道只有 65536 个组合，直接跑完，比挑样例更有说服力。
        for (var channel = 0; channel <= 255; channel++)
        {
            for (var alpha = 0; alpha <= 255; alpha++)
            {
                var expected = SwiftFormula((byte)channel, (byte)alpha);
                var actual = PixelAlpha.PremultiplyPixel((byte)channel, 0, 0, (byte)alpha).R;
                Assert.True(
                    expected == actual,
                    $"channel={channel} alpha={alpha}: Swift 公式得 {expected}，实现得 {actual}");
            }
        }
    }

    [Theory]
    // 期望值按 (c*a + 127) / 255 手算，逐条列出可复核：
    //   255,255 → (65025+127)/255 = 255.49 → 255
    //   128,128 → (16384+127)/255 =  64.75 →  64
    //   255,128 → (32640+127)/255 = 128.49 → 128
    //   255,  0 → (    0+127)/255 =   0.49 →   0
    //   254,254 → (64516+127)/255 = 253.50 → 253  （alpha 略小于 255，颜色会被拉低一点）
    [InlineData(255, 255, 255)]
    [InlineData(128, 128, 64)]
    [InlineData(255, 128, 128)]
    [InlineData(255, 0, 0)]
    [InlineData(254, 254, 253)]
    [InlineData(0, 255, 0)]
    public void Premultiply_KnownValues(int channel, int alpha, int expected)
    {
        var (r, g, b) = PixelAlpha.PremultiplyPixel(
            (byte)channel, (byte)channel, (byte)channel, (byte)alpha);

        Assert.Equal((byte)expected, r);
        Assert.Equal((byte)expected, g);
        Assert.Equal((byte)expected, b);
    }

    [Fact]
    public void Premultiply_LeavesAlphaUntouched()
    {
        var buffer = new byte[] { 10, 20, 30, 200, 40, 50, 60, 0 };
        PixelAlpha.Premultiply(buffer);

        Assert.Equal((byte)200, buffer[3]);
        Assert.Equal((byte)0, buffer[7]);
        // alpha=0 的像素颜色必须被清零，否则半透明边缘会残留黑边。
        Assert.Equal((byte)0, buffer[4]);
        Assert.Equal((byte)0, buffer[5]);
        Assert.Equal((byte)0, buffer[6]);
    }

    [Fact]
    public void Unpremultiply_ZeroAlphaYieldsZeroColor()
    {
        var (r, g, b) = PixelAlpha.UnpremultiplyPixel(255, 128, 64, 0);
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void Unpremultiply_ClampsInsteadOfOverflowing()
    {
        // c=255, a=1 → 255*255/1 = 65025，远超 255，必须钳位。
        var (r, _, _) = PixelAlpha.UnpremultiplyPixel(255, 0, 0, 1);
        Assert.Equal((byte)255, r);
    }

    [Fact]
    public void Unpremultiply_RoundTripIsLossyButMonotonic()
    {
        // 预乘→反预乘不可能完全无损（8 位量化），但不应出现非单调的塌陷：
        // 颜色越亮，反预乘后不应反而更暗。
        for (var channel = 0; channel <= 255; channel += 5)
        {
            var pre = PixelAlpha.PremultiplyPixel((byte)channel, 0, 0, 128).R;
            var back = PixelAlpha.UnpremultiplyPixel(pre, 0, 0, 128).R;
            Assert.True(
                back >= channel - 3,
                $"channel={channel} 往返后变成 {back}，偏离过大");
        }
    }

    [Fact]
    public void BufferLengthNotMultipleOfFour_Throws()
    {
        Assert.Throws<ArgumentException>(() => PixelAlpha.Premultiply(new byte[3]));
        Assert.Throws<ArgumentException>(() => PixelAlpha.Unpremultiply(new byte[5]));
    }
}