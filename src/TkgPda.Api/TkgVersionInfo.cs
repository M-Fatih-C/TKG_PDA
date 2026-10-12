namespace TkgPda.Api;

public static class TkgVersionInfo
{
    public const string Version = "1.0.0";
    public const string ServiceName = "TKG PDA Server";
    public const string DisplayName = "TKG PDA Server";

    public static string CommitHash { get; set; } = "10cf9de7";
    public static string BuildTimestamp { get; set; } = "2026-10-12T09:00:00Z";

    static TkgVersionInfo()
    {
        var asm = typeof(TkgVersionInfo).Assembly;
        var infoVer = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var parts = infoVer.Split('+');
            if (parts.Length > 1)
            {
                CommitHash = parts[1];
            }
            else if (!string.IsNullOrWhiteSpace(parts[0]))
            {
                CommitHash = parts[0];
            }
        }

        var envCommit = Environment.GetEnvironmentVariable("TKG_BUILD_COMMIT");
        if (!string.IsNullOrWhiteSpace(envCommit))
        {
            CommitHash = envCommit;
        }

        var envBuildTime = Environment.GetEnvironmentVariable("TKG_BUILD_TIMESTAMP");
        if (!string.IsNullOrWhiteSpace(envBuildTime))
        {
            BuildTimestamp = envBuildTime;
        }
    }
}
