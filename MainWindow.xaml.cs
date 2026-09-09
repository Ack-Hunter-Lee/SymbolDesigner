using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SymbolDesigner.ViewModels;

namespace SymbolDesigner;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>窗口级预览按键（tunneling 起点）：
    /// - Enter：绘制中强制结束绘制。原因：KeyBinding 在本 Window 的 InputBindings 里，
    ///   但若焦点落在某控件（如 SliderSpinBox 的内嵌 TextBox 会消费 Enter），KeyBinding 收不到。
    ///   与方向键一样在 Window 层拦截才稳定，绘制中始终放行提交。
    /// - 方向键微移选中元素：WPF 把方向键当焦点导航键，KeyBinding / 画布级 PreviewKeyDown 都收不到，
    ///   必须在 Window 级 PreviewKeyDown 拦截才会先于导航触发。
    /// 焦点在 TextBox(属性面板)内时方向键放行给光标移动；但不放行 Enter（避免干扰绘制结束）。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var vm = DataContext as SymbolDesignerViewModel
                 ?? (Content as FrameworkElement)?.DataContext as SymbolDesignerViewModel;
        // Enter 结束绘制：即使某控件持有键盘焦点也强制提交（保证 Polyline/BandPolyline 能用 Enter 结束）
        if (e.Key == Key.Enter && vm is not null && vm.IsDrawing)
        {
            vm.FinishDraw();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is not TextBox
            && vm is not null
            && (vm.SelectedItem is not null || vm.MultiSelected.Count > 0))
        {
            vm.MoveSelectionCommand.Execute(e.Key.ToString());
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>窗口关闭拦截：工程 IsDirty=true 时弹出"保存/不保存/取消"三选一对话框，
    /// 三种分支都不直接退出，默认行为是"取消"——中断退出流程（避免误触关闭按钮后丢失工作）。
    ///   保存   → 调 vm.SaveCommand.Execute(null)；成功 → 放行退出；失败/取消 → 中断
    ///   不保存 → 放行退出
    ///   取消   → 中断退出
    /// MessageBox 走系统默认样式，避免再引入一组 DarkDialog 资源——保持简洁。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var vm = DataContext as SymbolDesignerViewModel
                 ?? (Content as FrameworkElement)?.DataContext as SymbolDesignerViewModel;
        if (vm is null || !vm.IsDirty)
        {
            base.OnClosing(e);
            return;
        }

        var result = MessageBox.Show(
            "工程有未保存的改动，是否保存后退出？\n\n是：保存并退出\n否：放弃改动并退出\n取消：中断关闭",
            "未保存的改动",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel); // 默认选中"取消" → 默认中断退出

        switch (result)
        {
            case MessageBoxResult.Yes:
                if (vm.SaveProjectCommand.CanExecute(null))
                {
                    vm.SaveProjectCommand.Execute(null);
                }
                // 保存失败/取消了保存对话框 → 中断退出
                if (vm.IsDirty)
                {
                    e.Cancel = true;
                }
                break;

            case MessageBoxResult.No:
                // 不保存 → 放行（直接退出，不清 IsDirty——避免误导后续撤销栈）
                break;

            default: // Cancel / 关闭按钮 / Esc
                e.Cancel = true;
                break;
        }

        base.OnClosing(e);
    }
}