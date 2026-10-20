using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>图元布尔运算（合并/剪除/相交）+ "物化源 / 调整源 / 重新合并"一轮约束语义。
///
/// 源承载方式：源不再以"画布上保留但隐藏的图元"存在（会泄漏进导出/包围盒/隐藏计数），
/// 而是以【几何快照 DTO】挂载在结果（BooleanPath）上。需要调整时从快照"物化"两个临时源
/// 回 Primitives 进行编辑（编辑时结果实时预览），重新合并把最新源几何烧入结果并更新快照，
/// 收起源/取消则直接丢弃物化源。物化源全程处于一次 BeginEdit→EndEdit 手势内，
/// 不会被单独写进撤销历史，也不会残留到导出/保存（保存与导出前都统一收起）。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>能否执行图元布尔运算（合并/剪除/相交）：恰好选中 2 个且都是"闭合有面积"的图元。
    /// Line / 开放 Polyline 无面积（CanvasFillGeometry=null）→ 不可参与。</summary>
    public bool CanBooleanOp =>
        MultiSelected.Count == 2
        && MultiSelected[0].CanvasFillGeometry != null
        && MultiSelected[1].CanvasFillGeometry != null;

    /// <summary>布尔：合并（选取的 A ∪ B）。</summary>
    [RelayCommand(CanExecute = nameof(CanBooleanOp))]
    private void MergeShapes() => RunBooleanOp(System.Windows.Media.GeometryCombineMode.Union);

    /// <summary>布尔：剪除（先选的 A ∖ 后选的 B，区分顺序）。</summary>
    [RelayCommand(CanExecute = nameof(CanBooleanOp))]
    private void SubtractShapes() => RunBooleanOp(System.Windows.Media.GeometryCombineMode.Exclude);

    /// <summary>布尔：相交（选取的 A ∩ B）。</summary>
    [RelayCommand(CanExecute = nameof(CanBooleanOp))]
    private void IntersectShapes() => RunBooleanOp(System.Windows.Media.GeometryCombineMode.Intersect);

    /// <summary>执行一次布尔运算：两个选中闭合图元 → 新 BooleanPath 图元（多环骨架快照）。
    /// 结果记录运算模式 + 两个源的【几何快照】(顺序 = 选择顺序)，源即刻从画布移除
    /// （不留隐藏图元，天然不进导出/包围盒/隐藏计数）；选中结果可"调整源"按快照物化再"重新合并"。
    /// 结果无面积（如不相交的相交）时拒绝并提示，原图元保留。</summary>
    private void RunBooleanOp(System.Windows.Media.GeometryCombineMode mode)
    {
        if (MultiSelected.Count != 2) return;
        var a = MultiSelected[0]; // 先选 = 被减对象（剪除语义：a ∖ b）
        var b = MultiSelected[1];
        // 锁定保护：任一源图元锁定 → 拒绝布尔运算（结果会吞掉锁定图元，视为破坏）
        if (a.IsLocked || b.IsLocked) return;

        var ga = a.CanvasFillGeometry;
        var gb = b.CanvasFillGeometry;
        if (ga is null || gb is null) return;

        var result = Converters.BooleanOps.Compute(ga, gb, mode);
        if (result is null)
        {
            System.Windows.MessageBox.Show("两个图元不相交，该运算没有有效结果。", "布尔运算",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var item = PrimitiveItem.Create(L1Primitive.BooleanPath,
            result.X, result.Y, result.Width, result.Height);
        item.PathData = result.PathData;
        item.ZIndex = Math.Max(a.ZIndex, b.ZIndex) + 1; // 结果置于输入图元之上
        // 源以几何快照挂载（保留选择顺序，重合并保持 Exclude 的 a∖b 语义）
        item.BooleanMode = mode.ToString();
        item.BooleanSourceSnapshot.Add(ToDto(a));
        item.BooleanSourceSnapshot.Add(ToDto(b));

        Primitives.Add(item);
        Primitives.Remove(a);
        Primitives.Remove(b);
        ItemCount = Primitives.Count;
        ClearMultiSelect();
        SelectedItem = item;
    }

    /// <summary>能否切换"调整源"：选中 BooleanPath 且记录过运算模式 + 至少 2 份源快照。
    /// 老工程（快照为空）→ 禁用。</summary>
    public bool CanToggleSourceAdjust
    {
        get
        {
            if (SelectedItem is not { TypeKey: nameof(L1Primitive.BooleanPath) } item) return false;
            return !string.IsNullOrEmpty(item.BooleanMode) && item.BooleanSourceSnapshot.Count >= 2;
        }
    }

    /// <summary>能否"重新合并"：在"调整源"状态（物化源已挂画布）且快照齐备时才可执行。</summary>
    public bool CanRecomputeBoolean
    {
        get
        {
            if (!IsSourceAdjustMode) return false;
            if (SelectedItem is not { TypeKey: nameof(L1Primitive.BooleanPath) } target) return false;
            return target.Id == _sourceAdjustTarget && target.BooleanSourceSnapshot.Count >= 2;
        }
    }

    /// <summary>当前正处于"调整源"模式的布尔结果 Id（Guid.Empty = 未在调整）。
    /// 切换/取消选中时按此收起临时物化的源，避免残留画布。</summary>
    private Guid _sourceAdjustTarget = Guid.Empty;

    /// <summary>是否处于"调整源"状态（物化源已挂画布、可编辑，结果半透明作参考）。</summary>
    [ObservableProperty] private bool _isSourceAdjustMode;

    /// <summary>进入"调整源"前的结果透明度（退出时恢复，源遮挡结果看不清时结果半透明预览）。</summary>
    private double _sourceAdjustItemOpacity = 1.0;

    /// <summary>本次"调整源"物化的临时源 Id（收起时按此移除并丢弃）。</summary>
    private readonly List<Guid> _sourceAdjTemp = new();

    /// <summary>给定图元是否是当前调整中的某个物化源（用于选中互斥判断：点物化源不收起调整）。</summary>
    private bool IsActiveBooleanSourceItem(PrimitiveItem? item)
        => item is not null && IsSourceAdjustMode && _sourceAdjTemp.Contains(item.Id);

    /// <summary>统一刷新"调整源/重新合并"两处按钮的可用性。</summary>
    private void NotifyBooleanCanExecuteChanged()
    {
        ToggleSourceAdjustCommand.NotifyCanExecuteChanged();
        RecomputeBooleanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>解析当前画布上的布尔物化源图元（物化中才存在；任一被删 → null）。</summary>
    private List<PrimitiveItem>? ResolveBooleanSources(PrimitiveItem item)
    {
        if (item.Id != _sourceAdjustTarget || _sourceAdjTemp.Count < 2) return null;
        var list = new List<PrimitiveItem>(2);
        foreach (var id in _sourceAdjTemp)
        {
            var s = Primitives.FirstOrDefault(p => p.Id == id);
            if (s is null) return null;
            list.Add(s);
        }
        return list;
    }

    /// <summary>深拷贝源快照 DTO（物化独立对象，避免物化途中污染原始快照）。</summary>
    private static PrimitiveItemDto CloneDto(PrimitiveItemDto src)
        => System.Text.Json.JsonSerializer.Deserialize<PrimitiveItemDto>(
            System.Text.Json.JsonSerializer.Serialize(src))!;

    /// <summary>进入"调整源"：按快照物化两个临时源回 Primitives（置顶可编辑），结果半透明作参考。
    /// 全程 = 一次撤销手势：BeginEdit 置位后，物化/编辑/收起不会被拆成多步历史。</summary>
    private void EnterSourceAdjust(PrimitiveItem item)
    {
        int z = item.ZIndex + 1;
        foreach (var snap in item.BooleanSourceSnapshot)
        {
            var srcDto = CloneDto(snap);
            srcDto.Id = string.Empty; // 临时源强制全新 Id（抛灰对象，不占用快照原 Guid）
            var s = CreateFromDto(srcDto);
            s.ZIndex = z++;
            s.Opacity = 1.0;
            s.IsVisible = true;
            Primitives.Add(s);
            _sourceAdjTemp.Add(s.Id);
            // 订阅源几何变化 → 结果实时预览（拖动/改属性即时跟随）
            s.PropertyChanged += OnBooleanSourcePropertyChanged;
        }
        _sourceAdjustItemOpacity = item.Opacity;
        item.Opacity = 0.3;
        _sourceAdjustTarget = item.Id;
        IsSourceAdjustMode = true;
        // 一次撤销手势：临时源/几何变化在 EndEdit 前不落历史栈
        BeginEdit();
        NotifyBooleanCanExecuteChanged();
    }

    /// <summary>源 X/Y/W/H/PathData 等几何属性变化 → 实时按源当前几何重算结果（仅预览）。</summary>
    private void OnBooleanSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PrimitiveItem src) return;
        // 仅几何属性触发预览；样式/可见性等非几何改动不重算（避免冗余工作）
        if (e.PropertyName is nameof(PrimitiveItem.X)
            or nameof(PrimitiveItem.Y)
            or nameof(PrimitiveItem.Width)
            or nameof(PrimitiveItem.Height)
            or nameof(PrimitiveItem.PathData)
            or nameof(PrimitiveItem.InnerRadius)
            or nameof(PrimitiveItem.CornerRadius)
            or nameof(PrimitiveItem.Rotation))
        {
            NotifyBooleanSourceChanged(src);
        }
    }

    /// <summary>收起源（提交或取消都会调用）：移除并丢弃物化源、还原结果透明度、关闭调整态。
    /// 收口"调整源"手势（EndEdit）：有几何变化 → 一步撤销；无变化 → 自动丢弃。
    /// 若结果几何已在编辑中跟随预览变化，则收起源即保留该最新结果（对应"保持当前结果"）。</summary>
    private void CollapseSourceAdjust(PrimitiveItem item)
    {
        foreach (var id in _sourceAdjTemp)
        {
            var s = Primitives.FirstOrDefault(p => p.Id == id);
            if (s is null) continue;
            s.PropertyChanged -= OnBooleanSourcePropertyChanged;
            Primitives.Remove(s); // 丢弃物化源
        }
        _sourceAdjTemp.Clear();
        item.Opacity = _sourceAdjustItemOpacity;
        _sourceAdjustTarget = Guid.Empty;
        IsSourceAdjustMode = false;
        EndEdit(); // 与 EnterSourceAdjust 的 BeginEdit 配对，收口为单次撤销/丢弃
        RefreshScaleSelectionHandles();
        NotifyBooleanCanExecuteChanged();
    }

    /// <summary>切换"调整源"：进入 → 物化源可编辑（结果半透明）；退出 → 收起源（结果保持）。</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSourceAdjust))]
    private void ToggleSourceAdjust()
    {
        if (SelectedItem is not { TypeKey: nameof(L1Primitive.BooleanPath) } item) return;
        if (IsSourceAdjustMode) CollapseSourceAdjust(item);
        else EnterSourceAdjust(item);
    }

    /// <summary>重新合并：选中的 BooleanPath 按当前物化源几何重算结果，并把最新源几何烧入快照，随后收起源。
    /// 失败（删源/不相交）→ 保持调整现场便于继续编辑。</summary>
    [RelayCommand(CanExecute = nameof(CanRecomputeBoolean))]
    private void RecomputeBoolean()
    {
        if (SelectedItem is not { TypeKey: nameof(L1Primitive.BooleanPath) } item) return;
        if (item.IsLocked) return;   // 锁定保护：锁定结果禁止重新合并（会改动几何）
        if (TryRecomputeBooleanCore(item) is null) return;
        CollapseSourceAdjust(item); // 重合并成功 → 收起源（丢弃物化源，一步撤销收口）
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
    }

    /// <summary>重新合并核心：按物化源当前几何重算结果 PathData/X/Y/W/H，并用最新源几何刷新快照。
    /// 不包 BeginEdit/EndEdit——手势由 EnterSourceAdjust(CollapseSourceAdjust 配对管理。
    /// 成功 → 返回 result.PathData；失败（源缺失/无面积/不相交）→ 返回 null（UI 由调用方决定是否提示）。</summary>
    private string? TryRecomputeBooleanCore(PrimitiveItem item)
    {
        var srcs = ResolveBooleanSources(item);
        if (srcs is null || srcs.Count < 2) return null;
        var a = srcs[0];
        var b = srcs[1];
        var ga = a.CanvasFillGeometry;
        var gb = b.CanvasFillGeometry;
        if (ga is null || gb is null) return null;
        var mode = System.Enum.TryParse<System.Windows.Media.GeometryCombineMode>(item.BooleanMode, out var m)
            ? m
            : System.Windows.Media.GeometryCombineMode.Union;
        var result = Converters.BooleanOps.Compute(ga, gb, mode);
        if (result is null) return null;

        item.PathData = result.PathData;
        item.X = result.X;
        item.Y = result.Y;
        item.Width = result.Width;
        item.Height = result.Height;
        // 用物化源当前几何刷新快照（结果自带最新源数据，收起源后仍可再调整）
        item.BooleanSourceSnapshot.Clear();
        item.BooleanSourceSnapshot.Add(ToDto(a));
        item.BooleanSourceSnapshot.Add(ToDto(b));
        return result.PathData;
    }

    private static void ShowInfo(string msg)
        => System.Windows.MessageBox.Show(msg, "布尔运算",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    /// <summary>调整源期间，源 X/Y/W/H/PathData 变化触发"实时预览"：
    /// 按源当前几何临时重算结果 PathData（不提交撤销、不收起源），拖动期间结果即时跟随，
    /// 松开鼠标由 DraggablePrimitive.OnDragCompleted → NotifyBooleanSourceDragEnded 收手。
    /// 仅当源可见（IsSourceAdjustMode=true）时才生效；不命中失败静默忽略。</summary>
    public void NotifyBooleanSourceChanged(PrimitiveItem source)
    {
        if (!IsSourceAdjustMode || _sourceAdjustTarget == Guid.Empty) return;
        var item = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
        if (item is not { TypeKey: nameof(L1Primitive.BooleanPath) }) return;
        if (!_sourceAdjTemp.Contains(source.Id)) return;
        TryRecomputeBooleanCore(item);
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
        RefreshScaleSelectionHandles();
    }

    /// <summary>DraggablePrimitive.OnDragCompleted 调用：源拖动结束 → 收起源（结果已实时跟随）。
    /// 若当前不在调整源模式 / 不是某个 BooleanPath 的物化源 → 静默忽略。</summary>
    public void NotifyBooleanSourceDragEnded(PrimitiveItem source)
    {
        if (!IsSourceAdjustMode || _sourceAdjustTarget == Guid.Empty) return;
        var item = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
        if (item is not { TypeKey: nameof(L1Primitive.BooleanPath) }) return;
        if (!_sourceAdjTemp.Contains(source.Id)) return;
        // 拖动已结束 → 结果实时预览已完成 → 收起源（EndEdit 把本次调整合并为一步撤销）
        CollapseSourceAdjust(item);
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
    }
}