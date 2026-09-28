using System.Globalization;

namespace SymbolDesigner.Cli;

/// <summary>WPF Path mini-language 序列化（"M x,y L x,y ..."）。
/// PathData 是相对 (X,Y) 的局部坐标；调用方需先把顶点身平移到非负 bbox。</summary>
internal static class PathBuilder
{
    public static string Build(IReadOnlyList<double[]> pts)
    {
        if (pts.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append("M ").Append(pts[0][0].ToString("0.##", CultureInfo.InvariantCulture))
          .Append(',').Append(pts[0][1].ToString("0.##", CultureInfo.InvariantCulture));
        for (int i = 1; i < pts.Count; i++)
            sb.Append(" L ").Append(pts[i][0].ToString("0.##", CultureInfo.InvariantCulture))
              .Append(',').Append(pts[i][1].ToString("0.##", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}