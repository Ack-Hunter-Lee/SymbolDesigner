using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>阵列预览式创建（Rectangular / Polar / Path）与正式渲染：预览参数、克隆布局、确认/取消。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== 阵列创建（Array：Rectangular / Polar / Path） ==============

    /// <summary>阵列"预览式创建"浮层是否打开。
    /// 打开 = 画布上已显示半透明预览副本（未提交），任何参数改动即重建预览。</summary>
    [ObservableProperty] private bool _isPathArrayPreviewOpen;

    /// <summary>预览阶段当前阵列类型（矩形 / 环形 / 路径）。
    /// 默认 Path，打开预览时若无可用路径则自动切 Rectangular（避免"必须画线"）。</summary>
    [ObservableProperty] private Models.ArrayType _arrayPreviewType = Models.ArrayType.Path;

    /// <summary>预览式创建：数量（Path / Polar 用，1..100）。改动即 RebuildArrayPreview 重建预览。</summary>
    [ObservableProperty] private int _pathArrayCount = 5;

    /// <summary>预览式创建：路径阵列"偏移距离"（副本中心与参考路径的垂直距离，
    /// 默认自动取"源中心到路径的当前最近距离"）。手动改数值会自动退出"自动保持"模式。
    /// 改动即 RebuildArrayPreview 重建预览。</summary>
    [ObservableProperty] private double _pathArrayOffset;

    /// <summary>预览式创建：偏移距离是否自动保持当前值（默认 true）。
    /// 打开 = 路径 / 源变化时重算"源中心到路径的最近距离"并回写；关闭 = 只用手动输入的偏移。</summary>
    [ObservableProperty] private bool _pathArrayOffsetAuto = true;

    /// <summary>界面显示的偏移距离（**绝对值**，恒 ≥ 0）。
    /// 内部 PathArrayOffset 有符号（正负区分路径左右侧），用户不该被符号困扰：
    /// 输入 10 = 保持源所在那一侧、距离 10。手动改本属性 → 退出自动模式。</summary>
    [ObservableProperty] private double _pathArrayOffsetDistance;

    /// <summary>偏移方向（+1 / -1）：源位于路径切向的哪一侧。自动模式重算时刷新，手改距离时沿用。</summary>
    private int _pathArrayOffsetSign = 1;

    /// <summary>预览式创建：副本是否随路径切线旋转（默认 true = 切向站立，对齐 AutoCAD "Align items"）。</summary>
    [ObservableProperty] private bool _pathArrayAlign = true;

    /// <summary>重入守卫：内部回写 PathArrayOffset / PathArrayOffsetDistance 时勿再互相触发。</summary>
    private bool _syncingArrayOffset;

    // ── Rectangular 预览参数 ──
    /// <summary>列数（横向，沿 +X）。</summary>
    [ObservableProperty] private int _arrayPreviewColumns = 4;

    /// <summary>行数（纵向，沿 +Y）。</summary>
    [ObservableProperty] private int _arrayPreviewRows = 3;

    /// <summary>列间距（相邻副本中心 +X 距离）。</summary>
    [ObservableProperty] private double _arrayPreviewColSpacing = 40;

    /// <summary>行间距（相邻副本中心 +Y 距离）。</summary>
    [ObservableProperty] private double _arrayPreviewRowSpacing = 40;

    // ── Polar 预览参数 ──
    /// <summary>填充角度（度，360 = 整圆，负 = 顺时针）。</summary>
    [ObservableProperty] private double _arrayPreviewFillAngle = 360;

    /// <summary>环形副本随圆周旋转（切向对齐）。</summary>
    [ObservableProperty] private bool _arrayPreviewRotateItems = true;

    /// <summary>环形圆心 X（画布坐标）。</summary>
    [ObservableProperty] private double _arrayPreviewCenterX;

    /// <summary>环形圆心 Y（画布坐标）。</summary>
    [ObservableProperty] private double _arrayPreviewCenterY;

    /// <summary>预览渲染层：处于预览态时显示 N 份半透明副本（尚未提交到 ArrayInstances）。
    /// 由 RebuildArrayPreview() 填充，确认后清除并写 ArrayInstances。</summary>
    public ObservableCollection<PrimitiveItem> PathArrayPreviewItems { get; } = new();

    /// <summary>预览态源（用户选中的图形，确认时深拷贝入 ArrayInstance）。</summary>
    private PrimitiveItem? _previewSource;

    /// <summary>预览态"参考路径"候选（创建模式填充；编辑模式清空 —— 老阵列只存几何快照，
    /// 没有路径图元的引用，无法反查当初用的是哪一个）。</summary>
    public ObservableCollection<ArrayPathOption> PathOptions { get; } = new();

    /// <summary>参考路径下拉当前选中项（用户在浮层里显式指定哪一个图元当路径）。
    /// 不用"按类型猜"的自动链路：那样选什么都会被某条优先链劫持，用户无法表达意图。</summary>
    [ObservableProperty] private ArrayPathOption? _selectedPathOption;

    /// <summary>类型下拉选项（中文显示名 + 枚举值），供浮层 ComboBox 绑定。</summary>
    public IReadOnlyList<ArrayTypeOption> ArrayTypeOptions { get; } = new[]
    {
        new ArrayTypeOption("矩形阵列", Models.ArrayType.Rectangular),
        new ArrayTypeOption("环形阵列", Models.ArrayType.Polar),
        new ArrayTypeOption("路径阵列", Models.ArrayType.Path),
    };

    /// <summary>类型下拉当前选中项。</summary>
    [ObservableProperty] private ArrayTypeOption? _selectedArrayTypeOption;

    // ── 参数区可见性（随 ArrayPreviewType 变化）──
    /// <summary>路径阵列参数区可见（仅 Type==Path）。</summary>
    public bool ArrayPathParamsVisible => ArrayPreviewType == Models.ArrayType.Path;

    /// <summary>"参考路径"下拉可见（仅创建模式的路径阵列；编辑模式 PathOptions 为空，见其注释）。</summary>
    public bool ArrayPathPickerVisible => ArrayPathParamsVisible && !_isArrayEditMode;

    /// <summary>矩形阵列参数区可见（仅 Type==Rectangular）。</summary>
    public bool ArrayRectParamsVisible => ArrayPreviewType == Models.ArrayType.Rectangular;

    /// <summary>环形阵列参数区可见（仅 Type==Polar）。</summary>
    public bool ArrayPolarParamsVisible => ArrayPreviewType == Models.ArrayType.Polar;

    partial void OnArrayPreviewTypeChanged(Models.ArrayType value)
    {
        // 同步下拉选中项（内部状态唯一源是 ArrayPreviewType）
        SelectedArrayTypeOption = ArrayTypeOptions.FirstOrDefault(o => o.Value == value);
        // 同步参数区可见性（XAML 三组参数行）
        OnPropertyChanged(nameof(ArrayPathParamsVisible));
        OnPropertyChanged(nameof(ArrayPathPickerVisible));
        OnPropertyChanged(nameof(ArrayRectParamsVisible));
        OnPropertyChanged(nameof(ArrayPolarParamsVisible));
        // 切到环形：创建预览若无显式圆心，按源位置预设一个合理圆心（源右侧约一个源宽）；
        // 编辑模式圆心来自阵列已有值，不做预设
        if (_isArrayEditMode)
        {
            ApplyArrayEdit();
            return;
        }
        if (IsPathArrayPreviewOpen && value == Models.ArrayType.Polar
            && _previewSource is { } s && ArrayPreviewCenterX == 0 && ArrayPreviewCenterY == 0)
        {
            ArrayPreviewCenterX = s.X + s.Width * 1.5;
            ArrayPreviewCenterY = s.Y + s.Height * 1.5;
        }
        if (IsPathArrayPreviewOpen) RebuildArrayPreview();
    }

    partial void OnSelectedArrayTypeOptionChanged(ArrayTypeOption? value)
    {
        if (value is not null && value.Value != ArrayPreviewType)
            ArrayPreviewType = value.Value;
    }

    /// <summary>换参考路径 → 立即重建预览（仅创建模式；编辑模式无候选、路径取老阵列快照不改）。</summary>
    partial void OnSelectedPathOptionChanged(ArrayPathOption? value)
    {
        if (IsPathArrayPreviewOpen && !_isArrayEditMode) RebuildArrayPreview();
    }

    partial void OnPathArrayCountChanged(int value) => OnArrayParamsChanged();

    /// <summary>用户手动输入偏移距离（绝对值）：保持源所在那一侧（符号沿用），退出自动模式。</summary>
    partial void OnPathArrayOffsetDistanceChanged(double value)
    {
        if (_syncingArrayOffset) return;
        // 负号 = 切到路径另一侧（对称），数值仍归一到正
        if (value < 0)
        {
            _pathArrayOffsetSign = -_pathArrayOffsetSign;
            value = -value;
        }
        // 手动改数值 = 用户接管偏移量 → 退出自动模式（否则下次路径 / 源变化会把手改值冲掉）
        if (PathArrayOffsetAuto) PathArrayOffsetAuto = false;
        _syncingArrayOffset = true;
        PathArrayOffset = _pathArrayOffsetSign * value;
        if (System.Math.Abs(PathArrayOffsetDistance - value) > 1e-12)
            PathArrayOffsetDistance = value; // 负输入归一成正
        _syncingArrayOffset = false;
        OnArrayParamsChanged();
    }

    partial void OnPathArrayOffsetAutoChanged(bool value)
    {
        if (!value) return;
        // 切回自动：立即按当前源与路径重算并重建（自动值变了 → 预览要跟着动）
        SyncAutoPathOffset();
        OnArrayParamsChanged();
    }

    partial void OnPathArrayAlignChanged(bool value) => OnArrayParamsChanged();

    partial void OnArrayPreviewColumnsChanged(int value) => OnArrayParamsChanged();

    partial void OnArrayPreviewRowsChanged(int value) => OnArrayParamsChanged();

    partial void OnArrayPreviewColSpacingChanged(double value) => OnArrayParamsChanged();

    partial void OnArrayPreviewRowSpacingChanged(double value) => OnArrayParamsChanged();

    partial void OnArrayPreviewFillAngleChanged(double value) => OnArrayParamsChanged();

    partial void OnArrayPreviewRotateItemsChanged(bool value) => OnArrayParamsChanged();

    partial void OnArrayPreviewCenterXChanged(double value) => OnArrayParamsChanged();

    partial void OnArrayPreviewCenterYChanged(double value)
    {
        if (IsPathArrayPreviewOpen) RebuildArrayPreview();
    }

    /// <summary>开始预览式创建。源 = 当前选中图形（任意类型，能出几何就能阵列）；
    /// 参考路径 = 浮层里的"路径"下拉，由用户显式指定（默认第一个候选）。
    /// 类型默认取上次选择；若为 Path 但画布上没有可用路径 → 自动切 Rectangular（无需参考路径即可阵列）。
    /// 立即生成预览，浮层可改类型 / 路径 / 全套参数，✓ 确认 / ✕ 或 Esc 取消。</summary>
    [RelayCommand]
    private void StartArrayPreview()
    {
        var src = SelectedItem;
        if (src is null)
        {
            System.Windows.MessageBox.Show("请先选中要阵列的源图形（矩形 / 椭圆 / 圆环 / 折线 / 履带等任意图形）。", "阵列",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        _previewSource = src;
        // 路径候选 = 画布上除源之外任何"能产出几何"的图元；选哪一个由用户在浮层下拉里定，
        // 不再"按类型猜"（旧实现的两条 BandPolyline 优先链会劫持用户选择，选什么都落到折线上）。
        RefreshPathOptions(src);
        SelectedPathOption = PathOptions.FirstOrDefault();
        if (ArrayPreviewType == Models.ArrayType.Path && PathOptions.Count == 0)
        {
            // 无可用路径 → 自动切矩形阵列（用户不必先画一条参考线）
            ArrayPreviewType = Models.ArrayType.Rectangular;
        }
        // 同步类型下拉初始选中项（首次打开时 SelectedArrayTypeOption 仍为 null）
        SelectedArrayTypeOption = ArrayTypeOptions.FirstOrDefault(o => o.Value == ArrayPreviewType);
        // 新一次创建从"自动保持当前偏移"起步（上次手动输入的值不留到新阵列）
        _syncingArrayOffset = true;
        PathArrayOffsetAuto = true;
        _syncingArrayOffset = false;
        IsPathArrayPreviewOpen = true;
        RebuildArrayPreview();
    }

    /// <summary>填充"参考路径"候选：画布上除源之外、任何能产出几何的图元（几何入口 =
    /// PrimitiveItem.SelectionGeometry，不按类型列白名单）。标签带序号便于同类图形区分。</summary>
    private void RefreshPathOptions(PrimitiveItem? src)
    {
        PathOptions.Clear();
        var counters = new Dictionary<string, int>();
        foreach (var p in Primitives)
        {
            if (src is not null && ReferenceEquals(p, src)) continue;
            if (CanvasGeometry(p) is null) continue;
            string name = TypeDisplayName(p.TypeKey);
            counters.TryGetValue(name, out int n);
            counters[name] = ++n;
            PathOptions.Add(new ArrayPathOption($"{name} #{n}", p));
        }
    }

    /// <summary>图元类型的中文名（仅用于路径下拉的可读标签）。</summary>
    private static string TypeDisplayName(string typeKey) => typeKey switch
    {
        nameof(L1Primitive.Rectangle) => "矩形",
        nameof(L1Primitive.RoundedRectangle) => "圆角矩形",
        nameof(L1Primitive.Ellipse) => "椭圆",
        nameof(L1Primitive.Triangle) => "三角形",
        nameof(L1Primitive.Ring) => "圆环",
        nameof(L1Primitive.Pie) => "扇形",
        nameof(L1Primitive.Line) => "直线",
        nameof(L1Primitive.Polyline) => "折线",
        nameof(L1Primitive.BandPolyline) => "带状折线",
        nameof(L1Primitive.BooleanPath) => "布尔结果",
        nameof(L1Primitive.Belt) => "履带",
        _ => typeKey,
    };

    /// <summary>按当前类型/参数重建预览层：生成 N 份半透明克隆填充 PathArrayPreviewItems。
    /// 与正式渲染共用 BuildArrayClones（同一套 Placement 计算），保证预览所见 = 确认所得。
    /// 矩形/环形跳过第 1 份：它与源完全重合，预览应只画"新增副本"，源位由源自身代表，
    /// 避免用户看到"源 + 同位置副本"重叠（AutoCAD 预览同样不重复画源项）。</summary>
    private void RebuildArrayPreview()
    {
        PathArrayPreviewItems.Clear();
        if (_previewSource is null) return;
        // 自动模式：按当前源 / 路径重算偏移距离（回写 PathArrayOffset）后再装配
        if (ArrayPreviewType == Models.ArrayType.Path) SyncAutoPathOffset();
        var arr = ArrayInstanceFromPreview();
        if (arr is null) return;
        UpdatePolarGuide(arr);
        // 一律跳过第 1 份：三种阵列布局的第一份都落在源当前位置（矩形/环形 = 源位本身；
        // 路径 = 源中心在路径上的投影点 + 法向 PathOffset，等价于源当前位置）。
        // 源本体已在画布上，重复绘制只会重叠；数量 N 表示"含源在内共 N 个"。
        int idx = 0;
        foreach (var c in BuildArrayClones(arr, Primitives.Count))
        {
            if (idx++ == 0) continue;
            PathArrayPreviewItems.Add(c);
        }
    }

    /// <summary>自动模式：重算"源中心到路径的最近距离"并回写内部有符号偏移 + 界面距离（不触发重建）。
    /// 手动模式 / 无源 / 无路径 → 不动。</summary>
    private void SyncAutoPathOffset()
    {
        if (!PathArrayOffsetAuto) return;
        if (ComputeAutoPathOffset() is not { } v) return;
        _syncingArrayOffset = true;
        PathArrayOffset = v;
        _pathArrayOffsetSign = v < 0 ? -1 : 1;
        PathArrayOffsetDistance = System.Math.Abs(v);
        _syncingArrayOffset = false;
    }

    /// <summary>源中心到参考路径的有符号最近距离（画布坐标；正 = 路径切向左侧）。
    /// 路径走"几何 → 摊平直线段 → 最近点投影"通用入口。源 / 路径缺失 → null。</summary>
    private double? ComputeAutoPathOffset()
    {
        // 创建预览：源 = 画布选中图元，路径 = 下拉选中的图元
        if (_previewSource is { } src)
        {
            if (SelectedPathOption?.Item is not { } pathItem) return null;
            if (CanvasGeometry(pathItem) is not { } pathGeo) return null;
            return ProjectOffset(pathGeo, SourceCenter(src));
        }
        // 编辑已有阵列：没有预览源，用阵列自身的源快照 + 路径快照
        if (_editingArray is { } arr)
        {
            if (ParsePathData(arr.PathData) is not { } pathGeo) return null;
            return ProjectOffset(pathGeo, new System.Windows.Point(
                arr.Source.X + arr.Source.Width / 2,
                arr.Source.Y + arr.Source.Height / 2));
        }
        return null;
    }

    /// <summary>把画布坐标点投影到路径几何，返回有符号垂直距离；路径为空 → null。</summary>
    private static double? ProjectOffset(System.Windows.Media.Geometry pathGeo, System.Windows.Point point)
    {
        var segments = Converters.PolylineToSegments.Convert(pathGeo);
        if (segments.Count == 0) return null;
        return Converters.PathDistributor.ProjectOntoPath(segments, point)?.SignedOffset;
    }

    /// <summary>源的画布中心（包围盒中心）：全工程统一约定"中心 = 包围盒中心"，
    /// 也是阵列副本的旋转枢轴（ContentPresenter RenderTransformOrigin=0.5,0.5）。</summary>
    private static System.Windows.Point SourceCenter(PrimitiveItem item)
        => new(item.X + item.Width / 2, item.Y + item.Height / 2);

    /// <summary>把预览态参数装配成 ArrayInstance（Path 类型要求已有可用路径）。</summary>
    private ArrayInstance? ArrayInstanceFromPreview()
    {
        if (_previewSource is null) return null;
        var arr = new ArrayInstance
        {
            Source = ToDto(_previewSource),
            Type = ArrayPreviewType,
            ZIndexOffset = 1,
        };
        switch (ArrayPreviewType)
        {
            case Models.ArrayType.Rectangular:
                arr.Columns = Math.Clamp(ArrayPreviewColumns, 1, 100);
                arr.Rows = Math.Clamp(ArrayPreviewRows, 1, 100);
                arr.ColSpacing = ArrayPreviewColSpacing;
                arr.RowSpacing = ArrayPreviewRowSpacing;
                break;
            case Models.ArrayType.Polar:
                arr.Count = Math.Clamp(PathArrayCount, 1, 100);
                arr.CenterX = ArrayPreviewCenterX;
                arr.CenterY = ArrayPreviewCenterY;
                arr.FillAngle = ArrayPreviewFillAngle;
                arr.RotateItems = ArrayPreviewRotateItems;
                break;
            default:
                // 路径几何必须为**画布绝对坐标**：BuildArrayClones 把副本中心直接落到布局点上，
                // 只再加阵列整体 Offset。故这里存通用入口产出的画布坐标路径，而非路径图元自己的
                // PathData（那是局部坐标：折线 / 履带的几何都相对自身 X/Y）。
                if (SelectedPathOption?.Item is not { } pathItem) return null;
                if (CanvasGeometry(pathItem) is not { } pathGeo) return null;
                string pathData = pathGeo.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(pathData)) return null;
                arr.PathData = pathData;
                arr.Count = Math.Clamp(PathArrayCount, 1, 100);
                arr.PathOffset = PathArrayOffset;
                arr.PathAlign = PathArrayAlign;
                break;
        }
        return arr;
    }

    /// <summary>确认预览 → 提交 ArrayInstance 并退出预览态。源存快照（ToDto），
    /// 确认后预览副本清除，正式副本由 ArrayInstances → RebuildArrayRenderItems 重建。</summary>
    [RelayCommand]
    private void ConfirmArrayPreview()
    {
        // 编辑源模式：✓ = 完成源编辑，提交并回到"编辑阵列"浮层（参数可继续调）
        if (_isArraySourceEditMode)
        {
            var done = _arraySourceEditFor;
            CollapseArraySourceEdit();
            if (done is not null) SelectArray(done); // 重新选中阵列 → 浮层回"编辑阵列"态
            return;
        }
        // 编辑模式：参数已在输入过程中实时写回（ApplyArrayEdit），✓ 仅关闭浮层
        if (_isArrayEditMode)
        {
            ExitArrayEdit();
            return;
        }
        var arr = ArrayInstanceFromPreview();
        if (arr is null)
        {
            // 静默 return 会让「✓ 确定」看起来毫无反应 —— 用户分不清"没点中"和"条件不满足"。
            // 路径阵列唯一可能失败处就是找不到可用作路径的图元 → 明确告知。
            System.Windows.MessageBox.Show(
                "路径阵列需要一个「参考路径」图元：画布上除源图形外，至少再有一个图形。"
                + "直线 / 折线 / 圆 / 矩形 / 履带 / 布尔结果等任意图形都可以当路径。",
                "阵列", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        // 阵列吸收源：AutoCAD 关联阵列语义 —— 创建后源成为阵列的第一份副本，不再作为独立图元存在。
        // 否则源（Primitives）与同位置第一份克隆双层重叠，用户会看到"两个元素"。
        // 顺序：先 Add 阵列（克隆补位渲染）→ 再摘除源 → 视觉无缝；撤销一次整体回滚（源随快照还原）。
        var src = _previewSource;
        ArrayInstances.Add(arr);
        if (src is not null)
        {
            // 按 Id 找源并移除（正常是同一引用；兜底：若预览期间发生撤销等导致实例被重建，
            // 引用会指向旧对象 → Id 匹配画布中当前实例）
            var toRemove = Primitives.FirstOrDefault(p => p.Id == src.Id);
            if (toRemove is not null)
            {
                SelectedItem = null;      // 源可能正被选中，移除前清焦点（属性面板/手柄随之清空）
                Primitives.Remove(toRemove); // 触发 OnPrimitivesChanged → 与 Add 合并进同一撤销步骤
                ItemCount = Primitives.Count;
                PruneConstraints();       // 源若有相切约束 → 清理孤儿引用
                OnPropertyChanged(nameof(CanRecomputeBoolean)); // 源可能是布尔源 → 刷新合并可用态
            }
        }
        ExitArrayPreview();
    }

    /// <summary>取消预览（✕ / Esc / 遮罩点击）：清预览副本，不创建阵列。</summary>
    [RelayCommand]
    private void CancelArrayPreview()
    {
        // 编辑源模式：✕ = 完成源编辑（同上，无"放弃"概念，改动即生效），回"编辑阵列"浮层
        if (_isArraySourceEditMode)
        {
            var done = _arraySourceEditFor;
            CollapseArraySourceEdit();
            if (done is not null) SelectArray(done);
            return;
        }
        // 编辑模式：✕ 仅关闭浮层（参数已实时生效，无未提交内容）
        if (_isArrayEditMode)
        {
            ExitArrayEdit();
            return;
        }
        ExitArrayPreview();
    }

    /// <summary>退出预览态：清空预览渲染层与临时候选引用。</summary>
    private void ExitArrayPreview()
    {
        IsPathArrayPreviewOpen = false; // 先关（下拉清空会触发 SelectedItem 变更，勿在此时重建）
        PathArrayPreviewItems.Clear();
        PathOptions.Clear();
        SelectedPathOption = null;
        _previewSource = null;
    }

    /// <summary>重建 ArrayRenderItems：遍历 ArrayInstances，对每条按类型计算 N 个 Placement，
    /// 每份 Placement = 一份源 PrimitiveItem 克隆（平移 + 旋转）。
    /// 调用时机：
    ///   - ArrayInstances.CollectionChanged（Add/Remove）
    ///   - 工程加载/撤销恢复后
    /// 预览式创建流与正式渲染共用 BuildArrayClones，保证布局一致。
    /// 锚点约定：所有图形统一以**包围盒中心**为锚（BandPolyline 的 Width/Height = bbox 尺寸，
    /// PathData 是局部坐标），副本旋转后的中心恰好落在 Placement 上。</summary>
    public void RebuildArrayRenderItems()
    {
        ArrayRenderItems.Clear();
        int globalZ = Primitives.Count; // 阵列副本整体放在源集合之后
        foreach (var arr in ArrayInstances)
        {
            foreach (var c in BuildArrayClones(arr, globalZ))
                ArrayRenderItems.Add(c);
            globalZ += arr.Count + arr.ZIndexOffset; // 给下一个阵列留层叠空间
        }
        // 重建后克隆 IsSelected 已被 BuildArrayClones 复位为 false → 重刷选中阵列的高亮
        SyncArraySelectionHighlight();
    }

    /// <summary>按 ArrayInstance 计算 N 份克隆：按类型分发布局计算，统一以"源包围盒中心"为锚。
    /// - Rectangular：行列网格（无参考路径）
    /// - Polar：绕圆心圆周分布（无参考路径）
    /// - Path：沿路径分布，副本中心 = 路径点 + 法线 × PathOffset（保持与路径恒定距离）
    /// 预览式创建（RebuildArrayPreview）与正式渲染（RebuildArrayRenderItems）共用。</summary>
    private List<PrimitiveItem> BuildArrayClones(ArrayInstance arr, int globalZStart)
    {
        var anchors = new List<PlacementP>(64);
        switch (arr.Type)
        {
            case Models.ArrayType.Rectangular:
                BuildRectangularPlacements(arr, anchors);
                break;
            case Models.ArrayType.Polar:
                BuildPolarPlacements(arr, anchors);
                break;
            default:
                BuildPathPlacements(arr, anchors);
                break;
        }
        // 锚 = 源包围盒中心（局部坐标）：渲染端 ContentPresenter RenderTransformOrigin=0.5,0.5，
        // 副本绕自身中心旋转 → 旋转不移动中心，故中心对齐落点即得"锚点恒在落点上"。
        double ox = arr.Source.Width / 2;
        double oy = arr.Source.Height / 2;
        int z = globalZStart + arr.ZIndexOffset;
        var clones = new List<PrimitiveItem>(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            var p = anchors[i];
            var clone = CreateFromDto(arr.Source);
            // 克隆 → 关联到所属阵列（DraggablePrimitive 以此把点击/拖动路由到阵列整体操作）
            clone.ArrayOwner = arr;
            // 第一份 = 阵列源位（本体）：选中时视觉突出，与其余"影"副本区分
            clone.IsArraySourceClone = i == 0;
            // 编辑源模式：源由画布上的编辑源图元顶替，隐藏源位克隆避免两体重叠
            if (i == 0 && _isArraySourceEditMode) clone.IsVisible = false;
            clone.X = p.X - ox + arr.OffsetX;
            clone.Y = p.Y - oy + arr.OffsetY;
            if (arr.Type == Models.ArrayType.Path)
            {
                // 路径阵列：中心沿路径法线再偏移 PathOffset（AutoCAD 基点语义）。
                // 法线 = 切线旋转 +90°（y-down）：n = (-sinθ, cosθ)，与 ProjectOntoPath 的
                // 有符号距离同一约定 → 源所在的那一侧、那个距离，在全部副本上保持不变。
                if (arr.PathOffset != 0)
                {
                    double rad = p.AngleDeg * System.Math.PI / 180.0;
                    clone.X += -System.Math.Sin(rad) * arr.PathOffset;
                    clone.Y += System.Math.Cos(rad) * arr.PathOffset;
                }
                if (arr.PathAlign) clone.Rotation = p.AngleDeg;
            }
            else
            {
                clone.Rotation = p.AngleDeg;
            }
            clone.ZIndex = z;
            clone.IsSelected = false;  // 阵列副本不可作为普通图元被选中（选中走 SelectArray 整体高亮）
            clones.Add(clone);
        }
        return clones;
    }

    /// <summary>单个副本的落点（画布坐标 + 旋转角）。</summary>
    private readonly struct PlacementP
    {
        public PlacementP(double x, double y, double angleDeg)
        {
            X = x;
            Y = y;
            AngleDeg = angleDeg;
        }

        public double X { get; }
        public double Y { get; }
        public double AngleDeg { get; }
    }

    /// <summary>矩形阵列布局：以源包围盒中心为网格原点，列沿 +X（列间距），行沿 +Y（行间距）。
    /// 第一份（0,0）与源完全重合。</summary>
    private static void BuildRectangularPlacements(ArrayInstance arr, List<PlacementP> anchors)
    {
        // 网格原点 = 源包围盒中心
        double originX = arr.Source.X + arr.Source.Width / 2;
        double originY = arr.Source.Y + arr.Source.Height / 2;
        int cols = Math.Clamp(arr.Columns, 1, 100);
        int rows = Math.Clamp(arr.Rows, 1, 100);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
            anchors.Add(new PlacementP(originX + c * arr.ColSpacing, originY + r * arr.RowSpacing, 0));
    }

    /// <summary>环形阵列参考圆环的 PathData（预览/选中编辑时显示分布路径，直观看出副本所在圆周）。
    /// 半径 = 源中心到圆心距离；弧 = 按 FillAngle 的填充扇区；整圆时画完整圆环。
    /// 非 Polar 类型（矩形/路径）保持空串 → XAML 隐藏。确认提交后预览层整体隐藏，本参考圆环随之消失。</summary>
    public string PolarGuideData { get; private set; } = string.Empty;

    /// <summary>是否显示环形参考圆环（仅 Data 非空时显示）。</summary>
    public bool HasPolarGuide => !string.IsNullOrEmpty(PolarGuideData);

    /// <summary>刷新参考圆环 PathData：Polar 类型生成、其它类型清空。</summary>
    private void UpdatePolarGuide(Models.ArrayInstance? arr)
    {
        PolarGuideData = arr is { Type: Models.ArrayType.Polar } ? BuildPolarGuideData(arr) : string.Empty;
        OnPropertyChanged(nameof(PolarGuideData));
        OnPropertyChanged(nameof(HasPolarGuide));
    }

    /// <summary>生成环形参考圆环的 PathData（空串 = 不显示）。</summary>
    private static string BuildPolarGuideData(Models.ArrayInstance arr)
    {
        var (radius, startAngle) = GetPolarGeometry(arr);
        // 圆心跟随阵列整体位移：副本落点带 arr.OffsetX/Y，圆环必须同源平移，否则拖动过的阵列编辑时圆环与副本错位
        double cx = arr.CenterX + arr.OffsetX;
        double cy = arr.CenterY + arr.OffsetY;
        double fill = arr.FillAngle;
        var fmt = System.Globalization.CultureInfo.InvariantCulture;
        // 整圆（|fill|≈360°）：两个 180° 半圆拼整圈（两点定圆即可）
        if (System.Math.Abs(fill) >= 359.999)
        {
            return string.Format(fmt,
                "M {0:0.##},{1:0.##} A {2:0.##},{2:0.##} 0 1 1 {3:0.##},{4:0.##} A {2:0.##},{2:0.##} 0 1 1 {0:0.##},{1:0.##}",
                cx + radius, cy, radius, cx - radius, cy);
        }
        // 填充扇区：从起始角扫到 起始角+填充角；sweep=1（y-down 角度增大方向），|fill|&gt;180 → 大弧
        double a0 = startAngle * System.Math.PI / 180.0;
        double a1 = (startAngle + fill) * System.Math.PI / 180.0;
        double sx = cx + radius * System.Math.Cos(a0);
        double sy = cy + radius * System.Math.Sin(a0);
        double ex = cx + radius * System.Math.Cos(a1);
        double ey = cy + radius * System.Math.Sin(a1);
        int largeArc = System.Math.Abs(fill) > 180.0 ? 1 : 0;
        int sweep = fill >= 0 ? 1 : 0;
        return string.Format(fmt,
            "M {0:0.##},{1:0.##} A {2:0.##},{2:0.##} 0 {3} {4} {5:0.##},{6:0.##}",
            sx, sy, radius, largeArc, sweep, ex, ey);
    }

    /// <summary>环形阵列的圆心几何：半径 = 源中心到圆心距离；起始角 = 源中心相对圆心的方位角。
    /// BuildPolarPlacements（副本落点）与 BuildPolarGuideData（参考圆环）复用同一几何，避免两处漂移。</summary>
    private static (double radius, double startAngle) GetPolarGeometry(Models.ArrayInstance arr)
    {
        double srcCx = arr.Source.X + arr.Source.Width / 2;
        double srcCy = arr.Source.Y + arr.Source.Height / 2;
        double radius = System.Math.Sqrt(
            (srcCx - arr.CenterX) * (srcCx - arr.CenterX) + (srcCy - arr.CenterY) * (srcCy - arr.CenterY));
        if (radius < 0.01) radius = System.Math.Max(arr.Source.Width, arr.Source.Height); // 防零
        double startAngle = System.Math.Atan2(srcCy - arr.CenterY, srcCx - arr.CenterX) * 180.0 / System.Math.PI;
        return (radius, startAngle);
    }

    /// <summary>环形阵列布局：绕圆心(CenterX, CenterY) 分布，半径 = 源中心到圆心距离。
    /// 起始角 = 源中心相对圆心的方位角；FillAngle=360 → N 等分整圆；否则在填充角内 N 等分。
    /// RotateItems=true 时副本旋转到圆周切向（径向 +90°）。</summary>
    private static void BuildPolarPlacements(ArrayInstance arr, List<PlacementP> anchors)
    {
        var (radius, startAngle) = GetPolarGeometry(arr);
        int n = System.Math.Clamp(arr.Count, 1, 100);
        double fill = arr.FillAngle;
        // 360° → n 份等距（首份在起始角）；否则填充角内 n 份，步进 = fill/(n-1)
        double step = n <= 1 ? 0 : (System.Math.Abs(fill) >= 359.999 ? fill / n : fill / (n - 1));
        for (int i = 0; i < n; i++)
        {
            double angleDeg = startAngle + i * step;
            double rad = angleDeg * System.Math.PI / 180.0;
            double px = arr.CenterX + radius * System.Math.Cos(rad);
            double py = arr.CenterY + radius * System.Math.Sin(rad);
            double rot = arr.RotateItems ? angleDeg + 90 : 0; // 径向 +90° = 圆周切向
            anchors.Add(new PlacementP(px, py, rot));
        }
    }

    /// <summary>路径阵列布局：以"源中心在路径上的投影点"为起点（即"从源当前位置开始"），
    /// 沿路径等弧长推进，副本中心在 BuildArrayClones 里再沿法线偏移 PathOffset。
    /// 路径走"几何 → 最长轮廓 → 摊平直线段"通用入口，故圆轮廓 / 履带 / 布尔结果都能当路径。
    /// 注意：这里给的是**路径落点**（未含 PathOffset），PathOffset 在装配克隆时统一施加。</summary>
    private static void BuildPathPlacements(ArrayInstance arr, List<PlacementP> anchors)
    {
        var segments = Converters.PolylineToSegments.Convert(ParsePathData(arr.PathData));
        if (segments.Count == 0) return;

        // 起始弧长：源中心到路径的最近投影点（= 源当前位置在路径上的落点）
        var proj = Converters.PathDistributor.ProjectOntoPath(
            segments, new System.Windows.Point(
                arr.Source.X + arr.Source.Width / 2,
                arr.Source.Y + arr.Source.Height / 2));
        double startArc = proj?.ArcLength ?? 0;

        // 闭合路径（末点回到首点）→ 整圈等分并回绕；开放路径 → 从起点排到终点
        bool closed = IsClosedContour(segments);
        foreach (var p in Converters.PathDistributor.DistributeAlongPath(
                     segments, System.Math.Clamp(arr.Count, 1, 100), startArc, closed))
            anchors.Add(new PlacementP(p.X, p.Y, p.AngleDeg));
    }

    /// <summary>段序列是否闭合（末段终点 ≈ 首段起点；PolylineToSegments 对闭合轮廓补了收尾边）。</summary>
    private static bool IsClosedContour(IReadOnlyList<Converters.PathDistributor.PathSegment> segments)
    {
        if (segments.Count < 2) return false;
        var a = segments[0].Start;
        var b = segments[segments.Count - 1].End;
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy < 1e-9;
    }

    /// <summary>图元 → 画布坐标几何（保留弧/贝塞尔，不提前摊平）。
    /// 走**通用几何入口**：PrimitiveItem.SelectionGeometry 已按类型分发、覆盖全部图元
    /// （矩形/圆角/椭圆/三角/圆环/扇形/直线/折线(开+闭)/带状折线/布尔/履带），
    /// 只是它是**局部坐标**（相对自身 X/Y），这里把各段整体平移到画布坐标。
    /// 以后新增图元只要 SelectionGeometry 里有它，路径阵列自动支持 —— 不在此加类型分支。
    /// 不用"几何挂 Transform + 收口 PathGeometry"的写法：实测该组合对 RectangleGeometry /
    /// EllipseGeometry **不会**把变换烘进坐标（仍输出 M0,0），只有 GeometryGroup 会 —— 行为不一致。
    /// 逐段平移与几何类型无关，结果确定。几何为空 / 序列化为空串 → null（视为"不可作路径"）。</summary>
    private static System.Windows.Media.Geometry? CanvasGeometry(PrimitiveItem? item)
    {
        if (item?.SelectionGeometry is not { } local) return null;
        // CreateFromGeometry 对"已是 PathGeometry"的输入会**原样返回同一实例**（履带 / 带状折线 /
        // 布尔的轮廓几何是冻结缓存）→ 直接改点会抛"不能修改冻结几何"。故这一类先克隆。
        var pg = local is System.Windows.Media.PathGeometry
            ? (System.Windows.Media.PathGeometry)local.Clone()
            : System.Windows.Media.PathGeometry.CreateFromGeometry(local);
        var offset = new System.Windows.Vector(item.X, item.Y);
        if (offset.X != 0 || offset.Y != 0)
        {
            foreach (var figure in pg.Figures)
            {
                figure.StartPoint += offset;
                foreach (var segment in figure.Segments) ShiftSegment(segment, offset);
            }
        }
        // 空几何（如宽高为 0 的图元）序列化为 "" → 视为"不可作路径"，避免建出 0 份副本的空阵列。
        return string.IsNullOrWhiteSpace(pg.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ? null
            : pg;
    }

    /// <summary>把一段路径段整体平移（保留段类型，不摊平）。</summary>
    private static void ShiftSegment(System.Windows.Media.PathSegment segment, System.Windows.Vector offset)
    {
        switch (segment)
        {
            case System.Windows.Media.LineSegment line:
                line.Point += offset; break;
            case System.Windows.Media.PolyLineSegment poly:
                for (int i = 0; i < poly.Points.Count; i++) poly.Points[i] += offset;
                break;
            case System.Windows.Media.ArcSegment arc:
                arc.Point += offset; break;
            case System.Windows.Media.BezierSegment bezier:
                bezier.Point1 += offset; bezier.Point2 += offset; bezier.Point3 += offset; break;
            case System.Windows.Media.QuadraticBezierSegment quad:
                quad.Point1 += offset; quad.Point2 += offset; break;
            case System.Windows.Media.PolyBezierSegment polyBezier:
                for (int i = 0; i < polyBezier.Points.Count; i++) polyBezier.Points[i] += offset;
                break;
            case System.Windows.Media.PolyQuadraticBezierSegment polyQuad:
                for (int i = 0; i < polyQuad.Points.Count; i++) polyQuad.Points[i] += offset;
                break;
        }
    }

    /// <summary>路径 mini-language → 几何；空串 / 非法串 → null（调用方走"无可用路径"分支）。</summary>
    private static System.Windows.Media.Geometry? ParsePathData(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData)) return null;
        try
        {
            return System.Windows.Media.Geometry.Parse(pathData);
        }
        catch (System.FormatException)
        {
            return null;
        }
    }
}