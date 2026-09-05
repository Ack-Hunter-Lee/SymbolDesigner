using System.Windows;
using System.Windows.Media;

namespace SymbolDesigner.Converters
{
    /// <summary>轮廓点种类：区分"骨线转折产生的转角点"与"普通轮廓点"。
    /// 圆角等轮廓编辑按此分类分开处理：转角点走骨架转角圆角，普通点走端点/轮廓点圆角。</summary>
    internal enum OutlinePointKind
    {
        /// <summary>普通轮廓点：端点偏移点、段末端偏移点、共线直通点。</summary>
        Normal,
        /// <summary>骨线转折产生的 miter 顶点（转角）。</summary>
        Corner,
    }

    /// <summary>轮廓 emit 顶点：1 个 emit 步骤。基类 + 三个具体形态（直线 / Bevel 拆点 / Round 圆弧）。
    /// 每种 emit 类型字段明确，不会跨模式混乱。
    /// 基类 Point = miter 顶点 / 圆心（接缝处的轮廓点），派生类存放各自的额外参数。
    /// 这三个类是几何抽象，跟 ThickOutlineBuilder 的算法实现解耦。
    /// 分段控制厚度由 VertexThickness[] 数组实现，本文件不涉及。</summary>

    /// <summary>普通直线：ctx 当前点 → Point。</summary>
    internal class PointStep
    {
        internal PointStep(Point point, OutlinePointKind kind = OutlinePointKind.Normal, int joinIndex = -1, int boneIndex = -1, int figureIndex = -1)
        {
            Point = point;
            Kind = kind;
            JoinIndex = joinIndex;
            BoneIndex = boneIndex;
            FigureIndex = figureIndex;
        }
        public Point Point { get; private set; }
        /// <summary>轮廓点种类（Normal = 普通点，Corner = 骨线转折产生的转角点）。
        /// 由 ComputeMiterOutline 在生成 miter 顶点时打标，编辑功能据此区分两类点。</summary>
        internal OutlinePointKind Kind { get; }
        /// <summary>该转折点对应的骨架接缝索引（segs 下标，即接缝位于骨架顶点 1+JoinIndex）。
        /// 非接缝点（Kind=Normal）为 -1。扰动 join 样式时用它定位 from/to 段上下文。</summary>
        internal int JoinIndex { get; }
        /// <summary>该步骤对应的骨架顶点索引（0=首端 .. n-1=末端）。
        /// 拆双对模式下 lefts 长度 = 2n-2 > n，不能用索引公式推导，必须显式携带。
        /// 圆角重建（ApplyPerVertexCornerRadius）与手柄映射（VM）统一读它，避免越界/错位。</summary>
        internal int BoneIndex { get; }
        /// <summary>所属闭合环序号（多环布尔结果按环分组；单环/ThickPolyline 无标记 = -1 整条序列一个 figure）。
        /// EmitSteps 按相邻不同的 FigureIndex 开新 figure，保证带洞多环各自闭合、不串环连线。</summary>
        internal int FigureIndex { get; }
        internal virtual void Emit(StreamGeometryContext ctx, bool isStartOfFigure = false)
            => ctx.LineTo(Point, isStroked: true, isSmoothJoin: false);

        /// <summary>返回该步骤 emit 的轮廓连接点列表。
        /// 基类返回 Point（该点本身即是轮廓顶点）。</summary>
        internal virtual List<Point> GetCapturePoints()
            => new List<Point> { Point };
    }

    /// <summary>Bevel 拆点：ctx 当前点 → StepPoint → Miter 顶点。
    /// 基类 Point = miter 顶点（接缝处的偏移轮廓点）。
    /// StepPoint = 台阶点（下一段起点偏移点），由主流程根据接缝位置算好后传入。
    /// emit 走 ctx → StepPoint → Miter 顶点（视觉合并）。</summary>
    internal sealed class BevelStep : PointStep
    {
        internal Point FirstPoint { get; }
        internal Point SecondPoint { get; }

        internal BevelStep(Point miter, Point first, Point second, OutlinePointKind kind = OutlinePointKind.Normal, int joinIndex = -1, int boneIndex = -1) : base(miter, kind, joinIndex, boneIndex)
        {
            FirstPoint = first;
            SecondPoint = second;
        }

        internal override void Emit(StreamGeometryContext ctx, bool isStartOfFigure = false)
        {
            ctx.LineTo(FirstPoint, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(SecondPoint, isStroked: true, isSmoothJoin: false);
        }

        /// <summary>返回两个拆点（FirstPoint → SecondPoint），基类 Point 不 emit。</summary>
        internal override List<Point> GetCapturePoints()
            => new List<Point> { FirstPoint, SecondPoint };
    }

    /// <summary>Round 圆弧：ctx 当前点（arcStart）→ 圆弧 → ArcEnd，圆心 = 基类 Point（miter 顶点）。
    /// Radius / Sweep 由主流程根据接缝位置算好后传入。</summary>
    internal sealed class ArcStep : PointStep
    {
        internal Point ArcStart { get; }
        internal Point ArcEnd { get; }
        internal double Radius { get; }
        internal SweepDirection Sweep { get; }

        internal ArcStep(Point center, Point arcStart, Point arcEnd, double radius, SweepDirection sweep, OutlinePointKind kind = OutlinePointKind.Normal, int joinIndex = -1, int boneIndex = -1)
            : base(center, kind, joinIndex, boneIndex)
        {
            ArcStart = arcStart;
            ArcEnd = arcEnd;
            Radius = radius;
            Sweep = sweep;
        }

        internal override void Emit(StreamGeometryContext ctx, bool isStartOfFigure = false)
        {
            ctx.LineTo(ArcStart, isStroked: true, isSmoothJoin: false);
            ctx.ArcTo(ArcEnd, new Size(Radius, Radius), rotationAngle: 0,
                    isLargeArc: false, sweepDirection: Sweep,
                    isStroked: true, isSmoothJoin: false);
        }

        /// <summary>Round 模式：1 个手柄在弧线中点（≈ 基类 Point 位置）。
        /// 不返回 ArcStart/ArcEnd（它们是弧线端点而非独立手柄）。</summary>
        internal override List<Point> GetCapturePoints()
            => new List<Point> { Point };
    }
}