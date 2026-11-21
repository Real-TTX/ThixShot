using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using SkiaSharp;
using ThixShot.Library;
using ThixShot.Studio;

namespace ThixShot.Editor;

internal sealed class EditorWindow : MatWindow
{
    private readonly ImageDocument document;
    private readonly ImageSurface surface;
    private readonly TextBlock status = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Themed("MatText.Muted");
    private readonly Dictionary<MarkTool, Button> tools = [];
    private readonly Button undo;
    private readonly ComboBox colors;

    public EditorWindow(Stream source, bool sample = false)
    {
        document = new ImageDocument(source);
        surface = new ImageSurface(document);
        Title = sample ? "Beispielbild bearbeiten · ThixShot" : "Screenshot bearbeiten · ThixShot";
        Width = 1000; Height = 720; MinWidth = 740; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseOwnTitleBar();
        var logo = Ui.Asset("Mark.png");
        var titleBar = new MatTitleBar
        {
            AppName = "ThixShot", Logo = logo, BrandWidth = 168,
            Center = new TextBlock { Text = Title.Split('·')[0].Trim(), VerticalAlignment = VerticalAlignment.Center }.Themed("MatText.SectionTitle")
        };
        var toolRow = Ui.Row(6);
        foreach (var (mode, name, icon) in new[] { (MarkTool.Highlight,"Markieren","highlight"), (MarkTool.Arrow,"Pfeil","arrow"),
                     (MarkTool.Rectangle,"Rahmen","box"), (MarkTool.Crop,"Ausschneiden","crop") })
        {
            tools[mode] = Ui.Button(name, icon, () => Select(mode));
            toolRow.Children.Add(tools[mode]);
        }
        colors = Ui.Choices(["Gold", "Koralle", "Blau", "Grün"], 0, i =>
            surface.MarkColor = SKColor.Parse(new[] { "#FFB729", "#EF544A", "#3575E6", "#2DA476" }[i]));
        colors.MinWidth = 92;
        colors.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(colors, "Markierungsfarbe");
        ToolTip.SetTip(colors, "Markierungsfarbe");
        toolRow.Children.Add(colors);
        undo = Ui.Button("Zurücknehmen", "undo", Undo);
        toolRow.Children.Add(undo);
        var save = Ui.Button("In Mediathek speichern", "save", Save, "primary");
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var toolbar = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 9),
            Child = new ScrollViewer
            {
                Content = toolRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        }.Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.Border");
        var canvas = new Border
        {
            Child = surface, Padding = new Thickness(8), CornerRadius = new CornerRadius(MatTokens.Size.RadiusLg),
            BorderThickness = new Thickness(1), Margin = new Thickness(20, 16), ClipToBounds = true
        }.Use(Border.BackgroundProperty, "Mat.Surface2").Use(Border.BorderBrushProperty, "Mat.Border");
        var actions = new Border
        {
            Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Ui.Columns("Auto,*", Ui.Row(8, save, Ui.Button("Schließen", null, Close)), status)
        }.Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.Border");
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(toolbar, 1); Grid.SetRow(canvas, 2); Grid.SetRow(actions, 3);
        grid.Children.Add(titleBar); grid.Children.Add(toolbar); grid.Children.Add(canvas); grid.Children.Add(actions);
        Content = grid;
        surface.Submitted = mark => { if (ImageEditor.Add(document, mark)) Update(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { Undo(); e.Handled = true; }
            if (e.Key == Key.Escape) Close();
        };
        Closed += (_, _) => { surface.Dispose(); document.Dispose(); logo.Dispose(); };
        Select(MarkTool.Highlight);
        Update(false);
    }

    private void Select(MarkTool mode)
    {
        surface.Tool = mode;
        colors.IsVisible = mode != MarkTool.Crop;
        foreach (var (tool, button) in tools)
        {
            button.Use(Button.BackgroundProperty, tool == mode ? "Mat.AccentSoft" : "Mat.Surface");
            button.Use(Button.BorderBrushProperty, tool == mode ? "Mat.Accent" : "Mat.Border");
        }
    }
    private void Update(bool refresh = true)
    {
        if (refresh) surface.Refresh();
        undo.IsEnabled = document.History.Count > 0;
        status.Text = $"{document.Visible.Width} × {document.Visible.Height} px";
    }
    private void Undo() { ImageEditor.Undo(document); Update(); }
    private void Save()
    {
        string? temporary = null;
        try
        {
            temporary = MediaLibrary.TemporaryFile(".png");
            using var data = ImageEditor.Render(document);
            using (var file = File.Create(temporary)) data.SaveTo(file);
            Close(MediaLibrary.Import(temporary, "Screenshot"));
        }
        catch (Exception error) { status.Text = error.Message; }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class SelectionWindow : Window
{
    public SelectionWindow(Stream source)
    {
        Title = "Aufnahmebereich auswählen · ThixShot";
        var document = new ImageDocument(source);
        var surface = new ImageSurface(document) { Tool = MarkTool.Crop };
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
