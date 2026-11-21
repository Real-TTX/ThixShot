using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using SkiaSharp;

[assembly: AvaloniaTestApplication(typeof(ThixShot.Tests.TestProfile))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixShot.Tests;

public static class TestProfile
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [ModuleInitializer]
    internal static void IsolateUserData()
    {
        var folder = Directory.CreateTempSubdirectory("ThixShot-tests-");
        AppContext.SetData("ThixShot.MediaDirectory", Path.Combine(folder.FullName, "Media"));
        AppContext.SetData("ThixShot.IntegrationDirectory", Path.Combine(folder.FullName, "Integration"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { folder.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    internal static MemoryStream TestImage()
    {
        using var pixels = new SKBitmap(64, 32);
        pixels.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(pixels);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(png.ToArray());
    }
}
