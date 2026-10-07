namespace Compositor.Core;

/// <summary>
/// 选区。M0 只定义数据结构；<b>算法归 AI-4</b>。
/// </summary>
/// <remarks>
/// 契约给出的成员只有构造入口（<see cref="Empty"/> / <see cref="Full"/> /
/// <b><see cref="FromCoverage"/></b>）与查询入口（<see cref="CoverageAt"/>）。
/// <para>🔴 <b>v1.1 时刻意不提供「从覆盖率数据构造」的公开方法</b>，
/// 因为怎么把一条路径变成覆盖度（魔棒、套索、色彩范围）是 AI-4 的算法、不是契约。
/// 结果是 AI-4 连一个<b>矩形选区</b>都构造不出来（矩形需要非零 <see cref="Bounds"/>
/// 加非均匀 <see cref="Coverage"/>，而当时只有 <see cref="Empty"/> / <see cref="Full"/>
/// 两个返回 <see cref="Coverage8.Uniform(int)"/> 的入口）。</para>
/// <para><b>v1.2 新增 <see cref="FromCoverage"/></b> 作为这条通道：它只负责「把已有的
/// 覆盖度数据挂到一个矩形上」这件纯数据结构的事，<b>不包含任何选区算法</b>——
/// 算法仍归 AI-4，由 AI-4 算出覆盖度后调用本方法。</para>
/// </remarks>
public sealed class SelectionMask
{
    private SelectionMask(DocRect bounds, Coverage8 coverage)
    {
        Bounds = bounds;
        Coverage = coverage;
    }

    /// <summary>选区的外接矩形，位于文档坐标系。空选区为 <see cref="DocRect.Empty"/>。</summary>
    public DocRect Bounds { get; }

    /// <summary>选区内的 8-bit 覆盖度数据。</summary>
    public Coverage8 Coverage { get; }

    /// <summary>
    /// 选区是否为空。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>本项在契约中未定义，此处按下述口径实现，属于我替契约补的判断，需要评审确认。</b>
    /// <para>口径：<c>IsEmpty ⇔ Bounds.IsEmpty</c>，即"外接矩形宽或高为 0"。</para>
    /// <para>为什么不用"覆盖度全为 0"：那需要每次访问都 O(像素数) 扫描，
    /// 而 <c>IsEmpty</c> 是会被每帧调用的属性。更重要的是"有边界但零覆盖"
    /// 在语义上是<b>有效</b>的选区 —— 用户确实圈了一个形状出来，只是它当前不遮蔽任何像素；
    /// 把它当成空会让 UI 误判"没有选区"，从而丢掉用户的操作意图。</para>
    /// <para>若后续评审认为需要更严格的定义，应改成一个显式的方法而不是属性，
    /// 以免把 O(n) 扫描藏进一个看似廉价的属性访问里。</para>
    /// </remarks>
    public bool IsEmpty => Bounds.IsEmpty;

    /// <summary>空选区：无边界、零覆盖。</summary>
    public static SelectionMask Empty { get; } =
        new(DocRect.Empty, Coverage8.Uniform(0));

    /// <summary>覆盖整个文档的满选区。</summary>
    /// <param name="size">文档尺寸。宽或高 ≤ 0 时返回 <see cref="Empty"/>。</param>
    /// <returns>覆盖度为 255 的选区。</returns>
    /// <remarks>
    /// 用 <see cref="Coverage8.Uniform"/> 而不是全分辨率分配：满选区在图层面板里
    /// 是最常见的初始状态，为它分配 4K 缓冲是纯浪费。
    /// </remarks>
    public static SelectionMask Full(DocSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return Empty;
        }

        return new SelectionMask(new DocRect(new DocPoint(0, 0), size), Coverage8.Uniform(255));
    }

    /// <summary>
    /// 🔴 <b>契约 v1.2 新增。</b>用已有的覆盖度数据构造一个选区。
    /// </summary>
    /// <param name="bounds">
    /// 选区的外接矩形，位于文档坐标系（铁律 1：左上原点、Y 向下）。
    /// 其 <see cref="DocRect.Size"/> 必须与 <paramref name="coverage"/> 的尺寸逐字相等。
    /// </param>
    /// <param name="coverage">该矩形内的覆盖度数据。</param>
    /// <returns>覆盖度为 <paramref name="coverage"/> 的选区。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="coverage"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="bounds"/> 的尺寸与 <paramref name="coverage"/> 的宽高不一致。
    /// </exception>
    /// <remarks>
    /// <para><b>本方法只做「挂载」，不含任何选区算法。</b>
    /// 魔棒、套索、色彩范围怎么算出 <paramref name="coverage"/>，那是 AI-4 的事；
    /// 本方法只负责把「一块矩形 + 一块覆盖度」组装成一个自洽的
    /// <see cref="SelectionMask"/>，并挡住不自洽的组合。</para>
    ///
    /// <para><b>为什么必须校验尺寸一致，而不是自动裁剪或补齐。</b>
    /// <see cref="CoverageAt"/> 会把文档坐标换算成覆盖度缓冲的局部坐标再索引；
    /// 两者尺寸不一致时，这个换算的语义就是模糊的 ——
    /// 到底是覆盖度比矩形大（要裁）、比矩形小（要补 0）还是错位了（要平移）？
    /// 三种修法会给出三种不同画面，且<b>都不会让任何测试变红</b>，只会让选区看起来偏了一点。
    /// 在构造点炸掉是唯一能把问题暴露在源头的方式。</para>
    ///
    /// <para><b>为什么允许 <see cref="DocRect.Empty"/> 进来。</b>
    /// 尺寸校验会把空矩形挡掉（0 ≠ 覆盖度宽高），所以本方法不会产出「有边界但空」的选区；
    /// 需要空选区请用 <see cref="Empty"/>。</para>
    /// </remarks>
    public static SelectionMask FromCoverage(DocRect bounds, Coverage8 coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        if (bounds.Size.Width != coverage.Width || bounds.Size.Height != coverage.Height)
        {
            throw new ArgumentException(
                $"边界尺寸 {bounds.Size.Width}×{bounds.Size.Height} 与覆盖度尺寸 " +
                $"{coverage.Width}×{coverage.Height} 不一致。",
                nameof(coverage));
        }

        return new SelectionMask(bounds, coverage);
    }

    /// <summary>取值 0..255，区域外为 0。</summary>
    /// <param name="p">文档坐标系中的点，Y 向下（铁律 1）。</param>
    /// <returns>该点的覆盖度；点在 <see cref="Bounds"/> 外时返回 0。</returns>
    /// <remarks>
    /// 这是<b>访问覆盖度的唯一正确入口</b>：它同时处理了两种表示
    /// （1×1 均匀代理与全分辨率缓冲），并把文档坐标换算到覆盖度缓冲的局部坐标。
    /// 直接索引 <see cref="Coverage8.Data"/> 既会漏掉均匀代理，又容易把 Y 写反。
    /// </remarks>
    public byte CoverageAt(DocPoint p)
    {
        if (!Bounds.Contains(p))
        {
            return 0;
        }

        if (Coverage.IsUniform)
        {
            return Coverage.Data[0];
        }

        // 文档坐标 → 覆盖度缓冲局部坐标。Bounds 是半开区间，Contains 已保证在界内，
        // 这里仍要钳一次：浮点坐标落在右/下边界附近时，(int) 截断可能等于 Width/Height。
        int ix = (int)(p.X - Bounds.Left);
        int iy = (int)(p.Y - Bounds.Top);
        if (ix < 0 || iy < 0 || ix >= Coverage.Width || iy >= Coverage.Height)
        {
            return 0;
        }

        return Coverage.Data[iy * Coverage.Width + ix];
    }
}
