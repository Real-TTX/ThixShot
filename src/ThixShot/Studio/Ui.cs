using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace ThixShot.Studio;

internal static class Ui
{
    public static readonly IBrush Ink = Brush("#292625"), Muted = Brush("#6D6661"), Line = Brush("#E3DFDB"), Accent = Brush("#BA493D");
    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    public static TextBlock Text(string text, double size = 14, IBrush? color = null, FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = color ?? Ink, FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
    public static StackPanel Stack(double spacing = 8, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
    public static StackPanel Row(double spacing, params Control[] children)
    {
        var row = Stack(spacing, children);
        row.Orientation = Orientation.Horizontal;
        return row;
    }
    public static Grid Columns(string widths, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths) };
        for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); }
        return grid;
    }
    public static Border Card(Control content, double padding = 20) => new()
    {
        Child = content, Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1),
        Padding = new Thickness(padding), CornerRadius = new CornerRadius(14), ClipToBounds = true
    };
    public static Control Icon(string key, double size = 18, IBrush? color = null)
    {
        var path = new ShapePath { Data = Geometry.Parse(Paths[key]), Stroke = color ?? Ink, StrokeThickness = 1.65,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        var canvas = new Canvas { Width = 24, Height = 24, Children = { path } };
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }
    public static Button Button(string label, string? icon, Action action, string? style = null)
    {
        var color = style == "primary" ? Brushes.White : null;
        var result = new Button { Content = icon is null ? label : Row(8, Icon(icon, 17, color), Text(label, 13, color)) };
        if (style != null) result.Classes.Add(style);
        result.Click += (_, _) => action();
        return result;
    }
    public static CheckBox Check(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = value, MinHeight = 28, FontSize = 13 };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }
    public static ComboBox Choices(string[] values, int selected, Action<int> changed)
    {
        var combo = new ComboBox { ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13 };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        return combo;
    }
    public static Bitmap Asset(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://ThixShot/Assets/{name}"));
        return new Bitmap(stream);
    }
    public static WindowIcon WindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://ThixShot/Assets/ThixShot.ico"));
        return new WindowIcon(stream);
    }
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["studio"]="M3 8V5q0-2 2-2h3 M16 3h3q2 0 2 2v3 M21 16v3q0 2-2 2h-3 M8 21H5q-2 0-2-2v-3 M8 12a4 4 0 1 0 8 0a4 4 0 1 0-8 0",
        ["library"]="M3 4h5v16H3z M10 4h4v16h-4z M16 6l4-1 3 14-4 1z",
        ["cloud"]="M7 18H6a4.5 4.5 0 0 1-.6-9 7 7 0 0 1 13.3-1.4A5.2 5.2 0 0 1 18 18h-1 M12 21V12m-3 3 3-3 3 3",
        ["settings"]="M3 7h6m4 0h8M3 17h11m4 0h3 M9 7a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M14 17a2 2 0 1 0 4 0a2 2 0 1 0-4 0",
        ["camera"]="M3 7h4l2-3h6l2 3h4v13H3z M8 13a4 4 0 1 0 8 0a4 4 0 1 0-8 0",
        ["video"]="M3 6h12v12H3z M15 10l6-3v10l-6-3",
        ["screen"]="M3 4h18v13H3z M12 17v4m-4 0h8",
        ["crop"]="M6 2v16h16M2 6h16v16",
        ["time"]="M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 7v5l3 2",
        ["right"]="M8 5l7 7-7 7", ["arrow"]="M4 12h15m-6-6 6 6-6 6",
        ["edit"]="M3 21l5-1 13-13-4-4L4 16z M14 6l4 4",
        ["folder"]="M3 6h6l2 3h10v11H3z", ["save"]="M4 4h13l3 3v13H4z M8 4v6h8V4 M8 20v-6h8v6",
        ["undo"]="M8 5 3 10l5 5 M4 10h9a6 6 0 0 1 0 12", ["box"]="M4 5h16v14H4z",
        ["highlight"]="M4 14l9-10 6 5-9 10z M4 14l-2 6 7-1 M13 21h8",
        ["trash"]="M3 6h18M8 6V3h8v3 M6 6l1 15h10l1-15 M10 10v7m4-7v7",
        ["back"]="M20 12H4m6-6-6 6 6 6", ["stop"]="M6 6h12v12H6z",
        ["info"]="M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 11v6m0-10v1",
        ["download"]="M12 3v12m-5-5 5 5 5-5 M4 17v4h16v-4", ["copy"]="M8 8h12v13H8z M16 8V3H3v14h5",
        ["play"]="M7 4l13 8L7 20z", ["image"]="M3 4h18v16H3z M3 17l6-6 5 5 3-3 4 4 M15 8h1",
        ["record"]="M4 12a8 8 0 1 0 16 0a8 8 0 1 0-16 0 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
        ["search"]="M3 10a7 7 0 1 0 14 0a7 7 0 1 0-14 0 M15 15l6 6",
        ["code"]="M8 7 3 12l5 5m8-10 5 5-5 5", ["keyboard"]="M2 6h20v12H2z M5 10h1m3 0h1m3 0h1m3 0h1M7 14h10"
    };
}
