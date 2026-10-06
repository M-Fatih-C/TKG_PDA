import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
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

  testWidgets('clearing a location names it once', (tester) async {
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

    expect(find.text('Tümünü aldım (2)'), findsOneWidget);
    await tester.tap(find.byKey(const Key('clear-location-STORE-1')));
    await tester.pumpAndSettle();

    expect(repository.clearedLocations, ['location-STORE-1']);
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
    expect(find.text('1 yeni'), findsOneWidget);

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
}
