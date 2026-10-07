namespace Compositor.Core;

/// <summary>色彩空间标记。</summary>
/// <remarks>
/// 🔴 <b>本枚举不参与 <c>.comp</c> 的混合模式序列化</b>（那是 <see cref="BlendModeStrings"/> 的事），
/// 也与 <see cref="ProjectSnapshot.ColorSpace"/> 那个字符串字段不是一回事。
/// <para><b>当前判定：<see cref="Srgb"/>。</b> 判定依据见 <c>docs/color-space.md</c>：
/// 原项目 <c>SeparableBlend.swift:14-17</c> 的源码注释明确写
/// <i>"The blend has to happen in the same sRGB the canvas is in"</i>，
/// 并用 <c>.workingColorSpace: CGColorSpace.sRGB</c> 覆盖了 Core Image 的线性默认值。
/// 原项目 release notes 里修的 bug 正是"在线性空间混合"。</para>
/// <para><b>为什么保留 <see cref="LinearSrgb"/>：</b>枚举值顺序不得改动
/// （<c>LayerAppearance.swift:4</c> 的 <c>CaseIterable</c> 依赖它），
/// 且若后续 Tier 2 实测推翻结论，改的是混合模式内部的开关，不动枚举值。</para>
/// </remarks>
public enum ColorSpace
{
    /// <summary>编码 sRGB（非线性）。<b>当前生效值。</b></summary>
    Srgb = 0,

    /// <summary>扩展线性 sRGB。<b>当前未使用</b>，仅保留枚举位以防实测改判。</summary>
    LinearSrgb = 1,
}

/// <summary>sRGB ↔ 扩展线性 sRGB 的传递函数。</summary>
/// <remarks>
/// 🔴 <b>常数与分支条件逐字取自原项目 <c>Rendering/AdjustPixels.c:204-214</c>，
/// 不是教科书标准值。</b> 两处刻意的偏差，抄错会与 Mac 版差一点点：
/// <list type="number">
/// <item><c>srgb_to_linear</c> 的拐点用 <c>0.04045</c>（IEC 61966-2-1 的标准值是 0.040448236…）。</item>
/// <item><c>linear_to_srgb</c> 的拐点用 <c>0.0031308</c>（标准值是 0.003130668…）。</item>
/// </list>
/// 这两个值是原项目写死在 C 源码里的，Mac 版就是按它们算的。<b>不要"修正"成标准常数。</b>
/// <para><b>本类型目前无人调用。</b> 它是给波次 2 手写混合模式预留的转换入口；
/// 像素管线的 16 个调整函数已各自直译，不走这里（见 <c>docs/color-space.md</c> §二）。</para>
/// </remarks>
public static class ColorConvert
{
    // 逐字取自 AdjustPixels.c:204-207
    private const double LinearThreshold = 0.04045;
    private const double LinearDivisor = 12.92;
    private const double Gamma = 2.4;

    // 逐字取自 AdjustPixels.c:209-214
    private const double EncodedThreshold = 0.0031308;

    /// <summary>sRGB 编码值 → 线性光。</summary>
    /// <param name="v">编码值。⚠ 本函数<b>不做上限钳制</b>，与 C 的 <c>srgb_to_linear</c> 一致；传入 &gt; 1 会得到 &gt; 1 的结果。</param>
    /// <returns>线性光值。</returns>
    public static float SrgbToLinear(float v)
    {
        // C 侧是 double + pow()。这里也走 double，最后再收窄到 float，
        // 避免 MathF.Pow 的单精度误差在反复调用中累积。
        double encoded = v;
        double result = encoded <= LinearThreshold
            ? encoded / LinearDivisor
            : Math.Pow((encoded + 0.055) / 1.055, Gamma);
        return (float)result;
    }

    /// <summary>线性光 → sRGB 编码值。</summary>
    /// <param name="v">线性光值。负数按 0 处理，≥ 1 按 1 处理（与 C 的前置钳制一致）。</param>
    /// <returns>编码值，落在 0..1。</returns>
    public static float LinearToSrgb(float v)
    {
        double linear = v;
        if (linear <= 0)
        {
            return 0f;
        }

        if (linear >= 1)
        {
            return 1f;
        }

        double result = linear <= EncodedThreshold
            ? linear * LinearDivisor
            : 1.055 * Math.Pow(linear, 1.0 / Gamma) - 0.055;
        return (float)result;
    }
}
