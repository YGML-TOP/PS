using Compositor.Core;

namespace Compositor.Tools;

/// <summary>
/// 在目标表面上叠加一个笔尖。🔴 <b>唯一允许写像素的入口</b>。
/// </summary>
/// <remarks>
/// 🔴 <b>铁律 2（全程预乘）</b>：本类所有写入都是<b>预乘</b>值。
/// 颜色先经 <see cref="PixelBuffer.Premultiply"/> 预乘再按覆盖度累乘；
/// 橡皮走 <see cref="CompositeDestinationOut"/> 的<b>合成</b>路径，
/// <b>不是</b>把 alpha 直接置 0 —— 这是本模块最常见的错误。
/// </remarks>
public static class BrushCompositor
{
    /// <summary>
    /// 在目标缓冲上叠加一个笔尖 dab。
    /// </summary>
    /// <param name="target">目标像素（预乘 RGBA8），原地修改。</param>
    /// <param name="targetStride">目标每行字节数。</param>
    /// <param name="centerX">笔尖中心的 X，文档像素单位。</param>
    /// <param name="centerY">笔尖中心的 Y，文档像素单位（<b>Y 向下</b>）。</param>
    /// <param name="diameter">笔刷直径。</param>
    /// <param name="hardness">硬度 <c>[0,1]</c>。</param>
    /// <param name="opacity">笔刷不透明度 <c>[0.01,1]</c>。</param>
    /// <param name="straightColor">笔刷颜色（<b>直通</b>色，内部会预乘）。</param>
    /// <param name="bounds">受影响的文档矩形，供调用方合并失效区。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetStride"/> 不足一行。</exception>
    /// <remarks>
    /// 🔴 <b>铁律 1：全程 Y 向下</b>，<paramref name="centerY"/> 越大越靠下，
    /// 本方法内部<b>不做任何 <c>-y</c> 或 <c>height - y</c> 变换</b>。
    /// <para>
    /// <b>预乘不变量</b>：写入的是 <c>premultiplied(straightColor) * coverage * opacity</c>，
    /// 而非 <c>straightColor * …</c>。直接把直通色写进预乘缓冲会让半透明像素颜色偏暗。
    /// </para>
    /// </remarks>
    public static void StampDab(
        Span<byte> target, int targetStride,
        double centerX, double centerY,
        double diameter, double hardness, double opacity,
        RgbaColor straightColor,
        out DocRect bounds)
    {
        bounds = DocRect.Empty;
        if (diameter <= 0 || opacity <= 0)
        {
            return;
        }

        int width = targetStride / 4;
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetStride), targetStride, "行距不足一行像素。");
        }

        double radius = diameter / 2.0;

        // 向外取整（与 DocRect.Integral 同口径），保证不裁掉边缘半像素。
        int minX = (int)Math.Floor(centerX - radius);
        int maxX = (int)Math.Ceiling(centerX + radius);
        int minY = (int)Math.Floor(centerY - radius);
        int maxY = (int)Math.Ceiling(centerY + radius);

        // 先把颜色预乘一次：dab 内所有像素共用同一个预乘值。
        byte pr = PixelBuffer.Premultiply(straightColor.R, straightColor.A);
        byte pg = PixelBuffer.Premultiply(straightColor.G, straightColor.A);
        byte pb = PixelBuffer.Premultiply(straightColor.B, straightColor.A);
        byte pa = straightColor.A;

        int height = target.Length / targetStride;
        minX = Math.Max(0, minX);
        minY = Math.Max(0, minY);
        maxX = Math.Min(width, maxX);
        maxY = Math.Min(height, maxY);

        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                // Y 向下：dy 就是 y 与 centerY 的差，不做翻转。
                double dx = (x + 0.5) - centerX;
                double dy = (y + 0.5) - centerY;
                double cov = BrushFalloff.Coverage(Math.Sqrt(dx * dx + dy * dy), diameter, hardness);
                if (cov <= 0)
                {
                    continue;
                }

                double a = cov * opacity * pa;
                int idx = y * targetStride + x * 4;
                byte a8 = (byte)Math.Round(a, MidpointRounding.AwayFromZero);
                if (a8 <= 0)
                {
                    continue;
                }

                // 源（预乘）乘以 alpha 后再按标准 source-over 叠加。
                target[idx] = AddChannel(target[idx], (byte)((pr * a8 + 127) / 255));
                target[idx + 1] = AddChannel(target[idx + 1], (byte)((pg * a8 + 127) / 255));
                target[idx + 2] = AddChannel(target[idx + 2], (byte)((pb * a8 + 127) / 255));
                target[idx + 3] = AddChannel(target[idx + 3], a8);
            }
        }

        bounds = new DocRect(new DocPoint(minX, minY), new DocSize(maxX - minX, maxY - minY));
    }

    /// <summary>
    /// 在目标缓冲上做 <b><c>destination-out</c></b> 合成 —— 这是<b>橡皮</b>的语义。
    /// </summary>
    /// <param name="target">目标像素（预乘 RGBA8），原地修改。</param>
    /// <param name="targetStride">目标每行字节数。</param>
    /// <param name="centerX">笔尖中心 X。</param>
    /// <param name="centerY">笔尖中心 Y（<b>Y 向下</b>）。</param>
    /// <param name="diameter">笔刷直径。</param>
    /// <param name="hardness">硬度。</param>
    /// <param name="opacity">不透明度。</param>
    /// <param name="bounds">受影响的文档矩形。</param>
    /// <remarks>
    /// 🔴 <b>「橡皮」= <c>destination-out</c> 合成，不是把 alpha 设为 0。</b>
    /// 区别是致命的：<c>destination-out</c> 按 <c>α_out = α_dst × (1 − α_src)</c>
    /// <b>同时</b>把颜色按同一因子压暗，保持预乘不变量
    /// <c>RGB ≤ α</c>；若只把 alpha 置 0 而留下 RGB，半透明像素的颜色就会残留，
    /// 下次叠加时该像素会突然显出陈旧颜色 —— 这是本模块最典型的 bug。
    /// <para>
    /// 公式（W3C Compositing 1）：<c>Cr = 0, Cb = Cs × αb</c>；
    /// <c>αr = αs × αb</c>。因为源不透明色只贡献 alpha，颜色项自然按目标 alpha 压暗。
    /// </para>
    /// </remarks>
    public static void CompositeDestinationOut(
        Span<byte> target, int targetStride,
        double centerX, double centerY,
        double diameter, double hardness, double opacity,
        out DocRect bounds)
    {
        bounds = DocRect.Empty;
        if (diameter <= 0 || opacity <= 0)
        {
            return;
        }

        int width = targetStride / 4;
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetStride), targetStride, "行距不足一行像素。");
        }

        double radius = diameter / 2.0;
        int height = target.Length / targetStride;
        int minX = Math.Max(0, (int)Math.Floor(centerX - radius));
        int minY = Math.Max(0, (int)Math.Floor(centerY - radius));
        int maxX = Math.Min(width, (int)Math.Ceiling(centerX + radius));
        int maxY = Math.Min(height, (int)Math.Ceiling(centerY + radius));

        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                double dx = (x + 0.5) - centerX;
                double dy = (y + 0.5) - centerY;
                double cov = BrushFalloff.Coverage(Math.Sqrt(dx * dx + dy * dy), diameter, hardness);
                if (cov <= 0)
                {
                    continue;
                }

                double a = cov * opacity;
                byte keep = (byte)Math.Round((1.0 - a) * 255, MidpointRounding.AwayFromZero);

                int idx = y * targetStride + x * 4;
                byte dstA = target[idx + 3];

                // αr = αb × (1 − αs)：颜色与 alpha 同步按同一因子缩放，
                // 这样 RGB ≤ alpha 的预乘不变量得以保持。
                byte r = (byte)((target[idx] * keep + 127) / 255);
                byte g = (byte)((target[idx + 1] * keep + 127) / 255);
                byte b = (byte)((target[idx + 2] * keep + 127) / 255);
                byte na = (byte)((dstA * keep + 127) / 255);

                target[idx] = r;
                target[idx + 1] = g;
                target[idx + 2] = b;
                target[idx + 3] = na;
            }
        }

        bounds = new DocRect(new DocPoint(minX, minY), new DocSize(maxX - minX, maxY - minY));
    }

    /// <summary>两个 0–255 通道值相加并钳到 255。</summary>
    private static byte AddChannel(byte dst, byte src) => (byte)Math.Min(255, dst + src);
}