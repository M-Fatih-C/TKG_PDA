import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import '../../app/app_controller.dart' show AppStage;

/// Three warehouse notification types authorized by the business rules:
/// 1. [demand]: New semi-finished or empty container preparation request.
/// 2. [delivery]: New finished good recorded in the finished-goods store.
/// 3. [capacity]: Capacity warnings (1 free area left in preparation, or store location full).
enum WarehouseNotificationType {
  demand,
  delivery,
  capacity,
}

class WarehouseNotification {
  WarehouseNotification({
    required this.id,
    required this.type,
    required this.title,
    required this.message,
    required this.timestamp,
    required this.targetStage,
    this.machineCode,
    this.partCode,
    this.locationName,
    this.isRead = false,
  });

  final String id;
  final WarehouseNotificationType type;
  final String title;
  final String message;
  final DateTime timestamp;
  final AppStage targetStage;
  final String? machineCode;
  final String? partCode;
  final String? locationName;
  bool isRead;
}

/// Central notification coordinator for the warehouse tablet operator.
///
/// Lives at the AppController level so screen changes between warehouse
/// home, preparation areas, and store do NOT re-trigger sounds or duplicate
/// notifications.
///
/// Sounds and haptic feedback use Flutter's built-in [SystemSound] and
/// [HapticFeedback], requiring zero third-party dependencies and honoring
/// OS-level silent mode and volume settings.
class WarehouseNotificationCoordinator extends ChangeNotifier {
  WarehouseNotificationCoordinator();

  static const int maxNotificationHistory = 50;
  static const Duration bannerDisplayDuration = Duration(seconds: 5);

  final List<WarehouseNotification> _notifications = [];
  final Set<String> _seenEventIds = {};
  final Set<String> _soundPlayedEventIds = {};

  bool _soundEnabled = true;
  WarehouseNotification? _activeBanner;
  Timer? _bannerDismissTimer;
  bool _disposed = false;

  bool get soundEnabled => _soundEnabled;
  WarehouseNotification? get activeBanner => _activeBanner;
  List<WarehouseNotification> get notifications =>
      List.unmodifiable(_notifications);
  int get unreadCount => _notifications.where((n) => !n.isRead).length;
  bool get hasUnread => unreadCount > 0;

  void toggleSound([bool? enabled]) {
    _soundEnabled = enabled ?? !_soundEnabled;
    _notify();
  }

  void dismissActiveBanner() {
    _bannerDismissTimer?.cancel();
    _bannerDismissTimer = null;
    if (_activeBanner != null) {
      _activeBanner = null;
      _notify();
    }
  }

  void markAllRead() {
    for (final notification in _notifications) {
      notification.isRead = true;
    }
    _notify();
  }

  void markRead(String id) {
    final item = _notifications.where((n) => n.id == id).firstOrNull;
    if (item != null && !item.isRead) {
      item.isRead = true;
      _notify();
    }
  }

  void clearAll() {
    _notifications.clear();
    _activeBanner = null;
    _bannerDismissTimer?.cancel();
    _notify();
  }

  /// 1. New semi-finished or empty container preparation demand.
  void notifyDemand({
    required String requestId,
    required String machineCode,
    required String partCode,
    required bool needsMaterial,
    required int requestNo,
  }) {
    final eventId = 'demand-$requestId';
    if (_seenEventIds.contains(eventId)) return;
    _seenEventIds.add(eventId);

    final notification = WarehouseNotification(
      id: eventId,
      type: WarehouseNotificationType.demand,
      title: 'Yeni Hazırlık Talebi: $machineCode',
      message:
          '$partCode · ${needsMaterial ? "Yarı Mamul (Dolu Kasa)" : "Boş Kasa"} · Talep #$requestNo',
      timestamp: DateTime.now(),
      targetStage: AppStage.preparationAreas,
      machineCode: machineCode,
      partCode: partCode,
    );

    _addNotification(notification);
    _playAlert(
      eventId: eventId,
      soundType: SystemSoundType.alert,
      hapticAction: HapticFeedback.mediumImpact,
    );
  }

  /// 2. New finished good delivered to store.
  void notifyDelivery({
    required String unitId,
    required String locationName,
    required String partCode,
    String? machineCode,
    int? requestNo,
  }) {
    final eventId = 'delivery-$unitId';
    if (_seenEventIds.contains(eventId)) return;
    _seenEventIds.add(eventId);

    final fromMachine =
        machineCode != null && machineCode.isNotEmpty ? ' ($machineCode)' : '';
    final reqLabel = requestNo != null ? ' · Talep #$requestNo' : '';

    final notification = WarehouseNotification(
      id: eventId,
      type: WarehouseNotificationType.delivery,
      title: 'Yeni Ürün Teslimatı: $locationName',
      message: '$partCode$fromMachine$reqLabel alana teslim edildi.',
      timestamp: DateTime.now(),
      targetStage: AppStage.finishedGoodsStore,
      locationName: locationName,
      partCode: partCode,
      machineCode: machineCode,
    );

    _addNotification(notification);
    _playAlert(
      eventId: eventId,
      soundType: SystemSoundType.click,
      hapticAction: HapticFeedback.lightImpact,
    );
  }

  /// 3. Preparation area capacity warning (1 free area left, or none left).
  void notifyPreparationCapacity({
    required int freeAreaCount,
    required int totalAreas,
  }) {
    if (totalAreas == 0) return;
    final eventId =
        freeAreaCount == 0 ? 'prep-capacity-full' : 'prep-capacity-one-left';
    if (_seenEventIds.contains(eventId)) return;
    _seenEventIds.add(eventId);

    final message = freeAreaCount == 0
        ? 'Hazırlık alanlarının tamamı dolu. Yeni hazırlık yapılamaz.'
        : 'Son bir hazırlık alanı kaldı. Lütfen boşalan alanları bildirin.';

    final notification = WarehouseNotification(
      id: eventId,
      type: WarehouseNotificationType.capacity,
      title: 'Hazırlık Kapasite Uyarısı',
      message: message,
      timestamp: DateTime.now(),
      targetStage: AppStage.preparationAreas,
    );

    _addNotification(notification);
    _playAlert(
      eventId: eventId,
      soundType: SystemSoundType.alert,
      hapticAction: HapticFeedback.heavyImpact,
    );
  }

  void clearPreparationCapacityAlert() {
    _seenEventIds.remove('prep-capacity-full');
    _seenEventIds.remove('prep-capacity-one-left');
  }

  /// 3. Store location capacity warning (defined-capacity location is full).
  void notifyStoreCapacity({
    required String locationId,
    required String locationName,
    required int unitCount,
    required int capacity,
  }) {
    final eventId = 'store-capacity-full-$locationId-$unitCount';
    if (_seenEventIds.contains(eventId)) return;
    _seenEventIds.add(eventId);

    final notification = WarehouseNotification(
      id: eventId,
      type: WarehouseNotificationType.capacity,
      title: 'Depo Kapasite Uyarısı: $locationName',
      message:
          '$locationName alanı kapasitesine ulaştı ($unitCount/$capacity). Lütfen boşaltınız.',
      timestamp: DateTime.now(),
      targetStage: AppStage.finishedGoodsStore,
      locationName: locationName,
    );

    _addNotification(notification);
    _playAlert(
      eventId: eventId,
      soundType: SystemSoundType.alert,
      hapticAction: HapticFeedback.heavyImpact,
    );
  }

  void _addNotification(WarehouseNotification notification) {
    _notifications.insert(0, notification);
    if (_notifications.length > maxNotificationHistory) {
      _notifications.removeLast();
    }
    _activeBanner = notification;
    _bannerDismissTimer?.cancel();
    _bannerDismissTimer = Timer(bannerDisplayDuration, () {
      if (!_disposed && _activeBanner?.id == notification.id) {
        _activeBanner = null;
        _notify();
      }
    });
    _notify();
  }

  void _playAlert({
    required String eventId,
    required SystemSoundType soundType,
    required Future<void> Function() hapticAction,
  }) {
    if (!_soundEnabled) return;
    if (_soundPlayedEventIds.contains(eventId)) return;
    _soundPlayedEventIds.add(eventId);

    unawaited(SystemSound.play(soundType));
    unawaited(hapticAction());
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _bannerDismissTimer?.cancel();
    _bannerDismissTimer = null;
    super.dispose();
  }
}
