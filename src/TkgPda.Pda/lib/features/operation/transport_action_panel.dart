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
  Widget build(BuildContext context) {
    final content = Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'Aktif Taşımalar (${requests.length})',
          style: TextStyle(
            fontSize: PdaScale.secondaryAction(context),
            fontWeight: FontWeight.w900,
          ),
        ),
        const SizedBox(height: 6),
        if (requests.isEmpty)
          Text(
            'Aktif taşıma yok',
            key: const Key('active-transport-empty'),
            style: TextStyle(
              fontSize: PdaScale.caption(context),
              color: const Color(0xFF475569),
            ),
          )
        else
          for (var index = 0; index < requests.length; index++) ...[
            _TransportCard(request: requests[index], controller: controller),
            if (index != requests.length - 1) const SizedBox(height: 6),
          ],
      ],
    );
    return Card(
      key: const Key('active-transport-section'),
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
        child: LayoutBuilder(
          builder: (context, constraints) {
            final bounded = constraints.maxHeight.isFinite;
            if (!bounded && requests.length <= 3) return content;
            return SizedBox(
              height: bounded ? constraints.maxHeight : 280,
              child: SingleChildScrollView(
                key: const Key('active-transport-scroll'),
                child: content,
              ),
            );
          },
        ),
      ),
    );
  }
}

class _TransportCard extends StatefulWidget {
  const _TransportCard({required this.request, required this.controller});

  final ActiveTransportRequest request;
  final AppController controller;

  @override
  State<_TransportCard> createState() => _TransportCardState();
}

class _TransportCardState extends State<_TransportCard> {
  bool _details = false;

  ActiveTransportRequest get request => widget.request;

  @override
  Widget build(BuildContext context) {
    final execution = request.execution;
    final failure = execution.failureCode != null;
    final caption = TextStyle(
      fontSize: PdaScale.caption(context),
      color: const Color(0xFF334155),
    );
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
        padding: const EdgeInsets.fromLTRB(10, 8, 10, 6),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Wrap(
              crossAxisAlignment: WrapCrossAlignment.center,
              spacing: 6,
              runSpacing: 4,
              children: [
                Text(
                  '#${request.requestNo} · ${actionLabel(request.operation)}',
                  style: TextStyle(
                    fontSize: PdaScale.body(context),
                    fontWeight: FontWeight.w900,
                  ),
                ),
                Chip(
                  visualDensity: VisualDensity.compact,
                  label: Text(executionStateLabel(execution.state)),
                  materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
                ),
                if (request.canCancel)
                  OutlinedButton.icon(
                    key: Key('cancel-${request.requestId}'),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: Theme.of(context).colorScheme.error,
                      visualDensity: VisualDensity.compact,
                      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                      minimumSize: const Size(48, 36),
                    ),
                    onPressed: widget.controller.mutationEnabled
                        ? () => _confirmCancel(context)
                        : null,
                    icon: const Icon(Icons.cancel_outlined, size: 18),
                    label: const Text('Talebi İptal Et'),
                  ),
              ],
            ),
            const SizedBox(height: 4),
            ..._partLines(caption),
            ..._summaryLines(caption),
            const SizedBox(height: 4),
            _LifecycleBar(request: request),
            if (execution.failureCode != null)
              Text(
                failureLabel(execution.failureCode, execution.operatorMessage),
                key: const Key('operator-failure-message'),
                style: TextStyle(
                  fontWeight: FontWeight.w800,
                  fontSize: PdaScale.caption(context),
                ),
              ),
            if (!request.canCancel && request.cancelDisabledMessage != null)
              Text(request.cancelDisabledMessage!, style: caption),
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton(
                key: Key('transport-detail-${request.requestId}'),
                style: TextButton.styleFrom(
                  visualDensity: VisualDensity.compact,
                  tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  minimumSize: const Size(48, 32),
                ),
                onPressed: () => setState(() => _details = !_details),
                child: Text(_details ? 'Detayı gizle' : 'Detay'),
              ),
            ),
            if (_details) ..._detailLines(caption),
          ],
        ),
      ),
    );
  }

  /// Part identity exactly as the server resolved it from master data. No
  /// identifier is ever shown; a missing part falls back to a short note.
  List<Widget> _partLines(TextStyle caption) {
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
              materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
            ),
          ),
        ),
      if (code.isEmpty)
        Text(
          'Parça bilgisi yok',
          key: const Key('active-part-missing'),
          style: caption,
        )
      else
        Text(
          code,
          key: const Key('active-part-code'),
          style: TextStyle(
            fontSize: PdaScale.body(context),
            fontWeight: FontWeight.w800,
          ),
        ),
      if (description.isNotEmpty)
        Text(
          description,
          key: const Key('active-part-description'),
          style: caption,
        ),
    ];
  }

  /// The one-line operational summary. Queue, chain and robot elapsed stay
  /// behind Detay so the default card stays short.
  List<Widget> _summaryLines(TextStyle caption) {
    final readiness = requestReadinessLabel(request.readinessState);
    final age = request.ageSeconds;
    return [
      if (readiness != null)
        Text(
          'Hazırlık: $readiness',
          key: const Key('active-readiness'),
          style: caption,
        ),
      if (age != null)
        Text(
          'Talep süresi: ${formatServerDuration(age)}',
          key: const Key('active-age'),
          style: caption,
        ),
    ];
  }

  /// Secondary evidence. Every number is a server value. There is no
  /// progress percentage.
  List<Widget> _detailLines(TextStyle caption) {
    final queuePosition = request.queuePosition;
    final robotElapsed = request.executionElapsedSeconds;
    return [
      if (queuePosition != null)
        Tooltip(
          message: 'TKG aktif talep sırası',
          child: Text(
            'Talep Sırası: $queuePosition',
            key: const Key('active-queue-position'),
            style: caption,
          ),
        ),
      if (_chainLabel(request.chainState) case final label?)
        Text(label, key: const Key('active-chain-state'), style: caption),
      if (robotElapsed != null)
        Text(
          'Robot çalışma süresi: ${formatServerDuration(robotElapsed)}',
          key: const Key('active-robot-elapsed'),
          style: caption,
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
    if (confirmed == true) await widget.controller.cancelTransport(request);
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
