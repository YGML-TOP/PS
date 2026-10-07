namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// <see cref="LassoSession"/> 拖拽状态机的行为规格。
/// </summary>
/// <remarks>
/// 【照抄组】全部对应 Mac 版 <c>Document/Selection.swift:141-180</c> 与 <c>:211-231</c> 的具体行号。
/// </remarks>
public class LassoSessionTests
{
    private static DocPoint P(double x, double y) => new(x, y);

    private static byte At(CoveragePlane? plane, int x, int y) =>
        plane is null ? (byte)0 : plane.CoverageAt(new DocPoint(x, y));

    private static LassoDraft? StartMarquee(DocPoint at, LassoKind kind = LassoKind.Rectangle) =>
        LassoSession.Begin(isMarquee: true, at, SelectionCombineMode.Replace, kind, LassoKind.Freehand);

    private static LassoDraft? StartLasso(DocPoint at) =>
        LassoSession.Begin(isMarquee: false, at, SelectionCombineMode.Replace, LassoKind.Rectangle, LassoKind.Freehand);

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:141-150  beginLasso
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :145 —— 选框的起始角<b>取整到整像素</b>，用 <b>AwayFromZero</b>。</summary>
    /// <remarks>
    /// 手算：<c>Math.Round(10.5, AwayFromZero) = 11</c>，<c>Math.Round(3.5, AwayFromZero) = 4</c>。
    /// <b>决定性</b>：C# 的 <c>Math.Round</c> 默认 <c>ToEven</c> 会给 10 和 4。
    /// </remarks>
    [Fact]
    public void Begin_MarqueeRoundsAnchorAwayFromZero()
    {
        var draft = StartMarquee(P(10.5, 3.5));

        Assert.NotNull(draft);
        Assert.Equal(P(11, 4), draft!.Anchor);
        Assert.Equal(P(11, 4), draft.Points[0]);
    }

    /// <summary>照抄 :148 —— <b>套索的起始点不取整</b>，保留原始坐标。</summary>
    [Fact]
    public void Begin_LassoKeepsRawPoint()
    {
        var draft = StartLasso(P(10.5, 3.5));

        Assert.NotNull(draft);
        Assert.Equal(P(10.5, 3.5), draft!.Points[0]);
        Assert.Null(draft.Anchor);
    }

    /// <summary>照抄 :143/:156 的 guard —— 坐标非有限时不建草稿。</summary>
    [Fact]
    public void Begin_RejectsNonFiniteCoordinates()
    {
        Assert.Null(LassoSession.Begin(true, P(double.NaN, 0), SelectionCombineMode.Replace, LassoKind.Rectangle, LassoKind.Freehand));
        Assert.Null(LassoSession.Begin(false, P(0, double.PositiveInfinity), SelectionCombineMode.Replace, LassoKind.Rectangle, LassoKind.Freehand));
    }

    /// <summary>照抄 :146/:148 —— 草稿的 kind 取自<b>所属工具</b>的模式，不是另一个工具的。</summary>
    [Fact]
    public void Begin_TakesKindFromTheOwningTool()
    {
        var marquee = LassoSession.Begin(true, P(5, 5), SelectionCombineMode.Add, LassoKind.Ellipse, LassoKind.Polygonal);
        var lasso = LassoSession.Begin(false, P(5, 5), SelectionCombineMode.Add, LassoKind.Ellipse, LassoKind.Polygonal);

        Assert.Equal(LassoKind.Ellipse, marquee!.Kind);
        Assert.Equal(LassoKind.Polygonal, lasso!.Kind);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:155-162  dragMarquee
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :159-160 —— 四角按「左上、右上、右下、左下」固定顺序。</summary>
    /// <remarks>
    /// 手算：anchor (10,10)、point (20,18)，不取正方形、不从中心生长。
    /// <c>DragBox.Rect</c> 得 left=10, top=10, w=10, h=8，于是
    /// minX=10, minY=10, maxX=20, maxY=18 → 四角 (10,10) (20,10) (20,18) (10,18)。
    /// </remarks>
    [Fact]
    public void DragMarquee_ProducesFourCornersInFixedOrder()
    {
        var draft = StartMarquee(P(10, 10));
        var dragged = LassoSession.DragMarquee(draft, P(20, 18), square: false, fromCenter: false);

        Assert.NotNull(dragged);
        Assert.Equal(
            new[] { P(10, 10), P(20, 10), P(20, 18), P(10, 18) },
            dragged!.Points);
    }

    /// <summary>照抄 :156 的 guard —— 对<b>套索</b>草稿调用本方法必须原样返回，不得破坏草稿。</summary>
    [Fact]
    public void DragMarquee_OnLassoDraft_ReturnsUnchanged()
    {
        var draft = StartLasso(P(3, 3));
        var extended = LassoSession.Extend(draft, P(9, 9));

        var result = LassoSession.DragMarquee(extended, P(40, 40), square: false, fromCenter: false);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Points.Count);
        Assert.Equal(extended!.Points, result.Points);
    }

    /// <summary>照抄 :156 的 guard —— 坐标非有限时原样返回。</summary>
    [Fact]
    public void DragMarquee_RejectsNonFinitePoint()
    {
        var draft = LassoSession.DragMarquee(StartMarquee(P(10, 10)), P(20, 18), false, false);
        var result = LassoSession.DragMarquee(draft, P(double.NaN, 18), false, false);

        Assert.NotNull(result);
        Assert.Equal(draft!.Points, result!.Points);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:165-170  extendLasso
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :167 —— 距上一点不足 0.25 的候选点被跳过。</summary>
    [Fact]
    public void Extend_SkipsPointsCloserThanQuarterPixel()
    {
        var draft = StartLasso(P(10, 10));

        var after = LassoSession.Extend(draft, P(10.24, 10));   // 距离 0.24 < 0.25

        Assert.Single(after!.Points);
    }

    /// <summary>照抄 :167 —— 判据是<b>严格小于</b> 0.25，恰好等于 0.25 时要收下。</summary>
    [Fact]
    public void Extend_AcceptsPointAtExactlyQuarterPixel()
    {
        var draft = StartLasso(P(10, 10));

        var after = LassoSession.Extend(draft, P(10.25, 10));   // 距离 0.25，不满足 < 0.25

        Assert.Equal(2, after!.Points.Count);
        Assert.Equal(P(10.25, 10), after!.Points[1]);
    }

    /// <summary>照抄 :167 —— 距离是<b>欧氏</b>距离，斜向同样按 0.25 判。</summary>
    /// <remarks>
    /// 手算：<c>hypot(0.2, 0.2) = √0.08 ≈ 0.2828 &gt; 0.25</c> → <b>应收下</b>。
    /// 若误写成逐轴比较（两个轴都 &lt; 0.25 才跳过），这点会被错误地丢弃。
    /// </remarks>
    [Fact]
    public void Extend_UsesEuclideanDistanceNotPerAxis()
    {
        var draft = StartLasso(P(10, 10));

        var after = LassoSession.Extend(draft, P(10.2, 10.2));

        Assert.Equal(2, after!.Points.Count);
    }

    /// <summary>照抄 :166 的 guard —— 坐标非有限时原样返回。</summary>
    [Fact]
    public void Extend_RejectsNonFinitePoint()
    {
        var draft = StartLasso(P(10, 10));

        var after = LassoSession.Extend(draft, P(double.NaN, 12));

        Assert.Single(after!.Points);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:172/:174-180  光标与删点
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :172 —— 橡皮筋光标可设可清，且<b>不影响轮廓点</b>。</summary>
    [Fact]
    public void MoveCursor_SetsAndClearsWithoutTouchingPoints()
    {
        var draft = StartLasso(P(10, 10));

        var withCursor = LassoSession.MoveCursor(draft, P(14, 14));
        var cleared = LassoSession.MoveCursor(withCursor, null);

        Assert.Equal(P(14, 14), withCursor!.Cursor);
        Assert.Null(cleared!.Cursor);
        Assert.Equal(draft!.Points, cleared.Points);
    }

    /// <summary>照抄 :174-178 —— 删掉最后一个点；<b>删光就返回 null</b>（草稿作废）。</summary>
    [Fact]
    public void RemoveLast_NullsDraftWhenPointsRunOut()
    {
        var draft = StartLasso(P(10, 10));
        Assert.Single(draft!.Points);

        Assert.Null(LassoSession.RemoveLast(draft));           // 只剩 1 个，删光 → null
    }

    /// <summary>照抄 :177 —— 还有剩余点时保留草稿。</summary>
    [Fact]
    public void RemoveLast_KeepsDraftWhilePointsRemain()
    {
        var draft = LassoSession.Extend(StartLasso(P(10, 10)), P(20, 10));
        Assert.Equal(2, draft!.Points.Count);

        var after = LassoSession.RemoveLast(draft);

        Assert.NotNull(after);
        Assert.Single(after!.Points);
        Assert.Equal(P(10, 10), after.Points[0]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:211-231  finishLasso 的分派与退化
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :212 —— 没有草稿时什么也不做。</summary>
    [Fact]
    public void Finish_WithNoDraft_IsCancelled()
    {
        var result = LassoSession.Finish(null);

        Assert.Equal(LassoFinishOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Coverage);
    }

    /// <summary>照抄 :229-230 —— 撤销步骤名按 kind 分派。</summary>
    [Theory]
    [InlineData(LassoKind.Freehand, "Lasso")]
    [InlineData(LassoKind.Polygonal, "Polygonal Lasso")]
    [InlineData(LassoKind.Ellipse, "Elliptical Marquee")]
    [InlineData(LassoKind.Rectangle, "Rectangular Marquee")]
    public void Finish_NamesUndoStepByKind(LassoKind kind, string expected)
    {
        var draft = StartMarquee(P(10, 10), kind);
        var dragged = LassoSession.DragMarquee(draft, P(20, 18), false, false);

        var result = LassoSession.Finish(dragged);

        Assert.Equal(expected, result.UndoName);
    }

    /// <summary>照抄 :215 —— 椭圆<b>且恰好四点</b>才走 <c>addEllipse</c> 分支。</summary>
    /// <remarks>
    /// 拖到 (20,18) 后矩形是 (10,10,10,8)，椭圆中心 (15,14)、半径 5×4。
    /// 点 (19,17)：<c>((19-15)/5)² + ((17-14)/4)² = 0.64 + 0.5625 = 1.2025 &gt; 1</c> → <b>在椭圆外</b>。
    /// </remarks>
    [Fact]
    public void Finish_FourPointEllipseUsesTheEllipseBranch()
    {
        var draft = LassoSession.DragMarquee(StartMarquee(P(10, 10), LassoKind.Ellipse), P(20, 18), false, false);
        Assert.Equal(4, draft!.Points.Count);

        var result = LassoSession.Finish(draft, antialiased: false);

        Assert.Equal(LassoFinishOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Coverage);
        Assert.Equal(new DocRect(new DocPoint(10, 10), new DocSize(10, 8)), result.Coverage!.Bounds);
        Assert.Equal(0, At(result.Coverage, 19, 17));   // 椭圆外
        Assert.Equal(255, At(result.Coverage, 15, 14)); // 椭圆中心
    }

    /// <summary>照抄 :215 —— <b>椭圆但点数不是 4</b> 时退回 <c>addLines</c>，画出来是折线不是椭圆。</summary>
    /// <remarks>
    /// 同样拖到 (20,18)，但只加到第三个点就收手。
    /// 三个点 (10,10) (20,10) (20,18) 构成直角三角形，斜边从 (10,10) 到 (20,18)，
    /// 方程 <c>y = 10 + 0.8(x - 10)</c>。点 (19,17)：<c>17 &lt; 10 + 0.8×9 = 17.2</c> → <b>在三角形内</b>。
    /// 同一个点在四点多边形分支里是 0，两者<b>有明确差异</b> ——
    /// 这正是 <c>points.count == 4</c> 这个条件起作用的可观测证据。
    /// </remarks>
    [Fact]
    public void Finish_EllipseWithWrongPointCountFallsBackToPolyline()
    {
        var draft = StartMarquee(P(10, 10), LassoKind.Ellipse);
        var three = LassoSession.Extend(draft, P(20, 10));
        three = LassoSession.Extend(three, P(20, 18));
        Assert.Equal(3, three!.Points.Count);

        var triangle = LassoSession.Finish(three, antialiased: false);
        Assert.Equal(LassoFinishOutcome.Completed, triangle.Outcome);
        Assert.Equal(255, At(triangle.Coverage, 19, 17));

        // 对照：同样三点若 kind 声明为 Ellipse 但恰好凑够四点，走的就是椭圆分支。
        var four = LassoSession.Extend(three, P(10, 18));
        var ellipse = LassoSession.Finish(four, antialiased: false);
        Assert.Equal(0, At(ellipse.Coverage, 19, 17));
    }

    /// <summary>照抄 :224/:225 —— 退化（点数不够）且是 Replace → <b>取消选择</b>。</summary>
    [Fact]
    public void Finish_DegenerateInReplaceMode_Deselects()
    {
        var draft = LassoSession.Extend(StartLasso(P(10, 10)), P(20, 10));
        Assert.Equal(2, draft!.Points.Count);   // < 3，退化

        var result = LassoSession.Finish(draft);

        Assert.Equal(LassoFinishOutcome.DegenerateDeselected, result.Outcome);
        Assert.Null(result.Coverage);
    }

    /// <summary>照抄 :224/:226 —— 退化但不是 Replace → <b>什么都不做</b>，保持原选区。</summary>
    [Theory]
    [InlineData(SelectionCombineMode.Add)]
    [InlineData(SelectionCombineMode.Subtract)]
    public void Finish_DegenerateInNonReplaceMode_LeavesSelectionAlone(SelectionCombineMode mode)
    {
        var draft = LassoSession.Begin(
            isMarquee: false, P(10, 10), mode, LassoKind.Rectangle, LassoKind.Freehand);
        draft = LassoSession.Extend(draft, P(20, 10));
        Assert.Equal(2, draft!.Points.Count);

        var result = LassoSession.Finish(draft);

        Assert.Equal(LassoFinishOutcome.DegenerateIgnored, result.Outcome);
        Assert.Null(result.Coverage);
    }

    /// <summary>照抄 :224 —— 包围盒宽或高为 0 也算退化。</summary>
    [Fact]
    public void Finish_ZeroAreaIsDegenerate()
    {
        // 三点共线，围不出面积。
        var draft = StartLasso(P(10, 10));
        draft = LassoSession.Extend(draft, P(20, 10));
        draft = LassoSession.Extend(draft, P(30, 10));
        Assert.Equal(3, draft!.Points.Count);

        var result = LassoSession.Finish(draft);

        Assert.Equal(LassoFinishOutcome.DegenerateDeselected, result.Outcome);
    }

    /// <summary>照抄 :224 —— 椭圆分支不要求点数门槛，四个点就够。</summary>
    [Fact]
    public void Finish_EllipseBypassesTheThreePointRule()
    {
        var draft = LassoSession.DragMarquee(StartMarquee(P(10, 10), LassoKind.Ellipse), P(20, 18), false, false);
        Assert.Equal(4, draft!.Points.Count);

        var result = LassoSession.Finish(draft, antialiased: false);

        Assert.Equal(LassoFinishOutcome.Completed, result.Outcome);
    }

    /// <summary>推导组：完整三角形（3 点、Replace）应当正常闭合。</summary>
    [Fact]
    public void Finish_ThreePointTriangle_Completes()
    {
        var draft = StartLasso(P(10, 10));
        draft = LassoSession.Extend(draft, P(20, 10));
        draft = LassoSession.Extend(draft, P(20, 18));

        var result = LassoSession.Finish(draft, antialiased: false);

        Assert.Equal(LassoFinishOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Coverage);
        Assert.Equal("Lasso", result.UndoName);
    }
}