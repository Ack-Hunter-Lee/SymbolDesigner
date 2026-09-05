using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace SymbolDesigner.Converters;

/// <summary>
/// 取文件路径的纯文件名（不含目录），用于工具栏显示"当前背景图: xxx.png"。
/// 入参 null/空 → 返回空串。入参非路径 → 直接返回原字符串。
/// </summary>
public class FileNameOnlyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return string.Empty;
        try { return Path.GetFileName(s); }
        catch { return s; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
