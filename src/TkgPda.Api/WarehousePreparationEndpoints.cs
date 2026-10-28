using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Api;

/// <summary>
/// Load is EMPTY or FULL. Finished-goods empty-container preparation binds the
/// exact BRING_EMPTY request; semi-finished filled material binds BRING_FULL.
/// </summary>
public sealed record PlacePalletHttpRequest(
    string Load, Guid? ContainerTypeId, Guid? PartId, Guid? TransportRequestId);

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
        group.MapGet("/areas", (WarehousePreparationService service, CancellationToken token) =>
            AreasAsync(WarehousePreparationScopes.SemiFinished, service, token));
        group.MapGet("/demands", (WarehousePreparationService service, CancellationToken token) =>
            DemandsAsync(WarehousePreparationScopes.SemiFinished, service, token));
        group.MapPost("/areas/{areaId:guid}/pallet", (
            Guid areaId, PlacePalletHttpRequest request, HttpContext context,
            WarehousePreparationService service, CancellationToken token) =>
            PlaceAsync(WarehousePreparationScopes.SemiFinished, areaId, request, context, service, token));
        group.MapPost("/pallets/{palletId:guid}/ready", ReadyAsync);
        group.MapPost("/pallets/{palletId:guid}/release", ReleaseAsync);

        var finished = endpoints.MapGroup("/api/v1/warehouse/finished-goods-preparation")
            .RequireAuthorization(PolicyNames.FinishedGoodsWarehouse);
        finished.MapGet("/areas", (WarehousePreparationService service, CancellationToken token) =>
            AreasAsync(WarehousePreparationScopes.FinishedGoods, service, token));
        finished.MapGet("/demands", (WarehousePreparationService service, CancellationToken token) =>
            DemandsAsync(WarehousePreparationScopes.FinishedGoods, service, token));
        finished.MapPost("/areas/{areaId:guid}/pallet", (
            Guid areaId, PlacePalletHttpRequest request, HttpContext context,
            WarehousePreparationService service, CancellationToken token) =>
            PlaceAsync(WarehousePreparationScopes.FinishedGoods, areaId, request, context, service, token));
        finished.MapPost("/pallets/{palletId:guid}/ready", ReadyAsync);
        finished.MapPost("/pallets/{palletId:guid}/release", ReleaseAsync);
        return endpoints;
    }

    private static async Task<IResult> AreasAsync(
        string scope, WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAreasAsync(scope, cancellationToken));

    private static async Task<IResult> DemandsAsync(
        string scope, WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListDemandsAsync(scope, cancellationToken));

    private static async Task<IResult> PlaceAsync(
        string scope, Guid areaId, PlacePalletHttpRequest request, HttpContext context,
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
                context.Request.Headers[ApiHeaders.IdempotencyKey].ToString().Trim(),
                CorrelationId(context),
                scope),
            cancellationToken);
        context.Response.Headers[ApiHeaders.IdempotencyReplayed] = result.WasIdempotentReplay ? "true" : "false";
        return Results.Json(result.Pallet,
            statusCode: result.WasIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReadyAsync(
        Guid palletId, PalletVersionHttpRequest request, HttpContext context,
        WarehousePreparationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.MarkReadyAsync(
            new MarkPalletReadyCommand(palletId, request.ExpectedRowVersion, context.User.UserId(), DeviceId(context), CorrelationId(context)),
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

    private static Guid CorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid id ? id : Guid.NewGuid();
}
