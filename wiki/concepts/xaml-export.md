---
title: XAML 导出（XamlExporter）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [wpf, file-format]
prerequisites: [primitive-model]
confidence: high
---

# XAML 导出（XamlExporter）

`ViewModels/XamlExporter` 把画布上所有 `PrimitiveItem` 序列化成**可粘贴到其他工程的独立 XAML `<Canvas>` 片段**。

## 设计原则

- 输出不依赖编辑器运行时（无 DraggablePrimitive / ViewModel 引用）。
- **纯字符串拼接**，不用 `XamlReader`（避免 System.Xaml 类型映射、画笔资源依赖）。
- 数值一律 `InvariantCulture`（防本地化小数点变逗号）。

## 结构

- 根 `<Canvas>` 尺寸 = 全部图元内容包围盒；所有子元素坐标归一化平移到 (0,0) 起。
- 每个图元按 `TypeKey` switch 生成对应元素（Rectangle/Ellipse/Line/Polygon/Path）；未知类型输出 `<!-- 注释 -->` 防破坏 XML。
- 闭合 BandPolyline / BooleanPath：`OutlineGeometry.ToString()` 序列化 Path mini-language（含真圆弧、多环填充规则）。
- Ring：**单 Path 外顺内逆 Nonzero 双环**（不能用两个 Ellipse —— 渐变/图案会被内圆截断）。
- Belt：单线描边 Path，`Stretch="None"`，下游可直接做 `StrokeDashOffset` 动画。
- 旋转注入：`RenderTransformOrigin="0.5,0.5" RenderTransform="{RotateTransform Angle=...}"`
  （WPF TransformConverter **不接受** WinRT `rotate()` 简写，实测抛异常）。

## 画刷资源

- 渐变/图案**不能内联进属性值**（`Fill="<LinearGradientBrush>"` 是非法 XAML）。
- 两遍扫描：先收集 Pattern/Gradient 资源进 `ExportContext`，再写元素时统一 `{StaticResource key}` 引用；同色同角复用 Key。
- `PatternBrushFactory.BuildResourceKey` 用参数量化 + hash 生成稳定 Key。

## 颜色豁免

- 导出物属于"客户资产"（AGENTS.md 颜色规则豁免），允许硬编码颜色。
- `HexToArgb` 统一 `#RRGGBB` / `#AARRGGBB` → `#AARRGGBB`。
- 描边用真实 `p.StrokeColor`（旧版写死 `#FF5a7a8c` 导致用户改色丢失，已修复）。

## 相关页面

- [[primitive-model]] - 图元的导出数据源
- [[array-system]] - 阵列导出时展平补回
- [[file-formats]] - 工程文件（导出与保存的边界）