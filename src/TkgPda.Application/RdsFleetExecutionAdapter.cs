namespace TkgPda.Application;

public sealed class FleetExecutionException(
    string code,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}

/// <summary>
/// Compatibility boundary that exposes the existing, tested RDS client through
/// the provider-neutral fleet contract. It does not alter the RDS coordinator or
/// enable routing by itself.
/// </summary>
public sealed class RdsFleetExecutionAdapter(IRdsExecutionClient client) : IFleetExecutionClient
{
    public FleetProvider Provider => FleetProvider.Rds;
    public bool DispatchEnabled => client.DispatchEnabled;

    public async Task<FleetCreateOrderResult> CreateOrderAsync(
        FleetCreateOrderRequest request,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProviderTemplate))
        {
            throw new FleetExecutionException(
                "FLEET_PROVIDER_TEMPLATE_REQUIRED",
                "RDS dispatch requires an explicitly commissioned provider template.");
        }

        var validation = await client.ValidateTransportAsync(
            new RdsValidateTransportRequest(
                request.ExternalId,
                request.ProviderTemplate,
                request.InputParameters),
            correlationId,
            cancellationToken);
        if (!validation.Validated)
        {
            throw new FleetExecutionException(
                "RDS_VALIDATION_REJECTED",
                string.IsNullOrWhiteSpace(validation.Message)
                    ? "RDS rejected the provider-neutral transport validation request."
                    : validation.Message);
        }

        var result = await client.CreateTransportAsync(
            new RdsCreateTransportRequest(
                request.ExternalId,
                request.ProviderTemplate,
                request.Priority,
                request.InputParameters),
            correlationId,
            cancellationToken);
        if (!result.Accepted)
        {
            throw new FleetExecutionException(
                "RDS_CREATE_REJECTED",
                string.IsNullOrWhiteSpace(result.Message)
                    ? "RDS rejected the provider-neutral create request."
                    : result.Message);
        }

        return new FleetCreateOrderResult(
            result.IdempotentReplay ? FleetCreateDisposition.Existing : FleetCreateDisposition.Created,
            request.ExternalId,
            request.ExternalId,
            result.Code.ToString(),
            result.Message);
    }

    public async Task<FleetCancellationResult> CancelOrderAsync(
        string externalOrderId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalOrderId))
        {
            throw new FleetExecutionException(
                "FLEET_EXTERNAL_ORDER_ID_REQUIRED",
                "Cancellation requires the immutable external order identity.");
        }

        var result = await client.StopTaskAsync(externalOrderId, correlationId, cancellationToken);
        return new FleetCancellationResult(
            externalOrderId,
            Accepted: result.Code == 200,
            AlreadyTerminal: false,
            result.Code.ToString(),
            result.Message);
    }
}
