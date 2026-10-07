namespace Compositor.Core;

/// <summary>
/// 重采样档位。🔴 v1.1 修正：真实是<b>字符串</b>枚举，只有 3 档
/// （<c>Document/LayerTransform.swift:4-7</c>），映射到 CGInterpolationQuality none/low/high。
/// 契约原先写的 {Low,Medium,High} 整数枚举、以及"四档 nearest/bilinear/bicubic/Lanczos"
/// 都与 Mac 版不符——后者在 Mac 版根本不存在。
/// </summary>
public enum SamplingQuality
{
    /// <summary>最近邻。<c>.comp</c> 字面量 <c>"Nearest"</c>，对应 CGInterpolationQuality.none。</summary>
    Nearest = 0,

    /// <summary>双线性。<c>.comp</c> 字面量 <c>"Smooth"</c>，对应 CGInterpolationQuality.low。</summary>
    Smooth,

    /// <summary>高质量（三次卷积）。<c>.comp</c> 字面量 <c>"High quality"</c>，对应 CGInterpolationQuality.high。</summary>
    HighQuality,
}

/// <summary>
/// 采样档位 ↔ <c>.comp</c> 字面量。逐字取自 <c>LayerTransform.swift:4-7</c>。
/// </summary>
/// <remarks>
/// ⚠️ <b>"High quality" 中间有空格</b>，且首字母大写。写成 <c>"HighQuality"</c> 或 <c>"high_quality"</c>
/// 会让 Mac 版整个图层的采样设置失效。<b>禁止在别处硬编码这三个字符串。</b>
/// </remarks>
public static class SamplingStrings
{
    /// <summary>字面量表，索引与 <see cref="SamplingQuality"/> 的枚举值一一对应。</summary>
    private static readonly string[] Literal = { "Nearest", "Smooth", "High quality" };

    /// <summary>转为 <c>.comp</c> 字面量。</summary>
    /// <param name="q">采样档位。</param>
    /// <returns>写入 manifest 的字符串。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> 不是已定义的枚举值。</exception>
    public static string ToLiteral(SamplingQuality q)
    {
        int i = (int)q;
        if (i < 0 || i >= Literal.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(q), q, "未定义的采样档位。");
        }

        return Literal[i];
    }

    /// <summary>从 <c>.comp</c> 字面量解析。</summary>
    /// <param name="literal">manifest 中的字符串。</param>
    /// <returns>对应的采样档位。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="FormatException">
    /// 字面量不在这三个里。<b>大小写敏感</b>，与 Swift 的 <c>enum: String</c> Codable 行为一致。
    /// </exception>
    public static SamplingQuality Parse(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        for (int i = 0; i < Literal.Length; i++)
        {
            if (string.Equals(Literal[i], literal, StringComparison.Ordinal))
            {
                return (SamplingQuality)i;
            }
        }

        throw new FormatException(
            $"未知的采样字面量 '{literal}'。合法值：{string.Join(" | ", Literal)}（区分大小写）。");
    }
}

/// <summary>
/// 图层变换。不变量：Origin/Size/Rotation 均有限；|Origin| ≤ 1_000_000。
/// 与 <c>.comp</c> manifest 的字段一一对应，序列化时不得改名。
/// </summary>
public sealed record LayerTransform
{
    /// <summary>图层矩形<b>左上角</b>（不是中心！）。铁律 1：Y 向下。</summary>
    public DocPoint Origin { get; init; }

    /// <summary>图层矩形尺寸，单位为文档像素。</summary>
    public DocSize Size { get; init; }

    /// <summary>旋转角度，单位为度，<b>顺时针为正</b>。</summary>
    public double RotationDegrees { get; init; }

    /// <summary>水平翻转。</summary>
    public bool FlipX { get; init; }

    /// <summary>垂直翻转。</summary>
    public bool FlipY { get; init; }

    /// <summary>重采样档位，默认最高质量，与 Mac 版新建图层一致。</summary>
    public SamplingQuality Sampling { get; init; } = SamplingQuality.HighQuality;

    /// <summary>图层矩形中心点，公式与 <c>LayerTransform.swift</c> 一致。</summary>
    public DocPoint Center => new(Origin.X + Size.Width / 2.0, Origin.Y + Size.Height / 2.0);

    /// <summary>旋转弧度 = 角度归一化到 ±360 后转弧度。</summary>
    /// <remarks>
    /// 公式逐字取自契约：<c>RotationDegrees % 360 * Math.PI / 180.0</c>。
    /// <c>%</c> 是 C# 的余数运算符，负角度得到负余数（<c>-90 % 360 == -90</c>），与契约一致。
    /// </remarks>
    public double Radians => RotationDegrees % 360 * Math.PI / 180.0;

    /// <summary>变换是否可用于渲染：各分量有限，且原点未超出 ±1,000,000 的安全范围。</summary>
    /// <remarks>
    /// 为什么原点要卡在 1e6：<see cref="LayerTransform.Center"/> 会把 Origin 与 Size 相加，
    /// 没有上限时浮点会在 1e15 量级彻底丢失精度，表现为图层"跳到很远的地方"。
    /// </remarks>
    public bool IsValid =>
        double.IsFinite(Origin.X) && double.IsFinite(Origin.Y)
        && double.IsFinite(Size.Width) && double.IsFinite(Size.Height)
        && double.IsFinite(RotationDegrees)
        && Math.Abs(Origin.X) <= 1_000_000 && Math.Abs(Origin.Y) <= 1_000_000;
}
