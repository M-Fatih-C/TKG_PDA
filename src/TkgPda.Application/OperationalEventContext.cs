namespace TkgPda.Application;

/// <summary>
/// WP-06: Operational event context carried across workflow calls
/// to associate timeline events with their triggering user, field terminal device,
/// and API/RDS correlation identity without polluting the core business request model.
/// </summary>
public sealed record OperationalEventContext(
    Guid? ActorUserId = null,
    Guid? DeviceId = null,
    Guid? CorrelationId = null)
{
    public static OperationalEventContext Empty => new();

    public static OperationalEventContext ForUser(Guid? actorUserId, Guid? deviceId, Guid? correlationId = null) =>
        new(actorUserId, deviceId, correlationId);

    public static OperationalEventContext System(Guid? correlationId = null) =>
        new(null, null, correlationId);
}
