# 操作日志

> 所有 wiki 操作按时间追加。近期记录保留在此，更早按月归档到 `_archive/log/`。

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