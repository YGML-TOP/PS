using Compositor.App.Canvas;
using Compositor.Core;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// <see cref="TestPattern"/> 的断言。
/// </summary>
/// <remarks>
/// 这一组测的是画布控件的<b>数据源</b>：控件把 <see cref="PixelBuffer"/> 的字节
/// 逐个搬到位图里，所以只要这里每个像素值都对，渲染通路上就没有"搬错"的可能。
/// </remarks>
public sealed class TestPatternTests
{
    [Fact]
    public void Create_尺寸与行距符合契约()
    {
        PixelBuffer buffer = TestPattern.Create(64, 32);

        Assert.Equal(64, buffer.Width);
        Assert.Equal(32, buffer.Height);
        Assert.Equal(64 * 4, buffer.Stride);
        Assert.Equal(64 * 4 * 32, buffer.Raw.Length);
    }

    [Fact]
    public void Create_两次调用结果逐字节相同()
    {
        PixelBuffer a = TestPattern.Create(96, 96);
        PixelBuffer b = TestPattern.Create(96, 96);

        Assert.True(a.Rgba.SequenceEqual(b.Rgba), "测试图像必须是确定性的，否则截图无法用于回归比对。");
    }

    [Fact]
    public void Create_非法尺寸抛ArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestPattern.Create(0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestPattern.Create(16, -1));
    }

    [Fact]
    public void 渐变区_左上角为纯蓝灰且不透明()
    {
        PixelBuffer buffer = TestPattern.CreateDefault();
        (byte r, byte g, byte b, byte a) = Pixel(buffer, 0, 0);

        // 左上：R 起点 0、G 起点 0、B 恒 128、A 不透明。
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(128, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void 渐变区_R沿X递增且G沿Y递增_证明没有把Y翻转()
    {
        PixelBuffer buffer = TestPattern.CreateDefault();
        int halfH = (buffer.Height / 2) + 1;

        // G 必须随 y 增大而增大——如果界面或控件把 Y 翻了，这一行会失败。
        byte gTop = Pixel(buffer, 0, 0).g;
        byte gBottomOfGradient = Pixel(buffer, 0, halfH - 1).g;
        Assert.True(
            gBottomOfGradient > gTop,
            $"G 沿 y 应递增，实际 top={gTop} bottom={gBottomOfGradient}。");

        // R 必须随 x 增大而增大。
        byte rLeft = Pixel(buffer, 0, 0).r;
        byte rRight = Pixel(buffer, buffer.Width - 1, 0).r;
        Assert.True(rRight > rLeft, $"R 沿 x 应递增，实际 left={rLeft} right={rRight}。");
    }

    [Fact]
    public void 棋盘格_偶数格全透明且奇数格完全不透明()
    {
        PixelBuffer buffer = TestPattern.CreateDefault();
        int y = buffer.Height - 1;
        int halfW = (buffer.Width / 2) + 1;

        Assert.True(y / TestPattern.CheckerCell % 2 == 1, "本用例假定最后一行落在奇数格上。");

        // x=0 落在第 0 格，(0 + 奇数) = 奇 → 不透明。
        Assert.Equal(255, Pixel(buffer, 0, y).a);

        // x=CheckerCell 落在第 1 格，(1 + 奇数) = 偶 → 完全透明。
        (byte r, byte g, byte b, byte a) = Pixel(buffer, TestPattern.CheckerCell, y);
        Assert.Equal(0, a);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
        Assert.True(TestPattern.CheckerCell < halfW, "棋盘格区域必须容得下一整格。");
    }

    [Fact]
    public void 色板_四个实色块与一个半透明块都真实存在()
    {
        PixelBuffer buffer = TestPattern.CreateDefault();
        int y = buffer.Height - 1;
        int halfW = (buffer.Width / 2) + 1;
        int swatchWidth = Math.Max(1, buffer.Width - halfW);

        // 取每段色块中心那一列的像素：band 段覆盖 [band*S, (band+1)*S)，
        // 取起点再加 1 列，避开与上一段的分界。
        (byte r, byte g, byte b, byte a) BandAt(int band)
        {
            int x = halfW + (((band * swatchWidth) / (TestPattern.SwatchCount + 1)) + 1);
            return Pixel(buffer, x, y);
        }

        Assert.Equal((byte)255, BandAt(0).r);   // 红
        Assert.Equal((byte)255, BandAt(1).g);   // 绿
        Assert.Equal((byte)255, BandAt(2).b);   // 蓝
        Assert.Equal((byte)255, BandAt(3).r);   // 黄
        Assert.Equal((byte)255, BandAt(3).g);   // 黄

        // 第 5 段是 50% 半透明的白色。
        (byte tr, byte tg, byte tb, byte ta) = BandAt(TestPattern.SwatchCount);
        Assert.Equal(128, ta);
        Assert.Equal(tr, tg);
        Assert.Equal(tg, tb);
    }

    [Fact]
    public void 半透明像素已按预乘语义存储()
    {
        PixelBuffer buffer = TestPattern.CreateDefault();
        int halfW = (buffer.Width / 2) + 1;
        int swatchWidth = Math.Max(1, buffer.Width - halfW);
        int x = halfW + (((TestPattern.SwatchCount * swatchWidth) / (TestPattern.SwatchCount + 1)) + 1);

        // 直通色是 (255,255,255,128)，预乘后应为 (128,128,128,128)。
        // 若这里读到 255，说明有人绕过 FromStraightRgba 直接写了直通值。
        (byte r, byte g, byte b, byte a) = Pixel(buffer, x, buffer.Height - 1);
        Assert.Equal(128, a);
        Assert.Equal(PixelBuffer.Premultiply(255, 128), r);
        Assert.Equal(r, g);
        Assert.Equal(r, b);
    }

    private static (byte r, byte g, byte b, byte a) Pixel(PixelBuffer buffer, int x, int y)
    {
        int i = (y * buffer.Stride) + (x * 4);
        return (buffer.Rgba[i], buffer.Rgba[i + 1], buffer.Rgba[i + 2], buffer.Rgba[i + 3]);
    }
}