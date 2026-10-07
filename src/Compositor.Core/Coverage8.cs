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
    /// 🔴 <b>v1.2 由 internal 提升为 public。</b>v1.1 时刻意保持 internal，
    /// 理由是「外部一律走 <see cref="SelectionMask.CoverageAt"/>，不要自己读 <see cref="Data"/>」——
    /// 但契约 v1.2 查明 <b>AI-4 查不到它</b>，拿不到就没法区分两种表示。
    /// 契约公开面因此扩大了这一项。
    /// <para><b>⚠️ 公开之后这条建议依然成立，且更重要了</b>：
    /// <see cref="Data"/> 对 1×1 代理<b>只有 1 个字节</b>，直接索引 &gt; 0 就是越界。
    /// 拿到本属性后正确用法是：<c>IsUniform == true</c> 时读 <c>Data[0]</c>，
    /// 否则才按 <c>iy * Width + ix</c> 索引 —— 而
    /// <see cref="SelectionMask.CoverageAt"/> 已经把这两条分支都封好了，
    /// <b>消费方优先走它</b>。</para>
    /// </remarks>
    public bool IsUniform { get; }

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

    /// <summary>
    /// 🔴 <b>契约 v1.2 新增。</b>从调用方提供的字节数组导入覆盖度数据。
    /// </summary>
    /// <param name="data">
    /// 行优先的单通道灰度数据，长度必须<b>恰好等于</b> <c>width * height</c>，无对齐填充。
    /// 0 = 完全隐藏，255 = 完全显示。
    /// </param>
    /// <param name="width">宽度像素，&gt; 0。</param>
    /// <param name="height">高度像素，&gt; 0。</param>
    /// <returns>覆盖度缓冲，内容为 <paramref name="data"/> 的一份拷贝。</returns>
    /// <exception cref="ArgumentOutOfRangeException">宽或高不为正，或 <c>width * height</c> 溢出 <see cref="int"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> 的长度不等于 <c>width * height</c>。</exception>
    /// <remarks>
    /// <para><b>形参名照契约 v1.2 逐字取 <c>width</c> / <c>height</c></b>，
    /// 不沿用 <see cref="CreateFilled"/> 的 <c>w</c> / <c>h</c>：
    /// 形参名是<b>命名实参的调用契约</b>，<c>FromData(bytes, width: 4, height: 3)</c> 必须能编译。</para>
    ///
    /// <para><b>为什么必须拷贝，不能持有 <paramref name="data"/> 的引用。</b>
    /// <see cref="ReadOnlySpan{T}"/> 是<b>栈视图</b>，它的生存期不跨方法边界 ——
    /// 出了调用它的那个栈帧，底层内存就可能被复用或释放，而本对象仍持有指向它的视图。
    /// 那就是<b>悬垂引用</b>：不崩，但读到的是随机字节，而且崩溃点与真正的出错点相隔很远，
    /// 几乎无法排查。所以这里 <c>data.ToArray()</c> 一次性拷进本对象自己的数组。
    /// 代价是一次 O(w*h) 拷贝，换来的是一个拥有独立生命周期的值对象 ——
    /// 覆盖度缓冲本来就要在选区整个生命周期内常驻，这次拷贝是必要成本，不是浪费。</para>
    ///
    /// <para><b>为什么 <see cref="IsUniform"/> 恒为 <see langword="false"/>。</b>
    /// 即便调用方传进来的是 1×1，也按全分辨率缓冲处理。理由是
    /// 「调用方显式给了数据」本身就是「这不是代理」的信号；
    /// 而 <see cref="Uniform"/> 才是那个专门为省内存存在的代理形态。
    /// 走非均匀分支时 <see cref="SelectionMask.CoverageAt"/> 的索引逻辑对 1×1 同样正确
    /// （<c>ix &lt; Coverage.Width</c> 在 Width = 1 时会把越界坐标挡掉），所以没有正确性风险。</para>
    ///
    /// <para><b>长度必须恰好相等，不接受「至少」。</b>接受「至少」会让多余字节被静默忽略，
    /// 而调用方几乎总是因为算错了行距或忘了乘通道数才多给 —— 那时候应该炸，不该悄悄少读一段。</para>
    /// </remarks>
    public static Coverage8 FromData(ReadOnlySpan<byte> data, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正。");
        }

        long len = (long)width * height;
        if (len > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"尺寸过大：{width}×{height} 需要 {len} 字节，超过 int 上限。");
        }

        if (data.Length != len)
        {
            throw new ArgumentException(
                $"数据长度 {data.Length} 与尺寸 {width}×{height} 所需的 {len} 不符。", nameof(data));
        }

        // 拷贝而非持有 span：span 是栈视图，出了调用者的栈帧就是悬垂引用。
        return new Coverage8(width, height, data.ToArray(), isUniform: false);
    }
}
