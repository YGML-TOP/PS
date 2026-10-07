namespace Compositor.Core.Pixels;

/// <summary>
/// 色阶查找表、直方图统计与三维色彩立方体查表。
/// 直译自 <c>Rendering/LevelsPixels.c</c>（60 行，逐行对照）。
/// </summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>levels_apply</c> / <c>levels_histogram</c> / <c>cube_apply</c></para>
/// <para><b>共同不变量</b>：输入输出均为预乘 RGBA8，alpha=0 的像素一律跳过不动。
/// 需要在直通域计算的场合，三个函数都先反预乘、算完再按 alpha 乘回——这是铁律 2 在实现层的落点。</para>
/// </remarks>
public static class LevelsPixels
{
    /// <summary>
    /// 用每通道 256 项查找表调整色阶。直译自 <c>levels_apply</c>。
    /// </summary>
    /// <remarks>
    /// <para>表间做线性插值（<c>table[lo] + (table[hi]-table[lo])*(x-lo)</c>），
    /// 因此 LUT 不必是单调或 0→255 端点对齐的。</para>
    /// <para>🔴 <b>LUT 的量纲是 0…1，不是 0…255。</b> 原 C 写的是
    /// <c>fminf(alpha, roundf(result*alpha))</c>，只有 <c>result</c> 落在 0…1 时该式才成立
    /// （否则乘完 alpha 必然远超 255 而被 fminf 夹成 255，整片饱和）。
    /// 三个 Swift 调用方都据此构造：<c>Levels.swift:69</c> 的 <c>Float(apply(Double($0)/255, channel:))</c>、
    /// <c>Curves.swift:37</c> 的 <c>value(...)/255</c>、<c>ImageAdjustments.swift:61</c> 的
    /// <c>min(1, max(0, output))</c>。<b>传入 0…255 的表不是"效果更强"，是"全屏过曝"。</b></para>
    /// </remarks>
    /// <param name="pixels">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="count">像素个数。</param>
    /// <param name="tables">查找表，长度须 ≥ 3*256，按 R/G/B 分三段。</param>
    public static void Apply(Span<byte> pixels, int count, ReadOnlySpan<float> tables)
    {
        for (int i = 0; i < count; ++i)
        {
            int p = i * 4;
            float alpha = pixels[p + 3];
            if (alpha == 0) continue;

            for (int channel = 0; channel < 3; ++channel)
            {
                float x = MathF.Min(255f, pixels[p + channel] * 255.0f / alpha);
                int lo = (int)x, hi = lo < 255 ? lo + 1 : 255;
                int table = channel * 256;
                float result = tables[table + lo] + (tables[table + hi] - tables[table + lo]) * (x - lo);
                pixels[p + channel] = (byte)MathF.Min(alpha, MathF.Max(0f, CSemantics.RoundF(result * alpha)));
            }
        }
    }

    /// <summary>
    /// 累加亮度直方图（1024 个 bin：0–255 为亮度，256–511/512–767/768–1023 为 R/G/B）。
    /// 直译自 <c>levels_histogram</c>。
    /// </summary>
    /// <param name="pixels">预乘 RGBA8 像素，只读。</param>
    /// <param name="coverage">
    /// 可选的 8-bit 覆盖度（选区/蒙版）。传 <c>default</c>（长度 0）时所有不透明像素等权计入。
    /// </param>
    /// <param name="count">像素个数。</param>
    /// <param name="bins">输出，长度须 ≥ 1024 的 double。<b>调用前需自行清零</b>（原 C 同样是累加语义）。</param>
    /// <remarks>
    /// 🔴 <b>等价改写</b>：原 C 的形参是可为 NULL 的 <c>const uint8_t *coverage</c>，
    /// 但 <c>ReadOnlySpan&lt;byte&gt;?</c> <b>编译不过</b>——ref struct 不能作为泛型类型实参（CS9244）。
    /// 故按 C# 惯例改用「空 span 即 null」：<c>coverage.IsEmpty</c> 为真时走无覆盖度分支。
    /// 数值结果与原 C 的 <c>coverage == NULL</c> 分支逐位一致。
    /// </remarks>
    public static void Histogram(
        ReadOnlySpan<byte> pixels,
        ReadOnlySpan<byte> coverage,
        int count,
        Span<double> bins)
    {
        for (int i = 0; i < count; ++i)
        {
            int p = i * 4;
            if (pixels[p + 3] == 0) continue;

            double weight = pixels[p + 3] / 255.0 * (coverage.IsEmpty ? 1 : coverage[i] / 255.0);
            for (int channel = 0; channel < 3; ++channel)
            {
                int value = (int)Math.Min(255.0, CSemantics.Round(pixels[p + channel] * 255.0 / pixels[p + 3]));
                bins[(channel + 1) * 256 + value] += weight;
                bins[value] += weight / 3.0;
            }
        }
    }

    /// <summary>
    /// 三维色彩立方体（.cube）查表，对反预乘色做八点三线性插值，alpha 保持不变。
    /// 直译自 <c>cube_apply</c>。
    /// </summary>
    /// <param name="pixels">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="count">像素个数。</param>
    /// <param name="cube">立方体数据，长度须 ≥ dimension³ * 4 的 float，红通道变化最快。</param>
    /// <param name="dimension">立方体单边格数，须 ≥ 2。</param>
    public static void CubeApply(Span<byte> pixels, int count, ReadOnlySpan<float> cube, int dimension)
    {
        // 原 C 在 dimension < 2 时会算出 lo[channel] = -1 并越界读取（未定义行为）。
        // 这是退化输入（1×1×1 立方体没有任何插值意义），此处提前返回并在「已知问题」登记。
        if (dimension < 2) return;

        float scale = (dimension - 1) / 255.0f;
        int dy = dimension, dz = dimension * dimension;

        // 三个工作数组只在像素循环外分配一次。放在循环体内会让每个像素都做一次
        // stackalloc，对大图是明显的开销。语义与原 C 的局部数组完全一致。
        Span<float> position = stackalloc float[3];
        Span<float> fraction = stackalloc float[3];
        Span<int> lo = stackalloc int[3];

        for (int i = 0; i < count; ++i)
        {
            int p = i * 4;
            float alpha = pixels[p + 3];
            if (alpha == 0) continue;

            for (int channel = 0; channel < 3; ++channel)
            {
                position[channel] = MathF.Min(255f, pixels[p + channel] * 255.0f / alpha) * scale;
                lo[channel] = (int)position[channel];
                if (lo[channel] > dimension - 2) lo[channel] = dimension - 2;
                fraction[channel] = position[channel] - lo[channel];
            }

            // C 源码此处变量名为 base。C# 里 base 是关键字（继承访问），不能作标识符，故改名。
            int baseIndex = (lo[0] + lo[1] * dy + lo[2] * dz) * 4;
            int sx = 4, sy = dy * 4, sz = dz * 4;

            for (int channel = 0; channel < 3; ++channel)
            {
                int c = baseIndex + channel;
                float x00 = cube[c] + (cube[c + sx] - cube[c]) * fraction[0];
                float x10 = cube[c + sy] + (cube[c + sy + sx] - cube[c + sy]) * fraction[0];
                float x01 = cube[c + sz] + (cube[c + sz + sx] - cube[c + sz]) * fraction[0];
                float x11 = cube[c + sz + sy] + (cube[c + sz + sy + sx] - cube[c + sz + sy]) * fraction[0];
                float y0 = x00 + (x10 - x00) * fraction[1];
                float y1 = x01 + (x11 - x01) * fraction[1];
                float result = y0 + (y1 - y0) * fraction[2];
                pixels[p + channel] = (byte)MathF.Min(alpha, MathF.Max(0f, CSemantics.RoundF(result * alpha)));
            }
        }
    }
}