import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/app/theme.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/features/login/login_screen.dart';
import 'package:tkg_pda/features/machines/machine_list_screen.dart';
import 'package:tkg_pda/features/operation/part_first_screen.dart';
import 'package:tkg_pda/features/tasks/operator_queue_screen.dart';
import 'package:tkg_pda/features/warehouse/preparation_areas_screen.dart';
import 'package:tkg_pda/features/warehouse/preparation_coordinator.dart';
import 'package:tkg_pda/features/warehouse/store_coordinator.dart';
import 'package:tkg_pda/features/warehouse/store_screen.dart';
import 'package:tkg_pda/features/warehouse/warehouse_home_screen.dart';

import '../support/fakes.dart';

Future<void> _capture(
  WidgetTester tester,
  GlobalKey key,
  String filename, {
  Size size = const Size(1280, 800),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  await tester.pumpAndSettle();

  await tester.runAsync(() async {
    final boundary =
        key.currentContext!.findRenderObject() as RenderRepaintBoundary;
    final image = await boundary.toImage(pixelRatio: 1.0);
    final byteData = await image.toByteData(format: ui.ImageByteFormat.png);
    final bytes = byteData!.buffer.asUint8List();

    final dirs = [
      Directory('build/screenshots'),
      Directory(
        'C:/Users/fatih.cetintas/.gemini/antigravity-ide/brain/96922041-45ef-4fc2-9659-897c75284418/screenshots',
      ),
    ];
    for (final d in dirs) {
      if (!d.existsSync()) d.createSync(recursive: true);
      File('${d.path}/$filename').writeAsBytesSync(bytes);
    }
  });
}

Widget _wrap(GlobalKey key, Widget child) {
  return RepaintBoundary(
    key: key,
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildPdaTheme(),
      home: child,
    ),
  );
}

void main() {
  final sizes = <String, Size>{
    '1280x800': const Size(1280, 800),
    '1366x768': const Size(1366, 768),
    'tablet_800x1280': const Size(800, 1280),
  };

  testWidgets('Capture all demonstration screens', (tester) async {
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final repository = FakePdaRepository();
    final deviceStore = MemoryDeviceCodeStore(testDeviceCode);
    final session = SessionMemory();
    final controller = AppController(
      repository: repository,
      deviceCodeStore: deviceStore,
      session: session,
      enablePolling: false,
      enableProductionWorkflow: true,
    );
    await controller.initialize();

    // 1. LOGIN SCREEN
    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(_wrap(key, LoginScreen(controller: controller)));
      await _capture(
        tester,
        key,
        '01_login_${entry.key}.png',
        size: entry.value,
      );
    }

    // Authenticate controller
    await controller.login('00123');

    // 2. MACHINE LIST SCREEN
    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(
        _wrap(key, MachineListScreen(controller: controller)),
      );
      await _capture(
        tester,
        key,
        '02_machine_list_${entry.key}.png',
        size: entry.value,
      );
    }

    // Select Machine and set up production + container positions
    await controller.selectMachine(testMachine);
    await controller.selectMainPartOption(testMainPartOption);
    await controller.startProduction();

    // 3. OPERATION SCREEN (PartFirst, Production Session, Container Positions)
    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(
        _wrap(key, Scaffold(body: PartFirstSelector(controller: controller))),
      );
      await _capture(
        tester,
        key,
        '03_operation_workspace_${entry.key}.png',
        size: entry.value,
      );
    }

    // 4. OPERATOR QUEUE SCREEN
    repository.contextValue = testContext(activeRequests: [testActiveRequest]);
    repository.operatorQueueValue = OperatorQueuePage(
      terminal: false,
      offset: 0,
      limit: 50,
      hasMore: false,
      items: [
        OperatorQueueItem(
          queuePosition: 1,
          machine: 'SH-01-A',
          part: '71550BN000-10-HAOS',
          logicalContainerNumber: 1,
          businessAction: 'BRING_EMPTY',
          requestedAt: DateTime.now().subtract(const Duration(minutes: 3)),
          state: 'IN_PROGRESS',
          etaSeconds: 120,
          etaState: 'AVAILABLE',
          requestId: 'req-1',
          canCancel: true,
        ),
        OperatorQueueItem(
          queuePosition: 2,
          machine: 'SH-01-A',
          part: '715A2BN400-50',
          logicalContainerNumber: 2,
          businessAction: 'REMOVE_FULL',
          requestedAt: DateTime.now().subtract(const Duration(minutes: 7)),
          state: 'QUEUED',
          etaSeconds: 480,
          etaState: 'AVAILABLE',
          requestId: 'req-2',
          canCancel: true,
        ),
      ],
    );
    await controller.refreshContext();
    await controller.loadOperatorQueue();

    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(
        _wrap(key, OperatorQueueScreen(controller: controller)),
      );
      await _capture(
        tester,
        key,
        '04_operator_queue_${entry.key}.png',
        size: entry.value,
      );
    }

    // 5. WAREHOUSE HOME SCREEN
    controller.warehouseNotifications.notifyDemand(
      requestId: 'req-screen-1',
      machineCode: 'SH-01-A',
      partCode: '71550BN000-10-HAOS',
      needsMaterial: true,
      requestNo: 52,
    );

    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(
        _wrap(key, WarehouseHomeScreen(controller: controller)),
      );
      await _capture(
        tester,
        key,
        '05_warehouse_home_${entry.key}.png',
        size: entry.value,
      );
    }

    // 6. PREPARATION AREAS SCREEN (4 Areas)
    final prepCoordinator = PreparationCoordinator(
      repository,
      notificationCoordinator: controller.warehouseNotifications,
    );
    repository.preparationAreasValue = [
      fakeArea(number: 1), // Free
      fakeArea(
        number: 2,
        pallet: fakePallet(
          load: 'FULL',
          partCode: '71550BN000-10-HAOS',
          targetMachineCode: 'SH-01-A',
          requestNo: 48,
          rowVersion: 1,
        ),
      ), // Placed / preparing
      fakeArea(
        number: 3,
        pallet: fakePallet(
          load: 'FULL',
          partCode: '715A2BN400-50',
          targetMachineCode: 'SH-02-B',
          requestNo: 50,
          readyAt: DateTime.now().subtract(const Duration(minutes: 5)),
          rowVersion: 2,
        ),
      ), // Ready
      fakeArea(
        number: 4,
        pallet: fakePallet(load: 'EMPTY', rowVersion: 1),
      ), // Staged empty
    ];
    repository.warehouseDemandsValue = [
      fakeDemand(
        requestNo: 52,
        machineCode: 'SH-01-A',
        partCode: '71550BN000-10-HAOS',
        requestedAt: DateTime.now().subtract(const Duration(minutes: 8)),
      ),
      fakeDemand(
        requestNo: 53,
        machineCode: 'SH-03-C',
        partCode: '715B3CN500-60',
        requestedAt: DateTime.now().subtract(const Duration(minutes: 15)),
      ),
    ];
    await prepCoordinator.refresh();

    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(
        _wrap(
          key,
          PreparationAreasScreen(
            coordinator: prepCoordinator,
            onBack: () {},
          ),
        ),
      );
      await _capture(
        tester,
        key,
        '06_preparation_areas_${entry.key}.png',
        size: entry.value,
      );
    }

    // 7. STORE SCREEN (Finished goods)
    final storeCoordinator = StoreCoordinator(
      repository,
      notificationCoordinator: controller.warehouseNotifications,
    );
    repository.storeLocationsValue = [
      fakeStoreLocation(
        code: 'DEP-01',
        displayName: 'A Bölgesi (Bitmiş Ürün)',
        capacity: 6,
        units: [
          fakeStoreUnit(
            id: 'u1',
            partCode: '71550BN000-10-HAOS',
            machineCode: 'SH-01-A',
            requestNo: 45,
          ),
          fakeStoreUnit(
            id: 'u2',
            partCode: '71550BN000-10-HAOS',
            machineCode: 'SH-01-A',
            requestNo: 46,
          ),
          fakeStoreUnit(
            id: 'u3',
            partCode: '715A2BN400-50',
            machineCode: 'SH-02-B',
            requestNo: 47,
          ),
        ],
      ),
      fakeStoreLocation(
        code: 'DEP-02',
        displayName: 'B Bölgesi (Sevkiyat Bekleyen)',
        capacity: 4,
        units: [
          fakeStoreUnit(
            id: 'u4',
            partCode: '715B3CN500-60',
            machineCode: 'SH-03-C',
            requestNo: 49,
          ),
        ],
      ),
    ];
    await storeCoordinator.refresh();

    for (final entry in sizes.entries) {
      final key = GlobalKey();
      await tester.pumpWidget(
        _wrap(
          key,
          StoreScreen(
            coordinator: storeCoordinator,
            onBack: () {},
          ),
        ),
      );
      await _capture(
        tester,
        key,
        '07_store_screen_${entry.key}.png',
        size: entry.value,
      );
    }

    controller.warehouseNotifications.dismissActiveBanner();
    await tester.pump(const Duration(seconds: 6));
    controller.dispose();
  });
}
