namespace Compositor.Core.Pixels;

/// <summary>
/// 曲线分部：LUT 采样、RGB↔HSL、色彩混合器、颜色分级、锐化与蒙版、降噪、光学（去紫边 / 色差 / 暗角校正 / 镜头畸变）、相机校准。
/// </summary>
/// <remarks>
/// <para>直译自 <c>Rendering/AdjustPixels.c</c> 的 <b>L674–L1157</b>（文件最后一个函数）。</para>
///
/// <para><b>与原 C 的等价改写</b>，逐条列出，均不改变数值结果：
/// <list type="number">
///   <item><c>size_t</c> 索引 → <c>int</c>；<c>int64_t</c> → <c>long</c>（<c>sharpen_edge_at</c> 的采样坐标）。</item>
///   <item><c>const float *</c> → <c>ReadOnlySpan&lt;float&gt;</c>；<c>const uint8_t *</c> → <c>ReadOnlySpan&lt;byte&gt;</c>；
///   <c>uint8_t *</c> → <c>Span&lt;byte&gt;</c>。</item>
///   <item><c>double *r, *g, *b</c> → <c>ref double</c>；<c>double *h, *s, *l</c> → <c>out double</c>
///   （三者都是纯输出，语义与 C 一致）。</item>
///   <item><c>static const double mixer_centers[8]</c> → <c>private static readonly double[] MixerCenters</c>。</item>
///   <item><c>malloc</c> / <c>free</c> / <c>memcpy</c> → <c>new T[n]</c> 与 <c>Span.CopyTo</c>；
///   <c>optics_chromatic</c> 的整幅快照按行拷贝，行内只取 <c>width * 4</c> 字节（行距填充字节不参与）。</item>
///   <item><c>lround</c> → <see cref="CSemantics.LRound"/>，<c>round</c> → <see cref="CSemantics.Round"/>。</item>
///   <item><c>fmod(x, 6.0)</c> → <c>x % 6.0</c>，<b>乘法必须留在 fmod 之外</b>（见 <see cref="PixelHueDeg"/>）。
///   C 的 <c>fmod</c> 保留左操作数的符号，<c>(g - b)</c> 为负时结果为负，
///   随后的 <c>hue += 360.0</c> 正是为此存在——C# 的 <c>%</c> 对 <c>double</c> 同样是截断取余，符号一致。</item>
///   <item><c>hypot</c> → <c>Math.Hypot</c>（见 <c>AdjustPixels.Effects.cs</c> 同名说明）。</item>
///   <item><c>box_blur_plane</c> 已在上一个分部改为 <c>void</c>，本文件调用处不再有失败分支。</item>
///   <item><c>double weights[4] = {…}</c> → <c>Span&lt;double&gt;</c> 栈上数组 + 逐项赋值（<c>stackalloc</c> 初始化器在
///   嵌套循环里可读性差，赋值结果完全相同）。</item>
///   <item><c>double cr, cg, cb;</c> → 显式初值 0：<c>HslToRgb</c> 的每条路径都必然覆写三者，
///   初值只为让 C# 的定点分析无歧义，数值无影响。</item>
/// </list></para>
///
/// <para><b>未验证</b>：本仓库尚未安装 .NET SDK，以上代码<b>未经编译、未经测试</b>。</para>
/// </remarks>
public static partial class AdjustPixels
{
    // ══════════════════════ LUT 与色彩空间转换原语 ══════════════════════

    /// <summary>在 256 项 LUT 上做线性插值采样，入参 0…1。</summary>
    /// <remarks>
    /// 对应 <c>lut_at</c>。两个 8 位之间的中间值只能靠插值得到，所以末端特意不外推
    /// （<c>hi</c> 在 255 处夹住）。插值系数 <c>t</c> 是 double，但两个表项之差
    /// <b>在 float 里先算完</b>再提升——C# 与 C 的提升时机相同，不是精度损失。
    /// </remarks>
    private static double LutAt(ReadOnlySpan<float> lut, double value)
    {
        double scaled = CameraClamp(value) * 255.0;
        int lo = (int)scaled;
        int hi = lo < 255 ? lo + 1 : 255;
        double t = scaled - lo;
        return lut[lo] + (lut[hi] - lut[lo]) * t;
    }

    /// <summary>直通 RGB → HSL。<paramref name="h"/> 归一到 0…1（不是角度）。</summary>
    /// <remarks>
    /// 对应 <c>rgb_to_hsl</c>。近灰（色度 &lt; 1e-6）直接给 <c>h = s = 0</c>，这是刻意的：
    /// 让灰色不因浮点噪声被分到任意一个色相家族。
    /// <para>⚠ <c>fmod((g - b) / d, 6.0)</c> 的括号位置必须原样保留：
    /// 结果先落在 [0,6)（可为负）再除以 6 得到 [0,1)；
    /// 写成 <c>(60.0 * (g - b) / d) % 6.0</c> 会落在 [0,6)，色相完全错。</para>
    /// </remarks>
    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        l = (maxc + minc) * 0.5;
        double d = maxc - minc;
        if (d < 1e-6) { h = 0; s = 0; return; }
        s = d / (1.0 - Math.Abs(2.0 * l - 1.0));
        if (maxc == r) h = (g - b) / d % 6.0;
        else if (maxc == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        h /= 6.0;
        if (h < 0) h += 1;
    }

    /// <summary>HSL 的标准色相 → RGB 单通道辅助（<paramref name="p"/> 为暗端、<paramref name="q"/> 为亮端）。</summary>
    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    /// <summary>HSL → 直通 RGB。饱和度 &lt; 1e-6 时按无彩色处理，三个通道都取亮度。</summary>
    private static void HslToRgb(double h, double s, double l, ref double r, ref double g, ref double b)
    {
        if (s <= 1e-6) { r = g = b = l; return; }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3);
    }

    /// <summary>归一化色相（0…1）之间的环形距离，落在 0…0.5。</summary>
    /// <remarks>对应 <c>circular_distance</c>。红与青相距 0.5，不做归一化，因此调用方要自己除以半宽。</remarks>
    private static double CircularDistance(double a, double b)
    {
        double d = Math.Abs(a - b);
        return d > 0.5 ? 1 - d : d;
    }

    /// <summary>色彩混合器八个色相家族的中心，归一化到 0…1（红 0、橙 30°、黄 60°、绿 120°、青 180°、蓝 240°、紫 270°、洋红 300°）。</summary>
    private static readonly double[] MixerCenters =
    {
        0, 30.0 / 360, 60.0 / 360, 120.0 / 360, 180.0 / 360, 240.0 / 360, 270.0 / 360, 300.0 / 360
    };

    /// <summary>某个点色对当前 (h, s, l) 的影响程度，三个方向的半宽（<c>point[6..8]</c>）独立可调。</summary>
    /// <remarks>
    /// 对应 <c>point_weight</c>。三个半宽都有 0.01 的下限，保证除数不为 0；
    /// 任一方向落在范围外就整点丢弃（返回 0）而不是只扣那一个方向，
    /// 所以「一个方向超出 = 这个点色与此像素无关」是本函数的核心语义。
    /// </remarks>
    private static double PointWeight(double h, double s, double l, ReadOnlySpan<float> point)
    {
        double hueHalf = point[6] > 0.01f ? point[6] : 0.01f;
        double satHalf = point[7] > 0.01f ? point[7] : 0.01f;
        double lumHalf = point[8] > 0.01f ? point[8] : 0.01f;
        double hueW = 1 - CircularDistance(h, point[0]) / hueHalf;
        double satW = 1 - Math.Abs(s - point[1]) / satHalf;
        double lumW = 1 - Math.Abs(l - point[2]) / lumHalf;
        if (hueW < 0 || satW < 0 || lumW < 0) return 0;
        return hueW * satW * lumW;
    }

    // ══════════════════════ 曲线 / 色彩混合器 / 颜色分级 ══════════════════════

    /// <summary>
    /// 色调曲线 → 通道曲线 → 色彩混合器 → 点色（范围选择）→ 颜色分级四轮（阴影 / 中间调 / 高光 / 全局）。
    /// </summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_curve_color</c>。<b>调用顺序约束</b>：必须排在
    /// <see cref="CameraRaw"/> <b>之后</b>（它要作用在已调色的结果上），并排在
    /// <see cref="CameraRawEffects"/> <b>之前</b>（局部对比度、去雾、暗角要在最终颜色上判断）。
    /// 内部次序同样不可换：混合器先定色相，<b>点色的采样也用被混合器改过之后的 (h, s, l)</b>，
    /// 所以「改色相」和「限定范围」能叠加。
    /// <para><c>refineSaturation</c> 是一个<b>双极</b>旋钮：&lt; 0 时把色相不动、只沿曲线改亮度
    /// （−100 即全改亮度），&gt; 0 时在曲线结果上加彩。</para>
    /// <para>四轮分级的权重（<c>weights[3] = 1</c> 是全局轮）会按总和归一化，
    /// 因此<b>只有非零轮参与运算</b>时颜色不被稀释；<paramref name="balance"/> 把阴影轮与高光轮的
    /// 分界点上下移动，<paramref name="blending"/> 控制两轮的覆盖宽度。</para>
    /// <para><paramref name="visualize"/> 是范围检查视图：落在指定点色影响范围（权重 ≤ 0.05）之外的像素变暗。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="toneLut">256 项色调曲线，同时作用于 RGB 三通道。</param>
    /// <param name="redLut">256 项红通道曲线。</param>
    /// <param name="greenLut">256 项绿通道曲线。</param>
    /// <param name="blueLut">256 项蓝通道曲线。</param>
    /// <param name="refineSaturation">精调饱和度，负值改亮度、正值加彩，范围 −100…100。</param>
    /// <param name="mixer">24 项：8 个色相家族 × (色相, 饱和度, 明度)，各 −1…1。</param>
    /// <param name="pointCount">点色个数。</param>
    /// <param name="points">每点 9 项：(色相, 饱和度, 明度, 三个偏移, 三个范围半宽)，共 <paramref name="pointCount"/> × 9。</param>
    /// <param name="grade">12 项：4 轮 × (色相, 饱和度, 明度)。</param>
    /// <param name="blending">混合轮之间的覆盖宽度，范围 0…1。</param>
    /// <param name="balance">阴影 / 高光分界点偏移，范围 −1…1。</param>
    /// <param name="visualize">要检查的点色下标；&lt; 0 时不检查。</param>
    public static void CameraRawCurveColor(
        Span<byte> rgba, int width, int height, int stride,
        ReadOnlySpan<float> toneLut, ReadOnlySpan<float> redLut, ReadOnlySpan<float> greenLut, ReadOnlySpan<float> blueLut,
        double refineSaturation, ReadOnlySpan<float> mixer, int pointCount, ReadOnlySpan<float> points,
        ReadOnlySpan<float> grade, double blending, double balance, int visualize)
    {
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                // The tone curve works on red, green and blue alike, as Photoshop's does, so contrast brings color strength
                // with it. Refine Saturation below zero eases toward changing brightness alone (−100), and above zero adds
                // more color.
                double curvedR = LutAt(toneLut, r), curvedG = LutAt(toneLut, g), curvedB = LutAt(toneLut, b);
                if (refineSaturation < 0)
                {
                    double br = r, bg = g, bb = b;
                    ScaleLuminance(ref br, ref bg, ref bb, LutAt(toneLut, Rec709(r, g, b)));
                    double k = -refineSaturation;
                    curvedR += (br - curvedR) * k; curvedG += (bg - curvedG) * k; curvedB += (bb - curvedB) * k;
                }
                else if (refineSaturation > 0)
                {
                    double lum = Rec709(curvedR, curvedG, curvedB), factor = 1 + refineSaturation;
                    curvedR = CameraClamp(lum + (curvedR - lum) * factor);
                    curvedG = CameraClamp(lum + (curvedG - lum) * factor);
                    curvedB = CameraClamp(lum + (curvedB - lum) * factor);
                }
                r = curvedR; g = curvedG; b = curvedB;
                r = LutAt(redLut, r); g = LutAt(greenLut, g); b = LutAt(blueLut, b);
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                double sourceHue = h, sourceSat = s, sourceLum = l;
                double hueDelta = 0, satDelta = 0, lumDelta = 0, weightSum = 0;
                for (int i = 0; i < 8; ++i)
                {
                    double dist = CircularDistance(h, MixerCenters[i]);
                    double w = 1 - dist / (40.0 / 360);
                    if (w <= 0) continue;
                    hueDelta += mixer[i] * w * (30.0 / 360);
                    satDelta += mixer[8 + i] * w;
                    lumDelta += mixer[16 + i] * w * 0.25;
                    weightSum += w;
                }
                if (weightSum > 1) { hueDelta /= weightSum; satDelta /= weightSum; lumDelta /= weightSum; }
                h += hueDelta; if (h < 0) h += 1; if (h >= 1) h -= 1;
                s = CameraClamp(s * (1 + satDelta));
                l = CameraClamp(l + lumDelta);
                for (int i = 0; i < pointCount; ++i)
                {
                    ReadOnlySpan<float> point = points.Slice(i * 9);
                    double w = PointWeight(h, s, l, point);
                    if (w <= 0) continue;
                    h += point[3] * w * (30.0 / 360);
                    s = CameraClamp(s * (1 + point[4] * w));
                    l = CameraClamp(l + point[5] * w * 0.25);
                }
                if (h < 0) h += 1; if (h >= 1) h -= 1;
                HslToRgb(h, s, l, ref r, ref g, ref b);
                // Balance moves the crossover between the shadow and highlight wheels. Toward highlights
                // it has to move down, so more of the picture counts as highlight and the shadow wheel
                // loses its hold; the other sign strengthened the shadow tint it was meant to weaken.
                double split = 0.5 - balance * 0.2;
                double reach = 0.12 + blending * 0.38;
                double shadowW = CameraClamp((split + reach - Rec709(r, g, b)) / Math.Max(0.05, reach * 2));
                double highlightW = CameraClamp((Rec709(r, g, b) - (split - reach)) / Math.Max(0.05, reach * 2));
                double midW = CameraClamp(1 - Math.Abs(Rec709(r, g, b) - split) / (0.35 + reach));
                double sum = shadowW + midW + highlightW;
                if (sum > 1e-4) { shadowW /= sum; midW /= sum; highlightW /= sum; }
                Span<double> weights = stackalloc double[4];
                weights[0] = shadowW; weights[1] = midW; weights[2] = highlightW; weights[3] = 1;
                for (int wheel = 0; wheel < 4; ++wheel)
                {
                    double wh = grade[wheel * 3], ws = grade[wheel * 3 + 1], wl = grade[wheel * 3 + 2];
                    double w = weights[wheel];
                    if (w <= 0 || (ws <= 0 && wl == 0)) continue;
                    // 显式给 0 只是让 C# 的定点分析无歧义；HslToRgb 的每条路径都会覆写三者。
                    double cr = 0, cg = 0, cb = 0;
                    HslToRgb(wh, 1, 0.5, ref cr, ref cg, ref cb);
                    r = CameraClamp(r + (cr - 0.5) * ws * w * 0.85);
                    g = CameraClamp(g + (cg - 0.5) * ws * w * 0.85);
                    b = CameraClamp(b + (cb - 0.5) * ws * w * 0.85);
                    if (wl != 0) ScaleLuminance(ref r, ref g, ref b, CameraClamp(Rec709(r, g, b) + wl * 0.25 * w));
                }
                if (visualize >= 0 && visualize < pointCount &&
                    PointWeight(sourceHue, sourceSat, sourceLum, points.Slice(visualize * 9)) <= 0.05)
                {
                    r *= 0.35; g *= 0.35; b *= 0.35;
                }
                WritePremultiplied(p, r, g, b, alpha);
            }
        }
    }

    // ══════════════════════ 锐化与降噪的共用原语 ══════════════════════

    /// <summary>把锐化半径滑块换算成以图层像素为单位的浮点半径，夹到 0.5…64。</summary>
    /// <remarks>
    /// 对应 <c>detail_radius</c>。滑块 0 → 0.5、100 → 3.0，因此是<b>线性</b>映射而非取半径的倍数。
    /// C 的局部变量名是 <c>base</c>，C# 中 <c>base</c> 是关键字，改名 <c>baseRadius</c>。
    /// </remarks>
    private static double DetailRadius(double slider, double scale)
    {
        double baseRadius = 0.5 + (slider / 100.0) * 2.5;
        double radius = baseRadius * (scale > 0 ? scale : 1);
        if (radius < 0.5) radius = 0.5;
        if (radius > 64) radius = 64;
        return radius;
    }

    /// <summary>像素的色相，返回<b>角度</b> 0…360（与 <c>rgb_to_hsl</c> 的归一化 0…1 不是一回事）。</summary>
    /// <remarks>
    /// 对应 <c>pixel_hue_deg</c>。
    /// <para>⚠ 关键：<c>fmod</c> 与 <c>* 60.0</c> 是<b>分开的两步</b>，
    /// 所以 C# 必须写成 <c>hue = (g - b) / chroma % 6.0;</c> 再 <c>hue = hue * 60.0;</c>。
    /// 合并成 <c>hue = 60.0 * ((g - b) / chroma) % 6.0;</c> 会落在 [0,6)，
    /// 去紫边/绿色释放的色相区间判断会全部错位。</para>
    /// </remarks>
    private static double PixelHueDeg(double r, double g, double b)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        if (chroma < 1e-6) return 0;
        double hue;
        if (maxc == r) hue = (g - b) / chroma % 6.0;
        else if (maxc == g) hue = (b - r) / chroma + 2.0;
        else hue = (r - g) / chroma + 4.0;
        hue = hue * 60.0;
        if (hue < 0) hue += 360.0;
        return hue;
    }

    /// <summary>色相是否落在区间内；<paramref name="low"/> &gt; <paramref name="high"/> 时视为<b>跨越 0° 的环绕区间</b>。</summary>
    private static int HueInRange(double hue, double low, double high)
    {
        if (low <= high) return hue >= low && hue <= high ? 1 : 0;
        return hue >= low || hue <= high ? 1 : 0;
    }

    /// <summary>某点与四邻域的平均亮度差，作为「这里是边缘」的程度（0…1）。</summary>
    /// <remarks>
    /// 对应 <c>sharpen_edge_at</c>。<b>只取 8 个邻点</b>：<c>dy</c> / <c>dx</c> 以 <c>radius</c> 为步长，
    /// 所以无论半径多大，采的都是「中心 ± 一格」这 4 对点。半径越大越省钱，代价是它<b>看不见</b>
    /// 半径以内的细节——这是原算法的取舍，不要改成多环采样。
    /// 越界邻点被丢弃（不是钳制重复计入），所以图像边缘的 mask 会偏低。
    /// </remarks>
    private static float SharpenEdgeAt(ReadOnlySpan<float> luma, int width, int height, int x, int y, int radius)
    {
        if (radius < 1) radius = 1;
        float center = luma[y * width + x];
        float sum = 0;
        int count = 0;
        for (int dy = -radius; dy <= radius; dy += radius)
        {
            for (int dx = -radius; dx <= radius; dx += radius)
            {
                if (dx == 0 && dy == 0) continue;
                long sx = x + dx, sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                sum += MathF.Abs(luma[sy * width + sx] - center);
                count++;
            }
        }
        return count != 0 ? sum / count : 0;
    }

    /// <summary>锐化蒙版检查视图：会锐化的地方显示为灰白，被蒙版保护的地方保持黑。</summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_sharpen_mask_overlay</c>。<b>仅供预览</b>：它把三通道直接写成同一个灰度，
    /// 破坏原图颜色，因此绝不能写进 .comp 的成片数据。
    /// <b>调用顺序约束</b>：必须用<b>当前</b>锐化滑块（<paramref name="sharpenRadius"/> /
    /// <paramref name="sharpenDetail"/> / <paramref name="sharpenMasking"/>）调用，
    /// 并且放在 <see cref="CameraRawDetail"/> <b>之前</b>——它算的是「将要锐化的 mask」，
    /// 放在之后读到的是已锐化图像，边缘幅度不再对应滑块。
    /// 它<b>只写 RGB 不动 alpha</b>，因此仍是合法的预乘数据。
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改（RGB 被覆盖为灰度）。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="sharpenRadius">锐化半径滑块，范围 0…100。</param>
    /// <param name="sharpenDetail">细节，范围 0…100，提高边缘 mask 的判别力。</param>
    /// <param name="sharpenMasking">蒙版，范围 0…100，抑制低对比区的锐化。</param>
    /// <param name="scale">预览像素 / 图层像素。</param>
    public static void CameraRawSharpenMaskOverlay(
        Span<byte> rgba, int width, int height, int stride,
        double sharpenRadius, double sharpenDetail, double sharpenMasking, double scale)
    {
        if (width == 0 || height == 0) return;
        int count = width * height;
        var luma = new float[count];
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) { luma[y * width + x] = 0; continue; }
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        }
        int radius = EffectsRadius(DetailRadius(sharpenRadius, scale), 1);
        double threshold = (sharpenMasking / 100.0) * 0.35;
        double detailBoost = 0.5 + sharpenDetail / 100.0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                double mask = CameraClamp((edge * detailBoost - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
                byte gray = (byte)CSemantics.LRound(mask * alpha);
                rgba[p] = rgba[p + 1] = rgba[p + 2] = gray;
            }
        }
    }

    /// <summary>细节层：亮度降噪 → 颜色降噪 → 锐化。</summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_detail</c>。<b>调用顺序约束</b>：排在创意分级
    /// （<see cref="CameraRawCurveColor"/> / <see cref="CameraRawEffects"/>）<b>之后</b>——
    /// 降噪与锐化都是「按原像素动手」，排到调色之前会先放大/抹平原图里随后要被改掉的噪点。
    /// <para><b>三段内部次序不可调换</b>：亮度降噪<b>就地改写 luma 平面</b>并写回像素；
    /// 颜色降噪读的是已被降过亮度的像素，但用<b>自己重新算的</b> chroma 平面；
    /// 锐化段则<b>再次从像素重算 luma</b>，所以它看到的是「降噪后」的图像。
    /// 换句话说锐化永远作用在降噪结果上，不会被降噪擦掉，也不会擦掉降噪。</para>
    /// <para><c>preserve</c>（细节保护）在边缘处把降噪强度压到 0，
    /// <c>contrast</c> 在亮度降噪里额外回补一点高频——两者都靠 <see cref="SharpenEdgeAt"/>
    /// 的四邻域幅度判断，所以与半径滑块无关。</para>
    /// <para>alpha 保持不变；完全透明像素不动。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="sharpenAmount">锐化量，范围 0…100。</param>
    /// <param name="sharpenRadius">锐化半径，范围 0…100。</param>
    /// <param name="sharpenDetail">锐化细节，范围 0…100。</param>
    /// <param name="sharpenMasking">锐化蒙版，范围 0…100。</param>
    /// <param name="noiseLuminance">亮度降噪量，范围 0…100。</param>
    /// <param name="noiseLuminanceDetail">亮度降噪的细节保护，范围 0…100。</param>
    /// <param name="noiseLuminanceContrast">亮度降噪的高频回补，范围 −100…100。</param>
    /// <param name="noiseColor">颜色降噪量，范围 0…100。</param>
    /// <param name="noiseColorDetail">颜色降噪的细节保护，范围 0…100。</param>
    /// <param name="noiseColorSmoothness">颜色降噪的平滑度，范围 0…100（越大半径越大）。</param>
    /// <param name="scale">预览像素 / 图层像素。</param>
    public static void CameraRawDetail(
        Span<byte> rgba, int width, int height, int stride,
        double sharpenAmount, double sharpenRadius, double sharpenDetail, double sharpenMasking,
        double noiseLuminance, double noiseLuminanceDetail, double noiseLuminanceContrast,
        double noiseColor, double noiseColorDetail, double noiseColorSmoothness, double scale)
    {
        if (width == 0 || height == 0) return;
        if (sharpenAmount == 0 && noiseLuminance == 0 && noiseColor == 0) return;
        int count = width * height;
        var luma = new float[count];
        var work = new float[count];
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) { luma[y * width + x] = 0; continue; }
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        }

        if (noiseLuminance > 0)
        {
            int radius = EffectsRadius(1.0 + noiseLuminance / 50.0, scale);
            BoxBlurPlane(luma, work, width, height, radius);
            double strength = noiseLuminance / 100.0;
            double preserve = noiseLuminanceDetail / 100.0;
            double contrast = noiseLuminanceContrast / 100.0;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    int index = y * width + x;
                    float edge = SharpenEdgeAt(luma, width, height, x, y, 1);
                    double local = strength * (1.0 - preserve * Math.Min(1.0, edge * 6.0));
                    float blurred = work[index];
                    float target = (float)(luma[index] * (1.0 - local) + blurred * local);
                    if (contrast != 0) target = (float)(target + contrast * 0.25 * (luma[index] - blurred));
                    luma[index] = target;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                    ScaleLuminance(ref r, ref g, ref b, target);
                    WritePremultiplied(p, r, g, b, alpha);
                }
            }
        }

        if (noiseColor > 0)
        {
            int radius = EffectsRadius(1.0 + noiseColorSmoothness / 40.0, scale);
            var chroma = new float[count];
            var chromaBlur = new float[count];
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                    // C 原文声明了 h / l 但只用 s，这里用 out _ 丢弃。
                    RgbToHsl(r, g, b, out _, out double s, out _);
                    chroma[y * width + x] = (float)s;
                }
            }
            BoxBlurPlane(chroma, chromaBlur, width, height, radius);
            double strength = noiseColor / 100.0;
            double preserve = noiseColorDetail / 100.0;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    int index = y * width + x;
                    float edge = MathF.Abs(chroma[index] - chromaBlur[index]);
                    double local = strength * (1.0 - preserve * Math.Min(1.0, edge * 4.0));
                    float sat = chroma[index] * (float)(1.0 - local) + chromaBlur[index] * (float)local;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                    RgbToHsl(r, g, b, out double h, out double s, out double l);
                    s = sat;
                    HslToRgb(h, s, l, ref r, ref g, ref b);
                    WritePremultiplied(p, r, g, b, alpha);
                }
            }
        }

        if (sharpenAmount > 0)
        {
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                    luma[y * width + x] = (float)Rec709(r, g, b);
                }
            }
            int radius = EffectsRadius(DetailRadius(sharpenRadius, scale), 1);
            BoxBlurPlane(luma, work, width, height, radius);
            double amount = sharpenAmount / 100.0;
            double detailMix = sharpenDetail / 100.0;
            double threshold = (sharpenMasking / 100.0) * 0.35;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    int index = y * width + x;
                    float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                    double mask = CameraClamp((edge * (0.5 + detailMix) - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
                    double high = luma[index] - work[index];
                    double sharpened = CameraClamp(luma[index] + high * amount * mask * (0.5 + detailMix));
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                    ScaleLuminance(ref r, ref g, ref b, sharpened);
                    WritePremultiplied(p, r, g, b, alpha);
                }
            }
        }
    }

    // ══════════════════════ 光学：去紫边 / 色差 / 镜头暗角校正 ══════════════════════

    /// <summary>去紫边 / 释放绿色：按色相区间把饱和度朝中性拉。</summary>
    /// <remarks>
    /// 对应 <c>optics_defringe</c>。两个区间可<b>同时</b>命中，此时取较大的那个（<c>Math.Max</c>），
    /// 不是相加——紫边和绿边同时出现时只需按最强的一项处理。
    /// 拉的是饱和度、亮度不动（<c>rec709</c> 为基准），所以不会顺带改变曝光。
    /// 区间由 <see cref="HueInRange"/> 判断，因此低 &gt; 高 的配置会被当成跨 0° 的环绕区间。
    /// </remarks>
    private static void OpticsDefringe(ref double r, ref double g, ref double b,
        double purpleAmount, double purpleLow, double purpleHigh,
        double greenAmount, double greenLow, double greenHigh)
    {
        double hue = PixelHueDeg(r, g, b);
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        if (chroma < 1e-6) return;
        double sat = chroma / maxc;
        double reduce = 0;
        if (purpleAmount > 0 && HueInRange(hue, purpleLow, purpleHigh) != 0) reduce = Math.Max(reduce, purpleAmount / 100.0);
        if (greenAmount > 0 && HueInRange(hue, greenLow, greenHigh) != 0) reduce = Math.Max(reduce, greenAmount / 100.0);
        if (reduce <= 0) return;
        double lum = Rec709(r, g, b);
        double factor = 1.0 - reduce * sat;
        r = CameraClamp(lum + (r - lum) * factor);
        g = CameraClamp(lum + (g - lum) * factor);
        b = CameraClamp(lum + (b - lum) * factor);
    }

    /// <summary>横向色差：红蓝按到画面中心的距离平方反向位移，绿通道原地不动。</summary>
    /// <remarks>
    /// 对应 <c>optics_chromatic</c>。必须先把整幅图拷进 <c>copy</c> 再写回：
    /// 采样点可能落在同一行的<b>右侧</b>，若边写边读会把已校正的像素再次采样，边缘出现拖尾。
    /// 绿通道取<b>当前</b>像素（不位移），红取左移后的像素、蓝取右移后的像素，
    /// 三个来源的 alpha 各自用 <c>fmax(1.0, alpha)</c> 兜底以免透明像素除零。
    /// 位移量用 <c>lround</c> 取整到整像素，因此这是「阶梯」式色差而非亚像素采样。
    /// </remarks>
    private static void OpticsChromatic(Span<byte> rgba, int width, int height, int stride, double strength)
    {
        if (strength <= 0) return;
        var copy = new byte[height * stride];
        for (int y = 0; y < height; ++y)
            rgba.Slice(y * stride, width * 4).CopyTo(copy.AsSpan(y * stride));
        double cx = width * 0.5, cy = height * 0.5;
        double maxR = Math.Hypot(cx, cy);
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double radial = Math.Hypot(dx, dy) / maxR;
                double shift = strength * radial * radial * 2.5;
                int rx = CSemantics.LRound(x - shift), bx = CSemantics.LRound(x + shift);
                int pc = row + x * 4;
                int pr = row + ClampedIndex(rx, width) * 4;
                int pb = row + ClampedIndex(bx, width) * 4;
                double g = Math.Min(1.0, copy[pc + 1] / alpha);
                double r = Math.Min(1.0, copy[pr] / Math.Max(1.0, copy[pr + 3]));
                double b = Math.Min(1.0, copy[pb + 2] / Math.Max(1.0, copy[pb + 3]));
                WritePremultiplied(p, r, g, b, alpha);
            }
        }
    }

    /// <summary>镜头暗角校正：正数向白色提亮四角，负数压暗。</summary>
    /// <remarks>
    /// 对应 <c>optics_vignette_correct</c>。与暗角（<c>effects_vignette</c>）同形但<b>方向相反</b>，
    /// 且羽化固定 0.35、不接受圆/方与羽化参数——它补的是镜头物理暗角，不是艺术效果。
    /// <paramref name="amount"/> 传进来的可能是「手动暗角 + 镜头档案暗角 × 35」的合成值。
    /// </remarks>
    private static void OpticsVignetteCorrect(ref double r, ref double g, ref double b,
        int x, int y, int width, int height, double amount, double midpoint)
    {
        if (amount == 0 || width == 0 || height == 0) return;
        double nx = (x + 0.5) / width * 2.0 - 1.0;
        double ny = (y + 0.5) / height * 2.0 - 1.0;
        double dist = Math.Hypot(nx, ny) / Math.Sqrt(2.0);
        double start = (midpoint / 100.0) * 0.85;
        double t = CameraClamp((dist - start) / 0.35);
        double mask = t * t * (3.0 - 2.0 * t);
        double lift = (amount / 100.0) * mask;
        if (lift > 0)
        {
            r = CameraClamp(r + (1.0 - r) * lift);
            g = CameraClamp(g + (1.0 - g) * lift);
            b = CameraClamp(b + (1.0 - b) * lift);
        }
        else
        {
            double factor = 1.0 + lift;
            r *= factor; g *= factor; b *= factor;
        }
    }

    /// <summary>光学层：镜头畸变 → 横向色差 → 去紫边 / 绿色释放 → 镜头暗角校正。</summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_optics</c>。<b>调用顺序约束</b>：排在 <b>最前</b>，
    /// 必须在 <see cref="CameraRaw"/>、<see cref="CameraRawCurveColor"/>、<see cref="CameraRawDetail"/> 之前。
    /// 理由是畸变与色差都<b>改变采样位置</b>：先重采样再锐化，锐化的边缘 mask 才对应畸变后的图像；
    /// 反过来会在画幅边缘产生明显的双重边缘。色差校正读的是畸变后的图，同理。
    /// <para><b>镜头档案的畸变被绕过</b>：只有显式给出 <paramref name="distortionK"/> 才重采样
    /// （<paramref name="profileDistortion"/> 在原 C 中<b>从未被使用</b>，此处照直译保留参数）。
    /// 而档案暗角是生效的，按 <c>profileVignetting / 100 × 35</c> 加到手动暗角上——
    /// 这个 35 是「把百分制映射到同一套强度」的经验系数，不要当单位错误去改。</para>
    /// <para>alpha 保持不变；完全透明像素不动。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="removeChromatic">非 0 时做横向色差校正（强度固定 0.45）。</param>
    /// <param name="lensProfile">非 0 时启用镜头档案的暗角补偿。</param>
    /// <param name="profileDistortion">镜头档案的畸变参数。<b>原 C 未使用</b>，保留仅为签名一致。</param>
    /// <param name="profileVignetting">镜头档案的暗角量，范围 0…100，仅在 <paramref name="lensProfile"/> 非 0 时生效。</param>
    /// <param name="distortionK">桶形畸变强度，语义同 <see cref="LensPixels.Distort"/> 的 <c>k</c>；0 时跳过重采样。</param>
    /// <param name="purpleAmount">去紫边强度，范围 0…100。</param>
    /// <param name="purpleHueLow">紫边色相区间下界（角度）。</param>
    /// <param name="purpleHueHigh">紫边色相区间上界（角度）。</param>
    /// <param name="greenAmount">绿色释放强度，范围 0…100。</param>
    /// <param name="greenHueLow">绿色区间下界（角度）。</param>
    /// <param name="greenHueHigh">绿色区间上界（角度）。</param>
    /// <param name="vignetteAmount">手动暗角校正量，范围 −100…100。</param>
    /// <param name="vignetteMidpoint">暗角校正起点，范围 0…100。</param>
    /// <param name="scale">预览像素 / 图层像素。<b>原 C 未使用</b>，保留仅为签名一致。</param>
    public static void CameraRawOptics(
        Span<byte> rgba, int width, int height, int stride,
        int removeChromatic, int lensProfile, double profileDistortion, double profileVignetting,
        double distortionK, double purpleAmount, double purpleHueLow, double purpleHueHigh,
        double greenAmount, double greenHueLow, double greenHueHigh,
        double vignetteAmount, double vignetteMidpoint, double scale)
    {
        if (width == 0 || height == 0) return;
        double profileVignette = lensProfile != 0 ? profileVignetting / 100.0 : 0;
        double vignette = vignetteAmount + profileVignette * 35.0;
        if (distortionK != 0)
        {
            int bytes = height * stride;
            var copy = new byte[bytes];
            rgba.Slice(0, bytes).CopyTo(copy);
            LensPixels.Distort(copy, rgba, width, height, stride, distortionK);
        }
        if (removeChromatic != 0) OpticsChromatic(rgba, width, height, stride, 0.45);
        if (purpleAmount == 0 && greenAmount == 0 && vignette == 0) return;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                OpticsDefringe(ref r, ref g, ref b, purpleAmount, purpleHueLow, purpleHueHigh, greenAmount, greenHueLow, greenHueHigh);
                OpticsVignetteCorrect(ref r, ref g, ref b, x, y, width, height, vignette, vignetteMidpoint);
                WritePremultiplied(p, r, g, b, alpha);
            }
        }
    }

    // ══════════════════════ 相机校准 ══════════════════════

    /// <summary>相机校准：暗部色调偏移 + 主色的色相 / 饱和度微调，幅度按处理版本缩放。</summary>
    /// <remarks>
    /// 对应 <c>adjust_camera_raw_calibration</c>。<b>调用顺序约束</b>：排在 <b>最前</b>，
    /// 必须在 <see cref="CameraRaw"/> <b>之前</b>——校准模拟的是镜头与机身在<b>原始</b>信号上的偏差，
    /// 放到曝光/对比度之后，缩放次数不同会得到不同的最终偏差量。
    /// 它与 <see cref="CameraRawOptics"/> 的先后顺序<b>不影响数值</b>（两者都在 HSL 域做小数调整），
    /// 但约定上光学在前、校准在后，便于对照 Mac 版。
    /// <para>只动<b>主色</b>像素（r/g/b 中的最大者），次色与灰色不动；
    /// 暗部色调只作用于亮度 &lt; 0.35 的像素，幅度是色调滑块的 0.06 倍。</para>
    /// <para><paramref name="processVersion"/> 不是开关而是<b>幅度档</b>：越新的处理流程原图偏差越小，
    /// 所以校准量从 0.55 递增到 1.0。默认 0（&lt;= 1）时只给 0.55 的量。</para>
    /// <para>alpha 保持不变；完全透明像素不动。</para>
    /// </remarks>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="shadowTint">暗部色调，范围 −100…100（绿/洋红）。</param>
    /// <param name="redHue">红色相偏移，范围 −100…100。</param>
    /// <param name="redSaturation">红饱和度偏移，范围 −100…100。</param>
    /// <param name="greenHue">绿色相偏移，范围 −100…100。</param>
    /// <param name="greenSaturation">绿饱和度偏移，范围 −100…100。</param>
    /// <param name="blueHue">蓝色相偏移，范围 −100…100。</param>
    /// <param name="blueSaturation">蓝饱和度偏移，范围 −100…100。</param>
    /// <param name="processVersion">处理流程版本，决定校准幅度（&lt;=1 → 0.55，2 → 0.65，3 → 0.75，4 → 0.85，5 → 0.92，其他 → 1.0）。</param>
    public static void CameraRawCalibration(
        Span<byte> rgba, int width, int height, int stride,
        double shadowTint, double redHue, double redSaturation,
        double greenHue, double greenSaturation, double blueHue, double blueSaturation,
        int processVersion)
    {
        if (width == 0 || height == 0) return;
        double versionScale = processVersion <= 1 ? 0.55 : processVersion == 2 ? 0.65 : processVersion == 3 ? 0.75
            : processVersion == 4 ? 0.85 : processVersion == 5 ? 0.92 : 1.0;
        double tint = shadowTint / 100.0 * versionScale;
        double rh = redHue / 100.0 * (15.0 / 360.0) * versionScale;
        double rs = redSaturation / 100.0 * 0.45 * versionScale;
        double gh = greenHue / 100.0 * (15.0 / 360.0) * versionScale;
        double gs = greenSaturation / 100.0 * 0.45 * versionScale;
        double bh = blueHue / 100.0 * (15.0 / 360.0) * versionScale;
        double bs = blueSaturation / 100.0 * 0.45 * versionScale;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha), b = Math.Min(1.0, rgba[p + 2] / alpha);
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                if (l < 0.35 && tint != 0)
                {
                    h += tint * 0.06;
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
                if (maxc - minc > 1e-5)
                {
                    if (r >= g && r >= b) { h += rh; s = CameraClamp(s * (1 + rs)); }
                    else if (g >= r && g >= b) { h += gh; s = CameraClamp(s * (1 + gs)); }
                    else { h += bh; s = CameraClamp(s * (1 + bs)); }
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                HslToRgb(h, s, l, ref r, ref g, ref b);
                WritePremultiplied(p, r, g, b, alpha);
            }
        }
    }
}
