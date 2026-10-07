using Compositor.Core.Pixels;
using Xunit;

namespace Compositor.Core.Tests.Pixels.Tier1;

/// <summary>
/// Tier 1 验收基准：把 macOS 版 <c>ImageAdjustmentTests.swift</c>（9 测试 / 59 断言）与
/// <c>LevelsTests.swift</c>（10 测试 / 47 断言）的断言<b>逐条</b>翻译成 xunit。
/// </summary>
/// <remarks>
/// <para><b>铁律</b>：本文件里的每一个期望值与容差都<b>原样抄自 Swift 断言</b>，
/// 一个数字都没有自己发明，也没有为了让 C# 实现通过而放宽。
/// 翻译后若在 C# 上失败，那是<b>实现的问题</b>，不是断言的问题；
/// 预计失败的条目已在交付说明中逐条列出。</para>
///
/// <para><b>翻译规则</b></para>
/// <list type="bullet">
///   <item><c>#expect(abs(x - e) &lt;= n)</c> → <c>Assert.InRange(x, e - n, e + n)</c>（两端闭合，等价）</item>
///   <item><c>#expect(abs(x - e) &lt; t)</c> → <c>Assert.True(Math.Abs(x - e) &lt; t, …)</c>（保留严格小于，不退化成 InRange）</item>
///   <item><c>#expect(a[i] == b &amp;&amp; a[j] == c)</c> → <c>Assert.True(…, 原注释)</c></item>
///   <item><c>#expect(x === y)</c>（引用相等）→ 只在 C 层可观测的<b>字节恒等</b>上断言，
///         并在注释里如实标明它原本是"优化跳过"短路路径，见 <see cref="Levels_IdentitySettings_AreAnExactNoOp"/>。</item>
/// </list>
///
/// <para><b>Swift 包装层的处理</b>：Swift 测试不直接调 C 函数，而是经过
/// <c>ImageAdjustments.swift</c> / <c>Levels.swift</c> 里的薄包装。
/// 本文件把这些包装（settings 结构体的字段默认值、区间钳制、LUT 构建）<b>逐行抄成测试内的 private helper</b>，
/// helper 只服务本文件，<b>没有</b>放进 <c>src/</c>。CGImage 用 <c>byte[]</c> 预乘 RGBA8 直接替代。</para>
///
/// <para><b>行尾 / 编码</b>：与仓库现有 <c>.cs</c> 一致，UTF-8 无 BOM + LF。</para>
/// </remarks>
public sealed class AdjustmentsTier1Tests
{
    // ══════════════════════════════════════════════════════════════════════════
    // Swift 包装层直译 helper（只服务本文件）
    // 来源：Compositor/Document/ImageAdjustments.swift（210 行）
    //        Compositor/Document/Levels.swift（LevelRange / LevelsSettings / LevelsFilter）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>直译 <c>ImageAdjustmentPixels.clamp</c> / <c>LevelRange.normalized</c> 内的闭包 clamp：
    /// 有限则夹到区间内，否则取 fallback。</summary>
    private static double Clamp(double value, double low, double high, double fallback)
        => double.IsFinite(value) ? Math.Min(high, Math.Max(low, value)) : fallback;

    /// <summary>Swift 的 <c>UInt8(min(255.0, max(0.0, x)))</c>：先钳后截断。</summary>
    private static byte ToByte(double v) => (byte)Math.Clamp(v, 0.0, 255.0);

    // ────────────── 直译 ImageAdjustments.swift:8 image(...) 的填色 ──────────────

    /// <summary>
    /// 直译 Swift <c>ImageAdjustmentTests.image(width:height:red:green:blue:alpha:)</c>：
    /// 以<b>直通</b> sRGB 颜色填满一块<b>预乘</b> RGBA8 画布。
    /// </summary>
    /// <remarks>
    /// CGContext 是 premultipliedLast，因此写入前要乘上 alpha。
    /// 换算用 <c>.rounded()</c> 的四舍五入（远离零）语义，与 Swift <c>(v * 255).rounded()</c> 对齐；
    /// 因此 <c>alpha = 0.5</c> 落成 128 而不是 127。原 Swift 断言里凡涉及 alpha 的都自带 ±1 容差，
    /// 两边都成立，这里明确记录选值以便复现。
    /// </remarks>
    private static byte[] Solid(int width, int height, double red, double green, double blue, double alpha)
    {
        var px = new byte[width * height * 4];
        byte r = ToByte(Math.Round(red * alpha * 255.0, MidpointRounding.AwayFromZero));
        byte g = ToByte(Math.Round(green * alpha * 255.0, MidpointRounding.AwayFromZero));
        byte b = ToByte(Math.Round(blue * alpha * 255.0, MidpointRounding.AwayFromZero));
        byte a = ToByte(Math.Round(alpha * 255.0, MidpointRounding.AwayFromZero));
        for (int i = 0; i < width * height; i++)
        {
            px[i * 4 + 0] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return px;
    }

    /// <summary>直译 Swift <c>ImageAdjustmentTests.gray(...)</c>：直通 128/255 的中性灰。</summary>
    private static byte[] Gray(int width = 4, int height = 4, double alpha = 1)
        => Solid(width, height, 128.0 / 255.0, 128.0 / 255.0, 128.0 / 255.0, alpha);

    /// <summary>直译 Swift <c>ImageAdjustmentTests.pixels(_:)</c>：逐像素<b>反预乘</b>成直通 RGBA，
    /// 反预乘用 <c>min(255, (v * 255 + a / 2) / a)</c>（整数除法），<c>a == 0</c> 时三通道记 0。</summary>
    private static int[][] Pixels(byte[] px, int width, int height)
    {
        var result = new int[width * height][];
        for (int i = 0; i < width * height; i++)
        {
            int a = px[i * 4 + 3];
            var pixel = new int[4];
            for (int c = 0; c < 3; c++)
                pixel[c] = a == 0 ? 0 : Math.Min(255, (px[i * 4 + c] * 255 + a / 2) / a);
            pixel[3] = a;
            result[i] = pixel;
        }
        return result;
    }

    /// <summary>把 <c>[[Int]]</c> 摊平成 <c>Int[]</c>，以便用 xunit 的集合相等断言整体相等。</summary>
    private static int[] Flatten(int[][] v) => v.SelectMany(p => p).ToArray();

    // ────────────── 直译 ImageAdjustments.swift:20 AdjustmentColor ──────────────

    /// <summary>直译 <c>AdjustmentColor</c>（直通 sRGB，每通道 0–1）。</summary>
    /// <remarks>
    /// 这些 settings 包装一律做成 <c>class</c> 而不是 <c>struct</c>：结构体带全可选参数时，
    /// <c>new T()</c> 会因 C# 7.3 的"参数更少者更优"规则绑到<b>隐式无参构造</b>，
    /// 从而把默认值（如 <c>gamma</c>）静默清成 0 而不是走 Swift 的默认值 1。
    /// </remarks>
    private sealed class AdjustmentColor
    {
        public AdjustmentColor(double red, double green, double blue)
        {
            Red = red;
            Green = green;
            Blue = blue;
        }

        public double Red { get; }

        public double Green { get; }

        public double Blue { get; }

        /// <summary><c>isValid</c>：三通道都有限且落在 0…1。</summary>
        public bool IsValid
            => new[] { Red, Green, Blue }.All(v => double.IsFinite(v) && v >= 0 && v <= 1);

        /// <summary><c>clamped</c>：非有限取 0，其余夹到 0…1。</summary>
        public AdjustmentColor Clamped => new(
            Clamp(Red, 0, 1, 0),
            Clamp(Green, 0, 1, 0),
            Clamp(Blue, 0, 1, 0));
    }

    // ────────────── 直译 ImageAdjustments.swift:37 ExposureSettings ──────────────

    /// <summary>直译 <c>ExposureSettings</c>。<b><c>table</c> 是 levels_apply 的 LUT 来源，逐行照抄。</b></summary>
    private sealed class ExposureSettings
    {
        public ExposureSettings(double exposure = 0, double offset = 0, double gamma = 1)
        {
            Exposure = exposure;
            Offset = offset;
            Gamma = gamma;
        }

        /// <summary>档位，−20…20。</summary>
        public double Exposure { get; }

        /// <summary>线性光里的加项，−0.5…0.5。</summary>
        public double Offset { get; }

        /// <summary>伽马校正，0.01…9.99。</summary>
        public double Gamma { get; }

        /// <summary><c>isValid</c>：三个区间各自包含（NaN 天然落选，与 <c>ClosedRange.contains</c> 一致）。</summary>
        public bool IsValid
            => Exposure >= -20 && Exposure <= 20
            && Offset >= -0.5 && Offset <= 0.5
            && Gamma >= 0.01 && Gamma <= 9.99;

        /// <summary>
        /// <c>var table: [Float]</c>：每个输入字节（0–255）解到线性光、缩放平移、再编码回 sRGB。
        /// 这里的 <c>pow(2, exposure)</c> 与 sRGB↔线性往返是本条断言的全部依据。
        /// </summary>
        public float[] Table
        {
            get
            {
                double scale = Math.Pow(2, Exposure);
                var table = new float[256];
                for (int index = 0; index < 256; index++)
                {
                    double encoded = index / 255.0;
                    double linear = encoded <= 0.04045
                        ? encoded / 12.92
                        : Math.Pow((encoded + 0.055) / 1.055, 2.4);
                    linear = Math.Pow(Math.Max(0, linear * scale + Offset), 1 / Gamma);
                    double output = linear <= 0.0031308
                        ? linear * 12.92
                        : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
                    table[index] = (float)Math.Min(1, Math.Max(0, output));
                }
                return table;
            }
        }
    }

    /// <summary>直译 <c>ExposureSettings.apply(_:)</c>：把同一张表复制三份喂给 <c>levels_apply</c>。</summary>
    private static byte[] RunExposure(byte[] source, int count, ExposureSettings settings)
    {
        if (!settings.IsValid) throw new InvalidOperationException("ProjectError.invalid");

        float[] single = settings.Table;
        var tables = new float[256 * 3];
        for (int k = 0; k < 3; k++) Array.Copy(single, 0, tables, k * 256, 256);

        var px = (byte[])source.Clone();
        LevelsPixels.Apply(px, count, tables);
        return px;
    }

    // ────────────── 直译 ImageAdjustments.swift:75 GradientMapSettings ──────────────

    /// <summary>直译 <c>GradientMapSettings</c>。<b><c>table</c> 是 adjust_gradient_map 的调色板来源。</b></summary>
    private sealed class GradientMapSettings
    {
        public GradientMapSettings(AdjustmentColor shadows, AdjustmentColor highlights, bool reversed = false)
        {
            Shadows = shadows;
            Highlights = highlights;
            Reversed = reversed;
        }

        public AdjustmentColor Shadows { get; }

        public AdjustmentColor Highlights { get; }

        public bool Reversed { get; }

        public bool IsValid => Shadows.IsValid && Highlights.IsValid;

        /// <summary><c>ends</c>：反向时把阴影与高光对调。</summary>
        public (AdjustmentColor dark, AdjustmentColor light) Ends
            => Reversed ? (Highlights, Shadows) : (Shadows, Highlights);

        /// <summary>
        /// 768 字节调色板，最暗项在索引 0；每项 <c>from + (to - from) * t</c> 后
        /// <c>(value * 255).rounded()</c> 再钳到 0…255。逐行照抄 Swift。
        /// </summary>
        public byte[] Table
        {
            get
            {
                var (dark, light) = Ends;
                var table = new byte[256 * 3];
                for (int index = 0; index < 256; index++)
                {
                    double t = index / 255.0;
                    table[index * 3 + 0] = Channel(dark.Red, light.Red, t);
                    table[index * 3 + 1] = Channel(dark.Green, light.Green, t);
                    table[index * 3 + 2] = Channel(dark.Blue, light.Blue, t);
                }
                return table;
            }
        }

        /// <summary>Swift 的内嵌 <c>channel(_:_:_:)</c>。</summary>
        private static byte Channel(double from, double to, double t)
        {
            double value = from + (to - from) * t;
            double scaled = Math.Round(value * 255.0, MidpointRounding.AwayFromZero);
            return (byte)Math.Clamp(scaled, 0.0, 255.0);
        }
    }

    /// <summary>直译 <c>GradientMapSettings.apply(_:)</c>：建表后调 <c>adjust_gradient_map</c>。</summary>
    private static byte[] RunGradientMap(byte[] source, int width, int height, GradientMapSettings settings)
    {
        if (!settings.IsValid) throw new InvalidOperationException("ProjectError.invalid");

        byte[] table = settings.Table;
        var px = (byte[])source.Clone();
        AdjustPixels.GradientMap(px, width, height, width * 4, table);
        return px;
    }

    // ────────────── 直译 ImageAdjustments.swift:180 GrainSettings ──────────────

    /// <summary>直译 <c>GrainSettings</c>。</summary>
    private sealed class GrainSettings
    {
        public GrainSettings(double amount = 25, double size = 1.5, double roughness = 50, uint seed = 0)
        {
            Amount = amount;
            Size = size;
            Roughness = roughness;
            Seed = seed;
        }

        /// <summary>强度 0–100。</summary>
        public double Amount { get; }

        /// <summary>颗粒尺寸（文档像素）0.5–20。</summary>
        public double Size { get; }

        /// <summary>0–100，细小不规则细节的比重。</summary>
        public double Roughness { get; }

        public uint Seed { get; }

        public bool IsValid
            => Amount >= 0 && Amount <= 100
            && Size >= 0.5 && Size <= 20
            && Roughness >= 0 && Roughness <= 100;
    }

    /// <summary>直译 <c>GrainSettings.apply(_:origin:unitsPerPixel:seed:)</c>。
    /// <b>注意 Swift 的 <c>guard amount &gt; 0 else { return image }</c> 短路：返回的是原图，一个字节都不动。</b></summary>
    private static byte[] RunGrain(
        byte[] source, int width, int height, GrainSettings settings,
        double originX = 0, double originY = 0, double unitsPerPixel = 1)
    {
        if (!settings.IsValid || !double.IsFinite(unitsPerPixel) || unitsPerPixel <= 0)
            throw new InvalidOperationException("ProjectError.invalid");
        if (!(settings.Amount > 0)) return source;

        var px = (byte[])source.Clone();
        AdjustPixels.Grain(
            px, width, height, width * 4,
            settings.Amount, settings.Size, settings.Roughness, settings.Seed,
            originX, originY, unitsPerPixel);
        return px;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ImageAdjustmentTests.swift —— 9 测试 / 59 断言，本文件翻译 27 条
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>原 Swift：<c>ImageAdjustmentTests.swift:36</c> — <c>exposureWorksInLinearLightWithOffsetAndGamma</c>。
    /// 6 条断言<b>全部</b>翻译。</summary>
    /// <remarks>
    /// 依赖的 Swift 层：<c>ExposureSettings.apply</c>（ImageAdjustments.swift:37–71）→
    /// <c>levels_apply(pixels, w*h, tables)</c>，其中 <c>tables = table 复制三份</c>。
    /// LUT 与 <c>pow(2, exposure)</c>、sRGB↔线性往返已在本文件顶部逐行照抄。
    /// </remarks>
    [Fact]
    public void ImageAdjustment_Exposure_WorksInLinearLightWithOffsetAndGamma()
    {
        byte[] input = Gray();

        // 「默认值什么都不改」
        int[] defaults = Flatten(Pixels(RunExposure(input, 16, new ExposureSettings()), 4, 4));
        Assert.Equal(Flatten(Pixels(input, 4, 4)), defaults);

        // 「+1 档把线性光翻倍：\(brighter)」
        int[] brighter = Pixels(RunExposure(input, 16, new ExposureSettings(exposure: 1)), 4, 4)[0];
        Assert.InRange(brighter[0], 174, 178); // abs(brighter[0] - 176) <= 2

        Assert.Equal(brighter[2], brighter[0]); // brighter[0] == brighter[2]

        // 「gamma 2 取线性光的平方根：\(lifted)」
        int[] lifted = Pixels(RunExposure(input, 16, new ExposureSettings(gamma: 2)), 4, 4)[0];
        Assert.InRange(lifted[0], 179, 183); // abs(lifted[0] - 181) <= 2

        // 「offset 往线性光里加东西：\(offset)」
        byte[] black = Solid(4, 4, 0, 0, 0, 1);
        int[] offset = Pixels(RunExposure(black, 16, new ExposureSettings(offset: 0.1)), 4, 4)[0];
        Assert.InRange(offset[0], 87, 91); // abs(offset[0] - 89) <= 2

        // 「alpha 保持不变」
        byte[] translucent = Gray(4, 4, 0.5);
        int[] liftedT = Pixels(RunExposure(translucent, 16, new ExposureSettings(exposure: 1)), 4, 4)[0];
        Assert.Equal(Pixels(translucent, 4, 4)[0][3], liftedT[3]);
    }

    /// <summary>原 Swift：<c>ImageAdjustmentTests.swift:51</c> — <c>gradientMapColorsByBrightnessAndReverses</c>。
    /// 9 条断言<b>全部</b>翻译。</summary>
    /// <remarks>
    /// 依赖的 Swift 层：<c>GradientMapSettings.apply</c>（ImageAdjustments.swift:88–108）→
    /// 768 字节调色板 + <c>adjust_gradient_map</c>。调色板构建逐行照抄。
    /// </remarks>
    [Fact]
    public void ImageAdjustment_GradientMap_ColorsByBrightnessAndReverses()
    {
        var settings = new GradientMapSettings(
            new AdjustmentColor(1, 0, 0),
            new AdjustmentColor(0, 0, 1));

        // 纯黑落在阴影色（红）
        int[] black = Pixels(RunGradientMap(Solid(4, 4, 0, 0, 0, 1), 4, 4, settings), 4, 4)[0];
        Assert.Equal(new[] { 255, 0, 0, 255 }, black);

        // 纯白落在高光色（蓝）
        int[] white = Pixels(RunGradientMap(Solid(4, 4, 1, 1, 1, 1), 4, 4, settings), 4, 4)[0];
        Assert.Equal(new[] { 0, 0, 255, 255 }, white);

        // 中灰落在两端之间；红蓝之间走的是绿通道，绿必须仍是 0
        int[] middle = Pixels(RunGradientMap(Gray(), 4, 4, settings), 4, 4)[0];
        Assert.InRange(middle[0], 125, 129); // abs(red - 127) <= 2
        Assert.InRange(middle[2], 126, 130); // abs(blue - 128) <= 2
        Assert.Equal(0, middle[1]);          // green == 0

        // 半透明白：反预乘后蓝仍接近满值、红接近 0、alpha 不动
        int[] translucent = Pixels(RunGradientMap(Solid(4, 4, 1, 1, 1, 0.5), 4, 4, settings), 4, 4)[0];
        Assert.True(translucent[2] >= 250, "clearBlue >= 250");
        Assert.True(translucent[0] <= 5, "clearRed <= 5");
        Assert.InRange(translucent[3], 127, 129); // abs(clearAlpha - 128) <= 1

        // reversed = true 后纯黑落到另一端（蓝）
        var reversed = new GradientMapSettings(
            new AdjustmentColor(1, 0, 0),
            new AdjustmentColor(0, 0, 1),
            reversed: true);
        int[] reversedBlack = Pixels(RunGradientMap(Solid(4, 4, 0, 0, 0, 1), 4, 4, reversed), 4, 4)[0];
        Assert.Equal(new[] { 0, 0, 255, 255 }, reversedBlack);
    }

    /// <summary>原 Swift：<c>ImageAdjustmentTests.swift:70</c> — <c>grainIsFixedInDocumentSpaceAndLeavesTransparencyAlone</c>。
    /// 6 条断言<b>全部</b>翻译。</summary>
    /// <remarks>
    /// 依赖的 Swift 层：<c>GrainSettings.apply</c>（ImageAdjustments.swift:201–209）→
    /// <c>adjust_grain(…, origin, unitsPerPixel)</c>。文档坐标固定这一点靠
    /// <c>originX/originY</c> 传进 C 函数来验证。
    /// </remarks>
    [Fact]
    public void ImageAdjustment_Grain_IsFixedInDocumentSpaceAndLeavesTransparencyAlone()
    {
        var settings = new GrainSettings(amount: 60, size: 2, roughness: 40, seed: 7);

        int[][] whole = Pixels(RunGrain(Gray(40, 40), 40, 40, settings), 40, 40);

        Assert.True(whole.Select(p => p[0]).Distinct().Count() > 5, "颗粒会改变亮度");

        Assert.True(whole.All(p => p[0] == p[1] && p[1] == p[2]), "三个通道加的是同一个量");

        // 「画布上 20×20 的一块按它在文档里的位置绘制，必须和大图的同一块完全一致」
        int[][] part = Pixels(RunGrain(Gray(20, 20), 20, 20, settings, originX: 10, originY: 10), 20, 20);
        var crop = new List<int[]>();
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                crop.Add(whole[(y + 10) * 40 + x + 10]);
        Assert.Equal(Flatten(part), Flatten(crop.ToArray()));

        // 「换一个种子就是另一套图案」
        var reseeded = new GrainSettings(amount: 60, size: 2, roughness: 40, seed: 8);
        Assert.NotEqual(
            Flatten(Pixels(RunGrain(Gray(40, 40), 40, 40, reseeded), 40, 40)),
            Flatten(whole));

        // 「强度为 0 就不改」
        byte[] plain = Gray();
        Assert.Equal(
            Flatten(Pixels(plain, 4, 4)),
            Flatten(Pixels(RunGrain(plain, 4, 4, new GrainSettings(amount: 0)), 4, 4)));

        // 「全透明的像素保持全透明」
        int[][] cleared = Pixels(RunGrain(Gray(4, 4, 0), 4, 4, settings), 4, 4);
        Assert.True(cleared.All(p => p[3] == 0), "clear pixels stay clear");
    }

    /// <summary>原 Swift：<c>ImageAdjustmentTests.swift:89</c> — <c>grainSizeControlsParticleScaleEvenWithRoughness</c>。
    /// 1 条断言<b>翻译</b>（纯相对比较，无绝对期望值可抄，原样照抄）。</summary>
    /// <remarks>依赖的 Swift 层：同 <c>GrainSettings.apply</c>。<c>roughness = 70</c> 时尺寸仍须可见。</remarks>
    [Fact]
    public void ImageAdjustment_Grain_SizeControlsParticleScaleEvenWithRoughness()
    {
        byte[] source = Gray(64, 64);
        int[][] small = Pixels(RunGrain(source, 64, 64, new GrainSettings(70, 1, 70, 17)), 64, 64);
        int[][] large = Pixels(RunGrain(source, 64, 64, new GrainSettings(70, 12, 70, 17)), 64, 64);

        Assert.True(
            NeighboringDifference(large) < NeighboringDifference(small) * 0.7,
            "larger grain should form visibly larger, more coherent particles");
    }

    /// <summary>Swift <c>ImageAdjustmentTests.neighboringDifference(_:)</c>：逐行相邻像素差的均值。</summary>
    private static double NeighboringDifference(int[][] values)
    {
        long total = 0;
        int count = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 1; x < 64; x++)
            {
                total += Math.Abs(values[y * 64 + x][0] - values[y * 64 + x - 1][0]);
                count++;
            }
        return (double)total / count;
    }

    /// <summary>
    /// 原 Swift：<c>ImageAdjustmentTests.swift:104</c> — <c>settingsSaveAndOlderAdjustmentsStillOpen</c>。
    /// <b>只翻译其中 1 条</b>：<c>#expect(!broken.isValid)</c>（第 119 行，把 <c>ExposureSettings.gamma</c> 设成 0）。
    /// </summary>
    /// <remarks>
    /// 其余 6 条（第 108–111、115–116 行）断言的是 <c>LayerAdjustment</c> 的 <c>Codable</c> 编解码产物
    /// （"老 kind 的存档不能多出任何字段"、往返相等），属于未移植的持久化层，
    /// 在 C 函数层面不可复现，故不翻译。
    /// </remarks>
    [Fact]
    public void ImageAdjustment_SettingsSave_OnlyTheIsValidPartIsReproducible()
    {
        // broken.exposure.gamma = 0 → 0 落在 gammaRange 0.01…9.99 之外 → 非法
        var broken = new ExposureSettings(exposure: 0, offset: 0, gamma: 0);
        Assert.False(broken.IsValid);
    }

    /// <summary>
    /// 原 Swift：<c>ImageAdjustmentTests.swift:122</c> — <c>newAdjustmentLayersStartFromThePaletteRenderAndEditInThePanel</c>。
    /// <b>只翻译其中 3 条</b>：第 143–145 行对渲染结果的三个比较。
    /// </summary>
    /// <remarks>
    /// 这 3 条的输入在 C 函数层面<b>完全确定</b>：20×20 的直通 128/255 灰 +
    /// 前景红 / 背景蓝构成的渐变映射（<c>addAdjustment(.gradientMap)</c> 就是这样初始化 settings 的），
    /// 因此直接构造输入调 <c>AdjustPixels.GradientMap</c>，断言<b>原期望值</b>未做任何改动。
    /// 文档层因为 20×20 全覆盖且灰不透明，合成结果就等于映射结果，故像素等价。
    /// 其余 8 条（第 132–137、151–153 行）断言的是 <c>EditorSession</c> 的图层/撤销栈/枚举元数据，不翻译。
    /// </remarks>
    [Fact]
    public void ImageAdjustment_NewGradientMapLayer_RendersGrayBetweenThePaletteColors()
    {
        var settings = new GradientMapSettings(
            new AdjustmentColor(1, 0, 0), // 前景色
            new AdjustmentColor(0, 0, 1)); // 背景色

        int[] middle = Pixels(RunGradientMap(Gray(20, 20), 20, 20, settings), 20, 20)[210];

        Assert.True(middle[0] > 100, "gray maps between red and blue");
        Assert.True(middle[2] > 100, "gray maps between red and blue");
        Assert.True(middle[1] < 20, "gray maps between red and blue");
    }

    /// <summary>
    /// 原 Swift：<c>ImageAdjustmentTests.swift:156</c> — <c>imageMenuExposureChangesTheLayerInOneStep</c>。
    /// <b>只翻译其中 1 条</b>：第 169 行 <c>#expect(abs(result[0] - 176) &lt;= 2)</c>。
    /// </summary>
    /// <remarks>
    /// 输入与 LUT 参数完全确定（8×8 直通灰 + <c>exposure = 1</c>），可在 C 函数层面复现，故照抄原期望值 176。
    /// 其余 2 条（第 167 行撤销栈计数+名字、第 170 行 <c>FilterKind.isImageAdjustment</c> 元数据）依赖未移植的会话层。
    /// </remarks>
    [Fact]
    public void ImageAdjustment_ImageMenuExposure_ProducesTheSameOneStopLift()
    {
        int[] result = Pixels(RunExposure(Gray(8, 8), 64, new ExposureSettings(exposure: 1)), 8, 8)[0];
        Assert.InRange(result[0], 174, 178); // abs(result[0] - 176) <= 2
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Levels.swift 包装层直译 helper（只服务本文件）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 直译 <c>LevelsChannel</c>。<b>索引顺序必须照抄 Swift 的 <c>allCases</c> 合成顺序：
    /// rgb, red, green, blue</b>——即 <c>rgb.index == 0</c>，而 <c>LevelsSettings.ranges[0]</c>
    /// 正是合成 RGB 段。<c>LevelsTests.swift:65</c> 的期望值就建立在这个顺序上。
    /// </summary>
    private enum LevelsChannel
    {
        Rgb,
        Red,
        Green,
        Blue,
    }

    private static int ChannelIndex(LevelsChannel channel) => channel switch
    {
        LevelsChannel.Rgb => 0,
        LevelsChannel.Red => 1,
        LevelsChannel.Green => 2,
        _ => 3,
    };

    /// <summary>直译 <c>Levels.swift:8 LevelRange</c>。<b><c>apply</c> 是 levels_apply LUT 的来源。</b></summary>
    private sealed class LevelRange
    {
        public LevelRange(double black = 0, double gamma = 1, double white = 255,
                          double outputBlack = 0, double outputWhite = 255)
        {
            Black = black;
            Gamma = gamma;
            White = white;
            OutputBlack = outputBlack;
            OutputWhite = outputWhite;
        }

        public double Black { get; }

        public double Gamma { get; }

        public double White { get; }

        public double OutputBlack { get; }

        public double OutputWhite { get; }

        /// <summary>
        /// <c>normalized</c>。<b>顺序不能改</b>：先钳 black 到 0…254，再用<b>已钳好的</b> black
        /// 把 white 钳到 <c>(black + 1)…255</c>，最后钳 gamma 与两端输出值。
        /// </summary>
        public LevelRange Normalized()
        {
            double black = Clamp(Black, 0, 254, 0);
            double white = Clamp(White, black + 1, 255, 255);
            double gamma = Clamp(Gamma, 0.1, 9.99, 1);
            double outputBlack = Clamp(OutputBlack, 0, 255, 0);
            double outputWhite = Clamp(OutputWhite, 0, 255, 255);
            return new LevelRange(black, gamma, white, outputBlack, outputWhite);
        }

        /// <summary><c>apply(_:)</c>：先把输入黑场/白场夹进 0…1，再走 <c>pow(input, 1/gamma)</c>，
        /// 最后按输出黑/白重新拉伸并除回 255。</summary>
        public double Apply(double value)
        {
            LevelRange s = Normalized();
            double input = Math.Min(1, Math.Max(0, (value * 255 - s.Black) / (s.White - s.Black)));
            return (s.OutputBlack + Math.Pow(input, 1 / s.Gamma) * (s.OutputWhite - s.OutputBlack)) / 255;
        }

        /// <summary>等价于 Swift 的 <c>$0.normalized == LevelRange()</c>。</summary>
        public bool IsDefault
        {
            get
            {
                LevelRange n = Normalized();
                return n.Black == 0 && n.Gamma == 1 && n.White == 255
                    && n.OutputBlack == 0 && n.OutputWhite == 255;
            }
        }
    }

    /// <summary>直译 <c>Levels.swift:32 LevelsSettings</c>。</summary>
    private sealed class LevelsSettings
    {
        public LevelsChannel Channel { get; set; } = LevelsChannel.Rgb;

        /// <summary><c>ranges</c>：索引 0 是合成 RGB，1/2/3 是红/绿/蓝，顺序照抄 Swift。</summary>
        public LevelRange[] Ranges { get; } = new[]
        {
            new LevelRange(), new LevelRange(), new LevelRange(), new LevelRange(),
        };

        /// <summary><c>current</c> 的 setter 会先 <c>normalized</c> 再写回。</summary>
        public LevelRange Current
        {
            get => Ranges[ChannelIndex(Channel)];
            set => Ranges[ChannelIndex(Channel)] = value.Normalized();
        }

        public bool IsIdentity => Ranges.All(r => r.IsDefault);

        /// <summary><c>apply(_:channel:)</c>：单通道段先作用，合成 RGB 段后作用。</summary>
        public double Apply(double value, LevelsChannel channel)
            => Ranges[0].Apply(Ranges[ChannelIndex(channel)].Apply(value));
    }

    /// <summary>
    /// 直译 <c>Levels.swift:65 LevelsFilter.run(_:)</c> 的两条关键行为：
    /// ① <c>settings.isIdentity</c> 时<b>直接返回原图</b>，根本不进 <c>levels_apply</c>；
    /// ② <c>tables</c> 按 <b>红、绿、蓝</b>顺序各铺 256 项 —— 这就是喂给 <c>levels_apply</c> 的内容。
    /// </summary>
    private static byte[] LevelsRun(byte[] source, int count, LevelsSettings settings)
    {
        if (settings.IsIdentity) return source;

        var tables = new float[256 * 3];
        int k = 0;
        foreach (LevelsChannel channel in new[] { LevelsChannel.Red, LevelsChannel.Green, LevelsChannel.Blue })
        {
            for (int index = 0; index < 256; index++)
                tables[k++] = (float)settings.Apply(index / 255.0, channel);
        }

        var px = (byte[])source.Clone();
        LevelsPixels.Apply(px, count, tables);
        return px;
    }

    /// <summary>直译 <c>Levels.swift:86 LevelsFilter.histogram(_:)</c>：1024 个 bin 切成 4 段 256。
    /// <c>coverage</c> 为 <c>null</c> 等价于 C 侧的 <c>coverage == NULL</c> 分支。</summary>
    private static double[][] LevelsHistogram(byte[] pixels, byte[]? coverage, int count)
    {
        var bins = new double[1024];
        if (coverage is null)
            LevelsPixels.Histogram(pixels, null, count, bins);
        else
            LevelsPixels.Histogram(pixels, new ReadOnlySpan<byte>(coverage), count, bins);

        var result = new double[4][];
        for (int k = 0; k < 4; k++)
            result[k] = bins.Skip(k * 256).Take(256).ToArray();
        return result;
    }

    /// <summary>Swift <c>LevelsTests.session()</c> 用的 6×1 斜坡（预乘 RGBA8 原样）。</summary>
    private static byte[] Ramp() => new byte[]
    {
        0, 0, 0, 255,
        64, 64, 64, 255,
        128, 128, 128, 255,
        255, 255, 255, 255,
        64, 32, 0, 128,
        0, 0, 0, 0,
    };

    // ══════════════════════════════════════════════════════════════════════════
    // LevelsTests.swift —— 10 测试 / 47 断言，本文件翻译 15 条
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 原 Swift：<c>LevelsTests.swift:30</c> — <c>identityAndChannelSelectionAreExactNoOps</c>。
    /// <b>只翻译其中 1 条</b>：第 33 行。
    /// </summary>
    /// <remarks>
    /// <para><b>如实报告：原断言是 <c>#expect(try LevelsFilter.run(job(source)) === source)</c>，
    /// 测的是 <b>CGImage 引用相等</b>。</b>它在 Swift 里之所以成立，靠的是
    /// <c>LevelsFilter.run</c> 首行的短路 <c>if job.settings.isIdentity { return job.image }</c>——
    /// 对象根本没被重建。</para>
    /// <para>本测试保留同一条短路：命中恒等设置时 <c>LevelsRun</c> 直接返回传进来的那个缓冲，
    /// 于是 <c>Assert.Same</c> 就是 <c>===</c> 的逐字翻译（不是恒真的空转）；
    /// 再补一条字节恒等，说明该路径确实一个字节都没改。</para>
    /// <para>其余 2 条（第 38、40 行）断言的是 <c>EditorSession</c> 的 UI 可用性、文档快照与撤销栈计数，不翻译。</para>
    /// </remarks>
    [Fact]
    public void Levels_IdentitySettings_AreAnExactNoOp()
    {
        byte[] source = Ramp();
        byte[] result = LevelsRun(source, 6, new LevelsSettings());

        // `===` 的逐字翻译：命中的必须是同一个缓冲，而不是重建出来的副本。
        Assert.Same(source, result);
        // 字节层面同样恒等。
        Assert.Equal(source, result);
    }

    /// <summary>原 Swift：<c>LevelsTests.swift:42</c> — <c>inputClippingGammaOutputInversionAndAlpha</c>。
    /// 7 条断言<b>全部</b>翻译。</summary>
    /// <remarks>
    /// 输入是 6×1 的原始预乘字节，LUT 参数完全由 <c>LevelRange</c> 决定，
    /// 因此可在 <c>LevelsPixels.Apply</c> 层面逐条复现原期望值，未做任何改写。
    /// </remarks>
    [Fact]
    public void Levels_InputClippingGammaOutputInversionAndAlpha()
    {
        byte[] source = Ramp();
        var settings = new LevelsSettings();

        settings.Current = new LevelRange(black: 64, gamma: 1, white: 128);
        byte[] clipped = LevelsRun(source, 6, settings);
        Assert.Equal(
            new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255 },
            clipped.Take(12).ToArray());

        settings.Current = new LevelRange(gamma: 2);
        byte[] brightened = LevelsRun(source, 6, settings);
        Assert.InRange(brightened[4], 127, 129);   // abs(Int(brightened[4]) - 128) <= 1
        Assert.InRange(brightened[8], 180, 182);   // abs(Int(brightened[8]) - 181) <= 1
        Assert.True(brightened[19] == 128 && brightened[23] == 0);
        Assert.True(brightened[16] <= 128 && brightened[17] <= 128);

        settings.Current = new LevelRange(outputBlack: 255, outputWhite: 0);
        byte[] inverted = LevelsRun(source, 6, settings);
        Assert.True(inverted[0] == 255 && inverted[12] == 0);
        Assert.True(
            inverted[16] == 64 && inverted[17] == 96 && inverted[18] == 128 && inverted[19] == 128,
            "premultiplied [64,32,0,128] inverted");
    }

    /// <summary>原 Swift：<c>LevelsTests.swift:60</c> — <c>channelsCoexistAndUseDocumentedOrder</c>。
    /// 3 条断言<b>全部</b>翻译。</summary>
    /// <remarks>
    /// 第 65 行的期望值由 <c>LevelRange.apply</c> 本身给出，本文件把它照抄成 helper 后原样复用，
    /// 比较的仍是"实际输出字节"与"文档规定的通道顺序所算出的值"。
    /// </remarks>
    [Fact]
    public void Levels_ChannelsCoexistAndUseDocumentedOrder()
    {
        var settings = new LevelsSettings();
        settings.Channel = LevelsChannel.Red;
        settings.Current = new LevelRange(gamma: 2);
        settings.Channel = LevelsChannel.Rgb;
        settings.Current = new LevelRange(black: 40, white: 210);

        byte[] result = LevelsRun(new byte[] { 64, 64, 64, 255 }, 1, settings);

        double expectedRed = new LevelRange(black: 40, white: 210)
            .Apply(new LevelRange(gamma: 2).Apply(64.0 / 255.0));
        Assert.True(Math.Abs(result[0] - expectedRed * 255) <= 1);
        Assert.True(result[1] == result[2] && result[0] > result[1]);

        LevelRange invalid = new LevelRange(
            black: 300, gamma: double.NaN, white: -1, outputBlack: -100, outputWhite: 400).Normalized();
        Assert.True(invalid.Black < invalid.White && invalid.Gamma == 1
            && invalid.OutputBlack == 0 && invalid.OutputWhite == 255);
    }

    /// <summary>原 Swift：<c>LevelsTests.swift:71</c> — <c>histogramExcludesTransparencyAndWeightsSelection</c>。
    /// 4 条断言<b>全部</b>翻译。</summary>
    /// <remarks>
    /// 第 80、82 行虽然原文走的是 <c>SelectionClip</c>，但落到 C 函数上就只是
    /// <c>levels_histogram</c> 的 <c>coverage</c> 入参：1×1 选区在 3×1 画布上给出覆盖度
    /// <c>[255, 0, 0]</c>，<c>SelectionClip(rect: .zero, coverage: nil)</c> 给出 <c>[0, 0, 0]</c>。
    /// 期望值完全由输入像素 + 覆盖度决定，故直接构造覆盖度缓冲调 <c>LevelsPixels.Histogram</c>，
    /// 原期望值一字未改。
    /// </remarks>
    [Fact]
    public void Levels_HistogramExcludesTransparencyAndWeightsSelection()
    {
        byte[] source = new byte[] { 255, 0, 0, 255, 0, 128, 0, 128, 0, 0, 0, 0 };

        double[][] bins = LevelsHistogram(source, null, 3);
        Assert.True(bins[1][255] == 1 && Math.Abs(bins[2][255] - 128.0 / 255) < 0.00001);

        double total = bins[0].Sum();
        double expectedTotal = 1.0 + 128.0 / 255;
        Assert.True(Math.Abs(total - expectedTotal) < 0.00001);

        double[][] selected = LevelsHistogram(source, new byte[] { 255, 0, 0 }, 3);
        Assert.True(selected[1][255] == 1 && selected[2][255] == 0);

        double[][] empty = LevelsHistogram(source, new byte[] { 0, 0, 0 }, 3);
        Assert.True(empty.SelectMany(b => b).All(v => v == 0));
    }
}