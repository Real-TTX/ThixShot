using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MatDesktop;

namespace ThixShot.Studio;

/// <summary>
/// The look of the small floating windows (the ready bar, the countdown and the recording bar): a rounded, slightly see-through
/// panel on a window that is itself fully transparent, so nothing shows outside the rounded corners.
/// </summary>
/// <remarks>
/// Only per-pixel transparency is asked for. Acrylic or blur would fill the whole window rectangle, so the corners outside the
/// rounded panel would show a dark square.
/// </remarks>
internal static class GlassPanel
{
    private const double SeeThroughOpacity = .9;

    internal static Border Apply(Window window, Control content, double cornerRadius)
    {
        // MatWindow binds the background to the theme; the binding has to go, otherwise a theme switch brings it back.
        window.ClearValue(Window.BackgroundProperty);
        window.Background = Brushes.Transparent;
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];

        var backdrop = new Border { CornerRadius = new CornerRadius(Math.Max(0, cornerRadius - 1)) }.Use(Border.BackgroundProperty, "Mat.Surface");
        void UpdateTint() => backdrop.Opacity = window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent ? SeeThroughOpacity : 1;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty) UpdateTint();
        };
        UpdateTint();

        return new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { backdrop, content } },
        }.Use(Border.BorderBrushProperty, "Mat.BorderStrong");
    }
}
