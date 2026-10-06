import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/features/warehouse/android_push_notification_service.dart';
import 'package:tkg_pda/features/warehouse/push_notification_client.dart';
import 'package:tkg_pda/features/warehouse/store_coordinator.dart';
import 'package:tkg_pda/features/warehouse/store_screen.dart';
import 'package:tkg_pda/features/warehouse/warehouse_notification_coordinator.dart';

import '../support/fakes.dart';

void main() {
  test('warehouse login registers the Android token when push is configured', () async {
    final repository = FakePdaRepository()
      ..loginUser = _user(['FINISHED_GOODS_WAREHOUSE_OPERATOR']);
    final client = _FakePushClient();
    final controller = _controller(repository, client);
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    expect(repository.pushTokens, ['token-1']);
  });

  test('a line operator does not register warehouse push', () async {
    final repository = FakePdaRepository()
      ..loginUser = _user(['LINE_OPERATOR']);
    final client = _FakePushClient();
    final controller = _controller(repository, client);
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    expect(repository.pushTokens, isEmpty);
  });

  test('notification permission denial does not block login', () async {
    final repository = FakePdaRepository()
      ..loginUser = _user(['WAREHOUSE_OPERATOR']);
    final client = _FakePushClient()..permissionGranted = false;
    final controller = _controller(repository, client);
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    expect(controller.stage, AppStage.warehouseHome);
    expect(repository.pushTokens, isEmpty);
  });

  test('a failed registration does not fail login', () async {
    final repository = FakePdaRepository()
      ..loginUser = _user(['FINISHED_GOODS_WAREHOUSE_OPERATOR'])
      ..pushRegistrationError = StateError('push unavailable');
    final client = _FakePushClient();
    final controller = _controller(repository, client);
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    expect(repository.pushTokens, ['token-1']);
  });

  test('token refresh updates the registration while authenticated', () async {
    final repository = FakePdaRepository()
      ..loginUser = _user(['SEMI_FINISHED_WAREHOUSE_OPERATOR']);
    final client = _FakePushClient();
    final controller = _controller(repository, client);
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    client.tokens.add('token-2');
    await Future<void>.delayed(Duration.zero);
    expect(repository.pushTokens, ['token-1', 'token-2']);
  });

  test('explicit logout stops further registration', () async {
    final repository = FakePdaRepository()
      ..loginUser = _user(['FINISHED_GOODS_WAREHOUSE_OPERATOR']);
    final client = _FakePushClient();
    final controller = _controller(repository, client);
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    await controller.logout();
    client.tokens.add('token-3');
    await Future<void>.delayed(Duration.zero);

    expect(repository.logoutCount, 1);
    expect(repository.pushTokens, ['token-1']);
  });

  test('web and unconfigured clients do not initialize Firebase push', () async {
    expect(createPushNotificationClient().isConfigured, isFalse);
    final repository = FakePdaRepository()
      ..loginUser = _user(['FINISHED_GOODS_WAREHOUSE_OPERATOR']);
    final controller = _controller(repository, const DisabledPushNotificationClient());
    addTearDown(controller.dispose);

    await controller.initialize();
    expect(await controller.login('1001'), isTrue);
    expect(repository.pushTokens, isEmpty);
  });

  test('foreground delivery and a later poll share one notification', () async {
    final repository = FakePdaRepository();
    final notifications = WarehouseNotificationCoordinator();
    final client = _FakePushClient();
    final service = AndroidPushNotificationService(
      client: client,
      repository: repository,
      notifications: notifications,
    );
    final store = StoreCoordinator(
      repository,
      notificationCoordinator: notifications,
    );
    addTearDown(service.dispose);
    addTearDown(store.dispose);
    addTearDown(notifications.dispose);

    await service.start();
    await service.onAuthenticated({'FINISHED_GOODS_WAREHOUSE_OPERATOR'});
    repository.storeLocationsValue = [
      fakeStoreLocation(code: 'FG_AREA_04', displayName: 'Alan 4'),
    ];
    await store.refresh();

    client.foreground.add(
      const PushForegroundMessage(
        notificationType: 'FINISHED_GOODS_DELIVERY',
        unitId: 'unit-fg-1',
        locationName: 'Alan 4',
        partCode: 'ABC-123',
        machineCode: 'SH-01-A',
        requestNo: 145,
      ),
    );
    await Future<void>.delayed(Duration.zero);
    repository.storeLocationsValue = [
      fakeStoreLocation(
        code: 'FG_AREA_04',
        displayName: 'Alan 4',
        units: [fakeStoreUnit(id: 'unit-fg-1', partCode: 'ABC-123', requestNo: 145)],
      ),
    ];
    await store.refresh();

    final deliveries = notifications.notifications
        .where((item) => item.id == 'delivery-unit-fg-1')
        .toList();
    expect(deliveries, hasLength(1));
    expect(deliveries.single.type.name, 'delivery');
  });

  testWidgets('nineteen areas stay in code order with empty full and disabled states', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..storeLocationsValue = [
        for (var number = 1; number <= 19; number++)
          fakeStoreLocation(
            code: 'FG_AREA_${number.toString().padLeft(2, '0')}',
            displayName: 'Alan $number',
            units: number == 5
                ? [fakeStoreUnit(id: 'unit-fg-5', partCode: 'ABC-123', requestNo: 145)]
                : const [],
          ),
        fakeStoreLocation(code: 'FG_AREA_DISABLED', displayName: 'Kapalı', isEnabled: false),
      ];
    final coordinator = StoreCoordinator(repository);
    addTearDown(coordinator.dispose);
    await _pumpStore(tester, coordinator);

    final codes = tester
        .widgetList(
          find.byWidgetPredicate(
            (widget) =>
                widget.key is ValueKey<String> &&
                (widget.key! as ValueKey<String>).value.startsWith(
                  'store-location-FG_',
                ),
          ),
        )
        .map((widget) => (widget.key! as ValueKey<String>).value)
        .toList();
    expect(codes, [
      for (var number = 1; number <= 19; number++)
        'store-location-FG_AREA_${number.toString().padLeft(2, '0')}',
      'store-location-FG_AREA_DISABLED',
    ]);
    expect(find.text('Alan 1'), findsOneWidget);
    expect(find.text('FG_AREA_01'), findsOneWidget);
    expect(find.byKey(const Key('store-presence-FG_AREA_01')), findsOneWidget);
    expect(
      tester.widget<Text>(find.byKey(const Key('store-presence-FG_AREA_01'))).data,
      'BOŞ',
    );
    expect(
      tester.widget<Text>(find.byKey(const Key('store-presence-FG_AREA_05'))).data,
      'DOLU',
    );
    expect(find.text('ABC-123'), findsOneWidget);
    expect(find.textContaining('SH-01-A'), findsWidgets);
    expect(find.textContaining('Talep #145'), findsOneWidget);
    expect(find.text('unit-fg-5'), findsNothing);
    expect(
      tester.widget<Text>(find.byKey(const Key('store-presence-FG_AREA_DISABLED'))).data,
      'DEVRE DIŞI',
    );
  });

  testWidgets('the store does not invent its own location order', (tester) async {
    final repository = FakePdaRepository()
      ..storeLocationsValue = [
        fakeStoreLocation(code: 'FG_AREA_19', displayName: 'Alan 19'),
        fakeStoreLocation(code: 'FG_AREA_01', displayName: 'Alan 1'),
      ];
    final coordinator = StoreCoordinator(repository);
    addTearDown(coordinator.dispose);
    await _pumpStore(tester, coordinator);

    final codes = tester
        .widgetList(
          find.byWidgetPredicate(
            (widget) =>
                widget.key is ValueKey<String> &&
                (widget.key! as ValueKey<String>).value.startsWith(
                  'store-location-FG_',
                ),
          ),
        )
        .map((widget) => (widget.key! as ValueKey<String>).value)
        .toList();
    expect(codes, ['store-location-FG_AREA_19', 'store-location-FG_AREA_01']);
  });
}

AuthenticatedUser _user(List<String> roles) => AuthenticatedUser(
  id: 'user-1',
  username: 'warehouse',
  fullName: 'Depo',
  employeeNo: '1001',
  roles: roles,
);

AppController _controller(FakePdaRepository repository, PushNotificationClient client) =>
    AppController(
      repository: repository,
      deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
      session: SessionMemory(),
      enablePolling: false,
      enableProductionWorkflow: false,
      enableHeartbeat: false,
      pushClient: client,
    );

Future<void> _pumpStore(WidgetTester tester, StoreCoordinator coordinator) async {
  tester.view.physicalSize = const Size(900, 6000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    MaterialApp(home: StoreScreen(coordinator: coordinator, onBack: () {})),
  );
  await coordinator.refresh();
  await tester.pump();
}

class _FakePushClient implements PushNotificationClient {
  final tokens = StreamController<String>.broadcast();
  final foreground = StreamController<PushForegroundMessage>.broadcast();
  bool permissionGranted = true;
  String token = 'token-1';

  @override
  bool get isConfigured => true;

  @override
  Future<void> start() async {}

  @override
  Future<bool> ensurePermission() async => permissionGranted;

  @override
  Future<String?> currentToken() async => token;

  @override
  Stream<String> get tokenRefreshes => tokens.stream;

  @override
  Stream<PushForegroundMessage> get foregroundMessages => foreground.stream;

  @override
  Future<void> dispose() async {
    await tokens.close();
    await foreground.close();
  }
}
