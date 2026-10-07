namespace Compositor.Selection;

using Compositor.Core;

/// <summary>
/// 覆盖度平面：M5 选区算法层的通用产物，承载一段 8-bit 灰度覆盖度与其文档坐标包围盒。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是"上层"的载体，故意不依赖契约层尚未冻结的构造入口。</b>
/// 契约 v1.2 批准的 <c>Coverage8.FromData</c> 与 <c>SelectionMask.FromCoverage</c>
/// 由 AI-1 在 <c>Compositor.Core</c> 中实现；本工程在那些方法落地之前
/// 只产出 <see cref="Pixels"/> + <see cref="Width"/>/<see cref="Height"/> + <see cref="Bounds"/>，
/// 由下层（Adapter）负责包成 <c>SelectionMask</c>。这样几何算法可以完全独立推进与测试，
/// 且不需要任何 <c>InternalsVisibleTo</c> 之类的绕过手段。
/// </para>
/// <para>
/// 🔴 <b>铁律 2</b>：这是<b>覆盖度</b>，不是 alpha。它是单通道灰度，0 = 完全未选中、
/// 255 = 完全选中，不携带任何不透明度含义，更不是预乘 alpha 的 alpha 通道。
/// </para>
/// <para>
/// 🔴 <b>铁律 1</b>：<see cref="Bounds"/> 用文档坐标，<b>原点在左上角、Y 向下</b>，
/// 本类型<b>不做任何 Y 翻转</b>。<see cref="Pixels"/> 的第 0 行第 0 列对应
/// 文档坐标 <c>(Bounds.Left, Bounds.Top)</c>，行优先、无对齐填充，
/// 这与 <c>SelectionMask.CoverageAt</c> 的局部坐标换算口径完全一致。
/// </para>
/// </remarks>
public sealed class CoveragePlane
{
    private readonly byte[] _pixels;

    /// <summary>由已有缓冲构造，并校验长度与包围盒一致。</summary>
    /// <param name="bounds">文档坐标包围盒，必须非空。</param>
    /// <param name="pixels">行优先的覆盖度缓冲，长度必须等于 <c>bounds.Size.Width * bounds.Size.Height</c>。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="bounds"/> 为空，或 <paramref name="pixels"/> 长度与包围盒不匹配。
    /// </exception>
    public CoveragePlane(DocRect bounds, byte[] pixels)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("包围盒不能为空。", nameof(bounds));
        }

        long expected = (long)bounds.Size.Width * bounds.Size.Height;
        if (pixels.LongLength != expected)
        {
            throw new ArgumentException(
                $"缓冲长度 {pixels.LongLength} 与包围盒 {bounds.Size.Width}×{bounds.Size.Height} 期望的 {expected} 不符。",
                nameof(pixels));
        }

        Bounds = bounds;
        _pixels = pixels;
    }

    /// <summary>文档坐标包围盒。<see cref="Pixels"/> 第 0 行第 0 列位于它的左上角。</summary>
    public DocRect Bounds { get; }

    /// <summary>宽度像素，恒大于 0。</summary>
    public int Width => Bounds.Size.Width;

    /// <summary>高度像素，恒大于 0。</summary>
    public int Height => Bounds.Size.Height;

    /// <summary>底层覆盖度缓冲的只读视图，长度为 <c>Width * Height</c>，行优先。</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>按局部坐标读写覆盖度。</summary>
    /// <param name="x">列号，相对 <see cref="DocRect.Left"/>，范围 <c>[0, Width)</c>。</param>
    /// <param name="y">行号，相对 <see cref="DocRect.Top"/>，范围 <c>[0, Height)</c>。</param>
    /// <returns>该位置的覆盖度，取值 0–255。</returns>
    /// <exception cref="ArgumentOutOfRangeException">局部坐标越界。</exception>
    public byte this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width)
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, $"列号必须落在 [0, {Width})。");
            }

            if ((uint)y >= (uint)Height)
            {
                throw new ArgumentOutOfRangeException(nameof(y), y, $"行号必须落在 [0, {Height})。");
            }

            return _pixels[(y * Width) + x];
        }

        set
        {
            if ((uint)x >= (uint)Width)
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, $"列号必须落在 [0, {Width})。");
            }

            if ((uint)y >= (uint)Height)
            {
                throw new ArgumentOutOfRangeException(nameof(y), y, $"行号必须落在 [0, {Height})。");
            }

            _pixels[(y * Width) + x] = value;
        }
    }

    /// <summary>创建指定包围盒的全零覆盖度平面。</summary>
    /// <param name="bounds">非空的文档坐标包围盒。</param>
    /// <returns>全部为 0 的覆盖度平面。</returns>
    public static CoveragePlane CreateZero(DocRect bounds) =>
        new(bounds, new byte[(long)bounds.Size.Width * bounds.Size.Height]);

    /// <summary>创建指定包围盒的填充覆盖度平面。</summary>
    /// <param name="bounds">非空的文档坐标包围盒。</param>
    /// <param name="value">填充值，0–255。</param>
    /// <returns>全部为 <paramref name="value"/> 的覆盖度平面。</returns>
    public static CoveragePlane CreateFilled(DocRect bounds, byte value)
    {
        var plane = CreateZero(bounds);
        if (value != 0)
        {
            Array.Fill(plane._pixels, value);
        }

        return plane;
    }

    /// <summary>深拷贝，拷贝后可安全就地修改而不影响原对象。</summary>
    /// <returns>独立的覆盖度平面副本。</returns>
    public CoveragePlane Clone() => new(Bounds, (byte[])_pixels.Clone());

    /// <summary>
    /// 跳过坐标范围检查的写入，仅供本程序集内的光栅化热路径使用。
    /// </summary>
    /// <param name="x">列号，调用方须保证落在 <c>[0, Width)</c>。</param>
    /// <param name="y">行号，调用方须保证落在 <c>[0, Height)</c>。</param>
    /// <param name="value">覆盖度，取值 0–255。</param>
    /// <remarks>
    /// 公开索引器每次写入都做两次 <see cref="uint"/> 范围内的比较。
    /// 光栅化时坐标天然由循环变量保证合法，逐像素重复校验纯属浪费，
    /// 所以内部路径走本方法。它是 <c>internal</c>，外部拿不到，
    /// 也就不会有人用它写越界坐标。
    /// </remarks>
    internal void SetPixelUnchecked(int x, int y, byte value) => _pixels[(y * Width) + x] = value;

    /// <summary>按文档坐标取值，落在包围盒之外时返回 0。</summary>
    /// <param name="p">文档坐标点，Y 向下（铁律 1）。</param>
    /// <returns>该点的覆盖度，取值 0–255。</returns>
    /// <remarks>
    /// 换算口径与契约层 <c>SelectionMask.CoverageAt</c> 保持一致：
    /// 局部坐标 = 文档坐标减包围盒原点，包围盒按半开区间 <c>[Left, Right) × [Top, Bottom)</c> 处理。
    /// </remarks>
    public byte CoverageAt(DocPoint p)
    {
        if (!Bounds.Contains(p))
        {
            return 0;
        }

        int ix = (int)(p.X - Bounds.Left);
        int iy = (int)(p.Y - Bounds.Top);

        // Bounds.Contains 用的是半开区间，保证 ix/iy 必然在界内；
        // 这里仍钳一次，防止浮点在边界附近被截断成 Width/Height。
        if ((uint)ix >= (uint)Width || (uint)iy >= (uint)Height)
        {
            return 0;
        }

        return _pixels[(iy * Width) + ix];
    }

    /// <summary>缓冲中是否存在任何非零覆盖度。</summary>
    public bool HasContent()
    {
        foreach (byte v in _pixels)
        {
            if (v != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把包围盒收缩到实际含非零覆盖度的最小矩形，使包围盒与内容一致。
    /// </summary>
    /// <returns>
    /// 内容全部为 0 时返回 <see langword="null"/>（表示"空"）；
    /// 否则返回收缩后的新平面，缓冲内容保持不变。
    /// </returns>
    /// <remarks>
    /// 羽化会把覆盖度扩散到原包围盒之外，若不收缩，结果的包围盒会带一圈全 0 的空边，
    /// 既浪费内存也会让后续布尔运算多做无用功。
    /// </remarks>
    public CoveragePlane? TrimToContent()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (_pixels[row + x] == 0)
                {
                    continue;
                }

                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        int w = maxX - minX + 1;
        int h = maxY - minY + 1;
        var bounds = new DocRect(
            new DocPoint(Bounds.Left + minX, Bounds.Top + minY),
            new DocSize(w, h));
        var result = new byte[w * h];

        for (int y = 0; y < h; y++)
        {
            Array.Copy(_pixels, ((minY + y) * Width) + minX, result, y * w, w);
        }

        return new CoveragePlane(bounds, result);
    }
}