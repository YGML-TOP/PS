namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// Lasso（套索）工具的几何生成：自由套索与多边形套索，共用同一条闭合多边形轮廓。
/// </summary>
/// <remarks>
/// 对应 Mac 版的 <c>LassoKind.freehand</c> 与 <c>LassoKind.polygonal</c>
/// （<c>Document/Selection.swift:76-77</c>）。两者在 Mac 版里<b>走的是同一条代码路径</b> ——
/// <c>finishLasso</c>（<c>Selection.swift:220-221</c>）对二者一视同仁地
/// <c>addLines(between:)</c> + <c>closeSubpath()</c>，
/// 区别只在<b>采样点的来源</b>：自由套索沿指针轨迹密集采样，
/// 多边形套索由用户逐点点击，所以需要"退格删点"（<c>Selection.swift:174-178</c>）。
/// </remarks>
public static class LassoRaster
{
    /// <summary>
    /// 新采样点与上一个点的最小间距（文档像素）。
    /// </summary>
    /// <remarks>
    /// 照抄 <c>extendLasso</c>（<c>Selection.swift:167</c>）：
    /// <c>if let last = draft.points.last, hypot(point.x - last.x, point.y - last.y) &lt; 0.25 { return }</c>。
    /// 低于这个距离的点直接丢弃，否则自由套索在指针静止时会灌进成百上千个重复点。
    /// </remarks>
    public const double MinPointDistance = 0.25;

    /// <summary>判定一个新采样点是否应被接受。</summary>
    /// <param name="points">已有点序列。</param>
    /// <param name="candidate">候选新点，文档坐标。</param>
    /// <returns>应追加返回 <see langword="true"/>；距离上一点过近或坐标非有限时返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 对应 <c>extendLasso</c>（<c>Selection.swift:165-170</c>）的守卫条件：
    /// 坐标非有限则忽略；与上一点的欧氏距离小于 <see cref="MinPointDistance"/> 则忽略。
    /// </remarks>
    public static bool ShouldAppend(ReadOnlySpan<DocPoint> points, DocPoint candidate)
    {
        if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y))
        {
            return false;
        }

        if (points.Length == 0)
        {
            return true;
        }

        DocPoint last = points[^1];
        double dx = candidate.X - last.X;
        double dy = candidate.Y - last.Y;
        return Math.Sqrt((dx * dx) + (dy * dy)) >= MinPointDistance;
    }

    /// <summary>
    /// 把套索点序列光栅化为覆盖度平面，闭合后按<b>非零环绕规则</b>填充。
    /// </summary>
    /// <param name="points">套索采样点，文档坐标，至少 3 个。</param>
    /// <param name="antialiased">是否开启抗锯齿；为 <see langword="false"/> 时边缘只有 0 与 255。</param>
    /// <returns>
    /// 覆盖度平面；点数不足 3 或包围盒面积为零时返回 <see langword="null"/>，
    /// 对应 <c>Selection.swift:224</c> 的 <c>bounds.width &gt; 0 &amp;&amp; bounds.height &gt; 0</c> 判定。
    /// </returns>
    /// <remarks>
    /// 🔴 <b>填充规则必须是非零环绕（winding），不是奇偶（even-odd）。</b>
    /// Mac 版 <c>Selection.swift:22</c> 明确写着 <c>context.fillPath(using: .winding)</c>。
    /// 对简单多边形两种规则结果相同，但<b>自相交</b>的套索轮廓会有差异 ——
    /// 自由套索重复划过同一个区域时轮廓完全可能自相交，此时只有 winding 能给出
    /// Photoshop 那种"重叠区仍然实心"的结果。
    /// </remarks>
    public static CoveragePlane? Rasterize(ReadOnlySpan<DocPoint> points, bool antialiased = true)
    {
        if (points.Length < 3)
        {
            return null;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            double x = points[i].X;
            double y = points[i].Y;
            if (x < minX) { minX = x; }
            if (x > maxX) { maxX = x; }
            if (y < minY) { minY = y; }
            if (y > maxY) { maxY = y; }
        }

        // 包围盒向外取整（Floor 左上 / Ceil 右下），保证不裁掉任何被覆盖的像素。
        // 这与 DocRect.Integral 的口径一致。
        DocRect bounds = new(
            new DocPoint(Math.Floor(minX), Math.Floor(minY)),
            new DocSize(
                (int)Math.Ceiling(maxX) - (int)Math.Floor(minX),
                (int)Math.Ceiling(maxY) - (int)Math.Floor(minY)));

        if (bounds.IsEmpty)
        {
            return null;
        }

        // 扁平化为 x,y,x,y… 供扫描线热路径使用，避免逐点取 record struct 的字段。
        var xs = new double[points.Length];
        var ys = new double[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
        }

        return ScanlineFill(bounds, xs, ys, antialiased);
    }

    /// <summary>
    /// 逐子扫描线做非零环绕填充，每个像素在每条子扫描线上按 <see cref="Antialias.SamplesPerAxis"/> 采样。
    /// </summary>
    /// <param name="bounds">文档坐标包围盒。</param>
    /// <param name="xs">顶点 X 序列。</param>
    /// <param name="ys">顶点 Y 序列，与 <paramref name="xs"/> 等长。</param>
    /// <param name="antialiased">是否开启抗锯齿；为 <see langword="false"/> 时每像素只采一个点。</param>
    /// <returns>覆盖度平面。</returns>
    private static CoveragePlane ScanlineFill(DocRect bounds, double[] xs, double[] ys, bool antialiased)
    {
        int w = bounds.Size.Width;
        int h = bounds.Size.Height;
        var plane = CoveragePlane.CreateZero(bounds);

        int n = xs.Length;
        int samples = antialiased ? Antialias.SamplesPerAxis : 1;
        int total = samples * samples;

        // 每条子扫描线的交点缓存。交点数量最多与边数相同。
        var crossings = new List<(double X, int Dir)>(n + 1);

        for (int py = 0; py < h; py++)
        {
            // 当前像素行的累计命中数累加器：每个子采样行贡献 SamplesPerAxis 次机会。
            var rowHits = new int[w];

            for (int sy = 0; sy < samples; sy++)
            {
                double scanY = bounds.Top + py + ((sy + 0.5) / samples);

                crossings.Clear();
                CollectCrossings(xs, ys, n, scanY, crossings);
                if (crossings.Count == 0)
                {
                    continue;
                }

                crossings.Sort(static (p, q) => p.X.CompareTo(q.X));

                // 非零环绕：按 x 升序遍历交点，维护环绕数，
                // 凡是"环绕数从 0 变为非 0"的区段都在形状内部。
                int winding = 0;
                double prevX = 0;
                for (int i = 0; i < crossings.Count; i++)
                {
                    double cx = crossings[i].X;
                    int before = winding;
                    winding += crossings[i].Dir;

                    if (before == 0 && winding != 0)
                    {
                        prevX = cx;
                        continue;
                    }

                    if (before != 0 && winding == 0)
                    {
                        AccumulateSpan(rowHits, prevX, cx, bounds.Left, samples);
                    }
                }
            }

            int rowBase = py * w;
            for (int x = 0; x < w; x++)
            {
                int hits = rowHits[x];
                if (hits == 0)
                {
                    continue;
                }

                plane.SetPixelUnchecked(x, py, (byte)Math.Round(hits * 255.0 / total, MidpointRounding.AwayFromZero));
            }
        }

        return plane;
    }

    /// <summary>
    /// 把一段 x 区间内命中的子采样点累加到 <paramref name="rowHits"/>。
    /// </summary>
    /// <param name="rowHits">当前像素行的命中累加器。</param>
    /// <param name="fromX">区间起始的文档 X。</param>
    /// <param name="toX">区间结束的文档 X（不含）。</param>
    /// <param name="left">像素行左边界（文档 X）。</param>
    /// <param name="samples">每像素的横向采样数，必须与调用方的 <c>total</c> 口径一致。</param>
    /// <remarks>
    /// 🔴 <b>这里的 samples 必须由调用方传入，不能写死 <see cref="Antialias.SamplesPerAxis"/>。</b>
    /// 关闭抗锯齿时纵向降到 1 个子扫描行，若横向仍按 4 采样，
    /// 每行会累加 4 次而 <c>total</c> 只有 1，覆盖度算成 <c>4×255 = 1020</c> ——
    /// unchecked 转 <c>byte</c> 后溢出成 <b>252</b>，于是"关闭抗锯齿"仍产出中间灰度，
    /// 直接违反 <c>SelectionTests.swift:111</c> 的
    /// <c>hard.allSatisfy { $0 == 0 || $0 == 255 }</c>。
    /// </remarks>
    private static void AccumulateSpan(int[] rowHits, double fromX, double toX, double left, int samples)
    {
        if (toX <= fromX)
        {
            return;
        }

        double step = 1.0 / samples;

        for (int px = (int)Math.Floor(fromX - left); px < (int)Math.Ceiling(toX - left); px++)
        {
            if ((uint)px >= (uint)rowHits.Length)
            {
                continue;
            }

            int hits = 0;
            for (int s = 0; s < samples; s++)
            {
                double sx = left + px + ((s + 0.5) * step);
                if (sx >= fromX && sx < toX)
                {
                    hits++;
                }
            }

            rowHits[px] += hits;
        }
    }

    /// <summary>
    /// 收集所有边与水平线 <paramref name="scanY"/> 的交点及其绕行方向。
    /// </summary>
    /// <param name="xs">顶点 X 序列。</param>
    /// <param name="ys">顶点 Y 序列。</param>
    /// <param name="n">顶点数。</param>
    /// <param name="scanY">子扫描线的 Y 坐标。</param>
    /// <param name="crossings">交点输出，按 X 升序排列，同时写入绕行方向。</param>
    /// <remarks>
    /// 半开区间判定 <c>a.Y &lt;= scanY &lt; b.Y</c>（或反向）保证顶点只被计一次，
    /// 否则穿过顶点的边会被重复计入，环绕数直接翻倍。
    /// </remarks>
    private static void CollectCrossings(double[] xs, double[] ys, int n, double scanY, List<(double X, int Dir)> crossings)
    {
        crossings.Clear();

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double ay = ys[i];
            double by = ys[j];

            if (ay == by)
            {
                continue;
            }

            bool upward = ay < by;
            double lowY = upward ? ay : by;
            double highY = upward ? by : ay;

            if (scanY < lowY || scanY >= highY)
            {
                continue;
            }

            double ax = xs[i];
            double bx = xs[j];
            double t = (scanY - ay) / (by - ay);
            crossings.Add((ax + ((bx - ax) * t), upward ? 1 : -1));
        }
    }
}