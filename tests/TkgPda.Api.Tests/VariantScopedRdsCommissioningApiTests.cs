using Npgsql;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class VariantScopedRdsCommissioningApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Dispatch_prefers_active_product_group_override_and_falls_back_to_generic_when_inactive()
    {
        await fixture.ResetAsync();
        var slot = await SharedSlotAsync();
        var profile = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var request = await InsertDispatchRequestAsync(slot, profile, "template-override");
        var group = await fixture.ScalarAsync<Guid>("SELECT id FROM master.product_groups WHERE variant_id = (SELECT variant_id FROM ops.transport_requests WHERE id = @id) ORDER BY id LIMIT 1", ("id", request));
        await fixture.ExecuteAsync("UPDATE ops.transport_requests SET product_group_id = @group WHERE id = @id", ("group", group), ("id", request));
        var specific = await fixture.ScalarAsync<Guid>("""
            INSERT INTO master.task_templates (variant_id, product_group_id, task_type_id, rds_task_label, priority, config_json)
            SELECT variant_id, @group, task_type_id, 'TEST-SPECIFIC', 7, '{"override":true}'::jsonb
            FROM ops.transport_requests WHERE id = @id RETURNING id
            """, ("group", group), ("id", request));
        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsExecutionStore(source);
        var selected = await store.GetDispatchContextAsync(request);
        Assert.Equal(specific, selected!.TaskTemplateId);
        Assert.Equal("TEST-SPECIFIC", selected.RdsTaskLabel);
        Assert.True(selected.TemplateConfig.GetProperty("override").GetBoolean());
        await fixture.ExecuteAsync("UPDATE master.task_templates SET is_active = false WHERE id = @id", ("id", specific));
        var fallback = await store.GetDispatchContextAsync(request);
        Assert.NotEqual(specific, fallback!.TaskTemplateId);
        Assert.True(await fixture.ScalarAsync<bool>("SELECT product_group_id IS NULL FROM master.task_templates WHERE id = @id", ("id", fallback.TaskTemplateId)));
    }

    [Fact]
    public async Task All_twenty_five_variants_have_one_distinct_planned_navigation_identity()
    {
        await fixture.ResetAsync();

        Assert.Equal(25L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """));
        Assert.Equal(25L, await fixture.ScalarAsync<long>("""
            SELECT count(DISTINCT robot_map_name)
            FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """));
        Assert.Equal(25L, await fixture.ScalarAsync<long>("""
            SELECT count(DISTINCT code)
            FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """));
        Assert.Equal(25L, await fixture.ScalarAsync<long>("""
            SELECT count(DISTINCT config_json ->> 'rdsAreaName')
            FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """));
        Assert.Equal(25L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM master.variant_navigation_profile_assignments a
            JOIN master.navigation_profiles np ON np.id = a.navigation_profile_id
            WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
              AND status <> 'DRAFT'
            """));
        Assert.Equal("TKG_SH01A_V01", await fixture.ScalarAsync<string>("""
            SELECT config_json ->> 'rdsAreaName'
            FROM master.navigation_profiles
            WHERE code = 'TKG_SH01A_V01_NAV'
            """));
        Assert.Equal("TKG_SH01A_V01_MAP", await fixture.ScalarAsync<string>("""
            SELECT robot_map_name FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'
            """));
    }

    [Fact]
    public async Task Shared_layout_slot_maps_to_distinct_worksites_for_each_navigation_profile()
    {
        await fixture.ResetAsync();
        var slotId = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var v2 = await ProfileAsync("TKG_SH01P1P2_V02_NAV");
        Assert.Equal("TKG_SH01P1P2_V01_WS_P01", await PlannedSiteAsync(slotId, v1));
        Assert.Equal("TKG_SH01P1P2_V02_WS_P01", await PlannedSiteAsync(slotId, v2));

        var v1Site = await InsertLiveSiteAsync(v1, "TEST-NAV-SCOPE-V1");
        var v2Site = await InsertLiveSiteAsync(v2, "TEST-NAV-SCOPE-V2");
        await MapProfileAsync(slotId, v1, v1Site);
        await MapProfileAsync(slotId, v2, v2Site);

        Assert.True(await ResolvesAsync(slotId, v1, v1Site));
        Assert.True(await ResolvesAsync(slotId, v2, v2Site));
        Assert.False(await ResolvesAsync(slotId, v1, v2Site));
        Assert.False(await ResolvesAsync(slotId, v2, v1Site));

        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsExecutionStore(dataSource);
        var v1Request = await InsertDispatchRequestAsync(slotId, v1, "test-nav-scope-v1");
        var v2Request = await InsertDispatchRequestAsync(slotId, v2, "test-nav-scope-v2");
        var v1Dispatch = await store.GetDispatchContextAsync(v1Request);
        var v2Dispatch = await store.GetDispatchContextAsync(v2Request);
        Assert.Equal("TEST-NAV-SCOPE-V1", v1Dispatch!.Source.SiteId);
        Assert.Equal("TEST-NAV-SCOPE-V2", v2Dispatch!.Source.SiteId);
        Assert.NotEqual(v1Dispatch.Source.SiteId, v2Dispatch.Source.SiteId);
    }

    [Fact]
    public async Task Cross_profile_worksite_mapping_is_rejected()
    {
        await fixture.ResetAsync();
        var slotId = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var v2 = await ProfileAsync("TKG_SH01P1P2_V02_NAV");
        var v2Site = await InsertLiveSiteAsync(v2, "TEST-NAV-SCOPE-CROSS");

        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
            INSERT INTO integration.layout_slot_rds_profile_mapping
                (layout_slot_id, navigation_profile_id, rds_worksite_id)
            VALUES (@slot, @nav, @site)
            """, ("slot", slotId), ("nav", v1), ("site", v2Site)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM integration.layout_slot_rds_profile_mapping
            WHERE layout_slot_id = @slot
            """, ("slot", slotId)));
    }

    [Fact]
    public async Task Explicit_navigation_does_not_accept_a_missing_or_foreign_legacy_mapping()
    {
        await fixture.ResetAsync();
        var slotId = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var v2 = await ProfileAsync("TKG_SH01P1P2_V02_NAV");
        Assert.True(await ResolvesNullAsync(slotId, v1));

        var nullProfileSite = await InsertLiveSiteAsync(null, "TEST-NAV-SCOPE-NULL");
        await fixture.ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot, @site)",
            ("slot", slotId), ("site", nullProfileSite));
        Assert.True(await ResolvesNullAsync(slotId, v1));
        Assert.True(await ResolvesAsync(slotId, null, nullProfileSite));

        await fixture.ExecuteAsync(
            "DELETE FROM integration.layout_slot_rds_mapping WHERE layout_slot_id = @slot",
            ("slot", slotId));
        var otherSite = await InsertLiveSiteAsync(v2, "TEST-NAV-SCOPE-OTHER");
        await fixture.ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot, @site)",
            ("slot", slotId), ("site", otherSite));
        Assert.True(await ResolvesNullAsync(slotId, v1));
        Assert.True(await ResolvesAsync(slotId, v2, otherSite));
    }

    [Fact]
    public async Task Legacy_mapping_still_resolves_when_its_worksite_belongs_to_the_request_profile()
    {
        await fixture.ResetAsync();
        var slotId = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var site = await InsertLiveSiteAsync(v1, "TEST-NAV-SCOPE-LEGACY");
        await fixture.ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot, @site)",
            ("slot", slotId), ("site", site));

        Assert.True(await ResolvesAsync(slotId, v1, site));
        Assert.True(await ResolvesAsync(slotId, null, site));
    }

    [Fact]
    public async Task Planned_names_are_not_live_worksites_and_do_not_satisfy_dispatch()
    {
        await fixture.ResetAsync();
        Assert.Equal(1775L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksite_plans"));
        Assert.Equal(20L, await FamilyCountAsync("MACHINE_POSITION"));
        Assert.Equal(8L, await FamilyCountAsync("PREPARATION"));
        Assert.Equal(8L, await FamilyCountAsync("EMPTY"));
        Assert.Equal(8L, await FamilyCountAsync("RETURN"));
        Assert.Equal(19L, await FamilyCountAsync("FINISHED_GOODS"));
        Assert.Equal(4L, await FamilyCountAsync("PARK"));
        Assert.Equal(4L, await FamilyCountAsync("CHARGE"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM integration.rds_worksite_plans p
            JOIN integration.rds_worksites w ON w.rds_site_id = p.planned_rds_site_id
            JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
            WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM integration.rds_worksite_plans
            WHERE commissioning_state <> 'PLANNED' OR geometry_verified
            """));

        var slotId = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsExecutionStore(dataSource);
        var requestId = await InsertDispatchRequestAsync(slotId, v1, "test-nav-scope-planned");
        var dispatch = await store.GetDispatchContextAsync(requestId);
        Assert.False(dispatch!.Source.IsMapped);
        Assert.Null(dispatch.Source.SiteId);
    }

    [Fact]
    public async Task Sealer_v3_reserves_its_map_without_invented_slot_bindings()
    {
        await fixture.ResetAsync();
        Assert.Equal("PENDING_SOURCE_LAYOUT", await fixture.ScalarAsync<string>("""
            SELECT config_json ->> 'layoutBindingState'
            FROM master.navigation_profiles
            WHERE code = 'TKG_SEALER_V03_NAV'
            """));
        Assert.Equal("TKG_SEALER_V03_MAP", await fixture.ScalarAsync<string>("""
            SELECT robot_map_name FROM master.navigation_profiles WHERE code = 'TKG_SEALER_V03_NAV'
            """));
        Assert.Equal("DRAFT", await fixture.ScalarAsync<string>("""
            SELECT status FROM master.navigation_profiles WHERE code = 'TKG_SEALER_V03_NAV'
            """));
        Assert.Equal(71L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM integration.rds_worksite_plans p
            JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
            WHERE np.code = 'TKG_SEALER_V03_NAV'
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id
            WHERE np.code = 'TKG_SEALER_V03_NAV'
            """));
    }

    private Task<long> FamilyCountAsync(string family) => fixture.ScalarAsync<long>("""
        SELECT count(*)
        FROM integration.rds_worksite_plans p
        JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
        WHERE np.code = 'TKG_SH01A_V01_NAV' AND p.object_family = @family
        """, ("family", family));

    private Task<Guid> SharedSlotAsync() => fixture.ScalarAsync<Guid>("""
        SELECT s.id
        FROM master.layout_slots s
        JOIN master.layouts l ON l.id = s.layout_id
        JOIN master.machines m ON m.id = l.machine_id
        WHERE m.code = 'SH-01-P1--SH-01-P2'
          AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
          AND s.code = 'SLOT-001'
        """);

    private Task<Guid> ProfileAsync(string code) => fixture.ScalarAsync<Guid>(
        "SELECT id FROM master.navigation_profiles WHERE code = @code",
        ("code", code));

    private Task<string> PlannedSiteAsync(Guid slotId, Guid profileId) => fixture.ScalarAsync<string>("""
        SELECT p.planned_rds_site_id
        FROM integration.layout_slot_rds_plan_bindings b
        JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
        WHERE b.layout_slot_id = @slot AND b.navigation_profile_id = @nav
        """, ("slot", slotId), ("nav", profileId));

    private Task<Guid> InsertLiveSiteAsync(Guid? profileId, string siteId) => fixture.ScalarAsync<Guid>("""
        INSERT INTO integration.rds_worksites (navigation_profile_id, rds_site_id, rds_type, last_sync_at, raw_state_json)
        VALUES (@nav, @site, 1, now(), '{}'::jsonb)
        RETURNING id
        """, ("nav", profileId), ("site", siteId));

    private Task MapProfileAsync(Guid slotId, Guid profileId, Guid siteId) => fixture.ExecuteAsync("""
        INSERT INTO integration.layout_slot_rds_profile_mapping
            (layout_slot_id, navigation_profile_id, rds_worksite_id)
        VALUES (@slot, @nav, @site)
        """, ("slot", slotId), ("nav", profileId), ("site", siteId));

    private Task<bool> ResolvesAsync(Guid slotId, Guid? profileId, Guid siteId) => fixture.ScalarAsync<bool>("""
        SELECT integration.resolve_slot_worksite(@slot, @nav::uuid) IS NOT DISTINCT FROM @site::uuid
        """, ("slot", slotId), ("nav", profileId), ("site", siteId));

    private Task<bool> ResolvesNullAsync(Guid slotId, Guid profileId) => fixture.ScalarAsync<bool>("""
        SELECT integration.resolve_slot_worksite(@slot, @nav::uuid) IS NULL
        """, ("slot", slotId), ("nav", profileId));

    private async Task<Guid> InsertDispatchRequestAsync(Guid slotId, Guid profileId, string key)
    {
        await fixture.ExecuteAsync("""
            INSERT INTO master.task_templates (
                variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
            SELECT v.id, tt.id, 'TEST-NAV-SCOPE', 10, '{"testScope":"TEST-NAV-SCOPE"}'::jsonb, true
            FROM master.variants v
            JOIN master.machines m ON m.id = v.machine_id
            JOIN master.task_types tt ON tt.code = 'REMOVE_FULL'
            WHERE m.code = 'SH-01-P1--SH-01-P2' AND v.code = '1'
              AND NOT EXISTS (
                  SELECT 1 FROM master.task_templates existing
                  WHERE existing.variant_id = v.id
                    AND existing.task_type_id = tt.id
                    AND existing.config_json ->> 'testScope' = 'TEST-NAV-SCOPE')
            """);
        return await fixture.ScalarAsync<Guid>("""
            INSERT INTO ops.transport_requests (
                idempotency_key, request_origin, machine_id, variant_id, layout_id, navigation_id,
                task_type_id, source_slot_id, status, priority)
            SELECT @key, 'API', m.id, v.id, l.id, @nav, tt.id, @slot, 'QUEUED', 10
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
            JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
            JOIN master.task_types tt ON tt.code = 'REMOVE_FULL'
            WHERE m.code = 'SH-01-P1--SH-01-P2'
            RETURNING id
            """, ("key", key), ("nav", profileId), ("slot", slotId));
    }
}
