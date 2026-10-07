using System.Text.Json;

namespace Compositor.Adjust;

/// <summary>
/// 调整层种类。共 12 种。
/// </summary>
/// <remarks>
/// <para>移植自 <c>Document/LayerAdjustment.swift:4-9</c> 的
/// <c>enum AdjustmentKind: String, Codable, CaseIterable, Sendable</c>。</para>
/// <para>🔴 <b>枚举成员名与 manifest 字面量不同名，这是刻意的。</b>
/// C# 标识符不允许 <c>/</c> 与 <c>&amp;</c>，而 manifest 里的字面量带这两个字符：
/// <c>.hsv → "Hue/Saturation"</c>、<c>.blackWhite → "Black &amp; White"</c>。
/// 二者由 <see cref="AdjustmentKindStrings"/> 桥接，序列化必须经它转换。</para>
/// <para><b>写错一个字面量的后果</b>：Mac 版 <c>Codable</c> 解不出该 kind，
/// 调整层被当作未知对象丢弃或整层失效，且<b>不产生任何错误</b>——工程能打开，
/// 调整层静默失效。这条是 .comp 互通的生死线。</para>
/// </remarks>
public enum AdjustmentKind
{
    /// <summary>色相/饱和度/明度。manifest 字面量 <c>"Hue/Saturation"</c>（<b>带斜杠</b>）。</summary>
    HueSaturation,

    /// <summary>色阶。</summary>
    Levels,

    /// <summary>曲线。</summary>
    Curves,

    /// <summary>曝光。</summary>
    Exposure,

    /// <summary>渐变映射。</summary>
    GradientMap,

    /// <summary>颗粒。</summary>
    Grain,

    /// <summary>添加噪点。</summary>
    AddNoise,

    /// <summary>高斯模糊。</summary>
    GaussianBlur,

    /// <summary>动感模糊。</summary>
    MotionBlur,

    /// <summary>反相。<b>无任何参数</b>（Swift <c>isEditable == false</c>）。</summary>
    Invert,

    /// <summary>黑白。manifest 字面量 <c>"Black &amp; White"</c>（<b>带 &amp; 号</b>）。</summary>
    BlackWhite,

    /// <summary>色彩平衡。</summary>
    ColorBalance,
}

/// <summary>
/// 调整层种类 ↔ <c>.comp</c> manifest 字面量。逐字取自 <c>LayerAdjustment.swift:4-9</c>，
/// 并经 <c>docs/project-format.md:29,33</c> 独立确认。
/// </summary>
/// <remarks>
/// 与契约层 <c>BlendModeStrings</c> / <c>SamplingStrings</c> 同一个模式：
/// 枚举值本身不参与序列化，必须经本类转成字符串。
/// </remarks>
public static class AdjustmentKindStrings
{
    /// <summary>数组下标即 <see cref="AdjustmentKind"/> 的数值，顺序不可变动。</summary>
    private static readonly string[] Literal =
    [
        "Hue/Saturation",   // HueSaturation —— 斜杠
        "Levels",
        "Curves",
        "Exposure",
        "Gradient Map",
        "Grain",
        "Add Noise",
        "Gaussian Blur",
        "Motion Blur",
        "Invert",
        "Black & White",    // BlackWhite —— & 号
        "Color Balance",
    ];

    /// <summary>全部字面量，按 <see cref="Literal"/> 顺序。</summary>
    public static IReadOnlyList<string> AllLiterals => Literal;

    /// <summary>转成 manifest 字面量。</summary>
    /// <param name="kind">调整层种类。</param>
    /// <returns>写入 <c>.comp</c> 的字符串。</returns>
    /// <exception cref="ArgumentOutOfRangeException">枚举值不在 12 种之内。</exception>
    public static string ToLiteral(AdjustmentKind kind)
    {
        int i = (int)kind;
        if ((uint)i >= (uint)Literal.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, $"未定义的调整层种类（数值 {i}）。");
        }
        return Literal[i];
    }

    /// <summary>从 manifest 字面量解析。</summary>
    /// <param name="literal">工程文件里的原始字符串。</param>
    /// <returns>对应的调整层种类。</returns>
    /// <exception cref="JsonException">
    /// 字面量不在 12 种之内。<b>此处绝不静默回退到任何默认值</b>——
    /// 静默回退会让用户的调整层无声失效，且不留任何痕迹。
    /// </exception>
    public static AdjustmentKind Parse(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        for (int i = 0; i < Literal.Length; i++)
        {
            if (string.Equals(literal, Literal[i], StringComparison.Ordinal))
            {
                return (AdjustmentKind)i;
            }
        }
        throw new JsonException(
            $"未知的调整层字面量 \"{literal}\"。合法值：{string.Join(" / ", Literal)}。");
    }
}
