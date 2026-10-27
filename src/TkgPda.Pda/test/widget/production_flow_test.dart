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
    'ENDING retry requires confirmation and explains field verification',
    (tester) async {
      final original = activeRecord();
      final ending = ProductionSession(
        id: original.id,
        machineId: original.machineId,
        variantId: original.variantId,
        productGroupId: original.productGroupId,
        mainPartId: original.mainPartId,
        startedAt: original.startedAt,
        status: 'ENDING',
      );
      final repo = FakePdaRepository()..productionValue = ending;
      await harness(tester, repo);
      expect(
        find.byKey(const Key('production-end-retry-guidance')),
        findsOneWidget,
      );
      expect(
        find.textContaining('Saha durumunu doğruladıktan'),
        findsOneWidget,
      );
      await tap(tester, const Key('production-end'));
      expect(repo.productionEndCount, 0);
      await tap(tester, const Key('confirm-production-end'));
      expect(repo.productionEndCount, 1);
    },
  );

  testWidgets(
    'start is on workspace; no quantity or transport before production',
    (tester) async {
      final repo = FakePdaRepository();
      final c = await harness(tester, repo);
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('production-start')))
            .onPressed,
        isNull,
      );
      await tap(tester, Key('main-part-${testMainPartOption.partId}'));
      expect(c.production.active, isNull);
      expect(repo.productionStartCount, 0);
      expect(find.byKey(const Key('production-start')), findsOneWidget);
      expect(c.transportActionsReady, isFalse);
      expect(find.text('Miktar'), findsNothing);
      expect(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
        findsNothing,
      );
      await tap(tester, const Key('production-start'));
      expect(c.production.active, isNotNull);
      expect(repo.productionStartCount, 1);
      expect(repo.createCount, 0);
      expect(find.text('ÜRETİM AKTİF'), findsOneWidget);
      expect(find.byKey(const Key('change-main-part')), findsNothing);
      expect(c.transportActionsReady, isTrue);
    },
  );

  testWidgets('active open transport continues while production can end', (
    tester,
  ) async {
    final repo = FakePdaRepository()
      ..productionValue = activeRecord()
      ..contextValue = testContext(activeRequests: [testActiveRequest]);
    await harness(tester, repo);
    expect(
      tester
          .widget<OutlinedButton>(find.byKey(const Key('production-end')))
          .onPressed,
      isNotNull,
    );
    expect(find.byKey(const Key('production-open-transports')), findsOneWidget);
    expect(
      find.textContaining('görev yöneticisinde devam ediyor'),
      findsOneWidget,
    );
    await tap(tester, const Key('production-end'));
    expect(find.byKey(const Key('confirm-production-end')), findsOneWidget);
    expect(repo.productionEndCount, 0);
  });

  testWidgets('open transport does not block starting the next production', (
    tester,
  ) async {
    final backgroundRequest = ActiveTransportRequest(
      requestId: 'background-production-end-request',
      requestNo: 43,
      operation: 'REMOVE_EMPTY',
      status: 'QUEUED',
      requestedBy: testActiveRequest.requestedBy,
      requestedByUsername: testActiveRequest.requestedByUsername,
      isMine: true,
      sourceSlotId: testActiveRequest.sourceSlotId,
      destinationSlotId: testActiveRequest.destinationSlotId,
      productGroupId: testActiveRequest.productGroupId,
      partId: testActiveRequest.partId,
      containerTypeId: testActiveRequest.containerTypeId,
      priority: testActiveRequest.priority,
      createdAt: testActiveRequest.createdAt,
      canCancel: true,
      cancelDisabledCode: null,
      cancelDisabledMessage: null,
      execution: testActiveRequest.execution,
      productionEnd: true,
      blocksProductionTransition: false,
    );
    final repo = FakePdaRepository()
      ..contextValue = testContext(activeRequests: [backgroundRequest]);
    await harness(tester, repo);
    await tap(tester, Key('main-part-${testMainPartOption.partId}'));

    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('production-start')))
          .onPressed,
      isNotNull,
    );
    expect(find.byKey(const Key('production-open-transports')), findsOneWidget);
    expect(
      find.textContaining('Yeni üretimi başlatabilirsiniz'),
      findsOneWidget,
    );
  });

  testWidgets('end needs explicit confirmation and returns to main selection', (
    tester,
  ) async {
    final repo = FakePdaRepository()..productionValue = activeRecord();
    final c = await harness(tester, repo);
    await tap(tester, const Key('production-end'));
    expect(repo.productionEndCount, 0);
    expect(find.textContaining('sırasıyla kaldırılacaktır'), findsOneWidget);
    await tester.tap(find.text('Vazgeç'));
    await tester.pumpAndSettle();
    expect(c.production.active, isNotNull);
    await tap(tester, const Key('production-end'));
    await tap(tester, const Key('confirm-production-end'));
    expect(c.production.active, isNull);
    expect(c.selectedMainPart, isNull);
    expect(repo.productionEndCount, 1);
    expect(repo.createCount, 0);
  });

  testWidgets(
    'dialog cannot end a different session learned during confirmation',
    (tester) async {
      final repo = FakePdaRepository()..productionValue = activeRecord();
      final c = await harness(tester, repo);
      await tap(tester, const Key('production-end'));
      repo.productionValue = activeRecord(id: 'new-production');
      await c.refreshContext();
      await tester.pumpAndSettle();
      await tap(tester, const Key('confirm-production-end'));
      expect(repo.productionEndCount, 0);
      expect(c.production.active!.id, 'new-production');
    },
  );

  testWidgets(
    'uncertain start shows explicit same-request retry, not an enabled start',
    (tester) async {
      final repo = FakePdaRepository();
      await harness(tester, repo);
      await tap(tester, Key('main-part-${testMainPartOption.partId}'));
      repo.productionStartError = const ConnectivityException();
      await tap(tester, const Key('production-start'));
      expect(find.byKey(const Key('production-retry')), findsOneWidget);
      expect(find.byKey(const Key('production-start')), findsNothing);
      final key = repo.productionStartKeys.single;
      repo.productionStartError = null;
      await tap(tester, const Key('production-retry'));
      expect(repo.productionStartKeys, [key, key]);
    },
  );

  testWidgets(
    'active production requests an alt part without retargeting mamul',
    (tester) async {
      final repo = FakePdaRepository()..productionValue = activeRecord();
      final c = await harness(tester, repo);
      expect(c.selectedEffectivePart!.partId, testMainPartId);
      expect(
        find.byKey(const Key('related-part-$testRelatedPartId')),
        findsNothing,
      );
      await tap(tester, const Key('alt-part-$testAltPartId'));
      expect(c.selectedEffectivePart!.partId, testMainPartId);
      expect(repo.partDemandCount, 1);
      expect(repo.containerActionCount, 0);
      expect(find.textContaining('Kasa Doldu'), findsWidgets);
    },
  );

  testWidgets('remote end clears unsent cart instead of offering stale parts', (
    tester,
  ) async {
    final repo = FakePdaRepository()..productionValue = activeRecord();
    final c = await harness(tester, repo);
    c.enterBatchSelection();
    await c.requestAltPart(testMainPartOption.relatedParts.first);
    expect(c.batchCart, isEmpty);
    expect(c.mamulActionsReady, isTrue);
    expect(c.selectedEffectivePart!.partId, testMainPartId);
    repo.productionValue = null;
    await c.refreshContext();
    await tester.pumpAndSettle();
    expect(c.batchCart, isEmpty);
    expect(c.selectedMainPart, isNull);
    expect(c.transportActionsReady, isFalse);
    expect(repo.createCount, 0);
  });

  for (final size in [
    const Size(390, 844),
    const Size(800, 1280),
    const Size(1280, 800),
  ]) {
    testWidgets('production controls fit ${size.width}x${size.height}', (
      tester,
    ) async {
      await harness(
        tester,
        FakePdaRepository()..productionValue = activeRecord(),
        size: size,
      );
      await reveal(tester, const Key('production-end'));
      expect(find.byKey(const Key('production-panel')), findsOneWidget);
      expect(tester.takeException(), isNull);
      await reveal(tester, const Key('alt-part-$testAltPartId'));
      expect(tester.takeException(), isNull);
    });
  }
}

ProductionSession activeRecord({String id = 'production-1'}) =>
    ProductionSession(
      id: id,
      machineId: testMachine.id,
      variantId: testVariantV1Id,
      productGroupId: testProductGroupId,
      mainPartId: testMainPartId,
      startedAt: DateTime.utc(2026, 9, 15, 8),
    );

Future<AppController> harness(
  WidgetTester tester,
  FakePdaRepository repo, {
  Size size = const Size(800, 1280),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  final c = AppController(
    repository: repo,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
  );
  addTearDown(c.dispose);
  await c.initialize();
  await c.login('00123');
  await c.selectMachine(testMachine);
  await tester.pumpWidget(TkgPdaApp(controller: c));
  await tester.pumpAndSettle();
  return c;
}

Future<void> reveal(WidgetTester tester, Key key) async {
  final finder = find.byKey(key);
  if (finder.evaluate().isNotEmpty) {
    await tester.ensureVisible(finder);
  } else {
    await tester.scrollUntilVisible(
      finder,
      250,
      scrollable: find.descendant(
        of: find.byKey(const Key('part-first-selector')),
        matching: find.byType(Scrollable),
      ),
    );
  }
  await tester.pumpAndSettle();
}

Future<void> tap(WidgetTester tester, Key key) async {
  await reveal(tester, key);
  await tester.tap(find.byKey(key));
  await tester.pumpAndSettle();
}
