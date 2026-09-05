using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace SymbolDesigner.ViewModels;

/// <summary>背景参考图（描图用底层图片，仅编辑器内显示，不导出到 XAML）的加载 / 清除。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== 背景参考图导入/清除 ==============

    /// <summary>
    /// 从文件路径加载图片作为背景参考图。
    /// 构造 BitmapImage（带缓存，关闭文件锁），设置到 BackgroundImageSource。
    /// 失败时返回 false 并保持现有背景不变。
    /// </summary>
    public bool LoadBackgroundImage(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 加载后释放文件锁
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(filePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();  // 允许跨线程访问

            BackgroundImageSource = bmp;
            BackgroundImagePath = filePath;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载背景图失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// 从内存中的图片字节加载背景参考图（无需文件路径）。
    /// 工程文件嵌入 Base64 时调此方法恢复图像,不依赖任何外部文件。
    /// </summary>
    /// <param name="imageBytes">图片原始字节（PNG/JPG/BMP/GIF/TIFF 等 WPF 支持格式）。</param>
    /// <param name="extensionHint">扩展名提示（不带点号，如 "png"/"jpg"），
    /// BitmapImage 内部用此 hint 决定解码器。null 时默认 "png"。</param>
    public bool LoadBackgroundImage(byte[] imageBytes, string? extensionHint = null)
    {
        try
        {
            if (imageBytes is null || imageBytes.Length == 0) return false;

            var ext = (extensionHint ?? "png").TrimStart('.');
            // 缓存到临时目录，扩展名 hint 给 BitmapDecoder 用
            var tempFile = Path.Combine(Path.GetTempPath(), $"fk_bg_{Guid.NewGuid():N}.{ext}");
            File.WriteAllBytes(tempFile, imageBytes);
            try
            {
                return LoadBackgroundImage(tempFile);
            }
            finally
            {
                // LoadBackgroundImage 内部用 OnLoad 缓存,加载完可安全删除临时文件
                try { File.Delete(tempFile); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载背景图失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>清除背景参考图（恢复纯色背景）。</summary>
    [RelayCommand]
    private void ClearBackgroundImage()
    {
        BackgroundImageSource = null;
        BackgroundImagePath = null;
    }
}