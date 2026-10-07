using Avalonia;
using Compositor.App.Canvas;
using Compositor.Core;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// <see cref="CanvasViewport"/> 的断言，重点是<b>坐标契约</b>。
/// </summary>
/// <remarks>
/// 铁律 1：文档是左上原点、Y 向下；Avalonia 屏幕坐标也是 Y 向下。
/// 因此换算<b>不允许</b>翻转 Y。这组测试就是把"没有翻转"钉死在测试里，
/// 免得以后有人为了"看起来顺眼"加一个 <c>-y</c>。
/// </remarks>
public sealed class CanvasViewportTests
{
    [Fact]
    public void Identity_一比一且原点不动()
    {
        CanvasViewport viewport = CanvasViewport.Identity(new DocSize(100, 80));

        Assert.Equal(1.0, viewport.PointsPerPixel);
        Rect screen = viewport.ToScreen(new DocRect(new DocPoint(0, 0), new DocSize(100, 80)));

        Assert.Equal(0.0, screen.X);
        Assert.Equal(0.0, screen.Y);
        Assert.Equal(100.0, screen.Width);
        Assert.Equal(80.0, screen.Height);
    }

    [Fact]
    public void 文档Y增大时屏幕Y同步增大_没有翻转()
    {
        CanvasViewport viewport = CanvasViewport.Identity(new DocSize(100, 100));

        // 文档上边缘 → 屏幕更小的 Y；文档下边缘 → 屏幕更大的 Y。
        double top = viewport.ToScreenPoint(new DocPoint(0, 10)).Y;
        double bottom = viewport.ToScreenPoint(new DocPoint(0, 40)).Y;

        Assert.True(bottom > top, $"屏幕 Y 必须随文档 Y 一起增大，实际 top={top} bottom={bottom}。");
    }

    [Fact]
    public void ToScreen_文档上边缘永远映射到更小的屏幕Y()
    {
        CanvasViewport viewport = CanvasViewport.Fit(new Rect(0, 0, 400, 400), new DocSize(200, 100), 0);

        var doc = new DocRect(new DocPoint(10, 20), new DocSize(50, 30));
        Rect screen = viewport.ToScreen(doc);

        double topOnScreen = viewport.ToScreenPoint(new DocPoint(doc.Left, doc.Top)).Y;
        double bottomOnScreen = viewport.ToScreenPoint(new DocPoint(doc.Left, doc.Bottom)).Y;

        Assert.True(screen.Height > 0);
        Assert.Equal(screen.Y, topOnScreen, 6);
        Assert.Equal(screen.Bottom, bottomOnScreen, 6);

        // 显式钉住方向：文档下边缘必须映射到更大的屏幕 Y。
        Assert.True(bottomOnScreen > topOnScreen, $"top={topOnScreen} bottom={bottomOnScreen}");
    }

    [Fact]
    public void Fit_按可用区域等比缩放并居中()
    {
        // 可用区 80×80（100 减两侧各 10 的 padding），文档 50×50 → 缩放 1.6。
        CanvasViewport viewport = CanvasViewport.Fit(new Rect(0, 0, 100, 100), new DocSize(50, 50), 10);

        Assert.Equal(1.6, viewport.PointsPerPixel, 6);

        Rect screen = viewport.ToScreen(new DocRect(new DocPoint(0, 0), new DocSize(50, 50)));
        Assert.Equal(10, screen.X, 6);
        Assert.Equal(10, screen.Y, 6);
        Assert.Equal(80, screen.Width, 6);
        Assert.Equal(80, screen.Height, 6);
    }

    [Fact]
    public void Fit_宽高比不同时按较小的一边约束()
    {
        // 文档 400×100 放进 100×100：宽是瓶颈，缩放 0.25，垂直方向留白居中。
        CanvasViewport viewport = CanvasViewport.Fit(new Rect(0, 0, 100, 100), new DocSize(400, 100), 0);

        Assert.Equal(0.25, viewport.PointsPerPixel, 6);
        Rect screen = viewport.ToScreen(new DocRect(new DocPoint(0, 0), new DocSize(400, 100)));
        Assert.Equal(100, screen.Width, 6);
        Assert.Equal(25, screen.Height, 6);
        Assert.Equal(37.5, screen.Y, 6);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, -1)]
    public void Fit_退化文档尺寸不抛异常而是退化成Identity(int width, int height)
    {
        CanvasViewport viewport = CanvasViewport.Fit(new Rect(0, 0, 100, 100), new DocSize(width, height), 10);

        Assert.Equal(1.0, viewport.PointsPerPixel);
    }

    [Fact]
    public void Fit_缩放被夹在可显示区间内()
    {
        // 30000 像素的文档塞进 10×10 的控件：原始缩放 0.0003，必须被夹到下限。
        CanvasViewport tiny = CanvasViewport.Fit(new Rect(0, 0, 10, 10), new DocSize(30_000, 30_000), 0);
        Assert.Equal(CanvasViewport.MinPointsPerPixel, tiny.PointsPerPixel);

        // 1 像素的文档放进 10000×10000 的控件：原始缩放 10000，必须被夹到上限。
        CanvasViewport huge = CanvasViewport.Fit(new Rect(0, 0, 10_000, 10_000), new DocSize(1, 1), 0);
        Assert.Equal(CanvasViewport.MaxPointsPerPixel, huge.PointsPerPixel);
    }

    [Fact]
    public void 屏幕与文档往返换算保持一致()
    {
        CanvasViewport viewport = CanvasViewport.Fit(new Rect(12, 34, 640, 480), new DocSize(320, 240), 16);

        DocPoint original = new(123.5, 67.25);
        DocPoint screen = viewport.ToScreenPoint(original);
        DocPoint roundTripped = viewport.ToDocument(new Point(screen.X, screen.Y));

        Assert.Equal(original.X, roundTripped.X, 6);
        Assert.Equal(original.Y, roundTripped.Y, 6);
    }

    [Fact]
    public void 矩形往返换算保持尺寸()
    {
        CanvasViewport viewport = CanvasViewport.Fit(new Rect(0, 0, 800, 600), new DocSize(400, 300), 20);

        var doc = new DocRect(new DocPoint(10, 20), new DocSize(100, 50));
        DocRect back = viewport.ToDocument(viewport.ToScreen(doc));

        Assert.Equal(10, back.Left, 6);
        Assert.Equal(20, back.Top, 6);
        Assert.Equal(110, back.Right, 6);
        Assert.Equal(70, back.Bottom, 6);
    }
}