using TkgPda.Application;

namespace TkgPda.Api;

public sealed record SetProductionEndPriorityHttpRequest(string OperationCode, int? Priority);
public sealed record SetProductionEndAreaPriorityHttpRequest(Guid ProductGroupId, Guid LayoutSlotId, Guid PartId, int? Priority);

public static class OperationalEvidenceEndpoints
{
    public static RouteGroupBuilder MapOperationalEvidence(this RouteGroupBuilder admin)
    {
        admin.MapGet("/transport-requests/{requestId:guid}/evidence", ReadEvidenceAsync);
        admin.MapGet("/production-end-priorities", ListPrioritiesAsync);
        admin.MapGet("/production-end-priorities/{operationCode}/required", RequirePriorityAsync);
        admin.MapPut("/production-end-priorities", SetPriorityAsync);
        admin.MapGet("/production-end-area-priorities", ListAreaPrioritiesAsync);
        admin.MapPut("/production-end-area-priorities", SetAreaPriorityAsync);
        return admin;
    }

    private static async Task<IResult> ListAreaPrioritiesAsync(
        IOperationalEvidenceStore store,
        CancellationToken cancellationToken) =>
        Results.Ok(await store.ListProductionEndAreaPrioritiesAsync(cancellationToken));

    private static async Task<IResult> SetAreaPriorityAsync(
        SetProductionEndAreaPriorityHttpRequest request,
        HttpContext context,
        IOperationalEvidenceStore store,
        CancellationToken cancellationToken)
    {
        await store.SetProductionEndAreaPriorityAsync(
            request.ProductGroupId,
            request.LayoutSlotId,
            request.PartId,
            request.Priority,
            context.User.UserId(),
            cancellationToken);
        return Results.Ok(await store.ListProductionEndAreaPrioritiesAsync(cancellationToken));
    }

    private static async Task<IResult> ReadEvidenceAsync(
        Guid requestId,
        IOperationalEvidenceStore store,
        CancellationToken cancellationToken)
    {
        var evidence = await store.ReadTransportEvidenceAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        return Results.Ok(evidence);
    }

    private static async Task<IResult> ListPrioritiesAsync(
        IOperationalEvidenceStore store,
        CancellationToken cancellationToken) =>
        Results.Ok(await store.ListProductionEndPrioritiesAsync(cancellationToken));

    private static async Task<IResult> RequirePriorityAsync(
        string operationCode,
        IOperationalEvidenceStore store,
        CancellationToken cancellationToken)
    {
        var priority = await store.RequireProductionEndPriorityAsync(operationCode, cancellationToken);
        return Results.Ok(new { operationCode, priority });
    }

    private static async Task<IResult> SetPriorityAsync(
        SetProductionEndPriorityHttpRequest request,
        HttpContext context,
        IOperationalEvidenceStore store,
        CancellationToken cancellationToken)
    {
        await store.SetProductionEndPriorityAsync(
            request.OperationCode,
            request.Priority,
            context.User.UserId(),
            cancellationToken);
        return Results.Ok(await store.ListProductionEndPrioritiesAsync(cancellationToken));
    }
}
