import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../app/design_tokens.dart';
import '../../data/models/production_session.dart';
import 'production_coordinator.dart';

/// Production lifecycle is independent of transport execution. These controls
/// never cancel a request or instruct a robot.
class ProductionSessionPanel extends StatelessWidget {
  const ProductionSessionPanel({required this.controller, super.key});
  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final state = controller.production;
    final active = state.active;
    final openCount = controller.context?.activeTransportRequests.length ?? 0;
    final error = controller.productionContextError ?? state.error;
    if (active != null) {
      return _CompactActiveHeader(
        controller: controller,
        active: active,
        error: error,
        openCount: openCount,
        state: state,
      );
    }
    return Card(
      key: const Key('production-panel'),
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(
          color: active != null
              ? const Color(0xFF16835D).withValues(alpha: 0.35)
              : const Color(0xFFCBD5E1),
          width: active != null ? 1.5 : 1,
        ),
      ),
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                    color: active != null
                        ? const Color(0xFF16835D).withValues(alpha: 0.12)
                        : const Color(0xFF0B4F6C).withValues(alpha: 0.08),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: Icon(
                    active != null
                        ? Icons.play_circle_filled_rounded
                        : Icons.factory_outlined,
                    color: active != null
                        ? const Color(0xFF16835D)
                        : const Color(0xFF0B4F6C),
                    size: 24,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        active == null ? 'Üretim' : 'Üretim Devam Ediyor',
                        style: const TextStyle(
                          fontSize: 18,
                          fontWeight: FontWeight.w900,
                          letterSpacing: -0.3,
                        ),
                      ),
                      Text(
                        active == null
                            ? 'Makine için aktif üretim kaydı yok'
                            : 'Aktif üretim oturumu açık',
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w500,
                          color: Color(0xFF64748B),
                        ),
                      ),
                    ],
                  ),
                ),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 4,
                  ),
                  decoration: BoxDecoration(
                    color: active != null
                        ? const Color(0xFF16835D).withValues(alpha: 0.12)
                        : const Color(0xFFF1F5F9),
                    borderRadius: BorderRadius.circular(20),
                    border: Border.all(
                      color: active != null
                          ? const Color(0xFF16835D).withValues(alpha: 0.4)
                          : const Color(0xFFCBD5E1),
                    ),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 8,
                        height: 8,
                        decoration: BoxDecoration(
                          color: active != null
                              ? const Color(0xFF16835D)
                              : const Color(0xFF94A3B8),
                          shape: BoxShape.circle,
                        ),
                      ),
                      const SizedBox(width: 6),
                      Text(
                        active != null ? 'Aktif' : 'Bekliyor',
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w700,
                          color: active != null
                              ? const Color(0xFF16835D)
                              : const Color(0xFF64748B),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            if (state.loading || state.inFlight) ...[
              const SizedBox(height: 12),
              const ClipRRect(
                borderRadius: BorderRadius.all(Radius.circular(4)),
                child: LinearProgressIndicator(minHeight: 4),
              ),
              const SizedBox(height: 4),
              Text(
                state.inFlight
                    ? 'Üretim işlemi doğrulanıyor…'
                    : 'Üretim durumu kontrol ediliyor…',
                style: const TextStyle(fontSize: 12, color: Color(0xFF64748B)),
              ),
            ],
            const SizedBox(height: 14),
            if (active != null) ...[
              Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(
                  color: const Color(0xFFF8FAFC),
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(color: const Color(0xFFE2E8F0)),
                ),
                child: Column(
                  children: [
                    Row(
                      children: [
                        const Icon(
                          Icons.inventory_2_outlined,
                          size: 18,
                          color: Color(0xFF0B4F6C),
                        ),
                        const SizedBox(width: 8),
                        const Text(
                          'Ana ürün: ',
                          style: TextStyle(
                            fontWeight: FontWeight.w600,
                            fontSize: 14,
                            color: Color(0xFF475569),
                          ),
                        ),
                        Expanded(
                          child: Text(
                            controller.selectedMainPart?.partCode ??
                                'Doğrulanıyor…',
                            style: const TextStyle(
                              fontWeight: FontWeight.w800,
                              fontSize: 15,
                            ),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        const Icon(
                          Icons.alt_route_rounded,
                          size: 18,
                          color: Color(0xFF0B4F6C),
                        ),
                        const SizedBox(width: 8),
                        const Text(
                          'Varyant: ',
                          style: TextStyle(
                            fontWeight: FontWeight.w600,
                            fontSize: 14,
                            color: Color(0xFF475569),
                          ),
                        ),
                        Expanded(
                          child: Text(
                            controller.context?.activeVariantCode ??
                                'Doğrulanıyor…',
                            style: const TextStyle(
                              fontWeight: FontWeight.w800,
                              fontSize: 15,
                            ),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        const Icon(
                          Icons.schedule_rounded,
                          size: 18,
                          color: Color(0xFF0B4F6C),
                        ),
                        const SizedBox(width: 8),
                        const Text(
                          'Başlangıç: ',
                          style: TextStyle(
                            fontWeight: FontWeight.w600,
                            fontSize: 14,
                            color: Color(0xFF475569),
                          ),
                        ),
                        Expanded(
                          child: Text(
                            _time(active.startedAt.toLocal()),
                            style: const TextStyle(
                              fontWeight: FontWeight.w700,
                              fontSize: 14,
                            ),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 8),
              const Text(
                'Ana ürün ve varyant üretim bitene kadar sabittir.',
                style: TextStyle(
                  fontSize: 12,
                  color: Color(0xFF64748B),
                  fontWeight: FontWeight.w500,
                ),
              ),
            ] else
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: const Color(0xFFF8FAFC),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: const Color(0xFFE2E8F0)),
                ),
                child: const Row(
                  children: [
                    Icon(
                      Icons.info_outline,
                      size: 20,
                      color: Color(0xFF0B4F6C),
                    ),
                    SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        'Ana ürünü ve uyumlu varyantı seçip üretimi başlatın. Taşıma taleplerini ardından oluşturabilirsiniz.',
                        style: TextStyle(
                          fontSize: 13,
                          color: Color(0xFF334155),
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            if (error != null) ...[
              const SizedBox(height: 10),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: const Color(0xFFFEF2F2),
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(color: const Color(0xFFFCA5A5)),
                ),
                child: Text(
                  error,
                  key: const Key('production-error'),
                  style: const TextStyle(
                    color: Color(0xFFB91C1C),
                    fontWeight: FontWeight.w600,
                    fontSize: 13,
                  ),
                ),
              ),
            ],
            if (state.hasPending) ...[
              const SizedBox(height: 10),
              Text(
                '${state.pendingLabel} sonucu kesinleşmedi. Yeniden deneme aynı işlemi doğrular; yeni üretim oluşturmaz.',
                style: const TextStyle(
                  fontSize: 13,
                  color: Color(0xFFC26D16),
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(height: 8),
              OutlinedButton.icon(
                key: const Key('production-retry'),
                onPressed: controller.isMutationInFlight
                    ? null
                    : controller.retryProduction,
                icon: const Icon(Icons.refresh),
                label: const Text('Aynı İşlemi Yeniden Dene'),
              ),
            ] else if (active == null) ...[
              const SizedBox(height: 14),
              FilledButton.icon(
                key: const Key('production-start'),
                style: FilledButton.styleFrom(
                  minimumSize: const Size(0, 52),
                  backgroundColor: const Color(0xFF16835D),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(12),
                  ),
                  textStyle: const TextStyle(
                    fontSize: 17,
                    fontWeight: FontWeight.w800,
                  ),
                ),
                onPressed: controller.canStartProduction
                    ? controller.startProduction
                    : null,
                icon: const Icon(Icons.play_arrow_rounded, size: 24),
                label: const Text('Üretime Başla'),
              ),
              if (openCount > 0) ...[
                const SizedBox(height: 6),
                Text(
                  controller.canStartProduction
                      ? 'Önceki üretimin açık taşıma görevleri görev yöneticisinde devam eder. Yeni üretimi başlatabilirsiniz.'
                      : 'Yeni üretim için açık taşıma taleplerinin tamamlanmasını bekleyin veya talepleri ayrıca iptal edin.',
                  key: const Key('production-open-transports'),
                  style: TextStyle(
                    fontSize: 12,
                    color: controller.canStartProduction
                        ? const Color(0xFF0369A1)
                        : const Color(0xFF9A3412),
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
            ] else ...[
              const SizedBox(height: 14),
              OutlinedButton.icon(
                key: const Key('production-end'),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size(0, 52),
                  foregroundColor: const Color(0xFFB91C1C),
                  side: const BorderSide(color: Color(0xFFEF4444)),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(12),
                  ),
                  textStyle: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w800,
                  ),
                ),
                onPressed: controller.canEndProduction
                    ? () => _confirmEnd(context)
                    : null,
                icon: const Icon(Icons.stop_circle_outlined, size: 22),
                label: const Text('Üretimi Bitir'),
              ),
              if (active.isEnding && controller.canEndProduction)
                const Padding(
                  padding: EdgeInsets.only(top: 6),
                  child: Text(
                    'Bitiş henüz tamamlanmadı. Saha durumunu doğruladıktan sonra yeniden deneyin.',
                    key: Key('production-end-retry-guidance'),
                  ),
                ),
              if (openCount > 0) ...[
                const SizedBox(height: 6),
                Text(
                  '$openCount açık taşıma görev yöneticisinde devam ediyor. Üretimi bitirdiğinizde bitiş görevleri de sıraya alınır.',
                  key: const Key('production-open-transports'),
                  style: const TextStyle(
                    fontSize: 12,
                    color: Color(0xFF9A3412),
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
              if (controller.batchCart.isNotEmpty) ...[
                const SizedBox(height: 4),
                const Text(
                  'Önce sepetteki taşıma seçimlerini gönderin veya sepeti temizleyin.',
                  style: TextStyle(fontSize: 12, color: Color(0xFF9A3412)),
                ),
              ],
              if (controller.hasAmbiguousSubmission ||
                  controller.hasAmbiguousBatchSubmission) ...[
                const SizedBox(height: 4),
                const Text(
                  'Önce sonucu belirsiz taşıma talebini doğrulayın.',
                  style: TextStyle(fontSize: 12, color: Color(0xFF9A3412)),
                ),
              ],
            ],
            if (!state.verified &&
                !state.loading &&
                !state.inFlight &&
                !state.hasPending) ...[
              const SizedBox(height: 6),
              TextButton.icon(
                key: const Key('production-refresh'),
                onPressed: () => controller.refreshContext(),
                icon: const Icon(Icons.sync, size: 18),
                label: const Text('Üretim Durumunu Yenile'),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Future<void> _confirmEnd(BuildContext context) async {
    final sessionId = controller.production.active?.id;
    final accepted = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Üretimi bitir?'),
        content: const Text(
          'Üretimi bitirdiğinizde makinedeki mevcut paletler sırasıyla kaldırılacaktır. Tüm kaldırma görevleri tamamlandığında üretim kapanacaktır.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            key: const Key('confirm-production-end'),
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Üretimi Bitir'),
          ),
        ],
      ),
    );
    // Another tablet may have changed production while this dialog was open.
    if (accepted == true &&
        context.mounted &&
        sessionId == controller.production.active?.id) {
      await controller.endProduction();
    }
  }

  static String _time(DateTime value) {
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(value.day)}.${two(value.month)}.${value.year} ${two(value.hour)}:${two(value.minute)}';
  }

  static String _clock(DateTime value) {
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(value.hour)}:${two(value.minute)}';
  }
}

class _CompactActiveHeader extends StatelessWidget {
  const _CompactActiveHeader({
    required this.controller,
    required this.active,
    required this.error,
    required this.openCount,
    required this.state,
  });

  final AppController controller;
  final ProductionSession active;
  final String? error;
  final int openCount;
  final ProductionCoordinator state;

  @override
  Widget build(BuildContext context) {
    final part = controller.selectedMainPart;
    final code = part?.partCode ?? 'Doğrulanıyor';
    final name = part?.description?.trim();
    final variant = controller.context?.activeVariantCode ?? 'Doğrulanıyor';
    return Card(
      key: const Key('production-panel'),
      margin: EdgeInsets.zero,
      elevation: 0,
      color: const Color(0xFFF0FDF4),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: const BorderSide(color: Color(0xFF86EFAC)),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                const Icon(
                  Icons.play_circle_filled_rounded,
                  color: Color(0xFF16835D),
                  size: 22,
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        active.isEnding ? 'ÜRETİM BİTİRİLİYOR' : 'ÜRETİM AKTİF',
                        style: TextStyle(
                          fontWeight: FontWeight.w900,
                          fontSize: PdaScale.caption(context),
                          letterSpacing: 0.4,
                        ),
                      ),
                      Text(
                        name == null || name.isEmpty ? code : '$code · $name',
                        key: const Key('active-production-product'),
                        style: TextStyle(
                          fontWeight: FontWeight.w800,
                          fontSize: PdaScale.body(context),
                        ),
                      ),
                      Text(
                        'Varyant $variant · Başlangıç ${ProductionSessionPanel._clock(active.startedAt.toLocal())}',
                        style: TextStyle(
                          fontSize: PdaScale.caption(context),
                          color: const Color(0xFF334155),
                        ),
                      ),
                    ],
                  ),
                ),
                OutlinedButton(
                  key: const Key('production-end'),
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size(48, 48),
                    foregroundColor: const Color(0xFFB91C1C),
                    side: const BorderSide(color: Color(0xFFEF4444)),
                  ),
                  onPressed: controller.canEndProduction
                      ? () => _confirmEnd(context)
                      : null,
                  child: const Text('Üretimi Bitir'),
                ),
              ],
            ),
            if (active.isEnding && controller.canEndProduction)
              const Padding(
                padding: EdgeInsets.only(top: 6),
                child: Text(
                  'Bitiş henüz tamamlanmadı. Saha durumunu doğruladıktan sonra yeniden deneyin.',
                  key: Key('production-end-retry-guidance'),
                ),
              ),
            if (state.loading || state.inFlight)
              const Padding(
                padding: EdgeInsets.only(top: 6),
                child: LinearProgressIndicator(minHeight: 3),
              ),
            if (error != null)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(
                  error!,
                  key: const Key('production-error'),
                  style: const TextStyle(
                    color: Color(0xFFB91C1C),
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
            if (openCount > 0 && !active.isEnding)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  '$openCount açık taşıma görev yöneticisinde devam ediyor. Üretimi bitirdiğinizde bitiş görevleri de sıraya alınır.',
                  key: const Key('production-open-transports'),
                  style: TextStyle(
                    fontSize: PdaScale.caption(context),
                    color: const Color(0xFF9A3412),
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
            if (state.hasPending)
              Align(
                alignment: Alignment.centerLeft,
                child: TextButton(
                  key: const Key('production-retry'),
                  onPressed: controller.isMutationInFlight
                      ? null
                      : controller.retryProduction,
                  child: const Text('Aynı İşlemi Yeniden Dene'),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _confirmEnd(BuildContext context) async {
    final sessionId = active.id;
    final accepted = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Üretimi bitir?'),
        content: const Text(
          'Üretimi bitirdiğinizde makinedeki mevcut paletler sırasıyla kaldırılacaktır. Tüm kaldırma görevleri tamamlandığında üretim kapanacaktır.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            key: const Key('confirm-production-end'),
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Üretimi Bitir'),
          ),
        ],
      ),
    );
    if (accepted == true &&
        context.mounted &&
        sessionId == controller.production.active?.id) {
      await controller.endProduction();
    }
  }
}
