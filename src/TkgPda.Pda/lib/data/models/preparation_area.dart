/// Warehouse preparation areas as the tablet reads them.
///
/// Every field here is what the server reported. Nothing is derived from a
/// device clock or filled in when the server left it null: an area whose pallet
/// carries no part code is shown without one rather than guessing.
library;

import 'pda_models.dart' show JsonMap;

int _integer(Object? value) => switch (value) {
  int value => value,
  num value => value.toInt(),
  String value => int.tryParse(value) ?? 0,
  _ => 0,
};

DateTime? _time(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toLocal() : null;

/// What physically stands on an area. Null [partId] means an empty container;
/// its [transportRequestId] still identifies the exact finished-goods demand.
class PreparationPallet {
  const PreparationPallet({
    required this.id,
    required this.containerTypeId,
    required this.containerCode,
    required this.load,
    required this.partId,
    required this.partCode,
    required this.transportRequestId,
    required this.requestNo,
    required this.targetMachineCode,
    required this.placedAt,
    required this.readyAt,
    required this.rowVersion,
    required this.requestStatus,
  });

  final String id;
  final String containerTypeId;
  final String containerCode;

  /// 'EMPTY' or 'FULL', as the server reports it.
  final String load;
  final String? partId;
  final String? partCode;
  final String? transportRequestId;
  final int? requestNo;
  final String? targetMachineCode;
  final DateTime? placedAt;
  final DateTime? readyAt;
  final String? requestStatus;

  /// The version this screen is showing. Sent back on ready and release so a
  /// screen left open on a pallet that has since been replaced cannot confirm
  /// the new one.
  final int rowVersion;

  bool get isReady => readyAt != null;
  bool get holdsMaterial => load == 'FULL';
  bool get isBoundToRequest => transportRequestId != null;
  bool get canConfirmCollected =>
      requestStatus == 'SENT_TO_RDS' ||
      requestStatus == 'RUNNING' ||
      requestStatus == 'COMPLETED';
  bool get canWithdraw =>
      requestStatus == 'CREATED' ||
      requestStatus == 'VALIDATING' ||
      requestStatus == 'QUEUED' ||
      requestStatus == 'FAILED' ||
      requestStatus == 'CANCELLED';

  factory PreparationPallet.fromJson(JsonMap json) => PreparationPallet(
    id: json['id'] as String,
    containerTypeId: json['containerTypeId'] as String? ?? '',
    containerCode: json['containerCode'] as String? ?? '',
    load: json['load'] as String? ?? '',
    partId: json['partId'] as String?,
    partCode: json['partCode'] as String?,
    transportRequestId: json['transportRequestId'] as String?,
    requestNo: json['requestNo'] == null ? null : _integer(json['requestNo']),
    targetMachineCode: json['targetMachineCode'] as String?,
    placedAt: _time(json['placedAt']),
    readyAt: _time(json['readyAt']),
    rowVersion: _integer(json['rowVersion']),
    requestStatus: json['requestStatus'] as String?,
  );
}

class PreparationArea {
  const PreparationArea({
    required this.id,
    required this.areaNumber,
    required this.displayName,
    required this.isEnabled,
    required this.pallet,
  });

  final String id;
  final int areaNumber;
  final String displayName;
  final bool isEnabled;
  final PreparationPallet? pallet;

  bool get isFree => pallet == null;

  factory PreparationArea.fromJson(JsonMap json) => PreparationArea(
    id: json['id'] as String,
    areaNumber: _integer(json['areaNumber']),
    displayName: json['displayName'] as String? ?? '',
    isEnabled: json['isEnabled'] as bool? ?? true,
    pallet: json['pallet'] is JsonMap
        ? PreparationPallet.fromJson(json['pallet'] as JsonMap)
        : null,
  );
}

/// A machine's outstanding request, for the side list. [requestedAt] is the
/// server's own timestamp; when it is missing the screen says so rather than
/// substituting the tablet's clock.
class WarehouseDemand {
  const WarehouseDemand({
    required this.requestId,
    required this.requestNo,
    required this.machineId,
    required this.machineCode,
    required this.partId,
    required this.partCode,
    required this.containerTypeId,
    required this.containerCode,
    required this.containerDimensions,
    required this.operation,
    required this.requestedLoad,
    required this.requestedAt,
    required this.claimedByAreaId,
    required this.claimedByAreaNumber,
    this.partDescription,
    this.variantCode,
    this.variantName,
    this.productGroupName,
    this.alternativeContainerTypeId,
    this.alternativeContainerCode,
  });

  final String requestId;
  final int requestNo;
  final String machineId;
  final String machineCode;
  final String? partId;
  final String? partCode;
  final String? partDescription;
  final String? variantCode;
  final String? variantName;
  final String? productGroupName;
  final String? containerTypeId;
  final String? containerCode;
  final String? alternativeContainerTypeId;
  final String? alternativeContainerCode;
  final String? containerDimensions;

  /// 'BRING_FULL', 'BRING_EMPTY', …
  final String operation;

  /// What has to go in the container: 'FULL' or 'EMPTY'.
  final String requestedLoad;
  final DateTime? requestedAt;

  /// Set when an area is already preparing this demand.
  final String? claimedByAreaId;
  final int? claimedByAreaNumber;

  bool get isClaimed => claimedByAreaId != null;
  bool get needsMaterial => requestedLoad == 'FULL';

  factory WarehouseDemand.fromJson(JsonMap json) => WarehouseDemand(
    requestId: json['requestId'] as String,
    requestNo: _integer(json['requestNo']),
    machineId: json['machineId'] as String? ?? '',
    machineCode: json['machineCode'] as String? ?? '',
    partId: json['partId'] as String?,
    partCode: json['partCode'] as String?,
    partDescription: json['partDescription'] as String?,
    containerTypeId: json['containerTypeId'] as String?,
    containerCode: json['containerCode'] as String?,
    alternativeContainerTypeId: json['alternativeContainerTypeId'] as String?,
    alternativeContainerCode: json['alternativeContainerCode'] as String?,
    containerDimensions: json['containerDimensions'] as String?,
    operation: json['operation'] as String? ?? '',
    requestedLoad: json['requestedLoad'] as String? ?? '',
    requestedAt: _time(json['requestedAt']),
    claimedByAreaId: json['claimedByAreaId'] as String?,
    claimedByAreaNumber: json['claimedByAreaNumber'] == null
        ? null
        : _integer(json['claimedByAreaNumber']),
    variantCode: json['variantCode'] as String?,
    variantName: json['variantName'] as String?,
    productGroupName: json['productGroupName'] as String?,
  );
}
