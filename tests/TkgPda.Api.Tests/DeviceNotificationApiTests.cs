using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class FinishedGoodsAreasApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Two_delivery_locations_and_six_preparation_areas_survive_seed_and_migration_replay()
    {
        await fixture.ResetAsync();
        var worksitesBefore = await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites");
        await fixture.ExecuteScriptAsync(SeedFile("004_finished_goods_internal_locations.sql"));
        await AssertFinishedGoodsLayoutAsync();
        var identity = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.store_locations WHERE code = 'FG-01'");

        await fixture.ExecuteAsync(
            "UPDATE ops.store_locations SET display_name = @name WHERE code = 'FG-01'",
            ("name", "TEST-DISPLAY-KEEP"));
        await fixture.ExecuteScriptAsync(MigrationFile("016_finished_goods_areas.sql"));
        await fixture.ExecuteScriptAsync(SeedFile("004_finished_goods_internal_locations.sql"));

        await AssertFinishedGoodsLayoutAsync();
        Assert.Equal(identity, await fixture.ScalarAsync<Guid>("SELECT id FROM ops.store_locations WHERE code = 'FG-01'"));
        Assert.Equal("Mamul Teslim Alanı 1", await fixture.ScalarAsync<string>(
            "SELECT display_name FROM ops.store_locations WHERE code = 'FG-01'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_locations WHERE code LIKE 'FG_AREA_%'"));
        Assert.Equal(worksitesBefore, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_worksites"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM ops.store_location_delivery_slots d
            JOIN ops.store_locations l ON l.id = d.store_location_id
            WHERE l.code ~ '^FG-[0-9]{2}$'
            """));
    }

    [Fact]
    public async Task Finished_goods_operator_sees_two_delivery_locations_and_empty_record_is_not_a_delivery()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteScriptAsync(SeedFile("004_finished_goods_internal_locations.sql"));
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.FinishedGoodsWarehouseOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: false);
        using var client = await fixture.LoginClientAsync(identity);
        var listed = await client.GetFromJsonAsync<StoreLocationProbe[]>("/api/v1/warehouse/store/locations");
        var production = listed!.Where(location => System.Text.RegularExpressions.Regex.IsMatch(location.Code, "^FG-[0-9]{2}$")).ToArray();

        Assert.Equal(2, production.Length);
        Assert.Equal(new[] { "FG-01", "FG-02" }, production.Select(location => location.Code).OrderBy(code => code));
        Assert.All(production, location =>
        {
            Assert.True(location.IsEnabled);
            Assert.Equal(1, location.Capacity);
            Assert.Empty(location.Units);
        });

        var target = production.Single(location => location.Code == "FG-02");
        var transportsBefore = await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/store/locations/{target.Id}/empty-container");
        request.Headers.Add(ApiHeaders.IdempotencyKey, "fg-empty-" + Guid.NewGuid());
        var recorded = await client.SendAsync(request);
        var body = await recorded.Content.ReadAsStringAsync();
        Assert.True(recorded.StatusCode == HttpStatusCode.OK, body);

        Assert.Equal("EMPTY", await fixture.ScalarAsync<string>(
            """
            SELECT load_state FROM ops.store_units
            WHERE store_location_id = @id AND removed_at IS NULL
            """,
            ("id", target.Id)));
        Assert.True(await fixture.ScalarAsync<bool>(
            """
            SELECT transport_request_id IS NULL FROM ops.store_units
            WHERE store_location_id = @id AND removed_at IS NULL
            """,
            ("id", target.Id)));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*) FROM ops.store_events
            WHERE store_location_id = @id AND event_type = 'EMPTY_CONTAINER_RECORDED'
            """,
            ("id", target.Id)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*) FROM ops.store_events
            WHERE store_location_id = @id AND event_type = 'UNIT_DELIVERED'
            """,
            ("id", target.Id)));
        Assert.Equal(transportsBefore, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    private async Task AssertFinishedGoodsLayoutAsync()
    {
        Assert.Equal(2L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_locations WHERE code ~ '^FG-[0-9]{2}$'"));
        Assert.Equal(2L, await fixture.ScalarAsync<long>(
            "SELECT count(DISTINCT code) FROM ops.store_locations WHERE code ~ '^FG-[0-9]{2}$'"));
        for (var index = 1; index <= 2; index++)
        {
            var code = $"FG-{index:00}";
            Assert.True(await fixture.ScalarAsync<bool>(
                "SELECT capacity = 1 FROM ops.store_locations WHERE code = @code",
                ("code", code)));
            Assert.True(await fixture.ScalarAsync<bool>(
                "SELECT is_enabled FROM ops.store_locations WHERE code = @code",
                ("code", code)));
            Assert.True(await fixture.ScalarAsync<bool>(
                "SELECT area_purpose = 'DELIVERY' AND is_operator_visible FROM ops.store_locations WHERE code = @code",
                ("code", code)));
        }

        Assert.Equal(6L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM ops.preparation_areas
            WHERE warehouse_scope = 'FINISHED_GOODS' AND area_number BETWEEN 5 AND 10 AND is_enabled
            """));
        Assert.Equal("Mamul Teslim Alanı 2", await fixture.ScalarAsync<string>(
            "SELECT display_name FROM ops.store_locations WHERE code = 'FG-02'"));
    }

    private sealed record StoreLocationProbe(Guid Id, string Code, string DisplayName, int? Capacity, bool IsEnabled, StoreUnitProbe[] Units);

    private sealed record StoreUnitProbe(string LoadState);

    private static string MigrationFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "db", "migrations", name)))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException(name)
            : Path.Combine(directory.FullName, "db", "migrations", name);
    }

    private static string SeedFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "db", "seeds", name)))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException(name)
            : Path.Combine(directory.FullName, "db", "seeds", name);
    }
}

public sealed class DeviceNotificationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Registration_stores_only_the_credential_hash_for_the_authenticated_device()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.FinishedGoodsWarehouseOperator, machineCode: "SH-01-A", bindDeviceToMachine: false);
        using var client = await fixture.LoginClientAsync(identity);
        var response = await client.PutAsJsonAsync("/api/v1/auth/device-notifications/register", new
        {
            userId = Guid.NewGuid(),
            deviceId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DeviceNotificationRegistrationResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Credential));

        var storedUser = await fixture.ScalarAsync<Guid>("SELECT user_id FROM integration.device_notification_registrations");
        var storedDevice = await fixture.ScalarAsync<Guid>("SELECT device_id FROM integration.device_notification_registrations");
        var storedHash = await fixture.ScalarAsync<string>("SELECT encode(credential_hash, 'hex') FROM integration.device_notification_registrations");
        Assert.Equal(identity.UserId, storedUser);
        Assert.Equal(identity.DeviceId, storedDevice);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Credential))).ToLowerInvariant(), storedHash);
        Assert.DoesNotContain(body.Credential, storedHash, StringComparison.Ordinal);

        var rotated = await client.PutAsJsonAsync("/api/v1/auth/device-notifications/register", new { });
        var second = await rotated.Content.ReadFromJsonAsync<DeviceNotificationRegistrationResponse>();
        Assert.NotNull(second);
        Assert.NotEqual(body.Credential, second.Credential);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.device_notification_registrations"));
        using var stale = fixture.Client();
        stale.DefaultRequestHeaders.Add(ApiHeaders.NotificationCredential, body.Credential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/device-notifications/next")).StatusCode);
    }

    [Fact]
    public async Task Unsupported_role_cannot_register()
    {
        await fixture.ResetAsync();
        var line = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(line);
        var response = await client.PutAsync("/api/v1/auth/device-notifications/register", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.device_notification_registrations"));
    }

    [Fact]
    public async Task Explicit_logout_revokes_registration_and_lease_expiry_does_not()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.FinishedGoodsWarehouseOperator, machineCode: "SH-01-A", bindDeviceToMachine: false);
        using var client = await fixture.LoginClientAsync(identity);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/v1/auth/device-notifications/register", null)).StatusCode);
        await fixture.ExecuteAsync(
            "UPDATE auth.user_sessions SET lease_expires_at = clock_timestamp() - interval '1 minute' WHERE user_id = @user",
            ("user", identity.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT is_active FROM integration.device_notification_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));

        await fixture.ExecuteAsync(
            "UPDATE auth.user_sessions SET lease_expires_at = clock_timestamp() + interval '5 minutes' WHERE user_id = @user",
            ("user", identity.UserId));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.False(await fixture.ScalarAsync<bool>(
            "SELECT is_active FROM integration.device_notification_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT revoked_at IS NOT NULL FROM integration.device_notification_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
    }

    [Fact]
    public async Task Completion_notifies_only_eligible_registrations_once()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await RegisterAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        var legacy = await RegisterAsync(RoleCodes.WarehouseOperator);
        var admin = await RegisterAsync(RoleCodes.Admin);
        var supervisor = await RegisterAsync(RoleCodes.Supervisor);
        var semi = await RegisterAsync(RoleCodes.SemiFinishedWarehouseOperator);
        await RegisterAsync(RoleCodes.LineOperator);
        var requestId = await CreateRemoveFullAsync(context, "wp09r-boundary");

        Assert.Equal(0L, await OutboxCountAsync());
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var workflow = new TransportWorkflowService(store);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        Assert.Equal(0L, await OutboxCountAsync());
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
        Assert.Equal(4L, await OutboxCountAsync());
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
        Assert.Equal(4L, await OutboxCountAsync());

        using var client = NotificationClient(finished.Credential);
        var first = await client.GetAsync("/api/v1/device-notifications/next");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var delivered = await first.Content.ReadFromJsonAsync<DeviceNotificationResponse>();
        Assert.NotNull(delivered);
        Assert.StartsWith("Kasa Koyuldu — ", delivered.Title);
        Assert.Contains("Talep #", delivered.Body);
        Assert.Equal(FinishedGoodsNotificationCopy.NotificationType, delivered.NotificationType);
        Assert.DoesNotContain(delivered.Data["unitId"], delivered.Title);
        Assert.Equal(FinishedGoodsNotificationCopy.EventKey(Guid.Parse(delivered.Data["unitId"])), delivered.EventKey);

        using var concurrent = NotificationClient(finished.Credential);
        Assert.Equal(HttpStatusCode.NoContent, (await concurrent.GetAsync("/api/v1/device-notifications/next")).StatusCode);

        await fixture.ExecuteAsync(
            "UPDATE integration.notification_outbox SET lease_until = clock_timestamp() - interval '1 second' WHERE id = @id",
            ("id", delivered.Id));
        using var again = NotificationClient(finished.Credential);
        var replay = await again.GetFromJsonAsync<DeviceNotificationResponse>("/api/v1/device-notifications/next");
        Assert.Equal(delivered.Id, replay!.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await again.PostAsync($"/api/v1/device-notifications/{delivered.Id}/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await again.PostAsync($"/api/v1/device-notifications/{delivered.Id}/ack", null)).StatusCode);
        using var other = NotificationClient(legacy.Credential);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/device-notifications/{delivered.Id}/ack", null)).StatusCode);

        await fixture.ExecuteAsync(
            """
            DELETE FROM auth.user_roles WHERE user_id = @user;
            INSERT INTO auth.user_roles (user_id, role_id)
            SELECT @user, id FROM auth.roles WHERE code = 'LINE_OPERATOR'
            """,
            ("user", admin.Identity.UserId));
        using var revoked = NotificationClient(admin.Credential);
        Assert.Equal(HttpStatusCode.Forbidden, (await revoked.GetAsync("/api/v1/device-notifications/next")).StatusCode);

        await fixture.ExecuteAsync("UPDATE auth.users SET is_active = false WHERE id = @user", ("user", supervisor.Identity.UserId));
        using var disabledUser = NotificationClient(supervisor.Credential);
        Assert.Equal(HttpStatusCode.Forbidden, (await disabledUser.GetAsync("/api/v1/device-notifications/next")).StatusCode);

        await fixture.ExecuteAsync("UPDATE audit.client_devices SET is_active = false WHERE id = @device", ("device", semi.Identity.DeviceId));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*) FROM integration.notification_outbox o
            JOIN integration.device_notification_registrations r ON r.id = o.registration_id
            WHERE r.device_id = @device
            """,
            ("device", semi.Identity.DeviceId)));
    }

    [Fact]
    public async Task Disabled_device_and_other_operations_do_not_deliver()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var registered = await RegisterAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        await fixture.ExecuteAsync(
            "UPDATE audit.client_devices SET is_active = false WHERE id = @device",
            ("device", registered.Identity.DeviceId));
        await CreateRemoveFullAsync(context, "wp09r-disabled-device");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var workflow = new TransportWorkflowService(store);
        var requestId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = @key",
            ("key", "wp09r-disabled-device"));
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
        Assert.Equal(0L, await OutboxCountAsync());

        var active = await RegisterAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 3);
        var line = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        await using var otherStore = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var service = new TransportWorkflowService(otherStore);

        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        var bringEmpty = await service.CreateAsync(new CreateTransportRequest(
            $"wp09r-bring-empty-{Guid.NewGuid():N}",
            RequestOrigin.Api,
            line.UserId,
            context.MachineId,
            context.VariantId,
            context.LayoutId,
            null,
            null,
            await fixture.PartForSlotAsync(slots[0], "WP09R-BE"),
            null,
            TransportTaskType.BringEmpty,
            null,
            slots[0],
            50,
            RequiresPreparation: false));
        await CompleteAsync(service, bringEmpty.Request.Id);

        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        var bringFull = await service.CreateAsync(new CreateTransportRequest(
            $"wp09r-bring-full-{Guid.NewGuid():N}",
            RequestOrigin.Api,
            line.UserId,
            context.MachineId,
            context.VariantId,
            context.LayoutId,
            null,
            null,
            await fixture.PartForSlotAsync(slots[1], "WP09R-BF"),
            null,
            TransportTaskType.BringFull,
            null,
            slots[1],
            50,
            RequiresPreparation: false));
        await CompleteAsync(service, bringFull.Request.Id);

        await fixture.SetSlotAsync(slots[2], "OCCUPIED", "EMPTY");
        var removeEmpty = await service.CreateAsync(new CreateTransportRequest(
            $"wp09r-remove-empty-{Guid.NewGuid():N}",
            RequestOrigin.Api,
            line.UserId,
            context.MachineId,
            context.VariantId,
            context.LayoutId,
            null,
            null,
            await fixture.PartForSlotAsync(slots[2], "WP09R-RE"),
            null,
            TransportTaskType.RemoveEmpty,
            slots[2],
            null,
            50,
            RequiresPreparation: false));
        await CompleteAsync(service, removeEmpty.Request.Id);

        Assert.Equal(0L, await OutboxCountAsync());
        using var client = NotificationClient(active.Credential);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/device-notifications/next")).StatusCode);
    }

    private async Task<Registered> RegisterAsync(string role)
    {
        var identity = await fixture.SeedIdentityAsync(
            role: role,
            machineCode: "SH-01-A",
            bindDeviceToMachine: role == RoleCodes.LineOperator);
        using var client = await fixture.LoginClientAsync(identity);
        var response = await client.PutAsync("/api/v1/auth/device-notifications/register", null);
        if (role is RoleCodes.LineOperator or RoleCodes.SemiFinishedWarehouseOperator)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            return new Registered(identity, "");
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DeviceNotificationRegistrationResponse>();
        return new Registered(identity, body!.Credential);
    }

    private async Task<Guid> CreateRemoveFullAsync(LayoutContext context, string key)
    {
        var line = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(line);
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1))[0];
        var code = $"WP09R-{key}-{Guid.NewGuid():N}";
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
        await fixture.ExecuteAsync(
            """
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state, current_part_id)
            VALUES (@slot_id, 'OCCUPIED', 'FULL', @part_id)
            ON CONFLICT (layout_slot_id) DO UPDATE
            SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = EXCLUDED.current_part_id
            """,
            ("slot_id", slot),
            ("part_id", part));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                context.VariantId, context.LayoutId, null, group, part, 1, "FULL"))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = @key",
            ("key", key));
    }

    private static async Task CompleteAsync(TransportWorkflowService service, Guid requestId)
    {
        await service.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
    }

    private Task<long> OutboxCountAsync() =>
        fixture.ScalarAsync<long>("SELECT count(*) FROM integration.notification_outbox");

    private HttpClient NotificationClient(string credential)
    {
        var client = fixture.Client();
        client.DefaultRequestHeaders.Add(ApiHeaders.NotificationCredential, credential);
        return client;
    }

    private sealed record Registered(TestIdentity Identity, string Credential);
}
