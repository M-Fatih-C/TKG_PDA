import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';

import '../support/fakes.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  Future<AppController> setupController({
    FakePdaRepository? repository,
  }) async {
    final repo = repository ?? FakePdaRepository();
    final store = MemoryDeviceCodeStore(testDeviceCode);
    final controller = AppController(
      repository: repo,
      deviceCodeStore: store,
      session: SessionMemory(),
      enablePolling: false,
      enableProductionWorkflow: false, // Legacy transport regression.
    );
    await controller.initialize();
    await controller.login('operator');
    await controller.selectMachine(testMachine);
    await controller.selectMainPartOption(testPartOptions.first);
    return controller;
  }

  group('Operator UI Integrity — Phase 1 Requirements A-H', () {
    test(
      'A. container action pins machine/variant/part/container/action before request',
      () async {
        final repo = FakePdaRepository();
        final controller = await setupController(repository: repo);
        final position = controller.containerPositions.first;

        repo.createCompleter = Completer<TransportResult>();
        final future = controller.submitContainerAction(
          position,
          'FULL',
        );

        expect(controller.isMutationInFlight, isTrue);
        final pending = controller.activePendingOperation;
        expect(pending, isNotNull);
        expect(pending!.machineId, testMachine.id);
        expect(pending.machineCode, testMachine.code);
        expect(pending.variantId, testVariantV1Id);
        expect(pending.variantCode, 'V1');
        expect(pending.partId, testMainPartId);
        expect(pending.partCode, '71550BN000-10-HAOS');
        expect(pending.logicalContainerNumber, position.positionNumber);
        expect(pending.operation, 'FULL');
        expect(pending.idempotencyKey, isNotEmpty);
        expect(pending.startedAt, isNotNull);

        repo.createCompleter!.complete(
          const TransportResult(
            id: '90000000-0000-0000-0000-000000000001',
            requestNo: 101,
            status: 'QUEUED',
            operation: 'REMOVE_FULL',
            idempotentReplay: false,
          ),
        );
        await future;

        expect(controller.isMutationInFlight, isFalse);
        expect(controller.activePendingOperation, isNull);
      },
    );

    test(
      'B. context change attempt during mutation does not alter in-flight mutation identity',
      () async {
        final repo = FakePdaRepository();
        final controller = await setupController(repository: repo);
        final position = controller.containerPositions.first;

        repo.createCompleter = Completer<TransportResult>();
        final future = controller.submitContainerAction(
          position,
          'FULL',
        );

        final pinned = controller.activePendingOperation;
        expect(pinned, isNotNull);
        final initialKey = pinned!.idempotencyKey;

        // Attempt context-changing actions while mutation is in flight
        await controller.selectMachine(
          const MachineSummary(
            id: 'other-machine-id',
            code: 'SH-02',
            displayName: 'SH-02',
            displayOrder: 2,
            permissions: MachinePermissions(
              canView: true,
              canOperate: true,
              canChangeVariant: true,
              canManageSlots: false,
            ),
          ),
        );
        expect(controller.selectedMachine!.id, testMachine.id);

        await controller.selectMainPartOption(testPartOptions[1]);
        expect(controller.selectedMainPart?.partId, testMainPartId);

        await controller.enterTasks();
        expect(controller.stage, AppStage.operation);

        await controller.logout();
        expect(controller.isAuthenticated, isTrue);

        // Verify pinned operation remained unaltered
        expect(controller.activePendingOperation, isNotNull);
        expect(controller.activePendingOperation!.idempotencyKey, initialKey);
        expect(controller.activePendingOperation!.machineId, testMachine.id);
        expect(controller.activePendingOperation!.variantId, testVariantV1Id);
        expect(controller.activePendingOperation!.partId, testMainPartId);
        expect(
          controller.activePendingOperation!.logicalContainerNumber,
          position.positionNumber,
        );

        repo.createCompleter!.complete(
          const TransportResult(
            id: '90000000-0000-0000-0000-000000000001',
            requestNo: 101,
            status: 'QUEUED',
            operation: 'REMOVE_FULL',
            idempotentReplay: false,
          ),
        );
        await future;
      },
    );

    test(
      'C. ambiguous retry uses identical idempotencyKey and pinned context',
      () async {
        final repo = FakePdaRepository();
        final controller = await setupController(repository: repo);
        final position = controller.containerPositions.first;

        repo.createError = const AmbiguousMutationException(
          idempotencyKey: 'transient-key',
        );

        await controller.submitContainerAction(
          position,
          'FULL',
        );

        expect(controller.activePendingOperation, isNull);
        final ambiguous = controller.ambiguousPendingOperation;
        expect(ambiguous, isNotNull);
        final pinnedKey = ambiguous!.idempotencyKey;
        final pinnedMachine = ambiguous.machineId;
        final pinnedVariant = ambiguous.variantId;
        final pinnedPart = ambiguous.partId;

        // Reset error to allow retry to succeed
        repo.createError = null;

        await controller.retryAmbiguousTransport();

        // Check repository was called with the exact pinned idempotencyKey and context
        expect(repo.lastIdempotencyKey, pinnedKey);
        expect(repo.lastCommand?.machineId, pinnedMachine);
        expect(repo.lastCommand?.variantId, pinnedVariant);
        expect(repo.lastCommand?.partId, pinnedPart);
        expect(controller.ambiguousPendingOperation, isNull);
      },
    );

    test(
      'D. Remove All pins variant before request',
      () async {
        final repo = FakePdaRepository();
        final controller = await setupController(repository: repo);

        repo.removeAllCompleter = Completer<RemoveAllResult>();
        final future = controller.removeAllContainers();

        expect(controller.isMutationInFlight, isTrue);
        final pending = controller.activePendingOperation;
        expect(pending, isNotNull);
        expect(pending!.operation, 'REMOVE_ALL');
        expect(pending.machineId, testMachine.id);
        expect(pending.variantId, testVariantV1Id);
        expect(pending.variantCode, 'V1');
        expect(pending.idempotencyKey, isNotEmpty);

        repo.removeAllCompleter!.complete(
          const RemoveAllResult(
            idempotentReplay: false,
            ignoredVacantPositions: [],
            itemCount: 2,
          ),
        );
        await future;

        expect(repo.lastRemoveAllVariantId, testVariantV1Id);
        expect(repo.lastRemoveAllMachineId, testMachine.id);
        expect(controller.isMutationInFlight, isFalse);
        expect(controller.activePendingOperation, isNull);
      },
    );

    test(
      'E. offline disables every mutation entry point consistently via mutationGate',
      () async {
        final repo = FakePdaRepository();
        final controller = await setupController(repository: repo);

        // Force offline via connectivity error during refresh
        repo.contextError = const ConnectivityException();
        await controller.refreshContext(showBusy: true);

        expect(controller.offline, isTrue);
        expect(controller.mutationGate.canMutate, isFalse);
        expect(controller.mutationGate.reason, MutationGateReason.offline);
        expect(controller.mutationGate.message, isNotNull);

        repo.containerActionCount = 0;
        repo.removeAllCount = 0;
        repo.markReadyCount = 0;
        repo.cancelCount = 0;

        await controller.submitContainerAction(
          controller.containerPositions.first,
          'FULL',
        );
        expect(repo.containerActionCount, 0);

        await controller.removeAllContainers();
        expect(repo.removeAllCount, 0);

        await controller.markPreparationReady('req-prep-1');
        expect(repo.markReadyCount, 0);

        await controller.cancelTransport(testActiveRequest);
        expect(repo.cancelCount, 0);
      },
    );

    test(
      'F. warehouse Ready obeys mutationGate',
      () async {
        final repo = FakePdaRepository();
        final store = MemoryDeviceCodeStore(testDeviceCode);
        final controller = AppController(
          repository: repo,
          deviceCodeStore: store,
          session: SessionMemory(),
          enablePolling: false,
          enableProductionWorkflow: false, // Legacy transport regression.
        );
        await controller.initialize();
        await controller.login('operator');
        await controller.enterWarehousePreparation();

        repo.warehouseRequestsValue = [
          WarehousePreparationRequest(
            requestId: 'req-warehouse-01',
            requestNo: 1,
            operation: 'BRING_EMPTY',
            machineId: 'm1',
            machineCode: 'SH-01',
            machineDisplayName: 'SH-01 Hücresi',
            partCode: 'PART-01',
            containerTypeCode: 'KASA-01',
            sourceSlotCode: 'SRC-01',
            sourceSlotDisplayName: 'Kaynak Konum',
            destinationSlotCode: 'DST-01',
            destinationSlotDisplayName: 'Hedef Konum',
            createdAt: DateTime.now(),
            queuedAt: DateTime.now(),
          ),
        ];

        // While online, mutationGate allows mutation
        expect(controller.mutationGate.canMutate, isTrue);
        await controller.markPreparationReady('req-warehouse-01');
        expect(repo.markReadyCount, 1);

        // Force offline
        repo.warehouseRequestsError = const ConnectivityException();
        await controller.loadWarehousePreparationRequests();
        expect(controller.mutationGate.canMutate, isFalse);

        await controller.markPreparationReady('req-warehouse-01');
        // markReadyCount remains 1 (call blocked by mutationGate)
        expect(repo.markReadyCount, 1);
      },
    );

    test(
      'G. active transport cancel driven by backend canCancel',
      () {
        final cancelableItem = ActiveTransportRequest.fromJson({
          'requestId': 'req-01',
          'requestNo': 1,
          'operation': 'BRING_EMPTY',
          'status': 'QUEUED',
          'priority': 0,
          'createdAt': '2026-09-09T10:00:00Z',
          'canCancel': true,
          'execution': {'stage': 'QUEUED'},
        });

        final nonCancelableItem = ActiveTransportRequest.fromJson({
          'requestId': 'req-02',
          'requestNo': 2,
          'operation': 'REMOVE_FULL',
          'status': 'ACTIVE',
          'priority': 0,
          'createdAt': '2026-09-09T10:00:00Z',
          'canCancel': false,
          'execution': {'stage': 'ACTIVE'},
        });

        expect(cancelableItem.canCancel, isTrue);
        expect(nonCancelableItem.canCancel, isFalse);
      },
    );

    test(
      'H. assert no physical identifiers (sourceSlotId, destinationSlotId, workSite, RDS IDs) in request payload',
      () async {
        final repo = FakePdaRepository();
        final controller = await setupController(repository: repo);
        final position = controller.containerPositions.first;

        await controller.submitContainerAction(
          position,
          'FULL',
        );

        final cmd = repo.lastCommand;
        expect(cmd, isNotNull);
        expect(cmd!.sourceSlotId, isNull);
        expect(cmd.destinationSlotId, isNull);

        // Check PendingOperation serialized JSON has no physical routing/RDS fields
        final pending = PendingOperation(
          machineId: 'm-01',
          machineCode: 'SH-01',
          variantId: 'v-01',
          variantCode: 'V1',
          layoutId: 'l-01',
          productGroupId: 'pg-01',
          partId: 'p-01',
          logicalContainerNumber: 1,
          operation: 'FULL',
          idempotencyKey: 'idemp-1',
          startedAt: DateTime.now(),
        );

        final json = pending.toJson();
        expect(json.containsKey('sourceSlotId'), isFalse);
        expect(json.containsKey('destinationSlotId'), isFalse);
        expect(json.containsKey('workSite'), isFalse);
        expect(json.containsKey('work_site'), isFalse);
        expect(json.containsKey('rdsId'), isFalse);
        expect(json.containsKey('seerId'), isFalse);
      },
    );
  });
}
