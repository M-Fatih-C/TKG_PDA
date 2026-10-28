using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class FleetProviderRouterTests
{
    [Fact]
    public void Read_routing_works_while_dispatch_is_closed()
    {
        var m4 = new FakeClient(FleetProvider.M4);
        var router = Router(m4);

        var selected = router.ResolveReadClient(Assignment(dispatchEnabled: false));

        Assert.Same(m4, selected);
    }

    [Fact]
    public void Global_dispatch_gate_fails_closed()
    {
        var router = Router(new FakeClient(FleetProvider.M4));

        var error = Assert.Throws<FleetRoutingException>(() =>
            router.ResolveExecutionClient(Assignment(), new FleetRuntimeGates(false, false)));

        Assert.Equal("FLEET_DISPATCH_DISABLED", error.Code);
    }

    [Fact]
    public void Emergency_gate_fails_closed()
    {
        var router = Router(new FakeClient(FleetProvider.M4));

        var error = Assert.Throws<FleetRoutingException>(() =>
            router.ResolveExecutionClient(Assignment(), new FleetRuntimeGates(true, true)));

        Assert.Equal("FLEET_EMERGENCY_BLOCKED", error.Code);
    }

    [Fact]
    public void Commissioning_dispatch_gate_fails_closed()
    {
        var router = Router(new FakeClient(FleetProvider.M4));

        var error = Assert.Throws<FleetRoutingException>(() =>
            router.ResolveExecutionClient(Assignment(dispatchEnabled: false), new FleetRuntimeGates(true, false)));

        Assert.Equal("FLEET_PROVIDER_DISPATCH_DISABLED", error.Code);
    }

    [Fact]
    public void Disabled_execution_client_fails_closed()
    {
        var router = Router(new FakeClient(FleetProvider.M4) { DispatchEnabled = false });

        var error = Assert.Throws<FleetRoutingException>(() =>
            router.ResolveExecutionClient(Assignment(), new FleetRuntimeGates(true, false)));

        Assert.Equal("FLEET_EXECUTION_CLIENT_DISABLED", error.Code);
    }

    [Fact]
    public void Missing_selected_provider_never_falls_back_to_another_provider()
    {
        var router = Router(new FakeClient(FleetProvider.Rds));

        var error = Assert.Throws<FleetRoutingException>(() =>
            router.ResolveExecutionClient(Assignment(), new FleetRuntimeGates(true, false)));

        Assert.Equal("FLEET_PROVIDER_CLIENT_NOT_REGISTERED", error.Code);
    }

    [Fact]
    public void Cancellation_client_remains_available_when_new_dispatch_is_closed()
    {
        var m4 = new FakeClient(FleetProvider.M4) { DispatchEnabled = false };
        var router = Router(m4);

        var selected = router.ResolveCancellationClient(Assignment(dispatchEnabled: false));

        Assert.Same(m4, selected);
    }

    [Fact]
    public void Duplicate_provider_registration_is_rejected()
    {
        var first = new FakeClient(FleetProvider.M4);
        var second = new FakeClient(FleetProvider.M4);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new FleetProviderRouter([first, second], [first]));

        Assert.Contains("More than one read client", error.Message, StringComparison.Ordinal);
    }

    private static FleetProviderAssignment Assignment(bool dispatchEnabled = true) =>
        new(FleetProvider.M4, Enabled: true, ReadEnabled: true, DispatchEnabled: dispatchEnabled, SimulationMode: true, ExternalScopeId: "OneDemo");

    private static FleetProviderRouter Router(params FakeClient[] clients) => new(clients, clients);

    private sealed class FakeClient(FleetProvider provider) : IFleetReadClient, IFleetExecutionClient
    {
        public FleetProvider Provider { get; } = provider;
        public bool ReadEnabled { get; init; } = true;
        public bool DispatchEnabled { get; init; } = true;

        public Task<FleetProviderHealth> GetHealthAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FleetOrderSnapshot?> FindOrderByExternalIdAsync(string externalId, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FleetCreateOrderResult> CreateOrderAsync(FleetCreateOrderRequest request, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FleetCancellationResult> CancelOrderAsync(string externalOrderId, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
