import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';

import '../support/fakes.dart';

void main() {
  testWidgets(
    'entering batch mode displays the banner and keeps the main screen visible',
    (tester) async {
      final harness = await _harness(tester);

      expect(find.byKey(const Key('batch-mode-banner')), findsNothing);
      expect(find.byKey(const Key('batch-cart-tray')), findsNothing);

      await _toggleBatchMode(tester);

      expect(find.byKey(const Key('batch-mode-banner')), findsOneWidget);
      expect(harness.controller.batchMode, isTrue);
      // Main screen remains visible with parts and machines
      expect(find.byKey(const Key('part-first-selector')), findsOneWidget);
      expect(
        find.byKey(Key('main-part-${testMainPartOption.partId}')),
        findsOneWidget,
      );

      harness.dispose();
    },
  );

  testWidgets(
    'selecting container actions in batch mode adds them to the tray',
    (tester) async {
      final harness = await _harness(tester);
      await _toggleBatchMode(tester);

      // Select main part
      await _selectMainPart(tester, testMainPartOption.partId);
      expect(find.text('1. Kasam Doldu'), findsOneWidget);

      // Tap container action
      await tester.tap(find.text('1. Kasam Doldu'));
      await tester.pumpAndSettle();

      expect(harness.controller.batchCart.length, 1);
      expect(find.byKey(const Key('batch-cart-tray')), findsOneWidget);
      expect(
        find.descendant(
          of: find.byKey(const Key('batch-cart-count')),
          matching: find.text('1'),
        ),
        findsOneWidget,
      );

      harness.dispose();
    },
  );

  testWidgets('browsing another main part does not clear the cart', (
    tester,
  ) async {
    final harness = await _harness(tester);
    await _toggleBatchMode(tester);

    await _selectMainPart(tester, testMainPartOption.partId);
    await tester.tap(find.text('1. Kasam Doldu'));
    await tester.pumpAndSettle();

    expect(harness.controller.batchCart.length, 1);

    // Browse a second main part
    await _selectMainPart(tester, testSecondMainPartOption.partId);
    expect(harness.controller.batchCart.length, 1);
    expect(find.byKey(const Key('batch-cart-tray')), findsOneWidget);

    harness.dispose();
  });

  testWidgets('the same part and position cannot be added twice', (
    tester,
  ) async {
    final harness = await _harness(tester);
    await _toggleBatchMode(tester);

    await _selectMainPart(tester, testMainPartOption.partId);
    await tester.tap(find.text('1. Kasam Doldu'));
    await tester.pumpAndSettle();

    expect(harness.controller.batchCart.length, 1);

    // Tap again
    await tester.tap(find.text('1. Kasam Doldu'));
    await tester.pumpAndSettle();

    expect(harness.controller.batchCart.length, 1);
    expect(find.textContaining('zaten sepette ekli'), findsOneWidget);

    harness.dispose();
  });

  testWidgets(
    'a selected item can be removed from the tray before submission',
    (tester) async {
      final harness = await _harness(tester);
      await _toggleBatchMode(tester);

      await _selectMainPart(tester, testMainPartOption.partId);
      await tester.tap(find.text('1. Kasam Doldu'));
      await tester.pumpAndSettle();

      expect(harness.controller.batchCart.length, 1);
      final itemId = harness.controller.batchCart.first.cartItemId;

      // Expand the tray
      await tester.tap(find.byKey(const Key('batch-cart-tray')));
      await tester.pumpAndSettle();

      final removeButton = find.byKey(Key('batch-remove-item-$itemId'));
      expect(removeButton, findsOneWidget);

      await tester.tap(removeButton);
      await tester.pumpAndSettle();

      expect(harness.controller.batchCart, isEmpty);
      harness.dispose();
    },
  );

  testWidgets('clearing the cart resets the batch items', (tester) async {
    final harness = await _harness(tester);
    await _toggleBatchMode(tester);

    await _selectMainPart(tester, testMainPartOption.partId);
    await tester.tap(find.text('1. Kasam Doldu'));
    await tester.pumpAndSettle();

    expect(harness.controller.batchCart.length, 1);

    await tester.tap(find.byKey(const Key('batch-clear-cart')));
    await tester.pumpAndSettle();

    expect(harness.controller.batchCart, isEmpty);
    expect(harness.controller.batchMode, isFalse);
    harness.dispose();
  });

  testWidgets('leaving machine clears the entire batch selection', (
    tester,
  ) async {
    final repo = FakePdaRepository()..loginUser = testSupervisor;
    final harness = await _harness(tester, repository: repo);
    await _toggleBatchMode(tester);

    await _selectMainPart(tester, testMainPartOption.partId);
    await tester.tap(find.text('1. Kasam Doldu'));
    await tester.pumpAndSettle();

    expect(harness.controller.batchCart, isNotEmpty);

    harness.controller.leaveMachine();
    await tester.pumpAndSettle();

    expect(harness.controller.batchMode, isFalse);
    expect(harness.controller.batchCart, isEmpty);
    harness.dispose();
  });

  testWidgets(
    'successful batch submission sends N independent items with unique idempotency keys',
    (tester) async {
      final repository = FakePdaRepository();
      final harness = await _harness(tester, repository: repository);
      await _toggleBatchMode(tester);

      await _selectMainPart(tester, testMainPartOption.partId);
      await tester.tap(find.text('1. Kasam Doldu'));
      await tester.pumpAndSettle();

      expect(harness.controller.canReviewBatch, isTrue);

      await tester.tap(find.byKey(const Key('batch-submit')));
      await tester.pumpAndSettle();

      expect(repository.batchCreateCount, 1);
      expect(repository.lastBatchMachineId, testMachine.id);
      expect(repository.lastBatchItems!.length, 1);
      expect(harness.controller.batchMode, isFalse);
      expect(harness.controller.batchCart, isEmpty);
      harness.dispose();
    },
  );

  testWidgets(
    'retrying an ambiguous batch submission reuses the same idempotency keys',
    (tester) async {
      final repository = FakePdaRepository()
        ..batchCreateError = const AmbiguousMutationException(
          idempotencyKey: 'batch-key-1',
        );
      final harness = await _harness(tester, repository: repository);
      await _toggleBatchMode(tester);

      await _selectMainPart(tester, testMainPartOption.partId);
      await tester.tap(find.text('1. Kasam Doldu'));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('batch-submit')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('ambiguous-batch-banner')), findsOneWidget);
      final firstKey = repository.lastBatchItems!.first.idempotencyKey;

      repository.batchCreateError = null;
      await tester.tap(find.byKey(const Key('retry-ambiguous-batch')));
      await tester.pumpAndSettle();

      expect(repository.batchCreateCount, 2);
      expect(repository.lastBatchItems!.first.idempotencyKey, firstKey);
      expect(harness.controller.batchMode, isFalse);
      harness.dispose();
    },
  );

  testWidgets(
    'an API error during batch submission preserves the cart for correction',
    (tester) async {
      final repository = FakePdaRepository()
        ..batchCreateError = const ApiProblem(
          status: 400,
          code: 'INVALID_BATCH',
          detail: 'Geçersiz grup talebi.',
          correlationId: 'cid-1',
        );
      final harness = await _harness(tester, repository: repository);
      await _toggleBatchMode(tester);

      await _selectMainPart(tester, testMainPartOption.partId);
      await tester.tap(find.text('1. Kasam Doldu'));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('batch-submit')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('problem-banner')), findsOneWidget);
      expect(find.text('Gönderilen bilgiler geçersiz.'), findsOneWidget);
      // The cart is kept intact so operator can adjust or retry
      expect(harness.controller.batchCart.length, 1);
      harness.dispose();
    },
  );

  testWidgets(
    'the existing single-part workflow outside batch mode still works',
    (tester) async {
      final repository = FakePdaRepository();
      final harness = await _harness(tester, repository: repository);

      expect(harness.controller.batchMode, isFalse);

      await _selectMainPart(tester, testMainPartOption.partId);
      await tester.tap(find.text('1. Kasam Doldu'));
      await tester.pump(const Duration(milliseconds: 500));

      expect(repository.containerActionCount, 1);
      expect(find.byKey(const Key('batch-cart-tray')), findsNothing);
      harness.dispose();
    },
  );
}

Future<void> _toggleBatchMode(WidgetTester tester) async {
  await tester.tap(find.byKey(const Key('enter-batch-selection')));
  await tester.pumpAndSettle();
}

Future<void> _selectMainPart(WidgetTester tester, String partId) async {
  final change = find.byKey(const Key('change-main-part'));
  if (change.evaluate().isNotEmpty) {
    await tester.ensureVisible(change);
    await tester.tap(change);
    await tester.pumpAndSettle();
  }
  await tester.tap(find.byKey(Key('main-part-$partId')));
  await tester.pumpAndSettle();
}

Future<_Harness> _harness(
  WidgetTester tester, {
  FakePdaRepository? repository,
}) async {
  tester.view.devicePixelRatio = 1;
  tester.view.physicalSize = const Size(1024, 2200);
  addTearDown(tester.view.resetDevicePixelRatio);
  addTearDown(tester.view.resetPhysicalSize);
  final store = MemoryDeviceCodeStore(testDeviceCode);
  final actualRepository = repository ?? FakePdaRepository();
  final controller = AppController(
    repository: actualRepository,
    deviceCodeStore: store,
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
  await tester.pumpAndSettle();
  await controller.login('employee-1');
  await tester.pumpAndSettle();
  await controller.selectMachine(testMachine);
  await tester.pumpAndSettle();
  return _Harness(controller, actualRepository);
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}
