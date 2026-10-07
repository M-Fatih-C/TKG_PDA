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

  testWidgets('active production batch exit keeps the main product', (
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
    for (final id in [testAltPartId, _secondAltId]) {
      await tester.ensureVisible(find.byKey(Key('alt-part-$id')));
      await tester.tap(find.byKey(Key('alt-part-$id')));
      await tester.pumpAndSettle();
    }
    expect(harness.controller.selectedAltParts, hasLength(2));

    await tester.tap(find.byKey(const Key('batch-clear-cart')));
    await tester.pumpAndSettle();

    expect(harness.controller.batchMode, isFalse);
    expect(harness.controller.selectedAltParts, isEmpty);
    expect(harness.controller.notice, isNull);
    expect(find.textContaining('parça değiştirilemez'), findsNothing);
    expect(
      harness.controller.selectedMainPart?.partId,
      harness.controller.production.active!.mainPartId,
    );
    expect(harness.controller.containerPositions, hasLength(1));
  });

  testWidgets('Tekli Talep leaves batch mode without an error notice', (
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
    expect(find.text('Tekli Talep'), findsOneWidget);

    await tester.tap(find.byKey(const Key('enter-batch-selection')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));

    expect(harness.controller.batchMode, isFalse);
    expect(harness.controller.notice, isNull);
    expect(find.textContaining('parça değiştirilemez'), findsNothing);
    expect(find.byKey(const Key('notice-banner')), findsNothing);
  });

  testWidgets('legacy batch exit still clears the part selection', (
    tester,
  ) async {
    final repository = FakePdaRepository()
      ..partOptionsValue = [_describedMainPart];
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
    expect(controller.production.active, isNull);
    await controller.selectMainPartOption(_describedMainPart);
    expect(controller.selectedMainPart?.partId, testMainPartId);
    controller.enterBatchSelection();
    controller.exitBatchSelection();
    expect(controller.batchMode, isFalse);
    expect(controller.selectedMainPart, isNull);
    expect(controller.notice, isNull);
  });

  testWidgets('1024x600 keeps one transport section under Mamul Kasa', (
    tester,
  ) async {
    await _production(
      tester,
      size: const Size(1024, 600),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    _expectSideColumn(tester);
  });

  testWidgets('1280x800 keeps the same right-column hierarchy', (tester) async {
    await _production(
      tester,
      size: const Size(1280, 800),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    _expectSideColumn(tester);
  });

  testWidgets('a narrow layout stacks Mamul Kasa above transports', (
    tester,
  ) async {
    await _production(tester, size: const Size(360, 800));
    final mamul = tester.getTopLeft(find.text('Mamul Kasa'));
    final transports = tester.getTopLeft(
      find.byKey(const Key('active-transport-section')),
    );
    expect(transports.dy, greaterThan(mamul.dy));
    expect((transports.dx - mamul.dx).abs(), lessThan(80));
    expect(find.byKey(const Key('active-transport-section')), findsOneWidget);
    expect(find.text('Aktif taşıma yok'), findsOneWidget);
  });

  testWidgets('one active transport stays a compact card', (tester) async {
    await _production(
      tester,
      size: const Size(1024, 600),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    final card = tester.getSize(
      find.byKey(
        const Key('active-transport-90000000-0000-0000-0000-000000000001'),
      ),
    );
    expect(card.height, lessThan(220));
    expect(find.textContaining('%'), findsNothing);
    expect(find.byType(LinearProgressIndicator), findsNothing);
  });

  testWidgets('more than three transports scroll inside the section', (
    tester,
  ) async {
    await _production(
      tester,
      size: const Size(1024, 600),
      context: testContext(
        activeRequests: [
          for (var index = 0; index < 4; index++) _request(index),
        ],
      ),
    );
    final section = tester.getSize(
      find.byKey(const Key('active-transport-section')),
    );
    expect(section.height, lessThan(420));
    expect(find.text('Mamul Kasa'), findsOneWidget);
    expect(find.text('Kasa 1'), findsOneWidget);
    final fourth = find.byKey(const Key('active-transport-req-3'));
    if (fourth.evaluate().isEmpty) {
      await tester.scrollUntilVisible(
        fourth,
        120,
        scrollable: find.descendant(
          of: find.byKey(const Key('active-transport-section')),
          matching: find.byType(Scrollable),
        ),
      );
    }
    expect(find.byKey(const Key('active-transport-req-3')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('Mamul Kasa heading sits above the container cards', (
    tester,
  ) async {
    await _production(tester, size: const Size(1024, 600));
    final heading = tester.getRect(find.text('Mamul Kasa'));
    final card = tester.getRect(find.text('Kasa 1'));
    expect(heading.bottom, lessThanOrEqualTo(card.top));
  });

  testWidgets('an unavailable mamul card does not reserve a tall blank well', (
    tester,
  ) async {
    await _production(
      tester,
      size: const Size(1024, 600),
      positions: const [
        ContainerPosition(
          positionNumber: 1,
          label: 'Kasa 1',
          fullAction: ContainerActionAvailability(
            enabled: false,
            status: 'BLOCKED',
            disabledCode: 'NO_AREA',
            disabledMessage:
                'Bitmiş ürün teslim alanı henüz yapılandırılmamış.',
          ),
          emptyAction: ContainerActionAvailability(
            enabled: false,
            status: 'BLOCKED',
            disabledCode: 'NO_AREA',
            disabledMessage:
                'Bitmiş ürün teslim alanı henüz yapılandırılmamış.',
          ),
          transportState: 'READY',
          queuePosition: null,
          etaSeconds: null,
          etaState: 'UNAVAILABLE',
        ),
      ],
    );
    expect(
      find.text('Bitmiş ürün teslim alanı henüz yapılandırılmamış.'),
      findsOneWidget,
    );
    expect(
      tester.getSize(find.byKey(const Key('container-position-panel'))).height,
      lessThan(220),
    );
  });

  testWidgets('cancel still asks for confirmation before it is sent', (
    tester,
  ) async {
    final harness = await _production(
      tester,
      size: const Size(1024, 600),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    await tester.tap(
      find.byKey(const Key('cancel-90000000-0000-0000-0000-000000000001')),
    );
    await tester.pumpAndSettle();
    expect(find.text('İptal İsteği Gönder'), findsOneWidget);
    expect(harness.repository.cancelCount, 0);
    await tester.tap(find.byKey(const Key('confirm-cancel')));
    await tester.pumpAndSettle();
    expect(harness.repository.cancelCount, 1);
  });

  testWidgets('active production has no legacy kasa batch path', (
    tester,
  ) async {
    await _production(tester, size: const Size(1024, 600));
    expect(find.text('Kasa Doldu'), findsNothing);
    expect(find.text('Kasa Boş'), findsNothing);
    expect(find.text('Toplu Taşıma Modu Açık'), findsNothing);
  });

  testWidgets('1024x600 columns stay near an even split', (tester) async {
    await _production(
      tester,
      size: const Size(1024, 600),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    _expectSideColumn(tester);
    _expectBalancedColumns(tester, viewportWidth: 1024);
    final right = tester.getSize(
      find.byKey(const Key('production-right-column')),
    );
    expect(right.width, greaterThan(1024 * 0.44));
    expect(right.width, lessThan(560));
  });

  testWidgets('1280x800 columns stay near an even split', (tester) async {
    await _production(
      tester,
      size: const Size(1280, 800),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    _expectSideColumn(tester);
    _expectBalancedColumns(tester, viewportWidth: 1280);
  });

  testWidgets('a large desktop grows the right column past the old cap', (
    tester,
  ) async {
    await _production(
      tester,
      size: const Size(1920, 1080),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    _expectSideColumn(tester);
    _expectBalancedColumns(tester, viewportWidth: 1920);
    final right = tester.getSize(
      find.byKey(const Key('production-right-column')),
    );
    expect(right.width, greaterThan(560));
    final left = tester.getSize(
      find.byKey(const Key('production-left-column')),
    );
    expect(left.width / right.width, lessThan(1.2));
  });

  testWidgets('mamul cards stay two-up and centered inside a wide section', (
    tester,
  ) async {
    await _production(
      tester,
      size: const Size(1920, 1080),
      positions: [_readyPosition(1), _readyPosition(2)],
    );
    final first = tester.getRect(find.byKey(const Key('container-card-1')));
    final second = tester.getRect(find.byKey(const Key('container-card-2')));
    expect((first.top - second.top).abs(), lessThan(4));
    expect(second.left, greaterThan(first.right));
    expect((first.width - second.width).abs(), lessThan(2));
    expect(first.width, lessThanOrEqualTo(440));
    expect(first.height, inInclusiveRange(130, 210));
    final panel = tester.getRect(
      find.byKey(const Key('container-position-panel')),
    );
    final leftGap = first.left - panel.left;
    final rightGap = panel.right - second.right;
    expect((leftGap - rightGap).abs(), lessThan(28));
    final action = tester.getSize(
      find.byKey(const Key('direct-action-REMOVE_FULL-1')),
    );
    expect(action.height, greaterThanOrEqualTo(48));
    expect(action.width, greaterThanOrEqualTo(48));
  });

  testWidgets('short-terminal mamul action stays a glove-sized target', (
    tester,
  ) async {
    await _production(tester, size: const Size(1024, 600));
    final card = tester.getSize(find.byKey(const Key('container-card-1')));
    expect(card.height, inInclusiveRange(130, 155));
    final action = tester.getSize(
      find.byKey(const Key('direct-action-REMOVE_FULL-1')),
    );
    expect(action.height, greaterThanOrEqualTo(48));
    expect(action.width, greaterThanOrEqualTo(48));
    expect(tester.takeException(), isNull);
  });

  testWidgets('active transport cancel and detail keep glove-sized targets', (
    tester,
  ) async {
    await _production(
      tester,
      size: const Size(1280, 800),
      context: testContext(activeRequests: [testActiveRequest]),
    );
    final card = tester.getSize(
      find.byKey(
        const Key('active-transport-90000000-0000-0000-0000-000000000001'),
      ),
    );
    expect(card.height, lessThan(220));
    final title = tester.getRect(find.textContaining('#42'));
    final cancel = tester.getRect(
      find.byKey(const Key('cancel-90000000-0000-0000-0000-000000000001')),
    );
    final detail = tester.getRect(
      find.byKey(
        const Key('transport-detail-90000000-0000-0000-0000-000000000001'),
      ),
    );
    expect(cancel.height, greaterThan(47));
    expect(cancel.width, greaterThan(47));
    expect(detail.height, greaterThan(47));
    expect(detail.width, greaterThan(47));
    expect((title.center.dy - cancel.center.dy).abs(), lessThan(16));
    expect(find.byKey(const Key('active-transport-section')), findsOneWidget);
    expect(tester.takeException(), isNull);
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

void _expectBalancedColumns(
  WidgetTester tester, {
  required double viewportWidth,
}) {
  final left = tester.getSize(find.byKey(const Key('production-left-column')));
  final right = tester.getSize(
    find.byKey(const Key('production-right-column')),
  );
  expect(left.width / right.width, inInclusiveRange(0.98, 1.16));
  expect(right.width, greaterThan(viewportWidth * 0.40));
  expect(left.width, lessThan(viewportWidth * 0.60));
  expect(tester.takeException(), isNull);
}

ContainerPosition _readyPosition(int number) => ContainerPosition(
  positionNumber: number,
  label: 'Kasa $number',
  fullAction: const ContainerActionAvailability(
    enabled: true,
    status: 'READY',
    disabledCode: null,
    disabledMessage: null,
  ),
  emptyAction: const ContainerActionAvailability(
    enabled: false,
    status: 'READY',
    disabledCode: 'NOT_APPLICABLE',
    disabledMessage: 'Alan boşaltma şu an uygun değil.',
  ),
  transportState: 'READY',
  queuePosition: null,
  etaSeconds: null,
  etaState: 'UNAVAILABLE',
);

void _expectSideColumn(WidgetTester tester) {
  final alt = tester.getTopLeft(find.text('ALT PARÇA TALEPLERİ'));
  final mamul = tester.getTopLeft(find.text('Mamul Kasa'));
  final transports = tester.getTopLeft(
    find.byKey(const Key('active-transport-section')),
  );
  final kasa = tester.getTopLeft(find.text('Kasa 1'));
  expect(find.byKey(const Key('active-transport-section')), findsOneWidget);
  expect(mamul.dx, greaterThan(alt.dx + 80));
  expect(transports.dx, greaterThan(alt.dx + 80));
  expect(kasa.dy, greaterThan(mamul.dy));
  expect(transports.dy, greaterThan(kasa.dy));
  expect(tester.takeException(), isNull);
}

ActiveTransportRequest _request(int index) => ActiveTransportRequest.fromJson({
  'requestId': 'req-$index',
  'requestNo': 20 + index,
  'operation': 'BRING_EMPTY',
  'status': 'QUEUED',
  'isMine': true,
  'priority': 100,
  'createdAt': '2026-10-06T08:00:00Z',
  'partCode': '67115BN050-10',
  'canCancel': false,
  'execution': {'state': 'WAITING_FOR_ROBOT'},
});

Future<_Harness> _production(
  WidgetTester tester, {
  Size size = const Size(1280, 800),
  OperationalContext? context,
  List<ContainerPosition>? positions,
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
    ..containerPositionsValue =
        positions ??
        [
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
            'containerTypeId': '70000000-0000-0000-0000-000000000002',
            'containerCode': 'KASA',
            'relationType': 'ALT_PART',
            'lotQuantity': null,
            'displayOrder': 3,
          },
          {
            'id': '80000000-0000-0000-0000-000000000022',
            'partId': _thirdAltId,
            'partCode': 'A00293203',
            'containerTypeId': '70000000-0000-0000-0000-000000000003',
            'containerCode': 'KASA',
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
