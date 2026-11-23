using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MatDesktop;
using SkiaSharp;
using ThixShot.Capture;
using ThixShot.Editor;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private ReadyPalette? readyPalette;
    internal ReadyPalette? ActiveReadyPalette => readyPalette;

    internal void EnterReadyMode()
    {
        if (busy || allowClose) return;
        if (readyPalette is null)
        {
            var palette = new ReadyPalette(startVideo =>
            {
                if (busy) return;
                video = startVideo;
                Run(CaptureAsync);
            }, () => readyPalette?.Close(), () => Close(), ReadyOptions);
            readyPalette = palette;
            void StudioClosed(object? sender, EventArgs e)
            {
                readyPalette = null;
                palette.Close();
            }
            Closed += StudioClosed;
            palette.Closed += (_, _) =>
            {
                Closed -= StudioClosed;
                if (readyPalette != palette) return;
                readyPalette = null;
                if (!lifetime.IsCancellationRequested) { Show(); Activate(); RefreshPage(); }
            };
        }
        readyPalette.ShowReady();
        Hide();
    }

    private ContextMenu ReadyOptions()
    {
        var menu = new ContextMenu();
        MenuItem Choice(string text, bool selected, Action action)
        {
            var choice = ReadyPalette.MenuAction(text, action);
            choice.ToggleType = MenuItemToggleType.CheckBox;
            choice.IsChecked = selected;
            return choice;
        }
        MenuItem AreaMenu(string title, bool selectRegion, Action<bool> setRegion)
        {
            var area = new MenuItem { Header = title };
            area.Items.Add(Choice("Bereich aufziehen", selectRegion, () => setRegion(true)));
            area.Items.Add(Choice("Gesamter Hauptbildschirm", !selectRegion, () => setRegion(false)));
            return area;
        }
        var countdown = new MenuItem { Header = "Zeitverzögerung" };
        foreach (var seconds in Delays)
            countdown.Items.Add(Choice(seconds == 0 ? "Keine Verzögerung" : $"{seconds} Sekunden", delay == seconds, () => delay = seconds));
        var recording = new MenuItem { Header = "Videooptionen" };
        recording.Items.Add(Choice("Fokus-Zoom bei Klicks", focusZoom, () => focusZoom = !focusZoom));
        recording.Items.Add(Choice("Mauszeiger anzeigen", showCursor, () => showCursor = !showCursor));
        recording.Items.Add(Choice("Systemton aufnehmen", systemAudio, () => systemAudio = !systemAudio));
        menu.Items.Add(AreaMenu("Screenshotbereich", screenshotRegion, value => screenshotRegion = value));
        menu.Items.Add(AreaMenu("Videobereich", videoRegion, value => videoRegion = value));
        menu.Items.Add(countdown); menu.Items.Add(recording);
        return menu;
    }

    private void HideCaptureWindows()
    {
        static void HideWindow(Window window)
        {
            if (!window.IsVisible) return;
            // Menus and tooltips can have their own native popup windows.
            foreach (var control in window.GetVisualDescendants().OfType<Control>().ToArray())
            {
                if (ToolTip.GetIsOpen(control)) ToolTip.SetIsOpen(control, false);
                if (control.ContextMenu?.IsOpen == true) control.ContextMenu.Close();
            }
            window.Hide();
        }
        if (readyPalette != null) HideWindow(readyPalette);
        HideWindow(this);
    }

    private void ReturnFromCapture()
    {
        if (readyPalette != null) { readyPalette.ShowReady(); return; }
        if (!IsVisible) Show();
        Activate();
    }

    private Task CaptureAsync() => CaptureAsync(TakeScreenshotForEditorAsync);

    private async Task<bool> TakeScreenshotForEditorAsync(string path, CancellationToken token)
    {
        if (!screenshotRegion)
        {
            await CaptureService.TakeScreenshotAsync(path, token);
            return true;
        }
        if (OperatingSystem.IsMacOS())
            return await CaptureService.TakeInteractiveSnipAsync(path, token);

        string desktop = MediaLibrary.TemporaryFile(".png");
        try
        {
            var bounds = await CaptureService.TakeVirtualDesktopScreenshotAsync(desktop, token);
            var area = await SelectionWindow.SelectAsync(Screens, desktop, bounds, token);
            if (area is not { } crop) return false;
            await Task.Run(() => ImageOutput.SaveCrop(desktop, crop, path), token);
            return true;
        }
        finally { File.Delete(desktop); }
    }

    internal async Task CaptureAsync(Func<string, CancellationToken, Task<bool>> takeScreenshot)
    {
        string? screenshot = null;
        try
        {
            HideCaptureWindows();
            if (!video)
            {
                if (delay > 0) await CountdownAsync();
                await Task.Delay(320, lifetime.Token);
                screenshot = MediaLibrary.TemporaryFile(".png");
                if (!await takeScreenshot(screenshot, lifetime.Token)) return;
                using var input = File.OpenRead(screenshot);
                await EditAsync(input);
                return;
            }

            SKRectI? area = null;
            if (videoRegion)
            {
                await Task.Delay(320, lifetime.Token);
                screenshot = MediaLibrary.TemporaryFile(".png");
                await CaptureService.TakeScreenshotAsync(screenshot, lifetime.Token);
                area = await SelectionWindow.SelectAsync(Screens, screenshot, null, lifetime.Token);
                if (area == null) return;
            }
            if (delay > 0) await CountdownAsync();
            await Task.Delay(320, lifetime.Token);
            await RecordAsync(area);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Show the normal studio message if the platform refuses access or export fails.
            readyPalette?.Close();
            throw;
        }
        finally
        {
            if (screenshot != null) File.Delete(screenshot);
            if (!lifetime.IsCancellationRequested) ReturnFromCapture();
        }
    }

    private async Task CountdownAsync()
    {
        var label = Ui.Text("", MatTokens.Type.FsLg, weight: FontWeight.SemiBold);
        var panel = Floating("Aufnahme vorbereiten", activateOnShow: true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        panel.Closed += (_, _) => cancel.Cancel();
        panel.KeyDown += (_, e) => { if (e.Key == Key.Escape) panel.Close(); };
        panel.Content = CaptureBar("clock", "Mat.AccentText", label, Ui.Button("Abbrechen", null, panel.Close));
        panel.Show();
        try
        {
            for (int left = delay; left > 0; left--)
            {
                label.Text = $"Start in {left} s";
                await Task.Delay(1000, cancel.Token);
            }
        }
        finally { if (panel.IsVisible) panel.Close(); }
    }

    private static Border CaptureBar(string icon, string color, TextBlock label, Button action)
    {
        var glyph = new MatIcon { Icon = icon, Size = 18, IsFilled = icon == "circle", VerticalAlignment = VerticalAlignment.Center }
            .Use(MatIcon.ForegroundProperty, color);
        var row = Ui.Row(12, glyph, label, action);
        return new Border
        {
            Child = row, Padding = new Thickness(15, 9), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18)
        }.Use(Border.BackgroundProperty, "Mat.Surface").Use(Border.BorderBrushProperty, "Mat.BorderStrong");
    }

    private Window Floating(string title, bool activateOnShow = false)
    {
        var window = new MatWindow { Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false, Topmost = true, ShowInTaskbar = true, ShowActivated = activateOnShow, SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual, Icon = Ui.WindowIcon() };
        window.PointerPressed += (_, e) => { if (e.Source is TextBlock && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e); };
        window.Opened += (_, _) =>
        {
            var saved = readyPalette?.Position;
            var screen = saved is { } point ? window.Screens.All.FirstOrDefault(display => display.Bounds.Contains(point)) : null;
            screen ??= window.Screens.Primary;
            if (screen != null)
            {
                var size = window.Bounds.Size;
                if (size.Width < 2 || size.Height < 2) size = new Size(230, 54);
                window.Position = ReadyPalette.Fit(screen.WorkingArea, PixelSize.FromSize(size, screen.Scaling),
                    saved, (int)Math.Round(12 * screen.Scaling));
            }
        };
        return window;
    }

    private static bool ExcludeRecordingBar(Window window)
    {
        // ScreenCaptureKit's content filter excludes all windows of the desktop process.
        if (OperatingSystem.IsMacOS()) return true;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || window.TryGetPlatformHandle() is not { } handle) return false;
        return SetWindowDisplayAffinity(handle.Handle, 0x11) && GetWindowDisplayAffinity(handle.Handle, out var value) && value == 0x11;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    private async Task RecordAsync(SKRectI? area)
    {
        var path = MediaLibrary.TemporaryFile(".mp4");
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        void RequestStop()
        {
            stop.TrySetResult();
            startup.Cancel();
        }
        var timerLabel = Ui.Text("Startet …", MatTokens.Type.FsMd, weight: FontWeight.SemiBold)
            .Use(TextBlock.FontFamilyProperty, "Mat.Mono");
        var panel = Floating("ThixShot · Bildschirmaufnahme");
        var stopButton = Ui.Button("Stopp", "stop", RequestStop, "primary");
        AutomationProperties.SetHelpText(stopButton, "Aufnahme beenden und Video in der Mediathek speichern.");
        panel.Content = CaptureBar("circle", "Mat.DangerText", timerLabel, stopButton);
        var clock = new Stopwatch();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            var elapsed = clock.Elapsed.ToString(clock.Elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
            timerLabel.Text = $"Aufnahme · {elapsed}";
            panel.Title = $"● ThixShot · {elapsed} · Aufnahme stoppen";
        };
        var panelClosed = false;
        panel.Closed += (_, _) => { panelClosed = true; RequestStop(); };
        panel.KeyDown += (_, e) => { if (e.Key == Key.Escape) RequestStop(); };
        try
        {
            panel.Show();
            if (!ExcludeRecordingBar(panel))
            {
                // Some Windows configurations cannot exclude the HUD. Keep an accessible taskbar
                // stop button, and make it transparent before capturing a single frame.
                timerLabel.Text = "Zum Stoppen ThixShot in der Taskleiste öffnen.";
                await Task.Delay(2100, startup.Token);
                panel.Opacity = 0;
                panel.WindowState = WindowState.Minimized;
                panel.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Window.WindowStateProperty && panel.WindowState != WindowState.Minimized)
                    {
                        panel.Hide();
                        RequestStop();
                    }
                };
                await Task.Delay(300, startup.Token);
            }
            CaptureRegion? regionPixels = area is { } box ? new CaptureRegion(box.Left,box.Top,box.Width,box.Height) : null;
            var options = new RecordingOptions(focusZoom, showCursor, systemAudio, regionPixels);
            await using var session = await CaptureService.StartRecordingAsync(path, options, startup.Token);
            clock.Start(); timer.Start();
            await Task.WhenAny(stop.Task, session.Completion);
            timer.Stop(); timerLabel.Text = "Speichert …";
            await session.StopAsync();
            await SaveRecordingProjectAsync(path, session.ZoomPoints);
            if (readyPalette is null)
            {
                current = destinations[1];
                kind = 0; sort = 0; pageNumber = 0;
                search = string.Empty;
                globalSearch.Text = string.Empty;
            }
        }
        finally
        {
            timer.Stop();
            if (!panelClosed) panel.Close();
            File.Delete(path);
        }
    }
}
