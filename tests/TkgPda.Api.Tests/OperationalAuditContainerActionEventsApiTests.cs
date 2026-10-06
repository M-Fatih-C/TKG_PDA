using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-06: the container-action endpoints (single, batch, remove-all) create their
/// requests through a separate store path. Each created request must carry the
/// authenticated actor, the validated device and the HTTP correlation id on its
/// REQUEST_CREATED event, exactly like the plain create endpoint.
/// </summary>
public sealed class OperationalAuditContainerActionEventsApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private sealed record EventRow(Guid? ActorUserId, Guid? DeviceId, Guid? CorrelationId, string EventType);

    private async Task<List<EventRow>> EventsForKeyAsync(string idempotencyKey, bool viaBatch)
    {
        var filter = viaBatch
            ? "r.batch_id IN (SELECT id FROM ops.transport_batches WHERE idempotency_key = @key)"
            : "r.idempotency_key = @key";
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT e.actor_user_id, e.device_id, e.correlation_id, e.event_type
            FROM ops.transport_request_events e
            JOIN ops.transport_requests r ON r.id = e.transport_request_id
            WHERE {filter}
            ORDER BY e.id
            """,
            connection);
        command.Parameters.AddWithValue("key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<EventRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new EventRow(
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3)));
        }

        return rows;
    }

    [Fact]
    public async Task Single_container_action_persists_actor_device_and_correlation_on_created_event()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "P06-SINGLE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var correlationId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                context.VariantId, context.LayoutId, null, mapped.ProductGroupId, mapped.PartId, 1, "FULL"))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "p06-container-single");
        request.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        var events = await EventsForKeyAsync("p06-container-single", viaBatch: false);
        var created = Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCreated);
        Assert.Equal(identity.UserId, created.ActorUserId);
        Assert.Equal(identity.DeviceId, created.DeviceId);
        Assert.Equal(correlationId, created.CorrelationId);
        var reportedFull = Assert.Single(events, e => e.EventType == "CONTAINER_REPORTED_FULL");
        Assert.Equal(identity.UserId, reportedFull.ActorUserId);
        Assert.Equal(identity.DeviceId, reportedFull.DeviceId);
        Assert.Equal(correlationId, reportedFull.CorrelationId);

        // Idempotent replay adds no second REQUEST_CREATED.
        var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                context.VariantId, context.LayoutId, null, mapped.ProductGroupId, mapped.PartId, 1, "FULL"))
        };
        replay.Headers.Add(ApiHeaders.IdempotencyKey, "p06-container-single");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(replay)).StatusCode);
        Assert.Single(await EventsForKeyAsync("p06-container-single", viaBatch: false), e => e.EventType == TransportEventTypes.RequestCreated);
    }

    [Fact]
    public async Task Container_action_batch_and_remove_all_persist_context_on_every_created_event()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await MakeLayoutVacantAsync(context.LayoutId);
        var full = await SeedMappedPositionAsync(context, "P06-BATCH-FULL", positionOffset: 0);
        var empty = await SeedMappedPositionAsync(context, "P06-BATCH-EMPTY", positionOffset: 1);
        await SetPositionAsync(full.SlotId, full.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(empty.SlotId, empty.PartId, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        // remove-all: one REMOVE_FULL and one REMOVE_EMPTY child.
        var removeAllCorrelation = Guid.NewGuid();
        var removeAll = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/container-actions/remove-all")
        {
            Content = JsonContent.Create(new RemoveAllContainersHttpRequest(context.VariantId, context.LayoutId, null))
        };
        removeAll.Headers.Add(ApiHeaders.IdempotencyKey, "p06-remove-all");
        removeAll.Headers.Add(ApiHeaders.CorrelationId, removeAllCorrelation.ToString());
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(removeAll)).StatusCode);

        var removeAllEvents = (await EventsForKeyAsync("p06-remove-all", viaBatch: true))
            .Where(e => e.EventType == TransportEventTypes.RequestCreated).ToList();
        Assert.Equal(2, removeAllEvents.Count);
        Assert.All(removeAllEvents, e =>
        {
            Assert.Equal(identity.UserId, e.ActorUserId);
            Assert.Equal(identity.DeviceId, e.DeviceId);
            Assert.Equal(removeAllCorrelation, e.CorrelationId);
        });

        // Replay creates nothing new.
        var removeAllReplay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/container-actions/remove-all")
        {
            Content = JsonContent.Create(new RemoveAllContainersHttpRequest(context.VariantId, context.LayoutId, null))
        };
        removeAllReplay.Headers.Add(ApiHeaders.IdempotencyKey, "p06-remove-all");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(removeAllReplay)).StatusCode);
        Assert.Equal(2, (await EventsForKeyAsync("p06-remove-all", viaBatch: true))
            .Count(e => e.EventType == TransportEventTypes.RequestCreated));
    }

    [Fact]
    public async Task Container_action_batch_endpoint_persists_context_on_created_event()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "P06-BATCH-ENDPOINT");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var correlationId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/container-actions/batch")
        {
            Content = JsonContent.Create(new CreateContainerActionBatchHttpRequest(
                context.VariantId, context.LayoutId, null,
                [new(mapped.ProductGroupId, mapped.PartId, 1, "FULL")], 100))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "p06-container-batch");
        request.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        var created = Assert.Single(
            await EventsForKeyAsync("p06-container-batch", viaBatch: true),
            e => e.EventType == TransportEventTypes.RequestCreated);
        Assert.Equal(identity.UserId, created.ActorUserId);
        Assert.Equal(identity.DeviceId, created.DeviceId);
        Assert.Equal(correlationId, created.CorrelationId);
    }

    private sealed record MappedPosition(Guid SlotId, Guid PartId, Guid ProductGroupId);

    private async Task<MappedPosition> SeedMappedPositionAsync(LayoutContext context, string suffix, int positionOffset = 0)
    {
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, positionOffset + 1))[positionOffset];
        var code = $"WP06-{suffix}-{Guid.NewGuid():N}";
        var part = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, is_active) VALUES (@code, true) RETURNING id",
            ("code", code));
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id) VALUES (@variant_id, @part_id) RETURNING id",
            ("variant_id", context.VariantId),
            ("part_id", part));
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot_id, @part_id) ON CONFLICT DO NOTHING",
            ("slot_id", slot),
            ("part_id", part));
        return new(slot, part, group);
    }

    private Task SetPositionAsync(Guid slotId, Guid partId, string occupancy, string load) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state, current_part_id)
            VALUES (@slot_id, @occupancy, @load, @part_id)
            ON CONFLICT (layout_slot_id) DO UPDATE
            SET occupancy_state = EXCLUDED.occupancy_state,
                load_state = EXCLUDED.load_state,
                current_part_id = EXCLUDED.current_part_id,
                current_container_type_id = NULL,
                row_version = ops.slot_runtime_state.row_version + 1,
                updated_at = now()
            """,
            ("slot_id", slotId),
            ("occupancy", occupancy),
            ("load", load),
            ("part_id", partId));

    private Task MakeLayoutVacantAsync(Guid layoutId) => fixture.ExecuteAsync(
        """
        INSERT INTO ops.slot_runtime_state (
            layout_slot_id, occupancy_state, load_state, current_part_id, current_container_type_id)
        SELECT ls.id, 'VACANT', 'UNKNOWN', NULL, NULL
        FROM master.layout_slots ls
        WHERE ls.layout_id = @layout_id
          AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = 'VACANT',
            load_state = 'UNKNOWN',
            current_part_id = NULL,
            current_container_type_id = NULL,
            row_version = ops.slot_runtime_state.row_version + 1,
            updated_at = now()
        """,
        ("layout_id", layoutId));
}
