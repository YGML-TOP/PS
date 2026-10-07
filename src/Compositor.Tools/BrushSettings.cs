using Compositor.Core;

namespace Compositor.Tools;

/// <summary>
/// 笔刷参数。<b>取值范围与校验逐条照抄 <c>BrushStroke.swift:225-227</c></b>，未放宽一端。
/// </summary>
/// <remarks>
/// 🔴 Mac 侧原始校验：
/// <code>
/// settings.diameter.isFinite, (1...Self.maxDiameter).contains(settings.diameter),
/// settings.hardness.isFinite, (0...1).contains(settings.hardness),
/// settings.opacity.isFinite, (0.01...1).contains(settings.opacity) else { throw ProjectError.tooLarge }
/// </code>
/// 三处要点容易抄错：
/// <list type="number">
/// <item><b>diameter 下界是 1 不是 0</b>，上界 2100（<c>BrushStroke.swift:147</c> 的 <c>maxDiameter</c>）。</item>
/// <item><b>opacity 下界是 0.01 不是 0</b> —— 完全透明的笔刷在 Mac 上是非法参数。</item>
/// <item>三者都要求 <c>isFinite</c>，NaN 与 ±∞ 一律拒绝。</item>
/// </list>
/// <para>
/// ⚠️ 这些校验在 Mac 上是<b>抛异常</b>，不是静默钳制。
/// 但 UI 滑块会把值钳在范围内，所以正常路径不会触发；
/// 本类保留<b>与 Mac 一致的抛异常</b>语义，不用静默钳制代替 —— 静默钳制会让
/// 「值被谁改错了」变成不可查的问题。
/// </para>
/// </remarks>
public sealed record BrushSettings
{
    /// <summary>直径上界，对应 <c>BrushStroke.swift:147</c> 的 <c>maxDiameter</c>。</summary>
    public const double MaxDiameter = 2100;

    /// <summary>不透明度下界，对应 <c>BrushStroke.swift:227</c> 的 <c>0.01</c>。</summary>
    public const double MinOpacity = 0.01;

    /// <summary>笔刷直径，文档像素单位。<b>范围 [1, 2100]</b>，默认 40（<c>BrushStroke.swift:10</c>）。</summary>
    public double Diameter { get; init; } = 40;

    /// <summary>硬度，<b>范围 [0, 1]</b>，默认 1（<c>BrushStroke.swift:11</c>）。</summary>
    /// <remarks>0 = 全程羽化，1 = 硬边。</remarks>
    public double Hardness { get; init; } = 1;

    /// <summary>不透明度，<b>范围 [0.01, 1]</b>，默认 1（<c>BrushStroke.swift:16</c>）。</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>输入平滑强度，默认 0（<c>BrushStroke.swift:19</c>）。</summary>
    public double Smoothing { get; init; }

    /// <summary>是否为橡皮（走 <c>destination-out</c> 合成，不是把 alpha 设 0）。</summary>
    /// <remarks>对应 <c>BrushStroke.swift:24</c> 的 <c>var erasing = false</c>。</remarks>
    public bool Erasing { get; init; }

    /// <summary>是否为污点修复笔。对应 <c>BrushStroke.swift:25</c> 的 <c>var healing = false</c>。</summary>
    public bool Healing { get; init; }

    /// <summary>笔刷颜色（直通色）。写入像素时走预乘路径（铁律 2）。</summary>
    /// <remarks>对应 <c>BrushStroke.swift:12-14</c> 的 <c>red/green/blue</c>（那里是 0..1 直通）。</remarks>
    public RgbaColor Color { get; init; } = new(0, 0, 0, 255);

    /// <summary>校验三个参数是否在 Mac 允许的范围内。</summary>
    /// <exception cref="ArgumentOutOfRangeException">任一参数越界或非有限。</exception>
    /// <remarks>
    /// 逐条对应 <c>BrushStroke.swift:225-227</c>。
    /// 抛 <see cref="ArgumentOutOfRangeException"/> 而非 Mac 的 <c>ProjectError.tooLarge</c>，
    /// 因为后者是 AI-2 的工程层类型，本项目不引用。
    /// </remarks>
    public void Validate()
    {
        if (!double.IsFinite(Diameter) || Diameter is < 1 or > MaxDiameter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Diameter), Diameter,
                $"笔刷直径必须是 [1, {MaxDiameter}] 内的有限值（BrushStroke.swift:147,225）。");
        }

        if (!double.IsFinite(Hardness) || Hardness is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Hardness), Hardness,
                "硬度必须是 [0, 1] 内的有限值（BrushStroke.swift:226）。");
        }

        if (!double.IsFinite(Opacity) || Opacity is < MinOpacity or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Opacity), Opacity,
                $"不透明度必须是 [{MinOpacity}, 1] 内的有限值（BrushStroke.swift:227）。");
        }
    }
}

/// <summary>
/// 笔刷衰减曲线与笔尖覆盖度。逐条照抄 <c>BrushStroke.swift</c>，未自行设计。
/// </summary>
public static class BrushFalloff
{
    /// <summary>
    /// 软笔刷在「硬度半径到边缘」之间的衰减：归一化高斯，在整个半径上淡出，到边缘归零。
    /// </summary>
    /// <param name="u">归一化半径，0 = 中心，1 = 边缘。</param>
    /// <returns>覆盖度，<c>[0, 1]</c>。</returns>
    /// <remarks>
    /// 逐字对应 <c>BrushStroke.swift:106-109</c>：
    /// <code>let k: CGFloat = 2.5; return max(0, (exp(-k*u*u) - exp(-k)) / (1 - exp(-k)))</code>
    /// <para>
    /// 🔴 <b>这里的常数 k = 2.5 是 Mac 侧写死的</b>，不是可调参数，改它会让
    /// <c>BrushTests.swift:150-153</c> 的 alpha 断言全部失效。
    /// </para>
    /// <para>
    /// 数学性质：<c>u=0</c> 时 <c>(1 - e^-k)/(1 - e^-k) = 1</c>（中心满覆盖）；
    /// <c>u=1</c> 时 <c>(e^-k - e^-k)/… = 0</c>（边缘归零）。单调递减。
    /// </para>
    /// </remarks>
    public static double Falloff(double u)
    {
        const double k = 2.5;
        return Math.Max(0, (Math.Exp(-k * u * u) - Math.Exp(-k)) / (1 - Math.Exp(-k)));
    }

    /// <summary>
    /// 计算某个点到笔尖中心的归一化距离所对应的覆盖度。
    /// </summary>
    /// <param name="distance">到笔尖中心的像素距离。</param>
    /// <param name="diameter">笔刷直径。</param>
    /// <param name="hardness">硬度 <c>[0,1]</c>，1 = 硬边。</param>
    /// <returns>覆盖度 <c>[0, 1]</c>，超出半径为 0。</returns>
    /// <remarks>
    /// 🔴 对应 <c>BrushStroke.swift:258-263</c> 的笔尖构造：
    /// <c>hardness &gt;= 1</c> 走<b>硬边</b>（半径内恒 1）；
    /// 否则以 <c>radius * hardness</c> 为内圈实心区，外圈按 <see cref="Falloff"/> 羽化。
    /// <para>
    /// <b>Mac 用 <c>drawRadialGradient</c> 画笔尖，本类用解析式直接算</b>：
    /// 两者数学等价（同一个高斯），但省掉一次离屏纹理。这是本项目
    /// 「不用 CoreGraphics」的有意取舍，见 <c>docs/compositor-spec.md</c>。
    /// </para>
    /// </remarks>
    public static double Coverage(double distance, double diameter, double hardness)
    {
        if (diameter <= 0)
        {
            return 0;
        }

        double radius = diameter / 2.0;
        if (distance >= radius)
        {
            return 0;
        }

        // hardness >= 1：硬边笔刷，半径内恒为满覆盖。对应 :258 的分支。
        if (hardness >= 1)
        {
            return 1;
        }

        double inner = radius * hardness;
        if (distance <= inner)
        {
            return 1;
        }

        // 外圈：把 [inner, radius] 映射到 [0,1] 再走高斯。
        double u = (distance - inner) / (radius - inner);
        return Falloff(u);
    }
}
