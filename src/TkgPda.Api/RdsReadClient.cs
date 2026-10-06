using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Application;

namespace TkgPda.Api;

public sealed record RdsOptions(
    bool Enabled,
    string? BaseUrl,
    int TimeoutSeconds,
    bool DispatchEnabled = false,
    string? IntegrationKey = null,
    string? CallbackUrl = null,
    string? CallbackKey = null,
    string? EmptyContentValue = null,
    string? FullContentValue = null,
    bool AllowInsecureRemoteHttp = false,
    int WorkerIntervalSeconds = 3,
    int WorkSiteFreshnessSeconds = 300,
    int AlarmObservationIntervalSeconds = 10)
{
    public static RdsOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new RdsOptions(
            configuration.GetValue("Rds:Enabled", false),
            configuration["Rds:BaseUrl"],
            configuration.GetValue("Rds:TimeoutSeconds", 5),
            configuration.GetValue("Rds:DispatchEnabled", false),
            configuration["Rds:IntegrationKey"],
            configuration["Rds:CallbackUrl"],
            configuration["Rds:CallbackKey"],
            configuration["Rds:EmptyContentValue"],
            configuration["Rds:FullContentValue"],
            configuration.GetValue("Rds:AllowInsecureRemoteHttp", false),
            configuration.GetValue("Rds:WorkerIntervalSeconds", 3),
            configuration.GetValue("Rds:WorkSiteFreshnessSeconds", 300),
            configuration.GetValue("Rds:AlarmObservationIntervalSeconds", 10));
        options.Validate();
        return options;
    }

    public Uri? BaseAddress => string.IsNullOrWhiteSpace(BaseUrl) ? null : new Uri(BaseUrl.Trim(), UriKind.Absolute);
    public string? BaseAddressDisplaySafe => BaseAddress is { } uri
        ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}"
        : null;

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("Rds:TimeoutSeconds must be between 1 and 60.");
        }

        if (WorkerIntervalSeconds is < 1 or > 300 || WorkSiteFreshnessSeconds is < 10 or > 86400)
        {
            throw new InvalidOperationException("RDS worker interval/freshness settings are outside the supported bounds.");
        }

        if (AlarmObservationIntervalSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException("Rds:AlarmObservationIntervalSeconds must be between 5 and 300.");
        }

        if (!Enabled)
        {
            if (DispatchEnabled)
            {
                throw new InvalidOperationException("Rds:DispatchEnabled requires Rds:Enabled.");
            }
            return;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Enabled RDS integration requires an absolute HTTP(S) BaseUrl without embedded credentials.");
        }

        // Transport security is independent of dispatch permission: a read-only
        // deployment still must not send plaintext HTTP to a non-loopback host.
        // This check therefore runs before the dispatch-only validation below.
        // Loopback stays permitted so the local simulator keeps working.
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !AllowInsecureRemoteHttp)
        {
            throw new InvalidOperationException("RDS refuses insecure non-loopback HTTP unless the explicit unsafe-development override is enabled.");
        }

        if (!DispatchEnabled)
        {
            return;
        }

        if (IntegrationKey?.Length is < 32 or > 512 || CallbackKey?.Length is < 32 or > 512)
        {
            throw new InvalidOperationException("RDS dispatch requires 32-512 character IntegrationKey and CallbackKey values from environment variables or user-secrets.");
        }

        if (!Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var callbackUri)
            || (callbackUri.Scheme != Uri.UriSchemeHttp && callbackUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(callbackUri.UserInfo))
        {
            throw new InvalidOperationException("RDS dispatch requires an absolute callback HTTP(S) URL without embedded credentials.");
        }

        if (callbackUri.Scheme == Uri.UriSchemeHttp && !callbackUri.IsLoopback && !AllowInsecureRemoteHttp)
        {
            throw new InvalidOperationException("RDS callbacks refuse insecure non-loopback HTTP unless the explicit unsafe-development override is enabled.");
        }
    }
}

public sealed class RdsReadClient(
    IHttpClientFactory httpClientFactory,
    RdsOptions options,
    TimeProvider timeProvider,
    ILogger<RdsReadClient> logger) : IRdsReadClient
{
    private const string WorkSitesEndpoint = "/api/work-sites/sites";
    private const string WindTasksEndpoint = "/api/queryWindTask";
    private const string RobotAlarmsEndpoint = "/api/agv-report/core";
    private readonly object lastReadLock = new();
    private DateTimeOffset? lastSuccessfulRead;

    public bool Enabled => options.Enabled;
    public string? SafeBaseAddress => options.BaseAddress is { } uri
        ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}"
        : null;
    public DateTimeOffset? LastSuccessfulRead
    {
        get { lock (lastReadLock) return lastSuccessfulRead; }
    }

    public async Task<RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>> GetWorkSitesAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        using var document = await ReadAsync(WorkSitesEndpoint, HttpMethod.Post, new { }, correlationId, cancellationToken);
        var root = document.RootElement;
        var (code, message, data) = ValidateEnvelope(root, WorkSitesEndpoint, correlationId);
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw Failure("RDS_INVALID_RESPONSE", "RDS WorkSite data is not an array.", WorkSitesEndpoint, correlationId, code);
        }

        var values = new List<RdsWorkSiteSnapshot>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Failure("RDS_INVALID_RESPONSE", "RDS WorkSite item is not an object.", WorkSitesEndpoint, correlationId, code);
            }

            var siteId = Text(item, "siteId")?.Trim();
            if (string.IsNullOrEmpty(siteId))
            {
                throw Failure("RDS_INVALID_RESPONSE", "RDS WorkSite item has no siteId.", WorkSitesEndpoint, correlationId, code);
            }

            values.Add(new RdsWorkSiteSnapshot(
                siteId,
                Text(item, "area"),
                Text(item, "groupName"),
                Number(item, "type"),
                Number(item, "filled"),
                Number(item, "locked"),
                Text(item, "lockedBy"),
                Number(item, "disabled"),
                Number(item, "syncFailed"),
                Text(item, "content", preserveEmpty: true),
                Text(item, "agvId"),
                item.Clone()));
        }

        var readAt = MarkSuccess();
        logger.LogInformation("RDS read succeeded for {Endpoint} with {Count} records and correlation {CorrelationId}.", WorkSitesEndpoint, values.Count, correlationId);
        return new(values, code, message, WorkSitesEndpoint, correlationId, readAt);
    }

    public async Task<RdsReadResult<RdsWindTaskPage>> QueryWindTasksAsync(
        RdsWindTaskQuery query,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.CurrentPage < 1 || query.PageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "RDS task query page values are outside the supported range.");
        }

        var filters = new Dictionary<string, object?>();
        Add(filters, "taskRecordId", query.TaskRecordId);
        Add(filters, "taskLabel", query.TaskLabel);
        Add(filters, "status", query.Status);
        Add(filters, "agvId", query.AgvId);
        var body = new { query.CurrentPage, query.PageSize, queryParam = filters };
        using var document = await ReadAsync(WindTasksEndpoint, HttpMethod.Post, body, correlationId, cancellationToken);
        var root = document.RootElement;
        var (code, message, data) = ValidateEnvelope(root, WindTasksEndpoint, correlationId);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw Failure("RDS_INVALID_RESPONSE", "RDS Wind Task data is not an object.", WindTasksEndpoint, correlationId, code);
        }

        var tasks = new List<RdsWindTaskSnapshot>();
        if (data.TryGetProperty("pageList", out var pageList) && pageList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pageList.EnumerateArray())
            {
                tasks.Add(new RdsWindTaskSnapshot(
                    Text(item, "taskRecordId") ?? Text(item, "id"),
                    Text(item, "taskLabel") ?? Text(item, "defLabel"),
                    Number(item, "status"),
                    Text(item, "agvId"),
                    Text(item, "outOrderNo"),
                    item.Clone(),
                    Text(item, "endedReason", preserveEmpty: true)));
            }
        }
        else if (data.TryGetProperty("pageList", out pageList) && pageList.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            throw Failure("RDS_INVALID_RESPONSE", "RDS Wind Task pageList is not an array or null.", WindTasksEndpoint, correlationId, code);
        }

        var page = new RdsWindTaskPage(
            Number(data, "totalCount") ?? tasks.Count,
            Number(data, "currentPage") ?? query.CurrentPage,
            Number(data, "pageSize") ?? query.PageSize,
            Number(data, "totalPage") ?? 0,
            tasks);
        var readAt = MarkSuccess();
        logger.LogInformation("RDS read succeeded for {Endpoint} with {Count} records and correlation {CorrelationId}.", WindTasksEndpoint, tasks.Count, correlationId);
        return new(page, code, message, WindTasksEndpoint, correlationId, readAt);
    }

    public async Task<RdsReadResult<RdsRobotAlarmReport>> GetRobotAlarmsAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        using var document = await ReadAsync(RobotAlarmsEndpoint, HttpMethod.Get, null, correlationId, cancellationToken);
        var root = document.RootElement;
        var (code, message, data) = ValidateEnvelope(root, RobotAlarmsEndpoint, correlationId);
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("report", out var report)
            || report.ValueKind != JsonValueKind.Array)
        {
            throw Failure("RDS_INVALID_RESPONSE", "RDS robot report data is not an array.", RobotAlarmsEndpoint, correlationId, code);
        }

        var alarms = new List<RdsRobotAlarmSnapshot>();
        var robots = new List<RdsRobotStateSnapshot>();
        foreach (var robot in report.EnumerateArray())
        {
            if (robot.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Both SEER identity spaces are preserved; RobotId is the vehicle-space
            // mutation/join key so that every reader agrees (see RdsRobotIdentity).
            var identity = RdsRobotIdentity.FromRobotElement(robot);
            var robotId = identity.MutationTarget;
            if (string.IsNullOrEmpty(robotId))
            {
                continue;
            }

            var dispatchable = robot.TryGetProperty("dispatchable", out var dispatchableElement)
                ? dispatchableElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null,
                }
                : null;
            var model = robot.TryGetProperty("basic_info", out var basicInfo) && basicInfo.ValueKind == JsonValueKind.Object
                ? Text(basicInfo, "model")
                : null;
            var basicMap = robot.TryGetProperty("basic_info", out var bi) && bi.ValueKind == JsonValueKind.Object
                ? Text(bi, "current_map")
                : null;

            double? batteryLevel = null;
            bool? isCharging = null;
            string? currentStation = null;
            string? lastStation = null;
            string? currentMap = basicMap;
            RdsCoreRobotLockInfo? lockInfo = null;
            string? activeOrderId = null;
            string? activeOrderState = null;
            bool? rdsReportedPayloadLoaded = Bool(robot, "isLoaded");
            // Absence of connectivity evidence is UNKNOWN (null), never ONLINE.
            bool? online = Bool(robot, "connection_status");
            bool? blocked = Bool(robot, "blocked");
            bool? emergency = null;
            double? vx = null;
            double? vy = null;
            int? taskStatus = null;

            if (robot.TryGetProperty("rbk_report", out var rbkReport) && rbkReport.ValueKind == JsonValueKind.Object)
            {
                batteryLevel = Double(rbkReport, "battery_level");
                isCharging = Bool(rbkReport, "charging");
                currentStation = Text(rbkReport, "current_station");
                lastStation = Text(rbkReport, "last_station");
                var rbkMap = Text(rbkReport, "current_map");
                if (!string.IsNullOrWhiteSpace(rbkMap)) currentMap = rbkMap;

                if (Bool(rbkReport, "blocked") is { } b) blocked = b;
                emergency = Bool(rbkReport, "emergency");
                vx = Double(rbkReport, "vx");
                vy = Double(rbkReport, "vy");
                taskStatus = Number(rbkReport, "task_status");

                if (rbkReport.TryGetProperty("lock_info", out var rbkLock) && rbkLock.ValueKind == JsonValueKind.Object)
                {
                    lockInfo = ReadLockInfo(rbkLock);
                }

                AppendAlarms(alarms, rbkReport, robotId);
            }

            if (lockInfo is null && robot.TryGetProperty("lock_info", out var topLock) && topLock.ValueKind == JsonValueKind.Object)
            {
                lockInfo = ReadLockInfo(topLock);
            }

            if (robot.TryGetProperty("current_order", out var curOrder) && curOrder.ValueKind == JsonValueKind.Object)
            {
                activeOrderId = Text(curOrder, "id");
                activeOrderState = Text(curOrder, "state");
            }

            robots.Add(new RdsRobotStateSnapshot(
                robotId,
                model,
                dispatchable,
                UndispatchableReasonText(robot),
                batteryLevel,
                isCharging,
                currentStation,
                lastStation,
                currentMap,
                lockInfo,
                activeOrderId,
                activeOrderState,
                rdsReportedPayloadLoaded,
                online,
                blocked,
                emergency,
                vx,
                vy,
                taskStatus,
                identity));
        }

        var readAt = MarkSuccess();
        logger.LogInformation(
            "RDS read succeeded for {Endpoint} with {Count} alarms across {RobotCount} robots and correlation {CorrelationId}.",
            RobotAlarmsEndpoint,
            alarms.Count,
            robots.Count,
            correlationId);
        return new(new RdsRobotAlarmReport(alarms, robots), code, message, RobotAlarmsEndpoint, correlationId, readAt);
    }

    /// <summary>
    /// Delegates to <see cref="RdsRobotAlarmParser"/>, the single SEER alarm parser,
    /// and attributes each entry to this robot. This method used to hold its own copy
    /// of the bucket/shape logic; the native telemetry client held a second, different
    /// copy that read undocumented paths and matched nothing. One implementation is
    /// what stops them drifting apart again.
    /// </summary>
    private static void AppendAlarms(List<RdsRobotAlarmSnapshot> alarms, JsonElement rbkReport, string robotId)
    {
        var parsed = new List<RdsParsedAlarm>();
        RdsRobotAlarmParser.Append(parsed, rbkReport, new HashSet<string>(StringComparer.Ordinal));
        foreach (var alarm in parsed)
        {
            alarms.Add(new RdsRobotAlarmSnapshot(
                robotId,
                alarm.Severity.ToString().ToLowerInvariant(),
                alarm.Code,
                alarm.Description,
                alarm.OccurredAt,
                alarm.Times));
        }
    }


    // Only booleans with an unambiguous meaning are surfaced; "unlock" and
    // "dispatchable_status" are numeric codes whose semantics are not
    // documented, so they are left out rather than guessed at.
    private static readonly string[] UndispatchableReasonFlags =
        ["disconnect", "unconfirmed_reloc", "low_battery", "current_map_invalid", "suspended"];

    private static string? UndispatchableReasonText(JsonElement robot)
    {
        if (!robot.TryGetProperty("undispatchable_reason", out var reason) || reason.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var active = UndispatchableReasonFlags
            .Where(flag => reason.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True)
            .ToArray();
        return active.Length > 0 ? string.Join(", ", active) : null;
    }

    private async Task<JsonDocument> ReadAsync(string endpoint, HttpMethod method, object? body, Guid correlationId, CancellationToken cancellationToken)
    {
        if (!Enabled || options.BaseAddress is null)
        {
            throw Failure("RDS_DISABLED", "RDS read integration is disabled.", endpoint, correlationId);
        }

        try
        {
            using var request = new HttpRequestMessage(method, endpoint);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }
            request.Headers.TryAddWithoutValidation("language", "en");
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId.ToString());
            using var response = await RdsHttpReadTransport.SendAsync(
                httpClientFactory,
                "RdsReadOnly",
                request,
                cancellationToken);
            if (response.StatusCode is < 200 or > 299)
            {
                throw Failure("RDS_HTTP_FAILURE", $"RDS returned HTTP {response.StatusCode}.", endpoint, correlationId);
            }
            return JsonDocument.Parse(response.Document!.RootElement.GetRawText());
        }
        catch (RdsReadException)
        {
            throw;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("RDS_TIMEOUT", "RDS read timed out.", endpoint, correlationId, innerException: error);
        }
        catch (HttpRequestException error)
        {
            throw Failure("RDS_UNAVAILABLE", "RDS read endpoint is unavailable.", endpoint, correlationId, innerException: error);
        }
        catch (JsonException error)
        {
            throw Failure("RDS_INVALID_JSON", "RDS returned invalid JSON.", endpoint, correlationId, innerException: error);
        }
    }

    private static (int Code, string Message, JsonElement Data) ValidateEnvelope(JsonElement root, string endpoint, Guid correlationId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("code", out var codeElement)
            || !TryNumber(codeElement, out var code)
            || !root.TryGetProperty("msg", out var messageElement))
        {
            throw Failure("RDS_INVALID_RESPONSE", "RDS response does not contain a valid code/msg envelope.", endpoint, correlationId);
        }

        var message = messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() ?? string.Empty : messageElement.ToString();
        if (code != 200)
        {
            throw Failure("RDS_BUSINESS_FAILURE", $"RDS business request failed: {message}", endpoint, correlationId, code);
        }

        if (!root.TryGetProperty("data", out var data))
        {
            throw Failure("RDS_INVALID_RESPONSE", "RDS response contains no data field.", endpoint, correlationId, code);
        }

        return (code, message, data);
    }

    private DateTimeOffset MarkSuccess()
    {
        var value = timeProvider.GetUtcNow();
        lock (lastReadLock) lastSuccessfulRead = value;
        return value;
    }

    private static string? Text(JsonElement item, string name, bool preserveEmpty = false)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return preserveEmpty || !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static int? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && TryNumber(value, out var number) ? number : null;

    private static bool TryNumber(JsonElement value, out int number) =>
        value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out number)
            : int.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null, out number);

    private static double? Double(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    private static bool? Bool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.GetInt32() != 0;
        return null;
    }

    /// <summary>
    /// Reads the full SEER <c>lock_info</c> fingerprint so Admin correlation can use
    /// the same contract as execution recovery. Returns null when the object carries
    /// no usable <c>locked</c> flag, so that missing telemetry stays UNKNOWN rather
    /// than collapsing to UNLOCKED.
    /// </summary>
    private static RdsCoreRobotLockInfo? ReadLockInfo(JsonElement lockElement)
    {
        if (Bool(lockElement, "locked") is not { } locked) return null;
        return new RdsCoreRobotLockInfo(
            locked,
            Text(lockElement, "ip"),
            Text(lockElement, "nick_name"),
            Number(lockElement, "port"),
            TimeToken(lockElement, "time_t"),
            Number(lockElement, "type"),
            Text(lockElement, "desc"));
    }

    private static string? TimeToken(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static void Add(Dictionary<string, object?> target, string key, object? value)
    {
        if (value is string text ? !string.IsNullOrWhiteSpace(text) : value is not null)
        {
            target[key] = value is string normalized ? normalized.Trim() : value;
        }
    }

    private static RdsReadException Failure(string code, string message, string endpoint, Guid correlationId, int? businessCode = null, Exception? innerException = null) =>
        new(code, message, endpoint, correlationId, businessCode, innerException);
}
