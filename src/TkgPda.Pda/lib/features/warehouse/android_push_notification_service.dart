import 'dart:async';

import '../../data/repositories/pda_repository.dart';
import 'push_notification_client.dart';
import 'warehouse_notification_coordinator.dart';

/// Registers an Android FCM token after a warehouse login and feeds a
/// foreground finished-goods delivery into the existing notification
/// coordinator. Web and an unconfigured Android build do nothing.
class AndroidPushNotificationService {
  AndroidPushNotificationService({
    required PushNotificationClient client,
    required PdaDataSource repository,
    required WarehouseNotificationCoordinator notifications,
  }) : _client = client,
       _repository = repository,
       _notifications = notifications;

  static const warehouseRoles = {
    'ADMIN',
    'SUPERVISOR',
    'WAREHOUSE_OPERATOR',
    'SEMI_FINISHED_WAREHOUSE_OPERATOR',
    'FINISHED_GOODS_WAREHOUSE_OPERATOR',
  };

  final PushNotificationClient _client;
  final PdaDataSource _repository;
  final WarehouseNotificationCoordinator _notifications;
  StreamSubscription<String>? _tokens;
  StreamSubscription<PushForegroundMessage>? _messages;
  bool _started = false;
  bool _authenticated = false;

  Future<void> start() async {
    if (_started) return;
    _started = true;
    if (!_client.isConfigured) return;
    await _client.start();
    _tokens = _client.tokenRefreshes.listen((token) {
      unawaited(_register(refreshedToken: token));
    });
    _messages = _client.foregroundMessages.listen(_onForeground);
  }

  Future<void> onAuthenticated(Iterable<String> roles) async {
    _authenticated = roles.any(warehouseRoles.contains);
    if (!_authenticated) return;
    await _register();
  }

  void onLoggedOut() {
    _authenticated = false;
  }

  Future<void> dispose() async {
    await _tokens?.cancel();
    await _messages?.cancel();
    await _client.dispose();
  }

  Future<void> _register({String? refreshedToken}) async {
    if (!_authenticated || !_client.isConfigured) return;
    try {
      final allowed = await _client.ensurePermission();
      if (!_authenticated || !allowed) return;
      final token = (refreshedToken ?? await _client.currentToken())?.trim();
      if (!_authenticated || token == null || token.isEmpty) return;
      await _repository.registerPushToken(token);
    } catch (_) {
      // A failed registration must not block warehouse use.
    }
  }

  void _onForeground(PushForegroundMessage message) {
    if (message.notificationType != 'FINISHED_GOODS_DELIVERY') return;
    final unitId = message.unitId;
    if (unitId == null || unitId.isEmpty) return;
    _notifications.notifyDelivery(
      unitId: unitId,
      locationName: message.locationName ?? 'Bitmiş ürün alanı',
      partCode: message.partCode ?? 'Parça',
      machineCode: message.machineCode,
      requestNo: message.requestNo,
    );
  }
}
