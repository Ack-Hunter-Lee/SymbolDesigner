using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SymbolDesigner.Models;

/// <summary>工程文件读写收口（GUI 与 CLI 共用同一套读写，避免各写一份）。
///
/// 支持两种落盘形态：读时自动探测，写时按目标扩展名决定——
///  - **.symz 压缩包容器**：ZIP 内含 project.json（可读清单）+ assets/ 下的二进制资产。
///    底图不再内联 Base64 → 体积小、清单可读、资产随包自包含（不依赖外部原图，避免丢图）。
///  - **.sym.json 纯文本单文件**：沿用既有行为，底图以内联 Base64 存放，便于文本 diff 与 agent 直接读写。
///
/// 兼容：老 .sym.json（内联版 / 仅有外部路径版）继续可读；就地保存维持原格式，不擅自升级。
/// 确定性：容器内条目顺序固定、时间戳固定，同内容重复保存产出字节一致的包，便于 diff 与缓存。</summary>
public static class SymbolProjectFile
{
    /// <summary>压缩包容器扩展名。</summary>
    public const string ContainerExtension = ".symz";

    /// <summary>纯文本单文件扩展名。</summary>
    public const string LegacyExtension = ".sym.json";

    /// <summary>容器内工程清单的固定条目名。</summary>
    public const string ProjectEntryName = "project.json";

    /// <summary>容器内资产目录的固定前缀。</summary>
    public const string AssetsFolder = "assets";

    /// <summary>底图资产的逻辑键。</summary>
    public const string BackgroundAssetKey = "background";

    /// <summary>当前容器格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    private static readonly IReadOnlyDictionary<string, byte[]> s_noAssets = new Dictionary<string, byte[]>();

    /// <summary>读取结果：工程清单模型 + 容器内资产（逻辑键 → 原始字节）。纯文本格式时资产为空表。</summary>
    public sealed record LoadedProject(SymbolProject Project, IReadOnlyDictionary<string, byte[]> Assets);

    /// <summary>路径是否指向压缩包容器（仅按扩展名判断，不读文件）。</summary>
    public static bool IsContainerPath(string path)
        => string.Equals(Path.GetExtension(path), ContainerExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>读取工程（自动探测容器 / 纯文本）。文件不存在抛 FileNotFoundException，
    /// 内容非法抛 FormatException；返回 null 仅当清单反序列化结果为空。</summary>
    public static LoadedProject? Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"工程文件不存在: {path}", path);
        if (IsZip(path)) return LoadContainer(path);

        var project = SymbolProject.Deserialize(File.ReadAllText(path, Encoding.UTF8));
        return project is null ? null : new LoadedProject(project, s_noAssets);
    }

    /// <summary>写入工程（目标为 .symz 则打包容器，否则写纯文本内联）。
    /// <paramref name="assets"/> 为调用方显式附带的资产（CLI 原样回写用）；
    /// GUI 可不传——此时清单里内联的 BackgroundImageData 会被自动迁入 assets/ 并清空内联字段。</summary>
    public static void Save(string path, SymbolProject project, IReadOnlyDictionary<string, byte[]>? assets = null)
    {
        if (IsContainerPath(path)) SaveContainer(path, project, assets);
        else SavePlainText(path, project, assets);
    }

    // ==================== 写入 ====================

    private static void SaveContainer(string path, SymbolProject project, IReadOnlyDictionary<string, byte[]>? assets)
    {
        var manifest = new List<AssetDto>();
        var payload = new List<(string Name, byte[] Bytes)>();

        void AddAsset(string key, byte[] bytes)
        {
            if (bytes.Length == 0) return;
            var ext = SniffImageExtension(bytes);
            var name = $"{AssetsFolder}/{SafeAssetName(key)}.{ext}";
            manifest.Add(new AssetDto { Key = key, Path = name, MediaType = MediaTypeOf(ext) });
            payload.Add((name, bytes));
        }

        // 1) 清单里内联的底图（GUI 保存时由 ToSymbolProject 产出）→ 迁入 assets/
        if (project.BackgroundImageData is { Length: > 0 } inline)
            AddAsset(BackgroundAssetKey, inline);

        // 2) 调用方显式传入的资产（CLI 从容器读出后原样回写）；已在清单中的键不重复
        if (assets is not null)
            foreach (var kv in assets.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (kv.Value is { Length: > 0 } && manifest.All(a => a.Key != kv.Key))
                    AddAsset(kv.Key, kv.Value);

        // 资产进包 → 清单不再内联 Base64
        project.BackgroundImageData = null;
        project.BackgroundImageType = null;
        project.FormatVersion = CurrentFormatVersion;
        project.Assets = manifest;

        var json = Encoding.UTF8.GetBytes(project.Serialize());
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, ProjectEntryName, json, CompressionLevel.Optimal);
        // 图片本身已是压缩格式，再 deflate 只白耗 CPU 且几乎不缩小
        foreach (var (name, bytes) in payload)
            WriteEntry(zip, name, bytes, CompressionLevel.NoCompression);
    }

    private static void SavePlainText(string path, SymbolProject project, IReadOnlyDictionary<string, byte[]>? assets)
    {
        // 换格式（.symz → .sym.json）时把资产回填为内联，避免底图丢失
        if (project.BackgroundImageData is not { Length: > 0 }
            && assets is not null
            && assets.TryGetValue(BackgroundAssetKey, out var bytes)
            && bytes.Length > 0)
        {
            project.BackgroundImageData = bytes;
            project.BackgroundImageType ??= SniffImageExtension(bytes);
        }

        // 纯文本形态不携带容器元数据
        project.FormatVersion = 0;
        project.Assets = null;
        File.WriteAllText(path, project.Serialize(), new UTF8Encoding(false));
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        // 固定时间戳（1980-01-01，ZIP 合法下限）→ 抹掉"保存时刻"差异，同内容重复保存字节一致
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = entry.Open();
        s.Write(content, 0, content.Length);
    }

    // ==================== 读取 ====================

    private static LoadedProject? LoadContainer(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(ProjectEntryName)
            ?? throw new FormatException($"容器缺少 {ProjectEntryName}: {path}");

        string json;
        using (var stream = entry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            json = reader.ReadToEnd();

        var project = SymbolProject.Deserialize(json)
            ?? throw new FormatException($"容器内工程清单无法解析: {path}");

        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in project.Assets ?? Enumerable.Empty<AssetDto>())
        {
            if (string.IsNullOrWhiteSpace(asset.Key)) continue;
            var name = NormalizeAssetEntryName(asset.Path);
            if (name is null) continue;                 // 非法/越界路径 → 跳过
            var assetEntry = zip.GetEntry(name);
            if (assetEntry is null) continue;           // 清单指向缺失条目 → 跳过（不致命）
            using var es = assetEntry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            assets[asset.Key] = ms.ToArray();
        }
        return new LoadedProject(project, assets);
    }

    /// <summary>按扩展名优先 + 魔数兜底探测是否为 ZIP 容器
    /// （无扩展名或后缀与实际内容不符时仍能正确识别）。</summary>
    private static bool IsZip(string path)
    {
        if (IsContainerPath(path)) return true;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[2];
            return fs.Read(magic) == 2 && magic[0] == 0x50 && magic[1] == 0x4B; // "PK"
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 读不到头部 → 当作纯文本处理，由后续读取给出明确错误
        }
    }

    /// <summary>校验资产条目名：只接受 "assets/&lt;单个文件名&gt;" 形式的相对路径，
    /// 拒绝绝对路径、盘符、目录穿越（..）与多余层级（防 zip slip）。
    /// 返回保留原始大小写的规范化名（用于 GetEntry 精确命中）。</summary>
    private static string? NormalizeAssetEntryName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Replace('\\', '/');
        if (Path.IsPathRooted(raw) || cleaned.Contains(':')) return null;
        while (cleaned.StartsWith("./", StringComparison.Ordinal)) cleaned = cleaned[2..];
        if (cleaned.StartsWith('/')) return null;

        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        if (!string.Equals(parts[0], AssetsFolder, StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[1] is "." or ".." || parts[1].Contains("..")) return null;
        return cleaned;
    }

    /// <summary>资产键 → 安全文件名（只保留字母数字与 -_，防注入路径分隔符）。</summary>
    private static string SafeAssetName(string key)
    {
        var filtered = new string(key.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        return filtered.Length > 0 ? filtered : "asset";
    }

    /// <summary>按魔数判断图片格式，返回不带点号的扩展名（未知时回退 "png"，与既有加载逻辑默认一致）。</summary>
    private static string SniffImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "jpg";
        if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return "bmp";
        if (bytes.Length >= 3
            && ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A)
                || (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00)))
            return "tif";
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "webp";
        return "png";
    }

    private static string MediaTypeOf(string ext) => ext switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "tif" or "tiff" => "image/tiff",
        "webp" => "image/webp",
        _ => "image/png",
    };
}
