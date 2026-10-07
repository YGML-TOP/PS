using System.Text;
using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests.Contract;

/// <summary>
/// 🔴 <see cref="Golden"/> 与 <see cref="PngCodec"/> 的黄金样本回归<b>地基</b>测试。
/// </summary>
/// <remarks>
/// <para><b>为什么这组测试是红线级</b>：任务书写明「golden 不通过 = 禁止合并」。
/// 而 golden 的判定完全建立在本文件覆盖的两件事上：</para>
/// <list type="number">
/// <item><see cref="PngCodec"/> 能把像素<b>逐字节无损</b>地编码成合法 PNG，并能原样解回来；</item>
/// <item><see cref="Golden.CompareRgba"/> 能把 <see cref="PixelBuffer"/> 的<b>预乘</b>内部表示
/// 正确反预乘后，再与 PNG 的<b>直通</b>像素比较。</item>
/// </list>
/// <para>这两件事任何一件错，golden 都会给出「假通过」或「假失败」——
/// 而这恰好是最危险的失败模式：测试全绿，像素悄悄错掉。</para>
///
/// <para><b>期望值怎么来的</b>：全部按源码语义<b>手工推导</b>，
/// 不是「先跑一遍把输出抄成期望值」。两个最容易推错的量在这里先说清楚：
/// <list type="bullet">
/// <item><see cref="GoldenResult.MaxDelta"/> 是<b>全图所有通道</b>的最大绝对差，
/// <b>与容差无关</b>，通过时也可以非 0。</item>
/// <item><see cref="GoldenResult.DifferingPixels"/> 是「<b>任一</b>通道差值 &gt; 容差」的像素个数；
/// 差值<b>恰好等于</b>容差不算差异。</item>
/// </list></para>
///
/// <para><b>不污染仓库</b>：基准 PNG 一律写在
/// <c>Path.GetTempPath()/compositor-golden-tests/&lt;guid&gt;</c> 下，绝不落项目目录；
/// <see cref="Golden"/> 失败时按<b>相对当前工作目录</b>写出的 <c>test-artifacts/</c> 差异图，
/// 由 <see cref="GoldenFixture"/> 在 <c>Dispose</c> 里<b>按文件名前缀精确</b>删掉。
/// <c>using</c> 声明展开就是 try/finally，所以<b>断言失败也一定执行清理</b>。</para>
/// </remarks>
public sealed class GoldenTests
{
    // ───────────────────── 一、PngCodec 往返：框架的地基 ─────────────────────

    /// <summary>
    /// 多组尺寸下 <c>Encode → Decode</c> 必须<b>逐字节</b>还原。
    /// </summary>
    /// <remarks>
    /// 为什么尺寸必须覆盖 1×1、2×3、7×5、16×16：PNG 每行前面多一个滤波器字节，
    /// 行内偏移全靠 <c>width * 4 + 1</c> 这个 +1 对齐。
    /// <list type="bullet">
    /// <item><b>1×1</b>：只有一行，「上一行不存在」的反滤波边界（<c>a/b/c</c> 全为 0）。</item>
    /// <item><b>2×3</b>：宽小于高，行缓冲错位最容易在这里藏。</item>
    /// <item><b>7×5</b>：奇数宽度，<c>x * channels</c> 的取模/对齐最容易算错。</item>
    /// <item><b>16×16</b>：行数够多，能覆盖「上一行」缓冲的逐行交换。</item>
    /// </list>
    /// 只测一个尺寸，<c>rowStart</c> / 行缓冲写回的 bug 会整批漏过去。
    /// </remarks>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(7, 5)]
    [InlineData(16, 16)]
    public void PngCodec_往返后逐字节一致(int width, int height)
    {
        byte[] source = DeterministicOpaqueRgba(width, height);

        (int decodedWidth, int decodedHeight, byte[] decoded) =
            PngCodec.Decode(PngCodec.Encode(width, height, source));

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        AssertBytesEqual(source, decoded);
    }

    /// <summary>
    /// 全 0（全透明黑）必须原样往返。
    /// </summary>
    /// <remarks>
    /// 全 0 在<b>预乘</b>语义下是合法的「全透明」，是合成器最常见的初值。
    /// 如果编解码器在某个条件下把 alpha 误判成「缺失」而补上 255，
    /// 一张全透明的空图层就会变成全黑不透明 —— 肉眼在图层树里未必立刻发现。
    /// </remarks>
    [Fact]
    public void PngCodec_全0像素往返后仍全0()
    {
        const int width = 4;
        const int height = 3;
        byte[] source = Filled(0, width * height * 4);

        (int decodedWidth, int decodedHeight, byte[] decoded) =
            PngCodec.Decode(PngCodec.Encode(width, height, source));

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        AssertBytesEqual(source, decoded);
    }

    /// <summary>全 255（不透明的纯白）必须原样往返，不允许被当成「溢出」截断。</summary>
    [Fact]
    public void PngCodec_全255像素往返后仍全255()
    {
        const int width = 2;
        const int height = 2;
        byte[] source = Filled(255, width * height * 4);

        (int decodedWidth, int decodedHeight, byte[] decoded) =
            PngCodec.Decode(PngCodec.Encode(width, height, source));

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        AssertBytesEqual(source, decoded);
    }

    /// <summary>
    /// <b>alpha = 0 但 RGB 非 0</b> 的像素，往返后颜色必须保留，不能被压成 0。
    /// </summary>
    /// <remarks>
    /// 🔴 这是本文件最重要的一条编解码断言。PNG 标准允许 alpha 为 0 的像素仍携带非零颜色
    /// （这正是 Photoshop 里「图层完全隐藏但仍带颜色」的存法）。
    /// 编码器<b>没有权利</b>擅自把它清零：清零会让往返<b>不可逆</b>，
    /// 于是「同一张图编一次解一次再编一次」得到不同字节，
    /// 黄金样本的基准就不再稳定。<br/>
    /// 同时它顺带证明了 <c>PngCodec</c> 内部<b>不做预乘</b> ——
    /// 若有人在编码前顺手加了一次预乘，alpha=0 的像素会被清成全 0，这条测试立刻红。
    /// </remarks>
    [Fact]
    public void PngCodec_alpha为0但RGB非0_往返必须保留颜色不被压成0()
    {
        const int width = 2;
        const int height = 2;

        byte[] source = Filled(0, width * height * 4);
        for (int p = 0; p < width * height; p++)
        {
            source[p * 4 + 0] = 200;
            source[p * 4 + 1] = 100;
            source[p * 4 + 2] = 50;
            source[p * 4 + 3] = 0;
        }

        (int decodedWidth, int decodedHeight, byte[] decoded) =
            PngCodec.Decode(PngCodec.Encode(width, height, source));

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        AssertBytesEqual(source, decoded);
    }

    /// <summary>
    /// alpha 取<b>中间值</b>（含 127/128/129 这类量化边界）时，往返后逐字节一致。
    /// </summary>
    /// <remarks>
    /// 半透明是黄金样本里最常见的形态，也是预乘 bug 最容易暴露的地方。
    /// 特意塞进 127 / 128 / 129：这几个值在 <c>(c * a + 127) / 255</c> 这种
    /// 「+127 再整除」的四舍五入写法下正好压在大舍入的边界上，
    /// 一旦有人把公式换成截断版本，这里就会差 1。<br/>
    /// 注意期望值是<b>逐字节相等</b>而不是「差 ≤ 1」：<c>PngCodec</c> 不参与预乘，
    /// 所以任何量化都是 bug。
    /// </remarks>
    [Fact]
    public void PngCodec_alpha取中间值_往返后逐字节一致()
    {
        const int width = 3;
        const int height = 2;

        byte[] source = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            source[p * 4 + 0] = (byte)(127 + p);
            source[p * 4 + 1] = (byte)(128 + p);
            source[p * 4 + 2] = (byte)(129 + p);
            source[p * 4 + 3] = (byte)(p % 2 == 0 ? 128 : 127);
        }

        (int decodedWidth, int decodedHeight, byte[] decoded) =
            PngCodec.Decode(PngCodec.Encode(width, height, source));

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        AssertBytesEqual(source, decoded);
    }

    // ───────────────────── 二、PngCodec 输出结构与 CRC ─────────────────────

    /// <summary>
    /// <c>Encode</c> 产出的是<b>合法 PNG</b>：签名 8 字节正确，且能找到
    /// <c>IHDR</c> / <c>IDAT</c> / <c>IEND</c> 三个块标记。
    /// </summary>
    /// <remarks>
    /// 签名错了的话，某些解码器会「宽容地」猜格式，但 macOS 的 CoreGraphics 不会 ——
    /// 而 macOS 正是本项目要互通的那一侧。IHDR 的 13 个字节逐个锁死：
    /// 位深 8、颜色类型 6（RGBA）、非隔行。<b>隔行位必须是 0</b>，
    /// 因为 <c>PngCodec.Decode</c> 见到 <c>interlace != 0</c> 直接抛
    /// <see cref="NotSupportedException"/>：能编码出自己解不了的 PNG 是最坏的一类 bug。
    /// </remarks>
    [Fact]
    public void PngCodec_Encode产出合法Png结构()
    {
        const int width = 3;
        const int height = 2;
        byte[] png = PngCodec.Encode(width, height, DeterministicOpaqueRgba(width, height));

        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        Assert.True(png.Length > signature.Length, $"PNG 至少 {signature.Length} 字节签名，实际只有 {png.Length} 字节。");
        for (int i = 0; i < signature.Length; i++)
        {
            Assert.True(png[i] == signature[i], $"PNG 签名第 {i} 字节应为 {signature[i]}，实际 {png[i]}。");
        }

        // 布局：0-7 签名 | 8-11 长度(13) | 12-15 类型 | 16-28 IHDR 数据 | 29-32 CRC
        const int lengthOffset = 8;
        const int typeOffset = 12;
        const int dataOffset = 16;

        Assert.Equal(13u, ReadBigEndianUInt32(png, lengthOffset));
        AssertChunkTypeAt(png, typeOffset, "IHDR");

        Assert.Equal((uint)width, ReadBigEndianUInt32(png, dataOffset));
        Assert.Equal((uint)height, ReadBigEndianUInt32(png, dataOffset + 4));
        Assert.Equal(8, png[dataOffset + 8]);  // 位深：仅支持 8
        Assert.Equal(6, png[dataOffset + 9]);  // 颜色类型 6 = 真彩色 + alpha
        Assert.Equal(0, png[dataOffset + 10]); // 压缩方法：0 = deflate
        Assert.Equal(0, png[dataOffset + 11]); // 滤波方法：0 = adaptive
        Assert.Equal(0, png[dataOffset + 12]); // 隔行：0 = 非隔行

        Assert.True(ContainsSequence(png, Encoding.ASCII.GetBytes("IHDR")), "PNG 里找不到 IHDR 块标记。");
        Assert.True(ContainsSequence(png, Encoding.ASCII.GetBytes("IDAT")), "PNG 里找不到 IDAT 块标记。");
        Assert.True(ContainsSequence(png, Encoding.ASCII.GetBytes("IEND")), "PNG 里找不到 IEND 块标记。");
    }

    /// <summary>
    /// <c>IHDR</c> 块的 CRC32 必须与<b>测试里独立实现</b>的重算值一致。
    /// </summary>
    /// <remarks>
    /// <b>用的是哪条公式</b>：PNG（与 zlib / gzip / 以太网同源）用的是
    /// <b>CRC-32/ISO-HDLC</b>，即<b>反射式</b>实现：多项式 <c>0xEDB88320</c>，
    /// 初值 <c>0xFFFFFFFF</c>，逐字节 <c>crc ^= byte</c> 后做 8 次
    /// 「最低位为 1 则 <c>(crc &gt;&gt; 1) ^ 0xEDB88320</c>，否则 <c>crc &gt;&gt; 1</c>」，
    /// 收尾再异或 <c>0xFFFFFFFF</c>。PNG 规定 CRC 覆盖
    /// <b>块类型 4 字节 + 块数据</b>，<b>不含</b>长度字段。
    /// <para><b>为什么必须独立重算</b>：<c>PngCodec</c> 是 internal 且没有暴露任何 CRC 函数，
    /// 所以「文件里写的那 4 字节对不对」只有一个验证办法 —— 在测试里按格式规范另算一遍。
    /// CRC 写错的话文件会被 CoreGraphics 判为损坏，而 macOS 恰恰是要互通的那一侧。
    /// 又因为 <c>PngCodec.Decode</c> <b>从不校验</b> CRC，编码侧写错在解码侧<b>完全测不出来</b>，
    /// 这里就是唯一的拦截点。</para>
    /// <para><b>怎样保证「独立」是真的独立</b>：本测试的实现是<b>逐位</b>的（不建 256 项查表），
    /// 与 <c>PngCodec</c> 的查表法在算法路径上不同 ——
    /// 查表法写错表 和 逐位法写错多项式/初值 不会同时发生。
    /// 另外先用标准校验值 <c>"123456789" → 0xCBF43926</c> 自证本实现本身是对的。</para>
    /// </remarks>
    [Fact]
    public void PngCodec_IHDR块的CRC32与独立重算一致()
    {
        // 先证明「本测试自己的 CRC 实现」是对的，否则「独立」二字无从谈起。
        Assert.Equal(0xCBF43926u, IndependentCrc32(Encoding.ASCII.GetBytes("123456789")));

        const int width = 5;
        const int height = 4;
        byte[] png = PngCodec.Encode(width, height, DeterministicOpaqueRgba(width, height));

        const int typeOffset = 12;
        const int dataOffset = 16;
        const int ihdrLength = 13;
        const int crcOffset = dataOffset + ihdrLength;

        Assert.Equal((uint)ihdrLength, ReadBigEndianUInt32(png, 8));
        AssertChunkTypeAt(png, typeOffset, "IHDR");
        Assert.Equal((uint)width, ReadBigEndianUInt32(png, dataOffset));
        Assert.Equal((uint)height, ReadBigEndianUInt32(png, dataOffset + 4));

        // CRC 覆盖「类型 + 数据」，起点是类型字节、长度 4 + 13，<b>不含</b>长度字段
        uint recomputed = IndependentCrc32(png.AsSpan(typeOffset, 4 + ihdrLength));
        uint stored = ReadBigEndianUInt32(png, crcOffset);

        Assert.Equal(recomputed, stored);
    }

    // ───────────────────── 三、PngCodec 参数校验 ─────────────────────

    /// <summary>非 PNG 字节必须抛 <see cref="InvalidDataException"/>，而不是返回一堆垃圾。</summary>
    /// <remarks>
    /// 基准文件被截断、被 Git LFS 指针替换、或路径指到了 <c>.comp</c> 工程文件时都会走到这里。
    /// 若这里悄悄解出错误尺寸，后面所有比对结果都会变成无法解释的假失败。
    /// </remarks>
    [Fact]
    public void PngCodec_Decode非PNG字节抛InvalidDataException()
        => Assert.Throws<InvalidDataException>(() => PngCodec.Decode(new byte[] { 1, 2, 3, 4 }));

    /// <summary><c>null</c> 必须抛 <see cref="ArgumentNullException"/>，不能是空引用异常。</summary>
    /// <remarks>
    /// 空引用异常（<see cref="NullReferenceException"/>）不带参数名，排查时看不出是哪个入参出的问题；
    /// 契约层统一抛 <see cref="ArgumentNullException"/> 才能让错误信息可定位。
    /// </remarks>
    [Fact]
    public void PngCodec_Decode_null抛ArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => PngCodec.Decode(null!));

    /// <summary>
    /// <c>rgba</c> 长度不足时必须抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <remarks>
    /// 2×2 需要 16 字节，只给 4 字节。若不拦，<c>Buffer.BlockCopy</c> 会抛一个
    /// 带偏移量的底层异常，或更糟 —— 读到数组外面去写坏内存。
    /// 这里同时锁住异常类型（必须是 <see cref="ArgumentException"/>，不是它派生出的
    /// <see cref="ArgumentOutOfRangeException"/>）与参数名，让调用方能定位。
    /// </remarks>
    [Fact]
    public void PngCodec_Encode数据不足抛ArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => PngCodec.Encode(2, 2, new byte[4]));
        Assert.Equal("rgba", ex.ParamName);

        // 同一道守卫的另一半：null 输入
        Assert.Throws<ArgumentNullException>(() => PngCodec.Encode(1, 1, null!));
    }

    // ───────────────────── 四、Golden 比对 ─────────────────────

    /// <summary>
    /// 完全一致：<c>Passed == true</c>、零差异像素、差异图为<b>空字符串</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>为什么 actual 一律用 alpha = 255 构造</b>：
    /// <see cref="Golden.CompareRgba"/> 会先把 <c>actual</c> <b>反预乘</b>，
    /// 再与 PNG（直通 alpha）比较；而 <see cref="PixelBuffer"/> 内部是预乘的。
    /// 只有 <c>alpha == 255</c> 时「预乘 → 反预乘」才是<b>精确恒等</b>：
    /// <c>Premultiply(c, 255) == (c * 255 + 127) / 255 == c</c> 且
    /// <c>Unpremultiply(c, 255) == (c * 255 + 127) / 255 == c</c>
    /// （余数 127 &lt; 255，整除向零截断）。
    /// 于是「构造时写入的字节」与「比对时读到的字节」逐字节相同，
    /// <c>DifferingPixels == 0</c> 才是一个<b>可证明</b>的期望值，不掺任何舍入。<br/>
    /// 若改用半透明 alpha，同样的输入会因预乘往返的量化而天然差 1，
    /// 测试就变成在测「舍入是否稳定」，而不是在测比对逻辑 —— 那属于
    /// <c>PixelBuffer</c> 的测试，不是 golden 的。
    /// </remarks>
    [Fact]
    public void Golden_完全一致时通过且不产出差异图()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int width = 3;
        const int height = 2;
        byte[] source = DeterministicOpaqueRgba(width, height);
        string goldenPath = fixture.WriteGolden(source, width, height);

        PixelBuffer actual = PixelBuffer.FromStraightRgba(source, width, height);

        GoldenResult result = Golden.CompareRgba(goldenPath, actual);

        Assert.True(result.Passed, "像素逐字节相同，必须通过。");
        Assert.Equal(0, result.DifferingPixels);
        Assert.Equal(0, result.MaxDelta);
        Assert.Equal(string.Empty, result.DiffImagePath);
    }

    /// <summary>
    /// 每通道差 1：<c>tolerance = 1</c> 通过、<c>tolerance = 0</c> 失败。
    /// </summary>
    /// <remarks>
    /// 为什么这两件事必须一起断言：容差判定的语义是「<b>差 &gt; 容差</b>才算差异」，
    /// 所以差 1 在容差 1 下<b>恰好</b>通过。判据写成 <c>&gt;=</c> 的话，
    /// <see cref="Golden"/> 定容差 1 的意义（吸收 libm 跨平台 1 个量化步）就整个失效，
    /// 所有跨平台黄金样本都会长期飘红。<br/>
    /// 🔴 顺带锁住一个极容易读错的事实：<b>通过时 <c>MaxDelta</c> 仍为 1</b>。
    /// <c>MaxDelta</c> 是全图所有通道的最大绝对差，<b>与容差无关</b>；
    /// 「计入 MaxDelta 统计」和「计为一个差异像素」是两件独立的事。
    /// </remarks>
    [Fact]
    public void Golden_通道差1_容差1通过而容差0失败()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int width = 2;
        const int height = 2;
        string goldenPath = fixture.WriteGolden(SolidOpaque(width, height, 100, 110, 120), width, height);

        // R/G/B 各差 1、alpha 差 0 → MaxDelta 必为 1
        PixelBuffer actual =
            PixelBuffer.FromStraightRgba(SolidOpaque(width, height, 101, 111, 121), width, height);

        GoldenResult tolerant = Golden.CompareRgba(goldenPath, actual, 1);
        Assert.True(tolerant.Passed, "通道差 1 恰好等于容差 1，判定标准是「差 > 容差」，应当通过。");
        Assert.Equal(0, tolerant.DifferingPixels);
        Assert.Equal(string.Empty, tolerant.DiffImagePath);
        Assert.Equal(1, tolerant.MaxDelta);

        GoldenResult strict = Golden.CompareRgba(goldenPath, actual, 0);
        Assert.False(strict.Passed, "容差 0 下差 1 必须失败，否则容差形同虚设。");
        Assert.Equal(width * height, strict.DifferingPixels);
        Assert.Equal(1, strict.MaxDelta);
        Assert.NotEqual(string.Empty, strict.DiffImagePath);
        Assert.True(File.Exists(strict.DiffImagePath), $"差异图应已写出：{strict.DiffImagePath}");

        // 差异图必须本身是合法 PNG，且每个差异像素被染成洋红（alpha 拉满，保证灰底上可见）
        (int diffWidth, int diffHeight, byte[] diff) =
            PngCodec.Decode(File.ReadAllBytes(strict.DiffImagePath));
        Assert.Equal(width, diffWidth);
        Assert.Equal(height, diffHeight);
        for (int p = 0; p < width * height; p++)
        {
            AssertPixel(diff, p, 255, 0, 255, 255);
        }
    }

    /// <summary>
    /// 每个通道都差 50：全部像素判为差异，<c>MaxDelta == 50</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>为什么基准的 alpha 取 205 而 actual 仍是不透明的 255</b>：
    /// <c>PngCodec</c> 不做预乘，所以基准 PNG 可以是任意 alpha 的直通像素；
    /// 而 <c>actual</c> 必须是 alpha = 255 的不透明缓冲，预乘往返才是恒等变换、
    /// 期望值才可推导（理由见「完全一致」那条测试的说明）。
    /// 于是 R/G/B 各差 50，alpha 差 <c>|205 - 255| = 50</c> ——
    /// <b>四个通道都恰好差 50</b>，<c>MaxDelta == 50</c> 才是个无歧义的期望值。<br/>
    /// <c>DifferingPixels</c> 必等于总像素数：容差是默认的 1，任一通道差 50 都超。
    /// </remarks>
    [Fact]
    public void Golden_每通道差50_超容差时全部像素判为差异()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int width = 2;
        const int height = 2;
        string goldenPath = fixture.WriteGolden(Solid(width, height, 100, 100, 100, 205), width, height);

        PixelBuffer actual =
            PixelBuffer.FromStraightRgba(Solid(width, height, 150, 150, 150, 255), width, height);

        GoldenResult result = Golden.CompareRgba(goldenPath, actual);

        Assert.False(result.Passed, "每通道差 50 远超默认容差 1，必须失败。");
        Assert.Equal(width * height, result.DifferingPixels);
        Assert.Equal(50, result.MaxDelta);
        Assert.NotEqual(string.Empty, result.DiffImagePath);
        Assert.True(File.Exists(result.DiffImagePath), $"差异图应已写出：{result.DiffImagePath}");
    }

    /// <summary>
    /// 局部差异：只有 1 个像素超容差时 <c>DifferingPixels == 1</c>。
    /// </summary>
    /// <remarks>
    /// 为什么这个用例是「局部」的关键：容差是<b>每通道</b>的，
    /// 所以一个像素只要<b>任一</b>通道超了就整像素计一次 —— 而不是逐通道各计一次。
    /// 用例里三个像素分别代表三种边界：
    /// <list type="bullet">
    /// <item>像素 0：差值<b>恰好等于</b>容差 10 → 不算差异（判据是 <c>&gt;</c>）；</item>
    /// <item>像素 1：差 20 &gt; 10 → 算 1 个差异像素，并被染成洋红；</item>
    /// <item>像素 2、3：完全相同。</item>
    /// </list>
    /// 期望 <c>DifferingPixels == 1</c>，<c>MaxDelta == 20</c>。
    /// 差异图上再逐像素验一遍：像素 0 保留自己的颜色（证明「未超差就不染色」），
    /// 像素 1 是洋红，两者一比就把「统计口径」和「染色口径」是否一致钉死了。
    /// </remarks>
    [Fact]
    public void Golden_局部超容差时只统计超差的像素()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int width = 2;
        const int height = 2;
        const int tolerance = 10;

        string goldenPath = fixture.WriteGolden(SolidOpaque(width, height, 100, 110, 120), width, height);

        byte[] actualBytes = SolidOpaque(width, height, 100, 110, 120);
        actualBytes[0] = 110;
        actualBytes[1] = 120;
        actualBytes[2] = 130; // 像素 0：每通道 +10，恰等于容差
        actualBytes[4] = 120;
        actualBytes[5] = 130;
        actualBytes[6] = 140; // 像素 1：每通道 +20，超容差
        PixelBuffer actual = PixelBuffer.FromStraightRgba(actualBytes, width, height);

        GoldenResult result = Golden.CompareRgba(goldenPath, actual, tolerance);

        Assert.False(result.Passed, "像素 1 超容差，必须失败。");
        Assert.Equal(1, result.DifferingPixels);
        Assert.Equal(20, result.MaxDelta);

        Assert.NotEqual(string.Empty, result.DiffImagePath);
        Assert.True(File.Exists(result.DiffImagePath), $"差异图应已写出：{result.DiffImagePath}");

        (int diffWidth, int diffHeight, byte[] diff) =
            PngCodec.Decode(File.ReadAllBytes(result.DiffImagePath));
        Assert.Equal(width, diffWidth);
        Assert.Equal(height, diffHeight);

        AssertPixel(diff, 0, 110, 120, 130, 255);   // 恰等于容差 → 保留自己的颜色，不染色
        AssertPixel(diff, 1, 255, 0, 255, 255);     // 超容差 → 染成洋红
        AssertPixel(diff, 2, 100, 110, 120, 255);   // 完全相同
        AssertPixel(diff, 3, 100, 110, 120, 255);
    }

    /// <summary>
    /// 尺寸不一致（基准 2×2、实际 3×3）直接判失败：<c>MaxDelta == 255</c>、
    /// <c>DifferingPixels</c> 等于实际像素总数。
    /// </summary>
    /// <remarks>
    /// 尺寸不一致时逐像素比对<b>无从谈起</b>，所以实现短路返回，
    /// 并用 <c>MaxDelta = 255</c> 这个不可能来自真实像素误差的值
    /// 让报告一眼看出「这是尺寸问题，不是精度问题」。
    /// 文件名里的 <c>-size-2x2-vs-3x3</c> 同理：CI 日志上只看路径就能定位。<br/>
    /// 注意实现写出的是<b>实际图本身</b>（未染色，因为没有可逐像素比较的对象）。
    /// 这里按<b>现状</b>断言并注释清楚：人看到这张「差异图」时要知道它其实只是 actual。
    /// </remarks>
    [Fact]
    public void Golden_尺寸不一致时判失败并把尺寸写进文件名()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int goldenWidth = 2;
        const int goldenHeight = 2;
        string goldenPath =
            fixture.WriteGolden(SolidOpaque(goldenWidth, goldenHeight, 50, 60, 70), goldenWidth, goldenHeight);

        const int actualWidth = 3;
        const int actualHeight = 3;
        PixelBuffer actual = PixelBuffer.FromStraightRgba(
            SolidOpaque(actualWidth, actualHeight, 200, 100, 50), actualWidth, actualHeight);

        GoldenResult result = Golden.CompareRgba(goldenPath, actual);

        Assert.False(result.Passed, "尺寸不一致必须失败。");
        Assert.Equal(255, result.MaxDelta);
        Assert.Equal(actualWidth * actualHeight, result.DifferingPixels);
        Assert.NotEqual(string.Empty, result.DiffImagePath);
        Assert.Contains("-size-2x2-vs-3x3", result.DiffImagePath);
        Assert.True(File.Exists(result.DiffImagePath), $"差异图应已写出：{result.DiffImagePath}");

        (int diffWidth, int diffHeight, byte[] diff) =
            PngCodec.Decode(File.ReadAllBytes(result.DiffImagePath));
        Assert.Equal(actualWidth, diffWidth);
        Assert.Equal(actualHeight, diffHeight);

        // 尺寸不一致时逐像素差异本来就无从计算，所以差异图画成整片洋红，
        // 而不是 actual 原图 —— 后者会让人以为"没有差异"。
        // 判定标准：每个字节都是 (255, 0, 255, 255)。
        for (int p = 0; p < actualWidth * actualHeight; p++)
        {
            Assert.Equal(255, diff[p * 4]);
            Assert.Equal(0, diff[p * 4 + 1]);
            Assert.Equal(255, diff[p * 4 + 2]);
            Assert.Equal(255, diff[p * 4 + 3]);
        }
    }

    /// <summary>基准文件不存在时抛 <see cref="FileNotFoundException"/>，并带上找不到的路径。</summary>
    /// <remarks>
    /// 样本漏拷、路径拼错、CI 上没 checkout fixtures，都会走到这里。
    /// 必须抛「文件不存在」而不是 <see cref="InvalidDataException"/>：
    /// 两者的排查方向完全不同（前者查文件，后者查格式），
    /// 混用会让人在「文件损坏」上白查半天。
    /// </remarks>
    [Fact]
    public void Golden_基准文件不存在抛FileNotFoundException()
    {
        using GoldenFixture fixture = new GoldenFixture();

        string missing = Path.Combine(fixture.TempDirectory, "never-written.png");
        PixelBuffer actual = PixelBuffer.Create(2, 2);

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(
            () => Golden.CompareRgba(missing, actual));

        Assert.Equal(missing, ex.FileName);
    }

    /// <summary>
    /// 容差为负时抛 <see cref="ArgumentOutOfRangeException"/>，参数名为 <c>tolerance</c>。
    /// </summary>
    /// <remarks>
    /// 基准文件存在、缓冲也合法，<b>唯一非法的入参就是容差</b> ——
    /// 这样才能确定异常确实来自容差校验，而不是被别的前置检查抢先。
    /// 参数名必须锁：它直接出现在断言失败信息里。
    /// </remarks>
    [Fact]
    public void Golden_负容差抛ArgumentOutOfRangeException()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int width = 2;
        const int height = 2;
        string goldenPath = fixture.WriteGolden(SolidOpaque(width, height, 10, 20, 30), width, height);
        PixelBuffer actual = PixelBuffer.Create(width, height);

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Golden.CompareRgba(goldenPath, actual, -1));

        Assert.Equal("tolerance", ex.ParamName);
    }

    /// <summary>
    /// <c>CompareProject</c> 等价于按<b>约定文件名</b>调用 <c>CompareRgba</c>。
    /// </summary>
    /// <remarks>
    /// 这里比的是<b>整个 record</b>（含 <c>DiffImagePath</c>），所以连
    /// 「差异图名由基准文件名派生」这条约定也一起锁住：
    /// 两个入口产出的文件名必须一模一样，否则 CI 收集差异图会漏。<br/>
    /// 故意用一个<b>会失败</b>的输入（蓝通道差 30）：失败时 <c>DiffImagePath</c> 非空，
    /// 才有内容可比；通过时两边都是空字符串，测不出差异图命名。
    /// 同时把期望值（4 个差异像素、MaxDelta 30）显式断言一遍，
    /// 防止「两个入口一起错」导致自洽却错误。
    /// </remarks>
    [Fact]
    public void Golden_CompareProject等价于按约定文件名调用CompareRgba()
    {
        using GoldenFixture fixture = new GoldenFixture();

        const int width = 2;
        const int height = 2;
        fixture.WriteGoldenAs(Golden.ProjectGoldenFileName, SolidOpaque(width, height, 10, 20, 30), width, height);

        PixelBuffer actual =
            PixelBuffer.FromStraightRgba(SolidOpaque(width, height, 10, 20, 60), width, height);

        GoldenResult viaProject = Golden.CompareProject(fixture.TempDirectory, actual);
        GoldenResult viaRgba = Golden.CompareRgba(
            Path.Combine(fixture.TempDirectory, Golden.ProjectGoldenFileName), actual);

        Assert.Equal(viaRgba, viaProject);

        Assert.False(viaProject.Passed, "蓝通道差 30，超出默认容差，必须失败。");
        Assert.Equal(width * height, viaProject.DifferingPixels);
        Assert.Equal(30, viaProject.MaxDelta);
        Assert.NotEqual(string.Empty, viaProject.DiffImagePath);
    }

    // ───────────────────── 五、契约类型本身 ─────────────────────

    /// <summary><c>[GoldenSample]</c> 只携带标识，不改变任何行为。</summary>
    /// <remarks>
    /// 真正的比对是测试显式调 <see cref="Golden.CompareRgba"/> 完成的，
    /// 特性本身只提供全项目唯一的样本 id 供工具收集归档。
    /// 所以它<b>必须</b>是纯数据容器：一旦它偷偷触发比对，
    /// 「哪些测试用了黄金样本」就会和「哪些测试真的比了」脱节。
    /// </remarks>
    [Fact]
    public void GoldenSampleAttribute_只携带Id()
    {
        GoldenSampleAttribute attribute = new GoldenSampleAttribute("abc");

        Assert.Equal("abc", attribute.Id);
        Assert.IsAssignableFrom<Attribute>(attribute);
    }

    /// <summary>
    /// <see cref="GoldenResult"/> 是 record：四个字段相等时按<b>值</b>相等，
    /// 且四个字段都参与相等判定。
    /// </summary>
    /// <remarks>
    /// 为什么这条重要：黄金样本的结论会被测试汇总、报告、写进验收记录。
    /// 若它是普通 class（引用相等），汇总代码只能写成
    /// <c>new GoldenResult(p, 0, 0, "")</c> 这种手工重建才能比较，
    /// 极易写出「只比了 Passed、漏比 MaxDelta」的汇总 bug。
    /// 四个字段逐个构造变体，证明没有哪个字段被排除在相等判定之外。
    /// </remarks>
    [Fact]
    public void GoldenResult_是record_四字段相等时按值相等()
    {
        GoldenResult first = new GoldenResult(true, 0, 0, string.Empty);
        GoldenResult second = new GoldenResult(true, 0, 0, string.Empty);

        Assert.True(first.Passed, "构造时传入的 Passed 应可读回。");
        Assert.Equal(0, first.DifferingPixels);
        Assert.Equal(0, first.MaxDelta);
        Assert.Equal(string.Empty, first.DiffImagePath);

        Assert.True(first == second, "record 的 == 必须按字段值比较，而不是引用。");
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        Assert.True(first != new GoldenResult(false, 0, 0, string.Empty), "Passed 不同则不相等。");
        Assert.True(first != new GoldenResult(true, 1, 0, string.Empty), "DifferingPixels 不同则不相等。");
        Assert.True(first != new GoldenResult(true, 0, 7, string.Empty), "MaxDelta 不同则不相等。");
        Assert.True(first != new GoldenResult(true, 0, 0, "diff.png"), "DiffImagePath 不同则不相等。");
    }

    // ───────────────────── 六、夹具与工具 ─────────────────────

    /// <summary>
    /// 临时目录 + 差异图清理的夹具。<c>using</c> 声明展开为 try/finally，
    /// 所以<b>即使断言失败也一定会清理</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>为什么基准文件不放项目目录</b>：<see cref="Golden"/> 的差异图路径是
    /// <c>test-artifacts/…</c>（<b>相对当前工作目录</b>），基准文件若也写进项目目录，
    /// 测试一失败就会在仓库里留一堆 PNG。基准文件一律落在
    /// <c>Path.GetTempPath()/compositor-golden-tests/&lt;guid&gt;</c>。<br/>
    /// <b>为什么只按前缀删自己的文件</b>：差异图文件名由基准文件名派生
    /// （<c>{stem}-diff.png</c> 或 <c>{stem}-size-…png</c>），
    /// 所以按 <c>{stem}-*</c> 前缀删既能覆盖两个分支，又不会误删
    /// 并行执行的其他测试类的产物。目录只在<b>真的空了</b>时才回收。
    /// </remarks>
    private sealed class GoldenFixture : IDisposable
    {
        private const string TempRootName = "compositor-golden-tests";
        private const string ArtifactSubDirectory = "golden";

        private readonly List<string> _goldenPaths = new List<string>();
        private readonly string _stem;

        public GoldenFixture()
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), TempRootName, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            _stem = "golden-" + Guid.NewGuid().ToString("N");
        }

        /// <summary>本次测试专属的临时目录（绝对路径，位于系统临时目录下）。</summary>
        public string TempDirectory { get; }

        /// <summary>写一份基准 PNG，返回其绝对路径。</summary>
        public string WriteGolden(byte[] straightRgba, int width, int height)
            => WriteGoldenAs(_stem + ".png", straightRgba, width, height);

        /// <summary>
        /// 以指定文件名写一份基准 PNG。
        /// <see cref="Golden.CompareProject"/> 走的是<b>约定文件名</b>，所以要显式传。
        /// </summary>
        public string WriteGoldenAs(string fileName, byte[] straightRgba, int width, int height)
        {
            string path = Path.Combine(TempDirectory, fileName);
            File.WriteAllBytes(path, PngCodec.Encode(width, height, straightRgba));
            _goldenPaths.Add(path);
            return path;
        }

        /// <summary>删掉本次测试产出的差异图与临时目录。</summary>
        public void Dispose()
        {
            string artifactDirectory = Path.Combine(
                Environment.CurrentDirectory, Golden.ArtifactDirectory, ArtifactSubDirectory);

            if (Directory.Exists(artifactDirectory))
            {
                foreach (string goldenPath in _goldenPaths)
                {
                    // 基准路径一定非空且带扩展名，所以去扩展名后的 stem 也非空；
                    // `!` 只是压掉 Path API 的可空返回标注。
                    DeleteArtifactsFor(Path.GetFileNameWithoutExtension(goldenPath)!, artifactDirectory);
                }

                PruneIfEmpty(artifactDirectory);
            }

            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }

        private static void DeleteArtifactsFor(string stem, string artifactDirectory)
        {
            foreach (string file in Directory.GetFiles(artifactDirectory, stem + "-*"))
            {
                File.Delete(file);
            }
        }

        private static void PruneIfEmpty(string directory)
        {
            if (Directory.GetFileSystemEntries(directory).Length != 0)
            {
                return;
            }

            Directory.Delete(directory);

            string? parent = Path.GetDirectoryName(directory);
            if (parent is not null && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
            {
                Directory.Delete(parent);
            }
        }
    }

    /// <summary>确定性图案：每个字节都由像素下标推出，不含随机数，期望值可人工复核。</summary>
    /// <remarks>
    /// 全图 alpha 固定 255，于是 <c>FromStraightRgba → ToStraightRgba</c> 是恒等变换，
    /// 可以用它构造「精确可推导」的 <c>actual</c>（见「完全一致」那条测试的说明）。
    /// </remarks>
    private static byte[] DeterministicOpaqueRgba(int width, int height)
    {
        var data = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            data[p * 4 + 0] = (byte)((p * 7 + 3) % 256);
            data[p * 4 + 1] = (byte)((p * 13 + 101) % 256);
            data[p * 4 + 2] = (byte)((p * 29 + 197) % 256);
            data[p * 4 + 3] = 255;
        }

        return data;
    }

    /// <summary>整幅同一个直通颜色，alpha 固定 255。</summary>
    private static byte[] SolidOpaque(int width, int height, byte r, byte g, byte b)
        => Solid(width, height, r, g, b, 255);

    /// <summary>整幅同一个直通颜色，alpha 由调用方给（基准 PNG 可以是半透明的直通像素）。</summary>
    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var data = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            data[p * 4 + 0] = r;
            data[p * 4 + 1] = g;
            data[p * 4 + 2] = b;
            data[p * 4 + 3] = a;
        }

        return data;
    }

    /// <summary>整幅同一个字节值。</summary>
    private static byte[] Filled(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    /// <summary>逐字节比对，不一致时直接报出<b>第一个</b>不同的下标。</summary>
    private static void AssertBytesEqual(byte[] expected, byte[] actual)
    {
        Assert.True(expected.Length == actual.Length,
            $"长度不一致：期望 {expected.Length} 字节，实际 {actual.Length} 字节。");

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == actual[i], $"第 {i} 字节不一致：期望 {expected[i]}，实际 {actual[i]}。");
        }
    }

    /// <summary>逐通道断言第 <paramref name="pixelIndex"/> 个像素（行优先）。</summary>
    private static void AssertPixel(byte[] rgba, int pixelIndex, byte r, byte g, byte b, byte a)
    {
        int i = pixelIndex * 4;
        Assert.True(rgba[i] == r, $"像素 {pixelIndex} 的 R 应为 {r}，实际 {rgba[i]}。");
        Assert.True(rgba[i + 1] == g, $"像素 {pixelIndex} 的 G 应为 {g}，实际 {rgba[i + 1]}。");
        Assert.True(rgba[i + 2] == b, $"像素 {pixelIndex} 的 B 应为 {b}，实际 {rgba[i + 2]}。");
        Assert.True(rgba[i + 3] == a, $"像素 {pixelIndex} 的 A 应为 {a}，实际 {rgba[i + 3]}。");
    }

    /// <summary>断言 <paramref name="offset"/> 处的 4 字节是 PNG 块类型。</summary>
    private static void AssertChunkTypeAt(byte[] png, int offset, string expectedType)
    {
        byte[] expected = Encoding.ASCII.GetBytes(expectedType);
        Assert.True(expected.Length == 4, "PNG 块类型固定 4 字节 ASCII。");

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(png[offset + i] == expected[i],
                $"偏移 {offset + i} 应为块类型 \"{expectedType}\" 的第 {i} 字节 {expected[i]}，实际 {png[offset + i]}。");
        }
    }

    /// <summary>按 PNG 规范读 4 字节大端无符号整数（长度字段、尺寸、CRC 都用它）。</summary>
    private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
        => (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);

    /// <summary>
    /// 测试内<b>独立实现</b>的 CRC-32/ISO-HDLC（反射式，多项式 <c>0xEDB88320</c>，
    /// 初值 <c>0xFFFFFFFF</c>，收尾异或 <c>0xFFFFFFFF</c>）。
    /// </summary>
    /// <remarks>
    /// 逐位实现（不建 256 项查表），与 <c>PngCodec</c> 的查表法在算法路径上独立。
    /// 见「<c>IHDR</c> 块的 CRC32」那条测试的说明。
    /// </remarks>
    private static uint IndependentCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>朴素字节序列搜索（避免依赖具体 Span API 的重载解析）。</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            bool hit = true;
            for (int i = 0; i < needle.Length; i++)
            {
                if (haystack[start + i] != needle[i])
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }
}
