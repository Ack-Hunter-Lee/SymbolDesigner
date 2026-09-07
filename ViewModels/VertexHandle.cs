using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>顶点手柄模型：对应 BandPolyline PathData 的一个顶点。
/// X/Y 是画布坐标（含 PrimitiveItem.X/Y 偏移，方便 View 直接用 Canvas.Left/Top 定位）。
/// View 端 TwoWay 绑定 X/Y 实现拖拽跟随；拖拽过程中通过 ViewModel.UpdateVertexPosition 同步 PathData。</summary>
public partial class VertexHandle : ObservableObject
{
    /// <summary>顶点在 PathData 中的序号（0=首端，N-1=末端）。</summary>
    public int Index { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public VertexHandle(int index, double x, double y)
    {
        Index = index;
        _x = x;
        _y = y;
    }
}