using System.Net;
using System.Net.Http.Json;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

// WP-04 — TransportBatch: a business grouping boundary only. Each child
// TransportRequest keeps its own UUID/idempotency key/operation/part/
// source/destination/priority/status; the batch itself never gets an RDS
// task and has no execution-status state machine of its own.
public sealed class TransportBatchApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Batch_with_two_items_creates_one_batch_with_two_child_requests()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await PostBatchAsync(
            client,
            context.MachineId,
            "batch-two-items",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP1"), "item-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP2"), "item-2")]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<TransportBatchHttpResponse>();
        Assert.Equal(2, batch!.Items.Count);
        Assert.False(batch.IdempotentReplay);
    }

    [Fact]
    public async Task Child_request_uuids_are_distinct()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var batch = await CreateBatchAsync(
            client,
            context.MachineId,
            "batch-distinct-uuids",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP3"), "distinct-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP4"), "distinct-2")]);

        Assert.NotEqual(batch.Items[0].Id, batch.Items[1].Id);
    }

    [Fact]
    public async Task Every_child_retains_its_own_idempotency_key()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var batch = await CreateBatchAsync(
            client,
            context.MachineId,
            "batch-own-keys",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP5"), "own-key-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP6"), "own-key-2")]);

        var keys = await fixture.ScalarAsync<string[]>(
            "SELECT array_agg(idempotency_key ORDER BY request_no) FROM ops.transport_requests WHERE batch_id = @batch_id",
            ("batch_id", batch.BatchId));
        Assert.Equal(["own-key-1", "own-key-2"], keys);
    }

    [Fact]
    public async Task Every_child_keeps_its_own_operation_part_source_destination()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var batch = await CreateBatchAsync(
            client,
            context.MachineId,
            "batch-own-fields",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP7"), "fields-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP8"), "fields-2")]);

        Assert.Equal("BRING_EMPTY", batch.Items[0].Operation);
        Assert.Equal(slots[0], batch.Items[0].DestinationSlotId);
        // Customer Workflow — Empty Container Buffer: BRING_EMPTY's omitted
        // source is now auto-allocated from a configured EMPTY_STORAGE slot —
        // independently per child, so each gets its own buffer slot.
        Assert.NotNull(batch.Items[0].SourceSlotId);
        Assert.Equal("BRING_EMPTY", batch.Items[1].Operation);
        Assert.Equal(slots[1], batch.Items[1].DestinationSlotId);
        Assert.NotNull(batch.Items[1].SourceSlotId);
        Assert.NotEqual(batch.Items[0].SourceSlotId, batch.Items[1].SourceSlotId);
    }

    [Fact]
    public async Task Batch_id_links_children()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var batch = await CreateBatchAsync(
            client,
            context.MachineId,
            "batch-links",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP9"), "links-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP10"), "links-2")]);

        foreach (var item in batch.Items)
        {
            Assert.Equal(
                batch.BatchId,
                await fixture.ScalarAsync<Guid>("SELECT batch_id FROM ops.transport_requests WHERE id = @id", ("id", item.Id)));
        }
    }

    [Fact]
    public async Task Standalone_existing_request_still_has_null_batch_id()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-STANDALONE"), null, "BRING_EMPTY"),
            "standalone-no-batch");
        var created = await response.Content.ReadFromJsonAsync<TransportHttpResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT batch_id IS NULL FROM ops.transport_requests WHERE id = @id", ("id", created!.Id)));
    }

    [Fact]
    public async Task Same_batch_idempotency_key_replay_creates_no_duplicates()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var items = new[] { Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP11"), "replay-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP12"), "replay-2") };

        var first = await PostBatchAsync(client, context.MachineId, "batch-replay", items);
        var second = await PostBatchAsync(client, context.MachineId, "batch-replay", items);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBatch = await first.Content.ReadFromJsonAsync<TransportBatchHttpResponse>();
        var secondBatch = await second.Content.ReadFromJsonAsync<TransportBatchHttpResponse>();
        Assert.False(firstBatch!.IdempotentReplay);
        Assert.True(secondBatch!.IdempotentReplay);
        Assert.Equal(firstBatch.BatchId, secondBatch.BatchId);
        Assert.Equal(
            firstBatch.Items.Select(i => i.Id).Order(),
            secondBatch.Items.Select(i => i.Id).Order());
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE id = @id", ("id", firstBatch.BatchId)));
        Assert.Equal(2L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE batch_id = @id", ("id", firstBatch.BatchId)));
    }

    [Fact]
    public async Task One_invalid_child_creates_zero_batch_and_zero_child_requests()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        // slots[1] stays OCCUPIED/FULL-incompatible with BRING_EMPTY -> invalid child.
        await fixture.SetSlotAsync(slots[1], "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await PostBatchAsync(
            client,
            context.MachineId,
            "batch-one-invalid",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP13"), "valid-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP14"), "invalid-2")]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // With backend slot resolution the invalid child is rejected because no slot in
        // the required state is eligible for its part, rather than by a supplied-slot
        // state mismatch. The batch is still refused atomically.
        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_batches"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Unauthorized_machine_is_rejected()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: null);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await PostBatchAsync(
            client,
            context.MachineId,
            "batch-unauthorized",
            [Bring(context, await fixture.PartForSlotAsync(slot, "P05-TRANSP15"), "unauthorized-1")]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorCode = await ApiTestFixture.ErrorCodeAsync(response);
        Assert.True(errorCode is "MACHINE_ACCESS_DENIED" or "DEVICE_MACHINE_BINDING_REQUIRED");
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_batches"));
    }

    [Fact]
    public async Task Mixed_machine_context_across_items_is_rejected()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var otherContext = await fixture.ConfigureMachineAsync(machineCode: "SH-01-B");
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var otherSlot = (await fixture.LogisticsSlotsAsync(otherContext.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(otherSlot, "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await PostBatchAsync(
            client,
            context.MachineId,
            "batch-mixed-machine",
            [Bring(context, await fixture.PartForSlotAsync(slot, "P05-TRANSP16"), "mixed-1"), Bring(otherContext, await fixture.PartForSlotAsync(otherSlot, "P05-TRANSP17"), "mixed-2")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BATCH_MACHINE_MISMATCH", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_batches"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task No_rds_task_is_created_directly_by_the_batch_api()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var batch = await CreateBatchAsync(
            client,
            context.MachineId,
            "batch-no-rds",
            [Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-TRANSP18"), "no-rds-1"), Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-TRANSP19"), "no-rds-2")]);

        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.rds_tasks WHERE transport_request_id = ANY(@ids)",
            ("ids", batch.Items.Select(i => i.Id).ToArray())));
    }

    private static CreateTransportBatchItemHttpRequest Bring(LayoutContext context, Guid partId, string idempotencyKey) => new(
        idempotencyKey,
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        partId,
        null,
        "BRING_EMPTY");

    private static Task<HttpResponseMessage> PostBatchAsync(
        HttpClient client,
        Guid machineId,
        string batchIdempotencyKey,
        IReadOnlyList<CreateTransportBatchItemHttpRequest> items) =>
        client.PostAsJsonAsync(
            "/api/v1/transport-batches",
            new CreateTransportBatchHttpRequest(batchIdempotencyKey, machineId, items));

    private static async Task<TransportBatchHttpResponse> CreateBatchAsync(
        HttpClient client,
        Guid machineId,
        string batchIdempotencyKey,
        IReadOnlyList<CreateTransportBatchItemHttpRequest> items)
    {
        var response = await PostBatchAsync(client, machineId, batchIdempotencyKey, items);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransportBatchHttpResponse>()
            ?? throw new InvalidOperationException("Batch response was empty.");
    }
}
