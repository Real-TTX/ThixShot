using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MatDesktop;
using ThixShot.Capture;
using ThixShot.Library;
using ThixShot.Studio;

namespace ThixShot.Video;

internal sealed class VideoEditorWindow : MatWindow
{
    private readonly string source;
    private readonly VideoInfo info;
    private VideoProject saved;
    private readonly List<ZoomPoint> points;
    private readonly Stack<ZoomPoint[]> undo = new();
    private readonly Stack<ZoomPoint[]> redo = new();
    private readonly VideoCanvas preview = new();
    private readonly ZoomTimeline timeline;
    private readonly ComboBox selection = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown start, duration, scale;
    private readonly Button target, remove, play, undoButton, redoButton, save, export;
    private readonly TextBlock timestamp = Ui.Text("", 13), state = Ui.Text("", 13, Ui.Muted);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(85) };
    private readonly Stopwatch clock = new();
    private readonly Control properties;
    private double position, playbackStart;
    private int selected;
    private bool syncing, picking, decoding, closing, allowClose, working;
    private double? requested;
    private CancellationTokenSource? exportCancellation;

    public VideoEditorWindow(string path, VideoInfo video, VideoProject project)
    {
        source = MediaLibrary.RequireItem(path);
        info = video; saved = project; points = [.. project.Zooms];
        selected = points.Count == 0 ? -1 : 0;
        Title = $"{Path.GetFileNameWithoutExtension(path)} · Videoeditor · ThixShot";
        Width = 1120; Height = 770; MinWidth = 800; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseOwnTitleBar();
        var title = new MatTitleBar
        {
            AppName = "ThixShot",
            Logo = Ui.Asset("Mark.png"),
            BrandWidth = 160,
            Center = Ui.Stack(0, Ui.Text("Videoeditor", 17, weight: FontWeight.SemiBold), Ui.Text(Path.GetFileName(source), 12, Ui.Muted))
        };
        var commandBar = Ui.Row(6);
        play = Ui.Button("Abspielen", "play", TogglePlayback);
        ToolTip.SetTip(play, "Stumme Bildvorschau starten oder anhalten (Leertaste)");
        undoButton = Ui.Button("Rückgängig", "undo", () => Restore(undo, redo));
        redoButton = Ui.Button("Wiederholen", null, () => Restore(redo, undo));
        commandBar.Children.Add(play); commandBar.Children.Add(timestamp);
        var undoBar = Ui.Row(5, undoButton, redoButton);
        var transport = Ui.Columns("*,Auto", commandBar, undoBar);
        var surface = new Border
        {
            Background = Ui.Brush("#151B25"),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Child = preview,
            ClipToBounds = true
        };
        var center = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(18) };
        center.Children.Add(surface);
        Grid.SetRow(transport, 1); center.Children.Add(transport);
        timeline = new ZoomTimeline
        {
            Duration = info.DurationSeconds,
            Height = 106,
            Zooms = points,
            Seek = t => { Pause(); SetTime(t); },
            SelectZoom = SelectZoom,
            BeginChange = Remember,
            MoveZoom = (i, z) => { if (i < 0 || i >= points.Count) return; points[i] = z; Sync(); SetTime(z.StartSeconds + z.DurationSeconds / 2); }
        };
        AutomationProperties.SetName(timeline, "Video- und Zoomspur");
        ToolTip.SetTip(timeline, "Bildspur zum Spulen anklicken. Zoomabschnitte verschieben, an den Enden ihre Dauer anpassen.");
        var strip = new Border { Child = timeline, ClipToBounds = true, CornerRadius = new CornerRadius(7) };
        Grid.SetRow(strip, 2); center.Children.Add(strip);
        start = Number("Start in Sekunden", 0, (decimal)video.DurationSeconds, .1m, "0.00");
        duration = Number("Dauer in Sekunden", .001m, (decimal)video.DurationSeconds, .1m, "0.00");
        scale = Number("Vergrößerung", 1, 16, .1m, "0.0");
        start.ValueChanged += (_, _) => ApplyNumbers();
        duration.ValueChanged += (_, _) => ApplyNumbers();
        scale.ValueChanged += (_, _) => ApplyNumbers();
        selection.SelectionChanged += (_, _) => { if (!syncing && selection.SelectedIndex >= 0) SelectZoom(selection.SelectedIndex); };
        target = Ui.Button("Fokus im Bild setzen", "studio", () =>
        {
            Pause(); picking = !picking; Sync(); SetHint(picking ? "In der Vorschau auf das Zoomziel klicken." : "");
        });
        preview.PlaceTarget = (x, y) =>
        {
            if (selected < 0) return;
            Remember(); points[selected] = points[selected] with { X = x, Y = y }; picking = false; Sync();
            SetHint("Zoomziel gesetzt.");
        };
        remove = Ui.Button("Zoom entfernen", "trash", DeleteZoom).Themed("MatButton.Danger");
        var plus = Ui.Button("Zoom hinzufügen", "plus", AddZoom, "primary");
        ToolTip.SetTip(plus, "An der aktuellen Abspielposition einfügen (Z)");
        properties = Ui.Stack(10, Field("Abschnitt", selection), Field("Start (s)", start), Field("Dauer (s)", duration),
            Field("Vergrößerung (×)", scale), target, remove);
        var inspector = Ui.Stack(14, new TextBlock { Text = "Zoom" }.Themed("MatText.SectionTitle"),
            Wrap("Setze Zooms an der Abspielposition und wähle ihr Ziel direkt im Bild."), plus, properties);
        if (!project.RecordedWithMaster)
            inspector.Children.Add(Wrap("Ältere Aufnahme. Bereits gerenderte Zooms gehören zum Ausgangsbild."));
        var side = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = inspector } }
            .Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.Border");
        var content = Ui.Columns("*,248", center, side);
        save = Ui.Button("Projekt speichern", "save", Save, "primary");
        export = Ui.Button("MP4 exportieren", "download", async () => await ExportAsync());
        var close = Ui.Button("Schließen", null, Close);
        var actions = Ui.Row(7, save, export, close);
        state.TextTrimming = TextTrimming.CharacterEllipsis;
        var footer = new Border { Child = Ui.Columns("Auto,*", actions, state), Padding = new Thickness(18, 10), BorderThickness = new Thickness(0, 1, 0, 0) }
            .Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.Border");
        state.Margin = new Thickness(12, 0, 0, 0);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(title); Grid.SetRow(content, 1); layout.Children.Add(content); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        Sync();
        timer.Tick += (_, _) =>
        {
            double next = playbackStart + clock.Elapsed.TotalSeconds;
            SetTime(Math.Min(info.DurationSeconds, next));
            if (next >= info.DurationSeconds) Pause();
        };
        Opened += async (_, _) => { SetTime(0); await LoadFilmstripAsync(); };
        KeyDown += HandleKey;
        Closing += (_, e) =>
        {
            if (allowClose) return;
            if (working) { e.Cancel = true; exportCancellation?.Cancel(); return; }
            if (!Dirty) return;
            e.Cancel = true;
            if (!closing) Dispatcher.UIThread.Post(async () => await ConfirmCloseAsync());
        };
        Closed += (_, _) =>
        {
            Pause(); lifetime.Cancel(); preview.SetFrame(null);
            foreach (var frame in timeline.Frames) frame?.Dispose();
            lifetime.Dispose();
            (title.Logo as IDisposable)?.Dispose();
        };
    }

    private bool Dirty => !points.OrderBy(p => p.StartSeconds).SequenceEqual(saved.Zooms.OrderBy(p => p.StartSeconds));
    private static Control Field(string label, Control input) => Ui.Stack(4, new TextBlock { Text = label }.Themed("MatText.FieldLabel"), input);
    private static TextBlock Wrap(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted");
    private static NumericUpDown Number(string label, decimal min, decimal max, decimal increment, string format)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = Math.Max(min, max),
            Increment = increment,
            FormatString = format,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        AutomationProperties.SetName(n, label); return n;
    }
    private void Remember() { undo.Push(points.ToArray()); redo.Clear(); }
    private void Restore(Stack<ZoomPoint[]> from, Stack<ZoomPoint[]> to)
    {
        if (working || !from.TryPop(out var snapshot)) return;
        to.Push(points.ToArray()); points.Clear(); points.AddRange(snapshot);
        selected = Math.Clamp(selected, -1, points.Count - 1); picking = false; Sync();
    }
    private void SelectZoom(int index)
    {
        if (index < 0 || index >= points.Count) return;
        Pause(); selected = index; picking = false; Sync();
        SetTime(points[index].StartSeconds + points[index].DurationSeconds / 2);
    }
    private void AddZoom()
    {
        if (working) return;
        Pause(); Remember();
        double length = Math.Min(2, info.DurationSeconds);
        var at = Math.Min(position, Math.Max(0, info.DurationSeconds - Math.Min(.2, length)));
        var point = new ZoomPoint(at, Math.Min(length, info.DurationSeconds - at), .5, .5, 1.8);
        points.Add(point); SelectZoom(points.Count - 1); picking = true; Sync();
        SetHint("In der Vorschau auf das Zoomziel klicken.");
    }
    private void DeleteZoom()
    {
        if (working || selected < 0) return;
        Remember(); points.RemoveAt(selected); selected = Math.Min(selected, points.Count - 1); picking = false; Sync();
    }
    private void ApplyNumbers()
    {
        if (syncing || working || selected < 0 || start.Value == null || duration.Value == null || scale.Value == null) return;
        double min = Math.Min(.05, info.DurationSeconds);
        double at = Math.Clamp((double)start.Value, 0, info.DurationSeconds - min);
        var before = points[selected];
        var next = before with { StartSeconds = at, DurationSeconds = Math.Clamp((double)duration.Value, min, info.DurationSeconds - at), Scale = (double)scale.Value };
        if (next == before) return;
        Remember(); points[selected] = next; Pause(); Sync(); SetTime(at + next.DurationSeconds / 2);
    }
    private void Sync()
    {
        syncing = true;
        try
        {
            selection.ItemsSource = points.Select((p, i) => $"{i + 1}   ·   {ZoomTimeline.Timecode(p.StartSeconds)}   ·   {p.Scale:0.#}×").ToArray();
            selection.SelectedIndex = selected;
            properties.IsVisible = selected >= 0;
            if (selected >= 0)
            {
                var point = points[selected];
                start.Value = (decimal)point.StartSeconds; duration.Value = (decimal)point.DurationSeconds; scale.Value = (decimal)point.Scale;
            }
            preview.Target = picking && selected >= 0 ? points[selected] : null;
            preview.Cursor = new Cursor(picking ? StandardCursorType.Cross : StandardCursorType.Arrow);
            preview.Zooms = points; timeline.SelectedIndex = selected; timeline.InvalidateVisual(); preview.InvalidateVisual();
            undoButton.IsEnabled = undo.Count > 0; redoButton.IsEnabled = redo.Count > 0; save.IsEnabled = Dirty;
            if (!working) SetHint(Dirty ? "Ungespeicherte Änderungen" : $"{info.Width} × {info.Height}  ·  {points.Count} Zooms");
        }
        finally { syncing = false; }
    }
    private void SetHint(string text) { state.Text = text; ToolTip.SetTip(state, text); }
    private void Pause() { timer.Stop(); clock.Reset(); play.Content = "▶  Abspielen"; }
    private void TogglePlayback()
    {
        if (timer.IsEnabled) { Pause(); return; }
        if (working) return;
        if (position >= info.DurationSeconds - .03) SetTime(0);
        picking = false; Sync(); playbackStart = position; clock.Restart(); timer.Start(); play.Content = "Ⅱ  Pause";
    }
    private void SetTime(double seconds)
    {
        position = Math.Clamp(seconds, 0, info.DurationSeconds);
        timestamp.Text = $"{ZoomTimeline.Timecode(position)}  /  {ZoomTimeline.Timecode(info.DurationSeconds)}";
        timeline.Seconds = position; timeline.InvalidateVisual(); preview.Seconds = position; preview.InvalidateVisual();
        requested = Math.Min(position, Math.Max(0, info.DurationSeconds - .035));
        if (!decoding) _ = DecodeAsync();
    }
    private async Task DecodeAsync()
    {
        decoding = true;
        var ct = lifetime.Token;
        try
        {
            while (requested is { } at && !ct.IsCancellationRequested)
            {
                requested = null;
                var image = await SampleAsync(at, 1600, ct);
                if (ct.IsCancellationRequested) { image.Dispose(); break; }
                preview.SetFrame(image);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { Pause(); SetHint(error.Message); }
        finally { decoding = false; }
    }
    private async Task<Bitmap> SampleAsync(double seconds, int width, CancellationToken ct)
    {
        var data = await VideoEditService.FrameAsync(source, seconds, ct).ConfigureAwait(false);
        using var stream = new MemoryStream(data, false);
        return Bitmap.DecodeToWidth(stream, Math.Min(width, info.Width));
    }
    private async Task LoadFilmstripAsync()
    {
        var ct = lifetime.Token;
        try
        {
            for (int i = 0; i < timeline.Frames.Length; i++)
            {
                var frame = await SampleAsync(info.DurationSeconds * (i + .5) / timeline.Frames.Length, 150, ct);
                if (ct.IsCancellationRequested) { frame.Dispose(); break; }
                timeline.Frames[i] = frame; timeline.InvalidateVisual();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { SetHint(error.Message); }
    }
    private void Save()
    {
        try { saved = VideoProjects.Save(source, saved, points); Sync(); SetHint("Videoprojekt gespeichert."); }
        catch (Exception error) { SetHint(error.Message); }
    }
    private async Task ExportAsync()
    {
        if (working) { exportCancellation?.Cancel(); return; }
        Pause();
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "MP4 mit Zooms exportieren",
            SuggestedFileName = Path.GetFileNameWithoutExtension(source) + " – Export.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MP4-Video") { Patterns = ["*.mp4"] }]
        });
        if (destination == null) return;
        var output = destination.TryGetLocalPath();
        if (output == null) { SetHint("Bitte einen lokalen Exportpfad auswählen."); return; }
        output = Path.GetFullPath(output);
        if (Path.GetRelativePath(MediaLibrary.Folder, output).Split(Path.DirectorySeparatorChar)[0] != ".." && !Path.IsPathRooted(Path.GetRelativePath(MediaLibrary.Folder, output)))
        { SetHint("Wähle für den Export einen Speicherort außerhalb der Mediathek."); return; }
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".thixshot-" + Guid.NewGuid().ToString("N") + ".mp4");
        working = true; export.Content = "Export abbrechen"; save.IsEnabled = false;
        var content = (Grid)Content!; content.Children[1].IsEnabled = false;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        exportCancellation = cancel;
        try
        {
            var zooms = points.ToArray();
            if (zooms.Length > 0)
                await VideoEditService.ExportAsync(source, temporary, zooms, new Progress<double>(v => { if (working) SetHint($"MP4 wird gerendert · {v:P0}"); }), cancel.Token);
            else
            {
                await using var input = File.OpenRead(source);
                await using var file = File.Create(temporary);
                await input.CopyToAsync(file, cancel.Token);
            }
            cancel.Token.ThrowIfCancellationRequested(); File.Move(temporary, output, true);
            SetHint($"Exportiert · {Path.GetFileName(output)}");
        }
        catch (OperationCanceledException) { SetHint("Export abgebrochen."); }
        catch (Exception error) { SetHint(error.Message); }
        finally
        {
            working = false; exportCancellation = null; export.Content = "MP4 exportieren"; save.IsEnabled = Dirty;
            content.Children[1].IsEnabled = true; File.Delete(temporary);
        }
    }
    private async Task ConfirmCloseAsync()
    {
        if (closing || !IsVisible) return;
        closing = true; Pause();
        try
        {
            var dialog = new MatWindow
            {
                Title = "Videoprojekt schließen",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var actions = Ui.Row(8, Ui.Button("Speichern", null, () => dialog.Close(1), "primary"),
                Ui.Button("Weiter bearbeiten", null, () => dialog.Close(0)), Ui.Button("Verwerfen", null, () => dialog.Close(2)));
            dialog.Content = new Border { Padding = new Thickness(20), Child = Ui.Stack(16, Wrap("Änderungen an den Zooms speichern?"), actions) };
            int answer = await dialog.ShowDialog<int>(this);
            if (answer == 1) Save();
            if (answer == 2 || answer == 1 && !Dirty) { allowClose = true; Close(); }
        }
        finally { closing = false; }
    }
    private void HandleKey(object? sender, KeyEventArgs e)
    {
        if (working || e.Source is TextBox) return;
        bool command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        if (e.Key == Key.Space) TogglePlayback();
        else if (e.Key == Key.Z && command && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Restore(redo, undo);
        else if (e.Key == Key.Z && command) Restore(undo, redo);
        else if (e.Key == Key.S && command) Save();
        else if (e.Key == Key.Z) AddZoom();
        else if (e.Key == Key.Delete) DeleteZoom();
        else if (e.Key == Key.Left || e.Key == Key.Right) { Pause(); SetTime(position + (e.Key == Key.Right ? 1 : -1) / 30d); }
        else if (e.Key == Key.Escape) { if (picking) { picking = false; Sync(); } else Close(); }
        else return;
        e.Handled = true;
    }
}
