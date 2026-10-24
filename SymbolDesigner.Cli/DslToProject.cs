using SymbolDesigner.Models;

namespace SymbolDesigner.Cli;

/// <summary>把友好 DSL 组装成 SymbolProject（等价于 .sym.json 内容）。
/// 假定已通过 Validate 校验；对几何按"设计默认"(见 Draw.AddL1) 主动补齐，
/// 而非依赖 DTO 的"兜底默认"。本类自身不触碰 WPF 类型；皮带（Belt）的拓扑/几何求解
/// 委托给 BeltDtoGeometry（CLI 已引入 WPF 框架引用，仅 render 子命令需其运行时）。</summary>
internal static class DslToProject
{
    public static SymbolProject ToProject(DslProject dsl)
    {
        var proj = new SymbolProject
        {
            CanvasWidth = dsl.CanvasWidth ?? 1920,
            CanvasHeight = dsl.CanvasHeight ?? 1080,
            CanvasBackgroundColor = dsl.CanvasBackgroundColor,
        };
        proj.Primitives.AddRange(BuildPrimitives(dsl.Primitives, 0));
        BeltDtoGeometry.AutoEngageAll(proj.Primitives);
        return proj;
    }

    /// <summary>把 DSL 图元列表转成 DTO 列表（ZIndex 从 startZ 起递增）。供 new/add 共用。</summary>
    public static List<PrimitiveItemDto> BuildPrimitives(IList<DslPrimitive> list, int startZ)
    {
        var dto = new List<PrimitiveItemDto>(list.Count);
        for (int i = 0; i < list.Count; i++)
            dto.Add(MapPrimitive(list[i], startZ + i));
        return dto;
    }

    private static PrimitiveItemDto MapPrimitive(DslPrimitive p, int z)
    {
        var info = Validate.ResolveType(p.Type)
                   ?? throw new InvalidOperationException($"type 未识别: {p.Type} (应已通过校验)");
        var dto = new PrimitiveItemDto
        {
            Id = string.IsNullOrEmpty(p.Id) ? Guid.NewGuid().ToString() : p.Id,
            Kind = "L1",
            TypeKey = info.TypeKey,
            ZIndex = z,
            Rotation = p.Rotation,
            Opacity = p.Opacity,
            IsVisible = p.Visible,
            BandWidth = p.Thickness,
            LineAxis = ResolveLineAxis(p) ?? "Free",
        };
        ApplyAppearance(p, dto);

        if (info.IsPath)
            MapPath(p, info.Kind, dto);
        else
            MapFixed(p, info.Kind, dto);
        return dto;
    }

    private static void MapPath(DslPrimitive p, ShapeKind kind, PrimitiveItemDto dto)
    {
        // 端点来源：points（推荐）或 line 的具名 x1,y1,x2,y2
        double[][] pts;
        if (p.Points is { Count: > 0 })
        {
            pts = p.Points.Select(pt => new[] { pt[0], pt[1] }).ToArray();
        }
        else
        {
            double[] a = { p.X1!.Value, p.Y1!.Value };
            double[] b = { p.X2!.Value, p.Y2!.Value };
            pts = new[] { a, b };
        }

        // Belt：只写两个自由端点 + 开闭；绕行拓扑与几何由 BeltDtoGeometry.AutoEngageAll 求解（几何不落盘）
        if (kind == ShapeKind.Belt)
        {
            dto.BeltStartX = pts[0][0]; dto.BeltStartY = pts[0][1];
            dto.BeltEndX = pts[^1][0]; dto.BeltEndY = pts[^1][1];
            dto.IsClosed = p.Closed;
            return;
        }

        // PathData 是相对 (X,Y) 的局部坐标：先求 bbox、平移后序列化
        double minX = pts.Min(q => q[0]), maxX = pts.Max(q => q[0]);
        double minY = pts.Min(q => q[1]), maxY = pts.Max(q => q[1]);
        double[] translated = pts.Select(q => new[] { q[0] - minX, q[1] - minY }).SelectMany(q => q).ToArray();
        var local = new double[pts.Length][];
        for (int i = 0; i < pts.Length; i++) local[i] = new[] { translated[i * 2], translated[i * 2 + 1] };

        dto.X = minX; dto.Y = minY;
        dto.Width = maxX - minX; dto.Height = maxY - minY;
        dto.PathData = PathBuilder.Build(local);
        if (kind is ShapeKind.Polyline or ShapeKind.BandPolyline)
            dto.IsClosed = p.Closed;
    }

    private static void MapFixed(DslPrimitive p, ShapeKind kind, PrimitiveItemDto dto)
    {
        // 优先圆心+半径定位（ellipse/ring/pie），否则用左上角 x/y + 宽高
        double w = p.Width ?? DefaultWidth(kind);
        double h = p.Height ?? DefaultHeight(kind);
        if (p.CenterX is double cx && p.CenterY is double cy && p.Radius is double r)
        {
            dto.Width = 2 * r; dto.Height = 2 * r;
            dto.X = cx - r; dto.Y = cy - r;
        }
        else
        {
            dto.X = p.X ?? 0; dto.Y = p.Y ?? 0;
            dto.Width = w; dto.Height = h;
        }

        switch (kind)
        {
            case ShapeKind.Ring:
                dto.InnerRadius = p.InnerRadius ?? Math.Min(dto.Width, dto.Height) / 4;
                break;
            case ShapeKind.Pie:
                dto.StartAngle = p.StartAngle ?? 0;
                dto.SweepAngle = p.SweepAngle ?? 60;
                break;
            case ShapeKind.FixedRect:
                if (p.CornerRadius is double cr) dto.CornerRadius = cr;
                break;
        }
    }

    private static string? ResolveLineAxis(DslPrimitive p)
        => p.Type?.Equals("line", StringComparison.OrdinalIgnoreCase) == true ? p.LineAxis : null;

    private static void ApplyAppearance(DslPrimitive p, PrimitiveItemDto dto)
    {
        dto.StrokeColor = p.StrokeColor;
        dto.StrokeWidth = p.StrokeWidth;
        dto.StrokeDashStyle = p.StrokeDash;
        dto.FillStyle = p.FillStyle;
        dto.FillColor = p.FillColor;
        dto.GradientStartColor = p.GradientStart;
        dto.GradientEndColor = p.GradientEnd;
        dto.FillBrightness = p.FillBrightness;
        dto.FillOpacity = p.FillOpacity;
        dto.PatternStyle = p.PatternStyle;
    }

    private static double DefaultWidth(ShapeKind kind) => kind switch
    {
        ShapeKind.FixedRect or ShapeKind.Ring or ShapeKind.Pie => 40,
        ShapeKind.Ellipse => 24,
        _ => 30,
    };

    private static double DefaultHeight(ShapeKind kind) => kind switch
    {
        ShapeKind.FixedRect or ShapeKind.Ring or ShapeKind.Pie => 40,
        ShapeKind.Ellipse => 24,
        _ => 30,
    };
}