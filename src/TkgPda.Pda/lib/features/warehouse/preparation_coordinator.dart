import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:uuid/uuid.dart';

import '../../core/errors/api_problem.dart';
import '../../data/models/preparation_area.dart';
import '../../data/repositories/pda_repository.dart';

import 'warehouse_notification_coordinator.dart';

/// Server-owned state for the four shared preparation areas.
///
/// Every decision stays on the server: this holds what the last read reported
/// and sends the operator's actions back with the row version the screen was
/// showing. It never marks an area free on its own, never infers that a robot
/// collected a pallet, and never fills in a timestamp the server omitted.
class PreparationCoordinator extends ChangeNotifier {
  PreparationCoordinator(
    this.repository, {
    Uuid? uuid,
    this.notificationCoordinator,
  }) : _uuid = uuid ?? const Uuid();

  final PdaDataSource repository;
  final Uuid _uuid;
  final WarehouseNotificationCoordinator? notificationCoordinator;

  /// The tablet polls; the customer asked for 5-10 seconds.
  static const pollInterval = Duration(seconds: 6);

  List<PreparationArea> areas = const [];
  List<WarehouseDemand> demands = const [];
  bool loading = false;
  bool inFlight = false;
  String? error;
  DateTime? lastSuccessfulRefresh;

  /// Demands that appeared since the operator last looked. Drives the alert,
  /// and is cleared only by an explicit acknowledgement so a poll cannot
  /// silently swallow one.
  Set<String> unseenDemandIds = const {};

  Timer? _timer;
  bool _disposed = false;
  bool _primed = false;
  int _generation = 0;

  bool get hasUnseenDemands => unseenDemandIds.isNotEmpty;
  int get freeAreaCount => areas.where((area) => area.isFree).length;

  /// True once every area holds something. The operator is told before the
  /// last one goes, not after.
  bool get isNearlyFull => areas.isNotEmpty && freeAreaCount <= 1;

  bool isBusy(String id) => _busy.contains(id);
  final Set<String> _busy = {};

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
    areas = const [];
    demands = const [];
    unseenDemandIds = const {};
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
      final loadedAreas = await repository.preparationAreas();
      final loadedDemands = await repository.warehouseDemands();
      if (generation != _generation || _disposed) return;

      // The first load is not "new work"; it is simply what was already there.
      if (_primed) {
        final known = demands.map((demand) => demand.requestId).toSet();
        final arrived = loadedDemands
            .where((demand) => !known.contains(demand.requestId))
            .toList(growable: false);
        if (arrived.isNotEmpty) {
          unseenDemandIds = {
            ...unseenDemandIds,
            ...arrived.map((demand) => demand.requestId),
          };
          if (notificationCoordinator != null) {
            for (final demand in arrived) {
              notificationCoordinator!.notifyDemand(
                requestId: demand.requestId,
                machineCode: demand.machineCode,
                partCode: demand.partCode ?? 'Parça',
                needsMaterial: demand.needsMaterial,
                requestNo: demand.requestNo,
              );
            }
          } else {
            _alert(SystemSoundType.alert);
          }
        }
      }

      areas = loadedAreas;
      demands = loadedDemands;
      lastSuccessfulRefresh = DateTime.now();

      // Report capacity warnings to the notification coordinator if available
      if (isNearlyFull) {
        notificationCoordinator?.notifyPreparationCapacity(
          freeAreaCount: freeAreaCount,
          totalAreas: areas.length,
        );
      } else {
        notificationCoordinator?.clearPreparationCapacityAlert();
      }

      // Drop acknowledgements for demands that have since gone away, so the
      // badge cannot count work that no longer exists.
      final live = loadedDemands.map((demand) => demand.requestId).toSet();
      unseenDemandIds = unseenDemandIds.intersection(live);
      _primed = true;
      // A successful read does not mean the last action succeeded. Clearing the
      // message here would hide why a placement or confirmation was refused.
      if (!keepError) error = null;
    } on ConnectivityException {
      if (generation == _generation) {
        error = 'Bağlantı yok — alan ve talep listesi yenilenemedi.';
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

  void acknowledgeDemands() {
    if (unseenDemandIds.isEmpty) return;
    unseenDemandIds = const {};
    _notify();
  }

  /// Stages an empty container with no demand behind it. Allowed by the
  /// customer's rule: empty stock may be put out ahead of any request.
  Future<bool> stageEmptyContainer({
    required PreparationArea area,
    required String containerTypeId,
  }) => _mutate(area.id, () async {
    await repository.placePallet(
      areaId: area.id,
      load: 'EMPTY',
      containerTypeId: containerTypeId,
      idempotencyKey: _uuid.v4(),
    );
  });

  /// Prepares material for one demand. The part and container come from the
  /// demand the server published, never from anything typed on the tablet.
  Future<bool> prepareForDemand({
    required PreparationArea area,
    required WarehouseDemand demand,
  }) {
    final partId = demand.partId;
    final containerTypeId = demand.containerTypeId;
    if (partId == null || containerTypeId == null) {
      error =
          'Talebin parça veya taşıma kabı bilgisi eksik; hazırlık eşleştirilemez.';
      _notify();
      return Future.value(false);
    }

    return _mutate(area.id, () async {
      await repository.placePallet(
        areaId: area.id,
        load: demand.requestedLoad,
        containerTypeId: containerTypeId,
        partId: demand.requestedLoad == 'FULL' ? partId : null,
        transportRequestId: demand.requestedLoad == 'FULL'
            ? demand.requestId
            : null,
        idempotencyKey: _uuid.v4(),
      );
    });
  }

  Future<bool> markReady(PreparationPallet pallet) =>
      _mutate(pallet.id, () async {
        await repository.markPalletReady(
          palletId: pallet.id,
          expectedRowVersion: pallet.rowVersion,
        );
      });

  /// [reason] is COLLECTED when the operator confirms the robot physically took
  /// the pallet, WITHDRAWN when the preparation is being undone.
  Future<bool> release(PreparationPallet pallet, String reason) =>
      _mutate(pallet.id, () async {
        await repository.releasePreparationArea(
          palletId: pallet.id,
          expectedRowVersion: pallet.rowVersion,
          reason: reason,
        );
      });

  /// Runs one mutation, then re-reads rather than patching local state: the
  /// server is the only thing that knows what an area now holds.
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
      // Deliberately not retried here: the placement key made the first attempt
      // safe to repeat, but only the operator decides to repeat it.
      error =
          'İşlemin sunucuya ulaştığı doğrulanamadı. Listeyi yenileyip kontrol edin.';
    } on ApiProblem catch (problem) {
      error = problem.operatorMessage;
    } finally {
      inFlight = false;
      _busy.remove(busyId);
      _notify();
    }

    await refresh(silent: true, keepError: !succeeded);
    return succeeded;
  }

  void _alert(SystemSoundType sound) {
    // Built-in system feedback: no audio dependency, and it still works on a
    // tablet whose media volume is muted.
    unawaited(SystemSound.play(sound));
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
