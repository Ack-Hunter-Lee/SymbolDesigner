using CommunityToolkit.Mvvm.Input;

namespace SymbolDesigner.ViewModels;

/// <summary>图层顺序控制：置顶 / 置底 / 上移一层 / 下移一层 + ZIndex 归一化。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>
    /// 把选中元素移到画布最上层（遮挡所有其他元素）。
    /// 实现：将 SelectedItem 移到 Primitives 末尾，再重新归一化所有 ZIndex。
    /// </summary>
    [RelayCommand]
    private void BringToFront()
    {
        if (SelectedItem is null) return;
        var idx = Primitives.IndexOf(SelectedItem);
        if (idx < 0 || idx == Primitives.Count - 1) return;
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, Primitives.Count - 1);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把选中元素移到画布最下层（被所有其他元素遮挡）。
    /// </summary>
    [RelayCommand]
    private void SendToBack()
    {
        if (SelectedItem is null) return;
        var idx = Primitives.IndexOf(SelectedItem);
        if (idx <= 0) return;
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, 0);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把选中元素上移一层（ZIndex +1，遮挡其上方一个元素）。
    /// </summary>
    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedItem is null) return;
        var idx = Primitives.IndexOf(SelectedItem);
        if (idx < 0 || idx >= Primitives.Count - 1) return; // 已在最上层
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, idx + 1);
        NormalizeZIndex();
    }

    /// <summary>
    /// 把选中元素下移一层（ZIndex -1，被其下方一个元素遮挡）。
    /// </summary>
    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedItem is null) return;
        var idx = Primitives.IndexOf(SelectedItem);
        if (idx <= 0) return; // 已在最下层
        ForceCommitEdit(); // 排序 = 独立撤销步骤
        Primitives.Move(idx, idx - 1);
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