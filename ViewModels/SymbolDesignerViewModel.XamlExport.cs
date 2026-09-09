using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SymbolDesigner.Models;

namespace SymbolDesigner.ViewModels;

/// <summary>画布 XAML 导出：导出对话框 / 剪贴板复制 / 保存到文件。</summary>
public partial class SymbolDesignerViewModel
{
    // ============== XAML 导出 ==============

    /// <summary>当前画布的 XAML 导出（按需计算，避免重复序列化）。
    /// "调整源"期间会过滤掉物化的临时源——源专属瞬态编辑壳，不进客户工程/不撑包围盒。</summary>
    public string ExportedXaml
    {
        get
        {
            IEnumerable<PrimitiveItem> items = IsSourceAdjustMode && _sourceAdjTemp.Count > 0
                ? Primitives.Where(p => !_sourceAdjTemp.Contains(p.Id))
                : (IEnumerable<PrimitiveItem>)Primitives;
            return XamlExporter.ExportCanvas(items);
        }
    }

    /// <summary>导出弹窗是否可见。</summary>
    [ObservableProperty] private bool _isExportDialogOpen;

    /// <summary>弹窗打开时即时刷新 XAML 内容（避免打开后内容滞后）。</summary>
    partial void OnIsExportDialogOpenChanged(bool value)
    {
        if (value) OnPropertyChanged(nameof(ExportedXaml));
    }

    /// <summary>打开导出对话框。</summary>
    [RelayCommand]
    private void OpenExportDialog()
    {
        OnPropertyChanged(nameof(ExportedXaml));
        IsExportDialogOpen = true;
    }

    /// <summary>选中/取消选中某个骨点（点击"骨点 i"组标题时调用），高亮对应 Slider 的两个端点。
    /// 再次点击同一骨点 → 取消高亮（回到 -1）。仅 BandWidth 模式有效。</summary>
    [RelayCommand]
    private void ToggleBonePointSelection(int index)
    {
        if (OutlineEditMode != OutlineEditMode.BandWidth) return;
        SelectedBonePointIndex = (SelectedBonePointIndex == index) ? -1 : index;
    }

    /// <summary>把 XAML 写入系统剪贴板。</summary>
    [RelayCommand]
    private void CopyXamlToClipboard()
    {
        try
        {
            System.Windows.Clipboard.SetText(ExportedXaml);
        }
        catch
        {
            // Clipboard 在某些环境下可能抛 COMException；用户场景下一般不会发生
        }
    }

    /// <summary>弹保存对话框，把 XAML 写入 .xaml 文件。</summary>
    [RelayCommand]
    private void SaveXamlToFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "Symbol",
            DefaultExt = ".xaml",
            Filter = "XAML 文件 (*.xaml)|*.xaml|所有文件 (*.*)|*.*",
            AddExtension = true,
            Title = "导出画布 XAML",
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, ExportedXaml);
            }
            catch
            {
                // 写入失败静默（生产环境可加日志或弹错误提示）
            }
        }
    }
}