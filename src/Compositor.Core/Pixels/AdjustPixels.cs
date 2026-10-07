namespace Compositor.Core.Pixels;

/// <summary>
/// 调整层像素算法全集：渐变映射、胶片颗粒、黑白、色彩平衡，以及 Camera Raw 的白平衡/曝光/色调曲线/自然饱和度。
/// </summary>
/// <remarks>
/// <para><b>来源</b>：逐行直译 <c>Rendering/AdjustPixels.c</c>（1157 行，13 个公开函数 + 30 个静态辅助函数）。
/// 原始 C 文件过大，此处按功能拆成 4 个 <c>partial</c> 分部：
/// <list type="bullet">
///   <item>本文件 —— 渐变映射 / 颗粒 / 预乘钳制 / 黑白 / 色彩平衡 + 噪声网格辅助</item>
///   <item><c>AdjustPixels.CameraRaw.cs</c> —— 色彩空间转换、影调分级、基础调色、剪切叠加</item>
///   <item><c>AdjustPixels.Effects.cs</c> —— 盒式模糊、辉光、去雾、暗角、局部对比度</item>
///   <item><c>AdjustPixels.Curve.cs</c> —— 曲线/色彩混合器/分级、锐化、降噪、光学、相机校准</item>
/// </list></para>
///
/// <para><b>全局不变量（铁律 2）</b>：所有公开函数都作用于<b>预乘 RGBA8</b>。
/// 内部一律「反预乘 → 在直通域运算 → 按 alpha 乘回」，
/// 写回一律经 <see cref="WritePremultiplied"/>，因此 RGB ≤ alpha 恒成立。
/// 完全透明像素（alpha=0）一律跳过不动。</para>
///
/// <para><b>全局等价改写</b>：仅以下四类，均不改变数值结果：
/// <list type="number">
///   <item><c>size_t</c> 索引 → <c>int</c>（画布上限 30000 像素/边、总 1 亿像素，int 足够）</item>
///   <item>C 的输出指针（<c>double *r</c> 等）→ C# 的 <c>ref</c> / <c>out</c> 参数</item>
///   <item><c>malloc</c>/<c>free</c> → 定长数组（无失败分支；C 的分配失败提前返回在 C# 中不存在）</item>
///   <item><c>lround</c>/<c>round</c> → <see cref="CSemantics"/>，因为 C# 的 <c>Math.Round</c> 默认是银行家舍入</item>
/// </list></para>
///
/// <para><b>未验证</b>：本仓库尚未安装 .NET SDK，以上代码<b>未经编译、未经测试</b>。</para>
/// </remarks>
public static partial class AdjustPixels
{
    // ══════════════════════ 调整层基础：渐变映射 / 颗粒 / 黑白 / 色彩平衡 ══════════════════════

    /// <summary>
    /// 渐变映射：按每个像素的亮度从 256×3 的直通 sRGB 调色板取色。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_gradient_map</c>。调色板<b>最暗项在索引 0</b>，共 768 字节（256 项 × RGB）。
    /// alpha 原样保持；完全透明像素不动。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="table">256×3 直通 sRGB 调色板，最暗项在索引 0。</param>
    public static void GradientMap(Span<byte> rgba, int width, int height, int stride, ReadOnlySpan<byte> table)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                uint a = rgba[p + 3];
                if (a == 0) continue;

                uint r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                if (a < 255)
                {
                    r = (r * 255u + a / 2) / a;
                    g = (g * 255u + a / 2) / a;
                    b = (b * 255u + a / 2) / a;
                    if (r > 255) r = 255;
                    if (g > 255) g = 255;
                    if (b > 255) b = 255;
                }

                uint level = (2126u * r + 7152u * g + 722u * b + 5000u) / 10000u;

                // 原 C: uint32_t color = (level > 255 ? 255 : level) * 3;
                // 取 int 而非 uint：level 恒 ≤ 255（r/g/b 已被上面的反预乘夹到 ≤255，权重和恰为 10000），
                // 故 color ≤ 765，索引最大 767 < table.Length(768)，转换无损。
                int color = (int)((level > 255u ? 255u : level) * 3u);

                rgba[p] = (byte)((table[color] * a + 127u) / 255u);
                rgba[p + 1] = (byte)((table[color + 1] * a + 127u) / 255u);
                rgba[p + 2] = (byte)((table[color + 2] * a + 127u) / 255u);
            }
        }
    }

    /// <summary>
    /// 胶片颗粒：对三通道施加<b>同一个</b>亮度变化，在中间调最强。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_grain</c>。<paramref name="seed"/> 与文档坐标共同决定噪声图案，
    /// 因此同一张图的同一块区域在任何时候处理都得到相同颗粒——这是可撤销编辑的前提。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="amount">颗粒强度 0–100，≤0 时不处理。</param>
    /// <param name="size">颗粒尺寸（文档单位），≤0 时按 1 处理。</param>
    /// <param name="roughness">0–100，加入更细小的颗粒；其尺寸仍随 <paramref name="size"/> 缩放。</param>
    /// <param name="seed">噪声种子。</param>
    /// <param name="originX">文档坐标原点的 X 分量。</param>
    /// <param name="originY">文档坐标原点的 Y 分量。</param>
    /// <param name="unitsPerPixel">每像素对应的文档单位数；≤0 时不处理。</param>
    public static void Grain(
        Span<byte> rgba, int width, int height, int stride,
        double amount, double size, double roughness, uint seed,
        double originX, double originY, double unitsPerPixel)
    {
        if (!(amount > 0) || !(unitsPerPixel > 0)) return;
        if (!(size > 0)) size = 1;

        float strength = (float)(amount > 100 ? 1.0 : amount / 100.0) * 0.35f * 255.0f;
        float rough = (float)(roughness < 0 ? 0.0 : roughness > 100 ? 1.0 : roughness / 100.0);
        uint fineSeed = Mix32(seed ^ 0xA511E9B3u);

        // Roughness adds smaller, less regular particles, as in Photoshop, but their size remains
        // proportional to the Size control instead of collapsing to fixed one-pixel noise.
        double detailSize = Math.Max(0.5, size * 0.35);

        for (int y = 0; y < height; y++)
        {
            double v = originY + (y + 0.5) * unitsPerPixel;
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                uint a = rgba[p + 3];
                if (a == 0) continue;

                double u = originX + (x + 0.5) * unitsPerPixel;
                float smooth = GrainField(u, v, size, seed);
                float fine = GrainField(u, v, detailSize, fineSeed);
                float noise = smooth + (fine - smooth) * rough;

                float unpremultiply = a == 255 ? 1.0f : 255.0f / a;
                float r = rgba[p] * unpremultiply, g = rgba[p + 1] * unpremultiply, b = rgba[p + 2] * unpremultiply;
                float level = (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255.0f;
                if (level > 1) level = 1;

                // Film grain shows most in the midtones.
                float delta = noise * strength * (0.4f + 2.4f * level * (1.0f - level));
                float coverage = a / 255.0f;

                rgba[p] = CSemantics.U8F(Clamp255(r + delta) * coverage + 0.5f);
                rgba[p + 1] = CSemantics.U8F(Clamp255(g + delta) * coverage + 0.5f);
                rgba[p + 2] = CSemantics.U8F(Clamp255(b + delta) * coverage + 0.5f);
            }
        }
    }

    /// <summary>
    /// 把每个像素的 RGB 钳到不超过其 alpha —— <b>预乘不变量的强制手段</b>。
    /// </summary>
    /// <remarks>
    /// 对应 <c>rgba_clamp_premultiplied</c>。重采样滤波（尤其 Lanczos）会产生振铃，
    /// 使预乘通道超过 alpha；凡是跑过重采样的缓冲都必须过一遍本函数，
    /// 否则下游任何按「RGB ≤ alpha」做的假设都会失效。
    /// alpha 本身不变。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="count">像素个数。</param>
    public static void ClampPremultiplied(Span<byte> rgba, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int p = i * 4;
            byte a = rgba[p + 3];
            if (rgba[p] > a) rgba[p] = a;
            if (rgba[p + 1] > a) rgba[p + 1] = a;
            if (rgba[p + 2] > a) rgba[p + 2] = a;
        }
    }

    /// <summary>
    /// 黑白（Black &amp; White），按 Photoshop 的六段权重法把彩色转为灰度，可选单色调着色。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_black_white</c>。颜色被拆成：灰度底（min）、次色（两个最亮通道之间的量）、
    /// 原色（最亮通道），三者各占六段权重之一。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="weights">
    /// 6 个权重，顺序为 <b>红、黄、绿、青、蓝、品红</b>，取值 0–1（Photoshop 的 40% 即 0.4）。
    /// 纯红在默认 0.4 权重下得到 40% 灰度，与 Photoshop 一致。
    /// </param>
    /// <param name="tint">非 0 时启用单色调着色。</param>
    /// <param name="tintHue">单色调色相（度）。</param>
    /// <param name="tintSaturation">单色调饱和度 0–1。</param>
    public static void BlackWhite(
        Span<byte> rgba, int width, int height, int stride,
        ReadOnlySpan<float> weights, int tint, double tintHue, double tintSaturation)
    {
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                float alpha = rgba[p + 3];
                if (alpha == 0) continue;

                float r = rgba[p] * 255.0f / alpha, g = rgba[p + 1] * 255.0f / alpha, b = rgba[p + 2] * 255.0f / alpha;
                r = MathF.Min(255.0f, r) / 255.0f; g = MathF.Min(255.0f, g) / 255.0f; b = MathF.Min(255.0f, b) / 255.0f;

                float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
                float md = r + g + b - mx - mn;

                // weights: 0 red, 1 yellow, 2 green, 3 cyan, 4 blue, 5 magenta
                int primary, secondary;
                if (mx == r) { primary = 0; secondary = (g >= b) ? 1 : 5; }
                else if (mx == g) { primary = 2; secondary = (r >= b) ? 1 : 3; }
                else { primary = 4; secondary = (g >= r) ? 3 : 5; }

                float gray = mn + (md - mn) * weights[secondary] + (mx - md) * weights[primary];
                gray = MathF.Min(1.0f, MathF.Max(0.0f, gray));

                double outR = gray, outG = gray, outB = gray;
                if (tint != 0 && tintSaturation > 0)
                {
                    // The gray becomes the lightness of a color at the chosen hue.
                    double c = (1.0 - Math.Abs(2.0 * gray - 1.0)) * tintSaturation;
                    double hp = tintHue % 360.0 / 60.0;
                    double xx = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
                    double r1 = 0, g1 = 0, b1 = 0;
                    if (hp < 1) { r1 = c; g1 = xx; }
                    else if (hp < 2) { r1 = xx; g1 = c; }
                    else if (hp < 3) { g1 = c; b1 = xx; }
                    else if (hp < 4) { g1 = xx; b1 = c; }
                    else if (hp < 5) { r1 = xx; b1 = c; }
                    else { r1 = c; b1 = xx; }
                    double m = gray - c / 2.0;
                    outR = Math.Min(1.0, Math.Max(0.0, r1 + m));
                    outG = Math.Min(1.0, Math.Max(0.0, g1 + m));
                    outB = Math.Min(1.0, Math.Max(0.0, b1 + m));
                }

                rgba[p] = (byte)MathF.Min(alpha, MathF.Max(0.0f, CSemantics.RoundF((float)outR * alpha)));
                rgba[p + 1] = (byte)MathF.Min(alpha, MathF.Max(0.0f, CSemantics.RoundF((float)outG * alpha)));
                rgba[p + 2] = (byte)MathF.Min(alpha, MathF.Max(0.0f, CSemantics.RoundF((float)outB * alpha)));
            }
        }
    }

    /// <summary>
    /// 色彩平衡：按阴影/中间调/高光三段各偏移青-红、洋红-绿、黄-蓝。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_color_balance</c>。三段权重是平滑过渡的三条重叠曲线（见 <see cref="TonalWeights"/>），
    /// 偏移随色调淡入淡出而非在阈值处跳变，因此不会产生色带。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="shadows">阴影段的 3 个偏移：青/红、洋红/绿、黄/蓝，范围 −1…1。</param>
    /// <param name="midtones">中间调段的 3 个偏移，范围 −1…1。</param>
    /// <param name="highlights">高光段的 3 个偏移，范围 −1…1。</param>
    /// <param name="preserveLuminosity">非 0 时把原始亮度还原回去，只让颜色移动。</param>
    public static void ColorBalance(
        Span<byte> rgba, int width, int height, int stride,
        ReadOnlySpan<float> shadows, ReadOnlySpan<float> midtones, ReadOnlySpan<float> highlights,
        int preserveLuminosity)
    {
        // CA2014：stackalloc 提到循环外。三格在下一次迭代用到之前必被第 271 行那个 for 写满 0..2，
        // 所以提到外面不改变任何一次迭代读到的值，只是省掉每像素一次的栈槽重分配。
        Span<float> c = stackalloc float[3];

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                float alpha = rgba[p + 3];
                if (alpha == 0) continue;

                for (int i = 0; i < 3; ++i) c[i] = MathF.Min(255.0f, rgba[p + i] * 255.0f / alpha) / 255.0f;

                float before = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];

                for (int i = 0; i < 3; ++i)
                {
                    TonalWeights(c[i], out float s, out float m, out float h);
                    c[i] += shadows[i] * s + midtones[i] * m + highlights[i] * h;
                    c[i] = MathF.Min(1.0f, MathF.Max(0.0f, c[i]));
                }

                if (preserveLuminosity != 0)
                {
                    float after = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
                    if (after > 0.0001f)
                    {
                        float ratio = before / after;
                        for (int i = 0; i < 3; ++i) c[i] = MathF.Min(1.0f, MathF.Max(0.0f, c[i] * ratio));
                    }
                }

                for (int i = 0; i < 3; ++i)
                    rgba[p + i] = (byte)MathF.Min(alpha, MathF.Max(0.0f, CSemantics.RoundF(c[i] * alpha)));
            }
        }
    }

    // ══════════════════════ 静态辅助（与原 C 的 static inline 一一对应）══════════════════════

    /// <summary>良混合的 32 位哈希（原 C 的 <c>mix32</c>）。</summary>
    private static uint Mix32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>
    /// 整数格点上的 −1…1 值，由格点坐标与种子唯一确定。
    /// 两个均匀分布的半值相加得到三角分布 —— 比平铺噪声更接近真实胶片颗粒。
    /// </summary>
    private static float Lattice(long ix, long iy, uint seed)
    {
        uint h = Mix32(unchecked((uint)ix * 0x9E3779B1u) ^ Mix32(unchecked((uint)iy * 0x85EBCA77u) ^ seed));
        return (h & 0xFFFFu) / 65535.0f + (h >> 16) / 65535.0f - 1.0f;
    }

    /// <summary>
    /// 平滑的带种子噪声，特征尺寸为 <paramref name="scale"/> 个文档像素。
    /// 粗纹与细纹都相对请求的颗粒尺寸，因此任何 Roughness 下 Size 都保持可见。
    /// </summary>
    private static float GrainField(double u, double v, double scale, uint seed)
    {
        double cellX = Math.Floor(u / scale), cellY = Math.Floor(v / scale);
        float tx = (float)(u / scale - cellX), ty = (float)(v / scale - cellY);
        tx = tx * tx * (3.0f - 2.0f * tx);
        ty = ty * ty * (3.0f - 2.0f * ty);
        long ix = (long)cellX, iy = (long)cellY;

        float n00 = Lattice(ix, iy, seed), n10 = Lattice(ix + 1, iy, seed);
        float n01 = Lattice(ix, iy + 1, seed), n11 = Lattice(ix + 1, iy + 1, seed);
        float top = n00 + (n10 - n00) * tx, bottom = n01 + (n11 - n01) * tx;

        // Blending neighboring lattice values narrows the spread; restore approximately its original range.
        return (top + (bottom - top) * ty) * 1.6f;
    }

    /// <summary>原 C 的 <c>clamp255</c>（注意与 <see cref="CameraClamp"/> 不同：后者夹到 0…1）。</summary>
    private static float Clamp255(float value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>
    /// 一个色调属于阴影 / 中间调 / 高光的程度：三条在整个范围内总和约为 1 的重叠曲线，
    /// 使偏移平滑淡入淡出，而不是在阈值处产生色带。
    /// </summary>
    private static void TonalWeights(float v, out float shadow, out float mid, out float highlight)
    {
        const float a = 0.25f, b = 0.333f, scale = 0.7f;
        float s = (v - b) / -a + 0.5f;
        float h = (v + b - 1.0f) / a + 0.5f;
        s = MathF.Min(1.0f, MathF.Max(0.0f, s));
        h = MathF.Min(1.0f, MathF.Max(0.0f, h));
        float m1 = MathF.Min(1.0f, MathF.Max(0.0f, (v - b) / a + 0.5f));
        float m2 = MathF.Min(1.0f, MathF.Max(0.0f, (v + b - 1.0f) / -a + 0.5f));
        shadow = s * scale;
        mid = m1 * m2 * scale;
        highlight = h * scale;
    }
}