import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:uuid/uuid.dart';

import '../core/auth/session_memory.dart';
import '../core/errors/api_problem.dart';
import '../core/storage/device_code_store.dart';
import '../data/models/pda_models.dart';
import '../data/repositories/pda_repository.dart';
import '../features/transport/submission_coordinator.dart';
import '../features/operation/production_coordinator.dart';
import '../features/warehouse/internal_notification_client.dart';
import '../features/warehouse/preparation_coordinator.dart';
import '../features/warehouse/store_coordinator.dart';
import '../features/warehouse/warehouse_notification_coordinator.dart';

enum AppStage {
  starting,
  deviceSetup,
  login,
  machines,
  operation,
  tasks,
  warehousePreparation,
  warehouseHome,
  preparationAreas,
  finishedGoodsStore,
}

enum UiNoticeSeverity { success, info, warning, error }

class UiNotice {
  const UiNotice(this.message, this.severity);

  final String message;
  final UiNoticeSeverity severity;

  bool get persistent =>
      severity == UiNoticeSeverity.warning ||
      severity == UiNoticeSeverity.error;
}

const _semiFinishedWarehouseRoles = {
  'ADMIN',
  'SUPERVISOR',
  'SEMI_FINISHED_WAREHOUSE_OPERATOR',
  'WAREHOUSE_OPERATOR',
};

const _finishedGoodsWarehouseRoles = {
  'ADMIN',
  'SUPERVISOR',
  'FINISHED_GOODS_WAREHOUSE_OPERATOR',
  'WAREHOUSE_OPERATOR',
};

/// WP-03B: the part the operator has chosen as the effective part for
/// compatible-variant resolution — either a main part itself or one of its
/// Related/Alt parts.
class EffectivePartSelection {
  const EffectivePartSelection({
    required this.partId,
    required this.partCode,
    required this.isMain,
  });

  final String partId;
  final String partCode;
  final bool isMain;
}

/// Customer UI — Multi-Part Transport Batch: one effective part added to the
/// multi-select cart. [command] stays null until the operator has walked
/// this item through the existing part-first -> compatible-variant ->
/// operation flow and confirmed an action for it.
class BatchCartItem {
  BatchCartItem({
    required this.cartItemId,
    required this.mainPart,
    required this.effectivePart,
  });

  final String cartItemId;
  final MachineMainPartOption mainPart;
  final EffectivePartSelection effectivePart;
  ContainerActionBatchItemCommand? command;

  /// Display-only snapshot captured at confirm time. The physical slot stays
  /// backend-resolved, so it is shown as automatic until a request exists.
  /// Avoid re-deriving the variant later because another cart item may have
  /// switched the machine to a different active variant/layout by then.
  String? slotDisplayName;
  String? actionCode;
  String? variantCode;
  String? variantId;

  bool get isConfigured => command != null;
}

class AppController extends ChangeNotifier {
  factory AppController({
    required PdaDataSource repository,
    required DeviceCodeStore deviceCodeStore,
    required SessionMemory session,
    SubmissionCoordinator? submissionCoordinator,
    WarehouseNotificationCoordinator? warehouseNotificationCoordinator,
    bool enablePolling = true,
    bool enableProductionWorkflow = true,
    bool? enableHeartbeat,
    InternalNotificationClient? notificationClient,
  }) {
    final notifications =
        warehouseNotificationCoordinator ?? WarehouseNotificationCoordinator();
    return AppController._(
      repository: repository,
      deviceCodeStore: deviceCodeStore,
      session: session,
      submissionCoordinator: submissionCoordinator,
      notifications: notifications,
      enablePolling: enablePolling,
      enableProductionWorkflow: enableProductionWorkflow,
      enableHeartbeat: enableHeartbeat,
      notificationClient: notificationClient,
    );
  }

  AppController._({
    required PdaDataSource repository,
    required DeviceCodeStore deviceCodeStore,
    required SessionMemory session,
    required WarehouseNotificationCoordinator notifications,
    SubmissionCoordinator? submissionCoordinator,
    bool enablePolling = true,
    bool enableProductionWorkflow = true,
    bool? enableHeartbeat,
    InternalNotificationClient? notificationClient,
  }) : _repository = repository,
       _deviceCodeStore = deviceCodeStore,
       _session = session,
       _submission = submissionCoordinator ?? SubmissionCoordinator(),
       _enablePolling = enablePolling,
       _enableProductionWorkflow = enableProductionWorkflow,
       _enableHeartbeat = enableHeartbeat ?? enablePolling,
       _notificationClient = notificationClient,
       warehouseNotifications = notifications,
       production = ProductionCoordinator(repository),
       preparation = PreparationCoordinator(
         repository,
         notificationCoordinator: notifications,
       ),
       store = StoreCoordinator(
         repository,
         notificationCoordinator: notifications,
       ) {
    production.addListener(notifyListeners);
    warehouseNotifications.addListener(notifyListeners);
    _notificationClient?.onDelivery = _onWarehouseDelivery;
  }

  final PdaDataSource _repository;
  final DeviceCodeStore _deviceCodeStore;
  final SessionMemory _session;
  final SubmissionCoordinator _submission;
  final bool _enablePolling;
  final bool _enableProductionWorkflow;
  final bool _enableHeartbeat;
  final InternalNotificationClient? _notificationClient;
  final WarehouseNotificationCoordinator warehouseNotifications;
  final ProductionCoordinator production;
  int _machineGeneration = 0;
  String? _restoredProductionId;
  int? _restoringProductionGeneration;
  String? _productionContextError;
  DateTime? _lastSuccessfulQueueRefresh;

  DateTime? get lastSuccessfulQueueRefresh => _lastSuccessfulQueueRefresh;

  bool get usesProductionWorkflow =>
      _enableProductionWorkflow &&
      (user?.roles.any(
            (role) =>
                const {'ADMIN', 'SUPERVISOR', 'LINE_OPERATOR'}.contains(role),
          ) ??
          false);
  bool get productionTransportReady =>
      !usesProductionWorkflow ||
      (production.verified &&
          !production.hasPending &&
          production.active != null &&
          _productionContextError == null &&
          _selectedMainPart?.partId == production.active!.mainPartId &&
          _context?.activeVariantId == production.active!.variantId &&
          _resolvedCompatibleVariant?.productGroupId ==
              production.active!.productGroupId);
  String? get productionContextError => _productionContextError;
  bool get canStartProduction =>
      usesProductionWorkflow &&
      mutationEnabled &&
      production.verified &&
      production.active == null &&
      !production.hasPending &&
      partSelectionReady &&
      (_context?.machine.permissions.canOperate ?? false) &&
      (_context?.activeTransportRequests.isEmpty ?? false) &&
      _batchCart.isEmpty &&
      !hasAmbiguousSubmission &&
      !hasAmbiguousBatchSubmission;
  bool get canEndProduction =>
      usesProductionWorkflow &&
      mutationEnabled &&
      production.verified &&
      production.active != null &&
      production.active?.isEnding != true &&
      !production.hasPending &&
      (_context?.machine.permissions.canOperate ?? false) &&
      (_context?.activeTransportRequests.isEmpty ?? false) &&
      _batchCart.isEmpty &&
      !hasAmbiguousSubmission &&
      !hasAmbiguousBatchSubmission;

  AppStage _stage = AppStage.starting;
  String? _deviceCode;
  List<MachineSummary> _machines = const [];
  MachineSummary? _selectedMachine;
  OperationalContext? _context;
  bool _busy = false;
  bool _contextLoading = false;
  bool _offline = false;
  bool _foreground = true;
  bool _resumeValidationPending = false;
  ApiProblem? _problem;
  UiNotice? _notice;
  String? _submittingOperation;
  Timer? _pollTimer;
  Timer? _queuePollTimer;
  TransportCommand? _ambiguousCommand;
  ContainerPosition? _ambiguousContainerPosition;
  String? _ambiguousContainerAction;
  PendingOperation? _activePendingOperation;
  PendingOperation? _ambiguousPendingOperation;

  // WP-03B: part-first selection state (machine-level, independent of the
  // currently active variant until a compatible variant is resolved).
  List<MachineMainPartOption> _partOptions = const [];
  bool _partOptionsLoading = false;
  String? _partOptionsError;
  MachineMainPartOption? _selectedMainPart;
  EffectivePartSelection? _selectedEffectivePart;
  List<CompatibleVariant>? _compatibleVariants;
  bool _compatibleVariantsLoading = false;
  String? _compatibleVariantsError;
  CompatibleVariant? _resolvedCompatibleVariant;
  int _partResolutionGeneration = 0;

  // Customer UI — Multi-Part Transport Batch: cart state, independent of the
  // single-item part-first fields above (which drive whichever cart item is
  // currently being configured).
  bool _batchMode = false;
  final List<MachineRelatedPart> _selectedAltParts = [];
  final Map<String, String> _altDemandKeys = {};
  final Set<String> _altDemandInFlight = {};
  String? _altBatchKey;
  final List<BatchCartItem> _batchCart = [];
  int _batchConfiguringIndex = -1;
  final BatchSubmissionCoordinator _batchSubmission =
      BatchSubmissionCoordinator();
  final Uuid _cartIdFactory = const Uuid();

  // Customer UI — Warehouse Preparation Console.
  List<WarehousePreparationRequest> _warehouseRequests = const [];
  bool _warehouseLoading = false;
  String? _warehouseError;
  final Set<String> _warehouseMarkingReady = {};

  // Customer Monitoring — Robot Alarm Visibility: kept independent of
  // _offline/_problem above, since an RDS-unavailable alarm read must never
  // be shown to the operator as "no active alarm".
  RobotAlarmSummary? _robotAlarmSummary;
  String? _robotAlarmError;
  List<RobotTelemetry> _robots = const [];

  // WP-33: backend-derived logical positions. Physical slot identities never
  // enter Flutter state.
  List<ContainerPosition> _containerPositions = const [];
  bool _containerPositionsLoading = false;
  String? _containerPositionsError;
  final Map<String, String> _containerActionKeys = {};
  final Set<String> _submittingContainerActions = {};
  String? _removeAllKey;
  bool _removeAllSubmitting = false;
  bool _removeAllAmbiguous = false;

  // WP-33: fleet-wide, business-only Operator queue.
  OperatorQueuePage? _operatorQueue;
  bool _operatorQueueLoading = false;
  String? _operatorQueueError;
  bool _operatorQueueTerminal = false;
  AppStage? _taskReturnStage;

  String? _containerBatchKey;
  String? _containerBatchProductionId;
  String? _removeAllProductionId;
  bool _containerBatchAmbiguous = false;

  AppStage get stage => _stage;
  String? get deviceCode => _deviceCode;
  PdaDevice? get device => _session.device;
  AuthenticatedUser? get user => _session.user;
  bool get isAuthenticated => _session.isAuthenticated;
  bool get isAdminOrSupervisor =>
      (user?.roles ?? const []).any((r) => r == 'ADMIN' || r == 'SUPERVISOR');
  bool get isLineOperator => (user?.roles ?? const []).any(
    (r) => r == 'LINE_OPERATOR' || r == 'OPERATOR',
  );
  bool get isBoundLineTerminalOperator =>
      isLineOperator && !isAdminOrSupervisor && device?.machineId != null;
  List<MachineSummary> get machines => _machines;
  MachineSummary? get selectedMachine => _selectedMachine;
  OperationalContext? get context => _context;
  bool get busy => _busy;
  bool get offline => _offline;
  ApiProblem? get problem => _problem;
  String? get notice => _notice?.message;
  UiNotice? get uiNotice => _notice;

  void acknowledgeTransientNotice() {
    if (_notice != null && !_notice!.persistent) {
      _notice = null;
      notifyListeners();
    }
  }

  String? get submittingOperation => _submittingOperation;
  PendingOperation? get activePendingOperation => _activePendingOperation;
  PendingOperation? get ambiguousPendingOperation => _ambiguousPendingOperation;

  bool get hasAmbiguousSubmission =>
      _submission.hasAmbiguousRequest ||
      _ambiguousPendingOperation != null ||
      _ambiguousContainerPosition != null ||
      _removeAllAmbiguous;

  /// Authoritative check for any safety or business mutation currently in flight.
  /// Used to lock context changes and prevent state contamination.
  bool get isMutationInFlight =>
      production.inFlight ||
      _busy ||
      _submittingOperation != null ||
      _submittingContainerActions.isNotEmpty ||
      _removeAllSubmitting ||
      _warehouseMarkingReady.isNotEmpty ||
      _batchSubmission.inFlight ||
      _submission.inFlight ||
      _activePendingOperation != null;

  /// Central client-side mutation gate. Unified defense-in-depth policy
  /// ensuring mutations are executed only under valid, non-conflicting states.
  MutationGate get mutationGate {
    if (_offline) {
      return const MutationGate(
        canMutate: false,
        reason: MutationGateReason.offline,
        message: 'Bağlantı yok',
      );
    }
    if (isMutationInFlight) {
      return const MutationGate(
        canMutate: false,
        reason: MutationGateReason.mutationPending,
        message: 'İşlem devam ediyor',
      );
    }
    if (!_session.isAuthenticated || _deviceCode == null) {
      return const MutationGate(
        canMutate: false,
        reason: MutationGateReason.unauthenticated,
        message: 'Oturum geçersiz',
      );
    }
    if (_resumeValidationPending) {
      return const MutationGate(
        canMutate: false,
        reason: MutationGateReason.resumePending,
        message: 'Durum doğrulanıyor',
      );
    }
    return const MutationGate(
      canMutate: true,
      reason: MutationGateReason.ready,
      message: null,
    );
  }

  bool get mutationEnabled => mutationGate.canMutate;
  bool get pollingActive => _pollTimer?.isActive ?? false;
  bool get resumeValidationPending => _resumeValidationPending;
  RobotAlarmSummary? get robotAlarmSummary => _robotAlarmSummary;
  String? get robotAlarmError => _robotAlarmError;
  List<RobotTelemetry> get robots => _robots;
  List<ContainerPosition> get containerPositions => _containerPositions;
  bool get containerPositionsLoading => _containerPositionsLoading;
  String? get containerPositionsError => _containerPositionsError;
  bool get removeAllSubmitting => _removeAllSubmitting;
  bool isSubmittingContainerAction(int positionNumber, String action) =>
      _submittingContainerActions.contains('$positionNumber:$action');
  OperatorQueuePage? get operatorQueue => _operatorQueue;
  bool get operatorQueueLoading => _operatorQueueLoading;
  String? get operatorQueueError => _operatorQueueError;
  bool get operatorQueueTerminal => _operatorQueueTerminal;

  List<MachineMainPartOption> get partOptions => _partOptions;
  bool get partOptionsLoading => _partOptionsLoading;
  String? get partOptionsError => _partOptionsError;
  MachineMainPartOption? get selectedMainPart => _selectedMainPart;
  bool get canChangeEffectivePart =>
      !isMutationInFlight &&
      !hasAmbiguousSubmission &&
      !hasAmbiguousBatchSubmission &&
      (!usesProductionWorkflow ||
          (production.verified && !production.hasPending));
  bool get canChangePartSelection =>
      canChangeEffectivePart &&
      (!usesProductionWorkflow || production.active == null);
  EffectivePartSelection? get selectedEffectivePart => _selectedEffectivePart;
  List<CompatibleVariant>? get compatibleVariants => _compatibleVariants;
  bool get compatibleVariantsLoading => _compatibleVariantsLoading;
  String? get compatibleVariantsError => _compatibleVariantsError;
  CompatibleVariant? get resolvedCompatibleVariant =>
      _resolvedCompatibleVariant;

  // Customer UI — Multi-Part Transport Batch.
  bool get batchMode => _batchMode;
  List<MachineRelatedPart> get selectedAltParts =>
      List.unmodifiable(_selectedAltParts);
  bool isAltPartSelected(String partId) =>
      _selectedAltParts.any((part) => part.partId == partId);
  List<BatchCartItem> get batchCart => List.unmodifiable(_batchCart);
  bool get isConfiguringBatchItem =>
      _batchConfiguringIndex >= 0 && _batchConfiguringIndex < _batchCart.length;
  BatchCartItem? get currentBatchItem =>
      isConfiguringBatchItem ? _batchCart[_batchConfiguringIndex] : null;

  /// 1-based position of the item currently being configured, for "N/M" UI.
  int get currentBatchItemPosition => _batchConfiguringIndex + 1;
  bool get canReviewBatch =>
      _batchCart.isNotEmpty && _batchCart.every((item) => item.isConfigured);
  bool get hasAmbiguousBatchSubmission => _containerBatchAmbiguous;
  String? get batchTargetVariantId => _firstOrNull(
    _batchCart
        .where((item) => item.isConfigured)
        .map((item) => item.variantId)
        .whereType<String>(),
  );
  String? get batchTargetVariantCode => _firstOrNull(
    _batchCart
        .where((item) => item.isConfigured)
        .map((item) => item.variantCode)
        .whereType<String>(),
  );

  // Customer UI — Warehouse Capabilities.
  bool get canAccessSemiFinishedWarehouse =>
      (user?.roles ?? const []).any(_semiFinishedWarehouseRoles.contains);
  bool get canAccessFinishedGoodsWarehouse =>
      (user?.roles ?? const []).any(_finishedGoodsWarehouseRoles.contains);
  bool get canAccessAnyWarehouse =>
      canAccessSemiFinishedWarehouse || canAccessFinishedGoodsWarehouse;
  bool get canAccessWarehousePreparation => canAccessSemiFinishedWarehouse;
  List<WarehousePreparationRequest> get warehousePreparationRequests =>
      _warehouseRequests;
  bool get warehousePreparationLoading => _warehouseLoading;
  String? get warehousePreparationError => _warehouseError;
  bool isMarkingReady(String requestId) =>
      _warehouseMarkingReady.contains(requestId);

  /// The final Operator action surface becomes available only after the
  /// effective part and its compatible active variant are both proven.
  bool get partSelectionReady =>
      _selectedEffectivePart != null &&
      _resolvedCompatibleVariant != null &&
      _context?.activeVariantId == _resolvedCompatibleVariant!.variantId;
  bool get transportActionsReady =>
      partSelectionReady && productionTransportReady;

  /// Mamul Kasa follows the active production main product. Alt-part
  /// selection does not make this true or false.
  bool get mamulActionsReady {
    final active = production.active;
    if (active == null) return transportActionsReady;
    return production.verified &&
        !production.hasPending &&
        !active.isEnding &&
        _productionContextError == null &&
        _selectedMainPart?.partId == active.mainPartId &&
        _context?.activeVariantId == active.variantId &&
        _resolvedCompatibleVariant?.productGroupId == active.productGroupId &&
        _context?.activeLayoutId != null;
  }

  EffectivePartSelection? get _mamulDemandPart {
    final active = production.active;
    if (active == null) return _selectedEffectivePart;
    final main = _selectedMainPart;
    if (main == null || main.partId != active.mainPartId) return null;
    return EffectivePartSelection(
      partId: active.mainPartId,
      partCode: main.partCode,
      isMain: true,
    );
  }

  bool operationInProgress(String operation) =>
      _operationInProgress(operation, _selectedEffectivePart?.partId);

  Future<void> initialize() async {
    _deviceCode = await _deviceCodeStore.read();
    _stage = _deviceCode == null ? AppStage.deviceSetup : AppStage.login;
    notifyListeners();
  }

  Future<void> saveDeviceCode(String value) async {
    final normalized = value.trim();
    if (normalized.isEmpty || normalized.length > 120) {
      throw const FormatException('Geçerli bir cihaz kodu girin.');
    }
    _stopHeartbeat();
    await _releaseServerSessionBestEffort();
    _session.clear();
    _clearOperationalState();
    _submission.clear();
    _ambiguousCommand = null;
    await _deviceCodeStore.write(normalized);
    _deviceCode = normalized;
    _stage = AppStage.login;
    _clearFeedback();
    notifyListeners();
  }

  Future<void> beginDeviceCodeChange() async {
    if (isMutationInFlight) {
      _notice = UiNotice(
        'İşlem devam ederken cihaz kodu değiştirilemez.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    _stopHeartbeat();
    await _releaseServerSessionBestEffort();
    _session.clear();
    _clearOperationalState();
    _submission.clear();
    _ambiguousCommand = null;
    _stage = AppStage.deviceSetup;
    _clearFeedback();
    notifyListeners();
  }

  Future<bool> login(String employeeNumber) async {
    if (_deviceCode == null) {
      _stage = AppStage.deviceSetup;
      notifyListeners();
      return false;
    }
    _setBusy(true);
    _clearFeedback();
    var serverSessionEstablished = false;
    try {
      final session = await _repository.login(employeeNumber.trim());
      _session.establish(session);
      serverSessionEstablished = true;
      final roles = session.user.roles;
      final isAdminOrSupervisor =
          roles.contains('ADMIN') || roles.contains('SUPERVISOR');
      final isLegacyWarehouse = roles.contains('WAREHOUSE_OPERATOR');
      final isSemiFinished = roles.contains('SEMI_FINISHED_WAREHOUSE_OPERATOR');
      final isFinishedGoods = roles.contains(
        'FINISHED_GOODS_WAREHOUSE_OPERATOR',
      );
      final isLineOperator =
          roles.contains('LINE_OPERATOR') || roles.contains('OPERATOR');

      if (!isAdminOrSupervisor && isLineOperator) {
        final boundMachineId = session.device.machineId;
        if (boundMachineId == null || boundMachineId.isEmpty) {
          await _releaseServerSessionBestEffort();
          _stopHeartbeat();
          _session.clear();
          _clearOperationalState();
          _submission.clear();
          _problem = const ApiProblem(
            status: 403,
            code: 'DEVICE_MACHINE_BINDING_REQUIRED',
            detail:
                'Bu terminal henüz bir makineye tanımlanmamış. Lütfen sistem yöneticisine başvurun.',
            correlationId: null,
          );
          _stage = AppStage.login;
          notifyListeners();
          return false;
        }

        await _loadMachines();
        final boundMachine = _machines.cast<MachineSummary?>().firstWhere(
          (m) => m?.id == boundMachineId,
          orElse: () => null,
        );
        if (boundMachine != null) {
          _setBusy(false);
          await selectMachine(boundMachine);
        } else {
          await _releaseServerSessionBestEffort();
          _stopHeartbeat();
          _session.clear();
          _clearOperationalState();
          _submission.clear();
          _problem = const ApiProblem(
            status: 403,
            code: 'DEVICE_MACHINE_BINDING_REQUIRED',
            detail:
                'Terminalin bağlı olduğu makine sistemde bulunamadı veya doğrulanamadı.',
            correlationId: null,
          );
          _stage = AppStage.login;
          notifyListeners();
          return false;
        }
      } else if (isAdminOrSupervisor) {
        await _loadMachines();
        _stage = AppStage.machines;
      } else if (isLegacyWarehouse || (isSemiFinished && isFinishedGoods)) {
        _stage = AppStage.warehouseHome;
      } else if (isSemiFinished && !isFinishedGoods) {
        _stage = AppStage.preparationAreas;
        if (_enablePolling) {
          preparation.start();
        }
      } else if (isFinishedGoods && !isSemiFinished) {
        _stage = AppStage.finishedGoodsStore;
        if (_enablePolling) {
          store.start();
        }
      } else {
        await _loadMachines();
        _stage = AppStage.machines;
      }
      _offline = false;
      _startHeartbeat();
      if (canAccessFinishedGoodsWarehouse) {
        await _startWarehouseNotifications();
      }
      return true;
    } on ApiProblem catch (problem) {
      if (serverSessionEstablished) {
        await _releaseServerSessionBestEffort();
        _stopHeartbeat();
        _session.clear();
        _clearOperationalState();
        _submission.clear();
      }
      _problem = problem;
      return false;
    } on ConnectivityException {
      if (serverSessionEstablished) {
        await _releaseServerSessionBestEffort();
        _stopHeartbeat();
        _session.clear();
        _clearOperationalState();
        _submission.clear();
      }
      _offline = true;
      _notice = UiNotice(
        'Bağlantı yok — giriş yapılamadı.',
        UiNoticeSeverity.warning,
      );
      return false;
    } catch (e) {
      if (serverSessionEstablished) {
        await _releaseServerSessionBestEffort();
        _stopHeartbeat();
        _session.clear();
        _clearOperationalState();
        _submission.clear();
      }
      rethrow;
    } finally {
      _setBusy(false);
    }
  }

  Future<void> _startWarehouseNotifications() async {
    final client = _notificationClient;
    if (client == null || !client.isSupported) return;
    try {
      final registration = await _repository.registerDeviceNotification();
      if (registration.credential.isEmpty) return;
      await client.start(
        credential: registration.credential,
        registrationId: registration.registrationId,
      );
    } catch (_) {
      // A failed registration must not fail the warehouse login.
    }
  }

  void _onWarehouseDelivery(Map<String, String> data) {
    if (data['notificationType'] != 'FINISHED_GOODS_DELIVERY') return;
    final unitId = data['unitId'];
    if (unitId == null || unitId.isEmpty) return;
    warehouseNotifications.notifyDelivery(
      unitId: unitId,
      locationName: data['locationName'] ?? '',
      partCode: data['partCode'] ?? '',
      machineCode: data['machineCode'],
      requestNo: int.tryParse(data['requestNo'] ?? ''),
    );
  }

  Future<void> _releaseServerSessionBestEffort() async {
    if (!_session.isAuthenticated) return;
    await _notificationClient?.stop();
    try {
      await _repository.logout();
    } catch (_) {
      // Best-effort session release: ignore errors so caller can continue cleanup.
    }
  }

  Future<void> logout() async {
    if (isMutationInFlight || production.hasPending) {
      _notice = UiNotice(
        'İşlem devam ederken oturum kapatılamaz.',
        UiNoticeSeverity.warning,
      );
      notifyListeners();
      return;
    }
    _stopHeartbeat();
    await _notificationClient?.stop();
    try {
      await _repository.logout();
    } catch (_) {
      // Network timeout / offline: proceed with local logout.
      // Server lease will expire after configured LeaseSeconds.
    } finally {
      _session.clear();
      _clearOperationalState();
      _submission.clear();
      _ambiguousCommand = null;
      _stage = _deviceCode == null ? AppStage.deviceSetup : AppStage.login;
      _clearFeedback();
      notifyListeners();
    }
  }

  void handleUnauthorized() {
    _stopHeartbeat();
    _session.clear();
    _clearOperationalState();
    _submission.clear();
    _ambiguousCommand = null;
    _problem = const ApiProblem(
      status: 401,
      code: 'SESSION_EXPIRED',
      detail: 'Oturum sona erdi.',
      correlationId: null,
    );
    _stage = _deviceCode == null ? AppStage.deviceSetup : AppStage.login;
    notifyListeners();
  }

  Timer? _heartbeatTimer;

  /// Whether the session heartbeat keepalive timer is currently running.
  bool get isHeartbeatActive =>
      _heartbeatTimer != null && _heartbeatTimer!.isActive;

  /// Technical keepalive interval for field session lease.
  /// Note: This is NOT a business timing; it is a session lease technical keepalive (Section 25).
  static const Duration _heartbeatInterval = Duration(seconds: 30);

  void _startHeartbeat() {
    _stopHeartbeat();
    if (!_enableHeartbeat) return;
    _heartbeatTimer = Timer.periodic(_heartbeatInterval, (_) => _onHeartbeat());
  }

  void _stopHeartbeat() {
    _heartbeatTimer?.cancel();
    _heartbeatTimer = null;
  }

  Future<void> _onHeartbeat() async {
    if (!_session.isAuthenticated) {
      _stopHeartbeat();
      return;
    }
    try {
      await _repository.heartbeatSession();
    } on ApiProblem catch (problem) {
      if (problem.isUnauthorized ||
          problem.code == 'SESSION_NOT_ACTIVE' ||
          problem.code == 'SESSION_EXPIRED') {
        _stopHeartbeat();
        handleUnauthorized();
      }
    } on ConnectivityException {
      // Short-term network drop; do not log out locally.
      // Server lease will naturally expire if keepalives stop completely.
    } catch (_) {
      // Transient error; server lease will expire if keepalives fail completely.
    }
  }

  Future<void> reloadMachines() async {
    if (!_session.isAuthenticated) {
      return;
    }
    _setBusy(true);
    _clearFeedback();
    try {
      await _loadMachines();
      _offline = false;
    } on ConnectivityException {
      _offline = true;
      _notice = UiNotice(
        'Bağlantı yok — makine listesi güncellenemedi.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _problem = problem;
    } finally {
      _setBusy(false);
    }
  }

  Future<void> selectMachine(MachineSummary machine) async {
    if (isMutationInFlight || production.hasPending) {
      _notice = UiNotice(
        'İşlem devam ederken makine değiştirilemez.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    if (!isAdminOrSupervisor &&
        device?.machineId != null &&
        machine.id != device!.machineId) {
      _notice = const UiNotice(
        'Bu terminal yalnızca tanımlı olduğu makine üzerinde işlem yapabilir.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    _selectedMachine = machine;
    _machineGeneration++;
    _contextLoading = false;
    production.reset(machine.id);
    _restoredProductionId = _productionContextError = null;
    _context = null;
    _resetPartFirstState();
    _stage = AppStage.operation;
    notifyListeners();
    await Future.wait([refreshContext(showBusy: true), _loadPartOptions()]);
  }

  void leaveMachine() {
    if (isBoundLineTerminalOperator) {
      return;
    }
    if (isMutationInFlight || production.hasPending) {
      _notice = UiNotice(
        'İşlem devam ederken sayfadan ayrılınamaz.',
        UiNoticeSeverity.warning,
      );
      notifyListeners();
      return;
    }
    _stopPolling();
    _machineGeneration++;
    production.reset(null);
    _selectedMachine = null;
    _context = null;
    _resetPartFirstState();
    _stage = AppStage.machines;
    _clearFeedback();
    _robotAlarmSummary = null;
    _robotAlarmError = null;
    notifyListeners();
  }

  Future<void> refreshContext({bool showBusy = false}) async {
    final machine = _selectedMachine;
    final generation = _machineGeneration;
    if (machine == null || !_session.isAuthenticated || _contextLoading) {
      return;
    }
    _contextLoading = true;
    if (showBusy) {
      _setBusy(true);
    }
    unawaited(retryRobotAlarmSummary());
    try {
      final next = await _repository.operationalContext(machine.id);
      if (generation != _machineGeneration || !_session.isAuthenticated) return;
      _context = next;
      _offline = false;
      _problem = null;
      if (usesProductionWorkflow) {
        await production.refresh();
        if (generation != _machineGeneration) return;
        await _restoreProductionSelection();
      }
      if (_submission.hasAmbiguousRequest) {
        _submission.resolveByRefresh();
        _ambiguousCommand = null;
        _notice = next.activeTransportRequests.isEmpty
            ? const UiNotice(
                'Belirsiz isteğin kabul edildiği doğrulanamadı. Yeni işlem öncesi durumu kontrol edin.',
                UiNoticeSeverity.warning,
              )
            : const UiNotice(
                'Aktif talep sunucudan doğrulandı.',
                UiNoticeSeverity.success,
              );
      }
      if (_selectedEffectivePart != null &&
          _resolvedCompatibleVariant != null &&
          next.activeVariantId == _resolvedCompatibleVariant!.variantId) {
        await _loadContainerPositions(_partResolutionGeneration);
      }
      _schedulePolling();
    } on ConnectivityException {
      if (generation == _machineGeneration) {
        _offline = true;
        _notice = UiNotice(
          'Bağlantı yok — bilgiler güncel olmayabilir.',
          UiNoticeSeverity.warning,
        );
        _stopPolling();
      }
    } on ApiProblem catch (problem) {
      if (generation == _machineGeneration) _problem = problem;
    } finally {
      if (generation == _machineGeneration) {
        _contextLoading = false;
        if (showBusy) {
          _setBusy(false);
        } else {
          notifyListeners();
        }
      }
    }
  }

  /// Customer Monitoring — Robot Alarm Visibility. Runs independently of
  /// [refreshContext]'s own offline/problem handling: a failed alarm read
  /// must surface as [robotAlarmError], never silently as "no active alarm".
  Future<void> retryRobotAlarmSummary() async {
    if (!_session.isAuthenticated) return;
    try {
      _robotAlarmSummary = await _repository.robotAlarmSummary();
      _robotAlarmError = null;
    } on ConnectivityException {
      _robotAlarmError = 'Bağlantı yok — robot durumu güncellenemedi.';
    } on ApiProblem {
      _robotAlarmError = 'Robot durumu şu anda görüntülenemiyor.';
    }
    notifyListeners();
  }

  Future<void> switchVariant(
    VariantLayoutOption option, {
    bool preservePartSelection = false,
  }) async {
    final machine = _selectedMachine;
    if (usesProductionWorkflow &&
        (!production.verified ||
            production.active != null ||
            production.hasPending)) {
      _notice = UiNotice(
        'Üretim açıkken varyant değiştirilemez.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    if (machine == null || !mutationEnabled) {
      return;
    }
    _setBusy(true);
    _clearFeedback();
    try {
      await _repository.switchVariant(
        machineId: machine.id,
        variantId: option.variantId,
        layoutId: option.layoutId,
        navigationProfileId: null,
      );
      _context = null;
      await refreshContext();
      if (!preservePartSelection) {
        resetPartSelection();
      }
      _notice = UiNotice(
        'Üretim varyantı güncellendi.',
        UiNoticeSeverity.success,
      );
    } on ConnectivityException {
      _offline = true;
      _notice = UiNotice(
        'Bağlantı yok — varyant değiştirilemedi.',
        UiNoticeSeverity.warning,
      );
    } on AmbiguousMutationException {
      _offline = true;
      _notice = UiNotice(
        'Varyant değişikliğinin sonucu doğrulanamadı. Yenileyin.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      if (problem.shouldRefetch) {
        await refreshContext();
      }
      _problem = problem;
    } finally {
      _setBusy(false);
    }
  }

  // ---- WP-03B: part-first -> compatible-variant resolution ----

  Future<void> reloadPartOptions() => _loadPartOptions();

  /// One click: the chosen main part becomes the effective part immediately
  /// and its compatible variants start resolving. Its RELATED/ALT parts stay
  /// visible as distinct request targets under this main-part context —
  /// no second "use the main part" confirmation step.
  Future<void> selectMainPartOption(MachineMainPartOption option) async {
    if (!canChangePartSelection) {
      _notice = UiNotice(
        'Talebin sonucu doğrulanmadan parça değiştirilemez.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    _selectedMainPart = option;
    _selectedEffectivePart = null;
    _compatibleVariants = null;
    _compatibleVariantsError = null;
    notifyListeners();
    await selectEffectivePart(
      EffectivePartSelection(
        partId: option.partId,
        partCode: option.partCode,
        isMain: true,
      ),
    );
  }

  Future<void> selectEffectivePart(EffectivePartSelection selection) async {
    if (!canChangeEffectivePart ||
        (usesProductionWorkflow &&
            production.active != null &&
            selection.partId != _selectedMainPart?.partId &&
            !(_selectedMainPart?.relatedParts.any(
                  (part) => part.partId == selection.partId,
                ) ??
                false))) {
      _notice = UiNotice(
        'Talebin sonucu doğrulanmadan parça değiştirilemez.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    final generation = ++_partResolutionGeneration;
    _selectedEffectivePart = selection;
    _compatibleVariants = null;
    _compatibleVariantsError = null;
    _resolvedCompatibleVariant = null;
    _resetContainerPositions();
    notifyListeners();
    await _loadCompatibleVariants(generation);
  }

  Future<void> retryCompatibleVariants() {
    final generation = ++_partResolutionGeneration;
    return _loadCompatibleVariants(generation);
  }

  Future<void> selectCompatibleVariant(CompatibleVariant variant) =>
      _resolveVariant(variant, _partResolutionGeneration);

  /// Returns to the part-first gate without leaving the machine or losing
  /// the already-loaded operational context/slots.
  void resetPartSelection() {
    if (!canChangePartSelection) {
      _notice = UiNotice(
        'Talebin sonucu doğrulanmadan parça değiştirilemez.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    _selectedMainPart = null;
    _selectedEffectivePart = null;
    _compatibleVariants = null;
    _compatibleVariantsError = null;
    _resolvedCompatibleVariant = null;
    _resetContainerPositions();
    _partResolutionGeneration++;
    _compatibleVariantsLoading = false;
    notifyListeners();
  }

  // ---- Customer UI — Multi-Part Transport Batch ----

  void enterBatchSelection() {
    if (_batchMode) return;
    _batchMode = true;
    notifyListeners();
  }

  void toggleBatchMode() {
    if (_batchMode) {
      exitBatchSelection();
    } else {
      enterBatchSelection();
    }
  }

  /// Leaves batch mode and discards the cart. Only ever called after a
  /// successful submission or an explicit operator cancel — never on a
  /// recoverable failure (ambiguous/API error), so a failed submit keeps the
  /// cart intact for retry.
  ///
  /// During an active production session the main part, variant and mamul
  /// context stay. Resetting the part selection is refused in that state and
  /// would surface a false error, so only the batch-specific state is cleared.
  void exitBatchSelection() {
    _containerBatchProductionId = null;
    _batchMode = false;
    _selectedAltParts.clear();
    _altBatchKey = null;
    _batchCart.clear();
    _batchConfiguringIndex = -1;
    _batchSubmission.clear();
    _containerBatchKey = null;
    _containerBatchAmbiguous = false;
    if (production.active == null) {
      resetPartSelection();
    }
    notifyListeners();
  }

  /// Alt-part demand is only "I want this material". The operator never
  /// chooses a container, load or slot. A repeated tap while the first
  /// request is still in flight reuses the same idempotency key.
  Future<void> requestAltPart(MachineRelatedPart part) async {
    if (!part.requestable) {
      _notice = const UiNotice(
        'Taşıma kabı bilgisi bekleniyor',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    if (production.active?.isEnding == true) {
      _notice = const UiNotice(
        'Üretim bitiriliyor. Yeni alt parça talebi açılamaz.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    if (_batchMode) {
      if (isAltPartSelected(part.partId)) {
        _selectedAltParts.removeWhere((item) => item.partId == part.partId);
      } else {
        _selectedAltParts.add(part);
      }
      notifyListeners();
      return;
    }
    final machine = _context?.machine;
    if (machine == null || !productionTransportReady) {
      _notice = UiNotice(
        'Alt parça talebi için aktif üretim gerekir.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    if (_altDemandInFlight.contains(part.partId) || !mutationGate.canMutate) {
      return;
    }
    final key = _altDemandKeys.putIfAbsent(
      part.partId,
      () => 'part-demand:${part.partId}:${_cartIdFactory.v4()}',
    );
    _altDemandInFlight.add(part.partId);
    _setBusy(true);
    try {
      final result = await _repository.createPartDemand(
        machineId: machine.id,
        partId: part.partId,
        idempotencyKey: key,
      );
      _altDemandKeys.remove(part.partId);
      _notice = UiNotice(
        result.predecessorRequestNo == null
            ? '${part.partCode} talebi oluşturuldu. Talep #${result.requestNo}.'
            : '${part.partCode} için mevcut palet kaldırılıp yeni palet isteniyor. Talep #${result.requestNo}.',
        UiNoticeSeverity.success,
      );
      await refreshContext();
    } on ConnectivityException {
      _notice = UiNotice(
        'Bağlantı yok — alt parça talebi gönderilemedi.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _notice = UiNotice(problem.operatorMessage, UiNoticeSeverity.error);
      if (problem.code == 'PART_REQUEST_ALREADY_ACTIVE') {
        _altDemandKeys.remove(part.partId);
        await refreshContext();
      }
    } finally {
      _altDemandInFlight.remove(part.partId);
      _setBusy(false);
    }
  }

  Future<void> submitSelectedAltParts() async {
    if (production.active?.isEnding == true) {
      _notice = const UiNotice(
        'Üretim bitiriliyor. Yeni alt parça talebi açılamaz.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    final machine = _context?.machine;
    if (machine == null ||
        _selectedAltParts.isEmpty ||
        !mutationGate.canMutate) {
      return;
    }
    final key = _altBatchKey ??= 'part-demand-batch:${_cartIdFactory.v4()}';
    final partIds = _selectedAltParts.map((part) => part.partId).toList();
    _setBusy(true);
    try {
      final result = await _repository.createPartDemandBatch(
        machineId: machine.id,
        partIds: partIds,
        idempotencyKey: key,
      );
      _notice = UiNotice(
        '${result.items.length} alt parça talebi oluşturuldu.',
        UiNoticeSeverity.success,
      );
      _selectedAltParts.clear();
      _altBatchKey = null;
      _batchMode = false;
      await refreshContext();
    } on ConnectivityException {
      _notice = UiNotice(
        'Bağlantı yok — toplu alt parça talebi gönderilemedi.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _notice = UiNotice(problem.operatorMessage, UiNoticeSeverity.error);
    } finally {
      _setBusy(false);
    }
  }

  /// Adds [part] (main, RELATED or ALT) to the cart. Returns false without
  /// changing anything if that part is already selected.
  bool addPartToBatch(
    MachineMainPartOption mainPart,
    EffectivePartSelection part,
  ) {
    if (usesProductionWorkflow &&
        (!productionTransportReady ||
            mainPart.partId != production.active?.mainPartId)) {
      return false;
    }
    if (_batchCart.any((item) => item.effectivePart.partId == part.partId)) {
      return false;
    }
    _batchCart.add(
      BatchCartItem(
        cartItemId: _cartIdFactory.v4(),
        mainPart: mainPart,
        effectivePart: part,
      ),
    );
    notifyListeners();
    return true;
  }

  Future<void> removePartFromBatch(String cartItemId) async {
    final removedIndex = _batchCart.indexWhere(
      (item) => item.cartItemId == cartItemId,
    );
    if (removedIndex == -1) return;
    _batchCart.removeAt(removedIndex);
    if (_batchConfiguringIndex == removedIndex) {
      _batchConfiguringIndex = -1;
      resetPartSelection();
      await _advanceBatchConfiguration();
    } else if (_batchConfiguringIndex > removedIndex) {
      _batchConfiguringIndex--;
    }
    notifyListeners();
  }

  /// Starts (or resumes) the sequential per-item configuration wizard at the
  /// first not-yet-configured cart item.
  Future<void> beginBatchConfiguration() => _advanceBatchConfiguration();

  /// Removes the item currently being configured (used for the zero-variant
  /// "invalid item" case) and continues with whatever comes next.
  Future<void> skipInvalidBatchItem() async {
    final item = currentBatchItem;
    if (item == null) return;
    await removePartFromBatch(item.cartItemId);
  }

  /// Called instead of [submitTransport] from the action-confirm step while
  /// a batch item is being configured: stores the resolved command on that
  /// cart item and advances to the next unconfigured item (or to Review,
  /// once every item has a command).
  Future<void> confirmBatchItemCommand(
    ContainerActionBatchItemCommand command,
  ) async {
    if (!isConfiguringBatchItem) return;
    final item = _batchCart[_batchConfiguringIndex];
    item.command = command;
    item.slotDisplayName = '${command.positionNumber}. Kasa';
    item.actionCode = command.action;
    item.variantCode = _context?.activeVariantCode;
    item.variantId = _context?.activeVariantId;
    _batchConfiguringIndex = -1;
    resetPartSelection();
    await _advanceBatchConfiguration();
  }

  Future<void> _advanceBatchConfiguration() async {
    final index = _batchCart.indexWhere((item) => !item.isConfigured);
    if (index == -1) {
      notifyListeners();
      return;
    }
    _batchConfiguringIndex = index;
    await _loadBatchItemForConfiguration(_batchCart[index]);
  }

  Future<void> _loadBatchItemForConfiguration(BatchCartItem item) async {
    _selectedMainPart = item.mainPart;
    _selectedEffectivePart = null;
    _compatibleVariants = null;
    _compatibleVariantsError = null;
    _resolvedCompatibleVariant = null;
    notifyListeners();
    await selectEffectivePart(item.effectivePart);
  }

  /// Reopens one configured item without discarding the cart. This is the
  /// correction path after a recoverable batch API error or an operator
  /// review change; the item receives a fresh command while the other
  /// independent requests retain their configuration.
  Future<void> reconfigureBatchItem(String cartItemId) async {
    final index = _batchCart.indexWhere(
      (item) => item.cartItemId == cartItemId,
    );
    if (index == -1 || _busy) return;
    final item = _batchCart[index];
    item.command = null;
    item.slotDisplayName = null;
    item.actionCode = null;
    item.variantCode = null;
    item.variantId = null;
    _batchConfiguringIndex = index;
    await _loadBatchItemForConfiguration(item);
  }

  /// Submits the configured cart. Exactly one item preserves the existing
  /// single TransportRequest workflow; two or more use the batch endpoint
  /// with one batch idempotency key and one independent key per item.
  Future<void> submitBatch() async {
    if (!productionTransportReady && !_containerBatchAmbiguous) return;
    if (!canReviewBatch || _busy) return;
    final machine = _selectedMachine;
    final current = _context;
    if (machine == null || current == null || !_session.isAuthenticated) {
      return;
    }
    final variantId = current.activeVariantId;
    final layoutId = current.activeLayoutId;
    if (variantId == null ||
        layoutId == null ||
        _batchCart.any((item) => item.variantId != variantId)) {
      _notice = UiNotice(
        'Grup, makinenin tek etkin varyantıyla uyumlu değil.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    _setBusy(true);
    _clearFeedback();
    try {
      final key = _containerBatchKey ??=
          'container-batch:${_cartIdFactory.v4()}';
      _containerBatchProductionId ??= usesProductionWorkflow
          ? production.active?.id
          : null;
      final result = await _repository.createContainerActionBatch(
        machineId: machine.id,
        variantId: variantId,
        layoutId: layoutId,
        navigationProfileId: current.activeNavigationProfileId,
        items: _batchCart.map((item) => item.command!).toList(growable: false),
        idempotencyKey: key,
        productionSessionId: _containerBatchProductionId,
      );
      _notice = UiNotice(
        result.idempotentReplay
            ? 'Grup talebi doğrulandı.'
            : 'Grup talebi oluşturuldu: ${result.items.length} işlem.',
        UiNoticeSeverity.success,
      );
      _containerBatchKey = null;
      _containerBatchAmbiguous = false;
      exitBatchSelection();
      await refreshContext();
    } on AmbiguousMutationException {
      _containerBatchAmbiguous = true;
      _offline = true;
      _notice = const UiNotice(
        'Grup gönderiminin sonucu belirsiz. Aynı seçim aynı anahtarla yeniden denenebilir.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _problem = problem;
      if (problem.shouldRefetch) {
        await refreshContext();
      }
    } finally {
      _setBusy(false);
    }
  }

  Future<void> retryAmbiguousBatch() async {
    if (!_containerBatchAmbiguous || _containerBatchKey == null) return;
    _offline = false;
    await submitBatch();
  }

  // ---- Warehouse tablet — the four shared preparation areas ----

  /// Owns its own polling and its own error surface. Entering it never touches
  /// machine selection or part-first state.
  final PreparationCoordinator preparation;

  /// The warehouse tablet home.
  void enterWarehouseHome() {
    if (!canAccessAnyWarehouse || isMutationInFlight) return;
    _stage = AppStage.warehouseHome;
    _clearFeedback();
    notifyListeners();
  }

  void enterPreparationAreas() {
    if (!canAccessSemiFinishedWarehouse || isMutationInFlight) return;
    _stage = AppStage.preparationAreas;
    _clearFeedback();
    notifyListeners();
    preparation.start();
  }

  void leavePreparationAreas() {
    if (preparation.inFlight) return;
    if (canAccessAnyWarehouse) {
      _stage = AppStage.warehouseHome;
    } else {
      _stage = AppStage.machines;
    }
    _clearFeedback();
    notifyListeners();
  }

  /// The finished-goods half. Polls on its own and shares no state with the
  /// preparation side.
  final StoreCoordinator store;

  void enterFinishedGoodsStore() {
    if (!canAccessFinishedGoodsWarehouse || isMutationInFlight) return;
    _stage = AppStage.finishedGoodsStore;
    _clearFeedback();
    notifyListeners();
    store.start();
  }

  void leaveFinishedGoodsStore() {
    if (store.inFlight) return;
    if (canAccessAnyWarehouse) {
      _stage = AppStage.warehouseHome;
    } else {
      _stage = AppStage.machines;
    }
    _clearFeedback();
    notifyListeners();
  }

  void leaveWarehouseHome() {
    if (isMutationInFlight) return;
    preparation.stop();
    store.stop();
    _stage = AppStage.machines;
    _clearFeedback();
    notifyListeners();
  }

  // ---- Customer UI — Warehouse Preparation Console ----

  /// Kept separate from the line-operator part-selection flow: this stage
  /// never touches machine selection or part-first state, and vice versa.
  Future<void> enterWarehousePreparation() async {
    enterPreparationAreas();
  }

  void leaveWarehousePreparation() {
    if (isMutationInFlight) return;
    _stage = canAccessAnyWarehouse ? AppStage.warehouseHome : AppStage.machines;
    _warehouseRequests = const [];
    _warehouseError = null;
    _clearFeedback();
    notifyListeners();
  }

  Future<void> loadWarehousePreparationRequests() async {
    if (!_session.isAuthenticated) return;
    _warehouseLoading = true;
    _warehouseError = null;
    notifyListeners();
    try {
      _warehouseRequests = await _repository.warehousePreparationRequests();
      _offline = false;
    } on ConnectivityException {
      _offline = true;
      _warehouseError = 'Bağlantı yok — hazırlık listesi yüklenemedi.';
    } on ApiProblem catch (problem) {
      _warehouseError = problem.operatorMessage;
    } finally {
      _warehouseLoading = false;
      notifyListeners();
    }
  }

  /// Marks [requestId] READY. On success the item is removed from the local
  /// list (the source of truth remains the server; a refresh would drop it
  /// too since it is no longer WAITING_PREPARATION). On failure the item
  /// stays exactly as it was so the operator can retry.
  Future<void> markPreparationReady(String requestId) async {
    if (!mutationGate.canMutate || _warehouseMarkingReady.contains(requestId)) {
      return;
    }
    _warehouseMarkingReady.add(requestId);
    _clearFeedback();
    notifyListeners();
    try {
      await _repository.markTransportReady(requestId);
      _warehouseRequests = _warehouseRequests
          .where((request) => request.requestId != requestId)
          .toList(growable: false);
      _notice = const UiNotice(
        'Hazırlık onaylandı. Taşıma, koşullar uygun olduğunda otomatik başlayacak.',
        UiNoticeSeverity.success,
      );
    } on ConnectivityException {
      _offline = true;
      _notice = UiNotice(
        'Bağlantı yok — hazır durumu doğrulanamadı. Yeniden deneyin.',
        UiNoticeSeverity.warning,
      );
    } on AmbiguousMutationException {
      _offline = true;
      _notice = const UiNotice(
        'Hazır durumunun sunucuya ulaştığı doğrulanamadı. Yeniden deneyebilirsiniz.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _problem = problem;
    } finally {
      _warehouseMarkingReady.remove(requestId);
      notifyListeners();
    }
  }

  Future<void> _loadPartOptions() async {
    final machine = _selectedMachine;
    final generation = _machineGeneration;
    if (machine == null) return;
    _partOptionsLoading = true;
    _partOptionsError = null;
    notifyListeners();
    try {
      final options = await _repository.machinePartOptions(machine.id);
      if (generation != _machineGeneration) return;
      _partOptions = options;
    } on ConnectivityException {
      if (generation == _machineGeneration) {
        _partOptionsError = 'Bağlantı yok — parça listesi yüklenemedi.';
      }
    } on ApiProblem catch (problem) {
      if (generation == _machineGeneration) {
        _partOptionsError = problem.operatorMessage;
      }
    } finally {
      if (generation == _machineGeneration) {
        _partOptionsLoading = false;
        await _restoreProductionSelection();
        notifyListeners();
      }
    }
  }

  Future<void> _loadCompatibleVariants(int generation) async {
    final machine = _selectedMachine;
    final mainPart = _selectedMainPart;
    final part = _selectedEffectivePart;
    if (machine == null || mainPart == null || part == null) return;
    _compatibleVariantsLoading = true;
    _compatibleVariantsError = null;
    notifyListeners();
    try {
      var variants = await _repository.compatibleVariants(
        machineId: machine.id,
        mainPartId: mainPart.partId,
        partId: part.partId,
      );
      if (generation != _partResolutionGeneration ||
          _selectedEffectivePart?.partId != part.partId) {
        return;
      }
      final batchVariantId = isConfiguringBatchItem
          ? batchTargetVariantId
          : null;
      if (batchVariantId != null) {
        variants = variants
            .where((variant) => variant.variantId == batchVariantId)
            .toList(growable: false);
      }
      _compatibleVariants = variants;
      if (usesProductionWorkflow && production.active != null) {
        final active = production.active!;
        variants = variants
            .where(
              (variant) =>
                  variant.variantId == active.variantId &&
                  variant.productGroupId == active.productGroupId,
            )
            .toList(growable: false);
        _compatibleVariants = variants;
      }
      if (variants.length == 1) {
        await _resolveVariant(variants.single, generation);
      }
    } on ConnectivityException {
      if (generation == _partResolutionGeneration) {
        _compatibleVariantsError =
            'Bağlantı yok — uyumlu varyantlar yüklenemedi.';
      }
    } on ApiProblem catch (problem) {
      if (generation == _partResolutionGeneration) {
        _compatibleVariantsError = problem.operatorMessage;
      }
    } finally {
      if (generation == _partResolutionGeneration) {
        _compatibleVariantsLoading = false;
        notifyListeners();
      }
    }
  }

  /// Reuses the existing authorized variant-selection mechanism
  /// ([switchVariant]) when the resolved variant differs from the machine's
  /// current active variant; never bypasses machine authorization or
  /// canChangeVariant policy. The resolved product-group identity is retained
  /// on [CompatibleVariant] and later becomes transport-command material;
  /// there is no second manual product-group selection path.
  Future<void> _resolveVariant(
    CompatibleVariant variant,
    int generation,
  ) async {
    if (generation != _partResolutionGeneration) return;
    if (usesProductionWorkflow && production.active != null) {
      final active = production.active!;
      if (variant.variantId != active.variantId ||
          variant.productGroupId != active.productGroupId) {
        return;
      }
    }
    final current = _context;
    if (current != null && current.activeVariantId == variant.variantId) {
      _markVariantResolved(variant);
      await _loadContainerPositions(generation);
      notifyListeners();
      return;
    }
    final option = current == null
        ? null
        : _firstOrNull(
            current.variantLayouts.where(
              (candidate) => candidate.variantId == variant.variantId,
            ),
          );
    if (option == null) {
      _notice = UiNotice(
        'Seçilen varyant için düzenli bir yerleşim bulunamadı.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    await switchVariant(option, preservePartSelection: true);
    if (generation == _partResolutionGeneration &&
        _context?.activeVariantId == variant.variantId) {
      _markVariantResolved(variant);
      await _loadContainerPositions(generation);
    }
    notifyListeners();
  }

  void _markVariantResolved(CompatibleVariant variant) {
    _resolvedCompatibleVariant = variant;
  }

  Future<void> _loadContainerPositions(int generation) async {
    final current = _context;
    final part = _mamulDemandPart;
    final variant = _resolvedCompatibleVariant;
    final active = production.active;
    final layoutId = current?.activeLayoutId;
    if (current == null ||
        part == null ||
        variant == null ||
        layoutId == null ||
        (active != null &&
            (variant.variantId != active.variantId ||
                variant.productGroupId != active.productGroupId ||
                part.partId != active.mainPartId))) {
      _resetContainerPositions();
      return;
    }
    _containerPositionsLoading = true;
    _containerPositionsError = null;
    notifyListeners();
    try {
      final positions = await _repository.containerPositions(
        machineId: current.machine.id,
        variantId: variant.variantId,
        layoutId: layoutId,
        productGroupId: active?.productGroupId ?? variant.productGroupId,
        partId: part.partId,
      );
      if (generation == _partResolutionGeneration &&
          _mamulDemandPart?.partId == part.partId &&
          _resolvedCompatibleVariant?.variantId == variant.variantId) {
        _containerPositions = positions;
      }
    } on ConnectivityException {
      if (generation == _partResolutionGeneration) {
        _containerPositionsError = 'Bağlantı yok — kasa konumları yüklenemedi.';
      }
    } on ApiProblem catch (problem) {
      if (generation == _partResolutionGeneration) {
        _containerPositionsError = problem.operatorMessage;
      }
    } finally {
      if (generation == _partResolutionGeneration) {
        _containerPositionsLoading = false;
        notifyListeners();
      }
    }
  }

  void _resetContainerPositions() {
    _containerPositions = const [];
    _containerPositionsLoading = false;
    _containerPositionsError = null;
    _submittingContainerActions.clear();
  }

  Future<void> retryContainerPositions() =>
      _loadContainerPositions(_partResolutionGeneration);

  Future<void> submitContainerAction(
    ContainerPosition position,
    String action,
  ) async {
    if (!mamulActionsReady) return;
    final current = _context;
    final part = _mamulDemandPart;
    final variant = _resolvedCompatibleVariant;
    final variantId = current?.activeVariantId;
    final layoutId = current?.activeLayoutId;
    if (current == null ||
        part == null ||
        variant == null ||
        variantId == null ||
        layoutId == null ||
        variant.variantId != variantId) {
      _notice = UiNotice(
        'İşlem bağlamı değişti. Bilgileri yenileyin.',
        UiNoticeSeverity.warning,
      );
      notifyListeners();
      return;
    }

    final availability = action == 'FULL'
        ? position.fullAction
        : position.emptyAction;
    if (!availability.enabled) {
      _notice = UiNotice(
        availability.disabledMessage ?? 'Bu işlem şu anda hazır değil.',
        UiNoticeSeverity.warning,
      );
      notifyListeners();
      return;
    }

    final command = ContainerActionBatchItemCommand(
      productGroupId: variant.productGroupId,
      partId: part.partId,
      positionNumber: position.positionNumber,
      action: action,
    );
    if (isConfiguringBatchItem) {
      await confirmBatchItemCommand(command);
      return;
    }

    if (_batchMode && production.active == null) {
      final existingIndex = _batchCart.indexWhere(
        (item) =>
            item.effectivePart.partId == part.partId &&
            item.command?.positionNumber == position.positionNumber,
      );
      if (existingIndex != -1) {
        _notice = UiNotice(
          '${part.partCode} · ${position.label} zaten sepette ekli.',
          UiNoticeSeverity.error,
        );
        notifyListeners();
        return;
      }

      if (_batchCart.isNotEmpty &&
          _batchCart.any((item) => item.variantId != variantId)) {
        _notice = UiNotice(
          'Tüm toplu talepler aynı varyanta (${_batchCart.first.variantCode}) ait olmalıdır.',
          UiNoticeSeverity.error,
        );
        notifyListeners();
        return;
      }

      final cartItem =
          BatchCartItem(
              cartItemId: _cartIdFactory.v4(),
              mainPart: _selectedMainPart!,
              effectivePart: part,
            )
            ..command = command
            ..slotDisplayName = '${position.positionNumber}. Kasa'
            ..actionCode = action
            ..variantCode = current.activeVariantCode
            ..variantId = variantId;

      _batchCart.add(cartItem);
      _notice = UiNotice(
        '${part.partCode} · ${position.label} (${action == 'FULL' ? 'Dolu' : 'Boş'}) sepete eklendi.',
        UiNoticeSeverity.success,
      );
      notifyListeners();
      return;
    }

    final submissionId = '${position.positionNumber}:$action';
    if (_submittingContainerActions.contains(submissionId) ||
        !mutationGate.canMutate) {
      return;
    }
    final material =
        '${production.active?.id}|$variantId|$layoutId|${variant.productGroupId}|${part.partId}|$submissionId';
    final key = _containerActionKeys.putIfAbsent(
      material,
      () => 'container-action:${_cartIdFactory.v4()}',
    );
    final pending = PendingOperation(
      machineId: current.machine.id,
      machineCode: current.machine.code,
      variantId: variantId,
      variantCode: current.activeVariantCode,
      layoutId: layoutId,
      productGroupId: variant.productGroupId,
      partId: part.partId,
      partCode: part.partCode,
      logicalContainerNumber: position.positionNumber,
      operation: action,
      idempotencyKey: key,
      startedAt: DateTime.now(),
      navigationProfileId: current.activeNavigationProfileId,
      productionSessionId: usesProductionWorkflow
          ? production.active?.id
          : null,
    );
    _activePendingOperation = pending;
    _submittingContainerActions.add(submissionId);
    _submittingOperation = action;
    _clearFeedback();
    notifyListeners();
    try {
      final result = await _repository.createContainerAction(
        machineId: current.machine.id,
        variantId: variantId,
        layoutId: layoutId,
        navigationProfileId: current.activeNavigationProfileId,
        productGroupId: variant.productGroupId,
        partId: part.partId,
        positionNumber: position.positionNumber,
        action: action,
        idempotencyKey: key,
        productionSessionId: pending.productionSessionId,
      );
      _containerActionKeys.remove(material);
      _activePendingOperation = null;
      _ambiguousPendingOperation = null;
      _ambiguousContainerPosition = null;
      _ambiguousContainerAction = null;
      _notice = UiNotice(
        result.idempotentReplay
            ? 'Mevcut kasa talebi doğrulandı.'
            : '${position.label} talebi oluşturuldu.',
        UiNoticeSeverity.success,
      );
      await refreshContext();
    } on AmbiguousMutationException {
      _activePendingOperation = null;
      _ambiguousPendingOperation = pending;
      _ambiguousContainerPosition = position;
      _ambiguousContainerAction = action;
      _offline = true;
      _notice = const UiNotice(
        'Gönderim sonucu belirsiz. Aynı kasa işlemi aynı anahtarla yeniden denenecek.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _activePendingOperation = null;
      _containerActionKeys.remove(material);
      if (problem.shouldRefetch) await refreshContext();
      _problem = problem;
    } finally {
      _activePendingOperation = null;
      _submittingContainerActions.remove(submissionId);
      _submittingOperation = null;
      notifyListeners();
    }
  }

  Future<void> removeAllContainers() async {
    if (!productionTransportReady && !_removeAllAmbiguous) return;
    final current = _context;
    final variantId = current?.activeVariantId;
    final layoutId = current?.activeLayoutId;
    if (current == null ||
        variantId == null ||
        layoutId == null ||
        _removeAllSubmitting ||
        !mutationGate.canMutate) {
      return;
    }
    final key = _removeAllKey ??= 'remove-all:${_cartIdFactory.v4()}';
    _removeAllProductionId ??= usesProductionWorkflow
        ? production.active?.id
        : null;
    final pending = PendingOperation(
      machineId: current.machine.id,
      machineCode: current.machine.code,
      variantId: variantId,
      variantCode: current.activeVariantCode,
      layoutId: layoutId,
      productGroupId: null,
      partId: '',
      logicalContainerNumber: null,
      operation: 'REMOVE_ALL',
      idempotencyKey: key,
      startedAt: DateTime.now(),
      navigationProfileId: current.activeNavigationProfileId,
    );
    _activePendingOperation = pending;
    _removeAllSubmitting = true;
    _clearFeedback();
    notifyListeners();
    try {
      final result = await _repository.removeAllContainers(
        machineId: current.machine.id,
        variantId: variantId,
        layoutId: layoutId,
        navigationProfileId: current.activeNavigationProfileId,
        idempotencyKey: key,
        productionSessionId: _removeAllProductionId,
      );
      _activePendingOperation = null;
      _ambiguousPendingOperation = null;
      _removeAllKey = null;
      _removeAllProductionId = null;
      _removeAllAmbiguous = false;
      _notice = UiNotice(
        result.idempotentReplay
            ? 'Kaldırma grubu doğrulandı.'
            : '${result.itemCount} kasa için kaldırma talebi oluşturuldu.',
        UiNoticeSeverity.success,
      );
      await refreshContext();
    } on AmbiguousMutationException {
      _activePendingOperation = null;
      _ambiguousPendingOperation = pending;
      _removeAllAmbiguous = true;
      _offline = true;
      _notice = const UiNotice(
        'Tüm kasaları kaldırma sonucunun ulaştığı doğrulanamadı. Aynı istek yeniden denenebilir.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _activePendingOperation = null;
      _removeAllKey = null;
      _removeAllProductionId = null;
      _removeAllAmbiguous = false;
      if (problem.shouldRefetch) await refreshContext();
      _problem = problem;
    } finally {
      _activePendingOperation = null;
      _removeAllSubmitting = false;
      notifyListeners();
    }
  }

  Future<void> enterTasks() async {
    if (!_session.isAuthenticated || isMutationInFlight) return;
    if (_stage != AppStage.tasks) {
      _taskReturnStage = _stage;
    }
    _stage = AppStage.tasks;
    _operatorQueueTerminal = false;
    _clearFeedback();
    notifyListeners();
    await loadOperatorQueue();
  }

  void leaveTasks() {
    if (isMutationInFlight) return;
    _stopQueuePolling();
    final returnStage =
        _taskReturnStage ??
        (isBoundLineTerminalOperator ? AppStage.operation : AppStage.machines);
    _taskReturnStage = null;
    _stage = returnStage;
    _operatorQueue = null;
    _operatorQueueError = null;
    notifyListeners();
    if (_stage == AppStage.operation && _selectedMachine != null) {
      unawaited(refreshContext());
    }
  }

  Future<void> setOperatorQueueTerminal(bool value) async {
    if (_operatorQueueTerminal == value) return;
    _operatorQueueTerminal = value;
    await loadOperatorQueue();
  }

  Future<void> loadOperatorQueue({int offset = 0, bool silent = false}) async {
    if (!_session.isAuthenticated) return;
    unawaited(refreshRobots());
    if (!silent || _operatorQueue == null) {
      _operatorQueueLoading = true;
      _operatorQueueError = null;
      notifyListeners();
    }
    try {
      _operatorQueue = await _repository.operatorQueue(
        terminal: _operatorQueueTerminal,
        offset: offset,
      );
      _offline = false;
      _lastSuccessfulQueueRefresh = DateTime.now();
    } on ConnectivityException {
      _offline = true;
      if (!silent) {
        _operatorQueueError = 'Bağlantı yok — görevler yüklenemedi.';
      }
    } on ApiProblem catch (problem) {
      if (!silent) {
        _operatorQueueError = problem.operatorMessage;
      }
    } finally {
      _operatorQueueLoading = false;
      notifyListeners();
      _scheduleQueuePolling();
    }
  }

  void _scheduleQueuePolling() {
    _stopQueuePolling();
    if (!_enablePolling ||
        !_foreground ||
        _offline ||
        _stage != AppStage.tasks) {
      return;
    }
    _queuePollTimer = Timer(const Duration(seconds: 5), () {
      if (_stage == AppStage.tasks) {
        unawaited(
          loadOperatorQueue(offset: _operatorQueue?.offset ?? 0, silent: true),
        );
      }
    });
  }

  void _stopQueuePolling() {
    _queuePollTimer?.cancel();
    _queuePollTimer = null;
  }

  Future<void> refreshRobots() async {
    if (!_session.isAuthenticated) return;
    try {
      _robots = await _repository.robots();
      notifyListeners();
    } catch (_) {
      // Non-blocking telemetry
    }
  }

  Future<void> pauseRobot([List<String>? vehicles]) async {
    if (!mutationEnabled) return;
    _setBusy(true);
    try {
      await _repository.pauseRobot(vehicles);
      _notice = UiNotice(
        'Robot navigasyonu duraklatıldı.',
        UiNoticeSeverity.success,
      );
      await refreshRobots();
    } catch (_) {
      _notice = const UiNotice(
        'Robot duraklatılamadı.',
        UiNoticeSeverity.error,
      );
    } finally {
      _setBusy(false);
    }
  }

  Future<void> resumeRobot([List<String>? vehicles]) async {
    if (!mutationEnabled) return;
    _setBusy(true);
    try {
      await _repository.resumeRobot(vehicles);
      _notice = UiNotice(
        'Robot navigasyonu devam ettirildi.',
        UiNoticeSeverity.success,
      );
      await refreshRobots();
    } catch (_) {
      _notice = const UiNotice(
        'Robot devam ettirilemedi.',
        UiNoticeSeverity.error,
      );
    } finally {
      _setBusy(false);
    }
  }

  Future<void> clearRobotErrors([List<String>? vehicles]) async {
    if (!mutationEnabled) return;
    _setBusy(true);
    try {
      await _repository.clearRobotErrors(vehicles);
      _notice = UiNotice(
        'Robot hataları temizlendi.',
        UiNoticeSeverity.success,
      );
      await refreshRobots();
    } catch (_) {
      _notice = const UiNotice(
        'Robot hataları temizlenemedi.',
        UiNoticeSeverity.error,
      );
    } finally {
      _setBusy(false);
    }
  }

  Future<void> setRequestPriority(String requestId, int priority) async {
    if (!mutationGate.canMutate) return;
    _setBusy(true);
    try {
      await _repository.setRequestPriority(requestId, priority);
      _notice = UiNotice(
        'Görev önceliği güncellendi.',
        UiNoticeSeverity.success,
      );
      await loadOperatorQueue(
        offset: _operatorQueue?.offset ?? 0,
        silent: true,
      );
    } catch (_) {
      _notice = const UiNotice(
        'Öncelik güncellenemedi.',
        UiNoticeSeverity.error,
      );
    } finally {
      _setBusy(false);
    }
  }

  void _resetPartFirstState() {
    _containerBatchProductionId = null;
    _removeAllProductionId = null;
    _partOptions = const [];
    _partOptionsLoading = false;
    _partOptionsError = null;
    _selectedMainPart = null;
    _selectedEffectivePart = null;
    _compatibleVariants = null;
    _compatibleVariantsLoading = false;
    _compatibleVariantsError = null;
    _resolvedCompatibleVariant = null;
    _partResolutionGeneration++;
    _batchMode = false;
    _batchCart.clear();
    _batchConfiguringIndex = -1;
    _batchSubmission.clear();
    _containerBatchKey = null;
    _containerBatchAmbiguous = false;
    _resetContainerPositions();
  }

  Future<void> startProduction() async {
    if (!canStartProduction) return;
    final group = _firstOrNull(
      _context!.productGroups.where(
        (group) =>
            group.id == _resolvedCompatibleVariant!.productGroupId &&
            group.mainPartId == _selectedMainPart!.partId,
      ),
    );
    if (group == null) {
      _notice = UiNotice(
        'Seçilen ana ürünün üretim bilgileri doğrulanamadı.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return;
    }
    await production.start(
      ProductionStartCommand(
        machineId: _selectedMachine!.id,
        variantId: _resolvedCompatibleVariant!.variantId,
        productGroupId: group.id,
        mainPartId: group.mainPartId,
      ),
    );
    final error = production.error;
    await refreshContext();
    _notice = error != null
        ? UiNotice(error, UiNoticeSeverity.error)
        : UiNotice(
            production.active != null
                ? 'Üretim başladı.'
                : 'Üretim durumu güncellendi.',
            UiNoticeSeverity.success,
          );
    notifyListeners();
  }

  Future<void> endProduction() async {
    if (!canEndProduction) return;
    await production.end();
    final error = production.error;
    await refreshContext();
    _notice = error != null
        ? UiNotice(error, UiNoticeSeverity.error)
        : UiNotice(
            production.active?.isEnding == true
                ? 'Üretim bitiriliyor. Paletler sırayla kaldırılacak.'
                : 'Üretim durumu güncellendi.',
            UiNoticeSeverity.success,
          );
    notifyListeners();
  }

  Future<void> retryProduction() async {
    if (!_session.isAuthenticated ||
        isMutationInFlight ||
        _resumeValidationPending ||
        !_foreground) {
      return;
    }
    await production.retry();
    final error = production.error;
    await refreshContext();
    if (error != null) _notice = UiNotice(error, UiNoticeSeverity.error);
    notifyListeners();
  }

  Future<void> _restoreProductionSelection() async {
    final machineGeneration = _machineGeneration;
    if (!usesProductionWorkflow ||
        !production.verified ||
        _restoringProductionGeneration == machineGeneration ||
        _partOptionsLoading ||
        _context == null ||
        hasAmbiguousSubmission ||
        hasAmbiguousBatchSubmission) {
      return;
    }
    final active = production.active;
    if (active == null) {
      if (_restoredProductionId != null) {
        _selectedMainPart = null;
        _selectedEffectivePart = null;
        _resolvedCompatibleVariant = null;
        _compatibleVariants = null;
        _partResolutionGeneration++;
        _resetContainerPositions();
        _batchCart.clear();
        _batchConfiguringIndex = -1;
      }
      _restoredProductionId = _productionContextError = null;
      return;
    }
    final main = _firstOrNull(
      _partOptions.where((part) => part.partId == active.mainPartId),
    );
    final group = _firstOrNull(
      _context!.productGroups.where(
        (group) =>
            group.id == active.productGroupId &&
            group.mainPartId == active.mainPartId,
      ),
    );
    if (main == null ||
        group == null ||
        _context!.activeVariantId != active.variantId) {
      _productionContextError =
          'Açık üretimin ürün bilgileri doğrulanamadı. Bilgileri yenileyin.';
      return;
    }
    _productionContextError = null;
    final scoped = MachineMainPartOption(
      partId: main.partId,
      partCode: main.partCode,
      description: main.description,
      relatedParts: group.parts
          .map(
            (part) => MachineRelatedPart(
              partId: part.partId,
              partCode: part.partCode,
              description: _firstOrNull(
                main.relatedParts.where((item) => item.partId == part.partId),
              )?.description,
              relationType: part.relationType,
              requestable: part.relationType != 'ALT_PART' ||
                  (part.containerTypeId != null && part.containerTypeId!.isNotEmpty),
              blockers: part.relationType == 'ALT_PART' &&
                      (part.containerTypeId == null || part.containerTypeId!.isEmpty)
                  ? const ['CONTAINER_TYPE_MISSING']
                  : const [],
            ),
          )
          .toList(growable: false),
    );
    final changed = _restoredProductionId != active.id;
    if (changed) {
      _batchCart.clear();
      _batchConfiguringIndex = -1;
    }
    _selectedMainPart = scoped;
    final selected = _selectedEffectivePart;
    final validPart =
        selected != null &&
        (selected.partId == main.partId ||
            group.parts.any((part) => part.partId == selected.partId));
    if (changed || !validPart || _resolvedCompatibleVariant == null) {
      _restoringProductionGeneration = machineGeneration;
      _restoredProductionId = active.id;
      if (changed || !validPart) {
        _selectedEffectivePart = EffectivePartSelection(
          partId: main.partId,
          partCode: main.partCode,
          isMain: true,
        );
      }
      _resolvedCompatibleVariant = null;
      final generation = ++_partResolutionGeneration;
      try {
        await _loadCompatibleVariants(generation);
      } finally {
        if (_restoringProductionGeneration == machineGeneration) {
          _restoringProductionGeneration = null;
        }
      }
    }
  }

  /// Customer UX — Automatic Machine Slot Resolution: builds a command with
  /// no machine-side slot chosen, so the backend resolves and reserves it
  /// atomically from master.layout_slot_parts using [partId]. Always called
  /// with a compatible [_selectedEffectivePart], which is guaranteed non-null
  /// once the operation buttons are visible on the selection screen.
  TransportCommand commandForDirect(String operation) {
    final current = _context;
    final part = _selectedEffectivePart;
    final resolvedVariant = _resolvedCompatibleVariant;
    if (current == null || part == null || resolvedVariant == null) {
      throw StateError('Aktif işlem bağlamı bulunmuyor.');
    }
    final variantId = current.activeVariantId;
    final layoutId = current.activeLayoutId;
    if (variantId == null || layoutId == null) {
      throw StateError('Aktif varyant veya layout bulunmuyor.');
    }
    if (variantId != resolvedVariant.variantId) {
      throw StateError('Seçilen parça aktif varyantla artık uyumlu değil.');
    }
    final productGroup = _firstOrNull(
      current.productGroups.where(
        (group) => group.id == resolvedVariant.productGroupId,
      ),
    );
    if (productGroup == null) {
      throw StateError(
        'Seçilen parçanın ürün grubu aktif bağlamda bulunamadı.',
      );
    }
    final relatedPart = _firstOrNull(
      productGroup.parts.where((candidate) => candidate.partId == part.partId),
    );
    if (productGroup.mainPartId != part.partId && relatedPart == null) {
      throw StateError('Seçilen parça ürün grubunda bulunamadı.');
    }
    return TransportCommand(
      machineId: current.machine.id,
      variantId: variantId,
      layoutId: layoutId,
      navigationProfileId: current.activeNavigationProfileId,
      productGroupId: productGroup.id,
      partId: part.partId,
      containerTypeId: relatedPart?.containerTypeId,
      operation: operation,
      sourceSlotId: null,
      destinationSlotId: null,
      priority: 100,
    );
  }

  Future<TransportResult?> submitTransport(TransportCommand command) async {
    if (!productionTransportReady) return null;
    if (!_session.isAuthenticated ||
        _busy ||
        (_offline && !_submission.hasAmbiguousRequest)) {
      return null;
    }
    if (!_submission.hasAmbiguousRequest &&
        _operationInProgress(command.operation, command.partId)) {
      _notice = UiNotice(
        'Bu işlem için aktif bir taşıma talebi zaten var.',
        UiNoticeSeverity.error,
      );
      notifyListeners();
      return null;
    }
    _setBusy(true);
    _submittingOperation = command.operation;
    _clearFeedback();
    try {
      final result = await _submission.submit(
        command,
        _repository.createTransport,
      );
      _notice = UiNotice(
        result.idempotentReplay
            ? 'Mevcut talep doğrulandı.'
            : 'Taşıma talebi oluşturuldu.',
        UiNoticeSeverity.success,
      );
      await refreshContext();
      _ambiguousCommand = null;
      return result;
    } on AmbiguousMutationException {
      _ambiguousCommand = command;
      _offline = true;
      _notice = const UiNotice(
        'Gönderim sonucu belirsiz. Aynı komut aynı anahtarla yeniden denenebilir veya durum yenilenebilir.',
        UiNoticeSeverity.warning,
      );
      return null;
    } on ApiProblem catch (problem) {
      // refreshContext() unconditionally clears _problem on success, so it
      // must run first — otherwise a refetch (409/422/428) would silently
      // wipe the operator-facing message we are about to show, including
      // the friendly no-eligible-slot text from automatic machine-slot
      // resolution (BRING_EMPTY_SLOT_NOT_FOUND and friends).
      if (problem.shouldRefetch) {
        await refreshContext();
      }
      _problem = problem;
      return null;
    } on StateError {
      _notice = UiNotice(
        'İşlem bilgileri değişti. Durumu yenileyip tekrar deneyin.',
        UiNoticeSeverity.warning,
      );
      return null;
    } finally {
      _submittingOperation = null;
      _setBusy(false);
    }
  }

  Future<TransportResult?> retryAmbiguousTransport() async {
    if (_removeAllAmbiguous && _removeAllKey != null) {
      _offline = false;
      await removeAllContainers();
      return null;
    }
    if (_ambiguousPendingOperation case final pending?) {
      _offline = false;
      await _retryPendingOperation(pending);
      return null;
    }
    if (_ambiguousContainerPosition case final position?) {
      final action = _ambiguousContainerAction;
      if (action != null) {
        _offline = false;
        await submitContainerAction(position, action);
      }
      return null;
    }
    final command = _ambiguousCommand;
    if (command == null || !_submission.hasAmbiguousRequest) {
      return null;
    }
    _offline = false;
    return submitTransport(command);
  }

  Future<void> _retryPendingOperation(PendingOperation pending) async {
    _activePendingOperation = pending;
    final submissionId =
        '${pending.logicalContainerNumber}:${pending.operation}';
    _submittingContainerActions.add(submissionId);
    _submittingOperation = pending.operation;
    _clearFeedback();
    notifyListeners();
    try {
      final result = await _repository.createContainerAction(
        machineId: pending.machineId,
        variantId: pending.variantId,
        layoutId: pending.layoutId,
        navigationProfileId: pending.navigationProfileId,
        productGroupId: pending.productGroupId ?? '',
        partId: pending.partId,
        positionNumber: pending.logicalContainerNumber ?? 1,
        action: pending.operation,
        idempotencyKey: pending.idempotencyKey,
        productionSessionId: pending.productionSessionId,
      );
      _activePendingOperation = null;
      _ambiguousPendingOperation = null;
      _ambiguousContainerPosition = null;
      _ambiguousContainerAction = null;
      _notice = UiNotice(
        result.idempotentReplay
            ? 'Mevcut kasa talebi doğrulandı.'
            : '${pending.logicalContainerNumber ?? 1}. Kasa talebi oluşturuldu.',
        UiNoticeSeverity.success,
      );
      await refreshContext();
    } on AmbiguousMutationException {
      _activePendingOperation = null;
      _ambiguousPendingOperation = pending;
      _offline = true;
      _notice = const UiNotice(
        'Gönderim sonucu belirsiz. Aynı kasa işlemi aynı anahtarla yeniden denenecek.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _activePendingOperation = null;
      _ambiguousPendingOperation = null;
      if (problem.shouldRefetch) await refreshContext();
      _problem = problem;
    } finally {
      _activePendingOperation = null;
      _submittingContainerActions.remove(submissionId);
      _submittingOperation = null;
      notifyListeners();
    }
  }

  Future<void> cancelTransport(ActiveTransportRequest request) async {
    if (_stage != AppStage.operation ||
        _selectedMachine == null ||
        !request.canCancel ||
        !mutationGate.canMutate) {
      return;
    }
    _setBusy(true);
    _clearFeedback();
    try {
      final result = await _repository.cancel(
        machineId: _selectedMachine!.id,
        requestId: request.requestId,
      );
      _notice = UiNotice(
        result.status == 'CANCELLED'
            ? 'Talep iptal edildi.'
            : 'İptal durumu sunucudan güncellendi.',
        UiNoticeSeverity.success,
      );
      await refreshContext();
    } on AmbiguousMutationException {
      _offline = true;
      _notice = const UiNotice(
        'İptal sonucunun fiziksel olarak tamamlandığı doğrulanamadı. Durumu yenileyin.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _problem = problem;
      if (problem.shouldRefetch) {
        await refreshContext();
      }
    } finally {
      _setBusy(false);
    }
  }

  Future<void> onResumed() async {
    _foreground = true;
    if (!_session.isAuthenticated) {
      _resumeValidationPending = false;
      notifyListeners();
      return;
    }
    _resumeValidationPending = true;
    notifyListeners();
    try {
      await _repository.me();
      if (_selectedMachine != null) {
        await refreshContext(showBusy: true);
      } else {
        await reloadMachines();
      }
    } on ConnectivityException {
      _offline = true;
      _notice = UiNotice(
        'Bağlantı yok — bilgiler güncel olmayabilir.',
        UiNoticeSeverity.warning,
      );
    } on ApiProblem catch (problem) {
      _problem = problem;
    } finally {
      _resumeValidationPending = false;
      if (!_offline && _selectedMachine != null) _schedulePolling();
      notifyListeners();
    }
  }

  void onPaused() {
    _foreground = false;
    _resumeValidationPending = _session.isAuthenticated;
    _stopPolling();
    notifyListeners();
  }

  Future<void> _loadMachines() async {
    _machines = await _repository.machines();
  }

  bool _operationInProgress(String operation, String? partId) =>
      partId != null &&
      (_context?.activeTransportRequests.any(
            (request) =>
                request.partId == partId && request.operation == operation,
          ) ??
          false);

  void _schedulePolling() {
    _stopPolling();
    if (!_enablePolling ||
        !_foreground ||
        _offline ||
        _selectedMachine == null) {
      return;
    }
    final interval = (_context?.activeTransportRequests.isNotEmpty ?? false)
        ? const Duration(seconds: 4)
        : const Duration(seconds: 15);
    _pollTimer = Timer(interval, () => unawaited(refreshContext()));
  }

  void _stopPolling() {
    _pollTimer?.cancel();
    _pollTimer = null;
  }

  void _clearOperationalState() {
    _machineGeneration++;
    production.reset(null);
    _restoredProductionId = _productionContextError = null;
    _stopPolling();
    _stopQueuePolling();
    _machines = const [];
    _selectedMachine = null;
    _context = null;
    _resetPartFirstState();
    _offline = false;
    _resumeValidationPending = false;
    _warehouseRequests = const [];
    _warehouseError = null;
    _warehouseLoading = false;
    _warehouseMarkingReady.clear();
    _robotAlarmSummary = null;
    _robotAlarmError = null;
    _ambiguousContainerPosition = null;
    _ambiguousContainerAction = null;
    _resetContainerPositions();
    _containerActionKeys.clear();
    _removeAllKey = null;
    _removeAllSubmitting = false;
    _removeAllAmbiguous = false;
    _operatorQueue = null;
    _operatorQueueError = null;
    _operatorQueueLoading = false;
    _operatorQueueTerminal = false;
    _taskReturnStage = null;
    _containerBatchKey = null;
    _containerBatchAmbiguous = false;
    _activePendingOperation = null;
    _ambiguousPendingOperation = null;
  }

  void _clearFeedback() {
    _problem = null;
    _notice = null;
  }

  void _setBusy(bool value) {
    _busy = value;
    notifyListeners();
  }

  @override
  void dispose() {
    _stopHeartbeat();
    _stopPolling();
    _stopQueuePolling();
    preparation.stop();
    preparation.dispose();
    store.stop();
    store.dispose();
    production.removeListener(notifyListeners);
    production.dispose();
    warehouseNotifications.removeListener(notifyListeners);
    warehouseNotifications.dispose();
    super.dispose();
  }
}

T? _firstOrNull<T>(Iterable<T> values) {
  final iterator = values.iterator;
  return iterator.moveNext() ? iterator.current : null;
}
