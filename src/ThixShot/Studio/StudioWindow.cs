using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MatDesktop;
using Avalonia.Controls.Documents;
using ThixShot.Editor;
using ThixShot.Integration;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow : MatWindow
{
    private readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock message = Ui.Text("", 13);
    private readonly Border notice;
    private readonly MatNav sidebar = new();
    private readonly TextBox globalSearch = new() { Watermark = "Mediathek durchsuchen" };
    private readonly List<Bitmap> thumbnails = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> destinations = ["Aufnahmestudio", "Mediathek", "ThixCloud", "Einstellungen", "Über ThixShot"];

    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private string current = "Aufnahmestudio";
    private bool video, screenshotRegion = true, videoRegion, focusZoom = true, showCursor = true, systemAudio, busy, allowClose;
    private int delay;
    private string search = "";
    private int kind, sort;
    private string? selectedPath, pendingVersion;

    public StudioWindow()
    {
        Title = "ThixShot"; Width = 1180; Height = 760; MinWidth = 780; MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseOwnTitleBar();
        var wordmark = new TextBlock { FontSize = MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        var brandSuffix = new Run("Shot");
        brandSuffix[!Avalonia.Controls.Documents.Run.ForegroundProperty] = this.GetResourceObservable("Mat.AccentText").ToBinding();
        wordmark.Inlines!.Add(new Run("Thix")); wordmark.Inlines.Add(brandSuffix);
        var titleBar = new MatTitleBar { AppName = Title, Logo = Ui.Asset("Mark.png"), WordMark = wordmark };
        globalSearch.Themed("MatSearchBox");
        globalSearch.TextChanged += (_, _) =>
        {
            if (busy) return;
            search = globalSearch.Text ?? "";
            if (search.Length == 0 && current != destinations[1]) return;
            pageNumber = 0; current = destinations[1]; RefreshPage();
        };
        var captureShortcut = OperatingSystem.IsMacOS() ? "⌘ + ⇧ + R" : "Strg + ⇧ + R";
        var quickCapture = Ui.Button("Bereitschaft", "monitor", EnterReadyMode);
        ToolTip.SetTip(quickCapture, "Schwebende Aufnahmeleiste am Bildschirmrand öffnen");
        var keys = new Border { Child = Ui.Text(captureShortcut,12), Padding = new Thickness(7,2),
            VerticalAlignment = VerticalAlignment.Center }.Themed("MatKeyCap");
        var topTools = Ui.Columns("*,Auto,Auto",globalSearch,quickCapture,keys); topTools.ColumnSpacing = 10;
        titleBar.Center = topTools;
        sidebar.AppName = Title;
        sidebar.TitleBar = titleBar;
        sidebar.Version = UpdateService.DisplayVersion;
        sidebar.Channel = UpdateService.Channel;
        sidebar.FoldShortcut = OperatingSystem.IsMacOS() ? "⌘+B" : "Strg+B";
        sidebar.Items.Add(new MatNavItem("monitor",destinations[0]));
        sidebar.Items.Add(new MatNavItem("layers",destinations[1]));
        sidebar.Settings = new MatNavItem("settings",destinations[3]);
        sidebar.ItemInvoked += (_, item) => Navigate(item.Label);
        sidebar.AboutRequested += (_, _) => Navigate(destinations[4]);
        sidebar.UpdateRequested += (_, _) => { settingsTab = 1; Navigate(destinations[3]); };
        sidebar.IsFolded = File.Exists(FoldPreference);
        sidebar.FoldChanged += (_, folded) =>
        {
            try
            {
                if (folded) UserFiles.Write(FoldPreference,"1"); else File.Delete(FoldPreference);
            }
            catch (IOException ex) { ShowMessage(ex.Message); }
            RefreshFoot();
        };
        var main = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        main.Children.Add(page);
        message.TextWrapping = TextWrapping.Wrap;
        notice = Ui.Card(Ui.Columns("*,Auto", message, Ui.Button("Schließen", null, () => ShowMessage(""), "flat")), 9);
        notice.Margin = new Thickness(16, 0, 16, 10); notice.IsVisible = false;
        Grid.SetRow(notice, 1); main.Children.Add(notice);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var body = Ui.Columns("Auto,*",sidebar,main);
        layout.Children.Add(titleBar); Grid.SetRow(body,1); layout.Children.Add(body);
        Content = layout;
        Navigate(current);
        Closing += (_, e) =>
        {
            if (busy && !allowClose) { e.Cancel = true; return; }
            updateTimer.Stop(); lifetime.Cancel();
        };
        Closed += (_, _) => { ClearImages(); lifetime.Dispose(); };
        KeyDown += (_, e) =>
        {
            if (busy || !e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) return;
            switch (e.Key)
            {
                case Key.Enter: Run(CaptureAsync); break;
                case Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Shift): EnterReadyMode(); break;
                case Key.B: sidebar.IsFolded = !sidebar.IsFolded; break;
                case Key.F: globalSearch.Focus(); globalSearch.SelectAll(); break;
                default: return;
            }
            e.Handled = true;
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
        foreach (var item in sidebar.Items) item.IsCurrent = item.Label == current;
        if (sidebar.Settings != null) sidebar.Settings.IsCurrent = current == destinations[3];
        sidebar.UpdateVersion = pendingVersion;
        RefreshFoot();
        try
        {
            var items = MediaLibrary.List();

            Control content = current switch
            {
                "Aufnahmestudio" => StudioPage(items),
                "Mediathek" => LibraryPage(items),
                "ThixCloud" => CloudPage(),
                "Einstellungen" => SettingsPage(),
                _ => AboutPage()
            };
            page.Content = current == destinations[0] ? content : new ScrollViewer { Content = new Border { Padding = new Thickness(24, 22), Child = content } };
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

    private static string FoldPreference => Path.Combine(UserFiles.DirectoryPath, "sidebar-folded");
    private void RefreshFoot()
    {
        var label = CloudService.IsConnected ? "ThixCloud verbunden" : "Mit Cloud verbinden";
        var cloud = Ui.Button(label, "cloud", () => Navigate(destinations[2]), "flat");
        if (sidebar.IsFolded) cloud.Content = Ui.Icon("cloud");
        ToolTip.SetTip(cloud, label);
        sidebar.Foot = cloud;
    }

    private static Border Rule() => new Border { Height = 1 }.Use(Border.BackgroundProperty,"Mat.Border");

    private Control StudioPage(IReadOnlyList<MediaItem> items)
    {
        var modes = Ui.Row(6);
        foreach (var (name, icon, setting) in new[] { ("Screenshot","camera",false), ("Video","video",true) })
        {
            var button = Ui.Button(name, icon, () => { video = setting; RefreshPage(); });
            if (video == setting) button.Use(Button.BackgroundProperty,"Mat.AccentSoft").Use(Button.BorderBrushProperty,"Mat.Accent");
            modes.Children.Add(button);
        }
        var title = new TextBlock { Text = "Aufnahmestudio", VerticalAlignment = VerticalAlignment.Center }.Themed("MatText.PageTitle");
        var heading = Ui.Columns("*,Auto", title, modes);
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(24,21,20,15), RowSpacing = 14 };
        main.Children.Add(heading);
        var count = Ui.Text($"{items.Count} {(items.Count == 1 ? "Aufnahme" : "Aufnahmen")}", 13, Ui.Muted);
        var section = Ui.Columns("*,Auto", Ui.Text("Letzte Aufnahmen",14,weight:FontWeight.SemiBold), count);
        Grid.SetRow(section,1); main.Children.Add(section);
        Control recent = items.Count == 0 ? EmptyLibrary(false) :
            new ScrollViewer { Content = Ui.Stack(7,items.Take(8).Select(ItemRow).ToArray()) };
        Grid.SetRow(recent,2); main.Children.Add(recent);
        var library = Ui.Button("Mediathek öffnen","library",() => Navigate(destinations[1]));
        library.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(library,3); main.Children.Add(library);

        var selectRegion = video ? videoRegion : screenshotRegion;
        var preview = new Border { Height = 148, CornerRadius = new CornerRadius(MatTokens.Size.RadiusLg),
            ClipToBounds = true, Child = new StudioPreview { Region = selectRegion, Video = video } };
        var sample = video ? Ui.Button("Beispielvideo anlegen", "edit", () => Run(EditSampleVideoAsync)) :
            Ui.Button("Beispielbild bearbeiten", "edit", () => Run(EditSampleAsync));
        if (video) ToolTip.SetTip(sample,"Demo in die Mediathek übernehmen und ihre Zoomspur bearbeiten");
        sample.HorizontalAlignment = HorizontalAlignment.Left;
        var controls = Ui.Stack(12, preview, sample, Rule(),
            new TextBlock { Text = video ? "Videoaufnahme" : "Screenshot" }.Themed("MatText.SectionTitle"));
        var source = Ui.Choices(["Gesamter Hauptbildschirm", "Bereich aufziehen"], selectRegion ? 1 : 0, i =>
        {
            if (video) videoRegion = i == 1;
            else screenshotRegion = i == 1;
            RefreshPage();
        });
        var timer = Ui.Choices(["Keine Verzögerung","3 Sekunden","5 Sekunden","10 Sekunden"],Array.IndexOf(Delays,delay),i => delay = Delays[i]);
        controls.Children.Add(Ui.Stack(4, new TextBlock { Text = "Aufnahmebereich" }.Themed("MatText.FieldLabel"),source));
        controls.Children.Add(Ui.Stack(4, new TextBlock { Text = "Zeitverzögerung" }.Themed("MatText.FieldLabel"),timer));
        if (video)
        {
            controls.Children.Add(Ui.Check("Fokus-Zoom", focusZoom, value => focusZoom = value));
            controls.Children.Add(Ui.Check("Mauszeiger anzeigen", showCursor, value => showCursor = value));
            controls.Children.Add(Ui.Check("Systemton aufnehmen", systemAudio, value => systemAudio = value));
        }
        var capture = Ui.Button(video ? "Aufnahme starten" : "Screenshot erstellen", video ? "record" : "camera", () => Run(CaptureAsync), "primary");
        if (selectRegion) ToolTip.SetTip(capture, "ThixShot ausblenden und den Bereich direkt auf dem Bildschirm aufziehen.");
        capture.HorizontalAlignment = HorizontalAlignment.Left; controls.Children.Add(capture);
        var inspector = new Border { BorderThickness = new Thickness(1,0,0,0),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(20,22), Child = controls } } }
            .Use(Border.BackgroundProperty,"Mat.Surface").Use(Border.BorderBrushProperty,"Mat.Border");
        var split = Ui.Columns("*,310",main,inspector);
        void Adapt(double width)
        {
            split.ColumnDefinitions[1].Width = new GridLength(width < 740 ? 250 : 320);
            heading.ColumnDefinitions[1].Width = width < 740 ? new GridLength(0) : GridLength.Auto;
            modes.IsVisible = width >= 740;
            if (width < 740 && !controls.Children.Any(c => c.Name == "NarrowMode"))
            {
                var mode = Ui.Choices(["Screenshot","Video"],video ? 1 : 0,i => { video = i == 1; RefreshPage(); });
                mode.Name = "NarrowMode"; controls.Children.Insert(0,mode);
            }
            if (controls.Children.FirstOrDefault(c => c.Name == "NarrowMode") is { } narrow) narrow.IsVisible = width < 740;
        }
        split.SizeChanged += (_, e) => Adapt(e.NewSize.Width);
        return split;
    }
    private static readonly int[] Delays = [0,3,5,10];
    private static Control EmptyLibrary(bool filtered)
    {
        var icon = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center,
            Child = Ui.Icon(filtered ? "search" : "library",30,Ui.Accent) }.Use(Border.BackgroundProperty,"Mat.AccentSoft");
        var title = new TextBlock { Text = filtered ? "Keine Treffer" : "Noch keine Aufnahmen", HorizontalAlignment = HorizontalAlignment.Center }.Themed("MatText.SectionTitle");
        var hint = new TextBlock { Text = filtered ? "Suchtext oder Filter ändern." : "Gespeicherte Screenshots und Videos erscheinen hier.",
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted");
        var panel = Ui.Stack(10,icon,title,hint);
        panel.VerticalAlignment = VerticalAlignment.Center; panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.MaxWidth = 320;
        return panel;
    }
    private async Task EditSampleAsync()
    {
        using var source = AssetLoader.Open(new Uri("avares://ThixShot/Assets/Studio.jpg"));
        await EditAsync(source, true);
    }
    private async Task EditAsync(Stream source, bool sample = false)
    {
        var editor = new EditorWindow(source, sample);
        string? path;
        if (readyPalette is null)
        {
            if (!IsVisible) Show();
            path = await editor.ShowDialog<string?>(this);
        }
        else
        {
            // The floating palette must stay hidden. Its topmost native owner would also raise this editor.
            path = await editor.ShowStandaloneAsync();
        }
        if (path != null) { selectedPath = path; ShowMessage($"Gespeichert · {Path.GetFileName(path)}"); }
    }
}

