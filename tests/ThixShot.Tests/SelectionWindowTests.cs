using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using ThixShot.Capture;
using ThixShot.Editor;

namespace ThixShot.Tests;

public sealed class SelectionWindowTests
{
    [Fact]
    public void Drag_from_scaled_left_display_into_primary_uses_the_same_physical_pixels_in_both_windows()
    {
        var desktop = new CaptureRegion(-1920, -120, 4480, 1560);
        var image = new PixelSize(4480, 1560);
        var left = SelectionWindow.SourceTile(new PixelRect(-1920, -120, 1920, 1080), desktop, image);
        var primary = SelectionWindow.SourceTile(new PixelRect(0, 0, 2560, 1440), desktop, image);
        Assert.Equal(SKRectI.Create(0, 0, 1920, 1080), left);
        Assert.Equal(SKRectI.Create(1920, 120, 2560, 1440), primary);
        var leftClient = new Size(1280, 720); // left monitor at 150%, primary at 100%
        var start = SelectionWindow.SourcePoint(left, leftClient, new Point(1200, 200));
        var capturedEnd = SelectionWindow.SourcePoint(left, leftClient, new Point(1400, 300));
        var primaryEnd = SelectionWindow.SourcePoint(primary, new Size(2560, 1440), new Point(180, 330));
        Assert.Equal(primaryEnd, capturedEnd);
        Assert.Equal(new SKRectI(1800, 300, 2100, 450), SelectionWindow.Selection(start, capturedEnd, image));
        Assert.Equal(SelectionWindow.Selection(start, capturedEnd, image), SelectionWindow.Selection(capturedEnd, start, image));
        Assert.Throws<InvalidOperationException>(() => SelectionWindow.SourceTile(new PixelRect(0, 0, 2560, 1440), desktop, new PixelSize(1, 1)));
    }

    [Fact]
    public void Retina_display_points_map_to_the_primary_native_image_including_the_final_row_and_column()
    {
        var image = new PixelSize(2880, 1800);
        var tile = SelectionWindow.SourceTile(new PixelRect(0, 0, 1440, 900), null, image);
        var topLeft = SelectionWindow.SourcePoint(tile, new Size(1440, 900), new Point(30.5, 20.5));
        var bottomRight = SelectionWindow.SourcePoint(tile, new Size(1440, 900), new Point(1439.5, 899.5));
        Assert.Equal(new SKRectI(61, 41, 2880, 1800), SelectionWindow.Selection(bottomRight, topLeft, image));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Picker_without_visible_owner_accepts_a_reverse_drag_or_escape(bool cancel)
    {
        var hidden = new Window();
        var path = Path.Combine(Path.GetTempPath(), $"thixshot-selection-test-{Guid.NewGuid():N}.png");
        var opened = new TaskCompletionSource<SelectionWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observed = Window.WindowOpenedEvent.AddClassHandler<SelectionWindow>((window, _) => opened.TrySetResult(window));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using (var pixels = new SKBitmap(320, 200))
            {
                pixels.Erase(SKColors.LightBlue);
                using var png = pixels.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(path);
                png.SaveTo(stream);
            }
            var result = SelectionWindow.SelectAsync(hidden.Screens, path, null, timeout.Token);
            var picker = await opened.Task.WaitAsync(timeout.Token);
            picker.UpdateLayout();
            Assert.Null(picker.Owner);
            Assert.True(picker.Topmost);
            Assert.False(picker.ShowInTaskbar);
            Assert.Equal(SystemDecorations.None, picker.SystemDecorations);
            Assert.False(hidden.IsVisible);
            var area = picker.Bounds;
            var start = new Point(area.Width * .75, area.Height * .8);
            var end = new Point(area.Width * .25, area.Height * .2);
            picker.MouseMove(start);
            picker.MouseDown(start, MouseButton.Left);
            picker.MouseMove(end, RawInputModifiers.LeftMouseButton);
            if (cancel)
                picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            else
                picker.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(cancel ? null : new SKRectI(80, 40, 240, 160), await result);
            Assert.False(picker.IsVisible);
            Assert.False(hidden.IsVisible);
        }
        finally
        {
            timeout.Cancel();
            hidden.Close();
            File.Delete(path);
        }
    }
}
