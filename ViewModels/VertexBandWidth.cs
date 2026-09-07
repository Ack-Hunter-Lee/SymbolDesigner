using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>顶点带宽模型：对应 BandPolyline 的一个顶点。
/// 带宽绑定到顶点（连接点）而非段，符合"连接点承上启下"的语义。
/// - 首顶点（IsStart=true）：只有 OutgoingBandWidth（启下=段 0 首端带宽）
/// - 末顶点（IsEnd=true）：只有 IncomingBandWidth（承上=段 N-1 末端带宽）
/// - 中间顶点：IncomingBandWidth（承上=前段末端）+ OutgoingBandWidth（启下=后段首端）
/// 段 i 的首端宽度 = VertexBandWidths[i].OutgoingBandWidth
/// 段 i 的末端宽度 = VertexBandWidths[i+1].IncomingBandWidth
/// 中间线性渐变 → 段为梯形。承上≠启下时顶点处形成台阶。</summary>
public partial class VertexBandWidth : ObservableObject
{
    /// <summary>顶点序号（0=首，N-1=末）。</summary>
    public int Index { get; }

    /// <summary>承上：前一段末端带宽（首顶点无此项，不参与渲染）。</summary>
    [ObservableProperty] private double _incomingBandWidth;

    /// <summary>启下：后一段首端带宽（末顶点无此项，不参与渲染）。</summary>
    [ObservableProperty] private double _outgoingBandWidth;

    /// <summary>Round join 内径：JoinStyle == Round 时生效。
    /// 0 = 经典 stroke-linejoin: round（外径圆弧 + 内尖 vertex 单点）；
    /// &gt;0 = 在 vertex 凹方向额外画一段内径圆弧（半径 = JoinInnerRadius，圆心 = vertex），
    /// 同时外径半径 = halfBandWidth + JoinInnerRadius（"外径 = 带宽 + 内径"关系）。
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

    /// <summary>是否为首顶点（顶点 0）。首顶点只有 OutgoingBandWidth 生效。</summary>
    public bool IsStart { get; }

    /// <summary>是否为末顶点（顶点 N-1）。末顶点只有 IncomingBandWidth 生效。</summary>
    public bool IsEnd { get; }

    /// <summary>是否为中间顶点（既非首也非末）。中间顶点 Incoming + Outgoing 都生效。</summary>
    public bool IsMiddle => !IsStart && !IsEnd;

    /// <summary>是否显示"入端"带宽（属性面板 Slider + 画布 ▲ 三角形）。
    /// 首顶点恒 false；合并模式（CollinearSplitPairs=false）下共线同向直通接缝顶点也 false
    /// （该顶点合并后只有出端生效）。由 ViewModel 刷新 BandWidthHandles 时同步设置。</summary>
    [ObservableProperty] private bool _showIncoming = true;

    /// <summary>是否显示"出端"带宽（属性面板 Slider + 画布 ▼ 三角形）。末顶点恒 false。
    /// 由 ViewModel 刷新 BandWidthHandles 时同步设置。</summary>
    [ObservableProperty] private bool _showOutgoing = true;

    public VertexBandWidth(int index, double incoming, double outgoing, bool isStart, bool isEnd)
    {
        Index = index;
        _incomingBandWidth = incoming;
        _outgoingBandWidth = outgoing;
        IsStart = isStart;
        IsEnd = isEnd;
        _joinInnerRadius = 0;
    }

    partial void OnJoinInnerRadiusChanged(double value)
        => BandWidthChanged?.Invoke(this, EventArgs.Empty);

    partial void OnLeftCornerRadiusChanged(double value)
        => BandWidthChanged?.Invoke(this, EventArgs.Empty);

    partial void OnRightCornerRadiusChanged(double value)
        => BandWidthChanged?.Invoke(this, EventArgs.Empty);

    partial void OnIncomingBandWidthChanged(double value)
        => BandWidthChanged?.Invoke(this, EventArgs.Empty);

    partial void OnOutgoingBandWidthChanged(double value)
        => BandWidthChanged?.Invoke(this, EventArgs.Empty);

    partial void OnJoinStyleChanged(JoinStyle value)
        => BandWidthChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Incoming / Outgoing / JoinInnerRadius 任一变化触发。</summary>
    public event EventHandler? BandWidthChanged;
}