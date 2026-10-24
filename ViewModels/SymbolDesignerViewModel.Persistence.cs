using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>工程持久化：Save/SaveAs/Load 命令 + SymbolProject DTO 序列化 / 反序列化（画布快照与工程加载复用）。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== 工程保存/加载（编辑数据持久化，区别于 XAML 导出） ==============

    /// <summary>当前工程文件路径。null 表示"未保存"（新建或刚加载后又改）。
    /// 改变时清 IsDirty、保存成功后更新；用于判断 Save 是否走"另存为"路径。</summary>
    [ObservableProperty] private string? _currentFilePath;

    /// <summary>画布自上次保存/加载后是否被修改。true 时标题栏显示 "*" 提示。
    /// 任何 Primitives 增删/编辑、属性修改都应置 true；保存/加载成功后置 false。</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>当前窗口标题文本：文件名 + IsDirty 标记。
    /// View 侧 Window.Title 可绑定此属性（如果有的话）。</summary>
    public string WindowTitle
    {
        get
        {
            var name = CurrentFilePath is null
                ? "未命名"
                : System.IO.Path.GetFileName(CurrentFilePath);
            var dirty = IsDirty ? " *" : "";
            return $"{name}{dirty} - 元件图形设计";
        }
    }

    partial void OnCurrentFilePathChanged(string? value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(WindowTitle));

    /// <summary>取"另存为"的默认文件名：剥掉 .sym.json / .symz 这类多段后缀，
    /// 避免把工程名带成 "a.sym" 后被追加新扩展名。</summary>
    private static string ProjectBaseName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Symbol";
        var name = System.IO.Path.GetFileName(path);
        if (name.EndsWith(SymbolProjectFile.LegacyExtension, System.StringComparison.OrdinalIgnoreCase))
            return name[..^SymbolProjectFile.LegacyExtension.Length];
        return System.IO.Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>保存到当前文件：若已有路径直接覆盖，否则转 SaveAs 让用户选位置。
    /// 是"保存"按钮的实际行为。</summary>
    [RelayCommand]
    private void SaveProject()
    {
        if (string.IsNullOrEmpty(CurrentFilePath))
        {
            SaveAsProject();
            return;
        }
        try
        {
            // 就地保存：格式跟随当前文件扩展名（.symz 打包 / .sym.json 纯文本），不擅自升级用户文件
            SymbolProjectFile.Save(CurrentFilePath, ToSymbolProject());
            IsDirty = false;
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>另存为：总是弹窗让用户选路径（与 Save 不同）。
    /// 保存成功后更新 CurrentFilePath、清 IsDirty。</summary>
    [RelayCommand]
    private void SaveAsProject()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = ProjectBaseName(CurrentFilePath),
            DefaultExt = SymbolProjectFile.ContainerExtension,
            Filter = "元件图形工程 (*.symz;*.sym.json)|*.symz;*.sym.json|压缩包工程 (*.symz)|*.symz|纯文本工程 (*.sym.json)|*.sym.json|所有文件 (*.*)|*.*",
            AddExtension = true,
            Title = "另存为元件图形工程",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            SymbolProjectFile.Save(dlg.FileName, ToSymbolProject());
            CurrentFilePath = dlg.FileName;
            IsDirty = false;
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>从工程文件（.symz 容器 / .sym.json 纯文本）加载，重建 Primitives 集合。
    /// 加载后选中状态清空，VertexHandles 按新选中项刷新。</summary>
    [RelayCommand]
    private void LoadProject()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            DefaultExt = SymbolProjectFile.ContainerExtension,
            Filter = "元件图形工程 (*.symz;*.sym.json)|*.symz;*.sym.json|压缩包工程 (*.symz)|*.symz|纯文本工程 (*.sym.json)|*.sym.json|所有文件 (*.*)|*.*",
            Title = "加载元件图形工程",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var loaded = SymbolProjectFile.Load(dlg.FileName);
            if (loaded is null)
            {
                System.Windows.MessageBox.Show("文件格式无效", "错误",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }
            FromSymbolProject(loaded.Project, loaded.Assets);
            // 加载成功后，记录路径、清 IsDirty（加载后是"干净"状态）
            CurrentFilePath = dlg.FileName;
            IsDirty = false;
            ResetUndoRedo(); // 新文档起点:历史清空
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"加载失败：{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>当前 Primitives 集合转为 SymbolProject（DTO 序列化容器）。</summary>
    private SymbolProject ToSymbolProject()
    {
        // 若仍处于"调整源"模式 → 先收起（丢弃物化源），防止把临时层叠写进工程文件
        if (IsSourceAdjustMode && _sourceAdjustTarget != Guid.Empty)
        {
            var t = Primitives.FirstOrDefault(p => p.Id == _sourceAdjustTarget);
            if (t is not null)
            {
                CollapseSourceAdjust(t);
                NotifyBooleanCanExecuteChanged();
            }
        }

        var project = new SymbolProject
        {
            CanvasWidth = CanvasWidth,
            CanvasHeight = CanvasHeight,
            CanvasBackgroundColor = CanvasBackgroundColor,
            BackgroundImagePath = BackgroundImagePath,
            BackgroundOpacity = BackgroundOpacity,
            BackgroundFitMode = BackgroundFitMode,
        };

        // 背景图：直接用导入/加载时缓存的字节（不再回头读外部文件）→
        // 外部原图被删/移动也不会丢底图。存 .sym.json 时内联 Base64；
        // 存 .symz 时由 SymbolProjectFile 自动迁入 assets/ 并清空内联字段。
        if (_backgroundImageBytes is { Length: > 0 } bgBytes)
        {
            project.BackgroundImageData = bgBytes;
            project.BackgroundImageType = string.IsNullOrEmpty(_backgroundImageExt) ? "png" : _backgroundImageExt;
        }
        foreach (var p in Primitives)
            project.Primitives.Add(ToDto(p));
        // 沿路径阵列：保存所有块引用。null = 老工程无此字段 → 反序列化时退回空集合。
        foreach (var a in ArrayInstances)
            (project.ArrayInstances ??= new()).Add(ArrayInstanceToDto(a));
        // 约束：按 Id 序列化，加载时按 Kind 重新关联图元（与撤销快照共用 ToConstraintDto）。
        foreach (var c in Constraints)
            (project.Constraints ??= new()).Add(ToConstraintDto(c));
        return project;
    }

    /// <summary>把单个 PrimitiveItem 转成可序列化 DTO（供画布快照 / 工程保存复用）。</summary>
    private static PrimitiveItemDto ToDto(PrimitiveItem p)
    {
        var dto = new PrimitiveItemDto
        {
            // 旧版 Kind = p.Kind.ToString() 已删除（L1/L2 分类废弃；序列化不再输出 Kind 字段）。
            Id = p.Id.ToString(), // 持久约束引用图元的唯一标识
            TypeKey = p.TypeKey,
            X = p.X,
            Y = p.Y,
            Width = p.Width,
            Height = p.Height,
            CornerRadius = p.CornerRadius,
            InnerRadius = p.InnerRadius,
            StartAngle = p.StartAngle,
            SweepAngle = p.SweepAngle,
            Rotation = p.Rotation,
            ZIndex = p.ZIndex,
            LineAxis = p.LineAxis.ToString(),
            PathData = p.PathData,
            Thickness = p.BandWidth,
            OutlineJoinStyle = p.OutlineJoinStyle.ToString(),
            CollinearSplitPairs = p.CollinearSplitPairs,
            Opacity = p.Opacity,
            IsVisible = p.IsVisible,
            IsLocked = p.IsLocked,
            IsClosed = p.IsClosed,
            BooleanMode = p.BooleanMode,
            // 外观（线条/填充）——始终写出当前值
            StrokeColor = p.StrokeColor,
            StrokeWidth = p.StrokeWidth,
            StrokeDashStyle = p.StrokeDashStyle.ToString(),
            FillStyle = p.FillStyle.ToString(),
            FillColor = p.FillColor,
            GradientStartColor = p.GradientStartColor,
            GradientEndColor = p.GradientEndColor,
            FillBrightness = p.FillBrightness,
            FillOpacity = p.FillOpacity,
            // 图案填充（仅 FillStyle=Pattern 时实际生效；老工程忽略此组字段）
            PatternForeground = p.PatternForeground,
            PatternBackground = p.PatternBackground,
            PatternForegroundBrightness = p.PatternForegroundBrightness,
            PatternForegroundOpacity = p.PatternForegroundOpacity,
            PatternBackgroundBrightness = p.PatternBackgroundBrightness,
            PatternBackgroundOpacity = p.PatternBackgroundOpacity,
            PatternStyle = p.PatternStyle.ToString(),
            PatternTileSize = p.PatternTileSize,
            PatternSpacing = p.PatternSpacing,
        };
        if (p.BooleanSourceSnapshot.Count > 0)
            dto.BooleanSources = p.BooleanSourceSnapshot.ToList();
        // 传送带：只落盘"绕行拓扑 + 两个自由端点"（几何由 BeltSolver 重算，不序列化）
        if (p.TypeKey == nameof(L1Primitive.Belt))
        {
            dto.BeltStartX = p.BeltStartX;
            dto.BeltStartY = p.BeltStartY;
            dto.BeltEndX = p.BeltEndX;
            dto.BeltEndY = p.BeltEndY;
            if (p.BeltAnchors.Count > 0)
            {
                dto.BeltAnchors = new List<BeltAnchorDto>(p.BeltAnchors.Count);
                foreach (var a in p.BeltAnchors)
                    dto.BeltAnchors.Add(new BeltAnchorDto { CircleId = a.CircleId.ToString(), Wrap = a.Wrap });
            }
        }
        foreach (var v in p.VertexBandWidths)
            dto.VertexThicknesses.Add(new VertexThicknessDto
            {
                Index = v.Index,
                IncomingThickness = v.IncomingBandWidth,
                OutgoingThickness = v.OutgoingBandWidth,
                JoinInnerRadius = v.JoinInnerRadius,
                LeftCornerRadius = v.LeftCornerRadius,
                RightCornerRadius = v.RightCornerRadius,
            });
        // 逐顶点接缝样式（稀疏：只序列化非 null 项）
        var vjs = p.VertexJoinStylesSnapshot;
        for (int i = 0; i < vjs.Count; i++)
        {
            if (vjs[i].HasValue)
            {
                dto.VertexJoinStyles ??= new();
                dto.VertexJoinStyles.Add(new VertexJoinStyleDto
                {
                    Index = i,
                    Style = vjs[i]!.Value.ToString(),
                });
            }
        }
        return dto;
    }

    /// <summary>容错解析 JoinStyle：老文件可能没有此字段（为空或 null），默认返回 Miter。</summary>
    private static JoinStyle ParseJoinStyle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return JoinStyle.Miter;
        return System.Enum.TryParse<JoinStyle>(s, out var result) ? result : JoinStyle.Miter;
    }

    /// <summary>容错解析线轴模式：老文件缺省 / 非法值 → Free（与模型字段默认值一致）。</summary>
    private static LineAxisMode ParseLineAxis(string? s)
    {
        if (string.IsNullOrEmpty(s)) return LineAxisMode.Free;
        return System.Enum.TryParse<LineAxisMode>(s, out var result) ? result : LineAxisMode.Free;
    }

    /// <summary>容错解析虚线样式：老文件缺省 Solid。</summary>
    private static Models.StrokeDashStyle ParseStrokeDashStyle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Models.StrokeDashStyle.Solid;
        return System.Enum.TryParse<Models.StrokeDashStyle>(s, out var result) ? result : Models.StrokeDashStyle.Solid;
    }

    /// <summary>容错解析填充方式：老文件缺省 None（透明，与旧版形状一致）。</summary>
    private static Models.FillStyle ParseFillStyle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Models.FillStyle.None;
        return System.Enum.TryParse<Models.FillStyle>(s, out var result) ? result : Models.FillStyle.None;
    }

    /// <summary>容错解析图案样式：老文件缺省 Diagonal（45° 斜线，最常用）。</summary>
    private static Models.PatternStyle ParsePatternStyle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Models.PatternStyle.Diagonal;
        return System.Enum.TryParse<Models.PatternStyle>(s, out var result) ? result : Models.PatternStyle.Diagonal;
    }

    /// <summary>从 SymbolProject 重建 Primitives 集合（清空原集合后填充）。
    /// <paramref name="assets"/> 为容器（.symz）内解出的资产（逻辑键 → 原始字节）；纯文本工程传 null。</summary>
    private void FromSymbolProject(SymbolProject project, IReadOnlyDictionary<string, byte[]>? assets = null)
    {
        // 加载画布尺寸
        CanvasWidth = project.CanvasWidth;
        CanvasHeight = project.CanvasHeight;

        // 加载画布背景色:老工程无此字段 → 保持当前值(主题默认)
        if (!string.IsNullOrEmpty(project.CanvasBackgroundColor))
            CanvasBackgroundColor = project.CanvasBackgroundColor!;

        // 加载背景参考图，优先级：容器资产(.symz) → 内联 Base64(.sym.json) → 老工程外部路径。
        // 前两者都随文件自包含；外部路径仅作老工程兼容（路径可能已失效）。都拿不到则静默不显示。
        if (assets is not null
            && assets.TryGetValue(SymbolProjectFile.BackgroundAssetKey, out var assetBytes)
            && assetBytes.Length > 0)
        {
            var mediaType = project.Assets?
                .FirstOrDefault(a => a.Key == SymbolProjectFile.BackgroundAssetKey)?.MediaType;
            LoadBackgroundImage(assetBytes, mediaType?.Split('/').LastOrDefault());
            BackgroundImagePath = null;   // 资产随包自包含，没有外部来源文件
        }
        else if (project.BackgroundImageData is { Length: > 0 } bytes)
        {
            // 嵌入数据优先：无需任何外部文件
            LoadBackgroundImage(bytes, project.BackgroundImageType);
            // BackgroundImagePath 也设上(保持 UI 显示文件名一致);但即便 Path 失效也不影响显示
            BackgroundImagePath = project.BackgroundImagePath;
        }
        else if (!string.IsNullOrEmpty(project.BackgroundImagePath)
                 && File.Exists(project.BackgroundImagePath))
        {
            // 老工程兼容：只有外部路径
            LoadBackgroundImage(project.BackgroundImagePath);
        }
        else
        {
            BackgroundImageSource = null;
            BackgroundImagePath = null;
        }
        BackgroundOpacity = project.BackgroundOpacity <= 0 ? 0.5 : project.BackgroundOpacity;
        BackgroundFitMode = string.IsNullOrEmpty(project.BackgroundFitMode) ? "CanvasSize" : project.BackgroundFitMode;

        // 清空当前画布（走 ClearAllCore：加载新文档时清空不应进入撤销历史）
        ClearAllCore(scheduleUndo: false);
        foreach (var dto in project.Primitives)
            Primitives.Add(CreateFromDto(dto));
        ItemCount = Primitives.Count;
        // 加载沿路径阵列（老工程无此字段 → 跳过；null 安全）
        ArrayInstances.Clear();
        if (project.ArrayInstances != null)
            foreach (var adto in project.ArrayInstances)
                ArrayInstances.Add(ArrayInstanceFromDto(adto));
        // 显式重建渲染层（避免 Clear()+Add() 之间触发的事件链覆盖初始空渲染）
        RebuildArrayRenderItems();
        // 约束加载：按 Kind 分派到具体约束子类（与撤销快照共用 ConstraintFromDto；未来新类型在此扩展）
        Constraints.Clear();
        if (project.Constraints != null)
        {
            foreach (var cdto in project.Constraints)
            {
                var constraint = ConstraintFromDto(cdto);
                if (constraint is not null)
                    Constraints.Add(constraint);
            }
            PruneConstraints();
        }
        // 选中状态清空，VertexHandles 也清空（用户重新点击选中后才显示手柄）
        SelectedItem = null;
        VertexHandles.Clear();
        SegmentHandles.Clear();
        OutlineCornerHandles.Clear();
        // 传送带几何不落盘：图元 + 滚轮全部就位后统一重算一次
        SolveAllBelts();
    }

    /// <summary>序列化单条几何约束为 DTO（工程保存与撤销快照共用；与 ConstraintFromDto 对称）。
    /// 新约束类型在 switch 中扩展（此时 DTO 需相应增字段）。</summary>
    private static TangencyConstraintDto ToConstraintDto(IGeometricConstraint c)
    {
        var dto = new TangencyConstraintDto { Id = c.Id.ToString() };
        switch (c)
        {
            case LineCircleTangentConstraint lc:
                dto.Kind = GeometricConstraintKind.LineCircleTangent;
                dto.LineId = lc.LineId.ToString();
                dto.CircleId = lc.CircleId.ToString();
                dto.Side = lc.Side;
                dto.AnchorIsLine = lc.AnchorIsLine;
                break;
            case LineCircleCoincidentConstraint cc:
                dto.Kind = GeometricConstraintKind.LineCircleCoincident;
                dto.LineId = cc.LineId.ToString();
                dto.CircleId = cc.CircleId.ToString();
                dto.CoincidentModeStr = cc.Mode.ToString();
                dto.EndpointIndex = cc.EndpointIndex;
                dto.LockToTangentPoint = cc.LockToTangentPoint;
                break;
            // 未来：CircleCircleTangent 在此扩展
        }
        return dto;
    }

    /// <summary>从 DTO 重建约束实例（工程加载与撤销/重做恢复共用；与 ToConstraintDto 对称）。
    /// 引用图元 Id 解析失败 → 返回 null，由调用方跳过（约束随缺失图元一并丢弃）。</summary>
    private static IGeometricConstraint? ConstraintFromDto(TangencyConstraintDto cdto)
    {
        if (!Guid.TryParse(cdto.LineId, out var lineId)) return null;
        if (!Guid.TryParse(cdto.CircleId, out var circleId)) return null;
        // 兼容老文件 CoincidentModeStr 空 → 默认 EndPointOnArc
        var cMode = Enum.TryParse<CoincidentMode>(cdto.CoincidentModeStr, out var cm)
            ? cm : CoincidentMode.EndPointOnArc;
        return cdto.Kind switch
        {
            GeometricConstraintKind.LineCircleCoincident =>
                new LineCircleCoincidentConstraint
                {
                    Id = cdto.Id is { } ccId && Guid.TryParse(ccId, out var ccg) ? ccg : Guid.NewGuid(),
                    LineId = lineId,
                    CircleId = circleId,
                    Mode = cMode,
                    EndpointIndex = cdto.EndpointIndex,
                    LockToTangentPoint = cdto.LockToTangentPoint,
                },
            // 未来：CircleCircleTangent 在此扩展
            _ => new LineCircleTangentConstraint
            {
                Id = cdto.Id is { } cid && Guid.TryParse(cid, out var cg) ? cg : Guid.NewGuid(),
                LineId = lineId,
                CircleId = circleId,
                Side = cdto.Side == -1 ? -1 : 1,
                AnchorIsLine = cdto.AnchorIsLine,
            },
        };
    }

    /// <summary>从 DTO 重建单个 PrimitiveItem（撤销/重做恢复与工程加载复用）。
    /// 注意：BandWidth 必须在填充 VertexBandWidths 之前赋值——BandWidth 是 [ObservableProperty]，
    /// 赋值会触发 OnBandWidthChanged 把所有顶点带宽覆盖为整体值；先赋（此时集合为空）则同步循环空转不覆盖。
    /// 之前顺序写反导致加载后各段带宽被抹平成整体 BandWidth（加载丢带宽 bug 根因）。</summary>
    private static PrimitiveItem CreateFromDto(PrimitiveItemDto dto)
    {
        var item = new PrimitiveItem
        {
            // 旧版 dto.Kind (L1/L2) 字段被忽略——所有图元身份仅由 TypeKey 决定；保留 dto.Kind 仅做向后兼容解析。
            Id = Guid.TryParse(dto.Id, out var g) ? g : Guid.NewGuid(), // 老文件无 Id → 分配新
            // TypeKey 兼容：旧工程文件存的是 "ThickPolyline"，新命名体系下是 "BandPolyline" → 加载时映射
            TypeKey = dto.TypeKey == "ThickPolyline" ? nameof(L1Primitive.BandPolyline) : dto.TypeKey,
            X = dto.X,
            Y = dto.Y,
            Width = dto.Width,
            Height = dto.Height,
            CornerRadius = dto.CornerRadius,
            InnerRadius = dto.InnerRadius > 0 ? dto.InnerRadius : 10,
            Rotation = dto.Rotation,
            ZIndex = dto.ZIndex,
            LineAxis = ParseLineAxis(dto.LineAxis),
            PathData = dto.PathData,
            OutlineJoinStyle = ParseJoinStyle(dto.OutlineJoinStyle),
            CollinearSplitPairs = dto.CollinearSplitPairs ?? true,
            IsClosed = dto.IsClosed,
            // 外观（线条/填充）：老文件缺省 → 默认观感（白描边 0.8 实线 / 无填充）
            StrokeColor = string.IsNullOrEmpty(dto.StrokeColor) ? "#FFFFFF" : dto.StrokeColor,
            StrokeWidth = dto.StrokeWidth ?? 0.8,
            StrokeDashStyle = ParseStrokeDashStyle(dto.StrokeDashStyle),
            FillStyle = ParseFillStyle(dto.FillStyle),
            FillColor = string.IsNullOrEmpty(dto.FillColor) ? "#40FFFFFF" : dto.FillColor,
            GradientStartColor = string.IsNullOrEmpty(dto.GradientStartColor) ? "#FFFFFFFF" : dto.GradientStartColor,
            GradientEndColor = string.IsNullOrEmpty(dto.GradientEndColor) ? "#40000000" : dto.GradientEndColor,
            FillBrightness = dto.FillBrightness is { } b ? System.Math.Clamp(b, 0, 100) : 100.0,
            FillOpacity = dto.FillOpacity is { } o ? System.Math.Clamp(o, 0, 100) : 100.0,
            // 图案填充（老工程缺省 → 模型字段默认值生效：前景深蓝/背景完全透明/Diagonal/8）
            PatternForeground = string.IsNullOrEmpty(dto.PatternForeground) ? "#60A0FF" : dto.PatternForeground,
            PatternBackground = string.IsNullOrEmpty(dto.PatternBackground) ? "#00000000" : dto.PatternBackground,
            PatternForegroundBrightness = dto.PatternForegroundBrightness is { } fb ? System.Math.Clamp(fb, 0, 100) : 100.0,
            PatternForegroundOpacity = dto.PatternForegroundOpacity is { } fo ? System.Math.Clamp(fo, 0, 100) : 100.0,
            PatternBackgroundBrightness = dto.PatternBackgroundBrightness is { } bb ? System.Math.Clamp(bb, 0, 100) : 100.0,
            PatternBackgroundOpacity = dto.PatternBackgroundOpacity is { } bo ? System.Math.Clamp(bo, 0, 100) : 100.0,
            PatternStyle = ParsePatternStyle(dto.PatternStyle),
            PatternTileSize = dto.PatternTileSize is { } ts ? System.Math.Clamp(ts, 4, 40) : 8.0,
            PatternSpacing = dto.PatternSpacing is { } ps ? System.Math.Clamp(ps, 1, 40) : 6.0,
        };
        // 先设整体 BandWidth（触发同步时 VertexBandWidths 尚未填充，不会覆盖任何顶点带宽）
        item.BandWidth = dto.Thickness;
        // 再填逐顶点带宽（per-vertex 独立值，覆盖整体 BandWidth 的位置）
        int vertexCount = dto.VertexThicknesses.Count;
        for (int i = 0; i < vertexCount; i++)
        {
            var vd = dto.VertexThicknesses[i];
            bool isStart = i == 0;
            bool isEnd = i == vertexCount - 1;
            item.VertexBandWidths.Add(new VertexBandWidth(
                vd.Index, vd.IncomingThickness, vd.OutgoingThickness, isStart, isEnd)
            {
                JoinInnerRadius = vd.JoinInnerRadius,
                LeftCornerRadius = vd.LeftCornerRadius,
                RightCornerRadius = vd.RightCornerRadius,
            });
        }
        // VertexBandWidths 已在此前填充完毕（整体 BandWidth 已在填充前赋值，见方法头注释）。
        // 若 VertexBandWidths 为空（如旧文件/整体带宽模式），补一次 SyncVertexBandWidths
        if (item.TypeKey == nameof(L1Primitive.BandPolyline)
            && item.VertexBandWidths.Count == 0
            && !string.IsNullOrEmpty(item.PathData))
        {
            // 顶点数从 PathData 解析
            var pts = Converters.BandOutlineBuilder.ParsePathData(item.PathData);
            item.SyncVertexBandWidths(pts.Count, item.BandWidth);
        }
        // 恢复逐顶点接缝样式（稀疏：只覆盖 DTO 中明确给出的项）
        if (dto.VertexJoinStyles != null && dto.VertexJoinStyles.Count > 0)
        {
            foreach (var vjs in dto.VertexJoinStyles)
            {
                if (vjs.Index >= 0 && vjs.Index < item.VertexJoinStylesSnapshot.Count)
                    item.SetVertexJoinStyle(vjs.Index, ParseJoinStyle(vjs.Style));
            }
        }
        // 恢复布尔运算源快照（老文件无此字段 → 快照为空，老版布尔结果不可调整源/重新合并）
        item.BooleanMode = dto.BooleanMode ?? string.Empty;
        if (dto.BooleanSources != null)
        {
            foreach (var src in dto.BooleanSources)
                item.BooleanSourceSnapshot.Add(src); // 反序列化所得已是独立副本，直接沿用
        }
        // 恢复元素透明度：老工程文件没这字段 → 默认 1.0（完全不透明,行为不变）
        item.Opacity = dto.Opacity <= 0 ? 1.0 : Math.Min(1.0, dto.Opacity);
        // 恢复元素可见性：老工程文件没这字段 → 默认 true（可见,行为不变）
        item.IsVisible = dto.IsVisible;
        // 恢复元素锁定：老工程文件没这字段 → 默认 false（未锁定,行为不变）
        item.IsLocked = dto.IsLocked;
        // 恢复传送带"绕行拓扑 + 自由端点"（几何不落盘 → 由加载后的 SolveAllBelts 重算）
        if (item.TypeKey == nameof(L1Primitive.Belt))
        {
            item.BeltStartX = dto.BeltStartX;
            item.BeltStartY = dto.BeltStartY;
            item.BeltEndX = dto.BeltEndX;
            item.BeltEndY = dto.BeltEndY;
            if (dto.BeltAnchors != null)
            {
                foreach (var a in dto.BeltAnchors)
                {
                    // 容错：Id 缺失/非法 或 该项承载的圆已被删除 → 跳过（皮带自愈，不崩溃）
                    if (Guid.TryParse(a.CircleId, out var circleId))
                        item.BeltAnchors.Add(new BeltAnchor(circleId, a.Wrap >= 0 ? 1 : -1));
                }
            }
        }
        return item;
    }

    /// <summary>ArrayInstance → ArrayInstanceDto（深拷贝 Source，确保快照与运行状态解耦）。</summary>
    private static ArrayInstanceDto ArrayInstanceToDto(ArrayInstance a)
    {
        return new ArrayInstanceDto
        {
            Id = a.Id,
            // Source 是深拷贝快照：用 ToDto → CreateFromDto 链路保证与运行状态完全解耦。
            // 直接赋值 DTO 引用会导致后续修改 Source 字段影响快照（撤销恢复错乱）。
            Source = ArrayInstanceCloneSource(a.Source),
            ArrayType = (int)a.Type,
            // Path
            PathData = a.PathData,
            Count = a.Count,
            PathOffset = a.PathOffset,
            PathAlign = a.PathAlign,
            // Rectangular
            Columns = a.Columns,
            Rows = a.Rows,
            ColSpacing = a.ColSpacing,
            RowSpacing = a.RowSpacing,
            // Polar
            CenterX = a.CenterX,
            CenterY = a.CenterY,
            FillAngle = a.FillAngle,
            RotateItems = a.RotateItems,
            ZIndexOffset = a.ZIndexOffset,
            OffsetX = a.OffsetX,
            OffsetY = a.OffsetY,
        };
    }

    /// <summary>把传入的 PrimitiveItemDto 深拷贝一份（撤销快照专用）。
    /// 链路：临时建一个 PrimitiveItem → ToDto → CreateFromDto 重建 DTO → 拆出 Source。
    /// 走"实例化再序列化"链路是因为 VertexBandWidths 等集合结构在 DTO 上是简单 List，
    /// 但某些字段（如 OutlineJoinStyle 字符串）需要 Enum 解析兜底。</summary>
    private static PrimitiveItemDto ArrayInstanceCloneSource(PrimitiveItemDto source)
    {
        var item = CreateFromDto(source);
        return ToDto(item);
    }

    /// <summary>ArrayInstanceDto → ArrayInstance（加载/恢复）。
    /// 同样深拷贝 Source 字段 → 阵列与运行画布完全解耦。
    /// 旧文件 ArrayType 缺省 0 = Path → 加载不变。</summary>
    private static ArrayInstance ArrayInstanceFromDto(ArrayInstanceDto dto)
    {
        var t = (Models.ArrayType)Math.Clamp(dto.ArrayType, 0, 2);
        return new ArrayInstance
        {
            Id = string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
            Source = ArrayInstanceCloneSource(dto.Source),
            Type = t,
            // Path
            PathData = dto.PathData ?? string.Empty,
            Count = Math.Clamp(dto.Count, 1, 100),
            PathOffset = dto.PathOffset,
            PathAlign = dto.PathAlign,
            // Rectangular
            Columns = Math.Clamp(dto.Columns, 1, 100),
            Rows = Math.Clamp(dto.Rows, 1, 100),
            ColSpacing = dto.ColSpacing,
            RowSpacing = dto.RowSpacing,
            // Polar
            CenterX = dto.CenterX,
            CenterY = dto.CenterY,
            FillAngle = dto.FillAngle,
            RotateItems = dto.RotateItems,
            ZIndexOffset = dto.ZIndexOffset,
            OffsetX = dto.OffsetX,
            OffsetY = dto.OffsetY,
        };
    }
}