using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>
/// 选中 BandPolyline 后的编辑模式。
/// 三种模式共用 SelectedItem，但暴露不同的手柄集合和属性面板：
///   - Skeleton  = 骨架模式（默认）：显示顶点手柄（VertexHandles），编辑 PathData / 带宽
///   - BandWidth = 带宽线模式：显示每条带宽线（左右两条）及其两个端点
///   - Outline   = 轮廓模式：显示接缝手柄（OutlineCornerHandles），编辑接缝样式
/// 切换通过工具栏按钮或快捷键（Tab）触发；切换不丢 SelectedItem，只刷新手柄集合。
/// </summary>
public enum OutlineEditMode
{
    /// <summary>骨架编辑模式：操作骨架顶点 / 带宽 / 段长</summary>
    Skeleton,

    /// <summary>带宽线编辑模式：操作每条带宽线（左右两条）的两个端点带宽</summary>
    BandWidth,

    /// <summary>轮廓编辑模式：操作轮廓接缝（per-vertex OutlineJoinStyle + JoinInnerRadius）</summary>
    Outline,
}

/// <summary>
/// 右侧面板的页面（与"属性"页并列的第二个页面）。
/// Elements = 全量元素清单 + 隐藏/显示控制，用于排查画布上不可见的多余元素。
/// </summary>
public enum RightPanePage
{
    /// <summary>属性页（默认）：选中元素的属性编辑。</summary>
    Properties,

    /// <summary>元素页：所有 Primitives 的清单，可逐项隐藏/显示、点击选中定位。</summary>
    Elements,
}

/// <summary>
/// 元件设计器 ViewModel。
/// 左侧工具栏列出 5 个 L1 基础图形 + L2 复合图形，右侧画布（1920×1080）允许拖入并自由摆放，
/// 组合出 L2 设备元件的雏形。本 ViewModel 不持久化，重启后状态丢失（后续阶段再加保存）。
/// 本类按职责拆分为多个 partial 文件（*.SymbolDesignerViewModel.*.cs），本文件仅保留
/// 核心集合与构造函数：
///   - UndoRedo       撤销/重做栈与手势状态                Element 集合变更/隐藏/删除/定位
///   - Canvas         画布尺寸/缩放平移/背景参考图          Tools   工具栏绘制工具
///   - Draw           画布绘制状态机                        DrawState  画线状态与选中类型判定
///   - EditMode       编辑模式切换 + 右侧面板切换           Editing    选中图元联动/删除/清空
///   - Selection      多选 + 相切/重合约束命令              Constraints  持久几何约束求解
///   - Array / ArrayPreview  沿路径阵列 + 渲染层展平        Handles     顶点/带宽/圆环/轮廓手柄
///   - OutlineCorners 轮廓接缝圆角化                        Persistence 工程保存/加载
///   - BackgroundImage 背景图导入/清除                      XamlExport  XAML 导出/剪贴板
/// </summary>
public partial class SymbolDesignerViewModel : ObservableObject
{
    // ============== 核心集合（跨职责共享） ==============

    /// <summary>画布上已有的图形实例集合（按添加顺序排列）。</summary>
    public ObservableCollection<PrimitiveItem> Primitives { get; } = new();

    /// <summary>画布上的所有沿路径阵列实例集合（CAD 风格块引用）。
    /// 每条记录 = 源快照 + 路径 + 副本数；渲染时由 VM 展开为 N 份副本（见 ItemsControl 渲染层）。
    /// 修改源 Primitives 不会反向影响阵列（阵列存的是源快照，不是指针），符合"阵列 = 独立工件"语义。
    /// 第一阶段：仅支持 BandPolyline 路径（M/L 直线序列）。</summary>
    public ObservableCollection<ArrayInstance> ArrayInstances { get; } = new();

    /// <summary>渲染层"展平"集合：每个 ArrayInstance 展开为 N 份 PrimitiveItem（Clone + Placement 变换）。
    /// 由 RebuildArrayRenderItems() 在 ArrayInstances 变化时重建，供 XAML 的 ItemsControl 直接绑定。
    /// 这些 PrimitiveItem 不进入 Primitives 集合（避免与源重复 + 避免进入撤销栈），仅作为渲染层的影子。</summary>
    public ObservableCollection<PrimitiveItem> ArrayRenderItems { get; } = new();

    /// <summary>当前元素数量（状态栏显示用）。</summary>
    [ObservableProperty] private int _itemCount;

    /// <summary>取色面板"最近使用颜色"列表（环形缓冲：最多 10 色，最近在前，去重保留首次出现位）。
    /// 由 SelectedItem 的 FillColor/StrokeColor/GradientStartColor/GradientEndColor 变化时推入，
    /// 共享给所有 SwatchesPicker 实例显示。</summary>
    public ObservableCollection<string> RecentColorings { get; } = new();

    public SymbolDesignerViewModel()
    {
        // 集合增删 → 标 dirty；同时订阅新元素 PropertyChanged，新增的子项属性变化也标 dirty
        Primitives.CollectionChanged += OnPrimitivesChanged;
        // 沿路径阵列集合增删 → 同样标 dirty（快照由 SnapshotPrimitives() 覆盖两个集合）
        ArrayInstances.CollectionChanged += OnArrayInstancesChanged;

        // 属性编辑去抖:时间到 → 落栈
        _undoCommitTimer.Tick += (_, _) => ForceCommitEdit();

        // 初始化时计算一次带宽上限(默认画布 1920x1080 → 短边 1080×0.3=324,clamp 后 324)
        // 否则首屏显示的 Slider 上限是字段默认值 30,直到用户改画布尺寸才刷新
        RecomputeSelectedBandWidthMax();

        // 阶段 2：三模式按钮集合固定三项，初始化后基本不变。
        // IsSelected 字段在 OnOutlineEditModeChanged 时刷新，所以只读包装够用。
        var cmd = EnterModeCommand;
        _editModeChoices.Add(new EditModeChoice { Mode = OutlineEditMode.Skeleton,  Label = "骨架",   Command = cmd, CommandParameter = OutlineEditMode.Skeleton  });
        _editModeChoices.Add(new EditModeChoice { Mode = OutlineEditMode.BandWidth, Label = "带宽线", Command = cmd, CommandParameter = OutlineEditMode.BandWidth });
        _editModeChoices.Add(new EditModeChoice { Mode = OutlineEditMode.Outline,   Label = "轮廓",   Command = cmd, CommandParameter = OutlineEditMode.Outline   });
        EditModeChoices = new System.Collections.ObjectModel.ReadOnlyObservableCollection<EditModeChoice>(_editModeChoices);
        RefreshEditModeChoices();

        // 右侧面板"属性/元素"两页切换按钮：结构与三模式按钮一致。
        var paneCmd = ShowRightPaneCommand;
        _rightPaneChoices.Add(new RightPaneChoice { Page = RightPanePage.Properties, Label = "属性", Command = paneCmd, CommandParameter = RightPanePage.Properties  });
        _rightPaneChoices.Add(new RightPaneChoice { Page = RightPanePage.Elements,  Label = "元素", Command = paneCmd, CommandParameter = RightPanePage.Elements   });
        RightPaneChoices = new System.Collections.ObjectModel.ReadOnlyObservableCollection<RightPaneChoice>(_rightPaneChoices);
        RefreshRightPaneChoices();
        RefreshHiddenCount();
    }
}