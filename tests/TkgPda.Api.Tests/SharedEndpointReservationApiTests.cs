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
public sealed class SharedEndpointReservationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
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

            Assert.Contains("FINISHED_GOODS_CAPACITY_UNAVAILABLE", failedBody, StringComparison.Ordinal);
            Assert.Contains("Uygun boş bitmiş ürün alanı bulunamadı", failedBody, StringComparison.Ordinal);

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
    public async Task Reservation_is_released_when_request_is_cancelled_or_failed()
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
            Assert.Contains("FINISHED_GOODS_CAPACITY_UNAVAILABLE", await resB.Content.ReadAsStringAsync());

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

    // Helper setups

    private async Task<TwoMachineSetup> PrepareTwoMachineScenesAsync(bool includeFg02 = true, bool includeReturn02 = true)
    {
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
                (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
            SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
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
            SELECT n.id, 'EMPTY_SOURCE', p.planned_rds_site_id, NULL, w.id
            FROM master.navigation_profiles n
            JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'EMPTY' AND p.ordinal = 1
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
            WHERE n.code = @code
            ON CONFLICT DO NOTHING;

            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
            SELECT n.id, 'EMPTY_RETURN', p.planned_rds_site_id, NULL, w.id
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
