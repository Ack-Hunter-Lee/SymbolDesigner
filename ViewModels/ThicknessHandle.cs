using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>厚度线端点手柄模型：每条厚度线两个端点，每端一个手柄（阶段 3 显示，拖拽后续阶段实现）。
/// SegmentIndex 标识所属段（0..N-2），Side 区分 L/R 厚度线，Endpoint 区分 Start/End 两端点。
/// VertexIndex 映射到骨点（骨架顶点）序号：Start → 段起点顶点 = SegmentIndex；End → 段终点顶点 = SegmentIndex+1。
/// 点击端点/骨点 → 属性面板对应骨点的 Slider 组高亮。
/// X/Y 是画布坐标（含 SelectedItem.X/Y 偏移），View 端 OneWay 绑定。</summary>
public partial class ThicknessHandle : ObservableObject
{
    /// <summary>所属段序号（0=第一段）。</summary>
    public int SegmentIndex { get; }

    /// <summary>"L"=左侧厚度线，"R"=右侧厚度线。</summary>
    public string Side { get; }

    /// <summary>"Start"=端点 1（段起点侧），"End"=端点 2（段终点侧）。</summary>
    public string Endpoint { get; }

    /// <summary>映射的骨点（骨架顶点）序号：Start → SegmentIndex，End → SegmentIndex+1。</summary>
    public int VertexIndex { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public ThicknessHandle(int segmentIndex, string side, string endpoint, int vertexIndex, double x, double y)
    {
        SegmentIndex = segmentIndex;
        Side = side;
        Endpoint = endpoint;
        VertexIndex = vertexIndex;
        _x = x;
        _y = y;
    }
}