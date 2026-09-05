using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>顶点厚度模型：对应 ThickPolyline 的一个顶点。
/// 厚度绑定到顶点（连接点）而非段，符合"连接点承上启下"的语义。
/// - 首顶点（IsStart=true）：只有 OutgoingThickness（启下=段 0 首端厚度）
/// - 末顶点（IsEnd=true）：只有 IncomingThickness（承上=段 N-1 末端厚度）
/// - 中间顶点：IncomingThickness（承上=前段末端）+ OutgoingThickness（启下=后段首端）
/// 段 i 的首端宽度 = VertexThicknesses[i].OutgoingThickness
/// 段 i 的末端宽度 = VertexThicknesses[i+1].IncomingThickness
/// 中间线性渐变 → 段为梯形。承上≠启下时顶点处形成台阶。</summary>
public partial class VertexThickness : ObservableObject
{
    /// <summary>顶点序号（0=首，N-1=末）。</summary>
    public int Index { get; }

    /// <summary>承上：前一段末端厚度（首顶点无此项，不参与渲染）。</summary>
    [ObservableProperty] private double _incomingThickness;

    /// <summary>启下：后一段首端厚度（末顶点无此项，不参与渲染）。</summary>
    [ObservableProperty] private double _outgoingThickness;

    /// <summary>Round join 内径：JoinStyle == Round 时生效。
    /// 0 = 经典 stroke-linejoin: round（外径圆弧 + 内尖 vertex 单点）；
    /// &gt;0 = 在 vertex 凹方向额外画一段内径圆弧（半径 = JoinInnerRadius，圆心 = vertex），
    /// 同时外径半径 = halfThickness + JoinInnerRadius（"外径 = 厚度 + 内径"关系）。
    /// 末顶点 = 0（不渲染）。</summary>
    [ObservableProperty] private double _joinInnerRadius;

    /// <summary>左侧普通轮廓点圆角半径（emit 序列 lefts[i] ↔ 骨架顶点 i 的 Left 侧）。
    /// 普通轮廓点（端点偏移 / 段末端 / 共线直通点）的圆角化使用；0 = 无圆角。
    /// Corner 转角点圆角不走这里：用 JoinInnerRadius + per-vertex Round。
    /// 持久化字段：保存到 .sym.json，渲染时由 BuildOutlineSteps 重建 ArcStep。</summary>
    [ObservableProperty] private double _leftCornerRadius;

    /// <summary>右侧普通轮廓点圆角半径（emit 序列 rights[i] ↔ 骨架顶点 i 的 Right 侧；0 = 无圆角）。</summary>
    [ObservableProperty] private double _rightCornerRadius;

    /// <summary>该顶点的接缝样式（per-vertex）。首末顶点不参与接缝计算但保留字段以对齐。
    /// 默认 Miter，与老版本全局默认行为一致。
    /// 当 PrimitiveItem.OutlineJoinStyle 改变时，此字段不会被自动覆盖（用户逐顶点设置优先）。</summary>
    [ObservableProperty] private JoinStyle _joinStyle = JoinStyle.Miter;

    /// <summary>是否为首顶点（顶点 0）。首顶点只有 OutgoingThickness 生效。</summary>
    public bool IsStart { get; }

    /// <summary>是否为末顶点（顶点 N-1）。末顶点只有 IncomingThickness 生效。</summary>
    public bool IsEnd { get; }

    /// <summary>是否为中间顶点（既非首也非末）。中间顶点 Incoming + Outgoing 都生效。</summary>
    public bool IsMiddle => !IsStart && !IsEnd;

    /// <summary>是否显示"入端"厚度（属性面板 Slider + 画布 ▲ 三角形）。
    /// 首顶点恒 false；合并模式（CollinearSplitPairs=false）下共线同向直通接缝顶点也 false
    /// （该顶点合并后只有出端生效）。由 ViewModel 刷新 ThicknessHandles 时同步设置。</summary>
    [ObservableProperty] private bool _showIncoming = true;

    /// <summary>是否显示"出端"厚度（属性面板 Slider + 画布 ▼ 三角形）。末顶点恒 false。
    /// 由 ViewModel 刷新 ThicknessHandles 时同步设置。</summary>
    [ObservableProperty] private bool _showOutgoing = true;

    public VertexThickness(int index, double incoming, double outgoing, bool isStart, bool isEnd)
    {
        Index = index;
        _incomingThickness = incoming;
        _outgoingThickness = outgoing;
        IsStart = isStart;
        IsEnd = isEnd;
        _joinInnerRadius = 0;
    }

    partial void OnJoinInnerRadiusChanged(double value)
        => ThicknessChanged?.Invoke(this, EventArgs.Empty);

    partial void OnLeftCornerRadiusChanged(double value)
        => ThicknessChanged?.Invoke(this, EventArgs.Empty);

    partial void OnRightCornerRadiusChanged(double value)
        => ThicknessChanged?.Invoke(this, EventArgs.Empty);

    partial void OnIncomingThicknessChanged(double value)
        => ThicknessChanged?.Invoke(this, EventArgs.Empty);

    partial void OnOutgoingThicknessChanged(double value)
        => ThicknessChanged?.Invoke(this, EventArgs.Empty);

    partial void OnJoinStyleChanged(JoinStyle value)
        => ThicknessChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Incoming / Outgoing / JoinInnerRadius 任一变化触发。</summary>
    public event EventHandler? ThicknessChanged;
}