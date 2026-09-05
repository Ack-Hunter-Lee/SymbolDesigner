using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>画布尺寸 / 缩放平移 / 背景参考图状态（描图用，仅在编辑器内显示，不导出到 XAML）。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>画布宽度（逻辑坐标）。默认 240，可从工程文件加载。</summary>
    [ObservableProperty] private double _canvasWidth = 320;

    /// <summary>画布高度（逻辑坐标）。默认 192，可从工程文件加载。</summary>
    [ObservableProperty] private double _canvasHeight = 192;

    /// <summary>画布缩放倍率（0.25 ~ 4.0）。</summary>
    [ObservableProperty] private double _zoomLevel = 1.0;

    /// <summary>画布平移 X（缩放后，拖动平移）。</summary>
    [ObservableProperty] private double _panX = 0;

    /// <summary>画布平移 Y。</summary>
    [ObservableProperty] private double _panY = 0;

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