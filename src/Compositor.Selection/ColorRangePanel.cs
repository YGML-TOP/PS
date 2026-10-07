namespace Compositor.Selection;

using System.Collections.Generic;

/// <summary>
/// 面板上一个取色动作对「包含色 / 排除色」两个列表的影响，
/// 照抄 Mac 版 <c>HueSampleMode</c> 与 <c>ColorRangeEdit.sampleMode</c>（<c>Document/ColorRangeSelection.swift:14, 55-59</c>）。
/// </summary>
public enum HueSampleMode
{
    /// <summary>重新开始：清空排除色，<b>替换</b>整个包含色列表。对应 <c>ColorRangeSelection.swift:56</c>。</summary>
    Replace = 0,

    /// <summary>把该色<b>追加</b>到包含色。对应 <c>ColorRangeSelection.swift:57</c>。</summary>
    Add = 1,

    /// <summary>把该色<b>追加</b>到排除色。对应 <c>ColorRangeSelection.swift:58</c>。</summary>
    Remove = 2,
}

/// <summary>
/// Select ▸ Color Range 面板的<b>取色与滑杆语义</b>，逐行照抄
/// <c>Document/ColorRangeSelection.swift</c>。
/// </summary>
/// <remarks>
/// <para><b>与 <see cref="ColorRange"/> 的分工。</b>
/// <see cref="ColorRange.Select"/> 负责「拿到包含色/排除色之后怎么匹配像素」，
/// 本类负责「<b>怎么得到</b>包含色/排除色」与「滑杆的取值范围与取整」。
/// 这一层在 Mac 版是 <c>ColorRangeEdit</c> 里的属性 + <c>color(in:at:)</c> 私有方法，
/// 与 UI 绑在一起；这里拆成纯函数，便于单独验证。</para>
/// <para><b>三条最容易写错的语义：</b></para>
/// <list type="number">
/// <item><description>
/// 取色位置的取整是 <c>rounded(.down)</c>，即 <b>floor</b>（<c>ColorRangeSelection.swift:99</c>），
/// <b>不是</b>本项目别处常用的 <c>AwayFromZero</c>。
/// 对负坐标两者结果不同：<c>floor(-0.5) = -1</c>，而 <c>AwayFromZero</c> 会给 <c>-1</c>、
/// <c>AwayFromZero(-0.2) = 0</c> 而 <c>floor(-0.2) = -1</c>。
/// </description></item>
/// <item><description>
/// 取色反预乘的结果<b>要夹到 255</b>：<c>UInt8(min(255, (sums[c] * 255 + sums[a] / 2) / sums[a]))</c>
/// （<c>ColorRangeSelection.swift:111</c>）。
/// ⚠️ 而 <c>WandPixels.ColorRangeMask</c> 内部的反预乘<b>不夹到 255</b>。
/// 两处都是 Mac 版的真实行为，<b>不要顺手统一</b>。
/// </description></item>
/// <item><description>
/// 3×3 邻域在图像边缘会被<b>裁剪</b>（画到 3×3 上下文里，超出部分保持 0），
/// 但分母是<b>实际的 alpha 和</b>而非固定 9。
/// 所以贴边取色不会把颜色平均暗下去 —— 角上只累加 4 个像素，就用这 4 个的 alpha 和做分母。
/// </description></item>
/// </list>
/// </remarks>
public static class ColorRangePanel
{
    /// <summary>fuzziness 滑杆的下界，照抄 <c>ColorRangeSelection.swift:8</c> 的 <c>fuzzinessRange</c>。</summary>
    public const double FuzzinessMin = 0;

    /// <summary>fuzziness 滑杆的上界，照抄 <c>ColorRangeSelection.swift:8</c> 的 <c>fuzzinessRange</c>。</summary>
    public const double FuzzinessMax = 200;

    /// <summary>fuzziness 滑杆的缺省值，照抄 <c>ColorRangeSelection.swift:11</c> 的 <c>var fuzziness: Double = 40</c>。</summary>
    public const double FuzzinessDefault = 40;

    /// <summary>取色邻域的边长（像素），照抄 <c>ColorRangeSelection.swift:101</c> 的 <c>width: 3, height: 3</c>。</summary>
    public const int SampleKernelSize = 3;

    /// <summary>把 fuzziness 夹进滑杆范围，照抄 <c>ColorRangeSelection.swift:8</c> 的 <c>0...200</c>。</summary>
    /// <param name="fuzziness">滑杆原始值。</param>
    /// <returns>落在 <see cref="FuzzinessMin"/>–<see cref="FuzzinessMax"/> 内的值。</returns>
    /// <remarks>
    /// ⚠️ 上界是 <b>200</b>，不是 255。<c>ColorRange.Select</c> 内部对 fuzziness 夹的是 0–255，
    /// 那是 C 函数能接受的范围；<b>面板</b>的语义是 0–200，两处不要互相套用。
    /// </remarks>
    public static double ClampFuzziness(double fuzziness)
    {
        if (double.IsNaN(fuzziness))
        {
            return FuzzinessDefault;
        }

        return Math.Clamp(fuzziness, FuzzinessMin, FuzzinessMax);
    }

    /// <summary>把滑杆值转成传给 C 函数的整数，照抄 <c>ColorRangeSelection.swift:71</c> 的 <c>Int32(edit.fuzziness.rounded())</c>。</summary>
    /// <param name="fuzziness">滑杆原始值（未夹范围也可以）。</param>
    /// <returns>夹到 0–200 后按 <b>AwayFromZero</b> 取整的整数。</returns>
    /// <remarks>
    /// 🔴 <b>这里用 AwayFromZero，与取色位置的 floor 相反。</b>
    /// 两处不要混：<c>ColorRangeSelection.swift:71</c> 的 <c>.rounded()</c> 在 Swift 里就是
    /// 远离零取整，而 <c>:99</c> 显式写了 <c>.rounded(.down)</c> 即 floor。
    /// 用 C# 的 <c>Math.Round</c> 默认 <c>ToEven</c> 会在 0.5 / 1.5 这类中点走另一条分支。
    /// </remarks>
    public static int FuzzinessToInt(double fuzziness) =>
        (int)Math.Round(ClampFuzziness(fuzziness), MidpointRounding.AwayFromZero);

    /// <summary>
    /// 判定当前生效的取色模式，照抄 <c>ColorRangeSelection.swift:17</c> 的
    /// <c>var effectiveMode: HueSampleMode { held ?? sampleMode }</c>。
    /// </summary>
    /// <param name="held">当前按住的修饰键对应的模式；没按住时为 <see langword="null"/>。</param>
    /// <param name="sampleMode">面板上选定的取色器模式。</param>
    /// <returns>按住修饰键时用它，否则用面板选定的那个。</returns>
    public static HueSampleMode EffectiveMode(HueSampleMode? held, HueSampleMode sampleMode) =>
        held ?? sampleMode;

    /// <summary>
    /// 把一次取色应用到包含色/排除色两个列表，逐字照抄
    /// <c>ColorRangeSelection.swift:55-59</c>。
    /// </summary>
    /// <param name="mode">生效的取色模式（先由 <see cref="EffectiveMode"/> 决定）。</param>
    /// <param name="include">包含色列表，<b>就地</b>修改。</param>
    /// <param name="exclude">排除色列表，<b>就地</b>修改。</param>
    /// <param name="color">刚取到的直通 sRGB 三通道色，每通道 1 字节。</param>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="color"/> 长度不是 3。</exception>
    /// <remarks>
    /// <code>
    /// switch option ? .remove : shift ? .add : edit.sampleMode {
    /// case .replace: edit.include = color; edit.exclude = []
    /// case .add:     edit.include += color
    /// case .remove:  edit.exclude += color
    /// }
    /// </code>
    /// <para>
    /// 🔴 <b><see cref="HueSampleMode.Replace"/> 会把包含色整个换成这一个颜色，并清空排除色</b> ——
    /// 它不是「往里加一个」，而是「重新开始」。这条最容易实现成 append。
    /// </para>
    /// </remarks>
    public static void Apply(HueSampleMode mode, List<byte> include, List<byte> exclude, byte[] color)
    {
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(exclude);
        ArgumentNullException.ThrowIfNull(color);

        if (color.Length != 3)
        {
            throw new ArgumentException("颜色必须正好 3 字节（直通 sRGB）。", nameof(color));
        }

        switch (mode)
        {
            case HueSampleMode.Replace:
                include.Clear();
                include.AddRange(color);
                exclude.Clear();
                break;

            case HueSampleMode.Add:
                include.AddRange(color);
                break;

            case HueSampleMode.Remove:
                exclude.AddRange(color);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的取色模式。");
        }
    }

    /// <summary>是否已经取过色，照抄 <c>ColorRangeSelection.swift:28</c> 的 <c>var hasColors: Bool { !include.isEmpty }</c>。</summary>
    /// <param name="include">包含色列表。</param>
    /// <returns>包含色非空时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="include"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// ⚠️ 判据只看<b>包含色</b>，不看排除色。所以「只取排除色、没取包含色」<b>不算</b>已取过色，
    /// 此时 <c>updateColorRange</c>（<c>ColorRangeSelection.swift:69</c>）会还原原选区而不是去匹配。
    /// </remarks>
    public static bool HasColors(List<byte> include)
    {
        ArgumentNullException.ThrowIfNull(include);
        return include.Count > 0;
    }

    /// <summary>
    /// 取点周围 3×3 像素的平均色（直通 sRGB），逐字照抄 Mac 版
    /// <c>color(in:at:)</c>（<c>ColorRangeSelection.swift:98-112</c>）。
    /// </summary>
    /// <param name="rgba">预乘 RGBA8 像素，只读。</param>
    /// <param name="width">图像宽（像素）。</param>
    /// <param name="height">图像高（像素）。</param>
    /// <param name="stride">每行字节数，须 ≥ <c>width * 4</c>。</param>
    /// <param name="pointX">取色点的文档 X 坐标（可为小数，内部 floor）。</param>
    /// <param name="pointY">取色点的文档 Y 坐标（可为小数，内部 floor）。</param>
    /// <returns>长度 3 的直通 sRGB 色；坐标越界或邻域 alpha 全为 0 时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非正、stride 不足一行或缓冲过短。</exception>
    /// <remarks>
    /// Mac 版把整幅图画进一个 3×3 上下文（平移 <c>(1 - x, 1 - y)</c>），使被点的像素落在上下文中心，
    /// 然后把 9 个格子的四个通道各自求和。超出图像的部分<b>根本没被画进上下文</b>，保持 0。
    /// <para>
    /// 🔴 <b>所以分母是实际累加到的 alpha 和，不是 9，也不是固定像素数。</b>
    /// 这正是边缘取色不会把颜色平均暗下去的原因：图像角上只累加 4 个像素，就用这 4 个的 alpha 和做分母。
    /// 反之若误用固定分母 9 或固定样本数 4，贴边取色会得到一个明显偏暗的颜色。
    /// </para>
    /// <para>
    /// 反预乘公式照抄 <c>:111</c>：<c>min(255, (sums[c] * 255 + sums[a] / 2) / sums[a])</c>，
    /// 整数除法 + 四舍五入进位（<c>+ sums[a]/2</c>），<b>结果夹到 255</b>。
    /// </para>
    /// </remarks>
    public static byte[]? Sample3x3(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int stride,
        double pointX,
        double pointY)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}×{height}", "图像尺寸必须为正。");
        }

        if (stride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"stride 不足一行，至少需要 {width * 4}。");
        }

        if (rgba.Length < (long)height * stride)
        {
            throw new ArgumentException("像素缓冲长度不足。", nameof(rgba));
        }

        // 照抄 :99 —— rounded(.down) 即 floor，不是 AwayFromZero。
        double fx = Math.Floor(pointX);
        double fy = Math.Floor(pointY);
        if (!double.IsFinite(fx) || !double.IsFinite(fy))
        {
            return null;
        }

        int x = (int)fx;
        int y = (int)fy;

        // 照抄 :100 的 (0..<width).contains(x)。
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return null;
        }

        Span<int> sums = stackalloc int[4];
        int half = SampleKernelSize / 2;
        int last = SampleKernelSize - 1;

        for (int row = 0; row <= last; row++)
        {
            int sy = y - half + row;
            if (sy < 0 || sy >= height)
            {
                // 超出图像：Mac 版没画进 3×3 上下文，保持 0 —— 相当于跳过。
                continue;
            }

            int rowBase = sy * stride;
            for (int col = 0; col <= last; col++)
            {
                int sx = x - half + col;
                if (sx < 0 || sx >= width)
                {
                    continue;
                }

                int px = rowBase + (sx * 4);
                for (int ch = 0; ch < 4; ch++)
                {
                    sums[ch] += rgba[px + ch];
                }
            }
        }

        // 照抄 :110 —— alpha 和为 0 表示这一片完全透明，取不到颜色。
        if (sums[3] <= 0)
        {
            return null;
        }

        var color = new byte[3];
        for (int ch = 0; ch < 3; ch++)
        {
            // 整数除法 + 进位四舍五入；注意 min(255, …) 这一步，:111 明确要夹。
            int v = ((sums[ch] * 255) + (sums[3] / 2)) / sums[3];
            color[ch] = (byte)Math.Min(255, v);
        }

        return color;
    }
}