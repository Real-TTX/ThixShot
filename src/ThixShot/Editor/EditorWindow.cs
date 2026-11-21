using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;
using ThixShot.Library;
using ThixShot.Studio;

namespace ThixShot.Editor;

internal sealed class EditorWindow : Window
{
    private readonly ImageDocument document;
    private readonly ImageSurface surface;
    private readonly TextBlock status = Ui.Text("", 12, Ui.Muted);
    private readonly Dictionary<MarkTool, Button> tools = [];
    private readonly Button undo;

    public EditorWindow(Stream source, bool sample = false)
    {
        document = new ImageDocument(source);
        surface = new ImageSurface(document);
        Title = sample ? "Beispielbild bearbeiten · ThixShot" : "Screenshot bearbeiten · ThixShot";
        Width = 1000; Height = 720; MinWidth = 740; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = Ui.WindowIcon();
        var toolRow = Ui.Row(5);
        foreach (var (mode, name, icon) in new[] { (MarkTool.Highlight,"Markieren","highlight"), (MarkTool.Arrow,"Pfeil","arrow"),
                     (MarkTool.Rectangle,"Rahmen","box"), (MarkTool.Crop,"Ausschneiden","crop") })
        {
            tools[mode] = Ui.Button(name, icon, () => Select(mode));
            toolRow.Children.Add(tools[mode]);
        }
        var colors = Ui.Choices(["Gold", "Koralle", "Blau", "Grün"], 0, i =>
            surface.MarkColor = SKColor.Parse(new[] { "#FFB729", "#EF544A", "#3575E6", "#2DA476" }[i]));
        ToolTip.SetTip(colors, "Markierungsfarbe");
        toolRow.Children.Add(colors);
        undo = Ui.Button("Zurücknehmen", "undo", Undo);
        toolRow.Children.Add(undo);
        var save = Ui.Button("In Mediathek speichern", "save", Save, "primary");
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20), RowSpacing = 14 };
        var heading = Ui.Stack(12, Ui.Text(Title.Split('·')[0].Trim(), 23, weight: FontWeight.SemiBold), toolRow);
        var canvas = Ui.Card(surface, 8); canvas.Background = Ui.Brush("#312E2C");
        var actions = Ui.Columns("Auto,*", Ui.Row(8, save, Ui.Button("Schließen", null, Close)), status);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(canvas, 1); Grid.SetRow(actions, 2);
        grid.Children.Add(heading); grid.Children.Add(canvas); grid.Children.Add(actions);
        Content = grid;
        surface.Submitted = mark => { if (ImageEditor.Add(document, mark)) Update(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { Undo(); e.Handled = true; }
            if (e.Key == Key.Escape) Close();
        };
        Closed += (_, _) => { surface.Dispose(); document.Dispose(); };
        Select(MarkTool.Highlight);
        Update(false);
    }

    private void Select(MarkTool mode)
    {
        surface.Tool = mode;
        foreach (var (tool, button) in tools) button.Classes.Set("active", tool == mode);
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
        var hint = Ui.Card(Ui.Text("Bereich mit der Maus aufziehen    ·    Esc zum Abbrechen", 14), 12);
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
