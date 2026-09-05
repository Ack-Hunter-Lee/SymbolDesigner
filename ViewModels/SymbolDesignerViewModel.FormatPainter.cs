using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>
/// 格式刷（Format Painter）：选中节点 → 点击工具栏"格式刷"按钮 → 提取该图元的外观样式
/// （线条颜色/粗细/虚线、填充方式/颜色/渐变端点）；按钮处于"应用待命"状态后，点击
/// 画布上的其它图元 → 把提取的样式写到目标上，并自动退出发送状态。
/// 设计为"单次应用 + 自动退出"——Office / AutoCAD 的格式刷默认行为；
/// 也提供 "OnAfterApplied = ApplyAgain" 的连刷模式（按 Ctrl 启用，方便批量格式刷）。
///
/// 实现要点：
/// - 样式快照存 DTO（PrimitiveItemDto），与撤销/序列化共用同一通道，避免运行时
///   直接持有源图元引用（源图元可能被删除/修改后让格式刷"幽灵生效"）。
/// - 应用阶段用 ToDto→CreateFromDto 链路深拷贝目标当前状态、再按快照字段覆盖，
///   保证不破坏 X/Y/Width/Height/PathData 等几何信息（只改外观）。
/// - 视觉提示：IsFormatPainterActive=true 时画布光标切换为"画笔/十字"（XAML Trigger）。
/// </summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>格式刷按钮的可执行态：
    /// - 空闲态（IsFormatPainterActive=false）：选中图元后才可"吸取"样式
    /// - 应用待命态（IsFormatPainterActive=true）：始终可点——按一次即退出（即便 SelectedItem 为 null 也能取消）
    /// - 文本输入框聚焦时不拦截 Ctrl+C/V 等系统快捷键
    /// </summary>
    public bool CanPickFormat
        => !IsTextFieldFocused()
        && (IsFormatPainterActive || SelectedItem is not null);

    /// <summary>格式刷"应用待命"开关。true = 工具栏已吸取样式，等待用户点击目标图元。
    /// XAML DataTrigger 用此属性切光标 / 切按钮高亮。</summary>
    [ObservableProperty] private bool _isFormatPainterActive;

    /// <summary>已提取的样式快照（线条/填充全部字段；不存几何）。null = 未吸取。</summary>
    private PrimitiveItemDto? _formatPainterSnapshot;

    /// <summary>格式刷"连刷"开关：Ctrl+点击格式刷按钮进入，应用完样式后保持激活，
    /// 便于批量刷同一外观（Office 长按格式刷 / Paint 模式）。</summary>
    private bool _formatPainterLockMode;

    /// <summary>格式刷工具栏按钮：未激活→吸取选中图元样式；已激活→关闭应用模式（Office 双击取消）。</summary>
    [RelayCommand(CanExecute = nameof(CanPickFormat))]
    private void PickFormatPainter()
    {
        // 当前处于"应用待命"→ 再次点击 = 退出发送模式（与 Office 行为一致）
        if (IsFormatPainterActive)
        {
            ClearFormatPainter();
            return;
        }

        if (SelectedItem is null) return;
        // 深拷贝 DTO 字段（线条/填充），不持有源图元引用（源被删/改不影响格式刷）
        _formatPainterSnapshot = ToDto(SelectedItem);
        IsFormatPainterActive = true;
        PickFormatPainterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把吸取的样式写到目标图元。仅改外观（线条/填充相关字段），保留几何（位置/尺寸/路径）。
    /// 应用完根据 _formatPainterLockMode 决定是否退出发送模式。</summary>
    public void ApplyFormatPainterTo(PrimitiveItem target)
    {
        if (!IsFormatPainterActive || _formatPainterSnapshot is null || target is null) return;

        // 开始编辑：把"应用格式刷"登记为一次可撤销步骤（按一次按钮 = 一次撤销）
        BeginEdit();

        var src = _formatPainterSnapshot;
        // 外观字段全覆盖（DTO 的可空字段遇到 null 时保持目标现有值——避免破坏默认描边宽度）
        if (!string.IsNullOrEmpty(src.StrokeColor)) target.StrokeColor = src.StrokeColor!;
        if (src.StrokeWidth.HasValue) target.StrokeWidth = src.StrokeWidth.Value;
        target.StrokeDashStyle = ParseStrokeDashStyle(src.StrokeDashStyle);
        target.FillStyle = ParseFillStyle(src.FillStyle);
        target.FillColor = src.FillColor ?? target.FillColor;
        target.GradientStartColor = src.GradientStartColor ?? target.GradientStartColor;
        target.GradientEndColor = src.GradientEndColor ?? target.GradientEndColor;

        EndEdit();
        // 单次模式：应用完退出；连刷模式（Ctrl+按钮）保持激活
        if (!_formatPainterLockMode)
            ClearFormatPainter();
    }

    /// <summary>退出发送模式并清空样式快照。</summary>
    public void ClearFormatPainter()
    {
        IsFormatPainterActive = false;
        _formatPainterSnapshot = null;
        _formatPainterLockMode = false;
        PickFormatPainterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ctrl+点击格式刷按钮：进入连刷模式（应用完样式后保持激活）。
    /// 由 OnFormatPainterClick（XAML MouseLeftButtonDown handler 调用 ModifierKeys 判断）。</summary>
    public void EnterFormatPainterLocked()
    {
        if (SelectedItem is null) return;
        if (IsFormatPainterActive) return; // 已激活 → 不切换模式（避免误触把单次变连刷）
        _formatPainterSnapshot = ToDto(SelectedItem);
        _formatPainterLockMode = true;
        IsFormatPainterActive = true;
        PickFormatPainterCommand.NotifyCanExecuteChanged();
    }
}