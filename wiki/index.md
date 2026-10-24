# Wiki 导航

> SymbolDesigner 项目知识库。写作规范见 [[SCHEMA]]；用户画像见 [[user-profile]]。
> 探索入口：[[project-exploration-2026-11]]。

## 项目与架构

- [[symboldesigner-project]] - 项目实体（独立 WinExe + 被 FactoryKanban 复用）
- [[architecture-overview]] - 架构总览：分层、数据流、关键机制速查
- [[theme-system]] - 主题系统与颜色统一样式（Brush token）
- [[component-template-design]] - 组态元素模板设计规范（跨项目规则）

## 模型与格式

- [[primitive-model]] - 图元数据模型（TypeKey 体系、局部坐标、DTO）
- [[file-formats]] - 工程文件格式（.sym.json / .symz）
- [[xaml-export]] - XAML 导出（XamlExporter）

## 几何与算法

- [[band-outline-pipeline]] - 带宽轮廓几何管道
- [[boolean-ops]] - 布尔运算（Clipper2 接入、F0 多环、弧重拟合）
- [[svg-import]] - SVG 导入（SvgParser / PathDataSampler）
- [[notes/belt-primitive]] - 传送带图元（相切穿绕的皮带几何）

## 交互与编辑

- [[draw-state-machine]] - 绘制状态机（路径/形状、吸附、按键）
- [[handles-and-edit-modes]] - 手柄体系与编辑模式
- [[undo-redo]] - 撤销/重做模型
- [[constraint-system]] - 几何约束（相切/重合）
- [[array-system]] - 阵列系统（块引用 + 路径分布）

## 实体

- [[symboldesigner-project]] - 项目
- [[sym-cli]] - 命令行工具 sym

## 对比

- [[band-polyline-vs-polyline]] - BandPolyline 与 Polyline 对比

## 查询 / 调查

- [[project-exploration-2026-11]] - 本次探索记录与结论汇总

## 笔记与踩坑

- [[notes/legacy-thickness-handle]] - ThicknessHandle → BandWidthHandle 重命名考古
- [[notes/wpftmp-cs0579-duplicate-attributes]] - WPF wpftmp 临时工程 CS0579 构建踩坑
- [[notes/symz-container-format]] - .symz 压缩包容器格式与"内嵌+外链双份致丢图"
- [[notes/belt-primitive]] - 传送带图元：相切穿绕的皮带几何