import 'dart:async';
import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:tkg_pda/app/app_controller.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/core/network/api_client.dart';
import 'package:tkg_pda/core/storage/device_code_store.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/data/repositories/pda_repository.dart';
import 'package:tkg_pda/features/operation/production_coordinator.dart';
import '../support/fakes.dart';

ProductionSession productionRecord({
  String id = 'production-1',
  bool ended = false,
  String status = 'ACTIVE',
}) => ProductionSession(
  id: id,
  machineId: testMachine.id,
  variantId: testVariantV1Id,
  productGroupId: testProductGroupId,
  mainPartId: testMainPartId,
  startedAt: DateTime.utc(2026, 9, 15, 8),
  endedAt: ended ? DateTime.utc(2026, 9, 15, 9) : null,
  status: ended ? 'ENDED' : status,
);

final testProductionEndRequest = ActiveTransportRequest(
  requestId: '90000000-0000-0000-0000-000000000099',
  requestNo: 99,
  operation: 'REMOVE_FULL',
  status: testActiveRequest.status,
  requestedBy: testActiveRequest.requestedBy,
  requestedByUsername: testActiveRequest.requestedByUsername,
  isMine: testActiveRequest.isMine,
  sourceSlotId: testActiveRequest.destinationSlotId,
  destinationSlotId: null,
  productGroupId: testActiveRequest.productGroupId,
  partId: testActiveRequest.partId,
  containerTypeId: testActiveRequest.containerTypeId,
  priority: 40,
  createdAt: testActiveRequest.createdAt,
  canCancel: testActiveRequest.canCancel,
  cancelDisabledCode: testActiveRequest.cancelDisabledCode,
  cancelDisabledMessage: testActiveRequest.cancelDisabledMessage,
  execution: testActiveRequest.execution,
  productionEnd: true,
);

void main() {
  test(
    'ENDING recovery with no active transport uses a fresh End key',
    () async {
      final ending = productionRecord(status: 'ENDING');
      final repo = FakePdaRepository()
        ..productionValue = ending
        ..productionEndSession = ending
        ..contextValue = testContext();
      final c = await controller(repo);
      expect(c.canStartProduction, isFalse);
      expect(c.canEndProduction, isTrue);
      await c.endProduction();
      await c.endProduction();
      expect(repo.productionEndIds, ['production-1', 'production-1']);
      expect(repo.productionEndKeys.toSet(), hasLength(2));
      expect(c.production.active!.isEnding, isTrue);
      expect(repo.productionStartCount, 0);
    },
  );

  test(
    'ENDING recovery stays blocked while a child transport is active',
    () async {
      final repo = FakePdaRepository()
        ..productionValue = productionRecord(status: 'ENDING')
        ..contextValue = testContext(activeRequests: [testActiveRequest]);
      final c = await controller(repo);
      expect(c.canEndProduction, isFalse);
      await c.endProduction();
      expect(repo.productionEndCount, 0);
      expect(repo.contextValue.activeTransportRequests, hasLength(1));
    },
  );

  test('alt part demand does not retarget the mamul container', () async {
    final repo = FakePdaRepository();
    final c = await controller(repo);
    await c.selectMainPartOption(testMainPartOption);
    await c.startProduction();
    expect(c.selectedEffectivePart?.partId, testMainPartId);

    await c.requestAltPart(testMainPartOption.relatedParts.first);

    expect(repo.partDemandCount, 1);
    expect(repo.lastPartDemandIds, [testAltPartId]);
    expect(repo.containerActionCount, 0);
    expect(c.selectedEffectivePart?.partId, testMainPartId);
    expect(c.mamulActionsReady, isTrue);
    expect(repo.lastContainerPositionPartId, testMainPartId);
  });

  test('alt part batch selection does not retarget mamul kasa', () async {
    final repo = FakePdaRepository();
    final c = await controller(repo);
    await c.selectMainPartOption(testMainPartOption);
    await c.startProduction();
    final positions = c.containerPositions;
    c.enterBatchSelection();
    await c.requestAltPart(testMainPartOption.relatedParts.first);

    expect(c.batchMode, isTrue);
    expect(c.batchCart, isEmpty);
    expect(c.mamulActionsReady, isTrue);
    expect(c.selectedEffectivePart?.partId, testMainPartId);
    expect(c.containerPositions, positions);
    expect(repo.lastContainerPositionPartId, testMainPartId);
  });

  test(
    'normal client requires production; matching session accompanies transport without physical slots',
    () async {
      final repo = FakePdaRepository();
      final c = await controller(repo);
      await c.selectMainPartOption(testMainPartOption);
      expect(c.partSelectionReady, isTrue);
      expect(c.transportActionsReady, isFalse);
      await c.submitContainerAction(c.containerPositions.first, 'EMPTY');
      expect(repo.createCount, 0);
      await c.startProduction();
      expect(c.transportActionsReady, isTrue);
      await c.submitContainerAction(c.containerPositions.first, 'EMPTY');
      expect(repo.lastProductionSessionId, c.production.active!.id);
      expect(repo.lastCommand!.sourceSlotId, isNull);
      expect(repo.lastCommand!.destinationSlotId, isNull);
      expect(repo.createCount, 1);
    },
  );

  test(
    're-login restores active main and variant, not all variant related parts',
    () async {
      final repo = FakePdaRepository()..productionValue = productionRecord();
      final c = await controller(repo);
      expect(c.selectedMainPart!.partId, testMainPartId);
      expect(c.resolvedCompatibleVariant!.variantId, testVariantV1Id);
      expect(
        c.selectedMainPart!.relatedParts.map((p) => p.partId),
        isNot(contains(testOrphanPartId)),
      );
      expect(repo.productionStartCount, 0);
      await c.logout();
      await c.login('00123');
      await c.selectMachine(testMachine);
      expect(c.production.active!.id, 'production-1');
      expect(c.transportActionsReady, isTrue);
      expect(repo.productionStartCount, 0);
    },
  );

  test(
    'active main cannot change, related requests remain in active product group',
    () async {
      final repo = FakePdaRepository()..productionValue = productionRecord();
      final c = await controller(repo);
      c.resetPartSelection();
      await c.selectMainPartOption(testSecondMainPartOption);
      expect(c.selectedMainPart!.partId, testMainPartId);
      await c.selectEffectivePart(
        const EffectivePartSelection(
          partId: testRelatedPartId,
          partCode: 'RELATED',
          isMain: false,
        ),
      );
      expect(c.selectedEffectivePart!.partId, testRelatedPartId);
      expect(c.resolvedCompatibleVariant!.productGroupId, testProductGroupId);
      expect(c.transportActionsReady, isTrue);
      await c.selectEffectivePart(
        const EffectivePartSelection(
          partId: testOrphanPartId,
          partCode: 'OTHER',
          isMain: false,
        ),
      );
      expect(c.selectedEffectivePart!.partId, testRelatedPartId);
      await c.selectEffectivePart(
        const EffectivePartSelection(
          partId: testAltPartId,
          partCode: 'ALT',
          isMain: false,
        ),
      );
      // Fake ALT is only compatible with V2. Never switch active V1 production.
      expect(c.transportActionsReady, isFalse);
      expect(repo.switchCount, 0);
      expect(c.production.active!.variantId, testVariantV1Id);
    },
  );

  test(
    'active transport blocks end with no cancellation side effect',
    () async {
      final repo = FakePdaRepository()
        ..productionValue = productionRecord()
        ..contextValue = testContext(activeRequests: [testActiveRequest]);
      final c = await controller(repo);
      expect(c.canEndProduction, isFalse);
      await c.endProduction();
      expect(repo.productionEndCount, 0);
      expect(repo.contextValue.activeTransportRequests, hasLength(1));
      repo.contextValue = testContext();
      await c.refreshContext();
      expect(c.canEndProduction, isTrue);
      await c.endProduction();
      expect(c.production.active, isNull);
      expect(c.selectedMainPart, isNull);
      expect(c.transportActionsReady, isFalse);
      expect(repo.productionEndIds, ['production-1']);
      expect(repo.createCount, 0);
    },
  );

  test(
    'refresh read failure disables actions, later read recovers without POST',
    () async {
      final repo = FakePdaRepository()..productionValue = productionRecord();
      final c = await controller(repo);
      repo.productionReadError = const ConnectivityException();
      await c.refreshContext();
      expect(c.production.verified, isFalse);
      expect(c.transportActionsReady, isFalse);
      expect(c.canEndProduction, isFalse);
      repo.productionReadError = null;
      await c.refreshContext();
      expect(c.transportActionsReady, isTrue);
      expect(repo.productionStartCount + repo.productionEndCount, 0);
    },
  );

  test(
    'late machine read does not restore production after leaving workspace',
    () async {
      final repo = FakePdaRepository()..loginUser = testSupervisor;
      final c = await controller(repo);
      repo.productionReadCompleter = Completer<ProductionSession?>();
      final refresh = c.refreshContext();
      await Future<void>.delayed(Duration.zero);
      c.leaveMachine();
      repo.productionReadCompleter!.complete(productionRecord());
      await refresh;
      expect(c.selectedMachine, isNull);
      expect(c.production.active, isNull);
      expect(c.selectedMainPart, isNull);
    },
  );

  test(
    'ambiguous transport retry preserves its production identity after server end',
    () async {
      final repo = FakePdaRepository()..productionValue = productionRecord();
      final c = await controller(repo);
      repo.createError = const AmbiguousMutationException(
        idempotencyKey: 'uncertain',
      );
      await c.submitContainerAction(c.containerPositions.first, 'EMPTY');
      final key = repo.lastIdempotencyKey;
      repo.productionValue = null;
      await c.refreshContext();
      expect(c.hasAmbiguousSubmission, isTrue);
      repo.createError = null;
      await c.retryAmbiguousTransport();
      expect(repo.lastProductionSessionId, 'production-1');
      expect(repo.lastIdempotencyKey, key);
    },
  );

  test(
    'start double tap sends once and ambiguous retry keeps exact key',
    () async {
      final repo = FakePdaRepository()
        ..productionStartCompleter = Completer<ProductionSession>();
      final c = await controller(repo);
      await c.selectMainPartOption(testMainPartOption);
      final start = c.startProduction();
      await c.startProduction();
      expect(repo.productionStartCount, 1);
      repo.productionStartCompleter!.completeError(
        const ConnectivityException(),
      );
      await start;
      expect(c.production.hasPending, isTrue);
      expect(c.canStartProduction, isFalse);
      expect(c.canChangePartSelection, isFalse);
      repo.productionStartCompleter = null;
      await c.retryProduction();
      expect(repo.productionStartKeys.toSet(), hasLength(1));
      expect(repo.productionStartCount, 2);
      expect(c.production.hasPending, isFalse);
      expect(c.transportActionsReady, isTrue);
    },
  );

  test(
    'resume revalidates ended production and never restarts automatically',
    () async {
      final repo = FakePdaRepository()..productionValue = productionRecord();
      final c = await controller(repo);
      c.onPaused();
      repo.productionValue = null;
      await c.onResumed();
      expect(c.production.active, isNull);
      expect(c.selectedMainPart, isNull);
      expect(c.transportActionsReady, isFalse);
      expect(repo.productionStartCount, 0);
    },
  );

  test(
    'uncertain end retries original session, never ends a newer production',
    () async {
      final repo = FakePdaRepository()..productionValue = productionRecord();
      final state = ProductionCoordinator(repo)..reset(testMachine.id);
      addTearDown(state.dispose);
      await state.refresh();
      repo.productionEndError = const ConnectivityException();
      await state.end();
      repo.productionValue = productionRecord(id: 'new-session');
      await state.refresh();
      expect(state.hasPending, isTrue);
      repo.productionEndError = null;
      repo.productionEndCompleter = Completer<ProductionSession>()
        ..complete(productionRecord(ended: true));
      await state.retry();
      await state.refresh();
      expect(repo.productionEndIds, ['production-1', 'production-1']);
      expect(state.active!.id, 'new-session');
    },
  );

  test(
    'malformed production response fails closed and preserves retry',
    () async {
      final repo = FakePdaRepository()
        ..productionStartCompleter = (Completer<ProductionSession>()
          ..complete(
            ProductionSession(
              id: 'wrong',
              machineId: 'other-machine',
              variantId: testVariantV1Id,
              productGroupId: testProductGroupId,
              mainPartId: testMainPartId,
              startedAt: DateTime.utc(2026),
            ),
          ));
      final c = await controller(repo);
      await c.selectMainPartOption(testMainPartOption);
      await c.startProduction();
      expect(c.production.hasPending, isTrue);
      expect(c.transportActionsReady, isFalse);
    },
  );

  test(
    'production repository uses scoped authenticated API, exact key and no operator quantity/identity fields',
    () async {
      final requests = <http.Request>[];
      final repo = PdaRepository(
        ApiClient(
          baseUri: Uri.parse('http://localhost:5080'),
          deviceCode: () => testDeviceCode,
          accessToken: () => 'test-only',
          onUnauthorized: () {},
          httpClient: MockClient((request) async {
            requests.add(request);
            if (request.method == 'GET') return http.Response('', 204);
            return http.Response(jsonEncode({'session': sessionJson}), 201);
          }),
        ),
      );
      expect(await repo.activeProduction(testMachine.id), isNull);
      final command = ProductionStartCommand(
        machineId: testMachine.id,
        variantId: testVariantV1Id,
        productGroupId: testProductGroupId,
        mainPartId: testMainPartId,
      );
      final result = await repo.startProduction(command, 'same-start-key');
      expect(result.id, 'production-1');
      expect(jsonDecode(requests.last.body), command.toJson());
      expect(requests.last.headers['Idempotency-Key'], 'same-start-key');
      expect(requests.last.headers['X-Device-Code'], testDeviceCode);
      expect(
        requests.last.url.path,
        '/api/v1/machines/${testMachine.id}/production-sessions',
      );
    },
  );

  for (final change in <Map<String, Object?>>[
    {'status': 'UNKNOWN'},
    {'endedAt': 'not-a-time'},
    {'mainPartId': ' '},
    {'startedAt': null},
    {'status': 'ACTIVE', 'endedAt': '2026-09-15T09:00:00Z'},
  ]) {
    test('invalid production JSON is not accepted: $change', () {
      expect(
        () => ProductionSession.fromJson({...sessionJson, ...change}),
        throwsFormatException,
      );
    });
  }

  test('ending status stays an open server-owned session', () {
    final session = ProductionSession.fromJson({
      ...sessionJson,
      'status': 'ENDING',
    });
    expect(session.isEnding, isTrue);
    expect(session.isActive, isTrue);
    expect(session.endedAt, isNull);
  });

  test('production-end cleanup does not block starting the next job', () async {
    final ended = productionRecord(ended: true);
    final repo = FakePdaRepository()
      ..productionValue = productionRecord()
      ..productionEndSession = ended
      ..contextValue = testContext(activeRequests: [testProductionEndRequest]);
    final c = await controller(repo);
    expect(c.canEndProduction, isTrue);
    await c.endProduction();
    expect(c.production.active, isNull);
    expect(c.mamulActionsReady, isFalse);
    expect(repo.productionEndKeys, hasLength(1));
    expect(repo.productionEndKeys.single, startsWith('production-end:'));
    expect(
      repo.contextValue.activeTransportRequests.single.productionEnd,
      isTrue,
    );

    await c.selectMainPartOption(testMainPartOption);
    expect(c.canStartProduction, isTrue);
    await c.startProduction();
    expect(repo.productionStartCount, 1);
    expect(c.production.active, isNotNull);
  });
}

final sessionJson = <String, dynamic>{
  'id': 'production-1',
  'machineId': testMachine.id,
  'variantId': testVariantV1Id,
  'productGroupId': testProductGroupId,
  'mainPartId': testMainPartId,
  'startedAt': '2026-09-15T08:00:00Z',
  'endedAt': null,
  'status': 'ACTIVE',
};

Future<AppController> controller(FakePdaRepository repository) async {
  final result = AppController(
    repository: repository,
    deviceCodeStore: MemoryDeviceCodeStore(testDeviceCode),
    session: SessionMemory(),
    enablePolling: false,
  );
  addTearDown(result.dispose);
  await result.initialize();
  await result.login('00123');
  await result.selectMachine(testMachine);
  return result;
}
