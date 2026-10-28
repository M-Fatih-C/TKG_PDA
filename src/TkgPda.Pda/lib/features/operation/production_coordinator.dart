import 'package:flutter/foundation.dart';
import 'package:uuid/uuid.dart';
import '../../core/errors/api_problem.dart';
import '../../data/models/pda_models.dart';
import '../../data/repositories/pda_repository.dart';

/// Server-owned production state. Never starts production from a GET or lifecycle
/// callback; uncertain POSTs retain their exact target/key until explicit retry.
class ProductionCoordinator extends ChangeNotifier {
  ProductionCoordinator(this.repository);
  final PdaDataSource repository;
  ProductionSession? active;
  String? machineId;
  bool verified = false, loading = false, inFlight = false;
  String? error;
  ProductionStartCommand? _start;
  String? _startKey, _endId, _endKey;
  int _generation = 0;
  bool _disposed = false;
  bool get hasPending => _start != null || _endId != null;
  String get pendingLabel =>
      _endId != null ? 'Üretim bitişi' : 'Üretim başlangıcı';

  void reset(String? machine) {
    _generation++;
    machineId = machine;
    active = null;
    verified = loading = inFlight = false;
    _start = null;
    _startKey = _endId = _endKey = error = null;
  }

  Future<void> refresh() async {
    final machine = machineId;
    if (machine == null || inFlight) return;
    final generation = ++_generation;
    loading = true;
    // Keep the last verified view stable during background polling. Commands
    // carry the session identity and are checked under the server machine lock;
    // foreground resume has a separate mutation gate. A failed read fails closed.
    _notify();
    try {
      final value = await repository.activeProduction(machine);
      if (!_current(generation)) return;
      if (value != null && (value.machineId != machine || !value.isActive)) {
        throw const FormatException('Unexpected active production');
      }
      active = value;
      verified = true;
      if (!hasPending) error = null;
    } on Object catch (failure) {
      if (_current(generation)) {
        verified = false;
        error = _message(failure);
      }
    } finally {
      if (_current(generation)) {
        loading = false;
        _notify();
      }
    }
  }

  Future<void> start(ProductionStartCommand command) async {
    if (inFlight ||
        hasPending ||
        !verified ||
        active != null ||
        command.machineId != machineId) {
      return;
    }
    _start = command;
    _startKey = 'production-start:${const Uuid().v4()}';
    await retry();
  }

  Future<void> end() async {
    if (inFlight || hasPending || !verified || active == null) return;
    _endId = active!.id;
    _endKey ??= 'production-end:${const Uuid().v4()}';
    await retry();
  }

  Future<void> retry() async {
    final machine = machineId;
    if (inFlight || !hasPending || machine == null) return;
    final generation = ++_generation;
    inFlight = true;
    loading = false;
    verified = false;
    error = null;
    _notify();
    try {
      final result = _start != null
          ? await repository.startProduction(_start!, _startKey!)
          : await repository.endProduction(machine, _endId!, _endKey!);
      if (!_current(generation)) return;
      if (result.machineId != machine ||
          (_endId != null &&
              (result.id != _endId || (result.isActive && !result.isEnding))) ||
          (_start != null &&
              (result.variantId != _start!.variantId ||
                  result.productGroupId != _start!.productGroupId ||
                  result.mainPartId != _start!.mainPartId))) {
        throw const FormatException('Unexpected production response');
      }
      _start = null;
      _startKey = _endId = _endKey = null;
      active = result.isActive ? result : null;
      verified = true;
    } on ApiProblem catch (failure) {
      if (!_current(generation)) return;
      error = failure.code == 'ACTIVE_TRANSPORT_EXISTS'
          ? 'Açık taşıma talepleri var. Tamamlanmalarını bekleyin veya ayrıca iptal edin.'
          : failure.operatorMessage;
      if (failure.status < 500) {
        _start = null;
        _startKey = _endId = _endKey = null;
      }
    } on Object catch (failure) {
      if (_current(generation)) error = _message(failure);
    } finally {
      if (_current(generation)) {
        inFlight = false;
        _notify();
      }
    }
  }

  static String _message(Object failure) => failure is ApiProblem
      ? failure.operatorMessage
      : 'Üretim durumu doğrulanamadı. Bağlantıyı kontrol edip yeniden deneyin.';
  bool _current(int generation) => !_disposed && generation == _generation;
  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _generation++;
    super.dispose();
  }
}
