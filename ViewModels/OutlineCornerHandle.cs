using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>轮廓接缝手柄模型：对应 ThickPolyline 的一个内部接缝（vertex 1..N-2）。
/// 与 VertexHandle 的区别：
///   - VertexHandle 在骨架顶点（PathData 控制点）上 → 编辑骨架
///   - OutlineCornerHandle 在轮廓角点（miter 顶点 / 凹口中点 / 弧线中点）上 → 编辑轮廓
/// X/Y 是画布坐标（角点 = item 偏移 + BuildOutlineDetail 输出的 Position）。
/// 仅在 OutlineEditMode.Outline 下显示。点击 → vm.SelectedOutlineCorner = this。</summary>
public partial class OutlineCornerHandle : ObservableObject
{
    /// <summary>接缝在 PathData 中的顶点序号（1..N-2）。</summary>
    public int Index { get; }

    /// <summary>轮廓角点的画布 X 坐标（含 item.X 偏移）。</summary>
    [ObservableProperty] private double _x;

    /// <summary>轮廓角点的画布 Y 坐标（含 item.Y 偏移）。</summary>
    [ObservableProperty] private double _y;

    /// <summary>侧边标识："L"=左凸侧,"R"=右凸侧,"Round"=弧线中点。
    /// 用于 UI 显示，区分每个接缝的两个手柄。</summary>
    public string Side { get; }

    /// <summary>该接缝的当前样式（per-vertex override；null = 继承全局 OutlineJoinStyle）。</summary>
    [ObservableProperty] private JoinStyle? _currentStyle;

    /// <summary>接缝的有效样式（非 nullable）：per-vertex 优先，缺则回退到 OutlineJoinStyle。
    /// XAML ComboBox items 是非 nullable JoinStyle（避免"null 在 items 里匹配不到"导致 SelectedItem 空白）。</summary>
    [ObservableProperty] private JoinStyle _currentStyleEffective;

    /// <summary>ComboBox TwoWay 写 CurrentStyleEffective 时触发：把选择转发给 VM 的 _setCurrentStyle，
    /// 真正写入 per-vertex style 并刷新几何。没有这个钩子的话，切 Miter/Round 只改 UI 显示、模型不变，
    /// Builder 仍按旧样式渲染——之前"切 Miter 圆角还在"的根因。</summary>
    partial void OnCurrentStyleEffectiveChanged(JoinStyle value)
        => _setCurrentStyle?.Invoke(this, value);

    /// <summary>VM 写入接缝样式的入口：写 per-vertex override（如果 value 与全局相等就清除 per-vertex，保持一致）。
    /// 同时刷新 OutlineGeometry 让画布立刻看到效果。</summary>
    private readonly Action<OutlineCornerHandle, JoinStyle>? _setCurrentStyle;
    public void SetCurrentStyle(JoinStyle style) => _setCurrentStyle?.Invoke(this, style);

    /// <summary>是否被选中（高亮状态，由 vm.SelectedOutlineCorner == this 同步）。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>该手柄在 emit 步骤序列中的索引（指向 CachedOutlineSteps[StepIndex]）。
    /// 由 RefreshOutlineCornerHandles 在 BuildOutlineSteps 时填充，用于点击/属性面板
    /// 精准定位到对应步骤进行替换（圆角化或恢复）。</summary>
    public int StepIndex { get; }

    /// <summary>轮廓点种类（true = 骨线转折产生的转角点，false = 普通轮廓点）。
    /// 由 RefreshOutlineCornerHandles 从 step.Kind 透传，"应用到全部接缝"等批量操作据此跳过转角点。</summary>
    public bool IsCorner { get; }

    /// <summary>步骤类型："PointStep" / "BevelStep" / "ArcStep"。
    /// 用于判断能否替换为圆角（ArcStep/PointStep 可圆角化，BevelStep 是拆点不圆角化）。
    /// 可变：圆角化时会被 ApplyCornerArcToSelected / RestoreCornerFromArc 写回最新状态。</summary>
    [ObservableProperty] private string _stepKind = "";

    /// <summary>沿 emit 顺序"进入"该角的单位向量（上一条边方向）。
    /// 由 RefreshOutlineCornerHandles 根据 emit 序列前后步骤算出，圆角化时直接使用，
    /// 不依赖 vi/Side 区分——所有轮廓角共用同一逻辑。</summary>
    public Vector InDir { get; }
    /// <summary>沿 emit 顺序"离开"该角的单位向量（下一条边方向）。</summary>
    public Vector OutDir { get; }

    /// <summary>当前接缝的"圆角半径"读取委托：由 ViewModel 创建手柄时注入，
    /// 实现分源读取（转角点→JoinInnerRadius；非转角点→Left/RightCornerRadius）。
    /// XAML TwoWay 写时通过 SetRadius 走 _setRadius 委托写回对应字段并触发 OutlineGeometry 刷新。</summary>
    private readonly Func<OutlineCornerHandle, double> _getRadius;
    private readonly Action<OutlineCornerHandle, double>? _setRadius;

    /// <summary>接缝"圆角半径"派生属性（不持久化，每次读时调 _getRadius）。
    /// 读源：转角点 → VertexThickness[Index].JoinInnerRadius；
    ///       非转角点 → VertexThickness[Index].LeftCornerRadius (Side="L") / RightCornerRadius (Side="R")。
    /// 通过 OnPropertyChanged(nameof(CurrentRadius)) 触发 UI 同步（在 VertexThickness 联动回调里）。</summary>
    public double CurrentRadius => _getRadius?.Invoke(this) ?? 0;

    /// <summary>写"圆角半径"：调注入的 _setRadius，让 VM 把对应字段写到 VertexThickness 并刷新 OutlineGeometry。</summary>
    public void SetRadius(double radius) => _setRadius?.Invoke(this, radius);

    /// <summary>通知 CurrentRadius 已变化（VM 在 VertexThickness 联动 / 自身写入后调用，
    /// 因为 ObservableObject.OnPropertyChanged 是 protected，VM 无法直接代发）。
    /// XAML OneWay 绑定 {CurrentRadius} 会重新读 getter。</summary>
    public void NotifyRadiusChanged() => OnPropertyChanged(nameof(CurrentRadius));

    public OutlineCornerHandle(int index, double x, double y, string side, JoinStyle? currentStyle,
        JoinStyle currentStyleEffective,
        int stepIndex = -1, string stepKind = "", Vector inDir = default, Vector outDir = default,
        bool isCorner = false,
        Func<OutlineCornerHandle, double>? getRadius = null,
        Action<OutlineCornerHandle, double>? setRadius = null,
        Action<OutlineCornerHandle, JoinStyle>? setCurrentStyle = null)
    {
        Index = index;
        _x = x;
        _y = y;
        Side = side;
        _currentStyle = currentStyle;
        _currentStyleEffective = currentStyleEffective;
        StepIndex = stepIndex;
        _stepKind = stepKind;
        InDir = inDir;
        OutDir = outDir;
        IsCorner = isCorner;
        _getRadius = getRadius ?? (_ => 0);
        _setRadius = setRadius;
        _setCurrentStyle = setCurrentStyle;
    }
}