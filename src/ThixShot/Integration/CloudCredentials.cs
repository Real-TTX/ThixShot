using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ThixShot.Integration;

// The DPAPI entropy / Keychain account binds the device token to this particular server.
internal static class CloudCredentials
{
    private const string Framework = "/System/Library/Frameworks/Security.framework/Security";
    private static readonly byte[] Service = "de.thixapps.thixshot.cloud"u8.ToArray();

    public static string Store(string server, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var account = Encoding.UTF8.GetBytes(server);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Convert.ToBase64String(ProtectedData.Protect(bytes, account, DataProtectionScope.CurrentUser));
            }
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();

            var status = SecKeychainAddGenericPassword(0, Service.Length, Service, account.Length, account, bytes.Length, bytes, 0);
            if (status == -25299) // An existing Keychain item, e.g. after restoring old settings.
            {
                Check(SecKeychainFindGenericPassword(0, Service.Length, Service, account.Length, account, 0, 0, out var item));
                try { status = SecKeychainItemModifyAttributesAndData(item, 0, bytes.Length, bytes); }
                finally { CFRelease(item); }
            }
            Check(status);
            return "";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Read(string server, string encrypted)
    {
        var account = Encoding.UTF8.GetBytes(server);
        if (OperatingSystem.IsWindows())
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), account, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        Check(FindPasswordData(0, Service.Length, Service, account.Length, account, out var length, out var data, 0));
        try { return Marshal.PtrToStringUTF8(data, length)!; }
        finally { SecKeychainItemFreeContent(0, data); }
    }

    public static void Delete(string server)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var account = Encoding.UTF8.GetBytes(server);
        var status = SecKeychainFindGenericPassword(0, Service.Length, Service, account.Length, account, 0, 0, out var item);
        if (status == -25300) return; // errSecItemNotFound
        Check(status);
        try { Check(SecKeychainItemDelete(item)); }
        finally { CFRelease(item); }
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Zugriff auf den macOS-Schlüsselbund fehlgeschlagen ({status}).");
    }

    [DllImport(Framework)] private static extern int SecKeychainAddGenericPassword(nint keychain, int serviceLength, byte[] service, int accountLength, byte[] account, int length, byte[] data, nint item);
    [DllImport(Framework)] private static extern int SecKeychainFindGenericPassword(nint keychain, int serviceLength, byte[] service, int accountLength, byte[] account, nint length, nint data, out nint item);
    [DllImport(Framework, EntryPoint = "SecKeychainFindGenericPassword")] private static extern int FindPasswordData(nint keychain, int serviceLength, byte[] service, int accountLength, byte[] account, out int length, out nint data, nint item);
    [DllImport(Framework)] private static extern int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, int length, byte[] data);
    [DllImport(Framework)] private static extern int SecKeychainItemFreeContent(nint attributes, nint data);
    [DllImport(Framework)] private static extern int SecKeychainItemDelete(nint item);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint item);
}
