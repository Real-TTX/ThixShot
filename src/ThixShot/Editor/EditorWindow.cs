using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using SkiaSharp;
using ThixShot.Studio;

namespace ThixShot.Editor;

internal sealed partial class EditorWindow : MatWindow
{
    private static readonly (MarkTool Tool, string Label, string Icon, Key Key, string Hint)[] ToolSet =
    [
        (MarkTool.Highlight, "Markieren", "highlight", Key.M, "Bereich zum Hervorheben aufziehen."),
        (MarkTool.Arrow, "Pfeil", "arrow", Key.A, "Vom Anfang zur Pfeilspitze ziehen."),
        (MarkTool.Rectangle, "Rahmen", "rectangle", Key.R, "Rahmen um einen Bereich ziehen."),
        (MarkTool.Pen, "Freihand", "edit", Key.P, "Mit gedrückter Maustaste zeichnen."),
        (MarkTool.Text, "Text", "type", Key.T, "Ins Bild klicken, um Text zu platzieren."),
        (MarkTool.Crop, "Ausschneiden", "crop", Key.X, "Bereich aufziehen und zum Zuschneiden loslassen."),
        (MarkTool.Pan, "Verschieben", "pan", Key.H, "Vergrößerten Bildausschnitt mit der Maus verschieben.")
    ];

    private readonly ImageDocument document;
    private readonly ImageSurface surface;
    private readonly Dictionary<MarkTool, Button> buttons = [];
    private readonly Button undo;
    private readonly Button redo;
    private readonly TextBlock hint = Ui.Text("", 13, Ui.Muted);
    private readonly TextBlock dimensions = Ui.Text("", 13, Ui.Muted);
    private readonly TextBlock zoom = Ui.Text("", 13);
    private readonly StackPanel colorProperties = Ui.Stack(7);
    private readonly StackPanel strokeProperties = Ui.Stack(7);
    private readonly StackPanel textProperties = Ui.Stack(7);
    private readonly TextBox textInput = new() { Watermark = "Beschriftung", AcceptsReturn = true, MaxLength = 2000,
        MinHeight = 50, MaxHeight = 92, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<SKColor, Button> colorChips = [];
    private readonly Button applyText;
    private SKPoint? textAnchor;
    private float textSize;
    private ImageMark[] savedState = [];
    private bool canClose;
    private bool confirming;
    private bool outputBusy;

    public EditorWindow(Stream source, bool sample = false)
    {
        document = new ImageDocument(source);
        surface = new ImageSurface(document);
        textSize = Math.Clamp(document.Original.Height / 30f, 14, 48);
        Title = sample ? "Beispielbild bearbeiten · ThixShot" : "Screenshot bearbeiten · ThixShot";
        Width = 1110; Height = 740; MinWidth = 790; MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseOwnTitleBar();
        var logo = Ui.Asset("Mark.png");
        var title = new MatTitleBar { AppName = "ThixShot", Logo = logo, BrandWidth = 192,
            Center = Ui.Text(sample ? "Beispielbild bearbeiten" : "Screenshot bearbeiten", 15, weight: FontWeight.SemiBold) };
        undo = Ui.Button("Zurücknehmen", "undo", Undo, "flat");
        redo = IconAction("Wiederholen", "redo", Redo, Shortcut("Umschalt+Z"));
        ToolTip.SetTip(undo, $"Zurücknehmen ({Shortcut("Z")})");
        applyText = Ui.Button("Text übernehmen", "check", CommitText);
        var panel = CreateInspector();
        var workspace = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var zoomControls = Ui.Row(2, IconAction("Verkleinern", "minus", () => StepZoom(-1)), zoom,
            IconAction("Vergrößern", "plus", () => StepZoom(1)), Divider(),
            Ui.Button("Einpassen", null, surface.Fit, "flat"), Ui.Button("100 %", null, () => surface.ZoomTo(100), "flat"));
        zoom.Width = 49; zoom.TextAlignment = TextAlignment.Center;
        zoomControls.HorizontalAlignment = HorizontalAlignment.Right;
        var toolbar = Strip(Ui.Columns("Auto,*", Ui.Row(2, undo, redo), zoomControls), new Thickness(14, 8), false);
        var canvas = new Border { Child = surface, ClipToBounds = true }.Use(Border.BackgroundProperty, "Mat.Surface2");
        var hints = Strip(Ui.Columns("*,Auto", hint, dimensions), new Thickness(18, 8), true);
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        dimensions.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetRow(canvas, 1); Grid.SetRow(hints, 2);
        workspace.Children.Add(toolbar); workspace.Children.Add(canvas); workspace.Children.Add(hints);
        var content = Ui.Columns("194,*", panel, workspace);
        var footer = Strip(Ui.Row(8, Ui.Button("In Mediathek speichern", "save", SaveLibrary, "primary"),
            Ui.Button("Kopieren", "copy", () => _ = CopyAsync()), Ui.Button("PNG exportieren", "download", () => _ = ExportAsync())),
            new Thickness(20, 12), true);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(content, 1); Grid.SetRow(footer, 2);
        root.Children.Add(title); root.Children.Add(content); root.Children.Add(footer);
        Content = root;

        surface.Submitted = mark => { if (ImageEditor.Add(document, mark)) Refresh(); };
        surface.TextRequested = point =>
        {
            if (textAnchor.HasValue) CommitText();
            textAnchor = point;
            textInput.IsEnabled = true;
            textInput.Focus();
            PreviewText();
        };
        surface.ViewChanged = RefreshView;
        textInput.TextChanged += (_, _) => PreviewText();
        textInput.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            { CommitText(); surface.Focus(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        KeyDown += HandleKey;
        Closing += (_, args) =>
        {
            if (canClose) return;
            if (outputBusy) { args.Cancel = true; return; }
            if (!HasChanges) return;
            args.Cancel = true;
            if (!confirming) _ = ConfirmCloseAsync();
        };
        Closed += (_, _) => { surface.Dispose(); document.Dispose(); logo.Dispose(); };
        Select(MarkTool.Highlight);
        Refresh();
    }

    private static string Shortcut(string key) => (OperatingSystem.IsMacOS() ? "⌘+" : "Strg+") + key;

    private static Border Strip(Control child, Thickness padding, bool lineAbove) =>
        new Border { Child = child, Padding = padding, BorderThickness = new Thickness(0, lineAbove ? 1 : 0, 0, lineAbove ? 0 : 1) }
            .Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.Border");

    private static Border Divider() => new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0) }
        .Use(Border.BackgroundProperty, "Mat.Border");

    private Control CreateInspector()
    {
        var tools = Ui.Stack(3);
        foreach (var (mode, label, icon, key, _) in ToolSet)
        {
            var name = Ui.Text(label, 13, weight: FontWeight.Medium);
            var keyLabel = Ui.Text(key.ToString(), 11, Ui.Muted);
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5), Content = Ui.Columns("*,Auto", Ui.Row(9, EditorIcon(icon), name), keyLabel) }.Themed("MatButton.Ghost");
            button.Click += (_, _) => Select(mode);
            button.PointerEntered += (_, _) => PaintTool(mode);
            button.PointerExited += (_, _) => PaintTool(mode);
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, $"{label} ({key})");
            buttons.Add(mode, button);
            tools.Children.Add(button);
        }
        var colors = Ui.Row(3);
        foreach (var (label, hex) in new[] { ("Gold", "#FFB729"), ("Koralle", "#EC5963"), ("Blau", "#367CE5"),
            ("Grün", "#34AA76"), ("Schwarz", "#262833"), ("Weiß", "#FFFFFF") })
        {
            var color = SKColor.Parse(hex);
            var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                Background = Ui.Brush(hex), BorderThickness = new Thickness(1) }.Use(Border.BorderBrushProperty, "Mat.BorderStrong");
            var button = new Button { Width = 25, Height = 26, MinHeight = 0, Padding = new Thickness(0), Content = swatch,
                CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(2) }.Themed("MatButton.Ghost");
            button.Click += (_, _) => SetColor(color);
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, label);
            colorChips.Add(color, button);
            colors.Children.Add(button);
        }
        colorProperties.Children.Add(Ui.Text("Farbe", 13, Ui.Muted));
        colorProperties.Children.Add(colors);
        surface.StrokeWidth = MathF.Round(surface.StrokeWidth);
        var width = new NumericUpDown { Value = (decimal)surface.StrokeWidth, Minimum = 1, Maximum = 64, Increment = 1,
            FormatString = "0 'px'", HorizontalAlignment = HorizontalAlignment.Stretch };
        width.ValueChanged += (_, _) => surface.StrokeWidth = (float)(width.Value ?? 1);
        strokeProperties.Children.Add(Ui.Text("Strichstärke", 13, Ui.Muted));
        strokeProperties.Children.Add(width);
        var size = new NumericUpDown { Value = (decimal)Math.Round(textSize), Minimum = 6, Maximum = 200, Increment = 2, FormatString = "0 'px'",
            HorizontalAlignment = HorizontalAlignment.Stretch };
        size.ValueChanged += (_, _) => { textSize = (float)(size.Value ?? 14); PreviewText(); };
        textProperties.Children.Add(Ui.Text("Schriftgröße", 13, Ui.Muted)); textProperties.Children.Add(size);
        textProperties.Children.Add(textInput); textProperties.Children.Add(applyText);
        AutomationProperties.SetName(textInput, "Beschriftung");
        AutomationProperties.SetName(width, "Strichstärke"); AutomationProperties.SetName(size, "Schriftgröße");
        SetColor(surface.MarkColor);
        var rule = new Border { Height = 1 }.Use(Border.BackgroundProperty, "Mat.Border");
        var inspector = Ui.Stack(14, Ui.Text("Werkzeuge", 15, weight: FontWeight.SemiBold), tools, rule,
            colorProperties, strokeProperties, textProperties);
        inspector.Margin = new Thickness(14, 18);
        return new Border { BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = inspector } }
            .Use(Border.BackgroundProperty, "Mat.NavBg").Use(Border.BorderBrushProperty, "Mat.Border");
    }

    private static Control EditorIcon(string icon)
    {
        if (icon == "redo")
        {
            var control = Ui.Icon("undo", 16);
            control.RenderTransform = new ScaleTransform(-1, 1);
            return control;
        }
        if (icon is not ("pan" or "rectangle")) return Ui.Icon(icon, 16);
        return new Avalonia.Controls.Shapes.Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.65,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Data = Geometry.Parse(icon == "rectangle" ? "M3 5h18v14H3z" :
                "M8 13V6a1.5 1.5 0 0 1 3 0v5 M11 9V4a1.5 1.5 0 0 1 3 0v7 M14 9V6a1.5 1.5 0 0 1 3 0v6 M17 10a1.5 1.5 0 0 1 3 0v5q0 6-6 6h-1q-3 0-5-3l-4-6a1.5 1.5 0 0 1 2-2l2 3") }
            .Use(Avalonia.Controls.Shapes.Path.StrokeProperty, "Mat.Text");
    }

    private static Button IconAction(string label, string icon, Action action, string? shortcut = null)
    {
        var button = new Button { Content = EditorIcon(icon) }.Themed("MatButton.Icon");
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, shortcut == null ? label : $"{label} ({shortcut})");
        button.Click += (_, _) => action();
        return button;
    }

    private void Select(MarkTool mode)
    {
        surface.CancelGesture();
        if (surface.Tool == MarkTool.Text && mode != MarkTool.Text) CommitText();
        surface.Tool = mode;
        foreach (var key in buttons.Keys) PaintTool(key);
        colorProperties.IsVisible = mode is not (MarkTool.Crop or MarkTool.Pan);
        strokeProperties.IsVisible = mode is MarkTool.Pen or MarkTool.Rectangle or MarkTool.Arrow;
        textProperties.IsVisible = mode == MarkTool.Text;
        textInput.IsEnabled = textAnchor.HasValue;
        RefreshView();
    }

    private void PaintTool(MarkTool mode)
    {
        var button = buttons[mode];
        button.Use(Button.BackgroundProperty, mode == surface.Tool ? "Mat.AccentSoft" : button.IsPointerOver ? "Mat.Surface2" : "Mat.NavBg");
        button.Use(Button.BorderBrushProperty, mode == surface.Tool ? "Mat.Accent" : button.IsPointerOver ? "Mat.Border" : "Mat.NavBg");
    }

    private void SetColor(SKColor color)
    {
        surface.MarkColor = color;
        foreach (var (value, button) in colorChips)
            button.Use(Button.BorderBrushProperty, value == color ? "Mat.Accent" : "Mat.NavBg");
        PreviewText();
    }

    private ImageMark? Draft => textAnchor is { } anchor && !string.IsNullOrWhiteSpace(textInput.Text)
        ? new ImageMark(MarkTool.Text, anchor, anchor, surface.MarkColor, textSize, textInput.Text) : null;
    private bool HasChanges => Draft.HasValue || !document.History.SequenceEqual(savedState);

    private void PreviewText()
    {
        surface.TextPreview = Draft;
        applyText.IsEnabled = Draft.HasValue;
    }

    private void CommitText()
    {
        var draft = Draft;
        ClearText();
        if (draft.HasValue && ImageEditor.Add(document, draft.Value)) Refresh();
    }

    private void ClearText()
    {
        textAnchor = null;
        textInput.Text = "";
        textInput.IsEnabled = false;
        PreviewText();
    }

    private void Refresh()
    {
        surface.Refresh();
        undo.IsEnabled = document.History.Count > 0;
        redo.IsEnabled = document.RedoStack.Count > 0;
        RefreshView();
    }

    private void RefreshView()
    {
        var box = surface.CropPreview ?? document.Visible;
        dimensions.Text = $"{box.Width} × {box.Height} px";
        zoom.Text = $"{surface.ZoomPercent:0.#} %";
        hint.Text = surface.CropPreview.HasValue ? "Auswahl zum Zuschneiden" : ToolSet.First(item => item.Tool == surface.Tool).Hint;
    }

    private void Undo() { CommitText(); ImageEditor.Undo(document); Refresh(); }
    private void Redo() { ClearText(); ImageEditor.Redo(document); Refresh(); }
    private void StepZoom(int direction) => surface.ZoomTo(surface.ZoomPercent * Math.Pow(1.25, direction));

    private void HandleKey(object? sender, KeyEventArgs e)
    {
        bool shortcut = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Escape)
        {
            if (surface.CancelGesture()) { e.Handled = true; return; }
            if (textAnchor.HasValue) { ClearText(); surface.Focus(); } else Close();
        }
        else if (e.Source is TextBox && !(shortcut && e.Key == Key.S)) return;
        else if (shortcut && e.Key == Key.Z) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo(); else Undo(); }
        else if (shortcut && e.Key == Key.Y) Redo();
        else if (shortcut && e.Key == Key.S) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _ = ExportAsync(); else SaveLibrary(); }
        else if (shortcut && e.Key == Key.C) _ = CopyAsync();
        else if (e.Key == Key.D0) surface.Fit();
        else if (e.Key == Key.D1) surface.ZoomTo(100);
        else if (!shortcut && ToolSet.FirstOrDefault(item => item.Key == e.Key) is { Label: not null } tool) Select(tool.Tool);
        else return;
        e.Handled = true;
    }
}
