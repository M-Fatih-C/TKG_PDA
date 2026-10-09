using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// The Operator information boundary must be enforced by the API payload itself,
/// not by the Flutter client declining to render a field. These tests assert on the
/// raw JSON so that a widget-level assertion can never again stand in for a
/// contract-level guarantee.
/// </summary>
public sealed class OperatorInformationBoundaryApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    // Raw RDS technical identifiers that must never reach a Line/Warehouse Operator
    // on the robot/queue surfaces. Robot pose (x/y/angle) belongs here.
    private static readonly string[] ForbiddenRobotFields =
    [
        "uuid", "vehicleId", "vehicle_id", "currentMap", "current_map",
        "currentStation", "current_station", "lastStation", "last_station",
        "currentOrderId", "current_order_id", "currentOrderState",
        "blockId", "block_id", "lockInfo", "lock_info", "lockNickName",
        "workSite", "work_site", "siteId", "site_id",
        "x", "y", "angle", "isLoaded", "remainingTime", "agvId", "agv_id"
    ];

    // Forbidden on the operational-context surface. Deliberately excludes x/y/width/
    // height: those are TKG layout coordinates from master.layout_slots used to draw
    // the PDA slot view, not robot pose.
    private static readonly string[] ForbiddenContextFields =
    [
        "uuid", "vehicleId", "vehicle_id", "agvId", "agv_id",
        "rdsWorkSite", "workSite", "work_site", "siteId", "site_id",
        "rdsTaskRecordId", "rdsTaskLabel", "outOrderNo", "rdsStatus",
        "currentMap", "current_map", "currentStation", "current_station",
        "blockId", "block_id", "lockInfo", "lock_info", "lockNickName", "isLoaded"
    ];

    private static void AssertNoForbiddenFields(
        JsonElement element,
        IReadOnlyList<string> forbidden,
        string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.False(
                        forbidden.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
                        $"Operator payload leaked forbidden RDS field '{property.Name}' at {path}.");
                    AssertNoForbiddenFields(property.Value, forbidden, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoForbiddenFields(item, forbidden, $"{path}[{index++}]");
                }
                break;
        }
    }

    [Fact]
    public async Task Operator_robot_payload_contains_only_business_fields()
    {
        await fixture.ResetAsync();
        // A fully populated robot: every raw technical field is present upstream.
        fixture.RdsClient.RobotAlarms = new(
            [new RdsRobotAlarmSnapshot("AGV-01", "error", "E1", "a real fault", null, 1)],
            [
                new RdsRobotStateSnapshot(
                    RobotId: "AGV-01",
                    Model: "HELIFORK",
                    Dispatchable: true,
                    UndispatchableReason: null,
                    BatteryLevel: 0.88,
                    IsCharging: false,
                    CurrentStation: "PP80",
                    LastStation: "LM12",
                    CurrentMap: "26.7.2026_map_optimak",
                    LockInfo: new RdsCoreRobotLockInfo(true, "10.0.0.5", "TKG_LOCK", 8088, "1788522696", 1, null),
                    ActiveOrderId: "ORD-999",
                    ActiveOrderState: "RUNNING",
                    RdsReportedPayloadLoaded: true,
                    Online: true,
                    Identity: new RdsRobotIdentity("ROBOT-INTERNAL-123", "AGV-01"))
            ]);

        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);
        var response = await client.GetAsync("/api/v1/rds/robots");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        AssertNoForbiddenFields(document.RootElement, ForbiddenRobotFields);

        // The specific values must be absent too, not merely renamed.
        Assert.DoesNotContain("ROBOT-INTERNAL-123", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AGV-01", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PP80", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LM12", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("26.7.2026_map_optimak", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORD-999", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TKG_LOCK", json, StringComparison.OrdinalIgnoreCase);

        // ...while the business fields the Operator UX genuinely renders survive.
        var robot = document.RootElement.GetProperty("robots").EnumerateArray().Single();
        Assert.Equal("HELIFORK", robot.GetProperty("displayName").GetString());
        Assert.True(robot.GetProperty("online").GetBoolean());
        Assert.Equal(0.88, robot.GetProperty("batteryLevel").GetDouble(), 3);
        Assert.True(robot.GetProperty("hasActiveAlarm").GetBoolean());
    }

    [Fact]
    public async Task Operator_robot_fault_flag_ignores_notices_and_warnings()
    {
        await fixture.ResetAsync();
        fixture.RdsClient.RobotAlarms = new(
            [
                new RdsRobotAlarmSnapshot("AGV-02", "notice", "80004", "no joining order", null, 1),
                new RdsRobotAlarmSnapshot("AGV-02", "warning", "W1", "a warning", null, 1)
            ],
            [new RdsRobotStateSnapshot("AGV-02", "HELIFORK", true, null, Online: true)]);

        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);
        var response = await client.GetAsync("/api/v1/rds/robots");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var robot = document.RootElement.GetProperty("robots").EnumerateArray().Single();
        Assert.False(robot.GetProperty("hasActiveAlarm").GetBoolean());
    }

    [Fact]
    public async Task Operator_queue_payload_does_not_carry_the_raw_agv_identifier()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);

        var queuePart = await fixture.PartForSlotAsync(slot, "P05-QUEUEPART");
        var payload = new CreateTransportHttpRequest(
            context.MachineId,
            context.VariantId,
            context.LayoutId,
            null,
            null,
            queuePart,
            null,
            "REMOVE_FULL");
        using var create = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
            {
                Content = JsonContent.Create(payload),
                Headers = { { ApiHeaders.IdempotencyKey, "boundary-queue-agv" } },
            });
        create.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/v1/transport-requests/queue?terminal=false");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        AssertNoForbiddenFields(document.RootElement, ForbiddenRobotFields);
    }

    // operational-context is the main PDA surface and is reachable by every PDA
    // role. It previously serialized the raw AGV id, RDS task record id, task
    // label, out-order number, numeric RDS status and the whole RdsWorkSite record
    // (SiteId, area, group, content, AGV id, filled/locked/disabled/syncFailed),
    // and it gated only TechnicalMessage, so a device-bound Supervisor or Admin still
    // received the raw TKG FailureMessage or the raw RDS EndedReason. A Supervisor
    // holding a PDA does not turn a business contract into a technical one.
    [Fact]
    public async Task Operational_context_is_business_safe_for_every_pda_role()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 1);
        await fixture.SetSlotAsync(slots[0], "OCCUPIED", "FULL");

        // Seed once as a line operator, then read the same context as each PDA role.
        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator);
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var seedPart = await fixture.PartForSlotAsync(slots[0], "P05-CTXSEED");
        var payload = new CreateTransportHttpRequest(
            context.MachineId, context.VariantId, context.LayoutId,
            null, null, seedPart, null, "REMOVE_FULL");
        using var create = await creatorClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
            {
                Content = JsonContent.Create(payload),
                Headers = { { ApiHeaders.IdempotencyKey, "ctx-boundary-seed" } },
            });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(created);

        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'SENT_TO_RDS', updated_at = now() WHERE id = @id",
            ("id", created.Id));
        // The P08A- prefix is cleaned by ResetAsync, keeping the fixture idempotent.
        var workSiteId = await fixture.ScalarAsync<Guid>(
            """
            INSERT INTO integration.rds_worksites (
                rds_site_id, area, group_name, rds_type, last_filled, last_locked,
                last_disabled, last_content, last_agv_id, last_sync_failed, last_sync_at)
            VALUES ('P08A-LEAKSITE', 'LEAKAREA', 'LEAKGROUP', 1, 0, 0, 0,
                    'leakcontent', 'LEAKAGV42', 0, now())
            RETURNING id
            """);
        await fixture.ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot_id, @worksite_id)",
            ("slot_id", slots[0]), ("worksite_id", workSiteId));
        await fixture.ExecuteAsync(
            """
            INSERT INTO integration.rds_tasks (
                transport_request_id, rds_task_record_id, rds_task_label, out_order_no,
                agv_id, rds_status, ended_reason, sent_at, last_sync_at)
            VALUES (@request_id, 'LEAKRDS77', 'LEAKLABEL', 'LEAKOUT77',
                    'LEAKAGV42', 1000, 'SECRET-RDS-ENDED-REASON-9127', now(), now())
            """,
            ("request_id", created.Id));
        // Raw technical failure text on both sides of the boundary: the TKG
        // failure_message and the RDS ended_reason. Neither may reach any PDA role.
        // A recognised failure code is set too, so the normalised business triple is
        // genuinely produced and the assertion is not vacuous.
        await fixture.ExecuteAsync(
            """
            UPDATE ops.transport_requests
               SET failure_code = '60008', failure_message = 'SECRET-RDS-FAILURE-MESSAGE-9127'
             WHERE id = @id
            """,
            ("id", created.Id));

        foreach (var role in new[] { RoleCodes.LineOperator, RoleCodes.WarehouseOperator, RoleCodes.SemiFinishedWarehouseOperator, RoleCodes.FinishedGoodsWarehouseOperator, RoleCodes.Supervisor, RoleCodes.Admin })
        {
            var reader = await fixture.SeedIdentityAsync(role: role);
            using var client = await fixture.LoginClientAsync(reader);
            var response = await client.GetAsync($"/api/v1/machines/{context.MachineId}/operational-context");
            var json = await response.Content.ReadAsStringAsync();

            using var document = JsonDocument.Parse(json);
            AssertNoForbiddenFields(document.RootElement, ForbiddenContextFields);
            if (role is RoleCodes.WarehouseOperator or RoleCodes.SemiFinishedWarehouseOperator or RoleCodes.FinishedGoodsWarehouseOperator)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(response));
                continue;
            }
            response.EnsureSuccessStatusCode();

            foreach (var leaked in new[]
                     {
                         "LEAKAGV42", "LEAKRDS77", "LEAKLABEL", "LEAKOUT77",
                         "P08A-LEAKSITE", "LEAKAREA", "LEAKGROUP", "leakcontent",
                         "SECRET-RDS-ENDED-REASON-9127", "SECRET-RDS-FAILURE-MESSAGE-9127"
                     })
            {
                Assert.False(
                    json.Contains(leaked, StringComparison.OrdinalIgnoreCase),
                    $"{role} operational-context leaked '{leaked}'.");
            }

            // The business distillation the Operator genuinely needs still arrives,
            // proving the raw text was projected away rather than simply missing.
            Assert.Contains("executionReadiness", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WORKSITE_BUSY", json, StringComparison.Ordinal);
            Assert.DoesNotContain("technicalMessage", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Operational_context_never_exposes_the_configured_rds_task_label()
    {
        // A real, non-null RDS task label must not reach any PDA role. Only the
        // derived "is a label configured" readiness signal crosses the boundary.
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.task_templates (variant_id, task_type_id, rds_task_label, priority, is_active, config_json)
            SELECT @variant_id, tt.id, 'SECRET-RDS-TASK-LABEL', 10, true, '{"testScope":"P07A"}'::jsonb
            FROM master.task_types tt WHERE tt.code = 'BRING_EMPTY'
            """,
            ("variant_id", context.VariantId));

        foreach (var role in new[] { RoleCodes.LineOperator, RoleCodes.WarehouseOperator, RoleCodes.SemiFinishedWarehouseOperator, RoleCodes.FinishedGoodsWarehouseOperator, RoleCodes.Supervisor, RoleCodes.Admin })
        {
            var identity = await fixture.SeedIdentityAsync(role: role);
            using var client = await fixture.LoginClientAsync(identity);
            var response = await client.GetAsync($"/api/v1/machines/{context.MachineId}/operational-context");
            var json = await response.Content.ReadAsStringAsync();
            if (role is RoleCodes.WarehouseOperator or RoleCodes.SemiFinishedWarehouseOperator or RoleCodes.FinishedGoodsWarehouseOperator)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(response));
            }
            else response.EnsureSuccessStatusCode();

            Assert.False(
                json.Contains("SECRET-RDS-TASK-LABEL", StringComparison.OrdinalIgnoreCase),
                $"{role} operational-context leaked the RDS task label.");
            Assert.DoesNotContain("\"rdsTaskLabel\"", json, StringComparison.OrdinalIgnoreCase);
            // The business readiness signal derived from it still arrives.
            if (response.IsSuccessStatusCode)
                Assert.Contains("rdsTaskLabelConfigured", json, StringComparison.OrdinalIgnoreCase);
            else Assert.DoesNotContain("rdsTaskLabelConfigured", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Operator_cannot_choose_physical_routing_by_injecting_slot_ids()
    {
        // A normal Operator expresses business intent; the backend resolves the
        // physical position. Even a hand-crafted body naming a real slot must not
        // control routing — the fields are not part of the contract at all.
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        var intendedSlot = slots[0];
        var injectedSlot = slots[1];
        await fixture.SetSlotAsync(intendedSlot, "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(injectedSlot, "VACANT", "UNKNOWN");
        var part = await fixture.PartForSlotAsync(intendedSlot, "P05-INJECTPART");

        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);

        // Raw JSON carrying the forbidden fields alongside the legitimate ones.
        var body = $$"""
            {
              "machineId": "{{context.MachineId}}",
              "variantId": "{{context.VariantId}}",
              "layoutId": "{{context.LayoutId}}",
              "partId": "{{part}}",
              "operation": "BRING_EMPTY",
              "sourceSlotId": "{{injectedSlot}}",
              "destinationSlotId": "{{injectedSlot}}",
              "priority": 100
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            Headers = { { ApiHeaders.IdempotencyKey, "slot-injection" } },
        };
        var response = await client.SendAsync(request);
        Assert.True(
            response.IsSuccessStatusCode,
            $"create failed {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var created = await response.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(created);

        // Routing came from the part mapping, not from the injected slot.
        Assert.Equal(intendedSlot, created.DestinationSlotId);
        Assert.NotEqual(injectedSlot, created.DestinationSlotId);
        Assert.NotEqual(injectedSlot, created.SourceSlotId);
        // The auto-allocated EMPTY_STORAGE buffer is a backend choice too.
        Assert.NotNull(created.SourceSlotId);
    }

    [Fact]
    public async Task Load_state_response_does_not_expose_the_rds_worksite_site_id()
    {
        // The content-sync result carries the raw RDS WorkSite SiteId internally.
        // This endpoint is Operator-reachable, so only the business status and
        // message may cross the boundary.
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/machines/{context.MachineId}/slots/{slot}/load-state",
            new UpdateLoadStateHttpRequest("FULL"));
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sync = document.RootElement.GetProperty("rdsContentSync");

        // Structurally only status + message exist; no site identifier is serialized.
        Assert.False(sync.TryGetProperty("siteId", out _));
        Assert.False(sync.TryGetProperty("configuredContentValue", out _));
        Assert.True(sync.TryGetProperty("status", out _));
        AssertNoForbiddenFields(document.RootElement, ForbiddenContextFields);
    }

    [Fact]
    public async Task Raw_rds_order_passthrough_is_no_longer_reachable_by_an_operator()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync();
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync("/api/v1/rds/orders");

        // The unfiltered fleet-wide RDSCore passthrough has been removed entirely.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
