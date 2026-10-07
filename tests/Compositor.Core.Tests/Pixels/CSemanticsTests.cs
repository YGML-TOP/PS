using Compositor.Core.Pixels;
using Xunit;

namespace Compositor.Core.Tests.Pixels;

/// <summary>
/// <see cref="CSemantics"/> 的回归测试。
/// </summary>
/// <remarks>
/// <para><b>为什么这个文件最重要</b>：整套直译能在数值上对齐黄金样本，完全依赖这一层
/// 承载 C 与 C# 的三处语义差异。银行家舍入、窄化抛异常、无符号溢出——任何一条回退，
/// 都会让大量像素悄悄差 1，且<b>编译器和测试都不会报</b>。</para>
/// <para>本类依赖 <c>Directory.Build.props</c> 的 <c>CheckForOverflowUnderflow=false</c>
/// 与 <c>InternalsVisibleTo("Compositor.Core.Tests")</c>。两者任一被改，本文件会失败——
/// 这正是它们应有的效果。</para>
/// </remarks>
public sealed class CSemanticsTests
{
    // ─────────────────── 舍入：C 的远离零 vs .NET 的银行家舍入 ───────────────────

    /// <summary>
    /// 这几个值是银行家舍入的<b>全部分歧点</b>。
    /// <c>Math.Round(0.5)</c>=0 而 <c>lround(0.5)</c>=1；<c>Math.Round(1.5)</c>=2 两边一致；
    /// <c>Math.Round(2.5)</c>=2 而 <c>lround(2.5)</c>=3。
    /// </summary>
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 3)]
    [InlineData(3.5, 4)]
    [InlineData(-0.5, -1)]
    [InlineData(-2.5, -3)]
    public void LRound_RoundsHalfAwayFromZero(double input, int expected)
        => Assert.Equal(expected, CSemantics.LRound(input));

    /// <summary>
    /// <b>反向对照</b>：证明这些值确实会与 C# 默认的 <c>Math.Round</c> 不同。
    /// 若哪天有人把 CSemantics 改成直接调 <c>Math.Round</c>，本测试立刻失败。
    /// </summary>
    [Fact]
    public void LRound_ActuallyDiffersFromBankerRounding()
    {
        Assert.NotEqual((int)Math.Round(0.5, MidpointRounding.ToEven), CSemantics.LRound(0.5));
        Assert.NotEqual((int)Math.Round(2.5, MidpointRounding.ToEven), CSemantics.LRound(2.5));
        Assert.NotEqual((int)Math.Round(-0.5, MidpointRounding.ToEven), CSemantics.LRound(-0.5));
    }

    [Theory]
    [InlineData(0.4999, 0)]
    [InlineData(0.5001, 1)]
    [InlineData(254.5, 255)]
    [InlineData(254.4999, 254)]
    public void LRound_NonHalfValuesMatchTruncation(double input, int expected)
        => Assert.Equal(expected, CSemantics.LRound(input));

    [Theory]
    [InlineData(0.5f, 1)]
    [InlineData(2.5f, 3)]
    [InlineData(-0.5f, -1)]
    [InlineData(127.4999f, 127)]
    public void LRoundF_RoundsHalfAwayFromZero(float input, int expected)
        => Assert.Equal(expected, CSemantics.LRoundF(input));

    [Theory]
    [InlineData(2.5, 3.0)]
    [InlineData(-2.5, -3.0)]
    [InlineData(2.4, 2.0)]
    public void Round_MatchesC_Round(double input, double expected)
        => Assert.Equal(expected, CSemantics.Round(input), 10);

    [Theory]
    [InlineData(2.5f, 3.0f)]
    [InlineData(-2.5f, -3.0f)]
    [InlineData(2.4f, 2.0f)]
    public void RoundF_MatchesC_Roundf(float input, float expected)
        => Assert.Equal(expected, CSemantics.RoundF(input));

    // ─────────────────── 窄化转换：C 的未定义行为 → 饱和 ───────────────────

    [Fact]
    public void U8_InRange_TruncatesTowardZero()
    {
        // C 的 (uint8_t) 是向零截断，不是四舍五入
        Assert.Equal(0, CSemantics.U8(0.9));
        Assert.Equal(1, CSemantics.U8(1.9));
        Assert.Equal(127, CSemantics.U8(127.999));
    }

    [Fact]
    public void U8_OutOfRange_Saturates()
    {
        // C 里这是未定义行为（x86 回绕为 0），C# 会抛 OverflowException；这里取饱和。
        Assert.Equal(255, CSemantics.U8(255.5));
        Assert.Equal(255, CSemantics.U8(1000.0));
        Assert.Equal(0, CSemantics.U8(-0.5));
        Assert.Equal(0, CSemantics.U8(-1000.0));
    }

    /// <summary>
    /// 直译代码里 <c>(byte)</c> 强转出现约百处。只要值在 [0,255]，饱和分支永不命中；
    /// 本测试钉住「正常路径行为与 C 完全一致」，饱和只是兜底。
    /// </summary>
    [Fact]
    public void U8_NormalRange_IsIdenticalToCTruncation()
    {
        for (int i = 0; i <= 255; i++)
        {
            Assert.Equal((byte)i, CSemantics.U8(i));
            Assert.Equal((byte)i, CSemantics.U8F(i));
        }
    }

    [Fact]
    public void U8F_OutOfRange_Saturates()
    {
        Assert.Equal(255, CSemantics.U8F(255.5f));
        Assert.Equal(0, CSemantics.U8F(-1.0f));
        Assert.Equal(255, CSemantics.U8F(1e9f));
    }

    // ─────────────────── 整数预乘 ───────────────────

    /// <summary>
    /// C 的 <c>(color * a + 127) / 255</c>：127 偏置 + 整数除法（向零截断）。
    /// 验证 C# 整数 <c>/</c> 与 C 的整数除法行为一致。
    /// </summary>
    [Theory]
    [InlineData((byte)200, 255, (byte)200)]  // a=255 时近似恒等
    [InlineData((byte)128, 255, (byte)128)]
    [InlineData((byte)0, 0, (byte)0)]        // a=0 时结果为 0
    public void Premul_MatchesCIntegerDivision(byte channel, int alpha, byte expected)
        => Assert.Equal(expected, CSemantics.Premul(channel, alpha, 127));

    /// <summary>
    /// <b>回归锁</b>：<c>Premul</c> 必须走整数除法。
    /// 若有人改成浮点再四舍五入，这几个值会变。
    /// </summary>
    [Fact]
    public void Premul_TruncatesRatherThanRounds()
    {
        // (100 * 128 + 127) / 255 = 128127 / 255 = 502.45 → 截断 502；四舍五入会是 502（本例相同）
        Assert.Equal((byte)((100 * 128 + 127) / 255), CSemantics.Premul(100, 128, 127));
        // 找一个截断与四舍五入结果不同的值
        int channel = 10, alpha = 10;
        int truncated = (channel * alpha + 127) / 255;
        int rounded = (int)Math.Round((channel * alpha + 127) / 255.0, MidpointRounding.AwayFromZero);
        Assert.Equal((byte)truncated, CSemantics.Premul((byte)channel, alpha, 127));
        Assert.NotEqual(truncated, rounded); // 证明这两个口径确实不同，测试才有意义
    }
}