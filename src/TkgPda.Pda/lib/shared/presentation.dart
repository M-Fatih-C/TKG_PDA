import 'package:flutter/material.dart';

String actionLabel(String action) => switch (action) {
  'BRING_EMPTY' => 'Boş Kasa Getir',
  'BRING_FULL' => 'Dolu Kasa Getir',
  'REMOVE_FULL' => 'Dolu Kasayı Kaldır',
  'REMOVE_EMPTY' => 'Boş Kasayı Kaldır',
  _ => action,
};

String actionQuestion(String action) => switch (action) {
  'BRING_EMPTY' => 'Boş kasa getirmek istiyor musunuz?',
  'REMOVE_FULL' => 'Dolu kasayı kaldırmak istiyor musunuz?',
  'REMOVE_EMPTY' => 'Boş kasayı kaldırmak istiyor musunuz?',
  _ => 'Taşıma talebi oluşturulsun mu?',
};

IconData actionIcon(String action) => switch (action) {
  'BRING_EMPTY' => Icons.move_to_inbox_outlined,
  'REMOVE_FULL' => Icons.outbox,
  'REMOVE_EMPTY' => Icons.outbox_outlined,
  _ => Icons.local_shipping_outlined,
};

String executionStateLabel(String state) => switch (state) {
  'REQUESTED' => 'Talep oluşturuldu',
  'QUEUED' => 'Sırada',
  'WAITING_FOR_ROBOT' => 'Robot bekleniyor',
  'ROBOT_ASSIGNED' => 'Robot atandı',
  'RUNNING' => 'Taşıma devam ediyor',
  'WAITING' => 'Geçiş için bekleniyor',
  'COMPLETED' => 'Tamamlandı',
  'FAILED' => 'Tamamlanamadı',
  'TERMINATED' => 'Sonlandırıldı',
  _ => 'Durum güncelleniyor',
};

/// Warehouse readiness of a request, kept apart from its execution state.
String? requestReadinessLabel(String state) => switch (state) {
  'WAITING_PREPARATION' => 'Depo hazırlığı bekleniyor',
  'READY' => 'Hazır',
  'NOT_REQUIRED' => 'Hazırlık gerekmiyor',
  _ => null,
};

/// Friendly label for a server-derived part relation. Only ALT_PART is
/// surfaced as a badge; the other relations are never printed as enum text.
bool isAltPartRelation(String? relationType) => relationType == 'ALT_PART';

/// Formats a server-measured duration. The client never reads its own clock
/// for this: the value only changes when a newer server projection arrives.
String formatServerDuration(int seconds) {
  final total = seconds < 0 ? 0 : seconds;
  final hours = total ~/ 3600;
  final minutes = (total % 3600) ~/ 60;
  final secs = total % 60;
  String two(int value) => value.toString().padLeft(2, '0');
  return hours > 0
      ? '$hours:${two(minutes)}:${two(secs)}'
      : '${two(minutes)}:${two(secs)}';
}

String occupancyLabel(String state) => switch (state) {
  'VACANT' => 'Boş konum',
  'OCCUPIED' => 'Dolu konum',
  _ => 'Doluluk bilinmiyor',
};

String loadLabel(String state) => switch (state) {
  'EMPTY' => 'Boş kasa/palet',
  'FULL' => 'Dolu kasa/palet',
  _ => 'Yük durumu bilinmiyor',
};

String failureLabel(String? code, String? operatorMessage) {
  if (operatorMessage != null && operatorMessage.trim().isNotEmpty) {
    return operatorMessage;
  }
  return switch (code) {
    'WORKSITE_BUSY' => 'Konum şu anda kullanımda.',
    'PATH_UNAVAILABLE' => 'Robot hedef noktaya ulaşamıyor.',
    'EXECUTION_FAILED' => 'Robot görevi tamamlayamadı.',
    'TRAFFIC_WAITING' => 'Robot trafiği nedeniyle görev bekliyor.',
    _ => 'İşlem durumu kontrol ediliyor.',
  };
}

String readinessLabel(String? code) => switch (code) {
  'RDS_WORKSITE_NOT_MAPPED' => 'Robot noktası henüz eşlenmemiş.',
  'RDS_WORKSITE_NOT_PHYSICAL' =>
    'Eşlenen robot noktası fiziksel taşıma noktası değil.',
  'RDS_WORKSITE_STALE' =>
    'Robot noktası için güncel senkronizasyon kanıtı yok.',
  'RDS_WORKSITE_OCCUPANCY_CONFLICT' =>
    'Fiziksel alan ile sistem doluluk bilgisi uyuşmuyor. Alanı doğrulatıp RDS senkronundan sonra yeniden deneyin.',
  'RDS_WORKSITE_STATE_UNKNOWN' =>
    'Fiziksel alanın durumu bilinmiyor. RDS senkronunu kontrol edip ekranı yenileyin.',
  'RDS_WORKSITE_DISABLED' => 'Robot noktası kullanım dışı.',
  'RDS_WORKSITE_SYNC_FAILED' => 'Robot noktası senkronize değil.',
  'RDS_WORKSITE_LOCKED' => 'Robot noktası şu anda kullanımda.',
  _ => 'Robot yürütme hazırlığı tamamlanmamış.',
};

String knownOr(String? value) =>
    value == null || value.trim().isEmpty ? 'Tanımlı değil' : value;

/// Returns the customer-facing part/location evidence without exposing the
/// generated database slot code. Slot codes remain available to Admin and the
/// backend, but they are not knowledge a line Operator needs for routing.
String operatorLocationLabel(String? displayName, String? slotCode) {
  var label = displayName?.trim() ?? '';
  final code = slotCode?.trim() ?? '';
  if (code.isNotEmpty && label.startsWith(code)) {
    label = label.substring(code.length).trimLeft();
    for (final separator in const ['—', '–', '·', '-', ':']) {
      if (label.startsWith(separator)) {
        label = label.substring(separator.length).trimLeft();
        break;
      }
    }
  }
  if (label.isEmpty || label == code) return 'Konum';
  if (label.toLowerCase() == 'unresolved') return 'Eşleşmesi beklenen konum';
  return label;
}

/// The backend always supplies a friendly Turkish `disabledMessage` today.
/// This is defensive hardening only, so a raw uppercase code (e.g.
/// `SLOT_BUSY`) is never shown to the operator if that ever stops being true.
String disabledReason(String? disabledMessage) =>
    disabledMessage ?? 'Bu işlem şu anda kullanılamıyor.';
