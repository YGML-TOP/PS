namespace Compositor.Core;

/// <summary>直通（straight / non-premultiplied）颜色。仅在颜色交换边界使用。</summary>
/// <param name="R">红通道，0–255。</param>
/// <param name="G">绿通道，0–255。</param>
/// <param name="B">蓝通道，0–255。</param>
/// <param name="A">alpha，0–255。0 = 全透明，255 = 不透明。</param>
/// <remarks>
/// 🔴 <b>本类型表示直通色，不能直接写进 <see cref="PixelBuffer"/> 的存储。</b>
/// 必须经 <see cref="PixelBuffer.Fill"/> 或 <see cref="PixelBuffer.FromStraightRgba"/>，
/// 那两处会做预乘。直接把 <c>R</c> 塞进预乘缓冲会让半透明像素的颜色偏暗。
/// </remarks>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A);

/// <summary>
/// 预乘 RGBA8 像素缓冲（铁律 2）。内存布局：行优先，stride = Width * 4。
/// 不提供任何绕过预乘的入口。
/// </summary>
/// <remarks>
/// <para><b>铁律 2 的强制点。</b> 内部合成器、图层缓冲、蒙版合成全程预乘 RGBA8。
/// 本类只暴露两种进出方式：<see cref="FromStraightRgba"/>（进，立即预乘）与
/// <see cref="ToStraightRgba"/>（出，写 PNG 前反预乘）。
/// <see cref="Raw"/> 是唯一的可变入口，给直译代码与波次 2 的合成器用，
/// 调用方自行保证写入的是预乘值。</para>
///
/// <para><b>为什么预乘这么重要</b>：直通色在半透明处做混合会算错，
/// 两个模块一个预乘一个直通，合并后图层边缘发黑发白，
/// 而单元测试通常抓不住 —— 这是全项目最隐蔽的返工来源。</para>
///
/// <para><b>行距</b>恒为 <c>Width * 4</c>，无对齐填充。所以 <see cref="Rgba"/> 是一块连续内存，
/// 下标算法就是 <c>y * Width * 4 + x * 4</c>。</para>
/// </remarks>
public sealed class PixelBuffer
{
    private readonly byte[] _data;

    private PixelBuffer(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        _data = data;
    }

    /// <summary>宽度，单位像素。恒 &gt; 0。</summary>
    public int Width { get; }

    /// <summary>高度，单位像素。恒 &gt; 0。</summary>
    public int Height { get; }

    /// <summary>行距 = <c>Width * 4</c> 字节。无对齐填充。</summary>
    public int Stride => Width * 4;

    /// <summary>底层字节的可变视图。<b>写入者必须自行保证是预乘值。</b></summary>
    public Span<byte> Raw => _data;

    /// <summary>底层字节的只读视图（内容同样是预乘 RGBA8）。</summary>
    public ReadOnlySpan<byte> Rgba => _data;

    /// <summary>创建全透明缓冲。不得传入非正尺寸。</summary>
    /// <param name="width">宽度像素，&gt; 0。</param>
    /// <param name="height">高度像素，&gt; 0。</param>
    /// <returns>所有字节为 0 的缓冲（预乘语义下 0 = 透明）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">宽或高不为正，或 <c>Width * 4 * Height</c> 溢出 <see cref="int"/>。</exception>
    /// <remarks>
    /// 全 0 在预乘语义下是"全透明"而不是"全黑不透明"—— 这是预乘的固有好处：
    /// <c>颜色 × alpha = 0</c> 自然成立，不需要任何特判。
    /// </remarks>
    public static PixelBuffer Create(int width, int height)
    {
        int length = CheckedLength(width, height);
        return new PixelBuffer(width, height, new byte[length]);
    }

    /// <summary>从直通 RGBA8 导入并<b>立即预乘</b>。<paramref name="src"/> 长度必须 ≥ <c>Stride * Height</c>。</summary>
    /// <param name="src">直通 RGBA8 源数据，长度须 ≥ <c>Width * 4 * Height</c>；多余部分被忽略。</param>
    /// <param name="width">宽度像素，&gt; 0。</param>
    /// <param name="height">高度像素，&gt; 0。</param>
    /// <returns>预乘后的新缓冲。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="src"/> 的底层数组为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法。</exception>
    /// <exception cref="ArgumentException"><paramref name="src"/> 太短。</exception>
    public static PixelBuffer FromStraightRgba(ReadOnlySpan<byte> src, int width, int height)
    {
        int length = CheckedLength(width, height);
        if (src.Length < length)
        {
            throw new ArgumentException(
                $"源数据长度 {src.Length} 小于所需的 {length}（{width}×{height} 预乘 RGBA8）。", nameof(src));
        }

        var result = new byte[length];
        for (int i = 0; i < length; i += 4)
        {
            byte a = src[i + 3];
            // alpha == 0 时直通色无意义，原样写 0 即可（预乘后本就该全 0）。
            // C 源码 AdjustPixels.c:315-316 的 `if (!alpha) continue;` 是同一意图，
            // 但它跳过整个像素而这里是"写 0"，对结果没有区别。
            if (a == 0)
            {
                continue;
            }

            result[i] = Premultiply(src[i], a);
            result[i + 1] = Premultiply(src[i + 1], a);
            result[i + 2] = Premultiply(src[i + 2], a);
            result[i + 3] = a;
        }

        return new PixelBuffer(width, height, result);
    }

    /// <summary>导出为直通 RGBA8（写 PNG 前用）。</summary>
    /// <returns>一个新的直通缓冲；本实例不变。</returns>
    /// <remarks>
    /// 返回新实例而不是就地反预乘，是为了避免"同一个 <see cref="PixelBuffer"/>
    /// 有时是预乘有时是直通"这种状态泄漏。PNG 标准要求 straight alpha。
    /// </remarks>
    public PixelBuffer ToStraightRgba()
    {
        var result = new byte[_data.Length];
        for (int i = 0; i < _data.Length; i += 4)
        {
            byte a = _data[i + 3];
            if (a == 0)
            {
                continue;
            }

            result[i] = Unpremultiply(_data[i], a);
            result[i + 1] = Unpremultiply(_data[i + 1], a);
            result[i + 2] = Unpremultiply(_data[i + 2], a);
            result[i + 3] = a;
        }

        return new PixelBuffer(Width, Height, result);
    }

    /// <summary>深拷贝。</summary>
    /// <returns>内容完全相同的新缓冲。</returns>
    public PixelBuffer Clone() => new(Width, Height, (byte[])_data.Clone());

    /// <summary>用直通色填充，内部会预乘。</summary>
    /// <param name="straight">直通色。</param>
    public void Fill(RgbaColor straight)
    {
        byte a = straight.A;
        byte r = Premultiply(straight.R, a);
        byte g = Premultiply(straight.G, a);
        byte b = Premultiply(straight.B, a);
        for (int i = 0; i < _data.Length; i += 4)
        {
            _data[i] = r;
            _data[i + 1] = g;
            _data[i + 2] = b;
            _data[i + 3] = a;
        }
    }

    /// <summary>仅供铁律 2 的标量运算内联使用；调用方负责保证 a 已是预乘值。</summary>
    /// <param name="channel">直通通道值 0–255。</param>
    /// <param name="alpha">alpha 0–255。</param>
    /// <returns>预乘后的通道值，恒 ≤ alpha。</returns>
    /// <remarks>
    /// 逐字对应 C 的 <c>p[0] = (uint8_t)((color[0] * a + 127u) / 255u)</c>
    /// （<c>AdjustPixels.c:56</c>）。<b>+127 再整除</b>是四舍五入；
    /// 若写成 <c>(color[0] * a) / 255</c> 会系统性地偏暗半个量化步。
    /// 整数除法在 C# 与 C 都是向零截断，行为一致。
    /// </remarks>
    public static byte Premultiply(byte channel, byte alpha) => (byte)((channel * alpha + 127) / 255);

    /// <summary>反预乘：把预乘通道还原成直通值。仅供铁律 2 的标量运算内联使用。</summary>
    /// <param name="channel">预乘通道值 0–255。</param>
    /// <param name="alpha">alpha 0–255。</param>
    /// <returns>直通通道值，0–255。alpha 为 0 时返回 0。</returns>
    /// <remarks>
    /// C 侧读出用的是浮点 <c>unpremultiply = a == 255 ? 1.0f : 255.0f / a</c>
    /// （<c>AdjustPixels.c:57</c>）再乘回去；本方法是对它的<b>整数</b>等价写法
    /// （<c>(c*255 + a/2) / a</c>），用于 byte→byte 的边界。
    /// <b>需要浮点精度的逐像素直译请继续用 C 侧那个浮点写法，不要改调本方法。</b>
    /// <para>alpha 为 0 时是 0/0 —— 全透明像素没有颜色，返回 0。</para>
    /// </remarks>
    public static byte Unpremultiply(byte channel, byte alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        int v = (channel * 255 + alpha / 2) / alpha;
        return v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>校验尺寸合法并算出总字节数。</summary>
    private static int CheckedLength(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正。");
        }

        // Stride * Height 用 long 算：4×30000×30000 = 3.6e9 已超 int.MaxValue，
        // 而 DocumentLimits 的 maxSide 就是 30000，这个量级是真实存在的。
        long len = (long)width * 4 * height;
        if (len > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"尺寸过大：{width}×{height} 需要 {len} 字节，超过 int 上限。");
        }

        return (int)len;
    }
}
