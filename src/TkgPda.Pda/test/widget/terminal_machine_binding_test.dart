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
    'Bound line operator logs in directly to operation screen, skipping machine picker',
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
        ..loginDevice = testDevice; // testDevice has machineId == testMachine.id

      final harness = await _harness(tester, repository: repo);

      // 1. Stage is operation directly, bound machine is selected
      expect(harness.controller.stage, AppStage.operation);
      expect(harness.controller.selectedMachine?.id, testMachine.id);

      // 2. Machine picker is never displayed
      expect(find.text('Makine Seçimi'), findsNothing);

      // 3. Operation screen for SH-01-A is shown
      expect(find.textContaining('SH-01-A'), findsWidgets);

      // 4. Test A: Bound line operator cannot see the leave-machine back button
      expect(find.byKey(const Key('leave-machine')), findsNothing);

      // 5. Test B: Direct controller.leaveMachine() call keeps stage on operation and machine intact
      harness.controller.leaveMachine();
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.operation);
      expect(harness.controller.selectedMachine?.id, testMachine.id);

      // 6. Operator cannot select a different machine
      final otherMachine = MachineSummary(
        id: '30000000-0000-0000-0000-000000000099',
        code: 'SH-02-B',
        displayName: 'SH-02-B Hücresi',
        displayOrder: 2,
        permissions: testMachine.permissions,
      );
      await harness.controller.selectMachine(otherMachine);
      expect(harness.controller.selectedMachine?.id, testMachine.id);
      expect(
        harness.controller.notice,
        'Bu terminal yalnızca tanımlı olduğu makine üzerinde işlem yapabilir.',
      );

      harness.dispose();
    },
  );

  testWidgets(
    'Test C — Exact machine match: when device.machineId is not in /machines response, fail-closed without selecting first machine',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'line-op-mismatch',
        fullName: 'Hat Operatörü',
        employeeNo: '1001',
        roles: const ['LINE_OPERATOR'],
      );
      final boundToMachineA = PdaDevice(
        id: '20000000-0000-0000-0000-000000000001',
        code: 'PDA-TERM-01',
        name: 'Terminal 01',
        type: 'PDA',
        machineId: '10000000-0000-0000-0000-000000000001', // MACHINE-A
      );
      final onlyMachineB = MachineSummary(
        id: '10000000-0000-0000-0000-000000000002', // MACHINE-B
        code: 'SH-01-B',
        displayName: 'SH-01-B Hücresi',
        displayOrder: 2,
        permissions: testMachine.permissions,
      );
      final repo = FakePdaRepository()
        ..loginUser = user
        ..loginDevice = boundToMachineA
        ..machineValues = [onlyMachineB]; // Only MACHINE-B returned from /machines!

      final harness = await _harness(tester, repository: repo);

      // 1. Stage remains login / fail-closed
      expect(harness.controller.stage, AppStage.login);
      expect(harness.controller.isAuthenticated, isFalse);

      // 2. MACHINE-B is NOT selected, selectedMachine is null
      expect(harness.controller.selectedMachine, isNull);

      // 3. Problem banner explains binding could not be verified
      expect(harness.controller.problem?.code, 'DEVICE_MACHINE_BINDING_REQUIRED');
      expect(harness.controller.problem?.detail, 'Terminalin bağlı olduğu makine sistemde bulunamadı veya doğrulanamadı.');
      expect(
        find.text('Bu terminal henüz bir makineye tanımlanmamış. Lütfen sistem yöneticisine başvurun.'),
        findsOneWidget,
      );

      harness.dispose();
    },
  );

  testWidgets(
    'Test D — Supervisor can navigate to operation screen, sees leave-machine button, and leaveMachine returns to machines stage',
    (tester) async {
      final supervisorUser = AuthenticatedUser(
        id: testUser.id,
        username: 'supervisor',
        fullName: 'Vardiya Amiri',
        employeeNo: '2001',
        roles: const ['SUPERVISOR'],
      );
      final repo = FakePdaRepository()
        ..loginUser = supervisorUser
        ..loginDevice = testDevice;

      final harness = await _harness(tester, repository: repo);

      // 1. Supervisor starts at machines stage
      expect(harness.controller.stage, AppStage.machines);

      // 2. Select machine to enter operation screen
      await harness.controller.selectMachine(testMachine);
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.operation);

      // 3. Supervisor sees the leave-machine back button
      expect(find.byKey(const Key('leave-machine')), findsOneWidget);

      // 4. Clicking leave-machine returns supervisor to machines stage
      await tester.tap(find.byKey(const Key('leave-machine')));
      await tester.pumpAndSettle();
      expect(harness.controller.stage, AppStage.machines);
      expect(harness.controller.selectedMachine, isNull);

      harness.dispose();
    },
  );

  testWidgets(
    'Unbound line operator fails closed with binding required problem and stays on login',
    (tester) async {
      final user = AuthenticatedUser(
        id: testUser.id,
        username: 'unbound-line-op',
        fullName: 'Bağlantısız Hat Operatörü',
        employeeNo: '1002',
        roles: const ['LINE_OPERATOR'],
      );
      final unboundDevice = PdaDevice(
        id: '20000000-0000-0000-0000-000000000099',
        code: 'PDA-UNBOUND-01',
        name: 'Unbound PDA',
        type: 'PDA',
        machineId: null,
      );
      final repo = FakePdaRepository()
        ..loginUser = user
        ..loginDevice = unboundDevice;

      final harness = await _harness(tester, repository: repo);

      // 1. Stage remains login
      expect(harness.controller.stage, AppStage.login);
      expect(harness.controller.isAuthenticated, isFalse);

      // 2. Machine picker is not shown
      expect(find.text('Makine Seçimi'), findsNothing);

      // 3. Problem banner displays clear binding-required message
      expect(
        find.text(
          'Bu terminal henüz bir makineye tanımlanmamış. Lütfen sistem yöneticisine başvurun.',
        ),
        findsOneWidget,
      );

      harness.dispose();
    },
  );

  testWidgets(
    'Unbound warehouse tablet (machineId == null) logs in successfully without machine requirement',
    (tester) async {
      final semiUser = AuthenticatedUser(
        id: testUser.id,
        username: 'semi-user',
        fullName: 'Yarı Mamul Operatörü',
        employeeNo: '1003',
        roles: const ['SEMI_FINISHED_WAREHOUSE_OPERATOR'],
      );
      final finishedUser = AuthenticatedUser(
        id: testUser.id,
        username: 'finished-user',
        fullName: 'Bitmiş Ürün Operatörü',
        employeeNo: '1004',
        roles: const ['FINISHED_GOODS_WAREHOUSE_OPERATOR'],
      );
      final legacyUser = AuthenticatedUser(
        id: testUser.id,
        username: 'legacy-wh',
        fullName: 'Depo Operatörü',
        employeeNo: '1005',
        roles: const ['WAREHOUSE_OPERATOR'],
      );
      final unboundTablet = PdaDevice(
        id: '20000000-0000-0000-0000-000000000088',
        code: 'TABLET-WH-01',
        name: 'Warehouse Tablet',
        type: 'PDA',
        machineId: null,
      );

      // A. Semi-finished warehouse tablet
      final repoSemi = FakePdaRepository()
        ..loginUser = semiUser
        ..loginDevice = unboundTablet;
      final harnessSemi = await _harness(tester, repository: repoSemi);
      expect(harnessSemi.controller.stage, AppStage.preparationAreas);
      expect(find.text('Yarı Mamul Hazırlık'), findsOneWidget);
      harnessSemi.dispose();

      // B. Finished goods warehouse tablet
      final repoFinished = FakePdaRepository()
        ..loginUser = finishedUser
        ..loginDevice = unboundTablet;
      final harnessFinished = await _harness(tester, repository: repoFinished);
      expect(harnessFinished.controller.stage, AppStage.finishedGoodsStore);
      expect(find.text('Bitmiş Ürün Deposu'), findsOneWidget);
      harnessFinished.dispose();

      // C. Legacy warehouse tablet
      final repoLegacy = FakePdaRepository()
        ..loginUser = legacyUser
        ..loginDevice = unboundTablet;
      final harnessLegacy = await _harness(tester, repository: repoLegacy);
      expect(harnessLegacy.controller.stage, AppStage.warehouseHome);
      expect(find.text('Depo'), findsOneWidget);
      harnessLegacy.dispose();
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
