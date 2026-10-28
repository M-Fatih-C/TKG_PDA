import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';

import '../support/fakes.dart';

void main() {
  testWidgets(
    'Unknown runtime business state is not classified as missing configuration',
    (tester) async {
      const blocked = ContainerActionAvailability(
        enabled: false,
        status: 'READY',
        disabledCode: 'BUSINESS_RULE_NOT_CONFIGURED',
        disabledMessage: 'Kasa konumunun mevcut durumu bilinmiyor.',
      );
      final repository = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: blocked,
            emptyAction: blocked,
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      final harness = await _harness(tester, repository: repository);
      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);
      expect(find.text('Hazır'), findsNothing);
      expect(find.text('Yapılandırma Eksik'), findsNothing);
      expect(find.text('Hazır Değil'), findsOneWidget);
      for (final operation in ['REMOVE_FULL', 'BRING_EMPTY']) {
        expect(
          tester.widget<ElevatedButton>(
            find.byKey(Key('direct-action-$operation-1')),
          ).enabled,
          isFalse,
        );
      }
      harness.dispose();
    },
  );

  group('TKG PDA — Mamul Kasa Two-Action UI Consistency', () {
    testWidgets('A — vacant position: both visible, Kasa Doldu disabled, Boş Kasa Getir enabled', (
      tester,
    ) async {
      final repository = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'CONTAINER_POSITION_VACANT',
              disabledMessage: 'Bu konumda kasa bulunmuyor.',
            ),
            emptyAction: ContainerActionAvailability(
              enabled: true,
              status: 'READY',
              disabledCode: null,
              disabledMessage: null,
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      final harness = await _harness(tester, repository: repository);

      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);

      final fullBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      );
      final emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(fullBtn.enabled, isFalse);
      expect(emptyBtn.enabled, isTrue);
      expect(find.text('Hazır'), findsOneWidget);

      harness.dispose();
    });

    testWidgets('B — occupied empty container: both visible, Kasa Doldu enabled, Boş Kasa Getir disabled', (
      tester,
    ) async {
      final repository = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: true,
              status: 'READY',
              disabledCode: null,
              disabledMessage: null,
            ),
            emptyAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'CONTAINER_POSITION_OCCUPIED',
              disabledMessage: 'Bu konumda zaten bir kasa bulunuyor.',
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      final harness = await _harness(tester, repository: repository);

      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);

      final fullBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      );
      final emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(fullBtn.enabled, isTrue);
      expect(emptyBtn.enabled, isFalse);
      expect(find.text('Hazır'), findsOneWidget);

      harness.dispose();
    });

    testWidgets('C — occupied full container: both visible, preserves backend FULL semantics', (
      tester,
    ) async {
      final repository = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: true,
              status: 'READY',
              disabledCode: null,
              disabledMessage: null,
            ),
            emptyAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'CONTAINER_POSITION_OCCUPIED',
              disabledMessage: 'Bu konumda zaten bir kasa bulunuyor.',
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      final harness = await _harness(tester, repository: repository);

      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);

      final fullBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      );
      final emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(fullBtn.enabled, isTrue);
      expect(emptyBtn.enabled, isFalse);

      await tester.tap(find.byKey(const Key('direct-action-REMOVE_FULL-1')));
      await tester.pump(const Duration(milliseconds: 500));

      expect(repository.containerActionCount, 1);
      expect(repository.lastCommand?.operation, 'REMOVE_FULL');

      harness.dispose();
    });

    testWidgets('D — Finished Goods missing: both visible, FG disabled message, Boş Kasa Getir independent, Yapılandırma Eksik badge when 0 actions', (
      tester,
    ) async {
      const fgMessage =
          'Bitmiş ürün teslim alanı henüz yapılandırılmamış. Sistem yöneticisine başvurun.';

      // Sub-case 1: Vacant slot with FG missing -> Boş Kasa Getir is enabled independently!
      final repoIndependent = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'FINISHED_GOODS_DESTINATION_NOT_CONFIGURED',
              disabledMessage: fgMessage,
            ),
            emptyAction: ContainerActionAvailability(
              enabled: true,
              status: 'READY',
              disabledCode: null,
              disabledMessage: null,
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      var harness = await _harness(tester, repository: repoIndependent);

      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);
      var fullBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      );
      var emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(fullBtn.enabled, isFalse);
      expect(emptyBtn.enabled, isTrue);
      expect(find.text('Hazır'), findsOneWidget);
      harness.dispose();

      // Sub-case 2: Occupied slot with FG missing -> 0 actions available.
      // Must NOT show green 'Hazır'. Must show 'Yapılandırma Eksik' and FG message.
      final repoBlocked = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'FINISHED_GOODS_DESTINATION_NOT_CONFIGURED',
              disabledMessage: fgMessage,
            ),
            emptyAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'CONTAINER_POSITION_OCCUPIED',
              disabledMessage: 'Bu konumda zaten bir kasa bulunuyor.',
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      harness = await _harness(tester, repository: repoBlocked);

      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);
      fullBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      );
      emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(fullBtn.enabled, isFalse);
      expect(emptyBtn.enabled, isFalse);

      // Badge must NOT be 'Hazır'
      expect(find.text('Hazır'), findsNothing);
      expect(find.text('Yapılandırma Eksik'), findsOneWidget);
      expect(find.text(fgMessage), findsOneWidget);

      harness.dispose();
    });

    testWidgets('E — active transport: both buttons remain visible but disabled, status shown', (
      tester,
    ) async {
      final repository = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: false,
              status: 'IN_PROGRESS',
              disabledCode: 'SLOT_BUSY',
              disabledMessage: 'Bu kasa konumunda aktif bir taşıma talebi bulunuyor.',
            ),
            emptyAction: ContainerActionAvailability(
              enabled: false,
              status: 'IN_PROGRESS',
              disabledCode: 'SLOT_BUSY',
              disabledMessage: 'Bu kasa konumunda aktif bir taşıma talebi bulunuyor.',
            ),
            transportState: 'IN_PROGRESS',
            queuePosition: null,
            etaSeconds: 120,
            etaState: 'AVAILABLE',
          ),
        ];
      final harness = await _harness(
        tester,
        repository: repository,
        hasActiveTransport: true,
      );

      expect(find.text('Kasa Doldu'), findsOneWidget);
      expect(find.text('Boş Kasa Getir'), findsOneWidget);

      final fullBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-REMOVE_FULL-1')),
      );
      final emptyBtn = tester.widget<ElevatedButton>(
        find.byKey(const Key('direct-action-BRING_EMPTY-1')),
      );
      expect(fullBtn.enabled, isFalse);
      expect(emptyBtn.enabled, isFalse);
      expect(find.textContaining('Devam ediyor'), findsOneWidget);

      harness.dispose();
    });

    testWidgets('F — two-position card: exactly 4 action buttons, both actions per card', (
      tester,
    ) async {
      final repository = FakePdaRepository()
        ..containerPositionsValue = [
          const ContainerPosition(
            positionNumber: 1,
            label: '1. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: true,
              status: 'READY',
              disabledCode: null,
              disabledMessage: null,
            ),
            emptyAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'CONTAINER_POSITION_OCCUPIED',
              disabledMessage: 'Bu konumda zaten bir kasa bulunuyor.',
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
          const ContainerPosition(
            positionNumber: 2,
            label: '2. Kasa',
            fullAction: ContainerActionAvailability(
              enabled: false,
              status: 'READY',
              disabledCode: 'CONTAINER_POSITION_VACANT',
              disabledMessage: 'Bu konumda kasa bulunmuyor.',
            ),
            emptyAction: ContainerActionAvailability(
              enabled: true,
              status: 'READY',
              disabledCode: null,
              disabledMessage: null,
            ),
            transportState: 'READY',
            queuePosition: null,
            etaSeconds: null,
            etaState: 'UNAVAILABLE',
          ),
        ];
      final harness = await _harness(tester, repository: repository);

      expect(find.text('Kasa Doldu'), findsNWidgets(2));
      expect(find.text('Boş Kasa Getir'), findsNWidgets(2));

      expect(find.byKey(const Key('direct-action-REMOVE_FULL-1')), findsOneWidget);
      expect(find.byKey(const Key('direct-action-BRING_EMPTY-1')), findsOneWidget);
      expect(find.byKey(const Key('direct-action-REMOVE_FULL-2')), findsOneWidget);
      expect(find.byKey(const Key('direct-action-BRING_EMPTY-2')), findsOneWidget);

      // Card 1: full enabled, empty disabled
      expect(
        tester.widget<ElevatedButton>(find.byKey(const Key('direct-action-REMOVE_FULL-1'))).enabled,
        isTrue,
      );
      expect(
        tester.widget<ElevatedButton>(find.byKey(const Key('direct-action-BRING_EMPTY-1'))).enabled,
        isFalse,
      );

      // Card 2: full disabled, empty enabled
      expect(
        tester.widget<ElevatedButton>(find.byKey(const Key('direct-action-REMOVE_FULL-2'))).enabled,
        isFalse,
      );
      expect(
        tester.widget<ElevatedButton>(find.byKey(const Key('direct-action-BRING_EMPTY-2'))).enabled,
        isTrue,
      );

      harness.dispose();
    });

    testWidgets('G — shared-machine behavior: identical contract across SH-01-B, PAH-01-A, 412B', (
      tester,
    ) async {
      final machines = [
        MachineSummary(
          id: '30000000-0000-0000-0000-000000000002',
          code: 'SH-01-B',
          displayName: 'SH-01-B Pres',
          displayOrder: 2,
          permissions: const MachinePermissions(
            canView: true,
            canOperate: true,
            canChangeVariant: true,
            canManageSlots: false,
          ),
        ),
        MachineSummary(
          id: '30000000-0000-0000-0000-000000000003',
          code: 'PAH-01-A',
          displayName: 'PAH-01-A Hattı',
          displayOrder: 3,
          permissions: const MachinePermissions(
            canView: true,
            canOperate: true,
            canChangeVariant: true,
            canManageSlots: false,
          ),
        ),
        MachineSummary(
          id: '30000000-0000-0000-0000-000000000004',
          code: '412B',
          displayName: '412B Robot Hücresi',
          displayOrder: 4,
          permissions: const MachinePermissions(
            canView: true,
            canOperate: true,
            canChangeVariant: true,
            canManageSlots: false,
          ),
        ),
      ];

      for (final machine in machines) {
        final repository = FakePdaRepository()
          ..containerPositionsValue = [
            const ContainerPosition(
              positionNumber: 1,
              label: '1. Kasa',
              fullAction: ContainerActionAvailability(
                enabled: true,
                status: 'READY',
                disabledCode: null,
                disabledMessage: null,
              ),
              emptyAction: ContainerActionAvailability(
                enabled: false,
                status: 'READY',
                disabledCode: 'CONTAINER_POSITION_OCCUPIED',
                disabledMessage: 'Bu konumda zaten bir kasa bulunuyor.',
              ),
              transportState: 'READY',
              queuePosition: null,
              etaSeconds: null,
              etaState: 'UNAVAILABLE',
            ),
          ];

        final harness = await _harness(
          tester,
          repository: repository,
          machine: machine,
        );

        // Every machine gets exactly the same two buttons
        expect(find.text('Kasa Doldu'), findsOneWidget);
        expect(find.text('Boş Kasa Getir'), findsOneWidget);
        expect(
          tester.widget<ElevatedButton>(find.byKey(const Key('direct-action-REMOVE_FULL-1'))).enabled,
          isTrue,
        );
        expect(
          tester.widget<ElevatedButton>(find.byKey(const Key('direct-action-BRING_EMPTY-1'))).enabled,
          isFalse,
        );

        harness.dispose();
      }
    });
  });
}

Future<_Harness> _harness(
  WidgetTester tester, {
  required FakePdaRepository repository,
  MachineSummary? machine,
  bool hasActiveTransport = false,
}) async {
  final controller = AppController(
    repository: repository,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
    enableProductionWorkflow: false,
  );
  await controller.initialize();
  await tester.pumpWidget(
    TkgPdaApp(key: ValueKey(controller), controller: controller),
  );
  await controller.login('00123');
  await tester.pumpAndSettle();
  await controller.selectMachine(machine ?? testMachine);
  await controller.selectMainPartOption(testMainPartOption);
  if (hasActiveTransport) {
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));
  } else {
    await tester.pumpAndSettle();
  }
  return _Harness(controller, repository);
}

class _Harness {
  const _Harness(this.controller, this.repository);

  final AppController controller;
  final FakePdaRepository repository;

  void dispose() => controller.dispose();
}
