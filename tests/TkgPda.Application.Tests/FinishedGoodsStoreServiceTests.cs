using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

/// <summary>
/// The store's read shape and the refusals the service can make without reading
/// anything. Whether a container really left the warehouse is the operator's
/// statement, recorded by the store inside its own transaction.
/// </summary>
public sealed class FinishedGoodsStoreServiceTests
{
    private static readonly Guid Unit = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Location = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Actor = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Device = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private sealed class RecordingStore : IFinishedGoodsStore
    {
        public RemoveStoreUnitCommand? Removed { get; private set; }
        public ClearStoreLocationCommand? Cleared { get; private set; }
        public RecordEmptyContainerCommand? RecordedEmpty { get; private set; }

        public Task<IReadOnlyList<StoreLocationRecord>> ListLocationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoreLocationRecord>>([]);

        public Task<StoreLocationRecord> RemoveUnitAsync(RemoveStoreUnitCommand command, CancellationToken cancellationToken = default)
        {
            Removed = command;
            return Task.FromResult(Location_(capacity: 3));
        }

        public Task<StoreLocationRecord> ClearLocationAsync(ClearStoreLocationCommand command, CancellationToken cancellationToken = default)
        {
            Cleared = command;
            return Task.FromResult(Location_(capacity: 3));
        }

        public Task<StoreLocationRecord> RecordEmptyContainerAsync(RecordEmptyContainerCommand command, CancellationToken cancellationToken = default)
        {
            RecordedEmpty = command;
            return Task.FromResult(Location_(capacity: null));
        }
    }

    private static StoreUnitRecord Unit_(string? partCode = "67116BN050-40") => new(
        Id: Unit,
        TransportRequestId: Guid.NewGuid(),
        RequestNo: 42,
        PartId: Guid.NewGuid(),
        PartCode: partCode,
        ContainerTypeId: Guid.NewGuid(),
        ContainerCode: "4322",
        MachineId: Guid.NewGuid(),
        MachineCode: "SH-01-A",
        DeliveredAt: DateTimeOffset.UnixEpoch,
        RowVersion: 0);

    private static StoreLocationRecord Location_(int? capacity, int units = 0) => new(
        Id: Location,
        Code: "STORE-1",
        DisplayName: "Bitmiş Ürün Alanı 1",
        Capacity: capacity,
        IsEnabled: true,
        Units: [.. Enumerable.Range(0, units).Select(_ => Unit_())]);

    private static (FinishedGoodsStoreService Service, RecordingStore Store) Build()
    {
        var store = new RecordingStore();
        return (new FinishedGoodsStoreService(store), store);
    }

    [Fact]
    public async Task Removing_a_container_carries_the_version_the_screen_showed()
    {
        var (service, store) = Build();

        await service.RemoveUnitAsync(new RemoveStoreUnitCommand(Unit, 5, Actor, Device));

        Assert.Equal(5, store.Removed!.ExpectedRowVersion);
        Assert.Equal(Unit, store.Removed.UnitId);
    }

    [Fact]
    public async Task Removing_refuses_a_negative_version()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.RemoveUnitAsync(new RemoveStoreUnitCommand(Unit, -1, Actor, Device)));

        Assert.Equal("STORE_ROW_VERSION_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Removing_requires_a_real_actor_and_device()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.RemoveUnitAsync(new RemoveStoreUnitCommand(Unit, 0, Guid.Empty, Device)));

        Assert.Equal("STORE_IDENTITY_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Clearing_names_the_location()
    {
        var (service, store) = Build();

        await service.ClearLocationAsync(new ClearStoreLocationCommand(Location, Actor, Device));

        Assert.Equal(Location, store.Cleared!.LocationId);
    }

    [Fact]
    public async Task Clearing_requires_a_real_location()
    {
        var (service, _) = Build();

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(
            () => service.ClearLocationAsync(new ClearStoreLocationCommand(Guid.Empty, Actor, Device)));

        Assert.Equal("STORE_IDENTITY_REQUIRED", error.Code);
    }

    [Fact]
    public void An_unknown_capacity_produces_no_fill_ratio()
    {
        // The customer has not said how many containers fit. A client must show
        // no fill warning rather than invent a denominator.
        var location = Location_(capacity: null, units: 5);

        Assert.Null(location.FillRatio);
        Assert.False(location.IsFull);
        Assert.Equal(5, location.UnitCount);
    }

    [Fact]
    public void A_known_capacity_produces_a_ratio_and_a_full_verdict()
    {
        Assert.Equal(1.0 / 3, Location_(capacity: 3, units: 1).FillRatio);
        Assert.False(Location_(capacity: 3, units: 2).IsFull);
        Assert.True(Location_(capacity: 3, units: 3).IsFull);
        // Physically the robot can leave more than fits; the record is never
        // refused for being inconvenient, and the location simply reads over.
        Assert.True(Location_(capacity: 3, units: 4).IsFull);
    }

    [Fact]
    public void An_empty_location_reports_itself_empty()
    {
        Assert.True(Location_(capacity: 3).IsEmpty);
        Assert.False(Location_(capacity: 3, units: 1).IsEmpty);
    }
}
