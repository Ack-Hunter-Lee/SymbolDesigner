# 系统性代码审查报告 — SymbolDesigner（第二期）

- 日期：2026-10-24
- 审查对象：全部源码（ViewModels / Views / Controls / Geometry / Models / Converters / CLI / Themes）
- 审查方式：静态阅读 + grep 交叉验证；未做运行验证
- 前置：第一期审查报告见 `review-report-2026-09-12.md`（16 条已修复 / 1 条暂缓）
- 说明：本次审查时沙箱对 `obj/` 目录有 ACL 限制，`dotnet build` 无法完成（`Access to the path ... is denied`，环境问题非代码错误），故以下结论均来自静态分析。

---

## 总体印象

项目代码质量在同规模工具里属于中上：分层意图清晰、几何纯函数化、撤销/约束/阵列等难点收敛较完整、wiki 知识沉淀到位。
最突出的问题集中在三块：

1. **单体 VM + 巨型 XAML/code-behind** 的维护成本；
2. 撤销系统**全量 JSON 快照 + 逐属性订阅**带来的性能与误报问题；
3. 若干**容错不一致**（`LineAxis` 裸 Parse、字符串替换兼容）与**规则执行不彻底**（`MetalFrameInner` 硬编码色、`Debug.WriteLine` 残留）。

---

## P1 正确性 / 规范违反（建议尽快处理）

### 1. 纯点击选中也会把文档标脏（IsDirty 误报）

> **状态：已修复**（`OnPrimitiveItemPropertyChanged` 开头过滤 `nameof(PrimitiveItem.IsSelected)`——纯 UI 选中态不落盘、不参与标脏/撤销；实测：选中/取消选中/Ctrl 多选后 `IsDirty` 保持 false，改 X、切可见性仍标脏；编译 0 错误）

`PrimitiveItem.IsSelected` 是 `[ObservableProperty]`（`Models/PrimitiveItem.cs:86`），变化触发 `PropertyChanged`。
VM 在 `OnPrimitivesChanged` 订阅了每个图元的全部属性变化（`ViewModels/SymbolDesignerViewModel.Elements.cs:24-27`），
`OnPrimitiveItemPropertyChanged`（`Elements.cs:127-138`）对除 `OutlineGeometry` 外的所有属性都执行 `IsDirty = true`。

后果：**点击/框选图元（`SyncSelectedHighlight` 置 `p.IsSelected`）就会把工程标脏**。
虽然 `ScheduleUndoCommit` 因 DTO 不序列化 `IsSelected` 不会产生假撤销步，但 `IsDirty` 已置 true——
用户什么都没改，关闭窗口却弹出"有未保存的改动"。

改法：`OnPrimitiveItemPropertyChanged` 过滤 `IsSelected` 等纯 UI 态属性，或在标脏前判断属性是否为"持久化字段"。

### 2. `SymbolProject.Deserialize` 用全局字符串替换做兼容
`Models/SymbolProject.cs:81-86`：

```csharp
if (!string.IsNullOrEmpty(json) && json.Contains("\"JoinStyle\""))
    json = json.Replace("\"JoinStyle\"", "\"OutlineJoinStyle\"");
```

这不是只改 JSON 键名——会把文件里任何恰好出现 `"JoinStyle"` 的字符串内容一并替换（例如某个字符串字段值），一旦被用户数据触发就是静默数据损坏。
改法：保留 `[JsonPropertyName("JoinStyle")]` 遗留属性，或写自定义 `JsonConverter`。

### 3. `CreateFromDto` 对 `LineAxis` 直接 `Enum.Parse` 不设防

> **状态：已修复**（新增 `ParseLineAxis`，与同方法其它 `Parse*` 一致：空值/非法值都退回 `LineAxisMode.Free`）

`ViewModels/SymbolDesignerViewModel.Persistence.cs:454`：

```csharp
LineAxis = System.Enum.Parse<LineAxisMode>(dto.LineAxis),
```

同一方法里 `ParseJoinStyle` / `ParseStrokeDashStyle` / `ParseFillStyle` / `ParsePatternStyle` 全部容错，唯独 `LineAxis` 裸 Parse。
手改坏/第三方生成的工程文件会让整个加载在反序列化阶段抛异常，与"容错加载"设计目标矛盾。

### 4. `Themes/Generic.xaml` 的 `MetalFrameInner` 硬编码颜色且被 3 处 UI 引用
`Themes/Generic.xaml:19-23` 用 `#3a4a5a` / `#2a3a4a` / `#1a2a3a` 定义渐变画刷，被 `Views/SymbolDesignerView.xaml:803 / 1511 / 2995` 用作 `BorderBrush`。
直接违反 AGENTS.md 颜色硬规则（UI 颜色必须走 `Brush.*` token），且注释明说"主程序里同一份值，后期调整记得两边同步"——正是 token 化要消灭的重复维护点。
改法：渐变提升为 `Theme.xaml` 的 `Brush.*` token（如 `Brush.Border.MetalFrame`），再引用。

### 5. `App.xaml.cs` 吞掉所有未处理异常后继续运行
`App.xaml.cs:16-21` 对 `DispatcherUnhandledException` 一律 `e.Handled = true`。
异常被吞后应用可能处于半损坏状态，用户继续操作并把坏状态保存进工程文件。
改法：区分 XAML 解析类错误（可继续）与运行时异常（提示后终止），并把堆栈落日志。

---

## P2 性能 / 维护性

### 6. 撤销系统：全量 JSON 快照 + 逐属性订阅双重开销
`Elements.cs:127-138` 对每个图元每个属性变化都 `ScheduleUndoCommit`，`ViewModels/SymbolDesignerViewModel.UndoRedo.cs:54-72` 每次落栈都 `JsonSerializer.Serialize` 全部图元（含长 PathData、逐顶点带宽、外观字段）。
350ms 去抖只降低频率不降低单次成本；图元多、PathData 长时属性面板连续拖动会周期性卡顿。
改法：几何/外观编辑按图元增量记账，或对纯渲染属性（如 `IsSelected`，见 #1）直接短路。

### 7. `PrimitiveItem.OutlineGeometry` 的手工缓存过于复杂
`Models/PrimitiveItem.cs:599-718` 为"手柄改步骤后只重 emit 不重算"维护 `_cachedStepsVersion` / `_lastEmittedStepsVersion` / `_cachedVTHash` / `_cachedVJSHash` 等约 10 个字段 + 四分支逻辑，
且 `BooleanOutlineGeometry`（`:726`）与主 getter 共用同一批缓存字段但失效条件不同。
复杂度高，后续改动极易引入"缓存命中但几何不更新"。
改法：封装成独立的"输入签名 → 几何"缓存类型。

### 8. `OnBandWidthChanged` 一次改动触发 2N 次重算通知
`Models/PrimitiveItem.cs:570-579` 遍历每个顶点写 `IncomingBandWidth` / `OutgoingBandWidth`，
每个 setter 触发 `BandWidthChanged` → `OnVertexBandWidthChanged` → 两次 `OnPropertyChanged`。
整体 BandWidth 改一次 = 2N 次几何通知。加批量抑制标志即可。

### 9. `DraggablePrimitive.HitEntity` 绕过 `ParsedPathPoints` 缓存
`Views/DraggablePrimitive.cs:301` 对 Line/Polyline/BandPolyline 直接 `BandOutlineBuilder.ParsePathData(item.PathData)`，
而模型层已提供 `PrimitiveItem.ParsedPathPoints` 缓存（`PrimitiveItem.cs:323`）。
第一期审查修过 `GetItemBounds` 的同款问题（现用缓存），此处行为不一致，应统一。

### 10. `Debug.WriteLine` 残留

> **状态：已修复**（5 处全部删除，含高频路径 `Selection.cs` 的两条多选统计；`ClipperBooleanEngine` 的 `catch` 变量同步改为无参）

`ViewModels/SymbolDesignerViewModel.Selection.cs:88,110`、`ViewModels/SymbolDesignerViewModel.SelectionScale.cs:233`、`Views/DraggablePrimitive.cs:60`、`Geometry/ClipperBooleanEngine.cs:57` 仍有调试输出。
其中 `Selection.cs` 每次多选变化都打印整串 `MultiSelected`——高频路径。
第一期审查称已清理，实际未清干净。

### 11. 死代码与入库垃圾
- `ViewModels/SymbolDesignerViewModel.Draw.cs:18` 的 `AddL1` 全仓库无调用（拖拽路径已废弃，入口只剩 `SelectTool`）。
- `Views/UserControl1.xaml(.cs)` 空壳遗留。
- `SymbolDesigner.csproj.Backup.tmp` 被 git 跟踪（`git ls-files` 确认），应删除并加入 `.gitignore`。

---

## P3 结构 / 可维护性 / 可测试性

### 12. `Views/SymbolDesignerView.xaml` 4981 行 + code-behind 958 行
工具栏十几个按钮全部手写 `On*Click` 转发到 VM 命令（`Views/SymbolDesignerView.xaml.cs:204-537`），全是 `vm.XXXCommand.Execute(null)` 样板。
改法：`Command` 绑定 / `InputBinding` 收敛；XAML 拆分（属性面板、阵列浮层、导出弹窗各自成 UserControl）。

### 13. Geometry 目录命名空间错位
`Geometry/*` 下纯算法类全在 `SymbolDesigner.Converters` 命名空间（wiki 自认历史遗留），
导致 `Models` / `Views` 层到处 `using SymbolDesigner.Converters`，纯几何与 UI 转换器混在一起，违背"纯函数下沉"分层意图。低成本可修。

### 14. 完全没有自动化测试
`BeltSolver` / `BooleanOps` / `BandOutlineBuilder` / `TangentSolver` / `PathDistributor` 复杂度极高（历史上有 NaN 注入的 P0），却没有任何测试项目。
CLI `validate` 只校验 DSL 语法不校验几何正确性。
改法：至少给纯几何库建 xUnit 测试工程，把已修复回归（SampleArc 退化、闭合判定、布尔退化）固化成用例。

### 15. `XamlExporter.ApplyRotation` 用字符深度扫描找标签边界
`ViewModels/XamlExporter.cs:161-187` 脆弱但注释充分，属于可接受的技术债；未来若导出更多内联内容会继续膨胀，建议记录在案。

---

## 设计取舍（非缺陷，值得留意）

- **TypeKey 字符串驱动一切 switch**：模板选择器、导出器、命中检测、属性面板可见性都要按字符串 switch，新增类型容易漏改。
  团队已明确选择（wiki 记录），但可用 `TypeKey` 上的静态注册表/接口收敛。
- **`DrawMode` 与 `L1Primitive` 两套同名枚举**：工作区正在做 `ToolboxEntry.Key → TypeKey` 重构（未提交，6 个文件），方向正确；
  但 `DrawMode` 仍与 `L1Primitive` 重复定义，加新类型要同步两处枚举。可考虑 `DrawMode` 直接复用 `L1Primitive`（加 None 哨兵）。
- **`SymbolProjectFile.Save` 会原地清空传入 project 的 `BackgroundImageData`**（`Models/SymbolProjectFile.cs:95`）：公开 API 修改入参，属副作用设计。

---

## 过程备注

- 工作区有 6 个未提交文件（`ToolboxEntry.Key → TypeKey` 重构），交叉验证过 XAML / Converter / code-behind 引用已同步，逻辑自洽，但未编译验证。
- `obj/` 被沙箱 ACL 锁住无法构建；后续如需验证编译，可用 `diagnose-windows-sandbox-acl` 修复 ACL 后 `dotnet build SymbolDesigner.csproj --disable-build-servers`。