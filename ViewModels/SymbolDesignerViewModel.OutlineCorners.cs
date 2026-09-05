using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>轮廓接缝的样式与圆角化：单点圆角 / 批量圆角 / 清除覆盖 / 恢复。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>当前选中的接缝手柄（null = 未选；非 null = 显示该接缝的属性面板）。
    /// 点击 OutlineCornerHandle → 设为对应 handle；点击空白 / 切换 PrimitiveItem → 清空。</summary>
    [ObservableProperty] private OutlineCornerHandle? _selectedOutlineCorner;

    /// <summary>是否有选中接缝（供 XAML 属性面板显示/隐藏）。</summary>
    [ObservableProperty] private bool _hasSelectedOutlineCorner;

    /// <summary>圆角半径（px，画布逻辑坐标）。属性面板 Slider 调整，圆角化时使用。</summary>
    [ObservableProperty] private double _cornerArcRadius = 2.0;

    /// <summary>非转角点样式（轮廓属性面板"应用到全部接缝"使用）：Round / Miter。
    /// - Round：对所有非骨线转折的轮廓点应用 CornerArcRadius 圆角
    /// - Miter：清除所有非骨线转折的轮廓点圆角（恢复尖角/共线直通）</summary>
    [ObservableProperty] private JoinStyle _nonCornerStyle = JoinStyle.Round;

    // ============== 接缝样式与圆角化 ==============

    /// <summary>当前选中接缝的有效样式（null = 继承全局；非 null = 已显式覆盖）。</summary>
    public JoinStyle? SelectedCornerEffectiveStyle =>
        SelectedOutlineCorner is { } h ? h.CurrentStyle : null;

    /// <summary>设置当前选中接缝的样式（UI 改 ComboBox 时调）。
    /// null = 清除覆盖（恢复继承全局）；非 null = 显式覆盖。</summary>
    public void SetSelectedCornerStyle(JoinStyle? style)
    {
        if (SelectedItem is not { } item) return;
        if (SelectedOutlineCorner is not { } h) return;
        item.SetVertexJoinStyle(h.Index, style);
        h.CurrentStyle = style;
    }

    /// <summary>清除当前选中接缝的样式覆盖（恢复继承全局）。</summary>
    public void ClearSelectedCornerOverride()
        => SetSelectedCornerStyle(null);

    /// <summary>一键清除所有接缝的逐顶点覆盖（全部恢复继承全局 OutlineJoinStyle）。
    /// 同时清各顶点的普通轮廓点圆角半径（左/右两侧）与转角内径，避免残留数据仍画弧。</summary>
    public void ClearAllCornerOverrides()
    {
        if (SelectedItem is not { } item) return;
        item.ClearVertexJoinStyles();
        foreach (var v in item.VertexThicknesses)
        {
            v.LeftCornerRadius = 0;
            v.RightCornerRadius = 0;
            v.JoinInnerRadius = 0;
        }
        RefreshOutlineCornerHandles();
    }

    /// <summary>选中接缝手柄时调用：把对应 emit 步骤替换为 ArcStep（圆角化）。
    /// 圆心=手柄主 Point，半径=radius（属性面板 Slider 传入）。
    /// 统一替换逻辑在 TryReplaceWithArc（与批量圆角共用同一行为）。
    /// 失败条件：手柄不在缓存步骤范围内 / BevelStep（拆点不可圆角化）/ 半径非法。</summary>
    public void ApplyCornerArcToSelected(double radius)
    {
        try
        {
            if (radius <= 0)
            {
                ShowCornerFeedback($"圆角半径非法（radius={radius}），必须 > 0。");
                return;
            }
            if (SelectedItem is not { } item)
            {
                ShowCornerFeedback("未选中折线图元，无法圆角化。");
                return;
            }
            if (SelectedOutlineCorner is not { } h)
            {
                ShowCornerFeedback("未选中轮廓接缝手柄，无法圆角化。");
                return;
            }
            var steps = item.CachedOutlineSteps;
            if (steps is null)
            {
                ShowCornerFeedback($"轮廓步骤缓存为空（CachedOutlineSteps=null）。\n可能原因：OutlineGeometry 尚未渲染，或 PathData 非法。PathData='{item.PathData}'");
                return;
            }
            // 两类轮廓点分流：
            //   - 转角点（Corner）：走骨架转角 join 系统 → per-vertex JoinStyle = Round，由 AdjustForRound 渲染
            //   - 普通点（Normal）：替换 emit 步骤为 ArcStep（不依赖 join 系统，独立圆角化）
            string? err = h.IsCorner
                ? TrySetCornerJoinRound(item, h, radius)
                : TryReplaceWithArc(steps, h, radius);
            if (err != null)
            {
                ShowCornerFeedback($"该接缝无法圆角化：{err}");
                return;
            }
            if (!h.IsCorner)
            {
                // 普通轮廓点：写 per-vertex 半径（按 L/R 侧），保存工程后可持久化/加载重建
                int vi = h.Index;
                if (vi >= 0 && vi < item.VertexThicknesses.Count)
                {
                    if (h.Side == "L") item.VertexThicknesses[vi].LeftCornerRadius = radius;
                    else item.VertexThicknesses[vi].RightCornerRadius = radius;
                }
            }
            item.NotifyOutlineGeometryChanged();
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"圆角化失败：{ex.Message}\n\n{ex.StackTrace}", "异常");
        }
    }

    /// <summary>核心圆角化：把一个手柄对应的 emit 步骤替换为 ArcStep（同时写回手柄 StepKind）。
    /// 成功返回 null；失败返回可直接展示的原因字符串（单点 / 批量统一提示用）。
    /// - InDir / OutDir 由 RefreshOutlineCornerHandles 在生成手柄时按 emit 序列前后步骤算出，
    ///   不区分 L/R、不区分内部/端点，所有角共用同一几何逻辑；
    /// - BevelStep（拆点）不可圆角化；入线/出线缺失或共线时跳过；
    /// - 圆弧端点 = 圆心 ± 方向 * len，len 由 GetArcTangentOffset 半角公式换算（与渲染同一行为）；
    /// - sweep：det(inDir,outDir) &lt; 0 → 外凸角 → Counterclockwise；det &gt; 0 → 内凹角 → Clockwise。</summary>
    private static string? TryReplaceWithArc(IList<Converters.PointStep> steps,
        OutlineCornerHandle h, double radius)
    {
        int idx = h.StepIndex;
        if (idx < 0 || idx >= steps.Count)
            return $"StepIndex={idx} 越界（steps.Count={steps.Count}）";
        var step = steps[idx];
        if (step is Converters.BevelStep)
            return "Bevel 拆点不可圆角化";
        Vector inDir = h.InDir;
        Vector outDir = h.OutDir;
        if (inDir == default || outDir == default)
            return "未携带 InDir/OutDir";
        double det = Vector.Determinant(inDir, outDir);
        if (Math.Abs(det) < 1e-9)
            return "入边/出边共线（0° 或 180°）";

        // 切点偏移 = 半径 × cot(半跨角)，与 ThickOutlineBuilder.AdjustForRound 同一换算
        double len = Converters.ThickOutlineBuilder.GetArcTangentOffset(inDir, outDir, radius);
        Point center = step.Point;
        Point arcStart = center - inDir * len;
        Point arcEnd = center + outDir * len;
        var sweep = det < 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
        // 保留原步骤的 Kind/JoinIndex/BoneIndex（否则缓存直接复用时骨架映射丢失）
        steps[idx] = new Converters.ArcStep(center, arcStart, arcEnd, radius, sweep,
            step.Kind, step.JoinIndex, step.BoneIndex);
        h.StepKind = "ArcStep";
        return null;
    }

    /// <summary>转角点（Corner）圆角化：走骨架转角 join 系统而非替换 emit 步骤。
    /// 半径通过 VertexThicknesses[vi].JoinInnerRadius 传入（基线机制：AdjustForRound 读它算内/外弧）。
    /// per-vertex JoinStyle = Round：让 BuildOutlineSteps 走 Round 扰动（不替换为 BevelStep）。
    /// 半径上限防御：避免用户把 Slider 拉大造成无限远端点偏移（> min(段半厚) × 2 时提示但仍允许）。</summary>
    private static string? TrySetCornerJoinRound(PrimitiveItem item, OutlineCornerHandle h, double radius)
    {
        int vi = h.Index;
        if (vi < 0 || vi >= item.VertexThicknesses.Count)
            return $"顶点索引 {vi} 越界（顶点数 {item.VertexThicknesses.Count}）";
        item.VertexThicknesses[vi].JoinInnerRadius = radius;
        item.SetVertexJoinStyle(vi, JoinStyle.Round);
        h.CurrentStyle = JoinStyle.Round;
        return null;
    }

    /// <summary>一键对当前折线所有非转角轮廓点按样式统一处理（属性面板"非转角点样式"+ 圆角半径共同决定）。
    /// 跳过骨线转折产生的转角点（Corner）：走骨架转角 join 系统，不属于本批量范围。
    /// 样式 = Miter：清除所有非转角点的 Left/Right CornerRadius（恢复 PointStep）
    /// 样式 = Round：把所有非转角点的 emit 步骤替换为 ArcStep（圆心=手柄点，半径=radius），并写 per-vertex 持久化
    /// Bevel：与 Miter 几何一致（拆点同样无圆弧）→ 走清除路径
    /// 成功 ≥ 1 个即刷新渲染并汇总提示。</summary>
    public void ApplyCornerArcToAll(JoinStyle style, double radius)
    {
        try
        {
            if (SelectedItem is not { } item)
            {
                ShowCornerFeedback("未选中折线图元，无法统一应用样式。");
                return;
            }
            // Miter / Bevel：仅清除非转角点圆角半径（无需 emit 步骤操作，几何由 PerVertexStyle 决定）
            if (style != JoinStyle.Round)
            {
                int cleared = 0;
                foreach (var v in item.VertexThicknesses)
                {
                    if (v.LeftCornerRadius > 0) { v.LeftCornerRadius = 0; cleared++; }
                    if (v.RightCornerRadius > 0) { v.RightCornerRadius = 0; cleared++; }
                }
                item.NotifyOutlineGeometryChanged();
                ShowCornerFeedback(cleared > 0
                    ? $"非转角点已恢复为尖角（Miter），清除了 {cleared} 个圆角。"
                    : "非转角点已恢复为尖角（Miter），无需清除。");
                return;
            }

            if (radius <= 0)
            {
                ShowCornerFeedback($"圆角半径非法（radius={radius}），必须 > 0。");
                return;
            }
            var steps = item.CachedOutlineSteps;
            if (steps is null || steps.Count == 0)
            {
                ShowCornerFeedback("轮廓步骤缓存为空（CachedOutlineSteps=null）。\n请先进入轮廓模式刷新手柄。");
                return;
            }
            int ok = 0, skipped = 0;
            var reasons = new List<string>();
            int skippedCorner = 0;  // 统计跳过的骨线转折点（用户明确要求）
            foreach (var h in OutlineCornerHandles)
            {
                // 跳过骨线转折产生的转角点（Corner）：这些走的是骨架转角 join 系统，不属于"应用到全部接缝"的批量范围
                if (h.IsCorner)
                {
                    skippedCorner++;
                    continue;
                }
                string? err = TryReplaceWithArc(steps, h, radius);
                if (err is null)
                {
                    ok++;
                    // 普通轮廓点：写 per-vertex 半径（按 L/R 侧），保存工程后可持久化/加载重建
                    int vi = h.Index;
                    if (vi >= 0 && vi < item.VertexThicknesses.Count)
                    {
                        if (h.Side == "L") item.VertexThicknesses[vi].LeftCornerRadius = radius;
                        else item.VertexThicknesses[vi].RightCornerRadius = radius;
                    }
                }
                else
                {
                    skipped++;
                    if (reasons.Count < 4) reasons.Add($"[顶点{h.Index} {h.Side}] {err}");
                }
            }
            if (ok == 0)
            {
                ShowCornerFeedback($"统一圆角失败：没有可圆角化的接缝。\n{string.Join("\n", reasons)}");
                return;
            }
            item.NotifyOutlineGeometryChanged();
            ShowCornerFeedback($"统一圆角完成：成功 {ok} 个，跳过 {skipped} 个" +
                (skippedCorner > 0 ? $"（含 {skippedCorner} 个骨线转折点）" : "") + "。" +
                (reasons.Count > 0 ? $"\n跳过原因：\n{string.Join("\n", reasons)}" : ""));
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"统一应用样式失败：{ex.Message}\n\n{ex.StackTrace}", "异常");
        }
    }

    /// <summary>圆角操作的用户反馈：弹 MessageBox 显示具体原因，便于开发期定位。
    /// 后续可替换为状态栏提示，不影响功能。</summary>
    private static void ShowCornerFeedback(string message)
        => System.Windows.MessageBox.Show(message, "圆角化提示");

    /// <summary>恢复圆角为非圆角（两类轮廓点分流，同步回写 per-vertex 持久化数据）：
    ///   - 转角点（Corner）：清 JoinInnerRadius + per-vertex JoinStyle → Miter（尖角），重新渲染恢复
    ///   - 普通点（Normal）：清该侧 CornerRadius，重新渲染重建为 PointStep</summary>
    public void RestoreCornerFromArc()
    {
        if (SelectedItem is not { } item) return;
        if (SelectedOutlineCorner is not { } h) return;
        int vi = h.Index;
        if (vi < 0 || vi >= item.VertexThicknesses.Count) return;

        if (h.IsCorner)
        {
            // 转角点：清内径 + 显式回 Miter（含全局 Round 也强制尖角）
            item.VertexThicknesses[vi].JoinInnerRadius = 0;
            item.SetVertexJoinStyle(vi, JoinStyle.Miter);
            item.NotifyOutlineGeometryChanged();
            h.StepKind = "PointStep";
            return;
        }

        // 普通轮廓点：先清 per-vertex 半径（存在时），触发重算自然恢复 PointStep
        double r = h.Side == "L" ? item.VertexThicknesses[vi].LeftCornerRadius
                                 : item.VertexThicknesses[vi].RightCornerRadius;
        if (r > 0)
        {
            if (h.Side == "L") item.VertexThicknesses[vi].LeftCornerRadius = 0;
            else item.VertexThicknesses[vi].RightCornerRadius = 0;
            item.NotifyOutlineGeometryChanged();
            h.StepKind = "PointStep";
            return;
        }

        // 兜底（无 per-vertex 数据的旧内存 ArcStep）：直接重置内存步骤
        var steps = item.CachedOutlineSteps;
        if (steps is null) return;
        int idx = h.StepIndex;
        if (idx < 0 || idx >= steps.Count) return;
        if (steps[idx] is not Converters.ArcStep) return;
        steps[idx] = new Converters.PointStep(steps[idx].Point);
        item.NotifyOutlineGeometryChanged();
        h.StepKind = "PointStep";
    }

    /// <summary>矩形类允许的最大圆角 / 倒角半径（上限取 min(w,h)/2，避免越界）。</summary>
    public double SelectedCornerRadiusMax =>
        SelectedItem is { } it ? System.Math.Max(0, System.Math.Min(it.Width, it.Height) * 0.5) : 0;
}