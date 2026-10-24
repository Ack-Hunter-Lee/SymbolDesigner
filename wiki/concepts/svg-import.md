---
title: SVG 导入（SvgParser）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [geometry, wpf, file-format]
prerequisites: [primitive-model]
confidence: high
---

# SVG 导入（SvgParser）

`Geometry/SvgParser.cs`（命名空间 `SymbolDesigner.Svg`）把 SVG 文件解析成 `PrimitiveItem` 列表，支持常见基础图形。

## 解析器

- `XmlReader` 流式解析：Ignore DTD（防 XXE）、IgnoreComments。
- 支持 `rect/circle/ellipse/line/polyline/polygon/path/g`；`g` 递归但样式**不真正向下继承**（style 只取当前元素）。
- 属性与 `style="..."` 双来源，style 覆盖属性。

## 路径采样（PathDataSampler）

- 支持 M/L/H/V/C/S/Q/T/A/Z（大小写 = 绝对/相对）。
- 三次/二次贝塞尔用 de Casteljau 递归细分（Flatness=0.5 亚像素，深度上限 18）。
- 弧段用 W3C 端点→圆心转换后等角采样（上限 256 段防病态卡顿）。
- **隐式命令延续未实现**（数字开头直接跳过）。
- 已知 P0 曾修：`SampleArc` 起点==终点时 0/0 → NaN 污染几何，已加退化防御。

## 坐标变换（重要不一致）

- 文件头注释声称"viewBox 等比缩放居中"，但实际 `BuildTransform` 是 **scale=1 纯平移**（`-viewBox.X, -viewBox.Y`）：
  SVG 单位 = 画布单位 1:1，viewBox 只补偿原点偏移。
- 宽高属性 `%` 不支持（fallback）。

## 映射到 PrimitiveItem

| SVG 元素 | 结果 |
|---|---|
| rect（rx/ry） | Rectangle / RoundedRectangle |
| circle / ellipse | Ellipse |
| line | Polyline（IsClosed=false） |
| polyline / polygon | Polyline |
| path 单子路径 | Polyline |
| path 多子路径 | **BooleanPath**（多环骨架兜底） |

- 背景矩形过滤：覆盖 viewBox ≥85% 且白/近白/全透明（alpha<30）的 rect 判为"纸面/背景"跳过（启发式，已知取舍）。
- 颜色：fill 默认黑、stroke 默认 none；支持 `#hex/rgb()/rgba()/命名色`；`currentColor` 不解析。
- stroke-width 解析但**不按 scale 缩放**（留给属性面板微调）。

## 导入流程

- `NormalizeIntoCanvas` 无条件把整体最小包围盒平移到画布 (10,10)（SVG 坐标可能为负）。
- `BeginEdit/EndEdit` 包成一次撤销；选中第一个导入项；警告提示前 5 条。

## 相关页面

- [[primitive-model]] - 导入产物模型
- [[boolean-ops]] - 多子路径的 BooleanPath 兜底
- [[undo-redo]] - 导入作为一次撤销手势