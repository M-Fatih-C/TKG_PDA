import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/features/warehouse/internal_notification_client.dart';
import 'package:tkg_pda/features/warehouse/internal_notification_client_stub.dart';
import 'package:tkg_pda/features/warehouse/store_coordinator.dart';

import '../support/fakes.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('web notification client is a no-op', () async {
    final client = WebInternalNotificationClient(baseUrl: 'http://localhost');
    expect(client.isSupported, isFalse);
    await client.start(credential: 'secret', registrationId: 'reg');
    await client.stop();
  });

  test('finished-goods login registers and starts the service', () async {
    final repo = _warehouseRepo(const ['FINISHED_GOODS_WAREHOUSE_OPERATOR']);
    final client = _RecordingNotificationClient();
    final controller = await _login(repo, client);

    expect(controller.stage, AppStage.finishedGoodsStore);
    expect(repo.notificationRegistrationCount, 1);
    expect(client.startCount, 1);
    expect(client.credential, 'test-credential');
  });

  test('warehouse, admin and supervisor roles start the service', () async {
    for (final role in const [
      'WAREHOUSE_OPERATOR',
      'ADMIN',
      'SUPERVISOR',
    ]) {
      final repo = _warehouseRepo([role]);
      final client = _RecordingNotificationClient();
      final controller = await _login(repo, client);
      expect(controller.stage, isNot(AppStage.login));
      expect(client.startCount, 1, reason: role);
    }
  });

  test('line operator and semi-finished operator do not start the service', () async {
    final line = FakePdaRepository();
    final lineClient = _RecordingNotificationClient();
    final lineController = await _login(line, lineClient);
    expect(lineController.stage, isNot(AppStage.finishedGoodsStore));
    expect(line.notificationRegistrationCount, 0);
    expect(lineClient.startCount, 0);

    final semi = _warehouseRepo(const ['SEMI_FINISHED_WAREHOUSE_OPERATOR']);
    final semiClient = _RecordingNotificationClient();
    final semiController = await _login(semi, semiClient);
    expect(semiController.canAccessFinishedGoodsWarehouse, isFalse);
    expect(semi.notificationRegistrationCount, 0);
    expect(semiClient.startCount, 0);
  });

  test('registration failure and a rejected native start do not fail login', () async {
    final failed = _warehouseRepo(const ['FINISHED_GOODS_WAREHOUSE_OPERATOR'])
      ..notificationRegistrationError = StateError('registration down');
    final failedClient = _RecordingNotificationClient();
    final failedController = await _login(failed, failedClient);
    expect(failedController.stage, AppStage.finishedGoodsStore);
    expect(failedClient.startCount, 0);

    final denied = _warehouseRepo(const ['WAREHOUSE_OPERATOR']);
    final deniedClient = _RecordingNotificationClient()..failStart = true;
    final deniedController = await _login(denied, deniedClient);
    expect(deniedController.stage, AppStage.warehouseHome);
    expect(deniedClient.startCount, 1);
  });

  test('explicit logout stops the service and session expiry does not', () async {
    final repo = _warehouseRepo(const ['FINISHED_GOODS_WAREHOUSE_OPERATOR']);
    final client = _RecordingNotificationClient();
    final controller = await _login(repo, client);

    controller.handleUnauthorized();
    expect(client.stopCount, 0);

    await controller.login('E-001');
    expect(client.startCount, 2);
    await controller.logout();
    expect(client.stopCount, 1);
    expect(repo.logoutCount, 1);
  });

  test('a foreground delivery uses the existing coordinator identity once', () async {
    final repo = _warehouseRepo(const ['FINISHED_GOODS_WAREHOUSE_OPERATOR']);
    final client = _RecordingNotificationClient();
    final controller = await _login(repo, client);

    client.onDelivery?.call({
      'notificationType': 'FINISHED_GOODS_DELIVERY',
      'unitId': 'unit-9',
      'locationName': 'Alan 4',
      'partCode': 'ABC-123',
      'machineCode': 'SH-01-A',
      'requestNo': '145',
    });
    client.onDelivery?.call({
      'notificationType': 'FINISHED_GOODS_DELIVERY',
      'unitId': 'unit-9',
      'locationName': 'Alan 4',
      'partCode': 'ABC-123',
      'requestNo': '145',
    });

    expect(controller.warehouseNotifications.unreadCount, 1);
    expect(
      controller.warehouseNotifications.notifications.single.id,
      'delivery-unit-9',
    );
  });

  test('store polling remains a six-second foreground refresh', () {
    expect(StoreCoordinator.pollInterval, const Duration(seconds: 6));
  });
}

FakePdaRepository _warehouseRepo(List<String> roles) {
  return FakePdaRepository()
    ..loginUser = AuthenticatedUser(
      id: testUser.id,
      username: 'warehouse',
      fullName: 'Depo',
      employeeNo: '1002',
      roles: roles,
    );
}

Future<AppController> _login(
  FakePdaRepository repository,
  InternalNotificationClient client,
) async {
  final controller = AppController(
    repository: repository,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
    enableProductionWorkflow: false,
    notificationClient: client,
  );
  addTearDown(controller.dispose);
  await controller.initialize();
  final ok = await controller.login('E-001');
  expect(ok, isTrue);
  return controller;
}

class _RecordingNotificationClient implements InternalNotificationClient {
  int startCount = 0;
  int stopCount = 0;
  String? credential;
  bool failStart = false;

  @override
  bool get isSupported => true;

  @override
  void Function(Map<String, String> data)? onDelivery;

  @override
  Future<void> start({
    required String credential,
    required String registrationId,
  }) async {
    startCount++;
    this.credential = credential;
    if (failStart) throw StateError('notifications denied');
  }

  @override
  Future<void> stop() async {
    stopCount++;
  }
}
