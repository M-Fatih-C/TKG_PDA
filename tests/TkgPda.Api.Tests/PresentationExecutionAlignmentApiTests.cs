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
/// Public API requests on a commissioning navigation profile must persist the
/// same business endpoint identity that readiness and RDS dispatch resolve.
/// WorkSite rows here are test discovery only. The production seed does not insert them.
/// </summary>
public sealed class PresentationExecutionAlignmentApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Public_bring_full_resolves_the_profile_preparation_worksite()
    {
        var setup = await PrepareAsync();
        try
        {
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartAsync(client, setup);

        var created = await client.SendAsync(Demand(setup, setup.AltPart, "align-bring"));
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        var parsed = await created.Content.ReadFromJsonAsync<PartDemandHttpResponse>();
        Assert.NotNull(parsed);
        Assert.Equal("BRING_FULL", parsed.TaskType);
        Assert.Equal("WAITING_PREPARATION", parsed.Readiness);
        var requestId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests WHERE request_no = @no", ("no", parsed.RequestNo));
        Assert.Equal(setup.NavigationId, await fixture.ScalarAsync<Guid>("SELECT navigation_id FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT destination_business_code IS NULL FROM ops.transport_requests WHERE id = @id", ("id", requestId)));

        await MarkReadyAsync(setup, requestId);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var execution = new PostgresRdsExecutionStore(dataSource);
        Assert.Null(await execution.ReadRegistryDispatchBlockAsync(requestId));
        var context = await execution.GetDispatchContextAsync(requestId);
        Assert.NotNull(context);
        Assert.Contains("_WS_PREP01", context.Source.SiteId, StringComparison.Ordinal);
        Assert.DoesNotContain("_V02_", context.Source.SiteId, StringComparison.Ordinal);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Public_remove_chains_use_the_same_endpoint_classes_as_readiness()
    {
        var setup = await PrepareAsync(occupancy: "OCCUPIED", load: "FULL");
        try
        {
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartAsync(client, setup);
        var repeated = await client.SendAsync(Demand(setup, setup.AltPart, "align-repeat"));
        Assert.Equal(HttpStatusCode.Created, repeated.StatusCode);
        var body = await repeated.Content.ReadFromJsonAsync<PartDemandHttpResponse>();
        Assert.Equal("REMOVE_FULL", body!.PredecessorTaskType);
        var predecessor = await fixture.ScalarAsync<string>("""
            SELECT destination_business_code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE tt.code = 'REMOVE_FULL'
            """);
        Assert.Equal("RETURN-01", predecessor);
        Assert.DoesNotContain("FG-", predecessor, StringComparison.Ordinal);

        await fixture.ExecuteAsync("DELETE FROM ops.transport_request_dependencies");
        await fixture.ExecuteAsync("DELETE FROM ops.transport_requests");
        await fixture.SetSlotAsync(setup.Slot, "OCCUPIED", "EMPTY", setup.AltPart);
        var empty = await client.SendAsync(Demand(setup, setup.AltPart, "align-empty"));
        Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
        var emptyCode = await fixture.ScalarAsync<string>("""
            SELECT destination_business_code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE tt.code = 'REMOVE_EMPTY'
            """);
        Assert.Equal("EMPTY-01", emptyCode);
        Assert.True(await fixture.ScalarAsync<bool>("""
            SELECT destination_slot_id IS NULL FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE tt.code = 'REMOVE_EMPTY'
            """));

        await fixture.ExecuteAsync("DELETE FROM ops.transport_request_dependencies");
        await fixture.ExecuteAsync("DELETE FROM ops.transport_requests");
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING",
            ("slot", setup.Slot), ("part", setup.MainPart));
        await fixture.SetSlotAsync(setup.Slot, "OCCUPIED", "FULL", setup.MainPart);
        var action = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                setup.VariantId, setup.LayoutId, null, setup.Group, setup.MainPart, setup.Position, "FULL"))
        };
        action.Headers.Add(ApiHeaders.IdempotencyKey, "align-mamul");
        var filled = await client.SendAsync(action);
        var filledBody = await filled.Content.ReadAsStringAsync();
        Assert.True(filled.IsSuccessStatusCode, filledBody);
        Assert.Equal("FG-01", await fixture.ScalarAsync<string>("""
            SELECT destination_business_code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE tt.code = 'REMOVE_FULL'
            """));
        var mamulId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.transport_requests");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.RecordExecutionTransitionAsync(mamulId, TransportRequestStatus.SentToRds);
        await store.RecordExecutionTransitionAsync(mamulId, TransportRequestStatus.Running);
        await store.RecordExecutionTransitionAsync(mamulId, TransportRequestStatus.Completed);
        Assert.Equal("FG-01", await fixture.ScalarAsync<string>("""
            SELECT l.code FROM ops.store_units u
            JOIN ops.store_locations l ON l.id = u.store_location_id
            """));
        await store.RecordExecutionTransitionAsync(mamulId, TransportRequestStatus.Completed);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_events WHERE event_type = 'UNIT_DELIVERED'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Public_production_end_cleanup_uses_the_same_shared_endpoints()
    {
        var setup = await PrepareAsync();
        try
        {
            var slots = await ThreeSlotsAsync(setup.LayoutId);
            var fallbackContainer = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
            await MapOccupiedAsync(slots[0], setup.MainPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(slots[1], setup.AltPart, "FULL", fallbackContainer);
            await MapOccupiedAsync(slots[2], setup.AltPart, "EMPTY", fallbackContainer);
            // TEST/DEMO fixture only. These numbers are not customer production-end priorities.
            await fixture.ExecuteAsync("""
                INSERT INTO master.production_end_priorities (operation_code, priority)
                VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
                ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority
                """);

            using var client = await fixture.LoginClientAsync(setup.Operator);
            await StartAsync(client, setup);
            var sessionId = await fixture.ScalarAsync<Guid>(
                "SELECT id FROM ops.production_sessions WHERE machine_id = @machine AND status = 'ACTIVE'",
                ("machine", setup.MachineId));
            var end = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/machines/{setup.MachineId}/production-sessions/{sessionId}/end");
            end.Headers.Add(ApiHeaders.IdempotencyKey, "align-production-end");
            var ended = await client.SendAsync(end);
            var endedBody = await ended.Content.ReadAsStringAsync();
            Assert.True(ended.IsSuccessStatusCode, endedBody);

            Assert.Equal(3L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
            Assert.Equal("FG-01", await DestinationCodeAsync(setup.MainPart, "REMOVE_FULL"));
            var altReturn = await DestinationCodeAsync(setup.AltPart, "REMOVE_FULL");
            Assert.Equal("RETURN-01", altReturn);
            Assert.DoesNotContain("FG-", altReturn, StringComparison.Ordinal);
            var emptyReturn = await DestinationCodeAsync(setup.AltPart, "REMOVE_EMPTY");
            Assert.Equal("EMPTY-01", emptyReturn);
            Assert.Equal(3L, await fixture.ScalarAsync<long>("""
                SELECT count(*) FROM ops.transport_requests
                WHERE destination_slot_id IS NULL AND navigation_id = @nav
                """, ("nav", setup.NavigationId)));
            Assert.Equal(40, await fixture.ScalarAsync<int>("""
                SELECT priority FROM ops.transport_requests r
                JOIN master.task_types tt ON tt.id = r.task_type_id
                WHERE tt.code = 'REMOVE_FULL' AND r.part_id = @part
                """, ("part", setup.MainPart)));
            Assert.Equal(70, await fixture.ScalarAsync<int>("""
                SELECT priority FROM ops.transport_requests r
                JOIN master.task_types tt ON tt.id = r.task_type_id
                WHERE tt.code = 'REMOVE_EMPTY'
                """));
            Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));

            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var execution = new PostgresRdsExecutionStore(dataSource);
            await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                var requestId = await fixture.ScalarAsync<Guid>(
                    "SELECT transport_request_id FROM ops.production_end_run_items WHERE sequence_no = @n",
                    ("n", sequence));
                Assert.Null(await execution.ReadRegistryDispatchBlockAsync(requestId));
                var context = await execution.GetDispatchContextAsync(requestId);
                Assert.NotNull(context!.Destination.SiteId);
                Assert.DoesNotContain("_V02_", context.Destination.SiteId, StringComparison.Ordinal);
                await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
                await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
                await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
            }

            Assert.Equal("FG-01", await fixture.ScalarAsync<string>("""
                SELECT l.code FROM ops.store_units u
                JOIN ops.store_locations l ON l.id = u.store_location_id
                """));
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_events WHERE event_type = 'UNIT_DELIVERED'"));
            Assert.Equal("ENDED", await fixture.ScalarAsync<string>("SELECT status FROM ops.production_sessions WHERE id = @id", ("id", sessionId)));
            Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        }
        finally
        {
            await fixture.ResetAsync();
        }
    }

    [Fact]
    public async Task Source_backed_variants_are_classified_without_calling_missing_field_data_a_software_defect()
    {
        await fixture.ResetAsync();
        Assert.Equal(9L, await fixture.ScalarAsync<long>("SELECT count(*) FROM master.machines WHERE is_active"));
        Assert.Equal(25L, await fixture.ScalarAsync<long>("SELECT count(*) FROM master.variants WHERE is_active"));
        Assert.Equal(24L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
              AND config_json ->> 'layoutBindingState' = 'SOURCE_BACKED'
            """));
        Assert.Equal("PENDING_SOURCE_LAYOUT", await fixture.ScalarAsync<string>("""
            SELECT config_json ->> 'layoutBindingState' FROM master.navigation_profiles WHERE code = 'TKG_SEALER_V03_NAV'
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM master.task_templates t
            JOIN master.variants v ON v.id = t.variant_id
            JOIN master.machines m ON m.id = v.machine_id
            WHERE m.code = 'SEALER' AND v.code = '3' AND t.is_active
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites"));
        Assert.Equal(4L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.preparation_areas WHERE area_number BETWEEN 1 AND 4"));
        Assert.Equal(19L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_locations WHERE code LIKE 'FG-%' AND is_enabled"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM master.product_groups pg
            JOIN master.variants v ON v.id = pg.variant_id
            WHERE pg.is_active AND NOT v.is_active
            """));
        var altParts = await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM master.product_group_parts
            WHERE relation_type = 'ALT_PART' AND is_active
            """);
        var incomplete = await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM master.product_group_parts
            WHERE relation_type = 'ALT_PART' AND is_active AND container_type_id IS NULL
            """);
        Assert.True(altParts > incomplete);
        Assert.True(incomplete > 0);
        Assert.Equal(96L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM (
                SELECT DISTINCT t.variant_id, tt.code
                FROM master.task_templates t
                JOIN master.task_types tt ON tt.id = t.task_type_id
                JOIN master.variants v ON v.id = t.variant_id
                JOIN master.variant_navigation_profile_assignments a ON a.variant_id = v.id
                JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id
                    AND n.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
                    AND n.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED'
                WHERE t.is_active
                  AND t.product_group_id IS NULL
                  AND tt.code IN ('BRING_FULL','BRING_EMPTY','REMOVE_FULL','REMOVE_EMPTY')
            ) covered
            """));
    }

    private async Task<Scene> PrepareAsync(string occupancy = "VACANT", string load = "UNKNOWN")
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'PUBLISHED',
                published_at = COALESCE(published_at, now()),
                config_json = jsonb_set(
                    jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb),
                    '{commissioningState}', '"COMMISSIONED"'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        await InstallEndpointsAsync();
        var navigationId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'");
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
        var main = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code, description) VALUES ('P05-ALIGN-MAIN', 'Mamul') RETURNING id");
        var alt = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code, description) VALUES ('P05-ALIGN-ALT', 'Alt') RETURNING id");
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id, name) VALUES (@variant, @part, 'Align') RETURNING id",
            ("variant", context.VariantId), ("part", main));
        var container = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync("""
            INSERT INTO master.product_group_parts (product_group_id, part_id, container_type_id, relation_type, is_active)
            VALUES (@group, @part, @container, 'ALT_PART', true)
            """, ("group", group), ("part", alt), ("container", container));
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
            ("slot", slot), ("part", alt));
        await fixture.SetSlotAsync(slot, occupancy, load, occupancy == "OCCUPIED" ? alt : null);
        var oper = await fixture.SeedIdentityAsync();
        return new Scene(context.MachineId, context.VariantId, context.LayoutId, navigationId, group, main, alt, slot, position, oper);
    }

    private Task InstallEndpointsAsync() => fixture.ExecuteAsync("""
        INSERT INTO integration.rds_worksites
            (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
        SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
        FROM integration.rds_worksite_plans p
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
        WHERE n.code = 'TKG_SH01A_V01_NAV'
          AND (
              (p.object_family = 'PREPARATION' AND p.ordinal BETWEEN 1 AND 4)
              OR (p.object_family = 'EMPTY' AND p.ordinal = 1)
              OR (p.object_family = 'RETURN' AND p.ordinal = 1)
              OR (p.object_family = 'FINISHED_GOODS' AND p.ordinal = 1))
        ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
        SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

        INSERT INTO integration.preparation_area_rds_mapping (preparation_area_id, rds_worksite_id)
        SELECT pa.id, w.id
        FROM ops.preparation_areas pa
        JOIN integration.rds_worksite_plans p ON p.object_family = 'PREPARATION' AND p.ordinal = pa.area_number
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id AND n.code = 'TKG_SH01A_V01_NAV'
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE pa.area_number BETWEEN 1 AND 4
        ON CONFLICT (preparation_area_id, rds_worksite_id) DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'EMPTY_SOURCE', 'EMPTY-01', NULL, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'EMPTY' AND p.ordinal = 1
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE n.code = 'TKG_SH01A_V01_NAV'
        ON CONFLICT DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'EMPTY_RETURN', 'EMPTY-01', NULL, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'EMPTY' AND p.ordinal = 1
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE n.code = 'TKG_SH01A_V01_NAV'
        ON CONFLICT DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'NON_FINISHED_RETURN', 'RETURN-01', NULL, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'RETURN' AND p.ordinal = 1
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE n.code = 'TKG_SH01A_V01_NAV'
        ON CONFLICT DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'FINISHED_GOODS', 'FG-01', loc.id, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id AND p.object_family = 'FINISHED_GOODS' AND p.ordinal = 1
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        JOIN ops.store_locations loc ON loc.code = 'FG-01'
        WHERE n.code = 'TKG_SH01A_V01_NAV'
        ON CONFLICT DO NOTHING;
        """);

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

    private Task<string> DestinationCodeAsync(Guid partId, string operation) => fixture.ScalarAsync<string>(
        """
        SELECT r.destination_business_code
        FROM ops.transport_requests r
        JOIN master.task_types tt ON tt.id = r.task_type_id
        WHERE tt.code = @operation AND r.part_id = @part
        """,
        ("operation", operation),
        ("part", partId));

    private static async Task StartAsync(HttpClient client, Scene setup)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.MachineId}/production-sessions")
        {
            Content = JsonContent.Create(new StartProductionSessionHttpRequest(setup.VariantId, setup.Group, setup.MainPart))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "align-prod-" + Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static HttpRequestMessage Demand(Scene setup, Guid partId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.MachineId}/part-demands")
        {
            Content = JsonContent.Create(new PartDemandHttpRequest(partId))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return request;
    }

    private async Task MarkReadyAsync(Scene setup, Guid requestId)
    {
        var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.SemiFinishedWarehouseOperator, machineCode: null, bindDeviceToMachine: false);
        using var client = await fixture.LoginClientAsync(warehouse);
        var areaId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.preparation_areas WHERE area_number = 1");
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT container_type_id FROM ops.transport_requests WHERE id = @id", ("id", requestId));
        var place = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/areas/{areaId}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest("FULL", containerTypeId, setup.AltPart, requestId))
        };
        place.Headers.Add(ApiHeaders.IdempotencyKey, "align-place");
        var placed = await client.SendAsync(place);
        var placedBody = await placed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var pallet = System.Text.Json.JsonSerializer.Deserialize<PreparationPalletRecord>(
            placedBody, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var ready = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/pallets/{pallet!.Id}/ready")
        {
            Content = JsonContent.Create(new PalletVersionHttpRequest(pallet.RowVersion))
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ready)).StatusCode);
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
}
