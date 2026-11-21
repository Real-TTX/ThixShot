using System.Diagnostics;

namespace ThixShot.Library;

internal sealed record MediaItem(string Path, string Title, bool IsVideo, DateTime Created, long Bytes)
{
    public string Details => $"{(IsVideo ? "MP4" : "PNG")}  ·  {Created:g}  ·  {(Bytes >= 1048576 ? $"{Bytes / 1048576d:0.#} MB" : $"{Math.Max(1, Bytes / 1024)} KB")}";
}

internal static class MediaLibrary
{
    public static string Folder { get; } = AppContext.GetData("ThixShot.MediaDirectory") as string ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThixShot", "Media");

    public static IReadOnlyList<MediaItem> List()
    {
        Directory.CreateDirectory(Folder);
        return new DirectoryInfo(Folder).EnumerateFiles()
            .Where(file => IsMedia(file.Extension) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Select(file => new MediaItem(file.FullName, Path.GetFileNameWithoutExtension(file.Name),
                file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase), file.CreationTime, file.Length))
            .OrderByDescending(item => item.Created).ToList();
    }

    public static string TemporaryFile(string extension)
    {
        if (!IsMedia(extension)) throw new ArgumentException("Unbekanntes Dateiformat.");
        var staging = Path.Combine(Folder, ".work");
        Directory.CreateDirectory(staging);
        return Path.Combine(staging, Guid.NewGuid().ToString("N") + extension);
    }

    public static string Import(string temporaryPath, string label)
    {
        var extension = Path.GetExtension(temporaryPath);
        if (!IsMedia(extension) || !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            throw new IOException("Die Aufnahme ist leer oder fehlt.");
        var destination = Resolve($"{label} {DateTime.Now:yyyy-MM-dd HH-mm-ss}", extension);
        for (int copy = 2; File.Exists(destination); copy++)
            destination = Resolve($"{label} {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({copy})", extension);
        File.Move(temporaryPath, destination);
        return destination;
    }

    public static string RequireItem(string path)
    {
        string full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), Folder, comparison) || !IsMedia(Path.GetExtension(full))
            || !File.Exists(full) || File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Die Aufnahme ist in der Mediathek nicht verfügbar.");
        return full;
    }

    public static void Rename(string path, string title)
    {
        var source = RequireItem(path);
        var target = Resolve(title.Trim(), Path.GetExtension(source));
        if (source != target) File.Move(source, target);
    }

    public static void Delete(string path) => File.Delete(RequireItem(path));

    public static void OpenVideo(string path)
    {
        var file = RequireItem(path);
        if (!Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Bitte ein Video auswählen.");
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private static string Resolve(string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Bitte einen gültigen Namen mit höchstens 150 Zeichen eingeben.");
        return Path.Combine(Folder, name + extension);
    }

    private static bool IsMedia(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
}
