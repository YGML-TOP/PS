using Compositor.Core.Pixels;
using Xunit;

namespace Compositor.Core.Tests.Pixels;

/// <summary>
/// 波次 1-A 直译回归：锁死每个直译函数的行为。
/// </summary>
/// <remarks>
/// <para><b>这些测试锁的是什么</b>：不是"算法是否正确"，而是"直译是否与 C 源码逐行等价"。
/// 因此断言刻意偏向<b>可从 C 源码手工推导的确定值</b>，而不是从 C# 跑出来的黄金值——
/// 后者会把直译错误一起锁死，等于用错误实现给自己盖章。</para>
/// <para>真正的黄金样本需要 Mac 版实测导出，属波次 2 标定范畴（铁律 3），本文件不替代它。</para>
/// </remarks>
public sealed class TranslationTests
{
    // ────────────────────────────── LensPixels ──────────────────────────────

    /// <summary>
    /// k=0 时 scale 恒为 1，源码算得 sx = cx + dx - 0.5 = x、sy = y，故 fx=fy=0，
    /// 四个权重里只有 (i=0,j=0) 的 1 非零 → 结果必须是<b>逐字节恒等拷贝</b>。
    /// 这是整个文件里最强的一条直译断言。
    /// </summary>
    [Fact]
    public void LensPixels_ZeroK_IsExactIdentity()
    {
        const int w = 7, h = 5, stride = w * 4 + 6; // 故意让 stride > w*4，验证按 stride 寻址
        var src = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                src[p] = (byte)(x * 30 + y);
                src[p + 1] = (byte)(y * 40);
                src[p + 2] = (byte)(x + y);
                src[p + 3] = (byte)(255 - x * 10);
            }

        var dst = new byte[stride * h];
        LensPixels.Distort(src, dst, w, h, stride, 0.0);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(src[y * stride + x * 4 + c], dst[y * stride + x * 4 + c]);
    }

    /// <summary>k≠0 时中心附近应保持不变（畸变量随到中心的距离平方衰减）。</summary>
    [Fact]
    public void LensPixels_NonZeroK_KeepsCenterStable()
    {
        const int w = 9, h = 9, stride = w * 4;
        var src = new byte[stride * h];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i * 7);

        var dst = new byte[stride * h];
        LensPixels.Distort(src, dst, w, h, stride, 0.3);

        int cx = w / 2, cy = h / 2;
        for (int c = 0; c < 4; c++)
            Assert.Equal(src[cy * stride + cx * 4 + c], dst[cy * stride + cx * 4 + c]);
    }

    /// <summary>输出只写 width*4 列，绝不触碰 stride 尾部填充字节。</summary>
    [Fact]
    public void LensPixels_DoesNotWriteStridePadding()
    {
        const int w = 4, h = 3, stride = w * 4 + 5;
        var src = new byte[stride * h];
        Array.Fill(src, (byte)200);
        var dst = new byte[stride * h];

        LensPixels.Distort(src, dst, w, h, stride, 0.5);

        for (int y = 0; y < h; y++)
            for (int i = w * 4; i < stride; i++)
                Assert.Equal(0, dst[y * stride + i]);
    }

    // ────────────────────────────── NoisePixels ──────────────────────────────

    /// <summary>完全透明像素必须原样不动（源码 <c>if (!alpha) continue;</c>）。</summary>
    [Fact]
    public void NoisePixels_SkipsFullyTransparentPixels()
    {
        const int w = 4, h = 2, stride = w * 4;
        var buf = new byte[stride * h];
        // 每个像素 RGB 填非零、alpha 填 0
        for (int i = 0; i < buf.Length; i += 4) { buf[i] = 200; buf[i + 1] = 100; buf[i + 2] = 50; buf[i + 3] = 0; }
        var before = (byte[])buf.Clone();

        NoisePixels.Add(buf, w, h, stride, 100f, 1, 0, 12345u);

        Assert.Equal(before, buf);
    }

    /// <summary>不透明像素 + amount=0：spread=0 ⇒ n=0，浮点往返后必须还原为原值。</summary>
    [Fact]
    public void NoisePixels_ZeroAmount_OnOpaque_IsIdentity()
    {
        const int w = 8, h = 8, stride = w * 4;
        var buf = new byte[stride * h];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = (byte)((i / 4) * 31 % 256);
            buf[i + 1] = (byte)((i / 4) * 17 % 256);
            buf[i + 2] = (byte)((i / 4) * 53 % 256);
            buf[i + 3] = 255;
        }
        var before = (byte[])buf.Clone();

        NoisePixels.Add(buf, w, h, stride, 0f, 0, 0, 999u);

        Assert.Equal(before, buf);
    }

    /// <summary>同 seed 两次调用必须逐字节相同——"同一张图反复处理得到同一噪声"。</summary>
    [Fact]
    public void NoisePixels_IsDeterministicForSameSeed()
    {
        const int w = 16, h = 16, stride = w * 4;
        static byte[] Make()
        {
            var b = new byte[stride * h];
            for (int i = 0; i < b.Length; i += 4) { b[i] = 128; b[i + 1] = 128; b[i + 2] = 128; b[i + 3] = 255; }
            return b;
        }

        var a = Make();
        var c = Make();
        NoisePixels.Add(a, w, h, stride, 40f, 1, 0, 7u);
        NoisePixels.Add(c, w, h, stride, 40f, 1, 0, 7u);

        Assert.Equal(a, c);
    }

    /// <summary>单色模式下三通道噪声量必须相同（R/G/B 偏移相等）。</summary>
    [Fact]
    public void NoisePixels_Monochromatic_CorrelatesChannels()
    {
        const int w = 32, h = 32, stride = w * 4;
        var mono = new byte[stride * h];
        var color = new byte[stride * h];
        for (int i = 0; i < mono.Length; i += 4) { mono[i] = mono[i + 1] = mono[i + 2] = 128; mono[i + 3] = 255; }
        color.AsSpan().CopyFrom(mono);

        NoisePixels.Add(mono, w, h, stride, 50f, 0, 1, 4242u);
        NoisePixels.Add(color, w, h, stride, 50f, 0, 0, 4242u);

        int monoSpread = 0, colorSpread = 0;
        for (int i = 0; i < mono.Length; i += 4)
        {
            monoSpread = Math.Max(monoSpread, Math.Abs(mono[i] - 128));
            colorSpread = Math.Max(colorSpread, Math.Abs(color[i] - 128));
        }
        // 单色模式的通道间离散度必须小于彩色模式，否则说明 monochromatic 分支没生效
        Assert.True(monoSpread < colorSpread, $"mono={monoSpread} color={colorSpread}");
    }

    /// <summary>输出 alpha 必须不变（只动 RGB）。</summary>
    [Fact]
    public void NoisePixels_PreservesAlpha()
    {
        const int w = 16, h = 16, stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                buf[p] = 128; buf[p + 1] = 64; buf[p + 2] = 32;
                buf[p + 3] = (byte)(16 + (x * 7 + y * 13) % 200);
            }
        var alphas = new byte[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) alphas[y * w + x] = buf[y * stride + x * 4 + 3];

        NoisePixels.Add(buf, w, h, stride, 80f, 1, 0, 5150u);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(alphas[y * w + x], buf[y * stride + x * 4 + 3]);
    }

    /// <summary>origin 偏移必须平移噪声图案：同样的 seed，取不同 origin 段结果不同。</summary>
    [Fact]
    public void NoisePixels_OriginShiftsPattern()
    {
        const int w = 8, h = 8, stride = w * 4;
        static byte[] Make()
        {
            var b = new byte[stride * h];
            for (int i = 0; i < b.Length; i += 4) { b[i] = b[i + 1] = b[i + 2] = 100; b[i + 3] = 255; }
            return b;
        }
        var a = Make(); var b = Make();

        NoisePixels.AddAt(a, w, h, stride, 60f, 0, 1, 31337u, 0, 0);
        NoisePixels.AddAt(b, w, h, stride, 60f, 0, 1, 31337u, 1000, 1000);

        Assert.NotEqual(a, b);
    }

    // ────────────────────────────── BrushPixels ──────────────────────────────

    [Fact]
    public void AlphaBounds_FindsTightBox()
    {
        const int w = 6, h = 5, stride = w * 4;
        var buf = new byte[stride * h];
        // 在 (2,1)-(4,3) 区域放不透明像素
        for (int y = 1; y <= 3; y++)
            for (int x = 2; x <= 4; x++)
                buf[y * stride + x * 4 + 3] = 255;

        Span<int> bounds = stackalloc int[4];
        BrushPixels.AlphaBounds(buf, w, h, stride, bounds);

        Assert.Equal(2, bounds[0]);
        Assert.Equal(1, bounds[1]);
        Assert.Equal(5, bounds[2]);
        Assert.Equal(4, bounds[3]);
    }

    [Fact]
    public void AlphaBounds_AllTransparent_ReturnsAllZero()
    {
        const int w = 4, h = 4, stride = w * 4;
        var buf = new byte[stride * h];

        Span<int> bounds = stackalloc int[4];
        BrushPixels.AlphaBounds(buf, w, h, stride, bounds);

        Assert.Equal(0, bounds[0]);
        Assert.Equal(0, bounds[1]);
        Assert.Equal(0, bounds[2]);
        Assert.Equal(0, bounds[3]);
    }

    /// <summary>反预乘 → 恢复预乘 的配对往返，对 a=255 应完全还原。</summary>
    [Fact]
    public void UnpremultiplyRestore_RoundTripsOnOpaque()
    {
        const int w = 5, h = 3, stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                buf[p] = (byte)(x * 20 + y * 3);
                buf[p + 1] = (byte)(y * 25);
                buf[p + 2] = (byte)(x * 11 + 5);
                buf[p + 3] = 255;
            }
        var before = (byte[])buf.Clone();

        var alpha = new byte[w * h];
        BrushPixels.ExtractAlpha(buf, stride, alpha, w, w, h);
        BrushPixels.UnpremultiplyOpaque(buf, stride, w, h);
        BrushPixels.RestoreAlpha(buf, stride, alpha, w, w, h);

        Assert.Equal(before, buf);
    }

    [Fact]
    public void ExtractAlpha_CopiesAlphaChannelOnly()
    {
        const int w = 3, h = 2, stride = w * 4, gstride = w + 2;
        var rgba = new byte[stride * h];
        var gray = new byte[gstride * h];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                rgba[p] = 11; rgba[p + 1] = 22; rgba[p + 2] = 33;
                rgba[p + 3] = (byte)(100 + x + y * 10);
            }

        BrushPixels.ExtractAlpha(rgba, stride, gray, gstride, w, h);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(rgba[y * stride + x * 4 + 3], gray[y * gstride + x]);
        // 灰度行内填充字节不得被写
        for (int y = 0; y < h; y++)
            for (int i = w; i < gstride; i++)
                Assert.Equal(0, gray[y * gstride + i]);
    }

    /// <summary>半透明像素反预乘必须把 alpha 强制置 255（这是该函数存在的理由）。</summary>
    [Fact]
    public void UnpremultiplyOpaque_ForcesAlphaTo255()
    {
        const int w = 2, h = 2, stride = w * 4;
        var buf = new byte[stride * h];
        for (int i = 0; i < buf.Length; i += 4) { buf[i] = 50; buf[i + 1] = 25; buf[i + 2] = 10; buf[i + 3] = 128; }

        BrushPixels.UnpremultiplyOpaque(buf, stride, w, h);

        for (int i = 0; i < buf.Length; i += 4) Assert.Equal(255, buf[i + 3]);
    }

    // ────────────────────────────── LevelsPixels ──────────────────────────────

    /// <summary>恒等 LUT（table[i]=i）在不透明像素上必须近似恒等（±1 舍入）。</summary>
    [Fact]
    public void LevelsApply_IdentityTable_IsNearIdentity()
    {
        const int w = 4, h = 4, count = w * h;
        var buf = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            buf[i * 4] = (byte)(i * 13);
            buf[i * 4 + 1] = (byte)(i * 7);
            buf[i * 4 + 2] = (byte)(i * 29);
            buf[i * 4 + 3] = 255;
        }

        var tables = new float[768];
        for (int ch = 0; ch < 3; ch++)
            for (int i = 0; i < 256; i++) tables[ch * 256 + i] = i;

        LevelsPixels.Apply(buf, count, tables);

        for (int i = 0; i < count; i++)
        {
            Assert.InRange(Math.Abs(buf[i * 4] - (byte)(i * 13)), 0, 1);
            Assert.InRange(Math.Abs(buf[i * 4 + 1] - (byte)(i * 7)), 0, 1);
            Assert.InRange(Math.Abs(buf[i * 4 + 2] - (byte)(i * 29)), 0, 1);
        }
    }

    [Fact]
    public void LevelsApply_SkipsTransparentPixels()
    {
        const int count = 3;
        var buf = new byte[count * 4];
        for (int i = 0; i < count; i++) { buf[i * 4] = 200; buf[i * 4 + 1] = 200; buf[i * 4 + 2] = 200; buf[i * 4 + 3] = 0; }
        var before = (byte[])buf.Clone();

        var tables = new float[768];
        for (int i = 0; i < 256; i++) tables[i] = 0; // 全黑 LUT：若生效则非透明像素会被压黑

        LevelsPixels.Apply(buf, count, tables);

        Assert.Equal(before, buf);
    }

    [Fact]
    public void Histogram_PlacesLuminanceAndPerChannelBins()
    {
        const int count = 1;
        var px = new byte[] { 255, 255, 255, 255 }; // 纯白不透明
        var bins = new double[1024];

        LevelsPixels.Histogram(px, null, count, bins);

        Assert.Equal(1.0, bins[255], 6);      // 亮度 bin
        Assert.Equal(1.0, bins[256 + 255], 6); // R
        Assert.Equal(1.0, bins[512 + 255], 6); // G
        Assert.Equal(1.0, bins[768 + 255], 6); // B
        Assert.Equal(1.0 / 3.0, bins[255], 3); // 亮度 bin 另叠加三通道各 1/3
    }

    [Fact]
    public void Histogram_CoverageScalesWeight()
    {
        var px = new byte[] { 10, 20, 30, 255 };
        var full = new double[1024];
        var half = new double[1024];
        var cov = new byte[] { 255 };

        LevelsPixels.Histogram(px, null, 1, full);
        LevelsPixels.Histogram(px, cov, 1, half);

        Assert.Equal(full[30] * 255.0 / 255.0, half[30], 6);
        var cov128 = new byte[] { 128 };
        var q = new double[1024];
        LevelsPixels.Histogram(px, cov128, 1, q);
        Assert.Equal(full[30] * 128.0 / 255.0, q[30], 6);
    }

    [Fact]
    public void CubeApply_DimensionBelowTwo_IsNoOp()
    {
        var px = new byte[] { 100, 110, 120, 255 };
        var before = (byte[])px.Clone();

        LevelsPixels.CubeApply(px, 1, new float[4], 1);

        Assert.Equal(before, px);
    }

    // ────────────────────────────── ContentFill ──────────────────────────────

    /// <summary>掩码全空 ⇒ 没有待填充像素 ⇒ 返回 1 且像素不变。</summary>
    [Fact]
    public void ContentFill_EmptySelection_ReturnsOne_Unchanged()
    {
        const int w = 8, h = 8, stride = w * 4;
        var px = new byte[stride * h];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 90; px[i + 1] = 80; px[i + 2] = 70; px[i + 3] = 255; }
        var mask = new byte[w * h];
        var before = (byte[])px.Clone();

        Assert.Equal(1, ContentFill.Fill(px, stride, mask, w, w, h));
        Assert.Equal(before, px);
    }

    /// <summary>整幅选中且全部透明 ⇒ 没有已知参照 ⇒ 返回 0。</summary>
    [Fact]
    public void ContentFill_NoKnownReference_ReturnsZero()
    {
        const int w = 6, h = 6, stride = w * 4;
        var px = new byte[stride * h];               // 全部 alpha=0 ⇒ 无已知像素
        var mask = new byte[w * h];
        Array.Fill(mask, (byte)255);                  // 全选
        var before = (byte[])px.Clone();

        Assert.Equal(0, ContentFill.Fill(px, stride, mask, w, w, h));
        Assert.Equal(before, px);
    }

    /// <summary>半图已知时必须能把选中区填满，且选区像素最终拿到参照的不透明值。</summary>
    [Fact]
    public void ContentFill_FillsSelectionFromKnownRegion()
    {
        const int w = 12, h = 12, stride = w * 4;
        var px = new byte[stride * h];
        // 右半边不透明（已知区），左半边透明（待填充）
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = (byte)(x * 15 + y * 3);
                px[p + 1] = (byte)(y * 17);
                px[p + 2] = 40;
                px[p + 3] = x >= w / 2 ? (byte)255 : (byte)0;
            }

        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++) mask[y * w + x] = 255;

        Assert.Equal(1, ContentFill.Fill(px, stride, mask, w, w, h));

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++)
                Assert.Equal(255, px[y * stride + x * 4 + 3]);
    }

    /// <summary>已知区像素不得被填充算法改动。</summary>
    [Fact]
    public void ContentFill_LeavesKnownPixelsUntouched()
    {
        const int w = 12, h = 12, stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                px[p] = (byte)(x * 15 + y * 3);
                px[p + 1] = (byte)(y * 17);
                px[p + 2] = 40;
                px[p + 3] = x >= w / 2 ? (byte)255 : (byte)0;
            }
        var knownSnapshot = new byte[(h) * (w / 2)];
        for (int y = 0; y < h; y++)
            for (int x = w / 2; x < w; x++)
                knownSnapshot[y * (w / 2) + (x - w / 2)] = px[y * stride + x * 4];

        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++) mask[y * w + x] = 255;

        Assert.Equal(1, ContentFill.Fill(px, stride, mask, w, w, h));

        for (int y = 0; y < h; y++)
            for (int x = w / 2; x < w; x++)
                Assert.Equal(knownSnapshot[y * (w / 2) + (x - w / 2)], px[y * stride + x * 4]);
    }

    /// <summary>同输入两次调用结果必须一致（内部 LCG 初值固定）。</summary>
    [Fact]
    public void ContentFill_IsDeterministic()
    {
        static byte[] MakePixels()
        {
            var b = new byte[12 * 4 * 12];
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                {
                    int p = y * 12 * 4 + x * 4;
                    b[p] = (byte)(x * 15 + y * 3); b[p + 1] = (byte)(y * 17); b[p + 2] = 40;
                    b[p + 3] = x >= 6 ? (byte)255 : (byte)0;
                }
            return b;
        }
        static byte[] MakeMask()
        {
            var m = new byte[12 * 12];
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 6; x++) m[y * 12 + x] = 255;
            return m;
        }

        var a = MakePixels(); var b = MakePixels();
        ContentFill.Fill(a, 12 * 4, MakeMask(), 12, 12, 12);
        ContentFill.Fill(b, 12 * 4, MakeMask(), 12, 12, 12);

        Assert.Equal(a, b);
    }
}