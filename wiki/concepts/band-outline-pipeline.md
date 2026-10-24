---
title: 带宽轮廓几何管道（BandOutline）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [geometry, wpf]
prerequisites: [primitive-model]
confidence: high
---

# 带宽轮廓几何管道（BandOutline）

`Geometry/BandOutlineBuilder.cs`（命名空间 `SymbolDesigner.Converters`）把"骨架折线 + 逐顶点带宽 + 接缝样式"
实时合成一条**闭合轮廓带**（CAD stroke-outline 效果），是 BandPolyline / 闭合 Polyline / BooleanPath 的渲染核心。

## 算法流程（BuildOutlineSteps）

1. `ParsePathData` 解析骨架顶点。
2. `ComputeSegment` 逐段计算法线偏移端点：`perp = dir 旋转 90° × 半带宽`；段首端宽 = 前顶点 Outgoing，末端宽 = 后顶点 Incoming。
3. `ComputeMiterOutline` 先只算 **Miter 基线**（lefts/rights 两条独立路径，2N-1 个点）。
   共线同向 → 按 `CollinearSplitPairs` 决定保留两对轮廓点（带宽台阶）还是合并一对；
   共线反向（180°）→ 翻转 `verse` 标志，左右交换。
4. **接缝扰动**：只遍历 emit 步骤中 `Kind == Corner` 的转角点（用 `JoinIndex` 定位段上下文，避免旧版索引在共线分支错位）。
   - Miter：不动；
   - Bevel：仅**外凸侧**替换为 `BevelStep`（斜切封口），内侧保持尖角；
   - Round：外凸侧画外圆弧、内凸侧画内圆弧（圆心 = miter 顶点，默认内径 = 半带宽，可用 `JoinInnerRadius` 覆盖）。
5. 反转 rights → 合并 lefts + rights 为单条 emit 序列（**不含封口步骤**，闭合由 `EmitSteps` 的 `BeginFigure(isClosed:true)` 自动画线）。
6. `MergeCoincidentSteps`（eps=1e-9）：塌缩带宽归零产生的相邻重合点（否则圆角切线/半径退化、手柄视觉重叠）。
7. `ApplyPerVertexCornerRadius`：用步骤自带 `BoneIndex` 定位骨架顶点，Normal 点按左右侧读 `Left/RightCornerRadius` 替换为 `ArcStep`。

## PointStep 抽象（OutlineSteps.cs）

一个 emit 步骤 = 一个手柄/顶点：
- `PointStep`：普通点，`Emit` 画直线；
- `BevelStep`：连画两条线（台阶点 → miter 顶点）；
- `ArcStep`：从 `ArcStart` 弧到 `ArcEnd`（圆心 = 基类 Point）。
每个步骤带 `Kind`（Normal/Corner）、`JoinIndex`（接缝索引）、`BoneIndex`（骨架顶点）、`FigureIndex`（环归属）。

## EmitSteps：拼 StreamGeometry

- 按 `FigureIndex` 分组开 figure（多环布尔结果每环一个闭合 figure，不串环连线）。
- **FillRule = EvenOdd**：凹折线左右轮廓自相交时，EvenOdd 保证内外环正确填充；若用 Nonzero 会被填成实心。

## 圆角几何

- 切点偏移 = `r · cot(半跨角)`，`GetArcTangentOffset` 与 `MiterPoint` 共用半角公式 `sin(θ/2)=√((1+in·out)/2)`。
- 普通轮廓点圆角 `MakeNormalArcStep`：取前后邻点方向，`Vector.Determinant` 判 sweep，共线退化不圆角。

## 设计要点与坑

- **Corner/Normal 二分**：转角点走骨架接缝系统（JoinStyle/JoinInnerRadius），普通点走 per-vertex 圆角，两套逻辑互不踩。
- **缓存 + 外部可改**：`CachedOutlineSteps` 暴露给轮廓编辑手柄；手柄把步骤就地替换为 ArcStep 后调 `NotifyOutlineGeometryChanged()` 只 re-emit 不重算。
- **数值容差**：`GeometryMath` 集中了 `MergeTol=0.5`（布尔近重合）、`CoordEps=1e-3`（序列化）、`CollinearTol=0.02` 等常量。
- **闭合事实**：`IsPathDataClosed` 检查末尾 `Z`，与用户级 `IsClosed` 不同（见 [[primitive-model]]）。

## 相关页面

- [[primitive-model]] - 输入模型（PathData/VertexBandWidths）
- [[boolean-ops]] - 布尔结果的渲染管道（BakeCornerRadii 不经过步骤管道）
- [[handles-and-edit-modes]] - 轮廓接缝手柄如何就地改写步骤