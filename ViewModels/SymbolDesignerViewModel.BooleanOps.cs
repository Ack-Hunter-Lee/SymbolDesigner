using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>图元布尔运算（合并/剪除/相交）+ "保留源 / 调整源 / 重新合并"一轮约束语义。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>布尔运算时是否保留源图元（默认开）。保留后源可继续调整，
    /// 选中结果点"重新合并"按源当前几何重算；关闭则删源（老行为）。</summary>
    [ObservableProperty] private bool _keepBooleanSources = true;

    /// <summary>是否处于"调整源"状态：进入时源图元临时显示到结果之上（可编辑），
    /// 结果半透明作参考；重新合并 / 收起源 / 切走选中后源隐藏、结果恢复不透明。</summary>
    [ObservableProperty] private bool _isSourceAdjustMode;

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
    /// 保留源图元（KeepBooleanSources=true，默认）：结果记录源 Id + 运算模式，源可继续调整，
    /// 之后选中结果"重新合并"按源当前几何重算；关闭则删除源两图元（老行为）。
    /// 结果无面积（如不相交的相交）时拒绝并提示，原图元保留。
    /// 快照 PathData setter 内自动按环点数和同步 VertexThicknesses（圆角半径容器）。</summary>
    private void RunBooleanOp(System.Windows.Media.GeometryCombineMode mode)
    {
        if (MultiSelected.Count != 2) return;
        var a = MultiSelected[0]; // 先选 = 被减对象（剪除语义：a ∖ b）
        var b = MultiSelected[1];

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
        // 记录源引用（顺序 = 选择顺序，重合并保持 Exclude 的 a∖b 语义）
        item.BooleanMode = mode.ToString();
        item.BooleanSourceIds = new List<Guid> { a.Id, b.Id };

        Primitives.Add(item);
        if (!KeepBooleanSources)
        {
            Primitives.Remove(a);
            Primitives.Remove(b);
        }
        else
        {
            // 源默认隐藏（IsVisible=false），仅在选中结果点"调整源"时临时显示
            a.IsVisible = false;
            b.IsVisible = false;
        }
        ItemCount = Primitives.Count;
        ClearMultiSelect();
        SelectedItem = item;
    }

    /// <summary>能否"重新合并"：选中的是 BooleanPath、记录过源引用、且两个源（按 Id）仍存在并有面积。
    /// 源被删除 → false（按钮禁用，提示见 RecomputeBoolean）。</summary>
    public bool CanRecomputeBoolean
    {
        get
        {
            if (SelectedItem is not { TypeKey: nameof(L1Primitive.BooleanPath) } item) return false;
            if (string.IsNullOrEmpty(item.BooleanMode) || item.BooleanSourceIds.Count < 2) return false;
            var a = Primitives.FirstOrDefault(p => p.Id == item.BooleanSourceIds[0]);
            var b = Primitives.FirstOrDefault(p => p.Id == item.BooleanSourceIds[1]);
            return a != null && b != null
                && a.CanvasFillGeometry != null && b.CanvasFillGeometry != null;
        }
    }

    /// <summary>能否切换"调整源"：同"重新合并"可执行条件（源仍存在）。</summary>
    public bool CanToggleSourceAdjust => CanRecomputeBoolean;

    /// <summary>当前正处于"调整源"模式的布尔结果 Id（Guid.Empty = 未在调整）。
    /// 切换/取消选中时按此收起临时显示的源图元，避免残留画布。</summary>
    private Guid _sourceAdjustTarget = Guid.Empty;

    /// <summary>进入"调整源"前的结果透明度（退出时恢复，源遮挡结果看不清时结果半透明预览）。</summary>
    private double _sourceAdjustItemOpacity = 1.0;

    /// <summary>进入"调整源"时临时提升的源图元原始层叠（ZIndex）/透明度快照，退出时还原。
    /// 提升源到结果之上,否则结果（zIndex=max+1）盖住源无法点选编辑。</summary>
    private readonly Dictionary<Guid, (int ZIndex, double Opacity)> _sourceZRestore = new();

    /// <summary>按 Id 解析当前画布上的布尔源图元（任一源被删则返回 null）。</summary>
    private List<PrimitiveItem>? ResolveBooleanSources(PrimitiveItem item)
    {
        if (item.BooleanSourceIds.Count < 2) return null;
        var a = Primitives.FirstOrDefault(p => p.Id == item.BooleanSourceIds[0]);
        var b = Primitives.FirstOrDefault(p => p.Id == item.BooleanSourceIds[1]);
        return a is null || b is null ? null : new List<PrimitiveItem> { a, b };
    }

    /// <summary>隐藏/显示布尔源图元（IsVisible=false 时画布上不可见也不可命中，仅在"调整源"时显示）。</summary>
    private void SetBooleanSourcesVisible(PrimitiveItem item, bool visible)
    {
        var srcs = ResolveBooleanSources(item);
        if (srcs is null) return;
        foreach (var s in srcs) s.IsVisible = visible;
    }

    /// <summary>进入"调整源"：源临时置顶显示（可拖动/改尺寸/改形状），结果半透明作背景参考。</summary>
    private void EnterSourceAdjust(PrimitiveItem item)
    {
        _sourceZRestore.Clear();
        int z = item.ZIndex + 1;
        var srcs = ResolveBooleanSources(item);
        if (srcs is null) return; // 源已被删（按钮态防了，双保险）
        foreach (var s in srcs)
        {
            _sourceZRestore[s.Id] = (s.ZIndex, s.Opacity);
            s.ZIndex = z++;
            s.Opacity = 1.0;
            s.IsVisible = true; // 合并时源已隐藏（IsVisible=false），进入调整必须恢复显示
            // 订阅源属性变化 → 实时预览（结果即时跟随源）
            s.PropertyChanged += OnBooleanSourcePropertyChanged;
        }
        _sourceAdjustItemOpacity = item.Opacity;
        item.Opacity = 0.3;
        _sourceAdjustTarget = item.Id;
        IsSourceAdjustMode = true;
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

    /// <summary>收起源：还原源 ZIndex/透明度（并隐藏源），还原结果透明度。不重算几何。</summary>
    private void CollapseSourceAdjust(PrimitiveItem item)
    {
        foreach (var kv in _sourceZRestore)
        {
            var s = Primitives.FirstOrDefault(p => p.Id == kv.Key);
            if (s is null) continue;
            s.ZIndex = kv.Value.ZIndex;
            s.Opacity = kv.Value.Opacity;
            // 取消属性订阅，避免后续改源几何再次触发预览
            s.PropertyChanged -= OnBooleanSourcePropertyChanged;
        }
        _sourceZRestore.Clear();
        item.Opacity = _sourceAdjustItemOpacity;
        _sourceAdjustTarget = Guid.Empty;
        IsSourceAdjustMode = false;
    }

    /// <summary>切换"调整源"：进入 → 源临时出现可编辑（结果半透明）；退出 → 源收起（结果不变）。</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSourceAdjust))]
    private void ToggleSourceAdjust()
    {
        if (SelectedItem is not { TypeKey: nameof(L1Primitive.BooleanPath) } item) return;
        if (IsSourceAdjustMode) CollapseSourceAdjust(item);
        else EnterSourceAdjust(item);
    }

    /// <summary>重新合并：选中的 BooleanPath 按源图元当前几何重算并替换本结果。
    /// 成功 → 源收起（源只在调整时出现）；失败（源被删/不相交）→ 保持调整现场便于继续编辑。</summary>
    [RelayCommand(CanExecute = nameof(CanRecomputeBoolean))]
    private void RecomputeBoolean()
    {
        var item = SelectedItem as PrimitiveItem;
        if (item is not { TypeKey: nameof(L1Primitive.BooleanPath) }) return;
        if (TryRecomputeBooleanCore(item, commitUndo: true, collapseSources: true) is null) return;
        // 收起源（含结果透明度恢复 + 源隐藏）
        CollapseSourceAdjust(item);
        SetBooleanSourcesVisible(item, false);
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
        RecomputeBooleanCommand.NotifyCanExecuteChanged();
        ToggleSourceAdjustCommand.NotifyCanExecuteChanged();
    }

    /// <summary>重新合并核心：按源当前几何重算结果 PathData/X/Y/W/H。
    /// commitUndo=true → 走 BeginEdit/EndEdit，独立撤销步骤；预览/拖动中应传 false。
    /// collapseSources 仅 caller 用于后续收起源（核心函数本身不动源可见性）。
    /// 成功 → 返回 result.PathData；失败（源缺失/无面积/不相交）→ 返回 null，UI 已弹提示。</summary>
    private string? TryRecomputeBooleanCore(PrimitiveItem item, bool commitUndo, bool collapseSources)
    {
        if (item.BooleanSourceIds.Count < 2) return null;
        var a = Primitives.FirstOrDefault(p => p.Id == item.BooleanSourceIds[0]);
        var b = Primitives.FirstOrDefault(p => p.Id == item.BooleanSourceIds[1]);
        if (a is null || b is null)
        {
            if (commitUndo) ShowInfo("参与运算的源图元已被删除，无法重新合并。");
            return null;
        }
        var ga = a.CanvasFillGeometry;
        var gb = b.CanvasFillGeometry;
        if (ga is null || gb is null) return null;
        var mode = System.Enum.TryParse<System.Windows.Media.GeometryCombineMode>(
            item.BooleanMode, out var m)
            ? m
            : System.Windows.Media.GeometryCombineMode.Union;
        var result = Converters.BooleanOps.Compute(ga, gb, mode);
        if (result is null)
        {
            if (commitUndo) ShowInfo("当前源图元几何使该运算没有有效结果（如不相交），结果保持原状。");
            return null;
        }
        if (commitUndo) BeginEdit();
        item.PathData = result.PathData;
        item.X = result.X;
        item.Y = result.Y;
        item.Width = result.Width;
        item.Height = result.Height;
        if (commitUndo) EndEdit();
        return result.PathData;
    }

    private static void ShowInfo(string msg)
        => System.Windows.MessageBox.Show(msg, "布尔运算",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    /// <summary>调整源期间，源 X/Y/W/H/PathData 变化触发"实时预览"：
    /// 按源当前几何临时重算结果 PathData（不提交撤销、不收起源），
    /// 用户拖动期间结果即时跟随，松开鼠标时由 DraggablePrimitive.OnDragCompleted 触发正式合并 + 收起。
    /// 仅当源可见（IsSourceAdjustMode=true）时才生效；不命中失败（不相交）静默忽略。</summary>
    public void NotifyBooleanSourceChanged(PrimitiveItem source)
    {
        if (!IsSourceAdjustMode || _sourceAdjustTarget == Guid.Empty) return;
        var item = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
        if (item is not { TypeKey: nameof(L1Primitive.BooleanPath) }) return;
        // 只关心本结果引用的源
        if (!item.BooleanSourceIds.Contains(source.Id)) return;
        // 预览：不提交撤销、不弹失败提示
        TryRecomputeBooleanCore(item, commitUndo: false, collapseSources: false);
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
    }

    /// <summary>DraggablePrimitive.OnDragCompleted 调用：源拖动结束 → 触发正式合并并收起源。
    /// 若当前不在调整源模式 / 不是某个 BooleanPath 的源 → 静默忽略。</summary>
    public void NotifyBooleanSourceDragEnded(PrimitiveItem source)
    {
        if (!IsSourceAdjustMode || _sourceAdjustTarget == Guid.Empty) return;
        var item = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
        if (item is not { TypeKey: nameof(L1Primitive.BooleanPath) }) return;
        if (!item.BooleanSourceIds.Contains(source.Id)) return;
        // 拖动已结束 → 走正式合并（提交撤销 + 收起源）
        if (TryRecomputeBooleanCore(item, commitUndo: true, collapseSources: true) is null) return;
        CollapseSourceAdjust(item);
        SetBooleanSourcesVisible(item, false);
        OnPropertyChanged(nameof(SelectedBooleanSkeletonInfo));
        RecomputeBooleanCommand.NotifyCanExecuteChanged();
        ToggleSourceAdjustCommand.NotifyCanExecuteChanged();
    }
}