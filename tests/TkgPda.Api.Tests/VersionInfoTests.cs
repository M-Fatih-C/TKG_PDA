using System.Reflection;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class VersionInfoTests
{
    [Fact]
    public void VersionAndCommit_AreReadFromInformationalVersion()
    {
        var informationalVersion = typeof(TkgVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var parts = informationalVersion.Split('+', 2);

        Assert.Equal(parts[0], TkgVersionInfo.Version);
        Assert.Equal(parts.Length == 2 ? parts[1] : "unknown", TkgVersionInfo.CommitHash);
    }
}
