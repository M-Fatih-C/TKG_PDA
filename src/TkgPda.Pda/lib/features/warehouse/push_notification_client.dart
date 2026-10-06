import 'push_notification_client_stub.dart'
    if (dart.library.io) 'push_notification_client_io.dart'
    as implementation;

class PushForegroundMessage {
  const PushForegroundMessage({
    required this.notificationType,
    this.unitId,
    this.locationName,
    this.partCode,
    this.machineCode,
    this.requestNo,
  });

  final String? notificationType;
  final String? unitId;
  final String? locationName;
  final String? partCode;
  final String? machineCode;
  final int? requestNo;
}

abstract class PushNotificationClient {
  bool get isConfigured;
  Future<void> start();
  Future<bool> ensurePermission();
  Future<String?> currentToken();
  Stream<String> get tokenRefreshes;
  Stream<PushForegroundMessage> get foregroundMessages;
  Future<void> dispose();
}

class DisabledPushNotificationClient implements PushNotificationClient {
  const DisabledPushNotificationClient();

  @override
  bool get isConfigured => false;

  @override
  Future<void> start() async {}

  @override
  Future<bool> ensurePermission() async => false;

  @override
  Future<String?> currentToken() async => null;

  @override
  Stream<String> get tokenRefreshes => const Stream.empty();

  @override
  Stream<PushForegroundMessage> get foregroundMessages => const Stream.empty();

  @override
  Future<void> dispose() async {}
}

PushNotificationClient createPushNotificationClient() =>
    implementation.createPushNotificationClient();
