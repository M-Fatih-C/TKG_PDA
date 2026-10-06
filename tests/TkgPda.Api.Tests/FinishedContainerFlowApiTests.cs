using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-08 — Mamul Kasa: "Kasa Doldu" (REMOVE_FULL) and "Kasa Boş" (BRING_EMPTY).
/// Kasa Doldu must resolve a finished-goods destination on the server, refuse to
/// exist without an enabled store-location mapping, and stay atomic with the
/// load-state change. Delivery on completion is covered by
/// PostgresFinishedGoodsStoreTests.
/// </summary>
public sealed class FinishedContainerFlowApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private const string NotConfigured = "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED";

    [Fact]
    public async Task Kasa_doldu_marks_full_creates_remove_full_and_stores_the_server_resolved_destination()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "A");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        using var client = await ClientAsync();

        var response = await SendActionAsync(client, context, mapped, "FULL", "wp08-a");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("FULL", await LoadAsync(mapped.SlotId));
        var drop = await fixture.FinishedGoodsDropSlotAsync(context.LayoutId);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*) FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.idempotency_key = 'wp08-a' AND tt.code = 'REMOVE_FULL'
              AND r.source_slot_id = @source AND r.destination_slot_id = @drop
              AND r.part_id = @part AND r.product_group_id = @group
              AND r.readiness_state = 'NOT_REQUIRED'
            """,
            ("source", mapped.SlotId), ("drop", drop), ("part", mapped.PartId), ("group", mapped.ProductGroupId)));
        // The machine position is not vacated by merely creating the request.
        Assert.Equal("OCCUPIED", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", mapped.SlotId)));
    }

    [Fact]
    public async Task Kasa_doldu_on_an_already_full_container_creates_remove_full_without_a_load_mutation()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "B");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        using var client = await ClientAsync();

        var response = await SendActionAsync(client, context, mapped, "FULL", "wp08-b");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("FULL", await LoadAsync(mapped.SlotId));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_request_events WHERE event_type = 'CONTAINER_REPORTED_FULL'"));
    }

    [Fact]
    public async Task Kasa_doldu_double_submit_with_the_same_key_reuses_the_same_request_and_destination()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "DOUBLE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        using var client = await ClientAsync();

        var first = await SendActionAsync(client, context, mapped, "FULL", "wp08-double");
        var replay = await SendActionAsync(client, context, mapped, "FULL", "wp08-double");
        var second = await SendActionAsync(client, context, mapped, "FULL", "wp08-double-other-key");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Kasa_doldu_on_a_vacant_position_is_rejected_without_a_transport()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "VACANT");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "VACANT", "UNKNOWN");
        using var client = await ClientAsync();

        var response = await SendActionAsync(client, context, mapped, "FULL", "wp08-vacant");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OCCUPIED_CONTAINER_REQUIRED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("UNKNOWN", await LoadAsync(mapped.SlotId));
    }

    [Theory]
    [InlineData("UNKNOWN", "UNKNOWN")]
    [InlineData("OCCUPIED", "UNKNOWN")]
    public async Task Kasa_doldu_does_not_infer_empty_or_full_from_unknown_state(string occupancy, string load)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, $"UNK-{occupancy}-{load}");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, occupancy, load);
        using var client = await ClientAsync();

        var response = await SendActionAsync(client, context, mapped, "FULL", $"wp08-unk-{occupancy}-{load}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUSINESS_RULE_NOT_CONFIGURED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(load, await LoadAsync(mapped.SlotId));
        Assert.Equal(occupancy, await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", mapped.SlotId)));
    }

    [Theory]
    [InlineData("unlinked")]
    [InlineData("disabled-location")]
    [InlineData("no-drop-slot")]
    public async Task Kasa_doldu_without_an_enabled_finished_goods_destination_fails_closed_and_rolls_back_the_load_change(string gap)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, gap);
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        await OpenGapAsync(context.LayoutId, gap);
        using var client = await ClientAsync();

        var response = await SendActionAsync(client, context, mapped, "FULL", $"wp08-gap-{gap}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(NotConfigured, problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("yapılandırılmamış", problem.RootElement.GetProperty("detail").GetString());
        // The slot was EMPTY before Kasa Doldu and must still be EMPTY: the FULL
        // mutation and the request are one transaction.
        Assert.Equal("EMPTY", await LoadAsync(mapped.SlotId));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.store_units"));
    }

    [Fact]
    public async Task Position_projection_disables_kasa_doldu_but_not_kasa_bos_when_the_destination_is_not_configured()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "PROJECTION");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        using var client = await ClientAsync();

        var configured = await PositionAsync(client, context, mapped);
        Assert.True(configured.GetProperty("fullAction").GetProperty("enabled").GetBoolean());

        await OpenGapAsync(context.LayoutId, "unlinked");
        var unconfigured = await PositionAsync(client, context, mapped);
        var full = unconfigured.GetProperty("fullAction");
        Assert.False(full.GetProperty("enabled").GetBoolean());
        Assert.Equal(NotConfigured, full.GetProperty("disabledCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(full.GetProperty("disabledMessage").GetString()));

        await SetPositionAsync(mapped.SlotId, mapped.PartId, "VACANT", "UNKNOWN");
        var vacant = await PositionAsync(client, context, mapped);
        Assert.True(vacant.GetProperty("emptyAction").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Batch_and_remove_all_with_a_missing_destination_leave_no_partial_state()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await MakeLayoutVacantAsync(context.LayoutId);
        var mapped = await SeedMappedPositionAsync(context, "BATCH");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        await OpenGapAsync(context.LayoutId, "unlinked");
        using var client = await ClientAsync();

        var batch = await SendBatchAsync(client, context, mapped, "wp08-batch-gap");
        var removeAll = await SendRemoveAllAsync(client, context, "wp08-all-gap");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, batch.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, removeAll.StatusCode);
        Assert.Equal("FULL", await LoadAsync(mapped.SlotId));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_batches"));
    }

    [Fact]
    public async Task Kasa_bos_waits_for_preparation_needs_no_finished_goods_mapping_and_is_not_dispatched()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "BOS");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "VACANT", "UNKNOWN");
        await OpenGapAsync(context.LayoutId, "unlinked");
        using var client = await ClientAsync();

        var response = await SendActionAsync(client, context, mapped, "EMPTY", "wp08-bos");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("WAITING_PREPARATION", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE idempotency_key = 'wp08-bos'"));
        Assert.Equal("BRING_EMPTY", await fixture.ScalarAsync<string>(
            """
            SELECT tt.code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id WHERE r.idempotency_key = 'wp08-bos'
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        // Creating the request must not occupy the position.
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", mapped.SlotId)));
    }

    [Fact]
    public async Task Warehouse_mark_ready_lets_kasa_bos_progress_without_a_second_machine_confirmation()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "READY");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "VACANT", "UNKNOWN");
        using var lineClient = await ClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendActionAsync(lineClient, context, mapped, "EMPTY", "wp08-ready")).StatusCode);

        var requestId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = 'wp08-ready'");
        var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouse);
        var ready = await warehouseClient.PostAsync($"/api/v1/transport-requests/{requestId}/ready", null);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("READY", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", mapped.SlotId)));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Warehouse_only_roles_cannot_use_the_machine_side_container_actions()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "WAREHOUSE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");

        foreach (var role in new[]
                 {
                     RoleCodes.WarehouseOperator,
                     RoleCodes.SemiFinishedWarehouseOperator,
                     RoleCodes.FinishedGoodsWarehouseOperator
                 })
        {
            var identity = await fixture.SeedIdentityAsync(role: role, machineCode: "SH-01-A");
            using var client = await fixture.LoginClientAsync(identity);

            var response = await SendActionAsync(client, context, mapped, "FULL", $"wp08-wh-{role}");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal("EMPTY", await LoadAsync(mapped.SlotId));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task The_resulting_remove_full_is_visible_on_the_machine_active_transport_list()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "ACTIVE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        using var client = await ClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendActionAsync(client, context, mapped, "FULL", "wp08-active")).StatusCode);

        var response = await client.GetAsync($"/api/v1/machines/{context.MachineId}/operational-context");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var record = await response.Content.ReadFromJsonAsync<OperationalContextRecord>();

        var active = Assert.Single(record!.ActiveTransportRequests);
        Assert.Equal("REMOVE_FULL", active.Operation);
        Assert.Equal(mapped.PartId, active.PartId);
        Assert.Equal("NOT_REQUIRED", active.ReadinessState);
        Assert.Equal(1, active.QueuePosition);
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<HttpClient> ClientAsync()
    {
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        return await fixture.LoginClientAsync(identity);
    }

    private async Task<string> LoadAsync(Guid slotId) => await fixture.ScalarAsync<string>(
        "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
        ("slot", slotId));

    private Task OpenGapAsync(Guid layoutId, string gap) => gap switch
    {
        "unlinked" => fixture.ExecuteAsync(
            """
            DELETE FROM ops.store_location_delivery_slots
            WHERE layout_slot_id IN (
                SELECT id FROM master.layout_slots WHERE layout_id = @layout AND slot_role = 'DROP')
            """,
            ("layout", layoutId)),
        "disabled-location" => fixture.ExecuteAsync(
            "UPDATE ops.store_locations SET is_enabled = false WHERE code = @code",
            ("code", ApiTestFixture.FinishedGoodsTestLocationCode)),
        "no-drop-slot" => fixture.ExecuteAsync(
            "UPDATE master.layout_slots SET is_enabled = false WHERE layout_id = @layout AND slot_role = 'DROP'",
            ("layout", layoutId)),
        _ => throw new ArgumentOutOfRangeException(nameof(gap), gap, null)
    };

    private async Task<JsonElement> PositionAsync(HttpClient client, LayoutContext context, MappedPosition mapped)
    {
        var response = await client.GetAsync(
            $"/api/v1/machines/{context.MachineId}/container-positions" +
            $"?variantId={context.VariantId}&layoutId={context.LayoutId}" +
            $"&productGroupId={mapped.ProductGroupId}&partId={mapped.PartId}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var positions = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("positions");
        return positions.EnumerateArray().Single().Clone();
    }

    private async Task<MappedPosition> SeedMappedPositionAsync(LayoutContext context, string suffix)
    {
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1))[0];
        var code = $"WP08-{suffix}-{Guid.NewGuid():N}";
        var part = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, is_active) VALUES (@code, true) RETURNING id",
            ("code", code));
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id) VALUES (@variant_id, @part_id) RETURNING id",
            ("variant_id", context.VariantId),
            ("part_id", part));
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot_id, @part_id) ON CONFLICT DO NOTHING",
            ("slot_id", slot),
            ("part_id", part));
        return new(slot, part, group);
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

    private Task MakeLayoutVacantAsync(Guid layoutId) => fixture.ExecuteAsync(
        """
        INSERT INTO ops.slot_runtime_state (
            layout_slot_id, occupancy_state, load_state, current_part_id, current_container_type_id)
        SELECT ls.id, 'VACANT', 'UNKNOWN', NULL, NULL
        FROM master.layout_slots ls
        WHERE ls.layout_id = @layout_id AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = 'VACANT', load_state = 'UNKNOWN',
            current_part_id = NULL, current_container_type_id = NULL,
            row_version = ops.slot_runtime_state.row_version + 1, updated_at = now()
        """,
        ("layout_id", layoutId));
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

    private static Task<HttpResponseMessage> SendBatchAsync(
        HttpClient client, LayoutContext context, MappedPosition mapped, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions/batch")
        {
            Content = JsonContent.Create(new CreateContainerActionBatchHttpRequest(
                context.VariantId, context.LayoutId, null, [new(mapped.ProductGroupId, mapped.PartId, 1, "FULL")], 100))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendRemoveAllAsync(HttpClient client, LayoutContext context, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions/remove-all")
        {
            Content = JsonContent.Create(new RemoveAllContainersHttpRequest(context.VariantId, context.LayoutId, null))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private sealed record MappedPosition(Guid SlotId, Guid PartId, Guid ProductGroupId);
}
