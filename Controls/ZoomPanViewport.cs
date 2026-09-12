using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SymbolDesigner.Controls;

/// <summary>
/// 可复用视口控件：把任意内容当作"画布"进行缩放、平移、自动适配与居中。
///
/// 核心能力（全部内置，调用方零 code-behind）：
///   - 自动适配：内容按 FitRatio（默认 1.0）缩放到视口内，最长边填满，短边居中
///   - 滚轮缩放：以鼠标位置为锚点（EnableWheelZoom 可关）
///   - 中键拖拽平移：按住鼠标中键拖动（EnableMiddleDragPan 可关）
///   - 依赖属性：ContentWidth / ContentHeight / ZoomLevel / PanX / PanY 均可外部绑定
///   - 方法：ZoomToCenter(newZoom) 以内容中心缩放、ResetView() 复位、CenterOnContentPoint(x,y) 定位
///
/// 控件结构（自 v2026 起从 ContentControl 改为 Decorator）：
///   - 不再有 ControlTemplate（Themes/Generic.xaml 不再需要为它注册样式）
///   - Child = 调用方传入的内容（如 CanvasHost）。Canvas 直接放外面，不经过 ContentPresenter 中转
///   - 视觉树：MainWindow > Grid > ZoomPanViewport > Child（仅此 4 层，原 Border+ContentPresenter 两层去掉）
///   - RenderTransform 直接套在 Child 上
///   - ClipToBounds 与 Background 不在本控件提供，需要外层 Border 包裹（详见 SymbolDesignerView.xaml）
/// </summary>
public class ZoomPanViewport : Decorator
{
    /// <summary>内容逻辑宽度（如画布宽 1920）。</summary>
    public static readonly DependencyProperty ContentWidthProperty =
        DependencyProperty.Register(nameof(ContentWidth), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

    /// <summary>内容逻辑高度（如画布高 1080）。</summary>
    public static readonly DependencyProperty ContentHeightProperty =
        DependencyProperty.Register(nameof(ContentHeight), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

    /// <summary>用户缩放倍率（1.0 = 按 FitRatio 自动适配尺寸）。</summary>
    public static readonly DependencyProperty ZoomLevelProperty =
        DependencyProperty.Register(nameof(ZoomLevel), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

    /// <summary>平移偏移 X（叠加在居中偏移之上）。</summary>
    public static readonly DependencyProperty PanXProperty =
        DependencyProperty.Register(nameof(PanX), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(0.0, OnTransformPropertyChanged));

    /// <summary>平移偏移 Y。</summary>
    public static readonly DependencyProperty PanYProperty =
        DependencyProperty.Register(nameof(PanY), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(0.0, OnTransformPropertyChanged));

    /// <summary>自动适配比例：内容最长边占视口的比例（1.0 = 100% 填满，0.96/0.9 = 留白）。
    /// CAD 风格画布（逻辑 1920×1080）要在"作画区域（中间列）"里完全铺满，默认 1.0。</summary>
    public static readonly DependencyProperty FitRatioProperty =
        DependencyProperty.Register(nameof(FitRatio), typeof(double), typeof(ZoomPanViewport),
            new FrameworkPropertyMetadata(1.0, OnTransformPropertyChanged));

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

    private bool _isPanning;
    private Point _panStart;

    /// <summary>子元素在"未受视口约束"条件下测量出的完整内容尺寸（逻辑单位）。
    /// 由 MeasureOverride 记录，ArrangeOverride 用它把子级排布到全尺寸，避免被视口压缩二次缩小。</summary>
    private Size _contentSize = Size.Empty;

    public ZoomPanViewport()
    {
        // Decorator 没有 Template，但 Focusable 默认 false —— 滚轮缩放需要先 focus 才能收到事件。
        // 显式 Focusable=true 让控件可接收键盘焦点，进而触发 OnMouseWheel。
        Focusable = true;
        ClipToBounds = true;

        // 用 PreviewMouseWheel（隧道）处理滚轮：bubbling 的 MouseWheel 会先经过鼠标命中元素
        // （CanvasHost/ItemsControl/绘制橡皮线等），它们 CapetureMouse 或把事件 marked Handled 后，
        // ZoomPan 的 OnMouseWheel 就收不到 → 缩放被外部容器以画布中心代劳（表现为"中心缩放"）。
        // Preview 在路由进入子元素前触发，锚点用屏幕坐标（GetPosition(this)），任何子层都拦不住。
        PreviewMouseWheel += (_, e) => OnWheelZoom(e);
    }

    /// <summary>以鼠标位置为锚点的滚轮缩放（供 Preview 隧道 + 兜底 OnMouseWheel 共用）。</summary>
    private void OnWheelZoom(MouseWheelEventArgs e)
    {
        if (!EnableWheelZoom) return;
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        double newZoom = Math.Clamp(ZoomLevel * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - ZoomLevel) < 0.001) return;

        var p = e.GetPosition(this);
        ZoomToScreenPoint(newZoom, p.X, p.Y);
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateTransform();
    }

    /// <summary>以"无约束"方式测量子元素，拿到其完整逻辑内容尺寸（如画布 1920×1080）。
    /// 关键：不能像默认 Decorator 那样把子级约束到 avail W/H，否则画布会被 Arrange 成视口大小，
    /// 再套 RenderTransform 缩放时就"双重缩小"、且 cx/cy 按 ContentWidth 算就会错 → 画布既不满宽也不居中。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _contentSize = Size.Empty;
        if (Child is UIElement child)
        {
            // 无限约束测量：让显式设了 Width/Height 的子级（CanvasHost）按逻辑尺寸返回真实 DesiredSize
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _contentSize = child.DesiredSize;
        }
        return availableSize;  // 视口自身仍占满可用空间
    }

    /// <summary>把子级排布到其完整内容尺寸（而非视口尺寸），这样 RenderTransform 的 scale 才能精确控制其视觉大小。</summary>
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var rect = _contentSize == Size.Empty
            ? new Rect(arrangeSize)
            : new Rect(_contentSize);
        if (Child is UIElement child)
            child.Arrange(rect);
        return arrangeSize;
    }

    private static void OnTransformPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ZoomPanViewport)d).UpdateTransform();

    /// <summary>重新计算 RenderTransform：自动适配 + 居中 + 用户 Zoom/Pan。
    /// RenderTransform 直接套在 Child（调用方传入的画布 Grid）上，不再经过 ContentPresenter 中转。</summary>
    private void UpdateTransform()
    {
        if (Child is null) return;
        double cw = ContentWidth;
        double ch = ContentHeight;
        double availW = ActualWidth;
        double availH = ActualHeight;
        if (cw <= 0 || ch <= 0 || availW <= 0 || availH <= 0) return;

        double autoScale = Math.Min(availW * FitRatio / cw, availH * FitRatio / ch);
        double scale = autoScale * ZoomLevel;
        double cx = (availW - cw * scale) * 0.5;
        double cy = (availH - ch * scale) * 0.5;

        // [DEBUG 已清]
        // 暴露实际渲染缩放系数,供上层做"反缩放"(预览线/端点视觉恒定)
        if (Math.Abs(CurrentScale - scale) > 1e-9) SetValue(CurrentScaleProperty, scale);

        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(scale, scale));
        tg.Children.Add(new TranslateTransform(cx + PanX, cy + PanY));
        Child.RenderTransform = tg;
    }

    /// <summary>鼠标滚轮：以鼠标位置为锚点缩放。
    /// 实际逻辑在 PreviewMouseWheel（隧道）已处理；此处委托同一实现，
    /// 仅当 Preview 被外层提前截走（几乎不会）时兜底。
    /// 但 Preview 已把事件标记 Handled，bubbling 阶段 OnMouseWheel 仍会（handledEventsToo）
    /// 再触发一次 → 同一格滚轮 double-zoom，必须显式短路掉已处理的事件。</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return; // Preview 已缩放，避免重复缩放
        OnWheelZoom(e);
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

        double cw = ContentWidth;
        double ch = ContentHeight;
        if (cw <= 0 || ch <= 0) return;

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

        double logicX = (screenX - oldCx - PanX) / oldScale;
        double logicY = (screenY - oldCy - PanY) / oldScale;

        PanX = screenX - logicX * newScale - newCx;
        PanY = screenY - logicY * newScale - newCy;
        ZoomLevel = newZoom;
    }
}
