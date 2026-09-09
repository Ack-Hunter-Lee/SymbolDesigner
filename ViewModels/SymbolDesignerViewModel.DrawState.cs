using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>画线（Draw）的实时状态字段 / 预览属性，以及选中图元类型判定（属性面板分支显示依据）。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== 画线模式状态 ==============

    /// <summary>画布交互模式。None=普通拖入摆放；BandPolyline=连续画带带宽的折线；Line=单击-拖动画单条直线（预留）。</summary>
    [ObservableProperty] private DrawMode _drawMode = DrawMode.None;

    public bool IsDrawModeActive => DrawMode != DrawMode.None;

    /// <summary>用户是否从工具栏 ComboBox 手动选择过画线模式。
    /// 为 true 时选中 BandPolyline 不再自动切模式（尊重用户手动选择）；
    /// 为 false 时选中 BandPolyline 自动进入绘制模式（本轮需求）。</summary>
    private bool _userChangedDrawMode;

    /// <summary>联动代码执行 DrawMode 赋值过程中置 true，防止"选中联动 → 触发 OnDrawModeChanged → 误标记用户手动"的回环。</summary>
    private bool _autoSyncingDrawMode;

    /// <summary>当 DrawMode 变化时同步通知 IsDrawModeActive。
    /// 区分来源：程序联动（_autoSyncingDrawMode=true）不标记用户手动；UI 手动选择（ComboBox）标记之。</summary>
    partial void OnDrawModeChanged(DrawMode value)
    {
        OnPropertyChanged(nameof(IsDrawModeActive));
        // 联动代码改的（选中 BandPolyline 自动进入 / 取消选中自动退出）不算用户手动选择
        if (!_autoSyncingDrawMode)
            _userChangedDrawMode = true;
        // 模式切换：手动选绘制工具 → 隐藏选区缩放手柄；退出绘制 → 恢复显示
        RefreshScaleSelectionHandles();
    }

    /// <summary>绘制开始/结束变化时同步选区缩放手柄显隐（绘制中隐藏,退出绘制恢复）。</summary>
    partial void OnIsDrawingChanged(bool value) => RefreshScaleSelectionHandles();

    /// <summary>在联动代码中安全地设置 DrawMode（不标记"用户手动选择"）。</summary>
    private void SetDrawModeFromSelection(DrawMode value)
    {
        _autoSyncingDrawMode = true;
        try { DrawMode = value; }
        finally { _autoSyncingDrawMode = false; }
    }

    /// <summary>画线模式时是否正在绘制中（鼠标按下到松开之间）。</summary>
    [ObservableProperty] private bool _isDrawing;

    /// <summary>当前草稿路径的 PathGeometry 字符串（XAML "M x,y L x,y L ..."），供 View 的实时预览 Path 绑定。
    /// 画线模式下随鼠标移动持续更新；松开后清空。</summary>
    [ObservableProperty] private string _draftPathData = string.Empty;

    /// <summary>画线草稿对应的坐标列表（画布坐标系）。
    /// 用 ObservableCollection 以便 XAML 草稿层实时同步渲染顶点标记（小白点），
    /// 让同一直线上连续连点时也能数清已连了几个点。</summary>
    public ObservableCollection<Point> DraftPoints { get; } = new();

    /// <summary>当前鼠标位置（用于"橡皮线"预览：最后一个已确认点到当前鼠标位置的连线）。
    /// 默认值 (NaN, NaN) 表示不渲染橡皮线。</summary>
    [ObservableProperty] private Point _draftPreviewPoint = new(double.NaN, double.NaN);

    /// <summary>绘制路径模式下的"接近吸附"激活态：当前段方向落在水平/竖直阈值内（CAD Polar Tracking）。
    /// XAML 绑到橡皮线 StrokeBrush——吸附时变绿提示用户"现在按水平/竖直锁定"。
    /// 形状模式不触发（形状有独立的预览 Path）。</summary>
    [ObservableProperty] private bool _isPathSnappingActive;

    /// <summary>绘制路径模式下"对象追踪 X 对齐"激活态：鼠标 X 与任一已画点 X 同坐标（误差范围内），
    /// XAML 画一条贯穿画布的浅色虚线提示"现在按 X 对齐到 P_i"。
    /// 与 IsPathSnappingActive（轴对齐）并行：可同时为 true——既轴对齐又对象追踪。
    /// 仅 Polyline/BandPolyline/Line 路径类参与。</summary>
    [ObservableProperty] private bool _isObjectSnapTrackingX;

    /// <summary>绘制路径模式下"对象追踪 Y 对齐"激活态：鼠标 Y 与任一已画点 Y 同坐标。
    /// XAML 画一条贯穿画布的横向虚线。语义与 IsObjectSnapTrackingX 对称。</summary>
    [ObservableProperty] private bool _isObjectSnapTrackingY;

    /// <summary>对象追踪命中点的 X 坐标（画布逻辑坐标，用于 XAML 画贯穿虚线的 Y1/Y2）。
    /// 仅在 IsObjectSnapTrackingX=true 时有效。</summary>
    [ObservableProperty] private double _objectSnapTrackingX;

    /// <summary>对象追踪命中点的 Y 坐标。语义与 ObjectSnapTrackingX 对称。</summary>
    [ObservableProperty] private double _objectSnapTrackingY;

    /// <summary>起点吸附闭合激活态：绘制路径类（Polyline/BandPolyline）时，鼠标移动到首端点附近
    /// （距离 &lt; CloseSnapThreshold）即置 true。
    /// XAML 绑到起点手柄高亮（变绿 + 放大）与橡皮线终点——吸附时橡皮线终点吸附到起点坐标，
    /// 提示用户"点击此处即可闭环"。</summary>
    [ObservableProperty] private bool _draftSnapToStart;

    /// <summary>形状绘制模式下"中心点 → 当前鼠标"实时生成的轮廓 Path Geometry（Rectangle/RoundedRectangle/Ellipse/Ring/Triangle）。
    /// 阶段 3：让用户在第 2 次点击前就能看到即将生成的形状轮廓（半透明蓝绿色），确认后再点提交。
    /// 路径类（Line/Polyline/BandPolyline）下保持空字符串，避免与 DraftPathData 重复渲染。</summary>
    [ObservableProperty] private string _draftShapeData = string.Empty;

    /// <summary>形状预览图层是否可见。条件：正在绘制（IsDrawing=true）且 DrawMode 属于"中心点+第二点"两段式形状模式。
    /// 用于 XAML 绑定 Visibility，避免形状路径类下显示空 Path。</summary>
    [ObservableProperty] private bool _isShapePreviewVisible;

    // ============== 选中图元类型判定（属性面板分支显示） ==============

    /// <summary>当前选中的是否为矩形类（Rectangle / RoundedRectangle）。用于显示属性面板。</summary>
    [ObservableProperty] private bool _selectedItemIsRect;

    /// <summary>是否有选中元素（任意类型）。用于显示通用属性（如透明度）。
    /// 在 SelectedItem set 时一并通知刷新,与 _selectedItemIsRect 同生命周期。</summary>
    [ObservableProperty] private bool _hasSelectedItem;

    /// <summary>当前选中的是否为 BandPolyline。属性面板显示"带宽"Slider。</summary>
    public bool SelectedItemIsBandPolyline =>
        SelectedItem is not null
        && SelectedItem.TypeKey == nameof(L1Primitive.BandPolyline);

    /// <summary>当前选中的是否为 Polyline（BandPolyline 的无带宽兄弟类型）。
    /// 属性面板只显示类型提示，不暴露带宽控制（Polyline 永远 bandWidth=0）。</summary>
    public bool SelectedItemIsPolyline =>
        SelectedItem is not null
        && SelectedItem.TypeKey == nameof(L1Primitive.Polyline);

    /// <summary>当前选中的是否为 L1 Ring（圆环）。为 true 时画布显示 9 个圆环拖拽手柄。</summary>
    public bool SelectedItemIsRing =>
        SelectedItem is not null
        && SelectedItem.TypeKey == nameof(L1Primitive.Ring);

    /// <summary>当前选中的是否为扇形（Pie）。为 true 时属性面板显示起始角/扫掠角滑块。</summary>
    public bool SelectedItemIsPie =>
        SelectedItem is not null
        && SelectedItem.TypeKey == nameof(L1Primitive.Pie);

    /// <summary>当前选中的是否为布尔运算结果（BooleanPath）。
    /// 布尔结果为"多环骨架快照"，骨架点直连闭合环，与闭合 Polyline 同一编辑管道：
    /// 骨架模式拖顶点（段端点，弧保留）→ 轮廓模式逐点设圆角。
    /// 画面显示顶点手柄（骨架点）与轮廓圆角手柄（RefreshVertexHandles/RefreshOutlineCornerHandles）。</summary>
    public bool SelectedItemIsBooleanPath =>
        SelectedItem is not null
        && SelectedItem.TypeKey == nameof(L1Primitive.BooleanPath);

    /// <summary>布尔结果骨架点诊断文本：每环每点 序号/坐标/到前一点距离/闭合距。
    /// 用于排查"轮廓点关系奇怪"（近重合簇、被 Combine 切分的贝塞尔交界点）。
    /// 索引 = VertexBandWidths 平铺序号（跨环累计）。</summary>
    public string SelectedBooleanSkeletonInfo
    {
        get
        {
            if (SelectedItem is null || SelectedItem.TypeKey != nameof(L1Primitive.BooleanPath))
                return string.Empty;
            try
            {
                var sb = new StringBuilder();
                int flat = 0;
                var rings = Converters.BooleanOps.GetSkeletonRings(SelectedItem.PathData);
                var figs = Converters.BooleanOps.ParseFigures(SelectedItem.PathData);
                for (int ri = 0; ri < rings.Count; ri++)
                {
                    var ring = rings[ri];
                    int segCount = ri < figs.Count ? figs[ri].Segs.Count : ring.Count;
                    sb.Append("环 ").Append(ri).Append("（").Append(ring.Count).Append(" 骨架点 / ").Append(segCount)
                      .Append(" 段）\r\n");
                    Point prev = ring[^1];
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var p = ring[i];
                        double d = (p - prev).Length;
                        sb.Append("  #").Append(flat + i)
                          .Append(" (").Append(p.X.ToString("0.##")).Append(", ").Append(p.Y.ToString("0.##")).Append(')')
                          .Append("  距前 ").Append(d.ToString("0.###"));
                        if (i == 0)
                            sb.Append("  ←闭合点");
                        sb.Append('\n');
                        prev = p;
                    }
                    sb.Append("  闭合距 ").Append((ring[0] - ring[^1]).Length.ToString("0.###")).Append('\n');
                    flat += ring.Count;
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }
    }

    // ============== 带宽上限（属性面板 Slider） ==============

    /// <summary>
    /// 属性面板"带宽"Slider 的动态上限。画布尺寸变大时同步放大,
    /// 让用户在大画布(几百 px)上也能调到合适的带宽。
    /// 公式:
    ///   longEdge = max(CanvasWidth, CanvasHeight)
    ///   maxBandWidth = max(30, longEdge)
    ///   - 小画布(长边 ≤ 30):下限 30(保持原手感)
    ///   - 普通 1920×1080 画布:上限 = 长边 1920(给足空间,想多宽调多宽)
    ///   - 不再设额外封顶:画布尺寸本身就是合理界,带宽不可能超过画布
    /// 画布尺寸改变时通过 partial OnCanvasWidthChanged/OnCanvasHeightChanged 重新通知。
    /// 实现为 ObservableProperty 而非 get-only 计算属性,
    /// 是因为 WPF Slider.Maximum 绑定在 get-only 属性上时会推断 TwoWay 失败。
    /// </summary>
    [ObservableProperty] private double _selectedBandWidthMax = 30;

    /// <summary>画布尺寸变化时重新计算带宽上限并通知 UI 刷新。</summary>
    private void RecomputeSelectedBandWidthMax()
    {
        var longEdge = System.Math.Max(1, System.Math.Max(CanvasWidth, CanvasHeight));
        var newMax = System.Math.Max(30, longEdge);
        // 等值时不写(避免冗余通知)
        if (System.Math.Abs(SelectedBandWidthMax - newMax) < 0.001) return;
        SelectedBandWidthMax = newMax;
    }
}