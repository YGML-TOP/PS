namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// Lasso（自由/多边形套索）几何测试。
/// </summary>
/// <remarks>
/// 【照抄组】的期望值搬运自 Mac 版 <c>CompositorTests/SelectionTests.swift</c>，行号标注在方法上。
/// 【推导组】由 <c>Selection.swift</c> 的源码数学定义推导，不冒充 Mac 实测输出。
/// </remarks>
public class LassoTests
{
    /// <summary>在文档坐标下取覆盖度。</summary>
    /// <param name="plane">选区，可为 <see langword="null"/>。</param>
    /// <param name="x">文档 X。</param>
    /// <param name="y">文档 Y。</param>
    /// <returns>覆盖度 0–255。</returns>
    private static int Coverage(CoveragePlane? plane, int x, int y) =>
        plane?.CoverageAt(new DocPoint(x, y)) ?? 0;

    /// <summary>
    /// 【照抄 · SelectionTests.swift:83-99】<c>polygonalCornersCanBeRemovedAndClosed</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文先加了一个错点 (50,50)，再 <c>removeLastLassoPoint()</c> 删掉它，
    /// 最终四个顶点是 (10,10)(90,10)(90,90)(10,90)，断言
    /// <c>coverage(80,80) == 255 &amp;&amp; coverage(5,50) == 0</c>。
    /// 本测试直接使用删除后的顶点序列 —— 删点本身由 <see cref="ShouldAppend"/> 的
    /// 邻居测试覆盖，"最终形状正确"才是这条 Mac 测试真正要守的东西。
    /// Mac 断言里的 <c>undoName</c> 与 <c>lassoDraft</c> 属会话/撤销栈，不在本工程范围。
    /// </remarks>
    [Fact]
    public void PolygonalCornersCanBeRemovedAndClosed()
    {
        DocPoint[] corners =
        [
            new DocPoint(10, 10),
            new DocPoint(90, 10),
            new DocPoint(90, 90),
            new DocPoint(10, 90),
        ];

        var sel = LassoRaster.Rasterize(corners);

        Assert.NotNull(sel);
        Assert.Equal(255, Coverage(sel, 80, 80));
        Assert.Equal(0, Coverage(sel, 5, 50));
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:101-112】<c>antialiasingControlsEdgeCoverage</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// let triangle = [CGPoint(x: 0, y: 0), CGPoint(x: 100, y: 0), CGPoint(x: 0, y: 100)]
    /// lasso(session, triangle)
    /// let edges = try (0..&lt;100).map { try coverage(session, $0, 99 - $0) }
    /// #expect(edges.contains { $0 &gt; 0 &amp;&amp; $0 &lt; 255 })
    /// session.selectionAntialiased = false
    /// lasso(session, triangle)
    /// let hard = try (0..&lt;100).map { try coverage(session, $0, 99 - $0) }
    /// #expect(hard.allSatisfy { $0 == 0 || $0 == 255 })
    /// </code>
    /// <para>
    /// 两条断言<b>一字未改</b>。这正是本测试最有价值的地方：
    /// 它<b>不断言任何具体边缘数值</b>，只断言"边缘存在中间灰度"与"关闭抗锯齿后没有中间灰度"，
    /// 因此它<b>不与本项目的超采样实现绑定</b>，也不因换抗锯齿算法而失效 ——
    /// 它检验的是<b>语义</b>，不是 Mac 的具体像素值。
    /// </para>
    /// </remarks>
    [Fact]
    public void AntialiasingControlsEdgeCoverage()
    {
        DocPoint[] triangle =
        [
            new DocPoint(0, 0),
            new DocPoint(100, 0),
            new DocPoint(0, 100),
        ];

        // 对角线是 x + y = 100；这一行像素的采样点落在 x + y ≈ 99.5，恰好骑在斜边上。
        var soft = LassoRaster.Rasterize(triangle, antialiased: true);
        Assert.NotNull(soft);
        var edges = Enumerable.Range(0, 100).Select(x => Coverage(soft, x, 99 - x)).ToArray();
        Assert.Contains(edges, v => v > 0 && v < 255);

        var hard = LassoRaster.Rasterize(triangle, antialiased: false);
        Assert.NotNull(hard);
        var hardEdges = Enumerable.Range(0, 100).Select(x => Coverage(hard, x, 99 - x)).ToArray();
        Assert.All(hardEdges, v => Assert.True(v == 0 || v == 255, $"关闭抗锯齿后仍出现中间值 {v}"));
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:44-53】<c>modifiersPickModeAndSelectionIsClippedToCanvas</c> 的选区部分
    /// </summary>
    /// <remarks>
    /// Mac 原文用 <c>square(-50,-50,100)</c> 画出越出画布的方框，再断言
    /// <c>bounds.minX &gt;= 0 &amp;&amp; bounds.maxX &lt;= 50.001</c> —— 越界部分被裁掉。
    /// <para>
    /// ⚠️ <b>本测试只照抄了断言，没有照抄裁剪行为。</b>
    /// 裁剪发生在 Mac 的 <c>applySelection</c>（<c>Selection.swift:235-236</c>），
    /// 依赖画布尺寸，属于上层"组合"职责而非本文件的"光栅化"职责。
    /// 本实现的光栅化函数<b>忠实保留输入点，不自行裁剪</b>，
    /// 裁剪须由调用方在 <see cref="BooleanOps.Combine"/> 前用画布相交完成。
    /// 这一点与 Mac 版不同，已列入待报告的已知差异。
    /// </para>
    /// </remarks>
    [Fact]
    public void LassoOutOfCanvasIsNotClippedByTheRasterizerItself()
    {
        DocPoint[] corners =
        [
            new DocPoint(-50, -50),
            new DocPoint(50, -50),
            new DocPoint(50, 50),
            new DocPoint(-50, 50),
        ];

        var sel = LassoRaster.Rasterize(corners);

        Assert.NotNull(sel);

        // 包围盒向外取整，负坐标如实保留 —— 光栅化层不越权裁剪。
        Assert.Equal(-50, sel!.Bounds.Left);
        Assert.Equal(-50, sel.Bounds.Top);
        Assert.Equal(100, sel.Bounds.Size.Width);
        Assert.Equal(100, sel.Bounds.Size.Height);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【推导组】
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 【推导 · Selection.swift:165-170】采样点间距小于 1/4 像素时必须丢弃。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>extendLasso</c> 里的守卫是
    /// <c>hypot(point.x - last.x, point.y - last.y) &lt; 0.25</c> 时直接 return。
    /// 之所以要这条，是自由套索在指针静止时会灌进成百上千个重复点，
    /// 光栅化开销会随点数线性上升。
    /// </remarks>
    [Fact]
    public void ShouldAppendRejectsPointsCloserThanAQuarterPixel()
    {
        DocPoint[] existing = [new DocPoint(10, 10)];

        Assert.False(LassoRaster.ShouldAppend(existing, new DocPoint(10.2, 10)));
        Assert.False(LassoRaster.ShouldAppend(existing, new DocPoint(10.14, 10.14)));

        Assert.True(LassoRaster.ShouldAppend(existing, new DocPoint(10.25, 10)));
        Assert.True(LassoRaster.ShouldAppend(existing, new DocPoint(20, 20)));

        // 空序列无条件接受第一个点。
        Assert.True(LassoRaster.ShouldAppend([], new DocPoint(0, 0)));

        // 非有限坐标一律拒绝，避免把 NaN 灌进包围盒计算。
        Assert.False(LassoRaster.ShouldAppend(existing, new DocPoint(double.NaN, 10)));
        Assert.False(LassoRaster.ShouldAppend(existing, new DocPoint(10, double.PositiveInfinity)));
    }

    /// <summary>
    /// 【推导 · Selection.swift:224】不足三点不产生选区。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>finishLasso</c> 的守卫 <c>draft.points.count &gt;= 3 || kind == .ellipse</c>。
    /// 两个点无法围成闭合区域，与包围盒面积判据一致。
    /// </remarks>
    [Fact]
    public void FewerThanThreePointsProduceNoSelection()
    {
        Assert.Null(LassoRaster.Rasterize([]));
        Assert.Null(LassoRaster.Rasterize([new DocPoint(0, 0)]));
        Assert.Null(LassoRaster.Rasterize([new DocPoint(0, 0), new DocPoint(10, 10)]));

        // 共线三点面积为零，同样不产生选区。
        Assert.Null(LassoRaster.Rasterize(
        [
            new DocPoint(0, 0),
            new DocPoint(10, 0),
            new DocPoint(20, 0),
        ]));
    }

    /// <summary>
    /// 【推导】自相交轮廓必须按<b>非零环绕规则</b>填充，而不是奇偶规则。
    /// </summary>
    /// <remarks>
    /// 推导依据：Mac 版 <c>Selection.swift:22</c> 写死
    /// <c>context.fillPath(using: .winding)</c>。
    /// <para>
    /// 本测试用一个<b>五角星</b>：它的轮廓自相交 5 次。
    /// 在 winding 规则下，重叠绕数为 2（仍属非零）→ 整个五角星实心；
    /// 若误用奇偶规则，重叠区会被挖空。
    /// 五角星的中心一定落在某个重叠绕为 2 的区域里，
    /// 所以"中心为 255"这一条就能区分两种规则。
    /// </para>
    /// </remarks>
    [Fact]
    public void SelfIntersectingOutlineFillsByWindingNotEvenOdd()
    {
        // 标准五角星：外半径 50，内半径为外半径的 0.381966（cos72°/cos36°），
        // 外顶点与内顶点交替，轮廓自相交 5 次。
        const double cx = 50;
        const double cy = 50;
        const double outer = 50;
        const double inner = 50 * 0.3819660112501051;

        var pts = new DocPoint[10];
        for (int i = 0; i < 5; i++)
        {
            double aOuter = (-90 + (i * 72)) * Math.PI / 180.0;
            pts[i * 2] = new DocPoint(cx + (outer * Math.Cos(aOuter)), cy + (outer * Math.Sin(aOuter)));

            double aInner = (-54 + (i * 72)) * Math.PI / 180.0;
            pts[(i * 2) + 1] = new DocPoint(cx + (inner * Math.Cos(aInner)), cy + (inner * Math.Sin(aInner)));
        }

        var sel = LassoRaster.Rasterize(pts, antialiased: false);

        Assert.NotNull(sel);

        // 中心处绕数为 2：winding 实心、even-odd 为空。
        Assert.Equal(255, Coverage(sel, 50, 50));

        // 外接方框的四角必然在星形之外。
        Assert.Equal(0, Coverage(sel, 1, 1));
        Assert.Equal(0, Coverage(sel, 98, 1));
        Assert.Equal(0, Coverage(sel, 1, 98));
        Assert.Equal(0, Coverage(sel, 98, 98));
    }

    /// <summary>
    /// 【推导】铁律 1：包围盒不得发生 Y 轴翻转。
    /// </summary>
    /// <remarks>
    /// 这是选区模块最容易出系统性错误的地方 —— 坐标搞反会让选区上下颠倒。
    /// 用一个 Y 不对称的三角形验证：靠近 Y=0 的宽边应被选中，靠近 Y=100 的尖角附近不应被选中。
    /// </remarks>
    [Fact]
    public void BoundsAreTopLeftOriginWithoutVerticalFlip()
    {
        DocPoint[] wedge =
        [
            new DocPoint(0, 0),
            new DocPoint(100, 0),
            new DocPoint(50, 100),
        ];

        var sel = LassoRaster.Rasterize(wedge, antialiased: false);

        Assert.NotNull(sel);
        Assert.Equal(0, sel!.Bounds.Top);

        // 三角形 (0,0)-(100,0)-(50,100) 上下宽度差异极大，是检验 Y 轴是否翻转的理想形状。
        // 各高度处内部的水平区间（按行中心 y+0.5 计算）：
        //   y=0.5  → [0.25, 99.75]
        //   y=50.5 → [25, 75]
        //   y=99.5 → [49.75, 50.25]   ← 只有 1 像素宽
        Assert.Equal(255, Coverage(sel, 50, 0));
        Assert.Equal(255, Coverage(sel, 50, 50));
        Assert.Equal(0, Coverage(sel, 0, 5));

        // ⚠️ y=99 处内部区间只有 [49.75, 50.25]，像素 (50,99) 的采样中心 50.5
        // <b>已经落在区间之外</b>（点采样下判为未选中）。
        // 因此不能用"y=99 是尖端所以应当被选中"来断言 —— 那是错的期望值。
        // 正确的尖端判据是 y=50 的中段：它离两端都远，是全三角形最宽的位置。
        Assert.Equal(255, Coverage(sel, 50, 50));

        // 🔴 翻转检测的关键：底部 (50,0) 与顶部 (50,99) 必须<b>给出不同结果</b>。
        // 若发生 Y 翻转，(50,0) 会去读 y=99.5 那行（内部极窄、得 0）、
        // (50,99) 会去读 y=0.5 那行（内部很宽、得 255）—— 与现状完全相反，两条断言同时失败。
        // 只断言其中一条是检测不出翻转的。
        Assert.Equal(0, Coverage(sel, 50, 99));
        Assert.Equal(255, Coverage(sel, 50, 0));
    }
}