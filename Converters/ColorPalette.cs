using System.Collections.Generic;

namespace SymbolDesigner.Converters;

/// <summary>
/// 取色面板数据源：主网格（9 列 × 8 行 = 72 格）+ 标准色 10 色。
/// 矩阵布局 = 第 1 列灰度梯度（黑→白），其余 8 列 8 种色相 × 8 行明度梯度。
/// "无填充"快捷项独立放在矩阵外部（弹层"无填充"按钮），不再嵌入矩阵第 1 列，
/// 避免"一堆斜线"塞满最左一列的视觉污染。
/// 数据来源：参照 Office/Visio 经典主题色板。
/// </summary>
public static class ColorPalette
{
    /// <summary>主网格 9 列 × 8 行的 hex 字符串矩阵。</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> MainGrid = BuildMainGrid();

    /// <summary>标准色（10 个，纯色）。用于"标准颜色"行。</summary>
    public static readonly IReadOnlyList<string> StandardColors = new[]
    {
        "#C00000", // 深红
        "#FFC000", // 金黄
        "#FFFF00", // 纯黄
        "#92D050", // 亮绿
        "#00B050", // 标准绿
        "#00B0F0", // 天蓝
        "#0070C0", // 深蓝
        "#002060", // 海军蓝
        "#7030A0", // 紫色
        "#D9D9D9", // 浅灰
    };

    /// <summary>
    /// 主矩阵 = 8 色相（红/橙/黄/绿/青/蓝/紫/品红）+ 1 列灰度 = 9 列 × 8 行 = 72 格。
    /// 第 1 列（最左）= 灰度（行从黑→白 8 阶）；
    /// 第 2~9 列（8 个色相）= 行 0~7 对应明度梯度（行 0 最亮饱和度低，行 7 最深）。
    /// 数据：参照 Office 标准色板（#FFFFFF→#000000 灰度梯度 + 8 色相 × 6~8 明度阶）。
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> BuildMainGrid()
    {
        // 第 1 列 = 灰度梯度（行 0 最亮 → 行 7 最暗），与 Office "黑→白" 灰度一致
        string[] grayColumn =
        {
            "#FFFFFF", // 行 0：白
            "#D9D9D9", // 行 1
            "#BFBFBF", // 行 2
            "#A6A6A6", // 行 3
            "#808080", // 行 4
            "#595959", // 行 5
            "#404040", // 行 6
            "#000000", // 行 7：黑
        };

        // 8 色相 × 8 明度梯度（按 HSV 系统：S=0.85 V 从 0.95→0.45 递减 + 暗调修正）
        // 列顺序：红 / 橙 / 黄 / 绿 / 青 / 蓝 / 紫 / 品红
        // 每个色相 8 行 = 同一 H、不同 S×V 组合，明度从浅到深
        string[] hueColumns = BuildHueColumns();

        var rows = new List<IReadOnlyList<string>>();
        for (int row = 0; row < 8; row++)
        {
            var r = new string[9];
            r[0] = grayColumn[row]; // 第 1 列：灰度
            for (int col = 0; col < 8; col++)
                r[col + 1] = hueColumns[col * 8 + row];
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>返回 8 个色相 × 8 行明度梯度 = 64 个色 hex（按列排布：列0行0..7, 列1行0..7 ...）。
    /// 行 0 最浅/最低饱和 → 行 7 最深/最高饱和。</summary>
    private static string[] BuildHueColumns()
    {
        // 8 个色相的基色（H 值固定）：红 0°/ 橙 30°/ 黄 60°/ 绿 120°/ 青 180°/ 蓝 240°/ 紫 280°/ 品红 320°
        double[] hues = { 0, 30, 60, 120, 180, 240, 280, 320 };
        // 8 行明度梯度：V 从 0.95 → 0.40，S 从 0.10 → 0.90（行 0 浅淡 → 行 7 浓艳）
        double[] values  = { 0.97, 0.92, 0.85, 0.78, 0.68, 0.58, 0.48, 0.40 };
        double[] sats    = { 0.10, 0.30, 0.55, 0.75, 0.85, 0.90, 0.95, 0.95 };

        var all = new string[8 * 8];
        for (int col = 0; col < 8; col++)
        {
            double h = hues[col];
            for (int row = 0; row < 8; row++)
                all[col * 8 + row] = HsvToHex(h, sats[row], values[row]);
        }
        return all;
    }

    private static string HsvToHex(double h, double s, double v)
    {
        // 标准 HSV→RGB
        if (s == 0)
        {
            byte gg = (byte)(v * 255);
            return $"#{gg:X2}{gg:X2}{gg:X2}";
        }
        h %= 360; if (h < 0) h += 360;
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);
        double r, g, bl;
        switch (hi)
        {
            case 0: r = v; g = t; bl = p; break;
            case 1: r = q; g = v; bl = p; break;
            case 2: r = p; g = v; bl = t; break;
            case 3: r = p; g = q; bl = v; break;
            case 4: r = t; g = p; bl = v; break;
            default: r = v; g = p; bl = q; break;
        }
        return $"#{(byte)(r * 255):X2}{(byte)(g * 255):X2}{(byte)(bl * 255):X2}";
    }
}