---
title: BandPolyline 与 Polyline 对比
created: 2026-11-17
updated: 2026-11-17
type: comparison
tags: [geometry, wpf]
prerequisites: [primitive-model, band-outline-pipeline]
confidence: high
---

# BandPolyline 与 Polyline 对比

两者共用同一份 `PathData`（骨架折线）与同一个画线状态机（点击/移动/Enter 提交），
是"同一份骨架的两种表达"：**一个带带宽轮廓，一个纯描边**。

| 维度 | BandPolyline（带状折线） | Polyline（纯折线） |
|---|---|---|
| 视觉 | 骨架两侧按带宽偏移出闭合轮廓带（CAD stroke-outline） | 仅沿骨架的描边线 |
| 几何 | `OutlineGeometry`（BuildOutlineSteps 管道） | `PolylineGeometry`（StreamGeometry 骨架） |
| 填充 | 闭合轮廓可填充 | 开放时无面积；勾选 `IsClosed` 后变闭合多边形（复用轮廓管道） |
| 参数 | `BandWidth` / `VertexBandWidths`（逐顶点 Incoming/Outgoing） | 无带宽；闭合后用 `VertexBandWidths[].LeftCornerRadius` 做顶点圆角 |
| 接缝 | Miter / Bevel / Round 三种 JoinStyle + per-vertex 覆盖 | 闭合形态复用同一套 emit/圆角管道 |
| 用途 | 设备主体、带厚度语义的管线 | 参考线、工艺走向、分区辅助线 |
| 绘制 | 共用画线状态机，提交时按 `DrawMode` 决定目标类型 | 同上 |

## 关键差异点

- **闭合语义**：BandPolyline 天然闭合轮廓（`EmitSteps` 自动封口）；Polyline 开放时只描边，
  闭合由用户设置 `IsClosed`（提交时按闭环吸附请求自动置位）。
- **路径吸附**：Polyline 支持"点击起点闭环"（`CloseSnapThreshold` 8px）；BandPolyline **不启用**该吸附，
  防止"点击起点得到重合点"。
- **渲染缓存**：两者都有按 PathData 的 getter 缓存；BandPolyline 的缓存还含顶点带宽/接缝样式的 hash 判定。
- **导出**：BandPolyline 与闭合 Polyline 都走 `OutlineGeometry.ToString()` 导出为单 Path；
  开放 Polyline 导出为仅描边的骨架 Path。

## 如何选择

- 需要"带宽/粗细"语义、可编辑轮廓 → **BandPolyline**（[[band-outline-pipeline]]）。
- 只是参考线、不需要宽度 → **Polyline**。

## 相关页面

- [[band-outline-pipeline]] - BandPolyline 的几何算法
- [[primitive-model]] - 两者的模型字段
- [[draw-state-machine]] - 共用的绘制状态机