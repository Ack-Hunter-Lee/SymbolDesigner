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

    // 画布尺寸变化时,属性面板的"厚度" Slider 上限需重算(依赖短边)
    partial void OnCanvasWidthChanged(double value) => RecomputeSelectedThicknessMax();
    partial void OnCanvasHeightChanged(double value) => RecomputeSelectedThicknessMax();
}