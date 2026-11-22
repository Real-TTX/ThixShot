namespace ThixShot.Capture;

public sealed record ZoomPoint(double StartSeconds, double DurationSeconds, double X, double Y, double Scale);
public sealed record VideoInfo(double DurationSeconds, int Width, int Height);

public static class VideoEditService
{
    public static Task<VideoInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        path = RequireSource(path);
        return Task.Run(async () => OperatingSystem.IsWindows() ? WindowsVideoEditor.Inspect(path)
            : await MacVideoEditor.InspectAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken);
    }

    public static Task<byte[]> FrameAsync(string path, double seconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        path = RequireSource(path);
        return Task.Run(async () => OperatingSystem.IsWindows() ? WindowsVideoEditor.Frame(path, seconds, cancellationToken)
            : await MacVideoEditor.FrameAsync(path, seconds, cancellationToken).ConfigureAwait(false), cancellationToken);
    }

    public static (double X, double Y, double Width, double Height) ViewportAt(double seconds, IReadOnlyList<ZoomPoint> zoomPoints)
        => ViewportAtSorted(seconds, zoomPoints.Where(IsValid).OrderBy(z => z.StartSeconds));

    private static bool IsValid(ZoomPoint? p) => p != null && double.IsFinite(p.StartSeconds) && p.StartSeconds >= 0 &&
        double.IsFinite(p.DurationSeconds) && p.DurationSeconds > 0 && p.X is >= 0 and <= 1 &&
        p.Y is >= 0 and <= 1 && p.Scale is >= 1 and <= 16;

    internal static (double X, double Y, double Width, double Height) ViewportAtSorted(double seconds, IEnumerable<ZoomPoint> points)
    {
        double x = 0, y = 0, size = 1;
        foreach (var point in points)
        {
            double age = seconds - point.StartSeconds;
            if (age < 0) break;
            if (age >= point.DurationSeconds) continue;
            double edge = Math.Min(0.35, point.DurationSeconds / 2);
            double t = Math.Min(1, Math.Min(age, point.DurationSeconds - age) / edge);
            double weight = t * t * (3 - 2 * t);
            double targetSize = 1 / point.Scale;
            x += (Math.Clamp(point.X - targetSize / 2, 0, 1 - targetSize) - x) * weight;
            y += (Math.Clamp(point.Y - targetSize / 2, 0, 1 - targetSize) - y) * weight;
            size += (targetSize - size) * weight;
        }
        return (x, y, size, size);
    }

    public static async Task ExportAsync(string sourcePath, string targetPath, IReadOnlyList<ZoomPoint> zoomPoints,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        sourcePath = RequireSource(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        if (!Path.GetExtension(targetPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Die Exportdatei benötigt die Endung .mp4.", nameof(targetPath));
        if (File.Exists(targetPath)) throw new IOException("Die Zieldatei existiert bereits.");
        var points = zoomPoints.ToArray();
        if (points.Any(p => !IsValid(p)))
            throw new ArgumentException("Die Zoomspur enthält einen ungültigen Ausschnitt.", nameof(zoomPoints));
        points = points.OrderBy(p => p.StartSeconds).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".thixshot-export-{Guid.NewGuid():N}.mp4");
        try
        {
            if (points.Length == 0)
            {
                await using var input = File.OpenRead(sourcePath);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(async () =>
                {
                    if (OperatingSystem.IsWindows()) WindowsVideoEditor.Export(sourcePath, temporary, points, progress, cancellationToken);
                    else await MacVideoEditor.ExportAsync(sourcePath, temporary, points, progress, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, targetPath);
            progress?.Report(1);
        }
        finally { File.Delete(temporary); }
    }

    private static string RequireSource(string path)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOSVersionAtLeast(14))
            throw new PlatformNotSupportedException("Die Videobearbeitung benötigt Windows oder macOS 14 und neuer.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Das Quellvideo wurde nicht gefunden.", path);
        return path;
    }
}
