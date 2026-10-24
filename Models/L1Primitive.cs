namespace SymbolDesigner.Models;

/// <summary>
/// L1 基础图形枚举（元件设计器工具栏的"零件"）。
/// 每个值对应一种标准 WPF 控件（Rectangle / Ellipse / Line / Polygon）。
/// 由 SymbolDesignerViewModel 在工具栏展示，拖入画布后生成对应 PrimitiveItem。
/// </summary>
public enum L1Primitive
{
    /// <summary>矩形（设备主体、底座、面板等）</summary>
    Rectangle,

    /// <summary>椭圆（圆罐、电机圆、阀门圆盘等）</summary>
    Ellipse,

    /// <summary>直线（管路、连接线、刻度线等）</summary>
    Line,

    /// <summary>圆角矩形（机壳、面板、控制柜等）</summary>
    RoundedRectangle,

    /// <summary>三角形（指示箭头、警告标志、阀瓣等）</summary>
    Triangle,

    /// <summary>带状折线：折线的基础上按全局带宽（BandWidth）向外扩充左右两条平行轮廓，
    /// 形成一条闭合轮廓带（CAD 的 stroke outline 效果）。画线工具产物的目标类型。</summary>
    BandPolyline,

    /// <summary>纯折线（无带宽）：仅骨架沿的 PathData 描边显示，不带轮廓/接缝/逐顶点带宽控制。
    /// 与 BandPolyline 是一对兄弟——同一份 PathData，一个带带宽一个不带。
    /// 适合做"参考线/工艺走向/分区辅助线"等不需要带状粗细语义的场合。
    /// 绘制时与 BandPolyline 共用画线状态机（ClickDraw/MoveDraw/EndDraw），
    /// 仅在 EndDraw 提交时按 DrawMode 决定目标类型。</summary>
    Polyline,

    /// <summary>圆环：两个同圆心不同半径的圆构成的环形。Width/Height = 外圆直径，
    /// InnerRadius 单独存储（内圆半径）。渲染用单 Path + 外顺内逆反向 + NonZero 填充规则 → 内圆天然空洞。</summary>
    Ring,

    /// <summary>布尔运算结果（合并/剪除/相交的静态 Path 快照）。
    /// 来源：选中两个闭合图元执行布尔运算后，由 Geometry.Combine 结果 flattern 成
    /// M/L/Z 折线序列存 PathData（局部坐标），渲染/序列化/再次参与布尔都复用该数据。
    /// 属于"死形状"：只能整体移动/缩放，不具备圆角/厚度等参数化编辑能力。</summary>
    BooleanPath,

    /// <summary>扇形（Pie）：圆心 + 半径 + 起始角 + 扫掠角闭合的填充满扇面。
    /// Width/Height = 外圆包围盒（直径），StartAngle/SweepAngle 定义扇面方向与幅度。
    /// 渲染用单 Path（Data = PieGeometry：M 弧起点 A 弧终点 L 圆心 Z），可填充、可参与布尔。</summary>
    Pie,

    /// <summary>传送带（Belt）：一条皮带绕若干"圆滚轮"（Ellipse / Ring）穿行的单条路径。
    /// 几何 = 两圆公切线（直线段）+ 绕圆包络弧（真圆弧）拼接，由 BeltSolver 依"绕行拓扑 +
    /// 两个自由端点 + 滚轮实时位置"重算（不落盘）。
    /// 渲染为单线描边 Path（无填充），导出为 &lt;Path Data="..." Stretch="None"&gt;，
    /// 下游可直接对 StrokeDashOffset 做动画实现"皮带在运行"。</summary>
    Belt,
}

/// <summary>直线轴对齐模式（仅 L1 Line 生效）。</summary>
public enum LineAxisMode
{
    /// <summary>任意斜线（默认）</summary>
    Free,
    /// <summary>水平线</summary>
    Horizontal,
    /// <summary>竖直线</summary>
    Vertical,
}

/// <summary>画布交互模式。</summary>
public enum DrawMode
{
    /// <summary>普通模式（默认）。</summary>
    None,
    /// <summary>带状折线模式。点击鼠标逐点加入顶点，按 Enter 提交为 BandPolyline 图元，按 Backspace 撤销上一个点，按 Esc 取消。</summary>
    BandPolyline,
    /// <summary>纯折线模式。点击鼠标逐点加入顶点，按 Enter 提交为 Polyline 图元（无带宽的兄弟类型），
    /// 按 Backspace 撤销上一个点，按 Esc 取消。与 BandPolyline 共用画线状态机。</summary>
    Polyline,
    /// <summary>直线模式。第一次点击确定起点，第二次点击确定终点；提交后自动退出。</summary>
    Line,
    /// <summary>矩形模式。两次点击：中心点 + 对角点（提交后退出）。</summary>
    Rectangle,
    /// <summary>圆角矩形模式。两次点击：中心点 + 对角点（提交后退出）。</summary>
    RoundedRectangle,
    /// <summary>椭圆模式。两次点击：中心点 + 圆周点（提交后退出）。</summary>
    Ellipse,
    /// <summary>圆环模式。两次点击：中心点 + 外圆周点（提交后退出）。</summary>
    Ring,
    /// <summary>三角形模式。两次点击：中心点 + 角点（提交后退出，等边方向由中心-角点连线确定）。</summary>
    Triangle,
    /// <summary>扇形（Pie）模式。两次点击：中心点 + 半径点（周长点，正圆），提交后默认扫掠角 = 60°。</summary>
    Pie,
    /// <summary>传送带（Belt）模式。两次点击：自由端点 A + 自由端点 B，提交时自动收录挡路的圆
    /// （与工具栏 "Belt" 工具项对应，TypeKey 同名）。之后拖端点手柄可继续穿绕/脱开/两两靠近闭合。</summary>
    Belt,
}