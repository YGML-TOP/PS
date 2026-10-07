using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Compositor.App;

/// <summary>应用对象：加载主题，并在启动后创建主窗口。</summary>
/// <remarks>
/// <para>标准 Avalonia 骨架职责，<b>没有业务逻辑</b>。窗口的构造参数从
/// <see cref="IClassicDesktopStyleApplicationLifetime"/> 的
/// <c>MainWindow</c> 取（由 <see cref="Program.Main"/> 的
/// <c>StartWithClassicDesktopLifetime</c> 建立），这样命令行将来能加
/// <c>--file xxx.comp</c> 之类的开关而不必改这里。</para>
/// </remarks>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    /// <remarks>
    /// 只在桌面生命周期下创建窗口。单元测试若直接构造 <see cref="App"/>，
    /// 生命周期会是 <see langword="null"/>，此时保持"什么都不做"——不抛异常，
    /// 这样测试可以在没有窗口服务器的环境里实例化 <see cref="App"/>。
    /// </remarks>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow ??= new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}