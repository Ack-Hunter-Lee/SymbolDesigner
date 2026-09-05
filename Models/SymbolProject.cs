using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SymbolDesigner.Models;

/// <summary>元件图形设计工程文件（.sym.json）。
/// 保存画布上所有 PrimitiveItem 的编辑数据（位置/尺寸/PathData/厚度/顶点厚度等），
/// 以便加载回来接着编辑。不保存渲染后的 XAML（XAML 是导出产物，由 XamlExporter 生成）。</summary>
public class SymbolProject
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // byte[] 用 Base64 编码,既紧凑又可读（git diff 也能看清大致大小）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>画布宽度（逻辑坐标，默认 160）。加载时校验，不匹配则缩放或警告。</summary>
    public double CanvasWidth { get; set; } = 160;

    /// <summary>画布高度（逻辑坐标，默认 96）。</summary>
    public double CanvasHeight { get; set; } = 96;

    /// <summary>背景参考图绝对路径（null = 未导入）。加载时若文件丢失，背景图静默不显示。
    /// 老工程文件保留此字段作为 fallback。新工程优先用 BackgroundImageData 嵌入。</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>背景参考图内嵌数据（Base64 编码的图片字节）。新工程用此字段：
    /// 工程自包含,移动/分享不丢图,不受外部图片文件丢失/移动影响。
    /// 与 BackgroundImagePath 二选一（优先 BackgroundImageData）。
    /// 默认不序列化（null 时省略，减小 JSON 体积）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? BackgroundImageData { get; set; }

    /// <summary>BackgroundImageData 对应的 MIME / 文件扩展名（不带点号，如 "png"/"jpg"）。
    /// 加载时用此扩展名构造 BitmapImage（BitmapDecoder 需要扩展名提示格式）。
    /// 缺省 = "png"。</summary>
    public string? BackgroundImageType { get; set; }

    /// <summary>背景参考图透明度（0.0 ~ 1.0）。</summary>
    public double BackgroundOpacity { get; set; } = 0.5;

    /// <summary>背景参考图适配模式："CanvasSize" / "ImageSize"。</summary>
    public string BackgroundFitMode { get; set; } = "CanvasSize";

    /// <summary>画布上的所有图形实例（按 ZIndex 顺序）。</summary>
    public List<PrimitiveItemDto> Primitives { get; set; } = new();

    /// <summary>沿路径阵列实例集合（CAD 风格块引用，每条记录 = 源 + 路径 + 副本数）。
    /// 老工程没有此字段 → 反序列化时为 null/空列表，向前兼容。</summary>
    public List<ArrayInstanceDto>? ArrayInstances { get; set; }

    /// <summary>几何约束集合（当前：相切约束）。
    /// 老工程没有此字段 → 反序列化时为 null/空列表，向前兼容。</summary>
    public List<TangencyConstraintDto>? Constraints { get; set; }

    /// <summary>序列化为 JSON 字符串（带缩进，UTF-8）。</summary>
    public string Serialize()
        => JsonSerializer.Serialize(this, s_opts);

    /// <summary>从 JSON 字符串反序列化。
    /// 兼容老文件：若 JSON 含旧字段名 "JoinStyle"，临时改名为 "OutlineJoinStyle"
    /// （与 PrimitiveItem.OutlineJoinStyle 字段对齐），保证向前兼容。</summary>
    public static SymbolProject? Deserialize(string json)
    {
        if (!string.IsNullOrEmpty(json) && json.Contains("\"JoinStyle\""))
            json = json.Replace("\"JoinStyle\"", "\"OutlineJoinStyle\"");
        return JsonSerializer.Deserialize<SymbolProject>(json, s_opts);
    }
}

/// <summary>PrimitiveItem 的可序列化 DTO。
/// 只保存编辑数据，不保存运行时状态（IsSelected）和计算属性（OutlineGeometry）。</summary>
public class PrimitiveItemDto
{
    /// <summary>唯一标识（与 Models.PrimitiveItem.Id 对齐；持久约束引用用）。
    /// 老文件缺省 = 空 → 加载时按生成顺序分配新 Guid。</summary>
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "L1";
    public string TypeKey { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 30;
    public double Height { get; set; } = 30;
    public double CornerRadius { get; set; }
    /// <summary>内圆半径（仅 L1 Ring 用）。老文件缺省 = 0（非 Ring 元素不受影响）。</summary>
    public double InnerRadius { get; set; }
    public double Rotation { get; set; }
    public int ZIndex { get; set; }
    public string LineAxis { get; set; } = "Free";
    public string PathData { get; set; } = string.Empty;
    public double Thickness { get; set; } = 6;
    /// <summary>元素透明度 0.0~1.0,默认 1.0。老工程缺省 = 1.0(完全不透明)。</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>元素可见性（false=编辑期隐藏，用于排查不可见的多余元素）。
    /// 老工程缺省 = true(可见)。保存以在加载后继续排查/恢复。</summary>
    public bool IsVisible { get; set; } = true;
    /// <summary>转角连接方式（Miter/Bevel/Round），老文件缺省 = Miter。</summary>
    public string? OutlineJoinStyle { get; set; }
    /// <summary>共线同向直通点拆分模式：true=两对（默认，保留厚度台阶）；false=合并为一对。
    /// 可空：老文件缺省 = null → 加载时按 true 处理（与老版本行为一致）。</summary>
    public bool? CollinearSplitPairs { get; set; }
    public List<VertexThicknessDto> VertexThicknesses { get; set; } = new();
    /// <summary>逐顶点接缝样式覆盖（稀疏：只存非 null 的项）。
    /// 老文件没有此字段 → 加载时所有顶点继承全局 OutlineJoinStyle。</summary>
    public List<VertexJoinStyleDto>? VertexJoinStyles { get; set; }
    /// <summary>Polyline 闭合模式（true=闭合为可填充多边形，复用 ThickOutline 轮廓管道）。
    /// 老文件没有此字段 → 加载时按 false 处理（开放折线，向后兼容）。</summary>
    public bool IsClosed { get; set; }
    /// <summary>布尔运算模式（仅 BooleanPath）："Union"/"Exclude"/"Intersect"。
    /// 老文件没有此字段 → 加载时为空（老版布尔结果，无源可重新合并）。</summary>
    public string? BooleanMode { get; set; }
    /// <summary>参与布尔运算的源图元 Id（仅 BooleanPath，保留选择顺序）。
    /// 老文件没有此字段 → 加载时为空，结果图元本身不受影响。</summary>
    public List<string>? BooleanSourceIds { get; set; }

    // ===== 外观（线条/填充，任意图元通用；老文件缺省 = 各类型的默认观感） =====
    /// <summary>线条颜色 hex（缺省 null → 默认 #FFFFFF）。</summary>
    public string? StrokeColor { get; set; }
    /// <summary>线条粗细（缺省 null → 默认 0.8）。</summary>
    public double? StrokeWidth { get; set; }
    /// <summary>线条虚线样式名（Solid/Dash/Dot/DashDot，缺省 null → Solid）。</summary>
    public string? StrokeDashStyle { get; set; }
    /// <summary>填充方式名（None/Solid/Gradient，缺省 null → None）。</summary>
    public string? FillStyle { get; set; }
    /// <summary>单色填充色 hex（缺省 null → #40FFFFFF）。</summary>
    public string? FillColor { get; set; }
    /// <summary>线性渐变起始色 hex。</summary>
    public string? GradientStartColor { get; set; }
    /// <summary>线性渐变结束色 hex。</summary>
    public string? GradientEndColor { get; set; }
}

/// <summary>逐顶点接缝样式覆盖（稀疏存储）。Index 指向 VertexThicknesses 中的顶点序号。</summary>
public class VertexJoinStyleDto
{
    public int Index { get; set; }
    public string Style { get; set; } = string.Empty;
}

/// <summary>沿路径阵列的 DTO。
/// 渲染时由 VM 按 Type 分发：Path → PathDistributor；Rectangular → 行列网格；Polar → 圆周分布。
/// 不在 JSON 中保存"展开后的 N 份副本"——展开是运行期动作，文件只存指针 + 元数据。
/// 旧文件缺省：Type=Path（枚举 0），矩形/环形参数取字段默认值，行为不变。</summary>
public class ArrayInstanceDto
{
    /// <summary>唯一 ID（与 Models.ArrayInstance.Id 对齐）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>源图形快照（创建时克隆，源被删/改也不影响阵列）。</summary>
    public PrimitiveItemDto Source { get; set; } = new();

    /// <summary>阵列类型（0=Path，1=Rectangular，2=Polar）。缺省 0 = 旧文件路径阵列。</summary>
    public int ArrayType { get; set; }

    #region Path 参数
    /// <summary>路径 PathData（XAML 格式 "M x,y L x,y ..."）。</summary>
    public string PathData { get; set; } = string.Empty;

    /// <summary>副本总数（含源或不含源，1 ≤ Count ≤ 100）。</summary>
    public int Count { get; set; } = 5;

    /// <summary>true = 含源（第一份与源重合）；false = 内部均匀分布。</summary>
    public bool IncludeSource { get; set; } = true;
    #endregion

    #region Rectangular 参数
    /// <summary>列数（横向，沿 +X）。</summary>
    public int Columns { get; set; } = 4;

    /// <summary>行数（纵向，沿 +Y）。</summary>
    public int Rows { get; set; } = 3;

    /// <summary>列间距（相邻副本中心 +X 距离）。</summary>
    public double ColSpacing { get; set; } = 40;

    /// <summary>行间距（相邻副本中心 +Y 距离）。</summary>
    public double RowSpacing { get; set; } = 40;
    #endregion

    #region Polar 参数
    /// <summary>圆心 X（画布坐标）。</summary>
    public double CenterX { get; set; }

    /// <summary>圆心 Y（画布坐标）。</summary>
    public double CenterY { get; set; }

    /// <summary>填充角度（度，360 = 整圆）。</summary>
    public double FillAngle { get; set; } = 360;

    /// <summary>true = 副本随圆周旋转；false = 保持源朝向。</summary>
    public bool RotateItems { get; set; } = true;
    #endregion

    /// <summary>在源之上的 ZIndex 偏移（默认 1，避免被源遮挡）。</summary>
    public int ZIndexOffset { get; set; } = 1;

    /// <summary>阵列整体位移 X（拖动整个阵列的累积偏移，缺省 0）。</summary>
    public double OffsetX { get; set; }

    /// <summary>阵列整体位移 Y（拖动整个阵列的累积偏移，缺省 0）。</summary>
    public double OffsetY { get; set; }
}

/// <summary>画布快照 DTO（撤销/重做专用）：
/// 把 Primitives + ArrayInstances 打包成一个 JSON 对象，使单个快照覆盖画布全部状态。
/// 与 SymbolProject 的区别：SymbolProject 还包含画布尺寸/背景图等"工程元数据"，
/// 而快照只关心"可被编辑恢复的内容"（不重复存尺寸/背景图，减少快照大小与比对开销）。</summary>
public class ProjectSnapshotDto
{
    public List<PrimitiveItemDto> Primitives { get; set; } = new();
    public List<ArrayInstanceDto> ArrayInstances { get; set; } = new();
}

/// <summary>顶点厚度 DTO。IsStart/IsEnd 不保存（加载时按 Index 和集合 Count 重新计算）。</summary>
public class VertexThicknessDto
{
    public int Index { get; set; }
    public double IncomingThickness { get; set; }
    public double OutgoingThickness { get; set; }
    /// <summary>Round join 内径圆弧半径（默认 0 = 经典无内径弧）。末顶点 = 0。</summary>
    public double JoinInnerRadius { get; set; }
    /// <summary>左侧普通轮廓点圆角半径（0 = 无）。端点/段末端圆角化用，渲染由 BuildOutlineSteps 重建。</summary>
    public double LeftCornerRadius { get; set; }
    /// <summary>右侧普通轮廓点圆角半径（0 = 无）。</summary>
    public double RightCornerRadius { get; set; }
}
