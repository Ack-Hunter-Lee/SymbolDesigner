---
title: 架构总览
created: 2026-11-17
updated: 2026-10-24
type: concept
tags: [architecture, wpf, mvvm]
prerequisites: [symboldesigner-project]
confidence: high
---

# 架构总览

SymbolDesigner 是典型的 WPF + MVVM（CommunityToolkit.Mvvm）单体应用，但按"职责拆分 + 纯函数下沉"组织。
核心思想：**模型层只存编辑数据，几何一律实时计算；VM 只做协调，几何算法抽成无状态的静态工具类。**

## 分层

```
Views (XAML + DraggablePrimitive/模板选择器)
   ↓ 绑定/命令
SymbolDesignerViewModel（25+ partial 文件，协调一切）
   ↓ 读写
Models (PrimitiveItem/DTO/SymbolProject/约束/阵列)
   ↓ 调用
Geometry + Converters（纯静态算法：BandOutlineBuilder/BooleanOps/BeltSolver/...）
   ↓
Clipper2（唯一第三方几何依赖）
```

- **Views 层极薄**：`DraggablePrimitive`（Thumb）只做鼠标事件 → VM 命令/方法的翻译，
  `PrimitiveTemplateSelector` 只按 `TypeKey` 选 DataTemplate。
- **VM 是单体**：`SymbolDesignerViewModel` 按职责拆 25+ partial（Draw/Selection/UndoRedo/Array/...），
  共享 `Primitives`/`ArrayInstances`/`Constraints` 三个 ObservableCollection。
  代码审查曾建议抽 service，团队选择**保持单体**（见 `.trae/documents/review-report-2026-09-12.md`）。
- **几何是纯函数**：`GeometryMath`、`BandOutlineBuilder`、`BooleanOps`、`BeltSolver`、`PathDistributor`
  都不依赖 VM 状态，GUI 与 CLI 共用。它们位于 `Geometry/` 目录，命名空间统一为 `SymbolDesigner.Geometry`。

## 核心数据流

1. **绘制**：Draw 状态机收集草稿点 → 提交时算 bbox → 生成 `PrimitiveItem`（PathData 局部坐标，原点对齐包围盒）。
2. **渲染**：XAML DataTemplate 绑定 `OutlineGeometry`/`PolylineGeometry`/`RingGeometry` 等计算属性，
   getter 按输入缓存（`_cached*` 字段），拖动只触发通知不重算。
3. **编辑**：手柄拖动 → 回写模型 → `OnItemGeometryChanged` → 约束求解 + 皮带重算 + 撤销落栈。
4. **持久化**：`ToSymbolProject` 生成 DTO → `SymbolProjectFile` 写 `.symz`/`.sym.json`（GUI/CLI 同一收口）。
5. **导出**：`XamlExporter` 把图元序列化成独立 `<Canvas>` XAML（纯字符串拼接）。

## 关键机制速查

| 机制 | 位置 | 要点 |
|---|---|---|
| 撤销/重做 | `UndoRedo.cs` | 整画布 JSON 快照（三集合），350ms 去抖 + 手势合并，上限 50 |
| 绘制状态机 | `Draw.cs`/`DrawState.cs` | 路径类多点、形状类两段式、共线/轴吸附、Esc 只取消 |
| 手柄体系 | `Handles.cs` | 顶点/段/带宽/轮廓接缝/圆环/扇形/皮带端点 |
| 几何约束 | `Constraints.cs` + `TangentSolver` | 相切/重合，迭代求解至收敛（maxIter=10） |
| 阵列 | `Array.cs` + `PathDistributor` | 块引用 + 渲染层展平影子集合 |
| 图层排序 | `Layers.cs` | 不是真图层模型，是 ZIndex 排序命令 |

## 设计取舍

- **TypeKey 字符串驱动一切**（而非枚举 + 多态）：模板选择、导出、属性面板可见性、填充判定都按字符串 switch。
- **几何不落盘**：皮带只存拓扑，布尔只存段结构快照；加载/恢复/拖动后统一重算，避免"过期几何"。
- **双格式工程文件**：`.symz` 容器（自包含、确定性输出）与 `.sym.json` 纯文本（可 diff）并存。
- **颜色 token 化**：`Theme.xaml` 的 `Brush.*` 是唯一颜色来源（仓库硬规则）。
- **外部依赖最小化**：只引 Clipper2（布尔）与 CommunityToolkit.Mvvm，其余全部 BCL 实现。

## 相关页面

- [[symboldesigner-project]] - 项目实体
- [[primitive-model]] - 图元模型
- [[band-outline-pipeline]] - 带宽轮廓几何
- [[undo-redo]] - 撤销/重做
- [[draw-state-machine]] - 绘制状态机
- [[file-formats]] - 工程文件格式