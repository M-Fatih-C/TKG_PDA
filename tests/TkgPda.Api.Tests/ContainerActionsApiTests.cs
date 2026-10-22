using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class ContainerActionsApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Container_positions_reject_another_machines_variant_and_layout_context()
    {
        await fixture.ResetAsync();
        var allowed = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var other = await fixture.ConfigureMachineAsync("SH-01-P1--SH-01-P2", "1");
        var otherMapped = await SeedMappedPositionAsync(other, "CROSS-MACHINE");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync(
            $"/api/v1/machines/{allowed.MachineId}/container-positions" +
            $"?variantId={other.VariantId}&layoutId={other.LayoutId}" +
            $"&productGroupId={otherMapped.ProductGroupId}&partId={otherMapped.PartId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("CROSS-MACHINE", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kasa_doldu_atomically_marks_full_and_creates_remove_full()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "FULL-EVENT");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-event");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("FULL", await fixture.ScalarAsync<string>(
            "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot_id",
            ("slot_id", mapped.SlotId)));
        Assert.Equal("REMOVE_FULL", await fixture.ScalarAsync<string>(
            """
            SELECT tt.code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.source_slot_id = @slot_id
            """,
            ("slot_id", mapped.SlotId)));
        Assert.Equal("NOT_REQUIRED", await fixture.ScalarAsync<string>(
            """
            SELECT readiness_state FROM ops.transport_requests
            WHERE source_slot_id = @slot_id
            """,
            ("slot_id", mapped.SlotId)));
    }

    [Fact]
    public async Task Kasa_doldu_on_full_is_idempotent()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "FULL-REPLAY");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var first = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-replay");
        var replay = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-replay");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key = 'wp33-full-replay'"));
    }

    [Theory]
    [InlineData("VACANT", "UNKNOWN")]
    [InlineData("UNKNOWN", "UNKNOWN")]
    public async Task Kasa_doldu_rejects_unproven_container_state(string occupancy, string load)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, $"FULL-{occupancy}");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, occupancy, load);
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await SendActionAsync(client, context, mapped, "FULL", $"wp33-full-{occupancy}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key = @key",
            ("key", $"wp33-full-{occupancy}")));
    }

    [Fact]
    public async Task Kasa_doldu_rolls_back_load_when_request_creation_fails()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "FULL-ROLLBACK");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        await fixture.ExecuteAsync(
            """
            CREATE OR REPLACE FUNCTION ops.wp33_reject_test_request()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW.idempotency_key = 'wp33-full-rollback' THEN
                    RAISE EXCEPTION 'WP-33 rollback test';
                END IF;
                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER wp33_reject_test_request
            BEFORE INSERT ON ops.transport_requests
            FOR EACH ROW EXECUTE FUNCTION ops.wp33_reject_test_request();
            """);
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        try
        {
            var response = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-rollback");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("EMPTY", await fixture.ScalarAsync<string>(
                "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot_id",
                ("slot_id", mapped.SlotId)));
            Assert.Equal(0L, await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key = 'wp33-full-rollback'"));
        }
        finally
        {
            await fixture.ExecuteAsync(
                """
                DROP TRIGGER IF EXISTS wp33_reject_test_request ON ops.transport_requests;
                DROP FUNCTION IF EXISTS ops.wp33_reject_test_request();
                """);
        }
    }

    [Fact]
    public async Task Kasa_bos_from_vacant_creates_bring_empty_but_occupied_is_rejected()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var vacant = await SeedMappedPositionAsync(context, "EMPTY-VACANT", positionOffset: 0);
        var occupiedEmpty = await SeedMappedPositionAsync(context, "EMPTY-OCCUPIED-EMPTY", positionOffset: 1);
        var occupiedFull = await SeedMappedPositionAsync(context, "EMPTY-OCCUPIED-FULL", positionOffset: 2);
        await SetPositionAsync(vacant.SlotId, vacant.PartId, "VACANT", "UNKNOWN");
        await SetPositionAsync(occupiedEmpty.SlotId, occupiedEmpty.PartId, "OCCUPIED", "EMPTY");
        await SetPositionAsync(occupiedFull.SlotId, occupiedFull.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var created = await SendActionAsync(client, context, vacant, "EMPTY", "wp33-empty-vacant");
        var rejectedEmpty = await SendActionAsync(client, context, occupiedEmpty, "EMPTY", "wp33-empty-occupied-empty");
        var rejectedFull = await SendActionAsync(client, context, occupiedFull, "EMPTY", "wp33-empty-occupied-full");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedEmpty.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedFull.StatusCode);
        Assert.Equal("BRING_EMPTY", await fixture.ScalarAsync<string>(
            """
            SELECT tt.code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.idempotency_key = 'wp33-empty-vacant'
            """));
        Assert.Equal("NOT_REQUIRED", await fixture.ScalarAsync<string>(
            """
            SELECT readiness_state FROM ops.transport_requests
            WHERE idempotency_key = 'wp33-empty-vacant'
            """));
    }

    [Fact]
    public async Task Remove_all_uses_entire_layout_and_is_atomic_on_unknown()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await MakeLayoutVacantAsync(context.LayoutId);
        var full = await SeedMappedPositionAsync(context, "ALL-FULL", positionOffset: 0);
        var empty = await SeedMappedPositionAsync(context, "ALL-EMPTY", positionOffset: 1);
        var unknown = await SeedMappedPositionAsync(context, "ALL-UNKNOWN", positionOffset: 2);
        await SetPositionAsync(full.SlotId, full.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(empty.SlotId, empty.PartId, "OCCUPIED", "EMPTY");
        await SetPositionAsync(unknown.SlotId, unknown.PartId, "UNKNOWN", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var blocked = await SendRemoveAllAsync(client, context, "wp33-all-blocked");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE idempotency_key = 'wp33-all-blocked'"));

        await SetPositionAsync(unknown.SlotId, unknown.PartId, "VACANT", "UNKNOWN");
        var created = await SendRemoveAllAsync(client, context, "wp33-all-created");
        var replay = await SendRemoveAllAsync(client, context, "wp33-all-created");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var operations = await fixture.ScalarAsync<string[]>(
            """
            SELECT array_agg(tt.code ORDER BY tt.code)
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            JOIN ops.transport_batches b ON b.id = r.batch_id
            WHERE b.idempotency_key = 'wp33-all-created'
            """);
        Assert.Equal(["REMOVE_EMPTY", "REMOVE_FULL"], operations);
    }

    [Fact]
    public async Task Remove_all_rejects_the_whole_batch_when_any_position_is_reserved()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await MakeLayoutVacantAsync(context.LayoutId);
        var reserved = await SeedMappedPositionAsync(context, "ALL-RESERVED", positionOffset: 0);
        var otherwiseEligible = await SeedMappedPositionAsync(context, "ALL-OTHER", positionOffset: 1);
        await SetPositionAsync(reserved.SlotId, reserved.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(otherwiseEligible.SlotId, otherwiseEligible.PartId, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendActionAsync(client, context, reserved, "FULL", "wp33-reserve-position")).StatusCode);

        var response = await SendRemoveAllAsync(client, context, "wp33-all-reserved");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE idempotency_key = 'wp33-all-reserved'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Concurrent_container_actions_create_only_one_reservation()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "CONCURRENT");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var responses = await Task.WhenAll(
            SendActionAsync(client, context, mapped, "FULL", "wp33-concurrent-a"),
            SendActionAsync(client, context, mapped, "FULL", "wp33-concurrent-b"));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key LIKE 'wp33-concurrent-%'"));
    }

    [Fact]
    public async Task Container_action_batch_rejects_same_key_with_a_different_payload_or_endpoint_kind()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "BATCH-REUSE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var created = await SendActionBatchAsync(client, context, mapped, "FULL", "wp33-batch-reuse");
        var replay = await SendActionBatchAsync(client, context, mapped, "FULL", "wp33-batch-reuse");
        var changedPayload = await SendActionBatchAsync(client, context, mapped, "EMPTY", "wp33-batch-reuse");
        var changedEndpointKind = await SendRemoveAllAsync(client, context, "wp33-batch-reuse");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedEndpointKind.StatusCode);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE idempotency_key = 'wp33-batch-reuse'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE batch_id IS NOT NULL"));
    }

    [Fact]
    public async Task Global_queue_is_cross_user_cross_machine_and_hides_technical_identifiers()
    {
        await fixture.ResetAsync();
        var first = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var second = await fixture.ConfigureMachineAsync("SH-01-P1--SH-01-P2", "1");
        var firstMapped = await SeedMappedPositionAsync(first, "QUEUE-A");
        var secondMapped = await SeedMappedPositionAsync(second, "QUEUE-B");
        await SetPositionAsync(firstMapped.SlotId, firstMapped.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(secondMapped.SlotId, secondMapped.PartId, "OCCUPIED", "FULL");
        var firstIdentity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        var secondIdentity = await fixture.SeedIdentityAsync(machineCode: "SH-01-P1--SH-01-P2");
        using var firstClient = await fixture.LoginClientAsync(firstIdentity);
        using var secondClient = await fixture.LoginClientAsync(secondIdentity);
        await SendActionAsync(firstClient, first, firstMapped, "FULL", "wp33-queue-a");
        await SendActionAsync(secondClient, second, secondMapped, "FULL", "wp33-queue-b");
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'RUNNING' WHERE idempotency_key = 'wp33-queue-b'");

        var response = await secondClient.GetAsync("/api/v1/transport-requests/queue");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Contains(
            "SH-01-P1--SH-01-P2",
            document.RootElement.GetProperty("items")[0].GetProperty("machine").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("rdsStatus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceSlot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("destinationSlot", json, StringComparison.OrdinalIgnoreCase);

        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'COMPLETED' WHERE idempotency_key = 'wp33-queue-a'");
        var active = await secondClient.GetFromJsonAsync<JsonElement>("/api/v1/transport-requests/queue");
        var terminal = await secondClient.GetFromJsonAsync<JsonElement>("/api/v1/transport-requests/queue?terminal=true");
        Assert.Single(active.GetProperty("items").EnumerateArray());
        Assert.Single(terminal.GetProperty("items").EnumerateArray());
    }

    private async Task<MappedPosition> SeedMappedPositionAsync(
        LayoutContext context,
        string suffix,
        int positionOffset = 0)
    {
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, positionOffset + 1))[positionOffset];
        var code = $"WP33-{suffix}-{Guid.NewGuid():N}";
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
            layout_slot_id,
            occupancy_state,
            load_state,
            current_part_id,
            current_container_type_id)
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

    private static Task<HttpResponseMessage> SendActionAsync(
        HttpClient client,
        LayoutContext context,
        MappedPosition mapped,
        string action,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                context.VariantId,
                context.LayoutId,
                null,
                mapped.ProductGroupId,
                mapped.PartId,
                1,
                action))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendRemoveAllAsync(
        HttpClient client,
        LayoutContext context,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions/remove-all")
        {
            Content = JsonContent.Create(new RemoveAllContainersHttpRequest(
                context.VariantId,
                context.LayoutId,
                null))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendActionBatchAsync(
        HttpClient client,
        LayoutContext context,
        MappedPosition mapped,
        string action,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions/batch")
        {
            Content = JsonContent.Create(new CreateContainerActionBatchHttpRequest(
                context.VariantId,
                context.LayoutId,
                null,
                [new(mapped.ProductGroupId, mapped.PartId, 1, action)],
                100))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private sealed record MappedPosition(Guid SlotId, Guid PartId, Guid ProductGroupId);
}
