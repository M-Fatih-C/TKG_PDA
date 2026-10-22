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
    public async Task Restart_multi_child_end_preserves_chain_and_finishes_once()
    {
        await using var api = new RestartApiProcess(fixture.ConnectionString);
        var setup = await OccupyAsync(3, "FULL");
        await api.StartAsync();
        using var client = await api.LoginAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "restart-multi")).EnsureSuccessStatusCode();
        var requests = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");
        Assert.Equal(3, requests.Length);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        await api.RestartAsync();
        using var restarted = await api.LoginAsync(setup.User);
        var replay = await ReadAsync(await EndAsync(restarted, setup, session.Id, "restart-multi"));
        Assert.True(replay.WasIdempotentReplay);
        Assert.Equal(requests, await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no"));
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        for (var index = 0; index < requests.Length; index++)
        {
            await CompleteAsync(store, requests[index]); // Isolated outcome evidence, no robot call.
            Assert.Equal(index == requests.Length - 1 ? "ENDED" : "ENDING", await StatusAsync(session.Id));
        }
        await api.RestartAsync();
        using var final = await api.LoginAsync(setup.User);
        var ended = await ReadAsync(await EndAsync(final, setup, session.Id, "restart-multi"));
        Assert.Equal("ENDED", ended.Session.Status);
        Assert.NotNull(ended.Session.EndedAt);
        Assert.Equal("COMPLETED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_ENDED'"));
        Assert.Equal(3L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_failed_or_cancelled_cleanup_replays_old_key_and_replans_new_key(bool cancel)
    {
        await using var api = new RestartApiProcess(fixture.ConnectionString);
        var setup = await OccupyAsync(2, "FULL");
        await api.StartAsync();
        using var client = await api.LoginAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "failed-original")).EnsureSuccessStatusCode();
        var original = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        if (cancel) await store.CancelAsync(original[0], setup.User.UserId);
        else
        {
            await store.RecordExecutionTransitionAsync(original[0], TransportRequestStatus.SentToRds);
            await store.RecordExecutionTransitionAsync(original[0], TransportRequestStatus.Running);
            await store.RecordExecutionOutcomeAsync(original[0], new ExecutionOutcome(TransportRequestStatus.Failed, "RESTART_PROOF", "isolated cleanup failure"));
        }
        Assert.Equal("FAILED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id=@id", ("id", original[1])));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        await api.RestartAsync();
        using var restarted = await api.LoginAsync(setup.User);
        Assert.True((await ReadAsync(await EndAsync(restarted, setup, session.Id, "failed-original"))).WasIdempotentReplay);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        var retryResponse = await EndAsync(restarted, setup, session.Id, "failed-new-key");
        if (!cancel)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, retryResponse.StatusCode);
            Assert.Equal("PRODUCTION_END_POSITION_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(retryResponse));
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
            Assert.Equal("ENDING", await StatusAsync(session.Id));
            return;
        }

        retryResponse.EnsureSuccessStatusCode();
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
        var retried = await IdsAsync("SELECT i.transport_request_id FROM ops.production_end_run_items i JOIN ops.production_end_runs r ON r.id=i.run_id WHERE r.status='RUNNING' ORDER BY i.sequence_no");
        Assert.Equal(2, retried.Length);
        Assert.DoesNotContain(retried, id => original.Contains(id));
        foreach (var request in retried) await CompleteAsync(store, request);
        Assert.Equal("ENDED", await StatusAsync(session.Id));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs WHERE status='FAILED'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs WHERE status='COMPLETED'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action='PRODUCTION_ENDED'"));
    }

    [Fact]
    public async Task Restart_after_final_child_commit_keeps_session_ended_without_callback_replay()
    {
        await using var api = new RestartApiProcess(fixture.ConnectionString);
        var setup = await OccupyAsync(2, "FULL");
        await api.StartAsync();
        using var client = await api.LoginAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "completed-before-restart")).EnsureSuccessStatusCode();
        await using (var store = new PostgresTransportWorkflowStore(fixture.ConnectionString))
        {
            foreach (var request in await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no"))
                await CompleteAsync(store, request);
        }
        Assert.Equal("ENDED", await StatusAsync(session.Id));
        await api.RestartAsync();
        using var restarted = await api.LoginAsync(setup.User);
        var active = await restarted.GetAsync($"/api/v1/machines/{setup.Context.MachineId}/production-sessions/active");
        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);
        Assert.Equal("COMPLETED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action='PRODUCTION_ENDED'"));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_events WHERE event_type='REQUEST_COMPLETED'"));
    }

    [Fact]
    public async Task Restart_kill_during_run_advancement_rolls_back_child_run_and_slot_together()
    {
        await using var api = new RestartApiProcess(fixture.ConnectionString);
        var setup = await OccupyAsync(1, "FULL");
        await api.StartAsync();
        using var client = await api.LoginAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "crash-window")).EnsureSuccessStatusCode();
        var request = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        var run = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.production_end_runs");
        var childBefore = await fixture.ScalarAsync<string>("SELECT row_to_json(r)::text FROM ops.transport_requests r WHERE id=@id", ("id", request));
        var slotBefore = await fixture.ScalarAsync<string>("SELECT row_to_json(s)::text FROM ops.slot_runtime_state s WHERE layout_slot_id=@slot", ("slot", setup.Slots[0]));
        var gate = Random.Shared.NextInt64(1, long.MaxValue);
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(@gate)", blocker))
        {
            command.Parameters.AddWithValue("gate", gate);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            // Exact run guard; the database trigger holds the real API inside
            // its transaction after child cancellation, before run advancement.
            await fixture.ExecuteAsync($"""
                CREATE FUNCTION ops.restart_proof_gate() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN PERFORM pg_advisory_xact_lock({gate}); RETURN NEW; END $fn$;
                CREATE TRIGGER restart_proof_gate BEFORE UPDATE ON ops.production_end_runs
                FOR EACH ROW WHEN (OLD.id='{run}') EXECUTE FUNCTION ops.restart_proof_gate();
                """);
            var pending = client.PostAsync($"/api/v1/machines/{setup.Context.MachineId}/transport-requests/{request}/cancel", null);
            var waiting = false;
            for (var attempt = 0; attempt < 100 && !waiting; attempt++)
            {
                waiting = await fixture.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE application_name=@name AND wait_event='advisory'", ("name", api.ApplicationName)) > 0;
                if (!waiting) await Task.Delay(50);
            }
            Assert.True(waiting, "The real API must overlap the injected advancement gate.");
            await api.KillAsync();
            try { await pending; Assert.Fail("Killed API must not report successful cancellation."); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@gate)", blocker);
            release.Parameters.AddWithValue("gate", gate);
            await release.ExecuteNonQueryAsync();
            await fixture.ExecuteAsync("DROP TRIGGER IF EXISTS restart_proof_gate ON ops.production_end_runs; DROP FUNCTION IF EXISTS ops.restart_proof_gate()");
        }
        var gone = false;
        for (var attempt = 0; attempt < 100 && !gone; attempt++)
        {
            gone = await fixture.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE application_name=@name", ("name", api.ApplicationName)) == 0;
            if (!gone) await Task.Delay(50);
        }
        Assert.True(gone, "Killed API database transactions must close before checking rollback.");
        Assert.Equal(childBefore, await fixture.ScalarAsync<string>("SELECT row_to_json(r)::text FROM ops.transport_requests r WHERE id=@id", ("id", request)));
        Assert.Equal(slotBefore, await fixture.ScalarAsync<string>("SELECT row_to_json(s)::text FROM ops.slot_runtime_state s WHERE layout_slot_id=@slot", ("slot", setup.Slots[0])));
        Assert.Equal("RUNNING", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_events WHERE event_type='REQUEST_CANCELLED'"));
        await api.StartAsync();
        using var restarted = await api.LoginAsync(setup.User);
        (await restarted.PostAsync($"/api/v1/machines/{setup.Context.MachineId}/transport-requests/{request}/cancel", null)).EnsureSuccessStatusCode();
        Assert.Equal("FAILED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_end_runs"));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Product_specific_return_group_wins_over_a_generic_destination_group()
    {
        var setup = await SetupAsync();
        await MapAltAsync(setup, setup.Slots);
        var specificGroup = await CreateReturnGroupAsync(setup);
        var specificSlot = await AddReturnSlotAsync(setup, specificGroup, priority: 1, displayOrder: 9800);
        var genericGroup = await CreateReturnGroupAsync(setup);
        await AddReturnSlotAsync(setup, genericGroup, priority: 1, displayOrder: 9801);
        await SeedAltRemovalAsync(setup, specificGroup);
        await fixture.ExecuteAsync("""
            INSERT INTO master.task_templates (variant_id, task_type_id, destination_slot_group_id, is_active)
            SELECT @variant, id, @destination, true FROM master.task_types WHERE code = 'REMOVE_FULL'
            """, ("variant", setup.Context.VariantId), ("destination", genericGroup));
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "specific-return")).EnsureSuccessStatusCode();
        Assert.Equal(specificSlot, await fixture.ScalarAsync<Guid>("SELECT destination_slot_id FROM ops.transport_requests"));
    }

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
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL'"));
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);

        Assert.Equal("ENDED", await StatusAsync(session.Id));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL'"));
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
    public async Task Post_dispatch_predecessor_failure_blocks_later_cleanup_and_a_new_attempt()
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
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal("PRODUCTION_END_POSITION_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(retry));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", slots[1])));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
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
        var container = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync(
            "UPDATE ops.slot_runtime_state SET current_container_type_id = @container WHERE layout_slot_id = @slot",
            ("container", container),
            ("slot", setup.Slots[0]));
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
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL' OR transport_request_id IS NOT NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_events"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.notification_outbox"));
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", setup.Slots[0])));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>("SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", setup.Slots[0])));
        Assert.Equal("OCCUPIED", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", destination)));
        Assert.Equal("FULL", await fixture.ScalarAsync<string>("SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", destination)));
        Assert.Equal(alt, await fixture.ScalarAsync<Guid>("SELECT current_part_id FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", destination)));
        Assert.Equal(container, await fixture.ScalarAsync<Guid>("SELECT current_container_type_id FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", destination)));
        Assert.Equal("ENDED", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Two_alt_full_pallets_receive_distinct_return_slots()
    {
        var setup = await SetupAsync(slotCount: 2);
        var alt = await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        var first = await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9801);
        var second = await AddReturnSlotAsync(setup, groupId, priority: 2, displayOrder: 9802);
        await SeedAltRemovalAsync(setup, groupId);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-two-alt")).EnsureSuccessStatusCode();

        var destinations = await IdsAsync("""
            SELECT r.destination_slot_id
            FROM ops.production_end_run_items i
            JOIN ops.transport_requests r ON r.id = i.transport_request_id
            ORDER BY i.sequence_no
            """);
        Assert.Equal([first, second], destinations);
        Assert.Equal(2, destinations.Distinct().Count());
        Assert.Equal("ENDING", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task Too_few_return_slots_creates_zero_cleanup_requests()
    {
        var setup = await SetupAsync(slotCount: 2);
        await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9801);
        await SeedAltRemovalAsync(setup, groupId);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-capacity");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
    }

    [Fact]
    public async Task Occupied_configured_return_slot_is_not_allocated()
    {
        var setup = await SetupAsync();
        await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9801, occupancy: "OCCUPIED", load: "FULL");
        await SeedAltRemovalAsync(setup, groupId);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-occupied-return");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Return_slot_with_an_active_reservation_is_not_allocated()
    {
        var setup = await SetupAsync();
        await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        var reserved = await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9801);
        await SeedAltRemovalAsync(setup, groupId);
        var other = await fixture.ContextAsync("SH-01-B", "1");
        await fixture.ExecuteAsync("""
            INSERT INTO ops.transport_requests (
                idempotency_key, request_origin, machine_id, variant_id, layout_id,
                task_type_id, destination_slot_id, status, priority)
            SELECT 'reserve-return', 'API', @machine, @variant, @layout, tt.id, @slot, 'QUEUED', 1
            FROM master.task_types tt WHERE tt.code = 'BRING_EMPTY'
            """,
            ("machine", other.MachineId),
            ("variant", other.VariantId),
            ("layout", other.LayoutId),
            ("slot", reserved));
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);

        var response = await EndAsync(client, setup, session.Id, "end-reserved-return");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal("ACTIVE", await StatusAsync(session.Id));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Return_slots_are_allocated_by_group_priority_before_display_order()
    {
        var setup = await SetupAsync(slotCount: 2);
        await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        var laterDisplay = await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9900);
        var earlierDisplay = await AddReturnSlotAsync(setup, groupId, priority: 5, displayOrder: 10);
        await SeedAltRemovalAsync(setup, groupId);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-order")).EnsureSuccessStatusCode();

        var destinations = await IdsAsync("""
            SELECT r.destination_slot_id
            FROM ops.production_end_run_items i
            JOIN ops.transport_requests r ON r.id = i.transport_request_id
            ORDER BY i.sequence_no
            """);
        Assert.Equal([laterDisplay, earlierDisplay], destinations);
    }

    [Fact]
    public async Task Failed_post_dispatch_alt_removal_marks_its_endpoints_unknown_while_queued_successor_is_cancelled()
    {
        var setup = await SetupAsync(slotCount: 2);
        await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        var firstReturn = await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9801);
        var secondReturn = await AddReturnSlotAsync(setup, groupId, priority: 2, displayOrder: 9802);
        await SeedAltRemovalAsync(setup, groupId);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-fail-return")).EnsureSuccessStatusCode();
        var requests = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.RecordExecutionTransitionAsync(requests[0], TransportRequestStatus.SentToRds);
        await store.RecordExecutionOutcomeAsync(requests[0], new ExecutionOutcome(TransportRequestStatus.Failed, "TEST", "cleanup failed"));

        Assert.Equal("FAILED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id = @id", ("id", requests[0])));
        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id = @id", ("id", requests[1])));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", firstReturn)));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>("SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", firstReturn)));
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", secondReturn)));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>("SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", secondReturn)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL'"));
        Assert.Equal("ENDING", await StatusAsync(session.Id));
    }

    [Fact]
    public async Task New_end_attempt_is_blocked_after_an_unreconciled_post_dispatch_failure()
    {
        var setup = await SetupAsync(slotCount: 2);
        var alt = await MapAltAsync(setup, setup.Slots);
        var groupId = await CreateReturnGroupAsync(setup);
        var firstReturn = await AddReturnSlotAsync(setup, groupId, priority: 1, displayOrder: 9801);
        var secondReturn = await AddReturnSlotAsync(setup, groupId, priority: 2, displayOrder: 9802);
        await SeedAltRemovalAsync(setup, groupId);
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = await StartAsync(client, setup);
        (await EndAsync(client, setup, session.Id, "end-retry-return")).EnsureSuccessStatusCode();
        var requests = await IdsAsync("SELECT transport_request_id FROM ops.production_end_run_items ORDER BY sequence_no");
        var sources = await IdsAsync("SELECT layout_slot_id FROM ops.production_end_run_items ORDER BY sequence_no");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await CompleteAsync(store, requests[0]);
        await store.RecordExecutionTransitionAsync(requests[1], TransportRequestStatus.SentToRds);
        await store.RecordExecutionOutcomeAsync(requests[1], new ExecutionOutcome(TransportRequestStatus.Failed, "TEST", "second cleanup failed"));

        Assert.Equal("OCCUPIED", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", firstReturn)));
        Assert.Equal(alt, await fixture.ScalarAsync<Guid>("SELECT current_part_id FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", firstReturn)));
        var retry = await EndAsync(client, setup, session.Id, "end-retry-return-2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal("PRODUCTION_END_POSITION_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(retry));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", sources[1])));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", secondReturn)));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
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

    private async Task<Guid> MapAltAsync(Setup setup, IReadOnlyList<Guid> slots)
    {
        var alt = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code) VALUES (@code) RETURNING id",
            ("code", "PE-ALT-" + Guid.NewGuid().ToString("N")[..8]));
        await fixture.ExecuteAsync("""
            INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type)
            VALUES (@group, @part, 'ALT_PART')
            """, ("group", setup.Group), ("part", alt));
        foreach (var slot in slots)
        {
            await fixture.ExecuteAsync(
                "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
                ("slot", slot),
                ("part", alt));
            await fixture.SetSlotAsync(slot, "OCCUPIED", "FULL", alt);
        }

        return alt;
    }

    private Task<Guid> CreateReturnGroupAsync(Setup setup) => fixture.ScalarAsync<Guid>("""
        INSERT INTO master.slot_groups (layout_id, code, name, purpose)
        VALUES (@layout, @code, 'PE return', 'DESTINATION')
        RETURNING id
        """, ("layout", setup.Context.LayoutId), ("code", "PE-GROUP-" + Guid.NewGuid().ToString("N")[..8]));

    private async Task<Guid> AddReturnSlotAsync(
        Setup setup,
        Guid groupId,
        int priority,
        int displayOrder,
        string occupancy = "VACANT",
        string load = "UNKNOWN")
    {
        var slotId = await fixture.ScalarAsync<Guid>("""
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            VALUES (@layout, @code, 'PE return', 'OTHER', @display_order)
            RETURNING id
            """,
            ("layout", setup.Context.LayoutId),
            ("code", "PE-RETURN-" + Guid.NewGuid().ToString("N")[..8]),
            ("display_order", displayOrder));
        await fixture.SetSlotAsync(slotId, occupancy, load, occupancy == "OCCUPIED" ? setup.MainPart : null);
        await fixture.ExecuteAsync("""
            INSERT INTO master.slot_group_members (slot_group_id, layout_slot_id, priority)
            VALUES (@group, @slot, @priority)
            """, ("group", groupId), ("slot", slotId), ("priority", priority));
        return slotId;
    }

    private async Task SeedAltRemovalAsync(Setup setup, Guid groupId)
    {
        await SeedPriorityAsync("REMOVE_FULL", 40);
        await fixture.ExecuteAsync("""
            INSERT INTO master.task_templates (variant_id, product_group_id, task_type_id, destination_slot_group_id, rds_task_label, priority, is_active, config_json)
            SELECT @variant, @product, tt.id, @destination_group, 'PE-ALT', 10, true, '{"testScope":"PRODUCTION_END"}'::jsonb
            FROM master.task_types tt WHERE tt.code = 'REMOVE_FULL'
            """, ("variant", setup.Context.VariantId), ("product", setup.Group), ("destination_group", groupId));
    }

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
