import 'package:flutter/material.dart';

import '../../core/errors/api_problem.dart';
import '../../data/models/pda_models.dart';

class OfflineBanner extends StatelessWidget {
  const OfflineBanner({super.key});

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('offline-banner'),
    width: double.infinity,
    color: const Color(0xFF7C2D12),
    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
    child: const Row(
      children: [
        Icon(Icons.cloud_off, color: Colors.white),
        SizedBox(width: 10),
        Expanded(
          child: Text(
            'Bağlantı yok — bilgiler güncel olmayabilir. Yeni işlem yapılamaz.',
            style: TextStyle(color: Colors.white, fontWeight: FontWeight.w700),
          ),
        ),
      ],
    ),
  );
}

class ProblemBanner extends StatelessWidget {
  const ProblemBanner({required this.problem, super.key});

  final ApiProblem problem;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      key: const Key('problem-banner'),
      width: double.infinity,
      color: theme.colorScheme.errorContainer,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(
            Icons.error_outline_rounded,
            color: theme.colorScheme.onErrorContainer,
            size: 20,
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              problem.operatorMessage,
              style: TextStyle(
                fontWeight: FontWeight.w700,
                color: theme.colorScheme.onErrorContainer,
                height: 1.35,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Customer Monitoring — Robot Alarm Visibility. A coarse, non-technical
/// signal only: no robot identity or alarm code is ever shown here. `error`
/// (RDS-unavailable) and `summary` are rendered as distinct states — a
/// failed read must never look like "no active alarm".
class RobotAlarmBanner extends StatelessWidget {
  const RobotAlarmBanner({
    required this.summary,
    required this.error,
    required this.onRetry,
    super.key,
  });

  final RobotAlarmSummary? summary;
  final String? error;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    if (error case final message?) {
      return Container(
        key: const Key('robot-alarm-unavailable-banner'),
        width: double.infinity,
        color: const Color(0xFFE5E7EB),
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        child: Row(
          children: [
            const Icon(Icons.help_outline, color: Color(0xFF374151)),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                message,
                style: const TextStyle(
                  color: Color(0xFF374151),
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            TextButton(
              key: const Key('robot-alarm-retry'),
              onPressed: onRetry,
              child: const Text('Yeniden dene'),
            ),
          ],
        ),
      );
    }

    final active = summary;
    if (active == null || !active.hasActiveRobotAlarm) {
      return const SizedBox.shrink();
    }

    final severe =
        active.highestSeverity == 'fatal' || active.highestSeverity == 'error';
    final foreground = severe ? Colors.white : const Color(0xFF92400E);
    return Container(
      key: const Key('robot-alarm-banner'),
      width: double.infinity,
      color: severe ? const Color(0xFF7C2D12) : const Color(0xFFFEF3C7),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          Icon(Icons.warning_amber, color: foreground),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              severe
                  ? 'Robot işlemi için müdahale gerekiyor'
                  : 'Robot uyarısı mevcut',
              style: TextStyle(color: foreground, fontWeight: FontWeight.w700),
            ),
          ),
          TextButton(
            key: const Key('robot-alarm-refresh'),
            onPressed: onRetry,
            child: Text('Yenile', style: TextStyle(color: foreground)),
          ),
        ],
      ),
    );
  }
}

class NoticeBanner extends StatelessWidget {
  const NoticeBanner({required this.message, super.key});

  final String message;

  @override
  Widget build(BuildContext context) {
    final isSuccess = message.contains('oluşturuldu') ||
        message.contains('güncellendi') ||
        message.contains('başladı') ||
        message.contains('doğrulandı') ||
        message.contains('temizlendi') ||
        message.contains('iptal edildi');
    final bgColor =
        isSuccess ? const Color(0xFFDCFCE7) : const Color(0xFFDBEAFE);
    final fgColor =
        isSuccess ? const Color(0xFF14532D) : const Color(0xFF1E40AF);
    final icon = isSuccess
        ? Icons.check_circle_outline_rounded
        : Icons.info_outline_rounded;

    return Container(
      key: const Key('notice-banner'),
      width: double.infinity,
      color: bgColor,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        children: [
          Icon(icon, color: fgColor, size: 20),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              message,
              style: TextStyle(
                color: fgColor,
                fontWeight: FontWeight.w700,
                height: 1.35,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
