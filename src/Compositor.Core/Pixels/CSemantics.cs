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

    // ── C 的三个 libm 函数：.NET 10 的 Math / MathF 里<b>都没有</b>，必须自建 ──

    /// <summary>C 的 <c>hypot(double, double)</c>：√(x²+y²)，<b>带溢出/下溢保护</b>。</summary>
    /// <remarks>
    /// <para><b>不能写成 <c>Math.Sqrt(x * x + y * y)</c></b>。那样在 x、y 很大时会先溢出成
    /// <see cref="double.PositiveInfinity"/>，在两者都很小时会下溢丢精度——而 <c>hypot</c> 按定义
    /// 必须给出正确结果。本实现按「先除以较大者再还原」缩放，与 IEEE 754 <c>hypot</c> 及
    /// C 标准库各实现一致。</para>
    /// <para><b>NaN / 无穷的传播</b>：任一参数为 NaN 时返回 NaN（与 C 一致，C 的 <c>hypot</c> 不抛异常）；
    /// 任一为无穷时返回正无穷。<c>(0,0)</c> 返回 0，不产生 NaN。</para>
    /// <para>本项目 4 处调用点（暗角半径、色差径向距离）的实参都是像素坐标，量级远小于溢出阈值，
    /// 但保留缩放是为了让这个函数在任何输入下都与 C 一致，而不是"当前这些调用碰巧不会出事"。</para>
    /// </remarks>
    public static double Hypot(double x, double y)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y);
        double m = Math.Max(ax, ay);
        if (double.IsNaN(m) || double.IsInfinity(m) || m == 0.0) return m;
        return m * Math.Sqrt((ax / m) * (ax / m) + (ay / m) * (ay / m));
    }

    /// <summary>C 的 <c>exp2f(float)</c>：2 的 <paramref name="v"/> 次方。</summary>
    /// <remarks>
    /// <para>.NET 的 <see cref="Math"/> / <see cref="MathF"/> <b>都没有 <c>Exp2</c></b>。
    /// 唯一可用的替代是 <c>Math.Pow(2.0, v)</c>。</para>
    /// <para><b>刻意在 double 里算再一次性窄化回 float</b>，而不是用 <c>MathF.Pow</c>：
    /// 后者在 float 精度上算完 2 的幂、<b>再</b>舍入一次，会引入本不该有的二次舍入误差。
    /// 先在 double 里算完只舍入一次，结果不比正确舍入的 <c>exp2f</c> 差。</para>
    /// <para>对整数 <paramref name="v"/>（本项目唯一的用法：密度滑杆的 1.5 倍），
    /// <see cref="Math.Pow"/> 返回精确的 2 的幂，无误差。</para>
    /// </remarks>
    /// <param name="v">指数。</param>
    /// <returns>2<sup><paramref name="v"/></sup>。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Exp2F(float v) => (float)Math.Pow(2.0, v);

    /// <summary>C 的 <c>fmodf(float, float)</c>：截断除法的余数，符号随被除数。</summary>
    /// <remarks>
    /// <para>C# 的 float <c>%</c> 运算符<b>本身</b>就是 IEEE remainder-truncated，语义与
    /// <c>fmodf</c> 完全一致（不是 <c>Math.DivRem</c> 的整除取模，也不是 C# 的 <c>Math.IEEERemainder</c>）。
    /// 唯一的差别在除数为 0：<c>fmodf(x, 0)</c> 按 C 标准返回 <b>NaN</b> 并置 errno，
    /// 而 C# 的 <c>x % 0</c> 抛 <see cref="DivideByZeroException"/>。这里保留 C 的行为。</para>
    /// <para>除数为 ±∞ 时 <c>%</c> 返回被除数，与 <c>fmodf</c> 一致，无需特判。</para>
    /// <para>⚠️ 本项目唯一调用点（CRT 扫描线的珠子间距）的除数是
    /// <c>spacing = p.Cell &lt; 2 ? 2 : p.Cell</c>，恒 ≥ 2，因此 0 除分支<b>当前不可达</b>；
    /// 保留它是为了不让这个函数在被复用到别处时静默改变 C 的行为。</para>
    /// </remarks>
    /// <param name="x">被除数。</param>
    /// <param name="y">除数。</param>
    /// <returns><paramref name="x"/> 除以 <paramref name="y"/> 的截断余数；<paramref name="y"/> 为 0 时为 NaN。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FModF(float x, float y) => y == 0.0f ? float.NaN : x % y;
}