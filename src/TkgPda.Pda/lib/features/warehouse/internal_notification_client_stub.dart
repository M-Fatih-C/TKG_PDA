import 'internal_notification_client.dart';

class WebInternalNotificationClient implements InternalNotificationClient {
  WebInternalNotificationClient({this.baseUrl = ''});

  final String baseUrl;
  @override
  bool get isSupported => false;

  @override
  void Function(Map<String, String> data)? onDelivery;

  @override
  Future<void> start({
    required String credential,
    required String registrationId,
  }) async {}

  @override
  Future<void> stop() async {}
}

InternalNotificationClient createInternalNotificationClient({
  String baseUrl = '',
}) => WebInternalNotificationClient(baseUrl: baseUrl);
