using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class AdminConfigurationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Admin_web_login_needs_no_device_and_exposes_admin_surface()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(RoleCodes.Admin);
        using var client = fixture.Client();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new LoginRequest(identity.Username, identity.Password));

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<AdminLoginResponse>();
        Assert.NotNull(login);
        Assert.Equal(ClientTypes.AdminWeb, login.ClientType);
        Assert.Equal(RoleCodes.Admin, Assert.Single(login.User.Roles));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var roles = await client.GetFromJsonAsync<AdminRoleRecord[]>("/api/v1/admin/roles");
        Assert.Equal(KnownRoleCodes.All.Order(), roles!.Select(role => role.Code).Order());
    }

    [Fact]
    public async Task Admin_login_with_wrong_password_is_rejected()
    {
        // Admin Web must remain password-protected: the passwordless sicil
        // change is scoped to the device-bound Operator employee-number path
        // only and must not weaken Admin Web authentication.
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(RoleCodes.Admin);
        using var client = fixture.Client();

        var response = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new LoginRequest(identity.Username, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Non_admin_cannot_receive_admin_web_token()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(RoleCodes.Supervisor);
        using var client = fixture.Client();
        var response = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new LoginRequest(identity.Username, identity.Password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Admin_web_and_device_bound_tokens_cannot_cross_mutation_boundaries()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(RoleCodes.Admin);
        using var pdaClient = await fixture.LoginClientAsync(identity);
        using var adminClient = await fixture.AdminLoginClientAsync(identity);

        var adminFromPda = await pdaClient.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, adminFromPda.StatusCode);

        var context = await fixture.ConfigureMachineAsync();
        var transportFromAdminWeb = await adminClient.PostAsJsonAsync(
            "/api/v1/transport-requests",
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null, null, null, "BRING_EMPTY"));
        Assert.Equal(HttpStatusCode.Forbidden, transportFromAdminWeb.StatusCode);
    }

    [Fact]
    public async Task User_provisioning_assigns_exact_roles_hashes_password_and_audits()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var password = "Provisioned-password-42!";
            var response = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest(" P05.New.User ", password, "Phase 05 User", "P05-E1", [RoleCodes.LineOperator, RoleCodes.WarehouseOperator]));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.ETag);
            var created = await response.Content.ReadFromJsonAsync<AdminUserRecord>();
            Assert.Equal("p05.new.user", created!.Username);
            Assert.Equal([RoleCodes.LineOperator, RoleCodes.WarehouseOperator], created.Roles);
            var storedHash = await fixture.ScalarAsync<string>("SELECT password_hash FROM auth.users WHERE id = @id", ("id", created.Id));
            Assert.NotEqual(password, storedHash);
            Assert.StartsWith("$tkg-pbkdf2-sha512$", storedHash, StringComparison.Ordinal);
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_logs WHERE action = 'ADMIN_USER_CREATED' AND entity_id = @id", ("id", created.Id.ToString())));
        }
    }

    // ---- WP-01A: Admin employee / registry number management ----

    [Fact]
    public async Task Admin_login_does_not_accept_employee_number()
    {
        var admin = await AdminAsync();
        admin.Client.Dispose();

        // Give the admin identity a known employee number, then prove the Admin
        // Web login path still requires a username and ignores employeeNumber.
        using var anonymous = fixture.Client();
        var response = await anonymous.PostAsJsonAsync(
            "/api/v1/admin/auth/login",
            new LoginRequest(null, admin.Identity.Password, admin.Identity.EmployeeNo));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_LOGIN_REQUEST", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Admin_user_create_trims_and_preserves_employee_number()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.emp.trim", "Provisioned-password-42!", "Trim User", "  001245  ", [RoleCodes.LineOperator]));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<AdminUserRecord>();
            Assert.Equal("001245", created!.EmployeeNo);
        }
    }

    [Fact]
    public async Task Admin_duplicate_normalized_employee_number_is_rejected_cleanly()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var first = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.emp.first", "Provisioned-password-42!", "First", "ABC123", [RoleCodes.LineOperator]));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

            var conflict = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.emp.second", "Provisioned-password-42!", "Second", " abc123 ", [RoleCodes.LineOperator]));

            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("CONFIGURATION_CONFLICT", await ApiTestFixture.ErrorCodeAsync(conflict));
        }
    }

    [Fact]
    public async Task Admin_allows_multiple_users_without_employee_number()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var first = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.noemp.first", "Provisioned-password-42!", "No Emp 1", null, [RoleCodes.LineOperator]));
            var second = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.noemp.second", "Provisioned-password-42!", "No Emp 2", "   ", [RoleCodes.LineOperator]));

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            var firstUser = await first.Content.ReadFromJsonAsync<AdminUserRecord>();
            var secondUser = await second.Content.ReadFromJsonAsync<AdminUserRecord>();
            Assert.Null(firstUser!.EmployeeNo);
            Assert.Null(secondUser!.EmployeeNo);
        }
    }

    [Fact]
    public async Task Admin_accepts_leading_zero_distinct_employee_numbers()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var padded = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.zero.padded", "Provisioned-password-42!", "Padded", "001245", [RoleCodes.LineOperator]));
            var unpadded = await client.PostAsJsonAsync(
                "/api/v1/admin/users",
                new CreateAdminUserRequest("p05.zero.unpadded", "Provisioned-password-42!", "Unpadded", "1245", [RoleCodes.LineOperator]));

            Assert.Equal(HttpStatusCode.Created, padded.StatusCode);
            Assert.Equal(HttpStatusCode.Created, unpadded.StatusCode);
        }
    }

    [Fact]
    public async Task Password_reset_invalidates_old_password_and_never_audits_hash_material()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var created = await CreateUserAsync(client, "p05.reset", RoleCodes.Admin);
            var oldPassword = "Provisioned-password-42!";
            var newPassword = "Replacement-password-84!";
            var reset = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/users/{created.Id}/password-reset", new ResetPasswordRequest(newPassword), created.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            using var anonymous = fixture.Client();
            var oldLogin = await anonymous.PostAsJsonAsync("/api/v1/admin/auth/login", new LoginRequest(created.Username, oldPassword));
            var newLogin = await anonymous.PostAsJsonAsync("/api/v1/admin/auth/login", new LoginRequest(created.Username, newPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
            Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

            var auditJson = await fixture.ScalarAsync<string>(
                "SELECT after_json::text FROM audit.audit_logs WHERE action = 'ADMIN_USER_PASSWORD_RESET' AND entity_id = @id",
                ("id", created.Id.ToString()));
            Assert.DoesNotContain("pbkdf", auditJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(newPassword, auditJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Stale_if_match_returns_explicit_concurrency_conflict()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var part = await CreatePartAsync(client, "P05-CONCURRENCY");
            var first = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/parts/{part.Id}", new UpdateAdminPartRequest("first", true), part.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var stale = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/parts/{part.Id}", new UpdateAdminPartRequest("stale", true), part.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("CONCURRENCY_CONFLICT", await ApiTestFixture.ErrorCodeAsync(stale));
        }
    }

    [Fact]
    public async Task Missing_if_match_is_rejected_before_mutation()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var part = await CreatePartAsync(client, "P05-PRECONDITION");
            var response = await client.PutAsJsonAsync($"/api/v1/admin/parts/{part.Id}", new UpdateAdminPartRequest("no token", true));
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
            Assert.Equal("IF_MATCH_REQUIRED", await ApiTestFixture.ErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task Device_provisioning_and_archive_control_pda_access()
    {
        var (_, adminClient) = await AdminAsync();
        using (adminClient)
        {
            var user = await CreateUserAsync(adminClient, "p05.device.user", RoleCodes.LineOperator);
            var deviceResponse = await adminClient.PostAsJsonAsync(
                "/api/v1/admin/devices",
                new CreateAdminDeviceRequest("P05-PDA-01", "Phase 05 PDA", "PDA", true, Json("{\"zone\":\"line\"}")));
            var device = await deviceResponse.Content.ReadFromJsonAsync<AdminDeviceRecord>();
            Assert.Equal(HttpStatusCode.Created, deviceResponse.StatusCode);

            using var anonymous = fixture.Client();
            var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest(user.Username, "Provisioned-password-42!"))
            };
            login.Headers.Add(ApiHeaders.DeviceCode, device!.Code);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(login)).StatusCode);

            var archive = await SendAsync(
                adminClient,
                HttpMethod.Put,
                $"/api/v1/admin/devices/{device.Id}",
                new UpdateAdminDeviceRequest(device.Name, device.Type, false, device.Metadata),
                device.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

            var retry = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest(user.Username, "Provisioned-password-42!"))
            };
            retry.Headers.Add(ApiHeaders.DeviceCode, device.Code);
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(retry)).StatusCode);
        }
    }

    [Fact]
    public async Task Machine_permissions_are_administered_against_user_version()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var user = await CreateUserAsync(client, "p05.permission", RoleCodes.LineOperator);
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, machine => machine.Code == "SH-01-A");
            var invalid = await SendAsync(
                client,
                HttpMethod.Put,
                $"/api/v1/admin/users/{user.Id}/machine-permissions/{machine.Id}",
                new SetMachinePermissionRequest(false, true, false, false),
                user.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("MACHINE_VIEW_PERMISSION_REQUIRED", await ApiTestFixture.ErrorCodeAsync(invalid));

            var set = await SendAsync(
                client,
                HttpMethod.Put,
                $"/api/v1/admin/users/{user.Id}/machine-permissions/{machine.Id}",
                new SetMachinePermissionRequest(true, true, false, false),
                user.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

            var refreshed = Assert.Single((await client.GetFromJsonAsync<AdminUserRecord[]>("/api/v1/admin/users"))!, value => value.Id == user.Id);
            Assert.True(Assert.Single(refreshed.MachinePermissions).CanOperate);
            var removed = await SendAsync(
                client,
                HttpMethod.Delete,
                $"/api/v1/admin/users/{user.Id}/machine-permissions/{machine.Id}",
                null,
                refreshed.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }
    }

    [Fact]
    public async Task Warehouse_operator_cannot_create_or_cancel_machine_requests()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(RoleCodes.WarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);
        var context = await fixture.ConfigureMachineAsync();
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var remove = await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-ADMREMOVE"), null, "REMOVE_FULL"),
            "p05-warehouse-remove");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(remove));

        var bring = await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-ADMBRING"), null, "BRING_EMPTY"),
            "p05-warehouse-bring");
        Assert.Equal(HttpStatusCode.Forbidden, bring.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(bring));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        var supervisor = await fixture.SeedIdentityAsync(RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);
        var allowed = await fixture.SendTransportAsync(supervisorClient,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-ADMBRING"), null, "BRING_EMPTY"), "p05-supervisor-bring");
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var created = await allowed.Content.ReadFromJsonAsync<TransportHttpResponse>();
        var warehouseCancel = await client.PostAsync($"/api/v1/machines/{context.MachineId}/transport-requests/{created!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, warehouseCancel.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await supervisorClient.PostAsync($"/api/v1/machines/{context.MachineId}/transport-requests/{created.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Layout_draft_slots_publish_and_default_assignment_follow_lifecycle()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var variant = await CreateVariantAsync(client, machine.Id, "P05-VARIANT-A");
            var layout = await CreateLayoutAsync(client, machine.Id, "P05-LAYOUT-A");
            var slotResponse = await SendAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/admin/layouts/{layout.Id}/slots",
                new CreateAdminSlotRequest("P05-SLOT-1", "Phase 05 Slot", "EMPTY_STORAGE", null, 0.1m, 0.1m, 0.2m, 0.2m, 1, true, true, Json("{}")),
                layout.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.Created, slotResponse.StatusCode);

            var publish = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/layouts/{layout.Id}/publish", null, layout.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
            var published = await publish.Content.ReadFromJsonAsync<AdminLayoutRecord>();
            Assert.Equal("PUBLISHED", published!.Status);

            var assign = await SendAsync(
                client,
                HttpMethod.Put,
                $"/api/v1/admin/variants/{variant.Id}/default-layout",
                new SetDefaultLayoutRequest(layout.Id),
                variant.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
            Assert.Equal(layout.Id, (await assign.Content.ReadFromJsonAsync<AdminVariantRecord>())!.DefaultLayoutId);
        }
    }

    [Fact]
    public async Task Published_layout_is_immutable_and_new_version_copies_slots()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var layout = await CreatePublishableLayoutAsync(client, machine.Id, "P05-LAYOUT-VERSIONED");
            var publish = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/layouts/{layout.Id}/publish", null, layout.ConcurrencyToken);
            var published = await publish.Content.ReadFromJsonAsync<AdminLayoutRecord>();

            var immutable = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/layouts/{layout.Id}", new UpdateAdminLayoutRequest("illegal"), published!.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
            Assert.Equal("LAYOUT_NOT_DRAFT", await ApiTestFixture.ErrorCodeAsync(immutable));

            var clone = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/layouts/{layout.Id}/versions", null, published.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.Created, clone.StatusCode);
            var draft = await clone.Content.ReadFromJsonAsync<AdminLayoutRecord>();
            Assert.Equal(2, draft!.Version);
            Assert.Equal("DRAFT", draft.Status);
            Assert.Single((await client.GetFromJsonAsync<AdminSlotRecord[]>($"/api/v1/admin/layouts/{draft.Id}/slots"))!);
        }
    }

    [Fact]
    public async Task Publishing_new_layout_version_archives_previous_and_migrates_default_assignment()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var variant = await CreateVariantAsync(client, machine.Id, "P05-REPLACE-VARIANT");
            var firstDraft = await CreatePublishableLayoutAsync(client, machine.Id, "P05-REPLACE-LAYOUT");
            var firstPublish = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/layouts/{firstDraft.Id}/publish", null, firstDraft.ConcurrencyToken);
            var first = (await firstPublish.Content.ReadFromJsonAsync<AdminLayoutRecord>())!;
            var assignment = await SendAsync(
                client,
                HttpMethod.Put,
                $"/api/v1/admin/variants/{variant.Id}/default-layout",
                new SetDefaultLayoutRequest(first.Id),
                variant.ConcurrencyToken);
            variant = (await assignment.Content.ReadFromJsonAsync<AdminVariantRecord>())!;

            var cloneResponse = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/layouts/{first.Id}/versions", null, first.ConcurrencyToken);
            var secondDraft = (await cloneResponse.Content.ReadFromJsonAsync<AdminLayoutRecord>())!;
            var secondPublish = await SendAsync(client, HttpMethod.Post, $"/api/v1/admin/layouts/{secondDraft.Id}/publish", null, secondDraft.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, secondPublish.StatusCode);
            var second = (await secondPublish.Content.ReadFromJsonAsync<AdminLayoutRecord>())!;

            var layouts = await client.GetFromJsonAsync<AdminLayoutRecord[]>($"/api/v1/admin/layouts?machineId={machine.Id}");
            Assert.Equal("ARCHIVED", Assert.Single(layouts!, value => value.Id == first.Id).Status);
            Assert.Equal("PUBLISHED", Assert.Single(layouts!, value => value.Id == second.Id).Status);
            var variants = await client.GetFromJsonAsync<AdminVariantRecord[]>($"/api/v1/admin/variants?machineId={machine.Id}");
            Assert.Equal(second.Id, Assert.Single(variants!, value => value.Id == variant.Id).DefaultLayoutId);
        }
    }

    [Fact]
    public async Task Draft_slot_update_requires_slot_and_parent_layout_tokens()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var layout = await CreateLayoutAsync(client, machine.Id, "P05-SLOT-UPDATE-LAYOUT");
            var create = await SendAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/admin/layouts/{layout.Id}/slots",
                new CreateAdminSlotRequest("P05-SLOT-U", "Draft slot", "OTHER", null, 0.1m, 0.1m, 0.2m, 0.2m, 1, true, true, Json("{}")),
                layout.ConcurrencyToken);
            var slot = (await create.Content.ReadFromJsonAsync<AdminSlotRecord>())!;

            var update = await SendAsync(
                client,
                HttpMethod.Put,
                $"/api/v1/admin/layouts/{layout.Id}/slots/{slot.Id}",
                new UpdateAdminSlotRequest("Disabled draft slot", "OTHER", null, 0.1m, 0.1m, 0.2m, 0.2m, 2, true, true, false, Json("{\"reason\":\"admin-review\"}")),
                slot.ConcurrencyToken,
                layout.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var updated = await update.Content.ReadFromJsonAsync<AdminSlotRecord>();
            Assert.False(updated!.IsEnabled);
            Assert.Equal(2, updated.DisplayOrder);
        }
    }

    [Fact]
    public async Task Invalid_slot_geometry_is_not_persisted()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var layout = await CreateLayoutAsync(client, machine.Id, "P05-LAYOUT-GEOMETRY");
            var response = await SendAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/admin/layouts/{layout.Id}/slots",
                new CreateAdminSlotRequest("P05-BAD", "Bad", "OTHER", null, 0.9m, 0.9m, 0.2m, 0.2m, 1, true, true, Json("{}")),
                layout.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("INVALID_SLOT_GEOMETRY", await ApiTestFixture.ErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task Part_product_group_and_relation_management_preserves_nullable_evidence()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var main = await CreatePartAsync(client, "P05-MAIN");
            var child = await CreatePartAsync(client, "P05-CHILD");
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var variant = Assert.Single((await client.GetFromJsonAsync<AdminVariantRecord[]>($"/api/v1/admin/variants?machineId={machine.Id}"))!, value => value.Code == "1");
            var groupResponse = await client.PostAsJsonAsync(
                "/api/v1/admin/product-groups",
                new CreateAdminProductGroupRequest(variant.Id, main.Id, "P05 Group", 1));
            var group = await groupResponse.Content.ReadFromJsonAsync<AdminProductGroupRecord>();
            var relation = await SendAsync(
                client,
                HttpMethod.Put,
                $"/api/v1/admin/product-groups/{group!.Id}/parts/{child.Id}",
                new SetAdminProductGroupPartRequest(null, "RELATED", null, 1, true),
                group.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, relation.StatusCode);
            var updated = await relation.Content.ReadFromJsonAsync<AdminProductGroupRecord>();
            Assert.Null(Assert.Single(updated!.Parts).ContainerTypeId);
            Assert.Null(Assert.Single(updated.Parts).LotQuantity);
        }
    }

    [Fact]
    public async Task Unknown_container_footprint_remains_null_and_is_visible_as_data_issue()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/admin/containers",
                new CreateAdminContainerRequest("P05-UNKNOWN-BOX", "Unknown Box", "BOX", null, null, null, null));
            var container = await response.Content.ReadFromJsonAsync<AdminContainerRecord>();
            Assert.Null(container!.LengthMm);
            Assert.Null(container.WidthMm);
            Assert.Null(container.HeightMm);

            var issues = await client.GetFromJsonAsync<AdminDataIssueRecord[]>("/api/v1/admin/data-issues");
            Assert.Contains(issues!, issue => issue.EntityId == container.Id.ToString() && issue.Kind == "UNKNOWN_CONTAINER_FOOTPRINT");
        }
    }

    [Fact]
    public async Task Active_transport_blocks_replacement_layout_publication()
    {
        await fixture.ResetAsync();
        var adminIdentity = await fixture.SeedIdentityAsync(RoleCodes.Admin);
        var operatorIdentity = await fixture.SeedIdentityAsync(RoleCodes.LineOperator);
        using var admin = await fixture.AdminLoginClientAsync(adminIdentity);
        using var oper = await fixture.LoginClientAsync(operatorIdentity);
        var machine = Assert.Single((await admin.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
        var variant = await CreateVariantAsync(admin, machine.Id, "P05-ACTIVE-VARIANT");
        var layout = await CreatePublishableLayoutAsync(admin, machine.Id, "P05-ACTIVE-LAYOUT");
        var publishedResponse = await SendAsync(admin, HttpMethod.Post, $"/api/v1/admin/layouts/{layout.Id}/publish", null, layout.ConcurrencyToken);
        var published = await publishedResponse.Content.ReadFromJsonAsync<AdminLayoutRecord>();
        var assignedResponse = await SendAsync(
            admin,
            HttpMethod.Put,
            $"/api/v1/admin/variants/{variant.Id}/default-layout",
            new SetDefaultLayoutRequest(layout.Id),
            variant.ConcurrencyToken);
        variant = (await assignedResponse.Content.ReadFromJsonAsync<AdminVariantRecord>())!;
        await fixture.ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id)
            VALUES (@machine_id, @variant_id, @layout_id)
            ON CONFLICT (machine_id) DO UPDATE SET
                active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = NULL,
                row_version = ops.machine_runtime_state.row_version + 1,
                updated_at = now()
            """,
            ("machine_id", machine.Id),
            ("variant_id", variant.Id),
            ("layout_id", layout.Id));
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(layout.Id, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var cloneResponse = await SendAsync(admin, HttpMethod.Post, $"/api/v1/admin/layouts/{layout.Id}/versions", null, published!.ConcurrencyToken);
        var clone = await cloneResponse.Content.ReadFromJsonAsync<AdminLayoutRecord>();

        // Use a removal request here: this test needs only an active transport
        // reservation. BRING_EMPTY belongs to the request-bound finished-goods
        // preparation workflow and cannot be fabricated without a prepared pallet.
        await fixture.SetSlotAsync(slot, "OCCUPIED", "FULL");

        var active = await fixture.SendTransportAsync(
            oper,
            new CreateTransportHttpRequest(machine.Id, variant.Id, layout.Id, null, null,
                await fixture.PartForSlotAsync(slot, "P05-ADMACTIVE", variant.Id), null, "REMOVE_FULL"),
            "p05-active-publish");
        Assert.Equal(HttpStatusCode.Created, active.StatusCode);

        var publish = await SendAsync(admin, HttpMethod.Post, $"/api/v1/admin/layouts/{clone!.Id}/publish", null, clone.ConcurrencyToken);
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Equal("CONFIGURATION_IN_USE", await ApiTestFixture.ErrorCodeAsync(publish));
    }

    [Fact]
    public async Task Self_deactivation_and_self_admin_removal_are_blocked()
    {
        var (identity, client) = await AdminAsync();
        using (client)
        {
            var current = Assert.Single((await client.GetFromJsonAsync<AdminUserRecord[]>("/api/v1/admin/users"))!, value => value.Id == identity.UserId);
            var deactivate = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/users/{current.Id}", new UpdateAdminUserRequest(current.FullName, current.EmployeeNo, false), current.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
            Assert.Equal("SELF_DEACTIVATION_FORBIDDEN", await ApiTestFixture.ErrorCodeAsync(deactivate));

            var removeRole = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/users/{current.Id}/roles", new SetRolesRequest([RoleCodes.Supervisor]), current.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.Conflict, removeRole.StatusCode);
            Assert.Equal("SELF_ADMIN_REMOVAL_FORBIDDEN", await ApiTestFixture.ErrorCodeAsync(removeRole));
        }
    }

    [Fact]
    public async Task Audit_history_exposes_actor_correlation_and_before_after_without_hard_delete_surface()
    {
        var (identity, client) = await AdminAsync();
        using (client)
        {
            var part = await CreatePartAsync(client, "P05-AUDIT");
            var update = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/parts/{part.Id}", new UpdateAdminPartRequest("updated", true), part.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var audit = await client.GetFromJsonAsync<AdminAuditRecord[]>("/api/v1/admin/audit-logs?entityType=PART&limit=50");
            var entry = Assert.Single(audit!, value => value.Action == "ADMIN_PART_UPDATED" && value.EntityId == part.Id.ToString());
            Assert.Equal(identity.UserId, entry.ActorUserId);
            Assert.NotNull(entry.Before);
            Assert.NotNull(entry.After);
            Assert.NotNull(entry.CorrelationId);

            var hardDelete = await client.DeleteAsync($"/api/v1/admin/parts/{part.Id}");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, hardDelete.StatusCode);
        }
    }

    // Customer UX — Automatic Machine Slot Resolution: minimal Admin CRUD
    // over master.layout_slot_parts (list/assign/remove) so a customer
    // mapping correction no longer requires a direct SQL edit. No
    // concurrency token — mirrors the RDS slot-mapping membership pattern.
    [Fact]
    public async Task Slot_part_mapping_supports_list_assign_and_remove_with_audit()
    {
        var (identity, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var layout = await CreatePublishableLayoutAsync(client, machine.Id, "P05-SLOTPART-LAYOUT");
            var slot = Assert.Single((await client.GetFromJsonAsync<AdminSlotRecord[]>($"/api/v1/admin/layouts/{layout.Id}/slots"))!);
            var part = await CreatePartAsync(client, "P05-SLOTPART-1");

            var empty = await client.GetFromJsonAsync<AdminSlotPartRecord[]>($"/api/v1/admin/layout-slots/{slot.Id}/parts");
            Assert.Empty(empty!);

            var assign = await client.PutAsync($"/api/v1/admin/layout-slots/{slot.Id}/parts/{part.Id}", null);
            Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

            var assigned = await client.GetFromJsonAsync<AdminSlotPartRecord[]>($"/api/v1/admin/layout-slots/{slot.Id}/parts");
            var mapping = Assert.Single(assigned!);
            Assert.Equal(part.Id, mapping.PartId);
            Assert.Equal("P05-SLOTPART-1", mapping.PartCode);

            var assignAudit = await client.GetFromJsonAsync<AdminAuditRecord[]>("/api/v1/admin/audit-logs?entityType=LAYOUT_SLOT&limit=50");
            var assignEntry = Assert.Single(assignAudit!, value => value.Action == "ADMIN_LAYOUT_SLOT_PART_ASSIGNED" && value.EntityId == slot.Id.ToString());
            Assert.Equal(identity.UserId, assignEntry.ActorUserId);

            // Assigning the same pair again is idempotent (ON CONFLICT DO NOTHING).
            var reassign = await client.PutAsync($"/api/v1/admin/layout-slots/{slot.Id}/parts/{part.Id}", null);
            Assert.Equal(HttpStatusCode.NoContent, reassign.StatusCode);
            Assert.Single((await client.GetFromJsonAsync<AdminSlotPartRecord[]>($"/api/v1/admin/layout-slots/{slot.Id}/parts"))!);

            var remove = await client.DeleteAsync($"/api/v1/admin/layout-slots/{slot.Id}/parts/{part.Id}");
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<AdminSlotPartRecord[]>($"/api/v1/admin/layout-slots/{slot.Id}/parts"))!);

            var removeAudit = await client.GetFromJsonAsync<AdminAuditRecord[]>("/api/v1/admin/audit-logs?entityType=LAYOUT_SLOT&limit=50");
            Assert.Single(removeAudit!, value => value.Action == "ADMIN_LAYOUT_SLOT_PART_REMOVED" && value.EntityId == slot.Id.ToString());

            var removeAgain = await client.DeleteAsync($"/api/v1/admin/layout-slots/{slot.Id}/parts/{part.Id}");
            Assert.Equal(HttpStatusCode.NotFound, removeAgain.StatusCode);
            Assert.Equal("LAYOUT_SLOT_PART_NOT_FOUND", await ApiTestFixture.ErrorCodeAsync(removeAgain));
        }
    }

    [Fact]
    public async Task Slot_part_assignment_rejects_unknown_slot_or_inactive_part()
    {
        var (_, client) = await AdminAsync();
        using (client)
        {
            var machine = Assert.Single((await client.GetFromJsonAsync<AdminMachineRecord[]>("/api/v1/admin/machines"))!, value => value.Code == "SH-01-A");
            var layout = await CreatePublishableLayoutAsync(client, machine.Id, "P05-SLOTPART-INVALID-LAYOUT");
            var slot = Assert.Single((await client.GetFromJsonAsync<AdminSlotRecord[]>($"/api/v1/admin/layouts/{layout.Id}/slots"))!);
            var part = await CreatePartAsync(client, "P05-SLOTPART-2");

            var missingSlot = await client.PutAsync($"/api/v1/admin/layout-slots/{Guid.NewGuid()}/parts/{part.Id}", null);
            Assert.Equal(HttpStatusCode.NotFound, missingSlot.StatusCode);
            Assert.Equal("LAYOUT_SLOT_NOT_FOUND", await ApiTestFixture.ErrorCodeAsync(missingSlot));

            var deactivate = await SendAsync(client, HttpMethod.Put, $"/api/v1/admin/parts/{part.Id}", new UpdateAdminPartRequest(part.Description, false), part.ConcurrencyToken);
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
            var inactivePart = await client.PutAsync($"/api/v1/admin/layout-slots/{slot.Id}/parts/{part.Id}", null);
            Assert.Equal(HttpStatusCode.NotFound, inactivePart.StatusCode);
            Assert.Equal("PART_NOT_FOUND", await ApiTestFixture.ErrorCodeAsync(inactivePart));
        }
    }

    private async Task<(TestIdentity Identity, HttpClient Client)> AdminAsync()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(RoleCodes.Admin);
        return (identity, await fixture.AdminLoginClientAsync(identity));
    }

    private static async Task<AdminUserRecord> CreateUserAsync(HttpClient client, string username, string role)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/users",
            new CreateAdminUserRequest(username, "Provisioned-password-42!", $"User {username}", null, [role]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminUserRecord>())!;
    }

    private static async Task<AdminPartRecord> CreatePartAsync(HttpClient client, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/parts", new CreateAdminPartRequest(code, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminPartRecord>())!;
    }

    private static async Task<AdminVariantRecord> CreateVariantAsync(HttpClient client, Guid machineId, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/variants", new CreateAdminVariantRequest(machineId, code, code, 99));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminVariantRecord>())!;
    }

    private static async Task<AdminLayoutRecord> CreateLayoutAsync(HttpClient client, Guid machineId, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/layouts", new CreateAdminLayoutRequest(machineId, code, code));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminLayoutRecord>())!;
    }

    private static async Task<AdminLayoutRecord> CreatePublishableLayoutAsync(HttpClient client, Guid machineId, string code)
    {
        var layout = await CreateLayoutAsync(client, machineId, code);
        var slot = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/admin/layouts/{layout.Id}/slots",
            new CreateAdminSlotRequest("P05-SLOT-1", "Slot", "OTHER", null, 0.1m, 0.1m, 0.2m, 0.2m, 1, true, true, Json("{}")),
            layout.ConcurrencyToken);
        slot.EnsureSuccessStatusCode();
        return layout;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string uri, object? body, long version, long? parentVersion = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (parentVersion.HasValue)
        {
            request.Headers.TryAddWithoutValidation("X-Layout-Version", parentVersion.Value.ToString());
        }

        return await client.SendAsync(request);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
