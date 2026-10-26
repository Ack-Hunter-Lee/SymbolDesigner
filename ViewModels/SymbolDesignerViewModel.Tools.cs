using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>画布工具栏：10 个绘制工具 + 激活态 TypeKey + 点击进入模式/取消。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>工具栏：10 个绘制工具（基础零件），统一走"点击进入绘制模式"。
    /// 圆角/倒角效果由属性面板在矩形上调，不再分两个按钮。
    /// <para>
    /// 图元身份只有一个来源：<see cref="L1Primitive"/> 枚举名。
    /// 工具项的 TypeKey = PrimitiveItem.TypeKey = L1Primitive 名 = DrawMode 名，
    /// 因此进入模式不需要再维护 key → DrawMode 的映射表（见 SelectTool）。
    /// </para></summary>
    public ObservableCollection<ToolboxEntry> DrawingTools { get; } = new()
    {
        new ToolboxEntry(nameof(L1Primitive.Rectangle),  "矩形",   "▭"),
        new ToolboxEntry(nameof(L1Primitive.Ellipse),    "椭圆",   "⬭"),
        new ToolboxEntry(nameof(L1Primitive.Triangle),   "三角形", "▲"),
        new ToolboxEntry(nameof(L1Primitive.Ring),       "圆环",   "◯"),
        new ToolboxEntry(nameof(L1Primitive.Pie),        "扇形",   "◔"),
        new ToolboxEntry(nameof(L1Primitive.RoundedRectangle), "圆角矩形", "▢"),
        new ToolboxEntry(nameof(L1Primitive.Line),       "直线",   "／"),
        new ToolboxEntry(nameof(L1Primitive.Curve),      "曲线",   "⌒"),
        new ToolboxEntry(nameof(L1Primitive.Polyline),   "折线",   "〰"),
        new ToolboxEntry(nameof(L1Primitive.BandPolyline), "带宽折线", "▰"),
        new ToolboxEntry(nameof(L1Primitive.Belt),       "传送带", "⛓"),
    };

    /// <summary>工具栏当前激活的图元 TypeKey（点击进入模式后置位，再点同一项取消）。
    /// 所有绘制工具（形状类 + 路径类）都能写入，供 XAML 高亮选中态图标。</summary>
    [ObservableProperty] private string? _activeToolKey;

    /// <summary>工具栏点击入口：所有工具都进入对应 DrawMode + 高亮。
    /// 形状类为"两次点击 = 中心点 + 对角点/圆周点"绘制；路径类进入画线状态机。
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
        // DrawMode 与图元类型同名（L1Primitive.Belt ↔ DrawMode.Belt），按名字直接解析即可
        DrawMode = Enum.TryParse<DrawMode>(key, out var mode) && Enum.IsDefined(mode)
            ? mode
            : DrawMode.None;
    }
}