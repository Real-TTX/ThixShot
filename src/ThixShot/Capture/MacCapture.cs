using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ThixShot.Capture;

internal static class MacCapture
{
    public static async Task TakeScreenshotAsync(string path, CancellationToken cancellationToken)
    {
        using var process = Start(["screenshot", path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]);
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            CheckResult(process, await error.ConfigureAwait(false));
        }
        finally
        {
            Terminate(process);
        }
    }

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
            throw new UnauthorizedAccessException("Erlaube ThixShot in den macOS-Systemeinstellungen unter Datenschutz & Sicherheit die Bildschirm- und Systemaudioaufnahme. Öffne ThixShot anschließend erneut.");
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
