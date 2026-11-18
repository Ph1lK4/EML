using System.Windows;
using System.Windows.Media.Imaging;

namespace EML.App.Tests;

/// <summary>检查实际嵌入的 Logo 资源，不依赖源码目录或额外图片文件。</summary>
internal static class BrandingChecks
{
    /// <summary>在现有 STA 测试应用中解码透明标志与全部 ICO 尺寸。</summary>
    public static void Run()
    {
        using var icon = Application.GetResourceStream(new Uri("/EML.App;component/Assets/Branding/eml.ico", UriKind.Relative)).Stream;
        var decoder = new IconBitmapDecoder(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, decoder.Frames.Select(frame => frame.PixelWidth).Order().ToArray());
        Assert.All(decoder.Frames, frame => Assert.Equal(frame.PixelWidth, frame.PixelHeight));
        using var png = Application.GetResourceStream(new Uri("/EML.App;component/Assets/Branding/eml.png", UriKind.Relative)).Stream;
        var image = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(1024, image.Frames[0].PixelWidth);
        Assert.Equal(1024, image.Frames[0].PixelHeight);
    }
}
