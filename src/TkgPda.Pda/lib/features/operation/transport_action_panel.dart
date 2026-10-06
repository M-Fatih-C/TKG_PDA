import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../app/design_tokens.dart';
import '../../data/models/pda_models.dart';
import '../../shared/presentation.dart';

/// Existing active-request inspection and cancellation surface. WP-33 keeps
/// cancellation confirmation while the legacy creation confirmation is gone.
class OperatorActiveTransportList extends StatelessWidget {
  const OperatorActiveTransportList({
    required this.requests,
    required this.controller,
    super.key,
  });

  final List<ActiveTransportRequest> requests;
  final AppController controller;

  @override
  Widget build(BuildContext context) => Card(
    key: const Key('active-transport-section'),
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.local_shipping, size: 30),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  'Aktif Taşımalar (${requests.length})',
                  style: const TextStyle(
                    fontSize: 20,
                    fontWeight: FontWeight.w900,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          for (final request in requests) ...[
            _TransportCard(request: request, controller: controller),
            const SizedBox(height: 10),
          ],
        ],
      ),
    ),
  );
}

class _TransportCard extends StatelessWidget {
  const _TransportCard({required this.request, required this.controller});

  final ActiveTransportRequest request;
  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final execution = request.execution;
    final failure = execution.failureCode != null;
    return Material(
      key: Key('active-transport-${request.requestId}'),
      color: failure ? const Color(0xFFFEE2E2) : const Color(0xFFEFF6FF),
      shape: RoundedRectangleBorder(
        side: BorderSide(
          color: failure ? const Color(0xFFB91C1C) : const Color(0xFF3B82F6),
        ),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    '#${request.requestNo} · ${actionLabel(request.operation)}',
                    style: const TextStyle(
                      fontSize: 18,
                      fontWeight: FontWeight.w900,
                    ),
                  ),
                ),
                Chip(label: Text(executionStateLabel(execution.state))),
                if (request.canCancel) ...[
                  const SizedBox(width: 8),
                  OutlinedButton.icon(
                    key: Key('cancel-${request.requestId}'),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: Theme.of(context).colorScheme.error,
                      visualDensity: VisualDensity.compact,
                      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                    ),
                    onPressed: controller.mutationEnabled
                        ? () => _confirmCancel(context)
                        : null,
                    icon: const Icon(Icons.cancel_outlined, size: 18),
                    label: const Text('Talebi İptal Et'),
                  ),
                ],
              ],
            ),
            const SizedBox(height: 6),
            ..._partLines(),
            const SizedBox(height: 8),
            _LifecycleBar(request: request),
            ..._statusLines(),
            if (execution.failureCode != null)
              Text(
                failureLabel(execution.failureCode, execution.operatorMessage),
                key: const Key('operator-failure-message'),
                style: const TextStyle(fontWeight: FontWeight.w800),
              ),
            if (!request.canCancel && request.cancelDisabledMessage != null)
              Text(request.cancelDisabledMessage!),
          ],
        ),
      ),
    );
  }

  /// Part identity exactly as the server resolved it from master data. No
  /// identifier is ever shown; a missing part falls back to a short note.
  List<Widget> _partLines() {
    final code = request.partCode?.trim() ?? '';
    final description = request.partDescription?.trim() ?? '';
    return [
      if (isAltPartRelation(request.relationType))
        const Padding(
          padding: EdgeInsets.only(bottom: 4),
          child: Align(
            alignment: Alignment.centerLeft,
            child: Chip(
              key: Key('alt-part-badge'),
              avatar: Icon(Icons.swap_horiz, size: 18),
              label: Text(
                'Alt Parça',
                style: TextStyle(fontWeight: FontWeight.w900),
              ),
              visualDensity: VisualDensity.compact,
            ),
          ),
        ),
      if (code.isEmpty)
        const Text('Parça bilgisi yok', key: Key('active-part-missing'))
      else
        Text(
          'Parça: $code',
          key: const Key('active-part-code'),
          style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
        ),
      if (description.isNotEmpty)
        Text(
          'Açıklama: $description',
          key: const Key('active-part-description'),
        ),
    ];
  }

  /// Status rows. Every number is a server value: the TKG request order and
  /// the server-measured durations. There is deliberately no progress
  /// percentage, bar or distance: nothing here is a robot estimate.
  List<Widget> _statusLines() {
    final readiness = requestReadinessLabel(request.readinessState);
    final queuePosition = request.queuePosition;
    final age = request.ageSeconds;
    final robotElapsed = request.executionElapsedSeconds;
    return [
      const SizedBox(height: 4),
      if (queuePosition != null)
        Tooltip(
          message: 'TKG aktif talep sırası',
          child: Text(
            'Talep Sırası: $queuePosition',
            key: const Key('active-queue-position'),
          ),
        ),
      if (readiness != null)
        Text('Hazırlık: $readiness', key: const Key('active-readiness')),
      if (_chainLabel(request.chainState) case final label?)
        Text(label, key: const Key('active-chain-state')),
      if (age != null)
        Text(
          'Talep süresi: ${formatServerDuration(age)}',
          key: const Key('active-age'),
        ),
      if (robotElapsed != null)
        Text(
          'Robot çalışma süresi: ${formatServerDuration(robotElapsed)}',
          key: const Key('active-robot-elapsed'),
        ),
    ];
  }

  Future<void> _confirmCancel(BuildContext context) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Talebi iptal et'),
        content: Text(
          '#${request.requestNo} numaralı talep için iptal isteği gönderilsin mi?\n\nRobotun fiziksel olarak durduğu yalnız sunucu durumu ile doğrulanır.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          ElevatedButton(
            key: const Key('confirm-cancel'),
            style: ElevatedButton.styleFrom(
              backgroundColor: Theme.of(context).colorScheme.error,
              foregroundColor: Colors.white,
            ),
            onPressed: () => Navigator.pop(context, true),
            child: const Text('İptal İsteği Gönder'),
          ),
        ],
      ),
    );
    if (confirmed == true) await controller.cancelTransport(request);
  }
}

String? _chainLabel(String? chainState) => switch (chainState) {
  'WAITING_PREDECESSOR' => 'Önceki görev bekleniyor',
  'REMOVING_EXISTING' => 'Mevcut palet kaldırılıyor',
  'PREPARING_SUCCESSOR' => 'Yeni palet hazırlanıyor',
  'PREPARING' => 'Palet hazırlanıyor',
  _ => null,
};

/// Segmented lifecycle from persisted status and readiness. A stage lights
/// only when the backend has that evidence. There is no robot percentage.
class _LifecycleBar extends StatelessWidget {
  const _LifecycleBar({required this.request});

  final ActiveTransportRequest request;

  @override
  Widget build(BuildContext context) {
    final stages = <(String, bool)>[
      ('TALEP', true),
      (
        'HAZIRLIK',
        request.readinessState == 'WAITING_PREPARATION' ||
            request.readinessState == 'READY',
      ),
      ('ROBOT', request.status == 'SENT_TO_RDS' || request.status == 'RUNNING'),
      ('TAŞIMA', request.status == 'RUNNING'),
      ('TAMAM', request.status == 'COMPLETED'),
    ];
    return Row(
      key: const Key('transport-lifecycle-bar'),
      children: [
        for (final stage in stages)
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(right: 4),
              child: Column(
                children: [
                  Container(
                    height: 6,
                    color: stage.$2
                        ? const Color(0xFF0369A1)
                        : const Color(0xFFCBD5E1),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    stage.$1,
                    textAlign: TextAlign.center,
                    style: TextStyle(
                      fontSize: PdaScale.caption(context),
                      fontWeight: FontWeight.w800,
                      color: stage.$2
                          ? const Color(0xFF0F172A)
                          : const Color(0xFF94A3B8),
                    ),
                  ),
                ],
              ),
            ),
          ),
      ],
    );
  }
}
