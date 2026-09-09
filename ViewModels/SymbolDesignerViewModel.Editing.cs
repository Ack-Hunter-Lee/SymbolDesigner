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

        // 正处于"调整源"而选中切换到其它（含取消；点物化源本身不算切走）→ 自动收起源，避免源残留画布
        if (IsSourceAdjustMode
            && value?.Id != _sourceAdjustTarget
            && !IsActiveBooleanSourceItem(value))
        {
            var t = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
            if (t is not null)
            {
                CollapseSourceAdjust(t);
                NotifyBooleanCanExecuteChanged();
            }
            else
            {
                _sourceAdjTemp.Clear();
                _sourceAdjustTarget = Guid.Empty;
                IsSourceAdjustMode = false;
            }
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
        OnPropertyChanged(nameof(SelectedItemIsBandPolyline));
        OnPropertyChanged(nameof(SelectedItemIsPolyline));
        OnPropertyChanged(nameof(SelectedItemIsRing));
        OnPropertyChanged(nameof(SelectedItemIsBooleanPath));
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
        NotifyBooleanCanExecuteChanged(); // 刷新"CanRecomputeBoolean"属性 + 调整源/重新合并按钮
        // 格式刷按钮可用态：选中变化（多选累积切到单选、选中切到 null）→ 通知 PickFormatPainter 重新评估 CanExecute
        PickFormatPainterCommand.NotifyCanExecuteChanged();

        // 选中 BandPolyline / Polyline → 自动进入对应画线模式（光标变十字，CanvasHost 直接接收点击起笔）。
        // 尊重用户手动选择：若用户从 ComboBox 手动选定过模式（_userChangedDrawMode），
        // 则选中联动不再覆盖；取消选中 / 选中其它类型时，若当前正处于"自动进入"的
        // 画线模式则退回 None，避免残留绘制状态误伤后续操作。
        var isBandPolyline = value is not null
            && value.TypeKey == nameof(L1Primitive.BandPolyline);
        var isPolyline = value is not null
            && value.TypeKey == nameof(L1Primitive.Polyline);
        if (isBandPolyline && !_userChangedDrawMode && DrawMode != DrawMode.BandPolyline)
        {
            SetDrawModeFromSelection(DrawMode.BandPolyline);
        }
        else if (isPolyline && !_userChangedDrawMode && DrawMode != DrawMode.Polyline)
        {
            SetDrawModeFromSelection(DrawMode.Polyline);
        }
        else if (!isBandPolyline && !isPolyline && !_userChangedDrawMode
                 && (DrawMode == DrawMode.BandPolyline || DrawMode == DrawMode.Polyline))
        {
            SetDrawModeFromSelection(DrawMode.None);
        }

        // 监听 SelectedItem 的 Width/Height/PathData 变化（尺寸改 → 刷 Max；PathData 改 → 刷顶点手柄）
        if (_lastSelected != null)
        {
            _lastSelected.PropertyChanged -= OnSelectedItemPropertyChanged;
            _lastSelected.VertexBandWidthDataChanged -= OnSelectedVertexBandWidthDataChanged;
        }
        _lastSelected = value;
        if (value != null)
        {
            value.PropertyChanged += OnSelectedItemPropertyChanged;
            // 订阅顶点带宽数据变化（Slider 改 Incoming/OutgoingBandWidth 时刷新 BandWidthHandles/BandWidthLines）
            value.VertexBandWidthDataChanged += OnSelectedVertexBandWidthDataChanged;
        }
        else
        {
            // 取消选中元素：清掉所有挂在旧 item 上的子状态，避免画布残留接缝/带宽手柄和属性面板误显示
            SelectedOutlineCorner = null;
            SelectedBonePointIndex = -1;
            // BandWidthHandles / OutlineCornerHandles 集合里残留的是上一个 item 的 X/Y，
            // 必须显式刷新（Clear + SelectedItem==null return）才能让画布 ItemsControl 真正清空
            RefreshBandWidthHandles();
            RefreshRingHandles();
            if (OutlineEditMode == OutlineEditMode.Outline)
                RefreshOutlineCornerHandles();
        }

        RaiseMaxChanged();
        OnPropertyChanged(nameof(CanEnterOutlineMode));
        OnPropertyChanged(nameof(CanEnterBandWidthMode));
        // 切换选中 → 重新计算非转角点圆角半径上限（依赖 SelectedItem.PathData）
        OnPropertyChanged(nameof(CornerArcRadiusMax));
        // 选区缩放手柄随选中变化刷新（多选集合变化已由 RaiseMultiSelectChanged 处理,这里是单选焦点变化）
        RefreshScaleSelectionHandles();
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

    /// <summary>任意顶点 Incoming/Outgoing 带宽改变 → 刷新 BandWidthHandles（端点位置）+ BandWidthLines（画布虚线）。
    /// 链路：VertexBandWidth.OnIncoming/OutgoingBandWidthChanged → PrimitiveItem.VertexBandWidthDataChanged → 此方法。</summary>
    private void OnSelectedVertexBandWidthDataChanged(object? sender, EventArgs e)
    {
        if (OutlineEditMode != OutlineEditMode.BandWidth) return;
        RefreshBandWidthHandles();
    }

    private void OnSelectedItemPathDataChanged()
    {
        if (_suppressVertexRefresh) return;
        RefreshVertexHandles();
        RefreshSegmentHandles();
        if (OutlineEditMode == OutlineEditMode.Outline)
            RefreshOutlineCornerHandles();
        RefreshBandWidthHandles();
    }

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 多选批量外观同步：属性面板改的值落在 SelectedItem 上 → 广播给 MultiSelected 其余成员。
        // CAD 语义：批量选中后改颜色/线宽/填充等外观属性 = 对整组生效。
        // 仅同步"通用外观属性"（颜色/线宽/填充/透明度/虚线），几何属性（X/Y/W/H/PathData）保持单图元编辑。
        if (MultiSelected.Count > 1 && IsAppearanceProperty(e.PropertyName))
        {
            SyncAppearanceToMultiSelected(sender as PrimitiveItem, e.PropertyName!);
        }

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
            // PathData 变 → 段长集合变 → 非转角点圆角半径上限（最短段长/2）跟着变
            OnPropertyChanged(nameof(CornerArcRadiusMax));
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
            RefreshBandWidthHandles();
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
            // 合并模式切换 → 刷新带宽端点可见性（入端在共线合并顶点处隐藏）+ 属性面板骨点显示标志
            RefreshBandWidthHandles();
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

    /// <summary>是否为可批量同步的"通用外观属性"（多选时广播给其余成员）。</summary>
    private static bool IsAppearanceProperty(string? name) => name is
        nameof(PrimitiveItem.StrokeColor) or
        nameof(PrimitiveItem.StrokeWidth) or
        nameof(PrimitiveItem.StrokeDashStyle) or
        nameof(PrimitiveItem.FillStyle) or
        nameof(PrimitiveItem.FillColor) or
        nameof(PrimitiveItem.GradientStartColor) or
        nameof(PrimitiveItem.GradientEndColor) or
        nameof(PrimitiveItem.FillBrightness) or
        nameof(PrimitiveItem.FillOpacity) or
        nameof(PrimitiveItem.Opacity);

    /// <summary>把 SelectedItem 上已变化的外观属性值广播到 MultiSelected 其余成员。
    /// 用反射按属性名取值/赋值,避免为十几个属性手写 if 分支。
    /// 只同步外观属性（调用方已用 IsAppearanceProperty 过滤）,不触发撤销（连动系统默认整体撤销）。</summary>
    private void SyncAppearanceToMultiSelected(PrimitiveItem? source, string propertyName)
    {
        if (source is null) return;
        var prop = typeof(PrimitiveItem).GetProperty(propertyName);
        if (prop is null || !prop.CanRead || !prop.CanWrite) return;
        var value = prop.GetValue(source);
        foreach (var p in MultiSelected)
        {
            if (p == source) continue;
            prop.SetValue(p, value);
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

        // 确定删除集合：多选优先（右键框选 / Ctr 多选 → 整组删）；否则退回单选焦点
        var toRemove = MultiSelected.Count > 0
            ? MultiSelected.ToList()
            : (SelectedItem is { } single ? new List<PrimitiveItem> { single } : null);
        if (toRemove is null || toRemove.Count == 0) return;

        // 先收口上一段未完成编辑:每次删除按钮点击 = 独立撤销步骤
        ForceCommitEdit();
        SelectedItem = null;   // 触发 ClearMultiSelect：清多选集合 + 复位高亮、刷新缩放手柄
        foreach (var item in toRemove)
            Primitives.Remove(item); // 触发 OnPrimitivesChanged → 去抖合并为一次可撤销快照
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
        // 清空画布 → 调整源现场一并废弃（物化源随 Primitives.Clear 一并移除）
        _sourceAdjustTarget = Guid.Empty;
        _sourceAdjTemp.Clear();
        IsSourceAdjustMode = false;
        Primitives.Clear();
        ArrayInstances.Clear();
        Constraints.Clear(); // 清空画布 → 相切约束一并清空
        ItemCount = 0;
    }

    /// <summary>剪贴板 DTO 缓存：Ctrl+C 复制当前选中图元的深拷贝（DTO 序列化，与撤销/保存同源）。
    /// 支持多选批量复制：Ctrl+C 把 MultiSelected 全部成员存为列表；单选退化为一元列表。
    /// 应用内复制语义：缓存内存即可，无需系统剪贴板（跨进程粘贴不在本需求内）。</summary>
    private List<PrimitiveItemDto>? _copiedDtos;

    /// <summary>复制选中图元（Ctrl+C）。多选时复制整个选集；单选复制当前焦点。不影响画布内容/脏状态。</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void CopySelection()
    {
        // 多选优先：复制整个 MultiSelected；否则退化到单选 SelectedItem。
        var sources = MultiSelected.Count > 0 ? MultiSelected.ToList() : (SelectedItem is { } s ? new List<PrimitiveItem> { s } : null);
        if (sources is null || sources.Count == 0) return;
        _copiedDtos = sources.Select(ToDto).ToList();
    }

    /// <summary>焦点在文本输入框时不拦截按键：Ctrl+C/V 让给系统剪贴板（文本语义），
    /// 由 WPF 键盘路由把命令不可用时的按键继续传递给 TextBox。</summary>
    private bool CanCopySelection()
        => (MultiSelected.Count > 0 || SelectedItem is not null) && !IsTextFieldFocused();

    /// <summary>粘贴图元副本（Ctrl+V）：以 _copiedDtos 重建全部，整体偏移 (24,24) 防与源重叠。
    /// 粘贴后自动选中所有新副本（可立即整体拖动/批量改属性）。</summary>
    [RelayCommand(CanExecute = nameof(CanPasteCopied))]
    private void PasteCopied()
    {
        if (_copiedDtos is null || _copiedDtos.Count == 0) return;
        ForceCommitEdit();
        var clones = new List<PrimitiveItem>();
        foreach (var dto in _copiedDtos)
        {
            // 每次粘贴独立深拷贝（CreateFromDto→ToDto 往返），_copiedDtos 保持原始快照可反复粘贴。
            // 空 Id → CreateFromDto 内部的 Guid.TryParse 失败 → 分配新 Guid：副本与源不同身份，
            // 否则约束引用/选中/持久化全部串线（PrimitiveItemDto.Id 为 init-only，须经 DTO 通道重置）。
            var copy = ToDto(CreateFromDto(dto));
            copy.Id = string.Empty;
            var clone = CreateFromDto(copy);
            clone.X += 24;
            clone.Y += 24;
            clones.Add(clone);
        }
        foreach (var clone in clones)
            Primitives.Add(clone); // → OnPrimitivesChanged → IsDirty + 撤销快照
        ItemCount = Primitives.Count;
        // 选中全部新副本：批量粘贴后整体拖动/改属性（替换 SelectedItem，并清空 MultiSelected 再重建）。
        MultiSelected.Clear();
        foreach (var c in clones) MultiSelected.Add(c);
        SelectedItem = clones.FirstOrDefault();  // 焦点 = 第一个副本，供属性面板展示
        // 多选集合变化 → 刷新选区缩放手柄/约束命令可执行态（与 AddToMultiSelect 的 RaiseMultiSelectChanged 同效）
        RaiseMultiSelectChanged();
    }

    private bool CanPasteCopied()
        => (_copiedDtos is not null && _copiedDtos.Count > 0) && !IsTextFieldFocused();

    /// <summary>当前键盘焦点是否在文本输入控件（TextBox/PasswordBox）。</summary>
    private static bool IsTextFieldFocused()
        => System.Windows.Input.Keyboard.FocusedElement
            is System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox;
}