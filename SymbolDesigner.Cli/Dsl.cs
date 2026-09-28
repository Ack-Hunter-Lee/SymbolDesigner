using System.Globalization;
using System.Text.Json;

namespace SymbolDesigner.Cli;

/// <summary>DSL 顶层（比 .sym.json 更靠近人的 JSON 工程描述）。</summary>
internal sealed class DslProject
{
    public double? CanvasWidth { get; set; }
    public double? CanvasHeight { get; set; }
    public string? CanvasBackgroundColor { get; set; }
    public List<DslPrimitive> Primitives { get; set; } = new();
}

/// <summary>DSL 图元。几何字段按类型贡献不同（rect/ellipse 用宽高或 center+radius，line/polyline 用 points）。
/// 所有字段可空，凡缺省一律由映射器按"设计默认"补齐。</summary>
internal sealed class DslPrimitive
{
    public string? Type { get; set; }
    public string? Id { get; set; }

    // 常见图形（rect/roundedRect/ellipse/ring/triangle/pie）用左上角定位
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }

    // 圆形类便捷定位：centerX,centerY + radius（ellipse/ring/pie），优先于 x/y/w/h
    public double? CenterX { get; set; }
    public double? CenterY { get; set; }
    public double? Radius { get; set; }

    // 线：x1,y1,x2,y2 或 points:[[x,y],[x,y]]
    public double? X1 { get; set; }
    public double? Y1 { get; set; }
    public double? X2 { get; set; }
    public double? Y2 { get; set; }
    public List<List<double>>? Points { get; set; }

    // 类型专属几何
    public double? CornerRadius { get; set; }   // roundedRect
    public double? InnerRadius { get; set; }     // ring
    public double? StartAngle { get; set; }      // pie
    public double? SweepAngle { get; set; }      // pie
    public bool Closed { get; set; }             // polyline/bandPolyline 缺省 false

    // 变换/外观
    public double Rotation { get; set; }
    public int ZIndex { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool Visible { get; set; } = true;

    public string? StrokeColor { get; set; }
    public double? StrokeWidth { get; set; }
    public string? StrokeDash { get; set; }
    public double Thickness { get; set; } = 6;   // 带宽/描边厚（bandPolyline 带宽）
    public string? FillStyle { get; set; }
    public string? FillColor { get; set; }
    public string? GradientStart { get; set; }
    public string? GradientEnd { get; set; }
    public double? FillBrightness { get; set; }
    public double? FillOpacity { get; set; }
    public string? PatternStyle { get; set; }
    public string? LineAxis { get; set; }        // line: Free/Horizontal/Vertical
}

/// <summary>DSL JSON 解析与读取帮助。</summary>
internal static class Dsl
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>从"文件路径 / -" 读 DSL 文本（- = stdin）。</summary>
    public static string ReadSource(string path)
    {
        if (path == "-")
            return Console.In.ReadToEnd();
        return File.ReadAllText(path, System.Text.Encoding.UTF8);
    }

    public static DslProject Parse(string json)
    {
        var p = JsonSerializer.Deserialize<DslProject>(json, Options)
                ?? throw new FormatException("DSL JSON 为空或格式无效");
        p.Primitives ??= new List<DslPrimitive>();
        return p;
    }
}