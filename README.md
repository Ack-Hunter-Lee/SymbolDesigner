# SymbolDesigner — 工业组态元件设计器

一个 WPF 桌面应用，用于绘制工业组态中的**设备元件图形**（管路、罐体、电机、传送带等），
供主看板程序 **FactoryKanban** 复用渲染与导出。

## 双重身份

- **独立 WinExe**：`SymbolDesigner.exe` 可单独启动（`App.xaml` + `MainWindow.xaml`）。
- **嵌入式库**：FactoryKanban 通过 `ProjectReference` 复用同一套代码——改一处两边生效。

## 技术栈

| 项 | 值 |
|---|---|
| 框架 | .NET 8 `net8.0-windows` / WPF（`UseWPF`） |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| 几何 | Clipper2 2.0（布尔运算） |
| 外部依赖 | 仅上述两个 NuGet 包，其余全部 BCL 实现 |

## 功能概览

- **绘制**：矩形 / 圆角矩形 / 椭圆 / 三角形 / 圆环 / 扇形 / 直线 / 折线 / 带宽折线 / 传送带
- **编辑**：顶点 / 段 / 带宽 / 圆环 / 扇形 / 轮廓接缝手柄；多选、框选、复制粘贴、格式刷
- **几何约束**：直线-圆**相切**、**重合**（端点落圆弧 / 圆心落直线 / 点-点）
- **布尔运算**：合并 / 剪除 / 相交，结果可"调整源"再编辑后重新合并
- **阵列**：沿路径阵列、矩形阵列、环形阵列（CAD 块引用语义）
- **外观**：描边（颜色/宽度/虚线）、填充（无 / 单色 / 渐变 / 单色渐变 / 图案）、亮度 / 透明度合成
- **工程文件**：`.symz` 压缩包容器（自包含）与 `.sym.json` 纯文本（可 diff）双格式并存
- **导出**：XAML 片段（独立 `<Canvas>`，可粘贴到其它 WPF 项目）、复制到剪贴板 / 存 .xaml
- **SVG 导入**、背景参考图描图
- **CLI**：`sym` 无头命令行工具（DSL 建工程 / 校验 / 离屏渲染 / 编辑，见 `SymbolDesigner.Cli/`）

## 编译运行

在**仓库根目录**执行：

```bash
# 编译主程序
dotnet build SymbolDesigner.csproj

# 运行
dotnet run --project SymbolDesigner.csproj
# 或直接运行编译产物：bin/Debug/net8.0-windows/SymbolDesigner.exe
```

编译 CLI 工具：

```bash
dotnet build SymbolDesigner.Cli/SymbolDesigner.Cli.csproj
```

> **注意**：沙箱 / IDE 下若 `obj/` 文件报 `Access to the path ... is denied`（MSBuild 服务节点持锁），
> 在构建命令后加 `--disable-build-servers` 重试。

## 目录导览

| 目录 | 职责 |
|---|---|
| `Models/` | 图元模型（`PrimitiveItem`/DTO）、工程文件（`SymbolProject`/`SymbolProjectFile`）、约束、阵列 |
| `Geometry/` | 纯几何算法（轮廓 / 布尔 / 皮带 / 相切 / SVG 解析），命名空间统一为 `SymbolDesigner.Geometry` |
| `ViewModels/` | `SymbolDesignerViewModel` 按职责拆成 25+ partial 文件（单体架构） |
| `Views/` | 画布视图、`DraggablePrimitive`（交互路由）、`PrimitiveTemplateSelector` |
| `Controls/` | `ZoomPanViewport`（缩放平移视口）、取色器、`SliderSpinBox` 等 |
| `Converters/` | 颜色合成、图案画刷、缩放等转换器（纯函数大多放这里） |
| `Themes/` | `Theme.xaml`（`Brush.*` token 语义化颜色）+ `Generic.xaml`（控件样式） |
| `SymbolDesigner.Cli/` | 命令行工具 `sym` |
| `wiki/` | 项目知识库（术语表、架构、机制、踩坑） |
| `.trae/documents/` | 审查报告、功能实现计划 |

## 文档

- **[术语表](wiki/glossary.md)** — 新读者建议先看：TypeKey / BandWidth / BooleanPath / 阵列 / 约束等黑话速查
- **[Wiki 导航](wiki/index.md)** — 架构总览、图元模型、几何管道、撤销、约束、阵列等概念页
- `AGENTS.md` — 项目级规则（编译约定、颜色 token 硬规则、文件操作规范）

## 相关

- **FactoryKanban**：主看板程序，通过 ProjectReference 复用本仓库（外部仓库）。
- **Nodify**：`References/nodify/` 存有 Nodify 7.3 源码，涉及 Nodify 行为时直接读源码。