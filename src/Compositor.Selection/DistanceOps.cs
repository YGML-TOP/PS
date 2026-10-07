namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 选区的扩展与收缩（Select ▸ Modify ▸ Expand / Contract）。
/// </summary>
/// <remarks>
/// 🔴 <b>已知差异分类：确定不等价（算法路径不同）。</b>
/// Mac 版 <c>resizeSelection(by:name:)</c>（<c>Document/Selection.swift:333-342</c>）用的是
/// <c>path.copy(strokingWithWidth:)</c> + 路径布尔，属于 CoreGraphics 的<b>路径描边</b>，
/// 无公开规范。本实现走<b>光栅化后的欧氏距离变换</b>，两者不可逐位对齐。
/// 详见 <see cref="Expand"/> 的说明。
/// </remarks>
public static class DistanceOps
{
    /// <summary>单个方向可施加的最大扩张量，对齐 Mac 版 <c>abs(delta) &lt;= 500</c> 的守卫（<c>Selection.swift:334</c>）。</summary>
    public const int MaxAmount = 500;

    /// <summary>
    /// 距离场里表示"这里没有参考像素"的哨兵值。
    /// </summary>
    /// <remarks>
    /// 必须是<b>有限</b>数。抛物线下包络算法要算 <c>(f[q] + q²) − (f[t] + t²)</c>，
    /// 若用 <see cref="double.PositiveInfinity"/> 则两项皆无穷、结果为 <see cref="double.NaN"/>，
    /// 而 <c>NaN</c> 的比较恒为 <see langword="false"/>，包络维护会静默走错分支。
    /// 1e20 远大于文档尺度下的最大距离平方（约 3.6e9，30000px 见方），
    /// 任何真实距离都远小于它，因此不会被误判。
    /// </remarks>
    private const double FarDistance = 1e20;

    /// <summary>扩张量上限的最小值，对齐 <c>confirmSelectionAmount</c>（<c>Selection.swift:307</c>）。</summary>
    public const int MinAmount = 1;

    /// <summary>按带符号的增量重设选区轮廓。</summary>
    /// <param name="plane">当前覆盖度平面。</param>
    /// <param name="delta">正数扩张、负数收缩，绝对值不得超过 <see cref="MaxAmount"/>。</param>
    /// <param name="clipTo">裁剪到该矩形（通常是画布）；<see langword="null"/> 表示不裁剪。</param>
    /// <returns>调整后的覆盖度平面；收缩到消失时返回全零平面。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plane"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> 为 0 或绝对值超过 <see cref="MaxAmount"/>。</exception>
    /// <remarks>
    /// 对应 Mac 版 <c>resizeSelection(by:name:)</c>（<c>Selection.swift:333-342</c>）。
    /// 注意 Mac 版<b>只对扩张做画布裁剪</b>（<c>Selection.swift:338-339</c> 的
    /// <c>.intersection(canvas)</c>），收缩分支（<c>:340</c>）不裁剪 ——
    /// 收缩只会让形状变小，本来就不会越出画布。本实现保持同样口径。
    /// </remarks>
    public static CoveragePlane Resize(CoveragePlane plane, int delta, DocRect? clipTo = null)
    {
        ArgumentNullException.ThrowIfNull(plane);

        if (delta == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "增量不能为 0。");
        }

        int amount = Math.Abs(delta);
        if (amount > MaxAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta), delta, $"绝对值不得超过 {MaxAmount}（照抄 Selection.swift:334）。");
        }

        return delta > 0 ? Expand(plane, amount, clipTo) : Contract(plane, amount);
    }

    /// <summary>扩张选区：把轮廓向外推开 <paramref name="amount"/> 像素。</summary>
    /// <param name="plane">当前覆盖度平面。</param>
    /// <param name="amount">扩张像素数，取值 1–<see cref="MaxAmount"/>。</param>
    /// <param name="clipTo">裁剪到该矩形（通常是画布）；<see langword="null"/> 表示不裁剪。</param>
    /// <returns>扩张后的覆盖度平面。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plane"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> 不在 1–<see cref="MaxAmount"/> 内。</exception>
    /// <remarks>
    /// 🔴 <b>为什么这不是照抄。</b> Mac 版用
    /// <c>path.copy(strokingWithWidth: |delta| * 2, lineCap: .round, lineJoin: .round, miterLimit: 10)</c>
    /// 生成一条宽度为 <c>2×delta</c> 的描边带（CG 的描边以路径为中心，
    /// 故向两侧各推开 <c>delta</c>），再与原路径求并。
    /// <c>lineCap/.lineJoin = .round</c> 产生的形状在数学上<b>就是</b>"到原形状的距离 ≤ delta"的区域，
    /// 本实现用精确欧氏距离变换求同一集合。
    /// <para><b>但差异仍然存在，且不可消除：</b></para>
    /// <list type="number">
    /// <item><description>
    /// Mac 的输入是<b>路径</b>，本实现的输入是<b>光栅化后的覆盖度</b>。
    /// 抗锯齿边缘上覆盖度大于 0 的像素会被本实现判为"内部"，
    /// 使实际扩张范围比 Mac 多出约半个像素。
    /// </description></item>
    /// <item><description>
    /// CGPath 的布尔运算会输出<b>简化后的几何路径</b>，
    /// Mac 描边的是简化后的结果；本实现无路径概念，无法复现该简化。
    /// </description></item>
    /// </list>
    /// <para>所以本方法属于<b>语义近似</b>，不声称与 Mac 版逐位一致。</para>
    /// </remarks>
    public static CoveragePlane Expand(CoveragePlane plane, int amount, DocRect? clipTo = null)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ValidateAmount(amount);

        DocRect target = clipTo is null
            ? plane.Bounds.Inflate(amount)
            : plane.Bounds.Inflate(amount).Intersect(clipTo.Value);

        if (target.IsEmpty)
        {
            return CoveragePlane.CreateZero(plane.Bounds);
        }

        int w = target.Size.Width;
        int h = target.Size.Height;
        var result = new byte[w * h];
        bool hasReference;
        double[] distSq = EuclideanDistanceSquared(plane, target, referenceIsUnselected: false, out hasReference);
        int limit = amount * amount;

        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                if (distSq[rowBase + x] <= limit)
                {
                    result[rowBase + x] = 255;
                }
            }
        }

        var grown = new CoveragePlane(target, result);

        // 圆角带不会顶满扩张出来的矩形，trim 后包围盒才与内容一致
        // （Mac 版报的是 path.boundingBoxOfPath，同理只包住真实轮廓）。
        return grown.TrimToContent() ?? CoveragePlane.CreateZero(target);
    }

    /// <summary>收缩选区：把轮廓向内收进 <paramref name="amount"/> 像素。</summary>
    /// <param name="plane">当前覆盖度平面。</param>
    /// <param name="amount">收缩像素数，取值 1–<see cref="MaxAmount"/>。</param>
    /// <returns>收缩后的覆盖度平面；收缩到消失时返回包围盒不变的全零平面。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plane"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> 不在 1–<see cref="MaxAmount"/> 内。</exception>
    /// <remarks>
    /// 照抄 Mac 版收缩分支（<c>Selection.swift:340</c>）：
    /// <c>current.path.subtracting(band, using: .winding)</c>。
    /// <para>
    /// ⚠️ 对应 Mac 版注释 <c>Selection.swift:320</c>「收缩越过中线后留下一个<b>显式的空选区</b>」——
    /// 本实现返回<b>包围盒不变但全为 0</b> 的平面，正是"显式空选区"，
    /// 而不是"无选区"（<see langword="null"/>）。二者的区别见
    /// <c>SelectionTests.swift:55 emptySelectionIsDistinctFromNoSelection</c>。
    /// </para>
    /// </remarks>
    public static CoveragePlane Contract(CoveragePlane plane, int amount)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ValidateAmount(amount);

        // 🔴 收缩的判据是"到<b>未选中</b>像素的距离 > amount"，
        // 而包围盒<b>之外</b>全部是未选中的。
        // 所以距离场必须算在扩张过的区域上，否则实心选区内部找不到任何参考点
        // （每个像素自己就是参考点，距离恒为 0），整个选区会被整体抹光。
        DocRect probe = plane.Bounds.Inflate(amount);
        if (probe.IsEmpty)
        {
            return CoveragePlane.CreateZero(plane.Bounds);
        }

        bool hasReference;
        double[] distSq = EuclideanDistanceSquared(plane, probe, referenceIsUnselected: true, out hasReference);

        if (!hasReference)
        {
            return CoveragePlane.CreateZero(plane.Bounds);
        }

        int w = probe.Size.Width;
        int h = probe.Size.Height;
        var result = new byte[w * h];
        int limit = amount * amount;

        for (int i = 0; i < result.Length; i++)
        {
            if (distSq[i] > limit)
            {
                result[i] = 255;
            }
        }

        // 内容收缩了，包围盒必须跟着收，否则报出去的外接框仍是扩张后的大小
        // （Mac 版报的是收缩后的 path.boundingBoxOfPath）。
        return new CoveragePlane(probe, result).TrimToContent()
            ?? CoveragePlane.CreateZero(plane.Bounds);
    }

    /// <summary>校验扩张/收缩量落在 1–<see cref="MaxAmount"/> 内。</summary>
    /// <param name="amount">待校验的像素数。</param>
    /// <exception cref="ArgumentOutOfRangeException">越界时抛出。</exception>
    private static void ValidateAmount(int amount)
    {
        if (amount < MinAmount || amount > MaxAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, $"必须落在 [{MinAmount}, {MaxAmount}] 内（照抄 Selection.swift:307/334）。");
        }
    }

    /// <summary>
    /// 求 <paramref name="target"/> 内每个像素到最近<b>参考像素</b>的欧氏距离平方。
    /// </summary>
    /// <param name="source">覆盖度平面，提供参考像素的取值来源。</param>
    /// <param name="target">要计算距离的矩形，可超出 <paramref name="source"/> 的包围盒。</param>
    /// <param name="referenceIsUnselected">
    /// 参考点取哪一侧：<see langword="false"/> 时参考点是<b>已选中</b>像素（供 <see cref="Expand"/> 用，
    /// 保留"距选区 ≤ amount"的像素）；<see langword="true"/> 时参考点是<b>未选中</b>像素
    /// （供 <see cref="Contract"/> 用，保留"距选区外沿 &gt; amount"的像素）。
    /// </param>
    /// <param name="hasReference">目标区域内是否存在参考像素。</param>
    /// <returns>长度为目标像素数的距离平方数组；无参考像素时全为哨兵值 <see cref="FarDistance"/>。</returns>
    /// <remarks>
    /// 算法是 Felzenszwalb &amp; Huttenlocher 的<b>精确</b>欧氏距离变换
    /// （两趟一维抛物线下包络），O(W×H)，结果<b>精确</b>而非倒距离近似那种会差 1 像素的做法。
    /// <para>
    /// 🔴 <b>用有限的哨兵值而不是 <see cref="double.PositiveInfinity"/>。</b>
    /// 下包络算法要算 <c>(f[q] + q²) − (f[t] + t²)</c>；
    /// 当两项都是真正的无穷大时这是 <c>∞ − ∞ = NaN</c>，
    /// 而 <c>NaN</c> 的比较恒为 <see langword="false"/>，包络维护会静默走错分支、算出错误距离。
    /// 哨兵取 1e20，远大于文档尺度下的最大距离平方（约 3.6e9），又保证中间量全部有限。
    /// </para>
    /// </remarks>
    private static double[] EuclideanDistanceSquared(
        CoveragePlane source,
        DocRect target,
        bool referenceIsUnselected,
        out bool hasReference)
    {
        int w = target.Size.Width;
        int h = target.Size.Height;
        double[] grid = new double[w * h];

        Array.Fill(grid, FarDistance);
        bool anyReference = false;
        int offX = (int)(source.Bounds.Left - target.Left);
        int offY = (int)(source.Bounds.Top - target.Top);
        ReadOnlySpan<byte> src = source.Pixels;

        for (int y = 0; y < h; y++)
        {
            int sy = y - offY;
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int sx = x - offX;
                bool selected = (uint)sy < (uint)source.Height
                    && (uint)sx < (uint)source.Width
                    && src[(sy * source.Width) + sx] != 0;

                // 扩张时参考点是"已选中"，收缩时参考点是"未选中"（包围盒之外全是）。
                bool isReference = referenceIsUnselected ? !selected : selected;
                if (isReference)
                {
                    grid[rowBase + x] = 0.0;
                    anyReference = true;
                }
            }
        }

        hasReference = anyReference;

        if (!anyReference)
        {
            return grid;
        }

        var scratch = new double[Math.Max(w, h)];

        // 第一趟：逐列做一维变换，得到"同一列内最近内部像素"的距离平方。
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                scratch[y] = grid[(y * w) + x];
            }

            Transform1D(scratch, h);

            for (int y = 0; y < h; y++)
            {
                grid[(y * w) + x] = scratch[y];
            }
        }

        // 第二趟：逐行做一维变换，合成二维欧氏距离平方。
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            Array.Copy(grid, rowBase, scratch, 0, w);
            Transform1D(scratch, w);
            Array.Copy(scratch, 0, grid, rowBase, w);
        }

        return grid;
    }

    /// <summary>
    /// 一维平方距离变换：<c>d[q] = min_t ((q - t)^2 + f[t])</c>，就地改写 <paramref name="f"/>。
    /// </summary>
    /// <param name="f">输入为各位置的初始代价（0 或无穷大），输出为距离平方。</param>
    /// <param name="n">数组有效长度。</param>
    /// <remarks>
    /// 经典的下包络（lower envelope）抛物线算法。每个候选 <c>t</c> 对应一条抛物线
    /// <c>(q-t)^2 + f[t]</c>，算法维护这些抛物线的下包络，使求最小值退化为线性扫描。
    /// 时间 O(n)，且结果<b>精确</b>。
    /// </remarks>
    private static void Transform1D(double[] f, int n)
    {
        if (n <= 0)
        {
            return;
        }

        var v = new int[n];
        var z = new double[n + 1];
        int k = 0;

        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double s = Intersect(f, v[k], q);

            while (s <= z[k])
            {
                k--;
                s = Intersect(f, v[k], q);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            int t = v[k];
            long d = (long)q - t;
            f[q] = ((double)(d * d)) + f[t];
        }
    }

    /// <summary>抛物线 <c>f[q]</c> 与包络中 <paramref name="t"/> 号抛物线的交点。</summary>
    /// <param name="f">初始代价数组。</param>
    /// <param name="t">包络中的顶点下标。</param>
    /// <param name="q">新抛物线的下标。</param>
    /// <returns>两条抛物线的交点横坐标。</returns>
    private static double Intersect(double[] f, int t, int q)
    {
        double num = (f[q] + ((double)q * q)) - (f[t] + ((double)t * t));
        double den = 2.0 * (q - t);
        return num / den;
    }
}