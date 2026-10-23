import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';

import '../support/fakes.dart';

void main() {
  group('WP-05 — Global Task Visibility & Machine Cancellation Scope', () {
    testWidgets(
      'Section 36: Global task screen contains no cancel button even for cancelable items',
      (tester) async {
        const testRequestId = 'req-glob-101';
        final repository = FakePdaRepository()
          ..operatorQueueValue = OperatorQueuePage(
            terminal: false,
            offset: 0,
            limit: 50,
            hasMore: false,
            items: [
              OperatorQueueItem(
                queuePosition: 1,
                machine: 'SH-01-A · Hücre',
                part: '71550BN000-10-HAOS',
                logicalContainerNumber: 1,
                businessAction: 'Boş kasa getiriliyor',
                requestedAt: DateTime.utc(2026, 10, 2, 10, 0),
                state: 'IN_PROGRESS',
                etaSeconds: 60,
                etaState: 'AVAILABLE',
                requestId: testRequestId,
              ),
            ],
          );

        final harness = await _setupHarness(tester, repository: repository);
        await harness.controller.enterTasks();
        await tester.pumpAndSettle();

        expect(find.byKey(const Key('cancel-queue-req-glob-101')), findsNothing);
        expect(find.text('Görevi İptal Et'), findsNothing);
        expect(find.text('İptal Et'), findsNothing);
        expect(repository.cancelCount, 0);

        harness.dispose();
      },
    );

    testWidgets(
      'Section 37: OperationScreen Aktif Taşımalar has cancel button and calls machine-scoped repository cancel',
      (tester) async {
        const testRequestId = 'req-active-202';
        final activeReq = ActiveTransportRequest.fromJson({
          'requestId': testRequestId,
          'requestNo': 202,
          'operation': 'BRING_EMPTY',
          'status': 'QUEUED',
          'requestedBy': testUser.id,
          'requestedByUsername': testUser.username,
          'isMine': true,
          'priority': 0,
          'createdAt': '2026-10-02T10:15:00Z',
          'canCancel': true,
          'execution': {'state': 'WAITING_FOR_ROBOT'},
        });

        final repository = FakePdaRepository()
          ..contextValue = testContext(activeRequests: [activeReq]);

        final harness = await _setupHarness(tester, repository: repository);
        expect(harness.controller.stage, AppStage.operation);
        expect(harness.controller.selectedMachine, isNotNull);

        // Cancel button should be visible in Aktif Taşımalar panel
        final cancelBtnFinder = find.byKey(const Key('cancel-req-active-202'));
        expect(cancelBtnFinder, findsOneWidget);

        // Tap cancel button to open confirmation dialog
        await tester.tap(cancelBtnFinder);
        await tester.pumpAndSettle();

        expect(find.text('Talebi iptal et'), findsOneWidget);
        expect(find.byKey(const Key('confirm-cancel')), findsOneWidget);

        // Confirm cancellation
        await tester.tap(find.byKey(const Key('confirm-cancel')));
        await tester.pumpAndSettle();

        // Repository must have received machine-scoped call with selectedMachine.id and requestId
        expect(repository.cancelCount, 1);
        expect(repository.lastCancelMachineId, testMachine.id);
        expect(repository.lastCancelRequestId, testRequestId);

        harness.dispose();
      },
    );

    testWidgets(
      'Section 38: Bound LINE_OPERATOR returns from Tasks to Operation screen (not machine selection)',
      (tester) async {
        final repository = FakePdaRepository();
        final harness = await _setupHarness(tester, repository: repository);

        // Bound line operator is on OperationScreen
        expect(harness.controller.stage, AppStage.operation);
        final initialMachine = harness.controller.selectedMachine;
        expect(initialMachine, isNotNull);

        // Navigate to global tasks
        await harness.controller.enterTasks();
        await tester.pumpAndSettle();
        expect(harness.controller.stage, AppStage.tasks);

        // Tap Back on tasks screen
        await tester.tap(find.byKey(const Key('leave-operator-queue')));
        await tester.pumpAndSettle();

        // Must return to Operation screen with selected machine unchanged
        expect(harness.controller.stage, AppStage.operation);
        expect(harness.controller.selectedMachine?.id, initialMachine!.id);
        expect(find.byKey(const Key('leave-machine')), findsNothing); // Bound terminal operator cannot leave machine

        harness.dispose();
      },
    );

    testWidgets(
      'Section 39: Warehouse operator can open global tasks and back navigation returns to warehouse origin',
      (tester) async {
        const warehouseUser = AuthenticatedUser(
          id: 'user-wh-semi',
          username: 'wh.semi',
          fullName: 'Semi Warehouse Op',
          employeeNo: '55001',
          roles: ['SEMI_FINISHED_WAREHOUSE_OPERATOR'],
        );

        final repository = FakePdaRepository()
          ..loginUser = warehouseUser
          ..warehouseRequestsValue = [testWarehousePreparationRequest];

        final harness = await _setupHarness(
          tester,
          repository: repository,
          user: warehouseUser,
          initialStage: AppStage.preparationAreas,
        );

        // 1. PreparationAreasScreen has open-global-tasks button in AppBar
        expect(find.byKey(const Key('open-global-tasks')), findsOneWidget);

        // Tap open-global-tasks
        await tester.tap(find.byKey(const Key('open-global-tasks')));
        await tester.pumpAndSettle();

        expect(harness.controller.stage, AppStage.tasks);
        expect(find.text('Görevler'), findsOneWidget);
        expect(find.byKey(const Key('confirm-cancel-queue')), findsNothing);

        // Tap Back on tasks screen
        await tester.tap(find.byKey(const Key('leave-operator-queue')));
        await tester.pumpAndSettle();

        // Must return to PreparationAreasScreen
        expect(harness.controller.stage, AppStage.preparationAreas);

        harness.dispose();
      },
    );

    testWidgets(
      'Section 39B: Finished goods warehouse operator can open global tasks and return to store screen',
      (tester) async {
        const warehouseUser = AuthenticatedUser(
          id: 'user-wh-fg',
          username: 'wh.fg',
          fullName: 'Finished Goods Op',
          employeeNo: '55002',
          roles: ['FINISHED_GOODS_WAREHOUSE_OPERATOR'],
        );

        final repository = FakePdaRepository()
          ..loginUser = warehouseUser;

        final harness = await _setupHarness(
          tester,
          repository: repository,
          user: warehouseUser,
          initialStage: AppStage.finishedGoodsStore,
        );

        // StoreScreen has open-global-tasks button
        expect(find.byKey(const Key('open-global-tasks')), findsOneWidget);

        await tester.tap(find.byKey(const Key('open-global-tasks')));
        await tester.pumpAndSettle();

        expect(harness.controller.stage, AppStage.tasks);

        // Tap Back
        await tester.tap(find.byKey(const Key('leave-operator-queue')));
        await tester.pumpAndSettle();

        // Must return to FinishedGoodsStore
        expect(harness.controller.stage, AppStage.finishedGoodsStore);

        harness.dispose();
      },
    );
  });
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}

Future<_Harness> _setupHarness(
  WidgetTester tester, {
  required FakePdaRepository repository,
  AuthenticatedUser? user,
  AppStage? initialStage,
}) async {
  tester.view.physicalSize = const Size(1280, 800);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);

  final controller = AppController(
    repository: repository,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
    enableProductionWorkflow: false,
  );
  await controller.initialize();
  await tester.pumpWidget(
    TkgPdaApp(key: ValueKey(controller), controller: controller),
  );

  await controller.login(user?.employeeNo ?? '00123');
  await tester.pumpAndSettle();

  if (initialStage != null && controller.stage != initialStage) {
    if (initialStage == AppStage.preparationAreas) {
      controller.enterPreparationAreas();
    } else if (initialStage == AppStage.finishedGoodsStore) {
      controller.enterFinishedGoodsStore();
    }
    await tester.pumpAndSettle();
  }

  return _Harness(controller, repository);
}
