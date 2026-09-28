# Agent 使用指南：用 `sym` CLI 画图

> 面向外部 AI-Agent 的 DSL 语法与操作手册。
> 你（Agent）通过生成一份 **DSL JSON** 描述"要画什么"，用 `sym` 命令把它转成可打开的工程并渲染 PNG 做**视觉自检**。

---

## 0. 最小工作流（先记这 4 步）

```text
1. 想清楚画面    ← 确定画布尺寸、需要哪些形状、各放在哪、什么颜色
2. sym validate  ← 先用校验器检查你的 DSL 语法（错误会精确告诉你哪里不对、合法值是什么）
3. sym new/add   ← 校验通过后生成 .sym.json 工程
4. sym render    ← 把它渲染成 PNG，**打开图检查画面是否符合你的意图**
```

> **关键原则：DSL 不是"一次写对考试"，而是"validate 纠错 + render 看图"的循环。** 语法错了看报错改，画面不对看 PNG 改坐标。不要手工推算坐标重叠/越界，交给 render 出来的图去判断。

---

## 1. 命令清单

| 命令 | 作用 | 示例 |
|---|---|---|
| `sym new [--dsl f] [--canvas WxH] [--output p]` | 新建工程（带 DSL 则为全量内容） | `sym new --dsl dsl.json --output a.sym.json` |
| `sym add <proj> --dsl <f>` | 向已有工程追加图元 | `sym add a.sym.json --dsl more.json` |
| `sym info <proj>` | 查看工程画布尺寸、图元清单 | `sym info a.sym.json` |
| `sym validate [--dsl f] [<proj>]` | 校验 DSL 或工程合法性 | `sym validate --dsl dsl.json` |
| `sym render <proj> [--output png] [--size WxH]` | 离屏渲染 PNG 预览 | `sym render a.sym.json --output a.png --size 1280x720` |
| `sym help` | 帮助 | `sym help` |

**通用选项**
- `--json`：结果以机器可读 JSON 输出到 stdout（人类日志走 stderr）。Agent 工具调用建议加此项以解析结构化结果。
- `--dsl -`：DSL 从 stdin 读取（管道流水），供需要时内联透传。
- 退出码：`0` 成功｜`1` 参数错｜`2` 校验失败｜`3` IO 错｜`10` 未捕获异常。

---

## 2. DSL 语法

DSL 是一个 JSON 对象，只有两个部分：**画布设置** 和 **图元数组**。

```json
{
  "canvasWidth": 1600,
  "canvasHeight": 900,
  "canvasBackgroundColor": "#23232B",
  "primitives": [ { ...图元... }, { ...图元... } ]
}
```

| 顶层字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `canvasWidth` | number | 否（默认 1920） | 画布逻辑宽 |
| `canvasHeight` | number | 否（默认 1080） | 画布逻辑高 |
| `canvasBackgroundColor` | hex | 否 | 画布底色，`#RRGGBB`/`#AARRGGBB` |
| `primitives` | 数组 | 是 | 图元列表，按数组顺序自增 ZIndex（后画的在上层） |

> 坐标系：`X,Y` 是**左上角**，向右为 `+X`、向下为 `+Y`。同 SVG/CAD 习惯。

---

## 3. 图元字段总表

所有字段**可省略**，省略的由程序按类型设计默认补齐。`type` 是唯一硬性必填。

| 字段 | 类型 | 适用于 | 说明 |
|---|---|---|---|
| `type` | string | 全部 | **必填**。合法值见 §4 类型表 |
| `id` | string | 全部 | 可选，唯一标识；缺省自动生成 |
| `x`, `y` | number | 矩形类 | 左上角坐标 |
| `width`, `height` | number | 矩形类 | 宽高 |
| `centerX`, `centerY`, `radius` | number | 椭圆/圆环/扇形 | 圆心便捷定位；**三个必须一起给**，给了则忽略 x/y/w/h |
| `points` | `[[x,y],...]` | line/polyline/bandPolyline | 折线节点数组 |
| `x1`,`y1`,`x2`,`y2` | number | line | line 的另一写法（起止点），与 points 二选一 |
| `cornerRadius` | number | roundedRect | 圆角半径 |
| `innerRadius` | number | ring | 内圆半径（< 外径一半） |
| `startAngle`, `sweepAngle` | number | pie | 扇形起始角 / 张角（度，起始角 0=东 3 点钟、顺时针为正） |
| `closed` | bool | polyline/bandPolyline | 是否闭合为可填多边形 |
| `thickness` | number | line/bandPolyline | 线条粗细 / 带宽（bandPolyline 生效） |
| `strokeColor` | hex | 全部 | 描边色（默认 `#FFFFFF`） |
| `strokeWidth` | number | 全部 | 描边粗细（默认 0.8） |
| `strokeDash` | enum | 全部 | `Solid`/`Dash`/`Dot`/`DashDot` |
| `fillStyle` | enum | 全部 | `None`/`Solid`/`Gradient`/`Pattern`（默认 None） |
| `fillColor` | hex | Solid/Gradient 用 | 单色/渐变起始色 |
| `gradientStart`, `gradientEnd` | hex | Gradient 用 | 线性渐变起止色 |
| `fillBrightness` | number | 填充 | 亮度 0~100（默认 100） |
| `fillOpacity` | number | 填充 | 透明度 0~100（默认 100） |
| `patternStyle` | enum | Pattern 用 | `Diagonal`/`Horizontal`/`Vertical`/`Cross` |
| `rotation` | number | 全部 | 旋转角（度） |
| `zIndex` | int | 全部 | 层叠，大在上；缺省按数组顺序 |
| `opacity` | number | 全部 | 元素整体透明度 0~1（默认 1） |
| `visible` | bool | 全部 | 是否可见（默认 true） |

---

## 4. 类型速查（`type` 合法值）

| type | 定位方式 | 专属字段 | 说明 |
|---|---|---|---|
| `rect` | x/y/w/h | — | 矩形 |
| `roundedRect` | x/y/w/h | `cornerRadius` | 圆角矩形 |
| `ellipse` | centerX/Y+radius **或** x/y/w/h | — | 椭圆 |
| `ring` | centerX/Y+radius | `innerRadius` | 圆环（外径=radius） |
| `triangle` | x/y/w/h | — | 三角形（上中顶点→右下→左下） |
| `line` | points **或** x1/y1/x2/y2 | `thickness` | 直线 |
| `polyline` | points | `closed`, `thickness` | 折线 |
| `bandPolyline` | points | `closed`, `thickness` | 带状折线（有宽度的粗线） |
| `pie` | centerX/Y+radius | `startAngle`, `sweepAngle` | 扇形 |

> **`boolean`（BooleanPath）不支持通过 CLI 创建**——它需要布尔运算源，外部无法提供。若工程里有它（来自 GUI），`render` 可正常显示，只是 CLI 不能新增。

---

## 5. 工作示例

**场景：画一个"带圆环的管道仪表盘"。**

```json
{
  "canvasWidth": 800,
  "canvasHeight": 500,
  "canvasBackgroundColor": "#23232B",
  "primitives": [
    { "type": "rect",       "x": 100, "y": 120, "width": 200, "height": 260, "fillStyle": "Solid", "fillColor": "#40FFFFFF" },
    { "type": "ring",       "centerX": 200, "centerY": 250, "radius": 60, "innerRadius": 30, "strokeColor": "#FF7043" },
    { "type": "pie",        "centerX": 200, "centerY": 250, "radius": 40, "startAngle": -90, "sweepAngle": 270, "fillStyle": "Solid", "fillColor": "#FF7043" },
    { "type": "polyline",   "points": [[360,380],[450,300],[540,220],[630,300]], "strokeWidth": 3, "strokeColor": "#64B5F6" },
    { "type": "bandPolyline","points": [[400,100],[520,100],[520,180]], "thickness": 8, "strokeColor": "#FFB74D" }
  ]
}
```

---

## 6. 视觉自检提示（Agent 重点）

render 出 PNG 后，打开看图，**对照你的意图检查**：

- 所有想要的内容是否都在画布内（别只靠坐标手算，看图最可靠）
- 图元间是否意外重叠 / 遮挡（注意 ZIndex 上层盖下层）
- 颜色/填充是否符合描述
- 若有标注文字需求：**当前 DSL 不支持文本图元**，只能靠形状传达

发现不对 → 改 DSL → 重新 validate → new/add → render，直到图满意。

---

## 7. 反馈解析

建议工具调用时加 `--json`，返回形如：

```json
{ "ok": true, "path": "...", "width": 1280, "height": 720 }
```

失败时（exit 2 等），错误输出到 stderr，形如：

```text
[error] [#2] fillStyle 非法枚举(None/Solid/Gradient/Pattern): xxx
[error] [#3] radius 必须 >0，得到 -1
```

每个错误都带 `[#序号]` 定位到第几个图元、指明字段与**合法取值**，据此改正后重试即可。

---

## 8. 多次迭代编辑（read → edit → remove → render 循环）

CLI 支持**增量编辑同一幅图**，不必每次重抄整份 DSL：

```text
1. 查 id:     sym info a.sym.json            ← 拿到每个图元的 id / type / 坐标
2. 改单个:    sym edit a.sym.json --id <id> --set x=300 --set fillColor=#FF7043
              （--set 可重复；一次改多个字段；改完默认存回原文件，或 --output 另存）
3. 删单个:    sym remove a.sym.json --id <id>
4. 看图自检:  sym render a.sym.json --output a.png --size 1280x720
```

**`edit --set` 可写字段**（小写，一次 `--set field=value` 一个）：
- 几何定位：`x` `y` `width` `height` `cornerRadius` `innerRadius` `startAngle` `sweepAngle` `rotation` `zIndex`
- 外观：`thickness` `opacity` `isVisible` `isClosed` `strokeColor` `strokeWidth` `strokeDashStyle`
  `fillStyle` `fillColor` `gradientStartColor` `gradientEndColor` `fillBrightness` `fillOpacity`
  `patternStyle` `patternForeground` `patternBackground` `patternTileSize` `patternSpacing`

> 数字用小数（如 `x=300`、`opacity=0.5`）；bool 用 `true/false`。非法字段或无法解析的值会报错并列出全部合法字段。**不可编辑**的字段：`id` / `typeKey` / `pathData` / `kind` 等结构性字段（为保护工程引用）。

**典型多次编辑场景**：Agent 先 `new` 画个框架 → `info` 记 id → 逐个 `edit` 微调位置/颜色 → `remove` 去掉不要的 → 每轮 `render` 看图确认，直至画面符合需求。