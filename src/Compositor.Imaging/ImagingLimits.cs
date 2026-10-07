using System.Runtime.InteropServices;

namespace Compositor.Imaging;

/// <summary>
/// 图像层的尺寸上限与内存护栏。尺寸常量与 macOS 版 <c>DocumentLimits.swift</c> 逐值对应。
/// </summary>
/// <remarks>
/// <para>
/// <b>🔴 与 Mac 版的唯一结构差异：<see cref="DocumentPixelBudget"/> 不再看物理内存。</b>
/// </para>
/// <para>
/// Mac 侧 <c>DocumentLimits.swift:36-37</c> 的公式是
/// <c>min(800MP, max(200MP, physicalMemory / 16))</c>，而 <c>ProjectStore.swift:265</c>
/// 用它做<b>保存校验</b>。若 Windows 也按本机内存动态算，一台低配 Windows 就打不开
/// 高配 Mac 存得进去的工程 —— 而跨机互通正是本项目存在的唯一理由。
/// 因此拆成两层：
/// </para>
/// <list type="number">
/// <item><b>格式上限</b>（<see cref="DocumentPixelBudget"/>）：固定 <c>800_000_000</c>，
/// 与机器无关，保证两端能互相打开对方的工程。超了就是非法工程。</item>
/// <item><b>内存护栏</b>（<see cref="EnsureWithinMemoryBudget"/>）：按本机可用内存动态估算，
/// 开不动就抛明确异常，而不是让进程 OOM 当场崩掉。
/// 护栏<b>不参与格式合法性判定</b>，只回答"这台机器现在扛不扛得住"。</item>
/// </list>
/// <para>
/// ⚠️ <b>残留行为要知道：</b>护栏一旦触发，语义上等于"这台机器打不开这个工程"。
/// 这是 800 MP 固定上限的代价 —— 用清晰的报错换掉不可预期的 OOM 崩溃。
/// </para>
/// </remarks>
public static class ImagingLimits
{
    /// <summary>任意画布、图层、蒙版或生成表面的最长边（像素）。对应 <c>maxSide</c>。</summary>
    public const int MaxSide = 30_000;

    /// <summary>
    /// 单个表面的最大像素数：画布、导出、滤镜目标、调整或蒙版渲染。对应 <c>maxSurfacePixels</c>。
    /// </summary>
    /// <remarks>RGBA8 下一次分配最多 800 MB，而滤镜会同时持有若干张。</remarks>
    public const int MaxSurfacePixels = 200_000_000;

    /// <summary>
    /// 单个文档可持有的导入位图总量，逐图层与蒙版累加。
    /// </summary>
    /// <remarks>
    /// <b>固定值，与本机内存无关。</b>Mac 版此处按物理内存动态缩放
    /// （<c>DocumentLimits.swift:36-37</c>），本移植刻意不跟：见类型注释的「残留行为」。
    /// 要改成动态，改这一行即可，护栏逻辑不受影响。
    /// </remarks>
    public const int DocumentPixelBudget = 800_000_000;

    /// <summary><see cref="MaxSurfacePixels"/> 的兆像素表述，用于错误文案。</summary>
    public const int MaxSurfaceMegapixels = MaxSurfacePixels / 1_000_000;

    /// <summary><see cref="DocumentPixelBudget"/> 的兆像素表述，用于错误文案。</summary>
    public const int DocumentBudgetMegapixels = DocumentPixelBudget / 1_000_000;

    /// <summary>
    /// 可用物理内存探测点，默认走 Win32 <c>GlobalMemoryStatusEx</c>。抽出来是为了可测。
    /// </summary>
    public static Func<long> MemoryProbeBytes { get; set; } = QueryAvailablePhysicalBytes;

    /// <summary>
    /// 可用物理内存字节数。探测失败时返回 <see cref="long.MaxValue"/>，即"不拦你"。
    /// </summary>
    /// <remarks>
    /// 探测失败时选择放行而不是拦截：护栏的目的是把 OOM 换成可读的报错，
    /// 不是反过来制造"明明内存够却打不开"的假失败。
    /// </remarks>
    public static long AvailablePhysicalBytes
    {
        get
        {
            try
            {
                var value = MemoryProbeBytes();
                return value < 0 ? long.MaxValue : value;
            }
            catch
            {
                return long.MaxValue;
            }
        }
    }

    /// <summary>
    /// 内存护栏的可分配上限：可用物理内存的这个比例。
    /// </summary>
    /// <remarks>
    /// 为什么不直接用全部可用内存：Windows 上「可用物理内存」不等于「能给本进程的」，
    /// 页文件提交、分页、GPU 共享都还没算进去。留一半是保守估计，
    /// 宁可少拦也不要误杀正常工程。护栏本身<b>不影响格式合法性</b>，只是提前失败。
    /// </remarks>
    public const double AllocatableShare = 0.5d;

    /// <summary>
    /// 判断一张图是否超过导入上限。三个条件与 <c>ImageImporter.decode</c> 完全一致。
    /// </summary>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="remainingPixels">本次导入后本层预算的剩余像素数。</param>
    /// <returns>通过返回 <see langword="true"/>；超限返回 <see langword="false"/>。</returns>
    public static bool WithinImportLimits(int width, int height, int remainingPixels)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        return width <= MaxSide
            && height <= MaxSide
            && (long)width * height <= remainingPixels;
    }

    /// <summary>
    /// 内存护栏：估算这台机器扛不扛得住 <paramref name="pixelCount"/> 个像素的 RGBA8 分配。
    /// </summary>
    /// <param name="pixelCount">像素总数。</param>
    /// <param name="bytesPerPixel">每像素字节数，RGBA8 为 4。</param>
    /// <exception cref="ImageCodecException">
    /// 估算超出可分配上限时抛 <see cref="ImageImportError.OutOfMemory"/>。
    /// </exception>
    /// <remarks>
    /// 用 <see cref="long"/> 算：800 MP × 4 字节 = 3.2 GB，<c>int</c> 会溢出。
    /// </remarks>
    public static void EnsureWithinMemoryBudget(long pixelCount, int bytesPerPixel = 4)
    {
        if (pixelCount <= 0)
        {
            return;
        }

        var required = pixelCount * bytesPerPixel;
        var available = AvailablePhysicalBytes;

        // AvailablePhysicalBytes 返回 long.MaxValue 表示探测失败，放行。
        if (available == long.MaxValue)
        {
            return;
        }

        if (required > (long)(available * AllocatableShare))
        {
            throw new ImageCodecException(ImageImportError.OutOfMemory);
        }
    }

    private static long QueryAvailablePhysicalBytes()
    {
        if (!GlobalMemoryStatusExFn(out var status))
        {
            return long.MaxValue;
        }

        return status.ullAvailPhys > long.MaxValue ? long.MaxValue : (long)status.ullAvailPhys;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusExFn(out MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}