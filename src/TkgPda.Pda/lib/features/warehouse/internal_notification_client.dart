/// Platform boundary for the warehouse notification service.
///
/// Android starts a native foreground service. Web does nothing. The service
/// uses a dedicated credential and does not depend on the field-session lease.
library;

abstract class InternalNotificationClient {
  bool get isSupported;

  void Function(Map<String, String> data)? onDelivery;

  Future<void> start({
    required String credential,
    required String registrationId,
  });

  Future<void> stop();
}
