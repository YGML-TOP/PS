using Compositor.App.ViewModels;
using Compositor.Core;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// <see cref="MainWindowViewModel"/> 的断言：验证「契约 → 界面」的翻译链路真的通。
/// </summary>
public sealed class MainWindowViewModelTests
{
    [Fact]
    public void 混合模式_恰好24项且与契约枚举逐项对应()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(24, vm.BlendModes.Count);

        BlendMode[] contract = Enum.GetValues<BlendMode>();
        for (int i = 0; i < contract.Length; i++)
        {
            Assert.Equal(contract[i], vm.BlendModes[i].Mode);
            Assert.Equal(BlendModeStrings.ToLiteral(contract[i]), vm.BlendModes[i].Literal);
        }
    }

    [Fact]
    public void 混合模式_字面量能被契约解析回去_证明没有自造字符串()
    {
        var vm = new MainWindowViewModel();

        foreach (BlendModeOption option in vm.BlendModes)
        {
            Assert.Equal(option.Mode, BlendModeStrings.Parse(option.Literal));
        }
    }

    [Fact]
    public void 混合模式_显示文本用的就是comp字面量()
    {
        var vm = new MainWindowViewModel();

        // 挑两个"枚举名与字面量不一致"的项，确认界面显示的是字面量。
        Assert.Equal("Linear Dodge (Add)", vm.BlendModes[(int)BlendMode.LinearDodge].Display);
        Assert.Equal("Color Burn", vm.BlendModes[(int)BlendMode.ColorBurn].Display);
    }

    [Fact]
    public void 图层_按界面习惯自上而下排列()
    {
        var vm = new MainWindowViewModel();

        // 契约是自下而上（SampleProject: 背景、渐变叠加、注释），
        // 界面要反着排——最上面的图层显示在第一行。
        Assert.Equal(3, vm.Layers.Count);
        Assert.Equal("注释（隐藏）", vm.Layers[0].Name);
        Assert.Equal("渐变叠加", vm.Layers[1].Name);
        Assert.Equal("背景", vm.Layers[2].Name);
    }

    [Fact]
    public void 活动图层_取自快照的ActiveLayerId()
    {
        var vm = new MainWindowViewModel();

        Assert.NotNull(vm.ActiveLayer);
        Assert.Equal("渐变叠加", vm.ActiveLayer!.Name);
        Assert.Equal("Overlay", vm.ActiveLayerBlendModeText);
        Assert.Equal("85%", vm.ActiveLayerOpacityText);
    }

    [Fact]
    public void 可见性与混合模式从LayerNode投影过来()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.Layers[0].IsVisible);          // 注释（隐藏）
        Assert.True(vm.Layers[1].IsVisible);           // 渐变叠加
        Assert.Equal(BlendMode.Multiply, vm.Layers[0].BlendMode);
        Assert.Equal(BlendMode.Overlay, vm.Layers[1].BlendMode);
        Assert.Equal(BlendMode.Normal, vm.Layers[2].BlendMode);
    }

    [Fact]
    public void 画布像素来自确定性的测试图像()
    {
        var vm = new MainWindowViewModel();

        Assert.NotNull(vm.CanvasPixels);
        Assert.Equal(512, vm.CanvasPixels.Width);
        Assert.Equal(512, vm.CanvasPixels.Height);
        Assert.Equal("512 × 512", vm.DocumentSizeText);
    }

    [Fact]
    public void 空快照不会抛异常_退化成零图层()
    {
        var vm = new MainWindowViewModel(new ProjectSnapshot());

        Assert.Empty(vm.Layers);
        Assert.Null(vm.ActiveLayer);
        Assert.Equal("（无）", vm.ActiveLayerName);
        Assert.Equal("—", vm.ActiveLayerBlendModeText);
        Assert.Equal("0 × 0", vm.DocumentSizeText);
    }

    [Fact]
    public void 退出命令恒可用()
    {
        var vm = new MainWindowViewModel();

        Assert.True(vm.QuitCommand.CanExecute(null));

        // 没有 Avalonia 生命周期时不应抛：按钮点了没反应好过点了崩窗口。
        vm.QuitCommand.Execute(null);
    }
}