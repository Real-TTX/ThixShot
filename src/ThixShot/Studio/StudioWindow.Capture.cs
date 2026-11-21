using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using ThixShot.Capture;
using ThixShot.Editor;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private async Task CaptureAsync()
    {
        string? screenshot = null;
        try
        {
            Hide();
            if (!video || region)
            {
                if (!video && delay > 0) await CountdownAsync();
                await Task.Delay(320, lifetime.Token);
                screenshot = MediaLibrary.TemporaryFile(".png");
                await CaptureService.TakeScreenshotAsync(screenshot, lifetime.Token);
            }
            SKRectI? area = null;
            if (region)
            {
                Show();
                using var source = File.OpenRead(screenshot!);
                var selection = new SelectionWindow(source);
                if (Screens.Primary is { } display) selection.Position = display.Bounds.Position;
                area = await selection.ShowDialog<SKRectI?>(this);
                if (area == null) return;
                Hide();
            }
            if (video)
            {
                if (delay > 0) await CountdownAsync();
                await Task.Delay(320, lifetime.Token);
                await RecordAsync(area);
                return;
            }
            Show(); Activate();
            using var input = File.OpenRead(screenshot!);
            if (area is not { } crop) { await EditAsync(input); return; }
            using var document = new ImageDocument(input);
            ImageEditor.Add(document, new ImageMark(MarkTool.Crop, new SKPoint(crop.Left,crop.Top), new SKPoint(crop.Right,crop.Bottom), SKColors.White));
            using var png = ImageEditor.Render(document);
            using var cropped = png.AsStream();
            await EditAsync(cropped);
        }
        finally
        {
            if (screenshot != null) File.Delete(screenshot);
            if (!IsVisible) Show();
            Activate();
        }
    }

    private async Task CountdownAsync()
    {
        var label = Ui.Text("", 24, weight: FontWeight.SemiBold);
        var panel = Floating("Aufnahme vorbereiten", label);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        panel.Closed += (_, _) => cancel.Cancel();
        panel.KeyDown += (_, e) => { if (e.Key == Key.Escape) panel.Close(); };
        panel.Content = Ui.Card(Ui.Row(15, label, Ui.Button("Abbrechen", null, panel.Close)), 10);
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

    private static Window Floating(string title, Control content)
    {
        var window = new Window { Title = title, Content = content, SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false, Topmost = true, ShowInTaskbar = true, SystemDecorations = SystemDecorations.BorderOnly,
            WindowStartupLocation = WindowStartupLocation.Manual, Icon = Ui.WindowIcon() };
        window.PointerPressed += (_, e) => { if (e.Source is TextBlock && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e); };
        if (window.Screens.Primary is { } screen)
            window.Position = new PixelPoint(screen.WorkingArea.X + 20, screen.WorkingArea.Y + 20);
        window.Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { } hwnd)
                SetWindowDisplayAffinity(hwnd.Handle, 0x11);
        };
        return window;
    }
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

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
        var timerLabel = Ui.Text("Startet …", 16, weight: FontWeight.SemiBold);
        var panel = Floating("ThixShot · Bildschirmaufnahme", Ui.Card(Ui.Row(12,
            Ui.Icon("record", 20, Ui.Accent), timerLabel,
            Ui.Button("Stopp", "stop", RequestStop, "primary")), 9));
        var clock = new Stopwatch();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => timerLabel.Text = clock.Elapsed.ToString(clock.Elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        panel.Closed += (_, _) => RequestStop();
        panel.KeyDown += (_, e) => { if (e.Key == Key.Escape) RequestStop(); };
        try
        {
            panel.Show();
            CaptureRegion? regionPixels = area is { } box ? new CaptureRegion(box.Left,box.Top,box.Width,box.Height) : null;
            var options = new RecordingOptions(focusZoom, showCursor, systemAudio, regionPixels);
            await using var session = await CaptureService.StartRecordingAsync(path, options, startup.Token);
            clock.Start(); timer.Start();
            await Task.WhenAny(stop.Task, session.Completion);
            timer.Stop(); timerLabel.Text = "Speichert …";
            await session.StopAsync();
            selectedPath = MediaLibrary.Import(path, "Video");
            ShowMessage($"Video gespeichert · {Path.GetFileName(selectedPath)}");
        }
        finally
        {
            timer.Stop();
            if (panel.IsVisible) panel.Close();
            File.Delete(path);
        }
    }
}
