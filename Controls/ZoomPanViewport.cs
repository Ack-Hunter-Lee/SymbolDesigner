using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SymbolDesigner.Controls;

/// <summary>
/// 可复用视口控件：把任意内容当作"画布"进行缩放、平移、自动适配与居中。
///
/// 核心能力（全部内置，调用方零 code-behind）：
///   - 自动适配：内容按 FitRatio（默认 0.9）缩放到视口内，最长边占 90%，短边保持比例并居中
///   - 滚轮缩放：以鼠标位置为锚点（EnableWheelZoom 可关）
///   - 中键拖拽平移：按住鼠标中键拖动（EnableMiddleDragPan 可关）
///   - 依赖属性：ContentWidth / ContentHeight / ZoomLevel / PanX / PanY 均可外部绑定
///   - 方法：ZoomToCenter(newZoom) 以内容中心缩放、ResetView() 复位
///
/// 模板在 Themes/Generic.xaml 定义：Border(ClipToBounds) + ContentPresenter(PART_ContentHost)，
/// ContentPresenter 应用 RenderTransform 实现缩放/平移，HorizontalAlignment=Left/Top 保证变换锚点正确。
/// </summary>
public class ZoomPanViewport : ContentControl
{
    static ZoomPanViewport()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(typeof(ZoomPanViewport)));
    }

    /// <summary>内容逻辑宽度（如画布宽 1920）。</summary>
    public static readonly DependencyProperty ContentWidthProperty =
        DependencyProperty.Register(nameof(ContentWidth), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

    /// <summary>内容逻辑高度（如画布高 96）。</summary>
    public static readonly DependencyProperty ContentHeightProperty =
        DependencyProperty.Register(nameof(ContentHeight), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

    /// <summary>用户缩放倍率（1.0 = 按 FitRatio 自动适配尺寸）。</summary>
    public static readonly DependencyProperty ZoomLevelProperty =
        DependencyProperty.Register(nameof(ZoomLevel), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

    /// <summary>平移偏移 X（内容坐标系，叠加在居中偏移之上）。</summary>
    public static readonly DependencyProperty PanXProperty =
        DependencyProperty.Register(nameof(PanX), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(0.0, OnTransformPropertyChanged));

    /// <summary>平移偏移 Y。</summary>
    public static readonly DependencyProperty PanYProperty =
        DependencyProperty.Register(nameof(PanY), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(0.0, OnTransformPropertyChanged));

    /// <summary>自动适配比例：内容最长边占视口的比例（0.9 = 90%）。</summary>
    public static readonly DependencyProperty FitRatioProperty =
        DependencyProperty.Register(nameof(FitRatio), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(0.9, OnTransformPropertyChanged));

    /// <summary>当前实际渲染缩放系数（autoScale × ZoomLevel）。
    /// 只读暴露给 XAML：供预览线/端点做反向缩放，实现"视觉尺寸恒定、不随画布缩放变化"。
    /// 由 UpdateTransform 每次重算时赋值，任何缩放/窗口尺寸变化都会触发绑定刷新。</summary>
    public static readonly DependencyProperty CurrentScaleProperty =
        DependencyProperty.Register(nameof(CurrentScale), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0));

    /// <summary>最小缩放倍率。</summary>
    public static readonly DependencyProperty MinZoomProperty =
        DependencyProperty.Register(nameof(MinZoom), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(0.25));

    /// <summary>最大缩放倍率。</summary>
    public static readonly DependencyProperty MaxZoomProperty =
        DependencyProperty.Register(nameof(MaxZoom), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(4.0));

    /// <summary>是否启用鼠标滚轮缩放（以鼠标位置为锚点）。</summary>
    public static readonly DependencyProperty EnableWheelZoomProperty =
        DependencyProperty.Register(nameof(EnableWheelZoom), typeof(bool), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(true));

    /// <summary>是否启用鼠标中键拖拽平移。</summary>
    public static readonly DependencyProperty EnableMiddleDragPanProperty =
        DependencyProperty.Register(nameof(EnableMiddleDragPan), typeof(bool), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(true));

    public double ContentWidth { get => (double)GetValue(ContentWidthProperty); set => SetValue(ContentWidthProperty, value); }
    public double ContentHeight { get => (double)GetValue(ContentHeightProperty); set => SetValue(ContentHeightProperty, value); }
    public double ZoomLevel { get => (double)GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }
    public double PanX { get => (double)GetValue(PanXProperty); set => SetValue(PanXProperty, value); }
    public double PanY { get => (double)GetValue(PanYProperty); set => SetValue(PanYProperty, value); }
    public double FitRatio { get => (double)GetValue(FitRatioProperty); set => SetValue(FitRatioProperty, value); }
    public double CurrentScale => (double)GetValue(CurrentScaleProperty);
    public double MinZoom { get => (double)GetValue(MinZoomProperty); set => SetValue(MinZoomProperty, value); }
    public double MaxZoom { get => (double)GetValue(MaxZoomProperty); set => SetValue(MaxZoomProperty, value); }
    public bool EnableWheelZoom { get => (bool)GetValue(EnableWheelZoomProperty); set => SetValue(EnableWheelZoomProperty, value); }
    public bool EnableMiddleDragPan { get => (bool)GetValue(EnableMiddleDragPanProperty); set => SetValue(EnableMiddleDragPanProperty, value); }

    private ContentPresenter? _contentHost;
    private bool _isPanning;
    private Point _panStart;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _contentHost = GetTemplateChild("PART_ContentHost") as ContentPresenter;
        UpdateTransform();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateTransform();
    }

    private static void OnTransformPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ZoomPanViewport)d).UpdateTransform();

    /// <summary>重新计算 RenderTransform：自动适配 + 居中 + 用户 Zoom/Pan。</summary>
    private void UpdateTransform()
    {
        if (_contentHost is null) return;
        double cw = ContentWidth;
        double ch = ContentHeight;
        double availW = ActualWidth;
        double availH = ActualHeight;
        if (cw <= 0 || ch <= 0 || availW <= 0 || availH <= 0) return;

        double autoScale = Math.Min(availW * FitRatio / cw, availH * FitRatio / ch);
        double scale = autoScale * ZoomLevel;
        double cx = (availW - cw * scale) * 0.5;
        double cy = (availH - ch * scale) * 0.5;

        // 暴露实际渲染缩放系数,供上层做"反缩放"(预览线/端点视觉恒定)
        if (Math.Abs(CurrentScale - scale) > 1e-9) SetValue(CurrentScaleProperty, scale);

        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(scale, scale));
        tg.Children.Add(new TranslateTransform(cx + PanX, cy + PanY));
        _contentHost.RenderTransform = tg;
    }

    /// <summary>鼠标滚轮：以鼠标位置为锚点缩放。
    /// e.GetPosition(this) 返回控件本地坐标（不含内容 RenderTransform，因为它是后代变换），
    /// 即"屏幕坐标"，直接作为缩放锚点。</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!EnableWheelZoom) return;
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        double newZoom = Math.Clamp(ZoomLevel * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - ZoomLevel) < 0.001) return;

        var p = e.GetPosition(this);
        ZoomToScreenPoint(newZoom, p.X, p.Y);
        e.Handled = true;
    }

    /// <summary>鼠标中键按下：开始拖拽平移。</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (EnableMiddleDragPan && e.ChangedButton == MouseButton.Middle)
        {
            _isPanning = true;
            _panStart = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }
    }

    /// <summary>拖拽中：累加 PanX/PanY。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isPanning) return;
        var p = e.GetPosition(this);
        PanX += p.X - _panStart.X;
        PanY += p.Y - _panStart.Y;
        _panStart = p;
        e.Handled = true;
    }

    /// <summary>鼠标中键松开：结束拖拽平移。</summary>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isPanning && e.ChangedButton == MouseButton.Middle)
        {
            _isPanning = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>以内容中心为锚点缩放到指定倍率（供工具栏 +/− 按钮调用）。</summary>
    public void ZoomToCenter(double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        double availW = ActualWidth;
        double availH = ActualHeight;
        if (availW <= 0 || availH <= 0) return;
        ZoomToScreenPoint(newZoom, availW * 0.5, availH * 0.5);
    }

    /// <summary>复位视图：缩放到自动适配基准，清除平移。</summary>
    public void ResetView()
    {
        ZoomLevel = 1.0;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>以内容逻辑坐标点 (cx, cy) 为视觉中心（不改变 ZoomLevel，仅调整 PanX/PanY）。
    /// 坐标系：cx/cy = 内容逻辑坐标（与 Canvas.Left/Top 同系，单位为"内容 px"）。
    /// 用于"快速定位"：把视口平移到让该点落在屏幕中央。</summary>
    public void CenterOnContentPoint(double cx, double cy)
    {
        double availW = ActualWidth;
        double availH = ActualHeight;
        if (availW <= 0 || availH <= 0) return;

        // 与 ZoomToScreenPoint 同一变换公式（参照 UpdateTransform / OnRenderSizeChanged）：
        //   屏幕 X = 内容 X × scale + 居中偏移 + PanX
        //   scale = FitRatio 比例因子 × ZoomLevel（实际比例在内部计算，公式按源码内坐标系）
        // 这里用 reset 的逻辑：让"逻辑 cx"恰好出现在屏幕中心 (availW/2, availH/2)：
        //   availW/2 = cx × scale + (availW - cw×scale)/2 + PanX
        //   → PanX = availW/2 - cx × scale - (availW - cw×scale)/2
        // 简化：PanX = availW/2 × (1 - ZoomLevel×FitRatio 比例) - cx × scale + cw×scale/2
        // 注：scale 在内部按 FitRatio 重算，这里沿用 ZoomToScreenPoint 的"autoScale × ZoomLevel"语义。
        double cw = ContentWidth;
        double ch = ContentHeight;
        if (cw <= 0 || ch <= 0) return;

        // 内部 scale 比例 = min(availW*FitRatio/cw, availH*FitRatio/ch) * ZoomLevel
        // 为简化（且 FitRatio/autoScale 不直接暴露），把"内容逻辑点 → 屏幕"反向求解：
        // 用 ZoomToScreenPoint 的对称公式（让屏幕中心 availW/2 对应逻辑 cx）：
        // 直接置 PanX/PanY 使 cx/cy 出现在屏幕中心。
        double autoScale = Math.Min(availW * FitRatio / cw, availH * FitRatio / ch);
        double scale = autoScale * ZoomLevel;
        double offsetX = (availW - cw * scale) * 0.5;
        double offsetY = (availH - ch * scale) * 0.5;

        PanX = availW * 0.5 - cx * scale - offsetX;
        PanY = availH * 0.5 - cy * scale - offsetY;
    }

    /// <summary>缩放并调整 Pan，使屏幕坐标锚点 (screenX, screenY) 对应的内容点在缩放前后保持屏幕位置不变。</summary>
    private void ZoomToScreenPoint(double newZoom, double screenX, double screenY)
    {
        double availW = ActualWidth;
        double availH = ActualHeight;
        double cw = ContentWidth;
        double ch = ContentHeight;
        if (availW <= 0 || availH <= 0 || cw <= 0 || ch <= 0) return;

        double autoScale = Math.Min(availW * FitRatio / cw, availH * FitRatio / ch);
        double oldScale = autoScale * ZoomLevel;
        double newScale = autoScale * newZoom;

        double oldCx = (availW - cw * oldScale) * 0.5;
        double oldCy = (availH - ch * oldScale) * 0.5;
        double newCx = (availW - cw * newScale) * 0.5;
        double newCy = (availH - ch * newScale) * 0.5;

        // 屏幕锚点对应的内容逻辑坐标（缩放前）
        double logicX = (screenX - oldCx - PanX) / oldScale;
        double logicY = (screenY - oldCy - PanY) / oldScale;

        // 调整 Pan，使该逻辑点在缩放后仍映射到相同屏幕位置
        PanX = screenX - logicX * newScale - newCx;
        PanY = screenY - logicY * newScale - newCy;
        ZoomLevel = newZoom;
    }
}
