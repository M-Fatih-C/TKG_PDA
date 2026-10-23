import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';

import '../support/fakes.dart';

void main() {
  testWidgets(
    'Test 1: successful login starts heartbeat lifecycle',
    (tester) async {
      final repo = FakePdaRepository();
      final harness = await _harness(tester, repository: repo);

      expect(harness.controller.isHeartbeatActive, isTrue);
      harness.dispose();
    },
  );

  testWidgets(
    'Test 2: logout stops heartbeat, calls repository logout, and returns to login',
    (tester) async {
      final repo = FakePdaRepository();
      final harness = await _harness(tester, repository: repo);

      expect(harness.controller.isHeartbeatActive, isTrue);
      expect(repo.logoutCount, 0);

      await harness.controller.logout();
      await tester.pumpAndSettle();

      expect(harness.controller.isHeartbeatActive, isFalse);
      expect(repo.logoutCount, 1);
      expect(harness.controller.stage, AppStage.login);
      expect(harness.controller.isAuthenticated, isFalse);

      harness.dispose();
    },
  );

  testWidgets(
    'Test 3: 401 session expired cleans session via handleUnauthorized and stops heartbeat',
    (tester) async {
      final repo = FakePdaRepository();
      final harness = await _harness(tester, repository: repo);

      expect(harness.controller.isHeartbeatActive, isTrue);
      expect(harness.controller.isAuthenticated, isTrue);

      harness.controller.handleUnauthorized();
      await tester.pumpAndSettle();

      expect(harness.controller.isHeartbeatActive, isFalse);
      expect(harness.controller.isAuthenticated, isFalse);
      expect(harness.controller.stage, AppStage.login);
      expect(harness.controller.problem?.code, 'SESSION_EXPIRED');

      harness.dispose();
    },
  );

  testWidgets(
    'Test 4: ACTIVE_SESSION_EXISTS keeps user on login screen with clear operator Turkish message',
    (tester) async {
      final repo = FakePdaRepository()
        ..loginProblem = const ApiProblem(
          status: 409,
          code: 'ACTIVE_SESSION_EXISTS',
          detail: 'Bu sicil başka bir cihazda aktif olarak kullanılıyor.',
          correlationId: null,
        );

      tester.view.physicalSize = const Size(1280, 800);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      final controller = AppController(
        repository: repo,
        deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
        session: SessionMemory(),
        enablePolling: false,
        enableProductionWorkflow: false,
      );
      await controller.initialize();
      await tester.pumpWidget(
        TkgPdaApp(key: ValueKey(controller), controller: controller),
      );
      await tester.pumpAndSettle();

      // Attempt login
      final success = await controller.login('12345');
      await tester.pumpAndSettle();

      expect(success, isFalse);
      expect(controller.stage, AppStage.login);
      expect(controller.isHeartbeatActive, isFalse);
      expect(controller.problem?.code, 'ACTIVE_SESSION_EXISTS');
      expect(
        controller.problem?.operatorMessage,
        'Bu sicil başka bir cihazda aktif. Önce mevcut oturumdan çıkış yapın veya oturumun düşmesini bekleyin.',
      );

      // Verify that neither operation nor warehouse screens were opened
      expect(find.textContaining('SH-01-A'), findsNothing);
      expect(find.text('Depo'), findsNothing);
      expect(find.text('Yarı Mamul Hazırlık'), findsNothing);
      expect(find.text('Bitmiş Ürün Deposu'), findsNothing);

      controller.dispose();
    },
  );

  testWidgets(
    'Test 5: WP-03 bound machine direct routing is preserved',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'line-op',
        fullName: 'Hat Operatörü',
        employeeNo: '1001',
        roles: const ['LINE_OPERATOR'],
      );
      final repo = FakePdaRepository()
        ..loginUser = user
        ..loginDevice = testDevice;

      final harness = await _harness(tester, repository: repo);

      expect(harness.controller.stage, AppStage.operation);
      expect(harness.controller.selectedMachine?.id, testMachine.id);
      expect(harness.controller.isHeartbeatActive, isTrue);

      harness.dispose();
    },
  );

  testWidgets(
    'Test 6: WP-02 warehouse role routing is preserved',
    (tester) async {
      final semiFinishedUser = AuthenticatedUser(
        id: testUser.id,
        username: 'warehouse-semi',
        fullName: 'Yarı Mamul Operatörü',
        employeeNo: '1002',
        roles: const ['SEMI_FINISHED_WAREHOUSE_OPERATOR'],
      );
      final repo = FakePdaRepository()
        ..loginUser = semiFinishedUser
        ..loginDevice = const PdaDevice(
          id: 'dev-wh-1',
          code: 'TABLET-01',
          name: 'Warehouse Tablet',
          type: 'PDA',
          machineId: null,
        );

      final harness = await _harness(tester, repository: repo);

      expect(harness.controller.stage, AppStage.preparationAreas);
      expect(harness.controller.isHeartbeatActive, isTrue);

      harness.dispose();
    },
  );

  testWidgets(
    'Test D: unbound terminal login failure performs best-effort server logout and cleans local state',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'line-op',
        fullName: 'Hat Operatörü',
        employeeNo: '1001',
        roles: const ['LINE_OPERATOR'],
      );
      final repo = FakePdaRepository()
        ..loginUser = user
        ..loginDevice = const PdaDevice(
          id: 'dev-unbound',
          code: 'TERM-UNBOUND',
          name: 'Unbound Terminal',
          type: 'PDA',
          machineId: null,
        );

      final controller = AppController(
        repository: repo,
        deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
        session: SessionMemory(),
        enablePolling: false,
        enableProductionWorkflow: false,
        enableHeartbeat: true,
      );
      await controller.initialize();
      await tester.pumpWidget(
        TkgPdaApp(key: ValueKey(controller), controller: controller),
      );
      await tester.pumpAndSettle();

      final success = await controller.login('1001');
      await tester.pumpAndSettle();

      expect(success, isFalse);
      expect(repo.logoutCount, 1);
      expect(controller.isAuthenticated, isFalse);
      expect(controller.isHeartbeatActive, isFalse);
      expect(controller.stage, AppStage.login);
      expect(controller.problem?.code, 'DEVICE_MACHINE_BINDING_REQUIRED');

      controller.dispose();
    },
  );

  testWidgets(
    'Test E: exact machine mismatch performs best-effort server logout and cleans local state',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'line-op',
        fullName: 'Hat Operatörü',
        employeeNo: '1001',
        roles: const ['LINE_OPERATOR'],
      );
      final repo = FakePdaRepository()
        ..loginUser = user
        ..loginDevice = const PdaDevice(
          id: 'dev-1',
          code: 'TERM-01',
          name: 'Terminal 1',
          type: 'PDA',
          machineId: 'MACHINE-NON-EXISTENT',
        )
        ..machineValues = [testMachine];

      final controller = AppController(
        repository: repo,
        deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
        session: SessionMemory(),
        enablePolling: false,
        enableProductionWorkflow: false,
        enableHeartbeat: true,
      );
      await controller.initialize();
      await tester.pumpWidget(
        TkgPdaApp(key: ValueKey(controller), controller: controller),
      );
      await tester.pumpAndSettle();

      final success = await controller.login('1001');
      await tester.pumpAndSettle();

      expect(success, isFalse);
      expect(repo.logoutCount, 1);
      expect(controller.selectedMachine, isNull);
      expect(controller.isAuthenticated, isFalse);
      expect(controller.isHeartbeatActive, isFalse);
      expect(controller.stage, AppStage.login);

      controller.dispose();
    },
  );

  testWidgets(
    'Test F: post-login connectivity failure performs best-effort server logout and cleans local state',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'line-op',
        fullName: 'Hat Operatörü',
        employeeNo: '1001',
        roles: const ['LINE_OPERATOR'],
      );
      final repo = FakePdaRepository()
        ..loginUser = user
        ..loginDevice = testDevice
        ..machinesError = const ConnectivityException('Connection failed');

      final controller = AppController(
        repository: repo,
        deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
        session: SessionMemory(),
        enablePolling: false,
        enableProductionWorkflow: false,
        enableHeartbeat: true,
      );
      await controller.initialize();
      await tester.pumpWidget(
        TkgPdaApp(key: ValueKey(controller), controller: controller),
      );
      await tester.pumpAndSettle();

      final success = await controller.login('1001');
      await tester.pumpAndSettle();

      expect(success, isFalse);
      expect(repo.logoutCount, 1);
      expect(controller.isAuthenticated, isFalse);
      expect(controller.isHeartbeatActive, isFalse);
      expect(controller.offline, isTrue);

      controller.dispose();
    },
  );

  testWidgets(
    'Test G: beginDeviceCodeChange on authenticated user stops heartbeat and performs best-effort logout',
    (tester) async {
      final repo = FakePdaRepository();
      final harness = await _harness(tester, repository: repo);

      expect(harness.controller.isAuthenticated, isTrue);
      expect(harness.controller.isHeartbeatActive, isTrue);
      expect(repo.logoutCount, 0);

      await harness.controller.beginDeviceCodeChange();
      await tester.pumpAndSettle();

      expect(harness.controller.isHeartbeatActive, isFalse);
      expect(repo.logoutCount, 1);
      expect(harness.controller.isAuthenticated, isFalse);
      expect(harness.controller.stage, AppStage.deviceSetup);

      harness.dispose();
    },
  );
}

Future<_Harness> _harness(
  WidgetTester tester, {
  FakePdaRepository? repository,
}) async {
  tester.view.physicalSize = const Size(1280, 800);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final repo = repository ?? FakePdaRepository();
  final controller = AppController(
    repository: repo,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
    enableProductionWorkflow: false,
    enableHeartbeat: true,
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
