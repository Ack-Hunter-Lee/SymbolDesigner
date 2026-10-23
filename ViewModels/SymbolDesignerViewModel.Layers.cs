using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>图层顺序控制：置顶 / 置底 / 上移一层 / 下移一层 + ZIndex 归一化。
/// 四个命令都接受可选参数 target：元素面板按行传该行图元（无需先选中），
/// 工具栏 / 属性面板传 null 则作用于当前选中图元。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>
    /// 把目标元素（未指定时用选中元素）移到画布最上层（遮挡所有其他元素）。
    /// 实现：把元素移到 Primitives 末尾，再重新归一化所有 ZIndex。
    /// </summary>
    [RelayCommand]
    private void BringToFront(PrimitiveItem? target)
    {
        var item = target ?? SelectedItem;
        if (item is null) return;
        var idx = Primitives.IndexOf(item);
        if (idx < 0 || idx == Primitives.Count - 1) return;
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, Primitives.Count - 1);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把目标元素（未指定时用选中元素）移到画布最下层（被所有其他元素遮挡）。
    /// </summary>
    [RelayCommand]
    private void SendToBack(PrimitiveItem? target)
    {
        var item = target ?? SelectedItem;
        if (item is null) return;
        var idx = Primitives.IndexOf(item);
        if (idx <= 0) return;
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, 0);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把目标元素（未指定时用选中元素）上移一层（遮挡原上方一个元素）。
    /// </summary>
    [RelayCommand]
    private void MoveUp(PrimitiveItem? target)
    {
        var item = target ?? SelectedItem;
        if (item is null) return;
        var idx = Primitives.IndexOf(item);
        if (idx < 0 || idx >= Primitives.Count - 1) return; // 已在最上层
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, idx + 1);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把目标元素（未指定时用选中元素）下移一层（被原下方一个元素遮挡）。
    /// </summary>
    [RelayCommand]
    private void MoveDown(PrimitiveItem? target)
    {
        var item = target ?? SelectedItem;
        if (item is null) return;
        var idx = Primitives.IndexOf(item);
        if (idx <= 0) return; // 已在最下层
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, idx - 1);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把图元移动到 Primitives 集合的指定索引（元素面板拖放排序用）。
    /// 索引夹到 [0, Count-1]；排序作为独立撤销步骤，完成后归一化 ZIndex。
    /// </summary>
    public void MovePrimitiveTo(PrimitiveItem item, int targetIndex)
    {
        var idx = Primitives.IndexOf(item);
        if (idx < 0 || Primitives.Count == 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, Primitives.Count - 1);
        if (targetIndex == idx) return;
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, targetIndex);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把所有图元的 ZIndex 重新归一化为 0,1,2,...,N-1（按集合当前顺序）。
    /// 避免反复调整后 ZIndex 数值膨胀，并确保 ZIndex 与集合顺序一致。
    /// </summary>
    private void NormalizeZIndex()
    {
        for (int i = 0; i < Primitives.Count; i++)
            Primitives[i].ZIndex = i;
    }
}