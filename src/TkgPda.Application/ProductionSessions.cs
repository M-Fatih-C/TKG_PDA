using TkgPda.Domain;

namespace TkgPda.Application;

public sealed record StartProductionSession(
    Guid MachineId, Guid VariantId, Guid ProductGroupId, Guid MainPartId,
    Guid ActorUserId, Guid DeviceId, string IdempotencyKey);

public sealed record EndProductionSession(
    Guid MachineId, Guid SessionId, Guid ActorUserId, Guid DeviceId, string IdempotencyKey);

public sealed record ProductionSessionRecord(
    Guid Id, Guid MachineId, Guid VariantId, Guid ProductGroupId, Guid MainPartId,
    DateTimeOffset StartedAt, Guid StartedBy, Guid StartedDeviceId,
    DateTimeOffset? EndedAt, Guid? EndedBy, Guid? EndedDeviceId)
{
    /// <summary>
    /// Server-owned. ACTIVE and ENDING both leave ended_at empty. ENDED is
    /// written only after every production-end removal has completed, or
    /// immediately when the authoritative snapshot has nothing to remove.
    /// </summary>
    public string Status { get; init; } = EndedAt is null ? "ACTIVE" : "ENDED";
}

public sealed record ProductionSessionResult(ProductionSessionRecord Session, bool WasIdempotentReplay);

/// <summary>
/// Implementations serialize all mutations with the transport workflow's machine
/// runtime lock. Start/end do not dispatch, cancel or complete transport requests.
/// Audit and session writes must commit atomically.
/// </summary>
public interface IProductionSessionStore
{
    Task<ProductionSessionRecord?> FindActiveAsync(Guid machineId, CancellationToken cancellationToken = default);
    Task<ProductionSessionResult> StartAsync(StartProductionSession command, CancellationToken cancellationToken = default);
    Task<ProductionSessionResult> EndAsync(EndProductionSession command, CancellationToken cancellationToken = default);
}

public sealed class ProductionSessionService(IProductionSessionStore store)
{
    public Task<ProductionSessionRecord?> FindActiveAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        RequireIds(machineId);
        return store.FindActiveAsync(machineId, cancellationToken);
    }

    public Task<ProductionSessionResult> StartAsync(StartProductionSession command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.MachineId, command.VariantId, command.ProductGroupId, command.MainPartId, command.ActorUserId, command.DeviceId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REQUIRED", "Üretime başlamak için tekil istek anahtarı gereklidir.");
        if (command.IdempotencyKey.Length > 200)
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_TOO_LONG", "İstek anahtarı 200 karakteri geçemez.");
        return store.StartAsync(command, cancellationToken);
    }

    public Task<ProductionSessionResult> EndAsync(EndProductionSession command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.MachineId, command.SessionId, command.ActorUserId, command.DeviceId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REQUIRED", "Üretimi bitirmek için tekil istek anahtarı gereklidir.");
        if (command.IdempotencyKey.Length > 200)
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_TOO_LONG", "İstek anahtarı 200 karakteri geçemez.");
        return store.EndAsync(command, cancellationToken);
    }

    private static void RequireIds(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
            throw new WorkflowRuleException("ID_REQUIRED", "Üretim, makine, ürün, kullanıcı ve cihaz kimlikleri geçerli olmalıdır.");
    }
}
