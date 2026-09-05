using System.Windows;
using System.Windows.Controls.Primitives;
using SymbolDesigner.Models;

namespace SymbolDesigner.Views;

/// <summary>
/// 调整大小的方向。决定拖动时修改哪些属性（X/Y/Width/Height）。
/// </summary>
public enum ResizeDirection
{
    /// <summary>左上角：X/Y 增，Width/Height 减</summary>
    NW,
    /// <summary>右上角：Y 增，Width 增，Height 减</summary>
    NE,
    /// <summary>左下角：X 增，Width 减，Height 增</summary>
    SW,
    /// <summary>右下角：Width/Height 增</summary>
    SE,
}

/// <summary>
/// 调整大小的手柄。放在 DraggablePrimitive 的四个角，
/// 根据 Direction 在 DragDelta 时更新 PrimitiveItem 的 X/Y/Width/Height。
/// 仅在元素被选中时显示（由 XAML 模板的 DataTrigger 控制）。
/// 关键点：达到最小尺寸下限时把"反向位移"卡住，避免尺寸不再变而 X/Y 仍漂移。
/// </summary>
public class ResizeThumb : Thumb
{
    public static readonly DependencyProperty DirectionProperty =
        DependencyProperty.Register(
            nameof(Direction), typeof(ResizeDirection), typeof(ResizeThumb),
            new PropertyMetadata(ResizeDirection.SE));

    /// <summary>调整方向。决定拖动时修改哪些属性。</summary>
    public ResizeDirection Direction
    {
        get => (ResizeDirection)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    public ResizeThumb()
    {
        // 不要在构造函数里订阅 DragDelta，否则设计时可能报错
        // 改用 OnApplyTemplate 后再订阅？Thumb 没有 OnApplyTemplate 钩子，
        // 直接订阅即可（实例化即订阅，生命周期与控件一致）
        DragDelta += OnDragDelta;
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not PrimitiveItem item) return;

        const double MinSize = 4.0;
        double dx = e.HorizontalChange;
        double dy = e.VerticalChange;

        switch (Direction)
        {
            case ResizeDirection.SE:
                // 右下：仅改 W/H
                item.Width = System.Math.Max(MinSize, item.Width + dx);
                item.Height = System.Math.Max(MinSize, item.Height + dy);
                break;

            case ResizeDirection.NW:
            {
                // 左上：W/H 减少的同时 X/Y 增加相同量
                double newW = System.Math.Max(MinSize, item.Width - dx);
                double newH = System.Math.Max(MinSize, item.Height - dy);
                // 反向卡住：用实际变化量而不是 dx/dy
                item.X += item.Width - newW;
                item.Y += item.Height - newH;
                item.Width = newW;
                item.Height = newH;
                break;
            }

            case ResizeDirection.NE:
            {
                double newH = System.Math.Max(MinSize, item.Height - dy);
                item.Y += item.Height - newH;
                item.Width = System.Math.Max(MinSize, item.Width + dx);
                item.Height = newH;
                break;
            }

            case ResizeDirection.SW:
            {
                double newW = System.Math.Max(MinSize, item.Width - dx);
                item.X += item.Width - newW;
                item.Width = newW;
                item.Height = System.Math.Max(MinSize, item.Height + dy);
                break;
            }
        }

        e.Handled = true;
    }
}
