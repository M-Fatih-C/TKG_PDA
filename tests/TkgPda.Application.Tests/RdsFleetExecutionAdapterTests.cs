using System.Text.Json;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class RdsFleetExecutionAdapterTests
{
    [Fact]
    public async Task Create_preserves_external_identity_template_priority_and_inputs()
    {
        var inner = new FakeRdsClient();
        var adapter = new RdsFleetExecutionAdapter(inner);
        var request = Request();
        var correlationId = Guid.NewGuid();

        var result = await adapter.CreateOrderAsync(request, correlationId);

        Assert.Equal(FleetCreateDisposition.Created, result.Disposition);
        Assert.Equal(request.ExternalId, result.ExternalOrderId);
        Assert.Equal(request.ExternalId, inner.Validated!.TaskRecordId);
        Assert.Equal(request.ProviderTemplate, inner.Validated.TaskLabel);
        Assert.Equal(request.ExternalId, inner.Created!.TaskRecordId);
        Assert.Equal(request.ProviderTemplate, inner.Created.TaskLabel);
        Assert.Equal(request.Priority, inner.Created.Priority);
        Assert.Same(request.InputParameters, inner.Created.InputParams);
        Assert.Equal(correlationId, inner.CorrelationId);
    }

    [Fact]
    public async Task Idempotent_Rds_replay_maps_to_existing_order()
    {
        var inner = new FakeRdsClient { IdempotentReplay = true };
        var adapter = new RdsFleetExecutionAdapter(inner);

        var result = await adapter.CreateOrderAsync(Request(), Guid.NewGuid());

        Assert.Equal(FleetCreateDisposition.Existing, result.Disposition);
    }

    [Fact]
    public async Task Rejected_validation_never_calls_create()
    {
        var inner = new FakeRdsClient { ValidationAccepted = false };
        var adapter = new RdsFleetExecutionAdapter(inner);

        var error = await Assert.ThrowsAsync<FleetExecutionException>(() =>
            adapter.CreateOrderAsync(Request(), Guid.NewGuid()));

        Assert.Equal("RDS_VALIDATION_REJECTED", error.Code);
        Assert.Null(inner.Created);
    }

    [Fact]
    public async Task Cancellation_delegates_to_existing_Rds_safety_stop()
    {
        var inner = new FakeRdsClient { DispatchEnabled = false };
        var adapter = new RdsFleetExecutionAdapter(inner);

        var result = await adapter.CancelOrderAsync("immutable-record-id", Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal("immutable-record-id", inner.StoppedId);
    }

    private static FleetCreateOrderRequest Request()
    {
        var inputs = new Dictionary<string, object?>
        {
            ["fromSite"] = "SIM-A",
            ["toSite"] = "SIM-B"
        };
        return new(
            Guid.NewGuid(),
            Guid.NewGuid().ToString(),
            "DEV-RDS-SIM",
            "TKG_SIM_TRANSPORT",
            100,
            null,
            [],
            inputs);
    }

    private sealed class FakeRdsClient : IRdsExecutionClient
    {
        public bool DispatchEnabled { get; init; } = true;
        public bool ValidationAccepted { get; init; } = true;
        public bool IdempotentReplay { get; init; }
        public RdsValidateTransportRequest? Validated { get; private set; }
        public RdsCreateTransportRequest? Created { get; private set; }
        public string? StoppedId { get; private set; }
        public Guid? CorrelationId { get; private set; }

        public Task<RdsValidationResult> ValidateTransportAsync(RdsValidateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default)
        {
            Validated = request;
            CorrelationId = correlationId;
            return Task.FromResult(new RdsValidationResult(
                ValidationAccepted,
                true,
                false,
                ValidationAccepted ? 200 : 409,
                ValidationAccepted ? "ok" : "rejected",
                Json("{}")));
        }

        public Task<RdsCreateTransportResult> CreateTransportAsync(RdsCreateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default)
        {
            Created = request;
            CorrelationId = correlationId;
            return Task.FromResult(new RdsCreateTransportResult(true, IdempotentReplay, 200, "ok", Json("{}")));
        }

        public Task<RdsStopTaskResult> StopTaskAsync(string taskRecordId, Guid correlationId, CancellationToken cancellationToken = default)
        {
            StoppedId = taskRecordId;
            CorrelationId = correlationId;
            return Task.FromResult(new RdsStopTaskResult(200, "ok", Json("{}")));
        }

        public Task<RdsContentUpdateResult> UpdateContentAsync(string siteId, string content, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    }
}
