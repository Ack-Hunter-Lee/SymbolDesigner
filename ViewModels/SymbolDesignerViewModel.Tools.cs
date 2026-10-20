using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>画布工具栏：8 个绘制工具 + 激活态 Key + 点击进入模式/取消。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>工具栏：8 个绘制工具（基础零件），统一走"点击进入绘制模式"。
    /// 圆角/倒角效果由属性面板在矩形上调，不再分两个按钮。
    /// 旧版有 L1Toolbox / L2Toolbox / DragDropTools / DrawTools 等分段集合，均已合并为单一系列：
    /// 所有工具语义一致（点击进模式），不再按"进入模式方式"区分，图元身份仅由
    /// ToolboxEntry.Key + PrimitiveItem.TypeKey 字符串决定。</summary>
    public ObservableCollection<ToolboxEntry> DrawingTools { get; } = new()
    {
        new ToolboxEntry("L1_Rectangle",  "矩形",   "▭"),
        new ToolboxEntry("L1_Ellipse",    "椭圆",   "⬭"),
        new ToolboxEntry("L1_Triangle",   "三角形", "▲"),
        new ToolboxEntry("L1_Ring",       "圆环",   "◯"),
        new ToolboxEntry("L1_Pie",        "扇形",   "◔"),
        new ToolboxEntry("L1_RoundedRectangle", "圆角矩形", "▢"),
        new ToolboxEntry("L1_Line",       "直线",   "／"),
        new ToolboxEntry("L1_Polyline",   "折线",   "〰"),
        new ToolboxEntry("L1_BandPolyline", "带宽折线", "▰"),
        new ToolboxEntry("L1_Belt",       "传送带", "⛓"),
    };

    /// <summary>工具栏当前激活的图元 Key（点击进入模式后置位，再点同一项取消）。
    /// 所有绘制工具（形状类 + 路径类）都能写入，供 XAML 高亮选中态图标。</summary>
    [ObservableProperty] private string? _activeToolKey;

    /// <summary>工具栏点击入口：所有 key（含 Rect/Ellipse/Ring/Triangle/RoundedRect 形状类）都进入对应 DrawMode + 高亮。
    /// 阶段 1 目标：彻底放弃拖拽路径，所有工具统一走"点击进入模式"语义。
    /// 形状类在阶段 2 实现"两次点击 = 中心点 + 对角点/圆周点"绘制；阶段 1 进入模式后画布暂无形状图元生成。
    /// 同一 Key 再点一次 → 取消激活并退回 DrawMode.None。</summary>
    [RelayCommand]
    private void SelectTool(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;

        // 同一 Key 再点 → 取消
        if (ActiveToolKey == key)
        {
            ActiveToolKey = null;
            DrawMode = DrawMode.None;
            _userChangedDrawMode = true;
            return;
        }

        ActiveToolKey = key;
        _userChangedDrawMode = true;
        DrawMode = key switch
        {
            "L1_Polyline"          => DrawMode.Polyline,
            "L1_BandPolyline"     => DrawMode.BandPolyline,
            "L1_Line"              => DrawMode.Line,
            "L1_Rectangle"         => DrawMode.Rectangle,
            "L1_RoundedRectangle"  => DrawMode.RoundedRectangle,
            "L1_Ellipse"           => DrawMode.Ellipse,
            "L1_Ring"              => DrawMode.Ring,
            "L1_Triangle"          => DrawMode.Triangle,
            "L1_Pie"               => DrawMode.Pie,
            "L1_Belt"              => DrawMode.Belt,
            _                      => DrawMode.None,
        };
    }
}