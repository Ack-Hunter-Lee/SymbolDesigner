---
title: 工程文件格式（.sym.json / .symz）
created: 2026-11-17
updated: 2026-11-17
type: concept
tags: [file-format, wpf, cli]
prerequisites: [primitive-model]
confidence: high
---

# 工程文件格式（.sym.json / .symz）

`SymbolProject` 是唯一的工程清单模型，两种落盘形态共用同一 schema；`Models/SymbolProjectFile` 是 GUI 与 CLI 共用的读写收口。

## 两种形态

```
工程.symz                    ZIP 容器（默认格式）
├── project.json             SymbolProject 清单 + FormatVersion + assets[]
└── assets/background.png    二进制资产（底图原始字节，不内联）

工程.sym.json                纯文本单文件
└── 整个 SymbolProject 序列化（底图内联 Base64）
```

- `.symz`：自包含、清单可读、资产不膨胀 JSON；图片条目**不压缩**（已压缩格式再 deflate 白耗 CPU）。
- `.sym.json`：便于文本 diff / agent 直接读写。
- **写时按目标扩展名决定格式，就地保存不擅自升级**；读时扩展名优先 + ZIP 魔数 `PK` 兜底自动探测。
- `.symz` 产出**确定性字节**：条目顺序固定、时间戳统一 `1980-01-01` → 同内容重复保存字节一致（diff 干净、可缓存）。

## 资产与兼容

- 资产条目名只接受 `assets/<单文件名>`，拒绝绝对路径/盘符/`..`/多余层级（防 zip slip）；清单指向缺失条目跳过不报错。
- 格式转换不丢资产：`.symz` → `.sym.json` 时资产回填为内联 Base64；CLI 编辑时 assets 原样透传。
- 老字段兼容：`"ThickPolyline"→"BandPolyline"`、`"JoinStyle"→"OutlineJoinStyle"`、枚举缺省兜底、`BackgroundImageData/Path` 双字段保留。
- 底图读取优先级：容器资产 → 内联 Base64 → 老外部路径（失效静默不显示）。

## 序列化细节

- `JsonSerializerOptions`：`WriteIndented`、`WhenWritingNull`、`UnsafeRelaxedJsonEscaping`（Base64 可读）。
- `PrimitiveItemDto` 字段名与模型不一致是刻意的序列化兼容（`Thickness`/`VertexThicknesses`），见 [[notes/legacy-thickness-handle]]。

## 踩坑历史

- "内嵌 + 外链双份致丢图"：旧实现同时写 `BackgroundImageData`（内联）与 `BackgroundImagePath`（外部路径），
  且内联以 `File.Exists(外部路径)` 为前提 → 外部原图一删，保存后底图丢失。
  修复 = 字节缓存 + `.symz` 容器（详见 [[notes/symz-container-format]]）。

## 相关页面

- [[notes/symz-container-format]] - 容器格式改造的完整踩坑记录
- [[notes/legacy-thickness-handle]] - DTO 字段名兼容考古
- [[sym-cli]] - CLI 复用同一读写收口