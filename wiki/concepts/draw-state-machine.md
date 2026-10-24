---
title: 绘制状态机（DrawMode）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [interaction, wpf, mvvm]
prerequisites: [primitive-model]
confidence: high
---

# 绘制状态机（DrawMode）

画布绘制分为两类模式：**路径类**（Line/Polyline/BandPolyline/Belt）= 起笔 + 连续加点；
**形状类**（Rectangle/RoundedRectangle/Ellipse/Ring/Triangle/Pie）= 中心点 + 第二点两段式。
状态集中在 `SymbolDesignerViewModel.Draw.cs` / `DrawState.cs`。

## 形状类（两段式）

- 第一次点击 = 中心点（`IsDrawing=true`，`UpdateShapePreview()` 实时生成轮廓预览）；
- 第二次点击 = 对角点/半径点，`EndDraw()` 提交并退出。
- 语义差异：
  - Rectangle：第一点=角点、第二点=对角点，W/H = 2|dx|, 2|dy|；
  - Ellipse/Ring/Pie：第二点=半径点，`r = sqrt(dx²+dy²)` 恒为正圆；
  - **关键坑**：圆形类鼠标位置必须直接作圆周点，不能走 `ConstrainPoint` —— 否则 AxisSnap 投影到轴线会把半径算成 |dx|/|dy|。
- Shift 锁正形仅对非圆形生效（`ConstrainShapeToSquare`）；Pie 第二点方向 = 对称中轴（默认张角 60°），`SnapPieAxis` 吸附 45°/30° 倍数。

## 路径类（多点式）

- 起笔后连续追加顶点；`MoveDraw` 更新橡皮线；`UpdateDraftStartSnap` 在距起点 < 8px 时提示闭环。
- 点击起点 → 闭环提交（**仅 Polyline**；BandPolyline 不启用，防止"点击起点得到重合点"）。
- Line/Belt 两次点击即提交。

## 吸附管道 ConstrainPoint（优先级从高到低）

1. **共线吸附**：扫描所有已画点的 X/Y 基准线，容差 = 6px / CanvasScale（固定屏幕像素 → 缩放越大逻辑容差越小）。
   命中 anchor 基准线 → 绿线；命中其它点 → 橙色贯穿虚线（对象追踪）。
   距离守卫：鼠标距 anchor < snapTol 时 anchor 不参与吸附。
2. `AxisSnapEnabled=true` → 强制水平/竖直（Shift 切水平）。
3. 自由落点。

`ResetDraftSnapVisuals()` 在提交/取消/Backspace 时清零吸附提示线，避免孤儿虚线残留。

## 按键语义

- `Enter` = `FinishDraw`：只提交。
- `Esc` = `CancelOrClear`：按优先级逐级退出（格式刷待命 → 工具激活态 → 放弃草稿 → 取消选中），**绝不提交、绝不清空画布**。
- `Backspace` = `UndoLastPoint`：撤销上一个点；只剩 1 点时等同取消整段。

## 提交产物

- 路径类：算 bbox → 点集平移到 (0,0) → `BuildPathData`。
  - BandPolyline：默认 BandWidth=6，`SyncVertexBandWidths` 初始化逐顶点带宽；
  - Polyline：按闭环请求置 `IsClosed=true`；
  - Belt：不写 PathData，存自由端点后 `SolveBelt(autoEngage:true)` 自动收录挡路圆。
- 提交后一律 `DrawMode=None; ActiveToolKey=null` 自动退出绘制（防止误加点）。

## 相关页面

- [[handles-and-edit-modes]] - 绘制产物进入编辑
- [[constraint-system]] - 子对象拾取与约束创建
- [[undo-redo]] - 绘制提交落栈