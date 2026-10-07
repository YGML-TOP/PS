namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 选区组合模式。逐项对照 Mac 版 <c>SelectionMode</c>（<c>Document/Selection.swift:85-89</c>）。
/// </summary>
/// <remarks>
/// ⚠️ <b>Mac 版只有这三种，没有"交"也没有"排除"。</b>
/// <c>Selection.swift:238-245</c> 的分支只覆盖 replace / add / subtract，
/// 且该枚举是 <c>CaseIterable</c> 的，穷尽即三种。
/// 交集在 <c>Selection.swift:236</c> 出现过，但那是"与画布矩形求交"，
/// <b>不是两个选区求交</b>；排除（XOR）在 Mac 版<b>完全不存在</b>。
/// 因此本枚举保持与 Mac 版一致的三值，不擅自扩充。
/// </remarks>
public enum SelectionCombineMode
{
    /// <summary>替换为新选区。对应 Mac 版 <c>SelectionMode.replace</c>（"New"）。</summary>
    Replace = 0,

    /// <summary>与现有选区求并。对应 Mac 版 <c>SelectionMode.add</c>。</summary>
    Add = 1,

    /// <summary>从现有选区中扣除。对应 Mac 版 <c>SelectionMode.subtract</c>。</summary>
    Subtract = 2,
}

/// <summary>
/// 覆盖度平面的布尔运算。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>参照实现的本质差异（写在这里以免后人误以为这是逐位移植）。</b>
/// Mac 版 <c>applySelection</c>（<c>Selection.swift:233-247</c>）用的是
/// <c>CGPath.union / subtracting / intersection(using: .winding)</c>，
/// 即 <b>CoreGraphics 的路径布尔运算</b>。该运算<b>闭源、无公开规范</b>，
/// 且只在<b>二值</b>几何上有定义（内部 1、外部 0）。
/// </para>
/// <para>
/// 本工程工作在 8-bit 连续覆盖度上，必须给二值语义选一个连续延拓。
/// 本文件的取法是四个逐像素算子，全部满足：
/// <b>在二值输入上与 Mac 版的路径布尔完全一致</b>（0/1 上 max=OR、min=AND、
/// <c>max(0,a-b)</c>=AND-NOT、<c>|a-b|</c>=XOR），
/// 差异只出现在 Mac 版根本不存在的"半覆盖"区域。
/// </para>
/// </remarks>
public static class BooleanOps
{
    /// <summary>并集：逐像素取较大覆盖度。二值输入下等价于 OR。</summary>
    /// <param name="a">左侧覆盖度平面。</param>
    /// <param name="b">右侧覆盖度平面。</param>
    /// <returns>覆盖度为两者并集的平面，包围盒为两者的最小外接矩形。</returns>
    public static CoveragePlane Union(CoveragePlane a, CoveragePlane b) =>
        Compose(a, b, static (x, y) => (byte)Math.Max(x, y));

    /// <summary>交集：逐像素取较小覆盖度。二值输入下等价于 AND。</summary>
    /// <param name="a">左侧覆盖度平面。</param>
    /// <param name="b">右侧覆盖度平面。</param>
    /// <returns>覆盖度为两者交集的平面。</returns>
    /// <remarks>
    /// ⚠️ <b>Mac 版没有"两个选区求交"这个操作</b>（<c>SelectionMode</c> 只有三值，
    /// <c>Selection.swift:238-245</c> 可证）。本方法是按任务书要求补的，
    /// 其二值语义与 AND 一致，但<b>不应声称是照抄 Mac 版</b>。
    /// </remarks>
    public static CoveragePlane Intersect(CoveragePlane a, CoveragePlane b) =>
        Compose(a, b, static (x, y) => (byte)Math.Min(x, y));

    /// <summary>差集：从 <paramref name="a"/> 中扣除 <paramref name="b"/>。二值输入下等价于 AND-NOT。</summary>
    /// <param name="a">被减的覆盖度平面。</param>
    /// <param name="b">要扣除的覆盖度平面。</param>
    /// <returns>扣除后的覆盖度平面。</returns>
    public static CoveragePlane Subtract(CoveragePlane a, CoveragePlane b) =>
        Compose(a, b, static (x, y) => (byte)Math.Max(0, x - y));

    /// <summary>排除（XOR）：只被其中一方覆盖的像素保留。</summary>
    /// <param name="a">左侧覆盖度平面。</param>
    /// <param name="b">右侧覆盖度平面。</param>
    /// <returns>排除后的覆盖度平面。</returns>
    /// <remarks>
    /// ⚠️ <b>Mac 版不存在排除操作。</b> 源码可证：<c>SelectionMode</c>
    /// （<c>Selection.swift:85-89</c>）只有 replace / add / subtract 三个值且是
    /// <c>CaseIterable</c> 的，<c>Selection.swift:238-245</c> 也确实只处理这三种。
    /// 本方法按 Photoshop 的 Select ▸ Subtract ▸ Inverse 语义实现，
    /// <b>属于补充能力而非移植</b>，不得作为"与 Mac 版一致"的证据。
    /// </remarks>
    public static CoveragePlane Exclude(CoveragePlane a, CoveragePlane b) =>
        Compose(a, b, static (x, y) => (byte)Math.Abs(x - y));

    /// <summary>
    /// 按组合模式把新选区并入当前选区，逐条照抄 Mac 版 <c>applySelection</c>（<c>Selection.swift:233-247</c>）。
    /// </summary>
    /// <param name="mode">组合模式。</param>
    /// <param name="current">当前选区；<see langword="null"/> 表示<b>根本没有选区</b>。</param>
    /// <param name="incoming">新产生的选区。</param>
    /// <returns>组合后的选区；结果为"无选区"时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 🔴 <b>"无选区"与"空选区"是两件事，本方法是它们分叉的地方。</b>
    /// <c>Selection.swift:4-6</c> 的注释写明：
    /// <c>nil</c> 表示文档上没有选区，而<b>路径为空的选区是一个显式的空选区</b>，
    /// 后续编辑必须把它当作"什么都不碰"，绝不能当作"什么都碰"。
    /// <c>SelectionTests.swift:55 emptySelectionIsDistinctFromNoSelection</c> 就在守这条。
    /// <para>对应的三个分支：</para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Replace</c>（<c>Selection.swift:239</c>）：直接返回新选区，旧的不管是不是空。
    /// </description></item>
    /// <item><description>
    /// <c>Add</c>（<c>Selection.swift:240</c>）：<c>selection.map { …union… } ?? clipped</c> ——
    /// 没有选区时等价于 Replace。
    /// </description></item>
    /// <item><description>
    /// <c>Subtract</c>（<c>Selection.swift:242-243</c>）：
    /// <c>guard let current = selection else { return }</c> ——
    /// <b>从"根本没有选区"里减去东西，什么都不做，保持无选区</b>，
    /// 而不是凭空造出一个空选区。这条最容易实现错。
    /// </description></item>
    /// </list>
    /// </remarks>
    public static CoveragePlane? Combine(SelectionCombineMode mode, CoveragePlane? current, CoveragePlane incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        switch (mode)
        {
            case SelectionCombineMode.Replace:
                return incoming;

            case SelectionCombineMode.Add:
                return current is null ? incoming : Union(current, incoming);

            case SelectionCombineMode.Subtract:
                // 照抄 guard let current = selection else { return }：从无选区减去 = 维持无选区。
                return current is null ? null : Subtract(current, incoming);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的组合模式。");
        }
    }

    /// <summary>
    /// 逐像素合成两个平面。两者包围盒对齐到最小外接矩形后同坐标合成。
    /// </summary>
    /// <param name="a">左侧覆盖度平面。</param>
    /// <param name="b">右侧覆盖度平面。</param>
    /// <param name="op">逐像素合成算子。</param>
    /// <returns>合成结果平面，包围盒为两者外接矩形。</returns>
    private static CoveragePlane Compose(CoveragePlane a, CoveragePlane b, Func<byte, byte, byte> op)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(op);

        DocRect bounds = a.Bounds.Union(b.Bounds);
        int w = bounds.Size.Width;
        int h = bounds.Size.Height;
        var result = new byte[w * h];

        // 目标缓冲的局部坐标 = 文档坐标 - bounds.Origin，
        // 因此源平面的第 0 列在目标缓冲里位于 (源 Left - bounds.Left)。
        // 两个包围盒都来自 DocSize(int)，偏移量必然是整数，可直接转 int。
        int aOffX = (int)(a.Bounds.Left - bounds.Left);
        int aOffY = (int)(a.Bounds.Top - bounds.Top);
        int bOffX = (int)(b.Bounds.Left - bounds.Left);
        int bOffY = (int)(b.Bounds.Top - bounds.Top);

        ReadOnlySpan<byte> ap = a.Pixels;
        ReadOnlySpan<byte> bp = b.Pixels;

        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            int ay = y - aOffY;
            int by = y - bOffY;

            // 行是否落在源缓冲内。逐行算一次即可，不必每个像素重复判断。
            bool aRowOk = (uint)ay < (uint)a.Height;
            bool bRowOk = (uint)by < (uint)b.Height;
            int aRowBase = aRowOk ? (ay * a.Width) : 0;
            int bRowBase = bRowOk ? (by * b.Width) : 0;

            for (int x = 0; x < w; x++)
            {
                byte av = 0;
                byte bv = 0;

                // 🔴 这里是减号：目标缓冲局部 x 对应文档坐标 bounds.Left + x，
                // 而源的局部坐标是 文档坐标 - src.Bounds.Left，
                // 两者相减得 (bounds.Left + x) - src.Bounds.Left = x - (src.Bounds.Left - bounds.Left) = x - offX。
                // 写成加号会让偏移方向反转，选区越靠右偏得越多，
                // 表现为"两个不相邻的选区做并集后，中间大片变空"。
                if (aRowOk)
                {
                    int ax = x - aOffX;
                    if ((uint)ax < (uint)a.Width)
                    {
                        av = ap[aRowBase + ax];
                    }
                }

                if (bRowOk)
                {
                    int bx = x - bOffX;
                    if ((uint)bx < (uint)b.Width)
                    {
                        bv = bp[bRowBase + bx];
                    }
                }

                result[rowBase + x] = op(av, bv);
            }
        }

        return new CoveragePlane(bounds, result);
    }
}