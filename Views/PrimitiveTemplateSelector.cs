using System.Windows;
using System.Windows.Controls;
using SymbolDesigner.Models;

namespace SymbolDesigner.Views;

/// <summary>
/// 根据 PrimitiveItem 的 TypeKey 选择对应的 DataTemplate。
/// 每个画布模板内部用 DraggablePrimitive 包裹图形，使图形可在画布上拖动改位置。
/// 旧版"PrimitiveKind(L1/L2)"两层判断已废弃——图元身份仅由 TypeKey 一维字符串决定。
/// </summary>
public class PrimitiveTemplateSelector : DataTemplateSelector
{
    public DataTemplate? L1Rectangle { get; set; }
    public DataTemplate? L1Ellipse { get; set; }
    public DataTemplate? L1Line { get; set; }
    public DataTemplate? L1RoundedRectangle { get; set; }
    public DataTemplate? L1Triangle { get; set; }
    public DataTemplate? L1BandPolyline { get; set; }
    public DataTemplate? L1Polyline { get; set; }
    public DataTemplate? L1Ring { get; set; }
    public DataTemplate? L1Pie { get; set; }
    public DataTemplate? L1BooleanPath { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not PrimitiveItem p) return null;

        return p.TypeKey switch
        {
            "Rectangle" => L1Rectangle,
            "Ellipse" => L1Ellipse,
            "Line" => L1Line,
            "RoundedRectangle" => L1RoundedRectangle,
            "Triangle" => L1Triangle,
            "BandPolyline" => L1BandPolyline,
            "Polyline" => L1Polyline,
            "Ring" => L1Ring,
            "Pie" => L1Pie,
            "BooleanPath" => L1BooleanPath,
            _ => null,
        };
    }
}