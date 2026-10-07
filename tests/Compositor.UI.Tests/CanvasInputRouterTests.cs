using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Compositor.Core;
using Compositor.UI;
using Xunit;

namespace Compositor.UI.Tests;

/// <summary>
/// <see cref="CanvasInputRouter"/> 的行为测试。
/// </summary>
/// <remarks>
/// <para>每个用例都注明对应的 Swift 行号
/// （<c>_eval_Compositor\Compositor-main\Compositor\Rendering\EditorCanvas.swift</c>）。</para>
/// <para><b>🔴 铁律 1 专项</b>：本文件含一组 <c>ZeroFlip_*</c> 用例，专门断言<b>没有发生 Y 翻转</b>。
/// 这类 bug 肉眼截图看不出，必须靠显式断言。</para>
/// <para><b>⚠️ Avalonia 12 实测纠正</b>（与 11.x 记忆不同，勿照抄旧写法）：
/// <list type="bullet">
/// <item>滚轮事件类型是 <c>PointerWheelEventArgs</c>，<b>不是</b> <c>PointerWheelChangedEventArgs</c>。</item>
/// <item><c>PointerEventArgs</c> <b>没有</b> <c>Point</c> 属性；位置只经构造函数传入。</item>
/// <item><c>PointerEventArgs.Properties</c> 是<b>只读</b>，只能经构造函数设置。</item>
/// <item><c>KeyModifiers</c> 只有 None/Alt/Control/Shift/Meta 五个值。</item>
/// </list>
/// </para>
/// </remarks>
// 注：刻意不加 [Collection(...)]。本仓无对应的 CollectionDefinition，
// 挂一个空集合名只会让人误以为存在共享 fixture（本类不共享任何状态）。
public sealed class CanvasInputRouterTests
{
    /// <summary>测试用文档尺寸，喂给契约 <c>NullDocument</c>。</summary>
    private static readonly DocSize TestDocSize = new(1000, 800);

    private static readonly Viewport TestViewport = new(0, 0, 1.0, 1.0);

    // ═══════════════ 修饰键归一化 ═══════════════

    /// <summary>
    /// 对应拆解文档 §5.4：<c>ToolInput.Ctrl</c> ≡ Mac 的 <c>.command</c>（v1.2 裁决，禁止改名）。
    /// </summary>
    [Fact]
    public void Normalize_MapsControlAndMetaToCtrl()
    {
        Assert.True(CanvasInputRouter.Normalize(KeyModifiers.Control).Ctrl);
        Assert.True(CanvasInputRouter.Normalize(KeyModifiers.Meta).Ctrl);
        Assert.False(CanvasInputRouter.Normalize(KeyModifiers.None).Ctrl);
    }

    /// <summary>Mac 的 <c>.option</c> → Avalonia <c>Alt</c> → <c>ToolInput.Alt</c>。</summary>
    [Fact]
    public void Normalize_MapsAltToAlt()
    {
        Assert.True(CanvasInputRouter.Normalize(KeyModifiers.Alt).Alt);
        Assert.False(CanvasInputRouter.Normalize(KeyModifiers.Control).Alt);
    }

    [Fact]
    public void Normalize_MapsShiftToShift()
        => Assert.True(CanvasInputRouter.Normalize(KeyModifiers.Shift).Shift);

    [Fact]
    public void Normalize_HandlesCombinations()
    {
        var input = CanvasInputRouter.Normalize(
            KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt, 7);

        Assert.True(input.Ctrl);
        Assert.True(input.Shift);
        Assert.True(input.Alt);
        Assert.Equal(7, input.PointerId);
    }

    // ═══════════════ 零翻转（铁律 1）═══════════════

    /// <summary>
    /// <b>铁律 1 核心断言</b>：视图 → 文档换算<b>不得翻转 Y</b>。
    /// </summary>
    /// <remarks>
    /// Avalonia 与 AppKit 一样 Y 向下，所以 <c>DocCoord.FromView</c> 是纯线性。
    /// 若有人加了 <c>ScaleTransform(1,-1)</c>，本用例立刻红。
    /// </remarks>
    [Fact]
    public void ZeroFlip_ViewToDocumentDoesNotFlipY()
    {
        var top = CanvasInputRouter.ToDocument(new ViewPoint(100, 0), TestViewport);
        var bottom = CanvasInputRouter.ToDocument(new ViewPoint(100, 300), TestViewport);

        Assert.Equal(0.0, top.Y, 12);
        Assert.Equal(300.0, bottom.Y, 12);
        Assert.True(bottom.Y > top.Y, "视图 Y 越大，文档 Y 必须越大——反转即违反铁律 1");
    }

    /// <summary>带非零 origin 与缩放时仍不得翻转。</summary>
    [Fact]
    public void ZeroFlip_DoesNotFlipWithScaledOffsetViewport()
    {
        // origin=(100,50), ppp=2：文档 (0,0) → 视图 (100,50)
        var viewport = new Viewport(100, 50, 2.0, 1.0);

        var docTop = CanvasInputRouter.ToDocument(new ViewPoint(100, 50), viewport);
        var docBottom = CanvasInputRouter.ToDocument(new ViewPoint(100, 250), viewport);

        Assert.Equal(0.0, docTop.Y, 12);
        Assert.Equal(100.0, docBottom.Y, 12);
        Assert.True(docBottom.Y > docTop.Y, "缩放下同样不得翻转 Y");
    }

    /// <summary>文档 → 视图方向同样不翻转（对应 <c>CanvasViewport.swift:30-33</c>）。</summary>
    [Fact]
    public void ZeroFlip_DocumentToViewDoesNotFlipY()
    {
        var viewOfTop = DocCoord.ToView(new DocPoint(0, 0), TestViewport);
        var viewOfBottom = DocCoord.ToView(new DocPoint(0, 200), TestViewport);

        Assert.True(viewOfBottom.Y > viewOfTop.Y, "文档 Y 越大视图 Y 必须越大");
        Assert.Equal(200.0, viewOfBottom.Y - viewOfTop.Y, 12);
    }

    /// <summary>端到端往返，Y 分量不得取反。</summary>
    [Fact]
    public void ZeroFlip_RoundTripPreservesY()
    {
        var viewport = new Viewport(37, -19, 3.0, 1.0);
        var start = new ViewPoint(411, 733);

        var doc = CanvasInputRouter.ToDocument(start, viewport);
        var back = DocCoord.ToView(doc, viewport);

        Assert.Equal(start.X, back.X, 9);
        Assert.Equal(start.Y, back.Y, 9);
    }

    // ═══════════════ 导航守卫（EditorCanvas.swift:2109 / 2122）═══════════════

    /// <summary>
    /// 拖动进行中禁止缩放 / 平移。对应
    /// <c>guard transformDrag == nil, cropDrag == nil, !guideDragging,
    /// session.brushStroke == nil, session.warpStroke == nil else { return }</c>。
    /// </summary>
    [Theory]
    [InlineData(NavigationLockReason.TransformDrag)]
    [InlineData(NavigationLockReason.CropDrag)]
    [InlineData(NavigationLockReason.GuideDrag)]
    [InlineData(NavigationLockReason.BrushStroke)]
    [InlineData(NavigationLockReason.WarpStroke)]
    public void Navigation_BlockedWhileDragging(NavigationLockReason reason)
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);
        router.SetNavigationLock(reason, locked: true);

        var wheel = router.PointerWheelled(0, 120, KeyModifiers.Control);
        Assert.Equal(InputRouteKind.NavigationBlocked, wheel.Kind);
        Assert.Equal(reason, wheel.LockReason);

        var magnify = router.Magnify(0.1, new ViewPoint(100, 100));
        Assert.Equal(InputRouteKind.NavigationBlocked, magnify.Kind);
    }

    [Fact]
    public void Navigation_AllowedWhenNotLocked()
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);

        Assert.Equal(InputRouteKind.ZoomByWheel,
            router.PointerWheelled(0, 120, KeyModifiers.Control).Kind);
        Assert.Equal(InputRouteKind.PanByWheel,
            router.PointerWheelled(0, 120, KeyModifiers.None).Kind);
    }

    [Fact]
    public void Navigation_RecoversAfterUnlock()
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);

        router.SetNavigationLock(NavigationLockReason.BrushStroke, locked: true);
        Assert.Equal(InputRouteKind.NavigationBlocked,
            router.PointerWheelled(0, 120, KeyModifiers.Control).Kind);

        router.SetNavigationLock(NavigationLockReason.None, locked: false);
        Assert.Equal(InputRouteKind.ZoomByWheel,
            router.PointerWheelled(0, 120, KeyModifiers.Control).Kind);
        Assert.Equal(NavigationLockReason.None, router.NavigationLock);
    }

    // ═══════════════ 滚轮（EditorCanvas.swift:2108-2120）═══════════════

    /// <summary>对应 <c>2111</c>：Cmd（契约 Ctrl）或 Option（Alt）→ 缩放。</summary>
    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Alt)]
    public void Wheel_WithModifierZooms(KeyModifiers modifiers)
        => Assert.Equal(InputRouteKind.ZoomByWheel,
            new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport)
                .PointerWheelled(0, 120, modifiers).Kind);

    /// <summary>对应 <c>2114</c>：无修饰键 → 平移。</summary>
    [Fact]
    public void Wheel_WithoutModifierPans()
    {
        var route = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport)
            .PointerWheelled(15, -30, KeyModifiers.Shift);

        Assert.Equal(InputRouteKind.PanByWheel, route.Kind);
        Assert.Equal(15.0, route.WheelDeltaX, 12);
        Assert.Equal(-30.0, route.WheelDeltaY, 12);
    }

    /// <summary>
    /// <b>滚轮方向纪律</b>：本层<b>不预先加负号</b>，原始符号透传给上层。
    /// </summary>
    /// <remarks>
    /// 原实现是 <c>exp(-deltaY * 0.015)</c>（<c>EditorCanvas.swift:2112</c>），
    /// 而 Avalonia 的 <c>Delta.Y</c> 符号约定与 macOS 不同。
    /// 真机实测前<b>不做补偿</b>——补错了比不补更难查。本用例把纪律锁死。
    /// </remarks>
    [Theory]
    [InlineData(120.0)]
    [InlineData(-120.0)]
    public void Wheel_PreservesRawSignForRealDeviceCalibration(double rawDelta)
    {
        var route = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport)
            .PointerWheelled(0, rawDelta, KeyModifiers.Control);

        Assert.Equal(rawDelta, route.WheelDeltaY, 12);
    }

    // ═══════════════ 捏合（EditorCanvas.swift:2121-2125）═══════════════

    /// <summary>对应 <c>2123</c>：<c>zoom * (1 + magnification)</c>。</summary>
    [Fact]
    public void Magnify_ProducesMultiplierFromMagnification()
    {
        var route = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport)
            .Magnify(0.25, new ViewPoint(320, 240));

        Assert.Equal(InputRouteKind.ZoomByMagnification, route.Kind);
        Assert.Equal(1.25, route.Magnification, 9);
        Assert.Equal(320.0, route.ViewPosition!.Value.X, 9);
        Assert.Equal(240.0, route.ViewPosition!.Value.Y, 9);
    }

    /// <summary><b>铁律 1</b>：锚点原样透传，Y 不取反。</summary>
    [Fact]
    public void Magnify_PreservesAnchorY()
    {
        var route = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport)
            .Magnify(0.1, new ViewPoint(50, 900));

        Assert.Equal(900.0, route.ViewPosition!.Value.Y, 9);
    }

    // ═══════════════ 键盘（EditorCanvas.swift:2126 / 2249）═══════════════

    /// <summary>
    /// 对应 <c>2134</c>：<b>每次按键都重读修饰键</b>，不信任增量状态。
    /// </summary>
    /// <remarks>
    /// 原注释（<c>2132-2133</c>）记录了真实 bug：拖拽会吞掉 <c>flagsChanged</c>，
    /// 导致画布以为 Option 还按着，连带让吸管无法顶替画笔。
    /// </remarks>
    [Fact]
    public void KeyDown_AlwaysReportsCurrentModifiers()
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);

        var withAlt = router.KeyDown(new KeyEventArgs
        {
            Key = Key.B,
            KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
        });

        Assert.True(withAlt.Modifiers.Alt, "Option 按下必须被读到");
        Assert.True(withAlt.Modifiers.Shift);
        Assert.Equal(Key.B, withAlt.Key);

        var withoutAlt = router.KeyDown(new KeyEventArgs
        {
            Key = Key.B,
            KeyModifiers = KeyModifiers.None,
        });

        Assert.False(withoutAlt.Modifiers.Alt,
            "Option 已释放——沿用旧快照即为原项目修过的那个 bug");
    }

    /// <summary>
    /// 键盘事件<b>不硬编码键码</b>：原样透传，由上层 ShortcutSettings 重映射
    /// （对应 <c>EditorCanvas.swift:2128</c> 的 <c>ShortcutSettings.shared.canvasEvent</c>）。
    /// </summary>
    [Fact]
    public void KeyDown_PassesKeyThroughWithoutHardcodedMapping()
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);

        foreach (Key k in new[] { Key.V, Key.M, Key.B, Key.Z, Key.Space })
        {
            var route = router.KeyDown(new KeyEventArgs { Key = k, KeyModifiers = KeyModifiers.None });
            Assert.Equal(InputRouteKind.KeyDown, route.Kind);
            Assert.Equal(k, route.Key);
        }
    }

    [Fact]
    public void KeyUp_ReportsKeyRelease()
    {
        var route = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport)
            .KeyUp(new KeyEventArgs { Key = Key.Space, KeyModifiers = KeyModifiers.None });

        Assert.Equal(InputRouteKind.KeyUp, route.Kind);
        Assert.Equal(Key.Space, route.Key);
    }

    // ═══════════════ 无文档守卫（EditorCanvas.swift:1741）═══════════════

    /// <summary>对应 <c>guard session.document != nil</c>：无文档时不处理指针按下。</summary>
    [Fact]
    public void PointerPressed_IgnoredWhenDocumentIsNull()
    {
        var router = new CanvasInputRouter(null!, TestViewport);
        var route = router.PointerPressed(new PointerPressedEventArgs(
            source: null!, pointer: null!, rootVisual: null!,
            rootVisualPosition: default, timestamp: 0,
            properties: new PointerPointProperties(), modifiers: KeyModifiers.None, clickCount: 1));

        Assert.Equal(InputRouteKind.Ignored, route.Kind);
    }

    /// <summary>未 <c>AttachTo</c> 就处理事件必须明确报错，而不是静默用错误坐标系。</summary>
    /// <remarks>
    /// <b>⚠️ Avalonia 12 实测</b>：<c>PointerEventArgs</c> 是可直接实例化的基类，
    /// <b>没有</b> <c>PointerMovedEventArgs</c> 这个类型（v12 的 Pointer 事件类型只有
    /// <c>PointerPressedEventArgs</c> / <c>PointerReleasedEventArgs</c> /
    /// <c>PointerWheelEventArgs</c> / <c>PointerDeltaEventArgs</c> / <c>PointerCaptureLostEventArgs</c>）。
    /// </remarks>
    [Fact]
    public void AttachTo_RequiredBeforePointerMoved()
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);

        Assert.Throws<InvalidOperationException>(() => router.PointerMoved(
            new PointerPressedEventArgs(
                source: null!, pointer: null!, rootVisual: null!,
                rootVisualPosition: default, timestamp: 0,
                properties: new PointerPointProperties(), modifiers: KeyModifiers.None, clickCount: 1)));
    }

    [Fact]
    public void AttachTo_RejectsNull()
    {
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), TestViewport);
        Assert.Throws<ArgumentNullException>(() => router.AttachTo(null!));
    }
}
