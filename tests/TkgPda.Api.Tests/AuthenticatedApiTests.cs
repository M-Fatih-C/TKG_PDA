using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class AuthenticatedApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Health_endpoints_are_anonymous_and_database_ready()
    {
        await fixture.ResetAsync();
        using var client = fixture.Client();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("ready", await ready.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_returns_bound_jwt_exact_roles_and_current_user()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        using var client = fixture.Client();
        var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(identity.Username, identity.Password))
        };
        loginRequest.Headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        var response = await client.SendAsync(loginRequest);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(login);
        Assert.Equal("Bearer", login.TokenType);
        Assert.Equal([RoleCodes.LineOperator], login.User.Roles);
        Assert.Equal(identity.DeviceCode, login.Device.Code);
        Assert.InRange(login.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        client.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(identity.Username, await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'AUTH_LOGIN_SUCCEEDED'"));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT last_login_at IS NOT NULL FROM auth.users WHERE id = @id", ("id", identity.UserId)));
    }

    [Fact]
    public async Task Invalid_credentials_return_generic_error_and_are_audited()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        using var client = fixture.Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(identity.Username, "wrong-password"))
        };
        request.Headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'AUTH_LOGIN_FAILED'"));
    }

    [Fact]
    public async Task Login_rate_limit_returns_stable_problem_code()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        using var client = fixture.Client();
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt < 11; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest(identity.Username, "wrong-password"))
            };
            request.Headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
            response = await client.SendAsync(request);
        }

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("LOGIN_RATE_LIMITED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Inactive_device_cannot_login()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(deviceActive: false);
        using var client = fixture.Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(identity.Username, identity.Password))
        };
        request.Headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DEVICE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    // ---- WP-01A: employee / registry number (sicil) operator login ----

    private async Task<HttpResponseMessage> PostLoginAsync(string? deviceCode, LoginRequest body)
    {
        var client = fixture.Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(body)
        };
        if (deviceCode is not null)
        {
            request.Headers.Add(ApiHeaders.DeviceCode, deviceCode);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Employee_number_login_succeeds_and_returns_device_bound_session()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, identity.EmployeeNo));
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(login);
        Assert.Equal(identity.UserId, login.User.Id);
        Assert.Equal(identity.EmployeeNo, login.User.EmployeeNo);
        Assert.Equal([RoleCodes.LineOperator], login.User.Roles);
        Assert.Equal(identity.DeviceCode, login.Device.Code);

        var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        client.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(ClientTypes.DeviceBound, me.GetProperty("clientType").GetString());
    }

    [Fact]
    public async Task Employee_number_login_is_case_insensitive_and_trims_whitespace()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(employeeNo: "ab001");

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, "  AB001  "));
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(identity.UserId, login!.User.Id);
        Assert.Equal("ab001", login.User.EmployeeNo);
    }

    [Fact]
    public async Task Employee_number_login_preserves_significant_leading_zeros()
    {
        await fixture.ResetAsync();
        var padded = await fixture.SeedIdentityAsync(employeeNo: "001245");
        var unpadded = await fixture.SeedIdentityAsync(employeeNo: "1245");

        var paddedLogin = await (await PostLoginAsync(padded.DeviceCode, new LoginRequest(null, padded.Password, "001245")))
            .Content.ReadFromJsonAsync<LoginResponse>();
        var unpaddedLogin = await (await PostLoginAsync(unpadded.DeviceCode, new LoginRequest(null, unpadded.Password, "1245")))
            .Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(padded.UserId, paddedLogin!.User.Id);
        Assert.Equal(unpadded.UserId, unpaddedLogin!.User.Id);
        Assert.NotEqual(paddedLogin.User.Id, unpaddedLogin.User.Id);
    }

    [Fact]
    public async Task Employee_number_login_unknown_number_is_generic_credentials_error()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, "E-does-not-exist"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Employee_number_login_succeeds_without_a_password()
    {
        // Production customer requirement: the sicil path is passwordless.
        // Identity is established by employeeNumber + an authorized device;
        // the employee number itself is not a secret.
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, null, identity.EmployeeNo));
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(identity.UserId, login!.User.Id);
    }

    [Fact]
    public async Task Employee_number_login_does_not_verify_a_supplied_password()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, "definitely-the-wrong-password", identity.EmployeeNo));
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(identity.UserId, login!.User.Id);
    }

    [Fact]
    public async Task Username_login_without_a_password_is_rejected()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(identity.Username, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_LOGIN_REQUEST", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Employee_number_login_inactive_user_is_rejected()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(active: false);

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, identity.EmployeeNo));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Employee_number_login_requires_authorized_device()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(deviceActive: false);

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, identity.EmployeeNo));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DEVICE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Login_with_both_username_and_employee_number_is_ambiguous()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(identity.Username, identity.Password, identity.EmployeeNo));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("AMBIGUOUS_LOGIN_IDENTITY", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Login_with_no_identity_is_rejected()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_LOGIN_REQUEST", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Login_with_blank_employee_number_and_no_username_is_rejected()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(null, identity.Password, "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_LOGIN_REQUEST", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Legacy_username_operator_login_remains_supported()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();

        var response = await PostLoginAsync(identity.DeviceCode, new LoginRequest(identity.Username, identity.Password));
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(identity.UserId, login!.User.Id);
        Assert.Equal(identity.DeviceCode, login.Device.Code);
    }

    [Fact]
    public async Task Protected_endpoints_require_authentication()
    {
        await fixture.ResetAsync();
        using var client = fixture.Client();

        var response = await client.GetAsync("/api/v1/machines");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("AUTHENTICATION_REQUIRED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Token_is_rejected_when_device_header_changes()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);
        client.DefaultRequestHeaders.Remove(ApiHeaders.DeviceCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, "PDA-DIFFERENT");

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DEVICE_CONTEXT_MISMATCH", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Machine_permission_filters_list_and_blocks_context()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(machineCode: null);
        var machineId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-A'");
        using var client = await fixture.LoginClientAsync(identity);

        var list = await client.GetFromJsonAsync<MachineSummaryRecord[]>("/api/v1/machines");
        var context = await client.GetAsync($"/api/v1/machines/{machineId}/operational-context");

        Assert.Empty(list ?? []);
        Assert.Equal(HttpStatusCode.Forbidden, context.StatusCode);
        var errorCode = await ApiTestFixture.ErrorCodeAsync(context);
        Assert.True(errorCode is "MACHINE_ACCESS_DENIED" or "DEVICE_MACHINE_BINDING_REQUIRED");
    }

    [Fact]
    public async Task Admin_sees_all_nine_machine_masters_without_explicit_rows()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: null);
        using var client = await fixture.LoginClientAsync(identity);

        var machines = await client.GetFromJsonAsync<MachineSummaryRecord[]>("/api/v1/machines");

        Assert.Equal(9, machines?.Length);
        Assert.All(machines ?? [], machine => Assert.True(machine.Permissions.CanOperate));
    }

    [Fact]
    public async Task Operational_context_exposes_runtime_variants_geometry_and_non_transport_machine_region()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        var context = await fixture.ConfigureMachineAsync();
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync($"/api/v1/machines/{context.MachineId}/operational-context");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(context.VariantId, root.GetProperty("activeVariantId").GetGuid());
        Assert.Equal(context.LayoutId, root.GetProperty("activeLayoutId").GetGuid());
        Assert.NotEmpty(root.GetProperty("variantLayouts").EnumerateArray());
        var machineRegion = root.GetProperty("slots").EnumerateArray()
            .Single(slot => slot.GetProperty("role").GetString() == "MACHINE");
        Assert.False(machineRegion.GetProperty("transportEligible").GetBoolean());
    }

    [Fact]
    public async Task Transport_http_idempotency_returns_created_replay_and_conflict()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(identity);
        var body = Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-AUTHEN1"));

        var created = await fixture.SendTransportAsync(client, body, "http-idempotency");
        var replay = await fixture.SendTransportAsync(client, body, "http-idempotency");
        var conflict = await fixture.SendTransportAsync(client, Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-AUTHEN2")), "http-idempotency");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(ApiHeaders.IdempotencyReplayed).Single());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(conflict));
        Assert.Equal("API", await fixture.ScalarAsync<string>("SELECT request_origin FROM ops.transport_requests LIMIT 1"));
        Assert.Equal(identity.UserId, await fixture.ScalarAsync<Guid>("SELECT requested_by FROM ops.transport_requests LIMIT 1"));
    }

    [Fact]
    public async Task Operate_permission_is_required_even_for_operator_role()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(canOperate: false);
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await fixture.SendTransportAsync(client, Bring(context, await fixture.PartForSlotAsync(slot, "P05-AUTHEN3")), "operate-denied");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("MACHINE_ACCESS_DENIED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Queued_transport_cancels_locally_and_dispatched_transport_reports_pending_Rds_confirmation()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(identity);

        var createdResponse = await fixture.SendTransportAsync(client, Bring(context, await fixture.PartForSlotAsync(slots[0], "P05-AUTHEN4")), "cancel-http");
        var created = await createdResponse.Content.ReadFromJsonAsync<TransportHttpResponse>();
        var cancelled = await client.PostAsync($"/api/v1/transport-requests/{created!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Contains("CANCELLED", await cancelled.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var lateResponse = await fixture.SendTransportAsync(client, Bring(context, await fixture.PartForSlotAsync(slots[1], "P05-AUTHEN5")), "cancel-late-http");
        var late = await lateResponse.Content.ReadFromJsonAsync<TransportHttpResponse>();
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'SENT_TO_RDS' WHERE id = @id",
            ("id", late!.Id));
        await fixture.ExecuteAsync(
            "INSERT INTO integration.rds_tasks (transport_request_id, rds_task_record_id, rds_task_label) VALUES (@id, @record_id, 'SIM_TASK')",
            ("id", late.Id),
            ("record_id", late.Id.ToString()));
        var pending = await client.PostAsync($"/api/v1/transport-requests/{late.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingBody = await pending.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(pendingBody);
        Assert.Equal("SENT_TO_RDS", pendingBody.Status);
        Assert.True(pendingBody.RdsCancellationPending);
    }

    [Fact]
    public async Task Variant_switch_requires_supervisor_role_and_machine_permission()
    {
        await fixture.ResetAsync();
        var line = await fixture.SeedIdentityAsync(canChangeVariant: true);
        var active = await fixture.ConfigureMachineAsync();
        var next = await fixture.ContextAsync("SH-01-A", "2");
        using (var lineClient = await fixture.LoginClientAsync(line))
        {
            var deniedByRole = await lineClient.PostAsJsonAsync(
                $"/api/v1/machines/{active.MachineId}/variant-selection",
                new VariantSwitchHttpRequest(next.VariantId, next.LayoutId, null));
            Assert.Equal(HttpStatusCode.Forbidden, deniedByRole.StatusCode);
            Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(deniedByRole));
        }

        await fixture.ResetAsync();
        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor, canChangeVariant: false);
        active = await fixture.ConfigureMachineAsync();
        next = await fixture.ContextAsync("SH-01-A", "2");
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);
        var deniedByMachine = await supervisorClient.PostAsJsonAsync(
            $"/api/v1/machines/{active.MachineId}/variant-selection",
            new VariantSwitchHttpRequest(next.VariantId, next.LayoutId, null));
        Assert.Equal(HttpStatusCode.Forbidden, deniedByMachine.StatusCode);
        Assert.Equal("MACHINE_ACCESS_DENIED", await ApiTestFixture.ErrorCodeAsync(deniedByMachine));
    }

    [Fact]
    public async Task Authorized_variant_switch_uses_workflow_guard()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor, canChangeVariant: true);
        var active = await fixture.ConfigureMachineAsync();
        var next = await fixture.ContextAsync("SH-01-A", "2");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{active.MachineId}/variant-selection",
            new VariantSwitchHttpRequest(next.VariantId, next.LayoutId, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(next.VariantId, await fixture.ScalarAsync<Guid>(
            "SELECT active_variant_id FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", active.MachineId)));
    }

    [Fact]
    public async Task Active_transport_blocks_variant_switch_over_http()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor, canChangeVariant: true);
        var active = await fixture.ConfigureMachineAsync();
        var next = await fixture.ContextAsync("SH-01-A", "2");
        var slot = (await fixture.LogisticsSlotsAsync(active.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(identity);
        _ = await fixture.SendTransportAsync(client, Bring(active, await fixture.PartForSlotAsync(slot, "P05-AUTHEN6")), "switch-http-block");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{active.MachineId}/variant-selection",
            new VariantSwitchHttpRequest(next.VariantId, next.LayoutId, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ACTIVE_TRANSPORT_EXISTS", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Concurrent_http_requests_for_same_slot_create_one_reservation()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(identity);
        var body = Bring(context, await fixture.PartForSlotAsync(slot, "P05-AUTHEN7"));

        var responses = await Task.WhenAll(
            fixture.SendTransportAsync(client, body, "http-slot-race-a"),
            fixture.SendTransportAsync(client, body, "http-slot-race-b"));

        // The invariant under test is unchanged: exactly one reservation is created.
        // Only the rejection reason moved — with backend slot resolution the loser
        // finds no eligible slot rather than colliding with a supplied one.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var rejected = Assert.Single(responses, response => !response.IsSuccessStatusCode);
        var rejectedCode = await ApiTestFixture.ErrorCodeAsync(rejected);
        Assert.True(
            rejectedCode is "SLOT_ALREADY_RESERVED" or "BRING_EMPTY_SLOT_NOT_FOUND",
            $"Unexpected concurrent-rejection code '{rejectedCode}'.");
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Concurrent_same_key_http_retry_returns_one_created_and_one_replay()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(identity);
        var body = Bring(context, await fixture.PartForSlotAsync(slot, "P05-AUTHEN8"));

        var responses = await Task.WhenAll(
            fixture.SendTransportAsync(client, body, "http-same-key-race"),
            fixture.SendTransportAsync(client, body, "http-same-key-race"));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Execution_state_mutation_routes_are_not_exposed()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);
        var id = Guid.NewGuid();

        var responses = await Task.WhenAll(
            client.PostAsync($"/api/v1/transport-requests/{id}/run", null),
            client.PostAsync($"/api/v1/transport-requests/{id}/complete", null),
            client.PostAsync($"/api/v1/transport-requests/{id}/fail", null));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    // The Operator contract has no source/destination slot: the part identifies the
    // position and the backend resolves it from master.layout_slot_parts.
    private static CreateTransportHttpRequest Bring(LayoutContext context, Guid partId) => new(
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        partId,
        null,
        "BRING_EMPTY");
}
