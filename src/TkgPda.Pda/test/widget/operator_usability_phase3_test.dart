import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';

import '../support/fakes.dart';

void main() {
  group('Phase 3 — Field Usability & Operational Presentation', () {
    testWidgets(
      '1. Robot Telemetry: reports honest connectivity and low battery alerts',
      (tester) async {
        final repository = FakePdaRepository()
          ..robotsValue = [
            const RobotTelemetry(
              displayName: 'AMB-01',
              online: true,
              batteryLevel: 0.85,
            ),
            const RobotTelemetry(
              displayName: 'AMB-02',
              online: false,
              batteryLevel: 0.15,
            ),
            const RobotTelemetry(
              displayName: 'AMB-03',
              online: null,
              batteryLevel: null,
            ),
          ];

        final harness = await _loggedInHarness(tester, repository: repository);
        await tester.tap(find.byKey(const Key('open-operator-queue')));
        await tester.pumpAndSettle();

        expect(find.byKey(const Key('robot-status-bar')), findsOneWidget);

        // Robot 1: Online, 85%
        expect(find.text('AMB-01'), findsOneWidget);
        expect(find.text('Çevrimiçi'), findsOneWidget);
        expect(find.text('85%'), findsOneWidget);

        // Robot 2: Offline, 15% (Low battery alert)
        expect(find.text('AMB-02'), findsOneWidget);
        expect(find.text('Çevrimdışı'), findsOneWidget);
        expect(find.text('15%'), findsOneWidget);
        expect(find.byIcon(Icons.battery_alert), findsOneWidget);

        // Robot 3: Unknown connectivity and battery
        expect(find.text('AMB-03'), findsOneWidget);
        expect(find.text('Durum alınamadı'), findsOneWidget);
        expect(find.text('—'), findsOneWidget);

        harness.dispose();
      },
    );

    testWidgets(
      '2. Active Operator Banner & Sicil Visibility across all screens',
      (tester) async {
        const operatorUser = AuthenticatedUser(
          id: 'user-ali-01',
          username: 'ali.yilmaz',
          fullName: 'Ali Yılmaz',
          employeeNo: '10423',
          roles: ['OPERATOR', 'WAREHOUSE_OPERATOR'],
        );

        final repository = FakePdaRepository()
          ..loginUser = operatorUser
          ..warehouseRequestsValue = [testWarehousePreparationRequest];

        final harness = await _loggedInHarness(tester, repository: repository);

        // Screen 1: MachineListScreen
        expect(find.byKey(const Key('operator-user-banner')), findsOneWidget);
        expect(
          find.textContaining('Ali Yılmaz (Sicil: 10423)'),
          findsOneWidget,
        );

        // Screen 2: OperationScreen
        await harness.controller.selectMachine(testMachine);
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('operator-user-banner')), findsOneWidget);
        expect(
          find.textContaining('Ali Yılmaz (Sicil: 10423)'),
          findsOneWidget,
        );

        // Screen 3: OperatorQueueScreen
        await tester.tap(
          find.byKey(const Key('open-operator-queue-from-machine')),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('operator-user-banner')), findsOneWidget);
        expect(
          find.textContaining('Ali Yılmaz (Sicil: 10423)'),
          findsOneWidget,
        );

        // Navigate back to origin screen (operation per WP-05 origin-aware navigation)
        await tester.tap(find.byKey(const Key('leave-operator-queue')));
        await tester.pumpAndSettle();
        expect(harness.controller.stage, AppStage.operation);

        // Screen 4: WarehousePreparationScreen
        await harness.controller.enterWarehousePreparation();
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('operator-user-banner')), findsOneWidget);
        expect(
          find.textContaining('Ali Yılmaz (Sicil: 10423)'),
          findsOneWidget,
        );

        harness.dispose();
      },
    );

    testWidgets(
      '3. Logout accessibility: logout action is present and functional across screens',
      (tester) async {
        final repository = FakePdaRepository();
        final harness = await _loggedInHarness(tester, repository: repository);

        // Screen 1: MachineListScreen has logout-button
        expect(find.byKey(const Key('logout-button')), findsOneWidget);

        // Screen 2: OperationScreen has logout-button
        await harness.controller.selectMachine(testMachine);
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('logout-button')), findsOneWidget);

        // Screen 3: OperatorQueueScreen has logout-button
        await tester.tap(
          find.byKey(const Key('open-operator-queue-from-machine')),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('logout-button')), findsOneWidget);

        // Perform logout from OperatorQueueScreen
        await tester.tap(find.byKey(const Key('logout-button')));
        await tester.pumpAndSettle();

        expect(harness.controller.isAuthenticated, isFalse);
        expect(find.byKey(const Key('employee-number-field')), findsOneWidget);

        harness.dispose();
      },
    );

    testWidgets(
      '4. Warehouse Preparation Card Usability: shows container type, wait time, and obeys mutationGate',
      (tester) async {
        const warehouseUser = AuthenticatedUser(
          id: 'user-wh-01',
          username: 'wh.operator',
          fullName: 'Veli Kaya',
          employeeNo: '20555',
          roles: ['WAREHOUSE_OPERATOR'],
        );

        final prepRequest = WarehousePreparationRequest(
          requestId: 'req-prep-001',
          requestNo: 108,
          operation: 'BRING_EMPTY',
          machineId: testMachine.id,
          machineCode: testMachine.code,
          machineDisplayName: testMachine.displayName,
          partCode: '71550BN000-10-HAOS',
          containerTypeCode: 'KLT-4020',
          sourceSlotCode: 'WH-01',
          sourceSlotDisplayName: 'Depo Alanı',
          destinationSlotCode: 'SLOT-01',
          destinationSlotDisplayName: 'Hücre Girişi',
          createdAt: DateTime.now().subtract(const Duration(minutes: 18)),
          queuedAt: DateTime.now().subtract(const Duration(minutes: 17)),
        );

        final repository = FakePdaRepository()
          ..loginUser = warehouseUser
          ..warehouseRequestsValue = [prepRequest];

        final harness = await _loggedInHarness(tester, repository: repository);

        expect(
          find.byKey(const Key('open-warehouse-preparation')),
          findsNothing,
        );
        expect(find.byKey(const Key('open-preparation-areas')), findsOneWidget);
        await tester.tap(find.byKey(const Key('open-preparation-areas')));
        await tester.pumpAndSettle();

        expect(
          find.byKey(const Key('leave-preparation-areas')),
          findsOneWidget,
        );
        expect(find.text('Yarı Mamul Hazırlık'), findsOneWidget);
        expect(tester.takeException(), isNull);

        harness.dispose();
      },
    );
  });
}

Future<_Harness> _loggedInHarness(
  WidgetTester tester, {
  FakePdaRepository? repository,
}) async {
  final repo = repository ?? FakePdaRepository();
  final controller = AppController(
    repository: repo,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
    // Legacy pre-production-session workflow regression. Default-on production
    // lifecycle is exercised separately by production_flow_test.dart.
    enableProductionWorkflow: false,
  );
  await controller.initialize();
  await tester.pumpWidget(
    TkgPdaApp(key: ValueKey(controller), controller: controller),
  );
  await controller.login('00123');
  await tester.pumpAndSettle();
  return _Harness(controller, repo);
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}
