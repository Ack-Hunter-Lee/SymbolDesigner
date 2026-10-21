using System;
using System.Windows;
using System.Windows.Controls;

namespace SymbolDesigner.Controls;

/// <summary>Slider + 数值显示 + Up/Down 微调按钮组（基于 RepeatButton，长按连续触发）。
/// 用 DependencyProperty 把 Slider 的 4 个属性（Min/Max/Value/Step）暴露给外部 TwoWay 绑定，
/// 调用方直接当作"带微调按钮的 Slider"使用，绑定 SelectedItem.XXXProperty 即可。</summary>
public partial class SliderSpinBox : UserControl
{
    public SliderSpinBox()
    {
        InitializeComponent();
        // 内部 Slider Value 变化时同步触发外部 ValueChanged 路由事件，保持与原生 Slider 兼容
        PART_Slider.ValueChanged += (_, e) => ValueChanged?.Invoke(this, e);
    }

    public static readonly DependencyProperty MinimumProperty =
        Slider.MinimumProperty.AddOwner(typeof(SliderSpinBox));
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty =
        Slider.MaximumProperty.AddOwner(typeof(SliderSpinBox));
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        Slider.ValueProperty.AddOwner(
            typeof(SliderSpinBox),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>微调步长（按钮 +1 / -1 的幅度；Slider 小刻度同步）。</summary>
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(SliderSpinBox), new PropertyMetadata(1.0));
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>键盘 PgUp/PgDn 的大步长。原生 Slider 用 LargeChange 区分大小步。
    /// 默认 10 与 WPF Slider 自身的 LargeChange 默认值一致：此前用 NaN 表示"未设置"，
    /// 但 NaN 直接转发给 Slider.LargeChange 会被判为非法值，每个实例刷一条绑定错误。</summary>
    public static readonly DependencyProperty LargeChangeProperty =
        DependencyProperty.Register(nameof(LargeChange), typeof(double), typeof(SliderSpinBox), new PropertyMetadata(10.0));
    public double LargeChange
    {
        get => (double)GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    /// <summary>拖动时是否对齐刻度（tick）。原 Slider 同名属性的等价物。</summary>
    public static readonly DependencyProperty IsSnapToTickEnabledProperty =
        DependencyProperty.Register(nameof(IsSnapToTickEnabled), typeof(bool), typeof(SliderSpinBox), new PropertyMetadata(false));
    public bool IsSnapToTickEnabled
    {
        get => (bool)GetValue(IsSnapToTickEnabledProperty);
        set => SetValue(IsSnapToTickEnabledProperty, value);
    }

    /// <summary>禁用整个控件（含内部 Slider 和微调按钮）。覆盖 UserControl.IsEnabled，
    /// 以便外层 Style TargetType=controls:SliderSpinBox 用 Setter Property="IsEnabled" 控制可用性。</summary>
    public new static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.Register(nameof(IsEnabled), typeof(bool), typeof(SliderSpinBox),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, OnIsEnabledChanged));
    public new bool IsEnabled
    {
        get => (bool)GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SliderSpinBox s) return;
        // UserControl.IsEnabled 内部分发到视觉树，但内部的 RepeatButton 默认不会变灰；这里强制同步
        s.PART_Slider.IsEnabled = (bool)e.NewValue;
    }

    /// <summary>数值显示格式（StringFormat）。例如 "{0:0}px" / "{0:0}%"。
    /// 注意：XAML 里通常写成 StringFormat="{}{0:0.#}"（{} 用于防止被当成标记扩展），
    /// 但实测该转义前缀会原样落到本属性上，UpdateDisplayText 里统一剥除。</summary>
    public static readonly DependencyProperty StringFormatProperty =
        DependencyProperty.Register(nameof(StringFormat), typeof(string), typeof(SliderSpinBox),
            new PropertyMetadata("{0:0}", OnStringFormatChanged));
    public string StringFormat
    {
        get => (string)GetValue(StringFormatProperty);
        set => SetValue(StringFormatProperty, value);
    }

    /// <summary>值变化事件（透传内部 Slider.ValueChanged），用于替代原 Slider 上写回调的 XAML。
    /// 外部在 XAML 中写 ValueChanged="..." 即可拿到 RoutedPropertyChangedEventArgs&lt;double&gt;。</summary>
    public event RoutedPropertyChangedEventHandler<double>? ValueChanged;

    /// <summary>渲染文本（按 StringFormat 格式化当前 Value）。
    /// DP 化：值/格式变化时由回调刷新，保证 TextBlock 读数实时跟随（此前普通 CLR 属性不通知绑定）。</summary>
    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(SliderSpinBox),
            new FrameworkPropertyMetadata("0"));
    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SliderSpinBox s) s.UpdateDisplayText();
    }

    private static void OnStringFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SliderSpinBox s) s.UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        // XAML 的 StringFormat="{}{0:0.#}" 中，{} 转义前缀在自定义 DP 上不会被剥掉，
        // 直接喂给 string.Format 会在 offset 1 抛 FormatException —— 每个 SliderSpinBox
        // 实例化时一次，表现为选中带状折线/切到带宽模式时弹出一连串异常框。
        // 这里统一剥掉前缀，并对非法格式兜底，保证格式化异常永远不冒到布局阶段。
        string fmt = string.IsNullOrEmpty(StringFormat) ? "{0:0}" : StringFormat;
        if (fmt.StartsWith("{}", StringComparison.Ordinal))
            fmt = fmt[2..];
        string text;
        try
        {
            text = string.Format(fmt, Value);
        }
        catch (FormatException)
        {
            text = Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        }
        SetCurrentValue(DisplayTextProperty, text);
    }

    private void OnIncrementClick(object sender, RoutedEventArgs e) => Adjust(+Step);
    private void OnDecrementClick(object sender, RoutedEventArgs e) => Adjust(-Step);

    private void Adjust(double delta)
    {
        double next = Math.Max(Minimum, Math.Min(Maximum, Value + delta));
        // 通过 SetCurrentValue 触发 TwoWay 绑定的外部属性更新，且不在内部触发自递归回调
        SetCurrentValue(ValueProperty, next);
    }
}