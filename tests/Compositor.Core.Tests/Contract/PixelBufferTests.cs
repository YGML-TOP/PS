using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests.Contract;

/// <summary>
/// 🔴 <see cref="PixelBuffer"/> 的预乘不变量测试（铁律 2）。
/// </summary>
/// <remarks>
/// <para><b>为什么这个文件是全项目最要紧的回归锁之一：</b>本缓冲是唯一对外的像素载体，
/// 而"预乘"这件事<b>编译期无感</b>。一个模块存预乘、另一个模块存直通，
/// 两边各自单测全绿，合并后只有半透明边缘发黑发白这一种表现。</para>
///
/// <para><b>期望值的来源：全部手算。</b>本文件里每个期望值都是按
/// <c>Premultiply = (c*a + 127) / 255</c>、<c>Unpremultiply = (c*255 + a/2) / a</c>
/// 两条公式<b>在注释里写出算式</b>得到的，没有一个是"先跑一遍实现再抄输出"。
/// 从实现抄期望值会把直译错误一起锁死，等于用错误实现给自己盖章。</para>
///
/// <para><b>关键事实：往返不是逐位无损的。</b>byte→byte 的预乘/反预乘是一对量化映射，
/// 量化误差会被反预乘放大约 <c>255/alpha</c> 倍，所以本文件对 alpha=255 要求<b>严格相等</b>
/// （那条路径是恒等映射），对 alpha=128 给出<b>手算的精确往返结果</b>（多数恰好回到原值，
/// 少数差 1），并且明确不要求全 256 个输入都精确往返。</para>
/// </remarks>
public sealed class PixelBufferTests
{
    // ─────────────────── 创建与布局 ───────────────────

    /// <summary>
    /// <c>Create</c> 出来的字节必须全是 0。这看着像废话，实际是<b>预乘语义的定义</b>：
    /// 预乘缓冲里 <c>颜色 × alpha = 0</c>，所以"全 0"天然表示"全透明"，
    /// 不需要任何特判分支。若哪天有人改成"直通语义"，全 0 就变成了"全黑不透明"，
    /// 新建的空白图层会凭空变成一块黑色 —— 这条断言就是为了让那种改动当场变红。
    /// </summary>
    [Fact]
    public void Create_全零缓冲_因为预乘语义下零就是透明()
    {
        PixelBuffer buffer = PixelBuffer.Create(3, 2);

        byte[] raw = buffer.Raw.ToArray();
        Assert.Equal(24, raw.Length);
        Assert.All(raw, b => Assert.Equal(0, b));
    }

    /// <summary>
    /// 宽或高不为正必须抛 <see cref="ArgumentOutOfRangeException"/>，并且
    /// <c>ParamName</c> 要指出<b>到底是哪个参数</b>。分开判宽度与高度是有意义的：
    /// 上层把 <c>Size</c> 反过来传（DocSize 是 Width,Height，DocRect 是 Origin,Size）
    /// 时，只有 ParamName 能让人一眼看出是顺序传反了。
    /// </summary>
    [Theory]
    [InlineData(0, 3, "width")]
    [InlineData(-1, 3, "width")]
    [InlineData(3, 0, "height")]
    [InlineData(3, -1, "height")]
    public void Create_宽或高非正_抛ArgumentOutOfRangeException并指出参数(int w, int h, string paramName)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Create(w, h));

        Assert.Equal(paramName, ex.ParamName);
    }

    /// <summary>
    /// 尺寸大到 <c>Width*4*Height</c> 溢出 <see cref="int"/> 时必须拒绝，而不是回绕成负数
    /// 去 <c>new byte[负数]</c> 抛一个语焉不详的异常。
    /// <para>本例的算式：30000 × 4 × 30000 = 3,600,000,000 &gt; int.MaxValue (2,147,483,647)。
    /// 30000 不是随手写的 —— 它正是 DocumentLimits 的 maxSide，这个量级是真实存在的。</para>
    /// </summary>
    [Fact]
    public void Create_总字节数溢出int上限_抛ArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Create(30000, 30000));
    }

    /// <summary>
    /// 行距必须恒等于 <c>Width * 4</c>，<b>没有任何对齐填充</b>。
    /// 这条是"整块 <see cref="PixelBuffer.Rgba"/> 是连续内存、下标就是
    /// <c>y * Width * 4 + x * 4</c>"的前提。一旦有人为了 SIMD 对齐偷偷加 padding，
    /// 所有按 <c>y*Width*4</c> 直接算下标的直译代码会整体错位一行，而且不报异常 ——
    /// 所以必须用非 4/8/16 倍数的宽度（如 7、3）来测，才能真正抓到多出来的 padding。
    /// </summary>
    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(3, 2, 12)]
    [InlineData(7, 3, 28)]
    [InlineData(64, 64, 256)]
    public void Stride_恒等于宽度乘四_无对齐填充(int w, int h, int expectedStride)
    {
        PixelBuffer buffer = PixelBuffer.Create(w, h);

        Assert.Equal(expectedStride, buffer.Stride);
        Assert.Equal(w, buffer.Width);
        Assert.Equal(h, buffer.Height);
        // 长度必须正好等于 stride × height：多一个字节就说明有 padding，少一个说明算错了。
        Assert.Equal(expectedStride * h, buffer.Raw.Length);
    }

    // ─────────────────── 标量运算：期望值逐条手算 ───────────────────

    /// <summary>
    /// <see cref="PixelBuffer.Premultiply"/> = <c>(byte)((channel*alpha + 127) / 255)</c>，
    /// 逐字对应 C 的 <c>p[0] = (uint8_t)((color[0]*a + 127u)/255u)</c>。
    /// <para><b>为什么必须是 +127 而不是直接整除：</b>整数除法向零截断，等于"永远向下取整"，
    /// 会让每个通道<b>系统性地偏暗半个量化步</b>。+127 把截断变成四舍五入。
    /// 判断两者何时会分歧：记 <c>r = (c*a) mod 255</c>，
    /// 不加 127 时结果为 <c>floor(c*a/255)</c>，加了以后为 <c>floor((c*a+127)/255)</c>，
    /// 两者<b>仅当 r ≥ 128</b> 时相差 1。</para>
    /// <para>下面每条期望值的算式：</para>
    /// <list type="bullet">
    /// <item><c>(255,255)</c>：255×255=65025，+127=65152，65152/255=255.49 → <b>255</b>。</item>
    /// <item><c>(128,255)</c>：128×255=32640，+127=32767，32767/255=128.49 → <b>128</b>。</item>
    /// <item><c>(254,255)</c>：254×255=64770，+127=64897，64897/255=254.49 → <b>254</b>。</item>
    /// <item><c>(255,128)</c>：255×128=32640，+127=32767，32767/255=128.49 → <b>128</b>。</item>
    /// <item><c>(0,128)</c>：0+127=127，127/255=0.498 → <b>0</b>。</item>
    /// <item><c>(0,0)</c> / <c>(0,255)</c> / <c>(255,0)</c>：分子都是 127，127/255=0.498 → <b>0</b>。</item>
    /// <item><c>(1,255)</c>：255+127=382，382/255=1.498 → <b>1</b>。</item>
    /// <item><c>(255,1)</c>：255+127=382，382/255=1.498 → <b>1</b>
    /// （顺带说明：alpha=1 时任何通道都压到 ≤1，正是"预乘通道恒 ≤ alpha"的表现）。</item>
    /// <item><c>(100,128)</c>：100×128=12800，+127=12927，12927/255=50.69 → <b>50</b>。</item>
    /// <item><c>(100,127)</c>：12700+127=12827，12827/255=50.30 → <b>50</b>（同值但路径不同）。</item>
    /// <item><c>(3,127)</c>：381+127=508，508/255=1.99 → <b>1</b>。
    /// 这是<b>不该</b>被四舍五入推上去的例子：3×127/255=1.494 &lt; 1.5。</item>
    /// <item><c>(3,128)</c>：384+127=511，511/255=2.004 → <b>2</b>。
    /// 与上一条只差 alpha 一个刻度：3×128/255=1.506 &gt; 1.5，必须被推上去。
    /// <b>这两条合起来证明 +127 是四舍五入而不是"无条件 +1"。</b></item>
    /// <item><c>(2,200)</c>：400+127=527，527/255=2.07 → <b>2</b>；
    /// 而 400/255=1.569 截断会得 1 —— 这是 +127 的净收益。</item>
    /// <item><c>(128,1)</c>：128+127=255，255/255=<b>1.000</b> → <b>1</b>；
    /// 截断写法得 128/255=0.502 → 0，差整整 1 个量化步。</item>
    /// <item><c>(1,128)</c>：128+127=255，255/255=1.000 → <b>1</b>（同上，截断会得 0）。</item>
    /// <item><c>(201,7)</c>：1407+127=1534，1534/255=6.016 → <b>6</b>；截断得 5.518 → 5。</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(128, 255, 128)]
    [InlineData(254, 255, 254)]
    [InlineData(1, 255, 1)]
    [InlineData(255, 128, 128)]
    [InlineData(0, 128, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 0, 0)]
    [InlineData(255, 1, 1)]
    [InlineData(128, 1, 1)]
    [InlineData(1, 128, 1)]
    [InlineData(100, 128, 50)]
    [InlineData(100, 127, 50)]
    [InlineData(3, 127, 1)]
    [InlineData(3, 128, 2)]
    [InlineData(2, 200, 2)]
    [InlineData(201, 7, 6)]
    public void Premultiply_与C的AdjustPixels逐字一致(int channel, int alpha, int expected)
    {
        Assert.Equal((byte)expected, PixelBuffer.Premultiply((byte)channel, (byte)alpha));
    }

    /// <summary>
    /// alpha=255 时 <c>Premultiply</c> 必须是<b>恒等映射</b>：
    /// <c>(255c + 127)/255 = c + 127/255 = c + 0.498</c>，截断后仍是 c。
    /// <para>这条不是"顺手加的恒等用例"，而是 PNG 往返的基石：不透明像素走完
    /// 导入再导出必须逐位不变，否则每存一次 PNG 就在无透明区域引入色偏，
    /// 而这种色偏在肉眼和大多数单测里都看不出来。</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(200)]
    [InlineData(254)]
    [InlineData(255)]
    public void Premultiply_alpha为255时是恒等映射(int value)
    {
        Assert.Equal((byte)value, PixelBuffer.Premultiply((byte)value, 255));
    }

    /// <summary>
    /// 预乘后的通道<b>恒 ≤ alpha</b>：因为通道与 alpha 都是 0..255，
    /// 最大值出现在 channel=255，此时 <c>(255a + 127)/255 = a + 0.498</c> → a。
    /// <para>这条是铁律 2 的核心不变量：预乘缓冲里不可能出现"R 比 A 还大"的字节。
    /// 任何绕过 <see cref="PixelBuffer.Premultiply"/> 直接往 <see cref="PixelBuffer.Raw"/>
    /// 塞直通值的写法都会破坏它，而破坏之后所有 alpha 相关的代码依然能跑、依然有输出，
    /// 只是边缘偏暗 —— 所以必须在标量层面把它钉死。</para>
    /// </summary>
    [Theory]
    [InlineData(255, 128)]
    [InlineData(200, 50)]
    [InlineData(255, 200)]
    [InlineData(128, 64)]
    [InlineData(1, 1)]
    [InlineData(255, 1)]
    public void Premultiply_结果恒不超过alpha(int channel, int alpha)
    {
        byte result = PixelBuffer.Premultiply((byte)channel, (byte)alpha);

        Assert.True(result <= alpha, $"{channel},{alpha} 预乘得到 {result}，超过了 alpha。");
    }

    /// <summary>
    /// <see cref="PixelBuffer.Unpremultiply"/> = <c>(channel*255 + alpha/2) / alpha</c>，
    /// alpha=0 时直接返回 0（否则是 0/0）。
    /// <para>下列期望值的算式（<c>+ alpha/2</c> 同样是四舍五入，不是截断）：</para>
    /// <list type="bullet">
    /// <item><c>(0,255)</c>：0+127=127，127/255=0.498 → <b>0</b>。</item>
    /// <item><c>(1,255)</c>：255+127=382，382/255=1.498 → <b>1</b>。</item>
    /// <item><c>(128,255)</c>：32640+127=32767，32767/255=128.49 → <b>128</b>。</item>
    /// <item><c>(254,255)</c>：64770+127=64897，64897/255=254.49 → <b>254</b>。</item>
    /// <item><c>(255,255)</c>：65025+127=65152，65152/255=255.49 → <b>255</b>。</item>
    /// <item><c>(0,1)</c>：(0+0)/1=0 → <b>0</b>（此处 alpha/2 = 0，四舍五入位缺失但无害）。</item>
    /// <item><c>(1,1)</c>：(255+0)/1=255 → <b>255</b>。
    /// 注意它<b>不等于 1</b>：alpha=1 时量化极粗，预乘会把 [128,255] 的全部通道压成 1。</item>
    /// <item><c>(0,2)</c>：(0+1)/2=0.5 → <b>0</b>。</item>
    /// <item><c>(1,2)</c>：(255+1)/2=128 → <b>128</b>。</item>
    /// <item><c>(0,128)</c>：0+64=64，64/128=0.5 → <b>0</b>。</item>
    /// <item><c>(1,128)</c>：255+64=319，319/128=2.492 → <b>2</b>。</item>
    /// <item><c>(2,128)</c>：510+64=574，574/128=4.484 → <b>4</b>。</item>
    /// <item><c>(4,128)</c>：1020+64=1084，1084/128=8.469 → <b>8</b>。</item>
    /// <item><c>(8,128)</c>：2040+64=2104，2104/128=16.44 → <b>16</b>。</item>
    /// <item><c>(16,128)</c>：4080+64=4144，4144/128=32.375 → <b>32</b>。</item>
    /// <item><c>(32,128)</c>：8160+64=8224，8224/128=64.25 → <b>64</b>。</item>
    /// <item><c>(50,128)</c>：12750+64=12814，12814/128=100.11 → <b>100</b>。</item>
    /// <item><c>(64,128)</c>：16320+64=16384，16384/128=<b>128.000</b> → <b>128</b>（整除，是最好的锚点）。</item>
    /// <item><c>(100,128)</c>：25500+64=25564，25564/128=199.72 → <b>199</b>。</item>
    /// <item><c>(128,128)</c>：32640+64=32704，32704/128=255.5 → <b>255</b>。</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(0, 255, 0)]
    [InlineData(1, 255, 1)]
    [InlineData(128, 255, 128)]
    [InlineData(254, 255, 254)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 255)]
    [InlineData(0, 2, 0)]
    [InlineData(1, 2, 128)]
    [InlineData(0, 128, 0)]
    [InlineData(1, 128, 2)]
    [InlineData(2, 128, 4)]
    [InlineData(4, 128, 8)]
    [InlineData(8, 128, 16)]
    [InlineData(16, 128, 32)]
    [InlineData(32, 128, 64)]
    [InlineData(50, 128, 100)]
    [InlineData(64, 128, 128)]
    [InlineData(100, 128, 199)]
    [InlineData(128, 128, 255)]
    public void Unpremultiply_普通值与整数四舍五入一致(int channel, int alpha, int expected)
    {
        Assert.Equal((byte)expected, PixelBuffer.Unpremultiply((byte)channel, (byte)alpha));
    }

    /// <summary>
    /// alpha=0 时反预乘必须返回 0 而不是抛异常或除零。
    /// <para>这条看着简单，但它是"导出一张带透明区域的 PNG"必经的分支：
    /// 半透明路径之外还有全透明像素，<c>channel*255/a</c> 里的分母就是 0。
    /// 源码选择返回 0 的理由是<b>全透明像素没有颜色</b>，返回什么在语义上都无意义，
    /// 选 0 至少不会引入 NaN 或异常。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(128, 0)]
    [InlineData(254, 0)]
    [InlineData(255, 0)]
    public void Unpremultiply_alpha为0时返回0_不发生除零(int channel, int alpha)
    {
        Assert.Equal(0, PixelBuffer.Unpremultiply((byte)channel, (byte)alpha));
    }

    /// <summary>
    /// 反预乘算出超过 255 时<b>必须钳到 255</b>，绝不能直接强转 byte。
    /// <para>这一条比它看起来重要得多：<c>(byte)65025</c> 在 C# 里是 unchecked 转换，
    /// 结果是 <c>65025 mod 256 = <b>1</b></c> —— 不抛异常、不报警告，直接把纯白变成近黑。
    /// 这类"安静的错误结果"正是最贵的返工来源。</para>
    /// <para>下列算式：</para>
    /// <list type="bullet">
    /// <item><c>(255,1)</c>：(255×255 + 0)/1 = 65025 &gt; 255 → 钳成 <b>255</b>。</item>
    /// <item><c>(128,1)</c>：(128×255 + 0)/1 = 32640 &gt; 255 → <b>255</b>。</item>
    /// <item><c>(2,1)</c>：(2×255 + 0)/1 = 510 &gt; 255 → <b>255</b>。</item>
    /// <item><c>(255,2)</c>：(65025 + 1)/2 = 32513 &gt; 255 → <b>255</b>。</item>
    /// <item><c>(255,127)</c>：(65025 + 63)/127 = 65088/127 = 512.5 → 512 &gt; 255 → <b>255</b>。</item>
    /// <item><c>(200,100)</c>：(200×255 + 50)/100 = 51050/100 = 510 → <b>255</b>。</item>
    /// </list>
    /// <para><b>补充说明（不是 bug，是有意的防御）：</b>合规的预乘数据满足 channel ≤ alpha，
    /// 而 255×alpha/alpha = 255 恰好不溢出，所以上面这些组合<b>从合法预乘数据里走不到</b>。
    /// 钳制分支存在的意义是给"直译代码写坏了一个字节"的输入兜底 ——
    /// 兜底是否生效，只能靠直接打这条分支来验证。</para>
    /// </summary>
    [Theory]
    [InlineData(255, 1, 255)]
    [InlineData(128, 1, 255)]
    [InlineData(2, 1, 255)]
    [InlineData(255, 2, 255)]
    [InlineData(255, 127, 255)]
    [InlineData(200, 100, 255)]
    public void Unpremultiply_溢出时钳到255_不能截断成byte(int channel, int alpha, int expected)
    {
        Assert.Equal((byte)expected, PixelBuffer.Unpremultiply((byte)channel, (byte)alpha));
    }

    // ─────────────────── 导入：铁律 2 的入口，必须预乘 ───────────────────

    /// <summary>
    /// 🔴 <b>本文件最核心的一条断言。</b>导入直通色后，缓冲里存的必须是<b>预乘值</b>。
    /// <para>取直通色 (200, 100, 50, 128)：</para>
    /// <list type="bullet">
    /// <item>R：(200×128 + 127)/255 = 25727/255 = 100.89 → <b>100</b>（≠ 200）。</item>
    /// <item>G：(100×128 + 127)/255 = 12927/255 = 50.69 → <b>50</b>（≠ 100）。</item>
    /// <item>B：(50×128 + 127)/255 = 6527/255 = 25.60 → <b>25</b>（≠ 50）。</item>
    /// <item>A：<b>128</b> 原样保留（alpha 自身不参与预乘）。</item>
    /// </list>
    /// <para>断言里显式写了 <c>Assert.NotEqual(200, raw[0])</c>：万一哪天有人把
    /// <see cref="PixelBuffer.FromStraightRgba"/> 改成直通直通拷贝，这条会以"看起来完全合理"的
    /// 方式红掉，而不是等到合成器跑出黑边才被发现。</para>
    /// </summary>
    [Fact]
    public void FromStraightRgba_存进去的必须是预乘值而不是直通值()
    {
        byte[] src = { 200, 100, 50, 128 };

        PixelBuffer buffer = PixelBuffer.FromStraightRgba(src, 1, 1);

        byte[] raw = buffer.Raw.ToArray();
        Assert.Equal(new byte[] { 100, 50, 25, 128 }, raw);

        // 反证：存储值必须与直通值明显不同，否则就是没做预乘。
        Assert.NotEqual(src[0], raw[0]);
        Assert.NotEqual(src[1], raw[1]);
        Assert.NotEqual(src[2], raw[2]);
        // alpha 必须原样保留。
        Assert.Equal(128, raw[3]);
    }

    /// <summary>
    /// alpha=0 的像素导入后 RGB 必须是 0。
    /// <para>算式：<c>Premultiply(255, 0) = (255×0 + 127)/255 = 0</c>，公式本身已经给出 0；
    /// 源码还额外走了一条 <c>if (a == 0) continue;</c> 的短路。</para>
    /// <para>为什么值得单独测：alpha=0 时直通色<b>在语义上是垃圾数据</b>（常见来源是
    /// 未初始化的内存或被 memset 过的缓冲区，通常还是纯白）。如果实现"聪明地"把直通色
    /// 原样保留，那么同一个缓冲里就会同时存在"alpha=0 且 RGB=255"的字节 ——
    /// 这会让任何按预乘假设合成这段像素的代码算出一个"黑色半透明边"。
    /// 本例同时验证短路与公式两条路径给出的结果一致（都是全 0）。</para>
    /// </summary>
    [Fact]
    public void FromStraightRgba_alpha为0的像素_RGB必须被清成0()
    {
        // 第一个像素全透明且带垃圾色 255，第二个像素完全不透明，验证只清了第一个。
        byte[] src = { 255, 255, 255, 0, 10, 20, 30, 255 };

        PixelBuffer buffer = PixelBuffer.FromStraightRgba(src, 2, 1);

        byte[] raw = buffer.Raw.ToArray();
        Assert.Equal(0, raw[0]);
        Assert.Equal(0, raw[1]);
        Assert.Equal(0, raw[2]);
        Assert.Equal(0, raw[3]);

        // 相邻像素不能被误清：alpha=255 时预乘是恒等映射。
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, raw[4..]);
    }

    /// <summary>
    /// 源数据长度<b>恰好</b>等于 <c>Width*4*Height</c> 时必须成功。
    /// <para>源码判据是 <c>src.Length &lt; length</c>（严格小于），所以等长是合法边界。
    /// 用 <c>Assert.Equal</c> 精确到字节数来锁住"不多不少"这个边界：
    /// 2×2 需要 4 像素 × 4 通道 = 16 字节。</para>
    /// </summary>
    [Fact]
    public void FromStraightRgba_源数据长度恰好等于所需时成功()
    {
        PixelBuffer buffer = PixelBuffer.FromStraightRgba(new byte[16], 2, 2);

        Assert.Equal(2, buffer.Width);
        Assert.Equal(2, buffer.Height);
        Assert.Equal(16, buffer.Raw.Length);
        Assert.Equal(8, buffer.Stride);
    }

    /// <summary>
    /// 源数据比需要的多出来时必须<b>只取前 <c>Width*4*Height</c> 字节</b>。
    /// <para>这条对应真实调用场景：PNG 解码器给出的行缓冲常带 <c>filter bytes</c>
    /// 或行距对齐尾巴。若实现改成"按源长度循环"就会越界写爆目标数组。</para>
    /// <para>源数据 20 字节、只需 16 字节；第 0 像素取 (200,100,50,128) → 预乘 (100,50,25,128)，
    /// 第 1 像素取的是 <c>src[4..8]</c> 全 0 → alpha=0 → 短路留全 0。</para>
    /// </summary>
    [Fact]
    public void FromStraightRgba_源数据多出的尾部被忽略()
    {
        byte[] src = new byte[20];
        src[0] = 200;
        src[1] = 100;
        src[2] = 50;
        src[3] = 128;

        PixelBuffer buffer = PixelBuffer.FromStraightRgba(src, 2, 2);

        Assert.Equal(16, buffer.Raw.Length);
        Assert.Equal(new byte[] { 100, 50, 25, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, buffer.Raw.ToArray());
    }

    /// <summary>
    /// 源数据<b>差一个字节</b>就必须抛 <see cref="ArgumentException"/>。
    /// <para>2×2 需要 16 字节，给 15 字节。这是判据 <c>src.Length &lt; length</c> 的另一侧边界，
    /// 必须与上一条"等长成功"成对出现，否则判据写成 <c>&lt;=</c> 或 <c>&lt; length - 1</c>
    /// 都不会有任何测试报警。</para>
    /// <para>另外注意：xunit 的 <c>Assert.Throws&lt;T&gt;</c> 要求<b>类型精确匹配</b>，
    /// 而 <see cref="ArgumentOutOfRangeException"/> 是 <see cref="ArgumentException"/> 的子类。
    /// 这条测试因此顺带证明了"源数据太短"与"尺寸非法"走的是<b>不同</b>的异常类型
    /// —— 若哪天有人把长度检查改成抛 AOORE，这条会以类型不匹配变红。</para>
    /// </summary>
    [Fact]
    public void FromStraightRgba_源数据差一个字节就抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PixelBuffer.FromStraightRgba(new byte[15], 2, 2));
    }

    // ─────────────────── 往返：量化误差必须被显式承认 ───────────────────

    /// <summary>
    /// alpha=255 的往返必须<b>逐位无损</b>。
    /// <para>依据是上面已验证的两条：<c>Premultiply(x, 255) = x</c>（因为
    /// <c>255x + 127 &lt; 255(x+1)</c>），<c>Unpremultiply(x, 255) = x</c>（因为
    /// <c>255x + 127 &lt; 255(x+1)</c>）。两条都成立时，复合必然是恒等映射。</para>
    /// <para>所以"PNG 存进去再读出来，不透明区域不该有半点色偏"这句话在这条路径上是<b>硬保证</b>，
    /// 可以要求严格相等而不需要任何容差。取非对称的通道值（200,100,50）是为了顺带证明
    /// 四个通道没有被交叉处理（如果 R 跑到了 G 上，逐位相等的断言反而抓不到，
    /// 但下面的 alpha=128 那条用同样的值抓得到）。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(200, 100, 50)]
    [InlineData(1, 2, 3)]
    [InlineData(255, 254, 253)]
    [InlineData(128, 64, 32)]
    public void 往返_alpha255逐位无损_因为预乘与反预乘都是恒等映射(int r, int g, int b)
    {
        byte[] src = { (byte)r, (byte)g, (byte)b, 255 };

        PixelBuffer premultiplied = PixelBuffer.FromStraightRgba(src, 1, 1);
        PixelBuffer straight = premultiplied.ToStraightRgba();

        Assert.Equal(src, straight.Raw.ToArray());
    }

    /// <summary>
    /// alpha=128 的往返：<b>下面每个期望值都是逐步手算出来的精确结果，不是容差范围。</b>
    /// <para>做法是两步都写出来，而不是"容忍误差"：
    /// 第一步 <c>P = (v×128 + 127)/255</c>，第二步 <c>U = (P×255 + 64)/128</c>。</para>
    /// <list type="bullet">
    /// <item><c>v=0</c>：P=127/255=0 → 0；U=(0+64)/128=0.5 → <b>0</b>。</item>
    /// <item><c>v=2</c>：P=(256+127)/255=1.502 → 1；U=(255+64)/128=2.492 → <b>2</b>（精确回原值）。</item>
    /// <item><c>v=4</c>：P=639/255=2.505 → 2；U=(510+64)/128=4.484 → <b>4</b>。</item>
    /// <item><c>v=8</c>：P=1151/255=4.514 → 4；U=(1020+64)/128=8.469 → <b>8</b>。</item>
    /// <item><c>v=16</c>：P=2175/255=8.529 → 8；U=(2040+64)/128=16.44 → <b>16</b>。</item>
    /// <item><c>v=32</c>：P=4223/255=16.56 → 16；U=(4080+64)/128=32.375 → <b>32</b>。</item>
    /// <item><c>v=64</c>：P=8319/255=32.62 → 32；U=(8160+64)/128=64.25 → <b>64</b>。</item>
    /// <item><c>v=100</c>：P=12927/255=50.69 → 50；U=(12750+64)/128=100.11 → <b>100</b>。</item>
    /// <item><c>v=128</c>：P=16511/255=64.75 → 64；U=16384/128=128.000 → <b>128</b>（整除，零误差）。</item>
    /// <item><c>v=255</c>：P=32767/255=128.49 → 128；U=(32640+64)/128=255.5 → <b>255</b>。</item>
    /// <item><c>v=200</c>：P=25727/255=100.89 → 100；U=25564/128=199.72 → <b>199</b>（差 1）。</item>
    /// <item><c>v=1</c>：P=255/255=1.000 → 1；U=(255+64)/128=2.492 → <b>2</b>（差 1）。</item>
    /// </list>
    /// <para><b>为什么这里不能要求 256 个输入全部精确往返：</b>误差会被反预乘放大约
    /// <c>255/alpha</c> 倍。设预乘的取整误差为 e（|e| ≤ 0.5），则还原误差上界约
    /// <c>255e/128 + 0.5 ≈ 1.5</c>，取整后就是 ±1。所以 alpha=128 的往返<b>必然</b>存在差 1 的点，
    /// 写 <c>Assert.Equal(v, back)</c> 会在 v=1 和 v=200 上失败；反过来若把实现改成
    /// "反预乘多减一点强行对齐"，又会把 alpha 更低的路径搞坏 —— 那才是真错。
    /// 本文件的做法是<b>把差 1 的点显式钉进期望值</b>，既不放宽断言，也不掩盖非无损的事实。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(64, 64)]
    [InlineData(100, 100)]
    [InlineData(128, 128)]
    [InlineData(255, 255)]
    [InlineData(200, 199)]
    [InlineData(1, 2)]
    public void 往返_alpha128的量化误差已被逐条钉死(int v, int expectedAfterRoundTrip)
    {
        byte[] src = { (byte)v, (byte)v, (byte)v, 128 };

        PixelBuffer premultiplied = PixelBuffer.FromStraightRgba(src, 1, 1);
        PixelBuffer straight = premultiplied.ToStraightRgba();

        byte[] back = straight.Raw.ToArray();
        Assert.Equal(expectedAfterRoundTrip, back[0]);
        Assert.Equal(expectedAfterRoundTrip, back[1]);
        Assert.Equal(expectedAfterRoundTrip, back[2]);
        Assert.Equal(128, back[3]);
    }

    /// <summary>
    /// alpha=128 时<b>三个通道不能互相串位</b>：非对称色 (200,100,50,128)
    /// 往返后是 (199,100,50,128) —— R 差 1，G/B 精确回原值。
    /// <para>算式：存 (100, 50, 25, 128)（见导入那条）；导出的
    /// <c>U(100,128)=199</c>、<c>U(50,128)=100</c>、<c>U(25,128)=(6375+64)/128=50.30 → 50</c>。</para>
    /// <para>如果三个通道被写成了同一份（比如某个循环变量漏了步进），
    /// 结果会是 (199,199,199) 或 (199,100,25) 这类明显不对的值，逐位断言立刻抓到；
    /// 而只看"最大误差 ≤ 1"的宽松断言在 v=200 这种点上反而会放过 R、B 互换后误差同为 1 的情况。</para>
    /// </summary>
    [Fact]
    public void 往返_alpha128时四个通道各归各位_不串位()
    {
        byte[] src = { 200, 100, 50, 128 };

        PixelBuffer premultiplied = PixelBuffer.FromStraightRgba(src, 1, 1);
        Assert.Equal(new byte[] { 100, 50, 25, 128 }, premultiplied.Raw.ToArray());

        PixelBuffer straight = premultiplied.ToStraightRgba();

        Assert.Equal(new byte[] { 199, 100, 50, 128 }, straight.Raw.ToArray());
    }

    /// <summary>
    /// alpha=0 的像素往返后是 <b>(0,0,0,0)</b>，颜色信息被<b>刻意丢弃</b>。
    /// <para>导入时短路写 0，导出时再短路（结果数组本来就是全 0），所以两遍之后
    /// 直通色 (255,255,255,0) 变成 (0,0,0,0)。</para>
    /// <para>把这条钉死是为了防止有人"优化"成"alpha==0 时保留原色"：
    /// 那会让同一个缓冲里出现 RGB≠0 且 A=0 的像素，破坏
    /// <c>Premultiply_结果恒不超过alpha</c> 钉住的不变量，
    /// 而且"看起来更保真"，实际上让预乘假设失效。</para>
    /// </summary>
    [Fact]
    public void 往返_alpha为0时颜色信息被丢弃为全0()
    {
        byte[] src = { 255, 255, 255, 0 };

        PixelBuffer premultiplied = PixelBuffer.FromStraightRgba(src, 1, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, premultiplied.Raw.ToArray());

        PixelBuffer straight = premultiplied.ToStraightRgba();

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, straight.Raw.ToArray());
    }

    /// <summary>
    /// 🔴 <see cref="PixelBuffer.ToStraightRgba"/> 必须返回<b>新实例</b>，
    /// 原实例的字节一个都不能变。
    /// <para>这是铁律 2 反复返工的根源：如果导出改成"就地反预乘"，那么同一个
    /// <see cref="PixelBuffer"/> 实例会在"预乘 / 直通"之间来回切换，
    /// 而类型的任何字段都没变、类型检查照过、编译器不会报任何东西。
    /// 后果是同一个对象被两个模块先后读到时，一个按预乘解释、一个按直通解释，
    /// 结果只在半透明区域出现偏色。</para>
    /// <para>因此这条同时断言三件事：返回值不是同一个引用（<c>NotSame</c>）、
    /// 原缓冲内容与调用前逐字节相同、导出结果确实是直通色。</para>
    /// </summary>
    [Fact]
    public void ToStraightRgba_返回新实例_原实例的预乘字节一个都不变()
    {
        byte[] src = { 200, 100, 50, 128 };
        PixelBuffer premultiplied = PixelBuffer.FromStraightRgba(src, 1, 1);
        byte[] before = premultiplied.Raw.ToArray();

        PixelBuffer straight = premultiplied.ToStraightRgba();

        Assert.NotSame(premultiplied, straight);
        Assert.Equal(before, premultiplied.Raw.ToArray());
        Assert.Equal(new byte[] { 100, 50, 25, 128 }, premultiplied.Raw.ToArray());

        // 导出的才是直通色（199,100,50,128，见上一条的逐通道算式）。
        Assert.Equal(new byte[] { 199, 100, 50, 128 }, straight.Raw.ToArray());
    }

    // ─────────────────── Clone 与 Fill ───────────────────

    /// <summary>
    /// <see cref="PixelBuffer.Clone"/> 必须是<b>深拷贝</b>：改副本不影响原缓冲。
    /// <para>这条直接对应"快照式撤销"：波次 3 的撤销栈会克隆一份缓冲再开始改，
    /// 如果 <c>Clone</c> 只是复制了数组引用，那么"撤销"改的其实还是当前缓冲，
    /// 表现为<b>撤销功能静默失效</b>—— 没有异常、没有崩溃，只是历史丢了。</para>
    /// <para>本例把副本的前两个字节改成 0xAB/0xCD，再断言原缓冲逐字节未变。</para>
    /// </summary>
    [Fact]
    public void Clone_是深拷贝_改副本不影响原缓冲()
    {
        byte[] src = { 200, 100, 50, 128, 255, 0, 0, 255 };
        PixelBuffer original = PixelBuffer.FromStraightRgba(src, 2, 1);

        PixelBuffer copy = original.Clone();
        byte[] before = original.Raw.ToArray();

        copy.Raw[0] = 0xAB;
        copy.Raw[1] = 0xCD;

        Assert.NotSame(original, copy);
        Assert.Equal(before, original.Raw.ToArray());
        Assert.Equal(new byte[] { 100, 50, 25, 128, 255, 0, 0, 255 }, original.Raw.ToArray());
    }

    /// <summary>
    /// <see cref="PixelBuffer.Fill"/> 收的是<b>直通色</b>，但写进缓冲的必须是<b>预乘值</b>，
    /// 并且<b>覆盖每一个字节</b>（循环边界不能少写最后一个像素的 alpha）。
    /// <para>下列期望值的算式（<c>a = 128</c> 时除以 128，除以 255 时走恒等映射）：</para>
    /// <list type="bullet">
    /// <item><c>(200,100,50,128)</c> → R=(25600+127)/255=100.89→<b>100</b>、
    /// G=12927/255=50.69→<b>50</b>、B=6527/255=25.60→<b>25</b>、A=<b>128</b>。</item>
    /// <item><c>(10,20,30,255)</c> → alpha=255 是恒等映射 → <b>(10,20,30,255)</b>。</item>
    /// <item><c>(255,255,255,0)</c> → 每个通道 (255×0+127)/255=0.498→<b>0</b> → <b>(0,0,0,0)</b>。</item>
    /// <item><c>(128,128,128,128)</c> → 16511/255=64.75→<b>64</b> → <b>(64,64,64,128)</b>。</item>
    /// </list>
    /// <para>用 3×2（6 像素 / 24 字节）的缓冲、并断言 24 个字节<b>全部</b>相等，
    /// 是为了同时抓住两类 bug：没做预乘（值是直通色），以及循环写成 <c>i &lt; Length - 4</c>
    /// 导致最后一个像素残留旧数据（值对但最后一个 alpha 是 0，看起来像"画了 5 个像素"）。</para>
    /// </summary>
    [Theory]
    [InlineData(200, 100, 50, 128, 100, 50, 25, 128)]
    [InlineData(10, 20, 30, 255, 10, 20, 30, 255)]
    [InlineData(255, 255, 255, 0, 0, 0, 0, 0)]
    [InlineData(128, 128, 128, 128, 64, 64, 64, 128)]
    public void Fill_收直通色但写入预乘值且覆盖每个像素(int r, int g, int b, int a, int er, int eg, int eb, int ea)
    {
        PixelBuffer buffer = PixelBuffer.Create(3, 2);

        buffer.Fill(new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a));

        byte[] expected = new byte[24];
        for (int i = 0; i < expected.Length; i += 4)
        {
            expected[i] = (byte)er;
            expected[i + 1] = (byte)eg;
            expected[i + 2] = (byte)eb;
            expected[i + 3] = (byte)ea;
        }

        Assert.Equal(expected, buffer.Raw.ToArray());
    }
}
