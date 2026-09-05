using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SymbolDesigner.Converters;

namespace SymbolDesigner.Controls;

/// <summary>
/// 系统级颜色选择器（替代 System.Windows.Forms.ColorDialog — WPF 项目不想引入 WinForms）。
/// 布局：HSV 色相-饱和度平面（左侧）+ Hue 滑条（中间）+ 当前色 vs 原始色 + HEX/RGB 输入 + OK/Cancel。
/// 静态入口 <see cref="PickColor"/>：弹出模态对话框，返回用户选中的 hex（#AARRGGBB），
/// 取消返回 null。
/// </summary>
public partial class ColorDialogWindow : Window
{
    /// <summary>弹出色选择器（模态）；返回用户选中的 hex（#AARRGGBB），取消返回 null。</summary>
    public static string? PickColor(Window? owner, string? initialHex)
    {
        var w = new ColorDialogWindow(initialHex);
        if (owner is not null) w.Owner = owner;
        return w.ShowDialog() == true ? w._resultHex : null;
    }

    private string? _resultHex;
    private Color _original;
    private double _hue;       // 0..360
    private double _sat;       // 0..1
    private double _val;       // 0..1

    // HSV 平面尺寸（XAML 里 hardcode）
    private const double SvWidth = 200;
    private const double SvHeight = 160;
    private const double HueHeight = 160;

    private bool _draggingSv;
    private bool _draggingHue;
    private bool _updating;

    public ColorDialogWindow(string? initialHex)
    {
        InitializeComponent();
        if (ColorUtil.ParseHex(initialHex) is SolidColorBrush b)
        {
            _original = b.Color;
            OldSwatch.Background = b;
        }
        else
        {
            _original = Colors.White;
            OldSwatch.Background = new SolidColorBrush(_original);
        }
        // 初始化 HSV（HsvFromColor）
        HsvFromColor(_original, out _hue, out _sat, out _val);
        UpdateHueIndicator();
        UpdateSvIndicator();
        UpdateFromHsv();
    }

    // ────── HSV ↔ RGB 互转（标准公式） ──────

    private static void HsvFromColor(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, bl));
        double min = Math.Min(r, Math.Min(g, bl));
        v = max;
        s = max == 0 ? 0 : (max - min) / max;
        if (max == min) { h = 0; return; }
        double d = max - min;
        if (max == r) h = (g - bl) / d + (g < bl ? 6 : 0);
        else if (max == g) h = (bl - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
    }

    private static Color ColorFromHsv(double h, double s, double v, byte a = 255)
    {
        if (s == 0) { byte gg = (byte)(v * 255); return Color.FromArgb(a, gg, gg, gg); }
        h %= 360; if (h < 0) h += 360;
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);
        double r, g, bl;
        switch (hi)
        {
            case 0: r = v; g = t; bl = p; break;
            case 1: r = q; g = v; bl = p; break;
            case 2: r = p; g = v; bl = t; break;
            case 3: r = p; g = q; bl = v; break;
            case 4: r = t; g = p; bl = v; break;
            default: r = v; g = p; bl = q; break;
        }
        return Color.FromArgb(a, (byte)(r * 255), (byte)(g * 255), (byte)(bl * 255));
    }

    // ────── SV 平面交互 ──────

    private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true; Mouse.Capture(SvPlane); UpdateSvFromPoint(e.GetPosition(SvPlane));
    }
    private void OnSvMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSv) return;
        UpdateSvFromPoint(e.GetPosition(SvPlane));
    }
    private void OnSvMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false; Mouse.Capture(null);
    }

    private void UpdateSvFromPoint(Point p)
    {
        _sat = Math.Clamp(p.X / SvWidth, 0, 1);
        _val = 1 - Math.Clamp(p.Y / SvHeight, 0, 1);
        UpdateSvIndicator();
        UpdateFromHsv();
    }

    private void UpdateSvIndicator()
    {
        SvIndicator.HorizontalAlignment = HorizontalAlignment.Left;
        SvIndicator.VerticalAlignment = VerticalAlignment.Top;
        SvIndicator.Margin = new Thickness(_sat * SvWidth - 6, (1 - _val) * SvHeight - 6, 0, 0);
    }

    // ────── Hue 滑条交互 ──────

    private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true; Mouse.Capture(HueCanvas); UpdateHueFromPoint(e.GetPosition(HueCanvas));
    }
    private void OnHueMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHue) return;
        UpdateHueFromPoint(e.GetPosition(HueCanvas));
    }
    private void OnHueMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false; Mouse.Capture(null);
    }

    private void UpdateHueFromPoint(Point p)
    {
        _hue = Math.Clamp(p.Y / HueHeight, 0, 1) * 360;
        UpdateHueIndicator();
        UpdateFromHsv();
    }

    private void UpdateHueIndicator()
    {
        HueIndicator.VerticalAlignment = VerticalAlignment.Top;
        HueIndicator.Margin = new Thickness(0, _hue / 360 * HueHeight - 4, -2, 0);
    }

    // ────── 同步到 UI ──────

    private void UpdateFromHsv()
    {
        var c = ColorFromHsv(_hue, _sat, _val);
        _updating = true;
        try
        {
            NewSwatch.Background = new SolidColorBrush(c);
            HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            RBox.Text = c.R.ToString();
            GBox.Text = c.G.ToString();
            BBox.Text = c.B.ToString();
            // SV 平面顶部的 hue overlay 同步
            if (HueStop is GradientStop hs)
                hs.Color = ColorFromHsv(_hue, 1, 1);
        }
        finally { _updating = false; }
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        var t = HexBox.Text?.Trim() ?? "";
        if (t.Length is 6 or 7 && (t.Length == 6 || t[0] == '#'))
        {
            var s = t.Length == 6 ? "#FF" + t : t;
            if (ColorUtil.ParseHex(s) is SolidColorBrush b)
            {
                HsvFromColor(b.Color, out _hue, out _sat, out _val);
                UpdateHueIndicator();
                UpdateSvIndicator();
                UpdateFromHsv();
            }
        }
    }

    private void OnRgbChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (byte.TryParse(RBox.Text, out var rv) && byte.TryParse(GBox.Text, out var gv) && byte.TryParse(BBox.Text, out var bv))
        {
            var c = Color.FromRgb(rv, gv, bv);
            HsvFromColor(c, out _hue, out _sat, out _val);
            UpdateHueIndicator();
            UpdateSvIndicator();
            UpdateFromHsv();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _resultHex = HexBox.Text?.Trim() ?? "#FFFFFFFF";
        if (_resultHex.Length == 7) _resultHex = "#FF" + _resultHex.Substring(1);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}