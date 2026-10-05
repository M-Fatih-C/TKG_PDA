using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Api;

/// <summary>
/// Load is EMPTY or FULL. TransportRequestId is sent only for filled material;
/// staged empty stock carries none, and is matched to a demand at the ready step.
/// </summary>
public sealed record PlacePalletHttpRequest(
    string Load, Guid ContainerTypeId, Guid? PartId, Guid? TransportRequestId);

public sealed record PalletVersionHttpRequest(long ExpectedRowVersion);

public sealed record ReleaseAreaHttpRequest(long ExpectedRowVersion, string Reason);

/// <summary>
/// The warehouse tablet's surface. Every route is device-bound and restricted to
/// the warehouse preparation roles; nothing here reaches RDS.
/// </summary>
public static class WarehousePreparationEndpoints
{
    public static IEndpointRouteBuilder MapWarehousePreparation(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/warehouse/preparation")
            .RequireAuthorization(PolicyNames.SemiFinishedWarehouse);

        // Both reads are polled by the tablet every few seconds.
        group.MapGet("/areas", AreasAsync);
        group.MapGet("/demands", DemandsAsync);
        group.MapPost("/areas/{areaId:guid}/pallet", PlaceAsync);
        group.MapPost("/pallets/{palletId:guid}/ready", ReadyAsync);
        group.MapPost("/pallets/{palletId:guid}/release", ReleaseAsync);
        return endpoints;
    }

    private static async Task<IResult> AreasAsync(
        WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAreasAsync(cancellationToken));

    private static async Task<IResult> DemandsAsync(
        WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListDemandsAsync(cancellationToken));

    private static async Task<IResult> PlaceAsync(
        Guid areaId, PlacePalletHttpRequest request, HttpContext context,
        WarehousePreparationService service, CancellationToken cancellationToken)
    {
        var result = await service.PlacePalletAsync(
            new PlacePalletCommand(
                areaId,
                request.ContainerTypeId,
                ParseLoad(request.Load),
                request.PartId,
                request.TransportRequestId,
                context.User.UserId(),
                DeviceId(context),
                context.Request.Headers[ApiHeaders.IdempotencyKey].ToString().Trim()),
            cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        return Results.Json(result.Pallet,
            statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReadyAsync(
        Guid palletId, PalletVersionHttpRequest request, HttpContext context,
        WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.MarkReadyAsync(
            new MarkPalletReadyCommand(palletId, request.ExpectedRowVersion, context.User.UserId(), DeviceId(context)),
            cancellationToken));

    private static async Task<IResult> ReleaseAsync(
        Guid palletId, ReleaseAreaHttpRequest request, HttpContext context,
        WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ReleaseAsync(
            new ReleaseAreaCommand(
                palletId, request.ExpectedRowVersion, request.Reason?.Trim().ToUpperInvariant() ?? string.Empty,
                context.User.UserId(), DeviceId(context)),
            cancellationToken));

    private static LoadState ParseLoad(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "EMPTY" => LoadState.Empty,
        "FULL" => LoadState.Full,
        // Never defaulted to a guess: an unreadable load would decide whether a
        // container is recorded as holding material.
        _ => throw new ApiException(
            StatusCodes.Status400BadRequest, "INVALID_LOAD_STATE", "Load must be EMPTY or FULL.")
    };

    private static Guid DeviceId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord device
            ? device.Id
            : throw new ApiException(
                StatusCodes.Status401Unauthorized, "DEVICE_CONTEXT_REQUIRED", "Yetkilendirilmiş cihaz gereklidir.");
}
