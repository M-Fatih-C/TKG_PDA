namespace TkgPda.Application;

public sealed record OperationalReportPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record TaskReportQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? MachineId,
    string? EmployeeNo,
    Guid? DeviceId,
    string? Status,
    string? Operation,
    string? Part,
    long? RequestNo,
    string? AgvId,
    bool ErrorsOnly,
    bool CancelledOnly,
    int Page,
    int PageSize);

public sealed record TaskReportRow(
    Guid RequestId,
    long RequestNo,
    DateTimeOffset CreatedAt,
    Guid? RequestedByUserId,
    string? RequestedByName,
    string? EmployeeNo,
    string? Username,
    Guid? DeviceId,
    string? DeviceCode,
    string? DeviceName,
    Guid MachineId,
    string MachineCode,
    string MachineName,
    string? VariantCode,
    string? NavigationCode,
    string OperationCode,
    string OperationLabel,
    string? PartCode,
    string? PartDescription,
    string? ContainerCode,
    string? SourceSlotCode,
    string? DestinationSlotCode,
    string Status,
    string StatusLabel,
    bool IsSuccess,
    string? FailureCode,
    string? FailureMessage,
    string? FailureSummary,
    string? CancellationKind,
    string? CancellationLabel,
    DateTimeOffset? CancelledAt,
    Guid? CancelledByUserId,
    string? CancelledByName,
    string? CancelledByEmployeeNo,
    Guid? CancelledByDeviceId,
    string? CancelledByDeviceCode,
    string? RdsTaskRecordId,
    string? AgvId,
    int? RdsStatus,
    string? EndedReason,
    DateTimeOffset? RequestCreatedAt,
    DateTimeOffset? WarehouseStartedAt,
    DateTimeOffset? WarehouseReadyAt,
    DateTimeOffset? RdsSentAt,
    DateTimeOffset? RobotStartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt,
    long? TotalSeconds,
    long? WarehouseResponseSeconds,
    long? RobotExecutionSeconds,
    string? PreparationOperatorName,
    string? PreparationEmployeeNo,
    Guid? PreparationDeviceId,
    string? PreparationDeviceCode,
    string? PreparationArea,
    Guid CorrelationId);

public sealed record TaskTimelineEntry(
    DateTimeOffset At,
    string Title,
    string? ActorLine);

public sealed record TaskReportDetail(TaskReportRow Task, IReadOnlyList<TaskTimelineEntry> Timeline);

public sealed record SessionReportQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    string? EmployeeNo,
    Guid? DeviceId,
    Guid? MachineId,
    string? Activity,
    string? EndReason,
    int Page,
    int PageSize);

public sealed record SessionReportRow(
    Guid SessionId,
    Guid UserId,
    string FullName,
    string? EmployeeNo,
    string Username,
    IReadOnlyList<string> Roles,
    DateTimeOffset LoginAt,
    DateTimeOffset? EndedAt,
    long? DurationSeconds,
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    string DeviceType,
    string MachineState,
    Guid? MachineId,
    string? MachineCode,
    string StatusLabel,
    string? EndReason,
    string? EndReasonLabel);

public sealed record BlockedLoginRow(
    DateTimeOffset At,
    Guid? UserId,
    string? FullName,
    string? EmployeeNo,
    string? Username,
    string? AttemptedDeviceCode,
    string? ExistingDeviceCode,
    string Message);

public sealed record SessionReportPage(
    IReadOnlyList<SessionReportRow> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<BlockedLoginRow> BlockedAttempts);

public sealed record ReportCount(string Key, string Label, int Count);

public sealed record OperationsSummary(
    int Total,
    int Completed,
    int Failed,
    int Cancelled,
    int Active,
    double? SuccessRate,
    double? AverageTotalSeconds,
    double? AverageWarehouseSeconds,
    double? AverageRobotSeconds,
    IReadOnlyList<ReportCount> ByMachine,
    IReadOnlyList<ReportCount> ByOperation,
    IReadOnlyList<ReportCount> ByRequester,
    IReadOnlyList<ReportCount> TopFailures);

public interface IOperationalReportStore
{
    Task<OperationalReportPage<TaskReportRow>> ListTasksAsync(TaskReportQuery query, CancellationToken cancellationToken = default);
    Task<TaskReportDetail?> ReadTaskAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<SessionReportPage> ListSessionsAsync(SessionReportQuery query, CancellationToken cancellationToken = default);
    Task<OperationsSummary> ReadSummaryAsync(DateTimeOffset from, DateTimeOffset to, Guid? machineId, CancellationToken cancellationToken = default);
}

public static class OperationalReportText
{
    public static string OperationLabel(string code) => code switch
    {
        "BRING_FULL" => "Malzeme Getir",
        "BRING_EMPTY" => "Boş Kasa Getir",
        "REMOVE_FULL" => "Dolu Kasa Götür",
        "REMOVE_EMPTY" => "Boş Kasa Götür",
        _ => code
    };

    public static string StatusLabel(string status) => status switch
    {
        "CREATED" => "Talep oluşturuldu",
        "VALIDATING" => "Doğrulanıyor",
        "QUEUED" => "Sırada",
        "SENT_TO_RDS" => "RDS'ye gönderildi",
        "RUNNING" => "Robot çalışıyor",
        "COMPLETED" => "Başarıyla tamamlandı",
        "FAILED" => "Hata ile sonlandı",
        "CANCELLED" => "İptal edildi",
        _ => status
    };

    public static string FailureSummary(string operationCode) => operationCode switch
    {
        "BRING_FULL" or "BRING_EMPTY" => "Malzeme alma görevi başarısız oldu.",
        "REMOVE_FULL" => "Dolu kasa götürme görevi başarısız oldu.",
        "REMOVE_EMPTY" => "Boş kasa götürme görevi başarısız oldu.",
        _ => "Görev başarısız oldu."
    };

    public static string? CancellationLabel(string? kind) => kind switch
    {
        "OPERATOR" => "Operatör iptali",
        "SYSTEM" => "Sistem iptali",
        "DEPENDENCY" => "Bağımlı talep iptali",
        "PRODUCTION_END" => "Üretim bitişi kuyruk iptali",
        _ => null
    };

    public static string SessionEndLabel(string? reason, bool active) => active ? "Aktif" : reason switch
    {
        "USER_LOGOUT" => "Kullanıcı çıkışı",
        "LEASE_EXPIRED" => "Süre doldu",
        "TOKEN_EXPIRED" => "Token süresi doldu",
        "REPLACED_SAME_DEVICE_LOGIN" => "Aynı cihazda yeni giriş ile değiştirildi",
        "LOGIN_FINALIZATION_FAILED" => "Giriş tamamlanamadı",
        null or "" => "Sonlanma nedeni kayıtlı değil",
        _ => reason!
    };

    public static string EventTitle(string eventType) => eventType switch
    {
        "REQUEST_CREATED" => "Talep oluşturuldu",
        "WAREHOUSE_PREPARATION_STARTED" => "Depo operatörü hazırlamaya başladı",
        "WAREHOUSE_PREPARATION_READY" => "Malzeme hazır",
        "ROBOT_TASK_DISPATCHED" => "RDS'ye gönderildi",
        "ROBOT_TASK_STARTED" => "Robot göreve başladı",
        "ROBOT_TASK_COMPLETED" => "Robot görevi bitti",
        "REQUEST_COMPLETED" => "Başarıyla tamamlandı",
        "REQUEST_FAILED" => "Hata ile sonlandı",
        "REQUEST_CANCELLED" => "İptal edildi",
        _ => eventType
    };

    public static long? Seconds(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null || end < start ? null : (long)Math.Floor((end.Value - start.Value).TotalSeconds);
}
