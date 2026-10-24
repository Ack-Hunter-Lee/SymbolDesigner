---
title: 主题系统与颜色统一样式
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [wpf, architecture]
prerequisites: [symboldesigner-project]
confidence: high
---

# 主题系统与颜色统一样式

所有 UI 颜色由 `Themes/Theme.xaml` 的语义化 `Brush.*` token 提供；`Themes/Generic.xaml` 提供控件样式。这是仓库**硬性规则**（见根 `AGENTS.md`）：任何 XAML / C# 禁止硬编码颜色字面量。

## 资源合并顺序

`App.xaml` 中 `Theme.xaml` 必须在 `Generic.xaml` 之前合并 —— Generic 内的样式引用了 Theme 的 Brush。

## Token 分组

Theme.xaml 实际有 **10 组**（比 AGENTS.md 概述的 8 组多两组）：

| 组 | 用途 |
|---|---|
| `Brush.Bg.*` | 背景/画布 |
| `Brush.Border.*` | 边框 |
| `Brush.Text.*` | 文本 |
| `Brush.State.*` | 状态色（选中/悬停/强调） |
| `Brush.Port.*` | 端口 |
| `Brush.Brand.*` | 品牌/绘制线条 |
| `Brush.Overlay.*` | 浮层 |
| `Brush.Tool.*` | 工具栏 |
| `Brush.Outline.*` | 闭合轮廓（额外） |
| `Brush.Palette.Spectrum` | 取色面板彩虹（注释明确"固定展示数据，不是主题色"） |

主色基底 `#181818`；工具栏图标统一 `Brush.Tool.Icon`（白）。

## 落地方式

- XAML：`{StaticResource Brush.xxx}`；C# 动态画刷：`App.Current.TryFindResource("Brush.xxx")`。
- `TagToBrushesConverter` 的 Tag 从 hex 升级为 token 名。
- 新增颜色：先考虑复用已有 token，确实需要新色先加 Token 再接引用。

## 豁免边界

1. **Objective DataTemplate 导出 / CLI Renderer 的 DTO hex**：属"客户资产/画作内容"，允许硬编码。
2. **ColorDialogWindow 的 HSV 渐变光谱**：固定展示数据，不是主题色。
3. **资源缺失兜底色**（TagToBrushesConverter 等）。

## 相关页面

- [[symboldesigner-project]] - 项目实体
- [[xaml-export]] - 导出豁免规则
- [[sym-cli]] - CLI 渲染的颜色处理