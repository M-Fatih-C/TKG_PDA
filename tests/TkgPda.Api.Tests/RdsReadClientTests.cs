using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class RdsReadClientTests
{
    [Theory]
    [InlineData("0",0)]
    [InlineData("1",1)]
    [InlineData("2",2)]
    [InlineData("-1",-1)]
    [InlineData("null",null)]
    [InlineData("true",null)]
    [InlineData("0.5",null)]
    [InlineData("\"0\"",0)]
    [InlineData("\"1\"",1)]
    public async Task Filled_and_locked_preserve_numeric_evidence_and_unrecognized_types_as_unknown(string wire,int? expected)
    {
        var payload=$$"""{"code":200,"msg":"Success","data":[{"siteId":"ISOLATED","type":1,"filled":{{wire}},"locked":{{wire}}}]}""";
        var site=Assert.Single((await Client(new RecordingHandler(payload)).GetWorkSitesAsync(Guid.NewGuid())).Data);
        Assert.Equal(expected,site.Filled);
        Assert.Equal(expected,site.Locked);
        Assert.Equal(wire,site.RawState.GetProperty("filled").GetRawText());
    }

    [Fact]
    public async Task Worksite_response_preserves_external_evidence()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":[{"siteId":"SITE-1","type":1,"filled":1,"locked":0,"disabled":1,"syncFailed":0,"content":" exact content ","area":"A","groupName":"G","agvId":"AGV-1"}]}
            """);
        var client = Client(handler);

        var result = await client.GetWorkSitesAsync(Guid.NewGuid());
        var site = Assert.Single(result.Data);

        Assert.Equal(200, result.BusinessCode);
        Assert.Equal("SITE-1", site.SiteId);
        Assert.Equal(1, site.Type);
        Assert.Equal(1, site.Filled);
        Assert.Equal(0, site.Locked);
        Assert.Equal(1, site.Disabled);
        Assert.Equal(0, site.SyncFailed);
        Assert.Equal(" exact content ", site.Content);
        Assert.Contains("\"siteId\":\"SITE-1\"", site.RawState.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("/api/work-sites/sites", handler.LastPath);
        Assert.Equal("en", handler.LastLanguage);
    }

    [Fact]
    public async Task Http_200_business_failure_is_rejected_with_context()
    {
        var client = Client(new RecordingHandler("{\"code\":60008,\"msg\":\"Failure\",\"data\":null}"));

        var error = await Assert.ThrowsAsync<RdsReadException>(() => client.GetWorkSitesAsync(Guid.Parse("11111111-1111-1111-1111-111111111111")));

        Assert.Equal("RDS_BUSINESS_FAILURE", error.Code);
        Assert.Equal(60008, error.BusinessCode);
        Assert.Equal("/api/work-sites/sites", error.Endpoint);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), error.CorrelationId);
    }

    [Fact]
    public async Task Empty_wind_task_page_is_a_valid_read_result()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"totalCount":0,"currentPage":1,"pageSize":5,"totalPage":0,"pageList":null}}
            """);
        var client = Client(handler);

        var result = await client.QueryWindTasksAsync(new RdsWindTaskQuery(PageSize: 5), Guid.NewGuid());

        Assert.Equal(0, result.Data.TotalCount);
        Assert.Empty(result.Data.Tasks);
        Assert.Equal("/api/queryWindTask", handler.LastPath);
        Assert.Contains("\"pageSize\":5", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Robot_alarms_flatten_across_severity_buckets_with_correct_fields()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"sim_01","vehicle_id":"sim_01","dispatchable":true,
                 "basic_info":{"model":"AMB-150"},
                 "undispatchable_reason":{"disconnect":false,"unconfirmed_reloc":false,"unlock":0,"low_battery":false,"current_map_invalid":false,"dispatchable_status":0,"suspended":false},
                 "rbk_report":{
                    "fatals":[{"code":"F1","desc":"Fatal one","time":"2026-08-30T10:00:00Z","times":1}],
                    "errors":[{"code":"E1","desc":"Error one","time":"2026-08-30T10:01:00Z","times":2}],
                    "warnings":[{"code":"W1","desc":"Warning one","time":"2026-08-30T10:02:00Z","times":3}],
                    "notices":[{"code":"N1","desc":"Notice one","time":"2026-08-30T10:03:00Z","times":4}]}}
            ]}}
            """);
        var client = Client(handler);

        var result = await client.GetRobotAlarmsAsync(Guid.NewGuid());

        Assert.Equal(4, result.Data.Alarms.Count);
        Assert.Equal("/api/agv-report/core", handler.LastPath);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        var fatal = Assert.Single(result.Data.Alarms, a => a.Code == "F1");
        Assert.Equal("fatal", fatal.Severity);
        Assert.Equal("sim_01", fatal.RobotId);
        Assert.Equal("Fatal one", fatal.Description);
        Assert.Equal("2026-08-30T10:00:00Z", fatal.OccurredAt);
        Assert.Equal(1, fatal.OccurrenceCount);
        Assert.Equal("error", Assert.Single(result.Data.Alarms, a => a.Code == "E1").Severity);
        Assert.Equal("warning", Assert.Single(result.Data.Alarms, a => a.Code == "W1").Severity);
        Assert.Equal("notice", Assert.Single(result.Data.Alarms, a => a.Code == "N1").Severity);

        var robot = Assert.Single(result.Data.Robots);
        Assert.Equal("sim_01", robot.RobotId);
        Assert.Equal("AMB-150", robot.Model);
        Assert.True(robot.Dispatchable);
        Assert.Null(robot.UndispatchableReason);
    }

    [Fact]
    public async Task Robot_alarms_are_deterministic_across_multiple_robots()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"sim_01","dispatchable":true,"rbk_report":{"errors":[{"code":"E1","desc":"d1","time":"t1","times":1}]}},
                {"uuid":"sim_02","dispatchable":false,
                 "undispatchable_reason":{"disconnect":false,"unconfirmed_reloc":false,"unlock":0,"low_battery":true,"current_map_invalid":false,"dispatchable_status":0,"suspended":false},
                 "rbk_report":{"warnings":[{"code":"UNKNOWN_CODE","desc":"d2","time":"t2","times":2}]}}
            ]}}
            """);
        var client = Client(handler);

        var result = await client.GetRobotAlarmsAsync(Guid.NewGuid());

        Assert.Equal(["sim_01", "sim_02"], result.Data.Alarms.Select(a => a.RobotId));
        Assert.Equal("UNKNOWN_CODE", result.Data.Alarms[1].Code);
        var sim02 = result.Data.Robots.Single(r => r.RobotId == "sim_02");
        Assert.Equal("low_battery", sim02.UndispatchableReason);
    }

    [Fact]
    public async Task Empty_robot_alarm_snapshot_is_a_valid_result()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[{"uuid":"sim_01","dispatchable":true,"rbk_report":{"errors":[],"fatals":[],"warnings":[],"notices":[]}}]}}
            """);
        var client = Client(handler);

        var result = await client.GetRobotAlarmsAsync(Guid.NewGuid());

        Assert.Empty(result.Data.Alarms);
        Assert.Single(result.Data.Robots);
    }

    [Fact]
    public async Task Malformed_alarm_item_without_code_is_skipped_safely()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"sim_01","rbk_report":{"errors":[{"desc":"no code here","time":"t","times":1}]}}
            ]}}
            """);
        var client = Client(handler);

        var result = await client.GetRobotAlarmsAsync(Guid.NewGuid());

        Assert.Empty(result.Data.Alarms);
    }

    [Fact]
    public async Task Robot_alarm_read_failure_surfaces_as_rds_unavailable_not_empty()
    {
        var client = Client(new RecordingHandler("{\"code\":50000,\"msg\":\"boom\",\"data\":null}"));

        var error = await Assert.ThrowsAsync<RdsReadException>(() => client.GetRobotAlarmsAsync(Guid.NewGuid()));

        Assert.Equal("RDS_BUSINESS_FAILURE", error.Code);
    }

    [Fact]
    public async Task Disabled_client_does_not_send_http()
    {
        var handler = new RecordingHandler("{}");
        var options = new RdsOptions(false, null, 5);
        var client = new RdsReadClient(new Factory(handler), options, TimeProvider.System, NullLogger<RdsReadClient>.Instance);

        var error = await Assert.ThrowsAsync<RdsReadException>(() => client.GetWorkSitesAsync(Guid.NewGuid()));

        Assert.Equal("RDS_DISABLED", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Robot_telemetry_fields_are_parsed_accurately()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {
                    "uuid":"1003",
                    "vehicle_id":"1003",
                    "dispatchable":true,
                    "connection_status":1,
                    "isLoaded":false,
                    "basic_info":{"model":"HELIFORK","current_map":"26.7.2026_map_optimak"},
                    "current_order":{"id":"ORD-999","state":"RUNNING"},
                    "rbk_report":{
                        "battery_level":0.88,
                        "charging":true,
                        "current_station":"PP80",
                        "last_station":"LM12",
                        "current_map":"26.7.2026_map_optimak",
                        "lock_info":{"locked":true,"nick_name":"TKG_LOCK"},
                        "errors":[],"fatals":[],"warnings":[],"notices":[]
                    }
                }
            ]}}
            """);
        var client = Client(handler);

        var result = await client.GetRobotAlarmsAsync(Guid.NewGuid());

        var robot = Assert.Single(result.Data.Robots);
        Assert.Equal("1003", robot.RobotId);
        Assert.Equal("HELIFORK", robot.Model);
        Assert.True(robot.Dispatchable);
        Assert.True(robot.Online);
        Assert.Equal(0.88, robot.BatteryLevel);
        Assert.True(robot.IsCharging);
        Assert.Equal("PP80", robot.CurrentStation);
        Assert.Equal("LM12", robot.LastStation);
        Assert.Equal("26.7.2026_map_optimak", robot.CurrentMap);
        Assert.True(robot.IsLocked);
        Assert.Equal("TKG_LOCK", robot.LockNickName);
        Assert.Equal("ORD-999", robot.ActiveOrderId);
        Assert.Equal("RUNNING", robot.ActiveOrderState);
        Assert.False(robot.RdsReportedPayloadLoaded);
    }

    [Fact]
    public async Task Both_seer_identity_spaces_are_preserved_when_uuid_differs_from_vehicle_id()
    {
        // Real SEER robots report a different internal uuid and dispatch vehicle_id.
        // The simulator reports them identically, which is why the previous
        // uuid-first / vehicle-first disagreement between readers went unnoticed.
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {
                    "uuid":"ROBOT-INTERNAL-123",
                    "vehicle_id":"AGV-01",
                    "dispatchable":true,
                    "connection_status":1,
                    "rbk_report":{"errors":[],"fatals":[],"warnings":[],"notices":[]}
                }
            ]}}
            """);

        var result = await Client(handler).GetRobotAlarmsAsync(Guid.NewGuid());

        var robot = Assert.Single(result.Data.Robots);
        // RobotId is the vehicle-space mutation/join key.
        Assert.Equal("AGV-01", robot.RobotId);
        // Both spaces stay addressable.
        Assert.Equal("ROBOT-INTERNAL-123", robot.ResolvedIdentity.Uuid);
        Assert.Equal("AGV-01", robot.ResolvedIdentity.VehicleId);
        // Correlation is identity-space agnostic in both directions...
        Assert.True(robot.ResolvedIdentity.Matches("AGV-01"));
        Assert.True(robot.ResolvedIdentity.Matches("ROBOT-INTERNAL-123"));
        // ...but never widens to an unrelated robot or a blank candidate.
        Assert.False(robot.ResolvedIdentity.Matches("AGV-99"));
        Assert.False(robot.ResolvedIdentity.Matches(null));
        Assert.False(robot.ResolvedIdentity.Matches("   "));
    }

    [Fact]
    public async Task Absent_connectivity_and_lock_telemetry_stay_unknown_rather_than_online_and_unlocked()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"AGV-02","vehicle_id":"AGV-02",
                 "rbk_report":{"errors":[],"fatals":[],"warnings":[],"notices":[]}}
            ]}}
            """);

        var result = await Client(handler).GetRobotAlarmsAsync(Guid.NewGuid());

        var robot = Assert.Single(result.Data.Robots);
        Assert.Null(robot.Online);
        Assert.Null(robot.LockInfo);
        Assert.Null(robot.IsLocked);
        Assert.Null(robot.BatteryLevel);
        Assert.Null(robot.Dispatchable);
    }

    [Fact]
    public async Task Full_seer_lock_fingerprint_is_captured_for_correlation()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"AGV-03","vehicle_id":"AGV-03","connection_status":1,
                 "rbk_report":{
                    "lock_info":{"locked":true,"nick_name":"TKG_LOCK","ip":"10.0.0.5","port":8088,"time_t":1788522696,"type":1,"desc":"map prep"},
                    "errors":[],"fatals":[],"warnings":[],"notices":[]}}
            ]}}
            """);

        var result = await Client(handler).GetRobotAlarmsAsync(Guid.NewGuid());

        var lockInfo = Assert.Single(result.Data.Robots).LockInfo;
        Assert.NotNull(lockInfo);
        Assert.True(lockInfo.Locked);
        Assert.Equal("TKG_LOCK", lockInfo.NickName);
        Assert.Equal("10.0.0.5", lockInfo.Ip);
        Assert.Equal(8088, lockInfo.Port);
        Assert.Equal("1788522696", lockInfo.TimeT);
        Assert.Equal(1, lockInfo.Type);
        Assert.True(lockInfo.HasUsableFingerprint);
    }

    [Fact]
    public async Task Both_documented_seer_alarm_shapes_are_parsed_with_their_bucket_severity()
    {
        // Literal payloads from RDS DÖKÜMANLARI/RdsScriptMethods/RdsScriptMethods_en.md:
        // the core-level entry carries an explicit "code", the robot-level entry uses
        // the alarm code as the property key with the timestamp as its value.
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"hu-test-1","vehicle_id":"AMB-01","connection_status":1,
                 "rbk_report":{
                    "fatals":[{"code":50401,"desc":"battery too low and shutdown","times":1,"timestamp":1681110452}],
                    "errors":[{"52030":1681110460,"desc":"roller error","times":2}],
                    "warnings":[{"55001":1634020734,"desc":"The robot is in the dispatching state.","times":1}],
                    "notices":[{"code":80004,"desc":"no joining order","times":1}]}}
            ]}}
            """);

        var result = await Client(handler).GetRobotAlarmsAsync(Guid.NewGuid());

        var byCode = result.Data.Alarms.ToDictionary(a => a.Code, a => a);
        Assert.Equal(4, result.Data.Alarms.Count);

        // Explicit-code shape.
        Assert.Equal("fatal", byCode["50401"].Severity);
        Assert.Equal("battery too low and shutdown", byCode["50401"].Description);

        // Dynamically keyed shape — previously dropped entirely.
        Assert.Equal("error", byCode["52030"].Severity);
        Assert.Equal("roller error", byCode["52030"].Description);
        Assert.Equal(2, byCode["52030"].OccurrenceCount);
        Assert.Equal("1681110460", byCode["52030"].OccurredAt);

        Assert.Equal("warning", byCode["55001"].Severity);
        // A notice stays a notice; it is never promoted to an error.
        Assert.Equal("notice", byCode["80004"].Severity);

        // Every alarm is attributed to the vehicle-space robot key.
        Assert.All(result.Data.Alarms, a => Assert.Equal("AMB-01", a.RobotId));
    }

    [Fact]
    public async Task Malformed_and_duplicate_alarm_entries_do_not_break_the_fleet_read()
    {
        var handler = new RecordingHandler("""
            {"code":200,"msg":"Success","data":{"report":[
                {"uuid":"AGV-09","vehicle_id":"AGV-09","connection_status":1,
                 "rbk_report":{
                    "errors":[
                        {"52030":1681110460,"desc":"roller error","times":1},
                        {"52030":1681110499,"desc":"roller error repeated","times":1},
                        {"desc":"no code at all","times":1},
                        "not-an-object",
                        {}],
                    "fatals":[],"warnings":[],"notices":[]}}
            ]}}
            """);

        var result = await Client(handler).GetRobotAlarmsAsync(Guid.NewGuid());

        // The duplicate is collapsed, the malformed entries are skipped, and the read
        // still succeeds.
        var alarm = Assert.Single(result.Data.Alarms);
        Assert.Equal("52030", alarm.Code);
        Assert.Equal("error", alarm.Severity);
        Assert.Single(result.Data.Robots);
    }

    [Fact]
    public void Read_only_configuration_still_refuses_insecure_remote_http()
    {
        // Transport security is independent of dispatch permission.
        var remoteHttpReadOnly = () => new RdsOptions(true, "http://10.20.30.40:8080", 5, DispatchEnabled: false).Validate();
        Assert.Throws<InvalidOperationException>(remoteHttpReadOnly);

        var remoteHttpDispatching = () => new RdsOptions(
            true, "http://10.20.30.40:8080", 5, DispatchEnabled: true,
            IntegrationKey: new string('k', 40), CallbackUrl: "https://tkg.example.test/cb",
            CallbackKey: new string('c', 40)).Validate();
        Assert.Throws<InvalidOperationException>(remoteHttpDispatching);

        // Loopback stays usable for the local simulator, with or without dispatch.
        new RdsOptions(true, "http://localhost:8080", 5, DispatchEnabled: false).Validate();
        new RdsOptions(true, "http://127.0.0.1:8080", 5, DispatchEnabled: false).Validate();

        // Remote HTTPS is fine, and an explicit override permits remote HTTP.
        new RdsOptions(true, "https://rds.example.test", 5, DispatchEnabled: false).Validate();
        new RdsOptions(true, "http://10.20.30.40:8080", 5, DispatchEnabled: false, AllowInsecureRemoteHttp: true).Validate();
    }

    private static RdsReadClient Client(RecordingHandler handler)
    {
        var options = new RdsOptions(true, "http://127.0.0.1:8080/private/path?hidden=yes", 5);
        return new RdsReadClient(new Factory(handler, options.BaseAddress), options, TimeProvider.System, NullLogger<RdsReadClient>.Instance);
    }

    private sealed class Factory(HttpMessageHandler handler, Uri? baseAddress = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private sealed class RecordingHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastLanguage { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public HttpMethod? LastMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            LastLanguage = request.Headers.TryGetValues("language", out var values) ? values.Single() : null;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(json) };
        }
    }
}
