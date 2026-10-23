@TestOn('vm')
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:tkg_pda/core/auth/session_memory.dart';
import 'package:tkg_pda/core/errors/api_problem.dart';
import 'package:tkg_pda/core/network/api_client.dart';
import 'package:tkg_pda/data/models/pda_models.dart';
import 'package:tkg_pda/data/repositories/pda_repository.dart';

void main() {
  final environment = Platform.environment;
  final baseUrl = environment['TKG_REAL_API_BASE_URL'];
  if (baseUrl == null) {
    test(
      'real backend harness requires TKG_REAL_API_BASE_URL',
      () {},
      skip: true,
    );
    return;
  }

  final deviceCode = _required(environment, 'TKG_REAL_API_DEVICE_CODE');
  final username = _required(environment, 'TKG_REAL_API_USERNAME');
  final warehouseDevice = _required(
    environment,
    'TKG_REAL_API_WAREHOUSE_DEVICE_CODE',
  );
  final warehouseUsername = _required(
    environment,
    'TKG_REAL_API_WAREHOUSE_USERNAME',
  );
  final inactiveDevice = _required(
    environment,
    'TKG_REAL_API_INACTIVE_DEVICE_CODE',
  );
  final machineCode = _required(environment, 'TKG_REAL_API_MACHINE_CODE');

  late _ClientContext mainClient;
  late List<MachineSummary> machineList;
  late MachineSummary machine;
  late OperationalContext context;
  late LayoutSlot bringSlot;
  late TransportCommand bringCommand;
  late String idempotencyKey;
  TransportResult? created;

  setUpAll(() async {
    mainClient = _client(Uri.parse(baseUrl), deviceCode);
    final login = await mainClient.repository.login(username);
    mainClient.session.establish(login);
    machineList = await mainClient.repository.machines();
    machine = machineList.singleWhere((item) => item.code == machineCode);
    context = await mainClient.repository.operationalContext(machine.id);
    bringSlot = context.slots.singleWhere(
      (slot) => slot.availableActions.any(
        (action) => action.action == 'BRING_EMPTY' && action.enabled,
      ),
    );
    idempotencyKey = 'phase07b-${DateTime.now().microsecondsSinceEpoch}';
    bringCommand = TransportCommand(
      machineId: machine.id,
      variantId: context.activeVariantId!,
      layoutId: context.activeLayoutId!,
      navigationProfileId: context.activeNavigationProfileId,
      productGroupId: context.productGroups.firstOrNull?.id,
      partId: context.productGroups.firstOrNull?.parts.firstOrNull?.partId,
      containerTypeId:
          context.productGroups.firstOrNull?.parts.firstOrNull?.containerTypeId,
      operation: 'BRING_EMPTY',
      sourceSlotId: null,
      destinationSlotId: bringSlot.id,
      priority: 100,
    );
  });

  tearDownAll(() => mainClient.api.close());

  test('01 provisioned PDA login binds token to X-Device-Code', () async {
    expect(mainClient.session.isAuthenticated, isTrue);
    expect(mainClient.session.device?.code, deviceCode);
    expect(mainClient.session.device?.type, 'PDA');
  });

  test('02 auth me returns authenticated device-bound principal', () async {
    final me = await mainClient.repository.me();
    expect(me.username, username);
    expect(me.roles, contains('ADMIN'));
  });

  test('03 authorized machine list is parsed from the API', () {
    expect(machineList, isNotEmpty);
    expect(machineList.every((item) => item.permissions.canView), isTrue);
  });

  test('04 machine operational context is parsed', () {
    expect(context.machine.id, machine.id);
    expect(context.activeVariantId, isNotNull);
    expect(context.activeLayoutId, isNotNull);
    expect(context.slots, isNotEmpty);
  });

  test('05 canonical nine seeded machine masters are visible to ADMIN', () {
    expect(machineList.length, 9);
  });

  test('06 Product Group and Alt Part evidence parses correctly', () {
    expect(context.productGroups, isNotEmpty);
    expect(context.productGroups.first.mainPartCode, isNotEmpty);
    expect(context.productGroups.expand((group) => group.parts), isNotEmpty);
  });

  test('07 MACHINE region is recognized as nontransport', () {
    final regions = context.slots.where((slot) => slot.isMachineRegion);
    expect(regions, isNotEmpty);
    expect(regions.every((slot) => !slot.transportEligible), isTrue);
    expect(
      regions
          .expand((slot) => slot.availableActions)
          .any((action) => action.enabled),
      isFalse,
    );
  });

  test('08 availableActions are parsed from the server projection', () {
    final bring = bringSlot.availableActions.singleWhere(
      (action) => action.action == 'BRING_EMPTY',
    );
    expect(bring.enabled, isTrue);
    expect(bring.disabledCode, isNull);
  });

  test('09 create BRING_EMPTY succeeds with exact slot semantics', () async {
    created = await mainClient.repository.createTransport(
      bringCommand,
      idempotencyKey,
    );
    expect(created!.operation, 'BRING_EMPTY');
    expect(created!.status, 'QUEUED');
    expect(created!.idempotentReplay, isFalse);
  });

  test('10 same material command and key replays one request', () async {
    created ??= await mainClient.repository.createTransport(
      bringCommand,
      idempotencyKey,
    );
    final replay = await mainClient.repository.createTransport(
      bringCommand,
      idempotencyKey,
    );
    expect(replay.id, created!.id);
    expect(replay.idempotentReplay, isTrue);
  });

  test('11 changed material command with same key returns conflict', () async {
    created ??= await mainClient.repository.createTransport(
      bringCommand,
      idempotencyKey,
    );
    final changed = TransportCommand(
      machineId: bringCommand.machineId,
      variantId: bringCommand.variantId,
      layoutId: bringCommand.layoutId,
      navigationProfileId: bringCommand.navigationProfileId,
      productGroupId: bringCommand.productGroupId,
      partId: bringCommand.partId,
      containerTypeId: bringCommand.containerTypeId,
      operation: bringCommand.operation,
      sourceSlotId: bringCommand.sourceSlotId,
      destinationSlotId: bringCommand.destinationSlotId,
      priority: 101,
    );
    await expectLater(
      mainClient.repository.createTransport(changed, idempotencyKey),
      throwsA(
        isA<ApiProblem>().having(
          (problem) => problem.code,
          'code',
          'IDEMPOTENCY_KEY_REUSED',
        ),
      ),
    );
  });

  test('12 active request appears in operational context', () async {
    created ??= await mainClient.repository.createTransport(
      bringCommand,
      idempotencyKey,
    );
    context = await mainClient.repository.operationalContext(machine.id);
    expect(
      context.activeTransportRequests.any(
        (request) => request.requestId == created!.id,
      ),
      isTrue,
    );
  });

  test('13 active request projects SLOT_BUSY for its reserved slot', () async {
    context = await mainClient.repository.operationalContext(machine.id);
    final reserved = context.slots.singleWhere(
      (slot) => slot.id == bringSlot.id,
    );
    expect(
      reserved.availableActions.every(
        (action) => !action.enabled && action.disabledCode == 'SLOT_BUSY',
      ),
      isTrue,
    );
  });

  test('14 transport request detail endpoint parses exact request', () async {
    final detail = await mainClient.repository.transport(created!.id);
    expect(detail.id, created!.id);
    expect(detail.requestNo, created!.requestNo);
  });

  test('15 valid pre-execution cancellation succeeds', () async {
    final cancelled = await mainClient.repository.cancel(
      machineId: machine.id,
      requestId: created!.id,
    );
    expect(cancelled.status, 'CANCELLED');
  });

  test('16 warehouse role cannot create REMOVE_FULL', () async {
    final warehouse = _client(Uri.parse(baseUrl), warehouseDevice);
    addTearDown(warehouse.api.close);
    warehouse.session.establish(
      await warehouse.repository.login(warehouseUsername),
    );
    final warehouseContext = await warehouse.repository.operationalContext(
      machine.id,
    );
    final source = warehouseContext.slots.firstWhere(
      (slot) => slot.occupancyState == 'OCCUPIED' && slot.loadState == 'FULL',
    );
    final command = TransportCommand(
      machineId: machine.id,
      variantId: warehouseContext.activeVariantId!,
      layoutId: warehouseContext.activeLayoutId!,
      navigationProfileId: null,
      productGroupId: null,
      partId: null,
      containerTypeId: null,
      operation: 'REMOVE_FULL',
      sourceSlotId: source.id,
      destinationSlotId: null,
      priority: 100,
    );
    await expectLater(
      warehouse.repository.createTransport(command, 'warehouse-forbidden'),
      throwsA(
        isA<ApiProblem>().having(
          (problem) => problem.code,
          'code',
          'OPERATION_NOT_ALLOWED_FOR_ROLE',
        ),
      ),
    );
  });

  test('17 invalid token produces 401 and clears in-memory session', () async {
    final invalidSession = SessionMemory();
    invalidSession.establish(
      LoginSession(
        accessToken: 'invalid-signed-token',
        expiresAt: DateTime.now().toUtc().add(const Duration(minutes: 5)),
        user: mainClient.session.user!,
        device: mainClient.session.device!,
      ),
    );
    late ApiClient client;
    client = ApiClient(
      baseUri: Uri.parse(baseUrl),
      deviceCode: () => deviceCode,
      accessToken: () => invalidSession.accessToken,
      onUnauthorized: invalidSession.clear,
    );
    addTearDown(client.close);
    await expectLater(
      PdaRepository(client).me(),
      throwsA(isA<ApiProblem>().having((p) => p.status, 'status', 401)),
    );
    expect(invalidSession.isAuthenticated, isFalse);
  });

  test('18 inactive PDA login is rejected', () async {
    final inactive = _client(Uri.parse(baseUrl), inactiveDevice);
    addTearDown(inactive.api.close);
    await expectLater(
      inactive.repository.login(username),
      throwsA(
        isA<ApiProblem>().having(
          (problem) => problem.code,
          'code',
          'DEVICE_NOT_AUTHORIZED',
        ),
      ),
    );
  });

  test('19 NULL container and lot evidence remains NULL', () async {
    context = await mainClient.repository.operationalContext(machine.id);
    final unknown = context.productGroups
        .expand((group) => group.parts)
        .firstWhere(
          (part) => part.containerTypeId == null || part.lotQuantity == null,
        );
    expect(
      unknown.containerTypeId == null || unknown.lotQuantity == null,
      isTrue,
    );
  });

  test('20 contract needs no direct RDS network dependency', () async {
    context = await mainClient.repository.operationalContext(machine.id);
    expect(
      context.slots.any((slot) => !slot.executionReadiness.rdsWorkSiteMapped),
      isTrue,
    );
    expect(context.activeNavigationProfileId, isNull);
  });
}

_ClientContext _client(Uri baseUri, String deviceCode) {
  final session = SessionMemory();
  late ApiClient api;
  api = ApiClient(
    baseUri: baseUri,
    deviceCode: () => deviceCode,
    accessToken: () => session.accessToken,
    onUnauthorized: session.clear,
  );
  return _ClientContext(api, PdaRepository(api), session);
}

String _required(Map<String, String> environment, String key) {
  final value = environment[key];
  if (value == null || value.isEmpty) {
    throw StateError('$key is required for the real backend suite.');
  }
  return value;
}

class _ClientContext {
  const _ClientContext(this.api, this.repository, this.session);

  final ApiClient api;
  final PdaRepository repository;
  final SessionMemory session;
}
