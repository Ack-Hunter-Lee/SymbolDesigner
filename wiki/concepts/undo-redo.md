---
title: 撤销/重做模型
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [mvvm, interaction, wpf]
prerequisites: [primitive-model]
confidence: high
---

# 撤销/重做模型

基于**整画布 JSON 快照**的撤销/重做，不是命令模式。

## 快照

- 快照 = `ProjectSnapshotDto`：`Primitives` + `ArrayInstances` + `Constraints` 三个 DTO 列表打包成单个 JSON。
- **刻意排除**画布尺寸/背景图等工程元数据（那是 `SymbolProject` 的职责），减小快照大小与比对开销。
- 快照字符串可直接比较相等性：相同状态 → 相同字符串。

## 落栈时机（三路触发 + 去抖 + 手势合并）

- 触发：三集合的 `CollectionChanged` + 任何图元属性变化 → `ScheduleUndoCommit`。
- **350ms 去抖**（DispatcherTimer）：Slider 连续拖动合并为一次。
- **手势**：`BeginEdit/EndEdit` 用 `_gestureDepth` 深度计数，>0 时暂停去抖；归零时 `ForceCommitEdit` 统一收口 ——
  拖动元素/顶点/缩放/镜像/微移/阵列源编辑都包成一次撤销。
- 提交时比较 pending 与当前快照，**无变化则丢弃**（不污染栈）。
- 撤销前先 `ForceCommitEdit`，把 350ms 内未提交的编辑先落栈。

## 覆盖与重做

- 一个快照同时覆盖三集合 → 阵列/约束的新增删除天然可撤销，无需单独栈。
- Undo：把当前快照压 redoStack，再恢复旧快照；Redo 对称。栈上限 50（`TrimUndoStack`）。

## RestorePrimitives 边界

- `_isRestoring=true` 抑制一切历史记录。
- 内部 `ClearAllCore(scheduleUndo:false)` 清空三集合 → 按 DTO 重建 → `PruneConstraints` → 清空选中与手柄 →
  **`SolveAllBelts()`**（皮带几何不落盘，恢复后必须重算否则不可见）→ `IsDirty=true`。
- 被跳过的 `RefreshHiddenCount` 最后补刷。

## 相关页面

- [[primitive-model]] - DTO 快照的模型
- [[constraint-system]] - 约束纳入快照
- [[draw-state-machine]] - 绘制提交与撤销的衔接