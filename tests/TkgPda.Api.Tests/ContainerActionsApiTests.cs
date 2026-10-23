using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class ContainerActionsApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Container_positions_reject_another_machines_variant_and_layout_context()
    {
        await fixture.ResetAsync();
        var allowed = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var other = await fixture.ConfigureMachineAsync("SH-01-P1--SH-01-P2", "1");
        var otherMapped = await SeedMappedPositionAsync(other, "CROSS-MACHINE");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync(
            $"/api/v1/machines/{allowed.MachineId}/container-positions" +
            $"?variantId={other.VariantId}&layoutId={other.LayoutId}" +
            $"&productGroupId={otherMapped.ProductGroupId}&partId={otherMapped.PartId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("CROSS-MACHINE", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kasa_doldu_atomically_marks_full_and_creates_remove_full()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "FULL-EVENT");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-event");

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected Created, received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("FULL", await fixture.ScalarAsync<string>(
            "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot_id",
            ("slot_id", mapped.SlotId)));
        Assert.Equal("REMOVE_FULL", await fixture.ScalarAsync<string>(
            """
            SELECT tt.code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.source_slot_id = @slot_id
            """,
            ("slot_id", mapped.SlotId)));
        Assert.Equal("NOT_REQUIRED", await fixture.ScalarAsync<string>(
            """
            SELECT readiness_state FROM ops.transport_requests
            WHERE source_slot_id = @slot_id
            """,
            ("slot_id", mapped.SlotId)));
    }

    [Fact]
    public async Task Kasa_doldu_on_full_is_idempotent()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "FULL-REPLAY");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var first = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-replay");
        var replay = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-replay");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key = 'wp33-full-replay'"));
    }

    [Theory]
    [InlineData("VACANT", "UNKNOWN")]
    [InlineData("UNKNOWN", "UNKNOWN")]
    public async Task Kasa_doldu_rejects_unproven_container_state(string occupancy, string load)
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, $"FULL-{occupancy}");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, occupancy, load);
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var response = await SendActionAsync(client, context, mapped, "FULL", $"wp33-full-{occupancy}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key = @key",
            ("key", $"wp33-full-{occupancy}")));
    }

    [Fact]
    public async Task Kasa_doldu_rolls_back_load_when_request_creation_fails()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "FULL-ROLLBACK");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "EMPTY");
        await fixture.ExecuteAsync(
            """
            CREATE OR REPLACE FUNCTION ops.wp33_reject_test_request()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW.idempotency_key = 'wp33-full-rollback' THEN
                    RAISE EXCEPTION 'WP-33 rollback test';
                END IF;
                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER wp33_reject_test_request
            BEFORE INSERT ON ops.transport_requests
            FOR EACH ROW EXECUTE FUNCTION ops.wp33_reject_test_request();
            """);
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        try
        {
            var response = await SendActionAsync(client, context, mapped, "FULL", "wp33-full-rollback");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("EMPTY", await fixture.ScalarAsync<string>(
                "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot_id",
                ("slot_id", mapped.SlotId)));
            Assert.Equal(0L, await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key = 'wp33-full-rollback'"));
        }
        finally
        {
            await fixture.ExecuteAsync(
                """
                DROP TRIGGER IF EXISTS wp33_reject_test_request ON ops.transport_requests;
                DROP FUNCTION IF EXISTS ops.wp33_reject_test_request();
                """);
        }
    }

    [Fact]
    public async Task Kasa_bos_from_vacant_creates_bring_empty_but_occupied_is_rejected()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var vacant = await SeedMappedPositionAsync(context, "EMPTY-VACANT", positionOffset: 0);
        var occupiedEmpty = await SeedMappedPositionAsync(context, "EMPTY-OCCUPIED-EMPTY", positionOffset: 1);
        var occupiedFull = await SeedMappedPositionAsync(context, "EMPTY-OCCUPIED-FULL", positionOffset: 2);
        await SetPositionAsync(vacant.SlotId, vacant.PartId, "VACANT", "UNKNOWN");
        await SetPositionAsync(occupiedEmpty.SlotId, occupiedEmpty.PartId, "OCCUPIED", "EMPTY");
        await SetPositionAsync(occupiedFull.SlotId, occupiedFull.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var created = await SendActionAsync(client, context, vacant, "EMPTY", "wp33-empty-vacant");
        var rejectedEmpty = await SendActionAsync(client, context, occupiedEmpty, "EMPTY", "wp33-empty-occupied-empty");
        var rejectedFull = await SendActionAsync(client, context, occupiedFull, "EMPTY", "wp33-empty-occupied-full");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedEmpty.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedFull.StatusCode);
        Assert.Equal("BRING_EMPTY", await fixture.ScalarAsync<string>(
            """
            SELECT tt.code FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.idempotency_key = 'wp33-empty-vacant'
            """));
        Assert.Equal("WAITING_PREPARATION", await fixture.ScalarAsync<string>(
            """
            SELECT readiness_state FROM ops.transport_requests
            WHERE idempotency_key = 'wp33-empty-vacant'
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_reservations WHERE released_at IS NULL"));
    }

    [Fact]
    public async Task Customer_confirmed_finished_product_container_options_are_loaded_without_guessing_the_unresolved_part()
    {
        await fixture.ResetAsync();

        Assert.Equal(23L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM master.finished_product_container_options WHERE source_reference = 'TKG_EMAIL_RE_AMR_TAKIP_V1' AND is_active"));
        Assert.Equal(22L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM master.finished_product_container_options WHERE source_reference = 'TKG_EMAIL_RE_AMR_TAKIP_V1' AND part_id IS NOT NULL"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM master.finished_product_container_options WHERE customer_part_code = '66424BN100-10-HMTR' AND part_id IS NULL"));
        Assert.Equal("CLK_STR03|CLK_S963", await fixture.ScalarAsync<string>(
            """
            SELECT primary_container.code || '|' || alternative_container.code
            FROM master.finished_product_container_options option
            JOIN master.container_types primary_container ON primary_container.id = option.primary_container_type_id
            JOIN master.container_types alternative_container ON alternative_container.id = option.alternative_container_type_id
            WHERE option.customer_part_code = '66424BN100-10-HMTR'
            """));
        Assert.Equal("CLK_DB121010|CLK_1284", await fixture.ScalarAsync<string>(
            """
            SELECT primary_container.code || '|' || alternative_container.code
            FROM master.finished_product_container_options option
            JOIN master.container_types primary_container ON primary_container.id = option.primary_container_type_id
            JOIN master.container_types alternative_container ON alternative_container.id = option.alternative_container_type_id
            WHERE option.customer_part_code = '77160BN400-10-HWON'
            """));
    }

    [Fact]
    public async Task Finished_goods_operator_can_prepare_the_confirmed_alternative_container_for_an_empty_request()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var vacant = await SeedMappedPositionAsync(context, "EMPTY-ALTERNATIVE");
        await SetPositionAsync(vacant.SlotId, vacant.PartId, "VACANT", "UNKNOWN");
        await fixture.ExecuteAsync(
            "UPDATE master.layout_slots SET default_container_type_id = NULL WHERE id = @slot",
            ("slot", vacant.SlotId));
        var suffix = Guid.NewGuid().ToString("N");
        var primary = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.container_types (code, name, category) VALUES (@code, 'Ana kasa', 'OTHER') RETURNING id",
            ("code", "PRIMARY-" + suffix));
        var alternative = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.container_types (code, name, category) VALUES (@code, 'Alternatif kasa', 'OTHER') RETURNING id",
            ("code", "ALTERNATIVE-" + suffix));
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.finished_product_container_options
                (customer_part_code, part_id, primary_container_type_id,
                 alternative_container_type_id, source_reference)
            VALUES (@customer_code, @part, @primary, @alternative, 'TEST')
            """,
            ("customer_code", "CUSTOMER-" + suffix),
            ("part", vacant.PartId),
            ("primary", primary),
            ("alternative", alternative));

        using var lineClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(machineCode: "SH-01-A"));
        var created = await SendActionAsync(
            lineClient, context, vacant, "EMPTY", "empty-alternative-" + suffix);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = @key",
            ("key", "empty-alternative-" + suffix));
        Assert.Equal(primary, await fixture.ScalarAsync<Guid>(
            "SELECT container_type_id FROM ops.transport_requests WHERE id = @id", ("id", requestId)));

        using var warehouseClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(
                role: RoleCodes.FinishedGoodsWarehouseOperator,
                machineCode: "SH-01-A"));
        var demands = await warehouseClient.GetFromJsonAsync<List<WarehouseDemandRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/demands");
        var demand = Assert.Single(demands!, item => item.RequestId == requestId);
        Assert.Equal(primary, demand.ContainerTypeId);
        Assert.Equal(alternative, demand.AlternativeContainerTypeId);

        var areas = await warehouseClient.GetFromJsonAsync<List<PreparationAreaRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/areas");
        var placement = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/warehouse/finished-goods-preparation/areas/{areas![0].Id}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest(
                "EMPTY", alternative, null, requestId))
        };
        placement.Headers.Add(ApiHeaders.IdempotencyKey, "empty-alternative-placement-" + suffix);
        var placedResponse = await warehouseClient.SendAsync(placement);
        Assert.Equal(HttpStatusCode.Created, placedResponse.StatusCode);
        var placed = await placedResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placed);
        Assert.Equal(alternative, placed.ContainerTypeId);

        var readyResponse = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/finished-goods-preparation/pallets/{placed.Id}/ready",
            new PalletVersionHttpRequest(placed.RowVersion));
        readyResponse.EnsureSuccessStatusCode();
        Assert.Equal(alternative, await fixture.ScalarAsync<Guid>(
            "SELECT container_type_id FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
    }

    [Fact]
    public async Task Empty_container_request_stays_the_same_request_until_finished_goods_marks_it_ready()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var vacant = await SeedMappedPositionAsync(context, "EMPTY-PREPARED");
        var containerTypeId = await fixture.ScalarAsync<Guid>(
            """
            INSERT INTO master.container_types (code, name, category)
            VALUES (@code, 'Test boş kasa', 'CONTAINER')
            RETURNING id
            """,
            ("code", "WP33-CONTAINER-" + Guid.NewGuid().ToString("N")));
        await fixture.ExecuteAsync(
            "UPDATE master.layout_slots SET default_container_type_id = @container WHERE id = @slot",
            ("container", containerTypeId),
            ("slot", vacant.SlotId));
        await SetPositionAsync(vacant.SlotId, vacant.PartId, "VACANT", "UNKNOWN");
        var lineIdentity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var lineClient = await fixture.LoginClientAsync(lineIdentity);

        var created = await SendActionAsync(lineClient, context, vacant, "EMPTY", "wp33-empty-prepared");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = 'wp33-empty-prepared'");

        using var warehouseClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(
                role: RoleCodes.FinishedGoodsWarehouseOperator,
                machineCode: "SH-01-A"));
        var demands = await warehouseClient.GetFromJsonAsync<List<WarehouseDemandRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/demands");
        var demand = Assert.Single(demands!, item => item.RequestId == requestId);
        Assert.Equal("BRING_EMPTY", demand.Operation);
        Assert.Equal("SH-01-A", demand.MachineCode);
        Assert.NotNull(demand.VariantCode);
        Assert.NotNull(demand.PartCode);
        Assert.NotNull(demand.ContainerTypeId);

        var areas = await warehouseClient.GetFromJsonAsync<List<PreparationAreaRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/areas");
        Assert.Equal(6, areas!.Count);
        var area = areas[0];
        var placement = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/warehouse/finished-goods-preparation/areas/{area.Id}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest(
                "EMPTY", demand.ContainerTypeId!.Value, null, requestId))
        };
        placement.Headers.Add(ApiHeaders.IdempotencyKey, "wp33-empty-placement");
        var placedResponse = await warehouseClient.SendAsync(placement);
        Assert.Equal(HttpStatusCode.Created, placedResponse.StatusCode);
        var placed = await placedResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placed);
        Assert.Equal(requestId, placed.TransportRequestId);

        var readyResponse = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/finished-goods-preparation/pallets/{placed.Id}/ready",
            new PalletVersionHttpRequest(placed.RowVersion));
        readyResponse.EnsureSuccessStatusCode();

        Assert.Equal("READY", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        var claimedAfterReady = await warehouseClient.GetFromJsonAsync<List<WarehouseDemandRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/demands");
        var retained = Assert.Single(claimedAfterReady!, item => item.RequestId == requestId);
        Assert.Equal(area.Id, retained.ClaimedByAreaId);
        Assert.Equal(demand.VariantCode, retained.VariantCode);
        Assert.Equal(demand.PartCode, retained.PartCode);

        var navigationId = await fixture.ScalarAsync<Guid>(
            """
            INSERT INTO master.navigation_profiles (machine_id, code, name, status, created_by)
            VALUES (@machine, @code, 'Prepared empty source test', 'DRAFT', @user)
            RETURNING id
            """,
            ("machine", context.MachineId),
            ("code", "DEV-RDS-SIM-" + Guid.NewGuid().ToString("N")),
            ("user", lineIdentity.UserId));
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET navigation_id = @navigation WHERE id = @id",
            ("navigation", navigationId), ("id", requestId));
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var execution = new PostgresRdsExecutionStore(dataSource);
        Assert.Equal(
            "PREPARATION_WORKSITE_MAPPING_MISSING",
            await execution.ReadPhysicalDispatchBlockAsync(requestId));

        var workSiteId = await fixture.ScalarAsync<Guid>(
            """
            INSERT INTO integration.rds_worksites (
                navigation_profile_id, rds_site_id, rds_type, last_filled,
                last_locked, last_disabled, last_sync_failed, last_sync_at)
            VALUES (@navigation, @site, 1, 1, 0, 0, 0, clock_timestamp())
            RETURNING id
            """,
            ("navigation", navigationId),
            ("site", "PREP-EMPTY-" + Guid.NewGuid().ToString("N")));
        await fixture.ExecuteAsync(
            """
            INSERT INTO integration.preparation_area_rds_mapping (preparation_area_id, rds_worksite_id)
            VALUES (@area, @worksite)
            """,
            ("area", area.Id), ("worksite", workSiteId));
        Assert.Null(await execution.ReadPhysicalDispatchBlockAsync(requestId));

        await fixture.ExecuteAsync(
            "UPDATE integration.rds_worksites SET last_filled = 0, last_sync_at = clock_timestamp() WHERE id = @id",
            ("id", workSiteId));
        Assert.Equal(
            "RDS_WORKSITE_OCCUPANCY_CONFLICT",
            await execution.ReadPhysicalDispatchBlockAsync(requestId));
    }

    [Fact]
    public async Task Source_backed_dimensions_allow_finished_goods_to_prepare_an_exact_empty_request_without_inventing_a_container_code()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var vacant = await SeedMappedPositionAsync(context, "EMPTY-DIMENSIONS-ONLY");
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.part_planning_metrics
                (variant_id, product_group_id, part_id, container_dimensions, source_note)
            VALUES (@variant, @group, @part, '1200/1000/1370', 'Customer source test evidence')
            """,
            ("variant", context.VariantId),
            ("group", vacant.ProductGroupId),
            ("part", vacant.PartId));
        await fixture.ExecuteAsync(
            "UPDATE master.layout_slots SET default_container_type_id = NULL WHERE id = @slot",
            ("slot", vacant.SlotId));
        await SetPositionAsync(vacant.SlotId, vacant.PartId, "VACANT", "UNKNOWN");

        using var lineClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(machineCode: "SH-01-A"));
        var created = await SendActionAsync(
            lineClient, context, vacant, "EMPTY", "wp33-empty-dimensions-only");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = 'wp33-empty-dimensions-only'");

        using var warehouseClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(
                role: RoleCodes.FinishedGoodsWarehouseOperator,
                machineCode: "SH-01-A"));
        var demands = await warehouseClient.GetFromJsonAsync<List<WarehouseDemandRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/demands");
        var demand = Assert.Single(demands!, item => item.RequestId == requestId);
        Assert.Null(demand.ContainerTypeId);
        Assert.Null(demand.ContainerCode);
        Assert.Equal("1200/1000/1370", demand.ContainerDimensions);

        var areas = await warehouseClient.GetFromJsonAsync<List<PreparationAreaRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/areas");
        var placement = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/warehouse/finished-goods-preparation/areas/{areas![0].Id}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest(
                "EMPTY", null, null, requestId))
        };
        placement.Headers.Add(ApiHeaders.IdempotencyKey, "wp33-empty-dimensions-placement");
        var placedResponse = await warehouseClient.SendAsync(placement);
        var placedBody = await placedResponse.Content.ReadAsStringAsync();
        Assert.True(
            placedResponse.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created but received {(int)placedResponse.StatusCode}: {placedBody}");
        var placed = await placedResponse.Content.ReadFromJsonAsync<PreparationPalletRecord>();
        Assert.NotNull(placed);
        Assert.Null(placed.ContainerTypeId);

        var readyResponse = await warehouseClient.PostAsJsonAsync(
            $"/api/v1/warehouse/finished-goods-preparation/pallets/{placed.Id}/ready",
            new PalletVersionHttpRequest(placed.RowVersion));
        readyResponse.EnsureSuccessStatusCode();

        Assert.Equal("READY", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE id = @id AND container_type_id IS NOT NULL",
            ("id", requestId)));
    }

    [Fact]
    public async Task Finished_goods_preparation_rejects_an_untyped_empty_request_without_source_dimensions()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var vacant = await SeedMappedPositionAsync(context, "EMPTY-NO-SPEC");
        await fixture.ExecuteAsync(
            "UPDATE master.layout_slots SET default_container_type_id = NULL WHERE id = @slot",
            ("slot", vacant.SlotId));
        await SetPositionAsync(vacant.SlotId, vacant.PartId, "VACANT", "UNKNOWN");

        using var lineClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(machineCode: "SH-01-A"));
        var created = await SendActionAsync(
            lineClient, context, vacant, "EMPTY", "wp33-empty-no-spec");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE idempotency_key = 'wp33-empty-no-spec'");

        using var warehouseClient = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(
                role: RoleCodes.FinishedGoodsWarehouseOperator,
                machineCode: "SH-01-A"));
        var areas = await warehouseClient.GetFromJsonAsync<List<PreparationAreaRecord>>(
            "/api/v1/warehouse/finished-goods-preparation/areas");
        var placement = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/warehouse/finished-goods-preparation/areas/{areas![0].Id}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest(
                "EMPTY", null, null, requestId))
        };
        placement.Headers.Add(ApiHeaders.IdempotencyKey, "wp33-empty-no-spec-placement");

        var response = await warehouseClient.SendAsync(placement);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "PREPARATION_CONTAINER_SPEC_REQUIRED",
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Remove_all_uses_entire_layout_and_is_atomic_on_unknown()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await MakeLayoutVacantAsync(context.LayoutId);
        var full = await SeedMappedPositionAsync(context, "ALL-FULL", positionOffset: 0);
        var empty = await SeedMappedPositionAsync(context, "ALL-EMPTY", positionOffset: 1);
        var unknown = await SeedMappedPositionAsync(context, "ALL-UNKNOWN", positionOffset: 2);
        await SetPositionAsync(full.SlotId, full.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(empty.SlotId, empty.PartId, "OCCUPIED", "EMPTY");
        await SetPositionAsync(unknown.SlotId, unknown.PartId, "UNKNOWN", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var blocked = await SendRemoveAllAsync(client, context, "wp33-all-blocked");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE idempotency_key = 'wp33-all-blocked'"));

        await SetPositionAsync(unknown.SlotId, unknown.PartId, "VACANT", "UNKNOWN");
        var created = await SendRemoveAllAsync(client, context, "wp33-all-created");
        var replay = await SendRemoveAllAsync(client, context, "wp33-all-created");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var operations = await fixture.ScalarAsync<string[]>(
            """
            SELECT array_agg(tt.code ORDER BY tt.code)
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            JOIN ops.transport_batches b ON b.id = r.batch_id
            WHERE b.idempotency_key = 'wp33-all-created'
            """);
        Assert.Equal(["REMOVE_EMPTY", "REMOVE_FULL"], operations);
    }

    [Fact]
    public async Task Remove_all_rejects_the_whole_batch_when_any_position_is_reserved()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        await MakeLayoutVacantAsync(context.LayoutId);
        var reserved = await SeedMappedPositionAsync(context, "ALL-RESERVED", positionOffset: 0);
        var otherwiseEligible = await SeedMappedPositionAsync(context, "ALL-OTHER", positionOffset: 1);
        await SetPositionAsync(reserved.SlotId, reserved.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(otherwiseEligible.SlotId, otherwiseEligible.PartId, "OCCUPIED", "EMPTY");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendActionAsync(client, context, reserved, "FULL", "wp33-reserve-position")).StatusCode);

        var response = await SendRemoveAllAsync(client, context, "wp33-all-reserved");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE idempotency_key = 'wp33-all-reserved'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Concurrent_container_actions_create_only_one_reservation()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "CONCURRENT");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var responses = await Task.WhenAll(
            SendActionAsync(client, context, mapped, "FULL", "wp33-concurrent-a"),
            SendActionAsync(client, context, mapped, "FULL", "wp33-concurrent-b"));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE idempotency_key LIKE 'wp33-concurrent-%'"));
    }

    [Fact]
    public async Task Container_action_batch_rejects_same_key_with_a_different_payload_or_endpoint_kind()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var mapped = await SeedMappedPositionAsync(context, "BATCH-REUSE");
        await SetPositionAsync(mapped.SlotId, mapped.PartId, "OCCUPIED", "FULL");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);

        var created = await SendActionBatchAsync(client, context, mapped, "FULL", "wp33-batch-reuse");
        var replay = await SendActionBatchAsync(client, context, mapped, "FULL", "wp33-batch-reuse");
        var changedPayload = await SendActionBatchAsync(client, context, mapped, "EMPTY", "wp33-batch-reuse");
        var changedEndpointKind = await SendRemoveAllAsync(client, context, "wp33-batch-reuse");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedEndpointKind.StatusCode);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_batches WHERE idempotency_key = 'wp33-batch-reuse'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE batch_id IS NOT NULL"));
    }

    [Fact]
    public async Task Global_queue_is_cross_user_cross_machine_and_hides_technical_identifiers()
    {
        await fixture.ResetAsync();
        var first = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var second = await fixture.ConfigureMachineAsync("SH-01-P1--SH-01-P2", "1");
        var firstMapped = await SeedMappedPositionAsync(first, "QUEUE-A");
        var secondMapped = await SeedMappedPositionAsync(second, "QUEUE-B");
        await SetPositionAsync(firstMapped.SlotId, firstMapped.PartId, "OCCUPIED", "FULL");
        await SetPositionAsync(secondMapped.SlotId, secondMapped.PartId, "OCCUPIED", "FULL");
        var firstIdentity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        var secondIdentity = await fixture.SeedIdentityAsync(machineCode: "SH-01-P1--SH-01-P2");
        using var firstClient = await fixture.LoginClientAsync(firstIdentity);
        using var secondClient = await fixture.LoginClientAsync(secondIdentity);
        await SendActionAsync(firstClient, first, firstMapped, "FULL", "wp33-queue-a");
        await SendActionAsync(secondClient, second, secondMapped, "FULL", "wp33-queue-b");
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'RUNNING' WHERE idempotency_key = 'wp33-queue-b'");

        var response = await secondClient.GetAsync("/api/v1/transport-requests/queue");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Contains(
            "SH-01-P1--SH-01-P2",
            document.RootElement.GetProperty("items")[0].GetProperty("machine").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("rdsStatus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceSlot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("destinationSlot", json, StringComparison.OrdinalIgnoreCase);

        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'COMPLETED' WHERE idempotency_key = 'wp33-queue-a'");
        var active = await secondClient.GetFromJsonAsync<JsonElement>("/api/v1/transport-requests/queue");
        var terminal = await secondClient.GetFromJsonAsync<JsonElement>("/api/v1/transport-requests/queue?terminal=true");
        Assert.Single(active.GetProperty("items").EnumerateArray());
        Assert.Single(terminal.GetProperty("items").EnumerateArray());
    }

    private async Task<MappedPosition> SeedMappedPositionAsync(
        LayoutContext context,
        string suffix,
        int positionOffset = 0)
    {
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, positionOffset + 1))[positionOffset];
        var code = $"WP33-{suffix}-{Guid.NewGuid():N}";
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
            layout_slot_id,
            occupancy_state,
            load_state,
            current_part_id,
            current_container_type_id)
        SELECT ls.id, 'VACANT', 'UNKNOWN', NULL, NULL
        FROM master.layout_slots ls
        WHERE ls.layout_id = @layout_id
          AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = 'VACANT',
            load_state = 'UNKNOWN',
            current_part_id = NULL,
            current_container_type_id = NULL,
            row_version = ops.slot_runtime_state.row_version + 1,
            updated_at = now()
        """,
        ("layout_id", layoutId));

    private static Task<HttpResponseMessage> SendActionAsync(
        HttpClient client,
        LayoutContext context,
        MappedPosition mapped,
        string action,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions")
        {
            Content = JsonContent.Create(new CreateContainerActionHttpRequest(
                context.VariantId,
                context.LayoutId,
                null,
                mapped.ProductGroupId,
                mapped.PartId,
                1,
                action))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendRemoveAllAsync(
        HttpClient client,
        LayoutContext context,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions/remove-all")
        {
            Content = JsonContent.Create(new RemoveAllContainersHttpRequest(
                context.VariantId,
                context.LayoutId,
                null))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendActionBatchAsync(
        HttpClient client,
        LayoutContext context,
        MappedPosition mapped,
        string action,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/machines/{context.MachineId}/container-actions/batch")
        {
            Content = JsonContent.Create(new CreateContainerActionBatchHttpRequest(
                context.VariantId,
                context.LayoutId,
                null,
                [new(mapped.ProductGroupId, mapped.PartId, 1, action)],
                100))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private sealed record MappedPosition(Guid SlotId, Guid PartId, Guid ProductGroupId);
}
