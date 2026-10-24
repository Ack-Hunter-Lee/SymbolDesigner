---
title: sym（SymbolDesigner.Cli）
created: 2026-11-17
updated: 2026-11-17
type: entity
tags: [cli, wpf, file-format]
prerequisites: [symboldesigner-project]
confidence: high
---

# sym（SymbolDesigner.Cli）

无头命令行工具（AssemblyName = `sym`），复用主工程 DLL 做工程创建/编辑/校验/渲染。

## 命令与退出码

- 命令：`new`（可空工程或 `--dsl`）、`add`、`info`、`validate`、`render`、`edit`、`remove`、`help`。
- 退出码：0 成功 / 1 参数错 / 2 校验失败 / 3 IO 错 / 10 未捕获异常。
- `--json` 输出 stdout，人类日志走 stderr；`--dsl -` 从 stdin 读。
- 工程默认 `.symz`，`--output` 可指定 `.sym.json`。

## DSL JSON

`sample_dsl.json` 定义 `DslProject`（canvas 三项 + primitives 数组）；`DslPrimitive` 字段全部可空，映射层按"设计默认"补齐（rect 默认 40、ellipse 默认 24）。
路径类：points 或 line（x1,y1,x2,y2），先求 bbox 平移到非负局部坐标。
`Validate` 检查 hex 正则、belt/line points 恰 2 个、圆心定位三字段齐全；**BooleanPath 禁止 CLI 创建**；`thickpolyline` 老名自动映射 `bandPolyline`。

## render 离屏渲染

- STA 线程 + `DrawingVisual.RenderOpen` + `RenderTargetBitmap` + `PngBitmapEncoder`，不启动 Application/Window。
- fit 等比留边；底色取 DTO，缺省 `#FF1B1B1B`（画作内容豁免）。
- 每个图元 `PushOpacity + TranslateTransform` 后 `DrawGeometry`；Ring 用外顺内逆 Nonzero 单 StreamGeometry 形成空洞。

## 与 GUI 的关系

- **序列化收口**：读写复用 `SymbolProjectFile`（.symz/.sym.json），避免 GUI/CLI 漂移。
- **皮带**：几何不落盘，`BeltDtoGeometry` 按 GUI `TangentSolver.TryGetCircle` 语义在 DTO 上重建（圆类=Ellipse/Ring、r=min(w,h)/2、Ring 扣 `RingStrokeHalfThickness=0.4`），与 GUI 常量对齐；创建/追加后 `AutoEngageAll` 求解并写回拓扑与包围盒。
- **csproj 陷阱**：用 `ProjectReference` 引用 WPF WinExe 会触发 wpftmp 临时工程导致 AssemblyInfo 重复（见 [[notes/wpftmp-cs0579-duplicate-attributes]]），故改用 `Reference HintPath` 到已编译 DLL + `Microsoft.WindowsDesktop.App.WPF` FrameworkReference（仅 render 需要 WPF 类型）。

## 相关页面

- [[notes/wpftmp-cs0579-duplicate-attributes]] - CLI 子工程的构建踩坑
- [[file-formats]] - 双格式读写收口
- [[theme-system]] - CLI 渲染的颜色豁免