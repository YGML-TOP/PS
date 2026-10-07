namespace Compositor.Imaging;

/// <summary>
/// 图像层的尺寸与内存上限。与 macOS 版 <c>DocumentLimits.swift</c> 逐值对应。
/// </summary>
/// <remarks>
/// <para>
/// 与 Mac 版的唯一差别是 <see cref="DocumentPixelBudget"/>：Mac 走
/// <c>ProcessInfo.processInfo.physicalMemory</c>，那是 Apple 专有 API，Windows 上没有等价物。
/// 这里把内存探测抽象成 <see cref="MemoryProbeBytes"/>，公式本身保持一致。
/// </para>
/// <para>
/// ⚠️ <c>documentPixelBudget</c> 的最终口径仍待 YG 拍板（固定 800 MP / 按内存比例）。
/// 两种口径的差别见 <c>ai2-00-.comp格式移植手册.md</c>。改这里等于改跨机互通的上限，
/// 不要顺手改。
/// </para>
/// </remarks>
public static class ImagingLimits
{
    /// <summary>任意画布、图层、蒙版或生成表面的最长边（像素）。对应 <c>maxSide</c>。</summary>
    public const int MaxSide = 30_000;

    /// <summary>
    /// 单个表面的最大像素数：画布、导出、滤镜目标、调整或蒙版渲染。对应 <c>maxSurfacePixels</c>。
    /// </summary>
    /// <remarks>
    /// RGBA8 下一次分配最多 800 MB，而滤镜会同时持有若干张。
    /// </remarks>
    public const int MaxSurfacePixels = 200_000_000;

    /// <summary><see cref="MaxSurfacePixels"/> 的兆像素表述，用于错误文案。</summary>
    public const int MaxSurfaceMegapixels = MaxSurfacePixels / 1_000_000;

    /// <summary>
    /// 单个文档可持有的导入位图总量，逐图层与蒙版累加。对应 <c>documentPixelBudget</c>。
    /// </summary>
    /// <remarks>
    /// Mac 公式：<c>min(800_000_000, max(maxSurfacePixels, physicalMemory / 16))</c>。
    /// 永远不小于一个表面，永远不大于 800 MP（16 GB 的 Mac 刚好触到上限）。
    /// </remarks>
    public static int DocumentPixelBudget => System.Math.Min(800_000_000,
        (int)System.Math.Max((long)MaxSurfacePixels, MemoryProbeBytes() / 16));

    /// <summary><see cref="DocumentPixelBudget"/> 的兆像素表述，用于错误文案。</summary>
    public static int DocumentBudgetMegapixels => DocumentPixelBudget / 1_000_000;

    /// <summary>
    /// 物理内存字节数的探测点。默认读 GC 可见的可用内存总量。
    /// </summary>
    /// <remarks>
    /// 抽出来是为了可测：测试可以注入一个固定值，不必真的换机器。
    /// 若 YG 拍板「固定 800 MP」，把 <see cref="DocumentPixelBudget"/> 改成常量即可，
    /// 本方法即可删除。
    /// </remarks>
    public static Func<long> MemoryProbeBytes { get; set; } =
        static () => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>
    /// 判断一张图是否超过导入上限。三个条件与 <c>ImageImporter.decode</c> 完全一致。
    /// </summary>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="remainingPixels">本次导入后本层预算的剩余像素数。</param>
    /// <returns>通过返回 <see langword="true"/>；超限返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 注意 <c>width * height</c> 用 <see cref="long"/>：两个各 30_000 的 int 相乘会溢出 int
    /// （9e8 未溢出，但一旦有人放宽 MaxSide 就会）。Mac 侧是 Int，此处按 int 计算，
    /// 但调用方传进来的必须是合法值。
    /// </remarks>
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
}