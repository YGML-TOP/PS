namespace Compositor.Core.Pixels;

/// <summary>
/// 笔刷 alpha 边界提取、alpha 通道分离与预乘/反预乘互转。
/// 直译自 <c>Rendering/BrushPixels.c</c>（50 行，逐行对照）。
/// </summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>brush_alpha_bounds</c> / <c>layer_extract_alpha</c> /
/// <c>layer_unpremultiply_opaque</c> / <c>layer_restore_alpha</c></para>
/// <para><b>整数舍入保持</b>：反预乘用 <c>(p[c] * 255u + a / 2) / a</c>（半值上取整），
/// 恢复预乘用 <c>(p[c] * a + 127) / 255</c>（127 偏置），两者都是 C 的整数除法（向零截断），
/// C# 整数 <c>/</c> 语义相同，逐行照抄未改。</para>
/// </remarks>
public static class BrushPixels
{
    /// <summary>
    /// 求笔刷 alpha 的紧致包围盒。直译自 <c>brush_alpha_bounds</c>。
    /// </summary>
    /// <param name="bytes">预乘 RGBA8 像素，只读。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数，须 ≥ <paramref name="width"/> * 4。</param>
    /// <param name="bounds">
    /// 输出 4 个值：<c>[0]</c>=left、<c>[1]</c>=top、<c>[2]</c>=right、<c>[3]</c>=bottom。
    /// 全部透明时四项均为 0（与原 C 一致：<c>bounds = {0,0,0,0}</c>，<c>bottom</c> 也归零）。
    /// </param>
    public static void AlphaBounds(ReadOnlySpan<byte> bytes, int width, int height, int stride, Span<int> bounds)
    {
        int left = width, right = 0, top = height, bottom = 0;

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            int first = 0;
            while (first < width && bytes[row + first * 4 + 3] == 0) ++first;
            if (first == width) continue;

            int last = width;
            while (last > first && bytes[row + (last - 1) * 4 + 3] == 0) --last;

            if (first < left) left = first;
            if (last > right) right = last;
            if (y < top) top = y;
            bottom = y + 1;
        }

        bounds[0] = right != 0 ? left : 0;
        bounds[1] = right != 0 ? top : 0;
        bounds[2] = right;
        bounds[3] = bottom;
    }

    /// <summary>
    /// 把 alpha 通道单独抽成 8-bit 灰度图。直译自 <c>layer_extract_alpha</c>。
    /// </summary>
    /// <remarks>灰度图与 alpha 通道等宽等高，stride 各自独立（蒙版与图层可以不同宽）。</remarks>
    /// <param name="rgba">源预乘 RGBA8 像素，只读。</param>
    /// <param name="rgbaStride">源每行字节数。</param>
    /// <param name="gray">目标 8-bit 灰度，只写 alpha 值。</param>
    /// <param name="grayStride">目标每行字节数。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    public static void ExtractAlpha(
        ReadOnlySpan<byte> rgba, int rgbaStride,
        Span<byte> gray, int grayStride,
        int width, int height)
    {
        for (int y = 0; y < height; ++y)
        {
            int src = y * rgbaStride;
            int dst = y * grayStride;
            for (int x = 0; x < width; ++x)
                gray[dst + x] = rgba[src + x * 4 + 3];
        }
    }

    /// <summary>
    /// 把预乘图层转成不透明直通色（alpha 强制置 255）。直译自 <c>layer_unpremultiply_opaque</c>。
    /// </summary>
    /// <remarks>
    /// 仅供"先反预乘 → 在直通域做计算 → 再 <see cref="RestoreAlpha"/> 回去"的工作流使用。
    /// 执行后缓冲<b>不再是预乘</b>，调用方必须配对调用 <see cref="RestoreAlpha"/> 才能恢复不变量。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    public static void UnpremultiplyOpaque(Span<byte> rgba, int stride, int width, int height)
    {
        for (int y = 0; y < height; ++y)
        {
            int p = y * stride;
            for (int x = 0; x < width; ++x, p += 4)
            {
                uint a = rgba[p + 3];
                for (int c = 0; c < 3; ++c)
                {
                    uint v = a != 0 ? (rgba[p + c] * 255u + a / 2) / a : 0u;
                    rgba[p + c] = v > 255u ? (byte)255 : (byte)v;
                }
                rgba[p + 3] = 255;
            }
        }
    }

    /// <summary>
    /// 用先前抽出的 alpha 重新预乘直通色。直译自 <c>layer_restore_alpha</c>。
    /// </summary>
    /// <param name="rgba">直通色 RGBA8 像素，原地修改为预乘。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="alpha">8-bit alpha 灰度图，通常来自 <see cref="ExtractAlpha"/>。</param>
    /// <param name="alphaStride">alpha 图每行字节数。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    public static void RestoreAlpha(
        Span<byte> rgba, int stride,
        ReadOnlySpan<byte> alpha, int alphaStride,
        int width, int height)
    {
        for (int y = 0; y < height; ++y)
        {
            int p = y * stride;
            for (int x = 0; x < width; ++x, p += 4)
            {
                int a = alpha[y * alphaStride + x];
                for (int c = 0; c < 3; ++c)
                    rgba[p + c] = (byte)((rgba[p + c] * a + 127) / 255);
                rgba[p + 3] = (byte)a;
            }
        }
    }
}