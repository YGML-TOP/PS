using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Compositor.App.ViewModels;

namespace Compositor.App;

/// <summary>主窗口：菜单 + 图层面板 + 画布 + 属性面板的骨架布局。</summary>
/// <remarks>
/// 本类只做两件事：挂上视图模型、把视图模型交给 XAML 绑定。
/// <b>没有任何像素、图层或编辑逻辑</b>——那些属于后续的 lane。
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>构造窗口：先加载 XAML，再挂视图模型。</summary>
    /// <remarks>
    /// <see cref="MainWindowViewModel"/> 在 <c>AvaloniaXamlLoader.Load(this)</c> <b>之前</b>建好，
    /// 这样第一帧就有可绑的数据，不会闪一下空白面板。
    /// </remarks>
    public MainWindow()
    {
        _viewModel = new MainWindowViewModel();
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
    }
}