---
title: 几何约束系统（相切/重合）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [geometry, interaction, mvvm]
prerequisites: [primitive-model, draw-state-machine]
confidence: high
---

# 几何约束系统（相切/重合）

CAD 风格的持久几何约束：用户把"直线 ⊙ 圆"钉成相切或重合，之后拖动任一参与图元，另一图元自动跟随保持关系。

## 抽象

- `IGeometricConstraint` 接口：`Id`、`Participants`、`Resolve(allItems)`。
- 约束集合 `ObservableCollection<IGeometricConstraint>` 持有**多个约束实例**表达拓扑（N 圆之间 0~4 条切线 = 多个实例）。
- 约束类型用 `GeometricConstraintKind` 枚举区分（序列化用）：`LineCircleTangent` / `CircleCircleTangent`（预留）/ `LineCircleCoincident`。

## 相切（AutoCAD GCTANGENT 语义）

- 角色由选择顺序决定：**先选 = 锚（Anchor）不动**，后选 = 调整对象（Follower），建立时移动 Follower 凑相切。
- `LineCircleTangentConstraint`：`AnchorIsLine` 决定谁被移动。
  - 锚=直线：`SolveTangency` 把圆心投影到直线 AB，再沿法线移出 `r × Side`（Side 保持建立时的相切侧）。
  - 锚=圆：`MoveLineToTangent` 把直线移到与圆相切。
- 拖动时：`ConstrainCircleDrag`（圆跟手、直线平移保持相切）/ `ConstrainLineDrag`（直线沿切向轨道滑动，法线锁死）。

## 重合（AutoCAD GCCOINCIDENT）

`LineCircleCoincidentConstraint` 三种模式（按点击子对象智能选择）：
- `EndPointOnArc`：直线端点径向投影到圆上最近点（幂等）。
- `CenterOnLine`：圆心投影到直线的垂足（圆心只能沿直线滑动）。**与相切冲突**：已有相切时拒绝创建。
- `PointCoincide`：直线端点与圆心重合。
- **双重约束**：相切 + 端点落圆弧可共存 —— 把已有重合约束升级 `LockToTangentPoint=true`，端点被锁定到切点，两条约束同时满足。

## 求解时机与收敛

- 任一参与图元几何变化 → `OnItemGeometryChanged` → **迭代求解至收敛**（`maxIter=10`）。
  为什么迭代：约束链（直线两端分别贴圆 A/B），拖圆 B 会连锁改直线 → 端点 A。
- `CaptureGeometry` 原地覆写字典避免每次重分配；收敛比较用 `GeometryTolerance = 1e-3`（精确 `==` 在浮点振荡下永不命中，会跑满迭代）。
- 约束增删走撤销快照（快照含 Constraints）；图元删除/清空 → `PruneConstraints` 清理失效约束。

## 子对象识别

- 点击时 `GeometryMath.ClassifyPick` 按图元类型识别子对象：圆 → 圆心/圆弧；线 → 端点（PathData 首末点）/线段。
- `PickMarker` 覆盖层高亮命中点（圆心橙、端点绿），让用户确认约束选中了哪个子对象。
- 约束 DTO：`TangencyConstraintDto` 带 `Kind`、`CoincidentModeStr`、`LockToTangentPoint` 等字段，旧文件缺省按相切解析。

## 相关页面

- [[draw-state-machine]] - 吸附/绘制与约束的交互
- [[undo-redo]] - 约束纳入撤销快照
- [[band-outline-pipeline]]@weak - 同一几何容差体系（1e-3）