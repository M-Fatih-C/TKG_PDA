import 'dart:async';

import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/data/models/preparation_area.dart';
import 'package:tkg_pda/data/models/store_location.dart';
import 'package:tkg_pda/data/repositories/pda_repository.dart';

const testDeviceCode = 'PDA-TEST-01';

final testUser = AuthenticatedUser(
  id: '10000000-0000-0000-0000-000000000001',
  username: 'operator',
  fullName: 'Test Operatör',
  employeeNo: null,
  roles: const ['LINE_OPERATOR'],
);

final testSupervisor = AuthenticatedUser(
  id: '10000000-0000-0000-0000-000000000002',
  username: 'supervisor-user',
  fullName: 'Supervisor User',
  employeeNo: null,
  roles: const ['SUPERVISOR'],
);

final testDevice = PdaDevice(
  id: '20000000-0000-0000-0000-000000000001',
  code: testDeviceCode,
  name: 'Test PDA',
  type: 'PDA',
  machineId: '30000000-0000-0000-0000-000000000001',
);

final testMachine = MachineSummary(
  id: '30000000-0000-0000-0000-000000000001',
  code: 'SH-01-A',
  displayName: 'SH-01-A Hücresi',
  displayOrder: 1,
  permissions: const MachinePermissions(
    canView: true,
    canOperate: true,
    canChangeVariant: true,
    canManageSlots: false,
  ),
);

const testVariantV1Id = '40000000-0000-0000-0000-000000000001';
const testVariantV2Id = '40000000-0000-0000-0000-000000000002';
const testProductGroupId = '60000000-0000-0000-0000-000000000001';
const testMainPartId = '70000000-0000-0000-0000-000000000001';
const testAltPartId = '70000000-0000-0000-0000-000000000002';
const testRelatedPartId = '70000000-0000-0000-0000-000000000003';
const testOrphanPartId = '70000000-0000-0000-0000-000000000004';
const testAmbiguousMainPartId = '70000000-0000-0000-0000-000000000005';

OperationalContext testContext({
  List<ActiveTransportRequest> activeRequests = const [],
  String activeVariantId = testVariantV1Id,
  List<Map<String, Object?>> additionalParts = const [],
}) {
  final isV2 = activeVariantId == testVariantV2Id;
  return OperationalContext.fromJson({
    'machine': _machineJson,
    'activeVariantId': activeVariantId,
    'activeVariantCode': isV2 ? 'V2' : 'V1',
    'activeVariantName': isV2 ? 'Varyant 2' : 'Varyant 1',
    'activeLayoutId': '50000000-0000-0000-0000-000000000001',
    'activeLayoutCode': 'LAYOUT-A',
    'activeLayoutName': 'SH-01-A Yerleşimi',
    'activeNavigationProfileId': null,
    'runtimeRowVersion': 1,
    'variantLayouts': [
      {
        'variantId': testVariantV1Id,
        'variantCode': 'V1',
        'variantName': 'Varyant 1',
        'layoutId': '50000000-0000-0000-0000-000000000001',
        'layoutCode': 'LAYOUT-A',
        'layoutName': 'SH-01-A Yerleşimi',
        'isDefault': true,
      },
      {
        'variantId': testVariantV2Id,
        'variantCode': 'V2',
        'variantName': 'Varyant 2',
        'layoutId': '50000000-0000-0000-0000-000000000001',
        'layoutCode': 'LAYOUT-A',
        'layoutName': 'SH-01-A Yerleşimi',
        'isDefault': true,
      },
    ],
    'slots': [_machineSlot, _vacantSlot, _emptySlot, _fullSlot],
    'productGroups': [
      {
        'id': testProductGroupId,
        'mainPartId': testMainPartId,
        'mainPartCode': '71550BN000-10-HAOS',
        'name': 'Ana Parça',
        'displayOrder': 1,
        'parts': [
          {
            'id': '80000000-0000-0000-0000-000000000001',
            'partId': testAltPartId,
            'partCode': '715A2BN400-50',
            'containerTypeId': '70000000-0000-0000-0000-000000000001',
            'containerCode': 'KASA',
            'relationType': 'ALT_PART',
            'lotQuantity': null,
            'displayOrder': 1,
          },
          {
            'id': '80000000-0000-0000-0000-000000000002',
            'partId': testRelatedPartId,
            'partCode': '715B3CN500-60',
            'containerTypeId': null,
            'containerCode': null,
            'relationType': 'RELATED',
            'lotQuantity': null,
            'displayOrder': 2,
          },
          ...additionalParts,
        ],
      },
    ],
    'activeTransportRequests': activeRequests
        .map(
          (request) => {
            'requestId': request.requestId,
            'requestNo': request.requestNo,
            'operation': request.operation,
            'status': request.status,
            'requestedBy': request.requestedBy,
            'requestedByUsername': request.requestedByUsername,
            'isMine': request.isMine,
            'sourceSlotId': request.sourceSlotId,
            'destinationSlotId': request.destinationSlotId,
            'productGroupId': request.productGroupId,
            'partId': request.partId,
            'containerTypeId': request.containerTypeId,
            'priority': request.priority,
            'createdAt': request.createdAt.toIso8601String(),
            'partCode': request.partCode,
            'partDescription': request.partDescription,
            'relationType': request.relationType,
            'readinessState': request.readinessState,
            'queuePosition': request.queuePosition,
            'ageSeconds': request.ageSeconds,
            'executionElapsedSeconds': request.executionElapsedSeconds,
            'canCancel': request.canCancel,
            'cancelDisabledCode': request.cancelDisabledCode,
            'cancelDisabledMessage': request.cancelDisabledMessage,
            'execution': {
              'state': request.execution.state,
              'lastSyncAt': request.execution.lastSyncAt?.toIso8601String(),
              'failureCode': request.execution.failureCode,
              'failureCategory': request.execution.failureCategory,
              'operatorMessage': request.execution.operatorMessage,
              'correlationId': request.execution.correlationId,
            },
          },
        )
        .toList(),
    'taskTemplates': [],
  });
}

final testActiveRequest = ActiveTransportRequest.fromJson({
  'requestId': '90000000-0000-0000-0000-000000000001',
  'requestNo': 42,
  'operation': 'BRING_EMPTY',
  'status': 'QUEUED',
  'requestedBy': testUser.id,
  'requestedByUsername': testUser.username,
  'isMine': true,
  'sourceSlotId': null,
  'destinationSlotId': 'slot-vacant',
  'productGroupId': testProductGroupId,
  'partId': testMainPartId,
  'containerTypeId': null,
  'priority': 100,
  'createdAt': '2026-08-24T08:00:00Z',
  'canCancel': true,
  'cancelDisabledCode': null,
  'cancelDisabledMessage': null,
  'execution': {
    'state': 'WAITING_FOR_ROBOT',
    'rdsTaskRecordId': null,
    'rdsTaskLabel': null,
    'outOrderNo': null,
    'agvId': null,
    'rdsStatus': null,
    'lastSyncAt': null,
    'failureCode': null,
    'failureCategory': null,
    'operatorMessage': null,
    'correlationId': 'a0000000-0000-0000-0000-000000000001',
  },
});

// Customer UI — Warehouse Preparation Console fixture.
final testWarehousePreparationRequest = WarehousePreparationRequest.fromJson({
  'requestId': '90000000-0000-0000-0000-000000000002',
  'requestNo': 55,
  'operation': 'BRING_EMPTY',
  'machineId': testMachine.id,
  'machineCode': testMachine.code,
  'machineDisplayName': testMachine.displayName,
  'partCode': '71550BN000-10-HAOS',
  'containerTypeCode': null,
  'sourceSlotId': null,
  'sourceSlotCode': null,
  'sourceSlotDisplayName': null,
  'destinationSlotId': 'slot-vacant',
  'destinationSlotCode': 'SLOT-V',
  'destinationSlotDisplayName': 'Boş Konum',
  'createdAt': '2026-08-24T08:00:00Z',
  'queuedAt': '2026-08-24T08:00:01Z',
});

// WP-03B: part-first fixtures. testMainPartId matches testContext's
// productGroups[0].mainPartId; testAltPartId matches its ALT_PART child —
// so the "already active" auto-resolve and auto-carry-into-material paths
// can be exercised end to end.
final testMainPartOption = MachineMainPartOption(
  partId: testMainPartId,
  partCode: '71550BN000-10-HAOS',
  description: 'Ana parça',
  relatedParts: const [
    MachineRelatedPart(
      partId: testAltPartId,
      partCode: '715A2BN400-50',
      description: null,
      relationType: 'ALT_PART',
    ),
    MachineRelatedPart(
      partId: testRelatedPartId,
      partCode: '715B3CN500-60',
      description: null,
      relationType: 'RELATED',
    ),
    MachineRelatedPart(
      partId: testOrphanPartId,
      partCode: '715C4DN600-70',
      description: null,
      relationType: 'RELATED',
    ),
  ],
);

final testSecondMainPartOption = MachineMainPartOption(
  partId: '70000000-0000-0000-0000-000000000010',
  partCode: '900-OTHER-MAIN',
  description: null,
  relatedParts: const [],
);

/// A main part whose OWN compatible-variant lookup is ambiguous (two
/// variants), so one-click selecting it leaves the part-first gate open —
/// the case where its RELATED/ALT alternatives actually stay on screen and
/// can be chosen instead. Shares [testMainPartOption]'s children so the
/// existing child-part fixtures/variant mappings apply unchanged.
final testAmbiguousMainPartOption = MachineMainPartOption(
  partId: testAmbiguousMainPartId,
  partCode: '71570BN000-10-HAOS',
  description: null,
  relatedParts: testMainPartOption.relatedParts,
);

final testPartOptions = [
  testMainPartOption,
  testSecondMainPartOption,
  testAmbiguousMainPartOption,
];

/// Keyed by partId. Main part -> single, already-active variant (V1, no
/// switch needed). Ambiguous main part -> two variants (gate stays open).
/// Alt part -> single, DIFFERENT variant (V2, requires switchVariant).
/// Related part -> two variants (requires user selection). Orphan part ->
/// no mapping (empty list).
final testCompatibleVariantsByPart = <String, List<CompatibleVariant>>{
  testMainPartId: [
    const CompatibleVariant(
      variantId: testVariantV1Id,
      variantCode: 'V1',
      variantName: 'Varyant 1',
      machineId: '30000000-0000-0000-0000-000000000001',
      productGroupId: testProductGroupId,
      relationType: 'MAIN',
    ),
  ],
  testAmbiguousMainPartId: [
    const CompatibleVariant(
      variantId: testVariantV1Id,
      variantCode: 'V1',
      variantName: 'Varyant 1',
      machineId: '30000000-0000-0000-0000-000000000001',
      productGroupId: testProductGroupId,
      relationType: 'MAIN',
    ),
    const CompatibleVariant(
      variantId: testVariantV2Id,
      variantCode: 'V2',
      variantName: 'Varyant 2',
      machineId: '30000000-0000-0000-0000-000000000001',
      productGroupId: testProductGroupId,
      relationType: 'MAIN',
    ),
  ],
  testAltPartId: [
    const CompatibleVariant(
      variantId: testVariantV2Id,
      variantCode: 'V2',
      variantName: 'Varyant 2',
      machineId: '30000000-0000-0000-0000-000000000001',
      productGroupId: testProductGroupId,
      relationType: 'ALT_PART',
    ),
  ],
  testRelatedPartId: [
    const CompatibleVariant(
      variantId: testVariantV1Id,
      variantCode: 'V1',
      variantName: 'Varyant 1',
      machineId: '30000000-0000-0000-0000-000000000001',
      productGroupId: testProductGroupId,
      relationType: 'RELATED',
    ),
    const CompatibleVariant(
      variantId: testVariantV2Id,
      variantCode: 'V2',
      variantName: 'Varyant 2',
      machineId: '30000000-0000-0000-0000-000000000001',
      productGroupId: testProductGroupId,
      relationType: 'RELATED',
    ),
  ],
  testOrphanPartId: const [],
};

class FakePdaRepository implements PdaDataSource {
  ProductionSession? productionValue;
  Object? productionReadError, productionStartError, productionEndError;
  Completer<ProductionSession>? productionStartCompleter;
  Completer<ProductionSession>? productionEndCompleter;
  final List<String> productionEndIds = [];
  final List<String> productionEndKeys = [];
  ProductionSession? productionEndSession;
  Completer<ProductionSession?>? productionReadCompleter;
  int productionStartCount = 0, productionEndCount = 0;
  final List<String> productionStartKeys = [];
  String? lastProductionSessionId;
  @override
  Future<ProductionSession?> activeProduction(String machineId) async {
    if (productionReadError case final error?) throw error;
    if (productionReadCompleter case final completer?) return completer.future;
    return productionValue;
  }

  @override
  Future<ProductionSession> startProduction(
    ProductionStartCommand command,
    String key,
  ) async {
    productionStartCount++;
    productionStartKeys.add(key);
    if (productionStartError case final error?) throw error;
    if (productionStartCompleter case final completer?) {
      return productionValue = await completer.future;
    }
    return productionValue = ProductionSession(
      id: 'production-$productionStartCount',
      machineId: command.machineId,
      variantId: command.variantId,
      productGroupId: command.productGroupId,
      mainPartId: command.mainPartId,
      startedAt: DateTime.utc(2026, 9, 15, 8),
    );
  }

  @override
  Future<ProductionSession> endProduction(
    String machineId,
    String sessionId,
    String idempotencyKey,
  ) async {
    productionEndCount++;
    productionEndIds.add(sessionId);
    productionEndKeys.add(idempotencyKey);
    if (productionEndError case final error?) throw error;
    if (productionEndCompleter case final completer?) return completer.future;
    final current = productionValue!;
    if (productionEndSession case final ending?) {
      productionValue = ending;
      return ending;
    }
    productionValue = null;
    return ProductionSession(
      id: sessionId,
      machineId: machineId,
      variantId: current.variantId,
      productGroupId: current.productGroupId,
      mainPartId: current.mainPartId,
      startedAt: current.startedAt,
      endedAt: current.startedAt.add(const Duration(hours: 1)),
      status: 'ENDED',
    );
  }

  AuthenticatedUser loginUser = testUser;
  PdaDevice loginDevice = testDevice;
  List<MachineSummary> machineValues = [testMachine];
  OperationalContext contextValue = testContext();
  ApiProblem? loginProblem;
  Object? contextError;
  Object? createError;
  int loginCount = 0;
  String? lastEmployeeNumber;
  int createCount = 0;
  int batchCreateCount = 0;
  String? lastBatchMachineId;
  String? lastBatchIdempotencyKey;
  List<TransportBatchItemCommand>? lastBatchItems;
  Object? batchCreateError;
  Completer<TransportBatchResult>? batchCreateCompleter;
  int cancelCount = 0;
  String? lastCancelMachineId;
  String? lastCancelRequestId;
  List<WarehousePreparationRequest> warehouseRequestsValue = const [];
  Object? warehouseRequestsError;
  int warehouseRequestsCount = 0;
  Object? markReadyError;
  int markReadyCount = 0;
  String? lastMarkReadyRequestId;
  RobotAlarmSummary robotAlarmSummaryValue = const RobotAlarmSummary(
    hasActiveRobotAlarm: false,
    highestSeverity: null,
    activeAlarmCount: 0,
  );
  Object? robotAlarmSummaryError;
  int robotAlarmSummaryCount = 0;
  int switchCount = 0;
  String? lastSwitchVariantId;
  Object? switchVariantError;
  int contextCount = 0;
  int meCount = 0;
  List<MachineMainPartOption> partOptionsValue = testPartOptions;
  Object? partOptionsError;
  int partOptionsCount = 0;
  Map<String, List<CompatibleVariant>> compatibleVariantsValue =
      testCompatibleVariantsByPart;
  Object? compatibleVariantsError;
  int compatibleVariantsCount = 0;
  final List<String> compatibleVariantsRequestedMainPartIds = [];
  final List<String> compatibleVariantsRequestedPartIds = [];
  String? lastIdempotencyKey;
  TransportCommand? lastCommand;
  Completer<TransportResult>? createCompleter;
  Completer<AuthenticatedUser>? meCompleter;
  List<ContainerPosition> containerPositionsValue = const [
    ContainerPosition(
      positionNumber: 1,
      label: '1. Kasa',
      fullAction: ContainerActionAvailability(
        enabled: true,
        status: 'READY',
        disabledCode: null,
        disabledMessage: null,
      ),
      emptyAction: ContainerActionAvailability(
        enabled: true,
        status: 'READY',
        disabledCode: null,
        disabledMessage: null,
      ),
      transportState: 'READY',
      queuePosition: null,
      etaSeconds: null,
      etaState: 'UNAVAILABLE',
    ),
  ];
  int containerActionCount = 0;
  int containerActionBatchCount = 0;
  ActiveTransportRequest? containerActionActiveRequest;
  int removeAllCount = 0;
  Object? removeAllError;
  Completer<RemoveAllResult>? removeAllCompleter;
  String? lastRemoveAllIdempotencyKey;
  String? lastRemoveAllMachineId;
  String? lastRemoveAllVariantId;
  OperatorQueuePage operatorQueueValue = const OperatorQueuePage(
    terminal: false,
    offset: 0,
    limit: 50,
    hasMore: false,
    items: [],
  );

  @override
  Future<LoginSession> login(String employeeNumber) async {
    loginCount++;
    lastEmployeeNumber = employeeNumber;
    if (loginProblem case final problem?) throw problem;
    return LoginSession(
      accessToken: 'runtime-only-test-token',
      expiresAt: DateTime.now().toUtc().add(const Duration(minutes: 10)),
      user: loginUser,
      device: loginDevice,
    );
  }

  int heartbeatCount = 0;
  int logoutCount = 0;
  Object? heartbeatError;
  Object? logoutError;

  @override
  Future<void> heartbeatSession() async {
    heartbeatCount++;
    if (heartbeatError case final error?) throw error;
  }

  @override
  Future<void> logout() async {
    logoutCount++;
    if (logoutError case final error?) throw error;
  }

  int notificationRegistrationCount = 0;
  Object? notificationRegistrationError;
  DeviceNotificationRegistration notificationRegistration =
      const DeviceNotificationRegistration(
        registrationId: 'reg-1',
        credential: 'test-credential',
        pollIntervalSeconds: 5,
      );

  @override
  Future<DeviceNotificationRegistration> registerDeviceNotification() async {
    notificationRegistrationCount++;
    if (notificationRegistrationError case final error?) throw error;
    return notificationRegistration;
  }

  @override
  Future<AuthenticatedUser> me() async {
    meCount++;
    if (meCompleter case final completer?) return completer.future;
    return testUser;
  }

  Object? machinesError;

  @override
  Future<List<MachineSummary>> machines() async {
    if (machinesError case final error?) throw error;
    return machineValues;
  }

  @override
  Future<OperationalContext> operationalContext(String machineId) async {
    contextCount++;
    if (contextError case final error?) throw error;
    return contextValue;
  }

  @override
  Future<void> switchVariant({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
  }) async {
    switchCount++;
    lastSwitchVariantId = variantId;
    if (switchVariantError case final error?) throw error;
    contextValue = testContext(activeVariantId: variantId);
  }

  @override
  Future<List<MachineMainPartOption>> machinePartOptions(
    String machineId,
  ) async {
    partOptionsCount++;
    if (partOptionsError case final error?) throw error;
    return partOptionsValue;
  }

  @override
  Future<List<CompatibleVariant>> compatibleVariants({
    required String machineId,
    required String mainPartId,
    required String partId,
  }) async {
    compatibleVariantsCount++;
    compatibleVariantsRequestedMainPartIds.add(mainPartId);
    compatibleVariantsRequestedPartIds.add(partId);
    if (compatibleVariantsError case final error?) throw error;
    return compatibleVariantsValue[partId] ?? const [];
  }

  @override
  Future<TransportResult> createTransport(
    TransportCommand command,
    String idempotencyKey,
  ) async {
    createCount++;
    lastCommand = command;
    lastIdempotencyKey = idempotencyKey;
    if (createError case final error?) throw error;
    if (createCompleter case final completer?) return completer.future;
    contextValue = testContext(activeRequests: [testActiveRequest]);
    return const TransportResult(
      id: '90000000-0000-0000-0000-000000000001',
      requestNo: 42,
      status: 'QUEUED',
      operation: 'BRING_EMPTY',
      idempotentReplay: false,
    );
  }

  @override
  Future<TransportBatchResult> createTransportBatch(
    String machineId,
    String batchIdempotencyKey,
    List<TransportBatchItemCommand> items,
  ) async {
    batchCreateCount++;
    lastBatchMachineId = machineId;
    lastBatchIdempotencyKey = batchIdempotencyKey;
    lastBatchItems = items;
    if (batchCreateError case final error?) throw error;
    if (batchCreateCompleter case final completer?) return completer.future;
    contextValue = testContext(activeRequests: [testActiveRequest]);
    return TransportBatchResult(
      batchId: '95000000-0000-0000-0000-000000000001',
      idempotentReplay: false,
      items: [
        for (var i = 0; i < items.length; i++)
          TransportResult(
            id: '9500000$i-0000-0000-0000-00000000000$i',
            requestNo: 100 + i,
            status: 'QUEUED',
            operation: items[i].command.operation,
            idempotentReplay: false,
          ),
      ],
    );
  }

  @override
  Future<List<ContainerPosition>> containerPositions({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String productGroupId,
    required String partId,
  }) async {
    lastContainerPositionPartId = partId;
    return containerPositionsValue;
  }

  String? lastContainerPositionPartId;

  int partDemandCount = 0;
  int partDemandBatchCount = 0;
  List<String> lastPartDemandIds = const [];
  String? lastPartDemandKey;
  Object? partDemandError;

  @override
  Future<PartDemandResult> createPartDemand({
    required String machineId,
    required String partId,
    required String idempotencyKey,
  }) async {
    partDemandCount++;
    lastPartDemandIds = [partId];
    lastPartDemandKey = idempotencyKey;
    if (partDemandError case final error?) throw error;
    return const PartDemandResult(
      requestNo: 1,
      taskType: 'BRING_FULL',
      status: 'QUEUED',
      readiness: 'WAITING_PREPARATION',
      idempotentReplay: false,
    );
  }

  @override
  Future<PartDemandBatchResult> createPartDemandBatch({
    required String machineId,
    required List<String> partIds,
    required String idempotencyKey,
  }) async {
    partDemandBatchCount++;
    lastPartDemandIds = partIds;
    lastPartDemandKey = idempotencyKey;
    return PartDemandBatchResult(
      idempotentReplay: false,
      items: [
        for (var index = 0; index < partIds.length; index++)
          PartDemandResult(
            requestNo: index + 1,
            taskType: 'BRING_FULL',
            status: 'QUEUED',
            readiness: 'WAITING_PREPARATION',
            idempotentReplay: false,
          ),
      ],
    );
  }

  @override
  Future<ContainerActionResult> createContainerAction({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
    required String productGroupId,
    required String partId,
    required int positionNumber,
    required String action,
    required String idempotencyKey,
    String? productionSessionId,
  }) async {
    containerActionCount++;
    lastProductionSessionId = productionSessionId;
    createCount++;
    lastIdempotencyKey = idempotencyKey;
    lastCommand = TransportCommand(
      machineId: machineId,
      variantId: variantId,
      layoutId: layoutId,
      navigationProfileId: navigationProfileId,
      productGroupId: productGroupId,
      partId: partId,
      containerTypeId: null,
      operation: action == 'FULL' ? 'REMOVE_FULL' : 'BRING_EMPTY',
      sourceSlotId: null,
      destinationSlotId: null,
      priority: 100,
    );
    if (createError case final error?) throw error;
    if (createCompleter case final completer?) {
      await completer.future;
    }
    containerPositionsValue = [
      ContainerPosition(
        positionNumber: positionNumber,
        label: '$positionNumber. Kasa',
        fullAction: const ContainerActionAvailability(
          enabled: false,
          status: 'QUEUED',
          disabledCode: 'SLOT_BUSY',
          disabledMessage:
              'Bu kasa konumunda aktif bir taşıma talebi bulunuyor.',
        ),
        emptyAction: const ContainerActionAvailability(
          enabled: false,
          status: 'QUEUED',
          disabledCode: 'SLOT_BUSY',
          disabledMessage:
              'Bu kasa konumunda aktif bir taşıma talebi bulunuyor.',
        ),
        transportState: 'QUEUED',
        queuePosition: 1,
        etaSeconds: null,
        etaState: 'UNAVAILABLE',
      ),
    ];
    contextValue = testContext(
      activeRequests: [containerActionActiveRequest ?? testActiveRequest],
    );
    return ContainerActionResult(
      positionNumber: positionNumber,
      action: action,
      requestNo: 101,
      status: 'QUEUED',
      idempotentReplay: false,
    );
  }

  @override
  Future<TransportBatchResult> createContainerActionBatch({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
    required List<ContainerActionBatchItemCommand> items,
    required String idempotencyKey,
    String? productionSessionId,
  }) async {
    containerActionBatchCount++;
    lastProductionSessionId = productionSessionId;
    batchCreateCount++;
    lastBatchMachineId = machineId;
    lastBatchIdempotencyKey = idempotencyKey;
    lastBatchItems = [
      for (final (index, item) in items.indexed)
        TransportBatchItemCommand(
          idempotencyKey: '$idempotencyKey:$index',
          command: TransportCommand(
            machineId: machineId,
            variantId: variantId,
            layoutId: layoutId,
            navigationProfileId: navigationProfileId,
            productGroupId: item.productGroupId,
            partId: item.partId,
            containerTypeId: null,
            operation: item.action == 'FULL' ? 'REMOVE_FULL' : 'BRING_EMPTY',
            sourceSlotId: null,
            destinationSlotId: null,
            priority: 100,
          ),
        ),
    ];
    if (batchCreateError case final error?) throw error;
    if (batchCreateCompleter case final completer?) return completer.future;
    return TransportBatchResult(
      batchId: '96000000-0000-0000-0000-000000000001',
      idempotentReplay: false,
      items: [
        for (var index = 0; index < items.length; index++)
          TransportResult(
            id: '9600000$index-0000-0000-0000-00000000000$index',
            requestNo: 200 + index,
            status: 'QUEUED',
            operation: items[index].action == 'FULL'
                ? 'REMOVE_FULL'
                : 'BRING_EMPTY',
            idempotentReplay: false,
          ),
      ],
    );
  }

  @override
  Future<RemoveAllResult> removeAllContainers({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
    required String idempotencyKey,
    String? productionSessionId,
  }) async {
    removeAllCount++;
    lastProductionSessionId = productionSessionId;
    lastRemoveAllIdempotencyKey = idempotencyKey;
    lastRemoveAllMachineId = machineId;
    lastRemoveAllVariantId = variantId;
    if (removeAllError case final error?) throw error;
    if (removeAllCompleter case final completer?) {
      return completer.future;
    }
    return const RemoveAllResult(
      idempotentReplay: false,
      ignoredVacantPositions: [],
      itemCount: 2,
    );
  }

  @override
  Future<OperatorQueuePage> operatorQueue({
    bool terminal = false,
    int offset = 0,
    int limit = 50,
  }) async => operatorQueueValue;

  @override
  Future<TransportResult> transport(String requestId) async =>
      const TransportResult(
        id: '90000000-0000-0000-0000-000000000001',
        requestNo: 42,
        status: 'QUEUED',
        operation: 'BRING_EMPTY',
        idempotentReplay: false,
      );

  @override
  Future<TransportResult> cancel({
    required String machineId,
    required String requestId,
  }) async {
    cancelCount++;
    lastCancelMachineId = machineId;
    lastCancelRequestId = requestId;
    contextValue = testContext();
    return const TransportResult(
      id: '90000000-0000-0000-0000-000000000001',
      requestNo: 42,
      status: 'CANCELLED',
      operation: 'BRING_EMPTY',
      idempotentReplay: false,
    );
  }

  @override
  Future<List<WarehousePreparationRequest>>
  warehousePreparationRequests() async {
    warehouseRequestsCount++;
    if (warehouseRequestsError case final error?) throw error;
    return warehouseRequestsValue;
  }

  @override
  Future<void> markTransportReady(String requestId) async {
    markReadyCount++;
    lastMarkReadyRequestId = requestId;
    if (markReadyError case final error?) throw error;
    warehouseRequestsValue = warehouseRequestsValue
        .where((request) => request.requestId != requestId)
        .toList(growable: false);
  }

  // ---- Warehouse tablet: the four shared preparation areas ----
  List<PreparationArea> preparationAreasValue = const [];
  List<WarehouseDemand> warehouseDemandsValue = const [];
  Object? preparationAreasError;
  Object? placePalletError;
  Object? markPalletReadyError;
  Object? releaseAreaError;
  int preparationAreasCount = 0;
  int warehouseDemandsCount = 0;
  final List<Map<String, Object?>> placedPallets = [];
  final List<Map<String, Object?>> readiedPallets = [];
  final List<Map<String, Object?>> releasedPallets = [];
  List<PreparationArea> finishedGoodsPreparationAreasValue = const [];
  List<WarehouseDemand> finishedGoodsPreparationDemandsValue = const [];

  @override
  Future<List<PreparationArea>> preparationAreas() async {
    preparationAreasCount++;
    if (preparationAreasError case final error?) throw error;
    return preparationAreasValue;
  }

  @override
  Future<List<WarehouseDemand>> warehouseDemands() async {
    warehouseDemandsCount++;
    if (preparationAreasError case final error?) throw error;
    return warehouseDemandsValue;
  }

  @override
  Future<PreparationPallet> placePallet({
    required String areaId,
    required String load,
    String? containerTypeId,
    String? partId,
    String? transportRequestId,
    required String idempotencyKey,
  }) async {
    placedPallets.add({
      'areaId': areaId,
      'load': load,
      'containerTypeId': containerTypeId,
      'partId': partId,
      'transportRequestId': transportRequestId,
      'idempotencyKey': idempotencyKey,
    });
    if (placePalletError case final error?) throw error;
    return fakePallet(
      load: load,
      partId: partId,
      transportRequestId: transportRequestId,
    );
  }

  @override
  Future<PreparationPallet> markPalletReady({
    required String palletId,
    required int expectedRowVersion,
  }) async {
    readiedPallets.add({
      'palletId': palletId,
      'expectedRowVersion': expectedRowVersion,
    });
    if (markPalletReadyError case final error?) throw error;
    return fakePallet(id: palletId, readyAt: DateTime.utc(2026, 1, 1));
  }

  @override
  Future<PreparationPallet> releasePreparationArea({
    required String palletId,
    required int expectedRowVersion,
    required String reason,
  }) async {
    releasedPallets.add({
      'palletId': palletId,
      'expectedRowVersion': expectedRowVersion,
      'reason': reason,
    });
    if (releaseAreaError case final error?) throw error;
    return fakePallet(id: palletId);
  }

  @override
  Future<List<PreparationArea>> finishedGoodsPreparationAreas() async =>
      finishedGoodsPreparationAreasValue;

  @override
  Future<List<WarehouseDemand>> finishedGoodsPreparationDemands() async =>
      finishedGoodsPreparationDemandsValue;

  @override
  Future<PreparationPallet> placeFinishedGoodsPreparation({
    required String areaId,
    String? containerTypeId,
    required String transportRequestId,
    required String idempotencyKey,
  }) => placePallet(
    areaId: areaId,
    load: 'EMPTY',
    containerTypeId: containerTypeId,
    transportRequestId: transportRequestId,
    idempotencyKey: idempotencyKey,
  );

  @override
  Future<PreparationPallet> markFinishedGoodsPreparationReady({
    required String palletId,
    required int expectedRowVersion,
  }) => markPalletReady(
    palletId: palletId,
    expectedRowVersion: expectedRowVersion,
  );

  @override
  Future<PreparationPallet> releaseFinishedGoodsPreparation({
    required String palletId,
    required int expectedRowVersion,
    required String reason,
  }) => releasePreparationArea(
    palletId: palletId,
    expectedRowVersion: expectedRowVersion,
    reason: reason,
  );

  // ---- Warehouse tablet: the finished-goods store ----
  List<StoreLocation> storeLocationsValue = const [];
  Object? storeLocationsError;
  Object? removeStoreUnitError;
  Object? clearStoreLocationError;
  int storeLocationsCount = 0;
  final List<Map<String, Object?>> removedUnits = [];
  final List<String> clearedLocations = [];

  @override
  Future<List<StoreLocation>> storeLocations() async {
    storeLocationsCount++;
    if (storeLocationsError case final error?) throw error;
    return storeLocationsValue;
  }

  @override
  Future<StoreLocation> removeStoreUnit({
    required String unitId,
    required int expectedRowVersion,
  }) async {
    removedUnits.add({
      'unitId': unitId,
      'expectedRowVersion': expectedRowVersion,
    });
    if (removeStoreUnitError case final error?) throw error;
    return fakeStoreLocation(code: 'STORE-1');
  }

  @override
  Future<StoreLocation> recordEmptyContainer({
    required String locationId,
    required String idempotencyKey,
  }) async => fakeStoreLocation(code: 'STORE-1');

  @override
  Future<StoreLocation> clearStoreLocation(String locationId) async {
    clearedLocations.add(locationId);
    if (clearStoreLocationError case final error?) throw error;
    return fakeStoreLocation(code: 'STORE-1');
  }

  @override
  Future<RobotAlarmSummary> robotAlarmSummary() async {
    robotAlarmSummaryCount++;
    if (robotAlarmSummaryError case final error?) throw error;
    return robotAlarmSummaryValue;
  }

  List<RobotTelemetry> robotsValue = const [];
  int robotsCount = 0;
  Object? robotsError;

  @override
  Future<List<RobotTelemetry>> robots() async {
    robotsCount++;
    if (robotsError case final error?) throw error;
    return robotsValue;
  }

  int pauseRobotCount = 0;
  int resumeRobotCount = 0;
  int clearRobotErrorsCount = 0;
  int setRequestPriorityCount = 0;

  @override
  Future<void> pauseRobot([List<String>? vehicles]) async {
    pauseRobotCount++;
  }

  @override
  Future<void> resumeRobot([List<String>? vehicles]) async {
    resumeRobotCount++;
  }

  @override
  Future<void> clearRobotErrors([List<String>? vehicles]) async {
    clearRobotErrorsCount++;
  }

  @override
  Future<void> setRequestPriority(String requestId, int priority) async {
    setRequestPriorityCount++;
  }
}

const _machineJson = {
  'id': '30000000-0000-0000-0000-000000000001',
  'code': 'SH-01-A',
  'displayName': 'SH-01-A Hücresi',
  'displayOrder': 1,
  'permissions': {
    'canView': true,
    'canOperate': true,
    'canChangeVariant': true,
    'canManageSlots': false,
  },
};

const _workSite = {
  'isMapped': false,
  'siteId': null,
  'area': null,
  'groupName': null,
  'type': null,
  'filled': null,
  'locked': null,
  'disabled': null,
  'syncFailed': null,
  'content': null,
  'agvId': null,
  'lastSyncAt': null,
};

const _readiness = {
  'rdsWorkSiteMapped': false,
  'readinessCode': 'RDS_WORKSITE_NOT_MAPPED',
  'readinessMessage': 'Robot noktası eşlenmemiş.',
};

const _machineSlot = {
  'id': 'slot-machine',
  'code': 'MACHINE-01',
  'displayName': 'Makine',
  'role': 'MACHINE',
  'isEnabled': true,
  'transportEligible': false,
  'defaultContainerTypeId': null,
  'defaultContainerCode': null,
  'x': 0.35,
  'y': 0.35,
  'width': 0.3,
  'height': 0.3,
  'displayOrder': 1,
  'occupancyState': 'UNKNOWN',
  'loadState': 'UNKNOWN',
  'currentPartId': null,
  'currentPartCode': null,
  'currentContainerTypeId': null,
  'currentContainerCode': null,
  'rowVersion': 0,
  'rdsWorkSite': _workSite,
  'executionReadiness': _readiness,
  'availableActions': [
    {
      'action': 'BRING_EMPTY',
      'enabled': false,
      'disabledCode': 'MACHINE_REGION_FORBIDDEN',
      'disabledMessage': 'Makine alanı taşıma dışıdır.',
    },
  ],
  'loadStateActions': [],
};

const _vacantSlot = {
  'id': 'slot-vacant',
  'code': 'SLOT-V',
  'displayName': 'Boş Konum',
  'role': 'STAGING',
  'isEnabled': true,
  'transportEligible': true,
  'defaultContainerTypeId': null,
  'defaultContainerCode': null,
  'x': 0.05,
  'y': 0.05,
  'width': 0.2,
  'height': 0.2,
  'displayOrder': 2,
  'occupancyState': 'VACANT',
  'loadState': 'UNKNOWN',
  'currentPartId': null,
  'currentPartCode': null,
  'currentContainerTypeId': null,
  'currentContainerCode': null,
  'rowVersion': 0,
  'rdsWorkSite': _workSite,
  'executionReadiness': _readiness,
  'availableActions': [
    {
      'action': 'BRING_EMPTY',
      'enabled': true,
      'disabledCode': null,
      'disabledMessage': null,
    },
    {
      'action': 'REMOVE_FULL',
      'enabled': false,
      'disabledCode': 'SLOT_STATE_MISMATCH',
      'disabledMessage': 'Konum durumu uygun değil.',
    },
    {
      'action': 'REMOVE_EMPTY',
      'enabled': false,
      'disabledCode': 'SLOT_STATE_MISMATCH',
      'disabledMessage': 'Konum durumu uygun değil.',
    },
  ],
  'loadStateActions': [],
};

const _emptySlot = {
  'id': 'slot-empty',
  'code': 'SLOT-E',
  'displayName': 'Boş Kasa Konumu',
  'role': 'STAGING',
  'isEnabled': true,
  'transportEligible': true,
  'defaultContainerTypeId': null,
  'defaultContainerCode': '4311',
  'x': 0.7,
  'y': 0.05,
  'width': 0.2,
  'height': 0.2,
  'displayOrder': 3,
  'occupancyState': 'OCCUPIED',
  'loadState': 'EMPTY',
  'currentPartId': null,
  'currentPartCode': null,
  'currentContainerTypeId': null,
  'currentContainerCode': '4311',
  'rowVersion': 0,
  'rdsWorkSite': _workSite,
  'executionReadiness': _readiness,
  'availableActions': [
    {
      'action': 'BRING_EMPTY',
      'enabled': false,
      'disabledCode': 'SLOT_STATE_MISMATCH',
      'disabledMessage': 'Konum durumu uygun değil.',
    },
    {
      'action': 'REMOVE_FULL',
      'enabled': false,
      'disabledCode': 'SLOT_STATE_MISMATCH',
      'disabledMessage': 'Konum durumu uygun değil.',
    },
    {
      'action': 'REMOVE_EMPTY',
      'enabled': true,
      'disabledCode': null,
      'disabledMessage': null,
    },
  ],
  'loadStateActions': [
    {
      'action': 'MARK_FULL',
      'enabled': true,
      'disabledCode': null,
      'disabledMessage': null,
    },
    {
      'action': 'MARK_EMPTY',
      'enabled': false,
      'disabledCode': 'LOAD_STATE_ALREADY_REPORTED',
      'disabledMessage': 'Kasa zaten bu yük durumundadır.',
    },
  ],
};

const _fullSlot = {
  'id': 'slot-full',
  'code': 'SLOT-F',
  'displayName': 'Dolu Kasa Konumu',
  'role': 'STAGING',
  'isEnabled': true,
  'transportEligible': true,
  'defaultContainerTypeId': null,
  'defaultContainerCode': '4322',
  'x': 0.05,
  'y': 0.7,
  'width': 0.2,
  'height': 0.2,
  'displayOrder': 4,
  'occupancyState': 'OCCUPIED',
  'loadState': 'FULL',
  'currentPartId': null,
  'currentPartCode': null,
  'currentContainerTypeId': null,
  'currentContainerCode': '4322',
  'rowVersion': 0,
  'rdsWorkSite': _workSite,
  'executionReadiness': _readiness,
  'availableActions': [
    {
      'action': 'BRING_EMPTY',
      'enabled': false,
      'disabledCode': 'SLOT_STATE_MISMATCH',
      'disabledMessage': 'Konum durumu uygun değil.',
    },
    {
      'action': 'REMOVE_FULL',
      'enabled': true,
      'disabledCode': null,
      'disabledMessage': null,
    },
    {
      'action': 'REMOVE_EMPTY',
      'enabled': false,
      'disabledCode': 'SLOT_STATE_MISMATCH',
      'disabledMessage': 'Konum durumu uygun değil.',
    },
  ],
  'loadStateActions': [
    {
      'action': 'MARK_FULL',
      'enabled': false,
      'disabledCode': 'LOAD_STATE_ALREADY_REPORTED',
      'disabledMessage': 'Kasa zaten bu yük durumundadır.',
    },
    {
      'action': 'MARK_EMPTY',
      'enabled': true,
      'disabledCode': null,
      'disabledMessage': null,
    },
  ],
};

PreparationPallet fakePallet({
  String id = 'pallet-1',
  String load = 'EMPTY',
  String? partId,
  String? partCode,
  String? transportRequestId,
  int? requestNo,
  String? targetMachineCode,
  DateTime? readyAt,
  int rowVersion = 0,
  String? requestStatus = 'QUEUED',
}) => PreparationPallet(
  id: id,
  containerTypeId: 'container-1',
  containerCode: '4311',
  load: load,
  partId: partId,
  partCode: partCode,
  transportRequestId: transportRequestId,
  requestNo: requestNo,
  targetMachineCode: targetMachineCode,
  placedAt: DateTime.utc(2026, 1, 1),
  readyAt: readyAt,
  rowVersion: rowVersion,
  requestStatus: requestStatus,
);

PreparationArea fakeArea({
  required int number,
  PreparationPallet? pallet,
  bool isEnabled = true,
}) => PreparationArea(
  id: 'area-$number',
  areaNumber: number,
  displayName: 'Hazırlık Alanı $number',
  isEnabled: isEnabled,
  pallet: pallet,
);

WarehouseDemand fakeDemand({
  required int requestNo,
  String requestId = '',
  String machineCode = 'SH-01-A',
  String? partCode = '67185BN050-40',
  String? variantCode,
  String? variantName,
  String? containerCode = '4311',
  String? containerTypeId = 'container-1',
  String? alternativeContainerCode,
  String? alternativeContainerTypeId,
  String? containerDimensions,
  String requestedLoad = 'FULL',
  DateTime? requestedAt,
  int? claimedByAreaNumber,
}) => WarehouseDemand(
  requestId: requestId.isEmpty ? 'request-$requestNo' : requestId,
  requestNo: requestNo,
  machineId: 'machine-1',
  machineCode: machineCode,
  partId: 'part-1',
  partCode: partCode,
  containerTypeId: containerTypeId,
  containerCode: containerCode,
  alternativeContainerTypeId: alternativeContainerTypeId,
  alternativeContainerCode: alternativeContainerCode,
  containerDimensions: containerDimensions,
  operation: requestedLoad == 'FULL' ? 'BRING_FULL' : 'BRING_EMPTY',
  requestedLoad: requestedLoad,
  requestedAt: requestedAt,
  claimedByAreaId: claimedByAreaNumber == null
      ? null
      : 'area-$claimedByAreaNumber',
  claimedByAreaNumber: claimedByAreaNumber,
  variantCode: variantCode,
  variantName: variantName,
);

StoreUnit fakeStoreUnit({
  String id = 'unit-1',
  String? partCode = '67116BN050-40',
  String? machineCode = 'SH-01-A',
  String? containerCode = '4322',
  int? requestNo = 42,
  DateTime? deliveredAt,
  int rowVersion = 0,
  String loadState = 'FULL',
}) => StoreUnit(
  id: id,
  transportRequestId: 'request-1',
  requestNo: requestNo,
  partId: 'part-1',
  partCode: partCode,
  containerTypeId: 'container-1',
  containerCode: containerCode,
  machineId: 'machine-1',
  machineCode: machineCode,
  deliveredAt: deliveredAt,
  rowVersion: rowVersion,
  loadState: loadState,
);

StoreLocation fakeStoreLocation({
  String code = 'STORE-1',
  String? displayName,
  int? capacity,
  bool isEnabled = true,
  List<StoreUnit> units = const [],
}) => StoreLocation(
  id: 'location-$code',
  code: code,
  displayName: displayName ?? 'Bitmiş Ürün Alanı $code',
  capacity: capacity,
  isEnabled: isEnabled,
  units: units,
);
