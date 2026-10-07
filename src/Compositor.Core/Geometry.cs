namespace Compositor.Core;

/// <summary>文档坐标系中的一个点。不变量：全部为有限值。</summary>
/// <param name="X">水平分量，Y 向下为正。</param>
/// <param name="Y">垂直分量，<b>向下为正</b>（铁律 1）。</param>
/// <remarks>
/// 约定而非强制：本类型是 <c>readonly record struct</c> 的位置参数构造，
/// 与契约附录 A 逐字一致，因此<b>不</b>在构造时校验有限性（加了校验就得放弃位置参数形式）。
/// 需要强保证的调用点（如 <see cref="DocCoord"/>）各自校验。
/// </remarks>
public readonly record struct DocPoint(double X, double Y);

/// <summary>文档坐标系中的一个尺寸。不变量：Width > 0 且 Height > 0。</summary>
/// <param name="Width">宽度，单位为文档像素。</param>
/// <param name="Height">高度，单位为文档像素。</param>
/// <remarks>
/// 同样不强制不变量：<c>default(DocSize)</c> 即 <c>(0,0)</c>，是"空尺寸"而非非法值，
/// 多个契约成员（如 <see cref="DocRect.IsEmpty"/>）依赖它。
/// </remarks>
public readonly record struct DocSize(int Width, int Height);

/// <summary>二维向量。用于平移量等场景，语义上与 <see cref="DocPoint"/> 无区别。</summary>
/// <param name="X">水平分量。</param>
/// <param name="Y">垂直分量，向下为正。</param>
public readonly record struct DocVector(double X, double Y);

/// <summary>
/// 视图坐标系中的一个点。
/// </summary>
/// <param name="X">水平分量。</param>
/// <param name="Y">垂直分量，<b>通常向上为正</b>（屏幕坐标）。</param>
/// <remarks>
/// 🔴 铁律 1：本类型只允许出现在 <see cref="DocCoord"/> 的两个方法的签名里，
/// 以及 UI 层内部。<b>业务代码不得持有 <see cref="ViewPoint"/>。</b>
/// </remarks>
public readonly record struct ViewPoint(double X, double Y);

/// <summary>
/// 视图port：视图坐标到屏幕的映射参数。
/// </summary>
/// <param name="OriginX">视图原点的屏幕 X。</param>
/// <param name="OriginY">视图原点的屏幕 Y。</param>
/// <param name="PointsPerPixel">
/// 每文档像素对应的点数（缩放级别）。<b>必须 &gt; 0</b>，
/// 否则 <see cref="DocCoord"/> 的两个方法会除零 —— 那里会显式抛出 <see cref="ArgumentOutOfRangeException"/>。
/// </param>
/// <param name="Scale">缩放倍数，供 UI 层渲染使用。</param>
public sealed record Viewport(double OriginX, double OriginY, double PointsPerPixel, double Scale);

/// <summary>
/// 文档坐标系中的矩形，<b>Origin 为左上角</b>。空矩形表示 Width 或 Height 为 0。
/// </summary>
/// <remarks>
/// 🔴 铁律 1：<see cref="Top"/> 就是 Y 最小的那条边，<see cref="Bottom"/> 是 Y 最大的那条边。
/// 本类型<b>不做任何 Y 翻转</b>，因为原项目 <c>CanvasViewport.swift:25-33</c> 是纯线性变换、无 Y 取反。
/// 任何 <c>-y</c> 或 <c>height - y</c> 出现在使用本类型的地方都是违规。
/// <para>
/// 右边界与下边界按<b>半开区间</b>处理：<c>Left ≤ x &lt; Right</c>。这样相邻矩形无缝拼接时不会重复覆盖接缝列。
/// </para>
/// </remarks>
public readonly record struct DocRect
{
    /// <summary>矩形左上角。不变量：构成 <see cref="Center"/> 与四条边的基准。</summary>
    public DocPoint Origin { get; init; }

    /// <summary>矩形尺寸。宽或高为 0 时本矩形视为空。</summary>
    public DocSize Size { get; init; }

    /// <summary>左边界 X，等于 <c>Origin.X</c>。</summary>
    public double Left => Origin.X;

    /// <summary>上边界 Y，等于 <c>Origin.Y</c>。<b>向下的坐标轴上，越小越靠上。</b></summary>
    public double Top => Origin.Y;

    /// <summary>右边界 X，<b>不含</b>（半开区间）。</summary>
    public double Right => Origin.X + Size.Width;

    /// <summary>下边界 Y，<b>不含</b>（半开区间）。</summary>
    public double Bottom => Origin.Y + Size.Height;

    /// <summary>宽或高不大于 0 时为 <see langword="true"/>。</summary>
    public bool IsEmpty => Size.Width <= 0 || Size.Height <= 0;

    /// <summary>矩形中心点。由 <c>Origin + Size/2</c> 得到，与 <c>LayerTransform.Center</c> 同一公式。</summary>
    public DocPoint Center => new(Origin.X + Size.Width / 2.0, Origin.Y + Size.Height / 2.0);

    /// <summary>空矩形（原点 0,0，尺寸 0,0）。<see cref="IsEmpty"/> 为 <see langword="true"/>。</summary>
    public static DocRect Empty => default;

    /// <summary>由左上角与尺寸构造。</summary>
    /// <param name="origin">左上角。</param>
    /// <param name="size">尺寸。</param>
    public DocRect(DocPoint origin, DocSize size)
    {
        Origin = origin;
        Size = size;
    }

    /// <summary>与另一矩形的交集。无重叠时返回 <see cref="Empty"/>。</summary>
    /// <param name="o">另一矩形。</param>
    /// <returns>交集矩形，可能为空。</returns>
    public DocRect Intersect(DocRect o)
    {
        double l = Math.Max(Left, o.Left);
        double t = Math.Max(Top, o.Top);
        double r = Math.Min(Right, o.Right);
        double b = Math.Min(Bottom, o.Bottom);
        if (r <= l || b <= t)
        {
            return Empty;
        }

        // 用 Floor/Ceil 而不是截断：亚像素矩形交出来的尺寸截断后可能变成 0，
        // 而 (0,0) 会被 IsEmpty 判成空，语义就错了。Floor/Ceil 保证结果至少 1 像素。
        int w = ClampToInt(Math.Ceiling(r)) - ClampToInt(Math.Floor(l));
        int h = ClampToInt(Math.Ceiling(b)) - ClampToInt(Math.Floor(t));
        if (w <= 0 || h <= 0)
        {
            return Empty;
        }

        return new DocRect(new DocPoint(ClampToInt(Math.Floor(l)), ClampToInt(Math.Floor(t))), new DocSize(w, h));
    }

    /// <summary>与另一矩形的并集（最小外接矩形）。任一方为空时返回另一方。</summary>
    /// <param name="o">另一矩形。</param>
    /// <returns>并集矩形。</returns>
    public DocRect Union(DocRect o)
    {
        if (IsEmpty)
        {
            return o;
        }

        if (o.IsEmpty)
        {
            return this;
        }

        double l = Math.Min(Left, o.Left);
        double t = Math.Min(Top, o.Top);
        double r = Math.Max(Right, o.Right);
        double b = Math.Max(Bottom, o.Bottom);
        int w = ClampToInt(Math.Ceiling(r)) - ClampToInt(Math.Floor(l));
        int h = ClampToInt(Math.Ceiling(b)) - ClampToInt(Math.Floor(t));
        return new DocRect(new DocPoint(ClampToInt(Math.Floor(l)), ClampToInt(Math.Floor(t))), new DocSize(w, h));
    }

    /// <summary>是否包含某点。采用半开区间 <c>Left ≤ x &lt; Right</c>、<c>Top ≤ y &lt; Bottom</c>。</summary>
    /// <param name="p">待判定的文档坐标点。</param>
    /// <returns>落在矩形内返回 <see langword="true"/>；本矩形为空时恒为 <see langword="false"/>。</returns>
    public bool Contains(DocPoint p) => !IsEmpty && p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    /// <summary>向外取整为像素对齐的矩形：原点向下取整，右/下边界向上取整。</summary>
    /// <remarks>
    /// <c>Floor</c> 左上、<c>Ceil</c> 右下 —— 保证<b>不裁掉</b>任何被覆盖的像素。
    /// 这与"向内取整"（会裁掉边缘半像素）不同，重采样与蒙版合成必须用向外取整。
    /// </remarks>
    public DocRect Integral
    {
        get
        {
            if (IsEmpty)
            {
                return Empty;
            }

            int l = ClampToInt(Math.Floor(Left));
            int t = ClampToInt(Math.Floor(Top));
            int r = ClampToInt(Math.Ceiling(Right));
            int b = ClampToInt(Math.Ceiling(Bottom));
            int w = r - l;
            int h = b - t;
            if (w <= 0 || h <= 0)
            {
                return Empty;
            }

            return new DocRect(new DocPoint(l, t), new DocSize(w, h));
        }
    }

    /// <summary>四边向外扩张 <paramref name="d"/> 像素。负值等价于向内收缩。</summary>
    /// <param name="d">扩张量，可为负。收缩到负尺寸时返回 <see cref="Empty"/>。</param>
    /// <remarks>
    /// 🔴 <b>向外取整</b>：左边 <c>Floor(Left - d)</c>、右边 <c>Ceil(Right + d)</c>，
    /// 与 <see cref="Integral"/> 同一口径 —— 保证<b>不裁掉</b>扩张后应当覆盖的像素。
    /// <para>若改成向零截断（<c>(int)</c>），<c>Inflate(0.5)</c> 在 <c>[0,4)</c> 上会算成
    /// <c>l = (int)(-0.5) = 0</c>、<c>r = (int)(4.5) = 4</c> —— <b>矩形原封不动，扩张静默失效</b>。
    /// 负值收缩则相反：<c>Inflate(-0.5)</c> 在 <c>[0,4)</c> 上得到 <c>[0,4)</c>（向外取整后不变），
    /// 这同样是正确的 —— 收缩半个像素不该改变任何像素的归属。</para>
    /// </remarks>
    public DocRect Inflate(double d)
    {
        if (IsEmpty)
        {
            return Empty;
        }

        int l = ClampToInt(Math.Floor(Left - d));
        int t = ClampToInt(Math.Floor(Top - d));
        int w = ClampToInt(Math.Ceiling(Right + d)) - l;
        int h = ClampToInt(Math.Ceiling(Bottom + d)) - t;
        if (w <= 0 || h <= 0)
        {
            return Empty;
        }

        return new DocRect(new DocPoint(l, t), new DocSize(w, h));
    }

    /// <summary>
    /// 把可能越界的 double 夹进 <c>int</c> 可表示的范围。
    /// </summary>
    /// <remarks>
    /// <c>int.MinValue</c> 附近再减 1 会溢出，checked 上下文里直接抛
    /// <see cref="OverflowException"/>；这些方法可能在不受控的 UI 缩放值上被调用，
    /// 所以先夹到 <c>int.MinValue + 1</c> / <c>int.MaxValue - 1</c> 再转换。
    /// </remarks>
    private static int ClampToInt(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        if (v <= int.MinValue + 1d)
        {
            return int.MinValue + 1;
        }

        if (v >= int.MaxValue - 1d)
        {
            return int.MaxValue - 1;
        }

        return (int)v;
    }
}

/// <summary>
/// 视图坐标系 ↔ 文档坐标系的<b>唯一</b>边界。铁律 1：翻转只允许发生在这两个方法里。
/// </summary>
/// <remarks>
/// 🔴 <b>业务代码禁止直接做坐标换算。</b> 禁止出现 <c>-y</c>、<c>height - y</c>、
/// <c>(canvasH - y) * scale</c> 这类表达式。全部走这里。
/// <para>
/// 本类<b>不做 Y 翻转</b>，因为原项目 <c>Rendering/CanvasViewport.swift:25-33</c> 的
/// <c>documentPoint(from:)</c> 是纯线性变换、没有 Y 取反：
/// <c>(point.y - origin.y) / pointsPerPixel</c>。视图层若需要 Y 向上，
/// 由 UI 层在自己的一侧翻转，不要污染这里。
/// </para>
/// </remarks>
public static class DocCoord
{
    /// <summary>视图坐标 → 文档坐标。</summary>
    /// <param name="p">视图坐标系中的点（Y 通常向上）。</param>
    /// <param name="viewport">视口参数。</param>
    /// <returns>文档坐标系中的点，Y 向下。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="viewport"/> 的 <c>PointsPerPixel</c> 不是有限正数，
    /// 或换算结果不是有限值（详见 <see cref="Validate"/>）。
    /// </exception>
    public static DocPoint FromView(ViewPoint p, Viewport viewport)
    {
        Validate(viewport);
        double x = (p.X - viewport.OriginX) / viewport.PointsPerPixel;
        double y = (p.Y - viewport.OriginY) / viewport.PointsPerPixel;

        // 光校验 PointsPerPixel > 0 不够：double.Epsilon（≈4.94e-324）是有限的正数，
        // 但 1.0 / Epsilon 会溢出成 +Infinity —— 恰好是本方法要防的那种"静默污染"。
        // 所以校验落在**结果**上，而不是只校验除数。
        EnsureFinite(x, viewport, nameof(p));
        EnsureFinite(y, viewport, nameof(p));
        return new DocPoint(x, y);
    }

    /// <summary>文档坐标 → 视图坐标。</summary>
    /// <param name="p">文档坐标系中的点，Y 向下。</param>
    /// <param name="viewport">视口参数。</param>
    /// <returns>视图坐标系中的点。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="viewport"/> 的 <c>PointsPerPixel</c> 不是有限正数，或换算结果不是有限值。
    /// </exception>
    public static ViewPoint ToView(DocPoint p, Viewport viewport)
    {
        Validate(viewport);
        double x = p.X * viewport.PointsPerPixel + viewport.OriginX;
        double y = p.Y * viewport.PointsPerPixel + viewport.OriginY;

        // 与 FromView 对称：乘法同样会溢出（1e300 * 1e300 = +Inf）。
        EnsureFinite(x, viewport, nameof(p));
        EnsureFinite(y, viewport, nameof(p));
        return new ViewPoint(x, y);
    }

    /// <summary>
    /// 校验视口可换算。两个方向的换算共用同一条除法路径，所以只校验一次。
    /// </summary>
    private static void Validate(Viewport viewport)
    {
        if (!double.IsFinite(viewport.PointsPerPixel) || viewport.PointsPerPixel <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                viewport.PointsPerPixel,
                "PointsPerPixel 必须是有限正数，否则坐标换算会除零。");
        }

        if (!double.IsFinite(viewport.OriginX) || !double.IsFinite(viewport.OriginY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport), "OriginX/OriginY 必须是有限值。");
        }
    }

    /// <summary>换算结果必须是有限值，否则抛。</summary>
    private static void EnsureFinite(double value, Viewport viewport, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                $"坐标换算结果为 {value}。PointsPerPixel = {viewport.PointsPerPixel}，过小或过大。");
        }
    }
}
