using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ThixShot.Editor;
using ThixShot.Integration;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow : Window
{
    private readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock message = Ui.Text("", 13);
    private readonly Border notice;
    private readonly Dictionary<string, Button> nav = [];
    private readonly List<Bitmap> thumbnails = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> destinations = ["Aufnahmestudio", "Mediathek", "ThixCloud", "Einstellungen", "Über ThixShot"];
    private readonly TextBlock libraryCount = Ui.Text("0", 11, Ui.Muted);
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private string current = "Aufnahmestudio";
    private bool video, region, focusZoom = true, showCursor = true, systemAudio, busy, allowClose;
    private int delay;
    private string search = "";
    private int kind, sort;
    private string? selectedPath, pendingVersion;

    public StudioWindow()
    {
        Title = "ThixShot"; Width = 1160; Height = 774; MinWidth = 920; MinHeight = 600;
        Icon = Ui.WindowIcon();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("196,*") };
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(14, 25, 14, 14) };
        var brand = Ui.Row(10, new Image { Source = Ui.Asset("Mark.png"), Width = 37, Height = 37 },
            Ui.Stack(0, Ui.Text("ThixShot", 20, weight: FontWeight.Bold), Ui.Text("ThixApps.de", 11, Ui.Muted)));
        brand.Margin = new Thickness(8, 0, 0, 35);
        sidebar.Children.Add(brand);
        var menu = Ui.Stack(5);
        void AddNav(int i, string icon)
        {
            var title = destinations[i];
            var button = Ui.Button(title, icon, () => Navigate(title), "flat");
            button.Height = 40;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (i == 1 && button.Content is StackPanel row) row.Children.Add(libraryCount);
            nav[title] = button;
            menu.Children.Add(button);
        }
        AddNav(0, "studio"); AddNav(1, "library");
        menu.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(8, 14) });
        AddNav(2, "cloud"); AddNav(3, "settings");
        Grid.SetRow(menu, 1); sidebar.Children.Add(menu);
        var version = Ui.Button(UpdateService.DisplayVersion, "info", () => Navigate(destinations[4]), "flat");
        ToolTip.SetTip(version, "Über ThixShot"); Grid.SetRow(version, 3); sidebar.Children.Add(version);
        layout.Children.Add(new Border { Background = Ui.Brush("#F3F1EE"), BorderBrush = Ui.Line, BorderThickness = new Thickness(0,0,1,0), Child = sidebar });
        var main = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        main.Children.Add(page);
        message.TextWrapping = TextWrapping.Wrap;
        notice = Ui.Card(Ui.Columns("*,Auto", message, Ui.Button("Schließen", null, () => ShowMessage(""), "flat")), 9);
        notice.Margin = new Thickness(30, 0, 30, 12); notice.IsVisible = false;
        Grid.SetRow(notice, 1); main.Children.Add(notice);
        Grid.SetColumn(main, 1); layout.Children.Add(main); Content = layout;
        Navigate(current);
        Closing += (_, e) =>
        {
            if (busy && !allowClose) { e.Cancel = true; return; }
            updateTimer.Stop(); lifetime.Cancel();
        };
        Closed += (_, _) => { ClearImages(); lifetime.Dispose(); };
        KeyDown += (_, e) =>
        {
            if (busy || current != destinations[0]) return;
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            { e.Handled = true; Run(CaptureAsync); }
        };
        Opened += async (_, _) => { updateTimer.Start(); await AutomaticUpdateAsync(); };
        updateTimer.Tick += async (_, _) => await AutomaticUpdateAsync();
    }

    private void ClearImages()
    {
        foreach (var image in thumbnails) image.Dispose();
        thumbnails.Clear();
    }
    private void Navigate(string title)
    {
        if (busy) return;
        current = title;
        ShowMessage("");
        RefreshPage();
    }
    private void RefreshPage()
    {
        ClearImages();
        foreach (var (key, button) in nav) button.Classes.Set("active", key == current);
        try
        {
            var items = MediaLibrary.List();
            libraryCount.Text = items.Count.ToString();
            Control content = current switch
            {
                "Aufnahmestudio" => StudioPage(items),
                "Mediathek" => LibraryPage(items),
                "ThixCloud" => CloudPage(),
                "Einstellungen" => SettingsPage(),
                _ => AboutPage()
            };
            page.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(32, 27, 32, 27), Child = content } };
        }
        catch (Exception error) { ShowMessage(error.Message); }
    }
    private void ShowMessage(string text) { message.Text = text; notice.IsVisible = text.Length > 0; }
    private async void Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; ((Control)Content!).IsEnabled = false; ShowMessage("");
        try { await action(); }
        catch (OperationCanceledException) { ShowMessage("Vorgang abgebrochen."); }
        catch (Exception error) { ShowMessage(error.GetBaseException().Message); }
        finally
        {
            busy = false; ((Control)Content!).IsEnabled = true;
            if (!allowClose) RefreshPage();
        }
    }

    private static Control Heading(string title, string detail) =>
        Ui.Stack(4, Ui.Text(title, 31, weight: FontWeight.SemiBold), Ui.Text(detail, 14, Ui.Muted));

    private Control StudioPage(IReadOnlyList<MediaItem> items)
    {
        var modes = Ui.Row(3);
        foreach (var (name, icon, setting) in new[] { ("Screenshot","camera",false), ("Video","video",true) })
        {
            var button = Ui.Button(name, icon, () => { video = setting; RefreshPage(); }, "flat");
            button.Classes.Set("active", video == setting); modes.Children.Add(button);
        }
        var modeBox = Ui.Card(modes, 3); modeBox.CornerRadius = new CornerRadius(10); modeBox.HorizontalAlignment = HorizontalAlignment.Left;
        var preview = new Grid { MinHeight = 230, Children = { new StudioPreview { Region = region } } };
        var format = Ui.Text(video ? "MP4   /   H.264" : "PNG   /   BILD", 10, Brushes.White, FontWeight.SemiBold);
        var formatTag = new Border { Background = Ui.Brush("#80321F23"), CornerRadius = new CornerRadius(5), Padding = new Thickness(8,4),
            Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = format };
        preview.Children.Add(formatTag);
        var sample = Ui.Button("Beispiel im Editor öffnen", "edit", () => Run(EditSampleAsync));
        sample.HorizontalAlignment = HorizontalAlignment.Left;
        var previewPane = Ui.Stack(10, new Border { Child = preview, CornerRadius = new CornerRadius(9), ClipToBounds = true }, sample);
        var controls = Ui.Stack(7, Ui.Text(video ? "Video aufnehmen" : "Screenshot aufnehmen", 21, weight: FontWeight.SemiBold));
        controls.Children.Add(Ui.Text("Aufnahmebereich", 12, Ui.Muted, FontWeight.SemiBold));
        controls.Children.Add(Ui.Choices(["Gesamter Hauptbildschirm","Bereich auswählen"], region ? 1 : 0, i => { region = i == 1; RefreshPage(); }));
        controls.Children.Add(Ui.Text("Zeitverzögerung", 12, Ui.Muted, FontWeight.SemiBold));
        controls.Children.Add(Ui.Choices(["Keine Verzögerung","3 Sekunden","5 Sekunden","10 Sekunden"], Array.IndexOf(Delays, delay), i => delay = Delays[i]));
        if (video)
        {
            controls.Children.Add(Ui.Check("Fokus-Zoom", focusZoom, value => focusZoom = value));
            controls.Children.Add(Ui.Check("Mauszeiger anzeigen", showCursor, value => showCursor = value));
            controls.Children.Add(Ui.Check("Systemton aufnehmen", systemAudio, value => systemAudio = value));
        }
        var capture = Ui.Button(video ? "Aufnahme starten" : "Screenshot erstellen", video ? "record" : "camera", () => Run(CaptureAsync), "primary");
        capture.Margin = new Thickness(0,7,0,0); capture.Height = 40; capture.HorizontalAlignment = HorizontalAlignment.Stretch;
        capture.HorizontalContentAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(capture, OperatingSystem.IsMacOS() ? "⌘ + Enter" : "Strg + Enter");
        controls.Children.Add(capture);
        var captureCard = Ui.Card(Ui.Columns("1.45*,22,*", previewPane, new Border(), controls), 17);
        var recentHeading = Ui.Columns("*,Auto", Ui.Text("Letzte Aufnahmen", 21, weight: FontWeight.SemiBold),
            Ui.Button("Mediathek öffnen", "right", () => Navigate(destinations[1]), "flat"));
        Control recent = items.Count == 0 ? EmptyLibrary(false) : Ui.Stack(0, items.Take(3).Select(ItemRow).ToArray());
        return Ui.Stack(17, Heading("Aufnahmestudio", "Screenshots gestalten und Bildschirmvideos aufnehmen."),
            modeBox, captureCard, recentHeading, items.Count == 0 ? recent : Ui.Card(recent, 4));
    }
    private static readonly int[] Delays = [0,3,5,10];
    private static Control EmptyLibrary(bool filtered)
    {
        var icon = Ui.Card(Ui.Icon(filtered ? "search" : "library", 23, Ui.Muted), 12);
        icon.Background = Ui.Brush("#F5F2EF");
        return Ui.Card(Ui.Row(15, icon, Ui.Stack(3, Ui.Text(filtered ? "Keine Treffer" : "Noch keine Aufnahmen", 15, weight: FontWeight.SemiBold),
            Ui.Text(filtered ? "Suchtext oder Filter ändern." : "Gespeicherte Screenshots und Videos erscheinen hier.", 13, Ui.Muted))), 20);
    }
    private async Task EditSampleAsync()
    {
        using var source = AssetLoader.Open(new Uri("avares://ThixShot/Assets/Studio.jpg"));
        await EditAsync(source, true);
    }
    private async Task EditAsync(Stream source, bool sample = false)
    {
        var editor = new EditorWindow(source, sample);
        var path = await editor.ShowDialog<string?>(this);
        if (path != null) { selectedPath = path; ShowMessage($"Gespeichert · {Path.GetFileName(path)}"); }
    }
}
