using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;
using SymbolDesigner.Svg;

namespace SymbolDesigner.ViewModels;

/// <summary>
/// 从外部 SVG 文件导入到画布。
/// 流程:打开文件选择器 → SvgParser 解析 → 图元直接按 SVG 坐标入画布(viewBox 仅用于补偿起点偏移,不做缩放/居中)→ Primitives.Add 批量入栈。
/// 整次导入作为一次可撤销步骤(BeginEdit/EndEdit 包起来)。
///
/// 限制:
/// - 仅支持 MVP 范围:&lt;rect&gt;/&lt;circle&gt;/&lt;ellipse&gt;/&lt;line&gt;/&lt;polyline&gt;/&lt;polygon&gt;/&lt;path&gt;
/// - SVG 单位视为画布单位 1:1(图元 X/Y/Width/Height 直接来自 SVG 属性,viewBox 起点偏移作为平移补偿)
/// - 颜色:stroke / fill 解析为 hex 写入 PrimitiveItem
/// - 曲线 C/Q/A/S/T:PathDataSampler 降采样为折线段(精度 0.5)
/// 不支持(留作后续迭代):&lt;text&gt;/&lt;image&gt;/&lt;use&gt;/渐变/滤镜/图案。
/// </summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>导入后的整体平移:算所有图元的最小包围盒,若最小 X 或最小 Y < 落点 X/Y,
    /// 整体平移 (落点X - 最小X, 落点Y - 最小Y) 把包围盒左上角对齐到落点。
    /// 落点 = (10, 10),留 10 像素画布边距。
    /// 注:只正向平移(让元素落到画布内),不裁剪超出画布尺寸的元素——若元素整体大于画布,允许溢出。</summary>
    private void NormalizeIntoCanvas(System.Collections.Generic.List<Models.PrimitiveItem> items)
    {
        if (items.Count == 0) return;
        const double dropX = 10;
        const double dropY = 10;

        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var item in items)
        {
            if (item.X < minX) minX = item.X;
            if (item.Y < minY) minY = item.Y;
        }
        if (minX == double.MaxValue) return;

        double dx = minX < dropX ? dropX - minX : 0;
        double dy = minY < dropY ? dropY - minY : 0;
        if (dx == 0 && dy == 0) return;

        foreach (var item in items)
        {
            item.X += dx;
            item.Y += dy;
        }
    }
    /// <summary>导入 SVG 命令(由 View 在打开文件对话框后调用)。
    /// canvasSize 仅用于 SVG 单位与画布单位的兼容性校验,实际不做缩放。</summary>
    [RelayCommand]
    public void ImportSvg(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        // 跨文件对话框拿到路径可能不是绝对路径,这里不强校验
        var result = SvgParser.ParseFile(filePath, new Size(CanvasWidth, CanvasHeight));
        if (result.Items.Count == 0)
        {
            // 没有可导入的图元,提示
            System.Windows.MessageBox.Show(
                "未在 SVG 中找到可导入的图形元素。\n已支持的元素:rect/circle/ellipse/line/polyline/polygon/path。",
                "SVG 导入",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // 整次导入作为一次可撤销步骤
        BeginEdit();

        // 整体平移:把图元组最小包围盒左上角对齐到画布 (10, 10) 落点(留 10 像素边距)。
        // 原因:SVG 坐标可能为负,或在画布外/被画布边界遮挡,统一平移到画布可视区域内。
        NormalizeIntoCanvas(result.Items);

        foreach (var item in result.Items)
        {
            item.ZIndex = Primitives.Count;  // 追加到顶层
            Primitives.Add(item);
        }
        ItemCount = Primitives.Count;
        // 选中最先导入的图元(方便用户立即看到)
        SelectedItem = result.Items.FirstOrDefault();
        EndEdit();

        // 若有警告,简单提示一次(不阻断导入)
        if (result.Warnings.Count > 0)
        {
            var warnings = string.Join("\n", result.Warnings.Take(5));
            System.Windows.MessageBox.Show(
                $"导入成功 {result.Items.Count} 个图元。\n以下警告被记录:\n{warnings}",
                "SVG 导入",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }
}