namespace Compositor.Core.Pixels;

/// <summary>
/// 抖动风格。顺序与值与 C 的 <c>DitherPixels.h</c> 匿名枚举逐项一致，
/// 面板按这个顺序列出，且可能被序列化，因此不得重排、不得改动数值。
/// </summary>
public enum DitherStyle
{
    /// <summary><c>DITHER_ATKINSON</c> = 0：Atkinson 误差扩散（只传 6/8 的误差，Mac 那种干脆对比强烈的观感）。</summary>
    Atkinson = 0,

    /// <summary><c>DITHER_FLOYD_STEINBERG</c> = 1：Floyd–Steinberg 误差扩散。</summary>
    FloydSteinberg = 1,

    /// <summary><c>DITHER_BAYER_2</c> = 2：2×2 有序抖动。</summary>
    Bayer2 = 2,

    /// <summary><c>DITHER_BAYER_4</c> = 3：4×4 有序抖动。</summary>
    Bayer4 = 3,

    /// <summary><c>DITHER_BAYER_8</c> = 4：8×8 有序抖动。</summary>
    Bayer8 = 4,

    /// <summary><c>DITHER_DOTS</c> = 5：圆形网点半调。</summary>
    Dots = 5,

    /// <summary><c>DITHER_LINES</c> = 6：线状半调。</summary>
    Lines = 6,

    /// <summary><c>DITHER_DIAMONDS</c> = 7：菱形半调。</summary>
    Diamonds = 7,

    /// <summary><c>DITHER_PATTERNS</c> = 8：Mac 经典 8×8 填充图案。</summary>
    Patterns = 8,

    /// <summary><c>DITHER_GLYPHS</c> = 9：字形（按字形覆盖率逐格选一张位图）。</summary>
    Glyphs = 9,

    /// <summary><c>DITHER_SCANLINES</c> = 10：扫描线（CRT）。</summary>
    Scanlines = 10,
}

/// <summary>
/// 抖动参数。直译自 C 的 <c>typedef struct { ... } DitherParams</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么是 <c>readonly ref struct</c></b>：C 的结构体里有两个指针成员
/// （<c>const uint8_t *glyphs</c> / <c>const float *glyphCoverage</c>）。
/// C# 里 <c>Span&lt;T&gt;</c> / <c>ReadOnlySpan&lt;T&gt;</c> 是 ref struct，
/// <b>只能出现在 ref struct 的字段里</b>——放进普通 struct、class 或 record struct 都编译不过。
/// 所以这里用 <c>readonly ref struct</c>：既保留了 C 的"值拷贝"语义
/// （原 C 的 <c>DitherParams local = *p; local.levels = levels;</c> 对应
/// <see cref="WithLevels"/> 返回的副本），又能安全携带两个 span。</para>
///
/// <para><b>代价</b>：ref struct 不能进数组、不能被 async/迭代器/lambda 捕获。
/// 本文件所有调用都是同步直调，不受影响。</para>
///
/// <para><b>不变量</b>：<c>Dark</c>/<c>Light</c> 须各有 3 字节；
/// <c>Glyphs</c> 须 ≥ GlyphCount×GlyphWidth×GlyphHeight 字节，
/// <c>GlyphCoverage</c> 须 ≥ GlyphCount 个 float，且按"由少到多"排序（首项最淡、末项最浓，
/// 代码用 <c>GlyphCoverage[GlyphCount - 1]</c> 作为目标覆盖率）。</para>
/// </remarks>
public readonly ref struct DitherParams
{
    /// <summary>风格，取值见 <see cref="DitherStyle"/>。</summary>
    public readonly DitherStyle Style;

    /// <summary>扩散与有序风格的每通道色阶数；C 侧注释写 2–8，代码实际夹在 2–16（见 <c>Apply</c>）。</summary>
    public readonly int Levels;

    /// <summary>误差扩散传递比例 0–1。</summary>
    public readonly float Diffusion;

    /// <summary>密度 −1…1：正值压暗，负值提亮。</summary>
    public readonly float Density;

    /// <summary>对比度 −1…1：以中灰为轴。</summary>
    public readonly float Contrast;

    /// <summary>半调/字形格边长（像素）；扫描线风格下是行距。</summary>
    public readonly int Cell;

    /// <summary>半调网屏角度（弧度）。</summary>
    public readonly float Angle;

    /// <summary>非 0：网点、图案、字形改为在深色上画亮调。</summary>
    public readonly int LightOnDark;

    /// <summary>0：只用 <see cref="Dark"/>/<see cref="Light"/> 两色；1：保留图像原色。</summary>
    public readonly int OriginalColors;

    /// <summary>暗色，直通 sRGB，3 字节。</summary>
    public readonly ReadOnlySpan<byte> Dark;

    /// <summary>亮色，直通 sRGB，3 字节。</summary>
    public readonly ReadOnlySpan<byte> Light;

    /// <summary>单张字形位图宽（像素），&lt; 1 时按 1 处理。</summary>
    public readonly int GlyphWidth;

    /// <summary>单张字形位图高（像素），&lt; 1 时按 1 处理。</summary>
    public readonly int GlyphHeight;

    /// <summary>字形位图，GlyphCount 张，每张 GlyphWidth×GlyphHeight 字节，255 为全着墨。</summary>
    public readonly ReadOnlySpan<byte> Glyphs;

    /// <summary>每张字形的平均覆盖率（0–1），长度 = <see cref="GlyphCount"/>。</summary>
    public readonly ReadOnlySpan<float> GlyphCoverage;

    /// <summary>字形张数；≤ 0 时 <see cref="DitherStyle.Glyphs"/> 退化为按网点公式处理。</summary>
    public readonly int GlyphCount;

    /// <summary>扫描线：行扫描线断成圆点的程度 0–1。</summary>
    public readonly float Dots;

    /// <summary>扫描线：抖动的横向偏移像素数。</summary>
    public readonly float Wobble;

    /// <summary>按字段顺序构造（与 C 结构体初始化顺序一致）。</summary>
    /// <param name="style">风格。</param>
    /// <param name="levels">色阶数。</param>
    /// <param name="diffusion">误差扩散比例。</param>
    /// <param name="density">密度 −1…1。</param>
    /// <param name="contrast">对比度 −1…1。</param>
    /// <param name="cell">格边长 / 扫描线行距。</param>
    /// <param name="angle">网屏角度（弧度）。</param>
    /// <param name="lightOnDark">非 0 = 深底亮调。</param>
    /// <param name="originalColors">非 0 = 保留原色。</param>
    /// <param name="dark">暗色 3 字节。</param>
    /// <param name="light">亮色 3 字节。</param>
    /// <param name="glyphWidth">字形宽。</param>
    /// <param name="glyphHeight">字形高。</param>
    /// <param name="glyphs">字形位图。</param>
    /// <param name="glyphCoverage">字形平均覆盖率。</param>
    /// <param name="glyphCount">字形张数。</param>
    /// <param name="dots">扫描线断点程度。</param>
    /// <param name="wobble">扫描线横向抖动像素数。</param>
    public DitherParams(
        DitherStyle style, int levels, float diffusion, float density, float contrast,
        int cell, float angle, int lightOnDark, int originalColors,
        ReadOnlySpan<byte> dark, ReadOnlySpan<byte> light,
        int glyphWidth, int glyphHeight, ReadOnlySpan<byte> glyphs, ReadOnlySpan<float> glyphCoverage,
        int glyphCount, float dots, float wobble)
    {
        Style = style;
        Levels = levels;
        Diffusion = diffusion;
        Density = density;
        Contrast = contrast;
        Cell = cell;
        Angle = angle;
        LightOnDark = lightOnDark;
        OriginalColors = originalColors;
        Dark = dark;
        Light = light;
        GlyphWidth = glyphWidth;
        GlyphHeight = glyphHeight;
        Glyphs = glyphs;
        GlyphCoverage = glyphCoverage;
        GlyphCount = glyphCount;
        Dots = dots;
        Wobble = wobble;
    }

    /// <summary>
    /// 返回只把 <paramref name="levels"/> 换掉的新副本（原 C 的 <c>DitherParams local = *p; local.levels = levels;</c>）。
    /// </summary>
    /// <param name="levels">新的色阶数。</param>
    public DitherParams WithLevels(int levels) => new(
        Style, levels, Diffusion, Density, Contrast,
        Cell, Angle, LightOnDark, OriginalColors,
        Dark, Light, GlyphWidth, GlyphHeight, Glyphs, GlyphCoverage,
        GlyphCount, Dots, Wobble);
}

/// <summary>
/// 抖动 / 半调 / 扫描线。直译自 <c>Rendering/DitherPixels.c</c>（374 行，逐行对照）。
/// </summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>clamp01</c> / <c>in_bands</c> / <c>adjust_tone</c> /
/// <c>kernel_for</c> / <c>quantize</c> / <c>diffuse</c> / <c>ordered_threshold</c> /
/// <c>ordered</c> / <c>spot</c> / <c>write_pixel</c> / <c>dither_apply</c> /
/// <c>dither_dots</c> / <c>dither_glow</c>。除三个 <c>dither_*</c> 入口外全部是 <c>static</c>，保持 <c>private</c>。</para>
///
/// <para><b>不变量</b>：rgba 为预乘 RGBA8（4 字节/像素，<c>stride</c> 字节/行）；
/// alpha 永远保持不变，完全透明的像素一个字节都不改。</para>
///
/// <para><b>与原 C 的等价改写</b>（均不改变数值结果）：
/// <list type="bullet">
/// <item><c>in_bands(count, block)</c>（GCD 并行）在三处调用点内联为串行 band 循环。
/// 每个 band 只写自己的行（扫描线那处还自带独立的 <c>scan</c> 缓冲），彼此无共享写，
/// 故串行与并行逐位等价。</item>
/// <item><c>typedef struct Tap / Kernel</c> → 私有 <c>Tap</c> 普通 readonly struct +
/// 私有 <c>Kernel</c> <c>readonly ref struct</c>（<c>Kernel</c> 里存 <c>ReadOnlySpan&lt;Tap&gt;</c>，
/// 只有 ref struct 能放 span 字段）。<c>atkinson</c>/<c>floyd</c> 两张表原样保留。</item>
/// <item><c>malloc</c>/<c>free</c> → 定长 <c>new T[n]</c>；所有"分配失败返回 0"的分支
/// （原 C 返回 0）以及扫描线的 <c>__block failed</c> 在 C# 中不存在，改为抛
/// <see cref="OutOfMemoryException"/>。</item>
/// <item><c>source</c> 在 <c>originalColors == 0</c> 时原 C 为 NULL 且从不解引用；
/// C# 用 <see cref="Array.Empty{T}()"/> 代替 null（避免可空引用警告），解引用行为一致。</item>
/// <item><c>float *marks = originalColors ? malloc(count) : tone;</c> → C# 直接用
/// <c>float[] marks = originalColors != 0 ? new float[count] : tone;</c>，
/// 别名关系（<c>marks == tone</c> 时不重复释放）与原 C 一致。</item>
/// <item><c>static const uint8_t patterns[][8]</c>（17×8 的二维数组）→ 一维
/// <c>byte[136]</c>，下标 <c>patterns[index][y &amp; 7]</c> 改为
/// <c>PatternsTable[index * 8 + (y &amp; 7)]</c>。<c>patternCount</c> 仍是 17。</item>
/// <item><c>float dark[3] / light[3]</c>、<c>const float *screen/phosphor</c>、
/// <c>float *ink/*paper</c>、<c>float sum[3]</c> → 命名局部标量（逐个对应下标 0/1/2）。</item>
/// <item><c>roundf</c> → <see cref="CSemantics.RoundF"/>（C# 默认是银行家舍入），
/// <c>lroundf</c> → <see cref="CSemantics.LRoundF"/>，<c>(uint8_t)</c> → <see cref="CSemantics.U8F(float)"/>。</item>
/// <item>字面量一律原样：<c>3.14159265f</c>（截断的 π，<b>不是</b> <c>MathF.PI</c>）、
/// <c>0.2126f/0.7152f/0.0722f</c>、<c>255.0f</c>、<c>0.5f</c>、<c>1.35f</c>、
/// <c>0.42f</c>、<c>0.45f/1.7f/1.3f/0.7f/0.3f</c> 等；<c>exp2f</c> → <see cref="CSemantics.Exp2F"/>（.NET 没有 <c>MathF.Exp2</c>）。</item>
/// <item>C 中 double→float 的隐式窄化（<c>detail</c>、<c>sqrt</c> 等）补显式 <c>(float)</c>，
/// 取同一个值。</item>
/// </list></para>
///
/// <para><b>已知原 C 的可疑点（未修改）</b>：头文件写 <c>levels</c> 为 2–8，代码却夹在 2–16；
/// <c>diffuse</c> 扩散时只判 <c>ny &gt;= height</c> 而不判 <c>ny &lt; 0</c>（当前 tap 的 dy 非负，
/// 所以没触发，但补 tap 时会踩）；扫描线里 <c>screen</c> 恒取 <c>dark</c>、<c>phosphor</c> 恒取
/// <c>light</c>，不看 <c>lightOnDark</c>；<c>DITHER_GLYPHS</c> 在 <c>glyphCount &lt;= 0</c> 时
/// 会落进半调公式分支（<c>spot</c> 对 9 号没有 case，走 default 的菱形）。</para>
/// </remarks>
public static class DitherPixels
{
    /// <summary>C 的 <c>static const Tap atkinson[]</c>：只传 6/8 误差，故 DIV 8。</summary>
    private static readonly Tap[] AtkinsonTaps =
    {
        new Tap(1, 0, 1), new Tap(2, 0, 1), new Tap(-1, 1, 1),
        new Tap(0, 1, 1), new Tap(1, 1, 1), new Tap(0, 2, 1),
    };

    /// <summary>C 的 <c>static const Tap floyd[]</c>。</summary>
    private static readonly Tap[] FloydTaps =
    {
        new Tap(1, 0, 7), new Tap(-1, 1, 3), new Tap(0, 1, 5), new Tap(1, 1, 1),
    };

    /// <summary>C 的 <c>static const uint8_t bayer8[64]</c>。</summary>
    private static readonly byte[] Bayer8Table =
    {
         0, 32,  8, 40,  2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    };

    /// <summary>C 的 <c>static const uint8_t m[4]</c>（DITHER_BAYER_2）。</summary>
    private static readonly byte[] Bayer2Table = { 0, 2, 3, 1 };

    /// <summary>C 的 <c>static const uint8_t m[16]</c>（DITHER_BAYER_4）。</summary>
    private static readonly byte[] Bayer4Table =
    {
        0, 8, 2, 10, 12, 4, 14, 6,
        3, 11, 1, 9, 15, 7, 13, 5,
    };

    /// <summary>
    /// C 的 <c>static const uint8_t patterns[][8]</c>，展平成一维：每 8 个字节一行，
    /// 行内最左像素在最高位，从最稀疏到最密实。取值与顺序逐字节照抄。
    /// </summary>
    private static readonly byte[] PatternsTable =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x80, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x88, 0x00, 0x22, 0x00, 0x88, 0x00, 0x22, 0x00,
        0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01,
        0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22,
        0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00,
        0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88,
        0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00,
        0x88, 0x55, 0x22, 0x55, 0x88, 0x55, 0x22, 0x55,
        0xFF, 0x80, 0x80, 0x80, 0xFF, 0x08, 0x08, 0x08,
        0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55,
        0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81,
        0x77, 0xAA, 0xDD, 0xAA, 0x77, 0xAA, 0xDD, 0xAA,
        0xEE, 0xDD, 0xBB, 0x77, 0xEE, 0xDD, 0xBB, 0x77,
        0x77, 0xFF, 0xDD, 0xFF, 0x77, 0xFF, 0xDD, 0xFF,
        0x7F, 0xFF, 0xFF, 0xFF, 0xF7, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    /// <summary>C 的 <c>static const int patternCount = (int)(sizeof patterns / sizeof patterns[0]);</c> = 17。</summary>
    private const int PatternCount = 17;

    /// <summary>C 的 <c>typedef struct { int dx, dy, weight; } Tap</c>。</summary>
    private readonly struct Tap
    {
        /// <summary>相对当前像素的横向偏移。</summary>
        public readonly int Dx;

        /// <summary>相对当前像素的纵向偏移。</summary>
        public readonly int Dy;

        /// <summary>误差权重。</summary>
        public readonly int Weight;

        /// <summary>按 (dx, dy, weight) 构造。</summary>
        /// <param name="dx">横向偏移。</param>
        /// <param name="dy">纵向偏移。</param>
        /// <param name="weight">权重。</param>
        public Tap(int dx, int dy, int weight)
        {
            Dx = dx;
            Dy = dy;
            Weight = weight;
        }
    }

    /// <summary>
    /// C 的 <c>typedef struct { const Tap *taps; int count; float divisor; } Kernel</c>。
    /// 因为要装 <c>ReadOnlySpan&lt;Tap&gt;</c>，只能是 <c>ref struct</c>。
    /// </summary>
    private readonly ref struct Kernel
    {
        /// <summary>误差核（当前行右侧与下方）的 tap 表。</summary>
        public readonly ReadOnlySpan<Tap> Taps;

        /// <summary>tap 个数。</summary>
        public readonly int Count;

        /// <summary>权重总和（原 C 里 Atkinson 传 6 却除以 8，这是它干脆的关键）。</summary>
        public readonly float Divisor;

        /// <summary>按 (taps, count, divisor) 构造。</summary>
        /// <param name="taps">tap 表。</param>
        /// <param name="count">tap 个数。</param>
        /// <param name="divisor">权重分母。</param>
        public Kernel(ReadOnlySpan<Tap> taps, int count, float divisor)
        {
            Taps = taps;
            Count = count;
            Divisor = divisor;
        }
    }

    /// <summary>C 的 <c>clamp01</c>。</summary>
    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>
    /// 密度按 gamma 压暗/提亮（黑白两端不动），对比度以中灰为轴。
    /// </summary>
    private static float AdjustTone(float v, float gamma, float contrast)
    {
        v = MathF.Pow(Clamp01(v), gamma);
        return Clamp01((v - 0.5f) * contrast + 0.5f);
    }

    /// <summary>C 的 <c>kernel_for</c>：非 Atkinson 一律落到 Floyd–Steinberg。</summary>
    private static Kernel KernelFor(DitherStyle style) => style == DitherStyle.Atkinson
        ? new Kernel(AtkinsonTaps, 6, 8)
        : new Kernel(FloydTaps, 4, 16);

    /// <summary>C 的 <c>quantize</c>：<c>roundf</c>（远离零）不是 C# 默认的银行家舍入。</summary>
    private static float Quantize(float v, int levels)
    {
        float steps = levels - 1;
        return CSemantics.RoundF(Clamp01(v) * steps) / steps;
    }

    /// <summary>
    /// 逐平面做蛇形误差扩散（原 C 的 <c>diffuse</c>）。
    /// </summary>
    /// <remarks>
    /// 不变式：只改 <paramref name="plane"/>；alpha 为 0 的像素既不量化也不扩散（原样跳过）。
    /// ⚠ 原 C 只判 <c>nx &lt; 0 || nx &gt;= width || ny &gt;= height</c>，漏了 <c>ny &lt; 0</c>；
    /// 当前两张 tap 表的 dy 非负，故不会越界，此处原样保留该条件未补。
    /// </remarks>
    private static void Diffuse(Span<float> plane, ReadOnlySpan<byte> alpha, int width, int height, in DitherParams p)
    {
        Kernel k = KernelFor(p.Style);

        for (int y = 0; y < height; ++y)
        {
            int reverse = y & 1;

            for (int i = 0; i < width; ++i)
            {
                int x = reverse != 0 ? width - 1 - i : i;
                int at = y * width + x;
                if (alpha[at] == 0) continue;

                float old = plane[at], q = Quantize(old, p.Levels);
                plane[at] = q;
                float error = (old - q) * p.Diffusion / k.Divisor;

                for (int t = 0; t < k.Count; ++t)
                {
                    long nx = x + (reverse != 0 ? -k.Taps[t].Dx : k.Taps[t].Dx), ny = y + k.Taps[t].Dy;
                    if (nx < 0 || nx >= width || ny >= height) continue;
                    plane[(int)(ny * width + nx)] += error * (float)k.Taps[t].Weight;
                }
            }
        }
    }

    /// <summary>
    /// 有序抖动的阈值（取值 0–1，左闭右开）；更小的 Bayer 矩阵是 8×8 矩阵左上角的重标版本。
    /// </summary>
    private static float OrderedThreshold(int style, int x, int y)
    {
        switch (style)
        {
            case (int)DitherStyle.Bayer2:
                return (Bayer2Table[(y & 1) * 2 + (x & 1)] + 0.5f) / 4;
            case (int)DitherStyle.Bayer4:
                return (Bayer4Table[(y & 3) * 4 + (x & 3)] + 0.5f) / 16;
            default:
                return (Bayer8Table[(y & 7) * 8 + (x & 7)] + 0.5f) / 64;
        }
    }

    /// <summary>C 的 <c>ordered</c>：带阈值的向下取整量化。</summary>
    private static float Ordered(float v, float threshold, int levels)
    {
        float steps = levels - 1;
        float q = MathF.Floor(Clamp01(v) * steps + threshold);
        return (q > steps ? steps : q) / steps;
    }

    /// <summary>
    /// 各网屏形状的覆盖阈值（原 C 的 <c>spot</c>）：<c>u</c>/<c>v</c> 在格内从 −0.5 到 0.5，
    /// 形状由中心向外随覆盖率增长。
    /// </summary>
    private static float Spot(int style, float u, float v)
    {
        float au = MathF.Abs(u), av = MathF.Abs(v);

        switch (style)
        {
            case (int)DitherStyle.Dots:
                // 截断过的 π 字面量，原样保留（换成 MathF.PI 会差几个 ulp）。
                return 3.14159265f * (u * u + v * v);
            case (int)DitherStyle.Lines:
                return av * 2;
            default:
                return au + av;
        }
    }

    /// <summary>
    /// 把直通 RGB 按目标像素自己的 alpha 重新预乘后写回 3 个颜色通道（原 C 的 <c>write_pixel</c>）。
    /// </summary>
    /// <remarks>alpha 通道不读也不写；<c>clamp01(r) * a * 255.0f</c> 的左结合顺序保留。</remarks>
    private static void WritePixel(Span<byte> px, int at, float r, float g, float b)
    {
        float a = px[at + 3] / 255.0f;
        px[at] = CSemantics.U8F(CSemantics.LRoundF(Clamp01(r) * a * 255.0f));
        px[at + 1] = CSemantics.U8F(CSemantics.LRoundF(Clamp01(g) * a * 255.0f));
        px[at + 2] = CSemantics.U8F(CSemantics.LRoundF(Clamp01(b) * a * 255.0f));
    }

    /// <summary>
    /// 按参数对预乘 RGBA 做原地抖动。直译自 <c>int dither_apply(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：alpha 完全不变；alpha 为 0 的像素完全不变；
    /// 半调/图案/字形三种风格只按 coverage 覆盖"深↔浅"两个极色，不插中间调。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">rgba 每行字节数，须 ≥ <paramref name="width"/> * 4。</param>
    /// <param name="p">参数（值语义；内部只在扩散分支取一份改过 <c>levels</c> 的副本）。</param>
    /// <returns>1 = 完成。原 C 的 0（内存不足）改为抛 <see cref="OutOfMemoryException"/>；空图返回 1 且不改像素。</returns>
    public static int Apply(Span<byte> rgba, int width, int height, int stride, in DitherParams p)
    {
        int count = width * height;
        if (count == 0) return 1;

        int planes = p.OriginalColors != 0 ? 3 : 1;
        float[] tone = new float[count * planes];
        byte[] alpha = new byte[count];

        // The image's own colors, unadjusted: halftone dots and glyphs take them in Original mode.
        // 原 C 在 !originalColors 时 source 为 NULL 且从不解引用，这里用空数组代替 null。
        float[] source = p.OriginalColors != 0 ? new float[count * 3] : Array.Empty<float>();

        float gamma = CSemantics.Exp2F(p.Density * 1.5f);
        float contrast = p.Contrast >= 0 ? 1.0f / (1.0f - 0.95f * p.Contrast) : 1.0f + p.Contrast;

        // 原 C 的 in_bands(height, ...)：bands = height < 64 ? 1 : 32，size 向上取整。
        // 每个 band 只写自己的行，互不重叠，故串行执行与 dispatch_apply 并行逐位等价。
        int bands = height < 64 ? 1 : 32, bandSize = (height + bands - 1) / bands;
        for (int band = 0; band < bands; ++band)
        {
            int first = band * bandSize, last = first + bandSize < height ? first + bandSize : height;
            if (first >= last) continue;

            for (int y = first; y < last; ++y)
            {
                int row = y * stride;

                for (int x = 0; x < width; ++x)
                {
                    int px = row + x * 4, at = y * width + x;
                    alpha[at] = rgba[px + 3];
                    float r = 0, g = 0, b = 0;

                    if (rgba[px + 3] != 0)
                    {
                        float scale = 1.0f / rgba[px + 3];
                        r = rgba[px] * scale; g = rgba[px + 1] * scale; b = rgba[px + 2] * scale;
                    }

                    if (p.OriginalColors != 0)
                    {
                        tone[at] = AdjustTone(r, gamma, contrast);
                        tone[count + at] = AdjustTone(g, gamma, contrast);
                        tone[2 * count + at] = AdjustTone(b, gamma, contrast);
                        source[at * 3] = r; source[at * 3 + 1] = g; source[at * 3 + 2] = b;
                    }
                    else
                    {
                        tone[at] = AdjustTone(0.2126f * r + 0.7152f * g + 0.0722f * b, gamma, contrast);
                    }
                }
            }
        }

        float dark0 = p.Dark[0] / 255.0f, dark1 = p.Dark[1] / 255.0f, dark2 = p.Dark[2] / 255.0f;
        float light0 = p.Light[0] / 255.0f, light1 = p.Light[1] / 255.0f, light2 = p.Light[2] / 255.0f;
        int style = (int)p.Style;
        int levels = p.Levels < 2 ? 2 : p.Levels > 16 ? 16 : p.Levels;

        if (style <= (int)DitherStyle.Bayer8)
        {
            // Diffusion and ordered dithering: each plane is quantized to `levels` tones, then mapped to colors.
            if (style <= (int)DitherStyle.FloydSteinberg)
            {
                DitherParams local = p.WithLevels(levels);
                for (int c = 0; c < planes; ++c)
                    Diffuse(tone.AsSpan(c * count, count), alpha, width, height, in local);
            }
            else
            {
                for (int c = 0; c < planes; ++c)
                {
                    Span<float> plane = tone.AsSpan(c * count, count);

                    for (int y = 0; y < height; ++y)
                    {
                        for (int x = 0; x < width; ++x)
                        {
                            int at = y * width + x;
                            if (alpha[at] != 0) plane[at] = Ordered(plane[at], OrderedThreshold(style, x, y), levels);
                        }
                    }
                }
            }

            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;

                for (int x = 0; x < width; ++x)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;

                    if (p.OriginalColors != 0)
                    {
                        WritePixel(rgba, row + x * 4, tone[at], tone[count + at], tone[2 * count + at]);
                    }
                    else
                    {
                        float t = tone[at];
                        WritePixel(rgba, row + x * 4,
                            dark0 + (light0 - dark0) * t,
                            dark1 + (light1 - dark1) * t,
                            dark2 + (light2 - dark2) * t);
                    }
                }
            }
        }
        else if (style == (int)DitherStyle.Scanlines)
        {
            // A CRT: each line scans the image, its tone along the line the average of the rows it covers. The beam glows
            // brighter and blooms thicker where the picture is light, and the screen between lines stays dark.
            int spacing = p.Cell < 2 ? 2 : p.Cell;

            // 原 C 是 (float)spacing / 2 —— 浮点除法；写成 spacing / 2 会变成整数除法，奇数行距差 0.5。
            float middle = spacing / 2f, dots = Clamp01(p.Dots);

            // Each line is drawn on its own, so the lines are shared out across the cores.
            int lines = (height + spacing - 1) / spacing;

            // 原 C: const float *screen = dark, *phosphor = light;
            float screen0 = dark0, screen1 = dark1, screen2 = dark2;
            float phosphor0 = light0, phosphor1 = light1, phosphor2 = light2;

            int sBands = lines < 64 ? 1 : 32, sBandSize = (lines + sBands - 1) / sBands;
            for (int band = 0; band < sBands; ++band)
            {
                int firstLine = band * sBandSize, lastLine = firstLine + sBandSize < lines ? firstLine + sBandSize : lines;
                if (firstLine >= lastLine) continue;

                // 原 C 在 band 体里 malloc(width * sizeof(float) * planes) 作为本 band 私有的 scan。
                float[] scan = new float[width * planes];

                for (int line = firstLine; line < lastLine; ++line)
                {
                    int top = line * spacing;
                    int bottom = top + spacing < height ? top + spacing : height;

                    // Wobble: each line is pushed sideways, a slow wave down the screen with a quicker one over it, as a
                    // CRT's picture wavers when its sync drifts.
                    float wave = MathF.Sin(line * 0.45f) * 0.7f + MathF.Sin(line * 1.7f + 1.3f) * 0.3f;
                    long shift = CSemantics.LRoundF(p.Wobble * wave);

                    for (int x = 0; x < width; ++x)
                    {
                        float sum0 = 0, sum1 = 0, sum2 = 0;
                        int n = 0;
                        long sx = x - shift;

                        if (sx >= 0 && sx < width)
                        {
                            for (int y = top; y < bottom; ++y)
                            {
                                int at = y * width + (int)sx;
                                if (alpha[at] == 0) continue;
                                sum0 += tone[at];
                                if (planes == 3) { sum1 += tone[count + at]; sum2 += tone[2 * count + at]; }
                                ++n;
                            }
                        }

                        scan[x] = n != 0 ? sum0 / n : 0f;
                        if (planes == 3)
                        {
                            scan[width + x] = n != 0 ? sum1 / n : 0f;
                            scan[2 * width + x] = n != 0 ? sum2 / n : 0f;
                        }
                    }

                    for (int y = top; y < bottom; ++y)
                    {
                        int row = y * stride;
                        float offset = MathF.Abs(y - top + 0.5f - middle);

                        for (int x = 0; x < width; ++x)
                        {
                            if (alpha[y * width + x] == 0) continue;

                            // Dots: the line breaks into beads, one every line spacing, each lit in the color at its middle.
                            float along = CSemantics.FModF(x + 0.5f, spacing) - middle;
                            long centered = CSemantics.LRoundF(x - along * dots);
                            int at = centered < 0 ? 0 : centered >= width ? width - 1 : (int)centered;
                            float r, g, b, t;

                            if (p.OriginalColors != 0)
                            {
                                r = scan[at]; g = scan[width + at]; b = scan[2 * width + at];
                                t = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                            }
                            else
                            {
                                t = scan[at];
                                r = screen0 + (phosphor0 - screen0) * t;
                                g = screen1 + (phosphor1 - screen1) * t;
                                b = screen2 + (phosphor2 - screen2) * t;
                            }

                            // The beam is driven brighter than the picture, making up for the dark screen between lines.
                            r *= 1.35f; g *= 1.35f; b *= 1.35f;

                            // Half the beam's height: a thin line in the shadows, most of the way across in the highlights,
                            // always leaving dark screen between lines.
                            float beam = middle * (0.2f + 0.5f * MathF.Sqrt(Clamp01(t)));
                            float across = along * dots, distance = MathF.Sqrt(offset * offset + across * across);
                            float cover = Clamp01(beam - distance + 0.5f);

                            // Between the lines, the screen: black in Original, else the dark color.
                            float br = p.OriginalColors != 0 ? 0f : screen0;
                            float bg = p.OriginalColors != 0 ? 0f : screen1;
                            float bb = p.OriginalColors != 0 ? 0f : screen2;
                            WritePixel(rgba, row + x * 4, br + (r - br) * cover, bg + (g - bg) * cover, bb + (b - bb) * cover);
                        }
                    }
                }
            }
        }
        else
        {
            // Marks (halftone shapes, patterns, glyphs) cover as much of each spot as the tone calls for. On light, they
            // stand for darkness and are drawn in the dark color; light on dark, the reverse.
            float[] marks = p.OriginalColors != 0 ? new float[count] : tone;

            if (p.OriginalColors != 0)
            {
                for (int i = 0; i < count; ++i)
                    marks[i] = 0.2126f * tone[i] + 0.7152f * tone[count + i] + 0.0722f * tone[2 * count + i];
            }

            int cell = p.Cell < 2 ? 2 : p.Cell;
            float cosA = MathF.Cos(p.Angle), sinA = MathF.Sin(p.Angle);

            // 原 C: float *ink = lightOnDark ? light : dark, *paper = lightOnDark ? dark : light;
            float ink0 = p.LightOnDark != 0 ? light0 : dark0;
            float ink1 = p.LightOnDark != 0 ? light1 : dark1;
            float ink2 = p.LightOnDark != 0 ? light2 : dark2;
            float paper0 = p.LightOnDark != 0 ? dark0 : light0;
            float paper1 = p.LightOnDark != 0 ? dark1 : light1;
            float paper2 = p.LightOnDark != 0 ? dark2 : light2;

            // Glyphs: each cell shares one, picked from the cell's average tone, worked out once per cell.
            int gw = p.GlyphWidth < 1 ? 1 : p.GlyphWidth, gh = p.GlyphHeight < 1 ? 1 : p.GlyphHeight;
            int columns = (width + gw - 1) / gw, cellRows = (height + gh - 1) / gh;
            int[]? picked = null;

            if (style == (int)DitherStyle.Glyphs && p.GlyphCount > 0)
            {
                picked = new int[columns * cellRows];

                for (int row = 0; row < cellRows; ++row)
                {
                    for (int column = 0; column < columns; ++column)
                    {
                        float sum = 0;
                        int n = 0;

                        for (int yy = row * gh; yy < (row + 1) * gh && yy < height; ++yy)
                        {
                            for (int xx = column * gw; xx < (column + 1) * gw && xx < width; ++xx)
                            {
                                int i = yy * width + xx;
                                if (alpha[i] != 0) { sum += marks[i]; ++n; }
                            }
                        }

                        float t = n != 0 ? sum / n : 1;
                        float wanted = (p.LightOnDark != 0 ? t : 1 - t) * p.GlyphCoverage[p.GlyphCount - 1];
                        int best = 0;
                        float bestDistance = 2;

                        for (int g = 0; g < p.GlyphCount; ++g)
                        {
                            float d = MathF.Abs(p.GlyphCoverage[g] - wanted);
                            if (d < bestDistance) { bestDistance = d; best = g; }
                        }

                        picked[row * columns + column] = best;
                    }
                }
            }

            // Original colors: marks take the pixel's own color, on black (light on dark) or white.
            float paperOriginal = p.LightOnDark != 0 ? 0.0f : 1.0f;

            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;

                for (int x = 0; x < width; ++x)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;
                    float amount;

                    if (picked != null)
                    {
                        int glyph = picked[(y / gh) * columns + x / gw];
                        amount = p.Glyphs[glyph * gw * gh + (y % gh) * gw + x % gw] / 255.0f;
                    }
                    else if (style == (int)DitherStyle.Patterns)
                    {
                        float t = marks[at];
                        float coverage = p.LightOnDark != 0 ? t : 1 - t;
                        int index = CSemantics.LRoundF(coverage * (PatternCount - 1));
                        amount = (PatternsTable[index * 8 + (y & 7)] >> (7 - (x & 7))) & 1;
                    }
                    else
                    {
                        float fx = x + 0.5f, fy = y + 0.5f;
                        float u = (fx * cosA + fy * sinA) / cell, v = (-fx * sinA + fy * cosA) / cell;
                        u -= MathF.Floor(u) + 0.5f;
                        v -= MathF.Floor(v) + 0.5f;
                        float t = marks[at];
                        amount = (p.LightOnDark != 0 ? t : 1 - t) > Spot(style, u, v) ? 1 : 0;
                    }

                    if (p.OriginalColors != 0)
                    {
                        int s = at * 3;
                        WritePixel(rgba, row + x * 4,
                            paperOriginal + (source[s] - paperOriginal) * amount,
                            paperOriginal + (source[s + 1] - paperOriginal) * amount,
                            paperOriginal + (source[s + 2] - paperOriginal) * amount);
                    }
                    else
                    {
                        WritePixel(rgba, row + x * 4,
                            paper0 + (ink0 - paper0) * amount,
                            paper1 + (ink1 - paper1) * amount,
                            paper2 + (ink2 - paper2) * amount);
                    }
                }
            }
        }

        return 1;
    }

    /// <summary>
    /// 把每个 block×block 方块变成圆点网点，像点阵显示器的亮点。
    /// 直译自 <c>void dither_dots(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：alpha 保持不变；<c>block &lt; 2</c> 时不动任何像素。
    /// ⚠ 原 C 的 <c>(uint8_t)lroundf(px[c] * cover + gap[c] * px[3] / 255.0f * (1 - cover))</c>
    /// 在输入非预乘（通道值 &gt; alpha）时可以算出 &gt; 255 的值，在 C 里是未定义行为，
    /// 这里由 <see cref="CSemantics.U8F(float)"/> 饱和到 255。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8，原地修改（仅 RGB）。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">rgba 每行字节数。</param>
    /// <param name="block">方块边长（像素），&lt; 2 时直接返回。</param>
    /// <param name="gap">点间的底色，直通 sRGB 3 字节。</param>
    public static void Dots(Span<byte> rgba, int width, int height, int stride, int block, ReadOnlySpan<byte> gap)
    {
        if (block < 2) return;

        float radius = block * 0.42f;

        // 原 C 是 (float)block / 2 —— 浮点除法；写成 block / 2 会变成整数除法，奇数边长差 0.5。
        float middle = block / 2f;

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            float dy = y % block + 0.5f - middle;

            for (int x = 0; x < width; ++x)
            {
                int px = row + x * 4;
                if (rgba[px + 3] == 0) continue;

                float dx = x % block + 0.5f - middle;
                float cover = Clamp01(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f);
                if (cover >= 1) continue;

                for (int c = 0; c < 3; ++c)
                    rgba[px + c] = CSemantics.U8F(CSemantics.LRoundF(rgba[px + c] * cover + gap[c] * rgba[px + 3] / 255.0f * (1 - cover)));
            }
        }
    }

    /// <summary>
    /// 把辉光按比例加到像素上，且不超过像素自身的 alpha。
    /// 直译自 <c>void dither_glow(...)</c>。
    /// </summary>
    /// <remarks>
    /// 不变式：alpha 保持不变；<c>a</c> 在进入通道循环前读一次（只读 alpha 通道），
    /// 所以循环内写 RGB 不会互相影响。
    /// </remarks>
    /// <param name="rgba">目标预乘 RGBA8，原地修改（仅 RGB）。</param>
    /// <param name="glow">辉光源，预乘 RGBA8，只读，行距与 <paramref name="stride"/> 相同。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">rgba 每行字节数。</param>
    /// <param name="amount">辉光强度。</param>
    public static void Glow(Span<byte> rgba, ReadOnlySpan<byte> glow, int width, int height, int stride, float amount)
    {
        // 原 C 的 in_bands(height, ...)，串行等价（各 band 只写自己的行）。
        int bands = height < 64 ? 1 : 32, bandSize = (height + bands - 1) / bands;

        for (int band = 0; band < bands; ++band)
        {
            int first = band * bandSize, last = first + bandSize < height ? first + bandSize : height;
            if (first >= last) continue;

            for (int y = first; y < last; ++y)
            {
                int row = y * stride;

                for (int x = 0; x < width * 4; x += 4)
                {
                    float a = rgba[row + x + 3];

                    for (int c = 0; c < 3; ++c)
                    {
                        float v = rgba[row + x + c] + glow[row + x + c] * amount * a / 255.0f;
                        rgba[row + x + c] = CSemantics.U8F(CSemantics.LRoundF(v > a ? a : v));
                    }
                }
            }
        }
    }
}