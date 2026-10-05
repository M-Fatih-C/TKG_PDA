using TkgPda.Application;

namespace TkgPda.Api;

public sealed record RemoveStoreUnitHttpRequest(long ExpectedRowVersion);

/// <summary>
/// The finished-goods half of the warehouse tablet. Reads what the robot left,
/// and records what the operator took away.
///
/// There is no route that puts something into the store: a container appears
/// when the transport that carried it completes. A client saying "this arrived"
/// would be a claim about the physical world that nothing verified.
/// </summary>
public static class FinishedGoodsStoreEndpoints
{
    public static IEndpointRouteBuilder MapFinishedGoodsStore(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/warehouse/store")
            .RequireAuthorization(PolicyNames.FinishedGoodsWarehouse);

        // Polled by the tablet every few seconds.
        group.MapGet("/locations", LocationsAsync);
        group.MapPost("/units/{unitId:guid}/remove", RemoveUnitAsync);
        group.MapPost("/locations/{locationId:guid}/clear", ClearLocationAsync);
        return endpoints;
    }

    private static async Task<IResult> LocationsAsync(
        FinishedGoodsStoreService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListLocationsAsync(cancellationToken));

    private static async Task<IResult> RemoveUnitAsync(
        Guid unitId, RemoveStoreUnitHttpRequest request, HttpContext context,
        FinishedGoodsStoreService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.RemoveUnitAsync(
            new RemoveStoreUnitCommand(
                unitId, request.ExpectedRowVersion, context.User.UserId(), DeviceId(context)),
            cancellationToken));

    private static async Task<IResult> ClearLocationAsync(
        Guid locationId, HttpContext context,
        FinishedGoodsStoreService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ClearLocationAsync(
            new ClearStoreLocationCommand(locationId, context.User.UserId(), DeviceId(context)),
            cancellationToken));

    private static Guid DeviceId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord device
            ? device.Id
            : throw new ApiException(
                StatusCodes.Status401Unauthorized, "DEVICE_CONTEXT_REQUIRED", "Yetkilendirilmiş cihaz gereklidir.");
}
