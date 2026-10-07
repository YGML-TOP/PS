using Compositor.App.Canvas;
using Compositor.Core;

namespace Compositor.App.ViewModels;

/// <summary>
/// 造一份用于演示的 <see cref="ProjectSnapshot"/>。
/// </summary>
/// <remarks>
/// 🔴 这些图层是<b>假的</b>：没有栅格像素（<c>ImageFile</c> 全为 <see langword="null"/>），
/// 画布内容另由 <see cref="TestPattern"/> 提供。
/// 它们的作用是验证「契约层的 <see cref="LayerNode"/> → 界面行」这条链路真的通，
/// 以及让图层面板在第一版就有东西可显示。
/// <para>合成器与工程存储就位后，整个类型连同调用点一起删掉即可。</para>
/// </remarks>
public static class SampleProject
{
    private static readonly Guid BackgroundId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GradientId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid NotesId = new("33333333-3333-3333-3333-333333333333");

    /// <summary>构造演示用的工程快照。</summary>
    /// <returns>含 3 个假图层的 <see cref="ProjectSnapshot"/>，尺寸 512 × 512。</returns>
    public static ProjectSnapshot Create() => new()
    {
        DocumentId = new Guid("0C0C0C0C-0C0C-0C0C-0C0C-0C0C0C0C0C0C"),
        Size = new DocSize(512, 512),
        ColorSpace = "sRGB",
        Layers = new[]
        {
            MakeLayer(BackgroundId, "背景", isVisible: true, opacity: 1.0f, BlendMode.Normal),
            MakeLayer(GradientId, "渐变叠加", isVisible: true, opacity: 0.85f, BlendMode.Overlay),
            MakeLayer(NotesId, "注释（隐藏）", isVisible: false, opacity: 1.0f, BlendMode.Multiply),
        },
        ActiveLayerId = GradientId,
    };

    /// <summary>造一个带必需几何变换的图层节点。</summary>
    private static LayerNode MakeLayer(Guid id, string name, bool isVisible, float opacity, BlendMode mode) => new()
    {
        Id = id,
        Name = name,
        IsVisible = isVisible,
        Opacity = opacity,
        BlendMode = mode,

        // 🔴 LayerNode.Transform 是 required：漏设会在编译期报错，而不是在合成阶段 NRE。
        Transform = new LayerTransform
        {
            Origin = new DocPoint(0, 0),
            Size = new DocSize(512, 512),
            Sampling = SamplingQuality.HighQuality,
        },
    };
}