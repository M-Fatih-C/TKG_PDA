namespace TkgPda.Domain;

/// <summary>
/// WP-06: Canonical transport request lifecycle event types.
/// Defines the canonical boundaries for transport request audit,
/// warehouse response, robot execution duration (WP-13) and error reporting (WP-14).
/// </summary>
public static class TransportEventTypes
{
    public const string RequestCreated = "REQUEST_CREATED";
    public const string WarehousePreparationStarted = "WAREHOUSE_PREPARATION_STARTED";
    public const string WarehousePreparationReady = "WAREHOUSE_PREPARATION_READY";
    public const string WarehousePreparationWithdrawn = "WAREHOUSE_PREPARATION_WITHDRAWN";
    public const string PartDemandCurrentPalletReportedEmpty = "PART_DEMAND_CURRENT_PALLET_REPORTED_EMPTY";
    public const string MachineSlotReservationActivated = "MACHINE_SLOT_RESERVATION_ACTIVATED";
    public const string RobotTaskDispatched = "ROBOT_TASK_DISPATCHED";
    public const string RobotTaskStarted = "ROBOT_TASK_STARTED";
    public const string RobotTaskCompleted = "ROBOT_TASK_COMPLETED";
    public const string RequestCompleted = "REQUEST_COMPLETED";
    public const string RequestCancelled = "REQUEST_CANCELLED";
    public const string RequestFailed = "REQUEST_FAILED";
}
