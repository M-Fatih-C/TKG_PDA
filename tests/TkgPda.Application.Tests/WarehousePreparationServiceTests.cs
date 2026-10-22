using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

/// <summary>
/// The service refuses what it can decide without reading anything. Everything
/// that depends on stored state is the store's job inside its transaction, so
/// these tests assert the refusals and that a well-formed command is handed on
/// unchanged, never that a placement "worked".
/// </summary>
public sealed class WarehousePreparationServiceTests
{
    private static readonly Guid Area = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Container = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Part = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid Request = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Actor = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Device = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private sealed class RecordingStore : IWarehousePreparationStore
    {
        public PlacePalletCommand? Placed { get; private set; }
        public MarkPalletReadyCommand? Readied { get; private set; }
        public ReleaseAreaCommand? Released { get; private set; }

        public Task<IReadOnlyList<PreparationAreaRecord>> ListAreasAsync(
            string warehouseScope = WarehousePreparationScopes.SemiFinished,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PreparationAreaRecord>>([]);

        public Task<IReadOnlyList<WarehouseDemandRecord>> ListDemandsAsync(
            string warehouseScope = WarehousePreparationScopes.SemiFinished,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WarehouseDemandRecord>>([]);

        public Task<PreparationPalletResult> PlacePalletAsync(PlacePalletCommand command, CancellationToken cancellationToken = default)
        {
            Placed = command;
            return Task.FromResult(new PreparationPalletResult(Pallet(command), WasIdempotentReplay: false));
        }

        public Task<PreparationPalletRecord> MarkReadyAsync(MarkPalletReadyCommand command, CancellationToken cancellationToken = default)
        {
            Readied = command;
            return Task.FromResult(Pallet(null));
        }

        public Task<PreparationPalletRecord> ReleaseAsync(ReleaseAreaCommand command, CancellationToken cancellationToken = default)
        {
            Released = command;
            return Task.FromResult(Pallet(null));
        }

        private static PreparationPalletRecord Pallet(PlacePalletCommand? command) => new(
            Id: Guid.NewGuid(),
            ContainerTypeId: command?.ContainerTypeId ?? Container,
            ContainerCode: "4311",
            Load: (command?.Load ?? LoadState.Empty).ToDatabase(),
            PartId: command?.PartId,
            PartCode: null,
            TransportRequestId: command?.TransportRequestId,
            RequestNo: null,
            TargetMachineCode: null,
            PlacedAt: DateTimeOffset.UnixEpoch,
            PlacedBy: Actor,
            ReadyAt: null,
            RowVersion: 0);
    }

    private static PlacePalletCommand Place(
        LoadState load,
        Guid? partId = null,
        Guid? requestId = null,
        string key = "key-1",
        string scope = WarehousePreparationScopes.SemiFinished) =>
        new(Area, Container, load, partId, requestId, Actor, Device, key, WarehouseScope: scope);

    private static (WarehousePreparationService Service, RecordingStore Store) Build()
    {
        var store = new RecordingStore();
        return (new WarehousePreparationService(store), store);
    }

    [Fact]
    public async Task Empty_stock_may_be_staged_before_any_request_exists()
    {
        var (service, store) = Build();

        await service.PlacePalletAsync(Place(LoadState.Empty));

        Assert.NotNull(store.Placed);
        Assert.Equal(LoadState.Empty, store.Placed!.Load);
        Assert.Null(store.Placed.TransportRequestId);
    }

    [Fact]
    public async Task Empty_container_cannot_be_recorded_holding_material()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Empty, partId: Part)));

        Assert.Equal("EMPTY_PALLET_HAS_MATERIAL", error.Code);
    }

    [Fact]
    public async Task Empty_stock_is_not_bound_to_a_request_when_placed()
    {
        // Matching staged stock to a demand happens at the ready step, against
        // evidence the server loaded, not on the strength of a client-sent id.
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Empty, requestId: Request)));

        Assert.Equal("EMPTY_PREPARATION_NOT_REQUEST_BOUND", error.Code);
    }

    [Fact]
    public async Task Finished_goods_empty_pallet_is_bound_to_the_exact_request()
    {
        var (service, store) = Build();

        await service.PlacePalletAsync(Place(
            LoadState.Empty,
            requestId: Request,
            scope: WarehousePreparationScopes.FinishedGoods));

        Assert.Equal(Request, store.Placed!.TransportRequestId);
        Assert.Equal(WarehousePreparationScopes.FinishedGoods, store.Placed.WarehouseScope);
    }

    [Fact]
    public async Task Semi_finished_material_needs_a_request()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Full, partId: Part)));

        Assert.Equal("PREPARATION_DEMAND_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Semi_finished_material_names_its_part()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Full, requestId: Request)));

        Assert.Equal("PREPARATION_PART_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Well_formed_material_placement_reaches_the_store_unchanged()
    {
        var (service, store) = Build();

        await service.PlacePalletAsync(Place(LoadState.Full, partId: Part, requestId: Request));

        Assert.Equal(Part, store.Placed!.PartId);
        Assert.Equal(Request, store.Placed.TransportRequestId);
        Assert.Equal(LoadState.Full, store.Placed.Load);
    }

    [Fact]
    public async Task Unknown_load_is_refused_rather_than_stored()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Unknown)));

        Assert.Equal("PREPARATION_LOAD_UNKNOWN", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Placement_requires_an_idempotency_key(string key)
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Empty, key: key)));

        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Placement_rejects_an_oversized_idempotency_key()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(Place(LoadState.Empty, key: new string('k', 201))));

        Assert.Equal("IDEMPOTENCY_KEY_TOO_LONG", error.Code);
    }

    [Fact]
    public async Task Placement_requires_real_identities()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.PlacePalletAsync(
                new PlacePalletCommand(Guid.Empty, Container, LoadState.Empty, null, null, Actor, Device, "k")));

        Assert.Equal("PREPARATION_IDENTITY_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Ready_carries_the_version_the_screen_was_showing()
    {
        var (service, store) = Build();

        await service.MarkReadyAsync(new MarkPalletReadyCommand(Guid.NewGuid(), 7, Actor, Device));

        Assert.Equal(7, store.Readied!.ExpectedRowVersion);
    }

    [Fact]
    public async Task Ready_refuses_a_negative_version()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.MarkReadyAsync(new MarkPalletReadyCommand(Guid.NewGuid(), -1, Actor, Device)));

        Assert.Equal("PREPARATION_ROW_VERSION_REQUIRED", error.Code);
    }

    [Theory]
    [InlineData("COLLECTED")]
    [InlineData("WITHDRAWN")]
    public async Task Release_accepts_only_the_two_stated_reasons(string reason)
    {
        var (service, store) = Build();

        await service.ReleaseAsync(new ReleaseAreaCommand(Guid.NewGuid(), 3, reason, Actor, Device));

        Assert.Equal(reason, store.Released!.Reason);
    }

    [Theory]
    [InlineData("collected")]
    [InlineData("DELIVERED")]
    [InlineData("")]
    public async Task Release_refuses_any_other_reason(string reason)
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.ReleaseAsync(new ReleaseAreaCommand(Guid.NewGuid(), 3, reason, Actor, Device)));

        Assert.Equal("PREPARATION_RELEASE_REASON_INVALID", error.Code);
    }

    [Fact]
    public void A_free_area_reports_itself_free()
    {
        var free = new PreparationAreaRecord(Area, 1, "Hazırlık Alanı 1", true, null);
        Assert.True(free.IsFree);
    }

    [Fact]
    public void A_placed_pallet_is_not_ready_until_it_is_confirmed()
    {
        var pallet = new PreparationPalletRecord(
            Guid.NewGuid(), Container, "4311", "FULL", Part, "67115BN050-10-HAOS",
            Request, 42, "SH-01-A", DateTimeOffset.UnixEpoch, Actor, null, 0);

        Assert.Equal("PLACED", pallet.Status);
        Assert.Equal("READY", (pallet with { ReadyAt = DateTimeOffset.UnixEpoch }).Status);
    }
}
