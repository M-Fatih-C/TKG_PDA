using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class OperationalReportingApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Task_report_projects_people_machine_device_and_success_without_inventing_a_system_device()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false, employeeNo: "9001");
        var requester = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A", employeeNo: "1234");
        await fixture.ExecuteAsync("UPDATE auth.users SET full_name = 'Fatih Çetintaş' WHERE id = @id", ("id", requester.UserId));
        var context = await fixture.ContextAsync("SH-01-A", "1");
        var created = new DateTimeOffset(2026, 10, 7, 5, 31, 4, TimeSpan.Zero);
        var ready = created.AddSeconds(131);
        var robotStarted = created.AddSeconds(143);
        var completed = created.AddSeconds(239);
        var requestId = await InsertRequestAsync(context, requester.UserId, "COMPLETED", created, partCode: "ŞİŞE-01");
        await EventAsync(requestId, "REQUEST_CREATED", created, requester.UserId, requester.DeviceId);
        await EventAsync(requestId, "WAREHOUSE_PREPARATION_READY", ready, requester.UserId, requester.DeviceId);
        await EventAsync(requestId, "ROBOT_TASK_STARTED", robotStarted, null, null);
        await EventAsync(requestId, "ROBOT_TASK_COMPLETED", completed, null, null);
        await EventAsync(requestId, "REQUEST_COMPLETED", completed, null, null);
        var systemId = await InsertRequestAsync(context, null, "QUEUED", created.AddMinutes(1), partCode: null);

        using var client = await fixture.AdminLoginClientAsync(admin);
        var page = await client.GetFromJsonAsync<TaskPage>(Report("tasks"));
        Assert.NotNull(page);
        var row = Assert.Single(page.Items, item => item.RequestId == requestId);
        Assert.Equal("Fatih Çetintaş", row.RequestedByName);
        Assert.Equal("1234", row.EmployeeNo);
        Assert.Equal("SH-01-A", row.MachineCode);
        Assert.Equal(requester.DeviceId, row.DeviceId);
        Assert.Equal(requester.DeviceCode, row.DeviceCode);
        Assert.Equal("ŞİŞE-01", row.PartCode);
        Assert.Equal("Malzeme Getir", row.OperationLabel);
        Assert.True(row.IsSuccess);
        Assert.Equal("Başarıyla tamamlandı", row.StatusLabel);
        Assert.Equal(239, row.TotalSeconds);
        Assert.Equal(131, row.WarehouseResponseSeconds);
        Assert.Equal(96, row.RobotExecutionSeconds);

        var system = Assert.Single(page.Items, item => item.RequestId == systemId);
        Assert.Null(system.DeviceId);
        Assert.Null(system.RequestedByName);
        Assert.False(system.IsSuccess);

        var summary = await client.GetFromJsonAsync<SummaryPage>(Report("summary"));
        Assert.NotNull(summary);
        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Active);
        Assert.Equal(1d, summary.SuccessRate);
        Assert.Equal(239d, summary.AverageTotalSeconds);
        Assert.Equal(131d, summary.AverageWarehouseSeconds);
        Assert.Equal(96d, summary.AverageRobotSeconds);
    }

    [Fact]
    public async Task Non_terminal_and_failed_states_are_not_success_and_missing_duration_stays_null()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        var context = await fixture.ContextAsync("SH-01-A", "1");
        var at = new DateTimeOffset(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);
        var running = await InsertRequestAsync(context, null, "RUNNING", at, partCode: null);
        await EventAsync(running, "REQUEST_CREATED", at, null, null);
        var sent = await InsertRequestAsync(context, null, "SENT_TO_RDS", at.AddMinutes(1), partCode: null);
        await EventAsync(sent, "REQUEST_CREATED", at.AddMinutes(1), null, null);
        var failed = await InsertRequestAsync(context, null, "FAILED", at.AddMinutes(2), partCode: null, failureCode: "MOTOR_CHECK_TIMEOUT", failureMessage: "Motor kontrol zaman aşımı");
        await EventAsync(failed, "REQUEST_CREATED", at.AddMinutes(2), null, null);
        await EventAsync(failed, "REQUEST_FAILED", at.AddMinutes(3), null, null);

        using var client = await fixture.AdminLoginClientAsync(admin);
        var page = await client.GetFromJsonAsync<TaskPage>(Report("tasks"));
        Assert.NotNull(page);
        Assert.All(page.Items, item => Assert.False(item.IsSuccess));
        var failedRow = Assert.Single(page.Items, item => item.RequestId == failed);
        Assert.Equal("Hata ile sonlandı", failedRow.StatusLabel);
        Assert.Equal("MOTOR_CHECK_TIMEOUT", failedRow.FailureCode);
        Assert.Equal("Motor kontrol zaman aşımı", failedRow.FailureMessage);
        Assert.Equal("Malzeme alma görevi başarısız oldu.", failedRow.FailureSummary);
        Assert.Null(failedRow.TotalSeconds);
        Assert.Null(failedRow.WarehouseResponseSeconds);
        Assert.Null(failedRow.RobotExecutionSeconds);
        Assert.Equal("RDS'ye gönderildi", Assert.Single(page.Items, item => item.RequestId == sent).StatusLabel);
        Assert.Equal("Robot çalışıyor", Assert.Single(page.Items, item => item.RequestId == running).StatusLabel);

        var summary = await client.GetFromJsonAsync<SummaryPage>(Report("summary"));
        Assert.NotNull(summary);
        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, summary.Active);
        var failure = Assert.Single(summary.TopFailures);
        Assert.Equal("MOTOR_CHECK_TIMEOUT", failure.Key);
        Assert.Equal(1, failure.Count);
    }

    [Fact]
    public async Task Cancellation_warehouse_and_robot_evidence_stay_distinct()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        var canceller = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A", employeeNo: "2222");
        var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.SemiFinishedWarehouseOperator, machineCode: "SH-01-A", bindDeviceToMachine: false, employeeNo: "3333");
        await fixture.ExecuteAsync("UPDATE auth.users SET full_name = 'Mehmet Yılmaz' WHERE id = @id", ("id", warehouse.UserId));
        var context = await fixture.ContextAsync("SH-01-A", "1");
        var at = new DateTimeOffset(2026, 10, 7, 7, 0, 0, TimeSpan.Zero);
        var cancelled = await InsertRequestAsync(context, canceller.UserId, "CANCELLED", at, "RPT-PART");
        var partId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.parts WHERE part_code = 'RPT-PART'");
        var containerId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types LIMIT 1");
        var areaId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.preparation_areas WHERE area_number = 2");
        await EventAsync(cancelled, "REQUEST_CREATED", at, canceller.UserId, canceller.DeviceId);
        await fixture.ExecuteAsync(
            """
            INSERT INTO ops.transport_request_events (transport_request_id, actor_user_id, device_id, event_type, created_at, data_json)
            VALUES (@id, @actor, @device, 'REQUEST_CANCELLED', @at, '{"cancellation_source":"PRE_EXECUTION"}'::jsonb)
            """,
            ("id", cancelled), ("actor", canceller.UserId), ("device", canceller.DeviceId), ("at", at.AddMinutes(2)));
        await fixture.ExecuteAsync(
            """
            INSERT INTO ops.preparation_pallets (
                preparation_area_id, container_type_id, load_state, part_id, transport_request_id,
                placed_by, placed_device_id, placed_at, ready_at, ready_by)
            VALUES (@area, @container, 'FULL', @part, @request, @user, @device, @placed, @ready, @user)
            """,
            ("area", areaId), ("container", containerId), ("part", partId), ("request", cancelled),
            ("user", warehouse.UserId), ("device", warehouse.DeviceId), ("placed", at.AddMinutes(1)), ("ready", at.AddMinutes(2)));
        await fixture.ExecuteAsync(
            """
            INSERT INTO integration.rds_tasks (transport_request_id, rds_task_record_id, agv_id, rds_status, ended_reason)
            VALUES (@id, 'rds-record-1', 'Sfl-Cbd15', 7, 'OPERATOR_STOP')
            """,
            ("id", cancelled));
        var systemCancel = await InsertRequestAsync(context, null, "CANCELLED", at.AddHours(1), null);
        await fixture.ExecuteAsync(
            """
            INSERT INTO ops.transport_request_events (transport_request_id, event_type, created_at, data_json)
            VALUES (@id, 'REQUEST_CANCELLED', @at, '{"cancellation_source":"RDS_CONFIRMED"}'::jsonb)
            """,
            ("id", systemCancel), ("at", at.AddHours(1)));

        using var client = await fixture.AdminLoginClientAsync(admin);
        var page = await client.GetFromJsonAsync<TaskPage>(Report("tasks"));
        var row = Assert.Single(page!.Items, item => item.RequestId == cancelled);
        Assert.Equal("İptal edildi", row.StatusLabel);
        Assert.Equal("OPERATOR", row.CancellationKind);
        Assert.Equal("Operatör iptali", row.CancellationLabel);
        Assert.Equal(canceller.DeviceCode, row.CancelledByDeviceCode);
        Assert.Equal("Mehmet Yılmaz", row.PreparationOperatorName);
        Assert.Equal("3333", row.PreparationEmployeeNo);
        Assert.Equal("Hazırlık Alanı 2", row.PreparationArea);
        Assert.Equal("Sfl-Cbd15", row.AgvId);
        Assert.Equal("rds-record-1", row.RdsTaskRecordId);
        Assert.False(row.IsSuccess);
        Assert.Equal("SYSTEM", Assert.Single(page.Items, item => item.RequestId == systemCancel).CancellationKind);

        var detail = await client.GetFromJsonAsync<TaskDetail>($"/api/v1/admin/reports/tasks/{cancelled}");
        Assert.Contains(detail!.Timeline, entry => entry.Title == "Talep oluşturuldu" && entry.ActorLine!.Contains("2222"));
        Assert.Contains(detail.Timeline, entry => entry.Title == "İptal edildi");
    }

    [Fact]
    public async Task Session_report_keeps_logout_expiry_snapshot_and_blocked_login_distinct()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        var user = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A", employeeNo: "4455");
        await fixture.ExecuteAsync("UPDATE auth.users SET full_name = 'Ayşe Demir' WHERE id = @id", ("id", user.UserId));
        using (var loggedIn = await fixture.LoginClientAsync(user))
        {
            var other = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-B", employeeNo: "7788");
            var blocked = fixture.Client();
            var denied = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest(user.Username, user.Password))
            };
            denied.Headers.Add(ApiHeaders.DeviceCode, other.DeviceCode);
            var blockedResponse = await blocked.SendAsync(denied);
            Assert.Equal(HttpStatusCode.Conflict, blockedResponse.StatusCode);
            (await loggedIn.PostAsync("/api/v1/auth/logout", null)).EnsureSuccessStatusCode();
        }

        await fixture.ExecuteAsync(
            """
            UPDATE auth.user_sessions
            SET revoked_at = created_at + interval '125 seconds', revocation_reason = 'USER_LOGOUT'
            WHERE user_id = @user AND revocation_reason = 'USER_LOGOUT'
            """,
            ("user", user.UserId));
        var otherMachine = await fixture.ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-B'");
        await fixture.ExecuteAsync("UPDATE audit.client_devices SET machine_id = @machine WHERE id = @device", ("machine", otherMachine), ("device", user.DeviceId));

        var expiredUser = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A", employeeNo: "9900");
        var expiredSession = Guid.NewGuid();
        await fixture.ExecuteAsync(
            """
            INSERT INTO auth.user_sessions (id, user_id, device_id, created_at, last_seen_at, lease_expires_at, token_expires_at, revoked_at, revocation_reason)
            VALUES (@id, @user, @device, @created, @created, @created, @created, @ended, 'LEASE_EXPIRED')
            """,
            ("id", expiredSession), ("user", expiredUser.UserId), ("device", expiredUser.DeviceId),
            ("created", WindowStart.AddDays(2)), ("ended", WindowStart.AddDays(2).AddMinutes(10)));
        await fixture.ExecuteAsync(
            """
            INSERT INTO audit.audit_logs (actor_user_id, device_id, action, entity_type, entity_id, after_json)
            VALUES (@user, @device, 'AUTH_SESSION_CREATED', 'AUTH_SESSION', @session, '{"sessionId":"old"}'::jsonb)
            """,
            ("user", expiredUser.UserId), ("device", expiredUser.DeviceId), ("session", expiredSession.ToString()));

        using var client = await fixture.AdminLoginClientAsync(admin);
        var page = await client.GetFromJsonAsync<SessionPage>($"{Report("sessions")}&employeeNo=4455");
        var session = Assert.Single(page!.Items);
        Assert.Equal("Ayşe Demir", session.FullName);
        Assert.Equal("4455", session.EmployeeNo);
        Assert.Equal(user.DeviceCode, session.DeviceCode);
        Assert.Equal("Kullanıcı çıkışı", session.StatusLabel);
        Assert.NotEqual("Kullanıcı çıkışı", "Süre doldu");
        Assert.Equal(125, session.DurationSeconds);
        Assert.Equal("RECORDED", session.MachineState);
        Assert.Equal("SH-01-A", session.MachineCode);
        Assert.Contains(page.BlockedAttempts, item => item.EmployeeNo == "4455" && item.Message == "Bu sicil başka bir cihazda zaten aktifti.");

        var expired = await client.GetFromJsonAsync<SessionPage>($"{Report("sessions")}&employeeNo=9900");
        var expiredRow = Assert.Single(expired!.Items);
        Assert.Equal("Süre doldu", expiredRow.StatusLabel);
        Assert.NotEqual("Kullanıcı çıkışı", expiredRow.StatusLabel);
        Assert.Equal("UNKNOWN", expiredRow.MachineState);
        Assert.Null(expiredRow.MachineCode);

        var excel = await client.GetAsync($"/api/v1/admin/reports/sessions/export.xlsx?from={Uri.EscapeDataString(WindowStart.ToString("O"))}&to={Uri.EscapeDataString(WindowEnd.ToString("O"))}&employeeNo=4455");
        excel.EnsureSuccessStatusCode();
        await using var excelStream = await excel.Content.ReadAsStreamAsync();
        using var workbook = new ZipArchive(excelStream, ZipArchiveMode.Read);
        var sheet = await ReadEntryAsync(workbook, "xl/worksheets/sheet1.xml");
        var styles = await ReadEntryAsync(workbook, "xl/styles.xml");
        Assert.Contains("Oturum Süresi", sheet);
        Assert.Contains("Ayşe Demir", sheet);
        Assert.Contains("[&lt;0.416666666666667]&quot;0&quot;[h]:mm:ss", styles);
    }

    [Fact]
    public async Task Filters_pagination_csv_and_audit_drilldown_are_server_side_and_admin_only()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        var user = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A", employeeNo: "1234");
        var otherMachine = await fixture.ContextAsync("SH-01-B", "1");
        var home = await fixture.ContextAsync("SH-01-A", "1");
        var older = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(2);
        var outside = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var first = await InsertRequestAsync(home, user.UserId, "COMPLETED", older, "AAA");
        var second = await InsertRequestAsync(home, user.UserId, "FAILED", newer, "BBB", "=Şişe", "FORMULA");
        var other = await InsertRequestAsync(otherMachine, user.UserId, "COMPLETED", newer.AddMinutes(5), "CCC");
        await InsertRequestAsync(home, user.UserId, "COMPLETED", outside, "OLD");
        var correlation = Guid.NewGuid();
        await fixture.ExecuteAsync(
            """
            UPDATE ops.transport_requests SET correlation_id = @correlation WHERE id = @id;
            INSERT INTO audit.audit_logs (actor_user_id, action, entity_type, entity_id, correlation_id, after_json)
            VALUES (@user, 'REQUEST_CREATED', 'TRANSPORT_REQUEST', @entity, @correlation, '{}'::jsonb)
            """,
            ("correlation", correlation), ("id", second), ("user", user.UserId), ("entity", second.ToString()));

        using var client = await fixture.AdminLoginClientAsync(admin);
        var day = await client.GetFromJsonAsync<TaskPage>($"/api/v1/admin/reports/tasks?from=2026-10-07T00:00:00Z&to=2026-10-08T00:00:00Z");
        Assert.Equal(3, day!.Total);
        var machine = await client.GetFromJsonAsync<TaskPage>($"/api/v1/admin/reports/tasks?from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:00Z&machineId={home.MachineId}");
        Assert.DoesNotContain(machine!.Items, item => item.RequestId == other);
        var sicil = await client.GetFromJsonAsync<TaskPage>($"{Report("tasks")}&employeeNo=1234&status=FAILED");
        Assert.Equal(second, Assert.Single(sicil!.Items).RequestId);
        var page1 = await client.GetFromJsonAsync<TaskPage>($"/api/v1/admin/reports/tasks?from=2026-10-07T00:00:00Z&to=2026-10-08T00:00:00Z&machineId={home.MachineId}&page=1&pageSize=1");
        var page2 = await client.GetFromJsonAsync<TaskPage>($"/api/v1/admin/reports/tasks?from=2026-10-07T00:00:00Z&to=2026-10-08T00:00:00Z&machineId={home.MachineId}&page=2&pageSize=1");
        Assert.Equal(second, page1!.Items[0].RequestId);
        Assert.Equal(first, page2!.Items[0].RequestId);

        var export = await client.GetAsync($"/api/v1/admin/reports/tasks/export.csv?from=2026-10-07T00:00:00Z&to=2026-10-08T00:00:00Z&status=FAILED&employeeNo=1234");
        export.EnsureSuccessStatusCode();
        Assert.Contains("TKG_Gorev_Raporu_", export.Content.Headers.ContentDisposition!.FileName);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Talep No", csv);
        Assert.Contains("Şişe", csv);
        Assert.Contains("'=Şişe", csv);
        Assert.DoesNotContain(second.ToString(), csv);

        var excel = await client.GetAsync($"/api/v1/admin/reports/tasks/export.xlsx?from=2026-10-07T00:00:00Z&to=2026-10-08T00:00:00Z&status=FAILED&employeeNo=1234");
        excel.EnsureSuccessStatusCode();
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            excel.Content.Headers.ContentType!.MediaType);
        await using var excelStream = await excel.Content.ReadAsStreamAsync();
        using var workbook = new ZipArchive(excelStream, ZipArchiveMode.Read);
        var sheet = await ReadEntryAsync(workbook, "xl/worksheets/sheet1.xml");
        var styles = await ReadEntryAsync(workbook, "xl/styles.xml");
        var expectedHeaders = new[]
        {
            "Talep No", "Tarih", "Saat", "Sicil", "Kullanıcı", "Bilgisayar", "Makine", "Varyant", "İşlem", "Parça",
            "Durum", "Depo Operatörü", "Hazırlık Alanı", "Robot", "Toplam Süre", "Depo Süresi", "Robot Süresi",
            "Hata Kodu", "Hata Açıklaması"
        };
        var lastHeaderPosition = -1;
        foreach (var header in expectedHeaders)
        {
            var headerPosition = sheet.IndexOf($">{header}<", StringComparison.Ordinal);
            Assert.True(headerPosition > lastHeaderPosition, $"Expected header '{header}' in customer-approved order.");
            lastHeaderPosition = headerPosition;
        }
        Assert.DoesNotContain("Depo Hazırlık Başlangıcı", sheet);
        Assert.DoesNotContain("Robot Başlangıç", sheet);
        Assert.Contains("Şişe", sheet);
        Assert.Contains("[&lt;0.416666666666667]&quot;0&quot;[h]:mm:ss", styles);

        var hash = await fixture.ScalarAsync<string>("SELECT password_hash FROM auth.users WHERE id = @id", ("id", admin.UserId));
        var body = await client.GetStringAsync(Report("tasks"));
        Assert.DoesNotContain(hash, body);
        Assert.DoesNotContain("passwordHash", body);
        Assert.DoesNotContain("integrationKey", body);
        Assert.DoesNotContain("requestPayload", body);

        var audit = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/audit-logs?correlationId={correlation}&entityId={second}");
        Assert.Equal(1, audit.GetArrayLength());
        Assert.Equal("REQUEST_CREATED", audit[0].GetProperty("action").GetString());

        using var field = await fixture.LoginClientAsync(user);
        var denied = await field.GetAsync("/api/v1/admin/reports/tasks");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var auditStillThere = await client.GetAsync("/api/v1/admin/audit-logs?limit=5");
        Assert.Equal(HttpStatusCode.OK, auditStillThere.StatusCode);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"{name} missing");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static string Report(string kind) =>
        $"/api/v1/admin/reports/{kind}?from={Uri.EscapeDataString(WindowStart.ToString("O"))}&to={Uri.EscapeDataString(WindowEnd.ToString("O"))}";

    private async Task<Guid> InsertRequestAsync(
        LayoutContext context,
        Guid? userId,
        string status,
        DateTimeOffset createdAt,
        string? partCode,
        string? failureMessage = null,
        string? failureCode = null)
    {
        Guid? partId = null;
        if (partCode is not null)
        {
            partId = await fixture.ScalarAsync<Guid>(
                """
                INSERT INTO master.parts (part_code, description)
                VALUES (@code, @description)
                ON CONFLICT (part_code) DO UPDATE SET description = EXCLUDED.description
                RETURNING id
                """,
                ("code", partCode),
                ("description", partCode == "ŞİŞE-01" ? "Şişe gövdesi" : partCode));
        }

        return await fixture.ScalarAsync<Guid>(
            """
            INSERT INTO ops.transport_requests (
                request_origin, requested_by, machine_id, variant_id, layout_id, task_type_id,
                part_id, status, failure_code, failure_message, created_at)
            SELECT 'LINE_OPERATOR', @user, @machine, @variant, @layout, tt.id, @part, @status, @failure_code, @failure_message, @created
            FROM master.task_types tt WHERE tt.code = 'BRING_FULL'
            RETURNING id
            """,
            ("user", (object?)userId ?? DBNull.Value),
            ("machine", context.MachineId),
            ("variant", context.VariantId),
            ("layout", context.LayoutId),
            ("part", (object?)partId ?? DBNull.Value),
            ("status", status),
            ("failure_code", (object?)failureCode ?? DBNull.Value),
            ("failure_message", (object?)failureMessage ?? DBNull.Value),
            ("created", createdAt));
    }

    private Task EventAsync(Guid requestId, string eventType, DateTimeOffset at, Guid? actor, Guid? device) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ops.transport_request_events (transport_request_id, actor_user_id, device_id, event_type, created_at)
            VALUES (@id, @actor, @device, @event, @at)
            """,
            ("id", requestId),
            ("actor", (object?)actor ?? DBNull.Value),
            ("device", (object?)device ?? DBNull.Value),
            ("event", eventType),
            ("at", at));

    private sealed record TaskPage(TaskRow[] Items, int Total, int Page, int PageSize);
    private sealed record TaskRow(
        Guid RequestId,
        string? RequestedByName,
        string? EmployeeNo,
        Guid? DeviceId,
        string? DeviceCode,
        string MachineCode,
        string? PartCode,
        string OperationLabel,
        string StatusLabel,
        bool IsSuccess,
        string? FailureCode,
        string? FailureMessage,
        string? FailureSummary,
        string? CancellationKind,
        string? CancellationLabel,
        string? CancelledByDeviceCode,
        string? PreparationOperatorName,
        string? PreparationEmployeeNo,
        string? PreparationArea,
        string? AgvId,
        string? RdsTaskRecordId,
        long? TotalSeconds,
        long? WarehouseResponseSeconds,
        long? RobotExecutionSeconds);
    private sealed record TaskDetail(TaskRow Task, TimelineEntry[] Timeline);
    private sealed record TimelineEntry(string Title, string? ActorLine);
    private sealed record SessionPage(SessionRow[] Items, int Total, BlockedRow[] BlockedAttempts);
    private sealed record SessionRow(string FullName, string? EmployeeNo, string DeviceCode, string StatusLabel, long? DurationSeconds, string MachineState, string? MachineCode);
    private sealed record BlockedRow(string? EmployeeNo, string Message);
    private sealed record SummaryPage(
        int Total,
        int Completed,
        int Failed,
        int Cancelled,
        int Active,
        double? SuccessRate,
        double? AverageTotalSeconds,
        double? AverageWarehouseSeconds,
        double? AverageRobotSeconds,
        ReportCountRow[] TopFailures);
    private sealed record ReportCountRow(string Key, string Label, int Count);
}
