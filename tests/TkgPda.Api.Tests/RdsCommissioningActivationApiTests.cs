using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class RdsCommissioningActivationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Null_navigation_stays_null_when_no_profile_is_published()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "2");
        using var client = await SupervisorClientAsync("SH-01-A");

        var response = await SwitchAsync(client, context, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT active_navigation_id IS NULL FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
    }

    [Fact]
    public async Task Null_navigation_selects_the_only_published_profile()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await PublishAsync("TKG_SH01A_V01_NAV");
        using var client = await SupervisorClientAsync("SH-01-A");

        (await SwitchAsync(client, context, null)).EnsureSuccessStatusCode();

        Assert.Equal("TKG_SH01A_V01_NAV", await ActiveNavigationCodeAsync(context.MachineId));
    }

    [Fact]
    public async Task Null_navigation_selects_the_single_published_default()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var preferred = await InsertPublishedProfileAsync("SH-01-A", "1", "TEST-NAV-ACT-DEFAULT", isDefault: true);
        await InsertPublishedProfileAsync("SH-01-A", "1", "TEST-NAV-ACT-OTHER", isDefault: false);
        using var client = await SupervisorClientAsync("SH-01-A");

        (await SwitchAsync(client, context, null)).EnsureSuccessStatusCode();

        Assert.Equal(preferred, await fixture.ScalarAsync<Guid>(
            "SELECT active_navigation_id FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
    }

    [Fact]
    public async Task Multiple_published_profiles_without_one_default_are_ambiguous()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await InsertPublishedProfileAsync("SH-01-A", "1", "TEST-NAV-ACT-A", isDefault: false);
        await InsertPublishedProfileAsync("SH-01-A", "1", "TEST-NAV-ACT-B", isDefault: false);
        using var client = await SupervisorClientAsync("SH-01-A");

        var response = await SwitchAsync(client, context, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NAVIGATION_PROFILE_AMBIGUOUS", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT active_navigation_id IS NULL FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
    }

    [Fact]
    public async Task Explicit_draft_profile_is_rejected()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var draft = await fixture.ScalarAsync<Guid>("SELECT id FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'");
        using var client = await SupervisorClientAsync("SH-01-A");

        var response = await SwitchAsync(client, context, draft);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NAVIGATION_PROFILE_CONFLICT", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Explicit_profile_from_another_machine_is_rejected()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await PublishAsync("TKG_GAR_V01_NAV");
        var other = await fixture.ScalarAsync<Guid>("SELECT id FROM master.navigation_profiles WHERE code = 'TKG_GAR_V01_NAV'");
        using var client = await SupervisorClientAsync("SH-01-A");

        var response = await SwitchAsync(client, context, other);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NAVIGATION_PROFILE_CONFLICT", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Shared_layout_v1_selects_the_v01_navigation_profile()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-P1--SH-01-P2", "1");
        await PublishAsync("TKG_SH01P1P2_V01_NAV");
        using var client = await SupervisorClientAsync("SH-01-P1--SH-01-P2");

        (await SwitchAsync(client, context, null)).EnsureSuccessStatusCode();

        Assert.Equal("TKG_SH01P1P2_V01_NAV", await ActiveNavigationCodeAsync(context.MachineId));
    }

    [Fact]
    public async Task Shared_layout_v2_selects_the_v02_navigation_profile()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-P1--SH-01-P2", "2");
        await PublishAsync("TKG_SH01P1P2_V02_NAV");
        using var client = await SupervisorClientAsync("SH-01-P1--SH-01-P2");

        (await SwitchAsync(client, context, null)).EnsureSuccessStatusCode();

        Assert.Equal("TKG_SH01P1P2_V02_NAV", await ActiveNavigationCodeAsync(context.MachineId));
    }

    [Fact]
    public async Task Transport_created_after_variant_selection_carries_the_active_navigation()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await PublishAsync("TKG_SH01A_V01_NAV");
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor, canChangeVariant: true);
        using var client = await fixture.LoginClientAsync(identity);
        (await SwitchAsync(client, context, null)).EnsureSuccessStatusCode();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var part = await fixture.PartForSlotAsync(slot, "P-ACT-NAV", context.VariantId);
        await fixture.ExecuteAsync("""
            INSERT INTO integration.rds_worksites
                (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
            SELECT id, 'TEST-NAV-ACT-EMPTY', 1, now(), 0, 0, '{}'::jsonb
            FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV';
            INSERT INTO integration.profile_shared_endpoint_bindings
                (navigation_profile_id, endpoint_class, business_code, rds_worksite_id)
            SELECT n.id, 'EMPTY_SOURCE', 'TEST-NAV-ACT-EMPTY', w.id
            FROM master.navigation_profiles n
            JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = 'TEST-NAV-ACT-EMPTY'
            WHERE n.code = 'TKG_SH01A_V01_NAV';
            """);

        var created = await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null, part, null, "BRING_EMPTY"),
            "activation-nav-request");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(await ProfileAsync("TKG_SH01A_V01_NAV"), await fixture.ScalarAsync<Guid>(
            "SELECT navigation_id FROM ops.transport_requests WHERE idempotency_key = 'activation-nav-request'"));
    }

    [Fact]
    public async Task Dispatch_uses_the_profile_scoped_worksite()
    {
        await fixture.ResetAsync();
        var slot = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var scoped = await InsertLiveSiteAsync(v1, "TEST-NAV-ACT-SCOPED");
        var legacy = await InsertLiveSiteAsync(v1, "TEST-NAV-ACT-LEGACY");
        await MapProfileAsync(slot, v1, scoped);
        await fixture.ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot, @site)",
            ("slot", slot),
            ("site", legacy));
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsExecutionStore(dataSource);

        var dispatch = await store.GetDispatchContextAsync(await InsertDispatchRequestAsync(slot, v1, "activation-scoped-dispatch"));

        Assert.Equal("TEST-NAV-ACT-SCOPED", dispatch!.Source.SiteId);
        Assert.NotEqual("TEST-NAV-ACT-LEGACY", dispatch.Source.SiteId);
    }

    [Fact]
    public async Task Missing_scoped_mapping_does_not_fall_back_to_another_profile()
    {
        await fixture.ResetAsync();
        var slot = await SharedSlotAsync();
        var v1 = await ProfileAsync("TKG_SH01P1P2_V01_NAV");
        var v2 = await ProfileAsync("TKG_SH01P1P2_V02_NAV");
        var foreign = await InsertLiveSiteAsync(v2, "TEST-NAV-ACT-FOREIGN");
        await fixture.ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot, @site)",
            ("slot", slot),
            ("site", foreign));
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsExecutionStore(dataSource);

        var dispatch = await store.GetDispatchContextAsync(await InsertDispatchRequestAsync(slot, v1, "activation-no-fallback"));

        Assert.False(dispatch!.Source.IsMapped);
        Assert.Null(dispatch.Source.SiteId);
    }

    [Fact]
    public async Task Direct_sql_rejects_a_cross_machine_live_mapping()
    {
        await fixture.ResetAsync();
        var slot = await MachineSlotAsync("SH-01-A", "CUSTOMER-PHYSICAL-V1");
        var other = await ProfileAsync("TKG_SH01B_V01_NAV");
        var site = await InsertLiveSiteAsync(other, "TEST-NAV-ACT-OTHER-MACHINE");

        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
            INSERT INTO integration.layout_slot_rds_profile_mapping
                (layout_slot_id, navigation_profile_id, rds_worksite_id)
            VALUES (@slot, @nav, @site)
            """, ("slot", slot), ("nav", other), ("site", site)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Direct_sql_rejects_a_cross_machine_planned_binding()
    {
        await fixture.ResetAsync();
        var slot = await MachineSlotAsync("SH-01-A", "CUSTOMER-PHYSICAL-V1");
        var other = await ProfileAsync("TKG_SH01B_V01_NAV");
        var plan = await fixture.ScalarAsync<Guid>("""
            SELECT id FROM integration.rds_worksite_plans
            WHERE navigation_profile_id = @nav
            ORDER BY ordinal
            LIMIT 1
            """, ("nav", other));

        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("""
            INSERT INTO integration.layout_slot_rds_plan_bindings
                (layout_slot_id, navigation_profile_id, worksite_plan_id, logical_slot_code)
            VALUES (@slot, @nav, @plan, 'SLOT-X')
            """, ("slot", slot), ("nav", other), ("plan", plan)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(9L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN master.navigation_profiles n ON n.id = b.navigation_profile_id
            WHERE n.code = 'TKG_SH01A_V01_NAV'
            """));
    }

    [Fact]
    public async Task Seed_replay_preserves_mapped_plan_state()
    {
        await fixture.ResetAsync();
        await SetPlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P01", "MAPPED", false);

        await fixture.ExecuteScriptAsync(SeedFile());

        Assert.Equal("MAPPED", await PlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P01"));
    }

    [Fact]
    public async Task Seed_replay_preserves_verified_plan_state()
    {
        await fixture.ResetAsync();
        await SetPlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P02", "VERIFIED", false);

        await fixture.ExecuteScriptAsync(SeedFile());

        Assert.Equal("VERIFIED", await PlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P02"));
    }

    [Fact]
    public async Task Seed_replay_preserves_commissioned_profile_state()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'PUBLISHED',
                published_at = now(),
                config_json = jsonb_set(config_json, '{commissioningState}', '"COMMISSIONED"'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        await SetPlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P03", "COMMISSIONED", false);

        await fixture.ExecuteScriptAsync(SeedFile());

        Assert.Equal("PUBLISHED", await fixture.ScalarAsync<string>("SELECT status FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
        Assert.Equal("COMMISSIONED", await fixture.ScalarAsync<string>("SELECT config_json ->> 'commissioningState' FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
        Assert.Equal("COMMISSIONED", await PlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P03"));
    }

    [Fact]
    public async Task Seed_replay_preserves_geometry_verified()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET config_json = jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        await SetPlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P01", "PLANNED", true);

        await fixture.ExecuteScriptAsync(SeedFile());

        Assert.True(await fixture.ScalarAsync<bool>("SELECT (config_json ->> 'geometryVerified')::boolean FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
        Assert.True(await fixture.ScalarAsync<bool>("""
            SELECT p.geometry_verified
            FROM integration.rds_worksite_plans p
            JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
            WHERE n.code = 'TKG_SH01A_V01_NAV' AND p.planned_rds_site_id = 'TKG_SH01A_V01_WS_P01'
            """));
    }

    [Fact]
    public async Task Exact_worksite_discovery_marks_only_the_matching_plan_mapped()
    {
        await fixture.ResetAsync();
        var profile = await ProfileAsync("TKG_SH01A_V01_NAV");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        var raw = JsonSerializer.Deserialize<JsonElement>("{}");

        await store.SyncWorkSitesAsync(profile, [
            new RdsWorkSiteSnapshot("TKG_SH01A_V01_WS_P01", null, null, 1, null, null, null, 0, 0, null, null, raw),
            new RdsWorkSiteSnapshot("TEST-NAV-ACT-UNEXPECTED", null, null, 1, null, null, null, 0, 0, null, null, raw)
        ], DateTimeOffset.UtcNow);

        Assert.Equal("MAPPED", await PlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P01"));
        Assert.Equal("PLANNED", await PlanStateAsync("TKG_SH01A_V01_NAV", "TKG_SH01A_V01_WS_P02"));
        Assert.Equal(2L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.rds_worksites WHERE navigation_profile_id = @id",
            ("id", profile)));
    }

    [Fact]
    public async Task Profile_mapping_rejects_a_planned_name_mismatch()
    {
        await fixture.ResetAsync();
        var profile = await ProfileAsync("TKG_SH01A_V01_NAV");
        var slot = await MachineSlotAsync("SH-01-A", "CUSTOMER-PHYSICAL-V1");
        var site = await InsertLiveSiteAsync(profile, "TEST-NAV-ACT-MISMATCH");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var error = await Assert.ThrowsAsync<RdsIntegrationRuleException>(() => store.SetProfileSlotMappingAsync(
            slot, profile, site, new AdminMutationContext(Guid.NewGuid(), Guid.NewGuid(), null, "test")));

        Assert.Equal("RDS_WORKSITE_PLAN_NAME_MISMATCH", error.Code);
    }

    [Fact]
    public async Task Verification_fails_while_a_required_binding_is_missing()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        await fixture.ExecuteAsync("""
            DELETE FROM integration.layout_slot_rds_profile_mapping map
            USING integration.layout_slot_rds_plan_bindings b, master.navigation_profiles n
            WHERE map.layout_slot_id = b.layout_slot_id
              AND map.navigation_profile_id = b.navigation_profile_id
              AND n.id = b.navigation_profile_id
              AND n.code = 'TKG_SH01A_V01_NAV'
              AND b.logical_slot_code = (
                  SELECT logical_slot_code
                  FROM integration.layout_slot_rds_plan_bindings
                  WHERE navigation_profile_id = n.id
                  ORDER BY logical_slot_code
                  LIMIT 1)
            """);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var decision = await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, UntrackedActor());

        Assert.False(decision.Applied);
        Assert.Contains("BINDING_INCOMPLETE", decision.Blockers);
        Assert.Equal("PLANNED", decision.CommissioningState);
    }

    [Fact]
    public async Task Successful_verification_reaches_verified_without_publishing()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = await ActorAsync();
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);

        var decision = await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor);

        Assert.True(decision.Applied);
        Assert.Empty(decision.Blockers);
        Assert.Equal("VERIFIED", decision.CommissioningState);
        Assert.Equal("DRAFT", decision.ProfileStatus);
        Assert.True(decision.GeometryVerified);
    }

    [Fact]
    public async Task Promotion_fails_before_the_profile_is_verified()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var decision = await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, UntrackedActor());

        Assert.False(decision.Applied);
        Assert.Contains("COMMISSIONING_NOT_VERIFIED", decision.Blockers);
        Assert.Equal("DRAFT", await fixture.ScalarAsync<string>("SELECT status FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
    }

    [Fact]
    public async Task Promotion_publishes_the_verified_profile_and_makes_it_the_variant_default()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.variant_navigation_profile_assignments a
            SET is_default = true
            FROM master.navigation_profiles n, master.variants v
            WHERE n.id = a.navigation_profile_id
              AND v.id = a.variant_id
              AND n.code = 'TKG_SH01A_V02_NAV'
              AND v.code = '2'
            """);
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = await ActorAsync();
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);

        var decision = await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, actor);

        Assert.True(decision.Applied);
        Assert.Equal("PUBLISHED", decision.ProfileStatus);
        Assert.Equal("COMMISSIONED", decision.CommissioningState);
        Assert.True(decision.IsDefault);
        Assert.True(decision.GeometryVerified);
        Assert.True(await fixture.ScalarAsync<bool>("""
            SELECT a.is_default
            FROM master.variant_navigation_profile_assignments a
            JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id
            JOIN master.variants v ON v.id = a.variant_id
            WHERE n.code = 'TKG_SH01A_V02_NAV' AND v.code = '2'
            """));
    }

    [Fact]
    public async Task Promotion_does_not_change_navigation_during_active_production()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        var v2 = await ProfileAsync("TKG_SH01A_V02_NAV");
        await fixture.ExecuteAsync(
            "UPDATE ops.machine_runtime_state SET active_navigation_id = @nav WHERE machine_id = @machine",
            ("nav", v2),
            ("machine", context.MachineId));
        await fixture.ExecuteAsync("""
            INSERT INTO ops.production_sessions
                (machine_id, variant_id, product_group_id, main_part_id, start_key, started_by, started_device_id)
            SELECT m.id, v.id, pg.id, pg.main_part_id, 'activation-live-session', @user, @device
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
            JOIN master.product_groups pg ON pg.variant_id = v.id AND pg.is_active
            WHERE m.code = 'SH-01-A'
            ORDER BY pg.display_order, pg.id
            LIMIT 1
            """, ("user", identity.UserId), ("device", identity.DeviceId));
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = new AdminMutationContext(identity.UserId, Guid.NewGuid(), null, "test");
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);

        Assert.True((await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);

        Assert.Equal(v2, await fixture.ScalarAsync<Guid>(
            "SELECT active_navigation_id FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
        Assert.Equal("ACTIVE", await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.production_sessions WHERE machine_id = @id AND ended_at IS NULL",
            ("id", context.MachineId)));
    }

    [Fact]
    public async Task Profile_scoped_mapping_writes_an_audit_row()
    {
        await fixture.ResetAsync();
        var profile = await ProfileAsync("TKG_SH01A_V01_NAV");
        var slot = await MachineSlotAsync("SH-01-A", "CUSTOMER-PHYSICAL-V1");
        var planned = await fixture.ScalarAsync<string>("""
            SELECT p.planned_rds_site_id
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
            WHERE b.layout_slot_id = @slot AND b.navigation_profile_id = @nav
            """, ("slot", slot), ("nav", profile));
        var site = await InsertLiveSiteAsync(profile, planned);
        var actor = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        await store.SetProfileSlotMappingAsync(
            slot, profile, site, new AdminMutationContext(actor.UserId, Guid.NewGuid(), null, "test"));

        Assert.Equal(1L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM audit.audit_logs
            WHERE action = 'RDS_PROFILE_WORKSITE_MAPPING_SET'
              AND entity_type = 'LAYOUT_SLOT_RDS_PROFILE_MAPPING'
            """));
    }

    [Fact]
    public async Task Sealer_v3_stays_pending_source_layout()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        using var client = await fixture.AdminLoginClientAsync(admin);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var report = await client.GetFromJsonAsync<VariantCommissioningReport[]>("/api/v1/admin/rds/commissioning/report");
        var sealer = Assert.Single(report!, row => row.MachineCode == "SEALER" && row.VariantCode == "3");
        var decision = await store.VerifyVariantCommissioningAsync("SEALER", "3", true, UntrackedActor());

        Assert.Equal(25, report!.Length);
        Assert.Equal("PENDING_SOURCE_LAYOUT", sealer.LayoutBindingState);
        Assert.False(sealer.Ready);
        Assert.Equal("TKG_SEALER_V03_NAV", sealer.NavigationCode);
        Assert.False(decision.Applied);
        Assert.Contains("PENDING_SOURCE_LAYOUT", decision.Blockers);
        Assert.Equal("PENDING_SOURCE_LAYOUT", await fixture.ScalarAsync<string>(
            "SELECT config_json ->> 'layoutBindingState' FROM master.navigation_profiles WHERE code = 'TKG_SEALER_V03_NAV'"));
    }

    [Fact]
    public async Task False_profile_geometry_blocks_verification()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var decision = await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, UntrackedActor());

        Assert.False(decision.Applied);
        Assert.Contains("GEOMETRY_NOT_VERIFIED", decision.Blockers);
        Assert.False(decision.GeometryVerified);
        Assert.Equal("DRAFT", decision.ProfileStatus);
    }

    [Fact]
    public async Task One_unverified_required_plan_blocks_verification()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        await InsertGateTemplateAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET config_json = jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV';
            UPDATE integration.rds_worksite_plans p
            SET geometry_verified = true
            FROM master.navigation_profiles n
            WHERE n.id = p.navigation_profile_id AND n.code = 'TKG_SH01A_V01_NAV';
            UPDATE integration.rds_worksite_plans p
            SET geometry_verified = false
            WHERE p.id = (
                SELECT plan.id
                FROM integration.rds_worksite_plans plan
                JOIN integration.layout_slot_rds_plan_bindings b ON b.worksite_plan_id = plan.id
                JOIN master.navigation_profiles n ON n.id = plan.navigation_profile_id
                WHERE n.code = 'TKG_SH01A_V01_NAV'
                ORDER BY plan.planned_rds_site_id
                LIMIT 1)
            """);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var decision = await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, UntrackedActor());

        Assert.False(decision.Applied);
        Assert.Contains("GEOMETRY_NOT_VERIFIED", decision.Blockers);
        Assert.Equal("DRAFT", decision.ProfileStatus);
    }

    [Fact]
    public async Task Geometry_confirmation_dry_run_changes_nothing()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var actor = await ActorAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var decision = await store.ConfirmVariantGeometryAsync("SH-01-A", "1", false, actor);

        Assert.False(decision.Applied);
        Assert.Empty(decision.Blockers);
        Assert.Equal("TKG_SH01A_V01_NAV", decision.NavigationCode);
        Assert.True(decision.RequiredPlanCount > 0);
        Assert.Equal(0, decision.ConfirmedCount);
        Assert.Equal(decision.RequiredPlanCount, decision.RemainingCount);
        Assert.False(await fixture.ScalarAsync<bool>("SELECT (config_json ->> 'geometryVerified')::boolean FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM integration.rds_worksite_plans p
            JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
            WHERE n.code = 'TKG_SH01A_V01_NAV' AND p.geometry_verified
            """));
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT active_navigation_id IS NULL FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'RDS_COMMISSIONING_GEOMETRY_CONFIRMED'"));
    }

    [Fact]
    public async Task Explicit_geometry_confirmation_records_required_evidence_only()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var actor = await ActorAsync();
        var worksitesBefore = await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var decision = await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor);

        Assert.True(decision.Applied);
        Assert.Equal(decision.RequiredPlanCount, decision.ConfirmedCount);
        Assert.Equal(0, decision.RemainingCount);
        Assert.True(await fixture.ScalarAsync<bool>("SELECT (config_json ->> 'geometryVerified')::boolean FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*)
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
            JOIN master.navigation_profiles n ON n.id = b.navigation_profile_id
            WHERE n.code = 'TKG_SH01A_V01_NAV' AND NOT p.geometry_verified
            """));
        Assert.Equal("DRAFT", await fixture.ScalarAsync<string>("SELECT status FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
        Assert.Equal(worksitesBefore, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT active_navigation_id IS NULL FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
    }

    [Fact]
    public async Task Verified_profile_cannot_promote_after_geometry_is_cleared()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = await ActorAsync();
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET config_json = jsonb_set(config_json, '{geometryVerified}', 'false'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);

        var decision = await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, actor);

        Assert.False(decision.Applied);
        Assert.Contains("GEOMETRY_NOT_VERIFIED", decision.Blockers);
        Assert.Equal("DRAFT", decision.ProfileStatus);
        Assert.Equal("VERIFIED", decision.CommissioningState);
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT active_navigation_id IS NULL FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", context.MachineId)));
    }

    [Fact]
    public async Task Ready_stays_false_while_geometry_is_false()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        await InsertGateTemplateAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET status = 'PUBLISHED',
                published_at = now(),
                config_json = jsonb_set(
                    jsonb_set(config_json, '{commissioningState}', '"COMMISSIONED"'::jsonb),
                    '{geometryVerified}', 'false'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);

        var row = Assert.Single(await store.ListVariantCommissioningAsync(), item => item.MachineCode == "SH-01-A" && item.VariantCode == "1");

        Assert.False(row.GeometryVerified);
        Assert.True(row.ActiveTaskTemplates > 0);
        Assert.Equal(0, row.MissingBindings);
        Assert.Equal("COMMISSIONED", row.CommissioningState);
        Assert.False(row.Ready);
    }

    [Fact]
    public async Task Ready_is_true_only_after_geometry_and_the_other_commissioning_gates()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = await ActorAsync();
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        var before = Assert.Single(await store.ListVariantCommissioningAsync(), item => item.NavigationCode == "TKG_SH01A_V01_NAV");
        Assert.False(before.Ready);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);

        var ready = Assert.Single(await store.ListVariantCommissioningAsync(), item => item.NavigationCode == "TKG_SH01A_V01_NAV");
        Assert.True(ready.GeometryVerified);
        Assert.True(ready.ActiveTaskTemplates > 0);
        Assert.Equal(0, ready.MissingBindings);
        Assert.Equal("PUBLISHED", ready.ProfileStatus);
        Assert.Equal("COMMISSIONED", ready.CommissioningState);
        Assert.True(ready.Ready);

        await fixture.ExecuteAsync("""
            UPDATE master.navigation_profiles
            SET config_json = jsonb_set(config_json, '{geometryVerified}', 'false'::jsonb)
            WHERE code = 'TKG_SH01A_V01_NAV'
            """);
        var cleared = Assert.Single(await store.ListVariantCommissioningAsync(), item => item.NavigationCode == "TKG_SH01A_V01_NAV");
        Assert.False(cleared.Ready);
    }

    [Fact]
    public async Task Missing_active_task_template_blocks_ready_verification_and_promotion()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteAsync("""
            UPDATE master.task_templates AS template
            SET is_active = false
            FROM master.variants AS variant
            JOIN master.machines AS machine ON machine.id = variant.machine_id
            WHERE template.variant_id = variant.id
              AND machine.code = 'SH-01-A'
              AND variant.code = '1'
            """);
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = await ActorAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);

        var verify = await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor);
        var promote = await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, actor);
        var row = Assert.Single(await store.ListVariantCommissioningAsync(), item => item.NavigationCode == "TKG_SH01A_V01_NAV");

        Assert.False(verify.Applied);
        Assert.Contains("ACTIVE_TASK_TEMPLATE_MISSING", verify.Blockers);
        Assert.False(promote.Applied);
        Assert.Contains("ACTIVE_TASK_TEMPLATE_MISSING", promote.Blockers);
        Assert.Equal(0, row.ActiveTaskTemplates);
        Assert.False(row.Ready);
        Assert.Equal("DRAFT", await fixture.ScalarAsync<string>("SELECT status FROM master.navigation_profiles WHERE code = 'TKG_SH01A_V01_NAV'"));
    }

    [Fact]
    public async Task Commissioning_mutations_write_audit_rows()
    {
        await fixture.ResetAsync();
        await BindRequiredAsync("TKG_SH01A_V01_NAV");
        var actor = await ActorAsync();
        await InsertGateTemplateAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        Assert.True((await store.ConfirmVariantGeometryAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.VerifyVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);
        Assert.True((await store.PromoteVariantCommissioningAsync("SH-01-A", "1", true, actor)).Applied);

        foreach (var action in new[] { "RDS_COMMISSIONING_GEOMETRY_CONFIRMED", "RDS_COMMISSIONING_VERIFIED", "RDS_COMMISSIONING_PROMOTED" })
        {
            Assert.Equal(1L, await fixture.ScalarAsync<long>("""
                SELECT count(*) FROM audit.audit_logs
                WHERE action = @action
                  AND actor_user_id = @actor
                  AND correlation_id = @correlation
                  AND entity_type = 'NAVIGATION_PROFILE'
                  AND after_json ->> 'navigationCode' = 'TKG_SH01A_V01_NAV'
                  AND after_json ->> 'machineCode' = 'SH-01-A'
                  AND after_json ->> 'variantCode' = '1'
                """, ("action", action), ("actor", actor.ActorUserId), ("correlation", actor.CorrelationId)));
        }
    }

    private async Task<HttpClient> SupervisorClientAsync(string machineCode)
    {
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor, canChangeVariant: true, machineCode: machineCode);
        return await fixture.LoginClientAsync(identity);
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient client, LayoutContext context, Guid? navigation) =>
        client.PostAsJsonAsync(
            $"/api/v1/machines/{context.MachineId}/variant-selection",
            new VariantSwitchHttpRequest(context.VariantId, context.LayoutId, navigation));

    private static AdminMutationContext UntrackedActor() => new(Guid.NewGuid(), Guid.NewGuid(), null, "test");

    private async Task<AdminMutationContext> ActorAsync()
    {
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        return new AdminMutationContext(identity.UserId, Guid.NewGuid(), null, "test");
    }

    private async Task InsertGateTemplateAsync()
    {
        await fixture.ExecuteAsync("""
            INSERT INTO master.task_templates
                (variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
            SELECT v.id, tt.id, 'TKG_NATIVE_' || tt.code, 100,
                   '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1","labelRole":"TECHNICAL_ONLY","testScope":"TEST-NAV-GATE"}'::jsonb,
                   true
            FROM master.variants v
            JOIN master.machines m ON m.id = v.machine_id
            JOIN master.task_types tt ON tt.code IN ('BRING_FULL', 'BRING_EMPTY', 'REMOVE_FULL', 'REMOVE_EMPTY')
            WHERE m.code = 'SH-01-A' AND v.code = '1'
              AND NOT EXISTS (
                  SELECT 1 FROM master.task_templates existing
                  WHERE existing.variant_id = v.id
                    AND existing.task_type_id = tt.id
                    AND existing.product_group_id IS NULL);

            UPDATE master.task_templates AS template
            SET is_active = true
            FROM master.variants AS variant
            JOIN master.machines AS machine ON machine.id = variant.machine_id
            JOIN master.task_types AS task_type ON task_type.code IN ('BRING_FULL', 'BRING_EMPTY', 'REMOVE_FULL', 'REMOVE_EMPTY')
            WHERE template.variant_id = variant.id
              AND template.task_type_id = task_type.id
              AND machine.code = 'SH-01-A'
              AND variant.code = '1'
              AND template.product_group_id IS NULL;
            """);
        await InstallSharedEndpointsAsync("TKG_SH01A_V01_NAV");
    }

    private Task InstallSharedEndpointsAsync(string navigationCode) => fixture.ExecuteAsync("""
        INSERT INTO integration.rds_worksites
            (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
        SELECT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
        FROM integration.rds_worksite_plans p
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
        WHERE n.code = @code
          AND (
              (p.object_family = 'PREPARATION' AND p.ordinal BETWEEN 1 AND 4)
              OR (p.object_family = 'EMPTY' AND p.ordinal = 1)
              OR (p.object_family = 'RETURN' AND p.ordinal = 1)
              OR (p.object_family = 'FINISHED_GOODS' AND p.ordinal = 1))
        ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
        SET rds_type = 1, last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

        INSERT INTO integration.preparation_area_rds_mapping (preparation_area_id, rds_worksite_id)
        SELECT pa.id, w.id
        FROM ops.preparation_areas pa
        JOIN integration.rds_worksite_plans p
          ON p.object_family = 'PREPARATION' AND p.ordinal = pa.area_number
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id AND n.code = @code
        JOIN integration.rds_worksites w
          ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE pa.area_number BETWEEN 1 AND 4
        ON CONFLICT (preparation_area_id, rds_worksite_id) DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'EMPTY_SOURCE', 'EMPTY-01', NULL, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE n.code = @code AND p.object_family = 'EMPTY' AND p.ordinal = 1
        ON CONFLICT (navigation_profile_id, endpoint_class, business_code) DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'EMPTY_RETURN', 'EMPTY-01', NULL, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE n.code = @code AND p.object_family = 'EMPTY' AND p.ordinal = 1
        ON CONFLICT (navigation_profile_id, endpoint_class, business_code) DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'NON_FINISHED_RETURN', 'RETURN-01', NULL, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        WHERE n.code = @code AND p.object_family = 'RETURN' AND p.ordinal = 1
        ON CONFLICT (navigation_profile_id, endpoint_class, business_code) DO NOTHING;

        INSERT INTO integration.profile_shared_endpoint_bindings
            (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
        SELECT n.id, 'FINISHED_GOODS', loc.code, loc.id, w.id
        FROM master.navigation_profiles n
        JOIN integration.rds_worksite_plans p ON p.navigation_profile_id = n.id
        JOIN integration.rds_worksites w ON w.navigation_profile_id = n.id AND w.rds_site_id = p.planned_rds_site_id
        JOIN ops.store_locations loc ON loc.code = 'FG-01'
        WHERE n.code = @code AND p.object_family = 'FINISHED_GOODS' AND p.ordinal = 1
        ON CONFLICT (navigation_profile_id, endpoint_class, business_code) DO NOTHING;
        """, ("code", navigationCode));

    private Task PublishAsync(string code) => fixture.ExecuteAsync("""
        UPDATE master.navigation_profiles
        SET status = 'PUBLISHED', published_at = COALESCE(published_at, now())
        WHERE code = @code
        """, ("code", code));

    private async Task<Guid> InsertPublishedProfileAsync(string machineCode, string variantCode, string code, bool isDefault)
    {
        var id = await fixture.ScalarAsync<Guid>("""
            INSERT INTO master.navigation_profiles
                (machine_id, code, name, version, status, published_at, robot_map_name, config_json)
            SELECT m.id, @code, @code, 1, 'PUBLISHED', now(), @code, '{}'::jsonb
            FROM master.machines m
            WHERE m.code = @machine
            RETURNING id
            """, ("code", code), ("machine", machineCode));
        await fixture.ExecuteAsync("""
            INSERT INTO master.variant_navigation_profile_assignments
                (variant_id, machine_id, navigation_profile_id, is_default)
            SELECT v.id, v.machine_id, @profile, @is_default
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id AND v.code = @variant
            WHERE m.code = @machine
            """, ("profile", id), ("is_default", isDefault), ("variant", variantCode), ("machine", machineCode));
        return id;
    }

    private Task<string> ActiveNavigationCodeAsync(Guid machineId) => fixture.ScalarAsync<string>("""
        SELECT n.code
        FROM ops.machine_runtime_state rs
        JOIN master.navigation_profiles n ON n.id = rs.active_navigation_id
        WHERE rs.machine_id = @id
        """, ("id", machineId));

    private Task<Guid> ProfileAsync(string code) => fixture.ScalarAsync<Guid>(
        "SELECT id FROM master.navigation_profiles WHERE code = @code",
        ("code", code));

    private Task<Guid> SharedSlotAsync() => fixture.ScalarAsync<Guid>("""
        SELECT s.id
        FROM master.layout_slots s
        JOIN master.layouts l ON l.id = s.layout_id
        JOIN master.machines m ON m.id = l.machine_id
        WHERE m.code = 'SH-01-P1--SH-01-P2'
          AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
          AND s.code = 'SLOT-001'
        """);

    private Task<Guid> MachineSlotAsync(string machineCode, string layoutCode) => fixture.ScalarAsync<Guid>("""
        SELECT s.id
        FROM master.layout_slots s
        JOIN master.layouts l ON l.id = s.layout_id
        JOIN master.machines m ON m.id = l.machine_id
        JOIN integration.layout_slot_rds_plan_bindings b ON b.layout_slot_id = s.id
        JOIN master.navigation_profiles n ON n.id = b.navigation_profile_id AND n.machine_id = m.id
        WHERE m.code = @machine AND l.code = @layout AND n.code LIKE '%_V01_NAV'
        ORDER BY s.code
        LIMIT 1
        """, ("machine", machineCode), ("layout", layoutCode));

    private Task<Guid> InsertLiveSiteAsync(Guid profileId, string siteId) => fixture.ScalarAsync<Guid>("""
        INSERT INTO integration.rds_worksites
            (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
        VALUES (@nav, @site, 1, now(), 0, 0, '{}'::jsonb)
        RETURNING id
        """, ("nav", profileId), ("site", siteId));

    private Task MapProfileAsync(Guid slotId, Guid profileId, Guid siteId) => fixture.ExecuteAsync("""
        INSERT INTO integration.layout_slot_rds_profile_mapping
            (layout_slot_id, navigation_profile_id, rds_worksite_id)
        VALUES (@slot, @nav, @site)
        """, ("slot", slotId), ("nav", profileId), ("site", siteId));

    private async Task<Guid> InsertDispatchRequestAsync(Guid slotId, Guid profileId, string key)
    {
        await fixture.ExecuteAsync("""
            INSERT INTO master.task_templates
                (variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
            SELECT v.id, tt.id, 'TEST-NAV-ACT', 10, '{"testScope":"TEST-NAV-ACT"}'::jsonb, true
            FROM master.variants v
            JOIN master.machines m ON m.id = v.machine_id
            JOIN master.task_types tt ON tt.code = 'REMOVE_FULL'
            WHERE m.code = 'SH-01-P1--SH-01-P2' AND v.code = '1'
              AND NOT EXISTS (
                  SELECT 1 FROM master.task_templates existing
                  WHERE existing.variant_id = v.id
                    AND existing.task_type_id = tt.id
                    AND existing.config_json ->> 'testScope' = 'TEST-NAV-ACT')
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

    private Task SetPlanStateAsync(string navigationCode, string siteId, string state, bool geometryVerified) =>
        fixture.ExecuteAsync("""
            UPDATE integration.rds_worksite_plans p
            SET commissioning_state = @state, geometry_verified = @geometry
            FROM master.navigation_profiles n
            WHERE n.id = p.navigation_profile_id
              AND n.code = @code
              AND p.planned_rds_site_id = @site
            """, ("state", state), ("geometry", geometryVerified), ("code", navigationCode), ("site", siteId));

    private Task<string> PlanStateAsync(string navigationCode, string siteId) => fixture.ScalarAsync<string>("""
        SELECT p.commissioning_state
        FROM integration.rds_worksite_plans p
        JOIN master.navigation_profiles n ON n.id = p.navigation_profile_id
        WHERE n.code = @code AND p.planned_rds_site_id = @site
        """, ("code", navigationCode), ("site", siteId));

    private Task BindRequiredAsync(string navigationCode) => fixture.ExecuteAsync("""
        INSERT INTO integration.rds_worksites
            (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
        SELECT b.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
        FROM integration.layout_slot_rds_plan_bindings b
        JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
        JOIN master.navigation_profiles n ON n.id = b.navigation_profile_id
        WHERE n.code = @code
        ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
        SET rds_type = 1, last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

        INSERT INTO integration.layout_slot_rds_profile_mapping
            (layout_slot_id, navigation_profile_id, rds_worksite_id)
        SELECT b.layout_slot_id, b.navigation_profile_id, w.id
        FROM integration.layout_slot_rds_plan_bindings b
        JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
        JOIN integration.rds_worksites w
          ON w.navigation_profile_id = b.navigation_profile_id
         AND w.rds_site_id = p.planned_rds_site_id
        JOIN master.navigation_profiles n ON n.id = b.navigation_profile_id
        WHERE n.code = @code
        ON CONFLICT (layout_slot_id, navigation_profile_id) DO UPDATE
        SET rds_worksite_id = EXCLUDED.rds_worksite_id;
        """, ("code", navigationCode));

    private static string SeedFile()
    {
        var migration = ApiTestFixture.MigrationFile("023_rds_variant_commissioning_hardening.sql");
        var databaseDirectory = Directory.GetParent(Path.GetDirectoryName(migration)!)!.FullName;
        return Path.Combine(databaseDirectory, "seeds", "generated", "003_tkg_rds_variant_commissioning.sql");
    }
}
