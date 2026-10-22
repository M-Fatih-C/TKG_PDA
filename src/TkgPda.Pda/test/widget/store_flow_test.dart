import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/data/models/store_location.dart';
import 'package:tkg_pda/features/warehouse/store_coordinator.dart';
import 'package:tkg_pda/features/warehouse/store_screen.dart';

import '../support/fakes.dart';

/// The finished-goods screen. It reports what the robot left and records what
/// the operator took; nothing here can add a container, because only a
/// completing transport puts one in the store.
void main() {
  late FakePdaRepository repository;
  late StoreCoordinator coordinator;

  setUp(() {
    repository = FakePdaRepository();
    coordinator = StoreCoordinator(repository);
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
        home: StoreScreen(coordinator: coordinator, onBack: () {}),
      ),
    );
    await coordinator.refresh();
    await tester.pumpAndSettle();
  }

  testWidgets('with no locations defined the screen says so plainly', (
    tester,
  ) async {
    // Which physical locations the store has is still a customer decision.
    await pump(tester);

    expect(find.byKey(const Key('store-empty')), findsOneWidget);
    expect(find.textContaining('müşteri tarafından'), findsOneWidget);
  });

  testWidgets('an empty location offers no removal action', (tester) async {
    repository.storeLocationsValue = [fakeStoreLocation(capacity: 3)];
    await pump(tester);

    expect(find.textContaining('Bu alan boş.'), findsOneWidget);
    expect(find.byKey(const Key('clear-location-STORE-1')), findsNothing);
    expect(find.byKey(const Key('record-empty-STORE-1')), findsOneWidget);
  });

  testWidgets('a delivered container shows what it is and where from', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(
        capacity: 3,
        units: [
          fakeStoreUnit(
            deliveredAt: DateTime.now().subtract(const Duration(minutes: 8)),
          ),
        ],
      ),
    ];
    await pump(tester);

    expect(find.text('67116BN050-40'), findsOneWidget);
    expect(find.textContaining('SH-01-A'), findsOneWidget);
    expect(find.textContaining('Kap: 4322'), findsOneWidget);
    expect(find.textContaining('Talep #42'), findsOneWidget);
    expect(find.textContaining('8 dk'), findsOneWidget);
  });

  testWidgets('removing one carries the version the row was showing', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(
        capacity: 3,
        units: [fakeStoreUnit(id: 'unit-9', rowVersion: 4)],
      ),
    ];
    await pump(tester);

    await tester.tap(find.byKey(const Key('remove-unit-unit-9')));
    await tester.pumpAndSettle();

    expect(repository.removedUnits.single['unitId'], 'unit-9');
    expect(repository.removedUnits.single['expectedRowVersion'], 4);
  });

  testWidgets('multiple pallets require physical reconciliation', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(
        capacity: 3,
        units: [
          fakeStoreUnit(id: 'a'),
          fakeStoreUnit(id: 'b'),
        ],
      ),
    ];
    await pump(tester);

    expect(find.text('Fiziksel sayım gerekli'), findsOneWidget);
    final button = tester.widget<OutlinedButton>(
      find.byKey(const Key('clear-location-STORE-1')),
    );
    expect(button.onPressed, isNull);
    expect(repository.clearedLocations, isEmpty);
  });

  testWidgets('a full location warns the operator to empty it', (tester) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(
        capacity: 2,
        units: [
          fakeStoreUnit(id: 'a'),
          fakeStoreUnit(id: 'b'),
        ],
      ),
    ];
    await pump(tester);

    expect(find.byKey(const Key('store-full-warning')), findsOneWidget);
    expect(find.textContaining('doldu'), findsOneWidget);
    expect(find.text('2 / 2'), findsOneWidget);
  });

  testWidgets(
    'an empty pallet in a single-pallet area stays available without a clear warning',
    (tester) async {
      repository.storeLocationsValue = [
        fakeStoreLocation(
          capacity: 1,
          units: [fakeStoreUnit(id: 'empty-pallet', loadState: 'EMPTY')],
        ),
      ];
      await pump(tester);

      expect(find.text('BOŞ KASA VAR'), findsOneWidget);
      expect(find.byKey(const Key('store-full-warning')), findsNothing);
      expect(find.byKey(const Key('store-nearly-full-warning')), findsNothing);
      expect(coordinator.fullLocations, isEmpty);
      expect(coordinator.nearlyFullLocations, isEmpty);
    },
  );

  testWidgets('one short of capacity warns before the store stops', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(
        capacity: 3,
        units: [
          fakeStoreUnit(),
          fakeStoreUnit(id: 'b'),
        ],
      ),
    ];
    await pump(tester);

    expect(find.byKey(const Key('store-nearly-full-warning')), findsOneWidget);
    expect(find.textContaining('dolmak üzere'), findsOneWidget);
  });

  testWidgets('an unknown capacity shows a count and never warns', (
    tester,
  ) async {
    // Nobody has said how many containers fit, so there is no denominator and
    // no warning to give.
    repository.storeLocationsValue = [
      fakeStoreLocation(
        capacity: null,
        units: [
          fakeStoreUnit(),
          fakeStoreUnit(id: 'b'),
          fakeStoreUnit(id: 'c'),
        ],
      ),
    ];
    await pump(tester);

    expect(find.text('3 kasa'), findsOneWidget);
    expect(find.byKey(const Key('store-full-warning')), findsNothing);
    expect(find.byKey(const Key('store-nearly-full-warning')), findsNothing);
  });

  testWidgets('a container with no server timestamp says so', (tester) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(units: [fakeStoreUnit(deliveredAt: null)]),
    ];
    await pump(tester);

    expect(find.textContaining('Geliş zamanı bilinmiyor'), findsOneWidget);
  });

  testWidgets('a container that arrives between polls is flagged as new', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(units: [fakeStoreUnit(id: 'a')]),
    ];
    await pump(tester);
    expect(find.byKey(const Key('acknowledge-arrivals')), findsNothing);

    repository.storeLocationsValue = [
      fakeStoreLocation(
        units: [
          fakeStoreUnit(id: 'a'),
          fakeStoreUnit(id: 'b'),
        ],
      ),
    ];
    await coordinator.refresh(silent: true);
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('acknowledge-arrivals')), findsOneWidget);
    expect(find.text('Gördüm'), findsOneWidget);
    expect(find.textContaining(' yeni'), findsNothing);

    await tester.tap(find.byKey(const Key('acknowledge-arrivals')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('acknowledge-arrivals')), findsNothing);
  });

  testWidgets('a read failure is reported and not hidden', (tester) async {
    repository.storeLocationsError = const ConnectivityException();
    await pump(tester);

    expect(find.byKey(const Key('store-error')), findsOneWidget);
    expect(find.textContaining('Bağlantı yok'), findsOneWidget);
  });

  testWidgets('an ambiguous removal tells the operator to check', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(units: [fakeStoreUnit(id: 'unit-9')]),
    ];
    repository.removeStoreUnitError = const AmbiguousMutationException(
      idempotencyKey: 'key-1',
    );
    await pump(tester);

    await tester.tap(find.byKey(const Key('remove-unit-unit-9')));
    await tester.pumpAndSettle();

    expect(find.textContaining('doğrulanamadı'), findsOneWidget);
  });

  testWidgets('the screen stays usable on a narrow tablet', (tester) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(code: 'STORE-1', capacity: 2, units: [fakeStoreUnit()]),
      fakeStoreLocation(code: 'STORE-2'),
    ];
    await pump(tester, size: const Size(800, 1280));

    expect(find.byKey(const Key('store-location-STORE-1')), findsOneWidget);
    expect(find.byKey(const Key('store-location-STORE-2')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('nineteen locations use a readable grid without overflow', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      for (var number = 1; number <= 19; number++)
        fakeStoreLocation(code: 'AREA-${number.toString().padLeft(2, '0')}'),
    ];
    await pump(tester, size: const Size(1024, 600));

    expect(find.byKey(const Key('store-location-list')), findsOneWidget);
    expect(find.text('ALAN BOŞ'), findsWidgets);
    final first = tester.getTopLeft(
      find.byKey(const Key('store-location-AREA-01')),
    );
    final second = tester.getTopLeft(
      find.byKey(const Key('store-location-AREA-02')),
    );
    expect((second.dy - first.dy).abs(), lessThan(2));
    expect(second.dx, greaterThan(first.dx));
    await tester.scrollUntilVisible(
      find.byKey(const Key('store-location-AREA-19')),
      300,
      scrollable: find.byType(Scrollable).first,
    );
    expect(find.byKey(const Key('store-location-AREA-19')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('1280x800 shows three finished-goods columns', (tester) async {
    repository.storeLocationsValue = [
      for (var number = 1; number <= 19; number++)
        fakeStoreLocation(code: 'AREA-${number.toString().padLeft(2, '0')}'),
    ];
    await pump(tester, size: const Size(1280, 800));

    final wideFirst = tester.getTopLeft(
      find.byKey(const Key('store-location-AREA-01')),
    );
    final wideThird = tester.getTopLeft(
      find.byKey(const Key('store-location-AREA-03')),
    );
    expect((wideThird.dy - wideFirst.dy).abs(), lessThan(2));
    expect(tester.takeException(), isNull);
  });

  testWidgets('store states use words and icons, not color alone', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(code: 'OFF', isEnabled: false),
      fakeStoreLocation(code: 'VACANT'),
      fakeStoreLocation(
        code: 'EMPTY',
        units: [fakeStoreUnit(id: 'empty', loadState: 'EMPTY')],
      ),
      fakeStoreLocation(
        code: 'FULL',
        units: [fakeStoreUnit(id: 'full', loadState: 'FULL')],
      ),
      fakeStoreLocation(
        code: 'UNKNOWN',
        units: [fakeStoreUnit(id: 'unknown', loadState: 'UNKNOWN')],
      ),
    ];
    await pump(tester, size: const Size(1600, 900));

    expect(find.text('DEVRE DIŞI'), findsOneWidget);
    expect(find.text('ALAN BOŞ'), findsOneWidget);
    expect(find.text('BOŞ KASA VAR'), findsOneWidget);
    expect(find.text('DOLU MAMUL'), findsOneWidget);
    expect(find.text('DURUM BİLİNMİYOR'), findsOneWidget);
    expect(find.byIcon(Icons.block), findsOneWidget);
    expect(find.byKey(const Key('record-empty-OFF')), findsNothing);
    expect(find.text('Tümünü Aldım (1)'), findsNothing);
    expect(find.byKey(const Key('remove-unit-full')), findsOneWidget);
  });

  test('an omitted load state stays UNKNOWN', () {
    const unit = StoreUnit(
      id: 'unit-default',
      transportRequestId: null,
      requestNo: null,
      partId: null,
      partCode: '67116BN050-40',
      containerTypeId: null,
      containerCode: null,
      machineId: null,
      machineCode: null,
      deliveredAt: null,
      rowVersion: 0,
    );
    expect(unit.loadState, 'UNKNOWN');
    expect(unit.loadPresentation, 'Durum bilinmiyor');
    expect(unit.loadPresentation, isNot('Dolu mamul'));

    final parsed = StoreUnit.fromJson({'id': 'unit-json', 'rowVersion': 1});
    expect(parsed.loadState, 'UNKNOWN');
    expect(parsed.loadPresentation, 'Durum bilinmiyor');
  });

  testWidgets('UNKNOWN load is never presented as a full container', (
    tester,
  ) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(units: [fakeStoreUnit(loadState: 'UNKNOWN')]),
    ];
    await pump(tester);

    expect(find.textContaining('Durum bilinmiyor'), findsWidgets);
    expect(find.text('DURUM BİLİNMİYOR'), findsOneWidget);
    expect(find.textContaining('Dolu mamul'), findsNothing);
    expect(find.text('DOLU MAMUL'), findsNothing);

    await tester.tap(find.byKey(const Key('store-state-STORE-1')));
    await tester.pumpAndSettle();
    expect(find.text('Durum bilinmiyor'), findsWidgets);
    expect(find.textContaining('Dolu mamul'), findsNothing);
  });

  testWidgets('FULL and EMPTY keep their own labels', (tester) async {
    repository.storeLocationsValue = [
      fakeStoreLocation(
        code: 'FULL',
        units: [fakeStoreUnit(id: 'full', loadState: 'FULL')],
      ),
      fakeStoreLocation(
        code: 'EMPTY',
        units: [fakeStoreUnit(id: 'empty', loadState: 'EMPTY')],
      ),
    ];
    await pump(tester, size: const Size(1280, 800));

    expect(find.textContaining('Dolu mamul'), findsWidgets);
    expect(find.textContaining('Boş kasa'), findsWidgets);
    expect(find.text('DOLU MAMUL'), findsOneWidget);
    expect(find.text('BOŞ KASA VAR'), findsOneWidget);
  });
}
