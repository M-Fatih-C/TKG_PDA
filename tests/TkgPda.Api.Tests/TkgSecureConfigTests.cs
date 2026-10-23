using Xunit;

namespace TkgPda.Api.Tests;

public class TkgSecureConfigTests
{
    [Fact]
    public void UnprotectIfNeeded_ReturnsEmpty_WhenValueIsNullOrWhiteSpace()
    {
        Assert.Equal(string.Empty, TkgSecureConfig.UnprotectIfNeeded(null));
        Assert.Equal(string.Empty, TkgSecureConfig.UnprotectIfNeeded(""));
        Assert.Equal(string.Empty, TkgSecureConfig.UnprotectIfNeeded("   "));
    }

    [Fact]
    public void UnprotectIfNeeded_ReturnsOriginal_WhenNotPrefixedWithDpapi()
    {
        var raw = "Host=127.0.0.1;Port=5432;Database=tkg_pda;";
        Assert.Equal(raw, TkgSecureConfig.UnprotectIfNeeded(raw));
    }

    [Fact]
    public void UnprotectIfNeeded_ThrowsInvalidOperationException_WhenCiphertextCorrupted()
    {
        var corrupted = "dpapi:NotValidBase64OrCorruptedPayload";
        var ex = Assert.Throws<InvalidOperationException>(() => TkgSecureConfig.UnprotectIfNeeded(corrupted));
        Assert.Contains("DPAPI-protected", ex.Message);
    }

    [Fact]
    public void ProtectAndUnprotect_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var original = "SuperSecretPassword123!";
        var protectedValue = TkgSecureConfig.Protect(original);

        Assert.StartsWith("dpapi:", protectedValue);
        Assert.NotEqual(original, protectedValue);

        var restored = TkgSecureConfig.UnprotectIfNeeded(protectedValue);
        Assert.Equal(original, restored);
    }
}
