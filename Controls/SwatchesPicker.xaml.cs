using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SymbolDesigner.Converters;

namespace SymbolDesigner.Controls;

/// <summary>
/// 取色面板：4 区域（最近使用 / 主矩阵 / 标准颜色 / 快捷按钮）。
/// 弹层内布局：最近使用 1 行 + 主网格 9 列 × 8 行（左上第 1 格透明 = 无填充）+
/// 标准颜色 1 行 10 色 + 更多颜色 / 取色器 / 无填充 3 个快捷按钮。
/// 绑定 <see cref="SelectedColor"/>（string hex，默认 TwoWay）→ 更新任意外观颜色属性；
/// <see cref="RecentColors"/> 由外部 ViewModel 推入最近使用色；
/// 事件 <see cref="ClearFillRequested"/> / <see cref="MoreColorsRequested"/> /
/// <see cref="PickerRequested"/> 通知外部联动 FillStyle 或打开系统 ColorDialog。
/// </summary>
public partial class SwatchesPicker : UserControl
{
    public SwatchesPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildSwatches();
        Unloaded += (_, _) => DetachOutsideClickHook();
    }

    // ─── 依赖属性 ──────────────────────────────────────────────

    /// <summary>当前选中颜色（hex 字符串，如 "#4FC3F7"）。</summary>
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(string), typeof(SwatchesPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedColorChanged));

    public string? SelectedColor
    {
        get => (string?)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>最近使用颜色（外部 ViewModel 持有并推送，本控件只读展示）。
    /// 集合变化时（DP 重新赋值 / 集合增删）自动刷新槽位 Border。</summary>
    public static readonly DependencyProperty RecentColorsProperty = DependencyProperty.Register(
        nameof(RecentColors), typeof(ObservableCollection<string>), typeof(SwatchesPicker),
        new PropertyMetadata(null, OnRecentColorsChanged));

    public ObservableCollection<string>? RecentColors
    {
        get => (ObservableCollection<string>?)GetValue(RecentColorsProperty);
        set => SetValue(RecentColorsProperty, value);
    }

    /// <summary>DP 值变化回调：解绑旧集合的 CollectionChanged，挂新集合，然后刷新槽位。</summary>
    private static void OnRecentColorsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SwatchesPicker)d;
        if (e.OldValue is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= self.OnRecentCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += self.OnRecentCollectionChanged;
        self.RefreshRecentSlots();
    }

    private void OnRecentCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshRecentSlots();

    // ─── 路由事件（"无填充" / "更多颜色" / "取色器" 由外部处理） ────

    public static readonly RoutedEvent ClearFillRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ClearFillRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SwatchesPicker));
    public event RoutedEventHandler ClearFillRequested
    {
        add => AddHandler(ClearFillRequestedEvent, value);
        remove => RemoveHandler(ClearFillRequestedEvent, value);
    }

    public static readonly RoutedEvent MoreColorsRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(MoreColorsRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SwatchesPicker));
    public event RoutedEventHandler MoreColorsRequested
    {
        add => AddHandler(MoreColorsRequestedEvent, value);
        remove => RemoveHandler(MoreColorsRequestedEvent, value);
    }

    public static readonly RoutedEvent PickerRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(PickerRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SwatchesPicker));
    public event RoutedEventHandler PickerRequested
    {
        add => AddHandler(PickerRequestedEvent, value);
        remove => RemoveHandler(PickerRequestedEvent, value);
    }

    // ─── 入口 / 外部点击管理 ──────────────────────────────────

    private System.Windows.Window? _hookedWindow;

    private void OnMainChecked(object sender, RoutedEventArgs e) => HookOutsideClick();
    private void OnMainUnchecked(object sender, RoutedEventArgs e) => DetachOutsideClickHook();

    private void OnMainPreviewDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { }
    private void OnMainPreviewUp(object sender, System.Windows.Input.MouseButtonEventArgs e) { }

    private void HookOutsideClick()
    {
        if (_hookedWindow is not null) return;
        _hookedWindow = System.Windows.Window.GetWindow(this);
        if (_hookedWindow is null) return;
        _hookedWindow.PreviewMouseDown += OnWindowPreviewMouseDown;
        _hookedWindow.Deactivated += OnWindowDeactivated;
    }

    private void DetachOutsideClickHook()
    {
        if (_hookedWindow is null) return;
        _hookedWindow.PreviewMouseDown -= OnWindowPreviewMouseDown;
        _hookedWindow.Deactivated -= OnWindowDeactivated;
        _hookedWindow = null;
    }

    private void OnWindowPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Popup.IsOpen) return;
        var src = e.OriginalSource as System.Windows.DependencyObject;
        if (IsDescendantOfThis(src)) return;
        if (IsInsidePopup(src)) return;
        Popup.IsOpen = false;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (Popup.IsOpen) Popup.IsOpen = false;
    }

    private bool IsDescendantOfThis(System.Windows.DependencyObject? o)
    {
        for (var cur = o; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur == this) return true;
        return false;
    }

    private bool IsInsidePopup(System.Windows.DependencyObject? o)
    {
        for (var cur = o; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur == Popup || cur == Popup.Child) return true;
        return false;
    }

    // ─── 弹层构建 ──────────────────────────────────────────

    private bool _updating;
    private bool _swatchesBuilt;

    /// <summary>最近使用色槽位 Border（固定 10 个）。每个槽位根据 RecentColors[i] 是否存在
    /// 决定是实色还是虚线占位。0 色时所有槽位都是虚线占位框（行高不为 0，行始终可见）。</summary>
    private readonly System.Collections.Generic.List<Border> _recentSlots = new();

    private void BuildSwatches()
    {
        if (_swatchesBuilt) return;
        _swatchesBuilt = true;

        // 最近使用：固定 10 个槽位（始终显示）
        var recentGrid = FindName("RecentSlots") as UniformGrid;
        if (recentGrid is not null)
        {
            for (int i = 0; i < 10; i++)
            {
                var slot = new Border
                {
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(1),
                    Background = (Brush?)FindResource("Brush.Bg.Canvas"),
                    BorderBrush = (Brush?)FindResource("Brush.Border.Subtle"),
                    BorderThickness = new Thickness(0.6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = (string?)null,
                };
                slot.MouseLeftButtonDown += OnRecentSwatchClick;
                _recentSlots.Add(slot);
                recentGrid.Children.Add(slot);
            }
            // 初始按当前集合刷新（0 色 → 全部虚线占位）
            RefreshRecentSlots();
        }

        // 主矩阵：9 列 × 8 行
        var mainGrid = FindName("MainSwatchGrid") as UniformGrid;
        if (mainGrid is not null)
        {
            mainGrid.Columns = 9;
            mainGrid.Rows = 8;
            foreach (var row in ColorPalette.MainGrid)
                foreach (var hex in row)
                    mainGrid.Children.Add(BuildCell(hex, isMainMatrix: true));
        }

        // 标准颜色：1 行 10 列
        var stdGrid = FindName("StandardGrid") as UniformGrid;
        if (stdGrid is not null)
        {
            stdGrid.Columns = ColorPalette.StandardColors.Count;
            stdGrid.Rows = 1;
            foreach (var hex in ColorPalette.StandardColors)
                stdGrid.Children.Add(BuildCell(hex, isMainMatrix: false));
        }
    }

    /// <summary>把 RecentColors[i]（i &lt; Count）刷到 _recentSlots[i]（实色 + 写 Tag=hex），
    /// 其余槽位复位为虚线占位 + Tag=null（点击不响应）。
    /// 在 BuildSwatches 末尾、RecentColorsProperty 变化、RecentColors 集合增删时调用。</summary>
    private void RefreshRecentSlots()
    {
        if (_recentSlots.Count == 0) return;
        var list = RecentColors;
        int count = list?.Count ?? 0;
        for (int i = 0; i < _recentSlots.Count; i++)
        {
            var slot = _recentSlots[i];
            if (i < count && list is not null)
            {
                var hex = list[i];
                slot.Background = ColorUtil.ParseHex(hex);
                slot.Tag = hex;
                slot.BorderBrush = (Brush?)FindResource("Brush.Border.Mid");
                slot.Cursor = System.Windows.Input.Cursors.Hand;
            }
            else
            {
                // 占位：背景=画布色（淡化），边框=细灰
                slot.Background = (Brush?)FindResource("Brush.Bg.Canvas");
                slot.Tag = null;
                slot.BorderBrush = (Brush?)FindResource("Brush.Border.Subtle");
                slot.Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }
    }

    /// <summary>构造一个色块。矩阵内全为实色 hex（透明快捷项走外部按钮，不再嵌矩阵）。</summary>
    private FrameworkElement BuildCell(string hex, bool isMainMatrix)
    {
        var brush = ColorUtil.ParseHex(hex);
        var b = new Border
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(1),
            Background = brush,
            BorderBrush = (Brush?)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(0.6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = hex,
        };
        b.MouseLeftButtonDown += OnSwatchClick;
        return b;
    }

    // ─── 色块点击 ──────────────────────────────────────────

    private void OnSwatchClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string hex)
        {
            SelectedColor = hex;
            Popup.IsOpen = false;
        }
    }

    /// <summary>最近使用颜色点击：Tag 由 DataTemplate 传入 hex 字符串（与普通色块同路径）。</summary>
    private void OnRecentSwatchClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OnSwatchClick(sender, e);

    // ─── 快捷按钮 ──────────────────────────────────────────

    private void OnMoreColorsClick(object sender, RoutedEventArgs e)
    {
        // 由外部宿主处理：弹系统 ColorDialog，选完色后写回 SelectedColor（TwoWay 绑定驱动 SelectedItem 字段）。
        // 不在控件内部打开对话框，避免 WPF/WinForms 混引用的耦合扩散到所有使用方。
        RaiseEvent(new RoutedEventArgs(MoreColorsRequestedEvent, this));
        // 不立即关闭，让外部决定
    }

    private void OnEyedropperClick(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(PickerRequestedEvent, this));
    }

    private void OnNoFillClick(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ClearFillRequestedEvent, this));
        Popup.IsOpen = false;
    }

    // ─── HEX 输入同步 ──────────────────────────────────────

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        var text = HexInput.Text?.Trim() ?? string.Empty;
        if (ColorUtil.ParseHex(text) is not null)
            SelectedColor = text;
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SwatchesPicker)d).UpdateUi();

    private void UpdateUi()
    {
        _updating = true;
        try
        {
            if (ColorUtil.ParseHex(SelectedColor) is SolidColorBrush brush)
            {
                ColorPreview.Background = brush;
                HexText.Text = SelectedColor!;
                HexText.Foreground = (Brush)FindResource("Brush.Text.Primary");
            }
            else
            {
                ColorPreview.Background = null;
                HexText.Text = "—";
            }
        }
        finally
        {
            _updating = false;
        }
    }
}