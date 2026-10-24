# Wiki Schema（项目知识库规范）

> 本文件是 SymbolDesigner 仓库 `wiki/` 的元规范：目录结构、frontmatter、写作与更新策略。
> 写作规范以本文件为准；涉及用户画像与教学规则时联动 `user-profile.md`。
> 本规范脱胎于个人知识库 `C:/Users/admin/Documents/knowledge/my-wiki-old/SCHEMA.md`，按项目场景裁剪。

## 目录结构

```
wiki/
├── concepts/              # 概念页：架构、机制、算法、设计模式
├── entities/             # 实体页：项目、可执行文件、关键类/组件
├── comparisons/          # 对比页：两个以上方案的取舍分析
├── queries/              # 问题记录页：一次探索/调查的结论
├── notes/                  # 踩坑/考古笔记（保留既有文件）
├── index.md               # 人类导航入口（按主题分类）
├── log.md                 # 操作日志
├── user-profile.md        # 项目相关的用户知识画像与交互偏好
└── SCHEMA.md              # 本文件
```

- **目录 = 人类导航工具**，embedding 搜索 = AI 发现工具，两者各司其职。
- `type: concept / entity / comparison / query` = 页面属性（类型）；目录名 = 主题（导航用）。

## Conventions

- 文件名：小写、中划线、无空格。
- 每个页面以 YAML frontmatter 开头。
- 使用 `[[wikilinks]]` 关联其他页面（兼容 Obsidian）。链接强度：正文核心论述 = strong，
  正文有上下文引用 = medium，仅 Related 列表 = weak（格式 `[[页名]]@weak`）。
- 更新页面必须同步更新 `updated` 日期。
- 新页面必须登记到 `index.md` 对应章节。
- 所有操作必须追加到 `log.md`。
- `log.md` 只保留近期记录，更早按月归档到 `_archive/log/YYYY-MM.md`。

## Frontmatter

```yaml
---
title: Page Title
created: YYYY-MM-DD
updated: YYYY-MM-DD
type: entity | concept | comparison | query
tags: [tag1, tag2]
prerequisites: [前置概念A, 前置概念B]   # 必填，无前置写 []
confidence: high | medium | low          # 可选
sources: [相对路径/源文件]                # 可选，源码探索类页面建议填关键源文件
---
```

### prerequisites（必填）

- 本页理解所需的前置知识，用 wikilink 页面名列表声明；无前置写 `[]`。
- **前置知识必须单页化（硬规则）**：每个前置必须是独立 wiki 页面；缺失时先建精简占位页。
- **来源合法性**：写入 prerequisites 的前置必须可追溯到 —— 已在 `user-profile.md` 登记为已掌握 /
  已有独立页面 / 在本次对话中讨论确认。禁止凭空引入。
- **知识必须有向**：`prerequisites` 是唯一表达教学依赖方向的边；`[[wikilinks]]` 只用于导航。

## Tag Taxonomy

| Tag | 用途 |
|-----|------|
| `wpf` | WPF / XAML / .NET 桌面开发 |
| `geometry` | 几何算法、路径计算 |
| `mvvm` | MVVM / CommunityToolkit |
| `cli` | 命令行工具 |
| `file-format` | 工程文件 / 序列化 |
| `interaction` | 交互、拖拽、状态机 |
| `architecture` | 项目架构、设计决策 |
| `pitfall` | 踩坑、根因分析 |
| `meta` | 元知识（关于知识/规范的知识） |

## Page Thresholds

- **教学即时单页化**：教学中引入的知识链节点概念 → 即时建页（可精简），不等待确认。
- **知识原子性（一页一知识）**：每页只讲一件事；多知识点拆分为多页。
- **创建页面**：某概念/实体出现在 2+ 来源，或在一个来源中是核心主题。
- **追加到现有页面**：来源提到了已覆盖的内容。
- **不创建页面**：一次性提及、次要细节。
- **拆分页面**：超过 ~200 行或含多个独立知识点时拆分。
- **归档页面**：内容完全过时 → 移入 `_archive/`，从 index 移除。

## Update Policy

新信息与现有内容冲突时：检查日期（较新的覆盖较旧的）；真正矛盾时记录双方立场与来源，
在 frontmatter 标记 `contradictions: [page-name]`。

## 项目内引用规范

- 探索结论若来自源码，在正文用相对路径标注关键源文件（如 `Geometry/BeltSolver.cs`），
  方便他人复查；不强制贴大段代码。
- 涉及"颜色硬编码""编译验证"等仓库硬规则时，引用仓库根 `AGENTS.md`，不要复制规则正文。