using Npgsql;
using TkgPda.Application;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class VariantOperationReadinessApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Template_seed_is_replay_stable_and_leaves_sealer_v3_inexecutable()
    {
        await fixture.ResetAsync();
        var seed = SeedFile();
        var beforeSites = await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites");
        Assert.DoesNotContain("INSERT INTO integration.rds_worksites", await File.ReadAllTextAsync(seed), StringComparison.OrdinalIgnoreCase);
        await fixture.ExecuteAsync("""
            DELETE FROM master.task_templates
            WHERE product_group_id IS NULL
              AND task_type_id IN (
                  SELECT id FROM master.task_types
                  WHERE code IN ('BRING_FULL','BRING_EMPTY','REMOVE_FULL','REMOVE_EMPTY'))
            """);
        await fixture.ExecuteScriptAsync(seed);
        var first = await TemplateCoverageAsync();
        await fixture.ExecuteScriptAsync(SeedFile());
        var second = await TemplateCoverageAsync();

        Assert.Equal(beforeSites, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites"));
        Assert.Equal(24, first.SourceBacked);
        Assert.Equal(96, first.ActiveTemplates);
        Assert.Equal(first, second);
        Assert.Equal(0, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM master.task_templates t
            JOIN master.variants v ON v.id = t.variant_id
            JOIN master.machines m ON m.id = v.machine_id
            WHERE m.code = 'SEALER' AND v.code = '3' AND t.is_active
            """));

        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var report = await new PostgresRdsIntegrationStore(dataSource).ListVariantCommissioningAsync();
        var sealer = Assert.Single(report, row => row.MachineCode == "SEALER" && row.VariantCode == "3");
        Assert.Equal("PENDING_SOURCE_LAYOUT", sealer.LayoutBindingState);
        Assert.Equal("NOT_READY", sealer.OverallState);
        Assert.False(sealer.Ready);
        Assert.All(sealer.Operations, operation => Assert.Contains("SOURCE_LAYOUT_MISSING", operation.Blockers));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
    }

    [Fact]
    public async Task Bring_full_prep_mapping_is_profile_scoped_and_exact()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteScriptAsync(SeedFile());
        await ActivateTemplatesAsync();
        await PublishAsync("TKG_SH01A_V01_NAV");
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        var requestId = await InsertBringFullAsync(identity.UserId, identity.DeviceId, "TKG_SH01A_V01_NAV");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var execution = new PostgresRdsExecutionStore(dataSource);

        Assert.Equal("PREPARATION_WORKSITE_MAPPING_MISSING", await execution.ReadRegistryDispatchBlockAsync(requestId));

        await MapPreparationAsync("TKG_SH01A_V01_NAV", 1);
        var context = await execution.GetDispatchContextAsync(requestId);
        Assert.Equal("TKG_SH01A_V01_WS_PREP01", context!.Source.SiteId);
        Assert.Null(await execution.ReadRegistryDispatchBlockAsync(requestId));

        await MapPreparationAsync("TKG_SH01A_V02_NAV", 2);
        await fixture.ExecuteAsync("""
            UPDATE ops.preparation_pallets
            SET preparation_area_id = (SELECT id FROM ops.preparation_areas WHERE area_number = 2)
            WHERE transport_request_id = @request
            """, ("request", requestId));
        Assert.Equal("PREPARATION_WORKSITE_MAPPING_MISSING", await execution.ReadRegistryDispatchBlockAsync(requestId));
        Assert.True(string.IsNullOrEmpty((await execution.GetDispatchContextAsync(requestId))!.Source.SiteId));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
    }

    [Fact]
    public async Task Shared_endpoint_resolution_stays_inside_one_navigation_profile()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteScriptAsync(SeedFile());
        await ActivateTemplatesAsync();
        await PublishAsync("TKG_SH01A_V01_NAV");
        await PublishAsync("TKG_SH01A_V02_NAV");
        await DiscoverAsync("TKG_SH01A_V01_NAV", "EMPTY", 1);
        await DiscoverAsync("TKG_SH01A_V02_NAV", "EMPTY", 1);
        await DiscoverAsync("TKG_SH01A_V01_NAV", "FINISHED_GOODS", 1);
        await DiscoverAsync("TKG_SH01A_V02_NAV", "FINISHED_GOODS", 1);
        await DiscoverAsync("TKG_SH01A_V01_NAV", "RETURN", 1);
        var v1 = await ProfileAsync("TKG_SH01A_V01_NAV");
        var v2 = await ProfileAsync("TKG_SH01A_V02_NAV");
        var v2Empty = await WorkSiteAsync(v2, "TKG_SH01A_V02_WS_EMPTY01");
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, rds_worksite_id)
            VALUES (@profile, 'EMPTY_SOURCE', 'EMPTY-01', @site)
            """, ("profile", v1), ("site", v2Empty)));
        Assert.Contains("PROFILE_MISMATCH", mismatch.MessageText, StringComparison.Ordinal);

        await BindAsync(v1, "EMPTY_SOURCE", "EMPTY-01", await WorkSiteAsync(v1, "TKG_SH01A_V01_WS_EMPTY01"));
        await BindAsync(v1, "EMPTY_RETURN", "EMPTY-01", await WorkSiteAsync(v1, "TKG_SH01A_V01_WS_EMPTY01"));
        await BindAsync(v1, "FINISHED_GOODS", "FG-01", await WorkSiteAsync(v1, "TKG_SH01A_V01_WS_FG01"), "FG-01");
        await BindAsync(v2, "FINISHED_GOODS", "FG-01", await WorkSiteAsync(v2, "TKG_SH01A_V02_WS_FG01"), "FG-01");
        await BindAsync(v1, "NON_FINISHED_RETURN", "RETURN-01", await WorkSiteAsync(v1, "TKG_SH01A_V01_WS_RETURN01"));

        var bring = await InsertRequestAsync("BRING_EMPTY", "TKG_SH01A_V01_NAV", null);
        var removeEmpty = await InsertRequestAsync("REMOVE_EMPTY", "TKG_SH01A_V01_NAV", "EMPTY-01");
        var mamul = await InsertRequestAsync("REMOVE_FULL", "TKG_SH01A_V01_NAV", "FG-01");
        var alt = await InsertRequestAsync("REMOVE_FULL", "TKG_SH01A_V01_NAV", "RETURN-01");
        var otherNav = await InsertRequestAsync("REMOVE_FULL", "TKG_SH01A_V02_NAV", "FG-01");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var execution = new PostgresRdsExecutionStore(dataSource);

        Assert.Equal("TKG_SH01A_V01_WS_EMPTY01", (await execution.GetDispatchContextAsync(bring))!.Source.SiteId);
        Assert.Equal("TKG_SH01A_V01_WS_EMPTY01", (await execution.GetDispatchContextAsync(removeEmpty))!.Destination.SiteId);
        Assert.Equal("TKG_SH01A_V01_WS_FG01", (await execution.GetDispatchContextAsync(mamul))!.Destination.SiteId);
        Assert.Equal("TKG_SH01A_V01_WS_RETURN01", (await execution.GetDispatchContextAsync(alt))!.Destination.SiteId);
        Assert.Equal("TKG_SH01A_V02_WS_FG01", (await execution.GetDispatchContextAsync(otherNav))!.Destination.SiteId);
        Assert.NotEqual(
            (await execution.GetDispatchContextAsync(mamul))!.Destination.SiteId,
            (await execution.GetDispatchContextAsync(otherNav))!.Destination.SiteId);

        await fixture.ExecuteAsync("""
            UPDATE integration.rds_worksites SET last_disabled = 1
            WHERE rds_site_id = 'TKG_SH01A_V01_WS_FG01'
            """);
        Assert.Equal("RDS_WORKSITE_DISABLED", await execution.ReadRegistryDispatchBlockAsync(mamul));
        await fixture.ExecuteAsync("""
            UPDATE integration.rds_worksites SET last_disabled = 0, last_sync_failed = 1
            WHERE rds_site_id = 'TKG_SH01A_V01_WS_FG01'
            """);
        Assert.Equal("RDS_WORKSITE_SYNC_FAILED", await execution.ReadRegistryDispatchBlockAsync(mamul));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
    }

    [Fact]
    public async Task Operation_readiness_does_not_promote_a_missing_chain()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteScriptAsync(SeedFile());
        await ActivateTemplatesAsync();
        await PublishAsync("TKG_SH01A_V01_NAV");
        await fixture.ExecuteAsync("""
            UPDATE master.task_templates AS template
            SET is_active = false
            FROM master.variants AS variant
            JOIN master.machines AS machine ON machine.id = variant.machine_id
            JOIN master.task_types AS task_type ON task_type.code = 'BRING_FULL'
            WHERE template.variant_id = variant.id
              AND template.task_type_id = task_type.id
              AND machine.code = 'SH-01-A' AND variant.code = '1'
              AND template.product_group_id IS NULL
            """);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        var row = Assert.Single(await store.ListVariantCommissioningAsync(), item => item.NavigationCode == "TKG_SH01A_V01_NAV");
        var bringFull = Assert.Single(row.Operations, item => item.Operation == "BRING_FULL");
        var bringEmpty = Assert.Single(row.Operations, item => item.Operation == "BRING_EMPTY");
        Assert.Contains("TASK_TEMPLATE_MISSING", bringFull.Blockers);
        Assert.Contains("PREPARATION_WORKSITE_MAPPING_MISSING", bringFull.Blockers);
        Assert.DoesNotContain("TASK_TEMPLATE_MISSING", bringEmpty.Blockers);
        Assert.Contains("EMPTY_SOURCE_MAPPING_MISSING", bringEmpty.Blockers);
        Assert.NotEqual("READY", bringFull.State);
        Assert.NotEqual("READY", bringEmpty.State);
        Assert.False(row.Ready);
        Assert.NotEqual("READY", row.OverallState);

        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'DRAFT', config_json = jsonb_set(config_json, '{geometryVerified}', 'false'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        var request = await InsertRequestAsync("BRING_EMPTY", "TKG_SH01A_V01_NAV", null);
        var execution = new PostgresRdsExecutionStore(dataSource);
        Assert.Equal("NAVIGATION_PROFILE_NOT_PUBLISHED", await execution.ReadRegistryDispatchBlockAsync(request));
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'PUBLISHED', config_json = jsonb_set(config_json, '{geometryVerified}', 'false'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        Assert.Equal("GEOMETRY_NOT_VERIFIED", await execution.ReadRegistryDispatchBlockAsync(request));
    }

    private async Task<(long SourceBacked, long ActiveTemplates)> TemplateCoverageAsync()
    {
        var covered = await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM (
                SELECT v.id
                FROM master.variants v
                JOIN master.variant_navigation_profile_assignments a ON a.variant_id = v.id
                JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id
                JOIN master.task_types tt ON tt.code IN ('BRING_FULL','BRING_EMPTY','REMOVE_FULL','REMOVE_EMPTY')
                JOIN master.task_templates t ON t.variant_id = v.id AND t.task_type_id = tt.id
                 AND t.is_active AND t.product_group_id IS NULL
                WHERE n.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED'
                  AND n.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
                GROUP BY v.id
                HAVING count(DISTINCT tt.code) = 4
            ) covered
            """);
        var templates = await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM master.task_templates t
            WHERE t.is_active
              AND t.product_group_id IS NULL
              AND t.config_json ->> 'templateRegistry' = 'tkg_variant_task_templates_v1'
            """);
        return (covered, templates);
    }

    private Task ActivateTemplatesAsync() => fixture.ExecuteAsync("""
        UPDATE master.task_templates
        SET is_active = true
        WHERE product_group_id IS NULL
          AND config_json ->> 'templateRegistry' = 'tkg_variant_task_templates_v1'
        """);

    private Task PublishAsync(string code) => fixture.ExecuteAsync("""
        UPDATE master.navigation_profiles
        SET status = 'PUBLISHED',
            published_at = COALESCE(published_at, now()),
            config_json = jsonb_set(
                jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb),
                '{commissioningState}', '"COMMISSIONED"')
        WHERE code = @code
        """, ("code", code));

    private Task<Guid> ProfileAsync(string code) => fixture.ScalarAsync<Guid>(
        "SELECT id FROM master.navigation_profiles WHERE code = @code", ("code", code));

    private Task<Guid> WorkSiteAsync(Guid profileId, string siteId) => fixture.ScalarAsync<Guid>("""
        SELECT id FROM integration.rds_worksites
        WHERE navigation_profile_id = @profile AND rds_site_id = @site
        """, ("profile", profileId), ("site", siteId));

    private Task DiscoverAsync(string navigationCode, string family, int ordinal) => fixture.ExecuteAsync("""
        INSERT INTO integration.rds_worksites
            (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
        SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
        FROM integration.rds_worksite_plans p
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
        WHERE n.code = @code AND p.object_family = @family AND p.ordinal = @ordinal
        ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
        SET rds_type = 1, last_sync_at = now(), last_disabled = 0, last_sync_failed = 0
        """, ("code", navigationCode), ("family", family), ("ordinal", ordinal));

    private Task MapPreparationAsync(string navigationCode, int area) => fixture.ExecuteAsync("""
        INSERT INTO integration.rds_worksites
            (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
        SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
        FROM integration.rds_worksite_plans p
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
        WHERE n.code = @code AND p.object_family = 'PREPARATION' AND p.ordinal = @area
        ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
        SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0, rds_type = 1;

        INSERT INTO integration.preparation_area_rds_mapping (preparation_area_id, rds_worksite_id)
        SELECT pa.id, w.id
        FROM ops.preparation_areas pa
        JOIN master.navigation_profiles n ON n.code = @code
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id
         AND p.planned_rds_site_id = w.rds_site_id AND p.ordinal = pa.area_number
        WHERE pa.area_number = @area
        ON CONFLICT (preparation_area_id, rds_worksite_id) DO NOTHING
        """, ("code", navigationCode), ("area", area));

    private Task BindAsync(Guid profileId, string endpointClass, string businessCode, Guid workSiteId, string? storeCode = null) =>
        fixture.ExecuteAsync("""
            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
            VALUES (@profile, @class, @business, (SELECT id FROM ops.store_locations WHERE code = @store), @site)
            """,
            ("profile", profileId),
            ("class", endpointClass),
            ("business", businessCode),
            ("store", storeCode),
            ("site", workSiteId));

    private async Task<Guid> InsertBringFullAsync(Guid userId, Guid deviceId, string navigationCode)
    {
        var requestId = await InsertRequestAsync("BRING_FULL", navigationCode, null);
        await fixture.ExecuteAsync("""
            INSERT INTO ops.preparation_pallets
                (preparation_area_id, container_type_id, load_state, part_id, transport_request_id,
                 placed_by, placed_device_id, placed_at, ready_at, ready_by)
            SELECT pa.id, ct.id, 'FULL', p.id, @request, @user, @device, stamp.ts, stamp.ts, @user
            FROM ops.preparation_areas pa
            JOIN master.container_types ct ON ct.is_active
            JOIN master.parts p ON p.is_active
            CROSS JOIN (SELECT clock_timestamp() AS ts) stamp
            WHERE pa.area_number = 1
            ORDER BY ct.id, p.id
            LIMIT 1
            """, ("request", requestId), ("user", userId), ("device", deviceId));
        return requestId;
    }

    private Task<Guid> InsertRequestAsync(string operation, string navigationCode, string? businessCode) =>
        fixture.ScalarAsync<Guid>("""
            INSERT INTO ops.transport_requests (
                idempotency_key, request_origin, machine_id, variant_id, layout_id, navigation_id,
                task_type_id, status, priority, destination_business_code)
            SELECT @key, 'API', m.id, v.id, l.id, n.id, tt.id, 'QUEUED', 100, @business
            FROM master.navigation_profiles n
            JOIN master.machines m ON m.id = n.machine_id
            JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
            JOIN master.layouts l ON l.machine_id = m.id AND l.status = 'PUBLISHED'
            JOIN master.task_types tt ON tt.code = @operation
            WHERE n.code = @code
            ORDER BY l.id
            LIMIT 1
            RETURNING id
            """,
            ("key", Guid.NewGuid().ToString("N")),
            ("code", navigationCode),
            ("operation", operation),
            ("business", businessCode));

    private static string SeedFile()
    {
        var migration = ApiTestFixture.MigrationFile("025_profile_scoped_shared_endpoints.sql");
        var databaseDirectory = Directory.GetParent(Path.GetDirectoryName(migration)!)!.FullName;
        return Path.Combine(databaseDirectory, "seeds", "generated", "005_tkg_variant_task_templates.sql");
    }
}
