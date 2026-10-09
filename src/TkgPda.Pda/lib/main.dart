import 'package:flutter/material.dart';

import 'app/app.dart';
import 'app/app_controller.dart';
import 'core/auth/session_memory.dart';
import 'core/config/app_config.dart';
import 'core/config/runtime_config_loader.dart';
import 'core/network/api_client.dart';
import 'core/platform/platform_info.dart';
import 'features/warehouse/internal_notification_client_provider.dart';
import 'core/storage/device_code_store_factory.dart';
import 'data/repositories/pda_repository.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final platformInfo = createPlatformInfo();
  final runtimeConfig = await createRuntimeConfigLoader().load();
  final appConfig = AppConfig.resolve(
    platformInfo: platformInfo,
    runtimeApiBaseUrl: runtimeConfig.apiBaseUrl,
  );
  final session = SessionMemory();
  late final AppController controller;
  final api = ApiClient(
    baseUri: appConfig.apiBaseUri,
    deviceCode: () => controller.deviceCode,
    accessToken: () => session.accessToken,
    onUnauthorized: () => controller.handleUnauthorized(),
  );
  controller = AppController(
    repository: PdaRepository(api),
    deviceCodeStore: createDeviceCodeStore(),
    session: session,
    notificationClient: createInternalNotificationClient(
      baseUrl: appConfig.apiBaseUri.toString(),
    ),
  );
  await controller.initialize();
  runApp(TkgPdaApp(controller: controller));
}
