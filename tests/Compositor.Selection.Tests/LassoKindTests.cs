namespace Compositor.Selection.Tests;

using Compositor.Selection;
using Xunit;

/// <summary>
/// <see cref="LassoKind"/> 与两个工具的选项集合的边界规格。
/// </summary>
/// <remarks>
/// 【照抄组】全部对应 Mac 版 <c>Document/Selection.swift:75-83</c>。
/// </remarks>
public class LassoKindTests
{
    /// <summary>照抄 Selection.swift:81 —— 套索只有 Freehand 与 Polygonal 两项。</summary>
    [Fact]
    public void LassoChoices_ContainsOnlyFreehandAndPolygonal()
    {
        Assert.Equal(
            new[] { LassoKind.Freehand, LassoKind.Polygonal },
            LassoChoices.All);
    }

    /// <summary>照抄 Selection.swift:82 —— 选框只有 Rectangle 与 Ellipse 两项。</summary>
    [Fact]
    public void MarqueeChoices_ContainsOnlyRectangleAndEllipse()
    {
        Assert.Equal(
            new[] { LassoKind.Rectangle, LassoKind.Ellipse },
            MarqueeChoices.All);
    }

    /// <summary>
    /// 照抄 Selection.swift:78 的注释 —— Rectangle/Ellipse <b>不在</b>套索的选项里。
    /// </summary>
    /// <remarks>
    /// 这条守的是「套索与选框是两个独立工具共用枚举」这个边界：
    /// 一旦把四项混进同一个下拉菜单，用户就能用套索画矩形，与 Mac 版行为不符。
    /// </remarks>
    [Fact]
    public void LassoChoices_DoesNotLeakMarqueeShapes()
    {
        Assert.DoesNotContain(LassoKind.Rectangle, LassoChoices.All);
        Assert.DoesNotContain(LassoKind.Ellipse, LassoChoices.All);
    }

    /// <summary>照抄 Selection.swift:78 的注释 —— Freehand/Polygonal <b>不在</b>选框的选项里。</summary>
    [Fact]
    public void MarqueeChoices_DoesNotLeakLassoShapes()
    {
        Assert.DoesNotContain(LassoKind.Freehand, MarqueeChoices.All);
        Assert.DoesNotContain(LassoKind.Polygonal, MarqueeChoices.All);
    }

    /// <summary>照抄 Selection.swift:81-82 —— 两个集合合起来恰好覆盖全部四个成员，不重不漏。</summary>
    [Fact]
    public void Choices_PartitionTheWholeEnum()
    {
        var union = LassoChoices.All.Concat(MarqueeChoices.All).OrderBy(k => k).ToArray();
        var all = Enum.GetValues<LassoKind>().OrderBy(k => k).ToArray();

        Assert.Equal(all, union);
    }
}