namespace Compositor.Core.Pixels;

/// <summary>
/// 内容识别填充（Content-Aware Fill）：用 PatchMatch 思路从图中已知区域"借"像素填入选区。
/// 直译自 <c>Rendering/ContentFill.c</c>（93 行，逐行对照）。
/// </summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>int content_fill(uint8_t *pixels, size_t stride, const uint8_t *mask,
/// size_t ms, int w, int h)</c>，返回 0 表示没找到可用参照、1 表示填充完成。</para>
///
/// <para><b>语义</b>：选中像素（mask≠0）被填充；<b>未选中且不透明</b>的像素是"已知区"，
/// 既参与匹配也作为拷贝源；<b>未选中且透明</b>的像素两者都不是，保持原样不动。</para>
///
/// <para><b>算法</b>：广度优先从选区边界向内推进，每步用「四邻域的来源偏移传播（4 次）」
/// 加「随机采样（24 次）」先粗定位，再用半径 64→32→16→8→4→2→1 的收缩随机搜索精修，
/// 取平方色差均值最小者整像素拷贝。全部推进完后还有一轮兜底：处理"只挨着透明区"的孤立选区。</para>
///
/// <para><b>确定性</b>：内部使用固定初值的线性同余发生器（seed=0x6d2b79f5），
/// 同一输入逐次运行结果完全一致——这是"同一张图反复填充得到同一结果"的前提，不得改为随机种子。</para>
///
/// <para><b>与原 C 的等价改写</b>：<c>calloc</c>/<c>malloc</c> 改为定长数组；
/// 两处 <c>goto done</c> 改为等价的 <c>done</c> 标志分支；<c>memcpy</c> 改为逐字节拷贝。
/// 循环次序、随机数消耗顺序、匹配函数、边界判定逐行一致。
/// 分配失败分支（原 C 返回 -1）在 C# 中不存在——改为数组分配，失败即抛 <see cref="OutOfMemoryException"/>。</para>
/// </remarks>
public static class ContentFill
{
    /// <summary>执行内容识别填充，原地修改 <paramref name="pixels"/>。</summary>
    /// <param name="pixels">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="stride">每行字节数，须 ≥ <paramref name="w"/> * 4。</param>
    /// <param name="mask">选区掩码，非 0 表示要填充。只读。</param>
    /// <param name="ms">掩码每行字节数。</param>
    /// <param name="w">像素宽度。</param>
    /// <param name="h">像素高度。</param>
    /// <returns>1 = 找到参照并完成填充；0 = 没有可用参照，像素未改动。</returns>
    public static int Fill(Span<byte> pixels, int stride, ReadOnlySpan<byte> mask, int ms, int w, int h)
    {
        int n = w * h;

        var known = new byte[n];
        var target = new byte[n];
        var valid = new byte[n];
        var queued = new byte[n];
        var donors = new int[n];
        var queue = new int[n];
        var chosen = new int[n];

        int radius = (w >= 5 && h >= 5) ? 2 : 0;
        int missing = 0, donorCount = 0, head = 0, tail = 0, scan = 0;

        // Selected pixels are filled. Unselected opaque pixels are the image to match and copy from; unselected
        // transparent ones are neither — nothing to match against, and left as they are.
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                int p = y * w + x;
                target[p] = mask[y * ms + x] != 0 ? (byte)1 : (byte)0;
                known[p] = target[p] == 0 && pixels[y * stride + x * 4 + 3] == 255 ? (byte)1 : (byte)0;
                chosen[p] = -1;
                if (target[p] != 0) ++missing;
            }
        }

        bool done = missing == 0;
        if (done) donorCount = 1; // 对应原 C 的 `if(!missing) { donorCount=1; goto done; }`

        if (!done)
        {
            // 只有周围 radius 邻域内全部已知的像素才能当参照源，避免从边缘取样导致接缝。
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    int p = y * w + x;
                    if (known[p] == 0) continue;
                    int ok = 1;
                    for (int dy = -radius; dy <= radius && ok != 0; ++dy)
                    {
                        for (int dx = -radius; dx <= radius; ++dx)
                        {
                            int sx = x + dx, sy = y + dy;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h || known[sy * w + sx] == 0) { ok = 0; break; }
                        }
                    }
                    if (ok != 0) { valid[p] = 1; donors[donorCount++] = p; }
                }
            }

            done = donorCount == 0; // 对应原 C 的 `if(!donorCount) goto done;`
        }

        if (!done)
        {
            // 选区中紧贴已知区的像素先入队。
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    int p = y * w + x;
                    if (target[p] != 0 && ((x != 0 && known[p - 1] != 0) || (x + 1 < w && known[p + 1] != 0) ||
                                           (y != 0 && known[p - w] != 0) || (y + 1 < h && known[p + w] != 0)))
                    {
                        queue[tail++] = p; queued[p] = 1;
                    }
                }
            }

            uint seed = 0x6d2b79f5u;
            for (; ; )
            {
                while (head < tail)
                {
                    int p = queue[head++], x = p % w, y = p / w, best = -1;
                    double score = double.MaxValue;
                    Span<int> neighbors = stackalloc int[4];
                    neighbors[0] = x != 0 ? p - 1 : -1;
                    neighbors[1] = x + 1 < w ? p + 1 : -1;
                    neighbors[2] = y != 0 ? p - w : -1;
                    neighbors[3] = y + 1 < h ? p + w : -1;

                    // Propagate coherent source offsets, then refine with randomized patch search.
                    for (int k = 0; k < 28; ++k)
                    {
                        int q = -1;
                        if (k < 4)
                        {
                            int t = neighbors[k];
                            if (t >= 0) q = (chosen[t] >= 0 ? chosen[t] : t) + (p - t);
                        }
                        else
                        {
                            q = donors[(int)(NextRandom(ref seed) % (uint)donorCount)];
                        }
                        if (q < 0 || q >= n || valid[q] == 0) continue;
                        double s = Match(pixels, stride, known, w, h, p, q, radius);
                        if (best < 0 || s < score) { score = s; best = q; }
                    }

                    if (best < 0) best = donors[0];

                    for (int r = 64; r >= 1; r /= 2)
                    {
                        int qx = best % w + (int)(NextRandom(ref seed) % (uint)(2 * r + 1)) - r;
                        int qy = best / w + (int)(NextRandom(ref seed) % (uint)(2 * r + 1)) - r;
                        if (qx < 0 || qy < 0 || qx >= w || qy >= h || valid[qy * w + qx] == 0) continue;
                        int q = qy * w + qx;
                        double s = Match(pixels, stride, known, w, h, p, q, radius);
                        if (s < score) { score = s; best = q; }
                    }

                    int dst = y * stride + x * 4;
                    int src = (best / w) * stride + (best % w) * 4;
                    pixels[dst] = pixels[src];
                    pixels[dst + 1] = pixels[src + 1];
                    pixels[dst + 2] = pixels[src + 2];
                    pixels[dst + 3] = pixels[src + 3];
                    known[p] = 1; chosen[p] = best;

                    for (int k = 0; k < 4; ++k)
                    {
                        int q = neighbors[k];
                        if (q >= 0 && target[q] != 0 && known[q] == 0 && queued[q] == 0)
                        {
                            queued[q] = 1; queue[tail++] = q;
                        }
                    }
                }

                // A selected area that only transparency touches starts from the best random donor, then spreads.
                while (scan < n && (target[scan] == 0 || known[scan] != 0)) ++scan;
                if (scan >= n) break;
                queue[tail++] = scan; queued[scan] = 1;
            }
        }

        return donorCount != 0 ? 1 : 0;
    }

    /// <summary>线性同余发生器（原 C 的 <c>next_random</c>）。</summary>
    private static uint NextRandom(ref uint state)
    {
        state = unchecked(state * 1664525u + 1013904223u);
        return state;
    }

    /// <summary>
    /// 邻域平方色差均值。原 C 的 <c>match</c>；邻域内没有任何已知像素时返回 <c>DBL_MAX</c>。
    /// </summary>
    private static double Match(
        ReadOnlySpan<byte> pixels, int stride, ReadOnlySpan<byte> known,
        int w, int h, int p, int q, int radius)
    {
        int px = p % w, py = p / w, qx = q % w, qy = q / w, count = 0;
        double sum = 0;

        for (int dy = -radius; dy <= radius; ++dy)
        {
            for (int dx = -radius; dx <= radius; ++dx)
            {
                int x = px + dx, y = py + dy, sx = qx + dx, sy = qy + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || sx < 0 || sy < 0 || sx >= w || sy >= h || known[y * w + x] == 0)
                    continue;

                int a = y * stride + x * 4, b = sy * stride + sx * 4;
                for (int c = 0; c < 4; ++c) { int d = pixels[a + c] - pixels[b + c]; sum += d * d; }
                ++count;
            }
        }

        return count != 0 ? sum / count : double.MaxValue;
    }
}