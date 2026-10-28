using System.Net;
using System.Net.Http.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-03: Field Terminal to Machine Fixed Binding Tests.
/// Validates server-side device-machine binding, line operator scope enforcement,
/// cross-machine prevention, unbound fail-closed, warehouse tablet independence,
/// and live token re-resolution from fresh DB state.
/// </summary>
public sealed class TerminalMachineBindingApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task ScenarioA_Bound_terminal_allows_operational_context_without_user_machine_permissions()
    {
        await fixture.ResetAsync();

        // Device bound to SH-01-A, Line Operator, but NO auth.user_machine_permissions rows
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true,
            includeUserMachinePermissions: false);

        using var client = await fixture.LoginClientAsync(identity);

        // 1. Login and /auth/me projections include machineId
        var meResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var meDoc = await meResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var deviceMachineId = meDoc.GetProperty("device").GetProperty("machineId").GetGuid();
        Assert.Equal(identity.MachineId, deviceMachineId);

        // 2. Machine list returns ONLY the bound machine
        var machines = await client.GetFromJsonAsync<MachineSummaryRecord[]>("/api/v1/machines");
        Assert.NotNull(machines);
        Assert.Single(machines);
        Assert.Equal(identity.MachineId, machines[0].Id);

        // 3. Operational context for bound machine is allowed (200 OK)
        var contextResponse = await client.GetAsync($"/api/v1/machines/{identity.MachineId}/operational-context");
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
    }

    [Fact]
    public async Task ScenarioB_Cross_machine_access_is_forbidden_with_scope_mismatch()
    {
        await fixture.ResetAsync();

        var machineBId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-B'");

        // Device bound to SH-01-A, Line Operator
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        using var client = await fixture.LoginClientAsync(identity);

        // Attempting to read machine B's context -> 403 DEVICE_MACHINE_SCOPE_MISMATCH
        var response = await client.GetAsync($"/api/v1/machines/{machineBId}/operational-context");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DEVICE_MACHINE_SCOPE_MISMATCH", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ScenarioC_Spoofed_client_machine_id_in_mutation_is_rejected()
    {
        await fixture.ResetAsync();

        var machineBId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-B'");

        // Device bound to SH-01-A
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        using var client = await fixture.LoginClientAsync(identity);

        // Attempting a container action on machine B
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{machineBId}/container-actions")
        {
            Content = JsonContent.Create(new
            {
                positionNumber = 1,
                action = "EMPTY"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DEVICE_MACHINE_SCOPE_MISMATCH", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ScenarioD_Unbound_line_terminal_fails_closed_with_binding_required()
    {
        await fixture.ResetAsync();

        // Line operator on an unbound device (machine_id == null)
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: false);

        using var client = await fixture.LoginClientAsync(identity);

        // Machine list returns empty for unbound line operator
        var machines = await client.GetFromJsonAsync<MachineSummaryRecord[]>("/api/v1/machines");
        Assert.NotNull(machines);
        Assert.Empty(machines);

        // Machine-specific operation fails closed with 403 DEVICE_MACHINE_BINDING_REQUIRED
        var contextResponse = await client.GetAsync($"/api/v1/machines/{identity.MachineId}/operational-context");
        Assert.Equal(HttpStatusCode.Forbidden, contextResponse.StatusCode);
        Assert.Equal("DEVICE_MACHINE_BINDING_REQUIRED", await ApiTestFixture.ErrorCodeAsync(contextResponse));
    }

    [Fact]
    public async Task ScenarioE_Unbound_warehouse_tablet_can_access_preparation_api_without_machine_binding()
    {
        await fixture.ResetAsync();

        // Warehouse operator on unbound tablet (machine_id == null)
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.SemiFinishedWarehouseOperator,
            machineCode: null,
            bindDeviceToMachine: false);

        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync("/api/v1/warehouse/preparation/areas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScenarioF_Unbound_finished_goods_tablet_can_access_store_api_without_machine_binding()
    {
        await fixture.ResetAsync();

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.FinishedGoodsWarehouseOperator,
            machineCode: null,
            bindDeviceToMachine: false);

        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync("/api/v1/warehouse/store/locations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScenarioG_Binding_changed_in_database_while_jwt_alive_immediately_rebinds_scope()
    {
        await fixture.ResetAsync();

        var machineAId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-A'");
        var machineBId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-B'");

        // 1. Login with terminal bound to SH-01-A
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);

        using var client = await fixture.LoginClientAsync(identity);

        var initialContextA = await client.GetAsync($"/api/v1/machines/{machineAId}/operational-context");
        Assert.Equal(HttpStatusCode.OK, initialContextA.StatusCode);

        // 2. Admin re-binds device in DB to SH-01-B
        await fixture.ExecuteAsync(
            "UPDATE audit.client_devices SET machine_id = @machine_id WHERE id = @id",
            ("machine_id", machineBId),
            ("id", identity.DeviceId));

        // 3. Using the SAME live client and token, SH-01-A is now rejected, and SH-01-B is allowed!
        var updatedContextA = await client.GetAsync($"/api/v1/machines/{machineAId}/operational-context");
        Assert.Equal(HttpStatusCode.Forbidden, updatedContextA.StatusCode);
        Assert.Equal("DEVICE_MACHINE_SCOPE_MISMATCH", await ApiTestFixture.ErrorCodeAsync(updatedContextA));

        var updatedContextB = await client.GetAsync($"/api/v1/machines/{machineBId}/operational-context");
        Assert.Equal(HttpStatusCode.OK, updatedContextB.StatusCode);
    }

    [Fact]
    public async Task ScenarioH_Warehouse_only_role_on_bound_device_cannot_access_machine_operations()
    {
        await fixture.ResetAsync();

        var machineAId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-A'");

        // Terminal is bound to SH-01-A, but operator only has warehouse role
        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.FinishedGoodsWarehouseOperator,
            machineCode: "SH-01-A",
            deviceBoundMachineCode: "SH-01-A");

        using var client = await fixture.LoginClientAsync(identity);

        // Attempting to access machine operations on SH-01-A is rejected by role safety
        var contextResponse = await client.GetAsync($"/api/v1/machines/{machineAId}/operational-context");
        Assert.Equal(HttpStatusCode.Forbidden, contextResponse.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(contextResponse));
    }
}
