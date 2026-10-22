import '../../core/network/api_client.dart';
import '../models/pda_models.dart';
import '../models/preparation_area.dart';
import '../models/store_location.dart';

class DeviceNotificationRegistration {
  const DeviceNotificationRegistration({
    required this.registrationId,
    required this.credential,
    required this.pollIntervalSeconds,
  });

  final String registrationId;
  final String credential;
  final int pollIntervalSeconds;

  factory DeviceNotificationRegistration.fromJson(Map<String, dynamic> json) =>
      DeviceNotificationRegistration(
        registrationId: json['registrationId'] as String? ?? '',
        credential: json['credential'] as String? ?? '',
        pollIntervalSeconds: json['pollIntervalSeconds'] is int
            ? json['pollIntervalSeconds'] as int
            : 5,
      );
}

abstract interface class PdaDataSource {
  Future<ProductionSession?> activeProduction(String machineId);
  Future<ProductionSession> startProduction(
    ProductionStartCommand command,
    String key,
  );
  Future<ProductionSession> endProduction(
    String machineId,
    String sessionId,
    String idempotencyKey,
  );
  Future<LoginSession> login(String employeeNumber);
  Future<void> heartbeatSession();
  Future<void> logout();
  Future<DeviceNotificationRegistration> registerDeviceNotification();
  Future<AuthenticatedUser> me();
  Future<List<MachineSummary>> machines();
  Future<OperationalContext> operationalContext(String machineId);
  Future<List<MachineMainPartOption>> machinePartOptions(String machineId);
  Future<List<CompatibleVariant>> compatibleVariants({
    required String machineId,
    required String mainPartId,
    required String partId,
  });
  Future<void> switchVariant({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
  });
  Future<TransportResult> createTransport(
    TransportCommand command,
    String idempotencyKey,
  );
  Future<TransportBatchResult> createTransportBatch(
    String machineId,
    String batchIdempotencyKey,
    List<TransportBatchItemCommand> items,
  );
  Future<List<ContainerPosition>> containerPositions({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String productGroupId,
    required String partId,
  });
  Future<PartDemandResult> createPartDemand({
    required String machineId,
    required String partId,
    required String idempotencyKey,
  });
  Future<PartDemandBatchResult> createPartDemandBatch({
    required String machineId,
    required List<String> partIds,
    required String idempotencyKey,
  });
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
  });
  Future<TransportBatchResult> createContainerActionBatch({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
    required List<ContainerActionBatchItemCommand> items,
    required String idempotencyKey,
    String? productionSessionId,
  });
  Future<RemoveAllResult> removeAllContainers({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
    required String idempotencyKey,
    String? productionSessionId,
  });
  Future<OperatorQueuePage> operatorQueue({
    bool terminal = false,
    int offset = 0,
    int limit = 50,
  });
  Future<TransportResult> transport(String requestId);
  Future<TransportResult> cancel({
    required String machineId,
    required String requestId,
  });

  /// Customer UI — Warehouse Preparation Console.
  Future<List<WarehousePreparationRequest>> warehousePreparationRequests();
  Future<void> markTransportReady(String requestId);

  /// Warehouse tablet — the four shared preparation areas.
  Future<List<PreparationArea>> preparationAreas();
  Future<List<WarehouseDemand>> warehouseDemands();
  Future<PreparationPallet> placePallet({
    required String areaId,
    required String load,
    required String containerTypeId,
    String? partId,
    String? transportRequestId,
    required String idempotencyKey,
  });
  Future<PreparationPallet> markPalletReady({
    required String palletId,
    required int expectedRowVersion,
  });
  Future<PreparationPallet> releasePreparationArea({
    required String palletId,
    required int expectedRowVersion,
    required String reason,
  });

  /// Finished-goods warehouse — six request-bound empty-container areas.
  Future<List<PreparationArea>> finishedGoodsPreparationAreas();
  Future<List<WarehouseDemand>> finishedGoodsPreparationDemands();
  Future<PreparationPallet> placeFinishedGoodsPreparation({
    required String areaId,
    required String containerTypeId,
    required String transportRequestId,
    required String idempotencyKey,
  });
  Future<PreparationPallet> markFinishedGoodsPreparationReady({
    required String palletId,
    required int expectedRowVersion,
  });
  Future<PreparationPallet> releaseFinishedGoodsPreparation({
    required String palletId,
    required int expectedRowVersion,
    required String reason,
  });

  /// Warehouse tablet — the finished-goods store.
  Future<List<StoreLocation>> storeLocations();
  Future<StoreLocation> removeStoreUnit({
    required String unitId,
    required int expectedRowVersion,
  });
  Future<StoreLocation> clearStoreLocation(String locationId);
  Future<StoreLocation> recordEmptyContainer({
    required String locationId,
    required String idempotencyKey,
  });

  /// Customer Monitoring — Robot Alarm Visibility.
  Future<RobotAlarmSummary> robotAlarmSummary();

  /// Real RDSCore Telemetry & Control (Phase 4).
  Future<List<RobotTelemetry>> robots();
  Future<void> pauseRobot([List<String>? vehicles]);
  Future<void> resumeRobot([List<String>? vehicles]);
  Future<void> clearRobotErrors([List<String>? vehicles]);
  Future<void> setRequestPriority(String requestId, int priority);
}

class PdaRepository implements PdaDataSource {
  const PdaRepository(this._api);

  final ApiClient _api;

  @override
  Future<ProductionSession?> activeProduction(String machineId) async {
    final response = await _api.get(
      '/api/v1/machines/$machineId/production-sessions/active',
    );
    return response.statusCode == 204
        ? null
        : ProductionSession.fromJson(_json(response.body));
  }

  @override
  Future<ProductionSession> startProduction(
    ProductionStartCommand command,
    String key,
  ) async {
    final response = await _api.post(
      '/api/v1/machines/${command.machineId}/production-sessions',
      body: command.toJson(),
      idempotencyKey: key,
    );
    return ProductionSession.fromJson(_json(_json(response.body)['session']));
  }

  @override
  Future<ProductionSession> endProduction(
    String machineId,
    String sessionId,
    String idempotencyKey,
  ) async {
    final response = await _api.post(
      '/api/v1/machines/$machineId/production-sessions/$sessionId/end',
      idempotencyKey: idempotencyKey,
    );
    return ProductionSession.fromJson(_json(_json(response.body)['session']));
  }

  @override
  Future<LoginSession> login(String employeeNumber) async {
    // Production Sicil login is passwordless: identity is established by
    // employeeNumber + the already-authorized device, so no password (and
    // no username) is ever sent for this flow.
    final response = await _api.post(
      '/api/v1/auth/login',
      body: {'employeeNumber': employeeNumber},
      mutation: false,
    );
    return LoginSession.fromJson(_json(response.body));
  }

  @override
  Future<AuthenticatedUser> me() async {
    final response = await _api.get('/api/v1/auth/me');
    return AuthenticatedUser.fromJson(_json(response.body));
  }

  @override
  Future<void> heartbeatSession() async {
    await _api.post('/api/v1/auth/session/heartbeat');
  }

  @override
  Future<void> logout() async {
    await _api.post('/api/v1/auth/logout');
  }

  @override
  Future<DeviceNotificationRegistration> registerDeviceNotification() async {
    final response = await _api.put(
      '/api/v1/auth/device-notifications/register',
    );
    return DeviceNotificationRegistration.fromJson(_json(response.body));
  }

  @override
  Future<List<MachineSummary>> machines() async {
    final response = await _api.get('/api/v1/machines');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(MachineSummary.fromJson)
        .toList(growable: false);
  }

  @override
  Future<OperationalContext> operationalContext(String machineId) async {
    final response = await _api.get(
      '/api/v1/machines/$machineId/operational-context',
    );
    return OperationalContext.fromJson(_json(response.body));
  }

  @override
  Future<List<MachineMainPartOption>> machinePartOptions(
    String machineId,
  ) async {
    final response = await _api.get('/api/v1/machines/$machineId/part-options');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(MachineMainPartOption.fromJson)
        .toList(growable: false);
  }

  @override
  Future<List<CompatibleVariant>> compatibleVariants({
    required String machineId,
    required String mainPartId,
    required String partId,
  }) async {
    final response = await _api.get(
      '/api/v1/machines/$machineId/parts/$partId/compatible-variants',
      queryParameters: {'mainPartId': mainPartId},
    );
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(CompatibleVariant.fromJson)
        .toList(growable: false);
  }

  @override
  Future<void> switchVariant({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String? navigationProfileId,
  }) async {
    await _api.post(
      '/api/v1/machines/$machineId/variant-selection',
      body: {
        'variantId': variantId,
        'layoutId': layoutId,
        'navigationProfileId': navigationProfileId,
      },
    );
  }

  @override
  Future<TransportResult> createTransport(
    TransportCommand command,
    String idempotencyKey,
  ) async {
    final response = await _api.post(
      '/api/v1/transport-requests',
      body: command.toJson(),
      idempotencyKey: idempotencyKey,
    );
    return TransportResult.fromJson(_json(response.body));
  }

  @override
  Future<List<ContainerPosition>> containerPositions({
    required String machineId,
    required String variantId,
    required String layoutId,
    required String productGroupId,
    required String partId,
  }) async {
    final response = await _api.get(
      '/api/v1/machines/$machineId/container-positions',
      queryParameters: {
        'variantId': variantId,
        'layoutId': layoutId,
        'productGroupId': productGroupId,
        'partId': partId,
      },
    );
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(ContainerPosition.fromJson)
        .toList(growable: false);
  }

  @override
  Future<PartDemandResult> createPartDemand({
    required String machineId,
    required String partId,
    required String idempotencyKey,
  }) async {
    final response = await _api.post(
      '/api/v1/machines/$machineId/part-demands',
      idempotencyKey: idempotencyKey,
      body: {'partId': partId},
    );
    return PartDemandResult.fromJson(_json(response.body));
  }

  @override
  Future<PartDemandBatchResult> createPartDemandBatch({
    required String machineId,
    required List<String> partIds,
    required String idempotencyKey,
  }) async {
    final response = await _api.post(
      '/api/v1/machines/$machineId/part-demands/batch',
      idempotencyKey: idempotencyKey,
      body: {'partIds': partIds},
    );
    return PartDemandBatchResult.fromJson(_json(response.body));
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
    final response = await _api.post(
      '/api/v1/machines/$machineId/container-actions',
      idempotencyKey: idempotencyKey,
      body: {
        'variantId': variantId,
        'layoutId': layoutId,
        'navigationProfileId': navigationProfileId,
        'productGroupId': productGroupId,
        'partId': partId,
        'positionNumber': positionNumber,
        'action': action,
        'productionSessionId': ?productionSessionId,
      },
    );
    return ContainerActionResult.fromJson(_json(response.body));
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
    final response = await _api.post(
      '/api/v1/machines/$machineId/container-actions/batch',
      idempotencyKey: idempotencyKey,
      body: {
        'variantId': variantId,
        'layoutId': layoutId,
        'navigationProfileId': navigationProfileId,
        'items': items.map((item) => item.toJson()).toList(growable: false),
        'productionSessionId': ?productionSessionId,
      },
    );
    return TransportBatchResult.fromJson(_json(response.body));
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
    final response = await _api.post(
      '/api/v1/machines/$machineId/container-actions/remove-all',
      idempotencyKey: idempotencyKey,
      body: {
        'productionSessionId': ?productionSessionId,
        'variantId': variantId,
        'layoutId': layoutId,
        'navigationProfileId': navigationProfileId,
      },
    );
    return RemoveAllResult.fromJson(_json(response.body));
  }

  @override
  Future<OperatorQueuePage> operatorQueue({
    bool terminal = false,
    int offset = 0,
    int limit = 50,
  }) async {
    final response = await _api.get(
      '/api/v1/transport-requests/queue',
      queryParameters: {
        'terminal': '$terminal',
        'offset': '$offset',
        'limit': '$limit',
      },
    );
    return OperatorQueuePage.fromJson(_json(response.body));
  }

  @override
  Future<TransportBatchResult> createTransportBatch(
    String machineId,
    String batchIdempotencyKey,
    List<TransportBatchItemCommand> items,
  ) async {
    final response = await _api.post(
      '/api/v1/transport-batches',
      body: {
        'idempotencyKey': batchIdempotencyKey,
        'machineId': machineId,
        'items': items.map((item) => item.toJson()).toList(),
      },
    );
    return TransportBatchResult.fromJson(_json(response.body));
  }

  @override
  Future<TransportResult> transport(String requestId) async {
    final response = await _api.get('/api/v1/transport-requests/$requestId');
    return TransportResult.fromJson(_json(response.body));
  }

  @override
  Future<TransportResult> cancel({
    required String machineId,
    required String requestId,
  }) async {
    final response = await _api.post(
      '/api/v1/machines/$machineId/transport-requests/$requestId/cancel',
    );
    return TransportResult.fromJson(_json(response.body));
  }

  @override
  Future<List<WarehousePreparationRequest>>
  warehousePreparationRequests() async {
    final response = await _api.get('/api/v1/warehouse/preparation-requests');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(WarehousePreparationRequest.fromJson)
        .toList(growable: false);
  }

  @override
  Future<void> markTransportReady(String requestId) async {
    await _api.post('/api/v1/transport-requests/$requestId/ready');
  }

  static const _preparation = '/api/v1/warehouse/preparation';

  @override
  Future<List<PreparationArea>> preparationAreas() async {
    final response = await _api.get('$_preparation/areas');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(PreparationArea.fromJson)
        .toList(growable: false);
  }

  @override
  Future<List<WarehouseDemand>> warehouseDemands() async {
    final response = await _api.get('$_preparation/demands');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(WarehouseDemand.fromJson)
        .toList(growable: false);
  }

  @override
  Future<PreparationPallet> placePallet({
    required String areaId,
    required String load,
    required String containerTypeId,
    String? partId,
    String? transportRequestId,
    required String idempotencyKey,
  }) async {
    final response = await _api.post(
      '$_preparation/areas/$areaId/pallet',
      body: {
        'load': load,
        'containerTypeId': containerTypeId,
        'partId': ?partId,
        'transportRequestId': ?transportRequestId,
      },
      idempotencyKey: idempotencyKey,
    );
    return PreparationPallet.fromJson(_json(response.body));
  }

  @override
  Future<PreparationPallet> markPalletReady({
    required String palletId,
    required int expectedRowVersion,
  }) async {
    final response = await _api.post(
      '$_preparation/pallets/$palletId/ready',
      body: {'expectedRowVersion': expectedRowVersion},
    );
    return PreparationPallet.fromJson(_json(response.body));
  }

  @override
  Future<PreparationPallet> releasePreparationArea({
    required String palletId,
    required int expectedRowVersion,
    required String reason,
  }) async {
    final response = await _api.post(
      '$_preparation/pallets/$palletId/release',
      body: {'expectedRowVersion': expectedRowVersion, 'reason': reason},
    );
    return PreparationPallet.fromJson(_json(response.body));
  }

  static const _finishedGoodsPreparation =
      '/api/v1/warehouse/finished-goods-preparation';

  @override
  Future<List<PreparationArea>> finishedGoodsPreparationAreas() async {
    final response = await _api.get('$_finishedGoodsPreparation/areas');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(PreparationArea.fromJson)
        .toList(growable: false);
  }

  @override
  Future<List<WarehouseDemand>> finishedGoodsPreparationDemands() async {
    final response = await _api.get('$_finishedGoodsPreparation/demands');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(WarehouseDemand.fromJson)
        .toList(growable: false);
  }

  @override
  Future<PreparationPallet> placeFinishedGoodsPreparation({
    required String areaId,
    required String containerTypeId,
    required String transportRequestId,
    required String idempotencyKey,
  }) async {
    final response = await _api.post(
      '$_finishedGoodsPreparation/areas/$areaId/pallet',
      body: {
        'load': 'EMPTY',
        'containerTypeId': containerTypeId,
        'transportRequestId': transportRequestId,
      },
      idempotencyKey: idempotencyKey,
    );
    return PreparationPallet.fromJson(_json(response.body));
  }

  @override
  Future<PreparationPallet> markFinishedGoodsPreparationReady({
    required String palletId,
    required int expectedRowVersion,
  }) async {
    final response = await _api.post(
      '$_finishedGoodsPreparation/pallets/$palletId/ready',
      body: {'expectedRowVersion': expectedRowVersion},
    );
    return PreparationPallet.fromJson(_json(response.body));
  }

  @override
  Future<PreparationPallet> releaseFinishedGoodsPreparation({
    required String palletId,
    required int expectedRowVersion,
    required String reason,
  }) async {
    final response = await _api.post(
      '$_finishedGoodsPreparation/pallets/$palletId/release',
      body: {'expectedRowVersion': expectedRowVersion, 'reason': reason},
    );
    return PreparationPallet.fromJson(_json(response.body));
  }

  static const _store = '/api/v1/warehouse/store';

  @override
  Future<List<StoreLocation>> storeLocations() async {
    final response = await _api.get('$_store/locations');
    final values = response.body is List ? response.body as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(StoreLocation.fromJson)
        .toList(growable: false);
  }

  @override
  Future<StoreLocation> removeStoreUnit({
    required String unitId,
    required int expectedRowVersion,
  }) async {
    final response = await _api.post(
      '$_store/units/$unitId/remove',
      body: {'expectedRowVersion': expectedRowVersion},
    );
    return StoreLocation.fromJson(_json(response.body));
  }

  @override
  Future<StoreLocation> recordEmptyContainer({
    required String locationId,
    required String idempotencyKey,
  }) async {
    final response = await _api.post(
      '$_store/locations/$locationId/empty-container',
      idempotencyKey: idempotencyKey,
    );
    return StoreLocation.fromJson(_json(response.body));
  }

  @override
  Future<StoreLocation> clearStoreLocation(String locationId) async {
    final response = await _api.post('$_store/locations/$locationId/clear');
    return StoreLocation.fromJson(_json(response.body));
  }

  @override
  Future<RobotAlarmSummary> robotAlarmSummary() async {
    final response = await _api.get('/api/v1/rds/robot-alarm-summary');
    return RobotAlarmSummary.fromJson(_json(response.body));
  }

  @override
  Future<List<RobotTelemetry>> robots() async {
    final response = await _api.get('/api/v1/rds/robots');
    final map = _json(response.body);
    final values = map['robots'] is List ? map['robots'] as List : const [];
    return values
        .whereType<Map<String, dynamic>>()
        .map(RobotTelemetry.fromJson)
        .toList(growable: false);
  }

  @override
  Future<void> pauseRobot([List<String>? vehicles]) async {
    await _api.post('/api/v1/rds/robots/pause', body: {'vehicles': vehicles});
  }

  @override
  Future<void> resumeRobot([List<String>? vehicles]) async {
    await _api.post('/api/v1/rds/robots/resume', body: {'vehicles': vehicles});
  }

  @override
  Future<void> clearRobotErrors([List<String>? vehicles]) async {
    await _api.post(
      '/api/v1/rds/robots/clear-errors',
      body: {'vehicles': vehicles},
    );
  }

  @override
  Future<void> setRequestPriority(String requestId, int priority) async {
    await _api.post(
      '/api/v1/transport-requests/$requestId/priority',
      body: {'priority': priority},
    );
  }

  static JsonMap _json(Object? body) =>
      body is Map<String, dynamic> ? body : const {};
}
