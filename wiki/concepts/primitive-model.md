---
title: 图元数据模型（PrimitiveItem 与 TypeKey 体系）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [wpf, mvvm, architecture]
prerequisites: [symboldesigner-project]
confidence: high
---

# 图元数据模型（PrimitiveItem 与 TypeKey 体系）

画布上的每个图形实例是 `Models/PrimitiveItem`（`ObservableObject`）。它**同时**承担：
渲染输入（计算属性产出 WPF Geometry）、编辑对象（手柄/缩放）、持久化对象（DTO 往返）、导出对象（XAML）。

## TypeKey：一维字符串身份

`PrimitiveItem.TypeKey` 是字符串（`"Rectangle"`/`"Ellipse"`/`"Line"`/`"BandPolyline"`/`"Belt"`...），
一切分支（`PrimitiveTemplateSelector`、`XamlExporter`、`SelectionGeometry`、`CanvasFillGeometry`、`IsFillable`）都按它 switch。
`L1Primitive` 枚举只用于"新建图元"的默认值（`PrimitiveItem.Create(L1Primitive...)`），不参与运行时分派。

> **工具 Key 与 TypeKey 同源**：工具栏工具标识就是 `"L1_" + TypeKey`（如工具 `"L1_Belt"` → 图元 `"Belt"`，
> 见 `SymbolDesignerViewModel.Tools.cs`）。两者不是两套分类，而是同一个身份分别挂在
> "工具条目"（`ToolboxEntry.Key`，创建入口）和"图元实例"（`PrimitiveItem.TypeKey`，产物身份）上。
> 工具 Key 额外映射 `DrawMode`（决定绘制交互流程），TypeKey 决定渲染/导出/命中。
> 唯一需要区分二者的场景：画线工具 Polyline/BandPolyline 共用同一绘制状态机，提交时按 `DrawMode` 决定产出哪种 TypeKey。

## 几何表示：局部坐标 + 包围盒

- `X/Y/Width/Height` 是画布坐标，也是图元包围盒。
- `PathData` 是**局部坐标**的 XAML mini-language（`"M x,y L x,y ..."`），原点 = 包围盒左上角；模板用 `Canvas.Left/Top` 定位。
- 几何计算属性（`OutlineGeometry`/`PolylineGeometry`/`RingGeometry`/`PieGeometry`/`BeltGeometry`/`SelectionGeometry`）
  都是 getter + 输入缓存（`_cached*`），只由输入字段变化触发重算。
- `CanvasFillGeometry` 返回画布坐标的闭合几何，是布尔运算的输入（有面积的图元才返回非 null）。

## 关键语义细节

- **带宽绑定顶点**：`VertexBandWidths` 每顶点有 `IncomingBandWidth`（承上）/`OutgoingBandWidth`（启下），
  段 i 首端宽 = 顶点 i 的 Outgoing，末端宽 = 顶点 i+1 的 Incoming；首末不同 → 段为梯形。
- **闭合语义分两套**：`IsClosed`（用户设置的闭合标志，只对 Polyline 有效，不写 `Z`）vs
  `BandOutlineBuilder.IsPathDataClosed`（PathData 末尾是否真有 `Z`）。缩放/镜像等必须查几何事实。
- **外观**：`StrokeColor/Width/DashStyle`、`FillStyle`（None/Solid/Gradient/MonochromeGradient/Pattern/ImageTexture）、
  亮度/透明度合成（`FillBrightness`/`FillOpacity`）在 `FillBrush` getter 里经 `ColorUtil` 合成。
- **编辑状态**：`IsVisible`（隐藏仍保留在集合）、`IsLocked`（禁止一切移动/编辑，但可选中）。

## DTO 与序列化

- `PrimitiveItemDto` 只保存编辑数据（不含 `IsSelected`、计算几何）。字段名与模型**不完全一致**：
  `Thickness`（= BandWidth）、`VertexThicknesses`（= VertexBandWidths）、`JoinStyle`（旧名，加载时替换为 `OutlineJoinStyle`）——
  这是刻意的序列化兼容，见 [[notes/legacy-thickness-handle]]。
- `CreateFromDto` 有已知坑：**必须先赋整体 `BandWidth` 再填 `VertexBandWidths`**，
  否则 `OnBandWidthChanged` 会覆盖逐顶点带宽（曾导致加载后带宽被抹平）。
- 图元 `Id`（Guid）是约束/阵列/撤销引用的锚；复制粘贴时 `ToDto(CreateFromDto(dto))` 往返 + **置空 Id** 保证新身份。

## 相关页面

- [[band-outline-pipeline]] - PathData + 顶点带宽 → 轮廓几何的算法
- [[boolean-ops]] - BooleanPath 的"死形状"与段结构快照
- [[file-formats]] - DTO 的落盘形态
- [[symboldesigner-project]] - 项目实体