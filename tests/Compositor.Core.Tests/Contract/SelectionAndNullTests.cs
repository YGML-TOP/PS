using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests.Contract;

/// <summary>
/// 🔴 <b>覆盖度 / 选区 / Null 空实现 / 色彩转换</b>的契约测试。
/// </summary>
/// <remarks>
/// <para><b>覆盖度与 alpha 的区别（本文件的第一条铁律）：</b>
/// <see cref="Coverage8"/> 是<b>单通道灰度</b>，0 = 完全隐藏、255 = 完全显示，
/// 它描述"这个位置参与计算的比重"，与该位置有多不透明<b>无关</b>。
/// 铁律 2 之所以特意禁止蒙版带 alpha，就是因为 RGBA 的 alpha 两次相乘会把图层边缘压成全黑。
/// <see cref="SelectionMask.Empty"/> / <see cref="SelectionMask.Full"/> 都用
/// <see cref="Coverage8.Uniform"/> 的 1×1 代理表达，正是因为"满选区"是最常见的初始状态，
/// 为它分配 4K 缓冲纯属浪费。</para>
///
/// <para><b>Null 实现不是占位符，是并行度的前提：</b>AI-3~AI-6 要在真实合成器完工前
/// 用 <see cref="NullCanvas"/> / <see cref="NullProjectStore"/> 跑自己的单元测试。
/// 所以它们的每一条行为都必须是<b>可断言的确定值</b>：不抛、不阻塞、不写盘。
/// 本文件把这些默认值全部钉死，让任何"顺手加个真实行为"的改动当场变红。</para>
///
/// <para><b>期望值来源：全部手算。</b><see cref="ColorConvert"/> 的期望值按原项目
/// <c>Rendering/AdjustPixels.c:204-214</c> 的常数（拐点 0.04045 / 0.0031308、Gamma 2.4、
/// 除数 12.92）推出并在注释里写出算式；选区与覆盖度的期望值按
/// <c>DocRect.Contains</c> 的半开区间判据 <c>Left ≤ x &lt; Right</c> 推出。
/// 没有一条是从实现跑出来的输出。</para>
/// </remarks>
public sealed class SelectionAndNullTests
{
    // ─────────────────── Coverage8：单通道灰度，1×1 代理 ───────────────────

    /// <summary>
    /// <see cref="Coverage8.Uniform"/> 必须是 1×1、<see cref="Coverage8.Data"/> 长度为 1、
    /// 且唯一的字节就是传入值（钳到 0..255）。
    /// <para>钳制的算式：<c>(byte)Math.Clamp(value, 0, 255)</c>，所以
    /// 300 → 255、256 → 255、int.MaxValue → 255、−5 → 0。
    /// 这条不能省：覆盖度常量大量来自 UI 里的滑块与外部文件，
    /// 一个负值或溢出的输入若没被钳住，<c>(byte)</c> 强转会静默回绕
    /// （例如 −5 变 251 = 几乎完全不透明），表现为"选区莫名其妙全选上了"。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(128, 128)]
    [InlineData(254, 254)]
    [InlineData(255, 255)]
    [InlineData(256, 255)]
    [InlineData(300, 255)]
    [InlineData(int.MaxValue, 255)]
    [InlineData(-1, 0)]
    [InlineData(-5, 0)]
    [InlineData(int.MinValue, 0)]
    public void Uniform_是1乘1代理_并把取值钳到0到255(int value, int expected)
    {
        Coverage8 uniform = Coverage8.Uniform(value);

        Assert.Equal(1, uniform.Width);
        Assert.Equal(1, uniform.Height);
        Assert.Equal(1, uniform.Data.Length);
        Assert.Equal(expected, uniform.Data[0]);
    }

    /// <summary>
    /// <see cref="Coverage8.CreateFilled"/> 的长度必须是 <c>w * h</c>（行优先、无对齐填充），
    /// 且<b>每一个字节</b>都等于填充值。
    /// <para>取 4×3 = 12、7×5 = 35、2×2 = 4 做锚点。这里刻意用 7 和 5 这种
    /// 非 4/8/16 倍数：如果实现为了 SIMD 加了行对齐，长度会变成按行向上取整后的值，
    /// 而"每字节相等"的断言配合精确长度能把这种情况抓出来。</para>
    /// </summary>
    [Theory]
    [InlineData(1, 1, 0, 1)]
    [InlineData(2, 2, 1, 4)]
    [InlineData(4, 3, 200, 12)]
    [InlineData(7, 5, 255, 35)]
    public void CreateFilled_长度是宽乘高且每个字节都被填上(int w, int h, int value, int expectedLength)
    {
        Coverage8 coverage = Coverage8.CreateFilled(w, h, (byte)value);

        Assert.Equal(w, coverage.Width);
        Assert.Equal(h, coverage.Height);
        Assert.Equal(expectedLength, coverage.Data.Length);
        Assert.Equal(w * h, coverage.Data.Length);
        Assert.All(coverage.Data.ToArray(), b => Assert.Equal(value, b));
    }

    /// <summary>
    /// 宽或高不为正必须抛 <see cref="ArgumentOutOfRangeException"/>，并由 <c>ParamName</c>
    /// 指明是宽度还是高度。
    /// <para>注意本类型的参数名是 <c>w</c> / <c>h</c>（源码签名如此），
    /// 与 <c>PixelBuffer</c> 的 <c>width</c> / <c>height</c> <b>并不一致</b>。
    /// 本测试按源码逐字锁定，而不是"顺手统一"，因为改参数名属于契约层变更，
    /// 得由 AI-1 走评审，不该由一条测试默默改掉。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 3, "w")]
    [InlineData(-1, 3, "w")]
    [InlineData(4, 0, "h")]
    [InlineData(4, -1, "h")]
    public void CreateFilled_宽或高非正_抛ArgumentOutOfRangeException(int w, int h, string paramName)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => Coverage8.CreateFilled(w, h, 128));

        Assert.Equal(paramName, ex.ParamName);
    }

    /// <summary>
    /// 总字节数溢出 <see cref="int"/> 时必须拒绝，且<b>必须在分配之前</b>拒绝。
    /// <para>算式：46341 × 46341 = 2,147,488,281 &gt; int.MaxValue (2,147,483,647)，
    /// 超出 4,634 字节。源码先算 <c>len</c> 再 <c>new byte[len]</c>，
    /// 所以这条测试不会真的去申请 2 GB 内存 —— 它同时验证了"拒绝得足够早"。</para>
    /// </summary>
    [Fact]
    public void CreateFilled_字节数溢出int上限_抛异常且尚未尝试分配()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Coverage8.CreateFilled(46341, 46341, 255));
    }

    // ─────────────────── SelectionMask：只有 Empty 与 Full 两条公开路径 ───────────────────

    /// <summary>
    /// <see cref="SelectionMask.Empty"/>：<see cref="SelectionMask.IsEmpty"/> 为真、
    /// 边界是 <see cref="DocRect.Empty"/>、覆盖度是 1×1 的零。
    /// <para><see cref="SelectionMask.IsEmpty"/> 的口径是 <c>Bounds.IsEmpty</c>
    /// （<b>不是</b>"覆盖度全为 0"）。这正是本测试只断言边界而不去扫描像素的原因：
    /// 若改成扫描实现，这条测试照样绿，但每帧调用会退化成 O(像素数)。</para>
    /// </summary>
    [Fact]
    public void Empty_边界是空矩形_覆盖度是1乘1的零()
    {
        SelectionMask empty = SelectionMask.Empty;

        Assert.True(empty.IsEmpty);
        Assert.True(empty.Bounds.IsEmpty);
        Assert.Equal(DocRect.Empty, empty.Bounds);
        Assert.Equal(new DocPoint(0, 0), empty.Bounds.Origin);
        Assert.Equal(new DocSize(0, 0), empty.Bounds.Size);
        Assert.Equal(1, empty.Coverage.Data.Length);
        Assert.Equal(0, empty.Coverage.Data[0]);
    }

    /// <summary>
    /// 空选区在<b>任意</b>坐标的覆盖度都是 0 —— 包括文档内的点、负坐标、以及远超文档尺寸的坐标。
    /// <para>为什么这必须成立：裁剪、蒙版合成、导出都要无条件对每个像素问一次覆盖度，
    /// 一个需要调用方自己先判界的 API 会让每个调用点都漏判一次，表现为
    /// "边缘出现一圈幽灵像素"。<c>CoverageAt</c> 的契约是"区域外为 0"，
    /// 调用方不需要、也不应该自己判界。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(5, 5)]
    [InlineData(-1, -1)]
    [InlineData(-1000.5, 3)]
    [InlineData(1920, 1080)]
    [InlineData(1e9, 1e9)]
    public void Empty_CoverageAt在任意坐标都返回0(double x, double y)
    {
        Assert.Equal(0, SelectionMask.Empty.CoverageAt(new DocPoint(x, y)));
    }

    /// <summary>
    /// <see cref="SelectionMask.Full"/> 的边界正好是整个文档，且 <see cref="SelectionMask.IsEmpty"/> 为假。
    /// <para>本例用 800×600 而不是 4×3，是为了让 <c>Right</c> / <c>Bottom</c> 的算式清晰：
    /// <c>Right = Origin.X + Size.Width = 0 + 800 = 800</c>，<c>Bottom = 600</c>。
    /// <see cref="DocRect.IsEmpty"/> 只看 <c>Size.Width &lt;= 0 || Size.Height &lt;= 0</c>，
    /// 不看 Origin，所以 <c>IsEmpty</c> 必为假。</para>
    /// </summary>
    [Fact]
    public void Full_边界正好是整个文档_且IsEmpty为假()
    {
        SelectionMask full = SelectionMask.Full(new DocSize(800, 600));

        Assert.False(full.IsEmpty);
        Assert.Equal(new DocPoint(0, 0), full.Bounds.Origin);
        Assert.Equal(new DocSize(800, 600), full.Bounds.Size);
        Assert.Equal(0, full.Bounds.Left);
        Assert.Equal(0, full.Bounds.Top);
        Assert.Equal(800, full.Bounds.Right);
        Assert.Equal(600, full.Bounds.Bottom);
    }

    /// <summary>
    /// 满选区必须用 <b>1×1 均匀代理</b>，而不是为整幅文档分配全分辨率覆盖度。
    /// <para>本例的文档是 4000×4000；若误用 <see cref="Coverage8.CreateFilled"/>，
    /// 覆盖度缓冲将是 16,000,000 字节（16 MB），而每个"满选区"实例都要付一次。
    /// 图层面板里满选区是<b>初始状态</b>，新建文档就是满选区，
    /// 于是每开一个文档就漏 16 MB —— 表现为开几十个文档后内存暴涨。</para>
    /// <para>断言用的 <c>Data.Length == 1</c> 是公开面上能证明"它是 1×1 代理"的充分证据
    /// （<c>Coverage8.IsUniform</c> 是 internal，本测试不依赖它）。</para>
    /// </summary>
    [Fact]
    public void Full_满选区用1乘1代理_不为整幅文档分配覆盖度()
    {
        SelectionMask full = SelectionMask.Full(new DocSize(4000, 4000));

        Assert.Equal(1, full.Coverage.Width);
        Assert.Equal(1, full.Coverage.Height);
        Assert.Equal(1, full.Coverage.Data.Length);
        Assert.Equal(255, full.Coverage.Data[0]);

        // 若实现哪天改用全分辨率缓冲，这个断言会先于内存问题报警。
        Assert.True(full.Coverage.Data.Length < 16, "满选区不该为 4000×4000 的文档分配 16,000,000 字节。");
    }

    /// <summary>
    /// 界内任意点（含四个角、中心与贴着右/下边界的亚像素点）都必须返回 255。
    /// <para>本例文档为 4×3，<c>Right = 4</c>、<c>Bottom = 3</c>。
    /// (3,2) 是右下角：3 &lt; 4 且 2 &lt; 3 → 界内 ✓。
    /// (3.999, 2.999) 证明判定用的是<b>坐标比较</b>而不是整数取整：
    /// 若先把坐标 <c>(int)</c> 截断再比，3.999 会变成 3 仍然通过，
    /// 但 4.0001 会变成 4 反而<b>错误地</b>被拒 —— 那就是"半透明边缘少一列"的经典 bug。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(3, 2)]
    [InlineData(3.999, 2.999)]
    public void Full_界内任意点都返回255_含四角与亚像素点(double x, double y)
    {
        SelectionMask full = SelectionMask.Full(new DocSize(4, 3));

        Assert.Equal(255, full.CoverageAt(new DocPoint(x, y)));
    }

    /// <summary>
    /// 🔴 <see cref="SelectionMask.Bounds"/> 是<b>半开区间</b>：<c>Left ≤ x &lt; Right</c>、
    /// <c>Top ≤ y &lt; Bottom</c>。所以 x == Width 或 y == Height 的点<b>已经在界外</b>，
    /// 覆盖度必须是 0。
    /// <para>本例文档 4×3：<c>Right = 0 + 4 = 4</c>、<c>Bottom = 0 + 3 = 3</c>。
    /// (4,3) 是被<b>两重</b>排除的角点；把这两条钉死是因为半开区间一旦被改成闭区间，
    /// 相邻矩形拼接时会重复覆盖接缝列 —— 现象是"接缝处有一列重复叠加的暗边"，
    /// 而任何单张矩形的测试都仍然是绿的。</para>
    /// </summary>
    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void Full_边界是半开区间_右边界与下边界上的点返回0(double x, double y)
    {
        SelectionMask full = SelectionMask.Full(new DocSize(4, 3));

        Assert.Equal(0, full.CoverageAt(new DocPoint(x, y)));
    }

    /// <summary>
    /// 负坐标一律返回 0。
    /// <para>因为 <c>Full</c> 的边界原点在 (0,0)，判据 <c>p.X &gt;= Left</c> 里
    /// <c>Left = 0</c>，任何负值都不可能满足。特别测 <c>-0.5</c> 是因为
    /// <c>(int)</c> 截断是<b>向零</b>的：−0.5 截断得 0，恰好落在左边界上。
    /// 若实现改成"先转局部坐标再判界"，−0.5 会被截成 0 而<b>错误地</b>被判为界内。</para>
    /// </summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-0.5, 1)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    [InlineData(-1000, -1000)]
    [InlineData(-1e9, -1e9)]
    public void Full_负坐标返回0_不会被向零截断骗过界内判定(double x, double y)
    {
        SelectionMask full = SelectionMask.Full(new DocSize(4, 3));

        Assert.Equal(0, full.CoverageAt(new DocPoint(x, y)));
    }

    /// <summary>
    /// 远超出文档尺寸的坐标返回 0，且<b>不得抛异常</b>。
    /// <para>这是真实调用形态：蒙版合成时局部坐标换算可能算出远大于文档的索引。
    /// 越界访问 <c>byte[]</c> 会抛 <see cref="IndexOutOfRangeException"/>，
    /// 那一层没有"容错"可言 —— 所以必须在 API 边界就挡掉。</para>
    /// </summary>
    [Theory]
    [InlineData(8, 6)]
    [InlineData(4000, 3000)]
    [InlineData(4.0001, 0)]
    [InlineData(0, 3.0001)]
    [InlineData(1e9, 1e9)]
    public void Full_远超出文档的坐标返回0且不抛异常(double x, double y)
    {
        SelectionMask full = SelectionMask.Full(new DocSize(4, 3));

        Assert.Equal(0, full.CoverageAt(new DocPoint(x, y)));
    }

    /// <summary>
    /// 文档宽或高不为正时 <see cref="SelectionMask.Full"/> 必须退回
    /// <see cref="SelectionMask.Empty"/>(<b>同一个实例</b>)，而不是造出一个
    /// "有边界但覆盖度全 0"的怪选区。
    /// <para>用 <c>Assert.Same</c> 断言引用相同，是因为源码直接 <c>return Empty;</c>
    /// 那个缓存的静态实例。这条顺便锁住"退化路径不产生新分配"：
    /// 空文档（新建但还没确定尺寸时）是常见状态，每次都 new 一个对象没有必要。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    [InlineData(-3, -4)]
    public void Full_尺寸非正时原样退回Empty(int w, int h)
    {
        SelectionMask result = SelectionMask.Full(new DocSize(w, h));

        Assert.Same(SelectionMask.Empty, result);
        Assert.True(result.IsEmpty);
    }

    /// <summary>
    /// 🔴 <b>契约 v1.2：<see cref="Coverage8.FromData"/> 打通「外部字节 → 覆盖度」这条路径。</b>
    /// </summary>
    /// <remarks>
    /// <para>v1.1 时这条路径<b>从公开 API 完全不可达</b>：<see cref="Coverage8"/> 只有
    /// <c>CreateFilled</c>（填单值）与 <c>Uniform</c>（1×1），而 <see cref="SelectionMask"/>
    /// 的构造器是 private、公开工厂只有 <see cref="SelectionMask.Empty"/> 与
    /// <see cref="SelectionMask.Full"/>,两者都只产出 1×1 代理。
    /// 后果是 AI-4 连一个矩形选区都构造不出来（矩形需要非零 <c>Bounds</c> + 非均匀 <c>Coverage</c>），
    /// 连 <c>CoverageAt</c> 里 <c>iy * Width + ix</c> 那个索引分支都测不到。</para>
    /// <para>v1.2 增加 <see cref="SelectionMask.FromCoverage"/> 与
    /// <see cref="Coverage8.FromData"/> 后，这条路径可用了，本组测试改为锁它。</para>
    /// <para><b>Empty / Full 刻意保持 1×1 代理不变</b> —— 满选区是图层面板的初始状态，
    /// 为它分配全分辨率缓冲在 4000×4000 文档上是 16 MB 的纯浪费。</para>
    /// </remarks>
    [Fact]
    public void FromData_导入非均匀数据_Empty与Full仍是1乘1代理()
    {
        // v1.2 新入口：任意字节都能导进来。
        byte[] bytes = { 0, 17, 34, 51, 68, 85, 102, 119, 136, 153, 170, 187 };
        Coverage8 raster = Coverage8.FromData(bytes, 4, 3);
        Assert.Equal(4, raster.Width);
        Assert.Equal(3, raster.Height);
        Assert.Equal(12, raster.Data.Length);
        Assert.Equal(187, raster.Data[11]);

        // Empty：1×1 代理，值为 0。
        Assert.Equal(1, SelectionMask.Empty.Coverage.Data.Length);
        Assert.Equal(0, SelectionMask.Empty.Coverage.Data[0]);

        // Full：1×1 代理，值为 255 —— 仍然不是全分辨率缓冲。
        SelectionMask full = SelectionMask.Full(new DocSize(4, 3));
        Assert.Equal(1, full.Coverage.Data.Length);
        Assert.Equal(255, full.Coverage.Data[0]);
        Assert.Equal(255, full.CoverageAt(new DocPoint(0, 0)));
        Assert.Equal(255, full.CoverageAt(new DocPoint(3, 2)));
    }

    /// <summary>
    /// 🔴 <b>拷贝语义</b>：<see cref="Coverage8.FromData"/> 必须拷贝，不能持有调用方的 span 引用。
    /// </summary>
    /// <remarks>
    /// <c>ReadOnlySpan&lt;byte&gt;</c> 是<b>栈视图</b>，出了调用者的栈帧就指向未定义内存。
    /// 若实现只是 <c>_data = data.ToArray()</c> 之外的东西（比如持有 <c>MemoryMarshal.CreateSpan</c>
    /// 的视图），本测试会在「改原数组后」立刻抓到内容被篡改。
    /// </remarks>
    [Fact]
    public void FromData_是拷贝_修改原数组不影响已构造的覆盖度()
    {
        byte[] bytes = { 10, 20, 30, 40 };
        Coverage8 coverage = Coverage8.FromData(bytes, 2, 2);
        Assert.Equal(10, coverage.Data[0]);
        Assert.Equal(40, coverage.Data[3]);

        // 调用方改自己的数组 —— 覆盖度必须纹丝不动。
        bytes[0] = 200;
        bytes[3] = 250;

        Assert.Equal(10, coverage.Data[0]);
        Assert.Equal(20, coverage.Data[1]);
        Assert.Equal(30, coverage.Data[2]);
        Assert.Equal(40, coverage.Data[3]);
    }

    /// <summary>长度必须<b>恰好</b>等于 <c>w * h</c>，多一个少一个都抛。</summary>
    [Theory]
    [InlineData(4, 3, 11)]  // 少一个
    [InlineData(4, 3, 13)]  // 多一个
    [InlineData(4, 3, 0)]   // 空数组
    [InlineData(2, 2, 3)]
    public void FromData_长度不等于宽乘高_抛ArgumentException(int w, int h, int length)
    {
        var bytes = new byte[length];
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Coverage8.FromData(bytes, w, h));

        Assert.Equal("data", ex.ParamName);
    }

    /// <summary>宽或高非正 → <see cref="ArgumentOutOfRangeException"/>，与 <c>CreateFilled</c> 口径一致。</summary>
    [Theory]
    [InlineData(0, 3, "w")]
    [InlineData(-1, 3, "w")]
    [InlineData(4, 0, "h")]
    [InlineData(4, -1, "h")]
    public void FromData_宽或高非正_抛ArgumentOutOfRangeException(int w, int h, string paramName)
    {
        byte[] bytes = new byte[12];
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Coverage8.FromData(bytes, w, h));

        Assert.Equal(paramName, ex.ParamName);
    }

    /// <summary>尺寸溢出 int 上限时必须先抛，<b>不得尝试分配</b>。</summary>
    [Fact]
    public void FromData_字节数溢出int上限_抛异常且尚未尝试分配()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Coverage8.FromData(ReadOnlySpan<byte>.Empty, 46341, 46341));
    }

    /// <summary>
    /// 🔴 <b>正常构造：<see cref="SelectionMask.FromCoverage"/> 后，
    /// <see cref="SelectionMask.CoverageAt"/> 终于能走 <c>iy * Width + ix</c> 那个索引分支。</b>
    /// </summary>
    /// <remarks>
    /// 刻意用<b>非零原点</b>的边界（10, 20），因为原点为 0 时，
    /// Y 翻转与否都会得到同样的索引 —— 那样这条测试就锁不住铁律 1。
    /// </remarks>
    [Fact]
    public void FromCoverage_正常构造_坐标换算与半开区间钳制都正确()
    {
        byte[] bytes = { 0, 17, 34, 51, 68, 85, 102, 119, 136, 153, 170, 187 };
        Coverage8 coverage = Coverage8.FromData(bytes, 4, 3);
        var bounds = new DocRect(new DocPoint(10, 20), new DocSize(4, 3));
        SelectionMask mask = SelectionMask.FromCoverage(bounds, coverage);

        Assert.False(mask.IsEmpty);
        Assert.Equal(bounds, mask.Bounds);
        Assert.Same(coverage, mask.Coverage);

        // 局部 (ix, iy) → 全局 (10 + ix, 20 + iy)，行优先索引 = iy * 4 + ix。
        Assert.Equal(0, mask.CoverageAt(new DocPoint(10, 20)));   // data[0 * 4 + 0]
        Assert.Equal(51, mask.CoverageAt(new DocPoint(13, 20)));  // data[0 * 4 + 3]
        Assert.Equal(68, mask.CoverageAt(new DocPoint(10, 21)));  // data[1 * 4 + 0]
        Assert.Equal(187, mask.CoverageAt(new DocPoint(13, 22))); // data[2 * 4 + 3]

        // 铁律 1：Y 向下。若实现写成 iy = Bottom - p.Y，
        // (13, 22) 会取到 data[0 * 4 + 3] = 51 而不是 187 —— 本断言即可抓住。
        Assert.NotEqual(51, mask.CoverageAt(new DocPoint(13, 22)));
    }

    /// <summary>边界是半开区间：右边界与下边界上的点返回 0。</summary>
    [Theory]
    [InlineData(14, 20)]  // Right
    [InlineData(10, 23)]  // Bottom
    [InlineData(14, 23)]  // 角
    [InlineData(9, 20)]   // Left 外
    [InlineData(10, 19)]  // Top 外
    [InlineData(-1000, -1000)]
    public void FromCoverage_半开区间之外一律返回0(double x, double y)
    {
        byte[] bytes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        SelectionMask mask = SelectionMask.FromCoverage(
            new DocRect(new DocPoint(10, 20), new DocSize(4, 3)),
            Coverage8.FromData(bytes, 4, 3));

        Assert.Equal(0, mask.CoverageAt(new DocPoint(x, y)));
    }

    /// <summary>尺寸不一致必须抛 —— <b>不自动裁剪、不补齐、不平移</b>。</summary>
    [Theory]
    [InlineData(4, 3, 3, 3)]   // 宽不符
    [InlineData(4, 3, 4, 4)]   // 高不符
    [InlineData(4, 3, 4, 5)]
    [InlineData(1, 1, 2, 1)]   // 1×1 代理挂到更大的边界上
    [InlineData(2, 1, 1, 1)]   // 反过来
    public void FromCoverage_尺寸不一致_抛ArgumentException(int cw, int ch, int bw, int bh)
    {
        Coverage8 coverage = Coverage8.FromData(new byte[cw * ch], cw, ch);
        var bounds = new DocRect(new DocPoint(0, 0), new DocSize(bw, bh));

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SelectionMask.FromCoverage(bounds, coverage));

        Assert.Equal("coverage", ex.ParamName);
    }

    /// <summary>1×1 均匀代理同样可以挂上去（它是合法的 <see cref="Coverage8"/>）。</summary>
    [Fact]
    public void FromCoverage_接受1乘1均匀代理()
    {
        SelectionMask mask = SelectionMask.FromCoverage(
            new DocRect(new DocPoint(5, 5), new DocSize(1, 1)),
            Coverage8.Uniform(128));

        Assert.Equal(128, mask.CoverageAt(new DocPoint(5, 5)));
        Assert.Equal(0, mask.CoverageAt(new DocPoint(6, 5)));
    }

    /// <summary>null 覆盖度必须抛。</summary>
    [Fact]
    public void FromCoverage_覆盖度为null_抛ArgumentNullException()
    {
        var bounds = new DocRect(new DocPoint(0, 0), new DocSize(4, 3));

        Assert.Throws<ArgumentNullException>(() => SelectionMask.FromCoverage(bounds, null!));
    }

    /// <summary>
    /// 🔴 <b>契约公开面不得扩大：<see cref="Coverage8.IsUniform"/> 仍是 internal。</b>
    /// </summary>
    /// <remarks>
    /// 评审明确要求「<c>IsUniform</c> 继续保持 internal 不变」。
    /// 本测试用反射守住这条线 —— 一旦有人顺手把它改成 public，
    /// 外部就能开始绕过 <see cref="SelectionMask.CoverageAt"/> 直接索引
    /// <see cref="Coverage8.Data"/>，而那条路径对 1×1 代理是越界的。
    /// </remarks>
    [Fact]
    public void Coverage8_IsUniform_仍然不是public()
    {
        System.Reflection.PropertyInfo? prop = typeof(Coverage8).GetProperty(
            "IsUniform",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(prop);
        Assert.False(prop!.GetMethod!.IsPublic);
    }

    // ─────────────────── NullCanvas ───────────────────

    /// <summary>
    /// <see cref="NullCanvas"/> 报告的 <see cref="ICanvas.Size"/> 必须就是构造时传入的值，
    /// 不得被"修正"为任何默认值。
    /// <para>下游 AI 会按 <c>Size</c> 去分配图层缓冲或裁剪工具矩形，
    /// 所以这个值必须<b>可预测到逐字段相等</b>；这正是 Null 实现作为并行度前提的意义。</para>
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(800, 600)]
    [InlineData(4000, 3000)]
    [InlineData(0, 0)]
    public void NullCanvas_Size是构造时传入的值(int w, int h)
    {
        NullCanvas canvas = new NullCanvas(new DocSize(w, h));

        Assert.Equal(new DocSize(w, h), canvas.Size);
        Assert.Equal(w, canvas.Size.Width);
        Assert.Equal(h, canvas.Size.Height);
    }

    /// <summary>
    /// <see cref="NullCanvas.Snapshot"/> 恒返回 <see langword="null"/> —— 这是
    /// <see cref="ICanvas"/> 契约里"该区域无内容"的<b>约定值</b>，不是缺陷。
    /// <para>用几种形态的区域验证"恒"：越界区域、空矩形、以及超出文档尺寸的巨大区域
    /// 都必须返回 null。若实现改成"裁剪后再判断越界"，就可能出现某些区域返回 null、
    /// 某些返回空缓冲，下游的 null 检查就会漏掉后者。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(10, 10, 20, 20)]
    [InlineData(-5, -5, 10, 10)]
    [InlineData(1000000, 1000000, 4, 4)]
    [InlineData(0, 0, 0, 0)]
    public void NullCanvas_Snapshot恒返回null(double x, double y, int w, int h)
    {
        NullCanvas canvas = new NullCanvas(new DocSize(800, 600));

        PixelBuffer? snapshot = canvas.Snapshot(new DocRect(new DocPoint(x, y), new DocSize(w, h)));

        Assert.Null(snapshot);
    }

    /// <summary>
    /// <see cref="NullCanvas.Invalidate"/> 必须把区域<b>原样</b>传给
    /// <see cref="ICanvas.Invalidated"/> 的订阅者，且<b>恰好触发一次</b>。
    /// <para>"原样"指不做任何裁剪或坐标变换：传入
    /// <c>(3,7) 尺寸 (11,13)</c>，收到的就必须是同一个矩形（含 <c>Origin</c> 与 <c>Size</c>）。
    /// 下游会用这个参数去圈脏区并决定重绘范围，一旦被裁剪或改写，
    /// 表现就是"改了没重绘"或"重绘了多余区域导致闪烁"。</para>
    /// </summary>
    [Fact]
    public void NullCanvas_Invalidate把参数原样传出且只触发一次()
    {
        NullCanvas canvas = new NullCanvas(new DocSize(800, 600));
        DocRect area = new DocRect(new DocPoint(3, 7), new DocSize(11, 13));

        int calls = 0;
        DocRect captured = DocRect.Empty;
        canvas.Invalidated += rect =>
        {
            calls++;
            captured = rect;
        };

        canvas.Invalidate(area);

        Assert.Equal(1, calls);
        Assert.Equal(area, captured);
        Assert.Equal(new DocPoint(3, 7), captured.Origin);
        Assert.Equal(new DocSize(11, 13), captured.Size);
    }

    /// <summary>
    /// <see cref="NullCanvas"/> 必须能安全承受两类"没有订阅者"的调用：
    /// 直接 <see cref="NullCanvas.Invalidate"/>（事件为 null，用 <c>?.Invoke</c> 短路），
    /// 以及退订之后再次 <see cref="NullCanvas.Invalidate"/>。
    /// <para>这条锁的是"Null 实现不崩"这条总约定：<see cref="NullCanvas"/> 是所有下游
    /// 单测的公共依赖，一旦它抛 <see cref="NullReferenceException"/>，
    /// 报错会出现在完全无关的下游测试文件里，排查成本极高。</para>
    /// </summary>
    [Fact]
    public void NullCanvas_没有订阅者以及退订之后调用Invalidate都不抛()
    {
        NullCanvas canvas = new NullCanvas(new DocSize(4, 4));
        DocRect area = new DocRect(new DocPoint(0, 0), new DocSize(4, 4));

        // 完全没有订阅者：必须静默返回。
        canvas.Invalidate(area);

        int calls = 0;
        Action<DocRect> handler = _ => calls++;
        canvas.Invalidated += handler;
        canvas.Invalidate(area);
        Assert.Equal(1, calls);

        canvas.Invalidated -= handler;
        canvas.Invalidate(area);
        Assert.Equal(1, calls);
    }

    // ─────────────────── NullProjectStore ───────────────────

    /// <summary>
    /// <see cref="NullProjectStore.LoadAsync"/> 返回的必须是<b>已经完成</b>的任务，
    /// 而且是"成功完成"而不是"带异常完成"。
    /// <para>这条比断言返回值更重要：<see cref="NullProjectStore"/> 是 AI-2~AI-6
    /// 开工前的公共依赖，返回未完成的任务意味着每个下游单测都得 <c>await</c> 一次，
    /// 或者（更糟）有人干脆不 await，于是"打开工程"这一步变成静默的空操作。</para>
    /// </summary>
    [Fact]
    public void NullProjectStore_LoadAsync返回的是已经成功的完成态任务()
    {
        NullProjectStore store = new NullProjectStore();

        Task<ProjectSnapshot> task = store.LoadAsync("does-not-exist.comp");

        Assert.True(task.IsCompleted);
        Assert.True(task.IsCompletedSuccessfully);
    }

    /// <summary>
    /// 加载得到的快照必须是 <see cref="ProjectSnapshot"/> 的<b>默认值</b>：
    /// <c>Version = 11</c>、<c>Size = (0,0)</c>、<c>Layers</c> 为空、
    /// <c>ColorSpace = "sRGB"</c>、<c>ActiveLayerId</c> 为 null、<c>DocumentId</c> 为 <see cref="Guid.Empty"/>。
    /// <para>注意 <c>ColorSpace</c> 这里是<b>字符串字段</b> <c>"sRGB"</c>，
    /// 与 <see cref="ColorSpace"/> 枚举<b>不是同一回事</b>，也不参与枚举序列化 ——
    /// 源码对此有专门警示，这里一并锁住字面量 <c>"sRGB"</c>（大小写敏感）。</para>
    /// </summary>
    [Fact]
    public async Task NullProjectStore_LoadAsync返回默认快照()
    {
        NullProjectStore store = new NullProjectStore();

        ProjectSnapshot snapshot = await store.LoadAsync("any.comp");

        Assert.Equal(11, snapshot.Version);
        Assert.Equal("sRGB", snapshot.ColorSpace);
        Assert.Equal(new DocSize(0, 0), snapshot.Size);
        Assert.Empty(snapshot.Layers);
        Assert.Null(snapshot.ActiveLayerId);
        Assert.Equal(Guid.Empty, snapshot.DocumentId);
    }

    /// <summary>
    /// <see cref="NullProjectStore.LoadAsync"/> <b>不依赖路径是否存在</b>：
    /// 空串、空格、相对路径、指向不存在目录的绝对路径都必须返回同一个默认快照且不抛异常。
    /// <para>这条锁住"Null 实现不碰文件系统"这个前提 —— 如果它哪天真的去打开文件，
    /// 指向不存在路径的那几例会立刻变成 <see cref="System.IO.FileNotFoundException"/>。</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("相对路径.comp")]
    [InlineData("C:\\definitely\\not\\exist\\untouched.comp")]
    public async Task NullProjectStore_LoadAsync不依赖路径是否存在(string path)
    {
        NullProjectStore store = new NullProjectStore();

        ProjectSnapshot snapshot = await store.LoadAsync(path);

        Assert.Equal(11, snapshot.Version);
        Assert.Equal("sRGB", snapshot.ColorSpace);
        Assert.Empty(snapshot.Layers);
    }

    /// <summary>
    /// <see cref="NullProjectStore.SaveAsync"/> 必须<b>立即完成</b>，且<b>不产生任何磁盘写入</b>。
    /// <para>本例刻意用一个<b>保证不存在</b>的目录路径：先断言目录不存在，
    /// 保存完成后再断言<b>它依然不存在</b>、目标文件也不存在。
    /// 于是"没有写盘"这个断言是被真实检查过的，而不是靠"任务完成了"间接推断 ——
    /// 同时本测试自己也不创建任何目录或文件。</para>
    /// </summary>
    [Fact]
    public void NullProjectStore_SaveAsync立即完成且不产生任何磁盘写入()
    {
        NullProjectStore store = new NullProjectStore();
        string directory = Path.Combine(Path.GetTempPath(), "compositor-null-store-must-not-exist", Guid.NewGuid().ToString("N"));
        string packagePath = Path.Combine(directory, "untouched.comp");

        Assert.False(Directory.Exists(directory));

        Task task = store.SaveAsync(new ProjectSnapshot(), packagePath);

        Assert.True(task.IsCompletedSuccessfully);

        // Null 实现不建目录、不建文件：目录与文件此刻都必须仍然不存在。
        Assert.False(Directory.Exists(directory), "Null 实现不应创建目录。");
        Assert.False(File.Exists(packagePath), "Null 实现不应写入文件。");
    }

    /// <summary>
    /// <see cref="IProjectStore.ExternalChangeDetected"/> 在 Null 实现下<b>永不触发</b>：
    /// 没有文件监视器可言。
    /// <para>保存再加载一轮之后计数仍须为 0。把这条钉死是为了防止某天有人为了"方便测试"
    /// 加上手工触发或轮询 —— 那会让 AI-6 的热重载在 Null 环境下以为文件一直在变，
    /// 从而反复重载。</para>
    /// </summary>
    [Fact]
    public async Task NullProjectStore_ExternalChangeDetected永不触发()
    {
        NullProjectStore store = new NullProjectStore();

        int calls = 0;
        store.ExternalChangeDetected += _ => calls++;

        await store.SaveAsync(new ProjectSnapshot(), "any.comp");
        await store.LoadAsync("any.comp");

        Assert.Equal(0, calls);
    }

    // ─────────────────── 记录类型的默认值 ───────────────────

    /// <summary>
    /// <see cref="LayerNode"/> 的默认值必须是<b>最不容易被发现的那一套</b>：
    /// 可见、不透明、普通混合、启用蒙版、<b>不是</b>调整层。
    /// <para>为什么这四个布尔/浮点默认值最要紧：它们都是 <c>init</c> 属性且带默认值，
    /// <c>new LayerNode()</c> 与 <c>new LayerNode { Name = "背景" }</c> 得到的可见性、
    /// 不透明度、混合模式完全相同 —— 而反序列化路径（AI-2）如果不显式写这些字段，
    /// 就会静默采用这组默认值。<b>默认值选错不会有任何异常，只会让所有图层
    /// 突然全被调成同一个不透明度和同一种混合模式。</b></para>
    /// <para>其余字段一并断言：<c>Id</c> 为 <see cref="Guid.Empty"/>、<c>Name</c> 为空串、
    /// <c>IsGroup</c> 为假、四个可空引用/结构字段（父 id、图像文件、蒙版文件、
    /// 蒙版来源）均为 null、<c>MaskLinked</c> 为 null（语义待 AI-2 确认，不解释）。</para>
    /// </summary>
    [Fact]
    public void LayerNode_默认可见_不透明_普通混合_启用蒙版_非调整层()
    {
        // Transform 是 required（契约缺陷 #10 的编译期护栏）：漏设就编译不过。
        LayerNode node = new LayerNode { Transform = new LayerTransform() };

        Assert.True(node.IsVisible);
        Assert.Equal(1.0f, node.Opacity);
        Assert.Equal(BlendMode.Normal, node.BlendMode);
        Assert.True(node.MaskEnabled);
        Assert.False(node.IsAdjustment);

        Assert.Equal(Guid.Empty, node.Id);
        Assert.Equal(string.Empty, node.Name);
        Assert.False(node.IsGroup);
        Assert.Null(node.Adjustment);
        Assert.Null(node.ParentId);
        Assert.Null(node.ImageFile);
        Assert.Null(node.MaskFile);
        Assert.Null(node.MaskLinked);
        Assert.Null(node.MaskSourceId);
        Assert.Null(node.MaskPlacement);
    }

    /// <summary>
    /// 带上 <see cref="AdjustmentSpec"/> 之后 <see cref="LayerNode.IsAdjustment"/> 必须为真，
    /// 且它<b>等价于 <c>Adjustment is not null</c></b>。
    /// <para>等价性用记录类型的 <c>with</c> 表达式验证：把 <c>Adjustment</c> 改回 null
    /// 得到的新节点必须是普通像素层，而原节点不受影响（记录类型的 <c>with</c> 是不可变复制）。
    /// 这条对 AI-2 写出 manifest 很关键 —— 调整层与像素层的序列化形态完全不同，
    /// 而 <c>IsAdjustment</c> 是判定二者唯一可靠的依据。</para>
    /// </summary>
    [Fact]
    public void LayerNode_带上Adjustment之后IsAdjustment为真()
    {
        LayerNode node = new LayerNode
        {
            Transform = new LayerTransform(),   // required，见上方契约缺陷 #10
            Adjustment = new AdjustmentSpec { Kind = "Levels" },
        };
        // 存到局部变量再解引用：Nullable 流分析对"属性 + 断言后的状态"没有可靠保证，
        // 而本项目 TreatWarningsAsErrors，CS8602 会直接断构建。
        AdjustmentSpec? adjustment = node.Adjustment;

        Assert.True(node.IsAdjustment);
        Assert.NotNull(adjustment);
        Assert.Equal("Levels", adjustment!.Kind);
        // 未指定 Settings 时是一个空 JsonObject，而不是 null —— 序列化端可以直接遍历。
        Assert.Empty(adjustment!.Settings);

        LayerNode plain = node with { Adjustment = null };

        Assert.False(plain.IsAdjustment);
        Assert.Null(plain.Adjustment);
        // with 表达式不修改原实例。
        Assert.True(node.IsAdjustment);
        Assert.Equal("Levels", adjustment!.Kind);
    }

    /// <summary>
    /// <see cref="ProjectSnapshot"/> 的默认版本号是 <b>11</b>，默认色彩空间是字符串
    /// <c>"sRGB"</c>。
    /// <para>版本号 11 是 <c>.comp</c> manifest 的当前格式版本，一旦改动就是
    /// <b>格式不互通</b>：Mac 版读不了。把它钉成一条断言，是为了让"顺手把默认版本写成 12"
    /// 这种改动无法悄悄合入。</para>
    /// </summary>
    [Fact]
    public void ProjectSnapshot_默认版本是11_默认色彩空间是sRGB()
    {
        ProjectSnapshot snapshot = new ProjectSnapshot();

        Assert.Equal(11, snapshot.Version);
        Assert.Equal("sRGB", snapshot.ColorSpace);
        Assert.Equal(new DocSize(0, 0), snapshot.Size);
        Assert.Empty(snapshot.Layers);
        Assert.Null(snapshot.ActiveLayerId);
        Assert.Equal(Guid.Empty, snapshot.DocumentId);
    }

    // ─────────────────── ColorConvert ───────────────────

    /// <summary>
    /// <see cref="ColorConvert.SrgbToLinear"/> 的两个端点必须精确为 0 和 1。
    /// <para>推导：0 ≤ 拐点 0.04045，走线性段 <c>0 / 12.92 = 0</c> ✓；
    /// 1 &gt; 0.04045，走幂函数段 <c>pow((1 + 0.055)/1.055, 2.4)</c>，
    /// 而 1 + 0.055 与除数 1.055 是同一个 double，<c>x/x</c> 精确等于 1.0，
    /// <c>pow(1.0, 2.4) = 1.0</c> ✓。</para>
    /// <para>两条端点是传递函数的定义域端点：返回曲线只要在这两点不闭合，
    /// 任何"整幅线性化 → 再编码回来"的批处理都会出现<b>整体偏亮或偏暗</b>的常量误差。</para>
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    public void SrgbToLinear_两个端点精确闭合(float v, float expected)
    {
    float actual = ColorConvert.SrgbToLinear(v);

    // 数学上恰好相等；留 1e-6 的窗口，避免断言依赖 libm 最后一位的实现差异。
    Assert.InRange(actual, expected - 1e-6f, expected + 1e-6f);
    }

    /// <summary>
    /// 线性段（<c>v ≤ 0.04045</c>）必须就是 <c>v / 12.92</c>。
    /// <para>⚠ 拐点用的是 <b>0.04045</b> 而不是 IEC 61966-2-1 的标准值 0.040448236…，
    /// 这是原项目 <c>AdjustPixels.c:204-207</c> 写死的值，<b>不要"修正"</b>，
    /// 否则手绘结果会与 Mac 版差一点点。</para>
    /// <para>下列期望值（手算）：</para>
    /// <list type="bullet">
    /// <item><c>0.02 / 12.92 = 1/646 = 0.0015479876</c> → <b>0.001548</b>。</item>
    /// <item><c>0.04 / 12.92 = 1/323 = 0.0030959752</c> → <b>0.003096</b>。</item>
    /// <item><c>0.04045 / 12.92 = 0.0031311146</c> → <b>0.003131</b>。</item>
    /// </list>
    /// <para>第三个用例就是拐点 <c>0.04045</c> 本身，用来钉住判据是<b>闭区间</b>
    /// <c>&lt;=</c>：改成 <c>&lt;</c> 的话它会跳到幂函数段
    /// <c>pow(0.0904739, 2.4) ≈ 0.0031293</c>，与线性段的 0.0031311 差约 1.8e-6 ——
    /// 这个量级小到肉眼和大多数单测都看不出来，但它就是"曲线在拐点处有微小折角"的来源。</para>
    /// </summary>
    [Theory]
    [InlineData(0.02f, 0.001548f)]
    [InlineData(0.04f, 0.003096f)]
    [InlineData(0.04045f, 0.003131f)]
    public void SrgbToLinear_线性段就是除以12点92(float v, float expected)
    {
    float actual = ColorConvert.SrgbToLinear(v);

    Assert.InRange(actual, expected - 1e-6f, expected + 1e-6f);
    }

    /// <summary>
    /// <see cref="ColorConvert.SrgbToLinear"/> 的手算锚点：<c>SrgbToLinear(0.5) ≈ 0.2140</c>。
    /// <para><b>算式逐步展开</b>（源码：<c>pow((encoded + 0.055) / 1.055, 2.4)</c>）：</para>
    /// <list type="number">
    /// <item>0.5 &gt; 0.04045，所以走幂函数段，不走 12.92 除法。</item>
    /// <item>底数：<c>(0.5 + 0.055) / 1.055 = 0.555 / 1.055 = 0.5260663507</c>。</item>
    /// <item>取对数：<c>ln(0.5260663507) = ln(0.5) + ln(1.0521327)
    /// = −0.6931471806 + 0.0508192533 = −0.6423279273</c>。</item>
    /// <item>乘 Gamma：<c>−0.6423279273 × 2.4 = −1.5415870255</c>。</item>
    /// <item>取指数：<c>e^(−1.5415870255) = e^(−1.5) × e^(−0.0415870255)
    /// = 0.2231301601 × 0.959265884 = 0.2140411</c>。</item>
    /// <item>末位再由 double 收窄到 <see cref="float"/>。</item>
    /// </list>
    /// <para>为什么值得单独钉一个点：<c>0.5</c> 是"中灰"，而 sRGB 传递函数在中灰处斜率最陡。
    /// 若有人把 Gamma 抄成 2.2（另一个常见的教科书值），本例会得到
    /// <c>0.5^2.2 = 0.2176</c>，超出下面 [0.2135, 0.2145] 的窗口；而若把 12.92 线性段
    /// 误用到中灰上，会得到 0.0387，差一个数量级。<b>两个常见抄错都在这里被挡下。</b></para>
    /// </summary>
    [Fact]
    public void SrgbToLinear_0点5的推导值是02140()
    {
    float actual = ColorConvert.SrgbToLinear(0.5f);

    // 算式见 XML 注释：pow(0.5260663507, 2.4) = e^(-1.5415870255) = 0.2140411。
    Assert.InRange(actual, 0.2135f, 0.2145f);
    }

    /// <summary>
    /// 🔴 <see cref="ColorConvert.SrgbToLinear"/> <b>不做上限钳制</b>（与 C 的
    /// <c>srgb_to_linear</c> 一致，源码对此有 ⚠ 标注）：传入 &gt; 1 会得到 &gt; 1 的结果。
    /// <para>本例手算：<c>(1.5 + 0.055) / 1.055 = 1.555 / 1.055 = 1.4739336</c>；
    /// <c>ln(1.4739336) ≈ 0.3879348</c>；<c>× 2.4 = 0.9310435</c>；
    /// <c>e^0.9310435 ≈ 2.5372</c>。</para>
    /// <para>把"不钳制"钉成断言，是因为它与 <see cref="ColorConvert.LinearToSrgb"/> 的行为
    /// <b>不对称</b>（后者两端都钳制）。这个不对称是刻意保留的：任何在两侧各加一个钳制的"清理"
    /// 都会让线性空间里超出显示范围的值被静默压平，从而在两次以上混合后累积偏色。
    /// 边界理应由调用方（合成器）自己决定。</para>
    /// </summary>
    [Fact]
    public void SrgbToLinear_刻意不做上限钳制_与C的srgb_to_linear一致()
    {
    float actual = ColorConvert.SrgbToLinear(1.5f);

    Assert.InRange(actual, 2.4f, 2.7f);
    Assert.True(actual > 1f, "本函数按契约不做上限钳制，1.5 必须得到大于 1 的结果。");
    }

    /// <summary>
    /// <see cref="ColorConvert.LinearToSrgb"/> 对 ≤ 0 的输入必须返回<b>精确的 0</b>
    /// （源码是直接 <c>return 0f</c>）。
    /// <para>这条挡的是"负线性值被送进 <c>Math.Pow</c>"：负底数的小数次幂返回 <see cref="double.NaN"/>，
    /// NaN 一旦进到像素缓冲就会在后续运算里传播成整片 NaN，且没有任何异常。
    /// 所以钳制必须发生在取幂<b>之前</b>。</para>
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.0001f)]
    [InlineData(-1f)]
    [InlineData(-10f)]
    [InlineData(-1e9f)]
    public void LinearToSrgb_小于等于0时返回精确的0(float v)
    {
    Assert.Equal(0f, ColorConvert.LinearToSrgb(v));
    }

    /// <summary>
    /// <see cref="ColorConvert.LinearToSrgb"/> 对 ≥ 1 的输入必须返回<b>精确的 1</b>。
    /// <para>同样的理由：线性空间里 HDR 值（1.5、10）非常常见，若不钳制，
    /// <c>1.055 × pow(10, 1/2.4) − 0.055 ≈ 4.35</c> 会作为编码值写进 8 位缓冲，
    /// 溢出后被截断成垃圾。与 <see cref="ColorConvert.SrgbToLinear"/> 不同，
    /// <b>这一侧必须钳</b>，因为它面向的是 8 位存储。</para>
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.0001f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(255f)]
    public void LinearToSrgb_大于等于1时返回精确的1(float v)
    {
    Assert.Equal(1f, ColorConvert.LinearToSrgb(v));
    }

    /// <summary>
    /// 线性段（<c>v ≤ 0.0031308</c>）必须就是 <c>v × 12.92</c>。
    /// <para>⚠ 这里的拐点用的是 <b>0.0031308</b>，而不是标准值 0.003130668…，
    /// 同样是原项目写死的值，<b>不要"修正"</b>。</para>
    /// <para>下列期望值（手算）：<c>0.001 × 12.92 = 0.01292</c>；
    /// <c>0.003 × 12.92 = 0.03876</c>；
    /// <c>0.0031308 × 12.92 = 0.040449936</c> → <b>0.040450</b>。
    /// 第三个用例就是拐点本身，用来钉住闭区间判据 <c>&lt;=</c>。</para>
    /// </summary>
    [Theory]
    [InlineData(0.001f, 0.01292f)]
    [InlineData(0.003f, 0.03876f)]
    [InlineData(0.0031308f, 0.040450f)]
    public void LinearToSrgb_线性段乘以12点92(float v, float expected)
    {
    float actual = ColorConvert.LinearToSrgb(v);

    Assert.InRange(actual, expected - 1e-6f, expected + 1e-6f);
    }

    /// <summary>
    /// 🔴 传递函数的往返必须能把值还原回来：<c>LinearToSrgb(SrgbToLinear(v)) ≈ v</c>，
    /// 容差 1e-4。
    /// <para>为什么容差取 1e-4 而不是要求严格相等：两段各自取一次 <c>Math.Pow</c>，
    /// 而 <c>pow(pow(u, 2.4), 1/2.4)</c> 只在浮点精度内回到 <c>u</c>（相对误差约 1e-16），
    /// 乘回 1.055 再减 0.055 后绝对误差仍在 1e-6 量级。要求严格相等等于
    /// 断言"libm 的最后一位"，那种测试会在换平台/换 CPU 时假红。</para>
    /// <para><b>为什么必须测往返：</b>任何"为了省事把中间结果存成 8 位再继续"的实现
    /// 都依赖这条闭合性；一旦传递函数两段的拐点被"顺手统一"成同一个常数，
    /// 往返在拐点附近会出现最大约 5e-6 的偏差并累积多次。本例特意<b>避开</b>拐点
    /// （0.04045 / 0.0031308 两侧最近的取样点），把"拐点附近的最大偏差"
    /// 留给将来专门加测，而不是混在这条里让它变得难以诊断。</para>
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.02f)]
    [InlineData(0.04f)]
    [InlineData(0.05f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.9f)]
    [InlineData(1f)]
    public void 往返_线性化再编码后能还原回原值(float v)
    {
    float linear = ColorConvert.SrgbToLinear(v);
    float back = ColorConvert.LinearToSrgb(linear);

    Assert.InRange(back, v - 1e-4f, v + 1e-4f);
    }

    /// <summary>
    /// 🔴 <see cref="ColorSpace"/> 的枚举值顺序<b>不得改动</b>：
    /// 原项目 <c>Document/LayerAppearance.swift:4</c> 用 <c>CaseIterable</c> 依赖了顺序。
    /// <para>顺带提醒两个被源码反复警告的混淆点，这里一并锁住：</para>
    /// <list type="bullet">
    /// <item><see cref="ColorSpace"/> 枚举<b>不参与</b> <c>.comp</c> 序列化 ——
    /// 图层混合模式走的是 <c>BlendModeStrings</c>。</item>
    /// <item><see cref="ProjectSnapshot.ColorSpace"/> 那个<b>字符串</b>字段
    /// 与本枚举不是一回事，判定结论是 <c>"sRGB"</c>（见
    /// <c>ProjectSnapshot_默认版本是11_默认色彩空间是sRGB</c>）。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void ColorSpace_枚举值顺序被原项目CaseIterable依赖_不得重排()
    {
    Assert.Equal(0, (int)ColorSpace.Srgb);
    Assert.Equal(1, (int)ColorSpace.LinearSrgb);
    }
}
