using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TkgPda.Application;

namespace TkgPda.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapTkgPdaAdminApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/admin/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("admin-login");

        var admin = endpoints.MapGroup("/api/v1/admin")
            .RequireAuthorization(PolicyNames.Administrator);

        admin.MapGet("/roles", (AdminConfigurationService service, CancellationToken ct) => service.ListRolesAsync(ct));
        admin.MapGet("/users", (AdminConfigurationService service, CancellationToken ct) => service.ListUsersAsync(ct));
        admin.MapPost("/users", CreateUserAsync);
        admin.MapPut("/users/{userId:guid}", UpdateUserAsync);
        admin.MapPost("/users/{userId:guid}/password-reset", ResetPasswordAsync);
        admin.MapPut("/users/{userId:guid}/roles", SetRolesAsync);

        admin.MapGet("/devices", (AdminConfigurationService service, CancellationToken ct) => service.ListDevicesAsync(ct));
        admin.MapPost("/devices", CreateDeviceAsync);
        admin.MapPut("/devices/{deviceId:guid}", UpdateDeviceAsync);

        admin.MapGet("/machines", (AdminConfigurationService service, CancellationToken ct) => service.ListMachinesAsync(ct));
        admin.MapPut("/machines/{machineId:guid}", UpdateMachineAsync);
        admin.MapPut("/users/{userId:guid}/machine-permissions/{machineId:guid}", SetMachinePermissionAsync);
        admin.MapDelete("/users/{userId:guid}/machine-permissions/{machineId:guid}", RemoveMachinePermissionAsync);

        admin.MapGet("/variants", (Guid? machineId, AdminConfigurationService service, CancellationToken ct) => service.ListVariantsAsync(machineId, ct));
        admin.MapPost("/variants", CreateVariantAsync);
        admin.MapPut("/variants/{variantId:guid}", UpdateVariantAsync);
        admin.MapPut("/variants/{variantId:guid}/default-layout", SetDefaultLayoutAsync);

        admin.MapGet("/layouts", (Guid? machineId, AdminConfigurationService service, CancellationToken ct) => service.ListLayoutsAsync(machineId, ct));
        admin.MapPost("/layouts", CreateLayoutAsync);
        admin.MapPost("/layouts/{layoutId:guid}/versions", CreateLayoutVersionAsync);
        admin.MapPut("/layouts/{layoutId:guid}", UpdateLayoutAsync);
        admin.MapPost("/layouts/{layoutId:guid}/publish", PublishLayoutAsync);
        admin.MapPost("/layouts/{layoutId:guid}/archive", ArchiveLayoutAsync);
        admin.MapGet("/layouts/{layoutId:guid}/slots", (Guid layoutId, AdminConfigurationService service, CancellationToken ct) => service.ListSlotsAsync(layoutId, ct));
        admin.MapPost("/layouts/{layoutId:guid}/slots", CreateSlotAsync);
        admin.MapPut("/layouts/{layoutId:guid}/slots/{slotId:guid}", UpdateSlotAsync);

        // Customer UX — Automatic Machine Slot Resolution: curated
        // master.layout_slot_parts CRUD, so a customer correction no longer
        // requires a SQL edit.
        admin.MapGet("/layout-slots/{slotId:guid}/parts", (Guid slotId, AdminConfigurationService service, CancellationToken ct) => service.ListSlotPartsAsync(slotId, ct));
        admin.MapPut("/layout-slots/{slotId:guid}/parts/{partId:guid}", AssignSlotPartAsync);
        admin.MapDelete("/layout-slots/{slotId:guid}/parts/{partId:guid}", RemoveSlotPartAsync);

        admin.MapGet("/parts", (AdminConfigurationService service, CancellationToken ct) => service.ListPartsAsync(ct));
        admin.MapPost("/parts", CreatePartAsync);
        admin.MapPut("/parts/{partId:guid}", UpdatePartAsync);

        admin.MapGet("/product-groups", (Guid? variantId, AdminConfigurationService service, CancellationToken ct) => service.ListProductGroupsAsync(variantId, ct));
        admin.MapPost("/product-groups", CreateProductGroupAsync);
        admin.MapPut("/product-groups/{groupId:guid}", UpdateProductGroupAsync);
        admin.MapPut("/product-groups/{groupId:guid}/parts/{partId:guid}", SetProductGroupPartAsync);
        admin.MapDelete("/product-groups/{groupId:guid}/parts/{partId:guid}", RemoveProductGroupPartAsync);

        admin.MapGet("/containers", (AdminConfigurationService service, CancellationToken ct) => service.ListContainersAsync(ct));
        admin.MapPost("/containers", CreateContainerAsync);
        admin.MapPut("/containers/{containerId:guid}", UpdateContainerAsync);

        admin.MapGet("/data-issues", (AdminConfigurationService service, CancellationToken ct) => service.ListDataIssuesAsync(ct));
        admin.MapGet("/audit-logs", (int? limit, Guid? actorUserId, string? entityType, AdminConfigurationService service, CancellationToken ct) =>
            service.ListAuditAsync(limit ?? 100, actorUserId, entityType, ct));

        admin.MapGet("/rds/status", RdsStatusAsync);
        admin.MapGet("/rds/robots", ListRdsRobotsAsync);
        admin.MapGet("/rds/alarms", ListRdsRobotAlarmsAsync);
        admin.MapGet("/rds/navigation-profiles", (RdsIntegrationService service, CancellationToken ct) => service.ListNavigationProfilesAsync(ct));
        admin.MapPost("/rds/worksites/sync", SyncRdsWorkSitesAsync);
        admin.MapGet("/rds/worksites", ListRdsWorkSitesAsync);
        admin.MapGet("/rds/slot-mappings", (Guid? layoutId, RdsIntegrationService service, CancellationToken ct) => service.ListSlotMappingsAsync(layoutId, ct));
        admin.MapPut("/layout-slots/{slotId:guid}/rds-mapping", SetRdsMappingAsync);
        admin.MapDelete("/layout-slots/{slotId:guid}/rds-mapping", RemoveRdsMappingAsync);
        admin.MapGet("/rds/task-templates", (IRdsExecutionStore store, CancellationToken ct) => store.ListTaskTemplatesAsync(ct));
        admin.MapPost("/rds/task-templates/validate", ValidateRdsTaskTemplateAsync);
        admin.MapPost("/rds/commissioning/capture", CaptureRdsCommissioningDiscoveryAsync);

        return endpoints;
    }

    /// <summary>
    /// Admin diagnostics only. Reports the read-side status from the integration
    /// service and joins it with the two execution-side facts that live outside it:
    /// whether TKG is currently permitted to dispatch, and which RDSCore endpoint it
    /// would dispatch to. Composed here rather than inside RdsIntegrationService so
    /// the read service does not have to take a dependency on the execution
    /// coordinator.
    /// </summary>
    private static async Task<RdsConnectionStatusRecord> RdsStatusAsync(
        HttpContext context,
        RdsIntegrationService service,
        RdsExecutionCoordinator execution,
        RdsCoreExecutionOptions coreExecution,
        CancellationToken ct)
    {
        var status = await service.GetStatusAsync(CorrelationId(context), ct);
        return status with
        {
            DispatchEnabled = execution.IsDispatchEnabled,
            CoreBaseAddressDisplaySafe = coreExecution.BaseAddressDisplaySafe,
        };
    }

    private static Task<IReadOnlyList<RdsRobotAdminRecord>> ListRdsRobotsAsync(HttpContext context, RdsIntegrationService service, CancellationToken ct) =>
        service.ListRobotsForAdminAsync(CorrelationId(context), ct);

    private static Task<IReadOnlyList<RdsRobotAlarmAdminRecord>> ListRdsRobotAlarmsAsync(HttpContext context, RdsIntegrationService service, CancellationToken ct) =>
        service.ListRobotAlarmsForAdminAsync(CorrelationId(context), ct);

    private static async Task<RdsCommissioningCaptureResult> CaptureRdsCommissioningDiscoveryAsync(
        RdsCommissioningCaptureApiRequest request,
        HttpContext context,
        RdsCommissioningDiscoveryService service,
        IApiDataStore dataStore,
        CancellationToken ct)
    {
        if (!request.ConfirmReadOnlyDiscovery)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "RDSCORE_DISCOVERY_CONFIRMATION_REQUIRED",
                "An explicit read-only discovery confirmation is required.");
        }

        var correlationId = CorrelationId(context);
        var result = await service.CaptureAsync(
            new(
                request.ConnectivityProbes,
                request.DistanceProbes,
                request.OrderIds,
                request.ExternalOrderIds,
                request.BlockIds),
            correlationId,
            ct);
        var userAgent = context.Request.Headers.UserAgent.ToString();
        await dataStore.WriteAuditAsync(
            new AuditEntry(
                context.User.UserId(),
                null,
                "RDS_COMMISSIONING_DISCOVERY_CAPTURED",
                "RDSCORE_DISCOVERY_SNAPSHOT",
                result.SnapshotSha256,
                null,
                JsonSerializer.Serialize(new
                {
                    result.Snapshot.SchemaVersion,
                    result.Snapshot.Source.SafeOrigin,
                    RobotCount = result.Snapshot.Robots.Count,
                    WorkSiteCount = result.Snapshot.WorkSites.Count,
                    EndpointCount = result.Snapshot.EndpointResults.Count,
                    FindingCount = result.Findings.Count,
                    result.SnapshotSha256
                }),
                context.Connection.RemoteIpAddress?.ToString(),
                userAgent.Length > 2000 ? userAgent[..2000] : userAgent,
                correlationId),
            ct);

        return result;
    }

    private static Task<RdsWorkSiteSyncRecord> SyncRdsWorkSitesAsync(
        SyncRdsWorkSitesRequest request,
        HttpContext context,
        RdsIntegrationService service,
        CancellationToken ct) =>
        service.SyncWorkSitesAsync(request.NavigationProfileId, CorrelationId(context), ct);

    private static Task<IReadOnlyList<RdsMirroredWorkSiteRecord>> ListRdsWorkSitesAsync(
        Guid navigationProfileId,
        string? siteId,
        string? area,
        string? group,
        int? type,
        int? filled,
        int? locked,
        int? disabled,
        int? syncFailed,
        RdsIntegrationService service,
        CancellationToken ct) =>
        service.ListWorkSitesAsync(new(navigationProfileId, siteId, area, group, type, filled, locked, disabled, syncFailed), ct);

    private static Task<RdsSlotMappingRecord> SetRdsMappingAsync(
        Guid slotId,
        SetRdsWorkSiteMappingRequest request,
        HttpContext context,
        RdsIntegrationService service,
        CancellationToken ct) =>
        service.SetSlotMappingAsync(slotId, request.RdsWorksiteId, MutationContext(context), ct);

    private static async Task<IResult> RemoveRdsMappingAsync(
        Guid slotId,
        HttpContext context,
        RdsIntegrationService service,
        CancellationToken ct)
    {
        await service.RemoveSlotMappingAsync(slotId, MutationContext(context), ct);
        return Results.NoContent();
    }

    private static async Task<RdsValidationResult> ValidateRdsTaskTemplateAsync(
        ValidateRdsTaskTemplateRequest request,
        HttpContext context,
        IRdsExecutionStore store,
        IRdsExecutionClient client,
        CancellationToken cancellationToken)
    {
        var template = (await store.ListTaskTemplatesAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == request.TaskTemplateId)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "TASK_TEMPLATE_NOT_FOUND", "The task template does not exist.");
        if (string.IsNullOrWhiteSpace(template.RdsTaskLabel))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "RDS_TASK_LABEL_REQUIRED", "The task template has no RDS task label.");
        }

        var preview = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (template.Config.ValueKind == JsonValueKind.Object
            && template.Config.TryGetProperty("rdsInputBindings", out var bindings)
            && bindings.ValueKind == JsonValueKind.Array)
        {
            foreach (var binding in bindings.EnumerateArray())
            {
                if (binding.ValueKind == JsonValueKind.Object
                    && binding.TryGetProperty("param", out var param)
                    && param.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(param.GetString()))
                {
                    preview[param.GetString()!] = "TKG_VALIDATION_ONLY";
                }
            }
        }

        return await client.ValidateTransportAsync(
            new(Guid.NewGuid().ToString(), template.RdsTaskLabel, preview),
            CorrelationId(context),
            cancellationToken);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        IApiDataStore dataStore,
        Pbkdf2PasswordHasher passwordHasher,
        JwtTokenService tokenService,
        CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim().ToLowerInvariant() ?? string.Empty;
        if (username.Length is < 1 or > 100 || string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_LOGIN_REQUEST", "Username and password are required and must fit the supported limits.");
        }

        var user = await dataStore.FindUserByUsernameAsync(username, cancellationToken);
        var passwordValid = user is null
            ? passwordHasher.VerifyUnknownUser(request.Password)
            : passwordHasher.Verify(request.Password, user.PasswordHash);
        if (user is null || !user.IsActive || !passwordValid || !user.Roles.Contains(RoleCodes.Admin, StringComparer.Ordinal))
        {
            await WriteLoginAuditAsync(dataStore, context, null, "ADMIN_WEB_LOGIN_FAILED", username, new { reason = "INVALID_CREDENTIALS" }, cancellationToken);
            throw new ApiException(StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS", "The username or password is invalid.");
        }

        await dataStore.MarkLoginSucceededAsync(user.Id, cancellationToken);
        var token = tokenService.IssueAdmin(user);
        await WriteLoginAuditAsync(dataStore, context, user.Id, "ADMIN_WEB_LOGIN_SUCCEEDED", user.Id.ToString(), new { roles = user.Roles }, cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new AdminLoginResponse(
            token.Token,
            "Bearer",
            token.ExpiresAt,
            new AuthenticatedUserResponse(user.Id, user.Username, user.FullName, user.EmployeeNo, user.Roles),
            ClientTypes.AdminWeb));
    }

    private static async Task<IResult> CreateUserAsync(
        CreateAdminUserRequest request,
        HttpContext context,
        AdminConfigurationService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateUserAsync(
            new CreateUserCommand(request.Username, request.Password, request.FullName, request.EmployeeNo, request.Roles),
            MutationContext(context),
            cancellationToken);
        return Created(context, $"/api/v1/admin/users/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateUserAsync(Guid userId, UpdateAdminUserRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateUserAsync(userId, new UpdateUserCommand(request.FullName, request.EmployeeNo, request.IsActive, Version(context)), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> ResetPasswordAsync(Guid userId, ResetPasswordRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.ResetPasswordAsync(userId, request.NewPassword, Version(context), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> SetRolesAsync(Guid userId, SetRolesRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.SetUserRolesAsync(userId, request.Roles, Version(context), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> CreateDeviceAsync(CreateAdminDeviceRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateDeviceAsync(new CreateDeviceCommand(request.Code, request.Name, request.Type, request.IsActive, request.Metadata, request.MachineId), MutationContext(context), ct);
        return Created(context, $"/api/v1/admin/devices/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateDeviceAsync(Guid deviceId, UpdateAdminDeviceRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateDeviceAsync(deviceId, new UpdateDeviceCommand(request.Name, request.Type, request.IsActive, request.Metadata, Version(context), request.MachineId), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateMachineAsync(Guid machineId, UpdateAdminMachineRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateMachineAsync(machineId, new UpdateMachineCommand(request.DisplayName, request.DisplayOrder, request.IsActive, Version(context)), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> SetMachinePermissionAsync(Guid userId, Guid machineId, SetMachinePermissionRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var permission = await service.SetMachinePermissionAsync(
            userId,
            new MachinePermissionRecord(machineId, request.CanView, request.CanOperate, request.CanChangeVariant, request.CanManageSlots),
            Version(context),
            MutationContext(context),
            ct);
        return Results.Ok(permission);
    }

    private static async Task<IResult> RemoveMachinePermissionAsync(Guid userId, Guid machineId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        await service.RemoveMachinePermissionAsync(userId, machineId, Version(context), MutationContext(context), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateVariantAsync(CreateAdminVariantRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateVariantAsync(new CreateVariantCommand(request.MachineId, request.Code, request.Name, request.DisplayOrder), MutationContext(context), ct);
        return Created(context, $"/api/v1/admin/variants/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateVariantAsync(Guid variantId, UpdateAdminVariantRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateVariantAsync(variantId, new UpdateVariantCommand(request.Name, request.DisplayOrder, request.IsActive, Version(context)), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> SetDefaultLayoutAsync(Guid variantId, SetDefaultLayoutRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.SetDefaultLayoutAsync(variantId, request.LayoutId, Version(context), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> CreateLayoutAsync(CreateAdminLayoutRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateLayoutAsync(new CreateLayoutCommand(request.MachineId, request.Code, request.Name), MutationContext(context), ct);
        return Created(context, $"/api/v1/admin/layouts/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> CreateLayoutVersionAsync(Guid layoutId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateLayoutVersionAsync(layoutId, Version(context), MutationContext(context), ct);
        return Created(context, $"/api/v1/admin/layouts/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateLayoutAsync(Guid layoutId, UpdateAdminLayoutRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateLayoutAsync(layoutId, new UpdateLayoutCommand(request.Name, Version(context)), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> PublishLayoutAsync(Guid layoutId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.PublishLayoutAsync(layoutId, Version(context), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> ArchiveLayoutAsync(Guid layoutId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.ArchiveLayoutAsync(layoutId, Version(context), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> CreateSlotAsync(Guid layoutId, CreateAdminSlotRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateSlotAsync(
            layoutId,
            new CreateSlotCommand(request.Code, request.DisplayName, request.Role, request.DefaultContainerTypeId, request.X, request.Y, request.Width, request.Height,
                request.DisplayOrder, request.AllowsEmptyLoad, request.AllowsFullLoad, request.Metadata),
            Version(context),
            MutationContext(context),
            ct);
        return Created(context, $"/api/v1/admin/layouts/{layoutId}/slots/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateSlotAsync(Guid layoutId, Guid slotId, UpdateAdminSlotRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateSlotAsync(
            layoutId,
            slotId,
            new UpdateSlotCommand(request.DisplayName, request.Role, request.DefaultContainerTypeId, request.X, request.Y, request.Width, request.Height,
                request.DisplayOrder, request.AllowsEmptyLoad, request.AllowsFullLoad, request.IsEnabled, request.Metadata, Version(context), ParentVersion(context)),
            MutationContext(context),
            ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> AssignSlotPartAsync(Guid slotId, Guid partId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        await service.AssignSlotPartAsync(slotId, partId, MutationContext(context), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveSlotPartAsync(Guid slotId, Guid partId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        await service.RemoveSlotPartAsync(slotId, partId, MutationContext(context), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreatePartAsync(CreateAdminPartRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreatePartAsync(new CreatePartCommand(request.Code, request.Description), MutationContext(context), ct);
        return Created(context, $"/api/v1/admin/parts/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdatePartAsync(Guid partId, UpdateAdminPartRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdatePartAsync(partId, new UpdatePartCommand(request.Description, request.IsActive, Version(context)), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> CreateProductGroupAsync(CreateAdminProductGroupRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateProductGroupAsync(new CreateProductGroupCommand(request.VariantId, request.MainPartId, request.Name, request.DisplayOrder), MutationContext(context), ct);
        return Created(context, $"/api/v1/admin/product-groups/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateProductGroupAsync(Guid groupId, UpdateAdminProductGroupRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateProductGroupAsync(groupId, new UpdateProductGroupCommand(request.Name, request.DisplayOrder, request.IsActive, Version(context)), MutationContext(context), ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> SetProductGroupPartAsync(Guid groupId, Guid partId, SetAdminProductGroupPartRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.SetProductGroupPartAsync(
            groupId,
            partId,
            new SetProductGroupPartCommand(request.ContainerTypeId, request.RelationType, request.LotQuantity, request.DisplayOrder, request.IsActive, Version(context)),
            MutationContext(context),
            ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static async Task<IResult> RemoveProductGroupPartAsync(Guid groupId, Guid partId, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        await service.RemoveProductGroupPartAsync(groupId, partId, Version(context), MutationContext(context), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateContainerAsync(CreateAdminContainerRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var created = await service.CreateContainerAsync(
            new CreateContainerCommand(request.Code, request.Name, request.Category, request.LengthMm, request.WidthMm, request.HeightMm, request.CapacityQuantity),
            MutationContext(context),
            ct);
        return Created(context, $"/api/v1/admin/containers/{created.Id}", created, created.ConcurrencyToken);
    }

    private static async Task<IResult> UpdateContainerAsync(Guid containerId, UpdateAdminContainerRequest request, HttpContext context, AdminConfigurationService service, CancellationToken ct)
    {
        var updated = await service.UpdateContainerAsync(
            containerId,
            new UpdateContainerCommand(request.Name, request.Category, request.LengthMm, request.WidthMm, request.HeightMm, request.CapacityQuantity, request.IsActive, Version(context)),
            MutationContext(context),
            ct);
        return Versioned(context, updated, updated.ConcurrencyToken);
    }

    private static long Version(HttpContext context) => ParseVersion(context, "If-Match", context.Request.Headers.IfMatch.ToString());

    private static long ParentVersion(HttpContext context) => ParseVersion(context, "X-Layout-Version", context.Request.Headers["X-Layout-Version"].ToString());

    private static long ParseVersion(HttpContext context, string header, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ApiException(StatusCodes.Status428PreconditionRequired, "IF_MATCH_REQUIRED", $"{header} is required for this mutation.");
        }

        var normalized = value.Trim().Trim('"');
        return long.TryParse(normalized, out var version) && version > 0
            ? version
            : throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_CONCURRENCY_TOKEN", $"{header} must contain the numeric concurrency token returned by the API.");
    }

    private static AdminMutationContext MutationContext(HttpContext context)
    {
        var correlationId = CorrelationId(context);
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return new AdminMutationContext(
            context.User.UserId(),
            correlationId,
            context.Connection.RemoteIpAddress?.ToString(),
            userAgent.Length > 2000 ? userAgent[..2000] : userAgent);
    }

    private static Guid CorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid id ? id : Guid.NewGuid();

    private static IResult Versioned<T>(HttpContext context, T value, long version)
    {
        context.Response.Headers.ETag = $"\"{version}\"";
        return Results.Ok(value);
    }

    private static IResult Created<T>(HttpContext context, string location, T value, long version)
    {
        context.Response.Headers.ETag = $"\"{version}\"";
        return Results.Created(location, value);
    }

    private static async Task WriteLoginAuditAsync(
        IApiDataStore dataStore,
        HttpContext context,
        Guid? actorUserId,
        string action,
        string entityId,
        object after,
        CancellationToken cancellationToken)
    {
        var correlationId = context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid id ? id : Guid.NewGuid();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        await dataStore.WriteAuditAsync(
            new AuditEntry(
                actorUserId,
                null,
                action,
                "AUTH_USER",
                entityId,
                null,
                JsonSerializer.Serialize(after),
                context.Connection.RemoteIpAddress?.ToString(),
                userAgent.Length > 2000 ? userAgent[..2000] : userAgent,
                correlationId),
            cancellationToken);
    }
}

public sealed record RdsCommissioningCaptureApiRequest(
    bool ConfirmReadOnlyDiscovery,
    IReadOnlyList<RdsConnectivityProbe>? ConnectivityProbes = null,
    IReadOnlyList<RdsDistanceProbe>? DistanceProbes = null,
    IReadOnlyList<string>? OrderIds = null,
    IReadOnlyList<string>? ExternalOrderIds = null,
    IReadOnlyList<string>? BlockIds = null);
