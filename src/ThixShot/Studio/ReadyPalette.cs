using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixShot.Integration;

namespace ThixShot.Studio;

internal sealed class ReadyPalette : Window
{
    private static string PositionFile => Path.Combine(UserFiles.DirectoryPath, "ready-palette-position");
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private PixelPoint? remembered = ReadPosition();
    private bool trackDrag;

    internal ReadyPalette(Action<bool> capture, Action returnToStudio, Action quit, Func<ContextMenu> options)
    {
        Title = "ThixShot · Aufnahmebereit";
        Width = 426; Height = 60;
        CanResize = false; Topmost = true; ShowInTaskbar = true;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Icon = Ui.WindowIcon();

        Border Groove() => new Border { Width = 2, Height = 11, CornerRadius = new CornerRadius(1) }
            .Use(Border.BackgroundProperty, "Mat.TextFaint");
        var grip = new Border
        {
            Child = Ui.Row(7, Ui.Row(2, Groove(), Groove()), new Image { Source = Ui.Asset("Mark.png"), Width = 23, Height = 23 },
                Ui.Text("Bereit", 13, weight: FontWeight.SemiBold)),
            Padding = new Thickness(5, 6), Cursor = new Cursor(StandardCursorType.SizeAll), Background = Brushes.Transparent
        };
        Describe(grip, "Aufnahmeleiste verschieben", "Hier ziehen oder die Leiste mit Alt + Pfeiltaste verschieben.");
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            trackDrag = true;
            BeginMoveDrag(e);
        };
        var shot = Ui.Button("Screenshot", "camera", () => capture(false), "primary");
        Describe(shot, "Screenshot erstellen", "Screenshot erstellen und im Bildeditor öffnen.");
        var record = Ui.Button("Video", null, () => capture(true), "flat");
        var recordDot = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Padding = new Thickness(2),
            BorderThickness = new Thickness(1), Child = new Border { CornerRadius = new CornerRadius(5) }
                .Use(Border.BackgroundProperty, "Mat.Danger") }.Use(Border.BorderBrushProperty, "Mat.Danger");
        record.Content = Ui.Row(6, recordDot, Ui.Text("Video", 13));
        Describe(record, "Videoaufnahme starten", "Bildschirmvideo mit den gewählten Aufnahmeoptionen starten.");
        var more = new Button { Content = Ui.Icon("more-horizontal", 19) }.Themed("MatButton.Icon");
        Describe(more, "Aufnahmeoptionen", "Aufnahmebereich, Verzögerung, Videooptionen und Position einstellen.");
        more.Click += (_, _) =>
        {
            var menu = options();
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Leiste oben zentrieren", ResetPosition));
            menu.Items.Add(MenuAction("ThixShot beenden", quit));
            more.ContextMenu = menu;
            menu.Open(more);
        };
        var studio = new Button { Content = Ui.Icon("monitor", 18) }.Themed("MatButton.Icon");
        Describe(studio, "Studio öffnen", "Bereitschaft beenden und das Aufnahmestudio öffnen.");
        studio.Click += (_, _) => returnToStudio();
        var rule = new Border { Width = 1, Height = 23, Margin = new Thickness(2, 0) }
            .Use(Border.BackgroundProperty, "Mat.BorderStrong");
        var row = Ui.Row(6, grip, rule, shot, record, more, studio);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.VerticalAlignment = VerticalAlignment.Center;
        SetGlassContent(row);

        saveTimer.Tick += (_, _) => SavePosition();
        PositionChanged += (_, _) =>
        {
            if (!trackDrag || !IsVisible) return;
            Remember(Position);
        };
        Opened += (_, _) => { Screens.Changed -= ScreensChanged; Screens.Changed += ScreensChanged; Place(); };
        Closed += (_, _) =>
        {
            Screens.Changed -= ScreensChanged;
            if (saveTimer.IsEnabled) SavePosition();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; returnToStudio(); return; }
            if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            var step = (int)Math.Round(12 * screen.Scaling);
            var x = Position.X + (e.Key == Key.Right ? step : e.Key == Key.Left ? -step : 0);
            var y = Position.Y + (e.Key == Key.Down ? step : e.Key == Key.Up ? -step : 0);
            Position = Fit(screen.WorkingArea, PixelSize.FromSize(new Size(Width, Height), screen.Scaling), new PixelPoint(x, y), 0);
            Remember(Position);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    internal static MenuItem MenuAction(string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        return item;
    }

    private static void Describe(Control control, string label, string tip)
    {
        AutomationProperties.SetName(control, label);
        ToolTip.SetTip(control, tip);
    }

    private void SetGlassContent(Control content)
    {
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        var backdrop = new Border { CornerRadius = new CornerRadius(22) }.Use(Border.BackgroundProperty, "Mat.Surface");
        void UpdateTint()
        {
            var blur = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur || ActualTransparencyLevel == WindowTransparencyLevel.Blur;
            backdrop.Opacity = blur ? .74 : ActualTransparencyLevel == WindowTransparencyLevel.Transparent ? .88 : 1;
        }
        PropertyChanged += (_, e) => { if (e.Property == ActualTransparencyLevelProperty) UpdateTint(); };
        UpdateTint();
        Content = new Border
        {
            CornerRadius = new CornerRadius(23), ClipToBounds = true, BorderThickness = new Thickness(1),
            Child = new Grid { Children = { backdrop, content } }
        }.Use(Border.BorderBrushProperty, "Mat.BorderStrong");
    }

    internal void ShowReady()
    {
        Place();
        if (!IsVisible) Show();
        Activate();
    }

    internal void ResetPosition()
    {
        trackDrag = false;
        saveTimer.Stop();
        remembered = null;
        try { File.Delete(PositionFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Place();
    }

    private void ScreensChanged(object? sender, EventArgs args)
    {
        if (IsVisible) Place();
    }

    private void Place()
    {
        trackDrag = false;
        var screen = remembered is { } point ? Screens.All.FirstOrDefault(screen => screen.Bounds.Contains(point)) : null;
        var target = screen ?? Screens.Primary;
        if (target == null) return;
        Position = Fit(target.WorkingArea, PixelSize.FromSize(new Size(Width, Height), target.Scaling),
            screen == null ? null : remembered, (int)Math.Round(12 * target.Scaling));
    }

    internal static PixelPoint Fit(PixelRect area, PixelSize size, PixelPoint? saved, int topInset)
    {
        var right = Math.Max(area.X, area.Right - size.Width);
        var bottom = Math.Max(area.Y, area.Bottom - size.Height);
        return new PixelPoint(Math.Clamp(saved?.X ?? area.X + (area.Width - size.Width) / 2, area.X, right),
            Math.Clamp(saved?.Y ?? area.Y + topInset, area.Y, bottom));
    }

    private static PixelPoint? ReadPosition()
    {
        try
        {
            if (!File.Exists(PositionFile)) return null;
            var parts = File.ReadAllText(PositionFile).Split(' ');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                return new PixelPoint(x, y);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private void SavePosition()
    {
        saveTimer.Stop();
        if (remembered is not { } point) return;
        try { UserFiles.Write(PositionFile, FormattableString.Invariant($"{point.X} {point.Y}")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Remember(PixelPoint point)
    {
        remembered = point;
        saveTimer.Stop(); saveTimer.Start();
    }
}
