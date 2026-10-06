using TkgPda.Application;

namespace TkgPda.Api;

public sealed record StartProductionSessionHttpRequest(Guid VariantId, Guid ProductGroupId, Guid MainPartId);

public static class ProductionSessionEndpoints
{
    public static IEndpointRouteBuilder MapProductionSessions(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/machines/{machineId:guid}/production-sessions")
            .RequireAuthorization(PolicyNames.ProductionOperator);
        group.MapGet("/active", ActiveAsync);
        group.MapPost("", StartAsync);
        group.MapPost("/{sessionId:guid}/end", EndAsync);
        return endpoints;
    }

    private static async Task<IResult> ActiveAsync(Guid machineId, HttpContext context,
        MachineAccessService access, ProductionSessionService service, CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.View, cancellationToken);
        var session = await service.FindActiveAsync(machineId, cancellationToken);
        return session is null ? Results.NoContent() : Results.Ok(session);
    }

    private static async Task<IResult> StartAsync(Guid machineId, StartProductionSessionHttpRequest request,
        HttpContext context, MachineAccessService access, ProductionSessionService service, CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.Operate, cancellationToken);
        var result = await service.StartAsync(new(machineId, request.VariantId, request.ProductGroupId, request.MainPartId,
            context.User.UserId(), DeviceId(context), context.Request.Headers[ApiHeaders.IdempotencyKey].ToString().Trim()), cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        return Results.Json(result, statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> EndAsync(Guid machineId, Guid sessionId, HttpContext context,
        MachineAccessService access, ProductionSessionService service, CancellationToken cancellationToken)
    {
        _ = await access.EnsureAsync(context, machineId, MachineAccessKind.Operate, cancellationToken);
        var result = await service.EndAsync(new(machineId, sessionId, context.User.UserId(), DeviceId(context),
            context.Request.Headers[ApiHeaders.IdempotencyKey].ToString().Trim()), cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        return Results.Ok(result);
    }

    private static Guid DeviceId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord device
            ? device.Id
            : throw new ApiException(StatusCodes.Status401Unauthorized, "DEVICE_CONTEXT_REQUIRED", "Yetkilendirilmiş cihaz gereklidir.");
}
