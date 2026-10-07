using Avalonia.Input;
using Compositor.Core;

namespace Compositor.UI;

/// <summary>
/// 交互事件路由的<b>结果</b>类型。不可变 record，便于测试断言与日志。
/// </summary>
/// <remarks>
/// 设计意图：把「事件归一化」与「业务行为」彻底分开。
/// 原项目 <c>EditorCanvas.swift</c> 的 <c>mouseDown:1737</c> 长达 106 行、
/// <c>mouseDragged:1843</c> 156 行，业务分支与事件解析交织。
/// 本层只回答「发生了什么」，不回答「该做什么」——
/// 后者由 AI-3/AI-4/AI-5 的工具实现消费。
/// </remarks>
public readonly record struct InputRoute
{
    /// <summary>路由种类。</summary>
    public InputRouteKind Kind { get; init; }

    /// <summary>被忽略的原因，仅 <see cref="InputRouteKind.Ignored"/> 时有意义。</summary>
    public string? Reason { get; init; }

    /// <summary>归一化后的工具输入（按下 / 抬起类事件）。</summary>
    public ToolInput? ToolInput { get; init; }

    /// <summary>视图坐标下的点（按下 / 双击）。</summary>
    public ViewPoint? ViewPosition { get; init; }

    /// <summary>增量（平移类）。</summary>
    public ViewPoint? Delta { get; init; }

    /// <summary>滚轮原始增量 Y。<b>符号待真机校准</b>，见 <see cref="CanvasInputRouter.PointerWheelled"/>。</summary>
    public double WheelDeltaY { get; init; }

    /// <summary>滚轮原始增量 X。</summary>
    public double WheelDeltaX { get; init; }

    /// <summary>捏合倍率（<c>1 + magnification</c>）。</summary>
    public double Magnification { get; init; }

    /// <summary>按键（键盘类）。</summary>
    public Key? Key { get; init; }

    /// <summary>修饰键快照（键盘类）。</summary>
    public ModifierState Modifiers { get; init; }

    /// <summary>导航锁原因（被阻止时）。</summary>
    public NavigationLockReason LockReason { get; init; }

    // ═══════════════ 工厂 ═══════════════

    /// <summary>左键按下开始。</summary>
    /// <param name="input">工具输入。</param>
    /// <param name="position">视图坐标。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute StartedLeft(ToolInput input, ViewPoint position)
        => new() { Kind = InputRouteKind.StartedLeft, ToolInput = input, ViewPosition = position };

    /// <summary>双击开始（对应 <c>EditorCanvas.swift:1836</c>）。</summary>
    /// <param name="input">工具输入。</param>
    /// <param name="position">视图坐标。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute StartedDoubleClick(ToolInput input, ViewPoint position)
        => new() { Kind = InputRouteKind.StartedDoubleClick, ToolInput = input, ViewPosition = position };

    /// <summary>中键平移开始（对应 <c>EditorCanvas.swift:2003</c>）。</summary>
    /// <returns>路由结果。</returns>
    public static InputRoute StartedMiddlePan => new() { Kind = InputRouteKind.StartedMiddlePan };

    /// <summary>中键平移增量。</summary>
    /// <param name="delta">视图坐标增量。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute Panning(ViewPoint delta)
        => new() { Kind = InputRouteKind.Panning, Delta = delta };

    /// <summary>左键拖动增量。</summary>
    /// <param name="delta">视图坐标增量。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute LeftDelta(ViewPoint delta)
        => new() { Kind = InputRouteKind.LeftDelta, Delta = delta };

    /// <summary>左键抬起。</summary>
    /// <param name="input">工具输入。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute EndedLeft(ToolInput input)
        => new() { Kind = InputRouteKind.EndedLeft, ToolInput = input };

    /// <summary>中键平移结束。</summary>
    /// <returns>路由结果。</returns>
    public static InputRoute EndedMiddlePan => new() { Kind = InputRouteKind.EndedMiddlePan };

    /// <summary>无拖动（悬停）。</summary>
    /// <returns>路由结果。</returns>
    public static InputRoute NoDrag => new() { Kind = InputRouteKind.NoDrag };

    /// <summary>滚轮缩放。</summary>
    /// <param name="deltaY">原始增量 Y，符号未做预处理。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute ZoomByWheel(double deltaY)
        => new() { Kind = InputRouteKind.ZoomByWheel, WheelDeltaY = deltaY };

    /// <summary>滚轮平移。</summary>
    /// <param name="deltaX">增量 X。</param>
    /// <param name="deltaY">增量 Y。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute PanByWheel(double deltaX, double deltaY)
        => new() { Kind = InputRouteKind.PanByWheel, WheelDeltaX = deltaX, WheelDeltaY = deltaY };

    /// <summary>捏合缩放。</summary>
    /// <param name="multiplier">倍率（<c>1 + magnification</c>）。</param>
    /// <param name="anchor">锚点。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute ZoomByMagnification(double multiplier, ViewPoint anchor)
        => new() { Kind = InputRouteKind.ZoomByMagnification, Magnification = multiplier, ViewPosition = anchor };

    /// <summary>按键按下。</summary>
    /// <param name="key">按键。</param>
    /// <param name="modifiers">修饰键快照。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute KeyDownOf(Key key, ModifierState modifiers)
        => new() { Kind = InputRouteKind.KeyDown, Key = key, Modifiers = modifiers };

    /// <summary>按键抬起。</summary>
    /// <param name="key">按键。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute KeyUpOf(Key key)
        => new() { Kind = InputRouteKind.KeyUp, Key = key };

    /// <summary>导航被阻止（对应 <c>EditorCanvas.swift:2109</c> 守卫）。</summary>
    /// <param name="reason">锁原因。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute NavigationBlocked(NavigationLockReason reason)
        => new() { Kind = InputRouteKind.NavigationBlocked, LockReason = reason };

    /// <summary>被忽略。</summary>
    /// <param name="reason">原因。</param>
    /// <returns>路由结果。</returns>
    public static InputRoute Ignored(string reason)
        => new() { Kind = InputRouteKind.Ignored, Reason = reason };
}

/// <summary>路由种类。</summary>
public enum InputRouteKind
{
    /// <summary>事件被忽略（如无文档）。</summary>
    Ignored,

    /// <summary>左键按下开始。</summary>
    StartedLeft,

    /// <summary>双击开始。</summary>
    StartedDoubleClick,

    /// <summary>中键平移开始。</summary>
    StartedMiddlePan,

    /// <summary>中键平移中。</summary>
    Panning,

    /// <summary>左键拖动中。</summary>
    LeftDelta,

    /// <summary>左键抬起。</summary>
    EndedLeft,

    /// <summary>中键平移结束。</summary>
    EndedMiddlePan,

    /// <summary>无拖动（悬停）。</summary>
    NoDrag,

    /// <summary>滚轮缩放。</summary>
    ZoomByWheel,

    /// <summary>滚轮平移。</summary>
    PanByWheel,

    /// <summary>捏合缩放。</summary>
    ZoomByMagnification,

    /// <summary>按键按下。</summary>
    KeyDown,

    /// <summary>按键抬起。</summary>
    KeyUp,

    /// <summary>导航被阻止。</summary>
    NavigationBlocked,
}