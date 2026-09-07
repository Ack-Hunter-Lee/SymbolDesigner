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

    /// <summary>方向键微移选中元素：
    /// WPF 把方向键当焦点导航键，KeyBinding / 画布级 PreviewKeyDown 都收不到，
    /// 必须在 Window 级 PreviewKeyDown（tunneling 起点）拦截才会先于导航触发。
    /// 焦点在 TextBox(属性面板)内放行，方向键留给光标移动。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var vm = DataContext as SymbolDesignerViewModel
                 ?? (Content as FrameworkElement)?.DataContext as SymbolDesignerViewModel;
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
}