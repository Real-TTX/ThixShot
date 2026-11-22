using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ThixShot.Studio;

namespace ThixShot.Tests;

public sealed class ReadyPaletteTests
{
    [AvaloniaFact]
    public void Entering_and_leaving_ready_mode_keeps_the_studio_alive()
    {
        var studio = new StudioWindow();
        try
        {
            studio.Show();
            studio.EnterReadyMode();
            var palette = Assert.IsType<ReadyPalette>(studio.ActiveReadyPalette);
            Assert.True(palette.IsVisible);
            Assert.False(studio.IsVisible);
            studio.EnterReadyMode();
            Assert.Same(palette, studio.ActiveReadyPalette);
            using var image = palette.CaptureRenderedFrame();
            Assert.NotNull(image);
            var output = Environment.GetEnvironmentVariable("THIXSHOT_QA_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, "ready.png"));
            }
            Click(palette, "Studio öffnen");
            Assert.True(studio.IsVisible);
            Assert.Null(studio.ActiveReadyPalette);
            Assert.False(palette.IsVisible);
        }
        finally { studio.Close(); }
    }

    [AvaloniaFact]
    public async Task Screenshot_hides_both_windows_before_capture_and_restores_ready_after_cancellation()
    {
        var studio = new StudioWindow();
        try
        {
            studio.Show();
            studio.EnterReadyMode();
            var palette = Assert.IsType<ReadyPalette>(studio.ActiveReadyPalette);
            var menuButton = Click(palette, "Aufnahmeoptionen");
            Assert.True(menuButton.ContextMenu?.IsOpen);
            var called = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => studio.CaptureAsync((path, token) =>
            {
                called = true;
                Assert.False(studio.IsVisible);
                Assert.False(palette.IsVisible);
                Assert.False(menuButton.ContextMenu?.IsOpen);
                throw new OperationCanceledException(token);
            }));
            Assert.True(called);
            Assert.False(studio.IsVisible);
            Assert.True(palette.IsVisible);
        }
        finally { studio.Close(); }
    }

    [AvaloniaFact]
    public void Capture_starts_only_after_the_user_selects_screenshot_or_video()
    {
        var requests = new List<bool>();
        var palette = new ReadyPalette(requests.Add, () => { }, () => { }, () => new ContextMenu());
        try
        {
            palette.ShowReady();
            Assert.Empty(requests);
            Click(palette, "Screenshot erstellen");
            Click(palette, "Videoaufnahme starten");
            Assert.Equal(new[] { false, true }, requests);
            var options = Click(palette, "Aufnahmeoptionen");
            Assert.True(options.ContextMenu?.IsOpen);
            Assert.Contains(options.ContextMenu!.Items.OfType<MenuItem>(), item => item.Header as string == "Leiste oben zentrieren");
            options.ContextMenu.Close();
        }
        finally { palette.Close(); }
    }

    [Fact]
    public void Position_uses_the_work_area_and_keeps_all_controls_on_screen()
    {
        var leftMonitor = new PixelRect(-1600, 40, 1600, 860);
        var size = new PixelSize(400, 60);
        Assert.Equal(new PixelPoint(-1000, 52), ReadyPalette.Fit(leftMonitor, size, null, 12));
        Assert.Equal(new PixelPoint(-400, 840), ReadyPalette.Fit(leftMonitor, size, new PixelPoint(-120, 890), 12));
        Assert.Equal(new PixelPoint(-1470, 124), ReadyPalette.Fit(leftMonitor, size, new PixelPoint(-1470, 124), 12));
    }

    [AvaloniaFact]
    public void Moving_the_palette_is_remembered_on_reopen_and_reset_restores_top_center()
    {
        static ReadyPalette Create() => new(_ => { }, () => { }, () => { }, () => new ContextMenu());
        var first = Create();
        PixelPoint centered, moved;
        try
        {
            first.ShowReady();
            first.ResetPosition();
            centered = first.Position;
            first.UpdateLayout();
            first.GetVisualDescendants().OfType<Button>().First().Focus();
            first.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            first.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            moved = first.Position;
            Assert.True(moved.X > centered.X, $"Start={centered}, Ende={moved}, Bildschirm={first.Screens.Primary?.WorkingArea}");
        }
        finally { first.Close(); }
        var second = Create();
        try
        {
            second.ShowReady();
            Assert.Equal(moved, second.Position);
            second.ResetPosition();
            Assert.Equal(centered, second.Position);
        }
        finally { second.Close(); }
    }

    private static Button Click(Window window, string label)
    {
        window.UpdateLayout();
        var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), item => AutomationProperties.GetName(item) == label);
        Assert.NotNull(ToolTip.GetTip(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return button;
    }
}
