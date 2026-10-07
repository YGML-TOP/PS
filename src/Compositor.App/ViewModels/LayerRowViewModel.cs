using Avalonia.Media;
using Compositor.Core;

namespace Compositor.App.ViewModels;

/// <summary>图层面板里的一行。由契约层的 <see cref="LayerNode"/> 投影而来。</summary>
/// <remarks>
/// 这一层存在的意义是把 <b>契约类型</b>与 <b>界面字段</b>隔开：
/// 契约里叫 <c>Opacity</c>、界面上要显示 <c>OpacityText</c>；
/// 契约里没有缩略图，界面上要有一块——这些都放在投影层，
/// 合成器落地后也只需要改这里，不必去改契约。
/// </remarks>
public sealed class LayerRowViewModel
{
    /// <summary>由契约层的图层节点构造一行。</summary>
    /// <param name="node">图层节点。<paramref name="node.Transform"/> 必填，由契约保证。</param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> 为 <see langword="null"/>。</exception>
    public LayerRowViewModel(LayerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Id = node.Id;
        Name = node.Name;
        IsVisible = node.IsVisible;
        Opacity = node.Opacity;
        BlendMode = node.BlendMode;
        BlendModeLiteral = BlendModeStrings.ToLiteral(node.BlendMode);
        ThumbnailBrush = BuildThumbnailBrush(node);
    }

    /// <summary>图层 id。</summary>
    public Guid Id { get; }

    /// <summary>图层名。</summary>
    public string Name { get; }

    /// <summary>是否可见。</summary>
    public bool IsVisible { get; }

    /// <summary>不透明度，0–1。</summary>
    public double Opacity { get; }

    /// <summary>显示用的小数形式不透明度，例如 <c>85%</c>。</summary>
    public string OpacityText => (Opacity * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>混合模式枚举值。</summary>
    public BlendMode BlendMode { get; }

    /// <summary>混合模式的 <c>.comp</c> 字面量。</summary>
    public string BlendModeLiteral { get; }

    /// <summary>
    /// 缩略图色块。真正的像素缩略图要等合成器就位后由缓冲生成，
    /// 现在按图层 id 派生一个稳定颜色，好让列表在视觉上可区分。
    /// </summary>
    public IBrush ThumbnailBrush { get; }

    /// <summary>由图层 id 派生稳定色相的缩略图色块。</summary>
    private static IBrush BuildThumbnailBrush(LayerNode node)
    {
        // 取 id 的前 4 字节当色相种子：同一个 id 永远得到同一个颜色，刷新后不会跳色。
        Span<byte> idBytes = stackalloc byte[16];
        if (!node.Id.TryWriteBytes(idBytes))
        {
            return new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        }

        uint seed = ((uint)idBytes[0] << 24) | ((uint)idBytes[1] << 16) | ((uint)idBytes[2] << 8) | idBytes[3];
        return new SolidColorBrush(HsvToColor(seed % 360u, 0.55, 0.85));
    }

    /// <summary>HSV → RGB。</summary>
    /// <remarks>
    /// 自己实现而不用 Avalonia 的辅助函数：<c>Color.FromHsv</c> 在 Avalonia 12 上不存在，
    /// 而为了一行取色去引 System.Drawing / SkiaSharp 都不划算（后者还会碰上 NU1605）。
    /// </remarks>
    private static Color HsvToColor(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double h = hue / 60.0;
        double x = chroma * (1.0 - Math.Abs((h % 2.0) - 1.0));
        double m = value - chroma;

        (double r, double g, double b) = h switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}