namespace Compositor.Selection;

using System.Collections.Generic;
using Compositor.Core;

/// <summary>
/// 套索/选框正在拖拽中的草稿，对应 Mac 版 <c>LassoDraft</c>（<c>Document/Selection.swift:109-116</c>）。
/// </summary>
/// <remarks>
/// 不可变：Mac 版是就地改一个可选的存储属性，本类型改成「每个操作返回新草稿或
/// <see langword="null"/>」，语义等价但可测试、可回放。
/// </remarks>
public sealed class LassoDraft
{
    /// <summary>已落下的轮廓点。</summary>
    public IReadOnlyList<DocPoint> Points { get; }

    /// <summary>光标当前位置，只用于画橡皮筋，照抄 <c>Selection.swift:111</c> 的 <c>var cursor: CGPoint?</c>。</summary>
    public DocPoint? Cursor { get; }

    /// <summary>按下鼠标那一刻定下的组合模式，此后不随修饰键变化。</summary>
    public SelectionCombineMode Mode { get; }

    /// <summary>轮廓种类，取自所属工具的选项集合（见 <see cref="LassoChoices"/> / <see cref="MarqueeChoices"/>）。</summary>
    public LassoKind Kind { get; }

    /// <summary>
    /// 选框的起始角（或中心），整像素。套索为 <see langword="null"/>。
    /// </summary>
    public DocPoint? Anchor { get; }

    internal LassoDraft(
        IReadOnlyList<DocPoint> points,
        DocPoint? cursor,
        SelectionCombineMode mode,
        LassoKind kind,
        DocPoint? anchor)
    {
        Points = points;
        Cursor = cursor;
        Mode = mode;
        Kind = kind;
        Anchor = anchor;
    }

    internal LassoDraft With(
        IReadOnlyList<DocPoint>? points = null,
        DocPoint? cursor = null,
        bool setCursor = false) =>
        new(points ?? Points, setCursor ? cursor : Cursor, Mode, Kind, Anchor);
}

/// <summary>关闭草稿时发生了什么，照抄 <c>finishLasso</c>（<c>Selection.swift:211-231</c>）的三条出口。</summary>
public enum LassoFinishOutcome
{
    /// <summary>没有草稿可关（已经取消，或从未开始）。对应 <c>Selection.swift:212</c> 的 <c>guard let draft else { return }</c>。</summary>
    Cancelled = 0,

    /// <summary>正常闭合，<see cref="LassoFinishResult.Coverage"/> 有值。</summary>
    Completed = 1,

    /// <summary>退化（点数不够或包围盒为空）且模式是 Replace → <b>取消选择</b>。对应 <c>Selection.swift:225</c>。</summary>
    DegenerateDeselected = 2,

    /// <summary>退化但模式是 Add/Subtract → <b>什么都不做</b>，保持原选区。对应 <c>Selection.swift:226</c>。</summary>
    DegenerateIgnored = 3,
}

/// <summary>关闭草稿的结果。</summary>
/// <param name="Outcome">发生了什么。</param>
/// <param name="Coverage">闭合后的覆盖度（<b>尚未裁剪到画布</b>）；退化或取消时为 <see langword="null"/>。</param>
/// <param name="UndoName">撤销步骤名，照抄 <c>Selection.swift:229-230</c> 的分派。</param>
public readonly record struct LassoFinishResult(
    LassoFinishOutcome Outcome,
    CoveragePlane? Coverage,
    string UndoName);

/// <summary>
/// 套索/选框拖拽的状态机，逐行照抄 <c>Document/Selection.swift:141-180</c> 与 <c>:211-231</c>。
/// </summary>
/// <remarks>
/// <para><b>本类型不裁剪画布。</b>Mac 版 <c>finishLasso</c> 最后调的是
/// <c>applySelection</c>（<c>Selection.swift:228</c>），画布裁剪发生在它内部的
/// <c>:236</c>。本类型只负责<b>把轮廓画出来</b>，裁剪与布尔留给
/// <see cref="SelectionAdapter.Apply"/>——那条通道已经把「先裁剪再布尔」的顺序做对了。</para>
/// <para><b>三条容易写错的语义：</b></para>
/// <list type="number">
/// <item><description>
/// 🔴 选框的起始角要<b>取整到整像素</b>（<c>Selection.swift:145</c> 的 <c>.rounded()</c>，
/// 即 <b>AwayFromZero</b>），而套索的起始点<b>不取整</b>（<c>:148</c> 直接存原坐标）。
/// 两者的起手精度不同，混了会让套索整体偏移半个像素。
/// </description></item>
/// <item><description>
/// 🔴 套索的退化处理<b>按模式分叉</b>：点数不够或包围盒为空时，
/// Replace 模式<b>取消选择</b>，Add/Subtract 则<b>什么都不做</b>
/// （<c>Selection.swift:224-227</c>）。统一处理成「都不动」会让 Replace 失灵，
/// 统一处理成「都取消」会让 Shift 拖出的空白吃掉原选区。
/// </description></item>
/// <item><description>
/// 轮廓点的去重阈值是 <c>hypot(dx, dy) &lt; 0.25</c>（<c>Selection.swift:167</c>）——
/// 欧氏距离，不是逐轴比较，也不是 0.5。
/// </description></item>
/// </list>
/// </remarks>
public static class LassoSession
{
    /// <summary>轮廓点最小间距，照抄 <c>Selection.swift:167</c> 的 <c>&lt; 0.25</c>。</summary>
    public const double MinPointDistance = 0.25;

    /// <summary>
    /// 开始一次拖拽，照抄 <c>beginLasso(at:mode:)</c>（<c>Selection.swift:141-150</c>）。
    /// </summary>
    /// <param name="isMarquee">当前工具是不是选框。<see langword="false"/> 时按套索处理。</param>
    /// <param name="point">按下鼠标的位置（文档坐标，Y 向下，铁律 1）。</param>
    /// <param name="mode">按下瞬间的组合模式。</param>
    /// <param name="marqueeKind">选框工具当前选中的轮廓种类。</param>
    /// <param name="lassoKind">套索工具当前选中的模式。</param>
    /// <returns>新建的草稿；坐标非有限时返回 <see langword="null"/>（对应 Mac 版的 <c>guard</c> 早返回）。</returns>
    public static LassoDraft? Begin(
        bool isMarquee,
        DocPoint point,
        SelectionCombineMode mode,
        LassoKind marqueeKind,
        LassoKind lassoKind)
    {
        if (!IsFinite(point))
        {
            return null;
        }

        if (isMarquee)
        {
            // 照抄 :145 —— 只有选框把起点取整到整像素。
            var anchor = new DocPoint(
                Math.Round(point.X, MidpointRounding.AwayFromZero),
                Math.Round(point.Y, MidpointRounding.AwayFromZero));

            return new LassoDraft(new[] { anchor }, null, mode, marqueeKind, anchor);
        }

        // 照抄 :148 —— 套索保留原始坐标，不取整。
        return new LassoDraft(new[] { point }, null, mode, lassoKind, null);
    }

    /// <summary>
    /// 选框拖拽更新，照抄 <c>dragMarquee(to:square:fromCenter:)</c>（<c>Selection.swift:155-162</c>）。
    /// </summary>
    /// <param name="draft">当前草稿。</param>
    /// <param name="point">当前鼠标位置。</param>
    /// <param name="square">按住 Shift 时强制正方形（或正圆）。</param>
    /// <param name="fromCenter">以起始点为中心双向生长。</param>
    /// <returns>更新后的草稿；条件不满足时<b>原样返回</b>（Mac 版是 guard 早返回，不改草稿）。</returns>
    /// <remarks>
    /// 照抄 <c>:156</c> 的 guard：草稿必须是 <see cref="LassoKind.Rectangle"/> 或
    /// <see cref="LassoKind.Ellipse"/><b>且</b>有 anchor，坐标还必须有限。
    /// 对套索调用本方法会原样返回，不会破坏套索草稿。
    /// </remarks>
    public static LassoDraft? DragMarquee(LassoDraft? draft, DocPoint point, bool square, bool fromCenter)
    {
        if (draft is null
            || (draft.Kind != LassoKind.Rectangle && draft.Kind != LassoKind.Ellipse)
            || draft.Anchor is not { } anchor
            || !IsFinite(point))
        {
            return draft;
        }

        DocRect rect = DragBox.Rect(anchor, point, square, fromCenter);
        if (rect.IsEmpty)
        {
            return draft;
        }

        // 照抄 :159-160 —— 四个角按「左上、右上、右下、左下」的固定顺序。
        // Y 向下的坐标系里这就是顺时针；finishLasso 的椭圆分支依赖这个顺序无关的集合，
        // 但多边形分支依赖闭合顺序。
        DocPoint[] corners =
        [
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Bottom),
            new(rect.Left, rect.Bottom),
        ];

        return draft.With(points: corners);
    }

    /// <summary>
    /// 追加一个轮廓点，照抄 <c>extendLasso(to:)</c>（<c>Selection.swift:165-170</c>）。
    /// </summary>
    /// <param name="draft">当前草稿。</param>
    /// <param name="point">候选点（文档坐标）。</param>
    /// <returns>更新后的草稿；无草稿、坐标非有限或距上一点不足 <see cref="MinPointDistance"/> 时原样返回。</returns>
    public static LassoDraft? Extend(LassoDraft? draft, DocPoint point)
    {
        if (draft is null || !IsFinite(point))
        {
            return draft;
        }

        if (draft.Points.Count > 0)
        {
            DocPoint last = draft.Points[^1];
            double dx = point.X - last.X;
            double dy = point.Y - last.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < MinPointDistance)
            {
                return draft;
            }
        }

        var points = new List<DocPoint>(draft.Points) { point };
        return draft.With(points: points);
    }

    /// <summary>移动橡皮筋光标，照抄 <c>moveLassoCursor(to:)</c>（<c>Selection.swift:172</c>）。</summary>
    /// <param name="draft">当前草稿。</param>
    /// <param name="cursor">新的光标位置；<see langword="null"/> 表示光标离开画布。</param>
    /// <returns>更新后的草稿；无草稿时返回 <see langword="null"/>。</returns>
    public static LassoDraft? MoveCursor(LassoDraft? draft, DocPoint? cursor) =>
        draft is null ? null : draft.With(cursor: cursor, setCursor: true);

    /// <summary>
    /// 删掉最后一个轮廓点，照抄 <c>removeLastLassoPoint()</c>（<c>Selection.swift:174-178</c>）。
    /// </summary>
    /// <param name="draft">当前草稿。</param>
    /// <returns>
    /// 删点后的草稿；<b>删光了就返回 <see langword="null"/></b>——
    /// 这正是 Mac 版 <c>lassoDraft = draft.points.isEmpty ? nil : draft</c> 的语义。
    /// </returns>
    public static LassoDraft? RemoveLast(LassoDraft? draft)
    {
        if (draft is null)
        {
            return null;
        }

        if (draft.Points.Count <= 1)
        {
            return null;
        }

        var points = new List<DocPoint>(draft.Points);
        points.RemoveAt(points.Count - 1);
        return draft.With(points: points);
    }

    /// <summary>
    /// 关闭草稿并光栅化成覆盖度，照抄 <c>finishLasso()</c>（<c>Selection.swift:211-231</c>）。
    /// </summary>
    /// <param name="draft">当前草稿。</param>
    /// <param name="antialiased">抗锯齿开关。</param>
    /// <returns>结果；<b>不裁剪画布</b>，裁剪与布尔请接 <see cref="SelectionAdapter.Apply"/>。</returns>
    /// <remarks>
    /// <code>
    /// let outline = CGMutablePath()
    /// if draft.kind == .ellipse, draft.points.count == 4 {   // :215
    ///     outline.addEllipse(in: CGRect(x: xs.min(), y: ys.min(), …))
    /// } else {
    ///     outline.addLines(between: draft.points)             // :220
    ///     outline.closeSubpath()                             // :221
    /// }
    /// let bounds = outline.boundingBoxOfPath
    /// guard draft.points.count >= 3 || draft.kind == .ellipse, // :224
    ///       bounds.width > 0, bounds.height > 0 else {
    ///     if draft.mode == .replace { deselect() }            // :225
    ///     return
    /// }
    /// </code>
    /// <para>
    /// 🔴 椭圆分支的额外条件 <c>points.count == 4</c> 容易被漏掉：
    /// 没有它，一个还没拖完的椭圆草图会被当成多边形去 <c>addLines</c>，
    /// 画出来的是折线而不是椭圆。
    /// </para>
    /// </remarks>
    public static LassoFinishResult Finish(LassoDraft? draft, bool antialiased = true)
    {
        if (draft is null)
        {
            return new LassoFinishResult(LassoFinishOutcome.Cancelled, null, UndoName(LassoKind.Freehand));
        }

        // 照抄 :215 —— 必须是椭圆「且」恰好四个点（即拖拽已经给出完整包围盒）。
        bool isFilledEllipse = draft.Kind == LassoKind.Ellipse && draft.Points.Count == 4;

        CoveragePlane? coverage = isFilledEllipse
            ? EllipseFromCorners(draft.Points, antialiased)
            : LassoRaster.Rasterize(AsSpan(draft.Points), antialiased);

        string name = UndoName(draft.Kind);

        // 照抄 :224 —— 椭圆不要求点数，其余要求至少 3 个。
        bool enoughPoints = isFilledEllipse || draft.Points.Count >= 3;
        bool hasArea = coverage is not null && coverage.Bounds.Size.Width > 0 && coverage.Bounds.Size.Height > 0;

        if (!enoughPoints || !hasArea)
        {
            // 照抄 :225-226 —— 退化后按模式分叉。
            return draft.Mode == SelectionCombineMode.Replace
                ? new LassoFinishResult(LassoFinishOutcome.DegenerateDeselected, null, name)
                : new LassoFinishResult(LassoFinishOutcome.DegenerateIgnored, null, name);
        }

        return new LassoFinishResult(LassoFinishOutcome.Completed, coverage, name);
    }

    /// <summary>撤销步骤名，照抄 <c>Selection.swift:229-230</c> 的三元分派。</summary>
    private static string UndoName(LassoKind kind) => kind switch
    {
        LassoKind.Freehand => "Lasso",
        LassoKind.Polygonal => "Polygonal Lasso",
        LassoKind.Ellipse => "Elliptical Marquee",
        LassoKind.Rectangle => "Rectangular Marquee",
        _ => "Selection",
    };

    /// <summary>
    /// 照抄 <c>Selection.swift:217-218</c>：椭圆画在<b>四个角点的外接矩形</b>里。
    /// </summary>
    private static CoveragePlane? EllipseFromCorners(IReadOnlyList<DocPoint> points, bool antialiased)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (DocPoint p in points)
        {
            if (!IsFinite(p))
            {
                return null;
            }

            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        // 角点来自 DragBox.Rect，已是整像素；这里保留浮点宽度再取整，与 Mac 的 CGRect 语义一致。
        DocRect rect = new(
            new DocPoint(minX, minY),
            new DocSize((int)(maxX - minX), (int)(maxY - minY)));

        return rect.IsEmpty ? null : MarqueeShapes.Ellipse(
            rect.Origin,
            new DocPoint(rect.Right, rect.Bottom),
            square: false,
            fromCenter: false,
            antialiased);
    }

    private static bool IsFinite(DocPoint p) =>
        double.IsFinite(p.X) && double.IsFinite(p.Y);

    /// <summary>
    /// 把点序列转成 <see cref="ReadOnlySpan{T}"/>。
    /// </summary>
    /// <remarks>
    /// 底层是数组时<b>零拷贝</b>；是 <see cref="List{T}"/> 时走 <c>CollectionsMarshal.AsSpan</c>，
    /// 只有在实在拿不到 span（如 <c>ImmutableArray</c>）时才复制一份。
    /// 套索每次拖动都要重算，这一步的分配量要压住。
    /// </remarks>
    private static ReadOnlySpan<DocPoint> AsSpan(IReadOnlyList<DocPoint> points) => points switch
    {
        DocPoint[] array => array,
        List<DocPoint> list => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list),
        _ => System.Linq.Enumerable.ToArray(points),
    };
}