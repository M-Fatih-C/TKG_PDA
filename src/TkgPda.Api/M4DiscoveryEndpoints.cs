using TkgPda.Application;

namespace TkgPda.Api;

public static class M4DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapTkgPdaM4DiscoveryApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/fleet")
            .RequireAuthorization(PolicyNames.Administrator);

        group.MapGet("/status", StatusAsync);
        group.MapPost("/discovery/sync", CaptureAsync);
        group.MapGet("/discovery/latest", LatestAsync);
        group.MapGet("/discovery/captures/{id:guid}", GetAsync);
        group.MapGet("/scenes", ScenesAsync);
        group.MapGet("/robots", RobotsAsync);
        group.MapGet("/endpoints", EndpointsAsync);

        return endpoints;
    }

    private static async Task<IResult> StatusAsync(
        M4Options options,
        M4DiscoveryService service,
        CancellationToken cancellationToken)
    {
        var latest = await service.GetLatestAsync(cancellationToken);
        return Results.Ok(new
        {
            options.Enabled,
            options.ReadEnabled,
            options.DispatchEnabled,
            BaseAddress = options.SafeBaseAddress,
            LatestDiscovery = latest
        });
    }

    private static Task<M4DiscoverySummary> CaptureAsync(
        M4DiscoveryCommand command,
        HttpContext context,
        M4DiscoveryService service,
        CancellationToken cancellationToken) =>
        service.CaptureAsync(command, CorrelationId(context), cancellationToken);

    private static async Task<IResult> LatestAsync(
        M4DiscoveryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetLatestAsync(cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Ok(result);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        M4DiscoveryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Ok(result);
    }

    private static async Task<IResult> ScenesAsync(
        M4DiscoveryService service,
        CancellationToken cancellationToken) =>
        await LatestProjectionAsync(service, snapshot => snapshot.Scenes, cancellationToken);

    private static async Task<IResult> RobotsAsync(
        M4DiscoveryService service,
        CancellationToken cancellationToken) =>
        await LatestProjectionAsync(service, snapshot => snapshot.Robots, cancellationToken);

    private static async Task<IResult> EndpointsAsync(
        M4DiscoveryService service,
        CancellationToken cancellationToken) =>
        await LatestProjectionAsync(service, snapshot => snapshot.Endpoints, cancellationToken);

    private static async Task<IResult> LatestProjectionAsync<T>(
        M4DiscoveryService service,
        Func<M4DiscoverySummary, IReadOnlyList<T>> projection,
        CancellationToken cancellationToken)
    {
        var latest = await service.GetLatestAsync(cancellationToken);
        return latest is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                latest.Id,
                latest.Status,
                latest.CapturedAt,
                latest.FreshUntil,
                latest.Stale,
                Items = projection(latest)
            });
    }

    private static Guid CorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid correlationId
            ? correlationId
            : Guid.NewGuid();
}
