using Compositor.Core;

namespace Compositor.App.Canvas;

/// <summary>
/// 生成一张<b>确定性</b>的测试图像，用来证明「PixelBuffer → 屏幕」这条渲染通路是通的。
/// </summary>
/// <remarks>
/// <para>合成器主干（图层栈遍历 → 画布）尚未实现，所以此刻没有任何现成的
/// 「渲染整个文档」API 可用。本类型临时充当那个角色：合成器落地后，
/// 它会被真正的合成结果替换，<b>但可以整体删掉而不影响其余代码</b>。</para>
/// <para>图像内容刻意做成肉眼可判读的三块，用来肉眼验证渲染是否正确：</para>
/// <list type="number">
/// <item><b>上半区：二维线性渐变</b>，R 随 x 升、G 随 y 升、B 恒 128，全不透明。
/// 渐变能一眼看出有没有被上下颠倒（翻转 Y 的话渐变会反过来）。</item>
/// <item><b>左下半区：棋盘格</b>，偶数格<b>完全透明</b>、奇数格<b>完全不透明</b>。
/// 这样"透明"和"不透明"在屏幕上可以被明确区分，不会糊成一片灰。</item>
/// <item><b>右下半区：色板</b>，4 个实色加 1 个 50% 半透明块，用来检查通道顺序
/// （RGBA 写反成 BGRA 时，这一排的颜色会明显错位）。</item>
/// </list>
/// <para>🔴 颜色一律以<b>直通 RGBA8</b> 写进临时数组，再交给
/// <see cref="PixelBuffer.FromStraightRgba"/> 统一预乘。
/// 直接往 <see cref="PixelBuffer.Raw"/> 里写直通色会让半透明像素偏暗。</para>
/// </remarks>
public static class TestPattern
{
    /// <summary>默认图像宽度，单位为文档像素。</summary>
    public const int DefaultWidth = 512;

    /// <summary>默认图像高度，单位为文档像素。</summary>
    public const int DefaultHeight = 512;

    /// <summary>棋盘格边长，单位为文档像素。</summary>
    public const int CheckerCell = 16;

    private const int CheckerSize = 256;

    /// <summary>色板里实色块的数量（另加一个半透明块）。</summary>
    public const int SwatchCount = 4;

    private static readonly byte[] SwatchColors =
    {
        255, 0, 0,
        0, 255, 0,
        0, 0, 255,
        255, 255, 0,
    };

    /// <summary>按 <paramref name="width"/> × <paramref name="height"/> 生成测试图像。</summary>
    /// <param name="width">宽度，必须为正。</param>
    /// <param name="height">高度，必须为正。</param>
    /// <returns>预乘 RGBA8 的 <see cref="PixelBuffer"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">宽或高不为正。</exception>
    public static PixelBuffer Create(int width, int height)
    {
        // 与 PixelBuffer.Create 同一套校验，先做再分配：
        // width * height * 4 用 int 算会在 30000×30000 这种合法尺寸上溢出成负数。
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                width <= 0 ? nameof(width) : nameof(height), "宽高必须为正。");
        }

        long needed = (long)width * 4 * height;
        if (needed > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"尺寸过大：{width}×{height} 需要 {needed} 字节。");
        }

        var straight = new byte[(int)needed];

        // 渐变区的分界：上半。+1 保证退化尺寸（height == 1）时也不会越界。
        int halfH = (height / 2) + 1;
        int halfW = (width / 2) + 1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;

                if (y < halfH)
                {
                    // 区域一：二维线性渐变（全不透明）。
                    byte r = Ramp(x, width);
                    byte g = Ramp(y, halfH);
                    straight[i] = r;
                    straight[i + 1] = g;
                    straight[i + 2] = 128;
                    straight[i + 3] = 255;
                    continue;
                }

                if (x < halfW)
                {
                    // 区域二：棋盘格。偶数格完全透明，奇数格完全不透明。
                    bool opaque = (((x / CheckerCell) + (y / CheckerCell)) & 1) == 1;
                    straight[i] = opaque ? (byte)220 : (byte)0;
                    straight[i + 1] = opaque ? (byte)90 : (byte)0;
                    straight[i + 2] = opaque ? (byte)30 : (byte)0;
                    straight[i + 3] = opaque ? (byte)255 : (byte)0;
                    continue;
                }

                // 区域三：色板。左边 4 个实色块，最右边 1 个 50% 半透明块。
                // 宽度按"剩余区域实际有多宽"来分，不要写死格子宽——
                // 否则窄文档下 (x - halfW) / 固定格子宽 恒为 0，四个色块会塌成同一个红色。
                int swatchWidth = Math.Max(1, width - halfW);
                int band = ((x - halfW) * (SwatchCount + 1)) / swatchWidth;

                if (band >= SwatchCount)
                {
                    straight[i] = 255;
                    straight[i + 1] = 255;
                    straight[i + 2] = 255;
                    straight[i + 3] = 128;
                }
                else
                {
                    straight[i] = SwatchColors[band * 3];
                    straight[i + 1] = SwatchColors[(band * 3) + 1];
                    straight[i + 2] = SwatchColors[(band * 3) + 2];
                    straight[i + 3] = 255;
                }
            }
        }

        return PixelBuffer.FromStraightRgba(straight, width, height);
    }

    /// <summary>生成默认尺寸（<see cref="DefaultWidth"/> × <see cref="DefaultHeight"/>）的测试图像。</summary>
    /// <returns>预乘 RGBA8 的 <see cref="PixelBuffer"/>。</returns>
    public static PixelBuffer CreateDefault() => Create(DefaultWidth, DefaultHeight);

    /// <summary>
    /// 把坐标线性映射到 0–255。分母用 <c>extent - 1</c>，让两端分别正好落在 0 与 255。
    /// </summary>
    private static byte Ramp(int index, int extent)
    {
        int denom = extent - 1;
        if (denom <= 0)
        {
            return 0;
        }

        return (byte)((index * 255) / denom);
    }
}