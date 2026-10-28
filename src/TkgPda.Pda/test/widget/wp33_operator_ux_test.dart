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
  testWidgets('logical container actions submit once without confirmation', (
    tester,
  ) async {
    final harness = await _operationHarness(tester);

    expect(find.text('Kasa Doldu'), findsOneWidget);
    expect(find.text('Boş Kasa Getir'), findsOneWidget);
    expect(find.text('Tüm Kasaları Kaldır'), findsNothing);
    expect(find.byKey(const Key('direct-action-REMOVE_EMPTY-1')), findsNothing);

    await tester.tap(find.text('Kasa Doldu'));
    await tester.pump(const Duration(milliseconds: 500));

    expect(harness.repository.containerActionCount, 1);
    expect(harness.repository.lastCommand?.operation, 'REMOVE_FULL');
    expect(find.byKey(const Key('confirm-direct-transport')), findsNothing);
    harness.dispose();
  });

  testWidgets('container projection supports variable position counts', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..containerPositionsValue = [
        ...FakePdaRepository().containerPositionsValue,
        const ContainerPosition(
          positionNumber: 2,
          label: '2. Kasa',
          fullAction: ContainerActionAvailability(
            enabled: true,
            status: 'READY',
            disabledCode: null,
            disabledMessage: null,
          ),
          emptyAction: ContainerActionAvailability(
            enabled: false,
            status: 'READY',
            disabledCode: 'CONTAINER_POSITION_OCCUPIED',
            disabledMessage: 'Bu konumda zaten bir kasa bulunuyor.',
          ),
          transportState: 'READY',
          queuePosition: null,
          etaSeconds: null,
          etaState: 'UNAVAILABLE',
        ),
      ];
    final harness = await _operationHarness(tester, repository: repository);

    await tester.drag(
      find.byKey(const Key('container-position-list')),
      const Offset(-260, 0),
    );
    await tester.pumpAndSettle();
    expect(find.text('Kasa Doldu'), findsNWidgets(2));
    expect(find.text('Boş Kasa Getir'), findsNWidgets(2));

    // Each logical container must own its action keys. These previously collided:
    // every card rendered the position-independent 'direct-action-BRING_EMPTY', so a
    // finder matched one widget per container and the browser E2E failed with
    // "2 widgets found". This is the case that should have caught it — the other
    // widget fixtures have exactly one container position, so the collision was
    // invisible to them.
    expect(
      find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      findsOneWidget,
    );
    expect(
      find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      findsOneWidget,
    );
    expect(
      find.byKey(const Key('direct-action-REMOVE_FULL-2')),
      findsOneWidget,
    );
    // Both buttons remain visible; position 2's empty action is disabled.
    expect(find.byKey(const Key('direct-action-BRING_EMPTY-2')), findsOneWidget);
    final bringEmpty2 = tester.widget<ElevatedButton>(
      find.byKey(const Key('direct-action-BRING_EMPTY-2')),
    );
    expect(bringEmpty2.enabled, isFalse);
    harness.dispose();
  });

  testWidgets('ambiguous remove-all retry reuses the same idempotency key', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..removeAllError = const AmbiguousMutationException(
        idempotencyKey: 'ambiguous-remove-all',
      );
    final harness = await _operationHarness(tester, repository: repository);

    await harness.controller.removeAllContainers();
    await tester.pumpAndSettle();
    final firstKey = repository.lastRemoveAllIdempotencyKey;
    expect(
      find.byKey(const Key('ambiguous-submission-banner')),
      findsOneWidget,
    );

    repository.removeAllError = null;
    await tester.tap(find.byKey(const Key('retry-ambiguous')));
    await tester.pumpAndSettle();

    expect(repository.removeAllCount, 2);
    expect(repository.lastRemoveAllIdempotencyKey, firstKey);
    harness.dispose();
  });

  testWidgets('global tasks separates active and terminal business views', (
    tester,
  ) async {
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
            logicalContainerNumber: 2,
            businessAction: 'Dolu kasa kaldırılıyor',
            requestedAt: DateTime.utc(2026, 9, 3, 9, 30),
            state: 'IN_PROGRESS',
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ],
      );
    final harness = await _loggedInHarness(tester, repository);

    await tester.tap(find.byKey(const Key('open-operator-queue')));
    await tester.pumpAndSettle();

    expect(find.text('Görevler'), findsOneWidget);
    expect(find.text('SH-01-A · Hücre'), findsOneWidget);
    expect(find.text('2. Kasa · Dolu kasa kaldırılıyor'), findsOneWidget);
    expect(find.text('Süre bilgisi yok'), findsOneWidget);
    expect(find.text('Aktif'), findsOneWidget);
    expect(find.text('Tamamlananlar'), findsOneWidget);
    expect(find.textContaining('WorkSite'), findsNothing);
    expect(find.textContaining('AGV'), findsNothing);
    expect(find.textContaining('RDS'), findsNothing);
    harness.dispose();
  });

  testWidgets(
    'active operator queue item is read-only and has no cancel button',
    (tester) async {
      const testRequestId = 'req-12345';
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
              logicalContainerNumber: 2,
              businessAction: 'Dolu kasa kaldırılıyor',
              requestedAt: DateTime.utc(2026, 9, 3, 9, 30),
              state: 'IN_PROGRESS',
              etaSeconds: 90,
              etaState: 'AVAILABLE',
              requestId: testRequestId,
            ),
          ],
        );
      final harness = await _loggedInHarness(tester, repository);

      await tester.tap(find.byKey(const Key('open-operator-queue')));
      await tester.pumpAndSettle();

      // The raw AGV identifier is no longer part of the model at all.
      expect(find.textContaining('sim_02'), findsNothing);
      expect(find.text('Yaklaşık 2 dakika'), findsOneWidget);

      final cancelButton = find.byKey(const Key('cancel-queue-req-12345'));
      expect(cancelButton, findsNothing);
      expect(repository.cancelCount, 0);
      harness.dispose();
    },
  );

  testWidgets(
    'waiting preparation queue item displays Hazırlık Bekleniyor badge and has no cancel button',
    (tester) async {
      const testRequestId = 'req-prep-123';
      final repository = FakePdaRepository()
        ..operatorQueueValue = OperatorQueuePage(
          terminal: false,
          offset: 0,
          limit: 20,
          hasMore: false,
          items: [
            OperatorQueueItem(
              queuePosition: 1,
              machine: 'SH-01-A · Hücre',
              part: '67115BN050-10-HAOS',
              logicalContainerNumber: 1,
              businessAction: 'Boş kasa (Depo hazırlığı bekleniyor)',
              requestedAt: DateTime.utc(2026, 9, 3, 9, 30),
              state: 'WAITING_PREPARATION',
              etaSeconds: null,
              etaState: 'UNAVAILABLE',
              requestId: testRequestId,
            ),
          ],
        );
      final harness = await _loggedInHarness(tester, repository);

      await tester.tap(find.byKey(const Key('open-operator-queue')));
      await tester.pumpAndSettle();

      expect(find.text('Hazırlık Bekleniyor'), findsOneWidget);
      expect(
        find.textContaining('Boş kasa (Depo hazırlığı bekleniyor)'),
        findsOneWidget,
      );
      expect(find.text('Süre bilgisi yok'), findsOneWidget);

      final cancelButton = find.byKey(const Key('cancel-queue-req-prep-123'));
      expect(cancelButton, findsNothing);
      expect(repository.cancelCount, 0);
      harness.dispose();
    },
  );

  testWidgets('robot status bar shows business info without maintenance controls', (
    tester,
  ) async {
    // The Operator model no longer has fields for raw RDS telemetry at all — the
    // API refuses to serialize them — so this is now a contract guarantee rather
    // than a rendering choice.
    final repository = FakePdaRepository()
      ..robotsValue = [
        const RobotTelemetry(
          displayName: 'HELIFORK',
          online: true,
          batteryLevel: 0.98,
          hasActiveAlarm: true,
        ),
      ];
    final harness = await _loggedInHarness(tester, repository);

    await tester.tap(find.byKey(const Key('open-operator-queue')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('robot-status-bar')), findsOneWidget);
    expect(find.text('HELIFORK'), findsOneWidget);
    expect(find.text('98%'), findsOneWidget);
    expect(find.text('Çevrimiçi'), findsOneWidget);
    // Alarm icon is visible but no maintenance controls are reachable.
    expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);
    expect(find.byKey(const Key('clear-robot-errors-Sfl-Cbd15')), findsNothing);
    expect(find.byKey(const Key('pause-resume-robot-Sfl-Cbd15')), findsNothing);

    harness.dispose();
  });

  testWidgets('robot card reports unknown battery and connectivity honestly', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..robotsValue = [const RobotTelemetry(displayName: 'HELIFORK')];
    final harness = await _loggedInHarness(tester, repository);

    await tester.tap(find.byKey(const Key('open-operator-queue')));
    await tester.pumpAndSettle();

    // Missing battery telemetry must not render as a 0% reading.
    expect(find.text('0%'), findsNothing);
    expect(find.text('—'), findsOneWidget);
    // Unknown connectivity is not presented as online.
    expect(find.text('Çevrimiçi'), findsNothing);
    // A robot with no fault must not show the fault icon.
    expect(find.byIcon(Icons.warning_amber_rounded), findsNothing);

    harness.dispose();
  });

  test(
    'operator queue item ignores a raw AGV identifier even if the server sends one',
    () {
      // The model has no agvId field, so the compiler prevents it being carried and
      // a stray server value is dropped rather than reaching the Operator.
      final item = OperatorQueueItem.fromJson(const {
        'queuePosition': 1,
        'machine': 'SH-01-A · Hücre',
        'part': '71550BN000-10-HAOS',
        'logicalContainerNumber': 2,
        'businessAction': 'Dolu kasa kaldırılıyor',
        'requestedAt': '2026-09-03T09:30:00Z',
        'state': 'IN_PROGRESS',
        'etaSeconds': 90,
        'etaState': 'AVAILABLE',
        'requestId': 'req-1',
        'agvId': 'Sfl-Cbd15',
      });

      expect(item.requestId, 'req-1');
      expect(item.machine, 'SH-01-A · Hücre');
      expect(item.toString(), isNot(contains('Sfl-Cbd15')));
    },
  );
}

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
  return _Harness(controller, repository);
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}
