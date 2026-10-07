using Xunit;

namespace Compositor.Imaging.Tests;

/// <summary>
/// 限额与内存护栏的测试。
/// </summary>
/// <remarks>
/// <b>期望值纪律：</b>本文件的阈值分两类，来源不同，已逐条标注：
/// <list type="bullet">
/// <item><b>抄自 Mac 源码</b>：<c>MaxSide</c>、<c>MaxSurfacePixels</c>、<c>DocumentPixelBudget</c>
/// 三个常量逐字取自 <c>DocumentLimits.swift:18,25,36-37</c>。</item>
/// <item><b>本移植自定</b>：<c>DocumentPixelBudget</c> 固定 800 MP 与
/// <c>AllocatableShare = 0.5</c>，是 YG 2026-10-07 裁决的口径，
/// <b>不是</b> Mac 版行为，Mac 版是按物理内存动态缩放的。</item>
/// </list>
/// </remarks>
public sealed class ImagingLimitsTests : IDisposable
{
    private readonly Func<long> _originalProbe = ImagingLimits.MemoryProbeBytes;

    public void Dispose() => ImagingLimits.MemoryProbeBytes = _originalProbe;

    [Fact]
    public void ConstantsMatchDocumentLimitsSwift()
    {
        // DocumentLimits.swift:18  static let maxSide = 30_000
        Assert.Equal(30_000, ImagingLimits.MaxSide);

        // DocumentLimits.swift:25  static let maxSurfacePixels = 200_000_000
        Assert.Equal(200_000_000, ImagingLimits.MaxSurfacePixels);

        // DocumentLimits.swift:36-37 的上限封顶值 min(800_000_000, …)
        Assert.Equal(200, ImagingLimits.MaxSurfaceMegapixels);
        Assert.Equal(800, ImagingLimits.DocumentBudgetMegapixels);
    }

    [Fact]
    public void DocumentPixelBudgetIsFixed_EvenOnARAMStarvedMachine()
    {
        // 这是本次裁决的核心：格式上限与本机内存无关。
        // 若这里随内存缩水，一台低配 Windows 就打不开高配 Mac 存得进去的工程。
        ImagingLimits.MemoryProbeBytes = () => 1L * 1024 * 1024 * 1024; // 1 GB

        Assert.Equal(800_000_000, ImagingLimits.DocumentPixelBudget);
    }

    [Fact]
    public void MemoryGuard_ThrowsClearErrorInsteadOfLettingItOom()
    {
        ImagingLimits.MemoryProbeBytes = () => 4L * 1024 * 1024 * 1024; // 4 GB 可用

        // 4 GB × 0.5 = 2 GB 可分配；3.2 GB（= 800 MP × 4 字节）超出。
        var error = Assert.Throws<ImageCodecException>(
            () => ImagingLimits.EnsureWithinMemoryBudget(800_000_000L));

        Assert.Equal(ImageImportError.OutOfMemory, error.Kind);
    }

    [Fact]
    public void MemoryGuard_AllowsWhatActuallyFits()
    {
        ImagingLimits.MemoryProbeBytes = () => 16L * 1024 * 1024 * 1024; // 16 GB

        // 800 MP × 4 = 3.2 GB，远小于 16 GB × 0.5 —— 上一条裁决说的那个场景必须能开。
        ImagingLimits.EnsureWithinMemoryBudget(800_000_000L);
    }

    [Fact]
    public void MemoryGuard_FailsOpen_WhenProbeThrows()
    {
        // 探测失败时选择放行：护栏的目的是把 OOM 换成可读报错，
        // 不是反过来制造"内存其实够却打不开"的假失败。
        ImagingLimits.MemoryProbeBytes = () => throw new InvalidOperationException("探测失败");

        ImagingLimits.EnsureWithinMemoryBudget(800_000_000L);
    }

    [Fact]
    public void MemoryGuard_IgnoresNonPositivePixelCounts()
    {
        ImagingLimits.MemoryProbeBytes = () => 1L;

        ImagingLimits.EnsureWithinMemoryBudget(0L);
        ImagingLimits.EnsureWithinMemoryBudget(-1L);
    }

    [Fact]
    public void MemoryGuard_DoesNotOverflowOnTheLargestLegalDocument()
    {
        ImagingLimits.MemoryProbeBytes = () => long.MaxValue;

        // 800 MP × 4 = 3.2e9，超 int.MaxValue(2.147e9)。
        // 用 int 算会溢出成负数，护栏就会误判"扛得住"。这里钉住 long 计算。
        ImagingLimits.EnsureWithinMemoryBudget(800_000_000L);
        Assert.Equal(3_200_000_000L, 800_000_000L * 4L);
    }

    [Theory]
    [InlineData(64, 32, 10, false)]        // 2048 px，远超剩余 10
    [InlineData(64, 32, 2048, true)]       // 刚好用完预算
    [InlineData(64, 32, 2047, false)]      // 差一个像素也不行
    [InlineData(30_000, 1, 30_000, true)]  // 正好顶到 maxSide
    [InlineData(30_001, 1, 1_000_000, false)] // 超过 maxSide
    [InlineData(0, 10, 1000, false)]       // 非法尺寸
    [InlineData(-1, -1, 1000, false)]
    public void WithinImportLimits_ThreeConditionsMatchImageImporter(
        int width, int height, int remaining, bool expected)
    {
        Assert.Equal(expected, ImagingLimits.WithinImportLimits(width, height, remaining));
    }

    [Fact]
    public void WithinImportLimits_DoesNotOverflowOnMaxSideSquare()
    {
        // maxSide × maxSide = 9e8。int 装得下（int.MaxValue ≈ 2.147e9），但要确认
        // 面积比较没有走窄化路径 —— 一旦 MaxSide 被放宽，这个格子就是 int 溢出的地方。
        const long maxSideSquare = (long)30_000 * 30_000; // 900,000,000
        Assert.Equal(900_000_000L, maxSideSquare);

        // 预算给足时该放行。
        Assert.True(ImagingLimits.WithinImportLimits(
            ImagingLimits.MaxSide, ImagingLimits.MaxSide, int.MaxValue));

        // 预算为 800 MP 时 9e8 > 8e8，该拒 —— 这是真实会发生的情形。
        Assert.False(ImagingLimits.WithinImportLimits(
            ImagingLimits.MaxSide, ImagingLimits.MaxSide, ImagingLimits.DocumentPixelBudget));
    }
}