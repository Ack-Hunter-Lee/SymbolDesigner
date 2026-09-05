using System.Collections.Generic;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>外观（线条/填充）的属性面板选项源。三个大组：线条、填充、阴影（阴影暂留空）。
/// 枚举显示用"中文标签 + 枚举值"配对，ComboBox 绑 Value 写回模型。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>带显示的枚举选项。</summary>
    public sealed record AppearanceOption(string Label, object Value);

    /// <summary>线条虚线样式选项。（实线/虚线/点线/点划线）</summary>
    public IReadOnlyList<AppearanceOption> StrokeDashOptions { get; } = new[]
    {
        new AppearanceOption("实线",   StrokeDashStyle.Solid),
        new AppearanceOption("虚线",   StrokeDashStyle.Dash),
        new AppearanceOption("点线",   StrokeDashStyle.Dot),
        new AppearanceOption("点划线", StrokeDashStyle.DashDot),
    };

    /// <summary>填充方式选项。（无 / 单色 / 线性渐变）</summary>
    public IReadOnlyList<AppearanceOption> FillOptions { get; } = new[]
    {
        new AppearanceOption("无填充",     FillStyle.None),
        new AppearanceOption("单色填充",   FillStyle.Solid),
        new AppearanceOption("线性渐变",   FillStyle.Gradient),
    };
}