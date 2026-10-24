---
title: 探索记录：SymbolDesigner 项目全景（2026-11）
created: 2026-11-17
updated: 2026-11-17
type: query
tags: [architecture, meta]
prerequisites: []
confidence: high
---

# 探索记录：SymbolDesigner 项目全景（2026-11）

一次对 SymbolDesigner 仓库的系统性探索，目标是把项目理解沉淀为 wiki 知识。

## 方法

1. 读仓库规则（`AGENTS.md`）、既有 `wiki/notes/` 4 篇笔记；
2. 读核心模型（`PrimitiveItem` / `SymbolProject` / `SymbolProjectFile`）与主 VM；
3. 并行派出 4 个源码探索子代理，分别深挖：
   - 几何管道（BandOutline/布尔/皮带/相切/路径分布）
   - 编辑交互与 ViewModel（绘制/撤销/手柄/约束/选择/格式刷）
   - 阵列 / 图层 / 持久化 / XAML 导出 / SVG 导入 / 背景图
   - CLI 子工程 / 视图 / 控件 / 转换器 / 主题

## 产出页面

- 实体：[[symboldesigner-project]]、[[sym-cli]]
- 架构：[[architecture-overview]]、[[theme-system]]、[[component-template-design]]
- 模型：[[primitive-model]]、[[file-formats]]
- 几何：[[band-outline-pipeline]]、[[boolean-ops]]、[[svg-import]]
- 交互：[[draw-state-machine]]、[[undo-redo]]、[[handles-and-edit-modes]]、[[constraint-system]]、[[array-system]]
- 导出：[[xaml-export]]
- 对比：[[band-polyline-vs-polyline]]
- 既有笔记：[[notes/symz-container-format]]、[[notes/belt-primitive]]、[[notes/legacy-thickness-handle]]、[[notes/wpftmp-cs0579-duplicate-attributes]]

## 核心结论

1. **项目定位**：工业组态"元件设计器"，独立 WinExe 且被 FactoryKanban 通过 ProjectReference 复用。
2. **设计主线**：模型只存编辑数据，几何实时计算不落盘（皮带/布尔最典型）；TypeKey 一维字符串驱动一切分支。
3. **几何特色**：BandOutline 把"骨架 + 逐顶点带宽 + 接缝样式"实时合成轮廓；布尔用 Clipper2 + 弧重拟合 + EvenOdd 多环。
4. **交互特色**：整画布 JSON 快照撤销（350ms 去抖 + 手势合并）；手柄体系直接改写渲染缓存步骤。
5. **工程格式**：`.symz` 容器（确定性输出、自包含资产）与 `.sym.json`（可 diff）双格式，GUI/CLI 同一收口。
6. **约束纪律**：颜色一律走 `Brush.*` token；编译验证必须确认退出码 + dll 名 + 0 错误。

## 相关页面

- [[architecture-overview]] - 从总览开始读
- [[symboldesigner-project]] - 项目实体
- [[user-profile]] - 用户画像与锚点