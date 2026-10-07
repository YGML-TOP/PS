using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Compositor.App.Canvas;
using Compositor.Core;

namespace Compositor.App.ViewModels;

/// <summary>主窗口的视图模型：把契约层的工程快照翻译成界面能绑的东西。</summary>
/// <remarks>
/// <para>本类是 UI 层与契约层之间<b>唯一</b>的接缝：
/// 上游是 <see cref="ProjectSnapshot"/> / <see cref="LayerNode"/> / <see cref="BlendMode"/>，
/// 下游是 XAML 绑定能用的普通属性。后续的撤销栈、选中态、真实缩略图都挂在这里。</para>
/// </remarks>
public sealed class MainWindowViewModel
{
    /// <summary>用演示工程构造视图模型。</summary>
    public MainWindowViewModel()
        : this(SampleProject.Create())
    {
    }

    /// <summary>由工程快照构造视图模型。</summary>
    /// <param name="snapshot">工程快照。为 <see langword="null"/> 时退化成空工程。</param>
    public MainWindowViewModel(ProjectSnapshot? snapshot)
    {
        ProjectSnapshot model = snapshot ?? new ProjectSnapshot();
        Snapshot = model;

        // 契约层规定 Layers 自下而上（index 0 是最底层）。
        // 界面习惯是"最上面的图层排在列表第一行"，所以这里倒序建行。
        // 这是一次<b>有意的</b>方向翻转，与坐标系的 Y 轴无关（Y 轴不翻，见 CanvasViewport）。
        foreach (LayerNode node in Enumerable.Reverse(model.Layers))
        {
            Layers.Add(new LayerRowViewModel(node));
        }

        BlendModes = new ObservableCollection<BlendModeOption>(BuildBlendModeOptions());

        // 合成器主干还没实现，画布内容先由确定性的测试图像顶上。
        CanvasPixels = TestPattern.CreateDefault();

        ActiveLayer = Layers.FirstOrDefault(l => l.Id == model.ActiveLayerId) ?? Layers.FirstOrDefault();

        QuitCommand = new DelegateCommand(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
    }

    /// <summary>本次构造所用的工程快照。</summary>
    public ProjectSnapshot Snapshot { get; }

    /// <summary>图层面板的行集合，<b>自上而下</b>（列表第一行是最高图层）。</summary>
    public ObservableCollection<LayerRowViewModel> Layers { get; } = new();

    /// <summary>混合模式下拉框的 24 个选项，顺序即 Photoshop 菜单顺序。</summary>
    public ObservableCollection<BlendModeOption> BlendModes { get; }

    /// <summary>画布控件要显示的像素。</summary>
    public PixelBuffer CanvasPixels { get; }

    /// <summary>退出命令。这是当前唯一<b>真正可用</b>的菜单命令。</summary>
    public DelegateCommand QuitCommand { get; }

    /// <summary>当前选中图层的行；没有图层时为 <see langword="null"/>。</summary>
    public LayerRowViewModel? ActiveLayer { get; }

    /// <summary>选中图层名，供状态栏显示。</summary>
    public string ActiveLayerName => ActiveLayer?.Name ?? "（无）";

    /// <summary>选中图层的混合模式字面量，供属性面板显示。</summary>
    public string ActiveLayerBlendModeText => ActiveLayer?.BlendModeLiteral ?? "—";

    /// <summary>选中图层的不透明度文本，供属性面板显示。</summary>
    public string ActiveLayerOpacityText => ActiveLayer?.OpacityText ?? "—";

    /// <summary>文档尺寸文本，供状态栏显示。</summary>
    public string DocumentSizeText => $"{Snapshot.Size.Width} × {Snapshot.Size.Height}";

    /// <summary>
    /// 按 <see cref="BlendModeGroups.InPhotoshopOrder"/> 摊平成下拉框条目。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="BlendModeGroups"/> 而不是 <see cref="Enum.GetValues"/>：
    /// 前者是 Photoshop 菜单的真实分组顺序（暗化一组、亮化一组……中间画分隔线），
    /// 而枚举声明顺序恰好也一致，但只有 Groups 这条是被文档钉死的契约。
    /// </remarks>
    private static IEnumerable<BlendModeOption> BuildBlendModeOptions()
    {
        foreach (BlendMode[] group in BlendModeGroups.InPhotoshopOrder)
        {
            foreach (BlendMode mode in group)
            {
                yield return new BlendModeOption(mode, BlendModeStrings.ToLiteral(mode));
            }
        }
    }
}