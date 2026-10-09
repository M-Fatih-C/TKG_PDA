import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/features/warehouse/preparation_areas_screen.dart';
import 'package:tkg_pda/features/warehouse/preparation_coordinator.dart';

import '../support/fakes.dart';

/// The warehouse tablet's preparation screen. These exercise what the operator
/// can and cannot do from the four cards; nothing here proves a pallet was
/// really placed, which only the server can decide.
void main() {
  late FakePdaRepository repository;
  late PreparationCoordinator coordinator;

  setUp(() {
    repository = FakePdaRepository();
    coordinator = PreparationCoordinator(repository);
  });

  tearDown(() => coordinator.dispose());

  Future<void> pump(
    WidgetTester tester, {
    Size size = const Size(1280, 800),
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(
        home: PreparationAreasScreen(coordinator: coordinator, onBack: () {}),
      ),
    );
    await coordinator.refresh();
    await tester.pumpAndSettle();
  }

  testWidgets('a free area offers only the fill action', (tester) async {
    repository.preparationAreasValue = [
      fakeArea(number: 1),
      fakeArea(number: 2),
      fakeArea(number: 3),
      fakeArea(number: 4),
    ];
    await pump(tester);

    expect(find.byKey(const Key('preparation-area-1')), findsOneWidget);
    expect(find.byKey(const Key('fill-area-1')), findsOneWidget);
    expect(find.text('Talep Seç'), findsWidgets);
    expect(find.byKey(const Key('ready-area-1')), findsNothing);
    expect(find.byKey(const Key('collected-area-1')), findsNothing);
  });

  testWidgets('a placed pallet can be confirmed or withdrawn', (tester) async {
    repository.preparationAreasValue = [
      fakeArea(
        number: 1,
        pallet: fakePallet(
          load: 'FULL',
          partId: 'part-1',
          partCode: '67185BN050-40',
          transportRequestId: 'request-1',
          requestNo: 36,
          targetMachineCode: 'SH-01-A',
          rowVersion: 3,
        ),
      ),
    ];
    await pump(tester);

    expect(find.text('67185BN050-40'), findsOneWidget);
    expect(find.text('Hedef: SH-01-A'), findsOneWidget);
    expect(find.byKey(const Key('ready-area-1')), findsOneWidget);
    expect(find.byKey(const Key('withdraw-area-1')), findsOneWidget);
    expect(find.byKey(const Key('collected-area-1')), findsNothing);
  });

  testWidgets('confirming sends the row version the card was showing', (
    tester,
  ) async {
    repository.preparationAreasValue = [
      fakeArea(
        number: 1,
        pallet: fakePallet(
          load: 'FULL',
          partId: 'part-1',
          transportRequestId: 'request-1',
          rowVersion: 7,
        ),
      ),
    ];
    await pump(tester);

    await tester.tap(find.byKey(const Key('ready-area-1')));
    await tester.pumpAndSettle();

    expect(repository.readiedPallets, hasLength(1));
    expect(repository.readiedPallets.single['expectedRowVersion'], 7);
  });

  testWidgets('unmatched empty stock cannot be confirmed ready', (
    tester,
  ) async {
    // Matching staged stock to a demand is the server's job at the ready step;
    // an unmatched pallet has nothing to be ready for.
    repository.preparationAreasValue = [
      fakeArea(number: 1, pallet: fakePallet(load: 'EMPTY', rowVersion: 1)),
    ];
    await pump(tester);

    final button = tester.widget<FilledButton>(
      find.byKey(const Key('ready-area-1')),
    );
    expect(button.onPressed, isNull);
    expect(find.text('Talebe bağlı değil'), findsOneWidget);
  });

  testWidgets('a ready pallet offers the collected action', (tester) async {
    repository.preparationAreasValue = [
      fakeArea(
        number: 1,
        pallet: fakePallet(
          load: 'FULL',
          partId: 'part-1',
          transportRequestId: 'request-1',
          readyAt: DateTime.utc(2026, 1, 1),
          rowVersion: 4,
        ),
      ),
    ];
    await pump(tester);

    expect(find.byKey(const Key('collected-area-1')), findsOneWidget);
    expect(find.byKey(const Key('ready-area-1')), findsNothing);

    await tester.tap(find.byKey(const Key('collected-area-1')));
    await tester.pumpAndSettle();

    expect(repository.releasedPallets.single['reason'], 'COLLECTED');
    expect(repository.releasedPallets.single['expectedRowVersion'], 4);
  });

  testWidgets('filling an area prepares the demand the server published', (
    tester,
  ) async {
    repository.preparationAreasValue = [fakeArea(number: 1)];
    repository.warehouseDemandsValue = [fakeDemand(requestNo: 36)];
    await pump(tester);

    await tester.tap(find.byKey(const Key('fill-area-1')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('demand-option-36')));
    await tester.pumpAndSettle();

    expect(repository.placedPallets, hasLength(1));
    final placed = repository.placedPallets.single;
    expect(placed['areaId'], 'area-1');
    expect(placed['load'], 'FULL');
    expect(placed['partId'], 'part-1');
    expect(placed['transportRequestId'], 'request-36');
    expect(placed['idempotencyKey'], isNotEmpty);
  });

  testWidgets('a demand already being prepared is not offered again', (
    tester,
  ) async {
    repository.preparationAreasValue = [fakeArea(number: 1)];
    repository.warehouseDemandsValue = [
      fakeDemand(requestNo: 36, claimedByAreaNumber: 2),
    ];
    await pump(tester);

    await tester.tap(find.byKey(const Key('fill-area-1')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('demand-picker-empty')), findsOneWidget);
    expect(find.byKey(const Key('demand-option-36')), findsNothing);
  });

  testWidgets('the side list shows the part, the machine and the wait', (
    tester,
  ) async {
    repository.preparationAreasValue = [fakeArea(number: 1)];
    repository.warehouseDemandsValue = [
      fakeDemand(
        requestNo: 36,
        requestedAt: DateTime.now().subtract(const Duration(minutes: 12)),
      ),
    ];
    await pump(tester);

    expect(find.byKey(const Key('demand-36')), findsOneWidget);
    expect(find.textContaining('SH-01-A · Talep #36'), findsOneWidget);
    expect(find.textContaining('12 dk'), findsOneWidget);
  });

  testWidgets('a demand with no server timestamp says so', (tester) async {
    repository.preparationAreasValue = [fakeArea(number: 1)];
    repository.warehouseDemandsValue = [
      fakeDemand(requestNo: 36, requestedAt: null),
    ];
    await pump(tester);

    expect(find.text('İstenme zamanı bilinmiyor'), findsOneWidget);
  });

  testWidgets('the operator is warned before the last area goes', (
    tester,
  ) async {
    repository.preparationAreasValue = [
      fakeArea(number: 1, pallet: fakePallet()),
      fakeArea(number: 2, pallet: fakePallet(id: 'pallet-2')),
      fakeArea(number: 3, pallet: fakePallet(id: 'pallet-3')),
      fakeArea(number: 4),
    ];
    await pump(tester);

    expect(
      find.byKey(const Key('preparation-capacity-warning')),
      findsOneWidget,
    );
    expect(find.textContaining('Son bir hazırlık alanı'), findsOneWidget);
  });

  testWidgets('a full store says no new preparation is possible', (
    tester,
  ) async {
    repository.preparationAreasValue = [
      for (var i = 1; i <= 4; i++)
        fakeArea(
          number: i,
          pallet: fakePallet(id: 'pallet-$i'),
        ),
    ];
    await pump(tester);

    expect(find.textContaining('tamamı dolu'), findsOneWidget);
  });

  testWidgets('a read failure is reported and not hidden', (tester) async {
    repository.preparationAreasError = const ConnectivityException();
    await pump(tester);

    expect(find.byKey(const Key('preparation-error')), findsOneWidget);
    expect(find.textContaining('Bağlantı yok'), findsOneWidget);
  });

  testWidgets('an ambiguous submission tells the operator to check', (
    tester,
  ) async {
    repository.preparationAreasValue = [
      fakeArea(
        number: 1,
        pallet: fakePallet(
          load: 'FULL',
          partId: 'part-1',
          transportRequestId: 'request-1',
          rowVersion: 1,
        ),
      ),
    ];
    repository.markPalletReadyError = const AmbiguousMutationException(
      idempotencyKey: 'key-1',
    );
    await pump(tester);

    await tester.tap(find.byKey(const Key('ready-area-1')));
    await tester.pumpAndSettle();

    expect(find.textContaining('doğrulanamadı'), findsOneWidget);
  });

  testWidgets('the screen stays usable on a narrow tablet', (tester) async {
    repository.preparationAreasValue = [
      for (var i = 1; i <= 4; i++) fakeArea(number: i),
    ];
    repository.warehouseDemandsValue = [fakeDemand(requestNo: 36)];
    await pump(tester, size: const Size(800, 1280));

    expect(find.byKey(const Key('preparation-area-1')), findsOneWidget);
    expect(find.byKey(const Key('demand-36')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
