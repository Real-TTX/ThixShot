using ThixShot.Capture;

namespace ThixShot.Tests;

public sealed class SnipCaptureTests
{
    [Fact]
    public void Virtual_bounds_include_negative_coordinates_and_keep_their_physical_dimensions()
    {
        var desktop = new CaptureRegion(-1600, -900, 5440, 3060);
        Assert.True(desktop.Contains(desktop));
        Assert.True(desktop.Contains(new CaptureRegion(-1600, -900, 1600, 900)));
        Assert.True(desktop.Contains(new CaptureRegion(-200, -100, 600, 400)));
        Assert.True(desktop.Contains(new CaptureRegion(3839, 2159, 1, 1)));
        Assert.False(desktop.Contains(new CaptureRegion(-1601, -900, 1600, 900)));
        Assert.False(desktop.Contains(new CaptureRegion(3839, 2159, 2, 1)));
        Assert.False(desktop.Contains(new CaptureRegion(0, 0, 0, 1)));
        Assert.False(desktop.Contains(new CaptureRegion(int.MaxValue, 0, 2, 1)));
    }

    [Fact]
    public async Task Canceled_capture_does_not_request_permission_or_create_an_output_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        var canceled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CaptureService.TakeScreenshotAsync(path, canceled));
        if (OperatingSystem.IsWindows())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CaptureService.TakeVirtualDesktopScreenshotAsync(path, canceled));
        if (OperatingSystem.IsMacOS())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CaptureService.TakeInteractiveSnipAsync(path, canceled));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Native_snip_distinguishes_escape_failure_and_a_complete_pixel_sized_image()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        try
        {
            Assert.Null(MacCapture.ReadSnipResult(path, 0, ""));
            Assert.Null(MacCapture.ReadSnipResult(path, 1, "Screen capture cancelled."));
            Assert.Throws<IOException>(() => MacCapture.ReadSnipResult(path, 1, "Could not connect to display"));
            using var png = TestProfile.TestImage();
            var bytes = png.ToArray();
            File.WriteAllBytes(path, bytes[..^12]);
            Assert.Throws<InvalidDataException>(() => MacCapture.ReadSnipResult(path, 0, ""));
            File.WriteAllBytes(path, bytes);
            Assert.Equal(new CaptureRegion(0, 0, 64, 32), MacCapture.ReadSnipResult(path, 0, ""));
            Assert.Throws<IOException>(() => MacCapture.ReadSnipResult(path, 1, "Output failed"));
        }
        finally { File.Delete(path); }
    }
}
