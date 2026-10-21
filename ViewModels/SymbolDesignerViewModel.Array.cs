using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>阵列（AutoCAD 关联阵列语义）：选中整体 / 编辑参数 / 编辑源本体实时同步副本。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>当前选中的阵列（AutoCAD 关联阵列语义：点任一副本 → 整个阵列作为单一对象选中）。
    /// 与 SelectedItem 互斥：选阵列时清图元选择、选图元时清阵列选择。
    /// 高亮 = 该阵列全部克隆副本 IsSelected=true（整体描边，视觉提示"这是一个整体"）。</summary>
    [ObservableProperty] private Models.ArrayInstance? _selectedArray;

    partial void OnSelectedArrayChanged(Models.ArrayInstance? value)
    {
        SyncArraySelectionHighlight();
        OnPropertyChanged(nameof(HasSelectedArray)); // 浮层"编辑源"按钮显隐
    }

    /// <summary>选中一个阵列作为整体（点任一副本时由 DraggablePrimitive.OnSelfClick 调用）。
    /// 清空图元选择（OnSelectedItemChanged 内部会同步清旧阵列选择/多选）→ 该阵列全部副本高亮。
    /// 之后：拖动任一副本 = 整体平移；Delete = 整组删除；参数在浮层实时编辑（B 档）。</summary>
    public void SelectArray(Models.ArrayInstance arr)
    {
        SelectedItem = null;  // 清图元选择（互斥；内部同步清 SelectedArray/多选）
        SelectedArray = arr;  // 设阵列选中 → 高亮
        StartArrayEdit(arr);  // 自动打开编辑浮层（AutoCAD 上下文面板语义：选中即编辑）
    }

    // ── B 档：编辑已有阵列（复用预览浮层，改动即实时写回）──

    /// <summary>是否处于"编辑已有阵列"模式（与创建预览共用同一浮层 UI；参数改动 → 写回选中阵列）。
    /// 无需绑定 XAML（浮层标题/按钮由 ArrayPanel* 手动通知），普通字段即可。</summary>
    private bool _isArrayEditMode;

    // ── C 档：编辑阵列源本体（改动实时同步全部副本，AutoCAD 关联阵列核心语义）──

    /// <summary>是否处于"编辑阵列源本体"模式：源被借出为画布上可编辑的普通图元，
    /// 源位克隆隐藏、其余副本实时跟随源几何变化；退出时提交回 ArrayInstance.Source。
    /// 无需绑定 XAML（浮层标题由 ArrayPanelTitle 手动通知），普通字段即可。</summary>
    private bool _isArraySourceEditMode;

    /// <summary>编辑中的源图元 id（挂在主集合 Primitives 中）。</summary>
    private Guid _arraySourceEditGuid = Guid.Empty;

    /// <summary>编辑目标阵列（= 当前 SelectedArray）。</summary>
    private Models.ArrayInstance? _arraySourceEditFor;

    /// <summary>是否有阵列被整体选中（浮层"编辑源"按钮显隐）。</summary>
    public bool HasSelectedArray => SelectedArray is not null;

    /// <summary>浮层标题：编辑源 / 编辑阵列 / 创建预览 三态区分。</summary>
    public string ArrayPanelTitle =>
        _isArraySourceEditMode ? "编辑源（改动实时同步副本）" :
        _isArrayEditMode ? "编辑阵列" : "阵列（预览）";

    /// <summary>进入"编辑源"（C 档）：把阵列源借出为画布上可编辑的普通图元（置于源原始位置 + 阵列偏移），
    /// 选中它 → 属性面板/拖动手柄/改形状都可用；期间其他副本实时跟随。
    /// 退出（✓/✕/点空白/切走选中）由 CollapseArraySourceEdit 提交回 ArrayInstance.Source。</summary>
    [RelayCommand]
    private void EnterArraySourceEdit()
    {
        if (SelectedArray is not { } arr || arr.Source is null || _isArraySourceEditMode) return;
        // 若此刻还在"创建预览"态（理论不会：点阵列副本已切编辑）→ 防御性退出
        if (IsPathArrayPreviewOpen && !_isArrayEditMode) ExitArrayPreview();
        // 与布尔"调整源"互斥：先收起源（同为"编辑源"模式，同时只允许一个）
        if (IsSourceAdjustMode && _sourceAdjustTarget != Guid.Empty)
        {
            var t = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
            if (t is { TypeKey: nameof(L1Primitive.BooleanPath) }) CollapseSourceAdjust(t);
            _sourceAdjustTarget = Guid.Empty;
            IsSourceAdjustMode = false;
        }
        // 借出源：从 Source 克隆出可编辑图元，放在源原始位置 + 阵列整体偏移（用户可继续拖动改位置）
        var edit = CreateFromDto(arr.Source);
        edit.X = arr.Source.X + arr.OffsetX;
        edit.Y = arr.Source.Y + arr.OffsetY;
        edit.ZIndex = Primitives.Count + 10; // 置顶，明确当前编辑对象
        _arraySourceEditFor = arr;
        _arraySourceEditGuid = edit.Id;
        Primitives.Add(edit);      // 触发集合渲染
        _isArraySourceEditMode = true; // 在 Source 与集合就绪后才置位（OnSelectedItemChanged/OnCollectionChanged 会读取）
        edit.PropertyChanged += OnArraySourceGeometryChanged; // 几何变化 → 实时同步全部副本
        RebuildArrayRenderItems(); // 隐藏源位克隆（BuildArrayClones 检测 IsArraySourceEditMode）
        SelectedItem = edit;       // 属性面板可用（OnSelectedItemChanged 有守卫不关浮层）
        OnPropertyChanged(nameof(ArrayPanelTitle));
        // 编辑源全程 = 一次撤销手势：进入 BeginEdit（前置快照，途中集合/几何变化不落栈），
        // Collapse/退出时 EndEdit 收口为一步。避免中途把临时 edit 图元写进撤销历史。
        BeginEdit();
    }

    /// <summary>编辑源图元几何变化 → 实时同步 Source 并重建全部副本（拖动/属性面板即跟随）。</summary>
    private void OnArraySourceGeometryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PrimitiveItem edit) return;
        if (edit.Id != _arraySourceEditGuid || _arraySourceEditFor is not { } arr) return;
        // 仅几何属性触发同步；样式/可见性等不重建（避免冗余）
        if (e.PropertyName is not (nameof(PrimitiveItem.X) or nameof(PrimitiveItem.Y)
            or nameof(PrimitiveItem.Width) or nameof(PrimitiveItem.Height)
            or nameof(PrimitiveItem.PathData) or nameof(PrimitiveItem.InnerRadius)
            or nameof(PrimitiveItem.CornerRadius) or nameof(PrimitiveItem.Rotation)))
            return;
        arr.Source = ToDto(edit); // 直接引用 Source DTO（画布快照/序列化都会带走）
        RebuildArrayRenderItems(); // 副本从新 Source 重建，实时跟随
        IsDirty = true;
        ScheduleUndoCommit(); // 解除手势在拖动时去抖合并；属性面板改动单独登记
    }

    /// <summary>关闭编辑源：提交当前编辑结果回 ArrayInstance.Source，摘除临时图元，恢复副本渲染。
    /// 返回后停留在"编辑阵列"浮层（参数可继续调）。</summary>
    private void CollapseArraySourceEdit()
    {
        if (!_isArraySourceEditMode || _arraySourceEditFor is not { } arr) return;
        var edit = Primitives.FirstOrDefault(p => p.Id == _arraySourceEditGuid);
        if (edit is not null)
        {
            edit.PropertyChanged -= OnArraySourceGeometryChanged;
            arr.Source = ToDto(edit); // 提交：位置/形状变化 → 后续 Rebuild 整体基于新源
            Primitives.Remove(edit);
        }
        _isArraySourceEditMode = false;
        _arraySourceEditGuid = Guid.Empty;
        _arraySourceEditFor = null;
        OnPropertyChanged(nameof(ArrayPanelTitle));
        RebuildArrayRenderItems(); // 源位克隆恢复显示
        // 收口"编辑源"手势：整个源编辑过程合并为一次撤销（无实际改动时 ForceCommit 自动丢弃）
        EndEdit();
    }

    /// <summary>编辑模式的目标阵列（= 当前 SelectedArray）。</summary>
    private Models.ArrayInstance? _editingArray;

    /// <summary>浮层确认按钮文案（编辑模式参数已实时生效，按钮仅"完成"关闭）。</summary>
    public string ArrayPanelConfirmText => _isArrayEditMode ? "✓ 完成" : "✓ 创建";

    /// <summary>进入编辑：把选中阵列参数回填浮层并打开。</summary>
    private void StartArrayEdit(Models.ArrayInstance arr)
    {
        _editingArray = arr;
        _isArrayEditMode = true;
        OnPropertyChanged(nameof(ArrayPanelTitle));
        OnPropertyChanged(nameof(ArrayPanelConfirmText));
        OnPropertyChanged(nameof(ArrayPathPickerVisible)); // 编辑模式无路径候选 → 隐藏下拉
        LoadArrayParamsToPreview(arr);
        // 显式刷新一次：编辑态打开即按该阵列生成参考圆环 + 重建渲染。
        // 不能只依赖 LoadArrayParamsToPreview 的副作用（参数与上次同值时 ObservableProperty
        // 等值不触发 → 参考圆环会残留上一次的陈旧数据，与副本错位）。
        ApplyArrayEdit();
        IsPathArrayPreviewOpen = true;
    }

    /// <summary>退出编辑：关闭浮层（参数已实时生效，无需回滚）。</summary>
    private void ExitArrayEdit()
    {
        _isArrayEditMode = false;
        _editingArray = null;
        IsPathArrayPreviewOpen = false;
        PathArrayPreviewItems.Clear();
        OnPropertyChanged(nameof(ArrayPanelTitle));
        OnPropertyChanged(nameof(ArrayPanelConfirmText));
        OnPropertyChanged(nameof(ArrayPathPickerVisible));
    }

    /// <summary>把阵列当前参数回填到浮层绑定属性（进入编辑时显示现有值）。</summary>
    private void LoadArrayParamsToPreview(Models.ArrayInstance arr)
    {
        switch (arr.Type)
        {
            case Models.ArrayType.Rectangular:
                ArrayPreviewColumns = Math.Clamp(arr.Columns, 1, 100);
                ArrayPreviewRows = Math.Clamp(arr.Rows, 1, 100);
                ArrayPreviewColSpacing = arr.ColSpacing;
                ArrayPreviewRowSpacing = arr.RowSpacing;
                break;
            case Models.ArrayType.Polar:
                PathArrayCount = Math.Clamp(arr.Count, 1, 100);
                ArrayPreviewCenterX = arr.CenterX;
                ArrayPreviewCenterY = arr.CenterY;
                ArrayPreviewFillAngle = arr.FillAngle;
                ArrayPreviewRotateItems = arr.RotateItems;
                break;
            default:
                PathArrayCount = Math.Clamp(arr.Count, 1, 100);
                // 编辑已有阵列：显示其已存偏移并退出"自动保持"（否则会被当前距离冲掉）。
                // 回填期间挂起重入守卫，避免距离/符号互相触发重建覆盖未加载的字段。
                _syncingArrayOffset = true;
                PathArrayOffsetAuto = false;
                PathArrayOffset = arr.PathOffset;
                _pathArrayOffsetSign = arr.PathOffset < 0 ? -1 : 1;
                PathArrayOffsetDistance = System.Math.Abs(arr.PathOffset);
                PathArrayAlign = arr.PathAlign;
                _syncingArrayOffset = false;
                break;
        }
        ArrayPreviewType = arr.Type; // 触发 handler（此时浮层未开，只刷可见性）
        SelectedArrayTypeOption = ArrayTypeOptions.FirstOrDefault(o => o.Value == arr.Type);
    }

    /// <summary>把浮层当前参数写回选中阵列并实时重建正式渲染层。
    /// 参数改动即生效（AutoCAD 面板语义），撤销由 ScheduleUndoCommit 去抖合并。</summary>
    private void ApplyArrayEdit()
    {
        if (!_isArrayEditMode || _editingArray is not { } arr) return;
        arr.Type = ArrayPreviewType;
        switch (ArrayPreviewType)
        {
            case Models.ArrayType.Rectangular:
                arr.Columns = Math.Clamp(ArrayPreviewColumns, 1, 100);
                arr.Rows = Math.Clamp(ArrayPreviewRows, 1, 100);
                arr.ColSpacing = ArrayPreviewColSpacing;
                arr.RowSpacing = ArrayPreviewRowSpacing;
                break;
            case Models.ArrayType.Polar:
                arr.Count = Math.Clamp(PathArrayCount, 1, 100);
                arr.CenterX = ArrayPreviewCenterX;
                arr.CenterY = ArrayPreviewCenterY;
                arr.FillAngle = ArrayPreviewFillAngle;
                arr.RotateItems = ArrayPreviewRotateItems;
                break;
            default:
                // 路径阵列的折线不改（浮层只调数量 / 偏移距离 / 是否切向旋转）
                arr.Count = Math.Clamp(PathArrayCount, 1, 100);
                arr.PathOffset = PathArrayOffset;
                arr.PathAlign = PathArrayAlign;
                break;
        }
        IsDirty = true;
        ScheduleUndoCommit(); // 参数变化不触发集合事件，手动登记撤销快照（去抖 350ms）
        UpdatePolarGuide(arr); // 编辑态参考圆环随参数实时刷新
        RebuildArrayRenderItems(); // 末尾 SyncArraySelectionHighlight（SelectedArray==arr 保持整体高亮）
    }

    /// <summary>参数变化统一入口：创建预览 → 重建预览层；编辑模式 → 写回阵列并重建正式层。</summary>
    private void OnArrayParamsChanged()
    {
        if (!IsPathArrayPreviewOpen) return;
        if (_isArrayEditMode) ApplyArrayEdit();
        else RebuildArrayPreview();
    }

    /// <summary>点击画布空白：取消全部选中状态（图元焦点 / 多选 / 阵列整体选择）。
    /// 不能只置 SelectedItem=null：选中阵列时它已是 null → OnSelectedItemChanged 不触发，
    /// 必须显式清 SelectedArray，否则阵列高亮残留。由 xaml.cs 空白点击处调用。</summary>
    public void ClearSelection()
    {
        // 编辑源模式：点空白 = 提交源编辑并退出（AutoCAD：点击空白结束源编辑）
        if (_isArraySourceEditMode) CollapseArraySourceEdit();
        SelectedItem = null;
        if (SelectedArray is not null)
            SelectedArray = null;
        ClearMultiSelect(); // SelectedItem 无变化时多选也不会被自动清理，显式清空
        if (_isArrayEditMode) ExitArrayEdit(); // 阵列编辑浮层随之关闭
    }

    /// <summary>把 SelectedArray 的高亮同步到渲染层克隆：选中阵列的全部克隆 IsSelected=true，其余复位。
    /// RebuildArrayRenderItems 重建后克隆 IsSelected 会被 BuildArrayClones 复位为 false，必须重刷一次。</summary>
    private void SyncArraySelectionHighlight()
    {
        var sel = SelectedArray;
        foreach (var c in ArrayRenderItems)
            c.IsSelected = sel is not null && c.ArrayOwner == sel;
    }

    /// <summary>拖动单个阵列副本 → 整体平移阵列（DraggablePrimitive.OnDragDelta 按 ArrayOwner 路由）。
    /// 只改 ArrayInstance.OffsetX/Y（不改源/路径本身）+ 同步平移该阵列克隆。
    /// 不重建 ArrayRenderItems 容器：重建会把正在被拖的 Thumb 从视觉树移除，打断拖动手势。
    /// Offset 已入快照（DTO 序列化），下次 RebuildArrayRenderItems 时以新 Offset 统一重建，无漂移。
    /// 撤销由手势 BeginEdit/EndEdit 包裹（整个拖动手势合并一次快照）。</summary>
    public void MoveArrayElement(Models.ArrayInstance arr, double dx, double dy)
    {
        arr.OffsetX += dx;
        arr.OffsetY += dy;
        // 平移该阵列全部克隆（Canvas.Left/Top 绑定自动跟随 X/Y）
        foreach (var c in ArrayRenderItems)
            if (c.ArrayOwner == arr)
            {
                c.X += dx;
                c.Y += dy;
            }
        // 参考圆环跟随阵列整体位移：副本平移同步于 Offset，圆环圆心也必须同方向平移，
        // 否则拖动途中/刚停下时圆环停在旧圆心 → 视觉错位。仅正在编辑该阵列（圆环正显示）时刷新。
        if (_editingArray == arr)
            UpdatePolarGuide(arr);
    }
}