namespace Compositor.Core.Pixels;

/// <summary>
/// 魔棒选区、色彩范围选区与选区轮廓描边。
/// 直译自 <c>Rendering/WandPixels.c</c>（205 行，逐行对照）。
/// </summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>wand_matches</c> / <c>wand_mask</c> / <c>turn_right</c> / <c>turn_left</c> /
/// <c>wand_trace</c> / <c>color_near</c> / <c>color_range_mask</c>。
/// 其中前三个静态内联函数与 <c>color_near</c> 在原 C 是 <c>static</c>，这里保持 <c>private</c>。</para>
///
/// <para><b>不变量</b>：rgba 全部为预乘 RGBA8（4 字节/像素，<c>stride</c> 字节/行，行序自上而下）；
/// mask 为 <c>width * height</c> 字节，命中写 255、未命中写 0（<c>wand_mask</c> 只清/写前
/// <c>width * height</c> 字节，不碰调用方可能给的更长缓冲）；
/// <c>include</c>/<c>exclude</c> 为 3 字节一个颜色的直通 sRGB，调用方须保证长度 ≥ count*3。</para>
///
/// <para><b>与原 C 的等价改写</b>（均不改变数值结果）：
/// <list type="bullet">
/// <item><c>int32_t **points, size_t *pointCount, int32_t **loops, size_t *loopCount</c>
/// 四个出参 → <c>out int[] points, out int[] loops</c> 两个出参：<c>points</c> 装 2×顶点数，
/// <c>loops</c> 装闭环数，顶点数/闭环数由 <c>points.Length / 2</c> 与 <c>loops.Length</c> 得到
/// （原 C 里这两个数是 <c>malloc</c> 容量以下的实际写入量）。返回前把两个数组裁到实际长度
/// （只影响分配大小，不影响内容）。</item>
/// <item><c>malloc</c>/<c>realloc</c> → <c>new T[n]</c> / <c>Array.Resize</c>。C 的失败返回 -1
/// （内存不足 / 缓冲过大）在 C# 中不存在：分配失败抛 <see cref="OutOfMemoryException"/>。
/// 扩容策略逐字保留（原 C <c>realloc(pts, pointCapacity * 4 * sizeof(int32_t))</c> 后
/// <c>pointCapacity *= 2</c>，即实际是超量分配一倍），未"顺手修正"。</item>
/// <item><c>memset</c> → 显式逐字节写 0 / 255；<c>memmove</c> → <see cref="Array.Copy(Array,int,Array,int,int)"/>
/// （重叠区域行为与 memmove 一致）。</item>
/// <item><c>abs(int)</c> → <see cref="Math.Abs(int)"/>；<c>int reference[4]</c> / <c>int rgb[3]</c> →
/// 方法外层 <c>stackalloc</c>（长度固定，每轮显式赋值）。</item>
/// <item>C 的 <c>enum { EAST=1, SOUTH=2, WEST=4, NORTH=8 }</c> → <c>private const int</c>，
/// 值与顺序完全一致；<c>turn_right</c>/<c>turn_left</c> 的位移表达式原样保留。</item>
/// <item><c>wand_edge_limit</c>（<c>static const size_t</c>）→ <c>private const int</c>。</item>
/// </list></para>
///
/// <para><b>已知原 C 的可疑点（未修改）</b>：
/// <c>wand_mask</c> 的 <c>reference</c> 用的是 seed 邻域（含 seed 自身）的均值，
/// 而非仅未选中像素的均值；<c>wand_trace</c> 的 <c>lens[nl++]</c> 在 <c>np - first == 1</c>
/// 被回退一次时会记下 0 角闭环（退化闭环，原样保留）；<c>edges</c> 的 &gt; 8000000 检查
/// 在每行末尾做，故 <c>edges</c> 峰值 ≤ 限额 + 4×width，不会溢出。</para>
/// </remarks>
public static class WandPixels
{
    /// <summary>C 的 <c>enum { EAST = 1, SOUTH = 2, WEST = 4, NORTH = 8 }</c>，值必须保持一致。</summary>
    private const int East = 1, South = 2, West = 4, North = 8;

    /// <summary>C 的 <c>static const size_t wand_edge_limit</c>：边太多就拒绝描边。</summary>
    private const int WandEdgeLimit = 8000000;

    /// <summary>
    /// 四个通道（含 alpha）是否都在容差内（原 C 的 <c>wand_matches</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠ 原 C 在此处对 <c>tolerance</c> 取负：<c>tolerance == INT_MIN</c> 时 <c>-tolerance</c>
    /// 是未定义行为（x86 回绕）。C# 在 <c>CheckForOverflowUnderflow=false</c> 下同样回绕，语义一致，
    /// 故不额外防护。
    /// </remarks>
    private static bool WandMatches(ReadOnlySpan<byte> rgba, int at, ReadOnlySpan<int> reference, int tolerance)
    {
        for (int c = 0; c < 4; ++c)
        {
            int d = rgba[at + c] - reference[c];
            if (d < -tolerance || d > tolerance) return false;
        }

        return true;
    }

    /// <summary>
    /// 魔棒选区，原地写入掩码。直译自 <c>long wand_mask(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：<c>mask</c> 的前 <c>width * height</c> 字节被完整写入（255 / 0）后才返回，
    /// 包括 seed 越界的提前返回分支（那里是全 0）。
    /// 参考色 = seed 周围 (2×radius+1)² 方形（裁剪到图像）内全部像素的均值，按
    /// <c>(sum + samples/2) / samples</c> 取整，与选区无关。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8，只读。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">rgba 每行字节数。</param>
    /// <param name="seedX">种子 X（原 C 为 <c>size_t</c>，故不可为负）。</param>
    /// <param name="seedY">种子 Y（原 C 为 <c>size_t</c>，故不可为负）。</param>
    /// <param name="radius">参考色取样半径（原 C 为 <c>size_t</c>，故不可为负）。</param>
    /// <param name="tolerance">逐通道容差。</param>
    /// <param name="contiguous">非 0 = 从种子 4 邻域连通填充；0 = 全图逐像素比较。</param>
    /// <param name="mask">输出掩码，前 <c>width * height</c> 字节被写。</param>
    /// <returns>选中像素数；原 C 的 -1（栈分配失败）在 C# 中改为抛 <see cref="OutOfMemoryException"/>。</returns>
    public static long Mask(
        ReadOnlySpan<byte> rgba, int width, int height, int stride,
        int seedX, int seedY, int radius, int tolerance, int contiguous, Span<byte> mask)
    {
        if (width == 0 || height == 0) return 0;

        // 原 C: memset(mask, 0, width * height); 只清前 width*height 字节，不多清。
        mask.Slice(0, width * height).Clear();

        if (seedX >= width || seedY >= height) return 0;

        int x0 = seedX > radius ? seedX - radius : 0, x1 = seedX + radius < width ? seedX + radius : width - 1;
        int y0 = seedY > radius ? seedY - radius : 0, y1 = seedY + radius < height ? seedY + radius : height - 1;

        // 原 C 的 unsigned long sums[4] / samples 与 int reference[4]。
        Span<ulong> sums = stackalloc ulong[4];
        Span<int> reference = stackalloc int[4];
        ulong samples = 0;

        for (int y = y0; y <= y1; ++y)
        {
            for (int x = x0; x <= x1; ++x)
            {
                int at = y * stride + x * 4;
                for (int c = 0; c < 4; ++c) sums[c] += rgba[at + c];
                ++samples;
            }
        }

        for (int c = 0; c < 4; ++c) reference[c] = (int)((sums[c] + samples / 2) / samples);

        long count = 0;

        if (contiguous == 0)
        {
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride, outRow = y * width;

                for (int x = 0; x < width; ++x)
                {
                    if (WandMatches(rgba, row + x * 4, reference, tolerance)) { mask[outRow + x] = 255; ++count; }
                }
            }

            return count;
        }

        // Scanline flood fill: each popped seed fills its whole horizontal run, then pushes one
        // seed per matching run in the rows directly above and below it.
        int capacity = 4096, top = 1;
        int[] stack = new int[capacity * 2];
        stack[0] = seedX;
        stack[1] = seedY;

        while (top != 0)
        {
            --top;
            int x = stack[top * 2], y = stack[top * 2 + 1];
            int row = y * stride, outRow = y * width;

            if (mask[outRow + x] != 0 || !WandMatches(rgba, row + x * 4, reference, tolerance)) continue;

            int left = x, right = x;
            while (left > 0 && mask[outRow + left - 1] == 0 && WandMatches(rgba, row + (left - 1) * 4, reference, tolerance)) --left;
            while (right + 1 < width && mask[outRow + right + 1] == 0 && WandMatches(rgba, row + (right + 1) * 4, reference, tolerance)) ++right;

            for (int i = left; i <= right; ++i) mask[outRow + i] = 255; // 原 C: memset(out + left, 255, right - left + 1)
            count += right - left + 1;

            for (int side = 0; side < 2; ++side)
            {
                if (side == 0 ? y == 0 : y + 1 >= height) continue;
                int ny = side == 0 ? y - 1 : y + 1;
                int nrow = ny * stride, nout = ny * width;
                bool inRun = false;

                for (int nx = left; nx <= right; ++nx)
                {
                    bool candidate = mask[nout + nx] == 0 && WandMatches(rgba, nrow + nx * 4, reference, tolerance);

                    if (candidate && !inRun)
                    {
                        if (top == capacity)
                        {
                            // 原 C: realloc(stack, capacity * 4 * sizeof(size_t)); capacity *= 2;
                            Array.Resize(ref stack, capacity * 4);
                            capacity *= 2;
                        }

                        stack[top * 2] = nx;
                        stack[top * 2 + 1] = ny;
                        ++top;
                    }

                    inRun = candidate;
                }
            }
        }

        return count;
    }

    /// <summary>顺时针右转（屏幕坐标 y 向下）：东→南→西→北→东。</summary>
    private static int TurnRight(int d) => d == North ? East : d << 1;

    /// <summary>顺时针左转：东→北→西→南→东。</summary>
    private static int TurnLeft(int d) => d == East ? North : d >> 1;

    /// <summary>
    /// 沿像素边描出掩码的轮廓，得到一组闭环角点。直译自 <c>int wand_trace(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：<paramref name="points"/> 是 <c>(x, y)</c> 交替的像素边坐标（角点），
    /// 长度恒为偶数；<paramref name="loops"/> 每项是相邻闭环的角点数，
    /// 按 <c>points</c> 的切分顺序一一对应。外轮廓顺时针、孔洞逆时针，
    /// 因此非零环绕规则恰好填中原掩码的非零像素。
    /// </remarks>
    /// <param name="mask">掩码，非 0 视为实心像素，只读。</param>
    /// <param name="width">掩码宽度。</param>
    /// <param name="height">掩码高度。</param>
    /// <param name="points">输出角点，2 × 顶点数。</param>
    /// <param name="loops">输出每个闭环的角点数。</param>
    /// <returns>
    /// 0 = 成功（<paramref name="points"/>/<paramref name="loops"/> 为数组，可能为空）；
    /// -1 = 原 C 的 malloc 失败（宽高 ≥ <see cref="int.MaxValue"/>），C# 中另由
    /// <see cref="OutOfMemoryException"/> 覆盖分配失败；-2 = 边数超过 8000000，不值得描。
    /// </returns>
    public static int Trace(ReadOnlySpan<byte> mask, int width, int height, out int[] points, out int[] loops)
    {
        points = Array.Empty<int>();
        loops = Array.Empty<int>();

        if (width == 0 || height == 0) return 0;
        if (width >= int.MaxValue || height >= int.MaxValue) return -1;

        // Each vertex of the (width + 1) × (height + 1) grid records the directed boundary edges
        // leaving it: a selected pixel's unselected sides, walked clockwise around the pixel.
        int stride = width + 1, vertices = stride * (height + 1), edges = 0;
        byte[] outEdges = new byte[vertices]; // 原 C: calloc(vertices, 1)

        for (int y = 0; y < height; ++y)
        {
            int row = y * width;

            for (int x = 0; x < width; ++x)
            {
                if (mask[row + x] == 0) continue;
                if (y == 0 || mask[(y - 1) * width + x] == 0) { outEdges[y * stride + x] |= (byte)East; ++edges; }
                if (x + 1 == width || mask[row + x + 1] == 0) { outEdges[y * stride + x + 1] |= (byte)South; ++edges; }
                if (y + 1 == height || mask[(y + 1) * width + x] == 0) { outEdges[(y + 1) * stride + x + 1] |= (byte)West; ++edges; }
                if (x == 0 || mask[row + x - 1] == 0) { outEdges[(y + 1) * stride + x] |= (byte)North; ++edges; }
            }

            if (edges > WandEdgeLimit) return -2;
        }

        int pointCapacity = 1024, loopCapacity = 256, np = 0, nl = 0;
        int[] pts = new int[pointCapacity * 2];
        int[] lens = new int[loopCapacity];

        for (int start = 0; start < vertices; ++start)
        {
            while (outEdges[start] != 0)
            {
                int first = np, v = start;
                int heading = 0, initial = 0;

                do
                {
                    int bits = outEdges[v], d;

                    // Where two loops meet at a corner, turning right keeps them apart.
                    if (heading == 0) d = bits & -bits;
                    else if ((bits & TurnRight(heading)) != 0) d = TurnRight(heading);
                    else if ((bits & heading) != 0) d = heading;
                    else if ((bits & TurnLeft(heading)) != 0) d = TurnLeft(heading);
                    else d = bits & -bits;

                    if (d == 0) break;

                    outEdges[v] &= (byte)~d; // 原 C: out[v] &= (uint8_t)~d;

                    if (d != heading)
                    {
                        if (np == pointCapacity)
                        {
                            Array.Resize(ref pts, pointCapacity * 4);
                            pointCapacity *= 2;
                        }

                        pts[np * 2] = v % stride;
                        pts[np * 2 + 1] = v / stride;
                        ++np;
                    }

                    if (heading == 0) initial = d;
                    heading = d;
                    v = d == East ? v + 1 : d == West ? v - 1 : d == South ? v + stride : v - stride;
                } while (v != start);

                // The start is a corner unless the loop arrives on the heading it left with.
                if (heading == initial && np > first)
                {
                    // 原 C: memmove(pts + first*2, pts + (first+1)*2, (np - first - 1) * 2 * sizeof(int32_t));
                    Array.Copy(pts, (first + 1) * 2, pts, first * 2, (np - first - 1) * 2);
                    --np;
                }

                if (nl == loopCapacity)
                {
                    Array.Resize(ref lens, loopCapacity * 2);
                    loopCapacity *= 2;
                }

                lens[nl++] = np - first;
            }
        }

        // 只裁掉多余的容量，内容与原 C 的 (*pointCount, *loopCount) 完全一致。
        Array.Resize(ref pts, np * 2);
        Array.Resize(ref lens, nl);
        points = pts;
        loops = lens;
        return 0;
    }

    /// <summary>
    /// 三个颜色通道是否都与颜色表里某一项在容差内（原 C 的 <c>color_near</c>）。
    /// </summary>
    private static bool ColorNear(ReadOnlySpan<int> rgb, ReadOnlySpan<byte> colors, int count, int fuzziness)
    {
        for (int i = 0; i < count; ++i)
        {
            int c = i * 3;
            if (Math.Abs(rgb[0] - colors[c]) <= fuzziness &&
                Math.Abs(rgb[1] - colors[c + 1]) <= fuzziness &&
                Math.Abs(rgb[2] - colors[c + 2]) <= fuzziness) return true;
        }

        return false;
    }

    /// <summary>
    /// 色彩范围选区，原地写入掩码。直译自 <c>long color_range_mask(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：先把预乘像素反预乘成直通 sRGB（<c>(p*255 + a/2) / a</c> 的整数除法），
    /// 再比较——<b>反预乘结果不夹到 255</b>（原 C 的 <c>int rgb[3]</c> 不做截断，
    /// 像素数据非预乘时 rgb 可以大于 255，这里原样保留）。
    /// alpha 为 0 的像素永不匹配（invert 时除外：invert 会把它们选中）。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8，只读。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">rgba 每行字节数。</param>
    /// <param name="include">包含色，3 字节一个，直通 sRGB。</param>
    /// <param name="includeCount">包含色个数，须保证 <paramref name="include"/> 长度 ≥ includeCount*3。</param>
    /// <param name="exclude">排除色，3 字节一个，直通 sRGB。</param>
    /// <param name="excludeCount">排除色个数，长度须 ≥ excludeCount*3。</param>
    /// <param name="fuzziness">逐通道容差。</param>
    /// <param name="invert">非 0 = 反选（把不匹配的像素写 255）。</param>
    /// <param name="mask">输出掩码。</param>
    /// <returns>选中像素数。</returns>
    public static long ColorRangeMask(
        ReadOnlySpan<byte> rgba, int width, int height, int stride,
        ReadOnlySpan<byte> include, int includeCount,
        ReadOnlySpan<byte> exclude, int excludeCount,
        int fuzziness, int invert, Span<byte> mask)
    {
        long count = 0;
        Span<int> rgb = stackalloc int[3];

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride, outRow = y * width;

            for (int x = 0; x < width; ++x)
            {
                int px = row + x * 4;
                int matches = 0;

                if (rgba[px + 3] != 0)
                {
                    for (int c = 0; c < 3; ++c) rgb[c] = (rgba[px + c] * 255 + rgba[px + 3] / 2) / rgba[px + 3];
                    matches = ColorNear(rgb, include, includeCount, fuzziness) &&
                              !ColorNear(rgb, exclude, excludeCount, fuzziness) ? 1 : 0;
                }

                if (invert != 0) matches = matches != 0 ? 0 : 1;
                mask[outRow + x] = matches != 0 ? (byte)255 : (byte)0;
                count += matches;
            }
        }

        return count;
    }
}