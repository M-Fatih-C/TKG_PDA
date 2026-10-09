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

final _testSupervisor = AuthenticatedUser(
  id: testUser.id,
  username: 'supervisor',
  fullName: 'Test Vardiya Amiri',
  employeeNo: '1099',
  roles: const ['SUPERVISOR'],
);

void main() {
  testWidgets('first run opens Device Setup', (tester) async {
    final harness = await _harness(tester, deviceCode: null);

    expect(find.text('TKG Operator · Cihaz Kurulumu'), findsOneWidget);
    expect(find.byKey(const Key('device-code-field')), findsOneWidget);
    harness.dispose();
  });

  testWidgets('Device Setup securely saves provisioned code then opens login', (
    tester,
  ) async {
    final harness = await _harness(tester, deviceCode: null);

    await tester.enterText(
      find.byKey(const Key('device-code-field')),
      testDeviceCode,
    );
    await tester.tap(find.byKey(const Key('save-device-code')));
    await tester.pumpAndSettle();

    expect(harness.store.value, testDeviceCode);
    expect(find.text('TKG Operatör Girişi'), findsOneWidget);
    harness.dispose();
  });

  testWidgets('valid login opens machine list', (tester) async {
    final repo = FakePdaRepository()..loginUser = _testSupervisor;
    final harness = await _harness(tester, repository: repo);

    await _loginFromUi(tester);

    expect(find.text('Makine Seçimi'), findsOneWidget);
    expect(harness.controller.isAuthenticated, isTrue);
    harness.dispose();
  });

  testWidgets('login screen shows Sicil Numarası and no username field', (
    tester,
  ) async {
    final harness = await _harness(tester);

    expect(find.text('Sicil Numarası'), findsOneWidget);
    expect(find.text('Kullanıcı Adı'), findsNothing);
    expect(find.byKey(const Key('employee-number-field')), findsOneWidget);
    expect(find.byKey(const Key('username-field')), findsNothing);
    harness.dispose();
  });

  testWidgets('login submits the employee number, not a username', (
    tester,
  ) async {
    final repository = FakePdaRepository();
    final harness = await _harness(tester, repository: repository);

    await _loginFromUi(tester, employeeNumber: '00123');

    expect(repository.lastEmployeeNumber, '00123');
    harness.dispose();
  });

  testWidgets('login preserves significant leading zeros in the sicil value', (
    tester,
  ) async {
    final repository = FakePdaRepository();
    final harness = await _harness(tester, repository: repository);

    await _loginFromUi(tester, employeeNumber: '001245');

    expect(repository.lastEmployeeNumber, '001245');
    expect(repository.lastEmployeeNumber, isNot('1245'));
    harness.dispose();
  });

  testWidgets('invalid login shows generic ProblemDetails UX', (tester) async {
    final repository = FakePdaRepository()
      ..loginProblem = const ApiProblem(
        status: 401,
        code: 'INVALID_CREDENTIALS',
        detail: 'Kullanıcı adı veya şifre geçersiz.',
        correlationId: 'corr-login',
      );
    final harness = await _harness(tester, repository: repository);

    await _loginFromUi(tester);

    expect(find.byKey(const Key('problem-banner')), findsOneWidget);
    expect(
      find.text('Oturumunuz sona erdi. Lütfen yeniden giriş yapın.'),
      findsOneWidget,
    );
    expect(find.textContaining('corr-login'), findsNothing);
    harness.dispose();
  });

  testWidgets('login shows a device error when the device is not authorized', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..loginProblem = const ApiProblem(
        status: 403,
        code: 'DEVICE_NOT_AUTHORIZED',
        detail: 'The client device is unknown, inactive or not a PDA.',
        correlationId: 'corr-device',
      );
    final harness = await _harness(tester, repository: repository);

    await _loginFromUi(tester);

    expect(find.byKey(const Key('problem-banner')), findsOneWidget);
    expect(find.text('Bu işlem için yetkiniz bulunmuyor.'), findsOneWidget);
    harness.dispose();
  });

  testWidgets('machine list renders customer values without permission chips', (
    tester,
  ) async {
    final repository = FakePdaRepository()..loginUser = _testSupervisor;
    final harness = await _loggedInHarness(tester, repository: repository);

    expect(find.byKey(Key('machine-${testMachine.id}')), findsOneWidget);
    expect(find.text(testMachine.code), findsOneWidget);
    expect(find.text('Görüntüleme'), findsNothing);
    expect(find.text('Operasyon'), findsNothing);
    expect(find.text('Varyant'), findsNothing);
    harness.dispose();
  });

  testWidgets('an operator with no assigned machines sees an empty state', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..loginUser = _testSupervisor
      ..machineValues = [];
    final harness = await _loggedInHarness(tester, repository: repository);

    expect(find.byKey(const Key('machine-list-empty')), findsOneWidget);
    expect(
      find.textContaining('Size tanımlı makine bulunamadı'),
      findsOneWidget,
    );
    harness.dispose();
  });

  testWidgets('a view-only machine shows one understandable limitation', (
    tester,
  ) async {
    final viewOnly = MachineSummary(
      id: '30000000-0000-0000-0000-000000000099',
      code: 'VIEW-ONLY',
      displayName: 'Görüntülenebilir Makine',
      displayOrder: 99,
      permissions: const MachinePermissions(
        canView: true,
        canOperate: false,
        canChangeVariant: false,
        canManageSlots: false,
      ),
    );
    final repository = FakePdaRepository()
      ..loginUser = _testSupervisor
      ..machineValues = [viewOnly];
    final harness = await _loggedInHarness(tester, repository: repository);

    expect(
      find.text('Bu makinede taşıma işlemi yapamazsınız.'),
      findsOneWidget,
    );
    expect(find.text('Operasyon'), findsNothing);
    harness.dispose();
  });

  testWidgets('machine selection loads operational context', (tester) async {
    final repo = FakePdaRepository()..loginUser = _testSupervisor;
    final harness = await _loggedInHarness(tester, repository: repo);

    await tester.tap(find.byKey(Key('machine-${testMachine.id}')));
    await tester.pumpAndSettle();

    expect(find.text('${testMachine.code} · Parça ve İşlem'), findsOneWidget);
    expect(find.byKey(const Key('part-first-selector')), findsOneWidget);
    expect(harness.controller.context, isNotNull);
    harness.dispose();
  });

  testWidgets('resolved variant remains visible on the part screen', (
    tester,
  ) async {
    final harness = await _operationHarness(tester);

    await _scrollTo(tester, find.byKey(const Key('single-compatible-variant')));
    expect(find.textContaining('V1 · Varyant 1'), findsOneWidget);
    expect(find.byKey(const Key('part-first-selector')), findsOneWidget);
    harness.dispose();
  });

  group('Customer UX — Automatic Machine Slot Resolution', () {
    testWidgets(
      'direct action buttons are reachable without tapping a layout slot',
      (tester) async {
        final harness = await _operationHarness(tester);

        await _scrollTo(
          tester,
          find.byKey(const Key('direct-action-BRING_EMPTY-1')),
        );
        expect(
          find.byKey(const Key('direct-action-BRING_EMPTY-1')),
          findsOneWidget,
        );
        expect(
          find.byKey(const Key('direct-action-REMOVE_FULL-1')),
          findsOneWidget,
        );
        expect(
          find.byKey(const Key('direct-action-REMOVE_EMPTY-1')),
          findsNothing,
        );
        expect(find.text('İşleme Devam Et'), findsNothing);
        expect(find.text('Fiziksel Yerleşim'), findsNothing);
        harness.dispose();
      },
    );

    testWidgets('BRING_EMPTY submits with no client-selected physical slot', (
      tester,
    ) async {
      final repository = FakePdaRepository();
      final harness = await _operationHarness(tester, repository: repository);

      await _scrollTo(
        tester,
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      await tester.tap(find.byKey(const Key('direct-action-BRING_EMPTY-1')));
      await tester.pump(const Duration(milliseconds: 500));
      expect(find.byKey(const Key('confirm-direct-transport')), findsNothing);
      expect(find.text('Sistem tarafından seçilecek'), findsNothing);

      expect(repository.createCount, 1);
      expect(repository.lastCommand!.sourceSlotId, isNull);
      expect(repository.lastCommand!.destinationSlotId, isNull);
      expect(repository.lastCommand!.productGroupId, testProductGroupId);
      expect(repository.lastCommand!.partId, testMainPartId);
      expect(repository.lastCommand!.operation, 'BRING_EMPTY');
      await tester.pump();
      expect(find.text('1. Kasa talebi oluşturuldu.'), findsOneWidget);
      expect(find.text('Fiziksel Yerleşim'), findsNothing);
      expect(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
        findsOneWidget,
      );
      final emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(emptyBtn.enabled, isFalse);
      expect(find.textContaining('Aktif Taşımalar (1)'), findsOneWidget);
      harness.dispose();
    });

    for (final operationCase in const [(operation: 'REMOVE_FULL')]) {
      testWidgets(
        '${operationCase.operation} submits without a client physical slot',
        (tester) async {
          final repository = FakePdaRepository();
          final harness = await _operationHarness(
            tester,
            repository: repository,
          );

          await _scrollTo(
            tester,
            find.byKey(Key('direct-action-${operationCase.operation}-1')),
          );
          await tester.tap(
            find.byKey(Key('direct-action-${operationCase.operation}-1')),
          );
          await tester.pump(const Duration(milliseconds: 500));
          expect(
            find.byKey(const Key('confirm-direct-transport')),
            findsNothing,
          );

          expect(repository.lastCommand!.operation, operationCase.operation);
          expect(repository.lastCommand!.sourceSlotId, isNull);
          expect(repository.lastCommand!.destinationSlotId, isNull);
          harness.dispose();
        },
      );
    }

    for (final errorCase in const [
      (
        operation: 'BRING_EMPTY',
        code: 'BRING_EMPTY_SLOT_NOT_FOUND',
        message: 'Bu parça için boş konum bulunamadı.',
      ),
      (
        operation: 'REMOVE_FULL',
        code: 'REMOVE_FULL_SLOT_NOT_FOUND',
        message: 'Bu parça için dolu kasa bulunamadı.',
      ),
    ]) {
      testWidgets('${errorCase.operation} shows a friendly resolver error', (
        tester,
      ) async {
        final repository = FakePdaRepository()
          ..createError = ApiProblem(
            status: 422,
            code: errorCase.code,
            detail: errorCase.message,
            correlationId: null,
          );
        final harness = await _operationHarness(tester, repository: repository);

        await _scrollTo(
          tester,
          find.byKey(Key('direct-action-${errorCase.operation}-1')),
        );
        await tester.tap(
          find.byKey(Key('direct-action-${errorCase.operation}-1')),
        );
        await tester.pumpAndSettle();
        expect(find.text(errorCase.message), findsOneWidget);
        expect(find.textContaining(errorCase.code), findsNothing);
        harness.dispose();
      });
    }
  });

  testWidgets('the action button is disabled while a submission is in flight, '
      'preventing duplicate submission', (tester) async {
    final repository = FakePdaRepository()
      ..createCompleter = Completer<TransportResult>();
    final harness = await _operationHarness(tester, repository: repository);

    await _scrollTo(
      tester,
      find.byKey(const Key('direct-action-BRING_EMPTY-1')),
    );
    await tester.tap(find.byKey(const Key('direct-action-BRING_EMPTY-1')));
    // A single frame — the pending completer never resolves, and the busy
    // indicator is an indeterminate animation, so pumpAndSettle would hang.
    await tester.pump();

    expect(harness.controller.busy, isFalse);
    expect(find.text('Gönderiliyor'), findsOneWidget);
    expect(
      tester
          .widget<ElevatedButton>(
            find.byKey(const Key('direct-action-BRING_EMPTY-1')),
          )
          .onPressed,
      isNull,
    );
    expect(repository.createCount, 1);

    repository.createCompleter!.complete(
      const TransportResult(
        id: '90000000-0000-0000-0000-000000000001',
        requestNo: 42,
        status: 'QUEUED',
        operation: 'BRING_EMPTY',
        idempotentReplay: false,
      ),
    );
    await tester.pump(const Duration(milliseconds: 500));
    expect(repository.createCount, 1);
    harness.dispose();
  });

  testWidgets('ambiguous transport displays safe retry choices', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..createError = const AmbiguousMutationException(
        idempotencyKey: 'ambiguous',
      );
    final harness = await _operationHarness(tester, repository: repository);

    await _submitBringEmpty(tester);

    expect(
      find.byKey(const Key('ambiguous-submission-banner')),
      findsOneWidget,
    );
    expect(find.byKey(const Key('retry-ambiguous')), findsOneWidget);
    expect(find.byKey(const Key('refresh-after-ambiguous')), findsOneWidget);
    harness.dispose();
  });

  testWidgets('ambiguous retry reuses its existing idempotency key', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..createError = const AmbiguousMutationException(
        idempotencyKey: 'ambiguous',
      );
    final harness = await _operationHarness(tester, repository: repository);

    await _submitBringEmpty(tester);
    final firstKey = repository.lastIdempotencyKey;
    repository.createError = null;
    await tester.tap(find.byKey(const Key('retry-ambiguous')));
    await tester.pump(const Duration(milliseconds: 500));

    expect(repository.createCount, 2);
    expect(repository.lastIdempotencyKey, firstKey);
    harness.dispose();
  });

  testWidgets('offline state keeps last context but disables mutations', (
    tester,
  ) async {
    final repository = FakePdaRepository();
    final harness = await _operationHarness(tester, repository: repository);
    await _scrollTo(
      tester,
      find.byKey(const Key('direct-action-BRING_EMPTY-1')),
    );
    repository.contextError = const ConnectivityException();

    await harness.controller.refreshContext(showBusy: true);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('offline-banner')), findsOneWidget);
    final button = tester.widget<ElevatedButton>(
      find.byKey(const Key('direct-action-BRING_EMPTY-1')),
    );
    expect(button.onPressed, isNull);
    harness.dispose();
  });

  testWidgets('active transport uses stable execution-state presentation', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..contextValue = testContext(activeRequests: [testActiveRequest]);
    final harness = await _operationHarness(tester, repository: repository);

    await _scrollTo(tester, find.byKey(const Key('active-transport-section')));
    expect(find.text('Robot bekleniyor'), findsOneWidget);
    expect(find.text('#42 · Boş Kasa Getir'), findsOneWidget);
    expect(find.textContaining('Aktif Taşımalar (1)'), findsOneWidget);
    expect(find.textContaining('AGV'), findsNothing);
    harness.dispose();
  });

  testWidgets(
    'failure state shows operational language without technical references',
    (tester) async {
      final failure = _failureRequest();
      final repository = FakePdaRepository()
        ..contextValue = testContext(activeRequests: [failure]);
      final harness = await _operationHarness(tester, repository: repository);

      await _scrollTo(
        tester,
        find.byKey(const Key('active-transport-section')),
      );
      expect(find.text('Tamamlanamadı'), findsOneWidget);
      expect(find.text('Robot görevi tamamlayamadı.'), findsOneWidget);
      expect(find.text('Destek referansı'), findsNothing);
      expect(find.textContaining('failure-correlation'), findsNothing);
      harness.dispose();
    },
  );

  testWidgets('cancel requires confirmation and uses backend capability', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..contextValue = testContext(activeRequests: [testActiveRequest]);
    final harness = await _operationHarness(tester, repository: repository);

    await _scrollTo(
      tester,
      find.byKey(Key('cancel-${testActiveRequest.requestId}')),
    );
    await tester.tap(find.byKey(Key('cancel-${testActiveRequest.requestId}')));
    await tester.pumpAndSettle();
    expect(find.text('Talebi iptal et'), findsOneWidget);
    await tester.tap(find.byKey(const Key('confirm-cancel')));
    await tester.pumpAndSettle();

    expect(repository.cancelCount, 1);
    harness.dispose();
  });

  testWidgets('401 clears state and router returns to login', (tester) async {
    final harness = await _loggedInHarness(tester);

    harness.controller.handleUnauthorized();
    await tester.pumpAndSettle();

    expect(find.text('TKG Operatör Girişi'), findsOneWidget);
    expect(harness.controller.isAuthenticated, isFalse);
    harness.dispose();
  });

  testWidgets('logout clears auth while preserving device code', (
    tester,
  ) async {
    final harness = await _loggedInHarness(tester);

    await tester.tap(find.byKey(const Key('logout-button')));
    await tester.pumpAndSettle();

    expect(find.text('TKG Operatör Girişi'), findsOneWidget);
    expect(harness.store.value, testDeviceCode);
    expect(harness.controller.isAuthenticated, isFalse);
    harness.dispose();
  });

  testWidgets('process restart does not restore an authenticated session', (
    tester,
  ) async {
    final store = MemoryDeviceCodeStore(testDeviceCode);
    final first = await _loggedInHarness(tester, store: store);
    expect(first.controller.isAuthenticated, isTrue);
    first.dispose();

    final second = await _harness(tester, store: store);

    expect(find.text('TKG Operatör Girişi'), findsOneWidget);
    expect(second.controller.isAuthenticated, isFalse);
    expect(second.store.value, testDeviceCode);
    second.dispose();
  });

  testWidgets('Device Code change clears session and operational state', (
    tester,
  ) async {
    final harness = await _operationHarness(tester);
    expect(harness.controller.isAuthenticated, isTrue);
    expect(harness.controller.context, isNotNull);

    await harness.controller.beginDeviceCodeChange();
    await tester.pumpAndSettle();

    expect(find.text('TKG Operator · Cihaz Kurulumu'), findsOneWidget);
    expect(harness.controller.isAuthenticated, isFalse);
    expect(harness.controller.context, isNull);
    expect(harness.controller.machines, isEmpty);
    await harness.controller.saveDeviceCode('SH01A-TERM01');
    expect(harness.store.value, 'SH01A-TERM01');
    harness.dispose();
  });

  testWidgets('resume refetch completes before mutations are re-enabled', (
    tester,
  ) async {
    final repository = FakePdaRepository();
    final harness = await _operationHarness(tester, repository: repository);
    repository.meCompleter = Completer<AuthenticatedUser>();

    harness.controller.onPaused();
    expect(harness.controller.mutationEnabled, isFalse);
    expect(harness.controller.pollingActive, isFalse);

    final resumed = harness.controller.onResumed();
    await tester.pump();
    expect(harness.controller.resumeValidationPending, isTrue);
    expect(harness.controller.mutationEnabled, isFalse);

    repository.meCompleter!.complete(testUser);
    await resumed;
    await tester.pump();
    expect(harness.controller.resumeValidationPending, isFalse);
    expect(harness.controller.mutationEnabled, isTrue);
    expect(repository.meCount, 1);
    harness.dispose();
  });

  for (final size in const [
    Size(1024, 600),
    Size(1280, 720),
    Size(1366, 768),
    Size(1920, 1080),
  ]) {
    testWidgets(
      'combined workspace is usable at ${size.width.toInt()}x${size.height.toInt()}',
      (tester) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = size;
        addTearDown(tester.view.resetDevicePixelRatio);
        addTearDown(tester.view.resetPhysicalSize);
        final repository = FakePdaRepository()
          ..contextValue = testContext(activeRequests: [testActiveRequest]);
        final harness = await _operationHarness(tester, repository: repository);

        expect(find.byKey(const Key('part-first-selector')), findsOneWidget);
        await _scrollTo(
          tester,
          find.byKey(const Key('active-transport-section')),
        );
        expect(
          find.byKey(const Key('active-transport-section')),
          findsOneWidget,
        );
        expect(tester.takeException(), isNull);
        await _scrollTo(
          tester,
          find.byKey(const Key('direct-action-BRING_EMPTY-1')),
        );
        expect(
          tester
              .getSize(find.byKey(const Key('direct-action-BRING_EMPTY-1')))
              .height,
          greaterThanOrEqualTo(48),
        );
        expect(tester.takeException(), isNull);
        harness.dispose();
      },
    );
  }

  testWidgets(
    'the combined part and action flow remains usable on a narrow PDA',
    (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = const Size(360, 800);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);
      final harness = await _operationHarness(tester);

      await _scrollTo(
        tester,
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('direct-action-REMOVE_EMPTY-1')),
        findsNothing,
      );
      expect(find.text('İşleme Devam Et'), findsNothing);
      expect(find.text('Fiziksel Yerleşim'), findsNothing);
      expect(tester.takeException(), isNull);
      harness.dispose();
    },
  );
}

Future<_Harness> _harness(
  WidgetTester tester, {
  String? deviceCode = testDeviceCode,
  MemoryDeviceCodeStore? store,
  FakePdaRepository? repository,
}) async {
  final actualStore = store ?? MemoryDeviceCodeStore(deviceCode);
  final actualRepository = repository ?? FakePdaRepository();
  final controller = AppController(
    repository: actualRepository,
    deviceCodeStore: actualStore,
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
  return _Harness(controller, actualStore, actualRepository);
}

Future<_Harness> _loggedInHarness(
  WidgetTester tester, {
  MemoryDeviceCodeStore? store,
  FakePdaRepository? repository,
}) async {
  final harness = await _harness(tester, store: store, repository: repository);
  await harness.controller.login('operator');
  await tester.pumpAndSettle();
  return harness;
}

Future<_Harness> _operationHarness(
  WidgetTester tester, {
  FakePdaRepository? repository,
}) async {
  final harness = await _loggedInHarness(tester, repository: repository);
  await harness.controller.selectMachine(testMachine);
  await tester.pumpAndSettle();
  // Resolve the default fixture's main part. Its single compatible variant
  // is already active, so transport actions appear on this same page.
  await _resolveDefaultPartFirstSelection(harness.controller);
  await tester.pumpAndSettle();
  return harness;
}

Future<void> _resolveDefaultPartFirstSelection(AppController controller) async {
  await controller.selectMainPartOption(testMainPartOption);
}

Future<void> _loginFromUi(
  WidgetTester tester, {
  String employeeNumber = '00123',
}) async {
  await tester.enterText(
    find.byKey(const Key('employee-number-field')),
    employeeNumber,
  );
  await tester.tap(find.byKey(const Key('login-button')));
  await tester.pumpAndSettle();
}

Future<void> _submitBringEmpty(WidgetTester tester) async {
  await _scrollTo(tester, find.byKey(const Key('direct-action-BRING_EMPTY-1')));
  await tester.tap(find.byKey(const Key('direct-action-BRING_EMPTY-1')));
  await tester.pumpAndSettle();
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

ActiveTransportRequest _failureRequest() => ActiveTransportRequest(
  requestId: 'failure-request',
  requestNo: 77,
  operation: 'REMOVE_FULL',
  status: 'RUNNING',
  requestedBy: testUser.id,
  requestedByUsername: testUser.username,
  isMine: true,
  sourceSlotId: 'slot-full',
  destinationSlotId: null,
  productGroupId: null,
  partId: null,
  containerTypeId: null,
  priority: 100,
  createdAt: DateTime.utc(2026, 8, 24),
  canCancel: false,
  cancelDisabledCode: 'CANCELLATION_AFTER_EXECUTION_DEFERRED',
  cancelDisabledMessage: 'Yürütme başladıktan sonra PDA iptali yapılamaz.',
  execution: const ExecutionProjection(
    state: 'FAILED',
    lastSyncAt: null,
    failureCode: 'EXECUTION_FAILED',
    failureCategory: 'EXECUTION',
    operatorMessage: 'Robot görevi tamamlayamadı.',
    correlationId: 'failure-correlation',
  ),
);

class _Harness {
  const _Harness(this.controller, this.store, this.repository);

  final AppController controller;
  final MemoryDeviceCodeStore store;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}
