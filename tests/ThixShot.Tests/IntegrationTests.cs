using System.Security.Cryptography;
using ThixShot.Integration;
using ThixShot.Library;

namespace ThixShot.Tests;

public sealed class IntegrationTests
{
    [Theory]
    [InlineData("file:///tmp/cloud")]
    [InlineData("http://example.org")]
    [InlineData("https://user:password@example.org")]
    public async Task Pairing_checks_the_cloud_address_before_opening_a_browser(string address)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CloudService.ConnectAsync(address, (_, _) => Assert.Fail("Unexpected pairing request.")));
    }

    [Fact]
    public async Task Cloud_upload_requires_library_membership_and_a_paired_device()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        await Assert.ThrowsAsync<IOException>(() => CloudService.UploadAsync(outside));
        var staging = MediaLibrary.TemporaryFile(".png");
        using (var image = TestProfile.TestImage()) await File.WriteAllBytesAsync(staging, image.ToArray());
        var item = MediaLibrary.Import(staging, "Cloudtest");
        try { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CloudService.UploadAsync(item)); }
        finally { MediaLibrary.Delete(item); }
    }

    [Fact]
    public void Windows_cloud_credentials_are_bound_to_the_user_and_origin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = Guid.NewGuid().ToString();
        const string origin = "https://cloud.example.org";
        var encrypted = CloudCredentials.Store(origin, token);
        Assert.DoesNotContain(token, encrypted);
        Assert.Equal(token, CloudCredentials.Read(origin, encrypted));
        Assert.Throws<CryptographicException>(() => CloudCredentials.Read("https://example.net", encrypted));
    }
}
