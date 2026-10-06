import 'dart:io' show Platform;

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';

import 'push_notification_client.dart';

PushNotificationClient createPushNotificationClient() {
  if (!Platform.isAndroid) return const DisabledPushNotificationClient();
  final options = tkgFirebaseOptionsFromEnvironment();
  if (options == null) return const DisabledPushNotificationClient();
  return AndroidFirebasePushClient(options);
}

FirebaseOptions? tkgFirebaseOptionsFromEnvironment() {
  const apiKey = String.fromEnvironment('TKG_FIREBASE_API_KEY');
  const appId = String.fromEnvironment('TKG_FIREBASE_APP_ID');
  const senderId = String.fromEnvironment('TKG_FIREBASE_MESSAGING_SENDER_ID');
  const projectId = String.fromEnvironment('TKG_FIREBASE_PROJECT_ID');
  if (apiKey.isEmpty ||
      appId.isEmpty ||
      senderId.isEmpty ||
      projectId.isEmpty) {
    return null;
  }
  return FirebaseOptions(
    apiKey: apiKey,
    appId: appId,
    messagingSenderId: senderId,
    projectId: projectId,
  );
}

@pragma('vm:entry-point')
Future<void> tkgFirebaseMessagingBackgroundHandler(RemoteMessage message) async {
  final options = tkgFirebaseOptionsFromEnvironment();
  if (options == null) return;
  if (Firebase.apps.isEmpty) {
    await Firebase.initializeApp(options: options);
  }
}

class AndroidFirebasePushClient implements PushNotificationClient {
  AndroidFirebasePushClient(this._options);

  final FirebaseOptions _options;
  bool _started = false;

  @override
  bool get isConfigured => true;

  @override
  Future<void> start() async {
    if (_started) return;
    _started = true;
    if (Firebase.apps.isEmpty) {
      await Firebase.initializeApp(options: _options);
    }
    FirebaseMessaging.onBackgroundMessage(tkgFirebaseMessagingBackgroundHandler);
  }

  @override
  Future<bool> ensurePermission() async {
    final settings = await FirebaseMessaging.instance.requestPermission(
      alert: true,
      badge: true,
      sound: true,
    );
    return settings.authorizationStatus == AuthorizationStatus.authorized ||
        settings.authorizationStatus == AuthorizationStatus.provisional;
  }

  @override
  Future<String?> currentToken() => FirebaseMessaging.instance.getToken();

  @override
  Stream<String> get tokenRefreshes => FirebaseMessaging.instance.onTokenRefresh;

  @override
  Stream<PushForegroundMessage> get foregroundMessages =>
      FirebaseMessaging.onMessage.map(_fromRemote);

  @override
  Future<void> dispose() async {}

  static PushForegroundMessage _fromRemote(RemoteMessage message) {
    final data = message.data;
    return PushForegroundMessage(
      notificationType: data['notificationType'],
      unitId: data['unitId'],
      locationName: data['locationName'],
      partCode: data['partCode'],
      machineCode: data['machineCode'],
      requestNo: int.tryParse(data['requestNo'] ?? ''),
    );
  }
}
