using System.Text.Json;
using ThixShot.Capture;

namespace ThixShot.Library;

internal sealed record VideoProject(Guid Revision, bool RecordedWithMaster, long SourceSize, long SourceTimestamp, ZoomPoint[] Zooms);

/// <summary>The library MP4 is the original. Edits are atomic JSON revisions and each revision owns its rendered MP4.</summary>
internal static class VideoProjects
{
    private static string Root => Path.Combine(MediaLibrary.Folder, ".edits");
    private static string Metadata(string path) => Path.Combine(Root, Path.GetFileName(path) + ".json");
    private static string UnversionedRender(Guid revision) => Path.Combine(Root, $"{revision:N}.mp4");
    // Windows renders produced before the stride correction may contain skewed frames.
    private static string WindowsRender(Guid revision) => Path.Combine(Root, $"{revision:N}.win-mf-v2.mp4");
    private static string Rendered(Guid revision) => OperatingSystem.IsWindows() ? WindowsRender(revision) : UnversionedRender(revision);

    public static VideoProject Read(string path)
    {
        path = RequireVideo(path);
        var info = new FileInfo(path);
        var empty = new VideoProject(Guid.Empty, false, info.Length, info.LastWriteTimeUtc.Ticks, []);
        if (!File.Exists(Metadata(path))) return empty;
        var stored = JsonSerializer.Deserialize<VideoProject>(File.ReadAllBytes(Metadata(path)));
        if (stored is null || stored.Zooms is null) throw new InvalidDataException("Das Videoprojekt konnte nicht gelesen werden.");
        return stored.SourceSize == empty.SourceSize && stored.SourceTimestamp == empty.SourceTimestamp ? stored : empty;
    }

    public static VideoProject Save(string path, VideoProject baseline, IReadOnlyList<ZoomPoint> zooms, bool newRecording = false)
    {
        using var writeLock = Acquire();
        var current = Read(path);
        if (current.Revision != baseline.Revision || current.SourceSize != baseline.SourceSize || current.SourceTimestamp != baseline.SourceTimestamp)
            throw new IOException("Die Aufnahme wurde inzwischen geändert. Öffne den Editor erneut.");
        foreach (var zoom in zooms)
        {
            if (!double.IsFinite(zoom.StartSeconds) || zoom.StartSeconds < 0 || !double.IsFinite(zoom.DurationSeconds) || zoom.DurationSeconds <= 0
                || !double.IsFinite(zoom.X) || zoom.X is < 0 or > 1 || !double.IsFinite(zoom.Y) || zoom.Y is < 0 or > 1
                || !double.IsFinite(zoom.Scale) || zoom.Scale is < 1 or > 16)
                throw new ArgumentException("Der Zoom enthält ungültige Zeit- oder Bildkoordinaten.");
        }
        var updated = current with
        {
            Revision = Guid.NewGuid(),
            RecordedWithMaster = current.RecordedWithMaster || newRecording,
            Zooms = zooms.OrderBy(z => z.StartSeconds).ToArray()
        };
        var tmp = Path.Combine(Root, updated.Revision + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(updated));
            File.Move(tmp, Metadata(path), overwrite: true);
        }
        finally { File.Delete(tmp); }
        RemoveCache(current.Revision);
        return updated;
    }

    public static async Task<string> PlaybackPathAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var project = Read(path);
        if (project.Zooms.Length == 0) return path;
        string rendered = Rendered(project.Revision);
        if (File.Exists(rendered))
        {
            if (OperatingSystem.IsWindows()) TryDeleteRender(UnversionedRender(project.Revision));
            return rendered;
        }
        string tmp = MediaLibrary.TemporaryFile(".mp4");
        try
        {
            await VideoEditService.ExportAsync(path, tmp, project.Zooms, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            using var writeLock = await AcquireForRenderAsync(ct).ConfigureAwait(false);
            if (Read(path).Revision != project.Revision)
                throw new IOException("Die Zoomeinstellungen wurden während des Renderns geändert.");
            if (!File.Exists(rendered)) File.Move(tmp, rendered);
            if (OperatingSystem.IsWindows()) TryDeleteRender(UnversionedRender(project.Revision));
            return rendered;
        }
        finally { File.Delete(tmp); }
    }

    public static void Rename(string source, string target)
    {
        using var writeLock = Acquire();
        bool hasEdits = File.Exists(Metadata(source));
        File.Move(source, target);
        try { if (hasEdits) File.Move(Metadata(source), Metadata(target)); }
        catch { File.Move(target, source); throw; }
    }

    public static void Delete(string path)
    {
        using var writeLock = Acquire();
        var project = Read(path);
        File.Delete(path);
        File.Delete(Metadata(path));
        RemoveCache(project.Revision);
    }
    private static void RemoveCache(Guid revision)
    {
        if (revision == Guid.Empty) return;
        TryDeleteRender(UnversionedRender(revision));
        TryDeleteRender(WindowsRender(revision));
    }
    private static void TryDeleteRender(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { /* An older application or player can still be using the render. */ }
        catch (UnauthorizedAccessException) { /* Removing an obsolete cache is optional. */ }
    }
    private static async Task<FileStream> AcquireForRenderAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return Acquire(); }
            catch (IOException ex) when (attempt < 100 && (ex.HResult & 0xffff) is 11 or 32 or 33 or 35)
            {
                // Another app instance is committing an edit or the same render.
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
        }
    }
    private static string RequireVideo(string path)
    {
        var full = MediaLibrary.RequireItem(path);
        if (!Path.GetExtension(full).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Für ein Videoprojekt ist eine MP4-Aufnahme erforderlich.");
        if (Directory.Exists(Root) && File.GetAttributes(Root).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Der Projektordner verweist auf einen anderen Speicherort.");
        return full;
    }
    private static FileStream Acquire()
    {
        var directory = Directory.CreateDirectory(Root);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException();
        return new FileStream(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
