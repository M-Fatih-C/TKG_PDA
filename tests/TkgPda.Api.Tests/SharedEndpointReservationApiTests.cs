using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// Cross-profile shared endpoint reservation hardening tests.
/// Verifies global capacity exclusivity across machines and navigation profiles
/// while preserving profile-scoped physical RDS WorkSite resolution.
/// </summary>
public sealed class SharedEndpointReservationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await RemoveIsolatedPhysicalFixturesAsync();
        await fixture.ResetAsync();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public async Task Mamul_projection_uses_healthy_registry_fg_binding_without_legacy_delivery_slots(int blocker, bool enabled)
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        var scene = setup.SceneA;
        await fixture.ExecuteAsync("DELETE FROM ops.store_location_delivery_slots");
        if (blocker == 1)
            await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_disabled = 1 WHERE navigation_profile_id = @nav", ("nav", scene.NavigationId));
        if (blocker == 2)
            await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_sync_at = NULL WHERE navigation_profile_id = @nav", ("nav", scene.NavigationId));
        if (blocker == 3)
            await fixture.ExecuteAsync("DELETE FROM integration.profile_shared_endpoint_bindings WHERE navigation_profile_id = @nav AND endpoint_class = 'FINISHED_GOODS'", ("nav", scene.NavigationId));
        using var client = await fixture.LoginClientAsync(scene.Operator);
        var positions = await client.GetFromJsonAsync<ContainerPositionRecord[]>(
            $"/api/v1/machines/{scene.MachineId}/container-positions?variantId={scene.VariantId}&layoutId={scene.LayoutId}&productGroupId={scene.Group}&partId={scene.MainPart}");
        var position = Assert.Single(positions!);
        Assert.Equal(enabled, position.FullAction.Enabled);
        Assert.False(position.EmptyAction.Enabled);
        if (enabled)
        {
            var response = await client.SendAsync(CreateFullContainerActionRequest(scene, "projected-full"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("FG-01", await fixture.ScalarAsync<string>("SELECT destination_business_code FROM ops.transport_requests WHERE machine_id = @machine", ("machine", scene.MachineId)));
        }
        else Assert.Equal(blocker switch { 1 => "RDS_WORKSITE_DISABLED", 2 => "RDS_WORKSITE_STALE", _ => "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED" }, position.FullAction.DisabledCode);
    }

    [Fact]
    public async Task Two_machines_on_different_nav_profiles_competing_for_finished_goods_reserve_distinct_endpoints()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true);
        try
        {
            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            // Execute concurrent finished-goods removal requests from two distinct machines/profiles
            var taskA = clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "concurrent-mamul-a"));
            var taskB = clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "concurrent-mamul-b"));

            await Task.WhenAll(taskA, taskB);

            var respA = await taskA;
            var respB = await taskB;

            var bodyA = await respA.Content.ReadAsStringAsync();
            var bodyB = await respB.Content.ReadAsStringAsync();

            Assert.True(respA.IsSuccessStatusCode, $"Request A failed: {bodyA}");
            Assert.True(respB.IsSuccessStatusCode, $"Request B failed: {bodyB}");

            var destA = await fixture.ScalarAsync<string>(
                "SELECT destination_business_code FROM ops.transport_requests WHERE machine_id = @m",
                ("m", setup.SceneA.MachineId));
            var destB = await fixture.ScalarAsync<string>(
                "SELECT destination_business_code FROM ops.transport_requests WHERE machine_id = @m",
                ("m", setup.SceneB.MachineId));

            Assert.NotNull(destA);
            Assert.NotNull(destB);
            Assert.NotEqual(destA, destB);

            var allocatedCodes = new HashSet<string> { destA, destB };
            Assert.Contains("FG-01", allocatedCodes);
            Assert.Contains("FG-02", allocatedCodes);

            // Assert each dispatch context resolves the WorkSite under its own NAV profile
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var execution = new PostgresRdsExecutionStore(dataSource);

            var reqA = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneA.MachineId));
            var reqB = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneB.MachineId));

            var ctxA = await execution.GetDispatchContextAsync(reqA);
            var ctxB = await execution.GetDispatchContextAsync(reqB);

            Assert.NotNull(ctxA);
            Assert.NotNull(ctxB);
            Assert.StartsWith("TKG_SH01A_V01_WS_FG", ctxA.Destination.SiteId, StringComparison.Ordinal);
            Assert.StartsWith("TKG_PAH01A_V01_WS_FG", ctxB.Destination.SiteId, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Competing_requests_for_single_capacity_endpoint_reject_second_request_deterministically()
    {
        // Only FG-01 exists globally (includeFg02: false)
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        try
        {
            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            var taskA = clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "single-cap-a"));
            var taskB = clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "single-cap-b"));

            await Task.WhenAll(taskA, taskB);

            var respA = await taskA;
            var respB = await taskB;

            var responses = new[] { respA, respB };
            var successCount = responses.Count(r => r.IsSuccessStatusCode);
            var failureCount = responses.Count(r => !r.IsSuccessStatusCode);

            Assert.Equal(1, successCount);
            Assert.Equal(1, failureCount);

            var failedResp = responses.Single(r => !r.IsSuccessStatusCode);
            var failedBody = await failedResp.Content.ReadAsStringAsync();

            Assert.Contains("FINISHED_GOODS_STORE_FULL", failedBody, StringComparison.Ordinal);

            // Exactly 1 active transport request was created with FG-01
            var totalActive = await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_requests WHERE destination_business_code = 'FG-01'");
            Assert.Equal(1L, totalActive);

            // Zero orphans
            var orphanCount = await fixture.ScalarAsync<long>(
                """
                SELECT count(*) FROM ops.transport_requests r
                LEFT JOIN ops.production_sessions s ON s.id = r.production_session_id AND s.machine_id = r.machine_id
                WHERE r.production_session_id IS NOT NULL AND s.id IS NULL
                """);
            Assert.Equal(0L, orphanCount);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Same_machine_concurrent_requests_remain_serialized_and_safe()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true);
        try
        {
            using var client = await fixture.LoginClientAsync(setup.SceneA.Operator);
            await StartAsync(client, setup.SceneA);

            // Two concurrent container actions for the same machine
            var task1 = client.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "same-mach-1"));
            var task2 = client.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "same-mach-2"));

            await Task.WhenAll(task1, task2);

            var resp1 = await task1;
            var resp2 = await task2;

            // One must succeed. The other may fail cleanly (e.g. active transport conflict or idempotency).
            Assert.True(resp1.IsSuccessStatusCode || resp2.IsSuccessStatusCode);

            // No corrupted database state or orphan
            var active = await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneA.MachineId));
            Assert.True(active >= 1L);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Production_end_allocates_multiple_destinations_of_same_class_deterministically()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true, includeReturn02: true);
        try
        {
            // Set up Machine A with 2 ALT full containers and 1 main full container
            var slots = await ThreeSlotsAsync(setup.SceneA.LayoutId);
            var fallbackContainer = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");

            await MapOccupiedAsync(slots[0], setup.SceneA.MainPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(slots[1], setup.SceneA.AltPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(slots[2], setup.SceneA.AltPart, "FULL", fallbackContainer);

            await fixture.ExecuteAsync("""
                INSERT INTO master.production_end_priorities (operation_code, priority)
                VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
                ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
                """);

            using var client = await fixture.LoginClientAsync(setup.SceneA.Operator);
            await StartAsync(client, setup.SceneA);

            var sessionId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @machine AND status = 'ACTIVE'",
                ("machine", setup.SceneA.MachineId));

            var end = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/machines/{setup.SceneA.MachineId}/production-sessions/{sessionId}/end");
            end.Headers.Add(ApiHeaders.IdempotencyKey, "prod-end-multi-alt");
            var ended = await client.SendAsync(end);
            var endedBody = await ended.Content.ReadAsStringAsync();
            Assert.True(ended.IsSuccessStatusCode, endedBody);

            // Verify that 2 ALT return requests received distinct business codes: RETURN-01 and RETURN-02
            var returnCodes = new List<string>();
            await using var conn = new NpgsqlConnection(fixture.ConnectionString);
            await conn.OpenAsync();
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT r.destination_business_code
                FROM ops.transport_requests r
                JOIN master.task_types tt ON tt.id = r.task_type_id
                WHERE tt.code = 'REMOVE_FULL' AND r.part_id = @alt
                ORDER BY r.created_at
                """, conn))
            {
                cmd.Parameters.AddWithValue("alt", setup.SceneA.AltPart);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    returnCodes.Add(reader.GetString(0));
                }
            }

            Assert.Equal(2, returnCodes.Count);
            Assert.NotEqual(returnCodes[0], returnCodes[1]);
            Assert.Contains("RETURN-01", returnCodes);
            Assert.Contains("RETURN-02", returnCodes);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Production_end_fails_atomically_when_insufficient_capacity_exists()
    {
        // Only 1 return destination exists (RETURN-01), but production end has 2 ALT containers
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true, includeReturn02: false);
        try
        {
            var slots = await ThreeSlotsAsync(setup.SceneA.LayoutId);
            var fallbackContainer = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");

            await MapOccupiedAsync(slots[0], setup.SceneA.MainPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(slots[1], setup.SceneA.AltPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(slots[2], setup.SceneA.AltPart, "FULL", fallbackContainer);

            await fixture.ExecuteAsync("""
                INSERT INTO master.production_end_priorities (operation_code, priority)
                VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
                ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
                """);

            using var client = await fixture.LoginClientAsync(setup.SceneA.Operator);
            await StartAsync(client, setup.SceneA);

            var sessionId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @machine AND status = 'ACTIVE'",
                ("machine", setup.SceneA.MachineId));

            var end = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/machines/{setup.SceneA.MachineId}/production-sessions/{sessionId}/end");
            end.Headers.Add(ApiHeaders.IdempotencyKey, "prod-end-fail-atomic");
            var ended = await client.SendAsync(end);
            var endedBody = await ended.Content.ReadAsStringAsync();

            Assert.False(ended.IsSuccessStatusCode);
            Assert.Contains("NON_FINISHED_RETURN_CAPACITY_UNAVAILABLE", endedBody, StringComparison.Ordinal);
            Assert.Contains("Uygun boş alt parça dönüş alanı bulunamadı", endedBody, StringComparison.Ordinal);

            // Atomicity check: 0 transport requests created, session still ACTIVE
            var count = await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests");
            Assert.Equal(0L, count);

            var sessionStatus = await fixture.ScalarAsync<string>(
                "SELECT status FROM ops.production_sessions WHERE id = @id", ("id", sessionId));
            Assert.Equal("ACTIVE", sessionStatus);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Reservation_is_released_when_request_is_cancelled()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        try
        {
            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            // Request A claims FG-01
            var resA = await clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "cancel-test-a"));
            Assert.True(resA.IsSuccessStatusCode);

            // Request B fails because FG-01 is occupied
            var resB = await clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "cancel-test-b"));
            Assert.False(resB.IsSuccessStatusCode);
            Assert.Contains("FINISHED_GOODS_STORE_FULL", await resB.Content.ReadAsStringAsync());

            // Transition Request A to CANCELLED
            var reqAId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneA.MachineId));
            await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
            await store.RecordExecutionOutcomeAsync(
                reqAId,
                new ExecutionOutcome(
                    Status: TransportRequestStatus.Cancelled,
                    FailureCode: null,
                    FailureMessage: "Cancelled for test",
                    EvidenceType: null));

            // Now Request C from Machine B should succeed and reuse FG-01
            var resC = await clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "cancel-test-c"));
            var bodyC = await resC.Content.ReadAsStringAsync();
            Assert.True(resC.IsSuccessStatusCode, bodyC);

            var destC = await fixture.ScalarAsync<string>(
                "SELECT destination_business_code FROM ops.transport_requests WHERE machine_id = @m AND status IN ('CREATED','QUEUED','SENT_TO_RDS','RUNNING')",
                ("m", setup.SceneB.MachineId));
            Assert.Equal("FG-01", destC);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Post_dispatch_failure_holds_endpoint_until_operator_reconciliation()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        try
        {
            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            // Request A claims FG-01
            var resA = await clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "fail-test-a"));
            Assert.True(resA.IsSuccessStatusCode);

            // Request B fails because FG-01 is occupied
            var resB = await clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "fail-test-b"));
            Assert.False(resB.IsSuccessStatusCode);
            Assert.Contains("FINISHED_GOODS_STORE_FULL", await resB.Content.ReadAsStringAsync());

            // Transition Request A to FAILED
            var reqAId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneA.MachineId));
            await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
            await store.RecordExecutionTransitionAsync(reqAId, TransportRequestStatus.SentToRds);
            await store.RecordExecutionOutcomeAsync(
                reqAId,
                new ExecutionOutcome(
                    Status: TransportRequestStatus.Failed,
                    FailureCode: "ROBOT_TIMEOUT",
                    FailureMessage: "Failed for test",
                    EvidenceType: null));

            // The robot had already accepted the task, so failure does not
            // prove where the pallet is. FG-01 stays held and both transport
            // endpoints become UNKNOWN until a physical reconciliation.
            var resC = await clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "fail-test-c"));
            var bodyC = await resC.Content.ReadAsStringAsync();
            Assert.False(resC.IsSuccessStatusCode);
            Assert.Contains("FINISHED_GOODS_STORE_FULL", bodyC);
            Assert.Equal(1L, await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.store_reservations WHERE transport_request_id = @id AND released_at IS NULL AND reconciliation_required",
                ("id", reqAId)));
            Assert.Equal(1L, await fixture.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM ops.slot_runtime_state s
                JOIN ops.transport_requests r
                  ON s.layout_slot_id IN (r.source_slot_id, r.destination_slot_id)
                WHERE r.id = @id AND s.occupancy_state = 'UNKNOWN' AND s.load_state = 'UNKNOWN'
                """,
                ("id", reqAId)));

            var locationId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.store_locations WHERE code = 'FG-01'");
            var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.FinishedGoodsWarehouseOperator);
            using var warehouseClient = await fixture.LoginClientAsync(warehouse);
            var reconciled = await warehouseClient.PostAsync(
                $"/api/v1/warehouse/store/locations/{locationId}/clear", null);
            Assert.True(reconciled.IsSuccessStatusCode, await reconciled.Content.ReadAsStringAsync());
            Assert.Equal(1L, await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.store_reservations WHERE transport_request_id = @id AND released_at IS NOT NULL AND reconciled_at IS NOT NULL",
                ("id", reqAId)));

            var resD = await clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "fail-test-d"));
            Assert.True(resD.IsSuccessStatusCode, await resD.Content.ReadAsStringAsync());
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Reservation_is_released_when_request_is_completed()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        try
        {
            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            // Request A claims FG-01
            var resA = await clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "comp-test-a"));
            Assert.True(resA.IsSuccessStatusCode);

            // Transition Request A to COMPLETED
            var reqAId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneA.MachineId));
            await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
            await store.RecordExecutionTransitionAsync(reqAId, TransportRequestStatus.SentToRds);
            await store.RecordExecutionTransitionAsync(reqAId, TransportRequestStatus.Running);
            await store.RecordExecutionTransitionAsync(reqAId, TransportRequestStatus.Completed);

            // Completion releases the transport reservation, but the delivered
            // pallet keeps the single physical position occupied.
            var resB = await clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "comp-test-b"));
            var bodyB = await resB.Content.ReadAsStringAsync();
            Assert.False(resB.IsSuccessStatusCode);
            Assert.Contains("FINISHED_GOODS_STORE_FULL", bodyB);
            Assert.Equal(0L, await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.store_reservations WHERE released_at IS NULL"));
            Assert.Equal(1L, await fixture.ScalarAsync<long>("""
                SELECT count(*) FROM ops.store_units u
                JOIN ops.store_locations sl ON sl.id = u.store_location_id
                WHERE sl.code = 'FG-01' AND u.removed_at IS NULL AND u.load_state = 'FULL'
                """));
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Physical_rds_resolution_preserves_navigation_profile_isolation()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true);
        try
        {
            // Sequential usage of FG-02 by Machine A, then Machine B
            var reqA = await fixture.ScalarAsync<Guid>(
                """
                INSERT INTO ops.transport_requests
                    (request_origin, requested_by, machine_id, variant_id, layout_id, navigation_id,
                     task_type_id, status, priority, destination_business_code)
                SELECT 'API', @user, @machine, @variant, @layout, @nav,
                       (SELECT id FROM master.task_types WHERE code = 'REMOVE_FULL'), 'CREATED', 10, 'FG-02'
                RETURNING id
                """,
                ("user", setup.SceneA.Operator.UserId),
                ("machine", setup.SceneA.MachineId),
                ("variant", setup.SceneA.VariantId),
                ("layout", setup.SceneA.LayoutId),
                ("nav", setup.SceneA.NavigationId));

            var reqB = await fixture.ScalarAsync<Guid>(
                """
                INSERT INTO ops.transport_requests
                    (request_origin, requested_by, machine_id, variant_id, layout_id, navigation_id,
                     task_type_id, status, priority, destination_business_code)
                SELECT 'API', @user, @machine, @variant, @layout, @nav,
                       (SELECT id FROM master.task_types WHERE code = 'REMOVE_FULL'), 'CREATED', 10, 'FG-02'
                RETURNING id
                """,
                ("user", setup.SceneB.Operator.UserId),
                ("machine", setup.SceneB.MachineId),
                ("variant", setup.SceneB.VariantId),
                ("layout", setup.SceneB.LayoutId),
                ("nav", setup.SceneB.NavigationId));

            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var execution = new PostgresRdsExecutionStore(dataSource);

            var ctxA = await execution.GetDispatchContextAsync(reqA);
            var ctxB = await execution.GetDispatchContextAsync(reqB);

            Assert.NotNull(ctxA);
            Assert.NotNull(ctxB);

            // A resolves SH01A WorkSite; B resolves PAH01A WorkSite
            Assert.Equal("TKG_SH01A_V01_WS_FG02", ctxA.Destination.SiteId);
            Assert.Equal("TKG_PAH01A_V01_WS_FG02", ctxB.Destination.SiteId);
            Assert.DoesNotContain("PAH01A", ctxA.Destination.SiteId, StringComparison.Ordinal);
            Assert.DoesNotContain("SH01A", ctxB.Destination.SiteId, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Temporary_runtime_occupancy_does_not_break_configuration_commissioning_readiness()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        try
        {
            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            await StartAsync(clientA, setup.SceneA);

            // Create transport occupying FG-01
            var resA = await clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "readiness-test"));
            Assert.True(resA.IsSuccessStatusCode);

            // Commissioning readiness evaluation evaluates configuration mappings
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var commissioningStore = new PostgresRdsIntegrationStore(dataSource);

            var report = await commissioningStore.ListVariantCommissioningAsync();
            var shReport = report.Single(r => r.MachineCode == "SH-01-A" && r.VariantCode == "1");

            var fgOp = shReport.Operations.Single(o => o.Operation == "REMOVE_FULL_MAMUL");
            Assert.DoesNotContain("FINISHED_GOODS_MAPPING_MISSING", fgOp.Blockers);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Rds_resolution_isolates_worksite_per_navigation_profile_for_canonical_return()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true, includeReturn02: true);
        try
        {
            var reqA = await fixture.ScalarAsync<Guid>(
                """
                INSERT INTO ops.transport_requests
                    (request_origin, requested_by, machine_id, variant_id, layout_id, navigation_id,
                     task_type_id, status, priority, destination_business_code)
                SELECT 'API', @user, @machine, @variant, @layout, @nav,
                       (SELECT id FROM master.task_types WHERE code = 'REMOVE_FULL'), 'CREATED', 10, 'RETURN-01'
                RETURNING id
                """,
                ("user", setup.SceneA.Operator.UserId),
                ("machine", setup.SceneA.MachineId),
                ("variant", setup.SceneA.VariantId),
                ("layout", setup.SceneA.LayoutId),
                ("nav", setup.SceneA.NavigationId));

            var reqB = await fixture.ScalarAsync<Guid>(
                """
                INSERT INTO ops.transport_requests
                    (request_origin, requested_by, machine_id, variant_id, layout_id, navigation_id,
                     task_type_id, status, priority, destination_business_code)
                SELECT 'API', @user, @machine, @variant, @layout, @nav,
                       (SELECT id FROM master.task_types WHERE code = 'REMOVE_FULL'), 'CREATED', 10, 'RETURN-01'
                RETURNING id
                """,
                ("user", setup.SceneB.Operator.UserId),
                ("machine", setup.SceneB.MachineId),
                ("variant", setup.SceneB.VariantId),
                ("layout", setup.SceneB.LayoutId),
                ("nav", setup.SceneB.NavigationId));

            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var execution = new PostgresRdsExecutionStore(dataSource);

            var ctxA = await execution.GetDispatchContextAsync(reqA);
            var ctxB = await execution.GetDispatchContextAsync(reqB);

            Assert.NotNull(ctxA);
            Assert.NotNull(ctxB);

            // Both have destination_business_code = 'RETURN-01', but resolve distinct profile-local WorkSites
            Assert.Equal("TKG_SH01A_V01_WS_RETURN01", ctxA.Destination.SiteId);
            Assert.Equal("TKG_PAH01A_V01_WS_RETURN01", ctxB.Destination.SiteId);
            Assert.DoesNotContain("PAH01A", ctxA.Destination.SiteId, StringComparison.Ordinal);
            Assert.DoesNotContain("SH01A", ctxB.Destination.SiteId, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Real_commissioning_binds_cross_profile_return_and_empty_with_canonical_identities()
    {
        var setup = await PrepareTwoMachineScenesUnboundAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var commissioningStore = new PostgresRdsIntegrationStore(dataSource);
            var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
            var context = new AdminMutationContext(admin.UserId, Guid.NewGuid(), "127.0.0.1", "comm-test");

            // 1. Cross-profile NON_FINISHED_RETURN binding with canonical RETURN-01
            var bindA = await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-01", "TKG_SH01A_V01_WS_RETURN01", true, context);
            var bindB = await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-01", "TKG_PAH01A_V01_WS_RETURN01", true, context);

            Assert.True(bindA.Applied, $"Binding A failed: {string.Join(", ", bindA.Blockers)}");
            Assert.True(bindB.Applied, $"Binding B failed: {string.Join(", ", bindB.Blockers)}");
            Assert.Empty(bindA.Blockers);
            Assert.Empty(bindB.Blockers);
            Assert.Equal("RETURN-01", bindA.BusinessCode);
            Assert.Equal("RETURN-01", bindB.BusinessCode);
            Assert.NotNull(bindA.ResolvedSiteId);
            Assert.NotNull(bindB.ResolvedSiteId);
            Assert.Equal("TKG_SH01A_V01_WS_RETURN01", bindA.ResolvedSiteId);
            Assert.Equal("TKG_PAH01A_V01_WS_RETURN01", bindB.ResolvedSiteId);
            Assert.NotEqual(bindA.ResolvedSiteId, bindB.ResolvedSiteId);

            var siteA = await fixture.ScalarAsync<Guid>(
                "SELECT rds_worksite_id FROM integration.profile_shared_endpoint_bindings WHERE navigation_profile_id = @nav AND endpoint_class = 'NON_FINISHED_RETURN'",
                ("nav", setup.SceneA.NavigationId));
            var siteB = await fixture.ScalarAsync<Guid>(
                "SELECT rds_worksite_id FROM integration.profile_shared_endpoint_bindings WHERE navigation_profile_id = @nav AND endpoint_class = 'NON_FINISHED_RETURN'",
                ("nav", setup.SceneB.NavigationId));
            Assert.NotEqual(siteA, siteB);

            // 2. Cross-profile EMPTY_RETURN binding with canonical EMPTY-01
            var emptyRetA = await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_RETURN", "EMPTY-01", "TKG_SH01A_V01_WS_EMPTY01", true, context);
            var emptyRetB = await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "EMPTY_RETURN", "EMPTY-01", "TKG_PAH01A_V01_WS_EMPTY01", true, context);

            Assert.True(emptyRetA.Applied);
            Assert.True(emptyRetB.Applied);
            Assert.Equal("EMPTY-01", emptyRetA.BusinessCode);
            Assert.Equal("EMPTY-01", emptyRetB.BusinessCode);
            Assert.NotNull(emptyRetA.ResolvedSiteId);
            Assert.NotNull(emptyRetB.ResolvedSiteId);
            Assert.NotEqual(emptyRetA.ResolvedSiteId, emptyRetB.ResolvedSiteId);

            // 3. Cross-profile EMPTY_SOURCE binding with canonical EMPTY-01
            var emptySrcA = await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_SH01A_V01_WS_EMPTY01", true, context);
            var emptySrcB = await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_PAH01A_V01_WS_EMPTY01", true, context);

            Assert.True(emptySrcA.Applied);
            Assert.True(emptySrcB.Applied);
            Assert.Equal("EMPTY-01", emptySrcA.BusinessCode);
            Assert.Equal("EMPTY-01", emptySrcB.BusinessCode);

            // 4. Verify profile mismatch rejection via DB trigger
            var v1 = setup.SceneA.NavigationId;
            var v2ReturnSite = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM integration.rds_worksites WHERE rds_site_id = 'TKG_PAH01A_V01_WS_RETURN01'");
            var mismatchEx = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
                INSERT INTO integration.profile_shared_endpoint_bindings
                    (navigation_profile_id, endpoint_class, business_code, rds_worksite_id)
                VALUES (@profile, 'NON_FINISHED_RETURN', 'RETURN-01', @site)
                """, ("profile", v1), ("site", v2ReturnSite)));
            Assert.Contains("PROFILE_MISMATCH", mismatchEx.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Real_commissioning_rejects_mismatched_ordinals()
    {
        var setup = await PrepareTwoMachineScenesUnboundAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var commissioningStore = new PostgresRdsIntegrationStore(dataSource);
            var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
            var context = new AdminMutationContext(admin.UserId, Guid.NewGuid(), "127.0.0.1", "ordinal-test");

            // RETURN-02 passed with planned WS_RETURN01
            var wrongReturn = await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-02", "TKG_SH01A_V01_WS_RETURN01", true, context);
            Assert.False(wrongReturn.Applied);
            Assert.Contains("RDS_WORKSITE_PLAN_NAME_MISMATCH", wrongReturn.Blockers);

            // EMPTY-03 passed with planned WS_EMPTY02 (ordinal 2)
            var wrongEmpty = await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_RETURN", "EMPTY-03", "TKG_SH01A_V01_WS_EMPTY02", true, context);
            Assert.False(wrongEmpty.Applied);
            Assert.Contains("RDS_WORKSITE_PLAN_NAME_MISMATCH", wrongEmpty.Blockers);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Real_commissioning_return_cross_profile_concurrency_rejects_second_request()
    {
        var setup = await PrepareTwoMachineScenesUnboundAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var commissioningStore = new PostgresRdsIntegrationStore(dataSource);
            var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
            var context = new AdminMutationContext(admin.UserId, Guid.NewGuid(), "127.0.0.1", "ret-conc-test");

            // Real commissioning bindings for single RETURN-01 across NAV profiles
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-01", "TKG_SH01A_V01_WS_RETURN01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-01", "TKG_PAH01A_V01_WS_RETURN01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_SH01A_V01_WS_EMPTY01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_PAH01A_V01_WS_EMPTY01", true, context);

            // Map slots for AltPart and FULL
            var fallbackContainer = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
            await MapOccupiedAsync(setup.SceneA.Slot, setup.SceneA.AltPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(setup.SceneB.Slot, setup.SceneB.AltPart, "FULL", fallbackContainer);

            await fixture.ExecuteAsync("""
                INSERT INTO master.production_end_priorities (operation_code, priority)
                VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
                ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
                """);

            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            var sessionAId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @m AND status = 'ACTIVE'",
                ("m", setup.SceneA.MachineId));
            var sessionBId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @m AND status = 'ACTIVE'",
                ("m", setup.SceneB.MachineId));

            var endA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.SceneA.MachineId}/production-sessions/{sessionAId}/end");
            endA.Headers.Add(ApiHeaders.IdempotencyKey, "ret-conc-a");
            var endB = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.SceneB.MachineId}/production-sessions/{sessionBId}/end");
            endB.Headers.Add(ApiHeaders.IdempotencyKey, "ret-conc-b");

            // Concurrently request removal of full alt container (NON_FINISHED_RETURN)
            var taskA = clientA.SendAsync(endA);
            var taskB = clientB.SendAsync(endB);

            await Task.WhenAll(taskA, taskB);

            var respA = await taskA;
            var respB = await taskB;

            var responses = new[] { respA, respB };
            Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
            Assert.Equal(1, responses.Count(r => !r.IsSuccessStatusCode));

            var failedResp = responses.Single(r => !r.IsSuccessStatusCode);
            var failedBody = await failedResp.Content.ReadAsStringAsync();
            Assert.Contains("NON_FINISHED_RETURN_CAPACITY_UNAVAILABLE", failedBody, StringComparison.Ordinal);
            Assert.Contains("Uygun boş alt parça dönüş alanı bulunamadı", failedBody, StringComparison.Ordinal);

            // Exactly one active request owns RETURN-01 globally
            var activeReturnCount = await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_requests WHERE destination_business_code = 'RETURN-01' AND status IN ('CREATED','QUEUED','SENT_TO_RDS','RUNNING')");
            Assert.Equal(1L, activeReturnCount);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Real_commissioning_multiple_return_capacity_allocates_both_deterministically()
    {
        var setup = await PrepareTwoMachineScenesUnboundAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var commissioningStore = new PostgresRdsIntegrationStore(dataSource);
            var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
            var context = new AdminMutationContext(admin.UserId, Guid.NewGuid(), "127.0.0.1", "multi-ret-test");

            // Real commissioning bindings for RETURN-01 and RETURN-02 on both NAV profiles
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-01", "TKG_SH01A_V01_WS_RETURN01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-02", "TKG_SH01A_V01_WS_RETURN02", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-01", "TKG_PAH01A_V01_WS_RETURN01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "NON_FINISHED_RETURN", "RETURN-02", "TKG_PAH01A_V01_WS_RETURN02", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_SH01A_V01_WS_EMPTY01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_PAH01A_V01_WS_EMPTY01", true, context);

            var fallbackContainer = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
            await MapOccupiedAsync(setup.SceneA.Slot, setup.SceneA.AltPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(setup.SceneB.Slot, setup.SceneB.AltPart, "FULL", fallbackContainer);

            await fixture.ExecuteAsync("""
                INSERT INTO master.production_end_priorities (operation_code, priority)
                VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
                ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
                """);

            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            var sessionAId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @m AND status = 'ACTIVE'",
                ("m", setup.SceneA.MachineId));
            var sessionBId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @m AND status = 'ACTIVE'",
                ("m", setup.SceneB.MachineId));

            var endA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.SceneA.MachineId}/production-sessions/{sessionAId}/end");
            endA.Headers.Add(ApiHeaders.IdempotencyKey, "multi-ret-a");
            var endB = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.SceneB.MachineId}/production-sessions/{sessionBId}/end");
            endB.Headers.Add(ApiHeaders.IdempotencyKey, "multi-ret-b");

            var taskA = clientA.SendAsync(endA);
            var taskB = clientB.SendAsync(endB);

            await Task.WhenAll(taskA, taskB);

            var respA = await taskA;
            var respB = await taskB;

            Assert.True(respA.IsSuccessStatusCode, await respA.Content.ReadAsStringAsync());
            Assert.True(respB.IsSuccessStatusCode, await respB.Content.ReadAsStringAsync());

            var destA = await fixture.ScalarAsync<string>(
                "SELECT destination_business_code FROM ops.transport_requests WHERE machine_id = @m",
                ("m", setup.SceneA.MachineId));
            var destB = await fixture.ScalarAsync<string>(
                "SELECT destination_business_code FROM ops.transport_requests WHERE machine_id = @m",
                ("m", setup.SceneB.MachineId));

            Assert.NotNull(destA);
            Assert.NotNull(destB);
            Assert.NotEqual(destA, destB);

            var allocatedCodes = new HashSet<string> { destA, destB };
            Assert.Contains("RETURN-01", allocatedCodes);
            Assert.Contains("RETURN-02", allocatedCodes);

            // Assert each resolves WorkSite under its own NAV
            var execution = new PostgresRdsExecutionStore(dataSource);
            var reqA = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneA.MachineId));
            var reqB = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests WHERE machine_id = @m", ("m", setup.SceneB.MachineId));
            var ctxA = await execution.GetDispatchContextAsync(reqA);
            var ctxB = await execution.GetDispatchContextAsync(reqB);

            Assert.StartsWith("TKG_SH01A_V01_WS_RETURN", ctxA!.Destination.SiteId, StringComparison.Ordinal);
            Assert.StartsWith("TKG_PAH01A_V01_WS_RETURN", ctxB!.Destination.SiteId, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Real_commissioning_empty_return_cross_profile_concurrency_rejects_second_request()
    {
        var setup = await PrepareTwoMachineScenesUnboundAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var commissioningStore = new PostgresRdsIntegrationStore(dataSource);
            var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
            var context = new AdminMutationContext(admin.UserId, Guid.NewGuid(), "127.0.0.1", "empty-ret-test");

            // Real commissioning bindings for single EMPTY-01 on both NAV profiles
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_RETURN", "EMPTY-01", "TKG_SH01A_V01_WS_EMPTY01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "EMPTY_RETURN", "EMPTY-01", "TKG_PAH01A_V01_WS_EMPTY01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "SH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_SH01A_V01_WS_EMPTY01", true, context);
            await commissioningStore.BindSharedEndpointAsync(
                "PAH-01-A", "1", "EMPTY_SOURCE", "EMPTY-01", "TKG_PAH01A_V01_WS_EMPTY01", true, context);

            // Set slots to OCCUPIED and EMPTY
            var fallbackContainer = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
            await MapOccupiedAsync(setup.SceneA.Slot, setup.SceneA.MainPart, "EMPTY", fallbackContainer);
            await MapOccupiedAsync(setup.SceneB.Slot, setup.SceneB.MainPart, "EMPTY", fallbackContainer);

            await fixture.ExecuteAsync("""
                INSERT INTO master.production_end_priorities (operation_code, priority)
                VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
                ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
                """);

            using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
            using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);

            await StartAsync(clientA, setup.SceneA);
            await StartAsync(clientB, setup.SceneB);

            var sessionAId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @m AND status = 'ACTIVE'",
                ("m", setup.SceneA.MachineId));
            var sessionBId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @m AND status = 'ACTIVE'",
                ("m", setup.SceneB.MachineId));

            var endA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.SceneA.MachineId}/production-sessions/{sessionAId}/end");
            endA.Headers.Add(ApiHeaders.IdempotencyKey, "empty-conc-a");
            var endB = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.SceneB.MachineId}/production-sessions/{sessionBId}/end");
            endB.Headers.Add(ApiHeaders.IdempotencyKey, "empty-conc-b");

            // Concurrently request removal of empty container (EMPTY_RETURN)
            var taskA = clientA.SendAsync(endA);
            var taskB = clientB.SendAsync(endB);

            await Task.WhenAll(taskA, taskB);

            var respA = await taskA;
            var respB = await taskB;

            var responses = new[] { respA, respB };
            Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
            Assert.Equal(1, responses.Count(r => !r.IsSuccessStatusCode));

            var failedResp = responses.Single(r => !r.IsSuccessStatusCode);
            var failedBody = await failedResp.Content.ReadAsStringAsync();
            Assert.Contains("EMPTY_RETURN_CAPACITY_UNAVAILABLE", failedBody, StringComparison.Ordinal);
            Assert.Contains("Uygun boş kasa dönüş alanı bulunamadı", failedBody, StringComparison.Ordinal);

            // Exactly 1 active request holds EMPTY-01
            var totalActive = await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_requests WHERE destination_business_code = 'EMPTY-01' AND status IN ('CREATED','QUEUED','SENT_TO_RDS','RUNNING')");
            Assert.Equal(1L, totalActive);
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    // Helper setups

    [Theory]
    [InlineData("store_disabled", 1, "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED")]
    [InlineData("filled", 1, "FINISHED_GOODS_CAPACITY_UNAVAILABLE")]
    [InlineData("filled", null, "RDS_WORKSITE_STATE_UNKNOWN")]
    [InlineData("filled", 2, "RDS_WORKSITE_STATE_UNKNOWN")]
    [InlineData("locked", 1, "RDS_WORKSITE_LOCKED")]
    [InlineData("locked", null, "RDS_WORKSITE_STATE_UNKNOWN")]
    [InlineData("disabled", 1, "RDS_WORKSITE_DISABLED")]
    [InlineData("sync_failed", 1, "RDS_WORKSITE_SYNC_FAILED")]
    [InlineData("never_synced", null, "RDS_WORKSITE_STALE")]
    [InlineData("stale", null, "RDS_WORKSITE_STALE")]
    public async Task Physical_fg_gate_agrees_between_mamul_projection_and_creation(string field, int? value, string code)
    {
        bool? originalStoreEnabled = null;
        try
        {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        var scene = setup.SceneA;
        if (field == "store_disabled") originalStoreEnabled = await fixture.ScalarAsync<bool>("SELECT is_enabled FROM ops.store_locations WHERE code = 'FG-01'");
        using var client = await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client, scene);
        var assignment = field switch
        {
            "never_synced" => "last_sync_at = NULL",
            "stale" => "last_sync_at = now() - interval '1 hour'",
            _ => $"last_{field} = {(value.HasValue ? value.Value.ToString() : "NULL")}"
        };
        if (field == "store_disabled") await fixture.ExecuteAsync("UPDATE ops.store_locations SET is_enabled = false WHERE code = 'FG-01'");
        else await fixture.ExecuteAsync($"UPDATE integration.rds_worksites SET {assignment} WHERE navigation_profile_id = @nav AND rds_site_id LIKE '%WS_FG%'", ("nav", scene.NavigationId));
        var positions = await client.GetFromJsonAsync<ContainerPositionRecord[]>(
            $"/api/v1/machines/{scene.MachineId}/container-positions?variantId={scene.VariantId}&layoutId={scene.LayoutId}&productGroupId={scene.Group}&partId={scene.MainPart}");
        Assert.False(Assert.Single(positions!).FullAction.Enabled);
        Assert.Equal(code, positions![0].FullAction.DisabledCode);
        var response = await client.SendAsync(CreateFullContainerActionRequest(scene, "physical-fg"));
        Assert.False(response.IsSuccessStatusCode);
        var errorBody = await response.Content.ReadAsStringAsync();
        Assert.True(errorBody.Contains(code, StringComparison.Ordinal), errorBody);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        }
        finally
        {
            if (field == "store_disabled" && originalStoreEnabled.HasValue)
                await fixture.ExecuteAsync("UPDATE ops.store_locations SET is_enabled = @enabled WHERE code = 'FG-01'", ("enabled", originalStoreEnabled.Value));
        }
    }

    [Fact]
    public async Task Conflicting_current_cross_nav_physical_occupancy_blocks_both_allocators()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_filled = 1 WHERE navigation_profile_id = @nav AND rds_site_id LIKE '%WS_FG%'", ("nav", setup.SceneB.NavigationId));
        using var clientA = await fixture.LoginClientAsync(setup.SceneA.Operator);
        using var clientB = await fixture.LoginClientAsync(setup.SceneB.Operator);
        await StartAsync(clientA, setup.SceneA);
        await StartAsync(clientB, setup.SceneB);
        var responses = await Task.WhenAll(clientA.SendAsync(CreateFullContainerActionRequest(setup.SceneA, "contradict-a")),
            clientB.SendAsync(CreateFullContainerActionRequest(setup.SceneB, "contradict-b")));
        foreach (var response in responses)
        {
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains("FINISHED_GOODS_CAPACITY_UNAVAILABLE", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Theory]
    [InlineData("FINISHED_GOODS", "FG-", "FULL", false)]
    [InlineData("NON_FINISHED_RETURN", "RETURN-", "FULL", false)]
    [InlineData("EMPTY_RETURN", "EMPTY-", "EMPTY", false)]
    [InlineData("FINISHED_GOODS", "FG-", "FULL", true)]
    [InlineData("NON_FINISHED_RETURN", "RETURN-", "FULL", true)]
    [InlineData("EMPTY_RETURN", "EMPTY-", "EMPTY", true)]
    public async Task End_skips_physically_filled_destination_or_atomically_rejects_all_filled(string endpointClass, string prefix, string load, bool allFilled)
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: true, includeReturn02: true);
        var scene = setup.SceneA;
        if (endpointClass == "EMPTY_RETURN")
            await fixture.ExecuteAsync("""
                INSERT INTO integration.profile_shared_endpoint_bindings(navigation_profile_id, endpoint_class, business_code, rds_worksite_id)
                SELECT n.id, 'EMPTY_RETURN', 'EMPTY-02', w.id
                FROM master.navigation_profiles n JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'EMPTY' AND p.ordinal = 2
                JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
                WHERE n.id = @nav
                """, ("nav", scene.NavigationId));
        var container = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await MapOccupiedAsync(scene.Slot, endpointClass == "NON_FINISHED_RETURN" ? scene.AltPart : scene.MainPart, load, container);
        await fixture.ExecuteAsync("""
            UPDATE integration.rds_worksites w SET last_filled = 1
            FROM integration.profile_shared_endpoint_bindings b WHERE w.id = b.rds_worksite_id
            AND b.navigation_profile_id = @nav AND b.endpoint_class = @class AND (@all OR b.business_code = @first)
            """, ("nav", scene.NavigationId), ("class", endpointClass), ("all", allFilled), ("first", prefix + "01"));
        await fixture.ExecuteAsync("INSERT INTO master.production_end_priorities(operation_code,priority) VALUES ('REMOVE_FULL',40),('REMOVE_EMPTY',70) ON CONFLICT(operation_code) DO UPDATE SET priority=EXCLUDED.priority");
        using var client = await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client, scene);
        var session = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.production_sessions WHERE machine_id=@m AND status='ACTIVE'", ("m",scene.MachineId));
        var end = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{scene.MachineId}/production-sessions/{session}/end");
        end.Headers.Add(ApiHeaders.IdempotencyKey,"physical-end");
        var response = await client.SendAsync(end);
        if (allFilled)
        {
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains(endpointClass + "_CAPACITY_UNAVAILABLE", await response.Content.ReadAsStringAsync());
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
            Assert.Equal("ACTIVE", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_sessions WHERE id=@id",("id",session)));
        }
        else
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.Equal(prefix+"02", await fixture.ScalarAsync<string>("SELECT destination_business_code FROM ops.transport_requests WHERE machine_id=@m",("m",scene.MachineId)));
        }
    }

    [Fact]
    public async Task End_three_finished_pallets_requires_three_physically_available_endpoints_before_creating_children()
    {
        try
        {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02:true);
        var scene=setup.SceneA;
        var slots=await ThreeSlotsAsync(scene.LayoutId);
        var container=await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        foreach(var slot in slots) await MapOccupiedAsync(slot,scene.MainPart,"FULL",container);
        // Three mappings, but only two physical vacancies.
        await fixture.ExecuteAsync("""
            INSERT INTO integration.rds_worksites(navigation_profile_id,rds_site_id,rds_type,last_filled,last_locked,last_disabled,last_sync_failed,last_sync_at,raw_state_json)
            VALUES(@nav,'ISOLATED-FG03',1,1,0,0,0,now(),'{"filled":1}'::jsonb);
            INSERT INTO integration.profile_shared_endpoint_bindings(navigation_profile_id,endpoint_class,business_code,rds_worksite_id,store_location_id)
            SELECT @nav,'FINISHED_GOODS','FG-03',w.id,l.id FROM integration.rds_worksites w CROSS JOIN ops.store_locations l
            WHERE w.navigation_profile_id=@nav AND w.rds_site_id='ISOLATED-FG03' AND l.code='FG-03';
            INSERT INTO master.production_end_priorities(operation_code,priority) VALUES('REMOVE_FULL',40) ON CONFLICT(operation_code) DO UPDATE SET priority=40;
            """,("nav",scene.NavigationId));
        using var client=await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client,scene);
        var session=await fixture.ScalarAsync<Guid>("SELECT id FROM ops.production_sessions WHERE machine_id=@m AND status='ACTIVE'",("m",scene.MachineId));
        var end=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/machines/{scene.MachineId}/production-sessions/{session}/end");
        end.Headers.Add(ApiHeaders.IdempotencyKey,"three-physical");
        var response=await client.SendAsync(end);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("FINISHED_GOODS_CAPACITY_UNAVAILABLE",await response.Content.ReadAsStringAsync());
        Assert.Equal(0L,await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L,await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.production_end_runs"));
        Assert.Equal("ACTIVE",await fixture.ScalarAsync<string>("SELECT status FROM ops.production_sessions WHERE id=@id",("id",session)));
        }
        finally { await RemoveIsolatedPhysicalFixturesAsync(); }
    }

    [Theory]
    [InlineData(0,"PHYSICAL_STATE_MISMATCH","RDS_WORKSITE_OCCUPANCY_CONFLICT")]
    [InlineData(1,"CONSISTENT",null)]
    [InlineData(null,"PHYSICAL_STATE_UNKNOWN","RDS_WORKSITE_STATE_UNKNOWN")]
    public async Task Mapped_slot_reconciliation_is_read_only_and_blocks_contradiction(int? filled,string consistency,string? code)
    {
        try
        {
        var setup=await PrepareTwoMachineScenesAsync(includeFg02:false);
        var scene=setup.SceneA;
        var before=await fixture.ScalarAsync<string>("SELECT row_to_json(s)::text FROM ops.slot_runtime_state s WHERE layout_slot_id=@slot",("slot",scene.Slot));
        var worksite=await fixture.ScalarAsync<Guid>("""
            INSERT INTO integration.rds_worksites(navigation_profile_id,rds_site_id,rds_type,last_filled,last_locked,last_disabled,last_sync_failed,last_sync_at,raw_state_json)
            VALUES(@nav,'ISOLATED-MACHINE-POSITION',1,@filled,0,0,0,now(),'{"siteName":"Isolated physical position"}'::jsonb) RETURNING id
            """,("nav",scene.NavigationId),("filled",(object?)filled ?? DBNull.Value));
        await fixture.ExecuteAsync("INSERT INTO integration.layout_slot_rds_profile_mapping(layout_slot_id,navigation_profile_id,rds_worksite_id) VALUES(@slot,@nav,@w)",
            ("slot",scene.Slot),("nav",scene.NavigationId),("w",worksite));
        using var client=await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client,scene);
        var admin=await fixture.SeedIdentityAsync(role:RoleCodes.Admin,machineCode:"SH-01-A",bindDeviceToMachine:false);
        using var adminClient=await fixture.AdminLoginClientAsync(admin);
        var report=await adminClient.GetFromJsonAsync<RdsPhysicalDiagnosticRecord[]>($"/api/v1/admin/rds/physical-evidence?navigationProfileId={scene.NavigationId}");
        Assert.Contains(report!, r => r.SlotId is null);
        Assert.All(report!.Where(r => r.SlotId is null), r => Assert.Null(r.BusinessOccupancy));
        var diagnostic=Assert.Single(report!,r=>r.SlotId==scene.Slot);
        Assert.Equal(consistency,diagnostic.ConsistencyResult);
        Assert.Equal("OCCUPIED",diagnostic.BusinessOccupancy);
        Assert.Equal(filled,diagnostic.Evidence.Filled);
        Assert.Equal("Isolated physical position",diagnostic.SiteName);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/v1/admin/rds/physical-evidence?navigationProfileId={scene.NavigationId}")).StatusCode);
        Assert.Equal(before,await fixture.ScalarAsync<string>("SELECT row_to_json(s)::text FROM ops.slot_runtime_state s WHERE layout_slot_id=@slot",("slot",scene.Slot)));
        var response=await client.SendAsync(CreateFullContainerActionRequest(scene,"slot-reconcile"));
        if(code is null) Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        else
        {
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains(code,await response.Content.ReadAsStringAsync());
            Assert.Equal(0L,await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
            Assert.Equal(before,await fixture.ScalarAsync<string>("SELECT row_to_json(s)::text FROM ops.slot_runtime_state s WHERE layout_slot_id=@slot",("slot",scene.Slot)));
        }
        }
        finally { await RemoveIsolatedPhysicalFixturesAsync(); }
    }

    [Fact]
    public async Task Reserved_destination_rechecks_physical_evidence_before_dispatch_including_other_nav()
    {
        var setup=await PrepareTwoMachineScenesAsync(includeFg02:false);
        using var client=await fixture.LoginClientAsync(setup.SceneA.Operator);
        await StartAsync(client,setup.SceneA);
        Assert.True((await client.SendAsync(CreateFullContainerActionRequest(setup.SceneA,"physical-dispatch"))).IsSuccessStatusCode);
        var request=await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests WHERE machine_id=@m",("m",setup.SceneA.MachineId));
        await using var dataSource=NpgsqlDataSource.Create(fixture.ConnectionString);
        var execution=new PostgresRdsExecutionStore(dataSource);
        Assert.Null(await execution.ReadPhysicalDispatchBlockAsync(request));
        await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_filled=1 WHERE navigation_profile_id=@nav AND rds_site_id LIKE '%WS_FG%'",("nav",setup.SceneB.NavigationId));
        Assert.Equal("RDS_WORKSITE_OCCUPANCY_CONFLICT",await execution.ReadPhysicalDispatchBlockAsync(request));
        await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_filled=0 WHERE navigation_profile_id=@nav AND rds_site_id LIKE '%WS_FG%'",("nav",setup.SceneB.NavigationId));
        await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_locked=1 WHERE navigation_profile_id=@nav AND rds_site_id LIKE '%WS_FG%'",("nav",setup.SceneA.NavigationId));
        Assert.Equal("RDS_WORKSITE_LOCKED",await execution.ReadPhysicalDispatchBlockAsync(request));
        Assert.Equal(0L,await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
    }

    [Theory]
    [InlineData(0, false, "RDS_WORKSITE_OCCUPANCY_CONFLICT")]
    [InlineData(1, true, null)]
    [InlineData(null, false, "RDS_WORKSITE_STATE_UNKNOWN")]
    public async Task Empty_source_requires_the_selected_physical_pallet_to_be_present(
        int? filled, bool succeeds, string? expectedError)
    {
        var setup=await PrepareTwoMachineScenesAsync(includeFg02:false);
        var scene=setup.SceneA;
        await fixture.SetSlotAsync(scene.Slot,"VACANT","UNKNOWN");
        await fixture.ExecuteAsync("UPDATE integration.rds_worksites SET last_filled=@filled WHERE navigation_profile_id=@nav AND rds_site_id LIKE '%WS_EMPTY%'",
            ("filled",(object?)filled ?? DBNull.Value),("nav",scene.NavigationId));
        using var client=await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client,scene);
        var action=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/machines/{scene.MachineId}/container-actions")
        { Content=JsonContent.Create(new CreateContainerActionHttpRequest(scene.VariantId,scene.LayoutId,null,scene.Group,scene.MainPart,scene.Position,"EMPTY")) };
        action.Headers.Add(ApiHeaders.IdempotencyKey,"pool-source");
        var response=await client.SendAsync(action);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(succeeds, response.IsSuccessStatusCode);
        if (!succeeds)
        {
            Assert.Contains(expectedError!, body);
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
            return;
        }

        Assert.Equal("BRING_EMPTY",await fixture.ScalarAsync<string>("SELECT tt.code FROM ops.transport_requests r JOIN master.task_types tt ON tt.id=r.task_type_id"));
        Assert.Equal("EMPTY-01", await fixture.ScalarAsync<string>("SELECT source_business_code FROM ops.transport_requests"));
        Assert.Equal("TEST-FG-EMPTY", await fixture.ScalarAsync<string>("""
            SELECT sl.code FROM ops.store_reservations sr
            JOIN ops.store_locations sl ON sl.id = sr.store_location_id
            WHERE sr.released_at IS NULL AND sr.reservation_type = 'PICKUP'
            """));

        var requestId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        await using var workflow = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
        Assert.Equal("ROBOT", await fixture.ScalarAsync<string>(
            "SELECT removal_source FROM ops.store_units WHERE transport_request_id IS NULL AND removed_at IS NOT NULL ORDER BY removed_at DESC LIMIT 1"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_units WHERE removal_source = 'ROBOT' AND removed_by IS NULL AND removed_device_id IS NULL"));
    }

    [Fact]
    public async Task Empty_request_is_rejected_when_the_selected_physical_source_has_no_registered_pallet()
    {
        var setup = await PrepareTwoMachineScenesAsync(includeFg02: false);
        var scene = setup.SceneA;
        await fixture.SetSlotAsync(scene.Slot, "VACANT", "UNKNOWN");
        await fixture.ExecuteAsync("""
            UPDATE integration.rds_worksites
            SET last_filled = 1
            WHERE navigation_profile_id = @nav AND rds_site_id LIKE '%WS_EMPTY%';
            DELETE FROM ops.store_units
            WHERE store_location_id = (SELECT id FROM ops.store_locations WHERE code = 'TEST-FG-EMPTY');
            """, ("nav", scene.NavigationId));
        using var client = await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client, scene);
        var action = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{scene.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                scene.VariantId, scene.LayoutId, null, scene.Group, scene.MainPart, scene.Position, "EMPTY"))
        };
        action.Headers.Add(ApiHeaders.IdempotencyKey, "missing-selected-empty-pallet");

        var response = await client.SendAsync(action);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("FINISHED_GOODS_NO_EMPTY_CONTAINER", body);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_reservations WHERE released_at IS NULL"));
        Assert.True(await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM ops.store_units u
            JOIN ops.store_locations sl ON sl.id = u.store_location_id
            WHERE sl.code LIKE 'TEST-FG-EMPTY-%' AND u.removed_at IS NULL
            """) > 0);
    }

    [Fact]
    public async Task Mixed_disabled_filled_and_reserved_destinations_select_next_free_business_code_deterministically()
    {
        try
        {
        var setup=await PrepareTwoMachineScenesAsync(includeFg02:true);
        var scene=setup.SceneA;
        await fixture.ExecuteAsync("""
            UPDATE integration.rds_worksites w SET last_disabled=CASE WHEN b.business_code='FG-01' THEN 1 ELSE 0 END,
                last_filled=CASE WHEN b.business_code='FG-02' THEN 1 ELSE 0 END
            FROM integration.profile_shared_endpoint_bindings b WHERE w.id=b.rds_worksite_id AND b.navigation_profile_id=@nav AND b.endpoint_class='FINISHED_GOODS';
            INSERT INTO integration.rds_worksites(navigation_profile_id,rds_site_id,rds_type,last_filled,last_locked,last_disabled,last_sync_failed,last_sync_at,raw_state_json)
            VALUES(@nav,'ISOLATED-FG03',1,0,0,0,0,now(),'{"filled":0}'::jsonb),(@nav,'ISOLATED-FG04',1,0,0,0,0,now(),'{"filled":0}'::jsonb);
            INSERT INTO integration.profile_shared_endpoint_bindings(navigation_profile_id,endpoint_class,business_code,rds_worksite_id,store_location_id)
            SELECT @nav,'FINISHED_GOODS',l.code,w.id,l.id FROM integration.rds_worksites w CROSS JOIN ops.store_locations l
            WHERE w.navigation_profile_id=@nav AND ((w.rds_site_id='ISOLATED-FG03' AND l.code='FG-03') OR (w.rds_site_id='ISOLATED-FG04' AND l.code='FG-04'));
            INSERT INTO ops.transport_requests(request_origin,requested_by,machine_id,variant_id,layout_id,navigation_id,task_type_id,status,priority,destination_business_code)
            SELECT 'API',@user,@machine,@variant,@layout,@otherNav,id,'QUEUED',10,'FG-03' FROM master.task_types WHERE code='REMOVE_FULL';
            """,("nav",scene.NavigationId),("user",setup.SceneB.Operator.UserId),("machine",setup.SceneB.MachineId),
            ("variant",setup.SceneB.VariantId),("layout",setup.SceneB.LayoutId),("otherNav",setup.SceneB.NavigationId));
        using var client=await fixture.LoginClientAsync(scene.Operator);
        await StartAsync(client,scene);
        var response=await client.SendAsync(CreateFullContainerActionRequest(scene,"mixed-physical"));
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        Assert.Equal("FG-04",await fixture.ScalarAsync<string>("SELECT destination_business_code FROM ops.transport_requests WHERE machine_id=@m",("m",scene.MachineId)));
        }
        finally { await RemoveIsolatedPhysicalFixturesAsync(); }
    }

    private async Task RemoveIsolatedPhysicalFixturesAsync()
    {
        if (new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database != "tkg_pda_phase04")
            throw new InvalidOperationException("Physical evidence fixtures require the isolated phase04 database.");
        await fixture.ExecuteAsync("""
            DELETE FROM integration.layout_slot_rds_profile_mapping WHERE rds_worksite_id IN (
                SELECT id FROM integration.rds_worksites WHERE rds_site_id IN ('ISOLATED-MACHINE-POSITION','ISOLATED-FG03','ISOLATED-FG04'));
            DELETE FROM integration.profile_shared_endpoint_bindings WHERE rds_worksite_id IN (
                SELECT id FROM integration.rds_worksites WHERE rds_site_id IN ('ISOLATED-MACHINE-POSITION','ISOLATED-FG03','ISOLATED-FG04'));
            DELETE FROM integration.rds_worksites WHERE rds_site_id IN ('ISOLATED-MACHINE-POSITION','ISOLATED-FG03','ISOLATED-FG04');
            """);
    }

    private async Task<TwoMachineSetup> PrepareTwoMachineScenesAsync(bool includeFg02 = true, bool includeReturn02 = true)
    {
        await RemoveIsolatedPhysicalFixturesAsync();
        await fixture.ResetAsync();

        var sceneA = await PrepareSceneAsync("SH-01-A", "1", "TKG_SH01A_V01_NAV", includeFg02, includeReturn02);
        var sceneB = await PrepareSceneAsync("PAH-01-A", "1", "TKG_PAH01A_V01_NAV", includeFg02, includeReturn02);

        return new TwoMachineSetup(sceneA, sceneB);
    }

    private async Task<Scene> PrepareSceneAsync(
        string machineCode, string variantCode, string navCode, bool includeFg02, bool includeReturn02)
    {
        var context = await fixture.ConfigureMachineAsync(machineCode, variantCode);
        await InstallProfileEndpointsAsync(navCode, includeFg02, includeReturn02);

        var navigationId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.navigation_profiles WHERE code = @code", ("code", navCode));

        await fixture.ExecuteAsync(
            "UPDATE ops.machine_runtime_state SET active_navigation_id = @nav WHERE machine_id = @machine",
            ("nav", navigationId), ("machine", context.MachineId));

        var slot = await fixture.ScalarAsync<Guid>("""
            SELECT id FROM master.layout_slots
            WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ORDER BY display_order, code, id
            LIMIT 1
            """, ("layout", context.LayoutId));

        var position = await fixture.ScalarAsync<int>("""
            SELECT position_number FROM (
                SELECT id, row_number() OVER (ORDER BY display_order, code, id)::int AS position_number
                FROM master.layout_slots
                WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ) numbered
            WHERE id = @slot
            """, ("layout", context.LayoutId), ("slot", slot));

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var main = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, description) VALUES (@code, 'Mamul') RETURNING id",
            ("code", $"P-MAIN-{suffix}"));
        var alt = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, description) VALUES (@code, 'Alt') RETURNING id",
            ("code", $"P-ALT-{suffix}"));

        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id, name) VALUES (@variant, @part, 'Group') RETURNING id",
            ("variant", context.VariantId), ("part", main));

        var container = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync("""
            INSERT INTO master.product_group_parts (product_group_id, part_id, container_type_id, relation_type, is_active)
            VALUES (@group, @part, @container, 'ALT_PART', true)
            """, ("group", group), ("part", alt), ("container", container));

        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING",
            ("slot", slot), ("part", main));

        await fixture.SetSlotAsync(slot, "OCCUPIED", "FULL", main);

        var oper = await fixture.SeedIdentityAsync(machineCode: machineCode);

        return new Scene(context.MachineId, context.VariantId, context.LayoutId, navigationId, group, main, alt, slot, position, oper);
    }

    private async Task InstallProfileEndpointsAsync(string navCode, bool includeFg02, bool includeReturn02)
    {
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'PUBLISHED',
                published_at = COALESCE(published_at, now()),
                config_json = jsonb_set(
                    jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb),
                    '{commissioningState}', '"COMMISSIONED"'::jsonb)
            WHERE code = @code
            """, ("code", navCode));

        await fixture.ExecuteAsync("""
            INSERT INTO integration.rds_worksites
                (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, last_filled, last_locked, raw_state_json)
            SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0,
                   CASE WHEN p.object_family = 'EMPTY' AND p.ordinal = 1 THEN 1 ELSE 0 END,
                   0, '{"locked":0}'::jsonb
            FROM integration.rds_worksite_plans p
            JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
            WHERE n.code = @code
              AND (
                  (p.object_family = 'PREPARATION' AND p.ordinal BETWEEN 1 AND 4)
                  OR (p.object_family = 'EMPTY' AND p.ordinal BETWEEN 1 AND 2)
                  OR (p.object_family = 'RETURN' AND p.ordinal BETWEEN 1 AND 2)
                  OR (p.object_family = 'FINISHED_GOODS' AND p.ordinal BETWEEN 1 AND 2))
            ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
            SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

            INSERT INTO integration.preparation_area_rds_mapping (preparation_area_id, rds_worksite_id)
            SELECT pa.id, w.id
            FROM ops.preparation_areas pa
            JOIN integration.rds_worksite_plans p ON p.object_family = 'PREPARATION' AND p.ordinal = pa.area_number
            JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id AND n.code = @code
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            WHERE pa.area_number BETWEEN 1 AND 4
            ON CONFLICT (preparation_area_id, rds_worksite_id) DO NOTHING;

            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
            SELECT n.id, 'EMPTY_SOURCE', 'EMPTY-01', loc.id, w.id
            FROM master.navigation_profiles n
            JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'EMPTY' AND p.ordinal = 1
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            JOIN ops.store_locations loc ON loc.code = 'TEST-FG-EMPTY'
            WHERE n.code = @code
            ON CONFLICT DO NOTHING;

            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
            SELECT n.id, 'EMPTY_RETURN', 'EMPTY-01', NULL, w.id
            FROM master.navigation_profiles n
            JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'EMPTY' AND p.ordinal = 1
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            WHERE n.code = @code
            ON CONFLICT DO NOTHING;

            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
            SELECT n.id, 'FINISHED_GOODS', 'FG-01', loc.id, w.id
            FROM master.navigation_profiles n
            JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'FINISHED_GOODS' AND p.ordinal = 1
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            JOIN ops.store_locations loc ON loc.code = 'FG-01'
            WHERE n.code = @code
            ON CONFLICT DO NOTHING;

            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
            SELECT n.id, 'NON_FINISHED_RETURN', 'RETURN-01', NULL, w.id
            FROM master.navigation_profiles n
            JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'RETURN' AND p.ordinal = 1
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            WHERE n.code = @code
            ON CONFLICT DO NOTHING;
            """, ("code", navCode));

        if (includeFg02)
        {
            await fixture.ExecuteAsync("""
                INSERT INTO integration.profile_shared_endpoint_bindings
                    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
                SELECT n.id, 'FINISHED_GOODS', 'FG-02', loc.id, w.id
                FROM master.navigation_profiles n
                JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'FINISHED_GOODS' AND p.ordinal = 2
                JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
                JOIN ops.store_locations loc ON loc.code = 'FG-02'
                WHERE n.code = @code
                ON CONFLICT DO NOTHING;
                """, ("code", navCode));
        }

        if (includeReturn02)
        {
            await fixture.ExecuteAsync("""
                INSERT INTO integration.profile_shared_endpoint_bindings
                    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
                SELECT n.id, 'NON_FINISHED_RETURN', 'RETURN-02', NULL, w.id
                FROM master.navigation_profiles n
                JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'RETURN' AND p.ordinal = 2
                JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
                WHERE n.code = @code
                ON CONFLICT DO NOTHING;
                """, ("code", navCode));
        }
    }

    private static HttpRequestMessage CreateFullContainerActionRequest(Scene setup, string idempotencyKey)
    {
        var action = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                setup.VariantId, setup.LayoutId, null, setup.Group, setup.MainPart, setup.Position, "FULL"))
        };
        action.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        return action;
    }

    private async Task<TwoMachineSetup> PrepareTwoMachineScenesUnboundAsync()
    {
        await RemoveIsolatedPhysicalFixturesAsync();
        await fixture.ResetAsync();

        var sceneA = await PrepareSceneUnboundAsync("SH-01-A", "1", "TKG_SH01A_V01_NAV");
        var sceneB = await PrepareSceneUnboundAsync("PAH-01-A", "1", "TKG_PAH01A_V01_NAV");

        return new TwoMachineSetup(sceneA, sceneB);
    }

    private async Task<Scene> PrepareSceneUnboundAsync(string machineCode, string variantCode, string navCode)
    {
        var context = await fixture.ConfigureMachineAsync(machineCode, variantCode);
        await DiscoverProfileWorksitesOnlyAsync(navCode);

        var navigationId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.navigation_profiles WHERE code = @code", ("code", navCode));

        await fixture.ExecuteAsync(
            "UPDATE ops.machine_runtime_state SET active_navigation_id = @nav WHERE machine_id = @machine",
            ("nav", navigationId), ("machine", context.MachineId));

        var slot = await fixture.ScalarAsync<Guid>("""
            SELECT id FROM master.layout_slots
            WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ORDER BY display_order, code, id
            LIMIT 1
            """, ("layout", context.LayoutId));

        var position = await fixture.ScalarAsync<int>("""
            SELECT position_number FROM (
                SELECT id, row_number() OVER (ORDER BY display_order, code, id)::int AS position_number
                FROM master.layout_slots
                WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ) numbered
            WHERE id = @slot
            """, ("layout", context.LayoutId), ("slot", slot));

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var main = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, description) VALUES (@code, 'Mamul') RETURNING id",
            ("code", $"P-MAIN-{suffix}"));
        var alt = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, description) VALUES (@code, 'Alt') RETURNING id",
            ("code", $"P-ALT-{suffix}"));

        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id, name) VALUES (@variant, @part, 'Group') RETURNING id",
            ("variant", context.VariantId), ("part", main));

        var container = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync("""
            INSERT INTO master.product_group_parts (product_group_id, part_id, container_type_id, relation_type, is_active)
            VALUES (@group, @part, @container, 'ALT_PART', true)
            """, ("group", group), ("part", alt), ("container", container));

        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING",
            ("slot", slot), ("part", main));

        await fixture.SetSlotAsync(slot, "OCCUPIED", "FULL", main);

        var oper = await fixture.SeedIdentityAsync(machineCode: machineCode);

        return new Scene(context.MachineId, context.VariantId, context.LayoutId, navigationId, group, main, alt, slot, position, oper);
    }

    private async Task DiscoverProfileWorksitesOnlyAsync(string navCode)
    {
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'PUBLISHED',
                published_at = COALESCE(published_at, now()),
                config_json = jsonb_set(
                    jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb),
                    '{commissioningState}', '"COMMISSIONED"'::jsonb)
            WHERE code = @code;

            INSERT INTO integration.rds_worksites
                (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, last_filled, last_locked, raw_state_json)
            SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, 0, 0, '{"filled":0,"locked":0}'::jsonb
            FROM integration.rds_worksite_plans p
            JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
            WHERE n.code = @code
              AND (
                  (p.object_family = 'PREPARATION' AND p.ordinal BETWEEN 1 AND 4)
                  OR (p.object_family = 'EMPTY' AND p.ordinal BETWEEN 1 AND 8)
                  OR (p.object_family = 'RETURN' AND p.ordinal BETWEEN 1 AND 8)
                  OR (p.object_family = 'FINISHED_GOODS' AND p.ordinal BETWEEN 1 AND 19))
            ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
            SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

            INSERT INTO integration.preparation_area_rds_mapping (preparation_area_id, rds_worksite_id)
            SELECT pa.id, w.id
            FROM ops.preparation_areas pa
            JOIN integration.rds_worksite_plans p ON p.object_family = 'PREPARATION' AND p.ordinal = pa.area_number
            JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id AND n.code = @code
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            WHERE pa.area_number BETWEEN 1 AND 4
            ON CONFLICT (preparation_area_id, rds_worksite_id) DO NOTHING;
            """, ("code", navCode));
    }

    private static async Task StartAsync(HttpClient client, Scene setup)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.MachineId}/production-sessions")
        {
            Content = JsonContent.Create(new StartProductionSessionHttpRequest(setup.VariantId, setup.Group, setup.MainPart))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "prod-start-" + Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<Guid[]> ThreeSlotsAsync(Guid layoutId)
    {
        var slots = new Guid[3];
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index] = await fixture.ScalarAsync<Guid>("""
                SELECT id FROM master.layout_slots
                WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
                ORDER BY display_order, code, id
                OFFSET @index LIMIT 1
                """, ("layout", layoutId), ("index", index));
        }

        return slots;
    }

    private async Task MapOccupiedAsync(Guid slotId, Guid partId, string load, Guid fallbackContainerTypeId)
    {
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING",
            ("slot", slotId), ("part", partId));
        var containerTypeId = await fixture.ScalarAsync<Guid>("""
            SELECT COALESCE(default_container_type_id, @fallback)
            FROM master.layout_slots WHERE id = @slot
            """, ("fallback", fallbackContainerTypeId), ("slot", slotId));
        await fixture.SetSlotAsync(slotId, "OCCUPIED", load, partId);
        await fixture.ExecuteAsync(
            "UPDATE ops.slot_runtime_state SET current_container_type_id = @container WHERE layout_slot_id = @slot",
            ("container", containerTypeId), ("slot", slotId));
    }

    private sealed record Scene(
        Guid MachineId,
        Guid VariantId,
        Guid LayoutId,
        Guid NavigationId,
        Guid Group,
        Guid MainPart,
        Guid AltPart,
        Guid Slot,
        int Position,
        TestIdentity Operator);

    private sealed record TwoMachineSetup(Scene SceneA, Scene SceneB);
}
