import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/features/warehouse/warehouse_preparation_screen.dart';

import '../support/fakes.dart';

// Customer UI — Warehouse Preparation Console: a warehouse/supervisor-only
// list of WAITING_PREPARATION requests with a confirmed "Hazır" action,
// kept separate from the line-operator part-selection flow.
void main() {
  testWidgets(
    'preparation shows machine, content and server time without routing fields',
    (tester) async {
      final createdAt = DateTime(2026, 9, 17, 9, 5, 7);
      final request = WarehousePreparationRequest.fromJson({
        'requestId': 'test-preparation',
        'requestNo': 54,
        'machineId': testMachine.id,
        'machineCode': testMachine.code,
        'machineDisplayName': testMachine.displayName,
        'operation': 'BRING_EMPTY',
        'partCode': 'MAIN-PART',
        'containerTypeCode': 'CUSTOMER-CONTAINER',
        'sourceSlotCode': 'INTERNAL-SOURCE',
        'sourceSlotDisplayName': 'Hidden source',
        'destinationSlotCode': 'INTERNAL-DEST',
        'destinationSlotDisplayName': 'Hidden destination',
        'createdAt': createdAt.toUtc().toIso8601String(),
      });
      final repository = FakePdaRepository()
        ..loginUser = _warehouseUser
        ..warehouseRequestsValue = [request];
      final harness = await _harness(tester, repository: repository);
      await _openLegacyConsole(tester, harness);

      expect(find.textContaining(testMachine.code), findsOneWidget);
      expect(find.text('MAIN-PART'), findsOneWidget);
      expect(find.text('Boş taşıma kabı hazırlanacak'), findsOneWidget);
      expect(find.text('CUSTOMER-CONTAINER'), findsOneWidget);
      expect(find.text('17.09.2026 09:05:07'), findsOneWidget);
      for (final hidden in [
        'Kaynak',
        'Hedef',
        'Kasa Tipi',
        'Hidden source',
        'Hidden destination',
        'INTERNAL-SOURCE',
        'INTERNAL-DEST',
      ]) {
        expect(find.text(hidden), findsNothing);
      }
      expect(repository.markReadyCount, 0);
      harness.dispose();
    },
  );

  for (final timestamp in [null, 'invalid-time']) {
    testWidgets('missing/invalid timestamp stays unknown: $timestamp', (
      tester,
    ) async {
      final request = WarehousePreparationRequest.fromJson({
        'requestId': 'test-missing-time',
        'requestNo': 55,
        'machineId': testMachine.id,
        'machineCode': testMachine.code,
        'operation': 'BRING_EMPTY',
        'createdAt': timestamp,
      });
      expect(request.createdAt, isNull);
      final repository = FakePdaRepository()
        ..loginUser = _warehouseUser
        ..warehouseRequestsValue = [request];
      final harness = await _harness(tester, repository: repository);
      await _openLegacyConsole(tester, harness);
      expect(find.text('Talep zamanı alınamadı'), findsOneWidget);
      expect(find.text('Bilinmiyor'), findsOneWidget);
      expect(find.text('Az önce'), findsNothing);
      expect(find.text('Taşıma kabı bilgisi eksik'), findsOneWidget);
      harness.dispose();
    });
  }

  testWidgets('unknown operation is not guessed from part or container code', (
    tester,
  ) async {
    final request = WarehousePreparationRequest.fromJson({
      'requestId': 'test-unknown-kind',
      'requestNo': 56,
      'machineId': testMachine.id,
      'machineCode': testMachine.code,
      'operation': 'FUTURE_OPERATION',
      'partCode': 'A-PART',
      'containerTypeCode': 'PALLET-EVIDENCE',
    });
    final repository = FakePdaRepository()
      ..loginUser = _warehouseUser
      ..warehouseRequestsValue = [request];
    final harness = await _harness(tester, repository: repository);
    await _openLegacyConsole(tester, harness);
    expect(find.text('Talep türü doğrulanamadı'), findsOneWidget);
    expect(find.text('FUTURE_OPERATION'), findsNothing);
    expect(find.text('Boş taşıma kabı hazırlanacak'), findsNothing);
    expect(
      tester
          .widget<ElevatedButton>(
            find.byKey(const Key('warehouse-ready-test-unknown-kind')),
          )
          .onPressed,
      isNull,
    );
    expect(repository.markReadyCount, 0);
    harness.dispose();
  });

  testWidgets(
    'warehouse-authorized user can open the console and see waiting requests',
    (tester) async {
      final repository = FakePdaRepository()
        ..loginUser = _warehouseUser
        ..warehouseRequestsValue = [testWarehousePreparationRequest];
      final harness = await _harness(tester, repository: repository);

      expect(find.byKey(const Key('open-warehouse-preparation')), findsNothing);
      expect(find.byKey(const Key('open-preparation-areas')), findsOneWidget);
      await tester.tap(find.byKey(const Key('open-preparation-areas')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('leave-preparation-areas')), findsOneWidget);
      expect(find.byKey(const Key('warehouse-preparation-list')), findsNothing);
      expect(find.textContaining('SLOT-V'), findsNothing);
      harness.dispose();
    },
  );

  testWidgets('ordinary line operator does not gain the warehouse action', (
    tester,
  ) async {
    final harness = await _harness(tester);

    expect(find.byKey(const Key('open-warehouse-preparation')), findsNothing);
    expect(harness.controller.canAccessWarehousePreparation, isFalse);
    harness.dispose();
  });

  testWidgets('supervisor can also open the console', (tester) async {
    final repository = FakePdaRepository()
      ..loginUser = _supervisorUser
      ..warehouseRequestsValue = [testWarehousePreparationRequest];
    final harness = await _harness(tester, repository: repository);

    expect(find.byKey(const Key('open-warehouse-preparation')), findsNothing);
    expect(find.byKey(const Key('open-warehouse-home')), findsOneWidget);
    await tester.tap(find.byKey(const Key('open-warehouse-home')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('open-preparation-areas')), findsOneWidget);
    harness.dispose();
  });

  testWidgets('empty state is shown when there is nothing to prepare', (
    tester,
  ) async {
    final repository = FakePdaRepository()..loginUser = _warehouseUser;
    final harness = await _harness(tester, repository: repository);
    await tester.tap(find.byKey(const Key('open-preparation-areas')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('preparation-areas-empty')), findsOneWidget);
    harness.dispose();
  });

  testWidgets('a failed list load shows an error with retry', (tester) async {
    final repository = FakePdaRepository()
      ..loginUser = _warehouseUser
      ..warehouseRequestsError = const ApiProblem(
        status: 503,
        code: 'DATABASE_UNAVAILABLE',
        detail: 'Veritabanı geçici olarak kullanılamıyor.',
        correlationId: null,
      );
    final harness = await _harness(tester, repository: repository);
    await _openLegacyConsole(tester, harness);

    expect(
      find.byKey(const Key('warehouse-preparation-error')),
      findsOneWidget,
    );

    repository.warehouseRequestsError = null;
    repository.warehouseRequestsValue = [testWarehousePreparationRequest];
    await tester.tap(find.byKey(const Key('warehouse-preparation-retry')));
    await tester.pumpAndSettle();

    expect(
      find.byKey(
        Key('warehouse-item-${testWarehousePreparationRequest.requestId}'),
      ),
      findsOneWidget,
    );
    harness.dispose();
  });

  testWidgets('marking ready requires confirmation before acting', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..loginUser = _warehouseUser
      ..warehouseRequestsValue = [testWarehousePreparationRequest];
    final harness = await _harness(tester, repository: repository);
    await _openLegacyConsole(tester, harness);

    await tester.tap(
      find.byKey(
        Key('warehouse-ready-${testWarehousePreparationRequest.requestId}'),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('confirm-ready')), findsOneWidget);
    expect(
      find.textContaining(
        'güvenlik koşulları sağlandığında taşıma otomatik başlayacak',
      ),
      findsOneWidget,
    );

    await tester.tap(find.text('Vazgeç'));
    await tester.pumpAndSettle();

    expect(repository.markReadyCount, 0);
    expect(
      find.byKey(
        Key('warehouse-item-${testWarehousePreparationRequest.requestId}'),
      ),
      findsOneWidget,
    );
    harness.dispose();
  });

  testWidgets(
    'confirming ready marks the request and removes it from the list',
    (tester) async {
      final repository = FakePdaRepository()
        ..loginUser = _warehouseUser
        ..warehouseRequestsValue = [testWarehousePreparationRequest];
      final harness = await _harness(tester, repository: repository);
      await _openLegacyConsole(tester, harness);

      await tester.tap(
        find.byKey(
          Key('warehouse-ready-${testWarehousePreparationRequest.requestId}'),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('confirm-ready')));
      await tester.pumpAndSettle();

      expect(repository.markReadyCount, 1);
      expect(
        repository.lastMarkReadyRequestId,
        testWarehousePreparationRequest.requestId,
      );
      expect(
        find.byKey(
          Key('warehouse-item-${testWarehousePreparationRequest.requestId}'),
        ),
        findsNothing,
      );
      expect(
        find.byKey(const Key('warehouse-preparation-empty')),
        findsOneWidget,
      );
      harness.dispose();
    },
  );

  testWidgets('a failed ready action keeps the item for retry', (tester) async {
    final repository = FakePdaRepository()
      ..loginUser = _warehouseUser
      ..warehouseRequestsValue = [testWarehousePreparationRequest]
      ..markReadyError = const ApiProblem(
        status: 409,
        code: 'REQUEST_TERMINAL',
        detail: 'İstek artık etkin değil.',
        correlationId: 'corr-ready',
      );
    final harness = await _harness(tester, repository: repository);
    await _openLegacyConsole(tester, harness);

    await tester.tap(
      find.byKey(
        Key('warehouse-ready-${testWarehousePreparationRequest.requestId}'),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('confirm-ready')));
    await tester.pumpAndSettle();

    expect(repository.markReadyCount, 1);
    expect(
      find.byKey(
        Key('warehouse-item-${testWarehousePreparationRequest.requestId}'),
      ),
      findsOneWidget,
    );
    expect(harness.controller.problem?.code, 'REQUEST_TERMINAL');
    harness.dispose();
  });
}

final _warehouseUser = AuthenticatedUser(
  id: testUser.id,
  username: 'warehouse-operator',
  fullName: 'Depo Operatörü',
  employeeNo: null,
  roles: const ['WAREHOUSE_OPERATOR'],
);

final _supervisorUser = AuthenticatedUser(
  id: testUser.id,
  username: 'supervisor',
  fullName: 'Vardiya Amiri',
  employeeNo: null,
  roles: const ['SUPERVISOR'],
);

Future<void> _openLegacyConsole(WidgetTester tester, _Harness harness) async {
  await harness.controller.loadWarehousePreparationRequests();
  await tester.pumpWidget(
    MaterialApp(
      home: WarehousePreparationScreen(controller: harness.controller),
    ),
  );
  await tester.pumpAndSettle();
}

Future<_Harness> _harness(
  WidgetTester tester, {
  FakePdaRepository? repository,
}) async {
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
  return _Harness(controller, actualRepository);
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}
