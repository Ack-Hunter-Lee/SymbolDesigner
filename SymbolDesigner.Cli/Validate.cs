using System.Globalization;
using System.Text.RegularExpressions;

namespace SymbolDesigner.Cli;

/// <summary>形状分类，决定几何字段如何校验/映射。</summary>
internal enum ShapeKind { FixedRect, Ellipse, Ring, Pie, Line, Polyline, BandPolyline, BooleanPath }

/// <summary>type 别名解析结果。</summary>
internal readonly record struct TypeInfo(string TypeKey, ShapeKind Kind, string Alias)
{
    public bool IsPath => Kind is ShapeKind.Line or ShapeKind.Polyline or ShapeKind.BandPolyline;
}

/// <summary>校验结果。</summary>
internal sealed class ValidationResult
{
    public readonly List<string> Errors = new();
    public readonly List<string> Warnings = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>DSL 校验：只读、只报告，不改数据。</summary>
internal static class Validate
{
    private static readonly Regex s_hex = new("^#?[0-9a-fA-F]{3,8}$");
    private static readonly HashSet<string> s_dashes = new(StringComparer.Ordinal) { "Solid", "Dash", "Dot", "DashDot" };
    private static readonly HashSet<string> s_fills = new(StringComparer.Ordinal) { "None", "Solid", "Gradient", "Pattern" };
    private static readonly HashSet<string> s_patterns = new(StringComparer.Ordinal) { "Diagonal", "Horizontal", "Vertical", "Cross" };
    private static readonly HashSet<string> s_axis = new(StringComparer.Ordinal) { "Free", "Horizontal", "Vertical" };

    /// <summary>type 别名 → Kind 映射（含 ThickPolyline 老名自动转 BandPolyline）。</summary>
    public static TypeInfo? ResolveType(string? alias)
    {
        switch (alias?.ToLowerInvariant())
        {
            case "rect":              return new TypeInfo("Rectangle",       ShapeKind.FixedRect,     alias!);
            case "roundedrect":
            case "roundrect":         return new TypeInfo("RoundedRectangle", ShapeKind.FixedRect,    alias!);
            case "ellipse":           return new TypeInfo("Ellipse",         ShapeKind.Ellipse,       alias!);
            case "ring":              return new TypeInfo("Ring",            ShapeKind.Ring,          alias!);
            case "triangle":          return new TypeInfo("Triangle",        ShapeKind.FixedRect,     alias!);
            case "line":              return new TypeInfo("Line",            ShapeKind.Line,          alias!);
            case "polyline":          return new TypeInfo("Polyline",        ShapeKind.Polyline,      alias!);
            case "bandpolyline":
            case "thickpolyline":     return new TypeInfo("BandPolyline",    ShapeKind.BandPolyline,  alias!);
            case "pie":               return new TypeInfo("Pie",             ShapeKind.Pie,           alias!);
            case "boolean":           return new TypeInfo("BooleanPath",     ShapeKind.BooleanPath,   alias!);
            default:                  return null;
        }
    }

    public static ValidationResult Check(DslProject dsl)
    {
        var r = new ValidationResult();
        if (dsl.CanvasWidth is double w && w <= 0) r.Errors.Add($"[canvas] canvasWidth 必须 >0，得到 {w}");
        if (dsl.CanvasHeight is double h && h <= 0) r.Errors.Add($"[canvas] canvasHeight 必须 >0，得到 {h}");
        if (dsl.CanvasBackgroundColor is string cb && !s_hex.IsMatch(cb))
            r.Errors.Add($"[canvas] canvasBackgroundColor 非合法 hex: {cb}");

        for (int i = 0; i < dsl.Primitives.Count; i++)
        {
            var p = dsl.Primitives[i];
            string tag = $"[#{i} type={p.Type ?? "(null)"}]";
            var info = ResolveType(p.Type);
            if (info is null) { r.Errors.Add($"{tag} type 无法识别，合法别名: rect/roundedRect/ellipse/ring/triangle/line/polyline/bandPolyline/pie"); continue; }
            if (p.Type?.Equals("thickpolyline", StringComparison.OrdinalIgnoreCase) == true)
                r.Warnings.Add($"{tag} 老别名 ThickPolyline 已自动映射为 bandPolyline");
            if (info.Value.Kind == ShapeKind.BooleanPath)
            { r.Errors.Add($"{tag} boolean(BooleanPath) 不支持通过 CLI 创建（需布尔运算源，外部无从提供）"); continue; }

            ValidateGeometry(p, info.Value, tag, r);
            ValidateAppearance(p, tag, r);
        }
        return r;
    }

    private static void ValidateGeometry(DslPrimitive p, in TypeInfo info, string tag, ValidationResult r)
    {
        // 不合法数字的一个全局预检：DSL 反序列化时非法数字已抛异常，此处只查语义。
        if (info.IsPath)
        {
            bool hasNamed = info.Kind == ShapeKind.Line
                && p.X1 is not null && p.Y1 is not null && p.X2 is not null && p.Y2 is not null;
            bool hasPts = p.Points is { Count: >= 1 };
            if (!hasPts && !hasNamed)
                r.Errors.Add($"{tag} 路径类缺少端点：polyline/bandPolyline 用 points，line 可用 points 或用具名 x1,y1,x2,y2");
            if (hasPts)
            {
                foreach (var pt in p.Points)
                {
                    if (pt is null || pt.Count < 2 || !IsFinite(pt[0]) || !IsFinite(pt[1]))
                    { r.Errors.Add($"{tag} points 每项需为 [x,y] 数字对，得到 {FormatPt(pt)}"); break; }
                }
                if (info.Kind == ShapeKind.Line && p.Points.Count != 2)
                    r.Errors.Add($"{tag} line 的 points 恰好需要 2 个端点");
            }
        }
        else
        {
            // 通用 x/y/w/h；宽高缺省交给映射层补默认，仅在此处校验"已给定但非法"
            CheckDim(p.Width, "width", tag, r);
            CheckDim(p.Height, "height", tag, r);

            // 圆心便捷定位：centerX+centerY+radius 三者齐全则校验 radius
            bool hasCenter = p.CenterX is not null || p.CenterY is not null || p.Radius is not null;
            if (hasCenter)
            {
                if (p.CenterX is null || p.CenterY is null || p.Radius is null)
                    r.Errors.Add($"{tag} 圆心定位必须三个都给: centerX, centerY, radius");
                else CheckDim(p.Radius, "radius", tag, r);
            }

            switch (info.Kind)
            {
                case ShapeKind.FixedRect when p.CornerRadius is double cr && cr < 0:
                    r.Errors.Add($"{tag} cornerRadius 不能为负: {cr}");
                    break;
                case ShapeKind.Ring:
                    if (p.InnerRadius is double ir && ir < 0)
                    { r.Errors.Add($"{tag} innerRadius 不能为负: {ir}"); break; }
                    if (p.InnerRadius is double ir2 && p.Width is double rw && p.Height is double rh
                        && ir2 >= Math.Min(rw, rh) / 2)
                        r.Errors.Add($"{tag} innerRadius 必须小于外径 min(w,h)/2");
                    break;
                case ShapeKind.Pie:
                    if (p.SweepAngle is double sa && sa <= 0) r.Errors.Add($"{tag} sweepAngle 必须 >0: {sa}");
                    break;
            }
        }
    }

    private static void CheckDim(double? v, string name, string tag, ValidationResult r)
    {
        if (v is double d && !IsFinite(d))
            r.Errors.Add($"{tag} {name} 非法数字: {d}");
        else if (v is double dd && dd <= 0)
            r.Errors.Add($"{tag} {name} 必须 >0，得到 {dd}");
    }

    private static void ValidateAppearance(DslPrimitive p, string tag, ValidationResult r)
    {
        if (p.Opacity is < 0 or > 1) r.Errors.Add($"{tag} opacity 应在 [0,1]: {p.Opacity}");
        if (p.FillBrightness is < 0 or > 100) r.Errors.Add($"{tag} fillBrightness 应在 [0,100]");
        if (p.FillOpacity is < 0 or > 100) r.Errors.Add($"{tag} fillOpacity 应在 [0,100]");
        if (p.StrokeColor is string sc && !s_hex.IsMatch(sc)) r.Errors.Add($"{tag} strokeColor 非合法 hex: {sc}");
        if (p.FillColor is string fc && !s_hex.IsMatch(fc)) r.Errors.Add($"{tag} fillColor 非合法 hex: {fc}");
        if (p.GradientStart is string gs && !s_hex.IsMatch(gs)) r.Errors.Add($"{tag} gradientStart 非合法 hex: {gs}");
        if (p.GradientEnd is string ge && !s_hex.IsMatch(ge)) r.Errors.Add($"{tag} gradientEnd 非合法 hex: {ge}");
        if (p.StrokeDash is string sd && !s_dashes.Contains(sd)) r.Errors.Add($"{tag} strokeDash 非法枚举(Solid/Dash/Dot/DashDot): {sd}");
        if (p.FillStyle is string fs && !s_fills.Contains(fs)) r.Errors.Add($"{tag} fillStyle 非法枚举(None/Solid/Gradient/Pattern): {fs}");
        if (p.PatternStyle is string ps && !s_patterns.Contains(ps)) r.Errors.Add($"{tag} patternStyle 非法枚举(Diagonal/Horizontal/Vertical/Cross): {ps}");
        if (p.LineAxis is string la && !s_axis.Contains(la)) r.Errors.Add($"{tag} lineAxis 非法枚举(Free/Horizontal/Vertical): {la}");
    }

    private static bool IsFinite(double d) => double.IsFinite(d);
    private static string FormatPt(List<double>? pt)
        => pt is null ? "(null)" : string.Join(",", pt.Select(v => v.ToString(CultureInfo.InvariantCulture)));
}