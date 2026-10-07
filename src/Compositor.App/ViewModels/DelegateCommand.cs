using System.Windows.Input;

namespace Compositor.App.ViewModels;

/// <summary>
/// 最小的 <see cref="ICommand"/> 实现，把一个委托包成命令。
/// </summary>
/// <remarks>
/// 刻意不引第三方 MVVM 库：Avalonia 的 <c>Button.Command</c> / <c>MenuItem.Command</c>
/// 只要一个 <see cref="ICommand"/>，为它拉一整个 ReactiveUI 不划算。
/// </remarks>
public sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>用两个委托构造命令。</summary>
    /// <param name="execute">执行动作。</param>
    /// <param name="canExecute">可用性判定；为 <see langword="null"/> 时恒为可用。</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> 为 <see langword="null"/>。</exception>
    public DelegateCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();

    /// <summary>主动触发 <see cref="CanExecuteChanged"/>，让绑定重新查询可用性。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}