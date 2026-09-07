using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>画布尺寸 / 缩放平移 / 背景参考图状态（描图用，仅在编辑器内显示，不导出到 XAML）。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>画布宽度（逻辑坐标）。默认 1920(16:9)，可从工程文件加载。</summary>
    [ObservableProperty] private double _canvasWidth = 1920;

    /// <summary>画布高度（逻辑坐标）。默认 1080，可从工程文件加载。</summary>
    [ObservableProperty] private double _canvasHeight = 1080;

    /// <summary>画布缩放倍率（0.25 ~ 4.0）。</summary>
    [ObservableProperty] private double _zoomLevel = 1.0;

    /// <summary>画布平移 X（缩放后，拖动平移）。</summary>
    [ObservableProperty] private double _panX = 0;

    /// <summary>画布平移 Y。</summary>
    [ObservableProperty] private double _panY = 0;

    /// <summary>当前画布实际渲染缩放系数（autoScale × ZoomLevel，由 ZoomPanViewport.CurrentScale 同步）。
    /// 预览线/预览端点做"视觉恒定反缩放"用：线宽、端点尺寸除以它，任何缩放下屏幕尺寸不变。
    /// 放在 VM 上是因 XAML Style Setter 内不能用 ElementName 绑定，只能绑 DataContext 属性。</summary>
    [ObservableProperty] private double _canvasScale = 1.0;

    // ============== 状态栏只读信息（CAD 风格：缩放/平移/视口/渲染/光标位置，实时刷新） ==============

    /// <summary>视口物理像素宽（= ZoomPanViewport.ActualWidth，中间列实际宽度）。code-behind 同步。</summary>
    [ObservableProperty] private double _viewportWidth = 0;

    /// <summary>视口物理像素高。</summary>
    [ObservableProperty] private double _viewportHeight = 0;

    /// <summary>画布渲染像素宽（= CanvasWidth × CanvasScale）。</summary>
    [ObservableProperty] private double _renderWidth = 0;

    /// <summary>画布渲染像素高。</summary>
    [ObservableProperty] private double _renderHeight = 0;

    /// <summary>画布在视口内的物理左偏移（真实"左边距"，px）= 自动居中偏移 cx + 用户平移 PanX。
    /// code-behind 根据视口尺寸/缩放/平移实时计算。打开即反映画布相对视口的实际位置，不再固定 0。</summary>
    [ObservableProperty] private double _canvasLeft = 0;

    /// <summary>画布在视口内的物理上偏移（真实"上边距"，px）= 自动居中偏移 cy + 用户平移 PanY。</summary>
    [ObservableProperty] private double _canvasTop = 0;

    /// <summary>鼠标在画布逻辑坐标系下的 X 坐标（= (屏幕 X - 居中偏移 - PanX) / scale）。
    /// code-behind 在 CanvasHost.MouseMove 时同步。null = 鼠标不在画布上。</summary>
    [ObservableProperty] private double? _cursorX;

    /// <summary>鼠标在画布逻辑坐标系下的 Y 坐标。</summary>
    [ObservableProperty] private double? _cursorY;

    // ============== 画布背景色(用户可切换;默认跟随主题 Brush.Bg.Canvas) ==============

    /// <summary>画布背景颜色(hex)。默认 = 主题 Brush.Bg.Canvas 色值。
    /// 用户可切换成白/黑/浅色等,便于在不同底色下观察图元
    /// (例如深色 SVG 导入后画布上看不见时,切浅色背景即可看见)。
    /// 绑定:CanvasHost.Background 经 HexToBrushConverter 转换;空白画布点击判定不受影响。</summary>
    [ObservableProperty] private string _canvasBackgroundColor = ResolveDefaultCanvasColor();

    /// <summary>画布背景画刷(绑定 CanvasHost.Background)。颜色变化时实时刷新。</summary>
    public System.Windows.Media.Brush? CanvasBackgroundBrush => Converters.ColorUtil.ParseHex(CanvasBackgroundColor);

    partial void OnCanvasBackgroundColorChanged(string value)
    {
        OnPropertyChanged(nameof(CanvasBackgroundBrush));
        IsDirty = true;  // 画布背景是工程全局设置,改动即标脏
    }

    /// <summary>解析默认画布背景色:优先取主题资源 Brush.Bg.Canvas 的实际色值,
    /// 主题缺失时的兜底写死与 Theme.xaml 一致的色值(仅为兜底,正常时用主题值)。</summary>
    private static string ResolveDefaultCanvasColor()
    {
        var app = System.Windows.Application.Current;
        if (app is not null
            && app.TryFindResource("Brush.Bg.Canvas") is System.Windows.Media.SolidColorBrush b)
        {
            return b.Color.ToString();
        }
        return "#2A2A2A"; // 兜底:与 Themes/Theme.xaml Brush.Bg.Canvas 保持一致
    }

    /// <summary>把画布背景重置回主题默认色(属性面板"画布背景"组的重置按钮)。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetCanvasBackground() => CanvasBackgroundColor = ResolveDefaultCanvasColor();

    // ============== 背景参考图（描图用的底层图片，仅在编辑器内显示，不导出到 XAML） ==============

    /// <summary>背景参考图文件路径（绝对路径）。null = 未导入。
    /// 改变时同步刷新 BackgroundImageSource，供 XAML 绑定。</summary>
    [ObservableProperty] private string? _backgroundImagePath;

    /// <summary>背景参考图的 ImageSource（WPF 直接绑定此属性渲染 Image）。
    /// null = 不显示。导入时构造 BitmapImage；清除时置 null。</summary>
    [ObservableProperty] private ImageSource? _backgroundImageSource;

    /// <summary>背景参考图透明度（0.0 ~ 1.0），默认 0.5（半透明显而易见）。</summary>
    [ObservableProperty] private double _backgroundOpacity = 0.5;

    /// <summary>背景参考图对齐方式："CanvasSize" 按画布尺寸适配；"ImageSize" 按图片原始尺寸 1:1 显示。
    /// 默认 CanvasSize（图片自动适配到画布大小，便于按比例描图）。</summary>
    [ObservableProperty] private string _backgroundFitMode = "CanvasSize";

    /// <summary>是否正在显示背景参考图（控制 XAML 可见性）。</summary>
    public bool HasBackgroundImage => BackgroundImageSource != null;

    partial void OnBackgroundImagePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasBackgroundImage));
    }

    partial void OnBackgroundImageSourceChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasBackgroundImage));
        IsDirty = true;
    }

    partial void OnBackgroundOpacityChanged(double value) => IsDirty = true;
    partial void OnBackgroundFitModeChanged(string value) => IsDirty = true;

    // 画布尺寸变化时,属性面板的"带宽" Slider 上限需重算(依赖短边)
    partial void OnCanvasWidthChanged(double value) => RecomputeSelectedBandWidthMax();
    partial void OnCanvasHeightChanged(double value) => RecomputeSelectedBandWidthMax();

    // ============== 屏幕恒定尺寸 token（反缩放后） ==============
    // 用途：任何缩放下，手柄/提示线在屏幕上的物理尺寸恒为 token 值（屏幕像素）。
    // 由 CanvasScale 变化时统一重算并 RaisePropertyChanged，
    // XAML 引用方式：Width="{Binding HandleSizeM}"、StrokeThickness="{Binding StrokeWidthM}" 等，
    // 不再每处手写 {Binding CanvasScale, Converter={StaticResource DivByScale}, ConverterParameter=X.X}。

    // ── 手柄边长（画布坐标） ──
    // 档位含义：XS=草稿端点/小标记；S=顶点/普通轮廓点；M=段中点/圆环/选区缩放角；L=转角强调点
    private double _handleSizeXs = 2.0;
    public double HandleSizeXs => _handleSizeXs;
    private double _handleSizeS  = 3.0;
    public double HandleSizeS  => _handleSizeS;
    private double _handleSizeM  = 3.5;
    public double HandleSizeM  => _handleSizeM;
    private double _handleSizeL  = 5.0;
    public double HandleSizeL  => _handleSizeL;

    // ── 描边粗细（画布坐标） ──
    // 档位含义：XS=极细提示线（对象追踪贯穿虚线）；S=默认预览（草稿轨迹/选中装饰）；
    // M=强调预览（轴对齐激活）；L=粗选中装饰（MultiDataTrigger IsArraySourceClone）
    private double _strokeWidthXs = 0.4;
    public double StrokeWidthXs => _strokeWidthXs;
    private double _strokeWidthS  = 0.5;
    public double StrokeWidthS  => _strokeWidthS;
    private double _strokeWidthM  = 1.0;
    public double StrokeWidthM  => _strokeWidthM;
    private double _strokeWidthL  = 2.0;
    public double StrokeWidthL  => _strokeWidthL;
    // 选中装饰 MultiDataTrigger 用 3.0（粗体强调），与 L 同源但更粗
    private double _strokeWidthXl = 3.0;
    public double StrokeWidthXl => _strokeWidthXl;

    // ── 虚线 DashArray（画布坐标，已除 scale） ──
    // 档位含义：XS=对象追踪贯穿虚线（密）；S=选中装饰虚线；M=草稿轨迹（疏）
    private DoubleCollection _dashXs = new() { 2.0, 2.0 };
    public DoubleCollection DashXs => _dashXs;
    private DoubleCollection _dashS  = new() { 2.0, 1.4 };
    public DoubleCollection DashS  => _dashS;
    private DoubleCollection _dashM  = new() { 1.6, 1.2 };
    public DoubleCollection DashM  => _dashM;

    // ── 反缩放手柄的 RenderTransform 缩放系数（= 1/CanvasScale） ──
    // XAML 给手柄 / 提示线套 ScaleTransform 时直接绑这个，免去每处写 InvScale 转换器。
    private double _inverseScale = 1.0;
    public double InverseScale => _inverseScale;

    /// <summary>CanvasScale 变化时：所有 token 重新按 1/scale 计算并 RaisePropertyChanged。</summary>
    partial void OnCanvasScaleChanged(double value)
    {
        double s = value > 1e-9 ? value : 1.0;
        double inv = 1.0 / s;
        bool changed = false;
        changed |= SetIfDifferent(ref _handleSizeXs, 2.0  * inv);
        changed |= SetIfDifferent(ref _handleSizeS,  3.0  * inv);
        changed |= SetIfDifferent(ref _handleSizeM,  3.5  * inv);
        changed |= SetIfDifferent(ref _handleSizeL,  5.0  * inv);
        changed |= SetIfDifferent(ref _strokeWidthXs, 0.4 * inv);
        changed |= SetIfDifferent(ref _strokeWidthS,  0.5 * inv);
        changed |= SetIfDifferent(ref _strokeWidthM,  1.0 * inv);
        changed |= SetIfDifferent(ref _strokeWidthL,  2.0 * inv);
        changed |= SetIfDifferent(ref _strokeWidthXl, 3.0 * inv);
        changed |= SetIfDifferent(ref _inverseScale,  inv);
        // DashArray 是 DoubleCollection，整体替换（XAML 绑集合不会原地刷新）
        _dashXs = new DoubleCollection { 2.0 * inv, 2.0 * inv };
        _dashS  = new DoubleCollection { 2.0 * inv, 1.4 * inv };
        _dashM  = new DoubleCollection { 1.6 * inv, 1.2 * inv };
        if (changed)
        {
            OnPropertyChanged(nameof(HandleSizeXs));
            OnPropertyChanged(nameof(HandleSizeS));
            OnPropertyChanged(nameof(HandleSizeM));
            OnPropertyChanged(nameof(HandleSizeL));
            OnPropertyChanged(nameof(StrokeWidthXs));
            OnPropertyChanged(nameof(StrokeWidthS));
            OnPropertyChanged(nameof(StrokeWidthM));
            OnPropertyChanged(nameof(StrokeWidthL));
            OnPropertyChanged(nameof(StrokeWidthXl));
            OnPropertyChanged(nameof(InverseScale));
        }
        // 集合属性变化一定要通知（哪怕 changed=false，scale 变了 dash 数值就变了）
        OnPropertyChanged(nameof(DashXs));
        OnPropertyChanged(nameof(DashS));
        OnPropertyChanged(nameof(DashM));
    }

    private static bool SetIfDifferent(ref double field, double newValue)
    {
        if (System.Math.Abs(field - newValue) < 1e-9) return false;
        field = newValue;
        return true;
    }
}