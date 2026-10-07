namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// Marquee（矩形/椭圆选框）几何测试。
/// </summary>
/// <remarks>
/// 🔴 <b>本文件分成两组断言，来源不同，不得混淆：</b>
/// <list type="bullet">
/// <item><description>
/// <b>【照抄组】</b>标记的方法：期望值与容差<b>原样搬运自 Mac 版 Swift 测试</b>，
/// 并在方法名注释里给出 Swift 文件与行号。这些数值是参照实现的实测行为，
/// <b>不得为了让测试通过而修改</b>。
/// </description></item>
/// <item><description>
/// <b>【推导组】</b>标记的方法：期望值由 Mac 版源码的数学定义
/// （<c>DragBox.rect</c>，<c>Selection.swift:93-105</c>）在 C# 侧重新推导得到。
/// 这类断言<b>不是</b> Mac 的实测输出，因此单独分组，绝不冒充照抄。
/// </description></item>
/// </list>
/// </remarks>
public class MarqueeTests
{
    /// <summary>在文档坐标下取覆盖度，对应 Mac 测试的 <c>coverage(_:_:_:)</c> 助手（<c>SelectionTests.swift:22-30</c>）。</summary>
    /// <param name="plane">选区，可为 <see langword="null"/> 表示"无选区"。</param>
    /// <param name="x">文档 X。</param>
    /// <param name="y">文档 Y。</param>
    /// <returns>覆盖度 0–255；无选区返回 0。</returns>
    private static int Coverage(CoveragePlane? plane, int x, int y) =>
        plane?.CoverageAt(new DocPoint(x, y)) ?? 0;

    /// <summary>正方形套索点序列，对应 Mac 测试的 <c>square(_:_:_:)</c> 助手（<c>SelectionTests.swift:18-20</c>）。</summary>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    /// <param name="size">边长。</param>
    /// <returns>四个顶点。</returns>
    private static DocPoint[] Square(double x, double y, double size) =>
    [
        new DocPoint(x, y),
        new DocPoint(x + size, y),
        new DocPoint(x + size, y + size),
        new DocPoint(x, y + size),
    ];

    /// <summary>用套索与当前选区组合，对应 Mac 测试的 <c>lasso(_:_:mode:)</c> 助手（<c>SelectionTests.swift:13-17</c>）。</summary>
    /// <param name="current">当前选区。</param>
    /// <param name="mode">组合模式。</param>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    /// <param name="size">边长。</param>
    /// <returns>组合后的选区。</returns>
    private static CoveragePlane? Lasso(CoveragePlane? current, SelectionCombineMode mode, double x, double y, double size)
    {
        var incoming = LassoRaster.Rasterize(Square(x, y, size));
        if (incoming is null)
        {
            // 套索围不出闭合区域时，Mac 版在 replace 模式下取消选择（Selection.swift:225：
            // `if draft.mode == .replace { deselect() }`），其余模式保持原选区不变。
            return mode == SelectionCombineMode.Replace ? null : current;
        }

        return BooleanOps.Combine(mode, current, incoming);
    }

    /// <summary>创建矩形选区，断言其非空。</summary>
    /// <param name="ax">起点 X。</param>
    /// <param name="ay">起点 Y。</param>
    /// <param name="bx">终点 X。</param>
    /// <param name="by">终点 Y。</param>
    /// <param name="square">是否强制正方形。</param>
    /// <param name="fromCenter">是否以锚点为中心生长。</param>
    /// <returns>非空的矩形覆盖度平面。</returns>
    private static CoveragePlane Rect(double ax, double ay, double bx, double by, bool square = false, bool fromCenter = false)
    {
        var plane = MarqueeShapes.Rectangle(new DocPoint(ax, ay), new DocPoint(bx, by), square, fromCenter);
        Assert.NotNull(plane);
        return plane!;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】以下期望值搬运自 Mac 版 CompositorTests/SelectionTests.swift
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 【照抄 · SelectionTests.swift:319-332】<c>marqueeDrawsWholePixelRectanglesInAnyDirection</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// marquee(session, from: CGPoint(x: 60.4, y: 70.6), to: CGPoint(x: 20.2, y: 30.3))
    /// #expect(session.selection?.path.boundingBoxOfPath == CGRect(x: 20, y: 30, width: 40, height: 41))
    /// #expect(try coverage(session, 20, 30) == 255 &amp;&amp; coverage(session, 19, 30) == 0)
    /// </code>
    /// 这条测试同时锁死了三件事：<b>远离零取整</b>、<b>任意方向的拖拽</b>、<b>整像素矩形无抗锯齿</b>。
    /// 手工验算：anchor=(60.4,70.6)→(60,71)，point=(20.2,30.3)→(20,30)，
    /// dx=-40、dy=-41，左上角=min(60,20)/min(71,30)=(20,30)，宽高=40×41 ✓
    /// </remarks>
    [Fact]
    public void MarqueeDrawsWholePixelRectanglesInAnyDirection()
    {
        var first = MarqueeShapes.Rectangle(new DocPoint(60.4, 70.6), new DocPoint(20.2, 30.3), square: false, fromCenter: false);

        Assert.NotNull(first);
        Assert.Equal(20, first!.Bounds.Left);
        Assert.Equal(30, first.Bounds.Top);
        Assert.Equal(40, first.Bounds.Size.Width);
        Assert.Equal(41, first.Bounds.Size.Height);

        Assert.Equal(255, Coverage(first, 20, 30));
        Assert.Equal(0, Coverage(first, 19, 30));

        var added = BooleanOps.Combine(SelectionCombineMode.Add, first,
            Rect(80, 80, 90, 90));
        Assert.Equal(255, Coverage(added, 85, 85));
        Assert.Equal(255, Coverage(added, 40, 50));

        var subtracted = BooleanOps.Combine(SelectionCombineMode.Subtract, added,
            Rect(30, 40, 50, 60));
        Assert.Equal(0, Coverage(subtracted, 40, 50));
        Assert.Equal(255, Coverage(subtracted, 25, 35));
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:334-346】<c>marqueeShiftMakesSquaresAndCenteredDragsGrowFromTheAnchor</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文的三条包围盒断言全部照抄。第三条同时压住 <c>square</c> 与 <c>fromCenter</c> 两个修饰键：
    /// <code>
    /// #expect(… == CGRect(x: 10, y: 10, width: 30, height: 30))   // (10,10)→(40,20) square
    /// #expect(… == CGRect(x: 40, y: 45, width: 20, height: 10))   // (50,50)→(60,55) fromCenter
    /// #expect(… == CGRect(x: 42, y: 42, width: 16, height: 16))   // (50,50)→(45,58) square+fromCenter
    /// </code>
    /// Mac 原文最后一条断言工具身份（<c>NavigationTool.marquee.isSelectionTool</c>），
    /// 属于工具注册表，不在本工程范围内，故未照抄。
    /// </remarks>
    [Fact]
    public void MarqueeShiftMakesSquaresAndCenteredDragsGrowFromTheAnchor()
    {
        var squareBox = MarqueeShapes.Rectangle(new DocPoint(10, 10), new DocPoint(40, 20), square: true, fromCenter: false);
        Assert.NotNull(squareBox);
        Assert.Equal(10, squareBox!.Bounds.Left);
        Assert.Equal(10, squareBox.Bounds.Top);
        Assert.Equal(30, squareBox.Bounds.Size.Width);
        Assert.Equal(30, squareBox.Bounds.Size.Height);

        var centered = MarqueeShapes.Rectangle(new DocPoint(50, 50), new DocPoint(60, 55), square: false, fromCenter: true);
        Assert.NotNull(centered);
        Assert.Equal(40, centered!.Bounds.Left);
        Assert.Equal(45, centered.Bounds.Top);
        Assert.Equal(20, centered.Bounds.Size.Width);
        Assert.Equal(10, centered.Bounds.Size.Height);

        var both = MarqueeShapes.Rectangle(new DocPoint(50, 50), new DocPoint(45, 58), square: true, fromCenter: true);
        Assert.NotNull(both);
        Assert.Equal(42, both!.Bounds.Left);
        Assert.Equal(42, both.Bounds.Top);
        Assert.Equal(16, both.Bounds.Size.Width);
        Assert.Equal(16, both.Bounds.Size.Height);
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:348-366】<c>marqueeEllipseSelectsAnOvalInItsBoxAndShiftMakesACircle</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// #expect(abs(bounds.minX - 10) &lt; 0.5 &amp;&amp; abs(bounds.maxX - 70) &lt; 0.5 &amp;&amp; …)
    /// #expect(try coverage(session, 40, 40) == 255) // the middle
    /// #expect(try coverage(session, 11, 21) == 0)   // the box's corner lies outside the oval
    /// </code>
    /// 第三条断言（Shift 拖出正圆）在本测试里体现为 <c>Assert.Equal</c> 宽高严格相等 ——
    /// Mac 用的是 <c>abs(circle.width - circle.height) &lt; 0.5</c>，
    /// 而本实现两侧都是整数，<b>严格相等比 Mac 的容差断言更强</b>，不构成放宽容差。
    /// Mac 最后的 <c>toggleMarqueeKind</c> 属工具状态切换，不在本工程范围。
    /// </remarks>
    [Fact]
    public void MarqueeEllipseSelectsAnOvalInItsBoxAndShiftMakesACircle()
    {
        var oval = MarqueeShapes.Ellipse(new DocPoint(10, 20), new DocPoint(70, 60), square: false, fromCenter: false);

        Assert.NotNull(oval);
        Assert.True(Math.Abs(oval!.Bounds.Left - 10) < 0.5, $"minX={oval.Bounds.Left}");
        Assert.True(Math.Abs(oval.Bounds.Right - 70) < 0.5, $"maxX={oval.Bounds.Right}");
        Assert.True(Math.Abs(oval.Bounds.Top - 20) < 0.5, $"minY={oval.Bounds.Top}");
        Assert.True(Math.Abs(oval.Bounds.Bottom - 60) < 0.5, $"maxY={oval.Bounds.Bottom}");

        Assert.Equal(255, Coverage(oval, 40, 40));
        Assert.Equal(0, Coverage(oval, 11, 21));

        var circle = MarqueeShapes.Ellipse(new DocPoint(5, 5), new DocPoint(45, 25), square: true, fromCenter: false);
        Assert.NotNull(circle);
        Assert.Equal(circle!.Bounds.Size.Width, circle.Bounds.Size.Height);
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:32-42】<c>replaceAddAndSubtractCombineOutlines</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文的四个断言全部照抄，坐标与数值一字未改。
    /// 第四条尤其重要：它验证 <b>减选是"当前选区减去新形状"</b>，
    /// 而不是反向 —— 第 41 行 <c>coverage(70,20)==255 &amp;&amp; coverage(70,70)==0 &amp;&amp; coverage(15,15)==0</c>
    /// 只有在正确方向下才成立。
    /// </remarks>
    [Fact]
    public void ReplaceAddAndSubtractCombineOutlines()
    {
        var sel = Lasso(null, SelectionCombineMode.Replace, 10, 10, 40);
        Assert.Equal(255, Coverage(sel, 30, 30));
        Assert.Equal(0, Coverage(sel, 70, 70));

        sel = Lasso(sel, SelectionCombineMode.Add, 50, 50, 40);
        Assert.Equal(255, Coverage(sel, 30, 30));
        Assert.Equal(255, Coverage(sel, 70, 70));

        sel = Lasso(sel, SelectionCombineMode.Subtract, 20, 20, 20);
        Assert.Equal(0, Coverage(sel, 30, 30));
        Assert.Equal(255, Coverage(sel, 15, 15));

        sel = Lasso(sel, SelectionCombineMode.Replace, 60, 10, 20);
        Assert.Equal(255, Coverage(sel, 70, 20));
        Assert.Equal(0, Coverage(sel, 70, 70));
        Assert.Equal(0, Coverage(sel, 15, 15));
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:55-66】<c>emptySelectionIsDistinctFromNoSelection</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// lasso(session, square(0, 0, 50), mode: .subtract)
    /// #expect(session.selection == nil) // Nothing to subtract from.
    /// …
    /// lasso(session, square(0, 0, 60), mode: .subtract)
    /// #expect(selection.isEmpty)
    /// #expect(try coverage(session, 20, 20) == 0)
    /// </code>
    /// 这里锁定的是本项目最容易搞错的一处语义：<b>从"无选区"减去 → 仍然无选区（null）</b>；
    /// 而从<b>有选区</b>里减掉一个覆盖它的方框 → 得到<b>显式空选区</b>（非 null、全 0）。
    /// Mac 用 <c>session.selection == nil</c> 区分，本实现用 <see cref="CoveragePlane"/> 的可空引用表达同一状态。
    /// </remarks>
    [Fact]
    public void EmptySelectionIsDistinctFromNoSelection()
    {
        // 从无选区减去：什么都没有发生，仍然是"无选区"，不是"空选区"。
        var sel = Lasso(null, SelectionCombineMode.Subtract, 0, 0, 50);
        Assert.Null(sel);

        sel = Lasso(null, SelectionCombineMode.Replace, 10, 10, 20);
        Assert.NotNull(sel);

        // 用一个完全覆盖它的方框去减：得到显式空选区（非 null）。
        sel = Lasso(sel, SelectionCombineMode.Subtract, 0, 0, 60);
        Assert.NotNull(sel);
        Assert.False(sel!.HasContent());
        Assert.Equal(0, Coverage(sel, 20, 20));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【推导组】以下期望值由 Selection.swift 的数学定义推导，非 Mac 实测输出
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 【推导】<c>DragBox.rect</c> 向右拖拽的取整口径必须是<b>远离零</b>，不是银行家舍入。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>Selection.swift:95</c> 写的是 <c>point.x.rounded()</c>，
    /// 而 Swift 的 <c>Double.rounded()</c> 默认规则是 <c>toNearestOrAwayFromZero</c>；
    /// C# 的 <c>Math.Round(double)</c> 默认是 <c>ToEven</c>。两者在 <c>±0.5</c> 处相反。
    /// <para>
    /// <b>这条测试的价值在于它能杀死一个真实缺陷</b>：若有人把
    /// <c>Math.Round(value)</c> 直接抄过来不加 <c>MidpointRounding</c>，
    /// <c>2.5</c> 会取成 2 而不是 3，本测试立刻失败。
    /// </para>
    /// <para>
    /// ⚠️ <b>只能用宽度断言正向取整。</b>
    /// <c>DragBox.Rect</c> 的宽高取绝对值（<c>abs(dx)</c>，<c>Selection.swift:103</c>），
    /// 恒为非负，<b>无法表达"负数如何取整"</b>。负向由
    /// <see cref="DragBoxRoundsAwayFromZeroToTheLeft"/> 用左边界单独覆盖。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2.5, 3.0)]
    [InlineData(3.5, 4.0)]
    [InlineData(20.2, 20.0)]
    [InlineData(30.3, 30.0)]
    public void DragBoxRoundsAwayFromZeroLikeSwift(double input, double expectedWidth)
    {
        DocRect rect = DragBox.Rect(new DocPoint(0, 0), new DocPoint(input, 5), square: false, fromCenter: false);

        Assert.Equal(expectedWidth, rect.Size.Width);
    }

    /// <summary>
    /// 【推导】向左拖拽时取整同样是远离零，左边界为负。
    /// </summary>
    /// <remarks>
    /// 推导依据同上。此处必须看 <c>Bounds.Left</c> 而不是宽度 ——
    /// <c>abs(dx)</c> 把符号吞掉了，<c>-3.5</c> 与 <c>3.5</c> 的宽度都是 4，
    /// 只有左边界才能区分 <c>Math.Round(-3.5, AwayFromZero) = -4</c>
    /// 与错误的银行家舍入 <c>Math.Round(-3.5) = -4</c>（此项恰好相同）
    /// 之外的正负号丢失。
    /// </remarks>
    [Theory]
    [InlineData(-2.5, -3.0)]
    [InlineData(-3.5, -4.0)]
    public void DragBoxRoundsAwayFromZeroToTheLeft(double input, double expectedLeft)
    {
        DocRect rect = DragBox.Rect(new DocPoint(0, 0), new DocPoint(input, 5), square: false, fromCenter: false);

        Assert.Equal(expectedLeft, rect.Left);
    }

    /// <summary>
    /// 【推导】零面积的拖拽不产生选区，对应 <c>Selection.swift:224</c> 的
    /// <c>bounds.width &gt; 0 &amp;&amp; bounds.height &gt; 0</c> 守卫。
    /// </summary>
    /// <param name="anchorX">起点 X。</param>
    /// <param name="anchorY">起点 Y。</param>
    /// <param name="pointX">终点 X。</param>
    /// <param name="pointY">终点 Y。</param>
    [Theory]
    [InlineData(5, 5, 5, 5)]
    [InlineData(5, 5, 20, 5)]
    [InlineData(5, 5, 5, 20)]
    public void ZeroAreaDragProducesNoSelection(double anchorX, double anchorY, double pointX, double pointY)
    {
        Assert.Null(MarqueeShapes.Rectangle(new DocPoint(anchorX, anchorY), new DocPoint(pointX, pointY), false, false));
        Assert.Null(MarqueeShapes.Ellipse(new DocPoint(anchorX, anchorY), new DocPoint(pointX, pointY), false, false));
    }
}