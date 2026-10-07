using System.Runtime.CompilerServices;

namespace Compositor.Imaging;

/// <summary>
/// 直通 alpha（straight）与预乘 alpha（premultiplied）之间的换算。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一个类型：</b>全项目 alpha 全程预乘是硬约束，但换算只在边界发生
/// （PNG/PSD 读入时预乘，导出时反预乘）。把它钉在一个地方，是为了让「有没有哪里漏了换算」
/// 变成一个可以 grep、可以单测的问题，而不是散落在几十个调用点上的隐性约定。
/// </para>
/// <para>
/// <b>⚠️ 反预乘的舍入无法声称与 Mac 一致。</b>Mac 侧的预乘是源码写死的
/// （<c>PSDChannelCoder.swift:63-65</c>，<c>(c * a + 127) / 255</c>），可以逐值复现；
/// 但反预乘在 Mac 上由 CoreGraphics 在硬件里完成，<b>Swift 源码里没有对应定义</b>。
/// 下面 <see cref="Unpremultiply"/> 用的是四舍五入到最近的整数除法，是数学上正确的取法，
/// 但它是否与 CoreGraphics 的实际输出一致，属于「原理上无法从源码验证」的一类，
/// 不得在任何交付报告里写成「已与 Mac 版逐像素一致」。
/// </para>
/// </remarks>
public static class PixelAlpha
{
    /// <summary>
    /// 直通 alpha 转预乘 alpha，原地修改 <paramref name="rgba"/>。
    /// </summary>
    /// <param name="rgba">长度必须是 4 的倍数，按 R,G,B,A 排列，每通道 1 字节。</param>
    /// <remarks>
    /// 舍入公式逐字照抄 <c>PSDChannelCoder.swift:63-65</c> 的 <c>(c * a + 127) / 255</c>：
    /// 加 127 是为了在整除前完成四舍五入，避免向下取整带来的整体偏暗。
    /// A 通道本身不乘。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> 长度不是 4 的倍数。</exception>
    public static void Premultiply(Span<byte> rgba)
    {
        ValidateRgba(rgba);
        PremultiplyCore(rgba);
    }

    /// <summary>
    /// 预乘 alpha 转直通 alpha，原地修改 <paramref name="rgba"/>。
    /// </summary>
    /// <param name="rgba">长度必须是 4 的倍数，按 R,G,B,A 排列，每通道 1 字节。</param>
    /// <remarks>
    /// <c>a == 0</c> 时颜色不可恢复，一律写 0（这也是 CoreGraphics 的约定）。
    /// 结果做 255 饱和钳位：<c>a</c> 很小时 <c>c * 255 / a</c> 会溢出。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> 长度不是 4 的倍数。</exception>
    public static void Unpremultiply(Span<byte> rgba)
    {
        ValidateRgba(rgba);
        UnpremultiplyCore(rgba);
    }

    /// <summary>
    /// 单像素预乘，测试与边界调用用。
    /// </summary>
    /// <param name="red">红。</param>
    /// <param name="green">绿。</param>
    /// <param name="blue">蓝。</param>
    /// <param name="alpha">alpha。</param>
    /// <returns>预乘后的 R,G,B 三通道。</returns>
    public static (byte R, byte G, byte B) PremultiplyPixel(byte red, byte green, byte blue, byte alpha)
    {
        var rgba = new byte[4] { red, green, blue, alpha };
        PremultiplyCore(rgba);
        return (rgba[0], rgba[1], rgba[2]);
    }

    /// <summary>
    /// 单像素反预乘，测试与边界调用用。
    /// </summary>
    /// <param name="red">红（预乘态）。</param>
    /// <param name="green">绿（预乘态）。</param>
    /// <param name="blue">蓝（预乘态）。</param>
    /// <param name="alpha">alpha。</param>
    /// <returns>直通态的 R,G,B 三通道。</returns>
    public static (byte R, byte G, byte B) UnpremultiplyPixel(byte red, byte green, byte blue, byte alpha)
    {
        var rgba = new byte[4] { red, green, blue, alpha };
        UnpremultiplyCore(rgba);
        return (rgba[0], rgba[1], rgba[2]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte PremultiplyChannel(byte channel, byte alpha) =>
        (byte)((((uint)channel * alpha) + 127u) / 255u);

    private static void PremultiplyCore(Span<byte> rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var alpha = rgba[i + 3];
            rgba[i] = PremultiplyChannel(rgba[i], alpha);
            rgba[i + 1] = PremultiplyChannel(rgba[i + 1], alpha);
            rgba[i + 2] = PremultiplyChannel(rgba[i + 2], alpha);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte UnpremultiplyChannel(byte channel, byte alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        // 加 alpha/2 完成四舍五入；结果可能超 255，必须钳位（alpha 小、channel 大时会）。
        var value = ((uint)channel * 255u + (alpha / 2u)) / alpha;
        return value > 255u ? (byte)255 : (byte)value;
    }

    private static void UnpremultiplyCore(Span<byte> rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var alpha = rgba[i + 3];
            rgba[i] = UnpremultiplyChannel(rgba[i], alpha);
            rgba[i + 1] = UnpremultiplyChannel(rgba[i + 1], alpha);
            rgba[i + 2] = UnpremultiplyChannel(rgba[i + 2], alpha);
        }
    }

    private static void ValidateRgba(Span<byte> rgba)
    {
        if (rgba.Length % 4 != 0)
        {
            throw new ArgumentException(
                $"RGBA byte buffer length must be a multiple of 4, but was {rgba.Length}.",
                nameof(rgba));
        }
    }
}