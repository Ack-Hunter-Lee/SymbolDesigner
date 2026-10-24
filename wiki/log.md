# 操作日志

> 所有 wiki 操作按时间追加。近期记录保留在此，更早按月归档到 `_archive/log/`。

## 2026-10-24

- 新增术语表页 `glossary.md`（TypeKey / BandWidth / BooleanPath / 阵列 / 约束 / 撤销 / 视图主题 / 工具命令），
  并登记到 `index.md`。目的：降低新人读代码时的黑话门槛（可读性改进第一步）。
- 新增仓库根 `README.md`：项目简介 / 双重身份 / 技术栈 / 功能概览 / 编译运行 / 目录导览 / 文档链接。
  与术语表共同完成"可读性改进第 1 步（README + Glossary）"。
- 可读性改进第 2 步（命名空间统一 + DTO 术语统一）：
  - `Geometry/` 13 个类命名空间统一为 `SymbolDesigner.Geometry`（原 `SymbolDesigner.Converters` / `SymbolDesigner.Svg`），
    同步更新约 16 个引用方的 using 与 `Converters.*` 前缀（88 处引用）。
  - `PrimitiveItemDto.Thickness` → `BandWidth`、`VertexThicknesses` → `VertexBandWidths`、
    `VertexThicknessDto` → `VertexBandWidthDto`、`Incoming/OutgoingThickness` → `Incoming/OutgoingBandWidth`，
    均加 `[JsonPropertyName]` 保持旧文件 JSON 键名兼容；CLI DSL 的 `Thickness` 保留（外部契约）。
  - 同步更新 README 与 wiki 中"命名空间历史遗留"的过时描述。
  - 构建验证：`SymbolDesigner.csproj` 与 `SymbolDesigner.Cli.csproj` 均 0 错误通过
    （`--disable-build-servers`；6 / 1 条既有警告）。
  - 坑：`SymbolDesigner.Geometry` 命名空间存在后，`SymbolDesigner.*` 下的裸 `Geometry` 会被解析成命名空间
    而非 WPF 的 `System.Windows.Media.Geometry`（**类型位置**尤其明显，using 别名救不了——命名空间成员优先）。
    涉及 `BandOutlineBuilder` / `BooleanOps` / `ClipperBooleanEngine` / `PolylineToSegments` /
    `PatternBrushFactory` / `Renderer` / `BeltDtoGeometry`，统一改为全限定名。
  - CLI 侧连带：`DslToProject` 的 DTO 赋值改 `BandWidth = p.Thickness`（DSL 字段名不变）；
    `Renderer` 恢复 `using SymbolDesigner.Converters;`（ColorUtil / PatternBrushFactory 仍归属 UI 转换器）。

## 2026-11-17

- 初始化项目 wiki 结构：创建 `SCHEMA.md`、`index.md`、`log.md`、`user-profile.md`。
- 探索 SymbolDesigner 项目：读核心模型/主 VM/视图交互，并行派出 4 个子代理深挖
  几何管道、编辑交互、阵列/持久化/导出、CLI/视图/控件/主题 四大子系统。
- 新增实体页：`entities/symboldesigner-project.md`、`entities/sym-cli.md`。
- 新增概念页：
  - `concepts/architecture-overview.md`（架构总览）
  - `concepts/primitive-model.md`（图元数据模型）
  - `concepts/band-outline-pipeline.md`（带宽轮廓几何）
  - `concepts/boolean-ops.md`（布尔运算）
  - `concepts/constraint-system.md`（几何约束）
  - `concepts/draw-state-machine.md`（绘制状态机）
  - `concepts/undo-redo.md`（撤销/重做）
  - `concepts/handles-and-edit-modes.md`（手柄与编辑模式）
  - `concepts/array-system.md`（阵列系统）
  - `concepts/file-formats.md`（工程文件格式）
  - `concepts/xaml-export.md`（XAML 导出）
  - `concepts/svg-import.md`（SVG 导入）
  - `concepts/theme-system.md`（主题系统）
  - `concepts/component-template-design.md`（组态模板设计规范，补齐 AGENTS.md 引用缺失页）
- 新增对比页：`comparisons/band-polyline-vs-polyline.md`。
- 新增查询页：`queries/project-exploration-2026-11.md`（探索记录总览）。
- 更新 `index.md` 登记全部页面。
- 系统维护（非 wiki）：修复工作区 ACL 权限问题；按用户要求禁用 Codex 自启动（注册表 State 2→1）。