using System.Globalization;
using System.Text;
using TkgPda.Application;

namespace TkgPda.Api;

public static class OperationalReportEndpoints
{
    public static RouteGroupBuilder MapOperationalReports(this RouteGroupBuilder admin)
    {
        var reports = admin.MapGroup("/reports");
        reports.MapGet("/tasks", ListTasksAsync);
        reports.MapGet("/tasks/export.csv", ExportTasksAsync);
        reports.MapGet("/tasks/export.xlsx", ExportTasksXlsxAsync);
        reports.MapGet("/tasks/{requestId:guid}", ReadTaskAsync);
        reports.MapGet("/sessions", ListSessionsAsync);
        reports.MapGet("/sessions/export.csv", ExportSessionsAsync);
        reports.MapGet("/sessions/export.xlsx", ExportSessionsXlsxAsync);
        reports.MapGet("/summary", ReadSummaryAsync);
        return admin;
    }

    private static async Task<IResult> ListTasksAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? machineId,
        string? employeeNo,
        Guid? deviceId,
        string? status,
        string? operation,
        string? part,
        long? requestNo,
        string? agvId,
        bool? errorsOnly,
        bool? cancelledOnly,
        int? page,
        int? pageSize,
        IOperationalReportStore store,
        CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        var result = await store.ListTasksAsync(new TaskReportQuery(
            window.From, window.To, machineId, employeeNo, deviceId, status, operation, part, requestNo, agvId,
            errorsOnly == true, cancelledOnly == true, page ?? 1, pageSize ?? 50), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ReadTaskAsync(
        Guid requestId,
        IOperationalReportStore store,
        CancellationToken cancellationToken)
    {
        var detail = await store.ReadTaskAsync(requestId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "The transport request does not exist.");
        return Results.Ok(detail);
    }

    private static async Task<IResult> ExportTasksAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? machineId,
        string? employeeNo,
        Guid? deviceId,
        string? status,
        string? operation,
        string? part,
        long? requestNo,
        string? agvId,
        bool? errorsOnly,
        bool? cancelledOnly,
        IOperationalReportStore store,
        CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        var rows = await AllTasksAsync(store, new TaskReportQuery(
            window.From, window.To, machineId, employeeNo, deviceId, status, operation, part, requestNo, agvId,
            errorsOnly == true, cancelledOnly == true, 1, 500), cancellationToken);
        var csv = ReportCsv.Build(
            [
                "Talep No", "Tarih", "Saat", "Sicil", "Kullanıcı", "Bilgisayar", "Makine", "Varyant", "İşlem", "Parça",
                "Durum", "Depo Operatörü", "Hazırlık Alanı", "Robot", "Toplam Süre", "Depo Süresi", "Robot Süresi",
                "Hata Kodu", "Hata Açıklaması"
            ],
            rows.Select(row => new string?[]
            {
                row.RequestNo.ToString(CultureInfo.InvariantCulture),
                ReportCsv.Date(row.CreatedAt),
                ReportCsv.Time(row.CreatedAt),
                row.EmployeeNo,
                row.RequestedByName,
                row.DeviceCode ?? row.DeviceName,
                row.MachineCode,
                row.VariantCode,
                row.OperationLabel,
                row.PartCode,
                row.StatusLabel,
                row.PreparationOperatorName,
                row.PreparationArea,
                row.AgvId,
                ReportCsv.Duration(row.TotalSeconds),
                ReportCsv.Duration(row.WarehouseResponseSeconds),
                ReportCsv.Duration(row.RobotExecutionSeconds),
                row.FailureCode,
                row.FailureMessage
            }));
        return ReportCsv.File(csv, $"TKG_Gorev_Raporu_{ReportCsv.FileDate(window.From)}.csv");
    }

    private static async Task<IResult> ExportTasksXlsxAsync(
        DateTimeOffset? from, DateTimeOffset? to, Guid? machineId, string? employeeNo,
        Guid? deviceId, string? status, string? operation, string? part, long? requestNo,
        string? agvId, bool? errorsOnly, bool? cancelledOnly,
        IOperationalReportStore store, CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        var rows = await AllTasksAsync(store, new TaskReportQuery(
            window.From, window.To, machineId, employeeNo, deviceId, status, operation, part, requestNo, agvId,
            errorsOnly == true, cancelledOnly == true, 1, 500), cancellationToken);
        var sheets = new OperationalReportXlsx.Sheet[]
        {
            TaskSummarySheet(window, rows),
            new(
                "Görevler",
                [
                    "Talep No", "Talep Zamanı", "Makine", "Varyant", "İşlem", "Parça", "Parça Açıklaması",
                    "Durum", "İsteyen Operatör", "Sicil", "Depo Operatörü", "Hazırlık Alanı", "Robot",
                    "Toplam Süre", "Depo Hazırlık Süresi", "Robot Çalışma Süresi", "Sonuç Açıklaması"
                ],
                rows.Select(row => (IReadOnlyList<OperationalReportXlsx.Cell>)[
                    OperationalReportXlsx.Number(row.RequestNo), OperationalReportXlsx.Date(row.RequestCreatedAt ?? row.CreatedAt),
                    OperationalReportXlsx.Text(row.MachineCode), OperationalReportXlsx.Text(row.VariantCode),
                    OperationalReportXlsx.Text(row.OperationLabel), OperationalReportXlsx.Text(row.PartCode),
                    OperationalReportXlsx.Text(row.PartDescription), OperationalReportXlsx.Text(row.StatusLabel),
                    OperationalReportXlsx.Text(row.RequestedByName), OperationalReportXlsx.Text(row.EmployeeNo),
                    OperationalReportXlsx.Text(row.PreparationOperatorName), OperationalReportXlsx.Text(row.PreparationArea),
                    OperationalReportXlsx.Text(row.AgvId), OperationalReportXlsx.Duration(row.TotalSeconds),
                    OperationalReportXlsx.Duration(row.WarehouseResponseSeconds), OperationalReportXlsx.Duration(row.RobotExecutionSeconds),
                    OperationalReportXlsx.Text(TaskResult(row))
                ])),
            new(
                "Zaman Ayrıntıları",
                [
                    "Talep No", "Talep Oluşturma", "Depo Hazırlık Başlangıcı", "Depo Hazır",
                    "Robot Sistemine Gönderim", "Robot Başlangıç", "Tamamlanma", "Hata Zamanı", "İptal Zamanı",
                    "Toplam Süre", "Depo Hazırlık Süresi", "Robot Çalışma Süresi"
                ],
                rows.Select(row => (IReadOnlyList<OperationalReportXlsx.Cell>)[
                    OperationalReportXlsx.Number(row.RequestNo), OperationalReportXlsx.Date(row.RequestCreatedAt ?? row.CreatedAt),
                    OperationalReportXlsx.Date(row.WarehouseStartedAt), OperationalReportXlsx.Date(row.WarehouseReadyAt),
                    OperationalReportXlsx.Date(row.RdsSentAt), OperationalReportXlsx.Date(row.RobotStartedAt),
                    OperationalReportXlsx.Date(row.CompletedAt), OperationalReportXlsx.Date(row.FailedAt),
                    OperationalReportXlsx.Date(row.CancelledAt), OperationalReportXlsx.Duration(row.TotalSeconds),
                    OperationalReportXlsx.Duration(row.WarehouseResponseSeconds), OperationalReportXlsx.Duration(row.RobotExecutionSeconds)
                ])),
            new(
                "Teknik Ayrıntılar",
                [
                    "Talep No", "Kullanıcı Adı", "Bilgisayar", "Palet/Kasa Tipi", "Kaynak", "Hedef",
                    "Depo Sicil", "Depo Bilgisayarı", "Navigasyon", "RDS Görev No", "RDS Durumu",
                    "Robot Sonuç Nedeni", "Hata Kodu", "Hata Açıklaması", "İptal Türü", "Korelasyon No"
                ],
                rows.Select(row => (IReadOnlyList<OperationalReportXlsx.Cell>)[
                    OperationalReportXlsx.Number(row.RequestNo), OperationalReportXlsx.Text(row.Username),
                    OperationalReportXlsx.Text(row.DeviceCode ?? row.DeviceName), OperationalReportXlsx.Text(row.ContainerCode),
                    OperationalReportXlsx.Text(row.SourceSlotCode), OperationalReportXlsx.Text(row.DestinationSlotCode),
                    OperationalReportXlsx.Text(row.PreparationEmployeeNo), OperationalReportXlsx.Text(row.PreparationDeviceCode),
                    OperationalReportXlsx.Text(row.NavigationCode), OperationalReportXlsx.Text(row.RdsTaskRecordId),
                    OperationalReportXlsx.Text(row.RdsStatus), OperationalReportXlsx.Text(row.EndedReason),
                    OperationalReportXlsx.Text(row.FailureCode), OperationalReportXlsx.Text(row.FailureMessage),
                    OperationalReportXlsx.Text(row.CancellationLabel), OperationalReportXlsx.Text(row.CorrelationId)
                ]))
        };

        return OperationalReportXlsx.File(sheets, $"TKG_Yonetim_Raporu_{ReportCsv.FileDate(window.From)}.xlsx");
    }

    private static OperationalReportXlsx.Sheet TaskSummarySheet(
        (DateTimeOffset From, DateTimeOffset To) window,
        IReadOnlyList<TaskReportRow> rows)
    {
        var completed = rows.Count(row => row.Status == "COMPLETED");
        var failed = rows.Count(row => row.Status == "FAILED");
        var cancelled = rows.Count(row => row.Status == "CANCELLED");
        var finished = completed + failed + cancelled;
        var active = rows.Count - finished;

        return new OperationalReportXlsx.Sheet(
            "Özet",
            ["Gösterge", "Değer"],
            [
                [OperationalReportXlsx.Text("Rapor başlangıcı"), OperationalReportXlsx.Date(window.From)],
                [OperationalReportXlsx.Text("Rapor bitişi"), OperationalReportXlsx.Date(window.To)],
                [OperationalReportXlsx.Text("Toplam görev"), OperationalReportXlsx.Number(rows.Count)],
                [OperationalReportXlsx.Text("Tamamlanan"), OperationalReportXlsx.Number(completed)],
                [OperationalReportXlsx.Text("Hatalı"), OperationalReportXlsx.Number(failed)],
                [OperationalReportXlsx.Text("İptal"), OperationalReportXlsx.Number(cancelled)],
                [OperationalReportXlsx.Text("Aktif"), OperationalReportXlsx.Number(active)],
                [OperationalReportXlsx.Text("Başarı oranı"), OperationalReportXlsx.Percent(finished == 0 ? null : completed / (double)finished)],
                [OperationalReportXlsx.Text("Ortalama toplam süre"), OperationalReportXlsx.Duration(AverageSeconds(rows.Select(row => row.TotalSeconds)))],
                [OperationalReportXlsx.Text("Ortalama depo hazırlık süresi"), OperationalReportXlsx.Duration(AverageSeconds(rows.Select(row => row.WarehouseResponseSeconds)))],
                [OperationalReportXlsx.Text("Ortalama robot çalışma süresi"), OperationalReportXlsx.Duration(AverageSeconds(rows.Select(row => row.RobotExecutionSeconds)))]
            ]);
    }

    private static long? AverageSeconds(IEnumerable<long?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : (long)Math.Round(available.Average(), MidpointRounding.AwayFromZero);
    }

    private static string? TaskResult(TaskReportRow row)
    {
        if (row.Status == "FAILED") return row.FailureMessage ?? row.FailureSummary ?? row.FailureCode ?? "Hata ile sonlandı";
        if (row.Status == "CANCELLED") return row.CancellationLabel ?? "İptal edildi";
        return null;
    }

    private static async Task<IResult> ListSessionsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? employeeNo,
        Guid? deviceId,
        Guid? machineId,
        string? activity,
        string? endReason,
        int? page,
        int? pageSize,
        IOperationalReportStore store,
        CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        return Results.Ok(await store.ListSessionsAsync(new SessionReportQuery(
            window.From, window.To, employeeNo, deviceId, machineId, activity, endReason, page ?? 1, pageSize ?? 50), cancellationToken));
    }

    private static async Task<IResult> ExportSessionsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? employeeNo,
        Guid? deviceId,
        Guid? machineId,
        string? activity,
        string? endReason,
        IOperationalReportStore store,
        CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        var rows = await AllSessionsAsync(store, new SessionReportQuery(
            window.From, window.To, employeeNo, deviceId, machineId, activity, endReason, 1, 500), cancellationToken);
        var csv = ReportCsv.Build(
            [
                "Giriş Tarihi", "Giriş Saati", "Çıkış Tarihi", "Çıkış Saati", "Oturum Süresi", "Sicil", "Kullanıcı",
                "Bilgisayar", "Makine", "Durum", "Çıkış Nedeni"
            ],
            rows.Select(row => new string?[]
            {
                ReportCsv.Date(row.LoginAt),
                ReportCsv.Time(row.LoginAt),
                ReportCsv.Date(row.EndedAt),
                ReportCsv.Time(row.EndedAt),
                ReportCsv.Duration(row.DurationSeconds),
                row.EmployeeNo,
                row.FullName,
                row.DeviceCode,
                row.MachineState == "UNKNOWN" ? "Geçmiş makine bilgisi kayıtlı değil" : row.MachineCode,
                row.StatusLabel,
                row.EndReasonLabel
            }));
        return ReportCsv.File(csv, $"TKG_Oturum_Raporu_{ReportCsv.FileDate(window.From)}.csv");
    }

    private static async Task<IResult> ExportSessionsXlsxAsync(
        DateTimeOffset? from, DateTimeOffset? to, string? employeeNo, Guid? deviceId,
        Guid? machineId, string? activity, string? endReason,
        IOperationalReportStore store, CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        var sessions = await AllSessionsAsync(store, new SessionReportQuery(
            window.From, window.To, employeeNo, deviceId, machineId, activity, endReason, 1, 500), cancellationToken);
        string[] headers = [
            "Giriş Zamanı", "Çıkış Zamanı", "Oturum Süresi", "Sicil", "Kullanıcı", "Kullanıcı Adı",
            "Roller", "Bilgisayar Kodu", "Bilgisayar", "Cihaz Türü", "Makine", "Durum", "Çıkış Nedeni"
        ];
        var rows = sessions.Select(row => (IReadOnlyList<OperationalReportXlsx.Cell>)[
            OperationalReportXlsx.Date(row.LoginAt), OperationalReportXlsx.Date(row.EndedAt),
            OperationalReportXlsx.Duration(row.DurationSeconds), OperationalReportXlsx.Text(row.EmployeeNo),
            OperationalReportXlsx.Text(row.FullName), OperationalReportXlsx.Text(row.Username),
            OperationalReportXlsx.Text(string.Join(", ", row.Roles)), OperationalReportXlsx.Text(row.DeviceCode),
            OperationalReportXlsx.Text(row.DeviceName), OperationalReportXlsx.Text(row.DeviceType),
            OperationalReportXlsx.Text(row.MachineState == "UNKNOWN" ? "Geçmiş makine bilgisi kayıtlı değil" : row.MachineCode),
            OperationalReportXlsx.Text(row.StatusLabel), OperationalReportXlsx.Text(row.EndReasonLabel)
        ]);
        return OperationalReportXlsx.File(headers, rows, $"TKG_Oturum_Raporu_{ReportCsv.FileDate(window.From)}.xlsx");
    }

    private static async Task<IResult> ReadSummaryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? machineId,
        IOperationalReportStore store,
        CancellationToken cancellationToken)
    {
        var window = Window(from, to);
        return Results.Ok(await store.ReadSummaryAsync(window.From, window.To, machineId, cancellationToken));
    }

    private static async Task<IReadOnlyList<TaskReportRow>> AllTasksAsync(
        IOperationalReportStore store,
        TaskReportQuery first,
        CancellationToken cancellationToken)
    {
        var page = await store.ListTasksAsync(first, cancellationToken);
        if (page.Total <= page.Items.Count) return page.Items;
        var all = new List<TaskReportRow>(page.Items);
        var next = 2;
        while (all.Count < page.Total && all.Count < 20000)
        {
            var more = await store.ListTasksAsync(first with { Page = next }, cancellationToken);
            if (more.Items.Count == 0) break;
            all.AddRange(more.Items);
            next++;
        }

        return all;
    }

    private static async Task<IReadOnlyList<SessionReportRow>> AllSessionsAsync(
        IOperationalReportStore store,
        SessionReportQuery first,
        CancellationToken cancellationToken)
    {
        var page = await store.ListSessionsAsync(first, cancellationToken);
        if (page.Total <= page.Items.Count) return page.Items;
        var all = new List<SessionReportRow>(page.Items);
        var next = 2;
        while (all.Count < page.Total && all.Count < 20000)
        {
            var more = await store.ListSessionsAsync(first with { Page = next }, cancellationToken);
            if (more.Items.Count == 0) break;
            all.AddRange(more.Items);
            next++;
        }

        return all;
    }

    private static (DateTimeOffset From, DateTimeOffset To) Window(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not null && to is not null) return (from.Value, to.Value);
        var zone = ReportCsv.Zone;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, zone.GetUtcOffset(local));
        return (from ?? start, to ?? start.AddDays(1));
    }
}

internal static class ReportCsv
{
    internal static readonly TimeZoneInfo Zone = ResolveZone();

    internal static string Build(IReadOnlyList<string> headers, IEnumerable<string?[]> rows)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        Write(builder, headers);
        foreach (var row in rows) Write(builder, row);
        return builder.ToString();
    }

    internal static IResult File(string csv, string fileName) =>
        Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", fileName);

    internal static string Date(DateTimeOffset? value) => value is null ? "" : Local(value.Value).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    internal static string Time(DateTimeOffset? value) => value is null ? "" : Local(value.Value).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    internal static string FileDate(DateTimeOffset value) => Local(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Duration(long? seconds)
    {
        if (seconds is null) return "";
        var value = seconds.Value;
        if (value < 60) return $"{value} sn";
        if (value < 3600)
        {
            var minutes = value / 60;
            var rest = value % 60;
            return rest == 0 ? $"{minutes} dk" : $"{minutes} dk {rest} sn";
        }

        var hours = value / 3600;
        var minutesLeft = (value % 3600) / 60;
        return $"{hours} sa {minutesLeft:00} dk";
    }

    private static DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);

    private static void Write(StringBuilder builder, IReadOnlyList<string?> cells)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            if (index > 0) builder.Append(';');
            builder.Append(Escape(cells[index]));
        }

        builder.Append("\r\n");
    }

    private static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        if (text.Contains(';') || text.Contains('"') || text.Contains('\n') || text.Contains('\r') || text.StartsWith('\''))
        {
            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return text;
    }

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Turkey Standard Time", "Europe/Istanbul" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }
}
