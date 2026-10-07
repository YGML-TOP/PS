using System.Runtime.CompilerServices;

namespace Compositor.Core.Pixels;

/// <summary>
/// C 语言标量语义的等价实现。直译时凡是不能用 C# 同名写法替换的地方，都必须走这里。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：C# 和 C 在三个地方行为不同，且每一处都会让黄金样本差 1 或直接抛异常。
/// 这三个地方不是"风格问题"，是语义问题，所以集中在一处、逐条注明 C 的原文行为。</para>
///
/// <para><b>1. 舍入方向</b>。C 的 <c>lround</c>/<c>lroundf</c>/<c>round</c>/<c>roundf</c> 一律
/// <b>四舍五入、遇 .5 远离零</b>；C# 的 <see cref="Math.Round(double)"/> 默认是
/// <b>银行家舍入（ToEven）</b>。例如 <c>lround(0.5)=1</c> 而 <c>Math.Round(0.5)=0</c>，
/// <c>lround(1.5)=2</c> 而 <c>Math.Round(1.5)=2</c>，<c>lround(2.5)=3</c> 而 <c>Math.Round(2.5)=2</c>。
/// 不显式指定 <see cref="MidpointRounding.AwayFromZero"/> 就会静默错。</para>
///
/// <para><b>2. 窄化转换</b>。C 的 <c>(uint8_t)</c> 是先转 double 再截断，越界是未定义行为
/// （x86 实测回绕，如 <c>(uint8_t)256 == 0</c>）。C# 的 <c>(byte)double</c> 越界直接抛
/// <see cref="OverflowException"/>。直译源码里凡是能算出越界值的表达式（如双线性插值累加后的
/// <c>lround</c> 结果），原样翻译会在 C# 上崩，所以这里改为<b>饱和</b>。</para>
///
/// <para><b>3. 无符号溢出</b>。C 的 <c>uint32_t</c> 乘法按模 2³² 回绕；C# 的 <c>uint</c> 在默认
/// unchecked 上下文中行为相同，但一旦项目打开了溢出检查就会抛异常。
/// 因此本项目的 <c>Directory.Build.props</c> 显式写死 <c>CheckForOverflowUnderflow=false</c>。</para>
///
/// <para><b>饱和是否改变语义</b>：不会。凡是本项目里出现 <c>(uint8_t)</c> 的地方，
/// 原表达式都已经用 <c>fminf</c>/<c>fmaxf</c>/<c>lround</c> 把值夹在 [0,255] 内，
/// 饱和分支永不命中；它只是把 C 的未定义行为换成一个确定且安全的结果。</para>
/// </remarks>
internal static class CSemantics
{
    /// <summary>C 的 <c>lround(double)</c>：四舍五入，遇 .5 远离零。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LRound(double v) => checked((int)Math.Round(v, MidpointRounding.AwayFromZero));

    /// <summary>C 的 <c>lroundf(float)</c>。先提升为 double 再舍入，与 C 的 double 版本同精度路径。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LRoundF(float v) => checked((int)MathF.Round(v, MidpointRounding.AwayFromZero));

    /// <summary>C 的 <c>round(double)</c>（返回 double 的那个）：四舍五入，遇 .5 远离零。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>C 的 <c>roundf(float)</c>：四舍五入，遇 .5 远离零。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RoundF(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// C 的 <c>(uint8_t)double</c>：向零截断，越界饱和（C 为未定义行为，此处取确定结果）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte U8(double v) => v <= 0.0 ? (byte)0 : v >= 255.0 ? (byte)255 : (byte)v;

    /// <summary>C 的 <c>(uint8_t)float</c>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte U8F(float v) => v <= 0.0f ? (byte)0 : v >= 255.0f ? (byte)255 : (byte)v;

    /// <summary>
    /// C 的 <c>(uint8_t)(x * a + 127u) / 255u</c> 这一族整数预乘运算的等价写法。
    /// </summary>
    /// <remarks>
    /// C 里 <c>uint8_t</c> 会被整型提升为 <c>int</c> 再运算，所以中间值是 <c>int</c>；
    /// C# 里 <c>byte * byte</c> 本来就提升为 <c>int</c>，可直接照抄。
    /// 除法是整数除法（向零截断），C# 的 <c>/</c> 对整数同样是向零截断，一致。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Premul(byte channel, int alpha, int round) => (byte)((channel * alpha + round) / 255);
}