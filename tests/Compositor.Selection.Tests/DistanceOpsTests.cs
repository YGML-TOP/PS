namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// 选区扩展/收缩（距离变换）测试。
/// </summary>
/// <remarks>
/// 【照抄组】的期望值搬运自 Mac 版 <c>CompositorTests/SelectionTests.swift:204-231</c>，行号标注在方法上。
/// ⚠️ 扩展/收缩属于本项目分类中的<b>「确定不等价」</b>项：
/// Mac 走 CoreGraphics 路径描边，本实现走光栅化后的欧氏距离变换。
/// 但 Mac 的断言全部落在<b>边界位置</b>上（包围盒与内外像素），
/// 这些位置两种算法<b>必然一致</b>，因此照抄仍然成立 —— 变的只会是圆角带的亚像素细节。
/// </remarks>
public class DistanceOpsTests
{
    /// <summary>在文档坐标下取覆盖度。</summary>
    /// <param name="plane">选区，可为 <see langword="null"/>。</param>
    /// <param name="x">文档 X。</param>
    /// <param name="y">文档 Y。</param>
    /// <returns>覆盖度 0–255。</returns>
    private static int Coverage(CoveragePlane? plane, int x, int y) =>
        plane?.CoverageAt(new DocPoint(x, y)) ?? 0;

    /// <summary>创建填充矩形选区。</summary>
    /// <param name="left">左边界。</param>
    /// <param name="top">上边界。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <returns>覆盖度为 255 的平面。</returns>
    private static CoveragePlane Box(double left, double top, int width, int height) =>
        CoveragePlane.CreateFilled(new DocRect(new DocPoint(left, top), new DocSize(width, height)), 255);

    /// <summary>
    /// 【照抄 · SelectionTests.swift:204-219】<c>expandAndContractGrowAndShrinkTheOutline</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// lasso(session, square(40, 40, 20))
    /// session.expandSelection(by: 5)
    /// #expect(abs(grown.minX - 35) &lt; 0.01 &amp;&amp; abs(grown.width - 30) &lt; 0.01)
    /// #expect(try coverage(session, 37, 50) == 255 &amp;&amp; coverage(session, 33, 50) == 0)
    /// session.contractSelection(by: 8)
    /// #expect(abs(shrunk.minX - 43) &lt; 0.01 &amp;&amp; abs(shrunk.width - 14) &lt; 0.01)
    /// </code>
    /// 手算：原方框 [40,60)。扩张 5 → [35,65)，minX=35 宽 30 ✓；
    /// 像素 (37,50) 距原边缘 3 ≤ 5 → 255，(33,50) 距 7 > 5 → 0 ✓；
    /// 再收缩 8 → [43,57)，minX=43 宽 14 ✓。
    /// Mac 里的撤销步骤不在本工程范围。
    /// </remarks>
    [Fact]
    public void ExpandAndContractGrowAndShrinkTheOutline()
    {
        var original = Box(40, 40, 20, 20);

        var grown = DistanceOps.Expand(original, 5);
        Assert.True(Math.Abs(grown.Bounds.Left - 35) < 0.01, $"minX={grown.Bounds.Left}");
        Assert.True(Math.Abs(grown.Bounds.Size.Width - 30) < 0.01, $"width={grown.Bounds.Size.Width}");
        Assert.Equal(255, Coverage(grown, 37, 50));
        Assert.Equal(0, Coverage(grown, 33, 50));

        var shrunk = DistanceOps.Contract(grown, 8);
        Assert.True(Math.Abs(shrunk.Bounds.Left - 43) < 0.01, $"minX={shrunk.Bounds.Left}");
        Assert.True(Math.Abs(shrunk.Bounds.Size.Width - 14) < 0.01, $"width={shrunk.Bounds.Size.Width}");
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:221-231】<c>expandStaysOnCanvasAndContractCanEmptyTheSelection</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// session.selectAll()
    /// session.expandSelection(by: 10)
    /// #expect(session.selection?.path.boundingBoxOfPath == CGRect(x: 0, y: 0, width: 100, height: 100))
    /// session.contractSelection(by: 10) // Pulls in from the canvas edges too.
    /// #expect(try coverage(session, 5, 50) == 0 &amp;&amp; coverage(session, 50, 50) == 255)
    /// session.contractSelection(by: 45)
    /// #expect(session.selection?.isEmpty == true)
    /// </code>
    /// 三条断言全部照抄。第二条"收缩也从画布边缘往里收"是 Mac 的语义：
    /// 收缩对整个形状做距离变换，不区分"形状边缘"与"画布边缘"。
    /// 第三条收缩后得到的是<b>显式空选区</b>（非 null），与 <c>isEmpty</c> 对应。
    /// </remarks>
    [Fact]
    public void ExpandStaysOnCanvasAndContractCanEmptyTheSelection()
    {
        DocRect canvas = new(new DocPoint(0, 0), new DocSize(100, 100));
        var full = Box(0, 0, 100, 100);

        var grown = DistanceOps.Expand(full, 10, canvas);
        Assert.Equal(0, grown.Bounds.Left);
        Assert.Equal(0, grown.Bounds.Top);
        Assert.Equal(100, grown.Bounds.Size.Width);
        Assert.Equal(100, grown.Bounds.Size.Height);

        var shrunk = DistanceOps.Contract(grown, 10);
        Assert.Equal(0, Coverage(shrunk, 5, 50));
        Assert.Equal(255, Coverage(shrunk, 50, 50));

        var emptied = DistanceOps.Contract(shrunk, 45);
        Assert.NotNull(emptied);
        Assert.False(emptied!.HasContent());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【推导组】
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 【推导】欧氏距离变换必须是<b>精确</b>的，不能用倒距离近似。
    /// </summary>
    /// <remarks>
    /// 常见的两趟倒距离近似（chamfer）在斜向会差约 1 像素。
    /// 本测试取对角线上的点验证精确欧氏距离：
    /// 对 (0,0) 处的一个像素，距离 ≤ 1.414 的像素应当出现（斜向邻接），
    /// 而距离 > 1.414 的不会出现。
    /// </remarks>
    [Fact]
    public void ExpandUsesExactEuclideanDistance()
    {
        var dot = Box(0, 0, 1, 1);
        var expanded = DistanceOps.Expand(dot, 1);

        // 原点：距离 0。
        Assert.Equal(255, Coverage(expanded, 0, 0));

        // 四邻接：距离恰为 1，应当被扩张 1 覆盖。
        Assert.Equal(255, Coverage(expanded, 1, 0));
        Assert.Equal(255, Coverage(expanded, 0, 1));

        // 斜向：距离 √2 ≈ 1.414 > 1，不应被覆盖。
        Assert.Equal(0, Coverage(expanded, 1, 1));

        // 扩张到 2 时斜向就进入范围，而更远的斜向仍在外。
        // ⚠️ 这里<b>不能</b>写成 Contract(Expand(dot, 2), 1) 再断言 (1,1)：
        // 先扩 2 再收 1，净效果是半径 1 的圆盘，而 (1,1) 的距离是 √2 ≈ 1.414 > 1，
        // 它本就不该在里面 —— 那样的期望值是错的。
        var wide = DistanceOps.Expand(dot, 2);
        Assert.Equal(255, Coverage(wide, 1, 1));   // √2 ≈ 1.414 ≤ 2
        Assert.Equal(0, Coverage(wide, 3, 3));     // √18 ≈ 4.24 > 2
    }

    /// <summary>
    /// 【推导】扩张量越界必须抛异常，对齐 <c>Selection.swift:334</c> 的
    /// <c>abs(delta) &lt;= 500</c> 守卫。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void OutOfRangeAmountThrows(int amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DistanceOps.Expand(Box(0, 0, 10, 10), amount));

    /// <summary>
    /// 【推导】带符号增量：正数扩张、负数收缩，语义与 Mac 的 <c>resizeSelection(by:)</c> 一致。
    /// </summary>
    [Fact]
    public void SignedDeltaExpandsOrContracts()
    {
        var original = Box(40, 40, 20, 20);

        var expanded = DistanceOps.Resize(original, 5);
        Assert.Equal(35, expanded.Bounds.Left);

        var contracted = DistanceOps.Resize(original, -5);
        Assert.Equal(45, contracted.Bounds.Left);
        Assert.Equal(10, contracted.Bounds.Size.Width);

        // 0 是非法增量，对齐 Mac 的 <c>delta != 0</c> 守卫。
        Assert.Throws<ArgumentOutOfRangeException>(() => DistanceOps.Resize(original, 0));
    }

    /// <summary>
    /// 【推导】收缩一个空选区仍是空 —— 距离变换不存在参考点时不得凭空造出内容。
    /// </summary>
    [Fact]
    public void ContractingAnEmptyPlaneStaysEmpty()
    {
        var empty = CoveragePlane.CreateZero(new DocRect(new DocPoint(0, 0), new DocSize(20, 20)));

        var contracted = DistanceOps.Contract(empty, 3);

        Assert.False(contracted.HasContent());
    }
}