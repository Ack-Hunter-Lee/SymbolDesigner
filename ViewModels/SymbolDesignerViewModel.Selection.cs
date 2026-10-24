using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Geometry;
using SymbolDesigner.Models;
using Point = System.Windows.Point;

namespace SymbolDesigner.ViewModels;

/// <summary>多选集合 + "相切/重合"双图元约束命令（Ctrl+点击累积、拾取子对象标记、CanApply* 联动）。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>多选集合（Ctrl+点击加入/移出；"相切"等双图元操作取前两项）。
    /// 不替代 SelectedItem（单选语义保留），只在需要多图元的约束操作时使用。</summary>
    public ObservableCollection<PrimitiveItem> MultiSelected { get; } = new();

    /// <summary>多选时记录每个图元上的点击画布逻辑坐标（重合约束子对象识别：端点/圆心/圆弧/线段）。
    /// 以图元 Id 为键，随 AddToMultiSelect 更新、随移除/清空清理。</summary>
    private readonly Dictionary<Guid, Point> _pickCanvasByItem = new();

    /// <summary>多选时按点击位置识别出的子对象类别（重合约束直接据此判模式）。</summary>
    private readonly Dictionary<Guid, PickKind> _pickKindByItem = new();

    /// <summary>多选拾取标记集合（画布覆盖层高亮被识别的圆心/端点子对象，供用户确认）。</summary>
    public ObservableCollection<PickMarker> MultiPickMarkers { get; } = new();

    /// <summary>能否执行"相切"约束：多选集合中至少含一条直线 + 一个圆。
    /// 放宽为"至少"而非"恰好2项"，配合累积式多选（点击即加入），
    /// 避免"再选一个其它图元按钮就变灰"的困惑。</summary>
    public bool CanApplyTangent =>
        MultiSelected.Any(GeometryMath.IsLineLike) && MultiSelected.Any(GeometryMath.IsCircleLike);

    /// <summary>能否执行"重合"约束：与相切相同的多选前提（直线 + 圆）。
    /// 智能识别模式（端点落圆弧 / 圆心落直线）在 ApplyCoincident 内根据几何判定。</summary>
    public bool CanApplyCoincident =>
        MultiSelected.Any(GeometryMath.IsLineLike) && MultiSelected.Any(GeometryMath.IsCircleLike);

    /// <summary>Ctrl+点击：切换某图元是否在多选集合（并在视觉上高亮）。
    /// canvasPick 为用户点在该图元上的画布逻辑坐标（用于重合约束的子对象识别）。</summary>
    public void ToggleMultiSelect(PrimitiveItem item, Point canvasPick)
    {
        if (MultiSelected.Contains(item))
        {
            MultiSelected.Remove(item);
            _pickCanvasByItem.Remove(item.Id);
            _pickKindByItem.Remove(item.Id);
            item.IsSelected = SelectedItem == item;
        }
        else
        {
            MultiSelected.Add(item);
            _pickCanvasByItem[item.Id] = canvasPick;
            _pickKindByItem[item.Id] = GeometryMath.ClassifyPick(item, canvasPick, out _);
            item.IsSelected = true;
        }
        RaiseMultiSelectChanged();
    }

    /// <summary>普通点击：累积式加入多选集合（CAD 风格连续点选），同时更新单选焦点。
    /// 与 ToggleMultiSelect 的区别：不提供"再次点击移出"，避免误操作；点其它图元即整齐累积。
    /// canvasPick 为用户点在该图元上的画布逻辑坐标（用于重合约束的子对象识别）。</summary>
    public void AddToMultiSelect(PrimitiveItem item, Point canvasPick)
    {
        if (!MultiSelected.Contains(item))
        {
            MultiSelected.Add(item);
            _pickCanvasByItem[item.Id] = canvasPick;
            _pickKindByItem[item.Id] = GeometryMath.ClassifyPick(item, canvasPick, out _);
            item.IsSelected = true;
        }
        SelectedItem = item;
        RaiseMultiSelectChanged();
    }

    /// <summary>多选集合变化 → 刷新 CanApplyTangent 相关的绑定与命令可执行态。</summary>
    private void RaiseMultiSelectChanged()
    {
        RebuildPickMarkers();
        OnPropertyChanged(nameof(CanApplyTangent));
        ApplyTangentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplyCoincident));
        ApplyCoincidentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanBooleanOp));
        MergeShapesCommand.NotifyCanExecuteChanged();
        SubtractShapesCommand.NotifyCanExecuteChanged();
        IntersectShapesCommand.NotifyCanExecuteChanged();
        NotifyBooleanCanExecuteChanged(); // 调整源/重新合并按钮随多选态刷新
        RefreshScaleSelectionHandles();
    }

    /// <summary>清空多选集合并复位选中高亮（切换到单选时调用）。</summary>
    private void ClearMultiSelect()
    {
        foreach (var p in MultiSelected)
            p.IsSelected = p == SelectedItem;
        MultiSelected.Clear();
        _pickCanvasByItem.Clear();
        _pickKindByItem.Clear();
        RebuildPickMarkers();
        OnPropertyChanged(nameof(CanApplyTangent));
        ApplyTangentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplyCoincident));
        ApplyCoincidentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanBooleanOp));
        MergeShapesCommand.NotifyCanExecuteChanged();
        SubtractShapesCommand.NotifyCanExecuteChanged();
        IntersectShapesCommand.NotifyCanExecuteChanged();
        NotifyBooleanCanExecuteChanged(); // 调整源/重新合并按钮随多选态刷新
        RefreshScaleSelectionHandles();
    }

    /// <summary>把"被识别的子对象命中点"同步到画布覆盖层标记（圆心=橙，端点=绿）。
    /// 无命中（圆弧/线段/普通图形）不显示标记 → 让用户直观看出圆心/端点 vs 圆弧/线段的区别。</summary>
    private void RebuildPickMarkers()
    {
        MultiPickMarkers.Clear();
        foreach (var item in MultiSelected)
        {
            if (!_pickKindByItem.TryGetValue(item.Id, out var kind))
                continue;
            if (kind == PickKind.Center)
            {
                MultiPickMarkers.Add(new PickMarker { X = item.X + item.Width / 2.0, Y = item.Y + item.Height / 2.0, IsCircleCenter = true });
            }
            else if (kind == PickKind.EndPoint0 || kind == PickKind.EndPoint1)
            {
                if (TangentSolver.TryGetLineEndpoint(item, kind == PickKind.EndPoint0 ? 0 : 1, out var ep))
                    MultiPickMarkers.Add(new PickMarker { X = ep.X, Y = ep.Y });
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyTangent))]
    private void ApplyTangent()
    {
        var line = MultiSelected.FirstOrDefault(GeometryMath.IsLineLike);
        var circle = MultiSelected.FirstOrDefault(GeometryMath.IsCircleLike);
        if (line is null || circle is null) return;

        // AutoCAD GCTANGENT 规则：先选 = 锚（不动），后选 = 调整对象（移动去凑相切）。
        // MultiSelected 按点击顺序累积（AddToMultiSelect Append）→ 索引更小的先选。
        bool anchorIsLine = MultiSelected.IndexOf(line) < MultiSelected.IndexOf(circle);

        ApplyTangent(line, circle, anchorIsLine);
        ClearMultiSelect();
        SelectedItem = anchorIsLine ? line : circle;
    }

    [RelayCommand(CanExecute = nameof(CanApplyCoincident))]
    private void ApplyCoincident()
    {
        var line = MultiSelected.FirstOrDefault(GeometryMath.IsLineLike);
        var circle = MultiSelected.FirstOrDefault(GeometryMath.IsCircleLike);
        if (line is null || circle is null) return;

        ApplyCoincident(line, circle);
        ClearMultiSelect();
        SelectedItem = line;
    }
}