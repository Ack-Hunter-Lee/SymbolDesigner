# 重命名考古记录：ThicknessHandle → BandWidthHandle

## 概述

本文件是 2026-09-07 完成的"ThickPolyline → BandPolyline"整体重命名工程中，旧类 `ThicknessHandle` 的最终形态归档。

工程初衷：解决 `Thickness`（骨点带宽）与 `StrokeThickness`（轮廓线描边粗细）的命名混淆，建立清晰的"带宽（band width）"概念体系。

## 重命名范围（节选）

| 旧名 | 新名 | 范围 |
|------|------|------|
| `ThickPolyline` | `BandPolyline` | 类型、TypeKey、工具栏键、XAML 模板键 |
| `ThickOutlineBuilder` | `BandOutlineBuilder` | 几何管道静态类 |
| `Thickness` | `BandWidth` | `PrimitiveItem` 主属性 |
| `VertexThickness` / `VertexThicknesses` | `VertexBandWidth` / `VertexBandWidths` | 逐顶点带宽模型及集合 |
| `ThicknessHandle` | `BandWidthHandle` | 带宽模式画布端点手柄 |
| `IsThicknessEditMode` | `IsBandWidthEditMode` | VM 编辑模式标志 |
| `OutlineEditMode.Thickness` | `OutlineEditMode.BandWidth` | 枚举值 |
| UI 文案"厚度线" | "带宽线" | 工具栏 / 属性面板按钮 |
| UI 文案"粗折线" | "带宽折线" | 工具栏显示名 |

## 故意保留旧名的位置（DTO 序列化兼容）

`Models/SymbolProject.cs` 中的 `PrimitiveItemDto.Thickness`、`VertexThicknessDto.IncomingThickness/OutgoingThickness` **保留旧名**，因为它们是 `.sym.json` 持久化字段名。新 VM 写入时映射 `BandWidth → Thickness`，加载时反之。TypeKey 也加入兼容映射 `ThickPolyline → BandPolyline`，保证旧工程文件无损读取。

## 归档：原 ThicknessHandle.cs 全文

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.Views;

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
```

## 新等价类 BandWidthHandle.cs 关键差异

仅 4 处文本替换：`厚度线`→`带宽线`、`ThicknessHandle`→`BandWidthHandle`、构造器名同步。字段、签名、行为完全一致。