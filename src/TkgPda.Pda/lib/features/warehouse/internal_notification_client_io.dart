import 'dart:io' show Platform;

import 'package:flutter/services.dart';

import 'internal_notification_client.dart';

class AndroidInternalNotificationClient implements InternalNotificationClient {
  AndroidInternalNotificationClient({required this.baseUrl});

  static const _channel = MethodChannel(
    'com.logirobotic.tkg_pda/device_notifications',
  );
  static const _events = EventChannel(
    'com.logirobotic.tkg_pda/device_notifications_events',
  );

  final String baseUrl;
  var _listening = false;

  @override
  bool get isSupported => Platform.isAndroid;

  @override
  void Function(Map<String, String> data)? onDelivery;

  @override
  Future<void> start({
    required String credential,
    required String registrationId,
  }) async {
    if (!isSupported) return;
    if (!_listening) {
      _listening = true;
      _events.receiveBroadcastStream().listen((event) {
        if (event is Map) {
          onDelivery?.call({
            for (final entry in event.entries)
              entry.key.toString(): entry.value?.toString() ?? '',
          });
        }
      });
    }
    await _channel.invokeMethod<void>('start', {
      'credential': credential,
      'baseUrl': baseUrl,
      'registrationId': registrationId,
    });
  }

  @override
  Future<void> stop() async {
    if (!isSupported) return;
    await _channel.invokeMethod<void>('stop');
  }
}

InternalNotificationClient createInternalNotificationClient({
  String baseUrl = '',
}) => AndroidInternalNotificationClient(baseUrl: baseUrl);
