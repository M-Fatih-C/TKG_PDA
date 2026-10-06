using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class ProductionEndCleanupApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task All_vacant_positions_end_immediately_with_no_cleanup_requests()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var ended = await ReadAsync(await EndAsync(client, setup, session.Id, "end-vacant"));

        Assert.Equal("ENDED", ended.Session.Status);
        Assert.NotNull(ended.Session.EndedAt);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs WHERE status = 'COMPLETED'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_ENDED'"));
    }

    [Fact]
    public async Task One_full_main_pallet_waits_for_authoritative_completion_and_delivers_finished_goods()
    {
        var setup = await SetupAsync();
        await fixture.SetSlotAsync(setup.Slots[0], "OCCUPIED", "FULL", setup.MainPart);
        await SeedPriorityAsync("REMOVE_FULL", 40);
        await SeedTemplateAsync(setup, "REMOVE_FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var ending = await ReadAsync(await EndAsync(client, setup, session.Id, "end-full"));

        Assert.Equal("ENDING", ending.Session.Status);
        Assert.Null(ending.Session.EndedAt);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("REMOVE_FULL", await fixture.ScalarAsync<string>("SELECT tt.code FROM ops.transport_requests r JOIN master.task_types tt ON tt.id = r.task_type_id"));
        Assert.Equal(40, await fixture.ScalarAsync<int>("SELECT priority FROM ops.transport_requests"));
        Assert.Equal(await fixture.FinishedGoodsDropSlotAsync(setup.Context.LayoutId), await fixture.ScalarAsync<Guid>("SELECT destination_slot_id FROM ops.transport_requests"));
        var requestId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);

        Assert.Equal("ENDED", await StatusAsync(session.Id));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_events WHERE event_type = 'UNIT_DELIVERED'"));
        Assert.Equal("COMPLETED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_ENDED'"));
    }

    [Fact]
    public async Task One_empty_pallet_creates_remove_empty()
    {
        var setup = await SetupAsync();
        await fixture.SetSlotAsync(setup.Slots[0], "OCCUPIED", "EMPTY", setup.MainPart);
        await SeedPriorityAsync("REMOVE_EMPTY", 70);
        await SeedTemplateAsync(setup, "REMOVE_EMPTY");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var ending = await ReadAsync(await EndAsync(client, setup, session.Id, "end-empty"));

        Assert.Equal("ENDING", ending.Session.Status);
        Assert.Equal("REMOVE_EMPTY", await fixture.ScalarAsync<string>("SELECT tt.code FROM ops.transport_requests r JOIN master.task_types tt ON tt.id = r.task_type_id"));
        Assert.Equal(70, await fixture.ScalarAsync<int>("SELECT priority FROM ops.transport_requests"));
        Assert.Equal("EMPTY_STORAGE", await fixture.ScalarAsync<string>("""
            SELECT ls.slot_role FROM ops.transport_requests r
            JOIN master.layout_slots ls ON ls.id = r.destination_slot_id
            """));
    }

    [Fact]
    public async Task Three_occupied_pallets_are_sequenced_by_layout_order_and_dependency()
    {
        var setup = await SetupAsync(slotCount: 3);
        var originalOrder = new List<(Guid Slot, int Order)>();
        foreach (var slot in setup.Slots)
        {
            originalOrder.Add((slot, await fixture.ScalarAsync<int>("SELECT display_order FROM master.layout_slots WHERE id = @id", ("id", slot))));
        }

        try
        {
        await fixture.ExecuteAsync("UPDATE master.layout_slots SET display_order = 30 WHERE id = @id", ("id", setup.Slots[0]));
        await fixture.ExecuteAsync("UPDATE master.layout_slots SET display_order = 10 WHERE id = @id", ("id", setup.Slots[1]));
        await fixture.ExecuteAsync("UPDATE master.layout_slots SET display_order = 20 WHERE id = @id", ("id", setup.Slots[2]));
        foreach (var slot in setup.Slots)
        {
            await fixture.SetSlotAsync(slot, "OCCUPIED", "FULL", setup.MainPart);
        }

        await SeedPriorityAsync("REMOVE_FULL", 40);
        await SeedTemplateAsync(setup, "REMOVE_FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-three")).EnsureSuccessStatusCode();

        var orderedSlots = await IdsAsync("""
            SELECT layout_slot_id FROM ops.production_end_run_items ORDER BY sequence_no
            """);
        Assert.Equal([setup.Slots[1], setup.Slots[2], setup.Slots[0]], orderedSlots);
        var requests = await IdsAsync("""
            SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no
            """);
        Assert.Equal(3, requests.Length);
        Assert.Equal(requests[0], await fixture.ScalarAsync<Guid>("SELECT predecessor_request_id FROM ops.transport_request_dependencies WHERE successor_request_id = @id", ("id", requests[1])));
        Assert.Equal(requests[1], await fixture.ScalarAsync<Guid>("SELECT predecessor_request_id FROM ops.transport_request_dependencies WHERE successor_request_id = @id", ("id", requests[2])));
        }
        finally
        {
            foreach (var (slot, order) in originalOrder)
            {
                await fixture.ExecuteAsync("UPDATE master.layout_slots SET display_order = @order WHERE id = @id", ("order", order), ("id", slot));
            }
        }
    }

    [Fact]
    public async Task Later_cleanup_request_cannot_dispatch_before_its_predecessor_completes()
    {
        var setup = await OccupyAsync(3, "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-gate")).EnsureSuccessStatusCode();
        var requests = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");

        var queued = await QueuedAsync();
        Assert.Equal(requests[0], Assert.Single(queued));

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.RecordExecutionTransitionAsync(requests[0], TransportRequestStatus.SentToRds);
        await store.RecordExecutionTransitionAsync(requests[0], TransportRequestStatus.Running);
        await store.RecordExecutionTransitionAsync(requests[0], TransportRequestStatus.Completed);
        queued = await QueuedAsync();
        Assert.Equal(requests[1], Assert.Single(queued));
        Assert.DoesNotContain(requests[2], queued);
        Assert.Equal("ENDING", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Queued_sent_to_rds_and_running_do_not_end_production()
    {
        var setup = await OccupyAsync(1, "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-inflight")).EnsureSuccessStatusCode();
        var requestId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        Assert.Equal("QUEUED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT ended_at IS NULL FROM ops.production_sessions WHERE id = @id", ("id", session.Id)));
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);

        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        Assert.True(await fixture.ScalarAsync<bool>("SELECT ended_at IS NULL FROM ops.production_sessions WHERE id = @id", ("id", session.Id)));
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT ended_at IS NULL FROM ops.production_sessions WHERE id = @id", ("id", session.Id)));
    }

    [Fact]
    public async Task Only_the_final_authoritative_completion_ends_production()
    {
        var setup = await OccupyAsync(2, "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-final")).EnsureSuccessStatusCode();
        var requests = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await CompleteAsync(store, requests[0]);
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        await CompleteAsync(store, requests[1]);
        Assert.Equal("ENDED", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Unknown_occupancy_fails_closed_with_zero_requests()
    {
        var setup = await SetupAsync();
        await fixture.SetSlotAsync(setup.Slots[0], "UNKNOWN", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-unknown-occupancy");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_POSITION_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Unknown_load_fails_closed_with_zero_requests()
    {
        var setup = await SetupAsync();
        await fixture.ExecuteAsync("""
            UPDATE ops.slot_runtime_state
            SET occupancy_state = 'OCCUPIED', load_state = 'UNKNOWN', current_part_id = @part
            WHERE layout_slot_id = @slot
            """, ("slot", setup.Slots[0]), ("part", setup.MainPart));
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-unknown-load");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_LOAD_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Full_pallet_without_a_part_fails_closed_with_zero_requests()
    {
        var setup = await SetupAsync();
        await fixture.SetSlotAsync(setup.Slots[0], "OCCUPIED", "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-null-part");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_PART_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Missing_production_end_priority_creates_zero_requests()
    {
        var setup = await SetupAsync();
        await fixture.SetSlotAsync(setup.Slots[0], "OCCUPIED", "FULL", setup.MainPart);
        await SeedTemplateAsync(setup, "REMOVE_FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-no-priority");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_PRIORITY_NOT_CONFIGURED", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Missing_finished_goods_destination_creates_zero_requests()
    {
        var setup = await SetupAsync();
        await fixture.SetSlotAsync(setup.Slots[0], "OCCUPIED", "FULL", setup.MainPart);
        await SeedPriorityAsync("REMOVE_FULL", 40);
        await SeedTemplateAsync(setup, "REMOVE_FULL");
        await fixture.ExecuteAsync("""
            DELETE FROM ops.store_location_delivery_slots
            WHERE layout_slot_id IN (
                SELECT id FROM master.layout_slots WHERE layout_id = @layout AND slot_role = 'DROP')
            """, ("layout", setup.Context.LayoutId));
        try
        {
            using var client = await fixture.LoginClientAsync(setup.User);
            var session = await StartAsync(client, setup);
            var response = await EndAsync(client, setup, session.Id, "end-no-destination");

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", await ApiTestFixture.ErrorCodeAsync(response));
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
            Assert.Equal("ACTIVE", await StatusAsync(session.Id));
        }
        finally
        {
            await fixture.ConfigureMachineAsync();
        }
    }

    [Fact]
    public async Task One_invalid_position_among_five_creates_zero_requests()
    {
        var setup = await SetupAsync(slotCount: 5);
        for (var i = 0; i < 4; i++)
        {
            await fixture.SetSlotAsync(setup.Slots[i], "OCCUPIED", "FULL", setup.MainPart);
        }

        await fixture.SetSlotAsync(setup.Slots[4], "UNKNOWN", "UNKNOWN");
        await SeedPriorityAsync("REMOVE_FULL", 40);
        await SeedTemplateAsync(setup, "REMOVE_FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-partial");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_POSITION_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Repeating_the_end_key_returns_the_same_run_and_request_ids()
    {
        var setup = await OccupyAsync(1, "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        var first = await EndAsync(client, setup, session.Id, "same-end-key");
        first.EnsureSuccessStatusCode();
        var firstIds = await IdsAsync("SELECT id FROM ops.transport_requests ORDER BY request_no");

        var second = await EndAsync(client, setup, session.Id, "same-end-key");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await ReadAsync(second)).WasIdempotentReplay);
        Assert.Equal(firstIds, await IdsAsync("SELECT id FROM ops.transport_requests ORDER BY request_no"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
    }

    [Fact]
    public async Task Same_key_with_a_different_session_is_rejected()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "bound-key")).EnsureSuccessStatusCode();

        var response = await EndAsync(client, setup, Guid.NewGuid(), "bound-key");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Predecessor_failure_blocks_later_cleanup_and_a_new_attempt_skips_completed_slots()
    {
        var setup = await OccupyAsync(3, "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-fail")).EnsureSuccessStatusCode();
        var requests = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");
        var slots = await IdsAsync("SELECT layout_slot_id FROM ops.production_end_run_items ORDER BY sequence_no");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await CompleteAsync(store, requests[0]);
        await store.RecordExecutionTransitionAsync(requests[1], TransportRequestStatus.SentToRds);
        await store.RecordExecutionOutcomeAsync(requests[1], new ExecutionOutcome(TransportRequestStatus.Failed, "TEST", "cleanup failed"));

        Assert.Equal("ENDING", await StatusAsync(session.Id));
        Assert.Equal("FAILED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id = @id", ("id", requests[2])));
        Assert.DoesNotContain(requests[1], await QueuedAsync());
        Assert.DoesNotContain(requests[2], await QueuedAsync());

        (await EndAsync(client, setup, session.Id, "end-fail")).EnsureSuccessStatusCode();
        Assert.Equal(3L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        var retry = await EndAsync(client, setup, session.Id, "end-retry");
        retry.EnsureSuccessStatusCode();
        var retrySlots = await IdsAsync("""
            SELECT i.layout_slot_id
            FROM ops.production_end_run_items i
            JOIN ops.production_end_runs r ON r.id = i.run_id
            WHERE r.idempotency_key = 'end-retry'
            ORDER BY i.sequence_no
            """);
        Assert.DoesNotContain(slots[0], retrySlots);
        Assert.Equal(2, retrySlots.Length);
    }

    [Fact]
    public async Task Alt_part_remove_full_does_not_create_a_finished_goods_delivery()
    {
        var setup = await SetupAsync();
        var alt = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES (@code) RETURNING id", ("code", "PE-ALT-" + Guid.NewGuid().ToString("N")[..8]));
        await fixture.ExecuteAsync("""
            INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type)
            VALUES (@group, @part, 'ALT_PART')
            """, ("group", setup.Group), ("part", alt));
        await fixture.ExecuteAsync("INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)", ("slot", setup.Slots[0]), ("part", alt));
        await fixture.SetSlotAsync(setup.Slots[0], "OCCUPIED", "FULL", alt);
        var destination = await fixture.ScalarAsync<Guid>("""
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            VALUES (@layout, @code, 'PE return', 'OTHER', 9900)
            RETURNING id
            """, ("layout", setup.Context.LayoutId), ("code", "PE-RETURN-" + Guid.NewGuid().ToString("N")[..8]));
        await fixture.SetSlotAsync(destination, "VACANT", "UNKNOWN");
        var groupId = await fixture.ScalarAsync<Guid>("""
            INSERT INTO master.slot_groups (layout_id, code, name, purpose)
            VALUES (@layout, @code, 'PE return', 'DESTINATION')
            RETURNING id
            """, ("layout", setup.Context.LayoutId), ("code", "PE-GROUP-" + Guid.NewGuid().ToString("N")[..8]));
        await fixture.ExecuteAsync("""
            INSERT INTO master.slot_group_members (slot_group_id, layout_slot_id, priority)
            VALUES (@group, @slot, 1)
            """, ("group", groupId), ("slot", destination));
        await fixture.ExecuteAsync("""
            INSERT INTO master.task_templates (variant_id, product_group_id, task_type_id, destination_slot_group_id, rds_task_label, priority, is_active, config_json)
            SELECT @variant, @product, tt.id, @destination_group, 'PE-ALT', 10, true, '{"testScope":"PRODUCTION_END"}'::jsonb
            FROM master.task_types tt WHERE tt.code = 'REMOVE_FULL'
            """, ("variant", setup.Context.VariantId), ("product", setup.Group), ("destination_group", groupId));
        await SeedPriorityAsync("REMOVE_FULL", 40);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-alt")).EnsureSuccessStatusCode();

        Assert.Equal(destination, await fixture.ScalarAsync<Guid>("SELECT destination_slot_id FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_location_delivery_slots WHERE layout_slot_id = @id", ("id", destination)));
        var requestId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await CompleteAsync(store, requestId);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
        Assert.Equal("ENDED", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Ending_rejects_a_new_alt_part_demand()
    {
        var setup = await OccupyAsync(1, "FULL");
        var alt = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES (@code) RETURNING id", ("code", "PE-ALT-" + Guid.NewGuid().ToString("N")[..8]));
        await fixture.ExecuteAsync("""
            INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type)
            VALUES (@group, @part, 'ALT_PART')
            """, ("group", setup.Group), ("part", alt));
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-block-alt")).EnsureSuccessStatusCode();

        var demand = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/part-demands")
        {
            Content = JsonContent.Create(new PartDemandHttpRequest(alt))
        };
        demand.Headers.Add(ApiHeaders.IdempotencyKey, "alt-while-ending");
        var response = await client.SendAsync(demand);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_ENDING", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Ending_rejects_a_new_mamul_action()
    {
        var setup = await OccupyAsync(1, "FULL");
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-block-mamul")).EnsureSuccessStatusCode();
        var before = await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests");

        var response = await BringAsync(client, setup, "mamul-while-ending", session.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_ENDING", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(before, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Ending_rejects_a_variant_or_layout_change()
    {
        var setup = await OccupyAsync(1, "FULL", canChangeVariant: true, role: RoleCodes.Supervisor);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-block-layout")).EnsureSuccessStatusCode();
        var other = await fixture.ContextAsync("SH-01-A", "2");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{setup.Context.MachineId}/variant-selection",
            new VariantSwitchHttpRequest(other.VariantId, other.LayoutId, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_SESSION_ACTIVE", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Repeating_an_all_vacant_end_stays_idempotent()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        var first = await ReadAsync(await EndAsync(client, setup, session.Id, "vacant-twice"));
        var second = await ReadAsync(await EndAsync(client, setup, session.Id, "vacant-twice"));

        Assert.Equal(first.Session, second.Session);
        Assert.True(second.WasIdempotentReplay);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_ENDED'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    private async Task<Setup> OccupyAsync(int count, string load, bool canChangeVariant = false, string role = RoleCodes.LineOperator)
    {
        var setup = await SetupAsync(slotCount: count, canChangeVariant: canChangeVariant, role: role);
        var operation = load == "FULL" ? "REMOVE_FULL" : "REMOVE_EMPTY";
        await SeedPriorityAsync(operation, load == "FULL" ? 40 : 70);
        await SeedTemplateAsync(setup, operation);
        foreach (var slot in setup.Slots)
        {
            await fixture.SetSlotAsync(slot, "OCCUPIED", load, setup.MainPart);
        }

        return setup;
    }

    private async Task<Setup> SetupAsync(int slotCount = 1, bool canChangeVariant = false, string role = RoleCodes.LineOperator)
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("DELETE FROM master.production_end_priorities WHERE operation_code IN ('REMOVE_FULL', 'REMOVE_EMPTY')");
        var context = await fixture.ConfigureMachineAsync();
        var user = await fixture.SeedIdentityAsync(role: role, canChangeVariant: canChangeVariant);
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, slotCount);
        var part = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES (@code) RETURNING id", ("code", "PE-MAIN-" + Guid.NewGuid().ToString("N")[..8]));
        var group = await fixture.ScalarAsync<Guid>("INSERT INTO master.product_groups(variant_id, main_part_id) VALUES (@variant, @part) RETURNING id", ("variant", context.VariantId), ("part", part));
        foreach (var slot in slots)
        {
            await fixture.ExecuteAsync("INSERT INTO master.layout_slot_parts(layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING", ("slot", slot), ("part", part));
            await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        }

        return new Setup(context, user, group, part, slots);
    }

    private Task SeedPriorityAsync(string operation, int priority) => fixture.ExecuteAsync("""
        INSERT INTO master.production_end_priorities (operation_code, priority)
        VALUES (@code, @priority)
        ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
        """, ("code", operation), ("priority", priority));

    private Task SeedTemplateAsync(Setup setup, string operation) => fixture.ExecuteAsync("""
        INSERT INTO master.task_templates (variant_id, product_group_id, task_type_id, rds_task_label, priority, is_active, config_json)
        SELECT @variant, @group, tt.id, 'PE-TEST', 10, true, '{"testScope":"PRODUCTION_END"}'::jsonb
        FROM master.task_types tt WHERE tt.code = @code
        """, ("variant", setup.Context.VariantId), ("group", setup.Group), ("code", operation));

    private static async Task<ProductionSessionRecord> StartAsync(HttpClient client, Setup setup)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/production-sessions")
        {
            Content = JsonContent.Create(new StartProductionSessionHttpRequest(setup.Context.VariantId, setup.Group, setup.MainPart))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "production-start");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductionSessionResult>())!.Session;
    }

    private static Task<HttpResponseMessage> EndAsync(HttpClient client, Setup setup, Guid sessionId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/production-sessions/{sessionId}/end");
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> BringAsync(HttpClient client, Setup setup, string key, Guid sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                setup.Context.VariantId, setup.Context.LayoutId, null, setup.Group, setup.MainPart, 1, "FULL", 100, sessionId))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static async Task<ProductionSessionResult> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductionSessionResult>())!;
    }

    private Task<string> StatusAsync(Guid sessionId) =>
        fixture.ScalarAsync<string>("SELECT status FROM ops.production_sessions WHERE id = @id", ("id", sessionId));

    private static async Task CompleteAsync(PostgresTransportWorkflowStore store, Guid requestId)
    {
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
    }

    private async Task<Guid[]> IdsAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var values = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetGuid(0));
        }

        return values.ToArray();
    }

    private async Task<IReadOnlyList<Guid>> QueuedAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        return await new PostgresRdsExecutionStore(dataSource).ListQueuedRequestIdsAsync(20);
    }

    private sealed record Setup(LayoutContext Context, TestIdentity User, Guid Group, Guid MainPart, Guid[] Slots);
}
