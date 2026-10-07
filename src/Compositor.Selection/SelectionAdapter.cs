namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 一条选区在文档里的完整状态：光栅化后的覆盖度、是否抗锯齿、羽化量。
/// </summary>
/// <remarks>
/// 对应 Mac 版 <c>DocumentSelection</c>（<c>Document/Selection.swift:7-30</c>）。
/// <para><b>为什么单独建这个类型而不是直接用 <see cref="SelectionMask"/>。</b>
/// Mac 版把「羽化量」和「抗锯齿开关」挂在<b>路径</b>上，而 <see cref="SelectionMask"/>
/// 只持有 (包围盒, 覆盖度) —— 羽化在 Mac 版是<b>光栅化时</b>才兑现的（<c>Selection.swift:24-28</c>），
/// 不是路径上的属性。所以本类型负责把「路径 + 羽化 + 抗锯齿」一起兑现成最终覆盖度，
/// 再交给 <see cref="SelectionMask"/>。</para>
/// </remarks>
public sealed class SelectionDocument
{
    /// <summary>光栅化并羽化之后的覆盖度；已按 <see cref="CoverageBounds"/> 扩张好包围盒。</summary>
    public CoveragePlane Coverage { get; }

    /// <summary>是否开启抗锯齿，对应 Mac 版 <c>DocumentSelection.antialiased</c>（<c>Selection.swift:9</c>）。</summary>
    public bool Antialiased { get; }

    /// <summary>边缘羽化量（文档像素），0 表示硬边，对应 Mac 版 <c>Selection.swift:11</c>。</summary>
    public double Feather { get; }

    /// <summary>是否是一条「显式的空选区」。</summary>
    /// <remarks>
    /// 🔴 <b>「空选区」与「无选区」必须分开。</b> Mac 版 <c>Selection.swift:4-6</c> 的注释写得很明确：
    /// 路径为空的 <c>DocumentSelection</c> 是<b>显式空选区</b>，后续编辑必须当成「什么都不碰」，
    /// 绝不能当成「什么都碰」。本类型用「有对象但覆盖度全 0」表示它，
    /// 用 <see langword="null"/>（整个引用为 null）表示「无选区」。
    /// 守这条的测试：<c>SelectionTests.swift:55 emptySelectionIsDistinctFromNoSelection</c>。
    /// </remarks>
    public bool IsEmpty => !Coverage.HasContent();

    /// <summary>
    /// 真正送进光栅器的抗锯齿开关。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>照抄 <c>Selection.swift:19</c>：<c>context.setShouldAntialias(antialiased || feather &gt; 0)</c>。</b>
    /// <para>
    /// 这是<b>条件触发</b>，不是"永远开"：
    /// 只有 <see cref="Antialiased"/> 为 <see langword="true"/>
    /// <b>或者</b> <see cref="Feather"/> 大于 0 时才抗锯齿。
    /// </para>
    /// <para>
    /// <b>漏掉 <c>|| feather &gt; 0</c> 这一半会怎样：</b>选区被显式关掉抗锯齿、再施加羽化时，
    /// Mac 版仍会抗锯齿（否则羽化边缘本身就带阶梯），而漏掉的实现会退化成单点采样，
    /// 羽化前的轮廓就已经是阶梯状，羽化只会把阶梯抹平一点，边缘出现可见的台阶。
    /// </para>
    /// <para>
    /// 反向也要注意：<see cref="Antialiased"/> 为 <see langword="true"/> 且
    /// <see cref="Feather"/> 为 0 时，开销来自抗锯齿本身；两者都为假时光栅器退化为
    /// <b>单点采样</b>，输出只可能是 0 或 255，不产生任何边缘灰度。
    /// </para>
    /// </remarks>
    public bool ShouldAntialias => Antialiased || Feather > 0;

    internal SelectionDocument(CoveragePlane coverage, bool antialiased, double feather)
    {
        Coverage = coverage;
        Antialiased = antialiased;
        Feather = feather;
    }

    /// <summary>
    /// 把「形状 + 羽化 + 抗锯齿」兑现成最终覆盖度，逐字照抄 Mac 版
    /// <c>DocumentSelection.coverage(width:height:)</c>（<c>Selection.swift:15-29</c>）。
    /// </summary>
    /// <param name="pathBounds">轮廓自身的包围盒（<b>不含</b>羽化余量）。</param>
    /// <param name="inside">形状内部的判定回调。</param>
    /// <param name="antialiased">对应 Mac 版 <c>antialiased</c> 字段。</param>
    /// <param name="feather">羽化量，0–<see cref="FeatherOps.MaxFeather"/>；0 表示硬边。</param>
    /// <returns>已羽化、包围盒已按 <see cref="CoverageBounds"/> 扩张的选区。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inside"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="feather"/> 为负数或超过 <see cref="FeatherOps.MaxFeather"/>。</exception>
    /// <remarks>
    /// Mac 版这四步的顺序不能调换：
    /// <list type="number">
    /// <item><description>
    /// 先把整幅填充成黑（未选中），保证抗锯齿边缘之外是 0 而不是背景色。
    /// </description></item>
    /// <item><description>
    /// 再按 <see cref="ShouldAntialias"/> 设置抗锯齿并填白。
    /// </description></item>
    /// <item><description>
    /// 仅当 <c>feather &gt; 0</c> 时，对<b>光栅化结果</b>施加
    /// <c>CIImage.applyingGaussianBlur(sigma: feather / 2)</c>（<c>Selection.swift:27</c>）。
    /// 注意模糊的是<b>栅格</b>，不是解析地模糊轮廓 —— 这决定了羽化结果与手写的解析羽化不可逐位对齐。
    /// </description></item>
    /// <item><description>
    /// 模糊后裁回原范围（Mac 版 <c>clampedToExtent()</c> 再 <c>cropped(to: extent)</c>）。
    /// 本实现用「扩张区外视为 0」等价掉这两步，理由见 <c>FeatherOps.Apply</c> 的说明。
    /// </description></item>
    /// </list>
    /// </remarks>
    public static SelectionDocument Rasterize(
        DocRect pathBounds,
        Antialias.PointTest inside,
        bool antialiased,
        double feather = 0)
    {
        ArgumentNullException.ThrowIfNull(inside);

        if (feather < 0 || feather > FeatherOps.MaxFeather)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feather), feather, $"必须落在 [0, {FeatherOps.MaxFeather}] 内（照抄 Selection.swift:307/330）。");
        }

        // 羽化会把边缘扩散出去，先把包围盒撑到 4σ，否则渐变会在包围盒边界被硬切。
        bool antialiasedForRaster = antialiased || feather > 0;
        DocRect full = FeatherOps.CoverageBounds(pathBounds, feather);
        CoveragePlane plane = Antialias.Rasterize(full, inside, antialiasedForRaster);

        if (feather > 0)
        {
            plane = FeatherOps.Apply(plane, feather);
        }

        return new SelectionDocument(plane, antialiased, feather);
    }

    /// <summary>
    /// 羽化后覆盖度所需的包围盒，照抄 Mac 版 <c>coverageBounds</c>（<c>Selection.swift:34-36</c>）。
    /// </summary>
    public DocRect CoverageBounds => FeatherOps.CoverageBounds(Coverage.Bounds, Feather);

    /// <summary>把覆盖度包成契约层的 <see cref="SelectionMask"/>（契约 v1.2 的 <c>FromCoverage</c>）。</summary>
    /// <returns>可直接交给渲染/编辑层的选区。</returns>
    /// <exception cref="ArgumentNullException">本对象为 <see langword="null"/>。</exception>
    public SelectionMask ToSelectionMask() => SelectionAdapter.ToSelectionMask(Coverage);

    /// <summary>
    /// 以固定的抗锯齿开关与羽化量，把契约层的 <see cref="SelectionMask"/> 读回本类型。
    /// </summary>
    /// <param name="mask">契约层选区。</param>
    /// <param name="antialiased">抗锯齿开关。</param>
    /// <param name="feather">羽化量，0–<see cref="FeatherOps.MaxFeather"/>。</param>
    /// <returns>本类型的选区。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> 为 <see langword="null"/>。</exception>
    public static SelectionDocument FromSelectionMask(SelectionMask mask, bool antialiased, double feather = 0)
    {
        ArgumentNullException.ThrowIfNull(mask);

        if (feather < 0 || feather > FeatherOps.MaxFeather)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feather), feather, $"必须落在 [0, {FeatherOps.MaxFeather}] 内（照抄 Selection.swift:307/330）。");
        }

        return new SelectionDocument(SelectionAdapter.FromSelectionMask(mask), antialiased, feather);
    }
}

/// <summary>
/// 选区两层表示之间的桥：<see cref="CoveragePlane"/>（上层，几何运算用）
/// 与 <see cref="SelectionMask"/>（契约层，渲染/编辑用）。
/// </summary>
/// <remarks>
/// 契约 v1.2 新增了 <c>Coverage8.FromData</c> 与 <c>SelectionMask.FromCoverage</c>，
/// 这条通道此前是本 lane 的硬阻塞（<c>SelectionMask</c> 构造器 private、只有
/// <c>Empty</c>/<c>Full</c>、<c>Coverage8</c> 无外部字节入口）。
/// <para><b>为什么保留两层而不是统一成一层：</b>
/// 布尔运算需要按包围盒对齐后逐像素运算、扩展/收缩需要精确距离场，
/// 这些都要求「知道自己包围盒」的密集数组；而契约层要的是可以被裁剪、被文档持有的值对象。
/// 直接把上层数组塞进契约层会在每次运算时被迫反复重包装。</para>
/// </remarks>
public static class SelectionAdapter
{
    /// <summary>把上层覆盖度平面包成契约层的 <see cref="SelectionMask"/>。</summary>
    /// <param name="plane">上层覆盖度平面。</param>
    /// <returns>契约层选区；包围盒与覆盖度尺寸由契约层校验一致性。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plane"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 走契约 v1.2 的两步官方通道，<b>不做任何手工构造</b>：
    /// <list type="number">
    /// <item><description>
    /// <c>Coverage8.FromData(plane.Pixels, plane.Width, plane.Height)</c> ——
    /// 它要求数据长度<b>恰好</b>等于 <c>width × height</c>，不接受「至少」。
    /// 这正合适：多给字节几乎总是调用方算错了行距，那种情况应该在构造点炸掉，而不是静默少读一段。
    /// </description></item>
    /// <item><description>
    /// <c>SelectionMask.FromCoverage(plane.Bounds, coverage)</c> ——
    /// 它会校验 <c>bounds.Size</c> 与覆盖度宽高一致，不一致就抛。
    /// </description></item>
    /// </list>
    /// <para>
    /// ⚠️ <b>全零平面会得到一条「有包围盒但覆盖度全 0」的选区</b>，即「显式空选区」，
    /// 而不是「无选区」。要表达无选区请传 <see langword="null"/>，
    /// 见 <see cref="SelectionDocument.IsEmpty"/> 的说明。
    /// </para>
    /// </remarks>
    public static SelectionMask ToSelectionMask(CoveragePlane plane)
    {
        ArgumentNullException.ThrowIfNull(plane);

        Coverage8 coverage = Coverage8.FromData(plane.Pixels, plane.Width, plane.Height);
        return SelectionMask.FromCoverage(plane.Bounds, coverage);
    }

    /// <summary>把契约层的 <see cref="SelectionMask"/> 读回上层覆盖度平面。</summary>
    /// <param name="mask">契约层选区。</param>
    /// <returns>覆盖度平面，包围盒与 <see cref="SelectionMask.Bounds"/> 一致。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 走 <see cref="SelectionMask.CoverageAt"/> 而不是直接索引 <see cref="Coverage8.Data"/>：
    /// <c>CoverageAt</c> 同时处理了「1×1 均匀代理」与「全分辨率缓冲」两种表示，
    /// 并负责把文档坐标换算到缓冲局部坐标（Y 向下，铁律 1）。直接索引 <c>Data</c>
    /// 会漏掉均匀代理，也容易把 Y 写反。
    /// </remarks>
    public static CoveragePlane FromSelectionMask(SelectionMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        DocRect bounds = mask.Bounds;

        // SelectionMask.Empty 的包围盒是 DocRect.Empty（0×0），而 CoveragePlane 的构造器
        // 明确拒绝空包围盒 —— 它没法承载"无边界"这个状态。
        // 所以把契约层的 Empty 映射成一条"显式空选区"的 1×1 全零平面：
        // 语义上仍然是空（HasContent 为 false），又能通过上层构造器的校验。
        if (bounds.IsEmpty)
        {
            return EmptySelection();
        }

        int w = bounds.Size.Width;
        int h = bounds.Size.Height;
        var bytes = new byte[w * h];
        DocPoint origin = bounds.Origin;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                bytes[row + x] = mask.CoverageAt(new DocPoint(origin.X + x, origin.Y + y));
            }
        }

        return new CoveragePlane(bounds, bytes);
    }

    /// <summary>
    /// 照抄 Mac 版 <c>applySelection(_:mode:name:)</c>（<c>Selection.swift:233-247</c>）的完整语义。
    /// </summary>
    /// <param name="mode">组合模式，对应 Mac 版 <c>SelectionMode</c>。</param>
    /// <param name="current">当前选区；<see langword="null"/> 表示<b>无选区</b>。</param>
    /// <param name="incoming">新画出的形状（尚未裁剪到画布）。</param>
    /// <param name="canvas">画布尺寸，用于裁剪。</param>
    /// <param name="antialiased">写入新选区的抗锯齿开关，对应 Mac 版 <c>selectionAntialiased</c>。</param>
    /// <returns>组合后的选区；返回 <see langword="null"/> 表示<b>选区未发生变化</b>（仍无选区）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="incoming"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// Mac 版这四步的顺序与两个容易漏掉的细节：
    /// <list type="bullet">
    /// <item><description>
    /// 🔴 <b>先裁剪再布尔</b>：<c>Selection.swift:236</c> 是
    /// <c>shape.intersection(canvas, using: .winding)</c>，裁剪发生在<b>任何</b>布尔运算之前。
    /// 若漏掉这一步，画布外的那部分会先参与并/差运算，再随结果留在画布外 ——
    /// 对「加」是包围盒虚胖，对「差」是画布外也被挖掉。
    /// </description></item>
    /// <item><description>
    /// 🔴 <b>羽化归零</b>：<c>Selection.swift:246</c> 构造的是
    /// <c>DocumentSelection(path: antialiased:)</c>，<b>没有传 feather</b>，
    /// 于是走默认的 0。任何 Replace/Add/Subtract 都会把已有羽化清掉。
    /// 羽化只能靠 Feather 菜单项叠加，不会被画新选区保留下来。
    /// </description></item>
    /// <item><description>
    /// <c>Subtract</c> 且当前无选区时<b>直接返回、不改任何状态</b>
    /// （<c>Selection.swift:243</c> 的 <c>guard let current = selection else { return }</c>），
    /// 本方法以返回 <see langword="null"/> 表达。
    /// </description></item>
    /// <item><description>
    /// <c>Add</c> 且当前无选区时等价于 <c>Replace</c>
    /// （<c>Selection.swift:240</c> 的 <c>?? clipped</c>）。
    /// </description></item>
    /// </list>
    /// </remarks>
    public static SelectionDocument? Apply(
        SelectionCombineMode mode,
        SelectionDocument? current,
        CoveragePlane incoming,
        DocSize canvas,
        bool antialiased)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        // Selection.swift:243 —— 从"无选区"里减去东西 = 什么都不做，保持无选区。
        // 这条守卫必须排在裁剪之前：Mac 版是先 return，根本走不到裁剪后的布尔。
        if (mode == SelectionCombineMode.Subtract && current is null)
        {
            return null;
        }

        DocRect canvasRect = new(new DocPoint(0, 0), canvas);
        DocRect clipped = incoming.Bounds.Intersect(canvasRect);

        if (clipped.IsEmpty)
        {
            // 整块新形状落在画布外，裁剪后无内容。
            // 与空集做并/差都不改变已有选区，所以 Add/Subtract 维持当前选区；
            // 没有当前选区时 Replace/Add 落到一条"显式空选区"（Mac 版 result = clipped）。
            return current is not null
                ? new SelectionDocument(current.Coverage, antialiased, feather: 0)
                : new SelectionDocument(EmptySelection(), antialiased, feather: 0);
        }

        CoveragePlane clippedPlane = Slice(incoming, clipped);
        CoveragePlane? combined = BooleanOps.Combine(mode, current?.Coverage, clippedPlane);

        return combined is null ? null : new SelectionDocument(combined, antialiased, feather: 0);
    }

    /// <summary>
    /// 选区的扩展与收缩入口，照抄 Mac 版 <c>expandSelection</c> / <c>contractSelection</c>
    /// / <c>resizeSelection(by:name:)</c>（<c>Selection.swift:317, 321, 333-342</c>）。
    /// </summary>
    /// <param name="current">当前选区；<see langword="null"/> 或空选区时不做任何事。</param>
    /// <param name="delta">正数扩张、负数收缩；绝对值不得超过 <see cref="DistanceOps.MaxAmount"/>，且不得为 0。</param>
    /// <param name="canvas">画布尺寸。仅扩张分支会裁剪到它。</param>
    /// <returns>调整后的选区；输入为空或无选区时原样返回 <paramref name="current"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> 为 0 或绝对值超过 <see cref="DistanceOps.MaxAmount"/>。</exception>
    /// <remarks>
    /// <para><b>算法：描边式，不是形态学卷积。</b>
    /// Mac 版 <c>Selection.swift:336</c> 是
    /// <c>current.path.copy(strokingWithWidth: |delta| * 2, lineCap: .round, lineJoin: .round, miterLimit: 10)</c>
    /// —— 先把轮廓描成一条宽度 <c>2×delta</c> 的带（CG 描边以路径为中心，故每侧推开 <c>delta</c>），
    /// 再与原路径求并/差。</para>
    /// <para>
    /// <c>lineCap = .round</c> 且 <c>lineJoin = .round</c> 决定了这条带在数学上
    /// <b>恰好就是「到原轮廓的欧氏距离 ≤ delta」的区域</b>（圆端帽 = 圆盘，圆接头 = 圆锥包络）。
    /// 所以本实现用 <see cref="DistanceOps"/> 的<b>精确欧氏距离变换</b>求的是<b>同一个集合</b>，
    /// <b>不是</b>用结构元做卷积那种近似（那会把圆角磨成方角）。
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// 🔴 <b>只有扩张裁剪到画布，收缩不裁剪。</b> Mac 版 <c>Selection.swift:338-339</c> 在并集之后
    /// 接了 <c>.intersection(canvas)</c>，而 <c>:340</c> 的差集分支没有。
    /// 本方法照抄这个不对称。
    /// </description></item>
    /// <item><description>
    /// 本方法<b>保留</b>羽化量与抗锯齿开关（Mac 版 <c>Selection.swift:341</c>），
    /// 与 <see cref="Apply"/> 强制归零羽化正好相反 —— 这是两条不同的路径，别混。
    /// </description></item>
    /// <item><description>
    /// ⚠️ 剩余差异：Mac 的输入是<b>路径</b>，本实现输入的是<b>光栅化后的覆盖度</b>。
    /// 关闭抗锯齿时覆盖度只有 0/255，两者一致；
    /// 开启抗锯齿时边缘像素覆盖度 ∈ (0,1)，本实现把它判为"内部"，扩张范围会多出约半个像素。
    /// 这是<b>输入表示不同</b>导致的不等价，不是算法不同。
    /// </description></item>
    /// </list>
    /// </remarks>
    public static SelectionDocument? Resize(SelectionDocument? current, int delta, DocSize canvas)
    {
        // 对齐 Selection.swift:334 的 guard：无选区/空选区不改，delta 不得为 0，|delta| <= 500。
        if (current is null || current.IsEmpty)
        {
            return current;
        }

        int amount = Math.Abs(delta);

        if (delta == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "增量不能为 0（照抄 Selection.swift:334）。");
        }

        if (amount > DistanceOps.MaxAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta), delta, $"绝对值不得超过 {DistanceOps.MaxAmount}（照抄 Selection.swift:334）。");
        }

        // Mac 版 :339 只在扩张分支裁画布；收缩只会让形状变小，不裁。
        DocRect? clip = delta > 0 ? new DocRect(new DocPoint(0, 0), canvas) : null;
        CoveragePlane resized = DistanceOps.Resize(current.Coverage, delta, clip);

        return new SelectionDocument(resized, current.Antialiased, current.Feather);
    }

    /// <summary>
    /// 造一条「显式空选区」用的全零平面：1×1、覆盖度全 0。
    /// </summary>
    /// <remarks>
    /// 为什么不用 <see cref="DocRect.Empty"/>：契约 v1.2 的
    /// <c>Coverage8.FromData</c> 要求宽高<b>都大于 0</b>，<c>SelectionMask.FromCoverage</c>
    /// 又要求包围盒尺寸与覆盖度一致。0×0 的平面过不了这两道校验。
    /// 1×1 的全零平面既能通过校验，又让 <see cref="SelectionDocument.IsEmpty"/>
    /// 如实返回 <see langword="true"/>。
    /// </remarks>
    private static CoveragePlane EmptySelection() =>
        CoveragePlane.CreateZero(new DocRect(new DocPoint(0, 0), new DocSize(1, 1)));

    /// <summary>取平面在指定矩形内的子块（矩形必须已完全包含在平面包围盒内）。</summary>
    private static CoveragePlane Slice(CoveragePlane plane, DocRect region)
    {
        int w = region.Size.Width;
        int h = region.Size.Height;
        var bytes = new byte[w * h];
        int offX = (int)(region.Left - plane.Bounds.Left);
        int offY = (int)(region.Top - plane.Bounds.Top);
        byte[] src = plane.Pixels.ToArray();

        for (int y = 0; y < h; y++)
        {
            Array.Copy(src, ((y + offY) * plane.Width) + offX, bytes, y * w, w);
        }

        return new CoveragePlane(region, bytes);
    }
}