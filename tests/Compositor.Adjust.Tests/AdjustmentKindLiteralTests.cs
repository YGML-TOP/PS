using System.Text.Json;
using Xunit;

namespace Compositor.Adjust.Tests;

/// <summary>
/// 12 种调整层字面量的穷举断言。这是本模块最要紧的一组测试。
/// </summary>
/// <remarks>
/// 依据 <c>LayerAdjustment.swift:4-9</c> 与 <c>docs/project-format.md:29,33</c>。
/// 字面量写错 → Mac 版读到的调整层<b>静默失效且不报错</b>，故逐条锁定。
/// </remarks>
public class AdjustmentKindLiteralTests
{
    /// <summary>全部 12 种，期望字面量逐条照抄自 Swift 源码。</summary>
    public static TheoryData<AdjustmentKind, string> Expected
    {
        get
        {
            var d = new TheoryData<AdjustmentKind, string>();
            d.Add(AdjustmentKind.HueSaturation, "Hue/Saturation");
            d.Add(AdjustmentKind.Levels, "Levels");
            d.Add(AdjustmentKind.Curves, "Curves");
            d.Add(AdjustmentKind.Exposure, "Exposure");
            d.Add(AdjustmentKind.GradientMap, "Gradient Map");
            d.Add(AdjustmentKind.Grain, "Grain");
            d.Add(AdjustmentKind.AddNoise, "Add Noise");
            d.Add(AdjustmentKind.GaussianBlur, "Gaussian Blur");
            d.Add(AdjustmentKind.MotionBlur, "Motion Blur");
            d.Add(AdjustmentKind.Invert, "Invert");
            d.Add(AdjustmentKind.BlackWhite, "Black & White");
            d.Add(AdjustmentKind.ColorBalance, "Color Balance");
            return d;
        }
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public void ToLiteral_逐条匹配(AdjustmentKind kind, string literal)
        => Assert.Equal(literal, AdjustmentKindStrings.ToLiteral(kind));

    [Theory]
    [MemberData(nameof(Expected))]
    public void Parse_逐条反向匹配(AdjustmentKind kind, string literal)
        => Assert.Equal(kind, AdjustmentKindStrings.Parse(literal));

    [Fact]
    public void 字面量总数恰为12()
        => Assert.Equal(12, AdjustmentKindStrings.AllLiterals.Count);

    [Fact]
    public void 枚举成员总数恰为12()
        => Assert.Equal(12, Enum.GetValues<AdjustmentKind>().Length);

    [Fact]
    public void 字面量无重复()
        => Assert.Equal(12, AdjustmentKindStrings.AllLiterals.Distinct().Count());

    [Fact]
    public void 双向映射完全覆盖_每个枚举都能往返()
    {
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
        {
            var literal = AdjustmentKindStrings.ToLiteral(kind);
            Assert.Equal(kind, AdjustmentKindStrings.Parse(literal));
        }
    }

    // ── 两个最容易写错的字面量：单独再锁一遍 ─────────────────────────────────

    [Fact]
    public void HueSaturation_字面量带斜杠_不是_HueSaturation()
    {
        var literal = AdjustmentKindStrings.ToLiteral(AdjustmentKind.HueSaturation);
        Assert.Equal("Hue/Saturation", literal);
        Assert.Contains('/', literal);
        // 错误写法必须真的错：若有人把映射改成无斜杠，这条要立刻失败。
        Assert.NotEqual("HueSaturation", literal);
    }

    [Fact]
    public void BlackWhite_字面量带与号_不是_BlackWhite()
    {
        var literal = AdjustmentKindStrings.ToLiteral(AdjustmentKind.BlackWhite);
        Assert.Equal("Black & White", literal);
        Assert.Contains('&', literal);
        Assert.NotEqual("BlackWhite", literal);
    }

    [Fact]
    public void 字面量中的_与_号不需要XML转义()
    {
        // 项目内技术文档（Markdown/XML 风格）里的写法与 JSON 一致，均为原样字符。
        Assert.Equal("Black & White", AdjustmentKindStrings.ToLiteral(AdjustmentKind.BlackWhite));
        Assert.Equal("Hue/Saturation", AdjustmentKindStrings.ToLiteral(AdjustmentKind.HueSaturation));
    }

    // ── 未知字面量必须抛异常，绝不静默回退 ──────────────────────────────────

    [Theory]
    [InlineData("HueSaturation")]   // 漏斜杠
    [InlineData("BlackWhite")]      // 漏 & 号
    [InlineData("Hue Saturation")]  // 斜杠写成空格
    [InlineData("black & white")]   // 大小写错
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HSV")]
    [InlineData("不存在的调整")]
    public void Parse_未知字面量抛JsonException(string literal)
        => Assert.Throws<JsonException>(() => AdjustmentKindStrings.Parse(literal));

    [Fact]
    public void Parse_异常消息里含原始字面量()
    {
        var ex = Assert.Throws<JsonException>(() => AdjustmentKindStrings.Parse("HueSaturation"));
        Assert.Contains("HueSaturation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_未知字面量绝不回退到任何默认值()
    {
        // 静默回退是最坏的一种失败：用户的调整层无声失效且不留痕迹。
        // 逐个未知字面量确认抛出的类型，且**不返回任何合法枚举值**。
        foreach (var bad in new[] { "HueSaturation", "BlackWhite", "HSV", "", "Invert " })
        {
            var ex = Assert.Throws<JsonException>(() => AdjustmentKindStrings.Parse(bad));
            Assert.Contains(bad, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToLiteral_越界枚举抛ArgumentOutOfRange()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AdjustmentKindStrings.ToLiteral((AdjustmentKind)99));

    [Fact]
    public void Parse_区分大小写_不接受小写字面量()
        => Assert.Throws<JsonException>(() => AdjustmentKindStrings.Parse("levels"));
}
