---
title: SymbolDesigner 项目
created: 2026-11-17
updated: 2026-10-24
type: entity
tags: [architecture, wpf, mvvm]
prerequisites: []
confidence: high
---

# SymbolDesigner 项目

工业组态"元件设计器"：一个 WPF 桌面应用，用于绘制设备元件图形（管路/罐体/电机/传送带等），
供主看板程序 FactoryKanban 复用渲染与导出。

## 双重身份

- **独立 WinExe**：`SymbolDesigner.exe` 可单独启动（`App.xaml` + `MainWindow.xaml`）。
- **嵌入式库**：FactoryKanban 通过 `ProjectReference` 复用同一套代码（`SymbolDesigner.csproj` 注释明确"改一处两边生效"）。

## 技术栈

- .NET 8 `net8.0-windows` / WPF（`UseWPF`），`OutputType=WinExe`
- 依赖仅两个包：`CommunityToolkit.Mvvm 8.4`（MVVM）、`Clipper2 2.0`（布尔运算）
- 画布逻辑尺寸固定 1920×1080，缩放/平移通过 `Controls/ZoomPanViewport`（Decorator + RenderTransform）

## 目录结构

| 目录 | 职责 |
|---|---|
| `Models/` | 图元模型（`PrimitiveItem`/DTO）、工程文件（`SymbolProject`/`SymbolProjectFile`）、约束、阵列 |
| `Geometry/` | 纯几何算法（轮廓/布尔/皮带/相切/路径分布/SVG 解析），命名空间统一为 `SymbolDesigner.Geometry` |
| `ViewModels/` | `SymbolDesignerViewModel` 按职责拆成 25+ partial 文件（单体架构） |
| `Views/` | 画布视图、`DraggablePrimitive`（交互路由）、`PrimitiveTemplateSelector`（按 TypeKey 选模板） |
| `Controls/` | `ZoomPanViewport`、取色器、`SliderSpinBox` 等可复用控件 |
| `Converters/` | 颜色合成、图案画刷、缩放等转换器（纯函数大多放这里） |
| `Themes/` | `Theme.xaml`（`Brush.*` token 语义化颜色）+ `Generic.xaml`（控件样式） |
| `SymbolDesigner.Cli/` | 命令行工具 `sym`：DSL 建工程、校验、离屏渲染、编辑（见 [[sym-cli]]） |

## 关键设计决策

- **TypeKey 一维字符串决定身份**：图元渲染分支/属性面板/选中决策全部按 `TypeKey` 判断，废弃旧 L1/L2 两层分类。
- **几何不落盘**：皮带/布尔结果只存"拓扑/段结构快照 + 参数"，渲染时实时重算（见 [[primitive-model]]、[[band-outline-pipeline]]）。
- **颜色统一样式**：所有 UI 颜色必须引用 `Themes/Theme.xaml` 的 `Brush.*` token，禁止硬编码（仓库硬规则，见 `AGENTS.md`）。
- **撤销快照覆盖三集合**：Primitives + ArrayInstances + Constraints 打包为一个 JSON（见 [[undo-redo]]）。

## 相关页面

- [[architecture-overview]] - 架构总览（强关联）
- [[primitive-model]] - 图元数据模型（强关联）
- [[sym-cli]] - 命令行工具
- [[symz-container-format]]@weak - 工程文件格式笔记
- [[user-profile]] - 用户画像