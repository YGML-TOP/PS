using Avalonia;
using Avalonia.Input;
using Compositor.Core;

namespace Compositor.UI;

/// <summary>
/// 画布指针 / 键盘事件的<b>分发骨架</b>。
/// </summary>
/// <remarks>
/// <para>对应原项目 <c>EditorCanvas.swift</c> 的段①（<c>1439–2572</c>）的事件入口：
/// <c>mouseDown:1737</c>、<c>mouseDragged:1843</c>、<c>otherMouseDown:2003</c>、
/// <c>mouseUp:2025</c>、<c>scrollWheel:2108</c>、<c>magnify:2121</c>、<c>keyDown:2126</c>、
/// <c>keyUp:2249</c>。</para>
/// <para><b>本类只做「事件归一化 + 优先级守卫 + 路由决策」</b>，
/// 不含任何具体工具的行为——工具逻辑归 AI-3/AI-4/AI-5。</para>
/// <para><b>🔴 铁律 1</b>：本类<b>不含任何 Y 翻转</b>。
/// Avalonia 的控件坐标系本就 Y 向下（左上原点），与文档坐标天然一致，
/// 等价物是<b>什么都不做</b>。绝不能出现 <c>ScaleTransform(1,-1)</c> 或 <c>height - y</c>。
/// 依据：原项目靠 <c>EditorCanvas.swift:670</c> 的 <c>override var isFlipped: Bool { true }</c>
/// 才得到 Y 向下——那是 AppKit 默认 Y 上向的补偿，Avalonia 不需要。</para>
/// <para><b>坐标边界</b>：本类对外只暴露 <see cref="DocPoint"/>；
/// <see cref="ViewPoint"/> 仅出现在方法签名里，进入后立即经契约
/// <see cref="DocCoord.FromView"/> 转换。</para>
/// </remarks>
public sealed class CanvasInputRouter
{
    private readonly IDocument _document;
    private readonly Viewport _viewport;
    private readonly Func<Viewport> _viewportProvider;

    // ═══════════════ 拖动会话状态 ═══════════════

    /// <summary>左键拖动起点（视图坐标）。对应 <c>lastDragPoint</c>（<c>EditorCanvas.swift:64</c>）。</summary>
    private ViewPoint? _leftDragOrigin;

    /// <summary>中键平移起点（视图坐标）。对应 <c>middlePanPoint</c>（<c>:66</c>）。</summary>
    private ViewPoint? _middlePanOrigin;

    /// <summary>正在进行的拖动类型。</summary>
    private DragKind _dragKind = DragKind.None;

    /// <summary>
    /// 缩放/平移是否被锁定。
    /// 对应 <c>scrollWheel:2109</c> 与 <c>magnify:2122</c> 的守卫：
    /// <c>guard transformDrag == nil, cropDrag == nil, !guideDragging,
    /// session.brushStroke == nil, session.warpStroke == nil else { return }</c>。
    /// </summary>
    private bool _navigationLocked;

    /// <summary>当前导航锁的原因，用于诊断与测试断言。</summary>
    public NavigationLockReason NavigationLock { get; private set; } = NavigationLockReason.None;

    /// <summary>
    /// 由上层工具/会话置位导航锁。
    /// 对应 <c>transformDrag</c> / <c>cropDrag</c> / <c>guideDragging</c> /
    /// <c>brushStroke</c> / <c>warpStroke</c> 五个守卫条件的统一抽象。
    /// </summary>
    public void SetNavigationLock(NavigationLockReason reason, bool locked)
    {
        NavigationLock = locked ? reason : NavigationLockReason.None;
        _navigationLocked = locked;
    }

    /// <summary>构造分发器。</summary>
    /// <param name="document">文档，用于查画布是否存在（对应 <c>guard session.document != nil</c>）。</param>
    /// <param name="viewport">当前视口。</param>
    public CanvasInputRouter(IDocument document, Viewport viewport)
    {
        _document = document;
        _viewport = viewport;
        _viewportProvider = () => viewport;
    }

    /// <summary>构造带动态视口的分发器。</summary>
    /// <param name="document">文档。</param>
    /// <param name="viewportProvider">视口提供者，每次事件读取最新值。</param>
    public CanvasInputRouter(IDocument document, Func<Viewport> viewportProvider)
    {
        _document = document;
        _viewport = viewportProvider();
        _viewportProvider = viewportProvider;
    }

    // ═══════════════ 修饰键归一化 ═══════════════

    /// <summary>
    /// 把 Avalonia 的 <see cref="KeyModifiers"/> 归一化成契约 <see cref="ToolInput"/>。
    /// </summary>
    /// <param name="modifiers">Avalonia 修饰键。</param>
    /// <param name="pointerId">指针设备 id。</param>
    /// <returns>契约工具输入。</returns>
    /// <remarks>
    /// <b>🔴 映射必须显式，不可按位直传</b>：
    /// <list type="bullet">
    /// <item>Mac <c>.option</c> → Avalonia <see cref="KeyModifiers.Alt"/> → <c>ToolInput.Alt</c>。</item>
    /// <item>Mac <c>.command</c> → 契约 <c>ToolInput.Ctrl</c>（v1.2 裁决：保留原名，
    /// 语义 <b>就是</b> Mac 的 Command，见拆解文档 §5.4）。</item>
    /// </list>
    /// <para><b>⚠️ 已知无法映射的一条</b>：Mac 的 <c>.control</c>（Control）语义是
    /// 「自由移动、禁吸附」（<c>EditorCanvas.swift:1978</c>）与
    /// 「Cmd 拖动=扭曲」并存（<c>:2544</c>）。契约 <see cref="ToolInput"/>
    /// 只有 <c>Shift/Alt/Ctrl</c> 三位且已被占满，<b>无位承载</b>。
    /// 已报总项目管理，见拆解文档 §5.5。<b>此处不私自塞进 Shift。</b></para>
    /// <para>Avalonia 的 <see cref="KeyModifiers"/> 实测只有
    /// <c>None/Alt/Control/Shift/Meta</c> 五个值。</para>
    /// </remarks>
    public static ToolInput Normalize(KeyModifiers modifiers, int pointerId = 0)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool alt = modifiers.HasFlag(KeyModifiers.Alt);
        bool ctrl = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

        return new ToolInput(shift, alt, ctrl, pointerId);
    }

    // ═══════════════ 坐标换算（唯一边界） ═══════════════

    /// <summary>
    /// 视图坐标 → 文档坐标。
    /// </summary>
    /// <param name="viewPoint">视图坐标（Y 向下）。</param>
    /// <param name="viewport">视口。</param>
    /// <returns>文档坐标（Y 向下）。</returns>
    /// <remarks>
    /// 全部经契约 <see cref="DocCoord.FromView"/>，<b>本类不做任何翻转</b>（铁律 1）。
    /// 对应 <c>session.viewport.documentPoint(from:documentSize:)</c> 在
    /// <c>EditorCanvas.swift</c> 中的 30 余处调用点。
    /// </remarks>
    public static DocPoint ToDocument(ViewPoint viewPoint, Viewport viewport)
        => DocCoord.FromView(viewPoint, viewport);

    // ═══════════════ 事件路由 ═══════════════

    /// <summary>
    /// 指针按下。
    /// </summary>
    /// <param name="e">Avalonia 指针按下事件（实测类型 <c>Avalonia.Input.PointerPressedEventArgs</c>）。</param>
    /// <returns>路由决策。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:1737</c> 的 <c>mouseDown</c>，
    /// 但只保留<b>可判定的结构</b>，业务分支（取样模式等）留给上层。
    /// <list type="bullet">
    /// <item><c>1741</c> 忙碌守卫：<c>guard document != nil &amp;&amp; !isProjectBusy &amp;&amp; !isImporting</c>。</item>
    /// <item><c>2003-2004</c> 中键优先于左键：<c>guard event.buttonNumber == 2</c>，
    /// 对应 Avalonia <see cref="PointerPointProperties.IsMiddleButtonPressed"/>。</item>
    /// <item><c>1836</c> 双击：<c>event.clickCount &gt;= 2</c>，
    /// 对应 Avalonia <see cref="PointerPressedEventArgs.ClickCount"/>（实测存在）。</item>
    /// </list>
    /// </remarks>
    public InputRoute PointerPressed(PointerPressedEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        // 对应 EditorCanvas.swift:1741 —— 文档不存在时不处理。
        if (_document is null)
        {
            return InputRoute.Ignored("无文档");
        }

        // 对应 EditorCanvas.swift:2004 —— 中键是平移入口，独立于左键工具。
        if (e.Properties.IsMiddleButtonPressed)
        {
            _middlePanOrigin = PositionOf(e);
            _dragKind = DragKind.MiddlePan;
            return InputRoute.StartedMiddlePan;
        }

        // 对应 EditorCanvas.swift:1739 —— 按下即记录 Option 状态。
        var input = Normalize(e.KeyModifiers, PointerIdOf(e));

        // 对应 EditorCanvas.swift:1836 —— 双击（仅 move 工具用于活文字编辑，由上层判定）。
        bool isDoubleClick = e.ClickCount >= 2;

        _leftDragOrigin = PositionOf(e);
        _dragKind = DragKind.LeftButton;

        return isDoubleClick
            ? InputRoute.StartedDoubleClick(input, _leftDragOrigin.Value)
            : InputRoute.StartedLeft(input, _leftDragOrigin.Value);
    }

    /// <summary>
    /// 指针移动。
    /// </summary>
    /// <param name="e">Avalonia 指针移动事件。</param>
    /// <returns>路由决策（含平移增量）。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:1843</c> 的 <c>mouseDragged</c>。
    /// <b>注意</b>：原实现有 19 个优先级分支（笔刷/套索/裁剪/变换/参考线…），
    /// 本骨架只保留<b>兜底的平移分支</b>（<c>:1995-1998</c>），
    /// 其余分支需等 AI-3/AI-4/AI-5 的工具就位后再接。
    /// </remarks>
    public InputRoute PointerMoved(PointerEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        ViewPoint current = PositionOf(e);

        // 对应 EditorCanvas.swift:2009-2014 —— 中键平移拥有独立拖点，
        // 不会干扰左键正在做的事。
        if (_middlePanOrigin is { } panStart)
        {
            var panDelta = new ViewPoint(current.X - panStart.X, current.Y - panStart.Y);
            _middlePanOrigin = current;
            return InputRoute.Panning(panDelta);
        }

        // 对应 EditorCanvas.swift:1995-1998 —— 兜底平移。
        if (_leftDragOrigin is { } dragStart && _dragKind == DragKind.LeftButton)
        {
            var delta = new ViewPoint(current.X - dragStart.X, current.Y - dragStart.Y);
            _leftDragOrigin = current;
            return InputRoute.LeftDelta(delta);
        }

        return InputRoute.NoDrag;
    }

    /// <summary>
    /// 指针抬起。
    /// </summary>
    /// <param name="e">Avalonia 指针抬起事件。</param>
    /// <returns>路由决策。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:2025</c> 的 <c>mouseUp</c>。
    /// <para>对应 <c>:2100</c> <c>lastDragPoint = nil</c> 与
    /// <c>:2094</c> <c>cropDrag = nil</c> 等状态清理。</para>
    /// </remarks>
    public InputRoute PointerReleased(PointerReleasedEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        var input = Normalize(e.KeyModifiers, PointerIdOf(e));
        bool wasMiddlePan = _dragKind == DragKind.MiddlePan;

        _leftDragOrigin = null;
        _middlePanOrigin = null;
        _dragKind = DragKind.None;

        return wasMiddlePan ? InputRoute.EndedMiddlePan : InputRoute.EndedLeft(input);
    }

    /// <summary>
    /// 滚轮。
    /// </summary>
    /// <param name="deltaX">
    /// 滚轮 X 增量（<c>Avalonia.Input.PointerWheelEventArgs.Delta.X</c>，实测为 <c>Avalonia.Vector</c>）。
    /// </param>
    /// <param name="deltaY">
    /// 滚轮 Y 增量。⚠️ <b>实测</b>：Avalonia 12 的类型是
    /// <c>Avalonia.Input.PointerWheelEventArgs</c>（<b>不是</b> 11.x 的
    /// <c>PointerWheelChangedEventArgs</c>），其 <c>Delta</c> 是 <c>Avalonia.Vector</c>。
    /// </param>
    /// <param name="modifiers">修饰键。</param>
    /// <returns>缩放或平移指令。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:2108-2120</c>：
    /// <list type="bullet">
    /// <item><c>2109</c> 守卫：拖动进行中直接 return（<see cref="NavigationLock"/>）。</item>
    /// <item><c>2111</c> <b>Cmd 或 Option</b> → 缩放，锚点为指针位置。</item>
    /// <item><c>2114-2118</c> 否则 → 平移；<c>2115</c> 非精确滚轮乘 12。</item>
    /// </list>
    /// <para><b>⚠️ 方向必须真机实测</b>：原实现是 <c>exp(-deltaY * 0.015)</c>，
    /// 而 Avalonia 的 <c>Delta.Y</c> 符号约定与 macOS 不同。
    /// 本方法<b>不预先加负号</b>，把符号判定留给真机验证——
    /// 补错了比不补更难查。</para>
    /// </remarks>
    public InputRoute PointerWheelled(double deltaX, double deltaY, KeyModifiers modifiers)
    {
        // 对应 EditorCanvas.swift:2109 —— 拖动中禁止缩放/平移。
        if (_navigationLocked)
        {
            return InputRoute.NavigationBlocked(NavigationLock);
        }

        bool wantsZoom = modifiers.HasFlag(KeyModifiers.Control)
                         || modifiers.HasFlag(KeyModifiers.Meta)
                         || modifiers.HasFlag(KeyModifiers.Alt);

        if (wantsZoom)
        {
            // 对应 EditorCanvas.swift:2112 —— 原始 delta 透传，符号待真机校准。
            return InputRoute.ZoomByWheel(deltaY);
        }

        // 对应 EditorCanvas.swift:2115 —— 非精确滚轮（滚轮而非触控板）乘 12。
        return InputRoute.PanByWheel(deltaX, deltaY);
    }

    /// <summary>
    /// 捏合缩放（触控板）。
    /// </summary>
    /// <param name="magnification">捏合倍率增量。对应 <c>NSEvent.magnification</c>。</param>
    /// <param name="anchor">锚点（视图坐标）。</param>
    /// <returns>缩放指令。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:2121-2125</c>：
    /// <c>session.zoom(to: zoom * (1 + magnification), anchor: 指针位置)</c>。
    /// 守卫与滚轮相同（<c>:2122</c>）。
    /// </remarks>
    public InputRoute Magnify(double magnification, ViewPoint anchor)
    {
        if (_navigationLocked)
        {
            return InputRoute.NavigationBlocked(NavigationLock);
        }

        return InputRoute.ZoomByMagnification(1.0 + magnification, anchor);
    }

    /// <summary>
    /// 键盘按下。
    /// </summary>
    /// <param name="e">Avalonia 键按下事件。</param>
    /// <returns>键盘指令。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:2126</c> 的 <c>keyDown</c>。
    /// <para><b>🔴 纪律</b>：原实现 <c>2128</c> 经
    /// <c>ShortcutSettings.shared.canvasEvent(event)</c> 做<b>快捷键重映射</b>，
    /// <b>禁止硬编码键码</b>（任务书 §6-3 要求可重映射）。
    /// 本方法只产出「原始按键 + 修饰键」，重映射由上层
    /// <c>ShortcutSettings</c> 完成。</para>
    /// <para><b>🔴 必须移植的行为</b>：<c>2134</c> <b>每次按键都重读 Option 状态</b>。
    /// 注释（<c>2132-2133</c>）说明：拖拽会吞掉 <c>flagsChanged</c>，
    /// 导致画布以为 Option 还按着，连带让吸管无法顶替画笔。
    /// 这是实测踩过的坑，本方法用 <see cref="ModifierState"/> 显式建模。</para>
    /// </remarks>
    public InputRoute KeyDown(KeyEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        // 对应 EditorCanvas.swift:2134 —— 每次按键重读修饰键，不信任增量状态。
        bool optionHeld = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrlHeld = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        return InputRoute.KeyDownOf(e.Key, new ModifierState(shiftHeld, optionHeld, ctrlHeld));
    }

    /// <summary>
    /// 键盘抬起。
    /// </summary>
    /// <param name="e">Avalonia 键抬起事件。</param>
    /// <returns>键盘抬起指令。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:2249</c> 的 <c>keyUp</c>。
    /// 原实现的主要工作是清理 <c>spaceHeld</c> / <c>panPhysicalKey</c> 等按住态
    /// （对应属性区 <c>:55-56</c>），<c>2134</c> 之外还要重读 Option。
    /// </remarks>
    public InputRoute KeyUp(KeyEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        return InputRoute.KeyUpOf(e.Key);
    }

    // ═══════════════ 内部 ═══════════════

    /// <summary>
    /// 事件源控件。用于 <c>GetPosition(this)</c> 换算。
    /// </summary>
    /// <remarks>
    /// Avalonia 的 <c>GetPosition(IInputElement)</c> 返回相对该元素的<b>视图坐标</b>，
    /// 与 AppKit 的 <c>convert(_:from:nil)</c>（<c>EditorCanvas.swift:1742</c>）语义一致。
    /// 两者都是 Y 向下，<b>无需翻转</b>（铁律 1）。
    /// </remarks>
    private Visual? _source;

    /// <summary>
    /// 取事件相对画布的视图坐标。
    /// </summary>
    /// <param name="e">指针事件。</param>
    /// <returns>视图坐标（Y 向下）。</returns>
    /// <exception cref="InvalidOperationException">尚未 <see cref="AttachTo"/>。</exception>
    /// <remarks>
    /// Avalonia 的 <c>GetPosition(Visual?)</c> 返回相对该元素的<b>视图坐标</b>，
    /// 与 AppKit 的 <c>convert(_:from:nil)</c>（<c>EditorCanvas.swift:1742</c>）语义一致。
    /// 两者都是 Y 向下，<b>无需翻转</b>（铁律 1）——
    /// 这里只做类型转换 <see cref="Avalonia.Point"/> → <see cref="ViewPoint"/>，
    /// <b>不做任何坐标变换</b>。
    /// </remarks>
    private ViewPoint PositionOf(PointerEventArgs e)
    {
        if (_source is null)
        {
            throw new InvalidOperationException(
                "尚未 AttachTo：必须先设置事件源控件，否则 GetPosition 无法定位坐标系。");
        }

        Avalonia.Point p = e.GetPosition(_source);
        return new ViewPoint(p.X, p.Y);
    }

    /// <summary>设置事件源控件。必须在处理事件前调用。</summary>
    /// <param name="source">接收指针事件的控件，通常是画布本身。</param>
    public void AttachTo(Visual source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>取指针设备 id。对应契约 <see cref="ToolInput.PointerId"/>。</summary>
    /// <param name="e">指针事件。</param>
    /// <returns>设备 id；无法取得时返回 0。</returns>
    private static int PointerIdOf(PointerEventArgs e)
        => e.Pointer?.Id.GetHashCode() ?? 0;

    /// <summary>拖动类型。</summary>
    private enum DragKind
    {
        /// <summary>无拖动。</summary>
        None,

        /// <summary>左键拖动。</summary>
        LeftButton,

        /// <summary>中键平移（对应 <c>EditorCanvas.swift:2003</c>）。</summary>
        MiddlePan,
    }
}

/// <summary>
/// 导航被锁定的原因。对应 <c>EditorCanvas.swift:2109</c> / <c>:2122</c> 的五个守卫条件。
/// </summary>
public enum NavigationLockReason
{
    /// <summary>未锁定。</summary>
    None = 0,

    /// <summary>变换拖动中（<c>transformDrag != nil</c>，<c>:1953</c> 附近）。</summary>
    TransformDrag,

    /// <summary>裁剪拖动中（<c>cropDrag != nil</c>，<c>:2455</c>）。</summary>
    CropDrag,

    /// <summary>参考线拖动中（<c>guideDragging</c>，<c>:2486</c>）。</summary>
    GuideDrag,

    /// <summary>笔刷绘制中（<c>brushStroke != nil</c>）。</summary>
    BrushStroke,

    /// <summary>液化绘制中（<c>warpStroke != nil</c>）。</summary>
    WarpStroke,
}

/// <summary>
/// 修饰键状态快照。
/// </summary>
/// <param name="Shift">Shift 按下。</param>
/// <param name="Alt">Option / Alt 按下（Mac 的 <c>.option</c>）。</param>
/// <param name="Ctrl">
/// Command / Control / Meta 按下。语义见 <see cref="CanvasInputRouter.Normalize"/> 的说明。
/// </param>
public readonly record struct ModifierState(bool Shift, bool Alt, bool Ctrl);