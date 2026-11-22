using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using ThixShot.Studio;

namespace ThixShot.Editor;

internal sealed class SelectionWindow : Window
{
    public SelectionWindow(Stream source)
    {
        Title = "Aufnahmebereich auswählen · ThixShot";
        var document = new ImageDocument(source);
        var surface = new ImageSurface(document, editorViewport: false) { Tool = MarkTool.Crop };
        WindowState = WindowState.FullScreen;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Black;
        var hint = new Border
        {
            Child = Ui.Row(9, new MatIcon { Icon = "info", Size = 16 }.Use(MatIcon.ForegroundProperty, "Mat.AccentText"),
                new TextBlock { Text = "Bereich mit der Maus aufziehen   ·   Esc zum Abbrechen" }.Use(TextBlock.ForegroundProperty, "Mat.Text")),
            Padding = new Thickness(14, 9), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(MatTokens.Size.Radius)
        }.Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.BorderStrong");
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 18, 0, 0);
        Content = new Grid { Children = { surface, hint } };
        surface.Submitted = mark =>
        {
            var area = ImageEditor.Selection(mark.Start, mark.End, document.Visible);
            if (area.Width >= 2 && area.Height >= 2) Close(area);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { surface.Dispose(); document.Dispose(); };
    }
}
