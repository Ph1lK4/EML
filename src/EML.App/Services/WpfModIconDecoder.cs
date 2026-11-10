using System.IO;
using System.Windows.Media.Imaging;

namespace EML.App.Services;

/// <summary>远程图片只作为有界缩略图解码，输出单帧 PNG 后才允许 WPF 绑定本机路径。</summary>
internal static class WpfModIconDecoder
{
    private const int MaximumDimension = 2048;
    private const int MaximumFrames = 16;
    private const int ThumbnailSize = 96;

    /// <summary>按原比例生成不超过 96 像素的缩略图；超限返回空结果，损坏图片由缓存服务兜底。</summary>
    internal static byte[]? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        using var input = new MemoryStream(bytes, writable: false);
        var header = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (header.Frames.Count is 0 or > MaximumFrames) return null;
        var width = header.Frames[0].PixelWidth;
        var height = header.Frames[0].PixelHeight;
        // 平台图标不满足展示限制是正常的拒绝结果，不需要抛异常让调试器中断。
        if (width is <= 0 or > MaximumDimension || height is <= 0 or > MaximumDimension) return null;

        input.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        // 只约束长边，保留宽高比，也不放大小图标。
        if (width >= height) image.DecodePixelWidth = Math.Min(width, ThumbnailSize);
        else image.DecodePixelHeight = Math.Min(height, ThumbnailSize);
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
