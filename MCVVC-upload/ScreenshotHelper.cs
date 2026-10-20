using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MclLauncher;

/// <summary>
/// 把 WPF 窗口渲染成 PNG。本机 Add-Type / 反射加载都被安全策略拦了，
/// 截屏工具走不通，所以让程序自己渲染 —— 顺带也成了 UI 回归工具。
/// </summary>
public static class ScreenshotHelper
{
    public static void Save(Window window, string fileName)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"screenshot: {path} ({width}x{height})");
    }
}
