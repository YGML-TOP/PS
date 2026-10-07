namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 选区羽化（Select ▸ Modify ▸ Feather）。
/// </summary>
/// <remarks>
/// ⚠️ <b>已知差异分类：可能不一致。</b>
/// Mac 版在 <c>DocumentSelection.coverage</c>（<c>Document/Selection.swift:24-28</c>）里
/// 用 <c>CIImage.applyingGaussianBlur(sigma: feather / 2)</c> 做羽化。
/// CoreImage 的高斯模糊<b>闭源</b>：核的截断半径、边界重采样、定点/浮点实现都无法从源码或规范得知，
/// 因此本实现的羽化结果<b>不保证与 Mac 版逐像素一致</b>。
/// 本实现保证的是：羽化是<b>单调的边缘衰减</b>、常量区域不被改变、
/// 且同一输入永远得到同一输出。
/// </remarks>
public static class FeatherOps
{
    /// <summary>
    /// 羽化量上限，对齐 Mac 版 <c>min(250, softened)</c>（<c>Selection.swift:330</c>）。
    /// </summary>
    public const double MaxFeather = 250;

    /// <summary>单次羽化量的下界，对齐 <c>confirmSelectionAmount</c> 的 <c>1...250</c>（<c>Selection.swift:307</c>）。</summary>
    public const int MinAmount = 1;

    /// <summary>高斯核的截断半径，以标准差为单位。</summary>
    /// <remarks>
    /// 取 3σ：三倍标准差之外的高斯尾概率低于 0.3%，截断在视觉上不可分辨，
    /// 而核长正比于此值。CoreImage 内部的截断值未知，故本值是<b>本实现的工程选择</b>，
    /// 不声称与 Mac 一致。
    /// </remarks>
    private const double TruncationSigma = 3.0;

    /// <summary>
    /// 叠加两次羽化量，返回新的羽化量。
    /// </summary>
    /// <param name="current">当前羽化量，0 表示硬边。</param>
    /// <param name="amount">本次施加的羽化量，取值 <see cref="MinAmount"/>–<see cref="MaxFeather"/>。</param>
    /// <returns>叠加后的羽化量，范围 0–<see cref="MaxFeather"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> 不在 1–<see cref="MaxFeather"/> 内。</exception>
    /// <remarks>
    /// 🔴 <b>本方法是整个 M5 里最容易写错的一处，务必照抄。</b>
    /// Mac 版 <c>featherSelection(by:)</c>（<c>Document/Selection.swift:325-331</c>）是：
    /// <code>
    /// let softened = (current.feather * current.feather + CGFloat(amount) * CGFloat(amount)).squareRoot()
    /// setSelection(…, feather: min(250, softened), name: "Feather Selection")
    /// </code>
    /// 即 <b>(f₁² + f₂²)<sup>1/2</sup></b>，而不是 f₁ + f₂。
    /// 源码注释解释了原因（<c>Selection.swift:327</c>）：
    /// 「两条软边叠加后的扩散量比它们的和略小，如同模糊那样」——
    /// 这与高斯方差可加的性质一致。
    /// <para>
    /// 写成线性相加不会编译失败、也不会让任何一条断言报错，
    /// 只会在"羽化两次"这类用例上偏离 Mac 版 <b>约 15%</b>（f=6 时：√72≈8.49 vs 12）。
    /// </para>
    /// </remarks>
    public static double Combine(double current, int amount)
    {
        if (amount < MinAmount || amount > MaxFeather)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, $"必须落在 [{MinAmount}, {MaxFeather}] 内（照抄 Selection.swift:307）。");
        }

        if (current < 0)
        {
            current = 0;
        }

        double softened = Math.Sqrt((current * current) + ((double)amount * amount));
        return Math.Min(MaxFeather, softened);
    }

    /// <summary>
    /// 计算羽化后覆盖度所需的包围盒：原包围盒向四周各扩 <c>ceil(feather * 2)</c> 像素。
    /// </summary>
    /// <param name="pathBounds">轮廓本身的包围盒。</param>
    /// <param name="feather">羽化量，0–<see cref="MaxFeather"/>。</param>
    /// <returns>羽化覆盖度的包围盒。</returns>
    /// <remarks>
    /// 逐字照抄 Mac 版 <c>coverageBounds</c>（<c>Document/Selection.swift:34-36</c>）：
    /// <code>
    /// path.boundingBoxOfPath.insetBy(dx: -ceil(feather * 2), dy: -ceil(feather * 2))
    /// </code>
    /// 源码注释解释了 2 倍系数：「四个高斯标准差保留了轮廓之外可见的衰减」——
    /// 因为 <c>sigma = feather / 2</c>，所以 4σ = 4 × (feather/2) = 2 × feather。
    /// <para>
    /// 🔴 <b>漏掉这一步会导致羽化在边缘被截断</b>：只在原包围盒内做模糊，
    /// 会把本该扩散到包围盒外的渐变直接切掉，羽化看起来"只往里软"。
    /// </para>
    /// </remarks>
    public static DocRect CoverageBounds(DocRect pathBounds, double feather)
    {
        if (pathBounds.IsEmpty || feather <= 0)
        {
            return pathBounds;
        }

        double d = Math.Ceiling(feather * 2.0);
        return pathBounds.Inflate(d);
    }

    /// <summary>
    /// 对覆盖度平面施加羽化：可分离高斯卷积，<c>sigma = feather / 2</c>。
    /// </summary>
    /// <param name="plane">待羽化的覆盖度平面。</param>
    /// <param name="feather">羽化量，0–<see cref="MaxFeather"/>；0 表示硬边，直接原样返回。</param>
    /// <returns>羽化后的覆盖度平面，包围盒已按 <see cref="CoverageBounds"/> 扩张。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plane"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="feather"/> 为负数或超过 <see cref="MaxFeather"/>。</exception>
    /// <remarks>
    /// 🔴 <b>sigma 系数必须除以 2</b>，照抄 <c>Selection.swift:27</c> 的
    /// <c>applyingGaussianBlur(sigma: feather / 2)</c>。若照搬成 <c>sigma = feather</c>，
    /// 羽化宽度会宽一倍，且羽化量与实际观感脱钩。
    /// <para>
    /// 边界处理：本实现<b>不做边缘钳位</b>，扩张区外视为 0。
    /// 这与 Mac 版一致 —— <c>Selection.swift:27</c> 用 <c>clampedToExtent()</c> 先把图像延展，
    /// 再 <c>cropped(to: extent)</c> 裁回原画布；画布之外本来就是未选中（0），
    /// 所以等价于"以 0 为边界值"而不是"以边缘像素为边界值"。
    /// </para>
    /// <para>
    /// <b>性能</b>：可分离卷积的开销是 <c>O(W × H × radius)</c>，
    /// <c>radius = ceil(3σ) = ceil(1.5 × feather)</c>。
    /// feather=6（Mac 测试用值）时 radius=9，开销可忽略；
    /// feather=250 时 radius=375，在 4K 覆盖度上会明显变慢。
    /// 羽化是用户显式触发的一次性编辑操作，不在逐帧路径上，故此处不引入近似快速核 ——
    /// 近似核会直接改变羽化数值，属于"为了让测试变快而改语义"。
    /// </para>
    /// </remarks>
    public static CoveragePlane Apply(CoveragePlane plane, double feather)
    {
        ArgumentNullException.ThrowIfNull(plane);

        if (feather < 0 || feather > MaxFeather)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feather), feather, $"必须落在 [0, {MaxFeather}] 内（照抄 Selection.swift:307/330）。");
        }

        if (feather <= 0)
        {
            // 硬边快路径：不做任何卷积，也不扩张包围盒。
            return plane;
        }

        DocRect bounds = CoverageBounds(plane.Bounds, feather);
        if (bounds.IsEmpty)
        {
            return plane;
        }

        int w = bounds.Size.Width;
        int h = bounds.Size.Height;
        double sigma = feather / 2.0;

        var src = new double[w * h];
        int offX = (int)(plane.Bounds.Left - bounds.Left);
        int offY = (int)(plane.Bounds.Top - bounds.Top);
        ReadOnlySpan<byte> original = plane.Pixels;

        // 扩张区外保持 0（未选中），这正是 Mac 版 "画布之外为未选中" 的等价语义。
        for (int y = 0; y < h; y++)
        {
            int sy = y - offY;
            if ((uint)sy >= (uint)plane.Height)
            {
                continue;
            }

            int rowBase = y * w;
            int srcRowBase = sy * plane.Width;
            for (int x = 0; x < w; x++)
            {
                int sx = x - offX;
                if ((uint)sx < (uint)plane.Width)
                {
                    src[rowBase + x] = original[srcRowBase + sx];
                }
            }
        }

        var kernel = BuildKernel(sigma);
        var tmp = new double[w * h];

        // 横向卷积
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            Convolve1D(src, tmp, rowBase, w, kernel);
        }

        // 纵向卷积（就地写回 src）
        var dst = new byte[w * h];
        for (int x = 0; x < w; x++)
        {
            ConvolveColumns(tmp, src, x, w, h, kernel);

            for (int y = 0; y < h; y++)
            {
                double v = src[(y * w) + x];
                int q = (int)Math.Round(v, MidpointRounding.AwayFromZero);
                if (q <= 0)
                {
                    continue;
                }

                dst[(y * w) + x] = (byte)(q > 255 ? 255 : q);
            }
        }

        return new CoveragePlane(bounds, dst);
    }

    /// <summary>构建归一化的一维高斯核，<c>sigma</c> 为标准差。</summary>
    /// <param name="sigma">标准差，恒大于 0。</param>
    /// <returns>核系数数组，长度为 <c>2×radius + 1</c>，总和为 1。</returns>
    private static double[] BuildKernel(double sigma)
    {
        int radius = (int)Math.Ceiling(TruncationSigma * sigma);
        if (radius < 1)
        {
            radius = 1;
        }

        var kernel = new double[(radius * 2) + 1];
        double twoSigmaSq = 2.0 * sigma * sigma;
        double sum = 0;

        for (int i = -radius; i <= radius; i++)
        {
            double v = Math.Exp(-((double)(i * i)) / twoSigmaSq);
            kernel[i + radius] = v;
            sum += v;
        }

        // 归一化：保证常量区域卷积后仍是同一个常量，
        // 否则羽化会把"选区内部的 255"整体压暗。
        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    /// <summary>对一行做一维卷积，边界按 0 处理。</summary>
    /// <param name="src">源缓冲。</param>
    /// <param name="dst">目标缓冲。</param>
    /// <param name="rowBase">当前行在源/目标缓冲中的起始下标。</param>
    /// <param name="width">行宽。</param>
    /// <param name="kernel">卷积核，长度为奇数。</param>
    private static void Convolve1D(double[] src, double[] dst, int rowBase, int width, double[] kernel)
    {
        int radius = kernel.Length / 2;

        for (int x = 0; x < width; x++)
        {
            double acc = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int sx = x + k;
                if ((uint)sx >= (uint)width)
                {
                    continue;
                }

                acc += src[rowBase + sx] * kernel[k + radius];
            }

            dst[rowBase + x] = acc;
        }
    }

    /// <summary>对一列做一维卷积，边界按 0 处理。</summary>
    /// <param name="tmp">横向卷积结果。</param>
    /// <param name="dst">目标缓冲（原地写回与 <paramref name="tmp"/> 同尺寸）。</param>
    /// <param name="x">列号。</param>
    /// <param name="width">行宽。</param>
    /// <param name="height">行高。</param>
    /// <param name="kernel">卷积核，长度为奇数。</param>
    private static void ConvolveColumns(double[] tmp, double[] dst, int x, int width, int height, double[] kernel)
    {
        int radius = kernel.Length / 2;

        for (int y = 0; y < height; y++)
        {
            double acc = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int sy = y + k;
                if ((uint)sy >= (uint)height)
                {
                    continue;
                }

                acc += tmp[(sy * width) + x] * kernel[k + radius];
            }

            dst[(y * width) + x] = acc;
        }
    }
}