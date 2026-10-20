# .symz 容器工程格式 — 实现计划

## Context（为什么做）

现状是"已内嵌但冗余外链"的混合体，而不是干净的单一事实来源：

- 背景图保存时读成 `byte[]` 写进 `BackgroundImageData`（Base64 内嵌），**同时**仍写入原始外部路径 `BackgroundImagePath`（[Persistence.cs L151-170](file:///f:/Codes/CSharp/SymbolDesigner/ViewModels/SymbolDesignerViewModel.Persistence.cs#L151-L170)）。
- 于是存在真实丢图隐患：外部原图被删/移动后再保存，`File.Exists` 失败 → 底图不再嵌入 → 丢失。
- Base64 内嵌本身代价大：JSON 膨胀约 33%、大图导致解析慢、git diff 全是巨型字符串、CLI 每次读取都要解析整包。

目标：定义 `.symz` 压缩包容器，把"可读清单 JSON"与"二进制资产"分开存放且同包自包含；GUI 与 CLI 双通道读写；旧 `.sym.json` 继续可读、可就地可写。

已确认决策：扩展名 `.symz`；GUI 默认存 `.symz` 并保留"另存 `.sym.json`"；容器内不内联 base64；`assets/` 按可扩展多资产设计；打开旧 `.sym.json` 后就地保存维持原格式（不擅自升级）；CLI `sym new` 默认输出 `.symz`。

## 格式规格 v1

```
myproject.symz            ZIP (Deflate)
├── project.json          SymbolProject 序列化（与 .sym.json 同 schema）＋新增 formatVersion / assets 清单
└── assets/
    └── background.png    底图原始字节
```

`project.json` 新增字段：

- `formatVersion`: int，当前为 `1`
- `assets`: `[{ key, path, mediaType }]`，例如 `{ "key":"background", "path":"assets/background.png", "mediaType":"image/png" }`
- 容器内 `backgroundImageData` 一律为 null（不内联）；`backgroundOpacity` / `backgroundFitMode` 等展示属性沿用老字段

硬约束：

- 入口名固定 `project.json`；资产一律位于 `assets/` 前缀下
- 读包时条目名规范化，拒绝绝对路径与含 `..` 段落的名字，只接受期望前缀
- 确定性：条目写入顺序固定（`project.json` 在前，资产按路径排序），并统一设置固定时间戳（如 1980-01-01），使同内容重复保存产出字节一致的包，便于 diff 与缓存

格式探测（读）：扩展名优先，魔数 `PK\x03\x04` 兜底（防无扩展名或后缀不符）。

## 实现步骤

### 1. 模型 — [Models/SymbolProject.cs](file:///f:/Codes/CSharp/SymbolDesigner/Models/SymbolProject.cs)

- 新增 `FormatVersion`、`Assets`（`List<AssetDto>?`）与 `AssetDto { Key, Path, MediaType }`
- 不动 `BackgroundImageData` / `BackgroundImagePath` / `BackgroundImageType` 老字段，保证 `.sym.json` 完全兼容

### 2. 新增 Models/SymbolProjectFile.cs — 唯一读写收口

- 常量：`ContainerExtension=".symz"`、`LegacyExtension=".sym.json"`、`ProjectEntryName="project.json"`、`AssetsFolder="assets"`、`BackgroundAssetKey="background"`
- `sealed record LoadedProject(SymbolProject Project, IReadOnlyDictionary<string, byte[]> Assets)`
- `Load(string path) → LoadedProject?`：探测格式 → 容器走 `ZipArchive` 读 `project.json` + `assets/*`；纯文本走 `File.ReadAllText` + `SymbolProject.Deserialize`
- `Save(string path, SymbolProject project, IReadOnlyDictionary<string, byte[]>? assets)`：`.symz` → 填 `Assets` 清单、清空 `BackgroundImageData`、把资产写进条目；其它扩展名 → 维持现状纯文本内联
- 复用现有 [SymbolProject.Serialize()/Deserialize()](file:///f:/Codes/CSharp/SymbolDesigner/Models/SymbolProject.cs#L63-L75)，不重造序列化
- 依赖仅 `System.IO.Compression`（BCL，GUI 与 CLI 都可用，无新增包）

### 3. GUI 底图改为内存缓存字节（同时修既有丢图隐患）

- [BackgroundImage.cs](file:///f:/Codes/CSharp/SymbolDesigner/ViewModels/SymbolDesignerViewModel.BackgroundImage.cs)：新增私有字段 `_backgroundImageBytes` / `_backgroundImageExt`
  - `LoadBackgroundImage(path)`：读字节 → 缓存 → 保留现有 `BitmapImage`（`OnLoad` + `Freeze`）解码逻辑
  - `LoadBackgroundImage(byte[], ext)`：缓存字节；改为直接从 `MemoryStream` 解码，去掉临时文件那一跳
  - `ClearBackgroundImage`：一并清缓存
- `ToSymbolProject()`（[Persistence.cs L141-170](file:///f:/Codes/CSharp/SymbolDesigner/ViewModels/SymbolDesignerViewModel.Persistence.cs#L141-L170)）：优先用缓存字节写 `BackgroundImageData`，**不再依赖 `File.Exists(BackgroundImagePath)`** → 彻底消除"外部原图丢失即丢底图"

### 4. GUI 保存 / 加载 — [Persistence.cs](file:///f:/Codes/CSharp/SymbolDesigner/ViewModels/SymbolDesignerViewModel.Persistence.cs)

- `SaveProject()`（L42-60）：按 `CurrentFilePath` 扩展名决定写哪种格式（就地维持原格式）
- `SaveAsProject()`（L65-90）：`DefaultExt=".symz"`；Filter = `元件图形工程 (*.symz)|*.symz|纯文本工程 (*.sym.json)|*.sym.json|所有文件 (*.*)|*.*`
- `LoadProject()`（L95-125）：Filter 同为双格式；改用 `SymbolProjectFile.Load`，再 `FromSymbolProject(project, assets)`
- `FromSymbolProject()`（L289-321）加可选参 `assets`：命中 `background` 资产 → 按字节加载；否则维持现有 `Data → Path` 逻辑
- 取消/失败路径与 `IsDirty`、`ResetUndoRedo` 处理保持不变

### 5. CLI — [SymbolDesigner.Cli/Program.cs](file:///f:/Codes/CSharp/SymbolDesigner/SymbolDesigner.Cli/Program.cs)

- `LoadProject` / `WriteProject`（L290-301）改经 `SymbolProjectFile`：**Load 必须把 assets 一并带出、Write 原样写回**，否则 `sym edit` 一个带底图的 `.symz` 会把资产丢掉
- `DefaultOut`（L6-10）：`project.sym.json` → `project.symz`
- `info` / `validate` / `render` / `edit` / `remove` 仅适配新签名，业务逻辑不变
- [Renderer.cs](file:///f:/Codes/CSharp/SymbolDesigner/SymbolDesigner.Cli/Renderer.cs) 目前只画画布底色、不用底图 → 本轮不改

### 6. 文档与知识沉淀

- [docs/agent-dsl.md](file:///f:/Codes/CSharp/SymbolDesigner/docs/agent-dsl.md)：示例中的 `.sym.json` 改为 `.symz`，并补一句"`--output xxx.sym.json` 可输出纯文本工程"
- 按 AGENTS 知识沉淀原则，在 `wiki/` 记录容器格式规格与本次"内嵌＋外链双份致丢图"的踩坑结论

## 验证

1. 构建：`dotnet build SymbolDesigner.csproj` 与 `SymbolDesigner.Cli` 均须 0 错误（含最终行出现对应 dll 名）
2. GUI 往返：导入底图 → 存 `.symz` → 关闭重开 → 底图 / 图元 / 锁定状态齐全
3. 丢图隐患回归：删除原始外部图片文件后再次保存，底图仍在
4. 包结构核对：用 `[System.IO.Compression.ZipFile]::OpenRead` 列条目，确认 `project.json` + `assets/background.png`，且 `project.json` 中 `backgroundImageData` 为空、`assets` 清单就位
5. 旧格式兼容：分别用内嵌版与"仅外部路径"版旧 `.sym.json` 打开，均正常；就地保存仍为 `.sym.json`
6. CLI 往返：`sym new --dsl sample_dsl.json --output a.symz` → `sym info a.symz` → `sym edit a.symz --id <id> --set x=300` → 再 `sym info` 确认资产未丢 → `sym render a.symz`
7. 确定性：同内容连续保存两次，比对文件 SHA256 一致

## 不在本轮范围

- `sym render` 合成底图（Renderer 仍只看画布底色）
- 多底图 / 自定义图案等资产的实际使用（格式已预留 `assets` 清单，实现留待后续需求）
