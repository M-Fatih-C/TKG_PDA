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
      expect(find.text('Üretim Devam Ediyor'), findsOneWidget);
      await reveal(tester, const Key('change-main-part'));
      expect(
        tester
            .widget<OutlinedButton>(find.byKey(const Key('change-main-part')))
            .onPressed,
        isNull,
      );
      expect(c.transportActionsReady, isTrue);
    },
  );

  testWidgets('active open transport explains why end is disabled', (
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
      isNull,
    );
    expect(find.byKey(const Key('production-open-transports')), findsOneWidget);
    expect(repo.productionEndCount, 0);
  });

  testWidgets('end needs explicit confirmation and returns to main selection', (
    tester,
  ) async {
    final repo = FakePdaRepository()..productionValue = activeRecord();
    final c = await harness(tester, repo);
    await tap(tester, const Key('production-end'));
    expect(repo.productionEndCount, 0);
    expect(
      find.textContaining('taşıma görevlerini tamamlamaz'),
      findsOneWidget,
    );
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

  testWidgets('active production requests an alt part without retargeting mamul', (
    tester,
  ) async {
    final repo = FakePdaRepository()..productionValue = activeRecord();
    final c = await harness(tester, repo);
    expect(c.selectedEffectivePart!.partId, testMainPartId);
    expect(find.byKey(const Key('related-part-$testRelatedPartId')), findsNothing);
    await tap(tester, const Key('alt-part-$testAltPartId'));
    expect(c.selectedEffectivePart!.partId, testMainPartId);
    expect(repo.partDemandCount, 1);
    expect(repo.containerActionCount, 0);
    expect(find.textContaining('Kasam Doldu'), findsWidgets);
  });

  testWidgets('remote end clears unsent cart instead of offering stale parts', (
    tester,
  ) async {
    final repo = FakePdaRepository()..productionValue = activeRecord();
    final c = await harness(tester, repo);
    c.enterBatchSelection();
    await c.submitContainerAction(c.containerPositions.first, 'EMPTY');
    expect(c.batchCart, isNotEmpty);
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
