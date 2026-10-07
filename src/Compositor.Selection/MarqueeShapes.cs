namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 拖拽框：把一次"从锚点到当前点"的拖拽解析成一个矩形。
/// </summary>
/// <remarks>
/// 🔴 <b>逐行对照 Mac 版 <c>DragBox.rect(from:to:square:fromCenter:)</c>
/// （<c>Document/Selection.swift:93-105</c>），不要凭直觉改。</b>
/// 该函数同时被 Marquee 与 Shape 工具共用，它的取整口径决定了
/// <c>SelectionTests.swift:319 marqueeDrawsWholePixelRectanglesInAnyDirection</c>
/// 这类"整像素矩形"断言能否成立。
/// </remarks>
public static class DragBox
{
    /// <summary>
    /// 按从 <paramref name="anchor"/> 拖到 <paramref name="point"/> 计算矩形，全像素对齐。
    /// </summary>
    /// <param name="anchor">拖拽起点，<b>会被取整到整像素</b>。</param>
    /// <param name="point">当前指针位置，<b>会被取整到整像素</b>。</param>
    /// <param name="square">为 <see langword="true"/> 时强制正方形（正交约束）。</param>
    /// <param name="fromCenter">为 <see langword="true"/> 时以锚点为中心向两侧生长。</param>
    /// <returns>文档坐标下的矩形；宽或高为 0 时返回 <see cref="DocRect.Empty"/>。</returns>
    /// <remarks>
    /// 🔴 <b>取整口径是本函数最容易出错的地方。</b>
    /// Mac 版写的是 <c>point.x.rounded()</c>，而 Swift 的 <c>Double.rounded()</c>
    /// 默认规则是 <c>toNearestOrAwayFromZero</c>（<b>远离零</b>）；
    /// C# 的 <see cref="Math.Round(double)"/> 默认却是 <c>ToEven</c>（<b>银行家舍入</b>）。
    /// 两者在 <c>±0.5</c>、<c>±1.5</c> 这类中点上结果相反 ——
    /// 照抄时若直接用 <see cref="Math.Round(double)"/>，
    /// 矩形边会差一个像素，而这类错误在肉眼截图上<b>几乎看不出来</b>。
    /// 因此下面显式传 <see cref="MidpointRounding.AwayFromZero"/>。
    /// </remarks>
    public static DocRect Rect(DocPoint anchor, DocPoint point, bool square, bool fromCenter)
    {
        double ax = RoundAwayFromZero(anchor.X);
        double ay = RoundAwayFromZero(anchor.Y);

        double dx = RoundAwayFromZero(point.X) - ax;
        double dy = RoundAwayFromZero(point.Y) - ay;

        if (square)
        {
            // 正交约束取两轴绝对值的较大者，并让两轴保持同号，
            // 这样向左上拖与向右下拖得到的是同一个正方形而不是镜像。
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }

        double left = Math.Min(ax, ax + dx);
        double top = Math.Min(ay, ay + dy);
        double width = Math.Abs(dx);
        double height = Math.Abs(dy);

        if (fromCenter)
        {
            left = ax - Math.Abs(dx);
            top = ay - Math.Abs(dy);
            width = Math.Abs(dx) * 2;
            height = Math.Abs(dy) * 2;
        }

        int w = (int)width;
        int h = (int)height;
        if (w <= 0 || h <= 0)
        {
            return DocRect.Empty;
        }

        return new DocRect(new DocPoint(left, top), new DocSize(w, h));
    }

    /// <summary>
    /// 远离零取整，等价于 Swift 的 <c>Double.rounded()</c>。
    /// </summary>
    /// <param name="value">待取整的值。</param>
    /// <returns>远离零取整后的整数（以 double 返回，交给调用方做后续算术）。</returns>
    /// <remarks>
    /// 单独抽出来而不是散落各处，是为了让"与 Swift 对齐"这件事只有一个修改点。
    /// </remarks>
    internal static double RoundAwayFromZero(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Marquee（选框）工具的几何生成：矩形与椭圆。
/// </summary>
/// <remarks>
/// 对应 Mac 版的 <c>LassoKind.rectangle</c> 与 <c>LassoKind.ellipse</c>
/// （<c>Document/Selection.swift:79-80</c>）。⚠️ 这两个枚举值属于 <b>Marquee</b>，
/// 源码注释 <c>Selection.swift:78</c> 明确写了它们<b>不出现在 Lasso 的选项里</b> ——
/// 套索不能选矩形或椭圆，本工程不要提供该组合。
/// </remarks>
public static class MarqueeShapes
{
    /// <summary>生成矩形选框的覆盖度。</summary>
    /// <param name="anchor">拖拽起点。</param>
    /// <param name="point">当前指针位置。</param>
    /// <param name="square">为 <see langword="true"/> 时强制正方形。</param>
    /// <param name="fromCenter">为 <see langword="true"/> 时以锚点为中心向两侧生长。</param>
    /// <returns>
    /// 矩形选框的覆盖度平面；拖拽面积为零（宽或高为 0）时返回 <see langword="null"/>，
    /// 对应 Mac 版 <c>Selection.swift:224</c> 的 <c>bounds.width &gt; 0 &amp;&amp; bounds.height &gt; 0</c> 判定。
    /// </returns>
    /// <remarks>
    /// 🔴 矩形边界已由 <see cref="DragBox.Rect"/> 对齐到整数像素，
    /// 因此覆盖度<b>只有 0 与 255 两个值，不存在抗锯齿边缘</b> ——
    /// 这正是 Mac 版测试名 <c>marqueeDrawsWholePixelRectanglesInAnyDirection</c> 的含义。
    /// </remarks>
    public static CoveragePlane? Rectangle(DocPoint anchor, DocPoint point, bool square, bool fromCenter)
    {
        DocRect rect = DragBox.Rect(anchor, point, square, fromCenter);
        if (rect.IsEmpty)
        {
            return null;
        }

        return CoveragePlane.CreateFilled(rect, 255);
    }

    /// <summary>生成椭圆选框的覆盖度（内接于与矩形选框完全相同的拖拽框）。</summary>
    /// <param name="anchor">拖拽起点。</param>
    /// <param name="point">当前指针位置。</param>
    /// <param name="square">为 <see langword="true"/> 时强制正圆。</param>
    /// <param name="fromCenter">为 <see langword="true"/> 时以锚点为中心向两侧生长。</param>
    /// <param name="antialiased">是否开启抗锯齿；为 <see langword="false"/> 时边缘只有 0 与 255。</param>
    /// <returns>
    /// 椭圆选框的覆盖度平面；拖拽框面积为零时返回 <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// 框的计算<b>直接复用 <see cref="DragBox.Rect"/></b>，与 Mac 版一致：
    /// <c>Selection.swift:217-218</c> 先把四个角点取 min/max 得到外接框，
    /// 再 <c>addEllipse(in:)</c> 画内接椭圆。
    /// <para>
    /// ⚠️ <b>已知差异（可能不一致）</b>：椭圆边缘的抗锯齿覆盖度由本工程的超采样给出，
    /// Mac 版由 CoreGraphics 给出，两者<b>不保证逐位相同</b>；
    /// 但椭圆内部的 255 区域与外部的 0 区域必然一致。
    /// </para>
    /// </remarks>
    public static CoveragePlane? Ellipse(DocPoint anchor, DocPoint point, bool square, bool fromCenter, bool antialiased = true)
    {
        DocRect rect = DragBox.Rect(anchor, point, square, fromCenter);
        if (rect.IsEmpty)
        {
            return null;
        }

        double cx = rect.Left + (rect.Size.Width / 2.0);
        double cy = rect.Top + (rect.Size.Height / 2.0);
        double rx = rect.Size.Width / 2.0;
        double ry = rect.Size.Height / 2.0;

        return Antialias.Rasterize(rect, (x, y) =>
        {
            double dx = (x - cx) / rx;
            double dy = (y - cy) / ry;
            return (dx * dx) + (dy * dy) <= 1.0;
        }, antialiased);
    }
}