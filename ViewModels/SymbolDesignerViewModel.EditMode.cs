using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>编辑模式切换（骨架 Skeleton / 厚度 Thickness / 轮廓 Outline）与右侧面板（属性/元素）切换。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== 编辑模式（骨架 / 厚度 / 轮廓） ==============

    /// <summary>当前编辑模式（骨架 / 轮廓）。
    /// 切换不影响 SelectedItem；只决定显示哪一套手柄 + 属性面板内容。
    /// 工具栏按钮 / 快捷键（Tab）切换；SelectedItem 改变不影响本字段（保持用户当前模式）。</summary>
    [ObservableProperty] private OutlineEditMode _outlineEditMode = OutlineEditMode.Skeleton;

    /// <summary>是否处于轮廓编辑模式（XAML 绑定用：手柄集合 / 按钮高亮 / 快捷键提示）。</summary>
    public bool IsOutlineEditMode => OutlineEditMode == OutlineEditMode.Outline;

    /// <summary>骨架编辑模式（阶段 2/3 用于替代 IsOutlineEditMode 的反向语义判）。</summary>
    public bool IsSkeletonEditMode => OutlineEditMode == OutlineEditMode.Skeleton;

    /// <summary>厚度线编辑模式（阶段 3 用：ThicknessHandles 可见性 / 属性面板厚度线分支）。</summary>
    public bool IsThicknessEditMode => OutlineEditMode == OutlineEditMode.Thickness;

    /// <summary>轮廓编辑模式下能否使用（ThickPolyline / 已闭合的 Polyline / BooleanPath 可用；
    /// 无内部接缝时手柄集合为空，但用户仍可进入模式查看/等待编辑）。</summary>
    public bool CanEnterOutlineMode =>
        SelectedItem is { } it
        && (it.TypeKey == nameof(L1Primitive.ThickPolyline)
            || (it.TypeKey == nameof(L1Primitive.Polyline) && it.IsClosed)
            || it.TypeKey == nameof(L1Primitive.BooleanPath));

    /// <summary>厚度线模式下能否使用（同 Outline：仅 ThickPolyline 可用）。</summary>
    public bool CanEnterThicknessMode =>
        SelectedItem is { } it2
        && it2.TypeKey == nameof(L1Primitive.ThickPolyline);

    partial void OnOutlineEditModeChanged(OutlineEditMode value)
    {
        // 三 bool 属性同步发通知：XAML DataTrigger 三模式分支共用同一变更信号
        OnPropertyChanged(nameof(IsOutlineEditMode));
        OnPropertyChanged(nameof(IsSkeletonEditMode));
        OnPropertyChanged(nameof(IsThicknessEditMode));
        // 阶段 2：刷新三模式按钮的 IsSelected（按钮高亮态）
        RefreshEditModeChoices();
        // 模式切换时清空接缝选中（避免显示陈旧的高亮）
        SelectedOutlineCorner = null;
        // 刷新手柄集合：三个模式各显示一套
        RefreshVertexHandles();
        RefreshSegmentHandles();
        RefreshOutlineCornerHandles();
        RefreshThicknessHandles();
        SelectedBonePointIndex = -1;
    }

    /// <summary>阶段 2：把当前 OutlineEditMode 同步到三模式按钮的 IsSelected。
    /// EditModeChoice.IsSelected 是 init-only，所以替换整集合项。
    /// 集合项固定为 3，刷新成本极低。</summary>
    private void RefreshEditModeChoices()
    {
        if (_editModeChoices.Count == 0) return;  // 构造函数未跑时保护
        for (int i = 0; i < _editModeChoices.Count; i++)
        {
            var c = _editModeChoices[i];
            if (c.IsSelected == (c.Mode == OutlineEditMode)) continue;
            _editModeChoices[i] = new EditModeChoice
            {
                Mode = c.Mode,
                Label = c.Label,
                Command = c.Command,
                CommandParameter = c.CommandParameter,
                IsSelected = c.Mode == OutlineEditMode,
            };
        }
    }

    partial void OnSelectedOutlineCornerChanged(OutlineCornerHandle? value)
    {
        // 同步 HasSelectedOutlineCorner（供 XAML 属性面板显示/隐藏）
        HasSelectedOutlineCorner = value != null;
        // 同步每个手柄的 IsSelected 状态（供 UI 高亮）
        foreach (var h in OutlineCornerHandles)
            h.IsSelected = (h == value);
        // 记忆等价键（RefreshOutlineCornerHandles 用）：按 (Index, Side, StepIndex, IsCorner)
        // 重建后从 OutlineCornerHandles 里重新定位到对应的新 handle，避免 XAML 绑定指向游离对象。
        _selectedCornerKey = value is null
            ? null
            : (value.Index, value.Side, value.StepIndex, value.IsCorner);
    }

    /// <summary>切换到轮廓编辑模式（如果当前可以）。</summary>
    public void EnterOutlineMode()
    {
        if (!CanEnterOutlineMode) return;
        OutlineEditMode = OutlineEditMode.Outline;
    }

    /// <summary>切回骨架编辑模式。</summary>
    public void EnterSkeletonMode()
    {
        OutlineEditMode = OutlineEditMode.Skeleton;
    }

    /// <summary>切换到厚度线编辑模式（如果当前可以）。</summary>
    public void EnterThicknessMode()
    {
        if (!CanEnterThicknessMode) return;
        OutlineEditMode = OutlineEditMode.Thickness;
    }

    /// <summary>三态轮转编辑模式（Tab 快捷键 / 通用翻转按钮用）：
    /// Skeleton → Thickness → Outline → Skeleton。原先二态版本仅 Skeleton↔Outline。
    /// 闭合 Polyline / BooleanPath 无厚度概念：Skeleton ↔ Outline 直达，跳过 Thickness。</summary>
    [RelayCommand]
    public void ToggleEditMode()
    {
        bool noThickness = SelectedItem is { } sp
            && ((sp.TypeKey == nameof(L1Primitive.Polyline) && sp.IsClosed)
                || sp.TypeKey == nameof(L1Primitive.BooleanPath));
        OutlineEditMode = OutlineEditMode switch
        {
            OutlineEditMode.Skeleton => noThickness ? OutlineEditMode.Outline : OutlineEditMode.Thickness,
            OutlineEditMode.Thickness => OutlineEditMode.Outline,
            _ => OutlineEditMode.Skeleton,
        };
    }

    /// <summary>通用进入指定模式（XAML 三按钮统一 handler）。
    /// 阶段 2 让 XAML 用 ItemsControl + 一条 Command + CommandParameter，避免三段复制。</summary>
    [RelayCommand]
    private void EnterMode(OutlineEditMode? mode)
    {
        if (mode is null) return;
        switch (mode.Value)
        {
            case OutlineEditMode.Skeleton:
                EnterSkeletonMode();
                break;
            case OutlineEditMode.Thickness:
                EnterThicknessMode();
                break;
            case OutlineEditMode.Outline:
                EnterOutlineMode();
                break;
        }
    }

    /// <summary>三模式按钮集合（Skeleton / Thickness / Outline）。
    /// XAML 用 ItemsControl + UniformGrid 三列渲染；CommandParameter 传 mode。</summary>
    public ReadOnlyObservableCollection<EditModeChoice> EditModeChoices { get; }

    private ObservableCollection<EditModeChoice> _editModeChoices = new();

    /// <summary>右侧面板两页按钮集合（属性 / 元素）。
    /// XAML 用 ItemsControl + UniformGrid 两列渲染；CommandParameter 传 page。</summary>
    public ReadOnlyObservableCollection<RightPaneChoice> RightPaneChoices { get; }

    private ObservableCollection<RightPaneChoice> _rightPaneChoices = new();

    /// <summary>切换右侧面板页面（属性 ↔ 元素）。页面切换不丢 SelectedItem，画布状态保持不变。</summary>
    [RelayCommand]
    private void ShowRightPane(RightPanePage? page)
    {
        if (page is null || page.Value == RightPanePage) return;
        RightPanePage = page.Value;
    }

    /// <summary>把当前 RightPanePage 同步到两页按钮的 IsSelected（按钮高亮态）。
    /// RightPaneChoice.IsSelected 是 init-only，所以替换整集合项。</summary>
    private void RefreshRightPaneChoices()
    {
        if (_rightPaneChoices.Count == 0) return;  // 构造函数未跑时保护
        for (int i = 0; i < _rightPaneChoices.Count; i++)
        {
            var c = _rightPaneChoices[i];
            if (c.IsSelected == (c.Page == RightPanePage)) continue;
            _rightPaneChoices[i] = new RightPaneChoice
            {
                Page = c.Page,
                Label = c.Label,
                Command = c.Command,
                CommandParameter = c.CommandParameter,
                IsSelected = c.Page == RightPanePage,
            };
        }
    }

    /// <summary>属性页可见（XAML 页面切换用）。</summary>
    public bool IsPropertiesPaneVisible => RightPanePage == RightPanePage.Properties;

    /// <summary>元素页可见（XAML 页面切换用）。</summary>
    public bool IsElementsPaneVisible => RightPanePage == RightPanePage.Elements;

    /// <summary>右侧面板当前页面（属性 / 元素）。元素页提供全量元素清单 + 隐藏/显示控制，
    /// 用于排查画布上不可见的多余元素。切页不影响 SelectedItem 与编辑模式。</summary>
    [ObservableProperty] private RightPanePage _rightPanePage = RightPanePage.Properties;

    partial void OnRightPanePageChanged(RightPanePage value)
    {
        RefreshRightPaneChoices();
        OnPropertyChanged(nameof(IsPropertiesPaneVisible));
        OnPropertyChanged(nameof(IsElementsPaneVisible));
    }
}