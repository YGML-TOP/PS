namespace Compositor.Core.Pixels;

/// <summary>
/// Camera Raw「效果」分部：盒式模糊、局部对比度（纹理 / 清晰度）、去雾、辉光、暗角，
/// 以及独立的彩色暗角与影调对比度。
/// </summary>
/// <remarks>
/// <para>直译自 <c>Rendering/AdjustPixels.c</c> 的 <b>L351–L672</b>。
/// （其中 L502–L522 的 <c>adjust_camera_raw_clip_overlay</c> 归属
/// <c>AdjustPixels.CameraRaw.cs</c>，本文件<b>有意跳过</b>，不重复定义。）</para>
///
/// <para><b>与原 C 的等价改写</b>，逐条列出，均不改变数值结果：
/// <list type="number">
///   <item><b><c>box_blur_plane</c>：返回 <c>int</c> → <c>void</c>（本分部的强制改写）。
///   C 版返回 0 表示临时行缓冲 <c>malloc</c> 失败，调用方靠 <c>failed</c> 标志短路后续步骤并整体返回。
///   C# 的数组分配不会返回 null，函数没有可失败点，因此改为 <c>void</c>；同时
///   <c>adjust_camera_raw_effects</c> 里的 <c>int failed</c> 变量、每一处
///   <c>if (!fine || !box_blur_plane(...)) failed = 1;</c> 以及末尾的
///   <c>if (failed) { free(...); return; }</c> 分支<b>全部删除</b>，
///   <c>if (!failed &amp;&amp; clarity != 0)</c> 简化为 <c>if (clarity != 0)</c>。
///   理由：正常路径上 <c>box_blur_plane</c> 恒返回 1，<c>failed</c> 恒为 0，
///   因此删除这些分支后逐位等价。C 版的分配失败是一次「整张图不处理」的静默降级，
///   在 .NET 里改由 <c>OutOfMemoryException</c> 表达，不再需要人为吞掉。</item>
///   <item><c>float *luma = NULL</c> 一类指针 → <c>float[]? luma = null</c>；
///   C 的指针真值判断 <c>if (fine || coarse)</c> → <c>if (fine != null || coarse != null)</c>。
///   「某效果为 0 ⇒ 对应缓冲保持 null ⇒ 该分支不参与运算」这条语义在 C# 中靠 null 检查原样保留。</item>
///   <item><c>size_t</c> 索引 → <c>int</c>（画布上限 30000 像素/边、总 1 亿像素，int 足够）。</item>
///   <item><c>lround</c> / <c>round</c> → <see cref="CSemantics.LRound"/> / <see cref="CSemantics.Round"/>，
///   因为 C# 的 <c>Math.Round</c> 默认是银行家舍入。</item>
///   <item><c>fmax</c> / <c>fmin</c> → <c>Math.Max</c> / <c>Math.Min</c>；
///   <c>fabs</c> → <c>Math.Abs</c>；<c>tanh</c> → <c>Math.Tanh</c>。</item>
///   <item><c>hypot</c> → <c>Math.Hypot</c>。两者都是「误差小于 1 ulp」的正确实现；
///   刻意<b>不</b>改写成 <c>Math.Sqrt(x * x + y * y)</c>，后者末位可能差 1 ulp。</item>
///   <item>C 的 <c>if (!rgba)</c> 空指针检查删除（<c>Span&lt;byte&gt;</c> 不可为 null），其后条件原样保留。</item>
///   <item>C 的输出参数 <c>double *r, *g, *b</c> → C# 的 <c>ref double r, ref double g, ref double b</c>。</item>
/// </list></para>
///
/// <para><b>局部对比度的工作域</b>：<see cref="CameraRawEffects"/> 的纹理 / 清晰度先在
/// <b>直通 sRGB</b> 域算 Rec. 709 亮度再与模糊图比较，<see cref="TonalContrast"/> 同理；
/// 而 <c>adjust_camera_raw</c> 的曝光/对比度在<b>线性光</b>域。混用二者是色彩类 bug 的高发地。</para>
///
/// <para><b>未验证</b>：本仓库尚未安装 .NET SDK，以上代码<b>未经编译、未经测试</b>。</para>
/// </remarks>
public static partial class AdjustPixels
{
    // ══════════════════════ 盒式模糊与半径换算 ══════════════════════

    /// <summary>把越界索引夹回 [0, limit−1]，即边缘钳制采样点。</summary>
    /// <remarks>
    /// 对应 <c>clamped_index</c>。这是全部模糊 / 暗角代码取样的唯一方式：
    /// 负索引夹到 0、超上界夹到 limit−1，于是边界像素被<b>重复计入</b>而非丢弃，
    /// 这正是 C 版边缘不衰减的原因，不要改成「跳过越界」。
    /// </remarks>
    private static int ClampedIndex(int index, int limit)
    {
        if (index < 0) return 0;
        if (index >= limit) return limit - 1;
        return index;
    }

    /// <summary>
    /// 边缘钳制的可分离盒式模糊，窗口半径 <paramref name="radius"/>。
    /// </summary>
    /// <remarks>
    /// 对应 <c>box_blur_plane</c>，<b>返回类型已由 int 改为 void</b>（原因见文件级 remarks 第 1 条）。
    /// 先水平后垂直，两趟都是「先算满窗口再滑动一格」的增量式，<paramref name="dst"/>
    /// 与 <paramref name="src"/> <b>不得重叠</b>。累加用 <c>double</c>、落盘用
    /// <c>(float)</c>，逐行保持一致。半径 &lt; 1 时退化为一次整体拷贝（<c>memcpy</c>）。
    /// </remarks>
    private static void BoxBlurPlane(ReadOnlySpan<float> src, Span<float> dst, int width, int height, int radius)
    {
        if (radius < 1)
        {
            // C: memcpy(dst, src, width * height * sizeof(float))
            src.CopyTo(dst);
            return;
        }

        Span<float> temp = new float[width * height];
        int window = radius * 2 + 1;

        for (int y = 0; y < height; ++y)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; ++k) sum += src[y * width + ClampedIndex(k, width)];
            for (int x = 0; x < width; ++x)
            {
                temp[y * width + x] = (float)(sum / window);
                sum += src[y * width + ClampedIndex(x + radius + 1, width)];
                sum -= src[y * width + ClampedIndex(x - radius, width)];
            }
        }

        for (int x = 0; x < width; ++x)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; ++k) sum += temp[ClampedIndex(k, height) * width + x];
            for (int y = 0; y < height; ++y)
            {
                dst[y * width + x] = (float)(sum / window);
                sum += temp[ClampedIndex(y + radius + 1, height) * width + x];
                sum -= temp[ClampedIndex(y - radius, height) * width + x];
            }
        }
    }

    /// <summary>
    /// 把以「图层像素」为单位的半径换算成实际像素半径，夹到 1…64 后四舍五入。
    /// </summary>
    /// <remarks>
    /// 对应 <c>effects_radius</c>。C 的形参名 <c>base</c> 在 C# 中是关键字，改名为
    /// <c>baseRadius</c>。<paramref name="scale"/> 是「预览像素 / 图层像素」，
    /// 它保证同一个滑块值在 1× 与缩略图下模糊掉同样多的<b>文档</b>范围。
    /// 上下限 1/64 既是性能护栏也是画质护栏，不要放开。
    /// </remarks>
    private static int EffectsRadius(double baseRadius, double scale)
    {
        double radius = baseRadius * (scale > 0 ? scale : 1);
        if (radius < 1) radius = 1;
        if (radius > 64) radius = 64;
        return CSemantics.LRound(radius);
    }

    // ══════════════════════ 去雾 ══════════════════════

    /// <summary>去雾：正值抬对比度并加饱和，负值提亮暗部并压对比。</summary>
    /// <remarks>
    /// 对应 <c>effects_dehaze</c>。分两段：先围绕 0.45 的支点把亮度拉开（<paramref name="amount"/>
    /// 为负时改为提暗部），再用 <c>scale_luminance</c> 把新亮度按回去以保持色相；
    /// 之后重新取一次亮度并加饱和。支点 0.45 处的偏移刻意只对正半轴生效
    /// （<c>0.1 * (d &gt; 0 ? d : 0)</c>），改这个三元会改变负值方向的暗部提亮量。
    /// </remarks>
    private static void EffectsDehaze(ref double r, ref double g, ref double b, double amount)
    {
        double d = amount / 100.0;
        double y = Rec709(r, g, b);
        double contrast = 1.0 + 0.8 * d;
        double pivot = 0.45 - 0.1 * (d > 0 ? d : 0);
        double y2 = CameraClamp(pivot + (y - 0.45) * contrast);
        if (d < 0) y2 = CameraClamp(y2 + (-d) * (1.0 - y2) * 0.45);
        else y2 = CameraClamp(y2 - d * Math.Max(0.0, 0.4 - y2));
        ScaleLuminance(ref r, ref g, ref b, y2);
        y2 = Rec709(r, g, b);
        double sat = 1.0 + 0.7 * d;
        r = CameraClamp(y2 + (r - y2) * sat);
        g = CameraClamp(y2 + (g - y2) * sat);
        b = CameraClamp(y2 + (b - y2) * sat);
    }

    // ══════════════════════ 暗角 ══════════════════════

    /// <summary>
    /// 暗角在 <paramref name="px"/>, <paramref name="py"/> 处的强度：画面中心为 0，越过边缘到 1。
    /// </summary>
    /// <remarks>
    /// 对应 <c>vignette_mask_at</c>。形状在<b>方形</b>（<c>max(|nx|,|ny|)</c>）与<b>圆形</c>
    /// （<c>hypot/sqrt(2)</c>）之间插值，<paramref name="roundness"/> 就是这个插值系数：
    /// 0 = 圆，100 = 方。<paramref name="feather"/> 有 0.05 的下限，
    /// 保证 <c>t</c> 的除数不为 0（羽化 0 会让整圈边缘变成硬切）。
    /// 起点 <c>midpoint * 0.85</c> 使「中点 50」大致落在半径 42.5% 处。
    /// </remarks>
    private static double VignetteMaskAt(double px, double py, double width, double height,
        double midpoint, double roundness, double feather)
    {
        double nx = px / width * 2.0 - 1.0;
        double ny = py / height * 2.0 - 1.0;
        double square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        double circle = Math.Hypot(nx, ny) / Math.Sqrt(2.0);
        double shape = (1.0 - roundness / 100.0) * 0.5;
        double dist = circle + (square - circle) * shape;
        double start = (midpoint / 100.0) * 0.85;
        double soft = feather / 100.0;
        if (soft < 0.05) soft = 0.05;
        double t = (dist - start) / soft;
        t = CameraClamp(t);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>暗角在整数像素坐标 <paramref name="x"/>, <paramref name="y"/> 处的强度（取像素中心）。</summary>
    /// <remarks>对应 <c>vignette_mask</c>。</remarks>
    private static double VignetteMask(int x, int y, int width, int height,
        double midpoint, double roundness, double feather)
    {
        return VignetteMaskAt(x + 0.5, y + 0.5, width, height, midpoint, roundness, feather);
    }

    /// <summary>
    /// 就地对一个像素施加暗角。三种样式：0 高光优先、1 颜色优先、2 叠加（由调用方另行处理）。
    /// </summary>
    /// <remarks>
    /// 对应 <c>effects_vignette</c>。<b>调用顺序约束</b>：必须在该像素的其余分级完成之后、
    /// 写回缓冲之前调用，否则「高光优先」读到的是未调色的亮度，保护范围会错位。
    /// <b>不夹紧输出</b>：变暗分支是纯乘法，通道仍落在 [0,1]（因为输入已夹过），
    /// 但 <paramref name="amount"/> 为正时是「向 1 提亮」，同样不越界——写回时
    /// <see cref="WritePremultiplied"/> 才是最后一道防线。
    /// </remarks>
    private static void EffectsVignette(ref double r, ref double g, ref double b,
        int x, int y, int width, int height,
        double amount, double midpoint, double roundness, double feather, double highlights, int style)
    {
        if (amount == 0 || width == 0 || height == 0) return;
        double mask = VignetteMask(x, y, width, height, midpoint, roundness, feather);
        double effect = (amount / 100.0) * mask;
        // Highlight Priority eases a darkening vignette off bright pixels. The other styles do not.
        if (effect < 0 && style == 0)
        {
            double bright = CameraClamp((Rec709(r, g, b) - 0.45) / 0.55);
            effect *= 1.0 - (highlights / 100.0) * bright;
        }
        if (effect < 0)
        {
            double factor = 1.0 + effect;
            r *= factor; g *= factor; b *= factor;
        }
        else if (effect > 0)
        {
            r = r + (1.0 - r) * effect;
            g = g + (1.0 - g) * effect;
            b = b + (1.0 - b) * effect;
        }
        if (style == 1 && mask > 0)
        {
            double lum = Rec709(r, g, b);
            double sat = 1.0 - 0.75 * mask * Math.Abs(amount / 100.0);
            r = CameraClamp(lum + (r - lum) * sat);
            g = CameraClamp(lum + (g - lum) * sat);
            b = CameraClamp(lum + (b - lum) * sat);
        }
    }

    // ══════════════════════ 独立彩色暗角 ══════════════════════

    /// <summary>
    /// 独立暗角层：按画面坐标而非画布坐标塑形，把像素朝选定边缘色混合。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_colored_vignette</c>。C 的 <c>if (!rgba || ...)</c> 里，
    /// <c>!rgba</c> 是空指针检查，C# 的 <c>Span&lt;byte&gt;</c> 不可为 null，<b>已删去</b>，其余条件保留。
    /// <para><b>两种模式互斥且语义不同</b>：<paramref name="fillsClear"/> 为 0 时只「重新上色」，
    /// 覆盖率（alpha）原样保留，因此完全透明的像素被跳过；为非 0 时把暗角当作一层颜料
    /// 盖上去，透明像素也会被染色，并按 <c>alpha + effect * (1 - alpha)</c> <b>提高 alpha</b>。
    /// 这条不变量（模式 0 绝不改 alpha）不允许合并两个分支。</para>
    /// <para><paramref name="frameX"/> / <paramref name="frameY"/> 是暗角画框左上角在图像坐标系中的位置，
    /// 因此它可以小于 0 或大于画幅——形状按画框算，不按画布算。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="frameX">暗角画框左上角的 X（图像坐标，可越界）。</param>
    /// <param name="frameY">暗角画框左上角的 Y（图像坐标，可越界）。</param>
    /// <param name="frameWidth">画框宽，须 &gt; 0。</param>
    /// <param name="frameHeight">画框高，须 &gt; 0。</param>
    /// <param name="fillsClear">非 0 时连透明像素一起染色并按覆盖率提高 alpha。</param>
    /// <param name="amount">强度，范围 −100…100；≤0 时不处理。</param>
    /// <param name="midpoint">暗角起点，范围 0…100。</param>
    /// <param name="roundness">0 = 圆、100 = 方，范围 0…100。</param>
    /// <param name="feather">羽化宽度，范围 0…100，内部下限 0.05。</param>
    /// <param name="highlights">高光保护强度，范围 0…100，只在变暗方向生效。</param>
    /// <param name="red">边缘色红，0…1。</param>
    /// <param name="green">边缘色绿，0…1。</param>
    /// <param name="blue">边缘色蓝，0…1。</param>
    public static void ColoredVignette(
        Span<byte> rgba, int width, int height, int stride,
        double frameX, double frameY, double frameWidth, double frameHeight, int fillsClear,
        double amount, double midpoint, double roundness, double feather,
        double highlights, double red, double green, double blue)
    {
        // C: if (!rgba || amount <= 0 || ...) return;  —— `!rgba` 在 C# 中不存在，已删去。
        if (amount <= 0 || width == 0 || height == 0 || frameWidth <= 0 || frameHeight <= 0) return;
        double strength = CameraClamp(amount / 100.0);
        red = CameraClamp(red); green = CameraClamp(green); blue = CameraClamp(blue);
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                if (rgba[p + 3] == 0 && fillsClear == 0) continue;
                double mask = VignetteMaskAt(x + 0.5 - frameX, y + 0.5 - frameY, frameWidth, frameHeight,
                    midpoint, roundness, feather);
                if (mask <= 0) continue;
                double alpha = rgba[p + 3] / 255.0;
                double r = 0, g = 0, b = 0, bright = 0;
                if (rgba[p + 3] != 0)
                {
                    r = Math.Min(1.0, rgba[p] / (double)rgba[p + 3]);
                    g = Math.Min(1.0, rgba[p + 1] / (double)rgba[p + 3]);
                    b = Math.Min(1.0, rgba[p + 2] / (double)rgba[p + 3]);
                    bright = CameraClamp((Rec709(r, g, b) - 0.45) / 0.55);
                }
                double effect = strength * mask * (1.0 - (highlights / 100.0) * bright);
                if (fillsClear == 0)
                {
                    // Only the pixels that are there change color; their coverage stays as it was.
                    WritePremultiplied(p, r + (red - r) * effect, g + (green - g) * effect, b + (blue - b) * effect, rgba[p + 3]);
                    continue;
                }
                // The color painted over the pixel at `effect`: an opaque pixel moves toward it, a clear one takes it on.
                double outA = alpha + effect * (1.0 - alpha);
                if (outA <= 0) continue;
                r = (red * effect + r * alpha * (1.0 - effect)) / outA;
                g = (green * effect + g * alpha * (1.0 - effect)) / outA;
                b = (blue * effect + b * alpha * (1.0 - effect)) / outA;
                rgba[p + 3] = (byte)Math.Min(255.0, CSemantics.Round(outA * 255.0));
                WritePremultiplied(p, r, g, b, rgba[p + 3]);
            }
        }
    }

    // ══════════════════════ Camera Raw 效果层 ══════════════════════

    /// <summary>
    /// Camera Raw「效果」层：局部对比度（纹理 / 清晰度）→ 去雾 → 辉光 → 暗角。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_effects</c>。<b>调用顺序约束</b>：必须排在
    /// <see cref="CameraRaw"/>（光效 + 颜色）与 <see cref="CameraRawCurveColor"/>（曲线 / 色彩混合器 / 分级）
    /// <b>之后</b>——局部对比度是拿「调色后的亮度」与「调色后图像的模糊」比较，
    /// 排到前面会去强调本来要被调掉的瑕疵。暗角又必须在该像素其余效果之后，它读到的是最终亮度。
    /// <para><b>各效果的执行次序不可调换</b>：纹理/清晰度改亮度 → 去雾改亮度并加饱和 →
    /// 辉光<b>加</b>颜色（不是提亮）→ 暗角收边。因此辉光不会被后面的去雾削弱，反之亦然。</para>
    /// <para><b>「某效果为 0 ⇒ 对应平面缓冲保持 null」</b>是这套代码的算力闸门：
    /// 纹理/清晰度为 0 时连模糊都不算。因此 <paramref name="scale"/> 只影响实际用到的半径。</para>
    /// <para>alpha 保持不变；完全透明像素不动。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="texture">纹理，范围 −100…100；正的细节半径 1 × <paramref name="scale"/>。</param>
    /// <param name="clarity">清晰度，范围 −100…100；正的细节半径 4 × <paramref name="scale"/>。</param>
    /// <param name="dehaze">去雾，范围 −100…100。</param>
    /// <param name="glow">辉光强度，范围 0…100；≤0 时整条辉光路径不执行。</param>
    /// <param name="glowStyle">0 扩散 / 1 柔光 / 2 光晕（halation）。</param>
    /// <param name="glowRange">辉光阈值，范围 0…100，越大起辉越晚。</param>
    /// <param name="glowSpread">扩散范围，范围 0…100，放大模糊半径。</param>
    /// <param name="glowWarmth">辉光暖度，范围 −100…100。</param>
    /// <param name="vignetteAmount">暗角强度，范围 −100…100。</param>
    /// <param name="vignetteMidpoint">暗角起点，范围 0…100。</param>
    /// <param name="vignetteRoundness">0 = 圆、100 = 方。</param>
    /// <param name="vignetteFeather">羽化宽度，范围 0…100。</param>
    /// <param name="vignetteHighlights">暗角高光保护，范围 0…100。</param>
    /// <param name="vignetteStyle">0 高光优先 / 1 颜色优先 / 2 叠加。</param>
    /// <param name="scale">预览像素 / 图层像素，用于让半径与整幅渲染一致。</param>
    public static void CameraRawEffects(
        Span<byte> rgba, int width, int height, int stride,
        double texture, double clarity, double dehaze,
        double glow, int glowStyle, double glowRange, double glowSpread, double glowWarmth,
        double vignetteAmount, double vignetteMidpoint, double vignetteRoundness,
        double vignetteFeather, double vignetteHighlights, int vignetteStyle,
        double scale)
    {
        if (width == 0 || height == 0) return;
        if (texture == 0 && clarity == 0 && dehaze == 0 && !(glow > 0) && vignetteAmount == 0) return;

        int count = width * height;
        // C 的 `float *luma = NULL` 等：null 表示该效果未启用，对应的平面不参与运算。
        float[]? luma = null, fine = null, coarse = null, glowPlane = null;
        int glowRadius = 1;

        if (texture != 0 || clarity != 0 || glow > 0)
        {
            luma = new float[count];
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) { luma[y * width + x] = 0; continue; }
                    double r = Math.Min(1.0, rgba[p] / alpha);
                    double g = Math.Min(1.0, rgba[p + 1] / alpha);
                    double b = Math.Min(1.0, rgba[p + 2] / alpha);
                    luma[y * width + x] = (float)Rec709(r, g, b);
                }
            }

            if (texture != 0)
            {
                fine = new float[count];
                BoxBlurPlane(luma, fine, width, height, EffectsRadius(1, scale));
            }
            if (clarity != 0)
            {
                coarse = new float[count];
                BoxBlurPlane(luma, coarse, width, height, EffectsRadius(4, scale));
            }
            if (glow > 0)
            {
                double spread = glowSpread / 100.0;
                // C 的局部变量名是 `base`，C# 中 `base` 是关键字，改名 baseRadius。
                double baseRadius = glowStyle == 1 ? 2.0 : 5.0;
                double widened = baseRadius * (1.0 + spread);
                if (widened < 1) widened = 1;
                glowRadius = EffectsRadius(widened, scale);
                float threshold = (float)(0.55 + 0.4 * (glowRange / 100.0));
                glowPlane = new float[count];
                var source = new float[count];
                float denom = 1.0f - threshold;
                if (denom < 0.05f) denom = 0.05f;
                for (int i = 0; i < count; ++i)
                {
                    float t = (luma[i] - threshold) / denom;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    source[i] = t;
                }
                BoxBlurPlane(source, glowPlane, width, height, glowRadius);
            }
        }
        // C 的 `if (failed) { free(...); return; }` 已随 box_blur_plane 改 void 一并删除（见文件级 remarks）。

        double warmth = glowWarmth / 100.0;
        double glowRed, glowGreen, glowBlue, glowGain;
        if (glowStyle == 2)
        {
            // Halation's fringe is red. Warmth pushes it further that way, rather than toward yellow or blue.
            glowRed = 1;
            glowGreen = 0.35 - 0.3 * warmth;
            glowBlue = 0.2 - 0.2 * warmth;
            glowGain = 1;
        }
        else
        {
            glowRed = 0.75 + 0.25 * warmth;
            glowGreen = 0.6 + 0.2 * warmth;
            glowBlue = 0.75 - 0.6 * warmth;
            glowGain = glowStyle == 1 ? 1.4 : 1;
        }

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                int index = y * width + x;
                double r = Math.Min(1.0, rgba[p] / alpha);
                double g = Math.Min(1.0, rgba[p + 1] / alpha);
                double b = Math.Min(1.0, rgba[p + 2] / alpha);
                if (fine != null || coarse != null)
                {
                    double tone = Rec709(r, g, b);
                    double detail = 0;
                    if (fine != null) detail += (texture / 100.0) * (tone - fine[index]);
                    if (coarse != null) detail += (clarity / 100.0) * (tone - coarse[index]);
                    if (detail != 0) ScaleLuminance(ref r, ref g, ref b, CameraClamp(tone + detail));
                }
                if (dehaze != 0) EffectsDehaze(ref r, ref g, ref b, dehaze);
                if (glowPlane != null && glow > 0)
                {
                    double add = glowPlane[index] * (glow / 100.0) * glowGain;
                    r = CameraClamp(r + add * glowRed);
                    g = CameraClamp(g + add * glowGreen);
                    b = CameraClamp(b + add * glowBlue);
                }
                EffectsVignette(ref r, ref g, ref b, x, y, width, height, vignetteAmount, vignetteMidpoint,
                    vignetteRoundness, vignetteFeather, vignetteHighlights, vignetteStyle);
                WritePremultiplied(p, r, g, b, alpha);
            }
        }
    }

    // ══════════════════════ 影调对比度 ══════════════════════

    /// <summary>把一个值平滑地限制在 <paramref name="low"/>…<paramref name="high"/> 之间，返回 0…1 的三次平滑步。</summary>
    private static double TonalSmooth(double low, double high, double value)
    {
        double t = CameraClamp((value - low) / (high - low));
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>
    /// 影调对比度：以一张同尺寸的模糊图为基准，按阴影 / 中间调 / 高光三段分别增益局部对比。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_tonal_contrast</c>。<b>调用顺序约束</b>：
    /// <paramref name="blurred"/> 必须是<b>同一张、同一时刻</b>的图像在所选细节半径上的模糊，
    /// 也就是说它要在本函数被调用之前、用与 <paramref name="rgba"/> 相同的 <paramref name="stride"/>
    /// 对应的内容生成；错帧会让高光权重整体偏移。
    /// 两个缓冲的行距不同（<paramref name="stride"/> 与 <paramref name="blurredStride"/>），
    /// 本函数逐行各自取址，允许 <paramref name="blurred"/> 来自另一块布局不同的缓冲。
    /// <para>分段权重由<b>基准图的</b>亮度决定（原图与模糊图之差 <c>detail</c> 只用来定幅度），
    /// 这让「往哪个方向拉」在细节区保持稳定；<c>tanh</c> 与 <c>4 * lum * (1 - lum)</c>
    /// 分别限制极端差异与只作用于中间调。</para>
    /// <para>alpha 保持不变；原图或基准图任一为全透明的像素被跳过。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="blurred">同一张图像的模糊版，预乘 RGBA8，只读。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">原图每行字节数。</param>
    /// <param name="blurredStride">模糊图每行字节数，可与 <paramref name="stride"/> 不同。</param>
    /// <param name="amount">总量，范围 0…50（内部除以 50 归一）；≤0 时不处理。</param>
    /// <param name="shadows">阴影段增益，范围 −100…100。</param>
    /// <param name="midtones">中间调段增益，范围 −100…100。</param>
    /// <param name="highlights">高光段增益，范围 −100…100。</param>
    public static void TonalContrast(
        Span<byte> rgba, ReadOnlySpan<byte> blurred, int width, int height,
        int stride, int blurredStride, double amount,
        double shadows, double midtones, double highlights)
    {
        if (amount <= 0 || (shadows == 0 && midtones == 0 && highlights == 0)) return;
        double strength = amount / 50.0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            int baseRow = y * blurredStride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                int bp = baseRow + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0 || blurred[bp + 3] == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha);
                double g = Math.Min(1.0, rgba[p + 1] / alpha);
                double b = Math.Min(1.0, rgba[p + 2] / alpha);
                double lum = Rec709(r, g, b);
                double baseLum = Rec709(Math.Min(1.0, blurred[bp] / (double)blurred[bp + 3]),
                    Math.Min(1.0, blurred[bp + 1] / (double)blurred[bp + 3]),
                    Math.Min(1.0, blurred[bp + 2] / (double)blurred[bp + 3]));
                double shadowWeight = 1.0 - TonalSmooth(0.15, 0.5, baseLum);
                double highlightWeight = TonalSmooth(0.5, 0.85, baseLum);
                double midtoneWeight = 1.0 - shadowWeight - highlightWeight;
                double weight = (shadows * shadowWeight + midtones * midtoneWeight +
                                 highlights * highlightWeight) / 100.0;
                double detail = lum - baseLum;
                double delta = 0.18 * Math.Tanh(detail * 6.0) * weight * strength * (4.0 * lum * (1.0 - lum));
                WritePremultiplied(p, CameraClamp(r + delta), CameraClamp(g + delta),
                    CameraClamp(b + delta), alpha);
            }
        }
    }
}
