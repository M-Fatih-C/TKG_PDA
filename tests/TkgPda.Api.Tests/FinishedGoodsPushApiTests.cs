using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-09 push registration, the completion-only outbox, and the worker.
/// The sender is a fake. No Firebase credentials are used.
/// </summary>
public sealed class FinishedGoodsPushApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Registration_belongs_to_the_authenticated_user_and_device()
    {
        await fixture.ResetAsync();
        var identity = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.PutAsJsonAsync("/api/v1/auth/push-registration", new
        {
            provider = "FCM",
            platform = "ANDROID",
            token = "wp09-token-a",
            userId = Guid.NewGuid(),
            deviceId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(identity.UserId, await fixture.ScalarAsync<Guid>(
            "SELECT user_id FROM integration.device_push_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
        Assert.Equal(identity.DeviceId, await fixture.ScalarAsync<Guid>(
            "SELECT device_id FROM integration.device_push_registrations WHERE user_id = @user",
            ("user", identity.UserId)));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.device_push_registrations"));
    }

    [Fact]
    public async Task Unsupported_platform_is_rejected()
    {
        await fixture.ResetAsync();
        var identity = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.PutAsJsonAsync("/api/v1/auth/push-registration", new
        {
            provider = "FCM",
            platform = "WEB",
            token = "wp09-token-web"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PUSH_PROVIDER_UNSUPPORTED", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.device_push_registrations"));
    }

    [Fact]
    public async Task Token_refresh_updates_the_same_device_registration()
    {
        await fixture.ResetAsync();
        var identity = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);
        await RegisterAsync(client, "wp09-token-old");
        await RegisterAsync(client, "wp09-token-new");

        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.device_push_registrations WHERE is_active"));
        Assert.Equal("wp09-token-new", await fixture.ScalarAsync<string>(
            "SELECT token FROM integration.device_push_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
    }

    [Fact]
    public async Task Explicit_logout_deactivates_the_registration()
    {
        await fixture.ResetAsync();
        var identity = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);
        await RegisterAsync(client, "wp09-token-logout");

        var logout = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.False(await fixture.ScalarAsync<bool>(
            "SELECT is_active FROM integration.device_push_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT revoked_at IS NOT NULL FROM integration.device_push_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
    }

    [Fact]
    public async Task Lease_expiry_leaves_the_registration_active()
    {
        await fixture.ResetAsync();
        var identity = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);
        await RegisterAsync(client, "wp09-token-lease");
        await fixture.ExecuteAsync(
            "UPDATE auth.user_sessions SET lease_expires_at = clock_timestamp() - interval '1 minute'");

        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.True(await fixture.ScalarAsync<bool>(
            "SELECT is_active FROM integration.device_push_registrations WHERE device_id = @device",
            ("device", identity.DeviceId)));
    }

    [Fact]
    public async Task Delivery_push_is_created_only_when_remove_full_completes_and_replay_does_not_duplicate_it()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        var legacy = await WarehouseAsync(RoleCodes.WarehouseOperator);
        var admin = await WarehouseAsync(RoleCodes.Admin);
        var supervisor = await WarehouseAsync(RoleCodes.Supervisor);
        var semi = await WarehouseAsync(RoleCodes.SemiFinishedWarehouseOperator);
        var lineOnly = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var finishedClient = await fixture.LoginClientAsync(finished);
        using var legacyClient = await fixture.LoginClientAsync(legacy);
        using var adminClient = await fixture.LoginClientAsync(admin);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);
        using var semiClient = await fixture.LoginClientAsync(semi);
        using var lineClient = await fixture.LoginClientAsync(lineOnly);
        await RegisterAsync(finishedClient, "wp09-fg");
        await RegisterAsync(legacyClient, "wp09-legacy");
        await RegisterAsync(adminClient, "wp09-admin");
        await RegisterAsync(supervisorClient, "wp09-supervisor");
        await RegisterAsync(semiClient, "wp09-semi");
        await RegisterAsync(lineClient, "wp09-line");

        var mapped = await SeedMappedPositionAsync(context, "PUSH");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        var created = await SendActionAsync(lineClient, context, mapped, "FULL", "wp09-remove-full");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = await RequestIdAsync("wp09-remove-full");
        Assert.Equal(0L, await OutboxCountAsync());

        var workflow = fixture.Services.GetRequiredService<TransportWorkflowService>();
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        Assert.Equal(0L, await OutboxCountAsync());
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        Assert.Equal(0L, await OutboxCountAsync());
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);

        Assert.Equal(4L, await OutboxCountAsync());
        var unitId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.store_units WHERE transport_request_id = @request",
            ("request", requestId));
        Assert.Equal(FinishedGoodsPushCopy.EventKey(unitId), await fixture.ScalarAsync<string>(
            "SELECT event_key FROM integration.push_outbox LIMIT 1"));
        var title = await fixture.ScalarAsync<string>("SELECT title FROM integration.push_outbox LIMIT 1");
        var body = await fixture.ScalarAsync<string>("SELECT body FROM integration.push_outbox LIMIT 1");
        Assert.StartsWith("Kasa Koyuldu — ", title);
        Assert.Contains("SH-01-A", body);
        Assert.Contains("Talep #", body);
        Assert.DoesNotContain(unitId.ToString(), title);
        Assert.DoesNotContain(unitId.ToString(), body);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM integration.push_outbox o
            JOIN integration.device_push_registrations r ON r.id = o.registration_id
            WHERE r.user_id IN (@semi, @line)
            """,
            ("semi", semi.UserId),
            ("line", lineOnly.UserId)));

        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
        Assert.Equal(4L, await OutboxCountAsync());
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_events WHERE event_type = 'UNIT_DELIVERED'"));
    }

    [Fact]
    public async Task Other_operations_do_not_enqueue_a_finished_goods_delivery()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(finished);
        await RegisterAsync(client, "wp09-other");
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 3);
        var part = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, is_active) VALUES (@code, true) RETURNING id",
            ("code", $"WP09-OTHER-{Guid.NewGuid():N}"));
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id) VALUES (@variant, @part) RETURNING id",
            ("variant", context.VariantId),
            ("part", part));
        foreach (var slot in slots)
        {
            await fixture.ExecuteAsync(
                "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING",
                ("slot", slot),
                ("part", part));
        }
        var workflow = fixture.Services.GetRequiredService<TransportWorkflowService>();

        await SetPositionAsync(slots[0], part, "VACANT", "UNKNOWN");
        await CompleteAsync(workflow, Request(context, "wp09-bring-empty", TransportTaskType.BringEmpty, source: null, destination: slots[0], part, group));
        await SetPositionAsync(slots[1], part, "VACANT", "UNKNOWN");
        await CompleteAsync(workflow, Request(context, "wp09-bring-full", TransportTaskType.BringFull, source: null, destination: slots[1], part, group));
        await SetPositionAsync(slots[2], part, "OCCUPIED", "EMPTY");
        await CompleteAsync(workflow, Request(context, "wp09-remove-empty", TransportTaskType.RemoveEmpty, source: slots[2], destination: null, part, group));

        Assert.Equal(0L, await OutboxCountAsync());
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
    }

    [Fact]
    public async Task Inactive_registration_is_not_targeted()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(finished);
        await RegisterAsync(client, "wp09-inactive");
        await fixture.ExecuteAsync(
            """
            UPDATE integration.device_push_registrations
            SET is_active = false, revoked_at = clock_timestamp()
            WHERE device_id = @device
            """,
            ("device", finished.DeviceId));
        var line = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var lineClient = await fixture.LoginClientAsync(line);
        var mapped = await SeedMappedPositionAsync(context, "INACTIVE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var created = await SendActionAsync(lineClient, context, mapped, "FULL", "wp09-inactive-delivery");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await CompleteQueuedAsync(await RequestIdAsync("wp09-inactive-delivery"));

        Assert.Equal(0L, await OutboxCountAsync());
    }

    [Fact]
    public async Task Worker_marks_success_retries_transient_failure_and_revokes_an_invalid_token()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(finished);
        await RegisterAsync(client, "wp09-worker-token");
        var requestId = await CreateRemoveFullAsync(context, "wp09-worker");
        await CompleteQueuedAsync(requestId);
        var processor = fixture.Services.GetRequiredService<PushOutboxProcessor>();

        Assert.Equal(1, await processor.ProcessPendingAsync());
        Assert.Equal(1, fixture.PushSender.Calls);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.push_outbox WHERE sent_at IS NOT NULL AND last_error IS NULL"));

        await fixture.ResetAsync();
        context = await fixture.ConfigureMachineAsync();
        finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var retryClient = await fixture.LoginClientAsync(finished);
        await RegisterAsync(retryClient, "wp09-retry-token");
        requestId = await CreateRemoveFullAsync(context, "wp09-retry");
        await CompleteQueuedAsync(requestId);
        fixture.PushSender.Next = PushSendOutcome.TransientFailure;
        Assert.Equal(1, await processor.ProcessPendingAsync());
        Assert.Equal(1, fixture.PushSender.Calls);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*) FROM integration.push_outbox
            WHERE sent_at IS NULL AND attempt_count = 1 AND next_attempt_at > clock_timestamp()
            """));
        Assert.Equal(0, await processor.ProcessPendingAsync());
        Assert.Equal(1, fixture.PushSender.Calls);

        await fixture.ResetAsync();
        context = await fixture.ConfigureMachineAsync();
        finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var invalidClient = await fixture.LoginClientAsync(finished);
        await RegisterAsync(invalidClient, "wp09-invalid-token");
        requestId = await CreateRemoveFullAsync(context, "wp09-invalid");
        await CompleteQueuedAsync(requestId);
        fixture.PushSender.Next = PushSendOutcome.InvalidToken;
        Assert.Equal(1, await processor.ProcessPendingAsync());
        Assert.False(await fixture.ScalarAsync<bool>(
            "SELECT is_active FROM integration.device_push_registrations WHERE device_id = @device",
            ("device", finished.DeviceId)));
        Assert.Equal(0, await processor.ProcessPendingAsync());
        Assert.Equal(1, fixture.PushSender.Calls);
    }

    [Fact]
    public async Task Worker_sends_a_claimed_row_once_and_skips_a_revoked_role()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(finished);
        await RegisterAsync(client, "wp09-once-token");
        var requestId = await CreateRemoveFullAsync(context, "wp09-once");
        await CompleteQueuedAsync(requestId);
        var processor = fixture.Services.GetRequiredService<PushOutboxProcessor>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource<PushSendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PushSender.Hold = hold;
        fixture.PushSender.OnSend = () => started.TrySetResult();

        var first = processor.ProcessPendingAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = processor.ProcessPendingAsync();
            var finishedSecond = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(second, finishedSecond);
            Assert.Equal(0, await second);
            Assert.Equal(1, fixture.PushSender.Calls);
            hold.TrySetResult(PushSendOutcome.Sent);
            Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, fixture.PushSender.Calls);
        }
        finally
        {
            hold.TrySetResult(PushSendOutcome.Sent);
        }

        await fixture.ResetAsync();
        context = await fixture.ConfigureMachineAsync();
        finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var revokedClient = await fixture.LoginClientAsync(finished);
        await RegisterAsync(revokedClient, "wp09-revoked-token");
        requestId = await CreateRemoveFullAsync(context, "wp09-revoked");
        await CompleteQueuedAsync(requestId);
        await fixture.ExecuteAsync(
            """
            DELETE FROM auth.user_roles WHERE user_id = @user;
            INSERT INTO auth.user_roles (user_id, role_id)
            SELECT @user, id FROM auth.roles WHERE code = 'LINE_OPERATOR'
            """,
            ("user", finished.UserId));
        var callsBefore = fixture.PushSender.Calls;
        Assert.Equal(1, await processor.ProcessPendingAsync());
        Assert.Equal(callsBefore, fixture.PushSender.Calls);
        Assert.Equal("RECIPIENT_NOT_AUTHORIZED", await fixture.ScalarAsync<string>(
            "SELECT last_error FROM integration.push_outbox"));
    }

    [Fact]
    public async Task Worker_error_text_does_not_include_the_token()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var finished = await WarehouseAsync(RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(finished);
        const string token = "wp09-secret-token-value";
        await RegisterAsync(client, token);
        var requestId = await CreateRemoveFullAsync(context, "wp09-redact");
        await CompleteQueuedAsync(requestId);
        fixture.PushSender.Throw = new InvalidOperationException($"provider failed for {token}");
        var processor = fixture.Services.GetRequiredService<PushOutboxProcessor>();

        Assert.Equal(1, await processor.ProcessPendingAsync());
        var error = await fixture.ScalarAsync<string>("SELECT last_error FROM integration.push_outbox");
        Assert.DoesNotContain(token, error);
        Assert.Equal(nameof(InvalidOperationException), error);
    }

    private Task<Guid> RequestIdAsync(string key) => fixture.ScalarAsync<Guid>(
        "SELECT id FROM ops.transport_requests WHERE idempotency_key = @key",
        ("key", key));

    private Task<long> OutboxCountAsync() =>
        fixture.ScalarAsync<long>("SELECT count(*) FROM integration.push_outbox");

    private async Task<TestIdentity> WarehouseAsync(string role) =>
        await fixture.SeedIdentityAsync(role: role, machineCode: "SH-01-A", bindDeviceToMachine: false);

    private static async Task RegisterAsync(HttpClient client, string token)
    {
        var response = await client.PutAsJsonAsync("/api/v1/auth/push-registration", new
        {
            provider = "FCM",
            platform = "ANDROID",
            token
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<Guid> CreateRemoveFullAsync(LayoutContext context, string key)
    {
        var line = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(line);
        var mapped = await SeedMappedPositionAsync(context, key);
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var created = await SendActionAsync(client, context, mapped, "FULL", key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return await RequestIdAsync(key);
    }

    private async Task CompleteQueuedAsync(Guid requestId)
    {
        var workflow = fixture.Services.GetRequiredService<TransportWorkflowService>();
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
    }

    private static async Task CompleteAsync(TransportWorkflowService workflow, CreateTransportRequest request)
    {
        var created = await workflow.CreateAsync(request);
        await workflow.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await workflow.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await workflow.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);
    }

    private static CreateTransportRequest Request(
        LayoutContext context,
        string key,
        TransportTaskType taskType,
        Guid? source,
        Guid? destination,
        Guid partId,
        Guid groupId) => new(
        key,
        RequestOrigin.LineOperator,
        null,
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        groupId,
        partId,
        null,
        taskType,
        source,
        destination);

    private async Task<MappedPosition> SeedMappedPositionAsync(LayoutContext context, string suffix)
    {
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1))[0];
        var part = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, is_active) VALUES (@code, true) RETURNING id",
            ("code", $"WP09-{suffix}-{Guid.NewGuid():N}"));
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id) VALUES (@variant_id, @part_id) RETURNING id",
            ("variant_id", context.VariantId),
            ("part_id", part));
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot_id, @part_id) ON CONFLICT DO NOTHING",
            ("slot_id", slot),
            ("part_id", part));
        return new MappedPosition(slot, part, group);
    }

    private Task SetPositionAsync(Guid slotId, Guid partId, string occupancy, string load) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state, current_part_id)
            VALUES (@slot_id, @occupancy, @load, @part_id)
            ON CONFLICT (layout_slot_id) DO UPDATE
            SET occupancy_state = EXCLUDED.occupancy_state,
                load_state = EXCLUDED.load_state,
                current_part_id = EXCLUDED.current_part_id,
                current_container_type_id = NULL,
                row_version = ops.slot_runtime_state.row_version + 1,
                updated_at = now()
            """,
            ("slot_id", slotId),
            ("occupancy", occupancy),
            ("load", load),
            ("part_id", partId));

    private static Task<HttpResponseMessage> SendActionAsync(
        HttpClient client, LayoutContext context, MappedPosition mapped, string action, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                context.VariantId, context.LayoutId, null, mapped.ProductGroupId, mapped.PartId, 1, action))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private sealed record MappedPosition(Guid SlotId, Guid PartId, Guid ProductGroupId);
}
