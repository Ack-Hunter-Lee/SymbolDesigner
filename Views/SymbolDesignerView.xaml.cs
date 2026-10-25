using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SymbolDesigner.Controls;
using SymbolDesigner.Geometry;
using SymbolDesigner.Models;
using SymbolDesigner.ViewModels;

namespace SymbolDesigner.Views;

/// <summary>
/// 元件设计器视图。Code-behind 处理：
/// - 工具栏拖拽到画布
/// - 画布上拖动改位置（由 DraggablePrimitive 的 Thumb 处理）
/// - 点击画布上的图形 → 选中（区分拖动和点击）
/// - 删除选中 / 全部清空
/// 画布的缩放/平移/自动适配由 ZoomPanViewport 控件内置处理（见 Controls/ZoomPanViewport.cs）。
/// </summary>
public partial class SymbolDesignerView : UserControl
{
    public SymbolDesignerView()
    {
        InitializeComponent();
        // DataContext 变化时挂 VM 的 PropertyChanged → 监听 SelectedOutlineCorner 切换 → 同步圆角半径 Slider
        DataContextChanged += (s, e) =>
        {
            if (e.OldValue is ViewModels.SymbolDesignerViewModel oldVm)
            {
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
                oldVm.RequestLocate -= OnRequestLocate;
            }
            if (e.NewValue is ViewModels.SymbolDesignerViewModel newVm)
            {
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                newVm.RequestLocate += OnRequestLocate;
            }
        };
        // ZoomPanViewport.CurrentScale → VM.CanvasScale 同步（预览线/端点"视觉恒定反缩放"）。
        // 不能用 Style Setter 的 ElementName 绑定，这里用 DP 变更回调桥接。
        // 缩放、平移 PanX/Y、视口尺寸任一变化都要重算状态栏（含真实左/上偏移），所以统一走 SyncCanvasScale。
        _scaleDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Controls.ZoomPanViewport.CurrentScaleProperty, typeof(Controls.ZoomPanViewport));
        _scaleDescriptor.AddValueChanged(ZoomPan, OnTransformChanged);
        _panXDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Controls.ZoomPanViewport.PanXProperty, typeof(Controls.ZoomPanViewport));
        _panXDescriptor.AddValueChanged(ZoomPan, OnTransformChanged);
        _panYDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Controls.ZoomPanViewport.PanYProperty, typeof(Controls.ZoomPanViewport));
        _panYDescriptor.AddValueChanged(ZoomPan, OnTransformChanged);
        ZoomPan.SizeChanged += (_, _) => SyncCanvasScale();  // 视口尺寸变化（窗口缩放/抽屉开合）
        Loaded += (_, _) => SyncCanvasScale();  // 首帧布局后把初始 scale 同步过去
    }

    private readonly System.ComponentModel.DependencyPropertyDescriptor _scaleDescriptor;
    private readonly System.ComponentModel.DependencyPropertyDescriptor _panXDescriptor;
    private readonly System.ComponentModel.DependencyPropertyDescriptor _panYDescriptor;

    private void OnTransformChanged(object? sender, EventArgs e) => SyncCanvasScale();

    /// <summary>把 ZoomPanViewport 状态同步到 VM（CanvasScale + 状态栏字段）。
    /// 任何缩放/平移/窗口尺寸变化都通过 CurrentScale 回调触发，状态栏保持实时。</summary>
    private void SyncCanvasScale()
    {
        if (ZoomPan is not Controls.ZoomPanViewport zp ||
            DataContext is not ViewModels.SymbolDesignerViewModel vm) return;

        double s = zp.CurrentScale;
        if (Math.Abs(s - vm.CanvasScale) > 1e-9) vm.CanvasScale = s;

        // 视口尺寸（availW/availH）
        if (Math.Abs(zp.ActualWidth - vm.ViewportWidth) > 0.5) vm.ViewportWidth = zp.ActualWidth;
        if (Math.Abs(zp.ActualHeight - vm.ViewportHeight) > 0.5) vm.ViewportHeight = zp.ActualHeight;

        // 画布渲染尺寸（cw*s × ch*s）
        double cw = zp.ContentWidth;
        double ch = zp.ContentHeight;
        double renderW = cw * s;
        double renderH = ch * s;
        if (Math.Abs(renderW - vm.RenderWidth) > 0.5) vm.RenderWidth = renderW;
        if (Math.Abs(renderH - vm.RenderHeight) > 0.5) vm.RenderHeight = renderH;

        // 画布真实左/上偏移 = 自动居中偏移( (availW-cw*s)/2 ) + 用户平移( PanX/PanY )
        // 这就是"画布在视口内的左边距/上边距"：打开即显示真实位置（居中时四周留白，值非 0）。
        double cx = (zp.ActualWidth - cw * s) * 0.5;
        double cy = (zp.ActualHeight - ch * s) * 0.5;
        double left = cx + zp.PanX;
        double top = cy + zp.PanY;
        if (Math.Abs(left - vm.CanvasLeft) > 0.5) vm.CanvasLeft = left;
        if (Math.Abs(top - vm.CanvasTop) > 0.5) vm.CanvasTop = top;

        // 鼠标画布逻辑坐标（如有已记录的屏幕坐标，这里再算一遍）
        UpdateCursorFromLastScreen();
    }

    private System.Windows.Point _lastCanvasScreen;
    private bool _hasLastCursor;

    /// <summary>在 CanvasHost.MouseMove 中调用，记录鼠标屏幕位置；同时计算并同步到 VM 的 CursorX/Y。</summary>
    public void OnCanvasCursorMove(System.Windows.Point screenPos)
    {
        _lastCanvasScreen = screenPos;
        _hasLastCursor = true;
        UpdateCursorFromLastScreen();
    }

    /// <summary>用最新的屏幕坐标和当前 ZoomPan 状态算出画布逻辑坐标，写到 VM。
    /// 公式：logicX = (screenX - cx - PanX) / scale，cx = (availW - cw*scale) * 0.5。</summary>
    private void UpdateCursorFromLastScreen()
    {
        if (!_hasLastCursor) return;
        if (ZoomPan is not Controls.ZoomPanViewport zp ||
            DataContext is not ViewModels.SymbolDesignerViewModel vm) return;

        double availW = zp.ActualWidth;
        double availH = zp.ActualHeight;
        double cw = zp.ContentWidth;
        double ch = zp.ContentHeight;
        double scale = zp.CurrentScale;
        if (availW <= 0 || availH <= 0 || cw <= 0 || ch <= 0 || scale <= 1e-9) return;

        double cx = (availW - cw * scale) * 0.5;
        double cy = (availH - ch * scale) * 0.5;
        double lx = (_lastCanvasScreen.X - cx - zp.PanX) / scale;
        double ly = (_lastCanvasScreen.Y - cy - zp.PanY) / scale;
        if (Math.Abs(lx - (vm.CursorX ?? double.NaN)) > 0.5) vm.CursorX = lx;
        if (Math.Abs(ly - (vm.CursorY ?? double.NaN)) > 0.5) vm.CursorY = ly;
    }

    /// <summary>VM 触发"快速定位"事件 → 把视口中心平移到该元素画布中心。
    /// 元素画布中心 = (X + Width/2, Y + Height/2)（与画布坐标系一致；PathData 偏移不参与包围盒计算）。</summary>
    private void OnRequestLocate(Models.PrimitiveItem item)
    {
        if (ZoomPan is ZoomPanViewport zp)
        {
            double cx = item.X + item.Width * 0.5;
            double cy = item.Y + item.Height * 0.5;
            zp.CenterOnContentPoint(cx, cy);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 选中接缝变化时 → 同步接缝面板的"圆角半径"Slider 数值
        if (e.PropertyName == nameof(ViewModels.SymbolDesignerViewModel.SelectedOutlineCorner))
            SyncCornerRadiusSlider();
    }

    /// <summary>工具栏图标被点击：所有工具一律走 SelectTool 进入 DrawMode（参数即该工具的 TypeKey）。</summary>
    private void OnToolboxItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ToolboxEntry entry) return;
        if (e.ChangedButton != MouseButton.Left) return;

        if (DataContext is SymbolDesignerViewModel vm)
            vm.SelectToolCommand.Execute(entry.TypeKey);

        e.Handled = true;
    }

    /// <summary>
    /// 画布空白处点击：取消选中。
    /// DraggablePrimitive 自身处理点击/拖动区分（见 DraggablePrimitive.OnSelfClick）。
    /// </summary>
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点击画布空白处 → 取消全部选中（图元 / 多选 / 阵列整体）
        if (DataContext is SymbolDesignerViewModel vm && e.OriginalSource == sender)
        {
            vm.ClearSelection();
        }
    }

    /// <summary>抽屉把手勾选 → 展开属性面板（宽度滑到 240）。
    /// 用 BeginAnimation 而非 DataTrigger Storyboard：后者多轮交替时 HoldEnd 残留会导致"缩得回、展不开"。</summary>
    private void OnRightPaneToggleChecked(object sender, RoutedEventArgs e)
        => AnimateRightPaneWidth(240, isOpening: true);

    /// <summary>抽屉把手取消勾选 → 收起属性面板（宽度收到 0，内容被 ClipToBounds 裁切）。</summary>
    private void OnRightPaneToggleUnchecked(object sender, RoutedEventArgs e)
        => AnimateRightPaneWidth(0, isOpening: false);

    /// <summary>属性面板抽屉宽度动画（240↔0，0.25s 缓动）。动画结束后 FillBehavior.HoldEnd 保持目标值，
    /// 下次 BeginAnimation 会从当前值续动，不会出现值被锁死。
    /// 用 FindName 取得面板 Border：某些编译环境下 VS 生成的 .g.cs 不产出 x:Name 字段
    /// （导致 code-behind 里直接引用字段名报 CS0103），运行时 FindName 按 namescope 查找则无论
    /// 哪种编译路径都可靠，且事件触发时 InitializeComponent 早已执行、namescope 完整。</summary>
    private void AnimateRightPaneWidth(double to, bool isOpening)
    {
        if (FindName("RightPaneBorder") is not Border pane) return;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            to, System.TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = isOpening
                    ? System.Windows.Media.Animation.EasingMode.EaseOut
                    : System.Windows.Media.Animation.EasingMode.EaseIn,
            },
        };
        pane.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    /// <summary>点击"撤销"按钮 → VM 命令。</summary>
    private void OnUndoClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.UndoCommand.Execute(null);
    }

    /// <summary>点击"重做"按钮 → VM 命令。</summary>
    private void OnRedoClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.RedoCommand.Execute(null);
    }

    /// <summary>点击"删除选中"按钮 → VM 命令。</summary>
    private void OnDeleteSelectedClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.DeleteSelectedCommand.Execute(null);
    }

    /// <summary>点击"全部清空"按钮 → VM 命令。</summary>
    private void OnClearAllClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.ClearAllCommand.Execute(null);
    }

    /// <summary>点击"水平镜像"按钮 → 围绕选中集包围盒水平中心翻转。</summary>
    private void OnMirrorHorizontalClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.MirrorHorizontalCommand.Execute(null);
    }

    /// <summary>点击"垂直镜像"按钮 → 围绕选中集包围盒垂直中心翻转。</summary>
    private void OnMirrorVerticalClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.MirrorVerticalCommand.Execute(null);
    }

    /// <summary>点击"格式刷"按钮 → 吸取选中图元的样式进入"应用待命"态；
    /// 已激活时再点 = 退出；Ctrl+点击 = 进入连刷模式（应用后保持激活）。
    /// 用 MouseLeftButtonDown 而非 Up 是为了拦截时 ModifierKeys 还在按下（与 Ctrl 修饰键检测一致）。
    /// CanPickFormat 未通过时 IsEnabled=false → 按钮不接收事件，handler 不会被调到。</summary>
    private void OnFormatPainterClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        bool ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        if (ctrl)
            vm.EnterFormatPainterLocked();
        else
            vm.PickFormatPainterCommand.Execute(null);
    }

    /// <summary>点击"相切约束"按钮 → 对多选中的直线 + 圆建立持久相切。
    /// 前置：用户 Ctrl+点击已选中一条直线和一个圆（CanApplyTangent=true 时按钮才可点）。</summary>
    private void OnApplyTangentClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.ApplyTangentCommand.Execute(null);
    }

    /// <summary>点击"重合约束"按钮 → 对多选中的直线 + 圆智能建立持久重合约束。
    /// 前置：用户 Ctrl+点击已选中一条直线和一个圆（CanApplyCoincident=true 时按钮才可点）。</summary>
    private void OnApplyCoincidentClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.ApplyCoincidentCommand.Execute(null);
    }

    /// <summary>点击"布尔-合并"按钮 → 两个闭合图元求并集。</summary>
    private void OnMergeShapesClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.MergeShapesCommand.Execute(null);
    }

    /// <summary>点击"布尔-剪除"按钮 → 先选图元减后选图元（A ∖ B）。</summary>
    private void OnSubtractShapesClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.SubtractShapesCommand.Execute(null);
    }

    /// <summary>点击"布尔-相交"按钮 → 两个闭合图元交集。</summary>
    private void OnIntersectShapesClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.IntersectShapesCommand.Execute(null);
    }

    /// <summary>点击"置顶"按钮 → VM 命令。</summary>
    private void OnBringToFrontClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.BringToFrontCommand.Execute(null);
    }

    /// <summary>点击"上移一层"按钮 → VM 命令。</summary>
    private void OnMoveUpClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.MoveUpCommand.Execute(null);
    }

    /// <summary>点击"下移一层"按钮 → VM 命令。</summary>
    private void OnMoveDownClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.MoveDownCommand.Execute(null);
    }

    /// <summary>点击"置底"按钮 → VM 命令。</summary>
    private void OnSendToBackClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.SendToBackCommand.Execute(null);
    }

    /// <summary>点击"导出 XAML"按钮 → 打开导出弹窗。</summary>
    private void OnExportXamlClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.OpenExportDialogCommand.Execute(null);
    }

    /// <summary>点击"阵列"按钮 → 开始预览式创建（VM 内部校验源并就地生成预览，类型可在浮层切换）。
    /// 校验失败由 VM 弹 MessageBox 告知原因，不在此层重复处理。</summary>
    private void OnPathArrayClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.StartArrayPreviewCommand.Execute(null);
    }

    /// <summary>点击预览浮层的取消按钮 → 取消预览（不创建阵列）。</summary>
    private void OnPathArrayDialogCloseClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.CancelArrayPreviewCommand.Execute(null);
    }

    /// <summary>点击"取消"按钮 → 取消预览。</summary>
    private void OnPathArrayCancelClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.CancelArrayPreviewCommand.Execute(null);
    }

    /// <summary>点击"创建阵列"按钮 → 提交预览参数，VM 加入 ArrayInstances 并退出预览。</summary>
    private void OnPathArrayConfirmClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.ConfirmArrayPreviewCommand.Execute(null);
    }

    /// <summary>点击"编辑源"按钮（C 档）→ 把选中阵列的源借出为画布可编辑图元，副本实时跟随。</summary>
    private void OnArraySourceEditClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.EnterArraySourceEditCommand.Execute(null);
    }

    /// <summary>保存工程按钮：有 CurrentFilePath 直接覆盖，否则转另存为。</summary>
    private void OnSaveProjectClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.SaveProjectCommand.Execute(null);
    }

    /// <summary>另存为按钮：总是弹窗选路径。</summary>
    private void OnSaveAsProjectClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.SaveAsProjectCommand.Execute(null);
    }

    /// <summary>加载工程按钮：从 .symz 容器 / .sym.json 纯文本工程恢复编辑状态。</summary>
    private void OnLoadProjectClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.LoadProjectCommand.Execute(null);
    }

    /// <summary>导入 SVG 按钮：弹文件选择框 → 调 VM.ImportSvgCommand 解析入画布。
    /// MVP 范围:仅基本几何元素(矩形/圆/椭圆/线/折线/路径),曲线自动降采样。
    /// 不支持(留作后续迭代):渐变/滤镜/图案/文字/图片。</summary>
    private void OnImportSvgClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 SVG 文件",
            Filter = "SVG 文件 (*.svg)|*.svg|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        vm.ImportSvgCommand.Execute(dlg.FileName);
    }

    // ============== 背景参考图 ==============

    /// <summary>导入背景图：弹文件选择框 → 调用 VM.LoadBackgroundImage。</summary>
    private void OnImportBackgroundImageClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景参考图",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        vm.LoadBackgroundImage(dlg.FileName);
    }

    /// <summary>清除背景图按钮（无背景图时按钮变灰，但点击仍走 VM 逻辑 → 幂等 no-op）。</summary>
    private void OnClearBackgroundImageClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.ClearBackgroundImageCommand.Execute(null);
    }

    /// <summary>点击弹窗关闭按钮 / 半透明遮罩 → 关闭弹窗。</summary>
    private void OnExportDialogCloseClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.IsExportDialogOpen = false;
    }

    /// <summary>点击"复制到剪贴板"按钮：复制后自动关闭导出弹窗。</summary>
    private void OnCopyXamlClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
        {
            vm.CopyXamlToClipboardCommand.Execute(null);
            vm.IsExportDialogOpen = false; // 复制完成 → 关闭导出浮层
        }
    }

    /// <summary>点击"另存为 .xaml"按钮。</summary>
    private void OnSaveXamlClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm)
            vm.SaveXamlToFileCommand.Execute(null);
    }

    /// <summary>沿视觉树向上查找指定类型的祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T t) return t;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>元素面板拖放排序的数据格式标记（仅同进程内使用）。</summary>
    private const string ElementDragFormat = "SymbolDesigner.ElementRow";

    /// <summary>按下时记录的行图元（尚未达到拖拽阈值），null = 当前没有可拖拽的行。</summary>
    private PrimitiveItem? _elementDragCandidate;

    /// <summary>按下位置（元素面板坐标），用于判断是否超过系统拖拽阈值。</summary>
    private Point _elementDragOrigin;

    /// <summary>元素面板按下：记录可拖拽的行；点在行内操作按钮（睁眼/定位/删除）上时不启动拖拽。
    /// 这些按钮都用 InputBindings.MouseBinding 处理点击，据此识别，避免拖拽抢掉按钮。</summary>
    private void OnElementsListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _elementDragCandidate = null;
        if (e.OriginalSource is not DependencyObject src) return;
        for (var d = src; d is not null && d is not ListBoxItem; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement el && el.InputBindings.Count > 0) return;
        }
        if (FindAncestor<ListBoxItem>(src)?.DataContext is not PrimitiveItem item) return;
        _elementDragCandidate = item;
        _elementDragOrigin = e.GetPosition(ElementsList);
    }

    /// <summary>元素面板拖动：超过系统拖拽阈值才发起拖放，避免普通点击被当成拖拽。</summary>
    private void OnElementsListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_elementDragCandidate is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(ElementsList);
        if (Math.Abs(p.X - _elementDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _elementDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _elementDragCandidate = null; // 一次按下只发起一次拖放
        DragDrop.DoDragDrop(ElementsList, new DataObject(ElementDragFormat, item), DragDropEffects.Move);
    }

    private void OnElementsListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ElementDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>元素面板放下：把拖动行移到目标行所在位置（列表顺序 = ZIndex 顺序）。
    /// 目标行 DataContext 即要插入的索引；落在空白区域时移到最前（列表末行 = 最前层）。</summary>
    private void OnElementsListDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ElementDragFormat) ||
            e.Data.GetData(ElementDragFormat) is not PrimitiveItem item) return;
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PrimitiveItem;
        var targetIndex = target is null ? vm.Primitives.Count - 1 : vm.Primitives.IndexOf(target);
        vm.MovePrimitiveTo(item, targetIndex);
        e.Handled = true;
    }

    /// <summary>缩放按钮（+）：以画布中心为锚点放大（ZoomPanViewport 内置）。</summary>
    private void OnZoomInClick(object sender, MouseButtonEventArgs e)
    {
        if (ZoomPan is ZoomPanViewport zp) zp.ZoomToCenter(zp.ZoomLevel * 1.25);
    }

    /// <summary>缩放按钮（-）：以画布中心为锚点缩小（ZoomPanViewport 内置）。</summary>
    private void OnZoomOutClick(object sender, MouseButtonEventArgs e)
    {
        if (ZoomPan is ZoomPanViewport zp) zp.ZoomToCenter(zp.ZoomLevel * 0.8);
    }

    /// <summary>重置按钮：复位缩放与平移（ZoomPanViewport 内置）。</summary>
    private void OnZoomResetClick(object sender, MouseButtonEventArgs e)
    {
        if (ZoomPan is ZoomPanViewport zp) zp.ResetView();
    }

    // ───────────── 画布空白区左键拖拽平移（普通模式） ─────────────

    /// <summary>是否正在拖动画布空白处平移视图。</summary>
    private bool _isCanvasPanning;

    /// <summary>平移上一帧的鼠标屏幕位置（相对 ZoomPanViewport，用于累加 PanX/PanY）。</summary>
    private Point _panLastPos;

    // ───────────── 右键框选（Marquee 多选 + 选区缩放手柄） ─────────────

    /// <summary>是否正在右键拖拽框选（显示选区矩形,松开时把框内图元加入多选）。</summary>
    private bool _isMarqueeSelecting;

    /// <summary>框选起始点（画布逻辑坐标）。</summary>
    private Point _marqueeStart;

    /// <summary>右键按下：普通模式下开始框选（记录起始点 + 显示选区矩形 + 捕获鼠标）。
    /// 绘制模式下不启动（右键保留给绘制语义）。</summary>
    private void OnCanvasMarqueeDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        if (DataContext is not SymbolDesignerViewModel vm) return;
        // 绘制模式下右键不框选（CAD 语义：绘制中右键 = 撤销/结束,不抢）
        if (vm.IsDrawModeActive || IsEditingOutlineActive()) return;

        _isMarqueeSelecting = true;
        _marqueeStart = e.GetPosition(CanvasHost);
        MarqueeRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(MarqueeRect, _marqueeStart.X);
        Canvas.SetTop(MarqueeRect, _marqueeStart.Y);
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>右键移动：更新选区矩形（允许负尺寸拖动 = 四方向框选,用规范化矩形）。</summary>
    private void OnCanvasMarqueeMove(object sender, MouseEventArgs e)
    {
        if (!_isMarqueeSelecting) return;
        var cur = e.GetPosition(CanvasHost);
        double x = Math.Min(_marqueeStart.X, cur.X);
        double y = Math.Min(_marqueeStart.Y, cur.Y);
        double w = Math.Abs(cur.X - _marqueeStart.X);
        double h = Math.Abs(cur.Y - _marqueeStart.Y);
        Canvas.SetLeft(MarqueeRect, x);
        Canvas.SetTop(MarqueeRect, y);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;
        e.Handled = true;
    }

    /// <summary>右键松开：隐藏选区矩形,把框内图元加入多选（VM.SelectInRect）。</summary>
    private void OnCanvasMarqueeUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMarqueeSelecting) return;
        _isMarqueeSelecting = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var cur = e.GetPosition(CanvasHost);
        double x = Math.Min(_marqueeStart.X, cur.X);
        double y = Math.Min(_marqueeStart.Y, cur.Y);
        double w = Math.Abs(cur.X - _marqueeStart.X);
        double h = Math.Abs(cur.Y - _marqueeStart.Y);
        if (w < 0.5 || h < 0.5) { e.Handled = true; return; }  // 纯点击不框选

        vm.SelectInRect(new Rect(x, y, w, h));
        e.Handled = true;
    }

    /// <summary>是否处于轮廓编辑模式（骨架/带宽/轮廓三态之一都算编辑中,框选应让位）。</summary>
    private bool IsEditingOutlineActive()
        => DataContext is SymbolDesignerViewModel vm
           && (vm.OutlineEditMode == ViewModels.OutlineEditMode.BandWidth
               || vm.OutlineEditMode == ViewModels.OutlineEditMode.Outline);

    // ───────────── 选区缩放手柄（四角拖动 = 围绕对角锚点等比缩放选集） ─────────────

    /// <summary>当前是否正在拖拽缩放手柄。</summary>
    private bool _isScalingSelection;

    /// <summary>框选手柄拖拽起点（画布逻辑坐标,需配合对角线锚点算比例）。</summary>
    private Point _scaleHandleStart;

    /// <summary>缩放手柄按下：调 VM 记录手势快照（一次撤销）,保存本次鼠标起点用于比例计算。</summary>
    private void OnScaleHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not ViewModels.ScaleHandleInfo h) return;

        // 对角锚点:与拖动的角相对的角（画布逻辑坐标,从当前选区包围盒取）
        var frame = vm.ScaleSelectionFrame;
        var anchor = h.Corner switch
        {
            ViewModels.ScaleCorner.NW => new Point(frame.Right, frame.Bottom),
            ViewModels.ScaleCorner.NE => new Point(frame.Left, frame.Bottom),
            ViewModels.ScaleCorner.SW => new Point(frame.Right, frame.Top),
            ViewModels.ScaleCorner.SE => new Point(frame.Left, frame.Top),
            _ => new Point(frame.Left, frame.Top),
        };
        _isScalingSelection = true;
        _scaleHandleStart = e.GetPosition(CanvasHost);
        vm.BeginScaleSelection(h.Corner, anchor, _scaleHandleStart);
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>缩放手柄移动：把当前鼠标位置交给 VM,VM 按"到锚点距离比例"实时缩放选集。</summary>
    private void OnScaleHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isScalingSelection) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _isScalingSelection = false;
            return;
        }
        if (DataContext is SymbolDesignerViewModel vm)
            vm.UpdateScaleSelection(e.GetPosition(CanvasHost));
        e.Handled = true;
    }

    /// <summary>缩放手柄松开：结束手势,VM 提交一次撤销。</summary>
    private void OnScaleHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScalingSelection) return;
        _isScalingSelection = false;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        if (DataContext is SymbolDesignerViewModel vm)
            vm.EndScaleSelection();
        e.Handled = true;
    }

    // ───────────── 画线工具（CAD 风格连续绘制） ─────────────

    /// <summary>画布鼠标按下：CAD 模式下点击加点（BandPolyline 或 Line）；
    /// 普通模式下点击画布空白处（OriginalSource == CanvasHost 本身）→ 取消选中 + 启动左键拖拽平移。</summary>
    private void OnCanvasDrawMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;

        if (vm.DrawMode == Models.DrawMode.None)
        {
            // 普通模式：点击图形由 DraggablePrimitive 处理（OriginalSource 是图形内元素）；
            // 只有点中 CanvasHost 自身（空白区）才取消选中并进入平移。
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource == sender)
            {
                // 格式刷"应用待命" → 点击空白 = 退出（Office 行为：Esc/空白均取消，不应用）
                if (vm.IsFormatPainterActive)
                    vm.ClearFormatPainter();
                vm.ClearSelection();
                _isCanvasPanning = true;
                _panLastPos = e.GetPosition(ZoomPan);
                if (sender is UIElement ui) ui.CaptureMouse();
                e.Handled = true;
            }
            return;
        }

        var pos = e.GetPosition(CanvasHost);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (vm.ClickDraw(pos.X, pos.Y, shift))
            e.Handled = true;
    }

    /// <summary>画布鼠标移动：平移中累加 PanX/PanY（屏幕像素增量，与 ZoomPanViewport 中键平移一致）；
    /// 否则画线模式下更新橡皮线终点（实时预览下一段）。右键框选中则更新选区矩形。</summary>
    private void OnCanvasDrawMouseMove(object sender, MouseEventArgs e)
    {
        // 状态栏光标位置：把鼠标在画布坐标系下的逻辑坐标写到 VM（任何时候都更新，不影响主流程）
        if (sender is System.Windows.IInputElement ie && ZoomPan is not null)
            OnCanvasCursorMove(e.GetPosition(ZoomPan));

        // 右键框选中：优先更新选区矩形（不经过左键平移/画线分支）
        if (_isMarqueeSelecting)
        {
            OnCanvasMarqueeMove(sender, e);
            return;
        }

        if (_isCanvasPanning)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isCanvasPanning = false;
                if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
                return;
            }
            if (DataContext is SymbolDesignerViewModel vm)
            {
                var pos = e.GetPosition(ZoomPan);
                vm.PanX += pos.X - _panLastPos.X;
                vm.PanY += pos.Y - _panLastPos.Y;
                _panLastPos = pos;
                e.Handled = true;
            }
            return;
        }

        if (DataContext is not SymbolDesignerViewModel vm2) return;
        if (!vm2.IsDrawing) return;
        var p = e.GetPosition(CanvasHost);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        vm2.MoveDraw(p.X, p.Y, shift);
    }

    /// <summary>画布鼠标松开：结束平移（若在平移中）；CAD 模式下不结束绘制（Esc 才结束）。</summary>
    private void OnCanvasDrawMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCanvasPanning)
        {
            _isCanvasPanning = false;
            if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (vm.DrawMode == Models.DrawMode.None) return;
        // 不结束绘制：松开只是结束一次点击，并不退出连续模式。
        e.Handled = true;
    }

    // ───────────── 顶点手柄拖拽（BandPolyline 选中后编辑长度） ─────────────

    private VertexHandle? _draggingVertex;
    private Point _vertexDragStart;

    /// <summary>吸附闭合的判定阈值（画布逻辑坐标）。末端顶点距起点小于该值 → 自动吸附并闭合 Polyline。
    /// 视觉对应：手柄的画布逻辑尺寸 × 当前 RenderTransform 缩放 = 屏幕像素。
    /// 与是否"经 Viewbox"无关——画布缩放完全走 ZoomPanViewport 的 RenderTransform。</summary>
    private const double CloseSnapThreshold = 3.0;

    /// <summary>顶点手柄鼠标按下：记录拖拽起点 + 捕获鼠标，避免移动时丢事件。</summary>
    private void OnVertexMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        // 事件挂在 ItemsControl 上（Preview 版本，tunneling），OriginalSource 是手柄 Rectangle
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not VertexHandle vh) return;

        // 带宽模式下：点击骨点 → 高亮属性面板对应骨点的 Slider 组，不进入拖拽。
        if (vm.IsBandWidthEditMode)
        {
            vm.ToggleBonePointSelectionCommand.Execute(vh.Index);
            e.Handled = true;
            return;
        }

        _draggingVertex = vh;
        _vertexDragStart = e.GetPosition(CanvasHost);
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>顶点手柄鼠标移动：累计 delta，更新 VertexHandle.X/Y（TwoWay 绑定使手柄跟随鼠标），
    /// 同步到 ViewModel.UpdateVertexPosition 改写 PathData → MultiBinding 重新计算轮廓。
    /// AxisSnapEnabled=true 时强制正交约束：拖拽中间顶点 i 时，与前一顶点 i-1 的连线保持水平或竖直
    /// （取 dx/dy 较大方向），避免折线被拖歪。首顶点（i=0）无前驱，不约束。</summary>
    private void OnVertexMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingVertex is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingVertex = null;
            return;
        }
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var pos = e.GetPosition(CanvasHost);
        var dx = pos.X - _vertexDragStart.X;
        var dy = pos.Y - _vertexDragStart.Y;
        if (System.Math.Abs(dx) < 0.3 && System.Math.Abs(dy) < 0.3) return;

        // 正交约束：AxisSnapEnabled=true 且有前驱顶点时，约束顶点 i 与 i-1 的连线为水平或竖直
        if (vm.AxisSnapEnabled
            && _draggingVertex.Index > 0
            && _draggingVertex.Index - 1 < vm.VertexHandles.Count)
        {
            var anchor = vm.VertexHandles[_draggingVertex.Index - 1];
            // 以鼠标当前位置为目标，约束相对前驱锚点的方向
            var ddx = pos.X - anchor.X;
            var ddy = pos.Y - anchor.Y;
            if (System.Math.Abs(ddx) > System.Math.Abs(ddy))
            {
                // 水平方向占优 → 保持 Y 与锚点一致
                dx = pos.X - _draggingVertex.X;
                dy = anchor.Y - _draggingVertex.Y;
            }
            else
            {
                // 竖直方向占优 → 保持 X 与锚点一致
                dx = anchor.X - _draggingVertex.X;
                dy = pos.Y - _draggingVertex.Y;
            }
        }

        _draggingVertex.X += dx;
        _draggingVertex.Y += dy;
        _vertexDragStart = pos;

        // 吸附闭合：拖拽的是 Polyline 的末端顶点（Index = Count-1）时，判定与起点（Index 0）的距离。
        //   - 未闭合 & 距离 < 阈值 → 吸附到起点坐标 + 置 IsClosed=true（骨架点直连单环闭合）
        //   - 已闭合 & 距离 > 阈值 → 撤消闭合（IsClosed=false，端点又变回独立可编辑）
        // 只对 Polyline 生效（BandPolyline 有带宽语义，闭合由轮廓管道处理，不走吸附）。
        if (vm.SelectedItem is { TypeKey: nameof(L1Primitive.Polyline) } pl
            && _draggingVertex.Index == vm.VertexHandles.Count - 1
            && vm.VertexHandles.Count >= 2)
        {
            var start = vm.VertexHandles[0];
            double dist = System.Math.Sqrt(
                System.Math.Pow(_draggingVertex.X - start.X, 2)
                + System.Math.Pow(_draggingVertex.Y - start.Y, 2));

            if (!pl.IsClosed && dist < CloseSnapThreshold)
            {
                // 吸附到起点：用起点坐标覆盖末端顶点（PathData 末端 == 首端，闭合后首尾重合点
                // 由 MergeCoincidentSteps 塌缩处理），再开闭合开关触发单环轮廓。
                _draggingVertex.X = start.X;
                _draggingVertex.Y = start.Y;
                vm.UpdateVertexPosition(_draggingVertex.Index, start.X, start.Y);
                pl.IsClosed = true;
                return;
            }
            if (pl.IsClosed && dist > CloseSnapThreshold)
            {
                // 闭合中拖离起点 → 解除闭合，端点恢复独立编辑
                pl.IsClosed = false;
                vm.UpdateVertexPosition(_draggingVertex.Index, _draggingVertex.X, _draggingVertex.Y);
                return;
            }
        }

        vm.UpdateVertexPosition(_draggingVertex.Index, _draggingVertex.X, _draggingVertex.Y);
    }

    /// <summary>顶点手柄鼠标松开：释放鼠标捕获 + 清空拖拽状态。</summary>
    private void OnVertexMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingVertex is null) return;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        _draggingVertex = null;
    }

    // ───────────── 段拖拽手柄（BandPolyline 选中后整段平移，保持长度） ─────────────

    private SegmentDragHandle? _draggingSegment;
    private Point _segmentDragStart;

    /// <summary>段手柄鼠标按下：记录拖拽起点 + 捕获鼠标。</summary>
    private void OnSegmentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not SegmentDragHandle sh) return;

        _draggingSegment = sh;
        _segmentDragStart = e.GetPosition(CanvasHost);
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>段手柄鼠标移动：把鼠标位置作为新的中点，调 VM.UpdateSegmentPosition。
    /// 段两端顶点同步移动（保持段长度）。
    /// AxisSnapEnabled=true 时由 VM 内按段方向约束（水平段只允许 dy、竖直段只允许 dx）。</summary>
    private void OnSegmentMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSegment is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingSegment = null;
            return;
        }
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var pos = e.GetPosition(CanvasHost);
        var dx = pos.X - _segmentDragStart.X;
        var dy = pos.Y - _segmentDragStart.Y;
        if (System.Math.Abs(dx) < 0.3 && System.Math.Abs(dy) < 0.3) return;

        _draggingSegment.X = pos.X;
        _draggingSegment.Y = pos.Y;
        _segmentDragStart = pos;

        vm.UpdateSegmentPosition(_draggingSegment.Index, _draggingSegment.X, _draggingSegment.Y);
    }

    /// <summary>段手柄鼠标松开：释放鼠标捕获 + 清空拖拽状态。</summary>
    private void OnSegmentMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingSegment is null) return;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        _draggingSegment = null;
    }

    // ============== 圆环（L1 Ring）拖拽手柄 ==============

    private RingHandle? _draggingRing;
    private PieHandle? _draggingPie;

    /// <summary>圆环手柄按下：记录正在拖拽的手柄 + 捕获鼠标。
    /// 通过鼠标在画布上的位置驱动 VM.UpdateRingHandle（Center=平移，Outer=改外径，Inner=改内径）。</summary>
    private void OnRingHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not RingHandle h) return;

        _draggingRing = h;
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>圆环手柄移动：把鼠标位置传给 VM.UpdateRingHandle 更新圆心/半径（几何 + 其它手柄同步）。</summary>
    private void OnRingHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingRing is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingRing = null;
            return;
        }
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var pos = e.GetPosition(CanvasHost);
        vm.UpdateRingHandle(_draggingRing.Kind, pos.X, pos.Y);
    }

    /// <summary>圆环手柄松开：释放鼠标捕获 + 清空拖拽状态。</summary>
    private void OnRingHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingRing is null) return;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        _draggingRing = null;
    }

    // ───────────── 扇形（L1 Pie）拖拽手柄 ─────────────

    /// <summary>扇形手柄按下：记录正在拖拽的手柄 + 捕获鼠标。
    /// 通过鼠标在画布上的位置驱动 VM.UpdatePieHandle（Center=平移，Radius=改半径，Start/End=改角度）。</summary>
    private void OnPieHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not PieHandle h) return;

        _draggingPie = h;
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>扇形手柄移动：把鼠标位置传给 VM.UpdatePieHandle 更新圆心/半径/角度（几何 + 其它手柄同步）。</summary>
    private void OnPieHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPie is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingPie = null;
            return;
        }
        if (DataContext is not SymbolDesignerViewModel vm) return;

        var pos = e.GetPosition(CanvasHost);
        vm.UpdatePieHandle(_draggingPie.Kind, pos.X, pos.Y);
    }

    /// <summary>扇形手柄松开：释放鼠标捕获 + 清空拖拽状态。</summary>
    private void OnPieHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingPie is null) return;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        _draggingPie = null;
    }

    // ───────────── 传送带端点手柄拖拽（拖端点 → 自动穿绕/脱开/吸合闭合） ─────────────

    private BeltHandle? _draggingBelt;

    /// <summary>传送带端点手柄按下：记录拖拽中的手柄 + 捕获鼠标。</summary>
    private void OnBeltHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not BeltHandle h) return;

        _draggingBelt = h;
        vm.ClearBeltWrapMemory();   // 新手势：清掉上一次拖动期的绕行侧记忆（用户要改侧就得能改）
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>传送带端点手柄移动：把鼠标位置传给 VM.UpdateBeltHandle → 重算整条皮带。
    /// 闭合吸附：拖 End 手柄靠近 Start（< CloseSnapThreshold）→ 吸合成闭合环；
    /// 已闭合时拖离超过阈值 → 解除闭合（与 Polyline 末端吸附闭合手感一致）。</summary>
    private void OnBeltHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingBelt is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingBelt = null;
            return;
        }
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (vm.SelectedItem is not { TypeKey: nameof(L1Primitive.Belt) } belt) return;

        var pos = e.GetPosition(CanvasHost);

        // 闭合判定：只有拖 End 手柄时参与（拖 Start 时把 B 一起带走 = 整体平移，不触发闭合切换）
        if (_draggingBelt.Kind == BeltHandleKind.End)
        {
            // 皮带端点存的是未旋转画布坐标：先把鼠标位置反旋转回来再做距离判定
            var unrotated = GeometryMath.UnrotateCanvasPoint(belt, pos);
            double dx = unrotated.X - belt.BeltStartX;
            double dy = unrotated.Y - belt.BeltStartY;
            double dist = System.Math.Sqrt(dx * dx + dy * dy);

            if (!belt.IsClosed && dist < CloseSnapThreshold)
            {
                // 吸附到起点（A）并闭合：此后两端点合一，几何变为滚轮间的闭环。
                // UpdateBeltHandle 接收的是"旋转后的画布坐标"，而 BeltStartX/Y 存的是未旋转坐标 → 先正变换。
                var startCanvas = GeometryMath.RotateCanvasPoint(belt, new Point(belt.BeltStartX, belt.BeltStartY));
                vm.UpdateBeltHandle(_draggingBelt.Kind, startCanvas.X, startCanvas.Y);
                vm.SetBeltClosed(true);
                return;
            }
            if (belt.IsClosed && dist > CloseSnapThreshold)
            {
                // 闭合态下端点必然与 A 重合（dist ≈ 0），拖离即解除闭合，再按鼠标位置继续拖
                vm.SetBeltClosed(false);
            }
        }

        vm.UpdateBeltHandle(_draggingBelt.Kind, pos.X, pos.Y);
    }

    /// <summary>传送带端点手柄松开：释放捕获 + 清空拖拽状态 + 清空绕行侧记忆。</summary>
    private void OnBeltHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingBelt is null) return;
        if (sender is UIElement ui && ui.IsMouseCaptured) ui.ReleaseMouseCapture();
        _draggingBelt = null;
        if (DataContext is SymbolDesignerViewModel vm) vm.ClearBeltWrapMemory();
    }

    // ============== 轮廓编辑模式（OutlineEditMode） ==============
// 阶段 2 改用 EditModeChoices + EnterModeCommand（RelayCommand），XAML ItemsControl 统一渲染三按钮。
// 旧的 OnSetEditModeOutlineClick / OnSetEditModeSkeletonClick 已无 XAML 引用，移除以避免误用。

    /// <summary>轮廓接缝手柄点击：选中该接缝，供属性面板编辑。</summary>
    private void OnOutlineCornerMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = DataContext as ViewModels.SymbolDesignerViewModel;
        if (vm is null) return;

        // 直接从原始源向上找 DataContext 为 OutlineCornerHandle 的元素
        var fe = e.OriginalSource as FrameworkElement;
        while (fe is not null)
        {
            if (fe.DataContext is ViewModels.OutlineCornerHandle handle)
            {
                vm.SelectedOutlineCorner = handle;
                e.Handled = true;
                return;
            }
            fe = fe.Parent as FrameworkElement;
        }
    }

    /// <summary>带宽模式画布端点点击：高亮属性面板对应骨点的 Slider 组。
    /// BandWidthHandle.VertexIndex 已映射到骨架顶点序号。</summary>
    private void OnBandWidthHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = DataContext as ViewModels.SymbolDesignerViewModel;
        if (vm is null) return;

        var fe = e.OriginalSource as FrameworkElement;
        while (fe is not null)
        {
            if (fe.DataContext is ViewModels.BandWidthHandle handle)
            {
                vm.ToggleBonePointSelectionCommand.Execute(handle.VertexIndex);
                e.Handled = true;
                return;
            }
            fe = fe.Parent as FrameworkElement;
        }
    }

    /// <summary>接缝"圆角半径"Slider 值变化：把数值写到 OutlineCornerHandle.SetRadius，
    /// 由 VM 按 IsCorner/Side 路由到 VertexBandWidth 对应字段 + 触发 OutlineGeometry 重算。
    /// Slider 的 ValueChanged 在 IsEnabled=false 时不会触发，写入路径安全。</summary>
    private void OnCornerRadiusSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is not ViewModels.SymbolDesignerViewModel vm) return;
        if (vm.SelectedOutlineCorner is not { } h) return;
        // 写入手柄：VM 内已做差值判断和通知，避免无谓刷新
        h.SetRadius(e.NewValue);
        // 同步显示数值（虽然已有 OneWay 绑定，但写入路径直接走 SetRadius → 内部已发 OnPropertyChanged(CurrentRadius)，这里不重复）
    }

    /// <summary>把接缝面板的 CornerRadiusSlider.Value 同步为当前 SelectedOutlineCorner.CurrentRadius。
    /// Slider 没有 TwoWay 绑定，CurrentRadius 派生属性变化时不会自动推过来，必须手动同步。
    /// 调用时机：SelectedOutlineCorner 切换时 + 接缝样式变化时 + 选中接缝半径被外部改写时。
    /// 同时把 Maximum 同步为 VM 动态算的上限（与"非转角点"Slider 同源，
    /// 跟随选中图元 PathData 最短段长自适应，几何尺寸放大时不再卡 20）。</summary>
    private void SyncCornerRadiusSlider()
    {
        if (CornerRadiusSlider is null) return;
        if (DataContext is not ViewModels.SymbolDesignerViewModel vm) return;
        double v = vm.SelectedOutlineCorner?.CurrentRadius ?? 0;
        // 用 SetCurrentValue 而不是直接赋值：直接赋值会写入"本地值"并覆盖 XAML 上的绑定
        // （绑定的 SliderSpinBox 是 Mode=OneWay，覆盖后 CurrentRadius 再变就推不到滑块）。
        // SetCurrentValue 只更新当前有效值，保留绑定本身。
        if (Math.Abs(CornerRadiusSlider.Value - v) > 1e-9)
            CornerRadiusSlider.SetCurrentValue(Slider.ValueProperty, v);
        // 动态上限：与属性面板"非转角点"Slider 同源（最短段长/2），由 VM.CornerArcRadiusMax 暴露
        double max = vm.CornerArcRadiusMax;
        if (Math.Abs(CornerRadiusSlider.Maximum - max) > 1e-9)
            CornerRadiusSlider.SetCurrentValue(Slider.MaximumProperty, max);
        // 刻度步长：图元大时 0.5 步长会让用户拖半天，按上限 1/100 比例取，最小不低于 0.5
        double tick = Math.Max(0.5, Math.Round(max / 100, 1));
        if (Math.Abs(CornerRadiusSlider.TickFrequency - tick) > 1e-9)
            CornerRadiusSlider.SetCurrentValue(Slider.TickFrequencyProperty, tick);
    }

    /// <summary>一键圆角：把所有轮廓接缝统一应用当前圆角半径（无需逐个选中）。
    /// 对应 XAML 属性面板"应用到全部接缝"按钮。</summary>
    private void OnApplyRoundToAllClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SymbolDesignerViewModel vm) vm.ApplyCornerArcToAll(vm.NonCornerStyle, vm.CornerArcRadius);
    }

    // ============== 取色器（SwatchesPicker）弹层事件 ==============

    /// <summary>"无填充"快捷项 → 把当前选中图元的 FillStyle 强制设为 None。
    /// 同时清空 FillColor（避免后续切回 Solid 时残留颜色），由 VM 自动同步属性面板。</summary>
    private void OnFillColorClearFillRequested(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SymbolDesignerViewModel vm) return;
        if (vm.SelectedItem is null) return;
        vm.SelectedItem.FillStyle = Models.FillStyle.None;
        // 注意：不清 FillColor — 切回 Solid 时用户上次选的颜色仍在；"无填充"只是当前不画。
    }

    /// <summary>"更多颜色" → 弹系统 ColorDialog，选中色后写回触发事件的 SwatchesPicker.SelectedColor。
    /// SelectedColor 是 TwoWay 绑定 → 自然写回 SelectedItem.FillColor/StokeColor/GradientStart/End。</summary>
    private void OnSwatchesPickerMoreColors(object sender, RoutedEventArgs e)
    {
        if (sender is not Controls.SwatchesPicker picker) return;

        // 弹出 WPF 自建 ColorDialog（HSV 平面 + Hue 滑条 + HEX 输入 + OK/Cancel）。
        // 不引入 System.Windows.Forms，避免 WinForms 类型与 WPF Point/UserControl/MouseEventArgs 命名冲突。
        string? initial = picker.SelectedColor;
        if (Controls.ColorDialogWindow.PickColor(Window.GetWindow(this), initial) is not { } newHex) return;
        picker.SelectedColor = newHex;
    }

    /// <summary>"取色器"快捷项 → 弹出屏幕取色会话（全屏放大镜 + GDI 截屏），
    /// 点击取色后写回触发事件的 SwatchesPicker.SelectedColor。
    /// 会话期间宿主窗口临时透明（不截到自己），结束自动恢复。</summary>
    private void OnSwatchesPickerPickerRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Controls.SwatchesPicker picker) return;
        var host = Window.GetWindow(this);
        if (host is null) return;

        // 屏幕取色：确认返回 hex，Esc/点外部取消返回 null（不写回，保持原色）
        if (Controls.ScreenColorPicker.PickColor(host) is not { } newHex) return;
        picker.SelectedColor = newHex;
    }
}