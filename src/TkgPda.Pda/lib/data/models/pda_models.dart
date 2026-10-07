import 'dart:convert';
export 'production_session.dart';

typedef JsonMap = Map<String, dynamic>;

class AuthenticatedUser {
  const AuthenticatedUser({
    required this.id,
    required this.username,
    required this.fullName,
    required this.employeeNo,
    required this.roles,
  });

  final String id;
  final String username;
  final String fullName;
  final String? employeeNo;
  final List<String> roles;

  factory AuthenticatedUser.fromJson(JsonMap json) => AuthenticatedUser(
    id: json['id'] as String,
    username: json['username'] as String? ?? '',
    fullName: json['fullName'] as String? ?? '',
    employeeNo: json['employeeNo'] as String?,
    roles: _strings(json['roles']),
  );
}

class PdaDevice {
  const PdaDevice({
    required this.id,
    required this.code,
    required this.name,
    required this.type,
    this.machineId,
  });

  final String id;
  final String code;
  final String name;
  final String type;
  final String? machineId;

  factory PdaDevice.fromJson(JsonMap json) => PdaDevice(
    id: json['id'] as String,
    code: json['code'] as String? ?? '',
    name: json['name'] as String? ?? '',
    type: json['type'] as String? ?? '',
    machineId: json['machineId'] as String?,
  );
}

class LoginSession {
  const LoginSession({
    required this.accessToken,
    required this.expiresAt,
    required this.user,
    required this.device,
  });

  final String accessToken;
  final DateTime expiresAt;
  final AuthenticatedUser user;
  final PdaDevice device;

  factory LoginSession.fromJson(JsonMap json) => LoginSession(
    accessToken: json['accessToken'] as String,
    expiresAt: DateTime.parse(json['expiresAt'] as String),
    user: AuthenticatedUser.fromJson(_map(json['user'])),
    device: PdaDevice.fromJson(_map(json['device'])),
  );
}

class MachinePermissions {
  const MachinePermissions({
    required this.canView,
    required this.canOperate,
    required this.canChangeVariant,
    required this.canManageSlots,
  });

  final bool canView;
  final bool canOperate;
  final bool canChangeVariant;
  final bool canManageSlots;

  factory MachinePermissions.fromJson(JsonMap json) => MachinePermissions(
    canView: json['canView'] as bool? ?? false,
    canOperate: json['canOperate'] as bool? ?? false,
    canChangeVariant: json['canChangeVariant'] as bool? ?? false,
    canManageSlots: json['canManageSlots'] as bool? ?? false,
  );
}

class MachineSummary {
  const MachineSummary({
    required this.id,
    required this.code,
    required this.displayName,
    required this.displayOrder,
    required this.permissions,
  });

  final String id;
  final String code;
  final String displayName;
  final int displayOrder;
  final MachinePermissions permissions;

  factory MachineSummary.fromJson(JsonMap json) => MachineSummary(
    id: json['id'] as String,
    code: json['code'] as String? ?? '',
    displayName: json['displayName'] as String? ?? '',
    displayOrder: _integer(json['displayOrder']),
    permissions: MachinePermissions.fromJson(_map(json['permissions'])),
  );
}

class VariantLayoutOption {
  const VariantLayoutOption({
    required this.variantId,
    required this.variantCode,
    required this.variantName,
    required this.layoutId,
    required this.layoutCode,
    required this.layoutName,
    required this.isDefault,
  });

  final String variantId;
  final String variantCode;
  final String variantName;
  final String layoutId;
  final String layoutCode;
  final String layoutName;
  final bool isDefault;

  factory VariantLayoutOption.fromJson(JsonMap json) => VariantLayoutOption(
    variantId: json['variantId'] as String,
    variantCode: json['variantCode'] as String? ?? '',
    variantName: json['variantName'] as String? ?? '',
    layoutId: json['layoutId'] as String,
    layoutCode: json['layoutCode'] as String? ?? '',
    layoutName: json['layoutName'] as String? ?? '',
    isDefault: json['isDefault'] as bool? ?? false,
  );
}

/// WP-03B: a Related/Alt part of a [MachineMainPartOption], as returned by
/// GET /api/v1/machines/{machineId}/part-options.
class MachineRelatedPart {
  const MachineRelatedPart({
    required this.partId,
    required this.partCode,
    required this.description,
    required this.relationType,
    this.requestable = true,
    this.blockers = const [],
  });

  final String partId;
  final String partCode;
  final String? description;
  final String relationType;
  final bool requestable;
  final List<String> blockers;

  factory MachineRelatedPart.fromJson(JsonMap json) => MachineRelatedPart(
    partId: json['partId'] as String,
    partCode: json['partCode'] as String? ?? '',
    description: json['description'] as String?,
    relationType: json['relationType'] as String? ?? '',
    requestable: json['requestable'] as bool? ?? true,
    blockers: (json['blockers'] as List?)?.map((item) => item.toString()).toList(growable: false) ?? const [],
  );
}

/// WP-03B: a machine-level main-part selection option (part-first entry
/// point), deduplicated across the machine's variants.
class MachineMainPartOption {
  const MachineMainPartOption({
    required this.partId,
    required this.partCode,
    required this.description,
    required this.relatedParts,
  });

  final String partId;
  final String partCode;
  final String? description;
  final List<MachineRelatedPart> relatedParts;

  factory MachineMainPartOption.fromJson(JsonMap json) => MachineMainPartOption(
    partId: json['partId'] as String,
    partCode: json['partCode'] as String? ?? '',
    description: json['description'] as String?,
    relatedParts: _maps(
      json['relatedParts'],
    ).map(MachineRelatedPart.fromJson).toList(growable: false),
  );
}

/// WP-03B: a variant compatible with a selected part on an authorized
/// machine, as returned by
/// GET /api/v1/machines/{machineId}/parts/{partId}/compatible-variants?mainPartId=...
class CompatibleVariant {
  const CompatibleVariant({
    required this.variantId,
    required this.variantCode,
    required this.variantName,
    required this.machineId,
    required this.productGroupId,
    required this.relationType,
  });

  final String variantId;
  final String variantCode;
  final String variantName;
  final String machineId;
  final String productGroupId;
  final String relationType;

  factory CompatibleVariant.fromJson(JsonMap json) => CompatibleVariant(
    variantId: json['variantId'] as String,
    variantCode: json['variantCode'] as String? ?? '',
    variantName: json['variantName'] as String? ?? '',
    machineId: json['machineId'] as String,
    productGroupId: json['productGroupId'] as String,
    relationType: json['relationType'] as String? ?? '',
  );
}

class ContainerActionAvailability {
  const ContainerActionAvailability({
    required this.enabled,
    required this.status,
    required this.disabledCode,
    required this.disabledMessage,
  });

  final bool enabled;
  final String status;
  final String? disabledCode;
  final String? disabledMessage;

  factory ContainerActionAvailability.fromJson(JsonMap json) =>
      ContainerActionAvailability(
        enabled: json['enabled'] as bool? ?? false,
        status: json['status'] as String? ?? 'READY',
        disabledCode: json['disabledCode'] as String?,
        disabledMessage: json['disabledMessage'] as String?,
      );
}

class ContainerPosition {
  const ContainerPosition({
    required this.positionNumber,
    required this.label,
    required this.fullAction,
    required this.emptyAction,
    required this.transportState,
    required this.queuePosition,
    required this.etaSeconds,
    required this.etaState,
  });

  final int positionNumber;
  final String label;
  final ContainerActionAvailability fullAction;
  final ContainerActionAvailability emptyAction;
  final String transportState;
  final int? queuePosition;
  final int? etaSeconds;
  final String etaState;

  factory ContainerPosition.fromJson(JsonMap json) => ContainerPosition(
    positionNumber: _integer(json['positionNumber']),
    label: json['label'] as String? ?? '',
    fullAction: ContainerActionAvailability.fromJson(_map(json['fullAction'])),
    emptyAction: ContainerActionAvailability.fromJson(
      _map(json['emptyAction']),
    ),
    transportState: json['transportState'] as String? ?? 'READY',
    queuePosition: _nullableInteger(json['queuePosition']),
    etaSeconds: _nullableInteger(json['etaSeconds']),
    etaState: json['etaState'] as String? ?? 'UNAVAILABLE',
  );
}

class ContainerActionResult {
  const ContainerActionResult({
    required this.positionNumber,
    required this.action,
    required this.requestNo,
    required this.status,
    required this.idempotentReplay,
  });

  final int positionNumber;
  final String action;
  final int requestNo;
  final String status;
  final bool idempotentReplay;

  factory ContainerActionResult.fromJson(JsonMap json) => ContainerActionResult(
    positionNumber: _integer(json['positionNumber']),
    action: json['action'] as String? ?? '',
    requestNo: _integer(json['requestNo']),
    status: json['status'] as String? ?? '',
    idempotentReplay: json['idempotentReplay'] as bool? ?? false,
  );
}

class RemoveAllResult {
  const RemoveAllResult({
    required this.idempotentReplay,
    required this.ignoredVacantPositions,
    required this.itemCount,
  });

  final bool idempotentReplay;
  final List<int> ignoredVacantPositions;
  final int itemCount;

  factory RemoveAllResult.fromJson(JsonMap json) => RemoveAllResult(
    idempotentReplay: json['idempotentReplay'] as bool? ?? false,
    ignoredVacantPositions: json['ignoredVacantPositions'] is List
        ? (json['ignoredVacantPositions'] as List)
              .whereType<num>()
              .map((value) => value.toInt())
              .toList(growable: false)
        : const [],
    itemCount: json['items'] is List ? (json['items'] as List).length : 0,
  );
}

class ContainerActionBatchItemCommand {
  const ContainerActionBatchItemCommand({
    required this.productGroupId,
    required this.partId,
    required this.positionNumber,
    required this.action,
  });

  final String productGroupId;
  final String partId;
  final int positionNumber;
  final String action;

  JsonMap toJson() => {
    'productGroupId': productGroupId,
    'partId': partId,
    'positionNumber': positionNumber,
    'action': action,
  };
}

class OperatorQueueItem {
  const OperatorQueueItem({
    required this.queuePosition,
    required this.machine,
    required this.part,
    required this.logicalContainerNumber,
    required this.businessAction,
    required this.requestedAt,
    required this.state,
    required this.etaSeconds,
    required this.etaState,
    this.requestId,
  });

  final int queuePosition;
  final String machine;
  final String part;
  final int? logicalContainerNumber;
  final String businessAction;
  final DateTime requestedAt;
  final String state;
  final int? etaSeconds;
  final String etaState;
  final String? requestId;
  // No agvId: the Operator queue is a business view and the API no longer
  // serializes the raw RDS AGV identifier.

  factory OperatorQueueItem.fromJson(JsonMap json) => OperatorQueueItem(
    queuePosition: _integer(json['queuePosition']),
    machine: json['machine'] as String? ?? '',
    part: json['part'] as String? ?? '',
    logicalContainerNumber: _nullableInteger(json['logicalContainerNumber']),
    businessAction: json['businessAction'] as String? ?? '',
    requestedAt:
        _date(json['requestedAt']) ?? DateTime.fromMillisecondsSinceEpoch(0),
    state: json['state'] as String? ?? 'REQUESTED',
    etaSeconds: _nullableInteger(json['etaSeconds']),
    etaState: json['etaState'] as String? ?? 'UNAVAILABLE',
    requestId: json['requestId'] as String?,
  );
}

class OperatorQueuePage {
  const OperatorQueuePage({
    required this.terminal,
    required this.offset,
    required this.limit,
    required this.hasMore,
    required this.items,
  });

  final bool terminal;
  final int offset;
  final int limit;
  final bool hasMore;
  final List<OperatorQueueItem> items;

  factory OperatorQueuePage.fromJson(JsonMap json) => OperatorQueuePage(
    terminal: json['terminal'] as bool? ?? false,
    offset: _integer(json['offset']),
    limit: _integer(json['limit']),
    hasMore: json['hasMore'] as bool? ?? false,
    items: _maps(
      json['items'],
    ).map(OperatorQueueItem.fromJson).toList(growable: false),
  );
}

class ExecutionReadiness {
  const ExecutionReadiness({
    required this.rdsWorkSiteMapped,
    required this.isExecutionReady,
    required this.readinessCode,
    required this.readinessMessage,
  });

  final bool rdsWorkSiteMapped;
  final bool isExecutionReady;
  final String? readinessCode;
  final String? readinessMessage;

  factory ExecutionReadiness.fromJson(JsonMap json) => ExecutionReadiness(
    rdsWorkSiteMapped: json['rdsWorkSiteMapped'] as bool? ?? false,
    isExecutionReady: json['isExecutionReady'] as bool? ?? false,
    readinessCode: json['readinessCode'] as String?,
    readinessMessage: json['readinessMessage'] as String?,
  );
}

class AvailableAction {
  const AvailableAction({
    required this.action,
    required this.enabled,
    required this.disabledCode,
    required this.disabledMessage,
  });

  final String action;
  final bool enabled;
  final String? disabledCode;
  final String? disabledMessage;

  factory AvailableAction.fromJson(JsonMap json) => AvailableAction(
    action: json['action'] as String? ?? '',
    enabled: json['enabled'] as bool? ?? false,
    disabledCode: json['disabledCode'] as String?,
    disabledMessage: json['disabledMessage'] as String?,
  );
}

class LayoutSlot {
  const LayoutSlot({
    required this.id,
    required this.code,
    required this.displayName,
    required this.role,
    required this.isEnabled,
    required this.transportEligible,
    required this.defaultContainerTypeId,
    required this.defaultContainerCode,
    required this.x,
    required this.y,
    required this.width,
    required this.height,
    required this.displayOrder,
    required this.occupancyState,
    required this.loadState,
    required this.currentPartId,
    required this.currentPartCode,
    required this.currentContainerTypeId,
    required this.currentContainerCode,
    required this.rowVersion,
    required this.executionReadiness,
    required this.availableActions,
    required this.loadStateActions,
  });

  final String id;
  final String code;
  final String displayName;
  final String role;
  final bool isEnabled;
  final bool transportEligible;
  final String? defaultContainerTypeId;
  final String? defaultContainerCode;
  final double? x;
  final double? y;
  final double? width;
  final double? height;
  final int displayOrder;
  final String occupancyState;
  final String loadState;
  final String? currentPartId;
  final String? currentPartCode;
  final String? currentContainerTypeId;
  final String? currentContainerCode;
  final int rowVersion;
  final ExecutionReadiness executionReadiness;
  final List<AvailableAction> availableActions;
  final List<AvailableAction> loadStateActions;

  bool get isMachineRegion => role == 'MACHINE';

  factory LayoutSlot.fromJson(JsonMap json) => LayoutSlot(
    id: json['id'] as String,
    code: json['code'] as String? ?? '',
    displayName: json['displayName'] as String? ?? '',
    role: json['role'] as String? ?? 'OTHER',
    isEnabled: json['isEnabled'] as bool? ?? false,
    transportEligible: json['transportEligible'] as bool? ?? false,
    defaultContainerTypeId: json['defaultContainerTypeId'] as String?,
    defaultContainerCode: json['defaultContainerCode'] as String?,
    x: _decimal(json['x']),
    y: _decimal(json['y']),
    width: _decimal(json['width']),
    height: _decimal(json['height']),
    displayOrder: _integer(json['displayOrder']),
    occupancyState: json['occupancyState'] as String? ?? 'UNKNOWN',
    loadState: json['loadState'] as String? ?? 'UNKNOWN',
    currentPartId: json['currentPartId'] as String?,
    currentPartCode: json['currentPartCode'] as String?,
    currentContainerTypeId: json['currentContainerTypeId'] as String?,
    currentContainerCode: json['currentContainerCode'] as String?,
    rowVersion: _integer(json['rowVersion']),
    executionReadiness: ExecutionReadiness.fromJson(
      _map(json['executionReadiness']),
    ),
    availableActions: _maps(
      json['availableActions'],
    ).map(AvailableAction.fromJson).toList(growable: false),
    loadStateActions: _maps(
      json['loadStateActions'],
    ).map(AvailableAction.fromJson).toList(growable: false),
  );
}

class ProductGroupPart {
  const ProductGroupPart({
    required this.id,
    required this.partId,
    required this.partCode,
    required this.containerTypeId,
    required this.containerCode,
    required this.relationType,
    required this.lotQuantity,
    required this.displayOrder,
  });

  final String id;
  final String partId;
  final String partCode;
  final String? containerTypeId;
  final String? containerCode;
  final String relationType;
  final double? lotQuantity;
  final int displayOrder;

  factory ProductGroupPart.fromJson(JsonMap json) => ProductGroupPart(
    id: json['id'] as String,
    partId: json['partId'] as String,
    partCode: json['partCode'] as String? ?? '',
    containerTypeId: json['containerTypeId'] as String?,
    containerCode: json['containerCode'] as String?,
    relationType: json['relationType'] as String? ?? '',
    lotQuantity: _decimal(json['lotQuantity']),
    displayOrder: _integer(json['displayOrder']),
  );
}

class ProductGroup {
  const ProductGroup({
    required this.id,
    required this.mainPartId,
    required this.mainPartCode,
    required this.name,
    required this.displayOrder,
    required this.parts,
  });

  final String id;
  final String mainPartId;
  final String mainPartCode;
  final String? name;
  final int displayOrder;
  final List<ProductGroupPart> parts;

  factory ProductGroup.fromJson(JsonMap json) => ProductGroup(
    id: json['id'] as String,
    mainPartId: json['mainPartId'] as String,
    mainPartCode: json['mainPartCode'] as String? ?? '',
    name: json['name'] as String?,
    displayOrder: _integer(json['displayOrder']),
    parts: _maps(
      json['parts'],
    ).map(ProductGroupPart.fromJson).toList(growable: false),
  );
}

class TaskTemplate {
  const TaskTemplate({
    required this.id,
    required this.variantId,
    required this.productGroupId,
    required this.operation,
    required this.rdsTaskLabelConfigured,
    required this.priority,
  });

  final String id;
  final String variantId;
  final String? productGroupId;
  final String operation;
  final bool rdsTaskLabelConfigured;
  final int priority;

  factory TaskTemplate.fromJson(JsonMap json) => TaskTemplate(
    id: json['id'] as String,
    variantId: json['variantId'] as String,
    productGroupId: json['productGroupId'] as String?,
    operation: json['operation'] as String? ?? '',
    rdsTaskLabelConfigured: json['rdsTaskLabelConfigured'] as bool? ?? false,
    priority: _integer(json['priority']),
  );
}

/// Business execution view. The API does not serialize raw RDS identifiers
/// (taskRecordId, taskLabel, outOrderNo, AGV id, numeric status) to any PDA
/// role, so this model has no fields for them.
class ExecutionProjection {
  const ExecutionProjection({
    required this.state,
    required this.lastSyncAt,
    required this.failureCode,
    required this.failureCategory,
    required this.operatorMessage,
    required this.correlationId,
  });

  final String state;
  final DateTime? lastSyncAt;
  final String? failureCode;
  final String? failureCategory;
  final String? operatorMessage;
  final String correlationId;

  factory ExecutionProjection.fromJson(JsonMap json) => ExecutionProjection(
    state: json['state'] as String? ?? 'REQUESTED',
    lastSyncAt: _date(json['lastSyncAt']),
    failureCode: json['failureCode'] as String?,
    failureCategory: json['failureCategory'] as String?,
    operatorMessage: json['operatorMessage'] as String?,
    correlationId: json['correlationId'] as String? ?? '',
  );
}

class ActiveTransportRequest {
  const ActiveTransportRequest({
    required this.requestId,
    required this.requestNo,
    required this.operation,
    required this.status,
    required this.requestedBy,
    required this.requestedByUsername,
    required this.isMine,
    required this.sourceSlotId,
    required this.destinationSlotId,
    required this.productGroupId,
    required this.partId,
    required this.containerTypeId,
    required this.priority,
    required this.createdAt,
    required this.canCancel,
    required this.cancelDisabledCode,
    required this.cancelDisabledMessage,
    required this.execution,
    this.partCode,
    this.partDescription,
    this.relationType,
    this.readinessState = 'NOT_REQUIRED',
    this.queuePosition,
    this.ageSeconds,
    this.executionElapsedSeconds,
    this.chainState,
    this.productionEnd = false,
  });

  final String requestId;
  final int requestNo;
  final String operation;
  final String status;
  final String? requestedBy;
  final String? requestedByUsername;
  final bool isMine;
  final String? sourceSlotId;
  final String? destinationSlotId;
  final String? productGroupId;
  final String? partId;
  final String? containerTypeId;
  final int priority;
  final DateTime createdAt;
  final bool canCancel;
  final String? cancelDisabledCode;
  final String? cancelDisabledMessage;
  final ExecutionProjection execution;

  /// WP-07 server-derived display values. The client never infers any of them.
  final String? partCode;
  final String? partDescription;

  /// MAIN_PART, ALT_PART, RELATED, CONSUMABLE, OTHER, or null when the server
  /// has no master-data evidence.
  final String? relationType;
  final String readinessState;

  /// TKG's ordering of this machine's active requests. Not an RDS queue
  /// position and not a robot position.
  final int? queuePosition;

  /// Seconds since creation, measured by the server clock at read time.
  final int? ageSeconds;

  /// Seconds since RDS reported execution start; null without start evidence.
  final int? executionElapsedSeconds;

  /// Server chain label: WAITING_PREDECESSOR, REMOVING_EXISTING,
  /// PREPARING_SUCCESSOR, PREPARING, or null. Never a robot percentage.
  final String? chainState;

  /// True when the server created this request as part of production-end cleanup.
  final bool productionEnd;

  factory ActiveTransportRequest.fromJson(JsonMap json) =>
      ActiveTransportRequest(
        requestId: json['requestId'] as String,
        requestNo: _integer(json['requestNo']),
        operation: json['operation'] as String? ?? '',
        status: json['status'] as String? ?? '',
        requestedBy: json['requestedBy'] as String?,
        requestedByUsername: json['requestedByUsername'] as String?,
        isMine: json['isMine'] as bool? ?? false,
        sourceSlotId: json['sourceSlotId'] as String?,
        destinationSlotId: json['destinationSlotId'] as String?,
        productGroupId: json['productGroupId'] as String?,
        partId: json['partId'] as String?,
        containerTypeId: json['containerTypeId'] as String?,
        priority: _integer(json['priority']),
        createdAt: DateTime.parse(json['createdAt'] as String),
        canCancel: json['canCancel'] as bool? ?? false,
        cancelDisabledCode: json['cancelDisabledCode'] as String?,
        cancelDisabledMessage: json['cancelDisabledMessage'] as String?,
        execution: ExecutionProjection.fromJson(_map(json['execution'])),
        partCode: json['partCode'] as String?,
        partDescription: json['partDescription'] as String?,
        relationType: json['relationType'] as String?,
        readinessState: json['readinessState'] as String? ?? 'NOT_REQUIRED',
        queuePosition: json['queuePosition'] == null
            ? null
            : _integer(json['queuePosition']),
        ageSeconds: json['ageSeconds'] == null
            ? null
            : _integer(json['ageSeconds']),
        executionElapsedSeconds: json['executionElapsedSeconds'] == null
            ? null
            : _integer(json['executionElapsedSeconds']),
        chainState: json['chainState'] as String?,
        productionEnd: json['productionEnd'] == true,
      );
}

class PartDemandResult {
  const PartDemandResult({
    required this.requestNo,
    required this.taskType,
    required this.status,
    required this.readiness,
    this.predecessorRequestNo,
    this.predecessorTaskType,
    required this.idempotentReplay,
  });

  final int requestNo;
  final String taskType;
  final String status;
  final String readiness;
  final int? predecessorRequestNo;
  final String? predecessorTaskType;
  final bool idempotentReplay;

  factory PartDemandResult.fromJson(Map<String, dynamic> json) =>
      PartDemandResult(
        requestNo: _integer(json['requestNo']),
        taskType: json['taskType'] as String? ?? '',
        status: json['status'] as String? ?? '',
        readiness: json['readiness'] as String? ?? '',
        predecessorRequestNo: json['predecessorRequestNo'] == null
            ? null
            : _integer(json['predecessorRequestNo']),
        predecessorTaskType: json['predecessorTaskType'] as String?,
        idempotentReplay: json['idempotentReplay'] as bool? ?? false,
      );
}

class PartDemandBatchResult {
  const PartDemandBatchResult({
    required this.idempotentReplay,
    required this.items,
  });

  final bool idempotentReplay;
  final List<PartDemandResult> items;

  factory PartDemandBatchResult.fromJson(Map<String, dynamic> json) {
    final raw = json['items'];
    final items = raw is List
        ? raw
              .whereType<Map<String, dynamic>>()
              .map(PartDemandResult.fromJson)
              .toList(growable: false)
        : const <PartDemandResult>[];
    return PartDemandBatchResult(
      idempotentReplay: json['idempotentReplay'] as bool? ?? false,
      items: items,
    );
  }
}

class OperationalContext {
  const OperationalContext({
    required this.machine,
    required this.activeVariantId,
    required this.activeVariantCode,
    required this.activeVariantName,
    required this.activeLayoutId,
    required this.activeLayoutCode,
    required this.activeLayoutName,
    required this.activeNavigationProfileId,
    required this.runtimeRowVersion,
    required this.variantLayouts,
    required this.slots,
    required this.productGroups,
    required this.activeTransportRequests,
    required this.taskTemplates,
  });

  final MachineSummary machine;
  final String? activeVariantId;
  final String? activeVariantCode;
  final String? activeVariantName;
  final String? activeLayoutId;
  final String? activeLayoutCode;
  final String? activeLayoutName;
  final String? activeNavigationProfileId;
  final int runtimeRowVersion;
  final List<VariantLayoutOption> variantLayouts;
  final List<LayoutSlot> slots;
  final List<ProductGroup> productGroups;
  final List<ActiveTransportRequest> activeTransportRequests;
  final List<TaskTemplate> taskTemplates;

  factory OperationalContext.fromJson(JsonMap json) => OperationalContext(
    machine: MachineSummary.fromJson(_map(json['machine'])),
    activeVariantId: json['activeVariantId'] as String?,
    activeVariantCode: json['activeVariantCode'] as String?,
    activeVariantName: json['activeVariantName'] as String?,
    activeLayoutId: json['activeLayoutId'] as String?,
    activeLayoutCode: json['activeLayoutCode'] as String?,
    activeLayoutName: json['activeLayoutName'] as String?,
    activeNavigationProfileId: json['activeNavigationProfileId'] as String?,
    runtimeRowVersion: _integer(json['runtimeRowVersion']),
    variantLayouts: _maps(
      json['variantLayouts'],
    ).map(VariantLayoutOption.fromJson).toList(growable: false),
    slots: _maps(
      json['slots'],
    ).map(LayoutSlot.fromJson).toList(growable: false),
    productGroups: _maps(
      json['productGroups'],
    ).map(ProductGroup.fromJson).toList(growable: false),
    activeTransportRequests: _maps(
      json['activeTransportRequests'],
    ).map(ActiveTransportRequest.fromJson).toList(growable: false),
    taskTemplates: _maps(
      json['taskTemplates'],
    ).map(TaskTemplate.fromJson).toList(growable: false),
  );
}

class TransportCommand {
  const TransportCommand({
    required this.machineId,
    required this.variantId,
    required this.layoutId,
    required this.navigationProfileId,
    required this.productGroupId,
    required this.partId,
    required this.containerTypeId,
    required this.operation,
    required this.sourceSlotId,
    required this.destinationSlotId,
    required this.priority,
  });

  final String machineId;
  final String variantId;
  final String layoutId;
  final String? navigationProfileId;
  final String? productGroupId;
  final String? partId;
  final String? containerTypeId;
  final String operation;
  final String? sourceSlotId;
  final String? destinationSlotId;
  final int priority;

  JsonMap toJson() => {
    'machineId': machineId,
    'variantId': variantId,
    'layoutId': layoutId,
    'navigationProfileId': navigationProfileId,
    'productGroupId': productGroupId,
    'partId': partId,
    'containerTypeId': containerTypeId,
    'operation': operation,
    'sourceSlotId': sourceSlotId,
    'destinationSlotId': destinationSlotId,
    'priority': priority,
  };

  String get materialSignature => jsonEncode(toJson());
}

class TransportResult {
  const TransportResult({
    required this.id,
    required this.requestNo,
    required this.status,
    required this.operation,
    required this.idempotentReplay,
  });

  final String id;
  final int requestNo;
  final String status;
  final String operation;
  final bool idempotentReplay;

  factory TransportResult.fromJson(JsonMap json) => TransportResult(
    id: json['id'] as String,
    requestNo: _integer(json['requestNo']),
    status: json['status'] as String? ?? '',
    operation: json['operation'] as String? ?? '',
    idempotentReplay: json['idempotentReplay'] as bool? ?? false,
  );
}

/// Customer UI — Warehouse Preparation Console: a WAITING_PREPARATION
/// transport request as returned by
/// GET /api/v1/warehouse/preparation-requests. Self-contained (machine/part/
/// slot codes are denormalized) since this list spans machines rather than
/// being scoped to one already-loaded operational context.
class WarehousePreparationRequest {
  const WarehousePreparationRequest({
    required this.requestId,
    required this.requestNo,
    required this.operation,
    required this.machineId,
    required this.machineCode,
    required this.machineDisplayName,
    required this.partCode,
    required this.containerTypeCode,
    required this.sourceSlotCode,
    required this.sourceSlotDisplayName,
    required this.destinationSlotCode,
    required this.destinationSlotDisplayName,
    required this.createdAt,
    required this.queuedAt,
  });

  final String requestId;
  final int requestNo;
  final String operation;
  final String machineId;
  final String machineCode;
  final String machineDisplayName;
  final String? partCode;
  final String? containerTypeCode;
  final String? sourceSlotCode;
  final String? sourceSlotDisplayName;
  final String? destinationSlotCode;
  final String? destinationSlotDisplayName;
  final DateTime? createdAt;
  final DateTime? queuedAt;

  factory WarehousePreparationRequest.fromJson(JsonMap json) =>
      WarehousePreparationRequest(
        requestId: json['requestId'] as String,
        requestNo: _integer(json['requestNo']),
        operation: json['operation'] as String? ?? '',
        machineId: json['machineId'] as String,
        machineCode: json['machineCode'] as String? ?? '',
        machineDisplayName: json['machineDisplayName'] as String? ?? '',
        partCode: json['partCode'] as String?,
        containerTypeCode: json['containerTypeCode'] as String?,
        sourceSlotCode: json['sourceSlotCode'] as String?,
        sourceSlotDisplayName: json['sourceSlotDisplayName'] as String?,
        destinationSlotCode: json['destinationSlotCode'] as String?,
        destinationSlotDisplayName:
            json['destinationSlotDisplayName'] as String?,
        createdAt: _date(json['createdAt']),
        queuedAt: _date(json['queuedAt']),
      );
}

/// Customer Monitoring — Robot Alarm Visibility: coarse Operator signal only
/// (GET /api/v1/rds/robot-alarm-summary). No robot identity or diagnostic
/// detail is exposed at this projection.
class RobotAlarmSummary {
  const RobotAlarmSummary({
    required this.hasActiveRobotAlarm,
    required this.highestSeverity,
    required this.activeAlarmCount,
  });

  final bool hasActiveRobotAlarm;
  final String? highestSeverity;
  final int activeAlarmCount;

  factory RobotAlarmSummary.fromJson(JsonMap json) => RobotAlarmSummary(
    hasActiveRobotAlarm: json['hasActiveRobotAlarm'] as bool? ?? false,
    highestSeverity: json['highestSeverity'] as String?,
    activeAlarmCount: _integer(json['activeAlarmCount']),
  );
}

/// A single [TransportCommand] bound to its own child idempotency key, as
/// required by POST /api/v1/transport-batches. Each item stays an
/// independent TransportRequest server-side; the batch is a pure grouping
/// boundary (no batch-level status, no shared readiness).
class TransportBatchItemCommand {
  const TransportBatchItemCommand({
    required this.command,
    required this.idempotencyKey,
  });

  final TransportCommand command;
  final String idempotencyKey;

  JsonMap toJson() => {'idempotencyKey': idempotencyKey, ...command.toJson()};
}

class TransportBatchResult {
  const TransportBatchResult({
    required this.batchId,
    required this.idempotentReplay,
    required this.items,
  });

  final String batchId;
  final bool idempotentReplay;
  final List<TransportResult> items;

  factory TransportBatchResult.fromJson(JsonMap json) => TransportBatchResult(
    batchId: json['batchId'] as String? ?? '',
    idempotentReplay: json['idempotentReplay'] as bool? ?? false,
    items: _maps(
      json['items'],
    ).map(TransportResult.fromJson).toList(growable: false),
  );
}

/// Operator-safe robot view.
///
/// The API deliberately does not serialize raw RDS technical data to a Line or
/// Warehouse Operator — no SEER uuid, vehicle_id, map, WorkSite/station, pose,
/// order or block identifier, and no lock fingerprint. This model therefore has
/// no fields for them: the boundary is a contract, not a rendering choice.
///
/// Connectivity and battery are nullable because absent telemetry is UNKNOWN,
/// never "offline" or "0%".
class RobotTelemetry {
  const RobotTelemetry({
    this.displayName,
    this.online,
    this.batteryLevel,
    this.hasActiveAlarm = false,
  });

  /// Verified friendly model/display name. Never a raw dispatch identifier.
  final String? displayName;

  /// Null when connectivity could not be observed.
  final bool? online;

  /// Null when battery telemetry is unavailable. Never defaulted to zero.
  final double? batteryLevel;

  /// True only for fatal/error faults. Benign notices do not raise this.
  final bool hasActiveAlarm;

  bool get isConnected => online == true;

  factory RobotTelemetry.fromJson(JsonMap json) => RobotTelemetry(
    displayName: json['displayName'] as String?,
    online: json['online'] as bool?,
    batteryLevel: _decimal(json['batteryLevel']),
    hasActiveAlarm: json['hasActiveAlarm'] as bool? ?? false,
  );
}

JsonMap _map(Object? value) => value is Map<String, dynamic> ? value : {};

List<JsonMap> _maps(Object? value) => value is List
    ? value.whereType<Map<String, dynamic>>().toList(growable: false)
    : const [];

List<String> _strings(Object? value) => value is List
    ? value.whereType<String>().toList(growable: false)
    : const [];

int _integer(Object? value) => value is num ? value.toInt() : 0;

int? _nullableInteger(Object? value) => value is num ? value.toInt() : null;

double? _decimal(Object? value) => value is num ? value.toDouble() : null;

DateTime? _date(Object? value) =>
    value is String && value.isNotEmpty ? DateTime.tryParse(value) : null;

/// Immutable business state captured at the inception of an Operator mutation.
/// Pins all operational context so in-flight requests and ambiguous retries
/// cannot be corrupted by subsequent screen or navigation changes.
///
/// Under domain safety rules, physical routing fields (sourceSlotId,
/// destinationSlotId, workSite, RDS IDs) are strictly excluded: the backend
/// remains authoritative for physical slot resolution and mapping.
class PendingOperation {
  const PendingOperation({
    required this.machineId,
    required this.machineCode,
    required this.variantId,
    this.variantCode,
    required this.layoutId,
    this.productGroupId,
    required this.partId,
    this.partCode,
    this.logicalContainerNumber,
    required this.operation,
    required this.idempotencyKey,
    required this.startedAt,
    this.navigationProfileId,
    this.productionSessionId,
  });

  final String machineId;
  final String machineCode;
  final String variantId;
  final String? variantCode;
  final String layoutId;
  final String? productGroupId;
  final String partId;
  final String? partCode;
  final int? logicalContainerNumber;
  final String operation;
  final String idempotencyKey;
  final DateTime startedAt;
  final String? navigationProfileId;
  final String? productionSessionId;

  @override
  String toString() =>
      'PendingOperation($machineCode, variant: $variantCode, part: $partCode, '
      'pos: $logicalContainerNumber, op: $operation, key: $idempotencyKey)';

  Map<String, dynamic> toJson() => {
    'machineId': machineId,
    'machineCode': machineCode,
    'variantId': variantId,
    'variantCode': variantCode,
    'layoutId': layoutId,
    'productGroupId': productGroupId,
    'partId': partId,
    'partCode': partCode,
    'logicalContainerNumber': logicalContainerNumber,
    'operation': operation,
    'idempotencyKey': idempotencyKey,
    'startedAt': startedAt.toIso8601String(),
    'navigationProfileId': navigationProfileId,
    'productionSessionId': productionSessionId,
  };
}

enum MutationGateReason {
  ready,
  offline,
  mutationPending,
  unauthenticated,
  resumePending,
  noContext,
}

/// Central client-side mutation gate evaluating safety and operational
/// prerequisites across all Operator mutation entry points.
class MutationGate {
  const MutationGate({
    required this.canMutate,
    required this.reason,
    required this.message,
  });

  final bool canMutate;
  final MutationGateReason reason;
  final String? message;
}
