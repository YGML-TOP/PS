using Compositor.Core.Pixels;
using Xunit;

namespace Compositor.Core.Tests.Pixels;

/// <summary>
/// AdjustPixels 直译回归（分部一 + 分部二）。
/// </summary>
/// <remarks>
/// <para>与 <see cref="TranslationTests"/> 同一原则：断言的是<b>能从 C 源码手工推导出的确定值</b>，
/// 而不是从 C# 实现跑出来的黄金值——后者会把直译错误一起锁死。
/// 真正的黄金样本需 Mac 版实测导出，属波次 2 标定（铁律 3）。</para>
/// <para>本文件覆盖：<c>adjust_gradient_map</c> / <c>adjust_grain</c> / <c>rgba_clamp_premultiplied</c> /
/// <c>adjust_black_white</c> / <c>adjust_color_balance</c> / <c>adjust_camera_raw</c> /
/// <c>adjust_camera_raw_clip_overlay</c>。</para>
/// </remarks>
public sealed class AdjustPixelsTests
{
    // ────────────────── rgba_clamp_premultiplied ──────────────────
    // 这条是铁律 2 的兜底，断言可以完全由源码推导，是全文件最强的一条。

    [Fact]
    public void ClampPremultiplied_ClampsRgbToAlpha()
    {
        // R/G/B 全部超过 alpha → 全部钳到 alpha
        var px = new byte[] { 200, 100, 255, 50 };
        AdjustPixels.ClampPremultiplied(px, 1);
        Assert.Equal(50, px[0]);
        Assert.Equal(50, px[1]);
        Assert.Equal(50, px[2]);
        Assert.Equal(50, px[3]);
    }

    [Fact]
    public void ClampPremultiplied_LeavesLegalPixelsAlone()
    {
        var px = new byte[] { 10, 20, 30, 200 };
        AdjustPixels.ClampPremultiplied(px, 1);
        Assert.Equal(10, px[0]);
        Assert.Equal(20, px[1]);
        Assert.Equal(30, px[2]);
        Assert.Equal(200, px[3]); // alpha 本身绝不被改
    }

    [Fact]
    public void ClampPremultiplied_HandlesEqualChannels()
    {
        // RGB 恰好等于 alpha 时不改变（用的是 > 不是 >=）
        var px = new byte[] { 128, 128, 128, 128 };
        AdjustPixels.ClampPremultiplied(px, 1);
        Assert.Equal(128, px[0]);
        Assert.Equal(128, px[1]);
        Assert.Equal(128, px[2]);
    }

    [Fact]
    public void ClampPremultiplied_CountLimitsWork()
    {
        var px = new byte[] { 255, 255, 255, 0, 5, 5, 5, 5 };
        AdjustPixels.ClampPremultiplied(px, 1); // count=1 ⇒ 只覆盖 pixels[0..3]

        // rgba_clamp_premultiplied：每通道钳到**本像素**的 alpha。像素 0 的 alpha=0 ⇒ RGB 全变 0。
        // ⚠️ px[1] 是像素 0 的**绿**通道，不是"第 2 个像素"——像素 1 从 px[4] 开始。
        //    初版把 px[1] 写回 255 当作"未处理保持原值"，恒错。
        Assert.Equal(0, px[0]);   // R 钳到 alpha=0
        Assert.Equal(0, px[1]);   // G 同样被钳（不是"未处理"）
        Assert.Equal(0, px[2]);
        Assert.Equal(0, px[3]);   // alpha 本身从不被改
        Assert.Equal(5, px[4]);   // 像素 1 完全未处理
        Assert.Equal(5, px[6]);
    }

    // ────────────────── adjust_grain ──────────────────

    [Fact]
    public void Grain_NonPositiveAmount_IsNoOp()
    {
        var px = new byte[] { 100, 120, 140, 255 };
        var before = (byte[])px.Clone();
        AdjustPixels.Grain(px, 1, 1, 4, 0.0, 10.0, 50.0, 1u, 0, 0, 1.0);
        Assert.Equal(before, px); // amount=0 → 源码首行直接 return
    }

    [Fact]
    public void Grain_NonPositiveUnitsPerPixel_IsNoOp()
    {
        var px = new byte[] { 100, 120, 140, 255 };
        var before = (byte[])px.Clone();
        AdjustPixels.Grain(px, 1, 1, 4, 50.0, 10.0, 50.0, 1u, 0, 0, 0.0);
        Assert.Equal(before, px);
    }

    [Fact]
    public void Grain_SkipsTransparentAndPreservesAlpha()
    {
        const int w = 8, h = 8, stride = w * 4;
        var px = new byte[stride * h];
        var alphas = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = 120; px[p + 1] = 90; px[p + 2] = 60;
                px[p + 3] = (byte)((x * 8 + y * 16) % 256);
                alphas[y * w + x] = px[p + 3];
            }

        AdjustPixels.Grain(px, w, h, stride, 60.0, 8.0, 40.0, 20251007u, 0, 0, 1.0);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(alphas[y * w + x], px[y * stride + x * 4 + 3]);
    }

    [Fact]
    public void Grain_IsDeterministic()
    {
        static byte[] Make()
        {
            var b = new byte[8 * 4 * 8];
            for (int i = 0; i < b.Length; i += 4) { b[i] = 128; b[i + 1] = 128; b[i + 2] = 128; b[i + 3] = 255; }
            return b;
        }
        var a = Make(); var b = Make();
        AdjustPixels.Grain(a, 8, 8, 32, 50.0, 4.0, 30.0, 777u, 3.0, 5.0, 2.0);
        AdjustPixels.Grain(b, 8, 8, 32, 50.0, 4.0, 30.0, 777u, 3.0, 5.0, 2.0);
        Assert.Equal(a, b);
    }

    // ────────────────── adjust_gradient_map ──────────────────

    [Fact]
    public void GradientMap_PreservesAlphaAndSkipsTransparent()
    {
        const int w = 2, h = 1, stride = w * 4;
        var px = new byte[] { 200, 200, 200, 255, 200, 200, 200, 0 };
        var table = new byte[768]; // 全黑表

        AdjustPixels.GradientMap(px, w, h, stride, table);

        Assert.Equal(255, px[3]);
        Assert.Equal(0, px[0]);   // 不透明像素被黑色表压黑
        Assert.Equal(0, px[1]);
        Assert.Equal(0, px[2]);
        Assert.Equal(0, px[7]);   // 透明像素的 RGB 原样保留
        Assert.Equal(200, px[4]);
    }

    [Fact]
    public void GradientMap_MapsBrightPixelToTopOfTable()
    {
        const int w = 1, h = 1, stride = 4;
        var px = new byte[] { 255, 255, 255, 255 };
        var table = new byte[768];
        table[255 * 3 + 0] = 11; table[255 * 3 + 1] = 22; table[255 * 3 + 2] = 33;

        AdjustPixels.GradientMap(px, w, h, stride, table);

        // level = (2126*255 + 7152*255 + 722*255 + 5000) / 10000 = 2555000/10000 = 255（整数除法截断 .5）
        // → color = table + 255*3 = {11, 22, 33}，再按 alpha 乘回：(11*255+127)/255 = 11，其余同理。
        // ⚠️ 初版注释漏乘了 r/g/b 的 255，算出 level=1 并断言 0，是错的。
        Assert.Equal(11, px[0]);
        Assert.Equal(22, px[1]);
        Assert.Equal(33, px[2]);
        Assert.Equal(255, px[3]); // alpha 保持

        // 同一张表作用于同色像素必须完全一致（无隐藏状态）
        var px2 = new byte[] { 255, 255, 255, 255 };
        AdjustPixels.GradientMap(px2, w, h, stride, table);
        Assert.Equal(px, px2);
    }

    // ────────────────── adjust_black_white ──────────────────
    // 权重全为 1 时源码算得 gray = mn + (md-mn) + (mx-md) = mx，可直接验证。

    [Fact]
    public void BlackWhite_AllWeightsOne_YieldsMaxChannel()
    {
        const int w = 1, h = 1, stride = 4;
        var px = new byte[] { 200, 100, 50, 255 };
        var ones = new float[] { 1, 1, 1, 1, 1, 1 };

        AdjustPixels.BlackWhite(px, w, h, stride, ones, 0, 0, 0);

        Assert.Equal(200, px[0]); // mx = 200/255
        Assert.Equal(200, px[1]);
        Assert.Equal(200, px[2]);
    }

    /// <summary>源码注释明确写着："Pure red at the default 40% comes out 40% gray"。
    /// 这是整个文件里最有价值的一条断言——它验证的是与 Photoshop 对齐的行为。</summary>
    [Fact]
    public void BlackWhite_PureRedAtFortyPercent_GivesFortyPercentGray()
    {
        const int w = 1, h = 1, stride = 4;
        var px = new byte[] { 255, 0, 0, 255 };
        // 顺序：红、黄、绿、青、蓝、品红
        var weights = new float[] { 0.4f, 0.6f, 0.4f, 0.6f, 0.4f, 0.6f };

        AdjustPixels.BlackWhite(px, w, h, stride, weights, 0, 0, 0);

        // gray = mn + (md-mn)*w[secondary] + (mx-md)*w[primary]
        //      = 0   + (0-0)*0.6        + (255-0)*0.4   = 102
        Assert.Equal(102, px[0]);
        Assert.Equal(102, px[1]);
        Assert.Equal(102, px[2]);
    }

    [Fact]
    public void BlackWhite_SkipsTransparentPixels()
    {
        const int w = 1, h = 1, stride = 4;
        var px = new byte[] { 200, 100, 50, 0 };
        var before = (byte[])px.Clone();
        var ones = new float[] { 1, 1, 1, 1, 1, 1 };

        AdjustPixels.BlackWhite(px, w, h, stride, ones, 0, 0, 0);

        Assert.Equal(before, px);
    }

    // ────────────────── adjust_color_balance ──────────────────

    [Fact]
    public void ColorBalance_ZeroOffsets_IsIdentity()
    {
        const int w = 2, h = 1, stride = w * 4;
        var px = new byte[] { 100, 150, 200, 255, 30, 60, 90, 255 };
        var before = (byte[])px.Clone();
        var zero = new float[] { 0, 0, 0 };

        AdjustPixels.ColorBalance(px, w, h, stride, zero, zero, zero, 1);

        Assert.Equal(before, px);
    }

    [Fact]
    public void ColorBalance_PreservesAlphaChannel()
    {
        const int w = 4, h = 4, stride = w * 4;
        var px = new byte[stride * h];
        var alphas = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = 120; px[p + 1] = 80; px[p + 2] = 40;
                px[p + 3] = (byte)(32 + (x * 10 + y * 5) % 200);
                alphas[y * w + x] = px[p + 3];
            }
        var shadows = new float[] { 0.2f, -0.1f, 0.3f };
        var mids = new float[] { 0, 0, 0 };
        var highs = new float[] { -0.2f, 0.1f, 0 };

        AdjustPixels.ColorBalance(px, w, h, stride, shadows, mids, highs, 1);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(alphas[y * w + x], px[y * stride + x * 4 + 3]);
    }

    [Fact]
    public void ColorBalance_KeepsRgbWithinAlpha()
    {
        // WritePremultiplied/等价写回路径保证 RGB ≤ alpha，这是预乘不变量
        const int w = 4, h = 4, stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = 200; px[p + 1] = 200; px[p + 2] = 200;
                px[p + 3] = (byte)(10 + (x + y) * 10);
            }
        var shadows = new float[] { 0.5f, 0.5f, 0.5f };
        var mids = new float[] { 0.5f, 0.5f, 0.5f };
        var highs = new float[] { 0.5f, 0.5f, 0.5f };

        AdjustPixels.ColorBalance(px, w, h, stride, shadows, mids, highs, 0);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                Assert.True(px[p] <= px[p + 3], $"R > A at ({x},{y})");
                Assert.True(px[p + 1] <= px[p + 3], $"G > A at ({x},{y})");
                Assert.True(px[p + 2] <= px[p + 3], $"B > A at ({x},{y})");
            }
    }

    // ────────────────── adjust_camera_raw ──────────────────

    /// <summary>全部参数为 0 时，白平衡增益为 1、曝光 0 档、对比度比例 1，
    /// 四条影调分级在 amount=0 时都是恒等映射，自然饱和度/饱和度在 0 时也是恒等。
    /// 因此输出应与输入相同（仅允许 sRGB↔线性往返带来的 ±1 舍入）。</summary>
    [Fact]
    public void CameraRaw_AllZeroParameters_IsNearIdentity()
    {
        const int w = 4, h = 4, stride = w * 4;
        var px = new byte[stride * h];
        var before = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = (byte)(x * 60); px[p + 1] = (byte)(y * 60); px[p + 2] = 128;
                px[p + 3] = 255;
            }
        px.AsSpan().CopyTo(before);

        AdjustPixels.CameraRaw(px, w, h, stride, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        for (int i = 0; i < px.Length; i++)
            Assert.InRange(Math.Abs(px[i] - before[i]), 0, 1);
    }

    [Fact]
    public void CameraRaw_SkipsTransparentPixels()
    {
        const int w = 1, h = 1, stride = 4;
        var px = new byte[] { 200, 150, 100, 0 };
        var before = (byte[])px.Clone();

        AdjustPixels.CameraRaw(px, w, h, stride, 1.2, 1.0, 0.9, 0.5, 20, 10, -10, 5, -5, 15, 10, 0);

        Assert.Equal(before, px);
    }

    [Fact]
    public void CameraRaw_KeepsRgbWithinAlpha()
    {
        const int w = 4, h = 4, stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = 255; px[p + 1] = 255; px[p + 2] = 255;
                px[p + 3] = (byte)(20 + (x + y) * 12);
            }

        AdjustPixels.CameraRaw(px, w, h, stride, 1.4, 1.0, 0.8, 1.0, 40, 30, -30, 20, -20, 25, 15, 0);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                Assert.True(px[p] <= px[p + 3], $"R > A at ({x},{y})");
                Assert.True(px[p + 1] <= px[p + 3], $"G > A at ({x},{y})");
                Assert.True(px[p + 2] <= px[p + 3], $"B > A at ({x},{y})");
            }
    }

    /// <summary>高光剪切视图：接近 1.0 的通道应被置 1（显示为不透明），低的置 0。</summary>
    [Fact]
    public void CameraRaw_ClippingHighlight_ProducesBinaryOutput()
    {
        const int w = 2, h = 1, stride = w * 4;
        // 第 0 个像素全白（会剪切），第 1 个像素中灰（不会）
        var px = new byte[] { 255, 255, 255, 255, 128, 128, 128, 255 };

        AdjustPixels.CameraRaw(px, w, h, stride, 1, 1, 1, 2.0, 0, 0, 0, 0, 0, 0, 0, 1);

        Assert.Equal(255, px[0]);
        Assert.Equal(255, px[1]);
        Assert.Equal(255, px[2]);
        Assert.True(px[4] < 255 && px[5] < 255 && px[6] < 255, "中灰像素不应被判为剪切");
    }

    // ────────────────── adjust_camera_raw_clip_overlay ──────────────────

    [Fact]
    public void CameraRawClipOverlay_NoFlags_IsNoOp()
    {
        const int w = 2, h = 1, stride = w * 4;
        var px = new byte[] { 10, 20, 30, 255, 200, 210, 220, 255 };
        var before = (byte[])px.Clone();

        AdjustPixels.CameraRawClipOverlay(px, w, h, stride, 0, 0);

        Assert.Equal(before, px); // 源码首行直接 return
    }

    [Fact]
    public void CameraRawClipOverlay_ShadowFlag_TintsDarkPixelsBlue()
    {
        const int w = 1, h = 1, stride = 4;
        // C 的判定是 r <= 0.5/255.0，而 r = p[0]/alpha。alpha=255 时 p[0] 必须真的取 0 才触发；
        // 写成 1 会得到 r = 1/255 ≈ 0.0039 > 0.00196，分支根本不进（这版曾因此误报失败）。
        var px = new byte[] { 0, 0, 0, 255 }; // 被切到 0 的阴影

        AdjustPixels.CameraRawClipOverlay(px, w, h, stride, 1, 0);

        // C: shadows 分支 r*=0.35; g*=0.35; b = b*0.35 + 0.65 → 蓝通道最亮。
        // b = 0.65 → write_premultiplied 取 round(0.65*255) = round(165.75) = 166；r、g 为 0。
        Assert.Equal(0, px[0]);
        Assert.Equal(0, px[1]);
        Assert.Equal(166, px[2]);
        Assert.Equal(255, px[3]); // alpha 从不被改
    }

    [Fact]
    public void CameraRawClipOverlay_HighlightFlag_TintsBrightPixelsRed()
    {
        const int w = 1, h = 1, stride = 4;
        var px = new byte[] { 255, 255, 255, 255 }; // 被切到 255 的高光

        AdjustPixels.CameraRawClipOverlay(px, w, h, stride, 0, 1);

        Assert.True(px[0] > px[1], "高光叠加后红通道应高于绿通道");
        Assert.True(px[0] > px[2], "高光叠加后红通道应高于蓝通道");
    }
}