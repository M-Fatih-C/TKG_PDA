import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/features/warehouse/warehouse_notification_coordinator.dart';
import 'package:tkg_pda/features/warehouse/widgets/warehouse_notification_widgets.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('WarehouseNotificationCoordinator unit tests', () {
    late WarehouseNotificationCoordinator coordinator;

    setUp(() {
      coordinator = WarehouseNotificationCoordinator();
    });

    tearDown(() {
      coordinator.dispose();
    });

    test('new demand notification creates valid demand entry', () {
      coordinator.notifyDemand(
        requestId: 'req-1',
        machineCode: 'SH-01-A',
        partCode: '67185BN050-40',
        needsMaterial: true,
        requestNo: 42,
      );

      expect(coordinator.notifications, hasLength(1));
      final notif = coordinator.notifications.first;
      expect(notif.type, WarehouseNotificationType.demand);
      expect(notif.machineCode, 'SH-01-A');
      expect(notif.partCode, '67185BN050-40');
      expect(notif.message, contains('Yarı Mamul (Dolu Kasa)'));
      expect(notif.message, contains('Talep #42'));
      expect(notif.targetStage, AppStage.preparationAreas);
      expect(coordinator.unreadCount, 1);
      expect(coordinator.hasUnread, isTrue);
      expect(coordinator.activeBanner?.id, notif.id);
    });

    test('same demand event is never added twice on routine poll', () {
      coordinator.notifyDemand(
        requestId: 'req-1',
        machineCode: 'SH-01-A',
        partCode: '67185BN050-40',
        needsMaterial: true,
        requestNo: 42,
      );

      // Subsequent refresh/poll with same requestId
      coordinator.notifyDemand(
        requestId: 'req-1',
        machineCode: 'SH-01-A',
        partCode: '67185BN050-40',
        needsMaterial: true,
        requestNo: 42,
      );

      expect(coordinator.notifications, hasLength(1));
    });

    test(
      'finished-goods demand notification opens the finished-goods store',
      () {
        coordinator.notifyDemand(
          requestId: 'req-empty-1',
          machineCode: 'SH-01-A',
          partCode: '67185BN050-40',
          needsMaterial: false,
          requestNo: 43,
          targetStage: AppStage.finishedGoodsStore,
        );

        final notification = coordinator.notifications.single;
        expect(notification.message, contains('Boş Kasa'));
        expect(notification.targetStage, AppStage.finishedGoodsStore);
      },
    );

    test('new delivery notification creates valid delivery entry', () {
      coordinator.notifyDelivery(
        unitId: 'unit-99',
        locationName: 'STORE-A1',
        partCode: '67116BN050-40',
        machineCode: 'SH-01-A',
        requestNo: 15,
      );

      expect(coordinator.notifications, hasLength(1));
      final notif = coordinator.notifications.first;
      expect(notif.type, WarehouseNotificationType.delivery);
      expect(notif.title, contains('STORE-A1'));
      expect(notif.message, contains('67116BN050-40'));
      expect(notif.targetStage, AppStage.finishedGoodsStore);
      expect(coordinator.activeBanner?.id, notif.id);
    });

    test(
      'capacity warnings trigger correctly and clear when capacity frees up',
      () {
        // 1 free area left
        coordinator.notifyPreparationCapacity(freeAreaCount: 1, totalAreas: 4);
        expect(coordinator.notifications, hasLength(1));
        expect(
          coordinator.notifications.first.type,
          WarehouseNotificationType.capacity,
        );
        expect(
          coordinator.notifications.first.message,
          contains('Son bir hazırlık alanı'),
        );

        // 0 free areas left
        coordinator.notifyPreparationCapacity(freeAreaCount: 0, totalAreas: 4);
        expect(coordinator.notifications, hasLength(2));
        expect(
          coordinator.notifications.first.message,
          contains('tamamı dolu'),
        );

        // Area is cleared
        coordinator.clearPreparationCapacityAlert();
        // Can warn again after state reset
        coordinator.notifyPreparationCapacity(freeAreaCount: 1, totalAreas: 4);
        expect(coordinator.notifications, hasLength(3));
      },
    );

    test('sound toggle changes soundEnabled property', () {
      expect(coordinator.soundEnabled, isTrue);
      coordinator.toggleSound(false);
      expect(coordinator.soundEnabled, isFalse);
      coordinator.toggleSound();
      expect(coordinator.soundEnabled, isTrue);
    });

    test('markAllRead and clearAll update state properly', () {
      coordinator.notifyDemand(
        requestId: 'req-1',
        machineCode: 'SH-01',
        partCode: 'P1',
        needsMaterial: false,
        requestNo: 1,
      );
      coordinator.notifyDemand(
        requestId: 'req-2',
        machineCode: 'SH-02',
        partCode: 'P2',
        needsMaterial: true,
        requestNo: 2,
      );

      expect(coordinator.unreadCount, 2);
      coordinator.markAllRead();
      expect(coordinator.unreadCount, 0);
      expect(coordinator.notifications.every((n) => n.isRead), isTrue);

      coordinator.clearAll();
      expect(coordinator.notifications, isEmpty);
      expect(coordinator.activeBanner, isNull);
    });
  });

  group('WarehouseNotification UI Widgets tests', () {
    late WarehouseNotificationCoordinator coordinator;

    setUp(() {
      coordinator = WarehouseNotificationCoordinator();
    });

    tearDown(() {
      coordinator.dispose();
    });

    testWidgets(
      'top notification banner appears and tapping action navigates',
      (tester) async {
        AppStage? navigatedStage;
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(
              body: WarehouseNotificationBanner(
                coordinator: coordinator,
                onNavigate: (stage) {
                  navigatedStage = stage;
                },
              ),
            ),
          ),
        );

        expect(
          find.byKey(const Key('warehouse-notification-banner')),
          findsNothing,
        );

        coordinator.notifyDemand(
          requestId: 'req-test-1',
          machineCode: 'SH-01-A',
          partCode: '67185BN050-40',
          needsMaterial: true,
          requestNo: 101,
        );
        await tester.pumpAndSettle();

        expect(
          find.byKey(const Key('warehouse-notification-banner')),
          findsOneWidget,
        );
        expect(find.text('YENİ TALEP'), findsOneWidget);
        expect(find.textContaining('SH-01-A'), findsOneWidget);
        expect(find.textContaining('67185BN050-40'), findsOneWidget);

        final actionButton = find.byKey(
          const Key('warehouse-notification-action'),
        );
        expect(actionButton, findsOneWidget);
        await tester.tap(actionButton);
        await tester.pumpAndSettle();

        expect(navigatedStage, AppStage.preparationAreas);
        expect(coordinator.activeBanner, isNull);
        expect(
          find.byKey(const Key('warehouse-notification-banner')),
          findsNothing,
        );
      },
    );

    testWidgets('bell icon shows badge count and opens notification sheet', (
      tester,
    ) async {
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            appBar: AppBar(
              actions: [
                WarehouseNotificationBell(
                  coordinator: coordinator,
                  onNavigate: (_) {},
                ),
              ],
            ),
          ),
        ),
      );

      expect(
        find.byKey(const Key('warehouse-notification-badge')),
        findsNothing,
      );

      coordinator.notifyDelivery(
        unitId: 'unit-1',
        locationName: 'STORE-1',
        partCode: '67116BN050-40',
        machineCode: 'SH-01-A',
        requestNo: 55,
      );
      await tester.pumpAndSettle();

      expect(
        find.byKey(const Key('warehouse-notification-badge')),
        findsOneWidget,
      );
      expect(find.text('1'), findsOneWidget);
      expect(find.textContaining(' yeni'), findsNothing);

      await tester.tap(find.byKey(const Key('warehouse-notifications-button')));
      await tester.pumpAndSettle();

      expect(
        find.byKey(const Key('warehouse-notification-sheet')),
        findsOneWidget,
      );
      expect(find.text('Bildirimler'), findsOneWidget);
      expect(find.text('YENİ TESLİMAT'), findsOneWidget);
      expect(find.textContaining('STORE-1'), findsOneWidget);

      // Test sound toggle button in sheet
      expect(coordinator.soundEnabled, isTrue);
      await tester.tap(
        find.byKey(const Key('warehouse-notification-sound-toggle')),
      );
      await tester.pumpAndSettle();
      expect(coordinator.soundEnabled, isFalse);

      // Advance clock past the banner auto-dismiss duration to clear timers
      await tester.pump(const Duration(seconds: 6));
    });
  });
}
