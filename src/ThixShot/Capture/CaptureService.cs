namespace ThixShot.Capture;

public readonly record struct CaptureRegion(int X, int Y, int Width, int Height);

public sealed record RecordingOptions(
    bool FocusZoom = false,
    bool ShowCursor = true,
    bool SystemAudio = false,
    CaptureRegion? Region = null);

public static class CaptureService
{
    private static int recordingInProgress;

    public static async Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        CheckDesktopAccess();
        outputPath = ValidateNewPath(outputPath, ".png");
        string temporaryPath = TemporaryPathFor(outputPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                await Task.Run(() => WindowsDesktop.SaveScreenshot(temporaryPath), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await MacCapture.TakeScreenshotAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath);
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
                    WindowsRecording.Record(temporaryPath, options, session.StopSignal.Task, ready);
                else
                    await MacCapture.RecordAsync(temporaryPath, options, session.StopSignal.Task, ready).ConfigureAwait(false);

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
