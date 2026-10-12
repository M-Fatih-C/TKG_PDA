using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TkgPda.Api;

public static class TkgSecureConfig
{
    private const string DpapiPrefix = "dpapi:";

    public static string UnprotectIfNeeded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.StartsWith(DpapiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            return value;
        }

        try
        {
            return UnprotectWindows(value[DpapiPrefix.Length..]);
        }
        catch
        {
            return value;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindows(string cipherBase64)
    {
        var cipherBytes = Convert.FromBase64String(cipherBase64);
        try
        {
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return plainText;

        return ProtectWindows(plainText);
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindows(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.LocalMachine);
        return DpapiPrefix + Convert.ToBase64String(cipherBytes);
    }
}
