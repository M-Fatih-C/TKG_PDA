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
  testWidgets('1024x600 active production does not overflow', (tester) async {
    await _production(tester, size: const Size(1024, 600));
    expect(find.text('ÜRETİM AKTİF'), findsOneWidget);
    expect(find.text('ALT PARÇA TALEPLERİ'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('1280x800 active production does not overflow', (tester) async {
    await _production(tester, size: const Size(1280, 800));
    expect(find.text('ÜRETİM AKTİF'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('a narrow phone layout does not overflow', (tester) async {
    await _production(tester, size: const Size(360, 640));
    expect(find.text('ÜRETİM AKTİF'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('an alt part tile shows its description and requests on tap', (
    tester,
  ) async {
    final harness = await _production(tester, size: const Size(1024, 600));
    await tester.scrollUntilVisible(
      find.text('Sol Destek Braketi'),
      200,
      scrollable: find.byType(Scrollable).first,
    );
    expect(find.text('Sol Destek Braketi'), findsOneWidget);
    expect(find.text('TALEP ET'), findsWidgets);
    expect(find.text('Kasa Doldu'), findsNothing);
    expect(find.text('Kasa Boş'), findsNothing);

    final product = harness.controller.production.active!.mainPartId;
    await tester.tap(find.byKey(const Key('alt-part-$testAltPartId')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(harness.repository.partDemandCount, 1);
    expect(harness.repository.lastPartDemandIds, [testAltPartId]);
    expect(harness.controller.production.active!.mainPartId, product);
    expect(find.byKey(const Key('notice-banner')), findsNothing);
    expect(find.textContaining('talebi oluşturuldu'), findsOneWidget);
  });

  testWidgets('multi-select counts selected alt parts, not the legacy cart', (
    tester,
  ) async {
    final harness = await _production(tester, size: const Size(1024, 900));
    await tester.scrollUntilVisible(
      find.byKey(const Key('enter-batch-selection')),
      200,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.tap(find.byKey(const Key('enter-batch-selection')));
    await tester.pumpAndSettle();
    for (final id in [testAltPartId, _secondAltId, _thirdAltId]) {
      await tester.ensureVisible(find.byKey(Key('alt-part-$id')));
      await tester.tap(find.byKey(Key('alt-part-$id')));
      await tester.pumpAndSettle();
    }

    expect(harness.controller.selectedAltParts, hasLength(3));
    expect(harness.controller.batchCart, isEmpty);
    expect(harness.repository.partDemandCount, 0);
    expect(find.text('3 Alt Parça Seçildi'), findsOneWidget);
    expect(
      tester.widget<Text>(find.byKey(const Key('selected-alt-count'))).data,
      '3',
    );
    expect(find.text('3 Alt Parçayı Gönder'), findsOneWidget);
  });

  testWidgets('mamul shows only the server-enabled primary action', (
    tester,
  ) async {
    await _production(tester);
    expect(find.text('Kasam Doldu'), findsOneWidget);
    expect(find.text('Alan Boş'), findsNothing);
    expect(find.byKey(const Key('direct-action-BRING_EMPTY-1')), findsNothing);
  });

  testWidgets('active transport lifecycle labels are industrially readable', (
    tester,
  ) async {
    await _production(
      tester,
      context: testContext(activeRequests: [testActiveRequest]),
    );
    final label = tester.widget<Text>(find.text('TALEP').first);
    expect(label.style?.fontSize, isNot(9));
    expect(label.style?.fontSize, greaterThanOrEqualTo(12));
    expect(find.text('HAZIRLIK'), findsWidgets);
    expect(find.text('TAMAM'), findsWidgets);
  });

  testWidgets('a business error stays in the persistent banner', (
    tester,
  ) async {
    final harness = await _production(tester);
    harness.repository.partDemandError = const ApiProblem(
      status: 409,
      code: 'PART_REQUEST_ALREADY_ACTIVE',
      detail: 'Alt parça talebi reddedildi.',
      correlationId: null,
    );
    await tester.tap(find.byKey(const Key('alt-part-$testAltPartId')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('notice-banner')), findsOneWidget);
    expect(find.textContaining('aktif talep zaten var'), findsOneWidget);
  });
}

const _secondAltId = '70000000-0000-0000-0000-000000000021';
const _thirdAltId = '70000000-0000-0000-0000-000000000022';

Future<_Harness> _production(
  WidgetTester tester, {
  Size size = const Size(1280, 800),
  OperationalContext? context,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  final repository = FakePdaRepository()
    ..productionValue = ProductionSession(
      id: 'production-1',
      machineId: testMachine.id,
      variantId: testVariantV1Id,
      productGroupId: testProductGroupId,
      mainPartId: testMainPartId,
      startedAt: DateTime.utc(2026, 9, 15, 5, 42),
    )
    ..partOptionsValue = [_describedMainPart]
    ..containerPositionsValue = [
      const ContainerPosition(
        positionNumber: 1,
        label: 'Kasa 1',
        fullAction: ContainerActionAvailability(
          enabled: true,
          status: 'READY',
          disabledCode: null,
          disabledMessage: null,
        ),
        emptyAction: ContainerActionAvailability(
          enabled: false,
          status: 'READY',
          disabledCode: 'NOT_APPLICABLE',
          disabledMessage: 'Alan boşaltma şu an uygun değil.',
        ),
        transportState: 'READY',
        queuePosition: null,
        etaSeconds: null,
        etaState: 'UNAVAILABLE',
      ),
    ];
  repository.contextValue =
      context ??
      testContext(
        additionalParts: const [
          {
            'id': '80000000-0000-0000-0000-000000000021',
            'partId': _secondAltId,
            'partCode': 'A00293202',
            'containerTypeId': null,
            'containerCode': null,
            'relationType': 'ALT_PART',
            'lotQuantity': null,
            'displayOrder': 3,
          },
          {
            'id': '80000000-0000-0000-0000-000000000022',
            'partId': _thirdAltId,
            'partCode': 'A00293203',
            'containerTypeId': null,
            'containerCode': null,
            'relationType': 'ALT_PART',
            'lotQuantity': null,
            'displayOrder': 4,
          },
        ],
      );
  final controller = AppController(
    repository: repository,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
  );
  addTearDown(controller.dispose);
  await controller.initialize();
  await controller.login('00123');
  await controller.selectMachine(testMachine);
  await tester.pumpWidget(TkgPdaApp(controller: controller));
  await tester.pumpAndSettle();
  return _Harness(controller, repository);
}

final _describedMainPart = MachineMainPartOption(
  partId: testMainPartId,
  partCode: 'ABC-123',
  description: 'Ürün Adı',
  relatedParts: const [
    MachineRelatedPart(
      partId: testAltPartId,
      partCode: 'A00293201',
      description: 'Sol Destek Braketi',
      relationType: 'ALT_PART',
    ),
    MachineRelatedPart(
      partId: _secondAltId,
      partCode: 'A00293202',
      description: 'Sağ Destek Braketi',
      relationType: 'ALT_PART',
    ),
    MachineRelatedPart(
      partId: _thirdAltId,
      partCode: 'A00293203',
      description: 'Orta Bağlantı',
      relationType: 'ALT_PART',
    ),
  ],
);

class _Harness {
  const _Harness(this.controller, this.repository);
  final AppController controller;
  final FakePdaRepository repository;
}
