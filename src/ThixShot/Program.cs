using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using MatDesktop;
using ThixShot.Studio;

namespace ThixShot;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        MatText.Language = "de";
        using var icon = AssetLoader.Open(new Uri("avares://ThixShot/Assets/ThixShot.ico"));
        MatTheme.Install(this, new MatThemeOptions("#2F6FED", Appearance: StudioAppearance.Current) { Icon = new WindowIcon(icon) });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new StudioWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
