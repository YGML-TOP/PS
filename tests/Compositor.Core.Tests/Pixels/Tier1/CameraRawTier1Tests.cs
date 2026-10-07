using Compositor.Core.Pixels;
using Xunit;

namespace Compositor.Core.Tests.Pixels.Tier1;

/// <summary>
/// Tier 1 验收基准：把 <c>CompositorTests/CameraRawTests.swift</c>（702 行 / 23 个 <c>@Test</c> / 118 条 <c>#expect</c>）
/// 逐条翻译到 xunit，被测对象是 <c>AdjustPixels.CameraRaw</c> 与 <c>AdjustPixels.CameraRawClipOverlay</c>。
/// </summary>
/// <remarks>
/// <para><b>铁律：期望值与容差原样照抄。</b>本文件里每一个数字都来自 Swift 原断言，没有一个是为了让实现通过而改的。
/// 若某条断言在当前实现上预计失败，那是<b>实现的 bug 线索</b>，处理方式是照抄断言、在交付说明里报告，
/// 绝不放宽或改写。</para>
///
/// <para><b>Swift 包装层的处理。</b>Swift 测试不直接调 C 函数，而是经 <c>CameraRawSettings</c>（区间钳制、
/// 色温/色调→通道增益、恒等短路）＋ <c>CameraRawScope</c>（直方图 / 矢量示波器）＋ <c>CGImage</c> 存取。
/// 这三层在本文件里用私有 helper 复刻（见文件末尾的「Swift 包装层复刻」区），不进入 <c>src/</c>：
/// <list type="number">
/// <item><c>Lite</c>：<c>CameraRawSettings</c> 中能映射到 <c>AdjustPixels.CameraRaw</c> 的子集。</item>
/// <item><c>Raster</c>：预乘 RGBA8 + Swift <c>pixels()</c> 的反预乘读回。</item>
/// <item>直方图直接调 C# 已有的 <c>LevelsPixels.Histogram</c>，矢量示波器单独复刻。</item>
/// </list></para>
///
/// <para><b>覆盖度。</b>118 条断言里翻译了 44 条。未翻译的 74 条依赖尚未移植的 Swift 层：
/// effects 组（texture / clarity / dehaze / glow / vignette / grain 的非确定性部分）、
/// 曲线与色彩混合器、三段分级、detail、optics、geometry、calibration、upright、
/// 以及 <c>EditorSession</c> 的历史/提交/取色状态机。它们在 C# 侧没有对应实现，无法复刻。</para>
///
/// <para><b>像素量化假设（唯一一处非照抄的地方，如实声明）。</b>Swift 用
/// <c>CGColor(srgbRed: 230/255, …)</c> 填充，本文件直接按字节 230 建缓冲；alpha 非 1 时按
/// <c>round(straight * alpha / 255)</c> 预乘。这是把 <c>X/255</c> 还原成它显然想表达的那个字节，
/// 因为 CoreGraphics 预乘后的 8-bit 量化在源码里没有规定。凡是受影响的都是 ±1 容差级别的断言。</para>
///
/// <para><b>未经编译验证。</b>本机没有 .NET SDK，语法自查靠人工，详见交付说明。</para>
/// </remarks>
public sealed class CameraRawTier1Tests
{
    private const int ClippingHighlights = 1; // Swift CameraRawClipping.highlights.rawValue
    private const int ClippingShadows = 2;    // Swift CameraRawClipping.shadows.rawValue
    private const int ScopeSide = 64;          // Swift CameraRawScope.scopeSide

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:42  @Test func defaultsLeavePixelsAndAlphaAlone()
    // 4 条 #expect → 翻译 3 条。第 4 条（L50 `!FilterKind.cameraRaw.isImageAdjustment`）
    // 依赖 macOS 侧的枚举分类，无 C# 对应物。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void DefaultsLeavePixelsAndAlphaAlone()
    {
        // L44 `#expect(try pixels(CameraRawSettings().apply(input)) == pixels(input))`
        // 全默认设置是恒等，Swift 在 apply() 首行就短路返回原图，因此像素逐字节相同。
        var input = Raster.Solid(4, 4, 128, 128, 128, 128); // gray(alpha: 0.5)
        var applied = input.Clone().Apply(new Lite());
        Assert.True(SamePixels(input.Pixels(), applied.Pixels()), "默认设置应原样返回像素");

        // L48 `#expect(!broken.isValid)` + L49 `#expect(broken.normalized.exposure == 0 && broken.normalized.temperature == 100)`
        // exposure = NaN 落在 isFinite 之外 → 回退 0；temperature = 400 越界 → 钳到上界 100。
        var broken = new Lite { Exposure = double.NaN, Temperature = 400 };
        Assert.True(!broken.IsValid, "NaN 曝光的设置必须是无效的");
        var normalized = broken.Normalized();
        Assert.True(normalized.Exposure == 0.0, $"normalized.exposure 应回退到 0，实际 {normalized.Exposure}");
        Assert.True(normalized.Temperature == 100.0, $"normalized.temperature 应钳到 100，实际 {normalized.Temperature}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:53  @Test func exposureAddsOneStopAndContrastPivotsAroundMidGray()
    // 5 条 #expect → 全部翻译（全部落在光效组内核上）。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void ExposureAddsOneStopAndContrastPivotsAroundMidGray()
    {
        // L58 `#expect(abs(brighter[0] - 176) <= 2, "+1 stop: …")`
        var oneStop = new Lite { Exposure = 1 };
        var brighter = Raster.Solid(4, 4, 128, 128, 128).Apply(oneStop).Pixel(0);
        Assert.InRange(brighter[0], 174, 178); // 「+1 档：\(brighter)」

        // L59 `#expect(brighter[0] == brighter[1] && brighter[1] == brighter[2])`
        Assert.True(brighter[0] == brighter[1] && brighter[1] == brighter[2], "灰度输入经 +1 档后三通道应仍相等");

        // L61 `#expect(try pixels(settings.apply(translucent))[0][3] == pixels(translucent)[0][3])`
        var translucent = Raster.Solid(4, 4, 128, 128, 128, 128);
        Assert.Equal(translucent.Pixel(0)[3], translucent.Clone().Apply(oneStop).Pixel(0)[3]);

        // L72 `#expect(pushed[0][0] < 10 && pushed[1][0] > 250, "contrast +100 drives the pair apart: …")`
        var pair = Raster.Pair(64, 192);
        var pushed = pair.Clone().Apply(new Lite { Contrast = 100 }).Pixels();
        Assert.True(pushed[0][0] < 10 && pushed[1][0] > 250, $"contrast +100 把这一对拉开：{Describe(pushed)}");

        // L75 `#expect(abs(flat[0][0] - 128) <= 2 && abs(flat[1][0] - 128) <= 2, "contrast −100 meets at mid gray: …")`
        var flat = pair.Clone().Apply(new Lite { Contrast = -100 }).Pixels();
        Assert.True(Math.Abs(flat[0][0] - 128) <= 2 && Math.Abs(flat[1][0] - 128) <= 2,
            $"contrast −100 在中灰处会合：{Describe(flat)}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:78  @Test func tonalSlidersMoveTheEndTheyName()
    // 10 条 #expect → 全部翻译。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void TonalSlidersMoveTheEndTheyName()
    {
        var brightAndMid = Raster.Pair(230, 128);

        // L88 `#expect(recovered[0][0] < 200, "highlights −100 darkens the bright tone: …")`
        var recovered = brightAndMid.Clone().Apply(new Lite { Highlights = -100 }).Pixels();
        Assert.True(recovered[0][0] < 200, $"highlights −100 压暗亮部：{Describe(recovered[0])}");
        // L89 `#expect(abs(recovered[1][0] - 128) <= 2, "and leaves mid gray: …")`
        Assert.InRange(recovered[1][0], 126, 130); // 「并且放过中灰」

        // L94 `#expect(clipped[0][0] == 255, "whites +100 clips the bright tone: …")`
        var clipped = brightAndMid.Clone().Apply(new Lite { Whites = 100 }).Pixels();
        Assert.Equal(255, clipped[0][0]); // 「whites +100 切掉亮部」
        // L95 `#expect(abs(clipped[1][0] - 128) <= 2, "not the midtone: …")`
        Assert.InRange(clipped[1][0], 126, 130); // 「中灰除外」
        // L97 `#expect(viz[0] == [255,255,255,255] && viz[1] == [0,0,0,255], "highlight clipping is not the grade: …")`
        var viz = brightAndMid.Clone().Apply(new Lite { Whites = 100 }, ClippingHighlights).Pixels();
        Assert.True(Is(viz[0], 255, 255, 255, 255) && Is(viz[1], 0, 0, 0, 255),
            $"高光剪切视图看到的不是分级结果：{Describe(viz)}");

        var darkAndMid = Raster.Pair(20, 128);

        // L108 `#expect(opened[0][0] > 50, "shadows +100 opens the dark tone: …")`
        var opened = darkAndMid.Clone().Apply(new Lite { Shadows = 100 }).Pixels();
        Assert.True(opened[0][0] > 50, $"shadows +100 提开暗部：{Describe(opened[0])}");
        // L109 `#expect(abs(opened[1][0] - 128) <= 2, "and leaves mid gray: …")`
        Assert.InRange(opened[1][0], 126, 130); // 「并且放过中灰」

        // L114 `#expect(crushed[0][0] < 20, "blacks −100 crushes the dark tone: …")`
        var crushed = darkAndMid.Clone().Apply(new Lite { Blacks = -100 }).Pixels();
        Assert.True(crushed[0][0] < 20, $"blacks −100 压死暗部：{Describe(crushed[0])}");
        // L115 `#expect(abs(crushed[1][0] - 128) <= 2)`
        Assert.InRange(crushed[1][0], 126, 130);
        // L117 `#expect(shadowViz[0] == [0,0,0,255] && shadowViz[1] == [255,255,255,255], "shadow clipping is not the grade: …")`
        var shadowViz = darkAndMid.Clone().Apply(new Lite { Blacks = -100 }, ClippingShadows).Pixels();
        Assert.True(Is(shadowViz[0], 0, 0, 0, 255) && Is(shadowViz[1], 255, 255, 255, 255),
            $"阴影剪切视图看到的不是分级结果：{Describe(shadowViz)}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:120  @Test func temperatureWarmsAndTintMovesTowardMagenta()
    // 2 条 #expect → 全部翻译。色温/色调→增益的换算在 <c>Lite.Gains</c> 里照抄 Swift。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void TemperatureWarmsAndTintMovesTowardMagenta()
    {
        // L125 `#expect(warm[0] > 128 && warm[2] < 128 && warm[0] > warm[2], "warm shifts red up and blue down: …")`
        var warm = Raster.Solid(4, 4, 128, 128, 128).Apply(new Lite { Temperature = 100 }).Pixel(0);
        Assert.True(warm[0] > 128 && warm[2] < 128 && warm[0] > warm[2], $"偏暖抬红压蓝：{Describe(warm)}");

        // L129 `#expect(magenta[1] < 128 && magenta[1] < magenta[0] && magenta[1] < magenta[2], "magenta lowers green: …")`
        var magenta = Raster.Solid(4, 4, 128, 128, 128).Apply(new Lite { Tint = 100 }).Pixel(0);
        Assert.True(magenta[1] < 128 && magenta[1] < magenta[0] && magenta[1] < magenta[2], $"偏品压绿：{Describe(magenta)}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:132  @Test func vibranceFavorsDullColorsAndProtectsSkinWhileSaturationDoesNot()
    // 4 条 #expect → 全部翻译。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void VibranceFavorsDullColorsAndProtectsSkinWhileSaturationDoesNot()
    {
        var vibrance = new Lite { Vibrance = 100 };

        var dullGreen = Raster.Solid(4, 4, 77, 153, 77);
        var saturatedGreen = Raster.Solid(4, 4, 20, 200, 20);
        var skin = Raster.Solid(4, 4, 153, 115, 77);

        int dullBefore = Chroma(dullGreen.Pixel(0));
        int saturatedBefore = Chroma(saturatedGreen.Pixel(0));
        int skinBefore = Chroma(skin.Pixel(0));

        int dullDelta = Chroma(dullGreen.Clone().Apply(vibrance).Pixel(0)) - dullBefore;
        int saturatedDelta = Chroma(saturatedGreen.Clone().Apply(vibrance).Pixel(0)) - saturatedBefore;
        int skinDelta = Chroma(skin.Clone().Apply(vibrance).Pixel(0)) - skinBefore;

        // L144 `#expect(dullDelta > saturatedDelta + 10, "dull \(dullDelta) vs saturated \(saturatedDelta)")`
        // ⚠️ 上一行的 \( \) 是 Swift 的插值语法，在 C# 逐字插值字符串 @$"" 里原样保留即为字面量，
        // 不必也不该"翻译"成 {dullDelta}——本文件的原则是断言消息也照抄原文本。
        Assert.True(dullDelta > saturatedDelta + 10, $@"暗色 \(dullDelta) 对比饱和色 {saturatedDelta}");
        // L145 `#expect(dullBefore == skinBefore)`
        Assert.Equal(dullBefore, skinBefore);
        // L146 `#expect(dullDelta > skinDelta + 10, "skin is protected at the same saturation: …")`
        Assert.True(dullDelta > skinDelta + 10, $"同饱和度下肤色被保护：肤色 {skinDelta} 对比暗色 {dullDelta}");

        // L158 `#expect(abs(redRatio - 2) < 0.15 && abs(blueRatio - 2) < 0.15, "saturation doubles both: …")`
        var red = Raster.Solid(4, 4, 160, 120, 120);
        var blue = Raster.Solid(4, 4, 100, 100, 140);
        double redBefore = Chroma(red.Pixel(0));
        double blueBefore = Chroma(blue.Pixel(0));
        var saturation = new Lite { Saturation = 100 };
        double redRatio = Chroma(red.Clone().Apply(saturation).Pixel(0)) / redBefore;
        double blueRatio = Chroma(blue.Clone().Apply(saturation).Pixel(0)) / blueBefore;
        Assert.True(Math.Abs(redRatio - 2) < 0.15 && Math.Abs(blueRatio - 2) < 0.15,
            $"饱和度把两者都翻倍：{redRatio}, {blueRatio}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:161  @Test func eyedropperAndAutoNeutralizeAWarmPixel()
    // 6 条 #expect → 翻译 3 条（L172 / L178 / L190）。
    // 未翻译：L187、L188、L201 断言的是 EditorSession 里 whiteBalance 模式与面板状态，C# 无对应层。
    // L190 原文走 session 取色器；因输入是纯色填充，取色器解与 Neutralize 解逐位相同，故照此复刻。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void EyedropperAndAutoNeutralizeAWarmPixel()
    {
        var warm = Raster.Solid(8, 8, 160, 140, 120);
        var before = warm.Pixel(0);

        // L172 `#expect(chroma(neutral) < chroma(before) / 2, "eyedropper pulls the cast in: …")`
        var solved = Neutralize(160 / 255.0, 140 / 255.0, 120 / 255.0);
        Assert.True(solved.HasValue, "取色器应能解出该暖色");
        var eyedrop = new Lite { Temperature = solved!.Value.Temperature, Tint = solved!.Value.Tint };
        var neutral = warm.Clone().Apply(eyedrop).Pixel(0);
        Assert.True(Chroma(neutral) < Chroma(before) / 2, $"取色器把色偏拉回来：{Describe(before)} → {Describe(neutral)}");

        // L178 `#expect(chroma(averaged) < chroma(before) / 2, "auto matches the solid color: …")`
        var auto = AutoBalance(warm);
        Assert.True(auto.HasValue, "自动白平衡应能解出该纯色");
        var autoSettings = new Lite { Temperature = auto!.Value.Temperature, Tint = auto!.Value.Tint };
        var averaged = warm.Clone().Apply(autoSettings).Pixel(0);
        Assert.True(Chroma(averaged) < Chroma(before) / 2, $"自动白平衡匹配这块纯色：{Describe(averaged)}");

        // L190 `#expect(chroma(sampled) < chroma(before) / 2, "the canvas sample neutralizes: …")`
        var sampled = warm.Clone().Apply(eyedrop).Pixel(0);
        Assert.True(Chroma(sampled) < Chroma(before) / 2, $"画布取色同样完成中和：{Describe(sampled)}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:205  @Test func autoWhiteBalanceNeutralizesAfterTheScan()
    // 4 条 #expect → 翻译 1 条（L219）。
    // 未翻译：L217 / L229 / L230 断言的是 session 扫描后写入的 whiteBalance 模式与滑块留空状态。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void AutoWhiteBalanceNeutralizesAfterTheScan()
    {
        // L219 `#expect(chroma(averaged) < chroma(before) / 2, "auto neutralizes after the scan: …")`
        var warm = Raster.Solid(8, 8, 160, 140, 120);
        var before = warm.Pixel(0);
        var auto = AutoBalance(warm);
        Assert.True(auto.HasValue, "自动白平衡应能解出该纯色");
        var settings = new Lite { Temperature = auto!.Value.Temperature, Tint = auto!.Value.Tint };
        var averaged = warm.Clone().Apply(settings).Pixel(0);
        Assert.True(Chroma(averaged) < Chroma(before) / 2, $"扫描之后自动白平衡完成中和：{Describe(averaged)}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:233  @Test func hiddenGroupIsLeftOutAndOkIsOneUndoOrNone()
    // 12 条 #expect → 翻译 4 条（L241 / L242 / L244 / L245）。
    // 未翻译：L254、L255、L264、L265、L272、L273、L281、L283 全部依赖 EditorSession 的
    // 撤销栈、图层烘焙与 filterEdit 生命周期。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void HiddenGroupIsLeftOutAndOkIsOneUndoOrNone()
    {
        var input = Raster.Solid(8, 8, 128, 128, 128);
        var source = input.Pixels();
        var settings = new Lite { Exposure = 1, Temperature = 40 };

        // L241 `#expect(colorOnly != source)`
        var colorOnly = input.Clone().Apply(settings.Applying(showsLight: false, showsColor: true)).Pixels();
        Assert.True(!SamePixels(colorOnly, source), "关掉光效组后颜色组仍应改变像素");
        // L242 `#expect(hiddenLight.exposure == 0 && hiddenLight.temperature == 40)`
        var hiddenLight = settings.Applying(showsLight: false, showsColor: true);
        Assert.True(hiddenLight.Exposure == 0 && hiddenLight.Temperature == 40,
            "关掉光效组只清曝光，色温应留着");
        // L244 `#expect(neither.isIdentity)`
        var neither = settings.Applying(showsLight: false, showsColor: false);
        Assert.True(neither.IsIdentity, "两组都关掉后应是恒等设置");
        // L245 `#expect(try pixels(neither.apply(input)) == source)`
        Assert.True(SamePixels(input.Clone().Apply(neither).Pixels(), source), "恒等设置应原样返回像素");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:458  @Test func grainIsStableAndTheEffectsEyeDropsTheWholeGroup()
    // 8 条 #expect → 翻译 5 条（L465 / L469 / L470 / L471 / L474），走 AdjustPixels.Grain。
    // 未翻译：L484 / L485 / L486 依赖 texture（局部对比度带），C# 侧没有 adjust_camera_raw_effects。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void GrainIsStableAndTheEffectsEyeDropsTheWholeGroup()
    {
        var field = Raster.Solid(16, 16, 128, 128, 128);
        var fieldPixels = field.Pixels();

        // L465 `#expect(silent == fieldPixels, "size and roughness do nothing at amount zero")`
        // 粒径与粗糙度不是 isIdentity 的一部分，amount 为 0 时 apply() 不调 adjust_grain。
        var settings = new Lite { GrainSize = 40, GrainRoughness = 80 };
        Assert.True(SamePixels(field.Clone().Apply(settings, seed: 4).Pixels(), fieldPixels),
            "amount 为零时粒径与粗糙度不产生颗粒");

        // L466 `settings.grainAmount = 70`
        settings = new Lite { GrainSize = 40, GrainRoughness = 80, GrainAmount = 70 };
        var first = field.Clone().Apply(settings, seed: 4).Pixels();
        var second = field.Clone().Apply(settings, seed: 4).Pixels();

        // L469 `#expect(first == second, "the same seed keeps the same grain")`
        Assert.True(SamePixels(first, second), "同一个 seed 必须给出同一片颗粒");
        // L470 `#expect(first != fieldPixels)`
        Assert.True(!SamePixels(first, fieldPixels), "颗粒必须改变像素");
        // L471 `#expect(first[0][0] == first[0][1] && first[0][1] == first[0][2], "grain moves brightness only")`
        Assert.True(first[0][0] == first[0][1] && first[0][1] == first[0][2], "颗粒只动亮度");

        // L474 `#expect(clearPixels[0][3] == 0)`
        var clear = Raster.Solid(4, 4, 128, 128, 128, 0);
        Assert.Equal(0, clear.Clone().Apply(settings, seed: 4).Pixel(0)[3]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 对应 CameraRawTests.swift:489  @Test func histogramFollowsTheGradeAndClippingPaintStaysOffTheResult()
    // 7 条 #expect → 全部翻译。直方图走 C# 已有的 LevelsPixels.Histogram（即 levels_histogram），
    // 矢量示波器在 helper 里照抄 CameraRawScope.make。
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public void HistogramFollowsTheGradeAndClippingPaintStaysOffTheResult()
    {
        var black = Raster.Solid(4, 4, 0, 0, 0);
        var white = Raster.Solid(4, 4, 255, 255, 255);

        // L494 `#expect(peakIndex(blackScope.red) == 0)`
        Assert.Equal(0, PeakIndex(Histogram(black), 256));
        // L495 `#expect(peakIndex(whiteScope.red) == 255)`
        Assert.Equal(255, PeakIndex(Histogram(white), 256));
        // L499 `#expect(peakIndex(shifted.red) > 128, "exposure moves the midtones toward white")`
        var shifted = Raster.Solid(4, 4, 128, 128, 128).Apply(new Lite { Exposure = 1 });
        Assert.True(PeakIndex(Histogram(shifted), 256) > 128, "曝光把中间调推向白端");

        // L502 `#expect(shadowed[2] > shadowed[0], "clipped shadows are painted blue: …")`
        var shadowed = black.Clone().ClipOverlay(shadows: true, highlights: false).Pixel(0);
        Assert.True(shadowed[2] > shadowed[0], $"被切的阴影涂成蓝色：{Describe(shadowed)}");
        // L504 `#expect(highlighted[0] > highlighted[2], "clipped highlights are painted red: …")`
        var highlighted = white.Clone().ClipOverlay(shadows: false, highlights: true).Pixel(0);
        Assert.True(highlighted[0] > highlighted[2], $"被切的高光涂成红色：{Describe(highlighted)}");
        // L507 `#expect(untouched == originalBlack)`
        Assert.True(SamePixels(black.Clone().ClipOverlay(shadows: false, highlights: false).Pixels(), black.Pixels()),
            "两个开关都关时叠加层不得动结果");

        // L512 `#expect(hottest % CameraRawScope.scopeSide > CameraRawScope.scopeSide / 2, "red sits on the right of the vectorscope")`
        var scope = Vectorscope(Raster.Solid(4, 4, 255, 0, 0));
        var hottest = HottestIndex(scope);
        Assert.True(hottest % ScopeSide > ScopeSide / 2, "红色应落在矢量示波器的右半边");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Swift 包装层复刻  ——  以下全部只服务于本文件，不进入 src/
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>CameraRawSettings</c> 中能映射到 <c>adjust_camera_raw</c> / <c>adjust_grain</c> 的那部分。
    /// 字段默认值、区间钳制（<c>normalized</c>）、有效性（<c>isValid</c>）、
    /// 通道增益（<c>gains</c>）、面板开关（<c>applying</c>）、恒等短路（<c>apply</c>）全部照抄 Swift。
    /// </summary>
    /// <remarks>
    /// 未建模：curve / mixer / grading / detail / optics / geometry / calibration，
    /// 以及 effects 里的 texture / clarity / dehaze / glow / vignette —— 这些在 C# 侧没有实现，
    /// 它们的字段一律保持 0（= Swift 默认值），因此 <c>IsIdentity</c> 对这些组恒为「未调整」，
    /// 这与本文件所有用例的前提一致。
    /// </remarks>
    private sealed class Lite
    {
        public const double TemperatureGain = 0.35; // CameraRawSettings.temperatureGain
        public const double TintRedBlue = 0.15;     // CameraRawSettings.tintRedBlue
        public const double TintGreen = 0.30;       // CameraRawSettings.tintGreen

        public double Temperature;
        public double Tint;
        public double Exposure;
        public double Contrast;
        public double Highlights;
        public double Shadows;
        public double Whites;
        public double Blacks;
        public double Vibrance;
        public double Saturation;
        public double GrainAmount;
        public double GrainSize = 25;      // Swift 默认 25
        public double GrainRoughness = 50; // Swift 默认 50

        public bool AdjustsLight =>
            Exposure != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 || Whites != 0 || Blacks != 0;

        public bool AdjustsColor =>
            Temperature != 0 || Tint != 0 || Vibrance != 0 || Saturation != 0;

        /// <remarks>Swift 的 adjustsEffects 还含 texture/clarity/dehaze/glow/vignette，此处只建模了 grain。</remarks>
        public bool AdjustsEffects => GrainAmount != 0;

        public bool IsIdentity => !AdjustsLight && !AdjustsColor && !AdjustsEffects;

        /// <summary>Camera Raw 的 0…100 粒径，换算到 <c>adjust_grain</c> 的像素尺度。</summary>
        public double GrainKernelSize => 0.5 + (GrainSize / 100.0) * 19.5;

        /// <summary>
        /// 色温与色调换算成三通道乘子，中性为 (1, 1, 1)。
        /// </summary>
        public (double Red, double Green, double Blue) Gains
        {
            get
            {
                double warm = Temperature / 100.0;
                double magenta = Tint / 100.0;
                return (1.0 + TemperatureGain * warm + TintRedBlue * magenta,
                        1.0 - TintGreen * magenta,
                        1.0 - TemperatureGain * warm + TintRedBlue * magenta);
            }
        }

        /// <summary>
        /// <c>isValid</c>：曝光必须有限且落在 −5…5，其余色调类滑块有限且落在 −100…100。
        /// </summary>
        public bool IsValid =>
            IsFinite(Exposure) && Exposure >= -5 && Exposure <= 5
            && InToneRange(Contrast) && InToneRange(Highlights) && InToneRange(Shadows)
            && InToneRange(Whites) && InToneRange(Blacks) && InToneRange(Temperature)
            && InToneRange(Tint) && InToneRange(Vibrance) && InToneRange(Saturation);

        /// <summary>
        /// <c>normalized</c>：有限则钳到区间，非有限则回退到 fallback。
        /// 对应 <c>ImageAdjustmentPixels.clamp</c>。
        /// </summary>
        public Lite Normalized() => new Lite
        {
            Exposure = Clamp(Exposure, -5, 5, 0),
            Contrast = Clamp(Contrast, -100, 100, 0),
            Highlights = Clamp(Highlights, -100, 100, 0),
            Shadows = Clamp(Shadows, -100, 100, 0),
            Whites = Clamp(Whites, -100, 100, 0),
            Blacks = Clamp(Blacks, -100, 100, 0),
            Temperature = Clamp(Temperature, -100, 100, 0),
            Tint = Clamp(Tint, -100, 100, 0),
            Vibrance = Clamp(Vibrance, -100, 100, 0),
            Saturation = Clamp(Saturation, -100, 100, 0),
            GrainAmount = Clamp(GrainAmount, 0, 100, 0),
            GrainSize = Clamp(GrainSize, 0, 100, 25),
            GrainRoughness = Clamp(GrainRoughness, 0, 100, 50),
        };

        /// <summary><c>applying(showsLight:showsColor:showsEffects:)</c>：面板上关掉的那一组清零，其余留着。</summary>
        public Lite Applying(bool showsLight, bool showsColor, bool showsEffects = true)
        {
            var result = new Lite
            {
                Temperature = Temperature,
                Tint = Tint,
                Exposure = Exposure,
                Contrast = Contrast,
                Highlights = Highlights,
                Shadows = Shadows,
                Whites = Whites,
                Blacks = Blacks,
                Vibrance = Vibrance,
                Saturation = Saturation,
                GrainAmount = GrainAmount,
                GrainSize = GrainSize,
                GrainRoughness = GrainRoughness,
            };
            if (!showsLight)
            {
                result.Exposure = 0; result.Contrast = 0; result.Highlights = 0;
                result.Shadows = 0; result.Whites = 0; result.Blacks = 0;
            }
            if (!showsColor)
            {
                result.Temperature = 0; result.Tint = 0;
                result.Vibrance = 0; result.Saturation = 0;
            }
            if (!showsEffects) result.GrainAmount = 0;
            return result;
        }

        private static bool InToneRange(double value) => IsFinite(value) && value >= -100 && value <= 100;

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static double Clamp(double value, double low, double high, double fallback) =>
            IsFinite(value) ? Math.Min(high, Math.Max(low, value)) : fallback;
    }

    /// <summary>
    /// 预乘 RGBA8 画布，外加 Swift <c>pixels(_:)</c> 的反预乘读回。
    /// </summary>
    /// <remarks>
    /// Swift 的 <c>image(red:green:blue:alpha:)</c> 走 CoreGraphics 预乘填充，
    /// 这里按字节直接建缓冲；量化假设见类注释。
    /// </remarks>
    private sealed class Raster
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Stride;
        public readonly byte[] Data;

        private Raster(int width, int height)
        {
            Width = width;
            Height = height;
            Stride = width * 4;
            Data = new byte[Stride * height];
        }

        /// <summary>整幅同一颜色。<paramref name="r"/><paramref name="g"/><paramref name="b"/> 是直通 sRGB 字节。</summary>
        public static Raster Solid(int width, int height, int r, int g, int b, int a = 255)
        {
            var img = new Raster(width, height);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = y * img.Stride + x * 4;
                    img.Data[p] = Premultiply((byte)r, (byte)a);
                    img.Data[p + 1] = Premultiply((byte)g, (byte)a);
                    img.Data[p + 2] = Premultiply((byte)b, (byte)a);
                    img.Data[p + 3] = (byte)a;
                }
            return img;
        }

        /// <summary>2×1，左边一种灰右边另一种灰，对应 Swift 里逐像素 fill 出来的色块对。</summary>
        public static Raster Pair(int first, int second)
        {
            var img = new Raster(2, 1);
            for (int x = 0; x < 2; x++)
            {
                int p = x * 4;
                byte v = (byte)(x == 0 ? first : second);
                img.Data[p] = v;
                img.Data[p + 1] = v;
                img.Data[p + 2] = v;
                img.Data[p + 3] = 255;
            }
            return img;
        }

        public Raster Clone()
        {
            var copy = new Raster(Width, Height);
            Array.Copy(Data, copy.Data, Data.Length);
            return copy;
        }

        /// <summary>
        /// Swift <c>pixels(_:)</c> 的读回：alpha 为 0 时三通道记 0，否则按 <c>(v * 255 + alpha / 2) / alpha</c> 反预乘。
        /// </summary>
        public int[] Pixel(int index)
        {
            int p = index * 4;
            int alpha = Data[p + 3];
            var pixel = new int[4];
            for (int channel = 0; channel < 3; channel++)
                pixel[channel] = alpha == 0 ? 0 : Math.Min(255, (Data[p + channel] * 255 + alpha / 2) / alpha);
            pixel[3] = alpha;
            return pixel;
        }

        public int[][] Pixels()
        {
            var all = new int[Width * Height][];
            for (int index = 0; index < Width * Height; index++) all[index] = Pixel(index);
            return all;
        }

        /// <summary>
        /// <c>CameraRawSettings.apply(image, clipping:scale:seed:…)</c> 的等价物，原地修改。
        /// </summary>
        /// <remarks>
        /// 照抄 Swift 的三个前提：恒等即短路返回原图；<c>adjust_camera_raw</c> 只在光效组或颜色组
        /// 有动作、或在做剪切视图时才跑；<c>adjust_grain</c> 只在 <c>grainAmount &gt; 0</c> 时跑。
        /// Swift 里还会在之后依次跑 effects / 曲线 / 混合器 / 分级 / detail / optics，
        /// 这些在 C# 侧没有实现，本文件用到的用例里它们也全为 0。
        /// </remarks>
        public Raster Apply(Lite settings, int clipping = 0, uint seed = 0)
        {
            var s = settings.Normalized();
            if (s.IsIdentity && clipping == 0) return this;
            if (s.AdjustsLight || s.AdjustsColor || clipping != 0)
            {
                var gains = s.Gains;
                AdjustPixels.CameraRaw(Data, Width, Height, Stride,
                    gains.Red, gains.Green, gains.Blue,
                    s.Exposure, s.Contrast, s.Highlights, s.Shadows, s.Whites, s.Blacks,
                    s.Vibrance, s.Saturation, clipping);
            }
            if (s.GrainAmount > 0)
            {
                AdjustPixels.Grain(Data, Width, Height, Stride,
                    s.GrainAmount, s.GrainKernelSize, s.GrainRoughness, seed, 0.0, 0.0, 1.0);
            }
            return this;
        }

        /// <summary><c>CameraRawScope.overlay(image, shadows:highlights:)</c>：只叠剪切标记，不做分级。</summary>
        public Raster ClipOverlay(bool shadows, bool highlights)
        {
            AdjustPixels.CameraRawClipOverlay(Data, Width, Height, Stride,
                shadows ? 1 : 0, highlights ? 1 : 0);
            return this;
        }

        private static byte Premultiply(byte straight, byte alpha) =>
            (byte)Math.Round(straight * alpha / 255.0, MidpointRounding.AwayFromZero);
    }

    /// <summary><c>levels_histogram</c> 的 1024 个 bin（0…255 亮度，其后各 256 个是 R/G/B）。</summary>
    private static double[] Histogram(Raster img)
    {
        var bins = new double[1024];
        LevelsPixels.Histogram(img.Data, null, img.Width * img.Height, bins);
        return bins;
    }

    /// <summary><c>peakIndex(_:)</c>：取 <c>max { $0.element &lt; $1.element }</c>，并列时保留第一个。</summary>
    private static int PeakIndex(double[] bins, int offset)
    {
        int best = -1;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < 256; i++)
            if (bins[offset + i] > bestValue)
            {
                bestValue = bins[offset + i];
                best = i;
            }
        return best;
    }

    /// <summary><c>CameraRawScope.make(_:)</c> 里的矢量示波器：自圆心向外的密度，索引 <c>y * scopeSide + x</c>。</summary>
    private static double[] Vectorscope(Raster img)
    {
        var scope = new double[ScopeSide * ScopeSide];
        for (int index = 0; index < img.Width * img.Height; index++)
        {
            int p = index * 4;
            double alpha = img.Data[p + 3];
            if (alpha == 0) continue;
            double red = Math.Min(1.0, img.Data[p] / alpha);
            double green = Math.Min(1.0, img.Data[p + 1] / alpha);
            double blue = Math.Min(1.0, img.Data[p + 2] / alpha);
            double maxChannel = Math.Max(red, Math.Max(green, blue));
            double minChannel = Math.Min(red, Math.Min(green, blue));
            double chroma = maxChannel - minChannel;
            if (chroma <= 1e-4 || maxChannel <= 1e-4) continue;
            double hue;
            if (maxChannel == red) hue = (green - blue) / chroma;
            else if (maxChannel == green) hue = 2 + (blue - red) / chroma;
            else hue = 4 + (red - green) / chroma;
            hue /= 6;
            if (hue < 0) hue += 1;
            double angle = hue * 2 * Math.PI;
            double saturation = chroma / maxChannel;
            double plotX = 0.5 + Math.Cos(angle) * saturation * 0.48;
            double plotY = 0.5 + Math.Sin(angle) * saturation * 0.48;
            int column = Math.Min(ScopeSide - 1, Math.Max(0, (int)(plotX * ScopeSide)));
            int row = Math.Min(ScopeSide - 1, Math.Max(0, (int)(plotY * ScopeSide)));
            scope[row * ScopeSide + column] += alpha / 255;
        }
        return scope;
    }

    private static int HottestIndex(double[] scope)
    {
        int best = 0;
        double bestValue = scope[0];
        for (int i = 1; i < scope.Length; i++)
            if (scope[i] > bestValue)
            {
                bestValue = scope[i];
                best = i;
            }
        return best;
    }

    /// <summary><c>CameraRawSettings.neutralize(straightRed:green:blue:)</c>：解出让该像素变中性的色温与色调。</summary>
    private static (double Temperature, double Tint)? Neutralize(double straightRed, double straightGreen, double straightBlue) =>
        NeutralizeLinear(Decode(straightRed), Decode(straightGreen), Decode(straightBlue));

    private static (double Temperature, double Tint)? NeutralizeLinear(double red, double green, double blue)
    {
        if (red <= 1e-4 || green <= 1e-4 || blue <= 1e-4) return null;
        double a1 = Lite.TemperatureGain * red;
        double b1 = Lite.TintRedBlue * red + Lite.TintGreen * green;
        double c1 = green - red;
        double a2 = -Lite.TemperatureGain * blue;
        double b2 = Lite.TintRedBlue * blue + Lite.TintGreen * green;
        double c2 = green - blue;
        double determinant = a1 * b2 - a2 * b1;
        if (Math.Abs(determinant) <= 1e-8) return null;
        double warm = (c1 * b2 - c2 * b1) / determinant;
        double magenta = (a1 * c2 - a2 * c1) / determinant;
        if (double.IsNaN(warm) || double.IsInfinity(warm) || double.IsNaN(magenta) || double.IsInfinity(magenta)) return null;
        return (warm * 100, magenta * 100);
    }

    /// <summary><c>CameraRawSettings.autoBalance(of:)</c>：对不透明像素取线性光平均（灰世界），再解中和。</summary>
    private static (double Temperature, double Tint)? AutoBalance(Raster img)
    {
        double red = 0, green = 0, blue = 0, count = 0;
        for (int index = 0; index < img.Width * img.Height; index++)
        {
            int p = index * 4;
            double alpha = img.Data[p + 3];
            if (alpha == 0) continue;
            red += Decode(Math.Min(1.0, img.Data[p] / alpha));
            green += Decode(Math.Min(1.0, img.Data[p + 1] / alpha));
            blue += Decode(Math.Min(1.0, img.Data[p + 2] / alpha));
            count += 1;
        }
        if (count == 0) return null;
        return NeutralizeLinear(red / count, green / count, blue / count);
    }

    private static double Decode(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    private static int Chroma(int[] pixel) =>
        Math.Max(pixel[0], Math.Max(pixel[1], pixel[2])) - Math.Min(pixel[0], Math.Min(pixel[1], pixel[2]));

    private static bool Is(int[] pixel, int r, int g, int b, int a) =>
        pixel[0] == r && pixel[1] == g && pixel[2] == b && pixel[3] == a;

    /// <summary>Swift 的 <c>[[Int]]</c> 逐元素相等比较（数组本身是引用相等，不能直接比）。</summary>
    private static bool SamePixels(int[][] a, int[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            for (int channel = 0; channel < 4; channel++)
                if (a[i][channel] != b[i][channel])
                    return false;
        return true;
    }

    private static string Describe(int[] pixel) => $"[{string.Join(", ", pixel)}]";

    private static string Describe(int[][] pixels)
    {
        var parts = new string[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) parts[i] = Describe(pixels[i]);
        return $"[{string.Join(", ", parts)}]";
    }
}