using TkgPda.Application;

namespace TkgPda.Api;

public sealed record SetProfileSlotMappingHttpRequest(Guid SlotId, Guid NavigationProfileId, Guid RdsWorkSiteId);

public sealed record CommissioningActionHttpRequest(string MachineCode, string VariantCode, bool Apply);

public static class CommissioningActivationEndpoints
{
    public static RouteGroupBuilder MapCommissioningActivation(this RouteGroupBuilder admin)
    {
        admin.MapGet("/rds/commissioning/report", (IRdsIntegrationStore store, CancellationToken ct) =>
            store.ListVariantCommissioningAsync(ct));
        admin.MapGet("/rds/commissioning/worksite-diff", (
            string machineCode,
            string variantCode,
            IRdsIntegrationStore store,
            CancellationToken ct) => store.ReadWorkSiteNameDiffAsync(machineCode, variantCode, ct));
        admin.MapPut("/rds/commissioning/profile-mappings", SetProfileMappingAsync);
        admin.MapPost("/rds/commissioning/verify", (CommissioningActionHttpRequest request, HttpContext context, IRdsIntegrationStore store, CancellationToken ct) =>
            store.VerifyVariantCommissioningAsync(request.MachineCode, request.VariantCode, request.Apply, MutationContext(context), ct));
        admin.MapPost("/rds/commissioning/promote", (CommissioningActionHttpRequest request, HttpContext context, IRdsIntegrationStore store, CancellationToken ct) =>
            store.PromoteVariantCommissioningAsync(request.MachineCode, request.VariantCode, request.Apply, MutationContext(context), ct));
        admin.MapPost("/rds/commissioning/confirm-geometry", (CommissioningActionHttpRequest request, HttpContext context, IRdsIntegrationStore store, CancellationToken ct) =>
            store.ConfirmVariantGeometryAsync(request.MachineCode, request.VariantCode, request.Apply, MutationContext(context), ct));
        return admin;
    }

    private static Task<RdsProfileSlotMappingRecord> SetProfileMappingAsync(
        SetProfileSlotMappingHttpRequest request,
        HttpContext context,
        IRdsIntegrationStore store,
        CancellationToken cancellationToken)
    {
        var mutation = MutationContext(context);
        return store.SetProfileSlotMappingAsync(
            request.SlotId,
            request.NavigationProfileId,
            request.RdsWorkSiteId,
            mutation,
            cancellationToken);
    }

    private static AdminMutationContext MutationContext(HttpContext context)
    {
        var correlationId = context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value) && value is Guid id
            ? id
            : Guid.NewGuid();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return new AdminMutationContext(
            context.User.UserId(),
            correlationId,
            context.Connection.RemoteIpAddress?.ToString(),
            userAgent.Length > 2000 ? userAgent[..2000] : userAgent);
    }
}
