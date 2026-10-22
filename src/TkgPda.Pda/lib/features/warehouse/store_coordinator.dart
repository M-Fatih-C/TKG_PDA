import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import '../../core/errors/api_problem.dart';
import '../../data/models/store_location.dart';
import '../../data/repositories/pda_repository.dart';
import 'warehouse_notification_coordinator.dart';

/// Server-owned state for the finished-goods store.
///
/// The tablet never records an arrival: a container appears when the transport
/// that carried it completes. This only reports what the operator physically
/// took away, and always with the row version the screen was showing.
class StoreCoordinator extends ChangeNotifier {
  StoreCoordinator(this.repository, {this.notificationCoordinator});

  final PdaDataSource repository;
  final WarehouseNotificationCoordinator? notificationCoordinator;

  /// The customer asked for a 5-10 second refresh.
  static const pollInterval = Duration(seconds: 6);

  List<StoreLocation> locations = const [];
  bool loading = false;
  bool inFlight = false;
  String? error;
  DateTime? lastSuccessfulRefresh;

  /// Containers that arrived since the operator last looked. Cleared only by an
  /// explicit acknowledgement, so a later poll cannot swallow one.
  Set<String> unseenUnitIds = const {};

  Timer? _timer;
  bool _disposed = false;
  bool _primed = false;
  int _generation = 0;
  final Set<String> _busy = {};

  bool isBusy(String id) => _busy.contains(id);
  bool get hasUnseenUnits => unseenUnitIds.isNotEmpty;

  int get totalUnits =>
      locations.fold(0, (sum, location) => sum + location.unitCount);

  /// Locations whose stated capacity is occupied by finished product waiting
  /// for warehouse pickup. EMPTY pallets are robot pickup sources, so they do
  /// not produce a "clear this area" warning.
  List<StoreLocation> get fullLocations => locations
      .where((location) => location.isFullOfFinishedProducts)
      .toList(growable: false);

  List<StoreLocation> get nearlyFullLocations => locations
      .where((location) => location.isNearlyFullOfFinishedProducts)
      .toList(growable: false);

  void start() {
    _stopTimer();
    unawaited(refresh());
    _timer = Timer.periodic(pollInterval, (_) {
      if (!inFlight) unawaited(refresh(silent: true));
    });
  }

  void stop() {
    _stopTimer();
    _generation++;
    locations = const [];
    unseenUnitIds = const {};
    _primed = false;
    error = null;
    _notify();
  }

  Future<void> refresh({bool silent = false, bool keepError = false}) async {
    final generation = ++_generation;
    if (!silent) {
      loading = true;
      _notify();
    }

    try {
      final loaded = await repository.storeLocations();
      if (generation != _generation || _disposed) return;

      final live = {
        for (final location in loaded)
          for (final unit in location.units) unit.id,
      };

      // The first load is what was already standing there, not new arrivals.
      if (_primed) {
        final known = {
          for (final location in locations)
            for (final unit in location.units) unit.id,
        };
        final arrived = live.difference(known);
        if (arrived.isNotEmpty) {
          unseenUnitIds = {...unseenUnitIds, ...arrived};
          if (notificationCoordinator != null) {
            for (final unitId in arrived) {
              for (final location in loaded) {
                final unit = location.units
                    .where((u) => u.id == unitId)
                    .firstOrNull;
                if (unit != null) {
                  notificationCoordinator!.notifyDelivery(
                    unitId: unit.id,
                    locationName: location.displayName,
                    partCode: unit.partCode ?? 'Parça',
                    machineCode: unit.machineCode,
                    requestNo: unit.requestNo,
                  );
                  break;
                }
              }
            }
          } else {
            _alert();
          }
        }
      }

      locations = loaded;
      lastSuccessfulRefresh = DateTime.now();

      // Check capacity warning for store locations with defined capacity
      if (notificationCoordinator != null) {
        for (final loc in fullLocations) {
          notificationCoordinator!.notifyStoreCapacity(
            locationId: loc.id,
            locationName: loc.displayName,
            unitCount: loc.unitCount,
            capacity: loc.capacity ?? loc.unitCount,
          );
        }
      }

      unseenUnitIds = unseenUnitIds.intersection(live);
      _primed = true;
      if (!keepError) error = null;
    } on ConnectivityException {
      if (generation == _generation) {
        error = 'Bağlantı yok — depo listesi yenilenemedi.';
      }
    } on ApiProblem catch (problem) {
      if (generation == _generation) error = problem.operatorMessage;
    } finally {
      if (generation == _generation && !_disposed) {
        loading = false;
        _notify();
      }
    }
  }

  void acknowledgeArrivals() {
    if (unseenUnitIds.isEmpty) return;
    unseenUnitIds = const {};
    _notify();
  }

  /// The operator states they physically removed this container.
  Future<bool> removeUnit(StoreUnit unit) => _mutate(unit.id, () async {
    await repository.removeStoreUnit(
      unitId: unit.id,
      expectedRowVersion: unit.rowVersion,
    );
  });

  final Map<String, String> _emptyContainerKeys = {};

  /// Records an empty container standing in a vacant area. This is not inferred
  /// from the absence of a finished product.
  Future<bool> recordEmptyContainer(StoreLocation location) {
    final key = _emptyContainerKeys.putIfAbsent(
      location.id,
      () =>
          'empty-container:${location.id}:${DateTime.now().microsecondsSinceEpoch}',
    );
    return _mutate(location.id, () async {
      await repository.recordEmptyContainer(
        locationId: location.id,
        idempotencyKey: key,
      );
      _emptyContainerKeys.remove(location.id);
    });
  }

  /// Everything standing in one location left together, the lorry case.
  Future<bool> clearLocation(StoreLocation location) =>
      _mutate(location.id, () async {
        await repository.clearStoreLocation(location.id);
      });

  Future<bool> _mutate(String busyId, Future<void> Function() action) async {
    if (inFlight || _busy.contains(busyId)) return false;
    inFlight = true;
    _busy.add(busyId);
    error = null;
    _notify();
    var succeeded = false;
    try {
      await action();
      succeeded = true;
    } on ConnectivityException {
      error = 'Bağlantı yok — işlem sunucuya ulaşmadı. Yeniden deneyin.';
    } on AmbiguousMutationException {
      error =
          'İşlemin sunucuya ulaştığı doğrulanamadı. Listeyi yenileyip kontrol edin.';
    } on ApiProblem catch (problem) {
      error = problem.operatorMessage;
    } finally {
      inFlight = false;
      _busy.remove(busyId);
      _notify();
    }

    // Re-read rather than patch: only the server knows what a location holds.
    // A failed action keeps its message so the refresh cannot hide the reason.
    await refresh(silent: true, keepError: !succeeded);
    return succeeded;
  }

  void _alert() {
    // Built-in feedback: no audio dependency, and it still reaches an operator
    // whose tablet has its media volume muted.
    unawaited(SystemSound.play(SystemSoundType.alert));
    unawaited(HapticFeedback.mediumImpact());
  }

  void _stopTimer() {
    _timer?.cancel();
    _timer = null;
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _stopTimer();
    super.dispose();
  }
}
