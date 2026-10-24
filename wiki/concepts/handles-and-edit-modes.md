---
title: 手柄体系与编辑模式
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [interaction, wpf, mvvm]
prerequisites: [primitive-model, band-outline-pipeline]
confidence: high
---

# 手柄体系与编辑模式

选中图元后，VM 按图元类型与编辑模式生成**手柄集合**（`ObservableCollection<...>`），画布叠加显示；拖动手柄回写模型。

## 编辑模式（OutlineEditMode）

- `Skeleton`（骨架，默认）/ `BandWidth`（带宽线）/ `Outline`（轮廓接缝）：切换不丢 `SelectedItem`，只换手柄集合 + 属性面板。
- Tab 轮转；闭合 Polyline / BooleanPath 无带宽概念，Skeleton ↔ Outline 直达。
- **BooleanPath 选中自动切到 Outline**（骨架点不可拖）。
- 选中 BandPolyline/Polyline 自动联动进入对应 DrawMode（光标变十字）；`_userChangedDrawMode` 尊重手动选择。

## 手柄一览

| 手柄 | 适用 | 拖动回写要点 |
|---|---|---|
| `VertexHandle` | 有 PathData 的图元（BooleanPath 不拖） | 局部坐标重写 PathData；`_suppressVertexRefresh` 防循环；随后 `RecalcBoundingBox` 重算 X/Y/W/H；触发约束求解 |
| `SegmentDragHandle` | BandPolyline | 每段中点，两端同步平移保段长 |
| `BandWidthHandle` | BandPolyline（BandWidth 模式） | 每段 L/R × Start/End 4 个；入端在共线合并顶点隐藏 |
| `OutlineCornerHandle` | BandPolyline / 闭合 Polyline / BooleanPath（Outline 模式） | 复用 `CachedOutlineSteps`；圆角 = `TryReplaceWithArc` 把步骤替换为 ArcStep；Corner 点走 JoinInnerRadius + Round join |
| `RingHandle` | Ring | 中心/外圆东/内圆东 3 个；内径 Clamp 到外径内 |
| `PieHandle` | Pie | 中心/半径鼻尖/起止弧端点；Start/End 固定对称中轴 |
| `BeltHandle` | Belt | 2 个自由端点；写回 + `SolveBelt(autoEngage:true)`；闭合态两端同步 = 整体平移 |

所有手柄入口第一道检查 `item.IsLocked` 直接 return。

## 关键机制

- **轮廓手柄依赖渲染缓存**：`RefreshOutlineCornerHandles` 必须**先强制读一次 `item.OutlineGeometry`** 让 getter 重算缓存；
  `StepIndex` 严格 = 缓存索引；手柄携带 `BoneIndex` 映射骨架顶点（旧公式在拆双对模式下越界）。
- 手柄把 `CachedOutlineSteps` 的某项就地替换为 `ArcStep` 后调 `NotifyOutlineGeometryChanged()` → 只 re-emit 不重算。
- 圆角几何：切点偏移 = 半径 × cot(半跨角)，sweep 由 `det(inDir,outDir)` 决定。
- 选中装饰几何 `SelectionGeometry`：按 TypeKey 复用本体几何 → 选中虚线永远贴形。

## 相关页面

- [[band-outline-pipeline]] - 手柄改写的步骤管道
- [[draw-state-machine]] - 绘制产物进入编辑
- [[constraint-system]] - 子对象拾取