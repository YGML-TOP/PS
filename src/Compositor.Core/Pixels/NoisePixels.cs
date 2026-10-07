namespace Compositor.Core.Pixels;

/// <summary>加噪（均匀/高斯，单色/彩色）。直译自 <c>Rendering/NoisePixels.c</c>（49 行，逐行对照）。</summary>
/// <remarks>
/// <para><b>对应 C 函数</b>：<c>noise_add</c> / <c>noise_add_at</c></para>
/// <para><b>不变量</b>：输入为预乘 RGBA8；输出同样预乘。完全透明像素（alpha=0）原样跳过，
/// 不参与噪声——与原 C 的 <c>if (!alpha) continue;</c> 一致。</para>
/// <para><b>可复现性</b>：噪声只取决于 (originX+x, originY+y, seed, 通道号)，
/// 与缓冲区大小、stride、已处理像素无关。同一 seed 的两次调用逐字节相同，
/// 同一张图的同一块区域在任何时候处理都得到相同噪声。</para>
/// <para><b>与原 C 的等价改写</b>：<c>uint32_t*</c> 状态改 <c>ref uint</c>；
/// 常量 <c>6.2831853f</c> 与 <c>1.0f / 16777216.0f</c> <b>按原样保留</b>——
/// 它们是截断过的字面量，换成 <c>MathF.Tau</c> 或 <c>1f/16777216f</c> 的等价但不同表示会让黄金样本差 1。</para>
/// </remarks>
public static class NoisePixels
{
    /// <summary>加噪，原点固定在 (0,0)。等价于 C 的 <c>noise_add</c>。</summary>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数，须 ≥ <paramref name="width"/> * 4。</param>
    /// <param name="amount">噪声强度 0–100。</param>
    /// <param name="gaussian">非 0 走 Box–Muller 正态分布，0 走均匀分布。</param>
    /// <param name="monochromatic">非 0 表示单色噪声（三通道取同一随机值）。</param>
    /// <param name="seed">随机种子，决定噪声图案。</param>
    public static void Add(
        Span<byte> rgba, int width, int height, int stride,
        float amount, int gaussian, int monochromatic, uint seed)
        => AddAt(rgba, width, height, stride, amount, gaussian, monochromatic, seed, 0, 0);

    /// <summary>加噪，像素 (0,0) 对应文档坐标 (<paramref name="originX"/>, <paramref name="originY"/>)。</summary>
    /// <param name="rgba">预乘 RGBA8 像素，原地修改。</param>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    /// <param name="stride">每行字节数，须 ≥ <paramref name="width"/> * 4。</param>
    /// <param name="amount">噪声强度 0–100。</param>
    /// <param name="gaussian">非 0 走 Box–Muller 正态分布，0 走均匀分布。</param>
    /// <param name="monochromatic">非 0 表示单色噪声（三通道取同一随机值）。</param>
    /// <param name="seed">随机种子，决定噪声图案。</param>
    /// <param name="originX">文档坐标原点的 X 分量。</param>
    /// <param name="originY">文档坐标原点的 Y 分量。</param>
    public static void AddAt(
        Span<byte> rgba, int width, int height, int stride,
        float amount, int gaussian, int monochromatic, uint seed,
        long originX, long originY)
    {
        float spread = amount / 100.0f * 127.5f;

        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                uint alpha = rgba[p + 3];
                if (alpha == 0) continue;

                uint px = unchecked((uint)(originX + x));
                uint py = unchecked((uint)(originY + y));
                uint bse = NoiseHash(seed ^ NoiseHash(unchecked(px * 0x9e3779b9u) ^ NoiseHash(unchecked(py * 0x85ebca6bu))));

                for (int c = 0; c < 3; ++c)
                {
                    uint key = monochromatic != 0 ? bse : bse + unchecked((uint)c * 0x9e3779b9u);
                    float n;
                    if (gaussian != 0)
                    {
                        // Box–Muller: two uniform values make one normally distributed one.
                        float u1 = NoiseUnit(key), u2 = NoiseUnit(key ^ 0x68e31da4u);
                        n = MathF.Sqrt(-2.0f * MathF.Log(1.0f - u1)) * MathF.Cos(6.2831853f * u2) * spread * (2.0f / 3.0f);
                    }
                    else
                    {
                        n = (NoiseUnit(key) * 2.0f - 1.0f) * spread;
                    }

                    float value = rgba[p + c] * 255.0f / alpha + n;
                    value = value < 0 ? 0 : value > 255 ? 255 : value;
                    rgba[p + c] = CSemantics.U8F(value * alpha / 255.0f);
                }
            }
        }
    }

    /// <summary>A well-mixed 32-bit hash, so neighbouring pixels get unrelated values.</summary>
    private static uint NoiseHash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>
    /// Uniform in [0, 1)。
    /// </summary>
    /// <remarks>
    /// 括号位置必须与 C 原文一致：<c>(float)(noise_hash(key) &gt;&gt; 8) * (1.0f / 16777216.0f)</c>。
    /// C# 中 <c>*</c> 优先级高于 <c>&gt;&gt;</c>，漏掉括号会解析成"按小数移位"而编译失败。
    /// </remarks>
    private static float NoiseUnit(uint key) => (NoiseHash(key) >> 8) * (1.0f / 16777216.0f);
}