namespace Compositor.Core;

/// <summary>混合模式。⚠️ 必须经 <see cref="BlendModeStrings"/> 转成字符串后才能写进 <c>.comp</c>。
/// 枚举值本身不参与序列化。</summary>
/// <remarks>
/// 🔴 v1.1 修正（2026-10-07，经 AI-1 指控 + 项目负责人核实源码确认）
/// 原契约写成"整数枚举、顺序即序列化值"是<b>错的</b>，会让 <c>.comp</c> 完全互通不了。
/// 真实情况（<c>Document/LayerAppearance.swift:4-13</c>）：
/// <code>
/// enum LayerBlendMode: String, Codable, CaseIterable  —— 是字符串枚举，共 24 个
/// 字面量带空格与括号，如 colorBurn = "Color Burn"、linearDodge = "Linear Dodge (Add)"
/// 没有 DarkerColor / LighterColor（源码注释明写"故意不做，两个框架都不实现"）
/// </code>
/// → 序列化必须写字符串。整数序列化会让 Mac 版读出来的图层<b>全部落到 Normal</b>。
/// <para><b>成员顺序</b>与 <see cref="BlendModeStrings"/> 的字面量表一一对应（索引即枚举值），
/// 改动其一必须同步改另一个。</b></para>
/// </remarks>
public enum BlendMode
{
    /// <summary>正常。</summary>
    Normal,

    /// <summary>变暗。</summary>
    Darken,

    /// <summary>正片叠底。</summary>
    Multiply,

    /// <summary>颜色加深。</summary>
    ColorBurn,

    /// <summary>线性加深。</summary>
    LinearBurn,

    /// <summary>变亮。</summary>
    Lighten,

    /// <summary>滤色。</summary>
    Screen,

    /// <summary>颜色减淡。</summary>
    ColorDodge,

    /// <summary>线性减淡（添加）。</summary>
    LinearDodge,

    /// <summary>叠加。</summary>
    Overlay,

    /// <summary>柔光。</summary>
    SoftLight,

    /// <summary>强光。</summary>
    HardLight,

    /// <summary>亮光。</summary>
    VividLight,

    /// <summary>线性光。</summary>
    LinearLight,

    /// <summary>点光。</summary>
    PinLight,

    /// <summary>实色混合。</summary>
    HardMix,

    /// <summary>差值。</summary>
    Difference,

    /// <summary>排除。</summary>
    Exclusion,

    /// <summary>减去。</summary>
    Subtract,

    /// <summary>划分。</summary>
    Divide,

    /// <summary>色相。</summary>
    Hue,

    /// <summary>饱和度。</summary>
    Saturation,

    /// <summary>颜色。</summary>
    Color,

    /// <summary>明度。</summary>
    Luminosity,
}

/// <summary>混合模式 ↔ <c>.comp</c> 字面量的<b>唯一</b>映射。禁止在别处硬编码这些字符串。</summary>
public static class BlendModeStrings
{
    /// <summary>
    /// 字面量表，索引与 <see cref="BlendMode"/> 的枚举值一一对应。
    /// 逐字取自 <c>LayerAppearance.swift:4-13</c>，顺序即 Photoshop 菜单分组顺序。
    /// </summary>
    private static readonly string[] Literal =
    {
        "Normal", "Darken", "Multiply", "Color Burn", "Linear Burn",
        "Lighten", "Screen", "Color Dodge", "Linear Dodge (Add)",
        "Overlay", "Soft Light", "Hard Light", "Vivid Light", "Linear Light",
        "Pin Light", "Hard Mix",
        "Difference", "Exclusion", "Subtract", "Divide",
        "Hue", "Saturation", "Color", "Luminosity",
    };

    /// <summary>转为 <c>.comp</c> 字面量。</summary>
    /// <param name="m">混合模式。</param>
    /// <returns>写入 manifest 的字符串。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="m"/> 不是已定义的枚举值。</exception>
    public static string ToLiteral(BlendMode m)
    {
        int i = (int)m;
        if (i < 0 || i >= Literal.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(m), m, "未定义的混合模式。");
        }

        return Literal[i];
    }

    /// <summary>从 <c>.comp</c> 字面量解析。</summary>
    /// <param name="literal">manifest 中的字符串。</param>
    /// <returns>对应的混合模式。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="FormatException">
    /// 字面量不在这 24 个里。<b>大小写敏感</b>，与 Swift 的 <c>enum: String</c> Codable 行为一致。
    /// </exception>
    public static BlendMode Parse(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        for (int i = 0; i < Literal.Length; i++)
        {
            if (string.Equals(Literal[i], literal, StringComparison.Ordinal))
            {
                return (BlendMode)i;
            }
        }

        throw new FormatException(
            $"未知的混合模式字面量 '{literal}'。合法值共 {Literal.Length} 个，区分大小写。");
    }
}

/// <summary>Photoshop 菜单的分组，顺序与原项目一致（<c>LayerAppearance.swift:17-24</c>）。
/// UI 层画分隔线用。</summary>
/// <remarks>
/// 逐字照抄 <c>LayerAppearance.swift:17-24</c> 的 <c>groups</c>：
/// <code>
/// [ [.normal],
///   [.darken, .multiply, .colorBurn, .linearBurn],
///   [.lighten, .screen, .colorDodge, .linearDodge],
///   [.overlay, .softLight, .hardLight, .vividLight, .linearLight, .pinLight, .hardMix],
///   [.difference, .exclusion, .subtract, .divide],
///   [.hue, .saturation, .color, .luminosity] ]
/// </code>
/// 原注释：<i>"Photoshop's grouping: darkening modes together, then lightening, then contrast,
/// then the comparative ones, then the component modes. The menu draws a line between each group."</i>
/// </remarks>
public static class BlendModeGroups
{
    /// <summary>按 Photoshop 菜单顺序排列的分组。每组对应菜单里的一条分隔线，共 6 组。</summary>
    /// <remarks>
    /// 🔴 <b>每次访问都会新建一份分组与内层数组。</b> 原因是 <c>IReadOnlyList&lt;BlendMode[]&gt;</c>
    /// 只挡住了<b>外层</b>的改写，挡不住内层 ——
    /// <c>BlendModeGroups.InPhotoshopOrder[0][0] = BlendMode.Multiply;</c> 在类型上是<b>能编译通过</b>的。
    /// 若返回缓存的同一份结构，任何一个调用方的一次误写就会污染全进程的菜单分组。
    /// 代价是每次访问分配 6 个小数组；菜单只在打开时构建，这个代价可以忽略。
    /// </remarks>
    public static IReadOnlyList<BlendMode[]> InPhotoshopOrder => BuildGroups();

    /// <summary>构建一份全新的分组结构（6 组、24 个模式，不重不漏）。</summary>
    private static IReadOnlyList<BlendMode[]> BuildGroups() => new[]
    {
        new[] { BlendMode.Normal },
        new[]
        {
            BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn,
        },
        new[]
        {
            BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodge,
        },
        new[]
        {
            BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight,
            BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix,
        },
        new[]
        {
            BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide,
        },
        new[]
        {
            BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity,
        },
    };
}
