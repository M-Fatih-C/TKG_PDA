/// The finished-goods store as the warehouse tablet reads it: shared locations
/// and the containers the robot left standing in them.
///
/// A container appears here when the transport that carried it completes. The
/// tablet never claims one arrived; it only reports what the operator took away.
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

/// One delivered container standing in the store.
class StoreUnit {
  const StoreUnit({
    required this.id,
    required this.transportRequestId,
    required this.requestNo,
    required this.partId,
    required this.partCode,
    required this.containerTypeId,
    required this.containerCode,
    required this.machineId,
    required this.machineCode,
    required this.deliveredAt,
    required this.rowVersion,
    this.loadState = 'UNKNOWN',
  });

  final String id;
  final String? transportRequestId;
  final int? requestNo;
  final String? partId;
  final String? partCode;
  final String? containerTypeId;
  final String? containerCode;
  final String? machineId;

  /// Which machine produced it. Null only for a unit recorded by hand.
  final String? machineCode;
  final DateTime? deliveredAt;

  /// Sent back when the operator removes it, so a screen left open on a
  /// container that has since gone cannot remove the one that replaced it.
  final int rowVersion;

  /// FULL is a finished product. EMPTY is an empty container standing in the
  /// area. Absence of a unit is vacant, not an empty container. Anything else,
  /// including a missing value, stays UNKNOWN and is never treated as FULL.
  final String loadState;
  bool get isEmptyContainer => loadState == 'EMPTY';

  /// Operator-facing load words. UNKNOWN is never presented as full or empty.
  String get loadPresentation => switch (loadState) {
    'FULL' => 'Dolu mamul',
    'EMPTY' => 'Boş kasa',
    _ => 'Durum bilinmiyor',
  };

  factory StoreUnit.fromJson(JsonMap json) => StoreUnit(
    id: json['id'] as String,
    transportRequestId: json['transportRequestId'] as String?,
    requestNo: json['requestNo'] == null ? null : _integer(json['requestNo']),
    partId: json['partId'] as String?,
    partCode: json['partCode'] as String?,
    containerTypeId: json['containerTypeId'] as String?,
    containerCode: json['containerCode'] as String?,
    machineId: json['machineId'] as String?,
    machineCode: json['machineCode'] as String?,
    deliveredAt: _time(json['deliveredAt']),
    rowVersion: _integer(json['rowVersion']),
    loadState: json['loadState'] is String
        ? json['loadState'] as String
        : 'UNKNOWN',
  );
}

class StoreLocation {
  const StoreLocation({
    required this.id,
    required this.code,
    required this.displayName,
    required this.capacity,
    required this.isEnabled,
    required this.units,
    this.isReserved = false,
    this.reservationType,
    this.reservationRequestId,
  });

  final String id;
  final String code;
  final String displayName;

  /// Null until the customer states how many containers fit. The screen then
  /// shows a count without a fill ratio rather than inventing a denominator.
  final int? capacity;
  final bool isEnabled;
  final List<StoreUnit> units;

  /// Whether a robot transport is currently in-flight to or from this location.
  final bool isReserved;

  /// 'PICKUP' (robot fetching empty) or 'DROP' (robot delivering full).
  final String? reservationType;

  /// Transport request ID holding the reservation.
  final String? reservationRequestId;

  int get unitCount => units.length;
  int get finishedProductCount =>
      units.where((unit) => unit.loadState == 'FULL').length;
  bool get isEmpty => units.isEmpty;
  bool get hasKnownCapacity => capacity != null && capacity! > 0;

  double? get fillRatio => hasKnownCapacity ? unitCount / capacity! : null;

  /// Never true while capacity is unknown.
  bool get isFull => hasKnownCapacity && unitCount >= capacity!;

  /// One container short of the stated capacity, so the operator hears about it
  /// before the store stops accepting deliveries rather than after.
  bool get isNearlyFull =>
      hasKnownCapacity && !isFull && unitCount >= capacity! - 1;

  /// Capacity warnings on the finished-goods screen concern finished product
  /// waiting for warehouse pickup. An EMPTY pallet occupies the position as a
  /// robot pickup source and must not ask the warehouse operator to clear it.
  bool get isFullOfFinishedProducts =>
      hasKnownCapacity && finishedProductCount >= capacity!;

  bool get isNearlyFullOfFinishedProducts =>
      hasKnownCapacity &&
      capacity! > 1 &&
      finishedProductCount > 0 &&
      !isFullOfFinishedProducts &&
      finishedProductCount >= capacity! - 1;

  factory StoreLocation.fromJson(JsonMap json) => StoreLocation(
    id: json['id'] as String,
    code: json['code'] as String? ?? '',
    displayName: json['displayName'] as String? ?? '',
    capacity: json['capacity'] == null ? null : _integer(json['capacity']),
    isEnabled: json['isEnabled'] as bool? ?? true,
    units: [
      for (final unit in (json['units'] as List? ?? const []))
        if (unit is JsonMap) StoreUnit.fromJson(unit),
    ],
    isReserved: json['isReserved'] as bool? ?? false,
    reservationType: json['reservationType'] as String?,
    reservationRequestId: json['reservationRequestId'] as String?,
  );
}
