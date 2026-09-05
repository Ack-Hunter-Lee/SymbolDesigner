namespace SymbolDesigner.Models;

/// <summary>线条虚线样式（属性面板"线条"组可调）。映射到 WPF StrokeDashArray：
/// Solid 实线、Dash 短划线、Dot 点线、DashDot 点划线。</summary>
public enum StrokeDashStyle
{
    /// <summary>实线（无虚线）。</summary>
    Solid,
    /// <summary>短划线（"4 2"）。</summary>
    Dash,
    /// <summary>点线（"1 2"）。</summary>
    Dot,
    /// <summary>点划线（"4 2 1 2"）。</summary>
    DashDot,
}

/// <summary>填充方式（属性面板"填充"组可调）。</summary>
public enum FillStyle
{
    /// <summary>无填充（透明）。</summary>
    None,
    /// <summary>单色填充（FillColor）。</summary>
    Solid,
    /// <summary>线性渐变填充（GradientStart → GradientEnd）。</summary>
    Gradient,
}