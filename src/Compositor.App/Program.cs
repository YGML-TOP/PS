using Avalonia;

namespace Compositor.App;

/// <summary>进程入口。负责构造 Avalonia 应用并启动经典的桌面生命周期。</summary>
/// <remarks>
/// <para>这是 Avalonia 桌面应用的固定三段式：<see cref="BuildAvaloniaApp"/> 由
/// <c>Avalonia.Desktop</c> 提供平台探测（Win32/X11/macOS），
/// <c>StartWithClassicDesktopLifetime</c> 把主循环挂到当前线程上。</para>
/// <para>入口类是 <c>internal</c> 而不是 <c>public</c>：C# 的入口点不要求可见性，
/// 而公开它会让 CS1591（缺 XML 注释）在没有收益的情况下多一个必须维护的成员。</para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// 进程主入口。
    /// </summary>
    /// <param name="args">平台相关参数；本骨架不消费，原样交给 Avalonia 生命周期。</param>
    /// <remarks>
    /// 🔴 <c>OutputType=WinExe</c> 意味着没有控制台窗口，
    /// 因此这里的异常<b>不会</b>弹到任何地方——它会直接杀进程。
    /// 所以真正需要观察运行期错误时，要靠调试器或事件查看器，
    /// 不要指望 stdout。本项目在验收时用「进程存活 + 无未处理异常」间接判断。
    /// </remarks>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// 构造未初始化的 <see cref="AppBuilder"/>。Avalonia 约定用 <c>static</c> 无参方法，
    /// 供 <c>Avalonia.Desktop</c> 与设计器在运行时反射调用。
    /// </summary>
    /// <returns>已经绑定 <see cref="App"/> 类型、但尚未启动的配置器。</returns>
    /// <remarks>
    /// 这里刻意<b>不</b>注册任何内嵌字体：内嵌字体要额外的 <c>Avalonia.Fonts.*</c> 包，
    /// 而本工程只允许 Avalonia 官方包、且没必要为一个骨架背一份字体资源。
    /// 不注册时 Avalonia 回落到操作系统的默认字体族（Windows 上是 Segoe UI），控件文字正常显示。
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}