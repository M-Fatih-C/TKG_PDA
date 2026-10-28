using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// REMOVE_EMPTY is internal orchestration vocabulary, not a standalone Operator
/// action. The Operator's business actions are "Kasa Doldu" (REMOVE_FULL) and
/// "Kasa Boş" (BRING_EMPTY); the Remove-All workflow generates REMOVE_EMPTY
/// server-side for OCCUPIED+EMPTY positions. Hiding it from the PDA action list is
/// not a boundary, so these tests drive the raw HTTP surface directly.
/// </summary>
public sealed class OperatorOperationAuthorityApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Theory]
    [InlineData(RoleCodes.LineOperator)]
    [InlineData(RoleCodes.WarehouseOperator)]
    [InlineData(RoleCodes.Supervisor)]
    public async Task Direct_remove_empty_is_rejected_before_any_transport_is_created(string role)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        // A position REMOVE_EMPTY would genuinely be valid for, so the rejection is
        // an authority decision and not an incidental state mismatch.
        await fixture.SetSlotAsync(slot, "OCCUPIED", "EMPTY");
        var part = await fixture.PartForSlotAsync(slot, $"P05-REMEMPTY-{role}");
        var identity = await fixture.SeedIdentityAsync(role: role);
        using var client = await fixture.LoginClientAsync(identity);

        var payload = new CreateTransportHttpRequest(
            context.MachineId, context.VariantId, context.LayoutId,
            null, null, part, null, "REMOVE_EMPTY");
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
            {
                Content = JsonContent.Create(payload),
                Headers = { { ApiHeaders.IdempotencyKey, $"remove-empty-direct-{role}" } },
            });

        Assert.Equal(role == RoleCodes.WarehouseOperator ? HttpStatusCode.Forbidden : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(role == RoleCodes.WarehouseOperator ? "ROLE_NOT_AUTHORIZED" : "OPERATION_NOT_OPERATOR_SELECTABLE", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Direct_remove_empty_is_rejected_inside_a_transport_batch()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "OCCUPIED", "FULL");
        await fixture.SetSlotAsync(slots[1], "OCCUPIED", "EMPTY");
        var legitimate = await fixture.PartForSlotAsync(slots[0], "P05-BATCHOK");
        var smuggled = await fixture.PartForSlotAsync(slots[1], "P05-BATCHSMUGGLE");
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator);
        using var client = await fixture.LoginClientAsync(identity);

        // A valid REMOVE_FULL item paired with a smuggled REMOVE_EMPTY item: the
        // batch route parses operations per item, so it must apply the same rule.
        var batch = new CreateTransportBatchHttpRequest(
            "remove-empty-batch",
            context.MachineId,
            [
                new CreateTransportBatchItemHttpRequest(
                    "remove-empty-batch-ok", context.MachineId, context.VariantId, context.LayoutId,
                    null, null, legitimate, null, "REMOVE_FULL"),
                new CreateTransportBatchItemHttpRequest(
                    "remove-empty-batch-smuggled", context.MachineId, context.VariantId, context.LayoutId,
                    null, null, smuggled, null, "REMOVE_EMPTY"),
            ]);
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-batches")
            {
                Content = JsonContent.Create(batch),
                Headers = { { ApiHeaders.IdempotencyKey, "remove-empty-batch" } },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("OPERATION_NOT_OPERATOR_SELECTABLE", await ApiTestFixture.ErrorCodeAsync(response));
        // The whole batch is refused, including the otherwise-legitimate sibling.
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Operational_context_never_offers_remove_empty_as_a_selectable_action()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        // OCCUPIED+EMPTY is exactly the state REMOVE_EMPTY applies to, so if the
        // action were still advertised it would appear here as enabled.
        await fixture.SetSlotAsync(slot, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator);
        using var client = await fixture.LoginClientAsync(identity);

        using var response = await client.GetAsync($"/api/v1/machines/{context.MachineId}/operational-context");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        var offered = document.RootElement
            .GetProperty("slots")
            .EnumerateArray()
            .SelectMany(s => s.GetProperty("availableActions").EnumerateArray())
            .Select(a => a.GetProperty("action").GetString())
            .ToArray();

        Assert.NotEmpty(offered);
        Assert.DoesNotContain("REMOVE_EMPTY", offered);
        // The genuine Operator actions are still offered.
        Assert.Contains("REMOVE_FULL", offered);
        Assert.Contains("BRING_EMPTY", offered);
    }
}
