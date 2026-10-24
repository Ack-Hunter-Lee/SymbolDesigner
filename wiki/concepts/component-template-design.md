---
title: 组态元素模板设计规范（Component Template Design）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [wpf, architecture, geometry]
prerequisites: [primitive-model]
confidence: high
---

# 组态元素模板设计规范（Component Template Design）

> 本页是 `AGENTS.md` 引用的模板设计规则页（跨项目生效：主看板程序 FactoryKanban 的
> `Controls/Symbols/SymbolTemplates.xaml` 与本仓库画布模板都适用）。

生成 / 修改画布组态元素模板（DataTemplate）**必须遵守**以下规则，否则会出现"图形四周留白、拉宽变形、描边粗细不协调"等画布观感问题。

## 核心规则

### 1. 图形撑满节点边界框

- `Viewbox Stretch=Uniform` + 对齐 `Stretch`（**不是 Center**）。
- **禁止多层居中容器嵌套**：外层撑满、内层居中放大后仍会留白。

### 2. 包围盒占满内容区

- Path 坐标贴近内容边界（边距 ≤ 2~3px），不留大片无效空白。
- 否则放大后图形与包围盒之间出现可见空隙，选中框/端口对不齐。

### 3. 两类伸缩策略

| 图形类型 | 策略 |
|---|---|
| 等比例图形（圆/方/符号） | 固定内容 + `Viewbox` 撑满 |
| 横向可拉长设备（管道/皮带/槽） | 内容宽随节点纵横比动态算（`AspectWidthConverter`）+ 元素横向拉伸/外移 + **垂直铺满** |

### 4. 描边随 Viewbox 放大变粗

- `Viewbox` 放大 N 倍 → 线宽视觉也变成 ×N。
- 按目标尺寸预估并**调细基准描边**，避免放大后描边粗得突兀。

### 5. 生成后跑检查清单

1. 拖入画布目视：四周无空白；
2. 拉宽不变形；
3. 描边协调；
4. 无端口组件边界贴合。

## 与渲染管道的衔接

- 模板消费的是 `PrimitiveItem` 的计算几何（局部坐标，原点 = 包围盒左上角），
  由 `Canvas.Left/Top` 定位 —— 所以模板里不需要再算偏移，见 [[primitive-model]]。
- 端口/连接点必须贴着图形实际几何边界，否则"图形撑满"规则下端口会悬空。

## 相关页面

- [[primitive-model]] - 图元几何的局部坐标约定
- [[xaml-export]] - 模板/导出的另一种形态
- [[theme-system]] - 模板颜色必须引用 Brush token