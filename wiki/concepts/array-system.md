---
title: 阵列系统（Array）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [geometry, interaction, wpf]
prerequisites: [primitive-model]
confidence: high
---

# 阵列系统（Array）

CAD 风格的块引用：一个 `ArrayInstance` = 源快照 + 类型参数 + 整体偏移，画布上不存 N 份副本。

## 数据模型

- `Models/ArrayInstance`：`Source`（PrimitiveItemDto 快照）+ `Type`（Path=0 / Rectangular=1 / Polar=2，Path 故意为 0 保证旧文件兼容）+ 参数。
- `OffsetX/Y` 是阵列整体位移的累积值，与源/路径分离 —— 拖动阵列不改源、不改路径。
- `Source` 用 DTO 深拷贝（`CreateFromDto→ToDto` 实例化再序列化），源被删/改不影响阵列。

## 渲染层展平（ArrayRenderItems）

- `ArrayRenderItems` 是**渲染层影子集合**：`RebuildArrayRenderItems()` 把每条阵列展开为 N 份克隆供 ItemsControl 绑定。
- **阵列副本不进 `Primitives`**：避免与源重复、避免进撤销栈/元素面板/约束系统。
- 克隆带 `ArrayOwner` 引用：点击/拖动由 `DraggablePrimitive` 路由到整组操作（选中阵列、整体平移）。
- 锚点 = 源包围盒中心；副本旋转绕自身中心。

## 路径阵列

- **通用几何入口**：任意能产出几何的图元都能当路径（`SelectionGeometry` → `PolylineToSegments.Convert` 摊平，只取最长轮廓）。
- `PathDistributor`：等弧长参数化（Line/Arc 段），`ProjectOntoPath` 返回位置+弧长+切线角+有符号法向偏移。
- 路径阵列存**画布绝对坐标路径**（不是路径图元的局部 PathData），否则落点错位。
- **旋转 = 相对源位的切线增量**：`源自身Rotation + NormalizeAngleDelta(切线角 - 源位切线角)`。
  旧实现直接赋绝对切线角，导致"垂直于履带的小方块"阵列后变沿履带方向。
- 闭合路径整圈均分（起点只定相位）；开放路径 N-1 等分。

## 交互

- **整体拖动** `MoveArrayElement`：只累加 `OffsetX/Y` + 同步平移现有克隆，**刻意不重建容器**（重建会移除正被拖的 Thumb 打断手势）。
- **源位编辑** `EnterArraySourceEdit`：把源借出为画布普通图元，隐藏源位克隆，编辑时实时同步 `arr.Source` 并重建副本；整个手势 `BeginEdit/EndEdit` 收口为**一次撤销**。
- 预览跳过第 1 份（第一份与源重合）；数量 N = 含源共 N 个。

## 导出

- 阵列虽然不进 Primitives，但 `XamlExporter` 导出时展平补回：`Primitives + ArrayRenderItems.Where(IsVisible)`。

## 相关页面

- [[xaml-export]] - 阵列导出补回
- [[undo-redo]] - 阵列纳入撤销快照
- [[primitive-model]] - 源图元模型