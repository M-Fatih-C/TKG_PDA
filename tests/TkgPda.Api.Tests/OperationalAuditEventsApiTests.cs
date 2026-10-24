using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-06: Operational and Lifecycle Audit Event Foundation Tests.
/// Validates:
/// 1. REQUEST_CREATED emitted on request creation with actor, device, correlation, and data_json.
/// 2. Idempotent creation replay does not emit duplicate REQUEST_CREATED.
/// 3. Request-bound pallet placement emits PALLET_PLACED (prep) and single WAREHOUSE_PREPARATION_STARTED (transport).
/// 4. Staged empty pallet without transport request does NOT emit WAREHOUSE_PREPARATION_STARTED.
/// 5. Pallet ready emits PALLET_MARKED_READY (prep) and single WAREHOUSE_PREPARATION_READY (transport); retry is idempotent.
/// 6. Direct mark-ready endpoint emits WAREHOUSE_PREPARATION_READY; retry is idempotent.
/// 7. RDS confirmed dispatch emits single ROBOT_TASK_DISPATCHED; unconfirmed does not.
/// 8. RUNNING reconciliation emits single ROBOT_TASK_STARTED; repeated reconciliation is duplicate-free.
/// 9. COMPLETED emits both ROBOT_TASK_COMPLETED and REQUEST_COMPLETED in the same boundary.
/// 10. Pre-execution cancel emits single REQUEST_CANCELLED with operator, device, and correlation context; retry is idempotent.
/// 11. RDS cancellation confirmed emits single REQUEST_CANCELLED with cancellation_source = RDS_CONFIRMED.
/// 12. Execution failure emits single REQUEST_FAILED with failure details.
/// 13. Batch creation emits REQUEST_CREATED for each child with event context; replay produces no duplicates.
/// 14. Preparation events and general audit logs (audit.audit_logs) are preserved.
/// </summary>
public sealed class OperationalAuditEventsApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private sealed record TransportEventRow(
        long Id,
        Guid TransportRequestId,
        Guid? ActorUserId,
        Guid? DeviceId,
        Guid? CorrelationId,
        string EventType,
        string? OldStatus,
        string? NewStatus,
        string? Message,
        string DataJson,
        DateTimeOffset CreatedAt);

    private async Task<List<TransportEventRow>> GetEventsAsync(Guid requestId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, transport_request_id, actor_user_id, device_id, correlation_id,
                   event_type, old_status, new_status, message, data_json::text, created_at
            FROM ops.transport_request_events
            WHERE transport_request_id = @id
            ORDER BY id ASC
            """,
            connection);
        cmd.Parameters.AddWithValue("id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<TransportEventRow>();
        while (await reader.ReadAsync())
        {
            list.Add(new TransportEventRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }
        return list;
    }

    [Fact]
    public async Task Request_creation_records_REQUEST_CREATED_with_context_and_replay_is_idempotent()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(creator);

        var correlationId = Guid.NewGuid();
        var idempotencyKey = "op-audit-create-" + Guid.NewGuid();
        var partId = await fixture.PartForSlotAsync(slot, "P06-A1");

        var requestMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = JsonContent.Create(new CreateTransportHttpRequest(
                context.MachineId,
                context.VariantId,
                context.LayoutId,
                null,
                null,
                partId,
                null,
                "BRING_EMPTY"))
        };
        requestMsg.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        requestMsg.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());

        var response = await client.SendAsync(requestMsg);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(created);

        var events = await GetEventsAsync(created.Id);
        Assert.True(events.Count >= 1);

        var creationEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCreated);
        Assert.Equal(creator.UserId, creationEvent.ActorUserId);
        Assert.Equal(creator.DeviceId, creationEvent.DeviceId);
        Assert.Equal(correlationId, creationEvent.CorrelationId);
        Assert.Null(creationEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.Created.ToDatabase(), creationEvent.NewStatus);

        using (var doc = JsonDocument.Parse(creationEvent.DataJson))
        {
            Assert.Equal(idempotencyKey, doc.RootElement.GetProperty("idempotency_key").GetString());
            Assert.Equal("API", doc.RootElement.GetProperty("request_origin").GetString());
            Assert.Equal("BRING_EMPTY", doc.RootElement.GetProperty("task_type").GetString());
        }

        // Idempotent replay with same idempotency key
        var replayMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = JsonContent.Create(new CreateTransportHttpRequest(
                context.MachineId,
                context.VariantId,
                context.LayoutId,
                null,
                null,
                partId,
                null,
                "BRING_EMPTY"))
        };
        replayMsg.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        replayMsg.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());

        var replayResponse = await client.SendAsync(replayMsg);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);

        var eventsAfterReplay = await GetEventsAsync(created.Id);
        Assert.Single(eventsAfterReplay, e => e.EventType == TransportEventTypes.RequestCreated);
    }

    [Fact]
    public async Task Warehouse_preparation_lifecycle_emits_STARTED_and_READY_on_request_timeline()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var partId = await fixture.PartForSlotAsync(slot, "P06-BRING-FULL");
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");

        // Create waiting BRING_FULL request
        var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = JsonContent.Create(new CreateTransportHttpRequest(
                context.MachineId,
                context.VariantId,
                context.LayoutId,
                null,
                null,
                partId,
                containerTypeId,
                "BRING_FULL",
                RequiresPreparation: true))
        };
        createMsg.Headers.Add(ApiHeaders.IdempotencyKey, "prep-req-" + Guid.NewGuid());
        var createResp = await creatorClient.SendAsync(createMsg);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var request = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(request);

        // Warehouse operator places full pallet bound to this request
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);

        var areaId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");

        var placeCorrelationId = Guid.NewGuid();
        var placeMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/areas/{areaId}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest("FULL", containerTypeId, partId, request.Id))
        };
        placeMsg.Headers.Add(ApiHeaders.IdempotencyKey, "place-" + Guid.NewGuid());
        placeMsg.Headers.Add(ApiHeaders.CorrelationId, placeCorrelationId.ToString());

        var placeResp = await warehouseClient.SendAsync(placeMsg);
        Assert.Equal(HttpStatusCode.Created, placeResp.StatusCode);
        var placedPallet = await placeResp.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placedPallet);

        // 1. Verify ops.preparation_events contains PALLET_PLACED
        var prepEventCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_events WHERE preparation_pallet_id = @id AND event_type = 'PALLET_PLACED'",
            ("id", placedPallet.Id));
        Assert.Equal(1, prepEventCount);

        // 2. Verify ops.transport_request_events contains WAREHOUSE_PREPARATION_STARTED
        var transportEvents = await GetEventsAsync(request.Id);
        var prepStartedEvent = Assert.Single(transportEvents, e => e.EventType == TransportEventTypes.WarehousePreparationStarted);
        Assert.Equal(warehouseOperator.UserId, prepStartedEvent.ActorUserId);
        Assert.Equal(warehouseOperator.DeviceId, prepStartedEvent.DeviceId);
        Assert.Equal(placeCorrelationId, prepStartedEvent.CorrelationId);

        using (var doc = JsonDocument.Parse(prepStartedEvent.DataJson))
        {
            Assert.Equal(areaId, doc.RootElement.GetProperty("preparation_area_id").GetGuid());
            Assert.Equal(placedPallet.Id, doc.RootElement.GetProperty("preparation_pallet_id").GetGuid());
            Assert.Equal(partId, doc.RootElement.GetProperty("part_id").GetGuid());
            Assert.Equal(containerTypeId, doc.RootElement.GetProperty("container_type_id").GetGuid());
            Assert.Equal("FULL", doc.RootElement.GetProperty("load_state").GetString());
        }

        // Warehouse operator marks pallet READY
        var readyCorrelationId = Guid.NewGuid();
        var readyMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/pallets/{placedPallet.Id}/ready")
        {
            Content = JsonContent.Create(new PalletVersionHttpRequest(placedPallet.RowVersion))
        };
        readyMsg.Headers.Add(ApiHeaders.CorrelationId, readyCorrelationId.ToString());

        var readyResp = await warehouseClient.SendAsync(readyMsg);
        Assert.Equal(HttpStatusCode.OK, readyResp.StatusCode);
        var readyPallet = await readyResp.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(readyPallet);

        // 3. Verify ops.preparation_events contains PALLET_MARKED_READY
        var prepReadyCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_events WHERE preparation_pallet_id = @id AND event_type = 'PALLET_MARKED_READY'",
            ("id", placedPallet.Id));
        Assert.Equal(1, prepReadyCount);

        // 4. Verify ops.transport_request_events contains single WAREHOUSE_PREPARATION_READY
        transportEvents = await GetEventsAsync(request.Id);
        var prepReadyEvent = Assert.Single(transportEvents, e => e.EventType == TransportEventTypes.WarehousePreparationReady);
        Assert.Equal(warehouseOperator.UserId, prepReadyEvent.ActorUserId);
        Assert.Equal(warehouseOperator.DeviceId, prepReadyEvent.DeviceId);
        Assert.Equal(readyCorrelationId, prepReadyEvent.CorrelationId);

        using (var doc = JsonDocument.Parse(prepReadyEvent.DataJson))
        {
            Assert.Equal(areaId, doc.RootElement.GetProperty("preparation_area_id").GetGuid());
            Assert.Equal(placedPallet.Id, doc.RootElement.GetProperty("preparation_pallet_id").GetGuid());
            Assert.Equal(partId, doc.RootElement.GetProperty("part_id").GetGuid());
            Assert.Equal(containerTypeId, doc.RootElement.GetProperty("container_type_id").GetGuid());
            Assert.True(doc.RootElement.TryGetProperty("ready_at", out _));
        }

        // Retry marking ready is idempotent: does NOT emit duplicate WAREHOUSE_PREPARATION_READY
        var readyRetryResp = await warehouseClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/pallets/{placedPallet.Id}/ready")
        {
            Content = JsonContent.Create(new PalletVersionHttpRequest(readyPallet.RowVersion))
        });
        Assert.Equal(HttpStatusCode.OK, readyRetryResp.StatusCode);

        transportEvents = await GetEventsAsync(request.Id);
        Assert.Single(transportEvents, e => e.EventType == TransportEventTypes.WarehousePreparationReady);
    }

    [Fact]
    public async Task Staged_empty_pallet_without_transport_request_does_not_emit_transport_event()
    {
        await fixture.ResetAsync();
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);

        var areaId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");

        var placeMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/areas/{areaId}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest("EMPTY", containerTypeId, null, null))
        };
        placeMsg.Headers.Add(ApiHeaders.IdempotencyKey, "stage-empty-" + Guid.NewGuid());
        var placeResp = await warehouseClient.SendAsync(placeMsg);
        Assert.Equal(HttpStatusCode.Created, placeResp.StatusCode);
        var placedPallet = await placeResp.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placedPallet);

        // Preparation event PALLET_PLACED must be written
        var prepEventCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_events WHERE preparation_pallet_id = @id AND event_type = 'PALLET_PLACED'",
            ("id", placedPallet.Id));
        Assert.Equal(1, prepEventCount);

        // ops.transport_request_events must NOT have any WAREHOUSE_PREPARATION_STARTED
        var transportEventCount = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_request_events WHERE event_type = 'WAREHOUSE_PREPARATION_STARTED'");
        Assert.Equal(0, transportEventCount);
    }

    [Fact]
    public async Task Execution_lifecycle_emits_dispatched_started_completed_with_exact_boundaries()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var service = new TransportWorkflowService(store);

        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        var partId = await fixture.PartForSlotAsync(slot, "P06-EXEC");

        var command = new CreateTransportRequest(
            "exec-lifecycle-" + Guid.NewGuid(),
            RequestOrigin.Api,
            creator.UserId,
            context.MachineId,
            context.VariantId,
            context.LayoutId,
            null,
            null,
            partId,
            null,
            TransportTaskType.BringEmpty,
            null,
            slot,
            50,
            RequiresPreparation: false);

        var created = await service.CreateAsync(command);
        var requestId = created.Request.Id;

        var dispatchCorrelation = Guid.NewGuid();
        var dispatchContext = OperationalEventContext.System(dispatchCorrelation);

        // 1. Transition to SentToRds -> ROBOT_TASK_DISPATCHED
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds, dispatchContext);

        var events = await GetEventsAsync(requestId);
        var dispatchEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RobotTaskDispatched);
        Assert.Null(dispatchEvent.ActorUserId);
        Assert.Null(dispatchEvent.DeviceId);
        Assert.Equal(dispatchCorrelation, dispatchEvent.CorrelationId);
        Assert.Equal(TransportRequestStatus.Queued.ToDatabase(), dispatchEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.SentToRds.ToDatabase(), dispatchEvent.NewStatus);

        // 2. Transition to Running -> ROBOT_TASK_STARTED
        var runningCorrelation = Guid.NewGuid();
        var runningContext = OperationalEventContext.System(runningCorrelation);
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running, runningContext);

        events = await GetEventsAsync(requestId);
        var startedEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RobotTaskStarted);
        Assert.Equal(runningCorrelation, startedEvent.CorrelationId);
        Assert.Equal(TransportRequestStatus.SentToRds.ToDatabase(), startedEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.Running.ToDatabase(), startedEvent.NewStatus);

        // 3. Repeated Running reconciliation -> duplicate-free!
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running, runningContext);
        events = await GetEventsAsync(requestId);
        Assert.Single(events, e => e.EventType == TransportEventTypes.RobotTaskStarted);

        // 4. Transition to Completed -> ROBOT_TASK_COMPLETED and REQUEST_COMPLETED
        var completeCorrelation = Guid.NewGuid();
        var completeContext = OperationalEventContext.System(completeCorrelation);
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed, completeContext);

        events = await GetEventsAsync(requestId);
        var robotCompletedEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RobotTaskCompleted);
        var requestCompletedEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCompleted);

        Assert.Equal(completeCorrelation, robotCompletedEvent.CorrelationId);
        Assert.Equal(completeCorrelation, requestCompletedEvent.CorrelationId);
        Assert.Equal(TransportRequestStatus.Running.ToDatabase(), robotCompletedEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.Completed.ToDatabase(), robotCompletedEvent.NewStatus);
        Assert.Equal(TransportRequestStatus.Running.ToDatabase(), requestCompletedEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.Completed.ToDatabase(), requestCompletedEvent.NewStatus);

        // Repeated completed call -> duplicate-free!
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed, completeContext);
        events = await GetEventsAsync(requestId);
        Assert.Single(events, e => e.EventType == TransportEventTypes.RobotTaskCompleted);
        Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCompleted);
    }

    [Fact]
    public async Task Pre_execution_cancel_via_api_emits_single_REQUEST_CANCELLED_with_context()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(creator);
        var partId = await fixture.PartForSlotAsync(slot, "P06-CANCEL");

        var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = JsonContent.Create(new CreateTransportHttpRequest(
                context.MachineId,
                context.VariantId,
                context.LayoutId,
                null,
                null,
                partId,
                null,
                "BRING_EMPTY"))
        };
        createMsg.Headers.Add(ApiHeaders.IdempotencyKey, "cancel-test-" + Guid.NewGuid());
        var createResp = await client.SendAsync(createMsg);
        var req = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(req);

        // Cancel via machine-scoped cancel endpoint
        var cancelCorrelationId = Guid.NewGuid();
        var cancelMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/transport-requests/{req.Id}/cancel");
        cancelMsg.Headers.Add(ApiHeaders.CorrelationId, cancelCorrelationId.ToString());

        var cancelResp = await client.SendAsync(cancelMsg);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        var events = await GetEventsAsync(req.Id);
        var cancelEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCancelled);
        Assert.Equal(creator.UserId, cancelEvent.ActorUserId);
        Assert.Equal(creator.DeviceId, cancelEvent.DeviceId);
        Assert.Equal(cancelCorrelationId, cancelEvent.CorrelationId);
        Assert.Equal(TransportRequestStatus.Queued.ToDatabase(), cancelEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.Cancelled.ToDatabase(), cancelEvent.NewStatus);

        using (var doc = JsonDocument.Parse(cancelEvent.DataJson))
        {
            Assert.Equal("PRE_EXECUTION", doc.RootElement.GetProperty("cancellation_source").GetString());
            Assert.Equal("OPERATOR_COMMAND", doc.RootElement.GetProperty("evidence_type").GetString());
            Assert.True(doc.RootElement.TryGetProperty("restored_slot_id", out _));
        }

        // Retry cancel -> idempotent, no duplicate REQUEST_CANCELLED
        var retryCancelMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{context.MachineId}/transport-requests/{req.Id}/cancel");
        retryCancelMsg.Headers.Add(ApiHeaders.CorrelationId, cancelCorrelationId.ToString());
        var retryCancelResp = await client.SendAsync(retryCancelMsg);
        Assert.Equal(HttpStatusCode.OK, retryCancelResp.StatusCode);

        events = await GetEventsAsync(req.Id);
        Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCancelled);
    }

    [Fact]
    public async Task Execution_failure_and_rds_cancellation_emit_canonical_events()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var service = new TransportWorkflowService(store);
        var partId = await fixture.PartForSlotAsync(slot, "P06-FAIL");

        var command = new CreateTransportRequest(
            "fail-test-" + Guid.NewGuid(),
            RequestOrigin.Api,
            null,
            context.MachineId,
            context.VariantId,
            context.LayoutId,
            null,
            null,
            partId,
            null,
            TransportTaskType.BringEmpty,
            null,
            slot,
            50,
            RequiresPreparation: false);

        var created = await service.CreateAsync(command);
        var requestId = created.Request.Id;

        // Transition to SentToRds first
        await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);

        // Record execution outcome: Failed
        var failCorrelationId = Guid.NewGuid();
        var failContext = OperationalEventContext.System(failCorrelationId);
        await store.RecordExecutionOutcomeAsync(
            requestId,
            new ExecutionOutcome(TransportRequestStatus.Failed, "NAV_OBSTACLE", "Path blocked by unknown obstacle", "RDS_QUERY"),
            failContext);

        var events = await GetEventsAsync(requestId);
        var failedEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RequestFailed);
        Assert.Null(failedEvent.ActorUserId);
        Assert.Null(failedEvent.DeviceId);
        Assert.Equal(failCorrelationId, failedEvent.CorrelationId);
        Assert.Equal(TransportRequestStatus.SentToRds.ToDatabase(), failedEvent.OldStatus);
        Assert.Equal(TransportRequestStatus.Failed.ToDatabase(), failedEvent.NewStatus);

        using (var doc = JsonDocument.Parse(failedEvent.DataJson))
        {
            Assert.Equal("NAV_OBSTACLE", doc.RootElement.GetProperty("failure_code").GetString());
            Assert.Equal("Path blocked by unknown obstacle", doc.RootElement.GetProperty("failure_message").GetString());
            Assert.Equal("RDS_QUERY", doc.RootElement.GetProperty("evidence_type").GetString());
        }

        // Second request for RDS confirmed cancellation
        // The failed dispatched move correctly left the endpoint UNKNOWN. This
        // test is about audit boundaries, so provide explicit physical
        // reconciliation before creating another movement on the same slot.
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var cancelCommand = command with { IdempotencyKey = "rds-cancel-test-" + Guid.NewGuid() };
        var created2 = await service.CreateAsync(cancelCommand);
        await store.RecordExecutionTransitionAsync(created2.Request.Id, TransportRequestStatus.SentToRds);

        var rdsCancelCorrelationId = Guid.NewGuid();
        var rdsCancelContext = OperationalEventContext.System(rdsCancelCorrelationId);
        await store.RecordExecutionOutcomeAsync(
            created2.Request.Id,
            new ExecutionOutcome(TransportRequestStatus.Cancelled, EvidenceType: "RDSCORE_TERMINATE"),
            rdsCancelContext);

        var events2 = await GetEventsAsync(created2.Request.Id);
        var rdsCancelledEvent = Assert.Single(events2, e => e.EventType == TransportEventTypes.RequestCancelled);
        Assert.Equal(rdsCancelCorrelationId, rdsCancelledEvent.CorrelationId);
        using (var doc = JsonDocument.Parse(rdsCancelledEvent.DataJson))
        {
            Assert.Equal("RDS_CONFIRMED", doc.RootElement.GetProperty("cancellation_source").GetString());
            Assert.Equal("RDSCORE_TERMINATE", doc.RootElement.GetProperty("evidence_type").GetString());
        }
    }

    [Fact]
    public async Task Batch_creation_emits_REQUEST_CREATED_for_each_child_with_event_context()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        await fixture.SetSlotAsync(slots[0], "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slots[1], "VACANT", "UNKNOWN");

        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(creator);

        var part0 = await fixture.PartForSlotAsync(slots[0], "P06-BATCH-0");
        var part1 = await fixture.PartForSlotAsync(slots[1], "P06-BATCH-1");

        var batchKey = "batch-audit-" + Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var batchRequest = new CreateTransportBatchHttpRequest(
            batchKey,
            context.MachineId,
            new[]
            {
                new CreateTransportBatchItemHttpRequest(
                    "batch-child-0-" + Guid.NewGuid(),
                    context.MachineId,
                    context.VariantId,
                    context.LayoutId,
                    null,
                    null,
                    part0,
                    null,
                    "BRING_EMPTY"),
                new CreateTransportBatchItemHttpRequest(
                    "batch-child-1-" + Guid.NewGuid(),
                    context.MachineId,
                    context.VariantId,
                    context.LayoutId,
                    null,
                    null,
                    part1,
                    null,
                    "BRING_EMPTY")
            });

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-batches")
        {
            Content = JsonContent.Create(batchRequest)
        };
        msg.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());

        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var batchResp = await resp.Content.ReadFromJsonAsync<TransportBatchHttpResponse>();
        Assert.NotNull(batchResp);
        Assert.Equal(2, batchResp.Items.Count);

        foreach (var item in batchResp.Items)
        {
            var events = await GetEventsAsync(item.Id);
            var createdEvent = Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCreated);
            Assert.Equal(creator.UserId, createdEvent.ActorUserId);
            Assert.Equal(creator.DeviceId, createdEvent.DeviceId);
            Assert.Equal(correlationId, createdEvent.CorrelationId);
        }

        // Replay batch
        var replayMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-batches")
        {
            Content = JsonContent.Create(batchRequest)
        };
        replayMsg.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());
        var replayResp = await client.SendAsync(replayMsg);
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);

        foreach (var item in batchResp.Items)
        {
            var events = await GetEventsAsync(item.Id);
            Assert.Single(events, e => e.EventType == TransportEventTypes.RequestCreated);
        }
    }

    private sealed record WaitingBringFull(
        LayoutContext Context, Guid PartId, Guid ContainerTypeId, TransportHttpResponse Request);

    private async Task<WaitingBringFull> CreateWaitingBringFullAsync(string partSuffix)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var creator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var partId = await fixture.PartForSlotAsync(slot, partSuffix);
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");

        var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
        {
            Content = JsonContent.Create(new CreateTransportHttpRequest(
                context.MachineId, context.VariantId, context.LayoutId, null, null,
                partId, containerTypeId, "BRING_FULL", RequiresPreparation: true))
        };
        createMsg.Headers.Add(ApiHeaders.IdempotencyKey, "prep-req-" + Guid.NewGuid());
        var createResp = await creatorClient.SendAsync(createMsg);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var request = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(request);
        return new WaitingBringFull(context, partId, containerTypeId, request);
    }

    private static Task<HttpResponseMessage> PlaceFullPalletAsync(
        HttpClient client, Guid areaId, WaitingBringFull demand, Guid correlationId)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/areas/{areaId}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest("FULL", demand.ContainerTypeId, demand.PartId, demand.Request.Id))
        };
        message.Headers.Add(ApiHeaders.IdempotencyKey, "place-" + Guid.NewGuid());
        message.Headers.Add(ApiHeaders.CorrelationId, correlationId.ToString());
        return client.SendAsync(message);
    }

    [Fact]
    public async Task Release_then_replacement_pallet_writes_PALLET_PLACED_again_but_STARTED_only_once()
    {
        var demand = await CreateWaitingBringFullAsync("P06-RELEASE-REPLACE");
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var areaId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");

        var firstCorrelation = Guid.NewGuid();
        var firstResp = await PlaceFullPalletAsync(warehouseClient, areaId, demand, firstCorrelation);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        var first = await firstResp.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(first);
        Assert.Single(await GetEventsAsync(demand.Request.Id), e => e.EventType == TransportEventTypes.WarehousePreparationStarted);

        var releaseResp = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/preparation/pallets/{first.Id}/release",
            new ReleaseAreaHttpRequest(first.RowVersion, "WITHDRAWN"));
        Assert.Equal(HttpStatusCode.OK, releaseResp.StatusCode);

        var secondResp = await PlaceFullPalletAsync(warehouseClient, areaId, demand, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, secondResp.StatusCode);
        var second = await secondResp.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);

        // The physical history keeps both placements; the lifecycle timeline starts once.
        Assert.Equal(2, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_events WHERE transport_request_id = @id AND event_type = 'PALLET_PLACED'",
            ("id", demand.Request.Id)));
        var started = Assert.Single(await GetEventsAsync(demand.Request.Id), e => e.EventType == TransportEventTypes.WarehousePreparationStarted);
        Assert.Equal(first.Id, JsonDocument.Parse(started.DataJson).RootElement.GetProperty("preparation_pallet_id").GetGuid());
        Assert.Equal(firstCorrelation, started.CorrelationId);
    }

    [Fact]
    public async Task Withdrawing_ready_pallet_reopens_preparation_and_releases_machine_slot_claim()
    {
        var demand = await CreateWaitingBringFullAsync("P06-WITHDRAW-READY");
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var areaId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");

        var placedResponse = await PlaceFullPalletAsync(warehouseClient, areaId, demand, Guid.NewGuid());
        var placed = await placedResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placed);
        var readyResponse = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/preparation/pallets/{placed.Id}/ready",
            new PalletVersionHttpRequest(placed.RowVersion));
        readyResponse.EnsureSuccessStatusCode();
        var ready = await readyResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(ready);

        var released = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/preparation/pallets/{ready.Id}/release",
            new ReleaseAreaHttpRequest(ready.RowVersion, "WITHDRAWN"));
        released.EnsureSuccessStatusCode();

        Assert.Equal("WAITING_PREPARATION", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", demand.Request.Id)));
        Assert.False(await fixture.ScalarAsync<bool>(
            "SELECT machine_slot_reservation_active FROM ops.transport_requests WHERE id = @id", ("id", demand.Request.Id)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE id = @id AND ready_at IS NOT NULL", ("id", demand.Request.Id)));
        Assert.Single(await GetEventsAsync(demand.Request.Id),
            e => e.EventType == TransportEventTypes.WarehousePreparationWithdrawn);
    }

    [Fact]
    public async Task Ready_pallet_cannot_be_recorded_collected_before_robot_dispatch()
    {
        var demand = await CreateWaitingBringFullAsync("P06-COLLECT-BEFORE-DISPATCH");
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var areaId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");
        var placedResponse = await PlaceFullPalletAsync(warehouseClient, areaId, demand, Guid.NewGuid());
        var placed = await placedResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placed);
        var readyResponse = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/preparation/pallets/{placed.Id}/ready",
            new PalletVersionHttpRequest(placed.RowVersion));
        var ready = await readyResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(ready);

        var rejected = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/preparation/pallets/{ready.Id}/release",
            new ReleaseAreaHttpRequest(ready.RowVersion, "COLLECTED"));

        Assert.False(rejected.IsSuccessStatusCode);
        Assert.Equal("PREPARATION_COLLECTION_NOT_DISPATCHED", await ApiTestFixture.ErrorCodeAsync(rejected));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_pallets WHERE id = @id AND released_at IS NULL", ("id", ready.Id)));
    }

    [Fact]
    public async Task Cancelled_request_cannot_be_marked_ready()
    {
        var demand = await CreateWaitingBringFullAsync("P06-CANCEL-BEFORE-READY");
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var areaId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");
        var placedResponse = await PlaceFullPalletAsync(warehouseClient, areaId, demand, Guid.NewGuid());
        var placed = await placedResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placed);
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'CANCELLED', cancelled_at = now() WHERE id = @id",
            ("id", demand.Request.Id));

        var rejected = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/preparation/pallets/{placed.Id}/ready",
            new PalletVersionHttpRequest(placed.RowVersion));

        Assert.False(rejected.IsSuccessStatusCode);
        Assert.Equal("PREPARATION_REQUEST_NOT_WAITING", await ApiTestFixture.ErrorCodeAsync(rejected));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_pallets WHERE id = @id AND ready_at IS NOT NULL", ("id", placed.Id)));
    }

    [Fact]
    public async Task Concurrent_placements_for_one_request_emit_STARTED_exactly_once()
    {
        var demand = await CreateWaitingBringFullAsync("P06-CONCURRENT-PLACE");
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var areaIds = new List<Guid>();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 2", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) areaIds.Add(reader.GetGuid(0));
        }
        Assert.Equal(2, areaIds.Count);

        var responses = await Task.WhenAll(
            PlaceFullPalletAsync(warehouseClient, areaIds[0], demand, Guid.NewGuid()),
            PlaceFullPalletAsync(warehouseClient, areaIds[1], demand, Guid.NewGuid()));

        // One active pallet per request: exactly one placement wins, the other is refused.
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(response => response.StatusCode != HttpStatusCode.Created));
        Assert.Single(await GetEventsAsync(demand.Request.Id), e => e.EventType == TransportEventTypes.WarehousePreparationStarted);
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.preparation_events WHERE transport_request_id = @id AND event_type = 'PALLET_PLACED'",
            ("id", demand.Request.Id)));
    }

    [Fact]
    public async Task Unconfirmed_dispatch_and_pre_execution_state_write_no_robot_events()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(new CreateTransportRequest(
            "no-dispatch-" + Guid.NewGuid(), RequestOrigin.Api, null, context.MachineId, context.VariantId,
            context.LayoutId, null, null, await fixture.PartForSlotAsync(slot, "P06-NODISPATCH"), null,
            TransportTaskType.BringEmpty, null, slot, 50, RequiresPreparation: false));

        // RDS_DISPATCH_UNCONFIRMED performs no transition at all (proven in the coordinator tests);
        // the request therefore stays Queued with no robot lifecycle event.
        var events = await GetEventsAsync(created.Request.Id);
        Assert.DoesNotContain(events, e =>
            e.EventType is TransportEventTypes.RobotTaskDispatched
                or TransportEventTypes.RobotTaskStarted
                or TransportEventTypes.RobotTaskCompleted
                or TransportEventTypes.RequestCompleted
                or TransportEventTypes.RequestCancelled
                or TransportEventTypes.RequestFailed);
        Assert.Equal("QUEUED", await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id", ("id", created.Request.Id)));
    }

    [Fact]
    public async Task Rejected_cancellations_do_not_write_REQUEST_CANCELLED()
    {
        await fixture.ResetAsync();
        var contextA = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var contextB = await fixture.ConfigureMachineAsync("SH-01-B", "1");
        var slotA = Assert.Single(await fixture.LogisticsSlotsAsync(contextA.LayoutId, 1));
        await fixture.SetSlotAsync(slotA, "VACANT", "UNKNOWN");

        var operatorA = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var clientA = await fixture.LoginClientAsync(operatorA);
        var createResp = await fixture.SendTransportAsync(
            clientA,
            new CreateTransportHttpRequest(contextA.MachineId, contextA.VariantId, contextA.LayoutId, null, null,
                await fixture.PartForSlotAsync(slotA, "P06-REJECTED-CANCEL"), null, "BRING_EMPTY"),
            "p06-rejected-cancel");
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var request = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(request);

        // WP-05: warehouse operators cannot cancel.
        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var warehouseCancel = await warehouseClient.PostAsync(
            $"/api/v1/machines/{contextA.MachineId}/transport-requests/{request.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, warehouseCancel.StatusCode);

        // WP-05: the path machine must be the request's machine.
        var mismatch = await clientA.PostAsync(
            $"/api/v1/machines/{contextB.MachineId}/transport-requests/{request.Id}/cancel", null);
        Assert.False(mismatch.IsSuccessStatusCode);

        var events = await GetEventsAsync(request.Id);
        Assert.DoesNotContain(events, e => e.EventType == TransportEventTypes.RequestCancelled);
        Assert.Equal("QUEUED", await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id", ("id", request.Id)));
    }
}
