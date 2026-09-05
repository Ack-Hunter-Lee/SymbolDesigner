using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>元素集合变更处理（标脏/撤销/订阅）/ 元素页清单（隐藏/显示/删除/定位）。</summary>
public partial class SymbolDesignerViewModel
{
    private void OnPrimitivesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsDirty = true;
        // 增删/排序等结构变化 → 可撤销编辑(撤销/重做恢复过程由 _isRestoring 跳过)
        if (!_isRestoring) ScheduleUndoCommit();
        if (e.OldItems != null)
            foreach (PrimitiveItem p in e.OldItems)
            {
                p.PropertyChanged -= OnPrimitiveItemPropertyChanged;
                p.VisibilityChanged -= OnPrimitiveVisibilityChanged;
            }
        if (e.NewItems != null)
            foreach (PrimitiveItem p in e.NewItems)
            {
                p.PropertyChanged += OnPrimitiveItemPropertyChanged;
                p.VisibilityChanged += OnPrimitiveVisibilityChanged;
            }
        if (!_isRestoring) RefreshHiddenCount();
    }

    /// <summary>ArrayInstances 增/删 → 标 dirty + 调度撤销快照。
    /// 快照由 SnapshotPrimitives() 自动覆盖两个集合（Primitives + ArrayInstances），所以无需单独的阵列快照栈。
    /// 阵列源本身（Clone 的 PrimitiveItemDto）不会变 → 不需要 PropertyChanged 订阅。</summary>
    private void OnArrayInstancesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsDirty = true;
        if (!_isRestoring) ScheduleUndoCommit();
        // 阵列变更 → 立即重建渲染层（用户期望看到新副本出现 / 旧副本消失）
        // 撤销恢复过程（_isRestoring=true）期间也会重建——保证恢复后视图一致
        RebuildArrayRenderItems();
    }

    /// <summary>任一元素 IsVisible 变化 → 刷新隐藏计数（元素页统计/按钮可用态）。</summary>
    private void OnPrimitiveVisibilityChanged(object? sender, EventArgs e)
        => RefreshHiddenCount();

    /// <summary>隐藏元素数量（画布上 IsVisible=false 的图元数）。元素页统计 + "全部显示"按钮可用态。</summary>
    [ObservableProperty] private int _hiddenCount;

    /// <summary>重算隐藏元素数量 + 通知"全部显示"按钮可用态。</summary>
    private void RefreshHiddenCount()
    {
        int n = 0;
        foreach (var p in Primitives)
            if (!p.IsVisible) n++;
        HiddenCount = n;
        OnPropertyChanged(nameof(HasHiddenElements));
        ShowAllElementsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>是否存在隐藏元素（元素页"全部显示"按钮可用态）。</summary>
    public bool HasHiddenElements => HiddenCount > 0;

    /// <summary>一键把所有隐藏元素恢复可见。用于排查完多余元素后统一恢复。</summary>
    [RelayCommand(CanExecute = nameof(HasHiddenElements))]
    private void ShowAllElements()
    {
        foreach (var p in Primitives)
            if (!p.IsVisible)
                p.IsVisible = true;
        RefreshHiddenCount();
    }

    /// <summary>元素面板行内眼睛按钮：翻转单条元素的可见性。
    /// 不通过 IsVisible TwoWay 绑走的原因：元素面板 ListBoxItem 点击会同时触发"选中本行"
    /// （与画布选中联动），通过命令显式翻值 + 阻止冒泡到 ListBoxItem.OnSelected 更可控。
    /// 注：ListBoxItem 的选中由 ListBox.SelectedItem 绑定驱动，本命令不操作 SelectedItem。</summary>
    [RelayCommand]
    private void ToggleElementVisible(PrimitiveItem? item)
    {
        if (item is null) return;
        item.IsVisible = !item.IsVisible; // OnIsVisibleChanged → RefreshHiddenCount 自动触发
    }

    /// <summary>元素面板行末删除按钮：删除该元素（与工具栏"删除选中"等价）。
    /// 收口编辑、置空 SelectedItem、移除图元、清理相切约束。删除的撤销由 Primitives.Remove 触发的快照完成。
    /// 不复用 DeleteSelectedCommand 的原因：DeleteSelected 读的是当前 SelectedItem，
    /// 但元素面板 ListBoxItem 的选中绑定可能与本按钮点击存在时序竞争；显式传参更稳。</summary>
    [RelayCommand]
    private void DeleteElement(PrimitiveItem? item)
    {
        if (item is null) return;
        ForceCommitEdit();
        if (SelectedItem == item) SelectedItem = null;
        Primitives.Remove(item);
        ItemCount = Primitives.Count;
        PruneConstraints();
    }

    /// <summary>元素面板行末"快速定位"按钮：选中元素 + 通知 View 跳转视口到该元素中心。
    /// 选中由本命令负责（保证 ListBox 选中状态同步）；视口跳转由 View 端监听 RequestLocate 事件后调用
    /// ZoomPanViewport.CenterOnContentPoint 实现（VM 不感知 View 控件）。</summary>
    [RelayCommand]
    private void LocateElement(PrimitiveItem? item)
    {
        if (item is null) return;
        SelectedItem = item;
        RequestLocate?.Invoke(item);
    }

    /// <summary>View 端订阅：触发"把视口中心平移到该元素画布中心"的动作。
    /// 由 SymbolDesignerView.xaml.cs 在 Loaded 时挂上，调用 ZoomPan.CenterOnContentPoint。</summary>
    public event Action<PrimitiveItem>? RequestLocate;

    /// <summary>已加载/已保存的元素属性变化 → 标 dirty + 记录撤销。
    /// 暂停/恢复时通过 _suppressDirty 临时禁用，避免循环触发。</summary>
    private bool _suppressDirty;
    private void OnPrimitiveItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressDirty) return;
        if (e.PropertyName == nameof(PrimitiveItem.OutlineGeometry))
        {
            // 渲染几何不标 dirty（由 PathData/Thickness 驱动），但顶点厚度等编辑仍应进入撤销历史
            ScheduleUndoCommit();
            return;
        }
        IsDirty = true;
        ScheduleUndoCommit();
    }
}