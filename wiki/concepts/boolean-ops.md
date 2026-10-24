---
title: 布尔运算（BooleanOps 与 Clipper2 接入）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [geometry, wpf]
prerequisites: [primitive-model]
confidence: high
---

# 布尔运算（BooleanOps 与 Clipper2 接入）

对两个闭合图元做并/差/交，结果是一个 `BooleanPath` 图元 —— 一个"死形状"（静态多环 PathData 快照）。

## 为什么用 Clipper2 而不是 WPF Geometry.Combine

WPF `Geometry.Combine` 对弧/贝塞尔先按未知密度摊平再布尔，导致矩形∩圆时正圆弧变形偏心
（实测切点误差 0.458 逻辑单位 → `GeometryMath.MergeTol = 0.5` 的由来）。
`ClipperBooleanEngine` 用 `Clipper2Lib.ClipperD`：椭圆按 2° 均匀离散（180 点/圆）、矩形直接 4 角、其它走 `GetFlattenedPathGeometry(0.5)`。

## 分工与降级

- `BooleanOps.Compute` **先试 Clipper2**；返回 null（不支持/异常/碎环全被剔除）→ 回退 `Geometry.Combine`。
- Clipper 侧异常**捕获后降级**（只留 Debug 输出，不弹窗）；序列化/装配阶段自身 bug 则上抛暴露。

## 结果表示：F0 多环段结构快照

- 落盘是 `F0 M ... L ... Z M ... Z`：`F0` = EvenOdd 填充规则前缀，每环一个 `M…Z` 链。
- `SegData` **保留弧/贝塞尔完整参数**（终点+半径+大弧+扫掠），**不摊平** —— 渲染时 `Geometry.Parse` 原生画弧，
  骨架点 = 每环"起点 + 各段终点"，可再次作为下一次布尔输入（`CanvasFillGeometry` 返回它）。
- 序列化坐标 3 位小数（`CoordEps=1e-3`）。

## 弧重拟合（Kasa 最小二乘）

Clipper 输出是多边形顶点环；`SerializeRing` 做**贪心弧重拟合**，把真正的圆弧点段重新收编为 `A` 命令。
关键坑（均已在源码注释中记录）：
- **不能用三点定圆**：2° 间隔的近共线三元组会灾难性消元，拟合出半径数千的畸形圆 → 必须用 Kasa 多点最小二乘。
- 拒绝"直线退化成的巨圆"：R > 200×窗口包围盒最大边 → 不是弧。
- 弧窗口必须 ≥8 点（≥18°），否则矩形拐角处三元组残差 0 会产生伪弧。
- `AppendArc` 的 largeArc 曾写反导致优弧被误判为劣弧坍缩（已修）。

## 碎环剔除与"死形状"

- `SignedDoubleArea`（shoelace）剔除 |面积| < 1e-3 的数值碎环（重叠相切产生的细长/退化环）。
- 两几何不相交/结果无面积 → `Compute` 返回 null，调用方拒绝或提示。

## 渲染：BakeCornerRadii

`BooleanPath` 渲染**不走步骤管道**：`BooleanOps.BakeCornerRadii(PathData, radii[], styles[])` 把 L-L 尖角烘焙成 A 真圆弧
（Round 才烘焙，Miter/Bevel 保持尖角），然后 `Geometry.Parse` 原生渲染。
- 切点偏移超过相邻边 0.95×min 时**缩小半径**保持相切防自交；
- 从后往前插入弧避免索引位移。
- 这是**渲染/导出期产物，不写回 PathData** —— 落盘永远是可编辑的段结构快照。

## 相关页面

- [[band-outline-pipeline]] - 同一套几何数值容差体系
- [[primitive-model]] - BooleanPath 的 TypeKey 与 CanvasFillGeometry
- [[xaml-export]] - 布尔结果导出为多环 Path