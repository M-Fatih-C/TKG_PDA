import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../app/app_controller.dart';
import '../../data/models/pda_models.dart';
import '../../shared/presentation.dart';
import '../../shared/widgets/feedback_banners.dart';

/// Customer UI — Warehouse Preparation Console: a warehouse/supervisor-only
/// view of WAITING_PREPARATION transport requests, kept separate from the
/// line-operator part-selection flow (its own stage/route; no shared state
/// with machine selection or part-first browsing).
class WarehousePreparationScreen extends StatelessWidget {
  const WarehousePreparationScreen({required this.controller, super.key});

  final AppController controller;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: controller,
    builder: (context, _) => Scaffold(
      appBar: AppBar(
        leading: IconButton(
          key: const Key('leave-warehouse-preparation'),
          tooltip: 'Geri / İşlem',
          onPressed: controller.busy
              ? null
              : controller.leaveWarehousePreparation,
          icon: const Icon(Icons.arrow_back),
        ),
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('Depo Hazırlık'),
            if (controller.user case final user?)
              Text(
                '${user.fullName.trim().isNotEmpty ? user.fullName : user.username} (Sicil: ${user.employeeNo?.trim().isNotEmpty == true ? user.employeeNo! : "—"})',
                key: const Key('operator-user-banner'),
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.normal,
                ),
                overflow: TextOverflow.ellipsis,
              ),
          ],
        ),
        actions: [
          IconButton(
            tooltip: 'Yenile',
            onPressed: controller.warehousePreparationLoading
                ? null
                : controller.loadWarehousePreparationRequests,
            icon: const Icon(Icons.refresh),
          ),
          IconButton(
            key: const Key('logout-button'),
            tooltip: 'Çıkış',
            onPressed: controller.isMutationInFlight ? null : controller.logout,
            icon: const Icon(Icons.logout),
          ),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            if (controller.offline) const OfflineBanner(),
            if (controller.problem case final problem?)
              ProblemBanner(problem: problem),
            if (controller.uiNotice case final notice?)
              if (notice.persistent) NoticeBanner(notice: notice),
            Expanded(child: _Body(controller: controller)),
          ],
        ),
      ),
    ),
  );
}

class _Body extends StatelessWidget {
  const _Body({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final requests = controller.warehousePreparationRequests;
    if (controller.warehousePreparationLoading && requests.isEmpty) {
      return const Center(
        key: Key('warehouse-preparation-loading'),
        child: CircularProgressIndicator(),
      );
    }
    if (controller.warehousePreparationError case final error?) {
      if (requests.isEmpty) {
        return Padding(
          padding: const EdgeInsets.all(16),
          child: Card(
            key: const Key('warehouse-preparation-error'),
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(error),
                  const SizedBox(height: 10),
                  OutlinedButton.icon(
                    key: const Key('warehouse-preparation-retry'),
                    onPressed: controller.loadWarehousePreparationRequests,
                    icon: const Icon(Icons.refresh),
                    label: const Text('Yeniden Dene'),
                  ),
                ],
              ),
            ),
          ),
        );
      }
    }
    if (requests.isEmpty) {
      return const Center(
        key: Key('warehouse-preparation-empty'),
        child: Padding(
          padding: EdgeInsets.all(24),
          child: Text(
            'Hazırlık bekleyen talep yok.',
            style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }
    return RefreshIndicator(
      onRefresh: controller.loadWarehousePreparationRequests,
      child: ListView.separated(
        key: const Key('warehouse-preparation-list'),
        padding: const EdgeInsets.all(14),
        itemCount: requests.length,
        separatorBuilder: (_, _) => const SizedBox(height: 10),
        itemBuilder: (context, index) =>
            _RequestCard(request: requests[index], controller: controller),
      ),
    );
  }
}

class _RequestCard extends StatelessWidget {
  const _RequestCard({required this.request, required this.controller});

  final WarehousePreparationRequest request;
  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final marking = controller.isMarkingReady(request.requestId);
    final knownOperation = const {
      'BRING_EMPTY',
      'BRING_FULL',
      'REMOVE_FULL',
      'REMOVE_EMPTY',
    }.contains(request.operation);
    return Card(
      key: Key('warehouse-item-${request.requestId}'),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '${request.machineCode} · ${knownOr(request.machineDisplayName)}',
              style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w900),
            ),
            const SizedBox(height: 4),
            Text(switch (request.operation) {
              'BRING_EMPTY' => 'Boş taşıma kabı hazırlanacak',
              'BRING_FULL' => 'Dolu malzeme / kasa hazırlanacak',
              'REMOVE_FULL' => 'Dolu taşıma kabı alınacak',
              'REMOVE_EMPTY' => 'Boş taşıma kabı alınacak',
              _ => 'Talep türü doğrulanamadı',
            }, style: const TextStyle(fontWeight: FontWeight.w700)),
            // Preserve identification evidence without guessing basket/pallet
            // categories from customer codes or exposing routing details.
            Text(
              request.containerTypeCode?.trim().isNotEmpty == true
                  ? request.containerTypeCode!
                  : 'Taşıma kabı bilgisi eksik',
              key: Key('warehouse-container-${request.requestId}'),
            ),
            const SizedBox(height: 8),
            _InfoRow('Parça', knownOr(request.partCode)),
            _InfoRow('Talep No', '#${request.requestNo}'),
            _InfoRow(
              'İstenme',
              request.createdAt == null
                  ? 'Talep zamanı alınamadı'
                  : DateFormat(
                      'dd.MM.yyyy HH:mm:ss',
                    ).format(request.createdAt!.toLocal()),
            ),
            _InfoRow('Bekleme', _formatWaitDuration(request.createdAt)),
            const SizedBox(height: 12),
            Align(
              alignment: Alignment.centerRight,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  ElevatedButton.icon(
                    key: Key('warehouse-ready-${request.requestId}'),
                    onPressed:
                        (marking ||
                            !knownOperation ||
                            !controller.mutationGate.canMutate)
                        ? null
                        : () => _confirmReady(context),
                    icon: marking
                        ? const SizedBox.square(
                            dimension: 18,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.check_circle_outline),
                    label: const Text('Hazırlandı'),
                  ),
                  if (!knownOperation)
                    const Text(
                      'Talep türü doğrulanmadan hazırlık onaylanamaz.',
                    ),
                  if (!controller.mutationGate.canMutate)
                    Padding(
                      padding: const EdgeInsets.only(top: 4),
                      child: Text(
                        controller.mutationGate.reason ==
                                MutationGateReason.offline
                            ? 'Bağlantı yok'
                            : (controller.mutationGate.reason ==
                                      MutationGateReason.mutationPending
                                  ? 'İşlem devam ediyor'
                                  : 'İşlem uygun değil'),
                        style: TextStyle(
                          fontSize: 12,
                          color: Theme.of(context).colorScheme.error,
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _confirmReady(BuildContext context) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Hazır olarak işaretle'),
        content: Text(
          '${request.machineCode} için #${request.requestNo} numaralı talebin '
          'hazırlığı tamamlandı mı?\n\n'
          'Onayınızdan sonra güvenlik koşulları sağlandığında taşıma otomatik başlayacak.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          ElevatedButton(
            key: const Key('confirm-ready'),
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Onayla'),
          ),
        ],
      ),
    );
    if (confirmed == true) {
      await controller.markPreparationReady(request.requestId);
    }
  }
}

class _InfoRow extends StatelessWidget {
  const _InfoRow(this.label, this.value);

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 2),
    child: Row(
      children: [
        SizedBox(
          width: 80,
          child: Text(
            label,
            style: const TextStyle(
              fontWeight: FontWeight.w700,
              color: Color(0xFF64748B),
            ),
          ),
        ),
        Expanded(
          child: Text(
            value,
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
      ],
    ),
  );
}

String _formatWaitDuration(DateTime? createdAt) {
  if (createdAt == null) return 'Bilinmiyor';
  final diff = DateTime.now().difference(createdAt.toLocal());
  if (diff.isNegative || diff.inMinutes < 1) {
    return 'Az önce';
  }
  if (diff.inHours < 1) {
    return '${diff.inMinutes} dk. önce';
  }
  if (diff.inDays < 1) {
    final remainingMins = diff.inMinutes % 60;
    return '${diff.inHours} sa. ${remainingMins > 0 ? '$remainingMins dk. ' : ''}önce';
  }
  return '${diff.inDays} gün önce';
}
