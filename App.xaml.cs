using System.Windows;
using System.Windows.Threading;

namespace SymbolDesigner;

/// <summary>
/// SymbolDesigner 独立程序入口。
/// 启动时仅弹出元件设计器主窗口，不再嵌入主程序 FactoryKanban。
/// 仍可被 FactoryKanban 通过 ProjectReference 复用所有代码（库 + 启动代码）。
/// </summary>
public partial class App : Application
{
    public App()
    {
        // 捕获所有未处理异常到 MessageBox，方便定位 XAML 解析错误
        DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show($"[Dispatcher] {e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "SymbolDesigner 启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        System.Threading.Thread.GetDomain().UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                MessageBox.Show($"[Domain] {ex.Message}\n\n{ex.StackTrace}",
                    "SymbolDesigner 域异常", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}