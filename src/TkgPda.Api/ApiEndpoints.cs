using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapTkgPdaApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login");

        endpoints.MapPost("/api/v1/auth/session/heartbeat", HeartbeatAsync)
            .RequireAuthorization();

        endpoints.MapPost("/api/v1/auth/logout", LogoutAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/v1/auth/me", CurrentUserAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/v1/machines", ListMachinesAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/v1/machines/{machineId:guid}/operational-context", OperationalContextAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/v1/machines/{machineId:guid}/parts/{partId:guid}/compatible-variants", CompatibleVariantsAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/v1/machines/{machineId:guid}/part-options", MachinePartOptionsAsync)
            .RequireAuthorization();

        endpoints.MapPost("/api/v1/machines/{machineId:guid}/variant-selection", SwitchVariantAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        endpoints.MapPost("/api/v1/transport-requests", CreateTransportAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapPost("/api/v1/transport-batches", CreateTransportBatchAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapGet("/api/v1/machines/{machineId:guid}/container-positions", ContainerPositionsAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapPost("/api/v1/machines/{machineId:guid}/container-actions", CreateContainerActionAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapPost("/api/v1/machines/{machineId:guid}/container-actions/batch", CreateContainerActionBatchAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapPost("/api/v1/machines/{machineId:guid}/container-actions/remove-all", RemoveAllContainersAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapGet("/api/v1/transport-requests/queue", OperatorQueueAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapGet("/api/v1/transport-requests/{requestId:guid}", GetTransportAsync)
            .RequireAuthorization();

        endpoints.MapPost("/api/v1/transport-requests/{requestId:guid}/cancel", CancelTransportAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapPost("/api/v1/transport-requests/{requestId:guid}/ready", MarkTransportReadyAsync)
            .RequireAuthorization(PolicyNames.SemiFinishedWarehouse);

        endpoints.MapGet("/api/v1/warehouse/preparation-requests", ListWarehousePreparationRequestsAsync)
            .RequireAuthorization(PolicyNames.SemiFinishedWarehouse);

        endpoints.MapPut("/api/v1/machines/{machineId:guid}/slots/{slotId:guid}/load-state", UpdateLoadStateAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapGet("/api/v1/rds/robot-alarm-summary", RobotAlarmSummaryAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapGet("/api/v1/rds/robots", ListRobotsAsync)
            .RequireAuthorization(PolicyNames.Operator);

        endpoints.MapPost("/api/v1/rds/robots/pause", PauseRobotAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        endpoints.MapPost("/api/v1/rds/robots/resume", ResumeRobotAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        endpoints.MapPost("/api/v1/rds/robots/clear-errors", ClearRobotErrorsAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        endpoints.MapPost("/api/v1/rds/robots/dispatchable", SetRobotDispatchableAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        // Conservative product-policy choice: no repository or customer evidence was
        // found that a Line/Warehouse Operator should reprioritise a transport, so
        // priority mutation is restricted to Admin/Supervisor until one is cited.
        // Machine authorization and auditing are unchanged.
        endpoints.MapPost("/api/v1/transport-requests/{requestId:guid}/priority", SetRequestPriorityAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        endpoints.MapPost("/api/v1/transport-requests/{requestId:guid}/manual-finish", ManualFinishRequestAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        endpoints.MapPost("/api/v1/transport-requests/{requestId:guid}/redo", RedoRequestAsync)
            .RequireAuthorization(PolicyNames.VariantManager);

        // GET /api/v1/rds/orders is deliberately absent. It was an unfiltered,
        // fleet-wide RDSCore passthrough reachable by LINE_OPERATOR that leaked raw
        // order UUIDs, vehicles, block ids and WorkSite/point codes with no machine
        // scoping, and it had no client consumer. A properly scoped Admin
        // request/order correlation view belongs to the Task/Order Explorer work
        // package, not to the Operator surface.

        return endpoints;
    }

    // Customer Monitoring — Robot Alarm Visibility: coarse fleet-wide signal
    // only. No verified per-machine robot mapping exists yet, so this must
    // not leak individual robot identities or diagnostic detail to Operator.
    private static Task<RdsRobotAlarmOperatorSummary> RobotAlarmSummaryAsync(
        HttpContext context,
        RdsIntegrationService service,
        CancellationToken cancellationToken) =>
        service.GetRobotAlarmSummaryForOperatorAsync(CorrelationId(context), cancellationToken);

    internal static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        IApiDataStore dataStore,
        IFieldSessionStore sessionStore,
        FieldSessionOptions sessionOptions,
        TimeProvider timeProvider,
        JwtOptions jwtOptions,
        Pbkdf2PasswordHasher passwordHasher,
        JwtTokenService tokenService,
        CancellationToken cancellationToken)
    {
        // WP-01A: Operator login accepts either the legacy username (transitional,
        // still used by the current Flutter client) or the customer employee /
        // registry number (sicil). Exactly one identity must be supplied.
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            username = null;
        }

        var employeeNumber = EmployeeNumber.Canonicalize(request.EmployeeNumber);
        if (username is not null && employeeNumber is not null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "AMBIGUOUS_LOGIN_IDENTITY", "Provide either a username or an employee number, not both.");
        }

        if (username is null && employeeNumber is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_LOGIN_REQUEST", "A username or employee number is required.");
        }

        // Production customer requirement: the employee-number (sicil) path is
        // passwordless — identity is established by employeeNumber + an
        // already-authorized device, and the employee number itself is not a
        // secret. The legacy username path is unchanged and still requires a
        // password.
        if (employeeNumber is not null)
        {
            if (employeeNumber.Length > 100)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_LOGIN_REQUEST", "The employee number must fit the supported limit.");
            }
        }
        else if (username!.Length > 100 || string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_LOGIN_REQUEST", "The identity and password are required and must fit the supported limits.");
        }

        var deviceCode = context.Request.Headers[ApiHeaders.DeviceCode].ToString().Trim();
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "DEVICE_CONTEXT_REQUIRED", "Login requires an X-Device-Code header.");
        }

        var device = await dataStore.FindDeviceAsync(deviceCode, cancellationToken);
        if (device is null || !device.IsActive || device.Type != "PDA")
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "DEVICE_NOT_AUTHORIZED", "The client device is unknown, inactive or not a PDA.");
        }

        AuthUserRecord? user;
        bool credentialsValid;
        if (employeeNumber is not null)
        {
            user = await dataStore.FindUserByEmployeeNumberAsync(EmployeeNumber.NormalizeForLookup(employeeNumber)!, cancellationToken);
            credentialsValid = true; // Passwordless sicil path: no password is verified.
        }
        else
        {
            user = await dataStore.FindUserByUsernameAsync(username!, cancellationToken);
            credentialsValid = user is null
                ? passwordHasher.VerifyUnknownUser(request.Password!)
                : passwordHasher.Verify(request.Password!, user.PasswordHash);
        }

        if (user is null || !user.IsActive || !credentialsValid || user.Roles.Count == 0)
        {
            await WriteAuditAsync(
                dataStore,
                context,
                null,
                device.Id,
                "AUTH_LOGIN_FAILED",
                "AUTH_USER",
                employeeNumber ?? username,
                new { reason = "INVALID_CREDENTIALS" },
                cancellationToken);
            throw new ApiException(StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS", "The username or password is invalid.");
        }

        var now = timeProvider.GetUtcNow();
        var tokenExpiresAt = now.AddMinutes(jwtOptions.ExpiryMinutes);
        var sessionResult = await sessionStore.AcquireLoginSessionAsync(
            user.Id,
            device.Id,
            now,
            TimeSpan.FromSeconds(sessionOptions.LeaseSeconds),
            tokenExpiresAt,
            cancellationToken);

        if (sessionResult.Status == AcquireSessionStatus.ActiveSessionExists)
        {
            await WriteAuditAsync(
                dataStore,
                context,
                user.Id,
                device.Id,
                "AUTH_LOGIN_BLOCKED_ACTIVE_SESSION",
                "AUTH_SESSION",
                sessionResult.ExistingSessionId?.ToString(),
                new
                {
                    existingSessionId = sessionResult.ExistingSessionId,
                    existingDeviceId = sessionResult.ExistingDeviceId,
                    existingDeviceCode = sessionResult.ExistingDeviceCode,
                    attemptedDevice = device.Code
                },
                cancellationToken);

            throw new ApiException(
                StatusCodes.Status409Conflict,
                "ACTIVE_SESSION_EXISTS",
                "Bu sicil başka bir cihazda aktif olarak kullanılıyor. Mevcut oturum kapandıktan sonra tekrar deneyin.");
        }

        var session = sessionResult.Session!;
        try
        {
            await WriteAuditAsync(
                dataStore,
                context,
                user.Id,
                device.Id,
                "AUTH_SESSION_CREATED",
                "AUTH_SESSION",
                session.Id.ToString(),
                new
                {
                    sessionId = session.Id,
                    leaseExpiresAt = session.LeaseExpiresAt,
                    tokenExpiresAt = session.TokenExpiresAt
                },
                cancellationToken);

            await dataStore.MarkLoginSucceededAsync(user.Id, cancellationToken);
            var token = tokenService.Issue(user, device, session.Id);
            await WriteAuditAsync(
                dataStore,
                context,
                user.Id,
                device.Id,
                "AUTH_LOGIN_SUCCEEDED",
                "AUTH_USER",
                user.Id.ToString(),
                new { roles = user.Roles, device = device.Code, sessionId = session.Id },
                cancellationToken);
            context.Response.Headers.CacheControl = "no-store";

            return Results.Ok(new LoginResponse(
                token.Token,
                "Bearer",
                token.ExpiresAt,
                new AuthenticatedUserResponse(user.Id, user.Username, user.FullName, user.EmployeeNo, user.Roles),
                new DeviceResponse(device.Id, device.Code, device.Name, device.Type, device.MachineId)));
        }
        catch
        {
            try
            {
                await sessionStore.RevokeSessionAsync(session.Id, user.Id, "LOGIN_FINALIZATION_FAILED", timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch
            {
                // Do not mask original exception
            }

            throw;
        }
    }

    private static async Task<IResult> HeartbeatAsync(
        HttpContext context,
        IFieldSessionStore sessionStore,
        FieldSessionOptions sessionOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = context.Items.TryGetValue(ApiContextKeys.Session, out var sVal) && sVal is UserSessionRecord s
            ? s
            : null;
        var sessionId = session?.Id
            ?? (Guid.TryParse(context.User.FindFirstValue(ApiClaimNames.SessionId), out var sid) ? sid : Guid.Empty);

        if (sessionId == Guid.Empty)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "SESSION_NOT_ACTIVE", "The access token has no valid session identifier.");
        }

        var now = timeProvider.GetUtcNow();
        var updated = await sessionStore.HeartbeatAsync(
            sessionId,
            now,
            TimeSpan.FromSeconds(sessionOptions.LeaseSeconds),
            cancellationToken);

        if (updated is null)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "SESSION_NOT_ACTIVE", "Oturum aktif değil veya yenilenemedi.");
        }

        return Results.Ok(new HeartbeatResponse(updated.Id, updated.LeaseExpiresAt, updated.TokenExpiresAt));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IFieldSessionStore sessionStore,
        IApiDataStore dataStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = context.User;
        if (principal.IsDeviceBound())
        {
            var sessionIdStr = principal.FindFirstValue(ApiClaimNames.SessionId);
            if (Guid.TryParse(sessionIdStr, out var sessionId))
            {
                var userId = principal.UserId();
                var device = context.Items.TryGetValue(ApiContextKeys.Device, out var devVal) && devVal is DeviceRecord d ? d : null;
                var now = timeProvider.GetUtcNow();
                var revoked = await sessionStore.RevokeSessionAsync(sessionId, userId, "USER_LOGOUT", now, cancellationToken);
                if (revoked && device is not null)
                {
                    await WriteAuditAsync(
                        dataStore,
                        context,
                        userId,
                        device.Id,
                        "AUTH_SESSION_LOGOUT",
                        "AUTH_SESSION",
                        sessionId.ToString(),
                        new { reason = "USER_LOGOUT" },
                        cancellationToken);
                }
            }
        }

        return Results.Ok(new { message = "Logged out successfully" });
    }

    private static IResult CurrentUserAsync(HttpContext context)
    {
        var principal = context.User;
        var device = context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord record
            ? new DeviceResponse(record.Id, record.Code, record.Name, record.Type, record.MachineId)
            : null;
        return Results.Ok(new
        {
            id = principal.UserId(),
            username = principal.Identity?.Name,
            fullName = principal.FindFirstValue(ApiClaimNames.FullName),
            roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Order().ToArray(),
            clientType = principal.FindFirstValue(ApiClaimNames.ClientType),
            device
        });
    }

    private static async Task<IResult> ListMachinesAsync(
        HttpContext context,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        var device = context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord record ? record : null;
        var isAdmin = context.User.IsAdministrator();
        var isSupervisor = context.User.IsInRole(RoleCodes.Supervisor);
        var isLineOperator = context.User.IsInRole(RoleCodes.LineOperator);

        // Warehouse-only users do not have machine access
        if (!isAdmin && !isSupervisor && !isLineOperator)
        {
            return Results.Ok(Array.Empty<MachineSummaryRecord>());
        }

        // LINE_OPERATOR:
        // Must have a device bound to a machine.
        // If unbound, return empty list (fail-closed, no user_machine_permissions fallback).
        if (isLineOperator && !isAdmin && !isSupervisor)
        {
            if (device?.MachineId is null)
            {
                return Results.Ok(Array.Empty<MachineSummaryRecord>());
            }

            var boundMachine = await dataStore.FindMachineSummaryAsync(device.MachineId.Value, cancellationToken);
            if (boundMachine is null)
            {
                return Results.Ok(Array.Empty<MachineSummaryRecord>());
            }

            var legacyPerm = await dataStore.FindMachinePermissionAsync(context.User.UserId(), boundMachine.Id, cancellationToken);
            if (legacyPerm is not null && !legacyPerm.CanView)
            {
                return Results.Ok(Array.Empty<MachineSummaryRecord>());
            }

            return Results.Ok(new[]
            {
                boundMachine with
                {
                    Permissions = legacyPerm ?? new MachinePermissionRecord(
                        boundMachine.Id,
                        CanView: true,
                        CanOperate: true,
                        CanChangeVariant: false,
                        CanManageSlots: false)
                }
            });
        }

        // Admin or Supervisor on a bound terminal:
        if (device?.MachineId is not null)
        {
            var boundMachine = await dataStore.FindMachineSummaryAsync(device.MachineId.Value, cancellationToken);
            if (boundMachine is null)
            {
                return Results.Ok(Array.Empty<MachineSummaryRecord>());
            }

            var permissions = isAdmin
                ? new MachinePermissionRecord(boundMachine.Id, true, true, true, true)
                : await dataStore.FindMachinePermissionAsync(context.User.UserId(), boundMachine.Id, cancellationToken);

            if (permissions is null || !permissions.CanView)
            {
                return Results.Ok(Array.Empty<MachineSummaryRecord>());
            }

            return Results.Ok(new[]
            {
                boundMachine with { Permissions = permissions }
            });
        }

        // Admin Web or unbound Admin/Supervisor:
        var machines = await dataStore.ListVisibleMachinesAsync(
            context.User.UserId(),
            isAdmin,
            cancellationToken);
        return Results.Ok(machines);
    }

    private static async Task<IResult> OperationalContextAsync(
        Guid machineId,
        HttpContext context,
        MachineAccessService access,
        PdaOperationalContextService operationalContext,
        CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.View, cancellationToken);
        var roles = context.User.FindAll(ClaimTypes.Role)
            .Select(static claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        var result = await operationalContext.GetAsync(
            new PdaActorContext(context.User.UserId(), roles, context.User.IsDeviceBound()),
            machineId,
            context.User.IsAdministrator(),
            cancellationToken);
        return result is null
            ? throw new ApiException(StatusCodes.Status404NotFound, "MACHINE_NOT_FOUND", "The requested machine does not exist or is inactive.")
            : Results.Ok(result);
    }

    private static async Task<IResult> CompatibleVariantsAsync(
        Guid machineId,
        Guid partId,
        [FromQuery] Guid? mainPartId,
        HttpContext context,
        MachineAccessService access,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.View, cancellationToken);
        if (!mainPartId.HasValue || mainPartId.Value == Guid.Empty)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "MAIN_PART_CONTEXT_REQUIRED",
                "Uyumlu varyant sorgusu için ana parça bağlamı gereklidir.");
        }

        var variants = await dataStore.ListCompatibleVariantsAsync(machineId, mainPartId.Value, partId, cancellationToken);
        return Results.Ok(variants);
    }

    private static async Task<IResult> MachinePartOptionsAsync(
        Guid machineId,
        HttpContext context,
        MachineAccessService access,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.View, cancellationToken);
        var options = await dataStore.ListMachinePartOptionsAsync(machineId, cancellationToken);
        return Results.Ok(options);
    }

    private static async Task<IResult> CreateTransportAsync(
        CreateTransportHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        TransportWorkflowService workflow,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        _ = await access.EnsureAsync(context, request.MachineId, MachineAccessKind.Operate, cancellationToken);
        var idempotencyKey = context.Request.Headers[ApiHeaders.IdempotencyKey].ToString().Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "Transport creation requires an Idempotency-Key header.");
        }

        var operation = ParseOperation(request.Operation);
        EnsureTransportOperationAllowed(context.User, operation);
        var command = new CreateTransportRequest(
            idempotencyKey,
            RequestOrigin.Api,
            context.User.UserId(),
            request.MachineId,
            request.VariantId,
            request.LayoutId,
            request.NavigationProfileId,
            request.ProductGroupId,
            request.PartId,
            request.ContainerTypeId,
            operation,
            // Physical positions are resolved by the domain, never supplied by a caller.
            null,
            null,
            request.Priority,
            request.RequiresPreparation);
        var result = await workflow.CreateAsync(command, cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        context.Response.Headers.Location = $"/api/v1/transport-requests/{result.Request.Id}";
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.WasIdempotentReplay ? "TRANSPORT_REQUEST_REPLAYED" : "TRANSPORT_REQUEST_CREATED",
            "TRANSPORT_REQUEST",
            result.Request.Id.ToString(),
            new { result.Request.RequestNo, status = result.Request.Status.ToDatabase() },
            cancellationToken);

        return Results.Json(
            ToResponse(result.Request, result.WasIdempotentReplay),
            statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> ContainerPositionsAsync(
        Guid machineId,
        [FromQuery] Guid variantId,
        [FromQuery] Guid layoutId,
        [FromQuery] Guid productGroupId,
        [FromQuery] Guid partId,
        HttpContext context,
        MachineAccessService access,
        ContainerActionService service,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        var permission = await access.EnsureAsync(context, machineId, MachineAccessKind.View, cancellationToken);
        var actor = new PdaActorContext(
            context.User.UserId(),
            context.User.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToHashSet(StringComparer.Ordinal),
            context.User.IsDeviceBound());
        var positions = await service.ListPositionsAsync(
            new(machineId, variantId, layoutId, productGroupId, partId),
            actor,
            permission.CanOperate,
            cancellationToken);
        return Results.Ok(positions);
    }

    private static async Task<IResult> CreateContainerActionAsync(
        Guid machineId,
        CreateContainerActionHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        ContainerActionService service,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.Operate, cancellationToken);
        var action = OperatorContainerActionCode.Parse(request.Action);
        EnsureContainerActionAllowed(context.User, action);
        var idempotencyKey = RequiredIdempotencyKey(context);
        var result = await service.CreateAsync(
            new(
                idempotencyKey,
                context.User.UserId(),
                machineId,
                request.VariantId,
                request.LayoutId,
                request.NavigationProfileId,
                request.ProductGroupId,
                request.PartId,
                request.PositionNumber,
                action,
                request.Priority,
                request.ProductionSessionId),
            cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.WasIdempotentReplay ? "CONTAINER_ACTION_REPLAYED" : "CONTAINER_ACTION_CREATED",
            "TRANSPORT_REQUEST",
            result.Request.Id.ToString(),
            new { result.Request.RequestNo, result.PositionNumber, result.Action },
            cancellationToken);
        return Results.Json(
            new ContainerActionHttpResponse(
                result.PositionNumber,
                result.Action,
                result.Request.RequestNo,
                result.Request.Status.ToDatabase(),
                result.WasIdempotentReplay),
            statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> RemoveAllContainersAsync(
        Guid machineId,
        RemoveAllContainersHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        ContainerActionService service,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.Operate, cancellationToken);
        if (!context.User.IsInRole(RoleCodes.Admin)
            && !context.User.IsInRole(RoleCodes.Supervisor)
            && !context.User.IsInRole(RoleCodes.LineOperator))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "OPERATION_NOT_ALLOWED_FOR_ROLE", "Rolünüz tüm kasaları kaldırma işlemini başlatmaya yetkili değildir.");
        }

        var result = await service.RemoveAllAsync(
            new(
                RequiredIdempotencyKey(context),
                context.User.UserId(),
                machineId,
                request.VariantId,
                request.LayoutId,
                request.NavigationProfileId,
                request.Priority,
                request.ProductionSessionId),
            cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.Batch.WasIdempotentReplay ? "true" : "false";
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.Batch.WasIdempotentReplay ? "REMOVE_ALL_REPLAYED" : "REMOVE_ALL_CREATED",
            "TRANSPORT_BATCH",
            result.Batch.BatchId.ToString(),
            new { itemCount = result.Batch.Items.Count, result.IgnoredVacantPositions },
            cancellationToken);
        return Results.Json(
            new RemoveAllContainersHttpResponse(
                result.Batch.WasIdempotentReplay,
                result.IgnoredVacantPositions,
                result.Batch.Items.Select(item => new RemoveAllContainerItemHttpResponse(
                    item.RequestNo,
                    item.TaskType.ToDatabase(),
                    item.Status.ToDatabase())).ToArray()),
            statusCode: result.Batch.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> CreateContainerActionBatchAsync(
        Guid machineId,
        CreateContainerActionBatchHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        ContainerActionService service,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.Operate, cancellationToken);
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "BATCH_ITEMS_REQUIRED", "Grup en az bir kasa işlemi içermelidir.");
        }

        var items = request.Items.Select(item =>
        {
            var action = OperatorContainerActionCode.Parse(item.Action);
            EnsureContainerActionAllowed(context.User, action);
            return new ContainerActionBatchItem(item.ProductGroupId, item.PartId, item.PositionNumber, action);
        }).ToArray();
        var result = await service.CreateBatchAsync(
            new ContainerActionBatchCommand(
                RequiredIdempotencyKey(context),
                context.User.UserId(),
                machineId,
                request.VariantId,
                request.LayoutId,
                request.NavigationProfileId,
                items,
                request.Priority,
                request.ProductionSessionId),
            cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.WasIdempotentReplay ? "CONTAINER_ACTION_BATCH_REPLAYED" : "CONTAINER_ACTION_BATCH_CREATED",
            "TRANSPORT_BATCH",
            result.BatchId.ToString(),
            new { itemCount = result.Items.Count },
            cancellationToken);
        return Results.Json(
            ToBatchResponse(result),
            statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static Task<OperatorQueuePageRecord> OperatorQueueAsync(
        [FromQuery] bool? terminal,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromQuery(Name = "terminal_max_age_hours")] int? terminalMaxAgeHours,
        OperatorTransportQueueService service,
        CancellationToken cancellationToken) =>
        service.ListAsync(new(terminal ?? false, offset ?? 0, limit ?? 50, terminalMaxAgeHours ?? 8), cancellationToken);

    private static async Task<IResult> CreateTransportBatchAsync(
        CreateTransportBatchHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        TransportWorkflowService workflow,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        _ = await access.EnsureAsync(context, request.MachineId, MachineAccessKind.Operate, cancellationToken);

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "BATCH_ITEMS_REQUIRED", "A batch must contain at least one item.");
        }

        var items = new List<CreateTransportRequest>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var operation = ParseOperation(item.Operation);
            EnsureTransportOperationAllowed(context.User, operation);
            items.Add(new CreateTransportRequest(
                item.IdempotencyKey,
                RequestOrigin.Api,
                context.User.UserId(),
                item.MachineId,
                item.VariantId,
                item.LayoutId,
                item.NavigationProfileId,
                item.ProductGroupId,
                item.PartId,
                item.ContainerTypeId,
                operation,
                // Physical positions are resolved by the domain, never supplied.
                null,
                null,
                item.Priority,
                item.RequiresPreparation));
        }

        var batchCommand = new CreateTransportBatchRequest(
            request.IdempotencyKey,
            RequestOrigin.Api,
            context.User.UserId(),
            request.MachineId,
            items);
        var result = await workflow.CreateBatchAsync(batchCommand, cancellationToken);

        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.WasIdempotentReplay ? "TRANSPORT_BATCH_REPLAYED" : "TRANSPORT_BATCH_CREATED",
            "TRANSPORT_BATCH",
            result.BatchId.ToString(),
            new { result.BatchId, itemCount = result.Items.Count },
            cancellationToken);

        return Results.Json(
            ToBatchResponse(result),
            statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetTransportAsync(
        Guid requestId,
        HttpContext context,
        IApiDataStore dataStore,
        MachineAccessService access,
        CancellationToken cancellationToken)
    {
        var request = await dataStore.FindTransportRequestAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        _ = await access.EnsureAsync(context, request.MachineId, MachineAccessKind.View, cancellationToken);
        return Results.Ok(ToResponse(request, false));
    }

    private static async Task<IResult> CancelTransportAsync(
        Guid requestId,
        HttpContext context,
        IApiDataStore dataStore,
        MachineAccessService access,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken)
    {
        var existing = await dataStore.FindTransportRequestAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        _ = await access.EnsureAsync(context, existing.MachineId, MachineAccessKind.Operate, cancellationToken);
        EnsureCancellationAllowed(context.User, existing);
        var result = await rdsExecution.CancelAsync(
            requestId,
            context.User.UserId(),
            CorrelationId(context),
            cancellationToken);
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.WasIdempotentReplay ? "TRANSPORT_CANCELLATION_REPLAYED" : result.PendingRdsConfirmation ? "RDS_CANCELLATION_REQUESTED" : "TRANSPORT_REQUEST_CANCELLED",
            "TRANSPORT_REQUEST",
            requestId.ToString(),
            new { status = result.Request.Status.ToDatabase(), result.PendingRdsConfirmation },
            cancellationToken);
        return Results.Ok(ToResponse(result.Request, result.WasIdempotentReplay, result.PendingRdsConfirmation));
    }

    private static async Task<IResult> MarkTransportReadyAsync(
        Guid requestId,
        HttpContext context,
        IApiDataStore dataStore,
        TransportWorkflowService workflow,
        CancellationToken cancellationToken)
    {
        var existing = await dataStore.FindTransportRequestAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        if (!context.User.IsAdministrator())
        {
            var permission = await dataStore.FindMachinePermissionAsync(context.User.UserId(), existing.MachineId, cancellationToken);
            if (permission is null || !permission.CanOperate)
            {
                throw new ApiException(
                    StatusCodes.Status403Forbidden,
                    "MACHINE_ACCESS_DENIED",
                    "The user does not have permission for this machine.");
            }
        }
        var updated = await workflow.MarkReadyAsync(requestId, context.User.UserId(), cancellationToken);
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            "TRANSPORT_REQUEST_MARKED_READY",
            "TRANSPORT_REQUEST",
            requestId.ToString(),
            new { readinessState = updated.ReadinessState.ToDatabase(), updated.ReadyAt },
            cancellationToken);
        return Results.Ok(ToResponse(updated, replay: false));
    }

    private static async Task<IResult> ListWarehousePreparationRequestsAsync(
        HttpContext context,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        var requests = await dataStore.ListWarehousePreparationRequestsAsync(
            context.User.UserId(),
            context.User.IsAdministrator(),
            cancellationToken);
        return Results.Ok(requests);
    }

    private static async Task<IResult> UpdateLoadStateAsync(
        Guid machineId,
        Guid slotId,
        UpdateLoadStateHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        TransportWorkflowService workflow,
        RdsExecutionCoordinator rdsExecution,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = Device(context);
        if (!context.User.IsInRole(RoleCodes.Admin)
            && !context.User.IsInRole(RoleCodes.Supervisor)
            && !context.User.IsInRole(RoleCodes.LineOperator))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "OPERATION_NOT_ALLOWED_FOR_ROLE", "The authenticated role cannot report production load state.");
        }
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.Operate, cancellationToken);
        var target = request.LoadState?.Trim().ToUpperInvariant() switch
        {
            "EMPTY" => LoadState.Empty,
            "FULL" => LoadState.Full,
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "LOAD_STATE_NOT_SELECTABLE", "LoadState must be EMPTY or FULL.")
        };
        var result = await workflow.UpdateLoadStateAsync(
            new(machineId, slotId, target, context.User.UserId()),
            cancellationToken);

        RdsLoadContentSyncResult contentSync;
        try
        {
            contentSync = await rdsExecution.SynchronizeLoadContentAsync(slotId, target, CorrelationId(context), cancellationToken);
        }
        catch (RdsExecutionException error)
        {
            contentSync = new("FAILED", null, null, $"TKG load state was updated; RDS content synchronization failed with {error.Code}.");
        }

        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            result.WasIdempotentReplay ? "LOAD_STATE_REPLAYED" : "LOAD_STATE_REPORTED",
            "SLOT_RUNTIME_STATE",
            slotId.ToString(),
            new
            {
                occupancyState = result.OccupancyState.ToDatabase(),
                loadState = result.LoadState.ToDatabase(),
                result.RowVersion,
                rdsContentSync = contentSync.Status
            },
            cancellationToken);

        return Results.Ok(new UpdateLoadStateHttpResponse(
            result.MachineId,
            result.SlotId,
            result.OccupancyState.ToDatabase(),
            result.LoadState.ToDatabase(),
            result.RowVersion,
            result.WasIdempotentReplay,
            LoadContentSyncHttpResult.From(contentSync)));
    }

    private static async Task<IResult> SwitchVariantAsync(
        Guid machineId,
        VariantSwitchHttpRequest request,
        HttpContext context,
        MachineAccessService access,
        TransportWorkflowService workflow,
        IApiDataStore dataStore,
        CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.ChangeVariant, cancellationToken);
        await workflow.SwitchVariantAsync(
            new VariantSelection(machineId, request.VariantId, request.LayoutId, request.NavigationProfileId, context.User.UserId()),
            cancellationToken);
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            "MACHINE_VARIANT_SWITCHED",
            "MACHINE_RUNTIME_STATE",
            machineId.ToString(),
            request,
            cancellationToken);
        return Results.Ok(new
        {
            machineId,
            activeVariantId = request.VariantId,
            activeLayoutId = request.LayoutId,
            activeNavigationProfileId = request.NavigationProfileId
        });
    }

    private static TransportTaskType ParseOperation(string operation) => operation?.Trim().ToUpperInvariant() switch
    {
        "BRING_EMPTY" => TransportTaskType.BringEmpty,
        "REMOVE_FULL" => TransportTaskType.RemoveFull,
        "REMOVE_EMPTY" => TransportTaskType.RemoveEmpty,
        "BRING_FULL" => TransportTaskType.BringFull,
        _ => throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_OPERATION", "Operation must be BRING_EMPTY, BRING_FULL, REMOVE_FULL or REMOVE_EMPTY.")
    };

    private static void EnsureTransportOperationAllowed(ClaimsPrincipal principal, TransportTaskType operation)
    {
        // REMOVE_EMPTY is internal orchestration vocabulary, not a standalone
        // Operator action. The Operator's business actions are "Kasa Doldu"
        // (REMOVE_FULL) and "Kasa Boş" (BRING_EMPTY); REMOVE_EMPTY exists so the
        // Remove-All workflow can clear an OCCUPIED+EMPTY position, and the domain
        // generates it server-side. It stays in the Domain enum and Remove-All keeps
        // producing it — it is only barred from generic transport creation, which is
        // the Operator business surface. This applies to every role on this route:
        // holding more authority on a PDA does not make an internal operation
        // directly selectable.
        if (operation == TransportTaskType.RemoveEmpty)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "OPERATION_NOT_OPERATOR_SELECTABLE",
                "REMOVE_EMPTY is produced by the Remove-All workflow and cannot be requested directly.");
        }

        var warehouseOnly = principal.IsInRole(RoleCodes.WarehouseOperator)
            && !principal.IsInRole(RoleCodes.Admin)
            && !principal.IsInRole(RoleCodes.Supervisor)
            && !principal.IsInRole(RoleCodes.LineOperator);
        // Both deliveries are warehouse-side work: BRING_EMPTY stages an empty
        // container, BRING_FULL feeds prepared material. Removals stay with the
        // line roles.
        if (warehouseOnly && !TransportRules.DeliversToMachine(operation))
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "OPERATION_NOT_ALLOWED_FOR_ROLE",
                "WAREHOUSE_OPERATOR may create BRING_EMPTY requests but may not initiate line-side removal operations.");
        }
    }

    private static void EnsureContainerActionAllowed(ClaimsPrincipal principal, OperatorContainerAction action)
    {
        var warehouseOnly = principal.IsInRole(RoleCodes.WarehouseOperator)
            && !principal.IsInRole(RoleCodes.Admin)
            && !principal.IsInRole(RoleCodes.Supervisor)
            && !principal.IsInRole(RoleCodes.LineOperator);
        if (warehouseOnly && action != OperatorContainerAction.Empty)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "OPERATION_NOT_ALLOWED_FOR_ROLE",
                "WAREHOUSE_OPERATOR may request an empty container but may not report a full line-side container.");
        }
    }

    private static string RequiredIdempotencyKey(HttpContext context)
    {
        var key = context.Request.Headers[ApiHeaders.IdempotencyKey].ToString().Trim();
        return string.IsNullOrWhiteSpace(key)
            ? throw new ApiException(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "Transport creation requires an Idempotency-Key header.")
            : key;
    }

    private static void EnsureCancellationAllowed(ClaimsPrincipal principal, TransportRequestSnapshot request)
    {
        var warehouseOnly = principal.IsInRole(RoleCodes.WarehouseOperator)
            && !principal.IsInRole(RoleCodes.Admin)
            && !principal.IsInRole(RoleCodes.Supervisor)
            && !principal.IsInRole(RoleCodes.LineOperator);
        if (warehouseOnly && (!TransportRules.DeliversToMachine(request.TaskType) || request.RequestedBy != principal.UserId()))
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "CANCELLATION_NOT_ALLOWED_FOR_ROLE",
                "WAREHOUSE_OPERATOR may cancel only delivery requests created by the same user.");
        }
    }

    private static TransportHttpResponse ToResponse(TransportRequestSnapshot request, bool replay, bool cancellationPending = false) => new(
        request.Id,
        request.RequestNo,
        request.Status.ToDatabase(),
        request.TaskType.ToDatabase(),
        request.MachineId,
        request.VariantId,
        request.LayoutId,
        request.NavigationProfileId,
        request.ProductGroupId,
        request.PartId,
        request.ContainerTypeId,
        request.SourceSlotId,
        request.DestinationSlotId,
        request.Priority,
        replay,
        cancellationPending,
        request.ReadinessState.ToDatabase(),
        request.ReadyAt);

    private static TransportBatchHttpResponse ToBatchResponse(TransportBatchResult result) => new(
        result.BatchId,
        result.MachineId,
        result.WasIdempotentReplay,
        result.Items.Select(item => ToResponse(item, result.WasIdempotentReplay)).ToArray());

    private static DeviceRecord Device(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord device
            ? device
            : throw new ApiException(StatusCodes.Status401Unauthorized, "DEVICE_CONTEXT_REQUIRED", "A validated device context is required.");

    public sealed record RobotVehiclesApiRequest(IReadOnlyList<string>? Vehicles);
    public sealed record SetDispatchableApiRequest(IReadOnlyList<string>? Vehicles, string Type);
    public sealed record SetPriorityApiRequest(int Priority);

    // Operator information boundary: this returns an explicit business projection.
    // Raw RDS telemetry (uuid, vehicle_id, map, station, pose, order/block ids,
    // lock fingerprint) must never be serialized to a Line/Warehouse Operator, and
    // that is enforced here rather than by the client choosing not to render it.
    private static async Task<IResult> ListRobotsAsync(
        HttpContext context,
        RdsIntegrationService service,
        CancellationToken cancellationToken)
    {
        var robots = await service.ListRobotsForOperatorAsync(CorrelationId(context), cancellationToken);
        return Results.Ok(new { robots });
    }

    // Robot-scoped maintenance. These are fleet operations with no request context,
    // so an explicit vehicle list is legitimate — but the list must be non-empty and
    // explicit (never broadcast), Supervisor/Admin only, and every call is audited.
    /// <summary>Logger for audit-write failures, which must never mask the real outcome.</summary>
    private static ILogger MaintenanceLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TkgPda.Api.RobotMaintenance");

    private static async Task<IResult> RobotMaintenanceAsync(
        HttpContext context,
        IApiDataStore dataStore,
        RdsExecutionCoordinator rdsExecution,
        RdsRobotMaintenanceAction action,
        IReadOnlyList<string>? vehicles,
        string? dispatchableType,
        string auditAction,
        object? auditDetail,
        CancellationToken cancellationToken)
    {
        if (vehicles is not { Count: > 0 } || vehicles.Any(string.IsNullOrWhiteSpace))
        {
            return Results.BadRequest(new { error = "VEHICLES_REQUIRED", message = "At least one valid vehicle identifier is required." });
        }

        var correlationId = CorrelationId(context);
        RdsCoreSimpleResult result;
        try
        {
            // Routed through the Application layer so the one global mutation policy
            // applies; motion-enabling maintenance is refused when dispatch is disabled.
            result = await rdsExecution.RobotMaintenanceAsync(action, vehicles, dispatchableType, correlationId, cancellationToken);
        }
        // A refused attempt on a physical robot is exactly the event an audit log exists
        // for. Catching only RdsExecutionException was not enough: the native adapter
        // surfaces RdsCoreNativeOrderException, and HTTP/JSON faults can escape as
        // themselves, so those attempts were never recorded. Cancellation is excluded —
        // a disconnected caller is not a rejected attempt.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var rejectedCode = error switch
            {
                RdsExecutionException rds => rds.Code,
                RdsCoreNativeOrderException native => native.Code,
                _ => error.GetType().Name
            };

            await BestEffortAuditAsync(
                dataStore,
                context,
                auditAction,
                vehicles,
                new { vehicles, detail = auditDetail, accepted = false, rejectedCode },
                "Failed to record the rejected robot maintenance attempt");

            // The original physical failure is rethrown with its stack intact. The
            // mutation is never retried because bookkeeping failed.
            throw;
        }

        await BestEffortAuditAsync(
            dataStore,
            context,
            auditAction,
            vehicles,
            new { vehicles, detail = auditDetail, accepted = result.CoreAccepted, code = result.Code },
            "Robot maintenance succeeded but its audit row could not be written");

        return Results.Json(result, statusCode: result.HttpStatusCode);
    }

    /// <summary>
    /// Post-outcome audit. Once RDS has answered, the PHYSICAL result is authoritative
    /// and the audit row is bookkeeping: it must never change what the caller is told.
    ///
    /// Two things were wrong before. The catch filters excluded
    /// OperationCanceledException, so an audit cancellation escaped — masking the real
    /// physical failure on one path, and turning a completed mutation into a client
    /// error on the other, which invites a retry of something that already happened.
    /// And the write used the request's own token, which is exactly the token most
    /// likely to be cancelled at that moment.
    ///
    /// So this swallows EVERY exception including cancellation, and writes under its
    /// own short bounded token rather than the request token — bounded rather than
    /// CancellationToken.None so a stuck write cannot hang the response.
    /// </summary>
    private static async Task BestEffortAuditAsync(
        IApiDataStore dataStore,
        HttpContext context,
        string auditAction,
        IReadOnlyList<string> vehicles,
        object detail,
        string failureMessage)
    {
        await PostOutcomeAudit.RunAsync(
            token => WriteAuditAsync(
                dataStore,
                context,
                context.User.UserId(),
                Device(context).Id,
                auditAction,
                "ROBOT",
                string.Join(',', vehicles),
                detail,
                token),
            auditFailure => MaintenanceLogger(context).LogError(
                auditFailure,
                "{FailureMessage}: {AuditAction} for request {TraceIdentifier}.",
                failureMessage,
                auditAction,
                context.TraceIdentifier));
    }

    private static Task<IResult> PauseRobotAsync(
        RobotVehiclesApiRequest? request,
        HttpContext context,
        IApiDataStore dataStore,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken) =>
        RobotMaintenanceAsync(
            context, dataStore, rdsExecution, RdsRobotMaintenanceAction.Pause,
            request?.Vehicles, null, "RDS_ROBOT_PAUSE", null, cancellationToken);

    private static Task<IResult> ResumeRobotAsync(
        RobotVehiclesApiRequest? request,
        HttpContext context,
        IApiDataStore dataStore,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken) =>
        RobotMaintenanceAsync(
            context, dataStore, rdsExecution, RdsRobotMaintenanceAction.Resume,
            request?.Vehicles, null, "RDS_ROBOT_RESUME", null, cancellationToken);

    private static Task<IResult> ClearRobotErrorsAsync(
        RobotVehiclesApiRequest? request,
        HttpContext context,
        IApiDataStore dataStore,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken) =>
        RobotMaintenanceAsync(
            context, dataStore, rdsExecution, RdsRobotMaintenanceAction.ClearErrors,
            request?.Vehicles, null, "RDS_ROBOT_CLEAR_ERRORS", null, cancellationToken);

    private static Task<IResult> SetRobotDispatchableAsync(
        SetDispatchableApiRequest request,
        HttpContext context,
        IApiDataStore dataStore,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken) =>
        RobotMaintenanceAsync(
            context, dataStore, rdsExecution, RdsRobotMaintenanceAction.SetDispatchable,
            request.Vehicles, request.Type, "RDS_ROBOT_SET_DISPATCHABLE", new { request.Type }, cancellationToken);

    // Expert recovery actions bind to a real TransportRequest, enforce machine
    // authorization, resolve the exact RDS-assigned target server-side (never from
    // the caller) and write an audit record. See RdsExecutionCoordinator.
    private static async Task<IResult> SetRequestPriorityAsync(
        Guid requestId,
        SetPriorityApiRequest request,
        HttpContext context,
        IApiDataStore dataStore,
        MachineAccessService access,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken)
    {
        var existing = await dataStore.FindTransportRequestAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        _ = await access.EnsureAsync(context, existing.MachineId, MachineAccessKind.Operate, cancellationToken);

        var result = await rdsExecution.SetRequestPriorityAsync(requestId, request.Priority, CorrelationId(context), cancellationToken);
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            "RDS_EXPERT_SET_PRIORITY",
            "TRANSPORT_REQUEST",
            requestId.ToString(),
            new { request.Priority, result.TaskRecordId, result.Accepted },
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ManualFinishRequestAsync(
        Guid requestId,
        HttpContext context,
        IApiDataStore dataStore,
        MachineAccessService access,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken)
    {
        var existing = await dataStore.FindTransportRequestAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        _ = await access.EnsureAsync(context, existing.MachineId, MachineAccessKind.Operate, cancellationToken);

        var result = await rdsExecution.ManualFinishAsync(requestId, CorrelationId(context), cancellationToken);
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            "RDS_EXPERT_MANUAL_FINISH",
            "TRANSPORT_REQUEST",
            requestId.ToString(),
            new { result.Vehicle, result.TaskRecordId, result.Accepted },
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> RedoRequestAsync(
        Guid requestId,
        HttpContext context,
        IApiDataStore dataStore,
        MachineAccessService access,
        RdsExecutionCoordinator rdsExecution,
        CancellationToken cancellationToken)
    {
        var existing = await dataStore.FindTransportRequestAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        _ = await access.EnsureAsync(context, existing.MachineId, MachineAccessKind.Operate, cancellationToken);

        var result = await rdsExecution.RedoFailedAsync(requestId, CorrelationId(context), cancellationToken);
        await WriteAuditAsync(
            dataStore,
            context,
            context.User.UserId(),
            Device(context).Id,
            "RDS_EXPERT_REDO_FAILED",
            "TRANSPORT_REQUEST",
            requestId.ToString(),
            new { result.Vehicle, result.TaskRecordId, result.Accepted },
            cancellationToken);
        return Results.Ok(result);
    }

    private static Guid CorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid id ? id : Guid.NewGuid();

    private static async Task WriteAuditAsync(
        IApiDataStore dataStore,
        HttpContext context,
        Guid? actorUserId,
        Guid? deviceId,
        string action,
        string entityType,
        string? entityId,
        object? after,
        CancellationToken cancellationToken)
    {
        var correlationId = context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid id
            ? id
            : Guid.NewGuid();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        await dataStore.WriteAuditAsync(
            new AuditEntry(
                actorUserId,
                deviceId,
                action,
                entityType,
                entityId,
                null,
                after is null ? null : JsonSerializer.Serialize(after),
                context.Connection.RemoteIpAddress?.ToString(),
                userAgent.Length > 2000 ? userAgent[..2000] : userAgent,
                correlationId),
            cancellationToken);
    }
}
