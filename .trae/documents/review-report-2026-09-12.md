# 系统性代码审查报告 — SymbolDesigner

- 日期：2026-09-12
- 审查对象：全部源码（ViewModels / Views / Controls / Geometry / Models / Converters）
- 流水线：detect-stack → 业务/结构 → WPF 语言/主题 → 几何/通用质量 → 权衡
- 技术栈确认：C# / WPF（.NET8 `net8.0-windows`）+ CommunityToolkit.Mvvm + Nodify
- 原则：只审不改；每条可指认 `文件:行`；P0/P1 为主。
- 处理总览：已修复 12 条（#1/2/3/5/8/10/11/12/13/14/16/17）· 待确认 4 条（#6/7/9/15）· 暂缓不修 1 条（#4）

---

## P0 正确性 / 数据错（建议尽快修）

1. **[PathDataSampler.cs:373-402] 圆弧采样无退化防护 → NaN 注入几何**
   `SampleArc` 当弧终点==起点（`dx=dy=0`）时，`sq` 分母 `rx²y1p²+ry²x1p²=0` → 0/0 得 NaN，`coef`/`cx/cy` 全 NaN，最终写入 PathData，污染包围盒与渲染。
   改法：起点==终点时直接跳过该段；或在进 NaN 前兜底。

   > **状态：已修复**（`SampleArc` 开头增加退化防御：终点==起点（容差 1e-9）时整段忽略，避免 0/0 → NaN 采样点污染几何；SVG 规范视空弧处理）

---

## P1 结构 / 性能 / 规范违反（建议修）

2. **[ZoomPanViewport.cs:195-199] 滚轮双倍缩放**
   Preview 隧道已 `OnWheelZoom` 并 `e.Handled=true`；bubbling 的 `OnMouseWheel` class handler（handledEventsToo）又无条件再 zoom 一次 → 每格约 ×1.15²。表现为缩放过猛。
   改法：`OnMouseWheel` 开头 `if (e.Handled) return;`。

   > **状态：已修复**（commit 70818d1）

3. **[XamlExporter.cs:203] 导出描边色被写死，丢失 StrokeColor**
   `BuildStrokeExpression` 恒返回 `#FF5a7a8c`，注释误称"暂无 StrokeColor"；实际 [PrimitiveItem.cs:159/195] 已有 `StrokeColor`/`StrokeBrush`。用户改的描边色导出后丢失。
   改法：用 `p.StrokeColor` 走 `ColorUtil` 合成。

   > **状态：已修复**（commit 6084ab5）

4. **[SvgParser.cs:244] fill 缺失/为空误判为背景 → 可能误删形状**
   `IsLikelyBackgroundRect` 对 `fill` 为 null/空 直接 `return true`，覆盖 viewBox≥85% 的 `fill="none"` 底/轮廓会被当背景删除（数据丢失）。
   改法：仅当 fill 为透明时才判背景，或填充非空或存在描边时放行。

   > **状态：暂缓不修**（触发苛刻：≥85% + fill=none + 带描边，源以图标为主）

5. **[Constraints.cs:263-283/294] 约束求解每帧全图元扫描 + 精确浮点比较**
   拖动 delta：`Primitives.Where(全图元)` + `maxIter=10` 每次重分配；`GeometryUnchanged` 用 `==` 精确比较，振荡时必跑满 10 次。
   改法：只遍历相关约束集，比较用容差。

   > **状态：已修复**（commit e4c7845，含重合约束"线消失"根因修复）

6. **[UndoRedo.cs:54/67] 约束未纳入撤销快照**
   快照只存 `Primitives+ArrayInstances`，`Constraints`（相切/重合）不参与 → 新增约束不可撤销、撤销后旧约束实体残留继续求解。
   改法：快照 DTO 增 `Constraints`，恢复时重建。

   > **状态：待确认（未处理）**

7. **[AGENTS.md 颜色硬规则违反] UI 层大量硬编码色**
   `SymbolDesignerView.xaml` 约 173 处硬编码 hex/具名色（多数已有对应 `Brush.*` token：`:199 #2A2A2A→Brush.Bg.Canvas`、`:660 #141420→Brush.Bg.Deep`、`:418 #4FC3F7→Brush.State.Accent`、手柄/白线等）。Converters 亦违规：`PickCenterBrushConverter.cs:12`（橙/绿）、`TagToBrushesConverter.cs:76`（默认深蓝）。
   影响：全局换肤失效、与"先改 token 再接引用"约定相悖。
   改法：增量替换，优先覆盖面大的 `Brush.State.*/Brush.Brand.Stroke/Brush.Text.*`。

   > **状态：待确认（未处理）**

8. **[SliderSpinBox.xaml:32 / .cs:104] 数值读数不随 Value 刷新**
   `TextBlock` 绑 `DisplayText`，但它是普通 CLR 属性，控件未实现 INPC → 拖动/点按钮时读数停留在初始值（功能缺陷）。
   改法：`DisplayText` 改 DP，或绑 `Value`+`StringFormat`。

   > **状态：已修复**（DisplayText 改 DP + Value/StringFormat 变更回调刷新）

9. **[Editing.cs:18] 上帝方法 / 巨型 partial 状态内聚**
   `OnSelectedItemChanged`（~130 行）同时管阵列源编辑、多选、绘制模式联动、事件订阅、UI 按钮态；25 个 partial 堆在同一 VM 上并通过兄弟字段（`_sourceAdjTemp` 等）强耦合。
   改法：抽 `SelectionCoordinator`/`ArrayEditService` 组合注入。单点性能：`Elements.cs:118` 每次属性变化全量 JSON 快照（350ms 去抖仍成本高），可手势包裹收口时快照。

   > **状态：待确认（未处理）**

10. **[SelectionScale.cs:233] 每帧重解析 PathData，未用缓存**
    `GetItemBounds` 直接 `ParsePathData` 每帧 Split；同文件 `:160 GetUnionBounds` 已复用 `PrimitiveItem.ParsedPathPoints`。两处行为不一致。
    改法：统一走 `PrimitiveItem.ParsedPathPoints`。

    > **状态：已修复**（commit ea8165e）

11. **[ClipperBooleanEngine.cs:96-100] 宽泛 catch 吞异常**
    `catch{}` 吞掉所有异常（含 OOM/算法 bug）静默回落 Geometry.Combine，掩盖 Clipper 侧缺陷。
    改法：至少记录一条日志/`Debug.Fail`。

    > **状态：已修复**（commit 7953784）

---

## P2 可读性 / 可选优化

> **状态：以下 15 待确认（未处理）**

12. 语义重复且微差：半角圆角公式三处（`BooleanOps.cs:401`、`BandOutlineBuilder.cs:581`、`MiterPoint:642`）；闭合标志用字符串 `pd[^1]=='Z'`（`SelectionScale.cs:323/547`）而 `PrimitiveItem.IsClosed` 已存在——双套真相易失配。

    > **状态：已修复**（新增 `BandOutlineBuilder.SinHalfAngle` / `IsPathDataClosed` 共享方法，三处半角公式与两处闭合判定统一调用；`IsPathDataClosed` 语义为"PathData 几何闭合"，不等同 `IsClosed` 用户属性）
13. 精度魔法数散布不统一：`MergeTol=0.5`/`ArcFitTol=0.35`/`CollinearTol=0.02`/`CoordEps=1e-3`/`SliverAreaTol=1e-3`，建议集中为常量。

    > **状态：已修复**（集中到 `GeometryMath` 5 个 public const 并保留原语义注释；`BooleanOps` / `ClipperBooleanEngine` 删除本地定义改引用；Clipper 两处裸 `1e-3` 量零统一走 `CoordEps`）
14. `Constraints.cs`、`BandOutlineBuilder.cs` 遗留大量 `Debug.WriteLine`（含插值），发布前应清理。

    > **状态：已修复**（`Constraints.cs` 删除全部 17 处 `Debug.WriteLine`；`BandOutlineBuilder.cs` 清理 2 处注释残留；无 `[DEBUG-REMOVE]` 日志遗留）
15. `TagToBrushesConverter`/`PickCenterBrushConverter` 每次求值 `new SolidColorBrush`（未缓存/未冻结），工具栏多按钮时略浪费。
16. `PrimitiveTemplateSelector.cs:41` 未知 `TypeKey` 返回 null → 图元静默不可见，建议给兜底模板。

    > **状态：已修复**（新增 `L1DefaultNode` 兜底模板：占位框 + TypeKey 文本，全部走 Brush token；Selector 未知分支返回 `L1Default`）
17. 仓库 `obj/` 囤了一次性调试工程 `pietest/`、`ArcFitTest/`（gitignored，不影响产物）——可选删除整理。

    > **状态：已修复**（`obj/pietest`、`obj/ArcFitTest` 已删除，主工程编译 0 错误）

---

## 通过的部分
- 绘制状态机（IsDrawing/DraftPoints/DrawMode）复位路径完整；Undo 栈去抖与手势收口配对正确。
- 未发现后台线程改 UI / async void / Dispatcher 竞态。
- 串行 PathData 解析已由 `PrimitiveItem` 缓存兜底（拖动不整串重组）。
- `PieGeometryBuilder`、`RingGeometryBuilder`、`PathDistributor`、`BandOutlineBuilder` 逻辑自洽。
- 无确定的"崩溃/整库数据丢失"级 P0 路径；上文 P0 为边界退化输入触发。

---

## 备注
- 报告外新增改动：顶部栏 Ribbon 页签化重构（commit 7ca2cc8，UI 布局，非本报告条目）。