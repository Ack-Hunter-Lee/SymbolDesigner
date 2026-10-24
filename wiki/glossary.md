---
title: 术语表（Glossary）
created: 2026-10-24
updated: 2026-10-24
type: concept
tags: [architecture, meta]
prerequisites: []
confidence: high
---

# 术语表（Glossary）

> 面向新读者的术语速查。代码注释里反复出现的黑话在这里集中解释。
> 相关概念详见 [[architecture-overview]]、[[primitive-model]]。

## 图元与身份

| 术语 | 位置 | 含义 |
|---|---|---|
| TypeKey | `Models/PrimitiveItem.cs` | 图元类型的一维字符串身份（`"Rectangle"` / `"Belt"` …）。渲染分支、XAML 导出、属性面板可见性、命中检测全部按它分派 |
| L1Primitive | `Models/L1Primitive.cs` | 图元类型枚举。**枚举名 = TypeKey**（`L1Primitive.Belt` → `"Belt"`），是 TypeKey 的唯一来源 |
| PrimitiveItem | `Models/PrimitiveItem.cs` | 画布上的单个图形实例。同时承担渲染输入、编辑对象、持久化对象、导出对象 |
| PathData | `PrimitiveItem.PathData` | XAML mini-language 路径字符串（`"M x,y L x,y …"`）。**局部坐标**，原点 = 图元包围盒左上角，模板用 `Canvas.Left/Top` 定位 |
| BooleanPath | `L1Primitive.BooleanPath` | 布尔运算结果的图元类型。存"多环骨架快照 + 源图元 DTO 快照"，不是普通可参数化编辑的图形 |
| BandPolyline | `L1Primitive.BandPolyline` | 带宽折线：折线骨架按 BandWidth 扩出左右轮廓，形成闭合轮廓带 |
| Polyline | `L1Primitive.Polyline` | 纯折线：只描边骨架，无带宽/轮廓，与 BandPolyline 是兄弟类型 |
| Belt | `L1Primitive.Belt` | 传送带图元：绕圆滚轮（Ellipse/Ring）相切穿行的单条路径，几何不落盘、实时重算 |

## 几何与编辑

| 术语 | 位置 | 含义 |
|---|---|---|
| BandWidth | `PrimitiveItem.BandWidth` | 带宽折线的整体宽度。序列化 DTO 里叫 `Thickness`（历史遗留，见 [[notes/legacy-thickness-handle]]） |
| VertexBandWidth | `PrimitiveItem.VertexBandWidths` | 逐顶点带宽：每顶点有 `IncomingBandWidth`（承上）/ `OutgoingBandWidth`（启下），段 i 首端宽 = 顶点 i 的 Outgoing |
| OutlineGeometry | `PrimitiveItem.OutlineGeometry` | 带宽轮廓的几何（Path 绑定的 Data）。注意：它不等于"描边几何"，而是骨架 + 带宽偏移算出的轮廓 |
| IsClosed | `PrimitiveItem.IsClosed` | 闭合标志。注意有两套语义：用户设置的 `IsClosed`（仅 Polyline 有效）vs `BandOutlineBuilder.IsPathDataClosed`（PathData 末尾是否真有 `Z`，几何事实） |
| OutlineEditMode | `ViewModels/SymbolDesignerViewModel.EditMode.cs` | 选中 BandPolyline 后的编辑模式：骨架 / 带宽线 / 轮廓（接缝） |
| Handles | `ViewModels/SymbolDesignerViewModel.Handles.cs` 等 | 手柄体系：顶点、段、带宽、圆环、扇形、轮廓接缝、皮带端点 |
| 调整源（Source Adjust） | `ViewModels/SymbolDesignerViewModel.BooleanOps.cs` | 布尔结果按源快照物化出临时源图元到画布编辑，重新合并后收起源。全程一次撤销手势 |
| 格式刷（Format Painter） | `ViewModels/SymbolDesignerViewModel.FormatPainter.cs` | 复制选中图元的样式，点击应用到其它图元；支持连刷模式 |

## 阵列

| 术语 | 位置 | 含义 |
|---|---|---|
| ArrayInstance | `Models/ArrayInstance.cs` | 阵列实例（CAD 块引用）：源快照 + 路径/矩形/环形参数 + 整体偏移。不直接渲染 |
| ArrayRenderItems | `SymbolDesignerViewModel.ArrayRenderItems` | 渲染层"展平"集合：每个 ArrayInstance 展开为 N 份克隆，供 ItemsControl 绑定。不进入 Primitives、不进撤销栈 |
| OffsetX / OffsetY | `ArrayInstance.OffsetX/Y` | 阵列整体位移（拖动任一副本 = 平移整个阵列） |

## 几何约束

| 术语 | 位置 | 含义 |
|---|---|---|
| Constraints | `ViewModels/SymbolDesignerViewModel.Constraints.cs` | 持久几何约束集合（`ObservableCollection<IGeometricConstraint>`） |
| 相切（Tangent） | `Models/TangentConstraint.cs` | 直线-圆相切约束：先选为锚，后选被移动去凑相切 |
| 重合（Coincident） | `Models/CoincidentConstraint.cs` | 直线-圆重合约束：端点落圆弧 / 圆心落直线 / 点-点重合 |
| TangentSolver | `Geometry/TangentSolver.cs` | 相切/重合的求解器（几何纯函数） |

## 撤销与持久化

| 术语 | 位置 | 含义 |
|---|---|---|
| 快照（Snapshot） | `ViewModels/SymbolDesignerViewModel.UndoRedo.cs` | 撤销栈的元素：整画布（Primitives + ArrayInstances + Constraints）序列化为一个 JSON 字符串，深拷贝无共享引用 |
| ProjectSnapshotDto | `Models/SymbolProject.cs` | 撤销/重做快照的 DTO 容器 |
| SymbolProject | `Models/SymbolProject.cs` | 工程清单模型（两种落盘形态共用同一 schema） |
| PrimitiveItemDto | `Models/SymbolProject.cs` | 图元的序列化 DTO。只存编辑数据，不存 `IsSelected`/计算几何。字段名与模型**不完全一致**（如 `Thickness` = BandWidth） |
| .symz | `Models/SymbolProjectFile.cs` | 压缩包容器格式：内含 project.json + assets/ |
| .sym.json | `Models/SymbolProjectFile.cs` | 纯文本单文件格式：底图内联 Base64，便于 diff |

## 视图与主题

| 术语 | 位置 | 含义 |
|---|---|---|
| ZoomPanViewport | `Controls/ZoomPanViewport.cs` | 可复用视口控件：缩放、平移、自动适配、居中 |
| CanvasScale | `SymbolDesignerViewModel.CanvasScale` | 当前实际渲染缩放系数（autoScale × ZoomLevel），手柄/提示线做"反缩放"用 |
| Brush.* token | `Themes/Theme.xaml` | 颜色唯一来源。所有 UI 颜色必须引用 `Brush.*` 资源，禁止硬编码（仓库硬规则，见 `AGENTS.md`） |
| DraggablePrimitive | `Views/DraggablePrimitive.cs` | 画布图元容器（Thumb 实现）：拖动回写 X/Y、点击路由到选中/约束/格式刷 |

## 工具与命令

| 术语 | 位置 | 含义 |
|---|---|---|
| DrawMode | `Models/L1Primitive.cs` | 绘制交互模式。与 `L1Primitive` 枚举同名同值（`DrawMode.Belt` ↔ `L1Primitive.Belt`） |
| ToolboxEntry | `Models/PrimitiveItem.cs` | 工具栏条目：`TypeKey` + 标签 + 图标。点击后进入对应 DrawMode |
| sym | `SymbolDesigner.Cli/` | 无头命令行工具：DSL 建工程、校验、离屏渲染、编辑（见 [[sym-cli]]） |
| DSL | `SymbolDesigner.Cli/Dsl.cs` | 给外部 agent 用的结构化 JSON 描述图元 |

## 相关页面

- [[primitive-model]] - 图元数据模型（TypeKey、局部坐标、DTO）
- [[architecture-overview]] - 架构总览
- [[undo-redo]] - 撤销/重做
- [[array-system]] - 阵列系统
- [[constraint-system]] - 几何约束
- [[handles-and-edit-modes]] - 手柄体系与编辑模式
- [[notes/legacy-thickness-handle]] - Thickness/BandWidth 命名考古