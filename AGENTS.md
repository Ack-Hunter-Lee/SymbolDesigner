# Agent Rules

> 给 AI agent 看的项目级规则。Trae / Cursor / Copilot 等会读取此文件。
> 所有规则对当前项目全局生效，无需在对话中重复强调。

## 项目事实（常驻上下文）

- **`References/nodify/` 目录包含 Nodify 7.3 完整源码**（官方仓库拷贝：`Nodify/Containers`、`Connectors`、`Editor`、`Themes` 等）。
  - 涉及 Nodify 行为/机制的问题（拖动、Anchor 回写、连接状态机、PendingConnection 等）**优先直接读源码**，不要靠猜或外部网络搜索
  - 例如：拖动策略在 `Editor/NodifyEditor.Dragging.cs`（`DraggingSimple`/`DraggingOptimized`）、Anchor 更新在 `Connectors/Connector.cs`、待连预览在 `Connectors/PendingConnection.cs`、默认主题模板在 `Themes/Styles/*.xaml`

## 组态元素模板设计规范（概述）

生成 / 修改画布组态元素模板（`Controls/Symbols/SymbolTemplates.xaml` 的 DataTemplate）**必须遵守**以下规则，详情见 wiki `concepts/component-template-design.md`：

- **图形撑满节点边界框**：`Viewbox Stretch=Uniform` + 对齐 `Stretch`（不是 Center）；**禁止多层居中容器嵌套**（外层撑满、内层居中放大后仍留白）
- **包围盒占满内容区**：Path 坐标贴近内容边界（边距 ≤ 2~3px），不留大片无效空白
- **两类伸缩策略**：等比例图形 → 固定内容 + Viewbox 撑满；横向可拉长设备 → 内容宽随节点纵横比动态算（`AspectWidthConverter`）+ 元素横向拉伸/外移 + **垂直铺满**
- **描边随 Viewbox 放大变粗**（放 N 倍线宽 ×N），按目标尺寸预估并调细基准描边
- **生成后跑检查清单**（拖入画布目视四周无空白、拉宽不变形、描边协调、无端口组件边界贴合）

## 颜色统一样式（强制）

**任何 XAML / C# 代码中禁止硬编码颜色值（`#RRGGBB`、`#AARRGGBB`、`Red`/`White`/`Transparent` 等字面量、`Colors.White` 等），颜色一律通过 `SymbolDesigner/Themes/Theme.xaml` 中预定义的 `Brush.*` 资源引用**。这是硬性规则，违反即视为 bug。

原则：
- 颜色改动 → 先改 Theme.xaml 的 `Brush.*` token，再让 UI 引用 token，不直接改控件局部颜色，保证全局统一。
- UI 元素用 `{StaticResource Brush.Bg.xxx}`、`{StaticResource Brush.Text.Primary}`、`{StaticResource Brush.Border.Subtle}` 等；C# 动态画刷用 `App.Current.FindResource("Brush.xxx")` 或绑定触发。
- 工具栏图标 / 绘制线条等"前景色"，指向 `Brush.Tool.*` / `Brush.Brand.*` 对应 token（如白色图标），不得写 `Foreground="White"`。
- 例外：仅 `Objective DataTemplate` 导出（XamlExporter 序列化到客户工程）属"客户资产"，允许保留硬编码颜色，不被本规则约束。
- Theme.xaml 的 token 分 8 组：`Brush.Bg.*`(背景/画布)、`Brush.Border.*`(边框)、`Brush.Text.*`(文本)、`Brush.State.*`(状态色)、`Brush.Port.*`(端口)、`Brush.Brand.*`(品牌/绘制线条)、`Brush.Overlay.*`(浮层)、`Brush.Tool.*`(工具栏)。
- 新增颜色必须先考虑复用已有 token；确实需要新色时，先加 Token 再接引用，不得 `<Setter>` / `<TextBlock>` 里写裸色值。

## 文件操作

- **重命名文件时先复制再删除**，不允许直接删除。
  - 保证 git 历史可追溯、可回滚
  - 跨多个文件的重命名，先列出所有引用位置再统一操作
- **不许使用命令行做文本替换**，更不允许写脚本替换。所有文本修改走 Read/Edit 工具按内容上下文编辑。
- 大型删除 / 重构前先列出变更清单，等待用户确认

## 知识沉淀

- **主动记录原则**：发现以下信息时立即写入 `wiki/`，无需等待用户明确要求
  - 非显而易见的发现（验证过的库行为、边界情况、源码阅读结果）
  - 踩坑根因（导致崩溃 / 异常 / bug 的解决方案）
  - 架构决策（关键技术选型、替代方案取舍）
  - 验证过的经验（多轮试错后确认的有效实现）
- 写作规范按 `wiki/SCHEMA.md` 执行

## 编译

仓库包含多个独立项目，**编译目标按改动范围选择**；**报告"编译通过"前必须确认三点**（缺一不可）：

1. 退出码 = 0（不要被"只有警告就当成功"误导）
2. 编译器最终行包含**被验证项目**的 dll 名（`FactoryKanban -> ...FactoryKanban.dll` 或 `SymbolDesigner -> ...SymbolDesigner.dll`，证明该项目确实编译完成）
3. 错误计数为 `0 个错误`（不是只看警告）

- **SymbolDesigner/（元件设计器，独立 WinExe，只依赖 CommunityToolkit.Mvvm）**：
  - 改 SymbolDesigner 代码（布尔运算 / 轮廓手柄 / 属性面板 / Theme 等）→ **必须直接构建**：`dotnet build SymbolDesigner/SymbolDesigner.csproj`
  - 警告：`FactoryKanban.csproj` 通过 ProjectReference 引用 SymbolDesigner，但 `--no-dependencies` 会跳过**所有** ProjectReference。只 build `FactoryKanban.csproj --no-dependencies` **不会重新编译 SymbolDesigner**——用它来验证 SymbolDesigner 的改动，看到"编译通过"是假象。
- **FactoryKanban/（主看板程序，ProjectReference 引用 PlcDataBlock 与 SymbolDesigner）**：
  - 编译统一加 `--no-dependencies`，跳过 PlcDataBlock 依赖的重建：`dotnet build FactoryKanban/FactoryKanban.csproj --no-dependencies`
  - 原因：`F:\Codes\CSharp\PlcDataBlock\PlcDataBlock\obj\Debug\net8.0\PlcDataBlock.dll` 在沙箱/VS 场景下常被前一次编译实例持有写权限被拒（`CS2012: Access to the path ... is denied`），导致构建在 PlcDataBlock 阶段失败 → FactoryKanban 根本不编译 → 看似"无错误"的假象。
- 沙箱/IDE 下若 `obj/` 文件报 `Access to the path ... is denied`（MSBuild 服务节点持锁），构建命令加 `--disable-build-servers` 重试。

## 沟通

- 中文优先（跟随用户语言）
- 简短直接，避免冗余
- 主动汇报状态进度（编译/运行/验证）

## 终端命令逐条汇报

**核心原则**：只有"申请权限"时才停下来解释。普通操作直接执行，不要打断工作流。

### 必须停下汇报 + 等用户确认的场景（高风险操作）

- 删除文件（rm / del / DeleteFile）
- 推送到远程（git push）
- 强制覆盖（git push -f / git reset --hard）
- 安装/卸载依赖（npm install / dotnet add package 等）
- 修改系统配置 / 环境变量
- 修改全局 IDE 规则文件（如 `~/.trae-cn/` 下的文件）
- 终止运行中的进程
- 网络请求（WebFetch / WebSearch）

汇报格式：

> 我准备执行这一步：
> ```
> <命令>
> ```
> **做什么**：...
> **为什么需要**：...

### 直接执行、不汇报、不打断的场景（日常工作流）

- 读文件（Read / Glob / Grep / SearchCodebase）
- 编辑/创建文件（Edit / Write）
- 本地构建（dotnet build / npm run build）
- 本地 git 操作（status / log / diff / add / commit / branch / checkout 等非破坏性命令）
- 运行程序（dotnet run）—— 仅本地测试

### 简短说明的场景（Edit/Write 前）

虽然 Edit/Write 不需要"等你确认"，但仍应在执行前用 1-2 句简短说明改动意图（例如"修改第 X 行的文案"），方便用户审查改动方向，避免意外修改。
**说明后立即执行，不暂停。**

### 关键错误教训

- 把每个命令都设 `requires_approval=true` 并问"确认执行吗"——打断工作流（正确：仅对真正危险的操作要求权限）
- 要求权限时只贴命令不解释——用户不知道命令在做什么（正确：要求权限时必须说明做什么 + 为什么需要）
- 用 PowerShell 多行 HEREDOC 传 commit message 时被 `-` 误导解析失败（正确：单行 `-m` 或 `-F 文件`）
- **PowerShell 下用 `git commit -m "中文"` 会把中文编码成 `\u975e\u8f6c...` 字面量落到 commit message 里**（看似 PowerShell 解析成功，git 也"成功"，但 `git log` 看到的是转义字符串而非中文）。**正确做法**：先把 message 写到文件，再用 `-F 文件` 喂给 git（`Write` 工具→`git commit --amend -F msg.txt`）。这条规则覆盖所有 `-m` / `--message` 传中文场景，包括 `git commit`、`git commit --amend`、`git tag -m` 等
