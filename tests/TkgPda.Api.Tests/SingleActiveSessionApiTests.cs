using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-04: Single Active Field Session Tests.
/// Validates server-authoritative single active session per employee,
/// same-device replacement, different-device rejection (409 Conflict),
/// lease expiry, keepalive heartbeat, explicit logout, concurrency safety,
/// token theft prevention, and Admin Web independence.
/// </summary>
public sealed class SingleActiveSessionApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Scenario34_Old_jwt_rejected_after_same_device_relogin()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        // 1. Initial login on TERM-A -> TOKEN-1
        var client1 = fixture.Client();
        var login1Response = await client1.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        });
        Assert.Equal(HttpStatusCode.OK, login1Response.StatusCode);
        var login1 = await login1Response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login1);

        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login1.AccessToken);
        client1.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        var me1 = await client1.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me1.StatusCode);

        // 2. Re-login on SAME TERM-A -> TOKEN-2
        var client2 = fixture.Client();
        var login2Response = await client2.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        });
        Assert.Equal(HttpStatusCode.OK, login2Response.StatusCode);
        var login2 = await login2Response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login2);
        Assert.NotEqual(login1.AccessToken, login2.AccessToken);

        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login2.AccessToken);
        client2.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        // 3. Verify DB state: Old session is revoked with REPLACED_SAME_DEVICE_LOGIN
        var revokedReason = await fixture.ScalarAsync<string>(
            "SELECT revocation_reason FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NOT NULL",
            ("user_id", identity.UserId));
        Assert.Equal("REPLACED_SAME_DEVICE_LOGIN", revokedReason);

        var activeSessions = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        Assert.Equal(1L, activeSessions);

        // 4. Request with old TOKEN-1 is rejected (401 SESSION_NOT_ACTIVE)
        var oldTokenResponse = await client1.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);
        var oldProblem = await oldTokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SESSION_NOT_ACTIVE", oldProblem.GetProperty("code").GetString());

        // 5. Request with new TOKEN-2 succeeds (200 OK)
        var newTokenResponse = await client2.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
    }

    [Fact]
    public async Task Scenario35_Different_device_login_rejected_with_409_when_session_active()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        // Seed a second device TERM-B
        var deviceBCode = $"PDA-TERM-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Terminal B', 'PDA', true, @machine_id)
            """,
            ("code", deviceBCode),
            ("machine_id", identity.MachineId));

        // 1. Login on TERM-A succeeds
        using var clientA = fixture.Client();
        var loginAResponse = await clientA.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        });
        Assert.Equal(HttpStatusCode.OK, loginAResponse.StatusCode);
        var loginA = await loginAResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(loginA);
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginA.AccessToken);
        clientA.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        // 2. Login on TERM-B with SAME user credentials fails with 409 Conflict
        using var clientB = fixture.Client();
        var loginBResponse = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, deviceBCode);
        });
        Assert.Equal(HttpStatusCode.Conflict, loginBResponse.StatusCode);
        var problem = await loginBResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE_SESSION_EXISTS", problem.GetProperty("code").GetString());

        // 3. Verify TERM-A session remains valid and active
        var activeSessions = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        Assert.Equal(1L, activeSessions);

        var meA = await clientA.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meA.StatusCode);

        // 4. Verify audit log contains AUTH_LOGIN_BLOCKED_ACTIVE_SESSION
        var auditCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_logs WHERE action = 'AUTH_LOGIN_BLOCKED_ACTIVE_SESSION' AND actor_user_id = @user_id",
            ("user_id", identity.UserId));
        Assert.Equal(1L, auditCount);
    }

    [Fact]
    public async Task Scenario36_Lease_expiry_allows_new_device_login_and_invalidates_old_token()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var deviceBCode = $"PDA-TERM-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Terminal B', 'PDA', true, @machine_id)
            """,
            ("code", deviceBCode),
            ("machine_id", identity.MachineId));

        // 1. Login on TERM-A
        using var clientA = fixture.Client();
        var loginAResponse = await clientA.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        });
        var loginA = await loginAResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(loginA);
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginA.AccessToken);
        clientA.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);

        // 2. Simulate lease expiration (heartbeat stopped) by updating lease_expires_at into the past
        await fixture.ExecuteAsync(
            "UPDATE auth.user_sessions SET lease_expires_at = now() - interval '5 seconds' WHERE user_id = @user_id",
            ("user_id", identity.UserId));

        // 3. Old TERM-A token is rejected because lease is expired
        var meOld = await clientA.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meOld.StatusCode);
        var oldProblem = await meOld.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SESSION_EXPIRED", oldProblem.GetProperty("code").GetString());

        // 4. Now TERM-B logs in with same user: stale session is revoked and TERM-B login succeeds!
        using var clientB = fixture.Client();
        var loginBResponse = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, deviceBCode);
        });
        Assert.Equal(HttpStatusCode.OK, loginBResponse.StatusCode);
        var loginB = await loginBResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(loginB);

        // 5. Verify DB: exactly 1 active session exists, which belongs to TERM-B device
        var activeDeviceId = await fixture.ScalarAsync<Guid>(
            "SELECT device_id FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        var devBId = await fixture.ScalarAsync<Guid>("SELECT id FROM audit.client_devices WHERE device_code = @code", ("code", deviceBCode));
        Assert.Equal(devBId, activeDeviceId);
    }

    [Fact]
    public async Task Scenario37_Heartbeat_extends_lease_and_keeps_session_active()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var deviceBCode = $"PDA-TERM-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Terminal B', 'PDA', true, @machine_id)
            """,
            ("code", deviceBCode),
            ("machine_id", identity.MachineId));

        using var clientA = await fixture.LoginClientAsync(identity);

        var initialLeaseExpiresAt = await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT lease_expires_at FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));

        // Advance lease into near-expiry (e.g. 5 seconds from now)
        await fixture.ExecuteAsync(
            "UPDATE auth.user_sessions SET lease_expires_at = now() + interval '5 seconds' WHERE user_id = @user_id",
            ("user_id", identity.UserId));

        // Send heartbeat
        var heartbeatResponse = await clientA.PostAsync("/api/v1/auth/session/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, heartbeatResponse.StatusCode);
        var heartbeat = await heartbeatResponse.Content.ReadFromJsonAsync<HeartbeatResponse>();
        Assert.NotNull(heartbeat);

        // Verify lease was extended beyond the near-expiry
        var extendedLease = await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT lease_expires_at FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        Assert.True(extendedLease > DateTimeOffset.UtcNow.AddSeconds(30));

        // TERM-B login is still rejected with 409
        using var clientB = fixture.Client();
        var loginBResponse = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, deviceBCode);
        });
        Assert.Equal(HttpStatusCode.Conflict, loginBResponse.StatusCode);
    }

    [Fact]
    public async Task Scenario38_Heartbeat_never_extends_token_absolute_expiry()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        using var clientA = await fixture.LoginClientAsync(identity);

        var initialTokenExpiresAt = await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT token_expires_at FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));

        // Call heartbeat
        var heartbeatResponse = await clientA.PostAsync("/api/v1/auth/session/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, heartbeatResponse.StatusCode);

        var afterTokenExpiresAt = await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT token_expires_at FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));

        Assert.Equal(initialTokenExpiresAt, afterTokenExpiresAt);
    }

    [Fact]
    public async Task Scenario39_Explicit_logout_revokes_session_and_allows_new_device_login()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var deviceBCode = $"PDA-TERM-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Terminal B', 'PDA', true, @machine_id)
            """,
            ("code", deviceBCode),
            ("machine_id", identity.MachineId));

        using var clientA = await fixture.LoginClientAsync(identity);

        // 1. Explicit logout from TERM-A
        var logoutResponse = await clientA.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        // 2. Verify DB: session revoked with reason USER_LOGOUT
        var revokedReason = await fixture.ScalarAsync<string>(
            "SELECT revocation_reason FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NOT NULL",
            ("user_id", identity.UserId));
        Assert.Equal("USER_LOGOUT", revokedReason);

        var activeSessions = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        Assert.Equal(0L, activeSessions);

        // 3. Verify audit log contains AUTH_SESSION_LOGOUT
        var logoutAuditCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_logs WHERE action = 'AUTH_SESSION_LOGOUT' AND actor_user_id = @user_id",
            ("user_id", identity.UserId));
        Assert.Equal(1L, logoutAuditCount);

        // 4. Old TERM-A token is rejected (401)
        var meOld = await clientA.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meOld.StatusCode);

        // 5. TERM-B login succeeds immediately
        using var clientB = fixture.Client();
        var loginBResponse = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, deviceBCode);
        });
        Assert.Equal(HttpStatusCode.OK, loginBResponse.StatusCode);
    }

    [Fact]
    public async Task Scenario40_Concurrent_login_requests_serialize_and_only_one_succeeds()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var deviceBCode = $"PDA-TERM-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Terminal B', 'PDA', true, @machine_id)
            """,
            ("code", deviceBCode),
            ("machine_id", identity.MachineId));

        using var client1 = fixture.Client();
        using var client2 = fixture.Client();

        // Launch both logins concurrently
        var task1 = client1.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        });

        var task2 = client2.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, deviceBCode);
        });

        var response1 = await task1;
        var response2 = await task2;
        var responses = new[] { response1, response2 };
        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, okCount);
        Assert.Equal(1, conflictCount);

        // Verify exactly one active session in DB
        var activeCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        Assert.Equal(1L, activeCount);
    }

    [Fact]
    public async Task Scenario41_Warehouse_tablet_single_session_and_role_preservation()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.SemiFinishedWarehouseOperator,
            machineCode: null,
            bindDeviceToMachine: false);

        var tabletBCode = $"TABLET-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Warehouse Tablet B', 'PDA', true, NULL)
            """,
            ("code", tabletBCode));

        // Tablet A logs in
        using var clientA = await fixture.LoginClientAsync(identity);

        // Tablet A has warehouse access
        var prepResponse = await clientA.GetAsync("/api/v1/warehouse/preparation-requests");
        Assert.Equal(HttpStatusCode.OK, prepResponse.StatusCode);

        // Tablet B login is rejected with 409
        using var clientB = fixture.Client();
        var loginBResponse = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, tabletBCode);
        });
        Assert.Equal(HttpStatusCode.Conflict, loginBResponse.StatusCode);

        // Tablet A logs out
        var logout = await clientA.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // Tablet B can now log in successfully
        var loginB2Response = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, tabletBCode);
        });
        Assert.Equal(HttpStatusCode.OK, loginB2Response.StatusCode);
    }

    [Fact]
    public async Task Scenario42_Line_operator_machine_terminal_single_session_and_binding_preservation()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var termBCode = $"PDA-TERM-B-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Terminal B', 'PDA', true, @machine_id)
            """,
            ("code", termBCode),
            ("machine_id", identity.MachineId));

        using var clientA = await fixture.LoginClientAsync(identity);

        // Bound machine operation succeeds
        var ctx = await clientA.GetAsync($"/api/v1/machines/{identity.MachineId}/operational-context");
        Assert.Equal(HttpStatusCode.OK, ctx.StatusCode);

        // TERM-B rejected
        using var clientB = fixture.Client();
        var loginB = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, termBCode);
        });
        Assert.Equal(HttpStatusCode.Conflict, loginB.StatusCode);

        // TERM-A logout -> TERM-B login succeeds
        await clientA.PostAsync("/api/v1/auth/logout", null);
        var loginB2 = await clientB.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(identity.Username, identity.Password), headers =>
        {
            headers.Add(ApiHeaders.DeviceCode, termBCode);
        });
        Assert.Equal(HttpStatusCode.OK, loginB2.StatusCode);
    }

    [Fact]
    public async Task Scenario43_Admin_web_login_and_tokens_bypass_field_session_system()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.Admin,
            machineCode: null,
            bindDeviceToMachine: false);

        // Admin Web login
        using var adminClient = await fixture.AdminLoginClientAsync(identity);

        // Access admin endpoint
        var usersResponse = await adminClient.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);

        // Verify NO session in auth.user_sessions
        var sessionCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_sessions WHERE user_id = @user_id",
            ("user_id", identity.UserId));
        Assert.Equal(0L, sessionCount);
    }

    [Fact]
    public async Task Scenario33_Token_theft_device_code_mismatch_prevented()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var stolenDeviceCode = $"PDA-STOLEN-{Guid.NewGuid():N}"[..20];
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active, machine_id)
            VALUES (@code, 'Stolen Device', 'PDA', true, @machine_id)
            """,
            ("code", stolenDeviceCode),
            ("machine_id", identity.MachineId));

        using var client = await fixture.LoginClientAsync(identity);

        // Send request with TOKEN-A but X-Device-Code: STOLEN
        client.DefaultRequestHeaders.Remove(ApiHeaders.DeviceCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, stolenDeviceCode);

        var stolenResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, stolenResponse.StatusCode);
        var problem = await stolenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DEVICE_CONTEXT_MISMATCH", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TestA_Login_finalization_failure_compensates_and_revokes_session()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        var device = new DeviceRecord(
            identity.DeviceId,
            identity.DeviceCode,
            "Terminal A",
            "PDA",
            true,
            identity.MachineId);

        var user = new AuthUserRecord(
            identity.UserId,
            identity.Username,
            "dummy-hash",
            "Test Operator",
            identity.EmployeeNo,
            true,
            [RoleCodes.LineOperator]);

        var failingDataStore = new FailingFinalizationDataStore(user, device);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var sessionStore = new PostgresFieldSessionStore(dataSource);
        var passwordHasher = new Pbkdf2PasswordHasher();
        var jwtOptions = new JwtOptions(
            "tkg-pda",
            "tkg-pda-clients",
            "01234567890123456789012345678901",
            60,
            5);
        var fieldSessionOptions = new FieldSessionOptions
        {
            LeaseSeconds = 120,
            HeartbeatSeconds = 30
        };
        var tokenService = new JwtTokenService(jwtOptions, TimeProvider.System);

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiHeaders.DeviceCode] = identity.DeviceCode;

        // When login finalization fails after session acquisition, it must compensate and rethrow
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiEndpoints.LoginAsync(
            new LoginRequest(null, null, identity.EmployeeNo),
            context,
            failingDataStore,
            sessionStore,
            fieldSessionOptions,
            TimeProvider.System,
            jwtOptions,
            passwordHasher,
            tokenService,
            CancellationToken.None));

        Assert.Equal("Simulated database failure during MarkLoginSucceeded", ex.Message);

        // Verify active sessions count is 0 in DB
        var activeSessionCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_sessions WHERE user_id = @user_id AND revoked_at IS NULL",
            ("user_id", identity.UserId));
        Assert.Equal(0, activeSessionCount);

        // Verify the newly created row was revoked with LOGIN_FINALIZATION_FAILED
        var revocationReason = await fixture.ScalarAsync<string>(
            "SELECT revocation_reason FROM auth.user_sessions WHERE user_id = @user_id",
            ("user_id", identity.UserId));
        Assert.Equal("LOGIN_FINALIZATION_FAILED", revocationReason);
    }
}

internal static class HttpClientTestExtensions
{
    public static async Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client,
        string uri,
        T value,
        Action<HttpRequestHeaders> configureHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(value)
        };
        configureHeaders(request.Headers);
        return await client.SendAsync(request);
    }
}

file sealed class FailingFinalizationDataStore(AuthUserRecord user, DeviceRecord device) : IApiDataStore
{
    public Task<DeviceRecord?> FindDeviceAsync(string deviceCode, CancellationToken cancellationToken = default) =>
        Task.FromResult<DeviceRecord?>(device);

    public Task<AuthUserRecord?> FindUserByEmployeeNumberAsync(string normalizedEmployeeNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthUserRecord?>(user);

    public Task<AuthUserRecord?> FindUserByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthUserRecord?>(user);

    public Task MarkLoginSucceededAsync(Guid userId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated database failure during MarkLoginSucceeded");

    public Task WriteAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<MachinePermissionRecord?> FindMachinePermissionAsync(Guid userId, Guid machineId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<MachineSummaryRecord?> FindMachineSummaryAsync(Guid machineId, CancellationToken cancellationToken = default) =>
        Task.FromResult<MachineSummaryRecord?>(new MachineSummaryRecord(
            machineId,
            "SH-01-A",
            "SH-01-A",
            0,
            new MachinePermissionRecord(machineId, true, true, false, false)));
    public Task<IReadOnlyList<MachineSummaryRecord>> ListVisibleMachinesAsync(Guid userId, bool administrator, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<CompatibleVariantRecord>> ListCompatibleVariantsAsync(Guid machineId, Guid mainPartId, Guid partId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<MachineMainPartOptionRecord>> ListMachinePartOptionsAsync(Guid machineId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<OperationalContextDataRecord?> GetOperationalContextDataAsync(Guid userId, Guid machineId, bool administrator, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<TransportRequestSnapshot?> FindTransportRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<WarehousePreparationRequestRecord>> ListWarehousePreparationRequestsAsync(Guid userId, bool administrator, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}
