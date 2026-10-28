using TkgPda.Domain;

namespace TkgPda.Application;

public sealed record PartDemandCommand(
    string IdempotencyKey,
    Guid ActorUserId,
    Guid MachineId,
    Guid PartId,
    OperationalEventContext? EventContext);

public sealed record PartDemandBatchCommand(
    string IdempotencyKey,
    Guid ActorUserId,
    Guid MachineId,
    IReadOnlyList<Guid> PartIds,
    OperationalEventContext? EventContext);

public sealed record PartDemandResult(
    TransportRequestSnapshot Request,
    TransportRequestSnapshot? Predecessor,
    bool WasIdempotentReplay);

public sealed record PartDemandRejection(Guid PartId, string Code, string Message);

public sealed record PartDemandBatchResult(
    IReadOnlyList<PartDemandResult> Items,
    IReadOnlyList<PartDemandRejection> RejectedItems,
    bool WasIdempotentReplay);

public interface IPartDemandStore
{
    Task<PartDemandResult> CreateAsync(PartDemandCommand command, CancellationToken cancellationToken = default);

    Task<PartDemandBatchResult> CreateBatchAsync(PartDemandBatchCommand command, CancellationToken cancellationToken = default);
}

public sealed class PartDemandService(IPartDemandStore store)
{
    public Task<PartDemandResult> CreateAsync(PartDemandCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command.IdempotencyKey, command.ActorUserId, command.MachineId, [command.PartId]);
        return store.CreateAsync(command, cancellationToken);
    }

    public Task<PartDemandBatchResult> CreateBatchAsync(PartDemandBatchCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command.IdempotencyKey, command.ActorUserId, command.MachineId, command.PartIds);
        if (command.PartIds.Distinct().Count() != command.PartIds.Count)
        {
            throw new WorkflowRuleException("PART_DEMAND_DUPLICATE", "Aynı alt parça toplu talepte bir kez seçilebilir.");
        }

        return store.CreateBatchAsync(command, cancellationToken);
    }

    private static void Validate(string key, Guid actor, Guid machine, IReadOnlyList<Guid> parts)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 160)
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REQUIRED", "A part-demand idempotency key is required.");
        }

        if (actor == Guid.Empty || machine == Guid.Empty || parts.Count == 0 || parts.Any(part => part == Guid.Empty))
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Actor, machine and alt-part identifiers are required.");
        }
    }
}
