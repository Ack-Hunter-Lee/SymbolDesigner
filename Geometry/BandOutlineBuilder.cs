using SymbolDesigner.Models;
using SymbolDesigner.ViewModels;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Point = System.Windows.Point;

namespace SymbolDesigner.Converters;

/// <summary>
/// BandPolyline 轮廓生成工具（纯静态方法，不实现 IValueConverter 接口）。
///
/// 直接由 PrimitiveItem.OutlineGeometry / XamlExporter 等业务代码调用，
/// 避免 IValueConverter 模式下每个 Binding 触发的重复 Convert 调试噪声。
///
/// API：
///   - BuildOutline(pathData, double bandWidth, JoinStyle)                         整体带宽
///   - BuildOutline(pathData, VertexBandWidth[] vertexBandWidths, JoinStyle)       每段独立带宽
///   - BuildOutline(pathData, VertexBandWidth[] vertexBandWidths, JoinStyle[])     每顶点独立接缝样式
///   - ParsePathData(pathData)                                                   解析 PathData 顶点序列
///
/// 算法：
///   1. 解析 PathData 顶点序列（"M x,y L x,y ..."）
///   2. 每段计算法线偏移（perp = dir 旋转 90° × half）
///   3. 段间连接：Miter / Bevel / Round 三种接缝模式
///   4. 拼接：左侧顺向 + 右侧逆向 + 闭合
/// </summary>
public static class BandOutlineBuilder
{
    // ===================== 算法入口 =====================

    /// <summary>整体带宽版本：所有段统一 bandWidth，用 miter join。
    /// 内部转成 VertexBandWidth[] 走新架构（避免两套 miter 实现并存）。</summary>
    public static Geometry BuildOutline(string pathData, double bandWidth)
    {
        var pts = ParsePathData(pathData);
        if (pts.Count < 2) return Geometry.Parse(pathData);
        if (bandWidth <= 0) return Geometry.Parse(pathData);

        int n = pts.Count;
        var vt = new VertexBandWidth[n];
        for (int i = 0; i < n; i++)
        {
            bool isStart = i == 0;
            bool isEnd = i == n - 1;
            vt[i] = new VertexBandWidth(i, bandWidth, bandWidth, isStart, isEnd);
        }

        return BuildOutline(pathData, vt, JoinStyle.Miter);
    }

    /// <summary>按顶点带宽版本：每顶点 Incoming（承上）+ Outgoing（启下）。
    /// 段 i 的首端宽度 = VertexBandWidths[i].OutgoingBandWidth
    /// 段 i 的末端宽度 = VertexBandWidths[i+1].IncomingBandWidth
    /// 段首末宽度不同 → 段为梯形（中间线性渐变）。
    /// 中间顶点衔接由 JoinStyle 控制：Miter（尖角）/ Bevel（斜切封口）/ Round（圆弧替代尖点）。</summary>
    public static Geometry BuildOutline(string pathData, VertexBandWidth[] vertexBandWidths)
        => BuildOutline(pathData, vertexBandWidths, JoinStyle.Miter);

    /// <summary>按 join 风格分发：miter / bevel / round 三种接缝可选。
    /// 全局 join 风格作用于所有内部顶点；如需逐顶点控制，用带 JoinStyle[] 的重载。</summary>
    public static Geometry BuildOutline(string pathData, VertexBandWidth[] vertexBandWidths, JoinStyle joinStyle)
        => BuildOutline(pathData, vertexBandWidths, ExpandUniform(joinStyle, vertexBandWidths));

    /// <summary>逐顶点 join 风格版本：每内部顶点（Index 1..N-2）从 perVertexStyles 取值，
    /// 首末顶点（0/N-1）的样式不参与接缝计算但保留数组下标对齐。
    /// perVertexStyles 长度必须 ≥ vertexBandWidths 长度，否则退回全局 Miter。</summary>
    public static Geometry BuildOutline(string pathData, VertexBandWidth[] vertexBandWidths, JoinStyle[] perVertexStyles)
        => EmitSteps(BuildOutlineSteps(pathData, vertexBandWidths, perVertexStyles));

    // ===== 新增：emit 步骤缓存版（外部可改步骤后 re-emit）=====

    /// <summary>整体带宽版本，输出 emit 步骤序列。</summary>
    internal static List<PointStep> BuildOutlineSteps(string pathData, double bandWidth, bool collinearSplitPairs = true)
    {
        var pts = ParsePathData(pathData);
        if (pts.Count < 2 || bandWidth <= 0) return new List<PointStep>();
        int n = pts.Count;
        var vt = new VertexBandWidth[n];
        for (int i = 0; i < n; i++)
        {
            bool isStart = i == 0;
            bool isEnd = i == n - 1;
            vt[i] = new VertexBandWidth(i, bandWidth, bandWidth, isStart, isEnd);
        }
        return BuildOutlineSteps(pathData, vt, ExpandUniform(JoinStyle.Miter, vt), collinearSplitPairs);
    }

    /// <summary>逐顶点 join 版本，输出 emit 步骤序列（合并 lefts+rights 的单条 emit 序列）。
    /// collinearSplitPairs：骨架两段共线同向时 true=产生两对轮廓点（默认，保留带宽台阶），false=合并为一对。
    /// 返回的 List<PointStep> 可直接传给 EmitSteps 拼成 Geometry；
    /// 外部（轮廓编辑手柄）可修改某项后重新 emit。</summary>
    internal static List<PointStep> BuildOutlineSteps(string pathData, VertexBandWidth[] vertexBandWidths, JoinStyle[] perVertexStyles, bool collinearSplitPairs = true)
    {
        var pts = ParsePathData(pathData);
        if (pts.Count < 2) return new List<PointStep>();

        int n = pts.Count;
        int segCount = n - 1;
        if (vertexBandWidths == null || vertexBandWidths.Length < n)
            return new List<PointStep>();  // 退化：顶点带宽数组不足，返回空步骤序列

        // perVertexStyles 长度校验：不足直接当 Miter 处理（不递归回退避免浪费）
        if (perVertexStyles == null || perVertexStyles.Length < n)
            perVertexStyles = ExpandUniform(JoinStyle.Miter, vertexBandWidths);

        // 存放每段的输出元数据
        var segs = new SegmentData[segCount];
        for (int i = 0; i < segCount; i++)
            segs[i] = ComputeSegment(pts, vertexBandWidths, i);

        // 两阶段：先算 Miter 几何（左右两条独立路径），再按 join 模式扰动接缝点。
        // 这样 ComputeMiterOutline 职责单一：不识别 join，只产出 Miter baseline。
        var (lefts, rights) = ComputeMiterOutline(segs, collinearSplitPairs);

        //Debug.WriteLine("lefts");
        //Debug.WriteLine("Rights");

        // ===== 按 join 模式扰动接缝点（直接写在主流程，不封装）=====
        // 旧版：按骨架接缝索引 k 遍历，miterIdx = 1 + k（已注释保留，逻辑可用）——
        //   if (segCount >= 2)
        //   {
        //       for (int k = 0; k < segCount - 1; k++)
        //       {
        //           int miterIdx = 1 + k;
        //           // 接缝位于 vertex[1+k]，从 perVertexStyles 取样式
        //           JoinStyle js = perVertexStyles[1 + k];
        //           if (js == JoinStyle.Miter) continue;  // miter 无需扰动
        //           var convex = DetectConvexSide(segs[k], segs[k + 1]);
        //           if (js == JoinStyle.Bevel)
        //           {
        //               var (newLefts, newRights) = AdjustForBevel(segs[k], segs[k + 1], lefts[miterIdx], rights[miterIdx], convex);
        //               lefts[miterIdx] = newLefts;
        //               rights[miterIdx] = newRights;
        //           }
        //           else if (js == JoinStyle.Round)
        //           {
        //               var (newLefts, newRights) = AdjustForRound(segs[k], segs[k + 1], lefts[miterIdx], rights[miterIdx], convex);
        //               lefts[miterIdx] = newLefts;
        //               rights[miterIdx] = newRights;
        //           }
        //       }
        //   }
        //
        // 新版：接缝扰动改为由"轮廓步骤是否为转折点（Kind == Corner）"驱动。
        // 遍历 lefts/rights 的 emit 步骤，只有骨线转折产生的 miter 顶点才按该接缝的 join 样式扰动；
        // 端点偏移点 / 段末端 / 共线直通点（Kind == Normal）天然跳过。
        // JoinIndex 定位到 segs[k]/segs[k+1] 上下文（旧版依赖 lefts[1+k] 的位置对应，共线分支会错位）。
        if (segCount >= 2)
        {
            for (int i = 0; i < lefts.Length; i++)
            {
                if (lefts[i].Kind != OutlinePointKind.Corner) continue;
                int k = lefts[i].JoinIndex;
                if (k < 0 || k >= segCount - 1) continue;
                JoinStyle js = perVertexStyles[1 + k];
                if (js == JoinStyle.Miter) continue;  // miter 无需扰动
                var convex = DetectConvexSide(segs[k], segs[k + 1]);
                if (js == JoinStyle.Bevel)
                {
                    var (newLefts, newRights) = AdjustForBevel(segs[k], segs[k + 1], lefts[i], rights[i], convex);
                    lefts[i] = newLefts;
                    rights[i] = newRights;
                }
                else if (js == JoinStyle.Round)
                {
                    var (newLefts, newRights) = AdjustForRound(segs[k], segs[k + 1], lefts[i], rights[i], convex);
                    lefts[i] = newLefts;
                    rights[i] = newRights;
                }
            }
        }

        //right的逆向排序
        rights = rights.Reverse().ToArray();

        // 合并 lefts + rights 为单条 emit 序列。
        // 封口由 EmitSteps 里 BeginFigure(first, isClosed: true) 的 WPF 自动闭合直线负责，
        // emit 序列本身不包含封口步骤——保持手柄索引与 emit 步骤一一对应（视觉第一个手柄 = StepIndex 0）。
        var steps = new List<PointStep>(lefts.Length + rights.Length);
        steps.AddRange(lefts);
        steps.AddRange(rights);

        // ===== 塌缩任意相邻重合轮廓点 =====
        // 某个骨点带宽归零（Incoming/Outgoing 同侧半带宽 → 0）时，承上/启下两侧 offset 都收敛到骨点，
        // 会在合并序列里留下两个（甚至更多）坐标相同的相邻槽位。这些"重合双点"不是真正的单一顶点：
        //   - 零长度边使 MakeNormalArcStep / AdjustForRound 的切线/半径退化 → 无法圆角；
        //   - 编辑手柄会叠加显示成"视觉两个点"。
        // 统一在此把相邻且坐标重合的普通 PointStep 合并为一个（保留先出现的），
        // 之后再走圆角逻辑，该塌缩点就能作为"真正单点"被 per-vertex 圆角处理。
        // 注意：仅合并普通 PointStep；ArcStep/BevelStep 自带独立 emit 几何，不参与强并。
        steps = MergeCoincidentSteps(steps);

        // ===== 应用 per-vertex 普通轮廓点圆角（Normal 点：端点偏移 / 段末端 / 共线直通点的 L/R 侧）=====
        // 映射（合并后真实 emit 顺序，与 RefreshOutlineCornerHandles 一致）：
        //   左半 steps[i]（i ∈ [0, leftsLen)）↔ 骨架顶点 i，取 LeftCornerRadius
        //   右半 steps[leftsLen + j]（j ∈ [0, rightLen)）↔ 骨架顶点 n-1-j，取 RightCornerRadius
        // 仅处理 Kind==Normal 的步骤：Corner 转角点（含被 join 扰动替换成的 ArcStep/BevelStep）天然跳过。
        // 长度防线：lefts/rights 只在无共线拆点时等于 n，超出索引（i>=n）无 per-vertex 对应，跳过。
        // 持久化链路：VertexBandWidth.Left/RightCornerRadius → DTO → .sym.json；加载后由这里重建 ArcStep。
        ApplyPerVertexCornerRadius(steps, vertexBandWidths, n);
        return steps;
    }

    /// <summary>闭合多边形（Polyline 闭合形态）轮廓点生成：骨架点直连单环，无带宽偏移。
    /// 每个骨架点 → 1 个 PointStep（Kind=Normal，BoneIndex=i）；
    /// 首尾闭合由 EmitSteps 的 BeginFigure(isClosed:true) 自动画线完成（路径最后点 → 起点）。
    /// 与 BandPolyline 的区别只在"生成轮廓点的逻辑"：BandPolyline 按带宽向两侧偏移出左右双环，
    /// 闭合多边形直接把骨架点序列当轮廓点；之后的合并重合点 / per-vertex 圆角 / emit 全部复用。
    /// 骨架点圆角半径存 vertexBandWidths[vi].LeftCornerRadius（单环无左右侧，统一用 Left）。</summary>
    internal static List<PointStep> BuildClosedShapeSteps(string pathData, VertexBandWidth[]? vertexBandWidths = null)
    {
        var pts = ParsePathData(pathData);
        if (pts.Count < 3) return new List<PointStep>();  // 至少 3 点才能围成闭合面

        var steps = new List<PointStep>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
            steps.Add(new PointStep(pts[i], OutlinePointKind.Normal, -1, i));

        steps = MergeCoincidentSteps(steps);

        // 单环 per-vertex 圆角：半径统一读 LeftCornerRadius（无左右侧）
        if (vertexBandWidths != null && vertexBandWidths.Length >= pts.Count)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                if (s.Kind != OutlinePointKind.Normal) continue;
                int vi = s.BoneIndex;
                if (vi < 0 || vi >= vertexBandWidths.Length) continue;
                double r = vertexBandWidths[vi].LeftCornerRadius;
                if (r <= 0) continue;
                steps[i] = MakeNormalArcStep(steps, i, r);
            }
        }
        return steps;
    }

    /// <summary>布尔结果（段结构快照）的骨架点步骤序列——仅用于编辑手柄/轮廓手柄，不用于渲染。
    /// 每环 = 段链"起点 + 各段终点"（弧段端点 = 段端点，不摊平）；逐环去重重合点；
    /// 步骤带 FigureIndex（环归属）与 BoneIndex（跨环平铺骨架索引，对齐 VertexBandWidths）。
    /// 渲染不走此方法：布尔结果渲染 = BooleanOps.BakeCornerRadii → Geometry.Parse 原生画弧。</summary>
    internal static List<PointStep> BuildClosedShapeSegmentsSteps(string pathData, VertexBandWidth[]? vertexBandWidths = null)
    {
        var allSteps = new List<PointStep>();
        int boneOffset = 0;
        int r = 0;
        foreach (var ring in BooleanOps.GetSkeletonRings(pathData))
        {
            var ringSteps = new List<PointStep>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
                ringSteps.Add(new PointStep(ring[i], OutlinePointKind.Normal, -1, boneOffset + i, r));
            // 每环独立合并（环内相邻/首尾重合点塌缩，BoneIndex 保留跨环平铺索引）
            ringSteps = MergeCoincidentSteps(ringSteps);
            allSteps.AddRange(ringSteps);
            boneOffset += ring.Count;
            r++;
        }
        return allSteps;
    }

    /// <summary>合并所有坐标重合的相邻普通 PointStep，塌缩带宽归零接缝产生的"重叠双点"为单一顶点。
    /// 仅处理普通 PointStep（前后两步都是 PointStep 且距离 &lt; eps 时，跳过后出现的那个，保留先出现的）；首尾闭环重合对同样合并（保留首点，删末点，闭合线由 EmitSteps 自动绘制）；
    /// ArcStep/BevelStep 自带独立 emit 几何，不参与强并。
    /// 纯函数：返回新列表，不修改入参。</summary>
    private static List<PointStep> MergeCoincidentSteps(List<PointStep> steps)
    {
        const double eps = 1e-9;
        if (steps.Count < 2) return steps;
        var merged = new List<PointStep>(steps.Count);
        foreach (var s in steps)
        {
            bool coincide = s is PointStep
                            && merged.Count > 0
                            && merged[^1] is PointStep last
                            && (s.Point - last.Point).Length < eps;
            if (coincide) continue;  // 与上一个坐标重合 → 视为同一点，合并
            merged.Add(s);
        }

        // 首尾闭环重合也合并：带宽塌缩到起点骨点时 leftStart/rightStart 都收敛到同一点，
        // 形成 figure 起终点重合的"闭合对"。保留首点（BeginFigure 起点），删除重合的末点，
        // 闭合线由 EmitSteps 的 BeginFigure(isClosed:true) 自动绘制。
        if (merged.Count > 1
            && merged[0] is PointStep firstStep
            && merged[^1] is PointStep lastStep
            && (firstStep.Point - lastStep.Point).Length < eps)
        {
            merged.RemoveAt(merged.Count - 1);
        }
        return merged;
    }

    /// <summary>按 per-vertex 普通轮廓点圆角半径重建步骤（在合并后的完整 emit 序列上，邻接方向跨首尾闭环真实）。
    /// 用步骤自带的 BoneIndex（ComputeMiterOutline 显式记录）定位骨架顶点，替代旧的 vi=i 索引公式——
    /// 拆双对模式下 lefts 长度 = 2n-2 > n，索引公式越界/错位会把部分点漏掉（如 3 段共线 12 点中 L/R 各后 2 个）。</summary>
    private static void ApplyPerVertexCornerRadius(List<PointStep> steps, VertexBandWidth[] vertexBandWidths, int n)
    {
        int leftLen = steps.Count / 2;  // lefts 长度（合并后恰为半）
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s.Kind != OutlinePointKind.Normal) continue;  // 转角点（含替换后的 ArcStep/BevelStep）不在此处理
            int vi = s.BoneIndex;
            if (vi < 0 || vi >= n) continue;
            bool left = i < leftLen;  // 合并后前半是 lefts（顺向），后半是 rights（逆向）
            double r = left ? vertexBandWidths[vi].LeftCornerRadius
                            : vertexBandWidths[vi].RightCornerRadius;
            if (r <= 0) continue;
            steps[i] = MakeNormalArcStep(steps, i, r);
        }
    }

    /// <summary>普通轮廓点圆角：构造以该点为圆心的 ArcStep（与 ViewModel.TryReplaceWithArc 同一几何换算）。
    /// 邻边方向从合并后步骤序列的前后步骤取（含首尾闭环），返回原步骤若共线退化。</summary>
    private static PointStep MakeNormalArcStep(List<PointStep> steps, int i, double radius)
    {
        var pt = steps[i].Point;
        var prev = i > 0 ? steps[i - 1].Point : steps[steps.Count - 1].Point;  // 闭环前一点
        var next = i < steps.Count - 1 ? steps[i + 1].Point : steps[0].Point;  // 闭环后一点
        var inDir = pt - prev;
        var outDir = next - pt;
        inDir.Normalize();
        outDir.Normalize();
        if (inDir.Length < 1e-9 || outDir.Length < 1e-9) return steps[i];
        double det = Vector.Determinant(inDir, outDir);
        if (Math.Abs(det) < 1e-9) return steps[i];  // 共线（0°/180°）退化为直线，不圆角
        // 切点偏移 = 半径 × cot(半跨角)，与 AdjustForRound / ViewModel 同一换算
        double len = GetArcTangentOffset(inDir, outDir, radius);
        var arcStart = pt - inDir * len;
        var arcEnd = pt + outDir * len;
        var sweep = det < 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
        return new ArcStep(pt, arcStart, arcEnd, radius, sweep, steps[i].Kind, steps[i].JoinIndex, steps[i].BoneIndex);
    }

    /// <summary>把 emit 步骤序列拼成 StreamGeometry。
    /// 按 FigureIndex 分组开 figure：无标记（-1，BandPolyline/单环）整条序列一个闭合 figure；
    /// 多环布尔结果每环一个 figure（环内各自闭合），环间边界用 BeginFigure 断开，不串环连线。
    /// Each ring's first step：ArcStep → 以 ArcStart 起手直接画弧，否则以 Point 为起点。</summary>
    internal static Geometry EmitSteps(List<PointStep> steps)
    {
        var stream = new StreamGeometry();
        using (var ctx = stream.Open())
        {
            int i = 0;
            while (i < steps.Count)
            {
                BeginFigure(ctx, steps[i]);
                i++;
                while (i < steps.Count && steps[i].FigureIndex == steps[i - 1].FigureIndex)
                {
                    steps[i].Emit(ctx);
                    i++;
                }
            }
        }
        // EvenOdd：射线穿过奇数次才填。即使 emit 序列方向混叠（凹折线左/右轮廓自相交），
        // 内外环也能正确按"奇偶"区分填充，避免 Nonzero 下被填充成实心方形。
        stream.FillRule = FillRule.EvenOdd;
        return stream;
    }

    /// <summary>以步骤为 figure 起点开图（步骤 0 是 ArcStep 时以 ArcStart 起手直接画弧）。</summary>
    private static void BeginFigure(StreamGeometryContext ctx, PointStep first)
    {
        if (first is ArcStep arcStep)
        {
            ctx.BeginFigure(arcStep.ArcStart, isFilled: true, isClosed: true);
            ctx.ArcTo(arcStep.ArcEnd, new Size(arcStep.Radius, arcStep.Radius), rotationAngle: 0,
                isLargeArc: false, arcStep.Sweep, isStroked: true, isSmoothJoin: false);
        }
        else
        {
            ctx.BeginFigure(first.Point, isFilled: true, isClosed: true);
        }
    }

    /// <summary>计算 Miter 几何基底（输出左右两条路径）。
    /// 顺序（左 / 右对称）：
    ///   [0]: 起点
    ///   [1]: miter 顶点 0（若有）+ 段 1 末端（交错）
    ///   ...
    ///   末: 段 N-1 末端
    /// 长度：
    ///   N=1（单段）：2 个（起点 + 末端）
    ///   N≥2：2N-1 个（起点 + (N-1) 接缝 + (N-1) 段末端，交错摆放）
    ///                L 形 N=2: 3 个（每条）✓ 总 6
    /// 职责单一：只算 Miter 几何，不接受 join 参数（join 由 ApplyJoinAdjustment 处理）。
    /// 纯函数：返回新数组，不修改入参。</summary>
    private static (PointStep[] lefts, PointStep[] rights) ComputeMiterOutline(SegmentData[] segs, bool collinearSplitPairs = true)
    {
        int segCount = segs.Length;
        // 数组长度：单段 2 个（起点 + 末端）；多段 segCount+1 个
        int outlineLen = Math.Max(2, segCount + 1);

        var lefts = new List<PointStep>(outlineLen);
        var rights = new List<PointStep>(outlineLen);

        // 起点
        lefts.Add(new PointStep(segs[0].LeftStart, OutlinePointKind.Normal, -1, 0));
        rights.Add(new PointStep(segs[0].RightStart, OutlinePointKind.Normal, -1, 0));

        if (segCount == 1)
        {
            // 单段：直接末点
            lefts.Add(new PointStep(segs[0].LeftEnd, OutlinePointKind.Normal, -1, 1));
            rights.Add(new PointStep(segs[0].RightEnd, OutlinePointKind.Normal, -1, 1));
        }
        else
        {
            // 多段：交错 miter + 段末端
            bool verse = false;
            for (int k = 0; k < segCount - 1; k++)
            {
                // miter 顶点（接缝）
                int miterIdx = 1 + k;
                int bIdx = k + 1;  // 接缝 k 两侧的轮廓步骤都归属于骨架顶点 k+1

                var fromVec = segs[k].VertexEnd - segs[k].VertexStart;
                var toVec = segs[k + 1].VertexEnd - segs[k + 1].VertexStart;
                if (Vector.Determinant(fromVec, toVec) == 0 && fromVec*toVec> 0)
                {
                    if (collinearSplitPairs)
                    {
                        // 两对：保留带宽突变台阶（LeftEnd 与 LeftStart 可独立编辑/圆角）
                        lefts.Add(new PointStep(segs[k].LeftEnd, OutlinePointKind.Normal, -1, bIdx));
                        rights.Add(new PointStep(segs[k].RightEnd, OutlinePointKind.Normal, -1, bIdx));
                    }
                    else
                    {
                        // 一对：合并为一个直通点（取后段首端 = 骨点"出端"带宽，忽略台阶），两侧各只加一个步骤
                        // 与 UI 语义一致：合并后中间点只有出端（属性面板/画布只显示/编辑 OutgoingBandWidth）
                        lefts.Add(new PointStep(segs[k + 1].LeftStart, OutlinePointKind.Normal, -1, bIdx));
                        rights.Add(new PointStep(segs[k + 1].RightStart, OutlinePointKind.Normal, -1, bIdx));
                        continue;
                    }
                    lefts.Add(new PointStep(segs[k + 1].LeftStart, OutlinePointKind.Normal, -1, bIdx));
                    rights.Add(new PointStep(segs[k + 1].RightStart, OutlinePointKind.Normal, -1, bIdx));
                    continue;
                }
                else if (Vector.Determinant(fromVec, toVec) == 0 && fromVec * toVec < 0)
                {
                    lefts.Add(new PointStep(segs[k].LeftEnd, OutlinePointKind.Normal, -1, bIdx));
                    rights.Add(new PointStep(segs[k].RightEnd, OutlinePointKind.Normal, -1, bIdx));
                    lefts.Add(new PointStep(segs[k + 1].RightStart, OutlinePointKind.Normal, -1, bIdx));
                    rights.Add(new PointStep(segs[k + 1].LeftStart, OutlinePointKind.Normal, -1, bIdx));
                    verse = !verse;
                    continue;
                }
                var (left, right) = MiterPoint(segs[k], segs[k + 1]);
                if (verse)
                {
                    var temp = left;
                    left = right;
                    right= temp;
                }
                // 只有这里（MiterPoint 产出）的 left/right 是骨线转折产生的转角点，标记 Corner + 记录接缝索引 k + 骨点索引
                lefts.Add(new PointStep(left, OutlinePointKind.Corner, k, bIdx)) ;
                rights.Add(new PointStep(right, OutlinePointKind.Corner, k, bIdx));
            }
            // 段 k+1 末端
            var (leftEnd, rightEnd) = (segs[^1].LeftEnd, segs[^1].RightEnd);
            if (verse)
            {
                var temp = leftEnd;
                leftEnd = rightEnd;
                rightEnd = temp;
            }
            lefts.Add(new PointStep(leftEnd, OutlinePointKind.Normal, -1, segCount));
            rights.Add(new PointStep(rightEnd, OutlinePointKind.Normal, -1, segCount));
            //lefts.Add(new PointStep(segs[^1].LeftEnd)) ;
            //rights.Add(new PointStep(segs[^1].RightEnd));
        }

        return (lefts.ToArray(), rights.ToArray());
    }

    /// <summary>
    /// 单段几何元数据：轨迹线段 i（pts[i] → pts[i+1]）的偏移端点 + 骨架顶点。
    /// LeftHalf / RightHalf：用于 Round join 计算圆弧半径（=两端半带宽均值）。
    /// VertexStart / VertexEnd：骨架顶点，用于主流程算接缝 dIn/dOut → 内外侧判定。
    /// </summary>
    private struct SegmentData
    {
        public Point LeftStart;
        public Point LeftEnd;
        public Point RightStart;
        public Point RightEnd;
        /// <summary>骨架段起点（= pts[i]），用于接缝 dIn 方向。</summary>
        public Point VertexStart;
        /// <summary>骨架段末端顶点（= pts[i+1]），接缝 vertex。</summary>
        public Point VertexEnd;
        /// <summary>Round join 内径半径（顶点 i+1 的 JoinInnerRadius），0 = 经典无内径弧。</summary>
        public double JoinInnerRadius;
        public double LeftHalfStart;
        public double LeftHalfEnd;
        public double RightHalfStart;
        public double RightHalfEnd;
    }

    private static SegmentData ComputeSegment(List<Point> pts, VertexBandWidth[] vt, int i)
    {
        var sd = new SegmentData();
        var pStart = pts[i];
        var pEnd = pts[i + 1];
        int segCount = pts.Count - 1;
        double halfStart = vt[i].OutgoingBandWidth * 0.5;
        double halfEnd = vt[i + 1].IncomingBandWidth * 0.5;

        // 段 i 偏移端点计算：Left/Right 两侧，半带宽由承上/启下给出
        var segStart = pStart;
        var segEnd = pEnd;
        var dir = Normalize(segEnd - segStart);
        var perpL = new Vector(-dir.Y, dir.X);
        var perpR = -perpL;
        sd.LeftStart = segStart + perpL * halfStart;
        sd.LeftEnd = segEnd + perpL * halfEnd;
        sd.RightStart = segStart + perpR * halfStart;
        sd.RightEnd = segEnd + perpR * halfEnd;
        sd.VertexStart = pStart;
        sd.VertexEnd = pEnd;
        sd.JoinInnerRadius = vt[i + 1].JoinInnerRadius;
        sd.LeftHalfStart = halfStart;
        sd.LeftHalfEnd = halfEnd;
        sd.RightHalfStart = halfStart;
        sd.RightHalfEnd = halfEnd;
        return sd;
    }

    /// <summary>检测接缝处外凸侧分布。
    /// 算法：入射方向 dIn × 出射方向 dOut（叉积）= Vector.Determinant(dIn, dOut)
    /// WPF 屏幕坐标系下（Y 朝下）：
    ///   cross &lt; 0 → 逆时针拐弯 → 外凸在左侧
    ///   cross > 0 → 顺时针拐弯 → 外凸在右侧
    /// L形验证：dIn=(1,0), dOut=(0,1), cross=1 → 外凸在屏幕右上 = 右侧</summary>
    private static (bool leftIsOuter, bool rightIsOuter) DetectConvexSide(
        SegmentData from, SegmentData to)
    {
        var dIn = from.VertexEnd - from.VertexStart;
        var dOut = to.VertexEnd - to.VertexStart;
        return Vector.Determinant(dIn, dOut) < 0
            ? (leftIsOuter: true, rightIsOuter: false)
            : (leftIsOuter: false, rightIsOuter: true);
    }

    /// <summary>替换接缝位置的 PointStep 为 BevelStep（仅外侧，内侧保持原样）。
    /// bevel 几何：ctx 当前点 → 台阶点 → miter 顶点（沿 dIn/dOut 方向偏移）。
    /// 纯函数：返回新 PointStep 对（不修改入参）。</summary>
    private static (PointStep left, PointStep right) AdjustForBevel(
        SegmentData from, SegmentData to,
        PointStep leftMiter, PointStep rightMiter,
        (bool leftIsOuter, bool rightIsOuter) convex)
    {
        var dIn = Normalize(from.VertexEnd - from.VertexStart);
        var dOut = Normalize(to.VertexEnd - to.VertexStart);

        // 外凸侧走 BevelStep 封口斜面；内凸侧保持原 miter 顶点不变
        // BevelStep 由转角点替换而来，保留 Corner 标记与接缝索引（骨点索引用 bIdx = JoinIndex+1）
        var leftBevel = convex.leftIsOuter
            ? new BevelStep(leftMiter.Point, leftMiter.Point - dIn, leftMiter.Point + dOut, OutlinePointKind.Corner, leftMiter.JoinIndex, leftMiter.JoinIndex + 1)
            : leftMiter;
        var rightBevel = convex.rightIsOuter
            ? new BevelStep(rightMiter.Point, rightMiter.Point + dOut, rightMiter.Point - dIn, OutlinePointKind.Corner, rightMiter.JoinIndex, rightMiter.JoinIndex + 1)
            : rightMiter;
        return (leftBevel, rightBevel);
    }

    /// <summary>求圆角/接缝圆弧的切点沿入/出线方向的偏移距离 len（共享换算）。
    /// 几何：入线 inDir 与出线 outDir 的夹角为 θ，圆弧跨角 α = 180° − θ（恒为劣弧），
    ///   sinHalf = sin(α/2) = sin((180°−θ)/2) = cos(θ/2) = sqrt((1 + cosθ)/2)
    ///   cosHalf = cos(θ/2 之补) 对应 cos((180°−θ)/2) = sin(θ/2)
    ///   len = r·cosHalf/sinHalf = r·cot(α/2)   （切点偏移 = r·cot(半跨角)）
    /// 注意：θ>90° 时 cosθ 变负，sinHalf/cosHalf 数值含义随之变化，不能直接沿方向乘 r。</summary>
    public static double GetArcTangentOffset(Vector inDir, Vector outDir, double radius)
    {
        // 入线反向 (-inDir) 与出线方向的夹角 = 转向角 θ
        double sinHalf = Math.Sqrt((1.0 - Vector.Multiply(-inDir, outDir)) / 2.0);
        if (sinHalf < 1e-9)
            return radius; // 完全共线（0°/180°）退化，取半径兜底，避免除零
        double cosHalf = Math.Sqrt(1.0 - sinHalf * sinHalf);
        return radius * cosHalf / sinHalf;
    }

    /// <summary>替换接缝位置的 PointStep 为 ArcStep（外侧画外圆弧 + 内侧画内圆弧）。
    /// 几何：圆心 = miter 顶点（外凸交点），半径由内径 + 带宽决定。
    /// 纯函数：返回新 PointStep 对（不修改入参）。</summary>
    private static (PointStep left, PointStep right) AdjustForRound(
        SegmentData from, SegmentData to,
        PointStep leftMiter, PointStep rightMiter,
        (bool leftIsOuter, bool rightIsOuter) convex)
    {
        var dIn = Normalize(from.VertexEnd - from.VertexStart);
        var dOut = Normalize(to.VertexEnd - to.VertexStart);

        // 接缝处带宽 = 段 k 末端 + 段 k+1 起点半带宽之和
        double bandWidth = from.LeftHalfEnd + to.LeftHalfStart;
        // 内径：默认 = 半带宽（用户没设的话给个合理的默认值），用户可显式设 JoinInnerRadius 覆盖
        double innerRadius = from.JoinInnerRadius > 0 ? from.JoinInnerRadius : bandWidth * 0.5;
        double outerRadius = innerRadius + bandWidth;

        // 沿 dIn / dOut 方向的两个偏移向量（切点偏移 = 半径 × cot(半跨角)，与 ViewModel 圆角化同一换算）
        double innerPathDist = GetArcTangentOffset(dIn, dOut, innerRadius);
        double outerPathDist = GetArcTangentOffset(dIn, dOut, outerRadius);
        var innerFromDelta = dIn * innerPathDist;
        var innerToDelta = dOut * innerPathDist;
        var outerFromDelta = dIn * outerPathDist;
        var outerToDelta = dOut * outerPathDist;

        var miterL = leftMiter.Point;
        var miterR = rightMiter.Point;

        // 外凸侧画外圆弧（逆时针 Sweep），内凸侧画内圆弧（顺时针 Sweep）
        // ArcStep 由转角点替换而来，保留 Corner 标记与接缝索引（骨点索引用 bIdx = JoinIndex+1）
        var leftArc = convex.leftIsOuter
            ? new ArcStep(miterL, miterL - outerFromDelta, miterL + outerToDelta,
                          outerRadius, SweepDirection.Counterclockwise, OutlinePointKind.Corner, leftMiter.JoinIndex, leftMiter.JoinIndex + 1)
            : new ArcStep(miterL, miterL - innerFromDelta, miterL + innerToDelta,
                          innerRadius, SweepDirection.Clockwise, OutlinePointKind.Corner, leftMiter.JoinIndex, leftMiter.JoinIndex + 1);
        var rightArc = convex.rightIsOuter
            ? new ArcStep(miterR, miterR + outerToDelta, miterR - outerFromDelta,
                          outerRadius, SweepDirection.Counterclockwise, OutlinePointKind.Corner, rightMiter.JoinIndex, rightMiter.JoinIndex + 1)
            : new ArcStep(miterR, miterR + innerToDelta, miterR - innerFromDelta,
                          innerRadius, SweepDirection.Clockwise, OutlinePointKind.Corner, rightMiter.JoinIndex, rightMiter.JoinIndex + 1);
        return (leftArc, rightArc);
    }

    /// <summary>计算接缝 vertex 处的 miter 顶点对（左 / 右）。
    /// 算法：
    ///   1. 归一化 fromVec / toVec（消除段长影响）
    ///   2. bisector 方向 = fromVec - toVec（外凸方向）
    ///   3. sin(θ/2) = |fromVec - toVec| / 2（半角公式）
    ///   4. miter 距离 = halfWidth / sin(θ/2)
    ///   5. miter 顶点 = vertex ± bisector * miter 距离
    /// 根据 DetectConvexSide 决定 left / right 哪个对应外凸侧。</summary>
    private static (Point left, Point right) MiterPoint(SegmentData from, SegmentData to)
    {
        // 方向
        var fromVec = from.VertexEnd - from.VertexStart;
        var toVec = to.VertexEnd - to.VertexStart;
        fromVec.Normalize();
        toVec.Normalize();

        // 外凸方向（凸点方向）
        var sumVec = fromVec - toVec;
        sumVec.Normalize();

        // sin(θ/2) = √((1 - cos θ) / 2)
        //   cos θ = (-fromVec) · toVec（两个方向反向了再点乘，绕开正负号干扰）
        double dot = (-fromVec.X) * toVec.X + (-fromVec.Y) * toVec.Y;
        double sinHalf = Math.Sqrt((1 - dot) / 2);

        // 半带宽（接缝处左右均值）
        double halfWidth = 0.5 * (from.LeftHalfEnd + to.LeftHalfStart);

        // 共线 / 反向平行退化
        if (sinHalf < 1e-6) return (from.VertexEnd, from.VertexEnd);

        // miter 距离 = halfWidth / sin(θ/2)
        double miterDist = halfWidth / sinHalf;

        // 凸点（远离 vertex）= vertex + bisector * miterDist
        var vertex = from.VertexEnd;
        var outerPoint = new Point(vertex.X + sumVec.X * miterDist, vertex.Y + sumVec.Y * miterDist);
        // 凹点（靠近 vertex）= vertex - bisector * miterDist
        var innerPoint = new Point(vertex.X - sumVec.X * miterDist, vertex.Y - sumVec.Y * miterDist);

        // 根据 DetectConvexSide 决定 left / right 哪个对应 outer / inner
        var convex = DetectConvexSide(from, to);
        return convex.leftIsOuter
            ? (left: outerPoint, right: innerPoint)
            : (left: innerPoint, right: outerPoint);
    }

    /// <summary>把单个 JoinStyle 扩展成与 vt 等长的数组（每个顶点同一值）。
    /// 全局旧版 API 内部转 per-vertex 实现时复用，避免逻辑分叉。</summary>
    private static JoinStyle[] ExpandUniform(JoinStyle js, VertexBandWidth[] vt)
    {
        int n = vt?.Length ?? 0;
        var arr = new JoinStyle[n];
        for (int i = 0; i < n; i++) arr[i] = js;
        return arr;
    }

    /// <summary>返回轮廓线上所有转折点的详细信息。
    /// 每个内部接缝（vertex[1..N-2]）返回两个轮廓角点：
    ///   - Convex 侧：miter 顶点（外凸角，落在轮廓线上）
    ///   - Concave 侧：凹口面中点（两个偏移端点的中点，落在轮廓线上）
    /// Round 样式只返回弧线中点（1个手柄，可拖动控制半径）。
    /// N&lt;3（无内部接缝）或 bandWidth&lt;=0 返回空列表。
    /// 返回坐标为 PathData 局部坐标系，UI 端需按 item.Width/Height 缩放到画布坐标。</summary>
    public static List<(Point Position, int VertexIndex, string Side)> BuildOutlineDetail(
        string pathData, double bandWidth, JoinStyle[]? jointStyles = null)
    {
        var result = new List<(Point, int, string)>();
        var pts = ParsePathData(pathData);
        int n = pts.Count;
        if (n < 3 || bandWidth <= 0) return result;

        // 把整体带宽转成 VertexBandWidth[]（统一走 MiterPoint + DetectConvexSide 路径）
        var vt = new VertexBandWidth[n];
        for (int i = 0; i < n; i++)
        {
            bool isStart = i == 0;
            bool isEnd = i == n - 1;
            vt[i] = new VertexBandWidth(i, bandWidth, bandWidth, isStart, isEnd);
        }

        int segCount = n - 1;
        var segs = new SegmentData[segCount];
        for (int i = 0; i < segCount; i++)
            segs[i] = ComputeSegment(pts, vt, i);

        // 确保 jointStyles 长度匹配（不匹配或缺省时全用 Miter）
        if (jointStyles is null || jointStyles.Length != n)
        {
            jointStyles = new JoinStyle[n];
            for (int i = 0; i < n; i++) jointStyles[i] = JoinStyle.Miter;
        }

        // 内部接缝 k = 0..segCount-2 → vertex index = 1+k
        for (int k = 0; k < segCount - 1; k++)
        {
            int vi = 1 + k; // 骨架顶点 index
            var from = segs[k];
            var to = segs[k + 1];
            var js = jointStyles[vi];
            var convex = DetectConvexSide(from, to);
            var (leftMiter, rightMiter) = MiterPoint(from, to);

            if (js == JoinStyle.Round)
            {
                // Round：1个手柄，在弧线中点。弧线中点 ≈ convex 侧 miter 点位置
                // 用 outer miter 点作为 Round 手柄位置（弧线中点更靠近 miter 点）
                var outer = convex.leftIsOuter ? leftMiter : rightMiter;
                result.Add((outer, vi, "Round"));
            }
            else
            {
                // Miter / Bevel：2个手柄
                // Convex 侧：miter 顶点（落在轮廓线上）
                var convexPt = convex.leftIsOuter ? leftMiter : rightMiter;
                result.Add((convexPt, vi, convex.leftIsOuter ? "L" : "R"));

                // Concave 侧：凹口面中点（两个偏移端点的中点，落在轮廓线上）
                // 左凹口：from.LeftEnd 与 to.LeftStart 的中点
                // 右凹口：from.RightEnd 与 to.RightStart 的中点
                var concaveNotchMid = convex.leftIsOuter
                    ? new Point((from.RightEnd.X + to.RightStart.X) * 0.5,
                                (from.RightEnd.Y + to.RightStart.Y) * 0.5)
                    : new Point((from.LeftEnd.X + to.LeftStart.X) * 0.5,
                                (from.LeftEnd.Y + to.LeftStart.Y) * 0.5);
                string concaveSide = convex.leftIsOuter ? "R" : "L";
                result.Add((concaveNotchMid, vi, concaveSide));
            }
        }
        return result;
    }

    /// <summary>返回轮廓线上所有连接点（顶点）坐标，按轮廓多边形顺序排列，并标注每个点所属的骨架顶点序号。
    /// 复用 BuildOutline 的完整算法（SegmentData → MiterPoint → 扰动），仅最后一步改为收集顶点坐标而非 emit StreamGeometry。
    /// 返回：(Point 轮廓顶点坐标, int 骨架顶点序号 0..n-1)。
    /// 对于 Round 接缝，只返回弧线中点（1 个点），不返回弧线端点。
    /// 带宽 ≥ 0 允许零带宽退化。</summary>
    public static List<(Point Point, int VertexIndex)> BuildOutlineVertices(
        string pathData, VertexBandWidth[] vertexBandWidths, JoinStyle[] perVertexStyles)
    {
        var pts = ParsePathData(pathData);
        int n = pts.Count;
        int segCount = n - 1;
        if (segCount < 1) return new List<(Point, int)>();

        // 带宽校验：不足则退化线段
        if (vertexBandWidths == null || vertexBandWidths.Length < n)
            return new List<(Point, int)>();

        // perVertexStyles 长度校验
        if (perVertexStyles == null || perVertexStyles.Length < n)
            perVertexStyles = ExpandUniform(JoinStyle.Miter, vertexBandWidths);

        // 计算段数据（复用 ComputeSegment）
        var segs = new SegmentData[segCount];
        for (int i = 0; i < segCount; i++)
            segs[i] = ComputeSegment(pts, vertexBandWidths, i);

        // 计算 Miter 轮廓（复用 ComputeMiterOutline）
        var (lefts, rights) = ComputeMiterOutline(segs);

        // 按 join 模式扰动接缝点
        if (segCount >= 2)
        {
            for (int k = 0; k < segCount - 1; k++)
            {
                int miterIdx = 1 + k;
                JoinStyle js = perVertexStyles[1 + k];
                if (js == JoinStyle.Miter) continue;
                var convex = DetectConvexSide(segs[k], segs[k + 1]);
                if (js == JoinStyle.Bevel)
                {
                    var (newLefts, newRights) = AdjustForBevel(segs[k], segs[k + 1], lefts[miterIdx], rights[miterIdx], convex);
                    lefts[miterIdx] = newLefts;
                    rights[miterIdx] = newRights;
                }
                else if (js == JoinStyle.Round)
                {
                    var (newLefts, newRights) = AdjustForRound(segs[k], segs[k + 1], lefts[miterIdx], rights[miterIdx], convex);
                    lefts[miterIdx] = newLefts;
                    rights[miterIdx] = newRights;
                }
            }
        }

        // 收集顶点（与 BuildOutline 中 StreamGeometryContext 的 emit 顺序一致）
        var result = new List<(Point, int)>();
        // 起点：左侧起点 → 骨架顶点 0
        result.Add((lefts[0].Point, 0));
        // 左侧顺向 emit 的顶点：lefts[i] 对应骨架顶点 i
        for (int i = 1; i < lefts.Length; i++)
            foreach (var pt in lefts[i].GetCapturePoints())
                result.Add((pt, i));
        // 右侧逆向 emit 的顶点：rights 反转后，index i 对应骨架顶点 n-1-i
        rights = rights.Reverse().ToArray();
        for (int i = 0; i < rights.Length; i++)
            foreach (var pt in rights[i].GetCapturePoints())
                result.Add((pt, n - 1 - i));
        return result;
    }

    /// <summary>解析 "M x,y L x,y ..." 字符串为顶点列表。</summary>
    public static List<Point> ParsePathData(string s)
    {
        var pts = new List<Point>();
        var tokens = s.Split(new[] { ' ', ',', 'M', 'L' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
             && double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                pts.Add(new Point(x, y));
        }
        return pts;
    }

    private static Vector Normalize(Vector v)
    {
        var len = v.Length;
        return len > 1e-6 ? v / len : new Vector(1, 0);
    }
}