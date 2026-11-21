using System.Net;
using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Contract.Drive;
using ThixShot.Library;

namespace ThixShot.Integration;

public static class CloudService
{
    private sealed record Connection(string Server, string EncryptedToken);
    private static readonly SemaphoreSlim ConnectionLock = new(1);
    private static readonly HttpClientHandler Transport = new() { AllowAutoRedirect = false, UseCookies = false };
    private static readonly string ConnectionFile = Path.Combine(UserFiles.DirectoryPath, "cloud.json");

    public static bool IsConnected => File.Exists(ConnectionFile);
    public static string ServerUrl => Load()?.Server ?? "";

    public static async Task ConnectAsync(string serverUrl, Action<string, Uri> onCode, CancellationToken ct = default)
    {
        var server = Normalize(serverUrl);
        await ConnectionLock.WaitAsync(ct);
        try
        {
            if (IsConnected) throw new InvalidOperationException("Trenne zuerst die bestehende Cloud-Verbindung.");
            using var anonymous = Client(server, null);
            var request = new DeviceLinkRequest(Environment.MachineName, OperatingSystem.IsMacOS() ? "macOS" : "Windows",
                UpdateService.DisplayVersion, CloudApps.Shot);
            var start = await anonymous.StartLinkAsync(request, ct);
            if (!Uri.TryCreate(start.VerificationUrl, UriKind.Absolute, out var verification) ||
                !string.IsNullOrEmpty(verification.UserInfo) ||
                !(verification.Scheme == Uri.UriSchemeHttps || (verification.Scheme == Uri.UriSchemeHttp && verification.IsLoopback)))
            {
                throw new InvalidDataException("Die Cloud hat eine ungültige Anmeldeadresse geliefert.");
            }
            onCode(start.Code, verification);
            var login = await anonymous.WaitForLinkAsync(start, ct);
            using var authorized = Client(server, login.Token);
            try
            {
                if (login.Account.App != CloudApps.Shot || string.IsNullOrWhiteSpace(login.Token))
                    throw new UnauthorizedAccessException("Die Anmeldung gehört zu einer anderen App.");
                await authorized.GetDriveQuotaAsync(ct); // Checks the app grant and whether Drive is enabled.
                var encrypted = CloudCredentials.Store(server, login.Token);
                UserFiles.Write(ConnectionFile, JsonSerializer.Serialize(new Connection(server, encrypted)));
            }
            catch
            {
                try { await authorized.LogoutAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is CloudException or HttpRequestException or OperationCanceledException) { }
                CloudCredentials.Delete(server);
                throw;
            }
        }
        catch (CloudException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.Field == "app")
        {
            throw new InvalidOperationException("Dieser Server kennt ThixShot noch nicht. Die ThixCloud benötigt die App-Freigabe für „shot“.", ex);
        }
        finally { ConnectionLock.Release(); }
    }

    public static async Task DisconnectAsync()
    {
        await ConnectionLock.WaitAsync();
        try
        {
            var connection = Load();
            if (connection is null) return;
            try
            {
                using var client = AuthorizedClient(connection);
                await client.LogoutAsync(CancellationToken.None);
            }
            catch (CloudException ex) when (ex.IsUnauthorized) { }
            finally
            {
                // Local logout works offline. Server-side devices can also be removed in the account.
                Forget(connection);
            }
        }
        finally { ConnectionLock.Release(); }
    }

    public static async Task UploadAsync(string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        filePath = MediaLibrary.RequireItem(filePath);

        await ConnectionLock.WaitAsync(ct);
        try
        {
            var connection = Load() ?? throw new UnauthorizedAccessException("Verbinde zuerst dein ThixCloud-Konto.");
            using var client = AuthorizedClient(connection);
            try
            {
                var quota = await client.GetDriveQuotaAsync(ct);
                await using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                var size = source.Length;
                if (size == 0) throw new InvalidDataException("Die Aufnahme ist leer.");
                if (size > quota.MaxFileBytes || (quota.QuotaBytes is long limit && size > limit - quota.UsedBytes))
                    throw new IOException("Die Aufnahme überschreitet das Dateilimit oder den freien Speicher deiner ThixCloud.");
                var folderId = await GetFolderAsync(client, ct);
                await client.UploadDriveFileAsync(folderId, Path.GetFileName(filePath), source, DriveNameConflict.Rename, ct,
                    progress is null ? null : new ByteProgress(progress, size));
                progress?.Report(1);
            }
            catch (CloudException ex) when (ex.IsUnauthorized)
            {
                Forget(connection);
                throw;
            }
        }
        finally { ConnectionLock.Release(); }
    }

    private static async Task<long> GetFolderAsync(MatCloudClient client, CancellationToken ct)
    {
        try { return (await client.CreateDriveFolderAsync(null, "ThixShot", ct)).Id; }
        catch (DriveConflictException ex) when (ex.Current is { Kind: DriveItemKind.Folder, ParentId: null, IsTrashed: false } folder)
        {
            return folder.Id;
        }
    }

    private static void Forget(Connection connection)
    {
        CloudCredentials.Delete(connection.Server);
        File.Delete(ConnectionFile);
    }

    private static Connection? Load()
    {
        if (!File.Exists(ConnectionFile)) return null;
        return JsonSerializer.Deserialize<Connection>(File.ReadAllText(ConnectionFile))
            ?? throw new InvalidDataException("Die gespeicherte Cloud-Verbindung ist unvollständig.");
    }

    private static MatCloudClient AuthorizedClient(Connection connection)
    {
        var server = Normalize(connection.Server);
        return Client(server, CloudCredentials.Read(server, connection.EncryptedToken));
    }

    private static MatCloudClient Client(string server, string? token) =>
        new(server, token, $"ThixShot/{UpdateService.DisplayVersion}", Transport);

    private static string Normalize(string address)
    {
        var candidate = address.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException("Gib die HTTPS-Adresse deiner ThixCloud ein.");
        }
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private sealed class ByteProgress(IProgress<double> progress, long size) : IProgress<long>
    {
        public void Report(long bytes) => progress.Report((double)bytes / size);
    }
}
