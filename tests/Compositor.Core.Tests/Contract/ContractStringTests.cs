using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests.Contract;

/// <summary>
/// 🔴 <see cref="BlendModeStrings"/> 与 <see cref="SamplingStrings"/> 的<b>互通回归锁</b>。
/// </summary>
/// <remarks>
/// <para><b>为什么这是全项目最关键的几个测试</b>：<c>.comp</c> 互通是本项目存在的唯一理由，
/// 而图层混合模式与采样档位在 manifest 里都是<b>字符串</b>。v1.0 契约曾把它们写成整数枚举，
/// 一旦按那个实现，Mac 版读到的图层<b>全部落到 Normal</b>，而两边都编译通过、单元测试全绿。</para>
///
/// <para><b>本文件锁三件事，缺一不可：</b></para>
/// <list type="number">
/// <item><b>字面量逐字正确</b>——带空格、带括号的那些（<c>"Color Burn"</c>、<c>"Linear Dodge (Add)"</c>、
/// <c>"High quality"</c>）一个都不能被"规范化"掉。</item>
/// <item><b>24 个模式一个不多一个不少</b>，且 <see cref="BlendModeGroups"/> 把它们<b>不重不漏</b>地分完组
/// ——漏一个就是某个模式在菜单里点不到。</item>
/// <item><b>大小写敏感</b>——与 Swift 的 <c>enum: String</c> Codable 行为一致。</item>
/// </list>
///
/// <para>期望值逐字取自原项目源码：<c>Document/LayerAppearance.swift:4-13</c> 与
/// <c>Document/LayerTransform.swift:4-7</c>，<b>不是从 C# 实现跑出来的</b>。
/// 若从实现跑出来，直译错误会被一起锁死，等于用错误实现给自己盖章。</para>
/// </remarks>
public sealed class ContractStringTests
{
    // ─────────────────── 混合模式：24 个字面量逐字锁定 ───────────────────

    /// <summary>
    /// 24 个混合模式的 <c>.comp</c> 字面量。顺序即 <see cref="BlendMode"/> 的枚举值顺序。
    /// </summary>
    public static TheoryData<BlendMode, string> AllBlendModes => new()
    {
        { BlendMode.Normal, "Normal" },
        { BlendMode.Darken, "Darken" },
        { BlendMode.Multiply, "Multiply" },
        { BlendMode.ColorBurn, "Color Burn" },
        { BlendMode.LinearBurn, "Linear Burn" },
        { BlendMode.Lighten, "Lighten" },
        { BlendMode.Screen, "Screen" },
        { BlendMode.ColorDodge, "Color Dodge" },
        { BlendMode.LinearDodge, "Linear Dodge (Add)" },
        { BlendMode.Overlay, "Overlay" },
        { BlendMode.SoftLight, "Soft Light" },
        { BlendMode.HardLight, "Hard Light" },
        { BlendMode.VividLight, "Vivid Light" },
        { BlendMode.LinearLight, "Linear Light" },
        { BlendMode.PinLight, "Pin Light" },
        { BlendMode.HardMix, "Hard Mix" },
        { BlendMode.Difference, "Difference" },
        { BlendMode.Exclusion, "Exclusion" },
        { BlendMode.Subtract, "Subtract" },
        { BlendMode.Divide, "Divide" },
        { BlendMode.Hue, "Hue" },
        { BlendMode.Saturation, "Saturation" },
        { BlendMode.Color, "Color" },
        { BlendMode.Luminosity, "Luminosity" },
    };

    [Theory]
    [MemberData(nameof(AllBlendModes))]
    public void BlendModeStrings_ToLiteral_是逐字正确的(BlendMode mode, string expected)
        => Assert.Equal(expected, BlendModeStrings.ToLiteral(mode));

    [Theory]
    [MemberData(nameof(AllBlendModes))]
    public void BlendModeStrings_Parse_能读回每一个字面量(BlendMode mode, string literal)
        => Assert.Equal(mode, BlendModeStrings.Parse(literal));

    /// <summary>
    /// 枚举里必须正好 24 个成员，且<b>没有</b> <c>DarkerColor</c> / <c>LighterColor</c>。
    /// </summary>
    /// <remarks>
    /// 依据 <c>LayerAppearance.swift:4-13</c>：源码注释明写这两个模式"故意不做，两个框架都不实现"。
    /// v1.0 契约凭空多出它们，是 AI-1 指控的第 2 条，已在 v1.1 删除。
    /// </remarks>
    [Fact]
    public void BlendMode_正好24个且不含DarkerColor与LighterColor()
    {
        var values = Enum.GetValues<BlendMode>();
        Assert.Equal(24, values.Length);
        Assert.DoesNotContain("DarkerColor", values.Select(v => v.ToString()));
        Assert.DoesNotContain("LighterColor", values.Select(v => v.ToString()));
    }

    /// <summary>
    /// 每个枚举值都必须是连续整数 0..23 —— 索引即 <see cref="BlendModeStrings"/> 的字面量表下标。
    /// </summary>
    /// <remarks>
    /// 两者靠"索引对应"绑定。若哪天有人在中间插入成员而忘了同步字面量表，
    /// 序列化会整体错位，而<b>不会</b>抛异常 —— 只会静默把所有图层写成错误的模式。
    /// </remarks>
    [Fact]
    public void BlendMode_枚举值必须从0连续递增()
    {
        var values = Enum.GetValues<BlendMode>();
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(i, (int)values[i]);
        }
    }

    [Fact]
    public void BlendModeStrings_未知字面量抛FormatException()
        => Assert.Throws<FormatException>(() => BlendModeStrings.Parse("Darker Color"));

    /// <summary>
    /// 大小写敏感：<c>"color burn"</c> 必须是失败而不是被"友好地"纠正。
    /// </summary>
    /// <remarks>
    /// Swift 的 <c>enum LayerBlendMode: String, Codable</c> 用的是精确匹配，
    /// 大小写不符会解码失败。Windows 端若"宽容"地匹配，会把本该失败的工程静默加载成错误数据。
    /// </remarks>
    [Fact]
    public void BlendModeStrings_解析区分大小写()
    {
        Assert.Throws<FormatException>(() => BlendModeStrings.Parse("color burn"));
        Assert.Throws<FormatException>(() => BlendModeStrings.Parse("COLOR BURN"));
    }

    [Fact]
    public void BlendModeStrings_null抛ArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => BlendModeStrings.Parse(null!));

    [Fact]
    public void BlendModeStrings_越界枚举抛ArgumentOutOfRangeException()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BlendModeStrings.ToLiteral((BlendMode)99));

    // ─────────────────── Photoshop 菜单分组：不重不漏 ───────────────────

    /// <summary>
    /// 🔴 分组必须把 24 个模式<b>恰好各出现一次</b>：不重（重了就有个模式在菜单里出现两遍）
    /// 不漏（漏了就在菜单里点不到，只能靠手改 .comp）。
    /// </summary>
    [Fact]
    public void BlendModeGroups_恰好覆盖全部24个模式且不重复()
    {
        var all = Enum.GetValues<BlendMode>().ToHashSet();
        var seen = new HashSet<BlendMode>();
        var flattened = new List<BlendMode>();

        foreach (var group in BlendModeGroups.InPhotoshopOrder)
        {
            foreach (var mode in group)
            {
                Assert.True(
                    seen.Add(mode),
                    $"混合模式 {mode} 在分组里出现了不止一次。");
                flattened.Add(mode);
            }
        }

        Assert.Equal(24, flattened.Count);
        Assert.True(all.SetEquals(seen), $"分组漏掉了：{string.Join(", ", all.Except(seen))}");
    }

    /// <summary>
    /// 分组顺序与成员逐字取自 <c>LayerAppearance.swift:17-24</c>，UI 层靠它画 6 条分隔线。
    /// </summary>
    [Fact]
    public void BlendModeGroups_分组结构与原项目一致()
    {
        var groups = BlendModeGroups.InPhotoshopOrder;

        Assert.Equal(6, groups.Count);
        Assert.Equal(new[] { BlendMode.Normal }, groups[0]);
        Assert.Equal(
            new[] { BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn },
            groups[1]);
        Assert.Equal(
            new[] { BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodge },
            groups[2]);
        Assert.Equal(
            new[]
            {
                BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight,
                BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix,
            },
            groups[3]);
        Assert.Equal(
            new[] { BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide },
            groups[4]);
        Assert.Equal(
            new[] { BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity },
            groups[5]);
    }

    /// <summary>
    /// 🔴 每次访问返回<b>全新</b>的分组结构，调用方的误写不会污染其它调用方。
    /// </summary>
    /// <remarks>
    /// 为什么必须这样：<c>IReadOnlyList&lt;BlendMode[]&gt;</c> 只挡住外层，内层数组仍可被索引赋值 ——
    /// <c>BlendModeGroups.InPhotoshopOrder[0][0] = BlendMode.Multiply;</c> 是<b>能编译通过</b>的。
    /// 若实现返回缓存的同一份，任何一个 AI-6 的 UI 代码一次误写，全进程菜单分组就错了。
    /// </remarks>
    [Fact]
    public void BlendModeGroups_每次访问返回新副本_调用方改写不污染他人()
    {
        var first = BlendModeGroups.InPhotoshopOrder;
        var second = BlendModeGroups.InPhotoshopOrder;
        Assert.NotSame(first, second);

        // 模拟调用方犯的错：改写自己拿到的内层数组
        first[0][0] = BlendMode.Multiply;

        Assert.Equal(BlendMode.Normal, second[0][0]);
        Assert.Equal(BlendMode.Normal, BlendModeGroups.InPhotoshopOrder[0][0]);

        // 同时确认第 1 组始终只有 Normal 一个成员（没被写坏）
        Assert.Single(BlendModeGroups.InPhotoshopOrder[0]);
    }

    // ─────────────────── 采样档位：只有 3 档，且 "High quality" 带空格 ───────────────────

    [Theory]
    [InlineData(SamplingQuality.Nearest, "Nearest")]
    [InlineData(SamplingQuality.Smooth, "Smooth")]
    [InlineData(SamplingQuality.HighQuality, "High quality")]
    public void SamplingStrings_字面量逐字正确(SamplingQuality quality, string expected)
        => Assert.Equal(expected, SamplingStrings.ToLiteral(quality));

    [Theory]
    [InlineData(SamplingQuality.Nearest, "Nearest")]
    [InlineData(SamplingQuality.Smooth, "Smooth")]
    [InlineData(SamplingQuality.HighQuality, "High quality")]
    public void SamplingStrings_解析能读回(SamplingQuality quality, string literal)
        => Assert.Equal(quality, SamplingStrings.Parse(literal));

    /// <summary>
    /// 🔴 只有 3 档。契约 v1.0 的 <c>{Low,Medium,High}</c> 与"四档 nearest/bilinear/bicubic/Lanczos"
    /// 都与 Mac 版不符——<b>Lanczos 在 Mac 版根本不存在这个概念</b>。
    /// </summary>
    [Fact]
    public void SamplingQuality_正好3档()
    {
        var values = Enum.GetValues<SamplingQuality>();
        Assert.Equal(3, values.Length);
        Assert.Equal(new[] { SamplingQuality.Nearest, SamplingQuality.Smooth, SamplingQuality.HighQuality }, values);
    }

    /// <summary>
    /// "High quality" <b>中间有空格</b>。这是最容易"顺手规范化"掉的一个字面量。
    /// </summary>
    [Fact]
    public void SamplingStrings_HighQuality带空格()
    {
        var literal = SamplingStrings.ToLiteral(SamplingQuality.HighQuality);
        Assert.Contains(' ', literal);
        Assert.Equal("High quality", literal);

        // 常见的错误写法都必须失败，而不是被悄悄接受
        Assert.Throws<FormatException>(() => SamplingStrings.Parse("HighQuality"));
        Assert.Throws<FormatException>(() => SamplingStrings.Parse("high_quality"));
        Assert.Throws<FormatException>(() => SamplingStrings.Parse("high quality"));
    }

    [Fact]
    public void SamplingStrings_未知字面量抛FormatException()
        => Assert.Throws<FormatException>(() => SamplingStrings.Parse("Lanczos"));

    [Fact]
    public void SamplingStrings_枚举值从0连续递增()
    {
        var values = Enum.GetValues<SamplingQuality>();
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(i, (int)values[i]);
        }
    }
}
