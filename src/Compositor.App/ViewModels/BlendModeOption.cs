using Compositor.Core;

namespace Compositor.App.ViewModels;

/// <summary>
/// 混合模式下拉框里的一项。显示文本用的是 <b>.comp 字面量</b>，不是枚举名。
/// </summary>
/// <remarks>
/// 🔴 为什么显示字面量而不是枚举名：<see cref="BlendMode"/> 是整数枚举，
/// 而 <c>.comp</c> 里存的是 <see cref="BlendModeStrings"/> 的字符串。
/// 界面上直接显示字面量，可以保证"用户看到的"与"存进文件的"是同一个东西，
/// 少一次翻译就少一处可能对不上的地方（枚举名与字面量并不总是一一对应，
/// 例如 <c>LinearDodge</c> 的字面量是 <c>"Linear Dodge (Add)"</c>）。
/// </remarks>
public sealed class BlendModeOption
{
    /// <summary>构造一项下拉条目。</summary>
    /// <param name="mode">混合模式。</param>
    /// <param name="literal">对应的 <c>.comp</c> 字面量。</param>
    public BlendModeOption(BlendMode mode, string literal)
    {
        Mode = mode;
        Literal = literal;
    }

    /// <summary>混合模式枚举值。</summary>
    public BlendMode Mode { get; }

    /// <summary><c>.comp</c> 里的字面量，例如 <c>"Linear Dodge (Add)"</c>。</summary>
    public string Literal { get; }

    /// <summary>下拉框显示的文本，直接用 <see cref="Literal"/>。</summary>
    public string Display => Literal;

    /// <inheritdoc />
    public override string ToString() => Literal;
}