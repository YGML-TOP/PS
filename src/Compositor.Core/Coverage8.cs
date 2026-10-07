namespace Compositor.Core;

/// <summary>8-bit 覆盖度（选区与栅格蒙版）。不变量：与 alpha 无关，白=完全覆盖。</summary>
/// <remarks>
/// 🔴 铁律 2：<b>蒙版不含 alpha</b>。覆盖度是单通道灰度，0 = 完全隐藏，255 = 完全显示。
/// 它描述的是"这个位置参与计算的比重"，不是"这个位置有多不透明"。
/// 把 RGBA 的 alpha 通道当成蒙版用，会在两次相乘后把图层边缘压成全黑。
/// <para><b>两种表示</b>：全分辨率缓冲（<see cref="CreateFilled"/>）与 1×1 均匀代理
/// （<see cref="Uniform"/>）。后者是为了避免"只是想知道一个均匀值"就先吃整幅内存。
/// 消费方必须用 <see cref="SelectionMask.CoverageAt"/> 取值，由它负责把 1×1 展开成任意坐标。</para>
/// </remarks>
public sealed class Coverage8
{
    private readonly byte[] _data;

    private Coverage8(int width, int height, byte[] data, bool isUniform)
    {
        Width = width;
        Height = height;
        _data = data;
        IsUniform = isUniform;
    }

    /// <summary>宽度像素。恒 &gt; 0。</summary>
    public int Width { get; }

    /// <summary>高度像素。恒 &gt; 0。</summary>
    public int Height { get; }

    /// <summary>底层字节的只读视图，长度为 <c>Width * Height</c>，行优先，无对齐填充。</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>
    /// 是否为 1×1 均匀代理。
    /// </summary>
    /// <remarks>
    /// <b>刻意不设为 public。</b> 契约附录 A 没有这一项，公开面必须与 v1.1 逐字一致。
    /// 只在本程序集内给 <see cref="SelectionMask.CoverageAt"/> 判断要不要展开。
    /// 外部消费方一律走 <see cref="SelectionMask.CoverageAt"/>，不要自己读 <see cref="Data"/>。
    /// </remarks>
    internal bool IsUniform { get; }

    /// <summary>创建全分辨率覆盖度缓冲，全部填 <paramref name="value"/>。</summary>
    /// <param name="w">宽度像素，&gt; 0。</param>
    /// <param name="h">高度像素，&gt; 0。</param>
    /// <param name="value">填充值，0–255。</param>
    /// <returns>覆盖度缓冲。</returns>
    /// <exception cref="ArgumentOutOfRangeException">宽或高不为正，或总字节数溢出 <see cref="int"/>。</exception>
    public static Coverage8 CreateFilled(int w, int h, byte value)
    {
        if (w <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(w), w, "宽度必须为正。");
        }

        if (h <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(h), h, "高度必须为正。");
        }

        long len = (long)w * h;
        if (len > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(w), $"尺寸过大：{w}×{h} 需要 {len} 字节，超过 int 上限。");
        }

        var data = new byte[len];
        if (value != 0)
        {
            Array.Fill(data, value);
        }

        return new Coverage8(w, h, data, isUniform: false);
    }

    /// <summary>1×1 的均匀覆盖度，避免在全分辨率分配前吃内存。</summary>
    /// <param name="value">均匀值，0–255。</param>
    /// <returns>1×1 的覆盖度；经 <see cref="SelectionMask.CoverageAt"/> 取任意坐标都返回 <paramref name="value"/>。</returns>
    /// <remarks>
    /// <b>不要直接索引 <see cref="Data"/>。</b> 它只有 1 个字节，索引 &gt; 0 就是越界。
    /// 唯一正确的用法是交给 <see cref="SelectionMask.CoverageAt"/>。
    /// </remarks>
    public static Coverage8 Uniform(int value)
    {
        byte v = (byte)Math.Clamp(value, 0, 255);
        return new Coverage8(1, 1, new[] { v }, isUniform: true);
    }
}
