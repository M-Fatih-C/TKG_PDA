import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../app/app_controller.dart';
import '../../app/design_tokens.dart';
import '../../data/models/pda_models.dart';
import '../../shared/widgets/feedback_banners.dart';
import '../../shared/widgets/pda_empty_state.dart';

class OperatorQueueScreen extends StatelessWidget {
  const OperatorQueueScreen({required this.controller, super.key});

  final AppController controller;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: controller,
    builder: (context, _) => Scaffold(
      appBar: AppBar(
        leading: IconButton(
          key: const Key('leave-operator-queue'),
          tooltip: 'Geri / İşlem',
          onPressed: controller.leaveTasks,
          icon: const Icon(Icons.arrow_back),
        ),
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                const Text('Görevler'),
                if (controller.lastSuccessfulQueueRefresh case final last?) ...[
                  const SizedBox(width: 8),
                  Text(
                    controller.offline || controller.operatorQueueError != null
                        ? '· Bağlantı kesildi (${DateFormat('HH:mm:ss').format(last)})'
                        : '· ${DateFormat('HH:mm:ss').format(last)}',
                    key: controller.offline ||
                            controller.operatorQueueError != null
                        ? const Key('queue-stale-warning')
                        : const Key('queue-last-update'),
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.normal,
                      color: controller.offline ||
                              controller.operatorQueueError != null
                          ? const Color(0xFFFCA5A5)
                          : Colors.white70,
                    ),
                  ),
                ],
              ],
            ),
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
            key: const Key('refresh-operator-queue'),
            tooltip: 'Yenile',
            onPressed: controller.operatorQueueLoading
                ? null
                : controller.loadOperatorQueue,
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
            Padding(
              padding: const EdgeInsets.all(12),
              child: SegmentedButton<bool>(
                segments: const [
                  ButtonSegment(value: false, label: Text('Aktif')),
                  ButtonSegment(value: true, label: Text('Tamamlananlar')),
                ],
                selected: {controller.operatorQueueTerminal},
                onSelectionChanged: controller.operatorQueueLoading
                    ? null
                    : (values) =>
                          controller.setOperatorQueueTerminal(values.single),
              ),
            ),
            _RobotStatusBar(controller: controller),
            Expanded(child: _QueueBody(controller: controller)),
          ],
        ),
      ),
    ),
  );
}

class _RobotStatusBar extends StatelessWidget {
  const _RobotStatusBar({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) {
    final robots = controller.robots;
    if (robots.isEmpty) return const SizedBox.shrink();

    return Column(
      key: const Key('robot-status-bar'),
      mainAxisSize: MainAxisSize.min,
      children: [for (final robot in robots) _RobotCard(robot: robot)],
    );
  }
}

class _RobotCard extends StatelessWidget {
  const _RobotCard({required this.robot});

  final RobotTelemetry robot;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final battery = robot.batteryLevel;
    // Unavailable battery telemetry shows as unknown, never as a 0% reading.
    final batteryLabel = battery == null ? '—' : '${(battery * 100).toInt()}%';
    final isLowBattery = battery != null && battery < 0.20;

    final (
      String connectivityLabel,
      Color statusColor,
      Color statusBg,
      Color statusFg,
    ) = switch (robot.online) {
      true => (
        'Çevrimiçi',
        Colors.green.shade700,
        theme.colorScheme.primaryContainer,
        theme.colorScheme.onPrimaryContainer,
      ),
      false => (
        'Çevrimdışı',
        theme.colorScheme.error,
        theme.colorScheme.errorContainer,
        theme.colorScheme.onErrorContainer,
      ),
      null => (
        'Durum alınamadı',
        Colors.grey.shade600,
        theme.colorScheme.surfaceContainerHighest,
        theme.colorScheme.onSurfaceVariant,
      ),
    };

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
      elevation: 0,
      color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.5),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: theme.colorScheme.outlineVariant),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Row(
          children: [
            Icon(Icons.precision_manufacturing, color: statusColor, size: 20),
            const SizedBox(width: 8),
            Text(
              robot.displayName ?? 'Robot',
              style: theme.textTheme.labelLarge?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(width: 8),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: statusBg,
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text(
                connectivityLabel,
                style: theme.textTheme.labelSmall?.copyWith(
                  color: statusFg,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
            const SizedBox(width: 6),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: isLowBattery
                    ? theme.colorScheme.errorContainer
                    : theme.colorScheme.primaryContainer,
                borderRadius: BorderRadius.circular(6),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(
                    isLowBattery ? Icons.battery_alert : Icons.battery_std,
                    size: 14,
                    color: isLowBattery ? theme.colorScheme.error : null,
                  ),
                  const SizedBox(width: 2),
                  Text(
                    batteryLabel,
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: isLowBattery ? theme.colorScheme.error : null,
                      fontWeight: isLowBattery ? FontWeight.bold : null,
                    ),
                  ),
                  if (battery != null) ...[
                    const SizedBox(width: 4),
                    ClipRRect(
                      borderRadius: BorderRadius.circular(2),
                      child: SizedBox(
                        width: 24,
                        height: 6,
                        child: LinearProgressIndicator(
                          value: battery.clamp(0.0, 1.0),
                          backgroundColor: isLowBattery
                              ? theme.colorScheme.error.withValues(alpha: 0.2)
                              : theme.colorScheme.surfaceContainerHighest,
                          valueColor: AlwaysStoppedAnimation<Color>(
                            isLowBattery
                                ? theme.colorScheme.error
                                : (battery < 0.50
                                      ? Colors.amber.shade700
                                      : Colors.green.shade700),
                          ),
                        ),
                      ),
                    ),
                  ],
                ],
              ),
            ),
            const Spacer(),
            if (robot.hasActiveAlarm)
              Tooltip(
                message: 'Robot arızası mevcut',
                child: Icon(
                  Icons.warning_amber_rounded,
                  size: 20,
                  color: theme.colorScheme.error,
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _QueueBody extends StatelessWidget {
  const _QueueBody({required this.controller});

  final AppController controller;

  @override
  Widget build(BuildContext context) {
    if (controller.operatorQueueLoading && controller.operatorQueue == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (controller.operatorQueueError case final error?) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(error, textAlign: TextAlign.center),
        ),
      );
    }
    final page = controller.operatorQueue;
    final items = page?.items ?? const <OperatorQueueItem>[];
    if (items.isEmpty) {
      final isTerminal = controller.operatorQueueTerminal;
      return PdaEmptyState(
        icon: isTerminal ? Icons.history_rounded : Icons.task_outlined,
        title: isTerminal
            ? 'Yakın zamanda tamamlanan görev yok.'
            : 'Aktif görev yok.',
        subtitle: isTerminal
            ? 'Tamamlanan veya iptal edilen taşıma görevleri burada listelenecektir.'
            : 'Makinelerden yeni bir taşıma talebi başlatıldığında görev listesi otomatik olarak güncellenecektir.',
        textKey: const Key('operator-queue-empty'),
      );
    }
    return Column(
      children: [
        Expanded(
          child: RefreshIndicator(
            onRefresh: controller.loadOperatorQueue,
            child: ListView.separated(
              key: const Key('operator-queue-list'),
              padding: const EdgeInsets.fromLTRB(12, 0, 12, 12),
              itemCount: items.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (context, index) =>
                  _QueueCard(controller: controller, item: items[index]),
            ),
          ),
        ),
        if (page != null && (page.offset > 0 || page.hasMore))
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 4, 12, 12),
            child: Row(
              children: [
                Expanded(
                  child: OutlinedButton(
                    key: const Key('operator-queue-previous'),
                    onPressed: page.offset > 0
                        ? () => controller.loadOperatorQueue(
                            offset: (page.offset - page.limit).clamp(
                              0,
                              page.offset,
                            ),
                          )
                        : null,
                    child: const Text('Önceki'),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: OutlinedButton(
                    key: const Key('operator-queue-next'),
                    onPressed: page.hasMore
                        ? () => controller.loadOperatorQueue(
                            offset: page.offset + page.limit,
                          )
                        : null,
                    child: const Text('Sonraki'),
                  ),
                ),
              ],
            ),
          ),
      ],
    );
  }
}

class _QueueCard extends StatelessWidget {
  const _QueueCard({required this.controller, required this.item});

  final AppController controller;
  final OperatorQueueItem item;

  @override
  Widget build(BuildContext context) {
    final stateColor = PdaStatusColors.of(item.state);
    final stateBg = PdaStatusColors.surfaceOf(item.state);

    return Card(
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: const BorderSide(color: Color(0xFFE2E8F0)),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        child: Row(
          children: [
            Container(
              width: 42,
              height: 42,
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: const Color(0xFF0B4F6C),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Text(
                '#${item.queuePosition}',
                style: const TextStyle(
                  color: Colors.white,
                  fontWeight: FontWeight.w800,
                  fontSize: 14,
                ),
              ),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Row(
                    children: [
                      Text(
                        item.machine,
                        style: const TextStyle(
                          fontWeight: FontWeight.w900,
                          fontSize: 16,
                          color: Color(0xFF0F172A),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          item.part,
                          style: const TextStyle(
                            fontWeight: FontWeight.w700,
                            fontSize: 14,
                            color: Color(0xFF334155),
                          ),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Text(
                    '${item.logicalContainerNumber == null ? 'Kasa' : '${item.logicalContainerNumber}. Kasa'} · ${item.businessAction}',
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: Color(0xFF475569),
                    ),
                  ),
                  const SizedBox(height: 2),
                  Row(
                    children: [
                      Text(
                        _requestTime(item.requestedAt),
                        style: const TextStyle(
                          fontSize: 12,
                          color: Color(0xFF64748B),
                        ),
                      ),
                      const Text(
                        ' · ',
                        style: TextStyle(color: Color(0xFF94A3B8)),
                      ),
                      Expanded(
                        child: Text(
                          _etaText(item),
                          style: TextStyle(
                            fontSize: 12,
                            color: item.etaState == 'AVAILABLE'
                                ? const Color(0xFF0B4F6C)
                                : const Color(0xFF64748B),
                            fontWeight: item.etaState == 'AVAILABLE'
                                ? FontWeight.w700
                                : FontWeight.normal,
                          ),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
            const SizedBox(width: 8),
            Chip(
              backgroundColor: stateBg,
              side: BorderSide(color: stateColor.withValues(alpha: 0.3)),
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              label: Text(
                _stateText(item.state),
                style: TextStyle(
                  color: stateColor,
                  fontWeight: FontWeight.w700,
                  fontSize: 12,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

String _requestTime(DateTime value) {
  final local = value.toLocal();
  String two(int part) => part.toString().padLeft(2, '0');
  return '${two(local.day)}.${two(local.month)}.${local.year} ${two(local.hour)}:${two(local.minute)}';
}

String _etaText(OperatorQueueItem item) {
  if (item.etaState != 'AVAILABLE' || item.etaSeconds == null) {
    return 'Süre bilgisi yok';
  }
  final minutes = (item.etaSeconds! / 60).ceil();
  return minutes < 1 ? '1 dakikadan az' : 'Yaklaşık $minutes dakika';
}

String _stateText(String state) => switch (state) {
  'WAITING_PREPARATION' => 'Hazırlık Bekleniyor',
  'REQUESTED' => 'İstendi',
  'QUEUED' => 'Sırada',
  'ROBOT_ASSIGNED' => 'Robot atandı',
  'IN_PROGRESS' => 'Devam ediyor',
  'COMPLETED' => 'Tamamlandı',
  'ERROR' => 'Hata',
  'CANCELLED' => 'İptal',
  _ => 'Güncelleniyor',
};
