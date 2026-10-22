using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class ProductionSessionApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Bound_line_terminal_can_start_and_end_without_legacy_user_machine_permissions()
    {
        var setup = await SetupAsync();
        await fixture.ExecuteAsync("DELETE FROM auth.user_machine_permissions WHERE user_id = @user", ("user", setup.User.UserId));
        using var client = await fixture.LoginClientAsync(setup.User);
        var started = await StartAsync(client, setup, "terminal-start");
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var session = (await ReadAsync(started)).Session;
        var ended = await EndAsync(client, setup, session.Id);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal("ENDED", (await ReadAsync(ended)).Session.Status);
    }

    [Fact]
    public async Task Storage_boundary_rejects_line_terminal_rebound_to_another_machine()
    {
        var setup = await SetupAsync();
        var other = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-B'");
        await fixture.ExecuteAsync("UPDATE audit.client_devices SET machine_id = @machine WHERE id = @device", ("machine", other), ("device", setup.User.DeviceId));
        await using var store = new TkgPda.Infrastructure.Postgres.PostgresTransportWorkflowStore(fixture.ConnectionString);
        var error = await Assert.ThrowsAsync<TkgPda.Domain.WorkflowRuleException>(() => store.StartAsync(new(
            setup.Context.MachineId, setup.Context.VariantId, setup.Group, setup.MainPart, setup.User.UserId, setup.User.DeviceId, "rebound")));
        Assert.Equal("PRODUCTION_ACCESS_DENIED", error.Code);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
    }

    [Fact]
    public async Task Start_end_and_retries_persist_server_time_actor_device_and_one_audit_each()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Path(setup) + "/active")).StatusCode);
        var lowerBound = DateTimeOffset.UtcNow.AddSeconds(-1);
        var first = await StartAsync(client, setup, "production-start");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var started = await ReadAsync(first);
        Assert.Equal("ACTIVE", started.Session.Status);
        Assert.Equal(setup.User.UserId, started.Session.StartedBy);
        Assert.Equal(setup.User.DeviceId, started.Session.StartedDeviceId);
        Assert.InRange(started.Session.StartedAt, lowerBound, DateTimeOffset.UtcNow.AddSeconds(1));
        var replay = await StartAsync(client, setup, "production-start");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(started.Session, (await ReadAsync(replay)).Session);
        Assert.Equal("true", replay.Headers.GetValues(ApiHeaders.IdempotencyReplayed).Single());
        var active = await client.GetFromJsonAsync<ProductionSessionRecord>(Path(setup) + "/active");
        Assert.Equal(started.Session, active);
        var end = await EndAsync(client, setup, started.Session.Id);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        var ended = await ReadAsync(end);
        Assert.Equal("ENDED", ended.Session.Status);
        Assert.True(ended.Session.EndedAt >= ended.Session.StartedAt);
        Assert.Equal(setup.User.UserId, ended.Session.EndedBy);
        Assert.Equal(setup.User.DeviceId, ended.Session.EndedDeviceId);
        Assert.Equal(ended.Session, (await ReadAsync(await EndAsync(client, setup, started.Session.Id))).Session);
        Assert.Equal(ended.Session, (await ReadAsync(await StartAsync(client, setup, "production-start"))).Session);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Path(setup) + "/active")).StatusCode);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE entity_type = 'PRODUCTION_SESSION'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        Assert.Empty(fixture.RdsClient.Calls);
    }

    [Fact]
    public async Task Concurrent_same_key_returns_one_session_and_one_audit()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => StartAsync(client, setup, "same-key")));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, replies.Count(r => r.StatusCode == HttpStatusCode.OK));
        var results = await Task.WhenAll(replies.Select(ReadAsync));
        Assert.Single(results.Select(r => r.Session.Id).Distinct());
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_STARTED'"));
    }

    [Fact]
    public async Task Concurrent_different_keys_allow_only_one_active_production()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var replies = await Task.WhenAll(StartAsync(client, setup, "first"), StartAsync(client, setup, "second"));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Created);
        var conflict = Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("PRODUCTION_SESSION_ALREADY_ACTIVE", await ApiTestFixture.ErrorCodeAsync(conflict));
    }

    [Fact]
    public async Task Reused_key_with_changed_payload_is_not_a_second_start()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        (await StartAsync(client, setup, "key")).EnsureSuccessStatusCode();
        var response = await StartAsync(client, setup with { MainPart = Guid.NewGuid() }, "key");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
    }

    [Theory]
    [InlineData("variant")]
    [InlineData("main")]
    [InlineData("group")]
    public async Task Unsupported_customer_context_is_rejected(string field)
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var invalid = field switch
        {
            "variant" => setup with { Context = setup.Context with { VariantId = Guid.NewGuid() } },
            "main" => setup with { MainPart = Guid.NewGuid() },
            _ => setup with { Group = Guid.NewGuid() }
        };
        var response = await StartAsync(client, invalid, "invalid");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_CONTEXT_MISMATCH", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("VALIDATING")]
    [InlineData("QUEUED")]
    [InlineData("SENT_TO_RDS")]
    [InlineData("RUNNING")]
    public async Task End_blocks_every_active_transport_status_without_changing_request_or_readiness(string status)
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = (await ReadAsync(await StartAsync(client, setup, "session"))).Session;
        var transport = await BringAsync(client, setup, "transport");
        transport.EnsureSuccessStatusCode();
        // Synthetic executor state in isolated PostgreSQL; never call a robot.
        await fixture.ExecuteAsync("UPDATE ops.transport_requests SET status = @status", ("status", status));
        var response = await EndAsync(client, setup, session.Id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ACTIVE_TRANSPORT_EXISTS", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(status, await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests"));
        Assert.Equal("NOT_REQUIRED", await fixture.ScalarAsync<string>("SELECT readiness_state FROM ops.transport_requests"));
        Assert.Equal(session.Id, await fixture.ScalarAsync<Guid>("SELECT production_session_id FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions WHERE ended_at IS NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_ENDED'"));
    }

    [Fact]
    public async Task Explicit_transport_cancel_then_end_is_allowed_without_automatic_cancel()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = (await ReadAsync(await StartAsync(client, setup, "session"))).Session;
        (await BringAsync(client, setup, "bring")).EnsureSuccessStatusCode();
        var requestId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests WHERE idempotency_key = 'bring'");
        (await client.PostAsync($"/api/v1/machines/{setup.Context.MachineId}/transport-requests/{requestId}/cancel", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await EndAsync(client, setup, session.Id)).StatusCode);
        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Start_requires_a_clean_transport_boundary()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        (await BringAsync(client, setup, "prior-request")).EnsureSuccessStatusCode();
        var response = await StartAsync(client, setup, "start");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ACTIVE_TRANSPORT_EXISTS", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Active_production_blocks_other_product_transport_without_reservation_or_load_change()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        (await StartAsync(client, setup, "start")).EnsureSuccessStatusCode();
        // A valid different customer-like group/part reaches the production guard;
        // a random nonexistent group is rejected earlier by the position query.
        var otherPart = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts(part_code) VALUES ('P05-PRODUCTION-OTHER') RETURNING id");
        var otherGroup = await fixture.ScalarAsync<Guid>("INSERT INTO master.product_groups(variant_id, main_part_id) VALUES (@v, @p) RETURNING id", ("v", setup.Context.VariantId), ("p", otherPart));
        await fixture.ExecuteAsync("INSERT INTO master.layout_slot_parts(layout_slot_id, part_id) VALUES (@s, @p)", ("s", setup.Slot), ("p", otherPart));
        var response = await BringAsync(client, setup with { Group = otherGroup, MainPart = otherPart }, "wrong-product");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_CONTEXT_MISMATCH", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot", ("slot", setup.Slot)));
    }

    [Fact]
    public async Task Related_part_transport_is_linked_to_the_main_product_session()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var related = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts(part_code) VALUES ('P05-PRODUCTION-RELATED') RETURNING id");
        await fixture.ExecuteAsync("INSERT INTO master.product_group_parts(product_group_id, part_id, relation_type) VALUES (@g, @p, 'RELATED'); INSERT INTO master.layout_slot_parts(layout_slot_id, part_id) VALUES (@s, @p)", ("g", setup.Group), ("p", related), ("s", setup.Slot));
        var session = (await ReadAsync(await StartAsync(client, setup, "start"))).Session;
        (await BringAsync(client, setup with { MainPart = related }, "related")).EnsureSuccessStatusCode();
        Assert.Equal(session.Id, await fixture.ScalarAsync<Guid>("SELECT production_session_id FROM ops.transport_requests"));
        Assert.Equal(related, await fixture.ScalarAsync<Guid>("SELECT part_id FROM ops.transport_requests"));
        Assert.Equal(setup.MainPart, session.MainPartId);
    }

    [Fact]
    public async Task Runtime_context_change_is_blocked_but_same_context_is_allowed()
    {
        var setup = await SetupAsync(role: RoleCodes.Supervisor, canChange: true);
        using var client = await fixture.LoginClientAsync(setup.User);
        (await StartAsync(client, setup, "start")).EnsureSuccessStatusCode();
        var path = $"/api/v1/machines/{setup.Context.MachineId}/variant-selection";
        var response = await client.PostAsJsonAsync(path, new VariantSwitchHttpRequest(Guid.NewGuid(), setup.Context.LayoutId, null));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_SESSION_ACTIVE", await ApiTestFixture.ErrorCodeAsync(response));
        (await client.PostAsJsonAsync(path, new VariantSwitchHttpRequest(setup.Context.VariantId, setup.Context.LayoutId, null))).EnsureSuccessStatusCode();
        Assert.Equal(setup.Context.VariantId, await fixture.ScalarAsync<Guid>("SELECT active_variant_id FROM ops.machine_runtime_state WHERE machine_id = @machine", ("machine", setup.Context.MachineId)));
    }

    [Fact]
    public async Task Stale_end_retry_never_ends_a_new_session()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var old = (await ReadAsync(await StartAsync(client, setup, "old"))).Session;
        (await EndAsync(client, setup, old.Id)).EnsureSuccessStatusCode();
        var current = (await ReadAsync(await StartAsync(client, setup, "new"))).Session;
        var stale = await ReadAsync(await EndAsync(client, setup, old.Id));
        Assert.True(stale.WasIdempotentReplay);
        Assert.Equal(current.Id, (await client.GetFromJsonAsync<ProductionSessionRecord>(Path(setup) + "/active"))!.Id);
    }

    [Theory]
    [InlineData(RoleCodes.WarehouseOperator, true, false)]
    [InlineData(RoleCodes.LineOperator, false, false)]
    [InlineData(RoleCodes.Admin, true, true)]
    public async Task Warehouse_role_missing_permission_and_admin_web_cannot_mutate_production(string role, bool canOperate, bool adminWeb)
    {
        var setup = await SetupAsync(role, canOperate);
        using var client = adminWeb ? await fixture.AdminLoginClientAsync(setup.User) : await fixture.LoginClientAsync(setup.User);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(client, setup, "denied")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EndAsync(client, setup, Guid.NewGuid())).StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
    }

    [Fact]
    public async Task Missing_device_and_cross_machine_reads_are_rejected()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/machines/{Guid.NewGuid()}/production-sessions/active")).StatusCode);
        client.DefaultRequestHeaders.Remove(ApiHeaders.DeviceCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StartAsync(client, setup, "missing-device")).StatusCode);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_bad_request()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var response = await StartAsync(client, setup, null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Concurrent_start_and_variant_switch_are_serialized_by_the_same_machine_lock()
    {
        var setup = await SetupAsync(RoleCodes.Supervisor, canChange: true);
        using var client = await fixture.LoginClientAsync(setup.User);
        var other = await fixture.ContextAsync("SH-01-A", "2");
        var results = await Task.WhenAll(
            StartAsync(client, setup, "start-race"),
            client.PostAsJsonAsync($"/api/v1/machines/{setup.Context.MachineId}/variant-selection", new VariantSwitchHttpRequest(other.VariantId, other.LayoutId, null)))
            .WaitAsync(TimeSpan.FromSeconds(15));
        if (results[0].StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(HttpStatusCode.Conflict, results[1].StatusCode);
            Assert.Equal("PRODUCTION_SESSION_ACTIVE", await ApiTestFixture.ErrorCodeAsync(results[1]));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, results[0].StatusCode);
            Assert.Equal("PRODUCTION_CONTEXT_MISMATCH", await ApiTestFixture.ErrorCodeAsync(results[0]));
            Assert.Equal(HttpStatusCode.OK, results[1].StatusCode);
        }
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions s JOIN ops.machine_runtime_state m ON m.machine_id = s.machine_id WHERE s.ended_at IS NULL AND s.variant_id <> m.active_variant_id"));
    }

    [Fact]
    public async Task Concurrent_end_requests_record_one_end_and_one_audit()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = (await ReadAsync(await StartAsync(client, setup, "start"))).Session;
        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EndAsync(client, setup, session.Id)))
            .WaitAsync(TimeSpan.FromSeconds(15));
        var results = await Task.WhenAll(replies.Select(ReadAsync));
        Assert.Single(results, r => !r.WasIdempotentReplay);
        Assert.Single(results.Select(r => r.Session.EndedAt).Distinct());
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'PRODUCTION_ENDED'"));
    }

    [Theory]
    [InlineData("PRODUCTION_STARTED")]
    [InlineData("PRODUCTION_ENDED")]
    public async Task Audit_failure_rolls_back_the_session_mutation(string failingAction)
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var sessionId = failingAction == "PRODUCTION_ENDED"
            ? (await ReadAsync(await StartAsync(client, setup, "before-trigger"))).Session.Id
            : Guid.Empty;
        await fixture.ExecuteAsync("""
            CREATE FUNCTION audit.reject_production_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action = TG_ARGV[0] THEN RAISE EXCEPTION 'Synthetic audit failure'; END IF;
                RETURN NEW;
            END $$;
            """);
        try
        {
            // Action comes only from the two fixed InlineData values, never external input.
            await fixture.ExecuteAsync($"CREATE TRIGGER reject_production_test BEFORE INSERT ON audit.audit_logs FOR EACH ROW EXECUTE FUNCTION audit.reject_production_test('{failingAction}')");
            var response = failingAction == "PRODUCTION_STARTED"
                ? await StartAsync(client, setup, "with-trigger")
                : await EndAsync(client, setup, sessionId);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(failingAction == "PRODUCTION_STARTED" ? 0L : 1L,
                await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions WHERE ended_at IS NULL"));
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = @action", ("action", failingAction)));
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER IF EXISTS reject_production_test ON audit.audit_logs; DROP FUNCTION audit.reject_production_test()");
        }
    }

    [Fact]
    public async Task Batch_children_keep_independent_transport_identity_and_share_production_context()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var secondSlot = (await fixture.LogisticsSlotsAsync(setup.Context.LayoutId, 2))[1];
        await fixture.ExecuteAsync("INSERT INTO master.layout_slot_parts(layout_slot_id, part_id) VALUES (@slot, @part)", ("slot", secondSlot), ("part", setup.MainPart));
        await fixture.SetSlotAsync(secondSlot, "VACANT", "UNKNOWN");
        var session = (await ReadAsync(await StartAsync(client, setup, "start"))).Session;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/container-actions/batch")
        {
            Content = JsonContent.Create(new CreateContainerActionBatchHttpRequest(setup.Context.VariantId, setup.Context.LayoutId, null,
                [new(setup.Group, setup.MainPart, 1, "EMPTY"), new(setup.Group, setup.MainPart, 2, "EMPTY")], 100))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "batch");
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests WHERE production_session_id = @id", ("id", session.Id)));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(DISTINCT idempotency_key) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(DISTINCT batch_id) FROM ops.transport_requests"));
        Assert.Equal(HttpStatusCode.Conflict, (await EndAsync(client, setup, session.Id)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_start_and_transport_link_only_after_the_start_linearization_point()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var replies = await Task.WhenAll(StartAsync(client, setup, "start"), BringAsync(client, setup, "bring"))
            .WaitAsync(TimeSpan.FromSeconds(15));
        replies[1].EnsureSuccessStatusCode();
        if (replies[0].StatusCode == HttpStatusCode.Created)
        {
            var session = (await ReadAsync(replies[0])).Session;
            Assert.Equal(session.Id, await fixture.ScalarAsync<Guid>("SELECT production_session_id FROM ops.transport_requests"));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, replies[0].StatusCode);
            Assert.Equal("ACTIVE_TRANSPORT_EXISTS", await ApiTestFixture.ErrorCodeAsync(replies[0]));
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions"));
        }
    }

    [Fact]
    public async Task End_racing_transport_create_obeys_machine_lock_and_legacy_sessionless_compatibility()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = (await ReadAsync(await StartAsync(client, setup, "start"))).Session;
        var replies = await Task.WhenAll(EndAsync(client, setup, session.Id), BringAsync(client, setup, "bring"))
            .WaitAsync(TimeSpan.FromSeconds(15));
        replies[1].EnsureSuccessStatusCode();
        if (replies[0].StatusCode == HttpStatusCode.OK)
        {
            // Existing clients do not yet require a session. This later request
            // must never be retroactively linked to a production already ended.
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests WHERE production_session_id IS NULL"));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, replies[0].StatusCode);
            Assert.Equal("ACTIVE_TRANSPORT_EXISTS", await ApiTestFixture.ErrorCodeAsync(replies[0]));
            Assert.Equal(session.Id, await fixture.ScalarAsync<Guid>("SELECT production_session_id FROM ops.transport_requests"));
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions WHERE ended_at IS NULL"));
        }
    }

    [Fact]
    public async Task Database_also_enforces_one_active_session_and_customer_product_identity()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        (await StartAsync(client, setup, "start")).EnsureSuccessStatusCode();
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
            INSERT INTO ops.production_sessions(machine_id, variant_id, product_group_id, main_part_id, start_key, started_by, started_device_id)
            SELECT machine_id, variant_id, product_group_id, main_part_id, 'different-key', started_by, started_device_id
            FROM ops.production_sessions
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("uq_production_active_machine", duplicate.ConstraintName);
        var otherVariant = await fixture.ContextAsync("SH-01-A", "2");
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
            INSERT INTO ops.production_sessions(machine_id, variant_id, product_group_id, main_part_id, start_key,
                started_by, started_device_id, started_at, ended_by, ended_device_id, ended_at, status)
            SELECT machine_id, @wrong_variant, product_group_id, main_part_id, 'wrong-group-variant',
                started_by, started_device_id, started_at, started_by, started_device_id, clock_timestamp(), 'ENDED'
            FROM ops.production_sessions
            """, ("wrong_variant", otherVariant.VariantId)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, invalid.SqlState);
        Assert.Equal("fk_production_product_identity", invalid.ConstraintName);
    }

    [Fact]
    public async Task Unknown_session_id_returns_not_found_without_ending_current_session()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        (await StartAsync(client, setup, "start")).EnsureSuccessStatusCode();
        var response = await EndAsync(client, setup, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PRODUCTION_SESSION_NOT_FOUND", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_sessions WHERE ended_at IS NULL"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_tablet_single_action_never_attaches_to_no_or_new_production(bool newProduction)
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var old = (await ReadAsync(await StartAsync(client, setup, "old"))).Session;
        (await EndAsync(client, setup, old.Id)).EnsureSuccessStatusCode();
        if (newProduction) (await StartAsync(client, setup, "new")).EnsureSuccessStatusCode();
        var response = await BringAsync(client, setup, "stale", old.Id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_SESSION_CHANGED", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id", ("id", setup.Slot)));
    }

    [Fact]
    public async Task Matching_session_action_replays_after_end_but_not_with_a_different_session_identity()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var session = (await ReadAsync(await StartAsync(client, setup, "start"))).Session;
        (await BringAsync(client, setup, "bring", session.Id)).EnsureSuccessStatusCode();
        var id = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        (await client.PostAsync($"/api/v1/machines/{setup.Context.MachineId}/transport-requests/{id}/cancel", null)).EnsureSuccessStatusCode();
        (await EndAsync(client, setup, session.Id)).EnsureSuccessStatusCode();
        (await BringAsync(client, setup, "bring", session.Id)).EnsureSuccessStatusCode();
        var wrong = await BringAsync(client, setup, "bring", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(wrong));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(session.Id, await fixture.ScalarAsync<Guid>("SELECT production_session_id FROM ops.transport_requests"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_batch_or_remove_all_rejected_atomically(bool removeAll)
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.User);
        var current = (await ReadAsync(await StartAsync(client, setup, "current"))).Session;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/machines/{setup.Context.MachineId}/container-actions/" + (removeAll ? "remove-all" : "batch"))
        {
            Content = removeAll
                ? JsonContent.Create(new RemoveAllContainersHttpRequest(setup.Context.VariantId, setup.Context.LayoutId, null, 100, Guid.NewGuid()))
                : JsonContent.Create(new CreateContainerActionBatchHttpRequest(setup.Context.VariantId, setup.Context.LayoutId, null,
                    [new(setup.Group, setup.MainPart, 1, "EMPTY")], 100, Guid.NewGuid()))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "stale-group");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PRODUCTION_SESSION_CHANGED", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_batches"));
        Assert.Equal(current.Id, await fixture.ScalarAsync<Guid>("SELECT id FROM ops.production_sessions WHERE ended_at IS NULL"));
    }

    private async Task<Setup> SetupAsync(string role = RoleCodes.LineOperator, bool canOperate = true, bool canChange = false)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var user = await fixture.SeedIdentityAsync(role: role, canOperate: canOperate, canChangeVariant: canChange);
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        var part = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES ('P05-PRODUCTION-MAIN') RETURNING id");
        var group = await fixture.ScalarAsync<Guid>("INSERT INTO master.product_groups(variant_id, main_part_id) VALUES (@variant, @part) RETURNING id", ("variant", context.VariantId), ("part", part));
        await fixture.ExecuteAsync("INSERT INTO master.layout_slot_parts(layout_slot_id, part_id) VALUES (@slot, @part)", ("slot", slot), ("part", part));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        return new(context, user, group, part, slot);
    }

    private static string Path(Setup setup) => $"/api/v1/machines/{setup.Context.MachineId}/production-sessions";
    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Setup setup, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path(setup))
        {
            Content = JsonContent.Create(new StartProductionSessionHttpRequest(setup.Context.VariantId, setup.Group, setup.MainPart))
        };
        if (key is not null) request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }
    private static Task<HttpResponseMessage> EndAsync(HttpClient client, Setup setup, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path(setup) + $"/{id}/end");
        request.Headers.Add(ApiHeaders.IdempotencyKey, "production-end");
        return client.SendAsync(request);
    }
    private static async Task<ProductionSessionResult> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductionSessionResult>())!;
    }
    private static Task<HttpResponseMessage> BringAsync(HttpClient client, Setup setup, string key, Guid? productionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(setup.Context.VariantId, setup.Context.LayoutId, null, setup.Group, setup.MainPart, 1, "EMPTY", 100, productionId))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }
    private sealed record Setup(LayoutContext Context, TestIdentity User, Guid Group, Guid MainPart, Guid Slot);
}
