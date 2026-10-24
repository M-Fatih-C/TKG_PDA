import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';

import '../support/fakes.dart';

void main() {
  testWidgets(
    'Semi finished operator routes to preparation on login, cannot see finished goods tile',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'semi-operator',
        fullName: 'Yarı Mamul Operatörü',
        employeeNo: '1001',
        roles: const ['SEMI_FINISHED_WAREHOUSE_OPERATOR'],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(tester, repository: repo);

      // 1. Routes directly to preparation areas stage
      expect(harness.controller.stage, AppStage.preparationAreas);
      expect(harness.controller.canAccessSemiFinishedWarehouse, isTrue);
      expect(harness.controller.canAccessFinishedGoodsWarehouse, isFalse);

      // 2. Preparation screen is accessible
      expect(find.text('Yarı Mamul Hazırlık'), findsOneWidget);

      // 3. Leave preparation to warehouse home
      harness.controller.leavePreparationAreas();
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.warehouseHome);

      // 4. Warehouse home shows semi-finished tile, finished goods tile is not visible
      expect(find.byKey(const Key('open-preparation-areas')), findsOneWidget);
      expect(find.byKey(const Key('open-finished-goods')), findsNothing);

      harness.dispose();
    },
  );

  testWidgets(
    'Finished goods operator routes to store on login, cannot see semi finished tile',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'finished-operator',
        fullName: 'Bitmiş Ürün Operatörü',
        employeeNo: '1002',
        roles: const ['FINISHED_GOODS_WAREHOUSE_OPERATOR'],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(tester, repository: repo);

      // 1. Routes directly to finished goods store stage
      expect(harness.controller.stage, AppStage.finishedGoodsStore);
      expect(harness.controller.canAccessSemiFinishedWarehouse, isFalse);
      expect(harness.controller.canAccessFinishedGoodsWarehouse, isTrue);

      // 2. Store screen is accessible
      expect(find.text('Bitmiş Ürün Deposu'), findsOneWidget);

      // 3. Leave store to warehouse home
      harness.controller.leaveFinishedGoodsStore();
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.warehouseHome);

      // 4. Warehouse home shows finished goods tile, semi-finished tile is not visible
      expect(find.byKey(const Key('open-finished-goods')), findsOneWidget);
      expect(find.byKey(const Key('open-preparation-areas')), findsNothing);

      harness.dispose();
    },
  );

  testWidgets(
    'Operator with both warehouse roles routes to warehouse home and sees both tiles',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'both-operator',
        fullName: 'Çift Yetkili Depo Operatörü',
        employeeNo: '1003',
        roles: const [
          'SEMI_FINISHED_WAREHOUSE_OPERATOR',
          'FINISHED_GOODS_WAREHOUSE_OPERATOR',
        ],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(tester, repository: repo);

      // 1. Routes to warehouse home
      expect(harness.controller.stage, AppStage.warehouseHome);
      expect(harness.controller.canAccessSemiFinishedWarehouse, isTrue);
      expect(harness.controller.canAccessFinishedGoodsWarehouse, isTrue);

      // 2. Both tiles are visible
      expect(find.byKey(const Key('open-preparation-areas')), findsOneWidget);
      expect(find.byKey(const Key('open-finished-goods')), findsOneWidget);

      harness.dispose();
    },
  );

  testWidgets(
    'Legacy warehouse operator routes to warehouse home and can access both sections',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'legacy-operator',
        fullName: 'Eski Depo Operatörü',
        employeeNo: '1004',
        roles: const ['WAREHOUSE_OPERATOR'],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(tester, repository: repo);

      // 1. Routes to warehouse home
      expect(harness.controller.stage, AppStage.warehouseHome);
      expect(harness.controller.canAccessSemiFinishedWarehouse, isTrue);
      expect(harness.controller.canAccessFinishedGoodsWarehouse, isTrue);

      // 2. Both tiles are visible
      expect(find.byKey(const Key('open-preparation-areas')), findsOneWidget);
      expect(find.byKey(const Key('open-finished-goods')), findsOneWidget);

      harness.dispose();
    },
  );

  testWidgets(
    'Bound line operator routes directly to operation screen and has no warehouse capabilities',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'line-operator',
        fullName: 'Hat Operatörü',
        employeeNo: '1005',
        roles: const ['LINE_OPERATOR'],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(tester, repository: repo);

      // 1. Routes directly to operation screen (bound terminal)
      expect(harness.controller.stage, AppStage.operation);
      expect(harness.controller.selectedMachine?.id, testMachine.id);
      expect(harness.controller.canAccessSemiFinishedWarehouse, isFalse);
      expect(harness.controller.canAccessFinishedGoodsWarehouse, isFalse);
      expect(harness.controller.canAccessAnyWarehouse, isFalse);

      // 2. Operation screen is visible, machine list picker is skipped, no warehouse icons
      expect(find.text('Makine Seçimi'), findsNothing);
      expect(find.byKey(const Key('open-warehouse-home')), findsNothing);
      expect(find.byKey(const Key('open-warehouse-preparation')), findsNothing);

      harness.dispose();
    },
  );

  testWidgets(
    'Dedicated tablet TAB-YARI-MAMUL routes to preparation areas, shows banner and logout button',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'dev-wh-semi',
        fullName: 'Yarı Mamul Depo Operatörü',
        employeeNo: '2001',
        roles: const ['SEMI_FINISHED_WAREHOUSE_OPERATOR'],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(
        tester,
        repository: repo,
        deviceCode: 'TAB-YARI-MAMUL',
      );

      expect(harness.controller.stage, AppStage.preparationAreas);
      expect(find.text('Yarı Mamul Hazırlık'), findsOneWidget);
      expect(find.byKey(const Key('operator-user-banner')), findsOneWidget);
      expect(find.byKey(const Key('logout-button')), findsOneWidget);

      // Tap logout
      await tester.tap(find.byKey(const Key('logout-button')));
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.login);

      harness.dispose();
    },
  );

  testWidgets(
    'Dedicated tablet TAB-BITMIS-URUN routes to finished goods store, shows banner and logout button',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'dev-wh-finished',
        fullName: 'Bitmiş Ürün Depo Operatörü',
        employeeNo: '2002',
        roles: const ['FINISHED_GOODS_WAREHOUSE_OPERATOR'],
      );
      final repo = FakePdaRepository()..loginUser = user;
      final harness = await _harness(
        tester,
        repository: repo,
        deviceCode: 'TAB-BITMIS-URUN',
      );

      expect(harness.controller.stage, AppStage.finishedGoodsStore);
      expect(find.text('Bitmiş Ürün Deposu'), findsOneWidget);
      expect(find.byKey(const Key('operator-user-banner')), findsOneWidget);
      expect(find.byKey(const Key('logout-button')), findsOneWidget);

      // Tap logout
      await tester.tap(find.byKey(const Key('logout-button')));
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.login);

      harness.dispose();
    },
  );
}

Future<_Harness> _harness(
  WidgetTester tester, {
  FakePdaRepository? repository,
  String deviceCode = testDeviceCode,
}) async {
  tester.view.physicalSize = const Size(1280, 800);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final repo = repository ?? FakePdaRepository();
  final controller = AppController(
    repository: repo,
    deviceCodeStore: MemoryDeviceCodeStore(deviceCode),
    session: SessionMemory(),
    enablePolling: false,
    enableProductionWorkflow: false,
  );
  await controller.initialize();
  await tester.pumpWidget(
    TkgPdaApp(key: ValueKey(controller), controller: controller),
  );
  await controller.login('E-001');
  await tester.pumpAndSettle();
  return _Harness(controller, repo);
}

class _Harness {
  const _Harness(this.controller, this.repository);
  final AppController controller;
  final FakePdaRepository repository;
  void dispose() => controller.dispose();
}
