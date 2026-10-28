namespace TkgPda.Application;

public sealed class FleetRoutingException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Fail-closed provider selection. A commissioned M4 request can never fall
/// back to RDS (or vice versa) because a client is missing or disabled.
/// </summary>
public sealed class FleetProviderRouter : IFleetProviderRouter
{
    private readonly IReadOnlyDictionary<FleetProvider, IFleetReadClient> readClients;
    private readonly IReadOnlyDictionary<FleetProvider, IFleetExecutionClient> executionClients;

    public FleetProviderRouter(
        IEnumerable<IFleetReadClient> readClients,
        IEnumerable<IFleetExecutionClient> executionClients)
    {
        this.readClients = Index(readClients, client => client.Provider, "read");
        this.executionClients = Index(executionClients, client => client.Provider, "execution");
    }

    public IFleetReadClient ResolveReadClient(FleetProviderAssignment assignment)
    {
        EnsureProviderEnabled(assignment);
        if (!assignment.ReadEnabled)
        {
            throw new FleetRoutingException(
                "FLEET_READ_DISABLED",
                $"Read access for fleet provider {assignment.Provider} is disabled by commissioning configuration.");
        }

        if (!readClients.TryGetValue(assignment.Provider, out var client))
        {
            throw MissingClient(assignment.Provider, "read");
        }

        if (!client.ReadEnabled)
        {
            throw new FleetRoutingException(
                "FLEET_READ_CLIENT_DISABLED",
                $"The configured {assignment.Provider} read client is disabled.");
        }

        return client;
    }

    public IFleetExecutionClient ResolveExecutionClient(
        FleetProviderAssignment assignment,
        FleetRuntimeGates runtimeGates)
    {
        EnsureProviderEnabled(assignment);
        if (!runtimeGates.DispatchEnabled)
        {
            throw new FleetRoutingException(
                "FLEET_DISPATCH_DISABLED",
                "Fleet dispatch is globally disabled.");
        }

        if (runtimeGates.EmergencyDispatchBlocked)
        {
            throw new FleetRoutingException(
                "FLEET_EMERGENCY_BLOCKED",
                "Fleet dispatch is blocked by the global emergency gate.");
        }

        if (!assignment.DispatchEnabled)
        {
            throw new FleetRoutingException(
                "FLEET_PROVIDER_DISPATCH_DISABLED",
                $"Dispatch for fleet provider {assignment.Provider} is disabled by commissioning configuration.");
        }

        if (!executionClients.TryGetValue(assignment.Provider, out var client))
        {
            throw MissingClient(assignment.Provider, "execution");
        }

        if (!client.DispatchEnabled)
        {
            throw new FleetRoutingException(
                "FLEET_EXECUTION_CLIENT_DISABLED",
                $"The configured {assignment.Provider} execution client is disabled.");
        }

        return client;
    }

    public IFleetExecutionClient ResolveCancellationClient(FleetProviderAssignment assignment)
    {
        EnsureProviderEnabled(assignment);
        if (!executionClients.TryGetValue(assignment.Provider, out var client))
        {
            throw MissingClient(assignment.Provider, "execution");
        }

        // Cancellation reduces or stops existing motion. It deliberately remains
        // reachable when new dispatch is disabled, matching the established RDS
        // safety-stop boundary.
        return client;
    }

    private static void EnsureProviderEnabled(FleetProviderAssignment assignment)
    {
        if (!assignment.Enabled)
        {
            throw new FleetRoutingException(
                "FLEET_PROVIDER_DISABLED",
                $"Fleet provider {assignment.Provider} is disabled by commissioning configuration.");
        }
    }

    private static FleetRoutingException MissingClient(FleetProvider provider, string kind) => new(
        "FLEET_PROVIDER_CLIENT_NOT_REGISTERED",
        $"No {kind} client is registered for the commissioned fleet provider {provider}.");

    private static IReadOnlyDictionary<FleetProvider, TClient> Index<TClient>(
        IEnumerable<TClient> clients,
        Func<TClient, FleetProvider> provider,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var result = new Dictionary<FleetProvider, TClient>();
        foreach (var client in clients)
        {
            ArgumentNullException.ThrowIfNull(client);
            var key = provider(client);
            if (!result.TryAdd(key, client))
            {
                throw new InvalidOperationException($"More than one {kind} client is registered for fleet provider {key}.");
            }
        }

        return result;
    }
}
