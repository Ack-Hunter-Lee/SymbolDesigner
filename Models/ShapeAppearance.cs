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
    /// <summary>单色渐变填充：起始色=FillColor，结束色自动按亮度衰减 50% 派生。
    /// 与线性渐变共享起止色渲染管道,但用户只需选一个色——避免"渐变选什么结束色"的困惑。
    /// 适用于"想看出立体感但不想再选色"的场景。</summary>
    MonochromeGradient,
    /// <summary>图案填充（斜线/网格/点阵等 TileBrush）。当前 UI 框架已就位,具体图案后续迭代实现。
    /// 选中时 FillBrush 返回 null,渲染层不画填充但保留描边(避免元素看不见)。</summary>
    Pattern,
    /// <summary>图片或纹理填充（导入本地图片作为 FillBrush）。当前 UI 框架已就位,导入逻辑后续迭代实现。
    /// 选中时 FillBrush 返回 null,渲染层不画填充但保留描边。</summary>
    ImageTexture,
}

/// <summary>图案填充样式（FillStyle=Pattern 时选用）。4 种内置基础图案，
/// 用 DrawingBrush TileMode=Tile 平铺。属性面板"图案"下拉直接绑此枚举。
/// 注意：本枚举与 FillStyle.Gradient 的"渐变类型"无关——图案仅指 stroke 风格的平铺样式。</summary>
public enum PatternStyle
{
    /// <summary>45° 斜线网格（最常用的 hatch 风）。</summary>
    Diagonal,
    /// <summary>纯水平线。</summary>
    Horizontal,
    /// <summary>纯竖直线。</summary>
    Vertical,
    /// <summary>正交网格（水平 + 竖直交叉）。</summary>
    Cross,
}