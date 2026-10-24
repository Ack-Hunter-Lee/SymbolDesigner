using System;

namespace SymbolDesigner.Models;

/// <summary>
/// 阵列类型（对齐 AutoCAD 的三种阵列）。
/// 序列化时 Type 缺省 = Path（枚举 0），保证旧文件加载不变。
/// </summary>
public enum ArrayType
{
    /// <summary>沿路径阵列：沿折线均匀分布（原 POC 1）。</summary>
    Path = 0,

    /// <summary>矩形阵列：行列网格分布，无需参考路径。</summary>
    Rectangular = 1,

    /// <summary>环形阵列：绕圆心圆周分布，无需参考路径。</summary>
    Polar = 2,
}

/// <summary>
/// 阵列实例（CAD "Array" 风格）。
/// 一个 ArrayInstance = 一个源 + 类型参数 + 副本布局。
/// 画布上不直接存放 N 份"副本图形"——只在 Primitives 之上多挂一层"块引用"。
/// 渲染时根据源 + 类型参数计算 N 个 Placement，每份 Placement 应用到源的克隆：
///   - 平移到 Placement 位置
///   - 旋转到 Placement 朝向（路径切线方向 / 环形切向）
///
/// 设计动机（与"展开存 N 份 PrimitiveItem"对比）：
///   1. 源改了 → 阵列副本自动跟随（CAD 行为，源是 master）
///   2. 路径/行列/圆心改了 → 副本数与位置自动重算
///   3. 副本数 = 1 元数据（N 处都要存，浪费）
///   4. 导出 XAML 时直接展开（XamlExporter 阶段处理，与运行期解耦）
///
/// 几何约定：
///   - 路径走"通用几何入口"：任意能产出几何的图元都能当路径（圆/矩形/履带/布尔结果…），
///     曲线由 WPF GetFlattenedPathGeometry 摊平为直线段，只取最长轮廓
///   - 路径阵列的基准点 = **源中心**（包围盒中心）：阵列从源当前位置起步，
///     副本中心沿路径法向偏移 PathOffset（保持与路径恒定距离）
///   - 旋转：副本绕自身中心旋转路径切线方向（WPF Path.RenderTransform，旋转不移动中心）
///   - 矩形/环形：旋转 = 0（矩形）或环形切向（RotateItems 开启时）
/// </summary>
public class ArrayInstance
{
    /// <summary>唯一 ID。新建时分配 Guid 字符串，序列化保留。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>源图形的快照（创建时克隆，源被删/改也不影响阵列）。
    /// 用 PrimitiveItemDto 即可——渲染前会 ToDto/CreateFromDto 重建。</summary>
    public PrimitiveItemDto Source { get; set; } = new();

    /// <summary>阵列类型（矩形 / 环形 / 路径）。缺省 Path，保证旧文件兼容。</summary>
    public ArrayType Type { get; set; } = ArrayType.Path;

    // ── 路径阵列参数（Type == Path）──

    /// <summary>路径数据（XAML 格式 "M x,y L x,y ..."，与 PrimitiveItem.PathData 兼容）。
    /// 第一阶段只支持直线段；后续阶段扩展 ArcSegment 时此处替换为结构化 PathSegment[]。</summary>
    public string PathData { get; set; } = string.Empty;

    /// <summary>副本总数（1 ≤ Count ≤ 100）。</summary>
    public int Count { get; set; } = 5;

    /// <summary>路径阵列"偏移距离"：副本中心与参考路径的垂直距离（= 创建时源中心到路径的最近距离）。
    /// AutoCAD 路径阵列"基点"语义 —— 基点沿路径走，对象相对基点保持固定偏移，
    /// 故副本中心到路径的最近距离恒为 |PathOffset|，与曲线区间无关。
    /// 0 = 副本中心落在路径上；正负 = 路径切向的左右侧（y-down 坐标系下正 = 切向左侧 = 视觉上方）。
    /// 圆路径 → 副本中心落在同心的 ± 偏移圆上（半径 = 路径半径 ± 偏移）。</summary>
    public double PathOffset { get; set; }

    /// <summary>路径阵列：副本是否随路径切线旋转（true = 切向站立，对齐 AutoCAD "Align items"）。
    /// 环形/矩形阵列不受此字段影响（环形由 RotateItems 控制）。</summary>
    public bool PathAlign { get; set; } = true;

    // ── 矩形阵列参数（Type == Rectangular）──

    /// <summary>列数（横向，沿 +X）。1 ≤ Columns ≤ 100。</summary>
    public int Columns { get; set; } = 4;

    /// <summary>行数（纵向，沿 +Y）。1 ≤ Rows ≤ 100。</summary>
    public int Rows { get; set; } = 3;

    /// <summary>列间距（相邻副本包围盒中心之间，沿 X 方向的正向距离）。</summary>
    public double ColSpacing { get; set; } = 40;

    /// <summary>行间距（相邻副本包围盒中心之间，沿 Y 方向的正向距离）。</summary>
    public double RowSpacing { get; set; } = 40;

    // ── 环形阵列参数（Type == Polar）──

    /// <summary>圆心 X（画布坐标，副本绕此点圆周分布）。</summary>
    public double CenterX { get; set; }

    /// <summary>圆心 Y（画布坐标，副本绕此点圆周分布）。</summary>
    public double CenterY { get; set; }

    /// <summary>填充角度（度）：360 = 整圆，正数 = 逆时针，负数 = 顺时针。</summary>
    public double FillAngle { get; set; } = 360;

    /// <summary>true = 副本随圆周旋转（切向对齐，对齐 AutoCAD "Rotate Items"）；
    /// false = 副本保持源朝向只平移。</summary>
    public bool RotateItems { get; set; } = true;

    /// <summary>阵列在 Primitives 之上的额外 ZIndex 偏移。
    /// 默认 = 1（位于源之上，避免被源遮挡）。
    /// 0 = 与源同层（按 Primitives 顺序决定）；负值 = 位于源之下（罕见，用于"阵列铺底"）。</summary>
    public int ZIndexOffset { get; set; } = 1;

    /// <summary>阵列整体位移（用户拖动整个阵列产生的累积偏移，单位画布坐标）。
    /// 与 Source/PathData 分离：不改动源/路径本身，只让副本整体平移。
    /// 三种类型的语义一致：RenderPlacement = 理论位置 + Offset。
    /// 支持"整体移动阵列"不破坏各副本相对关系（行列网格/圆周/沿路径的间距角度原样保留）。</summary>
    public double OffsetX { get; set; }

    /// <summary>阵列整体位移 Y 分量（见 OffsetX）。</summary>
    public double OffsetY { get; set; }
}

/// <summary>阵列类型下拉选项（中文显示名 + 枚举值），供预览浮层 ComboBox 绑定。</summary>
public class ArrayTypeOption
{
    public string Label { get; }
    public ArrayType Value { get; }

    public ArrayTypeOption(string label, ArrayType value)
    {
        Label = label;
        Value = value;
    }

    public override string ToString() => Label;
}

/// <summary>阵列"参考路径"下拉选项（可读标签 + 目标图元），供预览浮层 ComboBox 绑定。
/// 候选 = 画布上除源之外**任何能产出几何的图元**（不按类型列白名单），由用户显式指定哪一个当路径。
/// 持有图元引用（仅存在于一次预览会话内，不入 DTO）。</summary>
public class ArrayPathOption
{
    public string Label { get; }
    public PrimitiveItem Item { get; }

    public ArrayPathOption(string label, PrimitiveItem item)
    {
        Label = label;
        Item = item;
    }

    public override string ToString() => Label;
}