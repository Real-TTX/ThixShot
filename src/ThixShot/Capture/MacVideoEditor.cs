using System.Globalization;
using System.Text.Json;

namespace ThixShot.Capture;

internal static class MacVideoEditor
{
    internal static async Task<VideoInfo> InspectAsync(string path, CancellationToken token)
    {
        string result = await RunAsync(["inspect", path], null, null, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<VideoInfo>(result, JsonSerializerOptions.Web) ?? throw new InvalidDataException("Ungültige Videoinformationen.");
    }

    internal static async Task<byte[]> FrameAsync(string path, double seconds, CancellationToken token)
    {
        string image = Path.Combine(Path.GetTempPath(), $"thixshot-frame-{Guid.NewGuid():N}.png");
        try
        {
            await RunAsync(["frame", path, seconds.ToString("R", CultureInfo.InvariantCulture), image], null, null, token).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(image, token).ConfigureAwait(false);
        }
        finally { File.Delete(image); }
    }

    internal static Task ExportAsync(string path, string output, ZoomPoint[] points, IProgress<double>? progress, CancellationToken token) =>
        RunAsync(["export", path, output], JsonSerializer.Serialize(points, JsonSerializerOptions.Web), progress, token);

    private static async Task<string> RunAsync(string[] args, string? input, IProgress<double>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = MacCapture.Start(args);
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            string? result = null;
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal) && double.TryParse(line.AsSpan(9), CultureInfo.InvariantCulture, out double fraction))
                    progress?.Report(Math.Clamp(fraction, 0, 1));
                else result = line;
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            MacCapture.CheckResult(process, await errors.ConfigureAwait(false));
            return result ?? "";
        }
        finally { MacCapture.Terminate(process); }
    }
}
