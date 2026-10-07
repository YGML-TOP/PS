namespace Compositor.Core.Pixels;

/// <summary>
/// 污点修复（Spot Healing）：把 coverage 标记的洞用附近纹理填掉，并把接缝处色调差平滑铺开。
/// 直译自 <c>Rendering/HealPixels.c</c>（259 行，逐行对照）。
/// </summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>heal_coverage_bounds</c> / <c>heal_hash</c> / <c>heal_unit</c> /
/// <c>heal_score</c> / <c>heal_solve</c> / <c>spot_heal</c>。
/// 其中 <c>heal_hash</c>、<c>heal_unit</c>、<c>heal_score</c>、<c>heal_solve</c> 在原 C 是
/// <c>static</c>，这里保持 <c>private</c>。</para>
///
/// <para><b>不变量</b>：<paramref name="rgba"/> 为预乘 RGBA8（4 字节/像素），
/// <paramref name="stride"/> 为每行字节数且 ≥ width*4；<paramref name="coverage"/> 是
/// <c>width * height</c> 紧密排列的灰度（0 表示不修）；工作盒（spot 加 ring，并裁剪到图像内）
/// 的宽高 ≤ width/height，故所有内部缓冲都在画布范围内。</para>
///
/// <para><b>确定性</b>：随机量来自 <c>seed</c> 与像素坐标的哈希（<c>heal_hash</c>），
/// 不含时间/全局状态，同输入同 seed 必得同结果。</para>
///
/// <para><b>与原 C 的等价改写</b>（均不改变数值结果）：
/// <list type="bullet">
/// <item><c>calloc</c>/<c>malloc</c> → 定长 <c>new T[n]</c>；两者都是 0 填充（C# 的
/// <c>new</c> 对数值类型同样 0 填充），分配失败分支（原 C 返回 -1）不存在，
/// C# 直接抛 <see cref="OutOfMemoryException"/>。</item>
/// <item><c>long bounds[4]</c> 出参 → <see cref="Span{T}"/>；
/// <c>double out[4]</c> / <c>double mean[4]</c> / <c>float knownSum[4]</c> / <c>float sum[4]</c> /
/// <c>long neighbors[4][2]</c> / <c>long offsets[4][2]</c> → 方法外层 <c>stackalloc</c>，
/// 原 C 在每轮循环重新初始化为 0 的地方，C# 在同一位置显式清零，语义相同。</item>
/// <item>两处 <c>goto done</c>（<c>!ringCount</c> 与分配失败）改为提前 <c>return</c>。
/// <c>!ringCount</c> 那一处退出时 <paramref name="rgba"/> 一个字节都还没被写过，
/// 与原 C「置 status=0 → 释放 → return 0」完全等价。</item>
/// <item>C 的 <c>(uint8_t)lround(v)</c> 一律走 <see cref="CSemantics.U8(double)"/> +
/// <see cref="CSemantics.LRound(double)"/>：此处表达式已被原 C 的 <c>0/255/t[3]</c> 夹在
/// [0,255]，饱和分支永不命中。</item>
/// <item>C 的 <c>INFINITY</c> → <see cref="double.PositiveInfinity"/>；
/// <c>isfinite</c> → 私有 <c>IsFinite</c>（同时排除 NaN，与 C 一致）。</item>
/// <item><c>M_PI</c> → <see cref="Math.PI"/>：C 的 <c>M_PI</c> 宏就是双精度 π 字面量，
/// 两者同一个值。<c>1.05f</c>/<c>0x7feb352dU</c> 等字面量一律原样保留，未做任何"化简"。</item>
/// <item><c>double → float</c> 的隐式窄化（C 允许、C# 需要显式）补了 <c>(float)</c> 强转，
/// 取值与 C 的隐式转换相同。</item>
/// <item><c>size_t</c> 索引改 <c>int</c>；C 中 <c>long</c> 坐标原样保留为 <c>long</c>，
/// 在下标处显式 <c>(int)</c>（工作盒 ≤ 画布尺寸，不会溢出）。</item>
/// </list></para>
///
/// <para><b>已知原 C 的可疑点（未修改）</b>：
/// <c>heal_score</c> 只在 <c>|dx| &lt; ww &amp;&amp; |dy| &lt; wh</c> 时判为无穷，
/// 即"两个方向都重叠"才拒绝，只有一个方向重叠仍会被采用（此时两片区域不重叠，不会自写自读）；
/// <c>detail[c]</c> 在 <c>haveSource</c> 为真时恒为 0，<c>grain</c> 也恒为 0，二者相乘无影响。</para>
/// </remarks>
public static class HealPixels
{
    /// <summary>C 的 <c>enum { OUTSIDE = 0, RING = 1, HOLE = 2 }</c>，值必须保持一致。</summary>
    private const byte Outside = 0, Ring = 1, Hole = 2;

    /// <summary>
    /// 求灰度图非零区域的半开包围盒。直译自 <c>heal_coverage_bounds</c>。
    /// </summary>
    /// <param name="gray">灰度字节，只读；非 0 字节视为覆盖。</param>
    /// <param name="width">灰度图宽度（像素）。</param>
    /// <param name="height">灰度图高度（像素）。</param>
    /// <param name="stride">灰度图每行字节数。</param>
    /// <param name="bounds">
    /// 输出 4 个值：<c>[0]</c>=x0、<c>[1]</c>=y0、<c>[2]</c>=x1、<c>[3]</c>=y1（右/下为开区间）。
    /// 整张图全 0 时四项全部归 0（与原 C 的 <c>x0 = y0 = x1 = y1 = 0</c> 一致，
    /// 即这种情况下 <c>x1</c> 也是 0 而非 width）。
    /// </param>
    public static void CoverageBounds(ReadOnlySpan<byte> gray, int width, int height, int stride, Span<long> bounds)
    {
        long x0 = width, y0 = height, x1 = 0, y1 = 0;

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;

            for (int x = 0; x < width; ++x)
            {
                if (gray[row + x] == 0) continue;
                if (x < x0) x0 = x;
                if (x + 1 > x1) x1 = x + 1;
                if (y < y0) y0 = y;
                if (y + 1 > y1) y1 = y + 1;
            }
        }

        if (x1 <= x0 || y1 <= y0) x0 = y0 = x1 = y1 = 0;
        bounds[0] = x0;
        bounds[1] = y0;
        bounds[2] = x1;
        bounds[3] = y1;
    }

    /// <summary>
    /// 低位混合哈希（原 C 的 <c>heal_hash</c>）。乘法按模 2³² 回绕，故整体 <c>unchecked</c>。
    /// </summary>
    private static uint HealHash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
        }

        return x;
    }

    /// <summary>
    /// 把 32 位键映射到 0–1（左闭右开）区间的均匀数（原 C 的 <c>heal_unit</c>）。
    /// </summary>
    private static double HealUnit(uint key) => (double)(HealHash(key) >> 8) / 16777216.0;

    /// <summary>C 的 <c>isfinite</c>：NaN 与 ±Inf 都要排除。</summary>
    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>
    /// 空洞外圈与候选补丁外圈的均方色差（原 C 的 <c>heal_score</c>）。
    /// 不变式：补丁与空洞重叠、或越出图像时返回 <see cref="double.PositiveInfinity"/>；
    /// 外圈一个 RING 像素都没有时也返回无穷（因此调用方只会采纳真正可比的分值）。
    /// </summary>
    private static double HealScore(
        ReadOnlySpan<byte> rgba, int stride, ReadOnlySpan<byte> role,
        long wx0, long wy0, long ww, long wh, long dx, long dy, long W, long H)
    {
        if (Math.Abs(dx) < ww && Math.Abs(dy) < wh) return double.PositiveInfinity;
        if (wx0 + dx < 0 || wy0 + dy < 0 || wx0 + ww + dx > W || wy0 + wh + dy > H) return double.PositiveInfinity;

        double sum = 0;
        long n = 0;

        for (long y = 0; y < wh; ++y)
        {
            for (long x = 0; x < ww; ++x)
            {
                if (role[(int)(y * ww + x)] != Ring) continue;

                int t = (int)((wy0 + y) * stride + (wx0 + x) * 4);
                int s = (int)((wy0 + y + dy) * stride + (wx0 + x + dx) * 4);

                for (int c = 0; c < 4; ++c)
                {
                    double d = (double)rgba[t + c] - rgba[s + c];
                    sum += d * d;
                }

                ++n;
            }
        }

        return n != 0 ? sum / n : double.PositiveInfinity;
    }

    /// <summary>
    /// 在固定住 RING 像素的前提下，求 HOLE 像素上的平滑值（原 C 的 <c>heal_solve</c>）。
    /// </summary>
    /// <remarks>
    /// 不变式：调用前后 RING 与 OUTSIDE 位置的 <paramref name="value"/> 必须保持不变。
    /// w/h 都 &gt; 32 且 depth &lt; 16 时先在半分辨率上递归求解一遍做初值，此时迭代次数由
    /// 300 降到 40（大洞因此只需很少轮就收敛）。松弛系数固定 1.8。
    /// </remarks>
    private static void HealSolve(Span<float> value, ReadOnlySpan<byte> role, long w, long h, int depth)
    {
        int iterations = 300;

        // 原 C 的 float knownSum[4] / holeSum[4] / sum[4] 与 long neighbors[4][2]：
        // C 里这些数组在每次进入循环体时重新初始化为 0，C# 没有可整体重置的数组，
        // 故在所有循环之外 stackalloc（长度固定），并在原 C 初始化数组的同一位置显式清零。
        Span<float> knownSum = stackalloc float[4];
        Span<float> holeSum = stackalloc float[4];
        Span<float> sum = stackalloc float[4];
        Span<long> neighbors = stackalloc long[8];

        if (w > 32 && h > 32 && depth < 16)
        {
            long cw = (w + 1) / 2, ch = (h + 1) / 2;
            float[] coarse = new float[(int)(cw * ch * 4)];
            byte[] coarseRole = new byte[(int)(cw * ch)];

            // 原 C 的 `if (coarse && coarseRole)`：C# 中数组分配失败即抛 OutOfMemoryException，
            // 不存在"分配失败仍继续求解"的分支，故直接展开；随之 free(coarse)/free(coarseRole) 也消失。

            for (long y = 0; y < ch; ++y)
            {
                for (long x = 0; x < cw; ++x)
                {
                    int known = 0, hole = 0;
                    knownSum[0] = 0; knownSum[1] = 0; knownSum[2] = 0; knownSum[3] = 0;
                    holeSum[0] = 0; holeSum[1] = 0; holeSum[2] = 0; holeSum[3] = 0;

                    for (long j = 0; j < 2; ++j)
                    {
                        for (long i = 0; i < 2; ++i)
                        {
                            long fx = x * 2 + i, fy = y * 2 + j;
                            if (fx >= w || fy >= h) continue;
                            long p = fy * w + fx;
                            if (role[(int)p] == Ring) { ++known; for (int c = 0; c < 4; ++c) knownSum[c] += value[(int)(p * 4 + c)]; }
                            else if (role[(int)p] == Hole) { ++hole; for (int c = 0; c < 4; ++c) holeSum[c] += value[(int)(p * 4 + c)]; }
                        }
                    }

                    long q = y * cw + x;

                    if (known != 0)
                    {
                        coarseRole[(int)q] = Ring;
                        for (int c = 0; c < 4; ++c) coarse[(int)(q * 4 + c)] = knownSum[c] / known;
                    }
                    else if (hole != 0)
                    {
                        coarseRole[(int)q] = Hole;
                        for (int c = 0; c < 4; ++c) coarse[(int)(q * 4 + c)] = holeSum[c] / hole;
                    }
                }
            }

            HealSolve(coarse, coarseRole, cw, ch, depth + 1);

            for (long y = 0; y < h; ++y)
            {
                for (long x = 0; x < w; ++x)
                {
                    long p = y * w + x, q = (y / 2) * cw + x / 2;
                    if (role[(int)p] == Hole && coarseRole[(int)q] == Hole)
                    {
                        for (int c = 0; c < 4; ++c) value[(int)(p * 4 + c)] = coarse[(int)(q * 4 + c)];
                    }
                }
            }

            iterations = 40;
        }

        const float omega = 1.8f;

        for (int it = 0; it < iterations; ++it)
        {
            for (long y = 0; y < h; ++y)
            {
                for (long x = 0; x < w; ++x)
                {
                    long p = y * w + x;
                    if (role[(int)p] != Hole) continue;

                    sum[0] = 0; sum[1] = 0; sum[2] = 0; sum[3] = 0;
                    int n = 0;

                    // 原 C 的 long neighbors[4][2] = {{x-1,y},{x+1,y},{x,y-1},{x,y+1}}
                    neighbors[0] = x - 1; neighbors[1] = y;
                    neighbors[2] = x + 1; neighbors[3] = y;
                    neighbors[4] = x; neighbors[5] = y - 1;
                    neighbors[6] = x; neighbors[7] = y + 1;

                    for (int k = 0; k < 4; ++k)
                    {
                        long nx = neighbors[k * 2], ny = neighbors[k * 2 + 1];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        long q = ny * w + nx;
                        if (role[(int)q] == Outside) continue;
                        for (int c = 0; c < 4; ++c) sum[c] += value[(int)(q * 4 + c)];
                        ++n;
                    }

                    if (n == 0) continue;

                    for (int c = 0; c < 4; ++c)
                    {
                        int vp = (int)(p * 4 + c);
                        value[vp] += omega * (sum[c] / n - value[vp]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 污点修复，原地修改预乘 RGBA。直译自 <c>int spot_heal(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：只写 <c>role == HOLE</c> 的那些像素（即 coverage 非 0 处），
    /// 其余像素（OUTSIDE / RING / coverage 为 0 的洞边）逐字节保持不变；
    /// alpha 先写、RGB 后写，RGB 的上限用的是写好的 alpha。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8，原地修改。</param>
    /// <param name="coverage">
    /// 覆盖蒙版，<c>width * height</c> 字节，非 0 表示要修。原 C 传入的 stride 就是 width。
    /// </param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">rgba 每行字节数，须 ≥ <paramref name="width"/> * 4。</param>
    /// <param name="opacity">修复不透明度，0–1，最终结果按 coverage/255 × opacity 混合。</param>
    /// <param name="mode">0 = Content-Aware，1 = Create Texture，2 = Proximity Match。</param>
    /// <param name="seed">噪声种子（Create Texture 模式的颗粒来源）。</param>
    /// <returns>0 = 完成；原 C 的 -1（内存不足）在 C# 中不存在，改为抛 <see cref="OutOfMemoryException"/>。</returns>
    public static int SpotHeal(
        Span<byte> rgba, ReadOnlySpan<byte> coverage, int width, int height, int stride,
        float opacity, int mode, uint seed)
    {
        long W = width, H = height;
        Span<long> bounds = stackalloc long[4];
        CoverageBounds(coverage, width, height, width, bounds);
        if (bounds[2] <= bounds[0]) return 0;

        long bw = bounds[2] - bounds[0], bh = bounds[3] - bounds[1], size = bw > bh ? bw : bh;
        long ring = size / 8;
        if (ring < 2) ring = 2;
        if (ring > 16) ring = 16;

        // Work box: the spot plus its ring, clipped to the image.
        long wx0 = bounds[0] - ring < 0 ? 0 : bounds[0] - ring, wy0 = bounds[1] - ring < 0 ? 0 : bounds[1] - ring;
        long wx1 = bounds[2] + ring > W ? W : bounds[2] + ring, wy1 = bounds[3] + ring > H ? H : bounds[3] + ring;
        long ww = wx1 - wx0, wh = wy1 - wy0, wn = ww * wh;

        // 原 C 的四个 calloc/malloc（含 `if (!role || ...) goto done;` 的 -1 返回）。
        byte[] role = new byte[(int)wn];
        byte[] near = new byte[(int)wn];
        long[] prefix = new long[(int)((ww > wh ? ww : wh) + 1)];
        float[] value = new float[(int)(wn * 4)];

        // 原 C 的 double out[4] / mean[4] / detail[3] 与 long offsets[4][2]。
        // 注意 mean/detail/out 在原 C 是 double，不是 float：累加精度必须保持 double。
        Span<double> mean = stackalloc double[4];
        Span<double> detail = stackalloc double[3];
        Span<double> outCh = stackalloc double[4];
        Span<long> offsets = stackalloc long[8];

        for (long y = 0; y < wh; ++y)
        {
            for (long x = 0; x < ww; ++x)
                role[(int)(y * ww + x)] = coverage[(int)((wy0 + y) * width + (wx0 + x))] != 0 ? Hole : Outside;
        }

        // The ring: pixels within `ring` of the spot (a square dilation, row pass then column pass).
        for (long y = 0; y < wh; ++y)
        {
            prefix[0] = 0;
            for (long x = 0; x < ww; ++x) prefix[(int)x + 1] = prefix[(int)x] + (role[(int)(y * ww + x)] == Hole);
            for (long x = 0; x < ww; ++x)
            {
                long lo = x - ring < 0 ? 0 : x - ring, hi = x + ring + 1 > ww ? ww : x + ring + 1;
                near[(int)(y * ww + x)] = prefix[(int)hi] - prefix[(int)lo] > 0 ? (byte)1 : (byte)0;
            }
        }

        for (long x = 0; x < ww; ++x)
        {
            prefix[0] = 0;
            for (long y = 0; y < wh; ++y) prefix[(int)y + 1] = prefix[(int)y] + near[(int)(y * ww + x)];
            for (long y = 0; y < wh; ++y)
            {
                long lo = y - ring < 0 ? 0 : y - ring, hi = y + ring + 1 > wh ? wh : y + ring + 1;
                if (role[(int)(y * ww + x)] == Outside && prefix[(int)hi] - prefix[(int)lo] > 0)
                    role[(int)(y * ww + x)] = Ring;
            }
        }

        long ringCount = 0;
        for (long p = 0; p < wn; ++p) ringCount += role[(int)p] == Ring;
        if (ringCount == 0) return 0; // 原 C: status = 0; goto done; —— 此时 rgba 一个字节都未被写过。

        // Source patch for Content-Aware and Proximity Match.
        long ox = 0, oy = 0;
        int haveSource = 0;

        if (mode != 1)
        {
            double[] factors = { 1.05, 1.35, 1.75, 2.25, 2.8 };
            int count = mode == 2 ? 2 : 5;
            double best = double.PositiveInfinity;

            for (int f = 0; f < count; ++f)
            {
                for (int a = 0; a < 24; ++a)
                {
                    double angle = a * Math.PI / 12.0;
                    long dx = CSemantics.LRound(Math.Cos(angle) * factors[f] * ww);
                    long dy = CSemantics.LRound(Math.Sin(angle) * factors[f] * wh);
                    double score = HealScore(rgba, stride, role, wx0, wy0, ww, wh, dx, dy, W, H);
                    if (!IsFinite(score)) continue;
                    score *= mode == 2 ? 1.0 + 0.6 * f : 1.0 + 0.1 * f; // nearer patches win ties
                    if (score < best) { best = score; ox = dx; oy = dy; }
                }
            }

            if (IsFinite(best))
            {
                // Fine-tune the alignment so repeating texture lines up.
                long cx = ox, cy = oy;
                double refined = HealScore(rgba, stride, role, wx0, wy0, ww, wh, cx, cy, W, H);

                for (long j = -3; j <= 3; ++j)
                {
                    for (long i = -3; i <= 3; ++i)
                    {
                        double score = HealScore(rgba, stride, role, wx0, wy0, ww, wh, cx + i, cy + j, W, H);
                        if (score < refined) { refined = score; ox = cx + i; oy = cy + j; }
                    }
                }

                haveSource = 1;
            }
        }

        // Membrane: the edge difference between the original and the patch (or the original itself
        // for a smooth fill), spread across the spot.
        for (long y = 0; y < wh; ++y)
        {
            for (long x = 0; x < ww; ++x)
            {
                long p = y * ww + x;
                if (role[(int)p] != Ring)
                {
                    for (int c = 0; c < 4; ++c) value[(int)(p * 4 + c)] = 0; // 原 C: memset(value + p*4, 0, ...)
                    continue;
                }

                long ix = wx0 + x, iy = wy0 + y;
                int to = (int)(iy * stride + ix * 4);
                int so = haveSource != 0 ? (int)((iy + oy) * stride + (ix + ox) * 4) : -1;

                for (int c = 0; c < 4; ++c)
                {
                    float v = (float)rgba[to + c] - (so >= 0 ? rgba[so + c] : 0);
                    value[(int)(p * 4 + c)] = v;
                    mean[c] += v;
                }

                if (haveSource == 0)
                {
                    // Fine detail around the spot: each pixel against the average of its neighbours.
                    for (int c = 0; c < 3; ++c)
                    {
                        double around = 0;
                        int n = 0;

                        // 原 C 的 long offsets[4][2] = {{ix-1,iy},{ix+1,iy},{ix,iy-1},{ix,iy+1}}
                        offsets[0] = ix - 1; offsets[1] = iy;
                        offsets[2] = ix + 1; offsets[3] = iy;
                        offsets[4] = ix; offsets[5] = iy - 1;
                        offsets[6] = ix; offsets[7] = iy + 1;

                        for (int k = 0; k < 4; ++k)
                        {
                            long ox2 = offsets[k * 2], oy2 = offsets[k * 2 + 1];
                            if (ox2 < 0 || oy2 < 0 || ox2 >= W || oy2 >= H) continue;
                            around += rgba[(int)(oy2 * stride + ox2 * 4 + c)];
                            ++n;
                        }

                        if (n != 0) { double d = rgba[to + c] - around / n; detail[c] += d * d; }
                    }
                }
            }
        }

        for (int c = 0; c < 4; ++c) mean[c] /= ringCount;

        for (long p = 0; p < wn; ++p)
        {
            if (role[(int)p] != Hole) continue;
            for (int c = 0; c < 4; ++c) value[(int)(p * 4 + c)] = (float)mean[c];
        }

        HealSolve(value, role, ww, wh, 0);

        // 原 C: detail[c] = sqrt(detail[c] / ringCount) * 0.9;  —— detail 是 double，全程保持 double。
        for (int c = 0; c < 3; ++c) detail[c] = Math.Sqrt(detail[c] / ringCount) * 0.9;

        for (long y = 0; y < wh; ++y)
        {
            for (long x = 0; x < ww; ++x)
            {
                long p = y * ww + x;
                if (role[(int)p] != Hole) continue;

                long ix = wx0 + x, iy = wy0 + y;
                int to = (int)(iy * stride + ix * 4);
                int so = haveSource != 0 ? (int)((iy + oy) * stride + (ix + ox) * 4) : -1;
                double amount = coverage[(int)(iy * width + ix)] / 255.0 * opacity;
                double grain = 0;

                if (haveSource == 0)
                {
                    uint key = HealHash(seed ^ HealHash(unchecked((uint)(iy * W + ix))));
                    double u1 = HealUnit(key), u2 = HealUnit(key ^ 0x68e31da4U);
                    grain = Math.Sqrt(-2.0 * Math.Log(1.0 - u1)) * Math.Cos(2.0 * Math.PI * u2);
                }

                for (int c = 0; c < 4; ++c)
                {
                    double healed = (so >= 0 ? rgba[so + c] : 0) + value[(int)(p * 4 + c)] + (c < 3 ? grain * detail[c] : 0);
                    outCh[c] = rgba[to + c] + (healed - rgba[to + c]) * amount;
                }

                double alpha = outCh[3] < 0 ? 0 : outCh[3] > 255 ? 255 : outCh[3];
                rgba[to + 3] = CSemantics.U8(CSemantics.LRound(alpha));

                for (int c = 0; c < 3; ++c)
                {
                    double v = outCh[c] < 0 ? 0 : outCh[c] > rgba[to + 3] ? rgba[to + 3] : outCh[c];
                    rgba[to + c] = CSemantics.U8(CSemantics.LRound(v));
                }
            }
        }

        return 0;
    }
}