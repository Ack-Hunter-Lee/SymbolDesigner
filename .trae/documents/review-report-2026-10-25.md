# 代码质量审查报告（第三期 · 画图操作专题）

- **日期**：2026-10-25
- **范围**：只覆盖**画图操作**链路 —— 绘制、选择、拖动、命中、手柄、旋转、缩放平移、图层排序、阵列预览、撤销、属性面板调节
- **明确排除**：SVG 导入/导出、XAML 导出、工程序列化格式、`SymbolDesigner.Cli`
- **方法**：detect-stack（WPF/.NET 8 + CommunityToolkit.Mvvm）→ 画图链路静态走查 → 随机抽查 XAML 资源/绑定 → 与第二期报告逐条比对状态

---

## P1 正确性 / 明显体验缺陷

### 1. 旋转后手柄与选中框不跟随（错位）

> **状态：已修复**（2026-10-25）。`Geometry/GeometryMath.cs` 新增统一的旋转正/反变换
> `RotationCenter` / `RotateAbout` / `RotateCanvasPoint` / `UnrotateCanvasPoint` / `LocalToCanvas`
> （绕 `(X+Width/2, Y+Height/2)`，`Rotation==0` 时原样返回，未旋转图元行为与性能不变）。
> 手柄坐标生成全部改走 `LocalToCanvas`：顶点、段中点、带宽 4 端点、圆环 3 手柄、扇形 4 手柄、
> 传送带端点、轮廓角手柄；拖动写回（`UpdateVertexPosition` / `UpdateSegmentPosition` /
> `UpdateRingHandle` / `UpdatePieHandle` / `UpdateBeltHandle` 及 View 端皮带闭合判定）先用
> `UnrotateCanvasPoint` 反变换再改几何。选区包围盒 `GetUnionBounds` 改为先旋转再取 AABB，
> 缩放锚点按图元快照旋转角反变换（`ScaleSnapshot` 增加 `Rotation`/`CenterX`/`CenterY`）。
> `Rotation` 属性变化时刷新全部手柄层与选区框。

图元本体走 `RenderTransform` 旋转，但所有交互装饰仍按**未旋转的 X/Y/Width/Height 包围盒**计算：

- `ViewModels/SymbolDesignerViewModel.Handles.cs` 全文只有一处出现 `Rotation`，且是注释（第 24 行），计算路径无旋转参与
- `ViewModels/SymbolDesignerViewModel.SelectionScale.cs` 无任何 `Rotation` 引用
- 受影响：顶点手柄、轮廓角手柄、缩放手柄、选中框（`SelectionGeometry`）

现象：用属性面板把图元转到 45° 后，扫码框仍是正的、手柄停在原包围盒的角上，拖动手柄的落点与视觉不一致。
建议：把"包围盒 → 旋转后的四角/手柄点"统一收口到一个变换函数（绕中心旋转 + 正变换/反变换成对使用），`HitEntity` 已有反变换可复用。

### 2. 每次编辑提交都全量序列化整个工程（拖动卡顿）

> **状态：已修复**（2026-10-25）。`UndoRedo.cs` 的 `SnapshotPrimitives()` 改为按图元缓存序列化片段：
> `_dtoFragmentCache` + `_dtoVersion`（`DtoFragment.Version`）——只有属性变化过的图元才重跑
> `ToDto` + `JsonSerializer.Serialize`，其余直接拼上次片段；缓存失效挂在既有的
> `OnPrimitiveItemPropertyChanged` 上（与撤销记录同一个信号，不会漏）；图元移除/集合 Reset 时
> 清理缓存项。快照 JSON 结构与 `ProjectSnapshotDto` 一致，撤销/重做反序列化路径不变。

`ViewModels/SymbolDesignerViewModel.UndoRedo.cs:54-72` 的 `SnapshotPrimitives()`：

```csharp
foreach (var p in Primitives) dtoList.Add(ToDto(p));
foreach (var a in ArrayInstances) arrDtoList.Add(ArrayInstanceToDto(a));
foreach (var c in Constraints) conDtoList.Add(ToConstraintDto(c));
return JsonSerializer.Serialize(snap);
```

每次撤销步（含每次拖动结束、每次属性面板调节）都把所有图元 + 阵列 + 约束全量转 DTO 再序列化成 JSON 字符串。图元上百、皮带/阵列多时，单次提交是 O(全部图元) 的分配 + 字符串化，拖动手感会随规模线性变差。
建议：热路径改为"记录受影响图元的浅拷贝"（`PrimitiveItemDto` 只存被改的那几个），或至少对未变图元复用缓存 DTO；阵列/约束按变更集增量记录。

### 3. `SliderSpinBox` 自定义 `IsEnabled` DP 遮蔽基类，禁用态名存实亡

> **状态：已修复**（2026-10-25）。删除 `SliderSpinBox` 里 `new` 出来的 `IsEnabledProperty` 与
> `OnIsEnabledChanged` 回调，直接沿用 `UserControl.IsEnabled` 基类 DP（元数据带 `Inherits`，
> 禁用态自动沿视觉树下传：内部 Slider、两个 RepeatButton、数值文本一并失效）。
> 外层 `Style TargetType=SliderSpinBox` 的 `Setter Property="IsEnabled"` 落到基类 DP，写法无需改动。

`Controls/SliderSpinBox.xaml.cs:76-90` 用 `new` 声明 `IsEnabledProperty`，PropertyChanged 回调只灰化内部 `PART_Slider`，两个 `RepeatButton` 仍可点击。属性面板（`SymbolDesignerView.xaml:4162`）正是靠它表达"锁定/接缝面板不可用"。
现象：面板上看起来变灰，仍能通过 ▲▼ 按钮改动值。
建议：不要新造同名 DP；改为对 `PART_Slider` 与按钮容器分别设置 `IsEnabled`，或直接沿用基类 `IsEnabled`（去掉这个 DP）。

### 4. `CornerRadiusSlider.Value` 直接覆盖 OneWay 绑定

> **状态：已修复**（2026-10-25）。`SyncCornerRadiusSlider` 的 `Value` / `Maximum` / `TickFrequency`
> 三处程序化同步统一改用 `SetCurrentValue(...)`：只改当前有效值、不写入本地值，XAML 上的
> `Mode=OneWay` 绑定保持存活，后续 `CurrentRadius` 变化仍能推动滑块。

`Views/SymbolDesignerView.xaml.cs:1142` 写 `CornerRadiusSlider.Value = v`，而 XAML（约 4159 行）绑定是 `Mode=OneWay`。WPF 的本地值会覆盖绑定 → 首次同步后 `CurrentRadius` 的改动不再驱动滑块。
建议：改 `SetCurrentValue`（保留绑定），或去掉绑定只留单向手工同步。

---

## P2 可维护 / 性能

### 5. `ZoomPanViewport` 每次平移/缩放都新建 `TransformGroup`

`Controls/ZoomPanViewport.cs:186-189` 每次输入都 `new TransformGroup` + 两个新 Transform，再整体替换 `Child.RenderTransform`。画布拖拽是最高频路径，造成每帧分配与整树重绘。
建议：缓存一组实例，只更新 `ScaleX/ScaleY/TranslateX/TranslateY`。

### 6. 事件订阅未退订（视图/VM 不可回收）

- `Views/SymbolDesignerView.xaml.cs:44-50`：三个 `DependencyPropertyDescriptor.AddValueChanged` 无对应 `RemoveValueChanged`
- `Controls/SwatchesPicker.xaml.cs:59-60`：订阅 `RecentColors.CollectionChanged` 无退订
建议：`Unloaded` 里成对退订，或在 VM 销毁时清理。

### 7. 命中测试重复解析 `PathData`

`Views/DraggablePrimitive.cs:300` 每次命中都把路径字符串重新解析成几何（此前二期第 9 条，仍未处理）。高频点击 + 多图元场景下是明显浪费。
建议：按 `(PathData, 版本号)` 缓存在图元上（与 `OutlineGeometry` 缓存同一思路）。

### 8. code-behind 承载绘制规则

- `Views/SymbolDesignerView.xaml.cs:1168`：`vm.SelectedItem.FillStyle = Models.FillStyle.None`（"清填色"规则写在视图）
- `Views/SymbolDesignerView.xaml.cs:507-519`：元素面板拖放行 → `ZIndex`/索引换算
建议：收口为 VM 命令（`ClearFillCommand` / `MovePrimitiveTo`，后者已存在，可复用）。

### 9. 取色器的 DPI 混算与静态缓存无上限

`Controls/ScreenColorPicker.cs:257-269`：
- 物理像素坐标（`GetCursorPos` / `SM_*VIRTUALSCREEN`）与 DIP（`Width`/`Window.Left`）混算，非 100% 缩放下放大镜定位/贴边翻转可能错位（**待实机确认**）
- 每 33ms 调 `ColorUtil.ParseHex`，新 hex 都会写入静态字典 `ColorUtil._solidCache`（只增不减）

建议：坐标统一过 `PresentationSource` 的 DPI 换算；缓存加容量上限或改用弱引用。

---

## 已确认修复 / 需要更正上期结论

- **各手柄层 Z 序已全部修好**：顶点手柄 9999、线段手柄 9998、圆环/扇形手柄 9996、轮廓角手柄 9997、缩放手柄 10000、BeltHandles 9990、框选层 10010。第二期遗留清单里"`RingHandles`/`PieHandles`/`SegmentHandles`/`OutlineCornerHandles`/`ScaleSelectionHandles` 仍是 ZIndex 0"的描述**已过期，不成立**。
- `IsSelected` 变化不再标脏（`Elements.cs`）
- `LineAxis` 加载容错已补（`Persistence.cs`）
- `Debug.WriteLine` 已清空
- `OutlineGeometry` 版本号缓存已生效（第二期第 7 条）
- `BandWidth` 覆盖顶点厚度的问题已修（第二期第 8 条）

## 通过的部分

- 选择语义（Ctrl 累积多选、单选焦点、多选高亮）代码集中，`CanApply*` 与命令可执行态联动完整
- 命中测试已区分"线框只认描边 / 填充认内部"，`StrokeContains` 容差取 `max(容差×2, 线宽)` 合理
- 撤销去抖（350ms）+ "纯 UI 态不标脏" 已避免假撤销步
- XAML 资源键交叉核对：`StaticResource`/`DynamicResource` 引用键与 `x:Key` 定义无缺失；`ElementName` 无悬空
- 画布颜色 token 化：除 `MetalFrameInner`（二期第 4 条，属模板主题）外，视图侧已无硬编码

---

## 建议处理顺序

1. P1-1 旋转后手柄错位（用户可感知，画图核心）
2. P1-4 / P1-3 属性面板两处失效（改法小）
3. P1-2 撤销全量快照（规模相关，改动中等）
4. P2-5 / P2-7（高频路径）
5. 其余按技术债批次
