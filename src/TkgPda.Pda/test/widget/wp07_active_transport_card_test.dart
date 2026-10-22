import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/features/operation/transport_action_panel.dart';

import '../support/fakes.dart';

ActiveTransportRequest _request({
  String id = 'req-1',
  int requestNo = 123,
  String operation = 'BRING_FULL',
  String? partCode = 'ABC-123',
  String? partDescription = 'Ön Sol Braket',
  String? relationType,
  String readinessState = 'NOT_REQUIRED',
  int? queuePosition = 2,
  int? ageSeconds = 253,
  int? executionElapsedSeconds,
  bool canCancel = true,
  String state = 'WAITING_FOR_ROBOT',
  String? chainState,
}) => ActiveTransportRequest.fromJson({
  'requestId': id,
  'requestNo': requestNo,
  'operation': operation,
  'status': 'QUEUED',
  'requestedBy': testUser.id,
  'requestedByUsername': testUser.username,
  'isMine': true,
  'priority': 100,
  'createdAt': '2026-10-06T08:00:00Z',
  'partCode': partCode,
  'partDescription': partDescription,
  'relationType': relationType,
  'readinessState': readinessState,
  'queuePosition': queuePosition,
  'ageSeconds': ageSeconds,
  'executionElapsedSeconds': executionElapsedSeconds,
  'canCancel': canCancel,
  'cancelDisabledMessage': canCancel
      ? null
      : 'Bu talebi iptal etme yetkiniz bulunmuyor.',
  'execution': {'state': state},
  'chainState': chainState,
});

Future<void> _pump(
  WidgetTester tester,
  List<ActiveTransportRequest> requests,
) async {
  final controller = AppController(
    repository: FakePdaRepository(),
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
    enableProductionWorkflow: false,
  );
  addTearDown(controller.dispose);
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(
          child: OperatorActiveTransportList(
            requests: requests,
            controller: controller,
          ),
        ),
      ),
    ),
  );
}

void main() {
  group('WP-07 active transport card', () {
    testWidgets(
      'A: shows request no, operation, part code, description and status',
      (tester) async {
        await _pump(tester, [_request()]);

        expect(find.textContaining('#123'), findsOneWidget);
        expect(find.textContaining('Dolu Kasa Getir'), findsOneWidget);
        expect(find.text('ABC-123'), findsOneWidget);
        expect(find.text('Ön Sol Braket'), findsOneWidget);
        expect(find.text('Robot bekleniyor'), findsOneWidget);
      },
    );

    testWidgets(
      'B: ALT_PART shows the Alt Parça badge; other relations do not',
      (tester) async {
        await _pump(tester, [_request(relationType: 'ALT_PART')]);
        expect(find.byKey(const Key('alt-part-badge')), findsOneWidget);
        expect(find.text('Alt Parça'), findsOneWidget);

        for (final relation in [
          'MAIN_PART',
          'RELATED',
          'CONSUMABLE',
          'OTHER',
          null,
        ]) {
          await _pump(tester, [_request(relationType: relation)]);
          expect(find.byKey(const Key('alt-part-badge')), findsNothing);
          // Enum text is never printed for the user.
          expect(find.text(relation ?? '-'), findsNothing);
        }
      },
    );

    testWidgets('C: shows the backend queue position as Talep Sırası', (
      tester,
    ) async {
      await _pump(tester, [_request(queuePosition: 3)]);
      expect(find.text('Talep Sırası: 3'), findsNothing);

      await tester.tap(find.byKey(const Key('transport-detail-req-1')));
      await tester.pumpAndSettle();

      expect(find.text('Talep Sırası: 3'), findsOneWidget);
      expect(find.textContaining('RDS'), findsNothing);
      expect(find.textContaining('Robot sırası'), findsNothing);
    });

    testWidgets('queue position is omitted when the server sent none', (
      tester,
    ) async {
      await _pump(tester, [_request(queuePosition: null)]);

      expect(find.textContaining('Talep Sırası'), findsNothing);
    });

    testWidgets(
      'D: formats the backend ageSeconds without reading the client clock',
      (tester) async {
        await _pump(tester, [_request(ageSeconds: 253)]);
        expect(find.text('Talep süresi: 04:13'), findsOneWidget);

        await _pump(tester, [_request(ageSeconds: 3725)]);
        expect(find.text('Talep süresi: 1:02:05'), findsOneWidget);

        // The projection is the only source: pumping time does not change it.
        await tester.pump(const Duration(minutes: 5));
        expect(find.text('Talep süresi: 1:02:05'), findsOneWidget);
      },
    );

    testWidgets('E: robot elapsed row is hidden without start evidence', (
      tester,
    ) async {
      await _pump(tester, [_request(executionElapsedSeconds: null)]);

      expect(find.textContaining('Robot çalışma süresi'), findsNothing);
    });

    testWidgets('F: robot elapsed row is shown when the server sent it', (
      tester,
    ) async {
      await _pump(tester, [
        _request(executionElapsedSeconds: 66, state: 'RUNNING'),
      ]);
      expect(find.text('Robot çalışma süresi: 01:06'), findsNothing);

      await tester.tap(find.byKey(const Key('transport-detail-req-1')));
      await tester.pumpAndSettle();

      expect(find.text('Robot çalışma süresi: 01:06'), findsOneWidget);
    });

    testWidgets('G: the card has no percentage or progress indicator', (
      tester,
    ) async {
      await _pump(tester, [
        _request(executionElapsedSeconds: 66, state: 'RUNNING', ageSeconds: 90),
      ]);

      final card = find.byKey(const Key('active-transport-req-1'));
      expect(card, findsOneWidget);
      expect(
        find.descendant(
          of: card,
          matching: find.byType(LinearProgressIndicator),
        ),
        findsNothing,
      );
      expect(
        find.descendant(
          of: card,
          matching: find.byType(CircularProgressIndicator),
        ),
        findsNothing,
      );
      expect(
        find.descendant(of: card, matching: find.byType(Slider)),
        findsNothing,
      );
      expect(
        find.descendant(of: card, matching: find.textContaining('%')),
        findsNothing,
      );
    });

    testWidgets('readiness is shown separately from the execution state', (
      tester,
    ) async {
      await _pump(tester, [
        _request(
          id: 'a',
          readinessState: 'WAITING_PREPARATION',
          state: 'QUEUED',
        ),
        _request(id: 'b', requestNo: 124, readinessState: 'READY'),
        _request(id: 'c', requestNo: 125, readinessState: 'NOT_REQUIRED'),
      ]);

      expect(find.text('Hazırlık: Depo hazırlığı bekleniyor'), findsOneWidget);
      expect(find.text('Hazırlık: Hazır'), findsOneWidget);
      expect(find.text('Hazırlık: Hazırlık gerekmiyor'), findsOneWidget);
    });

    testWidgets('ready pallet waiting for its machine slot has a clear label', (
      tester,
    ) async {
      await _pump(tester, [
        _request(readinessState: 'READY', chainState: 'WAITING_MACHINE_SLOT'),
      ]);
      await tester.tap(find.byKey(const Key('transport-detail-req-1')));
      await tester.pumpAndSettle();

      expect(find.text('Makine alanı bekleniyor'), findsOneWidget);
    });

    testWidgets('part fallbacks never print an id or an empty placeholder', (
      tester,
    ) async {
      await _pump(tester, [
        _request(id: 'no-desc', partDescription: ''),
        _request(
          id: 'no-part',
          requestNo: 124,
          partCode: null,
          partDescription: null,
        ),
      ]);

      expect(find.byKey(const Key('active-part-description')), findsNothing);
      expect(find.text('ABC-123'), findsOneWidget);
      expect(find.text('Parça bilgisi yok'), findsOneWidget);
      expect(find.textContaining('Tanımlı değil'), findsNothing);
    });

    testWidgets('H: canCancel=false hides the cancel button', (tester) async {
      await _pump(tester, [_request(canCancel: false)]);

      expect(find.byKey(const Key('cancel-req-1')), findsNothing);
      expect(find.text('Talebi İptal Et'), findsNothing);
    });

    testWidgets('I: canCancel=true keeps the cancel button', (tester) async {
      await _pump(tester, [_request(canCancel: true)]);

      expect(find.byKey(const Key('cancel-req-1')), findsOneWidget);
      expect(find.text('Talebi İptal Et'), findsOneWidget);
    });
  });
}
