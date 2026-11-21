using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ThixShot.Integration;

public static class UpdateService
{
    private sealed record Package(string File, string Sha256);
    private sealed record Manifest(string Version, string Channel, long BuildNumber, Dictionary<string, Package> Packages);
    private static readonly Assembly Assembly = typeof(UpdateService).Assembly;
    private static readonly string Channel = Metadata("ThixShotChannel") ?? "local";
    private static readonly long Build = long.TryParse(Metadata("ThixShotBuildNumber"), out var build) ? build : 0;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly string OptInFile = Path.Combine(UserFiles.DirectoryPath, "automatic-updates");
    private static readonly string LastAttempt = Path.Combine(UserFiles.DirectoryPath, "update-attempt");
    private static int _installing;

    public static string DisplayVersion { get; } = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";
    public static bool CanUpdate => Channel is "release" or "nightly" && PackageInstaller.IsInstalled;
    public static bool AutoUpdate
    {
        get => File.Exists(OptInFile);
        set
        {
            if (value) UserFiles.Write(OptInFile, "enabled");
            else if (File.Exists(OptInFile)) File.Delete(OptInFile);
        }
    }
    public static bool ShouldCheckAutomatically => AutoUpdate && CanUpdate &&
        (!File.Exists(LastAttempt) || DateTime.UtcNow - File.GetLastWriteTimeUtc(LastAttempt) > TimeSpan.FromHours(6));

    private static Uri ReleaseRoot => new($"https://github.com/Real-TTX/ThixShot/releases/download/{Channel}-channel/");
    private static string Platform => (OperatingSystem.IsMacOS() ? "osx-" : "win-") +
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public static async Task<string?> CheckAsync(CancellationToken ct = default) => (await ReadManifestAsync(ct))?.Version;

    public static async Task InstallAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _installing, 1) != 0)
            throw new InvalidOperationException("Ein Update wird bereits installiert.");
        string? staging = null;
        try
        {
            var manifest = await ReadManifestAsync(ct) ?? throw new InvalidOperationException("ThixShot ist bereits aktuell.");
            var package = manifest.Packages[Platform];
            staging = Path.Combine(UserFiles.DirectoryPath, "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var path = Path.Combine(staging, package.File);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            using (var response = await Http.GetAsync(new Uri(ReleaseRoot, package.File), HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await response.Content.CopyToAsync(target, deadline.Token);
            }
            await using (var file = File.OpenRead(path))
            {
                var hash = await SHA256.HashDataAsync(file, deadline.Token);
                if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(package.Sha256)))
                    throw new InvalidDataException("Die SHA-256-Prüfsumme des Installationspakets ist falsch.");
            }
            UserFiles.Write(LastAttempt, manifest.Version);
            await PackageInstaller.ScheduleAsync(path, ct);
            staging = null; // The installer cleans up after ThixShot exits.
        }
        catch
        {
            Interlocked.Exchange(ref _installing, 0);
            throw;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task<Manifest?> ReadManifestAsync(CancellationToken ct)
    {
        if (!CanUpdate)
            throw new InvalidOperationException("OTA-Updates stehen in einer installierten Release- oder Nightly-Version bereit.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ReleaseRoot, "thixshot-update.json"));
        request.Headers.UserAgent.ParseAdd("ThixShot/" + DisplayVersion);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Für diesen Update-Kanal wurde noch kein Manifest veröffentlicht.");
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<Manifest>(ct);
        if (manifest is null || manifest.Channel != Channel || string.IsNullOrWhiteSpace(manifest.Version) || manifest.BuildNumber <= 0)
            throw new InvalidDataException("Das Update-Manifest ist ungültig.");
        if (manifest.BuildNumber <= Build) return null;
        var expectedName = "ThixShot-" + Platform + (OperatingSystem.IsMacOS() ? ".zip" : ".msi");
        if (manifest.Packages is null || !manifest.Packages.TryGetValue(Platform, out var package) || package is null ||
            package.File != expectedName || package.Sha256 is not { Length: 64 } || !package.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"Kein gültiges Update-Paket für {Platform} gefunden.");
        return manifest;
    }

    private static string? Metadata(string key) =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == key)?.Value;
}
