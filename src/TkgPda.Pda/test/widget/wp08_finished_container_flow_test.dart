import 'dart:async';

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
  testWidgets('1: Kasa Doldu submits immediately without a confirmation dialog', (
    tester,
  ) async {
    final harness = await _operationHarness(tester);

    expect(find.text('1. Kasa Doldu'), findsOneWidget);
    await tester.tap(find.text('1. Kasa Doldu'));
    await tester.pump(const Duration(milliseconds: 500));

    expect(harness.repository.containerActionCount, 1);
    expect(harness.repository.lastCommand?.operation, 'REMOVE_FULL');
    expect(harness.repository.lastCommand?.sourceSlotId, isNull);
    expect(harness.repository.lastCommand?.destinationSlotId, isNull);
    expect(find.byKey(const Key('confirm-direct-transport')), findsNothing);
    harness.dispose();
  });

  testWidgets('2: Kasa Boş submits immediately without a confirmation dialog', (
    tester,
  ) async {
    final harness = await _operationHarness(tester);

    expect(find.text('1. Kasa Boş'), findsOneWidget);
    await tester.tap(find.text('1. Kasa Boş'));
    await tester.pump(const Duration(milliseconds: 500));

    expect(harness.repository.containerActionCount, 1);
    expect(harness.repository.lastCommand?.operation, 'BRING_EMPTY');
    expect(harness.repository.lastCommand?.sourceSlotId, isNull);
    expect(harness.repository.lastCommand?.destinationSlotId, isNull);
    expect(find.byKey(const Key('confirm-direct-transport')), findsNothing);
    harness.dispose();
  });

  testWidgets('3: a backend-disabled Kasa Doldu cannot be pressed', (
    tester,
  ) async {
    const message =
        'Bitmiş ürün teslim alanı henüz yapılandırılmamış. Sistem yöneticisine başvurun.';
    final repository = FakePdaRepository()
      ..containerPositionsValue = [
        const ContainerPosition(
          positionNumber: 1,
          label: '1. Kasa',
          fullAction: ContainerActionAvailability(
            enabled: false,
            status: 'READY',
            disabledCode: 'FINISHED_GOODS_DESTINATION_NOT_CONFIGURED',
            disabledMessage: message,
          ),
          emptyAction: ContainerActionAvailability(
            enabled: true,
            status: 'READY',
            disabledCode: null,
            disabledMessage: null,
          ),
          transportState: 'READY',
          queuePosition: null,
          etaSeconds: null,
          etaState: 'UNAVAILABLE',
        ),
      ];
    final harness = await _operationHarness(tester, repository: repository);

    final button = tester.widget<ElevatedButton>(
      find.byKey(const Key('direct-action-REMOVE_FULL-1')),
    );
    expect(button.onPressed, isNull);
    expect(
      tester.widget<Tooltip>(find.byKey(const Key('container-1-FULL'))).message,
      message,
    );

    await tester.tap(find.byKey(const Key('direct-action-REMOVE_FULL-1')));
    await tester.pump();
    expect(repository.containerActionCount, 0);
    harness.dispose();
  });

  testWidgets('4: double tap while submitting makes a single client call', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..createCompleter = Completer<TransportResult>();
    final harness = await _operationHarness(tester, repository: repository);

    await tester.tap(find.text('1. Kasa Doldu'));
    await tester.pump();
    await tester.tap(find.byKey(const Key('direct-action-REMOVE_FULL-1')));
    await tester.pump();

    expect(repository.containerActionCount, 1);

    repository.createCompleter!.complete(
      const TransportResult(
        id: '90000000-0000-0000-0000-000000000001',
        requestNo: 101,
        status: 'QUEUED',
        operation: 'REMOVE_FULL',
        idempotentReplay: false,
      ),
    );
    await tester.pump(const Duration(milliseconds: 500));
    harness.dispose();
  });

  testWidgets('5: ambiguous Kasa Doldu retry reuses the same key', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..createError = const AmbiguousMutationException(
        idempotencyKey: 'ambiguous-kasa-doldu',
      );
    final harness = await _operationHarness(tester, repository: repository);

    await tester.tap(find.text('1. Kasa Doldu'));
    await tester.pump(const Duration(milliseconds: 500));
    final firstKey = repository.lastIdempotencyKey;
    expect(find.byKey(const Key('retry-ambiguous')), findsOneWidget);

    repository.createError = null;
    await tester.tap(find.byKey(const Key('retry-ambiguous')));
    await tester.pump(const Duration(milliseconds: 500));

    expect(repository.containerActionCount, 2);
    expect(repository.lastIdempotencyKey, firstKey);
    harness.dispose();
  });

  testWidgets('6: successful Kasa Doldu refreshes the active transport list', (
    tester,
  ) async {
    final request = _activeRequest(
      id: 'wp08-full',
      operation: 'REMOVE_FULL',
      readinessState: 'NOT_REQUIRED',
    );
    final repository = FakePdaRepository()..containerActionActiveRequest = request;
    final harness = await _operationHarness(tester, repository: repository);

    await tester.tap(find.text('1. Kasa Doldu'));
    await tester.pump(const Duration(milliseconds: 500));

    expect(
      harness.controller.context!.activeTransportRequests.single.operation,
      'REMOVE_FULL',
    );
    await _scrollTo(tester, find.byKey(const Key('active-transport-section')));
    expect(find.byKey(const Key('active-transport-section')), findsOneWidget);
    expect(find.byKey(const Key('active-transport-wp08-full')), findsOneWidget);
    expect(find.textContaining('Dolu Kasayı Kaldır'), findsWidgets);
    expect(find.byType(LinearProgressIndicator), findsNothing);
    harness.dispose();
  });

  testWidgets('7: successful Kasa Boş shows backend WAITING_PREPARATION', (
    tester,
  ) async {
    final request = _activeRequest(
      id: 'wp08-empty',
      operation: 'BRING_EMPTY',
      readinessState: 'WAITING_PREPARATION',
    );
    final repository = FakePdaRepository()..containerActionActiveRequest = request;
    final harness = await _operationHarness(tester, repository: repository);

    await tester.tap(find.text('1. Kasa Boş'));
    await tester.pump(const Duration(milliseconds: 500));

    expect(
      harness.controller.context!.activeTransportRequests.single.readinessState,
      'WAITING_PREPARATION',
    );
    await _scrollTo(tester, find.byKey(const Key('active-transport-wp08-empty')));
    expect(find.byKey(const Key('active-transport-wp08-empty')), findsOneWidget);
    expect(find.byKey(const Key('active-readiness')), findsOneWidget);
    expect(find.text('Hazırlık: Depo hazırlığı bekleniyor'), findsOneWidget);
    expect(find.byType(LinearProgressIndicator), findsNothing);
    harness.dispose();
  });
}

ActiveTransportRequest _activeRequest({
  required String id,
  required String operation,
  required String readinessState,
}) => ActiveTransportRequest.fromJson({
  'requestId': id,
  'requestNo': 88,
  'operation': operation,
  'status': 'QUEUED',
  'requestedBy': testUser.id,
  'requestedByUsername': testUser.username,
  'isMine': true,
  'priority': 100,
  'createdAt': '2026-10-06T08:00:00Z',
  'partCode': '71550BN000-10-HAOS',
  'readinessState': readinessState,
  'queuePosition': 1,
  'ageSeconds': 4,
  'canCancel': true,
  'execution': {'state': 'WAITING_FOR_ROBOT'},
});

Future<_Harness> _operationHarness(
  WidgetTester tester, {
  FakePdaRepository? repository,
}) async {
  final harness = await _loggedInHarness(
    tester,
    repository ?? FakePdaRepository(),
  );
  await harness.controller.selectMachine(testMachine);
  await harness.controller.selectMainPartOption(testMainPartOption);
  await tester.pumpAndSettle();
  return harness;
}

Future<_Harness> _loggedInHarness(
  WidgetTester tester,
  FakePdaRepository repository,
) async {
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
  await controller.login('00123');
  await tester.pumpAndSettle();
  return _Harness(controller, repository);
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}

Future<void> _scrollTo(WidgetTester tester, Finder finder) async {
  if (finder.evaluate().isNotEmpty) {
    await tester.ensureVisible(finder);
    await tester.pump(const Duration(milliseconds: 300));
    return;
  }
  await tester.scrollUntilVisible(
    finder,
    350,
    scrollable: find.descendant(
      of: find.byKey(const Key('part-first-selector')),
      matching: find.byType(Scrollable),
    ),
  );
  await tester.pump(const Duration(milliseconds: 300));
}
