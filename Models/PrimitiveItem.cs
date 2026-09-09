using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Converters;
using SymbolDesigner.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;

namespace SymbolDesigner.Models;

/// <summary>
/// 工具栏的单个图形项（绑定到左侧工具栏的图标 + 标签）。
/// 不直接出现在画布上，拖入画布后由 SymbolDesignerViewModel 生成对应的 PrimitiveItem。
/// 旧版有 PrimitiveKind(L1/L2) 分类概念，已废弃：图元身份仅由 Key + TypeKey 一维字符串决定，
/// Key 用作拖拽决策 + 工具栏预览匹配的唯一标识。
/// </summary>
public class ToolboxEntry
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;

    public ToolboxEntry(string key, string label, string icon)
    {
        Key = key;
        Label = label;
        Icon = icon;
    }
}

/// <summary>
/// 画布上的单个图形实例。每个元件（设备）由多个 PrimitiveItem 组合而成。
/// X/Y/Width/Height 均为画布坐标（默认画布 1920×1080）。
///
/// 画布形式（CAD 风格，与 PowerPoint/Visio 一致）：
/// - 画布逻辑尺寸固定 1920×1080（不论主窗口多大，逻辑坐标永远不变）
/// - "作画区域" = 主窗口中间列，由 Grid.* 列宽自动适配主窗口大小
/// - 画布物理渲染尺寸 = 作画区域内 FitRatio×作画区域（默认 FitRatio=1.0，完全铺满）
/// - 缩放控制：滚轮/工具栏缩放 ZoomLevel（0.25~4.0）
/// - 平移：Space+拖动 / 中键拖动（按 PanX/PanY）
/// - 缩放/平移通过 RenderTransform 实现（不是 Viewbox）
///
/// 调试：实时可用视口尺寸 + 缩放/平移值显示在右下角调试标签。
/// </summary>
public partial class PrimitiveItem : ObservableObject
{
    /// <summary>唯一标识（持久约束 / 撤销 / 序列化引用图元用）。
    /// 一旦创建不改变；克隆时生成新 Id。init 允许 DTO 还原时写回（对象初始化器）。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>图元类型标识（唯一决定渲染分支、属性面板可见性）。
    /// 当前取值：Rectangle / Ellipse / Line / RoundedRectangle / Triangle / BandPolyline / Polyline / Ring。
    /// 旧版"PrimitiveKind(L1/L2)"分类概念已废弃——渲染分支/属性面板/选中决策一律按 TypeKey 字符串判断。</summary>
    [ObservableProperty] private string _typeKey = string.Empty;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width = 30;
    [ObservableProperty] private double _height = 30;

    /// <summary>圆角半径（仅矩形类生效）。0 = 直角；>0 时四角为该半径的圆弧。</summary>
    [ObservableProperty] private double _cornerRadius;

    /// <summary>内圆半径（仅 L1 Ring 生效）。Width/Height 表示外圆直径，此字段为内圆半径。
    /// 约束：0 < InnerRadius < min(Width,Height)/2（保存时由 UI/拖拽保证）。</summary>
    [ObservableProperty] private double _innerRadius = 10;

    /// <summary>圆角半径变化及时刷新选中装饰几何（圆角矩形贴形需求）。</summary>
    partial void OnCornerRadiusChanged(double value)
        => OnPropertyChanged(nameof(SelectionGeometry));

    /// <summary>旋转角度（仅 L2 复合图形使用，风扇叶片旋转用）</summary>
    [ObservableProperty] private double _rotation;

    /// <summary>
    /// 层叠顺序（Z-index）。值大的在上层。控制画布上两个元素的遮挡关系。
    /// 拖入时按添加顺序递增（Primitives 集合的索引就是初始 ZIndex）。
    /// </summary>
    [ObservableProperty] private int _zIndex;

    /// <summary>是否被选中（用于编辑时的选中高亮）</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>运行时关联：若该图元是阵列的某个副本克隆，则指向其所属 ArrayInstance（引用，不序列化）。
    /// null = 普通图元（画布主集合成员），非 null = ArrayRenderItems 渲染层克隆。
    /// DraggablePrimitive 据此把"点击/拖动"路由到阵列整体操作（选阵列/平移阵列），而非当作普通图元。
    /// 仅由 BuildArrayClones 赋值，DTO 往返不保留（克隆不落盘）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Models.ArrayInstance? ArrayOwner { get; set; }

    /// <summary>运行时标记：该克隆是否是阵列的"源位"（第一份，即阵列的本体）。
    /// 选中整个阵列时视觉区分：源位用亮蓝突出（本体可编辑/牵引），其余副本描边变灰（影元素）。
    /// 仅由 BuildArrayClones 对第一份克隆赋值，DTO 往返不保留（克隆不落盘）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsArraySourceClone { get; set; }

    /// <summary>元素透明度（0.0 ~ 1.0，默认 1.0 不透明）。
    /// 与背景图透明度独立——这是绘制层自己的可见度,可让用户做"半透明预览"或"分层叠加"。
    /// 改变时应标 dirty,但不影响 OutlineGeometry 计算（纯渲染属性）。</summary>
    [ObservableProperty] private double _opacity = 1.0;

    // ===== 外观（线条/填充，所有图元通用；属性面板三大组中的前两组） =====

    /// <summary>线条颜色（hex，如 "#FFFFFF"）。解析失败 → 不画描边（StrokeBrush 为 null）。</summary>
    [ObservableProperty] private string _strokeColor = "#FFFFFF";
    /// <summary>线条粗细（设备无关单位）。与 BandPolyline 的"体宽"BandWidth 语义不同：这是图形自身的描边粗细。</summary>
    [ObservableProperty] private double _strokeWidth = 0.8;
    /// <summary>线条虚线样式（实线/虚线/点线/点划线）。</summary>
    [ObservableProperty] private StrokeDashStyle _strokeDashStyle = StrokeDashStyle.Solid;

    /// <summary>填充方式：无 / 单色 / 线性渐变 / 单色渐变 / 图案 / 图片或纹理。</summary>
    [ObservableProperty] private FillStyle _fillStyle = FillStyle.None;
    /// <summary>单色填充颜色（FillStyle=Solid/MonochromeGradient 时用）。</summary>
    [ObservableProperty] private string _fillColor = "#40FFFFFF";
    /// <summary>线性渐变起始色（FillStyle=Gradient 时用）。</summary>
    [ObservableProperty] private string _gradientStartColor = "#FFFFFFFF";
    /// <summary>线性渐变结束色（FillStyle=Gradient 时用）。</summary>
    [ObservableProperty] private string _gradientEndColor = "#40000000";

    /// <summary>填充亮度百分比 0~100（默认 100 = 原色不变）。
    /// 作用于 FillColor / GradientStartColor / GradientEndColor（按 RGB 通道 × 系数），不影响描边。
    /// Pattern / ImageTexture 当前为占位渲染,本字段不参与（待图案/图片引擎接入后再接）。</summary>
    [ObservableProperty] private double _fillBrightness = 100.0;
    /// <summary>填充透明度百分比 0~100（默认 100 = 完全不透明）。
    /// 作用于上述同组色（按 alpha × 系数）。注意：与 PrimitiveItem.Opacity（元素整体透明度）是两条独立链路：
    /// 本字段只影响填充,不透明度乘法上元素整体仍受 Opacity 控制。</summary>
    [ObservableProperty] private double _fillOpacity = 100.0;

    // ===== 图案填充专属字段（仅 FillStyle=Pattern 时生效，与 Solid/Gradient 的颜色字段互不通用） =====
    // 遵循"按模式各管各的颜色"原则——图案前景/背景是独立语义，不与 FillColor 混用。

    /// <summary>图案前景色（Pattern 模式下画线/画点的颜色）。默认 #E0E0E0（近白浅灰，Brush.Brand.StrokeLight），
    /// 不扎眼、与画布深底色对比度足够，符合经典 CAD hatch 风格。
    /// 与 Solid 模式的 FillColor 互不干扰——切换模式各自保留，编辑另一组不会改变图案外观。
    /// 注意：默认值变更不影响已存在图元（旧工程按各自保存的色保留），仅新建图元生效。</summary>
    [ObservableProperty] private string _patternForeground = "#E0E0E0";
    /// <summary>图案背景色（Pattern 模式下的底色/网格内空白色）。默认完全透明——
    /// 经典 CAD hatch 视觉是"线条 + 透空底"，让用户透过图案看到画布/下方图元；
    /// 用户想加底色（如强调填充感）可改任意不透明颜色。仅在 alpha > 0 时画底，
    /// "#00000000" 视为透空（PatternBrushFactory 检测后跳过背景层）。</summary>
    [ObservableProperty] private string _patternBackground = "#00000000";
    /// <summary>图案前景亮度 0~100（默认 100 = 原色不变）。仅作用于 PatternForeground。</summary>
    [ObservableProperty] private double _patternForegroundBrightness = 100.0;
    /// <summary>图案前景透明度 0~100（默认 100 = 完全不透明）。仅作用于 PatternForeground。</summary>
    [ObservableProperty] private double _patternForegroundOpacity = 100.0;
    /// <summary>图案背景亮度 0~100（默认 100 = 原色不变）。仅作用于 PatternBackground。</summary>
    [ObservableProperty] private double _patternBackgroundBrightness = 100.0;
    /// <summary>图案背景透明度 0~100（默认 100 = 完全不透明）。仅作用于 PatternBackground。</summary>
    [ObservableProperty] private double _patternBackgroundOpacity = 100.0;
    /// <summary>图案样式（Diagonal/Horizontal/Vertical/Cross）。默认 Diagonal。</summary>
    [ObservableProperty] private PatternStyle _patternStyle = PatternStyle.Diagonal;
    /// <summary>图案平铺单元尺寸（逻辑像素）。改值大小 → 图案密度随之变化。
    /// 范围 4~40，默认 8。</summary>
    [ObservableProperty] private double _patternTileSize = 8.0;
    /// <summary>图案线间距（逻辑像素）：相邻两条线的垂直间距，直接控制密度。
    /// 数值越大线越疏、密度越低；越小越密。范围 2~24，默认 6（比 tileSize 更直观地表达"疏密"）。</summary>
    [ObservableProperty] private double _patternSpacing = 6.0;

    /// <summary>线条画刷（绑 Stroke）。</summary>
    public System.Windows.Media.Brush? StrokeBrush => Converters.ColorUtil.ParseHex(StrokeColor);
    /// <summary>填充画刷（绑 Fill）：按 FillStyle 分派到不同合成路径。
    /// None → null（WPF 视同不填充,等价透明）。
    /// Solid / Gradient / MonochromeGradient → 走"亮度+透明度"合成。
    /// Pattern / ImageTexture → 当前 UI 框架已就位,具体渲染后续迭代,此处返回 null（不画填充但保留描边）。</summary>
    public System.Windows.Media.Brush? FillBrush => FillStyle switch
    {
        FillStyle.None => null,
        FillStyle.Solid => Converters.ColorUtil.ParseHex(
            Converters.ColorUtil.ApplyBrightnessAndOpacity(FillColor, FillBrightness, FillOpacity)),
        FillStyle.Gradient => Converters.ColorUtil.BuildLinearGradient(
            Converters.ColorUtil.ApplyBrightnessAndOpacity(GradientStartColor, FillBrightness, FillOpacity),
            Converters.ColorUtil.ApplyBrightnessAndOpacity(GradientEndColor, FillBrightness, FillOpacity)),
        // 单色渐变：起始色 = FillColor 经合成；终止色 = 派生（同色更暗 + 更透）。两个色都跟着亮度/透明度走。
        FillStyle.MonochromeGradient => Converters.ColorUtil.BuildLinearGradient(
            Converters.ColorUtil.ApplyBrightnessAndOpacity(FillColor, FillBrightness, FillOpacity),
            Converters.ColorUtil.DeriveMonochromeEnd(FillColor, FillBrightness, FillOpacity)),
        // Pattern：4 种内置图案，前景/背景各走亮度+透明度合成后平铺。
        // 颜色字段完全独立（PatternForeground 等），与 Solid 模式的 FillColor 互不干扰。
        FillStyle.Pattern => Converters.PatternBrushFactory.Build(
            PatternStyle, PatternForeground,
            PatternForegroundBrightness, PatternForegroundOpacity,
            PatternBackground, PatternBackgroundBrightness, PatternBackgroundOpacity,
            PatternTileSize, PatternSpacing),
        // ImageTexture：图片引擎未接入 → 暂不画填充（保留描边）
        _ => null,
    };
    /// <summary>线条虚线数组（绑 StrokeDashArray）：Solid → null（实线）。</summary>
    public System.Windows.Media.DoubleCollection? StrokeDashArray => StrokeDashStyle switch
    {
        StrokeDashStyle.Dash => new System.Windows.Media.DoubleCollection { 4, 2 },
        StrokeDashStyle.Dot => new System.Windows.Media.DoubleCollection { 1, 2 },
        StrokeDashStyle.DashDot => new System.Windows.Media.DoubleCollection { 4, 2, 1, 2 },
        _ => null,
    };

    partial void OnStrokeColorChanged(string value) => OnPropertyChanged(nameof(StrokeBrush));
    partial void OnFillColorChanged(string value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnFillStyleChanged(FillStyle value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnGradientStartColorChanged(string value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnGradientEndColorChanged(string value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnFillBrightnessChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnFillOpacityChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnStrokeDashStyleChanged(StrokeDashStyle value) => OnPropertyChanged(nameof(StrokeDashArray));

    // Pattern 字段变化 → 通知 FillBrush 重算（任一字段影响图案外观）
    partial void OnPatternForegroundChanged(string value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternBackgroundChanged(string value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternForegroundBrightnessChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternForegroundOpacityChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternBackgroundBrightnessChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternBackgroundOpacityChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternStyleChanged(PatternStyle value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternTileSizeChanged(double value) => OnPropertyChanged(nameof(FillBrush));
    partial void OnPatternSpacingChanged(double value) => OnPropertyChanged(nameof(FillBrush));

    /// <summary>元素可见性（默认 true 可见）。false = 画布上整个元素隐藏
    /// （渲染与命中测试全禁用，拖拽/选中均不可达），但元素仍保留在 Primitives 集合里 →
    /// 用户从右侧"元素"页恢复显示。编辑辅助状态，用于排查不可见的多余元素。</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>IsVisible 变化时触发（ViewModel 订阅后刷新"隐藏元素计数"统计）。</summary>
    public event EventHandler? VisibilityChanged;

    partial void OnIsVisibleChanged(bool value)
        => VisibilityChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>元素页列表显示的位置尺寸辅助信息（如 "X=12 Y=8  40×24"）。
    /// 拖动/改尺寸时随坐标变化实时刷新。</summary>
    public string PositionInfo =>
        $"X={X:0.#}  Y={Y:0.#}   {Width:0.##}×{Height:0.##}";

    partial void OnXChanged(double value)
    {
        OnPropertyChanged(nameof(PositionInfo));
        OnPropertyChanged(nameof(RingGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }
    partial void OnYChanged(double value)
    {
        OnPropertyChanged(nameof(PositionInfo));
        OnPropertyChanged(nameof(RingGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }
    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(PositionInfo));
        OnPropertyChanged(nameof(RingGeometry));
        OnPropertyChanged(nameof(PieGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }
    partial void OnHeightChanged(double value)
    {
        OnPropertyChanged(nameof(PositionInfo));
        OnPropertyChanged(nameof(RingGeometry));
        OnPropertyChanged(nameof(PieGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }
    /// <summary>内圆半径变化时刷新 RingGeometry（圆环几何）—— 拖动内圆手柄看实时效果的关键钩子。</summary>
    partial void OnInnerRadiusChanged(double value)
    {
        OnPropertyChanged(nameof(RingGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>扇形起始角（度，0=东，正=屏幕顺时针）。仅 L1 Pie 生效。</summary>
    [ObservableProperty] private double _startAngle;
    /// <summary>扇形扫掠角（度，正角度方向，默认 60°）。仅 L1 Pie 生效。</summary>
    [ObservableProperty] private double _sweepAngle = 60;

    /// <summary>起始角 / 扫掠角变化时刷新 PieGeometry 与选中装饰（扇形贴形）。</summary>
    partial void OnStartAngleChanged(double value)
    {
        OnPropertyChanged(nameof(PieGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }
    partial void OnSweepAngleChanged(double value)
    {
        OnPropertyChanged(nameof(PieGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>扇形几何：单 Path（M 弧起点 A 弧终点 L 圆心 Z）闭合填充满扇面。
    /// 局部坐标（以元素 X/Y 为原点），圆心 = (W/2, H/2)，半径 = min(W,H)/2。
    /// Width/Height/StartAngle/SweepAngle 任一变化都会触发刷新。</summary>
    public System.Windows.Media.Geometry? PieGeometry
    {
        get
        {
            double radius = System.Math.Min(Width, Height) / 2;
            if (Width <= 0 || Height <= 0 || radius <= 0) return null;
            return System.Windows.Media.Geometry.Parse(
                Converters.PieGeometryBuilder.BuildPath(Width / 2, Height / 2, radius, StartAngle, SweepAngle));
        }
    }

    /// <summary>直线轴对齐模式（仅 L1 Line 生效）。Free=斜线，Horizontal=水平，Vertical=竖直。</summary>
    [ObservableProperty] private LineAxisMode _lineAxis = LineAxisMode.Free;

    /// <summary>轴模式变化时刷新选中装饰几何（直线贴形）。</summary>
    partial void OnLineAxisChanged(LineAxisMode value)
        => OnPropertyChanged(nameof(SelectionGeometry));

    /// <summary>折线 / 铅笔工具的 Path 数据字符串（XAML "M x,y L x,y ..."）。
    /// 对于 Line / Rectangle / Ellipse 等 L1 图形此字段为空。
    /// BooleanPath = 布尔快照（BooleanOps 的"多环骨架"M/L/Z + F 前缀，全局局部坐标）。</summary>
    [ObservableProperty] private string _pathData = string.Empty;

    /// <summary>PathData 的解析缓存（局部坐标顶点列表）。
    /// PathData 变化时由 OnPathDataChanged 重建；读缓存比每帧 Split+TryParse 快得多。
    /// 供选区包围盒、命中检测等频繁解析 PathData 的场景复用，避免拖动时反复字符串解析。</summary>
    private IReadOnlyList<Point>? _parsedPathPoints;

    /// <summary>PathData 解析缓存（局部坐标顶点）。null 表示 PathData 为空。</summary>
    public IReadOnlyList<Point>? ParsedPathPoints => _parsedPathPoints;

    /// <summary>PolylineGeometry 的 StreamGeometry 缓存（局部坐标）。PathData 变化时由 getter 命中失效重建。</summary>
    private System.Windows.Media.StreamGeometry? _polylineCache;
    /// <summary>PolylineGeometry 缓存对应的 PathData（用于判定失效）。</summary>
    private string? _cachedPolylinePath;

    /// <summary>BandPolyline 的整体带宽（保留兼容，新代码用 Segments 按段控制）。</summary>
    /// 仅 L1 BandPolyline 生效，其他 L1 图形此字段为 0。</summary>
    [ObservableProperty] private double _bandWidth = 6;

    /// <summary>轮廓线的接缝样式（控制 OutlineGeometry 的轮廓封口方式）。
    /// 通过轮廓本身直接控制接缝：Miter 尖角 / Bevel 斜切 / Round 圆弧。
    /// 默认 Miter（与老版本默认行为一致）。
    /// 注意：虽然控制的是轮廓，但底层通过骨架线 + 带宽间接实现——这就是用户
    /// 想要的"轮廓线层面"的控制粒度。</summary>
    [ObservableProperty] private JoinStyle _outlineJoinStyle = JoinStyle.Miter;

    /// <summary>是否闭合为形状（仅 Polyline 有效）。false = 开放折线（纯描边骨架）；
    /// true = 骨架点直连单环闭合 → 生成可填充的闭合多边形（OutlineGeometry 复用，
    /// 改用 BuildClosedShapeSteps 生成轮廓点，无带宽偏移）。闭合后每骨架点可用
    /// LeftCornerRadius 圆角（0=尖角 Miter，>0=圆角 Round）。</summary>
    [ObservableProperty] private bool _isClosed;

    /// <summary>布尔运算模式编码（仅 BooleanPath）："Union"/"Exclude"/"Intersect"。
    /// Exclude 有顺序语义（先选 ∖ 后选），重新合并时保持原顺序。
    /// 空 = 老版布尔结果（无源引用，不可重新合并）。</summary>
    [ObservableProperty] private string _booleanMode = string.Empty;

    /// <summary>参与本次布尔运算的源图元【几何快照】DTO（仅 BooleanPath，顺序 = 选择顺序）。
    /// 不存"画布上保留的源图元引用"，而是把源几何快照挂载在结果上；需要调整时按快照
    /// 物化回画布、编辑后再重新合并。空 = 老版布尔结果（无快照，调整源禁用）。</summary>
    [ObservableProperty] private List<PrimitiveItemDto> _booleanSourceSnapshot = new();

    partial void OnIsClosedChanged(bool value)
    {
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(PolylineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
        // 闭合形状每顶点圆角需要 VertexBandWidths 作为半径容器（带宽恒 0，仅用 LeftCornerRadius）
        if (value && !string.IsNullOrEmpty(PathData))
            SyncVertexBandWidths(Converters.BandOutlineBuilder.ParsePathData(PathData).Count, 0);
    }

    /// <summary>BandPolyline 的每顶点带宽集合（顶点数 = 段数 + 1）。
    /// 带宽绑定到顶点（连接点），符合"承上启下"语义：
    ///   - 首顶点：只有 OutgoingBandWidth（启下=段 0 首端）
    ///   - 末顶点：只有 IncomingBandWidth（承上=段 N-1 末端）
    ///   - 中间顶点：IncomingBandWidth（承上=前段末端）+ OutgoingBandWidth（启下=后段首端）
    /// 集合元素变化（增减或某顶点带宽变化）会触发 OnPropertyChanged("OutlineGeometry")。</summary>
    public ObservableCollection<VertexBandWidth> VertexBandWidths { get; } = new();

    /// <summary>逐顶点接缝样式（per-vertex OutlineJoinStyle）。与 VertexBandWidths 等长对齐。
    /// null = 继承全局 OutlineJoinStyle；非 null = 此顶点强制使用该值。
    /// 内部 List&lt;JoinStyle?&gt;，与 VertexBandWidths 同步增减：
    ///   - VertexBandWidths 新增顶点 → 此列表追加 null
    ///   - VertexBandWidths 删除顶点 → 此列表同步移除
    /// 元素变化触发 OnPropertyChanged("OutlineGeometry")。
    /// 注意：索引 0（首顶点）和 N-1（末顶点）的样式不参与接缝计算，但保留槽位以便 UI 通用。</summary>
    private readonly List<JoinStyle?> _vertexJoinStyles = new();

    public PrimitiveItem()
    {
        VertexBandWidths.CollectionChanged += OnVertexBandWidthsChanged;
    }

    /// <summary>VertexBandWidths 集合变化（增/删/重置）时刷新 OutlineGeometry。
    /// 同时同步 _vertexJoinStyles 列表长度（新增补 null，删除同步删）。</summary>
    private void OnVertexBandWidthsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 订阅新项的 BandWidthChanged，退订旧项
        if (e.OldItems != null)
            foreach (VertexBandWidth v in e.OldItems)
                v.BandWidthChanged -= OnVertexBandWidthChanged;
        if (e.NewItems != null)
            foreach (VertexBandWidth v in e.NewItems)
                v.BandWidthChanged += OnVertexBandWidthChanged;

        // 同步 _vertexJoinStyles 长度
        int vtCount = VertexBandWidths.Count;
        while (_vertexJoinStyles.Count > vtCount)
            _vertexJoinStyles.RemoveAt(_vertexJoinStyles.Count - 1);
        while (_vertexJoinStyles.Count < vtCount)
            _vertexJoinStyles.Add(null);  // 新顶点默认继承全局 OutlineJoinStyle

        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>设置指定顶点的接缝样式（null = 继承全局 OutlineJoinStyle）。
    /// UI / 序列化入口。无越界检查（索引越界由调用方负责）。</summary>
    public void SetVertexJoinStyle(int index, JoinStyle? style)
    {
        if (index < 0 || index >= _vertexJoinStyles.Count) return;
        if (_vertexJoinStyles[index] == style) return;
        _vertexJoinStyles[index] = style;
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>读取指定顶点的接缝样式（null = 继承全局 OutlineJoinStyle）。</summary>
    public JoinStyle? GetVertexJoinStyle(int index)
    {
        if (index < 0 || index >= _vertexJoinStyles.Count) return null;
        return _vertexJoinStyles[index];
    }

    /// <summary>清除单个顶点的 per-vertex 接缝样式（恢复继承全局 OutlineJoinStyle）。</summary>
    public void ClearVertexJoinStyle(int index)
    {
        if (index < 0 || index >= _vertexJoinStyles.Count) return;
        if (_vertexJoinStyles[index] == null) return;
        _vertexJoinStyles[index] = null;
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>把所有顶点的逐顶点样式清除回 null（全部恢复继承全局 OutlineJoinStyle）。
    /// 提供给"重置逐顶点覆盖"按钮用。</summary>
    public void ClearVertexJoinStyles()
    {
        for (int i = 0; i < _vertexJoinStyles.Count; i++)
            _vertexJoinStyles[i] = null;
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>逐顶点样式快照（用于序列化）。null 项不写入。</summary>
    internal IReadOnlyList<JoinStyle?> VertexJoinStylesSnapshot => _vertexJoinStyles;

    /// <summary>从序列化数据恢复逐顶点样式（数量对齐 VertexBandWidths，不足补 null）。</summary>
    internal void RestoreVertexJoinStyles(IReadOnlyList<JoinStyle?>? snapshot)
    {
        // 先清空所有覆盖
        for (int i = 0; i < _vertexJoinStyles.Count; i++)
            _vertexJoinStyles[i] = null;
        // 再按索引恢复
        if (snapshot == null) return;
        int n = Math.Min(snapshot.Count, _vertexJoinStyles.Count);
        for (int i = 0; i < n; i++)
            _vertexJoinStyles[i] = snapshot[i];
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>单顶点 Incoming/Outgoing 变化时刷新 OutlineGeometry。</summary>
    private void OnVertexBandWidthChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(OutlineGeometry));
        // 带宽变了 → 带宽线端点位置也要变（按段方向法线 × 半带宽），通知 ViewModel 刷新 BandWidthHandles/BandWidthLines
        OnPropertyChanged(nameof(SelectionGeometry));
        VertexBandWidthDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>任意 VertexBandWidth 的 Incoming/Outgoing 带宽改变事件。
    /// ViewModel 订阅后刷新 BandWidthHandles（端点坐标）+ BandWidthLines（画布左右虚线）。</summary>
    public event EventHandler? VertexBandWidthDataChanged;

    /// <summary>PathData 变化时刷新 OutlineGeometry（顶点改了，轮廓也要重算）和 PolylineGeometry（纯折线骨架）。
    /// BooleanPath 多环骨架 / Polyline 闭合形态：骨架点/环数变化时同步 VertexBandWidths（圆角半径容器）。</summary>
    partial void OnPathDataChanged(string value)
    {
        // 重建 PathData 解析缓存（选区包围盒/命中检测复用,避免每帧重复 Split+TryParse）
        _parsedPathPoints = string.IsNullOrEmpty(value)
            ? null
            : Converters.BandOutlineBuilder.ParsePathData(value);
        if (!string.IsNullOrEmpty(value))
        {
            if (TypeKey == nameof(L1Primitive.BooleanPath))
            {
                // 骨架点数 = 各环"段链起点+终点"去重后的总数（段端点，弧段不摊平）
                SyncVertexBandWidths(Converters.BooleanOps.GetSkeletonCount(value), 0);
            }
            else if (IsClosed && TypeKey == nameof(L1Primitive.Polyline) && !string.IsNullOrEmpty(value))
                SyncVertexBandWidths(Converters.BandOutlineBuilder.ParsePathData(value).Count, 0);
        }
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(PolylineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>整体 BandWidth 变化时同步所有顶点带宽（统一调整），新顶点也用此值。
    /// 顶点 Slider 单独调整时只改 VertexBandWidth.Incoming/Outgoing，不影响整体 BandWidth。</summary>
    partial void OnBandWidthChanged(double value)
    {
        foreach (var v in VertexBandWidths)
        {
            v.IncomingBandWidth = value;
            v.OutgoingBandWidth = value;
        }
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>OutlineJoinStyle 变化时刷新 OutlineGeometry（轮廓走线策略不同）。</summary>
    partial void OnOutlineJoinStyleChanged(JoinStyle value)
    {
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>共线同向直通点拆分模式：骨架两段共线同方向时，
    /// true = 产生两对轮廓点（保留带宽突变台阶，可对每侧独立圆角）；false = 合并为一对（忽略台阶）。
    /// 默认 true，与老版本行为一致。属性面板可切换，持久化到工程文件。</summary>
    [ObservableProperty] private bool _collinearSplitPairs = true;

    partial void OnCollinearSplitPairsChanged(bool value)
    {
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    // 缓存：上次计算的输入 + 输出，避免 WPF Binding 双调用造成的重复 BuildOutline
    private System.Windows.Media.Geometry? _outlineCache;
    private System.Collections.Generic.List<SymbolDesigner.Converters.PointStep>? _cachedSteps;
    private int _cachedStepsVersion;  // 步骤被外部（手柄拖动）就地替换时递增，触发 re-emit
    private string? _cachedPathData;
    private double _cachedBandWidth;
    private string? _cachedOutlineJoinStyle;
    private bool _cachedCollinearSplitPairs = true;
    private bool _cachedIsClosed;
    private int _cachedVTCount;
    private int _cachedVTHash;
    private int _cachedVJSCount;
    private int _cachedVJSHash;

    /// <summary>直接对外暴露缓存的 emit 步骤（合并 lefts+rights 的单条序列）。
    /// 外部（轮廓编辑手柄拖动）可通过修改此集合的某一项后调用 NotifyOutlineGeometryChanged() 触发重绘。
    /// 返回 internal 类型：PointStep 是 internal class，访问性受限于程序集内（仅同程序集可见）。</summary>
    internal System.Collections.Generic.IList<SymbolDesigner.Converters.PointStep>? CachedOutlineSteps => _cachedSteps;

    /// <summary>手柄改步骤后通知轮廓重算（不修改输入参数，只重 emit）。</summary>
    public void NotifyOutlineGeometryChanged()
    {
        // 递增步骤版本号，使 getter 检测到"步骤已变" → 重新 emit（但不再调 BuildOutlineSteps）
        _cachedStepsVersion++;
        OnPropertyChanged(nameof(OutlineGeometry));
        OnPropertyChanged(nameof(SelectionGeometry));
    }

    /// <summary>带带宽的闭合轮廓 Geometry（由 PathData + VertexBandWidths 计算）。
    /// Path.Data 直接绑定此属性，无需 MultiBinding，避免 TypeConverter 坑。
    /// VertexBandWidths/PathData/OutlineJoinStyle 任一变化都会触发刷新。
    /// 加缓存：第二次 getter 直接复用第一次结果，避免重复 BuildOutline。
    /// 逐顶点接缝样式：优先用 SetVertexJoinStyle 设置的 per-vertex 值，null 槽位用全局 OutlineJoinStyle。</summary>
    public System.Windows.Media.Geometry? OutlineGeometry
    {
        get
        {
            if (string.IsNullOrEmpty(PathData)) return null;

            // BooleanPath：渲染不走步骤管道——段结构快照 + 逐顶点圆角烘焙为 A 圆弧后 Geometry.Parse 原生渲染
            if (TypeKey == nameof(L1Primitive.BooleanPath))
                return BooleanOutlineGeometry();

            // 检查输入是否变化
            int vtHash = ComputeVTHash();
            int vjsHash = ComputeVJSHash();
            bool isDirty =
                _outlineCache == null
                || _cachedPathData != PathData
                || _cachedBandWidth != BandWidth
                || _cachedOutlineJoinStyle != OutlineJoinStyle.ToString()
                || _cachedCollinearSplitPairs != CollinearSplitPairs
                || _cachedIsClosed != IsClosed
                || _cachedVTCount != VertexBandWidths.Count
                || _cachedVTHash != vtHash
                || _cachedVJSCount != _vertexJoinStyles.Count
                || _cachedVJSHash != vjsHash;

            // 三种情况：
            //   1) 完全脏（输入变了）：重算 BuildOutlineSteps + emit
            //   2) 步骤被外部改（NotifyOutlineGeometryChanged）：仅重 emit
            //   3) 缓存命中：直接返回
            if (!isDirty)
            {
                // 检查步骤版本号（手柄拖动只改 _cachedSteps，不改输入参数）
                // 用一个隐藏标志位：_cachedStepsVersion 跟某个本地变量对比
                // 这里我们在 NotifyOutlineGeometryChanged 里递增版本号，命中时仍要 re-emit
                // 简化：版本号变化 → 走"仅 emit"分支
                if (_cachedSteps != null && _lastEmittedStepsVersion == _cachedStepsVersion)
                    return _outlineCache;  // 步骤也没变，纯缓存命中
            }

            // 重新 emit 缓存步骤（不重算几何）
            if (!isDirty && _cachedSteps != null)
            {
                _outlineCache = EmitSteps(_cachedSteps);
                _lastEmittedStepsVersion = _cachedStepsVersion;
                return _outlineCache;
            }

            // 重算 + 更新缓存
            List<Converters.PointStep> steps;
            if (IsClosed && TypeKey == nameof(L1Primitive.Polyline))
            {
                // Polyline 闭合形态：骨架点直连单环（无带宽偏移），复用同一个 emit/圆角/手柄管道。
                // 顶点圆角半径存 VertexBandWidths[vi].LeftCornerRadius（单环无左右侧）。
                steps = Converters.BandOutlineBuilder.BuildClosedShapeSteps(PathData, ToVertexBandWidthArray());
            }
            else if (VertexBandWidths.Count == 0)
                steps = Converters.BandOutlineBuilder.BuildOutlineSteps(PathData, BandWidth, CollinearSplitPairs);
            else
            {
                var vs = new VertexBandWidth[VertexBandWidths.Count];
                for (int i = 0; i < VertexBandWidths.Count; i++) vs[i] = VertexBandWidths[i];

                // 计算 effective JoinStyle[]（null 用全局 OutlineJoinStyle 替换）
                int n = vs.Length;
                var jsArr = new JoinStyle[n];
                for (int i = 0; i < n; i++)
                    jsArr[i] = _vertexJoinStyles[i] ?? OutlineJoinStyle;

                steps = Converters.BandOutlineBuilder.BuildOutlineSteps(PathData, vs, jsArr, CollinearSplitPairs);
            }

            // 把步骤合成 Geometry 缓存
            _cachedSteps = steps;
            _cachedStepsVersion++;  // 列表整体换了，版本递增
            _lastEmittedStepsVersion = _cachedStepsVersion;
            _outlineCache = EmitSteps(steps);
            _cachedPathData = PathData;
            _cachedBandWidth = BandWidth;
            _cachedOutlineJoinStyle = OutlineJoinStyle.ToString();
            _cachedCollinearSplitPairs = CollinearSplitPairs;
            _cachedIsClosed = IsClosed;
            _cachedVTCount = VertexBandWidths.Count;
            _cachedVTHash = vtHash;
            _cachedVJSCount = _vertexJoinStyles.Count;
            _cachedVJSHash = vjsHash;
            return _outlineCache;
        }
    }

    /// <summary>布尔结果渲染：段结构快照 + 逐顶点圆角半径（LeftCornerRadius）+ per-vertex 样式
    /// （Miter/Bevel 该角不烘焙保持尖角，Round 才烘焙半径为 A 命令真圆弧），
    /// 再 Geometry.Parse 原生渲染（F0 填充规则，弧段保留——圆/椭圆边界 = 圆弧，圆弧上零中间点）。
    /// 与步骤管道隔离：骨架点（段端点）不可拖拽，渲染每次从这里重建（样式/半径变化都会命中缓存失效）。
    /// 缓存由 PathData / VertexBandWidths / _vertexJoinStyles 内容驱动，命中直接返回。</summary>
    private System.Windows.Media.Geometry? BooleanOutlineGeometry()
    {
        int vtHash = ComputeVTHash();
        int vjsHash = ComputeVJSHash();
        bool dirty = _outlineCache == null
            || _cachedPathData != PathData
            || _cachedVTCount != VertexBandWidths.Count
            || _cachedVTHash != vtHash
            || _cachedVJSCount != _vertexJoinStyles.Count
            || _cachedVJSHash != vjsHash;
        if (!dirty) return _outlineCache;

        int n = VertexBandWidths.Count;
        var radii = new double[n];
        var styles = new JoinStyle[n];
        for (int i = 0; i < n; i++)
        {
            radii[i] = VertexBandWidths[i].LeftCornerRadius;
            styles[i] = _vertexJoinStyles[i] ?? OutlineJoinStyle;  // null 槽位继承全局样式
        }
        string baked = Converters.BooleanOps.BakeCornerRadii(PathData, radii, styles);
        System.Windows.Media.Geometry? g = null;
        if (!string.IsNullOrEmpty(baked))
        {
            try
            {
                g = System.Windows.Media.Geometry.Parse(baked);
                g.Freeze();
            }
            catch (System.FormatException)
            {
                g = null; // 快照数据损坏 → 退化为不可见，不向 UI 抛异常
            }
        }
        _outlineCache = g;
        _cachedPathData = PathData;
        _cachedVTCount = VertexBandWidths.Count;
        _cachedVTHash = vtHash;
        _cachedVJSCount = _vertexJoinStyles.Count;
        _cachedVJSHash = vjsHash;
        return g;
    }

    /// <summary>把 VertexBandWidths 转成数组（无则返回 null）。</summary>
    private VertexBandWidth[]? ToVertexBandWidthArray()
    {
        if (VertexBandWidths.Count == 0) return null;
        var vs = new VertexBandWidth[VertexBandWidths.Count];
        for (int i = 0; i < VertexBandWidths.Count; i++) vs[i] = VertexBandWidths[i];
        return vs;
    }

    private int _lastEmittedStepsVersion;  // 上次 emit 时对应的步骤版本号

    /// <summary>把 emit 步骤序列拼成 StreamGeometry（多环布尔结果按 FigureIndex 逐环开 figure，委托给 BandOutlineBuilder）。</summary>
    private static System.Windows.Media.Geometry EmitSteps(System.Collections.Generic.List<SymbolDesigner.Converters.PointStep> steps)
        => Converters.BandOutlineBuilder.EmitSteps(steps);

    /// <summary>VertexBandWidths 内容的轻量 hash（Incoming/Outgoing + 三类圆角字段）。
    /// 用于检测带宽数组内容变化（不只数量）。圆角字段参与 hash 是缓存失效的前提：
    /// 只改 Left/RightCornerRadius 或 JoinInnerRadius 而不让 hash 变化 → isDirty=false → 视觉不更新。</summary>
    private int ComputeVTHash()
    {
        int h = 0;
        foreach (var v in VertexBandWidths)
        {
            h = unchecked(h * 31 + v.IncomingBandWidth.GetHashCode());
            h = unchecked(h * 31 + v.OutgoingBandWidth.GetHashCode());
            h = unchecked(h * 31 + v.JoinInnerRadius.GetHashCode());
            h = unchecked(h * 31 + v.LeftCornerRadius.GetHashCode());
            h = unchecked(h * 31 + v.RightCornerRadius.GetHashCode());
        }
        return h;
    }

    /// <summary>_vertexJoinStyles 内容的轻量 hash（null 项 hash = 0）。
    /// 用于检测逐顶点样式数组内容变化。</summary>
    private int ComputeVJSHash()
    {
        int h = 0;
        for (int i = 0; i < _vertexJoinStyles.Count; i++)
        {
            var js = _vertexJoinStyles[i];
            int jsH = js.HasValue ? js.Value.GetHashCode() : 0;
            h = unchecked(h * 31 + jsH);
        }
        return h;
    }

    /// <summary>轨迹线 Path 字符串（与 PathData 相同，仅作为便于绑定的属性别名）。</summary>
    public string TrajectoryPathData => PathData ?? string.Empty;

    /// <summary>图元是否可填充。属性面板"图案填充"分支只对可填充图元启用：
    ///   - Line（无填充区域）
    ///   - BandPolyline（带宽轮廓开放形体：fill 为 null，图案平铺无意义）
    /// 其余类型（Rectangle/Ellipse/RoundedRectangle/Triangle/Ring/BooleanPath/Polyline）都允许。
    /// Polyline 开放时虽然 CanvasFillGeometry 返回 null，但图案分支仍可用——用户切到"图案"后
    /// 再勾选"闭合"即可看到效果；这样避免 UI 因 IsClosed 抖动。
    /// 公开属性而非内部标志：UI 绑定 + 序列化层不依赖，但属性面板靠它决定图案分支是否启用。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFillable => TypeKey is not nameof(L1Primitive.Line)
        and not nameof(L1Primitive.BandPolyline);

    partial void OnTypeKeyChanged(string value) => OnPropertyChanged(nameof(IsFillable));

    /// <summary>纯折线（Polyline）专用几何：解析 PathData 为开放骨架的 StreamGeometry，仅描边无填充。
    /// 与 BandPolyline 的 OutlineGeometry 严格分离——OutlineGeometry 走"带宽偏移/接缝"逻辑（仅 BandPolyline 用），
    /// PolylineGeometry 只为 Polyline 提供纯 stroke 渲染的 Geometry，完全绕开轮廓计算。
    /// 模板绑定：L1PolylineNode 绑此属性，L1BandPolylineNode 绑 OutlineGeometry。
    /// PathData 变化时刷新（见 OnPathDataChanged）。
    /// 缓存：拖动（X/Y 变化）会触发 SelectionGeometry → 本 getter 每帧被取；开放 Polyline 顶点多时
    /// 若每次重建 StreamGeometry 会很卡。故按 PathData 缓存结果（局部坐标几何，X/Y 不动不失效）。</summary>
    public System.Windows.Media.Geometry? PolylineGeometry
    {
        get
        {
            if (string.IsNullOrEmpty(PathData))
            {
                _polylineCache = null;
                _cachedPolylinePath = null;
                return null;
            }
            // PathData 未变则复用缓存（几何只由 PathData 决定，与 X/Y 无关）
            if (_polylineCache is not null && _cachedPolylinePath == PathData)
                return _polylineCache;
            var pts = ParsedPathPoints;   // OnPathDataChanged 已缓存解析结果，避免重复 Split+TryParse
            if (pts is null || pts.Count < 2) return null;  // 至少两个点才能形成线段
            var stream = new System.Windows.Media.StreamGeometry();
            using (var ctx = stream.Open())
            {
                ctx.BeginFigure(pts[0], isFilled: false, isClosed: false);
                for (int i = 1; i < pts.Count; i++)
                    ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: false);
            }
            stream.Freeze();
            _polylineCache = stream;
            _cachedPolylinePath = PathData;
            return _polylineCache;
        }
    }

    /// <summary>以画布坐标表示的"闭合可填充几何"（图元局部几何 + X/Y 平移，Freeze 后返回）。
    /// 布尔运算（合并/剪除/相交）的输入：只对"有面积"的闭合图元返回非 null；
    /// Line / 开放 Polyline 无面积 → 返回 null，不可参与布尔。</summary>
    public System.Windows.Media.Geometry? CanvasFillGeometry
    {
        get
        {
            System.Windows.Media.Geometry? local = TypeKey switch
            {
                nameof(L1Primitive.Rectangle)
                    => new System.Windows.Media.RectangleGeometry(new Rect(0, 0, Width, Height)),
                nameof(L1Primitive.Ellipse)
                    => new System.Windows.Media.EllipseGeometry(new System.Windows.Point(Width / 2, Height / 2),
                        Math.Max(0.1, Width / 2), Math.Max(0.1, Height / 2)),
                nameof(L1Primitive.RoundedRectangle)
                    => new System.Windows.Media.RectangleGeometry(new Rect(0, 0, Width, Height),
                        Math.Max(0, CornerRadius), Math.Max(0, CornerRadius)),
                nameof(L1Primitive.Triangle) => BuildTriangleGeometry(),
                nameof(L1Primitive.Ring) => RingGeometry,
                nameof(L1Primitive.Pie) => PieGeometry,
                nameof(L1Primitive.Polyline) when IsClosed => OutlineGeometry,
                nameof(L1Primitive.BandPolyline) => OutlineGeometry,
                nameof(L1Primitive.BooleanPath) => OutlineGeometry,  // 布尔结果 = 多环闭合骨架，走 OutlineGeometry 管道
                _ => null,
            };
            if (local is null) return null;
            // 局部几何平移到画布坐标；Clone 避免污染缓存几何（RingGeometry / OutlineGeometry 有缓存）
            var translated = local.Clone();
            translated.Transform = new System.Windows.Media.TranslateTransform(X, Y);
            translated.Freeze();
            return translated;
        }
    }

    /// <summary>三角形的局部填充几何：顶点 (0.5W,0) 上中，(W,H) 右下，(0,H) 左下（与模板 Polygon 0.5,0/1,1/0,1 Stretch=Fill 对应）。</summary>
    private System.Windows.Media.PathGeometry BuildTriangleGeometry()
    {
        var pg = new System.Windows.Media.PathGeometry();
        var fig = new System.Windows.Media.PathFigure
        {
            StartPoint = new System.Windows.Point(Width * 0.5, 0),
            IsClosed = true,
            IsFilled = true,
        };
        fig.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(Width, Height), true));
        fig.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(0, Height), true));
        pg.Figures.Add(fig);
        pg.Freeze();
        return pg;
    }

    /// <summary>圆环专用描边半厚度（= StrokeThickness/2）。几何半径外圆须 - 此值，
    /// 让描边外扩（0.4）恰好落在 Path 容器边界上 —— 否则描边一半被边界裁切（视觉"被切掉一点"）。
    /// 改 L1RingNode.StrokeThickness 时务必同步调此值。</summary>
    public const double RingStrokeHalfThickness = 0.4;

    /// <summary>圆环几何：外圆顺时钟 + 内圆逆时钟的 Nonzero 单 Path，内圆天然空洞。
    /// 使用**局部坐标**（以元素 X/Y 为原点）——模板已通过 Canvas.Left/Top 定位到 X/Y，
    /// 几何坐标再从 X/Y 中心算会双重偏移。圆心 = (Width/2, Height/2)，
    /// 外径（几何半径）= min(Width,Height)/2 - RingStrokeHalfThickness，
    /// 内径 = InnerRadius。
    /// Width/Height/InnerRadius 任一变化都会触发刷新（见 OnXChanged 等钩子）。</summary>
    public System.Windows.Media.Geometry? RingGeometry
    {
        get
        {
            if (Width <= 0 || Height <= 0 || InnerRadius <= 0) return null;
            double cx = Width / 2;
            double cy = Height / 2;
            // 外圆几何半径比容器短 RingStrokeHalfThickness（= StrokeThickness/2），
            // 让 Stroke 默认居中（内外各 StrokeHalfThickness）时，描边外扩恰好填到容器边界上。
            double outer = System.Math.Min(Width, Height) / 2 - RingStrokeHalfThickness;
            if (outer <= 0.01) return null; // 容器太小没空间画描边
            double inner = System.Math.Clamp(InnerRadius, 0.01, outer - 0.01);

            var stream = new System.Windows.Media.StreamGeometry
            {
                // Nonzero：外圆顺 + 内圆逆 = 环带填充、内圆空洞
                FillRule = System.Windows.Media.FillRule.Nonzero,
            };
            using (var ctx = stream.Open())
            {
                // 外圆：顺时钟（两个半圆弧，起点→+r→-r）
                ctx.BeginFigure(new System.Windows.Point(cx - outer, cy), true, true);
                ctx.ArcTo(new System.Windows.Point(cx + outer, cy), new System.Windows.Size(outer, outer), 0, false,
                    System.Windows.Media.SweepDirection.Clockwise, true, false);
                ctx.ArcTo(new System.Windows.Point(cx - outer, cy), new System.Windows.Size(outer, outer), 0, false,
                    System.Windows.Media.SweepDirection.Clockwise, true, false);

                // 内圆：逆时钟（起点→+r→-r）
                ctx.BeginFigure(new System.Windows.Point(cx - inner, cy), true, true);
                ctx.ArcTo(new System.Windows.Point(cx + inner, cy), new System.Windows.Size(inner, inner), 0, false,
                    System.Windows.Media.SweepDirection.Counterclockwise, true, false);
                ctx.ArcTo(new System.Windows.Point(cx - inner, cy), new System.Windows.Size(inner, inner), 0, false,
                    System.Windows.Media.SweepDirection.Counterclockwise, true, false);
            }
            stream.Freeze();
            return stream;
        }
    }

    /// <summary>选中装饰几何：贴合图元实际渲染形状的轮廓（局部坐标，以元素 X/Y 为原点，
    /// 由模板 Canvas.Left/Top 负责定位）。按 TypeKey 复用既有几何：
    /// 矩形/圆角→RectangleGeometry、椭圆→EllipseGeometry、三角→BuildTriangleGeometry、
    /// 圆环→RingGeometry、折线→PolylineGeometry、带状折线/布尔→OutlineGeometry。
    /// 与本体渲染共用同一几何 → 选中虚线永远贴形（圆是圆、三角是三角、不规则轮廓走骨架）。
    /// 相关字段（Width/Height/PathData/CornerRadius 等）变化时触发通知。 </summary>
    public System.Windows.Media.Geometry? SelectionGeometry => TypeKey switch
    {
        nameof(L1Primitive.Rectangle)
            => new System.Windows.Media.RectangleGeometry(new Rect(0, 0, Width, Height)),
        nameof(L1Primitive.RoundedRectangle)
            => new System.Windows.Media.RectangleGeometry(new Rect(0, 0, Width, Height),
                Math.Max(0, CornerRadius), Math.Max(0, CornerRadius)),
        nameof(L1Primitive.Ellipse)
            => new System.Windows.Media.EllipseGeometry(new System.Windows.Point(Width / 2, Height / 2),
                Math.Max(0.1, Width / 2), Math.Max(0.1, Height / 2)),
        nameof(L1Primitive.Triangle) => BuildTriangleGeometry(),
        nameof(L1Primitive.Ring) => RingGeometry,
        nameof(L1Primitive.Line) => BuildLineSelectionGeometry(),
        nameof(L1Primitive.Polyline) when IsClosed => OutlineGeometry,
        nameof(L1Primitive.Polyline) => PolylineGeometry,
        nameof(L1Primitive.BandPolyline) => OutlineGeometry,
        nameof(L1Primitive.BooleanPath) => OutlineGeometry,
        _ => null,
    };

    /// <summary>直线选中几何：有 PathData（拖动画线）用折线骨架；无 PathData 按 LineAxis
    /// 生成对应线段（Free=对角线 / Horizontal=水平中线 / Vertical=竖直中线），贴形不走矩形框。</summary>
    private System.Windows.Media.Geometry? BuildLineSelectionGeometry()
    {
        if (!string.IsNullOrEmpty(PathData))
            return PolylineGeometry;
        var (x1, y1, x2, y2) = LineAxis switch
        {
            LineAxisMode.Horizontal => (0.0, Height / 2, Width, Height / 2),
            LineAxisMode.Vertical => (Width / 2, 0.0, Width / 2, Height),
            _ => (0.0, Height, Width, 0.0), // Free = 对角斜线
        };
        var stream = new System.Windows.Media.StreamGeometry();
        using (var ctx = stream.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(x1, y1), isFilled: false, isClosed: false);
            ctx.LineTo(new System.Windows.Point(x2, y2), isStroked: true, isSmoothJoin: false);
        }
        stream.Freeze();
        return stream;
    }

    /// <summary>按顶点数同步 VertexBandWidths 数量（新增顶点用 defaultBandWidth 填充）。
    /// 顶点数变化时调用，保留已有顶点带宽。新增顶点根据位置（首/中/末）设置 IsStart/IsEnd。</summary>
    public void SyncVertexBandWidths(int vertexCount, double defaultBandWidth)
    {
        // 增加顶点
        while (VertexBandWidths.Count < vertexCount)
        {
            int idx = VertexBandWidths.Count;
            bool isStart = idx == 0;
            bool isEnd = idx == vertexCount - 1;
            VertexBandWidths.Add(new ViewModels.VertexBandWidth(
                idx, defaultBandWidth, defaultBandWidth, isStart, isEnd));
        }
        // 删除多余顶点
        while (VertexBandWidths.Count > vertexCount)
            VertexBandWidths.RemoveAt(VertexBandWidths.Count - 1);
        // 修复末顶点的 IsEnd 标记（顶点数变化时末顶点位置变）
        for (int i = 0; i < VertexBandWidths.Count; i++)
        {
            var v = VertexBandWidths[i];
            bool shouldBeStart = i == 0;
            bool shouldBeEnd = i == VertexBandWidths.Count - 1;
            if (v.IsStart != shouldBeStart || v.IsEnd != shouldBeEnd)
                {
                    // IsStart/IsEnd 是只读属性，需重建对象（保留其他字段）
                    VertexBandWidths[i] = new ViewModels.VertexBandWidth(
                        i, v.IncomingBandWidth, v.OutgoingBandWidth, shouldBeStart, shouldBeEnd)
                    {
                        JoinInnerRadius = v.JoinInnerRadius,
                        LeftCornerRadius = v.LeftCornerRadius,
                        RightCornerRadius = v.RightCornerRadius,
                    };
                }
        }
    }

    public static PrimitiveItem Create(L1Primitive primitive, double x, double y, double w, double h)
    {
        var item = new PrimitiveItem
        {
            TypeKey = primitive.ToString(),
            X = x, Y = y, Width = w, Height = h,
        };
        // 默认圆角：RoundedRectangle 预设 4px；其他 0
        if (primitive == L1Primitive.RoundedRectangle)
            item.CornerRadius = 4;
        return item;
    }
}