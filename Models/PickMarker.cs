namespace SymbolDesigner.Models;

/// <summary>点击某图元时识别出的子对象类别（重合约束等子对象操作依据）。</summary>
public enum PickKind
{
    /// <summary>未识别 / 普通图形。</summary>
    None = 0,

    /// <summary>直线命中的是端点（PathData 首点）。</summary>
    EndPoint0 = 1,

    /// <summary>直线命中的是端点（PathData 末点）。</summary>
    EndPoint1 = 2,

    /// <summary>直线命中的是线段本体。</summary>
    Segment = 3,

    /// <summary>圆命中的是圆心。</summary>
    Center = 4,

    /// <summary>圆命中的是圆弧。</summary>
    Arc = 5,
}

/// <summary>多选子对象拾取标记：在画布上以显眼小圆点高亮"被识别为子对象"的命中点
/// （圆心的圆心点、直线的端点）。用于让用户直观确认重合约束选中了哪个子对象
/// （圆心 vs 圆弧、端点 vs 线段）。</summary>
public class PickMarker
{
    /// <summary>画布逻辑 X。</summary>
    public double X { get; set; }

    /// <summary>画布逻辑 Y。</summary>
    public double Y { get; set; }

    /// <summary>是否为"圆心"（圆点用橙色以区别端点的绿色）。</summary>
    public bool IsCircleCenter { get; set; }
}