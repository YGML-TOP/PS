using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Compositor.Core;
using Compositor.UI;
using Xunit;

namespace Compositor.UI.Tests;

/// <summary>
/// Avalonia 控件坐标系的方向验证（<b>铁律 1 的地基</b>）。
/// </summary>
/// <remarks>
/// <para>本文件<b>刻意不使用 Avalonia Headless</b>，原因见
/// <see cref="AvaloniaHeadlessBlocker"/> 的说明。它改用<b>不依赖窗口</b>的
/// <c>Control.Bounds</c> 与 <see cref="Visual.TranslatePoint"/>，
/// 在纯单元层面验证坐标系方向。</para>
/// </remarks>
public sealed class AvaloniaCoordinateSystemTests
{
    /// <summary>测试用文档尺寸，喂给契约 <c>NullDocument</c>。</summary>
    private static readonly DocSize TestDocSize = new(1000, 800);

    /// <summary>
    /// <b>铁律 1 的地基断言</b>：Avalonia 控件坐标系 Y 向下（左上原点）。
    /// </summary>
    /// <remarks>
    /// 这是「移植等价物 = 什么都不做」这一结论的<b>实证依据</b>。
    /// 对比：AppKit 默认 Y 向上，所以 Mac 版必须写 <c>isFlipped = true</c>
    /// （<c>EditorCanvas.swift:670</c>）；Avalonia 不需要。
    /// <para><b>⚠️ 若此断言失败，「零翻转」策略必须重估</b>——
    /// 那意味着 Avalonia 存在 Y 向上的特例控件。</para>
    /// </remarks>
    [Fact]
    public void Control_BoundsUseTopLeftOrigin()
    {
        var canvas = new Canvas { Width = 400, Height = 300 };

        // ⚠️ 必须先跑一次手动布局，Bounds 才有值。
        // Control.Bounds 是「已 Arrange 之后」的矩形，不是声明的 Width/Height 属性；
        // 未挂进视觉树、没跑过布局的控件，Bounds 恒为 Rect.Empty。
        //
        // 踩坑记录（首版实跑得到 Height=0 而非 300）：直接断言 Bounds.Height 会失败；
        // 更隐蔽的是 Bounds.Top / Bounds.Left 断言「通过」了——但那是因为
        // Rect.Empty 的 Top/Left 本来就是 0，属于假通过，零证明力。
        // 故下方先加前置守卫，确保断言的是真矩形。
        canvas.Measure(new Size(400, 300));
        canvas.Arrange(new Rect(0, 0, 400, 300));

        Assert.True(
            canvas.Bounds.Width > 0 && canvas.Bounds.Height > 0,
            $"Bounds 未被布局填充（Width={canvas.Bounds.Width}, Height={canvas.Bounds.Height}），"
            + "本用例退化为断言空矩形，失去意义");

        // Avalonia 的 Bounds.Top 恒为 0（左上原点），不像 WinForms/GDI+ 的 -Height
        Assert.Equal(0.0, canvas.Bounds.Top, 6);
        Assert.Equal(0.0, canvas.Bounds.Left, 6);
        Assert.Equal(300.0, canvas.Bounds.Height, 6);
        Assert.Equal(400.0, canvas.Bounds.Width, 6);

        // 反向证据：若是 GDI+/WinForms 那套下原点，Top 会是 -300。显式钉住。
        Assert.NotEqual(-300.0, canvas.Bounds.Top, 6);
    }

    /// <summary>
    /// 控件内点的 Y 越大，离控件顶端越远——即 Y 向下增长。
    /// </summary>
    /// <remarks>
    /// <c>TranslatePoint(point, visual)</c> 把点从自身坐标系换算到目标视觉的坐标系。
    /// 不需要窗口即可调用，因此绕开了 Dispatcher 线程限制。
    /// </remarks>
    [Fact]
    public void TranslatePoint_ConfirmsYDownDirection()
    {
        var canvas = new Canvas { Width = 400, Height = 300 };

        Avalonia.Point? top = canvas.TranslatePoint(new Point(10, 0), canvas);
        Avalonia.Point? bottom = canvas.TranslatePoint(new Point(10, 290), canvas);

        Assert.NotNull(top);
        Assert.NotNull(bottom);

        // Y 不翻转：顶部 Y=0，底部 Y=290
        Assert.Equal(0.0, top!.Value.Y, 6);
        Assert.Equal(290.0, bottom!.Value.Y, 6);
        Assert.True(bottom!.Value.Y > top!.Value.Y,
            "控件内 Y 越大必须越靠下——若此断言失败说明存在 Y 向上行为");
    }

    /// <summary>
    /// 分发器在真实控件上读取坐标，Y 不取反（不依赖窗口）。
    /// </summary>
    [Fact]
    public void Router_ToDocumentMatchesControlCoordinateSpace()
    {
        var canvas = new Canvas { Width = 400, Height = 300 };
        var router = new CanvasInputRouter(new NullDocument(TestDocSize), new Viewport(0, 0, 1.0, 1.0));
        router.AttachTo(canvas);

        // identity 视口下，视图坐标应原样映射为文档坐标
        var fromTop = CanvasInputRouter.ToDocument(new ViewPoint(10, 0), new Viewport(0, 0, 1.0, 1.0));
        var fromBottom = CanvasInputRouter.ToDocument(new ViewPoint(10, 290), new Viewport(0, 0, 1.0, 1.0));

        Assert.Equal(0.0, fromTop.Y, 6);
        Assert.Equal(290.0, fromBottom.Y, 6);
    }
}

/// <summary>
/// Avalonia Headless 集成测试的<b>已知阻塞</b>，记录于此避免后人重复踩。
/// </summary>
/// <remarks>
/// <para><b>目标</b>：按任务书 §8-验收标准 #4，需要「Avalonia Headless 驱动真实窗口 +
/// 指针事件 + 截图对比」。</para>
/// <para><b>🔴 当前阻塞（实测，非推测）</b>：
/// <list type="number">
/// <item>Avalonia 控件<b>必须在 Dispatcher 线程上创建</b>，
/// 否则抛 <c>InvalidOperationException: The calling thread cannot access this object
/// because a different thread owns it</c>（<c>Dispatcher.VerifyAccess</c>）。</item>
/// <item>用 <c>Dispatcher.UIThread.InvokeAsync(...).GetAwaiter().GetResult()</c> 包装
/// 会<b>死锁</b>——<c>SetupWithoutStarting()</c> 不启动事件泵，
/// 测试线程等 UI 线程执行，UI 线程永远不跑。</item>
/// <item>官方的 <c>Avalonia.Headless.XUnit</c> 包（提供 <c>[AvaloniaFact]</c> 与线程托管）
/// 在 12.x <b>传递依赖 xunit.v3</b>，与本仓库统一的 <b>xunit v2</b> 冲突，
/// 实测报 <c>CS0433</c>：<c>FactAttribute</c> 同时存在于
/// <c>xunit.core 2.9.2</c> 与 <c>xunit.v3.core 3.2.2</c>。</item>
/// </list></para>
/// <para><b>处置</b>：<b>不擅自改全仓测试框架</b>（会波及 6 个 lane 的测试项目）。
/// 当前用不依赖窗口的 <see cref="Control.Bounds"/> /
/// <see cref="Visual.TranslatePoint"/> 验证坐标系方向，
/// 效果等价且无死锁。</para>
/// <para><b>待总管裁决</b>：(a) 全仓升级到 xunit v3 以启用官方 Headless 集成；
/// 或 (b) 自建最小 Dispatcher 线程泵；或 (c) 波次 4 做截图对比时再定。
/// 在此之前，<b>真实窗口 + 截图对比这一验收项尚未达成</b>。</para>
/// </remarks>
public static class AvaloniaHeadlessBlocker
{
    /// <summary>当前是否已具备 Headless 窗口测试能力。</summary>
    /// <remarks>
    /// 实测为 <see langword="false"/>：原因见本类型的说明。
    /// 截图对比（验收标准 #4）在解决前不可声称达成。
    /// </remarks>
    public const bool WindowTestsAvailable = false;
}