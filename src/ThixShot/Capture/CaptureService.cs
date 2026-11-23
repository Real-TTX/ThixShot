namespace ThixShot.Capture;

public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    internal bool Contains(CaptureRegion other) => other.Width > 0 && other.Height > 0 && other.X >= X && other.Y >= Y &&
        (long)other.X + other.Width <= (long)X + Width && (long)other.Y + other.Height <= (long)Y + Height;
}

public sealed record RecordingOptions(
    bool FocusZoom = false,
    bool ShowCursor = true,
    bool SystemAudio = false,
    CaptureRegion? Region = null);

public static class CaptureService
{
    private enum ScreenshotTarget { Primary, VirtualDesktop, InteractiveSelection }
    private static int recordingInProgress;

    public static Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default) =>
        SaveScreenshotAsync(outputPath, ScreenshotTarget.Primary, cancellationToken);

    /// <summary>Freezes all Windows monitors. The PNG's pixel (0, 0) is the returned desktop origin.</summary>
    internal static async Task<CaptureRegion> TakeVirtualDesktopScreenshotAsync(string outputPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Die virtuelle Desktopaufnahme benötigt Windows.");
        return await SaveScreenshotAsync(outputPath, ScreenshotTarget.VirtualDesktop, ct).ConfigureAwait(false)
            ?? throw new IOException("Windows hat kein Bildschirmbild geliefert.");
    }

    /// <summary>Lets macOS select a rectangle at native resolution. Escape leaves the destination untouched.</summary>
    internal static async Task<bool> TakeInteractiveSnipAsync(string outputPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Die native Ausschnittsauswahl benötigt macOS.");
        return (await SaveScreenshotAsync(outputPath, ScreenshotTarget.InteractiveSelection, ct).ConfigureAwait(false)).HasValue;
    }

    private static async Task<CaptureRegion?> SaveScreenshotAsync(string outputPath, ScreenshotTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckDesktopAccess();
        outputPath = ValidateNewPath(outputPath, ".png");
        string temporaryPath = TemporaryPathFor(outputPath);
        try
        {
            CaptureRegion? bounds;
            if (OperatingSystem.IsWindows())
            {
                bounds = await Task.Run(() => WindowsDesktop.SaveScreenshot(temporaryPath, target == ScreenshotTarget.VirtualDesktop),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                bounds = target == ScreenshotTarget.InteractiveSelection
                    ? await MacCapture.TakeInteractiveSnipAsync(temporaryPath, cancellationToken).ConfigureAwait(false)
                    : await MacCapture.TakeScreenshotAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (bounds.HasValue) File.Move(temporaryPath, outputPath);
            return bounds;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static async Task<RecordingSession> StartRecordingAsync(string outputPath, RecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        CheckDesktopAccess();
        ArgumentNullException.ThrowIfNull(options);
        outputPath = ValidateNewPath(outputPath, ".mp4");
        if (options.Region is { } region && (region.X < 0 || region.Y < 0 || region.Width < 2 || region.Height < 2))
            throw new ArgumentOutOfRangeException(nameof(options), "Der Aufnahmebereich ist zu klein oder liegt außerhalb des Bildschirms.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref recordingInProgress, 1, 0) != 0)
            throw new InvalidOperationException("Es läuft bereits eine Bildschirmaufnahme.");

        var session = new RecordingSession();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string temporaryPath = TemporaryPathFor(outputPath);
        session.Completion = Task.Run(async () =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    session.ZoomPoints = WindowsRecording.Record(temporaryPath, options, session.StopSignal.Task, ready);
                else
                    session.ZoomPoints = await MacCapture.RecordAsync(temporaryPath, options, session.StopSignal.Task, ready).ConfigureAwait(false);

                if (!session.StopSignal.Task.IsCompleted)
                    throw new IOException("Die Bildschirmaufnahme wurde unerwartet beendet.");
                if (await session.StopSignal.Task.ConfigureAwait(false))
                    File.Move(temporaryPath, outputPath);
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref recordingInProgress, 0);
                File.Delete(temporaryPath);
            }
        });
        try
        {
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.StopSignal.TrySetResult(false);
            try { await session.Completion.ConfigureAwait(false); } catch { /* Preserve the startup failure. */ }
            throw;
        }
    }

    private static void CheckDesktopAccess()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOSVersionAtLeast(14))
            throw new PlatformNotSupportedException("Die Bildschirmaufnahme benötigt Windows oder macOS 14 und neuer.");
        if (!Environment.UserInteractive)
            throw new UnauthorizedAccessException("Bildschirmaufnahmen benötigen eine interaktive Benutzersitzung.");
        if (OperatingSystem.IsWindows())
            WindowsDesktop.CheckAccess();
    }

    private static string ValidateNewPath(string path, string extension)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Die Zieldatei benötigt die Endung {extension}.", nameof(path));
        if (File.Exists(path))
            throw new IOException("Die Zieldatei ist bereits vorhanden.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string TemporaryPathFor(string path) => Path.Combine(Path.GetDirectoryName(path)!,
        $".thixshot-{Guid.NewGuid():N}{Path.GetExtension(path)}");
}

public sealed class RecordingSession : IAsyncDisposable
{
    internal RecordingSession() { }
    internal TaskCompletionSource<bool> StopSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion { get; internal set; } = Task.CompletedTask;
    public IReadOnlyList<ZoomPoint> ZoomPoints { get; internal set; } = [];

    public Task StopAsync()
    {
        StopSignal.TrySetResult(true);
        return Completion;
    }

    public ValueTask DisposeAsync()
    {
        StopSignal.TrySetResult(false);
        return new ValueTask(Completion);
    }
}
