# 工程文件格式：.symz 压缩包容器（含"内嵌＋外链双份致丢图"踩坑）

## 现象：底图有丢图隐患

导入底图并保存工程后，**删除或移动那张原始图片文件，再次保存工程，底图就没了**。

## 根因：同一份底图存了两处，且互相冲突

改造前 `ToSymbolProject()` 同时写入两个字段：

- `BackgroundImageData` —— 把图片读成 `byte[]` 内联进 JSON（Base64），用于"工程自包含"；
- `BackgroundImagePath` —— 仍写入导入时的**外部绝对路径**。

而内联那一步以 `File.Exists(BackgroundImagePath)` 为前提：外部原图一旦失效，内联分支直接跳过 → 保存出的工程里底图丢失。也就是说"自包含"只是看起来像，实际依赖外部文件存活。

顺带两个代价：Base64 让 JSON 膨胀约 1/3；大图使清单变成巨型字符串，`sym` 每次读取都要解析整包。

## 修复：容器格式 `.symz` + 字节缓存

### 落盘形态（`Models/SymbolProjectFile.cs` 收口，GUI 与 CLI 共用）

```
myproject.symz            ZIP
├── project.json          SymbolProject 清单（与 .sym.json 同 schema）+ formatVersion + assets[]
└── assets/background.png 底图原始字节（不再内联 Base64）
```

`.sym.json` 纯文本单文件形态**保留**（底图内联 Base64），供文本 diff / agent 直接读写；两种格式读取端都能解析（扩展名优先 + 魔数 `PK` 兜底），写入端按目标扩展名决定，**就地保存维持原格式，不擅自升级用户文件**。

### 关键实现点

- **清单模型零破坏**：`BackgroundImageData` / `BackgroundImagePath` / `BackgroundImageType` 老字段原样保留，只新增 `FormatVersion` / `Assets`，旧工程无需迁移即可读。
- **底图改为内存字节缓存**：`SymbolDesignerViewModel` 新增 `_backgroundImageBytes` / `_backgroundImageExt`，导入（`LoadBackgroundImage(path)`）与字节加载（`LoadBackgroundImage(byte[], ext)`）都缓存字节。保存时**直接用缓存字节，不再 `File.Exists` 外部路径** → 从根上消除丢图。
- **字节加载不再落临时文件**：原先 `LoadBackgroundImage(byte[])` 会把字节写临时文件再走路径加载。改为 `BitmapImage.StreamSource = MemoryStream(bytes)` + `CacheOption=OnLoad` + `Freeze()`，零临时文件、无文件锁。
- **容器安全**：资产条目名只接受 `assets/<单文件名>` 形式，拒绝绝对路径、盘符、`..` 与多余层级（防 zip slip）；清单指向缺失条目时跳过而非报错。
- **确定性输出**：条目写入顺序固定、时间戳统一 `1980-01-01`，同内容重复保存产出字节一致的包 → diff 干净、可缓存。
- **格式转换不丢资产**：`.symz` 转存 `.sym.json` 时把资产回填为内联 Base64；CLI 编辑时 assets 原样透传（否则 `sym edit` 一个带底图的 `.symz` 会把资产丢掉）。
- 依赖仅 `System.IO.Compression`（BCL），GUI 与 CLI 均无需新增包。

## 经验

- "内嵌自包含"必须**与任何外部文件解耦**：只要读写路径上还留着 `File.Exists(外部路径)` 这种分支，就不算自包含。判断方法是问"把外部文件全删掉，保存再打开还完整吗"。
- 二进制资产不要塞进 JSON：膨胀 + 清单不可读 + 每次解析都付全量代价。压缩包容器把"可读清单"与"二进制"分开，是更稳的形态。
- 双格式读写务必走**同一个收口类**（本处 `SymbolProjectFile`），否则 GUI 与 CLI 各写一份逻辑必然漂移，且容易在转换/编辑路径上漏掉资产。
- 让容器可确定性产出（固定顺序 + 固定时间戳）成本极低，但对 diff 与缓存收益很大。
