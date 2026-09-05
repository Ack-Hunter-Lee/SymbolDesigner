using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SymbolDesigner.Controls;

/// <summary>
/// 屏幕取色器：宿主窗口保持可见，放大镜实时截取鼠标周围像素跟随鼠标。
///
/// 实现要点（纯 P/Invoke，不引入 System.Drawing）：
/// - 不再对宿主窗口 Opacity=0：采样实时进行（每次 BitBlt 鼠标周围 33×33），
///   宿主界面保持可见可交互，也能从设计器自身取色；
/// - 放大镜窗口跟随鼠标但放在"右下偏移"：不覆盖鼠标所在像素（目标不被遮挡），
///   中心十字 = 采样区中心 = 鼠标位置；
/// - Enter/Space = 确认取色（值 = 鼠标所在像素），Esc = 取消；
/// - 每次采样 GDI BitBlt 到 33×33 内存位图 → CreateBitmapSourceFromHBitmap
///   转 WPF 位图 → NearestNeighbor 渲染（像素锐利），中心像素取鼠标所在点。
/// </summary>
public static class ScreenColorPicker
{
    /// <summary>弹出屏幕取色会话。返回选中的 hex（如 "#4FC3F7"）；取消返回 null。
    /// 宿主窗口保持可见，会话结束无需恢复透明度。</summary>
    public static string? PickColor(Window host)
    {
        var picker = new ScreenColorPickerWindow { Owner = host };
        picker.Show();
        picker.Activate();

        // 非模态挂起：不锁鼠标输入，用户可自由移动鼠标到任意屏幕位置取色；
        // 窗口关闭（按 Enter/Space/Esc）时结束循环。
        var frame = new DispatcherFrame();
        picker.Closed += (_, _) => frame.Continue = false;
        Dispatcher.PushFrame(frame);

        return picker.PickedHex;
    }
}

/// <summary>放大镜取色窗口（内部实现，不对外）。</summary>
internal class ScreenColorPickerWindow : Window
{
    // ─── P/Invoke（screen 捕获 + 鼠标定位） ───
    private const int SRCCOPY = 0x00CC0020;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int dx, int dy, int w, int h,
        IntPtr hdcSrc, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // ─── 常量 ───
    private const double ZoomScale = 8;        // 放大倍数
    private const int SampleRegion = 33;       // 采样区域边长（像素）
    private const double InfoBarHeight = 30;   // 底部信息条高度（px）
    private const double WindowOffset = 28;    // 放大镜相对鼠标的偏移（右下）
    private const double HintHeight = 16;      // 底部快捷键提示条高度

    // ─── 状态 ───
    private Image? _zoomImage;
    private TextBlock? _hexText;
    private Border? _colorPreview;
    private bool _done;
    private bool _closed;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);

    /// <summary>会话结果：确认后为 hex 字符串，取消为 null。</summary>
    public string? PickedHex { get; private set; }

    public ScreenColorPickerWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Focusable = true;
        Focus();

        Width = SampleRegion * ZoomScale + 12;
        Height = SampleRegion * ZoomScale + 12 + InfoBarHeight + HintHeight;
        PreviewKeyDown += OnPreviewKeyDown;
        // 失焦（点击其他程序/Alt+Tab）也结束取色：不直接 Close()，
        // 因为 Deactivated 可能在 WndProc 处理中触发，再 Close() 会抛 VerifyNotClosing。
        // 仅做"标记取消"，由 DispatcherFrame 自然回到调用方后再 Close。
        Deactivated += (_, _) =>
        {
            if (_done) return;
            PickedHex = null;
            _done = true;
            _timer.Stop();
            Dispatcher.BeginInvoke(new Action(CloseSafe), DispatcherPriority.Background);
        };

        var root = new Grid { Background = GetBrush("Brush.Bg.Popup"), Margin = new Thickness(2) };

        _zoomImage = new Image
        {
            Width = SampleRegion * ZoomScale,
            Height = SampleRegion * ZoomScale,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(_zoomImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_zoomImage, EdgeMode.Aliased);

        // 中心像素十字指示：红色细十字线覆盖在放大图中心（= 鼠标所在像素）
        var canvas = new Canvas { Width = _zoomImage.Width, Height = _zoomImage.Height, IsHitTestVisible = false };
        var hLine = new Rectangle
        {
            Width = _zoomImage.Width,
            Height = 1,
            Fill = GetBrush("Brush.State.Danger"),
        };
        Canvas.SetTop(hLine, _zoomImage.Height / 2.0 - 0.5);
        var vLine = new Rectangle
        {
            Width = 1,
            Height = _zoomImage.Height,
            Fill = GetBrush("Brush.State.Danger"),
        };
        Canvas.SetLeft(vLine, _zoomImage.Width / 2.0 - 0.5);
        canvas.Children.Add(hLine);
        canvas.Children.Add(vLine);

        // 信息条：色块预览 + hex 文本
        var info = new Grid { Height = InfoBarHeight, Background = GetBrush("Brush.Bg.CanvasChrome") };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InfoBarHeight - 8) });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _colorPreview = new Border
        {
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(3),
            Background = GetBrush("Brush.Bg.Canvas"),
            BorderBrush = GetBrush("Brush.Border.Mid"),
            BorderThickness = new Thickness(1),
        };
        _hexText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("Brush.Text.Primary"),
        };
        info.Children.Add(_colorPreview);
        info.Children.Add(_hexText);

        // 快捷键提示条
        var hint = new TextBlock
        {
            Height = HintHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = GetBrush("Brush.Text.Muted"),
            Text = "Enter 取色 · Esc 取消",
        };

        var gridBox = new Grid();
        gridBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_zoomImage.Height) });
        gridBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(InfoBarHeight) });
        gridBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HintHeight) });
        gridBox.Children.Add(_zoomImage);
        gridBox.Children.Add(canvas);
        Grid.SetRow(info, 1);
        gridBox.Children.Add(info);
        Grid.SetRow(hint, 2);
        gridBox.Children.Add(hint);

        root.Children.Add(gridBox);
        Content = root;

        _timer.Interval = TimeSpan.FromMilliseconds(30);
        _timer.Tick += (_, _) => UpdateMagnifier();
        _timer.Start();
        UpdateMagnifier();
    }

    /// <summary>从 App 资源取画刷（遵守项目"颜色一律走 Theme token"规则），找不到则返回透明。 </summary>
    private static Brush GetBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>定时器：鼠标位置 → 实时 BitBlt 采样 33×33 → 放大渲染 + 中心色显示。</summary>
    private void UpdateMagnifier()
    {
        if (_done || _closed || _zoomImage is null || _hexText is null || _colorPreview is null) return;
        if (!GetCursorPos(out var p)) return;

        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vWidth <= 0 || vHeight <= 0) return;

        // 采样区：鼠标所在像素为区域中心（截图在鼠标位置，取色即鼠标下像素）
        int left = p.X - SampleRegion / 2;
        int top = p.Y - SampleRegion / 2;
        // 越界 clamp（多显示器虚拟坐标含负区）
        left = Math.Max(vLeft, Math.Min(left, vLeft + vWidth - SampleRegion));
        top = Math.Max(vTop, Math.Min(top, vTop + vHeight - SampleRegion));

        byte[]? regionPixels = CaptureRegion(left, top, SampleRegion, SampleRegion);
        if (regionPixels is null) return;

        // 中心像素 = 鼠标所在像素（采样区中心）
        int ci = (SampleRegion / 2) * SampleRegion + SampleRegion / 2;
        byte b = regionPixels[ci * 4 + 0];
        byte g = regionPixels[ci * 4 + 1];
        byte r = regionPixels[ci * 4 + 2];
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        PickedHex = hex;
        _hexText.Text = hex;
        _colorPreview.Background = SymbolDesigner.Converters.ColorUtil.ParseHex(hex) ?? Brushes.Transparent;

        // 放大图（BGRA 位图，NearestNeighbor 放大）
        var src = BitmapSource.Create(SampleRegion, SampleRegion, 96, 96, PixelFormats.Bgra32, null, regionPixels, SampleRegion * 4);
        src.Freeze();
        _zoomImage.Source = src;

        // 窗口跟随鼠标（右下偏移 → 不盖住鼠标所在的目标像素）
        double offX = WindowOffset, offY = WindowOffset;
        if (p.X + Width + offX > vLeft + vWidth) offX = -Width - WindowOffset;   // 太靠右 → 左放
        if (p.Y + Height + offY > vTop + vHeight) offY = -Height - WindowOffset; // 太靠下 → 上放
        Left = p.X + offX;
        Top = p.Y + offY;
    }

    /// <summary>GDI BitBlt 采样屏幕区域（虚拟屏幕坐标）→ BGRA 像素数组。</summary>
    private byte[]? CaptureRegion(int left, int top, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, left, top, SRCCOPY))
                return null;
            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var pixels = new byte[w * h * 4];
            bs.CopyPixels(pixels, w * 4, 0);
            return pixels;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_done) return;
        if (e.Key == Key.Escape)
        {
            PickedHex = null;
            _done = true;
            _timer.Stop();
            Dispatcher.BeginInvoke(new Action(CloseSafe), DispatcherPriority.Background);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            // hex 已经在 UpdateMagnifier 实时写到 PickedHex，这里只结束会话
            _done = true;
            _timer.Stop();
            Dispatcher.BeginInvoke(new Action(CloseSafe), DispatcherPriority.Background);
            e.Handled = true;
        }
    }

    /// <summary>统一的关闭入口：带 _closed 守卫，避免重复 Close 触发 VerifyNotClosing。</summary>
    private void CloseSafe()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        try { Close(); } catch { /* 已在关闭流程中，吞掉 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        base.OnClosed(e);
    }
}