namespace TkgPda.Api;

public sealed class DemoModeOptions
{
    public bool Enabled { get; set; }
    public int SimulationDelaySeconds { get; set; } = 3;
    public int RunningDurationSeconds { get; set; } = 30;
    public bool AutoPrepareWarehouseDemands { get; set; } = false;
    public string SimulatedAgvId { get; set; } = "DEMO-AGV-01";

    public static DemoModeOptions FromConfiguration(IConfiguration configuration)
    {
        var envVar = Environment.GetEnvironmentVariable("TKG_DEMO_MODE");
        var envEnabled = !string.IsNullOrWhiteSpace(envVar) &&
            (string.Equals(envVar, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(envVar, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(envVar, "yes", StringComparison.OrdinalIgnoreCase));

        var configEnabled = configuration.GetValue("DemoMode:Enabled", false);
        var enabled = envEnabled || configEnabled;

        var delay = configuration.GetValue("DemoMode:SimulationDelaySeconds", 3);
        if (delay < 1) delay = 1;
        if (delay > 60) delay = 60;

        var runningDuration = configuration.GetValue("DemoMode:RunningDurationSeconds", 30);
        if (runningDuration < 3) runningDuration = 3;
        if (runningDuration > 120) runningDuration = 120;

        var autoPrepareEnv = Environment.GetEnvironmentVariable("TKG_DEMO_AUTO_PREPARE");
        var autoPrepare = !string.IsNullOrWhiteSpace(autoPrepareEnv)
            ? (string.Equals(autoPrepareEnv, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(autoPrepareEnv, "1", StringComparison.OrdinalIgnoreCase))
            : configuration.GetValue("DemoMode:AutoPrepareWarehouseDemands", false);

        var agvId = configuration["DemoMode:SimulatedAgvId"]?.Trim();
        if (string.IsNullOrWhiteSpace(agvId)) agvId = "DEMO-AGV-01";

        return new DemoModeOptions
        {
            Enabled = enabled,
            SimulationDelaySeconds = delay,
            RunningDurationSeconds = runningDuration,
            AutoPrepareWarehouseDemands = autoPrepare,
            SimulatedAgvId = agvId
        };
    }

    public void Validate(IConfiguration configuration)
    {
        if (!Enabled) return;

        // Safety Invariant: Demo Mode must NEVER connect or send commands to real external field hardware/RDS.
        var rdsBaseUrl = configuration["Rds:BaseUrl"];
        var rdsCoreBaseUrl = configuration["RdsCore:BaseUrl"];
        var m4BaseUrl = configuration["M4:BaseUrl"];

        AssertNotRemoteUrl("Rds:BaseUrl", rdsBaseUrl);
        AssertNotRemoteUrl("RdsCore:BaseUrl", rdsCoreBaseUrl);
        AssertNotRemoteUrl("M4:BaseUrl", m4BaseUrl);
    }

    private static void AssertNotRemoteUrl(string key, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        if (!uri.IsLoopback && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DEMO_MODE_SAFETY_VIOLATION: Demo mode cannot be enabled while {key} is configured with remote host '{uri.Host}'. " +
                "Demo mode must remain strictly offline and simulated, with zero connection to real field equipment.");
        }
    }
}
