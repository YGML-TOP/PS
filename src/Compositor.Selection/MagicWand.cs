namespace Compositor.Selection;

using Compositor.Core;
using Compositor.Core.Pixels;

/// <summary>魔棒的取样范围，对应 Mac 版 <c>WandSampleSize</c>（<c>Document/MagicWand.swift:3-8</c>）。</summary>
public enum WandSampleSize
{
    /// <summary>只取点击处的单个像素（"Point Sample"）。</summary>
    Point = 0,

    /// <summary>取点击点周围 3×3 的平均（"3 by 3 Average"）。</summary>
    ThreeByThree = 1,

    /// <summary>取点击点周围 5×5 的平均（"5 by 5 Average"）。</summary>
    FiveByFive = 2,
}

/// <summary>魔棒的选项，对应 Mac 版 <c>WandSettings</c>（<c>Document/MagicWand.swift:11-19</c>）。</summary>
/// <param name="Tolerance">每个通道最多允许与参考色相差多少（0–255）。Mac 版默认 32。</param>
/// <param name="SampleSize">参考色的取样范围。</param>
/// <param name="Contiguous">为 <see langword="true"/> 时只选中<b>与点击处连通</b>的相似像素。</param>
public readonly record struct WandOptions(
    int Tolerance = 32,
    WandSampleSize SampleSize = WandSampleSize.Point,
    bool Contiguous = true)
{
    /// <summary>Mac 版 <c>WandSettings()</c> 的实际初值：容差 32、单点取样、只选连通区。</summary>
    /// <remarks>
    /// 🔴 <b>不要用 <c>default(WandOptions)</c> 代替它。</b>
    /// 位置参数记录结构体在 <c>default</c> 时<b>不经过构造器</b>，
    /// 得到的全是零值 —— 容差 0、<b>Contiguous = false</b>，
    /// 与 Mac 版 <c>WandSettings()</c>（<c>MagicWand.swift:11-19</c>）的
    /// <c>tolerance = 32</c> / <c>contiguous = true</c> 正好相反。
    /// 症状是"默认参数下魔棒把整张图里所有相近像素都选上了"。
    /// 正因如此，<see cref="MagicWand.Select"/> 的 options 参数是<b>必填</b>，不给可选默认值。
    /// </remarks>
    public static WandOptions Default => new(32, WandSampleSize.Point, true);
}

/// <summary>
/// 魔棒选区：选中与点击处颜色相近的像素。
/// </summary>
/// <remarks>
/// <para>
/// 核心算法<b>不在本文件</b>：容差比较、连通填充、轮廓描边都在
/// <c>Rendering/WandPixels.c</c>（205 行），由 AI-1 直译为 <see cref="WandPixels"/>。
/// 本类只负责 Mac 版 <c>MagicWand.select(in:at:settings:)</c>（<c>MagicWand.swift:36-52</c>）
/// 那一层：取整、边界守卫、参数钳位、把二值掩码包成覆盖度平面。
/// </para>
/// <para>
/// 🔴 <b>铁律 2：颜色比较发生在预乘空间。</b>
/// <c>WandPixels.Mask</c> 直接在传入的预乘 RGBA 上逐通道比较（<c>wand_matches</c>），
/// 这与 Mac 版把像素画进 <c>premultipliedLast</c> 的 context 后再比较<b>是同一件事</b>。
/// <b>后果</b>：半透明像素的 RGB 已被 alpha 缩小，容差判定会偏严。
/// 这不是缺陷而是参照实现的真实行为，照抄不改。
/// </para>
/// <para>
/// ⚠️ <b>与色彩范围的不对称（已核对源码）</b>：
/// <c>WandPixels.ColorRangeMask</c> 会<b>先反预乘成直通 sRGB</b>再比较，
/// 而魔棒<b>不反预乘</b>。同一个像素用魔棒与色彩范围可能得到不同结果。
/// 这是 Mac 版两个文件各自独立实现造成的，不是移植引入的差异。
/// </para>
/// <para>
/// ⚠️ <b>铁律 3：比较在编码 sRGB 空间。</b>
/// 传进来的 RGBA 必须是编码 sRGB 的预乘值；若上游换成线性光空间，容差语义会整体偏移。
/// <c>00f-合并窗口执行清单.md</c> §7 已登记「色彩空间标定（编码 sRGB）未经 Mac 实测」。
/// </para>
/// </remarks>
public static class MagicWand
{
    /// <summary>在图像上执行一次魔棒选区。</summary>
    /// <param name="rgba">预乘 RGBA8 像素，只读。</param>
    /// <param name="width">图像宽（像素）。</param>
    /// <param name="height">图像高（像素）。</param>
    /// <param name="stride">每行字节数，须 ≥ <c>width * 4</c>。</param>
    /// <param name="pointX">点击处的文档 X，Y 向下（铁律 1）。</param>
    /// <param name="pointY">点击处的文档 Y，Y 向下（铁律 1）。</param>
    /// <param name="options">魔棒选项；<b>必填</b>，没有可选默认值（原因见 <see cref="WandOptions.Default"/>）。</param>
    /// <returns>
    /// 二值覆盖度平面（0 或 255）；无像素匹配或点击越界时返回 <see langword="null"/>，
    /// 对应 <c>MagicWand.swift:39</c> 与 <c>:50</c> 的两处 <c>return nil</c>。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/>、<paramref name="height"/> 不为正，或 <paramref name="stride"/> 不足一行。
    /// </exception>
    /// <remarks>
    /// 🔴 <b>取整是向下取整，不是远离零。</b>
    /// Mac 版 <c>MagicWand.swift:38</c> 写的是
    /// <c>Int(point.x.rounded(.down))</c>，<c>.down</c> 即 floor。
    /// 这与 <c>DragBox</c> 用的 <c>.rounded()</c>（<c>Selection.swift:95</c>，AwayFromZero）<b>不是同一个东西</b> ——
    /// 同一份代码里两种取整规则并存，抄错任何一处都会让种子像素差一行/一列。
    /// </remarks>
    public static CoveragePlane? Select(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int stride,
        double pointX,
        double pointY,
        WandOptions options)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}×{height}", "图像尺寸必须为正。");
        }

        long need = (long)width * height * 4;
        if (stride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"stride 不足一行，至少需要 {width * 4}。");
        }

        if (rgba.Length < need)
        {
            throw new ArgumentException($"像素缓冲长度 {rgba.Length} 不足 {need}。", nameof(rgba));
        }

        if (!double.IsFinite(pointX) || !double.IsFinite(pointY))
        {
            return null;
        }

        // 🔴 floor，不是 Math.Round 的任何一种模式。照抄 MagicWand.swift:38 的 rounded(.down)。
        int seedX = (int)Math.Floor(pointX);
        int seedY = (int)Math.Floor(pointY);

        if (seedX < 0 || seedY < 0 || seedX >= width || seedY >= height)
        {
            return null;
        }

        // 容差钳到 0–255，照抄 MagicWand.swift:46 的 min(255, max(0, tolerance))。
        int tolerance = Math.Clamp(options.Tolerance, 0, 255);
        int radius = (int)Math.Clamp((int)options.SampleSize, 0, 2);

        var mask = new byte[width * height];
        long count = WandPixels.Mask(
            rgba, width, height, stride,
            seedX, seedY, radius, tolerance, options.Contiguous ? 1 : 0, mask);

        // WandPixels 在分配失败时抛 OutOfMemoryException 而非返回 -1，
        // 因此 count 非负；仍按原 C 语义守一道 > 0。
        if (count <= 0)
        {
            return null;
        }

        // 二值掩码整幅都是 0/255，包围盒要收缩到内容，否则绝大部分是空白。
        return FromMask(mask, width, height);
    }

    /// <summary>把任意二值掩码包成选区，并把包围盒收缩到实际有内容的范围。</summary>
    /// <param name="mask">行优先掩码，长度须为 <c>width * height</c>，0 或 255。</param>
    /// <param name="width">掩码宽（像素）。</param>
    /// <param name="height">掩码高（像素）。</param>
    /// <returns>选区覆盖度平面；掩码全为 0 时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非正。</exception>
    /// <exception cref="ArgumentException">掩码长度与尺寸不符。</exception>
    /// <remarks>
    /// 本方法承担 Mac 版 <c>MagicWand.outline(of:width:height:)</c>（<c>MagicWand.swift:55-80</c>）
    /// 的<b>用途</b>——把掩码变成可用的选区——但走的是<b>不同的中间表示</b>：
    /// Mac 走「掩码 → 像素边描边 → CGPath」（调用 C 的 <c>wand_trace</c>），
    /// 本实现直接产出覆盖度、跳过路径描边。
    /// <para>
    /// 差异仅在于 Mac 随后要把路径重新光栅化（那一步才有抗锯齿），
    /// 而本实现<b>直接给出二值覆盖度、边缘没有抗锯齿灰度</b>。
    /// 两者覆盖的<b>像素集合</b>完全相同。
    /// </para>
    /// </remarks>
    public static CoveragePlane? FromMask(ReadOnlySpan<byte> mask, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}×{height}", "尺寸必须为正。");
        }

        if (mask.Length != width * height)
        {
            throw new ArgumentException($"掩码长度 {mask.Length} 与尺寸 {width}×{height} 不符。", nameof(mask));
        }

        return new CoveragePlane(
            new DocRect(new DocPoint(0, 0), new DocSize(width, height)), mask.ToArray()).TrimToContent();
    }
}

/// <summary>
/// 色彩范围选区（Color Range 面板算法）。
/// </summary>/// <remarks>
/// 对应 Mac 版 <c>Document/ColorRangeSelection.swift</c> 与 C 的 <c>color_range_mask</c>，
/// 后者由 AI-1 直译为 <see cref="WandPixels.ColorRangeMask"/>。
/// <para>
/// ⚠️ <b>本类与魔棒的颜色空间口径不同，且这是 Mac 版的真实行为。</b>
/// <c>ColorRangeMask</c> 会<b>反预乘</b>成直通 sRGB 再比较（<c>WandPixels.cs:374</c>），
/// 魔棒则直接比预乘值。同一像素用两者可能得到不同结果。
/// </para>
/// </remarks>
public static class ColorRange
{
    /// <summary>按包含色/排除色与容差做色彩范围选区。</summary>
    /// <param name="rgba">预乘 RGBA8 像素，只读。</param>
    /// <param name="width">图像宽（像素）。</param>
    /// <param name="height">图像高（像素）。</param>
    /// <param name="stride">每行字节数，须 ≥ <c>width * 4</c>。</param>
    /// <param name="include">包含色，直通 sRGB，每色 3 字节。</param>
    /// <param name="exclude">排除色，直通 sRGB，每色 3 字节；可为空。</param>
    /// <param name="fuzziness">逐通道容差。</param>
    /// <param name="invert">为 <see langword="true"/> 时选中所有<b>不匹配</b>的像素。</param>
    /// <returns>
    /// 二值覆盖度平面；无像素匹配时返回 <see langword="null"/>。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非正或 stride 不足一行。</exception>
    /// <remarks>
    /// ⚠️ <b>alpha 为 0 的像素永不匹配</b>（<c>WandPixels.cs:372</c>），
    /// 唯一的例外是 <paramref name="invert"/> 为 <see langword="true"/> 时它们会被选中。
    /// </remarks>
    public static CoveragePlane? Select(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int stride,
        ReadOnlySpan<byte> include,
        ReadOnlySpan<byte> exclude,
        int fuzziness,
        bool invert = false)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}×{height}", "图像尺寸必须为正。");
        }

        if (stride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"stride 不足一行，至少需要 {width * 4}。");
        }

        if (rgba.Length < (long)width * height * 4)
        {
            throw new ArgumentException("像素缓冲长度不足。", nameof(rgba));
        }

        if (include.Length % 3 != 0)
        {
            throw new ArgumentException("包含色每色必须正好 3 字节。", nameof(include));
        }

        if (exclude.Length % 3 != 0)
        {
            throw new ArgumentException("排除色每色必须正好 3 字节。", nameof(exclude));
        }

        var mask = new byte[width * height];
        long count = WandPixels.ColorRangeMask(
            rgba, width, height, stride,
            include, include.Length / 3,
            exclude, exclude.Length / 3,
            Math.Clamp(fuzziness, 0, 255), invert ? 1 : 0, mask);

        if (count <= 0)
        {
            return null;
        }

        var full = new CoveragePlane(new DocRect(new DocPoint(0, 0), new DocSize(width, height)), mask);
        return full.TrimToContent();
    }
}