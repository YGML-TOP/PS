namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 覆盖度平面与图层蒙版之间的转换。
/// </summary>
/// <remarks>
/// 🔴 <b>铁律 2：选区与蒙版共用同一种数据格式 —— 8-bit 覆盖度、无 alpha。</b>
/// 所以"选区 → 蒙版"在数据层面<b>不需要任何格式转换</b>，
/// 本类的职责是把这个恒等关系<b>显式化并加上阈值语义</b>，
/// 同时提供反向的"载入选区"能力。
/// <para>
/// ⚠️ <b>本文件更正一处方向性误读：</b>
/// <c>Document/MaskTracing.swift</c> 的注释是
/// 「Turns <b>raster coverage into a selection outline</b>」（<c>MaskTracing.swift:3</c>），
/// 即<b>蒙版 / 图层像素 → 选区</b>，用于 Cmd-click 蒙版缩略图与图层缩略图
/// （<c>MaskTracing.swift:76-83</c> 与 <c>:87-94</c>），
/// <b>不是</b>「选区 → 蒙版」。反方向的"从选区创建图层蒙版"
/// 属于 <c>Document/LayerMask.swift</c> 的范围（AI-5），
/// 由 <c>SelectionEditTests.swift:160 maskButtonAddsWhiteMaskOrRevealsTheSelection</c> 覆盖。
/// </para>
/// </remarks>
public static class MaskConversion
{
    /// <summary>
    /// 50% 灰阈值，区分蒙版的"显示"与"隐藏"。
    /// </summary>
    /// <remarks>
    /// 逐字照抄 Mac 版 <c>MaskTracing</c> 的三个入口
    /// （<c>Document/MaskTracing.swift:6, 9, 12</c>）：一律以 <c>128</c> 为界，
    /// 且<b>用 <c>&gt;= 128</c> 判"白"、<c>&lt; 128</c> 判"黑"</b>。
    /// 这不是"一半"，所以恰好等于 128 的像素归入"白"。
    /// </remarks>
    public const byte Threshold = 128;

    /// <summary>
    /// 选区 → 蒙版：覆盖度直通，得到可直接交给图层蒙版使用的覆盖度平面。
    /// </summary>
    /// <param name="selection">选区覆盖度平面。</param>
    /// <param name="threshold">是否二值化。为 <see langword="false"/>（默认）时逐值直通。</param>
    /// <returns>与 <paramref name="selection"/> 同尺寸的覆盖度平面。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 默认路径是<b>逐值直通</b>：选区的 0–255 灰度原样成为蒙版的 0–255 灰度。
    /// 这正是铁律 2 的直接结果 —— 选区与蒙版是同一种 8-bit 覆盖度，
    /// 中间不存在格式转换，也不存在 alpha 通道。
    /// <para>
    /// <paramref name="threshold"/> 为 <see langword="true"/> 时按
    /// <see cref="Threshold"/> 二值化，得到纯黑白蒙版。
    /// 🔴 <b>二值化会丢弃羽化信息</b>，因此只在明确需要硬边蒙版时使用。
    /// </para>
    /// </remarks>
    public static CoveragePlane ToMask(CoveragePlane selection, bool threshold = false)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (!threshold)
        {
            return selection.Clone();
        }

        var result = new byte[selection.Pixels.Length];
        ReadOnlySpan<byte> src = selection.Pixels;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = src[i] >= Threshold ? (byte)255 : (byte)0;
        }

        return new CoveragePlane(selection.Bounds, result);
    }

    /// <summary>
    /// 蒙版 → 选区（"载入选区"）：按 50% 阈值取出蒙版的黑区或白区。
    /// </summary>
    /// <param name="mask">蒙版覆盖度平面。</param>
    /// <param name="selectDark">
    /// 为 <see langword="true"/> 时取<b>黑</b>区（<c>&lt; 128</c>），对应
    /// <c>MaskTracing.darkPixels</c>（<c>MaskTracing.swift:6</c>），
    /// 也是 Cmd-click 蒙版缩略图的真实行为（<c>MaskTracing.swift:79</c>）；
    /// 为 <see langword="false"/> 时取白区，对应 <c>whitePixels</c>（<c>MaskTracing.swift:9</c>）。
    /// </param>
    /// <returns>二值化的选区覆盖度平面；蒙版内没有任何像素通过阈值时返回全零平面。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 对应 Mac 版 <c>loadMaskSelection(layerID:mode:)</c>（<c>MaskTracing.swift:76-83</c>）。
    /// 注意 Mac 版先做<b>路径描边</b>（<c>trace</c>，<c>MaskTracing.swift:17-70</c>）
    /// 再交给 <c>applySelection</c>；本实现直接产出覆盖度，跳过了描边这一中间表示。
    /// <para>
    /// ⚠️ <b>已知差异：可能不一致。</b> Mac 版路径描边只在像素<b>边界</b>上取整，
    /// 而本实现逐像素赋值 255。两者在二值区域<b>覆盖的像素集合相同</b>，
    /// 差异仅在于 Mac 版随后要重新光栅化路径、抗锯齿会重新产生边缘灰度。
    /// </para>
    /// </remarks>
    public static CoveragePlane FromMask(CoveragePlane mask, bool selectDark)
    {
        ArgumentNullException.ThrowIfNull(mask);

        var result = new byte[mask.Pixels.Length];
        ReadOnlySpan<byte> src = mask.Pixels;

        for (int i = 0; i < result.Length; i++)
        {
            bool pass = selectDark ? src[i] < Threshold : src[i] >= Threshold;
            if (pass)
            {
                result[i] = 255;
            }
        }

        return new CoveragePlane(mask.Bounds, result);
    }

    /// <summary>
    /// 逐像素差异掩码：<c>|after − before|</c>，用于量化"羽化前后到底改变了什么"。
    /// </summary>
    /// <param name="before">羽化前的覆盖度平面。</param>
    /// <param name="after">羽化后的覆盖度平面（其包围盒可能更大）。</param>
    /// <returns>差异掩码；值相同处为 0，差异处为绝对差值。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 包围盒取两者的并集 —— 羽化会把覆盖度扩散到原包围盒之外，
    /// 只按原包围盒比对会漏掉外侧新出现的渐变。
    /// </remarks>
    public static CoveragePlane Diff(CoveragePlane before, CoveragePlane after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return BooleanOps.Exclude(before, after);
    }

    /// <summary>
    /// 统计羽化前后有多少像素发生了变化、以及变化的绝对量之和。
    /// </summary>
    /// <param name="before">羽化前的覆盖度平面。</param>
    /// <param name="after">羽化后的覆盖度平面。</param>
    /// <returns>差异统计结果。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 羽化必然改变包围盒（见 <see cref="FeatherOps.CoverageBounds"/>），
    /// 所以"未变化的像素"里包含了一圈原包围盒之外、本就不该被选中的区域。
    /// 本统计把<b>并集包围盒</b>作为比较域，让调用方能区分
    /// "外侧新增的未选中区域"（预期内）与"原有区域的数值变化"（羽化的实际效果）。
    /// </remarks>
    public static FeatherDiffStats FeatherDifference(CoveragePlane before, CoveragePlane after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        DocRect bounds = before.Bounds.Union(after.Bounds);
        int w = bounds.Size.Width;
        int h = bounds.Size.Height;

        int changed = 0;
        long totalDelta = 0;

        int aOffX = (int)(before.Bounds.Left - bounds.Left);
        int aOffY = (int)(before.Bounds.Top - bounds.Top);
        int bOffX = (int)(after.Bounds.Left - bounds.Left);
        int bOffY = (int)(after.Bounds.Top - bounds.Top);

        ReadOnlySpan<byte> ap = before.Pixels;
        ReadOnlySpan<byte> bp = after.Pixels;

        for (int y = 0; y < h; y++)
        {
            int ay = y - aOffY;
            int by = y - bOffY;
            bool aRowOk = (uint)ay < (uint)before.Height;
            bool bRowOk = (uint)by < (uint)after.Height;

            for (int x = 0; x < w; x++)
            {
                byte av = 0;
                byte bv = 0;

                if (aRowOk)
                {
                    int ax = x + aOffX;
                    if ((uint)ax < (uint)before.Width)
                    {
                        av = ap[(ay * before.Width) + ax];
                    }
                }

                if (bRowOk)
                {
                    int bx = x + bOffX;
                    if ((uint)bx < (uint)after.Width)
                    {
                        bv = bp[(by * after.Width) + bx];
                    }
                }

                int d = Math.Abs(av - bv);
                if (d != 0)
                {
                    changed++;
                    totalDelta += d;
                }
            }
        }

        return new FeatherDiffStats(changed, totalDelta, w * h);
    }
}

/// <summary>羽化前后的差异统计。</summary>
/// <param name="ChangedPixels">并集包围盒内取值发生变化的像素数。</param>
/// <param name="TotalAbsoluteDelta">全部变化像素的绝对差值之和。</param>
/// <param name="ComparedPixels">参与比较的像素总数（两包围盒的并集面积）。</param>
public readonly record struct FeatherDiffStats(int ChangedPixels, long TotalAbsoluteDelta, int ComparedPixels)
{
    /// <summary>发生变化的像素占比，0–1。无像素变化时为 0。</summary>
    public double ChangedRatio => ComparedPixels == 0 ? 0 : (double)ChangedPixels / ComparedPixels;

    /// <summary>变化像素的平均绝对差值；无变化时为 0。</summary>
    public double MeanDelta => ChangedPixels == 0 ? 0 : (double)TotalAbsoluteDelta / ChangedPixels;
}