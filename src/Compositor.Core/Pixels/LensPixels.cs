namespace Compositor.Core.Pixels;

/// <summary>镜头桶形畸变的双线性重采样。直译自 <c>Rendering/LensPixels.c</c>（37 行，逐行对照）。</summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>void lens_distort(const uint8_t *source, uint8_t *destination,
/// size_t width, size_t height, size_t stride, double k)</c></para>
/// <para><b>不变量</b>：<c>destination</c> 按预乘 RGBA8 存储，<c>stride</c>
/// 为每行字节数且 ≥ width*4；<c>source</c> 与 <c>destination</c> 可以是同一块缓冲
/// （逐行读逐行写，行内已先算完所有累加再写回，故原位安全）。</para>
/// <para><b>与原 C 的等价改写</b>：仅 <c>size_t</c> 索引改 <c>int</c>；<c>double sums[4]</c> 改四个局部变量
/// （C 的数组每轮重新初始化为 0，四个局部变量的初值语义相同）；末行 <c>(uint8_t)lround(...)</c> 走
/// <see cref="CSemantics.U8"/> 饱和。算法、舍入、权重、边界判定逐行一致。</para>
/// </remarks>
public static class LensPixels
{
    /// <summary>把 <paramref name="source"/> 按桶形畸变重采样到 <paramref name="destination"/>。</summary>
    /// <param name="source">源像素，预乘 RGBA8，只读。</param>
    /// <param name="destination">目标像素，预乘 RGBA8，可写。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数，须 ≥ <paramref name="width"/> * 4。</param>
    /// <param name="k">畸变强度。0 为恒等（scale 恒为 1）。</param>
    public static void Distort(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int width,
        int height,
        int stride,
        double k)
    {
        double cx = width * 0.5, cy = height * 0.5;
        double halfDiagonal2 = cx * cx + cy * cy;

        for (int y = 0; y < height; ++y)
        {
            double dy = y + 0.5 - cy;
            int outRow = y * stride;

            for (int x = 0; x < width; ++x)
            {
                double dx = x + 0.5 - cx;
                double scale = 1.0 - k * (dx * dx + dy * dy) / halfDiagonal2;

                // Source position in pixel-center coordinates.
                double sx = cx + dx * scale - 0.5, sy = cy + dy * scale - 0.5;
                double fx0 = Math.Floor(sx), fy0 = Math.Floor(sy);
                double fx = sx - fx0, fy = sy - fy0;
                long x0 = (long)fx0, y0 = (long)fy0;

                double s0 = 0, s1 = 0, s2 = 0, s3 = 0;

                for (int j = 0; j < 2; ++j)
                {
                    long row = y0 + j;
                    if (row < 0 || row >= height) continue;
                    double wy = j != 0 ? fy : 1 - fy;
                    if (wy == 0) continue;
                    int line = (int)row * stride;

                    for (int i = 0; i < 2; ++i)
                    {
                        long column = x0 + i;
                        if (column < 0 || column >= width) continue;
                        double weight = wy * (i != 0 ? fx : 1 - fx);
                        if (weight == 0) continue;
                        int p = line + (int)column * 4;

                        s0 += weight * source[p];
                        s1 += weight * source[p + 1];
                        s2 += weight * source[p + 2];
                        s3 += weight * source[p + 3];
                    }
                }

                int o = outRow + x * 4;
                destination[o] = CSemantics.U8(CSemantics.LRound(s0));
                destination[o + 1] = CSemantics.U8(CSemantics.LRound(s1));
                destination[o + 2] = CSemantics.U8(CSemantics.LRound(s2));
                destination[o + 3] = CSemantics.U8(CSemantics.LRound(s3));
            }
        }
    }
}