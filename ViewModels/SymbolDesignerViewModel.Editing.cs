using System;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>选中图元的变化处理 / 图元属性联动刷新 / 删除与清空画布。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>当前选中的图形实例（用于属性编辑/删除）。</summary>
    [ObservableProperty] private PrimitiveItem? _selectedItem;

    private PrimitiveItem? _lastSelected;

    partial void OnSelectedItemChanged(PrimitiveItem? value)
    {
        // 编辑源模式：选中切到其它图元 / 取消选图 → 先提交并退出编辑源（AutoCAD：单击别处结束源编辑）
        if (_isArraySourceEditMode && value?.Id != _arraySourceEditGuid)
            CollapseArraySourceEdit();

        // 阵列选择互斥：选中或取消选中图元 → 清除阵列整体选择（阵列是"单一对象"语义，与图元不共存）
        if (SelectedArray is not null)
        {
            // 编辑源模式：SelectedItem=借出的编辑源图元（本体），不清阵列、不关浮层
            bool keepArray = _isArraySourceEditMode && value?.Id == _arraySourceEditGuid;
            if (!keepArray)
            {
                SelectedArray = null;
                // 阵列编辑浮层随之关闭（点图元/空白切走选中）
                if (_isArrayEditMode) ExitArrayEdit();
            }
        }

        // 正处于"调整源"而选中切换到其它（含取消）→ 自动收起源，避免源残留画布
        if (IsSourceAdjustMode && value?.Id != _sourceAdjustTarget)
        {
            if (_sourceAdjustTarget != Guid.Empty)
            {
                var t = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
                if (t is { TypeKey: nameof(L1Primitive.BooleanPath) })
                {
                    CollapseSourceAdjust(t);
                    SetBooleanSourcesVisible(t, false);
                }
            }
            _sourceAdjustTarget = Guid.Empty;
            IsSourceAdjustMode = false;
        }

        // 单选：把选中状态同步到所有 item
        foreach (var p in Primitives)
        {
            // 高亮同步：单选焦点高亮 + 多选集合成员高亮（累积式多选保留高亮）
            p.IsSelected = p == value || MultiSelected.Contains(p);
        }

        // 取消选中（选中 null / 点击画布空白）→ 清空多选，避免高亮残留
        if (value is null)
            ClearMultiSelect();

        SelectedItemIsRect = value is not null &&
                             (value.TypeKey == nameof(L1Primitive.Rectangle) ||
                              value.TypeKey == nameof(L1Primitive.RoundedRectangle));

        HasSelectedItem = value is not null;
        OnPropertyChanged(nameof(SelectedItemIsThickPolyline));
        OnPropertyChanged(nameof(SelectedItemIsPolyline));
        OnPropertyChanged(nameof(SelectedItemIsRing));
        OnPropertyChanged(nameof(SelectedItemIsBooleanPath));
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
        OnPropertyChanged(nameof(CanRecomputeBoolean));
        ToggleSourceAdjustCommand.NotifyCanExecuteChanged();
        // 格式刷按钮可用态：选中变化（多选累积切到单选、选中切到 null）→ 通知 PickFormatPainter 重新评估 CanExecute
        PickFormatPainterCommand.NotifyCanExecuteChanged();

        // 选中 ThickPolyline / Polyline → 自动进入对应画线模式（光标变十字，CanvasHost 直接接收点击起笔）。
        // 尊重用户手动选择：若用户从 ComboBox 手动选定过模式（_userChangedDrawMode），
        // 则选中联动不再覆盖；取消选中 / 选中其它类型时，若当前正处于"自动进入"的
        // 画线模式则退回 None，避免残留绘制状态误伤后续操作。
        var isThickPolyline = value is not null
            && value.TypeKey == nameof(L1Primitive.ThickPolyline);
        var isPolyline = value is not null
            && value.TypeKey == nameof(L1Primitive.Polyline);
        if (isThickPolyline && !_userChangedDrawMode && DrawMode != DrawMode.ThickPolyline)
        {
            SetDrawModeFromSelection(DrawMode.ThickPolyline);
        }
        else if (isPolyline && !_userChangedDrawMode && DrawMode != DrawMode.Polyline)
        {
            SetDrawModeFromSelection(DrawMode.Polyline);
        }
        else if (!isThickPolyline && !isPolyline && !_userChangedDrawMode
                 && (DrawMode == DrawMode.ThickPolyline || DrawMode == DrawMode.Polyline))
        {
            SetDrawModeFromSelection(DrawMode.None);
        }

        // 监听 SelectedItem 的 Width/Height/PathData 变化（尺寸改 → 刷 Max；PathData 改 → 刷顶点手柄）
        if (_lastSelected != null)
        {
            _lastSelected.PropertyChanged -= OnSelectedItemPropertyChanged;
            _lastSelected.VertexThicknessDataChanged -= OnSelectedVertexThicknessDataChanged;
        }
        _lastSelected = value;
        if (value != null)
        {
            value.PropertyChanged += OnSelectedItemPropertyChanged;
            // 订阅顶点厚度数据变化（Slider 改 Incoming/OutgoingThickness 时刷新 ThicknessHandles/ThicknessLines）
            value.VertexThicknessDataChanged += OnSelectedVertexThicknessDataChanged;
        }
        else
        {
            // 取消选中元素：清掉所有挂在旧 item 上的子状态，避免画布残留接缝/厚度手柄和属性面板误显示
            SelectedOutlineCorner = null;
            SelectedBonePointIndex = -1;
            // ThicknessHandles / OutlineCornerHandles 集合里残留的是上一个 item 的 X/Y，
            // 必须显式刷新（Clear + SelectedItem==null return）才能让画布 ItemsControl 真正清空
            RefreshThicknessHandles();
            RefreshRingHandles();
            if (OutlineEditMode == OutlineEditMode.Outline)
                RefreshOutlineCornerHandles();
        }

        RaiseMaxChanged();
        OnPropertyChanged(nameof(CanEnterOutlineMode));
        OnPropertyChanged(nameof(CanEnterThicknessMode));
        // 布尔结果选中即进入轮廓模式：轮廓点选择 + 接缝样式（Miter/Round）/圆角半径编辑入口，
        // 与闭合 Polyline 的轮廓模式一致——布尔骨架点本来就不允许拖拽
        // （VertexHandles 不生成、UpdateVertexPosition 早退），骨架模式没有可操作手柄。
        if (value?.TypeKey == nameof(L1Primitive.BooleanPath)
            && OutlineEditMode != OutlineEditMode.Outline)
            OutlineEditMode = OutlineEditMode.Outline;
        RefreshVertexHandles();
        RefreshSegmentHandles();
        RefreshRingHandles();
        // SelectedItem 改变时，若当前是轮廓模式也刷新接缝手柄
        if (OutlineEditMode == OutlineEditMode.Outline)
            RefreshOutlineCornerHandles();
    }

    /// <summary>PathData 变化时刷新顶点手柄（拖拽端点会改 PathData，
    /// 此时也要同步顶点手柄位置；但拖拽手柄本身改 PathData 时会跳过本回调，
    /// 由 UpdateVertexPosition 主动同步手柄，避免循环）。</summary>
    private bool _suppressVertexRefresh;

    /// <summary>任意顶点 Incoming/Outgoing 厚度改变 → 刷新 ThicknessHandles（端点位置）+ ThicknessLines（画布虚线）。
    /// 链路：VertexThickness.OnIncoming/OutgoingThicknessChanged → PrimitiveItem.VertexThicknessDataChanged → 此方法。</summary>
    private void OnSelectedVertexThicknessDataChanged(object? sender, EventArgs e)
    {
        if (OutlineEditMode != OutlineEditMode.Thickness) return;
        RefreshThicknessHandles();
    }

    private void OnSelectedItemPathDataChanged()
    {
        if (_suppressVertexRefresh) return;
        RefreshVertexHandles();
        RefreshSegmentHandles();
        if (OutlineEditMode == OutlineEditMode.Outline)
            RefreshOutlineCornerHandles();
        RefreshThicknessHandles();
    }

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrimitiveItem.Width) ||
            e.PropertyName == nameof(PrimitiveItem.Height))
        {
            RaiseMaxChanged();
            // 圆环：外径变 → 刷新外圆/内圆手柄位置
            RefreshRingHandles();
        }
        else if (e.PropertyName == nameof(PrimitiveItem.PathData))
        {
            OnSelectedItemPathDataChanged();
        }
        else if (e.PropertyName == nameof(PrimitiveItem.X) ||
                 e.PropertyName == nameof(PrimitiveItem.Y))
        {
            // 图元被拖动 → 顶点画布坐标跟着变，刷新手柄位置
            RefreshVertexHandles();
            RefreshSegmentHandles();
            // 轮廓手柄位置也要同步（外尖点坐标 = item.X + 局部坐标）
            if (OutlineEditMode == OutlineEditMode.Outline)
                RefreshOutlineCornerHandles();
            RefreshThicknessHandles();
            // 圆环：拖圆心手柄 → X/Y 变 → 刷新圆心/外圆/内圆手柄位置
            RefreshRingHandles();
        }
        else if (e.PropertyName == nameof(PrimitiveItem.InnerRadius))
        {
            // 圆环：内径变 → 刷新内圆手柄位置
            RefreshRingHandles();
        }
        else if (e.PropertyName == nameof(PrimitiveItem.IsClosed))
        {
            // Polyline 开关闭合：刷新轮廓手柄（闭合后可编辑各骨架点圆角）+ 模式可用性
            OnPropertyChanged(nameof(CanEnterOutlineMode));
            if (OutlineEditMode == OutlineEditMode.Outline)
                RefreshOutlineCornerHandles();
        }
        else if (e.PropertyName == nameof(PrimitiveItem.CollinearSplitPairs))
        {
            // 合并模式切换 → 刷新厚度端点可见性（入端在共线合并顶点处隐藏）+ 属性面板骨点显示标志
            RefreshThicknessHandles();
        }
        else if (e.PropertyName == nameof(PrimitiveItem.FillColor)
              || e.PropertyName == nameof(PrimitiveItem.StrokeColor)
              || e.PropertyName == nameof(PrimitiveItem.GradientStartColor)
              || e.PropertyName == nameof(PrimitiveItem.GradientEndColor))
        {
            // 外观颜色字段变化（用户/序列化/拖动 改值）→ 推入最近使用颜色环形缓冲
            string? hex = e.PropertyName switch
            {
                nameof(PrimitiveItem.FillColor)         => sender is PrimitiveItem p1 ? p1.FillColor : null,
                nameof(PrimitiveItem.StrokeColor)       => sender is PrimitiveItem p2 ? p2.StrokeColor : null,
                nameof(PrimitiveItem.GradientStartColor) => sender is PrimitiveItem p3 ? p3.GradientStartColor : null,
                nameof(PrimitiveItem.GradientEndColor)   => sender is PrimitiveItem p4 ? p4.GradientEndColor : null,
                _ => null,
            };
            if (!string.IsNullOrEmpty(hex))
                PushRecentColor(hex);
        }
    }

    /// <summary>把 hex 推到 RecentColorings 队首（去重 + 上限 10）。
    /// 无效 hex 忽略；空值清空最近使用 → 用户清空场不会留下"幽灵色"。</summary>
    private void PushRecentColor(string hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            int existing = RecentColorings.IndexOf(hex);
            if (existing >= 0) RecentColorings.RemoveAt(existing);
            RecentColorings.Insert(0, hex);
            while (RecentColorings.Count > 10) RecentColorings.RemoveAt(RecentColorings.Count - 1);
        }
    }

    private void RaiseMaxChanged()
    {
        OnPropertyChanged(nameof(SelectedCornerRadiusMax));
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        // 阵列整体删除：AutoCAD 关联阵列语义，Delete 整组移除（点任一副本 → 整组删除）
        if (SelectedArray is { } arr)
        {
            // 先收口上一段未完成编辑:每次删除 = 独立撤销步骤
            ForceCommitEdit();
            ExitArrayEdit();             // 关闭编辑浮层（_editingArray 悬空前清引用）
            SelectedArray = null;        // 先清选中，避免 Remove 触发 Rebuild 后高亮残留
            ArrayInstances.Remove(arr);  // 触发 OnArrayInstancesChanged → Rebuild + 撤销快照
            return;
        }
        // 编辑源图元不可直接删除：其语义 = 删除整个阵列，需先退出编辑源（✕/点空白）再删除阵列
        if (SelectedItem is { } src && _isArraySourceEditMode && src.Id == _arraySourceEditGuid)
            return;
        if (SelectedItem is null) return;
        // 先收口上一段未完成编辑:每次删除按钮点击 = 独立撤销步骤
        ForceCommitEdit();
        var toRemove = SelectedItem;
        SelectedItem = null;
        Primitives.Remove(toRemove); // 触发 OnPrimitivesChanged → 自动记录可撤销快照
        ItemCount = Primitives.Count;
        PruneConstraints(); // 图元删除 → 清理涉及它的相切约束
        // 若删的是布尔源图元 → 相关 BooleanPath 的"重新合并"可用态失效，刷新按钮
        OnPropertyChanged(nameof(CanRecomputeBoolean));
    }

    [RelayCommand]
    private void ClearAll()
    {
        // 清空前先把上一段未完成的编辑收口(否则快速连点会被合并)
        ForceCommitEdit();
        ClearAllCore(scheduleUndo: true);
    }

    /// <summary>清空画布集合。scheduleUndo=true 时把"清空前状态"登记为可撤销(仅 ClearAllCommand 用);
    /// 撤销/重做恢复与工程加载传 false,避免清空动作本身进历史。
    /// 同时清空 ArrayInstances（沿路径阵列也是画布内容，"全部清空"应一并清空）。
    /// Clear() 会触发 OnArrayInstancesChanged → RebuildArrayRenderItems() 自动同步渲染层。</summary>
    private void ClearAllCore(bool scheduleUndo)
    {
        // 编辑源模式：先提交临时源图元，避免其残留在集合中
        if (_isArraySourceEditMode) CollapseArraySourceEdit();
        SelectedItem = null;
        SelectedArray = null; // 阵列整体选择一并清空（SelectedItem 可能已为 null 不触发互斥，需显式清）
        if (_isArrayEditMode) ExitArrayEdit(); // 清空/撤销恢复关闭编辑浮层，避免 _editingArray 悬空
        if (scheduleUndo)
            ScheduleUndoCommit(); // 清空是可撤销步骤:先记前置快照
        // 清空画布 → 调整源现场一并废弃（源随集合清空，还原字典失去意义）
        _sourceAdjustTarget = Guid.Empty;
        _sourceZRestore.Clear();
        IsSourceAdjustMode = false;
        Primitives.Clear();
        ArrayInstances.Clear();
        Constraints.Clear(); // 清空画布 → 相切约束一并清空
        ItemCount = 0;
    }

    /// <summary>剪贴板 DTO 缓存：Ctrl+C 复制当前选中图元的深拷贝（DTO 序列化，与撤销/保存同源）。
    /// 应用内复制语义：缓存内存即可，无需系统剪贴板（跨进程粘贴不在本需求内）。</summary>
    private PrimitiveItemDto? _copiedDto;

    /// <summary>复制选中图元（Ctrl+C）。复制不影响画布内容/脏状态。</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void CopySelection()
    {
        if (SelectedItem is null) return;
        _copiedDto = ToDto(SelectedItem);
    }

    /// <summary>焦点在文本输入框时不拦截按键：Ctrl+C/V 让给系统剪贴板（文本语义），
    /// 由 WPF 键盘路由把命令不可用时的按键继续传递给 TextBox。</summary>
    private bool CanCopySelection()
        => SelectedItem is not null && !IsTextFieldFocused();

    /// <summary>粘贴图元副本（Ctrl+V）：以 _copiedDto 重建，整体偏移 (24,24) 防与源重叠。</summary>
    [RelayCommand(CanExecute = nameof(CanPasteCopied))]
    private void PasteCopied()
    {
        if (_copiedDto is null) return;
        ForceCommitEdit();
        // 每次粘贴独立深拷贝（CreateFromDto→ToDto 往返），_copiedDto 保持原始快照可反复粘贴。
        // 空 Id → CreateFromDto 内部的 Guid.TryParse 失败 → 分配新 Guid：副本与源不同身份，
        // 否则约束引用/选中/持久化全部串线（PrimitiveItemDto.Id 为 init-only，须经 DTO 通道重置）。
        var dto = ToDto(CreateFromDto(_copiedDto));
        dto.Id = string.Empty;
        var clone = CreateFromDto(dto);
        clone.X += 24;
        clone.Y += 24;
        Primitives.Add(clone); // → OnPrimitivesChanged → IsDirty + 撤销快照
        ItemCount = Primitives.Count;
        SelectedItem = clone;  // 粘贴后直接选中新副本，便于立即拖动/改属性
    }

    private bool CanPasteCopied()
        => _copiedDto is not null && !IsTextFieldFocused();

    /// <summary>当前键盘焦点是否在文本输入控件（TextBox/PasswordBox）。</summary>
    private static bool IsTextFieldFocused()
        => System.Windows.Input.Keyboard.FocusedElement
            is System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox;
}