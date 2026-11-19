using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace EML.App.Services;

/// <summary>保存外观设置和经过解码校验的静态壁纸副本，不修改用户原始图片。</summary>
internal sealed class AppearanceSettingsStore
{
    internal const long MaximumWallpaperBytes = 25L * 1024 * 1024;
    internal const long MaximumWallpaperPixels = 64L * 1024 * 1024;
    internal const int MaximumDecodedDimension = 3840;
    private const long MaximumSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal string SettingsPath { get; }
    internal string WallpaperDirectory { get; }

    /// <summary>固定外观数据位置，避免调色或更换壁纸影响其他设置文件。</summary>
    internal AppearanceSettingsStore(string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        var root = Path.GetFullPath(launcherRoot);
        SettingsPath = Path.Combine(root, "settings", "appearance.json");
        WallpaperDirectory = Path.Combine(root, "settings", "appearance");
    }

    /// <summary>读取设置；损坏或无法读取时保留原文件，并返回可向用户显示的说明。</summary>
    internal AppearanceSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumSettingsBytes) throw new InvalidDataException("外观设置文件异常偏大。");
            var settings = JsonSerializer.Deserialize<AppearanceSettings>(stream, JsonOptions)
                ?? throw new InvalidDataException("外观设置内容为空。");
            return AppearanceService.Validate(settings);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppearanceSettings();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = $"未能读取外观设置，已暂时使用默认外观；原文件未更改。\n{ex.Message}\n{SettingsPath}";
            return new AppearanceSettings();
        }
    }

    /// <summary>先完整写入同目录临时文件，再原子替换设置，保存失败不会宣称成功。</summary>
    internal void Save(AppearanceSettings settings)
    {
        settings = AppearanceService.Validate(settings);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".appearance-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { TryDeleteTemporary(temporary); }
    }

    /// <summary>导入本地 PNG/JPEG 并生成受控大小的 PNG 副本，返回可存入设置的文件名。</summary>
    internal string ImportWallpaper(string path)
    {
        var source = LocalImagePath(path);
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumWallpaperBytes)
            throw new InvalidDataException("请选择不超过 25 MiB 的 PNG 或 JPEG 静态图片。");
        var bitmap = Decode(stream);
        Directory.CreateDirectory(WallpaperDirectory);
        var name = $"wallpaper-{Guid.NewGuid():N}.png";
        var target = Path.Combine(WallpaperDirectory, name);
        var temporary = Path.Combine(WallpaperDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
            return name;
        }
        finally { TryDeleteTemporary(temporary); }
    }

    /// <summary>仅允许设置引用自管目录里的 PNG 文件名，禁止路径穿越与远程地址。</summary>
    internal string GetWallpaperPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("壁纸记录不是有效的本地 PNG 文件名。", nameof(fileName));
        return Path.Combine(WallpaperDirectory, fileName);
    }

    /// <summary>读入自管壁纸并释放文件句柄，缺失或损坏由界面回退为纯色并说明。</summary>
    internal BitmapSource LoadWallpaper(string fileName)
    {
        using var stream = new FileStream(GetWallpaperPath(fileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumWallpaperBytes * 4)
            throw new InvalidDataException("保存的壁纸文件大小异常，请重新选择图片。");
        return Decode(stream);
    }

    /// <summary>只接受本地绝对路径和静态图片扩展名，不自动访问网络共享或网址。</summary>
    private static string LocalImagePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc)
            throw new ArgumentException("请选择本机磁盘上的图片，不支持网址或网络共享。", nameof(path));
        var extension = Path.GetExtension(path);
        if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("仅支持 PNG、JPG 和 JPEG 静态壁纸，不支持 GIF 或视频。", nameof(path));
        return Path.GetFullPath(path);
    }

    /// <summary>先检查真实格式和像素数，再按屏幕用途限尺寸解码，防止超大图片占满内存。</summary>
    private static BitmapSource Decode(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder) || decoder.Frames.Count != 1)
                throw new InvalidDataException("图片内容不是支持的 PNG 或 JPEG 静态图片。");
            var frame = decoder.Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > MaximumWallpaperPixels)
                throw new InvalidDataException("图片分辨率过大，请选择不超过 6400 万像素的图片。");
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height && width > MaximumDecodedDimension) bitmap.DecodePixelWidth = MaximumDecodedDimension;
            else if (height > MaximumDecodedDimension) bitmap.DecodePixelHeight = MaximumDecodedDimension;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            throw new InvalidDataException("图片无法解码，请更换有效的 PNG 或 JPEG 文件。", ex);
        }
    }

    /// <summary>只清理由本次保存创建的临时文件，不删除已有设置、壁纸或用户原图。</summary>
    private static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
