using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;

namespace TkgPda.Api.Tests;

public sealed class ApiTestFixture : WebApplicationFactory<Program>
{
    public const string CallbackKey = "phase08b-test-callback-key-0000000000000000";
    public TestRdsReadClient RdsClient { get; } = new();
    public TestRdsCoreDiscoveryClient RdsCoreDiscoveryClient { get; } = new();

    public string ConnectionString => Environment.GetEnvironmentVariable("TKG_PDA_API_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException("TKG_PDA_API_TEST_CONNECTION_STRING is required.");

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Rds:CallbackKey", CallbackKey);
        builder.UseSetting("Cors:AllowedOrigins:0", "https://operator.example.test");
        builder.UseSetting("RdsCoreExecution:Enabled", "false");
        builder.UseSetting("Authentication:Jwt:ExpiryMinutes", "15");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRdsReadClient>();
            services.AddSingleton<IRdsReadClient>(RdsClient);
            services.RemoveAll<IRdsCoreDiscoveryClient>();
            services.AddSingleton<IRdsCoreDiscoveryClient>(RdsCoreDiscoveryClient);
        });
    }

    public async Task ResetAsync()
    {
        RdsClient.Reset();
        RdsCoreDiscoveryClient.Reset();
        await ExecuteAsync(
            """
            TRUNCATE
                ops.store_events,
                ops.store_units,
                integration.rds_tasks,
                ops.transport_request_events,
                ops.transport_requests,
                ops.transport_batches,
                ops.slot_runtime_state,
                ops.machine_runtime_state
            RESTART IDENTITY CASCADE;
            DELETE FROM audit.audit_logs;
            DELETE FROM integration.layout_slot_rds_mapping
            WHERE rds_worksite_id IN (
                SELECT id FROM integration.rds_worksites WHERE rds_site_id LIKE 'P07A-%');
            DELETE FROM integration.rds_worksites WHERE rds_site_id LIKE 'P07A-%';
            DELETE FROM integration.layout_slot_rds_mapping
            WHERE rds_worksite_id IN (
                SELECT id FROM integration.rds_worksites WHERE rds_site_id LIKE 'P08A-%');
            DELETE FROM integration.rds_worksites WHERE rds_site_id LIKE 'P08A-%';
            DELETE FROM integration.layout_slot_rds_mapping
            WHERE rds_worksite_id IN (
                SELECT id FROM integration.rds_worksites WHERE area = 'OPTIMAK_SIM_2026');
            DELETE FROM integration.rds_worksites WHERE area = 'OPTIMAK_SIM_2026';
            DELETE FROM master.navigation_profiles WHERE code LIKE 'DEV-RDS-SIM-%';
            DELETE FROM master.navigation_profiles WHERE code LIKE 'P08A-%';
            DELETE FROM master.task_templates
            WHERE config_json ->> 'testScope' = 'P07A';
            DELETE FROM auth.user_sessions;
            DELETE FROM auth.user_machine_permissions;
            DELETE FROM auth.user_roles;
            DELETE FROM auth.users;
            DELETE FROM audit.client_devices;
            DELETE FROM master.layouts WHERE code LIKE 'P05-%';
            DELETE FROM master.variants WHERE code LIKE 'P05-%';
            DELETE FROM master.product_group_parts
            WHERE part_id IN (SELECT id FROM master.parts WHERE part_code LIKE 'P05-%');
            DELETE FROM master.product_groups
            WHERE main_part_id IN (SELECT id FROM master.parts WHERE part_code LIKE 'P05-%');
            DELETE FROM master.parts WHERE part_code LIKE 'P05-%';
            DELETE FROM master.container_types WHERE code LIKE 'P05-%';
            DELETE FROM master.variants WHERE code LIKE 'P02-%';
            DELETE FROM master.product_group_parts
            WHERE part_id IN (SELECT id FROM master.parts WHERE part_code LIKE 'P02-%');
            DELETE FROM master.product_groups
            WHERE main_part_id IN (SELECT id FROM master.parts WHERE part_code LIKE 'P02-%');
            DELETE FROM master.parts WHERE part_code LIKE 'P02-%';
            DELETE FROM master.variants WHERE code LIKE 'P03A-%';
            DELETE FROM master.product_group_parts
            WHERE part_id IN (SELECT id FROM master.parts WHERE part_code LIKE 'P03A-%');
            DELETE FROM master.product_groups
            WHERE main_part_id IN (SELECT id FROM master.parts WHERE part_code LIKE 'P03A-%');
            DELETE FROM master.parts WHERE part_code LIKE 'P03A-%';
            """);
    }

    public async Task<TestIdentity> SeedIdentityAsync(
        string role = RoleCodes.LineOperator,
        bool active = true,
        bool deviceActive = true,
        bool canView = true,
        bool canOperate = true,
        bool canChangeVariant = false,
        string? machineCode = "SH-01-A",
        string? employeeNo = null,
        bool bindDeviceToMachine = true,
        string? deviceBoundMachineCode = null,
        bool includeUserMachinePermissions = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var username = $"user-{suffix}";
        var password = "Test-password-42!";
        var deviceCode = $"PDA-{suffix}";
        var employeeNumber = employeeNo ?? $"E-{suffix}";
        var hasher = new Pbkdf2PasswordHasher();
        var userId = await ScalarAsync<Guid>(
            """
            INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
            VALUES (@username, @password_hash, @full_name, @employee_no, @active)
            RETURNING id
            """,
            ("username", username),
            ("password_hash", hasher.Hash(password)),
            ("full_name", $"Test User {suffix}"),
            ("employee_no", employeeNumber),
            ("active", active));
        await ExecuteAsync(
            """
            INSERT INTO auth.user_roles (user_id, role_id)
            SELECT @user_id, id FROM auth.roles WHERE code = @role
            """,
            ("user_id", userId),
            ("role", role));

        Guid? machineId = null;
        if (machineCode is not null)
        {
            machineId = await ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = @code", ("code", machineCode));
            if (includeUserMachinePermissions)
            {
                await ExecuteAsync(
                    """
                    INSERT INTO auth.user_machine_permissions (
                        user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
                    VALUES (@user_id, @machine_id, @can_view, @can_operate, @can_change_variant, false)
                    """,
                    ("user_id", userId),
                    ("machine_id", machineId),
                    ("can_view", canView),
                    ("can_operate", canOperate),
                    ("can_change_variant", canChangeVariant));
            }
        }

        var isWarehouseRole = role is RoleCodes.WarehouseOperator
            or RoleCodes.SemiFinishedWarehouseOperator
            or RoleCodes.FinishedGoodsWarehouseOperator;

        Guid? deviceBoundMachineId = null;
        if (deviceBoundMachineCode is not null)
        {
            deviceBoundMachineId = await ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = @code", ("code", deviceBoundMachineCode));
        }
        else if (bindDeviceToMachine && !isWarehouseRole && machineCode is not null)
        {
            deviceBoundMachineId = machineId.HasValue
                ? machineId
                : await ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = @code", ("code", machineCode));
        }

        var deviceId = await ScalarAsync<Guid>(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, @name, 'PDA', @active, @machine_id)
            RETURNING id
            """,
            ("code", deviceCode),
            ("name", $"Test PDA {suffix}"),
            ("active", deviceActive),
            ("machine_id", (object?)deviceBoundMachineId ?? DBNull.Value));

        return new TestIdentity(userId, username, password, deviceId, deviceCode, machineId, employeeNumber);
    }

    public async Task<HttpClient> LoginClientAsync(TestIdentity identity)
    {
        var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(identity.Username, identity.Password))
        };
        request.Headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>()
            ?? throw new InvalidOperationException("Login response was empty.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        client.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        return client;
    }

    public async Task<HttpClient> AdminLoginClientAsync(TestIdentity identity)
    {
        var client = Client();
        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/auth/login",
            new LoginRequest(identity.Username, identity.Password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<AdminLoginResponse>()
            ?? throw new InvalidOperationException("Admin login response was empty.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    public async Task<LayoutContext> ConfigureMachineAsync(
        string machineCode = "SH-01-A",
        string variantCode = "1")
    {
        var context = await ContextAsync(machineCode, variantCode);
        await ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
            VALUES (@machine_id, @variant_id, @layout_id, NULL)
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = NULL,
                row_version = ops.machine_runtime_state.row_version + 1,
                updated_at = now()
            """,
            ("machine_id", context.MachineId),
            ("variant_id", context.VariantId),
            ("layout_id", context.LayoutId));
        await EnsureDefaultBufferSlotsAsync(context.LayoutId);
        await EnsureDefaultFinishedGoodsDestinationAsync(context.LayoutId);
        return context;
    }

    public const string FinishedGoodsTestDropCode = "FG-TEST-DROP";
    public const string FinishedGoodsTestLocationCode = "TEST-FG-API";

    /// <summary>
    /// WP-08: Kasa Doldu is refused unless the DROP the server would actually
    /// resolve is linked to an enabled store location. Customer layouts in the
    /// API suite have no DROP role (simulation DROP-001 is seed 002d, not
    /// loaded here), so a test-only DROP is inserted when the layout has none.
    /// The mapping then follows the same first-enabled-DROP rule as dispatch.
    /// Customer names, the 19 FG areas and capacity are not modelled.
    /// </summary>
    private async Task EnsureDefaultFinishedGoodsDestinationAsync(Guid layoutId)
    {
        await ExecuteAsync(
            """
            UPDATE master.layout_slots SET is_enabled = true
            WHERE layout_id = @layout_id AND slot_role = 'DROP';
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            SELECT @layout_id, @code, 'Test Finished Goods Drop', 'DROP', 9001
            WHERE NOT EXISTS (
                SELECT 1 FROM master.layout_slots
                WHERE layout_id = @layout_id AND slot_role = 'DROP');
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
            SELECT id, 'VACANT', 'UNKNOWN' FROM master.layout_slots
            WHERE layout_id = @layout_id AND slot_role = 'DROP'
            ON CONFLICT (layout_slot_id) DO NOTHING;
            INSERT INTO ops.store_locations (code, display_name, capacity)
            VALUES (@location_code, 'Test Finished Goods Location', NULL)
            ON CONFLICT (code) DO UPDATE SET is_enabled = true;
            INSERT INTO ops.store_location_delivery_slots (layout_slot_id, store_location_id)
            SELECT drop_slot.id, sl.id
            FROM (
                SELECT id FROM master.layout_slots
                WHERE layout_id = @layout_id AND slot_role = 'DROP' AND is_enabled
                ORDER BY display_order, id
                LIMIT 1
            ) drop_slot
            CROSS JOIN ops.store_locations sl
            WHERE sl.code = @location_code
            ON CONFLICT (layout_slot_id) DO UPDATE SET store_location_id = EXCLUDED.store_location_id;
            """,
            ("layout_id", layoutId),
            ("code", FinishedGoodsTestDropCode),
            ("location_code", FinishedGoodsTestLocationCode));
    }

    public Task<Guid> FinishedGoodsDropSlotAsync(Guid layoutId) => ScalarAsync<Guid>(
        """
        SELECT id FROM master.layout_slots
        WHERE layout_id = @layout AND slot_role = 'DROP' AND is_enabled
        ORDER BY display_order, id
        LIMIT 1
        """,
        ("layout", layoutId));

    /// <summary>
    /// Customer Workflow — Empty Container Buffer: every BRING_EMPTY/
    /// REMOVE_EMPTY request now requires a configured EMPTY_STORAGE buffer
    /// slot in the right state (see PostgresTransportWorkflowStore.
    /// AllocateBufferSlotAsync). Pre-provisions a small pool of ready-to-bring
    /// (OCCUPIED/EMPTY) and ready-to-receive (VACANT/UNKNOWN) buffer slots on
    /// the layout — enough for concurrent-request tests to each get their
    /// own — with a high display_order that keeps them out of the way of
    /// LogisticsSlotsAsync's ordered slot picks, so every existing test that
    /// omits an explicit source/destination for those operations keeps
    /// working exactly as before via auto-allocation.
    /// </summary>
    private async Task EnsureDefaultBufferSlotsAsync(Guid layoutId)
    {
        const int poolSize = 6;
        for (var i = 1; i <= poolSize; i++)
        {
            var bringBuffer = await EnsureLayoutSlotAsync(layoutId, $"BUFFER-TEST-BRING-{i}", $"Test Buffer Bring {i}");
            await SetSlotAsync(bringBuffer, "OCCUPIED", "EMPTY");
            var removeBuffer = await EnsureLayoutSlotAsync(layoutId, $"BUFFER-TEST-REMOVE-{i}", $"Test Buffer Remove {i}");
            await SetSlotAsync(removeBuffer, "VACANT", "UNKNOWN");
        }
    }

    private async Task<Guid> EnsureLayoutSlotAsync(Guid layoutId, string code, string displayName)
    {
        await ExecuteAsync(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            SELECT @layout_id, @code, @display_name, 'EMPTY_STORAGE', 9000
            WHERE NOT EXISTS (
                SELECT 1 FROM master.layout_slots WHERE layout_id = @layout_id AND code = @code
            )
            """,
            ("layout_id", layoutId),
            ("code", code),
            ("display_name", displayName));
        return await ScalarAsync<Guid>(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND code = @code",
            ("layout_id", layoutId),
            ("code", code));
    }

    public async Task<LayoutContext> ContextAsync(string machineCode, string variantCode)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT m.id, v.id, a.layout_id
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id AND v.code = @variant_code
            JOIN master.variant_layout_assignments a ON a.variant_id = v.id AND a.machine_id = m.id
            JOIN master.layouts l ON l.id = a.layout_id AND l.status = 'PUBLISHED'
            WHERE m.code = @machine_code
            ORDER BY a.is_default DESC, l.code
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("machine_code", machineCode);
        command.Parameters.AddWithValue("variant_code", variantCode);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Context not found for {machineCode}/{variantCode}.");
        }

        return new LayoutContext(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }

    public Task<Guid[]> LogisticsSlotsAsync(Guid layoutId, int count) => QueryGuidsAsync(
        """
        SELECT id FROM master.layout_slots
        WHERE layout_id = @layout_id AND slot_role <> 'MACHINE' AND is_enabled
        ORDER BY display_order, code
        LIMIT @count
        """,
        ("layout_id", layoutId),
        ("count", count));

    /// <summary>
    /// Creates a dedicated part and maps it to exactly one logistics slot, then
    /// returns the part id.
    ///
    /// The Operator HTTP contract no longer accepts a source or destination slot —
    /// a normal Operator expresses machine -> part -> operation and the backend
    /// resolves the physical position from master.layout_slot_parts. Tests therefore
    /// address a slot the same way the real PDA does: by naming its part. Giving each
    /// slot its own part keeps resolution deterministic, so assertions that expect a
    /// specific slot still hold.
    /// </summary>
    public async Task<Guid> PartForSlotAsync(Guid slotId, string partCode, Guid? variantId = null)
    {
        await ExecuteAsync(
            """
            INSERT INTO master.parts (part_code, description, is_active)
            VALUES (@part_code, 'API test part', true)
            ON CONFLICT (part_code) DO NOTHING
            """,
            ("part_code", partCode));
        var partId = await ScalarAsync<Guid>(
            "SELECT id FROM master.parts WHERE part_code = @part_code",
            ("part_code", partCode));
        await ExecuteAsync(
            """
            INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
            VALUES (@slot_id, @part_id)
            ON CONFLICT (layout_slot_id, part_id) DO NOTHING
            """,
            ("slot_id", slotId),
            ("part_id", partId));

        // A transport part must belong to the active variant (PART_VARIANT_CONFLICT),
        // so attach it to that variant's first product group exactly as customer
        // master data would.
        var variant = variantId ?? await ScalarAsync<Guid>(
            """
            SELECT v.id
            FROM master.layout_slots ls
            JOIN master.variant_layout_assignments a ON a.layout_id = ls.layout_id
            JOIN master.variants v ON v.id = a.variant_id
            WHERE ls.id = @slot_id
            ORDER BY a.is_default DESC
            LIMIT 1
            """,
            ("slot_id", slotId));
        // A freshly created test variant may have no product group at all; give it one
        // whose main part is this part, exactly as customer master data would.
        await ExecuteAsync(
            """
            INSERT INTO master.product_groups (variant_id, main_part_id, name, display_order, is_active)
            SELECT @variant_id, @part_id, 'API test group', 900, true
            WHERE NOT EXISTS (
                SELECT 1 FROM master.product_groups pg
                WHERE pg.variant_id = @variant_id AND pg.is_active)
            """,
            ("part_id", partId),
            ("variant_id", variant));
        await ExecuteAsync(
            """
            INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type, is_active)
            SELECT pg.id, @part_id, 'RELATED', true
            FROM master.product_groups pg
            WHERE pg.variant_id = @variant_id AND pg.is_active
              AND NOT EXISTS (
                  SELECT 1 FROM master.product_group_parts x
                  WHERE x.product_group_id = pg.id AND x.part_id = @part_id)
            ORDER BY pg.display_order, pg.id
            LIMIT 1
            """,
            ("part_id", partId),
            ("variant_id", variant));
        return partId;
    }

    public Task SetSlotAsync(Guid slotId, string occupancy, string load) => ExecuteAsync(
        """
        INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
        VALUES (@slot_id, @occupancy, @load)
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = EXCLUDED.occupancy_state,
            load_state = EXCLUDED.load_state,
            current_part_id = NULL,
            current_container_type_id = NULL,
            row_version = ops.slot_runtime_state.row_version + 1,
            updated_at = now()
        """,
        ("slot_id", slotId),
        ("occupancy", occupancy),
        ("load", load));

    public async Task<HttpResponseMessage> SendTransportAsync(
        HttpClient client,
        CreateTransportHttpRequest body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        return await client.SendAsync(request);
    }

    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is T typed)
        {
            return typed;
        }

        if (typeof(T) == typeof(DateTimeOffset) && value is DateTime dt)
        {
            return (T)(object)new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        throw new InvalidOperationException($"Scalar query did not return {typeof(T).Name}.");
    }

    private async Task<Guid[]> QueryGuidsAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        var values = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetGuid(0));
        }

        return values.ToArray();
    }

    private static void AddParameters(NpgsqlCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }
}

public sealed record TestIdentity(
    Guid UserId,
    string Username,
    string Password,
    Guid DeviceId,
    string DeviceCode,
    Guid? MachineId,
    string EmployeeNo);

public sealed record LayoutContext(Guid MachineId, Guid VariantId, Guid LayoutId);

public sealed class TestRdsReadClient : IRdsReadClient
{
    public bool Enabled { get; set; } = true;
    public string? SafeBaseAddress => "http://rds.test:8080";
    public DateTimeOffset? LastSuccessfulRead { get; private set; }
    public List<string> Calls { get; } = [];
    public IReadOnlyList<RdsWorkSiteSnapshot> WorkSites { get; set; } = [];
    public RdsRobotAlarmReport RobotAlarms { get; set; } = new([], []);
    public bool RobotAlarmsUnavailable { get; set; }

    public void Reset()
    {
        Enabled = true;
        LastSuccessfulRead = null;
        Calls.Clear();
        WorkSites = [];
        RobotAlarms = new([], []);
        RobotAlarmsUnavailable = false;
    }

    public Task<RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>> GetWorkSitesAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("/api/work-sites/sites");
        LastSuccessfulRead = DateTimeOffset.UtcNow;
        return Task.FromResult(new RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>(WorkSites, 200, "Success", "/api/work-sites/sites", correlationId, LastSuccessfulRead.Value));
    }

    public Task<RdsReadResult<RdsWindTaskPage>> QueryWindTasksAsync(RdsWindTaskQuery query, Guid correlationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("/api/queryWindTask");
        LastSuccessfulRead = DateTimeOffset.UtcNow;
        return Task.FromResult(new RdsReadResult<RdsWindTaskPage>(new(0, query.CurrentPage, query.PageSize, 0, []), 200, "Success", "/api/queryWindTask", correlationId, LastSuccessfulRead.Value));
    }

    public Task<RdsReadResult<RdsRobotAlarmReport>> GetRobotAlarmsAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("/api/agv-report/core");
        if (RobotAlarmsUnavailable)
        {
            throw new RdsReadException("RDS_UNAVAILABLE", "RDS read endpoint is unavailable.", "/api/agv-report/core", correlationId);
        }

        LastSuccessfulRead = DateTimeOffset.UtcNow;
        return Task.FromResult(new RdsReadResult<RdsRobotAlarmReport>(RobotAlarms, 200, "Success", "/api/agv-report/core", correlationId, LastSuccessfulRead.Value));
    }
}

public sealed class TestRdsCoreDiscoveryClient : IRdsCoreDiscoveryClient
{
    public bool Enabled { get; set; }
    public string? SafeOrigin => Enabled ? "https://rdscore.test:8088" : null;
    public int Calls { get; private set; }

    public void Reset()
    {
        Enabled = false;
        Calls = 0;
    }

    public Task<RdsCommissioningSnapshot> CaptureAsync(
        RdsCoreCaptureCommand command,
        Guid correlationId,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new RdsCommissioningSnapshot(
            "rds_commissioning_snapshot_v1",
            capturedAtUtc,
            correlationId,
            new(SafeOrigin!, null, [], null, null),
            [],
            [],
            [],
            [],
            new([], [], [], []),
            [],
            new("NOT_CAPTURED", new Dictionary<string, JsonElement>()),
            [],
            []));
    }
}
