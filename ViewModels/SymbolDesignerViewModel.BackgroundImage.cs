using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace SymbolDesigner.ViewModels;

/// <summary>背景参考图（描图用底层图片，仅编辑器内显示，不导出到 XAML）的加载 / 清除。</summary>
public partial class SymbolDesignerViewModel
{
    /// <summary>当前底图的原始字节缓存（导入或加载时记下）。保存工程时直接用它，
    /// 不再回头读外部文件 → 外部原图被删/移动也不会丢底图；存 .symz 时这些字节进 assets/。</summary>
    private byte[]? _backgroundImageBytes;

    /// <summary>底图字节的来源扩展名提示（不带点号），写入 BackgroundImageType 供解码端参考。
    /// 容器内资产无扩展名语义，缺省 "png"。</summary>
    private string? _backgroundImageExt;

    // ============== 背景参考图导入/清除 ==============

    /// <summary>
    /// 从文件路径加载图片作为背景参考图。
    /// 读入字节后缓存（供保存工程复用），再解码为 BitmapImage（带缓存，无文件锁）。
    /// 失败时返回 false 并保持现有背景不变。
    /// </summary>
    public bool LoadBackgroundImage(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            var bytes = File.ReadAllBytes(filePath);
            var bmp = DecodeImage(bytes);
            if (bmp is null) return false;

            _backgroundImageBytes = bytes;
            var ext = Path.GetExtension(filePath).TrimStart('.');
            _backgroundImageExt = string.IsNullOrEmpty(ext) ? "png" : ext;

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
    /// 从内存中的图片字节加载背景参考图（无需外部文件）。
    /// 工程内嵌 Base64 / 容器 assets 里的底图都走这里，加载后不依赖任何外部文件。
    /// </summary>
    /// <param name="imageBytes">图片原始字节（PNG/JPG/BMP/GIF/TIFF/WEBP 等 WPF 支持格式）。</param>
    /// <param name="extensionHint">扩展名提示（不带点号，如 "png"），仅记录到 BackgroundImageType；
    /// 解码本身由 WPF 按流内容自动识别格式，不依赖此值。</param>
    public bool LoadBackgroundImage(byte[] imageBytes, string? extensionHint = null)
    {
        try
        {
            if (imageBytes is null || imageBytes.Length == 0) return false;
            var bmp = DecodeImage(imageBytes);
            if (bmp is null) return false;

            _backgroundImageBytes = imageBytes;
            _backgroundImageExt = (extensionHint ?? "png").TrimStart('.');

            BackgroundImageSource = bmp;
            BackgroundImagePath = null;   // 字节加载没有外部来源文件
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载背景图失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>清除背景参考图（恢复纯色背景），并丢弃字节缓存。</summary>
    [RelayCommand]
    private void ClearBackgroundImage()
    {
        BackgroundImageSource = null;
        BackgroundImagePath = null;
        _backgroundImageBytes = null;
        _backgroundImageExt = null;
    }

    /// <summary>把图片字节解码为可跨线程使用的 BitmapImage。
    /// 用 StreamSource（OnLoad 缓存）直接从内存解码：无需临时文件，也不留文件锁。
    /// WPF 由流内容自动识别格式。失败返回 null。</summary>
    private static BitmapImage? DecodeImage(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;   // 解码完即入内存，流可释放
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();  // 允许跨线程访问
        return bmp;
    }
}
