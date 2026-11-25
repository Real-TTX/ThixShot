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

    /// <summary>How long a zoom takes to come in and to go (seconds); two zooms that overlap by this much pan from one to the other.</summary>
    public const double Edge = 0.5;

    /// <summary>
    /// The part of the picture shown at a moment. Every zoom has a weight that rises over its first <see cref="Edge"/> seconds
    /// and falls over its last; the picture moves to the weighted mean of the zooms by the sum of their weights (at most 1).
    /// One zoom alone comes in and goes; two zooms that overlap by one edge pan from the first place to the second without
    /// zooming out. native/VideoEditing.swift computes the same.
    /// </summary>
    internal static (double X, double Y, double Width, double Height) ViewportAtSorted(double seconds, IEnumerable<ZoomPoint> points)
    {
        double weights = 0, x = 0, y = 0, size = 0;
        foreach (var point in points)
        {
            double age = seconds - point.StartSeconds;
            if (age < 0) break;
            if (age >= point.DurationSeconds) continue;
            double edge = Math.Min(Edge, point.DurationSeconds / 2);
            double t = Math.Min(1, Math.Min(age, point.DurationSeconds - age) / edge);
            double weight = t * t * (3 - 2 * t);
            double targetSize = 1 / point.Scale;
            weights += weight;
            x += Math.Clamp(point.X - targetSize / 2, 0, 1 - targetSize) * weight;
            y += Math.Clamp(point.Y - targetSize / 2, 0, 1 - targetSize) * weight;
            size += targetSize * weight;
        }
        if (weights <= 0) return (0, 0, 1, 1);
        double strength = Math.Min(1, weights);
        double shown = 1 + (size / weights - 1) * strength;
        return (x / weights * strength, y / weights * strength, shown, shown);
    }

    public static Task ExportAsync(string sourcePath, string targetPath, IReadOnlyList<ZoomPoint> zoomPoints,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ExportAsync(sourcePath, targetPath, zoomPoints, [], progress, cancellationToken);

    /// <summary>Renders the zooms and, for every click in <paramref name="clicks"/>, its ring (<see cref="ClickEffect"/>).</summary>
    public static async Task ExportAsync(string sourcePath, string targetPath, IReadOnlyList<ZoomPoint> zoomPoints,
        IReadOnlyList<InputEvent> clicks, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
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
        var rings = clicks.Where(c => c.Kind == InputKind.Click && c.IsValid).OrderBy(c => c.Seconds).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".thixshot-export-{Guid.NewGuid():N}.mp4");
        try
        {
            if (points.Length == 0 && rings.Length == 0)
            {
                await using var input = File.OpenRead(sourcePath);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(async () =>
                {
                    if (OperatingSystem.IsWindows()) WindowsVideoEditor.Export(sourcePath, temporary, points, rings, progress, cancellationToken);
                    else await MacVideoEditor.ExportAsync(sourcePath, temporary, points, rings, progress, cancellationToken).ConfigureAwait(false);
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
