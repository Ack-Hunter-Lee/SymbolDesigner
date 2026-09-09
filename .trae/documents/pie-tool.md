# 新增 Pie（扇形）绘制工具

## Context
用户需要用 CAD 般的操作绘制"扇形"（圆心 + 半径 + 起始角 + 扫掠角闭合的填充满形状）。
当前软件只有矩形/椭圆/三角形/圆环/直线/折线等 [L1Primitive.cs](file:///F:/Codes/CSharp/SymbolDesigner/Models/L1Primitive.cs#L8-L45)，没有 Pie/Arc。本计划新增一个 **Pie 图元**，完全照抄 Ring 的端到端接入路线（工具栏→两段式绘制→几何→模板→属性面板→导出→布尔）。

已与用户确认的交互设计：
- **绘制**：两次点击，第一次定圆心、第二次定半径（半径点语义，同 Ellipse/Ring 的正圆逻辑）；成型后使用默认扫掠角，后续用属性面板调整。
- **角度调整（首版）**：仅属性面板提供"起始角"/"扫掠角"滑块（`SliderSpinBox`）。
- **默认扫掠角**：60°。

几何约定：起始角 0° = 3 点钟方向（东），正角度为逆时针（数学角，y 向下）；扇面自动闭合（弧 + 两条半径 + 闭合）。

## 改动清单

### 1. Models\L1Primitive.cs
- `L1Primitive` 枚举（末端）新增 `Pie`。
- `DrawMode` 枚举新增 `Pie`（两段式绘制模式，同 Ellipse/Ring）。

### 2. ViewModels\SymbolDesignerViewModel.Tools.cs
- `DrawingTools` 列表加 `new ToolboxEntry("L1_Pie", "扇形", "◔")`（图标可用现成文本字符）。
- `SelectTool` 的 key→DrawMode switch 加 `"L1_Pie" => DrawMode.Pie`。

### 3. ViewModels\SymbolDesignerViewModel.Draw.cs
- `AddL1` 默认尺寸 switch：`L1Primitive.Pie => (40.0, 40.0)`。
- `IsShapeDrawMode` 加入 `or DrawMode.Pie`。
- 三处 `isCircleLike = DrawMode is Ellipse or Ring` 都改为加 `or Pie`（第 70、150、308、567 行附近）——Pie 是"半径点 → 正圆周长"语义。
- `EndDraw` 形状提交 switch 加 `DrawMode.Pie => L1Primitive.Pie`；创建后设 `item.SweepAngle = 60`（默认），`StartAngle = 0`。
- `UpdateShapePreview` 加 `case DrawMode.Pie:`，生成扇面预览 PathData（圆心=center、半径=halfR、默认起止角），让用户第二次点击前能看到 60° 扇面。

### 4. Geometry\PieGeometryBuilder.cs（新增）
共享路径生成器：`BuildPath(cx, cy, r, startDeg, sweepDeg)` 返回 Path mini-language 字符串 `M S A r,r 0 large,sweep E L C Z`。
- `S = C + r·(cos a0, sin a0)`，`E = C + r·(cos a1, sin a1)`，`a1 = start + sweep`。
- `sweep` 恒定 1（y 向下顺时针=正角度方向）；`large = |sweepDeg| > 180`。
- `sweepDeg` clamp 到 `(0, 360)` 防整圆退化。
- 运行时几何（PrimitiveItem）与导出（XamlExporter）复用此字符串，避免重复实现。

### 5. Models\PrimitiveItem.cs
- 新增 `[ObservableProperty] private double _startAngle;` 与 `[ObservableProperty] private double _sweepAngle = 60;`（默认 60°，与绘制默认一致）。
- 新增 `PieGeometry` 属性：`Geometry.Parse(PieGeometryBuilder.BuildPath(w/2, h/2, min(w,h)/2, StartAngle, SweepAngle))`，需带缓存或直接每次重建（改动频率低，可用 `OnStartAngleChanged`/`OnSweepAngleChanged` 触发 `OnPropertyChanged(nameof(PieGeometry))` 与 `nameof(SelectionGeometry)`）。
- `SelectionGeometry` switch（第 866 行附近）加 `nameof(L1Primitive.Pie) => PieGeometry`。
- `CanvasFillGeometry` switch（第 767 行附近）加 `nameof(L1Primitive.Pie) => PieGeometry`（闭合有面积 → 可填充/可布尔）。
- `IsFillable` 不需要改（Pie 默认即允许填充）。

### 6. Views\SymbolDesignerView.xaml
- **工具栏预览**：在 L119-L201 附近加 `L1PiePreview` DataTemplate（仿 `L1RingPreview`，用一个 `<Path Data="M 50,50 L 50,10 走势弧靠内"/>` 或最小扇面 Path 表示）。
- **画布模板**：加 `x:Key="L1PieNode"`（仿 `L1RingNode`，[SymbolDesignerView.xaml:520](file:///F:/Codes/CSharp/SymbolDesigner/Views/SymbolDesignerView.xaml#L520-L557)）：`DraggablePrimitive` 内一个 `<Path Data="{Binding PieGeometry}" Stretch="None" Style="{StaticResource CadShapeSelected}" />`。**不得硬编码颜色**——沿用 `CadShapeSelected` 样式（走 Brush 资源）；若加圆心十字标注，用 `Brush.Overlay.*` token。
- **属性面板**：在几何/尺寸分区（仿 Ring 的 InnerRadius 行，[SymbolDesignerView.xaml:3566](file:///F:/Codes/CSharp/SymbolDesigner/Views/SymbolDesignerView.xaml#L3566)）加两个 `controls:SliderSpinBox`：`起始角`绑定 `StartAngle`、`扫掠角`绑定 `SweepAngle`，并使用与 Ring 相同的 TypeKey 可见性触发（`TypeKey == Pie` 时显示）。

### 7. ViewModels\XamlExporter.cs
- `ExportOne` 的 type-key switch（第 107 行附近）加 `"Pie" => PieElement(p, left, top, w, h, z, ctx)`。
- 新增 `PieElement`：输出 `<Path Canvas.Left="…" Canvas.Top="…" Data="PieGeometryBuilder.BuildPath(局部 w/2,h/2…)" Fill=… Stroke=… StrokeThickness=… />`。Data 用**局部坐标**（`Viewbox/PathData` 内），与其它元素坐标归一化约定一致。

## 复用与一致性
- 全程复用对象：`ToolboxEntry`/`PrimitiveItem.Create`/`CadShapeSelected` 样式/`SliderSpinBox`/`DraggablePrimitive` 两段式绘制状态机。
- 颜色 100% 走 Theme.xaml 的 `Brush.*` token（AGENTS 硬规则），不在新模板写裸色值。
- 命名与文档风格跟随既有 Ring/Triangle。

## 验证
1. `dotnet build SymbolDesigner/SymbolDesigner.csproj --disable-build-servers` 且确认输出含 `SymbolDesigner -> …SymbolDesigner.dll`、`0 个错误`。
2. 手工验证：
   - 工具栏点"扇形"→ 画布两次点击（中心+半径）→ 生成 60° 扇面，二次点击前有扇面预览。
   - 拖动扇面整体移动 / 四角缩放手柄缩放，几何正常。
   - 属性面板调起始角（0-360）与扫掠角 → 画布扇面实时变化。
   - 导出 XAML → 结果为 `<Path Data="M … A … L … Z" …>`，在浏览器/预览里能看到正确扇面且两条直线都贴圆心。
   - 用"合并"把 Pie 与一个矩形求并 → 有面积、能出结果，源被快照移除。
3. 无控键回归：既有 Ellipse/Ring/Triangle 仍需正常绘制（isCircleLike 改动未破坏其语义）。