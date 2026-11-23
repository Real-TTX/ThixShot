using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;

namespace ThixShot.Capture;

internal static class MacCapture
{
    public static async Task<CaptureRegion> TakeScreenshotAsync(string path, CancellationToken cancellationToken)
    {
        using var process = Start(["screenshot", path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]);
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            CheckResult(process, await error.ConfigureAwait(false));
            return ReadScreenshotBounds(path);
        }
        finally
        {
            Terminate(process);
        }
    }

    public static async Task<CaptureRegion?> TakeInteractiveSnipAsync(string path, CancellationToken ct)
    {
        // Screen Recording permission belongs to the requesting app. Ask before opening the
        // system picker so denied access cannot be mistaken for an Escape with no result.
        if (!CGPreflightScreenCaptureAccess() && !CGRequestScreenCaptureAccess()) throw ScreenAccessDenied();
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-i", "-s", "-x", "-t", "png", path }) start.ArgumentList.Add(arg);
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("Die macOS-Ausschnittsauswahl konnte nicht geöffnet werden.");
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var detail = (await error.ConfigureAwait(false)).Trim();
            if (!File.Exists(path) && !CGPreflightScreenCaptureAccess()) throw ScreenAccessDenied();
            return ReadSnipResult(path, process.ExitCode, detail);
        }
        finally { Terminate(process); }
    }

    internal static CaptureRegion? ReadSnipResult(string path, int exitCode, string error)
    {
        if (!File.Exists(path) && exitCode is 0 or 1 && (string.IsNullOrWhiteSpace(error) ||
            error.Contains("cancelled", StringComparison.OrdinalIgnoreCase) || error.Contains("canceled", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (exitCode != 0 || !File.Exists(path))
            throw new IOException(string.IsNullOrWhiteSpace(error) ? "macOS konnte den gewählten Ausschnitt nicht speichern." : error);
        return ReadScreenshotBounds(path);
    }

    private static CaptureRegion ReadScreenshotBounds(string path)
    {
        using var file = File.OpenRead(path);
        // A PNG decoder can read the dimensions of a file whose final scanlines are missing.
        // The system utility has exited here, so a missing IEND is a failed capture.
        ReadOnlySpan<byte> pngEnd = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
        Span<byte> tail = stackalloc byte[pngEnd.Length];
        if (file.Length < tail.Length) throw new InvalidDataException("Die Bildschirmaufnahme ist unvollständig.");
        file.Seek(-tail.Length, SeekOrigin.End);
        file.ReadExactly(tail);
        if (!tail.SequenceEqual(pngEnd)) throw new InvalidDataException("Die Bildschirmaufnahme ist unvollständig.");
        file.Position = 0;
        using var codec = SKCodec.Create(file);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new InvalidDataException("Die Bildschirmaufnahme enthält kein gültiges PNG.");
        return new CaptureRegion(0, 0, codec.Info.Width, codec.Info.Height);
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
    private static UnauthorizedAccessException ScreenAccessDenied() => new("Erlaube ThixShot in den macOS-Systemeinstellungen unter Datenschutz & Sicherheit die Bildschirm- und Systemaudioaufnahme. Öffne ThixShot anschließend erneut.");

    public static async Task<IReadOnlyList<ZoomPoint>> RecordAsync(string path, RecordingOptions options, Task stop, TaskCompletionSource ready)
    {
        var args = new List<string> { "record", path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            options.FocusZoom ? "1" : "0", options.ShowCursor ? "1" : "0", options.SystemAudio ? "1" : "0" };
        if (options.Region is { } region)
            args.AddRange(new[] { region.X, region.Y, region.Width, region.Height }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        using var process = Start(args);
        var error = process.StandardError.ReadToEndAsync();
        var exit = process.WaitForExitAsync();
        try
        {
            var signal = process.StandardOutput.ReadLineAsync();
            await Task.WhenAny(signal, stop).ConfigureAwait(false);
            if (stop.IsCompleted && !signal.IsCompleted) throw new OperationCanceledException();
            if (await signal.ConfigureAwait(false) != "READY")
            {
                await exit.ConfigureAwait(false);
                CheckResult(process, await error.ConfigureAwait(false));
                throw new IOException("Die Bildschirmaufnahme konnte nicht gestartet werden.");
            }
            ready.TrySetResult();
            var zooms = process.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(stop, exit).ConfigureAwait(false) == stop && !process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await exit.WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            CheckResult(process, await error.ConfigureAwait(false));
            return JsonSerializer.Deserialize<ZoomPoint[]>(await zooms.ConfigureAwait(false), JsonSerializerOptions.Web) ?? [];
        }
        finally
        {
            Terminate(process);
        }
    }

    internal static Process Start(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ThixShotCapture"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Die macOS-Bildschirmaufnahme konnte nicht geöffnet werden.");
    }

    internal static void CheckResult(Process process, string message)
    {
        if (process.ExitCode == 0) return;
        if (process.ExitCode == 77)
            throw ScreenAccessDenied();
        throw new IOException(string.IsNullOrWhiteSpace(message) ? "macOS hat die Bildschirmaufnahme unterbrochen." : message.Trim());
    }

    internal static void Terminate(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { return; }
        process.WaitForExit();
    }
}
