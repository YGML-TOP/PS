namespace Compositor.Core;

/// <summary>一次指针事件的修饰键与设备信息。</summary>
/// <param name="Shift">Shift 键按下。</param>
/// <param name="Alt">Alt 键按下。Mac 上对应 Option。</param>
/// <param name="Ctrl">Ctrl 键按下。Mac 上对应 Command。</param>
/// <param name="PointerId">指针设备 id。用于区分压感笔 / 触摸 / 鼠标的多点输入。</param>
public readonly record struct ToolInput(bool Shift, bool Alt, bool Ctrl, int PointerId);

/// <summary>
/// 工具抽象基类。所有画布交互工具（笔刷、橡皮、套索、魔棒…）的公共形状。
/// </summary>
/// <remarks>
/// 🔴 <b>本类不定义工具的状态机，也不定义坐标来源</b>。
/// 传入的 <see cref="DocPoint"/> 一律是<b>文档坐标</b>（Y 向下，铁律 1）；
/// 工具实现<b>不得</b>持有 <see cref="ViewPoint"/>，也不得自己做任何 Y 翻转。
/// </remarks>
public abstract class Tool
{
    /// <summary>工具标识，例如 <c>"brush"</c> / <c>"lasso"</c> / <c>"wand"</c>。用于持久化与命令路由。</summary>
    public abstract string Id { get; }

    /// <summary>指针按下。</summary>
    /// <param name="p">文档坐标下的位置。</param>
    /// <param name="input">修饰键与设备信息。</param>
    public abstract void PointerDown(DocPoint p, ToolInput input);

    /// <summary>指针拖动。</summary>
    /// <param name="p">文档坐标下的当前位置。</param>
    /// <param name="input">修饰键与设备信息。</param>
    public abstract void PointerDrag(DocPoint p, ToolInput input);

    /// <summary>指针抬起。</summary>
    /// <param name="input">修饰键与设备信息。</param>
    public abstract void PointerUp(ToolInput input);

    /// <summary>取消当前操作，回到未开始状态。默认无操作，因为并非所有工具都有中途取消的语义。</summary>
    public virtual void Cancel()
    {
    }

    /// <summary>工具能否作用于蒙版而非图层像素。</summary>
    /// <remarks>
    /// 默认 <see langword="true"/>：绝大多数工具在蒙版上绘制是合理的。
    /// 只有语义上不可用于蒙版的工具（如"图像大小"、"色阶取样"）才应覆写为 <see langword="false"/>，
    /// 由 UI 层据此把工具栏入口置灰。
    /// </remarks>
    public virtual bool SupportsMaskPainting => true;
}
