using Microsoft.Extensions.Configuration;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class DemoModeTests
{
    [Fact]
    public void DemoModeOptions_DefaultsToDisabled()
    {
        var config = new ConfigurationBuilder().Build();
        var options = DemoModeOptions.FromConfiguration(config);

        Assert.False(options.Enabled);
        Assert.Equal(3, options.SimulationDelaySeconds);
        Assert.Equal(30, options.RunningDurationSeconds);
        Assert.False(options.AutoPrepareWarehouseDemands);
        Assert.Equal("DEMO-AGV-01", options.SimulatedAgvId);
    }

    [Fact]
    public void DemoModeOptions_ReadsConfigurationValue()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["DemoMode:Enabled"] = "true",
            ["DemoMode:SimulationDelaySeconds"] = "5",
            ["DemoMode:RunningDurationSeconds"] = "20",
            ["DemoMode:AutoPrepareWarehouseDemands"] = "false",
            ["DemoMode:SimulatedAgvId"] = "DEMO-BOT-99"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var options = DemoModeOptions.FromConfiguration(config);

        Assert.True(options.Enabled);
        Assert.Equal(5, options.SimulationDelaySeconds);
        Assert.Equal(20, options.RunningDurationSeconds);
        Assert.False(options.AutoPrepareWarehouseDemands);
        Assert.Equal("DEMO-BOT-99", options.SimulatedAgvId);
    }

    [Fact]
    public void DemoModeOptions_ReadsEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable("TKG_DEMO_MODE");
        try
        {
            Environment.SetEnvironmentVariable("TKG_DEMO_MODE", "true");
            var config = new ConfigurationBuilder().Build();
            var options = DemoModeOptions.FromConfiguration(config);

            Assert.True(options.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TKG_DEMO_MODE", original);
        }
    }

    [Fact]
    public void DemoModeOptions_Validate_RejectsRemoteRdsEndpoint()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["DemoMode:Enabled"] = "true",
            ["Rds:BaseUrl"] = "http://192.168.1.100:8080"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var options = DemoModeOptions.FromConfiguration(config);

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(config));
        Assert.Contains("DEMO_MODE_SAFETY_VIOLATION", ex.Message);
        Assert.Contains("192.168.1.100", ex.Message);
    }

    [Fact]
    public void DemoModeOptions_Validate_RejectsRemoteRdsCoreEndpoint()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["DemoMode:Enabled"] = "true",
            ["RdsCore:BaseUrl"] = "http://seer-core.plant.local:8088"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var options = DemoModeOptions.FromConfiguration(config);

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(config));
        Assert.Contains("DEMO_MODE_SAFETY_VIOLATION", ex.Message);
    }

    [Fact]
    public void DemoModeOptions_Validate_AllowsLoopbackEndpoint()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["DemoMode:Enabled"] = "true",
            ["Rds:BaseUrl"] = "http://127.0.0.1:8080",
            ["RdsCore:BaseUrl"] = "http://localhost:8088"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var options = DemoModeOptions.FromConfiguration(config);

        // Does not throw
        options.Validate(config);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void RdsPhysicalAvailabilityPolicy_SimulationMode_BypassesStaleEvidence()
    {
        var staleEvidence = new RdsPhysicalEvidence(
            Type: 1,
            Filled: 0,
            Locked: 0,
            Disabled: 0,
            SyncFailed: 0,
            LastSyncAt: null); // Never synced

        // Production policy: fails closed
        var prodPolicy = new RdsPhysicalAvailabilityPolicy(isSimulationMode: false);
        Assert.Equal("NEVER_SYNCED", prodPolicy.State(staleEvidence));
        Assert.Equal("PHYSICAL_STATE_UNKNOWN", prodPolicy.Consistency("VACANT", staleEvidence));
        Assert.False(prodPolicy.IsDestinationAvailable(staleEvidence));

        // Simulation/Demo policy: permits operations without fake database telemetry
        var simPolicy = new RdsPhysicalAvailabilityPolicy(isSimulationMode: true);
        Assert.Equal("AVAILABLE", simPolicy.State(staleEvidence));
        Assert.Equal("CONSISTENT", simPolicy.Consistency("VACANT", staleEvidence));
        Assert.True(simPolicy.IsDestinationAvailable(staleEvidence));
    }

    [Fact]
    public void RdsPhysicalAvailabilityPolicy_SimulationMode_PermitsOperationsRegardlessOfPhysicalEvidence()
    {
        var filledEvidence = new RdsPhysicalEvidence(
            Type: 1,
            Filled: 1,
            Locked: 0,
            Disabled: 0,
            SyncFailed: 0,
            LastSyncAt: DateTimeOffset.UtcNow.AddHours(-10)); // Stale timestamp

        var simPolicy = new RdsPhysicalAvailabilityPolicy(isSimulationMode: true);
        Assert.Equal("AVAILABLE", simPolicy.State(filledEvidence));
        Assert.Equal("CONSISTENT", simPolicy.Consistency("OCCUPIED", filledEvidence));
        Assert.Equal("CONSISTENT", simPolicy.Consistency("VACANT", filledEvidence));
        Assert.True(simPolicy.IsDestinationAvailable(filledEvidence));
    }

    [Fact]
    public void DemoSimulation_ExpertActionResult_ProvidesSimulatedSuccess()
    {
        var requestId = Guid.NewGuid();
        var actionResult = new RdsExpertActionResult(
            requestId,
            "DEMO-1001",
            "DEMO-AGV-01",
            0,
            "Görev manuel olarak tamamlandı.",
            true);

        Assert.True(actionResult.Accepted);
        Assert.Equal(0, actionResult.Code);
        Assert.Equal("DEMO-AGV-01", actionResult.Vehicle);
        Assert.Equal("DEMO-1001", actionResult.TaskRecordId);
    }

    [Fact]
    public void DemoModeOptions_ClampsRunningDurationBounds()
    {
        var lowConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoMode:RunningDurationSeconds"] = "1"
        }).Build();
        var lowOptions = DemoModeOptions.FromConfiguration(lowConfig);
        Assert.Equal(3, lowOptions.RunningDurationSeconds);

        var highConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoMode:RunningDurationSeconds"] = "500"
        }).Build();
        var highOptions = DemoModeOptions.FromConfiguration(highConfig);
        Assert.Equal(120, highOptions.RunningDurationSeconds);
    }

    [Fact]
    public void DemoModeOptions_ReadsAutoPrepareFromEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable("TKG_DEMO_AUTO_PREPARE");
        try
        {
            Environment.SetEnvironmentVariable("TKG_DEMO_AUTO_PREPARE", "true");
            var config = new ConfigurationBuilder().Build();
            var options = DemoModeOptions.FromConfiguration(config);

            Assert.True(options.AutoPrepareWarehouseDemands);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TKG_DEMO_AUTO_PREPARE", original);
        }
    }
}
