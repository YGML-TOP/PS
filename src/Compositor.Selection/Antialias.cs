namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 覆盖度光栅化器：用规则超采样把"点在形状内"的判定转成 8-bit 覆盖度。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用超采样而不是解析求交。</b> 参照实现（Mac 版 <c>DocumentSelection.coverage</c>，
/// <c>Selection.swift:15-29</c>）把 CGPath 交给
/// <c>CGContext.setShouldAntialias</c> 让 CoreGraphics 自己抗锯齿。
/// CG 的抗锯齿采样策略<b>闭源、无公开规范</b>，因此本项目在边缘像素上
/// <b>确定无法与 Mac 版逐位对齐</b>（归入"可能不一致"一类）。
/// 但"边缘覆盖度 = 该像素被形状覆盖的比例"这个<b>语义</b>是明确的、可论证正确的，
/// 超采样正是对它的直接数值实现。
/// </para>
/// <para>
/// 本工具只保证<b>语义正确与结果可重现</b>（同一输入永远得到同一输出），
/// <b>不声称</b>与 Mac 版边缘像素一致。
/// </para>
/// </remarks>
public static class Antialias
{
    /// <summary>每轴的采样数，默认 4（每像素 4×4 = 16 个子采样点）。</summary>
    /// <remarks>
    /// 16 个子点意味着边缘覆盖度的量化步长为 <c>255/16 ≈ 15.94</c>。
    /// 选 4 而不是更高，是精度与开销的折中：再翻一倍只把步长降到约 7.98，
    /// 但光栅化开销翻四倍，而选区是每次拖动都要重算的高频操作。
    /// </remarks>
    public const int SamplesPerAxis = 4;

    /// <summary>单个形状内的判定回调。</summary>
    /// <param name="x">文档坐标 X，Y 向下（铁律 1）。</param>
    /// <param name="y">文档坐标 Y，Y 向下（铁律 1）。</param>
    /// <returns>该点是否落在形状内部（含边界）。</returns>
    public delegate bool PointTest(double x, double y);

    /// <summary>按包围盒逐像素光栅化，每个像素用 <see cref="SamplesPerAxis"/>×<see cref="SamplesPerAxis"/> 子采样求覆盖率。</summary>
    /// <param name="bounds">非空的文档坐标包围盒，应包含整个形状。</param>
    /// <param name="inside">形状内部的判定回调。</param>
    /// <param name="antialiased">
    /// 是否开启抗锯齿。为 <see langword="false"/> 时退化为<b>单点采样</b>，
    /// 结果只有 0 与 255 两种取值，不产生边缘灰度。
    /// </param>
    /// <returns>覆盖度平面；包围盒内完全不含形状时返回全零平面。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inside"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 该开关对应 Mac 版 <c>DocumentSelection.antialiased</c> 字段（<c>Selection.swift:9</c>）
    /// 与 <c>Selection.swift:19</c> 的 <c>setShouldAntialias(antialiased || feather &gt; 0)</c>。
    /// ⚠️ Mac 版在 <c>feather &gt; 0</c> 时会<b>强制打开</b>抗锯齿，
    /// 该联动规则由 <c>FeatherOps</c> 的调用方负责，本方法不做。
    /// </remarks>
    public static CoveragePlane Rasterize(DocRect bounds, PointTest inside, bool antialiased = true)
    {
        ArgumentNullException.ThrowIfNull(inside);

        int w = bounds.Size.Width;
        int h = bounds.Size.Height;
        var plane = CoveragePlane.CreateZero(bounds);

        int axis = antialiased ? SamplesPerAxis : 1;
        int total = axis * axis;
        double left = bounds.Left;
        double top = bounds.Top;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int hits = 0;

                for (int sy = 0; sy < axis; sy++)
                {
                    // 子采样点取每个子格的中心，避免样本落在像素边界上产生歧义。
                    double py = top + y + ((sy + 0.5) / axis);
                    for (int sx = 0; sx < axis; sx++)
                    {
                        double px = left + x + ((sx + 0.5) / axis);
                        if (inside(px, py))
                        {
                            hits++;
                        }
                    }
                }

                if (hits == 0)
                {
                    continue;
                }

                // 固定舍入：与 Swift 的 .rounded()（AwayFromZero）口径一致，
                // 不用 Math.Round 的默认银行家舍入，否则 0.5 这类中点会走另一条分支，
                // 导致同一份几何在不同平台上落到不同的字节值。
                plane.SetPixelUnchecked(x, y, (byte)Math.Round(hits * 255.0 / total, MidpointRounding.AwayFromZero));
            }
        }

        return plane;
    }
}